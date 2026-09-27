namespace AirTools.Parts
{
    /// Hold-to-confirm (SPEC M5 checkout): fires once after the press has been held continuously for Required
    /// seconds; releasing early resets it. Pure logic so the payment rule is tested without a headset.
    public class HoldTimer
    {
        public float Required = 1.0f;
        public float Progress { get; private set; }
        public bool Fired { get; private set; }
        /// How long the press had been held when it fired (seconds; 0 before the first fire). The measured mandate
        /// sends it as hold_ms (B3 hand-off §5.2: must be ≥ 1000).
        public float HeldSeconds { get; private set; }
        /// True while a press is under way (not yet fired).
        public bool Holding => m_Start >= 0f && !Fired;
        float m_Start = -1f;

        /// Feed the pressed state every frame; true exactly once, when the hold completes.
        public bool Update(bool pressed, float now)
        {
            if (!pressed)
            {
                m_Start = -1f;
                Progress = 0f;
                if (Fired) Fired = false;   // a new press can confirm again (after a failed payment, say)
                return false;
            }
            if (Fired) { Progress = 1f; return false; }
            if (m_Start < 0f) m_Start = now;
            Progress = Required <= 0f ? 1f : UnityEngine.Mathf.Clamp01((now - m_Start) / Required);
            if (Progress < 1f) return false;
            Fired = true;
            HeldSeconds = now - m_Start;
            return true;
        }

        public void Reset() { m_Start = -1f; Progress = 0f; Fired = false; }
    }
}
