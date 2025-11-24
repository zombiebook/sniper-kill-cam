using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ItemStatsSystem;
using UnityEngine;

// ─────────────────────────────────────────────────────────────
// SniperKillCam.cs
// - 모드로 체크 가능한 엔트리(sniperkillcam.ModBehaviour)
// - 저격 대미지: 몸 0.99배 / 머리 3배
// - 멀리 있는 적을 저격으로 죽이면 슬로우 + 총알 추적 킬캠
// - 테스트 입력: 우클릭(조준) + Q 로 레이캐스트 발사
// ─────────────────────────────────────────────────────────────

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

                // 매니저 및 테스트용 컴포넌트 추가
                root.AddComponent<SniperKillCamManager>();
                root.AddComponent<SniperKillCamTester>();

                Debug.Log("[sniperkillcam] Sniper Kill Cam + Damage mod loaded.");
            }
            catch (Exception ex)
            {
                Debug.Log("[sniperkillcam] OnAfterSetup 예외: " + ex);
            }
        }
    }

    /// <summary>
    /// 실제 킬캠 카메라 연출 담당 매니저
    /// </summary>
    public class SniperKillCamManager : MonoBehaviour
    {
        private static SniperKillCamManager _instance;
        public static SniperKillCamManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = UnityEngine.Object.FindObjectOfType<SniperKillCamManager>();
                    if (_instance == null)
                    {
                        GameObject go = new GameObject("SniperKillCamManager_Auto");
                        UnityEngine.Object.DontDestroyOnLoad(go);
                        _instance = go.AddComponent<SniperKillCamManager>();
                    }
                }
                return _instance;
            }
        }

        private Camera _mainCamera;

        private bool _isKillCamPlaying;
        private float _killCamTimer;
        private float _killCamDuration = 1.5f; // 실시간 기준 1.5초

        private Vector3 _camStartPos;
        private Quaternion _camStartRot;

        private Vector3 _killStartPos;
        private Vector3 _killTargetPos;

        private float _originalTimeScale = 1f;

        private void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
            }
            else if (_instance != this)
            {
                Destroy(this.gameObject);
                return;
            }

            UnityEngine.Object.DontDestroyOnLoad(this.gameObject);
        }

        private void Update()
        {
            if (_isKillCamPlaying)
            {
                UpdateKillCam();
            }
        }

        public static void PlayKillCamStatic(Camera cam, Vector3 origin, Vector3 hitPoint)
        {
            if (cam == null) return;
            Instance.PlayKillCam(cam, origin, hitPoint);
        }

        public void PlayKillCam(Camera cam, Vector3 origin, Vector3 hitPoint)
        {
            if (cam == null) return;

            _mainCamera = cam;

            // 이미 킬캠 중이면 즉시 복원 후 새로 시작
            if (_isKillCamPlaying)
            {
                RestoreCamera();
            }

            _camStartPos = _mainCamera.transform.position;
            _camStartRot = _mainCamera.transform.rotation;

            _killStartPos = origin;
            _killTargetPos = hitPoint;

            _killCamTimer = 0f;
            _originalTimeScale = Time.timeScale;
            Time.timeScale = 0.2f; // 슬로우 모션

            _isKillCamPlaying = true;

            Debug.Log("[sniperkillcam] KillCam 시작 origin=" + origin + " target=" + hitPoint);
        }

        private void UpdateKillCam()
        {
            if (_mainCamera == null)
            {
                _mainCamera = Camera.main;
                if (_mainCamera == null)
                {
                    StopKillCam();
                    return;
                }
            }

            float dt = Time.unscaledDeltaTime; // 슬로우모션에서도 일정한 속도
            _killCamTimer += dt;

            float t = 0f;
            if (_killCamDuration > 0.0001f)
                t = Mathf.Clamp01(_killCamTimer / _killCamDuration);

            Vector3 dir = (_killTargetPos - _killStartPos);
            if (dir.sqrMagnitude < 0.0001f)
            {
                dir = _mainCamera.transform.forward;
            }
            Vector3 dirNorm = dir.normalized;

            // 카메라를 총알보다 약간 뒤에서 따라가는 느낌
            Vector3 camPos = Vector3.Lerp(
                _killStartPos - dirNorm * 2f + Vector3.up * 0.3f,
                _killTargetPos - dirNorm * 0.5f + Vector3.up * 0.3f,
                t
            );

            _mainCamera.transform.position = camPos;
            _mainCamera.transform.rotation = Quaternion.LookRotation(dirNorm, Vector3.up);

            if (t >= 1f)
            {
                StopKillCam();
            }
        }

        private void StopKillCam()
        {
            RestoreCamera();
            _isKillCamPlaying = false;
            Debug.Log("[sniperkillcam] KillCam 종료");
        }

        private void RestoreCamera()
        {
            try
            {
                if (_mainCamera != null)
                {
                    _mainCamera.transform.position = _camStartPos;
                    _mainCamera.transform.rotation = _camStartRot;
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[sniperkillcam] RestoreCamera 예외: " + ex);
            }

            Time.timeScale = _originalTimeScale;
        }
    }

    /// <summary>
    /// 테스트 및 입력 처리:
    /// - 우클릭(조준) + Q 를 누르면
    ///   카메라 중앙에서 Ray 쏴서 저격 대미지 + 킬캠 실행
    /// </summary>
    public class SniperKillCamTester : MonoBehaviour
    {
        private Camera _mainCamera;
        private int _layerMask;

        private void Start()
        {
            _mainCamera = Camera.main;
            _layerMask = ~0; // 모든 레이어
        }

        private void Update()
        {
            if (_mainCamera == null)
            {
                _mainCamera = Camera.main;
                if (_mainCamera == null) return;
            }

            // 예시: 우클릭(조준) 중일 때만 작동
            bool isAiming = Input.GetMouseButton(1);
            if (!isAiming) return;

            // Q 를 누르는 순간 한 번 발사
            if (Input.GetKeyDown(KeyCode.Q))
            {
                TryFireSniperRay();
            }
        }

        private void TryFireSniperRay()
        {
            try
            {
                Ray ray = _mainCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                RaycastHit hit;

                if (Physics.Raycast(ray, out hit, 1000f, _layerMask))
                {
                    float baseDamage = 50f; // 임시 기본 대미지 (나중에 총기에서 가져와도 됨)

                    Debug.Log("[sniperkillcam] Ray hit " + hit.collider.name + " baseDamage=" + baseDamage);

                    SniperKillCamLogic.ApplySniperShot(_mainCamera, hit.collider, hit.point, baseDamage);
                }
                else
                {
                    Debug.Log("[sniperkillcam] Ray hit 없음");
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[sniperkillcam] TryFireSniperRay 예외: " + ex);
            }
        }
    }

    // ─────────────────────────────────────────────────────
    // 저격 대미지 + 킬캠 트리거 로직
    // - Health 타입 이름 직접 사용 X
    // - "health" 가 들어간 컴포넌트를 자동으로 찾아서
    //   현재/최대 체력 멤버를 리플렉션으로 사용
    // - 몸샷: baseDamage * 0.99
    // - 헤드샷: baseDamage * 3.0
    // - 죽였고 거리가 멀면 KillCam 실행
    // ─────────────────────────────────────────────────────
    public static class SniperKillCamLogic
    {
        private static Type _cachedHealthType;
        private static PropertyInfo _propCurrentHealth;
        private static PropertyInfo _propMaxHealth;
        private static FieldInfo _fieldCurrentHealth;
        private static FieldInfo _fieldMaxHealth;

        public static void ApplySniperShot(Camera cam, Collider hitCol, Vector3 hitPoint, float baseDamage)
        {
            if (hitCol == null) return;
            if (baseDamage <= 0f) return;

            try
            {
                Component[] comps = hitCol.GetComponentsInParent<Component>();
                if (comps == null || comps.Length == 0)
                {
                    Debug.Log("[sniperkillcam] 상위 컴포넌트 없음");
                    return;
                }

                object healthInstance = null;
                Type healthType = null;

                for (int i = 0; i < comps.Length; i++)
                {
                    Component c = comps[i];
                    if (c == null) continue;

                    Type t = c.GetType();
                    string typeName = t.Name;
                    if (string.IsNullOrEmpty(typeName)) continue;

                    string lower = typeName.ToLowerInvariant();
                    if (lower.Contains("health"))
                    {
                        healthInstance = c;
                        healthType = t;
                        break;
                    }
                }

                if (healthInstance == null || healthType == null)
                {
                    Debug.Log("[sniperkillcam] Health 컴포넌트를 찾지 못했습니다.");
                    return;
                }

                InitHealthMembers(healthType);

                if (_propCurrentHealth == null && _fieldCurrentHealth == null)
                {
                    Debug.Log("[sniperkillcam] 현재 체력 멤버를 찾지 못했습니다. type=" + healthType.FullName);
                    return;
                }

                float curHp = GetMemberValue(healthInstance, _propCurrentHealth, _fieldCurrentHealth);
                float maxHp = curHp;
                if (_propMaxHealth != null || _fieldMaxHealth != null)
                {
                    maxHp = GetMemberValue(healthInstance, _propMaxHealth, _fieldMaxHealth);
                }

                bool isHead = IsHeadCollider(hitCol);

                // ── 대미지 배율 ──
                float multiplier = isHead ? 3.0f : 0.99f;
                float finalDamage = baseDamage * multiplier;

                float newHp = Mathf.Max(0f, curHp - finalDamage);

                SetMemberValue(healthInstance, _propCurrentHealth, _fieldCurrentHealth, newHp);

                Debug.Log(
                    "[sniperkillcam] hit head=" + isHead +
                    " base=" + baseDamage +
                    " mul=" + multiplier +
                    " final=" + finalDamage +
                    " HP " + curHp + " -> " + newHp
                );

                bool killed = newHp <= 0f;

                if (killed && cam != null)
                {
                    float dist = Vector3.Distance(cam.transform.position, hitPoint);

                    // 어느 정도 이상 거리일 때만 킬캠 (예: 30m 이상)
                    if (dist >= 30f)
                    {
                        SniperKillCamManager.PlayKillCamStatic(cam, cam.transform.position, hitPoint);
                    }

                    // 전리품/이펙트 보정용 Kill/Damage 메서드 호출 시도
                    TryCallKillOrApplyDamage(healthInstance, finalDamage);
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[sniperkillcam] ApplySniperShot 예외: " + ex);
            }
        }

        // ──────────────────────────────────────────────
        // Health 멤버(현재/최대 체력) 리플렉션 초기화
        // ──────────────────────────────────────────────
        private static void InitHealthMembers(Type healthType)
        {
            if (healthType == null) return;

            if (_cachedHealthType == healthType &&
                (_propCurrentHealth != null || _fieldCurrentHealth != null))
            {
                return;
            }

            _cachedHealthType = healthType;
            _propCurrentHealth = null;
            _propMaxHealth = null;
            _fieldCurrentHealth = null;
            _fieldMaxHealth = null;

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                PropertyInfo[] props = healthType.GetProperties(flags);

                _propCurrentHealth =
                    FindNumberProperty(props, "current", "health") ??
                    FindNumberProperty(props, "cur", "health") ??
                    FindNumberProperty(props, null, "health");

                _propMaxHealth =
                    FindNumberProperty(props, "max", "health") ??
                    FindNumberProperty(props, null, "max") ??
                    FindNumberProperty(props, null, "health");

                FieldInfo[] fields = healthType.GetFields(flags);

                if (_propCurrentHealth == null)
                {
                    _fieldCurrentHealth =
                        FindNumberField(fields, "current", "health") ??
                        FindNumberField(fields, "cur", "health") ??
                        FindNumberField(fields, null, "health");
                }

                if (_propMaxHealth == null)
                {
                    _fieldMaxHealth =
                        FindNumberField(fields, "max", "health") ??
                        FindNumberField(fields, null, "max") ??
                        FindNumberField(fields, null, "health");
                }

                Debug.Log(
                    "[sniperkillcam] Health 멤버 탐색 완료: type=" + healthType.FullName +
                    " curProp=" + NameOrNull(_propCurrentHealth) +
                    " curField=" + NameOrNull(_fieldCurrentHealth) +
                    " maxProp=" + NameOrNull(_propMaxHealth) +
                    " maxField=" + NameOrNull(_fieldMaxHealth)
                );
            }
            catch (Exception ex)
            {
                Debug.Log("[sniperkillcam] InitHealthMembers 예외: " + ex);
            }
        }

        private static string NameOrNull(MemberInfo m)
        {
            return m == null ? "null" : m.Name;
        }

        private static PropertyInfo FindNumberProperty(PropertyInfo[] props, string key1, string key2)
        {
            if (props == null) return null;

            string k1 = key1 != null ? key1.ToLowerInvariant() : null;
            string k2 = key2 != null ? key2.ToLowerInvariant() : null;

            for (int i = 0; i < props.Length; i++)
            {
                PropertyInfo p = props[i];
                Type t = p.PropertyType;
                if (t != typeof(float) && t != typeof(int)) continue;

                string name = p.Name.ToLowerInvariant();

                if (k1 != null && !name.Contains(k1)) continue;
                if (k2 != null && !name.Contains(k2)) continue;

                return p;
            }

            return null;
        }

        private static FieldInfo FindNumberField(FieldInfo[] fields, string key1, string key2)
        {
            if (fields == null) return null;

            string k1 = key1 != null ? key1.ToLowerInvariant() : null;
            string k2 = key2 != null ? key2.ToLowerInvariant() : null;

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                Type t = f.FieldType;
                if (t != typeof(float) && t != typeof(int)) continue;

                string name = f.Name.ToLowerInvariant();

                if (k1 != null && !name.Contains(k1)) continue;
                if (k2 != null && !name.Contains(k2)) continue;

                return f;
            }

            return null;
        }

        private static float GetMemberValue(object instance, PropertyInfo prop, FieldInfo field)
        {
            try
            {
                if (prop != null)
                {
                    object v = prop.GetValue(instance, null);
                    return Convert.ToSingle(v);
                }

                if (field != null)
                {
                    object v = field.GetValue(instance);
                    return Convert.ToSingle(v);
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[sniperkillcam] GetMemberValue 예외: " + ex);
            }

            return 0f;
        }

        private static void SetMemberValue(object instance, PropertyInfo prop, FieldInfo field, float value)
        {
            try
            {
                if (prop != null)
                {
                    if (prop.PropertyType == typeof(int))
                        prop.SetValue(instance, (int)value, null);
                    else
                        prop.SetValue(instance, value, null);
                    return;
                }

                if (field != null)
                {
                    if (field.FieldType == typeof(int))
                        field.SetValue(instance, (int)value);
                    else
                        field.SetValue(instance, value);
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[sniperkillcam] SetMemberValue 예외: " + ex);
            }
        }

        private static bool IsHeadCollider(Collider col)
        {
            if (col == null) return false;

            try
            {
                string name = col.name;
                if (!string.IsNullOrEmpty(name))
                {
                    string lower = name.ToLowerInvariant();
                    if (lower.Contains("head") || lower.Contains("머리"))
                        return true;
                }

                try
                {
                    if (col.CompareTag("head"))
                        return true;
                }
                catch
                {
                    // 태그 없으면 무시
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[sniperkillcam] IsHeadCollider 예외: " + ex);
            }

            return false;
        }

        private static void TryCallKillOrApplyDamage(object healthInstance, float damage)
        {
            if (healthInstance == null) return;

            try
            {
                Type t = healthInstance.GetType();
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                MethodInfo[] methods = t.GetMethods(flags);

                // 1) 매개변수 없는 Kill / Die 류
                MethodInfo killMethod = null;

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m.GetParameters().Length != 0) continue;

                    string n = m.Name.ToLowerInvariant();
                    if (n.Contains("kill") || n.Contains("die") || n.Contains("death"))
                    {
                        killMethod = m;
                        break;
                    }
                }

                if (killMethod != null)
                {
                    killMethod.Invoke(healthInstance, null);
                    Debug.Log("[sniperkillcam] Kill 메서드 호출: " + killMethod.Name);
                    return;
                }

                // 2) float/int 하나 받는 Damage / Hit 류
                MethodInfo dmgMethod = null;
                ParameterInfo[] dmgParams = null;

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1) continue;

                    string n = m.Name.ToLowerInvariant();
                    if (n.Contains("damage") || n.Contains("hit"))
                    {
                        dmgMethod = m;
                        dmgParams = ps;
                        break;
                    }
                }

                if (dmgMethod != null && dmgParams != null)
                {
                    object arg = damage;
                    if (dmgParams[0].ParameterType == typeof(int))
                        arg = (int)damage;

                    dmgMethod.Invoke(healthInstance, new object[] { arg });
                    Debug.Log("[sniperkillcam] Damage 메서드 호출: " + dmgMethod.Name + " (" + arg + ")");
                    return;
                }

                Debug.Log("[sniperkillcam] Kill/데미지 메서드 없음: type=" + t.FullName);
            }
            catch (Exception ex)
            {
                Debug.Log("[sniperkillcam] TryCallKillOrApplyDamage 예외: " + ex);
            }
        }
    }
}
