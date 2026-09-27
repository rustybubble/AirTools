using System.Linq;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Tests
{
    public class PartSpecTests
    {
        static PartSpec Fixture(string id) =>
            PartSpec.Parse(System.IO.File.ReadAllText($"{PartCatalogBuilder.PartsFolder}/{id}/part.json"));

        [Test]
        public void HangerFixtureParses()
        {
            var s = Fixture("hidden-hanger-5k");
            Assert.AreEqual(127, s.dims_mm.w); Assert.AreEqual(38, s.dims_mm.d); Assert.AreEqual(45, s.dims_mm.h);
            Assert.AreEqual("-z", s.mount.face);
            Assert.AreEqual(600f, s.spacing_mm);
            Assert.AreEqual(25f, s.clearance_mm.top);
            Assert.AreEqual(2, s.finishes.Count);
            Assert.AreEqual("#5A3E2B", PartColor.ToHex(s.FindFinish("Brown").Color));
            Assert.AreEqual(3, s.sellers.Count);
            Assert.AreEqual(4.27f, s.RecommendedSeller.price_usd, 1e-4);
            Assert.IsFalse(s.HasWindowRange);
            Assert.AreEqual("2026-09-25T00:00:00-04:00", s.fetched_at, "dates stay strings");
        }

        [Test]
        public void AcFixtureParsesWithNulls()
        {
            var s = Fixture("window-ac-small");
            Assert.IsNull(s.spacing_mm, "spacing_mm: null");
            Assert.AreEqual("-y", s.mount.face);
            Assert.AreEqual(590f, s.min_window_width_mm);
            Assert.AreEqual(1000f, s.max_window_width_mm);
            Assert.IsTrue(s.HasWindowRange);
            Assert.AreEqual("Walmart (mock)", s.RecommendedSeller.name);
        }

        [Test]
        public void MissingFieldsGetDefaults()
        {
            var s = PartSpec.Parse("{\"id\":\"x\",\"name\":\"X\",\"dims_mm\":{\"w\":10,\"d\":20,\"h\":30},\"mount\":null,\"finishes\":null,\"mount_extra\":1}");
            Assert.AreEqual("-z", s.mount.face);
            Assert.AreEqual(0, s.finishes.Count);
            Assert.AreEqual(0, s.clearance_mm.top);
            Assert.IsNull(s.RecommendedSeller);
            Assert.Throws<System.FormatException>(() => PartSpec.Parse("{\"id\":\"x\",\"dims_mm\":{\"w\":0,\"d\":1,\"h\":1}}"));
            Assert.Throws<System.FormatException>(() => PartSpec.Parse("{\"name\":\"no id\",\"dims_mm\":{\"w\":1,\"d\":1,\"h\":1}}"));
        }

        [Test]
        public void JobStatusParses()
        {
            var j = PartSpec.ParseAny<SearchJobStatus>("{\"status\":\"done\",\"candidates\":[{\"id\":\"a\",\"name\":\"A\",\"dims_mm\":{\"w\":1,\"d\":2,\"h\":3},\"price_usd\":null,\"tier\":\"proxy\"}]}");
            Assert.IsTrue(j.Done);
            var c = PartsClient.ParseCandidates(j.candidates);
            Assert.AreEqual("a", c[0].id);
            Assert.IsNull(c[0].price_usd);
            Assert.IsFalse(PartSpec.ParseAny<SearchJobStatus>("{\"status\":\"pending\",\"candidates\":[]}").Done);
        }

        [Test]
        public void Formatting()
        {
            var d = new PartDims { w = 127, d = 38, h = 45 };
            Assert.AreEqual("127 × 38 × 45 mm", PartFormat.DimsMm(d));
            Assert.AreEqual("5 × 1½ × 1¾ in", PartFormat.DimsIn(d));
            Assert.AreEqual("⅛", PartFormat.Inches(3.2f));
            Assert.AreEqual("$4.27", PartFormat.Price(4.27f));
            Assert.AreEqual("—", PartFormat.Price(null));
            Assert.AreEqual("Small window air\nconditioner, 5,000 BTU", SpecCard.Wrap("Small window air conditioner, 5,000 BTU", 22, 2));
            Assert.AreEqual("aaa bbb…", SpecCard.Wrap("aaa bbb ccc", 7, 1));
        }

        [Test]
        public void FaceDirectionsAndLocalBox()
        {
            Assert.AreEqual(Vector3.back, PartMath.FaceDirection("-z"));
            Assert.AreEqual(Vector3.down, PartMath.FaceDirection("-y"));
            Assert.AreEqual(Vector3.right, PartMath.FaceDirection("+x"));
            Assert.IsFalse(PartMath.IsFace("up"));
            var hanger = Fixture("hidden-hanger-5k");
            var box = PartMath.LocalBox(hanger);
            Assert.That(Vector3.Distance(new Vector3(0, 0, 0.019f), box.center), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(new Vector3(0.127f, 0.045f, 0.038f), box.size), Is.LessThan(1e-5f));
            var ac = PartMath.LocalBox(Fixture("window-ac-small"));
            Assert.AreEqual(0f, ac.min.y, 1e-5f, "AC sits on its bottom face");
        }

        [Test]
        public void FitScaleUsesMedianRatio()
        {
            float s = PartMath.FitScale(new Vector3(2f, 1f, 0.5f), new Vector3(1f, 0.5f, 0.25f), out float res);
            Assert.AreEqual(0.5f, s, 1e-6f);
            Assert.AreEqual(0f, res, 1e-4f);
            // One wildly wrong axis doesn't drag the scale; it shows up as residual.
            s = PartMath.FitScale(new Vector3(2f, 1f, 3f), new Vector3(1f, 0.5f, 0.25f), out res);
            Assert.AreEqual(0.5f, s, 1e-6f);
            Assert.Greater(res, 100f);
        }

        [Test]
        public void PlacementRotationPutsMountFaceIntoSurface()
        {
            var hanger = Fixture("hidden-hanger-5k");
            var r = PartMath.PlacementRotation(hanger, Vector3.forward, Vector3.forward);
            Assert.That(Vector3.Angle(r * Vector3.back, Vector3.back), Is.LessThan(0.01f), "back face into a wall facing +z");
            Assert.That(Vector3.Angle(r * Vector3.up, Vector3.up), Is.LessThan(0.01f), "upright");
            var ac = Fixture("window-ac-small");
            r = PartMath.PlacementRotation(ac, Vector3.up, new Vector3(1f, 0.3f, 1f));
            Assert.That(Vector3.Angle(r * Vector3.down, Vector3.down), Is.LessThan(0.01f), "bottom on the sill");
            Assert.That(Vector3.Angle(r * Vector3.forward, new Vector3(1f, 0f, 1f)), Is.LessThan(0.01f), "front turned to the viewer");
            // Wall-type part on a floor: still flat, no NaNs.
            r = PartMath.PlacementRotation(hanger, Vector3.up, Vector3.forward);
            Assert.That(Vector3.Angle(r * Vector3.back, Vector3.down), Is.LessThan(0.01f));
        }

        [Test]
        public void ToolManagerParsesPart()
        {
            Assert.IsTrue(ToolManager.TryParse("parts", out var k));
            Assert.AreEqual(ToolKind.Part, k);
        }
    }

    /// The real facade + the shipped catalog + a real PartTool driven through a ToolInputHub.
    public class PartToolTests : FacadeToolFixture
    {
        const float Mm = 0.001f;
        static PartCatalog s_Catalog;
        GameObject m_Parts;
        PartTool m_Tool;
        PartLoader m_Loader;

        [OneTimeSetUp]
        public void LoadCatalog()
        {
            s_Catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath);
            if (s_Catalog == null || s_Catalog.entries.Count < 3) s_Catalog = PartCatalogBuilder.Build();   // assetgen: 3 fixtures
            PartCatalogBuilder.EnsurePartsLayer();
        }

        [SetUp]
        public void CreatePartTool()
        {
            Tool.Equip(false);
            m_Parts = new GameObject("parts-test");
            m_Loader = m_Parts.AddComponent<PartLoader>();
            m_Loader.catalog = s_Catalog;
            m_Tool = m_Parts.AddComponent<PartTool>();
            m_Tool.SetInput(Hub);
            m_Tool.Equip(true);
            Services.Register(m_Tool);
        }

        [TearDown]
        public void DestroyParts()
        {
            m_Tool.ClearAll();
            Services.Unregister(m_Tool);
            foreach (var p in Object.FindObjectsByType<PartInstance>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Object.DestroyImmediate(p.gameObject);
            Object.DestroyImmediate(m_Parts);
            Physics.SyncTransforms();
        }

        PartInstance Take(string id)
        {
            var p = m_Loader.LoadFromCatalog(id);
            Assert.IsNotNull(p, $"catalog has {id}");
            m_Tool.Hold(p);
            return p;
        }

        bool PlaceFrom(Vector3 origin, Vector3 target) => m_Tool.Release(From(origin, target));

        static void AddTape(Vector3 a, Vector3 b) =>
            Notebook.Add(new NotebookEntry("measure", Vector3.Distance(a, b), "m", new[] { a, b }, System.DateTime.Now, -1, "tape"));

        // ---------------- true size ----------------

        [TestCase("hidden-hanger-5k")]
        [TestCase("window-ac-small")]
        public void CatalogPartIsTrueSize(string id)
        {
            var p = m_Loader.LoadFromCatalog(id);
            var size = p.MeasuredSizeMm();
            var d = p.Spec.dims_mm;
            Assert.AreEqual(d.w, size.x, 1f, "W"); Assert.AreEqual(d.h, size.y, 1f, "H"); Assert.AreEqual(d.d, size.z, 1f, "D");
            // Origin at the mounting face centre.
            var b = p.MeasuredLocalBounds();
            var face = b.center + Vector3.Scale(PartMath.FaceDirection(p.Spec.mount.face), b.extents);
            Assert.That(face.magnitude, Is.LessThan(1 * Mm));
            Assert.AreEqual(PartLayers.Parts, p.gameObject.layer);
            Assert.AreEqual("catalog", p.Source);
            Object.DestroyImmediate(p.gameObject);
        }

        [Test]
        public void MisScaledModelIsFittedToListing()
        {
            // A generated model at the wrong scale with its origin off the mounting face.
            var holder = new GameObject("generated");
            var inner = Object.Instantiate(s_Catalog.Find("hidden-hanger-5k").model, holder.transform);
            inner.transform.localScale = Vector3.one * 3.7f;
            inner.transform.localPosition = new Vector3(0.2f, -0.5f, 0.3f);
            var spec = s_Catalog.Spec("hidden-hanger-5k");
            var p = PartInstance.Create(spec, holder, null, new MeasureStyle(), "test");
            var size = p.MeasuredSizeMm();
            Assert.AreEqual(127f, size.x, 1f); Assert.AreEqual(45f, size.y, 1f); Assert.AreEqual(38f, size.z, 1f);
            Assert.AreEqual(1f / 3.7f, p.ModelScale, 1e-4f);
            Assert.Less(p.ScaleResidualPct, 0.1f);
            var b = p.MeasuredLocalBounds();
            Assert.AreEqual(0f, b.min.z, 1 * Mm, "mounting (back) face at the origin");
            Assert.AreEqual(0f, b.center.x, 1 * Mm); Assert.AreEqual(0f, b.center.y, 1 * Mm);
            Object.DestroyImmediate(p.gameObject);
        }

        [Test]
        public void MissingModelFallsBackToTrueSizeBox()
        {
            var spec = s_Catalog.Spec("window-ac-small");
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.transform.localScale = spec.dims_mm.Metres;
            var p = PartInstance.Create(spec, box, null, new MeasureStyle(), "test");
            var size = p.MeasuredSizeMm();
            Assert.AreEqual(470f, size.x, 1f); Assert.AreEqual(300f, size.y, 1f); Assert.AreEqual(380f, size.z, 1f);
            Assert.AreEqual(0f, p.MeasuredLocalBounds().min.y, 1 * Mm);
            Assert.AreEqual(0, p.Model.GetComponentsInChildren<Collider>().Length, "model colliders removed (the part box is the collider)");
            Object.DestroyImmediate(p.gameObject);
        }

        // ---------------- finishes ----------------

        [Test]
        public void BrownSwatchRecoloursTo5A3E2B()
        {
            var p = Take("hidden-hanger-5k");
            var other = m_Loader.LoadFromCatalog("hidden-hanger-5k");
            Assert.IsTrue(AppCommands.SetFinish("brown"));
            Assert.AreEqual("#5A3E2B", PartColor.ToHex(p.MaterialColor().Value));
            Assert.AreEqual("brown", p.FinishName);
            Assert.AreNotEqual("#5A3E2B", PartColor.ToHex(other.MaterialColor().Value), "other instances keep their colour");
            Assert.IsFalse(p.SetFinish("purple"));
            Assert.IsTrue(p.SetFinish("white"));
            Assert.AreEqual("#F2F2F0", PartColor.ToHex(p.MaterialColor().Value));
            Object.DestroyImmediate(other.gameObject);
        }

        // ---------------- placement + fit ----------------

        static readonly Vector3 FasciaPoint = new Vector3(0.5f, 6.15f, S.FasciaProud);
        /// From the ground the gutter's front lip hides the fascia behind it, so fascia shots come from a raised
        /// vantage (the Operator test instead moves the controller up to the fascia: see ReleaseNearFascia…).
        static readonly Vector3 Ladder = new Vector3(0.5f, 7.0f, 1.5f);

        [Test]
        public void HangerOnFasciaIsSeatedAndGreen()
        {
            var p = Take("hidden-hanger-5k");
            int seated = AirTools.UI.FeedbackEvents.Count(AirTools.UI.Feedback.PartSeated);
            Assert.IsTrue(PlaceFrom(Ladder, FasciaPoint), m_Tool.LastAction);
            Assert.AreEqual(seated + 1, AirTools.UI.FeedbackEvents.Count(AirTools.UI.Feedback.PartSeated), "the seat answers (clunk + haptic)");
            // Back face within 2 mm of the fascia plane; mounting face into the surface; upright.
            var b = p.Box.bounds;
            Assert.AreEqual(S.FasciaProud, b.min.z, 2 * Mm, "back face on the fascia plane");
            Assert.That(Vector3.Angle(p.WorldMountDirection, Vector3.back), Is.LessThan(0.5f));
            Assert.That(Vector3.Angle(p.transform.up, Vector3.up), Is.LessThan(0.5f));
            Assert.AreEqual(127f, b.size.x * 1000f, 1f);
            Assert.AreEqual(FitStatus.Green, p.Fit.Status, p.Fit.ToString());
            Assert.AreEqual(5, p.Fit.SupportedSamples);
            Assert.AreEqual(PartOutline.Green, p.Outline.CurrentColor);
            StringAssert.Contains("127 × 38 × 45 mm", p.Outline.CalloutText);
            Assert.IsTrue(p.Placed); Assert.IsFalse(p.Held);
            Assert.IsNull(m_Tool.Held);
            Assert.AreEqual(p, m_Tool.Selected);
            Assert.AreEqual("part", Notebook.Last.Tool);
            // Display copy (UX W0.9 §4): a verdict you can act on, never "spare" for a fastener.
            Assert.AreEqual("Fits the fascia", p.Fit.Verdict);
            Assert.AreEqual("Measure the run to place the rest", p.Fit.Reason);
            Assert.AreEqual(FitAction.MeasureIt, p.Fit.Action);
            Assert.AreEqual("✓ Fits the fascia", AirTools.UI.Copy.FitLine(p.Fit));
            StringAssert.Contains("✓ Fits the fascia", p.Outline.CalloutText);
            Assert.AreEqual("Hanger", Notebook.Last.DisplayTitle);
        }

        [Test]
        public void ReleaseNearFasciaSnapsWithin15cm()
        {
            // Controller right in front of the fascia (as the Operator test does), pointing sideways: the part
            // rides 15 cm ahead and is within 15 cm of the fascia, so it snaps by proximity.
            var p = Take("hidden-hanger-5k");
            var origin = new Vector3(-0.2f, 6.16f, 0.09f);
            Assert.IsTrue(PlaceFrom(origin, origin + Vector3.right), m_Tool.LastAction);
            Assert.AreEqual(S.FasciaProud, p.Box.bounds.min.z, 2 * Mm);
            Assert.AreEqual(FitStatus.Green, p.Fit.Status, p.Fit.ToString());
        }

        [Test]
        public void ReleaseInOpenAirKeepsHolding()
        {
            var p = Take("hidden-hanger-5k");
            AirTools.UI.InputHints.Reset();
            Assert.IsFalse(PlaceFrom(MeasureScenarios.SpawnEye, MeasureScenarios.SpawnEye + new Vector3(0, 1, 1)));
            Assert.AreEqual(p, m_Tool.Held);
            Assert.AreEqual("no surface under the pointer", m_Tool.LastAction);
            Assert.AreEqual("Aim at the fascia to place it", AirTools.UI.InputHints.Last, "a refused release says where to aim");
        }

        [Test]
        public void HeldPartPreviewsOnSurfaceWithLiveFit()
        {
            var p = Take("hidden-hanger-5k");
            m_Tool.UpdateHeld(From(Ladder, FasciaPoint));
            Assert.IsTrue(m_Tool.OnSurface);
            Assert.AreEqual(FitStatus.Green, p.Fit.Status);
            m_Tool.UpdateHeld(From(MeasureScenarios.SpawnEye, MeasureScenarios.SpawnEye + Vector3.up));
            Assert.IsFalse(m_Tool.OnSurface);
            Assert.IsNull(p.Fit);
            Assert.That(Vector3.Distance(p.transform.position, MeasureScenarios.SpawnEye + Vector3.up * m_Tool.holdDistance), Is.LessThan(1e-4f));
        }

        [Test]
        public void HeldPartFollowsTheOtherHandWhenTheActiveRayIsOff()
        {
            var p = Take("hidden-hanger-5k");
            Hub.ClearOverrides();
            Hub.SetPointerOverride(ToolHand.Left, From(Ladder, FasciaPoint));   // right ray off (e.g. near a poke button)
            m_Tool.Tick();
            Assert.IsTrue(m_Tool.OnSurface);
            Assert.AreEqual(S.FasciaProud, p.Box.bounds.min.z, 2 * Mm);
            Hub.ClearOverrides();
            var before = p.transform.position;
            m_Tool.Tick();
            Assert.AreEqual(before, p.transform.position, "no pointer at all: the part stays put");
        }

        [Test]
        public void OverlappingPartSlidesClear()
        {
            var a = Take("hidden-hanger-5k");
            Assert.IsTrue(PlaceFrom(Ladder, FasciaPoint));
            var b = Take("hidden-hanger-5k");
            Assert.IsTrue(PlaceFrom(Ladder, FasciaPoint + new Vector3(0.1f, 0, 0)));
            // 127 mm wide, centres 100 mm apart → slides 27 mm along the fascia.
            Assert.GreaterOrEqual(b.transform.position.x - a.transform.position.x, 0.127f - 0.001f);
            Assert.AreEqual(S.FasciaProud, b.Box.bounds.min.z, 2 * Mm);
            Assert.AreEqual(FitStatus.Green, b.Fit.Status, b.Fit.ToString());
            StringAssert.Contains("slid", m_Tool.LastAction);
        }

        [Test]
        public void CoincidentPartIsRed()
        {
            var a = Take("hidden-hanger-5k");
            Assert.IsTrue(PlaceFrom(Ladder, FasciaPoint));
            // Same spot: the smallest way out is straight off the wall, which isn't allowed → it stays and is red.
            var b = Take("hidden-hanger-5k");
            b.transform.SetPositionAndRotation(a.transform.position, a.transform.rotation);
            var fit = b.Evaluate(null);
            Assert.AreEqual(FitStatus.Red, fit.Status);
            StringAssert.Contains("hidden gutter hanger", fit.Headline);
            Assert.AreEqual("Overlaps another hanger", fit.Verdict);
            Assert.AreEqual(FitAction.MoveIt, fit.Action);
        }

        [Test]
        public void BlockedClearanceIsAmber()
        {
            var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obstacle.name = "Obstacle";
            obstacle.layer = AirTools.Scene.SceneLayers.SceneSurface;
            obstacle.transform.position = new Vector3(1.5f, 6.215f, 0.0475f);
            obstacle.transform.localScale = new Vector3(0.3f, 0.07f, 0.045f);  // y 6.18–6.25, just in front of the fascia
            Physics.SyncTransforms();
            try
            {
                var p = Take("hidden-hanger-5k");
                Assert.IsTrue(PlaceFrom(new Vector3(1.5f, 7.0f, 1.5f), new Vector3(1.5f, 6.14f, S.FasciaProud)), m_Tool.LastAction);
                Assert.AreEqual(FitStatus.Amber, p.Fit.Status, p.Fit.ToString());
                StringAssert.Contains("Needs 25 mm clear above (Obstacle)", p.Fit.Headline);
                Assert.AreEqual("Too tight above", p.Fit.Verdict);
                Assert.AreEqual("Needs 25 mm clear · the building is in the way", p.Fit.Reason);
                Assert.AreEqual(PartOutline.Amber, p.Outline.CurrentColor);
            }
            finally { Object.DestroyImmediate(obstacle); }
        }

        [Test]
        public void HangerAgainstGutterTapeReportsSpare()
        {
            AddTape(new Vector3(-2.1f, 6.1f, 0.15f), new Vector3(2.1f, 6.1f, 0.15f));
            var p = Take("hidden-hanger-5k");
            Assert.IsTrue(PlaceFrom(Ladder, FasciaPoint));
            Assert.AreEqual(FitStatus.Green, p.Fit.Status);
            Assert.AreEqual("Fits, 4073 mm spare", p.Fit.Headline);
            Assert.AreEqual("Fits the fascia", p.Fit.Verdict);
            StringAssert.StartsWith("8 needed along the", p.Fit.Reason);
            Assert.AreEqual(FitAction.PlaceAll, p.Fit.Action);
            Assert.AreEqual("Place all 8", p.Fit.ActionLabel);
            StringAssert.DoesNotContain("spare", p.Fit.Verdict + p.Fit.Reason);
        }

        static readonly Vector3 AboveWindow = new Vector3(0f, 4.3f, 1.2f);
        static readonly Vector3 SillTop = new Vector3(0f, S.SillHeight, -0.03f);

        [Test]
        public void AcInWindowIsRedWithWidthMismatchAgainstTape()
        {
            AddTape(new Vector3(-0.75f, 3.6f, -0.05f), new Vector3(0.75f, 3.6f, -0.05f));
            var p = Take("window-ac-small");
            Assert.IsTrue(PlaceFrom(AboveWindow, SillTop), m_Tool.LastAction);
            var b = p.Box.bounds;
            Assert.AreEqual(S.SillHeight, b.min.y, 2 * Mm, "sits on the sill");
            Assert.GreaterOrEqual(b.min.z, -S.WindowRecess - 1 * Mm, "slid out of the glass");
            Assert.AreEqual(FitStatus.Red, p.Fit.Status, p.Fit.ToString());
            StringAssert.StartsWith("Window too wide by 500 mm", p.Fit.Headline);
            StringAssert.Contains("1500 mm opening, unit fits 590–1000 mm", p.Fit.Headline);
            Assert.AreEqual("Too small for this window", p.Fit.Verdict);
            Assert.AreEqual(FitAction.FindBigger, p.Fit.Action);
            Assert.AreEqual("window ac", p.Fit.ActionArg);
            Assert.AreEqual("tape #1", p.Fit.OpeningSource);
            Assert.That(Vector3.Angle(p.transform.forward, Vector3.forward), Is.LessThan(0.5f), $"square to the glass, not yawed to the viewer (camera: {(Camera.main != null ? Camera.main.transform.position.ToString() : "none")})");
            Assert.AreEqual(0, p.Fit.Collisions.Count, string.Join(",", p.Fit.Collisions));
            Assert.AreEqual(PartOutline.Red, p.Outline.CurrentColor);
        }

        [Test]
        public void AcWithoutTapeScansTheOpening()
        {
            var p = Take("window-ac-small");
            // Off-centre and viewed at an angle: still square to the window, and the scan reads the full opening.
            Assert.IsTrue(PlaceFrom(new Vector3(-1.5f, 4.3f, 1.5f), new Vector3(0.4f, S.SillHeight, -0.03f)));
            Assert.That(Vector3.Angle(p.transform.forward, Vector3.forward), Is.LessThan(0.5f));
            Assert.AreEqual("scan", p.Fit.OpeningSource);
            Assert.AreEqual(1500f, p.Fit.OpeningMm.Value, 2f);
            Assert.AreEqual(FitStatus.Red, p.Fit.Status);
        }

        [Test]
        public void AcInANarrowerOpeningFits()
        {
            AddTape(new Vector3(-0.4f, 3.6f, -0.05f), new Vector3(0.4f, 3.6f, -0.05f)); // pretend 800 mm
            var p = Take("window-ac-small");
            Assert.IsTrue(PlaceFrom(AboveWindow, SillTop));
            Assert.AreEqual(FitStatus.Green, p.Fit.Status, p.Fit.ToString());
            Assert.AreEqual("Fits the 800 mm window (590–1000 mm)", p.Fit.Headline);
            Assert.AreEqual("Fits this window", p.Fit.Verdict);
            Assert.AreEqual(200f, p.Fit.SpareMm.Value, 1f);
        }

        [Test]
        public void AcAimedAtSillFrontFromTheGroundGoesOnTop()
        {
            var p = Take("window-ac-small");
            Assert.IsTrue(PlaceFrom(MeasureScenarios.SpawnEye, new Vector3(0f, S.SillHeight - 0.02f, 0.05f)), m_Tool.LastAction);
            Assert.AreEqual(S.SillHeight, p.Box.bounds.min.y, 2 * Mm);
            Assert.That(Vector3.Angle(p.WorldMountDirection, Vector3.down), Is.LessThan(0.5f));
        }

        // ---------------- tool behaviour ----------------

        [Test]
        public void PressOnPlacedPartPicksItUp()
        {
            var p = Take("hidden-hanger-5k");
            Assert.IsTrue(PlaceFrom(Ladder, FasciaPoint));
            var at = p.WorldBoxCentre;
            Hub.RaisePressStart(ToolHand.Right, From(MeasureScenarios.SpawnEye, at));
            Assert.AreEqual(p, m_Tool.Held);
            Assert.IsFalse(p.Placed);
            Hub.RaisePressEnd(ToolHand.Right, From(Ladder, FasciaPoint + new Vector3(-1f, 0f, 0f)));
            Assert.IsTrue(p.Placed);
            Assert.AreEqual(-0.5f, p.transform.position.x, 0.01f);
            Assert.AreEqual(1, Notebook.Entries.Count(e => e.Tool == "part"), "moving a part updates its entry");
        }

        [Test]
        public void UndoAndClearRemoveParts()
        {
            Take("hidden-hanger-5k");
            Assert.IsTrue(PlaceFrom(Ladder, FasciaPoint));
            Take("hidden-hanger-5k");
            Assert.IsTrue(PlaceFrom(Ladder, FasciaPoint + new Vector3(-1f, 0, 0)));
            Assert.AreEqual(2, m_Tool.PlacedParts.Count);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Undo);
            Assert.AreEqual(1, m_Tool.PlacedParts.Count);
            Assert.AreEqual(1, Notebook.Entries.Count(e => e.Tool == "part"));
            Take("window-ac-small");
            Hub.RaiseButton(ToolHand.Right, ToolButton.Undo);
            Assert.IsNull(m_Tool.Held, "undo while holding puts the held part back");
            Assert.AreEqual(1, m_Tool.PlacedParts.Count);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Clear);
            Assert.AreEqual(0, m_Tool.PlacedParts.Count);
            Assert.AreEqual(0, Notebook.Entries.Count(e => e.Tool == "part"));
        }

        /// UX W0.4 (A13): putting the Part tool away (a ring spin) returns the held part to Find parts instead of
        /// destroying it; picking the tool again (or taking its card) puts the same part back in the hand.
        [Test]
        public void ToolSwitchParksTheHeldPart()
        {
            var p = Take("hidden-hanger-5k");
            m_Tool.Equip(false);
            Assert.IsNull(m_Tool.Held);
            Assert.IsTrue(p != null, "not destroyed");
            Assert.AreEqual(p, m_Tool.Parked);
            Assert.IsFalse(p.gameObject.activeSelf, "out of the scene while parked");
            m_Tool.Equip(true);
            Assert.AreEqual(p, m_Tool.Held, "back in the hand");
            Assert.IsNull(m_Tool.Parked);
            Assert.IsTrue(p.gameObject.activeSelf);
            m_Tool.Equip(false);
            Assert.IsTrue(m_Tool.TryUnpark("hidden-hanger-5k"), "taking its card again brings it back");
            Assert.AreEqual(p, m_Tool.Held);
            Assert.IsTrue(PlaceFrom(Ladder, FasciaPoint), m_Tool.LastAction);
            Assert.IsTrue(p.Placed);
        }

        [Test]
        public void TakingAnotherPartReplacesTheHeldOne()
        {
            var a = Take("hidden-hanger-5k");
            var b = Take("window-ac-small");
            Assert.IsTrue(a == null, "unplaced held part destroyed");
            Assert.AreEqual(b, m_Tool.Held);
            m_Tool.Equip(false);
            Assert.IsNull(m_Tool.Held);
        }

        [Test]
        public void OtherToolsIgnoreClicksWhilePartToolPlaces()
        {
            Take("hidden-hanger-5k");
            Assert.IsTrue(PlaceFrom(Ladder, FasciaPoint));
            Assert.AreEqual(0, Notebook.Entries.Count(e => e.Tool == "measure"));
        }

        [Test]
        public void CatalogSearchMatchesServerKeywords()
        {
            Assert.AreEqual(new[] { "hidden-hanger-5k" }, s_Catalog.Search("gutter hanger").Select(s => s.id).ToArray());
            // assetgen: the window frame fixture matches "window" too; the AC comes first (catalog order).
            Assert.AreEqual("window-ac-small", s_Catalog.Search("window AC").Select(s => s.id).First());
            Assert.AreEqual(new[] { "window-frame-58x46" }, s_Catalog.Search("replacement frame").Select(s => s.id).ToArray());
            Assert.AreEqual(s_Catalog.entries.Count, s_Catalog.Search("zzz").Count, "no match → everything");
            var s0 = s_Catalog.Search("hanger")[0];
            Assert.AreEqual(4.27f, s0.price_usd.Value, 1e-4f);
            Assert.AreEqual("/parts/hidden-hanger-5k/model.glb", s0.model_url);
            Assert.IsNotNull(s_Catalog.Find("hidden-hanger-5k").image);
        }

        /// D2 (SPEC §9): a units switch re-words the placed part's fit (in the user's unit), its callout's size line
        /// (one unit) and its notebook row; the diagnostic Headline and the raw Label stay metric; nothing moves.
        [Test]
        public void UnitsSwitch_RewordsTheFitAndTheCallout()
        {
            AddTape(new Vector3(-2.1f, 6.1f, 0.15f), new Vector3(2.1f, 6.1f, 0.15f));
            var p = Take("hidden-hanger-5k");
            Assert.IsTrue(PlaceFrom(Ladder, FasciaPoint));
            var pos = p.transform.position;
            string headline = p.Fit.Headline;
            var row = Notebook.Entries.Last(e => e.Tool == "part");
            string raw = row.Label;
            Assert.AreEqual("8 needed along the 4.20 m run · every 600 mm", p.Fit.Reason, "metric (the fixture's pin)");
            StringAssert.StartsWith("127 × 38 × 45 mm\n", p.Outline.CalloutText, "one unit: no inches line");
            try
            {
                AirTools.UI.UnitsSwitch.Use(UnitSystem.Imperial);   // this session only (PlayerPrefs untouched)
                Assert.AreEqual("8 needed along the 13′ 9⅜″ run · every 1′ 11⅝″", p.Fit.Reason);
                StringAssert.StartsWith("5 × 1½ × 1¾ in\n", p.Outline.CalloutText);
                StringAssert.DoesNotContain("mm", p.Outline.CalloutText);
                StringAssert.Contains("✓ Fits the fascia", p.Outline.CalloutText);
                Assert.AreEqual(headline, p.Fit.Headline, "the diagnostic headline stays raw");
                Assert.AreEqual(raw, row.Label);
                Assert.AreEqual(pos, p.transform.position, "nothing moves");
                Assert.AreEqual("✓ Fits the fascia", row.DisplayValue);
            }
            finally { AirTools.UI.UnitsSwitch.Use(UnitSystem.Metric); }
        }
    }
}

namespace AirTools.Tests
{
    /// Randomised placements (25 seeds each) through the same PartScenarios the in-Simulator harness runs.
    public class PartScenarioTests : FacadeToolFixture
    {
        GameObject m_Parts;
        PartTool m_Tool;
        PartLoader m_Loader;

        [SetUp]
        public void CreatePartTool()
        {
            Tool.Equip(false);
            var catalog = UnityEditor.AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath) ?? PartCatalogBuilder.Build();
            m_Parts = new GameObject("parts-scenarios");
            m_Loader = m_Parts.AddComponent<PartLoader>();
            m_Loader.catalog = catalog;
            m_Tool = m_Parts.AddComponent<PartTool>();
            m_Tool.SetInput(Hub);
            m_Tool.Equip(true);
        }

        [TearDown]
        public void DestroyParts()
        {
            m_Tool.ClearAll();
            Object.DestroyImmediate(m_Parts);
        }

        static System.Collections.Generic.IEnumerable<int> Seeds() => Enumerable.Range(1, 25);

        [Test] public void HangerNearFascia([ValueSource(nameof(Seeds))] int seed) => Assert(PartScenarios.HangerOnFascia(m_Tool, m_Loader, null, seed, proximity: true));
        [Test] public void HangerByRayFromLadder([ValueSource(nameof(Seeds))] int seed) => Assert(PartScenarios.HangerOnFascia(m_Tool, m_Loader, null, seed, proximity: false));
        [Test] public void AcOnSillWithTape([ValueSource(nameof(Seeds))] int seed) => Assert(PartScenarios.AcOnSill(m_Tool, m_Loader, null, seed, withTape: true));
        [Test] public void AcOnSillScanned([ValueSource(nameof(Seeds))] int seed) => Assert(PartScenarios.AcOnSill(m_Tool, m_Loader, null, seed, withTape: false));

        static void Assert(AirTools.Dev.ScenarioResult r) => NUnit.Framework.Assert.IsTrue(r.Passed, r.ToString());
    }
}
