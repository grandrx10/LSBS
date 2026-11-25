using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LSBS
{
    /// <summary>What the person has set, kept between games.</summary>
    public static class Options
    {
        public static float Master = 0.8f, Guns = 1f, Blasts = 1f, Sensitivity = 1f, Fov = 74f, Brightness = 0f, RenderScale = 1f, Detail = 45f;
        public static int Ragdolls = 140, Shadows = 2, Antialias = 1, FpsCap = 0, Window = 0, Width, Height;
        public static bool VSync, Grain = true, Bloom = true, Shake = true, Counter = true;

        public static void Load()
        {
            Master = PlayerPrefs.GetFloat("lsbs.master", Master); Guns = PlayerPrefs.GetFloat("lsbs.guns", Guns); Blasts = PlayerPrefs.GetFloat("lsbs.blasts", Blasts);
            Sensitivity = PlayerPrefs.GetFloat("lsbs.sens", Sensitivity); Fov = PlayerPrefs.GetFloat("lsbs.fov", Fov); Brightness = PlayerPrefs.GetFloat("lsbs.bright", Brightness);
            RenderScale = PlayerPrefs.GetFloat("lsbs.scale", RenderScale); Detail = PlayerPrefs.GetFloat("lsbs.detail", Detail);
            Ragdolls = PlayerPrefs.GetInt("lsbs.ragdolls", Ragdolls); Shadows = PlayerPrefs.GetInt("lsbs.shadows", Shadows); Antialias = PlayerPrefs.GetInt("lsbs.aa", Antialias); FpsCap = PlayerPrefs.GetInt("lsbs.cap", FpsCap);
            Window = PlayerPrefs.GetInt("lsbs.window", Screen.fullScreenMode == FullScreenMode.Windowed ? 2 : 1);
            Width = PlayerPrefs.GetInt("lsbs.w", 0); Height = PlayerPrefs.GetInt("lsbs.h", 0);
            VSync = PlayerPrefs.GetInt("lsbs.vsync", 0) == 1; Grain = PlayerPrefs.GetInt("lsbs.grain", 1) == 1; Bloom = PlayerPrefs.GetInt("lsbs.bloom", 1) == 1;
            Shake = PlayerPrefs.GetInt("lsbs.shake", 1) == 1; Counter = PlayerPrefs.GetInt("lsbs.counter", 1) == 1;
        }

        public static void Save()
        {
            PlayerPrefs.SetFloat("lsbs.master", Master); PlayerPrefs.SetFloat("lsbs.guns", Guns); PlayerPrefs.SetFloat("lsbs.blasts", Blasts);
            PlayerPrefs.SetFloat("lsbs.sens", Sensitivity); PlayerPrefs.SetFloat("lsbs.fov", Fov); PlayerPrefs.SetFloat("lsbs.bright", Brightness);
            PlayerPrefs.SetFloat("lsbs.scale", RenderScale); PlayerPrefs.SetFloat("lsbs.detail", Detail);
            PlayerPrefs.SetInt("lsbs.ragdolls", Ragdolls); PlayerPrefs.SetInt("lsbs.shadows", Shadows); PlayerPrefs.SetInt("lsbs.aa", Antialias); PlayerPrefs.SetInt("lsbs.cap", FpsCap);
            PlayerPrefs.SetInt("lsbs.window", Window); PlayerPrefs.SetInt("lsbs.w", Width); PlayerPrefs.SetInt("lsbs.h", Height);
            PlayerPrefs.SetInt("lsbs.vsync", VSync ? 1 : 0); PlayerPrefs.SetInt("lsbs.grain", Grain ? 1 : 0); PlayerPrefs.SetInt("lsbs.bloom", Bloom ? 1 : 0);
            PlayerPrefs.SetInt("lsbs.shake", Shake ? 1 : 0); PlayerPrefs.SetInt("lsbs.counter", Counter ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    /// <summary>The main menu, the pause menu (Esc) and the settings.</summary>
    public class GameMenu
    {
        public enum Page { None, Main, Pause, Settings }

        static readonly string[] ShadowNames = { "Off", "Near", "Far" }, AaNames = { "Off", "FXAA", "SMAA" }, WindowNames = { "Fullscreen", "Borderless", "Windowed" };
        static readonly int[] Caps = { 0, 60, 120, 144, 240 };

        readonly Battle _b;
        readonly Camera _cam;
        readonly UniversalRenderPipelineAsset _pipe;
        readonly VolumeProfile _look;
        readonly float _exposure;
        readonly List<Vector2Int> _sizes = new();
        int _size;
        Page _back = Page.Main;
        int _tab;
        public Page Now { get; private set; }
        public bool Open => Now != Page.None;
        int _green = 500, _grey = 500, _wave = 100;
        GUIStyle _title, _head, _text, _value, _button, _on, _field;

        public GameMenu(Battle b, Camera cam, UniversalRenderPipelineAsset pipe, VolumeProfile look)
        {
            _b = b; _cam = cam; _pipe = pipe; _look = look;
            if (look.TryGet<ColorAdjustments>(out var g)) _exposure = g.postExposure.value;
            foreach (var r in Screen.resolutions)
            {
                var s = new Vector2Int(r.width, r.height);
                if (!_sizes.Contains(s) && s.x >= 1024) _sizes.Add(s);
            }
            if (_sizes.Count == 0) _sizes.Add(new Vector2Int(Screen.width, Screen.height));
            _size = _sizes.IndexOf(new Vector2Int(Options.Width > 0 ? Options.Width : Screen.width, Options.Height > 0 ? Options.Height : Screen.height));
            if (_size < 0) _size = _sizes.Count - 1;
            _green = PlayerPrefs.GetInt("lsbs.green", 500);
            _grey = PlayerPrefs.GetInt("lsbs.grey", 500);
            _wave = PlayerPrefs.GetInt("lsbs.wave", 100);
            Apply();
            if (Options.Width > 0) ApplyWindow();
        }

        // ------------------------------------------------------------------ moving between pages

        public void Show(Page p)
        {
            Now = p;
            // the battle waits while the menu's up
            Time.timeScale = p == Page.Pause || p == Page.Settings && _back == Page.Pause ? 0f : 1f;
            AudioListener.pause = Time.timeScale == 0f;
            if (p == Page.None) Options.Save();
        }

        public void Update()
        {
            var kb = Keyboard.current;
            if (kb == null || !kb.escapeKey.wasPressedThisFrame) return;
            switch (Now)
            {
                case Page.None: Show(Page.Pause); break;
                case Page.Pause: Show(Page.None); break;
                case Page.Settings: Options.Save(); Show(_back); break;
            }
        }

        // ------------------------------------------------------------------ putting settings to work

        public void Apply()
        {
            AudioListener.volume = Options.Master;
            QualitySettings.vSyncCount = Options.VSync ? 1 : 0;
            Application.targetFrameRate = Options.FpsCap > 0 ? Options.FpsCap : -1;
            if (_pipe != null)
            {
                _pipe.renderScale = Options.RenderScale;
                _pipe.shadowDistance = Options.Shadows == 0 ? 0.01f : Options.Shadows == 1 ? 45f : 90f;
            }
            _b.SetRagdolls(Options.Ragdolls);
            _b.SetDetail(Options.Detail, Options.Shadows == 0 ? 0f : Options.Shadows == 1 ? 40f : 70f);
            var data = _cam.GetUniversalAdditionalCameraData();
            data.antialiasing = Options.Antialias == 0 ? AntialiasingMode.None : Options.Antialias == 1 ? AntialiasingMode.FastApproximateAntialiasing : AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            if (_look.TryGet<FilmGrain>(out var grain)) grain.active = Options.Grain;
            if (_look.TryGet<Bloom>(out var bloom)) bloom.active = Options.Bloom;
            if (_look.TryGet<ColorAdjustments>(out var grade)) grade.postExposure.Override(_exposure + Options.Brightness);
        }

        void ApplyWindow()
        {
            var s = _sizes[Mathf.Clamp(_size, 0, _sizes.Count - 1)];
            Options.Width = s.x; Options.Height = s.y;
            var mode = Options.Window == 0 ? FullScreenMode.ExclusiveFullScreen : Options.Window == 1 ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            Screen.SetResolution(s.x, s.y, mode);
        }

        // ------------------------------------------------------------------ drawing

        static readonly Color Ink = new(0.06f, 0.05f, 0.04f, 0.92f), Rule = new(0.6f, 0.44f, 0.2f), Parch = new(0.87f, 0.80f, 0.66f), Amber = new(1f, 0.68f, 0.25f), Dull = new(0.17f, 0.14f, 0.11f, 0.95f);
        float _x, _y, _w;

        static void Fill(Rect r, Color c) { GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.white; }

        void Styles()
        {
            if (_title != null) return;
            GUIStyle S(int size, FontStyle f, TextAnchor a, Color c)
            {
                var s = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = f, alignment = a };
                s.normal.textColor = s.hover.textColor = s.active.textColor = c;
                return s;
            }
            _title = S(64, FontStyle.Bold, TextAnchor.MiddleCenter, Parch);
            _head = S(20, FontStyle.Bold, TextAnchor.MiddleLeft, Amber);
            _text = S(16, FontStyle.Normal, TextAnchor.MiddleLeft, Parch);
            _value = S(16, FontStyle.Bold, TextAnchor.MiddleRight, Amber);
            _button = S(20, FontStyle.Bold, TextAnchor.MiddleCenter, Parch);
            _on = S(20, FontStyle.Bold, TextAnchor.MiddleCenter, Amber);
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _field.normal.background = _field.hover.background = _field.focused.background = _field.active.background = null;
            _field.normal.textColor = _field.hover.textColor = _field.focused.textColor = _field.active.textColor = Amber;
        }

        bool Button(Rect r, string label, bool on = false)
        {
            bool over = r.Contains(Event.current.mousePosition);
            Fill(r, over ? new Color(0.3f, 0.22f, 0.12f, 0.95f) : Dull);
            Fill(new Rect(r.x, r.yMax - 2, r.width, 2), on || over ? Amber : Rule);
            return GUI.Button(r, label, on ? _on : _button);
        }

        float Slider(string label, float v, float lo, float hi, string shown)
        {
            GUI.Label(new Rect(_x, _y, _w - 90, 24), label, _text);
            GUI.Label(new Rect(_x + _w - 120, _y, 120, 24), shown, _value);
            v = GUI.HorizontalSlider(new Rect(_x, _y + 26, _w, 18), v, lo, hi);
            _y += 50;
            return v;
        }

        string _typing;
        string _typed = "";

        /// <summary>A slider with its number beside it to type into.</summary>
        int Number(string label, int v, int lo, int hi)
        {
            GUI.Label(new Rect(_x, _y, _w - 110, 28), label, _text);
            var box = new Rect(_x + _w - 100, _y, 100, 28);
            Fill(box, Dull);
            Fill(new Rect(box.x, box.yMax - 2, box.width, 2), Rule);
            GUI.SetNextControlName(label);
            bool mine = GUI.GetNameOfFocusedControl() == label;
            if (mine && _typing != label) { _typing = label; _typed = v.ToString(); }
            string s = GUI.TextField(box, mine ? _typed : v.ToString(), 5, _field);
            if (mine)
            {
                _typed = s;
                if (int.TryParse(s, out int n)) v = Mathf.Clamp(n, lo, hi);
                if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return) GUI.FocusControl(null);
            }
            else if (_typing == label) _typing = null;
            float was = v, now = GUI.HorizontalSlider(new Rect(_x, _y + 32, _w, 18), v, lo, hi);
            if (Mathf.Abs(now - was) > 0.5f) { v = Mathf.Clamp(Mathf.RoundToInt(now / 10f) * 10, lo, hi); GUI.FocusControl(null); _typing = null; }
            _y += 56;
            return v;
        }

        bool Toggle(string label, bool v)
        {
            GUI.Label(new Rect(_x, _y, _w - 90, 28), label, _text);
            if (Button(new Rect(_x + _w - 84, _y, 84, 28), v ? "On" : "Off", v)) v = !v;
            _y += 38;
            return v;
        }

        int Choice(string label, int v, string[] names)
        {
            GUI.Label(new Rect(_x, _y, 160, 28), label, _text);
            if (Button(new Rect(_x + _w - 250, _y, 34, 28), "<")) v = (v + names.Length - 1) % names.Length;
            GUI.Label(new Rect(_x + _w - 212, _y, 174, 28), names[Mathf.Clamp(v, 0, names.Length - 1)], _on);
            if (Button(new Rect(_x + _w - 34, _y, 34, 28), ">")) v = (v + 1) % names.Length;
            _y += 38;
            return v;
        }


        public void Draw()
        {
            if (!Open) return;
            Styles();
            float w = Screen.width, h = Screen.height;
            Fill(new Rect(0, 0, w, h), new Color(0.02f, 0.015f, 0.01f, Now == Page.Main ? 0.45f : 0.6f));
            if (Now == Page.Main) Main(w, h);
            else if (Now == Page.Pause) Pause(w, h);
            else Settings(w, h);
        }

        Rect Box(float w, float h, float bw, float bh)
        {
            var r = new Rect((w - bw) * 0.5f, (h - bh) * 0.5f, bw, bh);
            Fill(r, Ink);
            Fill(new Rect(r.x, r.y, r.width, 3), Rule);
            Fill(new Rect(r.x, r.yMax - 3, r.width, 3), Rule);
            _x = r.x + 34; _y = r.y + 28; _w = r.width - 68;
            return r;
        }

        void Main(float w, float h)
        {
            GUI.Label(new Rect(0, h * 0.5f - 330, w, 80), "L.S.B.S", _title);
            Box(w, h, 520, 500);
            _green = Number("Greens", _green, 1, 12000);
            _grey = Number("Greys", _grey, 1, 12000);
            _wave = Number("Reinforcements a wave", _wave, 0, 5000);
            _b.tuning.waveInterval = Number("Seconds between waves", Mathf.RoundToInt(_b.tuning.waveInterval), 5, 600);
            _y += 8;
            if (Button(new Rect(_x, _y, _w, 54), "Begin"))
            {
                PlayerPrefs.SetInt("lsbs.green", _green); PlayerPrefs.SetInt("lsbs.grey", _grey); PlayerPrefs.SetInt("lsbs.wave", _wave);
                _b.tuning.waveSize = _wave;
                Show(Page.None);
                _b.Begin(_green, _grey);
            }
            _y += 66;
            if (Button(new Rect(_x, _y, _w * 0.48f, 42), "New field")) _b.NewField();
            if (Button(new Rect(_x + _w * 0.52f, _y, _w * 0.48f, 42), "Settings")) { _back = Page.Main; Show(Page.Settings); }
            _y += 54;
            if (Button(new Rect(_x, _y, _w, 42), "Quit")) Quit();
        }

        void Pause(float w, float h)
        {
            Box(w, h, 400, 310);
            string[] names = { "Resume", "Settings", "Main menu", "Quit" };
            for (int i = 0; i < names.Length; i++)
            {
                bool hit = Button(new Rect(_x, _y, _w, 52), names[i]);
                _y += 66;
                if (!hit) continue;
                if (i == 0) Show(Page.None);
                else if (i == 1) { _back = Page.Pause; Show(Page.Settings); }
                else if (i == 2) { _b.ToMenu(); Show(Page.Main); }
                else Quit();
            }
        }

        void Settings(float w, float h)
        {
            var box = Box(w, h, 600, 712);
            if (Button(new Rect(_x, _y, _w * 0.32f, 38), "Video", _tab == 0)) _tab = 0;
            if (Button(new Rect(_x + _w * 0.34f, _y, _w * 0.32f, 38), "Audio", _tab == 1)) _tab = 1;
            if (Button(new Rect(_x + _w * 0.68f, _y, _w * 0.32f, 38), "Controls", _tab == 2)) _tab = 2;
            _y += 58;
            if (_tab == 0)
            {
                var names = _sizes.ConvertAll(s => $"{s.x} x {s.y}").ToArray();
                int size = Choice("Resolution", _size, names);
                int window = Choice("Window", Options.Window, WindowNames);
                if (size != _size || window != Options.Window) { _size = size; Options.Window = window; ApplyWindow(); }
                Options.Shadows = Choice("Shadows", Options.Shadows, ShadowNames);
                Options.Antialias = Choice("Antialiasing", Options.Antialias, AaNames);
                int cap = Mathf.Max(0, System.Array.IndexOf(Caps, Options.FpsCap));
                Options.FpsCap = Caps[Choice("Frame limit", cap, System.Array.ConvertAll(Caps, c => c == 0 ? "None" : c.ToString()))];
                Options.VSync = Toggle("VSync", Options.VSync);
                Options.Bloom = Toggle("Bloom", Options.Bloom);
                Options.Grain = Toggle("Film grain", Options.Grain);
                Options.RenderScale = Mathf.Round(Slider("Render scale", Options.RenderScale, 0.5f, 1.5f, $"{Options.RenderScale * 100f:0}%") * 20f) / 20f;
                Options.Detail = Mathf.Round(Slider("Full detail within", Options.Detail, 10f, 120f, $"{Options.Detail:0} m"));
                Options.Ragdolls = Mathf.RoundToInt(Slider("Ragdolls at once", Options.Ragdolls, 20, 300, Options.Ragdolls.ToString()) / 10f) * 10;
                Options.Brightness = Slider("Brightness", Options.Brightness, -1f, 1f, $"{Options.Brightness:+0.0;-0.0;0}");
            }
            else if (_tab == 1)
            {
                Options.Master = Slider("Master", Options.Master, 0f, 1f, $"{Options.Master * 100f:0}%");
                Options.Guns = Slider("Guns", Options.Guns, 0f, 1f, $"{Options.Guns * 100f:0}%");
                Options.Blasts = Slider("Explosions", Options.Blasts, 0f, 1f, $"{Options.Blasts * 100f:0}%");
            }
            else
            {
                Options.Sensitivity = Slider("Mouse sensitivity", Options.Sensitivity, 0.1f, 3f, $"{Options.Sensitivity:0.00}");
                Options.Fov = Mathf.Round(Slider("Field of view", Options.Fov, 55f, 110f, $"{Options.Fov:0}"));
                Options.Shake = Toggle("Camera shake", Options.Shake);
                Options.Counter = Toggle("Performance readout", Options.Counter);
            }
            if (GUI.changed) Apply();
            if (Button(new Rect(_x, box.yMax - 62, _w, 42), "Back")) { Options.Save(); Show(_back); }
        }

        static void Quit()
        {
            Options.Save();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
