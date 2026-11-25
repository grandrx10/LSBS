using System.Collections.Generic;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Fully procedural animation for the "fellas" rig: velocity-driven gait in any
    /// direction (so strafing/backpedalling need no extra clips), idle, crouch, slide,
    /// airborne and wall-run poses, look pitch through the spine/head, and two-bone IK
    /// that puts both hands on the gun. The gun is placed relative to the eyes, so the
    /// same pose works as a first-person view (local) and third-person (remote).
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ProceduralAnimator : MonoBehaviour
    {
        [Header("Pistol hold (one-handed, right hand)")]
        public Transform gun;
        [Tooltip("Where the gun hand's palm (the gun's TriggerPoint) sits relative to the eyes: x right, y up, z forward (metres).")]
        public Vector3 handOffset = new(0.13f, -0.2f, 0.52f);
        [Tooltip("Distance from the wrist to the palm/grip point.")]
        public float palmDistance = 0.08f;
        [Tooltip("Direction the fingers point, in gun space (x right, y up, z forward).")]
        public Vector3 handGripDir = new(0f, -0.55f, 0.85f);
        [Range(0, 1)] public float clavicleReach = 0.15f;

        [Header("Rifle (two-handed)")]
        [Tooltip("Where the trigger hand sits relative to the eyes for rifles (x right, y up, z forward).")]
        public Vector3 rifleHandOffset = new(0.11f, -0.16f, 0.36f);
        [Tooltip("How far along the barrel the left hand holds the forend, if the model has no SupportPoint marker.")]
        public float rifleSupportDistance = 0.3f;
        [Tooltip("Direction the left hand's fingers point under the forend, in gun space.")]
        public Vector3 supportGripDir = new(0.5f, -0.45f, 0.75f);
        [Tooltip("Degrees the muzzle rocks up while the lever is thrown.")]
        public float cockPitch = 16f;
        [Tooltip("Degrees the rifle rolls while the lever is thrown.")]
        public float cockRoll = 18f;
        [Tooltip("How far the rifle drops/moves while the lever is thrown (eye space: x right, y up, z forward).")]
        public Vector3 cockOffset = new(-0.02f, -0.05f, -0.04f);

        [Header("Sword (two-handed, both hands on the Handle)")]
        // Not serialized on purpose: the values in this file are always the ones used in game
        // (Unity otherwise keeps a stale saved copy and code changes silently don't apply).
        /// <summary>Where the Handle sits relative to the eyes at rest (x right, y up, z forward).</summary>
        [System.NonSerialized] public Vector3 swordIdleOffset = new(0.17f, -0.15f, 0.38f);
        /// <summary>Blade direction at rest, in eye space (straight up, leaning slightly back).</summary>
        [System.NonSerialized] public Vector3 swordIdleDir = new(0f, 1f, -0.2f);
        /// <summary>Centre of the swing arc for the Handle, relative to the eyes.</summary>
        [System.NonSerialized] public Vector3 swordSwingCentre = new(0.02f, -0.1f, 0.42f);
        /// <summary>Which way the fingers point from the wrist, in eye space (forward, a little inward and down).</summary>
        [System.NonSerialized] public Vector3 swordHandDir = new(-0.25f, -0.2f, 1f);
        [Tooltip("Degrees either side of straight ahead the blade sweeps through.")]
        public float swingArc = 80f;
        [Tooltip("Extra degrees of wind-up before the swing.")]
        public float swingWindup = 15f;
        [Tooltip("Right hand sits this far up the grip from the Handle point, left hand this far down (metres).")]
        public float swordHandGap = 0.05f;

        [Header("Dual pistols")]
        [Tooltip("Sideways distance of each hand from the eye line when holding two guns (metres).")]
        public float dualHandSpread = 0.17f;
        [Tooltip("Degrees each gun is turned inward toward the crosshair.")]
        public float dualToeIn = 3f;

        [Header("Free arm")]
        public float freeArmSwing = 0.18f;

        [Header("Gait")]
        public float groundY = -0.92f;      // ankle height below the hips when standing (model space, metres)
        public float stanceWidth = 0.13f;
        public float maxStride = 0.42f;
        public float stepHeight = 0.12f;

        [Header("Poses")]
        public float crouchDrop = 0.32f;
        public float slideDrop = 0.5f;
        public float slideLeanBack = 22f;
        public float wallRunRoll = 20f;

        [Header("Camera")]
        [Tooltip("0 = rock-steady camera. 1 = camera follows the full body bob of the run cycle.")]
        [Range(0, 1)] public float cameraBob = 0f;

        public Vector3 EyePosition { get; private set; }
        float _eyeHeight = 0.865f;
        public Quaternion GunRotation => _gunRot;

        Combatant _pc;
        Transform _model, _rigRoot, _pelvis, _chest, _head;
        Limb _armL, _armR, _legL, _legR;
        readonly List<(Transform t, Quaternion r, Vector3 p)> _bind = new();
        Vector3 _rigBindLocal;
        Transform _triggerPoint;

        float _phase, _drop, _lean, _sideLean, _roll, _slideW, _airW, _moveW, _reload;
        Vector3 _footL, _footR;
        Quaternion _gunRot = Quaternion.identity;
        bool _initialised;

        class Limb
        {
            public Transform root, upper, lower, end, tip;
            public float side;
        }

        void Awake()
        {
            _pc = GetComponent<Combatant>();
            _model = _pc.model != null ? _pc.model : transform.Find("PlayerModel");
            var b = Rig.MapBones(_model);
            _rigRoot = Find(b, "both rig");
            _pelvis = Find(b, "Bone");
            _chest = Find(b, "Bone.003");
            _head = Find(b, "Bone.001");
            _armL = MakeLimb(b, "Arm L", -1);
            _armR = MakeLimb(b, "Arm R", 1);
            _legL = MakeLimb(b, "Leg L", -1);
            _legR = MakeLimb(b, "Leg R", 1);
            foreach (var t in _model.GetComponentsInChildren<Transform>(true))
                _bind.Add((t, t.localRotation, t.localPosition));
            if (_rigRoot) _rigBindLocal = _rigRoot.localPosition;
            if (_head) _eyeHeight = Vector3.Dot(_head.position + _head.rotation * Vector3.up * 0.15f - _model.position, _model.up);
            if (gun == null) gun = transform.Find("Gun");
            if (gun != null)
                _triggerPoint = FindDeep(gun, "TriggerPoint") ?? FindDeep(gun, "Trigger Point") ?? FindDeep(gun, "Trigger");
            _initialised = _pelvis && _chest && _head && _armL != null && _armR != null && _legL != null && _legR != null;
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

        static Transform Find(Dictionary<string, Transform> b, string n) => b.TryGetValue(n, out var t) ? t : null;

        static Limb MakeLimb(Dictionary<string, Transform> b, string n, float side)
        {
            if (!b.TryGetValue(n, out var root) || !b.TryGetValue(n + ".001", out var up) || !b.TryGetValue(n + ".002", out var lo) ||
                !b.TryGetValue(n + ".003", out var end) || !b.TryGetValue(n + ".003_end", out var tip)) return null;
            return new Limb { root = root, upper = up, lower = lo, end = end, tip = tip, side = side };
        }

        void LateUpdate()
        {
            if (!_initialised || !_pc.IsSpawned) return;
            float dt = Time.deltaTime;

            // ---- inputs ----
            byte flags = _pc.ViewFlags;
            bool grounded = (flags & NetState.Grounded) != 0;
            bool sliding = (flags & NetState.Sliding) != 0;
            bool crouching = (flags & NetState.Crouching) != 0;
            int wallSide = (flags & NetState.WallLeft) != 0 ? -1 : (flags & NetState.WallRight) != 0 ? 1 : 0;
            bool reloading = (flags & NetState.Reloading) != 0;
            bool onSurface = grounded || wallSide != 0;
            float yaw = _pc.ViewYaw, pitch = _pc.ViewPitch;
            Vector3 vel = _pc.ViewVelocity;
            Quaternion yawRot = Quaternion.Euler(0, yaw, 0);
            Vector3 lv = Quaternion.Inverse(yawRot) * new Vector3(vel.x, 0, vel.z);
            float speed = lv.magnitude;
            Vector3 moveDir = speed > 0.1f ? lv / speed : Vector3.forward;

            float k = 1f - Mathf.Exp(-12f * dt);
            _slideW = Mathf.Lerp(_slideW, sliding ? 1 : 0, k);
            _airW = Mathf.Lerp(_airW, onSurface ? 0 : 1, 1f - Mathf.Exp(-8f * dt));
            _moveW = Mathf.Lerp(_moveW, onSurface && !sliding ? Mathf.Clamp01(speed / 3f) : 0, k);
            _drop = Mathf.Lerp(_drop, sliding ? slideDrop : crouching ? crouchDrop : 0f, k);
            _roll = Mathf.Lerp(_roll, wallSide * wallRunRoll, k);
            _lean = Mathf.Lerp(_lean, onSurface && !sliding ? Mathf.Clamp(lv.z * 0.9f, -6f, 12f) : 0f, k);
            _sideLean = Mathf.Lerp(_sideLean, onSurface ? Mathf.Clamp(-lv.x * 0.7f, -8f, 8f) : 0f, k);
            _reload = Mathf.MoveTowards(_reload, reloading ? 1 : 0, dt * 5f);

            // ---- reset to bind pose ----
            foreach (var (bt, br, bp) in _bind) { bt.localRotation = br; bt.localPosition = bp; }
            _head.localScale = _pc.HideHead ? Vector3.zero : Vector3.one; // only if the arms-only first-person body is unavailable
            _model.localRotation = Quaternion.Euler(0, 0, _roll);

            Quaternion frame = _model.rotation; // yaw * roll (no scale)
            Vector3 origin = _model.position;
            Vector3 fUp = frame * Vector3.up, fFwd = frame * Vector3.forward, fRight = frame * Vector3.right;

            // ---- gait phase ----
            if (onSurface && !sliding)
            {
                float cycle = Mathf.Clamp(1.3f + speed * 0.11f, 1.3f, 3.6f);
                _phase += Mathf.PI * 2f * speed * dt / cycle;
            }
            _phase = Mathf.Repeat(_phase, Mathf.PI * 2f);
            float bob = (Mathf.Cos(_phase * 2f) * 0.5f - 0.5f) * 0.05f * _moveW;

            // ---- hips & spine ----
            _rigRoot.position += fUp * (bob - _drop);
            float lean = _lean + _airW * 6f - _slideW * slideLeanBack;
            _pelvis.rotation = Quaternion.AngleAxis(lean, fRight) * Quaternion.AngleAxis(_sideLean, fFwd) * _pelvis.rotation;

            float p = Mathf.Clamp(pitch, -85f, 85f);
            _chest.rotation = Quaternion.AngleAxis(p * 0.35f - lean * 0.3f, fRight) * _chest.rotation;
            _head.rotation = Quaternion.AngleAxis(p * 0.6f - lean * 0.6f, fRight) * Quaternion.AngleAxis(-_roll * 0.6f, fFwd) * _head.rotation;

            // ---- eyes & gun ----
            Quaternion view = Quaternion.Euler(pitch, yaw, 0);
            if (_pc.TryGetViewRotation(out var exact)) view = exact;
            // Eyes sit at a stable height above the hips (crouch/slide lower it smoothly) instead of
            // riding the head bone, so the run cycle doesn't bounce the camera.
            EyePosition = origin + fUp * (_eyeHeight - _drop + bob * cameraBob) + (view * Vector3.forward) * 0.07f;

            _gunRot = Quaternion.Slerp(_gunRot, view, 1f - Mathf.Exp(-35f * dt));
            if (Quaternion.Angle(_gunRot, view) > 25f) _gunRot = Quaternion.RotateTowards(view, _gunRot, 25f);
            // ---- arms: right hand holds the pistol (both hands for dual weapons), otherwise hang free ----
            var weapon = _pc.Weapon;
            Transform rGun = weapon != null && weapon.Right != null ? weapon.Right.root : gun;
            Transform rTrig = weapon != null && weapon.Right != null ? weapon.Right.trigger : _triggerPoint;
            bool dual = weapon != null && weapon.IsDual && weapon.Left != null && weapon.Left.trigger != null;
            if (weapon != null && weapon.Current.hold == HoldStyle.Sword && weapon.Right != null && weapon.Right.trigger != null)
            {
                PoseSword(weapon, weapon.Right, fRight, fUp, fFwd);
            }
            else if (weapon != null && weapon.Current.hold == HoldStyle.Rifle && weapon.Right != null && weapon.Right.trigger != null)
            {
                PoseRifle(weapon.Right, weapon.Definition, weapon.DrawProgress, fRight, fUp, fFwd);
            }
            else if (rGun != null && rTrig != null)
            {
                var def = weapon != null ? weapon.Definition : GunDefinition.Fallback;
                float draw = weapon != null ? weapon.DrawProgress : 1f;
                var style = weapon != null ? weapon.Current.draw : DrawStyle.CrossDrawLeftHip;
                PoseGunHand(_armR, rGun, rTrig, weapon != null && weapon.Right != null ? weapon.Right.kick : 0f, 1f, dual, def, draw, style, fRight, fUp, fFwd);
                if (dual)
                    PoseGunHand(_armL, weapon.Left.root, weapon.Left.trigger, weapon.Left.kick, -1f, true, def, draw, style, fRight, fUp, fFwd);
                else
                    SolveFreeArm(_armL, fRight, fUp, fFwd);
            }
            else
            {
                SolveFreeArm(_armR, fRight, fUp, fFwd);
                SolveFreeArm(_armL, fRight, fUp, fFwd);
            }

            // ---- legs ----
            SolveLeg(_legL, ref _footL, 0f, dt, origin, frame, moveDir, speed, grounded, sliding);
            SolveLeg(_legR, ref _footR, Mathf.PI, dt, origin, frame, moveDir, speed, grounded, sliding);

            _pc.Hitboxes?.Sync();
        }

        /// <summary>
        /// Aims one gun hand (side +1 = right, -1 = left) and plays the draw animation while draw &lt; 1.
        /// Wolfgang: cross-draw from the left hip. Dual Beethovens: each hand draws from its own hip, spinning the gun.
        /// </summary>
        void PoseGunHand(Limb arm, Transform gunT, Transform trigger, float kick, float side, bool dual, GunDefinition def,
            float draw, DrawStyle style, Vector3 fRight, Vector3 fUp, Vector3 fFwd)
        {
            float rl = Mathf.SmoothStep(0, 1, _reload);

            // ---- ready (aiming) pose ----
            Vector3 offset = dual
                ? new Vector3(side * dualHandSpread, handOffset.y + 0.01f, handOffset.z - 0.04f)
                : handOffset;
            Quaternion aimRot = _gunRot * Quaternion.Euler(-def.visualKickPitch * kick + 25f * rl, dual ? -side * dualToeIn : 0f, -40f * rl * side);
            Vector3 aimPalm = EyePosition + _gunRot * (offset + new Vector3(-0.04f * side, -0.08f, -0.1f) * rl)
                              - (aimRot * Vector3.forward) * (def.visualKickBack * kick);
            Quaternion gunRot = aimRot;
            Quaternion gripRot = aimRot; // orientation the wrist follows (no spin)
            Vector3 palmTarget = aimPalm;

            // ---- draw ----
            if (draw < 1f)
            {
                bool cross = style == DrawStyle.CrossDrawLeftHip;
                float holsterSide = cross ? -1f : side;               // Wolfgang lives on the left hip
                Vector3 hip = _pelvis.position + fRight * (holsterSide * 0.19f) - fUp * 0.08f + fFwd * 0.02f;
                // holstered: barrel down, butt forward (so the right hand can grab it across the body)
                Quaternion holster = Quaternion.LookRotation(-fUp, -fFwd);
                float pullEnd = cross ? 0.3f : 0.2f;
                Vector3 pulled = hip + fUp * 0.13f;
                if (draw < pullEnd)
                {
                    // grip and lift out of the holster
                    float u = Mathf.SmoothStep(0, 1, draw / pullEnd);
                    palmTarget = Vector3.Lerp(hip, pulled, u);
                    gunRot = gripRot = holster;
                }
                else
                {
                    float u = (draw - pullEnd) / (1f - pullEnd);
                    float e = 1f - (1f - u) * (1f - u) * (1f - u); // ease out
                    // swing up in front of the chest on the way to the aim pose
                    Vector3 ctrl = cross
                        ? _chest.position + fFwd * 0.35f - fRight * 0.08f - fUp * 0.05f
                        : _chest.position + fFwd * 0.3f + fRight * (side * 0.28f) - fUp * 0.1f;
                    palmTarget = Bezier(pulled, ctrl, aimPalm, e);
                    gripRot = Quaternion.Slerp(holster, aimRot, e);
                    gunRot = gripRot;
                    if (!cross)
                    {
                        // a full 360 vertical spin around the trigger finger, done up in front where you can see it:
                        // the gun points straight ahead before the spin starts, then flips nose-up, over the top
                        // and back down into aim
                        gripRot = Quaternion.Slerp(holster, aimRot, Mathf.Clamp01(e / 0.5f));
                        float s = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.3f, 0.95f, u));
                        gunRot = gripRot * Quaternion.AngleAxis(-360f * s, Vector3.right);
                    }
                }
            }

            Vector3 handDir = (gripRot * handGripDir).normalized;
            SolveGunArm(arm, palmTarget - handDir * palmDistance, handDir, fRight, fUp, fFwd);

            // put the gun's TriggerPoint exactly in the palm the arm actually reached
            Vector3 palm = arm.end.position + handDir * palmDistance;
            gunT.rotation = gunRot;
            gunT.position += palm - trigger.position;
        }

        /// <summary>
        /// Two-handed rifle: stock at the shoulder, right hand on the TriggerPoint (riding the lever when it's cocked),
        /// left hand under the forend. Reload: rifle cants to show the loading gate and the left hand thumbs rounds in.
        /// Draw: raised from a low-ready position.
        /// </summary>
        void PoseRifle(WeaponController.GunVisual g, GunDefinition def, float draw, Vector3 fRight, Vector3 fUp, Vector3 fFwd)
        {
            float rl = Mathf.SmoothStep(0, 1, _reload);
            float lever = g.LeverOpen;

            // ---- where the gun goes ----
            // cocking: the rifle drops off the shoulder a little, rocks muzzle-up and rolls as the lever is thrown
            // reload: muzzle up, rolled to the right (top of the gun toward the right), pulled in toward the chest
            float cockKick = Mathf.Sin(lever * Mathf.PI * 0.5f); // fast in, eases at full open
            // pump guns: the muzzle dips a little as the left hand racks the Handle (which carries OffhandHold with it)
            float pumpDip = 4f * Mathf.Sin(g.PumpOpen * Mathf.PI * 0.5f);
            Quaternion rot = _gunRot * Quaternion.Euler(-def.visualKickPitch * g.kick - cockPitch * cockKick - 18f * rl + pumpDip, 0,
                                                        cockRoll * cockKick - 35f * rl);
            Vector3 triggerPos = EyePosition + _gunRot * (rifleHandOffset + new Vector3(-0.05f, -0.07f, -0.08f) * rl
                                                          + cockOffset * cockKick)
                                 - (rot * Vector3.forward) * (def.visualKickBack * g.kick);
            if (draw < 1f)
            {
                float e = 1f - (1f - draw) * (1f - draw) * (1f - draw); // ease out
                Quaternion low = _gunRot * Quaternion.Euler(55f, -15f, 10f);
                Vector3 lowPos = EyePosition + _gunRot * (rifleHandOffset + new Vector3(0.02f, -0.28f, -0.12f));
                rot = Quaternion.Slerp(low, rot, e);
                triggerPos = Vector3.Lerp(lowPos, triggerPos, e);
            }
            g.root.rotation = rot;
            g.root.position += triggerPos - g.trigger.position;

            Vector3 gFwd = rot * Vector3.forward, gUp = rot * Vector3.up, gRight = rot * Vector3.right;

            // ---- right hand: on the trigger, swinging down with the lever ----
            Vector3 palmR = g.trigger.position;
            Quaternion leverRot = Quaternion.identity;
            if (g.fulcrum != null && g.LeverAngle != 0f)
            {
                leverRot = Quaternion.AngleAxis(g.LeverAngle, g.fulcrum.forward);
                palmR = g.fulcrum.position + leverRot * (palmR - g.fulcrum.position);
            }
            // the hand follows the lever loop down and forward
            palmR += (-gUp * 0.09f + gFwd * 0.03f) * lever;
            Vector3 dirR = (leverRot * (rot * handGripDir)).normalized;

            // ---- left hand: forend, or feeding rounds into the loading gate ----
            Vector3 support = g.support != null
                ? g.support.position
                : g.trigger.position + gFwd * rifleSupportDistance - gUp * 0.03f;
            Vector3 dirL = (rot * supportGripDir).normalized;
            if (rl > 0f)
            {
                // thumb one round in per reloadTime into the gate on the (now down-facing) right side:
                // the hand drops away to fetch a round, then pushes it up and in
                float roundTime = _pc != null && _pc.Weapon != null ? _pc.Weapon.ReloadTime : def.reloadTime; // Swiftloader
                float phase = Mathf.Repeat(Time.time / Mathf.Max(0.05f, roundTime), 1f);
                float push = Mathf.Sin(phase * Mathf.PI);
                Vector3 gate = g.trigger.position + gFwd * 0.07f + gRight * (0.04f + 0.03f * (1f - push))
                               - gUp * (0.03f + 0.08f * (1f - push));
                support = Vector3.Lerp(support, gate, rl);
                // fingers point forward and up into the gate
                dirL = Vector3.Slerp(dirL, (rot * new Vector3(-0.35f, 0.45f, 0.8f)).normalized, rl);
            }

            SolveGunArm(_armR, palmR - dirR * palmDistance, dirR, fRight, fUp, fFwd);
            SolveGunArm(_armL, support - dirL * palmDistance, dirL, fRight, fUp, fFwd);
        }

        /// <summary>
        /// Two-handed sword. Rest: blade up and forward on the right. Swings (WeaponController.SwingProgress):
        /// wind up, sweep across in a tilted arc (right→left high-to-low, then left→right), recover to rest.
        /// Draw: right hand pulls it from over the right shoulder, over the head, down to rest; the left hand joins at the end.
        /// </summary>
        void PoseSword(WeaponController w, WeaponController.GunVisual g, Vector3 fRight, Vector3 fUp, Vector3 fFwd)
        {
            Quaternion V = _gunRot;
            Vector3 readyPos = EyePosition + V * swordIdleOffset;
            Quaternion readyRot = V * Quaternion.LookRotation(swordIdleDir.normalized, Vector3.forward);
            Vector3 pos = readyPos;
            Quaternion rot = readyRot;

            // ---- swing ----
            float s = w.SwingProgress;
            if (s < 1f)
            {
                int dir = w.SwingDirection;
                float sign = dir == 0 ? 1f : -1f;           // right→left starts on the right (+angle)
                float a0 = sign * swingArc, a1 = -sign * swingArc;
                const float windEnd = 0.18f, sweepEnd = 0.6f;
                if (s < windEnd)
                {
                    float u = Mathf.SmoothStep(0, 1, s / windEnd);
                    ArcPose(dir, a0 + sign * swingWindup, out var p, out var r);
                    pos = Vector3.Lerp(readyPos, p, u);
                    rot = Quaternion.Slerp(readyRot, r, u);
                }
                else if (s < sweepEnd)
                {
                    float u = (s - windEnd) / (sweepEnd - windEnd);
                    u = u * u * (3f - 2f * u);
                    ArcPose(dir, Mathf.Lerp(a0 + sign * swingWindup, a1, u), out pos, out rot);
                }
                else
                {
                    float u = Mathf.SmoothStep(0, 1, (s - sweepEnd) / (1f - sweepEnd));
                    ArcPose(dir, a1, out var p, out var r);
                    pos = Vector3.Lerp(p, readyPos, u);
                    rot = Quaternion.Slerp(r, readyRot, u);
                }
            }

            // ---- draw: over the right shoulder ----
            float d = w.DrawProgress;
            float leftGrip = 1f;
            if (d < 1f)
            {
                Vector3 sheathPos = _chest.position + fUp * 0.32f + fRight * 0.14f - fFwd * 0.16f;
                // sheathed: blade runs diagonally down the back
                Quaternion sheathRot = Quaternion.LookRotation((-fUp * 0.85f - fFwd * 0.25f - fRight * 0.3f).normalized, -fFwd);
                // half way: handle over the head, blade pointing back
                Vector3 overHead = _chest.position + fUp * 0.62f + fRight * 0.1f + fFwd * 0.12f;
                Quaternion backRot = Quaternion.LookRotation((-fFwd * 0.8f + fUp * 0.45f).normalized, fUp);
                const float reachEnd = 0.25f;
                if (d < reachEnd)
                {
                    // hand goes back to the grip; the sword stays put
                    pos = sheathPos;
                    rot = sheathRot;
                }
                else
                {
                    float u = (d - reachEnd) / (1f - reachEnd);
                    float e = 1f - (1f - u) * (1f - u) * (1f - u);
                    pos = Bezier(sheathPos, overHead, pos, e);
                    rot = e < 0.5f ? Quaternion.Slerp(sheathRot, backRot, e * 2f) : Quaternion.Slerp(backRot, rot, (e - 0.5f) * 2f);
                }
                leftGrip = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.65f, 0.95f, d));
            }

            g.root.rotation = rot;
            g.root.position += pos - g.trigger.position;

            // ---- hands: both on the grip, right above left ----
            Vector3 blade = rot * Vector3.forward;
            // fingers point forward from the wrist and wrap around the grip (kept square to the blade)
            Vector3 dirGrip = Vector3.ProjectOnPlane(_gunRot * swordHandDir, blade);
            dirGrip = dirGrip.sqrMagnitude > 0.01f ? dirGrip.normalized : (rot * Vector3.up);
            Vector3 palmR = g.trigger.position + blade * swordHandGap;
            Vector3 palmL = g.trigger.position - blade * swordHandGap;
            SolveGunArm(_armR, palmR - dirGrip * palmDistance, dirGrip, fRight, fUp, fFwd);

            Vector3 restL = _armL.upper.position - fUp * 0.5f + fFwd * 0.08f;
            Vector3 wristL = Vector3.Lerp(restL, palmL - dirGrip * palmDistance, leftGrip);
            Vector3 dirL = Vector3.Slerp(-fUp, dirGrip, leftGrip).normalized;
            SolveGunArm(_armL, wristL, dirL, fRight, fUp, fFwd);

        }

        /// <summary>Handle position and sword rotation at an angle along the swing arc (eye space, tilted per swing).</summary>
        void ArcPose(int dir, float angle, out Vector3 pos, out Quaternion rot)
        {
            Quaternion tilt = Quaternion.AngleAxis(dir == 0 ? WeaponController.SwingTilt : -WeaponController.SwingTilt, Vector3.forward);
            float r = angle * Mathf.Deg2Rad;
            Vector3 blade = tilt * new Vector3(Mathf.Sin(r), 0.12f, Mathf.Cos(r)).normalized;
            // the edge leads: it faces the way the blade is moving
            Vector3 tangent = new Vector3(Mathf.Cos(r), 0f, -Mathf.Sin(r)); // d(blade)/d(angle)
            Vector3 motion = tilt * (dir == 0 ? -tangent : tangent);
            Vector3 handle = swordSwingCentre + tilt * new Vector3(Mathf.Sin(r) * 0.28f, 0f, (Mathf.Cos(r) - 1f) * 0.12f);
            pos = EyePosition + _gunRot * handle;
            rot = _gunRot * Quaternion.LookRotation(blade, motion);
        }

        static Vector3 Bezier(Vector3 a, Vector3 c, Vector3 b, float t)
        {
            float it = 1f - t;
            return it * it * a + 2f * it * t * c + t * t * b;
        }

        void SolveGunArm(Limb arm, Vector3 wristTarget, Vector3 handDir, Vector3 fRight, Vector3 fUp, Vector3 fFwd)
        {
            // reach the shoulder toward the gun a little
            Vector3 clavDir = arm.upper.position - arm.root.position;
            Vector3 toTarget = wristTarget - arm.root.position;
            Vector3 aim = Vector3.Slerp(clavDir.normalized, toTarget.normalized, clavicleReach) * clavDir.magnitude;
            Aim(arm.root, arm.upper.position, arm.root.position + aim);

            Vector3 pole = arm.upper.position + (-fUp * 0.6f + fRight * (arm.side * 0.4f) - fFwd * 0.1f); // elbow down & out
            TwoBone(arm.upper, arm.lower, arm.end, wristTarget, pole);
            Aim(arm.end, arm.tip.position, arm.end.position + handDir);
        }

        void SolveFreeArm(Limb arm, Vector3 fRight, Vector3 fUp, Vector3 fFwd)
        {
            Vector3 shoulder = arm.upper.position;
            // swing opposite to the same-side leg
            float swing = Mathf.Sin(_phase + (arm.side > 0 ? 0f : Mathf.PI)) * freeArmSwing * _moveW;
            Vector3 target = shoulder
                             - fUp * 0.5f
                             + fRight * (arm.side * 0.08f)
                             + fFwd * (0.04f + swing)
                             + (fRight * (arm.side * 0.18f) + fUp * 0.12f) * _airW      // balance in the air
                             + (fRight * (arm.side * 0.25f) + fUp * 0.2f) * _slideW;    // and when sliding
            Vector3 pole = shoulder - fFwd * 0.6f - fUp * 0.3f + fRight * (arm.side * 0.1f); // elbow points back
            TwoBone(arm.upper, arm.lower, arm.end, target, pole);
            Aim(arm.end, arm.tip.position, arm.end.position + (-fUp + fFwd * 0.15f).normalized);
        }

        void SolveLeg(Limb leg, ref Vector3 smoothed, float phaseOffset, float dt, Vector3 origin, Quaternion frame,
            Vector3 moveDir, float speed, bool grounded, bool sliding)
        {
            float side = leg.side;
            Vector3 fFwd = frame * Vector3.forward, fUp = frame * Vector3.up, fRight = frame * Vector3.right;
            Vector3 local;

            // ground gait
            float ph = _phase + phaseOffset;
            float stride = Mathf.Clamp(speed * 0.045f, 0f, maxStride) * _moveW;
            Vector3 g = new Vector3(side * stanceWidth, groundY, side * -0.03f);
            g += moveDir * (Mathf.Sin(ph) * stride);
            g.y += Mathf.Max(0f, Mathf.Cos(ph)) * (stepHeight * Mathf.Clamp01(stride * 4f) + stride * 0.3f);

            // airborne tuck
            float vy = _pc.ViewVelocity.y;
            float tuck = Mathf.Clamp01(1f - vy / 10f) * 0.1f;
            Vector3 a = new Vector3(side * (stanceWidth + 0.03f), groundY + 0.28f - tuck, side < 0 ? 0.18f : -0.12f);

            // slide: right leg forward, left leg tucked
            Vector3 sd = moveDir;
            Vector3 s = side > 0
                ? new Vector3(side * 0.12f, groundY, 0) + sd * 0.8f
                : new Vector3(side * 0.28f, groundY, 0) + sd * 0.2f;

            local = Vector3.Lerp(g, a, _airW);
            local = Vector3.Lerp(local, s, _slideW);

            Vector3 world = origin + frame * local;

            // plant on uneven ground
            if (grounded && !sliding && Physics.Raycast(world + fUp * 0.5f, -fUp, out var hit, 0.9f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
            {
                float lift = local.y - groundY;
                world = hit.point + fUp * (0.04f + Mathf.Max(0, lift));
            }

            float sk = 1f - Mathf.Exp(-30f * dt);
            if (smoothed == Vector3.zero || (smoothed - world).sqrMagnitude > 4f) smoothed = world;
            else smoothed = Vector3.Lerp(smoothed, world, sk);

            Vector3 knee = side < 0 && _slideW > 0.5f
                ? leg.upper.position + fUp * 0.6f + fRight * side * 0.6f
                : leg.upper.position + fFwd * 1f + fRight * side * 0.12f;
            TwoBone(leg.upper, leg.lower, leg.end, smoothed, knee);

            Vector3 footFwd = Vector3.ProjectOnPlane(fFwd, fUp).normalized;
            Vector3 toes = footFwd * 0.9f - fUp * 0.25f + (sliding && side > 0 ? fUp * 0.9f : Vector3.zero);
            Aim(leg.end, leg.tip.position, leg.end.position + toes);
        }

        // ---------------- IK helpers ----------------

        /// <summary>Rotate a bone so the point 'childPoint' (attached to it) points at target.</summary>
        static void Aim(Transform bone, Vector3 childPoint, Vector3 target)
        {
            Vector3 from = childPoint - bone.position;
            Vector3 to = target - bone.position;
            if (from.sqrMagnitude < 1e-8f || to.sqrMagnitude < 1e-8f) return;
            bone.rotation = Quaternion.FromToRotation(from, to) * bone.rotation;
        }

        static void TwoBone(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
        {
            Vector3 root = upper.position;
            float a = Vector3.Distance(root, lower.position);
            float b = Vector3.Distance(lower.position, end.position);
            Vector3 toT = target - root;
            float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(a - b) + 0.001f, a + b - 0.001f);
            Vector3 dir = toT.sqrMagnitude > 1e-8f ? toT.normalized : Vector3.down;

            float cosA = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            Vector3 bend = Vector3.ProjectOnPlane(pole - root, dir);
            if (bend.sqrMagnitude < 1e-6f) bend = Vector3.ProjectOnPlane(Vector3.forward, dir);
            bend.Normalize();

            Vector3 knee = root + dir * (a * cosA) + bend * (a * sinA);
            Aim(upper, lower.position, knee);
            Aim(lower, end.position, root + dir * d);
        }
    }
}
