using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace LSBS
{
    /// <summary>Who's standing on the objective, and how many each side has left.</summary>
    [BurstCompile]
    public struct PresenceJob : IJob
    {
        [ReadOnly] public NativeArray<SlotState> Slots;
        [ReadOnly] public NativeArray<float2> Objectives;
        /// <summary>Team.Max on the objective, then Team.Max alive.</summary>
        public NativeArray<int> Out;
        public int Count;
        public float Radius2;

        public void Execute()
        {
            for (int i = 0; i < Out.Length; i++) Out[i] = 0;
            float2 centre = Objectives[Front.Centre];
            for (int i = 0; i < Count; i++)
            {
                var s = Slots[i];
                if (!s.Alive || s.Team >= Team.Max) continue;
                Out[Team.Max + s.Team]++;
                if (!s.Inside && math.distancesq(s.Pos.xz, centre) < Radius2) Out[s.Team]++;
            }
        }
    }

    public struct TankRest { public float2 Pos; public float Yaw, Y, Pitch, Roll, Gauge, Reach; }

    /// <summary>How tanks sit: each track across the high points under it.</summary>
    [BurstCompile]
    public struct TankRestJob : IJobParallelFor
    {
        public NativeArray<TankRest> Tanks;
        [ReadOnly] public MapView Map;

        public unsafe void Execute(int index)
        {
            var t = Tanks[index];
            const int n = 11;
            float reach = t.Reach, gauge = t.Gauge;
            var s = new TankState { Pos = new float3(t.Pos.x, 0, t.Pos.y), Yaw = t.Yaw };
            float* h = stackalloc float[n];
            float hl = 0, hr = 0, sl = 0, sr = 0;
            for (int side = 0; side < 2; side++)
            {
                for (int i = 0; i < n; i++)
                {
                    float z = math.lerp(-reach, reach, i / (n - 1f));
                    // the ends of the belt turn up
                    h[i] = Map.Height(s.World(new float2(side == 0 ? -gauge : gauge, z))) - math.max(0f, math.abs(z) - (reach - 0.9f)) * 0.55f;
                }
                // the edge of what's under it that crosses beneath its middle
                float best = -1e9f, slope = 0f;
                for (int i = 0; i <= n / 2; i++)
                for (int j = n / 2; j < n; j++)
                {
                    float zi = math.lerp(-reach, reach, i / (n - 1f)), zj = math.lerp(-reach, reach, j / (n - 1f));
                    float at = i == j ? h[i] : math.lerp(h[i], h[j], -zi / math.max(0.01f, zj - zi));
                    if (at > best) { best = at; slope = i == j ? 0f : (h[j] - h[i]) / (zj - zi); }
                }
                if (side == 0) { hl = best; sl = slope; } else { hr = best; sr = slope; }
            }
            t.Y = (hl + hr) * 0.5f;
            t.Pitch = -math.atan((sl + sr) * 0.5f);
            t.Roll = math.atan2(hr - hl, gauge * 2f);
            Tanks[index] = t;
        }
    }

    public struct GunSearch { public float3 From; public quaternion Inverse; public byte Team; public int Tank, Side, Found, Gen; }

    /// <summary>The nearest of the enemy each side gun can be brought to bear on and can see.</summary>
    [BurstCompile]
    public struct GunSearchJob : IJobParallelFor
    {
        public NativeArray<GunSearch> Guns;
        [ReadOnly] public NativeArray<SlotState> Slots;
        [ReadOnly] public Grid All;
        [ReadOnly] public MapView Map;
        public float Range, Arc;

        public void Execute(int index)
        {
            var g = Guns[index];
            var theirs = All;
            // the nearest few first, and only then whether they're in sight
            var bestD = new FixedList64Bytes<float>();
            var bestI = new FixedList64Bytes<int>();
            const int K = 6;
            int2 lo = theirs.At(g.From.xz - Range), hi = theirs.At(g.From.xz + Range);
            for (int y = lo.y; y <= hi.y; y++)
            for (int x = lo.x; x <= hi.x; x++)
            {
                int k = y * theirs.W + x;
                for (int n = theirs.Start[k]; n < theirs.Start[k + 1]; n++)
                {
                    int slot = theirs.Items[n];
                    var s = Slots[slot];
                    if (!s.Alive || s.Team == g.Team) continue;
                    float d = math.distancesq(s.Pos, g.From);
                    if (d > Range * Range || bestD.Length == K && d >= bestD[K - 1]) continue;
                    float3 l = math.mul(g.Inverse, s.Pos + new float3(0, 1.1f, 0) - g.From);
                    if (math.abs(math.atan2(l.x, l.z)) > Arc || math.abs(math.atan2(l.y, math.length(l.xz))) > 0.4f) continue;
                    int at = bestD.Length;
                    while (at > 0 && bestD[at - 1] > d) at--;
                    if (bestD.Length == K) { bestD.RemoveAt(K - 1); bestI.RemoveAt(K - 1); }
                    bestD.InsertRange(at, 1); bestD[at] = d;
                    bestI.InsertRange(at, 1); bestI[at] = slot;
                }
            }
            g.Found = -1;
            for (int i = 0; i < bestI.Length; i++)
            {
                var s = Slots[bestI[i]];
                if (!Map.Sees(g.From, s.Pos + new float3(0, 1.4f, 0))) continue;
                g.Found = bestI[i];
                g.Gen = s.Gen;
                break;
            }
            Guns[index] = g;
        }
    }

    /// <summary>The same mesh in many places, drawn together.</summary>
    public class MeshBatch
    {
        readonly Dictionary<(Mesh, Material), List<Matrix4x4>> _lists = new();

        public void Add(Mesh mesh, Material mat, in Matrix4x4 at)
        {
            if (mesh == null) return;
            if (!_lists.TryGetValue((mesh, mat), out var l)) _lists[(mesh, mat)] = l = new List<Matrix4x4>();
            l.Add(at);
        }

        public void Draw()
        {
            foreach (var kv in _lists)
            {
                var l = kv.Value;
                if (l.Count == 0) continue;
                var rp = new RenderParams(kv.Key.Item2) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true, worldBounds = new Bounds(Vector3.zero, new Vector3(1000, 300, 1000)) };
                for (int i = 0; i < l.Count; i += 400) Graphics.RenderMeshInstanced(rp, kv.Key.Item1, 0, l, Mathf.Min(400, l.Count - i), i);
                l.Clear();
            }
        }
    }
}
