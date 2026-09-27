using AirTools.Core;
using AirTools.UI;

namespace AirTools.Agent.Grok
{
    /// Lane G3's agent actions (backend docs/api.md §5): panels, cards, the image / video quad and QR codes. Found by
    /// AgentActions through [AgentAction]; each parses its args into a view model and hands it to the scene's GrokCard
    /// (main window slot), ReimagineQuad (world quad) or GrokState (safety verdicts). None of them pays or posts:
    /// the booth post needs the physical 1 s hold on the card, checkout stays with the existing hold-to-pay panel.
    public static class GrokPanelActions
    {
        /// The action names this lane handles, in the §5 table's order.
        public static readonly string[] Names =
        {
            "show_reimagined", "show_installers", "show_report", "show_postcard", "show_safety", "flythrough_started", "show_video",
            "show_manual_answer", "rules_started", "show_rules", "show_packet", "packet_revoked", "show_share_preview", "show_quote_check",
        };

        static GrokCard Card => Services.TryGet<GrokCard>(out var c) ? c : null;
        static ReimagineQuad Quad => Services.TryGet<ReimagineQuad>(out var q) ? q : null;

        static bool NoView(AgentAction a, string view)
        {
            Log.Warn($"G3 {a.name}: no {view} in the scene (AirTools ▸ Wire Main Scene builds it)");
            return false;
        }

        static bool Bad(AgentAction a)
        {
            Log.Warn($"G3 {a.name}: args unreadable: {a}");
            return false;
        }

        [AgentAction("show_postcard")]
        static bool ShowPostcard(AgentAction a, string reply)
        {
            var v = PostcardView.Parse(a.args);
            if (v == null) return Bad(a);
            var card = Card;
            return card != null ? card.ShowPostcard(v) : NoView(a, "GrokCard");
        }

        /// Stores the verdict (GrokState: the spec card and seller panel pills, G1's receipt) and says it once.
        [AgentAction("show_safety")]
        static bool ShowSafety(AgentAction a, string reply)
        {
            var v = SafetyView.Parse(a.args);
            if (v == null) return Bad(a);
            GrokState.SetSafety(v.PartId, v.Verdict, v.Headline);
            if (v.Shows) UiToast.Show($"{(v.Verdict == "recalled" ? "Recalled" : "Caution")} · {GrokText.Clip(v.Headline, 70)}", v.Verdict == "recalled" ? ColorRole.Danger : ColorRole.Warning);
            Log.Info($"G3 safety {v.PartId}: {v.Verdict} \"{v.Headline}\"");
            return true;
        }

        [AgentAction("show_reimagined")]
        static bool ShowReimagined(AgentAction a, string reply)
        {
            var v = ReimagineView.Parse(a.args);
            if (v == null) return Bad(a);
            var quad = Quad;
            return quad != null ? quad.ShowReimagined(v) : NoView(a, "ReimagineQuad");
        }

        [AgentAction("flythrough_started")]
        static bool FlythroughStarted(AgentAction a, string reply)
        {
            var id = a.Str("job_id");
            if (string.IsNullOrEmpty(id)) return Bad(a);
            var quad = Quad;
            return quad != null ? quad.StartFlythrough(id) : NoView(a, "ReimagineQuad");
        }

        [AgentAction("show_video")]
        static bool ShowVideo(AgentAction a, string reply)
        {
            var v = VideoView.Parse(a.args);
            if (v == null) return Bad(a);
            var quad = Quad;
            return quad != null ? quad.ShowVideo(v) : NoView(a, "ReimagineQuad");
        }

        [AgentAction("show_manual_answer")]
        static bool ShowManualAnswer(AgentAction a, string reply)
        {
            var v = ManualAnswerView.Parse(a.args);
            if (v == null) return Bad(a);
            var card = Card;
            return card != null ? card.ShowManual(v) : NoView(a, "GrokCard");
        }

        [AgentAction("show_packet")]
        static bool ShowPacket(AgentAction a, string reply)
        {
            var v = PacketView.Parse(a.args);
            if (v == null) return Bad(a);
            var card = Card;
            return card != null ? card.ShowPacket(v) : NoView(a, "GrokCard");
        }

        [AgentAction("packet_revoked")]
        static bool PacketRevoked(AgentAction a, string reply)
        {
            var card = Card;
            if (card != null) return card.RevokePacket(a.Str("packet_id"));
            GrokState.PacketId = null;
            GrokState.PacketLink = null;
            return true;
        }

        [AgentAction("show_report")]
        static bool ShowReport(AgentAction a, string reply)
        {
            var v = ReportView.Parse(a.args);
            if (v == null) return Bad(a);
            var card = Card;
            return card != null ? card.ShowReport(v) : NoView(a, "GrokCard");
        }

        [AgentAction("show_share_preview")]
        static bool ShowSharePreview(AgentAction a, string reply)
        {
            var v = ShareView.Parse(a.args);
            if (v == null) return Bad(a);
            var card = Card;
            return card != null ? card.ShowShare(v) : NoView(a, "GrokCard");
        }

        [AgentAction("show_installers")]
        static bool ShowInstallers(AgentAction a, string reply)
        {
            var v = InstallersView.Parse(a.args);
            if (v == null) return Bad(a);
            var card = Card;
            return card != null ? card.ShowInstallers(v) : NoView(a, "GrokCard");
        }

        [AgentAction("rules_started")]
        static bool RulesStarted(AgentAction a, string reply)
        {
            var id = a.Str("check_id");
            if (string.IsNullOrEmpty(id)) return Bad(a);
            var card = Card;
            return card != null ? card.StartRules(id) : NoView(a, "GrokCard");
        }

        [AgentAction("show_rules")]
        static bool ShowRules(AgentAction a, string reply)
        {
            var v = RulesView.Parse(a.args);
            if (v == null) return Bad(a);
            var card = Card;
            return card != null ? card.ShowRules(v) : NoView(a, "GrokCard");
        }

        [AgentAction("show_quote_check")]
        static bool ShowQuoteCheck(AgentAction a, string reply)
        {
            var v = QuoteView.Parse(a.args);
            if (v == null) return Bad(a);
            var card = Card;
            return card != null ? card.ShowQuote(v) : NoView(a, "GrokCard");
        }
    }
}
