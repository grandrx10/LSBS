using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace LSBS.EditorTools
{
    /// <summary>
    /// Builds the soldiers out of the "fellas" body and the Sebastian, adds the helmets and kit, and bakes every pose.
    /// </summary>
    public static partial class SoldierBaker
    {
        public const string Folder = "Assets/LSBS/Generated", DataPath = Folder + "/Soldiers.asset", RagdollPath = Folder + "/Ragdoll.prefab";
        const string PlayerPath = "Assets/Prefab/NetPlayer.prefab", RiflePath = "Assets/Models/Sebastion.prefab";
        const float RifleScale = 0.4f;

        static Color Brass => MeshBuilder.Col(0x9c7433, MeshBuilder.Metal);
        static Color Copper => MeshBuilder.Col(0x7d4a2c, MeshBuilder.Metal);
        static Color Iron => MeshBuilder.Col(0x37373a, MeshBuilder.Metal);
        static Color Steel => MeshBuilder.Col(0x54565a, MeshBuilder.Metal);
        static Color Leather => MeshBuilder.Col(0x38271a);
        static Color Rubber => MeshBuilder.Col(0x1f1d1a);
        static Color Walnut => MeshBuilder.Col(0x3f2716);
        static Color Skin => MeshBuilder.Col(0x5c4436);

        struct Uniform { public Color coat, trousers, puttees, helmet, lens, cloth; }

        static readonly Uniform[] Uniforms =
        {
            new() { coat = MeshBuilder.Col(0x4b5433), trousers = MeshBuilder.Col(0x3a3f2a), puttees = MeshBuilder.Col(0x5a5238), helmet = MeshBuilder.Col(0x39432c, MeshBuilder.Metal), lens = MeshBuilder.Col(0xffa030, MeshBuilder.Glow), cloth = MeshBuilder.Col(0x4f7a2e) },
            new() { coat = MeshBuilder.Col(0x5b6067), trousers = MeshBuilder.Col(0x44474d), puttees = MeshBuilder.Col(0x2c2a29), helmet = MeshBuilder.Col(0x4a4f57, MeshBuilder.Metal), lens = MeshBuilder.Col(0x58c8e8, MeshBuilder.Glow), cloth = MeshBuilder.Col(0x9aa2ad) },
        };

        static Transform Deep(Transform t, string n) => t.GetComponentsInChildren<Transform>(true).FirstOrDefault(c => c.name == n);

        // ------------------------------------------------------------------ the rifle

        struct RifleParts { public MeshBuilder body, lever, simple; public Vector3 muzzle, support, fulcrum; }

        static RifleParts BuildRifle()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RiflePath);
            var go = Object.Instantiate(prefab);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (var pb in go.GetComponentsInChildren<ProBuilderMesh>(true)) { pb.ToMesh(); pb.Refresh(); }
            var root = go.transform;
            Vector3 trigger = root.InverseTransformPoint(Deep(root, "TriggerPoint").position);
            // its barrel lies along +x; here it's +z, with the trigger hand at the origin
            Matrix4x4 to = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0, -90, 0), Vector3.one * RifleScale) * Matrix4x4.Translate(-trigger) * root.worldToLocalMatrix;
            Vector3 At(string n) { var t = Deep(root, n); return to.MultiplyPoint3x4(t.position); }

            var r = new RifleParts { body = new MeshBuilder(), lever = new MeshBuilder(), simple = new MeshBuilder(), muzzle = At("BarrelPoint"), support = At("OffhandHold"), fulcrum = At("LeverActionFulcrum") };
            var lever = Deep(root, "LeverAction");
            int parts = 0;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                parts++;
                var mr = mf.GetComponent<MeshRenderer>();
                bool block = mr != null && mr.sharedMaterial != null && mr.sharedMaterial.name.StartsWith("GunBlock");
                bool isLever = mf.transform.IsChildOf(lever);
                bool sight = block && (mf.name == "Cube (12)" || mf.name == "Cube (14)" || mf.name == "Cube (15)");
                Color col = isLever || sight || mf.name == "Slide" ? Brass : block ? Walnut : Steel;
                (isLever ? r.lever : r.body).Add(mf.sharedMesh, to * mf.transform.localToWorldMatrix, col);
            }
            Bounds bounds = new(r.body.V[0], Vector3.zero);
            foreach (var v in r.body.V) bounds.Encapsulate(v);
            r.simple.Box(new Vector3(0, bounds.center.y + 0.02f, bounds.center.z + bounds.size.z * 0.15f), new Vector3(0.035f, 0.05f, bounds.size.z * 0.7f), Steel);
            r.simple.Box(new Vector3(0, bounds.center.y - 0.03f, bounds.min.z + bounds.size.z * 0.18f), new Vector3(0.04f, 0.1f, bounds.size.z * 0.36f), Walnut);
            Debug.Log($"[LSBS] rifle: {parts} parts, {r.body.I.Count / 3 + r.lever.I.Count / 3} tris, bounds {bounds}, muzzle {r.muzzle}, support {r.support}, fulcrum {r.fulcrum}");
            Object.DestroyImmediate(go);
            return r;
        }

        // ------------------------------------------------------------------ everything

        [MenuItem("LSBS/Rebuild Soldiers")]
        public static SoldierData Build()
        {
            System.IO.Directory.CreateDirectory(Folder);
            var rifle = BuildRifle();
            var player = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath));
            var model = player.transform.Find("PlayerModel");
            model.SetParent(null);
            model.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Object.DestroyImmediate(player);
            var smr = model.GetComponentInChildren<SkinnedMeshRenderer>(true);
            var mesh = smr.sharedMesh;
            var bones = smr.bones;
            int nb = bones.Length;
            Debug.Log($"[LSBS] body: {mesh.name}, {mesh.vertexCount} verts, {mesh.triangles.Length / 3} tris, bones: {string.Join(", ", bones.Select(b => b.name))}");

            var data = ScriptableObject.CreateInstance<SoldierData>();
            data.bodyBones = nb;
            data.boneNames = bones.Select(b => b.name).ToArray();
            data.bindposes = mesh.bindposes.Concat(Enumerable.Repeat(Matrix4x4.identity, 3)).ToArray();
            var chest = Deep(model, "Bone.003");
            data.upper = bones.Select(b => b.IsChildOf(chest)).Concat(new[] { true, true, true }).ToArray();
            data.chestBone = System.Array.IndexOf(bones, chest);
            data.chestPivot = mesh.bindposes[data.chestBone].inverse.MultiplyPoint3x4(Vector3.zero);
            data.muzzle = rifle.muzzle;

            var solver = new PoseSolver(model, rifle.support, rifle.fulcrum);

            // the kit is fitted on the body as it stands in the prefab
            solver.ToBind();
            var reference = bones.Select((b, i) => b.localToWorldMatrix * mesh.bindposes[i]).ToArray();
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
                int bi = System.Array.IndexOf(data.boneNames, bone);
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
            var headB = Region("Bone.001");
            var chestB = Region("Bone.003");
            var hipB = Region("Bone");
            Debug.Log($"[LSBS] head {headB.min} .. {headB.max}; chest {chestB.min} .. {chestB.max}; hips {hipB.min} .. {hipB.max}");

            // ---- the poses ----
            float feet = 0f;
            var frames = new List<Vector4>();
            var clips = new List<SoldierData.ClipInfo>();
            void Clip(string name, float seconds, bool loop, System.Func<float, PoseSolver.Pose> pose, System.Func<float, Matrix4x4> after = null)
            {
                int n = Mathf.Max(2, Mathf.RoundToInt(seconds * SoldierData.Fps));
                clips.Add(new SoldierData.ClipInfo { name = name, start = frames.Count / 3 / (nb + 3), frames = n, loop = loop, seconds = seconds });
                for (int f = 0; f < n; f++)
                {
                    float u = loop ? (float)f / n : (float)f / (n - 1);
                    solver.Apply(pose(u));
                    Matrix4x4 lift = Matrix4x4.Translate(new Vector3(0, feet, 0)) * (after != null ? after(u) : Matrix4x4.identity);
                    void Row(Matrix4x4 m) { m = lift * m; frames.Add(m.GetRow(0)); frames.Add(m.GetRow(1)); frames.Add(m.GetRow(2)); }
                    for (int b = 0; b < nb; b++) Row(bones[b].localToWorldMatrix * mesh.bindposes[b]);
                    Row(solver.Rifle);
                    Row(solver.LeverMatrix);
                    Row(bones[System.Array.IndexOf(data.boneNames, "Bone.001")].localToWorldMatrix * mesh.bindposes[System.Array.IndexOf(data.boneNames, "Bone.001")]);
                }
            }

            // the soles, standing
            var stand = new PoseSolver.Pose();
            solver.Apply(stand);
            for (int i = 0; i < verts.Length; i++)
            {
                var w = weights[i];
                var p = bones[w.boneIndex0].localToWorldMatrix.MultiplyPoint3x4(mesh.bindposes[w.boneIndex0].MultiplyPoint3x4(verts[i]));
                feet = Mathf.Max(feet, -p.y);
            }
            Debug.Log($"[LSBS] soles {feet} below the hips");

            const float Pi2 = Mathf.PI * 2f;
            float Lever(float t) // t seconds since the shot: 0.2 wait, 0.4 of lever
            {
                float u = (t - 0.2f) / 0.4f;
                if (u <= 0f || u >= 1f) return 0f;
                return u < 0.4f ? Mathf.SmoothStep(0, 1, u / 0.4f) : u < 0.5f ? 1f : Mathf.SmoothStep(1, 0, (u - 0.5f) / 0.5f);
            }
            var low = new Vector3(40f, -22f, 10f);            // low ready, across the body
            var lowAt = new Vector3(0.02f, -0.2f, -0.06f);
            PoseSolver.Pose Breathe(PoseSolver.Pose p, float u) { p.breath = Mathf.Sin(u * Pi2); return p; }
            PoseSolver.Pose Fire(PoseSolver.Pose p, float u)
            {
                float t = u * 0.6f;
                float kick = Mathf.Exp(-t * 14f);
                p.gunEuler += new Vector3(-9f * kick, 0, 0);
                p.gunOffset += new Vector3(0, 0, -0.09f * kick);
                p.lean -= 3f * kick;
                p.lever = Lever(t);
                return p;
            }
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
                return new PoseSolver.Pose { drop = 0.3f * buckle, lean = -12f * u * side, gunEuler = new Vector3(-30f * u, 50f * u, 30f * u), gunOffset = new Vector3(0.1f * u, -0.1f * u, 0), headDown = -18f * u * side };
            }
            Clip("DieBack", 0.85f, false, u => Dying(u, 1f), u => Topple(u, 1f));
            Clip("DieForward", 0.85f, false, u => Dying(u, -1f), u => Topple(u, -1f));
            data.clips = clips.ToArray();
            data.poses = frames.ToArray();

            // ---- the soldiers themselves ----
            var zoneOfVertex = weights.Select(w => ZoneOf(data.boneNames[w.boneIndex0])).ToArray();
            var bodyNormals = mesh.normals;
            var posed = Enumerable.Range(0, verts.Length).Select(Skinned).ToArray();
            var posedNormals = Enumerable.Range(0, verts.Length).Select(i => reference[weights[i].boneIndex0].MultiplyVector(bodyNormals[i]).normalized).ToArray();
            for (int team = 0; team < 2; team++)
            {
                EnsureKit(team, mesh, zoneOfVertex, posed, posedNormals, headB, chestB, hipB);
                data.near[team] = Dress(team, true);
                data.far[team] = Dress(team, false);
            }

            Mesh Dress(int team, bool near)
            {
                var (kit, zones) = ReadKit(team);
                var mb = new MeshBuilder();
                var normals = mesh.normals;
                // the body, dyed by which bone it hangs on
                for (int i = 0; i < verts.Length; i++)
                {
                    var w = weights[i];
                    mb.V.Add(verts[i]);
                    mb.N.Add(normals[i]);
                    mb.C.Add(zones[zoneOfVertex[i]]);
                    float sum = Mathf.Max(0.001f, w.weight0 + w.weight1);
                    mb.B.Add(new Vector4(w.boneIndex0, w.boneIndex1, w.weight0 / sum, 0));
                }
                for (int s = 0; s < mesh.subMeshCount; s++) mb.I.AddRange(mesh.GetTriangles(s));

                int B(string n) => System.Array.IndexOf(data.boneNames, n);
                foreach (var piece in kit)
                {
                    // each piece sits on its bone as it was placed in the fitting pose
                    var p = near ? piece : Far(piece);
                    int skinnedAs = B(piece.bone == "*" ? "Bone.001" : piece.bone);
                    int bone = piece.bone == "*" ? data.Helmet : skinnedAs;
                    Matrix4x4 m0 = reference[skinnedAs].inverse, n0 = reference[skinnedAs].transpose;
                    int k = mb.V.Count;
                    for (int i = 0; i < p.v.Count; i++)
                    {
                        mb.V.Add(m0.MultiplyPoint3x4(p.v[i]));
                        mb.N.Add(n0.MultiplyVector(p.n[i]).normalized);
                        mb.C.Add(p.c[i]);
                        mb.B.Add(new Vector4(bone, bone, 1, piece.captain ? 1 : 0));
                    }
                    mb.I.AddRange(p.i.Select(i => i + k));
                }
                void Part(MeshBuilder p, int bone)
                {
                    int k = mb.V.Count;
                    mb.V.AddRange(p.V); mb.N.AddRange(p.N); mb.C.AddRange(p.C);
                    for (int i = 0; i < p.V.Count; i++) mb.B.Add(new Vector4(bone, bone, 1, 0));
                    mb.I.AddRange(p.I.Select(i => i + k));
                }
                if (near) { Part(rifle.body, data.Rifle); Part(rifle.lever, data.Lever); }
                else Part(rifle.simple, data.Rifle);

                var m = mb.Build((team == 0 ? "Green" : "Grey") + (near ? " soldier" : " soldier (far)"), true);
                m.bounds = new Bounds(new Vector3(0, 1, 0), Vector3.one * 4f);
                Debug.Log($"[LSBS] {m.name}: {m.vertexCount} verts, {mb.I.Count / 3} tris");
                return m;
            }

            data.ragdoll = BuildRagdoll(model, solver, feet, rifle);

            // written over what's there, so the scene keeps pointing at it
            var old = AssetDatabase.LoadAssetAtPath<SoldierData>(DataPath);
            if (old == null || old.near[0] == null || old.far[0] == null)
            {
                AssetDatabase.DeleteAsset(DataPath);
                AssetDatabase.CreateAsset(data, DataPath);
                foreach (var m in data.near.Concat(data.far)) AssetDatabase.AddObjectToAsset(m, data);
            }
            else
            {
                for (int t = 0; t < 2; t++)
                {
                    EditorUtility.CopySerialized(data.near[t], old.near[t]); data.near[t] = old.near[t];
                    EditorUtility.CopySerialized(data.far[t], old.far[t]); data.far[t] = old.far[t];
                }
                string assetName = old.name;
                EditorUtility.CopySerialized(data, old);
                old.name = assetName;
                data = old;
                EditorUtility.SetDirty(old);
            }
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            Object.DestroyImmediate(model.gameObject);
            Debug.Log($"[LSBS] {frames.Count / 3 / (nb + 3)} frames of {nb + 3} bones baked");
            return data;
        }

        // ------------------------------------------------------------------ kit

        static void Helmet(MeshBuilder mb, int team, Uniform u, Bounds head, bool near, System.Action on, System.Action captain)
        {
            on();
            float r = Mathf.Max(head.extents.x, head.extents.z);
            Vector3 c = new(0, head.max.y, head.center.z);
            int sides = near ? 14 : 8;
            if (team == Team.Green)
            {
                // a shallow bowl with a wide, flat brim
                float brim = r * 1.75f, bowl = r * 1.18f;
                mb.Lathe(c, new[]
                {
                    new Vector2(brim * 0.96f, -r * 0.42f), new Vector2(brim, -r * 0.36f), new Vector2(bowl * 1.04f, -r * 0.18f),
                    new Vector2(bowl * 0.96f, r * 0.12f), new Vector2(bowl * 0.62f, r * 0.34f), new Vector2(0.0f, r * 0.42f),
                }, sides, u.helmet);
                mb.Lathe(c, new[] { new Vector2(bowl * 0.9f, -r * 0.26f), new Vector2(brim * 0.96f, -r * 0.42f) }, sides, Rubber);
                if (near)
                {
                    mb.Lathe(c, new[] { new Vector2(bowl * 1.06f, -r * 0.16f), new Vector2(bowl * 1.07f, -r * 0.04f) }, sides, Leather);
                    // goggles pushed up on the band
                    for (int s = -1; s <= 1; s += 2)
                    {
                        Vector3 g = c + new Vector3(s * r * 0.4f, -r * 0.04f, bowl * 1.0f);
                        mb.Tube(g, g + new Vector3(s * 0.01f, 0.012f, 0.05f), r * 0.26f, r * 0.24f, 8, Brass);
                        mb.Tube(g + new Vector3(s * 0.01f, 0.012f, 0.05f), g + new Vector3(s * 0.01f, 0.013f, 0.054f), r * 0.18f, r * 0.18f, 8, Rubber);
                    }
                }
                captain();
                mb.Box(c + new Vector3(0, r * 0.46f, 0), new Vector3(0.025f, r * 0.5f, r * 1.7f), Brass);
            }
            else
            {
                // deep crown, flaring over the ears and neck, with a visor
                float crown = r * 1.2f;
                Vector2 Flare(float t, int ring)
                {
                    float front = Mathf.Max(0f, Mathf.Cos(t)), back = Mathf.Max(0f, -Mathf.Cos(t)), ear = Mathf.Abs(Mathf.Sin(t));
                    if (ring <= 1) return new Vector2(front * r * 0.22f + back * r * 0.2f, front * r * 0.34f - ear * r * 0.14f - back * r * 0.1f);
                    if (ring == 2) return new Vector2(0, front * r * 0.22f);
                    return Vector2.zero;
                }
                mb.Lathe(c, new[]
                {
                    new Vector2(crown * 1.16f, -r * 0.98f), new Vector2(crown * 1.2f, -r * 0.92f), new Vector2(crown * 1.02f, -r * 0.42f),
                    new Vector2(crown * 1.0f, -r * 0.05f), new Vector2(crown * 0.84f, r * 0.26f), new Vector2(crown * 0.5f, r * 0.43f), new Vector2(0f, r * 0.48f),
                }, sides, u.helmet, 1.08f, Flare);
                mb.Lathe(c, new[] { new Vector2(crown * 0.92f, -r * 0.5f), new Vector2(crown * 1.16f, -r * 0.98f) }, sides, Rubber, 1.08f, (t, ring) => ring == 0 ? Vector2.zero : Flare(t, 0));
                if (near)
                    for (int s = -1; s <= 1; s += 2)
                    {
                        // the vent lugs, in brass
                        Vector3 g = c + new Vector3(s * crown * 0.9f, r * 0.02f, 0.02f);
                        mb.Tube(g, g + new Vector3(s * 0.022f, 0.004f, 0), r * 0.12f, r * 0.1f, 8, Brass);
                    }
                captain();
                mb.Tube(c + new Vector3(0, r * 0.4f, 0), c + new Vector3(0, r * 0.56f, 0), r * 0.3f, r * 0.22f, 8, Brass);
                mb.Tube(c + new Vector3(0, r * 0.56f, 0), c + new Vector3(0, r * 1.25f, 0), r * 0.11f, 0.004f, 6, Brass);
            }
        }

        static void Mask(MeshBuilder mb, Uniform u, Bounds head, bool near)
        {
            float r = Mathf.Max(head.extents.x, head.extents.z);
            Vector3 c = head.center;
            float z = head.max.z;
            // a leather respirator: face plate, two round lenses, a brass canister
            mb.Box(new Vector3(0, c.y - r * 0.28f, z - r * 0.1f), new Vector3(r * 1.35f, r * 1.0f, r * 0.42f), Leather);
            if (!near)
            {
                mb.Box(new Vector3(0, c.y + r * 0.22f, z + r * 0.06f), new Vector3(r * 1.1f, r * 0.24f, 0.02f), u.lens);
                return;
            }
            mb.Box(new Vector3(0, c.y + r * 0.2f, z - r * 0.06f), new Vector3(r * 1.5f, r * 0.62f, r * 0.34f), Rubber);
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 e = new(s * r * 0.4f, c.y + r * 0.22f, z + r * 0.1f);
                mb.Tube(e, e + new Vector3(s * 0.006f, 0, 0.026f), r * 0.32f, r * 0.29f, 10, Brass);
                mb.Tube(e + new Vector3(s * 0.006f, 0, 0.026f), e + new Vector3(s * 0.006f, 0, 0.03f), r * 0.21f, r * 0.2f, 10, u.lens);
            }
            Vector3 f = new(0, c.y - r * 0.42f, z + r * 0.06f);
            mb.Tube(f, f + new Vector3(0, -0.028f, 0.075f), r * 0.3f, r * 0.34f, 10, Brass);
            mb.Tube(f + new Vector3(0, -0.028f, 0.075f), f + new Vector3(0, -0.034f, 0.092f), r * 0.28f, r * 0.2f, 10, Iron);
        }

        static void Pack(MeshBuilder mb, Uniform u, Bounds chest)
        {
            float back = chest.min.z, y = chest.center.y;
            // a little boiler on the back, strapped on
            Vector3 a = new(0, y - 0.2f, back - 0.1f), b = new(0, y + 0.2f, back - 0.1f);
            mb.Tube(a, b, 0.115f, 0.115f, 12, Copper);
            mb.Tube(b, b + Vector3.up * 0.05f, 0.115f, 0.05f, 12, Copper);
            for (int i = -1; i <= 1; i++) mb.Tube(new Vector3(0, y + i * 0.14f - 0.018f, back - 0.1f), new Vector3(0, y + i * 0.14f + 0.018f, back - 0.1f), 0.121f, 0.121f, 12, Brass);
            for (int s = -1; s <= 1; s += 2)
            {
                mb.Tube(new Vector3(s * 0.16f, y - 0.17f, back - 0.06f), new Vector3(s * 0.16f, y + 0.08f, back - 0.06f), 0.05f, 0.05f, 8, Iron);
                mb.Tube(new Vector3(s * 0.16f, y + 0.08f, back - 0.06f), new Vector3(s * 0.12f, y + 0.2f, back - 0.1f), 0.016f, 0.016f, 6, Brass);
                // shoulder straps
                mb.Box(new Vector3(s * 0.12f, y + 0.02f, chest.max.z + 0.004f), new Vector3(0.05f, chest.size.y * 0.8f, 0.02f), Leather);
            }
            mb.Tube(b + new Vector3(0.05f, 0.02f, -0.02f), b + new Vector3(0.05f, 0.16f, -0.02f), 0.022f, 0.028f, 8, Iron); // flue
            mb.Tube(new Vector3(0, y + 0.06f, back - 0.21f), new Vector3(0, y + 0.06f, back - 0.235f), 0.045f, 0.042f, 10, Brass); // gauge
            mb.Tube(new Vector3(0, y + 0.06f, back - 0.235f), new Vector3(0, y + 0.06f, back - 0.238f), 0.032f, 0.032f, 10, MeshBuilder.Col(0xe8dcc0));
            // chest plate rivets: a small brass regulator
            mb.Tube(new Vector3(-0.02f, y - 0.02f, chest.max.z), new Vector3(-0.02f, y - 0.02f, chest.max.z + 0.04f), 0.04f, 0.035f, 8, Brass);
        }

        static void CaptainPennant(MeshBuilder mb, Uniform u, Bounds chest)
        {
            float back = chest.min.z, y = chest.center.y;
            Vector3 foot = new(-0.13f, y, back - 0.14f), top = foot + new Vector3(0, 0.95f, -0.06f);
            mb.Tube(foot, top, 0.012f, 0.01f, 6, Brass);
            Vector3 a = top - new Vector3(0, 0.02f, 0), b = a - new Vector3(0, 0.24f, -0.015f), tip = a + new Vector3(0, -0.12f, -0.36f);
            mb.Tri(a, b, tip, u.cloth);
            mb.Tri(a, tip, b, u.cloth);
            for (int s = -1; s <= 1; s += 2) mb.Box(new Vector3(s * (chest.extents.x + 0.02f), chest.max.y - 0.1f, chest.center.z), new Vector3(0.14f, 0.04f, 0.17f), Quaternion.Euler(0, 0, -s * 22f), Brass);
        }

        static void Belt(MeshBuilder mb, Uniform u, Bounds hips)
        {
            float y = hips.center.y + 0.04f;
            float rx = hips.extents.x + 0.03f, rz = hips.extents.z + 0.03f;
            float stretch = rz / rx;
            mb.Lathe(new Vector3(0, y, hips.center.z), new[] { new Vector2(rx, -0.035f), new Vector2(rx, 0.035f) }, 12, Leather, stretch);
            mb.Box(new Vector3(0, y, hips.center.z + rz + 0.01f), new Vector3(0.07f, 0.06f, 0.02f), Brass);
            for (int s = -1; s <= 1; s += 2)
            {
                mb.Box(new Vector3(s * rx * 0.62f, y - 0.01f, hips.center.z + rz * 0.86f), new Vector3(0.1f, 0.1f, 0.06f), Quaternion.Euler(0, s * 32f, 0), Leather);
                mb.Box(new Vector3(s * rx * 0.98f, y - 0.02f, hips.center.z + rz * 0.2f), new Vector3(0.06f, 0.1f, 0.1f), Leather);
            }
            // the skirt of the greatcoat, open at the front
            const int n = 10;
            for (int i = 0; i < n; i++)
            {
                float t0 = Mathf.Lerp(32f, 328f, (float)i / n) * Mathf.Deg2Rad, t1 = Mathf.Lerp(32f, 328f, (float)(i + 1) / n) * Mathf.Deg2Rad;
                Vector3 P(float t, float k, float py) => new(Mathf.Sin(t) * rx * k, py, hips.center.z + Mathf.Cos(t) * rz * k);
                Vector3 a = P(t0, 0.98f, y), b = P(t1, 0.98f, y), c = P(t1, 1.32f, y - 0.3f), d = P(t0, 1.32f, y - 0.3f);
                mb.Quad(b, a, d, c, u.coat * new Color(0.85f, 0.85f, 0.85f, 1));
                mb.Quad(a, b, c, d, u.coat * new Color(0.5f, 0.5f, 0.5f, 1));
            }
        }

        // ------------------------------------------------------------------ the ragdoll

        static readonly (string start, string end, float radius, int parent, float mass, bool spine)[] Segments =
        {
            ("Bone", "Bone.003", 0.17f, -1, 10f, true), ("Bone.003", "Bone.001", 0.19f, 0, 10f, true), ("Bone.001", "Bone.001_end", 0.15f, 1, 4f, true),
            ("Arm L.001", "Arm L.002", 0.07f, 1, 2f, false), ("Arm L.002", "Arm L.003_end", 0.06f, 3, 1.5f, false),
            ("Arm R.001", "Arm R.002", 0.07f, 1, 2f, false), ("Arm R.002", "Arm R.003_end", 0.06f, 5, 1.5f, false),
            ("Leg L.001", "Leg L.002", 0.10f, 0, 5f, false), ("Leg L.002", "Leg L.003_end", 0.08f, 7, 3f, false),
            ("Leg R.001", "Leg R.002", 0.10f, 0, 5f, false), ("Leg R.002", "Leg R.003_end", 0.08f, 9, 3f, false),
        };

        static GameObject BuildRagdoll(Transform model, PoseSolver solver, float feet, RifleParts rifle)
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
                var j = a.gameObject.AddComponent<CharacterJoint>();
                j.connectedBody = bodies[s.parent];
                j.axis = Vector3.right;
                j.swingAxis = Vector3.forward;
                j.lowTwistLimit = new SoftJointLimit { limit = s.spine ? -25f : -60f };
                j.highTwistLimit = new SoftJointLimit { limit = s.spine ? 25f : 60f };
                j.swing1Limit = new SoftJointLimit { limit = s.spine ? 30f : 70f };
                j.swing2Limit = new SoftJointLimit { limit = s.spine ? 30f : 50f };
                j.enableProjection = true;
                j.enablePreprocessing = false;
            }
            Rigidbody Loose(string name, float mass, System.Action<GameObject> collider)
            {
                var o = new GameObject(name);
                o.transform.SetParent(root.transform, false);
                var rb = o.AddComponent<Rigidbody>();
                rb.mass = mass; rb.angularDamping = 0.6f; rb.isKinematic = true; rb.interpolation = RigidbodyInterpolation.Interpolate;
                collider(o);
                return rb;
            }
            Loose("RifleBody", 3f, o => { var c = o.AddComponent<BoxCollider>(); c.center = new Vector3(0, 0.03f, 0.1f); c.size = new Vector3(0.05f, 0.12f, 1.1f); });
            Loose("HelmetBody", 1.2f, o => { var c = o.AddComponent<SphereCollider>(); c.radius = 0.16f; });
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, RagdollPath);
            Object.DestroyImmediate(root);
            return prefab;
        }
    }
}
