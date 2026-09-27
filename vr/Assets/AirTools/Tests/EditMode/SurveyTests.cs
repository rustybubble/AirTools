using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Tests
{
    /// B1 "the agent measures for you" on the synthetic facade exported as a package (structure from its boxes, the
    /// window and door as labelled objects), through the real SnapService and MeasureTool (hand-off §2–§4, §7).
    public class SurveyTests
    {
        protected GameObject Root;
        protected SceneRoot SceneRoot;
        protected GameObject Content;
        protected StructureLayer Layer;
        protected GameObject Rig;
        protected ToolInputHub Hub;
        protected MeasureTool Tool;
        protected SurveyRunner Runner;
        static readonly Vector3 EyePos = new Vector3(0.3f, 1.6f, 4f);

        protected virtual List<PackageFixtures.Rect3> Objects() => PackageFixtures.FacadeObjects();

        protected Pose Head => new Pose(Content.transform.TransformPoint(EyePos), Quaternion.LookRotation(Vector3.back));

        [OneTimeSetUp]
        public void Build()
        {
            var facade = SyntheticFacadeBuilder.CreateHierarchy();
            Layer = StructureLayer.Parse(PackageFixtures.StructureJson(facade.transform, 1f, Objects()));
            Root = new GameObject("SceneRoot");
            SceneRoot = Root.AddComponent<SceneRoot>();
            Content = new GameObject("Content");
            Content.transform.SetParent(Root.transform, false);
            facade.transform.SetParent(Content.transform, false);
            var manifest = SceneManifest.Parse(@"{""mesh"":{""file"":""mesh.r1.glb""},""revision"":1,""gravity_residual_deg"":0.0}");
            SceneRoot.SetRuntimeContent("synthetic-facade", manifest, null, Content, Layer);
            Services.Register(SceneRoot);
            Physics.SyncTransforms();
        }

        [OneTimeTearDown]
        public void Teardown()
        {
            Services.Unregister(SceneRoot);
            UnityEngine.Object.DestroyImmediate(Root);
        }

        [SetUp]
        public void CreateTools()
        {
            AirTools.UI.UiSettings.UseUnits(UnitSystem.Metric);   // D2: pinned ("1500 × 1200 mm"); UnitsSwitchTests: imperial
            Notebook.Clear();
            AppState.Reset();
            SessionInfo.Set("quest-test");
            Rig = new GameObject("survey-test");
            Hub = Rig.AddComponent<ToolInputHub>();
            Tool = Rig.AddComponent<MeasureTool>();
            Tool.frame = Root.transform;
            Tool.SetInput(Hub);
            Tool.Equip(true);
            Runner = Rig.AddComponent<SurveyRunner>();
            Runner.tool = Tool; Runner.root = SceneRoot;
        }

        [TearDown]
        public void DestroyTools()
        {
            Services.Unregister<IToolInput>(Hub);
            if (Tool != null && Tool.viewRoot != null) UnityEngine.Object.DestroyImmediate(Tool.viewRoot.gameObject);
            UnityEngine.Object.DestroyImmediate(Rig);
            Notebook.Clear();
            AppState.Reset();
        }

        [Test]
        public void Context_SendsTheSiteAndScale_ButNotForTheBuiltInScene()
        {
            var ctx = new Dictionary<string, object>();
            AgentContext.AddSite(ctx, SceneRoot);
            Assert.AreEqual("synthetic-facade", ctx["site"]);
            Assert.AreEqual(1.0, (double)ctx["scale"], 1e-6);
            var builtIn = new GameObject("BuiltIn").AddComponent<SceneRoot>();
            try
            {
                var none = new Dictionary<string, object>();
                AgentContext.AddSite(none, builtIn);
                Assert.IsFalse(none.ContainsKey("site"));
                Assert.IsFalse(none.ContainsKey("scale"));
            }
            finally { UnityEngine.Object.DestroyImmediate(builtIn.gameObject); }
        }

        [Test]
        public void Window_OneTarget_1500x1200_AndTheReportMatchesTheContract()
        {
            Assert.IsTrue(Runner.Run("window", "all", "size", "sv-test1", Head));
            Assert.AreEqual(1, Runner.Planned);
            Assert.IsTrue(Runner.Running, "sequential: nothing is measured until the steps run");
            Runner.RunToEnd();
            Assert.IsFalse(Runner.Running);
            Assert.AreEqual(1, Runner.Results.Count);
            var r = Runner.Results[0];
            Assert.AreEqual("o0", r.id);
            Assert.AreEqual(1.500, r.w_m, 0.005, "width");
            Assert.AreEqual(1.200, r.h_m, 0.005, "height");
            Assert.AreEqual(4, r.angles_deg.Length);
            foreach (var a in r.angles_deg) Assert.AreEqual(90.0, a, 0.5, "corner angle");
            Assert.IsFalse(r.unverified, $"snaps {string.Join(",", r.snap)}");
            CollectionAssert.AreEqual(new[] { "corner", "corner", "corner", "corner" }, r.snap);
            // One notebook entry, labelled with the object.
            Assert.AreEqual(1, Notebook.Entries.Count);
            var e = Notebook.Entries[0];
            Assert.AreEqual(e.Id, r.notebook_id);
            StringAssert.StartsWith("o0 window · 1500 × 1200 mm", e.Label);
            Assert.AreEqual("1500 × 1200 mm", e.DisplayValue);
            // Exactly one report, shaped like §3.
            Assert.AreEqual(1, Runner.Reports);
            var j = Runner.LastReport;
            Assert.AreEqual("quest-test", (string)j["session_id"]);
            Assert.AreEqual("sv-test1", (string)j["request_id"]);
            Assert.AreEqual("survey_result", (string)j["kind"]);
            Assert.AreEqual("done", (string)j["status"]);
            Assert.AreEqual(1, (int)j["planned"]);
            Assert.IsTrue((bool)j["tts"]);
            Assert.AreEqual(0, ((JArray)j["skipped"]).Count);
            var res = (JObject)((JArray)j["results"])[0];
            foreach (var key in new[] { "id", "label", "w_m", "h_m", "area_m2", "angles_deg", "off_plane_m", "snap", "unverified", "notebook_id" })
                Assert.IsTrue(res.ContainsKey(key), key);
            Assert.AreEqual("window", (string)res["label"]);
            Assert.AreEqual(1.5, (double)res["w_m"], 0.005);
            Assert.AreEqual(1.2, (double)res["h_m"], 0.005);
            Assert.AreEqual(1.8, (double)res["area_m2"], 0.01);
            Assert.AreEqual(JTokenType.Integer, res["notebook_id"].Type);
            Assert.AreEqual(4, ((JArray)res["angles_deg"]).Count);
            // Running it to the end again, or stopping afterwards, never reports twice.
            Runner.Abort();
            Assert.AreEqual(1, Runner.Reports);
        }

        [Test]
        public void Abort_KeepsOnlyCompleteEntries_AndReportsAborted()
        {
            Assert.IsTrue(Runner.Run("any", "all", "size", "sv-abort", Head));
            Assert.AreEqual(2, Runner.Planned);
            for (int i = 0; i < 4; i++) Runner.Step();   // the first object: 4 corners, finished
            Runner.Step(); Runner.Step();                // two corners of the second
            Assert.AreEqual(2, Tool.Session.Count, "half an object down");
            AppCommands.StopSurvey();                    // no runner registered: nothing happens
            Assert.IsTrue(Runner.Running);
            Runner.Abort();
            Assert.IsFalse(Runner.Running);
            Assert.AreEqual(0, Tool.Session.Count, "the half-done object is dropped");
            Assert.AreEqual(1, Notebook.Entries.Count, "the completed object stays");
            Assert.AreEqual(1, Tool.Shapes.Count);
            var j = Runner.LastReport;
            Assert.AreEqual("aborted", (string)j["status"]);
            Assert.AreEqual(2, (int)j["planned"]);
            Assert.AreEqual(1, ((JArray)j["results"]).Count);
            Assert.AreEqual(1, Runner.Reports);
        }

        [Test]
        public void APinchStopsTheSurvey()
        {
            Services.Register<IToolInput>(Hub);
            Assert.IsTrue(Runner.Run("any", "all", "size", "sv-pinch", Head));
            for (int i = 0; i < 5; i++) Runner.Step();
            var pose = ToolInputHub.RayPose(Head.position, Head.position + Vector3.back);
            Hub.RaisePressStart(ToolHand.Right, pose);
            Hub.RaisePressEnd(ToolHand.Right, pose);
            Assert.IsFalse(Runner.Running);
            Assert.AreEqual("aborted", (string)Runner.LastReport["status"]);
            Assert.AreEqual(1, Notebook.Entries.Count, "the pinch didn't add a point of its own");
            Assert.AreEqual(0, Tool.Session.Count);
        }

        [Test]
        public void OneUndoRemovesTheWholeSurvey_AndRedoBringsItBack()
        {
            // A hand tape first: it must survive the survey's undo.
            Tool.Click(new SurfaceHit { point = Content.transform.TransformPoint(new Vector3(-3f, 0.5f, 0f)), kind = SnapKind.Face });
            Tool.Click(new SurfaceHit { point = Content.transform.TransformPoint(new Vector3(-2f, 0.5f, 0f)), kind = SnapKind.Face });
            Tool.Finish();
            Assert.IsTrue(Runner.Run("any", "all", "size", "sv-undo", Head));
            Runner.RunToEnd();
            Assert.AreEqual(3, Notebook.Entries.Count);
            Tool.Undo();
            Assert.AreEqual(1, Notebook.Entries.Count, "one step removes both survey objects");
            Assert.AreEqual("measure", Notebook.Entries[0].Tool);
            Assert.AreEqual(1, Tool.Shapes.Count);
            Tool.Redo();
            Assert.AreEqual(3, Notebook.Entries.Count);
            Assert.AreEqual(3, Tool.Shapes.Count);
            Tool.Undo(); Tool.Undo();
            Assert.AreEqual(0, Notebook.Entries.Count, "then the hand tape");
        }

        [Test]
        public void NoStructureLayer_ReportsNoStructure()
        {
            var bare = new GameObject("Bare").AddComponent<SceneRoot>();
            try
            {
                Runner.root = bare;
                Assert.IsFalse(Runner.Run("cabinet_door", "all", "size", "sv-none", Head));
                Assert.AreEqual("no_structure", (string)Runner.LastReport["status"]);
                Assert.AreEqual(0, ((JArray)Runner.LastReport["results"]).Count);
            }
            finally { Runner.root = SceneRoot; UnityEngine.Object.DestroyImmediate(bare.gameObject); }
        }

        [Test]
        public void NothingOfThatLabel_ReportsDoneWithNoResults()
        {
            Assert.IsTrue(Runner.Run("drawer", "all", "size", "sv-drawer", Head));
            Assert.IsFalse(Runner.Running);
            Assert.AreEqual("done", (string)Runner.LastReport["status"]);
            Assert.AreEqual(0, (int)Runner.LastReport["planned"]);
        }

        [Test]
        public void WhereFilters()
        {
            var head = Head;
            Assert.AreEqual(1, SurveyPlanner.Plan(SceneRoot, "any", "nearest", head).Count);
            var right = SurveyPlanner.Plan(SceneRoot, "any", "right", head);
            // Looking at the wall (−Z): the head's right is −X in the scene, so the window (x = 0) is right of the eye
            // at x = 0.3 and the door (x = 2.8) is left.
            CollectionAssert.AreEqual(new[] { "o0" }, right.Select(t => t.Id).ToArray());
            CollectionAssert.AreEqual(new[] { "o1" }, SurveyPlanner.Plan(SceneRoot, "any", "left", head).Select(t => t.Id).ToArray());
            CollectionAssert.AreEqual(new[] { "o0" }, SurveyPlanner.Plan(SceneRoot, "any", "upper", head).Select(t => t.Id).ToArray());
            CollectionAssert.AreEqual(new[] { "o1" }, SurveyPlanner.Plan(SceneRoot, "any", "lower", head).Select(t => t.Id).ToArray());
            Assert.AreEqual(2, SurveyPlanner.Plan(SceneRoot, "any", "visible", head).Count);
            Assert.AreEqual(0, SurveyPlanner.Plan(SceneRoot, "any", "visible", new Pose(head.position, Quaternion.LookRotation(Vector3.forward))).Count);
            // Upper / lower on an uneven kitchen: 4 wall cabinets, 10 base cabinets split at the gap, not the median.
            var heights = Enumerable.Repeat(1.8f, 4).Concat(Enumerable.Repeat(0.5f, 10)).ToList();
            float split = SurveyPlanner.Split(heights);
            Assert.That(split, Is.GreaterThan(0.5f).And.LessThan(1.8f));
        }

        [Test]
        public void CheckSlope_GutterEdge_IsLevel_OverTheRun()
        {
            // The gutter's front lip top edge (y 6.100, z 0.152), the server's first edge id.
            int gutter = Array.FindIndex(Layer.Edges, e => Mathf.Abs(e.a.y - 6.1f) < 1e-3f && Mathf.Abs(e.b.y - 6.1f) < 1e-3f
                                                           && Mathf.Abs(e.a.z - 0.152f) < 1e-3f && Mathf.Abs(e.b.z - 0.152f) < 1e-3f && e.Length > 4f);
            Assert.GreaterOrEqual(gutter, 0, "fixture gutter edge");
            string id = Layer.Edges[gutter].id;
            Assert.IsTrue(Runner.CheckSlope("gutter", "sl-test", new List<string> { "nope", id }, Head));
            Assert.AreEqual(id, Runner.LastSlopeEdge, "the first id the layer has");
            var j = Runner.LastReport;
            Assert.AreEqual("slope_result", (string)j["kind"]);
            Assert.AreEqual("sl-test", (string)j["request_id"]);
            Assert.AreEqual("gutter", (string)j["target"]);
            Assert.AreEqual(S.GutterRun, (double)j["run_m"], 0.002);
            Assert.AreEqual(0.0, (double)j["fall_mm"], 0.5);
            Assert.AreEqual(3, ((JArray)j["low_end"]).Count);
            Assert.AreEqual(Notebook.Last.Id, (int)j["notebook_id"]);
            Assert.AreEqual(2, Notebook.Last.Points.Length, "a 2-point tape");
            StringAssert.StartsWith("gutter slope", Notebook.Last.Label);
            // Without ids the app picks a long level edge itself.
            Assert.IsTrue(Runner.CheckSlope("gutter", "sl-test2", new List<string>(), Head));
            Assert.AreEqual(0.0, (double)Runner.LastReport["fall_mm"], 0.5);
        }

        [Test]
        public void SlopeNumbers()
        {
            var s = SlopeProbe.Measure(new Vector3(-2.1f, 6.1f, 0.152f), new Vector3(2.1f, 6.1084f, 0.152f));
            Assert.AreEqual(4.2, s.runM, 1e-4);
            Assert.AreEqual(8.4, s.fallMm, 0.05);
            Assert.AreEqual(-2.1f, s.lowEnd.x, 1e-5f);
        }
    }

    /// The label diet (UX W1.9, coordinator add-on): survey objects draw one "W × H" label; at most 12 stay labelled,
    /// amber first, then the focus, then the most recent; the user's own shapes keep their full labels.
    public class SurveyLabelTests : FacadeToolFixture
    {
        /// D4 (SPEC §9): Line mode saves a tape at its 2nd point, so shapes and explicit finishes happen in Area mode.
        [SetUp] public void ShapesUseAreaMode() => Tool.AreaMode = true;

        MeasureShape SurveyQuad(int i, bool unverified = false)
        {
            float x = -3.8f + (i % 5) * 0.5f, y = 1.6f + (i / 5) * 0.5f;
            foreach (var p in new[] { new Vector3(x, y, 0f), new Vector3(x + 0.3f, y, 0f), new Vector3(x + 0.3f, y + 0.25f, 0f), new Vector3(x, y + 0.25f, 0f) })
                Tool.Click(new SurfaceHit { point = p, kind = SnapKind.Corner });
            var s = Tool.Finish(new SurveyTag { RequestId = "sv-labels", ObjectId = $"o{i}", Label = "panel", Unverified = unverified });
            Assert.IsNotNull(s);
            return s;
        }

        [Test]
        public void FifteenObjects_AtMostTwelveLabels_AmberAndFocusKeepTheirs()
        {
            var shapes = new List<MeasureShape>();
            for (int i = 0; i < 15; i++) shapes.Add(SurveyQuad(i, unverified: i == 1));
            Assert.AreEqual("300 × 250 mm", shapes[0].Entry.DisplayValue);
            foreach (var s in shapes) Assert.LessOrEqual(s.View.ActiveLabelCount, 1, "one compact label per object");
            Assert.LessOrEqual(Tool.VisibleLabelCount, 12);
            Assert.AreEqual(12, shapes.Count(s => s.View.ActiveLabelCount == 1));
            Assert.AreEqual(1, shapes[1].View.ActiveLabelCount, "amber keeps its label although it's old");
            StringAssert.Contains("300 × 250 mm", shapes[1].View.Labels[0].Text);
            StringAssert.StartsWith("⚠", shapes[1].View.Labels[0].Text);
            Assert.AreEqual(1, shapes[14].View.ActiveLabelCount, "the most recent is labelled");
            Assert.AreEqual(0, shapes[0].View.ActiveLabelCount, "the oldest verified one collapses to its outline");
            Tool.FocusSurvey(new[] { "o0" });
            Assert.AreEqual(1, shapes[0].View.ActiveLabelCount, "the focus gets its label back");
            Assert.LessOrEqual(Tool.VisibleLabelCount, 12);
            // A hand-measured quad keeps the full set (4 sides + 4 angles + area).
            foreach (var p in new[] { new Vector3(1f, 1f, 0f), new Vector3(1.5f, 1f, 0f), new Vector3(1.5f, 1.5f, 0f), new Vector3(1f, 1.5f, 0f) })
                Tool.Click(new SurfaceHit { point = p, kind = SnapKind.Corner });
            var hand = Tool.Finish();
            Assert.AreEqual(9, hand.View.ActiveLabelCount);
        }

        [Test]
        public void SurveyCard_FromTheContractReply()
        {
            var args = JObject.Parse(@"{""request_id"": ""sv-0c45d47f"", ""label"": ""cabinet_door"",
               ""groups"": [{""w_mm"": 262, ""h_mm"": 278, ""count"": 2, ""ids"": [""o0"", ""o1""]},
                          {""w_mm"": 208, ""h_mm"": 525, ""count"": 1, ""ids"": [""o6""]}],
               ""unverified"": [""o1""], ""skipped"": [""o9""], ""focus"": [""o6""]}");
            var c = SurveyCard.Compose(args);
            Assert.AreEqual("3 cabinet doors", c.title);
            Assert.AreEqual("2 sizes", c.subtitle);
            var rows = Regex.Replace(c.body, "<[^>]*>", "").Split('\n');
            Assert.AreEqual(2, rows.Length);
            StringAssert.StartsWith("2 × 262 × 278 mm", rows[0]);
            StringAssert.StartsWith("→ 1 × 208 × 525 mm", rows[1], "the focus is marked");
            StringAssert.Contains(ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.warning) + ">o1<", c.body, "amber id");
            var foot = Regex.Replace(c.footer, "<[^>]*>", "");
            StringAssert.Contains("1 didn't lock onto its corners", foot);
            StringAssert.Contains("1 skipped", foot);
        }
    }
}
