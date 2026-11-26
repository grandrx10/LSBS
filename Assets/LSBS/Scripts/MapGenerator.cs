using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LSBS
{
    /// <summary>
    /// Makes a battlefield from a seed: rolling dust-grey ground, an objective platform with its beacon in the middle, a
    /// landing ground for each side spaced round it, and between them the blocks, walls, barriers, pylons and crates of some
    /// abandoned settlement to fight through. Also fills in the MapView the soldiers think with.
    /// </summary>
    public class MapGenerator
    {
        const int W = MapView.W, H = MapView.H;

        public MapView Map;
        /// <summary>Where steam comes out: xyz, w how much.</summary>
        public readonly List<Vector4> Chimneys = new();
        /// <summary>Glowing strips that take the colour of whoever holds the objective.</summary>
        public Vector3 Beacon;
        public readonly int Teams;

        readonly Transform _root;
        readonly Material _ground, _props;
        Random _rng;
        float[] _h;
        Color[] _c;
        float[] _propTop;
        byte[] _blocked, _rough, _roof;
        readonly Dictionary<int2, MeshBuilder> _chunks = new();
        float2[] _objectives;
        float2 _off;

        static Color C(int hex) => MeshBuilder.Col(hex);
        static Color Metal(int hex) => MeshBuilder.Col(hex, MeshBuilder.Metal);
        static Color Glow(int hex) => MeshBuilder.Col(hex, MeshBuilder.Glow);
        static readonly Color Concrete = C(0x6d6f72), ConcreteDark = C(0x4a4c50), Panel = Metal(0x8a9098), PanelDark = Metal(0x3d434b),
            Steel = Metal(0x5b6169), Trim = Metal(0xb8bec6), Light = Glow(0x7fe6ff), Warn = Glow(0xffb03a), Crate = C(0x5a5f55);

        /// <summary>How far each side's landing ground is from the middle.</summary>
        public const float HomeRadius = 150f;

        public MapGenerator(Transform root, Material ground, Material props, uint seed, int teams)
        {
            _root = root; _ground = ground; _props = props;
            Teams = math.clamp(teams, 2, Team.Max);
            _rng = new Random(seed * 2654435761u | 1u);
        }

        // ------------------------------------------------------------------ the shape of the land

        static float Fbm(float2 p) => noise.snoise(p) * 0.6f + noise.snoise(p * 2.3f + 7f) * 0.28f + noise.snoise(p * 5.1f + 19f) * 0.12f;
        static int VI(int x, int z) => z * (W + 1) + x;
        static float2 World(int x, int z) => new(x - MapView.HalfW, z - MapView.HalfH);

        float Height(float2 p)
        {
            float2 g = math.clamp(p + new float2(MapView.HalfW, MapView.HalfH), 0f, new float2(W - 0.01f, H - 0.01f));
            int2 c = (int2)g;
            float2 f = g - c;
            return math.lerp(math.lerp(_h[VI(c.x, c.y)], _h[VI(c.x + 1, c.y)], f.x), math.lerp(_h[VI(c.x, c.y + 1)], _h[VI(c.x + 1, c.y + 1)], f.x), f.y);
        }

        public void Generate()
        {
            _h = new float[(W + 1) * (H + 1)];
            _c = new Color[(W + 1) * (H + 1)];
            _propTop = new float[W * H];
            _blocked = new byte[W * H];
            _rough = new byte[W * H];
            _roof = new byte[W * H];
            for (int i = 0; i < _propTop.Length; i++) _propTop[i] = -1000f;
            // the objective in the middle; the landing grounds evenly round it, the whole turned a random way
            Front.Count = 1 + Teams;
            _objectives = new float2[Front.Count];
            float turn = _rng.NextFloat(0f, math.PI * 2f);
            for (int t = 0; t < Teams; t++)
            {
                float a = turn + t * math.PI * 2f / Teams;
                _objectives[Front.Home(t)] = new float2(math.cos(a), math.sin(a)) * HomeRadius;
            }
            float2 off = _off = _rng.NextFloat2(-500f, 500f);

            // the objective and the landing grounds are levelled off at whatever height they come at
            var level = new float[Front.Count];
            for (int o = 0; o < Front.Count; o++)
                for (int i = 0; i < 12; i++) level[o] += Lie(_objectives[o] + new float2(math.cos(i), math.sin(i)) * (4f + i), off) / 12f;
            for (int z = 0; z <= H; z++)
            for (int x = 0; x <= W; x++)
            {
                float2 p = World(x, z);
                float h = Lie(p, off);
                for (int o = 0; o < Front.Count; o++)
                {
                    bool centre = o == Front.Centre;
                    float k = math.smoothstep(centre ? 26f : 38f, centre ? 58f : 70f, math.distance(p, _objectives[o]));
                    h = math.lerp(level[o], h, k);
                }
                _h[VI(x, z)] = h;
                _c[VI(x, z)] = Soil(p);
            }

            Centre();
            for (int t = 0; t < Teams; t++) LandingGround(t);
            Settlement();
            Horizon();
            Ground();
            foreach (var kv in _chunks) MakeChunk("Props " + kv.Key, kv.Value, _props);
            Finish();
        }

        /// <summary>The shape of the country: low hills and ridges, and broad flats between.</summary>
        static float Lie(float2 p, float2 off)
        {
            float2 q = p + off;
            float big = Fbm(q * 0.0062f) * 6.5f;
            float hills = math.pow(math.max(0f, Fbm(q * 0.0125f + 31f)), 1.35f) * 9f;
            float ridge = math.pow(1f - math.abs(noise.snoise(q * 0.0085f + 63f)), 3f) * 4.2f;
            float hollow = math.pow(math.max(0f, -Fbm(q * 0.011f + 12f)), 1.5f) * 5f;
            float h = big + hills + ridge - hollow;
            float flat = math.smoothstep(-0.08f, 0.2f, noise.snoise(q * 0.0075f + 141f) + 0.1f * noise.snoise(q * 0.03f));
            float level = Fbm(q * 0.0028f + 9f) * 4.5f + 1f;
            h = math.lerp(h, level, flat * 0.9f);
            h += (Fbm(q * 0.035f) * 0.6f + noise.snoise(q * 0.22f) * 0.08f) * (1f - 0.85f * flat);
            return h + 1.5f;
        }

        void Each(float2 p, float r, System.Action<int, int, float> f)
        {
            int x0 = math.max(0, (int)(p.x - r + MapView.HalfW)), x1 = math.min(W, (int)(p.x + r + MapView.HalfW) + 1);
            int z0 = math.max(0, (int)(p.y - r + MapView.HalfH)), z1 = math.min(H, (int)(p.y + r + MapView.HalfH) + 1);
            for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                float d = math.distance(World(x, z), p);
                if (d < r) f(x, z, d);
            }
        }

        MeshBuilder Mb(float2 p)
        {
            int2 k = (int2)math.floor((p + new float2(MapView.HalfW, MapView.HalfH)) / 40f);
            if (!_chunks.TryGetValue(k, out var mb)) _chunks[k] = mb = new MeshBuilder();
            return mb;
        }

        static Vector3 V(float2 p, float y) => new(p.x, y, p.y);

        /// <summary>Solid to bullets (up to top), feet, and bodies.</summary>
        void Solid(float2 p, float2 size, float yawDeg, float bottom, float top, bool blocks = true)
        {
            float a = math.radians(yawDeg);
            float2 ux = new(math.cos(a), -math.sin(a)), uz = new(math.sin(a), math.cos(a));
            float2 half = size * 0.5f;
            float r = math.length(half) + 1f;
            int2 c0 = MapView.Cell(p - r), c1 = MapView.Cell(p + r);
            for (int z = c0.y; z <= c1.y; z++)
            for (int x = c0.x; x <= c1.x; x++)
            {
                float2 d = MapView.Centre(new int2(x, z)) - p;
                if (math.abs(math.dot(d, ux)) > half.x + 0.4f || math.abs(math.dot(d, uz)) > half.y + 0.4f) continue;
                int i = MapView.Index(new int2(x, z));
                _propTop[i] = math.max(_propTop[i], top);
                if (blocks) _blocked[i] = 1;
            }
        }

        /// <summary>How near the objective, or any landing ground, a place is (whichever's nearer).</summary>
        float Kept(float2 p)
        {
            float best = math.length(p - _objectives[Front.Centre]) - 34f;
            for (int t = 0; t < Teams; t++) best = math.min(best, math.distance(p, _objectives[Front.Home(t)]) - 62f);
            return best;
        }

        bool Clear(float2 p, float r)
        {
            int2 c0 = MapView.Cell(p - r), c1 = MapView.Cell(p + r);
            for (int z = c0.y; z <= c1.y; z++)
            for (int x = c0.x; x <= c1.x; x++)
                if (_blocked[MapView.Index(new int2(x, z))] != 0) return false;
            return true;
        }

        // ------------------------------------------------------------------ what's built on it

        /// <summary>A plain block, its edges trimmed, a line of light round it near the top.</summary>
        void Block(float2 p, float2 size, float yaw, float height, bool lit)
        {
            float y = Height(p) - 0.4f;
            // sat on its lowest corner, so nothing hangs in the air
            float a = math.radians(yaw);
            float2 ux = new(math.cos(a), -math.sin(a)), uz = new(math.sin(a), math.cos(a));
            for (int sx = -1; sx <= 1; sx += 2) for (int sz = -1; sz <= 1; sz += 2) y = math.min(y, Height(p + ux * size.x * 0.5f * sx + uz * size.y * 0.5f * sz) - 0.4f);
            var mb = Mb(p);
            var q = Quaternion.Euler(0, yaw, 0);
            float top = Height(p) + height;
            float tall = top - y;
            mb.Box(V(p, y + tall * 0.5f), new Vector3(size.x, tall, size.y), q, Color.Lerp(Concrete, ConcreteDark, _rng.NextFloat(0f, 0.7f)));
            // a cap a little in from the edge, and a band of panelling
            mb.Box(V(p, top + 0.2f), new Vector3(size.x - 0.6f, 0.4f, size.y - 0.6f), q, PanelDark);
            mb.Box(V(p, top - 1.2f), new Vector3(size.x + 0.12f, 0.9f, size.y + 0.12f), q, Panel);
            if (lit) mb.Box(V(p, top - 1.9f), new Vector3(size.x + 0.16f, 0.14f, size.y + 0.16f), q, _rng.NextFloat() < 0.8f ? Light : Warn);
            // ribs down the long sides
            bool alongX = size.x > size.y;
            float len = alongX ? size.x : size.y;
            for (float t = -len * 0.5f + 1.5f; t < len * 0.5f - 1f; t += 3f)
            {
                Vector3 at = alongX ? new Vector3(t, 0, 0) : new Vector3(0, 0, t);
                mb.Box(V(p, y + tall * 0.5f) + q * at, alongX ? new Vector3(0.35f, tall - 1.4f, size.y + 0.25f) : new Vector3(size.x + 0.25f, tall - 1.4f, 0.35f), q, Steel);
            }
            Solid(p, size, yaw, y, top);
        }

        /// <summary>A barrier waist high: something to crouch behind and fire over.</summary>
        void Barrier(float2 a, float2 b)
        {
            float len = math.distance(a, b);
            float2 d = (b - a) / math.max(0.01f, len);
            float yaw = math.degrees(math.atan2(d.x, d.y));
            for (float t = 0.8f; t < len; t += 1.6f)
            {
                float2 p = a + d * t;
                if (!Clear(p, 0.6f)) continue;
                float y = Height(p);
                var mb = Mb(p);
                var q = Quaternion.Euler(0, yaw, 0);
                mb.Box(V(p, y + 0.5f), new Vector3(0.7f, 1.3f, 1.55f), q, Concrete, 0.8f);
                mb.Box(V(p, y + 1.12f), new Vector3(0.36f, 0.06f, 1.5f), q, (_rng.NextFloat() < 0.5f ? Warn : Steel));
                Solid(p, new float2(0.7f, 1.55f), yaw, y, y + 1.15f);
            }
        }

        /// <summary>A pylon: a lattice mast with a light at the top.</summary>
        void Pylon(float2 p, float height)
        {
            float y = Height(p) - 0.3f;
            var mb = Mb(p);
            for (int i = 0; i < 4; i++)
            {
                float a = i * math.PI * 0.5f + 0.785f;
                Vector3 foot = V(p + new float2(math.cos(a), math.sin(a)) * 1.6f, y), head = V(p + new float2(math.cos(a), math.sin(a)) * 0.35f, y + height);
                mb.Tube(foot, head, 0.14f, 0.1f, 6, Steel);
            }
            for (float h = 2f; h < height; h += 2.6f) mb.Box(V(p, y + h), new Vector3(2.8f * (1f - h / height) + 0.6f, 0.12f, 2.8f * (1f - h / height) + 0.6f), Trim);
            mb.Box(V(p, y + height + 0.3f), new Vector3(0.5f, 0.5f, 0.5f), Warn);
            Solid(p, new float2(2.6f, 2.6f), 0, y, y + height, true);
        }

        /// <summary>A stack of crates, chest high or more.</summary>
        void Crates(float2 p)
        {
            int n = _rng.NextInt(1, 4);
            for (int i = 0; i < n; i++)
            {
                float2 q = p + _rng.NextFloat2(-1.2f, 1.2f);
                if (!Clear(q, 0.9f)) continue;
                float y = Height(q), s = _rng.NextFloat(1.0f, 1.4f), yaw = _rng.NextFloat(0f, 90f);
                var mb = Mb(q);
                int stack = _rng.NextFloat() < 0.4f ? 2 : 1;
                for (int k = 0; k < stack; k++)
                {
                    mb.Box(V(q, y + s * (k + 0.5f)), new Vector3(s, s, s), Quaternion.Euler(0, yaw + k * 12f, 0), Color.Lerp(Crate, PanelDark, _rng.NextFloat(0f, 0.5f)));
                    mb.Box(V(q, y + s * (k + 0.5f)), new Vector3(s + 0.04f, 0.12f, s + 0.04f), Quaternion.Euler(0, yaw + k * 12f, 0), Trim);
                }
                Solid(q, new float2(s, s), yaw, y, y + s * stack);
            }
        }

        /// <summary>A tank of something, on its side or standing, with a vent of steam.</summary>
        void Silo(float2 p, float r, float height)
        {
            float y = Height(p) - 0.3f;
            var mb = Mb(p);
            mb.Tube(V(p, y), V(p, y + height), r, r, 18, Panel);
            mb.Tube(V(p, y + height), V(p, y + height + r * 0.4f), r, r * 0.5f, 18, PanelDark);
            for (float h = 1.5f; h < height; h += 2.5f) mb.Tube(V(p, y + h), V(p, y + h + 0.2f), r * 1.03f, r * 1.03f, 18, Steel);
            mb.Tube(V(p, y + height * 0.65f), V(p, y + height * 0.65f + 0.15f), r * 1.05f, r * 1.05f, 18, Light);
            Solid(p, new float2(r * 1.8f, r * 1.8f), 0, y, y + height);
            if (_rng.NextFloat() < 0.5f) Chimneys.Add(new Vector4(p.x, y + height + r * 0.4f + 0.5f, p.y, 0.25f));
        }

        /// <summary>The objective: a raised hexagonal platform, a beacon in the middle, broken walls round about.</summary>
        void Centre()
        {
            float2 o = _objectives[Front.Centre];
            float y = Height(o);
            var mb = Mb(o);
            // the platform, a low step up, walkable
            for (int i = 0; i < 6; i++)
            {
                float a0 = i * math.PI / 3f, a1 = (i + 1) * math.PI / 3f;
                Vector3 c = V(o, y + 0.25f), p0 = V(o + new float2(math.cos(a0), math.sin(a0)) * 16f, y + 0.25f), p1 = V(o + new float2(math.cos(a1), math.sin(a1)) * 16f, y + 0.25f);
                mb.Tri(c, p1, p0, ConcreteDark);
                mb.Quad(p0, p1, p1 - Vector3.up * 0.6f, p0 - Vector3.up * 0.6f, Steel);
                mb.Box((p0 + p1) * 0.5f + Vector3.up * 0.03f, new Vector3(0.25f, 0.06f, math.distance(new float2(p0.x, p0.z), new float2(p1.x, p1.z))), Quaternion.LookRotation(p1 - p0), Light);
            }
            Each(o, 17f, (x, z, d) => _c[VI(x, z)] = Color.Lerp(_c[VI(x, z)], C(0x3c3f44), math.saturate(1.2f - d / 17f)));
            // the beacon: a mast with a ring of light that takes the holder's colour
            mb.Tube(V(o, y), V(o, y + 1.2f), 2.2f, 1.8f, 12, PanelDark);
            mb.Tube(V(o, y + 1.2f), V(o, y + 11f), 0.35f, 0.22f, 10, Steel);
            Beacon = V(o, y + 11.5f);
            Solid(o, new float2(4f, 4f), 0, y, y + 1.2f);
            // walls and barriers in a broken ring: cover for whoever holds it, and whoever's taking it
            int segs = 12;
            for (int i = 0; i < segs; i++)
            {
                if (_rng.NextFloat() < 0.35f) continue;
                float a0 = (i + 0.15f) * math.PI * 2f / segs, a1 = (i + 0.85f) * math.PI * 2f / segs;
                float r = _rng.NextFloat(20f, 26f);
                Barrier(o + new float2(math.cos(a0), math.sin(a0)) * r, o + new float2(math.cos(a1), math.sin(a1)) * r);
            }
            for (int i = 0; i < 5; i++)
            {
                float a = _rng.NextFloat(0f, math.PI * 2f), r = _rng.NextFloat(8f, 14f);
                Crates(o + new float2(math.cos(a), math.sin(a)) * r);
            }
        }

        /// <summary>A side's landing ground: a broad pad, marked out, lights at its edge.</summary>
        void LandingGround(int team)
        {
            float2 p = _objectives[Front.Home(team)];
            float y = Height(p);
            Each(p, 46f, (x, z, d) => _c[VI(x, z)] = Color.Lerp(_c[VI(x, z)], C(0x484b4f), math.saturate((46f - d) / 6f) * 0.85f));
            var mb = Mb(p);
            for (int i = 0; i < 24; i++)
            {
                float a = i * math.PI * 2f / 24f;
                float2 q = p + new float2(math.cos(a), math.sin(a)) * 44f;
                mb.Box(V(q, Height(q) + 0.3f), new Vector3(0.5f, 0.6f, 0.5f), Quaternion.Euler(0, math.degrees(a), 0), i % 2 == 0 ? Light : Warn);
            }
            // a mast at the middle, so it's seen from afar
            mb.Tube(V(p, y), V(p, y + 7f), 0.3f, 0.18f, 8, Steel);
            mb.Box(V(p, y + 7.3f), new Vector3(1f, 0.5f, 1f), Light);
            Solid(p, new float2(1f, 1f), 0, y, y + 7f);
        }

        /// <summary>Everything between: blocks in loose streets, with barriers, crates, pylons and silos about them.</summary>
        void Settlement()
        {
            // streets run a random way; blocks sit along them
            float streets = _rng.NextFloat(0f, 90f);
            int tries = 0, placed = 0, want = 70 + Teams * 6;
            while (placed < want && tries++ < 3000)
            {
                float2 p = _rng.NextFloat2(-MapView.HalfW + 14f, MapView.HalfW - 14f);
                if (Kept(p) < 8f) continue;
                float2 size = new(_rng.NextFloat(5f, 14f), _rng.NextFloat(5f, 16f));
                float yaw = streets + (_rng.NextBool() ? 0f : 90f) + _rng.NextFloat(-4f, 4f);
                if (!Clear(p, math.length(size) * 0.5f + 6f)) continue;
                Block(p, size, yaw, _rng.NextFloat(4f, 13f), _rng.NextFloat() < 0.7f);
                placed++;
                // a barrier across its front, and crates by it
                float a = math.radians(yaw);
                float2 ux = new(math.cos(a), -math.sin(a)), uz = new(math.sin(a), math.cos(a));
                float side = _rng.NextBool() ? 1f : -1f;
                if (_rng.NextFloat() < 0.6f)
                {
                    float2 front = p + uz * side * (size.y * 0.5f + 5f);
                    Barrier(front - ux * size.x * 0.4f, front + ux * size.x * 0.4f);
                }
                if (_rng.NextFloat() < 0.5f) Crates(p + ux * side * (size.x * 0.5f + 2.5f));
            }
            // loose cover across the open ground
            for (int i = 0; i < 140 + Teams * 10; i++)
            {
                float2 p = _rng.NextFloat2(-MapView.HalfW + 10f, MapView.HalfW - 10f);
                if (Kept(p) < 0f || !Clear(p, 3f)) continue;
                float r = _rng.NextFloat();
                if (r < 0.55f)
                {
                    float a = _rng.NextFloat(0f, math.PI * 2f), len = _rng.NextFloat(4f, 11f);
                    float2 d = new(math.cos(a), math.sin(a));
                    Barrier(p - d * len * 0.5f, p + d * len * 0.5f);
                }
                else if (r < 0.85f) Crates(p);
                else if (r < 0.93f) Pylon(p, _rng.NextFloat(9f, 18f));
                else if (Clear(p, 6f)) Silo(p, _rng.NextFloat(2f, 3.4f), _rng.NextFloat(5f, 10f));
            }
        }

        /// <summary>
        /// The country past the edge: it starts from the field's own outermost corners, height for height, and the land goes on
        /// from there into hills for the haze to swallow.
        /// </summary>
        void Horizon()
        {
            const int round = 2 * (W + H);
            var far = new List<float> { 0f };
            // close together near the field, where the shape of the ground shows
            while (far[^1] < 380f) far.Add(far[^1] + math.clamp(1f + far[^1] * 0.06f, 1f, 40f));
            var outs = far.ToArray();
            var v = new Vector3[outs.Length * round];
            var nrm = new Vector3[v.Length];
            var col = new Color[v.Length];
            var tri = new int[(outs.Length - 1) * round * 6];
            int2 Edge(int i)
            {
                i = (i + round) % round;
                if (i < W) return new int2(i, 0);
                if (i < W + H) return new int2(W, i - W);
                if (i < W + H + W) return new int2(W - (i - W - H), H);
                return new int2(0, H - (i - W - H - W));
            }
            for (int r = 0; r < outs.Length; r++)
            for (int i = 0; i < round; i++)
            {
                int2 e = Edge(i);
                float d = outs[r];
                float2 at = World(e.x, e.y), p = at * new float2((MapView.HalfW + d) / MapView.HalfW, (MapView.HalfH + d) / MapView.HalfH);
                // whatever was dug or levelled at the edge carries on a little way
                float k = 1f - math.smoothstep(0f, 32f, d);
                float y = Lie(p, _off) + (_h[VI(e.x, e.y)] - Lie(at, _off)) * k + math.smoothstep(24f, 240f, d) * (8f + noise.snoise(p * 0.011f) * 8f);
                if (r == 0) y = _h[VI(e.x, e.y)];
                v[r * round + i] = V(p, y);
                Color c = Color.Lerp(Soil(p), C(0x5d5953), math.smoothstep(2f, 45f, d));
                c = Color.Lerp(c, _c[VI(e.x, e.y)], k);
                c = Color.Lerp(c, C(0x3a3a3c), math.smoothstep(60f, 300f, d));
                c.a = 0f;
                col[r * round + i] = c;
            }
            for (int r = 0; r < outs.Length; r++)
            for (int i = 0; i < round; i++)
            {
                int2 e = Edge(i);
                if (r == 0) { nrm[i] = GroundNormal(e.x, e.y); continue; }
                Vector3 along = v[r * round + (i + 1) % round] - v[r * round + (i + round - 1) % round];
                Vector3 outward = v[math.min(outs.Length - 1, r + 1) * round + i] - v[(r - 1) * round + i];
                Vector3 n = Vector3.Cross(along, outward).normalized;
                nrm[r * round + i] = n.y < 0f ? -n : n;
            }
            int t = 0;
            for (int r = 0; r + 1 < outs.Length; r++)
            for (int i = 0; i < round; i++)
            {
                int a = r * round + i, b = r * round + (i + 1) % round, c = b + round, d = a + round;
                tri[t++] = a; tri[t++] = b; tri[t++] = c;
                tri[t++] = a; tri[t++] = c; tri[t++] = d;
            }
            var m = new Mesh { name = "Horizon", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.vertices = v; m.normals = nrm; m.colors = col; m.triangles = tri;
            m.RecalculateBounds();
            Make("Horizon", m, _ground);
        }

        Vector3 GroundNormal(int gx, int gz)
        {
            float dx = _h[VI(math.min(W, gx + 1), gz)] - _h[VI(math.max(0, gx - 1), gz)];
            float dz = _h[VI(gx, math.min(H, gz + 1))] - _h[VI(gx, math.max(0, gz - 1))];
            return new Vector3(-dx, 2f, -dz).normalized;
        }

        /// <summary>The colour of untouched ground: grey dust, darker where it lies low.</summary>
        Color Soil(float2 p)
        {
            float n = Fbm((p + _off) * 0.05f) * 0.5f + 0.5f;
            float patch = math.smoothstep(0.45f, 0.75f, Fbm((p - _off) * 0.02f) * 0.5f + 0.5f);
            return Color.Lerp(Color.Lerp(C(0x5a5650), C(0x77726a), n), C(0x4a4f4a), patch * 0.6f);
        }

        void Ground()
        {
            const int size = 40; // divides the field exactly
            for (int cz = 0; cz < H; cz += size)
            for (int cx = 0; cx < W; cx += size)
            {
                int n = size + 1;
                var v = new Vector3[n * n];
                var nrm = new Vector3[n * n];
                var col = new Color[n * n];
                var tri = new int[size * size * 6];
                for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                {
                    int gx = cx + x, gz = cz + z, k = z * n + x;
                    v[k] = new Vector3(gx - MapView.HalfW, _h[VI(gx, gz)], gz - MapView.HalfH);
                    nrm[k] = GroundNormal(gx, gz);
                    col[k] = _c[VI(gx, gz)];
                    col[k].a = 0f;
                }
                int t = 0;
                for (int z = 0; z < size; z++)
                for (int x = 0; x < size; x++)
                {
                    int k = z * n + x;
                    tri[t++] = k; tri[t++] = k + n; tri[t++] = k + 1;
                    tri[t++] = k + 1; tri[t++] = k + n; tri[t++] = k + n + 1;
                }
                var m = new Mesh { name = "Ground" };
                m.vertices = v; m.normals = nrm; m.colors = col; m.triangles = tri;
                m.RecalculateBounds();
                var go = Make("Ground", m, _ground);
                go.AddComponent<MeshCollider>().sharedMesh = m;
                go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
        }

        GameObject Make(string name, Mesh m, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root, false);
            go.AddComponent<MeshFilter>().sharedMesh = m;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            go.isStatic = true;
            return go;
        }

        void MakeChunk(string name, MeshBuilder mb, Material mat)
        {
            if (mb.I.Count > 0) Make(name, mb.Build(name), mat);
        }

        // ------------------------------------------------------------------ what the soldiers need

        void Finish()
        {
            var map = new MapView
            {
                Ground = new NativeArray<float>(_h, Allocator.Persistent),
                PropTop = new NativeArray<float>(_propTop, Allocator.Persistent),
                Blocked = new NativeArray<byte>(_blocked, Allocator.Persistent),
                Rough = new NativeArray<byte>(_rough, Allocator.Persistent),
                Cover = new NativeArray<byte>(W * H, Allocator.Persistent),
                Flow = new NativeArray<sbyte>(Front.Count * W * H, Allocator.Persistent),
                Dist = new NativeArray<float>(Front.Count * W * H, Allocator.Persistent),
                Objectives = new NativeArray<float2>(_objectives, Allocator.Persistent),
            };
            for (int z = 1; z < H - 1; z++)
            for (int x = 1; x < W - 1; x++)
            {
                int i = z * W + x;
                if (_blocked[i] != 0) continue;
                float2 p = MapView.Centre(new int2(x, z));
                float h = Height(p);
                byte c = 0;
                // something chest high within a couple of metres on that side, but low enough to shoot over
                for (int k = 0; k < 4; k++)
                {
                    float2 dir = k == 0 ? new float2(1, 0) : k == 1 ? new float2(-1, 0) : k == 2 ? new float2(0, 1) : new float2(0, -1);
                    float top = -1000f;
                    for (float d = 1f; d <= 2.6f; d += 0.5f)
                    {
                        float2 q = p + dir * d;
                        top = math.max(top, math.max(Height(q), _propTop[MapView.Index(q)]));
                    }
                    float up = top - h;
                    if (up > 0.65f && up < 1.55f) c |= k == 0 ? MapView.FromPlusX : k == 1 ? MapView.FromMinusX : k == 2 ? MapView.FromPlusZ : MapView.FromMinusZ;
                }
                if (_roof[i] != 0) c |= MapView.Roofed;
                map.Cover[i] = c;
            }
            new FlowFieldJob { Ground = map.Ground, Blocked = map.Blocked, Rough = map.Rough, Objectives = map.Objectives, Flow = map.Flow, Dist = map.Dist }.Schedule(Front.Count, 1).Complete();
            Map = map;

            // ---- tanks: wide of everything solid, and their own ways to each objective
            TankBlocked = new NativeArray<byte>(W * H, Allocator.Persistent);
            TankFlow = new NativeArray<sbyte>(Front.Count * W * H, Allocator.Persistent);
            const int r = 3;
            for (int z = 0; z < H; z++)
            for (int x = 0; x < W; x++)
            {
                int i = z * W + x;
                if (x < 5 || z < 5 || x >= W - 5 || z >= H - 5) TankBlocked[i] = 1;
                if (_blocked[i] == 0 || _propTop[i] - Height(MapView.Centre(new int2(x, z))) < 0.55f) continue;
                for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dz * dz <= r * r + 2 && x + dx >= 0 && z + dz >= 0 && x + dx < W && z + dz < H) TankBlocked[(z + dz) * W + x + dx] = 1;
            }
            // as near each objective as a tank can stand
            var ends = new NativeArray<float2>(Front.Count, Allocator.TempJob);
            for (int o = 0; o < Front.Count; o++)
            {
                float best = 1e9f;
                ends[o] = _objectives[o];
                int2 c0 = MapView.Cell(_objectives[o]);
                for (int dz = -34; dz <= 34; dz++)
                for (int dx = -34; dx <= 34; dx++)
                {
                    int2 c = c0 + new int2(dx, dz);
                    if (c.x < 0 || c.y < 0 || c.x >= W || c.y >= H || TankBlocked[MapView.Index(c)] != 0) continue;
                    float d = dx * dx + dz * dz;
                    if (d < best) { best = d; ends[o] = MapView.Centre(c); }
                }
            }
            var smooth = new NativeArray<byte>(W * H, Allocator.TempJob);
            var far = new NativeArray<float>(Front.Count * W * H, Allocator.TempJob);
            new FlowFieldJob { Ground = map.Ground, Blocked = TankBlocked, Rough = smooth, Objectives = ends, Flow = TankFlow, Dist = far }.Schedule(Front.Count, 1).Complete();
            ends.Dispose(); smooth.Dispose(); far.Dispose();
        }

        public NativeArray<byte> TankBlocked;
        public NativeArray<sbyte> TankFlow;

        public void Dispose()
        {
            if (!Map.Ground.IsCreated) return;
            Map.Ground.Dispose(); Map.PropTop.Dispose(); Map.Blocked.Dispose(); Map.Rough.Dispose();
            Map.Cover.Dispose(); Map.Flow.Dispose(); TankBlocked.Dispose(); TankFlow.Dispose(); Map.Dist.Dispose(); Map.Objectives.Dispose();
        }
    }
}
