using AirTools.Core;

namespace AirTools.Agent.Grok
{
    /// Lane G2's agent actions (backend docs/api.md §5), registered with [AgentAction] (they take precedence over
    /// AgentActions' switch): show_plan, place_array, survey_started, show_survey, show_coverage, show_labels. They parse
    /// the args into view models and hand them to GrokOverlays; none of them pays.
    public static class GrokOverlayActions
    {
        static GrokOverlays Overlays => Services.TryGet<GrokOverlays>(out var o) ? o : null;

        /// show_plan {plan_id, segments, points, label} (F7 "where does it go?").
        [AgentAction("show_plan")]
        static bool ShowPlan(AgentAction a, string reply)
        {
            var plan = PlanView.Parse(a.args);
            if (plan == null) return false;
            var o = Overlays;
            if (o == null) { Log.Warn("show_plan: no GrokOverlays in the scene"); return false; }
            return o.ShowPlan(plan, reply);
        }

        /// place_array {spacing_mm, plan_id}: at the points of the plan on screen when plan_id matches it (one Undo removes
        /// the set); otherwise today's array along the last tape (AppCommands.PlaceArray), exactly as before.
        [AgentAction("place_array")]
        static bool PlaceArray(AgentAction a, string reply)
        {
            string planId = a.Str("plan_id");
            float? spacing = a.Float("spacing_mm");
            var o = Overlays;
            if (!string.IsNullOrEmpty(planId) && o != null && o.PlanMatches(planId)) return o.PlacePlan(spacing);
            if (!string.IsNullOrEmpty(planId)) Log.Info($"place_array: plan {planId} isn't on screen; array along the last tape instead");
            return AppCommands.PlaceArray(spacing);
        }

        /// survey_started {survey_id} (F10): a spinner; when no show_survey follows in the same reply, poll the survey and
        /// speak its `spoken`.
        [AgentAction("survey_started")]
        static bool SurveyStarted(AgentAction a, string reply)
        {
            string id = a.Str("survey_id");
            if (string.IsNullOrEmpty(id)) return false;
            var o = Overlays;
            return o != null ? o.BeginSurvey(id) : false;
        }

        /// show_survey: the backend's condition survey {survey_id, pins, label} draws pins. Without pins, a legacy B1 door
        /// survey {request_id, groups, unverified, focus} still opens its card (the backend now sends that as
        /// show_tape_survey, which AgentActions' switch handles); anything else is refused.
        [AgentAction("show_survey")]
        static bool ShowSurvey(AgentAction a, string reply)
        {
            if (!SurveyView.IsCondition(a.args))
            {
                if (SurveyView.IsDoorSurvey(a.args)) return AppCommands.ShowSurvey(a.args);
                Log.Warn($"show_survey refused: neither pins (condition survey) nor groups (door survey card): {a}");
                return false;
            }
            var survey = SurveyView.Parse(a.args);
            if (!string.IsNullOrEmpty(survey.SurveyId)) GrokState.SurveyId = survey.SurveyId;
            var o = Overlays;
            if (o == null) { Log.Warn("show_survey: no GrokOverlays in the scene"); return false; }
            return o.ShowSurvey(survey);
        }

        /// show_coverage: args are the GET /scenes/{site}/coverage body (F12 capture coach).
        [AgentAction("show_coverage")]
        static bool ShowCoverage(AgentAction a, string reply)
        {
            var c = CoverageView.Parse(a.args);
            if (c == null) return false;
            var o = Overlays;
            return o != null && o.ShowCoverage(c);
        }

        /// show_labels {frame_id, labels} (F16 "what am I looking at?"). The reply (the names, verbatim) is shown and
        /// spoken by the agent client as usual.
        [AgentAction("show_labels")]
        static bool ShowLabels(AgentAction a, string reply)
        {
            var v = LabelsView.Parse(a.args);
            if (v == null) return false;
            var o = Overlays;
            return o != null && o.ShowLabels(v);
        }
    }
}
