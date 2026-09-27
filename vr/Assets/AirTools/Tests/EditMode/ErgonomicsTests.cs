using System.Collections.Generic;
using System.Linq;
using AirTools.Editor;
using AirTools.UI;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AirTools.Tests
{
    /// UX acceptance (docs/ux/README.md §5) that code can check: A1 text floor at each surface's real distance, A4
    /// contrast of labels on filled buttons (and D3's white primary / ink, text on windows and rows), the glyphs the copy
    /// uses exist in the Inter atlas, A7 / A8 placement from the person, A9 status near the gaze, A10 one window at a time.
    public class ErgonomicsTests
    {
        // ---------------- A1: nothing below 16 dmm at its placement ----------------

        /// Where each UI root is read from (m): its own placement where it has one, else the design distance.
        static float ReadDistance(Transform t, out bool ring)
        {
            ring = false;
            for (var p = t; p != null; p = p.parent)
            {
                if (p.name.StartsWith("Item_") && p.parent != null && p.parent.name == "ToolRing") ring = true;
                if (p.GetComponent<HeadAnchor>() is HeadAnchor a) return a.distance;
                if (p.GetComponent<FloatingWindow>() is FloatingWindow w) return w.distance;
                if (p.GetComponent<AirTools.Notes.NotebookPanel>() is AirTools.Notes.NotebookPanel n) return n.distance;
                if (p.GetComponent<AirTools.Parts.PartsBrowser>() is AirTools.Parts.PartsBrowser b) return b.distance;
                if (p.GetComponent<UiToast>() is UiToast toast) return toast.distance;
                if (p.GetComponent<StatusLine>() is StatusLine line) return line.distance;      // W1.3 guide rail
                if (p.GetComponent<NextStepPill>() is NextStepPill pill) return pill.distance;  // W1.3 guide rail
                if (p.GetComponent<AirTools.Scene.SpawnMarker>() != null) return 0.6f;
                if (p.GetComponent<AirTools.Scene.TruthBar>() != null) return 0.7f;
                if (p.GetComponent<AirTools.Scene.ModelWheel>() is AirTools.Scene.ModelWheel wheel) return wheel.readDistance;   // modelwheel
                if (p.GetComponent<AirTools.Input.PalmMenu>() != null) return UiText.HandDistance;
                // edit-touch: the Edit view (arrows 0.42 m, panel 0.44 m) and a part's context menu (0.42 m), within reach.
                if (p.GetComponent<AirTools.Parts.EditView>() != null) return AirTools.Parts.EditView.ReadDistance;
                if (p.GetComponent<AirTools.Parts.PartContextMenu>() != null) return AirTools.Parts.EditView.ReadDistance;
            }
            return -1f;
        }

        [Test]
        public void EveryBuiltTextIsAtLeast16DmmAtItsPlacement()
        {
            var scene = EditorSceneManager.OpenScene(MainSceneBuilder.ScenePath, OpenSceneMode.Additive);
            try
            {
                var theme = UiTheme.Current;
                var icons = new HashSet<TMP_FontAsset> { theme.icons.regular, theme.icons.fill };
                var small = new List<string>();
                int checkedCount = 0;
                foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (icons.Contains(t.font)) continue;                       // Phosphor glyphs, not text
                    float d = ReadDistance(t.transform, out bool ring);
                    if (d <= 0f) continue;                                      // world annotations (A2) etc.
                    float em = UiText.EmDmmAt(t, d) * (ring ? 0.9f : 1f);      // ring items shrink to 0.9 off the lens
                    checkedCount++;
                    if (em < UiTheme.EmFloorDmm - 0.05f) small.Add($"{Path(t.transform)} \"{t.text}\" {em:0.0} dmm at {d:0.00} m");
                }
                Assert.Greater(checkedCount, 60, "walked the UI");
                Assert.IsEmpty(small, "below the 16 dmm floor:\n" + string.Join("\n", small));
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null; p = p.parent) parts.Insert(0, p.name);
            return string.Join("/", parts.Skip(Mathf.Max(0, parts.Count - 4)));
        }

        /// No drawn text shrinks below the floor with a size tag: the only <size=%> under 100 are the window titles'
        /// subtitles (Heading 26 dmm × 70 % = 18.2).
        [Test]
        public void NoSizeTagsBelowTheFloor()
        {
            var offenders = new List<string>();
            foreach (var f in System.IO.Directory.GetFiles("Assets/AirTools/Runtime", "*.cs", System.IO.SearchOption.AllDirectories))
                foreach (var line in System.IO.File.ReadAllLines(f))
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(line, @"<size=(\d+)%>"))
                        if (int.Parse(m.Groups[1].Value) < 100 && !line.Contains("title.text"))
                            offenders.Add($"{System.IO.Path.GetFileName(f)}: {line.Trim()}");
            Assert.IsEmpty(offenders, string.Join("\n", offenders));
            Assert.GreaterOrEqual(UiTheme.Current.EmDmm(TypeRole.Heading) * 0.7f, UiTheme.EmFloorDmm, "title subtitles stay above the floor");
            foreach (TypeRole r in System.Enum.GetValues(typeof(TypeRole)))
                Assert.GreaterOrEqual(UiTheme.Current.EmDmm(r), UiTheme.EmFloorDmm, $"{r} token");
        }

        // ---------------- A4 / D3: contrast from the tokens ----------------
        // Pure maths on the colour tokens (WCAG 2 relative luminance), so the code-token checks run offline too.

        static double Lin(float v) => v <= 0.04045 ? v / 12.92 : System.Math.Pow((v + 0.055) / 1.055, 2.4);

        static double Lum(Color c) => 0.2126 * Lin(c.r) + 0.7152 * Lin(c.g) + 0.0722 * Lin(c.b);

        static double Ratio(double la, double lb) => (System.Math.Max(la, lb) + 0.05) / (System.Math.Min(la, lb) + 0.05);

        public static double Contrast(Color a, Color b) => Ratio(Lum(a), Lum(b));

        /// Luminance of `c` alpha-blended over a backdrop of luminance `under` (blending happens in linear space in the
        /// Linear project's sRGB eye buffer, and luminance is linear in linear RGB).
        static double Over(Color c, double under) => c.a * Lum(c) + (1.0 - c.a) * under;

        /// What can sit behind a window: a white wall or the sky horizon in passthrough / the world (worst for light
        /// text), mid grey, black.
        static readonly double[] s_Backdrops = { 1.0, 0.18, 0.0 };

        /// Worst contrast of `text` drawn on `surfaces` (stacked back to front, e.g. window → row) over any backdrop.
        static double OnStack(Color text, params Color[] surfaces)
        {
            double worst = double.MaxValue;
            foreach (double b in s_Backdrops)
            {
                double under = b;
                foreach (var s in surfaces) under = Over(s, under);
                worst = System.Math.Min(worst, Ratio(Over(text, under), under));
            }
            return worst;
        }

        static string Hex(Color c) =>
            $"{Mathf.RoundToInt(c.r * 255f):X2}{Mathf.RoundToInt(c.g * 255f):X2}{Mathf.RoundToInt(c.b * 255f):X2}";

        /// Every failing pair for a token set (empty = all pass).
        static List<string> ContrastProblems(UiTheme.Colors c)
        {
            var bad = new List<string>();
            void Need(string what, double ratio, double min) { if (ratio < min - 1e-6) bad.Add($"{what}: {ratio:0.00}:1 < {min}:1"); }
            // D3: dark labels on the white primary, in every state (≥ 7:1).
            Need("onPrimary on primary", Contrast(c.onPrimary, c.primary), 7.0);
            Need("onPrimary on primaryHover", Contrast(c.onPrimary, c.primaryHover), 7.0);
            Need("onPrimary on primaryPressed", Contrast(c.onPrimary, c.primaryPressed), 7.0);
            // D3: text on ink (selected toggles / chips, the Best pick badge, a coached primary), every state (≥ 7:1).
            Need("onInk on ink", Contrast(c.onInk, c.ink), 7.0);
            Need("onInk on inkHover", Contrast(c.onInk, c.inkHover), 7.0);
            Need("onInk on inkPressed", Contrast(c.onInk, c.inkPressed), 7.0);
            // A4: white on the armed destructive fill.
            Need("onDanger on dangerFill", Contrast(c.onDanger, c.dangerFill), 4.5);
            // Text on the window surface, whatever is behind the window.
            Need("textSecondary on the window", OnStack(c.textSecondary, c.glassRegular), 4.5);
            Need("textPrimary on the window", OnStack(c.textPrimary, c.glassRegular), 4.5);
            Need("ink text on the window (ring label, focus ids)", OnStack(c.ink, c.glassRegular), 4.5);
            // A row on the window, plain and selected (ink wash).
            Need("textSecondary on a row", OnStack(c.textSecondary, c.glassRegular, c.elevatedSolid), 4.5);
            Need("textSecondary on a selected row", OnStack(c.textSecondary, c.glassRegular, UiTheme.Wash(c.elevatedSolid, c.inkWash)), 4.5);
            Need("textPrimary on a selected row", OnStack(c.textPrimary, c.glassRegular, UiTheme.Wash(c.elevatedSolid, c.inkWash)), 4.5);
            return bad;
        }

        /// UX D3 as decided (SPEC §9 "UX D1–D7"; docs/ux/README.md Appendix A): white primary with a near-black label,
        /// tape-yellow ink with its dark text, blue only for information, caution moved off the ink's hue.
        [Test]
        public void D3TokensAreTheDecidedColours()
        {
            var c = new UiTheme.Colors();
            Assert.AreEqual("F5F6F8", Hex(c.primary), "primary");
            Assert.AreEqual("0F1115", Hex(c.onPrimary), "onPrimary");
            Assert.AreEqual("FFD23F", Hex(c.ink), "ink (tape yellow)");
            Assert.AreEqual("17140A", Hex(c.onInk), "onInk");
            Assert.AreEqual("7CC4FF", Hex(c.info), "info");
            Assert.AreEqual("FF9F43", Hex(c.warning), "caution is orange, clear of the ink");
            Assert.AreEqual(1f, c.primary.a); Assert.AreEqual(1f, c.ink.a);
            Assert.That(Contrast(c.onPrimary, c.primary), Is.GreaterThan(17.4), "the 17.5:1 of the decision");
            Assert.That(Contrast(c.onInk, c.ink), Is.GreaterThan(12.7), "the 12.8:1 of the decision");
        }

        /// A4 + D3 on the code tokens (pure maths; runs offline).
        [Test]
        public void LabelsAndTextMeetContrastOnTheCodeTokens()
        {
            var bad = ContrastProblems(new UiTheme.Colors());
            Assert.IsEmpty(bad, string.Join("\n", bad));
        }

        /// A4 + D3 on the theme the app loads (Resources/AirToolsTheme): fails until AirTools ▸ Build UI Assets has
        /// re-saved the asset after a token change.
        [Test]
        public void LabelsOnFilledButtonsAreAtLeast4Point5To1()
        {
            var c = UiTheme.Current.colors;
            var bad = ContrastProblems(c);
            Assert.IsEmpty(bad, string.Join("\n", bad));
            Assert.AreEqual(Hex(new UiTheme.Colors().primary), Hex(c.primary), "built theme is current (Build UI Assets)");
            Assert.AreEqual(Hex(new UiTheme.Colors().warning), Hex(c.warning), "built theme is current (Build UI Assets)");
            Assert.AreEqual(new UiTheme.Colors().glassRegular.a, c.glassRegular.a, 1e-4f, "built theme is current (Build UI Assets)");
        }

        /// Every glyph the copy prints is in the Inter atlases (a missing one falls back to a box).
        [Test]
        public void CopyGlyphsAreInTheAtlas()
        {
            var theme = UiTheme.Current;
            string glyphs = Copy.Glyph(AirTools.Parts.FitStatus.Green) + Copy.Glyph(AirTools.Parts.FitStatus.Amber) + Copy.Glyph(AirTools.Parts.FitStatus.Red)
                            + "·×…′″°²⅛¼⅜½⅝¾⅞“”–" + AirTools.Core.Units.Minus;
            foreach (var w in new[] { Weight.Regular, Weight.Medium, Weight.Semibold })
            {
                var fa = theme.FontAsset(w);
                Assert.IsNotNull(fa, $"{w} font");
                foreach (char ch in glyphs) Assert.IsTrue(fa.characterLookupTable.ContainsKey(ch), $"{fa.name} lacks '{ch}' (U+{(int)ch:X4})");
            }
        }

        // ---------------- A7 / A8: placed from the person ----------------

        /// Degrees below the horizontal eye line (negative above).
        static float BelowEyeDeg(Vector3 eye, Vector3 p)
        {
            var d = p - eye;
            return Mathf.Atan2(-d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
        }

        [TestCase(1.20f)]
        [TestCase(1.42f)]
        [TestCase(1.60f)]
        [TestCase(1.76f)]
        public void EnterControlIsWithinReachAndBelowTheEyes(float eyeHeight)
        {
            var eye = new Vector3(0f, eyeHeight, 0f);
            foreach (float headPitch in new[] { -30f, 0f, 20f })   // looking down / level / up when it's placed
            {
                var fwd = Quaternion.Euler(headPitch, 15f, 0f) * Vector3.forward;
                var pose = HeadAnchor.PoseFor(eye, fwd, 0.45f, 25f);
                float below = BelowEyeDeg(eye, pose.position);
                Assert.That(below, Is.InRange(10f, 45f), $"{below:0.0}° below the eye");
                var shoulder = eye + new Vector3(0f, -0.24f, 0f) + Quaternion.Euler(0f, 15f, 0f) * new Vector3(0.18f, 0f, 0f);
                Assert.LessOrEqual(Vector3.Distance(shoulder, pose.position), 0.55f, "within reach of the shoulder");
                Assert.Greater(Vector3.Dot(pose.rotation * Vector3.forward, (pose.position - eye).normalized), 0.999f, "faces the eyes (pitch + yaw)");
            }
        }

        /// modelwheel (A7 / A8): Model view's wheel is below the eyes (16–34°), straight ahead, its visible arc within 35°
        /// of azimuth, facing the eyes — from a seated to a tall standing eye (pure; the placement is ModelViewLayout).
        [TestCase(1.20f)]
        [TestCase(1.42f)]
        [TestCase(1.60f)]
        [TestCase(1.76f)]
        public void ModelWheelIsBelowTheEyesAndWithinTheAzimuthBand(float eyeHeight)
        {
            var eye = new Vector3(0f, eyeHeight, 0f);
            foreach (var half in new[] { new Vector3(0.33f, 0.08f, 0.2f), new Vector3(0.14f, 0.4f, 0.14f) })
            {
                AirTools.Scene.ModelViewLayout.ModelPose(eye, 15f, Vector3.zero, 1f, half, out var centre);
                float down = AirTools.Scene.ModelViewLayout.WheelDownDeg(AirTools.Scene.ModelViewLayout.ModelBottomDownDeg(eye, 15f, centre, half), 0.066f);
                var frame = AirTools.Scene.ModelViewLayout.WheelPose(eye, 15f, down);
                Assert.That(BelowEyeDeg(eye, frame.position), Is.InRange(15.9f, 34.1f));
                foreach (float deg in new[] { -40f, 0f, 40f })   // the outermost cards as the wheel rests; past them they fade
                {
                    var p = AirTools.UI.WheelMath.ArcPoint(deg * Mathf.Deg2Rad, AirTools.Scene.ModelViewLayout.WheelRadius);
                    var w = frame.position + frame.rotation * new Vector3(p.x + Mathf.Sign(deg) * 0.075f * 0.92f, p.y, 0f);
                    float az = Vector3.SignedAngle(AirTools.Scene.ModelViewLayout.Dir(15f), new Vector3(w.x, 0f, w.z), Vector3.up);
                    Assert.LessOrEqual(Mathf.Abs(az), 35.01f, $"azimuth of the card edge at {deg}°");
                }
                Assert.Greater(Vector3.Dot(frame.rotation * Vector3.forward, (frame.position - eye).normalized), 0.9999f, "faces the eyes");
            }
        }

        [TestCase(0.45f, 20f, 0f)]      // main slot: Notebook / Sellers / Checkout
        [TestCase(0.5f, 18f, 35f)]      // Scene window (right side slot)
        [TestCase(0.5f, 15f, -35f)]     // Find parts (left side slot)
        public void WindowsSitInTheTouchBand(float distance, float down, float yaw)
        {
            var eye = new Vector3(0f, 1.6f, 0f);
            var pose = HeadAnchor.PoseFor(eye, Vector3.forward, distance, down, yaw);
            Assert.That(Vector3.Distance(eye, pose.position), Is.InRange(0.40f, 0.501f));
            Assert.That(BelowEyeDeg(eye, pose.position), Is.InRange(10f, 35f));
            Assert.LessOrEqual(Mathf.Abs(Vector3.SignedAngle(Vector3.forward, Vector3.ProjectOnPlane(pose.position - eye, Vector3.up), Vector3.up)), 35.01f, "azimuth");
        }

        // ---------------- A9: status near the gaze ----------------

        [TestCase(0f)]
        [TestCase(25f)]     // looking up
        [TestCase(-40f)]    // looking down
        public void ToastStaysWithin30DegreesOfTheGaze(float gazePitchUp)
        {
            var eye = new Vector3(0f, 1.6f, 0f);
            var gaze = Quaternion.Euler(-gazePitchUp, 40f, 0f) * Vector3.forward;
            // The toast (0.6 m, 12°) and, since declutter M1, the reply card at the status line's place (0.9 m, 17°).
            foreach (var (distance, below) in new[] { (0.6f, 12f), (0.9f, 17f) })
            {
                var pose = UiToast.PoseFor(eye, gaze, distance, below);
                Assert.LessOrEqual(Vector3.Angle(gaze, pose.position - eye), 30f, $"gaze pitch {gazePitchUp}°, {below}° below");
            }
        }

        /// W0.8: the pill hugs the wrapped text (≤ 26 cm, ≤ 2 lines) — also when the toast had faded out (inactive text).
        [Test]
        public void ToastWrapsAndSizesItsPill()
        {
            var go = new GameObject("toast");
            try
            {
                var toast = go.AddComponent<UiToast>();
                toast.surface = GlassSurface.Create(go.transform, "Pill", new Vector2(0.2f, 0.036f), GlassTier.ElevatedSolid, RadiusRole.Pill);
                toast.text = UiText.Create(go.transform, "Text", "", TypeRole.Label, 0.6f, width: 0.26f);
                toast.text.gameObject.SetActive(false);
                toast.surface.gameObject.SetActive(false);
                toast.Present("Couldn't reach the laptop · nothing was charged · hold Pay to try again when it's back", ColorRole.Danger);
                Assert.IsTrue(toast.text.gameObject.activeSelf);
                Assert.That(toast.surface.size.x, Is.InRange(0.15f, 0.26f + 0.06f), "wrapped at 26 cm");
                Assert.LessOrEqual(toast.text.textInfo.lineCount, 2);
                Assert.Greater(toast.surface.size.y, toast.text.fontSize * 0.1f * 1.5f, "two lines tall");
                toast.Present("✓ Saved · Width 0.26 m", ColorRole.Success);
                Assert.Less(toast.surface.size.x, 0.2f, "a short message sits in a short pill");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ToastTimeGrowsWithTheText()
        {
            Assert.AreEqual(2.4f, UiToast.Duration("✓ Saved · Width 0.26 m", 2.4f, false), 1e-4f);
            Assert.Greater(UiToast.Duration(new string('x', 80), 2.4f, false), 4f);
            Assert.LessOrEqual(UiToast.Duration(new string('x', 400), 2.4f, false), 6f);
            Assert.LessOrEqual(UiToast.Duration(new string('x', 400), 5f, true), 10f);
        }

        // ---------------- A10: one window at a time ----------------

        [Test]
        public void OneWindowAtATime()
        {
            WindowSlot.Reset();
            var a = new GameObject("sellers").AddComponent<FloatingWindow>();
            var b = new GameObject("checkout").AddComponent<FloatingWindow>();
            var side = new GameObject("scene").AddComponent<FloatingWindow>();
            side.mainSlot = false;
            try
            {
                a.Open();
                Assert.IsTrue(a.IsOpen);
                b.Open();
                Assert.IsTrue(b.IsOpen);
                Assert.IsFalse(a.IsOpen, "checkout takes the sellers' slot");
                side.Open();
                Assert.IsTrue(side.IsOpen && b.IsOpen, "side slots don't count");
                a.Open();
                Assert.IsFalse(b.IsOpen);
                Assert.AreEqual(1, new[] { a, b }.Count(w => w.IsOpen));
                a.Close();
                Assert.IsNull(WindowSlot.Current);
            }
            finally { Object.DestroyImmediate(a.gameObject); Object.DestroyImmediate(b.gameObject); Object.DestroyImmediate(side.gameObject); WindowSlot.Reset(); }
        }
    }
}
