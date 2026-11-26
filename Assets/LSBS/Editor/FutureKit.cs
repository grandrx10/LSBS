using UnityEditor;
using UnityEngine;

namespace LSBS.EditorTools
{
    /// <summary>
    /// "LSBS > Futurise Grey Kit": keeps the Grey kit's straight lines and pack, and adds armour over them: a visor across
    /// the helmet, a chest plate with a light at its middle, a power cell on the pack, plates on the shoulders and knees.
    /// Each is placed and sized from what's already in its group, and put in a group of its own ("Future ..." under the
    /// group), so it can be moved, or deleted to undo. Anything glowing takes its side's colour in the game.
    /// Then "LSBS > Rebuild Soldiers" puts it on everyone.
    /// </summary>
    public static class FutureKit
    {
        const string KitPath = "Assets/LSBS/Kit/Grey Kit.prefab", Folder = "Assets/LSBS/Kit/Future";
        const string Mark = "Future";

        [MenuItem("LSBS/Futurise Grey Kit")]
        static void Futurise()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(KitPath) == null) { Debug.LogError("[LSBS] No Grey Kit yet: run LSBS > Rebuild Soldiers once first."); return; }
            System.IO.Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();
            var plate = Material("Future Plate", new Color(0.16f, 0.18f, 0.21f), 0.65f, false);
            var dark = Material("Future Dark", new Color(0.06f, 0.07f, 0.08f), 0.55f, false);
            var glow = Material("Future Glow", new Color(0.5f, 0.9f, 1f), 0f, true);

            var root = PrefabUtility.LoadPrefabContents(KitPath);
            try
            {
                Bounds? Group(string name)
                {
                    var g = root.transform.Find(name);
                    if (g == null) return null;
                    Bounds? b = null;
                    foreach (var r in g.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (r.transform.parent != null && r.transform.parent.name.StartsWith(Mark)) continue;
                        if (b == null) b = r.bounds; else { var x = b.Value; x.Encapsulate(r.bounds); b = x; }
                    }
                    return b;
                }
                var chest = Group("Chest");
                var back = Group("Back");
                if (chest == null) { Debug.LogError("[LSBS] The Grey Kit has nothing in its Chest group to fit armour to."); return; }
                // which way the soldier faces: from the pack to the chest
                Vector3 fwd = back != null ? chest.Value.center - back.Value.center : Vector3.forward;
                fwd.y = 0f;
                fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
                var face = Quaternion.LookRotation(fwd, Vector3.up);
                Vector3 right = face * Vector3.right;
                float Depth(Bounds b) => Mathf.Abs(Vector3.Dot(b.extents, new Vector3(Mathf.Abs(fwd.x), Mathf.Abs(fwd.y), Mathf.Abs(fwd.z))));
                float Width(Bounds b) => Mathf.Abs(Vector3.Dot(b.extents, new Vector3(Mathf.Abs(right.x), Mathf.Abs(right.y), Mathf.Abs(right.z)))) * 2f;

                void Piece(string group, string name, Vector3 at, Vector3 size, Quaternion rot, Material mat)
                {
                    var g = root.transform.Find(group);
                    if (g == null) return;
                    var holder = g.Find(Mark + " " + group);
                    if (holder == null) { holder = new GameObject(Mark + " " + group).transform; holder.SetParent(g, false); }
                    var old = holder.Find(name);
                    if (old != null) Object.DestroyImmediate(old.gameObject);
                    var o = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Object.DestroyImmediate(o.GetComponent<Collider>());
                    o.name = name;
                    o.transform.SetParent(holder, true);
                    o.transform.SetPositionAndRotation(at, rot);
                    o.transform.localScale = Vector3.one;
                    // sized in world terms, whatever the group's scale
                    var lossy = holder.lossyScale;
                    o.transform.localScale = new Vector3(size.x / Mathf.Max(1e-4f, lossy.x), size.y / Mathf.Max(1e-4f, lossy.y), size.z / Mathf.Max(1e-4f, lossy.z));
                    o.GetComponent<MeshRenderer>().sharedMaterial = mat;
                }

                // ---- the chest: a plate across the front, a light at its middle, a strip down each side
                var c = chest.Value;
                Vector3 front = c.center + fwd * (Depth(c) + 0.012f);
                float cw = Width(c);
                Piece("Chest", "Plate", front + Vector3.up * c.extents.y * 0.15f, new Vector3(cw * 0.72f, c.size.y * 0.42f, 0.035f), face, plate);
                Piece("Chest", "Core", front + Vector3.up * c.extents.y * 0.2f + fwd * 0.022f, new Vector3(0.055f, 0.055f, 0.012f), face, glow);
                for (int s = -1; s <= 1; s += 2)
                    Piece("Chest", s < 0 ? "Seam L" : "Seam R", front + right * s * cw * 0.37f, new Vector3(0.012f, c.size.y * 0.5f, 0.02f), face, glow);
                Piece("Chest", "Collar", c.center + Vector3.up * (c.extents.y * 0.92f), new Vector3(cw * 0.8f, 0.05f, Depth(c) * 2.05f), face, dark);

                // ---- the helmet: a visor across the eyes, a ridge over the top
                var helmet = Group("Helmet");
                if (helmet != null)
                {
                    var h = helmet.Value;
                    Vector3 eyes = h.center + fwd * (Depth(h) + 0.006f) - Vector3.up * h.extents.y * 0.35f;
                    Piece("Helmet", "Visor", eyes, new Vector3(Width(h) * 0.8f, h.size.y * 0.16f, 0.02f), face, glow);
                    Piece("Helmet", "Visor frame", eyes - fwd * 0.004f, new Vector3(Width(h) * 0.9f, h.size.y * 0.28f, 0.018f), face, dark);
                    Piece("Helmet", "Ridge", h.center + Vector3.up * (h.extents.y + 0.008f), new Vector3(0.04f, 0.02f, Depth(h) * 1.6f), face, plate);
                }

                // ---- the pack: a power cell on its back, lit down the middle
                if (back != null)
                {
                    var b = back.Value;
                    Vector3 rear = b.center - fwd * (Depth(b) + 0.03f);
                    Piece("Back", "Cell", rear, new Vector3(Width(b) * 0.55f, b.size.y * 0.55f, 0.06f), face, dark);
                    Piece("Back", "Cell light", rear - fwd * 0.032f, new Vector3(0.025f, b.size.y * 0.42f, 0.01f), face, glow);
                }

                // ---- plates on the shoulders and the knees
                foreach (var arm in new[] { "Upper Arm L", "Upper Arm R" })
                {
                    var a = Group(arm);
                    if (a == null) continue;
                    var u = a.Value;
                    Piece(arm, "Shoulder", new Vector3(u.center.x, u.max.y - 0.01f, u.center.z), new Vector3(u.size.x * 1.25f, 0.045f, u.size.z * 1.25f), face, plate);
                    Piece(arm, "Shoulder light", new Vector3(u.center.x, u.max.y + 0.014f, u.center.z), new Vector3(u.size.x * 0.4f, 0.01f, u.size.z * 0.9f), face, glow);
                }
                foreach (var leg in new[] { "Shin L", "Shin R" })
                {
                    var l = Group(leg);
                    if (l == null) continue;
                    var k = l.Value;
                    Piece(leg, "Knee", new Vector3(k.center.x, k.max.y - 0.04f, k.center.z) + fwd * (Depth(k) + 0.01f), new Vector3(Width(k) * 0.9f, 0.1f, 0.03f), face, plate);
                }
                PrefabUtility.SaveAsPrefabAsset(root, KitPath);
                Debug.Log("[LSBS] Grey Kit futurised. Now run LSBS > Rebuild Soldiers.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        static Material Material(string name, Color colour, float metallic, bool glows)
        {
            string path = $"{Folder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", colour);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", 0.45f);
            if (glows) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", colour * 2f); m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None; }
            else m.DisableKeyword("_EMISSION");
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
