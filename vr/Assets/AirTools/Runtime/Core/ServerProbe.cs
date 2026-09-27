using System.Collections.Generic;

namespace AirTools.Core
{
    /// Which laptop server to use when nothing says (fix-ux, pure, EditMode-tested). Launched from the Quest library there
    /// is no launch extra, so the app asks its default (http://127.0.0.1:8000) — but the demo backend runs on :8004
    /// (2026-09-26: two sessions loaded nothing). ServerConfig asks the default, then each candidate in order, for
    /// GET /health (a server that answers that with an error is asked GET /scenes); the first that answers is used for
    /// this session only. Feed it one answer at a time: Next → Report(answered) → … until Done.
    public sealed class ServerProbe
    {
        public readonly string Default;
        /// The URLs to ask, in order: the default, then each candidate not already listed.
        public readonly List<string> Order;
        public string Chosen { get; private set; }
        public bool Done { get; private set; }
        int m_Index;

        public ServerProbe(string defaultUrl, IEnumerable<string> candidates)
        {
            Default = Normalise(defaultUrl);
            Order = OrderOf(defaultUrl, candidates);
            Done = Order.Count == 0;
        }

        /// The URL to ask now (null when done).
        public string Next => Done ? null : Order[m_Index];

        /// The answer for Next: the first URL that answers wins; after the last one, done with none.
        public void Report(bool answered)
        {
            if (Done) return;
            if (answered) { Chosen = Order[m_Index]; Done = true; return; }
            if (++m_Index >= Order.Count) Done = true;
        }

        /// The session's server when it isn't the default (null: keep the default).
        public string Switch => Done && Chosen != null && Chosen != Default ? Chosen : null;

        /// The log line once done: "ServerConfig: default http://127.0.0.1:8000 didn't answer; using http://127.0.0.1:8004".
        public string Message()
        {
            if (!Done) return null;
            if (Chosen == Default) return $"ServerConfig: default {Default} answered";
            if (Chosen != null) return $"ServerConfig: default {Default} didn't answer; using {Chosen}";
            return $"ServerConfig: no server answered ({string.Join(", ", Order)}); staying on {Default} (cached scenes still load)";
        }

        public static string Normalise(string url) => (url ?? "").Trim().TrimEnd('/');

        public static List<string> OrderOf(string defaultUrl, IEnumerable<string> candidates)
        {
            var list = new List<string>();
            void Add(string u) { u = Normalise(u); if (u.Length > 0 && !list.Contains(u)) list.Add(u); }
            Add(defaultUrl);
            if (candidates != null) foreach (var c in candidates) Add(c);
            return list;
        }

        /// Probe only when nothing chose the server (no launch extra, server.txt or saved override).
        public static bool ShouldProbe(bool hasOverride, int candidates) => !hasOverride && candidates > 0;

        /// GET /health answered: a 2xx.
        public static bool Answered(long code) => code >= 200 && code < 300;

        /// A server that answered /health with a client error (an older server without it) is asked GET /scenes.
        public static bool AskScenes(long healthCode) => healthCode >= 400 && healthCode < 500;
    }
}
