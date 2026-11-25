using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Gunbound
{
    /// <summary>Rebindable actions. Order = order in Settings > Controls. Saved by name, so reordering is safe.</summary>
    public enum GameAction { Forward, Back, Left, Right, Jump, Slide, SlideAlt, Reload, Grapple, Loadout, Primary, Secondary }

    /// <summary>All user settings, persisted in PlayerPrefs.</summary>
    public static class GameSettings
    {
        // Video
        public static int ResolutionIndex = -1;
        public static bool Fullscreen = true;
        public static int Quality = -1;
        public static bool VSync = false;
        public static int MaxFpsIndex = 0;
        public static float Fov = 90f;

        // Audio
        public static float MasterVolume = 1f;
        public static float SfxVolume = 1f;
        /// <summary>Music slider value, 0..1. 1 (100%) = MusicBaseGain, i.e. 25% of the stream's full loudness.</summary>
        public static float MusicVolume = 1f;
        public const float MaxMusicVolume = 1f;
        public const float MusicBaseGain = 0.25f;

        /// <summary>Final linear gain for the music stream: slider * cap * master. Music applies master itself
        /// (its AudioSource ignores AudioListener.volume) so master always affects it, exactly once.</summary>
        public static float MusicGain => Mathf.Clamp01(MusicVolume) * MusicBaseGain * Mathf.Clamp01(MasterVolume);

        // Profile
        public static string PlayerName = "";
        public static event Action NameChanged;
        public static int PlayerColor = -1;          // preferred palette index (-1 = pick for me)
        public static event Action ColorChanged;

        public static void SetPlayerColor(int index)
        {
            PlayerColor = Mathf.Clamp(index, -1, PlayerPalette.Count - 1);
            PlayerPrefs.SetInt("gb_color", PlayerColor);
            PlayerPrefs.Save();
            ColorChanged?.Invoke();
        }

        public static void SetPlayerName(string name)
        {
            PlayerName = SanitizeName(name);
            PlayerPrefs.SetString("gb_name", PlayerName);
            PlayerPrefs.Save();
            NameChanged?.Invoke();
        }

        /// <summary>Trim, strip control chars, max 16 chars (fits a FixedString32Bytes).</summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char ch in name.Trim())
            {
                if (char.IsControl(ch)) continue;
                sb.Append(ch);
                if (sb.Length >= 16) break;
            }
            string s = sb.ToString();
            while (System.Text.Encoding.UTF8.GetByteCount(s) > 29) s = s.Substring(0, s.Length - 1);
            return s;
        }

        // Controls
        public static float Sensitivity = 2f;
        public static bool InvertY = false;

        public static readonly int[] MaxFpsOptions = { 0, 60, 120, 144, 165, 240, 360 };

        public static readonly Dictionary<GameAction, KeyBind> DefaultBinds = new()
        {
            { GameAction.Forward, Key.W },
            { GameAction.Back, Key.S },
            { GameAction.Left, Key.A },
            { GameAction.Right, Key.D },
            { GameAction.Jump, Key.Space },
            { GameAction.Slide, Key.LeftCtrl },
            { GameAction.SlideAlt, Key.C },
            { GameAction.Reload, Key.R },
            { GameAction.Grapple, KeyBind.Mouse(KeyBind.MouseButton.Right) },
            { GameAction.Loadout, Key.B },
            { GameAction.Primary, Key.Digit1 },
            { GameAction.Secondary, Key.Digit2 },
        };

        public static readonly Dictionary<GameAction, KeyBind> Binds = new(DefaultBinds);

        public static event Action Changed;

        static bool _loaded;
        static Resolution[] _resolutions;

        public static Resolution[] Resolutions
        {
            get
            {
                if (_resolutions == null)
                {
                    var list = new List<Resolution>();
                    var seen = new HashSet<long>();
                    var all = Screen.resolutions;
                    for (int i = all.Length - 1; i >= 0; i--) // highest first
                    {
                        long key = ((long)all[i].width << 32) | (uint)all[i].height;
                        if (seen.Add(key)) list.Add(all[i]);
                    }
                    if (list.Count == 0) list.Add(Screen.currentResolution);
                    _resolutions = list.ToArray();
                }
                return _resolutions;
            }
        }

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            ResolutionIndex = PlayerPrefs.GetInt("gb_res", -1);
            PlayerName = SanitizeName(PlayerPrefs.GetString("gb_name", ""));
            PlayerColor = Mathf.Clamp(PlayerPrefs.GetInt("gb_color", -1), -1, PlayerPalette.Count - 1);
            Fullscreen = PlayerPrefs.GetInt("gb_fullscreen", 1) == 1;
            Quality = PlayerPrefs.GetInt("gb_quality", QualitySettings.GetQualityLevel());
            VSync = PlayerPrefs.GetInt("gb_vsync", 0) == 1;
            MaxFpsIndex = PlayerPrefs.GetInt("gb_maxfps", 0);
            Fov = PlayerPrefs.GetFloat("gb_fov", 90f);
            MasterVolume = PlayerPrefs.GetFloat("gb_master", 1f);
            SfxVolume = PlayerPrefs.GetFloat("gb_sfx", 1f);
            // new key: older "gb_music"/"gb_music2" values used different scales
            MusicVolume = Mathf.Clamp(PlayerPrefs.GetFloat("gb_music3", 1f), 0f, MaxMusicVolume);
            Sensitivity = PlayerPrefs.GetFloat("gb_sens", 2f);
            InvertY = PlayerPrefs.GetInt("gb_inverty", 0) == 1;
            foreach (var kv in DefaultBinds)
            {
                int k = PlayerPrefs.GetInt("gb_bind_" + kv.Key, kv.Value.Code);
                Binds[kv.Key] = KeyBind.IsValidCode(k) ? new KeyBind(k) : kv.Value;
            }
            MaxFpsIndex = Mathf.Clamp(MaxFpsIndex, 0, MaxFpsOptions.Length - 1);
        }

        public static void Save()
        {
            PlayerPrefs.SetInt("gb_res", ResolutionIndex);
            PlayerPrefs.SetInt("gb_fullscreen", Fullscreen ? 1 : 0);
            PlayerPrefs.SetInt("gb_quality", Quality);
            PlayerPrefs.SetInt("gb_vsync", VSync ? 1 : 0);
            PlayerPrefs.SetInt("gb_maxfps", MaxFpsIndex);
            PlayerPrefs.SetFloat("gb_fov", Fov);
            PlayerPrefs.SetFloat("gb_master", MasterVolume);
            PlayerPrefs.SetFloat("gb_sfx", SfxVolume);
            PlayerPrefs.SetFloat("gb_music3", MusicVolume);
            PlayerPrefs.SetFloat("gb_sens", Sensitivity);
            PlayerPrefs.SetInt("gb_inverty", InvertY ? 1 : 0);
            foreach (var kv in Binds) PlayerPrefs.SetInt("gb_bind_" + kv.Key, kv.Value.Code);
            PlayerPrefs.Save();
        }

        /// <summary>Apply settings to the engine, then save and notify listeners.</summary>
        public static void Apply(bool applyResolution = true)
        {
            if (applyResolution && !Application.isEditor)
            {
                var mode = Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
                if (ResolutionIndex >= 0 && ResolutionIndex < Resolutions.Length)
                {
                    var r = Resolutions[ResolutionIndex];
                    Screen.SetResolution(r.width, r.height, mode);
                }
                else Screen.fullScreenMode = mode;
            }
            if (Quality >= 0 && Quality < QualitySettings.names.Length && Quality != QualitySettings.GetQualityLevel())
                QualitySettings.SetQualityLevel(Quality, true);
            QualitySettings.vSyncCount = VSync ? 1 : 0;
            int fps = MaxFpsOptions[Mathf.Clamp(MaxFpsIndex, 0, MaxFpsOptions.Length - 1)];
            Application.targetFrameRate = fps <= 0 ? -1 : fps;
            AudioListener.volume = MasterVolume;
            Save();
            Changed?.Invoke();
        }

        public static void ResetBinds()
        {
            foreach (var kv in DefaultBinds) Binds[kv.Key] = kv.Value;
            Apply(false);
        }
    }
}
