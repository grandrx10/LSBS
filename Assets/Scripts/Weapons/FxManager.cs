using System.Collections.Generic;
using UnityEngine;

namespace Gunbound
{
    /// <summary>
    /// Pooled, code-built VFX: tracers, sparks, dust, blood, bullet holes and casings.
    /// Uses the always-included "Sprites/Default" shader so nothing is stripped from builds.
    /// </summary>
    public class FxManager : MonoBehaviour
    {
        public static FxManager I { get; private set; }

        public Color tracerColor = new(1f, 0.85f, 0.45f, 1f);
        public float tracerWidth = 0.025f;
        public float tracerLife = 0.12f;
        [Header("Ricochet tracer")]
        public Color ricochetColor = Color.white;
        public float ricochetWidth = 0.07f;
        [Tooltip("Colour of the border drawn along both edges of bullet trails (matches the screen outlines).")]
        public Color trailOutlineColor = new(0.32f, 0.33f, 0.36f, 1f);
        [Tooltip("Fraction of the trail's half-width used by the border.")]
        [Range(0.05f, 0.6f)] public float trailOutlineThickness = 0.12f;
        [Tooltip("Seconds the ricochet tracer stays fully visible.")]
        public float ricochetHold = 1f;
        [Tooltip("Seconds it then takes to fade out.")]
        public float ricochetFade = 0.5f;
        [Tooltip("Overall opacity of every bullet trail (normal and ricochet). Lower = less clutter.")]
        [Range(0f, 1f)] public float trailOpacity = 0.45f;
        [Header("Trail reveal")]
        [Tooltip("How fast the trail draws itself out (m/s).")]
        public float trailSpeed = 450f;
        [Tooltip("Draw time per segment is clamped to this range (seconds).")]
        public float minDrawTime = 0.02f, maxDrawTime = 0.09f;
        [Header("Impacts")]
        [Tooltip("Multiplies every spark burst.")]
        public float sparkAmount = 1f;
        public int maxBloodStains = 120;
        public int maxDecals = 80;
        public int maxCasings = 40;

        Material _additive, _alpha;
        Texture2D _softDot, _holeTex, _flashTex;
        ParticleSystem _sparks, _dust, _blood, _flash;
        Material[] _splatMats;
        readonly Queue<GameObject> _stains = new();

        class TracerLine { public LineRenderer lr; public Vector3 a, b; public float t, delay, draw, hold, fade, width; public bool solid; public Color c; }
        readonly List<(float time, System.Action action)> _delayed = new();

        /// <summary>Run an action after a delay (used to land impact effects when the trail arrives).</summary>
        public void After(float delay, System.Action action)
        {
            if (delay <= 0f) action();
            else _delayed.Add((Time.time + delay, action));
        }
        readonly List<TracerLine> _tracers = new();
        readonly Queue<GameObject> _decals = new();
        readonly Queue<GameObject> _casings = new();
        Material _casingMat;

        public static void Ensure()
        {
            if (I != null) return;
            var go = new GameObject("FxManager");
            DontDestroyOnLoad(go);
            go.AddComponent<FxManager>();
        }

        void Awake()
        {
            I = this;
            var shader = Shader.Find("Sprites/Default");
            _softDot = MakeRadial(64, 0f, 2.2f);
            _holeTex = MakeRadial(64, 0.35f, 6f);
            _flashTex = MakeFlash(128);
            _alpha = new Material(shader) { mainTexture = _softDot };
            _additive = new Material(shader) { mainTexture = MakeBeam(8, 64, trailOutlineColor, trailOutlineThickness) };

            _sparks = MakeSystem("Sparks", _softDot, stretched: true, gravity: 1.3f);
            _dust = MakeSystem("Dust", _softDot, stretched: false, gravity: -0.05f);
            _blood = MakeSystem("Blood", _softDot, stretched: false, gravity: 1.6f);
            _flash = MakeSystem("ImpactFlash", _softDot, stretched: false, gravity: 0f);
            EnableCollision(_sparks, bounce: 0.45f, dampen: 0.25f, sendMessages: false);
            EnableCollision(_blood, bounce: 0f, dampen: 1f, sendMessages: true);
            _blood.gameObject.AddComponent<BloodDropCollisions>().fx = this;

            _splatMats = new Material[4];
            for (int i = 0; i < _splatMats.Length; i++)
                _splatMats[i] = new Material(shader) { mainTexture = MakeSplat(128, 1234 + i * 77) };
        }

        public Material FlashMaterial(Color c)
        {
            var m = new Material(Shader.Find("Sprites/Default")) { mainTexture = _flashTex, color = c };
            return m;
        }

        // ------------------------------------------------------------ public API

        /// <summary>Draws a trail from a to b after delay seconds. Returns the time (from now) the trail reaches b.</summary>
        public float Tracer(Vector3 a, Vector3 b, bool ricochet, float delay = 0f)
        {
            TracerLine t = null;
            foreach (var x in _tracers) if (!x.lr.enabled) { t = x; break; }
            if (t == null)
            {
                var go = new GameObject("Tracer");
                go.transform.SetParent(transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = _additive;
                lr.positionCount = 2;
                lr.numCapVertices = 2;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.alignment = LineAlignment.View;
                t = new TracerLine { lr = lr };
                _tracers.Add(t);
            }
            t.lr.enabled = true;
            t.a = a;
            t.b = b;
            t.t = 0;
            t.delay = delay;
            t.draw = Mathf.Clamp(Vector3.Distance(a, b) / Mathf.Max(1f, trailSpeed), minDrawTime, maxDrawTime);
            t.hold = ricochet ? ricochetHold : 0f;
            t.fade = ricochet ? ricochetFade : tracerLife;
            t.c = ricochet ? ricochetColor : tracerColor;
            t.width = ricochet ? ricochetWidth : tracerWidth;
            t.solid = ricochet;
            t.lr.sortingOrder = ricochet ? 10 : 0;
            Update1(t);
            return delay + t.draw;
        }

        public void Sparks(Vector3 pos, Vector3 normal, int count, float speed)
        {
            count = Mathf.RoundToInt(count * sparkAmount);
            for (int i = 0; i < count; i++)
            {
                Vector3 dir = (normal + Random.insideUnitSphere * 1.1f).normalized;
                bool ember = Random.value < 0.25f; // slower, longer, bouncier
                Emit(_sparks, pos, dir * speed * (ember ? Random.Range(0.2f, 0.5f) : Random.Range(0.5f, 1.5f)),
                    ember ? Random.Range(0.05f, 0.09f) : Random.Range(0.035f, 0.08f),
                    ember ? Random.Range(0.8f, 1.6f) : Random.Range(0.25f, 0.7f),
                    Color.Lerp(new Color(1f, 0.95f, 0.65f), new Color(1f, 0.45f, 0.1f), Random.value));
            }
        }

        void Flash(Vector3 pos, Vector3 normal, float size, Color c)
        {
            Emit(_flash, pos + normal * 0.05f, Vector3.zero, size, 0.07f, c);
            Emit(_flash, pos + normal * 0.05f, Vector3.zero, size * 0.45f, 0.11f, Color.white);
        }

        public void Impact(Vector3 pos, Vector3 normal, Transform surface)
        {
            Flash(pos, normal, 0.9f, new Color(1f, 0.8f, 0.4f, 0.9f));
            Sparks(pos, normal, 40, 11f);
            for (int i = 0; i < 10; i++)
            {
                Vector3 dir = (normal + Random.insideUnitSphere * 0.6f).normalized;
                Emit(_dust, pos, dir * Random.Range(0.6f, 2.4f), Random.Range(0.25f, 0.6f), Random.Range(0.5f, 1.1f),
                    new Color(0.55f, 0.52f, 0.48f, 0.5f));
            }
            Decal(pos, normal, surface);
        }

        public void Ricochet(Vector3 pos, Vector3 normal, Transform surface)
        {
            Flash(pos, normal, 1.3f, new Color(1f, 0.85f, 0.5f, 1f));
            Sparks(pos, normal, 70, 15f);
            Decal(pos, normal, surface);
        }

        public void Blood(Vector3 pos, Vector3 dir)
        {
            dir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.forward;
            // droplets (these land and leave small stains)
            for (int i = 0; i < 45; i++)
            {
                Vector3 d = (dir * 1.1f + Random.insideUnitSphere * 0.9f).normalized;
                Emit(_blood, pos, d * Random.Range(1.5f, 8f), Random.Range(0.06f, 0.18f), Random.Range(0.6f, 1.4f),
                    new Color(Random.Range(0.5f, 0.75f), 0.01f, 0.02f, 1f));
            }
            // mist puff
            for (int i = 0; i < 8; i++)
            {
                Vector3 d = (dir + Random.insideUnitSphere * 0.7f).normalized;
                Emit(_dust, pos, d * Random.Range(0.5f, 2.5f), Random.Range(0.35f, 0.8f), Random.Range(0.3f, 0.6f),
                    new Color(0.55f, 0.02f, 0.03f, 0.55f));
            }
            // big stain where the bullet carries the blood (behind the victim), and one below them
            if (Physics.Raycast(pos, dir, out var hit, 4f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                BloodStain(hit.point, hit.normal, Random.Range(0.6f, 1.1f));
            if (Physics.Raycast(pos, Vector3.down, out hit, 3f, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                BloodStain(hit.point, hit.normal, Random.Range(0.5f, 0.9f));
        }

        /// <summary>Blood splat decal on a surface.</summary>
        public void BloodStain(Vector3 pos, Vector3 normal, float size)
        {
            GameObject go;
            if (_stains.Count >= maxBloodStains) go = _stains.Dequeue();
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "BloodStain";
                Destroy(go.GetComponent<Collider>());
                go.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = _splatMats[Random.Range(0, _splatMats.Length)];
            go.transform.SetPositionAndRotation(pos + normal * (0.006f + Random.value * 0.003f),
                Quaternion.LookRotation(-normal) * Quaternion.Euler(0, 0, Random.Range(0, 360f)));
            go.transform.localScale = new Vector3(size * Random.Range(0.8f, 1.25f), size * Random.Range(0.8f, 1.25f), 1f);
            go.SetActive(true);
            _stains.Enqueue(go);
        }

        float _dropStainBudget;

        /// <summary>Called when a blood droplet hits the world.</summary>
        internal void OnBloodDropLanded(Vector3 pos, Vector3 normal)
        {
            if (_dropStainBudget <= 0f || Random.value > 0.4f) return;
            _dropStainBudget -= 1f;
            BloodStain(pos, normal, Random.Range(0.12f, 0.3f));
        }

        public void Casing(Vector3 pos, Quaternion rot, Vector3 velocity, Material mat)
        {
            GameObject go;
            if (_casings.Count >= maxCasings)
            {
                go = _casings.Dequeue();
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Casing";
                go.layer = Layers.Fx;
                go.transform.localScale = new Vector3(0.012f, 0.012f, 0.035f);
                var rb = go.AddComponent<Rigidbody>();
                rb.mass = 0.01f;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                go.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            if (mat == null)
            {
                if (_casingMat == null)
                {
                    _casingMat = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.85f, 0.65f, 0.25f) };
                }
                mat = _casingMat;
            }
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.transform.SetPositionAndRotation(pos, rot);
            go.SetActive(true);
            var body = go.GetComponent<Rigidbody>();
            body.linearVelocity = velocity;
            body.angularVelocity = Random.insideUnitSphere * 25f;
            _casings.Enqueue(go);
        }

        // ------------------------------------------------------------ internals

        void Decal(Vector3 pos, Vector3 normal, Transform surface)
        {
            GameObject go;
            if (_decals.Count >= maxDecals) go = _decals.Dequeue();
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "BulletHole";
                Destroy(go.GetComponent<Collider>());
                var r = go.GetComponent<Renderer>();
                r.sharedMaterial = new Material(_alpha) { mainTexture = _holeTex, color = new Color(0.05f, 0.05f, 0.05f, 0.9f) };
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            go.transform.SetPositionAndRotation(pos + normal * 0.004f, Quaternion.LookRotation(-normal) * Quaternion.Euler(0, 0, Random.Range(0, 360f)));
            go.transform.localScale = Vector3.one * Random.Range(0.07f, 0.1f);
            go.SetActive(true);
            _decals.Enqueue(go);
        }

        static void EnableCollision(ParticleSystem ps, float bounce, float dampen, bool sendMessages)
        {
            var c = ps.collision;
            c.enabled = true;
            c.type = ParticleSystemCollisionType.World;
            c.mode = ParticleSystemCollisionMode.Collision3D;
            c.collidesWith = Layers.EnvironmentMask;
            c.bounce = bounce;
            c.dampen = dampen;
            c.lifetimeLoss = sendMessages ? 1f : 0.15f; // blood drops die on impact, sparks skitter
            c.quality = ParticleSystemCollisionQuality.Medium;
            c.radiusScale = 0.3f;
            c.sendCollisionMessages = sendMessages;
        }

        static Texture2D MakeSplat(int size, int seed)
        {
            var rng = new System.Random(seed);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var a = new float[size * size];
            float ox = (float)rng.NextDouble() * 100f, oy = (float)rng.NextDouble() * 100f;
            // irregular main blob
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float ang = Mathf.Atan2(dy, dx);
                float edge = 0.45f + 0.18f * Mathf.PerlinNoise(ox + Mathf.Cos(ang) * 1.7f, oy + Mathf.Sin(ang) * 1.7f)
                                   + 0.08f * Mathf.Sin(ang * 7f + ox);
                a[y * size + x] = Mathf.Clamp01((edge - d) * 25f);
            }
            // droplets around it
            int drops = 10 + rng.Next(10);
            for (int i = 0; i < drops; i++)
            {
                float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
                float dist = 0.5f + (float)rng.NextDouble() * 0.42f;
                float r = 0.02f + (float)rng.NextDouble() * 0.05f;
                float cx = Mathf.Cos(ang) * dist, cy = Mathf.Sin(ang) * dist;
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f - cx, dy = (y + 0.5f) / size * 2f - 1f - cy;
                    float v = Mathf.Clamp01((r - Mathf.Sqrt(dx * dx + dy * dy)) * 60f);
                    int k = y * size + x;
                    if (v > a[k]) a[k] = v;
                }
            }
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++)
            {
                // darker, glossier centre
                px[i] = new Color32((byte)(95 + 40 * a[i]), 4, 8, (byte)(a[i] * 235));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        static void Emit(ParticleSystem ps, Vector3 pos, Vector3 vel, float size, float life, Color c)
        {
            var e = new ParticleSystem.EmitParams
            {
                position = pos,
                velocity = vel,
                startSize = size,
                startLifetime = life,
                startColor = c,
                applyShapeToPosition = false,
            };
            ps.Emit(e, 1);
        }

        ParticleSystem MakeSystem(string name, Texture2D tex, bool stretched, float gravity)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = gravity;
            main.maxParticles = 6000;
            var em = ps.emission;
            em.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0, 1) });
            col.color = grad;

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, stretched ? AnimationCurve.Linear(0, 1, 1, 0.2f) : AnimationCurve.Linear(0, 0.6f, 1, 1.4f));

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { mainTexture = tex };
            r.renderMode = stretched ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            if (stretched)
            {
                r.velocityScale = 0.04f;
                r.lengthScale = 1.5f;
            }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            ps.Play();
            return ps;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            _dropStainBudget = Mathf.Min(6f, _dropStainBudget + dt * 30f);
            foreach (var t in _tracers)
            {
                if (!t.lr.enabled) continue;
                t.t += dt;
                if (t.t >= t.delay + t.draw + t.hold + t.fade) { t.lr.enabled = false; continue; }
                Update1(t);
            }
            for (int i = _delayed.Count - 1; i >= 0; i--)
            {
                if (Time.time < _delayed[i].time) continue;
                var a = _delayed[i].action;
                _delayed.RemoveAt(i);
                try { a(); } catch { /* effect target gone */ }
            }
        }

        void Update1(TracerLine t)
        {
            // reveal: the head races from a to b, then hold, then fade
            float local = t.t - t.delay;
            float reveal = local <= 0f ? 0f : Mathf.Clamp01(local / Mathf.Max(0.0001f, t.draw));
            t.lr.SetPosition(0, t.a);
            t.lr.SetPosition(1, Vector3.Lerp(t.a, t.b, reveal));
            float since = local - t.draw;
            float a = local <= 0f ? 0f : since <= t.hold ? 1f : 1f - (since - t.hold) / Mathf.Max(0.0001f, t.fade);
            var c = t.c;
            float o = trailOpacity;
            // ricochet lines are uniformly bright; normal tracers taper toward the shooter
            t.lr.startColor = new Color(c.r, c.g, c.b, (t.solid ? a : a * 0.35f) * o);
            t.lr.endColor = new Color(c.r, c.g, c.b, a * o);
            t.lr.widthMultiplier = (t.solid ? Mathf.Lerp(0.7f, 1f, a) : Mathf.Lerp(0.5f, 1f, a)) * t.width;
        }

        /// <summary>Texture that is soft across the line width but constant along its length.</summary>
        /// <summary>
        /// Trail texture across the line width: bright core, a solid outline band on both edges,
        /// and a 1-2 texel anti-aliased rim. Constant along the length.
        /// </summary>
        static Texture2D MakeBeam(int w, int h, Color outline, float outlineFrac)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[w * h];
            float coreEdge = 1f - outlineFrac;   // where the core turns into outline
            float aa = 2f / h;                   // anti-alias width
            for (int y = 0; y < h; y++)
            {
                float v = Mathf.Abs((y + 0.5f) / h * 2f - 1f);        // 0 centre .. 1 edge
                float toOutline = Mathf.Clamp01((v - coreEdge) / aa + 0.5f);
                float alpha = Mathf.Clamp01((1f - v) / aa);            // crisp outer edge
                Color c = Color.Lerp(Color.white, outline, toOutline);
                c.a = alpha;
                for (int x = 0; x < w; x++) px[y * w + x] = c;
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        static Texture2D MakeRadial(int size, float hole, float falloff)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(1f - d);
                a = Mathf.Pow(a, 1f / falloff * 2f);
                if (hole > 0) a = d < hole ? 1f : Mathf.Clamp01(1f - (d - hole) / (1f - hole)) * 0.6f;
                px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        static Texture2D MakeFlash(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float ang = Mathf.Atan2(dy, dx);
                float spikes = Mathf.Pow(Mathf.Abs(Mathf.Cos(ang * 3f)), 8f) * 0.6f + 0.4f;
                float a = Mathf.Clamp01(1f - d / spikes);
                a = a * a;
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
