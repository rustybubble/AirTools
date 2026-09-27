using System.Collections.Generic;
using System.Linq;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Editor;
using AirTools.Parts;
using AirTools.Structure;
using AirTools.Tools;
using AirTools.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AirTools.Tests
{
    /// settings-assets, the Editor gate (Unity: scenes, transforms): Main.unity has Settings' Talk and 3D-models rows and
    /// the card's Compare chip; AgentContext.Build carries asset_mode; the model swap keeps a placed part's anchor
    /// (PlacementEditor.SwapModelInPlace, with catalog parts on the built-in facade), for a part moved in Adjust too; the
    /// switcher leaves catalog parts alone. The pure half is SettingsAssetsTests.
    public class SettingsAssetsGateTests : FacadeToolFixture
    {
        static PartCatalog s_Catalog;
        static readonly Vector3 Spot = new Vector3(0.8f, 1.2f, 2.5f);
        GameObject m_Go;
        PartTool m_Parts;
        PartLoader m_Loader;
        PlacementEditor m_Editor;

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
            UserPrefs.PersistOverride = () => false;   // the Editor's saved choices stay out
            UserPrefs.ResetCache();
            m_Go = new GameObject("settings-assets-test");
            m_Loader = m_Go.AddComponent<PartLoader>();
            m_Loader.catalog = s_Catalog;
            m_Parts = m_Go.AddComponent<PartTool>();
            m_Parts.SetInput(Hub);
            Services.Register(m_Parts);
            EditHistory.Register(m_Parts);
            m_Editor = m_Go.AddComponent<PlacementEditor>();
            m_Editor.tool = m_Parts;
            m_Editor.loader = m_Loader;
            m_Editor.SyncCatalogLoads = true;
            m_Editor.Attach();
            m_Editor.SetInput(Hub);
        }

        [TearDown]
        public void DestroyEditor()
        {
            m_Editor.Detach();
            m_Parts.ClearAll();
            Services.Unregister(m_Parts);
            EditHistory.Unregister(m_Parts);
            foreach (var p in Object.FindObjectsByType<PartInstance>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Object.DestroyImmediate(p.gameObject);
            Object.DestroyImmediate(m_Go);
            Physics.SyncTransforms();
            PlacementFocus.Instance.Set(false);
            UserPrefs.PersistOverride = null;
            UserPrefs.ResetCache();
        }

        PartInstance Place(string id, Vector3 at, float yawDeg = 180f)
        {
            var p = m_Loader.LoadFromCatalog(id);
            Assert.IsNotNull(p, $"catalog has {id}");
            m_Parts.PlaceAt(p, at, PlacementMath.AxisAngle(Vector3.up, yawDeg));
            Physics.SyncTransforms();
            return p;
        }

        static GameObject OtherModel(Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);   // another shape, another size: fitted to the dims
            go.name = "hf-model";
            go.transform.localScale = size;
            return go;
        }

        static void Same(Vector3 a, Vector3 b, string what, float tol = 1e-4f) =>
            Assert.That(Vector3.Distance(a, b), Is.LessThan(tol), $"{what}: {a.ToString("F4")} vs {b.ToString("F4")}");

        // ---------------- the swap keeps the anchor ----------------

        [Test]
        public void TheModelSwap_KeepsThePlacementAnchor()
        {
            var p = Place("window-ac-small", Spot);
            Assert.IsTrue(m_Editor.TryAnchor(p, out var a0, out var r0));
            var box0 = p.LocalBox;
            var pos0 = p.transform.position;
            // Another shape at 2.5× the listed size (a server GLB is metres at size; the part fits it uniformly anyway).
            int n = m_Editor.SwapModelInPlace(new[] { p }, OtherModel(p.Spec.dims_mm.Metres * 2.5f), null, "hf");
            Assert.AreEqual(1, n);
            Assert.IsTrue(m_Editor.TryAnchor(p, out var a1, out var r1));
            Same(a0, a1, "the anchor");
            Assert.That(Quaternion.Angle(r0, r1), Is.LessThan(0.01f), "the turn");
            Same(pos0, p.transform.position, "the root");
            Assert.AreEqual(box0, p.LocalBox, "the true-size box");
            Assert.AreEqual("hf-model", p.Model.GetChild(0).name, "the new model is in");
            Assert.IsTrue(m_Parts.PlacedParts.Contains(p), "still placed");
            var measured = p.MeasuredSizeMm();
            Assert.That(measured.x / p.Spec.dims_mm.w, Is.EqualTo(1f).Within(0.02f), "fitted to the listing's width");
            Assert.That(measured.y / p.Spec.dims_mm.h, Is.EqualTo(1f).Within(0.02f), "… height");
            Assert.That(measured.z / p.Spec.dims_mm.d, Is.EqualTo(1f).Within(0.02f), "… depth");
            StringAssert.Contains("kept at their anchor", m_Editor.LastSwap);
        }

        [Test]
        public void AnAdjustedPart_KeepsItsNewSpot_ThroughTheSwap()
        {
            var p = Place("window-ac-small", Spot);
            Assert.IsTrue(AppCommands.AdjustPlacement(true), m_Editor.LastAction);
            Assert.IsTrue(m_Editor.NudgeStep(0));    // ⅜″ right
            Assert.IsTrue(m_Editor.RotateStep(0));   // turn 5°
            Assert.IsTrue(m_Editor.TryAnchor(p, out var a0, out var r0));
            m_Editor.SwapModelInPlace(new[] { p }, OtherModel(Vector3.one), null, "llm_scad");
            Assert.IsTrue(m_Editor.TryAnchor(p, out var a1, out var r1));
            Same(a0, a1, "the adjusted anchor");
            Assert.That(Quaternion.Angle(r0, r1), Is.LessThan(0.01f));
            Assert.AreSame(p, m_Editor.Adjusting, "still adjusting the same part");
            AppCommands.AdjustPlacement(false);
        }

        [Test]
        public void TheSwap_KeepsAListedFinishTint_AndIsNotAnUndoStep()
        {
            var p = Place("window-ac-small", Spot);
            var finish = p.Spec.finishes.FirstOrDefault();
            if (finish != null) Assert.IsTrue(p.SetFinish(finish.name));
            int undo = m_Editor.UndoCount;
            m_Editor.SwapModelInPlace(new[] { p }, OtherModel(Vector3.one), null, "hf");
            Assert.AreEqual(undo, m_Editor.UndoCount, "a view setting, not an edit");
            if (finish != null) Assert.AreEqual(finish.Color, p.MaterialColor(), "the tint is back on the new model");
        }

        [Test]
        public void TheSwitcher_LeavesCatalogPartsAlone()
        {
            var p = Place("window-ac-small", Spot);
            Assert.IsFalse(AssetModeSwitcher.IsServerPart(p), "a catalog part has one model");
            var sw = m_Go.AddComponent<AssetModeSwitcher>();
            sw.tool = m_Parts; sw.loader = m_Loader; sw.editor = m_Editor;
            Assert.IsFalse(sw.Compare(p));
            Assert.IsEmpty(sw.ServerParts());
            Assert.AreEqual(0, sw.RebuildAll(AssetMode.Hf));
        }

        // ---------------- the context ----------------

        [Test]
        public void EveryContext_CarriesTheAssetMode()
        {
            UserPrefs.Use(TalkStyle.Toggle, AssetMode.Hf);
            Assert.AreEqual("hf", AgentContext.Build()["asset_mode"]);
            UserPrefs.Use(TalkStyle.Toggle, AssetMode.LlmScad);
            Assert.AreEqual("llm_scad", AgentContext.Build(null, "find a dishwasher")["asset_mode"]);
        }

        // ---------------- Main.unity ----------------

        [Test]
        public void Settings_HasTheTalkAndModelsRows_TheCardHasCompare()
        {
            var scene = EditorSceneManager.OpenScene(MainSceneBuilder.ScenePath, OpenSceneMode.Additive);
            try
            {
                var roots = scene.GetRootGameObjects();
                var settings = roots.SelectMany(r => r.GetComponentsInChildren<ScenePanel>(true)).FirstOrDefault();
                Assert.IsNotNull(settings, "Settings");
                var rows = settings.window.content.GetComponentInChildren<SettingsPrefsRows>(true);
                Assert.IsNotNull(rows, "Settings ▸ Talk / 3D models (run AirTools ▸ Wire Main Scene)");
                CollectionAssert.AreEqual(new[] { "Hold", "Toggle", "Auto" }, rows.talk.Select(b => b.Text).ToArray());
                CollectionAssert.AreEqual(new[] { "HF", "LLM+CAD", "Auto" }, rows.models.Select(b => b.Text).ToArray());
                Assert.IsTrue(rows.talk.Concat(rows.models).All(b => b.style == ButtonStyle.Chip), "segmented chips");
                var partsRow = settings.window.content.GetComponentInChildren<ScenePartsRow>(true);
                Assert.Less(rows.transform.localPosition.y, partsRow.transform.localPosition.y, "under Take out: the last rows");
                Assert.AreEqual(MainSceneBuilder.SettingsPanelHeight, UiZones.SideRight.MaxHeight, 1e-4f, "the zone grew with the panel");
                var card = roots.SelectMany(r => r.GetComponentsInChildren<SpecCard>(true)).FirstOrDefault();
                Assert.IsNotNull(card, "the spec card");
                var chip = card.GetComponent<AssetCompareChip>();
                Assert.IsNotNull(chip, "Compare on the card");
                Assert.AreEqual(AssetCompareChip.Label, chip.button.Text);
                Assert.AreEqual(card.tier.transform.localPosition.y, chip.button.transform.localPosition.y, 1e-4f, "on the tier line");
                var app = roots.SelectMany(r => r.GetComponentsInChildren<AssetModeSwitcher>(true)).FirstOrDefault();
                Assert.IsNotNull(app, "the switcher");
                Assert.IsNotNull(app.editor, "wired to the placement editor");
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
