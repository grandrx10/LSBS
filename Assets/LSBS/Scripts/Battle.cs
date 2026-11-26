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
        /// <summary>The game being set up, carried across the field being made anew for it.</summary>
        public static GameSetup Pending;
        public GameSetup Setup { get; private set; }
        MeshRenderer _beacon;
        Material _beaconMat;
        public float Shake => _fx != null ? _fx.Shake : 0f;
        GameMenu _menu;
        VolumeProfile _look;
        UniversalRenderPipelineAsset _pipe;
        public bool MenuOpen => _menu != null && _menu.Open;
        float _orbit;
        readonly float[] _frames = new float[240];
        int _frameAt;
        float _avgMs, _lowFps, _readout;
        float _over;

        // ------------------------------------------------------------------ setting up

        void OnValidate() => Tuning.Upgrade(ref tuning);

        void Awake()
        {
            Tuning.Upgrade(ref tuning);
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            if (pipeline != null)
            {
                _wasPipeline = QualitySettings.renderPipeline;
                // a copy, so the settings menu never writes to the asset
                _pipe = Instantiate(pipeline) as UniversalRenderPipelineAsset;
                QualitySettings.renderPipeline = _pipe;
            }
            Options.Load();
            useGUILayout = false;
            if (seed == 0) seed = (uint)UnityEngine.Random.Range(1, int.MaxValue);
            Bench.Parse(this);
            Look();

            // the blockouts the machines were made from stay in the scene to look at, out of the way while playing
            foreach (var name in new[] { "Tank", "TankDestroyer", "Transport", "VehicleTransport", "MissileLauncher", "Nothing" })
            {
                var blockout = GameObject.Find(name);
                if (blockout != null && blockout.transform.parent == null) blockout.SetActive(false);
            }
            var root = new GameObject("Field").transform;
            var ground = new Material(worldShader); ground.SetFloat("_Mud", 0.1f);
            var props = new Material(worldShader); props.SetFloat("_Mud", 0.35f);
            _props = props;
            // the field's made for so many sides: a new game with a different number makes a new field
            Setup = Pending;
            int teams = Setup != null ? Setup.Teams : Bench.Asked ? 2 : 4;
            _map = new MapGenerator(root, ground, props, seed, teams);
            _map.Generate();

            _world = World.DefaultGameObjectInjectionWorld ?? new World("LSBS");
            Sim = _world.GetOrCreateSystemManaged<BattleSystem>();
            Sim.T = tuning;
            Sim.Map = _map.Map;
            
            Sim.TankBlocked = _map.TankBlocked;
            Sim.TankFlow = _map.TankFlow;
            Sim.MaximBack = soldiers.maximBack;
            Sim.MaximMuzzle = soldiers.maximMuzzle;
            Stands();
            Beacon(root);
            _renderer = new SoldierRenderer(soldiers, new Material(soldierShader), tuning.maxCorpses);
            _bodies = new BodyStore(soldiers, _renderer, tuning.maxCorpses, tuning.maxRagdolls, root, p => Sim.Map.Height(new float2(p.x, p.z)));
            _fx = new Fx(new Material(fxShader), _map.Chimneys);
            _bodies.Torn = _fx.Gore;
            _sfx = new Sfx(_cam.transform);
            Player = new PlayerControl(this, _cam);
            _menu = new GameMenu(this, _cam, _pipe, _look);
            if (Bench.Asked) { AudioListener.volume = 0f; StartNow(GameSetup.Even(2, tuning.startPerSide / Squad.Size, tuning)); }
            else if (Setup != null) { Pending = null; StartNow(Setup); }
            else _menu.Show(GameMenu.Page.Main);
        }

        // ------------------------------------------------------------------ what the menu asks for

        /// <summary>A new game: on a field made for it, so a fresh one is made first.</summary>
        public void Begin(GameSetup setup)
        {
            Pending = setup;
            Bench.NextSeed = setup.Seed != 0 ? setup.Seed : (uint)UnityEngine.Random.Range(1, int.MaxValue);
            Reload();
        }

        /// <summary>Starts the game set up, on the field as it is.</summary>
        public void StartNow(GameSetup setup)
        {
            if (Player.InSoldier) Player.Leave(false);
            _bodies.Clear();
            Setup = setup;
            Sim.T = tuning;
            Player.Rts.Team = setup.Player;
            Sim.StartBattle(setup.Teams, setup.Player, setup.First, setup.Wave);
            CommandHome();
            _over = 0f;
        }

        /// <summary>The commander's view goes back over their own side's landing ground, looking in at the objective.</summary>
        void CommandHome()
        {
            var home = Sim.Map.Objectives[Front.Home(Player.Rts.Team)];
            float2 ahead = home * 0.72f;
            float yaw = math.degrees(math.atan2(-home.x, -home.y));
            Player.Rts.LookAt(new Vector3(ahead.x, Sim.Map.Height(ahead), ahead.y), yaw);
            Player.Rts.Selected.Clear();
            Player.Rts.Vehicles.Clear();
        }

        public void ToMenu()
        {
            if (Player.InSoldier) Player.Leave(false);
            Sim.Stop();
            _bodies.Clear();
        }

        public void NewField()
        {
            Bench.NextSeed = (uint)UnityEngine.Random.Range(1, int.MaxValue);
            Reload();
        }

        void Reload()
        {
            Time.timeScale = 1f;
            AudioListener.pause = false;
#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(gameObject.scene.path, new UnityEngine.SceneManagement.LoadSceneParameters(UnityEngine.SceneManagement.LoadSceneMode.Single));
#else
            UnityEngine.SceneManagement.SceneManager.LoadScene(gameObject.scene.buildIndex);
#endif
        }

        public void SetRagdolls(int n) { if (_bodies != null) _bodies.Limit = n; }

        public Vector3 CorpsePos(out bool any)
        {
            any = _bodies.PlayerCorpse >= 0;
            return any ? _bodies.CorpsePos(_bodies.PlayerCorpse) : Vector3.zero;
        }

        string _killedBy = "";
        int _hover = -1, _hoverFrame = -1;

        public void SetDetail(float near, float shadow)
        {
            if (_renderer == null) return;
            _renderer.NearDistance = near;
            _renderer.ShadowDistance = shadow;
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
            _look = p;
        }

        void OnDestroy()
        {
            if (pipeline != null) QualitySettings.renderPipeline = _wasPipeline;
            if (_pipe != null) Destroy(_pipe);
            Time.timeScale = 1f;
            AudioListener.pause = false;
            if (Sim != null) { if (_world.IsCreated) { Sim.Stop(); _world.DestroySystemManaged(Sim); } }
            _renderer?.Dispose(); _bodies?.Dispose(); _fx?.Dispose(); _map?.Dispose();
        }

        /// <summary>The objective's beacon: a ring of light that takes the colour of whoever holds it.</summary>
        void Beacon(Transform root)
        {
            var mb = new MeshBuilder();
            mb.Tube(new Vector3(0, -0.35f, 0), new Vector3(0, 0.35f, 0), 1.6f, 1.6f, 24, MeshBuilder.Col(0xffffff, MeshBuilder.Glow), false);
            mb.Tube(new Vector3(0, 0.6f, 0), new Vector3(0, 1.0f, 0), 0.5f, 0.2f, 12, MeshBuilder.Col(0xffffff, MeshBuilder.Glow));
            var go = new GameObject("Beacon");
            go.transform.SetParent(root, false);
            go.transform.position = _map.Beacon;
            go.AddComponent<MeshFilter>().sharedMesh = mb.Build("Beacon");
            _beaconMat = new Material(_props);
            _beacon = go.AddComponent<MeshRenderer>();
            _beacon.sharedMaterial = _beaconMat;
        }

        /// <summary>Another game the same as the last, on a new field.</summary>
        public void Again() => Begin(Setup ?? GameSetup.Even(2, 30, tuning));

        // ------------------------------------------------------------------ the frame

        void Update()
        {
            float dt = Time.deltaTime;
            var kb = Keyboard.current;
            _menu.Update();
            if (_menu.Now == GameMenu.Page.Main)
            {
                // a slow look over the empty field
                _orbit += Time.unscaledDeltaTime * 4f;
                var turn = Quaternion.Euler(0, _orbit, 0);
                Player.Place(turn * new Vector3(0, 34f, -95f), _orbit, 14f);
            }
            Sim.T = tuning;
            using (Prof.Player.Auto()) Player.Before(dt);
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
                if (b.Report) _fx.GunReport(b.At, 1f, eye);
                else if (b.Cannon) _fx.CannonFire(b.At, b.Dir, eye);
                else if (b.Wreck) _fx.Explosion(b, eye);
                else if (!b.Whistle) { _fx.Explosion(b, eye); _bodies.Blast(b.At, b.Radius * 0.6f); LastBlast = b.At; LastBig |= b.Big; }
            }
            Shells(dt);
            using (Prof.DrawMaxims.Auto()) DrawMaxims();
            using (Prof.DrawTanks.Auto()) { DrawTanks(dt); DrawAir(dt); Launchers(); _batch.Draw(); }
            for (int i = 0; i < Sim.Deaths.Length; i++)
            {
                var d = Sim.Deaths[i];
                _bodies.Die(d, eye, tuning);
                if (!d.Player) continue;
                _killedBy = d.Kind == ShotKind.Tank ? "Run down by a tank" : d.Kind == ShotKind.Blast ? (d.Big ? "Artillery" : "Grenade") : d.Kind == ShotKind.Shotgun ? "Shotgun" : d.Kind == ShotKind.Maxim ? "Machine gun" : d.Kind == ShotKind.Pistol ? "Pistol" : "LR-15";
            }
            for (int i = 0; i < Sim.Grenades.Length; i++)
            {
                var g = Sim.Grenades[i];
                Graphics.DrawMesh(soldiers.grenade, Matrix4x4.TRS(g.Pos, Quaternion.Euler(g.Spin * Time.time * 57f, g.Spin * 40f, 0), Vector3.one * 1.1f), _props, 0);
            }
            if (_beacon != null)
            {
                byte owner = Sim.Owner.Length > 0 ? Sim.Owner[Front.Centre] : (byte)255;
                var c = owner == 255 ? new Color(0.55f, 0.58f, 0.62f) : Team.Colour(owner);
                if (Sim.Capturing.Length > 0 && Sim.Capturing[Front.Centre] != 255 && Mathf.Repeat(Time.time * 2f, 1f) < 0.5f) c = Color.Lerp(c, Team.Colour(Sim.Capturing[Front.Centre]), 0.7f);
                _beaconMat.SetColor("_Tint", c);
                _beacon.transform.rotation = Quaternion.Euler(0, Time.time * 40f, 0);
            }
            using (Prof.Bodies.Auto()) _bodies.Update(dt, eye);

            if (Sim.Winner != 255)
            {
                _over += dt;
                if (_over > 12f) { ToMenu(); _menu.Show(GameMenu.Page.Main); }
            }
            Timing();
        }

        // ------------------------------------------------------------------ tanks

        readonly MeshBatch _batch = new();
        VehicleModels _models;
        readonly Material[] _paint = new Material[Team.Max];
        Material _burnt;
        float _smokeClock;

        void Models()
        {
            if (_models != null) return;
            _models = new VehicleModels();
            _props.enableInstancing = true;
            _burnt = new Material(_props);
            _burnt.SetColor("_Tint", new Color(0.3f, 0.27f, 0.25f));
            for (int t = 0; t < Team.Max; t++)
            {
                _paint[t] = new Material(_props) { enableInstancing = true };
                _paint[t].SetColor("_Tint", Team.Colour(t));
            }
        }

        void DrawTanks(float dt)
        {
            Models();
            Vector3 eye = _cam.transform.position;
            _smokeClock += dt;
            bool puff = _smokeClock > 0.2f;
            if (puff) _smokeClock = 0f;
            foreach (var t in Sim.Tanks)
            {
                float d = ((Vector3)t.Pos - eye).magnitude;
                var rot = t.Rotation;
                float shake = t.Wreck || t.Carried ? 0f : 0.012f * (0.4f + t.Rumble);
                Vector3 p = (Vector3)t.Pos + Vector3.down * BattleSystem.Sunk(t) + new Vector3(0, Mathf.Sin(Time.time * 47f + t.Pos.x) * shake, 0);
                if (puff && d < 150f && !t.Carried && _renderer.Visible(p + Vector3.up * 3f, 6f))
                {
                    if (t.Wreck) { if (t.WreckTime < Tank.WreckStay) _fx.Funnel(p + rot * new Vector3(0, 2.6f, -0.6f), 1f, true); }
                    else if (Mathf.Abs(t.Speed) > 0.6f && d < 50f) for (int s = -1; s <= 1; s += 2) _fx.TrackDust(p + rot * new Vector3(s * 2.4f, 0.2f, -t.HalfLong * Mathf.Sign(t.Speed)), Mathf.Abs(t.Speed));
                }
                if (d > 460f || !_renderer.Visible(p, 8f)) continue;
                var m = t.Kind == TankKind.Tank ? _models.Tank : _models.Destroyer;
                var mat = t.Wreck ? _burnt : _props;
                Matrix4x4 body = Matrix4x4.TRS(p, rot, Vector3.one);
                _batch.Add(m.Hull, mat, body);
                if (!t.Wreck) _batch.Add(m.Paint, _paint[t.Team & 7], body);
                for (int s = 0; s < 2; s++)
                {
                    float run = s == 0 ? t.TrackL : t.TrackR;
                    int phase = d > 70f ? 0 : Mathf.FloorToInt(Mathf.Repeat(-run / 0.4f, 1f) * VehicleModels.Phases) % VehicleModels.Phases;
                    _batch.Add(m.Track[phase], mat, body * Matrix4x4.Translate(new Vector3((s == 0 ? -1f : 1f) * m.TrackX, 0, 0)));
                }
                if (t.Kind == TankKind.Tank)
                {
                    var turret = Matrix4x4.TRS(p + rot * TankShape.TurretPivot, t.TurretRotation, Vector3.one);
                    _batch.Add(m.Turret, mat, turret);
                    if (!t.Wreck) _batch.Add(m.TurretPaint, _paint[t.Team & 7], turret);
                    if (d < 140f) _batch.Add(m.MachineGun, mat, Matrix4x4.TRS(p + rot * TankShape.GunPort, t.GunRotation(0), Vector3.one));
                }
                var cannon = t.CannonRotation;
                Vector3 trunnion = t.CannonBase + (p - (Vector3)t.Pos);
                _batch.Add(m.Gun, mat, Matrix4x4.TRS(trunnion - cannon * Vector3.forward * (t.Recoil * t.Recoil * 0.7f), cannon, Vector3.one));
            }
        }

        /// <summary>Transports: their bodies, their colours, their fans going round.</summary>
        void DrawAir(float dt)
        {
            Models();
            Vector3 eye = _cam.transform.position;
            foreach (var a in Sim.Flights)
            {
                if (a.Delay > 0f) continue;
                Vector3 p = a.Pos;
                if ((p - eye).sqrMagnitude > 600f * 600f || !_renderer.Visible(p, 9f)) continue;
                var m = a.Kind == AirKind.Troop ? _models.Troop : _models.Lifter;
                var rot = a.Rotation;
                var body = Matrix4x4.TRS(p, rot, Vector3.one);
                _batch.Add(m.Body, a.Wreck ? _burnt : _props, body);
                if (!a.Wreck) _batch.Add(m.Paint, _paint[a.Team & 7], body);
                float spin = Time.time * 1400f * (a.Wreck ? 0.2f : 1f);
                for (int i = 0; i < m.Fans.Length; i++)
                    _batch.Add(m.Fan, _props, body * Matrix4x4.TRS(m.Fans[i], Quaternion.Euler(0, i % 2 == 0 ? spin : -spin, 0), Vector3.one));
                if (a.Wreck && Time.frameCount % 3 == 0) _fx.Funnel(p, 1f, true);
            }
        }

        void TankHud(Tank t, float w, float h, float cx)
        {
            Fill(new Rect(24, h - 74, 204, 18), new Color(0.07f, 0.06f, 0.05f, 0.72f));
            Fill(new Rect(26, h - 72, 200 * Mathf.Clamp01(t.Health / Tank.Health0), 14), t.Health > Tank.ShellHurt ? Parch : new Color(0.8f, 0.25f, 0.15f));
            string[] seats = { "1  Driver + main gun", "2  Machine gun" };
            for (int i = 0; i < (t.GunCount > 0 ? 2 : 1); i++) Text(new Rect(24, h - 152 + i * 24, 240, 22), seats[i], _small, t.Seat == i ? Amber : Parch);
            Fill(new Rect(w - 240, h - 66, 200, 18), new Color(0.07f, 0.06f, 0.05f, 0.72f));
            if (t.Seat == 0)
            {
                Fill(new Rect(w - 238, h - 64, 196 * (1f - t.Reload / Tank.ReloadTime), 14), t.Reload > 0f ? Parch : Amber);
                Text(new Rect(w - 240, h - 94, 200, 24), t.Reload > 0f ? "loading" : "main gun", _small, Parch);
                // whereabouts the shell will land, and how far round the gun goes
                Vector3 mark = Sim.CannonPoint(t);
                float scatter = Sim.Scatter(t, mark);
                for (int i = 0; i < 28; i++)
                {
                    float2 p = new float2(mark.x, mark.z) + new float2(Mathf.Sin(i * 0.2244f), Mathf.Cos(i * 0.2244f)) * scatter;
                    if (Project(new Vector3(p.x, Sim.Map.Height(p) + 0.2f, p.y), out var dot)) Fill(new Rect(dot.x - 1.5f, dot.y - 1.5f, 3, 3), t.Reload > 0f ? Parch : Amber);
                }
                if (Project(Sim.CannonPoint(t), out var at))
                {
                    var c = t.Reload > 0f ? Parch : Amber;
                    Fill(new Rect(at.x - 14, at.y - 1, 10, 2), c); Fill(new Rect(at.x + 4, at.y - 1, 10, 2), c);
                    Fill(new Rect(at.x - 1, at.y - 14, 2, 10), c); Fill(new Rect(at.x - 1, at.y + 4, 2, 10), c);
                }
                Fill(new Rect(cx - 1, h * 0.5f - 1, 3, 3), new Color(1, 1, 1, 0.5f));
                Fill(new Rect(cx - 70, h - 60, 140, 3), new Color(0.2f, 0.17f, 0.13f, 0.8f));
                Fill(new Rect(cx - 3 + t.CannonYaw / t.Arc * 70f, h - 64, 6, 11), Amber);
                Text(new Rect(20, h - 34, 1000, 24), "W S drive   A D turn   Mouse aim   LMB fire   " + (t.GunCount > 0 ? "1 2 change place   " : "") + "E get out", _small, Parch);
            }
            else
            {
                var g = t.Guns[t.Seat - 1];
                Fill(new Rect(w - 238, h - 64, 196 * (g.Reloading > 0f ? 1f - g.Reloading / Maxim.Reload : g.Ammo / (float)Maxim.Clip), 14), g.Reloading > 0f ? Parch : Amber);
                Text(new Rect(w - 240, h - 94, 200, 24), g.Reloading > 0f ? "reloading" : "Maxim  " + g.Ammo, _small, Parch);
                Fill(new Rect(cx - 1, h * 0.5f - 1, 2, 2), Parch);
                Text(new Rect(20, h - 34, 1000, 24), "Mouse swing   LMB fire   R reload   1 2 change place   E get out", _small, Parch);
            }
        }

        Mesh _tube, _missile;

        /// <summary>Launchers on shoulders, and missiles in the air.</summary>
        void Launchers()
        {
            if (_tube == null)
            {
                // from the blockout: a tube 1.7 m long on the shoulder, a sight off its side, a grip and trigger under it
                var mb = new MeshBuilder();
                Color body = MeshBuilder.Col(0x30363d, MeshBuilder.Metal), dark = MeshBuilder.Col(0x15181b), glow = MeshBuilder.Col(0x7fe6ff, MeshBuilder.Glow);
                mb.Tube(new Vector3(0, 0, -0.95f), new Vector3(0, 0, 0.7f), 0.15f, 0.15f, 12, body);
                mb.Tube(new Vector3(0, 0, -1.05f), new Vector3(0, 0, -0.95f), 0.18f, 0.16f, 12, dark);
                mb.Tube(new Vector3(0, 0, 0.66f), new Vector3(0, 0, 0.74f), 0.17f, 0.17f, 12, dark);
                mb.Tube(new Vector3(0, 0, 0.5f), new Vector3(0, 0, 0.54f), 0.155f, 0.155f, 12, glow);
                mb.Tube(new Vector3(0.2f, 0.04f, -0.3f), new Vector3(0.2f, 0.04f, 0.05f), 0.06f, 0.06f, 8, dark);
                mb.Box(new Vector3(0.2f, 0.04f, 0.06f), new Vector3(0.08f, 0.08f, 0.02f), Quaternion.identity, glow);
                mb.Box(new Vector3(0, -0.2f, -0.1f), new Vector3(0.05f, 0.18f, 0.08f), Quaternion.Euler(-12f, 0, 0), dark);
                mb.Box(new Vector3(0, -0.12f, 0.25f), new Vector3(0.05f, 0.12f, 0.06f), Quaternion.identity, dark);
                _tube = mb.Build("Launcher");
                mb = new MeshBuilder();
                mb.Tube(new Vector3(0, 0, -0.35f), new Vector3(0, 0, 0.25f), 0.07f, 0.07f, 8, MeshBuilder.Col(0xd8dde2, MeshBuilder.Metal));
                mb.Tube(new Vector3(0, 0, 0.25f), new Vector3(0, 0, 0.42f), 0.07f, 0.01f, 8, MeshBuilder.Col(0x30363d, MeshBuilder.Metal));
                for (int f = 0; f < 4; f++) mb.Box(Quaternion.Euler(0, 0, f * 90f) * new Vector3(0, 0.09f, -0.3f), new Vector3(0.01f, 0.08f, 0.12f), Quaternion.Euler(0, 0, f * 90f), MeshBuilder.Col(0x30363d));
                mb.Tube(new Vector3(0, 0, -0.4f), new Vector3(0, 0, -0.35f), 0.05f, 0.05f, 8, MeshBuilder.Col(0xff8a3a, MeshBuilder.Glow));
                _missile = mb.Build("Missile");
            }
            Vector3 eye = _cam.transform.position;
            for (int i = 0; i < Sim.HighSlot; i++)
            {
                var s = Sim.Slots[i];
                if (!s.Alive || s.Inside || s.Class != SoldierClass.Launcher) continue;
                if (((Vector3)s.Pos - eye).sqrMagnitude > 160f * 160f) continue;
                var rot = Quaternion.Euler(-6f, s.Yaw * Mathf.Rad2Deg, 0);
                float up = (s.Kneel ? 1.05f : 1.47f) * s.Height;
                _batch.Add(_tube, _props, Matrix4x4.TRS((Vector3)s.Pos + rot * new Vector3(0.24f, 0.12f, 0.05f) + Vector3.up * up, rot, Vector3.one));
            }
            foreach (var m in Sim.Missiles)
                _batch.Add(_missile, _props, Matrix4x4.TRS(m.Pos, Quaternion.LookRotation(math.normalizesafe(m.Vel, new float3(0, 0, 1))), Vector3.one));
        }

        readonly Mesh[] _stand = new Mesh[2];

        /// <summary>The sandbags a machine gun's tripod stands on, for either height.</summary>
        void Stands()
        {
            var rng = new Unity.Mathematics.Random(5);
            for (int t = 0; t < 2; t++)
            {
                float top = (t == 0 ? MaximShape.LowPivot : MaximShape.TallPivot) - soldiers.maximTripodHeight;
                var mb = new MeshBuilder();
                int rows = Mathf.Max(0, Mathf.CeilToInt(top / 0.2f));
                for (int r = 0; r < rows; r++)
                {
                    float y = top - (r + 0.5f) * 0.2f, wide = 0.62f + r * 0.07f;
                    for (int i = 0; i < 6; i++)
                    {
                        float a = (i + (r % 2) * 0.5f) * 60f;
                        var q = Quaternion.Euler(0, a + rng.NextFloat(-8f, 8f), 0);
                        mb.Box(q * new Vector3(0, 0, wide * 0.62f) + Vector3.up * y, new Vector3(0.5f, 0.22f, 0.36f), q, Color.Lerp(MeshBuilder.Col(0x6b5c40), MeshBuilder.Col(0x4c4230), rng.NextFloat()), 0.85f);
                    }
                }
                if (mb.I.Count > 0) _stand[t] = mb.Build("Stand");
            }
        }

        void DrawMaxims()
        {
            Vector3 eye = _cam.transform.position;
            for (int i = 0; i < Sim.Maxims.Length; i++)
            {
                var m = Sim.Maxims[i];
                Vector3 p = m.Pos;
                if (!m.Active || (p - eye).sqrMagnitude > 260f * 260f || !_renderer.Visible(p, 4f)) continue;
                int t = m.Tall ? 1 : 0;
                var still = Quaternion.Euler(0, m.BaseYaw * Mathf.Rad2Deg, 0);
                float shake = m.Timer > 0f && m.Timer < 0.1f && m.User >= 0 && m.Reloading <= 0f ? Mathf.Sin(Time.time * 190f) * 0.35f : 0f;
                var swing = Quaternion.Euler(m.Pitch * Mathf.Rad2Deg + shake, m.Yaw * Mathf.Rad2Deg, 0);
                if (_stand[t] != null) _batch.Add(_stand[t], _props, Matrix4x4.TRS(p, still, Vector3.one));
                _batch.Add(soldiers.maximTripod, _props, Matrix4x4.TRS(m.Pivot, still, Vector3.one));
                _batch.Add(soldiers.maximGun, _props, Matrix4x4.TRS(m.Pivot, swing, Vector3.one));
            }
        }

        unsafe void Shells(float dt)
        {
            if (dt <= 0f || !_fx.TracerTick(dt)) return;
            foreach (var s in Sim.Shells) _fx.ShellPoint(s.Pos, s.Vel);
            foreach (var m in Sim.Missiles) if (m.Age < Missile.Burn) _fx.MissileTrail(m.Pos, m.Vel);
        }

        void LateUpdate()
        {
            Player.After();
            Bench.Frame(this, _cam, _avgMs);
            using (Prof.Soldiers.Auto()) _renderer.Draw(Sim, _cam, _bodies, Player.HideHead);
            using (Prof.Fx.Auto()) _fx.Draw(Time.deltaTime, _cam);
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
        static readonly Color Parch = new(0.86f, 0.88f, 0.9f), Green = new(0.45f, 0.9f, 0.55f), Amber = new(0.5f, 0.9f, 1f);
        static Color Side(int t) => Team.Colour(t);

        void Fill(Rect r, Color c) { GUI.color = c; GUI.DrawTexture(r, _white); GUI.color = Color.white; }
        void Text(Rect r, string s, GUIStyle st, Color c)
        {
            st.normal.textColor = new Color(0, 0, 0, 0.7f); GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), s, st);
            st.normal.textColor = c; GUI.Label(r, s, st);
        }

        void OnGUI()
        {
            if (Bench.HideHud) return;
            // nothing to click: only the drawing needs doing
            if (Event.current.type != EventType.Repaint && !PanelOpen && !MenuOpen) return;
            if (_menu != null && _menu.Now == GameMenu.Page.Main || _menu != null && _menu.Now == GameMenu.Page.Settings && !Sim.Running) { _menu.Draw(); return; }
            if (_white == null)
            {
                _white = Texture2D.whiteTexture;
                _small = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
                _mid = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _big = new GUIStyle(GUI.skin.label) { fontSize = 44, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            }
            float w = Screen.width, h = Screen.height, cx = w * 0.5f;

            // ---- every side: how many it has, and how long it's held the objective ----
            int n = Sim.TeamCount;
            float rowH = 22f, boxW = 420f, top = 10f;
            Fill(new Rect(cx - boxW * 0.5f, top, boxW, 40 + n * rowH), new Color(0.04f, 0.05f, 0.06f, 0.75f));
            byte owner = Sim.Owner.Length > 0 ? Sim.Owner[Front.Centre] : (byte)255;
            string state = owner == 255 ? "OBJECTIVE  unheld" : $"OBJECTIVE  held by {Team.Names[owner]}";
            if (Sim.Capturing.Length > 0 && Sim.Capturing[Front.Centre] != 255) state += $"   {Team.Names[Sim.Capturing[Front.Centre]]} taking it {Sim.Capture[Front.Centre] * 100f:0}%";
            Text(new Rect(cx - boxW * 0.5f + 12, top + 4, boxW - 24, 22), state, _small, owner == 255 ? Parch : Side(owner));
            float wave = Mathf.Max(0f, Sim.WaveTimer);
            Text(new Rect(cx + boxW * 0.5f - 150, top + 4, 140, 22), $"wave in {(int)wave / 60}:{(int)wave % 60:00}", _small, Parch);
            for (int t = 0; t < n; t++)
            {
                float y = top + 30 + t * rowH;
                var ts = Sim.Teams[t];
                Fill(new Rect(cx - boxW * 0.5f + 12, y + 5, 12, 12), Side(t));
                Text(new Rect(cx - boxW * 0.5f + 30, y, 120, rowH), Team.Names[t] + (t == Sim.PlayerTeam ? "  (you)" : ""), _small, t == Sim.PlayerTeam ? Amber : Parch);
                Text(new Rect(cx - boxW * 0.5f + 140, y, 90, rowH), $"{ts.Alive} men  {Sim.TanksOf(t)} veh", _small, Parch);
                float held = Sim.Held[t], need = Mathf.Max(1f, tuning.holdToWin);
                Fill(new Rect(cx - boxW * 0.5f + 240, y + 7, 120, 8), new Color(0.15f, 0.16f, 0.18f, 0.9f));
                Fill(new Rect(cx - boxW * 0.5f + 240, y + 7, 120 * Mathf.Clamp01(held / need), 8), Side(t));
                Text(new Rect(cx - boxW * 0.5f + 366, y, 60, rowH), $"{(int)held / 60}:{(int)held % 60:00}", _small, owner == t ? Side(t) : Parch);
            }
            // and over the objective itself, where it is
            var oc = Sim.Map.Objectives[Front.Centre];
            if (Project(new Vector3(oc.x, Sim.Map.Height(oc) + 14f, oc.y), out var mark))
            {
                Fill(new Rect(mark.x - 6, mark.y - 6, 12, 12), new Color(0.04f, 0.05f, 0.06f, 0.7f));
                Fill(new Rect(mark.x - 5, mark.y - 5, 10, 10), owner == 255 ? Parch : Side(owner));
            }

            // ---- how it's running ----
            if (Options.Counter) {
            Fill(new Rect(10, 10, 208, 104), new Color(0.07f, 0.06f, 0.05f, 0.72f));
            float fps = _avgMs > 0 ? 1000f / _avgMs : 0;
            Text(new Rect(20, 12, 200, 24), $"{fps:0} fps   ({_avgMs:0.0} ms)", _small, fps > 90 ? Green : fps > 55 ? Amber : new Color(1, 0.4f, 0.3f));
            Text(new Rect(20, 32, 200, 24), $"1% low  {_lowFps:0} fps", _small, Parch);
            int everyone = 0; for (int t = 0; t < Sim.TeamCount; t++) everyone += Sim.Teams[t].Alive;
            Text(new Rect(20, 52, 200, 24), $"battle {Sim.SimMs:0.00} ms   soldiers {everyone}", _small, Parch);
            Text(new Rect(20, 72, 200, 24), $"drawn {Drawn}   bodies {Corpses}", _small, Parch);
            Text(new Rect(20, 90, 200, 24), $"ragdolls {Ragdolls}", _small, Parch);
            }

            if (Sim.Winner != 255) Text(new Rect(0, h * 0.3f, w, 70), Team.Names[Sim.Winner].ToUpper() + (Sim.Winner == Sim.PlayerTeam ? " WINS  -  YOU WIN" : " WINS"), _big, Side(Sim.Winner));

            if (Player.IsDead)
            {
                if (Player.HurtFlash > 0f) Fill(new Rect(0, 0, w, h), new Color(0.35f, 0.02f, 0f, Player.HurtFlash * 0.5f));
                Fill(new Rect(0, h * 0.72f, w, 96), new Color(0.05f, 0.02f, 0.02f, 0.6f));
                Text(new Rect(0, h * 0.72f + 4, w, 56), "YOU DIED", _big, new Color(0.85f, 0.2f, 0.12f));
                Text(new Rect(0, h * 0.72f + 60, w, 28), _killedBy, _mid, Parch);
            }
            else if (Player.InSoldier) SoldierHud(w, h, cx);
            else
            {
                Player.Rts.Draw(Side(Player.Rts.Team), Amber);
                int picked = Player.Rts.Selected.Count, armour = Player.Rts.Vehicles.Count;
                if (picked + armour > 0) Text(new Rect(20, h - 60, 900, 24), (picked > 0 ? picked + (picked == 1 ? " squad, " : " squads, ") + Player.Rts.SelectedSoldiers + " soldiers   " : "") + (armour > 0 ? armour + (armour == 1 ? " vehicle" : " vehicles") : "") + (Player.Rts.AirliftNext ? "     AIRLIFT: right-click where they're to go" : ""), _small, Amber);
                Text(new Rect(20, h - 34, 1300, 24), "WASD / edge pan   Q E or MMB turn   Wheel zoom   LMB pick (drag for a box)   RMB move   B airlift   X let them decide   ` all   F take over   Esc menu", _small, Parch);
            }
            _menu.Draw();
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
            if (Player.HurtFlash > 0f && Player.InTankNow) Fill(new Rect(0, 0, w, h), new Color(0.35f, 0.02f, 0f, Player.HurtFlash * 0.4f));
            var tank = Player.MyTank;
            // who's being looked at
            if (_hoverFrame != Time.frameCount) { _hoverFrame = Time.frameCount; _hover = Player.Hovered(90f); }
            int over = _hover;
            if (over >= 0)
            {
                var o = Sim.Slots[over];
                Text(new Rect(cx - 200, h * 0.5f + 40, 400, 26), o.Class.ToString(), _mid, Side(o.Team));
            }
            // the squad
            float ry = h * 0.5f - Player.Roster.Count * 12f - 30f;
            Fill(new Rect(14, ry - 6, 232, Player.Roster.Count * 24 + 36), new Color(0.07f, 0.06f, 0.05f, 0.6f));
            Text(new Rect(24, ry, 220, 22), "You   " + Sim.Slots[slot].Class, _small, Amber);
            ry += 26;
            foreach (var (ms, mg, mc) in Player.Roster)
            {
                bool alive = Sim.Slots[ms].Alive && Sim.Slots[ms].Gen == mg;
                var col = alive ? Parch : new Color(0.5f, 0.22f, 0.16f);
                Text(new Rect(24, ry, 200, 22), mc.ToString(), _small, col);
                Text(new Rect(206, ry, 36, 22), alive ? "" : "x", _small, alive ? Amber : col);
                if (!alive) Fill(new Rect(24, ry + 11, 170, 1), col);
                ry += 24;
            }
            if (tank != null) { TankHud(tank, w, h, cx); return; }
            if (Player.NearTank >= 0) Text(new Rect(cx - 200, h * 0.56f, 400, 26), "E  board tank", _mid, Amber);
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
            var able = Sim.EntityManager.GetComponentData<Ability>(e);
            if (Player.Progress >= 0f)
            {
                Fill(new Rect(cx - 110, h * 0.62f, 220, 14), new Color(0.07f, 0.06f, 0.05f, 0.8f));
                Fill(new Rect(cx - 108, h * 0.62f + 2, 216 * Player.Progress, 10), Amber);
            }
            if (able.Maxim >= 0 && able.Maxim < Sim.Maxims.Length)
            {
                var mg = Sim.Maxims[able.Maxim];
                Fill(new Rect(w - 240, h - 66, 200, 18), new Color(0.07f, 0.06f, 0.05f, 0.72f));
                Fill(new Rect(w - 238, h - 64, 196 * (mg.Reloading > 0f ? 1f - mg.Reloading / Maxim.Reload : mg.Ammo / (float)Maxim.Clip), 14), mg.Reloading > 0f ? Parch : Amber);
                Text(new Rect(w - 240, h - 94, 200, 24), mg.Reloading > 0f ? "reloading" : "Maxim  " + mg.Ammo, _small, Parch);
                Text(new Rect(20, h - 34, 900, 24), "Mouse swing   LMB fire   R reload   E leave gun" + (Player.Class == SoldierClass.Gunner && !able.Carrying ? "   X pack it up" : ""), _small, Parch);
                return;
            }
            if (Player.NearMaxim >= 0) Text(new Rect(cx - 200, h * 0.56f, 400, 26), "E  use Maxim" + (Player.Class == SoldierClass.Gunner && !able.Carrying ? "     X  pack it up" : ""), _mid, Amber);
            if (Player.Class == SoldierClass.Sweeper)
            {
                int left = Sim.EntityManager.GetComponentData<Ability>(e).Grenades;
                for (int i = 0; i < left; i++) Fill(new Rect(w - 120 + i * 22, h - 112, 14, 14), new Color(0.55f, 0.6f, 0.35f));
            }
            else if (Player.Class == SoldierClass.Launcher)
            {
                bool ready = able.Cooldown <= 0f;
                Fill(new Rect(w - 222, h - 120, 200, 10), new Color(0.07f, 0.06f, 0.05f, 0.72f));
                Fill(new Rect(w - 220, h - 118, 196 * Mathf.Clamp01(1f - able.Cooldown / Mathf.Max(1f, tuning.missileCooldown)), 6), ready ? Amber : Parch);
                Text(new Rect(w - 222, h - 144, 200, 22), ready ? "missile ready" : $"reloading  {able.Cooldown:0}s", _small, ready ? Amber : Parch);
                int lockOn = ready ? Player.LockOn() : -1;
                int air = lockOn - Sim.Tanks.Count;
                if (lockOn >= 0 && air < Sim.Flights.Count && Project(Sim.Flights[air].Pos, out var lk))
                {
                    Fill(new Rect(lk.x - 18, lk.y - 18, 36, 2), Amber); Fill(new Rect(lk.x - 18, lk.y + 16, 36, 2), Amber);
                    Fill(new Rect(lk.x - 18, lk.y - 18, 2, 36), Amber); Fill(new Rect(lk.x + 16, lk.y - 18, 2, 36), Amber);
                    Text(new Rect(lk.x - 60, lk.y + 20, 120, 22), "LOCKED  E", _small, Amber);
                }
            }
            else if (Player.Class == SoldierClass.Communicator)
                Text(new Rect(w - 220, h - 116, 196, 24), Sim.IsCalling(slot) ? "guns firing" : Player.CanCall ? "guns ready  (E)" : "no guns free", _small, Player.CanCall ? Amber : Parch);
            Text(new Rect(20, h - 34, 900, 24), "WASD move   Shift run   C kneel   RMB aim   R reload   " + (Player.Class == SoldierClass.Sweeper ? "E grenade   " : Player.Class == SoldierClass.Launcher ? "E missile (follows aircraft, flies straight at anything else)   " : Player.Class == SoldierClass.Communicator ? "E artillery   " : Player.Class == SoldierClass.Gunner && able.Carrying ? "E set up Maxim (kneel: low, stand: high)   " : "") + "G squad " + (q.PlayerHold ? "holding" : "following") + "   Q leave", _small, Parch);
        }
    }

    /// <summary>
    /// Command line, for measuring: -lsbs-bench 500,1000,2000 (a side) -lsbs-out results.txt; -lsbs-shots folder takes pictures.
    /// </summary>
    public static class Bench
    {
        public static bool HideHud;
        public static uint NextSeed;
        public static bool Asked => _counts != null || _shots != null;
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
            b.StartNow(GameSetup.Even(2, _counts[_at] / Squad.Size, b.tuning));
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
                case 3: Close(0, SoldierClass.Launcher, false, -1f); break;
                case 4: Close(1, SoldierClass.Infantry, true, 1f); break;
                case 5: Close(1, SoldierClass.Launcher, false, -1f); break;
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
