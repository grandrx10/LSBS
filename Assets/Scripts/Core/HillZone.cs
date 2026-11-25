using System.Collections.Generic;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// King of the Hill point. Add this ("Hill Zone") to the hill object inside a map prefab — the cylinder whose
    /// child has the "Is Trigger" collider. Anyone touching a trigger collider under it is on the point.
    /// The hill is only shown in King of the Hill rounds, and is highlighted through walls.
    /// (Adding it to the trigger child itself also works: the parent is then treated as the hill.)
    /// </summary>
    [AddComponentMenu("Gunbound/Hill Zone")]
    [DisallowMultipleComponent]
    public class HillZone : MonoBehaviour
    {
        static readonly Collider[] Buf = new Collider[64];
        static Material _xrayShared, _xrayMask;
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int CenterId = Shader.PropertyToID("_CenterOS");

        Collider[] _triggers;
        readonly List<(MeshRenderer renderer, Vector3 center)> _xray = new();
        readonly List<(MeshRenderer renderer, Color baseColor)> _solid = new();
        MaterialPropertyBlock _mpb;
        Vector4 _shownKey = new(-2, -2, -2, -2);
        bool _built;

        /// <summary>The object switched on/off with the game mode (the cylinder).</summary>
        public GameObject Root
        {
            get
            {
                // on the trigger child (no visuals of its own): the cylinder above it is the hill
                if (transform.parent != null && GetComponent<Renderer>() == null &&
                    TryGetComponent<Collider>(out var c) && c.isTrigger && transform.parent.GetComponent<GameMap>() == null)
                    return transform.parent.gameObject;
                return gameObject;
            }
        }

        /// <summary>Called once when the map is loaded.</summary>
        public void Build()
        {
            if (_built) return;
            _built = true;
            var root = Root.transform;
            var list = new List<Collider>();
            foreach (var c in root.GetComponentsInChildren<Collider>(true)) if (c.isTrigger) list.Add(c);
            if (list.Count == 0) Debug.LogWarning($"[Gunbound] Hill Zone '{name}' has no trigger collider; nobody can stand on it.");
            _triggers = list.ToArray();

            // the hill's own meshes get tinted with the holder's colour
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled || mf.sharedMesh == null) continue;
                var m = mr.sharedMaterial;
                Color baseColor = m == null ? Color.white : m.HasProperty(BaseColorId) ? m.GetColor(BaseColorId) : m.HasProperty(ColorId) ? m.GetColor(ColorId) : Color.white;
                _solid.Add((mr, baseColor));
            }

            // see-through edge outline of every visible mesh of the hill: an invisible stencil mask of its shape,
            // then a slightly fatter copy drawn only outside that mask and only where it's hidden behind something
            if (_xrayShared == null)
            {
                var shader = Shader.Find("Gunbound/XRay");
                if (shader != null) _xrayShared = new Material(shader) { name = "HillXRay" };
            }
            if (_xrayMask == null)
            {
                var shader = Shader.Find("Gunbound/XRayMask");
                if (shader != null) _xrayMask = new Material(shader) { name = "HillXRayMask" };
            }
            if (_xrayShared == null || _xrayMask == null) { SetTint(null, null, 0f); return; }
            foreach (var (mr, _) in _solid)
            {
                var mf = mr.GetComponent<MeshFilter>();
                Child(mf, "XRayMask", _xrayMask);
                var outline = Child(mf, "XRayOutline", _xrayShared);
                _xray.Add((outline, mf.sharedMesh.bounds.center));
            }
            SetTint(null, null, 0f);
        }

        static MeshRenderer Child(MeshFilter src, string name, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(src.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = src.sharedMesh;
            var r = go.AddComponent<MeshRenderer>();
            var mats = new Material[Mathf.Max(1, src.sharedMesh.subMeshCount)];
            for (int i = 0; i < mats.Length; i++) mats[i] = mat;
            r.sharedMaterials = mats;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        /// <summary>
        /// Colours the hill: blends from <paramref name="from"/> to <paramref name="to"/> by <paramref name="k"/>
        /// (null = no holder: the hill's own colour, white highlight). Used for the capture fill and the holder's colour.
        /// </summary>
        public void SetTint(Color? from, Color? to, float k)
        {
            k = Mathf.Clamp01(k);
            var key = new Vector4(from.HasValue ? from.Value.r + from.Value.g * 2 + from.Value.b * 4 : -1,
                                  to.HasValue ? to.Value.r + to.Value.g * 2 + to.Value.b * 4 : -1, k, 0);
            if (key == _shownKey) return;
            _shownKey = key;
            _mpb ??= new MaterialPropertyBlock();

            foreach (var (r, baseColor) in _solid)
            {
                if (!r) continue;
                Color c = Color.Lerp(from ?? baseColor, to ?? baseColor, k);
                c.a = baseColor.a;
                _mpb.Clear();
                _mpb.SetColor(BaseColorId, c);
                _mpb.SetColor(ColorId, c);
                r.SetPropertyBlock(_mpb);
            }

            Color x = Color.Lerp(from ?? Color.white, to ?? Color.white, k);
            x.a = 0.9f;
            foreach (var (r, center) in _xray)
            {
                if (!r) continue;
                _mpb.Clear();
                _mpb.SetColor(ColorId, x);
                _mpb.SetVector(CenterId, center);
                r.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>Server: is this player touching the point?</summary>
        public bool Contains(PlayerController p)
        {
            if (_triggers == null || p == null || p.CC == null) return false;
            var cc = p.CC;
            var t = p.transform;
            float r = cc.radius * Mathf.Max(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.z));
            float half = Mathf.Max(r, cc.height * Mathf.Abs(t.lossyScale.y) * 0.5f);
            Vector3 centre = t.TransformPoint(cc.center);
            Vector3 a = centre - Vector3.up * (half - r), b = centre + Vector3.up * (half - r);
            int n = Physics.OverlapCapsuleNonAlloc(a, b, r, Buf, Physics.AllLayers, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
                foreach (var trig in _triggers)
                    if (Buf[i] == trig && trig.enabled && trig.gameObject.activeInHierarchy) return true;
            return false;
        }
    }
}
