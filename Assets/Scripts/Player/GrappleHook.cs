using System.Collections.Generic;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Hold right mouse: a hook flies from your waist toward the crosshair and latches onto the map.
    /// While attached you're reeled in and can swing; A/D push you sideways. Release to let go —
    /// the hook retracts and your momentum carries. No cooldown.
    /// The rope is drawn for everyone (remote players get the hook position through NetState).
    /// </summary>
    [DefaultExecutionOrder(150)] // after ProceduralAnimator so the waist bone is posed
    public class GrappleHook : MonoBehaviour
    {
        public enum HookState : byte { Idle, Flying, Attached, Retracting, AttachedPlayer }

        [Header("Hook")]
        [Tooltip("0 = unlimited: the hook flies until it hits something or you let go.")]
        public float maxRange = 0f;
        public bool Unlimited => maxRange <= 0f;
        /// <summary>How far to look for something to aim at under the crosshair.</summary>
        const float AimDistance = 5000f;
        public float hookSpeed = 110f;
        public float retractSpeed = 140f;

        [Header("Rope look")]
        public float ropeWidth = 0.02f;
        public Color ropeColor = new(0.22f, 0.18f, 0.14f, 1f);
        public int ropeSegments = 24;
        public float flyWobble = 0.18f;

        public HookState State { get; private set; }
        public bool IsAttached => State == HookState.Attached;
        public bool IsOut => State != HookState.Idle;
        /// <summary>Hook is stuck in another player/dummy (they get reeled in, you don't).</summary>
        public bool IsOnPlayer => State == HookState.AttachedPlayer;
        public Combatant HookedTarget { get; private set; }
        Transform _hookedPoint;
        static readonly RaycastHit[] HitBuf = new RaycastHit[16];
        static readonly System.Comparison<RaycastHit> ByDist = (a, b) => a.distance.CompareTo(b.distance);
        public Vector3 Tip { get; private set; }
        public Vector3 Anchor { get; private set; }
        /// <summary>Current rope length (shrinks as you get reeled in).</summary>
        public float RopeLength;

        PlayerController _pc;
        Transform _origin;           // "GrapplePoint" if you add one, otherwise the pelvis bone
        Vector3 _flyDir;
        float _flown;
        LineRenderer _rope;
        Transform _hookVisual;
        bool _remoteOut;
        Vector3 _remoteTip;

        public Vector3 Origin => _origin != null ? _origin.position : transform.position;

        void Awake()
        {
            _pc = GetComponent<PlayerController>();
            _origin = FindDeep(transform, "GrapplePoint");
            if (_origin == null && _pc != null && _pc.Bones != null) _pc.Bones.TryGetValue("Bone", out _origin);
            BuildVisuals();
        }

        void BuildVisuals()
        {
            var go = new GameObject("GrappleRope");
            go.transform.SetParent(transform, false);
            _rope = go.AddComponent<LineRenderer>();
            _rope.sharedMaterial = new Material(Shader.Find("Sprites/Default"));
            _rope.startColor = _rope.endColor = ropeColor;
            _rope.widthMultiplier = ropeWidth;
            _rope.numCapVertices = 2;
            _rope.alignment = LineAlignment.View;
            _rope.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            _rope.useWorldSpace = true;
            _rope.enabled = false;

            var hook = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hook.name = "Hook";
            Destroy(hook.GetComponent<Collider>());
            hook.transform.SetParent(transform, false);
            hook.transform.localScale = new Vector3(0.07f, 0.07f, 0.14f);
            var r = hook.GetComponent<Renderer>();
            r.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.35f, 0.35f, 0.38f) };
            hook.SetActive(false);
            _hookVisual = hook.transform;
        }

        // ---------------- owner logic ----------------

        public void ResetHook()
        {
            State = HookState.Idle;
            HookedTarget = null;
            _rope.enabled = false;
            _hookVisual.gameObject.SetActive(false);
        }

        void Update()
        {
            if (_pc == null || !_pc.IsSpawned || !_pc.IsOwner) return;
            if (!_pc.IsAlive) { if (State != HookState.Idle) ResetHook(); return; }

            bool held = InputMap.GrappleHeld;
            float dt = Time.deltaTime;

            switch (State)
            {
                case HookState.Idle:
                    if (held) Launch();
                    break;

                case HookState.Flying:
                    if (!held) { State = HookState.Retracting; break; }
                    float step = hookSpeed * dt;
                    if (TryHitCombatant(step, out var victimBox, out var victimHit))
                    {
                        HookedTarget = victimBox.Owner;
                        _hookedPoint = victimBox.Start;
                        Tip = victimHit.point;
                        State = HookState.AttachedPlayer;
                        _pc.NotifyHookHit(victimBox.Owner, victimBox.SegmentIndex, victimHit.point, _flyDir);
                        break;
                    }
                    if (Physics.Raycast(Tip, _flyDir, out var hit, step, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                    {
                        if (!SurfaceRules.CanGrapple(hit.collider))
                        {
                            // glass: the hook glances off and comes back
                            Tip = hit.point;
                            State = HookState.Retracting;
                            if (FxManager.I != null) FxManager.I.Sparks(hit.point, hit.normal, 6, 3f);
                            break;
                        }
                        Tip = Anchor = hit.point;
                        RopeLength = Vector3.Distance(Origin, Anchor);
                        State = HookState.Attached;
                        break;
                    }
                    Tip += _flyDir * step;
                    _flown += step;
                    if (!Unlimited && _flown >= maxRange) State = HookState.Retracting;
                    break;

                case HookState.Attached:
                    if (!held) State = HookState.Retracting;
                    Tip = Anchor;
                    break;

                case HookState.AttachedPlayer:
                    if (!held || HookedTarget == null || !HookedTarget.IsAlive || !HookedTarget.Visible ||
                        (!Unlimited && (Origin - Tip).sqrMagnitude > (maxRange * 1.3f) * (maxRange * 1.3f)))
                    {
                        State = HookState.Retracting;
                        HookedTarget = null;
                        break;
                    }
                    Tip = _hookedPoint != null ? _hookedPoint.position : HookedTarget.transform.position;
                    break;

                case HookState.Retracting:
                    if (held) { Launch(); break; } // no cooldown: re-fire instantly
                    Tip = Vector3.MoveTowards(Tip, Origin, retractSpeed * dt);
                    if ((Tip - Origin).sqrMagnitude < 0.09f) State = HookState.Idle;
                    break;
            }
        }

        /// <summary>Did the hook hit another player's/dummy's hitbox this step (before any wall)?</summary>
        bool TryHitCombatant(float step, out Hitbox box, out RaycastHit hit)
        {
            box = null;
            hit = default;
            int n = Physics.RaycastNonAlloc(Tip, _flyDir, HitBuf, step, Layers.BulletMask, QueryTriggerInteraction.Collide);
            System.Array.Sort(HitBuf, 0, n, Comparer<RaycastHit>.Create(ByDist));
            for (int i = 0; i < n; i++)
            {
                var h = HitBuf[i];
                var hb = h.collider.GetComponent<Hitbox>();
                if (hb != null)
                {
                    if (hb.Owner == null || hb.Owner == (Combatant)_pc || !hb.Owner.IsAlive) continue;
                    box = hb;
                    hit = h;
                    return true;
                }
                if (!h.collider.isTrigger) return false; // a wall is in front of any player
            }
            return false;
        }

        void Launch()
        {
            // aim at whatever is under the crosshair, but fire from the waist
            var cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : Origin;
            Vector3 aim = _pc.Motor.AimDirection;
            float reach = Unlimited ? AimDistance : maxRange;
            Vector3 target = Physics.Raycast(eye, aim, out var hit, reach, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore)
                ? hit.point
                : eye + aim * reach;
            Tip = Origin;
            _flyDir = (target - Tip).normalized;
            _flown = 0f;
            State = HookState.Flying;
        }

        // ---------------- remote state (from NetState) ----------------

        public void SetRemote(bool hookOut, Vector3 tip)
        {
            _remoteOut = hookOut;
            _remoteTip = tip;
        }

        // ---------------- visuals (everyone) ----------------

        void LateUpdate()
        {
            if (_pc == null || !_pc.IsSpawned) return;
            bool owner = _pc.IsOwner;
            bool show = (owner ? IsOut : _remoteOut) && _pc.Visible;
            Vector3 tip = owner ? Tip : _remoteTip;
            bool taut = owner ? (IsAttached || IsOnPlayer) : false;

            _rope.enabled = show;
            _hookVisual.gameObject.SetActive(show);
            if (!show) return;

            Vector3 a = Origin;
            Vector3 d = tip - a;
            float len = d.magnitude;
            Vector3 dir = len > 0.001f ? d / len : Vector3.forward;
            Vector3 side = Vector3.Cross(dir, Vector3.up);
            if (side.sqrMagnitude < 0.001f) side = Vector3.right;
            side.Normalize();
            Vector3 up = Vector3.Cross(side, dir);

            int n = Mathf.Max(2, ropeSegments);
            _rope.positionCount = n;
            float wobble = taut ? 0f : flyWobble * Mathf.Clamp01(len / 8f);
            float sag = taut ? 0f : Mathf.Min(0.6f, len * 0.03f);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                float env = Mathf.Sin(t * Mathf.PI);
                Vector3 p = a + d * t
                            + side * (Mathf.Sin(t * 14f - Time.time * 30f) * wobble * env)
                            - up * (sag * env);
                _rope.SetPosition(i, p);
            }
            _hookVisual.SetPositionAndRotation(tip, Quaternion.LookRotation(dir));
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
