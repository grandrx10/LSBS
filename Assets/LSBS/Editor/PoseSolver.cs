using System.Collections.Generic;
using UnityEngine;

namespace LSBS.EditorTools
{
    /// <summary>
    /// Gunbound's procedural animation (hips, gait, two-handed rifle, two-bone IK) without the player attached, so its
    /// poses can be baked. The rifle kneel is Sudden Mage Encounter's.
    /// </summary>
    public class PoseSolver
    {
        class Limb { public Transform root, upper, lower, end, tip; public float side; }

        public struct Pose
        {
            public float phase, move;         // gait, 0..1 how hard
            public float stride, drop, lean, kneel;
            public Vector3 gunEuler;          // added to the aim (x up and down, y side, z roll)
            public Vector3 gunOffset;         // from the rifle's usual place by the eye
            public float lever;               // 0 shut .. 1 thrown open
            public float pump;                // 0 forward .. 1 racked back
            public float reload;              // 0..1 canted for loading
            public float feed;                // 0..1 thumbing a round in
            public float headDown, twist;
            public float breath;
            public float rightW, leftW;       // a hand off the gun
            public Vector3 rightAt, leftAt;   // where it is instead, from the eye
            public Vector3 rightDir, leftDir;
            public bool absolute;             // those hands are placed from the hips' rest, not the eye
            public float pistol;
            // standing at something that swings: the feet stay where they were while the rest goes round it
            public float about;               // how far in front it is
            public float footL, footR;        // degrees each foot's been left behind
            public float liftL, liftR;
        }

        const float GroundY = -0.92f, StanceWidth = 0.13f, StepHeight = 0.12f, Palm = 0.08f, EyeHeight = 0.865f, PumpTravel = 0.15f;
        static readonly Vector3 HandDir = new(0f, -0.55f, 0.85f), SupportDir = new(0.5f, -0.45f, 0.75f);
        static readonly Vector3 RifleHand = new(0.11f, -0.16f, 0.31f);
        const float CockPitch = 16f, CockRoll = 18f, LeverAngle = 80f;
        static readonly Vector3 CockOffset = new(-0.02f, -0.05f, -0.04f);

        readonly Transform _model, _rig, _pelvis, _chest, _head;
        readonly Limb _armL, _armR, _legL, _legR;
        readonly List<(Transform t, Quaternion r, Vector3 p)> _bind = new();
        readonly Vector3 _support, _fulcrum;

        /// <summary>Where the rifle's trigger is, and which way it points, after posing; its lever; a shotgun's pump.</summary>
        public Matrix4x4 Rifle, LeverMatrix, PumpMatrix;

        public PoseSolver(Transform model, Vector3 support, Vector3 fulcrum)
        {
            _model = model;
            _support = support;
            _fulcrum = fulcrum;
            var b = new Dictionary<string, Transform>();
            foreach (var t in model.GetComponentsInChildren<Transform>(true)) b.TryAdd(t.name, t);
            _rig = b["both rig"]; _pelvis = b["Bone"]; _chest = b["Bone.003"]; _head = b["Bone.001"];
            Limb L(string n, float side) => new Limb { root = b[n], upper = b[n + ".001"], lower = b[n + ".002"], end = b[n + ".003"], tip = b[n + ".003_end"], side = side };
            _armL = L("Arm L", -1); _armR = L("Arm R", 1); _legL = L("Leg L", -1); _legR = L("Leg R", 1);
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
                if (t != model) _bind.Add((t, t.localRotation, t.localPosition));
        }

        public void ToBind()
        {
            foreach (var (t, r, p) in _bind) { t.localRotation = r; t.localPosition = p; }
        }

        public void Apply(Pose s)
        {
            ToBind();
            Vector3 origin = _model.position;
            Vector3 up = Vector3.up, fwd = Vector3.forward, right = Vector3.right;
            float bob = (Mathf.Cos(s.phase * 2f) * 0.5f - 0.5f) * 0.05f * s.move;

            _rig.position += up * (bob - s.drop + s.breath * 0.006f);
            _pelvis.rotation = Quaternion.AngleAxis(s.lean, right) * _pelvis.rotation;
            // the run swings the shoulders a little
            float sway = Mathf.Sin(s.phase) * 7f * s.move + s.twist;
            _chest.rotation = Quaternion.AngleAxis(sway, up) * Quaternion.AngleAxis(-s.lean * 0.3f + s.breath * 0.6f, right) * _chest.rotation;
            _head.rotation = Quaternion.AngleAxis(-sway, up) * Quaternion.AngleAxis(-s.lean * 0.5f + s.headDown, right) * _head.rotation;

            if (s.about > 0f)
            {
                float mean = (s.footL + s.footR) * 0.5f;
                Vector3 drawn = Round(Vector3.zero, s.about, mean) * 0.55f;
                _rig.position += drawn;
                _pelvis.rotation = Quaternion.AngleAxis(mean * 0.5f, up) * _pelvis.rotation;
                _chest.rotation = Quaternion.AngleAxis(-mean * 0.4f, up) * _chest.rotation;
            }

            // the eyes follow the lean, so the rifle stays at the shoulder
            Vector3 pelvisAt = _pelvis.position;
            Vector3 eye = origin + up * (EyeHeight - s.drop + bob) + fwd * 0.07f;
            eye = pelvisAt + Quaternion.AngleAxis(s.lean * 0.6f, right) * (eye - pelvisAt);

            float cock = Mathf.Sin(s.lever * Mathf.PI * 0.5f);
            Quaternion rot = Quaternion.Euler(s.gunEuler) * Quaternion.Euler(-CockPitch * cock - 18f * s.reload + 4f * s.pump, 0, CockRoll * cock - 35f * s.reload);
            Vector3 trigger = eye + RifleHand + s.gunOffset + CockOffset * cock + new Vector3(-0.05f, -0.07f, -0.08f) * s.reload;
            Rifle = Matrix4x4.TRS(trigger, rot, Vector3.one);
            // barrel along z here, so the lever turns about x
            Quaternion leverRot = Quaternion.AngleAxis(-LeverAngle * s.lever, Vector3.right);
            LeverMatrix = Rifle * Matrix4x4.Translate(_fulcrum) * Matrix4x4.Rotate(leverRot) * Matrix4x4.Translate(-_fulcrum);
            PumpMatrix = Rifle * Matrix4x4.Translate(new Vector3(0, 0, -PumpTravel * s.pump));

            Vector3 gUp = rot * Vector3.up, gFwd = rot * Vector3.forward, gRight = rot * Vector3.right;
            Vector3 palmR = trigger + (-gUp * 0.13f + gFwd * 0.04f) * s.lever;
            Vector3 dirR = (rot * HandDir).normalized;
            Vector3 support = Rifle.MultiplyPoint3x4(_support) - gFwd * (PumpTravel * s.pump);
            Vector3 dirL = (rot * SupportDir).normalized;
            // no further along the barrel than the arm goes
            float arm = (Vector3.Distance(_armL.upper.position, _armL.lower.position) + Vector3.Distance(_armL.lower.position, _armL.end.position)) * 0.97f;
            for (int i = 0; i < 40 && Vector3.Distance(support - dirL * Palm, _armL.upper.position) > arm; i++) support -= gFwd * 0.01f;
            if (s.reload > 0f)
            {
                float push = s.feed;
                Vector3 gate = trigger + gFwd * 0.10f + gRight * (0.05f + 0.03f * (1f - push)) - gUp * (0.03f + 0.08f * (1f - push));
                support = Vector3.Lerp(support, gate, s.reload);
                dirL = Vector3.Slerp(dirL, (rot * new Vector3(-0.35f, 0.45f, 0.8f)).normalized, s.reload);
            }
            if (s.pistol > 0f)
            {
                // the other hand cups the first
                support = Vector3.Lerp(support, trigger + gFwd * 0.02f - gUp * 0.075f - gRight * 0.03f, s.pistol);
                dirL = Vector3.Slerp(dirL, (rot * new Vector3(0.55f, -0.25f, 0.8f)).normalized, s.pistol);
            }
            if (s.absolute)
            {
                palmR = Vector3.Lerp(palmR, origin + s.rightAt, s.rightW); dirR = Vector3.Slerp(dirR, s.rightDir.normalized, s.rightW);
                support = Vector3.Lerp(support, origin + s.leftAt, s.leftW); dirL = Vector3.Slerp(dirL, s.leftDir.normalized, s.leftW);
            }
            else if (s.rightW > 0f) { palmR = Vector3.Lerp(palmR, eye + s.rightAt, s.rightW); dirR = Vector3.Slerp(dirR, s.rightDir.normalized, s.rightW); }
            if (!s.absolute && s.leftW > 0f) { support = Vector3.Lerp(support, eye + s.leftAt, s.leftW); dirL = Vector3.Slerp(dirL, s.leftDir.normalized, s.leftW); }
            Arm(_armR, palmR - dirR * Palm, dirR, right, up, fwd);
            Arm(_armL, support - dirL * Palm, dirL, right, up, fwd);
            Leg(_legL, 0f, s, origin, up, fwd, right);
            Leg(_legR, Mathf.PI, s, origin, up, fwd, right);
        }

        /// <summary>A place (x, z) taken round something that far in front.</summary>
        static Vector3 Round(Vector3 p, float about, float degrees)
        {
            Vector3 c = new(0, 0, about);
            return c + Quaternion.AngleAxis(degrees, Vector3.up) * (p - c);
        }

        static void Arm(Limb arm, Vector3 wrist, Vector3 handDir, Vector3 right, Vector3 up, Vector3 fwd)
        {
            Vector3 clav = arm.upper.position - arm.root.position;
            Vector3 to = wrist - arm.root.position;
            Vector3 aim = Vector3.Slerp(clav.normalized, to.normalized, 0.15f) * clav.magnitude;
            Aim(arm.root, arm.upper.position, arm.root.position + aim);
            Vector3 pole = arm.upper.position + (-up * 0.6f + right * (arm.side * 0.4f) - fwd * 0.1f);
            TwoBone(arm.upper, arm.lower, arm.end, wrist, pole);
            Aim(arm.end, arm.tip.position, arm.end.position + handDir);
        }

        static void Leg(Limb leg, float offset, Pose s, Vector3 origin, Vector3 up, Vector3 fwd, Vector3 right)
        {
            float ph = s.phase + offset;
            float stride = s.stride * s.move;
            Vector3 g = new(leg.side * StanceWidth, GroundY, leg.side * -0.03f);
            g += Vector3.forward * (Mathf.Sin(ph) * stride);
            g.y += Mathf.Max(0f, Mathf.Cos(ph)) * (StepHeight * Mathf.Clamp01(stride * 4f) + stride * 0.3f);
            g += new Vector3(leg.side * 0.02f, 0.02f, leg.side > 0 ? -0.44f : 0.2f) * s.kneel;
            Vector3 ahead = fwd;
            if (s.about > 0f)
            {
                float left = leg.side < 0 ? s.footL : s.footR;
                g = Round(g, s.about, left);
                g.y += leg.side < 0 ? s.liftL : s.liftR;
                ahead = Quaternion.AngleAxis(left, up) * fwd;
            }
            Vector3 knee = leg.upper.position + ahead + right * (leg.side * 0.12f);
            TwoBone(leg.upper, leg.lower, leg.end, origin + g, knee);
            Aim(leg.end, leg.tip.position, leg.end.position + ahead * 0.9f - up * 0.25f);
        }

        static void Aim(Transform bone, Vector3 child, Vector3 target)
        {
            Vector3 from = child - bone.position, to = target - bone.position;
            if (from.sqrMagnitude < 1e-8f || to.sqrMagnitude < 1e-8f) return;
            bone.rotation = Quaternion.FromToRotation(from, to) * bone.rotation;
        }

        static void TwoBone(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
        {
            Vector3 root = upper.position;
            float a = Vector3.Distance(root, lower.position), b = Vector3.Distance(lower.position, end.position);
            Vector3 toT = target - root;
            float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(a - b) + 0.001f, a + b - 0.001f);
            Vector3 dir = toT.sqrMagnitude > 1e-8f ? toT.normalized : Vector3.down;
            float cosA = Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            Vector3 bend = Vector3.ProjectOnPlane(pole - root, dir);
            if (bend.sqrMagnitude < 1e-6f) bend = Vector3.ProjectOnPlane(Vector3.forward, dir);
            bend.Normalize();
            Aim(upper, lower.position, root + dir * (a * cosA) + bend * (a * sinA));
            Aim(lower, end.position, root + dir * d);
        }
    }
}
