using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Core;
using AirTools.Editor;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AirTools.Tests
{
    /// switchclean (2), Unity-only (the Editor gate: GameObjects, TMP-free windows, physics): a SceneRoot that switches
    /// models the way SceneLoader / SceneStreamer do, the real windows (FloatingWindow-backed panels, the notebook, the
    /// Catalog and Find parts, the checkout with its Pay hold, the status line), the real tools.
    /// - every window open when the model changes is closed and named in the log line; a reload of the same model
    ///   closes nothing;
    /// - the checkout is kept while Pay is held and closes right after the hold ends; a payment being authorized is never
    ///   closed;
    /// - coming back doesn't reopen the spec card, and the ladder card doesn't open for the arriving site's ladder;
    /// - no renderer or collider of another model's measurements (tapes, areas, survey objects, slope tapes, levels,
    ///   parts) draws; the cursor, rubber band, level ghost and a pending measure_edges title are cleared; a tape in
    ///   progress is parked with its model; Undo right after the switch reveals nothing of it;
    /// - an agent reply that arrives after its model was left is dropped (no note, no actions).
    public class SwitchCloseSceneTests
    {
        static PartCatalog s_Catalog;
        GameObject m_RootGo, m_Content, m_App, m_Ui;
        SceneRoot m_Root;
        MeasureTool m_Measure;
        LevelTool m_Level;
        PartTool m_Parts;
        PartLoader m_Loader;
        AirTools.Structure.CalibrationSync m_Sync;
        readonly List<Object> m_Services = new List<Object>();
        bool m_RailWas;

        // The windows.
        CatalogWindow m_Catalog;
        PartsBrowser m_Browser;
        CatalogKeyboard m_Keyboard;
        AirTools.Structure.ScenePanel m_Settings;
        NotebookPanel m_Notebook;
        PlacementPanel m_Adjust;
        SellerPanel m_Sellers;
        CheckoutPanel m_Checkout;
        GrokCard m_GrokCard;
        GrokOverlayCard m_OverlayCard;
        SurveyCard m_SurveyCard;
        CoachRailView m_Coach;
        LadderCard m_LadderCard;
        StatusLine m_Line;

        const string Kitchen = "kitchen", Gym = "zabel-gymnasium";

        [OneTimeSetUp]
        public void LoadCatalog()
        {
            s_Catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath);
            if (s_Catalog == null || s_Catalog.entries.Count < 2) s_Catalog = PartCatalogBuilder.Build();
            PartCatalogBuilder.EnsurePartsLayer();
        }

        T Service<T>(T c) where T : Object { Services.Register(c); m_Services.Add(c); return c; }

        /// A FloatingWindow with a content child (what the builders make), `mainSlot` as its slot says.
        FloatingWindow Window(string name, bool mainSlot)
        {
            var go = new GameObject(name);
            go.transform.SetParent(m_Ui.transform, false);
            var w = go.AddComponent<FloatingWindow>();
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            content.SetActive(false);
            w.content = content;
            w.mainSlot = mainSlot;
            return w;
        }

        T Panel<T>(string name, bool mainSlot, System.Action<T, FloatingWindow> wire) where T : MonoBehaviour
        {
            var w = Window(name, mainSlot);
            var t = w.gameObject.AddComponent<T>();
            wire(t, w);
            return Service(t);
        }

        [SetUp]
        public void SetUp()
        {
            SiteScope.Reset();
            SwitchClose.ResetStats();
            SwitchClose.Install();
            WindowSlot.Reset();
            Notebook.Clear();
            AppState.Reset();
            AppState.Set(AppMode.World);
            UiSettings.UseUnits(UnitSystem.Metric);
            WorldLabels.Reset();
            m_RailWas = GuideRail.Enabled;

            m_RootGo = new GameObject("SceneRoot");
            m_Root = m_RootGo.AddComponent<SceneRoot>();
            Service(m_Root);
            m_App = new GameObject("App");
            m_Measure = m_App.AddComponent<MeasureTool>();
            m_Measure.frame = m_RootGo.transform;
            m_Level = m_App.AddComponent<LevelTool>();
            m_Level.frame = m_RootGo.transform;
            m_Loader = m_App.AddComponent<PartLoader>();
            m_Loader.catalog = s_Catalog;
            m_Parts = m_App.AddComponent<PartTool>();
            m_Parts.frame = m_RootGo.transform;
            m_Sync = m_App.AddComponent<AirTools.Structure.CalibrationSync>();
            m_Sync.sceneRoot = m_Root;
            m_Root.Rescaled += m_Sync.OnRescaled;   // EditMode: OnEnable doesn't run for AddComponent
            Service(m_Measure); Service(m_Level); Service(m_Parts); Service(m_Loader);
            EditHistory.Register(m_Measure); EditHistory.Register(m_Level); EditHistory.Register(m_Parts);
            m_Measure.RegisterSite(); m_Level.RegisterSite(); m_Parts.RegisterSite();

            m_Ui = new GameObject("UI");
            // The Catalog and Find parts (as CatalogSceneTests builds them) with the glass keyboard.
            var cat = new GameObject("Catalog");
            cat.transform.SetParent(m_Ui.transform, false);
            m_Browser = cat.AddComponent<PartsBrowser>();
            m_Catalog = cat.AddComponent<CatalogWindow>();
            m_Browser.catalog = m_Catalog;
            m_Catalog.browser = m_Browser;
            var content = new GameObject("Content");
            content.transform.SetParent(cat.transform, false);
            content.SetActive(false);
            m_Browser.content = content;
            var kb = new GameObject("Keyboard");
            kb.transform.SetParent(content.transform, false);
            m_Keyboard = kb.AddComponent<CatalogKeyboard>();
            var keys = new GameObject("Keys");
            keys.transform.SetParent(kb.transform, false);
            keys.SetActive(false);
            m_Keyboard.content = keys;
            m_Catalog.keyboard = m_Keyboard;
            Service(m_Browser); Service(m_Catalog);

            m_Settings = Panel<AirTools.Structure.ScenePanel>("Settings", false, (p, w) => p.window = w);
            m_Adjust = Panel<PlacementPanel>("Adjust", true, (p, w) => p.window = w);
            m_Sellers = Panel<SellerPanel>("Sellers", true, (p, w) => p.window = w);
            m_Checkout = Panel<CheckoutPanel>("Checkout", true, (p, w) => { p.window = w; p.hold = w.gameObject.AddComponent<HoldToConfirm>(); });
            m_GrokCard = Panel<GrokCard>("GrokCard", true, (p, w) => p.window = w);
            m_OverlayCard = Panel<GrokOverlayCard>("OverlayCard", true, (p, w) => p.window = w);
            m_SurveyCard = Panel<SurveyCard>("SurveyCard", true, (p, w) => p.window = w);
            m_Coach = Panel<CoachRailView>("CoachCard", false, (p, w) => p.window = w);
            m_LadderCard = Panel<LadderCard>("LadderCard", true, (p, w) => p.window = w);
            var nb = new GameObject("Notebook");
            nb.transform.SetParent(m_Ui.transform, false);
            m_Notebook = nb.AddComponent<NotebookPanel>();
            var nbContent = new GameObject("Content");
            nbContent.transform.SetParent(nb.transform, false);
            nbContent.SetActive(false);
            m_Notebook.content = nbContent;
            Service(m_Notebook);
            var line = new GameObject("StatusLine");
            line.transform.SetParent(m_Ui.transform, false);
            m_Line = line.AddComponent<StatusLine>();
            m_Line.MakeCurrent();
            LoadBuiltIn();
        }

        [TearDown]
        public void TearDown()
        {
            SiteScope.Reset();
            SwitchClose.ResetStats();
            WindowSlot.Reset();
            Gaps.Forget();
            GuideRail.Enabled = m_RailWas;
            EditHistory.Unregister(m_Measure); EditHistory.Unregister(m_Level); EditHistory.Unregister(m_Parts);
            foreach (var s in m_Services) if (s != null) Services.Unregister(s);
            m_Services.Clear();
            Object.DestroyImmediate(m_Ui);
            Object.DestroyImmediate(m_App);
            Object.DestroyImmediate(m_RootGo);
            Notebook.Clear();
            AppState.Reset();
            WorldLabels.Reset();
        }

        // ---------------- loads, the way the app does them (SiteScopeSceneTests) ----------------

        GameObject NewContent(string name)
        {
            var go = SyntheticFacadeBuilder.CreateHierarchy();
            go.name = name;
            go.transform.SetParent(m_RootGo.transform, false);
            return go;
        }

        void LoadBuiltIn()
        {
            var old = m_Content;
            if (old != null) old.SetActive(false);
            m_Content = NewContent("SyntheticFacade");
            m_Root.SetContent(null, m_Content);
            if (old != null) Object.DestroyImmediate(old);
            Physics.SyncTransforms();
        }

        void LoadRuntime(string site, float scale = 1f)
        {
            var old = m_Content;
            if (old != null) old.SetActive(false);
            m_Content = NewContent(site);
            SiteScope.HoldArrival();
            m_Root.SetRuntimeContent(site, SceneManifest.Parse(ScenePartsParseTests.ApiMdScene), null, m_Content, null);
            m_Root.SetCalibration(scale);
            SiteScope.Arrive(m_Root.Calibration);
            if (old != null) Object.DestroyImmediate(old);
            Physics.SyncTransforms();
        }

        PartInstance Part(Vector3 at)
        {
            var p = m_Loader.LoadFromCatalog(AirTools.Dev.PartScenarios.Ac);
            Assert.IsNotNull(p, m_Loader.LastError);
            m_Parts.PlaceAt(p, at, Quaternion.identity);
            Physics.SyncTransforms();
            return p;
        }

        MeasureShape Shape(SurveyTag tag, bool area, params Vector3[] pts)
        {
            var t = m_RootGo.transform;
            m_Measure.AreaMode = area || tag != null;
            foreach (var p in pts) m_Measure.Click(new SurfaceHit { point = t.TransformPoint(p), rawPoint = t.TransformPoint(p), kind = SnapKind.Face });
            if (m_Measure.Session.Count >= MeasureMath.MinPoints) m_Measure.Finish(tag);
            m_Measure.AreaMode = false;
            return m_Measure.Shapes[m_Measure.Shapes.Count - 1];
        }

        static void Call(object target, string method, params object[] args) =>
            target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(target, args);

        static (int drawing, int colliding) Census(IEnumerable<GameObject> roots)
        {
            int r = 0, c = 0;
            foreach (var go in roots)
            {
                if (go == null) continue;
                foreach (var x in go.GetComponentsInChildren<Renderer>(true)) if (x.enabled && x.gameObject.activeInHierarchy) r++;
                foreach (var x in go.GetComponentsInChildren<Collider>(true)) if (x.enabled && x.gameObject.activeInHierarchy) c++;
            }
            return (r, c);
        }

        // ---------------- every open UI closes ----------------

        [Test]
        public void EveryWindowUpWhenTheModelChangesIsClosed_AndNamed()
        {
            var part = Part(new Vector3(0f, 1f, 3f));
            Assert.IsTrue(AppCommands.ShowCatalog());
            m_Catalog.Model.SetKeyboard(true);
            m_Keyboard.Show(true);
            m_Settings.window.Open();
            m_Notebook.SetOpen(true);
            m_Notebook.SetAllSites(true);
            m_Parts.Select(part);
            m_Browser.Adopt(new List<PartSummary> { part.Spec.ToSummary() }, "window ac", "catalog");
            m_Coach.window.Open();
            GuideRail.Enabled = true;   // a toast is a flash on the status line
            UiToast.Show("Saved · Width 0.61 m", ColorRole.Success);
            Assert.IsTrue(m_Catalog.IsOpen && m_Keyboard.IsOpen && m_Settings.IsOpen && m_Notebook.IsOpen && m_Coach.IsOpen && UiToast.AnyShowing());

            LoadRuntime(Kitchen);
            Assert.IsFalse(m_Catalog.IsOpen || m_Catalog.Model.IsOpen, "Catalog");
            Assert.IsFalse(m_Catalog.Model.KeyboardOpen || m_Keyboard.IsOpen, "keyboard");
            Assert.IsFalse(m_Settings.IsOpen, "Settings");
            Assert.IsFalse(m_Notebook.IsOpen, "Notebook");
            Assert.IsFalse(m_Notebook.AllSites, "the notebook opens on the arriving model's rows next time");
            Assert.IsNull(m_Parts.Selected, "the spec card");
            Assert.AreEqual(0, m_Browser.Candidates.Count, "Find parts' results for the model left");
            Assert.IsNull(m_Browser.LastQuery);
            Assert.IsFalse(m_Coach.IsOpen, "the coach card");
            Assert.IsFalse(UiToast.AnyShowing(), "the toast");
            Assert.AreEqual("Switched to kitchen: closed [Keyboard, Catalog, Settings, Notebook, Spec card, Find parts, Coach card, Toast]", SwitchClose.LastLine);
            Assert.AreEqual(1, SwitchClose.Runs);

            // Another model with nothing open: an empty close; the same model again (a reload): no switch, nothing closes.
            LoadRuntime(Gym);
            Assert.AreEqual("Switched to zabel-gymnasium: closed []", SwitchClose.LastLine);
            m_Settings.window.Open();
            LoadRuntime(Gym);
            Assert.IsTrue(m_Settings.IsOpen, "a reload of the same model isn't a switch");
            Assert.AreEqual(2, SwitchClose.Runs);
            LoadBuiltIn();
            Assert.IsFalse(m_Settings.IsOpen, "the built-in facade is a model too");
            Assert.AreEqual("Switched to built-in: closed [Settings]", SwitchClose.LastLine);
        }

        [Test]
        public void EveryMainSlotCardClosesOnASwitch()
        {
            var spec = s_Catalog.Spec(AirTools.Dev.PartScenarios.Ac);
            Assert.IsNotNull(spec);
            var cards = new (string name, System.Action open, System.Func<bool> isOpen)[]
            {
                ("Adjust", () => m_Adjust.window.Open(), () => m_Adjust.IsOpen),
                ("Sellers", () => m_Sellers.window.Open(), () => m_Sellers.IsOpen),
                ("Checkout", () => Assert.IsTrue(m_Checkout.Open(spec, 0, 1)), () => m_Checkout.State != CheckoutState.Closed || m_Checkout.window.IsOpen),
                ("Grok card", () => m_GrokCard.window.Open(), () => m_GrokCard.IsOpen),
                ("Overlay card", () => m_OverlayCard.window.Open(), () => m_OverlayCard.IsOpen),
                ("Survey card", () => m_SurveyCard.window.Open(), () => m_SurveyCard.IsOpen),
                ("Ladder card", () => m_LadderCard.window.Open(), () => m_LadderCard.IsOpen),
            };
            var sites = new[] { Kitchen, Gym };
            for (int i = 0; i < cards.Length; i++)
            {
                var (name, open, isOpen) = cards[i];
                open();
                Assert.IsTrue(isOpen(), $"{name} open");
                LoadRuntime(sites[i % 2]);
                Assert.IsFalse(isOpen(), $"{name} closed by the switch");
                CollectionAssert.AreEqual(new[] { name }, SwitchClose.LastClosed, SwitchClose.LastLine);
            }
            Assert.AreEqual(CheckoutState.Closed, m_Checkout.State, "the checkout is closed, not just hidden");
        }

        // ---------------- the checkout ----------------

        void OpenCheckout()
        {
            var spec = s_Catalog.Spec(AirTools.Dev.PartScenarios.Ac);
            Assert.IsTrue(m_Checkout.Open(spec, 0, 1));   // no client: an older server's panel (Legacy); opening never pays
            Assert.AreEqual(CheckoutState.Ready, m_Checkout.State);
        }

        [Test]
        public void TheCheckoutStaysWhilePayIsHeld_AndClosesRightAfterTheHoldEnds()
        {
            OpenCheckout();
            m_Checkout.hold.required = 3600f;
            m_Checkout.hold.PressedOverride = true;
            Call(m_Checkout.hold, "Update");
            Assert.IsTrue(m_Checkout.hold.Holding, "the Pay ring is filling");

            LoadRuntime(Kitchen);
            Assert.AreEqual(CheckoutState.Ready, m_Checkout.State, "not closed mid-hold");
            Assert.IsTrue(m_Checkout.window.IsOpen);
            Assert.IsTrue(m_Checkout.CloseAfterHold);
            CollectionAssert.AreEqual(new[] { "Checkout (holding Pay)" }, SwitchClose.LastKept, SwitchClose.LastLine);
            CollectionAssert.DoesNotContain(SwitchClose.LastClosed, "Checkout");

            m_Checkout.StepCloseAfterHold();
            Assert.AreEqual(CheckoutState.Ready, m_Checkout.State, "still held: it waits");
            m_Checkout.hold.PressedOverride = false;   // let go without paying
            Call(m_Checkout.hold, "Update");
            m_Checkout.StepCloseAfterHold();
            Assert.AreEqual(CheckoutState.Closed, m_Checkout.State, "closed right after the hold ended");
            Assert.IsFalse(m_Checkout.window.IsOpen);
            Assert.IsFalse(m_Checkout.CloseAfterHold);
        }

        [Test]
        public void AHoldThatPaysKeepsItsAnswerUp()
        {
            OpenCheckout();
            m_Checkout.hold.PressedOverride = true;
            m_Checkout.hold.required = 3600f;
            Call(m_Checkout.hold, "Update");
            LoadRuntime(Kitchen);
            Assert.IsTrue(m_Checkout.CloseAfterHold);
            // The hold confirmed and the payment is on its way (the state the panel's Pay sets; nothing is sent here).
            typeof(CheckoutPanel).GetProperty("State").SetValue(m_Checkout, CheckoutState.Paying);
            m_Checkout.StepCloseAfterHold();
            Assert.AreEqual(CheckoutState.Paying, m_Checkout.State, "the payment's answer (receipt or refusal) stays yours to see");
            Assert.IsTrue(m_Checkout.window.IsOpen);
            Assert.IsFalse(m_Checkout.CloseAfterHold, "the deferred close is dropped");
            m_Checkout.hold.PressedOverride = null;
        }

        [Test]
        public void APaymentBeingAuthorizedIsNeverClosed()
        {
            OpenCheckout();
            typeof(CheckoutPanel).GetProperty("State").SetValue(m_Checkout, CheckoutState.Paying);   // what Pay sets; nothing is sent here
            LoadRuntime(Gym);
            Assert.AreEqual(CheckoutState.Paying, m_Checkout.State);
            Assert.IsTrue(m_Checkout.window.IsOpen, "never closed while a payment is being authorized");
            Assert.IsFalse(m_Checkout.CloseAfterHold, "not a hold: it isn't closed after either (the receipt stays)");
            CollectionAssert.AreEqual(new[] { "Checkout (paying)" }, SwitchClose.LastKept, SwitchClose.LastLine);
        }

        // ---------------- nothing reopens on the way back ----------------

        [Test]
        public void ComingBackDoesntReopenTheSpecCardOrTheLadderCard()
        {
            var part = Part(new Vector3(0f, 1f, 3f));
            m_Parts.Select(part);
            LoadRuntime(Kitchen);
            Assert.IsNull(m_Parts.Selected);
            LoadBuiltIn();
            Assert.IsTrue(part.gameObject.activeInHierarchy, "the part is back");
            Assert.IsNull(m_Parts.Selected, "…but not selected: the spec card stays closed");

            // LadderTool.SwitchSite raises Changed(Last) for the arriving site: the card mustn't open for it.
            var ladder = new LadderPlacement { SupportName = "the counter" };
            var owner = new LadderEcho(m_LadderCard, ladder);
            SiteScope.Register(owner);
            LoadRuntime(Gym);
            Assert.IsTrue(owner.Raised, "the owner raised the ladder during the switch");
            Assert.IsFalse(m_LadderCard.IsOpen, "no ladder card for a ladder you didn't just place");
            Call(m_LadderCard, "OnLadderChanged", ladder);
            Assert.IsTrue(m_LadderCard.IsOpen, "outside a switch (a ladder placed now) it opens as before");
            SiteScope.Unregister(owner);
        }

        /// Stands in for LadderTool: raises the card's handler with a ladder while the site switches.
        sealed class LadderEcho : ISiteScoped
        {
            readonly LadderCard m_Card; readonly LadderPlacement m_Ladder;
            public bool Raised;
            public LadderEcho(LadderCard card, LadderPlacement ladder) { m_Card = card; m_Ladder = ladder; }
            public void SwitchSite(string from, string to, float factor)
            {
                if (to == SiteScope.Arriving) return;
                Raised = SiteScope.Switching;
                Call(m_Card, "OnLadderChanged", m_Ladder);
            }
            public void ClearParked() { }
            public int ParkedCount => 0;
        }

        // ---------------- measurements are fully world-model specific ----------------

        [Test]
        public void NoMeasurementOfOneModelDrawsOnAnother_AndNothingLiveCrosses()
        {
            var tape = Shape(null, false, new Vector3(-1f, 1f, 1f), new Vector3(1f, 1f, 1f));
            var area = Shape(null, true, new Vector3(-1f, 0f, 2f), new Vector3(1f, 0f, 2f), new Vector3(1f, 0f, 3f), new Vector3(-1f, 0f, 3f));
            var survey = Shape(new SurveyTag { RequestId = "s1", ObjectId = "o1", Label = "cabinet_door" }, false,
                new Vector3(2f, 0.2f, 1f), new Vector3(2.5f, 0.2f, 1f), new Vector3(2.5f, 0.9f, 1f), new Vector3(2f, 0.9f, 1f));
            var slope = Shape(new SurveyTag { RequestId = "sl1", ObjectId = "e1", Label = "gutter slope", Tape = true }, false,
                new Vector3(-2f, 3f, 0.2f), new Vector3(2f, 2.98f, 0.2f));
            var level = m_Level.Place(new LevelReading(LevelMode.Level, 0.5, new Vector3(0f, 0f, 2f), Vector3.up, Vector3.forward));
            var part = Part(new Vector3(0f, 1f, 3f));
            var mine = new List<GameObject> { tape.View.gameObject, area.View.gameObject, survey.View.gameObject, slope.View.gameObject, level.Gizmo.gameObject, part.gameObject };
            Assert.Greater(Census(mine).drawing, 0);

            // Live on the facade: a tape in progress, the snap cursor, the level's ghost, a pending measure_edges title.
            m_Measure.Equip(true);
            var at = m_RootGo.transform.TransformPoint(new Vector3(0.3f, 1.4f, 1f));
            m_Measure.Click(new SurfaceHit { point = at, rawPoint = at, kind = SnapKind.Face });
            Assert.AreEqual(1, m_Measure.Session.Count);
            typeof(MeasureTool).GetProperty("Cursor").SetValue(m_Measure, (SurfaceHit?)new SurfaceHit { point = at + Vector3.right * 0.2f, kind = SnapKind.Edge });
            m_Measure.Session.Preview = m_Measure.ToFrame(at + Vector3.right * 0.2f);
            m_Measure.NextLabel = "Roof length";
            m_Level.Equip(true);
            typeof(LevelTool).GetProperty("Live").SetValue(m_Level, (LevelReading?)new LevelReading(LevelMode.Level, 1.0, new Vector3(0f, 0f, 1f), Vector3.up, Vector3.forward));
            Call(m_Level, "RefreshGhost");
            var ghost = m_Level.viewRoot.Find("LevelGhost");
            Assert.IsTrue(ghost.gameObject.activeInHierarchy, "the ghost shows the live reading");

            LoadRuntime(Kitchen);
            var c = Census(mine);
            Assert.AreEqual(0, c.drawing, "no renderer of the facade's tapes, area, survey object, slope tape, level or part draws");
            Assert.AreEqual(0, c.colliding, "…or collides (nothing snaps or fits against them)");
            Assert.AreEqual(0, m_Measure.Shapes.Count);
            Assert.AreEqual(0, m_Measure.Session.Count, "the tape in progress didn't come along");
            Assert.IsFalse(m_Measure.Cursor.HasValue, "the snap cursor was on the facade");
            Assert.IsFalse(m_Measure.Session.Preview.HasValue, "the rubber band too");
            Assert.IsNull(m_Measure.NextLabel, "a measure_edges title asked for on the facade doesn't name a kitchen tape");
            var preview = m_Measure.viewRoot.Find("MeasurePreview").GetComponent<MeasureView>();
            Assert.AreEqual(0, preview.PointCount, "the rubber band draws nothing");
            Assert.IsFalse(ghost.gameObject.activeInHierarchy, "the level ghost was the facade's reading");
            Assert.IsFalse(m_Level.Live.HasValue);

            // Undo right after the switch: nothing of the facade's comes back.
            Assert.IsFalse(EditHistory.Undo(), "nothing of the kitchen's to undo");
            Assert.IsFalse(EditHistory.Redo());
            Assert.AreEqual(0, Census(mine).drawing);
            var kitchenTape = Shape(null, false, new Vector3(0f, 2f, 0f), new Vector3(0.5f, 2f, 0f));
            Assert.IsTrue(EditHistory.Undo());
            Assert.IsFalse(kitchenTape.View.gameObject.activeSelf, "Undo takes the kitchen's own tape");
            Assert.AreEqual(0, Census(mine).drawing, "…and never reveals the facade's");
            Assert.IsTrue(EditHistory.Redo());

            // Back: everything as it was, the tape in progress with its one point.
            LoadBuiltIn();
            Assert.AreEqual(6, mine.Count(g => g.activeInHierarchy));
            Assert.AreEqual(1, m_Measure.Session.Count, "parked with its model, not carried");
            Assert.IsFalse(kitchenTape.View.gameObject.activeInHierarchy, "the kitchen's tape stays with the kitchen");
        }

        // ---------------- a running job ----------------

        [Test]
        public void ThePersonsSwitchStopsARunningJob_TheJobsOwnSwitchDoesnt()
        {
            GrokRails.Reset();
            // The real sender (JobCancelClient.Send) on a PartsClient whose transport answers like the patched server.
            var client = m_App.AddComponent<AirTools.Parts.PartsClient>();
            var posts = new List<(string path, string body)>();
            client.testTransport = (path, body) => { posts.Add((path, body)); return (200, "{\"run_id\":\"r1\",\"status\":\"cancelled\",\"cancelled\":true,\"next\":9}"); };
            Service(client);
            AirTools.Core.SessionInfo.Set("quest-test");
            JobCancelClient.ResetStats();
            try
            {
                GuideRail.Enabled = true;   // the toast is a flash on the status line
                LoadRuntime(Kitchen);
                Assert.IsTrue(GrokRails.Apply("job_started", Newtonsoft.Json.Linq.JObject.Parse(
                    "{\"run_id\":\"r1\",\"steps\":[\"remove\",\"measure\",\"search\",\"place\"],\"kind\":\"replace\",\"title\":\"Replace the dishwasher\"}"), 0));
                // The job's own switch (a show_model on its poll page, noted before it runs): it carries on, on the gym.
                GrokRails.Site.NoteIssued(new List<AgentAction> { new AgentAction { name = "show_model", args = Newtonsoft.Json.Linq.JObject.Parse("{\"site\":\"zabel-gymnasium\"}") } },
                    Time.realtimeSinceStartupAsDouble);
                LoadRuntime(Gym);
                Assert.IsTrue(GrokRails.Running, "the job's own switch doesn't stop it");
                Assert.IsTrue(GrokRails.Poll.Active);
                Assert.AreEqual(Gym, GrokRails.Site.Site);
                CollectionAssert.Contains(SwitchClose.LastKept, "Job (its own switch)", SwitchClose.LastLine);

                // The person's switch: stopped on the headset, the strip cancelled, a quiet toast.
                LoadBuiltIn();
                Assert.IsFalse(GrokRails.Running);
                Assert.IsTrue(GrokRails.Job.IsCancelled);
                Assert.IsFalse(GrokRails.Poll.Active, "no more polls");
                CollectionAssert.Contains(SwitchClose.LastClosed, "Job", SwitchClose.LastLine);
                Assert.AreEqual("Stopped the dishwasher job: you switched to the test facade", m_Line.Model.Flash, "shown after the switch cleared the old toasts");
                Assert.IsTrue(m_Line.Model.Flashing(UiClock.Now));
                // The server is told once (not for the job's own switch): POST /job/run/r1/cancel {session_id, reason}.
                Assert.AreEqual(1, posts.Count, string.Join(" | ", posts));
                Assert.AreEqual("/job/run/r1/cancel", posts[0].path);
                var body = Newtonsoft.Json.Linq.JObject.Parse(posts[0].body);
                Assert.AreEqual("quest-test", (string)body["session_id"]);
                Assert.AreEqual("switched to built-in", (string)body["reason"]);
                Assert.AreEqual(1, JobCancelClient.Sent);
                Assert.AreEqual(200, JobCancelClient.LastCode);
            }
            finally { GrokRails.Reset(); JobCancelClient.ResetStats(); AirTools.Core.SessionInfo.Set(null); }
        }

        // ---------------- a late answer ----------------

        [Test]
        public void AnAgentReplyForTheModelLeftIsDropped_ExceptTheSwitchItAskedFor()
        {
            var agent = m_App.AddComponent<AgentClient>();
            var note = new AgentReply { reply = "The roof edge is 12.4 m", actions = { new AgentAction { name = "add_note", args = Newtonsoft.Json.Linq.JObject.Parse("{\"text\":\"roof edge 12.4 m\"}") } } };
            int rows = Notebook.Entries.Count;
            LoadRuntime(Kitchen);
            Call(agent, "Handle", "how long is the roof edge?", note, ModelSites.BuiltIn);
            Assert.AreEqual(1, agent.StaleReplies, "asked on the facade, answered on the kitchen");
            Assert.AreEqual(rows, Notebook.Entries.Count, "its actions don't run here");
            Call(agent, "Handle", "how long is the roof edge?", note, Kitchen);
            Assert.AreEqual(rows + 1, Notebook.Entries.Count, "a reply for the model loaded runs as before");
            Assert.AreEqual(1, agent.StaleReplies);
            Object.DestroyImmediate(agent);
        }
    }
}
