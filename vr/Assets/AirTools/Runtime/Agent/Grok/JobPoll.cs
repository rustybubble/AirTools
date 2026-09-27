namespace AirTools.Agent.Grok
{
    public enum PollState { Idle, Waiting, InFlight, Done, Failed, TimedOut, Cancelled }

    /// A background job's poll loop as a pure state machine with injected time (tests drive it without coroutines):
    /// the flythrough (GET /scene/flythrough/{job_id} every 3 s, pending → done | failed) and the rules check
    /// (GET /rules/check/{id}, running → done | failed). Due(now) says when to send the next request; OnReply feeds the
    /// answer back. A 404 (unknown job, e.g. after a server restart) fails at once; other errors retry until MaxErrors in
    /// a row; the whole poll gives up after Timeout.
    public sealed class JobPoll
    {
        public float Interval = 3f;
        public float Timeout = 330f;
        public int MaxErrors = 3;

        public string JobId { get; private set; }
        public PollState State { get; private set; } = PollState.Idle;
        public int Requests { get; private set; }
        public int Errors { get; private set; }
        public string LastStatus { get; private set; }
        float m_Next, m_Started;

        public bool Active => State == PollState.Waiting || State == PollState.InFlight;
        public bool Finished => State == PollState.Done || State == PollState.Failed || State == PollState.TimedOut;

        /// Begin polling `jobId`; the first request goes out after one interval (the job was just started), or at
        /// once with `immediate`.
        public void Start(string jobId, float now, bool immediate = false)
        {
            JobId = jobId;
            State = string.IsNullOrEmpty(jobId) ? PollState.Failed : PollState.Waiting;
            Requests = 0; Errors = 0; LastStatus = null;
            m_Started = now;
            m_Next = immediate ? now : now + Interval;
        }

        /// True exactly when a request should be sent now (the state moves to InFlight until OnReply).
        public bool Due(float now)
        {
            if (State == PollState.Waiting && now - m_Started >= Timeout) { State = PollState.TimedOut; return false; }
            if (State != PollState.Waiting || now < m_Next) return false;
            State = PollState.InFlight;
            Requests++;
            return true;
        }

        /// The reply to the request Due() asked for: HTTP code (0 = no answer) and the body's `status`.
        public PollState OnReply(long httpCode, string status, float now)
        {
            if (State != PollState.InFlight) return State;   // cancelled (or restarted) meanwhile
            if (httpCode == 404) { State = PollState.Failed; LastStatus = "unknown job"; return State; }
            if (httpCode < 200 || httpCode >= 300 || string.IsNullOrEmpty(status))
            {
                Errors++;
                State = Errors >= MaxErrors ? PollState.Failed : PollState.Waiting;
                m_Next = now + Interval;
                return State;
            }
            Errors = 0;
            LastStatus = status;
            switch (status)
            {
                case "done": State = PollState.Done; break;
                case "failed": State = PollState.Failed; break;
                default:   // pending / running
                    State = now - m_Started >= Timeout ? PollState.TimedOut : PollState.Waiting;
                    m_Next = now + Interval;
                    break;
            }
            return State;
        }

        /// Stop without a result (a newer job replaced it, or its answer arrived another way).
        public void Cancel()
        {
            if (Active) State = PollState.Cancelled;
        }
    }
}
