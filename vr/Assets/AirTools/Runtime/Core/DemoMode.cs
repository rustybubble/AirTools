using System;
using UnityEngine;

namespace AirTools.Core
{
    /// UX W1.8 (decision D7, SPEC §9): the expo's stage-safe behaviour behind one flag. Off by default everywhere since the
    /// user's call on Sat 09-26 evening (the app is a free world: no forced rail/coach, prefs saved, links open, no reset
    /// gestures); settable at runtime through
    /// AgentHarness.DemoOn, the presenter page's Demo switch (PresenterLink), or the launch extra `-e demo on|off`.
    /// While it is on:
    /// - the guide rail and its coach are on (GuideRail.Enabled; the coach's counts stay in memory);
    /// - preferences aren't saved (PersistPrefs is false: accessibility toggles and coach counts start clean for each judge);
    /// - "Open at seller" saves the link to the notebook instead of opening the headset browser (SafeLinks);
    /// - an offline receipt carries an explicit demo label (ReceiptLabel);
    /// - the reset gestures (DemoResetGesture) and the presenter link (PresenterLink) listen.
    /// DemoMode never pays and never changes what the 1 s hold on Pay does.
    public static class DemoMode
    {
        /// Shown on an offline receipt in DemoMode (the backend without Cybersource keys: the normal stage outcome).
        public const string OfflineReceiptLabel = "Demo · offline receipt · no payment was made";
        /// The notebook row's detail for that receipt.
        public const string OfflineReceiptDetail = "demo · no charge";

        static bool? s_On;

        /// (on) after every change.
        public static event Action<bool> Changed;

        /// The value before anyone sets it: **off** everywhere (user, Sat 09-26 evening: "indefinitely disable the demo mode and
        /// all restrictions associated with it … use it as a free world"). It turns on only when someone asks for it: the launch
        /// extra `-e demo on`, the presenter page's Demo switch, or AgentHarness.DemoOn. Was: on in Development player builds.
        public static bool DefaultOn => false;

        public static bool On
        {
            get
            {
                if (!s_On.HasValue) s_On = Initial();
                return s_On.Value;
            }
            set
            {
                if (s_On.HasValue && s_On.Value == value) return;
                s_On = value;
                Log.Info($"DemoMode {(value ? "on" : "off")}");
                ApplyRail(value);
                Changed?.Invoke(value);
            }
        }

        /// Whether user preferences (accessibility toggles, coach counts, a units choice…) may be written to PlayerPrefs.
        /// Ops settings (server URL, the saved "Set scale") always persist: they belong to the headset, not the judge.
        public static bool PersistPrefs => !On;

        /// "Open at seller" saves the link to the notebook instead of launching the headset browser.
        public static bool SafeLinks => On;

        /// The extra label an offline (unauthorized) receipt gets in DemoMode; null otherwise. Pure.
        public static string ReceiptLabel(bool authorized, bool demo) => demo && !authorized ? OfflineReceiptLabel : null;

        public static string ReceiptLabel(bool authorized) => ReceiptLabel(authorized, On);

        /// "on" / "off" / "1" / "0" / "true" / "false" / "yes" / "no" (any case); null when it isn't one of them. Pure.
        public static bool? Parse(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "1": case "on": case "true": case "yes": return true;
                case "0": case "off": case "false": case "no": return false;
                default: return null;
            }
        }

        /// Forget any runtime setting: the next read takes the default (tests; Play mode start).
        public static void ResetToDefault() => s_On = null;

        static bool Initial()
        {
            var extra = Parse(LaunchExtra("demo"));
            return extra ?? DefaultOn;
        }

        /// The rail follows the flag: on with DemoMode, off without it (GuideRail.Enabled can still be flipped on its own
        /// afterwards, e.g. AgentHarness.GuideOn).
        static void ApplyRail(bool on) => AirTools.UI.GuideRail.Enabled = on;

        /// A string launch extra (`adb shell am start … -e key value`); null in the Editor or when absent.
        internal static string LaunchExtra(string key)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                return intent.Call<string>("getStringExtra", key);
            }
            catch { return null; }
#else
            return null;
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnLoad() { s_On = null; Changed = null; }

        /// At start: a DemoMode that is on by default switches the rail on (nobody set it, so no setter ran).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (!On) return;
            ApplyRail(true);
            Log.Info("DemoMode on (default for this build): guide rail + coach on, prefs not saved, links saved, reset gestures + presenter link live");
        }

#if UNITY_EDITOR
        /// Back in Edit mode the Play session's setting is forgotten (the domain isn't reloaded on exit), so EditMode tests
        /// and the next Play session start from the default.
        [UnityEditor.InitializeOnLoadMethod]
        static void EditorHook() =>
            UnityEditor.EditorApplication.playModeStateChanged += s => { if (s == UnityEditor.PlayModeStateChange.EnteredEditMode) s_On = null; };
#endif
    }
}
