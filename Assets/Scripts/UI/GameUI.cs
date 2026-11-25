using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Gunbound
{
    /// <summary>Main menu, settings, pause menu and HUD — all built from code.</summary>
    public class GameUI : MonoBehaviour
    {
        enum UiScreen { Menu, Game }
        static readonly Color MenuBackground = new(0.11f, 0.115f, 0.13f, 1f); // solid dark grey

        Canvas _canvas;
        RectTransform _menu, _settings, _pause, _hud;
        Text _status;
        UiScreen _screen = UiScreen.Menu;
        bool _settingsOpen, _paused;

        // HUD
        Image[] _ammoSegs;
        Image _reloadRing, _damageFlash, _hitmarker;
        Text _ammoText, _reloadText, _deathText;
        Image _healthFill, _healthTrail;
        // health ring animation (0..1): shown = the white fill, trail = the damage/heal segment beside it
        float _hpShown = 1f, _hpTrail = 1f, _hpTrailHold, _hpLastTarget = 1f;
        bool _hpHealing, _wasDead = true;
        Text _bloodlustText;
        float _copyT;
        string _copyName;
        Outline _ammoOutline;
        RectTransform _deathPanel;
        Text _scorePopup, _myScore;
        RectTransform _feed;
        readonly List<(CanvasGroup group, float born)> _feedEntries = new();
        const int FeedMax = 6;
        const float FeedLife = 6f;
        float _scorePopupT;
        CanvasGroup _scorePopupGroup;
        float _hitmarkerT, _damageT;
        Color _hitmarkerColor = Color.white;
        float _lastHealth = 100f;

        // loadouts
        LoadoutMenu _loadout;
        Text _weaponText, _deathPrompt;

        // rebinding
        GameAction? _rebinding;
        int _rebindFrame;
        readonly Dictionary<GameAction, Text> _bindLabels = new();

        Sprite _ring;

        // names & leaderboard
        readonly List<InputField> _nameFields = new();
        RectTransform _board;
        Text _boardTitle;
        readonly List<Text[]> _boardRows = new();
        float _boardRefresh;

        void Awake()
        {
            _ring = MakeRingSprite(256, 0.78f);
            BuildCanvas();
            BuildMenu();
            BuildSettings();
            BuildPause();
            BuildHud();
            BuildLeaderboard();
            _loadout = new LoadoutMenu(_canvas.transform, RefreshVisibility);
            GameSettings.NameChanged += () => { foreach (var f in _nameFields) if (!f.isFocused) f.text = GameSettings.PlayerName; };
            SessionManager.Status += s => { if (_status) _status.text = s; };
            SessionManager.Left += () => SetScreen(UiScreen.Menu);
            PlayerController.LocalSpawned += _ => SetScreen(UiScreen.Game);
            PlayerController.LocalDespawned += _ => { if (SessionManager.Session == null) SetScreen(UiScreen.Menu); };
            WeaponController.LocalHitConfirmed += (self, killed) =>
            {
                _hitmarkerT = killed ? 1.6f : 1f; // kill marker lingers a bit longer
                _hitmarkerColor = killed ? new Color(1f, 0.15f, 0.12f) : Color.white;
                _hitmarker.transform.parent.localScale = Vector3.one * (killed ? 1.35f : 1f);
            };
            Combatant.KillFeed += AddFeedEntry;
            WeaponController.WeaponTaken += id => { _copyName = WeaponCatalog.Get(id).displayName.ToUpperInvariant(); _copyT = 2.5f; };
            PlayerController.LocalScored += (victim, pts, victimColor) =>
            {
                _scorePopup.text = $"+{pts}   <size=22>ELIMINATED <color=#{PlayerPalette.HexString(victimColor)}>{victim.ToUpperInvariant()}</color></size>";
                _scorePopupT = 1f;
            };
            SetScreen(UiScreen.Menu);
        }

        // ------------------------------------------------------------------ flow

        void SetScreen(UiScreen s)
        {
            _screen = s;
            _paused = false;
            _settingsOpen = false;
            _rebinding = null;
            _loadout?.Close();
            RefreshVisibility();
        }

        void RefreshVisibility()
        {
            if (_loadout == null) return; // still building
            bool game = _screen == UiScreen.Game;
            _menu.gameObject.SetActive(!game && !_settingsOpen);
            _hud.gameObject.SetActive(game);
            _pause.gameObject.SetActive(game && _paused && !_settingsOpen);
            _settings.gameObject.SetActive(_settingsOpen);
            // loadouts can be edited in game (B / pause menu) or from the main menu
            _loadout.SetShown(!_settingsOpen && !(game && _paused));
            _loadout.SetMusicVisible(game);
            // settings opened from the main menu get the solid menu background; in-game it's see-through
            _settings.GetComponent<Image>().color = game ? new Color(0, 0, 0, 0.6f) : MenuBackground;
            bool cursorFree = !game || _paused || _settingsOpen || _loadout.IsOpen;
            Cursor.lockState = cursorFree ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = cursorFree;
            InputMap.Blocked = cursorFree;
        }

        void Update()
        {
            if (_rebinding.HasValue) { PollRebind(); return; }

            if (InputMap.EscapeDown)
            {
                if (_settingsOpen) { _settingsOpen = false; RefreshVisibility(); }
                else if (!_paused && _loadout.IsOpen) _loadout.Back();
                else if (_screen == UiScreen.Game) { _paused = !_paused; RefreshVisibility(); }
            }
            // loadout menu (B): works alive or dead, not over the pause/settings menus or while typing a name
            if (_screen == UiScreen.Game && !_paused && !_settingsOpen && !LoadoutMenu.IsTyping &&
                InputMap.DownRaw(GameAction.Loadout))
                _loadout.Toggle();
            // clicking back into the game re-locks the cursor (e.g. after alt-tab)
            if (_screen == UiScreen.Game && !_paused && !_settingsOpen && !_loadout.IsOpen && Cursor.lockState != CursorLockMode.Locked &&
                Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                RefreshVisibility();

            if (_screen == UiScreen.Game) UpdateHud();
            UpdateLeaderboard();
        }

        // ------------------------------------------------------------------ build

        void BuildCanvas()
        {
            var go = new GameObject("GameUI Canvas", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 10;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
        }

        RectTransform FullPanel(string name, Color c)
        {
            var img = UIFactory.Image(name, _canvas.transform, c);
            return UIFactory.Stretch(img.rectTransform);
        }

        void BuildMenu()
        {
            _menu = FullPanel("MainMenu", MenuBackground);
            var title = UIFactory.Text("Title", _menu, "GUNBOUND", 120, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            UIFactory.Place(title.rectTransform, new Vector2(0.5f, 0.72f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000, 160));
            var sub = UIFactory.Text("Sub", _menu, "DEV TEST", 28, TextAnchor.MiddleCenter, UIFactory.Accent, FontStyle.Bold);
            UIFactory.Place(sub.rectTransform, new Vector2(0.5f, 0.72f), new Vector2(0.5f, 0.5f), new Vector2(0, -90), new Vector2(600, 40));

            var col = UIFactory.Rect("Buttons", _menu);
            UIFactory.Place(col, new Vector2(0.5f, 0.42f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(360, 240));
            UIFactory.VList(col, 16);
            UIFactory.Size(UIFactory.Button("Play", col, "PLAY", () => SessionManager.Play(), 34), 72);
            UIFactory.Size(UIFactory.Button("Loadout", col, "LOADOUT", () => _loadout.Open(), 30), 60);
            UIFactory.Size(UIFactory.Button("Settings", col, "SETTINGS", OpenSettings, 30), 60);

            _status = UIFactory.Text("Status", _menu, "", 26, TextAnchor.MiddleCenter, UIFactory.Accent);
            UIFactory.Place(_status.rectTransform, new Vector2(0.5f, 0.22f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1200, 40));

            // name (top right)
            var nameBox = UIFactory.Rect("Name", _menu);
            UIFactory.Place(nameBox, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-30, -30), new Vector2(360, 90));
            var nl = UIFactory.Text("Label", nameBox, "YOUR NAME", 18, TextAnchor.UpperLeft, UIFactory.Dim, FontStyle.Bold);
            UIFactory.Place(nl.rectTransform, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, new Vector2(340, 24));
            var nf = NameField(nameBox);
            UIFactory.Place((RectTransform)nf.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, -28), new Vector2(360, 54));
        }

        InputField NameField(Transform parent)
        {
            var f = UIFactory.Input(parent, "Enter a name...", GameSettings.PlayerName, 16, v => GameSettings.SetPlayerName(v));
            _nameFields.Add(f);
            return f;
        }

        // ------------------------------------------------------------------ leaderboard (hold Tab)

        void BuildLeaderboard()
        {
            var bg = UIFactory.Image("Leaderboard", _canvas.transform, new Color(0.04f, 0.05f, 0.07f, 0.88f));
            _board = bg.rectTransform;
            int rows = SessionManager.MaxPlayers + 2; // + a header per team in Team Deathmatch
            UIFactory.Place(_board, new Vector2(0.5f, 0.55f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(880, 120 + 40 * rows));
            _boardTitle = UIFactory.Text("Title", _board, "DEV TEST", 34, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
            UIFactory.Place(_boardTitle.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(28, -14), new Vector2(500, 50));

            Text[] Row(int i, Color c, FontStyle style, int size)
            {
                float y = -70 - i * 40;
                var name = UIFactory.Text("Name", _board, "", size, TextAnchor.MiddleLeft, c, style);
                UIFactory.Place(name.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(28, y), new Vector2(460, 38));
                var pts = UIFactory.Text("Points", _board, "", size, TextAnchor.MiddleCenter, c, FontStyle.Bold);
                UIFactory.Place(pts.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(500, y), new Vector2(110, 38));
                var k = UIFactory.Text("Kills", _board, "", size, TextAnchor.MiddleCenter, c, style);
                UIFactory.Place(k.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(620, y), new Vector2(110, 38));
                var d = UIFactory.Text("Deaths", _board, "", size, TextAnchor.MiddleCenter, c, style);
                UIFactory.Place(d.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(740, y), new Vector2(110, 38));
                return new[] { name, pts, k, d };
            }
            var header = Row(0, UIFactory.Dim, FontStyle.Bold, 18);
            header[0].text = "PLAYER"; header[1].text = "POINTS"; header[2].text = "KILLS"; header[3].text = "DEATHS";
            for (int i = 0; i < rows; i++) _boardRows.Add(Row(i + 1, Color.white, FontStyle.Normal, 24));
            _board.gameObject.SetActive(false);
        }

        void UpdateLeaderboard()
        {
            bool show = _screen == UiScreen.Game && !_paused && !_settingsOpen && !_loadout.IsOpen &&
                        Keyboard.current != null && Keyboard.current.tabKey.isPressed;
            if (_board.gameObject.activeSelf != show) { _board.gameObject.SetActive(show); _boardRefresh = 0; }
            if (!show || (_boardRefresh -= Time.unscaledDeltaTime) > 0) return;
            _boardRefresh = 0.25f;

            var players = new List<PlayerController>(PlayerController.All);
            players.RemoveAll(p => p == null || !p.IsSpawned);
            players.Sort((a, b) => b.Score.Value != a.Score.Value ? b.Score.Value.CompareTo(a.Score.Value) : a.Deaths.Value.CompareTo(b.Deaths.Value));
            _boardTitle.text = $"{GameRules.ModeName}  <size=22><color=#9aa0aa>{players.Count}/{SessionManager.MaxPlayers} players</color></size>";
            _boardTitle.supportRichText = true;

            int r = 0;
            void PlayerRow(PlayerController p)
            {
                if (r >= _boardRows.Count) return;
                var row = _boardRows[r++];
                Color c = p.IsOwner ? UIFactory.Accent : Color.white;
                row[0].supportRichText = true;
                row[0].fontStyle = FontStyle.Normal;
                row[0].text = $"<color=#{p.ColorHex}>\u25A0</color>  {p.DisplayName}";
                row[1].text = p.Score.Value.ToString();
                row[2].text = p.Kills.Value.ToString();
                row[3].text = p.Deaths.Value.ToString();
                foreach (var t in row) { t.enabled = true; t.color = c; }
            }
            void HeaderRow(string text, Color c)
            {
                if (r >= _boardRows.Count) return;
                var row = _boardRows[r++];
                row[0].fontStyle = FontStyle.Bold;
                row[0].text = text;
                row[1].text = row[2].text = row[3].text = "";
                foreach (var t in row) { t.enabled = true; t.color = c; }
            }

            if (MatchManager.IsTeamMode)
            {
                // Team Deathmatch: your team in blue, the other in red
                var me = PlayerController.Local;
                byte mine = me != null ? me.Team.Value : (byte)0;
                var s = MatchManager.Current;
                for (byte team = 1; team <= 2; team++)
                {
                    Color tc = mine == 0 ? Color.white : team == mine ? PlayerOutlines.TeamColor : PlayerOutlines.EnemyColor;
                    HeaderRow($"TEAM {team}   {(team == 1 ? s.team1 : s.team2)} KILLS", tc);
                    foreach (var p in players) if (p.Team.Value == team) PlayerRow(p);
                }
                foreach (var p in players) if (p.Team.Value != 1 && p.Team.Value != 2) PlayerRow(p);
            }
            else
                foreach (var p in players) PlayerRow(p);

            for (; r < _boardRows.Count; r++)
                foreach (var t in _boardRows[r]) t.enabled = false;
        }

        void OpenSettings()
        {
            _settingsOpen = true;
            RefreshVisibility();
        }

        void BuildPause()
        {
            _pause = FullPanel("Pause", new Color(0, 0, 0, 0.55f));
            var col = UIFactory.Rect("Buttons", _pause);
            UIFactory.Place(col, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(380, 490));
            UIFactory.VList(col, 14);
            var t = UIFactory.Text("Paused", col, "PAUSED", 48, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            UIFactory.Size(t, 70);
            UIFactory.Size(UIFactory.Button("Resume", col, "RESUME", () => { _paused = false; RefreshVisibility(); }), 56);
            UIFactory.Size(UIFactory.Button("Loadout", col, "LOADOUT", () => { _paused = false; _loadout.Open(); }), 56);
            UIFactory.Size(UIFactory.Button("Settings", col, "SETTINGS", OpenSettings), 56);
            var nl = UIFactory.Text("NameLabel", col, "YOUR NAME", 18, TextAnchor.LowerLeft, UIFactory.Dim, FontStyle.Bold);
            UIFactory.Size(nl, 26);
            UIFactory.Size(NameField(col), 50);
            UIFactory.Size(UIFactory.Button("MainMenu", col, "BACK TO MAIN MENU", () => SessionManager.Leave()), 56);
        }

        void BuildSettings()
        {
            _settings = FullPanel("Settings", new Color(0, 0, 0, 0.6f));
            var win = UIFactory.Image("Window", _settings, UIFactory.PanelCol).rectTransform;
            UIFactory.Place(win, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(900, 760));

            var title = UIFactory.Text("Title", win, "SETTINGS", 40, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
            UIFactory.Place(title.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(30, -20), new Vector2(400, 50));

            var tabs = UIFactory.Rect("Tabs", win);
            UIFactory.Place(tabs, new Vector2(0, 1), new Vector2(0, 1), new Vector2(30, -85), new Vector2(840, 50));
            var h = tabs.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 10; h.childControlWidth = true; h.childForceExpandWidth = true; h.childControlHeight = true;

            var pages = new List<RectTransform>();
            var tabButtons = new List<Button>();
            RectTransform Page(string name)
            {
                var p = UIFactory.Rect(name, win);
                UIFactory.Place(p, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -150), new Vector2(840, 520));
                pages.Add(p);
                return UIFactory.ScrollList(p, 6); // scrolls when a page outgrows the window
            }
            void Show(int i)
            {
                for (int k = 0; k < pages.Count; k++)
                {
                    pages[k].gameObject.SetActive(k == i);
                    UIFactory.Label(tabButtons[k]).color = k == i ? UIFactory.Accent : UIFactory.TextCol;
                }
            }
            string[] names = { "VIDEO", "AUDIO", "CONTROLS" };
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                tabButtons.Add(UIFactory.Button(names[i], tabs, names[i], () => Show(idx), 24));
            }

            // VIDEO
            var video = Page("Video");
            UIFactory.Cycler(video, "Resolution",
                () => Array.ConvertAll(GameSettings.Resolutions, r => $"{r.width} x {r.height}"),
                () => GameSettings.ResolutionIndex < 0 ? 0 : GameSettings.ResolutionIndex,
                i => { GameSettings.ResolutionIndex = i; GameSettings.Apply(); });
            UIFactory.Toggle(video, "Fullscreen", () => GameSettings.Fullscreen, v => { GameSettings.Fullscreen = v; GameSettings.Apply(); });
            UIFactory.Cycler(video, "Quality", () => QualitySettings.names,
                () => Mathf.Max(0, GameSettings.Quality), i => { GameSettings.Quality = i; GameSettings.Apply(false); });
            UIFactory.Toggle(video, "VSync", () => GameSettings.VSync, v => { GameSettings.VSync = v; GameSettings.Apply(false); });
            UIFactory.Cycler(video, "Max FPS",
                () => Array.ConvertAll(GameSettings.MaxFpsOptions, f => f <= 0 ? "Unlimited" : f.ToString()),
                () => GameSettings.MaxFpsIndex, i => { GameSettings.MaxFpsIndex = i; GameSettings.Apply(false); });
            UIFactory.Slider(video, "Field of View", 70, 120, GameSettings.Fov, v => { GameSettings.Fov = Mathf.Round(v); GameSettings.Apply(false); }, v => Mathf.Round(v).ToString());

            // AUDIO
            var audio = Page("Audio");
            UIFactory.Slider(audio, "Master Volume", 0, 1, GameSettings.MasterVolume, v => { GameSettings.MasterVolume = v; GameSettings.Apply(false); }, Pct);
            UIFactory.Slider(audio, "Effects Volume", 0, 1, GameSettings.SfxVolume, v => { GameSettings.SfxVolume = v; GameSettings.Apply(false); }, Pct);
            UIFactory.Slider(audio, "Music Volume", 0, GameSettings.MaxMusicVolume, GameSettings.MusicVolume, v => { GameSettings.MusicVolume = v; GameSettings.Apply(false); }, Pct);

            // CONTROLS
            var controls = Page("Controls");
            UIFactory.Slider(controls, "Mouse Sensitivity", 0.1f, 10f, GameSettings.Sensitivity, v => { GameSettings.Sensitivity = v; GameSettings.Apply(false); }, v => v.ToString("0.00"));
            UIFactory.Toggle(controls, "Invert Y", () => GameSettings.InvertY, v => { GameSettings.InvertY = v; GameSettings.Apply(false); });
            foreach (GameAction a in Enum.GetValues(typeof(GameAction)))
            {
                var act = a;
                UIFactory.Row(controls, Nice(a), out var area);
                var b = UIFactory.Button("Bind", area, "", () => BeginRebind(act), 22);
                UIFactory.Stretch((RectTransform)b.transform);
                _bindLabels[a] = UIFactory.Label(b);
            }
            UIFactory.Size(UIFactory.Button("ResetBinds", controls, "Reset keys to default", () => { GameSettings.ResetBinds(); RefreshBinds(); }, 22), 40);
            RefreshBinds();

            var back = UIFactory.Button("Back", win, "BACK", () => { _settingsOpen = false; RefreshVisibility(); }, 26);
            UIFactory.Place((RectTransform)back.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-30, 25), new Vector2(200, 54));
            var hint = UIFactory.Text("Hint", win, "Fire: Left Mouse   ·   Pause: Esc", 18, TextAnchor.MiddleLeft, UIFactory.Dim);
            UIFactory.Place(hint.rectTransform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(30, 40), new Vector2(500, 30));

            Show(0);
        }

        static string Pct(float v) => Mathf.RoundToInt(v * 100) + "%";

        static string Nice(GameAction a) => a switch
        {
            GameAction.Forward => "Move Forward",
            GameAction.Back => "Move Back",
            GameAction.Left => "Move Left",
            GameAction.Right => "Move Right",
            GameAction.Slide => "Slide / Crouch",
            GameAction.SlideAlt => "Slide (alt)",
            GameAction.Grapple => "Grapple",
            GameAction.Loadout => "Loadout Menu",
            GameAction.Primary => "Primary Weapon",
            GameAction.Secondary => "Secondary Weapon",
            _ => a.ToString(),
        };

        void RefreshBinds()
        {
            foreach (var kv in _bindLabels)
                kv.Value.text = _rebinding == kv.Key ? "Press a key or mouse button... (Esc to cancel)" : GameSettings.Binds[kv.Key].DisplayName;
        }

        void BeginRebind(GameAction a)
        {
            _rebinding = a;
            _rebindFrame = Time.frameCount; // ignore the click that opened it
            RefreshBinds();
        }

        void PollRebind()
        {
            if (Time.frameCount == _rebindFrame) return;
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) { _rebinding = null; RefreshBinds(); return; }

            KeyBind? pressed = null;
            if (kb != null)
                foreach (var k in kb.allKeys)
                    if (k != null && k.wasPressedThisFrame) { pressed = k.keyCode; break; }
            var m = Mouse.current;
            if (pressed == null && m != null)
            {
                if (m.leftButton.wasPressedThisFrame) pressed = KeyBind.Mouse(KeyBind.MouseButton.Left);
                else if (m.rightButton.wasPressedThisFrame) pressed = KeyBind.Mouse(KeyBind.MouseButton.Right);
                else if (m.middleButton.wasPressedThisFrame) pressed = KeyBind.Mouse(KeyBind.MouseButton.Middle);
                else if (m.backButton.wasPressedThisFrame) pressed = KeyBind.Mouse(KeyBind.MouseButton.Back);
                else if (m.forwardButton.wasPressedThisFrame) pressed = KeyBind.Mouse(KeyBind.MouseButton.Forward);
            }
            if (pressed == null) return;

            GameSettings.Binds[_rebinding.Value] = pressed.Value;
            _rebinding = null;
            GameSettings.Apply(false);
            RefreshBinds();
        }

        // ------------------------------------------------------------------ HUD

        void BuildHud()
        {
            _hud = UIFactory.Stretch(UIFactory.Rect("HUD", _canvas.transform));

            _damageFlash = UIFactory.Image("DamageFlash", _hud, new Color(0.8f, 0, 0, 0));
            UIFactory.Stretch(_damageFlash.rectTransform);
            _damageFlash.raycastTarget = false;

            // crosshair
            var ch = UIFactory.Rect("Crosshair", _hud);
            UIFactory.Place(ch, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(40, 40));
            UIFactory.Place(UIFactory.Image("Dot", ch, Color.white).rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4, 4));
            foreach (var d in new[] { Vector2.up, Vector2.down, Vector2.left, Vector2.right })
            {
                var line = UIFactory.Image("Line", ch, new Color(1, 1, 1, 0.85f)).rectTransform;
                bool vert = d.x == 0;
                UIFactory.Place(line, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), d * 11f, vert ? new Vector2(2, 8) : new Vector2(8, 2));
            }

            // hitmarker
            var hm = UIFactory.Rect("Hitmarker", _hud);
            UIFactory.Place(hm, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(40, 40));
            hm.localRotation = Quaternion.Euler(0, 0, 45);
            _hitmarker = UIFactory.Image("HM", hm, new Color(1, 1, 1, 0));
            UIFactory.Stretch(_hitmarker.rectTransform);
            _hitmarker.enabled = false;
            foreach (var d in new[] { Vector2.up, Vector2.down, Vector2.left, Vector2.right })
            {
                bool vert = d.x == 0;
                var line = UIFactory.Image("L", hm, Color.white);
                UIFactory.Place(line.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), d * 14f, vert ? new Vector2(3, 12) : new Vector2(12, 3));
            }

            // ammo ring (bottom left)
            var ring = UIFactory.Rect("AmmoRing", _hud);
            UIFactory.Place(ring, new Vector2(0, 0), new Vector2(0.5f, 0.5f), new Vector2(130, 130), new Vector2(170, 170));
            UIFactory.Stretch(UIFactory.Image("Back", ring, new Color(0, 0, 0, 0.35f), _ring).rectTransform);
            _ammoRing = ring;
            BuildAmmoSegments(GunDefinition.Fallback.magazineSize);
            // current weapon, centred just above the ring
            _weaponText = UIFactory.Text("Weapon", _hud, "", 24, TextAnchor.LowerCenter, Color.white, FontStyle.Bold);
            var wo = _weaponText.gameObject.AddComponent<Outline>();
            wo.effectColor = new Color(0, 0, 0, 0.8f);
            wo.effectDistance = new Vector2(1.5f, -1.5f);
            _weaponText.supportRichText = false;
            UIFactory.Place(_weaponText.rectTransform, new Vector2(0, 0), new Vector2(0.5f, 0), new Vector2(130, 238), new Vector2(400, 32));
            _reloadRing = UIFactory.Image("Reload", ring, Color.white, _ring);
            UIFactory.Stretch(_reloadRing.rectTransform, 14, 14, 14, 14);
            _reloadRing.type = Image.Type.Filled;
            _reloadRing.fillMethod = Image.FillMethod.Radial360;
            _reloadRing.fillOrigin = (int)Image.Origin360.Top;
            _reloadRing.fillClockwise = true;
            _ammoText = UIFactory.Text("Ammo", ring, "8", 54, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            UIFactory.Stretch(_ammoText.rectTransform); // dead centre of the ring
            _ammoOutline = _ammoText.gameObject.AddComponent<Outline>(); // white outline when low (orange)
            _ammoOutline.effectColor = Color.white;
            _ammoOutline.effectDistance = new Vector2(2f, -2f);
            _ammoOutline.enabled = false;
            // "[R] Reload" prompt, bottom left next to the ring, only when the mag is empty
            _reloadText = UIFactory.Text("ReloadPrompt", _hud, "", 28, TextAnchor.MiddleLeft, UIFactory.Accent, FontStyle.Bold);
            UIFactory.Place(_reloadText.rectTransform, new Vector2(0, 0), new Vector2(0, 0.5f), new Vector2(252, 130), new Vector2(320, 40));
            WhiteOutline(_reloadText, 2f);

            // health: thin ring around the ammo circle
            var thin = MakeRingSprite(256, 0.88f);
            var hp = UIFactory.Rect("HealthRing", _hud);
            UIFactory.Place(hp, new Vector2(0, 0), new Vector2(0.5f, 0.5f), new Vector2(130, 130), new Vector2(204, 204));
            hp.SetAsFirstSibling();
            UIFactory.Stretch(UIFactory.Image("Back", hp, new Color(0, 0, 0, 0.35f), thin).rectTransform);
            // trail: red chunk that lingers after damage, green chunk ahead of the fill while healing
            _healthTrail = UIFactory.Image("Trail", hp, Color.white, thin);
            UIFactory.Stretch(_healthTrail.rectTransform);
            _healthTrail.type = Image.Type.Filled;
            _healthTrail.fillMethod = Image.FillMethod.Radial360;
            _healthTrail.fillOrigin = (int)Image.Origin360.Top;
            _healthTrail.fillClockwise = true;
            _healthFill = UIFactory.Image("Fill", hp, Color.white, thin);
            UIFactory.Stretch(_healthFill.rectTransform);
            _healthFill.type = Image.Type.Filled;
            _healthFill.fillMethod = Image.FillMethod.Radial360;
            _healthFill.fillOrigin = (int)Image.Origin360.Top;
            _healthFill.fillClockwise = true;

            // Bloodlust / Hyper timers, right of the ring above the reload prompt (grows upward)
            _bloodlustText = UIFactory.Text("PerkTimers", _hud, "", 26, TextAnchor.LowerLeft, Color.white, FontStyle.Bold);
            _bloodlustText.supportRichText = true;
            UIFactory.Place(_bloodlustText.rectTransform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(252, 164), new Vector2(360, 72));
            var blo = _bloodlustText.gameObject.AddComponent<Outline>();
            blo.effectColor = new Color(0, 0, 0, 0.85f);
            blo.effectDistance = new Vector2(2f, -2f);

            // kill popup + own score
            _scorePopup = UIFactory.Text("ScorePopup", _hud, "", 40, TextAnchor.MiddleCenter, UIFactory.Accent, FontStyle.Bold);
            _scorePopup.supportRichText = true;
            UIFactory.Place(_scorePopup.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -110), new Vector2(900, 60));
            // fade the whole popup (rich-text colours ignore the Text alpha, which left the name stuck on screen)
            _scorePopupGroup = _scorePopup.gameObject.AddComponent<CanvasGroup>();
            _scorePopupGroup.alpha = 0f;
            _scorePopupGroup.blocksRaycasts = false;
            _myScore = UIFactory.Text("MyScore", _hud, "", 26, TextAnchor.UpperRight, Color.white, FontStyle.Bold);
            var so = _myScore.gameObject.AddComponent<Outline>();
            so.effectColor = Color.black;
            so.effectDistance = new Vector2(2f, -2f);
            UIFactory.Place(_myScore.rectTransform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-30, -24), new Vector2(500, 40));

            // kill feed (top right, under the points)
            _feed = UIFactory.Rect("KillFeed", _hud);
            UIFactory.Place(_feed, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-30, -72), new Vector2(700, 400));
            var fv = _feed.gameObject.AddComponent<VerticalLayoutGroup>();
            fv.childAlignment = TextAnchor.UpperRight;
            fv.spacing = 6;
            fv.childControlWidth = fv.childControlHeight = false;
            fv.childForceExpandWidth = fv.childForceExpandHeight = false;

            // death
            _deathPanel = UIFactory.Rect("Death", _hud);
            UIFactory.Place(_deathPanel, new Vector2(0.5f, 0.7f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000, 240));
            var dt = UIFactory.Text("Title", _deathPanel, "ELIMINATED", 90, TextAnchor.MiddleCenter, new Color(1f, 0.3f, 0.25f), FontStyle.Bold);
            UIFactory.Place(dt.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), Vector2.zero, new Vector2(1000, 110));
            WhiteOutline(dt, 3f);
            // "Respawn in 2.1s" → "[SPACE] to respawn", then "[B] to select loadout" underneath
            _deathText = UIFactory.Text("Timer", _deathPanel, "", 42, TextAnchor.MiddleCenter, UIFactory.Accent, FontStyle.Bold);
            UIFactory.Place(_deathText.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -122), new Vector2(1000, 54));
            WhiteOutline(_deathText, 2f);
            _deathPrompt = UIFactory.Text("Loadout", _deathPanel, "", 34, TextAnchor.MiddleCenter, UIFactory.Accent, FontStyle.Bold);
            UIFactory.Place(_deathPrompt.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -182), new Vector2(1000, 46));
            WhiteOutline(_deathPrompt, 2f);
            _deathPanel.gameObject.SetActive(false);

            BuildSongToast();
            BuildMatchHud();
        }

        // ------------------------------------------------------------------ rounds (top centre + round over)

        Text _matchTimer, _hillBanner, _roundTitle, _roundWinner, _roundNext, _roundCount;
        RectTransform _roundPanel;

        void BuildMatchHud()
        {
            Text Label(string name, Transform parent, int size, Vector2 anchor, Vector2 pos, Vector2 box)
            {
                var t = UIFactory.Text(name, parent, "", size, TextAnchor.UpperCenter, Color.white, FontStyle.Bold);
                t.supportRichText = true;
                t.raycastTarget = false;
                UIFactory.Place(t.rectTransform, anchor, new Vector2(0.5f, 1), pos, box);
                var o = t.gameObject.AddComponent<Outline>();
                o.effectColor = new Color(0, 0, 0, 0.85f);
                o.effectDistance = new Vector2(2f, -2f);
                return t;
            }
            // mode + clock (and the team score in Team Deathmatch), then who holds the hill
            _matchTimer = Label("MatchTimer", _hud, 30, new Vector2(0.5f, 1), new Vector2(0, -18), new Vector2(1000, 84));
            _hillBanner = Label("HillBanner", _hud, 30, new Vector2(0.5f, 1), new Vector2(0, -104), new Vector2(1000, 44));

            _roundPanel = UIFactory.Rect("RoundOver", _hud);
            UIFactory.Place(_roundPanel, new Vector2(0.5f, 0.66f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1400, 300));
            _roundTitle = Label("Title", _roundPanel, 72, new Vector2(0.5f, 1), Vector2.zero, new Vector2(1400, 90));
            _roundWinner = Label("Winner", _roundPanel, 50, new Vector2(0.5f, 1), new Vector2(0, -96), new Vector2(1400, 66));
            _roundNext = Label("Next", _roundPanel, 30, new Vector2(0.5f, 1), new Vector2(0, -172), new Vector2(1400, 44));
            _roundCount = Label("Count", _roundPanel, 30, new Vector2(0.5f, 1), new Vector2(0, -218), new Vector2(1400, 44));
            _roundTitle.text = "ROUND OVER";
            _roundNext.color = _roundCount.color = UIFactory.Accent;
            _roundPanel.gameObject.SetActive(false);
        }

        static string Clock(float seconds)
        {
            int s = Mathf.CeilToInt(Mathf.Max(0f, seconds));
            return $"{s / 60}:{s % 60:00}";
        }

        static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        void UpdateMatchHud()
        {
            bool running = MatchManager.Running;
            var s = MatchManager.Current;
            var me = PlayerController.Local;
            bool playing = running && s.phase == MatchPhase.Playing;

            // top centre: mode and time left
            _matchTimer.gameObject.SetActive(playing);
            if (playing)
            {
                float left = MatchManager.TimeLeft;
                string line = $"{MatchManager.ModeName(s.mode)}   <color=#{(left <= 30f ? "ff5a4a" : "ffffff")}>{Clock(left)}</color>";
                if (s.mode == GameMode.TeamDeathmatch)
                {
                    byte mine = me != null ? me.Team.Value : (byte)0;
                    string c1 = mine == 0 ? "ffffff" : Hex(mine == 1 ? PlayerOutlines.TeamColor : PlayerOutlines.EnemyColor);
                    string c2 = mine == 0 ? "ffffff" : Hex(mine == 2 ? PlayerOutlines.TeamColor : PlayerOutlines.EnemyColor);
                    line += $"\n<size=26><color=#{c1}>TEAM 1  {s.team1}</color>   <color=#9aa0aa>-</color>   <color=#{c2}>{s.team2}  TEAM 2</color></size>";
                }
                _matchTimer.text = line;
            }

            // who holds the hill (nothing when nobody does)
            string hill = "";
            if (playing && s.mode == GameMode.KingOfTheHill && s.hillOwner != MatchManager.Nobody)
            {
                var owner = PlayerController.FindByClient(s.hillOwner);
                if (owner != null)
                    hill = owner == me ? $"<color=#{Hex(UIFactory.Accent)}>YOU HOLD THE HILL</color>"
                                       : $"<color=#{owner.ColorHex}>{owner.DisplayName.ToUpperInvariant()}</color> HOLDS THE HILL";
            }
            _hillBanner.text = hill;
            _hillBanner.gameObject.SetActive(hill.Length > 0);

            // between rounds: winner, what's next, countdown
            bool between = running && MatchManager.InTransition;
            _roundPanel.gameObject.SetActive(between && !_loadout.IsOpen);
            if (!between) return;
            string winner;
            if (string.IsNullOrEmpty(s.winner)) winner = "DRAW";
            else if (s.endedMode == GameMode.TeamDeathmatch)
            {
                byte mine = me != null ? me.Team.Value : (byte)0;
                Color c = mine == 0 ? Color.white : mine == s.winnerTeam ? PlayerOutlines.TeamColor : PlayerOutlines.EnemyColor;
                winner = $"<color=#{Hex(c)}>{s.winner}</color> WINS   <size=34><color=#9aa0aa>{s.team1} - {s.team2}</color></size>";
            }
            else if (me != null && s.winner == me.DisplayName) winner = $"<color=#{Hex(UIFactory.Accent)}>YOU WIN</color>";
            else winner = $"<color=#{(s.winnerColor >= 0 ? PlayerPalette.HexString(s.winnerColor) : "ffffff")}>{s.winner.ToUpperInvariant()}</color> WINS";
            _roundWinner.text = winner;
            string map = MatchManager.MapName(s.map);
            _roundNext.text = $"NEXT:  {MatchManager.ModeName(s.mode)}" + (map.Length > 0 ? $"  ·  {map}" : "");
            _roundCount.text = $"Next round in {Mathf.CeilToInt(MatchManager.TimeLeft)}";
        }

        static void WhiteOutline(Text t, float px)
        {
            var o = t.gameObject.AddComponent<Outline>();
            o.effectColor = Color.white;
            o.effectDistance = new Vector2(px, -px);
        }

        // ------------------------------------------------------------------ song toast (bottom right)

        const float ToastLife = 7f;
        const int ToastMaxTitle = 38;
        CanvasGroup _toast;
        Text _toastTitle, _toastBy;
        float _toastT;
        int _toastPlayId;

        void BuildSongToast()
        {
            var bg = UIFactory.Image("SongToast", _hud, new Color(0.04f, 0.05f, 0.07f, 0.85f));
            var r = bg.rectTransform;
            UIFactory.Place(r, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-30, 30), new Vector2(460, 112));
            bg.raycastTarget = false;
            var stripe = UIFactory.Image("Stripe", r, UIFactory.Accent);
            stripe.raycastTarget = false;
            UIFactory.Place(stripe.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), Vector2.zero, new Vector2(5, 112));
            var label = UIFactory.Text("Label", r, "NEXT SONG:", 18, TextAnchor.UpperLeft, UIFactory.Accent, FontStyle.Bold);
            UIFactory.Place(label.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -10), new Vector2(420, 24));
            _toastTitle = UIFactory.Text("Title", r, "", 26, TextAnchor.UpperLeft, Color.white, FontStyle.Bold);
            _toastTitle.supportRichText = false;
            UIFactory.Place(_toastTitle.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -36), new Vector2(425, 34));
            _toastBy = UIFactory.Text("By", r, "", 19, TextAnchor.UpperLeft, UIFactory.Dim);
            _toastBy.supportRichText = true;
            UIFactory.Place(_toastBy.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -76), new Vector2(425, 26));
            _toast = bg.gameObject.AddComponent<CanvasGroup>();
            _toast.alpha = 0f;
            _toast.blocksRaycasts = false;
            MusicNet.StateChanged += OnSongState;
        }

        void OnSongState()
        {
            if (this == null) return;
            var c = MusicNet.Current;
            if (!c.Active || c.playId == _toastPlayId) return;
            _toastPlayId = c.playId;
            if (MusicNet.SongTime > 3.0) return; // joined mid-song: it's not "next" any more
            string title = c.title ?? "";
            _toastTitle.text = title.Length > ToastMaxTitle ? title.Substring(0, ToastMaxTitle - 1).TrimEnd() + "…" : title;
            string hex = "FFFFFF";
            foreach (var p in PlayerController.All)
                if (p != null && p.IsSpawned && p.OwnerClientId == c.owner) { hex = p.ColorHex; break; }
            _toastBy.text = MusicNet.IsMine ? "queued by you" : $"queued by <color=#{hex}>{c.ownerName}</color>";
            _toastT = ToastLife;
        }

        void OnDestroy() => MusicNet.StateChanged -= OnSongState;

        void UpdateSongToast(float dt)
        {
            _toastT = Mathf.Max(0f, _toastT - dt);
            // quick fade in, fade out over the last second
            _toast.alpha = Mathf.Clamp01(_toastT) * Mathf.Clamp01((ToastLife - _toastT) / 0.25f);
        }

        RectTransform _ammoRing;

        /// <summary>(Re)builds the ring segments — one per round, so it follows the equipped gun's magazine.</summary>
        void BuildAmmoSegments(int mag)
        {
            if (_ammoSegs != null)
                foreach (var s in _ammoSegs) if (s) Destroy(s.gameObject);
            mag = Mathf.Max(1, mag);
            float gap = mag > 16 ? 0.006f : 0.012f;
            _ammoSegs = new Image[mag];
            for (int i = 0; i < mag; i++)
            {
                var seg = UIFactory.Image("Seg" + i, _ammoRing, Color.white, _ring);
                UIFactory.Stretch(seg.rectTransform);
                seg.transform.SetSiblingIndex(1 + i); // above the backing, below the reload ring / number
                seg.type = Image.Type.Filled;
                seg.fillMethod = Image.FillMethod.Radial360;
                seg.fillOrigin = (int)Image.Origin360.Top;
                seg.fillClockwise = true;
                seg.fillAmount = 1f / mag - gap;
                seg.rectTransform.localRotation = Quaternion.Euler(0, 0, -360f / mag * i - gap * 50f);
                _ammoSegs[i] = seg;
            }
        }

        void UpdateHud()
        {
            UpdateMatchHud();
            var pc = PlayerController.Local;
            float dt = Time.deltaTime;
            if (pc == null || !pc.IsSpawned) return;

            var w = pc.Weapon;
            int mag = w.Definition.magazineSize;
            if (_ammoSegs.Length != mag) BuildAmmoSegments(mag);
            // round-by-round reloads keep showing the rounds as they go in (and the gun can still fire)
            bool perRound = w.Definition.reloadPerRound;
            bool hideAmmo = w.IsReloading && !perRound;
            for (int i = 0; i < _ammoSegs.Length; i++)
            {
                bool full = i < w.Ammo && !hideAmmo;
                _ammoSegs[i].color = full ? Color.white : new Color(1, 1, 1, 0.12f);
            }
            _weaponText.text = w.Current.displayName.ToUpperInvariant();
            _reloadRing.enabled = w.IsReloading;
            _reloadRing.fillAmount = w.ReloadProgress;
            _ammoText.text = hideAmmo ? "" : w.Ammo.ToString();
            bool low = w.Ammo <= Mathf.Max(2, mag / 5);
            if (w.Current.Melee)
            {
                // melee has no ammo: a single full ring, no number, no low-ammo warning
                _ammoText.text = "";
                low = false;
            }
            _ammoText.color = low ? UIFactory.Accent : Color.white;
            _ammoOutline.enabled = low;
            _reloadText.text = !w.IsReloading && w.Ammo == 0 ? $"{InputMap.Prompt(GameAction.Reload)} Reload" : "";

            float hp = pc.Health.Value;
            if (hp < _lastHealth - 0.01f) _damageT = 1f;
            _lastHealth = hp;
            UpdateHealthRing(pc, dt);

            // on-kill perk timers (pop briefly when triggered)
            float bl = pc.BloodlustRemaining, hy = pc.HyperRemaining;
            string timers = "";
            if (bl > 0f) timers += $"<color=#ff3a2e>BLOODLUST  {bl:0.0}s</color>";
            if (hy > 0f) timers += (timers.Length > 0 ? "\n" : "") + $"<color=#c073ff>HYPER  {hy:0.0}s</color>";
            _copyT = Mathf.Max(0f, _copyT - dt);
            if (_copyT > 0f && !pc.Dead.Value) timers += (timers.Length > 0 ? "\n" : "") + $"<color=#8cf266>COPIED  {_copyName}</color>";
            int col = pc.Collected.Value;
            if (col > 0 && !pc.Dead.Value)
                timers += (timers.Length > 0 ? "\n" : "") +
                          $"<color=#ff8cbf>COLLECTOR  +{Perks.CollectorHealthBonus(col):0} HP  +{Mathf.RoundToInt(Mathf.Min(Perks.CollectorSpeed * col, Perks.MaxSpeedMultiplier - 1f) * 100)}% SPD</color>";
            _bloodlustText.text = timers;
            float pop = Mathf.Max(Mathf.Max(0f, bl - (Perks.BloodlustDuration - 0.25f)), Mathf.Max(0f, hy - (Perks.HyperDuration - 0.25f)));
            _bloodlustText.rectTransform.localScale = Vector3.one * (1f + pop * 1.2f);

            _damageT = Mathf.MoveTowards(_damageT, 0, dt * 2.5f);
            _damageFlash.color = new Color(0.8f, 0, 0, _damageT * 0.35f);

            _hitmarkerT = Mathf.MoveTowards(_hitmarkerT, 0, dt * 4f);
            var hmRoot = _hitmarker.transform.parent;
            foreach (Transform c in hmRoot)
            {
                var img = c.GetComponent<Image>();
                if (img != null && img != _hitmarker) img.color = new Color(_hitmarkerColor.r, _hitmarkerColor.g, _hitmarkerColor.b, Mathf.Clamp01(_hitmarkerT));
            }


            _scorePopupT = Mathf.MoveTowards(_scorePopupT, 0f, dt / 2.2f);
            _scorePopupGroup.alpha = Mathf.Clamp01(_scorePopupT * 3f);
            if (_scorePopupT <= 0f && _scorePopup.text.Length > 0) _scorePopup.text = "";
            _scorePopup.rectTransform.localScale = Vector3.one * (1f + Mathf.Max(0f, _scorePopupT - 0.85f) * 2f);
            _myScore.text = $"POINTS  {pc.Score.Value}";
            UpdateFeed();
            UpdateSongToast(dt);

            bool dead = pc.Dead.Value;
            // between rounds the round-over panel takes its place (respawns wait for the next map)
            _deathPanel.gameObject.SetActive(dead && !_loadout.IsOpen && !MatchManager.InTransition);
            if (dead)
            {
                float wait = pc.RespawnWait;
                _deathText.text = wait > 0f
                    ? $"Respawn in {wait:0.0}s"
                    : $"{InputMap.Prompt(PlayerController.RespawnAction)} to respawn";
                _deathPrompt.text = $"{InputMap.Prompt(GameAction.Loadout)} to select loadout";
            }
        }

        /// <summary>
        /// Health ring eases to the real value instead of jumping. Damage: the fill slides down and a red chunk
        /// lingers, then drains after it. Healing (e.g. Lifesteal): a green chunk shows where you're heading and the
        /// fill slides up into it.
        /// </summary>
        void UpdateHealthRing(PlayerController pc, float dt)
        {
            float target = Mathf.Clamp01(pc.Health.Value / Mathf.Max(1f, pc.MaxHealth));
            bool dead = pc.Dead.Value;
            if (dead || _wasDead)
            {
                // death / respawn: no animation, just show it
                _hpShown = _hpTrail = target;
                _hpHealing = false;
                _wasDead = dead;
            }
            else
            {
                if (target < _hpLastTarget - 0.0005f)
                {
                    // took damage: the trail holds where the fill was, then drains
                    if (_hpHealing) _hpTrail = _hpShown;
                    _hpHealing = false;
                    _hpTrailHold = 0.45f;
                }
                if (target > _hpShown + 0.0005f)
                {
                    _hpHealing = true;
                    _hpTrail = target; // heal preview leads the fill
                }

                _hpShown = Mathf.Lerp(_hpShown, target, 1f - Mathf.Exp(-(_hpHealing ? 6f : 14f) * dt));
                if (Mathf.Abs(_hpShown - target) < 0.0005f) _hpShown = target;

                if (_hpHealing)
                {
                    _hpTrail = Mathf.Max(target, _hpShown);
                    if (_hpShown >= target) _hpHealing = false;
                }
                else
                {
                    _hpTrailHold -= dt;
                    if (_hpTrailHold <= 0f) _hpTrail = Mathf.Lerp(_hpTrail, _hpShown, 1f - Mathf.Exp(-5f * dt));
                    if (_hpTrail < _hpShown) _hpTrail = _hpShown;
                }
            }

            _hpLastTarget = target;
            float shownHp = _hpShown * pc.MaxHealth;
            _healthFill.fillAmount = _hpShown;
            _healthFill.color = shownHp > 50 ? Color.white : Color.Lerp(new Color(1f, 0.25f, 0.2f), UIFactory.Accent, shownHp / 50f);
            _healthTrail.fillAmount = _hpTrail;
            _healthTrail.enabled = _hpTrail > _hpShown + 0.001f;
            _healthTrail.color = _hpHealing ? new Color(0.35f, 1f, 0.45f, 0.9f) : new Color(1f, 0.2f, 0.15f, 0.9f);
        }

        void AddFeedEntry(string killer, int killerColor, string victim, int victimColor, bool headshot, bool self, DeathCause cause)
        {
            if (_feed == null) return;
            string Col(int c) => c >= 0 ? PlayerPalette.HexString(c) : "C8CCD2";
            var me = PlayerController.Local;
            string myName = me != null ? me.DisplayName : null;
            bool involvesMe = myName != null && (killer == myName || victim == myName);

            var bg = UIFactory.Image("Entry", _feed, involvesMe ? new Color(0.25f, 0.06f, 0.05f, 0.75f) : new Color(0f, 0f, 0f, 0.55f));
            bg.rectTransform.pivot = new Vector2(1, 1);
            var h = bg.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.padding = new RectOffset(12, 12, 5, 5);
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = h.childForceExpandHeight = false;
            var fit = bg.gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var group = bg.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;

            h.spacing = 10;
            h.childAlignment = TextAnchor.MiddleCenter;

            Text Part(string s)
            {
                var t = UIFactory.Text("Text", bg.transform, s, 22, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
                t.supportRichText = true;
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
                return t;
            }
            void Icon(Color c)
            {
                var img = UIFactory.Image("Crosshair", bg.transform, c, CrosshairSprite);
                img.preserveAspect = true;
                var le = img.gameObject.AddComponent<LayoutElement>();
                le.preferredWidth = le.minWidth = 24;
                le.preferredHeight = le.minHeight = 24;
            }

            string v = $"<color=#{Col(victimColor)}>{victim}</color>";
            if (cause == DeathCause.Lava)
            {
                // "Killer forced Player to jump into lava" / "Player jumped into lava" (nobody hurt them this life)
                const string lava = "<color=#ff7a1a>lava</color>";
                if (!self && !string.IsNullOrEmpty(killer))
                {
                    Part($"<color=#{Col(killerColor)}>{killer}</color>");
                    Part("forced");
                    Part(v);
                    Part("to jump into " + lava);
                }
                else
                {
                    Part(v);
                    Part("jumped into " + lava);
                }
            }
            else if (cause == DeathCause.Crushed)
            {
                Part(v);
                Part("got <color=#c9a26b>crushed</color>");
            }
            else
            {
            if (self) { killer = victim; killerColor = victimColor; } // shows as "Name [x] Name"
            if (string.IsNullOrEmpty(killer))
            {
                Icon(new Color(0.75f, 0.77f, 0.8f));
                Part(v);
            }
            else
            {
                Part($"<color=#{Col(killerColor)}>{killer}</color>");
                Icon(headshot ? new Color(1f, 0.29f, 0.24f) : Color.white);
                if (headshot) Part("<color=#ff4a3d>HEADSHOT</color>");
                Part(v);
            }
            }

            bg.transform.SetAsFirstSibling(); // newest on top
            _feedEntries.Insert(0, (group, Time.time));
            while (_feedEntries.Count > FeedMax)
            {
                var old = _feedEntries[^1];
                _feedEntries.RemoveAt(_feedEntries.Count - 1);
                if (old.group) Destroy(old.group.gameObject);
            }
        }

        void UpdateFeed()
        {
            for (int i = _feedEntries.Count - 1; i >= 0; i--)
            {
                var (g, born) = _feedEntries[i];
                float age = Time.time - born;
                if (g == null || age > FeedLife)
                {
                    if (g) Destroy(g.gameObject);
                    _feedEntries.RemoveAt(i);
                    continue;
                }
                g.alpha = Mathf.Clamp01((FeedLife - age) / 0.6f) * Mathf.Clamp01(age / 0.12f);
            }
        }

        static Sprite _crosshair;

        /// <summary>Crosshair icon: ring, four ticks and a centre dot.</summary>
        static Sprite CrosshairSprite
        {
            get
            {
                if (_crosshair != null) return _crosshair;
                const int size = 64;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[size * size];
                float aa = 2.5f / size;
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float ring = Mathf.Clamp01((0.09f - Mathf.Abs(d - 0.62f)) / aa);
                    float ax = Mathf.Abs(dx), ay = Mathf.Abs(dy);
                    float tickV = Mathf.Clamp01((0.075f - ax) / aa) * (ay > 0.32f && ay < 0.98f ? 1f : 0f);
                    float tickH = Mathf.Clamp01((0.075f - ay) / aa) * (ax > 0.32f && ax < 0.98f ? 1f : 0f);
                    float dot = Mathf.Clamp01((0.11f - d) / aa);
                    float a = Mathf.Max(Mathf.Max(ring, dot), Mathf.Max(tickV, tickH));
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
                tex.SetPixels32(px);
                tex.Apply();
                _crosshair = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
                return _crosshair;
            }
        }

        static Sprite MakeRingSprite(int size, float inner)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            float aa = 2f / size;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01((1f - d) / aa) * Mathf.Clamp01((d - inner) / aa);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
