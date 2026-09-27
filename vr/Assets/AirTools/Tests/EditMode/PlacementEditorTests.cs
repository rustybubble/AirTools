using System.Collections.Generic;
using System.Linq;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Tools;
using AirTools.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AirTools.Tests
{
    /// The placement editor with a real PartTool, catalog parts and the ToolInputHub, on the built-in facade (Editor gate:
    /// transforms, colliders and raycasts). The maths is PlacementMathTests (offline too). Parts float in front of the
    /// facade at a pose (PartTool.PlaceAt), so their frame is the view frame (up + the viewer's facing).
    public class PlacementEditorTests : FacadeToolFixture
    {
        static PartCatalog s_Catalog;
        static readonly Vector3 Spot = new Vector3(0.8f, 1.2f, 2.5f);
        GameObject m_Go;
        PartTool m_Parts;
        PartLoader m_Loader;
        PlacementEditor m_Editor;
        ToolManager m_Tools;

        [OneTimeSetUp]
        public void LoadCatalog()
        {
            s_Catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath);
            if (s_Catalog == null || s_Catalog.entries.Count < 2) s_Catalog = PartCatalogBuilder.Build();
            PartCatalogBuilder.EnsurePartsLayer();
        }

        [SetUp]
        public void CreateEditor()
        {
            UiSettings.UseUnits(UnitSystem.Imperial);
            AppState.Set(AppMode.World);
            m_Go = new GameObject("placement-test");
            m_Loader = m_Go.AddComponent<PartLoader>();
            m_Loader.catalog = s_Catalog;
            m_Parts = m_Go.AddComponent<PartTool>();
            m_Parts.SetInput(Hub);
            Services.Register(m_Parts);
            EditHistory.Register(m_Parts);   // EditMode: OnEnable doesn't run for AddComponent
            m_Tools = m_Go.AddComponent<ToolManager>();
            m_Tools.measure = Tool;
            m_Tools.parts = m_Parts;
            Services.Register(m_Tools);
            m_Tools.Equip(ToolKind.Measure);
            m_Editor = m_Go.AddComponent<PlacementEditor>();
            m_Editor.tool = m_Parts;
            m_Editor.loader = m_Loader;
            m_Editor.SyncCatalogLoads = true;
            m_Editor.CandidatesOverride = new List<PartSummary> { new PartSummary { id = "hidden-hanger-5k" }, new PartSummary { id = "window-ac-small" } };
            m_Editor.Attach();
            m_Editor.SetInput(Hub);
        }

        [TearDown]
        public void DestroyEditor()
        {
            m_Editor.Detach();
            m_Parts.ClearAll();
            Services.Unregister(m_Parts);
            Services.Unregister(m_Tools);
            EditHistory.Unregister(m_Parts);
            foreach (var p in Object.FindObjectsByType<PartInstance>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Object.DestroyImmediate(p.gameObject);
            Object.DestroyImmediate(m_Go);
            Physics.SyncTransforms();
            PlacementFocus.Instance.Set(false);
        }

        PartInstance Place(string id, Vector3 at, float yawDeg = 180f)
        {
            var p = m_Loader.LoadFromCatalog(id);
            Assert.IsNotNull(p, $"catalog has {id}");
            m_Parts.PlaceAt(p, at, PlacementMath.AxisAngle(Vector3.up, yawDeg));   // facing the spawn (+Z)
            Physics.SyncTransforms();
            return p;
        }

        static Pose Aim(Vector3 target) => ToolInputHub.RayPose(MeasureScenarios.SpawnEye, target);

        static void Same(Vector3 a, Vector3 b, string what, float tol = 1e-4f) =>
            Assert.That(Vector3.Distance(a, b), Is.LessThan(tol), $"{what}: {a.ToString("F4")} vs {b.ToString("F4")}");

        static void Same(Quaternion a, Quaternion b, string what) => Assert.That(Quaternion.Angle(a, b), Is.LessThan(0.05f), what);

        // ---------------- lock ----------------

        [Test]
        public void PlacedPartsAreLockedOutsideAdjustMode()
        {
            var p = Place("window-ac-small", Spot);
            m_Tools.Equip(ToolKind.Part);
            var pose = p.transform.position;
            Hub.RaisePressStart(ToolHand.Right, Aim(p.WorldBoxCentre));
            Hub.RaisePressEnd(ToolHand.Right, Aim(p.WorldBoxCentre + Vector3.left));
            Assert.IsNull(m_Parts.Held, "a stray pinch doesn't pick it up");
            Assert.IsTrue(m_Parts.PlacedParts.Contains(p));
            Assert.AreSame(p, m_Parts.Selected, "…it selects it (the card's Adjust chip shows)");
            Same(pose, p.transform.position, "it didn't move");
            StringAssert.Contains("locked", m_Parts.LastAction);
            // The hook is the editor's: unlocked, the old pick-up works.
            m_Editor.lockPlaced = false;
            Hub.RaisePressStart(ToolHand.Right, Aim(p.WorldBoxCentre));
            Assert.AreSame(p, m_Parts.Held, "unlocked: picked up");
        }

        // ---------------- adjust mode ----------------

        [Test]
        public void AdjustTakesThePinchAndGivesItBack()
        {
            var p = Place("window-ac-small", Spot);
            Assert.IsTrue(AppCommands.AdjustPlacement(true), m_Editor.LastAction);
            Assert.AreSame(p, m_Editor.Adjusting);
            Assert.AreEqual(ToolKind.None, m_Tools.Active, "no tool in hand while adjusting: a pinch is a grab");
            Assert.IsTrue(PlacementFocus.Instance.On, "the world's labels step back");
            // A pinch on the part grabs it (no measure point), a drag moves it with the hand.
            var start = p.transform.position;
            Hub.RaisePressStart(ToolHand.Right, Aim(p.WorldBoxCentre));
            Assert.IsTrue(m_Editor.Grabbing, m_Editor.LastAction);
            var moved = new Pose(Aim(p.WorldBoxCentre).position + new Vector3(0.2f, 0f, 0f), Aim(p.WorldBoxCentre).rotation);
            Hub.RaisePressMove(ToolHand.Right, moved);
            Hub.RaisePressEnd(ToolHand.Right, moved);
            Assert.AreEqual(0, Tool.Session.Count, "the tape didn't get a point");
            Assert.AreEqual(0.2f, p.transform.position.x - start.x, 0.002f, "moved 20 cm right with the hand");
            // Picking a tool ends adjusting (the session is committed).
            m_Tools.Equip(ToolKind.Level);
            Assert.IsNull(m_Editor.Adjusting);
            Assert.IsFalse(PlacementFocus.Instance.On);
            Assert.AreEqual(1, m_Editor.UndoCount);
            // Adjust again, Done: the tool that was in hand comes back.
            m_Tools.Equip(ToolKind.Measure);
            Assert.IsTrue(m_Editor.Enter(p));
            Assert.IsTrue(AppCommands.AdjustPlacement(false));
            Assert.AreEqual(ToolKind.Measure, m_Tools.Active);
        }

        [Test]
        public void OneAdjustSessionIsOneUndoStep()
        {
            var p = Place("window-ac-small", Spot);
            var pos0 = p.transform.position; var rot0 = p.transform.rotation;
            Assert.IsTrue(m_Editor.Enter(p));
            for (int i = 0; i < 3; i++) Assert.IsTrue(m_Editor.NudgeStep(0));   // 3 × ⅜″ right
            Assert.IsTrue(m_Editor.RotateStep(0));                            // turn 5°
            Assert.IsTrue(m_Editor.NudgeStep(2));                             // ⅜″ up
            Assert.IsTrue(m_Editor.TryReadout(p, out var off, out var ttr));
            Assert.AreEqual(3 * 0.009525f, off.x, 1e-4f, "right");
            Assert.AreEqual(0.009525f, off.y, 1e-4f, "up");
            Assert.AreEqual(5f, ttr.x, 0.01f, "turned");
            Assert.IsTrue(EditHistory.CanUndo);
            Assert.AreEqual(0, m_Editor.UndoCount, "in progress: nothing committed yet");
            var pos1 = p.transform.position; var rot1 = p.transform.rotation;
            m_Editor.Exit();
            Assert.AreEqual(1, m_Editor.UndoCount, "the whole session is one step");
            Assert.IsTrue(EditHistory.Undo());
            Same(pos0, p.transform.position, "undo: back where it was");
            Same(rot0, p.transform.rotation, "undo: turned back");
            Assert.IsTrue(m_Parts.PlacedParts.Contains(p), "the placement itself is still there");
            Assert.IsTrue(EditHistory.Redo());
            Same(pos1, p.transform.position, "redo: the adjusted pose");
            Same(rot1, p.transform.rotation, "redo: turned");
            // The next undo is the session again, then the placement.
            Assert.IsTrue(EditHistory.Undo());
            Assert.IsTrue(EditHistory.Undo());
            Assert.IsFalse(m_Parts.PlacedParts.Contains(p), "then the placement");
        }

        [Test]
        public void UndoMidSessionGoesBackToItsStart()
        {
            var p = Place("window-ac-small", Spot);
            var pos0 = p.transform.position;
            m_Editor.Enter(p);
            m_Editor.NudgeStep(4);
            m_Editor.NudgeStep(4);
            Assert.IsTrue(EditHistory.Undo());
            Same(pos0, p.transform.position, "back to the session's start");
            Assert.AreSame(p, m_Editor.Adjusting, "still adjusting");
            Assert.IsTrue(EditHistory.Redo());
            Assert.Greater(Vector3.Distance(pos0, p.transform.position), 0.018f, "redo: moved out again");
        }

        [Test]
        public void ResetToFitAndSnap()
        {
            var p = Place("window-ac-small", Spot);
            var pos0 = p.transform.position;
            m_Editor.Enter(p);
            m_Editor.Nudge(new Vector3(0.05f, 0.02f, 0f), new Vector3(12f, 0f, 3f));
            Assert.IsTrue(AppCommands.ResetPlacement());
            Same(pos0, p.transform.position, "reset: the auto-placement pose");
            // A small grab near the fit snaps back to it (snap on); with snap off it stays.
            m_Editor.SetSnap(true);
            var aim = Aim(p.WorldBoxCentre);
            var nudged = new Pose(aim.position + new Vector3(0.01f, 0f, 0f), aim.rotation);
            Hub.RaisePressStart(ToolHand.Right, aim);
            Hub.RaisePressEnd(ToolHand.Right, nudged);
            Same(pos0, p.transform.position, "snapped back to the fit");
            StringAssert.Contains("snapped (fit)", m_Editor.LastAction);
            m_Editor.SetSnap(false);
            Hub.RaisePressStart(ToolHand.Right, aim);
            Hub.RaisePressEnd(ToolHand.Right, nudged);
            Assert.AreEqual(0.01f, p.transform.position.x - pos0.x, 0.001f, "snap off: 1 cm right");
        }

        // ---------------- saved placements ----------------

        [Test]
        public void SavesAreUndoableNotebookRowsAndSwitchable()
        {
            Notebook.Clear();
            var p = Place("window-ac-small", Spot);
            m_Editor.Enter(p);
            m_Editor.NudgeStep(0);
            Assert.IsTrue(AppCommands.SavePlacement(null));
            Assert.AreEqual("A", m_Editor.ActiveSlot);
            var row = Notebook.Entries.Last();
            Assert.AreEqual("placement_pose", row.Tool);
            Assert.AreEqual("A", row.Slot);
            Assert.AreEqual("window-ac-small", row.PartId);
            Assert.AreEqual(7, row.Pose.Length);
            var atA = p.transform.position;
            m_Editor.NudgeStep(1); m_Editor.NudgeStep(1); m_Editor.NudgeStep(1);
            Assert.IsNull(m_Editor.ActiveSlot, "moved off A");
            Assert.IsTrue(AppCommands.SavePlacement("b"));
            Assert.AreEqual("B", m_Editor.ActiveSlot);
            var atB = p.transform.position;
            Assert.IsTrue(AppCommands.LoadPlacement("A"));
            Same(atA, p.transform.position, "chip A");
            Assert.IsTrue(AppCommands.LoadPlacement("B"));
            Same(atB, p.transform.position, "chip B");
            int steps = m_Editor.UndoCount;
            m_Editor.Exit();
            Assert.AreEqual(steps, m_Editor.UndoCount, "switching A → B, ending where it began, is no step");
            // Undo: the save of B (its row goes), then the moves before it, then A.
            Assert.IsTrue(EditHistory.Undo());
            Assert.IsNull(m_Editor.Saved("B"), "save B undone");
            Assert.IsFalse(Notebook.Entries.Any(e => e.Tool == "placement_pose" && e.Slot == "B"), "its notebook row went too");
            Assert.IsNotNull(m_Editor.Saved("A"));
            Assert.IsTrue(EditHistory.Redo());
            Assert.IsNotNull(m_Editor.Saved("B"), "redo: B again");
            Assert.IsTrue(Notebook.Entries.Any(e => e.Tool == "placement_pose" && e.Slot == "B"));
        }

        // ---------------- model swaps ----------------

        [Test]
        public void SwappingTheModelKeepsTheAnchorAndIsOneUndo()
        {
            Notebook.Clear();
            var hanger = Place("hidden-hanger-5k", Spot);
            var spot = m_Editor.SpotOf(hanger);
            Assert.AreEqual(PlacementAnchor.BottomBackCentre, spot.Anchor, "off a cavity: the bottom-back-centre stays");
            var anchor = PlacementMath.AnchorOf(hanger.transform.position, hanger.transform.rotation, hanger.LocalBox, spot.Anchor);
            Assert.IsTrue(AppCommands.NextPlacedModel(1), m_Editor.LastSwap);
            var ac = m_Editor.Target();
            Assert.AreEqual("window-ac-small", ac.Spec.id, m_Editor.LastSwap);
            Same(anchor, PlacementMath.AnchorOf(ac.transform.position, ac.transform.rotation, ac.LocalBox, spot.Anchor), "anchor on anchor");
            Same(hanger.transform.rotation, ac.transform.rotation, "same turn");
            Assert.IsFalse(hanger.gameObject.activeSelf, "the hanger is put away (kept for undo)");
            Assert.IsFalse(m_Parts.PlacedParts.Contains(hanger));
            Assert.AreEqual(1, m_Parts.QuantityOf("window-ac-small"));
            Assert.AreEqual(0, m_Parts.QuantityOf("hidden-hanger-5k"));
            Assert.AreEqual("window-ac-small", Notebook.Entries.Last(e => e.Tool == "part").PartId, "the placement row is the new part's");
            // Cycling on comes back round to the hanger (the kept one: no reload) as the same undo step.
            Assert.IsTrue(AppCommands.NextPlacedModel(1));
            Assert.AreSame(hanger, m_Editor.Target(), "cycled back to the kept hanger");
            Assert.AreEqual(0, m_Editor.UndoCount, "back at the model you started from: the step cancels out (Undo reaches the placement)");
            Assert.IsTrue(AppCommands.NextPlacedModel(-1));
            Assert.AreSame(ac, m_Editor.Target());
            Assert.AreEqual(1, m_Editor.UndoCount, "cycling is one undo step");
            Assert.IsTrue(EditHistory.Undo());
            Assert.AreSame(hanger, m_Editor.Target(), "undo: the model you started from");
            Assert.IsTrue(m_Parts.PlacedParts.Contains(hanger) && !m_Parts.PlacedParts.Contains(ac));
            Assert.IsTrue(EditHistory.Redo());
            Assert.AreSame(ac, m_Editor.Target(), "redo: the AC");
            // Undoing the placement itself takes whichever model is there.
            Assert.IsTrue(EditHistory.Undo());
            Assert.IsTrue(EditHistory.Undo());
            Assert.AreEqual(0, m_Parts.PlacedParts.Count);
        }

        [Test]
        public void SavedPlacementKeepsItsPoseAcrossASwap()
        {
            var hanger = Place("hidden-hanger-5k", Spot);
            m_Editor.Enter(hanger);
            m_Editor.Nudge(new Vector3(0.03f, 0f, 0f), new Vector3(15f, 0f, 0f));
            m_Editor.SavePlacement("A");
            var spot = m_Editor.SpotOf(hanger);
            var anchor = PlacementMath.AnchorOf(hanger.transform.position, hanger.transform.rotation, hanger.LocalBox, spot.Anchor);
            Assert.IsTrue(m_Editor.CycleModel(1));
            var ac = m_Editor.Adjusting;
            Assert.AreEqual("window-ac-small", ac.Spec.id, "swapped while adjusting: the new model is the one adjusted");
            Same(anchor, PlacementMath.AnchorOf(ac.transform.position, ac.transform.rotation, ac.LocalBox, spot.Anchor), "at the saved placement");
            m_Editor.NudgeStep(1);
            Assert.IsTrue(m_Editor.LoadPlacement("A"));
            Same(anchor, PlacementMath.AnchorOf(ac.transform.position, ac.transform.rotation, ac.LocalBox, spot.Anchor), "placement A with the other model");
            m_Editor.Exit();
            Assert.IsTrue(EditHistory.Undo(), "the session (the swap and the moves)");
            Assert.AreSame(hanger, m_Editor.Target(), "one undo: the hanger at A's pose");
        }
    }
}
