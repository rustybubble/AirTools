using System.Collections.Generic;
using System.Linq;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Scene;
using AirTools.Tools;
using NUnit.Framework;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Tests
{
    /// Shared fixture: the real synthetic facade (colliders on SceneSurface) + a real ToolInputHub + MeasureTool.
    public abstract class FacadeToolFixture
    {
        protected GameObject Facade;
        protected ToolInputHub Hub;
        protected MeasureTool Tool;
        GameObject m_Rig;

        [OneTimeSetUp]
        public void BuildFacade()
        {
            Facade = SyntheticFacadeBuilder.CreateHierarchy();
            Physics.SyncTransforms();
        }

        [OneTimeTearDown]
        public void DestroyFacade()
        {
            if (Facade != null) Object.DestroyImmediate(Facade);
        }

        [SetUp]
        public void CreateTool()
        {
            // D2: every fixture pins its unit (the Editor's saved choice must not leak in). These pin metric so their
            // "1.50 m" / "127 × 38 × 45 mm" expectations hold; UnitsSwitchTests covers the imperial default.
            AirTools.UI.UiSettings.UseUnits(UnitSystem.Metric);
            Notebook.Clear();
            AppState.Reset();
            m_Rig = new GameObject("tool-test");
            Hub = m_Rig.AddComponent<ToolInputHub>();
            Tool = m_Rig.AddComponent<MeasureTool>();
            Tool.SetInput(Hub);
            Tool.Equip(true);
        }

        [TearDown]
        public void DestroyTool()
        {
            if (Tool != null && Tool.viewRoot != null) Object.DestroyImmediate(Tool.viewRoot.gameObject);
            Object.DestroyImmediate(m_Rig);
            Notebook.Clear();
            AppState.Reset();
        }

        protected static Pose From(Vector3 origin, Vector3 target) => ToolInputHub.RayPose(origin, target);

        protected void Click(Vector3 origin, Vector3 target)
        {
            var p = From(origin, target);
            Hub.RaisePressStart(ToolHand.Right, p);
            Hub.RaisePressEnd(ToolHand.Right, p);
        }

        protected void Click(Vector3 target) => Click(MeasureScenarios.SpawnEye, target);
    }

    public class SnapServiceTests : FacadeToolFixture
    {
        const float Mm = 0.001f;

        static SurfaceHit Ray(Vector3 origin, Vector3 target)
        {
            Assert.IsTrue(SnapService.TryRaySnap(new Ray(origin, (target - origin).normalized), out var hit), "ray missed");
            return hit;
        }

        [Test]
        public void JambFaceHitIsExactInX()
        {
            var hit = Ray(MeasureScenarios.SpawnEye, new Vector3(-0.75f, 4.1f, -0.05f));
            Assert.AreEqual(-0.75f, hit.point.x, Mm);
        }

        [Test]
        public void GlassCornerSnapsToCorner()
        {
            var hit = Ray(new Vector3(0, 4.1f, 2.5f), new Vector3(-0.72f, 4.68f, -0.1f));
            Assert.AreEqual(SnapKind.Corner, hit.kind);
            Assert.Less(Vector3.Distance(hit.point, new Vector3(-0.75f, 4.7f, -0.1f)), Mm);
        }

        [Test]
        public void NearGlassEdgeSnapsToEdge()
        {
            var hit = Ray(new Vector3(0, 4.1f, 2.5f), new Vector3(-0.73f, 4.1f, -0.1f));
            Assert.AreEqual(SnapKind.Edge, hit.kind);
            Assert.AreEqual(-0.75f, hit.point.x, Mm);
            Assert.AreEqual(-0.1f, hit.point.z, Mm);
        }

        [Test]
        public void OpenWallFaceStaysOnFace()
        {
            var target = new Vector3(-2.5f, 3.0f, 0f);
            var hit = Ray(MeasureScenarios.SpawnEye, target);
            Assert.AreEqual(SnapKind.Face, hit.kind);
            Assert.Less(Vector3.Distance(hit.point, target), Mm);
        }

        [Test]
        public void InsideCornerBetweenLedgeAndWall()
        {
            var hit = Ray(new Vector3(S.LedgeCentreX, 2.3f, 1.3f), new Vector3(-3.48f, 1.0f, 0.02f));
            Assert.AreEqual(SnapKind.Corner, hit.kind);
            Assert.Less(Vector3.Distance(hit.point, new Vector3(-3.5f, 1.0f, 0f)), Mm);
        }

        [Test]
        public void OpeningCornerOnWallFace()
        {
            // Aim at the wall just outside the top-left of the opening: snaps to the opening corner on the wall face.
            var hit = Ray(MeasureScenarios.SpawnEye, new Vector3(-0.78f, 4.73f, 0f));
            Assert.AreEqual(SnapKind.Corner, hit.kind);
            Assert.Less(Vector3.Distance(hit.point, new Vector3(-0.75f, 4.7f, 0f)), Mm);
        }

        [Test]
        public void TrySnapFindsNearestSurfaceWithinRadius()
        {
            Assert.IsTrue(SnapService.TrySnap(new Vector3(-2.5f, 3.0f, 0.10f), out var hit, features: false));
            Assert.Less(Vector3.Distance(hit.point, new Vector3(-2.5f, 3.0f, 0f)), Mm);
            Assert.AreEqual(1f, Vector3.Dot(hit.normal, Vector3.forward), 1e-3f);
        }

        [Test]
        public void TrySnapReturnsNothingBeyondRadius()
        {
            Assert.IsFalse(SnapService.TrySnap(new Vector3(-2.5f, 3.0f, 0.16f), out _, features: false));
            Assert.IsFalse(SnapService.TrySnap(new Vector3(-2.5f, 3.0f, 0.5f), out _, radius: 0.3f, features: false));
        }

        [Test]
        public void TrySnapInsideABoxFindsNearestFace()
        {
            // 2 cm inside the wall behind the face → nearest face is the front (z = 0).
            Assert.IsTrue(SnapService.TrySnap(new Vector3(-2.5f, 3.0f, -0.02f), out var hit, features: false));
            Assert.AreEqual(0f, hit.point.z, Mm);
        }

        [Test]
        public void RayMissReturnsFalse()
        {
            Assert.IsFalse(SnapService.TryRaySnap(new Ray(MeasureScenarios.SpawnEye, Vector3.forward), out _));
        }
    }

    public class MeasureScenarioTests : FacadeToolFixture
    {
        public static IEnumerable<TestCaseData> Cases()
        {
            foreach (var s in MeasureScenarios.M2())
                for (int seed = 1; seed <= 25; seed++)
                    yield return new TestCaseData(s.Id, seed).SetName($"{s.Id}.seed{seed:00}");
        }

        [TestCaseSource(nameof(Cases))]
        public void GroundTruth(string id, int seed)
        {
            var s = MeasureScenarios.M2().Single(x => x.Id == id);
            var r = MeasureScenarios.Run(s, Tool, Hub, null, seed);
            Assert.IsTrue(r.Passed, r.ToString());
            Assert.AreEqual(1, Notebook.Entries.Count, "one notebook entry per measurement");
        }
    }

    public class MeasureToolBehaviourTests : FacadeToolFixture
    {
        /// D4 (SPEC §9): Line mode saves a tape at its 2nd point, so shapes and explicit finishes happen in Area mode.
        [SetUp] public void ShapesUseAreaMode() => Tool.AreaMode = true;

        static readonly Vector3 LeftJamb = new Vector3(-0.75f, 4.1f, -0.05f);
        static readonly Vector3 RightJamb = new Vector3(0.75f, 4.1f, -0.05f);

        /// UX W0.3 + D4: with the auto-save flag, the 2nd point of a tape saves it (no finish gesture); Area mode keeps
        /// going. Off (today), nothing is saved until finished, and the on-cursor chip says how.
        [Test]
        public void AutoSaveFlagSavesTapesAtTheSecondPoint()
        {
            bool was = MeasureTool.AutoSaveTwoPointTapes;
            try
            {
                MeasureTool.AutoSaveTwoPointTapes = true;
                Tool.AreaMode = false;   // Line mode
                Click(LeftJamb);
                Assert.AreEqual(0, Notebook.Entries.Count);
                Click(RightJamb);
                Assert.AreEqual(1, Notebook.Entries.Count, "saved at point 2");
                Assert.AreEqual(0, Tool.Session.Count);
                StringAssert.StartsWith("Distance 1.50 m", Notebook.Last.Label);
                Tool.AreaMode = true;
                Click(LeftJamb);
                Click(RightJamb);
                Assert.AreEqual(1, Notebook.Entries.Count, "Area mode: not saved at point 2");
                Assert.AreEqual(2, Tool.Session.Count);
            }
            finally { MeasureTool.AutoSaveTwoPointTapes = was; Tool.AreaMode = false; }
        }

        [Test]
        public void FinishChipSaysHowToSave()
        {
            try
            {
                AirTools.Input.InputMode.Override = false;
                Assert.AreEqual("", Tool.FinishHint);
                Click(LeftJamb);
                Assert.AreEqual("", Tool.FinishHint, "one point: nothing to save yet");
                Click(RightJamb);
                Assert.AreEqual("Pinch your left hand to save", Tool.FinishHint);
                AirTools.Input.InputMode.Override = true;
                Assert.AreEqual("Press B to save", Tool.FinishHint);
                Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
                Assert.AreEqual("", Tool.FinishHint);
            }
            finally { AirTools.Input.InputMode.Override = null; }
        }

        /// UX W0.4 (A13): switching tools mid-tape parks it; picking Measure again brings the points back.
        [Test]
        public void ToolSwitchKeepsTheOpenTape()
        {
            Click(LeftJamb);
            Assert.AreEqual(1, Tool.Session.Count);
            Tool.Equip(false);                                  // e.g. the ring settles on Level
            Assert.AreEqual(1, Tool.ParkedPoints, "parked, not lost");
            Click(RightJamb);                                   // clicks while put away do nothing
            Assert.AreEqual(1, Tool.Session.Count);
            Tool.Equip(true);
            Assert.AreEqual(0, Tool.ParkedPoints);
            Assert.AreEqual(1, Tool.Session.Count, "points intact");
            Click(RightJamb);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            Assert.AreEqual(1.5, Notebook.Last.ValueSI, 0.001, "the parked tape finishes as if never interrupted");
        }

        [Test]
        public void TwoClicksThenFinishButtonLogsDistance()
        {
            Click(LeftJamb);
            Click(RightJamb);
            Assert.AreEqual(0, Notebook.Entries.Count, "nothing logged until finished");
            Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            Assert.AreEqual(1, Notebook.Entries.Count);
            var e = Notebook.Last;
            Assert.AreEqual("measure", e.Tool);
            Assert.AreEqual("m", e.Unit);
            Assert.AreEqual(1.5, e.ValueSI, 0.001);
            StringAssert.StartsWith("Distance 1.50 m", e.Label);
            Assert.AreEqual(0, Tool.Session.Count, "session resets after finishing");
        }

        [Test]
        public void ClickingTheLastPointAgainFinishes()
        {
            Click(LeftJamb);
            Click(RightJamb);
            Click(RightJamb + new Vector3(0, 0.01f, 0));
            Assert.AreEqual(1, Notebook.Entries.Count);
            Assert.AreEqual(1, Tool.Shapes.Count);
            Assert.AreEqual(2, Tool.Shapes[0].Points.Length);
        }

        [Test]
        public void DuplicateFirstClickIsIgnored()
        {
            Click(LeftJamb);
            Click(LeftJamb);
            Assert.AreEqual(1, Tool.Session.Count);
        }

        [Test]
        public void FinishWithOnePointDoesNothing()
        {
            Click(LeftJamb);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            Assert.AreEqual(0, Notebook.Entries.Count);
            Assert.AreEqual(1, Tool.Session.Count);
        }

        [Test]
        public void ThreePointsMakeATriangleWithAnglesAndArea()
        {
            var eye = new Vector3(0, 4.1f, 2.5f);
            Click(eye, new Vector3(-0.74f, 4.69f, -0.1f));
            Click(eye, new Vector3(0.74f, 4.69f, -0.1f));
            Click(eye, new Vector3(0.74f, 3.51f, -0.1f));
            Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            var e = Notebook.Last;
            Assert.AreEqual("m²", e.Unit);
            Assert.AreEqual(0.9, e.ValueSI, 0.01);
            Assert.AreEqual(3, e.Angles.Length);
            Assert.AreEqual(90, e.Angles[1], 1.0);
            Assert.AreEqual(180, e.Angles.Sum(), 0.01);
            StringAssert.StartsWith("Triangle", e.Label);
        }

        [Test]
        public void FourPointsStayOpenUntilFinishedAndLabelEverything()
        {
            var eye = new Vector3(0, 4.1f, 2.5f);
            Click(eye, new Vector3(-0.74f, 4.69f, -0.1f));
            Click(eye, new Vector3(0.74f, 4.69f, -0.1f));
            Click(eye, new Vector3(0.74f, 3.51f, -0.1f));
            Click(eye, new Vector3(-0.74f, 3.51f, -0.1f));
            Assert.AreEqual(0, Tool.Shapes.Count, "no 4-point cap: keeps going until finished");
            Assert.AreEqual(4, Tool.Session.Count);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            var view = Tool.Shapes[0].View;
            Assert.AreEqual(9, view.ActiveLabelCount, "4 sides + 4 angles + area");
            var texts = view.Labels.Take(view.ActiveLabelCount).Select(l => l.Text).ToList();
            Assert.That(texts.Count(t => t == "90.0°"), Is.EqualTo(4));
            Assert.That(texts.Count(t => t.StartsWith("1.50 m")), Is.EqualTo(2));
            Assert.That(texts.Count(t => t.StartsWith("1.20 m")), Is.EqualTo(2));
            Assert.That(texts.Any(t => t.StartsWith("1.80 m²")));
        }

        [Test]
        public void OtherHandPinchFinishesTheShape()
        {
            var eye = new Vector3(0, 4.1f, 2.5f);
            Click(eye, LeftJamb); Click(eye, RightJamb);
            // Left hand pinches (anywhere): finishes the right hand's distance, adds no point.
            var p = From(eye, new Vector3(0f, 0f, 3f));
            Hub.RaisePressStart(ToolHand.Left, p);
            Hub.RaisePressEnd(ToolHand.Left, p);
            Assert.AreEqual(1, Tool.Shapes.Count);
            Assert.AreEqual(0, Tool.Session.Count);
            Assert.AreEqual(2, Tool.Shapes[0].Points.Length);
            StringAssert.EndsWith("(other hand)", Tool.LastAction);
            Assert.AreEqual(1.5, Notebook.Last.ValueSI, 0.01);
        }

        [Test]
        public void OtherHandWithOnePointPlacesInstead()
        {
            var eye = new Vector3(0, 4.1f, 2.5f);
            Click(eye, LeftJamb);
            var p = From(eye, RightJamb);
            Hub.RaisePressStart(ToolHand.Left, p);
            Hub.RaisePressEnd(ToolHand.Left, p);
            Assert.AreEqual(2, Tool.Session.Count, "nothing to finish yet: it's a second point");
        }

        [Test]
        public void ClickingTheFirstPointClosesThePolygon()
        {
            var eye = new Vector3(0, 4.1f, 2.5f);
            var a = new Vector3(-0.74f, 4.69f, -0.1f);
            Click(eye, a);
            Click(eye, new Vector3(0.74f, 4.69f, -0.1f));
            Click(eye, new Vector3(0.74f, 3.51f, -0.1f));
            Click(eye, new Vector3(-0.74f, 3.51f, -0.1f));
            Click(eye, new Vector3(0f, 3.3f, 0f));   // 5th point on the wall below the sill
            Click(eye, a);                          // back to the start
            Assert.AreEqual(1, Tool.Shapes.Count);
            StringAssert.EndsWith("(closed)", Tool.LastAction);
            Assert.AreEqual(5, Tool.Shapes[0].Points.Length);
            StringAssert.StartsWith("Polygon (5 sides)", Notebook.Last.Label);
            Assert.IsTrue(Tool.Shapes[0].Measurement.Reordered, "the 5th point makes the clicks cross, but all five are hull corners");
        }

        /// Concave shapes: an L on the open wall (1 × 1 m minus a 0.5 m square), measured as clicked, not as its hull.
        static readonly Vector3[] WallL =
        {
            new Vector3(0.5f, 1.0f, 0f), new Vector3(1.5f, 1.0f, 0f), new Vector3(1.5f, 1.5f, 0f),
            new Vector3(1.0f, 1.5f, 0f), new Vector3(1.0f, 2.0f, 0f), new Vector3(0.5f, 2.0f, 0f),
        };

        [Test]
        public void LShapeOnTheWallSavesItsConcaveArea()
        {
            foreach (var p in WallL) Click(p);
            Click(WallL[0]);   // close on the first point
            Assert.AreEqual(1, Tool.Shapes.Count);
            StringAssert.EndsWith("(closed)", Tool.LastAction);
            var e = Notebook.Last;
            Assert.AreEqual("m²", e.Unit);
            Assert.AreEqual(0.75, e.ValueSI, 0.005, "1 − 0.25 m², not the hull's 0.875");
            StringAssert.StartsWith("Polygon (6 sides)", e.Label);
            Assert.AreEqual(6, e.Points.Length);
            Assert.AreEqual(6, e.Angles.Length);
            Assert.AreEqual(270, e.Angles[3], 1.0, "the inside corner");
            Assert.AreEqual(720, e.Angles.Sum(), 0.01);
            Assert.AreEqual(4.0, e.Sides.Sum(), 0.005, "perimeter");
            var view = Tool.Shapes[0].View;
            Assert.AreEqual(13, view.ActiveLabelCount, "6 sides + 6 angles + area");
            var texts = view.Labels.Take(view.ActiveLabelCount).Select(l => l.Text).ToList();
            Assert.That(texts.Count(t => t == "270.0°"), Is.EqualTo(1));
            Assert.That(texts.Count(t => t == "90.0°"), Is.EqualTo(5));
            Assert.That(texts.Any(t => t.StartsWith("0.75 m²")));
        }

        [Test]
        public void CrossedOutlineIsRefusedAndUndoFixesIt()
        {
            // A 1 m square's corners in bow-tie order, then a point inside it: the outline crosses, so no area to save.
            var a = new Vector3(0.5f, 1.0f, 0f); var b = new Vector3(1.5f, 1.0f, 0f);
            var c = new Vector3(1.5f, 2.0f, 0f); var d = new Vector3(0.5f, 2.0f, 0f);
            Click(a); Click(c); Click(b); Click(d); Click(new Vector3(1.0f, 1.2f, 0f));
            Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            Assert.AreEqual(0, Tool.Shapes.Count, "not saved");
            Assert.AreEqual(0, Notebook.Entries.Count);
            Assert.AreEqual(5, Tool.Session.Count, "the points stay for Undo");
            Assert.AreEqual(MeasureTool.CrossRefused, Tool.LastAction);
            Click(a);   // closing on the first point is refused the same way
            Assert.AreEqual(0, Tool.Shapes.Count);
            Assert.AreEqual(5, Tool.Session.Count);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Undo);   // the inside point off: four hull corners, reordered
            Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            Assert.AreEqual(1, Tool.Shapes.Count);
            Assert.AreEqual(1.0, Notebook.Last.ValueSI, 0.005);
            Assert.IsTrue(Tool.Shapes[0].Measurement.Reordered);
        }

        [Test]
        public void DraggingAPointAcrossTheOutlineIsPutBack()
        {
            foreach (var p in WallL) Click(p);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            var entry = Notebook.Last;
            double area = entry.ValueSI;
            var eye = MeasureScenarios.SpawnEye;
            // Drag the top-inner corner (1, 2) down to (1.4, 1.2): its side to (0.5, 2) would cross the inner step
            // (1.5, 1.5)–(1, 1.5), and with that point inside the hull the hull-order rescue can't apply, so the move is put back.
            // (Targets below the bottom side hit the facade's 1 m ledge instead; a point dragged outside every other one would
            // make all six points hull corners, which reorders into a valid hexagon rather than refusing.)
            var end = From(eye, new Vector3(1.4f, 1.2f, 0f));
            Hub.RaisePressStart(ToolHand.Right, From(eye, WallL[4]));
            Hub.RaisePressMove(ToolHand.Right, From(eye, new Vector3(1.2f, 1.6f, 0f)));
            Hub.RaisePressMove(ToolHand.Right, end);
            Hub.RaisePressEnd(ToolHand.Right, end);
            StringAssert.StartsWith("move refused: edges cross", Tool.LastAction);
            Assert.Less(Vector3.Distance(WallL[4], Tool.Shapes[0].Points[4]), 0.002f, "the point is back");
            Assert.AreEqual(area, entry.ValueSI, 1e-9, "the notebook keeps the good reading");
            Assert.IsFalse(Tool.Shapes[0].Measurement.SelfIntersecting);
            Assert.AreEqual(0, Tool.Session.Count, "no stray point added by the drag");
        }

        [Test]
        public void UndoRemovesPointsThenTheLastShape()
        {
            Click(LeftJamb);
            Click(RightJamb);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            Click(LeftJamb);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Undo);
            Assert.AreEqual(0, Tool.Session.Count, "first undo removes the in-progress point");
            Assert.AreEqual(1, Notebook.Entries.Count);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Undo);
            Assert.AreEqual(0, Tool.Shapes.Count, "second undo removes the finished shape");
            Assert.AreEqual(0, Notebook.Entries.Count, "and its notebook entry");
        }

        [Test]
        public void RedoPutsBackWhatUndoTook_AndANewPointForgetsIt()
        {
            Click(LeftJamb); Click(RightJamb);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            int id = Notebook.Last.Id;
            Hub.RaiseButton(ToolHand.Right, ToolButton.Undo);
            Assert.AreEqual(0, Tool.Shapes.Count);
            Assert.IsTrue(Tool.CanRedo);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Redo);
            Assert.AreEqual(1, Tool.Shapes.Count, "the shape is back");
            Assert.AreEqual(id, Notebook.Last.Id, "with its notebook entry and number");
            Assert.IsTrue(Tool.Shapes[0].View.gameObject.activeSelf);

            Click(LeftJamb);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Undo);
            Assert.AreEqual(0, Tool.Session.Count);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Redo);
            Assert.AreEqual(1, Tool.Session.Count, "the point is back");
            Hub.RaiseButton(ToolHand.Right, ToolButton.Undo);
            Click(RightJamb);
            Assert.IsFalse(Tool.CanRedo, "a new point forgets the undone one");
        }

        [Test]
        public void ClearRemovesEverything()
        {
            Click(LeftJamb); Click(RightJamb); Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            Click(LeftJamb);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Clear);
            Assert.AreEqual(0, Tool.Shapes.Count);
            Assert.AreEqual(0, Tool.Session.Count);
            Assert.AreEqual(0, Notebook.Entries.Count);
        }

        [Test]
        public void DraggingAPointUpdatesTheShapeAndItsEntry()
        {
            Click(LeftJamb); Click(RightJamb); Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            var entry = Notebook.Last;
            var eye = MeasureScenarios.SpawnEye;
            // Grab the right jamb point and drag it onto the door's left edge area on the wall face.
            var start = From(eye, RightJamb);
            var end = From(eye, new Vector3(2.0f, 4.1f, 0f));
            Hub.RaisePressStart(ToolHand.Right, start);
            Hub.RaisePressMove(ToolHand.Right, From(eye, new Vector3(1.2f, 4.1f, 0f)));
            Hub.RaisePressMove(ToolHand.Right, end);
            Hub.RaisePressEnd(ToolHand.Right, end);
            Assert.AreEqual(1, Notebook.Entries.Count, "drag edits, it doesn't add");
            Assert.AreSame(entry, Notebook.Last);
            Assert.AreEqual(Vector3.Distance(Tool.Shapes[0].Points[0], new Vector3(2.0f, 4.1f, 0f)), entry.ValueSI, 0.002);
            Assert.AreEqual(2, Tool.Session.Count + Tool.Shapes[0].Points.Length, "no stray point added by the drag");
        }

        [Test]
        public void SmallPressJitterIsAClickNotADrag()
        {
            Click(LeftJamb); Click(RightJamb); Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            var eye = MeasureScenarios.SpawnEye;
            var p = From(eye, RightJamb);
            Hub.RaisePressStart(ToolHand.Right, p);
            Hub.RaisePressMove(ToolHand.Right, From(eye, RightJamb + new Vector3(0, 0.005f, 0)));
            Hub.RaisePressEnd(ToolHand.Right, p);
            Assert.AreEqual(1, Tool.Session.Count, "a new measurement starts at the shared point");
            Assert.AreEqual(1.5, Notebook.Last.ValueSI, 0.002, "the finished shape is untouched");
        }

        [Test]
        public void LivePreviewShowsTheDistanceBeforeTheSecondClick()
        {
            Click(LeftJamb);
            Hub.SetPointerOverride(ToolHand.Right, From(MeasureScenarios.SpawnEye, RightJamb));
            Tool.Tick();
            Assert.IsTrue(Tool.Cursor.HasValue);
            Assert.IsTrue(Tool.Session.TryMeasureLive(out var live));
            Assert.AreEqual(1.5, live.Distance, 0.005);
            Hub.SetPointerOverride(ToolHand.Right, null);
        }

        [Test]
        public void UnequippedToolIgnoresInput()
        {
            Tool.Equip(false);
            Click(LeftJamb); Click(RightJamb);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            Assert.AreEqual(0, Tool.Session.Count);
            Assert.AreEqual(0, Notebook.Entries.Count);
        }

        [Test]
        public void MissingTheSceneDoesNotPlaceAPoint()
        {
            var p = From(MeasureScenarios.SpawnEye, MeasureScenarios.SpawnEye + Vector3.forward * 5f);
            Hub.RaisePressStart(ToolHand.Right, p);
            Hub.RaisePressEnd(ToolHand.Right, p);
            Assert.AreEqual(0, Tool.Session.Count);
            Assert.AreEqual("miss", Tool.LastAction);
        }
    }

    public class ToolPlumbingTests
    {
        [Test]
        public void ToolNamesParse()
        {
            Assert.IsTrue(ToolManager.TryParse("Tape", out var k)); Assert.AreEqual(ToolKind.Measure, k);
            Assert.IsTrue(ToolManager.TryParse("protractor", out k)); Assert.AreEqual(ToolKind.Measure, k);
            Assert.IsTrue(ToolManager.TryParse("level", out k)); Assert.AreEqual(ToolKind.Level, k);
            Assert.IsTrue(ToolManager.TryParse("none", out k)); Assert.AreEqual(ToolKind.None, k);
            Assert.IsFalse(ToolManager.TryParse("hammer", out _));
        }

        [Test]
        public void AppCommandsEquipToolReachesTheMeasureTool()
        {
            var go = new GameObject("tools");
            try
            {
                var manager = go.AddComponent<ToolManager>();
                manager.measure = go.AddComponent<MeasureTool>();
                Services.Register(manager);
                Assert.IsTrue(AppCommands.EquipTool("measure"));
                Assert.IsTrue(manager.measure.Equipped);
                Assert.IsTrue(AppCommands.EquipTool("none"));
                Assert.IsFalse(manager.measure.Equipped);
                Assert.IsFalse(AppCommands.EquipTool("hammer"));
            }
            finally
            {
                Services.Clear();
                var m = go.GetComponent<MeasureTool>();
                if (m != null && m.viewRoot != null) Object.DestroyImmediate(m.viewRoot.gameObject);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void InputHubPressLifecycle()
        {
            var go = new GameObject("hub");
            try
            {
                var hub = go.AddComponent<ToolInputHub>();
                var log = new List<string>();
                hub.PressStart += (h, p) => log.Add("start");
                hub.PressMove += (h, p) => log.Add("move");
                hub.PressEnd += (h, p) => log.Add("end");
                hub.RaisePressMove(ToolHand.Left, default);            // ignored: not pressed
                hub.RaisePressStart(ToolHand.Left, default);
                hub.RaisePressMove(ToolHand.Left, default);
                hub.RaisePressStart(ToolHand.Left, default);           // re-press ends the previous one first
                hub.RaisePressEnd(ToolHand.Left, default);
                hub.RaisePressEnd(ToolHand.Left, default);             // ignored: already released
                CollectionAssert.AreEqual(new[] { "start", "move", "end", "start", "end" }, log);
                Assert.AreEqual(ToolHand.Left, hub.LastActiveHand);
                Assert.IsFalse(hub.HasPointer(ToolHand.Right));
                hub.SetPointerOverride(ToolHand.Right, new Pose(Vector3.one, Quaternion.identity));
                Assert.IsTrue(hub.HasPointer(ToolHand.Right));
                Assert.AreEqual(Vector3.one, hub.GetPointer(ToolHand.Right).position);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void EvidenceCameraFrustumContainsTheReading()
        {
            var cams = SyntheticFacadeBuilder.CreateCameras();
            var window = new[] { new Vector3(-0.75f, 4.1f, -0.1f), new Vector3(0.75f, 4.1f, -0.1f) };
            int id = CameraEvidence.Nearest(window, cams);
            Assert.GreaterOrEqual(id, 0);
            Assert.IsTrue(CameraEvidence.ContainsAll(cams[id], window));
        }

        [Test]
        public void SessionLivePoints()
        {
            var s = new MeasureSession();
            s.Preview = Vector3.one;
            Assert.AreEqual(0, s.LivePoints().Count, "preview alone is not a measurement");
            s.Add(Vector3.zero, SnapKind.Face);
            Assert.AreEqual(2, s.LivePoints().Count);
            s.Add(Vector3.right, SnapKind.Face); s.Add(Vector3.up, SnapKind.Face); s.Add(Vector3.forward, SnapKind.Face);
            Assert.IsFalse(s.IsFull, "no 4-point cap");
            Assert.AreEqual(5, s.LivePoints().Count, "preview follows the 4 placed points");
            Assert.IsTrue(s.Add(Vector3.one, SnapKind.Face));
        }
    }
}
