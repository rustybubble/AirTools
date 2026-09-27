using System;
using System.Collections.Generic;

namespace AirTools.Agent.Grok
{
    /// Replies with several actions (backend docs/api.md §5, integration-grok-features.md "Riders on the next
    /// command"): a walk-in clip or a rules check that finished after its own reply leads the NEXT command's actions
    /// (show_video / show_rules) with its own `spoken` line, which that command's `reply` doesn't contain.
    public static class GrokReplies
    {
        /// The reply card for a reply whose actions carry spoken lines of their own: those lines (in action order),
        /// then the reply. Null when every spoken line is already in the reply (the usual case: the reply card shows
        /// the reply as it is).
        public static string Caption(string reply, IList<AgentAction> actions)
        {
            if (actions == null || actions.Count == 0) return null;
            var lines = new List<string>();
            foreach (var a in actions)
            {
                string spoken = SpokenOf(a);
                if (string.IsNullOrWhiteSpace(spoken)) continue;
                spoken = spoken.Trim();
                if (!string.IsNullOrEmpty(reply) && reply.IndexOf(spoken, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (lines.Exists(l => string.Equals(l, spoken, StringComparison.OrdinalIgnoreCase))) continue;
                lines.Add(spoken);
            }
            if (lines.Count == 0) return null;
            if (!string.IsNullOrWhiteSpace(reply)) lines.Add(reply.Trim());
            return string.Join(" · ", lines);
        }

        /// `args.spoken` of an action that leads a reply as a rider (show_rules, show_video carries none).
        static string SpokenOf(AgentAction a)
        {
            if (a == null || a.args == null) return null;
            switch (a.name)
            {
                case "show_rules": case "show_video": case "show_survey": case "show_coverage": case "show_quote_check":
                    try { return a.Str("spoken"); } catch (Exception) { return null; }
                default: return null;
            }
        }
    }
}
