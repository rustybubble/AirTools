using System;
using UnityEngine;

namespace AirTools.Core
{
    // settings-assets: two user settings in Settings (ScenePanel ▸ SettingsPrefsRows), saved per device.

    /// Talk (Settings ▸ Talk): how a Talk button listens. Stored as its int value (keep them).
    ///   Hold   — listens while pressed; the release sends.
    ///   Toggle — a press starts listening; a second press sends, and so does the end of speech (1 s of silence).
    ///   Auto   — a press under 0.35 s toggles, a longer one is hold-to-talk (the tap-or-hold before this setting).
    public enum TalkStyle : byte { Auto = 0, Toggle = 1, Hold = 2 }

    /// 3D models (Settings ▸ 3D models): how the parts server makes a part's model — backend `asset_mode` (docs/api.md
    /// "Asset modes"): Hf = Hunyuan3D image-to-3D on Hugging Face, LlmScad = Grok writes OpenSCAD, Auto = the server's
    /// tier order. Stored as its int value (keep them).
    public enum AssetMode : byte { Auto = 0, Hf = 1, LlmScad = 2 }

    /// Where the settings are kept (PlayerPrefs on the headset; a dictionary in tests).
    public interface IPrefsStore
    {
        bool Has(string key);
        int GetInt(string key, int fallback);
        void SetInt(string key, int value);
        void Save();
    }

    public sealed class PlayerPrefsStore : IPrefsStore
    {
        public bool Has(string key) => PlayerPrefs.HasKey(key);
        public int GetInt(string key, int fallback) => PlayerPrefs.GetInt(key, fallback);
        public void SetInt(string key, int value) => PlayerPrefs.SetInt(key, value);
        public void Save() => PlayerPrefs.Save();
    }

    /// The Talk and 3D-models settings: read once, cached, saved on change (not while DemoMode is on: each judge starts
    /// with the defaults, like UiSettings). Changed fires after either changes (the Talk labels, the model swap). Pure
    /// (no logging: the chips and the harness log what the user did).
    /// Default Talk is Toggle: on the headset a ray-pinch hold kept dropping mid-sentence (the pinch flickers), so a
    /// press that starts listening and a second press (or silence) that sends is the reliable one.
    public static class UserPrefs
    {
        public const string KeyTalk = "airtools.voice.talkStyle", KeyAssets = "airtools.parts.assetMode";
        public const TalkStyle DefaultTalk = TalkStyle.Toggle;
        public const AssetMode DefaultAssets = AssetMode.Auto;

        /// Tests: a store instead of PlayerPrefs, and whether to save (null: DemoMode.PersistPrefs).
        public static IPrefsStore Store;
        public static Func<bool> PersistOverride;

        static TalkStyle? s_Talk;
        static AssetMode? s_Assets;
        static IPrefsStore s_PlayerPrefs;

        public static event Action Changed;

        static IPrefsStore Prefs => Store ?? (s_PlayerPrefs ??= new PlayerPrefsStore());
        static bool Persist => PersistOverride != null ? PersistOverride() : DemoMode.PersistPrefs;

        public static TalkStyle Talk
        {
            get => s_Talk ??= Load(KeyTalk, DefaultTalk);
            set
            {
                if (Talk == value) return;
                s_Talk = value;
                Save(KeyTalk, (int)value);
                Changed?.Invoke();
            }
        }

        public static AssetMode Assets
        {
            get => s_Assets ??= Load(KeyAssets, DefaultAssets);
            set
            {
                if (Assets == value) return;
                s_Assets = value;
                Save(KeyAssets, (int)value);
                Changed?.Invoke();
            }
        }

        /// The asset_mode the server takes: "hf" | "llm_scad" | "auto".
        public static string AssetModeWire => Wire(Assets);

        public static string Wire(AssetMode m) => m == AssetMode.Hf ? "hf" : m == AssetMode.LlmScad ? "llm_scad" : "auto";

        /// "hf" / "llm_scad" / "llm+cad" / "auto" (any case) → the mode; anything else Auto. Pure.
        public static AssetMode ParseAssetMode(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "_"))
            {
                case "hf": case "hunyuan": case "ai_mesh": return AssetMode.Hf;
                case "llm_scad": case "llm+scad": case "llm+cad": case "scad": case "cad": case "openscad": return AssetMode.LlmScad;
                default: return AssetMode.Auto;
            }
        }

        /// "hold" / "toggle" / "tap" / "auto" (any case) → the style; null when it isn't one. Pure.
        public static TalkStyle? ParseTalk(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "hold": return TalkStyle.Hold;
                case "toggle": case "tap": return TalkStyle.Toggle;
                case "auto": return TalkStyle.Auto;
                default: return null;
            }
        }

        /// The chip words: Hold / Toggle / Auto; HF / LLM+CAD / Auto.
        public static string Label(TalkStyle s) => s == TalkStyle.Hold ? "Hold" : s == TalkStyle.Toggle ? "Toggle" : "Auto";
        public static string Label(AssetMode m) => m == AssetMode.Hf ? "HF" : m == AssetMode.LlmScad ? "LLM+CAD" : "Auto";

        static T Load<T>(string key, T fallback) where T : struct, Enum
        {
            if (!Persist) return fallback;
            try
            {
                var store = Prefs;
                if (!store.Has(key)) return fallback;
                int v = store.GetInt(key, Convert.ToInt32(fallback));
                if (v < 0 || v > 255 || !Enum.IsDefined(typeof(T), (byte)v)) return fallback;
                return (T)Enum.ToObject(typeof(T), (byte)v);
            }
            catch (Exception) { return fallback; }   // no PlayerPrefs (a pure test): the default
        }

        static void Save(string key, int value)
        {
            if (!Persist) return;
            try
            {
                var store = Prefs;
                store.SetInt(key, value);
                store.Save();
            }
            catch (Exception) { }   // no PlayerPrefs (a pure test): kept for this run only
        }

        /// Tests: forget the cached values (the store keeps its own).
        public static void ResetCache() { s_Talk = null; s_Assets = null; }

        /// Use these values without saving them (tests, a demo reset).
        public static void Use(TalkStyle talk, AssetMode assets)
        {
            bool changed = Talk != talk || Assets != assets;
            s_Talk = talk;
            s_Assets = assets;
            if (changed) Changed?.Invoke();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnLoad() { Changed = null; ResetCache(); }
    }
}
