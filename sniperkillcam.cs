using System;
using System.Collections.Generic;
using UnityEngine;

namespace sniperkillcam
{
    // Duckov 모드 로더가 찾는 엔트리: sniperkillcam.ModBehaviour
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        protected override void OnAfterSetup()
        {
            try
            {
                GameObject root = new GameObject("SniperKillCamRoot");
                UnityEngine.Object.DontDestroyOnLoad(root);

                root.AddComponent<SniperKillCamManager>();

                Debug.Log("[SniperKillCam] OnAfterSetup - Manager created");
            }
            catch (Exception ex)
            {
                Debug.Log("[SniperKillCam] OnAfterSetup 예외: " + ex);
            }
        }
    }

    public class SniperKillCamManager : MonoBehaviour
    {
        // ─────────────── 내부용 적 트래킹 정보 ───────────────
        private class TrackedEnemy
        {
            public Transform transform;
            public Vector3 lastKnownPosition;
            public string lastName;
            public bool wasPresent;
            public bool stillExists;
        }

        // ─────────────── 기본 상태 ───────────────
        private Camera _mainCamera;
        private Transform _playerTransform;

        private readonly List<TrackedEnemy> _trackedEnemies = new List<TrackedEnemy>();

        private float _scanInterval = 0.25f;   // 몇 초마다 캐릭터 스캔할지
        private float _scanTimer;

        // 멀리 있는 적만 킬캠 (스나이퍼 느낌용)
        private const float SNIPER_MIN_DISTANCE = 25f;

        // 킬캠 연출 시간(실제 시간 기준, unscaledTime 기준)
        private const float KILLCAM_DURATION = 1.2f;

        // 슬로우 타임 값
        private const float SLOW_TIMESCALE = 0.1f;

        // "내가 마지막으로 쏜 시간" (unscaled time)
        private float _lastShotTimeUnscaled = -999f;

        // 내가 마지막으로 쏜 위치/방향 (카메라 기준)
        private Vector3 _lastShotOrigin = Vector3.zero;
        private Vector3 _lastShotForward = Vector3.forward;

        // 방향 필터 설정
        private const float MAX_SHOT_ANGLE = 15f;      // 샷 방향과 적 방향의 최대 각도 차 (deg)
        private const float MAX_SHOT_SIDE_DISTANCE = 4f; // 샷 경로에서의 최대 수직 거리

        // ─────────────── KillCam 상태 ───────────────
        private bool _isPlaying;
        private float _startTimeUnscaled;
        private Vector3 _startPos;
        private Vector3 _endPos;
        private Transform _lookTarget;
        private float _savedTimeScale = 1f;

        // ─────────────── Unity LifeCycle ───────────────

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Debug.Log("[SniperKillCam] Manager Awake");
        }

        private void Update()
        {
            // 메인 카메라 확보
            if (_mainCamera == null || !_mainCamera.gameObject.activeInHierarchy)
            {
                _mainCamera = Camera.main;
            }

            // 킬캠 켜져 있으면 슬로우 강제 유지
            if (_isPlaying)
            {
                ApplySlowMotion();
                return;
            }

            // 플레이어 발사 입력 감지 (좌클릭)
            if (Input.GetMouseButtonDown(0))
            {
                _lastShotTimeUnscaled = Time.unscaledTime;

                if (_mainCamera != null)
                {
                    _lastShotOrigin = _mainCamera.transform.position;
                    _lastShotForward = _mainCamera.transform.forward.normalized;
                }
                else
                {
                    _lastShotOrigin = Vector3.zero;
                    _lastShotForward = Vector3.forward;
                }

                // Debug.Log("[SniperKillCam] Shot detected at " + _lastShotTimeUnscaled.ToString("F3"));
            }

            _scanTimer -= Time.unscaledDeltaTime;
            if (_scanTimer <= 0f)
            {
                _scanTimer = _scanInterval;
                RefreshCharacters();
                CheckEnemyDisappear();
            }
        }

        private void LateUpdate()
        {
            if (!_isPlaying || _mainCamera == null)
                return;

            // 혹시라도 다른 쪽에서 timeScale을 되돌리면 다시 슬로우 적용
            ApplySlowMotion();

            float t = (Time.unscaledTime - _startTimeUnscaled) / KILLCAM_DURATION;
            if (t >= 1f)
            {
                EndKillCam();
                return;
            }

            // 부드러운 가속/감속
            t = Mathf.SmoothStep(0f, 1f, t);

            Vector3 camPos = Vector3.Lerp(_startPos, _endPos, t);

            Vector3 targetPos = _lookTarget != null ? _lookTarget.position : _endPos;

            // 카메라가 벽 속으로 들어가지 않게 간단 충돌 보정
            Vector3 dirFromTarget = camPos - targetPos;
            float dist = dirFromTarget.magnitude;
            if (dist > 0.01f)
            {
                RaycastHit hit;
                if (Physics.Raycast(
                        targetPos,
                        dirFromTarget.normalized,
                        out hit,
                        dist,
                        ~0,
                        QueryTriggerInteraction.Ignore))
                {
                    camPos = hit.point;
                }
            }

            _mainCamera.transform.position = camPos;
            _mainCamera.transform.LookAt(targetPos);
        }

        private void OnDestroy()
        {
            if (_isPlaying)
            {
                RestoreTimeScale();
            }
        }

        // ─────────────── 캐릭터 스캔 ───────────────

        private void RefreshCharacters()
        {
            if (_mainCamera == null)
                return;

            // 씬에 있는 모든 MonoBehaviour 검색
            MonoBehaviour[] allMono = GameObject.FindObjectsOfType<MonoBehaviour>();
            if (allMono == null || allMono.Length == 0)
                return;

            // 기존 적들은 일단 "안 보인다"로 마킹
            for (int i = 0; i < _trackedEnemies.Count; i++)
            {
                if (_trackedEnemies[i] != null)
                {
                    _trackedEnemies[i].stillExists = false;
                }
            }

            // 타입 이름에 "CharacterMainControl" 이 들어가는 컴포넌트만 골라서 캐릭터로 취급
            List<Transform> charTransforms = new List<Transform>();

            for (int i = 0; i < allMono.Length; i++)
            {
                MonoBehaviour mb = allMono[i];
                if (mb == null) continue;

                Type t = mb.GetType();
                if (t == null) continue;

                string typeName = t.Name;
                if (string.IsNullOrEmpty(typeName)) continue;

                if (typeName.Contains("CharacterMainControl"))
                {
                    Transform tr = mb.transform;
                    if (tr != null && !charTransforms.Contains(tr))
                    {
                        charTransforms.Add(tr);
                    }
                }
            }

            if (charTransforms.Count == 0)
                return;

            // 플레이어 추정: 카메라에 가장 가까운 CharacterMainControl 하나
            if (_playerTransform == null)
            {
                float bestDist = float.MaxValue;
                Transform best = null;
                Vector3 camPos = _mainCamera.transform.position;

                for (int i = 0; i < charTransforms.Count; i++)
                {
                    Transform tr = charTransforms[i];
                    float d = Vector3.Distance(tr.position, camPos);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = tr;
                    }
                }

                _playerTransform = best;
                if (_playerTransform != null)
                {
                    Debug.Log("[SniperKillCam] Player 후보 감지: " + _playerTransform.name);
                }
            }

            // 적 목록 갱신
            for (int i = 0; i < charTransforms.Count; i++)
            {
                Transform tr = charTransforms[i];
                if (tr == null)
                    continue;

                // 플레이어는 적 리스트에서 제외
                if (_playerTransform != null && tr == _playerTransform)
                    continue;

                TrackedEnemy te = null;

                for (int j = 0; j < _trackedEnemies.Count; j++)
                {
                    if (_trackedEnemies[j] != null && _trackedEnemies[j].transform == tr)
                    {
                        te = _trackedEnemies[j];
                        break;
                    }
                }

                if (te == null)
                {
                    te = new TrackedEnemy();
                    te.transform = tr;
                    te.wasPresent = true;
                    _trackedEnemies.Add(te);
                    Debug.Log("[SniperKillCam] 적 등록: " + tr.name);
                }

                te.stillExists = true;
                te.lastKnownPosition = tr.position;
                te.lastName = tr.name;
            }
        }

        // ─────────────── "사라진 적" = 사망으로 간주 ───────────────

        private void CheckEnemyDisappear()
        {
            if (_playerTransform == null || _mainCamera == null)
                return;

            for (int i = _trackedEnemies.Count - 1; i >= 0; i--)
            {
                TrackedEnemy te = _trackedEnemies[i];
                if (te == null)
                {
                    _trackedEnemies.RemoveAt(i);
                    continue;
                }

                bool wasPresent = te.wasPresent;
                bool nowExists = te.stillExists && te.transform != null;

                // 이전 스캔에서 존재했는데 이번 스캔에는 없음 → "죽었을 가능성"
                if (wasPresent && !nowExists)
                {
                    // ▶ 내가 최근에 쏜 직후에만 킬캠 시도 (0.8초 안)
                    float dt = Time.unscaledTime - _lastShotTimeUnscaled;
                    if (dt >= 0f && dt <= 0.8f)
                    {
                        TryStartKillCamForEnemy(te.lastKnownPosition, te.lastName);
                    }
                    else
                    {
                        Debug.Log("[SniperKillCam] 적 사라짐 감지 but 최근 샷 아님 -> 킬캠 스킵: "
                                  + te.lastName + " dt=" + dt.ToString("F2"));
                    }

                    _trackedEnemies.RemoveAt(i);
                    continue;
                }

                te.wasPresent = nowExists;

                // 더 이상 존재하지 않으면 리스트에서 제거
                if (!nowExists)
                {
                    _trackedEnemies.RemoveAt(i);
                }
                else
                {
                    // 다음 스캔에서 다시 채울 거라 false로 리셋
                    te.stillExists = false;
                }
            }
        }

        // ─────────────── KillCam 시작 조건 / 연출 ───────────────

        private void TryStartKillCamForEnemy(Vector3 enemyPos, string enemyName)
        {
            if (_isPlaying) return;
            if (_playerTransform == null || _mainCamera == null) return;

            Vector3 playerPos = _playerTransform.position;
            float dist = Vector3.Distance(enemyPos, playerPos);

            // 너무 가까운 적은 킬캠 스킵 (스나이퍼 느낌)
            if (dist < SNIPER_MIN_DISTANCE)
                return;

            // ── 방향 필터: 내가 쏜 방향과 적 위치가 어느 정도 일치하는지 체크 ──

            // 1) 적이 "발사 방향 앞쪽"에 있는지 (뒤쪽이면 스킵)
            Vector3 shotToEnemy = enemyPos - _lastShotOrigin;
            if (shotToEnemy.sqrMagnitude < 0.0001f)
            {
                // 발사 위치와 거의 같은 위치면 그냥 스킵
                Debug.Log("[SniperKillCam] KillCam 스킵: enemy too close to shot origin");
                return;
            }

            Vector3 dirToEnemy = shotToEnemy.normalized;
            float dot = Vector3.Dot(_lastShotForward, dirToEnemy);
            if (dot <= 0f)
            {
                // 내 샷 방향의 정반대 혹은 옆쪽 → 내가 쏜 방향이 아님
                Debug.Log("[SniperKillCam] KillCam 스킵: enemy behind shot direction: " + enemyName);
                return;
            }

            // 2) 각도 제한
            float angle = Vector3.Angle(_lastShotForward, dirToEnemy);
            if (angle > MAX_SHOT_ANGLE)
            {
                Debug.Log("[SniperKillCam] KillCam 스킵: angle too large " +
                          angle.ToString("F1") + " deg, target=" + enemyName);
                return;
            }

            // 3) 샷 경로에서의 수직 거리 제한 (탄 경로에서 너무 벗어나면 스킵)
            float along = Vector3.Dot(shotToEnemy, _lastShotForward); // 경로 상 거리
            Vector3 closestPoint = _lastShotOrigin + _lastShotForward * along;
            float sideDist = (enemyPos - closestPoint).magnitude;
            if (sideDist > MAX_SHOT_SIDE_DISTANCE)
            {
                Debug.Log("[SniperKillCam] KillCam 스킵: side distance too large " +
                          sideDist.ToString("F2") + " target=" + enemyName);
                return;
            }

            // ── 여기까지 통과하면 "내가 방금 쏜 탄에 맞아 죽었을 확률이 높다"라고 보고 킬캠 ──

            // 머리 위치 대충 위로 올려서 사용
            Vector3 headPos = enemyPos + Vector3.up * 1.6f;

            Vector3 camPos = _mainCamera.transform.position;
            Vector3 camForward = _mainCamera.transform.forward;

            // 카메라 앞에서 약간 튀어나온 위치를 총알 출발점처럼 사용
            Vector3 startPos = camPos + camForward * 0.3f + Vector3.up * -0.05f;

            Vector3 dirToHead = headPos - startPos;
            if (dirToHead.sqrMagnitude < 0.0001f)
            {
                dirToHead = camForward;
            }

            // 머리 바로 앞까지 날아가게
            Vector3 endPos = headPos - dirToHead.normalized * 0.2f;

            StartKillCam(startPos, endPos, null);

            Debug.Log("[SniperKillCam] KillCam start: target=" + enemyName +
                      " dist=" + dist.ToString("F1") +
                      " angle=" + angle.ToString("F1") +
                      " sideDist=" + sideDist.ToString("F2"));
        }

        private void StartKillCam(Vector3 startPos, Vector3 endPos, Transform lookTarget)
        {
            if (_mainCamera == null)
                return;

            if (_isPlaying)
                return;

            _isPlaying = true;
            _startTimeUnscaled = Time.unscaledTime;

            _startPos = startPos;
            _endPos = endPos;
            _lookTarget = lookTarget;

            _savedTimeScale = Time.timeScale;
            if (_savedTimeScale <= 0f)
            {
                _savedTimeScale = 1f;
            }

            ApplySlowMotion();

            Debug.Log("[SniperKillCam] SlowMotion ON, saved=" + _savedTimeScale.ToString("F2")
                      + " current=" + Time.timeScale.ToString("F2"));
        }

        private void EndKillCam()
        {
            _isPlaying = false;
            _lookTarget = null;

            RestoreTimeScale();

            Debug.Log("[SniperKillCam] KillCam end, Time.timeScale=" + Time.timeScale.ToString("F2"));
        }

        // ─────────────── 타임스케일 유틸 ───────────────

        private void ApplySlowMotion()
        {
            if (Mathf.Abs(Time.timeScale - SLOW_TIMESCALE) > 0.0001f)
            {
                Time.timeScale = SLOW_TIMESCALE;
            }
        }

        private void RestoreTimeScale()
        {
            float target = _savedTimeScale;
            if (target <= 0f) target = 1f;
            Time.timeScale = target;
        }
    }
}
