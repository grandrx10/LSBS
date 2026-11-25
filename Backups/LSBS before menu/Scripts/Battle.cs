using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LSBS
{
    /// <summary>L.S.B.S. The one object in the scene: makes the field, runs the battle, draws it.</summary>
    [DefaultExecutionOrder(-50)]
    public class Battle : MonoBehaviour
    {
        public Tuning tuning = Tuning.Default;
        [Tooltip("0 picks a new field every time.")] public uint seed = 1917;
        public SoldierData soldiers;
        public Shader soldierShader, worldShader, fxShader, skyShader;
        public RenderPipelineAsset pipeline;

        public BattleSystem Sim { get; private set; }
        public PlayerControl Player { get; private set; }
        public bool PanelOpen { get; private set; }
        public bool PanelHasMouse => PanelOpen;

        MapGenerator _map;
        SoldierRenderer _renderer;
        BodyStore _bodies;
        Fx _fx;
        Sfx _sfx;
        Camera _cam;
        World _world;
        RenderPipelineAsset _wasPipeline;
        Material _props;
        readonly MeshFilter[] _flags = new MeshFilter[Front.Count];
        readonly Mesh[] _cloth = new Mesh[3];
        public float Shake => _fx != null ? _fx.Shake : 0f;
        readonly float[] _frames = new float[240];
        int _frameAt;
        float _avgMs, _lowFps, _readout;
        float _over;

        // ------------------------------------------------------------------ setting up

        void OnValidate() { if (tuning.version != Tuning.Latest) tuning = Tuning.Default; }

        void Awake()
        {
            if (tuning.version != Tuning.Latest) tuning = Tuning.Default;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            if (pipeline != null)
            {
                _wasPipeline = QualitySettings.renderPipeline;
                QualitySettings.renderPipeline = pipeline;
            }
            if (seed == 0) seed = (uint)UnityEngine.Random.Range(1, int.MaxValue);
            Bench.Parse(this);
            Look();

            var root = new GameObject("Field").transform;
            var ground = new Material(worldShader); ground.SetFloat("_Mud", 0.1f);
            var props = new Material(worldShader); props.SetFloat("_Mud", 0.35f);
            _props = props;
            _map = new MapGenerator(root, ground, props, seed);
            _map.Generate();

            _world = World.DefaultGameObjectInjectionWorld ?? new World("LSBS");
            Sim = _world.GetOrCreateSystemManaged<BattleSystem>();
            Sim.T = tuning;
            Sim.Map = _map.Map;
            Sim.Guns = new[] { _map.Guns[0].ToArray(), _map.Guns[1].ToArray() };
            Flags(root);
            _renderer = new SoldierRenderer(soldiers, new Material(soldierShader), tuning.maxCorpses);
            _bodies = new BodyStore(soldiers, _renderer, tuning.maxCorpses, tuning.maxRagdolls, root);
            _fx = new Fx(new Material(fxShader), _map.Chimneys);
            _bodies.Torn = _fx.Gore;
            _sfx = new Sfx(_cam.transform);
            Player = new PlayerControl(this, _cam);
            Sim.StartBattle(tuning.startPerSide);
        }

        void Look()
        {
            _cam = Camera.main;
            if (_cam == null) _cam = new GameObject("Camera", typeof(AudioListener)) { tag = "MainCamera" }.AddComponent<Camera>();
            _cam.farClipPlane = 700f;
            var data = _cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.80f, 0.58f);
            sun.intensity = 1.55f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.92f;
            sunGo.transform.rotation = Quaternion.Euler(27f, 128f, 0);
            RenderSettings.sun = sun;
            RenderSettings.skybox = new Material(skyShader);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.56f, 0.56f, 0.57f);
            RenderSettings.ambientEquatorColor = new Color(0.44f, 0.40f, 0.35f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.19f, 0.16f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.42f, 0.36f, 0.28f);
            RenderSettings.fogDensity = 0.0062f;

            var volume = new GameObject("Post").AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 100;
            var p = ScriptableObject.CreateInstance<VolumeProfile>();
            var tone = p.Add<Tonemapping>(); tone.mode.Override(TonemappingMode.ACES);
            var bloom = p.Add<Bloom>(); bloom.intensity.Override(0.3f); bloom.threshold.Override(1.1f); bloom.scatter.Override(0.6f);
            var grade = p.Add<ColorAdjustments>(); grade.postExposure.Override(1.55f); grade.contrast.Override(10f); grade.saturation.Override(-24f); grade.colorFilter.Override(new Color(1f, 0.965f, 0.91f));
            var vig = p.Add<Vignette>(); vig.intensity.Override(0.24f); vig.smoothness.Override(0.5f);
            var grain = p.Add<FilmGrain>(); grain.type.Override(FilmGrainLookup.Medium2); grain.intensity.Override(0.28f); grain.response.Override(0.75f);
            volume.sharedProfile = p;
        }

        void OnDestroy()
        {
            if (pipeline != null) QualitySettings.renderPipeline = _wasPipeline;
            if (Sim != null) { Sim.Running = false; if (_world.IsCreated) _world.DestroySystemManaged(Sim); }
            _renderer?.Dispose(); _bodies?.Dispose(); _fx?.Dispose(); _map?.Dispose();
        }

        void Flags(Transform root)
        {
            int[] colours = { 0x5d7a2c, 0xa3abb5, 0x4a3c2c };
            for (int i = 0; i < 3; i++)
            {
                var mb = new MeshBuilder();
                Vector3 a = new(0, 0, 0), b = new(0, -2.2f, 0), c = new(0.35f, -1.9f, 3.6f), d = new(0.2f, 0.1f, 3.4f);
                var col = MeshBuilder.Col(colours[i]);
                mb.Quad(a, d, c, b, col);
                mb.Quad(b, c, d, a, col);
                _cloth[i] = mb.Build("Flag");
            }
            for (int o = 0; o < Front.Count; o++)
            {
                var p = Sim.Map.Objectives[o];
                bool home = o == Front.GreenBase || o == Front.GreyBase;
                var go = new GameObject("Flag " + o);
                go.transform.SetParent(root, false);
                go.transform.position = new Vector3(p.x, Sim.Map.Height(p) + (home ? 10.8f : o == Front.Centre ? 8.8f : 7.8f), p.y);
                go.transform.localScale = Vector3.one * (home ? 1.25f : 1f);
                _flags[o] = go.AddComponent<MeshFilter>();
                go.AddComponent<MeshRenderer>().sharedMaterial = _props;
            }
        }

        public void Restart(int perSide)
        {
            if (Player.InSoldier) Player.Leave(false);
            _bodies.Clear();
            tuning.startPerSide = perSide;
            tuning.capPerSide = math.max(perSide, tuning.capPerSide == 0 ? perSide : perSide);
            Sim.T = tuning;
            Sim.StartBattle(perSide);
            _over = 0f;
        }

        // ------------------------------------------------------------------ the frame

        void Update()
        {
            float dt = Time.deltaTime;
            var kb = Keyboard.current;
            if (kb != null && kb.tabKey.wasPressedThisFrame) PanelOpen = !PanelOpen;
            Sim.T = tuning;
            Player.Before(dt);
            Sim.Update();

            Vector3 eye = _cam.transform.position;
            _sfx.Tick(dt);
            for (int i = 0; i < Sim.ShotsSeen.Length; i++)
            {
                var s = Sim.ShotsSeen[i];
                _fx.Shot(s, eye);
                _sfx.Shot(s, eye, Player.InSoldier && s.Shooter == Player.Slot);
                float3 dir = math.normalizesafe(s.To - s.From);
                float3 at = s.To;
                if (!s.Hit) at.y = math.max(at.y, Sim.Map.Height(at.xz));
                if (s.Hit || at.y - Sim.Map.Height(at.xz) < 0.4f) _fx.Impact(at, dir, s.Hit, eye);
            }
            for (int i = 0; i < Sim.Blasts.Length; i++)
            {
                var b = Sim.Blasts[i];
                _sfx.Blast(b, eye);
                if (b.Report) _fx.GunReport(b.At, b.Team == 0 ? 1f : -1f, eye);
                else if (!b.Whistle) { _fx.Explosion(b, eye); _bodies.Blast(b.At, b.Radius * 0.6f); LastBlast = b.At; LastBig |= b.Big; }
            }
            for (int i = 0; i < Sim.Deaths.Length; i++) _bodies.Die(Sim.Deaths[i], eye, tuning);
            for (int i = 0; i < Sim.Grenades.Length; i++)
            {
                var g = Sim.Grenades[i];
                Graphics.DrawMesh(soldiers.grenade, Matrix4x4.TRS(g.Pos, Quaternion.Euler(g.Spin * Time.time * 57f, g.Spin * 40f, 0), Vector3.one * 1.1f), _props, 0);
            }
            for (int o = 0; o < Front.Count; o++)
            {
                var cloth = _cloth[Sim.Owner[o] == 255 ? 2 : Sim.Owner[o]];
                if (_flags[o].sharedMesh != cloth) _flags[o].sharedMesh = cloth;
                _flags[o].transform.rotation = Quaternion.Euler(0, 24f + Mathf.Sin(Time.time * 1.3f + o) * 9f, Mathf.Sin(Time.time * 2.1f + o * 2f) * 4f);
            }
            _bodies.Update(dt);

            if (Sim.Winner != 255)
            {
                _over += dt;
                if (_over > 10f) Restart(tuning.startPerSide);
            }
            Timing();
        }

        void LateUpdate()
        {
            Player.After();
            Bench.Frame(this, _cam, _avgMs);
            _renderer.Draw(Sim, _cam, _bodies, Player.HideHead);
            _fx.Draw(Time.deltaTime, _cam);
        }

        void Timing()
        {
            _frames[_frameAt++ % _frames.Length] = Time.unscaledDeltaTime;
            _readout -= Time.unscaledDeltaTime;
            if (_readout > 0f) return;
            _readout = 0.4f;
            var l = new List<float>(_frames);
            l.RemoveAll(f => f <= 0f);
            if (l.Count == 0) return;
            l.Sort();
            float sum = 0; foreach (var f in l) sum += f;
            _avgMs = sum / l.Count * 1000f;
            _lowFps = 1f / l[Mathf.Clamp((int)(l.Count * 0.99f), 0, l.Count - 1)];
        }

        public Vector3 LastBlast;
        public bool LastBig;
        public int TornApart => _bodies.TornApart;
        public int Drawn => _renderer.Drawn;
        public int Ragdolls => _bodies.Live;
        public int Corpses => _bodies.Count;
        public int Particles => _fx.Count;

        // ------------------------------------------------------------------ screen

        GUIStyle _small, _mid, _big;
        Texture2D _white;
        static readonly Color Parch = new(0.87f, 0.80f, 0.66f), Green = new(0.55f, 0.72f, 0.36f), Grey = new(0.70f, 0.75f, 0.81f), Amber = new(1f, 0.68f, 0.25f);
        static Color Side(int t) => t == 0 ? Green : t == 1 ? Grey : new Color(0.45f, 0.42f, 0.38f);

        void Fill(Rect r, Color c) { GUI.color = c; GUI.DrawTexture(r, _white); GUI.color = Color.white; }
        void Text(Rect r, string s, GUIStyle st, Color c)
        {
            st.normal.textColor = new Color(0, 0, 0, 0.7f); GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), s, st);
            st.normal.textColor = c; GUI.Label(r, s, st);
        }

        void OnGUI()
        {
            if (Bench.HideHud) return;
            if (_white == null)
            {
                _white = Texture2D.whiteTexture;
                _small = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
                _mid = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _big = new GUIStyle(GUI.skin.label) { fontSize = 44, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            }
            float w = Screen.width, h = Screen.height, cx = w * 0.5f;

            // ---- the front: both strengths and the three objectives ----
            Fill(new Rect(cx - 270, 10, 540, 92), new Color(0.07f, 0.06f, 0.05f, 0.72f));
            Fill(new Rect(cx - 270, 100, 540, 2), new Color(0.6f, 0.44f, 0.2f));
            for (int t = 0; t < 2; t++)
            {
                var ts = Sim.Teams[t];
                float x = t == 0 ? cx - 268 : cx + 148;
                Text(new Rect(x, 18, 120, 30), ts.Alive.ToString(), _mid, Side(t));
                Text(new Rect(x, 48, 120, 24), (ts.Attacking ? "▲ " : "■ ") + ts.TotalKills, _mid, ts.Attacking ? Amber : Parch);
            }
            for (int o = 0; o < Front.Count; o++)
            {
                // laid out as they are on the field
                int row = Front.Row[o];
                float down = row == 0 || row == 4 ? 1f : row == 2 ? o - 3 : (o == 1 || o == 6) ? 0.5f : 1.5f;
                var r = new Rect(cx - 140 + row * 60, 70 - down * 27f, 40, 22);
                Fill(r, Side(Sim.Owner[o]) * new Color(0.75f, 0.75f, 0.75f, 1f));
                if (Sim.Capture[o] > 0f) Fill(new Rect(r.x, r.yMax - r.height * Sim.Capture[o], r.width, r.height * Sim.Capture[o]), Side(Sim.Capturing[o]));
                if (o == Front.GreenBase || o == Front.GreyBase) { Fill(new Rect(r.x + 14, r.y + 3, 2, 16), new Color(0.1f, 0.08f, 0.06f)); Fill(new Rect(r.x + 16, r.y + 4, 11, 7), new Color(0.1f, 0.08f, 0.06f)); }
                // and over each, where it is
                var at3 = new Vector3(Sim.Map.Objectives[o].x, Sim.Map.Height(Sim.Map.Objectives[o]) + 12f, Sim.Map.Objectives[o].y);
                if (Project(at3, out var mark)) { Fill(new Rect(mark.x - 5, mark.y - 5, 10, 10), new Color(0.07f, 0.06f, 0.05f, 0.7f)); Fill(new Rect(mark.x - 4, mark.y - 4, 8, 8), Side(Sim.Owner[o])); if (Sim.Capture[o] > 0f) Fill(new Rect(mark.x - 12, mark.y + 7, 24 * Sim.Capture[o], 3), Side(Sim.Capturing[o])); }
            }
            Fill(new Rect(cx - 140, 103, 280 * Mathf.Clamp01(1f - Sim.WaveTimer / Mathf.Max(1f, tuning.waveInterval)), 4), Amber);

            // ---- how it's running ----
            Fill(new Rect(10, 10, 208, 104), new Color(0.07f, 0.06f, 0.05f, 0.72f));
            float fps = _avgMs > 0 ? 1000f / _avgMs : 0;
            Text(new Rect(20, 12, 200, 24), $"{fps:0} fps   ({_avgMs:0.0} ms)", _small, fps > 90 ? Green : fps > 55 ? Amber : new Color(1, 0.4f, 0.3f));
            Text(new Rect(20, 32, 200, 24), $"1% low  {_lowFps:0} fps", _small, Parch);
            Text(new Rect(20, 52, 200, 24), $"battle {Sim.SimMs:0.00} ms   soldiers {Sim.Teams[0].Alive + Sim.Teams[1].Alive}", _small, Parch);
            Text(new Rect(20, 72, 200, 24), $"drawn {Drawn}   bodies {Corpses}", _small, Parch);
            Text(new Rect(20, 90, 200, 24), $"ragdolls {Ragdolls}", _small, Parch);

            if (Sim.Winner != 255) Text(new Rect(0, h * 0.3f, w, 70), Sim.Winner == 0 ? "THE GREENS TAKE THE FIELD" : "THE GREYS TAKE THE FIELD", _big, Side(Sim.Winner));

            if (Player.InSoldier) SoldierHud(w, h, cx);
            else
            {
                Fill(new Rect(cx - 1, h * 0.5f - 1, 3, 3), Parch);
                if (Player.AimedSlot >= 0)
                {
                    var s = Sim.Slots[Player.AimedSlot];
                    if (s.Alive && Project((Vector3)s.Pos + Vector3.up * 2.1f, out var at)) Text(new Rect(at.x - 60, at.y - 24, 120, 24), "F", _mid, Side(s.Team));
                }
                Text(new Rect(20, h - 34, 900, 24), "WASD fly   Shift faster   F take over   1 / 2 deploy with the next green / grey wave   Tab settings", _small, Parch);
            }
            if (Sim.PlayerDeploy >= 0) Text(new Rect(0, 108, w, 30), $"deploying in {Mathf.Max(0, Sim.WaveTimer):0}", _mid, Side(Sim.PlayerDeploy));
            if (PanelOpen) Panel(h);
        }

        bool Project(Vector3 p, out Vector2 at)
        {
            var v = _cam.WorldToScreenPoint(p);
            at = new Vector2(v.x, Screen.height - v.y);
            return v.z > 0;
        }

        unsafe void SoldierHud(float w, float h, float cx)
        {
            int slot = Player.Slot;
            if (slot < 0 || !Sim.Slots[slot].Alive) return;
            var q = Sim.Squads[Sim.Slots[slot].Squad];
            for (int m = 0; m < Squad.Size; m++)
            {
                int s = q.Members[m];
                if (s < 0 || s == slot) continue;
                if (!Project((Vector3)Sim.Slots[s].Pos + Vector3.up * 2.15f, out var at)) continue;
                Text(new Rect(at.x - 20, at.y - 18, 40, 24), "▼", _mid, Amber);
            }
            float gap = Player.Aiming ? 3 : 9;
            Fill(new Rect(cx - 1, h * 0.5f - 1, 2, 2), Parch);
            if (!Player.Aiming)
            {
                Fill(new Rect(cx - gap - 8, h * 0.5f - 1, 8, 2), Parch); Fill(new Rect(cx + gap, h * 0.5f - 1, 8, 2), Parch);
                Fill(new Rect(cx - 1, h * 0.5f + gap, 2, 8), Parch);
            }
            if (Player.HitMark > 0f)
            {
                var c = new Color(1f, 0.5f, 0.2f, Player.HitMark);
                for (int s = -1; s <= 1; s += 2) { Fill(new Rect(cx + s * 12 - 2, h * 0.5f - 14, 4, 4), c); Fill(new Rect(cx + s * 12 - 2, h * 0.5f + 10, 4, 4), c); }
            }
            if (Player.HurtFlash > 0f) Fill(new Rect(0, 0, w, h), new Color(0.35f, 0.02f, 0f, Player.HurtFlash * 0.4f));
            var e = Sim.SlotEntities[slot];
            var gun = Sim.EntityManager.GetComponentData<Gun>(e);
            float health = Sim.EntityManager.GetComponentData<Health>(e).Value;
            Fill(new Rect(24, h - 74, 204, 18), new Color(0.07f, 0.06f, 0.05f, 0.72f));
            Fill(new Rect(26, h - 72, 200 * Mathf.Clamp01(health / 100f), 14), health > 50 ? Parch : new Color(0.8f, 0.25f, 0.15f));
            for (int i = 0; i < Gun.Magazine(Player.Class); i++) Fill(new Rect(w - 120 + i * 22, h - 84, 12, 30), i < gun.Ammo ? Amber : new Color(0.2f, 0.17f, 0.13f, 0.8f));
            if (gun.Phase == GunPhase.Reloading) Fill(new Rect(w - 120, h - 48, 78 * Mathf.Clamp01(1f - gun.Timer / Gun.ReloadTime(Player.Class)), 4), Amber);
            Text(new Rect(24, h - 100, 220, 24), Player.Class.ToString(), _small, Parch);
            if (Player.Class == SoldierClass.Sweeper)
            {
                int left = Sim.EntityManager.GetComponentData<Ability>(e).Grenades;
                for (int i = 0; i < left; i++) Fill(new Rect(w - 120 + i * 22, h - 112, 14, 14), new Color(0.55f, 0.6f, 0.35f));
            }
            else if (Player.Class == SoldierClass.Communicator)
                Text(new Rect(w - 220, h - 116, 196, 24), Sim.IsCalling(slot) ? "guns firing" : Player.CanCall ? "guns ready  (E)" : "no guns free", _small, Player.CanCall ? Amber : Parch);
            Text(new Rect(20, h - 34, 900, 24), "WASD move   Shift run   C kneel   RMB aim   R reload   " + (Player.Class == SoldierClass.Sweeper ? "E grenade   " : Player.Class == SoldierClass.Communicator ? "E artillery   " : "") + "G squad " + (q.PlayerHold ? "holding" : "following") + "   Q leave", _small, Parch);
        }

        int _pick = 500;
        void Panel(float h)
        {
            var r = new Rect(10, 124, 300, 392);
            Fill(r, new Color(0.07f, 0.06f, 0.05f, 0.9f));
            float y = r.y + 8;
            float Row(string label, float v, float lo, float hi, string fmt)
            {
                Text(new Rect(20, y, 180, 22), label, _small, Parch);
                Text(new Rect(230, y, 80, 22), v.ToString(fmt), _small, Amber);
                v = GUI.HorizontalSlider(new Rect(20, y + 22, 270, 16), v, lo, hi);
                y += 42;
                return v;
            }
            _pick = Mathf.RoundToInt(Row("Soldiers a side (applies on restart)", _pick, 50, 10000, "0") / 50f) * 50;
            tuning.waveSize = Mathf.RoundToInt(Row("Wave size", tuning.waveSize, 0, 1000, "0") / 5f) * 5;
            tuning.waveInterval = Row("Seconds between waves", tuning.waveInterval, 5, 120, "0");
            tuning.closeHit = Row("Hit chance up close", tuning.closeHit, 0, 1, "0.00");
            tuning.maxRangeHit = Row("Hit chance at full range", tuning.maxRangeHit, 0, 1, "0.00");
            tuning.aimMax = Row("Longest aim (s)", tuning.aimMax, tuning.aimMin, 6, "0.0");
            tuning.battlefieldModifiers = GUI.Toggle(new Rect(20, y, 260, 22), tuning.battlefieldModifiers, "  cover, kneeling and suppression count");
            y += 30;
            if (GUI.Button(new Rect(20, y, 130, 28), "Restart")) Restart(_pick);
            if (GUI.Button(new Rect(160, y, 130, 28), "New field"))
            {
                Bench.NextSeed = (uint)UnityEngine.Random.Range(1, int.MaxValue);
                
#if UNITY_EDITOR
                UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(gameObject.scene.path, new UnityEngine.SceneManagement.LoadSceneParameters(UnityEngine.SceneManagement.LoadSceneMode.Single));
#else
                UnityEngine.SceneManagement.SceneManager.LoadScene(gameObject.scene.buildIndex);
#endif
            }
        }
    }

    /// <summary>
    /// Command line, for measuring: -lsbs-bench 500,1000,2000 (a side) -lsbs-out results.txt; -lsbs-shots folder takes pictures.
    /// </summary>
    public static class Bench
    {
        public static bool HideHud;
        public static uint NextSeed;
        static int[] _counts;
        static string _out, _shots;
        static int _at = -1;
        static float _t, _battle; static int _killsAt;
        static readonly List<float> Ms = new();
        static float _simSum; static int _simN, _drawnMax, _ragMax;
        static int _shot;
        static float _shotT, _logT;

        public static void Parse(Battle b)
        {
            if (NextSeed != 0) b.seed = NextSeed;
            var a = System.Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < a.Length; i++)
            {
                if (a[i] == "-lsbs-bench") _counts = System.Array.ConvertAll(a[i + 1].Split(','), int.Parse);
                if (a[i] == "-lsbs-out") _out = a[i + 1];
                if (a[i] == "-lsbs-shots") _shots = a[i + 1];
                if (a[i] == "-lsbs-start") { b.tuning.startPerSide = b.tuning.capPerSide = int.Parse(a[i + 1]); }
                if (a[i] == "-lsbs-seed") b.seed = uint.Parse(a[i + 1]);
            }
            if (_counts != null) b.tuning.startPerSide = b.tuning.capPerSide = _counts[0];
            // measuring and taking pictures is silent
            AudioListener.volume = _counts != null || _shots != null || System.Array.IndexOf(a, "-lsbs-mute") >= 0 ? 0f : 1f;
        }

        public static void Frame(Battle b, Camera cam, float avgMs)
        {
            if (_shots != null) Shots(b, cam, avgMs);
            if (_counts == null) return;
            if (_at < 0) { _at = 0; _t = 0; Next(b); return; }
            _t += Time.unscaledDeltaTime;
            _battle += Time.deltaTime;
            // the march is run at speed; measuring starts well into the fighting
            bool warm = _battle < 75f && Ms.Count == 0;
            Time.timeScale = warm ? 4f : 1f;
            // the camera holds a hill over no man's land, most of both armies in view
            b.Player.Place(new Vector3(-30f, 13f, -38f), 42f, 14f);
            if (warm) { _t = 0f; _killsAt = b.Sim.Teams[0].TotalKills + b.Sim.Teams[1].TotalKills; return; }
            Ms.Add(Time.unscaledDeltaTime * 1000f);
            _simSum += b.Sim.SimMs; _simN++;
            _drawnMax = Mathf.Max(_drawnMax, b.Drawn);
            _ragMax = Mathf.Max(_ragMax, b.Ragdolls);
            if (_t < 20f) return;
            Ms.Sort();
            float sum = 0; foreach (var m in Ms) sum += m;
            float avg = sum / Ms.Count, low = Ms[(int)(Ms.Count * 0.99f)];
            string line = $"{_counts[_at]} a side: avg {1000f / avg:0} fps ({avg:0.00} ms), 1% low {1000f / low:0} fps, battle {_simSum / _simN:0.00} ms, alive {b.Sim.Teams[0].Alive}+{b.Sim.Teams[1].Alive}, drawn up to {_drawnMax}, ragdolls up to {_ragMax}, deaths while measuring {b.Sim.Teams[0].TotalKills + b.Sim.Teams[1].TotalKills - _killsAt}, corpses {b.Corpses}, winner {b.Sim.Winner}";
            Debug.Log("[LSBS bench] " + line);
            if (_out != null) File.AppendAllText(_out, line + "\n");
            _at++;
            if (_at >= _counts.Length) { Application.Quit(); _counts = null; return; }
            Next(b);
        }

        static void Next(Battle b)
        {
            _t = 0; _battle = 0; Ms.Clear(); _simSum = 0; _simN = 0; _drawnMax = 0; _ragMax = 0;
            // everyone who falls is replaced at once, so the numbers hold through the measurement
            b.tuning.waveInterval = 1f;
            b.tuning.waveSize = _counts[_at];
            b.Restart(_counts[_at]);
        }

        /// <summary>Pictures at set moments, for checking how it looks.</summary>
        static void Shots(Battle b, Camera cam, float avgMs)
        {
            if (Time.frameCount < 10) return;
            _shotT += Time.deltaTime;
            float[] when = { 3f, 5f, 7f, 9f, 11f, 13f, 15f, 45f, 55f, 70f, 80f, 100f, 101f, 130f };
            Spin(b);
            _logT -= Time.deltaTime;
            if (_logT < 0f)
            {
                _logT = 10f;
                var g = b.Sim.Teams[0]; var y = b.Sim.Teams[1];
                Debug.Log($"[LSBS log] {_shotT:0}s alive {g.Alive}/{y.Alive} attacking {g.Attacking}/{y.Attacking} held {g.Held}/{y.Held} owners {string.Join("", System.Array.ConvertAll(b.Sim.Owner, x => x == 255 ? "." : x == 0 ? "G" : "Y"))} kills {g.TotalKills}/{y.TotalKills} (blast {b.Sim.BlastKills} shotgun {b.Sim.ShotgunKills}) grenades {b.Sim.GrenadesThrown} strikes {b.Sim.StrikesCalled} shells {b.Sim.ShellsLanded} torn {b.TornApart} winner {b.Sim.Winner} sim {b.Sim.SimMs:0.00}ms frame {avgMs:0.0}ms SPIN {_spinText}");
            }
            float end = 400f;
            Time.timeScale = _shot >= when.Length || when[_shot] - _shotT > 3f ? 5f : 1f;
            if (_shot >= when.Length) { if (_shotT > end) Application.Quit(); return; }
            Pose(b, cam, _shot);
            if (_shotT < when[_shot]) return;
            ScreenCapture.CaptureScreenshot(Path.Combine(_shots, $"shot{_shot}.png"));
            _shot++;
        }

        // how much people turn about: the share who turned more than 270 degrees in three seconds, and how often they about-face
        static float[] _yaw, _turned; static float _spinT; static string _spinText = "";
        static void Spin(Battle b)
        {
            var s = b.Sim.Slots;
            if (_yaw == null) { _yaw = new float[s.Length]; _turned = new float[s.Length]; }
            for (int i = 0; i < b.Sim.HighSlot; i++)
            {
                float d = s[i].Yaw - _yaw[i];
                d -= Mathf.PI * 2f * Mathf.Round(d / (Mathf.PI * 2f));
                _yaw[i] = s[i].Yaw;
                if (s[i].Alive) _turned[i] += Mathf.Abs(d);
            }
            _spinT += Time.deltaTime;
            if (_spinT < 3f) return;
            int n = 0, half = 0, full = 0; float sum = 0;
            for (int i = 0; i < b.Sim.HighSlot; i++)
            {
                if (s[i].Alive) { n++; sum += _turned[i]; if (_turned[i] > Mathf.PI) half++; if (_turned[i] > Mathf.PI * 1.5f) full++; }
                _turned[i] = 0f;
            }
            _spinT = 0f;
            _spinText = $"avg {sum / Mathf.Max(1, n) * Mathf.Rad2Deg / 3f:0} deg/s, over 180 in 3s {100f * half / Mathf.Max(1, n):0.0}%, over 270 {100f * full / Mathf.Max(1, n):0.0}%";
        }

        static void Pose(Battle b, Camera cam, int i)
        {
            var sim = b.Sim;
            int FindSlot(System.Func<SlotState, bool> f)
            {
                for (int k = 0; k < sim.HighSlot; k++) if (sim.Slots[k].Alive && f(sim.Slots[k])) return k;
                return -1;
            }
            switch (i)
            {
                case 0: b.Player.Place(new Vector3(-150f, 40f, -100f), 60f, 18f); break;
                case 1: Close(0, SoldierClass.Infantry, true, 1f); break;
                case 2: Close(0, SoldierClass.Infantry, false, -1f); break;
                case 3: Close(0, SoldierClass.Communicator, false, -1f); break;
                case 4: Close(1, SoldierClass.Infantry, true, 1f); break;
                case 5: Close(1, SoldierClass.Communicator, false, -1f); break;
                case 6: Close(1, SoldierClass.Sweeper, false, 1f); break;
                case 7: b.Player.Place(new Vector3(-70f, 34f, -100f), 42f, 18f); break;
                case 8: b.Player.Place(new Vector3(-34f, 9f, -100f), 70f, 10f); break;
                case 9: b.Player.Place(new Vector3(-10f, 60f, -10f), 90f, 40f); break;
                case 10:
                    if (!b.Player.InSoldier) { int s = FindSlot(x => x.Team == 0 && x.HasTarget && x.Class == SoldierClass.Sweeper && sim.Squads[x.Squad].Alive >= 5); if (s >= 0) b.Player.Take(s); }
                    break;
                case 11: case 12:
                    if (b.Player.InSoldier) b.Player.Leave(false);
                    if ((cam.transform.position - b.LastBlast).sqrMagnitude > 40f * 40f || !b.LastBig)
                        b.Player.Place(b.LastBlast + new Vector3(-17f, 7f, -14f), 50f, 15f);
                    break;
                case 13: b.Player.Place(new Vector3(-222f, 12f, -112f), 20f, 12f); break;
            }
            void Close(int team, SoldierClass cls, bool captain, float front)
            {
                int s = FindSlot(x => x.Team == team && x.Class == cls && (sim.Squads[x.Squad].Captain == x.Member) == captain);
                if (s < 0) return;
                var st = sim.Slots[s];
                Vector3 f = new Vector3(Mathf.Sin(st.Yaw), 0, Mathf.Cos(st.Yaw)), r = new Vector3(f.z, 0, -f.x);
                Vector3 eye = (Vector3)st.Pos + f * front * 1.9f - r * 1.1f + Vector3.up * 1.6f;
                Vector3 look = (Vector3)st.Pos + Vector3.up * 1.15f - eye;
                var q = Quaternion.LookRotation(look).eulerAngles;
                b.Player.Place(eye, q.y, q.x > 180f ? q.x - 360f : q.x);
            }
        }
    }
}
