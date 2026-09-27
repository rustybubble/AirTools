using System.Collections.Generic;
using System.Linq;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Tools;
using AirTools.UI;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AirTools.Tests
{
    /// The tool ring's wheel (DialPhysics), gestures and undo / redo routing.
    public class ToolRingTests
    {
        const float Dt = 1f / 72f;

        /// Flick at v rad/s (four drag frames), run 6 s; the items moved, the ticks heard, and the dial.
        static DialPhysics Spin(float v, out int moved, out int ticks)
        {
            var d = new DialPhysics(8);
            int start = d.Selected;
            ticks = 0;
            d.BeginDrag();
            for (int i = 0; i < 4; i++) { d.Drag(v * Dt, Dt); ticks += Mathf.Abs(d.Step(Dt)); }
            d.EndDrag();
            for (float t = 0f; t < 6f; t += Dt) ticks += Mathf.Abs(d.Step(Dt));
            moved = Mathf.RoundToInt(-d.Angle / d.Spacing);
            return d;
        }

        [Test]
        public void AHardFlickSpinsFurtherThanASoftOne_TickingEveryItem_ThenSettlesExactlyOnOne()
        {
            var soft = Spin(1.5f, out int softMoved, out int softTicks);
            var hard = Spin(11f, out int hardMoved, out int hardTicks);
            Assert.IsTrue(soft.Settled && hard.Settled);
            Assert.LessOrEqual(Mathf.Abs(softMoved), 1, "a soft flick just settles");
            Assert.GreaterOrEqual(Mathf.Abs(hardMoved), 3, "a hard flick passes a few items");
            Assert.LessOrEqual(Mathf.Abs(hardMoved), 7, "…but it isn't a slot machine");
            Assert.AreEqual(Mathf.Abs(hardMoved), hardTicks, "one tick per item passed");
            Assert.AreEqual(Mathf.Abs(softMoved), softTicks);
            Assert.AreEqual(0f, Mathf.Repeat(hard.Angle / hard.Spacing + 0.5f, 1f) - 0.5f, 1e-4f, "rests exactly on an item");
            // Dragging clockwise brings the items on the left to the top.
            Assert.Less(hardMoved, 0);
        }

        [Test]
        public void TheFlickSpeedIsCapped_AndSpinToTakesTheShortWayRound()
        {
            var d = new DialPhysics(8);
            d.BeginDrag();
            d.Drag(2f, 0.02f); d.Drag(2f, 0.02f);
            d.EndDrag();
            Assert.LessOrEqual(Mathf.Abs(d.Velocity), d.MaxSpeed + 1e-4f);

            var e = new DialPhysics(8);
            e.SpinTo(7);
            int ticks = 0;
            for (float t = 0f; t < 3f; t += Dt) ticks += Mathf.Abs(e.Step(Dt));
            Assert.AreEqual(7, e.Selected);
            Assert.AreEqual(1, ticks, "one step back, not seven forward");
            e.Jump(3);
            Assert.AreEqual(3, e.Selected);
            Assert.AreEqual(0, e.Step(Dt), "a jump makes no ticks");
        }

        [Test]
        public void DraggingTurnsTheWheelUnderTheFingers()
        {
            Assert.AreEqual(0.1f, ToolRing.DragAngle(new Vector2(0f, 0.1f), new Vector2(0.01f, 0f)), 1e-5f, "right along the top = clockwise");
            Assert.AreEqual(0.1f, ToolRing.DragAngle(new Vector2(0.1f, 0f), new Vector2(0f, -0.01f)), 1e-5f, "down the right side = clockwise");
            Assert.Less(Mathf.Abs(ToolRing.DragAngle(new Vector2(0f, 0.001f), new Vector2(0.01f, 0f))), 0.005f, "near the middle it can't whip round");
        }

        [Test]
        public void TwoQuickPinchesAreADoubleTap_SlowOrLongOnesAreNot()
        {
            var t = new DoubleTap();
            Assert.IsFalse(t.Update(true, 0f)); Assert.IsFalse(t.Update(false, 0.1f));
            Assert.IsFalse(t.Update(true, 0.3f)); Assert.IsTrue(t.Update(false, 0.4f), "second quick pinch");
            Assert.IsFalse(t.Update(true, 1f)); Assert.IsFalse(t.Update(false, 1.1f));
            Assert.IsFalse(t.Update(true, 2f)); Assert.IsFalse(t.Update(false, 2.1f), "too late");
            Assert.IsFalse(t.Update(true, 2.3f)); Assert.IsFalse(t.Update(false, 3f), "held too long");
            Assert.IsFalse(t.Update(true, 3.1f)); Assert.IsFalse(t.Update(false, 3.2f), "the long hold broke the pair");
        }

        class Fake : IEditable
        {
            public readonly List<float> Done = new List<float>(), Undone = new List<float>();
            public bool CanUndo => Done.Count > 0;
            public bool CanRedo => Undone.Count > 0;
            public float LastEditAt => Done.Count > 0 ? Done[Done.Count - 1] : float.NegativeInfinity;
            public float LastUndoAt => Undone.Count > 0 ? Undone[Undone.Count - 1] : float.NegativeInfinity;
            public float Clock;
            public void Undo() { Done.RemoveAt(Done.Count - 1); Undone.Add(Clock); }
            public void Redo() { Undone.RemoveAt(Undone.Count - 1); Done.Add(Clock); }
            public void DropRedo() => Undone.Clear();
        }

        [Test]
        public void UndoReachesTheLatestEditInAnyTool_RedoTheLatestUndo_ANewEditForgetsTheFuture()
        {
            var measure = new Fake(); var level = new Fake();
            EditHistory.Register(measure); EditHistory.Register(level);
            try
            {
                measure.Done.Add(1f); level.Done.Add(2f);
                Assert.IsTrue(EditHistory.CanUndo); Assert.IsFalse(EditHistory.CanRedo);
                level.Clock = measure.Clock = 3f;
                EditHistory.Undo();
                Assert.AreEqual(0, level.Done.Count, "the level (latest) went first");
                level.Clock = measure.Clock = 4f;
                EditHistory.Undo();
                Assert.AreEqual(0, measure.Done.Count);
                EditHistory.Redo();
                Assert.AreEqual(1, measure.Done.Count, "redo: the latest undo first");
                measure.Done.Add(5f);
                EditHistory.Edited(measure);
                Assert.IsFalse(level.CanRedo, "a new edit forgets the other tools' undone future");
            }
            finally { EditHistory.Unregister(measure); EditHistory.Unregister(level); }
        }

        /// UX W0.10: a hand-opened menu blocks tool presses while open and just after; a controller-opened one only
        /// blocks a trigger press that starts on the ring, so the right trigger keeps measuring elsewhere.
        [TestCase(true, "hand", 99f, false, true)]
        [TestCase(false, "", 0.2f, false, true)]
        [TestCase(false, "", 0.5f, false, false)]
        [TestCase(true, "forced", 99f, false, true)]
        [TestCase(true, "controller", 99f, false, false)]
        [TestCase(true, "controller", 99f, true, true)]
        [TestCase(false, "", 0.5f, true, false)]
        public void BlocksToolsTruthTable(bool open, string by, float sinceClosed, bool onMenu, bool blocks) =>
            Assert.AreEqual(blocks, PalmMenu.Blocks(open, by, sinceClosed, onMenu));

        /// Ring v2 (declutter DC3, M6) in Main.unity (run AirTools ▸ Wire Main Scene first): catalog: seven fixed items (the
        /// Catalog in W1.5's Parts seat, after Level); with Measure on the lens five read (fade ≥ 0.7) and the two one detent
        /// from the back are hidden; every item that shows is ≥ 4 cm from the Undo / Redo capsules.
        [Test]
        public void MainRingHasSevenItems()
        {
            var scene = EditorSceneManager.OpenScene(MainSceneBuilder.ScenePath, OpenSceneMode.Additive);
            try
            {
                var ring = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<ToolRing>(true)).FirstOrDefault();
                Assert.IsNotNull(ring, "the palm menu's ToolRing");
                CollectionAssert.AreEqual(new[] { "Move", "Measure", "Level", "Catalog", "Notebook", "Model view", "Settings" }, ring.items.Select(i => i.label).ToArray());
                // catalog: the Catalog item wears the storefront and toggles the Catalog window.
                var catalogItem = ring.items.Single(i => i.label == "Catalog");
                Assert.AreEqual(AirTools.UI.Icons.Catalog, catalogItem.icon);
                Assert.AreEqual(AirTools.UI.Icons.Catalog, catalogItem.iconText.text, "the storefront glyph on the ring");
                Assert.IsFalse(catalogItem.isMode, "an action: a pinch on the lens opens it");
                Assert.AreEqual(AirTools.Tools.ToolboxAction.ToggleCatalog, catalogItem.action);
                // scalemodels: Settings (was More) wears the gear, and opens the Settings window.
                var settingsItem = ring.items.Last();
                Assert.AreEqual(AirTools.UI.Icons.Settings, settingsItem.icon);
                Assert.AreEqual(AirTools.UI.Icons.Settings, settingsItem.iconText.text, "the gear glyph on the ring");
                Assert.AreEqual(AirTools.Tools.ToolboxAction.ToggleScenePanel, settingsItem.action);
                int measure = System.Array.FindIndex(ring.items, i => i.isMode && i.mode == ToolKind.Measure);
                var dial = new DialPhysics(ring.items.Length, measure);
                var undo = ring.ItemPos(-ring.buttonAngle * Mathf.Deg2Rad);
                var redo = ring.ItemPos(ring.buttonAngle * Mathf.Deg2Rad);
                int readable = 0, hidden = 0;
                var near = new List<string>();
                for (int i = 0; i < ring.items.Length; i++)
                {
                    float a = dial.ItemAngle(i), deg = Mathf.Abs(a) * Mathf.Rad2Deg;
                    float fade = ToolRing.Fade(deg, ring.visibleHalfAngle, ring.fadeBandDeg);
                    if (deg >= ring.visibleHalfAngle) hidden++;
                    else if (fade >= 0.7f) readable++;
                    var p = ring.ItemPos(a);
                    float gap = Mathf.Min(Vector2.Distance(p, undo), Vector2.Distance(p, redo));
                    if (fade > 0.3f && gap < 0.04f) near.Add($"{ring.items[i].label} {gap * 100f:0.0} cm from Undo/Redo");
                }
                Assert.AreEqual(5, readable, "five items read with Measure on the lens");
                Assert.AreEqual(2, hidden, "the two one detent from the back are hidden");
                // The Catalog is one of the five in view with Measure on the lens.
                float catalogDeg = Mathf.Abs(dial.ItemAngle(System.Array.FindIndex(ring.items, i => i.label == "Catalog"))) * Mathf.Rad2Deg;
                Assert.Less(catalogDeg, ring.visibleHalfAngle - ring.fadeBandDeg, "the Catalog reads with Measure on the lens");
                Assert.IsEmpty(near, string.Join("\n", near));
                Assert.IsTrue(ring.items.All(i => !i.confirm), "nothing on the ring asks twice (Exit world moved to Settings)");
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        [Test]
        public void RingLayout_TheInspectorCardSitsBesideTheRing_NothingSlides()
        {
            var go = new GameObject("palm");
            var card = new GameObject("card");
            try
            {
                var menu = go.AddComponent<PalmMenu>();
                menu.ringLayout = true;
                menu.panel = card.AddComponent<GlassSurface>();
                card.SetActive(false);
                menu.SetInspector(true);
                Assert.IsTrue(card.activeSelf);
                Assert.AreEqual(Vector3.zero, menu.ContentOffsetTarget);
                menu.SetInspector(false);
                Assert.IsFalse(card.activeSelf);
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(card); }
        }
    }
}
