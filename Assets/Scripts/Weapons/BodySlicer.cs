using System.Collections.Generic;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Cuts a combatant in two along a plane (sword kills). Each half is a full physics ragdoll
    /// (RagdollFactory) whose skinned mesh only keeps the triangles on its side of the cut, with the
    /// open cross-section capped by a "flesh" face. The mesh stays skinned, so each half flops around
    /// on its joints like a normal corpse. Bones belonging to the other half stay attached but are
    /// weightless and collisionless, so they're invisible and don't disturb the motion.
    /// </summary>
    public static class BodySlicer
    {
        public static float Lifetime = 20f;
        public static Color FleshColor = new(0.55f, 0.04f, 0.04f);
        static Material _flesh;

        struct CutPoint
        {
            public Vector3 bind, world;
            public BoneWeight weight;
        }

        /// <summary>One side of one skinned mesh while it's being built (bind-pose vertices).</summary>
        class Side
        {
            public readonly List<Vector3> pos = new();
            public readonly List<Vector3> nrm = new();
            public readonly List<Vector2> uv = new();
            public readonly List<BoneWeight> bw = new();
            public readonly List<List<int>> subs = new();
            public readonly List<(CutPoint a, CutPoint b)> cutEdges = new();

            public int Add(Vector3 p, Vector3 n, Vector2 t, BoneWeight w)
            {
                pos.Add(p); nrm.Add(n); uv.Add(t); bw.Add(w);
                return pos.Count - 1;
            }
        }

        /// <summary>Everyone: replace the combatant's body with two cut ragdoll halves. Returns the root to clean up later.</summary>
        public static GameObject Slice(Combatant c, Vector3 point, Vector3 normal, Vector3 impulse, Vector3 velocity)
        {
            if (normal.sqrMagnitude < 1e-6f) normal = Vector3.up;
            normal.Normalize();

            // where every vertex is right now (world space), per source renderer
            var srcRenderers = c.model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var worldVerts = new Vector3[srcRenderers.Length][];
            for (int i = 0; i < srcRenderers.Length; i++)
            {
                var smr = srcRenderers[i];
                if (smr.sharedMesh == null) continue;
                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                // BakeMesh only applies the renderer's own scale, not scale on its parents (the model is scaled 0.5),
                // so measure the baked mesh against the renderer's real world bounds and correct the difference
                var m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                float fix = ScaleCorrection(baked, m, smr);
                if (Mathf.Abs(fix - 1f) > 0.02f)
                    m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one * fix);
                var v = baked.vertices;
                for (int k = 0; k < v.Length; k++) v[k] = m.MultiplyPoint3x4(v[k]);
                worldVerts[i] = v;
                Object.Destroy(baked);
            }

            var root = new GameObject(c.name + "_Halves");
            var upper = BuildHalf(c, "Upper", true, srcRenderers, worldVerts, point, normal, impulse, velocity, root.transform);
            var lower = BuildHalf(c, "Lower", false, srcRenderers, worldVerts, point, normal, impulse, velocity, root.transform);
            if (upper == null && lower == null)
            {
                Object.Destroy(root);
                return null;
            }

            var fx = FxManager.I;
            if (fx != null)
                for (int i = 0; i < 4; i++) fx.Blood(point + Random.insideUnitSphere * 0.08f, (normal * (i % 2 == 0 ? 1 : -1) + Random.insideUnitSphere * 0.5f).normalized);

            Object.Destroy(root, Lifetime);
            return root;
        }

        static GameObject BuildHalf(Combatant c, string name, bool upperSide, SkinnedMeshRenderer[] srcRenderers, Vector3[][] worldVerts,
            Vector3 point, Vector3 normal, Vector3 impulse, Vector3 velocity, Transform parent)
        {
            var go = RagdollFactory.Spawn(c, velocity, -1, point, impulse * 0.5f);
            go.name = name;
            go.transform.SetParent(parent, true);

            // the drawn face only stays on the half that kept the head
            foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                if (mr.name == FaceDecal.ObjectName && Vector3.Dot(mr.bounds.center - point, normal) * (upperSide ? 1f : -1f) < 0f)
                    mr.enabled = false;

            // ---- mesh: keep this side, cap the cut ----
            var renderers = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            Vector3 capNormal = upperSide ? -normal : normal; // the cap faces out of the half, into the cut
            bool anyGeometry = false;
            for (int i = 0; i < renderers.Length && i < srcRenderers.Length; i++)
            {
                var r = renderers[i];
                var src = r.sharedMesh;
                if (src == null || worldVerts[i] == null || !src.isReadable) continue;
                var mesh = CutSkinned(src, worldVerts[i], point, normal, upperSide, capNormal);
                if (mesh == null) { r.enabled = false; continue; }
                anyGeometry = true;

                // flesh as an extra material; the player tint only goes on the body materials
                var mats = new List<Material>(r.sharedMaterials);
                while (mats.Count < src.subMeshCount) mats.Add(mats.Count > 0 ? mats[mats.Count - 1] : Flesh);
                if (mats.Count > src.subMeshCount) mats.RemoveRange(src.subMeshCount, mats.Count - src.subMeshCount);
                mats.Add(Flesh);
                MaterialPropertyBlock tint = null;
                if (r.HasPropertyBlock())
                {
                    tint = new MaterialPropertyBlock();
                    r.GetPropertyBlock(tint);
                    r.SetPropertyBlock(null);
                }
                r.sharedMesh = mesh;
                r.sharedMaterials = mats.ToArray();
                if (tint != null)
                    for (int s = 0; s < src.subMeshCount; s++) r.SetPropertyBlock(tint, s);
            }
            if (!anyGeometry)
            {
                Object.Destroy(go);
                return null;
            }

            // ---- physics: bodies on the other side become weightless ghosts (still jointed, so the skin doesn't stretch) ----
            var bodies = go.GetComponentsInChildren<Rigidbody>(true);
            var keep = new bool[bodies.Length];
            int kept = 0, best = -1;
            float bestSide = float.NegativeInfinity;
            for (int i = 0; i < bodies.Length; i++)
            {
                var cap = bodies[i].GetComponent<CapsuleCollider>();
                Vector3 centre = cap != null ? bodies[i].transform.TransformPoint(cap.center) : bodies[i].position;
                float d = Vector3.Dot(centre - point, normal) * (upperSide ? 1f : -1f);
                keep[i] = d >= 0f;
                if (keep[i]) kept++;
                if (d > bestSide) { bestSide = d; best = i; }
            }
            if (kept == 0 && best >= 0) keep[best] = true; // a sliver: let the nearest bone carry it

            foreach (var (rb, i) in Indexed(bodies))
            {
                if (keep[i])
                {
                    // fly apart along the cut
                    rb.linearVelocity += upperSide ? normal * 2.2f + Vector3.up * 1.2f : -normal * 0.6f;
                    continue;
                }
                rb.mass = 0.2f;
                rb.linearDamping = 1f;
                rb.angularDamping = 2f;
                foreach (var col in rb.GetComponents<Collider>()) col.enabled = false;
            }
            return go;
        }

        static IEnumerable<(T, int)> Indexed<T>(T[] a)
        {
            for (int i = 0; i < a.Length; i++) yield return (a[i], i);
        }

        /// <summary>world size the renderer really has / world size the baked mesh would get with matrix m.</summary>
        static float ScaleCorrection(Mesh baked, Matrix4x4 m, SkinnedMeshRenderer smr)
        {
            var verts = baked.vertices;
            if (verts.Length == 0) return 1f;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (var v in verts)
            {
                var w = m.MultiplyPoint3x4(v);
                min = Vector3.Min(min, w);
                max = Vector3.Max(max, w);
            }
            float baked3 = (max - min).magnitude;
            float real = smr.bounds.size.magnitude; // skinned bounds (updateWhenOffscreen is on for players)
            if (baked3 < 1e-4f || real < 1e-4f) return 1f;
            float ratio = real / baked3;
            // only trust clear, uniform scale mismatches (e.g. 0.5); tiny differences are just bounds padding
            return ratio > 0.1f && ratio < 10f ? ratio : 1f;
        }

        static Material Flesh
        {
            get
            {
                if (_flesh != null) return _flesh;
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                _flesh = new Material(shader) { name = "SliceFlesh", color = FleshColor };
                if (_flesh.HasProperty("_BaseColor")) _flesh.SetColor("_BaseColor", FleshColor);
                if (_flesh.HasProperty("_Smoothness")) _flesh.SetFloat("_Smoothness", 0.6f);
                return _flesh;
            }
        }

        // ------------------------------------------------------------------ cutting

        /// <summary>
        /// A copy of the skinned mesh with only the triangles on one side of the plane (decided by where the
        /// vertices are in the world right now). Split triangles get new vertices interpolated in bind pose,
        /// skinned to the bone of the vertex they came from. Returns null if nothing is on this side.
        /// </summary>
        static Mesh CutSkinned(Mesh src, Vector3[] wp, Vector3 point, Vector3 normal, bool upperSide, Vector3 capNormal)
        {
            var verts = src.vertices;
            var norms = src.normals;
            var uvs = src.uv;
            var weights = src.boneWeights;
            int n = verts.Length;
            if (wp.Length != n) return null;
            bool hasN = norms != null && norms.Length == n;
            bool hasUv = uvs != null && uvs.Length == n;
            bool hasW = weights != null && weights.Length == n;

            var side = new float[n];
            for (int i = 0; i < n; i++) side[i] = Vector3.Dot(wp[i] - point, normal) * (upperSide ? 1f : -1f);
            Vector3 N(int i) => hasN ? norms[i] : Vector3.up;
            Vector2 Uv(int i) => hasUv ? uvs[i] : Vector2.zero;
            BoneWeight W(int i) => hasW ? weights[i] : default;

            var h = new Side();
            var map = new Dictionary<int, int>();
            int V(int i)
            {
                if (!map.TryGetValue(i, out var j)) { j = h.Add(verts[i], N(i), Uv(i), W(i)); map[i] = j; }
                return j;
            }
            CutPoint Mid(int keepV, int dropV)
            {
                float t = side[keepV] / (side[keepV] - side[dropV]);
                return new CutPoint
                {
                    bind = Vector3.Lerp(verts[keepV], verts[dropV], t),
                    world = Vector3.Lerp(wp[keepV], wp[dropV], t),
                    weight = W(keepV),
                };
            }
            int AddMid(CutPoint p, int keepV, int dropV)
            {
                float t = side[keepV] / (side[keepV] - side[dropV]);
                return h.Add(p.bind, Vector3.Lerp(N(keepV), N(dropV), t).normalized, Vector2.Lerp(Uv(keepV), Uv(dropV), t), p.weight);
            }

            for (int s = 0; s < src.subMeshCount; s++)
            {
                var list = Sub(h, s);
                var tris = src.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    bool ka = side[a] >= 0, kb = side[b] >= 0, kc = side[c] >= 0;
                    int count = (ka ? 1 : 0) + (kb ? 1 : 0) + (kc ? 1 : 0);
                    if (count == 3) { list.Add(V(a)); list.Add(V(b)); list.Add(V(c)); continue; }
                    if (count == 0) continue;

                    // rotate so 'a' is the vertex alone on its side (keeps winding)
                    if (ka == kb) { (a, b, c) = (c, a, b); (ka, kb, kc) = (kc, ka, kb); }
                    else if (ka == kc) { (a, b, c) = (b, c, a); (ka, kb, kc) = (kb, kc, ka); }

                    if (ka)
                    {
                        // only a is kept: triangle a, ab, ac
                        var pab = Mid(a, b); var pac = Mid(a, c);
                        list.Add(V(a)); list.Add(AddMid(pab, a, b)); list.Add(AddMid(pac, a, c));
                        h.cutEdges.Add((pab, pac));
                    }
                    else
                    {
                        // b and c kept: quad ab, b, c, ac
                        var pab = Mid(b, a); var pac = Mid(c, a);
                        int iab = AddMid(pab, b, a), iac = AddMid(pac, c, a);
                        list.Add(iab); list.Add(V(b)); list.Add(V(c));
                        list.Add(iab); list.Add(V(c)); list.Add(iac);
                        h.cutEdges.Add((pab, pac));
                    }
                }
            }
            if (h.pos.Count < 3) return null;

            int fleshSub = src.subMeshCount;
            Cap(h, fleshSub, capNormal);

            var mesh = new Mesh { name = src.name + (upperSide ? "_Upper" : "_Lower") };
            if (h.pos.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(h.pos);
            mesh.SetNormals(h.nrm);
            mesh.SetUVs(0, h.uv);
            mesh.boneWeights = h.bw.ToArray();
            mesh.bindposes = src.bindposes;
            while (h.subs.Count <= fleshSub) h.subs.Add(new List<int>());
            mesh.subMeshCount = fleshSub + 1;
            for (int s = 0; s <= fleshSub; s++) mesh.SetTriangles(h.subs[s], s);
            mesh.RecalculateBounds();
            return mesh;
        }

        static List<int> Sub(Side h, int index)
        {
            while (h.subs.Count <= index) h.subs.Add(new List<int>());
            return h.subs[index];
        }

        // ------------------------------------------------------------------ caps

        /// <summary>Joins the cut edges into loops and fills each loop with a fan facing capNormal (decided in world space).</summary>
        static void Cap(Side h, int fleshSub, Vector3 capNormal)
        {
            if (h.cutEdges.Count == 0) return;
            const float q = 2000f; // weld cut points closer than 0.5 mm (UV seams duplicate vertices)
            (int, int, int) Key(Vector3 p) => (Mathf.RoundToInt(p.x * q), Mathf.RoundToInt(p.y * q), Mathf.RoundToInt(p.z * q));

            var points = new Dictionary<(int, int, int), CutPoint>();
            var adj = new Dictionary<(int, int, int), List<(int, int, int)>>();
            foreach (var (a, b) in h.cutEdges)
            {
                var ka = Key(a.world); var kb = Key(b.world);
                if (ka.Equals(kb)) continue;
                points[ka] = a; points[kb] = b;
                if (!adj.TryGetValue(ka, out var la)) adj[ka] = la = new List<(int, int, int)>();
                if (!adj.TryGetValue(kb, out var lb)) adj[kb] = lb = new List<(int, int, int)>();
                la.Add(kb); lb.Add(ka);
            }

            var list = Sub(h, fleshSub);
            var visited = new HashSet<(int, int, int)>();
            foreach (var start in adj.Keys)
            {
                if (visited.Contains(start)) continue;
                // walk one loop (or open chain) until it runs out of unvisited neighbours
                var loop = new List<CutPoint>();
                var cur = start;
                while (true)
                {
                    visited.Add(cur);
                    loop.Add(points[cur]);
                    bool found = false;
                    foreach (var next in adj[cur])
                        if (!visited.Contains(next)) { cur = next; found = true; break; }
                    if (!found) break;
                }
                if (loop.Count < 3) continue;

                // orient the loop so the fan faces capNormal in the world
                Vector3 cw = Vector3.zero, cb = Vector3.zero;
                foreach (var p in loop) { cw += p.world; cb += p.bind; }
                cw /= loop.Count; cb /= loop.Count;
                Vector3 nw = Vector3.zero;
                for (int i = 0; i < loop.Count; i++)
                    nw += Vector3.Cross(loop[i].world - cw, loop[(i + 1) % loop.Count].world - cw);
                if (Vector3.Dot(nw, capNormal) < 0) loop.Reverse();

                // the same face in bind pose gives the cap's normal
                Vector3 nb = Vector3.zero;
                for (int i = 0; i < loop.Count; i++)
                    nb += Vector3.Cross(loop[i].bind - cb, loop[(i + 1) % loop.Count].bind - cb);
                nb = nb.sqrMagnitude > 1e-12f ? nb.normalized : Vector3.up;

                // the flesh is flat colour, so UVs don't matter
                int ci = h.Add(cb, nb, Vector2.zero, loop[0].weight);
                int first = h.pos.Count;
                foreach (var p in loop) h.Add(p.bind, nb, Vector2.zero, p.weight);
                for (int i = 0; i < loop.Count; i++)
                {
                    list.Add(ci); list.Add(first + i); list.Add(first + (i + 1) % loop.Count);
                }
            }
        }
    }
}
