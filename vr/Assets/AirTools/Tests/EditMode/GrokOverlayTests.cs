using System.Collections.Generic;
using System.Linq;
using AirTools.Agent.Grok;
using AirTools.Dev;
using AirTools.Scene;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// Grok lane G2 (3D overlays in the scene frame): the pure pieces, against payloads copied from the backend's tests,
    /// its docs/api.md examples and the running OFFLINE server (GrokG2Fixtures). No scene objects: these run offline.
    public class GrokOverlayTests
    {
        static JObject J(string json) => JObject.Parse(json);

        static Vector3 Flip(double[] p) => new Vector3((float)-p[0], (float)p[1], (float)p[2]);

        static Vector3 Horizontal(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // ---------------- show_coverage: the ring, the legs, the flip-last rule ----------------

        [Test]
        public void Coverage_ParsesTheFacadeBody()
        {
            var c = CoverageView.Parse(J(GrokG2Fixtures.FacadeCoverage));
            Assert.AreEqual("synthetic-facade", c.Site);
            Assert.IsFalse(c.Interior);
            Assert.IsTrue(c.HasRing);
            Assert.AreEqual(8, c.Sides.Count);
            CollectionAssert.AreEqual(new[] { "front-left", "back-left" }, c.Sides.Where(s => s.Seen).Select(s => s.Label).ToArray());
            Assert.AreEqual(new[] { "orbit", "orbit", "nadir_grid" }, c.Legs.Select(l => l.Pattern).ToArray());
            Assert.AreEqual(67.5, c.Legs[0].FromDeg.Value, 1e-9);
            Assert.AreEqual(45.0, c.Legs[0].SweepDeg.Value, 1e-9);
            Assert.IsNull(c.Legs[2].FromDeg, "nadir_grid has no bearing");
            Assert.AreEqual(2, CoverageGeometry.SeenCount(c));
            StringAssert.StartsWith("You've covered 2 of 8 sides", c.Caption);
        }

        [Test]
        public void Coverage_DirAndP_FollowTheContract_InTheSceneFrame()
        {
            var c = CoverageView.Parse(J(GrokG2Fixtures.FacadeCoverage));
            var d0 = CoverageGeometry.Dir(c, 0); var d90 = CoverageGeometry.Dir(c, 90);
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(c.ZeroDir[i], d0[i], 1e-9, "dir(0) = zero_dir");
                Assert.AreEqual(c.QuarterDir[i], d90[i], 1e-9, "dir(90) = quarter_dir");
            }
            var p = CoverageGeometry.P(c, 45, 10, 3);
            double k = System.Math.Sqrt(0.5);
            for (int i = 0; i < 3; i++)
                Assert.AreEqual(c.RingCentre[i] + 10 * (k * c.ZeroDir[i] + k * c.QuarterDir[i]) + (i == 1 ? 3 : 0), p[i], 1e-9);
        }

        [Test]
        public void Coverage_FlipIsAppliedLast_ToEveryPoint()
        {
            var c = CoverageView.Parse(J(GrokG2Fixtures.FacadeCoverage));
            foreach (double b in new[] { 0.0, 22.5, 45.0, 137.0, 270.0, 359.0 })
            {
                var scene = CoverageGeometry.P(c, b, c.RingRadius, 1.5);
                var unity = CoverageGeometry.PU(c, b, c.RingRadius, 1.5);
                Assert.That(Vector3.Distance(Flip(scene), unity), Is.LessThan(1e-4f), $"bearing {b}");
            }
        }

        /// docs/api.md §5 "Check it once: the green wedge must sit on the side the flight's cameras are on", with
        /// synthetic-facade's real cameras (all at +Z of the facade) and its real coverage body.
        [Test]
        public void Coverage_GreenWedgesSitOnTheCamerasSide()
        {
            var c = CoverageView.Parse(J(GrokG2Fixtures.FacadeCoverage));
            var centre = Horizontal(Flip(c.RingCentre));
            var cams = Vector3.zero;
            foreach (var p in GrokG2Fixtures.FacadeCameraPositions) cams += Horizontal(Flip(p));
            cams /= GrokG2Fixtures.FacadeCameraPositions.Length;
            var toCams = (cams - centre).normalized;
            float greenSum = 0f, redSum = 0f; int green = 0, red = 0;
            for (int k = 0; k < c.Sides.Count; k++)
            {
                var dir = (Horizontal(CoverageGeometry.WedgeCentre(c, k, 0.05)) - centre).normalized;
                float dot = Vector3.Dot(dir, toCams);
                if (c.Sides[k].Seen) { Assert.Greater(dot, 0.3f, $"green wedge {c.Sides[k].Label} faces the cameras"); greenSum += dot; green++; }
                else { redSum += dot; red++; }
            }
            Assert.AreEqual(2, green);
            Assert.Greater(greenSum / green, redSum / red + 0.5f, "green on the flight's side, red away from it");
            // A mirrored ring (bearings run the other way, the mistake the flip invites) puts the seen sides away.
            foreach (var s in c.Sides.Where(s => s.Seen))
            {
                var wrong = CoverageGeometry.PU(c, -s.BearingDeg, c.RingRadius * CoverageGeometry.LabelFraction, 0.05);
                Assert.Less(Vector3.Dot((Horizontal(wrong) - centre).normalized, toCams), 0f, $"mirrored {s.Label} would face away");
            }
        }

        [Test]
        public void Coverage_WedgeIsAFlatAnnulusSector_BetweenPoint7AndOneRadius()
        {
            var c = CoverageView.Parse(J(GrokG2Fixtures.FacadeCoverage));
            var verts = new List<Vector3>(); var tris = new List<int>();
            CoverageGeometry.Wedge(c, 1, 8, 0.05, verts, tris);
            Assert.AreEqual(18, verts.Count);
            Assert.AreEqual(8 * 6, tris.Count);
            var centre = Flip(c.RingCentre);
            for (int i = 0; i < verts.Count; i++)
            {
                float r = Horizontal(verts[i] - centre).magnitude;
                Assert.AreEqual(i % 2 == 0 ? c.RingRadius * 0.7 : c.RingRadius, r, 1e-3, $"vertex {i}");
                Assert.AreEqual(centre.y + 0.05f, verts[i].y, 1e-4f, "flat on the ground, lifted");
            }
            // It spans bearing 45 ± 22.5 (flip last): first and last outer vertices.
            Assert.Less(Vector3.Distance(verts[1], CoverageGeometry.PU(c, 22.5, c.RingRadius, 0.05)), 1e-3f);
            Assert.Less(Vector3.Distance(verts[17], CoverageGeometry.PU(c, 67.5, c.RingRadius, 0.05)), 1e-3f);
        }

        [Test]
        public void Coverage_LegsAreArcsAndASquare_AtTheirRadiusAndAltitude()
        {
            var c = CoverageView.Parse(J(GrokG2Fixtures.FacadeCoverage));
            var centre = Flip(c.RingCentre);
            var orbit = c.Legs[1];   // from the back round to the front: 157.5 … 382.5
            var arc = CoverageGeometry.Arc(c, orbit);
            Assert.Greater(arc.Count, 60);
            Assert.Less(Vector3.Distance(arc[0], CoverageGeometry.PU(c, 157.5, orbit.Radius, orbit.Altitude)), 1e-3f);
            Assert.Less(Vector3.Distance(arc[arc.Count - 1], CoverageGeometry.PU(c, 157.5 + 225, orbit.Radius, orbit.Altitude)), 1e-3f);
            foreach (var p in arc)
            {
                Assert.AreEqual(orbit.Radius, Horizontal(p - centre).magnitude, 1e-2);
                Assert.AreEqual(centre.y + orbit.Altitude, p.y, 1e-3);
            }
            Assert.IsFalse(CoverageGeometry.IsFullCircle(orbit));
            var grid = c.Legs[2];
            var square = CoverageGeometry.NadirSquare(c, grid);
            Assert.AreEqual(4, square.Count);
            foreach (var p in square)
            {
                Assert.AreEqual(grid.Radius * System.Math.Sqrt(2), Horizontal(p - centre).magnitude, 1e-2, "corners at half-size radius on both axes");
                Assert.AreEqual(centre.y + grid.Altitude, p.y, 1e-3);
            }
            Assert.AreEqual(2 * grid.Radius, Vector3.Distance(square[0], square[1]), 1e-2);
            // Drone icons: the orbit's looks at the middle, 30° down; the grid's straight down.
            var fwd = CoverageGeometry.DroneForward(c, orbit);
            Assert.AreEqual(-0.5f, fwd.y, 1e-3f, "tilted down by gimbal_pitch_deg 30");
            var drone = CoverageGeometry.DronePoint(c, orbit);
            Assert.Greater(Vector3.Dot(Horizontal(fwd).normalized, Horizontal(centre - drone).normalized), 0.999f, "toward the ring's centre");
            Assert.AreEqual(-1f, CoverageGeometry.DroneForward(c, grid).y, 1e-3f);
        }

        [Test]
        public void Coverage_KitchenIsInterior_NoRing_TheNoteIsTheCaption()
        {
            var c = CoverageView.Parse(J(GrokG2Fixtures.KitchenCoverage));
            Assert.AreEqual("kitchen", c.Site);
            Assert.IsTrue(c.Interior);
            Assert.IsFalse(c.HasRing);
            Assert.AreEqual(0, c.Legs.Count);
            StringAssert.StartsWith("This scan is only 3 metres across", c.Caption);
            Assert.IsEmpty(CoverageGeometry.Arc(c, new CoverageLeg { Pattern = "orbit", Radius = 1, Altitude = 1 }));
        }

        [Test]
        public void Coverage_EavePassIsAClosedFullCircle()
        {
            var c = CoverageView.Parse(J(GrokG2Fixtures.FacadeCoverage));
            var eave = new CoverageLeg { Pattern = "eave_pass", FromDeg = 0, SweepDeg = 360, GimbalPitchDeg = 10, Radius = 20, Altitude = 4 };
            Assert.IsTrue(CoverageGeometry.IsFullCircle(eave));
            var arc = CoverageGeometry.Arc(c, eave, 3);
            Assert.AreEqual(120, arc.Count, "the duplicate end dropped: the dasher closes it");
        }

        // ---------------- the frame-box → camera ray (show_labels, scene_pin) ----------------

        /// The backend's own test (tests/test_labels.py test_to_pin_lands_on_the_wall): a camera at the origin looking
        /// down −Z (R = diag(1, −1, −1)), fx = fy = 100, cx = cy = 50, 100 × 100; an outlet on a wall at z = −1.95.
        [Test]
        public void FrameRay_ThroughTheBoxCentre_LandsOnTheOutlet_AfterTheFlip()
        {
            var cam = new SceneCameraJson
            {
                id = "0001", R = new[] { new[] { 1.0, 0, 0 }, new[] { 0, -1.0, 0 }, new[] { 0, 0, -1.0 } }, t = new[] { 0.0, 0, 0 },
                position = new[] { 0.0, 0, 0 }, fx = 100, fy = 100, cx = 50, cy = 50, w = 100, h = 100,
            };
            var outlet = new[] { 0.2, 0.1, -1.95 };
            // _percent_box: x = R·(p − pos); u = fx·x/z + cx, v = fy·y/z + cy; a 4 % box, then /100.
            double xc = outlet[0], yc = -outlet[1], zc = -outlet[2];
            double u = 100 * xc / zc + 50, v = 100 * yc / zc + 50;
            var box = new[] { (float)(u - 2) / 100f, (float)(v - 2) / 100f, (float)(u + 2) / 100f, (float)(v + 2) / 100f };
            var ray = FrameRay.Local(cam, box);
            // The wall's face (z = −1.95 in both frames: the flip only negates x).
            float t = (-1.95f - ray.origin.z) / ray.direction.z;
            var hit = ray.origin + ray.direction * t;
            Assert.Less(Vector3.Distance(hit, Flip(outlet)), 0.01f, $"hit {hit} vs {Flip(outlet)}");
            Assert.AreEqual(0.5f, FrameRay.BoxCentre(null).x, 1e-6f);
        }

        /// Kitchen frame 0221 (the labels fixture's frame): the ray through the outlet box passes within a few cm of
        /// the pre-labelled outlet anchor (docs/api.md GET /scenes/kitchen/labels a18, seen in 0221), flipped.
        [Test]
        public void FrameRay_Kitchen0221_OutletBox_MeetsTheOutletAnchor()
        {
            var cam = SceneCameras.Parse("[" + GrokG2Fixtures.KitchenCamera0221 + "]")[0];
            var labels = LabelsView.Parse(J(GrokG2Fixtures.KitchenLabels));
            var outlet = labels.Labels.First(l => l.Kind == "outlet");
            var anchor = LabelsView.Parse(J(GrokG2Fixtures.KitchenSceneLabels)).Labels[0];
            var ray = FrameRay.Local(cam, outlet.Box);
            var p = Flip(anchor.Pos);
            float along = Vector3.Dot(p - ray.origin, ray.direction);
            Assert.Greater(along, 0.5f, "in front of the camera");
            float off = Vector3.Distance(ray.origin + ray.direction * along, p);
            Assert.Less(off, 0.06f, $"ray passes {off:0.000} m from the anchor");
            // And back: the anchor projects inside the frame, next to the box.
            Assert.IsTrue(SceneCameras.Project(cam, p, out var uv));
            Assert.That(uv.x, Is.InRange(0.10f, 0.20f));
            Assert.That(uv.y, Is.InRange(0.25f, 0.40f));
            // The same ray scene_pin casts (SceneCameras.PixelRay through the centre).
            var c = FrameRay.BoxCentre(outlet.Box);
            var pin = SceneCameras.PixelRay(cam, c.x, c.y);
            Assert.Less(Vector3.Distance(pin.direction, ray.direction), 1e-6f);
        }

        // ---------------- show_survey: severity colours, JSON → view model ----------------

        [Test]
        public void Severity_MapsToThemeRoles_WorstIsBiggest()
        {
            Assert.AreEqual(ColorRole.Danger, SeverityStyle.Role("severe"));
            Assert.AreEqual(ColorRole.Warning, SeverityStyle.Role("moderate"));
            Assert.AreEqual(SeverityStyle.Yellow, SeverityStyle.Role("Minor "));
            Assert.AreEqual(ColorRole.TextSecondary, SeverityStyle.Role("none"));
            Assert.IsTrue(SeverityStyle.Yellow == ColorRole.Warning || SeverityStyle.Yellow.ToString() == "Ink", "tape yellow (D3 Ink) or, before D3, the amber");
            Assert.Greater(SeverityStyle.MarkerScale("severe"), SeverityStyle.MarkerScale("moderate"));
            Assert.Greater(SeverityStyle.MarkerScale("moderate"), SeverityStyle.MarkerScale("minor"));
            Assert.AreEqual(3, SeverityStyle.Rank("SEVERE"));
            Assert.AreEqual(ColorRole.TextPrimary, SeverityStyle.Named("NoSuchRole", ColorRole.TextPrimary));
            Assert.AreEqual(ColorRole.Danger, SeverityStyle.Named("Danger", ColorRole.TextPrimary));
            // The yellow is yellow (D3's ink #FFD23F is 46°; before D3 the amber #FFB529 is 39°), and after D3 it is
            // clear of the caution orange moderate pins get.
            float yellow = Hue(RoleColor(SeverityStyle.Yellow));
            Assert.That(yellow, Is.InRange(35f, 65f), "minor pins are yellow");
            if (SeverityStyle.Yellow != ColorRole.Warning)
                Assert.GreaterOrEqual(Mathf.Abs(yellow - Hue(RoleColor(ColorRole.Warning))), 10f, "minor and moderate differ in hue");
            Assert.That(Hue(RoleColor(ColorRole.Danger)), Is.LessThan(15f).Or.GreaterThan(345f), "severe pins are red");
        }

        /// A role's default colour from the theme's token class (UiTheme.Color needs the asset; the fields don't).
        static Color RoleColor(ColorRole role)
        {
            string name = role.ToString();
            var field = typeof(UiTheme.Colors).GetField(char.ToLowerInvariant(name[0]) + name.Substring(1));
            Assert.IsNotNull(field, $"theme colour for {role}");
            return (Color)field.GetValue(new UiTheme.Colors());
        }

        static float Hue(Color c)
        {
            Color.RGBToHSV(c, out float h, out _, out _);
            return h * 360f;
        }

        [Test]
        public void Survey_ParsesTheBackendsShowSurvey()
        {
            var args = J(GrokG2Fixtures.KitchenShowSurvey);
            Assert.IsTrue(SurveyView.IsCondition(args));
            var s = SurveyView.Parse(args);
            Assert.AreEqual((string)J(GrokG2Fixtures.KitchenSurveyStarted)["survey_id"], s.SurveyId);
            Assert.AreEqual("AI triage from drone frames, not an inspection", s.Label);
            Assert.AreEqual(2, s.Pins.Count);
            var f1 = s.Pins[0];
            Assert.AreEqual("f1", f1.Id);
            Assert.AreEqual("severe", f1.Severity);
            Assert.AreEqual("Gutter sagging, joint split · 90 %", f1.LabelText);
            Assert.AreEqual("5 in aluminium K-style gutter section", f1.PartQuery);
            Assert.AreEqual(-1.4456, f1.P[0], 1e-9);
            var f2 = s.Pins[1];
            Assert.IsTrue(f2.Suspected);
            Assert.IsNull(f2.PartQuery);
            StringAssert.Contains("(suspected, verify on the roof)", f2.LabelText);
            Assert.AreEqual(new[] { "f1", "f2" }, s.Ranked().Select(p => p.Id).ToArray());
            Assert.AreEqual(2, s.Drawable);
        }

        [Test]
        public void Survey_B1DoorSurveyArgsAreNotACondition_PinsWithoutPointsAreListedNotDrawn()
        {
            var b1 = J(@"{""request_id"":""r1"",""label"":""cabinet_door"",""groups"":[{""w_mm"":262,""h_mm"":279,""count"":6,""ids"":[""o1""]}],""unverified"":[],""focus"":[]}");
            Assert.IsFalse(SurveyView.IsCondition(b1), "B1's card keeps show_survey without pins");
            Assert.IsTrue(SurveyView.IsDoorSurvey(b1), "groups: the legacy door survey card");
            Assert.IsTrue(SurveyView.IsCondition(J(@"{""survey_id"":""x"",""pins"":[]}")), "an empty survey is still a condition survey");
            var neither = J(@"{""survey_id"":""x""}");
            Assert.IsFalse(SurveyView.IsCondition(neither) || SurveyView.IsDoorSurvey(neither), "no pins, no groups: refused");
            var facade = SurveyView.Parse(J(GrokG2Fixtures.FacadeSurvey));
            Assert.AreEqual(3, facade.Pins.Count);
            Assert.AreEqual(2, facade.Drawable, "p: null is listed, not drawn");
            Assert.AreEqual(new[] { "f1", "f2", "f3" }, facade.Ranked().Select(p => p.Id).ToArray());
        }

        [Test]
        public void Survey_PolledBody_AndThePollStateMachine()
        {
            var body = SurveyView.Parse(J(GrokG2Fixtures.KitchenSurveyBody));
            Assert.AreEqual("done", body.Status);
            Assert.AreEqual("kitchen", body.Site);
            StringAssert.StartsWith("2 problems found", body.Spoken);
            CollectionAssert.AreEqual(new[] { "window (0701)" }, body.LookedFine);
            Assert.AreEqual(SurveyPollStep.Done, SurveyPoll.Next(200, "done", 3));
            Assert.AreEqual(SurveyPollStep.Poll, SurveyPoll.Next(200, "running", 3));
            Assert.AreEqual(SurveyPollStep.Failed, SurveyPoll.Next(200, "failed", 3));
            Assert.AreEqual(SurveyPollStep.Failed, SurveyPoll.Next(404, null, 3), "unknown survey (laptop restarted)");
            Assert.AreEqual(SurveyPollStep.Poll, SurveyPoll.Next(0, null, 3), "no answer: try again");
            Assert.AreEqual(SurveyPollStep.GiveUp, SurveyPoll.Next(200, "running", SurveyPoll.TimeoutSeconds + 1));
        }

        // ---------------- show_plan: JSON → view model, point → transform ----------------

        [Test]
        public void Plan_ParsesPointsAndRuns()
        {
            var hooks = PlanView.Parse(J(GrokG2Fixtures.KitchenPlanHooks));
            Assert.AreEqual("p-5e0c9a17", hooks.PlanId);
            Assert.AreEqual("Planned from the scan; check with the tape", hooks.Label);
            Assert.AreEqual(1, hooks.Segments.Count);
            Assert.AreEqual(0.548, hooks.Segments[0].LengthM, 1e-9);
            Assert.AreEqual(3, hooks.Points.Count);
            Assert.IsTrue(hooks.HasPoints);
            Assert.AreEqual(0.137f, PlanGeometry.Spacing(hooks), 0.002f, "the 137 mm place_array spacing");
            var led = PlanView.Parse(J(GrokG2Fixtures.KitchenPlanLed));
            Assert.IsFalse(led.HasPoints, "a run: place them → BOM");
            Assert.AreEqual(2.433, led.TotalLengthM, 1e-9);
            var fascia = PlanView.Parse(J(GrokG2Fixtures.FacadePlanFascia));
            Assert.AreEqual(8, fascia.Points.Count);
            Assert.AreEqual(0.557f, PlanGeometry.Spacing(fascia), 0.001f);
        }

        [Test]
        public void Plan_PointsGoThroughTheFlip_ThenTheContentTransform()
        {
            var plan = PlanView.Parse(J(GrokG2Fixtures.KitchenPlanHooks));
            var local = PlanGeometry.ContentPoints(plan);
            Assert.AreEqual(new Vector3(1.334f, 0.011f, -0.81f), local[0]);
            // Content: calibration × 2, moved by (1, 0, 3) (a hand-built TRS: no rotation).
            var m = Matrix4x4.identity;
            m.m00 = 2; m.m11 = 2; m.m22 = 2; m.m03 = 1; m.m13 = 0; m.m23 = 3;
            var world = PlanGeometry.WorldPoints(plan, m);
            Assert.Less(Vector3.Distance(world[0], new Vector3(1 + 2 * 1.334f, 2 * 0.011f, 3 + 2 * -0.81f)), 1e-5f);
            var mid = PlanGeometry.Midpoint(plan.Segments[0]);
            Assert.Less(Vector3.Distance(mid, new Vector3(1.324f, 0.011f, -0.946f)), 1e-4f);
            var fascia = PlanView.Parse(J(GrokG2Fixtures.FacadePlanFascia));
            Assert.Less(Vector3.Distance(PlanGeometry.Centroid(fascia), new Vector3(0f, 6.1f, 0.025f)), 1e-4f, "symmetric fascia: centred");
            Assert.Less(Vector3.Distance(PlanGeometry.Centroid(PlanView.Parse(J(GrokG2Fixtures.KitchenPlanLed))), new Vector3(1.358f, 0.02f, -0.003f)), 1e-3f, "a run: the segment's middle");
        }

        // ---------------- show_labels / the pre-labelled scan ----------------

        [Test]
        public void Labels_ParseTheLiveSetAndTheScanAnchors()
        {
            var live = LabelsView.Parse(J(GrokG2Fixtures.KitchenLabels));
            Assert.AreEqual("0221", live.FrameId);
            Assert.AreEqual(6, live.Labels.Count);
            var outlet = live.Labels[0];
            Assert.AreEqual("electrical outlet", outlet.Name);
            Assert.AreEqual("white duplex receptacle electrical outlet", outlet.SearchQuery);
            Assert.AreEqual(0.125f, outlet.Box[0], 1e-6f);
            Assert.IsFalse(live.Labels.Any(l => l.Greyed), "all ≥ 0.7");
            Assert.IsNull(outlet.Pos);
            var scan = LabelsView.Parse(J(GrokG2Fixtures.KitchenSceneLabels));
            Assert.AreEqual("kitchen", scan.Site);
            StringAssert.StartsWith("Grok's reading of the camera view", scan.Label);
            Assert.AreEqual(new Vector3(1.58f, -0.38f, 0.39f), PlanGeometry.ToContent(scan.Labels[0].Pos), "pos, flipped");
            Assert.AreEqual(4, scan.Labels[0].Frames.Count);
            var facade = LabelsView.Parse(J(GrokG2Fixtures.FacadeLabels));
            Assert.IsTrue(facade.Labels[1].Greyed, "0.65 < 0.7 is greyed");
            Assert.AreEqual("exterior door", facade.Labels[1].SearchQuery);
            var noQuery = LabelsView.Parse(J(@"{""labels"":[{""name"":""sink"",""confidence"":0.9},{""name"":"""",""confidence"":0.9}]}"));
            Assert.AreEqual(1, noQuery.Labels.Count, "nameless labels are skipped");
            Assert.AreEqual("sink", noQuery.Labels[0].SearchQuery, "no query: search the name");
        }

        // ---------------- the label diet, dashes ----------------

        [Test]
        public void LabelBudget_CaptionsFirst_ThenNewestItems_AtMost12()
        {
            CollectionAssert.AreEqual(new[] { 8, 1 }, LabelBudget.Split(12, new[] { 2, 1 }, new[] { 8, 8 }));
            CollectionAssert.AreEqual(new[] { 3, 0 }, LabelBudget.Split(12, new[] { 1, 8 }, new[] { 3, 5 }));
            CollectionAssert.AreEqual(new[] { 0 }, LabelBudget.Split(12, new[] { 13 }, new[] { 4 }));
            CollectionAssert.AreEqual(new[] { 6, 6 }, LabelBudget.Split(12, new int[0], new[] { 6, 9 }));
            Assert.AreEqual(12, LabelBudget.Max);
        }

        [Test]
        public void Dashes_CutTheLineIntoDashesAndGaps()
        {
            var line = new List<Vector3> { Vector3.zero, new Vector3(1f, 0f, 0f) };
            var dashes = new List<List<Vector3>>();
            Dashes.Split(line, false, 0.1f, 0.05f, dashes);
            Assert.AreEqual(7, dashes.Count);
            Assert.AreEqual(0.15f, dashes[1][0].x, 1e-5f);
            Assert.AreEqual(0.25f, dashes[1][dashes[1].Count - 1].x, 1e-5f);
            Assert.AreEqual(1f, dashes[6][dashes[6].Count - 1].x, 1e-5f);
            // A closed square: dashes turn its corners.
            var square = new List<Vector3> { Vector3.zero, Vector3.right, new Vector3(1, 0, 1), Vector3.forward };
            Dashes.Split(square, true, 0.35f, 0.2f, dashes);
            Assert.AreEqual(4f, Dashes.Length(square, true), 1e-5f);
            Assert.AreEqual(8, dashes.Count);
            Assert.IsTrue(dashes.Any(d => d.Count > 2), "a dash across a corner keeps the corner");
            Dashes.Pattern(0.548f, 16, 0.03f, out float dash, out float gap);
            Assert.AreEqual(0.03f, dash, 1e-6f, "never shorter than the minimum");
            Assert.AreEqual(0.018f, gap, 1e-6f);
        }
    }
}
