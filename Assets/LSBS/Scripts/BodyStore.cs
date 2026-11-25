using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace LSBS
{
    /// <summary>
    /// The dead. Those near the camera fall as real ragdolls from a pool; once they've come to rest (or the pool's needed)
    /// their pose is kept as plain numbers and the ragdoll goes back. The rest play a fall. Oldest bodies go first.
    /// Explosions throw them up and away, and sometimes take them to pieces.
    /// </summary>
    public class BodyStore
    {
        class Doll
        {
            public GameObject root;
            public Transform[] bones;       // the body's, then rifle, lever (= rifle), helmet, pump (= rifle)
            public Rigidbody[] bodies;      // everything
            public Rigidbody[] skeleton;    // by segment
            public ConfigurableJoint[] joints; // by segment; null for the pelvis
            public Transform model, rifle, helmet, head;
            public Matrix4x4 helmetRel;
            public Transform[] all;         // how everything stands at rest: joints take their frames from it when they're switched on
            public Vector3[] restPos;
            public Quaternion[] restRot;
            public int corpse = -1;
            public float born, still;
            public bool player;
            public bool helmetOff;
        }

        struct Corpse
        {
            public bool used, limp, torn;
            public byte team;
            public int flags;
            public float seed, height;
            public Vector3 pos;
            // falling, not limp
            public float3 at;
            public float yaw, time;
            public byte clip;
            public float3 up;       // the slope they fell on
            // limp and left in the air: how far they've dropped, how fast, how far there is to go
            public float drop, fall, room;
        }

        /// <summary>The ragdoll's parts, in the order the baker made them.</summary>
        static readonly string[] SegmentBones = { "Bone", "Bone.003", "Bone.001", "Arm L.001", "Arm L.002", "Arm R.001", "Arm R.002", "Leg L.001", "Leg L.002", "Leg R.001", "Leg R.002" };
        /// <summary>How readily each comes off.</summary>
        static readonly float[] Looseness = { 0f, 0.35f, 0.4f, 0.5f, 0.3f, 0.5f, 0.3f, 0.55f, 0.35f, 0.55f, 0.35f };

        readonly SoldierData _data;
        readonly Corpse[] _corpses;
        readonly List<Doll> _pool = new(), _live = new();
        readonly SoldierRenderer _renderer;
        readonly int _headBone;
        NativeArray<Vector4> _scratch;
        readonly Matrix4x4[] _bindInverse;
        readonly PhysicsMaterial _mud;
        readonly System.Func<Vector3, float> _ground;
        Vector3 _eye;
        /// <summary>How many may be going at once (no more than the pool).</summary>
        public int Limit = 140;
        /// <summary>The corpse the player left.</summary>
        public int PlayerCorpse = -1;
        public Vector3 CorpsePos(int c) => _corpses[c].pos + Vector3.down * _corpses[c].drop;
        int _next;
        public int Count { get; private set; }
        public int Live => _live.Count;
        public int TornApart;
        /// <summary>Where something's been torn off, for the blood.</summary>
        public System.Action<Vector3, Vector3> Torn;

        public BodyStore(SoldierData data, SoldierRenderer renderer, int max, int dolls, Transform parent, System.Func<Vector3, float> ground)
        {
            _ground = ground;
            _data = data; _renderer = renderer;
            _headBone = data.Bone("Bone.001");
            _corpses = new Corpse[Mathf.Max(1, max)];
            _scratch = new NativeArray<Vector4>(data.BoneCount * 3, Allocator.Persistent);
            _bindInverse = new Matrix4x4[data.BoneCount];
            for (int i = 0; i < data.BoneCount; i++) _bindInverse[i] = data.bindposes[i].inverse;
            _mud = new PhysicsMaterial("Mud") { dynamicFriction = 0.7f, staticFriction = 0.8f, bounciness = 0f };
            for (int i = 0; i < dolls; i++)
            {
                var go = Object.Instantiate(data.ragdoll, parent);
                var d = new Doll { root = go, model = go.transform.Find("Model"), bones = new Transform[data.BoneCount], skeleton = new Rigidbody[SegmentBones.Length], joints = new ConfigurableJoint[SegmentBones.Length] };
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                {
                    int b = data.Bone(t.name);
                    if (b >= 0 && d.bones[b] == null) d.bones[b] = t;
                    if (t.name == "RifleBody") d.rifle = t;
                    if (t.name == "HelmetBody") d.helmet = t;
                }
                for (int s = 0; s < SegmentBones.Length; s++)
                {
                    var t = d.bones[data.Bone(SegmentBones[s])];
                    d.skeleton[s] = t.GetComponent<Rigidbody>();
                    d.joints[s] = t.GetComponent<ConfigurableJoint>();
                }
                d.head = d.bones[_headBone];
                d.bones[data.Rifle] = d.bones[data.Lever] = d.bones[data.Pump] = d.rifle;
                d.bones[data.Helmet] = d.helmet;
                d.bodies = go.GetComponentsInChildren<Rigidbody>(true);
                // bodies pile on the ground, not on each other: that's what keeps dozens of them cheap
                foreach (var c in go.GetComponentsInChildren<Collider>(true)) { c.sharedMaterial = _mud; c.gameObject.layer = 2; }
                go.SetActive(false);
                d.all = go.GetComponentsInChildren<Transform>(true);
                d.restPos = System.Array.ConvertAll(d.all, t => t.localPosition);
                d.restRot = System.Array.ConvertAll(d.all, t => t.localRotation);
                _pool.Add(d);
            }
            Physics.IgnoreLayerCollision(2, 2, true);
        }

        public void Dispose() => _scratch.Dispose();

        // ------------------------------------------------------------------ dying

        public void Die(in DeathEvent e, Vector3 camera, in Tuning tuning)
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
            float facing = math.dot(e.Dir.xz, new float2(math.sin(e.Yaw), math.cos(e.Yaw)));
            _corpses[c] = new Corpse
            {
                used = true, team = e.Team, seed = math.frac(e.Slot * 0.61803f + e.Gen * 0.37f), height = e.Height, pos = pos, at = e.Pos, yaw = e.Yaw,
                flags = SoldierInstance.ClassFlag(e.Class) | (e.Carrying ? SoldierInstance.Carrying : 0) | (e.Captain ? SoldierInstance.Captain : 0), clip = facing < 0.2f ? Clip.DieBack : Clip.DieForward,
            };

            _eye = camera;
            if (e.Player) PlayerCorpse = c;
            bool blast = e.Kind == ShotKind.Blast;
            // which way is up where they fall
            const float w = 0.8f;
            Vector3 up = new Vector3(_ground(pos + Vector3.left * w) - _ground(pos + Vector3.right * w), 2f * w, _ground(pos + Vector3.back * w) - _ground(pos + Vector3.forward * w)).normalized;
            _corpses[c].up = up;

            float reach = tuning.ragdollDistance;
            float score = Worth(pos);
            if (!e.Player && score > reach) return;
            if (_pool.Count == 0 || _live.Count >= Limit)
            {
                // somebody further off, or out of sight, gives theirs up (but never in mid air where it can be seen)
                int worst = -1;
                float worstScore = e.Player ? -1f : score * 1.15f;
                for (int i = 0; i < _live.Count; i++)
                {
                    var l = _live[i];
                    if (l.player) continue;
                    float k = Worth(l.skeleton[0].position);
                    bool seen = _renderer.Visible(l.skeleton[0].position, 2f);
                    if (seen && l.still < 0.2f && k < 70f && !e.Player) continue;
                    // one that's come to rest costs nothing to keep as it is
                    if (l.still > 0.2f) k += 60f;
                    if (k > worstScore) { worstScore = k; worst = i; }
                }
                if (worst < 0) return;
                Settle(worst);
            }
            var doll = _pool[_pool.Count - 1];
            _pool.RemoveAt(_pool.Count - 1);
            _live.Add(doll);
            doll.corpse = c;
            doll.born = Time.time;
            doll.still = 0f;
            doll.player = e.Player;
            _corpses[c].limp = true;
            // well inside it, some go to pieces and some don't
            bool torn = blast && e.Power > 0.3f && UnityEngine.Random.value < tuning.dismember * Mathf.Clamp01(0.55f + e.Power * 0.6f);
            _corpses[c].torn = torn;
            Pose(doll, e, torn);
            // its place in the buffer still has whoever was there before
            Write(doll);
        }

        void Pose(Doll d, in DeathEvent e, bool torn)
        {
            for (int i = 0; i < d.all.Length; i++) d.all[i].SetLocalPositionAndRotation(d.restPos[i], d.restRot[i]);
            d.model.localScale = Vector3.one * 0.5f * e.Height;
            d.root.SetActive(true);
            var world = Matrix4x4.TRS(e.Pos, Quaternion.Euler(0, e.Yaw * Mathf.Rad2Deg, 0), Vector3.one * e.Height);
            int frame = _data.Frame(e.Anim.Clip, e.Anim.Time, out _, out _);
            foreach (var rb in d.bodies) { rb.isKinematic = true; rb.interpolation = RigidbodyInterpolation.None; }
            d.model.localScale = Vector3.one * 0.5f * e.Height;
            d.rifle.localScale = d.helmet.localScale = Vector3.one * e.Height;
            d.model.SetPositionAndRotation(world.MultiplyPoint3x4(Vector3.up), world.rotation);
            for (int b = 0; b < _data.BoneCount; b++)
            {
                if (d.bones[b] == null || b == _data.Lever || b == _data.Pump || b == _data.Helmet) continue;
                Matrix4x4 m = world * _data.Pose(frame, b) * _bindInverse[b];
                d.bones[b].SetPositionAndRotation(m.GetColumn(3), m.rotation);
            }
            bool blast = e.Kind == ShotKind.Blast;
            d.helmetOff = e.Region == 1 || blast && e.Seed < 0.75f;
            d.helmet.SetPositionAndRotation(d.head.position + d.head.up * 0.16f, d.head.rotation);
            d.helmetRel = d.helmet.worldToLocalMatrix * d.head.localToWorldMatrix;

            // which limbs let go
            int off = 0;
            if (torn)
            {
                for (int s = 1; s < d.joints.Length; s++) if (UnityEngine.Random.value < Looseness[s]) off |= 1 << s;
                while (Bits(off) < 2) off |= 1 << UnityEngine.Random.Range(2, d.joints.Length);
                TornApart++;
            }
            for (int s = 1; s < d.joints.Length; s++)
            {
                bool free = (off & (1 << s)) != 0;
                var j = d.joints[s];
                j.xMotion = j.yMotion = j.zMotion = free ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
                j.angularXMotion = j.angularYMotion = j.angularZMotion = free ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Limited;
                j.projectionMode = free ? JointProjectionMode.None : JointProjectionMode.PositionAndRotation;
            }

            Vector3 vel = new(e.Vel.x, 0, e.Vel.y), dir = (Vector3)e.Dir;
            Vector3 fling;
            if (blast)
            {
                Vector3 away = (Vector3)(e.Pos - e.From);
                away.y = 0f;
                away = away.sqrMagnitude > 0.01f ? away.normalized : (Vector3)(Vector2)UnityEngine.Random.insideUnitCircle.normalized;
                // up and away
                float force = (e.Big ? 4.5f : 3.5f) + e.Power * (e.Big ? 6.5f : 4.5f);
                fling = (away * 0.75f + Vector3.up * (0.75f + e.Power * 0.5f)) * force * (0.75f + e.Seed * 0.5f);
            }
            else fling = dir * (1.6f + e.Seed * 2.2f) * (e.Kind == ShotKind.Shotgun ? 1.9f : 1f);

            foreach (var rb in d.bodies)
            {
                bool helmet = rb.transform == d.helmet;
                var col = rb.GetComponent<Collider>();
                // physics still has it where it was switched on (the middle of the map) until it's told
                rb.position = rb.transform.position;
                rb.rotation = rb.transform.rotation;
                if (helmet && !d.helmetOff) { col.enabled = false; continue; }
                if (helmet) col.enabled = true;
                rb.isKinematic = false;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.maxDepenetrationVelocity = 3f;
                rb.linearVelocity = vel * 0.9f + fling;
                rb.angularVelocity = blast ? UnityEngine.Random.insideUnitSphere * (4f + e.Power * 6f) : Vector3.Cross(Vector3.up, vel) * 0.3f;
            }
            for (int s = 1; s < d.joints.Length; s++)
            {
                if ((off & (1 << s)) == 0) continue;
                // each piece its own way
                Vector3 kick = UnityEngine.Random.insideUnitSphere * (2.5f + e.Power * 4f) + Vector3.up * UnityEngine.Random.Range(0.5f, 3f);
                d.skeleton[s].linearVelocity += kick;
                d.skeleton[s].angularVelocity = UnityEngine.Random.insideUnitSphere * 16f;
                Torn?.Invoke(d.skeleton[s].position, fling * 0.4f + kick * 0.3f);
            }

            if (!blast)
            {
                int hit = e.Region == 1 ? 2 : e.Region == 2 ? (e.Seed < 0.5f ? 7 : 9) : 1;
                d.skeleton[hit].linearVelocity += dir * (3.5f + e.Seed * 3f) * (e.Kind == ShotKind.Shotgun ? 1.5f : 1f) + Vector3.up * 0.6f;
            }
            if (d.rifle.TryGetComponent<Rigidbody>(out var gun))
            {
                // out of their hands, and off to one side
                Vector3 side = world.MultiplyVector(Vector3.right) * (e.Seed < 0.5f ? -1f : 1f), ahead = world.MultiplyVector(Vector3.forward);
                gun.linearVelocity = vel * 0.6f + (blast ? fling * 0.9f : dir * 0.3f) + side * (1.1f + e.Seed * 1.2f) + ahead * 0.9f + Vector3.up * 1.1f;
                gun.angularVelocity = UnityEngine.Random.insideUnitSphere * (blast ? 12f : 5f);
            }
            if (d.helmetOff && d.helmet.TryGetComponent<Rigidbody>(out var hat))
            {
                hat.linearVelocity = (blast ? fling * 1.15f : dir * 3f) + Vector3.up * (2.5f + e.Seed * 2f);
                hat.angularVelocity = UnityEngine.Random.insideUnitSphere * 14f;
            }
        }

        /// <summary>How much less it matters, in metres: the distance, and more again for what can't be seen.</summary>
        float Worth(Vector3 p) => (p - _eye).magnitude * (_renderer.Visible(p, 3f) ? 1f : 2.5f);

        static int Bits(int v) { int n = 0; while (v != 0) { n += v & 1; v >>= 1; } return n; }

        /// <summary>An explosion among bodies already down.</summary>
        public void Blast(Vector3 at, float radius)
        {
            foreach (var d in _live)
            {
                if ((d.skeleton[0].position - at).sqrMagnitude > radius * radius) continue;
                foreach (var rb in d.bodies)
                {
                    if (rb.isKinematic) continue;
                    Vector3 away = rb.position - at;
                    float k = 1f - Mathf.Clamp01(away.magnitude / radius);
                    rb.linearVelocity += (away.normalized + Vector3.up) * (k * 6f);
                }
                d.still = 0f;
            }
        }

        // ------------------------------------------------------------------ each frame

        public void Update(float dt, Vector3 eye)
        {
            _eye = eye;
            for (int i = 0; i < _corpses.Length; i++)
            {
                ref var c = ref _corpses[i];
                if (!c.used) continue;
                if (!c.limp) c.time += dt;
                else if (c.drop < c.room)
                {
                    // left in the air: it goes on down
                    c.fall += 9.81f * dt;
                    c.drop = Mathf.Min(c.room, c.drop + c.fall * dt);
                }
            }

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var d = _live[i];
                float age = Time.time - d.born;
                bool slow = age > 0.8f;
                if (slow)
                    foreach (var rb in d.skeleton)
                        if (rb.linearVelocity.sqrMagnitude > 0.05f) { slow = false; break; }
                d.still = slow ? d.still + dt : 0f;
                Vector3 lies = d.skeleton[0].transform.position;
                float far = Worth(lies);
                // close by they're let lie as long as there's room; nothing's stopped while it's moving unless it's gone on far too long
                float patience = d.player ? 8f : far < 30f ? 5f : far < 80f ? 1.5f : 0.4f;
                bool lost = lies.y < _ground(lies) - 4f;
                if (d.still > patience || age > 25f || lost) Settle(i);
                else if (far < 45f || ((Time.frameCount + i) & 1) == 0 && _renderer.Visible(lies, 4f)) Write(d);
                else _corpses[d.corpse].pos = lies;
            }
        }

        void Write(Doll d)
        {
            for (int b = 0; b < _data.BoneCount; b++)
            {
                Matrix4x4 m;
                if (b == _data.Helmet)
                {
                    Matrix4x4 at = d.helmetOff ? d.helmet.localToWorldMatrix * d.helmetRel : d.head.localToWorldMatrix;
                    m = at * _data.bindposes[_headBone];
                }
                else if (_data.IsWeapon(b)) m = d.rifle.localToWorldMatrix;
                else m = d.bones[b].localToWorldMatrix * _data.bindposes[b];
                _scratch[b * 3] = m.GetRow(0);
                _scratch[b * 3 + 1] = m.GetRow(1);
                _scratch[b * 3 + 2] = m.GetRow(2);
            }
            _renderer.Corpses.SetData(_scratch, 0, d.corpse * _data.BoneCount * 3, _scratch.Length);
            _corpses[d.corpse].pos = d.bones[0].position;
        }

        /// <summary>Keep how it's lying; give the ragdoll back. If it isn't lying on anything, it'll drop the rest of the way.</summary>
        void Settle(int i)
        {
            var d = _live[i];
            Write(d);
            ref var c = ref _corpses[d.corpse];
            if (c.torn) Ground(d);
            else
            {
                float clear = float.MaxValue;
                foreach (var rb in d.skeleton) clear = Mathf.Min(clear, rb.worldCenterOfMass.y - _ground(rb.worldCenterOfMass));
                c.room = clear > 0.35f ? clear - 0.12f : 0f;
                c.fall = Mathf.Max(0f, -d.skeleton[0].linearVelocity.y);
            }
            Release(i);
        }

        /// <summary>Pieces: each is put down on the ground beneath it.</summary>
        void Ground(Doll d)
        {
            for (int b = 0; b < _data.BoneCount; b++)
            {
                Transform t = b == _data.Helmet ? (d.helmetOff ? d.helmet : d.head) : _data.IsWeapon(b) ? d.rifle : d.bones[b];
                float clear = t.position.y - _ground(t.position);
                if (clear < 0.4f) continue;
                int row = b * 3 + 1;
                var v = _scratch[row];
                v.w -= clear - 0.1f;
                _scratch[row] = v;
            }
            _renderer.Corpses.SetData(_scratch, 0, d.corpse * _data.BoneCount * 3, _scratch.Length);
        }

        void Release(int i)
        {
            var d = _live[i];
            d.corpse = -1;
            d.player = false;
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
            PlayerCorpse = -1;
            TornApart = 0;
        }

        public void Collect(SoldierRenderer r, Vector3 cam, SoldierRenderer.DeadLists green, SoldierRenderer.DeadLists grey)
        {
            float nearSq = r.NearDistance * r.NearDistance, shadowSq = r.ShadowDistance * r.ShadowDistance;
            for (int i = 0; i < _corpses.Length; i++)
            {
                ref var c = ref _corpses[i];
                if (!c.used) continue;
                float d2 = (c.pos - cam).sqrMagnitude;
                bool seen = d2 < 320f * 320f && r.Visible(c.pos, c.torn ? 8f : 1.5f);
                bool shadow = d2 < shadowSq && !c.torn;
                if (!seen && !shadow) continue;
                var inst = new SoldierInstance
                {
                    Pos = c.at, Yaw = c.yaw, Blends = new float4(0, 0, 1, 0),
                    Misc = new float4(c.seed, c.flags, c.limp ? i : -1, c.height), Tilt = new float4(0, 1, 0, 0),
                };
                if (c.limp) inst.Pos = new float3(0, -c.drop, 0);
                else inst.Tilt = new float4(math.normalize(math.lerp(new float3(0, 1, 0), c.up, math.smoothstep(0.1f, 0.8f, c.time))), 0);
                if (!c.limp)
                {
                    int f = _data.Frame(c.clip, c.time, out int next, out float t);
                    inst.Frames = new uint4((uint)f, (uint)next, (uint)f, (uint)next);
                    inst.Blends.x = t;
                }
                var l = c.team == 0 ? green : grey;
                if (seen)
                {
                    if (c.torn) l.Torn.Add(inst);
                    else if (d2 < nearSq) l.Near.Add(inst);
                    else l.Far.Add(inst);
                }
                if (shadow) l.Shadow.Add(inst);
            }
        }
    }
}
