using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LSBS.EditorTools
{
    /// <summary>
    /// The shapes the kits start out with. Greens: a khaki infantryman in a soup-plate helmet, everything rounded. Greys: a
    /// field-grey one in a spiked helmet, everything square. One mask for both.
    /// </summary>
    public static partial class SoldierBaker
    {
        static Color Matte(int hex) => MeshBuilder.Col(hex);
        static Color Metal(int hex) => MeshBuilder.Col(hex, MeshBuilder.Metal);

        // shared by name with Kit/Materials
        static readonly Dictionary<string, Color> Palette = new()
        {
            ["Brass"] = Metal(0x8f6f38), ["Spike"] = Matte(0x8a6c3a), ["Iron"] = Metal(0x3a3a3a), ["Steel"] = Metal(0x5c5e60), ["Leather"] = Matte(0x4d3320), ["Dark"] = Matte(0x1d1c1a),
            ["Leader Strip"] = Matte(0xd8a53a), ["Radio"] = Matte(0x454a3a), ["Radio Dark"] = Matte(0x26281f), ["Dial"] = Matte(0xd8cfb0),
            ["Mask Cloth"] = Matte(0x7b8070), ["Mask Seam"] = Matte(0x5f6455), ["Mask Rubber"] = Matte(0x23271f), ["Mask Canister"] = Metal(0x3a443a), ["Mask Lens"] = Matte(0xb4b9b4),

            ["Green Coat"] = Matte(0x5e6236), ["Green Trousers"] = Matte(0x5a5832), ["Green Puttees"] = Matte(0x6f6b45), ["Green Boots"] = Matte(0x4b3826), ["Green Gloves"] = Matte(0x8a6650), ["Green Head"] = Matte(0x8a6650),
            ["Green Helmet"] = Metal(0x585c36), ["Green Webbing"] = Matte(0x8d8559), ["Green Pack"] = Matte(0x5b5434), ["Green Pack Roll"] = Matte(0x716a45), ["Green Satchel"] = Matte(0x7b7346),

            ["Grey Coat"] = Matte(0x5f6b70), ["Grey Trousers"] = Matte(0x4b5458), ["Grey Puttees"] = Matte(0x3f2b1c), ["Grey Boots"] = Matte(0x36251a), ["Grey Gloves"] = Matte(0x8a6650), ["Grey Head"] = Matte(0x8a6650),
            ["Grey Helmet"] = Matte(0x6d6a4e), ["Grey Piping"] = Matte(0x6e2a1f), ["Grey Leather"] = Matte(0x523a26), ["Grey Pack"] = Matte(0x54462f), ["Grey Blanket"] = Matte(0x7d6b4b), ["Grey Bag"] = Matte(0x6e6a55),
        };

        static Color P(string name) => Palette[name];

        struct Fit { public Bounds head, chest, hips; public System.Func<string, Vector3> joint; }

        /// <summary>Everything a side wears, group by group.</summary>
        static Dictionary<string, MeshBuilder> Outfit(int team, Fit f)
        {
            var o = new Dictionary<string, MeshBuilder>();
            MeshBuilder G(string group) => o.TryGetValue(group, out var mb) ? mb : o[group] = new MeshBuilder();
            if (team == Team.Green) Green(G, f); else Grey(G, f);
            Radio(G("Back (communicator)"), f, team);
            // the leader's strip
            Vector3 a = f.joint("Arm L.001"), b = f.joint("Arm L.002");
            G("Upper Arm L (captain)").SmoothTube(Vector3.Lerp(a, b, 0.42f), Vector3.Lerp(a, b, 0.62f), 0.081f, 0.077f, 14, P("Leader Strip"), false);
            return o;
        }

        // ------------------------------------------------------------------ greens

        static void Green(System.Func<string, MeshBuilder> G, Fit f)
        {
            Color coat = P("Green Coat"), web = P("Green Webbing"), leather = P("Leather"), brass = P("Brass");
            float cz = f.chest.center.z;

            // ---- helmet: a shallow rounded bowl, and a wide brim that rolls down and out ----
            var h = G("Helmet");
            float R = 0.133f;
            Vector3 hc = new(0, f.head.max.y - 0.055f, f.head.center.z);
            h.SmoothLathe(hc, new[]
            {
                new Vector2(R * 1.70f, -R * 0.36f), new Vector2(R * 1.73f, -R * 0.31f), new Vector2(R * 1.50f, -R * 0.17f), new Vector2(R * 1.22f, -R * 0.04f), new Vector2(R * 1.08f, R * 0.10f),
                new Vector2(R * 1.03f, R * 0.34f), new Vector2(R * 0.93f, R * 0.62f), new Vector2(R * 0.72f, R * 0.86f), new Vector2(R * 0.4f, R * 1.0f), new Vector2(0f, R * 1.05f),
            }, 24, P("Green Helmet"), 1.12f);
            h.SmoothLathe(hc, new[] { new Vector2(R * 0.9f, -R * 0.02f), new Vector2(R * 1.3f, -R * 0.14f), new Vector2(R * 1.70f, -R * 0.36f) }, 24, P("Dark"), 1.12f);
            for (int s = -1; s <= 1; s += 2)
                h.SmoothTube(hc + new Vector3(s * R * 1.0f, -R * 0.02f, 0.02f), new Vector3(s * 0.06f, f.head.min.y + 0.02f, f.head.center.z + 0.05f), 0.009f, 0.009f, 6, leather);

            // ---- tunic ----
            var c = G("Chest");
            var tunic = new[]
            {
                new Vector2(0.200f, 0.345f), new Vector2(0.213f, 0.44f), new Vector2(0.222f, 0.55f), new Vector2(0.212f, 0.63f), new Vector2(0.165f, 0.69f), new Vector2(0.09f, 0.725f), new Vector2(0.07f, 0.73f),
            };
            c.SmoothLathe(new Vector3(0, 0, cz), tunic, 24, coat, 0.70f);
            // a stand collar
            c.SmoothLathe(new Vector3(0, 0, cz - 0.005f), new[] { new Vector2(0.082f, 0.705f), new Vector2(0.079f, 0.775f), new Vector2(0.066f, 0.778f) }, 14, P("Green Trousers"), 1.0f);
            float front = cz + 0.222f * 0.70f;
            for (int i = 0; i < 4; i++) c.Ball(new Vector3(0, 0.40f + i * 0.085f, front - 0.002f - (i == 3 ? 0.012f : 0)), new Vector3(0.013f, 0.013f, 0.008f), brass, 8, 5);
            for (int s = -1; s <= 1; s += 2)
            {
                // breast pockets, full
                c.Ball(new Vector3(s * 0.105f, 0.545f, front - 0.028f), new Vector3(0.062f, 0.062f, 0.026f), coat * new Color(0.9f, 0.9f, 0.9f, 1), 10, 6);
                c.Ball(new Vector3(s * 0.105f, 0.575f, front - 0.012f), new Vector3(0.01f, 0.01f, 0.007f), brass, 6, 4);
                // crossed straps, and the pack's straps over the shoulders
                Across(c, tunic, cz, 0.70f, new Vector2(-s * 0.185f, 0.36f), new Vector2(s * 0.125f, 0.655f), 0.020f, web);
                c.Strap(new[] { new Vector3(s * 0.122f, 0.652f, cz + 0.118f), new Vector3(s * 0.128f, 0.708f, cz + 0.055f), new Vector3(s * 0.130f, 0.724f, cz - 0.01f), new Vector3(s * 0.128f, 0.700f, cz - 0.09f), new Vector3(s * 0.120f, 0.63f, cz - 0.15f) }, 0.020f, 0.010f, web);
                // shoulders
                c.Ball(new Vector3(s * 0.185f, 0.655f, cz), new Vector3(0.075f, 0.062f, 0.085f), coat, 12, 8);
            }

            var hips = G("Hips");
            var skirt = new[]
            {
                new Vector2(0.235f, -0.115f), new Vector2(0.243f, -0.11f), new Vector2(0.225f, 0.02f), new Vector2(0.205f, 0.14f), new Vector2(0.193f, 0.24f), new Vector2(0.196f, 0.33f), new Vector2(0.205f, 0.40f),
            };
            float hz = f.hips.center.z - 0.005f;
            hips.SmoothLathe(new Vector3(0, 0, hz), skirt, 24, coat, 0.74f);
            hips.SmoothLathe(new Vector3(0, 0, hz), new[] { new Vector2(0.203f, 0.235f), new Vector2(0.208f, 0.245f), new Vector2(0.208f, 0.275f), new Vector2(0.203f, 0.285f) }, 18, web, 0.76f);
            hips.Ball(new Vector3(0, 0.26f, hz + 0.16f), new Vector3(0.028f, 0.024f, 0.01f), brass, 10, 6);
            // rounded pouches all round the belt
            foreach (float deg in new[] { 28f, 58f, -28f, -58f, 100f })
            {
                float t = deg * Mathf.Deg2Rad;
                hips.Ball(new Vector3(Mathf.Sin(t) * 0.218f, 0.248f, hz + Mathf.Cos(t) * 0.166f), new Vector3(0.042f, 0.048f, 0.042f), web * new Color(0.88f, 0.88f, 0.88f, 1), 10, 7);
            }
            hips.Ball(new Vector3(0.10f, 0.12f, hz + 0.145f), new Vector3(0.055f, 0.06f, 0.02f), coat * new Color(0.9f, 0.9f, 0.9f, 1), 10, 6);
            hips.Ball(new Vector3(-0.10f, 0.12f, hz + 0.145f), new Vector3(0.055f, 0.06f, 0.02f), coat * new Color(0.9f, 0.9f, 0.9f, 1), 10, 6);
            // the big round satchel on the left hip, and a canteen on the right
            Vector3 bag = new(-0.255f, 0.10f, hz - 0.03f);
            hips.Ball(bag, new Vector3(0.062f, 0.125f, 0.115f), P("Green Satchel"), 14, 10);
            hips.Ball(bag + new Vector3(-0.012f, 0.045f, 0), new Vector3(0.060f, 0.092f, 0.120f), P("Green Satchel") * new Color(0.82f, 0.82f, 0.82f, 1), 14, 9);
            hips.Ball(bag + new Vector3(-0.068f, 0.0f, 0.01f), new Vector3(0.007f, 0.012f, 0.012f), brass, 6, 4);
            hips.Strap(new[] { bag + new Vector3(0.03f, 0.10f, 0.02f), new Vector3(-0.212f, 0.26f, hz + 0.02f) }, 0.018f, 0.010f, web);
            hips.Ball(new Vector3(0.245f, 0.13f, hz - 0.05f), new Vector3(0.046f, 0.085f, 0.075f), P("Green Pack Roll"), 12, 8);
            hips.SmoothTube(new Vector3(0.245f, 0.20f, hz - 0.05f), new Vector3(0.245f, 0.235f, hz - 0.05f), 0.018f, 0.016f, 8, P("Iron"));

            // ---- pack: a long round roll down the back, another across the top ----
            var k = G("Back");
            float back = cz - 0.222f * 0.70f;
            Vector3 p0 = new(0, 0.30f, back - 0.085f), p1 = new(0, 0.66f, back - 0.075f);
            k.Capsule(p0, p1, 0.118f, 16, P("Green Pack"));
            k.Capsule(new Vector3(-0.115f, 0.735f, back - 0.06f), new Vector3(0.115f, 0.735f, back - 0.06f), 0.068f, 14, P("Green Pack Roll"));
            k.Ball(new Vector3(0, 0.655f, back - 0.14f), new Vector3(0.105f, 0.085f, 0.075f), P("Green Pack Roll"), 12, 8);
            k.Ball(new Vector3(0, 0.215f, back - 0.085f), new Vector3(0.105f, 0.075f, 0.1f), P("Green Pack Roll"), 12, 8);
            foreach (float y in new[] { 0.33f, 0.45f, 0.57f }) k.Ring(new Vector3(0, y, back - 0.082f), 0.121f, 0.011f, 16, web);
            for (int s = -1; s <= 1; s += 2) k.Ring(new Vector3(s * 0.07f, 0.735f, back - 0.06f), 0.070f, 0.010f, 14, web, Quaternion.Euler(0, 0, 90));
            // bayonet down the left side
            k.Capsule(new Vector3(-0.145f, 0.36f, back - 0.05f), new Vector3(-0.135f, 0.60f, back - 0.05f), 0.02f, 8, leather);
            k.SmoothTube(new Vector3(-0.135f, 0.61f, back - 0.05f), new Vector3(-0.130f, 0.70f, back - 0.05f), 0.016f, 0.018f, 8, P("Leather"));
            k.SmoothTube(new Vector3(-0.155f, 0.61f, back - 0.05f), new Vector3(-0.110f, 0.61f, back - 0.05f), 0.009f, 0.009f, 6, P("Steel"));
            k.Ball(new Vector3(-0.130f, 0.71f, back - 0.05f), new Vector3(0.018f, 0.014f, 0.018f), P("Steel"), 8, 5);

            // ---- sleeves, breeches, puttees, boots ----
            foreach (string s in new[] { "L", "R" })
            {
                Vector3 sh = f.joint($"Arm {s}.001"), el = f.joint($"Arm {s}.002"), wr = f.joint($"Arm {s}.003");
                G($"Upper Arm {s}").Limb(sh, el, new[] { 0.068f, 0.075f, 0.070f, 0.063f }, 12, coat);
                G($"Forearm {s}").Ball(el, Vector3.one * 0.060f, coat, 10, 7);
                G($"Forearm {s}").Limb(el, wr, new[] { 0.062f, 0.062f, 0.055f, 0.051f }, 12, coat);
                G($"Forearm {s}").SmoothTube(Vector3.Lerp(el, wr, 0.86f), wr, 0.057f, 0.057f, 12, coat * new Color(0.8f, 0.8f, 0.8f, 1));
                Vector3 hip = f.joint($"Leg {s}.001"), kn = f.joint($"Leg {s}.002"), an = f.joint($"Leg {s}.003"), toe = f.joint($"Leg {s}.003_end");
                G($"Thigh {s}").Limb(hip, kn, new[] { 0.112f, 0.128f, 0.122f, 0.098f, 0.084f }, 12, P("Green Trousers"));
                // breeches bag out over the tops of the puttees, wound round and round
                var shin = G($"Shin {s}");
                shin.Limb(kn, Vector3.Lerp(kn, an, 0.18f), new[] { 0.088f, 0.094f, 0.080f }, 12, P("Green Trousers"));
                for (int i = 0; i < 7; i++)
                {
                    float t0 = 0.16f + i * 0.112f, t1 = t0 + 0.122f;
                    float r = Mathf.Lerp(0.083f, 0.056f, i / 6f) + (i == 1 ? 0.006f : 0f);
                    shin.SmoothTube(Vector3.Lerp(kn, an, t0), Vector3.Lerp(kn, an, Mathf.Min(1f, t1)), r + 0.006f, r - 0.003f, 12, P("Green Puttees") * new Color(i % 2 == 0 ? 1f : 0.86f, i % 2 == 0 ? 1f : 0.86f, i % 2 == 0 ? 1f : 0.86f, 1));
                }
                var foot = G($"Foot {s}");
                foot.Capsule(an + (toe - an) * 0.05f, an + (toe - an) * 0.95f, 0.056f, 12, P("Green Boots"));
                foot.Ball(an + (toe - an) * 0.95f, new Vector3(0.058f, 0.045f, 0.06f), P("Green Boots"), 10, 7);
                foot.SmoothTube(an + Vector3.up * 0.05f, an - Vector3.up * 0.02f, 0.06f, 0.062f, 12, P("Green Boots"));
            }
        }

        /// <summary>A strap laid over the front of a turned shape, from one place (x, y) to another.</summary>
        static void Across(MeshBuilder mb, Vector2[] profile, float cz, float stretch, Vector2 from, Vector2 to, float halfWidth, Color col)
        {
            const int n = 7;
            var pts = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 p = Vector2.Lerp(from, to, i / (n - 1f));
                float r = profile[profile.Length - 1].x;
                for (int k = 0; k + 1 < profile.Length; k++)
                    if (p.y >= profile[k].y && p.y <= profile[k + 1].y) r = Mathf.Lerp(profile[k].x, profile[k + 1].x, Mathf.InverseLerp(profile[k].y, profile[k + 1].y, p.y));
                float z = r * stretch * Mathf.Sqrt(Mathf.Max(0.02f, 1f - p.x * p.x / (r * r)));
                pts[i] = new Vector3(p.x, p.y, cz + z + 0.004f);
            }
            mb.Strap(pts, halfWidth, 0.009f, col);
        }

        // ------------------------------------------------------------------ greys

        static void Grey(System.Func<string, MeshBuilder> G, Fit f)
        {
            Color coat = P("Grey Coat"), red = P("Grey Piping"), leather = P("Grey Leather"), brass = P("Brass");
            float cz = f.chest.center.z;
            // squared-off rather than round
            Vector2 Square(float t, int ring)
            {
                float k = Mathf.Pow(Mathf.Pow(Mathf.Abs(Mathf.Sin(t)), 4f) + Mathf.Pow(Mathf.Abs(Mathf.Cos(t)), 4f), -0.25f);
                return new Vector2(k - 1f, 0f);
            }
            void SquareLathe(MeshBuilder mb, Vector3 at, Vector2[] profile, Color col, float stretch, int sides = 12) =>
                mb.Lathe(at, profile, sides, col, stretch, (t, ring) => new Vector2(profile[ring].x * Square(t, ring).x * 0.8f, 0));

            // ---- helmet: tall, spiked, in its cloth cover ----
            var h = G("Helmet");
            float R = 0.128f;
            Vector3 hc = new(0, f.head.max.y - 0.075f, f.head.center.z);
            h.Lathe(hc, new[]
            {
                new Vector2(R * 1.16f, -R * 0.34f), new Vector2(R * 1.13f, R * 0.04f), new Vector2(R * 1.1f, R * 0.42f), new Vector2(R * 0.96f, R * 0.86f), new Vector2(R * 0.66f, R * 1.16f), new Vector2(R * 0.26f, R * 1.3f), new Vector2(0, R * 1.31f),
            }, 12, P("Grey Helmet"), 1.12f);
            // peak in front, tail behind
            h.Box(hc + new Vector3(0, -R * 0.36f, R * 1.36f), new Vector3(R * 1.7f, 0.014f, R * 0.62f), Quaternion.Euler(24f, 0, 0), P("Grey Helmet") * new Color(0.75f, 0.75f, 0.75f, 1));
            h.Box(hc + new Vector3(0, -R * 0.5f, -R * 1.28f), new Vector3(R * 1.5f, 0.014f, R * 0.55f), Quaternion.Euler(-34f, 0, 0), P("Grey Helmet") * new Color(0.75f, 0.75f, 0.75f, 1));
                        // a square plate where the number goes
            h.Box(hc + new Vector3(0, R * 0.32f, R * 1.25f), new Vector3(0.07f, 0.05f, 0.012f), Quaternion.Euler(-9f, 0, 0), red);
            h.Box(hc + new Vector3(0, R * 1.33f, 0), new Vector3(0.075f, 0.018f, 0.075f), brass);
            h.Box(hc + new Vector3(0, R * 1.45f, 0), new Vector3(0.05f, 0.03f, 0.05f), P("Spike"));
            h.Tube(hc + new Vector3(0, R * 1.5f, 0), hc + new Vector3(0, R * 2.0f, 0), 0.021f, 0.013f, 6, P("Spike"));
            h.Ball(hc + new Vector3(0, R * 2.0f, 0), Vector3.one * 0.0135f, P("Spike"), 10, 7);
            for (int s = -1; s <= 1; s += 2) h.Box(hc + new Vector3(s * R * 1.12f, -R * 0.75f, 0.03f), new Vector3(0.012f, R * 0.95f, 0.022f), Quaternion.Euler(0, 0, s * 8f), leather);

            // ---- tunic ----
            var c = G("Chest");
            SquareLathe(c, new Vector3(0, 0, cz), new[] { new Vector2(0.185f, 0.345f), new Vector2(0.196f, 0.45f), new Vector2(0.200f, 0.60f), new Vector2(0.175f, 0.68f), new Vector2(0.08f, 0.715f) }, coat, 0.70f);
            SquareLathe(c, new Vector3(0, 0, cz - 0.005f), new[] { new Vector2(0.076f, 0.70f), new Vector2(0.074f, 0.765f), new Vector2(0.062f, 0.766f) }, coat * new Color(0.85f, 0.85f, 0.85f, 1), 1f, 8);
            SquareLathe(c, new Vector3(0, 0, cz - 0.005f), new[] { new Vector2(0.078f, 0.755f), new Vector2(0.078f, 0.768f) }, red, 1f, 8);
            float front = cz + 0.202f * 0.70f;
            c.Box(new Vector3(0.016f, 0.515f, front + 0.004f), new Vector3(0.010f, 0.36f, 0.012f), red);
            for (int i = 0; i < 5; i++) c.Box(new Vector3(-0.004f, 0.375f + i * 0.072f, front + 0.008f), new Vector3(0.022f, 0.022f, 0.012f), brass);
            for (int s = -1; s <= 1; s += 2)
            {
                // shoulder boards, edged
                c.Box(new Vector3(s * 0.155f, 0.705f, cz), new Vector3(0.12f, 0.014f, 0.075f), Quaternion.Euler(0, 0, -s * 16f), coat * new Color(0.8f, 0.8f, 0.8f, 1));
                c.Box(new Vector3(s * 0.155f, 0.700f, cz), new Vector3(0.13f, 0.010f, 0.086f), Quaternion.Euler(0, 0, -s * 16f), red);
                c.Box(new Vector3(s * 0.195f, 0.645f, cz), new Vector3(0.10f, 0.11f, 0.14f), Quaternion.Euler(0, 0, -s * 10f), coat);
                // pack straps, straight down the front
                c.Box(new Vector3(s * 0.105f, 0.53f, front + 0.004f), new Vector3(0.036f, 0.36f, 0.016f), leather);
                c.Box(new Vector3(s * 0.105f, 0.715f, cz), new Vector3(0.036f, 0.016f, 0.26f), leather);
                c.Box(new Vector3(s * 0.105f, 0.50f, front + 0.014f), new Vector3(0.044f, 0.032f, 0.008f), brass);
            }

            var hips = G("Hips");
            float hz = f.hips.center.z - 0.005f;
            SquareLathe(hips, new Vector3(0, 0, hz), new[] { new Vector2(0.222f, -0.125f), new Vector2(0.205f, 0.02f), new Vector2(0.186f, 0.16f), new Vector2(0.180f, 0.27f), new Vector2(0.186f, 0.39f) }, coat, 0.74f);
            SquareLathe(hips, new Vector3(0, 0, hz), new[] { new Vector2(0.226f, -0.127f), new Vector2(0.223f, -0.112f) }, red, 0.74f);
            hips.Box(new Vector3(0.016f, 0.07f, hz + 0.166f), new Vector3(0.010f, 0.36f, 0.012f), Quaternion.Euler(-4f, 0, 0), red);
            SquareLathe(hips, new Vector3(0, 0, hz), new[] { new Vector2(0.189f, 0.232f), new Vector2(0.192f, 0.236f), new Vector2(0.192f, 0.284f), new Vector2(0.189f, 0.288f) }, leather, 0.78f);
            hips.Box(new Vector3(0, 0.26f, hz + 0.158f), new Vector3(0.068f, 0.056f, 0.016f), brass);
            for (int s = -1; s <= 1; s += 2)
            {
                // three square cartridge boxes a side
                for (int i = 0; i < 3; i++)
                {
                    Vector3 at = new(s * (0.072f + i * 0.058f), 0.25f, hz + 0.172f - i * i * 0.016f);
                    var q = Quaternion.Euler(0, s * i * 17f, 0);
                    hips.Box(at, new Vector3(0.052f, 0.082f, 0.05f), q, leather);
                    hips.Box(at + q * new Vector3(0, 0.026f, 0.004f), new Vector3(0.056f, 0.034f, 0.052f), q, leather * new Color(0.8f, 0.8f, 0.8f, 1));
                    hips.Box(at + q * new Vector3(0, 0.006f, 0.03f), new Vector3(0.012f, 0.012f, 0.008f), q, brass);
                }
                hips.Box(new Vector3(s * 0.11f, 0.09f, hz + 0.158f), new Vector3(0.095f, 0.032f, 0.014f), coat * new Color(0.82f, 0.82f, 0.82f, 1));
            }
            hips.Box(new Vector3(0.245f, 0.10f, hz - 0.05f), new Vector3(0.07f, 0.17f, 0.19f), P("Grey Bag"));
            hips.Box(new Vector3(0.25f, 0.15f, hz - 0.05f), new Vector3(0.076f, 0.08f, 0.196f), P("Grey Bag") * new Color(0.85f, 0.85f, 0.85f, 1));
            hips.Box(new Vector3(-0.235f, 0.10f, hz - 0.03f), new Vector3(0.034f, 0.32f, 0.05f), leather);
            hips.Box(new Vector3(-0.235f, 0.30f, hz - 0.03f), new Vector3(0.03f, 0.10f, 0.04f), P("Dark"));

            // ---- pack: a box, with the blanket squared round it and a mess tin on the flap ----
            var k = G("Back");
            float back = cz - 0.202f * 0.70f;
            Vector3 b = new(0, 0.50f, back - 0.085f);
            k.Box(b, new Vector3(0.32f, 0.36f, 0.15f), P("Grey Pack"));
            k.Box(b + new Vector3(0, 0.05f, -0.082f), new Vector3(0.33f, 0.27f, 0.018f), P("Grey Pack") * new Color(0.8f, 0.8f, 0.8f, 1));
            k.Box(b + new Vector3(0, 0.215f, 0), new Vector3(0.44f, 0.075f, 0.135f), P("Grey Blanket"));
            for (int s = -1; s <= 1; s += 2)
            {
                k.Box(b + new Vector3(s * 0.192f, 0.02f, 0), new Vector3(0.07f, 0.32f, 0.13f), P("Grey Blanket"));
                k.Box(b + new Vector3(s * 0.09f, 0.02f, -0.094f), new Vector3(0.024f, 0.40f, 0.012f), leather);
                k.Box(b + new Vector3(s * 0.09f, 0.215f, 0), new Vector3(0.026f, 0.084f, 0.146f), leather);
            }
            k.Box(b + new Vector3(0, -0.03f, -0.115f), new Vector3(0.15f, 0.16f, 0.06f), P("Iron"));
            k.Box(b + new Vector3(0, 0.03f, -0.118f), new Vector3(0.156f, 0.035f, 0.066f), P("Steel"));

            // ---- sleeves, trousers, boots ----
            foreach (string s in new[] { "L", "R" })
            {
                Vector3 sh = f.joint($"Arm {s}.001"), el = f.joint($"Arm {s}.002"), wr = f.joint($"Arm {s}.003");
                G($"Upper Arm {s}").Tube(sh, el, 0.078f, 0.068f, 6, coat);
                G($"Forearm {s}").Tube(el, wr, 0.067f, 0.057f, 6, coat);
                G($"Forearm {s}").Tube(Vector3.Lerp(el, wr, 0.70f), Vector3.Lerp(el, wr, 0.99f), 0.066f, 0.064f, 6, coat * new Color(0.82f, 0.82f, 0.82f, 1));
                G($"Forearm {s}").Tube(Vector3.Lerp(el, wr, 0.67f), Vector3.Lerp(el, wr, 0.71f), 0.069f, 0.069f, 6, red);
                Vector3 hip = f.joint($"Leg {s}.001"), kn = f.joint($"Leg {s}.002"), an = f.joint($"Leg {s}.003"), toe = f.joint($"Leg {s}.003_end");
                G($"Thigh {s}").Tube(hip, kn, 0.122f, 0.092f, 6, P("Grey Trousers"));
                var shin = G($"Shin {s}");
                shin.Tube(kn, Vector3.Lerp(kn, an, 0.36f), 0.092f, 0.088f, 6, P("Grey Trousers"));
                shin.Ball(kn, Vector3.one * 0.094f, P("Grey Trousers"), 10, 7);
                G($"Forearm {s}").Ball(el, Vector3.one * 0.064f, coat, 10, 7);
                // high, square-topped boots
                shin.Tube(Vector3.Lerp(kn, an, 0.30f), an, 0.096f, 0.072f, 6, P("Grey Boots"));
                var foot = G($"Foot {s}");
                Vector3 along = toe - an;
                var q = Quaternion.LookRotation(along.normalized, Vector3.up);
                foot.Box(an + along * 0.45f, new Vector3(0.105f, 0.085f, along.magnitude * 1.35f), q, P("Grey Boots"), 0.85f);
                foot.Box(an + along * 0.45f + q * new Vector3(0, -0.045f, 0), new Vector3(0.115f, 0.02f, along.magnitude * 1.42f), q, P("Dark"));
            }
        }

        // ------------------------------------------------------------------ both

        static void Radio(MeshBuilder mb, Fit f, int team)
        {
            // a square set on the back, its aerial standing well above the head
            float back = f.chest.center.z - 0.155f;
            Color shell = P("Radio") * (team == 0 ? Color.white : new Color(0.95f, 1.02f, 1.12f, 1)), dark = P("Radio Dark");
            shell.a = 0f;
            Vector3 b = new(0, 0.51f, back - 0.10f);
            mb.Box(b, new Vector3(0.33f, 0.44f, 0.19f), shell);
            mb.Box(b + new Vector3(0, 0.235f, 0), new Vector3(0.35f, 0.03f, 0.21f), dark);
            mb.Box(b + new Vector3(0, -0.235f, 0), new Vector3(0.35f, 0.03f, 0.21f), dark);
            mb.Box(b + new Vector3(0, 0.07f, -0.098f), new Vector3(0.27f, 0.2f, 0.012f), dark);
            for (int i = -1; i <= 1; i++) mb.Tube(b + new Vector3(i * 0.085f, 0.11f, -0.10f), b + new Vector3(i * 0.085f, 0.11f, -0.125f), 0.03f, 0.028f, 10, P("Brass"));
            mb.Box(b + new Vector3(0, 0.02f, -0.106f), new Vector3(0.2f, 0.04f, 0.008f), P("Dial"));
            for (int i = 0; i < 5; i++) mb.Box(b + new Vector3(0, -0.09f - i * 0.022f, -0.099f), new Vector3(0.22f, 0.01f, 0.014f), dark);
            // the handset on its hook, and its lead
            mb.Box(b + new Vector3(-0.19f, 0.06f, 0.01f), new Vector3(0.035f, 0.16f, 0.04f), P("Dark"));
            mb.Box(b + new Vector3(-0.195f, 0.135f, 0.02f), new Vector3(0.045f, 0.035f, 0.06f), P("Dark"));
            mb.Box(b + new Vector3(-0.195f, -0.015f, 0.02f), new Vector3(0.045f, 0.035f, 0.06f), P("Dark"));
            mb.Tube(b + new Vector3(-0.19f, -0.03f, 0.01f), b + new Vector3(-0.15f, -0.16f, -0.02f), 0.008f, 0.008f, 5, P("Dark"));
            Vector3 foot = b + new Vector3(0.13f, 0.24f, -0.05f), mid = foot + new Vector3(0.01f, 0.45f, -0.05f), top = mid + new Vector3(0.03f, 0.75f, -0.14f);
            mb.Tube(foot, foot + new Vector3(0, 0.06f, 0), 0.026f, 0.02f, 8, P("Brass"));
            mb.Tube(foot, mid, 0.011f, 0.009f, 6, P("Iron"));
            mb.Tube(mid, top, 0.008f, 0.005f, 6, P("Iron"));
            mb.Tube(mid - new Vector3(0, 0.015f, 0), mid + new Vector3(0, 0.015f, 0), 0.016f, 0.016f, 8, P("Brass"));
            mb.Ball(top, Vector3.one * 0.014f, P("Brass"), 8, 5);
            for (int s = -1; s <= 1; s += 2) mb.Box(new Vector3(s * 0.11f, 0.70f, f.chest.center.z - 0.02f), new Vector3(0.04f, 0.018f, 0.24f), P("Leather"));
        }

        /// <summary>The mask on its own: a soft leather-and-cloth face, round glass eyes, and a canister screwed in at the mouth.</summary>
        static MeshBuilder MaskModel(Bounds head)
        {
            var mb = new MeshBuilder();
            Color cloth = P("Mask Cloth"), seam = P("Mask Seam"), rubber = P("Mask Rubber"), can = P("Mask Canister");
            Vector3 c = new(0, head.center.y - 0.005f, head.center.z + 0.012f);
            // the face, fuller at the cheeks than the brow
            mb.Ball(c + new Vector3(0, 0.012f, 0.006f), new Vector3(0.117f, 0.138f, 0.126f), cloth, 18, 12);
            mb.Ball(c + new Vector3(0, -0.055f, 0.035f), new Vector3(0.098f, 0.088f, 0.105f), cloth, 16, 10);
            mb.Ball(c + new Vector3(0, -0.06f, 0.085f), new Vector3(0.062f, 0.072f, 0.062f), cloth, 14, 9);
            // the seam up the middle and round the edge
            mb.Strap(new[] { c + new Vector3(0, 0.135f, 0.06f), c + new Vector3(0, 0.09f, 0.112f), c + new Vector3(0, 0.02f, 0.134f), c + new Vector3(0, -0.03f, 0.142f) }, 0.008f, 0.006f, seam);
            // eyes: a round of cloth, a black rim, pale glass with its spokes
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 n = new Vector3(s * 0.46f, 0.02f, 0.885f).normalized;
                var q = Quaternion.FromToRotation(Vector3.up, n);
                Vector3 e = c + new Vector3(s * 0.056f, 0.028f, 0.100f);
                mb.SmoothTube(e - n * 0.02f, e + n * 0.010f, 0.056f, 0.049f, 16, seam);
                mb.Ring(e + n * 0.014f, 0.037f, 0.012f, 16, rubber, q);
                mb.SmoothTube(e, e + n * 0.013f, 0.034f, 0.034f, 14, P("Mask Lens"));
                for (int i = 0; i < 6; i++)
                {
                    float a = i * 60f * Mathf.Deg2Rad;
                    Vector3 d = q * new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                    mb.SmoothTube(e + n * 0.015f + d * 0.011f, e + n * 0.015f + d * 0.032f, 0.0028f, 0.0028f, 4, rubber);
                }
                mb.Ring(e + n * 0.015f, 0.011f, 0.0028f, 10, rubber, q);
                // straps back round the head
                foreach (float y in new[] { 0.05f, -0.035f })
                {
                    float k = Mathf.Sqrt(1f - y * y / (0.138f * 0.138f)) * 1.015f;
                    var pts = new Vector3[6];
                    for (int i = 0; i < 6; i++)
                    {
                        float a = Mathf.Lerp(68f, 180f, i / 5f) * Mathf.Deg2Rad;
                        pts[i] = c + new Vector3(s * Mathf.Sin(a) * 0.117f * k, 0.012f + y, 0.006f + Mathf.Cos(a) * 0.126f * k);
                    }
                    mb.Strap(pts, 0.013f, 0.007f, seam);
                }
            }
            {
                var pts = new Vector3[7];
                for (int i = 0; i < 7; i++)
                {
                    float a = Mathf.Lerp(35f, 175f, i / 6f) * Mathf.Deg2Rad;
                    pts[i] = c + new Vector3(0, 0.012f + Mathf.Sin(a) * 0.141f, 0.006f + Mathf.Cos(a) * 0.129f);
                }
                mb.Strap(pts, 0.014f, 0.007f, seam);
            }
            // the canister: hanging forward and down, ribbed, wider at the far end
            Vector3 m = c + new Vector3(0, -0.075f, 0.135f), dir = new Vector3(0, -0.5f, 0.866f);
            var turn = Quaternion.FromToRotation(Vector3.up, dir);
            mb.SmoothTube(m - dir * 0.03f, m + dir * 0.012f, 0.047f, 0.050f, 16, rubber);
            mb.Ring(m + dir * 0.012f, 0.050f, 0.007f, 16, rubber, turn);
            mb.SmoothTube(m + dir * 0.012f, m + dir * 0.088f, 0.048f, 0.054f, 18, can);
            foreach (float t in new[] { 0.032f, 0.055f }) mb.Ring(m + dir * t, 0.051f, 0.004f, 18, can, turn);
            mb.Ring(m + dir * 0.088f, 0.054f, 0.006f, 18, can, turn);
            mb.SmoothTube(m + dir * 0.086f, m + dir * 0.089f, 0.05f, 0.05f, 16, rubber);
            return mb;
        }
    }
}
