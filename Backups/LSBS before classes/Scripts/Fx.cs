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
        const int Max = 8192;
        readonly Material _material;
        NativeList<Particle> _live = new(Max, Allocator.Persistent);
        NativeList<PuffGpu> _draw = new(Max, Allocator.Persistent);
        readonly GraphicsBuffer _buffer = new(GraphicsBuffer.Target.Structured, Max, PuffGpu.Stride);
        readonly MaterialPropertyBlock _block = new();
        readonly List<Vector4> _chimneys;
        Random _rng = new(99);
        float _chimneyClock;
        public int Count => _live.Length;

        public Fx(Material material, List<Vector4> chimneys) { _material = material; _chimneys = chimneys; }

        void Add(in Particle p) { if (_live.Length < Max) _live.Add(p); }

        static float4 Lin(int hex, float a)
        {
            var c = new Color(((hex >> 16) & 255) / 255f, ((hex >> 8) & 255) / 255f, (hex & 255) / 255f).linear;
            return new float4(c.r, c.g, c.b, a);
        }

        static readonly float4 Smoke = Lin(0x8a8074, 0.34f), Dirt = Lin(0x3a2c1e, 0.85f), Soot = Lin(0x1b1815, 0.5f), Steam = Lin(0xb8b0a2, 0.3f),
            Blood = Lin(0x3c0806, 0.9f), Flash = new(2.6f, 1.3f, 0.4f, 1f), Tracer = new(1.4f, 0.8f, 0.3f, 0.7f);

        public void Shot(in ShotEvent s, Vector3 cam)
        {
            float near = math.min(math.distance(s.From, cam), math.distance(s.To, cam));
            float3 d = s.To - s.From;
            float len = math.length(d);
            if (len < 0.01f) return;
            d /= len;
            if (near < 200f)
            {
                Add(new Particle { Pos = s.From, Size = 0.32f, Life = 0.06f, Shape = 1, Additive = 1, Color = Flash, Seed = _rng.NextFloat() });
                // a round crossing the air in a twentieth of a second
                float run = math.min(len, 14f);
                Add(new Particle { Pos = s.From, Vel = d * (len / 0.07f), Size = 0.014f, Life = 0.07f, Length = run, Shape = 2, Additive = 1, Color = Tracer, Seed = _rng.NextFloat() });
            }
            if (math.distance(s.From, cam) < 130f)
                Add(new Particle { Pos = s.From + d * 0.15f, Vel = d * 1.4f + new float3(0.4f, 0.35f, 0), Size = 0.18f, Grow = 0.75f, Life = _rng.NextFloat(1.0f, 1.9f), Drag = 1.6f, Color = Smoke, Seed = _rng.NextFloat() });
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

        /// <summary>A pack boiler letting go as its owner falls.</summary>
        public void Burst(float3 at, Vector3 cam)
        {
            if (math.distance(at, cam) > 90f) return;
            for (int i = 0; i < 5; i++)
                Add(new Particle { Pos = at + _rng.NextFloat3(-0.1f, 0.1f), Vel = _rng.NextFloat3(-0.7f, 0.7f) + new float3(0, 1.3f, 0), Size = 0.16f, Grow = 0.9f, Life = _rng.NextFloat(0.8f, 1.8f), Drag = 1.2f, Color = Steam, Seed = _rng.NextFloat() });
        }

        public void Draw(float dt, Camera cam)
        {
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

    /// <summary>Rifle cracks and the rumble of the line, made from noise.</summary>
    public class Sfx
    {
        readonly AudioSource[] _voices = new AudioSource[24];
        readonly AudioClip[] _near = new AudioClip[3], _far = new AudioClip[3];
        int _next;
        float _budget;

        public Sfx(Transform parent)
        {
            var rng = new Random(7);
            for (int i = 0; i < 3; i++)
            {
                _near[i] = Crack(ref rng, 0.5f, 34f, 0.55f, 0.9f);
                _far[i] = Crack(ref rng, 1.1f, 7f, 0.08f, 0.8f);
            }
            for (int i = 0; i < _voices.Length; i++)
            {
                var go = new GameObject("Voice");
                go.transform.SetParent(parent, false);
                var a = go.AddComponent<AudioSource>();
                a.spatialBlend = 1f; a.rolloffMode = AudioRolloffMode.Linear; a.minDistance = 4f; a.maxDistance = 190f; a.dopplerLevel = 0f; a.playOnAwake = false;
                _voices[i] = a;
            }
        }

        static AudioClip Crack(ref Random rng, float seconds, float decay, float bright, float gain)
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
                phase += 2f * math.PI * math.lerp(48f, 130f, math.exp(-t * 22f)) / rate;
                float body = math.sin(phase) * math.exp(-t * 16f) * 0.5f;
                d[i] = math.clamp((low * math.exp(-t * decay) + boom * 5f * math.exp(-t * decay * 0.35f) * 0.5f + body) * gain * math.saturate(t * 3000f), -1f, 1f);
            }
            var c = AudioClip.Create("Crack", n, 1, rate, false);
            c.SetData(d, 0);
            return c;
        }

        public void Tick(float dt) => _budget = math.min(10f, _budget + dt * 45f);

        public void Shot(float3 at, Vector3 ear, bool mine)
        {
            float d = math.distance(at, ear);
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
            a.Stop();
            a.transform.position = at;
            a.clip = (d < 45f ? _near : _far)[UnityEngine.Random.Range(0, 3)];
            a.volume = mine ? 0.8f : d < 45f ? 0.5f : 0.4f;
            a.pitch = UnityEngine.Random.Range(0.88f, 1.1f);
            a.Play();
        }
    }
}
