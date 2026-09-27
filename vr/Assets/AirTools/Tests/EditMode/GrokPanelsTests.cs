using System;
using System.Linq;
using System.Text.RegularExpressions;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Dev;
using AirTools.Scene;
using AirTools.UI;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// Lane G3 (Grok integration, backend docs/api.md §5 panels): the JSON → view models for every panel action, fed
    /// the backend's own payloads (GrokG3Examples, copied from its tests), the camera pose → quad placement on the
    /// kitchen's real camera, and the flythrough / rules poll state machine with injected time. Pure: runs offline.
    public class GrokPanelsTests
    {
        /// Colour roles as readable names instead of theme hex (no theme asset offline).
        static string Hex(ColorRole r) => r.ToString().ToUpperInvariant();
        static string Plain(string s) => Regex.Replace(s ?? "", "<[^>]*>", "");
        static bool Near(float a, float b, float tol = 1e-3f) => Mathf.Abs(a - b) <= tol;
        static bool Near(Vector3 a, Vector3 b, float tol = 1e-3f) => (a - b).magnitude <= tol;

        // ---------------- action names ----------------

        [Test]
        public void EveryExampleIsOneOfTheLanesActions()
        {
            var names = new[]
            {
                GrokG3Examples.Postcard, GrokG3Examples.PostcardUploaded, GrokG3Examples.SafetyRecalled, GrokG3Examples.SafetyCaution,
                GrokG3Examples.SafetyClear, GrokG3Examples.ReimaginedStep2, GrokG3Examples.ReimaginedStep0, GrokG3Examples.ReimaginedKitchen,
                GrokG3Examples.FlythroughStarted, GrokG3Examples.Video, GrokG3Examples.Manual, GrokG3Examples.ManualNotCovered,
                GrokG3Examples.PacketPublic, GrokG3Examples.PacketLan, GrokG3Examples.PacketRevoked, GrokG3Examples.Report,
                GrokG3Examples.Share, GrokG3Examples.ShareNoX, GrokG3Examples.Installers, GrokG3Examples.RulesStarted,
                GrokG3Examples.RulesDone, GrokG3Examples.RulesOffline, GrokG3Examples.QuoteBad, GrokG3Examples.QuoteClean,
            }.Select(j => GrokG3Examples.Action(j).name).Distinct().ToList();
            CollectionAssert.IsSubsetOf(names, GrokPanelActions.Names);
            CollectionAssert.AreEquivalent(GrokPanelActions.Names, names, "an example for every action");
        }

        // ---------------- show_postcard ----------------

        [Test]
        public void PostcardFlipsOnlyWithABeforeFrame()
        {
            var v = PostcardView.Parse(GrokG3Examples.Args(GrokG3Examples.Postcard));
            Assert.AreEqual("karran-qu-670-bl-2d781c", v.PartId);
            Assert.AreEqual("/parts/karran-qu-670-bl-2d781c/postcard.jpg?v=1790412901175330649", v.ImageUrl);
            Assert.AreEqual("/scenes/kitchen/thumbs/0241.jpg", v.BeforeUrl);
            Assert.AreEqual("AI preview, not to scale", v.Label);
            Assert.IsTrue(v.CanFlip);
            var up = PostcardView.Parse(GrokG3Examples.Args(GrokG3Examples.PostcardUploaded));
            Assert.IsNull(up.BeforeUrl);
            Assert.IsFalse(up.CanFlip, "a headset frame has no before");
            Assert.IsNull(PostcardView.Parse(new Newtonsoft.Json.Linq.JObject { ["part_id"] = "x" }), "no picture, no card");
        }

        // ---------------- show_safety ----------------

        [Test]
        public void SafetyVerdictsPickThePill()
        {
            var r = SafetyView.Parse(GrokG3Examples.Args(GrokG3Examples.SafetyRecalled));
            Assert.AreEqual("midea-maw08u1qwt-8d6d08", r.PartId);
            Assert.AreEqual("recalled", r.Verdict);
            Assert.AreEqual("Recalled June 2025: risk of mold exposure (CPSC #25320)", r.Headline);
            Assert.IsTrue(r.Shows);
            Assert.AreEqual("RECALLED", r.Pill);
            Assert.AreEqual(ColorRole.Danger, r.Tone, "red");
            var c = SafetyView.Parse(GrokG3Examples.Args(GrokG3Examples.SafetyCaution));
            Assert.AreEqual("CAUTION", c.Pill);
            Assert.AreEqual(ColorRole.Warning, c.Tone, "amber");
            var clear = SafetyView.Parse(GrokG3Examples.Args(GrokG3Examples.SafetyClear));
            Assert.IsFalse(clear.Shows);
            Assert.IsNull(clear.Pill, "clear shows nothing");
            var unknown = SafetyView.Parse(new Newtonsoft.Json.Linq.JObject { ["part_id"] = "p", ["verdict"] = null });
            Assert.AreEqual("unknown", unknown.Verdict);
            Assert.IsFalse(unknown.Shows);
        }

        [Test]
        public void ThePillOpensTheFirstRecallElseAComplaint()
        {
            var report = GrokG3Examples.Body(GrokG3Examples.SafetyReport);
            Assert.AreEqual("https://www.cpsc.gov/Recalls/2025/Midea-Recalls-About-1-7-Million-U-and-U-Window-Air-Conditioners-Due-to-Risk-of-Mold-Exposure",
                SafetyView.LinkFrom(report));
            report["recalls"] = new Newtonsoft.Json.Linq.JArray();
            Assert.AreEqual("https://www.nytimes.com/wirecutter/reviews/where-are-midea-u-air-conditioners/", SafetyView.LinkFrom(report), "caution from complaints");
            Assert.IsNull(SafetyView.LinkFrom(new Newtonsoft.Json.Linq.JObject()));
            Assert.IsNull(SafetyView.LinkFrom(null));
        }

        [Test]
        public void GrokStateKeepsTheVerdictPerPartForTheReceipt()
        {
            GrokState.ResetG3();
            string changed = null;
            Action<string> h = id => changed = id;
            GrokState.SafetyChanged += h;
            try
            {
                Assert.IsNull(GrokState.VerdictFor("midea-maw08u1qwt-8d6d08"), "never checked");
                GrokState.SetSafety("midea-maw08u1qwt-8d6d08", "recalled", "Recalled June 2025: risk of mold exposure (CPSC #25320)");
                Assert.AreEqual("midea-maw08u1qwt-8d6d08", changed);
                Assert.AreEqual("recalled", GrokState.VerdictFor("midea-maw08u1qwt-8d6d08"));
                Assert.IsTrue(GrokState.SafetyFor("midea-maw08u1qwt-8d6d08").Shows);
                GrokState.SetRecallUrl("midea-maw08u1qwt-8d6d08", "https://www.cpsc.gov/x");
                Assert.AreEqual("https://www.cpsc.gov/x", GrokState.SafetyFor("midea-maw08u1qwt-8d6d08").RecallUrl);
                GrokState.SetSafety("midea-maw08u1qwt-8d6d08", "clear", "No recalls or complaint patterns found");
                Assert.IsFalse(GrokState.SafetyFor("midea-maw08u1qwt-8d6d08").Shows, "a later clear takes the pill down");
                Assert.IsNull(GrokState.SetSafety(null, "recalled", "x"));
            }
            finally { GrokState.SafetyChanged -= h; GrokState.ResetG3(); }
            Assert.IsNull(GrokState.VerdictFor("midea-maw08u1qwt-8d6d08"));
        }

        // ---------------- show_manual_answer ----------------

        [Test]
        public void ManualAnswerQuotesThePageWhenCovered()
        {
            var v = ManualAnswerView.Parse(GrokG3Examples.Args(GrokG3Examples.Manual));
            Assert.AreEqual("Use a 1/8 in drill bit.", v.Answer);
            Assert.AreEqual(2, v.Page);
            Assert.IsTrue(v.Cited);
            Assert.AreEqual("“Drill and 1/8” drill bit” · p. 2", Plain(v.Citation()));
            Assert.AreEqual("/parts/midea-maw08u1qwt-8d6d08/manual.pdf#page=2", v.PdfUrl, "opens on the cited page");
            var not = ManualAnswerView.Parse(GrokG3Examples.Args(GrokG3Examples.ManualNotCovered));
            Assert.IsFalse(not.Cited);
            Assert.AreEqual("", not.Citation(), "page null: the answer only");
            Assert.AreEqual("The manual doesn't cover that, so I won't guess.", not.Answer);
        }

        // ---------------- show_packet / show_report ----------------

        [Test]
        public void PacketLinksPublicAsIsAndLanOnTheServer()
        {
            var pub = PacketView.Parse(GrokG3Examples.Args(GrokG3Examples.PacketPublic));
            Assert.IsTrue(pub.Public);
            Assert.IsNull(pub.QrPngUrl, "no server QR: the app encodes it");
            Assert.AreEqual("https://files-cdn.x.ai/tok123/file_3f2a9c1e-7b4d-4e8a-9c6f-1d2e3f4a5b6c.pdf", pub.Link("http://192.168.1.20:8000"));
            Assert.AreEqual("Public link · until 3 Oct 2026, 10:32 UTC", pub.Details());
            StringAssert.Contains("take the packet down", pub.Label);
            Assert.AreEqual("2026-10-03T10:32:16Z", pub.ExpiresAt, "Json.NET's date token comes back as the same instant");
            var lan = PacketView.Parse(GrokG3Examples.Args(GrokG3Examples.PacketLan));
            Assert.IsFalse(lan.Public);
            Assert.AreEqual("http://192.168.1.20:8000/packet/f7b741f8bc5847cf.pdf", lan.Link("http://192.168.1.20:8000/"));
            Assert.AreEqual("Local network only", lan.Details());
            Assert.IsNotNull(QrCode.Encode(pub.Link("")), "a public link fits a QR");
        }

        [Test]
        public void ReportLinkGetsTheServerBaseUrl()
        {
            var v = ReportView.Parse(GrokG3Examples.Args(GrokG3Examples.Report));
            Assert.AreEqual("/report/g3-probe", v.Url);
            Assert.AreEqual("http://127.0.0.1:8000/report/g3-probe", v.Link("http://127.0.0.1:8000"));
            Assert.AreEqual("http://127.0.0.1:8000/report/g3-probe", v.Link("http://127.0.0.1:8000/"));
            Assert.IsNull(ReportView.Parse(new Newtonsoft.Json.Linq.JObject()));
        }

        [Test]
        public void UrlHelpers()
        {
            Assert.AreEqual("https://x.com/a", GrokUrls.Absolute("https://x.com/a", "http://h:8000"));
            Assert.AreEqual("http://h:8000/a/b", GrokUrls.Absolute("a/b", "http://h:8000/"));
            Assert.IsNull(GrokUrls.Absolute("  ", "http://h:8000"));
            Assert.AreEqual("estesair.com", GrokUrls.Host("https://www.estesair.com/ductless-mini-splits"));
            Assert.AreEqual("profindr.com", GrokUrls.Host("https://profindr.com:443/top?x=1"));
            Assert.AreEqual("$1,195.00", GrokText.Money(1195));
            Assert.AreEqual("$500", GrokText.MoneyShort(500));
            Assert.AreEqual("$499.50", GrokText.MoneyShort(499.5));
            Assert.AreEqual("<noparse><b>x</b></noparse>", GrokText.Esc("<b>x</b>"), "server text is never markup");
            Assert.IsNull(GrokText.When("not a date"));
            Assert.IsTrue(GrokUrls.IsLoopback("http://127.0.0.1:8000/report/x"));
            Assert.IsTrue(GrokUrls.IsLoopback("http://localhost:8000/report/x"));
            Assert.IsFalse(GrokUrls.IsLoopback("http://192.168.1.20:8000/report/x"));
        }

        // ---------------- show_share_preview ----------------

        [Test]
        public void SharePostsOnlyWithAToken()
        {
            var v = ShareView.Parse(GrokG3Examples.Args(GrokG3Examples.Share));
            Assert.AreEqual("/booth/cards/1b41aba008de.jpg", v.CardUrl);
            Assert.AreEqual("Undermount Quartz Sink at kitchen, $364.93.", v.Caption);
            Assert.IsTrue(v.XReady);
            Assert.IsTrue(v.CanPost);
            StringAssert.EndsWith("#AirTool", v.PreviewText);
            Assert.AreEqual("{\"confirm_token\":\"KXxHBFBkgzBAzKuMpFodtQ\"}", v.ConfirmBody());
            Assert.AreEqual("https://x.com/i/web/status/t9", ShareView.PostedUrl(GrokG3Examples.BoothConfirmReply));
            Assert.IsNull(ShareView.PostedUrl("not json"));
            Assert.IsTrue(ShareView.TokenFresh(0f));
            Assert.IsTrue(ShareView.TokenFresh(290f));
            Assert.IsFalse(ShareView.TokenFresh(296f), "the 5 min token (with margin): no ring");
            var no = ShareView.Parse(GrokG3Examples.Args(GrokG3Examples.ShareNoX));
            Assert.IsFalse(no.CanPost, "no X: no ring");
            StringAssert.Contains("expired", ShareView.ConfirmError(409, null));
            StringAssert.Contains("isn't set up", ShareView.ConfirmError(503, "X not configured"));
            StringAssert.Contains("nothing was posted", ShareView.ConfirmError(0, null));
        }

        // ---------------- show_installers ----------------

        [Test]
        public void InstallersShowEvidenceLinksAndSnippets()
        {
            var v = InstallersView.Parse(GrokG3Examples.Args(GrokG3Examples.Installers));
            Assert.AreEqual("Found 2 with evidence near Atlanta, GA: Estes Services and Bardi.", v.Summary);
            Assert.AreEqual(2, v.Rows.Count);
            var rows = v.RowsFor(Hex);
            Assert.AreEqual(new[] { "estesair.com", "profindr.com" }, rows[0].Links.Select(l => l.Label).ToArray());
            Assert.AreEqual("https://profindr.com/top/ductless-mini-split-atlanta-ga", rows[0].Links[1].Url);
            Assert.AreEqual(1, rows[1].Links.Count);
            var estes = Plain(rows[0].Text);
            StringAssert.Contains("Estes Services · Atlanta, GA", estes);
            StringAssert.Contains("(404) 366-9620 · good reviews · from the web", estes);
            StringAssert.Contains("Review snippet: “Very professional and personable.”", estes, "a snippet, not a verified quote");
            StringAssert.DoesNotContain("Review snippet", Plain(rows[1].Text), "no quote, no snippet");
            StringAssert.Contains("<color=#SUCCESS>good reviews</color>", rows[0].Text);
        }

        // ---------------- show_rules ----------------

        [Test]
        public void RulesPermitTab()
        {
            var v = RulesView.Parse(GrokG3Examples.Args(GrokG3Examples.RulesDone));
            Assert.IsTrue(v.Done);
            Assert.AreEqual("minisplit_install", v.Job);
            Assert.AreEqual("yes", v.PermitRequired);
            Assert.AreEqual(ColorRole.Warning, RulesView.PermitTone("yes"), "yes amber");
            Assert.AreEqual(ColorRole.Success, RulesView.PermitTone("no"), "no green");
            Assert.AreEqual(ColorRole.TextSecondary, RulesView.PermitTone("depends"), "else grey");
            Assert.AreEqual("Atlanta city, GA", v.Place);
            Assert.IsFalse(v.CityOnly);
            var header = Plain(v.PermitHeader(Hex));
            StringAssert.Contains("Permit: yes", header);
            StringAssert.Contains("Office: City of Atlanta Office of Buildings", header);
            StringAssert.Contains("Inspections: Mechanical final, Electrical rough-in, Electrical final", header);
            StringAssert.Contains("IMC: prior edition with Georgia amendments; 2024 with Georgia Amendments from 2027-01-01\n", header + "\n");
            StringAssert.DoesNotContain("T00:00", header, "a bare date stays a date");
            var rows = v.PermitRows(Hex);
            Assert.AreEqual(2, rows.Count);
            StringAssert.StartsWith("Mechanical (HVAC) Permit · Office of Buildings, Trades Division", Plain(rows[0].Text));
            StringAssert.Contains("unverified quote", Plain(rows[0].Text), "quote_status unreadable");
            StringAssert.Contains("unverified (site blocks automated checks)", Plain(rows[0].Text));
            Assert.AreEqual("Office page", rows[0].Links[0].Label);
            Assert.AreEqual("Research with sources, not a permit determination or tax advice. Not legal advice; confirm with the permitting office.", v.Label);
        }

        [Test]
        public void RulesMoneyTab()
        {
            var v = RulesView.Parse(GrokG3Examples.Args(GrokG3Examples.RulesDone));
            Assert.AreEqual(3, v.Money.Count);
            var heip = v.Money[0];
            Assert.AreEqual("$500", heip.Amount);
            Assert.AreEqual("not eligible", RulesView.MoneyStatus(heip));
            Assert.AreEqual(ColorRole.TextSecondary, RulesView.MoneyTone(heip));
            Assert.IsTrue(RulesView.ShowsReason(heip));
            var fed = v.Money[1];
            Assert.AreEqual("see page", fed.Amount);
            Assert.AreEqual("ended", RulesView.MoneyStatus(fed));
            Assert.IsFalse(RulesView.ShowsReason(fed));
            var rows = v.MoneyRows(Hex);
            Assert.AreEqual("Ductless Mini-Split Heat Pump Rebate · $500 · not eligible\nneeds the whole ENERGY STAR certified pair; add the KUSAH121B", Plain(rows[0].Text));
            Assert.AreEqual("KUSAH121B", v.EnergyStarMissing);
            Assert.AreEqual("find KUSAH121B", v.FindCommand, "the warning row's button");
            var header = Plain(v.MoneyHeader(Hex));
            StringAssert.Contains("KNSAH121B: ENERGY STAR certified · SEER2 20 · HSPF2 9", header);
            StringAssert.Contains("⚠ Rebate needs the outdoor unit KUSAH121B", header);
            StringAssert.Contains("No rebate counted yet", header, "rebates_usd 0");
            var active = new RulesView.MoneyRow { Status = "active", Eligibility = "eligible" };
            Assert.AreEqual(ColorRole.Success, RulesView.MoneyTone(active), "active green");
            Assert.AreEqual(ColorRole.TextSecondary, RulesView.MoneyTone(new RulesView.MoneyRow { Status = "closed" }), "closed grey");
        }

        [Test]
        public void RulesAreKeptPerPartForTheSpecCardChip()
        {
            GrokState.ResetG3();
            try
            {
                var v = RulesView.Parse(GrokG3Examples.Args(GrokG3Examples.RulesDone));
                Assert.AreEqual(new[] { "lg-knsah121b-04da2e" }, v.PartIds.ToArray());
                GrokState.SetRules(v);
                Assert.AreSame(v, GrokState.RulesFor("lg-knsah121b-04da2e"));
                Assert.IsNull(GrokState.RulesFor("midea-maw08u1qwt-8d6d08"));
                GrokState.SetRules(RulesView.Parse(GrokG3Examples.Args(GrokG3Examples.RulesOffline)));   // asked by words: no part
                Assert.AreSame(v, GrokState.RulesFor("lg-knsah121b-04da2e"));
                Assert.AreEqual("Permit: yes", v.PermitPillText);
            }
            finally { GrokState.ResetG3(); }
        }

        [Test]
        public void RulesOfflineBodyStillShowsWhatItKnows()
        {
            var v = RulesView.Parse(GrokG3Examples.Args(GrokG3Examples.RulesOffline));
            Assert.IsFalse(v.PermitResearched);
            Assert.AreEqual("unknown", v.PermitRequired);
            Assert.AreEqual(ColorRole.TextSecondary, RulesView.PermitTone(v.PermitRequired), "grey");
            Assert.IsTrue(v.CityOnly);
            StringAssert.Contains("Couldn't research the permit rules right now.", Plain(v.PermitHeader(Hex)));
            Assert.AreEqual(0, v.PermitRows(Hex).Count);
            Assert.AreEqual(3, v.Money.Count);
            Assert.AreEqual("unverified", RulesView.MoneyStatus(v.Money[1]));
            Assert.IsTrue(RulesView.ShowsReason(v.Money[1]), "an unverified row says why");
            StringAssert.Contains("unavailable", Plain(v.MoneyHeader(Hex)));
            Assert.IsNull(v.FindCommand);
            StringAssert.EndsWith("Not legal advice; confirm with the permitting office.", v.Label);
        }

        [Test]
        public void RunningRulesBodyIsNotDone()
        {
            var v = RulesView.Parse(GrokG3Examples.Body(GrokG3Examples.RulesRunning));
            Assert.IsFalse(v.Done);
            Assert.IsFalse(v.Failed);
            Assert.AreEqual(GrokG3Examples.Args(GrokG3Examples.RulesStarted)["check_id"].ToString(), v.CheckId);
        }

        // ---------------- show_quote_check ----------------

        [Test]
        public void QuoteRowsFlagsAndLabel()
        {
            var v = QuoteView.Parse(GrokG3Examples.Args(GrokG3Examples.QuoteBad));
            Assert.AreEqual(7, v.Lines.Count);
            Assert.AreEqual("Sample Bros. Heating & Air (fake)", v.Subtitle);
            var line5 = v.Lines[4];
            Assert.AreEqual(5, line5.I);
            Assert.AreEqual("math_line", line5.Flags.Single().Code);
            var t5 = Plain(v.LineText(line5, Hex));
            StringAssert.Contains("5. Wall sleeve / line hide cover", t5);
            StringAssert.Contains("2 × $45.00 = $100.00", t5);
            StringAssert.Contains(" Math ", t5, "the flag as a chip");
            StringAssert.Contains("Line 5: 2 × $45.00 is $90.00, but it says $100.00.", t5, "a warn flag's sentence");
            StringAssert.Contains("<color=#DANGER>Math</color>", v.LineText(line5, Hex), "warn chips are red");
            var line3 = v.Lines[2];
            Assert.AreEqual(new[] { "recall", "price_spread" }, line3.Flags.Select(f => f.Code).ToArray());
            StringAssert.Contains("<color=#TEXTSECONDARY>Price spread</color>", v.LineText(line3, Hex), "info chips are grey");
            StringAssert.Contains("quote $429.00 vs lowest verified seller $379.00 (Home Depot)", Plain(v.LineText(line3, Hex)));
            Assert.IsNull(line3.CompareUrl);
            Assert.AreEqual(0, v.Rows(Hex)[2].Links.Count, "no seller_url: the compare text is plain");
            Assert.AreEqual(new[] { "missing_permit", "rebate" }, v.QuoteFlags.Select(f => f.Code).ToArray(), "line: null flags under the table");
            var notes = Plain(v.Notes(Hex));
            StringAssert.Contains("Subtotal $5,074.00 · tax 8.9% $334.11 · total $5,408.11", notes);
            StringAssert.Contains("No permit line, but Atlanta requires", notes);
            StringAssert.Contains("Research with sources, not a permit determination", notes, "rules.label beside the permit / rebate flags");
            StringAssert.StartsWith("AI reading of the quote: check it against the paper.", v.Label);
            Assert.AreEqual(3, v.WarnCount);
            var clean = QuoteView.Parse(GrokG3Examples.Args(GrokG3Examples.QuoteClean));
            Assert.AreEqual(0, clean.WarnCount, "the clean quote has no warnings");
        }

        [Test]
        public void InferredValuesAreGreyWithATilde()
        {
            var printed = QuoteView.Val.From(Newtonsoft.Json.Linq.JObject.Parse("{\"value\": 45.0, \"source\": \"printed_text\"}"));
            var inferred = QuoteView.Val.From(Newtonsoft.Json.Linq.JObject.Parse("{\"value\": 45.0, \"source\": \"inferred\"}"));
            var missing = QuoteView.Val.From(Newtonsoft.Json.Linq.JObject.Parse("{\"value\": null, \"source\": \"inferred\"}"));
            Assert.AreEqual("$45.00", QuoteView.Value(printed, true, Hex));
            Assert.AreEqual("<color=#TEXTSECONDARY>~$45.00</color>", QuoteView.Value(inferred, true, Hex));
            Assert.AreEqual("<color=#TEXTSECONDARY>?</color>", QuoteView.Value(missing, true, Hex));
            Assert.AreEqual("2", QuoteView.Value(QuoteView.Val.From(Newtonsoft.Json.Linq.JObject.Parse("{\"value\": 2.0, \"source\": \"printed_text\"}")), false, Hex));
        }

        // ---------------- show_reimagined / camera → quad ----------------

        [Test]
        public void ReimagineStepsAndTheUndoHint()
        {
            var two = ReimagineView.Parse(GrokG3Examples.Args(GrokG3Examples.ReimaginedStep2));
            Assert.AreEqual(2, two.Step);
            Assert.IsTrue(two.CanUndo);
            Assert.AreEqual("0301", two.FrameId);
            Assert.AreEqual(500.0, two.Camera.fx);
            Assert.AreEqual("Step 2 · say “undo” to step back", two.StateLine());
            var zero = ReimagineView.Parse(GrokG3Examples.Args(GrokG3Examples.ReimaginedStep0));
            Assert.IsTrue(zero.IsOriginal);
            Assert.AreEqual("/scenes/kitchen/thumbs/0301.jpg", zero.ImageUrl, "step 0 is the thumb");
            Assert.AreEqual("The original photo", zero.StateLine());
            Assert.IsNull(zero.Drift);
            Assert.AreEqual("AI preview, not to scale", zero.Label);
            Assert.AreEqual("/scenes/kitchen/thumbs/0481.jpg", ReimagineView.ThumbUrl("kitchen", "thumbs/0481.jpg", "0481"));
            Assert.AreEqual("/scenes/kitchen/thumbs/0301.jpg", ReimagineView.ThumbUrl("kitchen", null, "0301"), "no camera entry: the thumbs/ convention");
            Assert.IsNull(ReimagineView.ThumbUrl(null, null, "0301"));
        }

        static SceneCameraJson Kitchen0481() => ReimagineView.Parse(GrokG3Examples.Args(GrokG3Examples.ReimaginedKitchen)).Camera;

        [Test]
        public void QuadCoversThePhotosFrustumOnTheKitchenCamera()
        {
            var cam = Kitchen0481();
            Assert.IsTrue(CameraQuad.TryCompute(cam, 1.5f, out var q));
            Assert.IsTrue(Near(q.Width, (float)(1.5 * 1920 / 1457.6137201326615)), $"width {q.Width}");
            Assert.IsTrue(Near(q.Height, (float)(1.5 * 1079 / 1457.6137201326615)), $"height {q.Height}");
            var eye = SceneCameras.Position(cam);
            foreach (var (u, v) in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f), (0.5f, 0.5f), (0.25f, 0.8f) })
            {
                var p = q.Point(u, v);
                var ray = SceneCameras.PixelRay(cam, u, v);   // the path the scene pins use (X flip included)
                Assert.IsTrue(Near(Vector3.Cross((p - eye).normalized, ray.direction), Vector3.zero, 1e-4f), $"({u},{v}) on its pixel ray");
                Assert.Greater(Vector3.Dot(p - eye, ray.direction), 0f, "in front of the camera");
                Assert.IsTrue(Near(Vector3.Dot(p - eye, q.Forward), 1.5f, 1e-3f), "on the plane 1.5 m out");
                Assert.IsTrue(SceneCameras.Project(cam, p, out var uv) && Near(uv.x, u, 1e-3f) && Near(uv.y, v, 1e-3f), $"projects back to ({u},{v})");
            }
        }

        [Test]
        public void QuadBasisIsUnmirroredAfterTheXFlip()
        {
            Assert.IsTrue(CameraQuad.TryCompute(Kitchen0481(), 1.5f, out var q));
            // Unity's LookRotation(Forward, Up) has x = Up × Forward: equal to Right means the image isn't mirrored.
            Assert.IsTrue(Near(Vector3.Cross(q.Up, q.Forward), q.Right, 1e-4f));
            Assert.IsTrue(Near(q.Forward, SceneCameras.DirectionToUnity(Kitchen0481(), 0, 0, 1).normalized, 1e-4f));
            // The top-left pixel sits on the −Right, +Up corner.
            var tl = q.Point(0f, 0f) - q.Centre;
            Assert.Less(Vector3.Dot(tl, q.Right), 0f);
            Assert.Greater(Vector3.Dot(tl, q.Up), 0f);
        }

        [Test]
        public void QuadRegistersOnlyOnTheSameScansCamera()
        {
            var kitchen = Kitchen0481();
            Assert.AreSame(kitchen, CameraQuad.Match(kitchen, Kitchen0481()), "the action's camera sits on the loaded one");
            Assert.AreSame(kitchen, CameraQuad.Match(kitchen, null), "no pose in the action: trust the loaded scan");
            var other = ReimagineView.Parse(GrokG3Examples.Args(GrokG3Examples.ReimaginedStep2)).Camera;   // another scan's 0301 at the origin
            Assert.IsNull(CameraQuad.Match(kitchen, other), "a different scan with the same frame id floats instead");
            Assert.IsNull(CameraQuad.Match(null, kitchen), "no scan loaded");
        }

        [Test]
        public void QuadFallsBackForSparseCameras()
        {
            var sparse = ReimagineView.Parse(GrokG3Examples.Args(GrokG3Examples.ReimaginedStep2)).Camera;   // no cx, cy, w, h
            Assert.IsFalse(CameraQuad.TryCompute(sparse, 1.5f, out _), "no frame size at all");
            Assert.IsTrue(CameraQuad.TryCompute(sparse, 1.5f, out var q, 1280, 720), "the picture's own size");
            Assert.IsTrue(Near(q.Width, 1.5f * 1280f / 500f));
            Assert.IsTrue(Near(q.Centre, new Vector3(0f, 0f, 1.5f)), $"centred on the axis: {q.Centre}");
            Assert.IsTrue(Near(q.Up, new Vector3(0f, -1f, 0f)), "OpenCV y-down → up = −R[1]");
            sparse.fx = 0;
            Assert.IsFalse(CameraQuad.TryCompute(sparse, 1.5f, out _, 1280, 720));
            Assert.IsFalse(CameraQuad.TryCompute(null, 1.5f, out _));
        }

        [Test]
        public void FloatingQuadFacesTheEyes()
        {
            var eye = new Vector3(1f, 1.6f, 2f);
            var q = CameraQuad.Floating(eye, new Vector3(0.3f, -0.4f, 1f), 1.5f, 8f, 1.2f, 848f / 480f);
            Assert.IsTrue(Near((q.Centre - eye).magnitude, 1.5f));
            Assert.Greater(Vector3.Dot(q.Forward, (q.Centre - eye).normalized), 0.9999f, "faces the eyes");
            Assert.IsTrue(Near(Vector3.Cross(q.Up, q.Forward), q.Right, 1e-4f), "unmirrored");
            Assert.Less(q.Centre.y, eye.y, "a little below the eye line");
            Assert.IsTrue(Near(q.Height, 1.2f * 480f / 848f));
        }

        // ---------------- polling (flythrough, rules) ----------------

        [Test]
        public void PollAsksEveryThreeSecondsUntilDone()
        {
            var p = new JobPoll { Interval = 3f, Timeout = 330f };
            p.Start("946e49055025", 100f);
            Assert.AreEqual(PollState.Waiting, p.State);
            Assert.IsFalse(p.Due(102.9f));
            Assert.IsTrue(p.Due(103f));
            Assert.AreEqual(PollState.InFlight, p.State);
            Assert.IsFalse(p.Due(110f), "one request at a time");
            Assert.AreEqual(PollState.Waiting, p.OnReply(200, "pending", 103.4f));
            Assert.IsFalse(p.Due(106.3f));
            Assert.IsTrue(p.Due(106.4f), "3 s after the last answer");
            Assert.AreEqual(PollState.Done, p.OnReply(200, "done", 106.6f));
            Assert.IsFalse(p.Active);
            Assert.IsTrue(p.Finished);
            Assert.AreEqual(2, p.Requests);
        }

        [Test]
        public void PollFailsOnFailedUnknownJobOrRepeatedErrors()
        {
            var p = new JobPoll();
            p.Start("j", 0f, immediate: true);
            Assert.IsTrue(p.Due(0f), "immediate");
            Assert.AreEqual(PollState.Failed, p.OnReply(200, "failed", 0.5f));

            p.Start("j", 0f);
            Assert.IsTrue(p.Due(3f));
            Assert.AreEqual(PollState.Failed, p.OnReply(404, null, 3.1f), "unknown job (server restarted)");

            p.Start("j", 0f);
            Assert.IsTrue(p.Due(3f)); Assert.AreEqual(PollState.Waiting, p.OnReply(0, null, 3f));
            Assert.IsTrue(p.Due(6f)); Assert.AreEqual(PollState.Waiting, p.OnReply(502, null, 6f));
            Assert.IsTrue(p.Due(9f)); Assert.AreEqual(PollState.Waiting, p.OnReply(200, "running", 9f), "an answer resets the error count");
            Assert.AreEqual(0, p.Errors);
            Assert.IsTrue(p.Due(12f)); p.OnReply(0, null, 12f);
            Assert.IsTrue(p.Due(15f)); p.OnReply(0, null, 15f);
            Assert.IsTrue(p.Due(18f));
            Assert.AreEqual(PollState.Failed, p.OnReply(0, null, 18f), "three misses in a row");
        }

        [Test]
        public void PollTimesOutAndCancels()
        {
            var p = new JobPoll { Interval = 3f, Timeout = 90f };
            p.Start("rules", 0f);
            float t = 0f;
            while (t < 200f && p.Active)
            {
                t += 0.5f;
                if (p.Due(t)) p.OnReply(200, "running", t);
            }
            Assert.AreEqual(PollState.TimedOut, p.State);
            Assert.That(t, Is.InRange(90f, 94f));

            p.Start("fly", 0f);
            Assert.IsTrue(p.Due(3f));
            p.Cancel();
            Assert.AreEqual(PollState.Cancelled, p.State);
            Assert.AreEqual(PollState.Cancelled, p.OnReply(200, "done", 3.2f), "a late answer after a cancel changes nothing");
            p.Start(null, 0f);
            Assert.AreEqual(PollState.Failed, p.State, "no job id");
        }

        [Test]
        public void FlythroughStatusesAndTheVideo()
        {
            var pending = FlythroughStatus.Parse(GrokG3Examples.FlyPending);
            Assert.IsFalse(pending.Done || pending.Failed);
            StringAssert.EndsWith(".jpg", pending.PosterUrl, "the poster is known while it renders");
            var done = FlythroughStatus.Parse(GrokG3Examples.FlyDone);
            Assert.IsTrue(done.Done);
            var video = done.ToVideo();
            StringAssert.EndsWith(".mp4", video.VideoUrl);
            Assert.AreEqual("AI preview, not to scale", video.Label);
            Assert.IsNull(video.ShareUrl);
            var failed = FlythroughStatus.Parse(GrokG3Examples.FlyFailed);
            Assert.IsTrue(failed.Failed);
            Assert.AreEqual("I couldn't render that; here's the still.", failed.Spoken);
            Assert.IsNotNull(failed.PosterUrl, "the still to show instead");
            Assert.IsNull(FlythroughStatus.Parse("<html>"));
            var shown = VideoView.Parse(GrokG3Examples.Args(GrokG3Examples.Video));
            Assert.AreEqual(done.VideoUrl, shown.VideoUrl);
            Assert.AreEqual("AI preview, not to scale", shown.Label);
            Assert.AreEqual("946e49055025", GrokG3Examples.Args(GrokG3Examples.FlythroughStarted)["job_id"].ToString());
        }
    }
}
