using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace LSBS
{
    /// <summary>
    /// Makes a battlefield from a seed: mud, craters, trench lines, wire, wrecks, a ruined pumping station in the middle
    /// and a smoking works at each end. Also fills in the MapView the soldiers think with.
    /// </summary>
    public class MapGenerator
    {
        const int W = MapView.W, H = MapView.H;

        public MapView Map;
        /// <summary>Where smoke comes out: xyz, w how much.</summary>
        public readonly List<Vector4> Chimneys = new();
        /// <summary>Where each side's artillery may stand, row behind row.</summary>
        public readonly List<List<float3>>[] GunRows = { new(), new() };
        public struct BunkerInfo { public float2 At; public float Face; }
        public readonly List<BunkerInfo> BunkerList = new();

        readonly Transform _root;
        readonly Material _ground, _props;
        Random _rng;
        float[] _h;
        Color[] _c;
        float[] _propTop;
        byte[] _blocked, _rough, _roof;
        readonly Dictionary<int2, MeshBuilder> _chunks = new();
        readonly List<(float2[] line, float toward)> _trenches = new();
        float2[] _objectives;
        float2 _off;

        static Color C(int hex) => MeshBuilder.Col(hex);
        static Color Metal(int hex) => MeshBuilder.Col(hex, MeshBuilder.Metal);
        static readonly Color Brick = C(0x5a3a2c), BrickDark = C(0x3f2a22), Wood = C(0x4a3826), WoodDark = C(0x2f241a), Bag = C(0x6b5c40), Stone = C(0x55504a);
        static readonly Color Iron = Metal(0x3a3837), Rust = Metal(0x5c3420), Brass = Metal(0x8f6a30), Copper = Metal(0x6e452c), Ember = MeshBuilder.Col(0xff7a20, MeshBuilder.Glow);

        public MapGenerator(Transform root, Material ground, Material props, uint seed)
        {
            _root = root; _ground = ground; _props = props;
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
            _objectives = new[]
            {
                new float2(-196, 0), new float2(-100, -62), new float2(-100, 62), new float2(0, -88), new float2(0, 0), new float2(0, 88),
                new float2(100, -62), new float2(100, 62), new float2(196, 0),
            };
            float2 off = _off = _rng.NextFloat2(-500f, 500f);

            // objectives are levelled off at whatever height they come at
            var level = new float[Front.Count];
            for (int o = 0; o < Front.Count; o++)
            {
                for (int i = 0; i < 12; i++) level[o] += Lie(_objectives[o] + new float2(math.cos(i), math.sin(i)) * (4f + i), off) / 12f;
                if (o == Front.GreenBase || o == Front.GreyBase) level[o] = math.clamp(level[o], 1f, 5f);
            }
            Color mudDark = C(0x3a332a), clay = C(0x62564a);
            for (int z = 0; z <= H; z++)
            for (int x = 0; x <= W; x++)
            {
                float2 p = World(x, z);
                float h = Lie(p, off);
                // objectives stand on flat ground
                for (int o = 0; o < Front.Count; o++)
                {
                    bool home = o == Front.GreenBase || o == Front.GreyBase, centre = o == Front.Centre;
                    float k = math.smoothstep(home ? 26f : centre ? 20f : 13f, home ? 52f : centre ? 46f : 32f, math.distance(p, _objectives[o]));
                    h = math.lerp(level[o], h, k);
                }
                _h[VI(x, z)] = h;
                _c[VI(x, z)] = Soil(p);
            }

            Trenches();
            int craters = 300;
            for (int i = 0; i < craters; i++)
            {
                float2 p = new(_rng.NextFloat(-150f, 150f) * math.sqrt(_rng.NextFloat(0.15f, 1f)), _rng.NextFloat(-MapView.HalfH + 8f, MapView.HalfH - 8f));
                float r = _rng.NextFloat() < 0.12f ? _rng.NextFloat(5f, 8f) : _rng.NextFloat(1.8f, 4.2f);
                bool clear = true;
                for (int o = 0; o < Front.Count; o++) clear &= math.distance(p, _objectives[o]) > (o == Front.Centre ? 22f : 12f) + r;
                // fewer out on the flats
                if (noise.snoise((p + off) * 0.0075f + 141f) > 0.1f && _rng.NextFloat() < 0.6f) continue;
                if (!clear) continue;
                Crater(p, r, mudDark, clay);
            }

            PumpingStation();
            for (int o = 1; o < Front.Count - 1; o++) if (o != Front.Centre) Outpost(o);
            Works(0);
            Works(1);
            Battery(0);
            Battery(1);
            Bunkers();
            Sandbags();
            NoMansLand();
            Horizon();
            Ground();
            foreach (var kv in _chunks) MakeChunk("Props " + kv.Key, kv.Value, _props);
            Finish();
        }

        /// <summary>The shape of the country: hills and ridges, and between them shelves and bottoms that are all but flat.</summary>
        static float Lie(float2 p, float2 off)
        {
            float2 q = p + off;
            float big = Fbm(q * 0.0062f) * 6.5f;
            float hills = math.pow(math.max(0f, Fbm(q * 0.0125f + 31f)), 1.35f) * 9f;
            float ridge = math.pow(1f - math.abs(noise.snoise(q * 0.0085f + 63f)), 3f) * 4.2f;
            float hollow = math.pow(math.max(0f, -Fbm(q * 0.011f + 12f)), 1.5f) * 5f;
            float h = big + hills + ridge - hollow;
            // the flats: long stretches, all but level
            float flat = math.smoothstep(-0.08f, 0.2f, noise.snoise(q * 0.0075f + 141f) + 0.1f * noise.snoise(q * 0.03f));
            float level = Fbm(q * 0.0028f + 9f) * 4.5f + 1f;
            h = math.lerp(h, level, flat * 0.96f);
            // and the small stuff, less of it on the flat
            h += (Fbm(q * 0.035f) * 0.7f + noise.snoise(q * 0.22f) * 0.1f) * (1f - 0.85f * flat);
            return h + 1.5f;
        }

        void Crater(float2 p, float r, Color floor, Color rim)
        {
            float depth = r * 0.34f;
            Each(p, r * 1.6f, (x, z, d) =>
            {
                float k = d / r;
                float dh = k < 1f ? -depth * (1f - k * k) + 0.18f * r * 0.35f * k : 0.063f * r * math.saturate(1f - (k - 1f) / 0.6f);
                _h[VI(x, z)] += dh;
                Color tone = k < 1f ? Color.Lerp(floor, rim, k * 0.6f) : rim;
                _c[VI(x, z)] = Color.Lerp(_c[VI(x, z)], tone, k < 1f ? 0.8f : 0.5f * math.saturate(1.6f - k));
            });
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

        // ------------------------------------------------------------------ trenches

        void Trenches()
        {
            _dug = new float[_h.Length];
            _bank = new float[_h.Length];
            for (int side = -1; side <= 1; side += 2)
            {
                // line behind line, each to its own depth
                FrontLine(side * 36f, side, 8f, 1.6f);
                FrontLine(side * 57f, side, 7f, 1.9f);
                FrontLine(side * 80f, side, 6f, 1.85f);
                FrontLine(side * 118f, side, 6f, 1.95f);
                FrontLine(side * 138f, side, 5f, 1.8f);
                FrontLine(side * 162f, side, 5f, 2.0f);
                FrontLine(side * 178f, side, 4f, 1.65f);
                // deep ways from line to line
                for (int k = 0; k < 11; k++)
                {
                    float z = math.lerp(-112f, 112f, k / 10f) + _rng.NextFloat(-6f, 6f);
                    float from = k % 2 == 0 ? 36f : 57f, to = k % 3 == 0 ? 178f : 162f;
                    Dig(Zig(new float2(side * from, z), new float2(side * to, z + _rng.NextFloat(-16f, 16f)), 8f, 6f), 2.2f, 0f);
                }
                // short blind saps pushed out toward the other side
                for (int k = 0; k < 8; k++)
                {
                    float z = math.lerp(-104f, 104f, k / 7f) + _rng.NextFloat(-8f, 8f);
                    Dig(Zig(new float2(side * 37f, z), new float2(side * _rng.NextFloat(12f, 24f), z + _rng.NextFloat(-9f, 9f)), 6f, 4f), _rng.NextFloat(1.3f, 1.9f), 0f);
                }
            }
            // works about every objective out in the open, so whoever has it can hold it
            for (int o = 1; o < Front.Count - 1; o++)
            {
                bool centre = o == Front.Centre;
                float rx = centre ? 27f : 20f, rz = centre ? 32f : 23f;
                for (int s = -1; s <= 1; s += 2)
                {
                    var arc = new List<float2>();
                    for (int i = 0; i <= 10; i++)
                    {
                        float a = math.lerp(-1.2f, 1.2f, i / 10f);
                        arc.Add(_objectives[o] + new float2(s * math.cos(a) * rx, math.sin(a) * rz) + _rng.NextFloat2(-1f, 1f));
                    }
                    Dig(arc.ToArray(), _rng.NextFloat(1.6f, 2.0f), s);
                }
            }
            // and along the middle of everything, from one to the next
            for (int o = 3; o < 5; o++)
                Dig(Zig(_objectives[o] + new float2(_rng.NextFloat(-6f, 6f), 26f), _objectives[o + 1] + new float2(_rng.NextFloat(-6f, 6f), -26f), 8f, 7f), 2.1f, 0f);

            for (int i = 0; i < _h.Length; i++) _h[i] += _dug[i] > 0.05f ? -_dug[i] : _bank[i];
        }

        float[] _dug, _bank;

        void FrontLine(float x, float side, float wobble, float depth)
        {
            var pts = new List<float2>();
            float z = -MapView.HalfH + 6f;
            int i = 0;
            float bend = _rng.NextFloat(0f, 100f);
            while (z < MapView.HalfH - 6f)
            {
                // dog-legged, the way they dug them
                float zig = (i++ % 2 == 0 ? -1f : 1f) * wobble * 0.5f;
                pts.Add(new float2(x + zig + noise.snoise(new float2(bend, z * 0.012f)) * 7f, z));
                z += _rng.NextFloat(8f, 13f);
            }
            Dig(pts.ToArray(), depth, -side);
        }

        float2[] Zig(float2 a, float2 b, float step, float wobble)
        {
            float len = math.distance(a, b);
            int n = math.max(2, (int)(len / step));
            float2 d = math.normalize(b - a), s = new(-d.y, d.x);
            var pts = new float2[n + 1];
            for (int i = 0; i <= n; i++) pts[i] = math.lerp(a, b, (float)i / n) + (i > 0 && i < n ? s * ((i % 2 == 0 ? -1f : 1f) * wobble * 0.5f) : 0f);
            return pts;
        }

        static float SegDist(float2 p, float2 a, float2 b)
        {
            float2 ab = b - a;
            float t = math.saturate(math.dot(p - a, ab) / math.lengthsq(ab));
            return math.distance(p, a + ab * t);
        }

        /// <summary>How deep the step along the enemy's side is: no more than can be seen and shot over.</summary>
        const float FireStep = 1.1f;

        /// <param name="toward">The side the enemy's on (+1 is +x), where the parapet goes; 0 for a plain ditch.</param>
        void Dig(float2[] line, float depth, float toward)
        {
            _trenches.Add((line, toward));
            Color floor = C(0x2e241a), bank = C(0x64513a);
            // shallower here, deeper there
            float seed = _rng.NextFloat(0f, 500f);
            var dist = new Dictionary<int, (float d, bool front)>();
            for (int s = 0; s + 1 < line.Length; s++)
            {
                float2 mid = (line[s] + line[s + 1]) * 0.5f;
                float r = math.distance(line[s], line[s + 1]) * 0.5f + 5f;
                float2 a = line[s], b = line[s + 1];
                float2 ab = b - a;
                Each(mid, r, (x, z, _) =>
                {
                    float2 p = World(x, z), from = p - (a + ab * math.saturate(math.dot(p - a, ab) / math.lengthsq(ab)));
                    float d = math.length(from);
                    int k = VI(x, z);
                    if (!dist.TryGetValue(k, out var had) || d < had.d) dist[k] = (d, from.x * toward > 0f);
                });
            }
            foreach (var kv in dist)
            {
                var (d, front) = kv.Value;
                float2 at = World(kv.Key % (W + 1), kv.Key / (W + 1));
                float vary = 1f + 0.2f * noise.snoise(at * 0.028f + seed);
                // a step on the enemy's side, to see out from; the back goes straight up
                float edge = front ? 3.1f : 2.4f;
                if (d < edge)
                {
                    float k = 1f - math.smoothstep(front ? 2.2f : 1.0f, edge, d);
                    float deep = depth * vary;
                    if (front) deep = math.lerp(deep, math.min(deep, FireStep), math.smoothstep(0.6f, 1.2f, d));
                    _dug[kv.Key] = math.max(_dug[kv.Key], deep * k);
                    _c[kv.Key] = Color.Lerp(_c[kv.Key], floor, k);
                }
                else if (d < edge + 1.5f && toward != 0f)
                {
                    float k = 1f - math.abs(d - edge - 0.4f) / 1.0f;
                    if (k <= 0f) continue;
                    _bank[kv.Key] = math.max(_bank[kv.Key], 0.24f * k);
                    _c[kv.Key] = Color.Lerp(_c[kv.Key], bank, k * 0.8f);
                }
            }
        }

        // ------------------------------------------------------------------ props

        MeshBuilder Mb(float2 p)
        {
            int2 k = (int2)math.floor((p + new float2(MapView.HalfW, MapView.HalfH)) / 40f);
            if (!_chunks.TryGetValue(k, out var mb)) _chunks[k] = mb = new MeshBuilder();
            return mb;
        }

        static Vector3 V(float2 p, float y) => new(p.x, y, p.y);

        /// <summary>Solid to bullets (up to top), feet, and bodies.</summary>
        void Solid(float2 p, float2 size, float yawDeg, float bottom, float top, bool collider = true, bool blocks = true)
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
            if (!collider) return;
            var go = new GameObject("Solid");
            go.transform.SetParent(_root, false);
            go.transform.SetPositionAndRotation(V(p, (bottom + top) * 0.5f), Quaternion.Euler(0, yawDeg, 0));
            go.AddComponent<BoxCollider>().size = new Vector3(size.x, math.max(0.1f, top - bottom), size.y);
        }

        bool NearObjective(float2 p, float centre)
        {
            if (math.length(p) < centre) return true;
            for (int o = 0; o < Front.Count; o++) if (math.distance(p, _objectives[o]) < 15f) return true;
            return false;
        }

        bool Clear(float2 p, float r)
        {
            int2 c0 = MapView.Cell(p - r), c1 = MapView.Cell(p + r);
            for (int z = c0.y; z <= c1.y; z++)
            for (int x = c0.x; x <= c1.x; x++)
                if (_blocked[MapView.Index(new int2(x, z))] != 0) return false;
            return true;
        }

        float TrenchDistance(float2 p)
        {
            float best = 1e9f;
            foreach (var (line, _) in _trenches)
                for (int s = 0; s + 1 < line.Length; s++)
                    best = math.min(best, SegDist(p, line[s], line[s + 1]));
            return best;
        }

        /// <summary>Concrete boxes a little behind the lines: a slit to shoot through, a way in at the back.</summary>
        void Bunkers()
        {
            var spots = new List<(float2 p, float face)>();
            for (int side = -1; side <= 1; side += 2)
            {
                for (int k = 0; k < 7; k++) spots.Add((new float2(side * 67f, math.lerp(-100f, 100f, k / 6f) + _rng.NextFloat(-7f, 7f)), -side));
                for (int k = 0; k < 5; k++) spots.Add((new float2(side * 128f, math.lerp(-90f, 90f, k / 4f) + _rng.NextFloat(-7f, 7f)), -side));
                for (int k = 0; k < 3; k++) spots.Add((new float2(side * 170f, math.lerp(-60f, 60f, k / 2f) + _rng.NextFloat(-7f, 7f)), -side));
            }
            foreach (var (at, face) in spots)
            {
                // the nearest place to it that's clear of trenches and objectives
                float2 p = at;
                bool ok = false;
                for (int t = 0; t < 30 && !ok; t++)
                {
                    p = at + (t == 0 ? float2.zero : _rng.NextFloat2Direction() * _rng.NextFloat(0f, 3f + t * 0.5f));
                    ok = math.abs(p.x) > 30f && math.abs(p.x) < 185f && math.abs(p.y) < 115f && Clear(p, 6f) && TrenchDistance(p) > 5.6f && !NearObjective(p, 30f);
                }
                if (ok) Bunker(p, face);
            }
        }

        void Bunker(float2 p, float face)
        {
            const float deep = 5f, wide = 7f;   // x, z inside and out
            // on the grid, so its walls and its way in fall on whole cells
            p = MapView.Centre(MapView.Cell(p));
            BunkerList.Add(new BunkerInfo { At = p, Face = face });
            Color concrete = C(0x66625a), dark = C(0x45423c);
            // level ground under it
            float y = Height(p);
            Each(p, 6.5f, (x, z, d) => { int k = VI(x, z); _h[k] = math.lerp(y, _h[k], math.smoothstep(4.6f, 6.5f, d)); _c[k] = Color.Lerp(C(0x3a332a), _c[k], math.smoothstep(3f, 6.5f, d)); });
            var mb = Mb(p);
            void Block(float2 at, float2 size, float from, float to, bool solid)
            {
                float2 c = p + new float2(at.x * face, at.y);
                mb.Box(V(c, y + (from + to) * 0.5f), new Vector3(size.x, to - from, size.y), concrete);
                if (solid) Solid(c, size, 0f, y + from, y + to);
            }
            // front, chest high, with the slit over it; sides full height; a way in at the back
            Block(new float2(deep * 0.5f, 0), new float2(0.6f, wide), -0.3f, 1.28f, true);
            Block(new float2(deep * 0.5f, 0), new float2(0.6f, wide), 1.95f, 2.45f, false);
            for (int s = -1; s <= 1; s += 2)
            {
                Block(new float2(0, s * wide * 0.5f), new float2(deep, 0.6f), -0.3f, 2.45f, true);
                Block(new float2(deep * 0.5f, s * wide * 0.5f), new float2(0.7f, 0.7f), 1.2f, 2.45f, false);
                Block(new float2(-deep * 0.5f, s * (wide * 0.5f - 1.0f)), new float2(0.5f, 2.0f), -0.3f, 2.45f, true);
            }
            mb.Box(V(p, y + 2.68f), new Vector3(deep + 1.2f, 0.46f, wide + 1.2f), dark);
            mb.Box(V(p, y + 3.0f), new Vector3(deep + 0.2f, 0.25f, wide + 0.2f), C(0x4a4032));
            for (int i = 0; i < 12; i++)
            {
                float2 q = p + new float2((deep * 0.5f + 0.5f) * face, math.lerp(-wide * 0.5f, wide * 0.5f, i / 11f));
                mb.Box(V(q, y + 0.12f + (i % 2) * 0.16f), new Vector3(0.45f, 0.24f, 0.7f), Quaternion.Euler(0, _rng.NextFloat(-9f, 9f), 0), Color.Lerp(Bag, C(0x4c4230), _rng.NextFloat()), 0.82f);
            }
            // a stovepipe, and iron round the slit
            mb.Tube(V(p + new float2(-1.2f * face, 1.5f), y + 2.8f), V(p + new float2(-1.2f * face, 1.5f), y + 4f), 0.13f, 0.11f, 8, Iron);
            mb.Box(V(p + new float2((deep * 0.5f + 0.32f) * face, 0), y + 1.32f), new Vector3(0.08f, 0.1f, wide - 0.6f), Rust);
            mb.Box(V(p + new float2((deep * 0.5f + 0.32f) * face, 0), y + 1.92f), new Vector3(0.08f, 0.1f, wide - 0.6f), Rust);
            // the roof, for falling bodies and for the player to stand under
            var roof = new GameObject("Roof");
            roof.transform.SetParent(_root, false);
            roof.transform.position = V(p, y + 2.75f);
            roof.AddComponent<BoxCollider>().size = new Vector3(deep + 1.2f, 0.6f, wide + 1.2f);
            for (float z = -wide * 0.5f + 0.5f; z < wide * 0.5f; z += 0.5f)
            for (float x = -deep * 0.5f + 0.5f; x < deep * 0.5f; x += 0.5f)
                _roof[MapView.Index(p + new float2(x, z))] = 1;
        }

        void Sandbags()
        {
            foreach (var (line, toward) in _trenches)
            {
                float run = 0f;
                for (int s = 0; s + 1 < line.Length; s++)
                {
                    float2 a = line[s], b = line[s + 1];
                    float len = math.distance(a, b);
                    float2 d = (b - a) / len, n = new(-d.y, d.x);
                    float yaw = math.degrees(math.atan2(d.x, d.y));
                    if (toward != 0f && n.x * toward < 0f) n = -n;
                    for (float t = 0f; t < len; t += 0.72f, run += 0.72f)
                    {
                        float2 p = a + d * t;
                        // duckboards along the floor
                        if ((int)(run / 0.72f) % 2 == 0)
                            Mb(p).Box(V(p, Height(p) + 0.05f), new Vector3(1.05f, 0.05f, 0.5f), Quaternion.Euler(_rng.NextFloat(-3f, 3f), yaw + _rng.NextFloat(-6f, 6f), _rng.NextFloat(-3f, 3f)), _rng.NextBool() ? Wood : WoodDark);
                        if (toward == 0f) continue;
                        // gaps to climb out by
                        if (noise.snoise(new float2(run * 0.09f, line[0].x)) > 0.45f) continue;
                        for (int row = 0; row < 2; row++)
                        {
                            float2 q = p + n * (3.3f + _rng.NextFloat(-0.08f, 0.08f)) + d * (row * 0.36f);
                            if (row == 1 && _rng.NextFloat() < 0.2f) continue;
                            Mb(q).Box(V(q, Height(q) + 0.1f + row * 0.18f), new Vector3(0.42f, 0.22f, 0.7f), Quaternion.Euler(_rng.NextFloat(-7f, 7f), yaw + _rng.NextFloat(-9f, 9f), _rng.NextFloat(-6f, 6f)), Color.Lerp(Bag, C(0x4c4230), _rng.NextFloat()), 0.82f);
                        }
                        // revetting: planks holding up the far wall
                        if ((int)(run / 0.72f) % 5 == 0)
                        {
                            float2 q = p - n * 1.6f;
                            Mb(q).Box(V(q, Height(p) + 0.85f), new Vector3(0.09f, 1.9f, 0.12f), Quaternion.Euler(0, yaw, _rng.NextFloat(-5f, 5f)), WoodDark);
                        }
                    }
                }
            }
        }

        void Wall(float2 a, float2 b, float height, float ruin, Color col)
        {
            float len = math.distance(a, b);
            float2 d = (b - a) / len;
            float yaw = math.degrees(math.atan2(d.x, d.y));
            float seed = _rng.NextFloat(0f, 100f);
            for (float t = 0.5f; t < len; t += 1f)
            {
                float2 p = a + d * t;
                float y0 = Height(p) - 0.3f;
                float k = 0.5f + 0.5f * noise.snoise(new float2(seed, t * 0.23f));
                float h = height * math.lerp(1f, k, ruin);
                if (h < 0.6f) continue;
                var mb = Mb(p);
                mb.Box(V(p, y0 + h * 0.5f), new Vector3(0.55f, h, 1.02f), Quaternion.Euler(0, yaw, 0), Color.Lerp(col, BrickDark, _rng.NextFloat(0f, 0.6f)));
                if (_rng.NextFloat() < 0.5f) mb.Box(V(p, y0 + h + 0.15f), new Vector3(0.5f, _rng.NextFloat(0.2f, 0.5f), 0.45f), Quaternion.Euler(0, yaw, 0), BrickDark);
                Solid(p, new float2(0.55f, 1.02f), yaw, y0, y0 + h);
                if (_rng.NextFloat() < 0.25f) Rubble(p + _rng.NextFloat2(-2f, 2f), 4);
            }
        }

        void Rubble(float2 p, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float2 q = p + _rng.NextFloat2(-1f, 1f);
                Mb(q).Box(V(q, Height(q) + 0.06f), new Vector3(_rng.NextFloat(0.15f, 0.5f), _rng.NextFloat(0.1f, 0.3f), _rng.NextFloat(0.15f, 0.5f)), UnityEngine.Random.rotation, Color.Lerp(Brick, Stone, _rng.NextFloat()));
            }
        }

        void Gear(Vector3 at, Quaternion rot, float r, int teeth, Color col)
        {
            var mb = Mb(new float2(at.x, at.z));
            var was = mb.M;
            mb.M = Matrix4x4.TRS(at, rot, Vector3.one);
            float w = r * 0.16f;
            // rim, spokes, hub, teeth
            for (int i = 0; i < teeth; i++)
            {
                float a = i * 360f / teeth;
                var q = Quaternion.Euler(0, a, 0);
                mb.Box(q * new Vector3(0, 0, r * 0.88f), new Vector3(r * 6.6f / teeth, w, r * 0.16f), q, col);
                if (i % 2 == 0) mb.Box(q * new Vector3(0, 0, r * 1.03f), new Vector3(r * 3.2f / teeth, w * 0.8f, r * 0.16f), q, col);
            }
            for (int i = 0; i < 6; i++)
            {
                var q = Quaternion.Euler(0, i * 60f, 0);
                mb.Box(q * new Vector3(0, 0, r * 0.48f), new Vector3(r * 0.1f, w * 0.6f, r * 0.74f), q, col);
            }
            mb.Tube(new Vector3(0, -w, 0), new Vector3(0, w, 0), r * 0.2f, r * 0.2f, 10, Brass);
            mb.M = was;
        }

        void Boiler(float2 p, float yaw, float length, float r, bool standing)
        {
            float y = Height(p) - 0.1f;
            var mb = Mb(p);
            var was = mb.M;
            if (standing)
            {
                mb.M = Matrix4x4.TRS(V(p, y), Quaternion.Euler(0, yaw, 0), Vector3.one);
                mb.Tube(Vector3.zero, Vector3.up * length, r, r, 14, Copper);
                mb.Tube(Vector3.up * length, Vector3.up * (length + r * 0.5f), r, r * 0.35f, 14, Copper);
                for (float h = 0.5f; h < length; h += 1.1f) mb.Tube(Vector3.up * h, Vector3.up * (h + 0.12f), r * 1.04f, r * 1.04f, 14, Rust);
                mb.Box(new Vector3(0, 0.7f, r * 0.96f), new Vector3(0.8f, 0.8f, 0.2f), Iron);
                mb.Box(new Vector3(0, 0.7f, r * 1.03f), new Vector3(0.5f, 0.4f, 0.1f), Ember);
                Solid(p, new float2(r * 1.8f, r * 1.8f), yaw, y, y + length);
            }
            else
            {
                mb.M = Matrix4x4.TRS(V(p, y + r * 1.05f), Quaternion.Euler(0, yaw, 0), Vector3.one);
                Vector3 a = new(0, 0, -length * 0.5f), b = new(0, 0, length * 0.5f);
                mb.Tube(a, b, r, r, 14, Copper);
                for (float t = 0.1f; t < 1f; t += 0.2f) mb.Tube(Vector3.Lerp(a, b, t), Vector3.Lerp(a, b, t) + Vector3.forward * 0.14f, r * 1.04f, r * 1.04f, 14, Rust);
                mb.Tube(b, b + Vector3.forward * 0.15f, r * 0.8f, r * 0.7f, 14, Iron);
                mb.Tube(a * 0.4f + Vector3.up * r, a * 0.4f + Vector3.up * (r + 1.4f), 0.25f, 0.2f, 8, Iron);
                for (int s = -1; s <= 1; s += 2) mb.Box(new Vector3(0, -r * 0.8f, s * length * 0.3f), new Vector3(r * 1.8f, r * 0.7f, 0.5f), Iron);
                Solid(p, new float2(r * 2f, length), yaw, y, y + r * 2f);
            }
            mb.M = was;
        }

        void Chimney(float2 p, float height, float r, Color col, float smoke)
        {
            float y = Height(p) - 0.5f;
            var mb = Mb(p);
            mb.Tube(V(p, y), V(p, y + height), r, r * 0.68f, 14, col);
            mb.Tube(V(p, y + height), V(p, y + height + 0.8f), r * 0.82f, r * 0.82f, 14, BrickDark);
            for (float h = 2f; h < height; h += height * 0.22f)
            {
                float k = math.lerp(r, r * 0.68f, h / height);
                mb.Tube(V(p, y + h), V(p, y + h + 0.25f), k * 1.05f, k * 1.05f, 14, Iron);
            }
            mb.Box(V(p, y + 0.8f), new Vector3(r * 2.5f, 2.2f, r * 2.5f), BrickDark);
            Solid(p, new float2(r * 2.4f, r * 2.4f), 0, y, y + height);
            if (smoke > 0f) Chimneys.Add(new Vector4(p.x, y + height + 1f, p.y, smoke));
        }

        void Pipe(float r, Color col, params Vector3[] pts)
        {
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                var mb = Mb(new float2(pts[i].x, pts[i].z));
                mb.Tube(pts[i], pts[i + 1], r, r, 8, col);
                mb.Tube(pts[i + 1] - (pts[i + 1] - pts[i]).normalized * r, pts[i + 1] + (pts[i + 1] - pts[i]).normalized * r, r * 1.3f, r * 1.3f, 8, Brass);
            }
        }

        void PumpingStation()
        {
            float y = Height(0);
            // the shell of a brick hall, blown open
            float2[] hall = { new(-9, -11), new(9, -11), new(9, 11), new(-9, 11) };
            for (int i = 0; i < 4; i++)
            {
                float2 a = hall[i], b = hall[(i + 1) % 4];
                float2 d = math.normalize(b - a);
                float gap = math.distance(a, b) * 0.5f;
                Wall(a, a + d * (gap - 2.2f), 3.8f, 0.85f, Brick);
                Wall(a + d * (gap + 2.2f), b, 3.8f, 0.85f, Brick);
            }
            foreach (var h in hall)
            {
                Mb(h).Box(V(h, y + 2.4f), new Vector3(1.1f, 5.4f, 1.1f), BrickDark);
                Solid(h, new float2(1.1f, 1.1f), 0, y, y + 5f);
            }
            Boiler(new float2(-3.6f, 2.5f), 20f, 5.5f, 1.2f, false);
            Boiler(new float2(4.2f, -4f), 0f, 4.2f, 1.1f, true);
            Gear(new Vector3(3.5f, y + 1.9f, 5.2f), Quaternion.Euler(78, 25, 0), 2.6f, 24, Rust);
            Gear(new Vector3(6.0f, y + 1.0f, 3.2f), Quaternion.Euler(84, -20, 0), 1.4f, 16, Brass);
            Solid(new float2(3.5f, 5.2f), new float2(4.4f, 1f), 25, y, y + 2.6f, true);
            Pipe(0.22f, Copper, new Vector3(4.2f, y + 3.6f, -4f), new Vector3(4.2f, y + 5.4f, -4f), new Vector3(-6.5f, y + 5.4f, -4f), new Vector3(-6.5f, y + 5.4f, 7f), new Vector3(-6.5f, y, 7f));
            Chimney(new float2(13.5f, 14f), 17f, 1.5f, Brick, 0.5f);
            for (int i = 0; i < 14; i++) Rubble(_rng.NextFloat2(-15f, 15f), 6);
            // the mast everybody's after
            Mb(0).Tube(new Vector3(0, y, 0), new Vector3(0, y + 9f, 0), 0.14f, 0.09f, 8, Iron);
            Mb(0).Tube(new Vector3(0, y, 0), new Vector3(0, y + 0.5f, 0), 0.9f, 0.7f, 10, Brass);
            Solid(0, new float2(0.8f, 0.8f), 0, y, y + 1f, true, false);
        }

        /// <summary>One of the lesser objectives: a mast, and something worth fighting over.</summary>
        void Outpost(int o)
        {
            float2 p = _objectives[o];
            float y = Height(p);
            Mb(p).Tube(V(p, y), V(p, y + 8f), 0.13f, 0.09f, 8, Iron);
            Mb(p).Tube(V(p, y), V(p, y + 0.45f), 0.85f, 0.65f, 10, Brass);
            switch (o % 4)
            {
                case 0: // a generator house
                    Boiler(p + new float2(4.5f, 3f), _rng.NextFloat(0f, 360f), 4.6f, 1.1f, false);
                    Wall(p + new float2(-7, -6), p + new float2(5, -6), 2.8f, 0.8f, Brick);
                    Wall(p + new float2(-7, -6), p + new float2(-7, 4), 2.8f, 0.8f, Brick);
                    Gear(new Vector3(p.x - 4f, y + 1.6f, p.y + 4.5f), Quaternion.Euler(80, 35, 0), 2f, 20, Rust);
                    break;
                case 1: // a pump head
                    Boiler(p + new float2(-4f, 3.5f), 0f, 4f, 1f, true);
                    Boiler(p + new float2(-4f, -3.5f), 0f, 3.2f, 0.9f, true);
                    Pipe(0.2f, Copper, V(p + new float2(-4f, 3.5f), y + 3.4f), V(p + new float2(-4f, -3.5f), y + 3.0f), V(p + new float2(5f, -5f), y + 3.0f), V(p + new float2(5f, -5f), y));
                    Wall(p + new float2(6, -2), p + new float2(6, 8), 2.2f, 1f, Brick);
                    break;
                case 2: // a wrecked crawler in a yard
                    Crawler(p + new float2(5.5f, 4.5f), _rng.NextFloat(0f, 360f));
                    Wall(p + new float2(-8, 5), p + new float2(0, 8), 2.4f, 1f, Brick);
                    break;
                default: // a chimney and what's left round it
                    Chimney(p + new float2(6f, 6f), 11f, 1.1f, Brick, 0.35f);
                    Wall(p + new float2(-6, -7), p + new float2(6, -7), 2.6f, 0.9f, Brick);
                    Wall(p + new float2(8, -3), p + new float2(8, 4), 2.6f, 0.9f, Brick);
                    break;
            }
            for (int i = 0; i < 5; i++) Rubble(p + _rng.NextFloat2(-9f, 9f), 4);
            // low sandbag walls on two sides, to shoot over
            for (int s = -1; s <= 1; s += 2)
                for (int i = -2; i <= 2; i++)
                {
                    float2 q = p + new float2(s * 10.5f, i * 1.4f + _rng.NextFloat(-0.15f, 0.15f));
                    if (!Clear(q, 1f)) continue;
                    for (int row = 0; row < 3; row++)
                        Mb(q).Box(V(q, Height(q) + 0.1f + row * 0.2f), new Vector3(0.5f, 0.22f, 1.3f), Quaternion.Euler(0, (row % 2) * 5f + _rng.NextFloat(-4f, 4f), 0), Color.Lerp(Bag, C(0x4c4230), _rng.NextFloat()), 0.85f);
                    Solid(q, new float2(0.6f, 1.3f), 0f, Height(q), Height(q) + 0.65f);
                }
        }

        /// <summary>Twenty guns along the back of a side's ground.</summary>
        void Battery(int team)
        {
            float s = team == 0 ? -1f : 1f;
            foreach (float x in new[] { 214f, 208f, 219.5f, 202.5f })
            {
                var row = new List<float3>();
                for (float z = -118f; z <= 118f; z += 5.4f)
                {
                    float2 p = new(s * x, z + _rng.NextFloat(-0.6f, 0.6f));
                    if (!Clear(p, 2f) || math.distance(p, _objectives[Front.Home(team)]) < 12f) continue;
                    row.Add(new float3(p.x, Height(p), p.y));
                }
                GunRows[team].Add(row);
            }
        }

        /// <summary>Places for so many guns: each row filled along its whole length before the next is begun.</summary>
        public List<float3> GunPlaces(int team, int count)
        {
            var places = new List<float3>();
            foreach (var row in GunRows[team])
            {
                int n = math.min(count - places.Count, row.Count);
                for (int i = 0; i < n; i++) places.Add(row[(int)((i + 0.5f) * row.Count / n)]);
                if (places.Count >= count) break;
            }
            return places;
        }

        /// <summary>One gun and its shells, muzzle along z.</summary>
        public static Mesh HowitzerMesh()
        {
            var mb = new MeshBuilder();
            for (int s = -1; s <= 1; s += 2)
            {
                mb.Tube(new Vector3(s * 0.75f, 0.65f, 0), new Vector3(s * 0.9f, 0.65f, 0), 0.65f, 0.65f, 14, WoodDark);
                mb.Tube(new Vector3(s * 0.72f, 0.65f, 0), new Vector3(s * 0.94f, 0.65f, 0), 0.16f, 0.16f, 8, Brass);
                mb.Box(new Vector3(s * 0.4f, 0.4f, -1.3f), new Vector3(0.14f, 0.2f, 2.8f), Quaternion.Euler(-12f, s * -8f, 0), Iron);
            }
            mb.Tube(new Vector3(-0.8f, 0.65f, 0), new Vector3(0.8f, 0.65f, 0), 0.1f, 0.1f, 8, Iron);
            mb.Box(new Vector3(0, 1.15f, 0.35f), new Vector3(1.5f, 1.1f, 0.06f), Quaternion.Euler(-14f, 0, 0), Rust);
            Vector3 breech = new(0, 1.0f, -0.6f), muzzle = new(0, 2.5f, 2.1f);
            mb.Tube(breech, Vector3.Lerp(breech, muzzle, 0.45f), 0.24f, 0.2f, 12, Iron);
            mb.Tube(Vector3.Lerp(breech, muzzle, 0.45f), muzzle, 0.17f, 0.14f, 12, Iron);
            mb.Tube(muzzle - (muzzle - breech).normalized * 0.2f, muzzle, 0.19f, 0.19f, 12, Brass);
            mb.Tube(breech + new Vector3(0, 0.22f, 0.2f), Vector3.Lerp(breech, muzzle, 0.5f) + new Vector3(0, 0.22f, 0), 0.08f, 0.08f, 8, Copper);
            for (int i = 0; i < 5; i++) mb.Tube(new Vector3(1.3f + (i % 2) * 0.22f, 0, -1.2f + i * 0.2f), new Vector3(1.3f + (i % 2) * 0.22f, 0.42f, -1.2f + i * 0.2f), 0.09f, 0.03f, 6, Brass);
            return mb.Build("Howitzer");
        }

        void Works(int team)
        {
            float s = team == 0 ? -1f : 1f; // away from the middle
            float2 b = _objectives[Front.Home(team)];
            // smokestacks and halls along the back edge
            for (int i = 0; i < 4; i++)
                Chimney(b + new float2(s * 34f, -66f + i * 44f + _rng.NextFloat(-6f, 6f)), _rng.NextFloat(20f, 30f), _rng.NextFloat(1.7f, 2.3f), i % 2 == 0 ? Brick : Rust, 1f);
            for (int i = 0; i < 3; i++)
            {
                float2 p = b + new float2(s * 33f, -44f + i * 44f);
                float y = Height(p) - 0.3f;
                var mb = Mb(p);
                mb.Box(V(p, y + 3.2f), new Vector3(8f, 6.4f, 20f), Brick);
                // a sawtooth roof
                for (int k = -2; k <= 2; k++) mb.Box(V(p + new float2(0, k * 4f), y + 7f), new Vector3(8.4f, 0.3f, 4.6f), Quaternion.Euler(-24, 0, 0), Rust);
                for (int k = -2; k <= 2; k++) mb.Box(V(p + new float2(-s * 4.05f, k * 3.6f), y + 3.6f), new Vector3(0.1f, 1.6f, 1.6f), k % 2 == 0 ? Ember : BrickDark);
                Solid(p, new float2(8f, 20f), 0, y, y + 7f);
                Pipe(0.3f, Copper, V(p + new float2(-s * 4.4f, -9f), y), V(p + new float2(-s * 4.4f, -9f), y + 5.4f), V(p + new float2(-s * 4.4f, 9f), y + 5.4f), V(p + new float2(-s * 4.4f, 9f), y));
                Gear(new Vector3(p.x - s * 4.5f, y + 4.6f, p.y + 12f), Quaternion.Euler(0, 0, 90), 2.2f, 20, Brass);
            }
            // the yard: boilers, crates, a redoubt about the flag
            for (int i = 0; i < 4; i++)
            {
                float2 p = b + new float2(s * _rng.NextFloat(8f, 20f), (i - 1.5f) * 26f + _rng.NextFloat(-4f, 4f));
                if (Clear(p, 4f)) Boiler(p, _rng.NextFloat(0f, 360f), _rng.NextFloat(3.4f, 5f), _rng.NextFloat(0.9f, 1.3f), _rng.NextBool());
            }
            for (int i = 0; i < 26; i++)
            {
                float2 p = b + new float2(s * _rng.NextFloat(-18f, 24f), _rng.NextFloat(-70f, 70f));
                if (!Clear(p, 2f) || math.distance(p, b) < 6f || TrenchDistance(p) < 4f) continue;
                float k = _rng.NextFloat(0.7f, 1.2f), yaw = _rng.NextFloat(0f, 90f), y = Height(p) - 0.05f;
                Mb(p).Box(V(p, y + k * 0.5f), Vector3.one * k, Quaternion.Euler(0, yaw, 0), Wood);
                if (_rng.NextBool()) Mb(p).Box(V(p, y + k * 1.3f), Vector3.one * k * 0.7f, Quaternion.Euler(0, yaw + 20f, 0), WoodDark);
                Solid(p, k, yaw, y, y + k);
            }
            for (int i = 0; i < 40; i++)
            {
                float a = i / 40f * math.PI * 2f;
                if (i % 10 < 2) continue;
                float2 p = b + new float2(math.cos(a), math.sin(a)) * 9f;
                for (int row = 0; row < 3; row++)
                    Mb(p).Box(V(p, Height(p) + 0.1f + row * 0.2f), new Vector3(0.5f, 0.22f, 1.3f), Quaternion.Euler(0, -math.degrees(a) + (row % 2) * 4f, 0), Color.Lerp(Bag, C(0x4c4230), _rng.NextFloat()), 0.85f);
                Solid(p, new float2(0.6f, 1.3f), -math.degrees(a), Height(p), Height(p) + 0.65f);
            }
            float by = Height(b);
            Mb(b).Tube(V(b, by), V(b, by + 11f), 0.16f, 0.1f, 8, Iron);
            Mb(b).Tube(V(b, by), V(b, by + 0.6f), 1.1f, 0.8f, 10, Brass);
        }

        void NoMansLand()
        {
            // wire in front of each first line
            for (int side = -1; side <= 1; side += 2)
                for (float z = -MapView.HalfH + 8f; z < MapView.HalfH - 8f; z += 2.6f)
                {
                    if (noise.snoise(new float2(z * 0.05f, side * 9f)) > 0.25f) continue; // gaps
                    float2 p = new(side * (43f + noise.snoise(new float2(z * 0.02f, side)) * 4f), z);
                    Wire(p);
                }
            // steel hedgehogs
            for (int i = 0; i < 70; i++)
            {
                float2 p = new(_rng.NextFloat(-52f, 52f), _rng.NextFloat(-118f, 118f));
                if (NearObjective(p, 30f) || TrenchDistance(p) < 4f || !Clear(p, 2f)) continue;
                float y = Height(p);
                for (int k = 0; k < 3; k++)
                {
                    var q = Quaternion.Euler(0, k * 120f + i * 31f, 0) * Quaternion.Euler(0, 0, 52f);
                    Mb(p).Box(V(p, y + 0.55f), new Vector3(0.14f, 2.0f, 0.14f), q, i % 2 == 0 ? Rust : Iron);
                }
                Solid(p, new float2(1f, 1f), 0, y, y + 0.9f);
            }
            // dead trees
            for (int i = 0; i < 130; i++)
            {
                float2 p = new(_rng.NextFloat(-175f, 175f), _rng.NextFloat(-122f, 122f));
                if (NearObjective(p, 28f) || TrenchDistance(p) < 3f || !Clear(p, 1.5f)) continue;
                float y = Height(p) - 0.2f, h = _rng.NextFloat(2f, 7f), r = _rng.NextFloat(0.16f, 0.32f);
                Vector3 top = V(p + _rng.NextFloat2(-0.6f, 0.6f), y + h);
                var mb = Mb(p);
                mb.Tube(V(p, y), top, r, r * 0.4f, 6, WoodDark);
                for (int k = 0; k < 4; k++)
                {
                    Vector3 from = Vector3.Lerp(V(p, y), top, _rng.NextFloat(0.4f, 0.95f));
                    mb.Tube(from, from + new Vector3(_rng.NextFloat(-1f, 1f), _rng.NextFloat(0.1f, 1.1f), _rng.NextFloat(-1f, 1f)), r * 0.35f, 0.02f, 4, WoodDark, false);
                }
                Solid(p, 0.5f, 0, y, y + h * 0.9f);
            }
            // wrecked steam crawlers: something to hide behind out there
            for (int i = 0; i < 12; i++)
            {
                float2 p = new(_rng.NextFloat(-95f, 95f), _rng.NextFloat(-110f, 110f));
                if (NearObjective(p, 34f) || TrenchDistance(p) < 7f || !Clear(p, 6f)) continue;
                Crawler(p, _rng.NextFloat(0f, 360f));
            }
            // fallen walls here and there
            for (int i = 0; i < 16; i++)
            {
                float2 p = new(_rng.NextFloat(-100f, 100f), _rng.NextFloat(-115f, 115f));
                if (NearObjective(p, 34f) || TrenchDistance(p) < 6f || !Clear(p, 5f)) continue;
                float2 d = _rng.NextFloat2Direction();
                Wall(p - d * 4f, p + d * 4f, 2.2f, 1f, Brick);
            }
        }

        void Wire(float2 p)
        {
            float y = Height(p);
            var mb = Mb(p);
            float lean = _rng.NextFloat(-12f, 12f);
            mb.Box(V(p, y + 0.45f), new Vector3(0.07f, 1.1f, 0.07f), Quaternion.Euler(lean, 0, 24f), WoodDark);
            mb.Box(V(p, y + 0.45f), new Vector3(0.07f, 1.1f, 0.07f), Quaternion.Euler(lean, 0, -24f), WoodDark);
            Vector3 last = V(p, y + 0.8f);
            for (int k = 1; k <= 6; k++)
            {
                float t = k / 6f;
                float2 q = p + new float2(math.sin(t * 19f + p.y) * 0.45f, t * 2.6f);
                Vector3 next = V(q, Height(q) + 0.5f + math.cos(t * 23f + p.x) * 0.35f);
                mb.Tube(last, next, 0.014f, 0.014f, 3, Rust, false);
                last = next;
            }
            for (int dz = 0; dz < 3; dz++)
            for (int dx = -1; dx <= 1; dx++)
                _rough[MapView.Index(p + new float2(dx, dz))] = 3;
        }

        void Crawler(float2 p, float yaw)
        {
            float y = Height(p) - 0.3f;
            var mb = Mb(p);
            mb.M = Matrix4x4.TRS(V(p, y), Quaternion.Euler(_rng.NextFloat(-7f, 7f), yaw, _rng.NextFloat(-9f, 9f)), Vector3.one);
            for (int s = -1; s <= 1; s += 2)
            {
                mb.Box(new Vector3(s * 1.5f, 0.7f, 0), new Vector3(0.7f, 1.4f, 5.4f), Iron);
                for (int k = -3; k <= 3; k++) mb.Tube(new Vector3(s * 1.1f, 0.55f, k * 0.75f), new Vector3(s * 1.92f, 0.55f, k * 0.75f), 0.4f, 0.4f, 10, Rust);
            }
            mb.Box(new Vector3(0, 1.4f, -0.6f), new Vector3(2.4f, 1.3f, 3.6f), Rust);
            mb.Tube(new Vector3(0, 1.7f, 0.6f), new Vector3(0, 1.7f, 2.9f), 0.85f, 0.85f, 14, Copper);
            mb.Tube(new Vector3(0, 1.7f, 2.9f), new Vector3(0, 1.7f, 3.1f), 0.6f, 0.5f, 14, Iron);
            mb.Tube(new Vector3(0, 2.4f, 2.2f), new Vector3(0.1f, 4.0f, 2.2f), 0.24f, 0.3f, 10, Iron);
            mb.Box(new Vector3(0, 2.4f, -0.9f), new Vector3(1.5f, 0.8f, 1.6f), Iron);
            mb.Tube(new Vector3(0, 2.4f, -0.2f), new Vector3(0.3f, 2.2f, 2.4f), 0.11f, 0.09f, 8, Iron);
            mb.M = Matrix4x4.identity;
            Solid(p, new float2(3.7f, 5.4f), yaw, y, y + 2.1f);
            if (_rng.NextBool()) Chimneys.Add(new Vector4(p.x, y + 3.6f, p.y, 0.35f));
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
                Color c = Color.Lerp(Soil(p), C(0x49402f), math.smoothstep(2f, 45f, d));
                c = Color.Lerp(c, _c[VI(e.x, e.y)], k);
                c = Color.Lerp(c, C(0x2e281f), math.smoothstep(60f, 300f, d));
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

        /// <summary>The colour of untouched ground.</summary>
        Color Soil(float2 p)
        {
            float n = Fbm((p + _off) * 0.05f) * 0.5f + 0.5f;
            float grass = math.saturate((math.abs(p.x) - 120f) / 60f) * math.smoothstep(0.45f, 0.7f, Fbm((p - _off) * 0.06f) * 0.5f + 0.5f);
            return Color.Lerp(Color.Lerp(C(0x3a332a), C(0x50463a), n), C(0x55543c), grass);
        }

        void Ground()
        {
            const int size = 32;
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
                for (int s = -1; s <= 1; s += 2)
                {
                    float top = -1000f;
                    for (float d = 1f; d <= 2.6f; d += 0.5f)
                    {
                        float2 q = p + new float2(s * d, 0);
                        top = math.max(top, math.max(Height(q), _propTop[MapView.Index(q)]));
                    }
                    float up = top - h;
                    if (up > 0.65f && up < 1.55f) c |= s > 0 ? MapView.FromPlusX : MapView.FromMinusX;
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
