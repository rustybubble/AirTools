using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AirTools.Core;
using AirTools.Scene;
using AirTools.UI;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// modelview (Model view shows the model only; the model switcher). Pure: the fit, the switcher's list, order,
    /// cycling, matching and load queue, the step-in re-base, the mode gate and the census of world text owners against
    /// the code (run from the project root) — all in the offline runner. modelwheel: the floating placement and the wheel
    /// are ModelWheelTests (pure); the scene checks are in ModelViewSceneTests (Unity only).
    public class ModelViewTests
    {
        // ---------------- the fit ----------------

        [Test]
        public void TheFitPutsTheLongestSideAt60To80Centimetres()
        {
            Assert.AreEqual(6, TabletopFit.Denominator(4.2f), "a 4.2 m kitchen: 1:6 (0.70 m)");
            Assert.AreEqual(75, TabletopFit.Denominator(50.2f), "Zabel's 50 m: 1:75 (0.67 m)");
            Assert.AreEqual(150, TabletopFit.Denominator(110f), "the hospital's 110 m: 1:150 (0.73 m)");
            Assert.AreEqual(10, TabletopFit.Denominator(8f), "the 8 m test facade: 1:10 (0.80 m)");
            Assert.AreEqual(1, TabletopFit.Denominator(0.5f), "already small: true size");
            Assert.AreEqual(0, TabletopFit.Denominator(0f), "nothing to fit");
            Assert.AreEqual(0, TabletopFit.Denominator(float.NaN));
            Assert.AreEqual(1500, TabletopFit.Denominator(1100f), "past the ladder: the next multiple of 250");
            for (float l = 2.4f; l < 4000f; l *= 1.07f)
            {
                int n = TabletopFit.Denominator(l);
                float side = l / n;
                Assert.LessOrEqual(side, TabletopFit.DefaultMax + 1e-4f, $"{l:0.0} m at 1:{n}");
                Assert.GreaterOrEqual(side, TabletopFit.DefaultMin - 1e-3f, $"{l:0.0} m at 1:{n} is only {side:0.00} m");
                Assert.AreEqual(1f / n, TabletopFit.ScaleFor(n, 0.02f), 1e-7f);
            }
            Assert.AreEqual(0.02f, TabletopFit.ScaleFor(0, 0.02f), "no fit: the fixed scale");
            Assert.AreEqual(12f, TabletopFit.LongestSide(new Vector3(5f, 12f, 3f)), "a tower is fitted by its height");
            Assert.AreEqual("1:75", TabletopFit.Label(75));
            Assert.AreEqual("1:1", TabletopFit.Label(1));
            Assert.AreEqual("Model 1:75", TabletopFit.ScaleWord("1:75"));
            Assert.AreEqual("", TabletopFit.ScaleWord(""));
        }

        [Test]
        public void AFlatGroundPlateIsSkippedButABigScanCounts()
        {
            Assert.IsTrue(TabletopFit.IsGroundPlate(new Vector3(15f, 0.025f, 12.5f)), "the synthetic facade's ground");
            Assert.IsFalse(TabletopFit.IsGroundPlate(new Vector3(25f, 10f, 20f)), "a gym scan (the old > 6 m rule dropped it)");
            Assert.IsFalse(TabletopFit.IsGroundPlate(new Vector3(2f, 1.3f, 1.6f)), "a kitchen");
            Assert.IsFalse(TabletopFit.IsGroundPlate(new Vector3(4f, 0.01f, 4f)), "small and flat: not the big plate");
        }

        /// On a table (the M7 placement, now an option) the model still stands back from the eye.
        [Test]
        public void OnATableTheModelStandsBackFromTheEye()
        {
            Assert.AreEqual(0.55f, TabletopFit.CentreDistance(0.55f, 0.1f, 0.32f), 1e-6f, "a shallow model: as before");
            Assert.AreEqual(0.72f, TabletopFit.CentreDistance(0.55f, 0.4f, 0.32f), 1e-6f, "a deep one stands back");
        }

        // ---------------- stepping in at the chosen model's spawn ----------------

        [Test]
        public void TheRebasedPosePutsTheSpawnUnderYourFeetLookingYourWay()
        {
            var feetLocal = new Vector3(3f, 0f, -2f);
            const float spawnYaw = 30f, heading = 95f;
            var feetWorld = new Vector3(1f, 0.1f, 5f);
            foreach (float ws in new[] { 1f, 1.5f })
            {
                var pose = TabletopFit.Rebased(feetLocal, spawnYaw, ws, feetWorld, heading);
                var landed = pose.position + pose.rotation * (feetLocal * ws);
                Assert.Less(Vector3.Distance(landed, feetWorld), 1e-4f, $"scale {ws}: the spawn is under your feet");
                var look = pose.rotation * (TabletopFit.Yaw(spawnYaw) * Vector3.forward);
                Assert.AreEqual(heading, TabletopFit.Heading(look), 1e-3f, "the spawn's view is along yours");
            }
            Assert.AreEqual(90f, TabletopFit.Heading(TabletopFit.Yaw(90f) * Vector3.forward), 1e-4f, "Yaw is Euler(0, deg, 0)");
            Assert.AreEqual(0f, TabletopFit.Heading(Vector3.forward), 1e-6f);
        }

        // ---------------- the switcher's models ----------------

        static readonly string[] Listing =
        {
            "gt-lcc-canopy", "gt-lcc-pavilion", "gt-lcc-tower", "hospital-bg", "kitchen", "synthetic-facade", "synthetic-facade-parts",
            "synthetic-facade-preview", "synthetic-facade-small", "zabel-gymnasium",
        };

        [Test]
        public void TheSwitcherListsTheScansInOrderThenTheBuiltInFacade()
        {
            CollectionAssert.AreEqual(new[] { "kitchen", "zabel-gymnasium", "hospital-bg", "gt-lcc-canopy", "gt-lcc-pavilion", "gt-lcc-tower", ModelSites.BuiltIn },
                ModelSites.Order(Listing), "test packages out, the built-in facade last");
            CollectionAssert.AreEqual(new[] { "kitchen", "aaa-new", "zzz-new", ModelSites.BuiltIn }, ModelSites.Order(new[] { "zzz-new", "kitchen", "aaa-new", "kitchen", "built-in" }),
                "unknown scans A–Z after the known ones; no duplicates");
            CollectionAssert.AreEqual(new[] { ModelSites.BuiltIn }, ModelSites.Order(null), "no server: the built-in facade");
            foreach (var hidden in new[] { "synthetic-facade", "synthetic-facade-parts", "synthetic-facade-preview", "synthetic-facade-small" })
                Assert.IsTrue(ModelSites.Hidden(hidden), hidden);
            Assert.IsFalse(ModelSites.Hidden("kitchen"));
        }

        [Test]
        public void CardsCarryShortFriendlyNames()
        {
            var names = ModelSites.Order(Listing).Select(ModelSites.Name).ToArray();
            CollectionAssert.AreEqual(new[] { "Kitchen", "Zabel gym", "Hospital", "GT canopy", "GT pavilion", "GT tower", "Test facade" }, names);
            Assert.AreEqual("Zabel gym", Copy.SiteName("zabel-gymnasium"), "reads mid-sentence: Loading the Zabel gym…");
            Assert.AreEqual("hospital", Copy.SiteName("hospital-bg"));
            Assert.AreEqual("GT tower", Copy.SiteName("gt-lcc-tower"));
            Assert.AreEqual("GT new building", Copy.SiteName("gt-lcc-new-building"));
            Assert.AreEqual("test facade", Copy.SiteName(null));
            Assert.AreEqual("kitchen", Copy.SiteName("kitchen"));
            foreach (var n in names) Assert.LessOrEqual(n.Length, 11, $"\"{n}\" fits one card line");
        }

        [Test]
        public void CyclingWrapsBothWays()
        {
            var list = ModelSites.Order(Listing);
            Assert.AreEqual("zabel-gymnasium", ModelSites.Step(list, "kitchen", +1));
            Assert.AreEqual(ModelSites.BuiltIn, ModelSites.Step(list, "kitchen", -1), "previous from the first: the last");
            Assert.AreEqual("kitchen", ModelSites.Step(list, ModelSites.BuiltIn, +1), "next from the last: the first");
            Assert.AreEqual("gt-lcc-tower", ModelSites.Step(list, ModelSites.BuiltIn, -1));
            Assert.AreEqual("kitchen", ModelSites.Step(list, "synthetic-facade-parts", +1), "from a hidden package: the start");
            Assert.AreEqual(ModelSites.BuiltIn, ModelSites.Step(list, "synthetic-facade-parts", -1));
            Assert.IsNull(ModelSites.Step(new List<string>(), "kitchen", 1));
            // Walking the whole list comes back round.
            string s = "kitchen";
            for (int i = 0; i < list.Count; i++) s = ModelSites.Step(list, s, +1);
            Assert.AreEqual("kitchen", s);
        }

        [Test]
        public void TheWindowKeepsTheChosenCardInView()
        {
            Assert.AreEqual(0, ModelSites.WindowStart(7, 0, 4));
            Assert.AreEqual(0, ModelSites.WindowStart(7, 2, 4));
            Assert.AreEqual(1, ModelSites.WindowStart(7, 3, 4));
            Assert.AreEqual(3, ModelSites.WindowStart(7, 6, 4), "the end: the last four");
            Assert.AreEqual(0, ModelSites.WindowStart(3, 2, 4), "fewer models than cards");
            for (int f = 0; f < 7; f++)
            {
                int start = ModelSites.WindowStart(7, f, 4);
                Assert.That(f, Is.InRange(start, start + 3), $"card {f} in view");
            }
        }

        [Test]
        public void SpokenNamesFindTheirModel()
        {
            Assert.AreEqual("zabel-gymnasium", ModelSites.Match(Listing, "show me the gym model"));
            Assert.AreEqual("zabel-gymnasium", ModelSites.Match(Listing, "Zabel gym"));
            Assert.AreEqual("zabel-gymnasium", ModelSites.Match(Listing, "gymnasium"));
            Assert.AreEqual("zabel-gymnasium", ModelSites.Match(Listing, "zabel-gymnasium"));
            Assert.AreEqual("gt-lcc-tower", ModelSites.Match(Listing, "GT tower"));
            Assert.AreEqual("gt-lcc-tower", ModelSites.Match(Listing, "the tower"));
            Assert.AreEqual("gt-lcc-pavilion", ModelSites.Match(Listing, "pavilion"));
            Assert.AreEqual("hospital-bg", ModelSites.Match(Listing, "Hospital"));
            Assert.AreEqual("kitchen", ModelSites.Match(Listing, "kitchen model"));
            Assert.AreEqual(ModelSites.BuiltIn, ModelSites.Match(Listing, "facade"), "the facade offered is the built-in one");
            Assert.AreEqual(ModelSites.BuiltIn, ModelSites.Match(Listing, "test facade"));
            Assert.AreEqual("synthetic-facade-parts", ModelSites.Match(Listing, "synthetic-facade-parts"), "a hidden package by its id");
            Assert.IsNull(ModelSites.Match(Listing, "cathedral"));
            Assert.IsNull(ModelSites.Match(Listing, "the model"), "nothing but filler");
            Assert.IsNull(ModelSites.Match(Listing, ""));
        }

        [Test]
        public void TheSiteThumbnailIsThePhotoNearestTheSpawn()
        {
            var positions = new[] { new Vector3(10f, 0f, 0f), new Vector3(1f, 1.6f, 4f), new Vector3(-3f, 2f, 1f) };
            Assert.AreEqual(1, ModelSites.Nearest(positions, new Vector3(0f, 1.6f, 4f)));
            Assert.AreEqual(-1, ModelSites.Nearest(new Vector3[0], Vector3.zero));
        }

        // ---------------- the load queue (a card click calls Load) ----------------

        [Test]
        public void ACardClickLoadsThatModelAndTheLastTapWins()
        {
            var q = new ModelSwitchState();
            var started = new List<string>();
            bool Start(string s) { started.Add(s); return true; }

            Assert.AreEqual("chose zabel-gymnasium", q.Choose("zabel-gymnasium", "kitchen"));
            Assert.AreEqual(ModelSwitchState.Outcome.None, q.Pump(false, "kitchen", settled: false, 0f, Start), "not on the table yet: waits");
            Assert.AreEqual(ModelSwitchState.Outcome.Started, q.Pump(false, "kitchen", true, 1f, Start));
            CollectionAssert.AreEqual(new[] { "zabel-gymnasium" }, started, "the click called Load for its site");
            Assert.AreEqual("zabel-gymnasium", q.Loading);

            // Taps while it loads: they wait, the last one wins, the loading one isn't disturbed.
            Assert.AreEqual("hospital-bg up next", q.Choose("hospital-bg", "kitchen"));
            q.Choose("gt-lcc-tower", "kitchen");
            Assert.AreEqual("gt-lcc-tower", q.Want);
            Assert.AreEqual(ModelSwitchState.Outcome.None, q.Pump(true, "kitchen", true, 2f, Start));
            Assert.AreEqual(1, started.Count, "nothing else starts while the streamer loads");

            Assert.AreEqual(ModelSwitchState.Outcome.Loaded, q.Pump(false, "zabel-gymnasium", true, 4.5f, Start));
            Assert.AreEqual("zabel-gymnasium", q.LastSite);
            Assert.AreEqual(3.5f, q.LastSeconds, 1e-4f);
            Assert.AreEqual(ModelSwitchState.Outcome.Started, q.Pump(false, "zabel-gymnasium", true, 5f, Start));
            CollectionAssert.AreEqual(new[] { "zabel-gymnasium", "gt-lcc-tower" }, started);

            // It fails: remembered (the card says Retry) until it's chosen again.
            Assert.AreEqual(ModelSwitchState.Outcome.Failed, q.Pump(false, "zabel-gymnasium", true, 9f, Start));
            CollectionAssert.Contains(q.Failed, "gt-lcc-tower");
            Assert.AreEqual(ModelCardState.Failed, ModelSites.StateOf("gt-lcc-tower", "zabel-gymnasium", q.Loading, q.Want, q.Failed));
            Assert.AreEqual("Retry", ModelSites.CardText("GT tower", ModelCardState.Failed));
            q.Choose("gt-lcc-tower", "zabel-gymnasium");
            Assert.AreEqual(ModelSwitchState.Outcome.Started, q.Pump(false, "zabel-gymnasium", true, 10f, Start), "retry");
            CollectionAssert.DoesNotContain(q.Failed, "gt-lcc-tower");
            Assert.AreEqual(ModelCardState.Loading, ModelSites.StateOf("gt-lcc-tower", "zabel-gymnasium", q.Loading, q.Want, q.Failed));
            Assert.AreEqual(ModelSwitchState.Outcome.Loaded, q.Pump(false, "gt-lcc-tower", true, 14f, Start));
            Assert.AreEqual(2, q.Loads);
            Assert.AreEqual(1, q.Failures);
        }

        [Test]
        public void TheModelOnTheTableAndTheBuiltInFacade()
        {
            var q = new ModelSwitchState();
            var started = new List<string>();
            bool Start(string s) { started.Add(s); return true; }
            Assert.AreEqual("already showing kitchen", q.Choose("kitchen", "kitchen"));
            Assert.AreEqual(ModelSwitchState.Outcome.None, q.Pump(false, "kitchen", true, 0f, Start));
            q.Choose(ModelSites.BuiltIn, "kitchen");
            Assert.AreEqual(ModelSwitchState.Outcome.Loaded, q.Pump(false, "kitchen", true, 1f, Start), "the built-in loads at once");
            Assert.IsNull(q.Loading);
            // A busy streamer: the choice waits for the next step.
            q.Choose("hospital-bg", ModelSites.BuiltIn);
            Assert.AreEqual(ModelSwitchState.Outcome.Busy, q.Pump(false, null, true, 2f, _ => false));
            Assert.AreEqual("hospital-bg", q.Want);
            q.Forget();
            Assert.IsNull(q.Want, "left Model view: nothing waits");
            // Card words.
            Assert.AreEqual(ModelCardState.Current, ModelSites.StateOf("kitchen", "kitchen", null, null, null));
            Assert.AreEqual(ModelCardState.Queued, ModelSites.StateOf("kitchen", "x", "y", "kitchen", null));
            Assert.AreEqual("Loading…", ModelSites.CardText("Kitchen", ModelCardState.Loading));
            Assert.AreEqual("Up next", ModelSites.CardText("Kitchen", ModelCardState.Queued));
            Assert.AreEqual("Kitchen", ModelSites.CardText("Kitchen", ModelCardState.Current));
            Assert.AreEqual("built-in", ModelSites.Current(false, "kitchen"));
            Assert.AreEqual("kitchen", ModelSites.Current(true, "kitchen"));
        }

        // ---------------- the model-only gate ----------------

        [Test]
        public void LabelSuppressionTogglesWithTheMode()
        {
            ModelView.Reset();
            try
            {
                Assert.IsFalse(ModelView.Hides(AppMode.World, false));
                Assert.IsFalse(ModelView.Hides(AppMode.Passthrough, false));
                Assert.IsTrue(ModelView.Hides(AppMode.Tabletop, false), "on the way to the table");
                Assert.IsTrue(ModelView.Hides(AppMode.Tabletop, true));
                Assert.IsTrue(ModelView.Hides(AppMode.World, true), "still on the table while it grows back");
                var seen = new List<bool>();
                ModelView.Changed += seen.Add;
                ModelView.Set(true);
                ModelView.Set(true);
                ModelView.Set(false);
                CollectionAssert.AreEqual(new[] { true, false }, seen, "raised on change only");
                Assert.IsFalse(ModelView.HidesAnnotations);
            }
            finally { ModelView.Reset(); }
        }

        // ---------------- the census of world text owners ----------------

        /// Owners whose annotations live under another owner's root (ModelViewDeclutter.CollectRoots walks the parent).
        static readonly Dictionary<string, string> UnderRootOf = new Dictionary<string, string>
        {
            { "MeasureView", "MeasureTool" }, { "SurveyRunner", "MeasureTool" }, { "LevelGizmo", "LevelTool" },
            { "LadderView", "LadderTool" }, { "WorldChip", "GrokOverlays" },
        };

        /// Every runtime file that puts text into the scene (MeasureLabel.Create, WorldChip.Create, UiText.Create outside
        /// the UI) is a Model view owner (hidden) or listed as staying; every owner is a real type, and the declutter walks
        /// each one's root. A new owner that skips this list fails here.
        [Test]
        public void EveryWorldTextOwnerIsInTheCensusAndWalked()
        {
            const string runtime = "Assets/AirTools/Runtime";
            Assert.IsTrue(Directory.Exists(runtime), "run from the project root");
            var owners = new HashSet<string>(ModelView.Owners);
            var stays = new HashSet<string>(ModelView.Stays.Select(s => s.owner));
            // UI surfaces that build their own text (windows, the wrist, the palm, the status line) aren't world text.
            var ui = new HashSet<string> { "UiText", "MeasureLabel", "WorldChip" };
            var creators = new SortedSet<string>();
            foreach (var file in Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (ui.Contains(name)) continue;
                string code = File.ReadAllText(file);
                bool world = Regex.IsMatch(code, @"MeasureLabel\.Create\(|WorldChip\.Create\(") ||
                             (Regex.IsMatch(code, @"UiText\.Create\(") && !file.Replace('\\', '/').Contains("/UI/"));
                if (world) creators.Add(name);
            }
            Assert.Greater(creators.Count, 8, "found the world text owners: " + string.Join(", ", creators));
            var missing = creators.Where(c => !owners.Contains(c) && !stays.Contains(c)).ToList();
            Assert.IsEmpty(missing, "world text owners Model view doesn't know (add them to ModelView.Owners and ModelViewDeclutter.CollectRoots): " + string.Join(", ", missing));
            // The builder-made world annotations: the coach's stop card, the truth bar's label, the pin.
            CollectionAssert.IsSubsetOf(new[] { "CoachOverlay", "DrillCrosshair" }, ModelView.Owners);
            CollectionAssert.IsSubsetOf(new[] { "TruthBar", "SpawnMarker", "ModelSwitcher", "ModelWheel" }, stays.ToList());

            // One MonoBehaviour per file, named after the class: each owner is a real runtime type.
            var files = new HashSet<string>(Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension));
            foreach (var o in owners) Assert.IsTrue(files.Contains(o), $"{o} is a runtime type ({o}.cs)");
            string declutter = File.ReadAllText($"{runtime}/Scene/ModelViewDeclutter.cs");
            foreach (var o in owners)
            {
                string walked = UnderRootOf.TryGetValue(o, out var parent) ? parent : o;
                StringAssert.Contains(walked, declutter, $"ModelViewDeclutter walks {o}'s annotations ({walked})");
            }
        }

        /// The "You are here" pin hides while its chip would sit on a wheel card (the built-in facade at 1:10: the
        /// spawn, 4 m out front, lands on the lens card).
        [Test]
        public void ThePinHidesOnlyOverAWheelCard()
        {
            var half = new Vector2(0.15f, 0.118f) * (0.5f * 1.12f) + new Vector2(0.04f, 0.013f);   // the lens card + the chip
            Assert.IsTrue(ModelWheel.OnCard(new Vector2(0.01f, 0.02f), Vector2.zero, half), "on the lens card: hidden");
            Assert.IsTrue(ModelWheel.OnCard(new Vector2(0.12f, -0.07f), Vector2.zero, half), "the chip's corner over the card's");
            Assert.IsFalse(ModelWheel.OnCard(new Vector2(0.2f, 0f), Vector2.zero, half), "beside it: shown");
            Assert.IsFalse(ModelWheel.OnCard(new Vector2(0f, 0.09f), Vector2.zero, half), "above it: shown");
        }
    }

    /// modelview, the scene checks (Unity only): Model view hides every world text owner's labels and lines and brings them
    /// back exactly; a new scan is re-fitted and stepping in lands at its spawn; the switcher as Wire builds it.
    public class ModelViewSceneTests
    {
        GameObject m_Root, m_Rig, m_Head, m_App;
        SceneRoot m_Scene;
        TabletopController m_Table;
        AirTools.Input.ToolInputHub m_Hub;
        AirTools.Tools.MeasureTool m_Measure;
        AirTools.Tools.LevelTool m_Level;
        ModelViewDeclutter m_Declutter;

        [SetUp]
        public void SetUp()
        {
            AirTools.Notes.Notebook.Clear();
            AppState.Reset();
            ModelView.Reset();
            m_Root = new GameObject("SceneRoot");
            m_Scene = m_Root.AddComponent<SceneRoot>();
            var facade = AirTools.Editor.SyntheticFacadeBuilder.CreateHierarchy();
            facade.transform.SetParent(m_Root.transform, false);
            m_Scene.SetContent(null, facade);
            m_Rig = new GameObject("rig");
            m_Rig.transform.SetPositionAndRotation(AirTools.Scene.SyntheticFacadeSpec.SpawnPosition, Quaternion.Euler(0f, AirTools.Scene.SyntheticFacadeSpec.SpawnYawDeg, 0f));
            m_Head = new GameObject("head");
            m_Head.transform.SetParent(m_Rig.transform, false);
            m_Head.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            m_App = new GameObject("app");
            m_Table = m_App.AddComponent<TabletopController>();
            m_Table.root = m_Scene; m_Table.rig = m_Rig.transform; m_Table.head = m_Head.transform;
            m_Hub = m_App.AddComponent<AirTools.Input.ToolInputHub>();
            m_Measure = m_App.AddComponent<AirTools.Tools.MeasureTool>();
            m_Measure.frame = m_Root.transform;
            m_Measure.SetInput(m_Hub);
            m_Measure.Equip(true);
            m_Level = m_App.AddComponent<AirTools.Tools.LevelTool>();
            m_Level.viewRoot = new GameObject("Levels").transform;
            m_Level.viewRoot.SetParent(m_Root.transform, false);
            // OnEnable doesn't run in EditMode: register what the declutter looks up.
            Services.Register(m_Measure);
            Services.Register(m_Level);
            m_Declutter = m_App.AddComponent<ModelViewDeclutter>();
            m_Declutter.tabletop = m_Table;
            m_Declutter.sceneRoot = m_Scene;
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            m_Declutter.Restore();
            m_Table.Apply(false);
            Services.Unregister(m_Measure);
            Services.Unregister(m_Level);
            if (m_Measure.viewRoot != null) Object.DestroyImmediate(m_Measure.viewRoot.gameObject);
            Object.DestroyImmediate(m_App); Object.DestroyImmediate(m_Rig); Object.DestroyImmediate(m_Root);
            AirTools.Scene.SnapService.Scale = 1f;
            AirTools.Notes.Notebook.Clear();
            AppState.Reset();
            ModelView.Reset();
        }

        void ClickScene(Vector3 eyeLocal, Vector3 targetLocal)
        {
            var t = m_Root.transform;
            var p = AirTools.Input.ToolInputHub.RayPose(t.TransformPoint(eyeLocal), t.TransformPoint(targetLocal));
            m_Hub.RaisePressStart(AirTools.Input.ToolHand.Right, p);
            m_Hub.RaisePressEnd(AirTools.Input.ToolHand.Right, p);
        }

        static Dictionary<Renderer, bool> Snapshot(params Transform[] roots)
        {
            var d = new Dictionary<Renderer, bool>();
            foreach (var r in roots) foreach (var x in r.GetComponentsInChildren<Renderer>(true)) d[x] = x.enabled;
            return d;
        }

        /// In Model view no world text owner draws (a tape's labels and lines, a level readout, a part's callout and
        /// outline) while the part's own mesh and the scene stay; the owners' own state is untouched; a label made while
        /// hidden is hidden too; back in the world everything draws exactly as before.
        [Test]
        public void InModelViewNoWorldTextOwnerIsVisibleAndItAllComesBack()
        {
            var eye = AirTools.Dev.MeasureScenarios.SpawnEye;
            float w = AirTools.Scene.SyntheticFacadeSpec.WindowWidth;
            ClickScene(eye, new Vector3(-w / 2, 4.1f, -0.05f));
            ClickScene(eye, new Vector3(w / 2, 4.1f, -0.05f));
            m_Hub.RaiseButton(AirTools.Input.ToolHand.Right, AirTools.Input.ToolButton.Finish);
            Assert.Greater(m_Measure.viewRoot.GetComponentsInChildren<TMPro.TMP_Text>(true).Length, 0, "a tape with labels: " + m_Measure.LastAction);
            AirTools.Tools.LevelGizmo.Create(m_Level.viewRoot, m_Measure.style, "Level1");
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = "Part";
            part.transform.SetParent(m_Root.transform, false);
            part.transform.localPosition = new Vector3(0.3f, 6.1f, 0.2f);
            part.transform.localScale = Vector3.one * 0.1f;
            var outline = AirTools.Parts.PartOutline.Create(part.transform, new Bounds(Vector3.zero, Vector3.one), m_Measure.style);
            outline.Show(AirTools.Parts.FitStatus.Green, "127 × 45 × 38 mm");
            var partMesh = part.GetComponent<MeshRenderer>();
            var sceneRenderers = m_Scene.Content.GetComponentsInChildren<Renderer>(true);

            var before = Snapshot(m_Measure.viewRoot, m_Level.viewRoot, outline.transform);
            int visibleBefore = m_Declutter.VisibleWorldText().Count;
            Assert.GreaterOrEqual(visibleBefore, 2, "tape, level and callout text draw in the world (the pool may drop a tape's side labels)");

            AppState.Set(AppMode.Tabletop);
            m_Table.Apply(true);
            m_Declutter.Refresh();
            Assert.IsTrue(ModelView.HidesAnnotations);
            var shown = m_Declutter.VisibleWorldText();
            Assert.IsEmpty(shown, "world text drawing in Model view:\n" + string.Join("\n", shown));
            foreach (var r in before.Keys) Assert.IsTrue(r.forceRenderingOff, $"{r.name} ({r.GetType().Name}) hidden");
            foreach (var kv in before) Assert.AreEqual(kv.Value, kv.Key.enabled, $"{kv.Key.name}: the owner's own enabled state untouched");
            Assert.IsFalse(partMesh.forceRenderingOff, "the part itself stays");
            // The model stays, but not the facade's 30 × 25 m test ground: on the table it was a 3 m slab over the wheel.
            var ground = sceneRenderers.Single(r => r.name == "Ground");
            Assert.IsTrue(ground.forceRenderingOff, "the ground plate is hidden on the table");
            Assert.IsTrue(ground.enabled, "…by the flag only");
            foreach (var r in sceneRenderers) if (r != ground) Assert.IsFalse(r.forceRenderingOff, $"the model ({r.name}) stays");
            Assert.AreEqual(before.Count + 1, m_Declutter.HiddenCount);

            // A label made while the model is on the table is hidden too (the next refresh).
            var late = AirTools.Tools.LevelGizmo.Create(m_Level.viewRoot, m_Measure.style, "Level2");
            m_Declutter.Refresh();
            foreach (var r in late.GetComponentsInChildren<Renderer>(true)) Assert.IsTrue(r.forceRenderingOff, $"late {r.name} hidden");

            AppState.Set(AppMode.World);
            m_Table.Apply(false);
            m_Declutter.Refresh();
            Assert.IsFalse(ModelView.HidesAnnotations);
            Assert.AreEqual(0, m_Declutter.HiddenCount);
            foreach (var kv in before)
            {
                Assert.IsFalse(kv.Key.forceRenderingOff, $"{kv.Key.name} back");
                Assert.AreEqual(kv.Value, kv.Key.enabled, $"{kv.Key.name} exactly as before");
            }
            foreach (var r in late.GetComponentsInChildren<Renderer>(true)) Assert.IsFalse(r.forceRenderingOff, $"late {r.name} back");
            Assert.IsFalse(ground.forceRenderingOff, "the ground is back in the world");
            Assert.GreaterOrEqual(m_Declutter.VisibleWorldText().Count, visibleBefore + 1, "every label draws again (and the new one)");
        }

        /// The model is fitted to the table (1:N, longest side 0.6–0.8 m); a new, smaller scan is re-fitted; with its spawn
        /// kept, the pin stands on it and stepping in puts it under your feet, looking your way.
        [Test]
        public void ASwitchedModelIsRefittedAndSteppingInLandsAtItsSpawn()
        {
            m_Table.fitToTable = true;
            AppState.Set(AppMode.Tabletop);
            m_Table.Apply(true);
            var box = m_Table.FootprintLocal(out bool any);
            Assert.IsTrue(any);
            float side = TabletopFit.LongestSide(box.size) * m_Table.Scale;
            Assert.That(side, Is.InRange(m_Table.ActiveFitMax / 1.34f, m_Table.ActiveFitMax + 1e-4f), $"the facade at {m_Table.ScaleLabel}");   // modelwheel: floating, ≤ 0.9 m
            Assert.AreEqual(m_Table.Scale, m_Root.transform.lossyScale.x, 1e-6f);
            Assert.AreEqual(m_Table.Scale, AirTools.Scene.SnapService.Scale, 1e-6f);
            Assert.AreEqual("1:" + m_Table.Denominator, m_Table.ScaleLabel);
            Assert.AreEqual("Model " + m_Table.ScaleLabel, m_Table.ScaleWord);

            // A 4 m "kitchen" replaces it: re-fitted to 1:5 (0.8 m), where the old model was (the anchor is kept).
            var anchorBefore = m_Table.AnchorEye;
            var kitchen = GameObject.CreatePrimitive(PrimitiveType.Cube);
            kitchen.name = "Kitchen";
            kitchen.transform.SetParent(m_Root.transform, false);
            kitchen.transform.localScale = new Vector3(4f, 2.5f, 3f);
            kitchen.transform.localPosition = new Vector3(0f, 1.25f, 0f);
            Object.DestroyImmediate(m_Scene.Content);
            m_Scene.SetContent(null, kitchen);
            m_Table.Refit();
            Assert.AreEqual(5, m_Table.Denominator);
            Assert.AreEqual(0.2f, m_Root.transform.lossyScale.x, 1e-6f);
            Assert.AreEqual(1, m_Table.Refits);
            Assert.AreEqual(anchorBefore, m_Table.AnchorEye, "re-fitted in place: the anchor is kept");

            var spawn = new Vector3(1f, 0f, 2f);
            m_Table.SetSpawn(spawn, 180f, "kitchen");
            Assert.IsTrue(m_Table.HasPendingSpawn);
            var anchor = m_Table.StepInAnchorLocal();
            Assert.Less(Vector2.Distance(new Vector2(anchor.x, anchor.z), new Vector2(spawn.x, spawn.z)), 1e-3f, "the pin stands on the spawn");
            AppState.Set(AppMode.World);
            m_Table.Apply(false);
            Assert.IsFalse(m_Table.HasPendingSpawn);
            Assert.AreEqual(1f, m_Root.transform.lossyScale.x, 1e-6f);
            var landed = m_Root.transform.TransformPoint(spawn);
            var feet = m_Table.FeetWorld();
            Assert.Less(Vector2.Distance(new Vector2(landed.x, landed.z), new Vector2(feet.x, feet.z)), 1e-3f, "the spawn is under your feet");
            var look = m_Root.transform.TransformDirection(TabletopFit.Yaw(180f) * Vector3.forward);
            var fwd = Vector3.ProjectOnPlane(m_Head.transform.forward, Vector3.up).normalized;
            Assert.Less(Vector3.Angle(look, fwd), 0.1f, "looking where the spawn looks");
        }

        /// The switcher's choices (the wheel's lens pinch and show_model call Choose / Step): a choice waits for the
        /// table and the last one wins; the model on view drops a waiting choice; Step wraps.
        [Test]
        public void ChoicesQueueAndStepWraps()
        {
            var go = new GameObject("switcher");
            try
            {
                var s = go.AddComponent<ModelSwitcher>();
                s.tabletop = m_Table;
                s.SetSites(new[] { "zabel-gymnasium", "kitchen", "synthetic-facade-small", "hospital-bg" });
                CollectionAssert.AreEqual(new[] { "kitchen", "zabel-gymnasium", "hospital-bg", ModelSites.BuiltIn }, s.Sites);
                int v = s.SitesVersion;
                s.SetSites(new[] { "kitchen", "zabel-gymnasium", "hospital-bg" });
                Assert.AreEqual(v, s.SitesVersion, "the same listing again: the wheel isn't rebuilt");
                s.Choose("zabel-gymnasium");
                Assert.AreEqual("zabel-gymnasium", s.Queued, "waits for the table, then loads");
                Assert.AreEqual("zabel-gymnasium", s.Focus);
                s.Choose("hospital-bg");
                Assert.AreEqual("hospital-bg", s.Queued, "the last choice wins");
                Assert.AreEqual(ModelSites.BuiltIn, s.Current, "the facade is on view");
                s.Choose(ModelSites.BuiltIn);
                Assert.IsNull(s.Queued, "choosing the model on view drops the waiting choice");
                Assert.AreEqual(ModelSites.BuiltIn, s.Focus);
                s.Step(+1);
                Assert.AreEqual("kitchen", s.Queued, "→ from the facade (the last) wraps to the kitchen");
                s.Step(-1);
                Assert.IsNull(s.Queued, "← from the waiting kitchen is the facade on view: nothing to load");
                Assert.AreEqual(ModelCardState.Current, s.StateOf(ModelSites.BuiltIn, null));
                Assert.AreEqual(ModelCardState.Idle, s.StateOf("kitchen", null));
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ---------------- modelwheel: in front of you, and the wheel ----------------

        /// Model view puts the model's centre straight ahead of the eyes at eye height, ~1 m away — not at table height —
        /// and the wheel's frame under it facing the eyes; it stays put when you turn; a recentre brings it back in front.
        [Test]
        public void ModelViewPlacesTheModelInFrontOfTheEyesNotOnATable()
        {
            Assert.AreEqual(ModelPlacement.FrontOfMe, m_Table.placement, "in front of you by default");
            m_Head.transform.localRotation = Quaternion.Euler(20f, 0f, 0f);   // looking a little down when it opens
            AppState.Set(AppMode.Tabletop);
            m_Table.Apply(true);
            Assert.IsTrue(m_Table.OnTable && m_Table.Floating && m_Table.Anchored);
            Assert.AreEqual(TabletopController.SourceFront, m_Table.TableSource);
            var eye = m_Head.transform.position;
            var fwd = Vector3.ProjectOnPlane(m_Head.transform.forward, Vector3.up).normalized;
            var box = m_Table.FootprintLocal(out bool any);
            Assert.IsTrue(any);
            var centre = m_Root.transform.TransformPoint(box.center);
            Assert.Less(Vector3.Distance(centre, m_Table.ModelCentreWorld), 1e-3f, "the box centre is where it says");
            var flat = new Vector3(centre.x - eye.x, 0f, centre.z - eye.z);
            Assert.That(flat.magnitude, Is.InRange(0.9f, 1.2f), "about a metre ahead");
            Assert.Less(Vector3.Angle(flat, fwd), 0.1f, "straight ahead (yaw only: the head's pitch doesn't tilt it)");
            Assert.AreEqual(eye.y - ModelViewLayout.Drop(m_Table.ModelHalfSize), centre.y, 1e-3f, "at eye height, a few cm below");
            Assert.That(centre.y - eye.y, Is.InRange(-ModelViewLayout.DropBelowEye - 1e-4f, 0.02f), "(the facade: a few cm below)");
            Assert.Greater(centre.y - m_Rig.transform.position.y, 1.2f, "not at a table's height");
            Assert.Less(Vector3.Angle(m_Root.transform.up, Vector3.up), 0.01f, "level");

            Assert.IsTrue(m_Table.TryWheelPose(0.066f, out var wheel));
            float down = TabletopFit.BelowEyeDeg(eye, wheel.position);
            Assert.That(down, Is.InRange(ModelViewLayout.WheelMinDownDeg - 0.01f, ModelViewLayout.WheelMaxDownDeg + 0.01f), "the wheel under it");
            Assert.Greater(down, TabletopFit.BelowEyeDeg(eye, centre), "below the model");
            Assert.Less(Vector3.Angle(wheel.rotation * Vector3.forward, wheel.position - eye), 0.01f, "facing the eyes");

            // Turn away: world-locked, it stays.
            var placed = m_Root.transform.position;
            m_Rig.transform.rotation *= Quaternion.Euler(0f, 90f, 0f);
            m_Table.Refit();
            Assert.Less(Vector3.Distance(m_Root.transform.position, placed), 1e-4f, "it doesn't follow your head");
            // Recentre: in front of you again (instant in EditMode).
            Assert.IsTrue(m_Table.Recentre("test"));
            Assert.AreEqual(1, m_Table.Recentres);
            var fwd2 = Vector3.ProjectOnPlane(m_Head.transform.forward, Vector3.up).normalized;
            var flat2 = new Vector3(m_Table.ModelCentreWorld.x - eye.x, 0f, m_Table.ModelCentreWorld.z - eye.z);
            Assert.Less(Vector3.Angle(flat2, fwd2), 0.1f, "back in front of you");
            // Leave and come back: placed afresh from where you look then.
            AppState.Set(AppMode.World);
            m_Table.Apply(false);
            Assert.IsFalse(m_Table.Anchored);
            Assert.IsFalse(m_Table.Recentre("outside"), "nothing to recentre outside Model view");
        }

        /// On a table (the option), the model's ground is on the (assumed) table as before, and the wheel floats 24°
        /// below the eye line.
        [Test]
        public void OnTheTableOptionTheModelSitsOnTheTable()
        {
            m_Table.placement = ModelPlacement.OnTable;
            m_Table.fitToTable = false;
            AppState.Set(AppMode.Tabletop);
            m_Table.Apply(true);
            Assert.IsFalse(m_Table.Floating);
            Assert.AreEqual(m_Rig.transform.position.y + m_Table.tableHeight, m_Root.transform.position.y, 1e-4f, "scene ground on the table");
            Assert.IsTrue(m_Table.TryWheelPose(0.066f, out var wheel));
            Assert.AreEqual(ModelViewLayout.TableWheelDownDeg, TabletopFit.BelowEyeDeg(m_Head.transform.position, wheel.position), 1e-3f);
        }

        /// No "Model view · Kitchen · 1:5" toast over the wheel, however Model view is reached: floating it is always
        /// quiet (entry, a re-fit, a new model); on the table option it toasts as before unless there is a wheel, which is
        /// found in the scene even when the builder didn't wire it and it never registered (OnEnable doesn't run here; the
        /// gate saw the built-in facade's re-fit toast over the wheel).
        [Test]
        public void ModelViewNeverToastsOverTheWheel()
        {
            Assert.IsNull(Object.FindAnyObjectByType<ModelWheel>(FindObjectsInactive.Include), "no wheel in the scene yet");
            AppState.Set(AppMode.Tabletop);
            m_Table.Apply(true);
            Assert.AreEqual(0, m_Table.Toasts, "floating: quiet on entry");
            StringAssert.StartsWith("Model view · ", m_Table.LastNote, "logged instead");
            m_Table.Refit();
            Assert.AreEqual(0, m_Table.Toasts, "floating: quiet on a re-fit (a new model, the built-in facade)");
            StringAssert.DoesNotStartWith("Model view", m_Table.LastNote);
            AppState.Set(AppMode.World);
            m_Table.Apply(false);

            m_Table.placement = ModelPlacement.OnTable;
            AppState.Set(AppMode.Tabletop);
            m_Table.Apply(true);
            Assert.AreEqual(1, m_Table.Toasts, "on the table without a wheel: the toast, as before");
            AppState.Set(AppMode.World);
            m_Table.Apply(false);

            var go = new GameObject("wheel");
            try
            {
                var w = go.AddComponent<ModelWheel>();
                Assert.IsNull(m_Table.wheel, "not wired");
                Assert.IsFalse(AirTools.Core.Services.TryGet<ModelWheel>(out _), "not registered");
                AppState.Set(AppMode.Tabletop);
                m_Table.Apply(true);
                m_Table.Refit();
                Assert.AreEqual(1, m_Table.Toasts, "a wheel in the scene: quiet, entry and re-fit");
                Assert.AreSame(w, m_Table.wheel, "found and kept");
                AppState.Set(AppMode.World);
                m_Table.Apply(false);
            }
            finally { Object.DestroyImmediate(go); }
        }

        /// The wheel as a user drives it: the model on view under the lens when Model view opens; a side card's tap spins it
        /// to the lens and a tap on the lens opens it (the switcher's queue); an offline model that isn't on the headset
        /// is dimmed and never a resting place; show_model elsewhere spins the wheel to it.
        [Test]
        public void TheWheelSpinsToACardAndOpensItFromTheLens()
        {
            var go = new GameObject("wheel");
            try
            {
                var s = go.AddComponent<ModelSwitcher>();
                s.tabletop = m_Table;
                var w = go.AddComponent<ModelWheel>();
                w.switcher = s; w.tabletop = m_Table; w.head = m_Head.transform;
                w.content = new GameObject("Content");
                w.content.transform.SetParent(go.transform, false);
                w.content.SetActive(false);
                w.cards = new AirTools.UI.GlassButton[7];
                for (int i = 0; i < 7; i++)
                {
                    var c = new GameObject($"Card{i}");
                    c.transform.SetParent(w.content.transform, false);
                    w.cards[i] = c.AddComponent<AirTools.UI.GlassButton>();
                    var b = c.AddComponent<ModelWheelButton>();
                    b.button = w.cards[i]; b.wheel = w; b.action = ModelWheelAction.Card; b.slot = i;
                }
                s.SetSites(new[] { "kitchen", "zabel-gymnasium", "hospital-bg", "gt-lcc-canopy", "gt-lcc-pavilion", "gt-lcc-tower" });
                Assert.AreEqual(7, s.Sites.Count, "six scans and the built-in facade");
                AppState.Set(AppMode.Tabletop);
                m_Table.Apply(true);
                s.Tick();
                Assert.IsTrue(s.Shown, "Model view settled");
                float now = 10f;
                void Step(float seconds) { for (float t = 0f; t < seconds; t += 1f / 72f) { now += 1f / 72f; w.Tick(now, 1f / 72f); } }
                Step(0.05f);
                Assert.IsTrue(w.Shown && w.content.activeSelf, "the wheel shows");
                Assert.AreEqual(ModelSites.BuiltIn, w.LensSite, "the model on view under the lens");
                Assert.IsTrue(w.cards[w.LensSlot].selected, "the model on view is the ink card");
                Assert.Less(Vector3.Distance(w.transform.position, TryWheel()), 1e-4f, "placed under the model");
                Assert.AreEqual(0, w.Commits);

                // A tap on the card right of the lens: it spins to the lens (the loop wraps: after the facade, the kitchen).
                var right = w.CardFor("kitchen");
                Assert.IsNotNull(right, w.Brief());
                w.Press(ModelWheelAction.Card, System.Array.IndexOf(w.cards, right));
                Step(1.5f);
                Assert.AreEqual("kitchen", w.LensSite, w.Brief());
                Assert.IsNull(s.Queued, "spinning only previews");
                // A tap on the lens card opens it; the card says so the next frame (not on the 0.1 s refresh).
                w.Press(ModelWheelAction.Card, w.LensSlot);
                Assert.AreEqual(1, w.Commits);
                Assert.AreEqual("kitchen", s.Queued, "chosen: loads once the streamer can (the switcher's queue)");
                now += 0.001f;
                w.Tick(now, 0.001f);
                Assert.AreEqual(ModelSites.CardText("Kitchen", ModelCardState.Queued), w.cards[w.LensSlot].Text, "redrawn at once");
                Assert.AreEqual(ModelCardState.Queued, w.LensState);

                // Elsewhere (show_model): the wheel spins to it.
                s.Choose("gt-lcc-tower");
                Step(1.5f);
                Assert.AreEqual("gt-lcc-tower", w.LensSite, w.Brief());
                // Spin by one detent each way, and a flick rests on a card.
                Assert.AreEqual(ModelSites.BuiltIn, w.SpinBy(+1));
                Step(1.5f);
                Assert.AreEqual(ModelSites.BuiltIn, w.LensSite);
                w.Flick(-3f);
                Step(4f);
                Assert.IsTrue(w.Dial.Settled);
                Assert.Greater(w.Ticks, 0, "it ticked past cards");
            }
            finally { Object.DestroyImmediate(go); }

            Vector3 TryWheel() { m_Table.TryWheelPose(0.118f * 0.5f * 1.12f, out var p); return p.position; }
        }
    }

    /// modelview in Main.unity (run AirTools ▸ Wire Main Scene first; Unity only).
    public class ModelViewMainSceneTests
    {
        [Test]
        public void TheWheelAndTheDeclutterAreWired()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(AirTools.Editor.MainSceneBuilder.ScenePath, UnityEditor.SceneManagement.OpenSceneMode.Additive);
            try
            {
                var roots = scene.GetRootGameObjects();
                var s = roots.SelectMany(r => r.GetComponentsInChildren<ModelSwitcher>(true)).FirstOrDefault();
                Assert.IsNotNull(s, "ModelSwitcher (ModelSwitcherBuilder)");
                Assert.IsNotNull(s.tabletop); Assert.IsNotNull(s.head); Assert.IsNotNull(s.mode);
                Assert.IsTrue(s.tabletop.fitToTable, "the model is fitted");
                Assert.AreEqual(ModelPlacement.FrontOfMe, s.tabletop.placement, "modelwheel: in front of you by default");
                // modelwheel: the wheel.
                var w = s.GetComponent<ModelWheel>();
                Assert.IsNotNull(w, "ModelWheel beside the switcher");
                Assert.AreEqual(s, w.switcher); Assert.AreEqual(s.tabletop, w.tabletop); Assert.IsNotNull(w.head);
                Assert.IsFalse(w.content.activeSelf, "hidden until Model view");
                Assert.AreEqual(AirTools.Editor.ModelSwitcherBuilder.Slots, w.cards.Length);
                Assert.AreEqual(3, w.LensSlot, "the middle slot is under the lens");
                for (int i = 0; i < w.cards.Length; i++)
                {
                    Assert.AreEqual(AirTools.UI.ButtonStyle.Toggle, w.cards[i].style, "the model on view is the ink card");
                    var b = w.cards[i].GetComponent<ModelWheelButton>();
                    Assert.AreEqual(ModelWheelAction.Card, b.action); Assert.AreEqual(i, b.slot); Assert.AreEqual(w, b.wheel);
                    Assert.IsNotNull(w.thumbs[i]); Assert.IsNotNull(w.thumbs[i].sharedMaterial, "the picture's material");
                    Assert.IsNotNull(w.badges[i], "the Not downloaded badge");
                    Assert.IsNull(w.cards[i].poke, "cards are pressed by the wheel's own pinch");
                    Assert.IsNull(w.cards[i].ray);
                }
                Assert.IsNotNull(w.glass); Assert.IsNotNull(w.glass.sharedMaterial, "the liquid glass arc");
                Assert.IsNotNull(w.lensName); Assert.IsNotNull(w.lensDetail);
                foreach (var (chip, action) in new[] { (w.walkIn, ModelWheelAction.WalkIn), (w.recentre, ModelWheelAction.Recentre), (w.exit, ModelWheelAction.Exit) })
                {
                    Assert.IsNotNull(chip, action.ToString());
                    Assert.AreEqual(action, chip.GetComponent<ModelWheelButton>().action);
                    Assert.IsNotNull(chip.poke, $"{action}: poke (hands)");
                    Assert.IsNotNull(chip.ray, $"{action}: ray (D5)");
                    Assert.Less(chip.transform.localPosition.y, w.lensDetail.transform.localPosition.y, $"{action}: under the lens preview");
                }
                Assert.IsNotNull(w.target, "the ray target over the wheel");
                Assert.Greater(w.target.transform.localPosition.z, 0f, "behind the cards and chips");
                var d = roots.SelectMany(r => r.GetComponentsInChildren<ModelViewDeclutter>(true)).FirstOrDefault();
                Assert.IsNotNull(d, "ModelViewDeclutter");
                Assert.AreEqual(s.tabletop, d.tabletop);
                Assert.IsNotNull(d.sceneRoot);
                Assert.IsNotNull(d.crosshair, "the coach's crosshair");
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
