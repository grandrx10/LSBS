using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Random = Unity.Mathematics.Random;

namespace LSBS
{
    /// <summary>One card of smoke, dirt, steam, flash or tracer.</summary>
    public struct Particle
    {
        public float3 Pos, Vel;
        public float Size, Grow, Life, Age, Drag, Gravity, Length, Seed, Shape, Additive;
        public float4 Color;
    }

    public struct PuffGpu
    {
        public float3 Pos; public float Size;
        public float3 Dir; public float Length;
        public float4 Color;
        public float Seed, Additive, Shape, Pad;
        public const int Stride = 64;
    }

    public class Fx : IDisposable
    {
        const int Max = 16384;
        readonly Material _material;
        NativeList<Particle> _live = new(Max, Allocator.Persistent);
        NativeList<PuffGpu> _draw = new(Max, Allocator.Persistent);
        readonly GraphicsBuffer _buffer = new(GraphicsBuffer.Target.Structured, Max, PuffGpu.Stride);
        readonly MaterialPropertyBlock _block = new();
        readonly List<Vector4> _chimneys;
        Random _rng = new(99);
        float _chimneyClock;
        /// <summary>How hard the ground's shaking where the camera is.</summary>
        public float Shake;
        public int Count => _live.Length;

        public Fx(Material material, List<Vector4> chimneys) { _material = material; _chimneys = chimneys; }

        void Add(in Particle p) { if (_live.Length < Max) _live.Add(p); }

        static float4 Lin(int hex, float a)
        {
            var c = new Color(((hex >> 16) & 255) / 255f, ((hex >> 8) & 255) / 255f, (hex & 255) / 255f).linear;
            return new float4(c.r, c.g, c.b, a);
        }

        static readonly float4 Smoke = Lin(0x8a8074, 0.34f), Dirt = Lin(0x3a2c1e, 0.85f), Soot = Lin(0x1b1815, 0.5f), BlastSmoke = Lin(0x2e2822, 0.8f), Dust = Lin(0x6e604c, 0.4f),
            Blood = Lin(0x3c0806, 0.9f), Flash = new(2.6f, 1.3f, 0.4f, 1f), Fire = new(0.85f, 0.27f, 0.05f, 0.75f), Tracer = new(1.4f, 0.8f, 0.3f, 0.7f), Spark = new(2.2f, 1.2f, 0.4f, 0.9f), ShellGlow = new(3.2f, 2.5f, 0.25f, 1f);

        public void Shot(in ShotEvent s, Vector3 cam)
        {
            float near = math.min(math.distance(s.From, cam), math.distance(s.To, cam));
            float3 d = s.To - s.From;
            float len = math.length(d);
            if (len < 0.01f) return;
            d /= len;
            bool shotgun = s.Kind == ShotKind.Shotgun;
            if (near < 200f)
            {
                Add(new Particle { Pos = s.From, Size = shotgun ? 0.45f : 0.32f, Life = 0.06f, Shape = 1, Additive = 1, Color = Flash, Seed = _rng.NextFloat() });
                // a round crossing the air in a twentieth of a second
                for (int i = 0; i < (shotgun ? 4 : 1); i++)
                {
                    float3 way = shotgun ? math.normalize(d + _rng.NextFloat3(-0.05f, 0.05f)) : d;
                    Add(new Particle { Pos = s.From, Vel = way * (len / 0.07f), Size = shotgun ? 0.008f : 0.014f, Life = 0.07f, Length = math.min(len, shotgun ? 5f : 14f), Shape = 2, Additive = 1, Color = Tracer, Seed = _rng.NextFloat() });
                }
            }
            if (math.distance(s.From, cam) < 130f)
                Add(new Particle { Pos = s.From + d * 0.15f, Vel = d * 1.4f + new float3(0.4f, 0.35f, 0), Size = shotgun ? 0.26f : 0.18f, Grow = shotgun ? 1f : 0.75f, Life = _rng.NextFloat(1.0f, 1.9f), Drag = 1.6f, Color = Smoke, Seed = _rng.NextFloat() });
        }

        /// <summary>A miss into the ground, or a hit.</summary>
        public void Impact(float3 at, float3 dir, bool flesh, Vector3 cam)
        {
            if (math.distance(at, cam) > 120f) return;
            int n = flesh ? 3 : 4;
            for (int i = 0; i < n; i++)
                Add(new Particle
                {
                    Pos = at, Vel = (flesh ? dir * 1.5f : -dir * 0.8f + new float3(0, 2.4f, 0)) + _rng.NextFloat3(-1f, 1f), Size = flesh ? 0.07f : 0.14f, Grow = flesh ? 0.3f : 0.8f,
                    Life = _rng.NextFloat(0.35f, 0.8f), Drag = 2f, Gravity = 4f, Color = flesh ? Blood : Dirt, Seed = _rng.NextFloat(),
                });
        }

        /// <summary>Where a limb's come away.</summary>
        public void Gore(Vector3 at, Vector3 vel)
        {
            for (int i = 0; i < 7; i++)
                Add(new Particle { Pos = at, Vel = (float3)vel + _rng.NextFloat3(-2.2f, 2.2f) + new float3(0, 1f, 0), Size = _rng.NextFloat(0.06f, 0.16f), Grow = 0.35f, Life = _rng.NextFloat(0.5f, 1.2f), Drag = 1.2f, Gravity = 6f, Color = Blood, Seed = _rng.NextFloat() });
        }

        /// <summary>A grenade, or a shell: flash, fire, a column of earth, shrapnel and smoke.</summary>
        public void Explosion(in BlastEvent b, Vector3 cam)
        {
            if (b.Wreck)
            {
                // a gun knocked to bits
                for (int i = 0; i < 14; i++)
                    Add(new Particle { Pos = b.At, Vel = _rng.NextFloat3(-5f, 5f) + new float3(0, 4f, 0), Size = _rng.NextFloat(0.05f, 0.14f), Life = _rng.NextFloat(0.6f, 1.4f), Drag = 0.6f, Gravity = 9f, Color = Soot * new float4(1, 1, 1, 1.8f), Seed = _rng.NextFloat() });
                for (int i = 0; i < 8; i++)
                    Add(new Particle { Pos = b.At, Vel = _rng.NextFloat3(-9f, 9f) + new float3(0, 4f, 0), Size = 0.018f, Length = 0.7f, Life = 0.35f, Drag = 2f, Gravity = 8f, Shape = 2, Additive = 1, Color = Spark, Seed = _rng.NextFloat() });
                return;
            }
            float far = math.distance(b.At, cam);
            float k = b.Big ? 1.7f : 1f;
            Shake = math.min(1.6f, Shake + (b.Big ? 1.5f : 0.8f) * math.saturate(1f - far / (b.Big ? 90f : 45f)));
            if (far > 420f) return;
            int lots = far < 120f ? 1 : 2; // fewer, further off
            float3 up = new(0, 1, 0);
            Add(new Particle { Pos = b.At + up * 0.8f * k, Size = 2.6f * k, Grow = 6f, Life = 0.09f, Shape = 1, Additive = 1, Color = Flash * new float4(0.55f, 0.5f, 0.45f, 0.8f), Seed = _rng.NextFloat() });
            for (int i = 0; i < 9; i += lots)
                Add(new Particle { Pos = b.At + up * 0.6f * k + _rng.NextFloat3(-0.6f, 0.6f) * k, Vel = (_rng.NextFloat3(-3f, 3f) + up * 3f) * k, Size = 0.7f * k, Grow = 3.2f * k, Life = _rng.NextFloat(0.2f, 0.42f), Drag = 4f, Additive = 1, Color = Fire, Seed = _rng.NextFloat() });
            // the earth it throws up
            for (int i = 0; i < 22 * k; i += lots)
            {
                float3 v = new float3(_rng.NextFloat(-3.2f, 3.2f), _rng.NextFloat(6f, 15f), _rng.NextFloat(-3.2f, 3.2f)) * math.sqrt(k);
                Add(new Particle { Pos = b.At + up * 0.2f, Vel = v, Size = _rng.NextFloat(0.3f, 0.8f) * k, Grow = 0.7f * k, Life = _rng.NextFloat(0.9f, 1.8f) * math.sqrt(k), Drag = 0.7f, Gravity = 10f, Color = Dirt, Seed = _rng.NextFloat() });
            }
            // shrapnel
            for (int i = 0; i < 20 * k; i += lots)
            {
                float3 d = math.normalize(_rng.NextFloat3(-1f, 1f) + new float3(0, 0.35f, 0));
                Add(new Particle { Pos = b.At + up * 0.5f, Vel = d * _rng.NextFloat(30f, 60f), Size = 0.022f, Length = 1.6f, Life = _rng.NextFloat(0.15f, 0.36f), Drag = 2.5f, Gravity = 6f, Shape = 2, Additive = 1, Color = Spark, Seed = _rng.NextFloat() });
            }
            // a skirt of dust, and the smoke left hanging
            for (int i = 0; i < 12; i += lots)
            {
                float a = i / 12f * math.PI * 2f;
                Add(new Particle { Pos = b.At + up * 0.3f, Vel = new float3(math.cos(a), 0.12f, math.sin(a)) * 9f * k, Size = 0.7f * k, Grow = 2.2f * k, Life = _rng.NextFloat(0.9f, 1.6f), Drag = 3.2f, Color = Dust, Seed = _rng.NextFloat() });
            }
            for (int i = 0; i < 8 * k; i += lots)
                Add(new Particle { Pos = b.At + up * _rng.NextFloat(0.5f, 2.5f) * k, Vel = _rng.NextFloat3(-0.8f, 0.8f) + new float3(0.5f, 1.5f, 0), Size = 1.1f * k, Grow = 0.9f * k, Life = _rng.NextFloat(2.5f, 5f), Drag = 0.8f, Color = BlastSmoke, Seed = _rng.NextFloat() });
        }

        /// <summary>A shell on its way over: where it is at u (0 at the gun, 1 landing).</summary>
        public static float3 Arc(float3 from, float3 to, float u)
        {
            float3 p = math.lerp(from, to, u);
            p.y += (26f + 0.2f * math.distance(from, to)) * 4f * u * (1f - u);
            return p;
        }

        float _tracerClock;
        public bool TracerTick(float dt)
        {
            _tracerClock += dt;
            if (_tracerClock < 0.016f) return false;
            _tracerClock = 0f;
            return true;
        }

        /// <summary>A tank's shell, where it is.</summary>
        public void ShellPoint(float3 p, float3 vel)
        {
            float3 d = math.normalizesafe(vel);
            Add(new Particle { Pos = p, Vel = d * 0.02f, Size = 0.16f, Length = 4f, Life = 0.2f, Shape = 2, Additive = 1, Color = ShellGlow, Seed = _rng.NextFloat() });
        }

        public void CannonFire(float3 at, float3 d, Vector3 cam)
        {
            float far = math.distance(at, cam);
            Shake = math.min(1.4f, Shake + 0.6f * math.saturate(1f - far / 40f));
            if (far > 380f) return;
            Add(new Particle { Pos = at, Size = 1.3f, Life = 0.09f, Shape = 1, Additive = 1, Color = Flash, Seed = _rng.NextFloat() });
            for (int i = 0; i < 12; i++)
                Add(new Particle { Pos = at, Vel = d * _rng.NextFloat(2f, 12f) + _rng.NextFloat3(-1.6f, 1.6f), Size = 0.4f, Grow = 1.5f, Life = _rng.NextFloat(1.2f, 2.8f), Drag = 2.2f, Color = Smoke, Seed = _rng.NextFloat() });
            for (int i = 0; i < 8; i++)
            {
                float a = i * 0.785f;
                Add(new Particle { Pos = at - new float3(0, 1.4f, 0), Vel = new float3(math.cos(a), 0.1f, math.sin(a)) * 5f, Size = 0.5f, Grow = 1.6f, Life = 1.2f, Drag = 3f, Color = Dust, Seed = _rng.NextFloat() });
            }
        }

        /// <summary>Out of a tank's funnels; more when it's working, and a wreck burns.</summary>
        public void Funnel(float3 at, float hard, bool burning)
        {
            Add(new Particle { Pos = at + _rng.NextFloat3(-0.1f, 0.1f), Vel = new float3(0.5f, 1.6f + hard, 0.1f), Size = burning ? 0.7f : 0.2f, Grow = burning ? 1.1f : 0.5f, Life = burning ? 5f : 1.6f + hard, Drag = 0.4f, Color = burning ? BlastSmoke : Soot * new float4(1, 1, 1, 0.5f + hard * 0.5f), Seed = _rng.NextFloat() });
            if (burning && _rng.NextFloat() < 0.6f)
                Add(new Particle { Pos = at + _rng.NextFloat3(-0.8f, 0.8f) - new float3(0, 0.6f, 0), Vel = new float3(0, 2f, 0), Size = 0.4f, Grow = 0.3f, Life = 0.5f, Additive = 1, Color = Fire, Seed = _rng.NextFloat() });
        }

        public void TrackDust(float3 at, float speed)
        {
            Add(new Particle { Pos = at + _rng.NextFloat3(-0.4f, 0.4f), Vel = new float3(0.3f, 0.6f, 0), Size = 0.35f, Grow = 0.9f, Life = 1.3f, Drag = 1f, Color = Dust * new float4(1, 1, 1, 0.35f * math.saturate(speed / 3f)), Seed = _rng.NextFloat() });
        }

        public void ShellTracer(float3 from, float3 to, float u)
        {
            float3 p = Arc(from, to, u), d = math.normalizesafe(Arc(from, to, u + 0.01f) - p);
            Add(new Particle { Pos = p, Vel = d * 0.02f, Size = 0.22f, Length = 7f, Life = 0.28f, Shape = 2, Additive = 1, Color = ShellGlow, Seed = _rng.NextFloat() });
            Add(new Particle { Pos = p, Vel = d * 0.02f, Size = 0.9f, Life = 0.05f, Shape = 0, Additive = 1, Color = ShellGlow * new float4(0.8f, 0.8f, 0.8f, 0.9f), Seed = _rng.NextFloat() });
        }

        /// <summary>One of a side's guns, at home.</summary>
        public void GunReport(float3 at, float toward, Vector3 cam)
        {
            if (math.distance(at, cam) > 420f) return;
            float3 muzzle = at + new float3(toward * 2.4f, 2.5f, 0);
            Add(new Particle { Pos = muzzle, Size = 1.6f, Life = 0.1f, Shape = 1, Additive = 1, Color = Flash, Seed = _rng.NextFloat() });
            for (int i = 0; i < 7; i++)
                Add(new Particle { Pos = muzzle, Vel = new float3(toward * _rng.NextFloat(3f, 9f), _rng.NextFloat(1f, 4f), _rng.NextFloat(-1.5f, 1.5f)), Size = 0.6f, Grow = 1.3f, Life = _rng.NextFloat(1.5f, 3f), Drag = 1.8f, Color = Smoke, Seed = _rng.NextFloat() });
        }

        public void Draw(float dt, Camera cam)
        {
            Shake = math.max(0f, Shake - dt * 2.2f);
            _chimneyClock += dt;
            if (_chimneyClock > 0.22f)
            {
                _chimneyClock = 0f;
                foreach (var c in _chimneys)
                {
                    if (_rng.NextFloat() > 0.75f) continue;
                    float k = c.w;
                    Add(new Particle { Pos = new float3(c.x, c.y, c.z) + _rng.NextFloat3(-0.3f, 0.3f), Vel = new float3(_rng.NextFloat(1.2f, 2.2f), _rng.NextFloat(2f, 3.2f), _rng.NextFloat(0.2f, 0.8f)) * (0.5f + k * 0.5f), Size = 0.8f + k, Grow = 1.1f * k + 0.2f, Life = _rng.NextFloat(6f, 10f) * (0.4f + 0.6f * k), Drag = 0.08f, Color = Soot * new float4(1, 1, 1, 0.55f + 0.45f * k), Seed = _rng.NextFloat() });
                }
                // drifting battlefield haze about the camera
                float2 at = new float2(cam.transform.position.x, cam.transform.position.z) + _rng.NextFloat2(-60f, 60f);
                Add(new Particle { Pos = new float3(at.x, _rng.NextFloat(1.5f, 4f), at.y), Vel = new float3(0.9f, 0, 0.3f), Size = _rng.NextFloat(5f, 9f), Grow = 0.3f, Life = 9f, Color = Smoke * new float4(0.75f, 0.72f, 0.68f, 0.22f), Seed = _rng.NextFloat() });
            }
            _draw.Clear();
            new StepJob { Live = _live, Draw = _draw, Dt = dt, Cam = cam.transform.position, Forward = cam.transform.forward }.Run();
            int n = _draw.Length;
            if (n == 0) return;
            _buffer.SetData(_draw.AsArray(), 0, 0, n);
            _block.SetBuffer("_Puffs", _buffer);
            var rp = new RenderParams(_material) { matProps = _block, shadowCastingMode = ShadowCastingMode.Off, worldBounds = new Bounds(Vector3.zero, Vector3.one * 3000f) };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 6, n);
        }

        [BurstCompile]
        struct StepJob : IJob
        {
            public NativeList<Particle> Live;
            public NativeList<PuffGpu> Draw;
            public float Dt;
            public float3 Cam, Forward;

            struct ByDepth : IComparer<PuffGpu>
            {
                public float3 Cam, Forward;
                public int Compare(PuffGpu a, PuffGpu b) => math.dot(b.Pos - Cam, Forward).CompareTo(math.dot(a.Pos - Cam, Forward));
            }

            public void Execute()
            {
                for (int i = Live.Length - 1; i >= 0; i--)
                {
                    var p = Live[i];
                    p.Age += Dt;
                    if (p.Age >= p.Life) { Live.RemoveAtSwapBack(i); continue; }
                    p.Vel *= math.exp(-p.Drag * Dt);
                    p.Vel.y -= p.Gravity * Dt;
                    p.Pos += p.Vel * Dt;
                    p.Size += p.Grow * Dt;
                    Live[i] = p;
                    float u = p.Age / p.Life;
                    float fade = p.Shape > 0.5f ? 1f - u * u : math.saturate(u * 8f) * (1f - u) * (1f - u * 0.3f);
                    if (math.dot(p.Pos - Cam, Forward) < -p.Size - p.Length) continue;
                    var c = p.Color;
                    c.w *= fade;
                    Draw.Add(new PuffGpu { Pos = p.Pos, Size = p.Size, Dir = p.Length > 0 ? math.normalizesafe(p.Vel) : 0, Length = p.Length, Color = c, Seed = p.Seed, Additive = p.Additive, Shape = p.Shape });
                }
                Draw.Sort(new ByDepth { Cam = Cam, Forward = Forward });
            }
        }

        public void Dispose() { _live.Dispose(); _draw.Dispose(); _buffer.Dispose(); }
    }

    /// <summary>Cracks, booms and whistles, made from noise.</summary>
    public class Sfx
    {
        readonly AudioSource[] _voices = new AudioSource[24], _heavy = new AudioSource[10];
        readonly AudioClip[] _near = new AudioClip[3], _far = new AudioClip[3], _shotgun = new AudioClip[2], _boom = new AudioClip[2];
        readonly AudioClip _whistle, _thud;
        int _next, _nextHeavy;
        float _budget;

        public Sfx(Transform parent)
        {
            var rng = new Random(7);
            for (int i = 0; i < 3; i++)
            {
                _near[i] = Crack(ref rng, 0.5f, 34f, 0.55f, 0.9f, 130f);
                _far[i] = Crack(ref rng, 1.1f, 7f, 0.08f, 0.8f, 130f);
            }
            for (int i = 0; i < 2; i++)
            {
                _shotgun[i] = Crack(ref rng, 0.7f, 20f, 0.35f, 1.1f, 95f);
                _boom[i] = Crack(ref rng, 2.6f, 2.6f, 0.12f, 1.4f, 70f);
            }
            _thud = Crack(ref rng, 1.8f, 4f, 0.03f, 0.9f, 60f);
            _whistle = Whistle(2.1f);
            _Fill(_voices, parent, 190f);
            _Fill(_heavy, parent, 420f);
        }

        static void _Fill(AudioSource[] a, Transform parent, float reach)
        {
            for (int i = 0; i < a.Length; i++)
            {
                var go = new GameObject("Voice");
                go.transform.SetParent(parent, false);
                var s = go.AddComponent<AudioSource>();
                s.spatialBlend = 1f; s.rolloffMode = AudioRolloffMode.Linear; s.minDistance = 4f; s.maxDistance = reach; s.dopplerLevel = 0f; s.playOnAwake = false;
                a[i] = s;
            }
        }

        static AudioClip Crack(ref Random rng, float seconds, float decay, float bright, float gain, float pitch)
        {
            const int rate = 44100;
            int n = (int)(seconds * rate);
            var d = new float[n];
            float low = 0f, boom = 0f, phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float white = rng.NextFloat(-1f, 1f);
                low += (white - low) * math.lerp(0.05f, 0.8f, bright * math.exp(-t * 18f));
                boom += (white - boom) * 0.02f;
                phase += 2f * math.PI * math.lerp(pitch * 0.37f, pitch, math.exp(-t * 22f)) / rate;
                float body = math.sin(phase) * math.exp(-t * math.min(16f, decay * 2f)) * 0.5f;
                d[i] = math.clamp((low * math.exp(-t * decay) + boom * 5f * math.exp(-t * decay * 0.35f) * 0.5f + body) * gain * math.saturate(t * 3000f), -1f, 1f);
            }
            var c = AudioClip.Create("Crack", n, 1, rate, false);
            c.SetData(d, 0);
            return c;
        }

        static AudioClip Whistle(float seconds)
        {
            const int rate = 44100;
            int n = (int)(seconds * rate);
            var d = new float[n];
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float u = (float)i / n;
                phase += 2f * math.PI * math.lerp(1500f, 520f, u * u) / rate;
                d[i] = (math.sin(phase) + 0.3f * math.sin(phase * 2.01f)) * 0.3f * math.smoothstep(0f, 0.5f, u) * (1f - math.smoothstep(0.97f, 1f, u));
            }
            var c = AudioClip.Create("Whistle", n, 1, rate, false);
            c.SetData(d, 0);
            return c;
        }

        public void Tick(float dt) => _budget = math.min(10f, _budget + dt * 45f);

        public void Shot(in ShotEvent s, Vector3 ear, bool mine)
        {
            float d = math.distance(s.From, ear);
            if (d > 185f) return;
            if (!mine)
            {
                if (_budget < 1f) return;
                // the far ones thin out first
                if (d > 40f && UnityEngine.Random.value < (d - 40f) / 170f) return;
                _budget -= 1f;
            }
            var a = _voices[_next];
            _next = (_next + 1) % _voices.Length;
            var clip = d >= 45f ? _far[UnityEngine.Random.Range(0, 3)] : s.Kind == ShotKind.Shotgun ? _shotgun[UnityEngine.Random.Range(0, 2)] : _near[UnityEngine.Random.Range(0, 3)];
            Play(a, s.From, clip, (mine ? 0.8f : d < 45f ? 0.5f : 0.4f) * Options.Guns, UnityEngine.Random.Range(0.88f, 1.1f));
        }

        public void Blast(in BlastEvent b, Vector3 ear)
        {
            float d = math.distance(b.At, ear);
            if (b.Cannon) { var v = _heavy[_nextHeavy]; _nextHeavy = (_nextHeavy + 1) % _heavy.Length; if (d < 400f) Play(v, b.At, _boom[UnityEngine.Random.Range(0, 2)], 0.8f * Options.Blasts, 1.35f); return; }
            if (b.Wreck || b.Whistle && d > 90f || d > 410f) return;
            var a = _heavy[_nextHeavy];
            _nextHeavy = (_nextHeavy + 1) % _heavy.Length;
            if (b.Whistle) Play(a, b.At + new float3(0, 10f, 0), _whistle, 0.35f * Options.Blasts, 1f);
            else if (b.Report) Play(a, b.At, _thud, 0.5f * Options.Blasts, UnityEngine.Random.Range(0.85f, 1.05f));
            else Play(a, b.At, _boom[UnityEngine.Random.Range(0, 2)], (b.Big ? 1f : 0.75f) * Options.Blasts, (b.Big ? 0.85f : 1.15f) * UnityEngine.Random.Range(0.93f, 1.07f));
        }

        static void Play(AudioSource a, float3 at, AudioClip clip, float volume, float pitch)
        {
            a.Stop();
            a.transform.position = at;
            a.clip = clip;
            a.volume = volume;
            a.pitch = pitch;
            a.Play();
        }
    }
}
