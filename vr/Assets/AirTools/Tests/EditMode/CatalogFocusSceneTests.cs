using System.Collections.Generic;
using System.IO;
using System.Linq;
using AirTools.Core;
using AirTools.Editor;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.UI;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AirTools.Tests
{
    /// gaze-catalog: the focus chip and the gaze tracker as Wire builds them in Main.unity, and the window following the
    /// focus (a request per focus, the copy kept per site and focus, pin, closed). Needs the Editor (GameObjects); the
    /// pure logic is CatalogFocusTests.
    public class CatalogFocusSceneTests
    {
        [Test]
        public void WireBuildsTheFocusChipAndTheGazeTracker()
        {
            var scene = EditorSceneManager.OpenScene(MainSceneBuilder.ScenePath, OpenSceneMode.Additive);
            try
            {
                var w = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<CatalogWindow>(true)).Single();
                // The chip: in the search field's row, at its right, a chip that pins.
                Assert.IsNotNull(w.focusChip, "focusChip");
                Assert.AreEqual(ButtonStyle.Chip, w.focusChip.style);
                var cb = w.focusChip.GetComponent<CatalogButton>();
                Assert.AreEqual(CatalogAction.FocusPin, cb.action);
                Assert.AreSame(w, cb.window);
                Assert.IsNotNull(w.focusChip.poke, "poke");
                Assert.IsNotNull(w.focusChip.ray, "ray (D5)");
                var content = w.browser.content.transform;
                var chip = content.InverseTransformPoint(w.focusChip.transform.position);
                var field = content.InverseTransformPoint(w.field.transform.position);
                Assert.AreEqual(field.y, chip.y, 1e-4f, "one row");
                float chipLeft = chip.x - w.focusChip.surface.size.x * 0.5f, fieldRight = field.x + w.field.surface.size.x * 0.5f;
                Assert.GreaterOrEqual(chipLeft, fieldRight - 1e-4f, "no overlap");
                Assert.LessOrEqual(chip.x + w.focusChip.surface.size.x * 0.5f, CatalogBuilder.W * 0.5f + 1e-4f, "inside the panel");
                Assert.GreaterOrEqual(w.focusChip.surface.size.y, 0.026f - 1e-4f, "a finger's target");
                // The tracker: on the window's object, fed by the centre eye, pausing on the window's panel.
                var g = w.GetComponent<GazeFocusTracker>();
                Assert.IsNotNull(g, "GazeFocusTracker on the PartsMenu");
                Assert.AreSame(g, w.gaze);
                Assert.AreSame(w, g.window);
                Assert.IsNotNull(g.head, "the centre eye");
                StringAssert.Contains("CenterEye", g.head.name);
                Assert.AreEqual(CatalogBuilder.W, g.panelSize.x, 1e-4f);
                Assert.AreEqual(CatalogBuilder.H, g.panelSize.y, 1e-4f);
                Assert.LessOrEqual(g.samplesPerSecond, 4f + 1e-4f, "about 4 times a second, never per frame");
                Assert.AreEqual(1.2f, g.holdSeconds, 1e-4f);
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        // ---------------- the window following the focus ----------------

        GameObject m_Go;
        CatalogWindow m_Window;
        PartsBrowser m_Browser;
        CatalogClient m_Client;
        readonly List<string> m_Asked = new List<string>();
        readonly Dictionary<string, byte[]> m_Saved = new Dictionary<string, byte[]>();

        static string CacheDir => Path.Combine(Application.persistentDataPath, "catalog");

        static string Answer(string focus, params string[] ids)
        {
            string cats = string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"title\":\"{id}\",\"items\":[{{\"part_id\":\"{id}-1\",\"name\":\"{id} one\"}}]}}"));
            return $"{{\"site\":\"built-in\",\"environment\":\"facade\",\"title\":\"Facade\",\"focus\":{(focus == null ? "null" : $"\"{focus}\"")}," +
                   $"\"foci\":[\"roof\",\"wall\",\"ground\",\"ceiling\",\"opening\"],\"categories\":[{cats}]}}";
        }

        [SetUp]
        public void Build()
        {
            Notebook.Clear();
            AppState.Reset();
            AppState.Set(AppMode.World);
            // The headset's copies this test writes (built-in.json and its focus variants): kept and put back.
            m_Saved.Clear();
            foreach (CatalogFocus f in System.Enum.GetValues(typeof(CatalogFocus)))
            {
                string p = Path.Combine(CacheDir, CatalogText.CacheFile("built-in", f));
                m_Saved[p] = File.Exists(p) ? File.ReadAllBytes(p) : null;
            }
            m_Go = new GameObject("catalog-focus-test");
            m_Browser = m_Go.AddComponent<PartsBrowser>();
            m_Window = m_Go.AddComponent<CatalogWindow>();
            m_Client = m_Go.AddComponent<CatalogClient>();
            m_Browser.catalog = m_Window;
            m_Window.browser = m_Browser;
            m_Window.client = m_Client;
            var content = new GameObject("Content");
            content.transform.SetParent(m_Go.transform, false);
            content.SetActive(false);
            m_Browser.content = content;
            m_Asked.Clear();
            m_Client.testTransport = path =>
            {
                m_Asked.Add(path);
                if (path.Contains("focus=roof")) return (200, Answer("roof", "solar-panels", "gutters", "roof-vents"));
                if (path.Contains("focus=wall")) return (200, Answer("wall", "wall-hvac", "windows", "window-ac"));
                if (path.Contains("focus=ground")) return (200, Answer("ground", "pavers", "planters"));
                return (200, Answer(null, "windows", "doors", "siding", "gutters"));
            };
            Services.Register(m_Browser);
            Services.Register(m_Window);
        }

        [TearDown]
        public void Destroy()
        {
            Services.Unregister(m_Browser);
            Services.Unregister(m_Window);
            Object.DestroyImmediate(m_Go);
            foreach (var kv in m_Saved)
            {
                if (kv.Value != null) File.WriteAllBytes(kv.Key, kv.Value);
                else if (File.Exists(kv.Key)) File.Delete(kv.Key);
            }
            Notebook.Clear();
            AppState.Reset();
        }

        int FocusRequests(string focus) => m_Asked.Count(p => p.Contains("&focus=" + focus));

        [Test]
        public void TheOpenCatalogFollowsTheFocusWithOneRequestPerChange()
        {
            m_Window.Open();
            var m = m_Window.Model;
            Assert.IsTrue(m.FocusSupported, "the laptop's foci");
            Assert.AreEqual("windows", m.CurrentCategory.id);
            m_Window.OnGazeFocus(CatalogFocus.Roof, null);
            Assert.AreEqual(1, FocusRequests("roof"));
            Assert.AreEqual(CatalogFocus.Roof, m.ShownFocus);
            Assert.AreEqual("solar-panels", m.CurrentCategory.id, "the roof leads with its own");
            m_Window.OnGazeFocus(CatalogFocus.Roof, null);   // the same focus again: nothing
            Assert.AreEqual(1, FocusRequests("roof"));
            m_Window.OnGazeFocus(CatalogFocus.Wall, null);
            Assert.AreEqual("wall-hvac", m.CurrentCategory.id);
            // Back to the roof: from the copy kept in memory at once (and the laptop asked once more for the latest).
            m_Client.testTransport = path => { m_Asked.Add(path); return (503, null); };
            m_Window.OnGazeFocus(CatalogFocus.Roof, null);
            Assert.AreEqual(CatalogFocus.Roof, m.ShownFocus, "kept per site and focus");
            Assert.AreEqual("solar-panels", m.CurrentCategory.id);
            Assert.AreEqual(2, FocusRequests("roof"));
            Assert.IsTrue(File.Exists(Path.Combine(CacheDir, CatalogText.CacheFile("built-in", CatalogFocus.Wall))), "the wall's copy saved on the headset");
        }

        [Test]
        public void PinnedTheCatalogStaysAndUnpinnedItCatchesUp()
        {
            m_Window.Open();
            var m = m_Window.Model;
            m_Window.OnGazeFocus(CatalogFocus.Roof, null);
            m_Window.ToggleFocusPin();
            Assert.IsTrue(m.FocusPinned);
            int asked = m_Asked.Count;
            m_Window.OnGazeFocus(CatalogFocus.Wall, null);
            Assert.AreEqual(CatalogFocus.Roof, m.ShownFocus, "pinned: no change");
            Assert.AreEqual(asked, m_Asked.Count, "and no request");
            m_Window.ToggleFocusPin();
            Assert.IsFalse(m.FocusPinned);
            Assert.AreEqual(CatalogFocus.Wall, m.ShownFocus, "caught up with the gaze");
        }

        [Test]
        public void ClosedTheFocusIsAskedForOnOpening()
        {
            m_Window.Open();
            m_Window.Hide();
            int asked = m_Asked.Count;
            m_Window.OnGazeFocus(CatalogFocus.Ground, null);
            Assert.AreEqual(asked, m_Asked.Count, "closed: nothing asked");
            m_Window.Open();
            Assert.AreEqual(1, FocusRequests("ground"));
            Assert.AreEqual(CatalogFocus.Ground, m_Window.Model.ShownFocus);
            Assert.AreEqual("pavers", m_Window.Model.CurrentCategory.id);
        }

        [Test]
        public void AnOlderLaptopWithoutFociIsNeverAskedForAFocus()
        {
            m_Client.testTransport = path =>
            {
                m_Asked.Add(path);
                return (200, "{\"site\":\"built-in\",\"environment\":\"facade\",\"categories\":[{\"id\":\"windows\",\"items\":[]}]}");
            };
            m_Window.Open();
            m_Window.OnGazeFocus(CatalogFocus.Roof, null);
            Assert.IsFalse(m_Asked.Any(p => p.Contains("focus=")), string.Join(" ", m_Asked));
            Assert.AreEqual("All categories", m_Window.Model.FocusChipLabel);
        }
    }
}
