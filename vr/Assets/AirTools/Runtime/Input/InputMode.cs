using UnityEngine;

namespace AirTools.Input
{
    /// Hands or controllers right now (for wording: "pinch" vs "pull the trigger"). The same test GlassButton logs.
    /// Tests and the harness can pin it with Override.
    public static class InputMode
    {
        /// Forced answer (tests / harness); null = live.
        public static bool? Override;

        public static bool Controllers => Override ?? (Application.isPlaying && (OVRInput.GetConnectedControllers() & OVRInput.Controller.Touch) != 0);
    }
}
