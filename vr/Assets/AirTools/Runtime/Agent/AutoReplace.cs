using System.Collections.Generic;

namespace AirTools.Agent
{
    /// autonomy: one sentence → the whole replace (backend server/replace_job.py, kind "replace"). The server runs the chain
    /// and the app only executes what arrives (the reply's job_started, then every poll page through AgentActions.ExecuteAll):
    ///   remove_component → measure_cavity → [scale_gap] → search_started → place_part → job_done
    /// with job_step lines between them (the job strip's dots and caption). Pure: what the harness (E2EHarness.Auto) and the
    /// tests check a run's sequence against.
    public static class AutoReplace
    {
        /// The step names the server sends in job_started.steps, in order.
        public static readonly string[] Steps = { "remove", "measure", "scale", "search", "pick", "model", "place" };

        /// The actions a finished replace carries out, in order (scale_gap only when the scale wasn't calibrated).
        public static readonly string[] Order = { "remove_component", "measure_cavity", "scale_gap", "search_started", "place_part", "job_done" };

        /// Actions that ride along and never count (the rail's own lines, a limit set by the same sentence).
        public static bool Passive(string name) => name == "job_step" || name == "job_started" || name == "show_limits";

        /// Whether `ran` (the action names executed, in order, with job_step lines and the like) is a whole replace: each
        /// action of Order at most once and in order, all but scale_gap present, and none failed (`failed`: names that
        /// returned false). `detail` says what's missing or out of place.
        public static bool CheckOrder(IList<string> ran, ICollection<string> failed, out string detail)
        {
            var seen = new List<string>();
            foreach (var n in ran ?? new List<string>())
                if (!Passive(n)) seen.Add(n);
            int at = 0;
            var problems = new List<string>();
            var done = new HashSet<string>();
            foreach (var n in seen)
            {
                int i = System.Array.IndexOf(Order, n);
                if (i < 0) { if (n == "restore_component" || n == "cycle_model" || n == "undo_edit") problems.Add($"unexpected {n}"); continue; }
                if (!done.Add(n)) { problems.Add($"{n} twice"); continue; }
                if (i < at) { problems.Add($"{n} out of order"); continue; }
                at = i + 1;
            }
            foreach (var need in Order)
                if (need != "scale_gap" && !seen.Contains(need)) problems.Add($"no {need}");
            if (failed != null)
                foreach (var f in failed) if (System.Array.IndexOf(Order, f) >= 0) problems.Add($"{f} failed");
            detail = problems.Count == 0 ? string.Join(" → ", seen) : string.Join("; ", problems) + $" (ran: {string.Join(", ", seen)})";
            return problems.Count == 0;
        }

        /// The reply started a replace run (its job_started; the rail's model says so once it's applied).
        public static string RunId(IList<AgentAction> actions)
        {
            if (actions == null) return null;
            foreach (var a in actions) if (a != null && a.name == "job_started") return a.Str("run_id");
            return null;
        }
    }
}
