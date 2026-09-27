using System;
using System.Collections.Generic;
using AirTools.Scene;

namespace AirTools.Agent.Grok
{
    /// What a world-model switch means for the running job (switchclean).
    public enum JobSwitchVerdict
    {
        /// No job is running (or it has finished): nothing to do.
        NoJob,
        /// The switch lands on the job's own model (not a change for it).
        SameSite,
        /// The job asked for this switch itself (its show_model / next_model / load_site step): it goes on, on the new model.
        JobSwitch,
        /// The person switched: the job stops on the headset.
        UserSwitch,
    }

    /// switchclean: which model a running job ("do the whole job", the autonomous replace) works on, and whether a switch
    /// is its own or the person's. Pure (the offline runner tests it).
    /// - **The site.** Begin records the model the job started on (SiteScope.Current at job_started).
    /// - **Switch tokens.** A job that switches models itself does it through an action in its reply or a poll page
    ///   (show_model {site}, next_model, load_site {site}). JobRunPoller (and AgentClient, for the reply that starts the
    ///   job) note a token per such action before running it; the model loads a little later (the table settles, the
    ///   package downloads), so a token lives TokenSeconds. `target` is what the action named (a site id or a spoken
    ///   name: ModelSites.Match), or null for "the next one" (any model).
    /// - **The verdict** (OnSwitch, from SiteScope.Leaving through AppCommands.CloseAllForSwitch): a token that matches
    ///   the arriving model is consumed and the job's site moves with it; anything else is the person's switch.
    public sealed class JobSiteGuard
    {
        /// How long a job's switch action may take to become the switch (a fresh model download included).
        public const double TokenSeconds = 180.0;

        public string RunId { get; private set; }
        /// The model the job works on now ("built-in" for the facade).
        public string Site { get; private set; }
        /// Switch tokens not yet used.
        public int Tokens => m_Tokens.Count;

        readonly List<(string target, double until)> m_Tokens = new List<(string, double)>();

        static string Norm(string site) => string.IsNullOrEmpty(site) ? ModelSites.BuiltIn : site;

        /// A job started on `site`. Tokens noted just before (the reply that starts it may carry its own switch) stay.
        public void Begin(string runId, string site)
        {
            RunId = runId;
            Site = Norm(site);
        }

        /// The job finished or stopped: no site, no tokens.
        public void End()
        {
            RunId = null;
            Site = null;
            m_Tokens.Clear();
        }

        /// The actions that switch models.
        public static bool IsSwitchAction(string name) => name == "show_model" || name == "next_model" || name == "load_site";

        /// The model a switch action names, or null for "any" (next / previous, or no name).
        public static string TargetOf(AgentAction a)
        {
            if (a == null || a.name == "next_model") return null;
            string t = a.Str("site") ?? a.Str("model") ?? a.Str("name") ?? a.Str("query") ?? a.Str("scene");
            t = t?.Trim();
            if (string.IsNullOrEmpty(t)) return null;
            switch (t.ToLowerInvariant())
            {
                case "next": case "next model": case "previous": case "prev": case "previous model": case "back": return null;
            }
            return t;
        }

        /// The job issued a switch to `target` (null: any model) at `now`.
        public void Issued(string target, double now, double seconds = TokenSeconds) => m_Tokens.Add((target, now + seconds));

        /// Note a token for every switch action in `actions` (a poll page; with `afterJobStarted`, only those after the
        /// reply's job_started: the rest of a reply is the person's). Returns how many.
        public int NoteIssued(IList<AgentAction> actions, double now, bool afterJobStarted = false)
        {
            if (actions == null) return 0;
            int n = 0;
            bool jobs = !afterJobStarted;
            foreach (var a in actions)
            {
                if (a == null) continue;
                if (a.name == "job_started") { jobs = true; continue; }
                if (!jobs || !IsSwitchAction(a.name)) continue;
                Issued(TargetOf(a), now);
                n++;
            }
            return n;
        }

        /// Does a token's target name the model `to`?
        public static bool Matches(string target, string to)
        {
            if (target == null) return true;
            if (string.Equals(target, to, StringComparison.OrdinalIgnoreCase)) return true;
            return ModelSites.Match(new[] { to }, target) == to;
        }

        /// A switch to `to` is starting. `running`: a job is on the rail and hasn't finished. A JobSwitch consumes its
        /// token and moves the job to `to`.
        public JobSwitchVerdict OnSwitch(string to, bool running, double now)
        {
            if (!running || RunId == null) return JobSwitchVerdict.NoJob;
            to = Norm(to);
            if (to == Site) return JobSwitchVerdict.SameSite;
            for (int i = m_Tokens.Count - 1; i >= 0; i--) if (m_Tokens[i].until < now) m_Tokens.RemoveAt(i);
            for (int i = 0; i < m_Tokens.Count; i++)
            {
                if (!Matches(m_Tokens[i].target, to)) continue;
                m_Tokens.RemoveAt(i);
                Site = to;
                return JobSwitchVerdict.JobSwitch;
            }
            return JobSwitchVerdict.UserSwitch;
        }
    }
}
