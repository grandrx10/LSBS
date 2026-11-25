using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// A streak in the player's colour that appears behind them once they go fast enough,
    /// getting longer and brighter the faster they move. Visible to everyone.
    /// </summary>
    [DefaultExecutionOrder(160)] // after the animator has posed the hips
    public class SpeedTrail : MonoBehaviour
    {
        [Tooltip("Speed (m/s) at which the trail starts to appear.")]
        public float minSpeed = 14f;
        [Tooltip("Speed (m/s) at which the trail is at full strength.")]
        public float fullSpeed = 24f;
        public float width = 0.45f;
        [Tooltip("Trail length in seconds at full speed.")]
        public float maxTime = 0.45f;

        [Header("Own trail (local player only)")]
        [Tooltip("Your own trail is invisible within this distance of your camera...")]
        public float ownHideRadius = 2f;
        [Tooltip("...and fades in to full strength by this distance.")]
        public float ownFullRadius = 3.5f;

        PlayerController _pc;
        bool _ownMatApplied;
        static Material _ownMat;
        static readonly int FadeStartId = Shader.PropertyToID("_FadeStart");
        static readonly int FadeEndId = Shader.PropertyToID("_FadeEnd");
        TrailRenderer _trail;
        Transform _anchor;
        float _strength;
        Vector3 _lastPos;

        static Material _mat;

        void Awake()
        {
            _pc = GetComponent<PlayerController>();
            if (_pc != null && _pc.Bones != null) _pc.Bones.TryGetValue("Bone.003", out _anchor); // chest height

            var go = new GameObject("SpeedTrail");
            go.transform.SetParent(transform, false);
            _trail = go.AddComponent<TrailRenderer>();
            _trail.sharedMaterial = _mat ??= MakeMaterial();
            _trail.time = maxTime;
            _trail.minVertexDistance = 0.15f;
            _trail.numCapVertices = 4;
            _trail.alignment = LineAlignment.View;
            _trail.textureMode = LineTextureMode.Stretch;
            _trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _trail.receiveShadows = false;
            _trail.widthCurve = new AnimationCurve(new Keyframe(0, 1), new Keyframe(1, 0));
            _trail.emitting = false;
        }

        void LateUpdate()
        {
            if (_pc == null || !_pc.IsSpawned) return;

            // the local player sees their own trail fade out near the camera so it never blocks the view
            if (_pc.IsOwner && !_ownMatApplied)
            {
                _ownMatApplied = true;
                var shader = Shader.Find("Gunbound/NearFade");
                if (shader != null)
                {
                    _ownMat ??= new Material(shader) { mainTexture = _mat.mainTexture };
                    _trail.sharedMaterial = _ownMat;
                }
            }
            if (_ownMat != null && _pc.IsOwner)
            {
                _ownMat.SetFloat(FadeStartId, ownHideRadius);
                _ownMat.SetFloat(FadeEndId, Mathf.Max(ownHideRadius + 0.01f, ownFullRadius));
            }

            // snap (don't streak) across teleports / respawns
            Vector3 pos = _anchor != null ? _anchor.position : transform.position + Vector3.up * 0.4f;
            if ((pos - _lastPos).sqrMagnitude > 25f) _trail.Clear();
            _lastPos = pos;
            _trail.transform.position = pos;

            float speed = _pc.ViewVelocity.magnitude;
            float target = _pc.Visible && _pc.IsAlive ? Mathf.InverseLerp(minSpeed, fullSpeed, speed) : 0f;
            _strength = Mathf.MoveTowards(_strength, target, Time.deltaTime * 4f);

            _trail.emitting = _strength > 0.01f;
            if (!_pc.Visible) { _trail.Clear(); return; }

            Color c = _pc.ColorIndex.Value != 255 ? _pc.PlayerColor : Color.white;
            float a = Mathf.Lerp(0.25f, 0.85f, _strength);
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.Lerp(c, Color.white, 0.35f), 0f), new GradientColorKey(c, 1f) },
                new[] { new GradientAlphaKey(a, 0f), new GradientAlphaKey(a * 0.5f, 0.4f), new GradientAlphaKey(0f, 1f) });
            _trail.colorGradient = g;
            _trail.widthMultiplier = width * Mathf.Lerp(0.5f, 1f, _strength);
            _trail.time = maxTime * Mathf.Lerp(0.4f, 1f, _strength);
        }

        static Material MakeMaterial()
        {
            // soft across the width, solid along the length
            const int w = 8, h = 64;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                float v = Mathf.Abs((y + 0.5f) / h * 2f - 1f);
                float a = Mathf.Clamp01(1f - v);
                a = a * a * (3f - 2f * a);
                for (int x = 0; x < w; x++) px[y * w + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return new Material(Shader.Find("Sprites/Default")) { mainTexture = tex };
        }
    }
}
