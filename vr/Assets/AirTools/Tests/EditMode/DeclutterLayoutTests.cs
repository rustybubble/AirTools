using System.Collections.Generic;
using System.Linq;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Parts;
using AirTools.Tools;
using AirTools.UI;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AirTools.Tests
{
    /// De-clutter lane B (docs/ux/declutter.md S4, S7, S9): the pure parts of the layout moves, so they also run in the
    /// offline runner. The scene checks (Main.unity) are in DeclutterLayoutSceneTests and ToolRingTests.
    public class DeclutterLayoutTests
    {
        // ---------------- S4 (M4, DC4): the coach card takes the left side slot; Find parts yields ----------------

        [Test]
        public void FindPartsYieldsToTheCoachCardAndToSellersOrCheckout()
        {
            object sellers = new object(), checkout = new object(), notebook = new object();
            Assert.IsFalse(PartsBrowser.YieldsTo(false, null, sellers, checkout), "nothing else up: Find parts stays");
            Assert.IsTrue(PartsBrowser.YieldsTo(true, null, sellers, checkout), "the coach card takes the left side slot");
            Assert.IsTrue(PartsBrowser.YieldsTo(false, sellers, sellers, checkout), "Sellers in the main slot");
            Assert.IsTrue(PartsBrowser.YieldsTo(false, checkout, sellers, checkout), "Checkout in the main slot");
            Assert.IsFalse(PartsBrowser.YieldsTo(false, notebook, sellers, checkout), "any other card leaves the side slot alone");
            Assert.IsFalse(PartsBrowser.YieldsTo(false, null, null, null), "no panels built");
            Assert.IsTrue(PartsBrowser.YieldsTo(true, notebook, sellers, checkout));
        }

        // ---------------- S7 (M6, DC3): ring v2 ----------------

        [Test]
        public void RingV2_ThePlusMinus120ItemsStayReadable_The180OneIsHidden()
        {
            const float visible = 128f, band = 12f;   // ToolRing's ring v2 defaults
            Assert.AreEqual(1f, ToolRing.Fade(0f, visible, band), 1e-5f, "the lens");
            Assert.AreEqual(1f, ToolRing.Fade(60f, visible, band), 1e-5f, "±60°");
            float f120 = ToolRing.Fade(120f, visible, band);
            Assert.That(f120, Is.InRange(0.7f, 0.8f), "±120° (74 % with a 12° band)");
            Assert.AreEqual(0f, ToolRing.Fade(128f, visible, band), 1e-5f);
            Assert.AreEqual(0f, ToolRing.Fade(180f, visible, band), 1e-5f, "the item one detent away from the back");
            Assert.Less(ToolRing.Fade(120f, 128f, 28f), 0.25f, "why the band had to shrink (20 % with the old 28° band)");
        }

        /// RunDemo's ring.level beat: a −4 rad/s flick from Measure coasts one item on and settles on Level (6 items).
        [TestCase(1f / 60f)]
        [TestCase(1f / 72f)]
        [TestCase(1f / 90f)]
        public void RingV2_AFlickFromMeasureSettlesOnLevel(float dt)
        {
            var d = new DialPhysics(6, 1);   // Move · Measure · Level · Notebook · Model view · Settings, Measure on the lens
            d.BeginDrag();
            for (int k = 0; k < 3; k++) d.Drag(-4f * 0.02f, 0.02f);   // ToolRing.Flick(-4)
            d.EndDrag();
            for (float t = 0f; t < 4f; t += dt) d.Step(dt);
            Assert.IsTrue(d.Settled);
            Assert.AreEqual(2, d.Selected, "Level");
        }

        // ---------------- S9 (M9, DC5): one wrist strip; the credit ahead for 8 s ----------------

        const string ZabelCredit = "\"Haus Schiller – Zabelgymnasium Gera – Drohnenflug\" by zabelgymnasium, CC BY 3.0, via Wikimedia Commons";

        [Test]
        public void TheHeadCreditShowsForEightSecondsThenLeaves()
        {
            const float s = 8f;
            Assert.IsTrue(AirTools.Scene.SceneCreditChip.HeadVisible(100.0, 100.0, s), "as the scan becomes visible");
            Assert.IsTrue(AirTools.Scene.SceneCreditChip.HeadVisible(100.0, 104.0, s));
            Assert.IsTrue(AirTools.Scene.SceneCreditChip.HeadVisible(100.0, 108.0, s), "up to 8 s");
            Assert.IsFalse(AirTools.Scene.SceneCreditChip.HeadVisible(100.0, 109.0, s), "gone at 9 s");
            Assert.IsFalse(AirTools.Scene.SceneCreditChip.HeadVisible(-1.0, 5.0, s), "no credit due");
            Assert.IsFalse(AirTools.Scene.SceneCreditChip.HeadVisible(100.0, 99.0, s));
        }

        [Test]
        public void TheWristStripCarriesLimitsScaleAndTheCreditVerbatim()
        {
            // The credit, verbatim, for the Zabel scan only (cached per site string).
            Assert.AreEqual(ZabelCredit, AirTools.Scene.SceneCredits.Zabel);
            Assert.AreEqual(ZabelCredit, AirTools.Scene.SceneCreditChip.CreditFor("zabel-gymnasium"));
            Assert.IsNull(AirTools.Scene.SceneCreditChip.CreditFor("kitchen"));
            Assert.AreEqual(ZabelCredit, AirTools.Scene.SceneCreditChip.CreditFor("zabel-gymnasium"), "back again");
            Assert.IsNull(AirTools.Scene.SceneCreditChip.CreditFor(null));

            // The scale state (ScenePanel.ScaleChip's rule, in one word).
            Assert.IsNull(LimitsChip.ScaleWord(false, false, 1f), "nothing loaded");
            Assert.AreEqual(LimitsChip.ScaleNotSet, LimitsChip.ScaleWord(true, true, 1f), "a scan before Set scale");
            Assert.AreEqual(LimitsChip.ScaleSet, LimitsChip.ScaleWord(true, true, 1.49f), "a scan after Set scale");
            Assert.AreEqual(LimitsChip.ScaleSet, LimitsChip.ScaleWord(true, false, 1f), "the built-in facade is built to size");

            // Line 1: limits, then the scale.
            Assert.AreEqual("≤ $40 · by Fri · fastest · Scale ✓", LimitsChip.FirstLine("≤ $40 · by Fri · fastest", false, "Scale ✓"));
            Assert.AreEqual("≤ $20 · raise it on the panel", LimitsChip.FirstLine("≤ $20", true, "Scale not set"), "never \"Scale not set\" on the strip (user 09-27)");
            Assert.AreEqual("", LimitsChip.FirstLine(null, false, "Scale not set"));
            Assert.AreEqual("", LimitsChip.FirstLine(null, false, null));

            // While a Zabel scan is loaded the wrist text carries the credit under line 1.
            string wrist = LimitsChip.Compose(LimitsChip.FirstLine(null, false, LimitsChip.ScaleWord(true, true, 1f)),
                AirTools.Scene.SceneCreditChip.CreditFor("zabel-gymnasium"));
            StringAssert.Contains("CC BY 3.0", wrist);
            Assert.AreEqual(ZabelCredit, wrist, "the credit only");
            Assert.AreEqual("≤ $40", LimitsChip.Compose("≤ $40", null));
            Assert.AreEqual(ZabelCredit, LimitsChip.Compose("", ZabelCredit));

            // It shows in the world with limits or a credit; the scale alone isn't a reason; never in passthrough.
            Assert.IsFalse(LimitsChip.HasSomething(false, null, false));
            Assert.IsTrue(LimitsChip.HasSomething(true, null, false));
            Assert.IsTrue(LimitsChip.HasSomething(false, ZabelCredit, false));
            Assert.IsFalse(LimitsChip.HasSomething(true, ZabelCredit, true));
        }
    }

    /// De-clutter lane B, scene checks on Main.unity (run AirTools ▸ Wire Main Scene first; Unity only).
    public class DeclutterLayoutSceneTests
    {
        /// Controls that sit on a title's line today although declutter M8 says title rows hold only the title and Close.
        /// Checkout's quantity stepper (lane A's panel) is the one left; Settings' Labels toggle moved to its Layers row.
        static readonly HashSet<string> KnownTitleRowControls = new HashSet<string> { "CheckoutPanel/QtyMinus", "CheckoutPanel/QtyPlus" };

        [Test]
        public void TitleRowsHoldOnlyTitleAndClose()
        {
            var scene = EditorSceneManager.OpenScene(MainSceneBuilder.ScenePath, OpenSceneMode.Additive);
            try
            {
                var offenders = new List<string>();
                int windows = 0;
                foreach (var root in scene.GetRootGameObjects())
                foreach (var w in root.GetComponentsInChildren<FloatingWindow>(true))
                {
                    if (w.content == null) continue;
                    var content = w.content.transform;
                    var title = content.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Title");
                    if (title == null) continue;
                    float ty = content.InverseTransformPoint(title.position).y;
                    if (title.localPosition == Vector3.zero) continue;   // laid out at runtime (the coach card)
                    windows++;
                    foreach (var b in content.GetComponentsInChildren<GlassButton>(true))
                    {
                        float y = content.InverseTransformPoint(b.transform.position).y;
                        if (Mathf.Abs(y - ty) > 0.012f || b.name == "Close") continue;
                        // G3's card rows (and their link chips) are laid out at runtime by GrokCardLayout; where the builder
                        // leaves them says nothing about the shown card.
                        if (b.GetComponentInParent<AirTools.Agent.Grok.GrokCardRow>(true) != null) continue;
                        string id = $"{w.name}/{b.name}";
                        if (!KnownTitleRowControls.Contains(id)) offenders.Add($"{id} \"{b.Text}\" on the title's line");
                    }
                }
                Assert.GreaterOrEqual(windows, 5, "walked the windows");
                Assert.IsEmpty(offenders, string.Join("\n", offenders));
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        /// M7: Settings (the Scene window, renamed) holds what left the ring — Home, Ladder, Exit world (asks twice) — and the
        /// Layers row (Show edges · Labels · Fall edges); the panel grew by one row.
        [Test]
        public void SettingsHoldsHomeLadderExitAndTheLayersRow()
        {
            var scene = EditorSceneManager.OpenScene(MainSceneBuilder.ScenePath, OpenSceneMode.Additive);
            try
            {
                var settings = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<AirTools.Structure.ScenePanel>(true)).FirstOrDefault();
                Assert.IsNotNull(settings, "Settings (ScenePanel)");
                Assert.AreEqual(AirTools.Structure.ScenePanel.Title, settings.title.text);
                Assert.IsFalse(settings.window.mainSlot, "a side slot");
                Assert.AreEqual(35f, settings.window.yawDeg, 1e-3f, "the right side slot");
                foreach (var b in new[] { settings.home, settings.ladder, settings.exitWorld, settings.fallEdges, settings.structure, settings.snapping })
                    Assert.IsNotNull(b);
                Assert.IsTrue(settings.exitWorld.confirm, "Exit world asks twice");
                var content = settings.window.content.transform;
                var labels = content.Find("GrokLabels");
                Assert.IsNotNull(labels, "the scan labels toggle (GrokOverlaysBuilder)");
                float layersY = settings.fallEdges.transform.localPosition.y;
                Assert.AreEqual(layersY, settings.structure.transform.localPosition.y, 1e-4f);
                Assert.AreEqual(layersY, labels.localPosition.y, 1e-4f, "Labels sits in the Layers row");
                // Scene parts: Take out is Settings' last row (ScenePartsBuilder), one chip per removable part.
                var partsRow = content.GetComponentInChildren<AirTools.Structure.ScenePartsRow>(true);
                Assert.IsNotNull(partsRow, "Settings ▸ Take out (ScenePartsBuilder)");
                Assert.AreEqual(ScenePartsBuilder.Chips, partsRow.chips.Length);
                Assert.Less(partsRow.transform.localPosition.y, settings.home.transform.localPosition.y, "below the other rows");
                // scalemodels: the settings rows first (snapping, contrast, less motion, scale, units), the scene actions below.
                Assert.Greater(settings.snapping.transform.localPosition.y, settings.structure.transform.localPosition.y, "settings above the layers");
                Assert.Greater(settings.structure.transform.localPosition.y, settings.home.transform.localPosition.y, "layers above Home / Ladder / Exit world");
                var units = content.GetComponentInChildren<UnitsChip>(true);
                if (units != null) Assert.Greater(units.transform.localPosition.y, settings.home.transform.localPosition.y, "units above the scene actions");
                var panel = content.Find("Panel").GetComponent<GlassSurface>();
                Assert.AreEqual(MainSceneBuilder.SettingsPanelHeight, panel.size.y, 1e-4f);
                foreach (var b in content.GetComponentsInChildren<GlassButton>(true))
                {
                    float y = content.InverseTransformPoint(b.transform.position).y;
                    Assert.That(y, Is.InRange(-panel.size.y * 0.5f + 0.012f, panel.size.y * 0.5f - 0.012f), $"{b.name} inside the panel");
                }
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
