using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace LSBS.EditorTools
{
    /// <summary>
    /// What each side wears, as a prefab to edit (Assets/LSBS/Kit/Green Kit and Grey Kit): the body as a guide, and a group
    /// for each place on it. Anything put under a group is worn there; anything else is left alone. The mask is a prefab of
    /// its own (Mask), worn by both, and so is the grenade (Grenade).
    ///  - Materials give the colours: Metallic 0.5 or more is metal, Emission on glows.
    ///  - The materials on the guide body are the uniform's colours.
    ///  - "LSBS > Rebuild Soldiers" puts the kits on the soldiers. It never changes a kit.
    /// </summary>
    public static partial class SoldierBaker
    {
        const string KitFolder = "Assets/LSBS/Kit", MaskPath = KitFolder + "/Mask.prefab", GrenadePath = KitFolder + "/Grenade.prefab";
        const string Guide = "Body (guide)";
        /// <summary>The mark of the set of models the kits were last made from. A kit without it is remade (what isn't in a group is kept).</summary>
        const string ModelMark = "Models 7 (leave this here)";
        static readonly string[] Zones = { "Coat", "Trousers", "Puttees", "Boots", "Gloves", "Head" };

        static string KitPath(int team) => $"{KitFolder}/{(team == 0 ? "Green" : "Grey")} Kit.prefab";

        [MenuItem("LSBS/Edit Green Kit")] static void EditGreen() => Open(KitPath(0));
        [MenuItem("LSBS/Edit Grey Kit")] static void EditGrey() => Open(KitPath(1));
        [MenuItem("LSBS/Edit Mask")] static void EditMask() => Open(MaskPath);
        [MenuItem("LSBS/Edit Grenade")] static void EditGrenade() => Open(GrenadePath);

        static void Open(string path)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) Build();
            AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<GameObject>(path));
        }

        /// <summary>Group name; the bone it's worn on ("*" = the helmet, which can be knocked off); who wears it; how many of its triangles show from a distance.</summary>
        static readonly (string name, string bone, int forWhom, int far)[] Groups =
        {
            ("Helmet", "*", ForAll, 40), ("Mask", "Bone.001", ForAll, 24), ("Back", "Bone.003", NotCommunicator, 40), ("Back (communicator)", "Bone.003", ForCommunicator, 30),
            ("Back (gunner, carrying)", "Bone.003", WhenCarrying, 34), ("Back (gunner)", "Bone.003", ForGunner, 16),
            ("Chest", "Bone.003", ForAll, 0), ("Hips", "Bone", ForAll, 36), ("Hips (sweeper)", "Bone", ForSweeper, 0),
            ("Upper Arm L", "Arm L.001", ForAll, 0), ("Upper Arm L (captain)", "Arm L.001", ForCaptain, 12), ("Forearm L", "Arm L.002", ForAll, 0),
            ("Upper Arm R", "Arm R.001", ForAll, 0), ("Forearm R", "Arm R.002", ForAll, 0),
            ("Thigh L", "Leg L.001", ForAll, 0), ("Shin L", "Leg L.002", ForAll, 0), ("Foot L", "Leg L.003", ForAll, 0),
            ("Thigh R", "Leg R.001", ForAll, 0), ("Shin R", "Leg R.002", ForAll, 0), ("Foot R", "Leg R.003", ForAll, 0),
        };
        /// <summary>Groups there used to be.</summary>
        static readonly string[] Retired = { "Helmet (captain)", "Back (captain)" };

        static int ZoneOf(string bone)
        {
            if (bone == "Bone.001") return 5;
            if (bone.StartsWith("Leg")) return bone.EndsWith(".003") ? 3 : bone.EndsWith(".002") ? 2 : 1;
            if (bone.StartsWith("Arm") && bone.EndsWith(".003")) return 4;
            return 0;
        }

        // ------------------------------------------------------------------ making them to start from

        static Material Mat(string name, Color linear, bool recolour)
        {
            string path = $"{KitFolder}/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null && !recolour) return m;
            bool made = m == null;
            if (made) m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var c = linear; c.a = 1f; c = c.gamma; c.a = 1f;
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Metallic", linear.a > 0.25f && linear.a < 0.75f ? 0.6f : 0f);
            m.SetFloat("_Smoothness", 0.25f);
            m.DisableKeyword("_EMISSION");
            if (made) AssetDatabase.CreateAsset(m, path);
            EditorUtility.SetDirty(m);
            return m;
        }

        static string NameOf(Color c)
        {
            foreach (var kv in Palette)
                if (Mathf.Abs(kv.Value.r - c.r) + Mathf.Abs(kv.Value.g - c.g) + Mathf.Abs(kv.Value.b - c.b) + Mathf.Abs(kv.Value.a - c.a) < 0.0005f) return kv.Key;
            // a shade of one of them
            string best = null;
            float bestD = 1f;
            foreach (var kv in Palette)
            {
                Vector3 a = new(kv.Value.r, kv.Value.g, kv.Value.b), b = new(c.r, c.g, c.b);
                float d = Vector3.Angle(a, b) / 10f + Mathf.Abs(a.magnitude - b.magnitude) * 3f;
                if (Mathf.Abs(kv.Value.a - c.a) < 0.1f && d < bestD) { bestD = d; best = kv.Key; }
            }
            return best != null ? $"{best} (shade {Mathf.RoundToInt(c.maxColorComponent * 1000f)})" : "Colour " + ColorUtility.ToHtmlStringRGB(c.gamma);
        }

        /// <summary>One object a material under a parent, so each can be seen, moved or thrown away.</summary>
        static void Put(Transform parent, string meshName, MeshBuilder mb)
        {
            foreach (var colour in mb.I.Select(i => mb.C[i]).Distinct().ToArray())
            {
                var part = new MeshBuilder();
                var moved = new Dictionary<int, int>();
                for (int i = 0; i + 2 < mb.I.Count; i += 3)
                {
                    if (mb.C[mb.I[i]] != colour) continue;
                    for (int c = 0; c < 3; c++)
                    {
                        int v = mb.I[i + c];
                        if (!moved.TryGetValue(v, out int to))
                        {
                            moved[v] = to = part.V.Count;
                            part.V.Add(mb.V[v]); part.N.Add(mb.N[v]); part.C.Add(colour); part.B.Add(default);
                        }
                        part.I.Add(to);
                    }
                }
                string name = NameOf(colour);
                string path = $"{KitFolder}/Meshes/{meshName} {name}.asset";
                var m = part.Build($"{meshName} {name}");
                var had = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (had != null) { EditorUtility.CopySerialized(m, had); m = had; }
                else AssetDatabase.CreateAsset(m, path);
                var o = new GameObject(name);
                o.transform.SetParent(parent, false);
                o.AddComponent<MeshFilter>().sharedMesh = m;
                o.AddComponent<MeshRenderer>().sharedMaterial = Mat(name, colour, true);
            }
        }

        static void Folders()
        {
            System.IO.Directory.CreateDirectory($"{KitFolder}/Meshes");
            System.IO.Directory.CreateDirectory($"{KitFolder}/Materials");
            AssetDatabase.Refresh();
        }

        static void EnsureMask(Fit fit)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(MaskPath) != null) return;
            Folders();
            var root = new GameObject("Mask");
            Put(root.transform, "Mask", MaskModel(fit.head));
            PrefabUtility.SaveAsPrefabAsset(root, MaskPath);
            Object.DestroyImmediate(root);
        }

        static void EnsureGrenade()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(GrenadePath) != null) return;
            Folders();
            // the one modelled in the green kit, if it's there
            GameObject root = null;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(KitPath(0)) != null)
            {
                var kit = PrefabUtility.LoadPrefabContents(KitPath(0));
                var g = kit.transform.Find("Grenade");
                if (g != null) { root = Object.Instantiate(g.gameObject); root.name = "Grenade"; }
                PrefabUtility.UnloadPrefabContents(kit);
            }
            if (root == null)
            {
                root = new GameObject("Grenade");
                var mb = new MeshBuilder();
                mb.Ball(Vector3.zero, new Vector3(0.045f, 0.06f, 0.045f), P("Iron"), 10, 7);
                mb.SmoothTube(new Vector3(0, 0.05f, 0), new Vector3(0, 0.09f, 0), 0.014f, 0.014f, 8, P("Brass"));
                Put(root.transform, "Grenade", mb);
            }
            PrefabUtility.SaveAsPrefabAsset(root, GrenadePath);
            Object.DestroyImmediate(root);
        }

        /// <summary>The grenade, about its own middle, a hand's length long.</summary>
        static MeshBuilder GrenadeMesh(float scale, Vector3 at)
        {
            var part = new KitPart { v = new(), n = new(), c = new(), i = new() };
            var go = PrefabUtility.LoadPrefabContents(GrenadePath);
            foreach (var pb in go.GetComponentsInChildren<ProBuilderMesh>(true)) { pb.ToMesh(); pb.Refresh(); }
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(false))
                if (r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null)
                    Add(part, Readable(mf.sharedMesh), go.transform.worldToLocalMatrix * r.transform.localToWorldMatrix, r.sharedMaterials);
            PrefabUtility.UnloadPrefabContents(go);
            var mb = new MeshBuilder();
            if (part.v.Count == 0) return mb;
            Bounds b = new(part.v[0], Vector3.zero);
            foreach (var v in part.v) b.Encapsulate(v);
            float fit = 0.17f / Mathf.Max(0.001f, Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z))) * scale;
            for (int i = 0; i < part.v.Count; i++) { mb.V.Add((part.v[i] - b.center) * fit + at); mb.N.Add(part.n[i]); mb.C.Add(part.c[i]); mb.B.Add(default); }
            mb.I.AddRange(part.i);
            return mb;
        }

        static void EnsureKit(int team, Mesh body, int[] zoneOfVertex, Vector3[] posed, Vector3[] posedNormals, Fit fit)
        {
            string path = KitPath(team), side = team == 0 ? "Green" : "Grey";
            bool exists = AssetDatabase.LoadAssetAtPath<GameObject>(path) != null;
            var root = exists ? PrefabUtility.LoadPrefabContents(path) : new GameObject(side + " Kit");
            if (exists && root.transform.Find(ModelMark) != null) { PrefabUtility.UnloadPrefabContents(root); return; }
            Folders();

            // the groups are cleared and filled afresh; anything that isn't one of them stays as it is
            foreach (string name in Groups.Select(g => g.name).Concat(Retired))
            {
                var t = root.transform.Find(name);
                if (t != null) Object.DestroyImmediate(t.gameObject);
            }
            var guide = root.transform.Find(Guide);
            if (guide == null)
            {
                guide = new GameObject(Guide).transform;
                guide.SetParent(root.transform, false);
                var gm = new Mesh { name = side + " Body", vertices = posed, normals = posedNormals, subMeshCount = Zones.Length };
                var tris = new List<int>[Zones.Length];
                for (int z = 0; z < Zones.Length; z++) tris[z] = new List<int>();
                for (int s = 0; s < body.subMeshCount; s++)
                {
                    var t = body.GetTriangles(s);
                    for (int i = 0; i < t.Length; i += 3) tris[zoneOfVertex[t[i]]].AddRange(new[] { t[i], t[i + 1], t[i + 2] });
                }
                for (int z = 0; z < Zones.Length; z++) gm.SetTriangles(tris[z], z);
                gm.RecalculateBounds();
                AssetDatabase.CreateAsset(gm, AssetDatabase.GenerateUniqueAssetPath($"{KitFolder}/Meshes/{gm.name}.asset"));
                guide.gameObject.AddComponent<MeshFilter>().sharedMesh = gm;
                guide.gameObject.AddComponent<MeshRenderer>();
            }
            guide.GetComponent<MeshRenderer>().sharedMaterials = Zones.Select(z => Mat($"{side} {z}", P($"{side} {z}"), true)).ToArray();

            var outfit = Outfit(team, fit);
            foreach (var g in Groups)
            {
                var t = new GameObject(g.name).transform;
                t.SetParent(root.transform, false);
                if (g.name == "Mask")
                {
                    var mask = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(MaskPath), t);
                    mask.name = "Mask";
                }
                else if (g.name == "Hips (sweeper)")
                {
                    // two grenades hung at the belt
                    for (int s = -1; s <= 1; s += 2)
                    {
                        var gm = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(GrenadePath), t);
                        gm.name = s < 0 ? "Grenade L" : "Grenade R";
                        foreach (var pb in gm.GetComponentsInChildren<ProBuilderMesh>(true)) { pb.ToMesh(); pb.Refresh(); }
                        Bounds b = default;
                        bool any = false;
                        foreach (var r in gm.GetComponentsInChildren<MeshRenderer>()) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
                        if (!any) continue;
                        float size = 0.15f / Mathf.Max(0.001f, Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)));
                        gm.transform.localScale = Vector3.one * size;
                        Vector3 centre = root.transform.InverseTransformPoint(b.center);
                        gm.transform.localPosition = new Vector3(s * 0.15f, 0.16f, fit.hips.center.z + 0.16f) - centre * size;
                    }
                }
                else if (outfit.TryGetValue(g.name, out var mb)) Put(t, $"{side} {g.name}", mb);
            }
            new GameObject(ModelMark).transform.SetParent(root.transform, false);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            if (exists) PrefabUtility.UnloadPrefabContents(root); else Object.DestroyImmediate(root);
            Debug.Log($"[LSBS] made {path}");
        }

        // ------------------------------------------------------------------ putting it on

        struct KitPart { public string bone; public int forWhom, farTriangles; public List<Vector3> v, n; public List<Color> c; public List<int> i; }

        static (List<KitPart> parts, Color[] zones) ReadKit(int team)
        {
            var go = PrefabUtility.LoadPrefabContents(KitPath(team));
            var parts = new List<KitPart>();
            var zones = new Color[Zones.Length];
            try
            {
                foreach (var pb in go.GetComponentsInChildren<ProBuilderMesh>(true)) { pb.ToMesh(); pb.Refresh(); }
                var guide = go.transform.Find(Guide);
                var mats = guide != null && guide.TryGetComponent<MeshRenderer>(out var gr) ? gr.sharedMaterials : new Material[0];
                for (int z = 0; z < zones.Length; z++) zones[z] = Colour(z < mats.Length ? mats[z] : null);

                foreach (var g in Groups)
                {
                    var t = go.transform.Find(g.name);
                    if (t == null) continue;
                    var part = new KitPart { bone = g.bone, forWhom = g.forWhom, farTriangles = g.far, v = new(), n = new(), c = new(), i = new() };
                    foreach (var r in t.GetComponentsInChildren<Renderer>(false))
                    {
                        Mesh m = null;
                        Matrix4x4 at = go.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                        if (r is MeshRenderer && r.TryGetComponent<MeshFilter>(out var mf)) m = mf.sharedMesh;
                        else if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                        {
                            m = new Mesh();
                            smr.BakeMesh(m, true);
                        }
                        if (m == null || !r.enabled) continue;
                        Add(part, Readable(m), at, r.sharedMaterials);
                    }
                    if (part.i.Count > 0) parts.Add(part);
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(go); }
            return (parts, zones);
        }

        static Mesh Readable(Mesh m)
        {
            if (m.isReadable) return m;
            string path = AssetDatabase.GetAssetPath(m);
            if (AssetImporter.GetAtPath(path) is ModelImporter importer)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
                return AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().FirstOrDefault(x => x.name == m.name) ?? m;
            }
            return m;
        }

        static void Add(KitPart part, Mesh m, Matrix4x4 at, Material[] mats)
        {
            if (!m.isReadable) { Debug.LogWarning($"[LSBS] {m.name} can't be read: turn on Read/Write in its import settings"); return; }
            var v = m.vertices;
            var n = m.normals;
            var uv = m.uv;
            bool flip = at.determinant < 0f;
            var turn = at.inverse.transpose;
            for (int s = 0; s < m.subMeshCount; s++)
            {
                if (m.GetTopology(s) != MeshTopology.Triangles) continue;
                var mat = mats.Length == 0 ? null : mats[Mathf.Min(s, mats.Length - 1)];
                Color colour = Colour(mat);
                var tex = Texture(mat);
                var t = m.GetTriangles(s);
                var moved = new Dictionary<int, int>();
                int Corner(int k)
                {
                    if (moved.TryGetValue(k, out int to)) return to;
                    to = moved[k] = part.v.Count;
                    part.v.Add(at.MultiplyPoint3x4(v[k]));
                    part.n.Add(n.Length == v.Length ? turn.MultiplyVector(n[k]).normalized : Vector3.up);
                    var col = colour;
                    if (tex != null && uv.Length == v.Length)
                    {
                        var q = tex.GetPixelBilinear(uv[k].x, uv[k].y).linear;
                        col = new Color(col.r * q.r, col.g * q.g, col.b * q.b, col.a);
                    }
                    part.c.Add(col);
                    return to;
                }
                for (int i = 0; i < t.Length; i += 3)
                {
                    int a = Corner(t[i]), b = Corner(flip ? t[i + 2] : t[i + 1]), c = Corner(flip ? t[i + 1] : t[i + 2]);
                    part.i.Add(a); part.i.Add(b); part.i.Add(c);
                }
            }
        }

        /// <summary>A material as the soldiers' shader wants it: linear colour, and what it's made of.</summary>
        static Color Colour(Material m)
        {
            if (m == null) return MeshBuilder.Col(0x808080);
            Color c = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.grey;
            c = c.linear;
            bool glow = m.IsKeywordEnabled("_EMISSION") && m.HasProperty("_EmissionColor") && m.GetColor("_EmissionColor").maxColorComponent > 0.02f;
            bool metal = m.HasProperty("_Metallic") && m.GetFloat("_Metallic") >= 0.5f;
            c.a = glow ? MeshBuilder.Glow : metal ? MeshBuilder.Metal : MeshBuilder.Matte;
            return c;
        }

        static readonly Dictionary<Texture, Texture2D> TextureCopies = new();

        /// <summary>Soldiers carry no textures: one on a material is read into the colour of each corner.</summary>
        static Texture2D Texture(Material m)
        {
            if (m == null || !m.HasProperty("_BaseMap")) return null;
            var t = m.GetTexture("_BaseMap");
            if (t == null) return null;
            if (TextureCopies.TryGetValue(t, out var copy) && copy != null) return copy;
            var rt = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(t, rt);
            var was = RenderTexture.active;
            RenderTexture.active = rt;
            copy = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
            copy.Apply();
            RenderTexture.active = was;
            RenderTexture.ReleaseTemporary(rt);
            return TextureCopies[t] = copy;
        }

        /// <summary>However detailed a piece is, only so many triangles of it are drawn from a distance.</summary>
        static KitPart Far(KitPart piece)
        {
            var p = Simplify(piece, 0.04f);
            for (float cell = 0.048f; p.i.Count > piece.farTriangles * 3 && cell < 0.6f; cell *= 1.18f) p = Simplify(piece, cell);
            return p;
        }

        /// <summary>The same piece with its corners snapped together, for soldiers in the distance.</summary>
        static KitPart Simplify(KitPart p, float cell)
        {
            var o = p;
            o.v = new(); o.n = new(); o.c = new(); o.i = new();
            var sum = new Dictionary<Vector3Int, (Vector3 total, int count)>();
            Vector3Int Key(Vector3 v) => Vector3Int.RoundToInt(v / cell);
            foreach (var v in p.v)
            {
                var k = Key(v);
                sum.TryGetValue(k, out var s);
                sum[k] = (s.total + v, s.count + 1);
            }
            var seen = new HashSet<(Vector3Int, Vector3Int, Vector3Int)>();
            for (int i = 0; i < p.i.Count; i += 3)
            {
                var ka = Key(p.v[p.i[i]]); var kb = Key(p.v[p.i[i + 1]]); var kc = Key(p.v[p.i[i + 2]]);
                if (ka == kb || kb == kc || ka == kc || !seen.Add((ka, kb, kc))) continue;
                Vector3 A(Vector3Int k) => sum[k].total / sum[k].count;
                Vector3 a = A(ka), b = A(kb), c = A(kc), n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-10f) continue;
                n.Normalize();
                foreach (var v in new[] { a, b, c }) { o.i.Add(o.v.Count); o.v.Add(v); o.n.Add(n); o.c.Add(p.c[p.i[i]]); }
            }
            return o;
        }
    }
}
