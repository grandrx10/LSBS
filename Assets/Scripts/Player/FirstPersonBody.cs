using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gunbound
{
    /// <summary>
    /// Local player only. The full body keeps rendering — but only into the shadow map, so you
    /// still cast a complete shadow (head included) without seeing your own torso. A second
    /// renderer shows just the arms, built at runtime from the triangles skinned to the arm bones.
    /// </summary>
    public class FirstPersonBody : MonoBehaviour
    {
        static readonly HashSet<string> ArmBones = new()
        {
            "Arm L.001", "Arm L.002", "Arm L.003", "Arm L.003_end",
            "Arm R.001", "Arm R.002", "Arm R.003", "Arm R.003_end",
        };

        /// <summary>How much of a vertex must belong to the arm bones for it to count as "arm".</summary>
        const float ArmThreshold = 0.5f;

        static readonly Dictionary<Mesh, Mesh> ArmMeshCache = new();

        public SkinnedMeshRenderer Arms { get; private set; }
        readonly List<SkinnedMeshRenderer> _bodies = new();

        public bool Build(Transform model)
        {
            foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                var armMesh = GetArmMesh(mesh, smr.bones);
                if (armMesh == null) continue;

                var go = new GameObject("FP_Arms");
                go.transform.SetParent(transform, false);
                var arms = go.AddComponent<SkinnedMeshRenderer>();
                arms.sharedMesh = armMesh;
                arms.bones = smr.bones;
                arms.rootBone = smr.rootBone;
                arms.sharedMaterials = smr.sharedMaterials;
                arms.updateWhenOffscreen = true;
                arms.shadowCastingMode = ShadowCastingMode.Off; // the full body already casts the shadow
                arms.receiveShadows = true;
                Arms = arms;

                smr.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                _bodies.Add(smr);
                return true;
            }
            return false;
        }

        public void SetVisible(bool on)
        {
            if (Arms != null) Arms.enabled = on;
        }

        static Mesh GetArmMesh(Mesh src, Transform[] bones)
        {
            if (ArmMeshCache.TryGetValue(src, out var cached) && cached != null) return cached;

            var armIndex = new bool[bones.Length];
            bool any = false;
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] != null && ArmBones.Contains(bones[i].name)) { armIndex[i] = true; any = true; }
            if (!any) return null;

            var weights = src.boneWeights;
            if (weights == null || weights.Length != src.vertexCount) return null;
            var isArm = new bool[weights.Length];
            for (int v = 0; v < weights.Length; v++)
            {
                var w = weights[v];
                float a = 0f;
                if (w.boneIndex0 < armIndex.Length && armIndex[w.boneIndex0]) a += w.weight0;
                if (w.boneIndex1 < armIndex.Length && armIndex[w.boneIndex1]) a += w.weight1;
                if (w.boneIndex2 < armIndex.Length && armIndex[w.boneIndex2]) a += w.weight2;
                if (w.boneIndex3 < armIndex.Length && armIndex[w.boneIndex3]) a += w.weight3;
                isArm[v] = a >= ArmThreshold;
            }

            var mesh = Object.Instantiate(src);
            mesh.name = src.name + "_Arms";
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var tris = src.GetTriangles(s);
                var kept = new List<int>(tris.Length / 4);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                    if (isArm[tris[t]] && isArm[tris[t + 1]] && isArm[tris[t + 2]])
                    {
                        kept.Add(tris[t]); kept.Add(tris[t + 1]); kept.Add(tris[t + 2]);
                    }
                mesh.SetTriangles(kept, s);
            }
            ArmMeshCache[src] = mesh;
            return mesh;
        }
    }
}
