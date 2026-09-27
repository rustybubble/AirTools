using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AirTools.Dev;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Tools;
using NUnit.Framework;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Tests
{
    public class LevelMathTests
    {
        [Test]
        public void FloorIsLevelZero()
        {
            var r = LevelMath.Read(Vector3.zero, Vector3.up, Vector3.up);
            Assert.AreEqual(LevelMode.Level, r.Mode);
            Assert.AreEqual(0, r.Degrees, 1e-6);
            Assert.AreEqual(Vector3.zero, r.Downhill);
        }

        [Test]
        public void TwoDegreeSlopeReadsTwoDegreesAndGrade()
        {
            var n = Quaternion.Euler(2f, 0f, 0f) * Vector3.up;
            var r = LevelMath.Read(Vector3.zero, n, Vector3.up);
            Assert.AreEqual(LevelMode.Level, r.Mode);
            Assert.AreEqual(2.0, r.Degrees, 1e-4);
            Assert.AreEqual(3.492, r.PercentGrade, 0.01);
            Assert.Greater(Vector3.Dot(r.Downhill, Vector3.down), 0f, "downhill points down the slope");
        }

        [Test]
        public void WallIsPlumb()
        {
            var r = LevelMath.Read(Vector3.zero, Vector3.forward, Vector3.up);
            Assert.AreEqual(LevelMode.Plumb, r.Mode);
            Assert.AreEqual(0, r.Degrees, 1e-6);
        }

        [Test]
        public void LeaningWallReadsDeviation()
        {
            var n = Quaternion.Euler(-3f, 0f, 0f) * Vector3.forward; // leans 3° back
            var r = LevelMath.Read(Vector3.zero, n, Vector3.up);
            Assert.AreEqual(LevelMode.Plumb, r.Mode);
            Assert.AreEqual(3.0, r.Degrees, 1e-4);
        }

        [Test]
        public void CeilingReadsLevel()
        {
            var r = LevelMath.Read(Vector3.zero, Quaternion.Euler(1f, 0, 0) * Vector3.down, Vector3.up);
            Assert.AreEqual(LevelMode.Level, r.Mode);
            Assert.AreEqual(1.0, r.Degrees, 1e-4);
        }

        [Test]
        public void ModeSplitsAt45Degrees()
        {
            Assert.AreEqual(LevelMode.Level, LevelMath.Read(Vector3.zero, Quaternion.Euler(44f, 0, 0) * Vector3.up, Vector3.up).Mode);
            Assert.AreEqual(LevelMode.Plumb, LevelMath.Read(Vector3.zero, Quaternion.Euler(46f, 0, 0) * Vector3.up, Vector3.up).Mode);
        }
    }

    public abstract class LevelFixture : FacadeToolFixture
    {
        protected LevelTool Level;

        [SetUp]
        public void CreateLevel()
        {
            Tool.Equip(false);
            Level = Tool.gameObject.AddComponent<LevelTool>();
            Level.SetInput(Hub);
            Level.Equip(true);
        }

        [TearDown]
        public void DestroyLevel()
        {
            if (Level != null && Level.viewRoot != null) UnityEngine.Object.DestroyImmediate(Level.viewRoot.gameObject);
        }
    }

    public class LevelScenarioTests : LevelFixture
    {
        public static IEnumerable<TestCaseData> Cases()
        {
            foreach (var s in LevelScenarios.M3())
                for (int seed = 1; seed <= 25; seed++)
                    yield return new TestCaseData(s.Id, seed).SetName($"{s.Id}.seed{seed:00}");
        }

        [TestCaseSource(nameof(Cases))]
        public void GroundTruth(string id, int seed)
        {
            var s = LevelScenarios.M3().Single(x => x.Id == id);
            var r = LevelScenarios.Run(s, Level, Hub, null, seed);
            Assert.IsTrue(r.Passed, r.ToString());
            Assert.AreEqual("level", Notebook.Last.Tool);
            Assert.AreEqual("°", Notebook.Last.Unit);
        }
    }

    public class LevelToolBehaviourTests : LevelFixture
    {
        [Test]
        public void LiveReadingFollowsThePointer()
        {
            Hub.SetPointerOverride(ToolHand.Right, ToolInputHub.RayPose(new Vector3(S.LedgeCentreX, 1.7f, 1.5f), new Vector3(S.LedgeCentreX, 0.995f, 0.15f)));
            Level.Tick();
            Assert.IsTrue(Level.Live.HasValue);
            Assert.AreEqual(2.0, Level.Live.Value.Degrees, 0.05);
            Hub.SetPointerOverride(ToolHand.Right, null);
            Assert.AreEqual(0, Notebook.Entries.Count, "live reading is not logged");
        }

        [Test]
        public void ClickLogsAndUndoRemoves()
        {
            var s = LevelScenarios.M3()[0];
            LevelScenarios.Run(s, Level, Hub, null, 1);
            Assert.AreEqual(1, Level.Placements.Count);
            StringAssert.StartsWith("Level 2.0°", Notebook.Last.Label);
            StringAssert.Contains("3.5% slope", Notebook.Last.Label);
            Hub.RaiseButton(ToolHand.Right, ToolButton.Undo);
            Assert.AreEqual(0, Level.Placements.Count);
            Assert.AreEqual(0, Notebook.Entries.Count);
        }

        [Test]
        public void MeasureToolIgnoresClicksWhileLevelIsEquipped()
        {
            LevelScenarios.Run(LevelScenarios.M3()[0], Level, Hub, null, 1);
            Assert.AreEqual(0, Tool.Session.Count);
            Assert.AreEqual(1, Notebook.Entries.Count);
        }

        [Test]
        public void GizmoShowsTheReading()
        {
            var p = LevelScenarios.Run(LevelScenarios.M3()[0], Level, Hub, null, 1);
            Assert.IsTrue(p.Passed, p.ToString());
            StringAssert.StartsWith("Level 2.0°", Level.Placements[0].Gizmo.LabelText);
        }
    }

    public class NotebookExportTests
    {
        List<NotebookEntry> m_Entries;
        string m_Dir;

        [SetUp]
        public void SetUp()
        {
            Notebook.Clear();
            var t = new DateTime(2026, 9, 25, 14, 2, 10);
            m_Entries = new List<NotebookEntry>
            {
                new NotebookEntry("measure", 1.5, "m", new[] { new Vector3(-0.75f, 4.1f, -0.05f), new Vector3(0.75f, 4.1f, -0.05f) }, t, 6, "Distance 1.50 m · 4′ 11″") { Sides = new[] { 1.5 } },
                new NotebookEntry("measure", 1.8, "m²", new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up }, t, 5, "Quad 1.80 m² · sides 1.500, 1.200") { Sides = new[] { 1.5, 1.2, 1.5, 1.2 }, Angles = new[] { 90.0, 90.0, 90.0, 90.0 } },
                new NotebookEntry("level", 2.0, "°", new[] { new Vector3(-2.5f, 1f, 0.15f) }, t, 2, "Level 2.0° (3.5% slope)"),
                new NotebookEntry("note", 0, "", Array.Empty<Vector3>(), t, -1, "check \"flashing\", left side,\nnear the gutter"),
            };
            foreach (var e in m_Entries) Notebook.Add(e);
            m_Dir = Path.Combine(Path.GetTempPath(), "airtools-export-test-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            Notebook.Clear();
            if (Directory.Exists(m_Dir)) Directory.Delete(m_Dir, true);
        }

        [Test]
        public void CsvRoundTripsIncludingQuotesCommasAndNewlines()
        {
            var rows = NotebookExporter.ParseCsv(NotebookExporter.ToCsv(Notebook.Entries));
            Assert.AreEqual(1 + m_Entries.Count, rows.Count);
            CollectionAssert.AreEqual(NotebookExporter.CsvColumns, rows[0]);
            Assert.IsTrue(rows.Skip(1).All(r => r.Length == NotebookExporter.CsvColumns.Length), "every row has every column");
            Assert.AreEqual("1.5", rows[1][2]);
            Assert.AreEqual("m²", rows[2][3]);
            Assert.AreEqual("check \"flashing\", left side,\nnear the gutter", rows[4][4]);
            Assert.AreEqual("90.00 ; 90.00 ; 90.00 ; 90.00", rows[2][9]);
        }

        [Test]
        public void HtmlHasOneRowPerEntryAndEmbedsEvidence()
        {
            var tex = new Texture2D(8, 6, TextureFormat.RGB24, false);
            try
            {
                string html = NotebookExporter.ToHtml(Notebook.Entries, id => id >= 0 ? tex : null, "test <site>");
                Assert.AreEqual(m_Entries.Count, CountOf(html, "<tr class=\"entry\">"));
                Assert.AreEqual(3, CountOf(html, "data:image/jpeg;base64,"), "3 entries have a camera");
                StringAssert.Contains("test &lt;site&gt;", html);
                StringAssert.EndsWith("</html>", html);
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        [Serializable] class JsonEntry { public int id; public string tool; public double value_si; public string unit; public string label; public int camera_id; }
        [Serializable] class JsonDoc { public string site; public string exported_at; public JsonEntry[] entries; }

        [Test]
        public void JsonParsesBack()
        {
            var doc = JsonUtility.FromJson<JsonDoc>(NotebookExporter.ToJson(Notebook.Entries, "synthetic-facade"));
            Assert.AreEqual("synthetic-facade", doc.site);
            Assert.AreEqual(m_Entries.Count, doc.entries.Length);
            Assert.AreEqual(1.5, doc.entries[0].value_si, 1e-9);
            Assert.AreEqual("level", doc.entries[2].tool);
            Assert.AreEqual("check \"flashing\", left side,\nnear the gutter", doc.entries[3].label);
        }

        [Test]
        public void ExportWritesBothFiles()
        {
            var (csv, html) = NotebookExporter.Export(Notebook.Entries, _ => null, m_Dir, "export test");
            Assert.IsTrue(File.Exists(csv));
            Assert.IsTrue(File.Exists(html));
            Assert.AreEqual(1 + m_Entries.Count, NotebookExporter.ParseCsv(File.ReadAllText(csv)).Count);
            StringAssert.Contains("<table>", File.ReadAllText(html));
        }

        [Test]
        public void EncodeJpgWorksForNonReadableTextures()
        {
            var tex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            tex.Apply(false, makeNoLongerReadable: true);
            try
            {
                var jpg = NotebookExporter.EncodeJpg(tex);
                Assert.IsNotNull(jpg);
                Assert.AreEqual(0xFF, jpg[0]); Assert.AreEqual(0xD8, jpg[1]);
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        static int CountOf(string s, string sub)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
            return n;
        }
    }

    public class NotebookPageTests
    {
        /// D2: pinned to metric (the row expectations below); UnitsCopyTests covers the imperial rows.
        [SetUp]
        public void MetricRows() => AirTools.UI.UiSettings.UseUnits(AirTools.Core.UnitSystem.Metric);

        static List<NotebookEntry> Make(int n) =>
            Enumerable.Range(1, n).Select(i => new NotebookEntry("measure", i, "m", null, DateTime.Now, -1, $"entry {i}")).ToList();

        [Test]
        public void NewestFirstWithPaging()
        {
            var entries = Make(10);
            var page = new NotebookPage(4);
            Assert.AreEqual(3, page.PageCount(entries.Count));
            CollectionAssert.AreEqual(new[] { "entry 10", "entry 9", "entry 8", "entry 7" }, page.Rows(entries).Select(e => e.Label));
            page.Next(entries.Count); page.Next(entries.Count); page.Next(entries.Count);
            Assert.AreEqual(2, page.Page, "clamped at the last page");
            var last = page.Rows(entries);
            CollectionAssert.AreEqual(new[] { "entry 2", "entry 1" }, last.Where(e => e != null).Select(e => e.Label));
            Assert.IsNull(last[2]);
            page.Prev(); page.Prev(); page.Prev();
            Assert.AreEqual(0, page.Page);
        }

        [Test]
        public void EmptyNotebookHasOnePage()
        {
            var page = new NotebookPage(4);
            Assert.AreEqual(1, page.PageCount(0));
            Assert.IsTrue(page.Rows(new List<NotebookEntry>()).All(e => e == null));
        }

        [Test]
        public void RowTitleTruncates()
        {
            var e = new NotebookEntry("note", 0, "", null, DateTime.Now, 3, new string('x', 60));
            Assert.LessOrEqual(NotebookPage.RowTitle(e).Length, 34);
            StringAssert.EndsWith("…", NotebookPage.RowTitle(e));
            StringAssert.DoesNotContain("photo", NotebookPage.RowDetail(e));
            StringAssert.Contains(":", NotebookPage.RowDetail(e));
            // A 1.0 m horizontal tape: named by its direction, the value never cut.
            var tape = new NotebookEntry("measure", 1.0, "m", new[] { Vector3.zero, new Vector3(1f, 0f, 0f) }, DateTime.Now, -1, "Distance 1.00 m · 3′ 3⅜″");
            StringAssert.StartsWith("Width · 1.00 m", NotebookPage.RowTitle(tape));
            Assert.LessOrEqual(NotebookPage.RowTitle(tape).Length, 34);
            var post = new NotebookEntry("measure", 2.4, "m", new[] { Vector3.zero, new Vector3(0f, 2.4f, 0f) }, DateTime.Now, -1, "Distance 2.40 m");
            StringAssert.StartsWith("Height · 2.40 m", NotebookPage.RowTitle(post));
        }
    }

    public class EvidenceCameraTests
    {
        /// SPEC M3: every reading's evidence photo is a camera whose frustum contains the reading's points.
        [Test]
        public void EveryScenarioReadingHasACameraThatSeesIt()
        {
            var cams = SyntheticFacadeBuilder.CreateCameras();
            var readings = MeasureScenarios.M2().Select(s => s.Targets).Concat(LevelScenarios.M3().Select(s => new[] { s.Target }));
            foreach (var pts in readings)
            {
                int id = CameraEvidence.Nearest(pts, cams);
                Assert.GreaterOrEqual(id, 0);
                Assert.IsTrue(CameraEvidence.ContainsAll(cams[id], pts), $"camera {id} doesn't see {string.Join(", ", pts)}");
            }
        }
    }
}
