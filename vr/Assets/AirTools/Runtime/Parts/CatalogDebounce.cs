namespace AirTools.Parts
{
    /// catalog: when to ask the laptop while someone types (pure; the clock is passed in). Every change pokes it: the
    /// generation goes up and the ask waits `Delay` seconds from the last key. A reply is used only when its generation is
    /// still the latest, so a slow answer to "dis" never replaces the one for "dishw" (stale replies are dropped).
    public sealed class CatalogDebounce
    {
        public float Delay;
        /// Bumped by every change (and by Cancel): the id of the text as it is now.
        public int Generation { get; private set; }
        /// The generation last sent to the laptop (0: none yet).
        public int Sent { get; private set; }
        /// Replies that came back after a newer key press and were dropped.
        public int Dropped { get; private set; }
        float m_DueAt = -1f;

        public CatalogDebounce(float delay = 0.5f) { Delay = delay; }

        /// Waiting for the typing to pause.
        public bool Waiting => m_DueAt >= 0f;
        /// Sent and not answered (the answer may still be dropped if a newer key press came).
        public bool InFlight { get; private set; }

        /// The text changed at `now`: returns the new generation; the ask waits until now + Delay.
        public int Poke(float now)
        {
            Generation++;
            m_DueAt = now + Delay;
            return Generation;
        }

        /// The text is gone (cleared, or a live search took over): nothing is due, and replies in flight are stale.
        public void Cancel()
        {
            Generation++;
            m_DueAt = -1f;
            InFlight = false;
        }

        /// True once, `Delay` after the last poke: send the ask for `generation` now.
        public bool Due(float now, out int generation)
        {
            generation = 0;
            if (m_DueAt < 0f || now < m_DueAt) return false;
            m_DueAt = -1f;
            generation = Sent = Generation;
            InFlight = true;
            return true;
        }

        /// A reply for `generation` arrived: true when it's still the latest text's (use it), false when a newer key
        /// press made it stale (drop it).
        public bool Accept(int generation)
        {
            if (generation == Generation)
            {
                InFlight = false;
                return true;
            }
            Dropped++;
            return false;
        }

        public void Reset()
        {
            Generation = 0;
            Sent = 0;
            Dropped = 0;
            m_DueAt = -1f;
            InFlight = false;
        }
    }
}
