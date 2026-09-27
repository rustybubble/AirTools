#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Core;
using AirTools.UI;

namespace AirTools.Dev
{
    /// AgentHarness.GrokCheck("g3") and QrSelfTest(): lane G3's Play-mode checks. Each example reply (GrokG3Examples,
    /// copied from the backend's tests) goes through AgentActions.ExecuteAll exactly like a voice reply, then one
    /// [AirTools.Check] G3.&lt;action&gt; line says what the card / quad / state shows. Nothing is paid, posted or opened:
    /// links go through GrokLinks (the Editor never launches a browser), the hold-to-post is never held.
    public static class GrokG3Check
    {
        static GrokCard Card => Services.TryGet<GrokCard>(out var c) ? c : null;
        static ReimagineQuad Quad => Services.TryGet<ReimagineQuad>(out var q) ? q : null;

        public static string Run()
        {
            var sb = new StringBuilder();
            int pass = 0, total = 0;
            var card = Card;
            var quad = Quad;
            if (card == null || quad == null)
            {
                Log.Check("G3.scene", false, $"card={(card != null)} quad={(quad != null)} (run AirTools ▸ Wire Main Scene)");
                return "G3: the card or the quad is missing from the scene";
            }
            int confirmsBefore = Services.TryGet<GrokClient>(out var client) ? client.BoothConfirms : 0;
            int paidBefore = Services.TryGet<AirTools.Parts.PartsClient>(out var parts) ? parts.CheckoutRequests : 0;
            foreach (var (id, actions) in GrokG3Examples.Replies())
            {
                var list = actions.Select(GrokG3Examples.Action).ToList();
                long before = AgentActions.Executed;
                AgentActions.ExecuteAll(list, "(G3 check)");
                int ran = (int)(AgentActions.Executed - before);   // History is capped at 50, so count by Executed
                var results = AgentActions.History.Skip(System.Math.Max(0, AgentActions.History.Count - ran)).ToList();
                bool ok = results.Count == list.Count && results.All(r => r.EndsWith(": ok"));
                var (good, detail) = Verify(id, card, quad);
                ok &= good;
                total++;
                if (ok) pass++;
                Log.Check($"G3.{id}", ok, $"[{string.Join("; ", results)}] {detail}");
                sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {id}: {detail}");
            }
            // Never pays, never posts without the hold.
            int confirms = client != null ? client.BoothConfirms : 0;
            int paid = parts != null ? parts.CheckoutRequests : 0;
            bool quiet = confirms == confirmsBefore && paid == paidBefore;
            total++;
            if (quiet) pass++;
            Log.Check("G3.never_posts_or_pays", quiet, $"booth confirms {confirms - confirmsBefore}, checkout requests {paid - paidBefore}");
            sb.AppendLine($"{(quiet ? "PASS" : "FAIL")} never posts or pays");
            sb.Insert(0, $"G3 {pass}/{total}\n");
            return sb.ToString().TrimEnd();
        }

        static (bool ok, string detail) Verify(string id, GrokCard card, ReimagineQuad quad)
        {
            string s = card.Summary;
            switch (id)
            {
                case "show_postcard":
                    return (card.Kind == GrokCardKind.Postcard && card.IsOpen && s.Contains("AI preview, not to scale"), s);
                case "show_safety":
                {
                    var v = GrokState.SafetyFor("midea-maw08u1qwt-8d6d08");
                    return (v != null && v.Recalled && v.Headline.StartsWith("Recalled June 2025"), $"verdict={v?.Verdict} \"{v?.Headline}\"");
                }
                case "show_reimagined":
                {
                    bool kitchen = Services.TryGet<AirTools.Scene.SceneStreamer>(out var st) && st.Site == "kitchen" && st.FindCamera("0481") != null;
                    bool placed = quad.Mode == ReimagineQuad.QuadMode.Still && (!kitchen || quad.Registered);
                    return (placed && quad.Current?.Label == "AI preview, not to scale", (kitchen ? "kitchen camera 0481: " : "no kitchen scan: ") + quad.Describe());
                }
                case "flythrough_started":
                    return (quad.Mode == ReimagineQuad.QuadMode.Rendering && quad.FlyPoll.Active, quad.Describe());
                case "show_video":
                    return (quad.Mode == ReimagineQuad.QuadMode.Video && !quad.Registered && quad.CurrentVideo?.VideoUrl?.EndsWith(".mp4") == true, quad.Describe());
                case "show_manual_answer":
                    return (card.Kind == GrokCardKind.Manual && s.Contains("p. 2") && s.Contains("“Drill and 1/8” drill bit”") && s.Contains("[Open manual]"), s);
                case "show_packet":
                    return (card.Kind == GrokCardKind.Packet && card.Qr != null && card.QrText.StartsWith("https://files-cdn.x.ai/") && s.Contains("take the packet down"), s);
                case "packet_revoked":
                    return (card.Kind != GrokCardKind.Packet && GrokState.PacketId == null, $"card={card.Kind} open={card.IsOpen}");
                case "show_report":
                    return (card.Kind == GrokCardKind.Report && card.QrText == ServerConfig.Current.TrimEnd('/') + "/report/g3-probe" && card.Qr != null, s);
                case "show_share_preview":
                    return (card.Kind == GrokCardKind.Share && s.Contains("[hold: Hold 1 s to post it on X]") && s.Contains("#AirTool"), s);
                case "show_installers":
                    return (card.Kind == GrokCardKind.Installers && s.Contains("Estes Services") && s.Contains("estesair.com"), s);
                case "rules":
                    return (card.Kind == GrokCardKind.Rules && !card.RulesPoll.Active && s.Contains("Permit: yes") && s.Contains("Not legal advice"), s);
                case "show_quote_check":
                    return (card.Kind == GrokCardKind.Quote && card.Pages == 2 && s.Contains("AI reading of the quote"), s);
                default:
                    return (false, "unknown check");
            }
        }

        /// Press a G3 control the way a poke / pinch does (GlassButton.Press): "tab permit" | "tab money" | "more" |
        /// "flip" | "open" (the primary button) | "close" | "link <row> <i>" | "quad flip" | "quad close" | "safety" (the
        /// spec card's pill) | "hold" (a simulated 1 s hold on Post: sends POST /booth/x/confirm only when the share is
        /// x_ready — against a server without X keys it gets the 503 and says so).
        public static string Press(string what)
        {
            var card = Card;
            var quad = Quad;
            var w = (what ?? "").Trim().ToLowerInvariant().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (w.Length == 0) return "say what to press";
            bool ok;
            switch (w[0])
            {
                case "tab": ok = card != null && (w.Length > 1 && w[1] == "money" ? card.tabB : card.tabA)?.Press() == true; break;
                case "more": ok = card?.more != null && card.more.Press(); break;
                case "flip": ok = card?.secondary != null && card.secondary.gameObject.activeInHierarchy && card.secondary.Press(); break;
                case "open": ok = card?.primary != null && card.primary.gameObject.activeInHierarchy && card.primary.Press(); break;
                case "close": ok = card?.close != null && card.close.Press(); break;
                case "link": ok = card != null && w.Length > 2 && int.TryParse(w[1], out var r) && int.TryParse(w[2], out var i) && card.PressRowLink(r, i); break;
                case "quad":
                    ok = quad != null && w.Length > 1 && (w[1] == "close" ? quad.closeButton : quad.flipButton) is AirTools.UI.GlassButton b
                         && b.gameObject.activeInHierarchy && b.Press();
                    break;
                case "safety":
                {
                    var banner = UnityEngine.Object.FindFirstObjectByType<SafetyBanner>();
                    ok = banner != null && banner.content != null && banner.content.activeInHierarchy && banner.button.Press();
                    break;
                }
                case "hold": ok = card?.holdConfirm != null && card.hold.gameObject.activeInHierarchy && card.holdConfirm.Simulate(1.05f); break;
                default: return $"unknown control '{what}'";
            }
            return $"{what}: {(ok ? "pressed" : "not available")} | card {card?.Kind} {card?.Summary} | quad {quad?.Describe()} | last link {GrokLinks.LastOpened}";
        }

        /// The QR encoder in the running player: reference codewords (Arase, v2), the format / version bits read back,
        /// a texture with its quiet zone, and the codes the card would draw for this server's report and a long link.
        public static string QrSelfTest()
        {
            var sb = new StringBuilder();
            bool all = true;
            // Kazuhiko Arase's reference codewords for this payload at 2-M (QrCodeTests' vector).
            const string refText = "http://h.io/bipw3-elsz6aho";
            const string refCodewords = "41a687474703a2f2f682e696f2f62697077332d656c737a3661686f0ffe860a30d139c176be71299e340d024";
            var cw = QrCode.Codewords(Encoding.UTF8.GetBytes(refText), 2);
            string hex = string.Concat(cw.Select(b => b.ToString("x2")));
            all &= Log.Check("G3.qr.reference", hex == refCodewords, $"v2 codewords {(hex == refCodewords ? "match" : "differ: " + hex)}");
            var samples = new List<string>
            {
                ServerConfig.Current.TrimEnd('/') + "/report/" + SessionInfo.Id,
                "https://files-cdn.x.ai/tok123/file_3f2a9c1e-7b4d-4e8a-9c6f-1d2e3f4a5b6c.pdf",
                "https://files-cdn.x.ai/" + new string('a', 180),
            };
            foreach (var text in samples)
            {
                var q = QrCode.Encode(text);
                if (q == null) { all &= Log.Check("G3.qr.encode", false, $"{text.Length} chars didn't fit"); continue; }
                var pos = QrCode.FormatPositions(q.Size);
                int first = 0, second = 0;
                for (int i = 0; i < 15; i++)
                {
                    if (q[pos[i].row, pos[i].col]) first |= 1 << i;
                    if (q[pos[15 + i].row, pos[15 + i].col]) second |= 1 << i;
                }
                bool format = first == QrCode.FormatBits(q.Mask) && second == first;
                var tex = q.ToTexture(QrCode.QuietZone, 4);
                bool size = tex.width == (q.Size + 2 * QrCode.QuietZone) * 4 && tex.height == tex.width;
                if (UnityEngine.Application.isPlaying) UnityEngine.Object.Destroy(tex); else UnityEngine.Object.DestroyImmediate(tex);
                bool ok = format && size;
                all &= Log.Check("G3.qr.encode", ok, $"{text.Length} chars → v{q.Version} mask {q.Mask} penalty {QrCode.Penalty(q.Modules)} format {(format ? "ok" : "bad")} texture {(size ? "ok" : "bad")}");
                sb.AppendLine($"{text.Length} chars: v{q.Version} ({q.Size}×{q.Size}) mask {q.Mask} {(ok ? "ok" : "BAD")}");
            }
            all &= Log.Check("G3.qr.too_long", QrCode.Encode(new string('x', QrCode.MaxBytes + 1)) == null, $"> {QrCode.MaxBytes} bytes → no code");
            return $"QR self-test {(all ? "PASS" : "FAIL")}\n{sb.ToString().TrimEnd()}";
        }
    }
}
#endif
