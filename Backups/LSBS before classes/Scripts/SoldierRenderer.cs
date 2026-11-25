using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace LSBS
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SoldierInstance
    {
        public float3 Pos;
        public float Yaw;
        public uint4 Frames;
        public float4 Blends;
        public float4 Misc;
        public const int Stride = 64;
        public const int Captain = 1, NoHead = 2, Mine = 4;
    }

    public struct ClipTable
    {
        public int Start, Frames;
        public bool Loop;
    }

    /// <summary>Sorts everybody in view into near and far lists for the camera, and those near enough to cast shadows.</summary>
    [BurstCompile]
    public partial struct CullJob : IJobEntity
    {
        [ReadOnly] public NativeArray<ClipTable> Clips;
        [ReadOnly] public NativeArray<Squad> Squads;
        [ReadOnly] public NativeArray<float4> Planes;
        public float3 Camera;
        public float NearSq, ShadowSq, FarSq;
        public int PlayerSquad, HideHead; // slot whose head is hidden (the person looking out of it)
        public NativeList<SoldierInstance>.ParallelWriter Near0, Near1, Far0, Far1, Shadow0, Shadow1;

        uint2 Frame(byte clip, float time, out float t)
        {
            var c = Clips[clip];
            float f = time * SoldierData.Fps;
            if (c.Loop) f -= math.floor(f / c.Frames) * c.Frames;
            else f = math.clamp(f, 0f, c.Frames - 1);
            int i = math.min((int)f, c.Frames - 1);
            t = f - i;
            int n = c.Loop ? (i + 1) % c.Frames : math.min(i + 1, c.Frames - 1);
            return new uint2((uint)(c.Start + i), (uint)(c.Start + n));
        }

        void Execute(in Soldier s, in Body b, in Anim a)
        {
            float d2 = math.distancesq(b.Pos, Camera);
            if (d2 > FarSq) return;
            float3 mid = b.Pos + new float3(0, 0.9f, 0);
            bool seen = true;
            for (int i = 0; i < Planes.Length; i++) seen &= math.dot(Planes[i].xyz, mid) + Planes[i].w > -1.5f;
            bool shadow = d2 < ShadowSq;
            if (!seen && !shadow) return;

            uint2 fa = Frame(a.Clip, a.Time, out float ta), fb = Frame(a.Prev, a.PrevTime, out float tb);
            int flags = 0;
            if (Squads[s.Squad].Captain == s.Member && s.Slot != HideHead) flags |= SoldierInstance.Captain;
            if (s.Slot == HideHead) flags |= SoldierInstance.NoHead;
            if (s.Squad == PlayerSquad && s.Slot != HideHead) flags |= SoldierInstance.Mine;
            var inst = new SoldierInstance
            {
                Pos = b.Pos, Yaw = b.Yaw, Frames = new uint4(fa, fb),
                Blends = new float4(ta, tb, a.Blend * a.Blend * (3f - 2f * a.Blend), math.clamp(b.Pitch, -1.2f, 1.2f)),
                Misc = new float4(math.frac(s.Slot * 0.61803f + s.Gen * 0.37f), flags, -1f, 0f),
            };
            if (seen)
            {
                bool near = d2 < NearSq;
                if (s.Team == 0) { if (near) Near0.AddNoResize(inst); else Far0.AddNoResize(inst); }
                else { if (near) Near1.AddNoResize(inst); else Far1.AddNoResize(inst); }
            }
            if (shadow)
            {
                inst.Misc.y = flags & ~SoldierInstance.NoHead;
                if (s.Team == 0) Shadow0.AddNoResize(inst); else Shadow1.AddNoResize(inst);
            }
        }
    }

    /// <summary>Draws the living from the ECS and the dead from their own list, in at most six instanced calls each.</summary>
    public class SoldierRenderer : IDisposable
    {
        const int Max = BattleSystem.MaxSoldiers;

        class Batch : IDisposable
        {
            public NativeList<SoldierInstance> List = new(Max, Allocator.Persistent);
            public GraphicsBuffer Buffer = new(GraphicsBuffer.Target.Structured, 1024, SoldierInstance.Stride);
            public readonly MaterialPropertyBlock Block = new();

            public void Draw(SoldierRenderer r, Mesh mesh, ShadowCastingMode shadows, Camera cam)
            {
                int n = List.Length;
                if (n == 0) return;
                if (Buffer.count < n)
                {
                    Buffer.Dispose();
                    Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(n), SoldierInstance.Stride);
                }
                Buffer.SetData(List.AsArray(), 0, 0, n);
                Block.SetBuffer("_Instances", Buffer);
                Block.SetBuffer("_Poses", r._poses);
                Block.SetBuffer("_Upper", r._upper);
                Block.SetBuffer("_Corpses", r.Corpses);
                Block.SetInt("_Offset", 0);
                var rp = new RenderParams(r._material)
                {
                    matProps = Block, shadowCastingMode = shadows, receiveShadows = true,
                    worldBounds = new Bounds(Vector3.zero, new Vector3(2000, 400, 2000)),
                };
                Graphics.RenderMeshPrimitives(rp, mesh, 0, n);
            }

            public void Dispose() { List.Dispose(); Buffer.Dispose(); }
        }

        readonly SoldierData _data;
        readonly Material _material;
        readonly GraphicsBuffer _poses, _upper;
        /// <summary>Three rows a bone for every corpse.</summary>
        public readonly GraphicsBuffer Corpses;
        readonly Batch[] _near = { new(), new() }, _far = { new(), new() }, _shadow = { new(), new() };
        readonly Batch[] _deadNear = { new(), new() }, _deadFar = { new(), new() }, _deadShadow = { new(), new() };
        NativeArray<ClipTable> _clips;
        NativeArray<float4> _planes;
        readonly Plane[] _planeScratch = new Plane[6];
        public int Drawn;
        public float NearDistance = 55f, ShadowDistance = 70f;

        public SoldierRenderer(SoldierData data, Material material, int maxCorpses)
        {
            _data = data;
            _material = material;
            _poses = new GraphicsBuffer(GraphicsBuffer.Target.Structured, data.poses.Length, 16);
            _poses.SetData(data.poses);
            _upper = new GraphicsBuffer(GraphicsBuffer.Target.Structured, data.BoneCount, 4);
            _upper.SetData(Array.ConvertAll(data.upper, u => u ? 1f : 0f));
            Corpses = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, maxCorpses) * data.BoneCount * 3, 16);
            material.SetInt("_BoneCount", data.BoneCount);
            material.SetInt("_ChestBone", data.chestBone);
            material.SetInt("_HeadBone", Array.IndexOf(data.boneNames, "Bone.001"));
            material.SetInt("_HelmetBone", data.Helmet);
            material.SetVector("_ChestPivot", data.chestPivot);
            _clips = new NativeArray<ClipTable>(data.clips.Length, Allocator.Persistent);
            for (int i = 0; i < data.clips.Length; i++) _clips[i] = new ClipTable { Start = data.clips[i].start, Frames = data.clips[i].frames, Loop = data.clips[i].loop };
            _planes = new NativeArray<float4>(6, Allocator.Persistent);
        }

        void Planes(Camera cam)
        {
            GeometryUtility.CalculateFrustumPlanes(cam, _planeScratch);
            for (int i = 0; i < 6; i++) _planes[i] = new float4(_planeScratch[i].normal, _planeScratch[i].distance);
        }

        public bool Visible(Vector3 p, float r = 1.5f)
        {
            for (int i = 0; i < 6; i++)
                if (math.dot(_planes[i].xyz, p) + _planes[i].w < -r) return false;
            return true;
        }

        public void Draw(BattleSystem battle, Camera cam, BodyStore dead, int hideHead)
        {
            Planes(cam);
            foreach (var set in new[] { _near, _far, _shadow, _deadNear, _deadFar, _deadShadow })
                foreach (var b in set) b.List.Clear();
            battle.Cull(new CullJob
            {
                Clips = _clips, Planes = _planes, Squads = battle.Squads, Camera = cam.transform.position,
                NearSq = NearDistance * NearDistance, ShadowSq = ShadowDistance * ShadowDistance, FarSq = 600f * 600f,
                PlayerSquad = battle.PlayerSquad, HideHead = hideHead,
                Near0 = _near[0].List.AsParallelWriter(), Near1 = _near[1].List.AsParallelWriter(),
                Far0 = _far[0].List.AsParallelWriter(), Far1 = _far[1].List.AsParallelWriter(),
                Shadow0 = _shadow[0].List.AsParallelWriter(), Shadow1 = _shadow[1].List.AsParallelWriter(),
            });
            dead.Collect(this, cam.transform.position, _deadNear[0].List, _deadNear[1].List, _deadFar[0].List, _deadFar[1].List, _deadShadow[0].List, _deadShadow[1].List);
            Drawn = 0;
            for (int t = 0; t < 2; t++)
            {
                Drawn += _near[t].List.Length + _far[t].List.Length + _deadNear[t].List.Length + _deadFar[t].List.Length;
                _near[t].Draw(this, _data.near[t], ShadowCastingMode.Off, cam);
                _far[t].Draw(this, _data.far[t], ShadowCastingMode.Off, cam);
                _shadow[t].Draw(this, _data.far[t], ShadowCastingMode.ShadowsOnly, cam);
                _deadNear[t].Draw(this, _data.near[t], ShadowCastingMode.Off, cam);
                _deadFar[t].Draw(this, _data.far[t], ShadowCastingMode.Off, cam);
                _deadShadow[t].Draw(this, _data.far[t], ShadowCastingMode.ShadowsOnly, cam);
            }
        }

        public void Dispose()
        {
            foreach (var set in new[] { _near, _far, _shadow, _deadNear, _deadFar, _deadShadow })
                foreach (var b in set) b.Dispose();
            _poses.Dispose(); _upper.Dispose(); Corpses.Dispose();
            _clips.Dispose(); _planes.Dispose();
        }
    }
}
