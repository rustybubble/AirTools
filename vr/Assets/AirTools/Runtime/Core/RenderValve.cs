using UnityEngine;

namespace AirTools.Core
{
    /// Pixel guardrails (UX W0.11): the eye buffer never drops below native (the builder sets OVRManager's dynamic
    /// resolution floor to 1.0), and fixed foveated rendering goes to Low in the world only — a valve for the heavier
    /// scene that costs nothing in passthrough, where the UI is read at its sharpest. Headset only.
    public class RenderValve : MonoBehaviour
    {
        [Tooltip("FFR level in the world (passthrough / tabletop keep Off).")]
        public OVRManager.FoveatedRenderingLevel world = OVRManager.FoveatedRenderingLevel.Low;
        public bool enableInWorld = true;

        public string LastApplied { get; private set; } = "";

        void OnEnable() { AppState.Changed += OnMode; Apply(AppState.Mode); }
        void OnDisable() => AppState.Changed -= OnMode;

        void OnMode(AppMode from, AppMode to) => Apply(to);

        /// Foveation on in this mode (pure: tests / the harness).
        public bool FoveatedIn(AppMode mode) => enableInWorld && mode == AppMode.World && world != OVRManager.FoveatedRenderingLevel.Off;

        OVRManager.FoveatedRenderingLevel LevelFor(AppMode mode) => FoveatedIn(mode) ? world : OVRManager.FoveatedRenderingLevel.Off;

        void Apply(AppMode mode)
        {
            var level = LevelFor(mode);
            LastApplied = level.ToString();
            if (Application.isEditor || OVRManager.instance == null || !OVRManager.fixedFoveatedRenderingSupported) return;
            if (OVRManager.foveatedRenderingLevel == level) return;
            OVRManager.foveatedRenderingLevel = level;
            Log.Info($"Render: FFR {level} ({mode})");
        }
    }
}
