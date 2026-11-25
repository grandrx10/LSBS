using UnityEngine;

namespace LSBS
{
    /// <summary>
    /// The tank, to the outline of Models/Tank.prefab: hull between two long track pods, a sponson on either side, and the
    /// gun off to the right of the nose. Ground at y 0, its middle at the origin, front along z.
    /// </summary>
    public class TankModel
    {
        /// <summary>The prefab's units to metres.</summary>
        public const float Scale = 0.7f;
        // the outline, in metres
        public const float HullX = 1.59f, HullY0 = 0.65f, HullY1 = 2.86f, HullZ = 3.02f;
        public const float TrackIn = 1.57f, TrackOut = 2.39f, TrackTop = 2.21f, TrackZ = 3.92f;
        public const float SponsonIn = 2.35f, SponsonOut = 3.2f, SponsonY0 = 0.75f, SponsonY1 = 1.97f, SponsonZ0 = -0.76f, SponsonZ1 = 1.07f;
        public static readonly Vector3 Cannon = new(0.77f, 1.87f, 3.0f);
        public const float CannonLength = 2.05f;
        public static Vector3 SideGun(int side) => new((side == 0 ? -1f : 1f) * 3.0f, 1.5f, 0.3f);
        public const int Phases = 8;
        const float Link = 0.36f;

        public readonly Mesh Hull, Gun;
        public readonly Mesh[] Track = new Mesh[Phases]; // one pod's belt, at each stage of going round
        public static readonly Vector3[] Exhausts = { new(-0.8f, 3.75f, -1.9f), new(0.8f, 3.75f, -1.9f) };

        static Color C(int hex) => MeshBuilder.Col(hex);
        static Color Metal(int hex) => MeshBuilder.Col(hex, MeshBuilder.Metal);

        public TankModel(int team)
        {
            Color paint = team == 0 ? C(0x525a34) : C(0x5a6268), dark = team == 0 ? C(0x3b4126) : C(0x41474c);
            Color iron = Metal(0x3a3837), rust = Metal(0x5c3a26), brass = Metal(0x8f6a30), copper = Metal(0x6e452c), black = C(0x161514), cloth = team == 0 ? C(0x7f9a3c) : C(0xb4bcc6);
            var mb = new MeshBuilder();

            // ---- hull: a deep box with its nose and tail raked, plated
            Prism(mb, new[]
            {
                new Vector2(-HullZ, HullY0 + 0.35f), new Vector2(-HullZ, HullY1 - 0.55f), new Vector2(-HullZ + 0.5f, HullY1 - 0.12f), new Vector2(HullZ - 1.0f, HullY1 - 0.12f),
                new Vector2(HullZ, HullY1 - 0.75f), new Vector2(HullZ, HullY0 + 0.55f), new Vector2(HullZ - 0.7f, HullY0), new Vector2(-HullZ + 0.6f, HullY0),
            }, -HullX, HullX, paint);
            // the raised cabin, and its slits
            mb.Box(new Vector3(0, HullY1 + 0.02f, 0.9f), new Vector3(1.9f, 0.5f, 1.5f), Quaternion.identity, paint, 0.88f);
            mb.Box(new Vector3(-0.59f, 1.97f, HullZ + 0.02f), new Vector3(1.08f, 0.2f, 0.06f), Quaternion.Euler(0, 0, 0), dark);
            mb.Box(new Vector3(-0.59f, 1.97f, HullZ + 0.05f), new Vector3(0.9f, 0.07f, 0.04f), black);
            mb.Box(new Vector3(0, HullY1 + 0.06f, 1.63f), new Vector3(1.0f, 0.08f, 0.1f), black);
            for (int s = -1; s <= 1; s += 2) mb.Box(new Vector3(s * 0.93f, HullY1 + 0.06f, 0.9f), new Vector3(0.1f, 0.08f, 0.7f), black);
            // hatches
            mb.Tube(new Vector3(0, HullY1 + 0.26f, 0.75f), new Vector3(0, HullY1 + 0.36f, 0.75f), 0.36f, 0.34f, 12, dark);
            mb.Tube(new Vector3(0, HullY1 + 0.36f, 0.75f), new Vector3(0, HullY1 + 0.40f, 0.75f), 0.05f, 0.05f, 6, brass);
            mb.Box(new Vector3(0, 1.7f, -HullZ - 0.03f), new Vector3(0.9f, 1.1f, 0.06f), dark);
            mb.Box(new Vector3(0.3f, 1.7f, -HullZ - 0.08f), new Vector3(0.06f, 0.24f, 0.06f), brass);
            // seams and rivets
            foreach (float z in new[] { -2.1f, -0.9f, 0.3f, 1.5f })
            {
                mb.Box(new Vector3(0, HullY1 - 0.11f, z), new Vector3(HullX * 2f, 0.04f, 0.09f), dark);
                for (int i = -5; i <= 5; i++) Rivet(mb, new Vector3(i * 0.28f, HullY1 - 0.08f, z), Vector3.up, iron);
            }
            for (int i = -4; i <= 4; i++)
                foreach (float y in new[] { 1.2f, 2.0f }) Rivet(mb, new Vector3(i * 0.34f, y - 0.0f, -HullZ), Vector3.back, iron);
            for (int i = -4; i <= 4; i++) Rivet(mb, new Vector3(i * 0.34f, 1.25f, HullZ), Vector3.forward, iron);

            // ---- the works: a boiler along the back, pipes, two funnels
            mb.Tube(new Vector3(0, HullY1 + 0.12f, -2.7f), new Vector3(0, HullY1 + 0.12f, -0.35f), 0.48f, 0.48f, 14, copper);
            mb.Tube(new Vector3(0, HullY1 + 0.12f, -2.85f), new Vector3(0, HullY1 + 0.12f, -2.7f), 0.36f, 0.48f, 14, iron);
            foreach (float z in new[] { -2.4f, -1.6f, -0.8f }) mb.Tube(new Vector3(0, HullY1 + 0.12f, z), new Vector3(0, HullY1 + 0.12f, z + 0.12f), 0.51f, 0.51f, 14, brass);
            foreach (var e in Exhausts)
            {
                var foot = new Vector3(e.x, HullY1 - 0.2f, e.z);
                mb.Tube(foot, e, 0.15f, 0.12f, 10, iron);
                mb.Tube(e - Vector3.up * 0.14f, e, 0.17f, 0.17f, 10, rust);
                mb.Tube(foot + new Vector3(0, 0.25f, 0), new Vector3(0, HullY1 + 0.12f, e.z), 0.08f, 0.08f, 8, brass);
            }
            mb.Tube(new Vector3(0.55f, HullY1 + 0.0f, -0.2f), new Vector3(0.9f, HullY1 + 0.0f, 0.2f), 0.06f, 0.06f, 6, brass);
            mb.Tube(new Vector3(-0.3f, HullY1 + 0.62f, -1.2f), new Vector3(-0.3f, HullY1 + 0.75f, -1.2f), 0.09f, 0.07f, 8, brass);

            // ---- track pods: flat-sided lozenges with rounded ends, wheels showing through the plates
            for (int s = -1; s <= 1; s += 2)
            {
                float a = s * TrackIn, b = s * (TrackOut - 0.03f);
                Prism(mb, PodProfile(0.10f), Mathf.Min(a, b), Mathf.Max(a, b), paint);
                // plates down the outside, with their rivets
                float x = s * (TrackOut - 0.02f);
                for (int i = -3; i <= 3; i++)
                {
                    mb.Box(new Vector3(x, 1.1f, i * 0.92f), new Vector3(0.05f, 1.25f, 0.06f), dark);
                    for (int k = 0; k < 4; k++) Rivet(mb, new Vector3(x + s * 0.02f, 0.62f + k * 0.32f, i * 0.92f), Vector3.right * s, iron);
                }
                for (int i = -2; i <= 2; i++)
                {
                    mb.Tube(new Vector3(x - s * 0.05f, 0.42f, i * 1.15f), new Vector3(x + s * 0.04f, 0.42f, i * 1.15f), 0.27f, 0.27f, 12, iron);
                    mb.Tube(new Vector3(x, 0.42f, i * 1.15f), new Vector3(x + s * 0.08f, 0.42f, i * 1.15f), 0.08f, 0.08f, 8, brass);
                }
                // the driving gear at the back, the idler in front
                foreach (float z in new[] { -TrackZ + 0.95f, TrackZ - 0.95f })
                {
                    mb.Tube(new Vector3(x - s * 0.05f, 1.1f, z), new Vector3(x + s * 0.06f, 1.1f, z), 0.5f, 0.5f, 16, rust);
                    mb.Tube(new Vector3(x, 1.1f, z), new Vector3(x + s * 0.1f, 1.1f, z), 0.16f, 0.16f, 10, brass);
                    for (int k = 0; k < 6; k++)
                    {
                        float t = k * 60f * Mathf.Deg2Rad;
                        mb.Box(new Vector3(x + s * 0.06f, 1.1f + Mathf.Sin(t) * 0.3f, z + Mathf.Cos(t) * 0.3f), new Vector3(0.05f, 0.1f, 0.1f), iron);
                    }
                }
                // its side's colours
                mb.Box(new Vector3(x + s * 0.012f, 1.75f, -1.4f), new Vector3(0.03f, 0.42f, 0.9f), cloth);

                // ---- sponsons
                float si = s * SponsonIn, so = s * SponsonOut;
                float mid = (SponsonZ0 + SponsonZ1) * 0.5f, len = SponsonZ1 - SponsonZ0, high = SponsonY1 - SponsonY0;
                mb.Box(new Vector3((si + so) * 0.5f, SponsonY0 + high * 0.5f, mid), new Vector3(Mathf.Abs(so - si), high, len), paint);
                mb.Box(new Vector3((si + so) * 0.5f, SponsonY1 + 0.04f, mid), new Vector3(Mathf.Abs(so - si) + 0.08f, 0.08f, len + 0.08f), dark);
                // the round shield its gun swings in
                var gun = SideGun(s < 0 ? 0 : 1);
                mb.Tube(new Vector3(gun.x, SponsonY0 + 0.1f, gun.z), new Vector3(gun.x, SponsonY1 - 0.05f, gun.z), 0.46f, 0.46f, 14, dark);
                mb.Tube(new Vector3(gun.x, SponsonY0 + 0.06f, gun.z), new Vector3(gun.x, SponsonY0 + 0.14f, gun.z), 0.49f, 0.49f, 14, brass);
                mb.Tube(new Vector3(gun.x, SponsonY1 - 0.13f, gun.z), new Vector3(gun.x, SponsonY1 - 0.05f, gun.z), 0.49f, 0.49f, 14, brass);
                for (int k = 0; k < 5; k++) Rivet(mb, new Vector3(so, SponsonY0 + 0.2f, SponsonZ0 + 0.2f + k * 0.32f), Vector3.right * s, iron);
            }
            // the collar the gun stands out of
            mb.Tube(Cannon - Vector3.forward * 0.1f, Cannon + Vector3.forward * 0.28f, 0.62f, 0.5f, 16, dark);
            mb.Tube(Cannon + Vector3.forward * 0.2f, Cannon + Vector3.forward * 0.3f, 0.53f, 0.53f, 16, brass);
            for (int k = 0; k < 10; k++)
            {
                float t = k * 36f * Mathf.Deg2Rad;
                Rivet(mb, Cannon + new Vector3(Mathf.Cos(t) * 0.56f, Mathf.Sin(t) * 0.56f, 0.12f), Vector3.forward, iron);
            }
            // tow hooks, lamps
            for (int s = -1; s <= 1; s += 2)
            {
                mb.Box(new Vector3(s * 0.9f, 0.95f, HullZ + 0.08f), new Vector3(0.14f, 0.18f, 0.2f), iron);
                mb.Tube(new Vector3(s * 1.2f, HullY1 - 0.3f, HullZ - 0.45f), new Vector3(s * 1.2f, HullY1 - 0.32f, HullZ - 0.2f), 0.13f, 0.17f, 10, brass);
            }
            Hull = mb.Build("Tank hull");

            // ---- the gun, about its mount
            var g = new MeshBuilder();
            g.Tube(new Vector3(0, 0, -0.1f), new Vector3(0, 0, 0.55f), 0.36f, 0.30f, 14, iron);
            g.Tube(new Vector3(0, 0, 0.5f), new Vector3(0, 0, CannonLength), 0.24f, 0.20f, 14, iron);
            g.Tube(new Vector3(0, 0, 0.55f), new Vector3(0, 0, 0.68f), 0.31f, 0.31f, 14, brass);
            g.Tube(new Vector3(0, 0, CannonLength - 0.32f), new Vector3(0, 0, CannonLength), 0.27f, 0.27f, 14, iron);
            g.Tube(new Vector3(0, 0, CannonLength), new Vector3(0, 0, CannonLength + 0.01f), 0.16f, 0.16f, 12, black);
            g.Tube(new Vector3(0, 0.3f, 0.3f), new Vector3(0, 0.3f, 1.1f), 0.07f, 0.07f, 8, copper);
            Gun = g.Build("Tank gun");

            // ---- the belts
            for (int p = 0; p < Phases; p++)
            {
                var t = new MeshBuilder();
                var line = PodProfile(0f);
                float total = 0f;
                for (int i = 0; i < line.Length; i++) total += Vector2.Distance(line[i], line[(i + 1) % line.Length]);
                int links = Mathf.RoundToInt(total / Link);
                float step = total / links;
                for (int i = 0; i < links; i++)
                {
                    At(line, (i + (float)p / Phases) * step, out var at, out var dir);
                    var q = Quaternion.LookRotation(new Vector3(0, dir.y, dir.x), new Vector3(0, dir.x, -dir.y));
                    t.Box(new Vector3(0, at.y, at.x), new Vector3(TrackOut - TrackIn + 0.06f, 0.07f, step * 0.86f), q, rust);
                    t.Box(new Vector3(0, at.y, at.x) + q * new Vector3(0, 0.05f, 0), new Vector3(TrackOut - TrackIn + 0.06f, 0.07f, step * 0.25f), q, iron);
                }
                Track[p] = t.Build("Tank track");
            }
        }

        /// <summary>The side of a pod, (z, y), going round it. Inset pulls it in from the belt's line.</summary>
        static Vector2[] PodProfile(float inset)
        {
            float z = TrackZ - inset, y0 = 0.05f + inset, y1 = TrackTop - inset;
            return new[]
            {
                new Vector2(-z + 1.35f, y0), new Vector2(-z + 0.55f, y0 + 0.38f), new Vector2(-z + 0.08f, y0 + 0.9f), new Vector2(-z + 0.08f, y1 - 0.75f), new Vector2(-z + 0.45f, y1 - 0.2f), new Vector2(-z + 1.0f, y1),
                new Vector2(z - 1.0f, y1), new Vector2(z - 0.4f, y1 - 0.2f), new Vector2(z, y1 - 0.75f), new Vector2(z - 0.05f, y0 + 1.0f), new Vector2(z - 0.55f, y0 + 0.4f), new Vector2(z - 1.35f, y0),
            };
        }

        static void At(Vector2[] line, float d, out Vector2 at, out Vector2 dir)
        {
            for (int i = 0; ; i = (i + 1) % line.Length)
            {
                Vector2 a = line[i], b = line[(i + 1) % line.Length];
                float len = Vector2.Distance(a, b);
                if (d <= len) { dir = (b - a) / len; at = a + dir * d; return; }
                d -= len;
            }
        }

        /// <summary>A shape in (z, y), drawn out along x. Corners run clockwise seen from +x... whichever way, both faces are drawn right.</summary>
        static void Prism(MeshBuilder mb, Vector2[] p, float x0, float x1, Color col)
        {
            Vector2 mid = Vector2.zero;
            foreach (var v in p) mid += v;
            mid /= p.Length;
            float area = 0f;
            for (int i = 0; i < p.Length; i++) { var a = p[i]; var b = p[(i + 1) % p.Length]; area += a.x * b.y - b.x * a.y; }
            for (int i = 0; i < p.Length; i++)
            {
                Vector2 a = p[i], b = p[(i + 1) % p.Length];
                if (area > 0f) (a, b) = (b, a);
                Vector3 a0 = new(x0, a.y, a.x), b0 = new(x0, b.y, b.x), a1 = new(x1, a.y, a.x), b1 = new(x1, b.y, b.x);
                mb.Quad(a0, b0, b1, a1, col);
                mb.Tri(new Vector3(x1, mid.y, mid.x), a1, b1, col);
                mb.Tri(new Vector3(x0, mid.y, mid.x), b0, a0, col);
            }
        }

        static void Rivet(MeshBuilder mb, Vector3 at, Vector3 outward, Color col) => mb.Tube(at, at + outward * 0.025f, 0.035f, 0.022f, 5, col);
    }
}
