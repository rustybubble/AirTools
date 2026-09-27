using System;
using UnityEngine;

namespace AirTools.Core
{
    /// Top-level app mode (writeup §3 state diagram).
    public enum AppMode { Passthrough, World, Tabletop }

    /// Single source of truth for the app mode. Visuals (fade, passthrough, scene root) react to
    /// <see cref="Changed"/>; they never own the mode themselves.
    public static class AppState
    {
        public const AppMode InitialMode = AppMode.Passthrough;

        public static AppMode Mode { get; private set; } = InitialMode;

        /// The mode before the latest change (e.g. World entered from Tabletop = stepped in from the model).
        public static AppMode Previous { get; private set; } = InitialMode;

        /// (previous, next). Raised only when the mode actually changes.
        public static event Action<AppMode, AppMode> Changed;

        /// Unscaled time of the last mode change (for "World within 1.5 s" style checks).
        public static float LastChangeTime { get; private set; }

        public static void Set(AppMode mode)
        {
            if (mode == Mode) return;
            var previous = Mode;
            Previous = previous;
            Mode = mode;
            LastChangeTime = Time.unscaledTime;
            Log.Info($"AppState {previous} -> {mode}");
            Changed?.Invoke(previous, mode);
        }

        /// Back to the initial mode with no listeners. Used by tests and on entering Play mode
        /// (the static survives when domain reload is disabled).
        public static void Reset()
        {
            Mode = InitialMode;
            Previous = InitialMode;
            LastChangeTime = 0f;
            Changed = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnLoad() => Reset();
    }
}
