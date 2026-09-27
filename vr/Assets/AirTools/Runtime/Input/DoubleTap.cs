namespace AirTools.Input
{
    /// Two quick pinches (each a press held under MaxHold, the second starting within Window of the first release)
    /// → fires once on the second release. Pure (EditMode-tested); used for the menu hand's "back to Move" gesture.
    public class DoubleTap
    {
        public float MaxHold = 0.35f;
        public float Window = 0.45f;

        float m_DownAt = -1f, m_LastTapAt = -99f;
        bool m_Down;

        /// Feed the pressed state each frame; true on the frame the double tap completes.
        public bool Update(bool pressed, float now)
        {
            if (pressed && !m_Down)
            {
                m_Down = true;
                m_DownAt = now;
                return false;
            }
            if (!pressed && m_Down)
            {
                m_Down = false;
                bool quick = now - m_DownAt <= MaxHold;
                if (!quick) { m_LastTapAt = -99f; return false; }
                if (m_DownAt - m_LastTapAt <= Window) { m_LastTapAt = -99f; return true; }
                m_LastTapAt = now;
            }
            return false;
        }

        public void Reset() { m_Down = false; m_LastTapAt = -99f; }
    }
}
