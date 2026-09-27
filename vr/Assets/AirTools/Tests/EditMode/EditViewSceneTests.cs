using System.Collections.Generic;
using System.Linq;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AirTools.Tests
{
    /// edit6dof: the Edit view with a real PartTool, PlacementEditor, catalog parts and the ToolInputHub on the built-in
    /// facade (Editor gate: transforms, colliders, raycasts, glass surfaces), and the view as Wire builds it in Main.unity
    /// (run AirTools ▸ Wire Main Scene first). The maths and the flow are EditViewMathTests (offline too). The view here
    /// is put together in code (EditMode runs no OnEnable for AddComponent, so a knob's GlassButton isn't subscribed:
    /// the tests poke through EditArrowButton.Press / EditView.PokeArrow, the path Clicked takes); the fly is instant in
    /// EditMode. edit-touch: every control a poke button within reach; Move and the new part's placing only translate.
    public class EditViewSceneTests : FacadeToolFixture
    {
        static PartCatalog s_Catalog;
        static readonly Vector3 Spot = new Vector3(0.8f, 1.2f, 2.5f);
        GameObject m_Go, m_Head, m_ViewGo, m_DimGo, m_MenuGo, m_AdjustGo;
        PartTool m_Parts;
        PartLoader m_Loader;
        PlacementEditor m_Editor;
        ToolManager m_Tools;
        EditView m_View;
        PlacementPanel m_Adjust;
        float m_T;

        [OneTimeSetUp]
        public void LoadCatalog()
        {
            s_Catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath);
            if (s_Catalog == null || s_Catalog.entries.Count < 2) s_Catalog = PartCatalogBuilder.Build();
            PartCatalogBuilder.EnsurePartsLayer();
        }

        [SetUp]
        public void Create()
        {
            UiSettings.UseUnits(UnitSystem.Imperial);
            AppState.Set(AppMode.World);
            m_Go = new GameObject("editview-test");
            m_Loader = m_Go.AddComponent<PartLoader>();
            m_Loader.catalog = s_Catalog;
            m_Parts = m_Go.AddComponent<PartTool>();
            m_Parts.SetInput(Hub);
            Services.Register(m_Parts);
            EditHistory.Register(m_Parts);
            m_Tools = m_Go.AddComponent<ToolManager>();
            m_Tools.measure = Tool;
            m_Tools.parts = m_Parts;
            Services.Register(m_Tools);
            m_Tools.Equip(ToolKind.Measure);
            m_Editor = m_Go.AddComponent<PlacementEditor>();
            m_Editor.tool = m_Parts;
            m_Editor.loader = m_Loader;
            m_Editor.SyncCatalogLoads = true;
            m_Editor.Attach();
            m_Editor.SetInput(Hub);

            // An adjust panel open in the main slot: the Edit view closes it (and everything else).
            m_AdjustGo = new GameObject("adjust");
            var w = m_AdjustGo.AddComponent<FloatingWindow>();
            w.content = new GameObject("Content");
            w.content.transform.SetParent(m_AdjustGo.transform, false);
            m_Adjust = m_AdjustGo.AddComponent<PlacementPanel>();
            m_Adjust.window = w;
            Services.Register(m_Adjust);

            m_Head = new GameObject("head");
            m_Head.transform.SetPositionAndRotation(MeasureScenarios.SpawnEye, Quaternion.LookRotation(Vector3.back));
            m_View = BuildView(m_Head.transform);
            m_View.editor = m_Editor;
            m_View.tool = m_Parts;
            m_View.SetInput(Hub);
            m_View.Hook();
            Services.Register(m_View);
            m_T = 100f;
            m_View.Clock = () => m_T;
        }

        EditView BuildView(Transform head)
        {
            m_ViewGo = new GameObject("edit-view");
            var view = m_ViewGo.AddComponent<EditView>();
            view.head = head;
            var copy = new GameObject("copy").transform;
            copy.SetParent(m_ViewGo.transform, false);
            view.proxyRoot = copy;
            var stage = new GameObject("stage").transform;
            stage.SetParent(m_ViewGo.transform, false);
            view.stage = stage;
            var arrowsGo = new GameObject("arrows");
            arrowsGo.transform.SetParent(stage, false);
            var arrows = arrowsGo.AddComponent<EditArrows>();
            arrows.knobs = new GlassButton[EditViewMath.Arrows.Length];
            for (int i = 0; i < arrows.knobs.Length; i++)
            {
                var k = new GameObject(EditViewMath.Arrows[i].Name);
                k.transform.SetParent(arrowsGo.transform, false);
                var b = k.AddComponent<GlassButton>();
                b.surface = GlassSurface.Create(k.transform, "Surface", new Vector2(arrows.knobSize, arrows.knobSize), GlassTier.GlassClear, RadiusRole.Pill);
                var eb = k.AddComponent<EditArrowButton>();
                eb.button = b;
                eb.index = i;
                arrows.knobs[i] = b;
            }
            view.arrows = arrows;
            var panel = stage.gameObject.AddComponent<EditViewPanel>();
            panel.root = new GameObject("panel");
            panel.root.transform.SetParent(stage, false);
            view.panel = panel;
            m_DimGo = new GameObject("dim");
            m_DimGo.AddComponent<MeshFilter>();
            m_DimGo.AddComponent<MeshRenderer>().sharedMaterial = EditViewBuilder.DimMaterial();
            view.dim = m_DimGo.AddComponent<EditDim>();
            m_MenuGo = new GameObject("menu");
            var menu = m_MenuGo.AddComponent<PartContextMenu>();
            menu.head = head;
            menu.card = new GameObject("card");
            menu.card.transform.SetParent(m_MenuGo.transform, false);
            menu.undoRow = new GameObject("undo");
            menu.undoRow.transform.SetParent(menu.card.transform, false);
            menu.card.SetActive(false);
            view.menu = menu;
            stage.gameObject.SetActive(false);
            return view;
        }

        [TearDown]
        public void Destroy()
        {
            if (m_View != null && m_View.Active) m_View.Finish(keep: false);
            m_Editor.Detach();
            m_Parts.ClearAll();
            Services.Unregister(m_Parts);
            Services.Unregister(m_Tools);
            Services.Unregister(m_View);
            Services.Unregister(m_Adjust);
            EditHistory.Unregister(m_Parts);
            foreach (var p in Object.FindObjectsByType<PartInstance>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Object.DestroyImmediate(p.gameObject);
            foreach (var g in new[] { m_Go, m_Head, m_ViewGo, m_DimGo, m_MenuGo, m_AdjustGo }) if (g != null) Object.DestroyImmediate(g);
            Physics.SyncTransforms();
            PlacementFocus.Instance.Set(false);
            ModelViewDeclutter.EditViewHides = false;
            Hub.ClearOverrides();
        }

        PartInstance Place(string id, Vector3 at, float yawDeg = 180f)
        {
            var p = m_Loader.LoadFromCatalog(id);
            Assert.IsNotNull(p, $"catalog has {id}");
            m_Parts.PlaceAt(p, at, PlacementMath.AxisAngle(Vector3.up, yawDeg));
            Physics.SyncTransforms();
            return p;
        }

        void Open(PartInstance p)
        {
            Assert.IsTrue(m_View.OpenPlaced(p), m_View.LastAction);
            m_T += 0.6f;
            m_View.Tick(0.6f);
            Assert.AreEqual(EditPhase.Orient, m_View.Phase, m_View.Report());
        }

        /// A poke on arrow `name` (the path its GlassButton.Clicked takes), the finger out again at once.
        void Poke(string name)
        {
            m_View.arrows.knobs[EditView.ArrowIndex(name)].GetComponent<EditArrowButton>().Press();
            m_T += 0.02f;
            m_View.Tick(0.02f);
        }

        /// A poke on arrow `name` held in for `seconds` (60 Hz on the view's clock).
        void Hold(string name, float seconds)
        {
            m_View.arrows.knobs[EditView.ArrowIndex(name)].GetComponent<EditArrowButton>().Press();
            m_View.SimulateHold(true);
            for (float t = 0f; t < seconds - 1e-4f; t += 1f / 60f) { m_T += 1f / 60f; m_View.Tick(1f / 60f); }
            m_View.SimulateHold(false);
            m_T += 1f / 60f;
            m_View.Tick(1f / 60f);
        }

        static void Same(Vector3 a, Vector3 b, string what, float tol = 1e-4f) =>
            Assert.That(Vector3.Distance(a, b), Is.LessThan(tol), $"{what}: {a.ToString("F4")} vs {b.ToString("F4")}");

        // ---------------- opening ----------------

        [Test]
        public void OpeningShowsACopyAtTheCentreDimsTheWorldAndClosesTheRest()
        {
            var p = Place("window-ac-small", Spot);
            var pose = p.transform.position;
            m_Adjust.window.Open();
            Assert.IsTrue(m_Adjust.IsOpen);
            Open(p);
            Assert.IsTrue(m_Editor.EditViewActive, "the editor's session is the view's");
            Assert.AreSame(p, m_Editor.Adjusting);
            Assert.AreEqual(ToolKind.None, m_Tools.Active, "no tool in hand: only the view takes presses");
            Assert.IsFalse(m_Adjust.IsOpen, "other windows close");
            Assert.IsFalse(p.Model.gameObject.activeSelf, "the real part is hidden in the room…");
            Same(pose, p.transform.position, "…and stays at its true pose");
            Assert.AreEqual(1, m_View.proxyRoot.childCount, "a copy is shown instead");
            // edit-touch: within reach — the arrows 0.42 m from the eye, the panel 0.44 m, both below the eyes, facing them.
            var eye = m_Head.transform.position;
            m_View.arrows.Tick();
            for (int i = 0; i < m_View.arrows.Count; i++)
            {
                Assert.AreEqual(m_View.touchDistance, Vector3.Distance(eye, m_View.arrows.World[i]), 1e-3f, EditViewMath.Arrows[i].Name);
                Assert.Greater(Vector3.Dot(m_View.arrows.knobs[i].transform.forward, (m_View.arrows.World[i] - eye).normalized), 0.999f, "facing the eye");
            }
            var panelAt = m_View.panel.root.transform.position;
            Assert.AreEqual(m_View.panelDistance, Vector3.Distance(eye, panelAt), 1e-3f, "the panel within reach");
            Assert.That(eye.y - panelAt.y, Is.InRange(0.15f, 0.3f), "below the eyes");
            Assert.That(eye.y - m_View.stage.position.y, Is.InRange(0.05f, 0.25f), "the item below the eyes");
            var size = p.LocalBox.size;
            Assert.LessOrEqual(Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * m_View.ShownScale, 0.2501f, "its largest side at most 25 cm");
            Same(m_View.stage.position, m_View.proxyRoot.TransformPoint(p.LocalBox.center), "its box centre on the stage", 2e-3f);
            Assert.Greater(m_View.dim.Target, 0.3f, "the world greys out");
            Assert.IsTrue(m_View.dim.WritesDepth, "at the centre the item draws over near walls");
            Assert.IsTrue(ModelViewDeclutter.EditViewHides, "the world's annotations go");
            Assert.IsTrue(PlacementFocus.Instance.On, "the world's labels step back");
            Assert.IsTrue(p.OverDim, "the item draws after the dim");
            Assert.AreEqual(12, m_View.arrows.Enabled.Count(e => e), "all twelve arrows for a placed part");
        }

        // ---------------- arrows ----------------

        [Test]
        public void APokeStepsAndHoldingItRepeatsOnItsAxisOnly()
        {
            var p = Place("window-ac-small", Spot);
            Open(p);
            Poke("TiltUp");
            m_Editor.TryReadout(p, out var off, out var ttr);
            Assert.AreEqual(5f, ttr.y, 0.05f, "tilt up: +5°");
            Assert.AreEqual(0f, ttr.x, 0.05f);
            Assert.AreEqual(0f, ttr.z, 0.05f);
            Assert.AreEqual(-1, m_View.Pressed, "the finger came out: no repeat");
            m_Editor.SetFine(true);
            Poke("RollLeft");
            m_Editor.TryReadout(p, out off, out ttr);
            Assert.AreEqual(-1f, ttr.z, 0.05f, "fine: −1° roll");
            m_Editor.SetFine(false);
            // Turn ← held for a second (and a hair): the poke, then repeats at 0.4, 0.52, 0.64, 0.76, 0.88 and 1.0 s.
            m_Editor.TryReadout(p, out var off0, out var ttr0);
            Hold("TurnLeft", 1.02f);
            m_Editor.TryReadout(p, out off, out ttr);
            Assert.AreEqual(7, HoldRepeat.Count(1.02f));
            Assert.AreEqual(ttr0.x + 5f * 7, ttr.x, 0.1f, "seven steps of 5°");
            Assert.AreEqual(6, m_View.Repeats, "six of them repeats");
            Assert.AreEqual(ttr0.y, ttr.y, 0.05f, "tilt unchanged");
            Assert.AreEqual(ttr0.z, ttr.z, 0.05f, "roll unchanged");
            Same(off0, off, "offset unchanged");
            Assert.AreEqual(-1, m_View.Pressed, "let go: it stops");
            // Right held 0.5 s: two steps of ⅜″, right only.
            Hold("Right", 0.5f);
            m_Editor.TryReadout(p, out var off2, out var ttr2);
            Assert.AreEqual(off.x + HoldRepeat.Count(0.5f) * Edit6DofMath.Step(EditAxis.Right, UnitSystem.Imperial, false), off2.x, 0.002f, "right");
            Assert.AreEqual(off.y, off2.y, 1e-4f);
            Assert.AreEqual(off.z, off2.z, 1e-4f);
            Same(ttr, ttr2, "no turn", 0.05f);
            // A pinch at a knob does nothing now: the arrows are poke-only.
            m_View.arrows.Tick();
            var atKnob = ToolInputHub.RayPose(m_Head.transform.position, m_View.arrows.World[EditView.ArrowIndex("TiltUp")]);
            m_Editor.TryReadout(p, out var off3, out var ttr3);
            Hub.RaisePressStart(ToolHand.Right, atKnob);
            Hub.RaisePressEnd(ToolHand.Right, atKnob);
            m_Editor.TryReadout(p, out var off4, out var ttr4);
            Same(ttr3, ttr4, "a pinch on a knob doesn't step it", 0.05f);
        }

        [Test]
        public void TheItemTurnsWithTheHand()
        {
            var p = Place("window-ac-small", Spot);
            Open(p);
            m_Editor.TryReadout(p, out _, out var ttr0);
            var body = ToolInputHub.RayPose(m_Head.transform.position, m_View.stage.position);
            Hub.SetGripOverride(ToolHand.Right, new Pose(m_Head.transform.position, Quaternion.identity));
            Hub.RaisePressStart(ToolHand.Right, body);
            // The item turns about the axis the wrist turns about. The stage now sits below the eye (edit-touch: in reach),
            // so twist about its level roll axis (a twist about the tilted line of sight also yaws it a little).
            var rollAxis = Vector3.ProjectOnPlane(m_View.stage.forward, Vector3.up).normalized;
            Hub.SetGripOverride(ToolHand.Right, new Pose(m_Head.transform.position, PlacementMath.AxisAngle(rollAxis, 12f)));
            m_View.Tick(0.02f);
            Hub.RaisePressEnd(ToolHand.Right, body);
            m_Editor.TryReadout(p, out _, out var ttr);
            Assert.AreEqual(12f, Mathf.Abs(ttr.z - ttr0.z), 0.5f, "a 12° twist of the wrist rolls it 12°");
            Assert.AreEqual(ttr0.x, ttr.x, 0.5f);
            Assert.AreEqual(ttr0.y, ttr.y, 0.5f);
        }

        // ---------------- save, cancel, undo ----------------

        [Test]
        public void SaveIsOneUndoStepWithTheShadeAndCancelKeepsNothing()
        {
            var p = Place("window-ac-small", Spot);
            var rot0 = p.transform.rotation;
            int n0 = m_Editor.UndoCount;
            Open(p);
            Poke("TurnLeft");
            Poke("TiltUp");
            Assert.IsTrue(m_View.SetSwatch(5));
            Assert.AreEqual("Navy", p.Shade.Name);
            Assert.IsTrue(m_View.Save());
            Assert.AreEqual(EditPhase.Closing, m_View.Phase);
            m_T += 0.6f;
            m_View.Tick(0.6f);
            Assert.AreEqual(EditPhase.Idle, m_View.Phase);
            Assert.AreEqual(n0 + 1, m_Editor.UndoCount, "the whole session is one step");
            Assert.IsTrue(p.Model.gameObject.activeSelf, "the real part shows again");
            Assert.AreEqual(0, m_View.proxyRoot.childCount, "the copy goes");
            Assert.IsFalse(p.OverDim);
            Assert.AreEqual(0f, m_View.dim.Target);
            Assert.IsFalse(ModelViewDeclutter.EditViewHides);
            Assert.AreEqual(ToolKind.Measure, m_Tools.Active, "the tool comes back");
            var rot1 = p.transform.rotation;
            Assert.Greater(Quaternion.Angle(rot0, rot1), 5f);
            Assert.IsTrue(EditHistory.Undo());
            Assert.Less(Quaternion.Angle(rot0, p.transform.rotation), 0.05f, "undo: turned back");
            Assert.IsTrue(p.Shade.IsOriginal, "…and its colour");
            Assert.IsTrue(EditHistory.Redo());
            Assert.Less(Quaternion.Angle(rot1, p.transform.rotation), 0.05f);
            Assert.AreEqual("Navy", p.Shade.Name);
            // Cancel: nothing kept, no step.
            int n1 = m_Editor.UndoCount;
            Open(p);
            Poke("RollRight");
            m_View.SetSwatch(2);
            Assert.IsTrue(m_View.Cancel());
            m_T += 0.6f;
            m_View.Tick(0.6f);
            Assert.AreEqual(EditPhase.Idle, m_View.Phase);
            Assert.AreEqual(n1, m_Editor.UndoCount);
            Assert.Less(Quaternion.Angle(rot1, p.transform.rotation), 0.05f, "cancel: as it was");
            Assert.AreEqual("Navy", p.Shade.Name, "cancel: its colour as it was");
        }

        // ---------------- move ----------------

        [Test]
        public void MoveDragsOnlyThePartAndSaveKeepsItsNewSpot()
        {
            var p = Place("window-ac-small", Spot);
            var at0 = p.transform.position;
            int n0 = m_Editor.UndoCount;
            Open(p);
            Poke("TiltUp");   // turned a little first: the move must keep it
            var turned = p.transform.rotation;
            Assert.IsTrue(m_View.StartMove());
            m_T += 0.6f;
            m_View.Tick(0.6f);
            Assert.AreEqual(EditPhase.Moving, m_View.Phase);
            Assert.IsTrue(p.Model.gameObject.activeSelf, "the real part, out in the room");
            Assert.IsFalse(m_View.dim.WritesDepth, "depth-tested against the room while it moves");
            var aim = ToolInputHub.RayPose(MeasureScenarios.SpawnEye, new Vector3(-1f, 0f, 1.5f));
            Hub.RaisePressStart(ToolHand.Right, aim);
            m_View.Tick(0.02f);
            Hub.RaisePressEnd(ToolHand.Right, aim);
            Assert.AreEqual(0, Tool.Session.Count, "the tape didn't get a point: only the part moves");
            Assert.Greater(Vector3.Distance(at0, p.transform.position), 0.5f, "on the ground where you aimed");
            Assert.Less(Quaternion.Angle(turned, p.transform.rotation), 0.01f, "edit-touch: a translation only — its turn untouched");
            var lowest = float.MaxValue;
            var b = p.LocalBox;
            for (int c = 0; c < 8; c++)
                lowest = Mathf.Min(lowest, p.transform.TransformPoint(b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1))).y);
            Assert.AreEqual(0f, lowest, 0.02f, "seated on it (its lowest corner on the ground)");
            var moved = p.transform.position;
            Assert.IsTrue(m_View.Save());
            Assert.AreEqual(EditPhase.Idle, m_View.Phase);
            Assert.AreEqual(n0 + 1, m_Editor.UndoCount);
            Same(moved, p.transform.position, "saved where it is");
            Assert.Less(Quaternion.Angle(turned, p.transform.rotation), 0.01f, "saved with its turn");
            Assert.IsTrue(EditHistory.Undo());
            Same(at0, p.transform.position, "undo: back where it was", 1e-3f);
            Assert.Less(Quaternion.Angle(PlacementMath.AxisAngle(Vector3.up, 180f), p.transform.rotation), 0.05f, "…and turned as it was");
        }

        // ---------------- a new part ----------------

        [Test]
        public void ANewPartIsOrientedFirstThenPlacingItSavesAndCloses()
        {
            var p = m_Loader.LoadFromCatalog("window-ac-small");
            Assert.IsTrue(m_View.OpenNew(p));
            m_T += 0.6f;
            m_View.Tick(0.6f);
            Assert.AreEqual(EditPhase.Orient, m_View.Phase);
            Assert.AreEqual(EditKind.New, m_View.Kind);
            Assert.AreEqual(6, m_View.arrows.Enabled.Count(e => e), "a new part: the six turn arrows only");
            Same(m_View.stage.position, p.WorldBoxCentre, "at the centre", 0.01f);
            Assert.Less(Vector3.Distance(m_Head.transform.position, p.WorldBoxCentre), 0.56f, "within reach");
            Poke("RollRight");
            Assert.AreEqual(5f, m_View.NewTurn.z, 0.05f);
            Assert.IsTrue(m_View.SetSwatch(2));
            var oriented = p.transform.rotation;
            Assert.IsTrue(m_View.Place());
            Assert.Less(Quaternion.Angle(oriented, p.transform.rotation), 0.01f, "on the pointer with the turn it was given");
            Assert.AreSame(p, m_Parts.Held, "on the pointer");
            var aim = ToolInputHub.RayPose(MeasureScenarios.SpawnEye, new Vector3(-1f, 0f, 1.5f));
            Hub.RaisePressStart(ToolHand.Right, aim);
            Hub.RaisePressEnd(ToolHand.Right, aim);
            Assert.IsTrue(p.Placed, m_Parts.LastAction);
            Assert.AreEqual(EditPhase.Idle, m_View.Phase, "placing it saved and closed the view");
            Assert.AreEqual(1, m_View.Places);
            Assert.AreEqual(5f, Vector3.Angle(p.transform.up, Vector3.up), 0.6f, "it kept its roll");
            Assert.Less(Quaternion.Angle(oriented, p.transform.rotation), 0.01f, "edit-touch: placed with exactly the Edit view's rotation");
            Assert.IsNull(m_Editor.HeldTurnPart, "the kept rotation is spent");
            Assert.AreEqual("Black", p.Shade.Name, "and its colour");
            Assert.IsTrue(EditHistory.Undo(), "one undo: the placement");
            Assert.IsFalse(m_Parts.PlacedParts.Contains(p));
        }

        // ---------------- the context menu ----------------

        [Test]
        public void TheGripOrALongPinchOpensTheMenuAndDeleteIsUndoable()
        {
            var p = Place("window-ac-small", Spot);
            Hub.SetPointerOverride(ToolHand.Right, ToolInputHub.RayPose(MeasureScenarios.SpawnEye, p.WorldBoxCentre));
            Hub.RaiseButton(ToolHand.Right, ToolButton.Context);
            Assert.IsTrue(m_View.menu.Showing, "the grip squeeze at it");
            Assert.AreSame(p, m_View.menu.Part);
            m_View.menu.Hide();
            // A long pinch with the tape in hand is a measurement, not a menu.
            var aim = ToolInputHub.RayPose(MeasureScenarios.SpawnEye, p.WorldBoxCentre);
            m_Tools.Equip(ToolKind.Measure);
            Hub.RaisePressStart(ToolHand.Right, aim);
            m_T += 0.7f;
            m_View.Tick(0.7f);
            Hub.RaisePressEnd(ToolHand.Right, aim);
            Assert.IsFalse(m_View.menu.Showing, "measuring: no menu");
            Tool.ClearAll();
            // With the Part tool: 0.6 s held still opens it.
            m_Tools.Equip(ToolKind.Part);
            Hub.RaisePressStart(ToolHand.Right, aim);
            m_T += 0.3f;
            m_View.Tick(0.3f);
            Assert.IsFalse(m_View.menu.Showing, "not yet");
            m_T += 0.35f;
            m_View.Tick(0.35f);
            Assert.IsTrue(m_View.menu.Showing, "held: the menu");
            Hub.RaisePressEnd(ToolHand.Right, aim);
            Assert.IsTrue(m_View.menu.Showing, "it stays after the release");
            Assert.IsNull(m_Parts.Held, "the part stays placed");
            // Delete, then the menu's Undo brings it back.
            Assert.IsTrue(m_View.Delete(p));
            Assert.IsFalse(m_Parts.PlacedParts.Contains(p));
            Assert.IsFalse(p.gameObject.activeSelf);
            Assert.IsTrue(m_View.menu.UndoShowing, "Deleted · Undo");
            Assert.IsTrue(m_View.UndoDelete());
            Assert.IsTrue(m_Parts.PlacedParts.Contains(p));
            Assert.IsTrue(p.gameObject.activeSelf);
            // Deleted again, the global Undo does the same.
            Assert.IsTrue(m_View.Delete(p));
            Assert.IsTrue(EditHistory.Undo());
            Assert.IsTrue(m_Parts.PlacedParts.Contains(p));
            Assert.IsTrue(EditHistory.Redo());
            Assert.IsFalse(m_Parts.PlacedParts.Contains(p), "redo: deleted again");
        }

        /// delete-undo: the menu's Delete brings the part back exactly — pose, shade, fit, selection, the same notebook
        /// row — by the chip's Undo and by the ring's; the toast says it can be undone.
        [Test]
        public void MenuDelete_UndoBringsItBackExactly()
        {
            var p = Place("window-ac-small", Spot);
            p.SetShade(EditShades.Make(1, EditShades.Full));
            string shade = p.Shade.ToString();
            var row = AirTools.Notes.Notebook.Last;
            Assert.AreEqual("part", row.Tool);
            var pos = p.transform.position;
            var rot = p.transform.rotation;
            var status = p.Fit?.Status;
            string headline = p.Fit?.Headline;
            void AssertBack(string how)
            {
                Assert.IsTrue(m_Parts.PlacedParts.Contains(p) && p.gameObject.activeSelf && p.Placed, how);
                Assert.That(Vector3.Distance(pos, p.transform.position), Is.LessThan(1e-5f), $"{how}: same place");
                Assert.That(Quaternion.Angle(rot, p.transform.rotation), Is.LessThan(1e-3f), $"{how}: same turn");
                Assert.AreEqual(shade, p.Shade.ToString(), $"{how}: same shade");
                Assert.AreEqual(status, p.Fit?.Status, $"{how}: same fit");
                Assert.AreEqual(headline, p.Fit?.Headline, $"{how}: same fit words");
                Assert.AreSame(p, m_Parts.Selected, $"{how}: selected");
                Assert.AreSame(row, AirTools.Notes.Notebook.Find(row.Id), $"{how}: its row, same id");
            }

            Assert.IsTrue(m_View.OpenMenu(p));
            string toast = null;
            const string k = "[AirTools] Toast: ";
            void OnLog(string m, string s, LogType t) { if (m != null && m.StartsWith(k)) toast = m.Substring(k.Length); }
            Application.logMessageReceived += OnLog;
            try { Assert.IsTrue(m_View.Delete(p)); }
            finally { Application.logMessageReceived -= OnLog; }
            StringAssert.EndsWith("removed · Undo on the ring", toast);
            Assert.IsNull(AirTools.Notes.Notebook.Find(row.Id), "its row goes");
            Assert.IsTrue(p != null && !p.gameObject.activeSelf, "hidden, kept");

            Assert.IsTrue(m_View.UndoDelete());
            AssertBack("the chip's Undo");
            Assert.IsTrue(m_View.Delete(p));
            Assert.IsTrue(EditHistory.Undo());
            AssertBack("the ring's Undo");
            Assert.IsTrue(EditHistory.Redo());
            Assert.IsNull(AirTools.Notes.Notebook.Find(row.Id), "redo: gone again");
            Assert.IsTrue(EditHistory.Undo());
            AssertBack("undo again");
        }

        // ---------------- as Wire builds it ----------------

        [Test]
        public void WireBuildsTheEditViewArrowsSwatchesMoveBarDimAndMenu()
        {
            var scene = EditorSceneManager.OpenScene(MainSceneBuilder.ScenePath, OpenSceneMode.Additive);
            try
            {
                var roots = scene.GetRootGameObjects();
                var views = roots.SelectMany(r => r.GetComponentsInChildren<EditView>(true)).ToList();
                Assert.AreEqual(1, views.Count, "one Edit view");
                var v = views[0];
                Assert.IsNotNull(v.editor, "the placement editor");
                Assert.IsNotNull(v.tool, "the part tool");
                Assert.IsNotNull(v.head, "the centre eye");
                Assert.IsNotNull(v.proxyRoot);
                Assert.IsFalse(v.stage.gameObject.activeSelf, "hidden until it opens");
                // Twelve arrows: round glass poke buttons (Control), a glyph each (Phosphor or Out / In), EditArrowButton.
                Assert.AreEqual(EditViewMath.Arrows.Length, v.arrows.knobs.Length);
                for (int i = 0; i < v.arrows.knobs.Length; i++)
                {
                    var k = v.arrows.knobs[i];
                    Assert.IsNotNull(k, EditViewMath.Arrows[i].Name);
                    Assert.AreEqual(GlassRole.Control, k.surface.EffectiveRole, EditViewMath.Arrows[i].Name);
                    Assert.IsNotNull(v.arrows.glyphs[i], $"{EditViewMath.Arrows[i].Name}: glyph");
                    Assert.IsNotNull(k.poke, $"{k.name}: an ISDK poke");
                    Assert.GreaterOrEqual(k.surface.size.x, 0.03f, "a 3 cm target");
                    var eb = k.GetComponent<EditArrowButton>();
                    Assert.AreSame(k, eb.button);
                    Assert.AreEqual(i, eb.index);
                }
                Assert.IsNotNull(v.arrows.readoutPlate);
                // The panel: swatches, Step · Shade · Reset · Fit, Cancel · Move · Save / Place.
                var panel = v.panel;
                Assert.AreEqual(EditShades.Count, panel.swatches.Length);
                for (int i = 0; i < panel.swatches.Length; i++)
                {
                    var eb = panel.swatches[i].GetComponent<EditViewButton>();
                    Assert.AreEqual(EditViewAction.Swatch, eb.action);
                    Assert.AreEqual(i, eb.index);
                }
                foreach (var (b, a) in new[] { (panel.step, EditViewAction.Step), (panel.shade, EditViewAction.Shade), (panel.reset, EditViewAction.ResetTurn),
                             (panel.fitToOpening, EditViewAction.FitToOpening), (panel.cancel, EditViewAction.Cancel), (panel.move, EditViewAction.Move),
                             (panel.save, EditViewAction.Save), (panel.place, EditViewAction.Place), (panel.moveSave, EditViewAction.Save), (panel.moveCancel, EditViewAction.Cancel) })
                {
                    Assert.IsNotNull(b, a.ToString());
                    Assert.AreEqual(a, b.GetComponent<EditViewButton>().action, b.name);
                }
                Assert.AreEqual(ButtonStyle.Primary, panel.save.style, "one primary");
                // The move bar: a main-slot card.
                var bar = panel.moveWindow;
                Assert.IsNotNull(bar);
                Assert.IsTrue(bar.mainSlot);
                Assert.IsTrue(UiZones.Main.Holds(bar.distance, bar.downDeg, bar.yawDeg), "the main slot");
                var barPanel = FloatingWindow.FindPanel(bar.transform);
                Assert.LessOrEqual(barPanel.size.x, UiZones.Main.MaxWidth + 1e-4f);
                Assert.LessOrEqual(barPanel.size.y, UiZones.Main.MaxHeight + 1e-4f);
                // The dim: on the centre eye, behind the item, AirTools/DimShell, off.
                Assert.IsNotNull(v.dim);
                Assert.AreSame(v.head, v.dim.transform.parent, "on the centre eye");
                Assert.Greater(v.dim.transform.localScale.x * 0.5f, v.touchDistance + 1f, "the sphere is behind the item");
                var r = v.dim.GetComponent<MeshRenderer>();
                Assert.AreEqual("AirTools/DimShell", r.sharedMaterial.shader.name);
                Assert.IsFalse(r.enabled);
                // The context menu.
                var m = v.menu;
                Assert.IsNotNull(m);
                foreach (var (b, a) in new[] { (m.edit, EditViewAction.MenuEdit), (m.similar, EditViewAction.MenuSimilar), (m.delete, EditViewAction.MenuDelete), (m.undo, EditViewAction.MenuUndo) })
                    Assert.AreEqual(a, b.GetComponent<EditViewButton>().action, b.name);
                Assert.IsFalse(m.card.activeSelf);
                // edit-touch: every control is a poke button (a fingertip, or a controller's poke tip) and has no ray.
                var buttons = new List<GlassButton>();
                buttons.AddRange(v.GetComponentsInChildren<GlassButton>(true));
                buttons.AddRange(bar.GetComponentsInChildren<GlassButton>(true));
                buttons.AddRange(m.GetComponentsInChildren<GlassButton>(true));
                Assert.Greater(buttons.Count, 20);
                Assert.Greater(buttons.Count, 30);
                foreach (var b in buttons)
                {
                    Assert.IsNotNull(b.poke, $"{b.name}: poke");
                    Assert.IsNull(b.ray, $"{b.name}: no ray");
                }
                // The card's chip opens it (it reads "Edit"); the grips feed the hub.
                var chip = roots.SelectMany(x => x.GetComponentsInChildren<PlacementButton>(true)).First(pb => pb.action == PlacementAction.ToggleAdjust);
                Assert.AreEqual("Edit", chip.button.Text);
                var source = roots.SelectMany(x => x.GetComponentsInChildren<OvrToolInputSource>(true)).First();
                Assert.IsNotNull(source.leftGrip, "left grip");
                Assert.IsNotNull(source.rightGrip, "right grip");
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
