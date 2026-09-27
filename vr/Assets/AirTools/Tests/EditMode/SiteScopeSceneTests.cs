using System.Collections.Generic;
using System.Linq;
using AirTools.Core;
using AirTools.Editor;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AirTools.Tests
{
    /// sitescope (2), Unity-only (the Editor gate: GameObjects, physics): the real tools on a SceneRoot that switches
    /// between the built-in facade and runtime packages the way SceneLoader / SceneStreamer do (the old content destroyed
    /// after the switch, as Play mode's deferred Destroy does; a runtime arrival held while its scale is restored).
    /// - items are tagged on create, hidden (inactive, no collider) and out of the live lists on another site;
    /// - back on their site they are the same objects at the same poses (and follow a new scale);
    /// - EditHistory's undo on B never touches A, and a redo never resurrects an item into another scan;
    /// - the context's tape, placed parts and gap tapes are the current site's; the notebook panel lists them;
    /// - scene-part removals (and the model in the gap) come back with their site; answer pins park with theirs;
    /// - a demo reset clears every site.
    public class SiteScopeSceneTests
    {
        static PartCatalog s_Catalog;
        GameObject m_RootGo, m_App, m_Content;
        SceneRoot m_Root;
        MeasureTool m_Measure;
        LevelTool m_Level;
        PartTool m_Parts;
        PartLoader m_Loader;
        AirTools.Structure.CalibrationSync m_Sync;

        const string Kitchen = "kitchen", Gym = "zabel-gymnasium";

        [OneTimeSetUp]
        public void LoadCatalog()
        {
            s_Catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath);
            if (s_Catalog == null || s_Catalog.entries.Count < 2) s_Catalog = PartCatalogBuilder.Build();
            PartCatalogBuilder.EnsurePartsLayer();
        }

        [SetUp]
        public void SetUp()
        {
            SiteScope.Reset();
            Notebook.Clear();
            AppState.Reset();
            AirTools.UI.UiSettings.UseUnits(UnitSystem.Metric);
            AirTools.UI.WorldLabels.Reset();
            m_RootGo = new GameObject("SceneRoot");
            m_Root = m_RootGo.AddComponent<SceneRoot>();
            Services.Register(m_Root);
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
            Services.Register(m_Measure); Services.Register(m_Level); Services.Register(m_Parts); Services.Register(m_Loader);
            EditHistory.Register(m_Measure); EditHistory.Register(m_Level); EditHistory.Register(m_Parts);
            m_Measure.RegisterSite(); m_Level.RegisterSite(); m_Parts.RegisterSite();
            LoadBuiltIn();
        }

        [TearDown]
        public void TearDown()
        {
            SiteScope.Reset();
            Gaps.Forget();
            EditHistory.Unregister(m_Measure); EditHistory.Unregister(m_Level); EditHistory.Unregister(m_Parts);
            Services.Unregister(m_Measure); Services.Unregister(m_Level); Services.Unregister(m_Parts); Services.Unregister(m_Loader);
            Services.Unregister(m_Root);
            Object.DestroyImmediate(m_App);
            Object.DestroyImmediate(m_RootGo);   // the tools' views and every part (parked ones too) live under it
            Notebook.Clear();
            AppState.Reset();
            AirTools.UI.WorldLabels.Reset();
        }

        // ---------------- loads, the way the app does them ----------------

        GameObject NewContent(string name)
        {
            var go = SyntheticFacadeBuilder.CreateHierarchy();   // any scan will do: these tests are about the items
            go.name = name;
            go.transform.SetParent(m_RootGo.transform, false);
            return go;
        }

        /// SceneLoader.Load: the built-in facade (Play mode's Destroy of the old content lands after the switch).
        void LoadBuiltIn()
        {
            var old = m_Content;
            if (old != null) old.SetActive(false);
            m_Content = NewContent("SyntheticFacade");
            m_Root.SetContent(null, m_Content);
            if (old != null) Object.DestroyImmediate(old);
            Physics.SyncTransforms();
        }

        /// SceneStreamer.Swap (fresh): new content at ×1, the arrival held while the site's scale is restored.
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

        MeasureShape Tape(Vector3 a, Vector3 b)
        {
            var t = m_RootGo.transform;
            m_Measure.AreaMode = false;
            m_Measure.Click(new SurfaceHit { point = t.TransformPoint(a), rawPoint = t.TransformPoint(a), kind = SnapKind.Face });
            m_Measure.Click(new SurfaceHit { point = t.TransformPoint(b), rawPoint = t.TransformPoint(b), kind = SnapKind.Face });
            Assert.AreEqual(0, m_Measure.Session.Count, "a 2-point tape saves at its 2nd point (D4)");
            return m_Measure.Shapes[m_Measure.Shapes.Count - 1];
        }

        PartInstance Part(Vector3 at)
        {
            var p = m_Loader.LoadFromCatalog(AirTools.Dev.PartScenarios.Ac);
            Assert.IsNotNull(p, m_Loader.LastError);
            m_Parts.PlaceAt(p, at, Quaternion.identity);
            Physics.SyncTransforms();
            return p;
        }

        LevelPlacement Level(Vector3 at) => m_Level.Place(new LevelReading(LevelMode.Level, 0.5, at, Vector3.up, Vector3.forward));

        static bool Hits(PartInstance p, Vector3 from)
        {
            var to = p.transform.position;
            foreach (var h in Physics.RaycastAll(from, (to - from).normalized, 20f, ~0, QueryTriggerInteraction.Collide))
                if (h.collider != null && h.collider.GetComponentInParent<PartInstance>() == p) return true;
            return false;
        }

        // ---------------- tag, hide, show ----------------

        [Test]
        public void ItemsBelongToTheirSite_HiddenUnsnappableAndOutOfTheListsElsewhere()
        {
            var tape = Tape(new Vector3(-1f, 1f, 1f), new Vector3(1f, 1f, 1f));
            var level = Level(new Vector3(0f, 0f, 2f));
            var part = Part(new Vector3(0f, 1f, 3f));
            var eye = m_RootGo.transform.TransformPoint(new Vector3(0f, 1.2f, 6f));
            Assert.IsTrue(Hits(part, eye), "the part's box collides on its own site");
            Assert.AreEqual(ModelSites.BuiltIn, tape.Entry.SiteKey, "tagged on create");
            int rows = Notebook.Entries.Count;

            LoadRuntime(Kitchen, 1.63f);
            Assert.IsFalse(tape.View.gameObject.activeInHierarchy, "tape hidden");
            Assert.IsFalse(level.Gizmo.gameObject.activeInHierarchy, "level hidden");
            Assert.IsFalse(part.gameObject.activeInHierarchy, "part hidden");
            Assert.IsFalse(Hits(part, eye), "no collider: nothing snaps to or fits against another site's part");
            Assert.AreEqual(0, m_Measure.Shapes.Count);
            Assert.AreEqual(0, m_Level.Placements.Count);
            Assert.AreEqual(0, m_Parts.PlacedParts.Count);
            Assert.IsNull(m_Parts.Selected, "nothing selected (context.selected_part_id, placed_box)");
            Assert.AreEqual(rows, Notebook.Entries.Count, "the notebook keeps every row (a log)");
            Assert.IsFalse(tape.Entry.OnCurrentSite);
            Assert.AreEqual(1, m_Measure.ParkedShapes(ModelSites.BuiltIn));
            Assert.AreEqual(1, m_Parts.ParkedParts(ModelSites.BuiltIn));
            Assert.IsNotNull(part, "parked, never destroyed");

            var mine = Tape(new Vector3(0f, 2f, 0f), new Vector3(0.5f, 2f, 0f));
            Assert.AreEqual(Kitchen, mine.Entry.SiteKey, "new items take the current site");
        }

        [Test]
        public void BackOnItsSite_TheSameItemsAtTheSamePoses_AtTheScaleItLoadsWith()
        {
            LoadRuntime(Kitchen, 1.63f);
            var tape = Tape(new Vector3(-0.3f, 0.9f, 1.2f), new Vector3(0.4f, 0.9f, 1.2f));
            var part = Part(new Vector3(0.2f, 0f, 1.5f));
            var pts = (Vector3[])tape.Points.Clone();
            var pose = part.transform.localPosition;
            double length = tape.Entry.ValueSI;

            LoadBuiltIn();
            var facadeTape = Tape(new Vector3(-1f, 1f, 1f), new Vector3(1f, 1f, 1f));
            LoadRuntime(Kitchen, 1.63f);
            Assert.AreSame(tape, m_Measure.Shapes[0], "the same shape");
            Assert.IsTrue(tape.View.gameObject.activeInHierarchy);
            for (int i = 0; i < pts.Length; i++) Assert.AreEqual(pts[i], tape.Points[i], "bit for bit at the scale it was left at");
            Assert.AreEqual(pose, part.transform.localPosition);
            Assert.IsTrue(part.gameObject.activeInHierarchy);
            Assert.IsTrue(m_Parts.PlacedParts.Contains(part));
            Assert.IsFalse(facadeTape.View.gameObject.activeInHierarchy, "the facade's tape waits with the facade");
            Assert.AreEqual(length, tape.Entry.ValueSI, 1e-9);

            // The kitchen comes back at another scale (a person's Set scale saved meanwhile): its items move with the scene.
            LoadBuiltIn();
            LoadRuntime(Kitchen, 1.5f);
            float k = 1.5f / 1.63f;
            for (int i = 0; i < pts.Length; i++) Assert.That(Vector3.Distance(pts[i] * k, tape.Points[i]), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(pose * k, part.transform.localPosition), Is.LessThan(1e-5f));
            Assert.AreEqual(length * k, tape.Entry.ValueSI, 1e-5, "re-measured in scene metres");
        }

        [Test]
        public void UndoneItemsStayHiddenWhenTheirSiteComesBackAtAnotherScale()
        {
            LoadRuntime(Kitchen, 1.63f);
            var level = Level(new Vector3(0.2f, 0.9f, 1f));
            var tape = Tape(new Vector3(-0.3f, 0.9f, 1.2f), new Vector3(0.4f, 0.9f, 1.2f));
            var pts = (Vector3[])tape.Points.Clone();
            Assert.IsTrue(EditHistory.Undo(), "the tape");
            Assert.IsTrue(EditHistory.Undo(), "the level");
            LoadBuiltIn();
            LoadRuntime(Kitchen, 1.5f);   // back at another scale
            Assert.IsFalse(level.Gizmo.gameObject.activeSelf, "an undone level stays hidden");
            Assert.IsFalse(tape.View.gameObject.activeSelf, "an undone tape stays hidden");
            Assert.IsTrue(EditHistory.Redo());
            Assert.IsTrue(EditHistory.Redo());
            Assert.IsTrue(level.Gizmo.gameObject.activeSelf && tape.View.gameObject.activeSelf, "redo on its own site");
            float k = 1.5f / 1.63f;
            Assert.That(Vector3.Distance(new Vector3(0.2f, 0.9f, 1f) * k, level.Reading.Point), Is.LessThan(1e-5f), "undone items moved with the scene");
            for (int i = 0; i < pts.Length; i++) Assert.That(Vector3.Distance(pts[i] * k, tape.Points[i]), Is.LessThan(1e-5f));
        }

        // ---------------- undo / redo ----------------

        [Test]
        public void UndoOnB_NeverTouchesAsItems()
        {
            var tape = Tape(new Vector3(-1f, 1f, 1f), new Vector3(1f, 1f, 1f));
            var part = Part(new Vector3(0f, 1f, 3f));
            LoadRuntime(Kitchen);
            var level = Level(new Vector3(0f, 0f, 1f));
            Assert.IsTrue(EditHistory.Undo(), "the kitchen's level");
            Assert.AreEqual(0, m_Level.Placements.Count);
            Assert.IsFalse(level.Gizmo.gameObject.activeSelf);
            Assert.IsFalse(EditHistory.CanUndo, "the facade's tape and part are out of reach");
            Assert.IsFalse(EditHistory.Undo());
            Assert.AreEqual(1, m_Measure.ParkedShapes(ModelSites.BuiltIn));
            Assert.AreEqual(1, m_Parts.ParkedParts(ModelSites.BuiltIn));

            LoadBuiltIn();
            Assert.IsTrue(part.gameObject.activeSelf && tape.View.gameObject.activeSelf, "never undone");
            Assert.IsTrue(EditHistory.Undo(), "the facade's newest: the part");
            Assert.IsFalse(part.gameObject.activeSelf);
            Assert.AreEqual(1, m_Measure.Shapes.Count, "the tape stays");
            Assert.AreEqual(0, m_Level.Placements.Count, "the kitchen's (undone) level stays with the kitchen");
        }

        [Test]
        public void RedoNeverResurrectsAnItemIntoAnotherScan()
        {
            var tape = Tape(new Vector3(-1f, 1f, 1f), new Vector3(1f, 1f, 1f));
            Assert.IsTrue(EditHistory.Undo());
            Assert.IsTrue(EditHistory.CanRedo);
            LoadRuntime(Kitchen);
            Assert.IsFalse(EditHistory.CanRedo, "the facade's undone tape can't come back in the kitchen");
            Assert.IsFalse(EditHistory.Redo());
            Assert.IsFalse(tape.View.gameObject.activeSelf);
            Tape(new Vector3(0f, 2f, 0f), new Vector3(0.5f, 2f, 0f));   // a new edit here drops the kitchen's redo only
            LoadBuiltIn();
            Assert.IsTrue(EditHistory.CanRedo, "the facade's redo waited");
            Assert.IsTrue(EditHistory.Redo());
            Assert.Contains(tape, (System.Collections.ICollection)new List<MeasureShape>(m_Measure.Shapes));
            Assert.IsTrue(tape.View.gameObject.activeInHierarchy);
            Assert.AreEqual(ModelSites.BuiltIn, tape.Entry.SiteKey);
        }

        // ---------------- the context and the notebook ----------------

        [Test]
        public void ContextAndLookupsCarryOnlyTheCurrentSite()
        {
            var facadeTape = Tape(new Vector3(-1f, 1f, 1f), new Vector3(1f, 1f, 1f));
            var part = Part(new Vector3(0f, 1f, 3f));
            Gaps.RecordMeasured("dw1", new Vector3(0.6f, 0.8f, 0.55f));
            Assert.IsNotNull(PartsClient.MeasurementContext());
            Assert.AreSame(facadeTape.Entry, AirTools.Structure.ScaleCalibration.LatestTape());
            Assert.AreEqual(1, m_Parts.QuantityOf(part.Spec.id));

            LoadRuntime(Kitchen);
            Assert.IsNull(PartsClient.MeasurementContext(), "context.measurement: no kitchen tape yet");
            Assert.IsNull(AirTools.Structure.ScaleCalibration.LatestTape(), "Set scale can't use another scan's tape");
            Assert.AreEqual(0, m_Parts.QuantityOf(part.Spec.id), "context.placed / the checkout: the kitchen's parts");
            Assert.IsFalse(Gaps.TryMeasured("dw1", out _), "another site's dw1 tapes");
            Assert.AreEqual(0, Notebook.OfSite().Count, "the panel lists the kitchen's rows");
            var kitchenTape = Tape(new Vector3(0f, 2f, 0f), new Vector3(0.5f, 2f, 0f));
            Assert.AreSame(kitchenTape.Entry, AirTools.Structure.ScaleCalibration.LatestTape());
            Assert.AreEqual(1, Notebook.OfSite().Count);
            Assert.AreEqual(3, Notebook.Entries.Count, "every row kept");

            LoadBuiltIn();
            Assert.AreSame(facadeTape.Entry, AirTools.Structure.ScaleCalibration.LatestTape());
            Assert.IsTrue(Gaps.TryMeasured("dw1", out var m));
            Assert.AreEqual(0.6f, m.x, 1e-5f);
            Assert.AreEqual(1, m_Parts.QuantityOf(part.Spec.id));
        }

        [Test]
        public void TheNotebookPanelListsTheLoadedSite_AllSitesListsEveryRow()
        {
            var panelGo = new GameObject("NotebookPanel");
            try
            {
                var panel = panelGo.AddComponent<NotebookPanel>();
                Tape(new Vector3(-1f, 1f, 1f), new Vector3(1f, 1f, 1f));
                LoadRuntime(Kitchen);
                Tape(new Vector3(0f, 2f, 0f), new Vector3(0.5f, 2f, 0f));
                Tape(new Vector3(0f, 3f, 0f), new Vector3(0.5f, 3f, 0f));
                Assert.AreEqual(2, panel.Visible.Count, "the kitchen's two");
                panel.SetAllSites(true);
                Assert.AreEqual(3, panel.Visible.Count, "All sites");
                panel.SetAllSites(false);
                LoadBuiltIn();
                Assert.AreEqual(1, panel.Visible.Count, "the facade's one");
            }
            finally { Object.DestroyImmediate(panelGo); }
        }

        // ---------------- scene parts, gap models, pins ----------------

        static GameObject Node(Transform parent, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            return go;
        }

        (GameObject visual, GameObject collision, GameObject cavities) PartsNodes()
        {
            var visual = new GameObject("Mesh r1"); visual.transform.SetParent(m_Content.transform, false);
            var collision = new GameObject("Collision r1"); collision.transform.SetParent(m_Content.transform, false);
            var cavities = new GameObject("Cavities r1"); cavities.transform.SetParent(m_Content.transform, false);
            foreach (var id in new[] { "dw1", "bc1", "rg1", "fr1" })
            {
                Node(visual.transform, $"part_{id}");
                Node(collision.transform, $"part_{id}");
                Node(cavities.transform, $"cavity_{id}");
            }
            return (visual, collision, cavities);
        }

        [Test]
        public void RemovalsAndTheModelInTheGapComeBackWithTheirSite()
        {
            var partsGo = new GameObject("SceneParts");
            var sceneParts = partsGo.AddComponent<SceneParts>();
            sceneParts.sceneRoot = m_Root;
            Services.Register(sceneParts);
            EditHistory.Register(sceneParts);
            try
            {
                var doc = ScenePartsDoc.Parse(ScenePartsParseTests.Kitchen4);
                LoadRuntime(Kitchen);
                var n = PartsNodes();
                sceneParts.Bind(Kitchen, 1, doc, n.visual, n.collision, n.cavities);
                Assert.IsTrue(sceneParts.Remove("dw1"));
                var model = Part(new Vector3(0.3f, 0f, 0.5f));   // the new dishwasher in the gap

                // Another scan (SceneStreamer: Unbind first, then the switch).
                sceneParts.Unbind();
                LoadRuntime(Gym);
                Assert.AreEqual(0, sceneParts.RemovedCount, "the gym has nothing out");
                CollectionAssert.AreEqual(new[] { "dw1" }, sceneParts.SavedRemoved(Kitchen));
                Assert.IsFalse(model.gameObject.activeInHierarchy, "the gap model doesn't leak into the gym");
                Assert.IsFalse(EditHistory.CanUndo, "nothing of the kitchen's to undo in the gym");

                // The kitchen again: dw1 out again (its new nodes hidden), its undo back, the model in its gap.
                sceneParts.Unbind();
                LoadRuntime(Kitchen);
                n = PartsNodes();
                sceneParts.Bind(Kitchen, 1, doc, n.visual, n.collision, n.cavities);
                Assert.IsTrue(sceneParts.IsRemoved("dw1"));
                Assert.IsFalse(n.visual.transform.Find("part_dw1").gameObject.activeSelf, "the dishwasher's mesh is out again");
                Assert.IsTrue(sceneParts.CanUndo, "its removal can still be undone");
                Assert.AreEqual(0, sceneParts.SavedSites);
                Assert.IsTrue(model.gameObject.activeInHierarchy);

                Assert.IsTrue(EditHistory.Undo(), "the newest kitchen edit: the model");
                Assert.IsFalse(model.gameObject.activeSelf);
                Assert.IsTrue(EditHistory.Undo(), "then the removal");
                Assert.IsFalse(sceneParts.IsRemoved("dw1"));
            }
            finally
            {
                EditHistory.Unregister(sceneParts);
                Services.Unregister(sceneParts);
                Object.DestroyImmediate(partsGo);
            }
        }

        [Test]
        public void AnswerPinsParkWithTheirSite()
        {
            var askGo = new GameObject("SceneAsk");
            try
            {
                var ask = askGo.AddComponent<AirTools.Agent.SceneAsk>();
                ask.sceneRoot = m_Root;
                ask.RegisterSite();
                var at = m_RootGo.transform.TransformPoint(new Vector3(0.5f, 1.5f, 0.2f));
                Assert.IsTrue(ask.PinAtFocus(at, "gutter"));
                var pin = ask.Pins[0];
                var local = pin.marker.transform.localPosition;
                LoadRuntime(Kitchen);
                Assert.AreEqual(0, ask.Pins.Count);
                Assert.AreEqual(1, ask.ParkedPins(ModelSites.BuiltIn));
                Assert.IsTrue(pin.marker != null, "not destroyed with the facade's content");
                Assert.IsFalse(pin.marker.activeInHierarchy);
                LoadBuiltIn();
                Assert.AreEqual(1, ask.Pins.Count);
                Assert.AreSame(m_Content.transform, pin.marker.transform.parent, "under the facade's new content");
                Assert.AreEqual(local, pin.marker.transform.localPosition, "same package-frame point");
                Assert.IsTrue(pin.marker.activeInHierarchy);
            }
            finally { Object.DestroyImmediate(askGo); }
        }

        // ---------------- demo reset ----------------

        [Test]
        public void DemoResetClearsEverySite()
        {
            Tape(new Vector3(-1f, 1f, 1f), new Vector3(1f, 1f, 1f));
            LoadRuntime(Kitchen, 1.63f);
            Tape(new Vector3(0f, 2f, 0f), new Vector3(0.5f, 2f, 0f));
            var part = Part(new Vector3(0f, 0f, 1f));
            LoadBuiltIn();
            Assert.AreEqual(2, SiteScope.ParkedCount, "the kitchen's tapes and parts");
            // DemoReset.Run's tools step, then every other site.
            m_Measure.ClearAll(); m_Level.ClearAll(); m_Parts.ClearAll();
            SiteScope.ClearParked();
            Assert.AreEqual(0, SiteScope.ParkedCount);
            Assert.IsTrue(part == null, "the kitchen's part is gone");
            Assert.IsFalse(EditHistory.CanUndo || EditHistory.CanRedo);
            LoadRuntime(Kitchen, 1.63f);
            Assert.AreEqual(0, m_Measure.Shapes.Count, "nothing comes back after a reset");
            Assert.AreEqual(0, m_Parts.PlacedParts.Count);
            Assert.AreEqual(0, Notebook.Entries.Count, "their rows went with them");
        }
    }
}
