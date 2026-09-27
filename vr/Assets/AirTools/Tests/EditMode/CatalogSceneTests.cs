using System.Linq;
using AirTools.Core;
using AirTools.Editor;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Tools;
using AirTools.UI;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AirTools.Tests
{
    /// catalog: the Catalog as Wire builds it in Main.unity (run AirTools ▸ Wire Main Scene first), and the ring's /
    /// the pill's paths opening it with and without a tape. Needs the Editor (the pure logic is CatalogTests).
    public class CatalogSceneTests
    {
        [Test]
        public void WireBuildsTheCatalogWindowAndKeyboard()
        {
            var scene = EditorSceneManager.OpenScene(MainSceneBuilder.ScenePath, OpenSceneMode.Additive);
            try
            {
                var roots = scene.GetRootGameObjects();
                var windows = roots.SelectMany(r => r.GetComponentsInChildren<CatalogWindow>(true)).ToList();
                Assert.AreEqual(1, windows.Count, "one Catalog");
                var w = windows[0];
                Assert.AreEqual(CatalogBuilder.Name, w.gameObject.name);
                var browser = w.GetComponent<PartsBrowser>();
                Assert.IsNotNull(browser, "the parts engine under it");
                Assert.AreSame(w, browser.catalog);
                Assert.AreSame(browser, w.browser);
                Assert.IsNotNull(w.client, "CatalogClient");
                Assert.IsNotNull(w.parts, "PartsClient (photos)");
                Assert.IsNotNull(w.shipped, "the shipped parts (the built-in catalog)");
                // The left side slot (declutter §3.1), no bigger than it.
                Assert.IsTrue(UiZones.SideLeft.Holds(browser.distance, browser.downDeg, browser.yawDeg), "SideLeft");
                Assert.LessOrEqual(browser.panel.size.x, UiZones.SideLeft.MaxWidth + 1e-4f);
                Assert.LessOrEqual(browser.panel.size.y, UiZones.SideLeft.MaxHeight + 1e-4f);
                Assert.AreSame(browser.panel, FloatingWindow.FindPanel(w.transform), "the window's one Panel is the catalog's (not the keyboard's)");
                // Header, field, chips, fits, status, grid, pager.
                foreach (var (name, o) in new (string, Object)[]
                {
                    ("title", w.title), ("field", w.field), ("fieldText", w.fieldText), ("fits", w.fits), ("status", w.status), ("close", w.close),
                    ("previous", w.previous), ("next", w.next), ("pageText", w.pageText), ("emptyButton", w.emptyButton), ("keyboard", w.keyboard),
                    ("talk", browser.talk), ("handle", browser.handle),
                })
                    Assert.IsTrue(o != null, name);
                Assert.AreSame(w.status, browser.statusText, "the live search's progress shows on the catalog's status line");
                Assert.AreEqual(CatalogModel.ChipSlots, w.chips.Length);
                for (int i = 0; i < w.chips.Length; i++)
                {
                    var cb = w.chips[i].GetComponent<CatalogButton>();
                    Assert.AreEqual(CatalogAction.Chip, cb.action);
                    Assert.AreEqual(i, cb.index);
                    Assert.AreEqual(ButtonStyle.Chip, w.chips[i].style);
                }
                Assert.AreEqual(CatalogModel.CardsPerPage, w.cards.Length);
                for (int i = 0; i < w.cards.Length; i++)
                {
                    var card = w.cards[i];
                    Assert.IsNotNull(card.button);
                    Assert.IsNotNull(card.photo);
                    Assert.IsNotNull(card.detail);
                    Assert.IsNotNull(card.badge, "the 3D badge");
                    var cb = card.GetComponent<CatalogButton>();
                    Assert.AreEqual(CatalogAction.Card, cb.action);
                    Assert.AreEqual(i, cb.index);
                }
                // Every control: poke (hands) and a ray (D5: hand ray pinch, controller trigger).
                var buttons = w.GetComponentsInChildren<GlassButton>(true);
                Assert.Greater(buttons.Length, 60);
                foreach (var b in buttons)
                {
                    Assert.IsNotNull(b.poke, $"{b.name}: poke");
                    Assert.IsNotNull(b.ray, $"{b.name}: ray (D5)");
                }
                // The keyboard: every key, 3 cm, quick repeats, Search the primary, under the window and hidden until used.
                var kb = w.keyboard;
                Assert.IsTrue(kb.transform.IsChildOf(browser.content.transform), "hides with the window");
                Assert.IsFalse(kb.content.activeSelf, "hidden until the field is tapped");
                var keys = kb.keys.Select(k => k.key).ToList();
                CollectionAssert.AreEquivalent(CatalogTextField.Rows.SelectMany(r => r).ToList(), keys);
                foreach (var k in kb.keys)
                {
                    Assert.AreEqual(CatalogAction.Key, k.action);
                    Assert.AreSame(w, k.window);
                    Assert.GreaterOrEqual(k.button.surface.size.x, 0.03f - 1e-4f, k.key);
                    Assert.GreaterOrEqual(k.button.surface.size.y, 0.03f - 1e-4f, k.key);
                    Assert.LessOrEqual(k.button.cooldownSeconds, 0.12f, $"{k.key}: typing repeats");
                }
                Assert.AreEqual(ButtonStyle.Primary, kb.Find(CatalogTextField.Enter).style);
                var local = browser.content.transform.InverseTransformPoint(kb.transform.position);
                Assert.Less(local.y, -browser.panel.size.y * 0.5f, "below the window");
                Assert.Less(local.z, 0f, "nearer than the window");
                // The ring's Catalog item and the app's CatalogClient.
                Assert.IsTrue(roots.SelectMany(r => r.GetComponentsInChildren<CatalogClient>(true)).Count() == 1, "one CatalogClient");
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        GameObject m_Go;
        CatalogWindow m_Window;
        PartsBrowser m_Browser;

        [SetUp]
        public void Build()
        {
            Notebook.Clear();
            AppState.Reset();
            AppState.Set(AppMode.World);
            m_Go = new GameObject("catalog-test");
            m_Browser = m_Go.AddComponent<PartsBrowser>();
            m_Window = m_Go.AddComponent<CatalogWindow>();
            m_Browser.catalog = m_Window;
            m_Window.browser = m_Browser;
            var content = new GameObject("Content");
            content.transform.SetParent(m_Go.transform, false);
            content.SetActive(false);
            m_Browser.content = content;
            // OnEnable doesn't run in edit mode: register by hand.
            Services.Register(m_Browser);
            Services.Register(m_Window);
        }

        [TearDown]
        public void Destroy()
        {
            Services.Unregister(m_Browser);
            Services.Unregister(m_Window);
            Object.DestroyImmediate(m_Go);
            Notebook.Clear();
            AppState.Reset();
        }

        [Test]
        public void TheRingsCatalogOpensIt_WithNoTape_AndClosesIt()
        {
            Assert.IsFalse(Notebook.Entries.Any(e => e.Tool == "measure"), "no tape");
            Assert.IsFalse(m_Window.IsOpen);
            ToolboxButton.Run(ToolboxAction.ToggleCatalog);
            Assert.IsTrue(m_Window.IsOpen, "open with no tape");
            Assert.IsTrue(m_Window.Showing);
            Assert.AreEqual(true, ToolboxButton.IsOn(ToolboxAction.ToggleCatalog));
            Assert.Greater(m_Window.Model.CategoryCount, 0, "the place's categories (built-in: no laptop, no saved copy)");
            Assert.AreEqual(CatalogMode.Browse, m_Window.Model.Mode);
            Assert.IsFalse(m_Window.Model.Fit.Available, "no Fits chip without a tape");
            ToolboxButton.Run(ToolboxAction.ToggleCatalog);
            Assert.IsFalse(m_Window.IsOpen, "the ring's Catalog again closes it");
            Assert.IsFalse(m_Browser.content.activeSelf);
        }

        [Test]
        public void ThePillsFindPartsOpensItWithFitsOn()
        {
            var e = new NotebookEntry("measure", 0.61, "m", new[] { new Vector3(0f, 1f, 0f), new Vector3(0.61f, 1f, 0f) }, System.DateTime.Now, -1, "0.61 m");
            Notebook.Add(e);
            Assert.IsTrue(AppCommands.ShowFindParts());
            Assert.IsTrue(m_Window.IsOpen);
            Assert.IsTrue(m_Window.Model.Fit.Available);
            Assert.AreEqual("w", m_Window.Model.Fit.Axis);
            Assert.AreEqual(0.61, m_Window.Model.Fit.TapeM, 1e-4);
            Assert.IsTrue(m_Window.Model.FitsOn, "from the tape's pill: Fits on");
            AppCommands.HideCatalog();
            Assert.IsTrue(AppCommands.ShowCatalog());
            Assert.IsTrue(m_Window.Model.FitsOn, "stays as the person left it");
            m_Window.ToggleFits();
            Assert.IsFalse(m_Window.Model.FitsOn);
        }

        [Test]
        public void ShowCatalogTakesACategoryAndAQuery()
        {
            Assert.IsTrue(AppCommands.ShowCatalog("hangers"));
            Assert.AreEqual("gutter-hangers", m_Window.Model.CurrentCategory.id, "built-in facade (no scene loaded): Gutter hangers");
            Assert.IsTrue(AppCommands.ShowCatalog(null, "window ac"));
            Assert.AreEqual(CatalogMode.Search, m_Window.Model.Mode);
            Assert.AreEqual("window ac", m_Window.Model.Query);
            Assert.AreEqual("", m_Window.Model.Field.Text, "voice / Grok's show_catalog doesn't type into the search bar");
            Assert.IsTrue(AppCommands.ShowCatalog("rocket launchers"));
            Assert.AreEqual("rocket launchers", m_Window.Model.Query, "an unknown category is searched for");
        }
    }
}
