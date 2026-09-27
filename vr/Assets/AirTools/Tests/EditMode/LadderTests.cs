using System.Collections.Generic;
using System.Linq;
using AirTools.Structure;
using AirTools.Tools;
using NUnit.Framework;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Tests
{
    /// presence.md S4 (P6 ladder check, P7 fall edges): the pure maths. Every [A] EditMode item of S4 is here; the
    /// engine-side probes (ground, clearance, collision on the built facade) are in LadderSceneTests below.
    public class LadderTests
    {
        const double Ft = LadderMath.MetresPerFoot;

        // ---------------- S4 maths table ----------------

        [Test]
        public void FasciaTop_6_20_Gives1_60Out_75_52Deg_28ft()
        {
            var s = LadderMath.Solve(S.FasciaTop, LadderSupport.Landing);
            Assert.AreEqual(1.601, s.FootOut, 0.005, "foot distance d = h/√15");
            Assert.AreEqual(75.52, s.AngleDeg, 0.05, "acos(1/4)");
            Assert.AreEqual(28, s.SizeFt, "size");
            Assert.AreEqual(6.40, s.ContactLength, 0.005, "working length Lw = h·4/√15");
            Assert.AreEqual(0.944, s.Extension, 0.001, "extension e = 0.914 / sin 75.52°");
            Assert.AreEqual(7.35, s.RailNeeded, 0.005, "rail needed R = Lw + e");
            Assert.AreEqual(24.1, s.RailNeeded / Ft, 0.05, "R in feet");
            Assert.AreEqual(0.914, s.AboveEdge, 0.001, "3 ft above the landing");
            Assert.AreEqual(25, LadderMath.MaxWorkingFt(s.SizeFt), "28 ft reaches 25 ft");
        }

        [Test]
        public void Sill_3_50_NoLanding_Gives16ft()
        {
            var s = LadderMath.Solve(S.SillHeight, LadderSupport.Wall);
            Assert.AreEqual(3.61, s.ContactLength, 0.005, "Lw");
            Assert.AreEqual(0.904, s.FootOut, 0.001, "d");
            Assert.AreEqual(0.0, s.Extension, 1e-9, "no landing, no extension");
            Assert.AreEqual(11.9, s.RailNeeded / Ft, 0.05, "R in feet");
            Assert.AreEqual(16, s.SizeFt);
            Assert.AreEqual(13, LadderMath.MaxWorkingFt(16), "16 ft reaches 13 ft");
        }

        [Test]
        public void TheIdealAngleIsAcosAQuarter()
        {
            Assert.AreEqual(75.5225, LadderMath.IdealAngleDeg, 1e-4);
            Assert.AreEqual(1.0328, LadderMath.WorkingLength(1.0), 1e-4);
            Assert.AreEqual(LadderMath.WorkingLength(5.0) / 4.0, LadderMath.FootDistance(5.0), 1e-12, "d = Lw / 4");
        }

        // ---------------- sizes ----------------

        [Test]
        public void A24ftLadderIsNeverChosenWhenTheRailNeedIsOver21ft()
        {
            for (double r = 0.1; r <= 12.5; r += 0.001)
            {
                int size = LadderMath.SizeFor(r);
                if (r / Ft > 21.0 + 1e-6) Assert.AreNotEqual(24, size, $"R = {r:0.000} m = {r / Ft:0.000} ft");
                if (size > 0) Assert.GreaterOrEqual(LadderMath.MaxWorkingFt(size) + 1e-6, r / Ft, "the chosen ladder reaches");
            }
            Assert.AreEqual(24, LadderMath.SizeFor(21.0 * Ft), "OCWR: a 24 ft ladder reaches 21 ft");
            Assert.AreEqual(28, LadderMath.SizeFor(21.01 * Ft));
        }

        [TestCase(16, 13)]
        [TestCase(20, 17)]
        [TestCase(24, 21)]
        [TestCase(28, 25)]
        [TestCase(32, 29)]
        [TestCase(36, 33)]
        [TestCase(40, 36)]
        public void MaxWorkingLengthIsNominalMinusOverlap(int nominal, int working) => Assert.AreEqual(working, LadderMath.MaxWorkingFt(nominal));

        [Test]
        public void SmallestSizeThatReaches_NoneOver36ftUsable()
        {
            Assert.AreEqual(16, LadderMath.SizeFor(0.5));
            Assert.AreEqual(20, LadderMath.SizeFor(13.5 * Ft));
            Assert.AreEqual(40, LadderMath.SizeFor(35.9 * Ft));
            Assert.AreEqual(0, LadderMath.SizeFor(36.1 * Ft), "taller than any stock extension ladder");
            var tall = LadderMath.Solve(12.0, LadderSupport.Landing);
            Assert.AreEqual(0, tall.SizeFt);
            Assert.AreEqual(LadderVerdict.Red, LadderMath.Evaluate(tall, LadderChecks.Clear).Verdict);
        }

        // ---------------- classes ----------------

        [TestCase(0.0, LadderVerdict.Green)]
        [TestCase(2.9, LadderVerdict.Green)]
        [TestCase(3.0, LadderVerdict.Green)]
        [TestCase(3.1, LadderVerdict.Amber)]
        [TestCase(-4.5, LadderVerdict.Amber)]
        [TestCase(6.0, LadderVerdict.Amber)]
        [TestCase(6.1, LadderVerdict.Red)]
        [TestCase(12.0, LadderVerdict.Red)]
        public void FootingClasses(double slopeDeg, LadderVerdict expected) => Assert.AreEqual(expected, LadderMath.FootingClass(slopeDeg));

        [TestCase(75.52, LadderVerdict.Green)]
        [TestCase(77.5, LadderVerdict.Green)]
        [TestCase(73.6, LadderVerdict.Green)]
        [TestCase(77.6, LadderVerdict.Amber)]
        [TestCase(70.6, LadderVerdict.Amber)]
        [TestCase(80.6, LadderVerdict.Red)]
        [TestCase(70.4, LadderVerdict.Red)]
        public void AngleClasses(double angleDeg, LadderVerdict expected) => Assert.AreEqual(expected, LadderMath.AngleClass(angleDeg));

        [TestCase(2.0, LadderVerdict.Green)]
        [TestCase(4.5, LadderVerdict.Amber)]
        [TestCase(7.0, LadderVerdict.Red)]
        public void FootingDecidesTheVerdictOnAnOtherwiseGoodLadder(double footingDeg, LadderVerdict expected)
        {
            var s = LadderMath.Solve(S.FasciaTop, LadderSupport.Landing);
            var r = LadderMath.Evaluate(s, new LadderChecks { GroundFound = true, FootingDeg = footingDeg });
            Assert.AreEqual(expected, r.Verdict, r.Reason);
            if (expected != LadderVerdict.Green) StringAssert.Contains("ground slopes", r.Reason);
        }

        // ---------------- dragging the foot ----------------

        [Test]
        public void AFootDraggedTo0_5mOutIsRed_1To12()
        {
            var s = LadderMath.SolveFoot(S.FasciaTop, 0.5, LadderSupport.Landing);
            var r = LadderMath.Evaluate(s, LadderChecks.Clear);
            Assert.AreEqual(LadderVerdict.Red, r.Verdict);
            Assert.AreEqual("1 : 12", r.Ratio);
            StringAssert.Contains("1 : 12", r.Reason);
            StringAssert.Contains("too steep", r.Reason);
            Assert.AreEqual(85.39, s.AngleDeg, 0.01);
        }

        [Test]
        public void AFootTooFarOutIsShallow_AndTheLadderResolves()
        {
            var s = LadderMath.SolveFoot(S.FasciaTop, 2.0, LadderSupport.Landing);   // 1 : 3.1
            var r = LadderMath.Evaluate(s, LadderChecks.Clear);
            Assert.AreEqual("1 : 3.1", r.Ratio);
            Assert.AreEqual(LadderVerdict.Amber, r.Verdict, "72.1° is 3.4° off");
            StringAssert.Contains("shallow", r.Reason);
            double h = S.FasciaTop;
            Assert.AreEqual(System.Math.Sqrt(h * h + 4.0), s.ContactLength, 1e-9, "contact length re-solves");
            Assert.AreEqual(0.914, s.AboveEdge, 0.001, "still sized 3 ft past the landing");

            var ideal = LadderMath.Solve(S.FasciaTop, LadderSupport.Landing);
            Assert.AreEqual("1 : 3.9", LadderMath.Evaluate(ideal, LadderChecks.Clear).Ratio, "h/d = √15 = 3.87");
        }

        [Test]
        public void FootOutIsMeasuredStraightOutFromTheSupportFace()
        {
            var top = new Vector3(0.3f, 6.2f, 0.152f);
            var foot = LadderMath.FootPoint(top, new Vector3(0f, 0.2f, 2f), 1.6, 0f);
            Assert.AreEqual(0.3f, foot.x, 1e-5f);
            Assert.AreEqual(0f, foot.y, 1e-6f);
            Assert.AreEqual(1.752f, foot.z, 1e-5f);
            Assert.AreEqual(1.6, LadderMath.FootOutFor(top, Vector3.forward, foot), 1e-5);
            Assert.AreEqual(1.1, LadderMath.FootOutFor(top, Vector3.forward, foot + new Vector3(0.4f, 0f, -0.5f)), 1e-5, "sideways doesn't count");
            Assert.AreEqual(0.05, LadderMath.FootOutFor(top, Vector3.forward, new Vector3(0f, 0f, -3f)), 1e-9, "never behind the wall");
        }

        // ---------------- verdict rules ----------------

        [Test]
        public void TheFasciaLadderIsGreen_WithItsLabel()
        {
            var s = LadderMath.Solve(S.FasciaTop, LadderSupport.Landing);
            var r = LadderMath.Evaluate(s, LadderChecks.Clear);
            Assert.AreEqual(LadderVerdict.Green, r.Verdict, r.Reason);
            Assert.AreEqual("28 ft extension ladder · foot 1.60 m out · 75.5° · 0.91 m above the edge", LadderMath.Label(s));
            Assert.AreEqual("28 ft extension ladder · foot 1.60 m out · 75.5° · 0.91 m above the edge · Green", LadderMath.Summary(s, r));
            Assert.AreEqual("28 ft extension ladder", LadderMath.SearchQuery(s.SizeFt));
            StringAssert.StartsWith("✓", LadderMath.VerdictLine(r));
            Assert.AreEqual("Set up right", LadderMath.VerdictTitle(r.Verdict));
            Assert.AreEqual("Guide only — follow the ladder's label and OSHA 1926.1053.", LadderMath.Disclaimer);
        }

        [Test]
        public void TheSillLadderHasNoLandingPart()
        {
            var s = LadderMath.Solve(S.SillHeight, LadderSupport.Wall);
            Assert.AreEqual("16 ft extension ladder · foot 0.90 m out · 75.5°", LadderMath.Label(s));
            Assert.AreEqual(LadderVerdict.Green, LadderMath.Evaluate(s, LadderChecks.Clear).Verdict);
        }

        [Test]
        public void GutterWorkWithNoLandingIsAmber_UseAStandoff()
        {
            var s = LadderMath.Solve(6.1, LadderSupport.GutterNoLanding);
            var r = LadderMath.Evaluate(s, LadderChecks.Clear);
            Assert.AreEqual(LadderVerdict.Amber, r.Verdict);
            Assert.AreEqual("don't lean on the gutter — use a standoff", r.Reason);
        }

        [Test]
        public void CollisionOrNoGroundIsRed_BeforeAnythingElse()
        {
            var s = LadderMath.Solve(S.FasciaTop, LadderSupport.Landing);
            var hit = LadderMath.Evaluate(s, new LadderChecks { GroundFound = true, Collision = true, CollisionWith = "the gutter", FootingDeg = 4 });
            Assert.AreEqual(LadderVerdict.Red, hit.Verdict);
            StringAssert.StartsWith("hits the gutter", hit.Reason);
            var air = LadderMath.Evaluate(s, new LadderChecks { GroundFound = false });
            Assert.AreEqual(LadderVerdict.Red, air.Verdict);
            Assert.AreEqual("no ground under the foot", air.Reason);
            StringAssert.StartsWith("✗", LadderMath.VerdictLine(air));
        }

        [Test]
        public void ShortOfTheLandingIsAmber()
        {
            var s = LadderMath.Solve(S.FasciaTop, LadderSupport.Landing);
            s.AboveEdge = 0.5;
            Assert.AreEqual(LadderVerdict.Amber, LadderMath.Evaluate(s, LadderChecks.Clear).Verdict);
        }

        [Test]
        public void RungsEvery30cm()
        {
            Assert.AreEqual(24, LadderMath.RungCount(7.35));
            Assert.AreEqual(0, LadderMath.RungCount(0.2));
            Assert.AreEqual(1, LadderMath.RungCount(0.4));
        }

        // ---------------- P7 fall edges on the synthetic facade ----------------

        static FallEdge[] Named(List<FallEdge> edges, string name) => edges.Where(e => e.Edge.Name == name).ToArray();

        [Test]
        public void FallEdges_FlagTheFasciaTopAndTheSill_NotTheLedge()
        {
            var edges = FallEdgeMath.SyntheticFacadeEdges();
            var fascia = Named(edges, "Fascia").Where(e => e.Flagged).ToArray();
            var sill = Named(edges, "Sill").Where(e => e.Flagged).ToArray();
            var ledge = Named(edges, "Ledge/Slab");
            Assert.AreEqual(1, fascia.Length, "the fascia's front top edge");
            Assert.AreEqual(S.FasciaTop, fascia[0].DropM, 1e-4f);
            Assert.AreEqual(S.FasciaTop, fascia[0].Edge.A.y, 1e-4f);
            Assert.AreEqual(S.FasciaProud, fascia[0].Edge.A.z, 1e-4f, "the front face, proud of the wall");
            Assert.AreEqual(1f, fascia[0].Edge.Outward.z, 1e-5f, "falls away from the wall");
            Assert.AreEqual(S.FasciaLength, fascia[0].Edge.Length, 1e-4f);
            Assert.AreEqual(1, sill.Length, "the sill's front edge");
            Assert.AreEqual(S.SillHeight, sill[0].DropM, 1e-4f);
            Assert.Greater(ledge.Length, 0, "the ledge is a candidate");
            Assert.IsFalse(ledge.Any(e => e.Flagged), "the 1.0 m ledge is not a fall edge");
            Assert.AreEqual(1f, ledge.Max(e => e.DropM), 0.02f, "about a metre up");
            Assert.AreEqual(2, edges.Count(e => e.Flagged), "only the fascia and the sill");
        }

        [Test]
        public void FallEdges_TheWallSideAndBoxEndsAreNotEdges()
        {
            var edges = FallEdgeMath.SyntheticFacadeEdges();
            // The fascia's back edge meets the wall, the sill's back edge the window; ends are 2.5 / 15 cm long.
            Assert.IsFalse(edges.Any(e => e.Edge.Name == "Fascia" && e.Edge.Outward.z < -0.5f), "fascia back edge (wall rises beside it)");
            Assert.IsFalse(edges.Any(e => e.Edge.Name == "Sill" && e.Edge.Outward.z < -0.5f), "sill back edge (window rises beside it)");
            Assert.IsFalse(edges.Any(e => e.Edge.Length < FallEdgeMath.MinLengthM), "box ends");
            Assert.IsFalse(edges.Any(e => e.Edge.Name.StartsWith("Wall") || e.Edge.Name.StartsWith("Gutter") || e.Edge.Name == "Door"), "not walking surfaces");
        }

        [Test]
        public void FallEdges_Classification()
        {
            var flat = new EdgeCandidate { Name = "e", A = new Vector3(0, 3, 0), B = new Vector3(2, 3, 0), Outward = Vector3.forward };
            Assert.IsTrue(FallEdgeMath.Classify(flat, 0f, 1.8f).Flagged, "6 ft exactly");
            Assert.IsFalse(FallEdgeMath.Classify(flat, 0f, 1.79f).Flagged, "under 6 ft");
            Assert.IsFalse(FallEdgeMath.Classify(flat, 0.5f, 3f).Flagged, "no surface at the edge's level (a line on a wall)");
            var tilted = flat; tilted.B = new Vector3(2, 3 + 2f * Mathf.Tan(12f * Mathf.Deg2Rad), 0);
            Assert.AreEqual(12f, FallEdgeMath.TiltDeg(tilted.A, tilted.B), 1e-3f);
            Assert.IsFalse(FallEdgeMath.Classify(tilted, 0f, 3f).Flagged, "a rake edge steeper than 10°");
            var gentle = flat; gentle.B = new Vector3(2, 3 + 2f * Mathf.Tan(9f * Mathf.Deg2Rad), 0);
            Assert.IsTrue(FallEdgeMath.Classify(gentle, 0f, 3f).Flagged, "within 10° of level");
            var stub = flat; stub.B = new Vector3(0.3f, 3, 0);
            Assert.IsFalse(FallEdgeMath.Classify(stub, 0f, 3f).Flagged, "30 cm stub");
        }

        [Test]
        public void FallEdges_AtMostThreeLabels_HighestFirst()
        {
            var e = new List<FallEdge>();
            foreach (var d in new[] { 2f, 6.2f, 1f, 3.5f, 4f })
                e.Add(new FallEdge { DropM = d, Flagged = d >= FallEdgeMath.MinDropM });
            var order = FallEdgeMath.LabelOrder(e);
            CollectionAssert.AreEqual(new[] { 1, 4, 3 }, order);
            Assert.AreEqual("1.8 m+ edge · fall protection", FallEdgeMath.LabelText);
            Assert.AreEqual("6.20 m drop · fall protection", FallEdgeMath.DropLabel(6.2f));
        }

        [Test]
        public void TheLadderSupportMagnet_PicksTheEaveForTheLiveAcceptanceRay()
        {
            // [A] Live: Ladder(0, 6.2, 0.13) from spawn (eye 1.6 m up). The ray grazes the gutter and meets the wall
            // above the eave; the nearest support edge within 25 cm is the fascia top.
            var candidates = new List<EdgeCandidate>();
            FallEdgeMath.SyntheticFacadeEdges(candidates);
            var eye = S.SpawnPosition + Vector3.up * 1.6f;
            var dir = (new Vector3(0f, 6.2f, 0.13f) - eye).normalized;
            int i = FallEdgeMath.NearestToRay(candidates, eye, dir, 0.25f, out var p, out float dist);
            Assert.GreaterOrEqual(i, 0);
            Assert.AreEqual("Fascia", candidates[i].Name);
            Assert.AreEqual(LadderSupport.Landing, candidates[i].Support);
            Assert.AreEqual(0f, p.x, 0.01f);
            Assert.AreEqual(S.FasciaTop, p.y, 1e-4f);
            Assert.Less(dist, 0.12f);
            // The window sill from the same spot.
            dir = (new Vector3(0.2f, S.SillHeight + 0.05f, 0.03f) - eye).normalized;
            i = FallEdgeMath.NearestToRay(candidates, eye, dir, 0.25f, out p, out _);
            Assert.AreEqual("Sill", candidates[i].Name);
            Assert.AreEqual(LadderSupport.Wall, candidates[i].Support);
        }

        [Test]
        public void RaySegmentDistance()
        {
            float d = FallEdgeMath.RaySegmentDistance(Vector3.zero, Vector3.forward, new Vector3(-1, 0.5f, 3), new Vector3(1, 0.5f, 3), out var p, out float t);
            Assert.AreEqual(0.5f, d, 1e-5f);
            Assert.AreEqual(3f, t, 1e-5f);
            Assert.AreEqual(0f, p.x, 1e-5f);
            d = FallEdgeMath.RaySegmentDistance(Vector3.zero, Vector3.forward, new Vector3(2, 0f, 3), new Vector3(4, 0f, 3), out p, out _);
            Assert.AreEqual(2f, d, 1e-5f, "clamped to the segment's end");
        }
    }

#if UNITY_EDITOR
    /// Engine side (physics on the real built facade): the known boxes match the builder, and the probes LadderTool
    /// uses find the ground, clear the gutter and see collisions.
    public class LadderSceneTests
    {
        GameObject m_Facade;

        [OneTimeSetUp]
        public void Build()
        {
            m_Facade = AirTools.Editor.SyntheticFacadeBuilder.CreateHierarchy();
            Physics.SyncTransforms();
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (m_Facade != null) Object.DestroyImmediate(m_Facade);
        }

        [Test]
        public void TheKnownBoxesAreTheBuildersColliders()
        {
            foreach (var box in FallEdgeMath.SyntheticFacade())
            {
                var t = m_Facade.transform.Find(box.Name);
                Assert.IsNotNull(t, box.Name);
                var col = t.GetComponent<BoxCollider>();
                Assert.IsNotNull(col, box.Name);
                // Compare the eight corners in facade space.
                for (int i = 0; i < 8; i++)
                {
                    var local = new Vector3((i & 1) == 0 ? box.Min.x : box.Max.x, (i & 2) == 0 ? box.Min.y : box.Max.y, (i & 4) == 0 ? box.Min.z : box.Max.z);
                    var expected = m_Facade.transform.InverseTransformPoint(t.TransformPoint(local));
                    var ours = box.ToParent(local);
                    Assert.Less(Vector3.Distance(expected, ours), 1e-4f, $"{box.Name} corner {i}");
                    var colLocal = col.center + Vector3.Scale(col.size * 0.5f, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Assert.Less(Vector3.Distance(colLocal, local), 1e-4f, $"{box.Name} collider corner {i}");
                }
            }
        }

        /// Every glyph the ladder and fall-edge copy prints is in the Inter atlas charset (a missing one draws a box).
        [Test]
        public void LadderCopyGlyphsAreInTheCharset()
        {
            var sb = new System.Text.StringBuilder(LadderMath.Disclaimer + FallEdgeMath.LabelText + FallEdgeMath.DropLabel(6.2f));
            foreach (var units in new[] { AirTools.Core.UnitSystem.Imperial, AirTools.Core.UnitSystem.Metric })   // D2: both
            {
            AirTools.UI.UiSettings.UseUnits(units);
            sb.Append(FallEdgeMath.LabelFor(units)).Append(FallEdgeMath.DropLabel(6.2f, units));
            foreach (var support in new[] { LadderSupport.Landing, LadderSupport.Wall, LadderSupport.GutterNoLanding })
            foreach (var d in new[] { 0.3, 0.5, 1.6, 2.0, 3.0 })
            foreach (var c in new[] { LadderChecks.Clear, new LadderChecks { GroundFound = false }, new LadderChecks { GroundFound = true, Collision = true, CollisionWith = "the gutter" },
                                      new LadderChecks { GroundFound = true, FootingDeg = 4.5 }, new LadderChecks { GroundFound = true, FootingDeg = 8 } })
            {
                var s = LadderMath.SolveFoot(6.2, d, support);
                var r = LadderMath.Evaluate(s, c);
                sb.Append(LadderMath.Summary(s, r)).Append(LadderMath.VerdictLine(r)).Append(r.Ratio).Append(LadderMath.VerdictTitle(r.Verdict));
                sb.Append(LadderTool.LabelFor(new LadderPlacement { Solution = s, Report = r }));
            }
            }
            foreach (char ch in sb.ToString())
                if (ch != '\n') Assert.IsTrue(AirTools.Editor.UiAssetsBuilder.Charset.IndexOf(ch) >= 0, $"'{ch}' (U+{(int)ch:X4}) is not in UiAssetsBuilder.Charset");
        }

        /// The ring's Ladder and Fall edges icons are in both Phosphor atlases (AirTools ▸ Build UI Assets after adding them).
        [Test]
        public void LadderIconsAreInTheIconAtlases()
        {
            var icons = AirTools.UI.UiTheme.Current.icons;
            foreach (var font in new[] { icons.regular, icons.fill })
            {
                Assert.IsNotNull(font, "icon font");
                foreach (char ch in AirTools.UI.Icons.Ladder + AirTools.UI.Icons.FallEdges)
                    Assert.IsTrue(font.characterLookupTable.ContainsKey(ch), $"{font.name} lacks U+{(int)ch:X4} — run AirTools ▸ Build UI Assets");
            }
        }

        [Test]
        public void VoiceAndRingNameTheTool()
        {
            Assert.IsTrue(ToolManager.TryParse("ladder", out var kind));
            Assert.AreEqual(ToolKind.Ladder, kind);
            Assert.IsTrue(ToolManager.TryParse(" Ladder ", out kind));
            Assert.AreEqual(ToolKind.Ladder, kind);
        }

        [Test]
        public void GroundUnderTheFoot_AndFlatFooting()
        {
            var foot = new Vector3(0f, 0f, 1.75f);
            Assert.IsTrue(LadderProbe.Ground(foot, Vector3.up, 1f, out var ground, out float slope));
            Assert.AreEqual(0f, ground.y, 1e-4f);
            Assert.AreEqual(0f, slope, 0.1f);
            Assert.IsFalse(LadderProbe.Ground(new Vector3(40f, 0f, 1.75f), Vector3.up, 1f, out _, out _), "off the ground box");
        }

        [Test]
        public void TheEaveContactClearsTheGutter_AndTheLadderDoesNotCollide()
        {
            var top = new Vector3(0f, S.FasciaTop, S.FasciaProud);
            var contact = LadderProbe.Clearance(top, Vector3.forward, Vector3.up, 1f);
            // The rail line at 75.5° must pass the gutter lip (z 0.152 at 6.10 m): out ≈ 0.127 − 0.10/√15 + margin.
            Assert.AreEqual(S.FasciaTop, contact.y, 1e-4f, "same height");
            Assert.That(contact.z, Is.InRange(0.125f, 0.145f), "rests on the gutter's lip, not through it");
            float railAtLip = contact.z + (S.FasciaTop - (S.FasciaBottom + 0.10f)) / Mathf.Sqrt(15f);
            Assert.GreaterOrEqual(railAtLip, S.FasciaProud + S.GutterWidth, "clears the lip");
            var s = LadderMath.Solve(contact.y, LadderSupport.Landing);
            var foot = LadderMath.FootPoint(contact, Vector3.forward, s.FootOut, 0f);
            Assert.IsFalse(LadderProbe.Collides(foot, contact, Vector3.forward, Vector3.up, 1f, out var with), $"hits {with}");
            // Through the gutter instead: collides.
            var through = LadderMath.FootPoint(top, Vector3.forward, s.FootOut, 0f);
            Assert.IsTrue(LadderProbe.Collides(through, top, Vector3.forward, Vector3.up, 1f, out with));
        }
    }
#endif
}
