using System.Collections.Generic;
using System.Linq;
using AirTools.Input;
using AirTools.Tools;
using AirTools.UI;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    public class LabelLayoutTests
    {
        static LabelBox Box(float u, float v, float w = 0.1f, float h = 0.03f, int pri = 1, int order = 0) =>
            new LabelBox { U = u, V = v, HalfW = w / 2, HalfH = h / 2, Priority = pri, Order = order };

        static List<LabelBox> Apply(List<LabelBox> boxes, float[] shifts)
        {
            var r = new List<LabelBox>();
            for (int i = 0; i < boxes.Count; i++) { var b = boxes[i]; b.V += shifts[i]; r.Add(b); }
            return r;
        }

        [Test]
        public void SeparateLabelsStayPut()
        {
            var boxes = new List<LabelBox> { Box(0, 0), Box(0.5f, 0), Box(0, 0.5f) };
            var shifts = new float[3];
            LabelLayout.Solve(boxes, shifts);
            Assert.That(shifts.All(s => s == 0f));
        }

        [Test]
        public void OverlappingLabelsNoLongerOverlap()
        {
            // The window-AC case: a part callout, a tape label and two side labels on top of each other.
            var boxes = new List<LabelBox> { Box(0, 0, pri: 3), Box(0.01f, 0.005f, order: 1), Box(-0.02f, 0.01f, order: 2), Box(0, -0.01f, pri: 2, order: 3) };
            var shifts = new float[4];
            LabelLayout.Solve(boxes, shifts, pad: 0.004f);
            var placed = Apply(boxes, shifts);
            for (int i = 0; i < placed.Count; i++)
                for (int j = i + 1; j < placed.Count; j++)
                    Assert.IsFalse(LabelLayout.Overlaps(placed[i], placed[j]), $"{i} overlaps {j}");
            Assert.AreEqual(0f, shifts[0], "highest priority keeps its place");
        }

        [Test]
        public void MovesTheShorterWay()
        {
            var boxes = new List<LabelBox> { Box(0, 0, pri: 2), Box(0, -0.02f) };   // second sits mostly below
            var shifts = new float[2];
            LabelLayout.Solve(boxes, shifts, pad: 0f);
            Assert.Less(shifts[1], 0f, "pushed down, not all the way over the top");
            Assert.AreEqual(-0.03f, boxes[1].V + shifts[1], 1e-5f);
        }

        [Test]
        public void CrowdedLowPriorityLabelsAreDecluttered()
        {
            // Ten small labels stacked on one spot under a big callout: some can't find room within 3 heights.
            var boxes = new List<LabelBox> { Box(0, 0, 0.3f, 0.1f, pri: 3) };
            for (int i = 0; i < 10; i++) boxes.Add(Box(0, 0, 0.1f, 0.03f, pri: 1, order: i + 1));
            var shifts = new float[boxes.Count];
            LabelLayout.Solve(boxes, shifts, pad: 0.004f, maxShiftHeights: 3f);
            Assert.AreEqual(0f, shifts[0], "the callout stays");
            int hidden = shifts.Count(float.IsNaN);
            Assert.Greater(hidden, 0);
            var shown = new List<LabelBox>();
            for (int i = 0; i < boxes.Count; i++) if (!float.IsNaN(shifts[i])) { var b = boxes[i]; b.V += shifts[i]; shown.Add(b); }
            for (int i = 0; i < shown.Count; i++)
                for (int j = i + 1; j < shown.Count; j++)
                    Assert.IsFalse(LabelLayout.Overlaps(shown[i], shown[j]), "every label still shown is clear");
        }

        [Test]
        public void OlderLabelWinsTies()
        {
            var boxes = new List<LabelBox> { Box(0, 0, order: 5), Box(0, 0, order: 2) };
            var shifts = new float[2];
            LabelLayout.Solve(boxes, shifts);
            Assert.AreEqual(0f, shifts[1]);
            Assert.AreNotEqual(0f, shifts[0]);
        }
    }

    public class PalmTests
    {
        // A hand held up in front of the face at (0, 1.4, 0.3), fingers up. Viewer at (0, 1.6, 0).
        static readonly Vector3 Head = new Vector3(0f, 1.6f, 0f);

        [Test]
        public void LeftPalmFacingTheViewerPointsAtTheHead()
        {
            // Left hand held up, palm toward the face: thumb/index on the viewer's LEFT, pinky on the right.
            var wrist = new Vector3(0f, 1.35f, 0.3f);
            Vector3 index = new Vector3(-0.03f, 1.43f, 0.3f), middle = new Vector3(-0.01f, 1.44f, 0.3f), pinky = new Vector3(0.03f, 1.42f, 0.3f);
            var palm = PalmMath.Palm(wrist, index, middle, pinky, leftHand: true);
            Assert.Greater(PalmMath.FacingDot(palm, Head), 0.8f, "palm toward the face opens the menu");
            // The same left hand turned round (back of the hand toward the face) mirrors index and pinky: must NOT face.
            var back = PalmMath.Palm(wrist, pinky, new Vector3(0.01f, 1.44f, 0.3f), index, leftHand: true);
            Assert.Less(PalmMath.FacingDot(back, Head), -0.8f, "palm facing outward doesn't open it");
        }

        [Test]
        public void RightPalmFacingTheViewer()
        {
            // Right hand, palm toward the face: thumb/index on the viewer's RIGHT.
            var wrist = new Vector3(0f, 1.35f, 0.3f);
            var palm = PalmMath.Palm(wrist, new Vector3(0.03f, 1.43f, 0.3f), new Vector3(0.01f, 1.44f, 0.3f), new Vector3(-0.03f, 1.42f, 0.3f), leftHand: false);
            Assert.Greater(PalmMath.FacingDot(palm, Head), 0.8f);
        }

        [Test]
        public void FingerExtension()
        {
            var w = Vector3.zero; var k = new Vector3(0, 0.09f, 0);
            Assert.IsTrue(PalmMath.IsExtended(w, k, k + new Vector3(0, 0.07f, 0.01f)));
            Assert.IsFalse(PalmMath.IsExtended(w, k, k + new Vector3(0, -0.02f, 0.04f)), "curled");
        }

        [Test]
        public void PalmHold_TheOtherHandOnTheMenu_KeepsItOpenAndStill_ThenLetsGo()
        {
            // fix-ux (2026-09-26): PokeGrace 0.6 → 0.3 s, only a real press / the ring's band holds, the palm turning away
            // or a hand lost past LostGrace closes (PalmHoldTests replays the headset log).
            var h = new PalmHold();
            (bool, bool) U(bool open, bool onRing, bool pressing, bool tracked, float now)
            {
                var st = h.Update(open, onRing, pressing, tracked, 0.9f, -0.9f, now);
                return (st.Hold, st.Freeze);
            }
            Assert.AreEqual((false, false), U(false, true, true, false, 0f), "closed: nothing to hold");
            Assert.AreEqual((false, false), U(true, false, false, true, 1f), "open, hand fine, nobody on it");
            // The right hand works the ring over the palm: the left hand's tracking degrades — held open and frozen.
            Assert.AreEqual((true, true), U(true, true, true, false, 1.1f));
            Assert.AreEqual((true, true), U(true, true, true, false, 3.0f), "as long as the other hand keeps working it");
            // It leaves: 0.3 s grace, then the lost hand (lost since 1.1 s, far past LostGrace) closes it.
            Assert.AreEqual((true, true), U(true, false, false, false, 3.2f));
            Assert.AreEqual(PalmCloseReason.HandLost, h.Update(true, false, false, false, 0f, 0f, 3.4f).Close);
            Assert.AreEqual((false, false), U(true, false, false, true, 3.8f), "reopened, hand back: follows it again");
            // A brief tracking drop-out on its own holds for 0.8 s, then closes.
            Assert.AreEqual((true, true), U(true, false, false, false, 4.0f));
            Assert.AreEqual((true, true), U(true, false, false, false, 4.7f));
            Assert.AreEqual(PalmCloseReason.HandLost, h.Update(true, false, false, false, 0f, 0f, 4.9f).Close);
        }

        [Test]
        public void GestureOpensAfterDelayAndHasHysteresis()
        {
            var g = new PalmGesture();
            Assert.IsFalse(g.Update(4, 0.9f, true, 0f));
            Assert.IsFalse(g.Update(4, 0.9f, true, 0.05f), "debounced");
            Assert.IsTrue(g.Update(4, 0.9f, true, 0.2f));
            Assert.IsTrue(g.Update(3, 0.4f, true, 0.3f), "stays open with 3 fingers, palm half turned");
            Assert.IsTrue(g.Update(1, 0.9f, true, 0.35f), "a fist has to last before it closes");
            Assert.IsFalse(g.Update(1, 0.9f, true, 0.7f));
            Assert.IsFalse(g.Update(4, 0.4f, true, 1.0f), "not facing enough to open");
            Assert.IsFalse(g.Update(4, 0.9f, false, 2.0f), "untracked hand never opens");
        }
    }

    public class PixelGuardrailTests
    {
        /// UX W0.11: world labels are gravity-aligned — a tilted head doesn't roll them.
        [Test]
        public void LabelsStayUprightWhenTheHeadRolls()
        {
            var camGo = new GameObject("cam") { tag = "MainCamera" };
            var root = new GameObject("labels");
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, 0f), Quaternion.Euler(0f, 0f, 30f));   // head rolled 30°
                var label = AirTools.Tools.MeasureLabel.Create(root.transform, new AirTools.Tools.MeasureStyle(), "L");
                label.SetAnchorWorld(new Vector3(0f, 1.5f, 2f));
                label.Set("1.50 m", 1f);
                Assert.Greater(Vector3.Dot(label.transform.up, Vector3.up), 0.99f, "upright, not rolled with the head");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(camGo); }
        }

        [Test]
        public void FoveationOnlyInTheWorld()
        {
            var go = new GameObject("valve");
            try
            {
                var v = go.AddComponent<AirTools.Core.RenderValve>();
                Assert.IsTrue(v.FoveatedIn(AirTools.Core.AppMode.World));
                Assert.IsFalse(v.FoveatedIn(AirTools.Core.AppMode.Passthrough));
                Assert.IsFalse(v.FoveatedIn(AirTools.Core.AppMode.Tabletop));
            }
            finally { Object.DestroyImmediate(go); }
        }
    }

    public class DoneGestureTests
    {
        [Test]
        public void MiddlePinchFiresDoneOnce()
        {
            var s = OvrToolInputSource.Gesture(0.2f, 0.9f, false, false);
            Assert.IsTrue(s.done); Assert.IsTrue(s.middle); Assert.IsFalse(s.index);
            s = OvrToolInputSource.Gesture(0.6f, 0.95f, s.index, s.middle);
            Assert.IsFalse(s.done, "held: no repeat"); Assert.IsFalse(s.index, "index can't start while the middle finger pinches");
            s = OvrToolInputSource.Gesture(0.1f, 0.2f, s.index, s.middle);
            Assert.IsFalse(s.middle);
            s = OvrToolInputSource.Gesture(0.1f, 0.9f, s.index, s.middle);
            Assert.IsTrue(s.done, "fires again after release");
        }

        /// UX W0.3: while Meta's system gesture is in progress (palm toward you → the universal menu), no pinch is a
        /// tool press or a finish, and a held one is released.
        [Test]
        public void SystemGestureReleasesEverything()
        {
            var s = OvrToolInputSource.Gesture(0.95f, 0.1f, false, false, systemGesture: true);
            Assert.IsFalse(s.index); Assert.IsFalse(s.middle); Assert.IsFalse(s.done);
            s = OvrToolInputSource.Gesture(0.1f, 0.95f, true, false, systemGesture: true);
            Assert.IsFalse(s.index, "a held pinch is released"); Assert.IsFalse(s.done, "no finish from a system pinch");
        }

        [Test]
        public void IndexPinchPlacesAndNeverFiresDone()
        {
            var s = OvrToolInputSource.Gesture(0.8f, 0.3f, false, false);
            Assert.IsTrue(s.index); Assert.IsFalse(s.done);
            s = OvrToolInputSource.Gesture(0.9f, 0.85f, s.index, s.middle);
            Assert.IsFalse(s.done, "middle creeping up during an index pinch doesn't finish");
            s = OvrToolInputSource.Gesture(0.2f, 0.1f, s.index, s.middle);
            Assert.IsFalse(s.index);
        }
    }

    public class DesignSystemTests
    {
        [TearDown]
        public void ResetSettings()
        {
            UiSettings.HighContrast = false;
            UiSettings.ReducedMotion = false;
        }

        [Test]
        public void TextTokens()
        {
            Assert.AreEqual("Measure", UiText.Sentence("MEASURE"));
            Assert.AreEqual("Window AC", UiText.Sentence("Window AC"));
            // Digits only: the decimal point keeps its own advance ("1.50", not "1 . 50").
            Assert.AreEqual("<mspace=0.58em>1</mspace>.<mspace=0.58em>50</mspace> m · <mspace=0.58em>4</mspace>′ <mspace=0.58em>11</mspace>″", UiText.Tabular("1.50 m · 4′ 11″"));
            Assert.AreEqual("<color=#FF382E>by <mspace=0.58em>500</mspace> mm</color>", UiText.Tabular("<color=#FF382E>by 500 mm</color>"), "tags untouched");
            // Tokens are em heights (UX W0.6): 20 dmm body text read at 45 cm = a 9 mm em (10.9 mm line).
            Assert.AreEqual(0.009f, UiText.EmMetres(TypeRole.Body, 0.45f), 1e-6f);
            Assert.AreEqual(0.009f * UiTheme.LineRatio, UiText.LineHeightMetres(TypeRole.Body, 0.45f), 1e-6f);
            Assert.AreEqual(Weight.Semibold, UiTheme.DefaultWeight(TypeRole.Title));
            Assert.AreEqual(Weight.Medium, UiTheme.DefaultWeight(TypeRole.Label));
            Assert.AreEqual(Weight.Regular, UiTheme.DefaultWeight(TypeRole.Body));
        }

        [Test]
        public void ThemeAssetIsBuiltWithFontAndMaterials()
        {
            var theme = Resources.Load<UiTheme>(UiTheme.ResourcePath);
            Assert.IsNotNull(theme, "AirTools ▸ Build UI Assets");
            Assert.IsNotNull(theme.type.font);
            Assert.IsNotNull(theme.type.regular); Assert.IsNotNull(theme.type.medium); Assert.IsNotNull(theme.type.semibold);
            Assert.IsNotNull(theme.type.fontMedium); Assert.IsNotNull(theme.type.fontSemibold);
            Assert.AreNotEqual(theme.type.font, theme.type.fontSemibold, "real weights, not a dilated Regular");
            Assert.AreEqual(theme.type.fontSemibold.atlasTexture, theme.type.semibold.GetTexture("_MainTex"), "material samples its own weight's atlas");
            foreach (var m in new[] { theme.materials.glassRegular, theme.materials.glassClear, theme.materials.elevatedSolid, theme.materials.shadow })
            {
                Assert.IsNotNull(m);
                Assert.AreEqual("AirTools/Glass", m.shader.name);
            }
            foreach (var f in new[] { theme.type.font, theme.type.fontMedium, theme.type.fontSemibold })
                foreach (char ch in "×·′″°²⅜½–…✓→") Assert.IsTrue(f.characterLookupTable.ContainsKey(ch), $"glyph {ch} in {f.name}");
        }

        [Test]
        public void GlassSurfaceWritesShapeIntoVertexData()
        {
            var s = GlassSurface.Create(null, "glass", new Vector2(0.2f, 0.1f), GlassTier.GlassRegular, RadiusRole.Large, Elevation.Panel);
            try
            {
                var mesh = s.GetComponent<MeshFilter>().sharedMesh;
                var uv1 = new List<Vector4>();
                mesh.GetUVs(1, uv1);
                Assert.AreEqual(4, uv1.Count);
                Assert.AreEqual(0.2f, uv1[0].x, 1e-6f); Assert.AreEqual(0.1f, uv1[0].y, 1e-6f);
                Assert.AreEqual(UiTheme.Current.shape.radiusLarge, uv1[0].z, 1e-6f);
                Assert.AreEqual(UiTheme.Current.colors.glassRegular, mesh.colors[0]);
                Assert.IsNotNull(s.transform.Find("Shadow"), "elevation adds a soft shadow");
                s.SetTint(Color.red);
                Assert.AreEqual(Color.red, mesh.colors[2]);
            }
            finally { Object.DestroyImmediate(s.gameObject); }
        }

        [Test]
        public void HighContrastTurnsGlassSolid()
        {
            Assert.AreEqual(GlassTier.GlassClear, UiSettings.Effective(GlassTier.GlassClear));
            UiSettings.HighContrast = true;
            Assert.AreEqual(GlassTier.ElevatedSolid, UiSettings.Effective(GlassTier.GlassClear));
            Assert.AreEqual(GlassTier.ElevatedSolid, UiSettings.Effective(GlassTier.GlassRegular));
            var s = GlassSurface.Create(null, "glass", new Vector2(0.1f, 0.1f), GlassTier.GlassRegular, RadiusRole.Medium);
            try { Assert.AreEqual(UiTheme.Current.colors.elevatedSolid, s.CurrentTint); }
            finally { Object.DestroyImmediate(s.gameObject); }
            UiSettings.ReducedMotion = true;
            Assert.AreEqual(0f, UiSettings.Duration(0.16f));
        }

        static GlassButton MakeButton(ButtonStyle style)
        {
            var go = new GameObject("button");
            var b = go.AddComponent<GlassButton>();
            b.style = style;
            b.cooldownSeconds = 0f;
            return b;
        }

        [Test]
        public void ButtonClicksAndStates()
        {
            var b = MakeButton(ButtonStyle.Toggle);
            try
            {
                int clicks = 0;
                b.Clicked += () => clicks++;
                Assert.IsTrue(b.Press());
                Assert.AreEqual(1, clicks);
                var rest = b.TargetTint();
                b.SetSelected(true);
                Assert.AreNotEqual(rest, b.TargetTint(), "selected is a distinct fill");
                b.SetInteractable(false);
                Assert.IsFalse(b.Press(), "disabled ignores presses");
                Assert.AreEqual(1, clicks);
            }
            finally { Object.DestroyImmediate(b.gameObject); }
        }

        [Test]
        public void DestructiveConfirmNeedsTwoPresses()
        {
            var b = MakeButton(ButtonStyle.Destructive);
            try
            {
                b.confirm = true;
                int clicks = 0;
                b.Clicked += () => clicks++;
                Assert.IsFalse(b.Press());
                Assert.IsTrue(b.Armed);
                Assert.AreEqual(0, clicks);
                Assert.IsTrue(b.Press());
                Assert.AreEqual(1, clicks);
                Assert.IsFalse(b.Armed);
            }
            finally { Object.DestroyImmediate(b.gameObject); }
        }

        /// UX D3: the primary is white with a dark label; selected / on is tape-yellow ink with dark text (a coached
        /// primary too); the armed destructive label is white; borderless is clear at rest.
        [Test]
        public void PrimaryIsWhiteSelectedIsInkAndBorderlessIsClearAtRest()
        {
            var c = UiTheme.Current.colors;
            var p = MakeButton(ButtonStyle.Primary);
            var q = MakeButton(ButtonStyle.Borderless);
            var t = MakeButton(ButtonStyle.Toggle);
            var d = MakeButton(ButtonStyle.Destructive);
            try
            {
                Assert.AreEqual(c.primary, p.TargetTint());
                Assert.AreEqual(c.onPrimary, p.TargetLabelColor());
                Assert.AreEqual(0f, q.TargetTint().a);
                Assert.AreEqual(c.textPrimary, q.TargetLabelColor());
                Assert.AreEqual(c.control, t.TargetTint());
                Assert.AreEqual(c.textPrimary, t.TargetLabelColor());
                t.SetSelected(true);
                Assert.AreEqual(c.ink, t.TargetTint(), "selected = ink");
                Assert.AreEqual(c.onInk, t.TargetLabelColor());
                p.SetSelected(true);
                Assert.AreEqual(c.ink, p.TargetTint(), "a coached primary shows as selected");
                Assert.AreEqual(c.onInk, p.TargetLabelColor());
                d.confirm = true;
                d.Press();
                Assert.IsTrue(d.Armed);
                Assert.AreEqual(c.dangerFill, d.TargetTint());
                Assert.AreEqual(c.onDanger, d.TargetLabelColor());
                p.SetInteractable(false);
                Assert.AreEqual(c.controlDisabled, p.TargetTint());
                Assert.AreEqual(c.textDisabled, p.TargetLabelColor());
            }
            finally
            {
                Object.DestroyImmediate(p.gameObject); Object.DestroyImmediate(q.gameObject);
                Object.DestroyImmediate(t.gameObject); Object.DestroyImmediate(d.gameObject);
            }
        }
    }
}

namespace AirTools.Tests
{
    public class ToolHandTests
    {
        [Test]
        public void MenuHandCannotTakeOverWhileToolHandIsTracked()
        {
            var go = new GameObject("hub");
            try
            {
                var hub = go.AddComponent<AirTools.Input.ToolInputHub>();
                hub.SetPointerOverride(AirTools.Input.ToolHand.Right, new Pose(Vector3.zero, Quaternion.identity));
                hub.RaisePressStart(AirTools.Input.ToolHand.Left, default);
                hub.RaisePressEnd(AirTools.Input.ToolHand.Left, default);
                hub.RaiseButton(AirTools.Input.ToolHand.Left, AirTools.Input.ToolButton.Finish);
                Assert.AreEqual(AirTools.Input.ToolHand.Right, hub.LastActiveHand, "the left hand never becomes the aiming hand");
                hub.SetPointerOverride(AirTools.Input.ToolHand.Right, null);
                hub.RaisePressStart(AirTools.Input.ToolHand.Left, default);
                Assert.AreEqual(AirTools.Input.ToolHand.Left, hub.LastActiveHand, "…unless the right hand isn't there at all");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void GestureNeedsAFacingPalm()
        {
            var g = new PalmGesture();
            Assert.IsFalse(g.Update(4, 0.5f, true, 0f));
            Assert.IsFalse(g.Update(4, 0.5f, true, 1f), "half-turned palm doesn't open (threshold 0.6)");
        }
    }
}
