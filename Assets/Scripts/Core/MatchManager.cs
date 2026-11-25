using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Gunbound
{
    public enum GameMode : byte { FreeForAll = 0, TeamDeathmatch = 1, KingOfTheHill = 2 }
    public enum MatchPhase : byte { None = 0, Playing = 1, Sinking = 2, Rising = 3 }

    /// <summary>
    /// Rounds, game modes and the map rotation, over NGO named messages (no NetworkObject needed).
    ///
    /// A round lasts roundDuration. When it ends the winner is shown, the map sinks into the ground (sinkSeconds),
    /// then a different random map rises (riseSeconds) and the next round starts with a random mode that isn't
    /// the one just played. Respawns are paused and nothing scores during the transition; anything a map lands
    /// on or lifts into a ceiling is crushed.
    ///
    /// Map instances exist on every peer; only the state (map, mode, phase, start time) is sent, and every client
    /// moves the maps itself in step with the server clock.
    /// </summary>
    [AddComponentMenu("Gunbound/Match Manager")]
    [DefaultExecutionOrder(-200)] // maps move before players do
    public class MatchManager : MonoBehaviour
    {
        const string MsgState = "gb_match_state";
        const string MsgHello = "gb_match_hello";
        public const ulong Nobody = ulong.MaxValue;
        /// <summary>Maps move in steps no bigger than this (metres) so thin floors can't skip past a player.</summary>
        const float MaxStep = 0.2f;

        [Tooltip("Map prefabs in the rotation (Gunbound > Setup Map Rotation fills this in). The next map is never the current one.")]
        public List<GameObject> maps = new();
        [Tooltip("Length of a round in seconds.")]
        public float roundDuration = 300f;
        [Tooltip("Seconds for the old map to sink into the ground.")]
        public float sinkSeconds = 5f;
        [Tooltip("Seconds for the next map to rise.")]
        public float riseSeconds = 5f;

        [Header("King of the Hill")]
        [Tooltip("Seconds standing on the point alone to capture it.")]
        public float hillCaptureSeconds = 1f;
        [Tooltip("Points per second for whoever holds the point.")]
        public int hillPointsPerSecond = 1;
        [Tooltip("Points per kill in King of the Hill.")]
        public int hillKillPoints = 2;

        public struct State
        {
            public int round;
            /// <summary>Mode being played (during a transition: the mode of the round about to start).</summary>
            public GameMode mode;
            /// <summary>Mode of the round that just ended (transition only).</summary>
            public GameMode endedMode;
            /// <summary>Map in play (during a transition: the next map). -1 = none.</summary>
            public sbyte map;
            /// <summary>The map sinking during a transition. -1 = none.</summary>
            public sbyte prevMap;
            public MatchPhase phase;
            public double phaseStart;
            public int team1, team2;
            /// <summary>Winner of the round that just ended ("" = draw).</summary>
            public string winner;
            public sbyte winnerColor;
            public byte winnerTeam;
            public ulong hillOwner;
            /// <summary>Who is capturing the hill right now (Nobody = no one), and since when (server time).</summary>
            public ulong hillCapturer;
            public double captureStart;
        }

        public static MatchManager Instance { get; private set; }
        public static State Current { get; private set; }
        public static event Action StateChanged;

        public static bool Running => Instance != null && Current.phase != MatchPhase.None;
        public static double ServerNow => NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0;
        public static bool InTransition => Current.phase == MatchPhase.Sinking || Current.phase == MatchPhase.Rising;
        /// <summary>Kills, deaths and points only count while a round is being played.</summary>
        public static bool ScoringOpen => !Running || Current.phase == MatchPhase.Playing;
        public static bool RespawnsAllowed => !InTransition;
        /// <summary>The mode the HUD / scoreboard / outlines describe: the round in play, or the one that just ended.</summary>
        public static GameMode ShownMode => !Running ? GameMode.FreeForAll : InTransition ? Current.endedMode : Current.mode;
        public static bool IsTeamMode => Running && ShownMode == GameMode.TeamDeathmatch;
        public static int PointsPerKill => Running && Current.mode == GameMode.KingOfTheHill && Instance != null ? Instance.hillKillPoints : GameRules.PointsPerKill;

        /// <summary>The map whose spawn points are used (the next map during a transition).</summary>
        public static GameMap CurrentMap => Instance != null ? Instance.MapAt(Current.map) : null;
        public static GameMap PreviousMap => Instance != null ? Instance.MapAt(Current.prevMap) : null;

        public static bool AreTeammates(Combatant a, Combatant b) =>
            IsTeamMode && a is PlayerController pa && b is PlayerController pb && pa != pb &&
            pa.Team.Value != 0 && pa.Team.Value == pb.Team.Value;

        /// <summary>Seconds until the current phase (round, or whole transition) ends.</summary>
        public static float TimeLeft
        {
            get
            {
                var i = Instance;
                if (i == null || !Running) return 0f;
                float t = (float)(ServerNow - Current.phaseStart);
                return Current.phase switch
                {
                    MatchPhase.Playing => Mathf.Max(0f, i.roundDuration - t),
                    MatchPhase.Sinking => Mathf.Max(0f, i.sinkSeconds + i.riseSeconds - t),
                    MatchPhase.Rising => Mathf.Max(0f, i.riseSeconds - t),
                    _ => 0f,
                };
            }
        }

        public static string ModeName(GameMode m) => m switch
        {
            GameMode.TeamDeathmatch => "TEAM DEATHMATCH",
            GameMode.KingOfTheHill => "KING OF THE HILL",
            _ => "FREE FOR ALL",
        };

        public static string MapName(int index)
        {
            var m = Instance != null ? Instance.MapAt(index) : null;
            return m != null ? m.DisplayName : "";
        }

        readonly List<GameMap> _maps = new();
        Transform _mapRoot;
        bool _registered, _helloSent;
        State _server;
        ulong _capturer = Nobody;
        float _capTimer, _hillAccum;
        bool _crushReported;

        GameMap MapAt(int i) => i >= 0 && i < _maps.Count ? _maps[i] : null;

        void Awake()
        {
            Instance = this;
            Current = default;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ lifecycle

        void Update()
        {
            var nm = NetworkManager.Singleton;
            bool listening = nm != null && nm.IsListening && nm.CustomMessagingManager != null;
            if (listening && !_registered) Register(nm);
            else if (!listening && _registered) ResetAll();
            if (!_registered) return;

            if (nm.IsServer)
            {
                ServerEnsureStarted();
                ServerTick();
            }
            else if (nm.IsConnectedClient && !_helloSent)
            {
                _helloSent = true;
                using var w = new FastBufferWriter(8, Allocator.Temp);
                w.WriteValueSafe((byte)1);
                nm.CustomMessagingManager.SendNamedMessage(MsgHello, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
            }

            UpdateMaps();
            UpdateHillColor();
        }

        /// <summary>
        /// The hill takes the holder's colour. While someone captures it, it fills from the current colour
        /// (or its own, if nobody holds it) to the capturer's over the capture time.
        /// </summary>
        void UpdateHillColor()
        {
            var s = Current;
            var hill = MapAt(s.map)?.Hill;
            if (hill == null) return;
            var owner = s.hillOwner == Nobody ? null : PlayerController.FindByClient(s.hillOwner);
            Color? from = owner != null ? owner.PlayerColor : null;
            var capturer = s.phase == MatchPhase.Playing && s.hillCapturer != Nobody ? PlayerController.FindByClient(s.hillCapturer) : null;
            if (capturer != null)
            {
                float k = (float)(ServerNow - s.captureStart) / Mathf.Max(0.01f, hillCaptureSeconds);
                hill.SetTint(from, capturer.PlayerColor, k);
            }
            else hill.SetTint(from, from, 1f);
        }

        void Register(NetworkManager nm)
        {
            var cmm = nm.CustomMessagingManager;
            cmm.RegisterNamedMessageHandler(MsgState, OnStateMessage);
            cmm.RegisterNamedMessageHandler(MsgHello, OnHelloMessage);
            _registered = true;
            _helloSent = false;
            EnsureMaps();
        }

        void ResetAll()
        {
            _registered = false;
            _helloSent = false;
            _server = default;
            _capturer = Nobody;
            _capTimer = _hillAccum = 0f;
            foreach (var m in _maps) if (m != null) Destroy(m.gameObject);
            _maps.Clear();
            ApplyState(default);
        }

        /// <summary>One instance of every map prefab, all hidden until the state says otherwise.</summary>
        void EnsureMaps()
        {
            if (_maps.Count > 0) return;
            if (maps.Count == 0) Debug.LogError("[Gunbound] The Match Manager has no maps, so there's nothing to play on. Use Gunbound > Setup Map Rotation and save the scene.");
            if (_mapRoot == null)
            {
                _mapRoot = new GameObject("Maps").transform;
                _mapRoot.SetParent(transform, false);
            }
            for (int i = 0; i < maps.Count; i++)
            {
                var prefab = maps[i];
                if (prefab == null) continue;
                var go = Instantiate(prefab, prefab.transform.position, prefab.transform.rotation, _mapRoot);
                go.name = prefab.name;
                var gm = go.AddComponent<GameMap>();
                gm.Init(_maps.Count, prefab.name); // while active: bounds of inactive renderers come back empty
                gm.SetOffset(-gm.Depth);
                go.SetActive(false);
                _maps.Add(gm);
            }
        }

        // ------------------------------------------------------------------ messages

        void OnHelloMessage(ulong sender, FastBufferReader r)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            ServerEnsureStarted();
            Send(nm, sender);
        }

        void OnStateMessage(ulong sender, FastBufferReader r)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsServer || sender != NetworkManager.ServerClientId) return;
            try { ApplyState(Read(r)); }
            catch (Exception e) { Debug.LogWarning("[Match] bad state message: " + e.Message); }
        }

        void Broadcast()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            foreach (var c in nm.ConnectedClientsIds)
                if (c != nm.LocalClientId) Send(nm, c);
            ApplyState(_server);
        }

        void Send(NetworkManager nm, ulong client)
        {
            if (client == nm.LocalClientId) return;
            using var w = new FastBufferWriter(256, Allocator.Temp, 2048);
            var s = _server;
            w.WriteValueSafe(s.round);
            w.WriteValueSafe((byte)s.mode);
            w.WriteValueSafe((byte)s.endedMode);
            w.WriteValueSafe(s.map);
            w.WriteValueSafe(s.prevMap);
            w.WriteValueSafe((byte)s.phase);
            w.WriteValueSafe(s.phaseStart);
            w.WriteValueSafe(s.team1);
            w.WriteValueSafe(s.team2);
            w.WriteValueSafe(s.winner ?? "");
            w.WriteValueSafe(s.winnerColor);
            w.WriteValueSafe(s.winnerTeam);
            w.WriteValueSafe(s.hillOwner);
            w.WriteValueSafe(s.hillCapturer);
            w.WriteValueSafe(s.captureStart);
            nm.CustomMessagingManager.SendNamedMessage(MsgState, client, w, NetworkDelivery.ReliableSequenced);
        }

        static State Read(FastBufferReader r)
        {
            var s = new State();
            r.ReadValueSafe(out s.round);
            r.ReadValueSafe(out byte mode); s.mode = (GameMode)mode;
            r.ReadValueSafe(out byte ended); s.endedMode = (GameMode)ended;
            r.ReadValueSafe(out s.map);
            r.ReadValueSafe(out s.prevMap);
            r.ReadValueSafe(out byte phase); s.phase = (MatchPhase)phase;
            r.ReadValueSafe(out s.phaseStart);
            r.ReadValueSafe(out s.team1);
            r.ReadValueSafe(out s.team2);
            r.ReadValueSafe(out s.winner);
            r.ReadValueSafe(out s.winnerColor);
            r.ReadValueSafe(out s.winnerTeam);
            r.ReadValueSafe(out s.hillOwner);
            r.ReadValueSafe(out s.hillCapturer);
            r.ReadValueSafe(out s.captureStart);
            return s;
        }

        void ApplyState(State s)
        {
            bool newRound = s.phase == MatchPhase.Playing && (Current.phase != MatchPhase.Playing || Current.round != s.round);
            Current = s;
            GameRules.ModeName = ModeName(ShownMode);
            if (newRound) _crushReported = false;

            // hills: shown on the map in play when the round there is King of the Hill
            for (int i = 0; i < _maps.Count; i++)
            {
                bool hill = (i == s.map && s.mode == GameMode.KingOfTheHill) ||
                            (i == s.prevMap && s.prevMap != s.map && s.endedMode == GameMode.KingOfTheHill);
                _maps[i].SetHillShown(hill);
            }
            UpdateHillColor();
            UpdateMaps(); // show the map right away (the first player spawns on it this same frame)
            StateChanged?.Invoke();
        }

        // ------------------------------------------------------------------ server

        /// <summary>Server: make sure a round is running (the first one starts on a random map, no rise).</summary>
        public static void ServerEnsureStarted()
        {
            var i = Instance;
            var nm = NetworkManager.Singleton;
            if (i == null || nm == null || !nm.IsServer || !nm.IsListening) return;
            if (!i._registered) i.Register(nm);
            if (i._server.phase != MatchPhase.None) return;
            i._server = new State { round = 1, map = -1, prevMap = -1, hillOwner = Nobody, hillCapturer = Nobody, winner = "", winnerColor = -1 };
            if (i._maps.Count > 0) i._server.map = (sbyte)Random.Range(0, i._maps.Count);
            i._server.mode = i.PickMode(i._server.map, null);
            i.BeginRound();
        }

        void ServerTick()
        {
            if (_server.phase == MatchPhase.None) return;
            double now = ServerNow;
            float t = (float)(now - _server.phaseStart);
            switch (_server.phase)
            {
                case MatchPhase.Playing:
                    if (_server.mode == GameMode.KingOfTheHill) HillTick(Time.deltaTime);
                    if (t >= roundDuration) EndRound(now);
                    break;
                case MatchPhase.Sinking:
                    if (t >= sinkSeconds)
                    {
                        _server.phase = MatchPhase.Rising;
                        _server.phaseStart += sinkSeconds;
                        Broadcast();
                    }
                    break;
                case MatchPhase.Rising:
                    if (t >= riseSeconds) BeginRound();
                    break;
            }
        }

        /// <summary>Server: the next round starts now. Scores reset, teams are dealt, the dead respawn.</summary>
        void BeginRound()
        {
            _server.phase = MatchPhase.Playing;
            _server.phaseStart = ServerNow;
            _server.prevMap = -1;
            _server.team1 = _server.team2 = 0;
            _server.hillOwner = Nobody;
            _server.hillCapturer = Nobody;
            _capturer = Nobody;
            _capTimer = _hillAccum = 0f;

            var players = LivePlayers();
            foreach (var p in players)
            {
                p.Score.Value = 0;
                p.Kills.Value = 0;
                p.Deaths.Value = 0;
                p.Team.Value = 0;
            }
            if (_server.mode == GameMode.TeamDeathmatch)
            {
                // balanced random teams
                for (int i = players.Count - 1; i > 0; i--)
                {
                    int j = Random.Range(0, i + 1);
                    (players[i], players[j]) = (players[j], players[i]);
                }
                int first = Random.Range(1, 3);
                for (int i = 0; i < players.Count; i++) players[i].Team.Value = (byte)(i % 2 == 0 ? first : 3 - first);
            }

            Broadcast(); // before respawning, so spawns come from the new map
            foreach (var p in players) if (p.Dead.Value) p.ServerForceRespawn();
        }

        void EndRound(double now)
        {
            DecideWinner();
            _server.endedMode = _server.mode;
            sbyte next = PickMap(_server.map);
            _server.prevMap = _server.map;
            _server.map = next;
            _server.mode = PickMode(next, _server.endedMode);
            _server.hillOwner = Nobody;
            _server.hillCapturer = Nobody;
            _server.phase = MatchPhase.Sinking;
            _server.phaseStart = now;
            _server.round++;
            Broadcast();
        }

        void DecideWinner()
        {
            _server.winner = "";
            _server.winnerColor = -1;
            _server.winnerTeam = 0;
            if (_server.mode == GameMode.TeamDeathmatch)
            {
                if (_server.team1 != _server.team2)
                {
                    _server.winnerTeam = (byte)(_server.team1 > _server.team2 ? 1 : 2);
                    _server.winner = "TEAM " + _server.winnerTeam;
                }
                return;
            }
            // FFA: most kills. KOTH: most points. A tie (or nobody scoring) is a draw.
            bool koth = _server.mode == GameMode.KingOfTheHill;
            PlayerController best = null;
            int bestValue = 0;
            bool tie = false;
            foreach (var p in LivePlayers())
            {
                int v = koth ? p.Score.Value : p.Kills.Value;
                if (v > bestValue) { best = p; bestValue = v; tie = false; }
                else if (v == bestValue && v > 0) tie = true;
            }
            if (best == null || tie) return;
            _server.winner = best.DisplayName;
            _server.winnerColor = (sbyte)(best.ColorIndex.Value == 255 ? -1 : best.ColorIndex.Value);
        }

        sbyte PickMap(sbyte current)
        {
            if (_maps.Count == 0) return -1;
            if (_maps.Count == 1) return 0;
            int n = Random.Range(0, _maps.Count - 1);
            if (n >= current && current >= 0) n++; // never the same map twice in a row
            return (sbyte)n;
        }

        /// <summary>Team Deathmatch is out of the rotation for now (all its team code is kept; flip this to bring it back).</summary>
        static readonly bool TeamDeathmatchEnabled = false;

        GameMode PickMode(int map, GameMode? exclude)
        {
            var options = new List<GameMode> { GameMode.FreeForAll, GameMode.KingOfTheHill };
            if (TeamDeathmatchEnabled) options.Add(GameMode.TeamDeathmatch);
            var m = MapAt(map);
            if (m == null || m.Hill == null) options.Remove(GameMode.KingOfTheHill);
            if (exclude.HasValue && options.Count > 1) options.Remove(exclude.Value);
            return options[Random.Range(0, options.Count)];
        }

        static List<PlayerController> LivePlayers()
        {
            var list = new List<PlayerController>();
            foreach (var p in PlayerController.All) if (p != null && p.IsSpawned) list.Add(p);
            return list;
        }

        /// <summary>Server: a player joined. Mid-round in Team Deathmatch they go to the smaller team.</summary>
        public static void ServerOnPlayerSpawned(PlayerController pc)
        {
            var i = Instance;
            if (i == null || pc == null) return;
            ServerEnsureStarted();
            if (i._server.phase == MatchPhase.None) return;
            bool teams = i._server.phase == MatchPhase.Playing ? i._server.mode == GameMode.TeamDeathmatch : i._server.endedMode == GameMode.TeamDeathmatch;
            if (!teams) { pc.Team.Value = 0; return; }
            int t1 = 0, t2 = 0;
            foreach (var p in PlayerController.All)
            {
                if (p == null || p == pc || !p.IsSpawned) continue;
                if (p.Team.Value == 1) t1++;
                else if (p.Team.Value == 2) t2++;
            }
            pc.Team.Value = (byte)(t1 < t2 ? 1 : t2 < t1 ? 2 : Random.Range(1, 3));
        }

        /// <summary>Server: someone scored a kill (already credited to the player).</summary>
        public static void ServerOnKill(PlayerController killer)
        {
            var i = Instance;
            if (i == null || killer == null || i._server.phase != MatchPhase.Playing || i._server.mode != GameMode.TeamDeathmatch) return;
            if (killer.Team.Value == 1) i._server.team1++;
            else if (killer.Team.Value == 2) i._server.team2++;
            else return;
            i.Broadcast();
        }

        /// <summary>Server: is a crush report believable right now? (transition, or just after one)</summary>
        public static bool ServerCrushAllowed =>
            Instance != null && (InTransition || (Current.phase == MatchPhase.Playing && ServerNow - Current.phaseStart < 1.5));

        void HillTick(float dt)
        {
            var map = MapAt(_server.map);
            var hill = map != null ? map.Hill : null;
            if (hill == null) return;

            int count = 0;
            PlayerController only = null;
            foreach (var p in PlayerController.All)
            {
                if (p == null || !p.IsAlive || !hill.Contains(p)) continue;
                count++;
                only = p;
            }

            // capture: alone on the point for hillCaptureSeconds
            if (count == 1 && only.OwnerClientId != _server.hillOwner)
            {
                if (_capturer != only.OwnerClientId)
                {
                    _capturer = only.OwnerClientId;
                    _capTimer = 0f;
                    _server.hillCapturer = _capturer; // clients animate the colour fill
                    _server.captureStart = ServerNow;
                    Broadcast();
                }
                _capTimer += dt;
                if (_capTimer >= hillCaptureSeconds)
                {
                    _server.hillOwner = only.OwnerClientId;
                    _server.hillCapturer = Nobody;
                    _capturer = Nobody;
                    _capTimer = 0f;
                    _hillAccum = 0f;
                    Broadcast();
                }
            }
            else
            {
                _capturer = Nobody;
                _capTimer = 0f;
                if (_server.hillCapturer != Nobody)
                {
                    _server.hillCapturer = Nobody; // capture interrupted: colour snaps back
                    Broadcast();
                }
            }

            // whoever holds it earns points, on the point or not
            if (_server.hillOwner == Nobody) return;
            var holder = PlayerController.FindByClient(_server.hillOwner);
            if (holder == null)
            {
                _server.hillOwner = Nobody;
                Broadcast();
                return;
            }
            _hillAccum += dt;
            while (_hillAccum >= 1f)
            {
                _hillAccum -= 1f;
                holder.Score.Value += hillPointsPerSecond;
            }
        }

        // ------------------------------------------------------------------ map motion (every peer)

        static float Ease(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        void UpdateMaps()
        {
            var s = Current;
            float t = (float)(ServerNow - s.phaseStart);
            for (int i = 0; i < _maps.Count; i++)
            {
                var m = _maps[i];
                bool show = false;
                float target = 0f;
                switch (s.phase)
                {
                    case MatchPhase.Playing:
                        show = i == s.map;
                        break;
                    case MatchPhase.Sinking:
                        show = i == s.prevMap;
                        target = -m.Depth * Ease(t / Mathf.Max(0.01f, sinkSeconds));
                        break;
                    case MatchPhase.Rising:
                        show = i == s.map;
                        target = -m.Depth * (1f - Ease(t / Mathf.Max(0.01f, riseSeconds)));
                        break;
                }

                if (!show)
                {
                    if (m.gameObject.activeSelf) m.gameObject.SetActive(false);
                    continue;
                }
                if (!m.gameObject.activeSelf)
                {
                    // appearing (joined mid-transition, or the rise begins): just put it where it should be
                    m.SetOffset(target);
                    m.gameObject.SetActive(true);
                    Physics.SyncTransforms();
                    continue;
                }
                MoveMap(m, target);
            }
        }

        void MoveMap(GameMap m, float target)
        {
            float from = m.Offset, d = target - from;
            if (Mathf.Abs(d) < 1e-4f) return;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(d) / MaxStep));
            float step = d / steps;
            for (int k = 1; k <= steps; k++)
            {
                m.SetOffset(from + step * k);
                Physics.SyncTransforms();
                PushLocalPlayer(m, step);
            }
        }

        void PushLocalPlayer(GameMap m, float dy)
        {
            var pc = PlayerController.Local;
            if (_crushReported || pc == null || !pc.IsAlive || pc.Motor == null || !pc.Motor.enabled || pc.CC == null || !pc.CC.enabled) return;
            if (MapPush.Push(pc, m, dy))
            {
                _crushReported = true;
                pc.ReportCrushed();
            }
        }

        /// <summary>Owner: we respawned, crush reports are allowed again.</summary>
        public static void LocalRespawned()
        {
            if (Instance != null) Instance._crushReported = false;
        }
    }

    /// <summary>
    /// Moving map vs the local player: maps carry players standing on them, push them out of the way,
    /// and crush them when there's nowhere to go (a sinking roof pressing them into the floor,
    /// a rising floor lifting them into a ceiling).
    /// </summary>
    static class MapPush
    {
        static CapsuleCollider _probe;
        static readonly Collider[] Buf = new Collider[64];
        /// <summary>Overlap deeper than this (metres) after being pushed = crushed.</summary>
        const float CrushDepth = 0.12f;

        /// <returns>True when the player got crushed.</returns>
        public static bool Push(PlayerController pc, GameMap map, float dy)
        {
            var motor = pc.Motor;
            var cc = pc.CC;
            var t = pc.transform;
            Probe(cc);

            // standing on a sinking map: go down with it (stops on solid ground that isn't moving)
            if (dy < 0f && motor.IsGrounded && map.Owns(motor.GroundCollider) && motor.Velocity.y <= 0.5f)
                cc.Move(Vector3.up * dy);

            // anything of the map now inside us shoves us out (a rising floor lifts us, a sinking roof presses down)
            for (int it = 0; it < 4; it++)
            {
                if (!MapPenetration(cc, t, map, out var push)) break;
                t.position += push;
                Physics.SyncTransforms();
            }

            return Stuck(cc, t);
        }

        static void Probe(CharacterController cc)
        {
            if (_probe == null)
            {
                var go = new GameObject("MapPushProbe") { layer = Layers.Fx };
                go.transform.position = new Vector3(0, -10000, 0);
                UnityEngine.Object.DontDestroyOnLoad(go);
                _probe = go.AddComponent<CapsuleCollider>();
                _probe.isTrigger = true;
                _probe.direction = 1;
            }
            _probe.center = cc.center;
            _probe.radius = cc.radius;
            _probe.height = cc.height;
        }

        static void Capsule(CharacterController cc, Transform t, float shrink, out Vector3 a, out Vector3 b, out float r)
        {
            r = Mathf.Max(0.05f, cc.radius - shrink);
            float half = Mathf.Max(r, cc.height * 0.5f - shrink);
            Vector3 c = t.position + cc.center;
            a = c - Vector3.up * (half - r);
            b = c + Vector3.up * (half - r);
        }

        static bool MapPenetration(CharacterController cc, Transform t, GameMap map, out Vector3 push)
        {
            push = Vector3.zero;
            Capsule(cc, t, 0f, out var a, out var b, out float r);
            int n = Physics.OverlapCapsuleNonAlloc(a, b, r, Buf, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                var c = Buf[i];
                if (!map.Owns(c) || c.isTrigger) continue;
                if (!Physics.ComputePenetration(_probe, t.position, Quaternion.identity, c, c.transform.position, c.transform.rotation, out var dir, out float dist)) continue;
                if (dist < 0.001f) continue;
                var p = dir * (dist + 0.01f);
                if (p.sqrMagnitude > push.sqrMagnitude) push = p;
                any = true;
            }
            return any;
        }

        static bool Stuck(CharacterController cc, Transform t)
        {
            Capsule(cc, t, CrushDepth, out var a, out var b, out float r);
            int n = Physics.OverlapCapsuleNonAlloc(a, b, r, Buf, Layers.EnvironmentMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (Buf[i] != null && !Buf[i].isTrigger) return true;
            return false;
        }
    }
}
