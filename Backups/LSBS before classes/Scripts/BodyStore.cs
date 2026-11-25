using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace LSBS
{
    /// <summary>
    /// The dead. Those near the camera fall as real ragdolls from a pool; once they've come to rest (or the pool's needed)
    /// their pose is kept as plain numbers and the ragdoll goes back. The rest play a fall. Oldest bodies go first.
    /// </summary>
    public class BodyStore
    {
        class Doll
        {
            public GameObject root;
            public Transform[] bones;       // the body's, then rifle, lever (= rifle), helmet
            public Rigidbody[] bodies;      // everything
            public Rigidbody[] skeleton;
            public Transform model, rifle, helmet, head;
            public int corpse = -1;
            public float born;
            public bool helmetOff;
        }

        struct Corpse
        {
            public bool used, limp;
            public byte team;
            public float seed;
            public bool captain;
            public Vector3 pos;
            // falling, not limp
            public float3 at;
            public float yaw, time;
            public byte clip;
        }

        readonly SoldierData _data;
        readonly BattleSystem _battle;
        readonly Corpse[] _corpses;
        readonly List<Doll> _pool = new(), _live = new();
        readonly SoldierRenderer _renderer;
        NativeArray<Vector4> _scratch;
        readonly Matrix4x4[] _bindInverse;
        readonly PhysicsMaterial _mud;
        int _next;
        public int Count { get; private set; }
        public int Live => _live.Count;

        public BodyStore(SoldierData data, BattleSystem battle, SoldierRenderer renderer, int max, int dolls, Transform parent)
        {
            _data = data; _battle = battle; _renderer = renderer;
            _corpses = new Corpse[Mathf.Max(1, max)];
            _scratch = new NativeArray<Vector4>(data.BoneCount * 3, Allocator.Persistent);
            _bindInverse = new Matrix4x4[data.BoneCount];
            for (int i = 0; i < data.BoneCount; i++) _bindInverse[i] = data.bindposes[i].inverse;
            _mud = new PhysicsMaterial("Mud") { dynamicFriction = 0.7f, staticFriction = 0.8f, bounciness = 0f };
            for (int i = 0; i < dolls; i++)
            {
                var go = Object.Instantiate(data.ragdoll, parent);
                var d = new Doll { root = go, model = go.transform.Find("Model"), bones = new Transform[data.BoneCount] };
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                {
                    int b = System.Array.IndexOf(data.boneNames, t.name);
                    if (b >= 0 && d.bones[b] == null) d.bones[b] = t;
                    if (t.name == "RifleBody") d.rifle = t;
                    if (t.name == "HelmetBody") d.helmet = t;
                }
                d.head = d.bones[System.Array.IndexOf(data.boneNames, "Bone.001")];
                d.bones[data.Rifle] = d.bones[data.Lever] = d.rifle;
                d.bones[data.Helmet] = d.helmet;
                d.bodies = go.GetComponentsInChildren<Rigidbody>(true);
                d.skeleton = d.model.GetComponentsInChildren<Rigidbody>(true);
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) { c.sharedMaterial = _mud; c.gameObject.layer = 2; }
                // bodies pile on the ground, not on each other: that's what keeps dozens of them cheap
                go.SetActive(false);
                _pool.Add(d);
            }
            Physics.IgnoreLayerCollision(2, 2, true);
        }

        public void Dispose() => _scratch.Dispose();

        // ------------------------------------------------------------------ dying

        public void Die(in DeathEvent e, Vector3 camera, float ragdollDistance)
        {
            int c = _next;
            _next = (_next + 1) % _corpses.Length;
            if (_corpses[c].used)
            {
                // its place is needed: a ragdoll still using it is dropped
                for (int i = _live.Count - 1; i >= 0; i--) if (_live[i].corpse == c) Release(i);
            }
            else Count++;
            Vector3 pos = e.Pos;
            float seed = math.frac(e.Slot * 0.61803f + e.Gen * 0.37f);
            float facing = math.dot(e.Dir.xz, new float2(math.sin(e.Yaw), math.cos(e.Yaw)));
            _corpses[c] = new Corpse { used = true, team = e.Team, seed = seed, captain = e.Captain, pos = pos, at = e.Pos, yaw = e.Yaw, clip = facing < 0.2f ? Clip.DieBack : Clip.DieForward };

            float d2 = (pos - camera).sqrMagnitude;
            bool worth = d2 < ragdollDistance * ragdollDistance && (_renderer.Visible(pos, 4f) || d2 < 20f * 20f);
            if (!worth) return;
            if (_pool.Count == 0)
            {
                // take the one that's been lying longest, if it's had its fall
                int oldest = -1;
                for (int i = 0; i < _live.Count; i++) if (oldest < 0 || _live[i].born < _live[oldest].born) oldest = i;
                if (oldest < 0 || Time.time - _live[oldest].born < 1.6f) return;
                Settle(oldest);
            }
            var doll = _pool[_pool.Count - 1];
            _pool.RemoveAt(_pool.Count - 1);
            _live.Add(doll);
            doll.corpse = c;
            doll.born = Time.time;
            _corpses[c].limp = true;
            Pose(doll, e);
        }

        void Pose(Doll d, in DeathEvent e)
        {
            d.root.SetActive(true);
            var world = Matrix4x4.TRS(e.Pos, Quaternion.Euler(0, e.Yaw * Mathf.Rad2Deg, 0), Vector3.one);
            int frame = _data.Frame(e.Anim.Clip, e.Anim.Time, out _, out _);
            foreach (var rb in d.bodies) { rb.isKinematic = true; rb.interpolation = RigidbodyInterpolation.None; }
            d.model.SetPositionAndRotation(world.MultiplyPoint3x4(Vector3.up), world.rotation);
            for (int b = 0; b < _data.BoneCount; b++)
            {
                var t = d.bones[b];
                if (t == null || b == _data.Lever) continue;
                Matrix4x4 m = world * _data.Pose(frame, b) * (b == _data.Helmet ? Matrix4x4.identity : _bindInverse[b]);
                if (b == _data.Helmet) continue; // below, with the head
                t.SetPositionAndRotation(m.GetColumn(3), m.rotation);
            }
            d.helmetOff = e.Region == 1;
            d.helmet.SetPositionAndRotation(d.head.position + d.head.up * 0.16f, d.head.rotation);
            d.helmet.gameObject.SetActive(true);
            _helmetRel[d] = d.helmet.worldToLocalMatrix * d.head.localToWorldMatrix;

            Vector3 vel = new(e.Vel.x, 0, e.Vel.y);
            Vector3 dir = (Vector3)e.Dir;
            float punch = 1.6f + e.Seed * 2.2f;
            int hit = e.Region == 1 ? System.Array.IndexOf(_data.boneNames, "Bone.001") : e.Region == 2 ? System.Array.IndexOf(_data.boneNames, e.Seed < 0.5f ? "Leg L.001" : "Leg R.001") : _data.chestBone;
            foreach (var rb in d.bodies)
            {
                if (rb.transform == d.helmet && !d.helmetOff) { rb.GetComponent<Collider>().enabled = false; continue; }
                if (rb.transform == d.helmet) rb.GetComponent<Collider>().enabled = true;
                rb.isKinematic = false;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.maxDepenetrationVelocity = 3f;
                rb.linearVelocity = vel * 0.9f + dir * punch;
                rb.angularVelocity = Vector3.Cross(Vector3.up, vel) * 0.3f;
            }
            if (hit >= 0 && d.bones[hit] != null && d.bones[hit].TryGetComponent<Rigidbody>(out var struck))
                struck.linearVelocity += dir * (3.5f + e.Seed * 3f) + Vector3.up * 0.6f;
            if (d.rifle.TryGetComponent<Rigidbody>(out var gun)) gun.angularVelocity = UnityEngine.Random.insideUnitSphere * 4f;
            if (d.helmetOff && d.helmet.TryGetComponent<Rigidbody>(out var hat))
            {
                hat.linearVelocity = dir * 3f + Vector3.up * (2.5f + e.Seed * 2f);
                hat.angularVelocity = UnityEngine.Random.insideUnitSphere * 14f;
            }
        }

        readonly Dictionary<Doll, Matrix4x4> _helmetRel = new();

        // ------------------------------------------------------------------ each frame

        public void Update(float dt)
        {
            for (int i = 0; i < _corpses.Length; i++)
                if (_corpses[i].used && !_corpses[i].limp) _corpses[i].time += dt;

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var d = _live[i];
                float age = Time.time - d.born;
                bool still = age > 1.5f;
                if (still)
                    foreach (var rb in d.skeleton)
                        if (rb.linearVelocity.sqrMagnitude > 0.04f) { still = false; break; }
                if (still || age > 5f) Settle(i);
                else Write(d);
            }
        }

        void Write(Doll d)
        {
            for (int b = 0; b < _data.BoneCount; b++)
            {
                Matrix4x4 m;
                if (b == _data.Helmet)
                {
                    int head = System.Array.IndexOf(_data.boneNames, "Bone.001");
                    Matrix4x4 at = d.helmetOff ? d.helmet.localToWorldMatrix * _helmetRel[d] : d.head.localToWorldMatrix;
                    m = at * _data.bindposes[head];
                }
                else if (b == _data.Rifle || b == _data.Lever) m = d.rifle.localToWorldMatrix;
                else m = d.bones[b].localToWorldMatrix * _data.bindposes[b];
                _scratch[b * 3] = m.GetRow(0);
                _scratch[b * 3 + 1] = m.GetRow(1);
                _scratch[b * 3 + 2] = m.GetRow(2);
            }
            _renderer.Corpses.SetData(_scratch, 0, d.corpse * _data.BoneCount * 3, _scratch.Length);
            _corpses[d.corpse].pos = d.bones[0].position;
        }

        /// <summary>Keep how it's lying; give the ragdoll back.</summary>
        void Settle(int i)
        {
            Write(_live[i]);
            Release(i);
        }

        void Release(int i)
        {
            var d = _live[i];
            d.corpse = -1;
            foreach (var rb in d.bodies) rb.isKinematic = true;
            d.root.SetActive(false);
            _live.RemoveAt(i);
            _pool.Add(d);
        }

        public void Clear()
        {
            for (int i = _live.Count - 1; i >= 0; i--) Release(i);
            System.Array.Clear(_corpses, 0, _corpses.Length);
            Count = 0;
            _next = 0;
        }

        public void Collect(SoldierRenderer r, Vector3 cam, NativeList<SoldierInstance> near0, NativeList<SoldierInstance> near1,
            NativeList<SoldierInstance> far0, NativeList<SoldierInstance> far1, NativeList<SoldierInstance> shadow0, NativeList<SoldierInstance> shadow1)
        {
            float nearSq = r.NearDistance * r.NearDistance, shadowSq = r.ShadowDistance * r.ShadowDistance;
            for (int i = 0; i < _corpses.Length; i++)
            {
                ref var c = ref _corpses[i];
                if (!c.used) continue;
                float d2 = (c.pos - cam).sqrMagnitude;
                bool seen = d2 < 320f * 320f && r.Visible(c.pos);
                bool shadow = d2 < shadowSq;
                if (!seen && !shadow) continue;
                var inst = new SoldierInstance
                {
                    Pos = c.at, Yaw = c.yaw, Blends = new float4(0, 0, 1, 0),
                    Misc = new float4(c.seed, c.captain ? SoldierInstance.Captain : 0, c.limp ? i : -1, 0),
                };
                if (!c.limp)
                {
                    int f = _data.Frame(c.clip, c.time, out int next, out float t);
                    inst.Frames = new uint4((uint)f, (uint)next, (uint)f, (uint)next);
                    inst.Blends.x = t;
                }
                if (seen)
                {
                    bool near = d2 < nearSq;
                    if (c.team == 0) { if (near) near0.Add(inst); else far0.Add(inst); }
                    else { if (near) near1.Add(inst); else far1.Add(inst); }
                }
                if (shadow) { if (c.team == 0) shadow0.Add(inst); else shadow1.Add(inst); }
            }
        }
    }
}
