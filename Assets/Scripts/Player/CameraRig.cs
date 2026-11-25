using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Drives Camera.main: first person at the local player's eyes while alive,
    /// a death cam that watches your own ragdoll while dead, and a static view in menus.
    /// </summary>
    [DefaultExecutionOrder(300)]
    public class CameraRig : MonoBehaviour
    {
        public float speedFovBoost = 8f;
        public float deathCamDistance = 3.5f;
        public float deathCamHeight = 1.6f;

        Camera _cam;
        Vector3 _menuPos;
        Quaternion _menuRot;
        Vector3 _deathOffset;
        bool _wasDead;
        float _fov;

        void Awake()
        {
            _cam = GetComponent<Camera>();
            _menuPos = transform.position;
            _menuRot = transform.rotation;
            _cam.nearClipPlane = 0.03f;
            _fov = GameSettings.Fov;
        }

        void LateUpdate()
        {
            var pc = PlayerController.Local;
            float dt = Time.deltaTime;
            if (pc == null || !pc.IsSpawned)
            {
                transform.SetPositionAndRotation(_menuPos, _menuRot);
                _cam.fieldOfView = 70f;
                _wasDead = false;
                return;
            }

            if (pc.Dead.Value || !pc.Visible)
            {
                var rag = pc.LastRagdoll;
                Transform focus = null;
                if (rag != null)
                {
                    var bones = rag.transform.Find("both rig");
                    // ragdoll: follow the pelvis; sliced body (sword kill): follow the upper half
                    focus = bones != null ? FindDeep(bones, "Bone") : SlicedFocus(rag.transform);
                }
                Vector3 target = focus != null ? focus.position : pc.transform.position;
                if (!_wasDead)
                {
                    Vector3 back = -(transform.rotation * Vector3.forward);
                    back.y = 0;
                    if (back.sqrMagnitude < 0.01f) back = Vector3.back;
                    _deathOffset = back.normalized * deathCamDistance + Vector3.up * deathCamHeight;
                    _wasDead = true;
                }
                Vector3 desired = target + _deathOffset;
                // keep the camera out of walls
                if (Physics.Linecast(target, desired, out var hit, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                    desired = hit.point + hit.normal * 0.2f;
                transform.position = Vector3.Lerp(transform.position, desired, 1f - Mathf.Exp(-4f * dt));
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(target - transform.position), 1f - Mathf.Exp(-8f * dt));
                _cam.fieldOfView = Mathf.Lerp(_cam.fieldOfView, GameSettings.Fov, 1f - Mathf.Exp(-4f * dt));
                return;
            }

            _wasDead = false;
            transform.SetPositionAndRotation(pc.Animator.EyePosition, pc.Motor.ViewRotation);
            float speedT = Mathf.InverseLerp(pc.Motor.runSpeed, pc.Motor.maxSpeed, pc.Motor.HorizontalSpeed);
            _fov = Mathf.Lerp(_fov, GameSettings.Fov + speedFovBoost * speedT, 1f - Mathf.Exp(-6f * dt));
            _cam.fieldOfView = HorizontalToVerticalFov(_fov, _cam.aspect);
        }

        static float HorizontalToVerticalFov(float hFov, float aspect)
        {
            // Settings FOV is horizontal at 16:9 (like most shooters); convert to Unity's vertical FOV.
            float h = hFov * Mathf.Deg2Rad;
            float v = 2f * Mathf.Atan(Mathf.Tan(h / 2f) / (16f / 9f));
            return v * Mathf.Rad2Deg;
        }

        /// <summary>Sword kill: follow a real (colliding) bone of the upper half, else whatever is there.</summary>
        static Transform SlicedFocus(Transform rag)
        {
            var half = rag.Find("Upper") ?? rag.Find("Lower");
            if (half == null) return rag;
            foreach (var rb in half.GetComponentsInChildren<Rigidbody>())
            {
                var col = rb.GetComponent<Collider>();
                if (col != null && col.enabled) return rb.transform;
            }
            return half;
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            foreach (Transform c in root)
            {
                var f = FindDeep(c, name);
                if (f != null) return f;
            }
            return null;
        }
    }
}
