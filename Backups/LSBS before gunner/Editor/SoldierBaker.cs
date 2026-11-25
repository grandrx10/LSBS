using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace LSBS.EditorTools
{
    /// <summary>
    /// Builds the soldiers out of the "fellas" body, the Sebastion and the Rachmaninoff, dresses them in the kits, and bakes
    /// every pose.
    /// </summary>
    public static partial class SoldierBaker
    {
        public const string Folder = "Assets/LSBS/Generated", DataPath = Folder + "/Soldiers.asset", RagdollPath = Folder + "/Ragdoll.prefab";
        const string PlayerPath = "Assets/Prefab/NetPlayer.prefab", RiflePath = "Assets/Models/Sebastion.prefab", ShotgunPath = "Assets/Models/Rachmaninoff.prefab";
        const float RifleScale = 0.4f;

        /// <summary>Who a part is for, as the shader reads it (the last number on each corner).</summary>
        const int ForAll = 0, ForCaptain = 1, NotCommunicator = 2, ForCommunicator = 3, ForSweeper = 4, NotSweeper = 5;

        static Color Walnut => MeshBuilder.Col(0x3f2716);
        static Color GunSteel => MeshBuilder.Col(0x54565a, MeshBuilder.Metal);
        static Color GunBrass => MeshBuilder.Col(0x9c7433, MeshBuilder.Metal);
        static Color GunBlack => MeshBuilder.Col(0x2c2d30, MeshBuilder.Metal);

        static Transform Deep(Transform t, string n) => t.GetComponentsInChildren<Transform>(true).FirstOrDefault(c => c.name == n);

        // ------------------------------------------------------------------ the guns

        struct GunParts { public MeshBuilder body, moving, simple; public Vector3 muzzle, support, fulcrum; }

        static GunParts BuildGun(string path, bool shotgun)
        {
            var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (var pb in go.GetComponentsInChildren<ProBuilderMesh>(true)) { pb.ToMesh(); pb.Refresh(); }
            var root = go.transform;
            Vector3 trigger = root.InverseTransformPoint(Deep(root, "TriggerPoint").position);
            // its barrel lies along +x; here it's +z, with the trigger hand at the origin
            Matrix4x4 to = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0, -90, 0), Vector3.one * RifleScale) * Matrix4x4.Translate(-trigger) * root.worldToLocalMatrix;
            Vector3 At(string n) { var t = Deep(root, n); return t != null ? to.MultiplyPoint3x4(t.position) : Vector3.zero; }

            var r = new GunParts { body = new MeshBuilder(), moving = new MeshBuilder(), simple = new MeshBuilder(), muzzle = At("BarrelPoint"), support = At("OffhandHold"), fulcrum = At("LeverActionFulcrum") };
            var moving = Deep(root, shotgun ? "Handle" : "LeverAction");
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                string mat = mr != null && mr.sharedMaterial != null ? mr.sharedMaterial.name : "";
                bool moves = mf.transform.IsChildOf(moving);
                Color col;
                if (shotgun) col = mat.StartsWith("GunBlock") ? GunBlack : mat.StartsWith("White") ? GunBrass : Walnut;
                else
                {
                    bool block = mat.StartsWith("GunBlock");
                    bool sight = block && (mf.name == "Cube (12)" || mf.name == "Cube (14)" || mf.name == "Cube (15)");
                    col = moves || sight || mf.name == "Slide" ? GunBrass : block ? Walnut : GunSteel;
                }
                (moves ? r.moving : r.body).Add(mf.sharedMesh, to * mf.transform.localToWorldMatrix, col);
            }
            Bounds bounds = new(r.body.V[0], Vector3.zero);
            foreach (var v in r.body.V) bounds.Encapsulate(v);
            r.simple.Box(new Vector3(0, bounds.center.y + 0.02f, bounds.center.z + bounds.size.z * 0.15f), new Vector3(0.035f, 0.05f, bounds.size.z * 0.7f), shotgun ? GunBlack : GunSteel);
            r.simple.Box(new Vector3(0, bounds.center.y - 0.03f, bounds.min.z + bounds.size.z * 0.18f), new Vector3(0.04f, 0.1f, bounds.size.z * 0.36f), Walnut);
            Debug.Log($"[LSBS] {System.IO.Path.GetFileNameWithoutExtension(path)}: {r.body.I.Count / 3 + r.moving.I.Count / 3} tris, muzzle {r.muzzle}, support {r.support}");
            Object.DestroyImmediate(go);
            return r;
        }

        // ------------------------------------------------------------------ everything

        [MenuItem("LSBS/Rebuild Soldiers")]
        public static SoldierData Build()
        {
            System.IO.Directory.CreateDirectory(Folder);
            var rifle = BuildGun(RiflePath, false);
            var shotgun = BuildGun(ShotgunPath, true);
            var player = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath));
            var model = player.transform.Find("PlayerModel");
            model.SetParent(null);
            model.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Object.DestroyImmediate(player);
            var smr = model.GetComponentInChildren<SkinnedMeshRenderer>(true);
            var mesh = smr.sharedMesh;
            var bones = smr.bones;
            int nb = bones.Length;

            var data = ScriptableObject.CreateInstance<SoldierData>();
            data.bodyBones = nb;
            data.boneNames = bones.Select(b => b.name).ToArray();
            data.bindposes = mesh.bindposes.Concat(Enumerable.Repeat(Matrix4x4.identity, 4)).ToArray();
            var chest = Deep(model, "Bone.003");
            data.upper = bones.Select(b => b.IsChildOf(chest)).Concat(new[] { true, true, true, true }).ToArray();
            data.chestBone = System.Array.IndexOf(bones, chest);
            data.chestPivot = mesh.bindposes[data.chestBone].inverse.MultiplyPoint3x4(Vector3.zero);
            data.muzzle = rifle.muzzle;
            int B(string n) => System.Array.IndexOf(data.boneNames, n);
            int head = B("Bone.001");

            var solver = new PoseSolver(model, rifle.support, rifle.fulcrum);

            // the kit is fitted on the body as it stands in the prefab
            solver.ToBind();
            var reference = bones.Select((b, i) => b.localToWorldMatrix * mesh.bindposes[i]).ToArray();
            var joints = model.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First().position);
            var weights = mesh.boneWeights;
            var verts = mesh.vertices;
            Vector3 Skinned(int i)
            {
                var w = weights[i];
                Vector3 p = verts[i];
                return reference[w.boneIndex0].MultiplyPoint3x4(p) * w.weight0 + reference[w.boneIndex1].MultiplyPoint3x4(p) * w.weight1
                     + reference[w.boneIndex2].MultiplyPoint3x4(p) * w.weight2 + reference[w.boneIndex3].MultiplyPoint3x4(p) * w.weight3;
            }
            Bounds Region(string bone)
            {
                int bi = B(bone);
                Bounds b = default;
                bool any = false;
                for (int i = 0; i < verts.Length; i++)
                {
                    if (weights[i].boneIndex0 != bi) continue;
                    var p = Skinned(i);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
                return b;
            }
            var fit = new Fit { head = Region("Bone.001"), chest = Region("Bone.003"), hips = Region("Bone"), joint = n => joints[n] };

            // ---- the poses ----
            float feet = 0f;
            var frames = new List<Vector4>();
            var clips = new List<SoldierData.ClipInfo>();
            void Clip(string name, float seconds, bool loop, System.Func<float, PoseSolver.Pose> pose, System.Func<float, Matrix4x4> after = null, bool drops = false)
            {
                int n = Mathf.Max(2, Mathf.RoundToInt(seconds * SoldierData.Fps));
                Matrix4x4 held = Matrix4x4.identity;
                clips.Add(new SoldierData.ClipInfo { name = name, start = frames.Count / 3 / (nb + 4), frames = n, loop = loop, seconds = seconds });
                for (int f = 0; f < n; f++)
                {
                    float u = loop ? (float)f / n : (float)f / (n - 1);
                    solver.Apply(pose(u));
                    Matrix4x4 lift = Matrix4x4.Translate(new Vector3(0, feet, 0)) * (after != null ? after(u) : Matrix4x4.identity);
                    void Row(Matrix4x4 m) { m = lift * m; frames.Add(m.GetRow(0)); frames.Add(m.GetRow(1)); frames.Add(m.GetRow(2)); }
                    for (int b = 0; b < nb; b++) Row(bones[b].localToWorldMatrix * mesh.bindposes[b]);
                    if (drops)
                    {
                        // let go of: it falls by itself and lies flat beside them
                        if (f == 0) held = solver.Rifle;
                        float k = Mathf.Clamp01(u / 0.55f);
                        Vector3 from = held.GetColumn(3), to = new(0.62f, -feet + 0.05f, 0.45f);
                        Vector3 at = Vector3.Lerp(from, to, k);
                        at.y = Mathf.Lerp(from.y, to.y, k * k);
                        var gun = Matrix4x4.TRS(at, Quaternion.Slerp(held.rotation, Quaternion.Euler(0, 64f, 92f), k), Vector3.one);
                        lift = Matrix4x4.Translate(new Vector3(0, feet, 0));
                        Row(gun); Row(gun);
                        lift *= after != null ? after(u) : Matrix4x4.identity;
                        Row(bones[head].localToWorldMatrix * mesh.bindposes[head]);
                        lift = Matrix4x4.Translate(new Vector3(0, feet, 0));
                        Row(gun);
                        continue;
                    }
                    Row(solver.Rifle);
                    Row(solver.LeverMatrix);
                    Row(bones[head].localToWorldMatrix * mesh.bindposes[head]);
                    Row(solver.PumpMatrix);
                }
            }

            // the soles, standing
            solver.Apply(new PoseSolver.Pose());
            for (int i = 0; i < verts.Length; i++)
            {
                var w = weights[i];
                var p = bones[w.boneIndex0].localToWorldMatrix.MultiplyPoint3x4(mesh.bindposes[w.boneIndex0].MultiplyPoint3x4(verts[i]));
                feet = Mathf.Max(feet, -p.y);
            }

            const float Pi2 = Mathf.PI * 2f;
            float Action(float t, float delay, float time) // seconds since the shot
            {
                float u = (t - delay) / time;
                if (u <= 0f || u >= 1f) return 0f;
                return u < 0.4f ? Mathf.SmoothStep(0, 1, u / 0.4f) : u < 0.5f ? 1f : Mathf.SmoothStep(1, 0, (u - 0.5f) / 0.5f);
            }
            var low = new Vector3(40f, -22f, 10f);            // low ready, across the body
            var lowAt = new Vector3(0.02f, -0.2f, -0.06f);
            PoseSolver.Pose Breathe(PoseSolver.Pose p, float u) { p.breath = Mathf.Sin(u * Pi2); return p; }
            PoseSolver.Pose Kick(PoseSolver.Pose p, float t)
            {
                float kick = Mathf.Exp(-t * 14f);
                p.gunEuler += new Vector3(-9f * kick, 0, 0);
                p.gunOffset += new Vector3(0, 0, -0.09f * kick);
                p.lean -= 3f * kick;
                return p;
            }
            PoseSolver.Pose Fire(PoseSolver.Pose p, float u) { p = Kick(p, u * 0.6f); p.lever = Action(u * 0.6f, 0.2f, 0.4f); return p; }
            PoseSolver.Pose Pump(PoseSolver.Pose p, float u) { p = Kick(p, u * 0.4f); p.pump = Action(u * 0.4f, 0.1f, 0.3f); return p; }
            PoseSolver.Pose Reload(PoseSolver.Pose p, float u)
            {
                p.reload = 1f;
                p.feed = Mathf.Sin(u * Mathf.PI);
                p.headDown = 14f;
                return p;
            }
            var kneel = new PoseSolver.Pose { drop = 0.42f, kneel = 1f, lean = 4f };
            var kneelLow = kneel; kneelLow.gunEuler = new Vector3(30f, -14f, 6f); kneelLow.gunOffset = new Vector3(0.01f, -0.14f, -0.04f);
            var idle = new PoseSolver.Pose { gunEuler = low, gunOffset = lowAt };
            var aimPose = new PoseSolver.Pose { lean = 3f };

            Clip("Idle", 2f, true, u => Breathe(idle, u));
            Clip("Run", 0.62f, true, u => new PoseSolver.Pose { phase = u * Pi2, move = 1f, stride = 0.36f, lean = 11f, gunEuler = new Vector3(24f, -30f, 12f), gunOffset = new Vector3(0.0f, -0.16f, -0.08f) });
            Clip("CrouchRun", 0.6f, true, u => new PoseSolver.Pose { phase = u * Pi2, move = 1f, stride = 0.3f, lean = 26f, drop = 0.24f, headDown = -8f, gunEuler = new Vector3(22f, -24f, 10f), gunOffset = new Vector3(0f, -0.12f, -0.06f) });
            Clip("StandAim", 2f, true, u => Breathe(aimPose, u));
            Clip("StandFire", 0.6f, false, u => Fire(aimPose, u));
            Clip("StandReload", 0.5f, true, u => Reload(aimPose, u));
            Clip("KneelIdle", 2f, true, u => Breathe(kneelLow, u));
            Clip("KneelAim", 2f, true, u => Breathe(kneel, u));
            Clip("KneelFire", 0.6f, false, u => Fire(kneel, u));
            Clip("KneelReload", 0.5f, true, u => Reload(kneel, u));
            Matrix4x4 Topple(float u, float side)
            {
                float a = Mathf.Clamp01((u - 0.15f) / 0.75f);
                a = a * a * (2.2f - 1.2f * a);
                var pivot = new Vector3(0, -feet + 0.02f, 0.02f * side);
                return Matrix4x4.Translate(pivot + new Vector3(0, 0.12f * a, 0)) * Matrix4x4.Rotate(Quaternion.Euler(-88f * a * side, 14f * a, 0)) * Matrix4x4.Translate(-pivot);
            }
            PoseSolver.Pose Dying(float u, float side)
            {
                float buckle = Mathf.Sin(Mathf.Clamp01(u / 0.8f) * Mathf.PI);
                float loose = Mathf.SmoothStep(0, 1, Mathf.Clamp01(u / 0.35f));
                return new PoseSolver.Pose
                {
                    drop = 0.3f * buckle, lean = -12f * u * side, headDown = -18f * u * side,
                    // the arms fly out and go slack
                    rightW = loose, rightAt = new Vector3(0.42f, -0.28f, 0.12f), rightDir = new Vector3(0.7f, -0.6f, 0.2f),
                    leftW = loose, leftAt = new Vector3(-0.4f, -0.34f, 0.16f), leftDir = new Vector3(-0.7f, -0.6f, 0.2f),
                };
            }
            Clip("DieBack", 0.85f, false, u => Dying(u, 1f), u => Topple(u, 1f), true);
            Clip("DieForward", 0.85f, false, u => Dying(u, -1f), u => Topple(u, -1f), true);
            Clip("StandPump", 0.4f, false, u => Pump(aimPose, u));
            Clip("KneelPump", 0.4f, false, u => Pump(kneel, u));
            // the arm comes back, over, and through; the gun's kept low in the left hand
            PoseSolver.Pose Throwing(float u)
            {
                float back = Mathf.SmoothStep(0, 1, Mathf.Clamp01(u / 0.36f)), over = Mathf.SmoothStep(0, 1, Mathf.Clamp01((u - 0.36f) / 0.2f)), home = Mathf.SmoothStep(0, 1, Mathf.Clamp01((u - 0.68f) / 0.32f));
                Vector3 wound = new(0.34f, 0.06f, -0.34f), top = new(0.24f, 0.42f, 0.12f), through = new(0.02f, -0.08f, 0.52f);
                Vector3 at = Vector3.Lerp(Vector3.Lerp(wound, top, over), Vector3.Lerp(top, through, over), over);
                float w = back * (1f - home);
                return new PoseSolver.Pose
                {
                    lean = Mathf.Lerp(-8f, 14f, over) * w, twist = Mathf.Lerp(26f, -24f, over) * w,
                    gunEuler = new Vector3(30f, -34f, 10f), gunOffset = new Vector3(-0.1f, -0.26f, -0.04f),
                    rightW = w, rightAt = at, rightDir = Vector3.Slerp(new Vector3(0.1f, 0.9f, -0.4f), new Vector3(0, -0.5f, 0.85f), over),
                };
            }
            Clip("Throw", 0.9f, false, Throwing);
            Clip("Call", 2.6f, false, u =>
            {
                float w = Mathf.SmoothStep(0, 1, Mathf.Clamp01(u / 0.12f)) * (1f - Mathf.SmoothStep(0, 1, Mathf.Clamp01((u - 0.92f) / 0.08f)));
                var p = Breathe(kneelLow, u * 2f);
                p.gunEuler = new Vector3(34f, -10f, 0); p.gunOffset = new Vector3(0.05f, -0.2f, -0.06f);
                p.leftW = w; p.leftAt = new Vector3(-0.10f, -0.02f, 0.02f); p.leftDir = new Vector3(0.35f, 0.75f, -0.2f);
                p.headDown = Mathf.Sin(u * Pi2 * 3f) * 3f;
                return p;
            });
            data.clips = clips.ToArray();
            data.poses = frames.ToArray();

            // ---- the soldiers themselves ----
            var zoneOfVertex = weights.Select(w => ZoneOf(data.boneNames[w.boneIndex0])).ToArray();
            var bodyNormals = mesh.normals;
            var posed = Enumerable.Range(0, verts.Length).Select(Skinned).ToArray();
            var posedNormals = Enumerable.Range(0, verts.Length).Select(i => reference[weights[i].boneIndex0].MultiplyVector(bodyNormals[i]).normalized).ToArray();
            EnsureGrenade();
            EnsureMask(fit);
            data.grenade = GrenadeMesh(1f, Vector3.zero).Build("Grenade");
            for (int team = 0; team < 2; team++)
            {
                EnsureKit(team, mesh, zoneOfVertex, posed, posedNormals, fit);
                data.near[team] = Dress(team, 0);
                data.far[team] = Dress(team, 1);
                data.torn[team] = Dress(team, 2);
            }

            // detail: 0 near, 1 far, 2 near and cut through at the joints
            Mesh Dress(int team, int detail)
            {
                var (kit, zones) = ReadKit(team);
                var mb = new MeshBuilder();
                bool near = detail != 1;
                if (detail == 2) Cut(mb, zones);
                else
                {
                    // the body, dyed by which bone it hangs on
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var w = weights[i];
                        mb.V.Add(verts[i]);
                        mb.N.Add(bodyNormals[i]);
                        mb.C.Add(zones[zoneOfVertex[i]]);
                        float sum = Mathf.Max(0.001f, w.weight0 + w.weight1);
                        mb.B.Add(new Vector4(w.boneIndex0, w.boneIndex1, w.weight0 / sum, 0));
                    }
                    for (int s = 0; s < mesh.subMeshCount; s++) mb.I.AddRange(mesh.GetTriangles(s));
                }

                foreach (var piece in kit)
                {
                    // each piece sits on its bone as it was placed in the fitting pose
                    if (!near && piece.farTriangles <= 0) continue;
                    var p = near ? piece : Far(piece);
                    int skinnedAs = piece.bone == "*" ? head : B(piece.bone);
                    int bone = piece.bone == "*" ? data.Helmet : skinnedAs;
                    Matrix4x4 m0 = reference[skinnedAs].inverse, n0 = reference[skinnedAs].transpose;
                    int k = mb.V.Count;
                    for (int i = 0; i < p.v.Count; i++)
                    {
                        mb.V.Add(m0.MultiplyPoint3x4(p.v[i]));
                        mb.N.Add(n0.MultiplyVector(p.n[i]).normalized);
                        mb.C.Add(p.c[i]);
                        mb.B.Add(new Vector4(bone, bone, 1, piece.forWhom));
                    }
                    mb.I.AddRange(p.i.Select(i => i + k));
                }
                void Part(MeshBuilder p, int bone, int forWhom)
                {
                    int k = mb.V.Count;
                    mb.V.AddRange(p.V); mb.N.AddRange(p.N); mb.C.AddRange(p.C);
                    for (int i = 0; i < p.V.Count; i++) mb.B.Add(new Vector4(bone, bone, 1, forWhom));
                    mb.I.AddRange(p.I.Select(i => i + k));
                }
                if (near)
                {
                    Part(rifle.body, data.Rifle, NotSweeper); Part(rifle.moving, data.Lever, NotSweeper);
                    Part(shotgun.body, data.Rifle, ForSweeper); Part(shotgun.moving, data.Pump, ForSweeper);
                }
                else Part(rifle.simple, data.Rifle, ForAll);

                var m = mb.Build((team == 0 ? "Green" : "Grey") + (detail == 0 ? " soldier" : detail == 1 ? " soldier (far)" : " soldier (torn)"), true);
                m.bounds = new Bounds(new Vector3(0, 1, 0), Vector3.one * 4f);
                Debug.Log($"[LSBS] {m.name}: {m.vertexCount} verts, {mb.I.Count / 3} tris");
                return m;
            }

            // the body with every triangle on one bone, so it comes apart cleanly, and flesh over the ends
            void Cut(MeshBuilder mb, Color[] zones)
            {
                var tris = new List<int>();
                for (int s = 0; s < mesh.subMeshCount; s++) tris.AddRange(mesh.GetTriangles(s));
                Vector3Int Key(Vector3 v) => Vector3Int.RoundToInt(v * 2000f);
                var edges = new Dictionary<(Vector3Int, Vector3Int), List<(int seg, int bone, Vector3 a, Vector3 b)>>();
                for (int t = 0; t < tris.Count; t += 3)
                {
                    // the bone most of it hangs on
                    int b0 = weights[tris[t]].boneIndex0, b1 = weights[tris[t + 1]].boneIndex0, b2 = weights[tris[t + 2]].boneIndex0;
                    int bone = b1 == b2 ? b1 : b0;
                    int seg = SegmentOf(data.boneNames[bone]);
                    for (int c = 0; c < 3; c++)
                    {
                        int v = tris[t + c];
                        mb.I.Add(mb.V.Count);
                        mb.V.Add(verts[v]); mb.N.Add(bodyNormals[v]); mb.C.Add(zones[zoneOfVertex[v]]); mb.B.Add(new Vector4(bone, bone, 1, 0));
                        Vector3 p = verts[v], q = verts[tris[t + (c + 1) % 3]];
                        var (kp, kq) = (Key(p), Key(q));
                        var k = (kp.x, kp.y, kp.z).CompareTo((kq.x, kq.y, kq.z)) < 0 ? (kp, kq) : (kq, kp);
                        if (!edges.TryGetValue(k, out var list)) edges[k] = list = new();
                        list.Add((seg, bone, p, q));
                    }
                }
                var flesh = MeshBuilder.Col(0x5a100c);
                var ends = new Dictionary<(int seg, int other), List<(int bone, Vector3 a, Vector3 b)>>();
                foreach (var list in edges.Values)
                    foreach (var x in list)
                        foreach (var y in list)
                            if (x.seg != y.seg)
                            {
                                if (!ends.TryGetValue((x.seg, y.seg), out var l)) ends[(x.seg, y.seg)] = l = new();
                                l.Add((x.bone, x.a, x.b));
                            }
                foreach (var l in ends.Values)
                {
                    Vector3 mid = Vector3.zero;
                    foreach (var (_, a, b) in l) mid += (a + b) * 0.5f;
                    mid /= l.Count;
                    foreach (var (bone, a, b) in l)
                    {
                        mb.M = Matrix4x4.identity;
                        mb.Bone = new Vector4(bone, bone, 1, 0);
                        mb.Tri(a, mid, b, flesh);
                        mb.Tri(a, b, mid, flesh);
                    }
                }
            }

            data.ragdoll = BuildRagdoll(model, solver);
            Save(ref data);
            Object.DestroyImmediate(model.gameObject);
            Debug.Log($"[LSBS] {frames.Count / 3 / (nb + 4)} frames of {nb + 4} bones baked");
            return data;
        }

        /// <summary>Written over what's there, so the scene keeps pointing at it.</summary>
        static void Save(ref SoldierData data)
        {
            var old = AssetDatabase.LoadAssetAtPath<SoldierData>(DataPath);
            if (old == null)
            {
                AssetDatabase.CreateAsset(data, DataPath);
                old = data;
            }
            Mesh Keep(Mesh made, Mesh had, SoldierData owner)
            {
                if (had == null || ReferenceEquals(made, had)) { AssetDatabase.AddObjectToAsset(made, owner); return made; }
                string name = made.name;
                EditorUtility.CopySerialized(made, had);
                had.name = name;
                return had;
            }
            bool fresh = ReferenceEquals(old, data);
            for (int t = 0; t < 2; t++)
            {
                data.near[t] = Keep(data.near[t], fresh || old.near == null || old.near.Length < 2 ? null : old.near[t], old);
                data.far[t] = Keep(data.far[t], fresh || old.far == null || old.far.Length < 2 ? null : old.far[t], old);
                data.torn[t] = Keep(data.torn[t], fresh || old.torn == null || old.torn.Length < 2 ? null : old.torn[t], old);
            }
            data.grenade = Keep(data.grenade, fresh ? null : old.grenade, old);
            if (!fresh)
            {
                string name = old.name;
                EditorUtility.CopySerialized(data, old);
                old.name = name;
                data = old;
            }
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------ the ragdoll

        public static readonly (string start, string end, float radius, int parent, float mass, bool spine)[] Segments =
        {
            ("Bone", "Bone.003", 0.17f, -1, 10f, true), ("Bone.003", "Bone.001", 0.19f, 0, 10f, true), ("Bone.001", "Bone.001_end", 0.15f, 1, 4f, true),
            ("Arm L.001", "Arm L.002", 0.07f, 1, 2f, false), ("Arm L.002", "Arm L.003_end", 0.06f, 3, 1.5f, false),
            ("Arm R.001", "Arm R.002", 0.07f, 1, 2f, false), ("Arm R.002", "Arm R.003_end", 0.06f, 5, 1.5f, false),
            ("Leg L.001", "Leg L.002", 0.10f, 0, 5f, false), ("Leg L.002", "Leg L.003_end", 0.08f, 7, 3f, false),
            ("Leg R.001", "Leg R.002", 0.10f, 0, 5f, false), ("Leg R.002", "Leg R.003_end", 0.08f, 9, 3f, false),
        };

        /// <summary>Which part of the ragdoll a bone goes with.</summary>
        static int SegmentOf(string bone)
        {
            switch (bone)
            {
                case "Bone": case "Leg L": case "Leg R": return 0;
                case "Bone.003": case "Arm L": case "Arm R": return 1;
                case "Bone.001": return 2;
            }
            int limb = bone.StartsWith("Arm L") ? 3 : bone.StartsWith("Arm R") ? 5 : bone.StartsWith("Leg L") ? 7 : 9;
            return limb + (bone.EndsWith(".001") ? 0 : 1);
        }

        static GameObject BuildRagdoll(Transform model, PoseSolver solver)
        {
            solver.ToBind();
            var root = new GameObject("Ragdoll");
            var go = Object.Instantiate(model.gameObject, root.transform);
            go.name = "Model";
            go.transform.localPosition = Vector3.zero;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) Object.DestroyImmediate(r.gameObject);
            Transform F(string n) => Deep(go.transform, n);
            // the hips hang off the rig, not the spine: make them follow the pelvis
            F("Leg L").SetParent(F("Bone"), true);
            F("Leg R").SetParent(F("Bone"), true);
            var bodies = new Rigidbody[Segments.Length];
            for (int i = 0; i < Segments.Length; i++)
            {
                var s = Segments[i];
                Transform a = F(s.start), b = F(s.end);
                var rb = a.gameObject.AddComponent<Rigidbody>();
                rb.mass = s.mass; rb.linearDamping = 0.1f; rb.angularDamping = 1.2f;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                rb.isKinematic = true;
                var col = a.gameObject.AddComponent<CapsuleCollider>();
                Vector3 lp = a.InverseTransformPoint(b.position);
                float scale = Mathf.Max(0.0001f, a.lossyScale.x);
                int axis = Mathf.Abs(lp.x) > Mathf.Abs(lp.y) && Mathf.Abs(lp.x) > Mathf.Abs(lp.z) ? 0 : Mathf.Abs(lp.y) > Mathf.Abs(lp.z) ? 1 : 2;
                col.direction = axis;
                col.center = lp * 0.5f;
                col.radius = s.radius / scale;
                col.height = Mathf.Abs(lp[axis]) + col.radius * (s.spine ? 0.6f : 2f);
                bodies[i] = rb;
                if (s.parent < 0) continue;
                // joints that can be let go of, when a limb comes off
                var j = a.gameObject.AddComponent<ConfigurableJoint>();
                j.connectedBody = bodies[s.parent];
                j.axis = Vector3.right;
                j.secondaryAxis = Vector3.up;
                j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
                j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Limited;
                j.lowAngularXLimit = new SoftJointLimit { limit = s.spine ? -25f : -60f };
                j.highAngularXLimit = new SoftJointLimit { limit = s.spine ? 25f : 60f };
                j.angularYLimit = new SoftJointLimit { limit = s.spine ? 30f : 60f };
                j.angularZLimit = new SoftJointLimit { limit = s.spine ? 30f : 50f };
                j.projectionMode = JointProjectionMode.PositionAndRotation;
                j.projectionDistance = 0.1f;
                j.projectionAngle = 60f;
                j.enablePreprocessing = false;
            }
            void Loose(string name, float mass, System.Action<GameObject> collider)
            {
                var o = new GameObject(name);
                o.transform.SetParent(root.transform, false);
                var rb = o.AddComponent<Rigidbody>();
                rb.mass = mass; rb.angularDamping = 0.6f; rb.isKinematic = true; rb.interpolation = RigidbodyInterpolation.Interpolate;
                collider(o);
            }
            Loose("RifleBody", 3f, o => { var c = o.AddComponent<BoxCollider>(); c.center = new Vector3(0, 0.03f, 0.1f); c.size = new Vector3(0.05f, 0.12f, 1.1f); });
            Loose("HelmetBody", 1.2f, o => { var c = o.AddComponent<SphereCollider>(); c.radius = 0.16f; });
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, RagdollPath);
            Object.DestroyImmediate(root);
            return prefab;
        }
    }
}
