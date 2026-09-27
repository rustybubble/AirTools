namespace AirTools.UI
{
    /// edit-touch: auto-repeat for a held poke (the Edit view's arrows). The press itself acts once (Begin); while the
    /// finger stays in, it acts again after `Delay` (0.4 s) and then every `Interval` (0.12 s) until it comes out. Pure: a
    /// clock in, a yes / no out, no allocation. A late frame never fires a burst: the schedule catches up to now.
    public sealed class HoldRepeat
    {
        public float Delay = 0.4f;
        public float Interval = 0.12f;

        /// A press is being held (between Begin and the first Tick that sees it released, or End).
        public bool Active { get; private set; }
        /// Repeats fired since Begin (the press itself not counted).
        public int Repeats { get; private set; }

        float m_Next;

        public void Begin(float now)
        {
            Active = true;
            Repeats = 0;
            m_Next = now + Delay;
        }

        public void End() => Active = false;

        /// Is a repeat due at `now`? `held`: the finger is still in (false ends it: nothing more fires).
        public bool Tick(float now, bool held)
        {
            if (!Active) return false;
            if (!held) { Active = false; return false; }
            if (now < m_Next) return false;
            Repeats++;
            m_Next += Interval;
            if (m_Next <= now) m_Next = now + Interval;
            return true;
        }

        /// How many times a press held for `seconds` acts in all (the press + its repeats), sampled finely.
        public static int Count(float seconds, float delay = 0.4f, float interval = 0.12f)
        {
            if (seconds < delay || interval <= 0f) return 1;
            return 2 + (int)System.Math.Floor((seconds - delay) / interval + 1e-4);
        }
    }
}
