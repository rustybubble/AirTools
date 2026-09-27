using System;
using System.Linq;
using System.Text.RegularExpressions;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Structure;
using AirTools.Tools;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// UX decision D2 (SPEC §9): ONE unit on every on-screen label, imperial first, metric one tap away; the notebook and
    /// export keep both. Pure: the Copy hooks, notebook rows and ladder / fall-edge words follow UiSettings.UnitSystem
    /// (set with UseUnits, which never touches PlayerPrefs).
    public class UnitsCopyTests
    {
        const UnitSystem Imp = UnitSystem.Imperial, Met = UnitSystem.Metric;

        [Test]
        public void TheHackGtBuildDefaultsToImperial() => Assert.AreEqual(Imp, UiSettings.DefaultUnits);

        [Test]
        public void UseUnitsSwitchesWithoutSaving()
        {
            int changed = 0;
            void Count() => changed++;
            UiSettings.UseUnits(Met);
            UiSettings.Changed += Count;
            try
            {
                UiSettings.UseUnits(Imp);
                Assert.AreEqual(Imp, UiSettings.UnitSystem);
                Assert.AreEqual(1, changed, "Changed fires (the chip and glass buttons refresh)");
                UiSettings.UseUnits(Imp);
                Assert.AreEqual(1, changed, "no change, no event");
            }
            finally { UiSettings.Changed -= Count; }
        }

        [Test]
        public void CopyHooksShowOneUnit_TheUsersChoice()
        {
            var d = new PartDims { w = 127, d = 38, h = 45 };
            UiSettings.UseUnits(Imp);
            Assert.AreEqual("4′ 11⅛″", Copy.Len(1.5003));
            Assert.AreEqual("4′ 11″ · 1.50 m", Copy.LenFull(1.5));
            Assert.AreEqual("19 ft²", Copy.Area(1.8));
            Assert.AreEqual("19.4 ft² · 1.80 m²", Copy.AreaFull(1.8));
            Assert.AreEqual("1″", Copy.Gap(-25f));
            Assert.AreEqual("10⅜″", Copy.Size(263f));
            Assert.AreEqual("23¼–39⅜″", Copy.Range(590f, 1000f));
            Assert.AreEqual("≥ 23¼″", Copy.Range(590f, null));
            Assert.AreEqual("2.1 oz", Copy.Weight(60f));
            Assert.AreEqual("inches", Copy.KeypadUnit);
            Assert.AreEqual(0.6096, Copy.KeypadMetres(24), 1e-9, "a 24″ dishwasher");
            Assert.AreEqual("10⅜ × 11″", Copy.WxH(0.262, 0.279));
            Assert.AreEqual("5 × 1½ × 1¾ in", Copy.Dims(d));
            Assert.AreEqual("127 × 38 × 45 mm", Copy.DimsOther(d), "the listing's other unit (spec card, quiet line)");
            Assert.AreEqual("Can't pay: needs 1½″ clear", Copy.MandateBlocked("needs 38 mm clear"));

            UiSettings.UseUnits(Met);   // metric = exactly the pre-D2 strings
            Assert.AreEqual("1.50 m", Copy.Len(1.5003));
            Assert.AreEqual("1.50 m · 4′ 11″", Copy.LenFull(1.5));
            Assert.AreEqual("1.80 m²", Copy.Area(1.8));
            Assert.AreEqual("1.80 m² · 19.4 ft²", Copy.AreaFull(1.8));
            Assert.AreEqual("25 mm", Copy.Gap(-25f));
            Assert.AreEqual("263 mm", Copy.Size(263f));
            Assert.AreEqual("590–1000 mm", Copy.Range(590f, 1000f));
            Assert.AreEqual("60 g", Copy.Weight(60f));
            Assert.AreEqual("cm", Copy.KeypadUnit);
            Assert.AreEqual(0.6096, Copy.KeypadMetres(60.96), 1e-9);
            Assert.AreEqual("262 × 279 mm", Copy.WxH(0.262, 0.279));
            Assert.AreEqual("127 × 38 × 45 mm", Copy.Dims(d));
            Assert.AreEqual("5 × 1½ × 1¾ in", Copy.DimsOther(d));
        }

        [Test]
        public void NotebookKeepsBothUnits_ToastsShowOne_TheRawLabelNeverChanges()
        {
            var width = new NotebookEntry("measure", 1.0, "m", new[] { Vector3.zero, new Vector3(1f, 0f, 0f) }, DateTime.Now, -1, "Distance 1.00 m · 3′ 3⅜″");
            var rect = new NotebookEntry("measure", 0.09, "m²", new Vector3[4], DateTime.Now, -1, "Quad …")
            { Sides = new[] { 0.3, 0.3, 0.3, 0.3 }, Angles = new[] { 90.0, 90.0, 90.0, 90.0 } };
            var tri = new NotebookEntry("measure", 1.8, "m²", new Vector3[3], DateTime.Now, -1, "Triangle …") { Sides = new[] { 2.0, 2.0, 2.8 }, Angles = new[] { 45.0, 45.0, 90.0 } };

            UiSettings.UseUnits(Imp);
            Assert.AreEqual("Width · 3′ 3⅜″ · 1.00 m", NotebookRow.RowTitle(width), "the notebook keeps both, the user's first");
            Assert.LessOrEqual(NotebookRow.RowTitle(width).Length, 34);
            Assert.AreEqual("✓ Saved · Width 3′ 3⅜″", NotebookRow.SavedToast(width));
            Assert.AreEqual("11¾″ × 11¾″", NotebookRow.Value(rect));
            Assert.AreEqual("✓ Saved · Rectangle 1.0 ft²", NotebookRow.SavedToast(rect));
            Assert.AreEqual("19.4 ft² · 1.80 m²", NotebookRow.Value(tri));
            Assert.AreEqual("✓ Saved · Triangle 19 ft²", NotebookRow.SavedToast(tri));

            UiSettings.UseUnits(Met);
            Assert.AreEqual("Width · 1.00 m · 3′ 3⅜″", NotebookRow.RowTitle(width));
            Assert.AreEqual("✓ Saved · Width 1.00 m", NotebookRow.SavedToast(width));
            Assert.AreEqual("1.80 m² · 19.4 ft²", NotebookRow.Value(tri));
            Assert.AreEqual("Distance 1.00 m · 3′ 3⅜″", width.Label, "export / logs / hcheck read the raw label");
        }

        [Test]
        public void LadderAndFallEdgeWordsFollowTheUnit()
        {
            var s = LadderMath.Solve(SyntheticFacadeSpec.FasciaTop, LadderSupport.Landing);
            var r = LadderMath.Evaluate(s, LadderChecks.Clear);
            var p = new LadderPlacement { Solution = s, Report = r };
            UiSettings.UseUnits(Imp);
            string imp = LadderTool.LabelFor(p);
            StringAssert.StartsWith($"✓ 28 ft extension ladder\nfoot {Units.FormatPrimary(s.FootOut, Imp)} out · 75.5° · ", imp, "ladder sizes stay in feet");
            StringAssert.StartsWith("✓ 28 ft extension ladder\nfoot 5′ 3", imp);
            StringAssert.Contains($" · {Units.FormatPrimary(s.AboveEdge, Imp)} above the edge", imp);
            StringAssert.DoesNotContain(" m ", imp.Replace("\n", " ") + " ");
            Assert.AreEqual("6 ft+ edge · fall protection", FallEdgeMath.LabelFor(Imp));
            Assert.AreEqual("20′ 4⅛″ drop · fall protection", FallEdgeMath.DropLabel(6.2f, Imp));
            UiSettings.UseUnits(Met);
            StringAssert.StartsWith("✓ 28 ft extension ladder\nfoot 1.60 m out · 75.5° · 0.91 m above the edge", LadderTool.LabelFor(p));
            Assert.AreEqual(FallEdgeMath.LabelText, FallEdgeMath.LabelFor(Met), "metric = the raw label");
            Assert.AreEqual(FallEdgeMath.DropLabel(6.2f), FallEdgeMath.DropLabel(6.2f, Met));
            // The harness line stays metric whatever the chip says.
            UiSettings.UseUnits(Imp);
            Assert.AreEqual("28 ft extension ladder · foot 1.60 m out · 75.5° · 0.91 m above the edge", LadderMath.Label(s));
        }

        [Test]
        public void TheChipSaysWhichUnit()
        {
            Assert.AreEqual("Units · ft·in", UnitsChip.Label(Imp));
            Assert.AreEqual("Units · m", UnitsChip.Label(Met));
            foreach (char ch in UnitsChip.Label(Imp) + UnitsChip.Label(Met))
                Assert.IsTrue(AirTools.Editor.UiAssetsBuilder.Charset.IndexOf(ch) >= 0, $"'{ch}' is not in the charset");
        }
    }

    /// Engine side (Editor gate: MeasureView / TMP / physics): the chip toggles and saves the preference, and what's drawn
    /// re-labels in place — no re-measure, same ValueSI, same raw Label.
    public class UnitsSwitchTests : FacadeToolFixture
    {
        static readonly Vector3 LeftJamb = new Vector3(-0.75f, 4.1f, -0.05f);
        static readonly Vector3 RightJamb = new Vector3(0.75f, 4.1f, -0.05f);
        bool m_HadKey;
        int m_SavedUnits;

        [SetUp]
        public void SaveThePreference()
        {
            m_HadKey = PlayerPrefs.HasKey(UiSettings.KeyUnits);
            m_SavedUnits = PlayerPrefs.GetInt(UiSettings.KeyUnits, (int)UiSettings.DefaultUnits);
            Services.Register(Tool);   // OnEnable doesn't run in EditMode: UnitsSwitch finds the tool through Services
        }

        [TearDown]
        public void RestoreThePreference()
        {
            Services.Unregister(Tool);
            if (m_HadKey) PlayerPrefs.SetInt(UiSettings.KeyUnits, m_SavedUnits); else PlayerPrefs.DeleteKey(UiSettings.KeyUnits);
            UiSettings.ResetCache();
        }

        static void Invoke(object o, string method) =>
            o.GetType().GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(o, null);

        [Test]
        public void TheChipTogglesAndSaves_AndTheTapeRelabelsInPlace()
        {
            UiSettings.UseUnits(UnitSystem.Imperial);   // the HackGT default
            Click(LeftJamb);
            Click(RightJamb);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            var shape = Tool.Shapes[0];
            var e = shape.Entry;
            string raw = e.Label;
            double v = e.ValueSI;
            Assert.AreEqual(1.5, v, 0.001);
            StringAssert.StartsWith("Distance 1.50 m · ", raw, "the raw label stays dual, metric first");
            Assert.AreEqual(1, shape.View.ActiveLabelCount);
            Assert.AreEqual(Units.FormatPrimary(v, UnitSystem.Imperial), shape.View.Labels[0].Text, "one unit: feet and inches");
            StringAssert.StartsWith("4′ 11", shape.View.Labels[0].Text);

            var go = new GameObject("units-chip");
            var b = go.AddComponent<GlassButton>();
            b.style = ButtonStyle.Chip;
            b.cooldownSeconds = 0f;
            var chip = go.AddComponent<UnitsChip>();
            chip.button = b;
            Invoke(chip, "OnEnable");   // OnEnable doesn't run in EditMode
            try
            {
                Assert.AreEqual("Units · ft·in", b.Text);
                Assert.IsTrue(b.Press(), "a poke / ray pinch on the chip");
                Assert.AreEqual(UnitSystem.Metric, UiSettings.UnitSystem);
                Assert.AreEqual((int)UnitSystem.Metric, PlayerPrefs.GetInt(UiSettings.KeyUnits, -1), "saved on this headset");
                Assert.AreEqual("Units · m", b.Text);
                Assert.AreEqual("1.50 m", shape.View.Labels[0].Text, "re-labelled in place");
                Assert.AreSame(shape, Tool.Shapes[0]);
                Assert.AreEqual(1, Notebook.Entries.Count, "nothing re-measured or re-logged");
                Assert.AreEqual(v, e.ValueSI);
                Assert.AreEqual(raw, e.Label);

                Assert.IsTrue(b.Press());
                Assert.AreEqual(UnitSystem.Imperial, UiSettings.UnitSystem);
                Assert.AreEqual((int)UnitSystem.Imperial, PlayerPrefs.GetInt(UiSettings.KeyUnits, -1));
                Assert.AreEqual(Units.FormatPrimary(v, UnitSystem.Imperial), shape.View.Labels[0].Text);
                Assert.AreEqual("Units · ft·in", b.Text);
            }
            finally
            {
                Invoke(chip, "OnDisable");
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ShapesAndSurveyObjects_UndoneOnesToo_RelabelWithoutReMeasuring()
        {
            UiSettings.UseUnits(UnitSystem.Metric);
            Tool.AreaMode = true;   // D4 (SPEC §9): Line mode saves at point 2; a rectangle is drawn in Area mode
            // A rectangle by hand: 4 sides + 4 angles + area.
            var eye = new Vector3(0, 4.1f, 2.5f);
            Click(eye, new Vector3(-0.74f, 4.69f, -0.1f));
            Click(eye, new Vector3(0.74f, 4.69f, -0.1f));
            Click(eye, new Vector3(0.74f, 3.51f, -0.1f));
            Click(eye, new Vector3(-0.74f, 3.51f, -0.1f));
            Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            var quad = Tool.Shapes[0];
            string Texts(MeasureShape s) => string.Join(" | ", s.View.Labels.Take(s.View.ActiveLabelCount).Select(l => l.Text));
            StringAssert.Contains("1.80 m²", Texts(quad));
            // A survey object (B1): one compact W × H label and its notebook row; then undo it.
            foreach (var p in new[] { new Vector3(-3.8f, 1.6f, 0f), new Vector3(-3.5f, 1.6f, 0f), new Vector3(-3.5f, 1.85f, 0f), new Vector3(-3.8f, 1.85f, 0f) })
                Tool.Click(new SurfaceHit { point = p, kind = SnapKind.Corner });
            var survey = Tool.Finish(new SurveyTag { RequestId = "sv-d2", ObjectId = "o0", Label = "panel" });
            Assert.AreEqual("300 × 250 mm", survey.Entry.DisplayValue);
            StringAssert.Contains("300 × 250 mm", survey.View.Labels[0].Text);
            string surveyRaw = survey.Entry.Label;
            Tool.Undo();
            Assert.AreEqual(1, Tool.Shapes.Count, "the survey object is undone (hidden, kept for redo)");

            UnitsSwitch.Use(UnitSystem.Imperial);   // this session only; relabels through Services
            string imp = Texts(quad);
            StringAssert.Contains("19 ft²", imp);
            StringAssert.Contains("4′ 11", imp, "the 1.50 m sides");
            StringAssert.Contains("3′ 11", imp, "the 1.20 m sides");
            StringAssert.DoesNotContain(" m", imp);
            Assert.AreEqual("11¾ × 9⅞″", survey.Entry.DisplayValue, "the undone row too");
            Assert.AreEqual(surveyRaw, survey.Entry.Label, "raw: still mm");
            StringAssert.Contains("300 × 250 mm", survey.Entry.Label);
            Tool.Redo();
            Assert.AreEqual(2, Tool.Shapes.Count);
            StringAssert.Contains("11¾ × 9⅞″", survey.View.Labels[0].Text, "redo shows the new unit");
        }

        [Test]
        public void SurveyCardAndEvidenceRows_InTheUsersUnit()
        {
            var args = JObject.Parse(@"{""request_id"": ""sv-0c45d47f"", ""label"": ""cabinet_door"",
               ""groups"": [{""w_mm"": 262, ""h_mm"": 278, ""count"": 2, ""ids"": [""o0"", ""o1""]},
                          {""w_mm"": 208, ""h_mm"": 525, ""count"": 1, ""ids"": [""o6""]}],
               ""unverified"": [], ""skipped"": [], ""focus"": [""o6""]}");
            var m = new ReceiptMandate
            {
                cart = new MandateCart { hash = "abc3a72", total_usd = 34.16f },
                authorization = new MandateAuthorization { mode = "offline" },
                evidence = { new CheckoutEvidence { label = "tape #3", value_m = 4.2f, array = new EvidenceArray { count = 8, spacing_mm = 600 }, photo = "p.jpg" } },
            };
            var check = new CheckoutCheck { id = "qty_evidence", status = "ok", detail = "tape #3 4.20 m ÷ 600 mm → 8 (reported by headset)" };
            string Plain(string s) => Regex.Replace(s, "<[^>]*>", "");

            UiSettings.UseUnits(UnitSystem.Imperial);
            var rows = Plain(SurveyCard.Compose(args).body).Split('\n');
            StringAssert.StartsWith("2 × 10⅜ × 11″", rows[0]);
            StringAssert.StartsWith("→ 1 × 8¼ × 20⅝″", rows[1]);
            StringAssert.StartsWith("✓ Evidence · tape #3 13′ 9⅜″ → 8 · photo", Plain(MandateText.Chain(m)).Split('\n')[3]);
            Assert.AreEqual("✓ Quantity · tape #3 13′ 9⅜″ ÷ 1′ 11⅝″ → 8 (reported by headset)", Plain(MandateText.Row(check)));

            UiSettings.UseUnits(UnitSystem.Metric);
            StringAssert.StartsWith("2 × 262 × 278 mm", Plain(SurveyCard.Compose(args).body).Split('\n')[0]);
            StringAssert.StartsWith("✓ Evidence · tape #3 4.20 m → 8 · photo", Plain(MandateText.Chain(m)).Split('\n')[3]);
            Assert.AreEqual("✓ Quantity · tape #3 4.20 m ÷ 600 mm → 8 (reported by headset)", Plain(MandateText.Row(check)));
        }
    }
}
