using System;
using System.Collections.Generic;

namespace AirTools.Scene
{
    /// The Model view switcher's load queue (modelview; pure, so the EditMode runner tests it offline): one model loads at
    /// a time; a choice made meanwhile waits and the last one wins; a failed model is remembered until it's chosen again.
    /// ModelSwitcher drives it every frame with the streamer's state.
    public class ModelSwitchState
    {
        public enum Outcome { None, Loaded, Failed, Started, Busy }

        /// The model waiting to load (a tap while another loads), the one loading, and the ones that failed.
        public string Want { get; private set; }
        public string Loading { get; private set; }
        public readonly HashSet<string> Failed = new HashSet<string>();
        public int Loads { get; private set; }
        public int Failures { get; private set; }
        public string LastAction { get; private set; } = "";
        /// The model the last Loaded / Failed outcome was about, and how long it took (s).
        public string LastSite { get; private set; }
        public float LastSeconds { get; private set; }
        float m_Started;

        /// Choose `site` (a card, an arrow, show_model). The model on the table with nothing loading: nothing to do (and a
        /// waiting choice is dropped). The one loading now: nothing more to do. Anything else waits for the table and the
        /// streamer; the last choice wins. Returns what it did (for the log).
        public string Choose(string site, string current)
        {
            if (string.IsNullOrEmpty(site)) return LastAction = "nothing chosen";
            if (site == current && Loading == null && !Failed.Contains(site)) { Want = null; return LastAction = $"already showing {site}"; }
            if (site == Loading) { Want = null; return LastAction = $"already loading {site}"; }
            Want = site;
            return LastAction = Loading != null ? $"{site} up next" : $"chose {site}";
        }

        /// Drop the waiting choice (left Model view).
        public void Forget() => Want = null;

        /// One step. First the running load: once the streamer is idle it is Loaded when the streamer's site is the one
        /// asked for, else Failed. Then, with the table settled and the streamer idle, the waiting choice starts through
        /// `start` (false: the streamer was busy after all; it waits for the next step). `now` is a clock in seconds.
        public Outcome Pump(bool streamerLoading, string streamerSite, bool settled, float now, Func<string, bool> start)
        {
            if (Loading != null)
            {
                if (streamerLoading) return Outcome.None;
                LastSite = Loading;
                LastSeconds = now - m_Started;
                Loading = null;
                if (streamerSite == LastSite) { Failed.Remove(LastSite); Loads++; return Outcome.Loaded; }
                Failed.Add(LastSite);
                Failures++;
                return Outcome.Failed;
            }
            if (Want == null || streamerLoading || !settled) return Outcome.None;
            string want = Want;
            m_Started = now;
            if (start == null || !start(want)) return Outcome.Busy;
            Failed.Remove(want);
            Want = null;
            if (want == ModelSites.BuiltIn)
            {
                // Synchronous: in by the time start returns.
                LastSite = want;
                LastSeconds = 0f;
                Loads++;
                return Outcome.Loaded;
            }
            Loading = want;
            return Outcome.Started;
        }
    }
}
