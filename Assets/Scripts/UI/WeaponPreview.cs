using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Spinning weapon renders for the loadout cards. Each weapon gets a tiny "photo stage" far below the map:
    /// the model on a spinning pivot, and a camera that only sees the preview layer, rendering into a RenderTexture.
    /// Models come from the player prefab (its "Gun" child for Wolfgang, WeaponController.models for the rest),
    /// so they look exactly like in game. Cameras only run while the edit page is open (SetRendering).
    /// </summary>
    public static class WeaponPreview
    {
        const int Layer = 31;                 // unnamed layer, used only by these stages
        const int TexWidth = 512, TexHeight = 256;
        const float Fov = 20f;
        const float SpinSpeed = 70f;          // degrees per second
        const float DualSpacing = 1.1f;       // gap between the two Beethovens, in model heights

        class Stage
        {
            public Camera cam;
            public Transform pivot;
            public RenderTexture tex;
        }

        static readonly Dictionary<WeaponId, Stage> Stages = new();
        static Transform _root;
        static bool _rendering;

        /// <summary>The preview texture for a weapon (null if its model can't be found).</summary>
        public static Texture Get(WeaponId id)
        {
            if (!WeaponCatalog.IsValid((int)id)) return null; // empty slot
            if (Stages.TryGetValue(id, out var s))
            {
                if (s == null) return null;              // no model for this weapon (already tried)
                if (s.cam != null) return s.tex;
            }
            s = Build(WeaponCatalog.Get(id), Stages.Count);
            Stages[id] = s;
            return s?.tex;
        }

        public static void SetRendering(bool on)
        {
            _rendering = on;
            foreach (var s in Stages.Values)
                if (s != null && s.cam != null) s.cam.enabled = on;
        }

        static Stage Build(WeaponInfo w, int index)
        {
            var model = Source(w);
            if (model == null) return null;

            if (_root == null)
            {
                _root = new GameObject("WeaponPreviews").transform;
                _root.gameObject.AddComponent<Spinner>();
                var main = Camera.main;
                if (main != null) main.cullingMask &= ~(1 << Layer);
            }

            var stage = new GameObject("Preview_" + w.id).transform;
            stage.SetParent(_root, false);
            stage.position = new Vector3(index * 50f, -5000f, 0f);

            var pivot = new GameObject("Pivot").transform;
            pivot.SetParent(stage, false);

            // the gun (two side by side for dual weapons), aiming along +Z like the in-game holders
            int copies = w.Dual ? 2 : 1;
            var guns = new List<Transform>();
            for (int i = 0; i < copies; i++) guns.Add(Instantiate(model, w, pivot));

            Bounds b = Measure(guns);
            if (copies == 2)
            {
                float gap = b.size.y * DualSpacing;
                guns[0].localPosition += new Vector3(-gap * 0.5f, 0, 0);
                guns[1].localPosition += new Vector3(gap * 0.5f, 0, 0);
                b = Measure(guns);
            }
            // centre the model on the pivot so it spins in place
            Vector3 offset = pivot.position - b.center;
            foreach (var g in guns) g.position += offset;
            b.center += offset;

            // camera from the side, far enough that the spinning model never leaves the frame
            var camGo = new GameObject("Camera");
            camGo.transform.SetParent(stage, false);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.cullingMask = 1 << Layer;
            cam.fieldOfView = Fov;
            cam.aspect = TexWidth / (float)TexHeight;
            cam.allowHDR = false;
            cam.allowMSAA = true;
            float r = new Vector2(b.extents.x, b.extents.z).magnitude; // radius swept while spinning
            float halfH = b.extents.y;
            float tanV = Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad);
            float tanH = tanV * cam.aspect;
            // nearest point of the swept circle is at (dist - r): make sure that still fits
            float dist = Mathf.Max(r / tanH, halfH / tanV) + r;
            dist *= 1.08f;
            camGo.transform.position = pivot.position + new Vector3(-dist, b.extents.y * 0.15f, 0f);
            camGo.transform.LookAt(pivot.position);
            cam.nearClipPlane = Mathf.Max(0.01f, (dist - r) * 0.5f);
            cam.farClipPlane = dist + r * 2f + 1f;

            var tex = new RenderTexture(TexWidth, TexHeight, 24, RenderTextureFormat.ARGB32) { name = "Preview_" + w.id, antiAliasing = 4 };
            tex.Create();
            cam.targetTexture = tex;
            cam.enabled = _rendering;

            return new Stage { cam = cam, pivot = pivot, tex = tex };
        }

        /// <summary>Copy of the weapon's model, rotated/scaled like the in-game holder, stripped down to renderers.</summary>
        static Transform Instantiate(GameObject source, WeaponInfo w, Transform parent)
        {
            var holder = new GameObject("Gun").transform;
            holder.SetParent(parent, false);
            var inst = Object.Instantiate(source, holder);
            inst.SetActive(true);
            if (!string.IsNullOrEmpty(w.modelPath))
            {
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.Euler(w.modelEuler);
                inst.transform.localScale = Vector3.one * w.modelScale;
            }
            else
            {
                // the player's built-in "Gun" child already holds the model oriented along +Z
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.identity;
            }
            foreach (var c in inst.GetComponentsInChildren<Collider>(true)) Object.Destroy(c);
            foreach (var m in inst.GetComponentsInChildren<MonoBehaviour>(true)) m.enabled = false;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            Layers.SetLayerRecursive(holder.gameObject, Layer);
            return holder;
        }

        static Bounds Measure(List<Transform> guns)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var g in guns)
                foreach (var r in g.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                    if (!any) { b = r.bounds; any = true; }
                    else b.Encapsulate(r.bounds);
                }
            if (!any) b = new Bounds(guns[0].position, Vector3.one * 0.3f);
            return b;
        }

        /// <summary>The model to copy for a weapon, taken from the networked player prefab.</summary>
        static GameObject Source(WeaponInfo w)
        {
            var nm = NetworkManager.Singleton;
            var player = nm != null && nm.NetworkConfig != null ? nm.NetworkConfig.PlayerPrefab : null;
            var wc = player != null ? player.GetComponent<WeaponController>() : null;

            if (string.IsNullOrEmpty(w.modelPath))
            {
                if (wc != null && wc.gunRoot != null) return wc.gunRoot.gameObject;
                var gun = player != null ? player.transform.Find("Gun") : null;
                return gun != null ? gun.gameObject : null;
            }
            if (wc != null)
                foreach (var m in wc.models)
                    if (m != null && m.id == w.id && m.prefab != null) return m.prefab;
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(w.modelPath);
#else
            return null;
#endif
        }

        class Spinner : MonoBehaviour
        {
            void Update()
            {
                if (!_rendering) return;
                float a = SpinSpeed * Time.unscaledDeltaTime;
                foreach (var s in Stages.Values)
                    if (s != null && s.pivot != null) s.pivot.Rotate(0f, a, 0f, Space.World);
            }
        }
    }
}
