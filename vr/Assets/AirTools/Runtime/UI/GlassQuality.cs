using UnityEngine;

namespace AirTools.UI
{
    /// The Liquid Glass kill switch for the headset A/B (docs/UI.md §7): Lite draws every AirTools/Glass surface as the flat
    /// D3 glass (tint, top light, frost, rim line; no bezel lensing, highlights, clear edge or sheen) through the global
    /// shader keyword AIRTOOLS_GLASS_LITE — one shared variant switch, no material changes. The tool ring
    /// (AirTools/LiquidGlass) is not affected.
    /// Set it with AgentHarness.GlassLite(bool) or at launch: `adb shell am start … -e glass lite`. Each switch logs
    /// "Glass: lite" / "Glass: liquid", so `tools/demo/fps.py <log> 'Glass: lite' --until 'Glass: liquid'` splits the VrApi
    /// samples.
    public static class GlassQuality
    {
        public const string LiteKeyword = "AIRTOOLS_GLASS_LITE";

        public static bool Lite { get; private set; }

        public static void SetLite(bool lite)
        {
            Lite = lite;
            if (lite) Shader.EnableKeyword(LiteKeyword);
            else Shader.DisableKeyword(LiteKeyword);
            AirTools.Core.Log.Info($"Glass: {(lite ? "lite" : "liquid")}");
        }

        /// The launch extra's value: "lite" / "flat" / "off" → lite; "liquid" / "on" / anything else → liquid. Pure.
        public static bool ParseLite(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "lite": case "flat": case "off": case "0": case "false": return true;
                default: return false;
            }
        }

        /// At start: the launch extra (absent = liquid), logged once so a headset log says which run it is.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            string extra = AirTools.Core.DemoMode.LaunchExtra("glass");
            SetLite(extra != null && ParseLite(extra));
        }
    }
}
