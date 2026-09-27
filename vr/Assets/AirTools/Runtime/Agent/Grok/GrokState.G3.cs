using System;
using System.Collections.Generic;

namespace AirTools.Agent.Grok
{
    /// What the Grok features told the app this session, shared between the integration lanes (G1 context/core,
    /// G2 3D overlays, G3 panels, G4 progress rails). Partial: each lane keeps its fields in its own block (or file).
    public static partial class GrokState
    {
        // ---------------- G3 (panels): safety verdicts, links on screen ----------------

        /// A part's recall / defect verdict from show_safety (backend GET /parts/{id}/safety).
        public sealed class SafetyVerdict
        {
            public string PartId;
            /// "recalled" | "caution" | "clear" | "unknown".
            public string Verdict;
            public string Headline;
            /// recalls[0].url (else the first complaint's), fetched when the pill is first tapped; null until then.
            public string RecallUrl;

            public bool Recalled => Verdict == "recalled";
            public bool Caution => Verdict == "caution";
            /// The pill shows (recalled red, caution amber); clear / unknown show nothing.
            public bool Shows => Recalled || Caution;
        }

        static readonly Dictionary<string, SafetyVerdict> s_Safety = new Dictionary<string, SafetyVerdict>();

        /// (part id) after a verdict for it arrives or changes. G1 reads the verdict for the checkout receipt.
        public static event Action<string> SafetyChanged;

        /// The part's verdict, or null when no show_safety has named it this session.
        public static SafetyVerdict SafetyFor(string partId) =>
            partId != null && s_Safety.TryGetValue(partId, out var v) ? v : null;

        /// "recalled" / "caution" / "clear" / "unknown", or null when never checked.
        public static string VerdictFor(string partId) => SafetyFor(partId)?.Verdict;

        public static SafetyVerdict SetSafety(string partId, string verdict, string headline)
        {
            if (string.IsNullOrEmpty(partId)) return null;
            if (!s_Safety.TryGetValue(partId, out var v)) s_Safety[partId] = v = new SafetyVerdict { PartId = partId };
            v.Verdict = string.IsNullOrEmpty(verdict) ? "unknown" : verdict;
            v.Headline = headline ?? "";
            SafetyChanged?.Invoke(partId);
            return v;
        }

        public static void SetRecallUrl(string partId, string url)
        {
            var v = SafetyFor(partId);
            if (v != null) v.RecallUrl = url;
        }

        public static IEnumerable<SafetyVerdict> SafetyVerdicts => s_Safety.Values;

        /// The last rules check per part it names (show_rules part_ids): the spec card's "Permits and rebates" chip.
        static readonly Dictionary<string, RulesView> s_Rules = new Dictionary<string, RulesView>();

        public static void SetRules(RulesView v)
        {
            if (v?.PartIds == null) return;
            foreach (var id in v.PartIds) if (!string.IsNullOrEmpty(id)) s_Rules[id] = v;
        }

        public static RulesView RulesFor(string partId) => partId != null && s_Rules.TryGetValue(partId, out var v) ? v : null;

        /// The job packet on screen (show_packet), until packet_revoked.
        public static string PacketId, PacketLink;
        public static bool PacketPublic;
        /// The last report link (show_report), absolute.
        public static string ReportLink;
        /// The last booth post on X (after the hold-to-post), and how many were sent.
        public static string PostedUrl;
        public static int PostsSent;
        /// Links saved to the notebook instead of opened (DemoMode's SafeLinks).
        public static readonly List<string> SavedLinks = new List<string>();

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetG3()
        {
            s_Safety.Clear();
            s_Rules.Clear();
            PacketId = PacketLink = ReportLink = PostedUrl = null;
            PacketPublic = false;
            PostsSent = 0;
            SavedLinks.Clear();
        }
    }
}
