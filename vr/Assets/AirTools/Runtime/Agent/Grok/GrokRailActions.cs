using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// Lane G4's agent actions (backend docs/api.md §5): "do the whole job" (F15) and the install coach (F17). Found by
    /// AgentActions through [AgentAction]; each one updates GrokRails, which the rail views draw.
    ///
    /// A run's other actions (show_survey, select_candidate, show_safety, show_rules, show_postcard, show_packet,
    /// start_checkout) follow its job_step in the same reply or poll page and go through AgentActions.ExecuteAll in order,
    /// to the lanes that own them. start_checkout only opens the hold-to-pay panel: nothing in this lane pays.
    public static class GrokRailActions
    {
        static double Now => Time.realtimeSinceStartupAsDouble;

        /// Where a rail sound plays: just ahead of the eyes (the rails are heads-up / beside you).
        static Vector3 Ahead => Camera.main != null ? Camera.main.transform.position + Camera.main.transform.forward * 0.4f : Vector3.zero;

        /// job_started {run_id, steps}: the progress rail, and JobRunPoller starts polling GET /job/run/{run_id}?after=1.
        [AgentAction("job_started")]
        static bool JobStarted(AgentAction a, string reply)
        {
            bool ok = GrokRails.Apply(a.name, a.args, Now);
            if (ok) Log.Info($"Rail: job {GrokRails.Job?.RunId} started, {GrokRails.Job?.Dots.Count} steps");
            return ok;
        }

        /// job_step {i, name, status, spoken, seconds, cost_usd}: the dot turns green / grey / red; `spoken` captions it.
        [AgentAction("job_step")]
        static bool JobStep(AgentAction a, string reply)
        {
            bool ok = GrokRails.Apply(a.name, a.args, Now);
            Log.Info($"Rail: step {a.Str("name")} {a.Str("status")}: {a.Str("spoken")}");
            if (ok && a.Str("status") == "done") FeedbackEvents.SnapTick(false);   // a quiet tick per finished step
            if (ok && a.Str("status") == "stopped") FeedbackEvents.Miss();
            return ok;
        }

        /// job_done {run_id, status, total_usd, after_rebates_usd, packet_url, cost_usd}: the summary chip; the rail fades.
        [AgentAction("job_done")]
        static bool JobDone(AgentAction a, string reply)
        {
            bool ok = GrokRails.Apply(a.name, a.args, Now);
            if (!ok) return false;
            Log.Info($"Rail: job {GrokRails.Job.RunId} {GrokRails.Job.Status}: {GrokRailText.JobSummary(GrokRails.Job.Done, GrokRails.Job)}");
            if (GrokRails.Job.Status == "stopped") FeedbackEvents.Miss();
            else FeedbackEvents.Saved(Ahead);   // never Paid: the run only opens the pay panel
            // autonomy: a run that ends with its own words (a replace: "Put a Whirlpool … in the dishwasher gap …") says them
            // aloud, like a voice reply (polled actions carry no audio).
            string spoken = a.Str("spoken");
            if (!string.IsNullOrWhiteSpace(spoken))
            {
                if (Services.TryGet<GrokOverlays>(out var overlays)) overlays.Say(spoken);
                else UiToast.Reply(Copy.Clip(Copy.Clean(spoken), 80));
            }
            return true;
        }

        /// coach_started {coach_id, job, label, source, steps, label_note}: the coach card with one dot per step.
        [AgentAction("coach_started")]
        static bool CoachStarted(AgentAction a, string reply)
        {
            bool ok = GrokRails.Apply(a.name, a.args, Now);
            if (ok) Log.Info($"Rail: coach {GrokRails.Coach.CoachId} ({GrokRails.Coach.Source}) {GrokRails.Coach.Label}: {GrokRails.Coach.DotCount} steps");
            return ok;
        }

        /// coach_step {coach_id, i, of, say, page, quote, pdf_url, tool, checks}: the step's words, manual page, chips.
        [AgentAction("coach_step")]
        static bool CoachStep(AgentAction a, string reply)
        {
            bool ok = GrokRails.Apply(a.name, a.args, Now);
            if (ok) Log.Info($"Rail: coach step {GrokRails.Coach.Current + 1} of {GrokRails.Coach.DotCount}{(GrokRails.Coach.IsDrill ? " (drill)" : "")}");
            return ok;
        }

        /// coach_check {coach_id, i, frame_id, verdict, results, spoken}: chips green / amber / blue; boxes on the scene.
        [AgentAction("coach_check")]
        static bool CoachCheck(AgentAction a, string reply)
        {
            bool ok = GrokRails.Apply(a.name, a.args, Now);
            Log.Info($"Rail: coach check {a.Str("verdict")} frame {a.Str("frame_id") ?? GrokState.LastFrameId ?? "-"}");
            if (ok && a.Str("verdict") == "passed") FeedbackEvents.Saved(Ahead);
            return ok;
        }

        /// coach_stop {coach_id, i, kind, box, drill_px}: a red "don't drill here" card and box; the rail pauses.
        [AgentAction("coach_stop")]
        static bool CoachStop(AgentAction a, string reply)
        {
            bool ok = GrokRails.Apply(a.name, a.args, Now);
            if (ok) Log.Info($"Rail: coach stop ({a.Str("kind")}) on step {GrokRails.Coach.PausedStep + 1}");
            if (ok) FeedbackEvents.Miss();   // a low double pulse with the red card
            return ok;
        }

        /// coach_done {coach_id, checked, overridden}: steps checked by camera vs on the user's word.
        [AgentAction("coach_done")]
        static bool CoachDone(AgentAction a, string reply)
        {
            bool ok = GrokRails.Apply(a.name, a.args, Now);
            if (ok) Log.Info($"Rail: coach done: {GrokRailText.CoachSummary(GrokRails.Coach.Done, GrokRails.Coach.DotCount)}");
            if (ok) FeedbackEvents.Saved(Ahead);
            return ok;
        }
    }
}
