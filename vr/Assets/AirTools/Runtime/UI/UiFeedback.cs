using UnityEngine;

namespace AirTools.UI
{
    /// UI hook points (a press, a hover, the payment chime). Kept for existing callers; the sound and haptics now
    /// come from the FeedbackEvents hub (pooled, spatialised SfxPlayer — no PlayClipAtPoint).
    public static class UiFeedback
    {
        public static void Press(Vector3 at, bool controllerHaptic = true) => FeedbackEvents.Press(at, controllerHaptic);

        public static void Hover() => FeedbackEvents.Hover();

        /// Was the per-frame "end the buzz" call; haptic patterns now run on SfxPlayer. Kept for callers.
        public static void Tick(float now) { }

        /// Success chime (two rising notes) — payment authorized.
        public static void Chime(Vector3 at) => FeedbackEvents.Paid(at);
    }
}
