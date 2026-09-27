using System.Collections.Generic;
using System.Linq;
using System.Text;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Tests
{
    /// UX W1.2 through the real tools on the real facade (PhysX: these run in the Editor, not offline). A pinch is fed
    /// through the hub's live path the way OvrToolInputSource feeds it, with an injected 2° dip.
    static class LivePinch
    {
        /// 90 Hz frames into the right hand's live history: the ray aims from origin at target and pitches down dipDeg
        /// over the 80 ms before the press while the pinch strength rises to the press threshold. Returns the press pose.
        public static Pose Feed(ToolInputHub hub, Vector3 origin, Vector3 target, float dipDeg = 2f)
        {
            var look = Quaternion.LookRotation(target - origin);
            var h = hub.History(ToolHand.Right);
            h.Clear();
            Pose pose = default;
            for (int k = 39; k >= 0; k--)
            {
                float before = k / 90f, f = Mathf.Clamp01((0.08f - before) / 0.08f);
                pose = new Pose(origin, look * Quaternion.AngleAxis(dipDeg * f, Vector3.right));
                hub.SetLivePointer(ToolHand.Right, pose, true);
                hub.RecordLive(ToolHand.Right, 100f - before, pose, true, 0.55f * f);
            }
            return pose;
        }

        /// Feed + a live press (not released).
        public static Pose Press(ToolInputHub hub, Vector3 origin, Vector3 target, float dipDeg = 2f)
        {
            var pose = Feed(hub, origin, target, dipDeg);
            hub.RaiseLivePressStart(ToolHand.Right, pose);
            return pose;
        }

        /// Press + release at the dipped pose.
        public static Pose Click(ToolInputHub hub, Vector3 origin, Vector3 target, float dipDeg = 2f)
        {
            var pose = Press(hub, origin, target, dipDeg);
            hub.RaisePressEnd(ToolHand.Right, pose);
            return pose;
        }

        public static void Flags(bool rewind, bool angular)
        {
            PointerHistory.Rewind = rewind;
            AngularRadii.Enabled = angular;
        }

        public static float DipError(float distance, float dipDeg = 2f) => distance * Mathf.Tan(dipDeg * Mathf.Deg2Rad);
    }

    public class CommitWhatYouSawMeasureTests : FacadeToolFixture
    {
        /// D4 (SPEC §9): Line mode saves a tape at its 2nd point, so shapes and explicit finishes happen in Area mode.
        [SetUp] public void ShapesUseAreaMode() => Tool.AreaMode = true;

        const float Mm = 0.001f;
        /// Open brick, nothing to snap to within 30 cm.
        static readonly Vector3 Wall = new Vector3(-2.5f, 3.0f, 0f);

        [TearDown]
        public void RestoreFlags() => LivePinch.Flags(true, true);

        [TestCase(1f)]
        [TestCase(2f)]
        public void DippedPinchPlacesThePointYouSaw(float distance)
        {
            LivePinch.Click(Hub, Wall + Vector3.forward * distance, Wall);
            Assert.AreEqual(1, Tool.Session.Count, Tool.LastAction);
            Assert.Less(Vector3.Distance(Tool.Session.Points[0], Wall), 1 * Mm);
        }

        [TestCase(1f)]
        [TestCase(2f)]
        public void RewindOffPlacesTheDippedPointAsBefore(float distance)
        {
            LivePinch.Flags(false, true);
            LivePinch.Click(Hub, Wall + Vector3.forward * distance, Wall);
            Assert.AreEqual(LivePinch.DipError(distance), Vector3.Distance(Tool.Session.Points[0], Wall), 1 * Mm);
        }

        [Test]
        public void TheCursorHoldsWhatYouSawDuringTheClick()
        {
            var pose = LivePinch.Press(Hub, Wall + Vector3.forward * 2f, Wall);
            Tool.Tick();
            Assert.IsTrue(Tool.Cursor.HasValue);
            Assert.Less(Vector3.Distance(Tool.Cursor.Value.point, Wall), 1 * Mm, "the cursor stays on the committed hit");
            Hub.RaisePressEnd(ToolHand.Right, pose);
            Assert.AreEqual(1, Tool.Session.Count);
            Tool.Tick();
            Assert.AreEqual(LivePinch.DipError(2f), Vector3.Distance(Tool.Cursor.Value.point, Wall), 1 * Mm, "then follows the ray again");
        }

        [Test]
        public void InjectedPressesNeverRewind()
        {
            // The live history holds a dip, but the press comes from a test / the harness / the agent.
            var dipped = LivePinch.Feed(Hub, Wall + Vector3.forward * 2f, Wall);
            Hub.RaisePressStart(ToolHand.Right, dipped);
            Hub.RaisePressEnd(ToolHand.Right, dipped);
            Assert.AreEqual(LivePinch.DipError(2f), Vector3.Distance(Tool.Session.Points[0], Wall), 1 * Mm);
        }

        [Test]
        public void AnAimOverrideNeverRewinds()
        {
            var dipped = LivePinch.Feed(Hub, Wall + Vector3.forward * 2f, Wall);
            Hub.SetPointerOverride(ToolHand.Right, dipped);
            Hub.RaiseLivePressStart(ToolHand.Right, dipped);
            Hub.RaisePressEnd(ToolHand.Right, dipped);
            Hub.SetPointerOverride(ToolHand.Right, null);
            Assert.AreEqual(LivePinch.DipError(2f), Vector3.Distance(Tool.Session.Points[0], Wall), 1 * Mm);
        }

        [Test]
        public void DippedPinchOnAPlacedPointIsAClickNotADrag()
        {
            // Finishing a tape by pinching its last point again: the dip after the rewound hit must not start a drag.
            var eye = Wall + Vector3.forward * 2f;
            Click(eye, Wall + Vector3.up * 0.5f);
            Click(eye, Wall);
            var pose = LivePinch.Press(Hub, eye, Wall);
            Hub.RaisePressMove(ToolHand.Right, pose);   // the live source moves every frame; the ray is 7 cm below the rewound hit
            Hub.RaisePressEnd(ToolHand.Right, pose);
            Assert.AreEqual(1, Tool.Shapes.Count, Tool.LastAction);
            Assert.AreEqual(0.5, Tool.Shapes[0].Measurement.Distance, 0.001);
        }

        [Test]
        public void FinishRadiusIsAngularFarAway()
        {
            // From 5 m a click 6 cm from the last point is "the last point again" (1° = 8.9 cm), not a third point.
            var eye = new Vector3(-2.5f, 3.0f, 5f);
            Click(eye, Wall); Click(eye, Wall + Vector3.down);
            Click(eye, Wall + new Vector3(0f, -0.94f, 0f));
            Assert.AreEqual(1, Tool.Shapes.Count, Tool.LastAction);
            Assert.AreEqual(2, Tool.Shapes[0].Points.Length);
        }

        [Test]
        public void FinishRadiusIsMetricWithTheAngularRadiiOff()
        {
            LivePinch.Flags(true, false);
            var eye = new Vector3(-2.5f, 3.0f, 5f);
            Click(eye, Wall); Click(eye, Wall + Vector3.down);
            Click(eye, Wall + new Vector3(0f, -0.94f, 0f));
            Assert.AreEqual(0, Tool.Shapes.Count);
            Assert.AreEqual(3, Tool.Session.Count, "3 cm: a new point, as before");
        }

        [Test]
        public void GrabRadiusIsAngularFarAway()
        {
            var eye = new Vector3(-2.5f, 3.0f, 5f);
            Click(eye, Wall); Click(eye, Wall + Vector3.down); Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            // Press 8 cm off the first point (1.2° at 5 m = 10.5 cm) and drag it 30 cm to the right.
            var start = From(eye, Wall + new Vector3(0.08f, 0f, 0f));
            var end = From(eye, Wall + new Vector3(0.3f, 0f, 0f));
            Hub.RaisePressStart(ToolHand.Right, start);
            Hub.RaisePressMove(ToolHand.Right, end);
            Hub.RaisePressEnd(ToolHand.Right, end);
            Assert.AreEqual(0, Tool.Session.Count, "a drag, not a new point");
            Assert.Less(Vector3.Distance(Tool.Shapes[0].Points[0], Wall + new Vector3(0.3f, 0f, 0f)), 1 * Mm);
        }
    }

    public class CommitWhatYouSawLevelTests : LevelFixture
    {
        const float Mm = 0.001f;
        static readonly Vector3 Wall = new Vector3(-2.5f, 3.0f, 0f);

        [TearDown]
        public void RestoreFlags() => LivePinch.Flags(true, true);

        [TestCase(true)]
        [TestCase(false)]
        public void LevelPlacesTheReadingYouSaw(bool rewind)
        {
            LivePinch.Flags(rewind, true);
            LivePinch.Click(Hub, Wall + Vector3.forward * 2f, Wall);
            Assert.AreEqual(1, Level.Placements.Count, Level.LastAction);
            var r = Level.Placements[0].Reading;
            Assert.AreEqual(LevelMode.Plumb, r.Mode);
            float error = Vector3.Distance(r.Point, Wall);
            if (rewind) Assert.Less(error, 1 * Mm, "the reading from before the pinch");
            else Assert.AreEqual(LivePinch.DipError(2f), error, 1 * Mm, "flag off: the release reading, as before");
        }

        [Test]
        public void TheGhostHoldsDuringThePinch()
        {
            var pose = LivePinch.Press(Hub, Wall + Vector3.forward * 2f, Wall);
            Level.Tick();
            Assert.IsTrue(Level.Live.HasValue);
            Assert.Less(Vector3.Distance(Level.Live.Value.Point, Wall), 1 * Mm);
            Hub.RaisePressEnd(ToolHand.Right, pose);
            Assert.AreEqual(1, Level.Placements.Count);
        }

        [Test]
        public void MovedMoreThanThreeDegreesReadsAtTheRelease()
        {
            var eye = Wall + Vector3.forward * 2f;
            LivePinch.Press(Hub, eye, Wall);
            var there = Wall + new Vector3(0.5f, 0f, 0f);   // 14° away: a deliberate move
            Hub.RaisePressEnd(ToolHand.Right, From(eye, there));
            Assert.Less(Vector3.Distance(Level.Placements[0].Reading.Point, there), 1 * Mm);
        }
    }

    public class CommitWhatYouSawPartTests : FacadeToolFixture
    {
        const float Mm = 0.001f;
        static readonly Vector3 FasciaPoint = new Vector3(0.5f, 6.15f, S.FasciaProud);
        static readonly Vector3 Ladder = new Vector3(0.5f, 7.0f, 1.5f);
        static PartCatalog s_Catalog;
        GameObject m_Parts;
        PartTool m_Tool;
        PartLoader m_Loader;

        [OneTimeSetUp]
        public void LoadCatalog()
        {
            s_Catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath);
            if (s_Catalog == null || s_Catalog.entries.Count < 2) s_Catalog = PartCatalogBuilder.Build();
            PartCatalogBuilder.EnsurePartsLayer();
        }

        [SetUp]
        public void CreatePartTool()
        {
            Tool.Equip(false);
            m_Parts = new GameObject("w12-parts-test");
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
            LivePinch.Flags(true, true);
            m_Tool.ClearAll();
            Services.Unregister(m_Tool);
            foreach (var p in Object.FindObjectsByType<PartInstance>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Object.DestroyImmediate(p.gameObject);
            Object.DestroyImmediate(m_Parts);
            Physics.SyncTransforms();
        }

        PartInstance Take()
        {
            var p = m_Loader.LoadFromCatalog(PartScenarios.Hanger);
            Assert.IsNotNull(p);
            m_Tool.Hold(p);
            return p;
        }

        /// Where a plain release at the undipped pointer seats the hanger (today's path).
        Vector3 Reference()
        {
            var p = Take();
            Assert.IsTrue(m_Tool.Release(From(Ladder, FasciaPoint)), m_Tool.LastAction);
            var at = p.transform.position;
            m_Tool.ClearAll();
            Physics.SyncTransforms();
            return at;
        }

        [Test]
        public void DippedPinchSeatsThePartWhereItPreviewed()
        {
            var want = Reference();
            var p = Take();
            LivePinch.Click(Hub, Ladder, FasciaPoint);
            Assert.IsTrue(p.Placed, m_Tool.LastAction);
            Assert.Less(Vector3.Distance(p.transform.position, want), 1 * Mm);
        }

        [Test]
        public void RewindOffSeatsItWhereTheDipWent()
        {
            var want = Reference();
            LivePinch.Flags(false, true);
            var p = Take();
            LivePinch.Click(Hub, Ladder, FasciaPoint);
            Assert.IsTrue(p.Placed, m_Tool.LastAction);
            Assert.Greater(Vector3.Distance(p.transform.position, want), 10 * Mm, "as before: the dip moved it");
        }

        [Test]
        public void ThePreviewHoldsItsSeatDuringThePinch()
        {
            var want = Reference();
            var p = Take();
            var pose = LivePinch.Press(Hub, Ladder, FasciaPoint);
            m_Tool.Tick();
            Assert.Less(Vector3.Distance(p.transform.position, want), 1 * Mm, "the part doesn't follow the dip");
            Hub.RaisePressEnd(ToolHand.Right, pose);
            Assert.IsTrue(p.Placed);
        }

        [Test]
        public void MovedMoreThanThreeDegreesPlacesAtTheRelease()
        {
            var p = Take();
            LivePinch.Press(Hub, Ladder, FasciaPoint);
            Hub.RaisePressEnd(ToolHand.Right, From(Ladder, FasciaPoint + new Vector3(-1f, 0f, 0f)));   // a drag
            Assert.IsTrue(p.Placed, m_Tool.LastAction);
            Assert.AreEqual(-0.5f, p.transform.position.x, 0.01f);
        }
    }

    public class AngularSnapTests : FacadeToolFixture
    {
        const float Mm = 0.001f;
        static readonly Vector3 OpeningCorner = new Vector3(-0.75f, 4.7f, 0f);
        /// On the wall face 5.5 cm left of and above the opening's top-left corner (7.8 cm from it, 5.5 cm from its edges).
        static readonly Vector3 NearCorner = new Vector3(-0.805f, 4.755f, 0f);

        static SurfaceHit Snap(Vector3 eye, Vector3 target)
        {
            Assert.IsTrue(SnapService.TryRaySnap(new Ray(eye, (target - eye).normalized), out var hit), "ray missed");
            return hit;
        }

        [Test]
        public void FarCornerSnapsWithinOneDegree()
        {
            bool was = AngularRadii.Enabled;
            try
            {
                var eye = MeasureScenarios.SpawnEye;   // 5.1 m away: 1° = 8.9 cm
                AngularRadii.Enabled = true;
                var hit = Snap(eye, NearCorner);
                Assert.AreEqual(SnapKind.Corner, hit.kind);
                Assert.Less(Vector3.Distance(hit.point, OpeningCorner), 1 * Mm);
                AngularRadii.Enabled = false;
                Assert.AreEqual(SnapKind.Face, Snap(eye, NearCorner).kind, "6 cm metric radius: no snap, as before");
            }
            finally { AngularRadii.Enabled = was; }
        }

        [Test]
        public void UpCloseTheMetricRadiusStillRules()
        {
            var eye = new Vector3(-0.8f, 4.75f, 1f);   // 1 m: 1° = 1.7 cm
            Assert.AreEqual(SnapKind.Corner, Snap(eye, OpeningCorner + new Vector3(-0.03f, 0.03f, 0f)).kind);
            Assert.AreEqual(SnapKind.Face, Snap(eye, NearCorner).kind);
        }

        [Test]
        public void PointQueriesStayMetric()
        {
            // TrySnap (the part's "surface near my hand") has no ray: nothing changes.
            Assert.IsTrue(SnapService.TrySnap(NearCorner + Vector3.forward * 0.01f, out var hit));
            Assert.AreEqual(SnapKind.Face, hit.kind);
        }
    }

    /// UX W1.2: the M2 ground truth at arm's length (each point from 0.6–2 m) with ±1° aim noise.
    public class MeasureScenarioNearTests : FacadeToolFixture
    {
        public static IEnumerable<TestCaseData> Cases()
        {
            foreach (var s in MeasureScenarios.M2Near())
                for (int seed = 1; seed <= 25; seed++)
                    yield return new TestCaseData(s.Id, seed).SetName($"{s.Id}.seed{seed:00}");
        }

        [TestCaseSource(nameof(Cases))]
        public void GroundTruth(string id, int seed)
        {
            var s = MeasureScenarios.M2Near().Single(x => x.Id == id);
            var r = MeasureScenarios.Run(s, Tool, Hub, null, seed);
            Assert.IsTrue(r.Passed, r.ToString());
            Assert.AreEqual(1, AirTools.Notes.Notebook.Entries.Count, "one notebook entry per measurement");
        }
    }

    /// The stress run: 300 seeds × 5 scenarios = 1500, plain M2 and the ±1° near variant. Explicit (≈ 3000 tool runs):
    /// run it by name in the gate.
    [Explicit("stress: 2 × 1500 runs")]
    public class MeasureScenarioStressTests : FacadeToolFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void ThreeHundredSeedsEach(bool near)
        {
            int pass = 0, total = 0;
            var fails = new StringBuilder();
            foreach (var s in near ? MeasureScenarios.M2Near() : MeasureScenarios.M2())
                for (int seed = 1; seed <= 300; seed++)
                {
                    var r = MeasureScenarios.Run(s, Tool, Hub, null, seed, removeAfter: true);
                    total++;
                    if (r.Passed) pass++;
                    else if (fails.Length < 8000) fails.AppendLine(r.ToString());
                }
            Debug.Log($"[AirTools.Check] W12.stress.{(near ? "near" : "m2")} passed={pass} total={total} {(pass == total ? "PASS" : "FAIL")}");
            Assert.AreEqual(total, pass, $"{pass}/{total}\n{fails}");
        }
    }
}
