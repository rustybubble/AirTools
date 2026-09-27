using System;
using System.Collections.Generic;

namespace AirTools.Agent.Grok
{
    /// Polling "do the whole job" (docs/api.md `GET /job/run/{run_id}?after=n`): every second, from cursor `after`, with
    /// `next` as the following cursor, until the run's status is done or stopped. Pure: the caller sends the request
    /// (JobRunPoller) and passes the time in, so the whole cycle is unit-tested.
    ///
    /// job_started is the run's action 0, and it already arrived (in the /agent/command reply), so polling starts at
    /// after = 1. A 404 (unknown run: the server restarted, runs live in memory) stops; other failures back off
    /// (2, 4, 8 s) and give up after MaxFailures in a row; a run is never polled for longer than MaxRunSeconds.
    public sealed class JobPollState
    {
        public float IntervalSeconds = 1f;
        public float MaxBackoffSeconds = 8f;
        public int MaxFailures = 10;
        public float MaxRunSeconds = 600f;

        public string RunId { get; private set; }
        public int After { get; private set; }
        public bool Active { get; private set; }
        public bool InFlight { get; private set; }
        public int Failures { get; private set; }
        public int Polls { get; private set; }
        public double NextAt { get; private set; }
        /// Why polling stopped: "done", "stopped", "unknown run", "unreachable", "timeout", or the caller's reason.
        public string StopReason { get; private set; }
        public string LastStatus { get; private set; }
        double m_StartedAt;

        public void Start(string runId, double now, int after = 1)
        {
            RunId = runId;
            After = Math.Max(0, after);
            Active = !string.IsNullOrEmpty(runId);
            InFlight = false;
            Failures = 0;
            Polls = 0;
            NextAt = now;
            m_StartedAt = now;
            StopReason = Active ? null : "no run";
            LastStatus = "running";
        }

        public bool Due(double now) => Active && !InFlight && now >= NextAt;

        /// The request path (relative to the server base).
        public string Path => $"/job/run/{Uri.EscapeDataString(RunId ?? "")}?after={After}";

        public static bool IsFinal(string status) => status == "done" || status == "stopped" || status == "cancelled";   // switchclean: + cancelled

        /// switchclean: StopReason when the person switched models mid-run (GrokRails.CancelForSwitch).
        public const string SwitchedAway = "cancelled: switched models";

        public void Sent() => InFlight = true;

        /// A body arrived: the actions to run now (in order), and the cursor moves on. Finished runs stop polling.
        public List<AgentAction> Received(JobPollBody body, double now)
        {
            InFlight = false;
            var none = new List<AgentAction>();
            if (!Active) return none;
            if (body == null) { Failed(-2, now); return none; }
            if (!string.IsNullOrEmpty(body.run_id) && body.run_id != RunId) { NextAt = now + IntervalSeconds; return none; }
            Polls++;
            Failures = 0;
            LastStatus = body.status;
            var actions = body.actions ?? none;
            // next = the run's action count; never step back (a body can't un-send actions).
            After = Math.Max(After, body.next);
            if (IsFinal(body.status)) { Active = false; StopReason = body.status; }
            else if (now - m_StartedAt > MaxRunSeconds) { Active = false; StopReason = "timeout"; }
            else NextAt = now + IntervalSeconds;
            return actions;
        }

        /// The request failed (`httpCode` 0 = no response).
        public void Failed(long httpCode, double now)
        {
            InFlight = false;
            if (!Active) return;
            Failures++;
            if (httpCode == 404) { Active = false; StopReason = "unknown run"; return; }
            if (Failures >= MaxFailures) { Active = false; StopReason = "unreachable"; return; }
            if (now - m_StartedAt > MaxRunSeconds) { Active = false; StopReason = "timeout"; return; }
            NextAt = now + Math.Min(MaxBackoffSeconds, IntervalSeconds * Math.Pow(2, Failures));
        }

        /// A request that will never answer (its component went away): free the slot, poll again now.
        public void Abandon(double now)
        {
            if (!InFlight) return;
            InFlight = false;
            NextAt = now;
        }

        public void Stop(string reason)
        {
            Active = false;
            InFlight = false;
            StopReason = reason;
        }
    }
}
