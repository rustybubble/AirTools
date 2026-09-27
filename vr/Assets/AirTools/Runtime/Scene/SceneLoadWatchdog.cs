namespace AirTools.Scene
{
    /// Tracks one scene load in plain fields, outside the coroutine that runs it (pure, EditMode-tested). Unity doesn't run
    /// an iterator's finally when a coroutine is stopped (StopAllCoroutines, the object deactivated or destroyed), so
    /// "loading" must not live only inside it: SceneStreamer.Loading is derived from this — a generation id, the start
    /// time and the last progress. A load that makes no progress for StallSeconds, or works longer than TotalSeconds
    /// (downloads don't count: they report their bytes as progress and have their own timeouts), has timed out; the
    /// streamer then gives up (Abort), and a stale coroutine (an older generation) that finishes later is ignored.
    public sealed class SceneLoadWatchdog
    {
        public float StallSeconds = 20f;
        public float TotalSeconds = 60f;

        /// Bumped by every Begin and Abort: a coroutine holding an older one is stale.
        public int Generation { get; private set; }
        public bool Active { get; private set; }
        public string Site { get; private set; }
        /// The step the load last reached ("scene.json", "file mesh.r1.glb", "import", "swap mesh"…).
        public string Stage { get; private set; } = "";
        public float StartedAt { get; private set; } = -1f;
        public float LastProgressAt { get; private set; } = -1f;

        float m_NetSeconds, m_NetSince = -1f;

        /// A new load starts (any older one is superseded); returns its generation.
        public int Begin(string site, float now)
        {
            Generation++;
            Active = true;
            Site = site;
            Stage = "start";
            StartedAt = LastProgressAt = now;
            m_NetSeconds = 0f;
            m_NetSince = -1f;
            return Generation;
        }

        public bool IsCurrent(int gen) => Active && gen == Generation;

        /// The current load reached a new step (progress). False for a stale generation.
        public bool Step(int gen, string stage, float now)
        {
            if (!IsCurrent(gen)) return false;
            Stage = stage;
            LastProgressAt = now;
            return true;
        }

        /// Progress within a step (bytes arriving). False for a stale generation.
        public bool Touch(int gen, float now)
        {
            if (!IsCurrent(gen)) return false;
            LastProgressAt = now;
            return true;
        }

        /// A download starts / ends: its time doesn't count toward TotalSeconds.
        public void NetBegin(int gen, float now) { if (IsCurrent(gen) && m_NetSince < 0f) m_NetSince = now; }
        public void NetEnd(int gen, float now)
        {
            if (!IsCurrent(gen) || m_NetSince < 0f) return;
            m_NetSeconds += now - m_NetSince;
            m_NetSince = -1f;
        }

        /// Seconds of this load not spent downloading.
        public float WorkSeconds(float now) => now - StartedAt - m_NetSeconds - (m_NetSince >= 0f ? now - m_NetSince : 0f);

        public bool TimedOut(float now) => Active && (now - LastProgressAt > StallSeconds || WorkSeconds(now) > TotalSeconds);

        /// "no progress for 21 s" / "over 60 s".
        public string Why(float now) =>
            now - LastProgressAt > StallSeconds ? $"no progress for {now - LastProgressAt:0} s" : $"over {TotalSeconds:0} s";

        /// The current load finished (loaded, or a failure it handled itself). False for a stale generation.
        public bool End(int gen)
        {
            if (!IsCurrent(gen)) return false;
            Active = false;
            return true;
        }

        /// Give up on whatever is running (timed out, the streamer disabled, replaced by the built-in scene): Active goes
        /// false at once and the coroutine, if it's still alive, becomes stale. False when nothing was running.
        public bool Abort()
        {
            if (!Active) return false;
            Active = false;
            Generation++;
            return true;
        }
    }
}
