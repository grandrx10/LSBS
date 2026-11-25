using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace LSBS.EditorTools
{
    /// <summary>
    /// What each side wears, as a prefab to edit (Assets/LSBS/Kit/Green Kit and Grey Kit): the body as a guide, and a group
    /// for each place on it. Anything put under a group is worn there. The kits are made once and never overwritten.
    ///  - Materials give the colours: Metallic 0.5 or more is metal, Emission on glows.
    ///  - The materials on the guide body are the uniform's colours.
    ///  - "LSBS > Rebuild Soldiers" puts the kits on the soldiers.
    /// </summary>
    public static partial class SoldierBaker
    {
        const string KitFolder = "Assets/LSBS/Kit";
        const string Guide = "Body (guide)";
        static readonly string[] Zones = { "Coat", "Trousers", "Puttees", "Boots", "Gloves", "Head" };

        static string KitPath(int team) => $"{KitFolder}/{(team == 0 ? "Green" : "Grey")} Kit.prefab";

        [MenuItem("LSBS/Edit Green Kit")] static void EditGreen() => OpenKit(0);
        [MenuItem("LSBS/Edit Grey Kit")] static void EditGrey() => OpenKit(1);

        static void OpenKit(int team)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(KitPath(team)) == null) Build();
            AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<GameObject>(KitPath(team)));
        }

        /// <summary>Group name, the bone it's worn on ("*" = the helmet, which can be knocked off), captains only.</summary>
        static readonly (string name, string bone, bool captain)[] Groups =
        {
            ("Helmet", "*", false), ("Helmet (captain)", "*", true), ("Mask", "Bone.001", false), ("Back", "Bone.003", false),
            ("Back (captain)", "Bone.003", true), ("Chest", "Bone.003", false), ("Hips", "Bone", false),
            ("Upper Arm L", "Arm L.001", false), ("Forearm L", "Arm L.002", false), ("Upper Arm R", "Arm R.001", false), ("Forearm R", "Arm R.002", false),
            ("Thigh L", "Leg L.001", false), ("Shin L", "Leg L.002", false), ("Thigh R", "Leg R.001", false), ("Shin R", "Leg R.002", false),
        };

        static int ZoneOf(string bone)
        {
            if (bone == "Bone.001") return 5;
            if (bone.StartsWith("Leg")) return bone.EndsWith(".003") ? 3 : bone.EndsWith(".002") ? 2 : 1;
            if (bone.StartsWith("Arm") && bone.EndsWith(".003")) return 4;
            return 0;
        }

        // ------------------------------------------------------------------ making one to start from

        static void EnsureKit(int team, Mesh body, int[] zoneOfVertex, Vector3[] posed, Vector3[] posedNormals, Bounds head, Bounds chest, Bounds hips)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(KitPath(team)) != null) return;
            string side = team == 0 ? "Green" : "Grey";
            System.IO.Directory.CreateDirectory($"{KitFolder}/Meshes");
            System.IO.Directory.CreateDirectory($"{KitFolder}/Materials");
            AssetDatabase.Refresh();
            var u = Uniforms[team];
            var root = new GameObject(side + " Kit");

            var names = new Dictionary<Color, string>
            {
                [Brass] = "Brass", [Copper] = "Copper", [Iron] = "Iron", [Steel] = "Steel", [Leather] = "Leather", [Rubber] = "Rubber", [Skin] = side + " Head",
                [u.coat] = side + " Coat", [u.trousers] = side + " Trousers", [u.puttees] = side + " Puttees", [u.helmet] = side + " Helmet",
                [u.lens] = side + " Lens", [u.cloth] = side + " Cloth", [MeshBuilder.Col(0xe8dcc0)] = "Dial",
            };

            Material Mat(string name, Color linear)
            {
                string path = $"{KitFolder}/Materials/{name}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m != null) return m;
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                var c = linear; c.a = 1f; c = c.gamma;
                m.SetColor("_BaseColor", c);
                m.SetFloat("_Metallic", linear.a > 0.25f && linear.a < 0.75f ? 0.6f : 0f);
                m.SetFloat("_Smoothness", 0.25f);
                if (linear.a > 0.75f) { m.EnableKeyword("_EMISSION"); m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive; m.SetColor("_EmissionColor", c); }
                AssetDatabase.CreateAsset(m, path);
                return m;
            }

            // the body to fit things to
            var guide = new GameObject(Guide);
            guide.transform.SetParent(root.transform, false);
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
            AssetDatabase.CreateAsset(gm, $"{KitFolder}/Meshes/{gm.name}.asset");
            guide.AddComponent<MeshFilter>().sharedMesh = gm;
            Color[] zoneColours = { u.coat, u.trousers, u.puttees, Leather, Leather, Skin };
            guide.AddComponent<MeshRenderer>().sharedMaterials = Zones.Select((z, i) => Mat($"{side} {z}", zoneColours[i])).ToArray();

            Transform Group(string name)
            {
                var t = root.transform.Find(name);
                if (t != null) return t;
                t = new GameObject(name).transform;
                t.SetParent(root.transform, false);
                return t;
            }
            foreach (var g in Groups) Group(g.name);

            // one object a material, so each can be seen, moved or thrown away
            void Put(string group, MeshBuilder mb, int from, int to)
            {
                foreach (var colour in Enumerable.Range(from, to - from).Select(i => mb.C[mb.I[i]]).Distinct().ToArray())
                {
                    var part = new MeshBuilder();
                    for (int i = from; i + 2 < to; i += 3)
                        if (mb.C[mb.I[i]] == colour) part.Tri(mb.V[mb.I[i]], mb.V[mb.I[i + 1]], mb.V[mb.I[i + 2]], colour);
                    if (part.I.Count == 0) continue;
                    if (!names.TryGetValue(colour, out string name)) name = "Colour " + ColorUtility.ToHtmlStringRGB(colour.gamma);
                    var m = part.Build($"{side} {group} {name}");
                    AssetDatabase.CreateAsset(m, AssetDatabase.GenerateUniqueAssetPath($"{KitFolder}/Meshes/{m.name}.asset"));
                    var o = new GameObject(name);
                    o.transform.SetParent(Group(group), false);
                    o.AddComponent<MeshFilter>().sharedMesh = m;
                    o.AddComponent<MeshRenderer>().sharedMaterial = Mat(name, colour);
                }
            }

            var helmet = new MeshBuilder();
            int captainFrom = 0;
            Helmet(helmet, team, u, head, true, () => { }, () => captainFrom = helmet.I.Count);
            Put("Helmet", helmet, 0, captainFrom);
            Put("Helmet (captain)", helmet, captainFrom, helmet.I.Count);
            void Piece(string group, System.Action<MeshBuilder> make)
            {
                var mb = new MeshBuilder();
                make(mb);
                Put(group, mb, 0, mb.I.Count);
            }
            Piece("Mask", mb => Mask(mb, u, head, true));
            Piece("Back", mb => Pack(mb, u, chest));
            Piece("Back (captain)", mb => CaptainPennant(mb, u, chest));
            Piece("Hips", mb => Belt(mb, u, hips));
            PrefabUtility.SaveAsPrefabAsset(root, KitPath(team));
            Object.DestroyImmediate(root);
            Debug.Log($"[LSBS] made {KitPath(team)}");
        }

        // ------------------------------------------------------------------ putting it on

        struct KitPart { public string bone; public bool captain; public List<Vector3> v, n; public List<Color> c; public List<int> i; }

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
                    var part = new KitPart { bone = g.bone, captain = g.captain, v = new(), n = new(), c = new(), i = new() };
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
                for (int i = 0; i < t.Length; i += 3)
                {
                    int a = t[i], b = flip ? t[i + 2] : t[i + 1], c = flip ? t[i + 1] : t[i + 2];
                    Vector3 pa = at.MultiplyPoint3x4(v[a]), pb = at.MultiplyPoint3x4(v[b]), pc = at.MultiplyPoint3x4(v[c]);
                    Vector3 face = Vector3.Cross(pb - pa, pc - pa).normalized;
                    void Corner(int k, Vector3 p)
                    {
                        part.i.Add(part.v.Count);
                        part.v.Add(p);
                        part.n.Add(n.Length == v.Length ? turn.MultiplyVector(n[k]).normalized : face);
                        var col = colour;
                        if (tex != null && uv.Length == v.Length)
                        {
                            var q = tex.GetPixelBilinear(uv[k].x, uv[k].y).linear;
                            col = new Color(col.r * q.r, col.g * q.g, col.b * q.b, col.a);
                        }
                        part.c.Add(col);
                    }
                    Corner(a, pa); Corner(b, pb); Corner(c, pc);
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

        /// <summary>However detailed a piece is, this many triangles of it at most are drawn from a distance.</summary>
        const int FarTriangles = 36;

        static KitPart Far(KitPart piece)
        {
            var p = Simplify(piece, 0.05f);
            for (float cell = 0.06f; p.i.Count > FarTriangles * 3 && cell < 0.5f; cell *= 1.2f) p = Simplify(piece, cell);
            return p;
        }

        /// <summary>The same piece with its corners snapped together, for soldiers in the distance.</summary>
        static KitPart Simplify(KitPart p, float cell)
        {
            var o = new KitPart { bone = p.bone, captain = p.captain, v = new(), n = new(), c = new(), i = new() };
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
