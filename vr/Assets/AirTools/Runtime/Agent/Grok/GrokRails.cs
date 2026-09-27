using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AirTools.Agent.Grok
{
    /// The progress rails' state (lane G4): the "do the whole job" run (backend F15) with its poll cursor, and the
    /// install coach (F17). The [AgentAction] handlers (GrokRailActions) feed it; JobRailView / CoachRailView /
    /// CoachOverlay / DrillCrosshair draw it; JobRunPoller polls it. Pure apart from the Changed event: time comes in.
    ///
    /// Never pays: the chain's last step is a `start_checkout`, which AgentActions hands to the existing hold-to-pay
    /// panel (it only opens). Nothing here calls the checkout endpoint; the rail's own buttons send only the coach words below.
    public static class GrokRails
    {
        public static JobRunModel Job { get; private set; }
        public static CoachSessionModel Coach { get; private set; }
        public static readonly JobPollState Poll = new JobPollState();
        /// switchclean: the model the running job works on, and the switches it issued itself.
        public static readonly JobSiteGuard Site = new JobSiteGuard();

        /// Bumped on every change; views redraw when it moves.
        public static int Version { get; private set; }
        public static event Action Changed;

        // ---------------- hands-only coach controls ----------------

        /// The words the rail's buttons send as normal /agent/command commands (the push-to-talk path, which has every
        /// coach fast path: agent.py _COACH_CHECK_RE / _COACH_MOVE_RE), so a judge can drive the coach without speaking.
        public const string CheckIt = "check it", Next = "next", Back = "back", Repeat = "repeat that";
        public static readonly string[] Words = { CheckIt, Back, Repeat, Next };

        /// How a word is sent (AppCommands.SendCommand in the app; tests and the harness capture it instead).
        public static Func<string, bool> Sender;
        public static readonly List<string> Sent = new List<string>();

        /// A rail button: only the four coach words, nothing else.
        public static bool Send(string word)
        {
            if (Array.IndexOf(Words, word) < 0) return false;
            Sent.Add(word);
            if (Sent.Count > 50) Sent.RemoveAt(0);
            return Sender != null && Sender(word);
        }

        /// The manual page last opened ("Open manual"): on the headset it opens in the browser; in the Editor it's
        /// only recorded here (like SellerPanel.LastOpenedUrl).
        public static string LastOpenedUrl;

        // ---------------- actions ----------------

        /// Every action this lane handles (docs/api.md §5).
        public static readonly string[] Actions =
            { "job_started", "job_step", "job_done", "coach_started", "coach_step", "coach_check", "coach_stop", "coach_done" };

        /// Apply one action's args (the handlers call this; tests call it directly with recorded payloads).
        public static bool Apply(string name, JObject args, double now) => name switch
        {
            "job_started" => JobStarted(GrokRailPayloads.JobStarted(args), now),
            "job_step" => JobStep(GrokRailPayloads.JobStep(args), now),
            "job_done" => JobDone(GrokRailPayloads.JobDone(args), now),
            "coach_started" => CoachStarted(GrokRailPayloads.CoachStarted(args), now),
            "coach_step" => CoachStep(GrokRailPayloads.CoachStep(args), now),
            "coach_check" => CoachCheck(GrokRailPayloads.CoachCheck(args), now),
            "coach_stop" => CoachStop(GrokRailPayloads.CoachStop(args), now),
            "coach_done" => CoachDone(GrokRailPayloads.CoachDone(args), now),
            _ => false,
        };

        /// job_started: a rail with one dot per step, and polling from after = 1 (this action is the run's first). The
        /// same run again (a poll from 0 repeats it) keeps the rail as it is.
        public static bool JobStarted(JobStartedArgs a, double now)
        {
            if (a == null || string.IsNullOrEmpty(a.run_id)) return false;
            if (Job != null && Job.RunId == a.run_id) return true;
            Job = JobRunModel.Start(a, now);
            Poll.Start(a.run_id, now, after: 1);
            GrokState.RunId = a.run_id;
            Site.Begin(a.run_id, AirTools.Scene.SiteScope.Current);   // switchclean: the model it works on
            Bump();
            return true;
        }

        public static bool JobStep(JobStepArgs s, double now)
        {
            if (Job == null || s == null || !Job.Step(s, now)) return false;
            Bump();
            return true;
        }

        public static bool JobDone(JobDoneArgs d, double now)
        {
            if (Job == null || !Job.Finish(d, now)) return false;
            if (Poll.RunId == Job.RunId && Poll.Active) Poll.Stop(d.status ?? "done");
            Site.End();   // switchclean
            Bump();
            return true;
        }

        /// A poll body arrived: its actions to execute (in order) and the dots reconciled with its steps[].
        public static List<AgentAction> Polled(JobPollBody body, double now)
        {
            var actions = Poll.Received(body, now);
            if (body != null && Job != null && (string.IsNullOrEmpty(body.run_id) || body.run_id == Job.RunId) && Job.Reconcile(body.steps) > 0) Bump();
            return actions;
        }

        public static bool CoachStarted(CoachStartedArgs a, double now)
        {
            var m = CoachSessionModel.Start(a, now);
            if (m == null) return false;
            Coach = m;
            SyncState();
            Bump();
            return true;
        }

        public static bool CoachStep(CoachStepArgs s, double now)
        {
            if (s == null || string.IsNullOrEmpty(s.coach_id) && Coach == null) return false;
            if (Coach == null || Coach.Finished || !Coach.Owns(s.coach_id)) Coach = CoachSessionModel.FromStep(s, now);
            if (Coach == null || !Coach.ApplyStep(s, now)) return false;
            SyncState();
            Bump();
            return true;
        }

        public static bool CoachCheck(CoachCheckArgs c, double now)
        {
            if (Coach == null || !Coach.ApplyCheck(c, now)) return false;
            Bump();
            return true;
        }

        public static bool CoachStop(CoachStopArgs s, double now)
        {
            if (Coach == null || !Coach.ApplyStop(s, now)) return false;
            SyncState();
            Bump();
            return true;
        }

        public static bool CoachDone(CoachDoneArgs d, double now)
        {
            if (Coach == null || !Coach.Finish(d, now)) return false;
            SyncState();
            Bump();
            return true;
        }

        /// The coach's parts of GrokState that G1 reads (coach running, drill step up).
        static void SyncState()
        {
            bool running = Coach != null && !Coach.Finished;
            GrokState.CoachActive = running;
            GrokState.CoachId = running ? Coach.CoachId : null;
            bool drill = running && Coach.IsDrill;
            if (drill && !GrokState.DrillActive) GrokState.DrillPx = new[] { 0.5f, 0.5f };
            GrokState.DrillActive = drill;
            if (!drill) { GrokState.DrillPx = null; GrokState.DrillPoint = null; GrokState.DrillWorld = null; GrokState.DrillEye = null; }
        }

        /// The frame a coach check's boxes are in: its own frame_id, else the frame G1 last sent (the /agent/command path
        /// sends none).
        public static string FrameFor(CoachCheckArgs c) => !string.IsNullOrEmpty(c?.frame_id) ? c.frame_id : GrokState.LastFrameId;

        // ---------------- switchclean: a job never acts on another model ----------------

        /// A job is on the rail and hasn't finished.
        public static bool Running => Job != null && !Job.Finished;

        /// What a switch to `to` means for the running job (JobSiteGuard.OnSwitch; a JobSwitch moves it to `to`).
        public static JobSwitchVerdict OnSwitch(string to, double now) => Site.OnSwitch(to, Running, now);

        /// The person switched to `to` mid-run: the headset stops the job — no more polls, a poll in flight is dropped
        /// when it answers, and its remaining steps never run here — the strip ends cancelled ("✗ Cancelled · you
        /// switched to the Zabel gym"), and the server is told (CancelOnServer: POST /job/run/{id}/cancel, fire and
        /// forget), so its run stops and the session can start a new job at once. `toast` is the quiet line to show.
        /// False when no job was running.
        public static bool CancelForSwitch(string to, double now, out string toast)
        {
            toast = null;
            if (!Running) return false;
            var job = Job;
            toast = GrokRailText.CancelledToast(job, to);
            if (Poll.RunId == job.RunId) Poll.Stop(JobPollState.SwitchedAway);
            job.Cancel(GrokRailText.CancelledSummary(to), now);
            Site.End();
            Bump();
            try { CancelOnServer?.Invoke(job.RunId, CancelReason(to)); }
            catch (Exception) { /* fire and forget: the headset has already stopped the run */ }
            return true;
        }

        /// How the server is told a run is cancelled: (run id, reason). The app's is AirTools.Agent.JobCancelClient.Send
        /// (lane G4's files never send a request themselves); tests capture it instead. Reset puts the app's back.
        static readonly Action<string, string> DefaultCancel = AirTools.Agent.JobCancelClient.Send;   // first: initializers run in order
        public static Action<string, string> CancelOnServer = DefaultCancel;

        /// The reason the server logs and puts in its job_done: "switched to zabel-gymnasium".
        public static string CancelReason(string to) => $"switched to {(string.IsNullOrEmpty(to) ? AirTools.Scene.ModelSites.BuiltIn : to)}";
        // end switchclean

        /// Close the rails (the harness; leaving the app).
        public static void Clear()
        {
            Job = null;
            Coach = null;
            Poll.Stop("cleared");
            Site.End();   // switchclean
            GrokState.ResetG4();
            Bump();
        }

        /// Everything back to the start (tests; Play mode keeps statics).
        public static void Reset()
        {
            Clear();
            Sent.Clear();
            Sender = null;
            CancelOnServer = DefaultCancel;   // switchclean
            LastOpenedUrl = null;
            Version = 0;
        }

        public static void Bump()
        {
            Version++;
            Changed?.Invoke();
        }
    }
}
