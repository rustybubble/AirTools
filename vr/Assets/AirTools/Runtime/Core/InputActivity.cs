namespace AirTools.Core
{
    /// When the wearer last did something on purpose, and with what (UX W1.3 §2.1: the coach's idle delays and its
    /// modality-aware copy). GuideRail touches it from the existing input events (ToolInputHub press / button, UI
    /// presses and every feedback event, the palm menu opening, a ring spin, holding Pay) and sets the modality from
    /// InputMode each frame. Head motion doesn't count. Pure: callers pass the time.
    public static class InputActivity
    {
        public static float LastAt { get; private set; }
        public static InputModality Modality { get; set; }
        public static int Touches { get; private set; }

        public static void Touch(float now)
        {
            if (now > LastAt) LastAt = now;
            Touches++;
        }

        /// Seconds since the last deliberate input (never negative).
        public static float IdleSeconds(float now) => now > LastAt ? now - LastAt : 0f;

        public static void Reset(float now = 0f)
        {
            LastAt = now;
            Touches = 0;
            Modality = InputModality.Hands;
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnLoad() => Reset();
    }
}
