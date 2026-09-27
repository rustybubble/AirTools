using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Tests
{
    /// B3 measured mandate (hand-off §5, §7) with an in-memory server: prepare → checks → the 1 s hold → proof.
    public class MandateCheckoutTests : FacadeToolFixture
    {
        GameObject m_Parts;
        PartTool m_Tool;
        PartLoader m_Loader;
        PartsClient m_Client;
        CheckoutPanel m_Checkout;
        readonly List<(string path, JObject body)> m_Sent = new List<(string, JObject)>();
        Func<string, JObject, (long, string)> m_Server;
        static readonly Vector3 Ladder = new Vector3(0.5f, 7.0f, 1.5f);

        const string Checks = @"[
          {""id"": ""price_reread"", ""status"": ""ok"", ""ok"": true, ""detail"": ""$2.98 × 8 from the saved listing""},
          {""id"": ""within_limit"", ""status"": ""ok"", ""ok"": true, ""detail"": ""$23.84 ≤ $40""},
          {""id"": ""delivery"", ""status"": ""ok"", ""ok"": true, ""detail"": ""arrives Thu Oct 1, by Fri Oct 2""},
          {""id"": ""qty_evidence"", ""status"": ""ok"", ""ok"": true, ""detail"": ""tape #3 4.20 m ÷ 600 mm → 8 (reported by headset)""},
          {""id"": ""seller_verified"", ""status"": ""warn"", ""ok"": true, ""detail"": ""Home Depot: listing read by the model, not a seller API""},
          {""id"": ""fit"", ""status"": ""ok"", ""ok"": true, ""detail"": ""green, 38 mm spare""},
          {""id"": ""card"", ""status"": ""ok"", ""ok"": true, ""detail"": ""Visa test card •••• 1111, held by the server""}]";
        const string Intent = @"{""id"": ""int-f1e5b3fb"", ""max_total_usd"": 40.0, ""deliver_by"": ""2026-10-02"", ""seller_policy"": ""fastest"", ""source"": ""panel"", ""text"": ""panel: fastest""}";
        const string Hash = "sha256:e846d33b8d4814db3bdbfa7dd91c7f8774f689e50a494ee27100486e0c063a72";
        int m_Nonce;

        string Prepared(int units, bool allOk = true)
        {
            int packs = units;
            return $@"{{""cart"": {{""id"": ""cart-06182d8e"", ""intent_id"": ""int-f1e5b3fb"",
               ""lines"": [{{""part_id"": ""hidden-hanger-5k"", ""seller_idx"": 0, ""seller"": ""Home Depot"", ""units_needed"": {units}, ""pack_qty"": 1, ""packs"": {packs},
                          ""unit_price_usd"": 2.98, ""shipping_usd"": 0.0, ""line_total_usd"": {2.98 * packs:0.00}}}],
               ""bom_id"": null, ""bom_lines"": [], ""shipping_usd"": 0.0, ""total_usd"": {2.98 * packs:0.00},
               ""evidence"": [{{""notebook_id"": 3, ""label"": ""tape #3"", ""value_m"": 4.2, ""camera_id"": 316, ""photo"": ""/scenes/synthetic-facade/thumbs/0316.jpg"", ""array"": {{""spacing_mm"": 600.0, ""count"": 8}}}}],
               ""created_at"": ""2026-09-26T08:31:26.179791Z""}},
              ""cart_hash"": ""{Hash}"", ""hold_nonce"": ""hn_{++m_Nonce}"", ""nonce_expires_at"": ""2026-09-26T08:33:26.179791+00:00"",
              ""intent"": {Intent},
              ""checks"": {(allOk ? Checks : Checks.Replace(@"""within_limit"", ""status"": ""ok"", ""ok"": true, ""detail"": ""$23.84 ≤ $40""", @"""within_limit"", ""status"": ""fail"", ""ok"": false, ""detail"": ""$23.84 is over your $20 limit"""))},
              ""all_ok"": {(allOk ? "true" : "false")}}}";
        }

        static string Receipt(int holdMs) => $@"{{""status"": ""OFFLINE_RECEIPT"", ""mode"": ""offline"", ""approval_code"": null, ""card_last4"": ""1111"",
            ""total_usd"": 23.84, ""receipt_id"": ""rcpt-1"", ""part_id"": ""hidden-hanger-5k"", ""seller"": ""Home Depot"", ""qty"": 8,
            ""label"": ""Offline receipt: no payment was authorized"",
            ""mandate"": {{""intent"": {Intent},
              ""cart"": {{""id"": ""cart-06182d8e"", ""hash"": ""{Hash}"", ""total_usd"": 23.84, ""units_needed"": 8, ""packs"": 8}},
              ""authorization"": {{""mode"": ""offline"", ""status"": ""OFFLINE_RECEIPT"", ""approval_code"": null, ""hold_ms"": {holdMs}}},
              ""evidence"": [{{""notebook_id"": 3, ""label"": ""tape #3"", ""value_m"": 4.2, ""camera_id"": 316, ""photo"": ""/scenes/synthetic-facade/thumbs/0316.jpg"", ""array"": {{""spacing_mm"": 600.0, ""count"": 8}}}}],
              ""checks"": {Checks}}}}}";

        /// The contract server: prepare → the §5 fixture; checkout with a proof → the §5.3 receipt.
        (long, string) Contract(string path, JObject body)
        {
            if (path == "/checkout/prepare") return (200, Prepared((int)body["units_needed"]));
            if (path == "/checkout") return (200, Receipt((int?)body["hold_ms"] ?? 0));
            return (404, @"{""detail"":""Not Found""}");
        }

        [SetUp]
        public void Build()
        {
            AppCommands.ClearBom();
            Tool.Equip(false);
            var catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath) ?? PartCatalogBuilder.Build();
            m_Parts = new GameObject("b3");
            m_Loader = m_Parts.AddComponent<PartLoader>();
            m_Loader.catalog = catalog;
            m_Tool = m_Parts.AddComponent<PartTool>();
            m_Tool.SetInput(Hub);
            m_Tool.Equip(true);
            m_Client = m_Parts.AddComponent<PartsClient>();
            m_Sent.Clear();
            m_Nonce = 0;
            m_Server = Contract;
            m_Client.testTransport = (path, json) => { var b = JObject.Parse(json); m_Sent.Add((path, b)); return m_Server(path, b); };
            m_Checkout = m_Parts.AddComponent<CheckoutPanel>();
            m_Checkout.client = m_Client;
            m_Checkout.hold = m_Parts.AddComponent<HoldToConfirm>();
            Services.Register(m_Tool); Services.Register(m_Client); Services.Register(m_Checkout);
            m_Checkout.hold.Confirmed += () => typeof(CheckoutPanel).GetMethod("OnHoldConfirmed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(m_Checkout, null);
            SessionInfo.Set("quest-test");
        }

        [TearDown]
        public void Destroy()
        {
            m_Tool.ClearAll();
            Services.Unregister(m_Tool); Services.Unregister(m_Client); Services.Unregister(m_Checkout);
            UnityEngine.Object.DestroyImmediate(m_Parts);
        }

        int Count(string path) => m_Sent.Count(s => s.path == path);

        void EightHangers()
        {
            var p = m_Loader.LoadFromCatalog(PartScenarios.Hanger);
            m_Tool.Hold(p);
            Assert.IsTrue(m_Tool.Release(ToolInputHub.RayPose(Ladder, new Vector3(0.37f, 6.15f, S.FasciaProud))), m_Tool.LastAction);
            Notebook.Add(new NotebookEntry("measure", 4.2, "m", new[] { new Vector3(-2.1f, 6.1f, 0.15f), new Vector3(2.1f, 6.1f, 0.15f) }, DateTime.Now, 316, "gutter"));
            Assert.IsNotNull(m_Tool.PlaceArray(), m_Tool.LastAction);
            Assert.IsTrue(AppCommands.StartCheckout(0));
        }

        [Test]
        public void Open_Prepares_WithTheArrayAsEvidence_AndShowsTheServerTotalAndChecks()
        {
            EightHangers();
            Assert.AreEqual(1, Count("/checkout/prepare"), "prepared on open");
            Assert.AreEqual(0, Count("/checkout"), "opening never pays");
            var req = m_Sent[0].body;
            Assert.AreEqual("quest-test", (string)req["session_id"]);
            Assert.AreEqual(8, (int)req["units_needed"], "pieces, not packs");
            var ev = (JObject)((JArray)req["evidence"])[0];
            int tapeId = Notebook.Entries.First(e => e.Tool == "measure").Id;
            Assert.AreEqual(tapeId, (int)ev["notebook_id"]);
            Assert.AreEqual($"tape #{tapeId}", (string)ev["label"]);
            Assert.AreEqual(4.2, (double)ev["value_m"], 1e-3);
            Assert.AreEqual(316, (int)ev["camera_id"]);
            Assert.AreEqual(600.0, (double)ev["array"]["spacing_mm"], 1e-3);
            Assert.AreEqual(8, (int)ev["array"]["count"]);
            Assert.AreEqual(MandateState.Prepared, m_Checkout.Mandate);
            Assert.IsTrue(m_Checkout.CanPay);
            Assert.AreEqual(23.84f, m_Checkout.DisplayTotal, 1e-3f, "the server's price");
            var rows = Regex.Replace(m_Checkout.ChecksText(), "<[^>]*>", "").Split('\n');
            Assert.AreEqual(7, rows.Length);
            StringAssert.StartsWith("✓ Price re-read · $2.98 × 8", rows[0]);
            StringAssert.StartsWith("⚠ Seller", rows[4], "warn is amber ⚠ and doesn't block");
            Assert.AreEqual("Limits · ≤ $40 · by Fri · fastest", m_Checkout.LimitsText());
        }

        [Test]
        public void ShortHoldSendsNothing_FullHoldSendsExactlyOneCheckoutWithTheProof()
        {
            EightHangers();
            Assert.IsFalse(m_Checkout.hold.Simulate(0.6f));
            Assert.AreEqual(0, Count("/checkout"), "a 0.6 s hold sends nothing");
            Assert.IsTrue(m_Checkout.hold.Simulate(1.0f));
            Assert.AreEqual(1, Count("/checkout"), "a 1.0 s hold sends exactly one /checkout");
            var body = m_Sent.Last(s => s.path == "/checkout").body;
            Assert.AreEqual(Hash, (string)body["cart_hash"]);
            Assert.AreEqual("hn_1", (string)body["hold_nonce"]);
            Assert.GreaterOrEqual((int)body["hold_ms"], 1000);
            Assert.AreEqual(8, (int)body["qty"], "the prepared packs");
            Assert.AreEqual(CheckoutState.Paid, m_Checkout.State);
            Assert.AreEqual("purchase", Notebook.Last.Tool);
        }

        [Test]
        public void AQuantityChangeRePrepares()
        {
            EightHangers();
            m_Checkout.SetQuantity(9);
            Assert.AreEqual(2, Count("/checkout/prepare"));
            Assert.AreEqual(9, (int)m_Sent.Last(s => s.path == "/checkout/prepare").body["units_needed"]);
            Assert.AreEqual("hn_2", m_Checkout.Prepared.hold_nonce, "the new nonce");
            Assert.IsTrue(m_Checkout.hold.Simulate(1.0f));
            Assert.AreEqual(9, (int)m_Sent.Last(s => s.path == "/checkout").body["qty"]);
            Assert.AreEqual("hn_2", (string)m_Sent.Last(s => s.path == "/checkout").body["hold_nonce"]);
        }

        [Test]
        public void ReceiptChainRendersFromTheFixture()
        {
            EightHangers();
            Assert.IsTrue(m_Checkout.hold.Simulate(1.0f));
            var r = m_Checkout.Receipt;
            Assert.IsNotNull(r.mandate);
            Assert.AreEqual("offline", r.mandate.authorization.mode);
            var rows = Regex.Replace(m_Checkout.ChecksText(), "<[^>]*>", "").Split('\n');
            Assert.AreEqual(4, rows.Length);
            Assert.AreEqual("✓ Intent · panel: fastest", rows[0]);
            StringAssert.StartsWith("✓ Cart · …3a72", rows[1]);
            Assert.AreEqual("✗ Authorization · offline, no payment", rows[2]);
            StringAssert.StartsWith("✓ Evidence · tape #3 4.20 m → 8 · photo", rows[3]);
            StringAssert.Contains("Offline receipt: no payment was authorized", CheckoutPanel.ReceiptText(r, m_Checkout.Spec), "the label verbatim");
            // Sandbox: ✓ plus the approval code; no limits: "no limits".
            var sandbox = new ReceiptMandate
            {
                intent = null, cart = new MandateCart { hash = Hash, total_usd = 23.84f },
                authorization = new MandateAuthorization { mode = "sandbox", status = "AUTHORIZED", approval_code = "831000" },
            };
            var s = Regex.Replace(MandateText.Chain(sandbox), "<[^>]*>", "").Split('\n');
            Assert.AreEqual("✓ Intent · no limits", s[0]);
            Assert.AreEqual("✓ Authorization · approval 831000 · sandbox", s[2]);
        }

        [Test]
        public void OldServer_404Prepare_PaysExactlyAsBefore()
        {
            m_Server = (path, body) => path == "/checkout"
                ? (200, @"{""status"":""OFFLINE_RECEIPT"",""mode"":""offline"",""label"":""Offline receipt"",""total_usd"":34.16,""receipt_id"":""r0"",""seller"":""Home Depot"",""qty"":8,""card_last4"":""1111""}")
                : (404, @"{""detail"":""Not Found""}");
            EightHangers();
            Assert.AreEqual(MandateState.Legacy, m_Checkout.Mandate);
            Assert.IsTrue(m_Checkout.CanPay, "Pay enabled");
            Assert.AreEqual("", m_Checkout.ChecksText());
            Assert.AreEqual("", m_Checkout.LimitsText());
            Assert.IsFalse(m_Checkout.hold.Simulate(0.6f));
            Assert.IsTrue(m_Checkout.hold.Simulate(1.0f));
            Assert.AreEqual(1, Count("/checkout"));
            var body = m_Sent.Last(s => s.path == "/checkout").body;
            Assert.IsFalse(body.ContainsKey("hold_nonce"));
            Assert.IsFalse(body.ContainsKey("cart_hash"));
            Assert.IsFalse(body.ContainsKey("hold_ms"));
            Assert.AreEqual(8, (int)body["qty"]);
            Assert.AreEqual(CheckoutState.Paid, m_Checkout.State);
            Assert.IsNull(m_Checkout.Receipt.mandate);
            // No server at all (edit mode, no transport): legacy too, and the hold still gets exactly one attempt.
            m_Client.testTransport = null;
            m_Checkout.Close();
            Assert.IsTrue(AppCommands.StartCheckout(0));
            Assert.AreEqual(MandateState.Legacy, m_Checkout.Mandate);
            Assert.IsTrue(m_Checkout.CanPay);
        }

        [Test]
        public void AFailingCheckDisablesPay_AndTheHoldSendsNothing()
        {
            m_Server = (path, body) => path == "/checkout/prepare" ? (200, Prepared((int)body["units_needed"], allOk: false)) : Contract(path, body);
            EightHangers();
            Assert.AreEqual(MandateState.Refused, m_Checkout.Mandate);
            Assert.IsFalse(m_Checkout.CanPay);
            Assert.AreEqual("$23.84 is over your $20 limit", m_Checkout.FailingDetail);
            StringAssert.Contains("✗ Within your limit · $23.84 is over your $20 limit", Regex.Replace(m_Checkout.ChecksText(), "<[^>]*>", ""));
            Assert.IsTrue(m_Checkout.hold.Simulate(1.0f), "the hold itself completes");
            Assert.AreEqual(0, Count("/checkout"), "…but nothing is sent");
            Assert.AreEqual(1, m_Checkout.RefusedHolds);
            Assert.AreEqual(CheckoutState.Ready, m_Checkout.State);
        }

        [Test]
        public void Refusals_400_409_422_GoBackToReadyAndPrepareAgain()
        {
            foreach (var (code, detail) in new[]
            {
                (400L, @"""hold_ms 600 < 1000: paying needs the full hold"""),
                (409L, @"""hold nonce unknown or already used; reopen checkout"""),
                (422L, @"{""error"": ""a mandate check failed"", ""checks"": [{""id"": ""within_limit"", ""status"": ""fail"", ""ok"": false, ""detail"": ""$23.84 is over your $20 limit""}]}"),
            })
            {
                m_Sent.Clear();
                m_Server = (path, body) => path == "/checkout" ? (code, $@"{{""detail"": {detail}}}") : Contract(path, body);
                if (m_Checkout.State == CheckoutState.Closed || m_Checkout.Spec == null) EightHangers();
                else { m_Checkout.Close(); Assert.IsTrue(AppCommands.StartCheckout(0)); }
                int prepares = Count("/checkout/prepare");
                Assert.IsTrue(m_Checkout.hold.Simulate(1.0f));
                Assert.AreEqual(1, Count("/checkout"), $"{code}: one attempt");
                Assert.AreEqual(CheckoutState.Ready, m_Checkout.State, $"{code}: back to Ready");
                Assert.AreEqual(prepares + 1, Count("/checkout/prepare"), $"{code}: prepared again");
                Assert.AreEqual(code, m_Checkout.LastRefusal.Code);
                if (code == 422) Assert.AreEqual("$23.84 is over your $20 limit", m_Checkout.LastRefusal.Checks[0].detail, "the 422's failing rows");
            }
            // FastAPI's validation 422 (a list) parses without throwing.
            var e = CheckoutError.Parse(422, @"{""detail"":[{""type"":""value_error"",""loc"":[""body""],""msg"":""Value error, bad""}]}");
            Assert.AreEqual("Value error, bad", e.Detail);
            Assert.IsNull(e.Checks);
        }

        [Test]
        public void OnlyTheHoldHandlerSendsAHoldNonce()
        {
            var files = Directory.GetFiles("Assets/AirTools/Runtime", "*.cs", SearchOption.AllDirectories);
            // The request field is written in exactly one place: PartsClient.Checkout, from a HoldProof.
            CollectionAssert.AreEqual(new[] { "PartsClient.cs" }, files.Where(f => File.ReadAllText(f).Contains("\"hold_nonce\"")).Select(Path.GetFileName).ToArray());
            // A HoldProof is made in exactly one place: CheckoutPanel's hold-confirmed handler.
            var makers = files.Where(f => File.ReadAllText(f).Contains("new HoldProof")).Select(Path.GetFileName).ToArray();
            CollectionAssert.AreEqual(new[] { "CheckoutPanel.cs" }, makers);
            var src = File.ReadAllText("Assets/AirTools/Runtime/Parts/CheckoutPanel.cs");
            Assert.AreEqual(1, Regex.Matches(src, @"new HoldProof").Count);
            int handler = src.IndexOf("void OnHoldConfirmed()", StringComparison.Ordinal), next = src.IndexOf("void Pay(HoldProof", StringComparison.Ordinal);
            int made = src.IndexOf("new HoldProof", StringComparison.Ordinal);
            Assert.That(made, Is.GreaterThan(handler).And.LessThan(next), "inside OnHoldConfirmed");
            // The nonce value is read only there (and by CanPay's is-there-one check).
            var reads = Regex.Matches(src, @"\.hold_nonce").Cast<Match>().Select(m => m.Index).ToList();
            int canPay = src.IndexOf("public bool CanPay", StringComparison.Ordinal);
            foreach (int i in reads) Assert.IsTrue((i > handler && i < next) || (i > canPay && i < canPay + 600), $"hold_nonce read at {i}");
            // And the proof checkout is called only from Pay, which only the hold handler reaches with a proof.
            Assert.AreEqual(1, Regex.Matches(src, @"Pay\(new HoldProof").Count);
        }

        [Test]
        public void LimitsChipText()
        {
            Assert.AreEqual("≤ $40 · by Fri · fastest", MandateText.Limits(new MandateIntent { max_total_usd = 40, deliver_by = "2026-10-02", seller_policy = "fastest" }));
            Assert.AreEqual("≤ $23.50", MandateText.Limits(new MandateIntent { max_total_usd = 23.5f }));
            Assert.AreEqual("No limits", MandateText.Limits(new MandateIntent()));
            var a = new AirTools.Agent.AgentAction { name = "show_limits", args = JObject.Parse(@"{""intent_id"":""int-1"",""max_total_usd"":20.0,""deliver_by"":""2026-10-02"",""seller_policy"":null,""refused"":[{""field"":""max_total_usd""}]}") };
            Assert.IsTrue(AgentActions.Execute(a));
            Assert.AreEqual(20f, LimitsChip.Current.max_total_usd);
            Assert.IsTrue(LimitsChip.LastRefused);
            Assert.AreEqual(0, Count("/checkout/prepare"), "no checkout open: nothing to re-prepare");
            EightHangers();
            Assert.IsTrue(AgentActions.Execute(a));
            Assert.AreEqual(2, Count("/checkout/prepare"), "an open checkout re-checks against the new limits");
            LimitsChip.Set(null);
        }
    }
}
