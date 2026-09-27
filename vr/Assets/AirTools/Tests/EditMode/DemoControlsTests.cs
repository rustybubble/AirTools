using System;
using System.IO;
using System.Linq;
using AirTools.Agent.Grok;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Tools;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Tests
{
    /// D7 (UX W1.8) DemoMode: the pure parts (no Unity engine calls, so they also run in the offline runner).
    public class DemoModeTests
    {
        [TestCase("on", true)] [TestCase("ON", true)] [TestCase(" 1 ", true)] [TestCase("true", true)] [TestCase("yes", true)]
        [TestCase("off", false)] [TestCase("0", false)] [TestCase("False", false)] [TestCase("no", false)]
        [TestCase("", null)] [TestCase(null, null)] [TestCase("maybe", null)]
        public void ParsesTheLaunchExtra(string s, bool? expected) => Assert.AreEqual(expected, DemoMode.Parse(s));

        [Test]
        public void OnlyAnOfflineReceiptInDemoModeGetsTheDemoLabel()
        {
            Assert.AreEqual(DemoMode.OfflineReceiptLabel, DemoMode.ReceiptLabel(authorized: false, demo: true));
            Assert.IsNull(DemoMode.ReceiptLabel(authorized: true, demo: true), "a sandbox authorization is labelled by the server");
            Assert.IsNull(DemoMode.ReceiptLabel(authorized: false, demo: false));
            StringAssert.Contains("no payment", DemoMode.OfflineReceiptLabel);
            StringAssert.Contains("Demo", DemoMode.OfflineReceiptLabel);
        }
    }

    /// Declutter S1: Grok hygiene's pure parts (they run in the offline runner too).
    public class GrokHygieneTests
    {
        [SetUp]
        public void SetUp() => GrokRails.Reset();

        [TearDown]
        public void TearDown() => GrokRails.Reset();

        [Test]
        public void TheJobRailNeverShowsInPassthrough()
        {
            Assert.IsFalse(JobRailView.ShowsIn(AppMode.Passthrough));
            Assert.IsTrue(JobRailView.ShowsIn(AppMode.World));
            Assert.IsTrue(JobRailView.ShowsIn(AppMode.Tabletop));
        }

        [Test]
        public void ClearingTheRailsForgetsTheRunAndTheCoach()
        {
            foreach (var a in JArray.Parse(GrokRailFixtures.JobHappy).OfType<JObject>().Take(3))
                GrokRails.Apply((string)a["name"], a["args"] as JObject ?? new JObject(), 1);
            Assert.IsNotNull(GrokRails.Job);
            Assert.IsTrue(GrokRails.Poll.Active, "polling the run");
            GrokRails.Clear();
            Assert.IsNull(GrokRails.Job);
            Assert.IsNull(GrokRails.Coach);
            Assert.IsFalse(GrokRails.Poll.Active, "no more polls for the last judge's run");
            Assert.IsNull(GrokState.RunId);
        }
    }

    /// D7: the reset gestures' pure parts — the 2 s hold (both stick clicks, or the hands' pose) and the hands' "stop" pose.
    public class DemoResetGestureTests
    {
        static readonly Vector3 Head = new Vector3(0f, 1.6f, 0f), Fwd = Vector3.forward;

        static PalmFrame Palm(float x, float y = 1.58f, float z = 0.35f, Vector3? normal = null, Vector3? up = null) =>
            new PalmFrame { Centre = new Vector3(x, y, z), Normal = normal ?? Vector3.forward, Up = up ?? Vector3.up };

        [Test]
        public void StopWithBothHandsIsTheResetPose()
        {
            Assert.IsTrue(ResetPose.Both(Palm(-0.2f), 4, Palm(0.2f), 4, Head, Fwd));
            Assert.IsTrue(ResetPose.Both(Palm(-0.2f, y: 1.52f), 5, Palm(0.25f, y: 1.7f), 4, Head, Fwd), "anywhere from 10 cm under the eyes up");
        }

        [TestCase("palms facing you (Meta's system gesture)")]
        [TestCase("left palm up (the tool ring)")]
        [TestCase("a pinch (index bent)")]
        [TestCase("a poke (only the index straight)")]
        [TestCase("hands at chest height (windows, Pay)")]
        [TestCase("fingers pointing forward (pushing)")]
        [TestCase("hands together")]
        [TestCase("hands out of view")]
        public void EverydayHandsAreNotTheResetPose(string what)
        {
            PalmFrame l = Palm(-0.2f), r = Palm(0.2f);
            int le = 4, re = 4;
            switch (what)
            {
                case "palms facing you (Meta's system gesture)": l.Normal = r.Normal = Vector3.back; break;
                case "left palm up (the tool ring)": l.Normal = Vector3.up; l.Up = Vector3.forward; break;
                case "a pinch (index bent)": re = 3; break;
                case "a poke (only the index straight)": re = 1; break;
                case "hands at chest height (windows, Pay)": l.Centre.y = r.Centre.y = 1.3f; break;
                case "fingers pointing forward (pushing)": l.Up = r.Up = Vector3.forward; l.Normal = r.Normal = Vector3.down; break;
                case "hands together": l.Centre = new Vector3(-0.05f, 1.58f, 0.35f); r.Centre = new Vector3(0.05f, 1.58f, 0.35f); break;
                case "hands out of view": l.Centre = new Vector3(-0.6f, 1.58f, -0.1f); r.Centre = new Vector3(0.6f, 1.58f, -0.1f); break;
            }
            Assert.IsFalse(ResetPose.Both(l, le, r, re, Head, Fwd), what);
        }

        [Test]
        public void FiresOnceAfterTwoSecondsThenWaitsForTheRelease()
        {
            var c = new HoldChord { Seconds = 2f };
            Assert.AreEqual(HoldChord.Change.Started, c.Update(true, 10f));
            Assert.AreEqual(HoldChord.Change.None, c.Update(true, 10.3f));
            Assert.AreEqual(HoldChord.Change.Warned, c.Update(true, 10.5f), "the toast: keep holding · let go to cancel");
            Assert.AreEqual(HoldChord.Change.None, c.Update(true, 11.99f));
            Assert.AreEqual(HoldChord.Change.Fired, c.Update(true, 12.0f));
            Assert.AreEqual(HoldChord.Change.None, c.Update(true, 15f), "still held: no second reset");
            Assert.AreEqual(1, c.Fires);
            c.Update(false, 15.1f); c.Update(false, 15.5f);
            Assert.IsFalse(c.Latched);
            Assert.AreEqual(HoldChord.Change.Started, c.Update(true, 16f));
            Assert.AreEqual(HoldChord.Change.Fired, c.Update(true, 18f));
            Assert.AreEqual(2, c.Fires);
        }

        [Test]
        public void ATrackingBlinkIsForgivenLettingGoCancels()
        {
            var c = new HoldChord { Seconds = 2f, Grace = 0.2f };
            c.Update(true, 0f);
            c.Update(true, 0.6f);
            Assert.AreEqual(HoldChord.Change.None, c.Update(false, 0.7f), "a 0.1 s blink");
            Assert.AreEqual(HoldChord.Change.None, c.Update(true, 0.75f));
            Assert.AreEqual(HoldChord.Change.Fired, c.Update(true, 2.0f));

            var d = new HoldChord { Seconds = 2f, Grace = 0.2f };
            d.Update(true, 0f);
            d.Update(true, 1.0f);
            d.Update(false, 1.1f);
            Assert.AreEqual(HoldChord.Change.Cancelled, d.Update(false, 1.25f));
            Assert.IsTrue(d.CancelledAfterWarning);
            Assert.AreEqual(HoldChord.Change.Started, d.Update(true, 1.3f), "starts over");
            Assert.AreEqual(HoldChord.Change.Warned, d.Update(true, 2.5f), "1.2 s since the new start: warned, not fired");
            Assert.AreEqual(0, d.Fires);
        }

        [Test]
        public void HandsMustHoldStill()
        {
            var t = new HandsResetTracker();
            Vector3 l = new Vector3(-0.2f, 1.6f, 0.35f), r = new Vector3(0.2f, 1.6f, 0.35f);
            Assert.AreEqual(HoldChord.Change.Started, t.Update(true, l, r, 0f));
            t.Update(true, l, r, 1.0f);
            var moved = r + new Vector3(0.2f, 0f, 0f);   // waving
            Assert.AreEqual(HoldChord.Change.Started, t.Update(true, l, moved, 1.2f), "moved 20 cm: starts over from here");
            Assert.AreEqual(HoldChord.Change.Warned, t.Update(true, l, moved, 2.1f), "0.9 s since the restart: not 2 s since the first start");
            Assert.AreEqual(HoldChord.Change.Fired, t.Update(true, l, moved + new Vector3(0f, 0.05f, 0f), 3.2f), "5 cm of tremor is still holding");
        }
    }

    /// D7: the presenter protocol on the headset side (pure: runs offline) and the "never pays" guards.
    public class PresenterTests
    {
        static PresenterCommand C(string cmd, string arg = null) => new PresenterCommand("c1", cmd, arg);

        [Test]
        public void EveryListedCommandAndBeatIsValid()
        {
            foreach (var cmd in PresenterCommands.Commands)
            {
                string arg = cmd == "beat" ? "measure" : cmd == "demo" ? "on" : null;
                Assert.IsNull(PresenterCommands.Validate(C(cmd, arg)), cmd);
            }
            foreach (var beat in PresenterCommands.Beats) Assert.IsNull(PresenterCommands.Validate(C("beat", beat)), beat);
            CollectionAssert.AreEqual(new[] { "measure", "find", "take", "place", "sellers", "home", "report", "replace" }, PresenterCommands.Beats,
                "§2.7 order (e2e's replace flow after it), and the same list as tools/presenter/presenter_server.py BEATS");
        }

        [TestCase("pay")] [TestCase("PAY")] [TestCase("payment")] [TestCase("checkout")] [TestCase("start_checkout")] [TestCase("check-out")]
        [TestCase("hold")] [TestCase("hold_pay")] [TestCase("buy")] [TestCase("purchase")] [TestCase("order")] [TestCase("charge")]
        [TestCase("visa")] [TestCase("receipt")] [TestCase("use_offline_receipt")]
        public void PayingIsRefusedAsACommandOrAnArgument(string word)
        {
            Assert.AreEqual(PresenterCommands.Refusal, PresenterCommands.Validate(C(word)));
            Assert.AreEqual(PresenterCommands.Refusal, PresenterCommands.Validate(C("beat", word)));
            Assert.AreEqual(PresenterCommands.Refusal, PresenterCommands.Validate(C("reset", "then " + word)));
            StringAssert.Contains("never pays", PresenterCommands.Refusal);
        }

        [Test]
        public void UnknownOrMalformedCommandsAreRejected()
        {
            StringAssert.StartsWith("unknown command", PresenterCommands.Validate(C("teleport")));
            Assert.IsNotNull(PresenterCommands.Validate(C("beat")));
            Assert.IsNotNull(PresenterCommands.Validate(C("beat", "fly")));
            Assert.IsNotNull(PresenterCommands.Validate(C("demo", "maybe")));
            Assert.IsNotNull(PresenterCommands.Validate(C("hint", "now")));
            Assert.IsNull(PresenterCommands.Validate(new PresenterCommand("c9", " Beat ", " SELLERS ")), "trimmed, any case");
        }

        [Test]
        public void ParsesTheRelaysQueue()
        {
            var list = PresenterCommands.ParseNext("{\"commands\":[{\"id\":\"c1\",\"cmd\":\"reset\",\"arg\":null,\"age_s\":0.2},{\"id\":\"c2\",\"cmd\":\"beat\",\"arg\":\"find\"}]}");
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual(("c1", "reset", (string)null), (list[0].Id, list[0].Cmd, list[0].Arg));
            Assert.AreEqual(("c2", "beat", "find"), (list[1].Id, list[1].Cmd, list[1].Arg));
            Assert.AreEqual(0, PresenterCommands.ParseNext("{\"commands\":[]}").Count);
            Assert.AreEqual(0, PresenterCommands.ParseNext("<html>").Count);
            Assert.AreEqual(0, PresenterCommands.ParseNext(null).Count);
        }

        [TestCase("http://127.0.0.1:8000", "http://127.0.0.1:8766")]
        [TestCase("http://localhost:8000/", "http://localhost:8766")]
        [TestCase("http://172.20.10.2:8000", "http://172.20.10.2:8766")]
        [TestCase("https://laptop.local", "https://laptop.local:8766")]
        [TestCase("", "http://127.0.0.1:8766")]
        [TestCase("not a url", "http://127.0.0.1:8766")]
        public void TheRelaySitsNextToThePartsServer(string server, string expected) => Assert.AreEqual(expected, PresenterCommands.BaseUrl(server));

        [Test]
        public void RulesMapToTheJudgesBeats()
        {
            Assert.AreEqual("0:00 start", PresenterCommands.BeatOf(RuleId.R05, AppMode.Passthrough, false));
            Assert.AreEqual("0:20 measure", PresenterCommands.BeatOf(RuleId.R52, AppMode.World, true));
            Assert.AreEqual("0:40 find parts", PresenterCommands.BeatOf(RuleId.R42, AppMode.World, true));
            Assert.AreEqual("1:35 sellers", PresenterCommands.BeatOf(RuleId.R29, AppMode.World, true));
            Assert.AreEqual("1:50 judge pays", PresenterCommands.BeatOf(RuleId.R10, AppMode.World, true));
            Assert.AreEqual("2:30 report", PresenterCommands.BeatOf(RuleId.R04, AppMode.Passthrough, true));
            // Every beat the page can skip to has a rule that reports it (the page highlights the current one).
            foreach (var beat in PresenterCommands.Beats.Where(b => b != "take" && b != "place" && b != "replace"))   // e2e: replace isn't a judge's beat
                Assert.IsTrue(Enum.GetValues(typeof(RuleId)).Cast<RuleId>().Any(r => (PresenterCommands.BeatOf(r, AppMode.World, true).Split(' ').ElementAtOrDefault(1) ?? "") == beat), beat);
        }

        /// Grep-style guard: nothing in the presenter's code path names a way to pay — not the checkout panel or its
        /// hold, not the parts client's checkout request, not StartCheckout / Pay / UseOfflineReceipt, not the step
        /// dispatcher (which can open the checkout), not the harness's HoldPay. Comments and string literals are
        /// ignored (the refusal words live in strings).
        [Test]
        public void PresenterLinkCannotPay()
        {
            var files = new[] { "Assets/AirTools/Runtime/Dev/PresenterLink.cs", "Assets/AirTools/Runtime/Dev/PresenterCommands.cs", "Assets/AirTools/Runtime/Dev/DemoBeats.cs" };
            var forbidden = new[] { "StartCheckout", "CheckoutPanel", "HoldToConfirm", "HoldTimer", "PartsClient", ".Checkout(", "Simulate(", "PressedOverride",
                "PayRow", "HoldPay", "UseOfflineReceipt", "NextStepActions", "StepCommand", "AgentHarness", "DemoWalkthrough", "BackendHarness",
                "OnHoldConfirmed", "CheckoutMandate", "SellerPanel", "hold." };
            foreach (var f in files)
            {
                Assert.IsTrue(File.Exists(f), f);
                string code = CodeOnly(File.ReadAllText(f));
                foreach (var token in forbidden) StringAssert.DoesNotContain(token, code, $"{Path.GetFileName(f)} names {token}");
            }
        }

        /// The source without comments and string literals (enough C# for these three files). An interpolated string's
        /// holes ({…}) are code and stay.
        static string CodeOnly(string src)
        {
            var sb = new System.Text.StringBuilder(src.Length);
            int n = src.Length;
            for (int i = 0; i < n; i++)
            {
                char c = src[i];
                if (c == '/' && i + 1 < n && src[i + 1] == '/') { while (i < n && src[i] != '\n') i++; sb.Append('\n'); continue; }
                if (c == '/' && i + 1 < n && src[i + 1] == '*') { i += 2; while (i + 1 < n && !(src[i] == '*' && src[i + 1] == '/')) i++; i++; continue; }
                if (c == '@' && i + 1 < n && src[i + 1] == '"')
                {
                    i += 2;
                    while (i < n && !(src[i] == '"' && (i + 1 >= n || src[i + 1] != '"'))) i += src[i] == '"' ? 2 : 1;
                    sb.Append("\"\"");
                    continue;
                }
                if (c == '$' && i + 1 < n && src[i + 1] == '"')
                {
                    i += 2;
                    sb.Append("$\"");
                    while (i < n && src[i] != '"')
                    {
                        if (src[i] == '\\') { i += 2; continue; }
                        if (src[i] == '{' && i + 1 < n && src[i + 1] == '{') { i += 2; continue; }
                        if (src[i] == '{')
                        {
                            int depth = 0;
                            for (; i < n; i++)
                            {
                                if (src[i] == '{') depth++;
                                else if (src[i] == '}' && --depth == 0) { sb.Append('}'); break; }
                                sb.Append(src[i]);
                            }
                        }
                        i++;
                    }
                    sb.Append('"');
                    continue;
                }
                if (c == '"') { i++; while (i < n && src[i] != '"') i += src[i] == '\\' ? 2 : 1; sb.Append("\"\""); continue; }
                if (c == '\'' && i + 2 < n) { int j = i + 1; if (src[j] == '\\') j++; j++; if (j < n && src[j] == '\'') { i = j; sb.Append("' '"); continue; } }
                sb.Append(c);
            }
            return sb.ToString();
        }

        [Test]
        public void TheGuardReallyStripsOnlyCommentsAndStrings()
        {
            string code = CodeOnly("var a = \"StartCheckout\"; // CheckoutPanel\n/* HoldToConfirm */ x.Checkout(1); char q = '\"'; var r = @\"Pay\"\"x\"; " +
                                   "var i = $\"PayRow {AppCommands.UseOfflineReceipt()} {{HoldPay}}\";");
            StringAssert.DoesNotContain("StartCheckout", code);
            StringAssert.DoesNotContain("CheckoutPanel", code);
            StringAssert.DoesNotContain("HoldToConfirm", code);
            StringAssert.DoesNotContain("PayRow", code);
            StringAssert.DoesNotContain("HoldPay", code);
            StringAssert.Contains(".Checkout(", code, "code outside strings and comments is still checked");
            StringAssert.Contains("UseOfflineReceipt", code, "a call inside an interpolated string's hole is code");
        }
    }

    /// D7: PresenterLink refuses a pay / checkout / hold command before anything runs (a component, no network).
    public class PresenterLinkRefusalTests
    {
        [Test]
        public void TheLinkRefusesToPay()
        {
            var go = new GameObject("presenter-link-test");
            try
            {
                var link = go.AddComponent<PresenterLink>();
                foreach (var cmd in new[] { "pay", "checkout", "hold", "start_checkout", "use_offline_receipt" })
                {
                    Assert.IsFalse(link.Execute(new PresenterCommand("local-" + cmd, cmd, null), out var detail), cmd);
                    Assert.AreEqual(PresenterCommands.Refusal, detail);
                }
                Assert.IsFalse(link.Execute(new PresenterCommand("local-b", "beat", "pay"), out _));
                Assert.AreEqual(6, link.Refused);
                Assert.IsFalse(link.LastOk);
                Assert.IsTrue(link.Execute(new PresenterCommand("local-p", "ping", null), out var pong), pong);
                StringAssert.StartsWith("pong", pong);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }

    /// D7: AppCommands.ResetDemo after a populated session (tapes finished and in progress, a level, a placed part + an
    /// array, an undone edit on the redo stack, a note, a BOM, a purchase, accessibility on, coach shows) returns the
    /// first-run state, keeps the exported notebook files, and refuses while a payment is being authorized.
    public class ResetDemoTests : FacadeToolFixture
    {
        GameObject m_App;
        ToolManager m_Tools;
        LevelTool m_Level;
        PartTool m_Parts;
        PartLoader m_Loader;
        PartsClient m_Client;
        CheckoutPanel m_Checkout;
        TakeItHome m_Home;
        PartsBrowser m_Browser;
        GuideRail m_Rail;
        string m_Dir;

        static readonly Vector3 Ladder = new Vector3(0.5f, 7.0f, 1.5f);

        [SetUp]
        public void Build()
        {
            AppCommands.ClearBom();
            DemoMode.On = true;   // prefs stay in memory (PlayerPrefs untouched by this test)
            m_App = new GameObject("d7-app");
            var catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath) ?? PartCatalogBuilder.Build();
            m_Level = m_App.AddComponent<LevelTool>();
            m_Level.SetInput(Hub);
            m_Loader = m_App.AddComponent<PartLoader>();
            m_Loader.catalog = catalog;
            m_Parts = m_App.AddComponent<PartTool>();
            m_Parts.SetInput(Hub);
            m_Client = m_App.AddComponent<PartsClient>();
            m_Checkout = m_App.AddComponent<CheckoutPanel>();
            m_Checkout.client = m_Client;
            m_Checkout.hold = m_App.AddComponent<HoldToConfirm>();
            m_Home = m_App.AddComponent<TakeItHome>();
            m_Browser = m_App.AddComponent<PartsBrowser>();
            m_Browser.client = m_Client;
            m_Rail = m_App.AddComponent<GuideRail>();
            m_Tools = m_App.AddComponent<ToolManager>();
            m_Tools.measure = Tool; m_Tools.level = m_Level; m_Tools.parts = m_Parts;
            // OnEnable doesn't run in edit mode: register and subscribe by hand.
            Services.Register(m_Tools); Services.Register(Tool); Services.Register(m_Level); Services.Register(m_Parts);
            Services.Register(m_Client); Services.Register(m_Checkout); Services.Register(m_Home); Services.Register(m_Browser);
            Services.Register(m_Rail);
            EditHistory.Register(Tool); EditHistory.Register(m_Level); EditHistory.Register(m_Parts);
            m_Checkout.hold.Confirmed += () => typeof(CheckoutPanel).GetMethod("OnHoldConfirmed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(m_Checkout, null);
            m_Dir = Path.Combine(Path.GetTempPath(), "airtools-d7-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        [TearDown]
        public void Destroy()
        {
            m_Parts.ClearAll(); m_Level.ClearAll();
            EditHistory.Unregister(Tool); EditHistory.Unregister(m_Level); EditHistory.Unregister(m_Parts);
            Services.Unregister(m_Tools); Services.Unregister(Tool); Services.Unregister(m_Level); Services.Unregister(m_Parts);
            Services.Unregister(m_Client); Services.Unregister(m_Checkout); Services.Unregister(m_Home); Services.Unregister(m_Browser);
            Services.Unregister(m_Rail);
            if (m_Level.viewRoot != null) UnityEngine.Object.DestroyImmediate(m_Level.viewRoot.gameObject);
            UnityEngine.Object.DestroyImmediate(m_App);
            AppCommands.ClearBom();
            DemoMode.ResetToDefault();
            GuideRail.Enabled = false;
            UiSettings.ResetCache();
            if (Directory.Exists(m_Dir)) Directory.Delete(m_Dir, true);
        }

        PartInstance HangerOnFascia(float x)
        {
            var p = m_Loader.LoadFromCatalog(PartScenarios.Hanger);
            m_Parts.Hold(p);
            Assert.IsTrue(m_Parts.Release(ToolInputHub.RayPose(Ladder, new Vector3(x, 6.15f, S.FasciaProud))), m_Parts.LastAction);
            return p;
        }

        /// A judge's worth of state: every kind of thing ResetDemo must take away.
        void Populate()
        {
            AppState.Set(AppMode.World);
            m_Tools.Equip(ToolKind.Measure);
            var win = MeasureScenarios.M2().First(s => s.Id == "M2.measure.window.width");
            Assert.IsTrue(MeasureScenarios.Run(win, Tool, Hub, null, 1).Passed, "tape");
            m_Tools.Equip(ToolKind.Level);
            Assert.IsTrue(LevelScenarios.Run(LevelScenarios.M3()[0], m_Level, Hub, null, 1).Passed, "level");
            m_Tools.Equip(ToolKind.Measure);
            Click(new Vector3(0f, 4.1f, 2.5f), new Vector3(-0.7f, 4.6f, -0.1f));   // a tape in progress: one point on the glass
            m_Tools.Equip(ToolKind.Part);   // … parked by the tool switch (UX W0.4)
            HangerOnFascia(0.37f);
            Notebook.Add(new NotebookEntry("measure", 4.2, "m", new[] { new Vector3(-2.1f, 6.1f, 0.15f), new Vector3(2.1f, 6.1f, 0.15f) }, DateTime.Now, -1, "gutter"));
            Assert.IsNotNull(m_Parts.PlaceArray(), m_Parts.LastAction);
            m_Parts.Undo();   // the array, undone: something on the redo stack (the part tool's own undo: edit-mode time doesn't advance)
            Assert.IsTrue(EditHistory.CanRedo);
            AppCommands.AddNote("check the flashing");
            AppCommands.ShowBom(new PartBom { bom_id = "bom-1", total_usd = 3.5f, lines = { new PartBomLine { idx = 0, name = "screws", qty = 8 } } });
            var spec = PartSpec.Parse(File.ReadAllText($"{PartCatalogBuilder.PartsFolder}/{PartScenarios.Hanger}/part.json"));
            m_Home.OnPurchased(new CheckoutReceipt { status = "OFFLINE_RECEIPT", mode = "offline", qty = 8, total_usd = 34.16f, seller = "Home Depot", receipt_id = "r1" }, spec);
            UiSettings.HighContrast = true;
            m_Rail.CoachState.Shows[(int)CoachRuleId.C03] = 2;
        }

        [Test]
        public void ResetReturnsTheFirstRunState()
        {
            Populate();
            Assert.Greater(Notebook.Entries.Count, 4);
            Assert.AreEqual(1, Tool.Session.Count, "one point of a tape in progress (parked)");
            Assert.IsTrue(EditHistory.CanUndo);
            Assert.IsTrue(EditHistory.CanRedo);
            var (csv, html) = NotebookExporter.Export(Notebook.Entries, _ => null, m_Dir, "before the reset");
            int lastId = Notebook.Entries.Max(e => e.Id);
            string session = SessionInfo.Id;

            Assert.IsTrue(AppCommands.ResetDemo(), DemoReset.LastReport);

            Assert.IsTrue(DemoReset.Verify(out var detail), detail);
            Assert.AreEqual(AppMode.Passthrough, AppState.Mode);
            Assert.AreEqual(ToolKind.None, m_Tools.Active, "no tool: entering the world equips ToolManager.Default (D1)");
            Assert.AreEqual(0, Tool.Shapes.Count);
            Assert.AreEqual(0, Tool.Session.Count, "the tape in progress is gone too");
            Assert.IsFalse(Tool.AreaMode);
            Assert.AreEqual(0, m_Level.Placements.Count);
            Assert.AreEqual(0, m_Parts.PlacedParts.Count);
            Assert.IsNull(m_Parts.Held);
            Assert.AreEqual(0, Notebook.Entries.Count, "notes, BOM and tapes: the session's entries");
            Assert.IsFalse(EditHistory.CanUndo, "undo history empty");
            Assert.IsFalse(EditHistory.CanRedo, "redo history empty");
            Assert.IsNull(AppCommands.CurrentBom);
            Assert.AreEqual(0, m_Home.Purchases.Count);
            Assert.AreEqual(CheckoutState.Closed, m_Checkout.State);
            Assert.IsNull(m_Browser.LastQuery);
            Assert.AreEqual(0, m_Browser.Candidates.Count);
            Assert.IsFalse(UiSettings.HighContrast);
            Assert.AreEqual(0, m_Rail.CoachState.Shows[(int)CoachRuleId.C03], "coach counts reset");
            Assert.AreEqual(0, m_Rail.RingOpensAll);
            Assert.AreNotEqual(session, SessionInfo.Id, "a new server session for the next judge");
            Assert.IsTrue(File.Exists(csv) && File.Exists(html), "the exported notebook files on disk stay");

            // The next judge's readings keep counting up (the laptop upload sends only ids it hasn't had).
            AppCommands.AddNote("next judge");
            Assert.Greater(Notebook.Last.Id, lastId);
        }

        /// Declutter S1: the last judge's job rail, install coach and Grok cards never greet the next judge.
        [Test]
        public void ResetForgetsTheLastJudgesGrok()
        {
            GrokRails.Reset();
            var cardGo = new GameObject("d7-grok-card");
            var card = cardGo.AddComponent<GrokCard>();
            card.window = cardGo.AddComponent<FloatingWindow>();
            var overlayGo = new GameObject("d7-overlay-card");
            var overlayCard = overlayGo.AddComponent<GrokOverlayCard>();
            overlayCard.window = overlayGo.AddComponent<FloatingWindow>();
            overlayCard.window.mainSlot = false;   // two cards open at once, to see both close
            Services.Register(card); Services.Register(overlayCard);
            try
            {
                Populate();
                foreach (var a in JArray.Parse(GrokRailFixtures.JobHappy).OfType<JObject>().Take(7))
                    GrokRails.Apply((string)a["name"], a["args"] as JObject ?? new JObject(), 1);
                var coach = JArray.Parse(GrokRailFixtures.CoachFaucet).OfType<JObject>().First()["actions"].OfType<JObject>().First(x => (string)x["name"] == "coach_started");
                Assert.IsTrue(GrokRails.Apply("coach_started", coach["args"] as JObject, 1));
                card.window.Open();
                overlayCard.window.Open();
                Assert.IsNotNull(GrokRails.Job);
                Assert.IsFalse(DemoReset.Verify(out var before));
                StringAssert.Contains("grok", before);
                StringAssert.Contains("job", DemoReset.GrokLeft());

                Assert.IsTrue(AppCommands.ResetDemo(), DemoReset.LastReport);

                Assert.IsNull(GrokRails.Job, "no job rail for the next judge");
                Assert.IsNull(GrokRails.Coach, "no install coach for the next judge");
                Assert.IsFalse(card.IsOpen, "the Grok card closed");
                Assert.IsFalse(overlayCard.IsOpen, "the overlay card closed");
                Assert.AreEqual("", DemoReset.GrokLeft());
                Assert.IsTrue(DemoReset.Verify(out var detail), detail);
            }
            finally
            {
                Services.Unregister(card); Services.Unregister(overlayCard);
                UnityEngine.Object.DestroyImmediate(cardGo); UnityEngine.Object.DestroyImmediate(overlayGo);
                GrokRails.Reset();
                WindowSlot.Reset();
            }
        }

        [Test]
        public void ResetTwiceIsHarmless()
        {
            Populate();
            Assert.IsTrue(AppCommands.ResetDemo());
            Assert.IsTrue(AppCommands.ResetDemo());
            Assert.IsTrue(DemoReset.Verify(out var detail), detail);
        }

        [Test]
        public void ResetRefusesWhileAPaymentIsBeingAuthorized()
        {
            m_Tools.Equip(ToolKind.Part);
            HangerOnFascia(0.37f);
            Assert.IsTrue(AppCommands.StartCheckout(0));
            m_Checkout.hold.Simulate(1.0f);
            Assume.That(m_Checkout.State == CheckoutState.Paying, $"the hold didn't start a payment in edit mode ({m_Checkout.State}, mandate {m_Checkout.Mandate})");
            int placed = m_Parts.PlacedParts.Count;
            Assert.IsFalse(AppCommands.ResetDemo());
            StringAssert.StartsWith("refused", DemoReset.LastReport);
            Assert.AreEqual(placed, m_Parts.PlacedParts.Count, "nothing was cleared");
        }
    }
}
