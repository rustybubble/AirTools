using System;
using System.Collections.Generic;
using System.Globalization;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// Grok lane G1 (context + core): pure logic, runnable without the Editor. Example payloads are copied from the
    /// backend's own tests (integration/grok-all tests/test_finish.py, test_safety.py, test_postcard.py,
    /// test_survey.py, test_rules.py) and its kitchen scene package (cameras.r1.json).
    public class GrokContextTests
    {
        // Two real kitchen cameras (backend scene/kitchen/cameras.r1.json, ids 0001 and 0201).
        const string Kitchen0001 = @"{""id"": ""0001"", ""file"": ""frames/0001.jpg"", ""thumb"": ""thumbs/0001.jpg"", ""R"": [[-0.015007159013118691, 0.0006782913539031022, -0.9998871561827335], [-0.012508652969073365, -0.9999216433990388, -0.000490574219418917], [-0.9998091411760818, 0.01249987931960492, 0.015014467614306236]], ""t"": [-0.9402422604471028, -0.3970671522387847, -0.38472469202429194], ""position"": [-0.4037284042481969, -0.3915892689889705, -0.9345545144007225], ""fx"": 1457.6137201326615, ""fy"": 1457.6137201326615, ""cx"": 960.0, ""cy"": 539.5, ""w"": 1920, ""h"": 1079}";
        const string Kitchen0201 = @"{""id"": ""0201"", ""file"": ""frames/0201.jpg"", ""thumb"": ""thumbs/0201.jpg"", ""R"": [[-0.0272776325610112, -0.0006942839232801989, -0.9996276550454676], [0.2361666658054174, -0.9716954347278867, -0.005769583289570596], [-0.9713296231065016, -0.23623611091197905, 0.026669517749002725]], ""t"": [-0.11827513311625434, -0.16502668583632515, -0.5801583267334582], ""position"": [-0.5277774323074754, -0.2974921405795902, -0.10371068638407664], ""fx"": 1457.6137201326615, ""fy"": 1457.6137201326615, ""cx"": 960.0, ""cy"": 539.5, ""w"": 1920, ""h"": 1079}";

        static SceneCameraJson Cam(string json) => JsonConvert.DeserializeObject<SceneCameraJson>(json);

        /// A camera at the glTF origin's +Z side looking down −Z... simplest: R = I, t = (0, 0, 5): x_cam = x + (0, 0, 5).
        static SceneCameraJson Straight() => new SceneCameraJson
        {
            id = "0007", thumb = "thumbs/0007.jpg",
            R = new[] { new[] { 1.0, 0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, 0, 1.0 } },
            t = new[] { 0.0, 0, 5 }, fx = 1000, fy = 1000, cx = 640, cy = 360, w = 1280, h = 720,
        };

        [Test]
        public void Pointer_IsInTheStructureFrame_WithTheXFlipUndone()
        {
            var unity = new Vector3(1.5f, 2.25f, -3.125f);
            var p = GrokContext.ToStructureFrame(unity);
            CollectionAssert.AreEqual(new[] { -1.5, 2.25, -3.125 }, p);
            // Back through the app's own flip: the same Unity point.
            Assert.AreEqual(unity, GltfFrame.ToUnity(p[0], p[1], p[2]));
            // Rounded to the millimetre.
            CollectionAssert.AreEqual(new[] { -0.123, 0.0, 1.0 }, GrokContext.ToStructureFrame(new Vector3(0.12345f, 0.00004f, 0.99996f)));
        }

        [Test]
        public void PointInFrame_ProjectsThroughPoseAndIntrinsics()
        {
            var cam = Straight();
            var centre = GrokContext.PointInFrame(cam, Vector3.zero);
            Assert.AreEqual(0.5f, centre[0], 1e-4f);
            Assert.AreEqual(0.5f, centre[1], 1e-4f);
            // glTF +X is Unity −X: glTF (1, 0, 0) → u = 640 + 1000·1/5 = 840 → 0.65625 (right of centre).
            var right = GrokContext.PointInFrame(cam, new Vector3(-1f, 0f, 0f));
            Assert.AreEqual(840f / 1280f, right[0], 1e-4f);
            Assert.AreEqual(0.5f, right[1], 1e-4f);
            // OpenCV image y points down, and with R = I the camera's y axis is the world's +Y: +Y lands below the centre.
            Assert.AreEqual((360f + 1000f / 5f) / 720f, GrokContext.PointInFrame(cam, new Vector3(0f, 1f, 0f))[1], 1e-4f);
            // Behind the camera, or outside the picture: nothing.
            Assert.IsNull(GrokContext.PointInFrame(cam, new Vector3(0f, 0f, -6f)));
            Assert.IsNull(GrokContext.PointInFrame(cam, new Vector3(-50f, 0f, 0f)));
        }

        [Test]
        public void DrillPoint_ClampsToThePhoto_LikeG4sDrillPxIn()
        {
            var cam = Straight();
            var edge = GrokContext.PointInFrame(cam, new Vector3(-50f, 0f, 0f), clamp: true);
            Assert.AreEqual(1f, edge[0], 1e-6f);
            Assert.AreEqual(0.5f, edge[1], 1e-4f);
            Assert.IsNull(GrokContext.PointInFrame(cam, new Vector3(0f, 0f, -6f), clamp: true), "behind the camera");
        }

        [Test]
        public void OtherLanes_Missing_MeanNothingKnown()
        {
            // This lane builds alone: G2 / G3 / G4's GrokState members are looked up by name. Where they're absent,
            // nothing is known and nothing throws. (Merged, the same calls reach their fields.)
            bool g4 = typeof(GrokState).GetField("CoachActive") != null;
            if (!g4)
            {
                Assert.IsFalse(GrokLanes.CoachActive);
                Assert.IsFalse(GrokLanes.DrillActive);
                Assert.IsNull(GrokLanes.DrillPxIn(Straight()));
                GrokLanes.LastFrameId = "0021";
                Assert.IsNull(GrokLanes.LastFrameId);
            }
            if (!GrokLanes.HasSafety)
            {
                Assert.IsNull(GrokLanes.Verdict("p"));
                Assert.IsFalse(GrokLanes.IsRecalled("p"));
                Assert.IsNull(GrokLanes.Headline("p"));
                Assert.DoesNotThrow(() => GrokLanes.SetSafety("p", "recalled", "h"));
                Assert.IsFalse(GrokLanes.OnSafetyChanged(_ => { }, true));
            }
            if (typeof(GrokState).GetField("SurveyId") == null && typeof(GrokState).GetProperty("SurveyId") == null)
                Assert.IsNull(GrokLanes.SurveyId);
        }

        [Test]
        public void PointInFrame_IsTheInverseOfPixelRay_OnARealKitchenCamera()
        {
            foreach (var json in new[] { Kitchen0001, Kitchen0201 })
            {
                var cam = Cam(json);
                foreach (var (x, y) in new[] { (0.3f, 0.6f), (0.5f, 0.5f), (0.9f, 0.1f) })
                {
                    var ray = SceneCameras.PixelRay(cam, x, y);
                    var uv = GrokContext.PointInFrame(cam, ray.origin + ray.direction * 1.7f);
                    Assert.IsNotNull(uv, $"{cam.id} ({x}, {y})");
                    Assert.AreEqual(x, uv[0], 2e-4f, $"{cam.id} x");
                    Assert.AreEqual(y, uv[1], 2e-4f, $"{cam.id} y");
                }
            }
        }

        [Test]
        public void PlacedBox_IsTheProjectedBoundsClippedToThePhoto()
        {
            var cam = Straight();
            // A 0.4 m cube centred on the axis 5 m out: symmetric about the centre.
            var corners = GrokContext.Corners(new Bounds(Vector3.zero, Vector3.one * 0.4f));
            Assert.AreEqual(8, corners.Length);
            var box = GrokContext.BoxInFrame(cam, corners);
            Assert.IsNotNull(box);
            Assert.AreEqual(0.5f - (box[2] - 0.5f), box[0], 1e-3f);
            Assert.AreEqual(0.5f - (box[3] - 0.5f), box[1], 1e-3f);
            Assert.Less(box[0], box[2]); Assert.Less(box[1], box[3]);
            // Near face (z = −0.2 → 4.8 m) sets the extent: 1000 · 0.2 / 4.8 px each side.
            Assert.AreEqual((640f + 1000f * 0.2f / 4.8f) / 1280f, box[2], 1e-3f);
            // Half out of the picture: clipped to it.
            var edge = GrokContext.BoxInFrame(cam, GrokContext.Corners(new Bounds(new Vector3(-3.2f, 0f, 0f), Vector3.one * 0.4f)));
            Assert.AreEqual(1f, edge[2], 1e-6f);
            Assert.Less(edge[0], 1f);
            // Entirely outside, or behind the camera: nothing.
            Assert.IsNull(GrokContext.BoxInFrame(cam, GrokContext.Corners(new Bounds(new Vector3(-40f, 0f, 0f), Vector3.one * 0.4f))));
            Assert.IsNull(GrokContext.BoxInFrame(cam, GrokContext.Corners(new Bounds(new Vector3(0f, 0f, -5f), Vector3.one * 0.4f))));
        }

        [Test]
        public void PickView_PrefersPhotosThatSeeTheFocus_ElseNearestToTheHead()
        {
            var a = Straight(); a.id = "a";                               // at glTF (0, 0, −5), looking +Z
            var b = Straight(); b.id = "b"; b.t = new[] { 0.0, 0, -5 };   // at glTF (0, 0, 5), looking +Z (away)
            var cams = new List<SceneCameraJson> { b, a };
            // The origin is seen only by "a", even though "b" is as close.
            CollectionAssert.AreEqual(new[] { 1 }, SceneCameras.PickView(cams, Vector3.zero, new Vector3(0, 0, 4.9f), 3));
            // No focus: the photo taken nearest to the head (package Unity (0, 0, 4.9) ≈ glTF (0, 0, 4.9) → "b").
            CollectionAssert.AreEqual(new[] { 0, 1 }, SceneCameras.PickView(cams, null, new Vector3(0, 0, 4.9f), 3));
            Assert.IsEmpty(SceneCameras.PickView(new List<SceneCameraJson>(), null, Vector3.zero, 1));
        }

        static ContextSnapshot Full() => new ContextSnapshot
        {
            Measurement = new Dictionary<string, object> { ["label"] = "tape #3", ["value_m"] = 3.66, ["axis"] = "length" },
            SelectedPartId = "gutter-hanger-hidden-k-style",
            CandidateIds = new List<string> { "gutter-hanger-hidden-k-style", "gutter-hanger-spike-ferrule" },
            Placed = new List<KeyValuePair<string, int>>
            {
                new KeyValuePair<string, int>("gutter-hanger-hidden-k-style", 8),
                new KeyValuePair<string, int>("gutter-hanger-hidden-k-style", 8),   // one row per placed piece: deduplicated
                new KeyValuePair<string, int>("downspout-strap", 0),               // counted at least once
            },
            Tool = "tape",
            Site = "kitchen", Scale = 1.0234567, SceneInView = true,
            FrameId = "0481", FrameJpgB64 = "AAAA",
            PointerPackage = new Vector3(1.36f, 0.15f, -0.12f),
            PlacedBox = new[] { 0.23f, 0.58f, 0.61f, 0.73f },
            DrillPx = new[] { 0.5f, 0.42f },
            Location = "  Atlanta, GA ", Address = "225 North Ave NW, Atlanta, GA 30332",
            Placement = "replacing the steel sink", SurveyId = "84e27743a1c9",
        };

        [Test]
        public void Context_FromASnapshot_HasEveryApiField()
        {
            var ctx = JObject.Parse(JsonConvert.SerializeObject(GrokContext.Build(Full())));
            Assert.AreEqual(3.66, (double)ctx["measurement"]["value_m"], 1e-9);
            Assert.AreEqual("gutter-hanger-hidden-k-style", (string)ctx["selected_part_id"]);
            Assert.AreEqual(2, ((JArray)ctx["candidate_ids"]).Count);
            var placed = (JArray)ctx["placed"];
            Assert.AreEqual(2, placed.Count);
            Assert.AreEqual(8, (int)placed[0]["count"]);
            Assert.AreEqual("downspout-strap", (string)placed[1]["part_id"]);
            Assert.AreEqual(1, (int)placed[1]["count"]);
            Assert.AreEqual("tape", (string)ctx["tool"]);
            Assert.AreEqual("kitchen", (string)ctx["site"]);
            Assert.AreEqual(1.02346, (double)ctx["scale"], 1e-9);
            Assert.AreEqual("0481", (string)ctx["frame_id"]);
            Assert.AreEqual("AAAA", (string)ctx["frame_jpg_b64"]);
            CollectionAssert.AreEqual(new[] { 0.23f, 0.58f, 0.61f, 0.73f }, ctx["placed_box"].ToObject<float[]>());
            CollectionAssert.AreEqual(new[] { 0.5f, 0.42f }, ctx["drill_px"].ToObject<float[]>());
            CollectionAssert.AreEqual(new[] { -1.36, 0.15, -0.12 }, ctx["pointer"].ToObject<double[]>());
            Assert.AreEqual("Atlanta, GA", (string)ctx["location"]);
            Assert.AreEqual("225 North Ave NW, Atlanta, GA 30332", (string)ctx["address"]);
            Assert.AreEqual("replacing the steel sink", (string)ctx["placement"]);
            Assert.AreEqual("84e27743a1c9", (string)ctx["survey_id"]);
        }

        [Test]
        public void Context_InPassthrough_SendsTheSiteButNoView()
        {
            var s = Full();
            s.SceneInView = false;
            s.Frames = new List<Dictionary<string, string>> { new Dictionary<string, string> { ["id"] = "0001", ["jpg_b64"] = "AAAA" } };
            var ctx = GrokContext.Build(s);
            Assert.AreEqual("kitchen", ctx["site"]);
            foreach (var key in new[] { "frame_id", "frame_jpg_b64", "placed_box", "drill_px", "pointer", "frames" })
                Assert.IsFalse(ctx.ContainsKey(key), key);
            Assert.AreEqual("gutter-hanger-hidden-k-style", ctx["selected_part_id"]);
        }

        [Test]
        public void Context_OnTheBuiltInScene_HasNoSiteOrView_AndDefaultsTheLocation()
        {
            var s = Full();
            s.Site = null; s.Address = " "; s.Location = null; s.Placement = null; s.SurveyId = null;
            var ctx = GrokContext.Build(s);
            foreach (var key in new[] { "site", "scale", "frame_id", "frame_jpg_b64", "placed_box", "drill_px", "pointer", "address", "placement", "survey_id" })
                Assert.IsFalse(ctx.ContainsKey(key), key);
            Assert.AreEqual("Atlanta, GA", ctx["location"]);
            Assert.AreEqual(GrokState.DefaultLocation, ctx["location"]);
        }

        [Test]
        public void Context_FrameId_FallsBackToTheFirstFrameSent_AndBadBoxesAreDropped()
        {
            var s = Full();
            s.FrameId = null;
            s.PlacedBox = new[] { 0.6f, 0.5f, 0.2f, 0.7f };   // x1 < x0: the server would 400 the postcard
            s.DrillPx = null; s.PointerPackage = null; s.FrameJpgB64 = null;
            s.Frames = new List<Dictionary<string, string>> { new Dictionary<string, string> { ["id"] = "0201", ["jpg_b64"] = "BBBB" } };
            var ctx = GrokContext.Build(s);
            Assert.AreEqual("0201", ctx["frame_id"]);
            Assert.AreSame(s.Frames, ctx["frames"]);
            foreach (var key in new[] { "placed_box", "drill_px", "pointer", "frame_jpg_b64" }) Assert.IsFalse(ctx.ContainsKey(key), key);
            // No photo at all: no frame_id, and the pieces that live in its frame go with it.
            s.Frames = null; s.PlacedBox = new[] { 0.1f, 0.1f, 0.2f, 0.2f }; s.DrillPx = new[] { 0.3f, 0.3f };
            ctx = GrokContext.Build(s);
            Assert.IsFalse(ctx.ContainsKey("frame_id"));
            Assert.IsFalse(ctx.ContainsKey("placed_box"));
            Assert.IsFalse(ctx.ContainsKey("drill_px"));
        }

        [TestCase("what am I looking at?", false, true)]
        [TestCase("Label this", false, true)]
        [TestCase("what's this stuff", false, true)]
        [TestCase("check this quote", false, true)]
        [TestCase("can you look over this estimate for me", false, true)]
        [TestCase("read me the quote from the manual", false, false)]
        [TestCase("check it", false, true)]
        [TestCase("okay, check my work", false, true)]
        [TestCase("done", true, true)]
        [TestCase("done", false, false)]
        [TestCase("find me a K-style gutter hanger", false, false)]
        [TestCase("check the rebates", true, false)]
        [TestCase("", true, false)]
        public void NeedsFrame_OnlyForTheCommandsThatReadIt(string text, bool coach, bool expected) =>
            Assert.AreEqual(expected, GrokContext.NeedsFrame(text, coach));
    }

    public class GrokNotebookTests
    {
        static readonly DateTime When = new DateTime(2026, 9, 26, 14, 3, 7, 120, DateTimeKind.Utc);

        /// Timestamps stay strings (JObject.Parse would turn ISO dates into DateTime).
        static JObject Parse(string json) => JsonConvert.DeserializeObject<JObject>(json, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });

        static JArray Entries(params NotebookEntry[] entries) =>
            (JArray)Parse(NotebookExporter.ToJson(entries, "kitchen", "quest-abc"))["entries"];

        [Test]
        public void Measurements_CarryTypeToolValueSiteAndPhoto()
        {
            var tape = new NotebookEntry("measure", 2.41, "m", new[] { Vector3.zero, Vector3.right }, When, 21, "Distance 2.41 m")
                { Site = "kitchen", Quality = "preview", CameraKey = "0021", ThumbPath = "thumbs/0021.jpg" };
            var area = new NotebookEntry("measure", 1.8, "m²", new Vector3[0], When, -1, "Quad 1.80 m²") { Site = "kitchen" };
            var level = new NotebookEntry("level", 0.4, "°", new Vector3[0], When, -1, "Level 0.4°");
            var e = Entries(tape, area, level);
            Assert.AreEqual("measurement", (string)e[0]["type"]);
            Assert.AreEqual("tape", (string)e[0]["tool"]);
            Assert.AreEqual(2.41, (double)e[0]["value_m"], 1e-9);
            Assert.AreEqual("kitchen", (string)e[0]["site"]);
            Assert.AreEqual("0021", (string)e[0]["nearest_camera_id"]);
            Assert.AreEqual("thumbs/0021.jpg", (string)e[0]["thumb"]);
            Assert.AreEqual("preview", (string)e[0]["quality"]);
            Assert.AreEqual("Distance 2.41 m", (string)e[0]["label"]);
            Assert.AreEqual("area", (string)e[1]["tool"]);
            Assert.IsNull(e[1]["value_m"]);
            Assert.AreEqual(1.8, (double)e[1]["value"], 1e-9);
            Assert.AreEqual("m²", (string)e[1]["unit"]);
            Assert.AreEqual("level", (string)e[2]["tool"]);
            Assert.AreEqual("°", (string)e[2]["unit"]);
            Assert.IsNull(e[2]["site"], "the built-in scene has no site");
            // The app's own fields stay (older readers of the upload).
            foreach (var key in new[] { "id", "value_si", "unit", "label", "text", "time", "camera_id", "points", "sides_m", "angles_deg" })
                Assert.IsNotNull(e[0][key], key);
            Assert.AreEqual(21, (int)e[0]["camera_id"]);
        }

        [Test]
        public void Placements_CarryPartCountAndWhere_ArraysCountTheirCopies()
        {
            var part = new NotebookEntry("part", 0, "", new[] { Vector3.one }, When, -1, "Hidden hanger: fits")
                { PartId = "gutter-hanger-hidden-k-style", Count = 1, Where = "on the fascia" };
            var array = new NotebookEntry("array", 8, "pcs", new Vector3[0], When, -1, "Array: 8 × hanger")
                { PartId = "gutter-hanger-hidden-k-style", Count = 7, Total = 8, Where = "every 600 mm along 4.20 m on the fascia" };
            var orphan = new NotebookEntry("part", 0, "", new Vector3[0], When, -1, "no id");
            var e = Entries(part, array, orphan);
            Assert.AreEqual("placement", (string)e[0]["type"]);
            Assert.AreEqual("gutter-hanger-hidden-k-style", (string)e[0]["part_id"]);
            Assert.AreEqual(1, (int)e[0]["count"]);
            Assert.AreEqual("on the fascia", (string)e[0]["where"]);
            Assert.AreEqual("placement", (string)e[1]["type"]);
            Assert.AreEqual(7, (int)e[1]["count"]);
            Assert.AreEqual(8, (int)e[1]["total"]);
            // What the report sums per part (server/report.py assemble): the run's count.
            Assert.AreEqual(8, (int)e[0]["count"] + (int)e[1]["count"]);
            Assert.AreEqual("part", (string)e[2]["type"], "no part id: not a placement the report could count");
        }

        [Test]
        public void Notes_AreSharedOnlyWhenMarked()
        {
            var shared = new NotebookEntry("note", 0, "", new Vector3[0], When, -1, "Pin f1 (gutter, severe): sagging") { Share = true, PinId = "f1", FrameId = "0001" };
            var priv = new NotebookEntry("note", 0, "", new Vector3[0], When, -1, "Customer wants black");
            var e = Entries(shared, priv);
            Assert.AreEqual("note", (string)e[0]["type"]);
            Assert.AreEqual("Pin f1 (gutter, severe): sagging", (string)e[0]["text"]);
            Assert.IsTrue((bool)e[0]["share"]);
            Assert.AreEqual("f1", (string)e[0]["pin_id"]);
            Assert.AreEqual("0001", (string)e[0]["frame_id"]);
            Assert.IsNull(e[1]["share"], "private by default (the packet leaves it out)");
        }

        [Test]
        public void Orders_StayPurchases_WithThePostcardLabelled()
        {
            var buy = new NotebookEntry("purchase", 17.3, "USD", new Vector3[0], When, -1, "Bought 10 × hanger") { PostcardUrl = "/parts/p/postcard.jpg?v=1" };
            var e = Entries(buy);
            Assert.AreEqual("purchase", (string)e[0]["type"], "the server writes the order entry itself");
            Assert.AreEqual("/parts/p/postcard.jpg?v=1", (string)e[0]["postcard_url"]);
            Assert.AreEqual("AI preview, not to scale", (string)e[0]["postcard_label"]);
        }

        [Test]
        public void Timestamps_AreUtcIso_AndScanLoadedGoesFirst()
        {
            Assert.AreEqual("2026-09-26T14:03:07.120+00:00", NotebookExporter.Timestamp(When));
            var local = new DateTime(2026, 9, 26, 10, 3, 7, 120, DateTimeKind.Local);
            Assert.AreEqual(local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + "+00:00", NotebookExporter.Timestamp(local));
            var scan = NotebookExporter.ScanLoadedJson(When, "zabel-gymnasium", 3);
            var doc = Parse(NotebookExporter.ToJson(new[] { new NotebookEntry("note", 0, "", new Vector3[0], When, -1, "hi") }, "site", "quest-abc", new[] { scan }));
            var first = doc["entries"][0];
            Assert.AreEqual("scan_loaded", (string)first["type"]);
            Assert.AreEqual("2026-09-26T14:03:07.120+00:00", (string)first["ts"]);
            Assert.AreEqual("zabel-gymnasium", (string)first["scan"]);
            Assert.AreEqual(3, (int)first["revision"]);
            Assert.IsNull(first["site"], "the report's scene comes from the first entry with a site: a reading's");
            Assert.AreEqual("2026-09-26T14:03:07.120+00:00", (string)doc["entries"][1]["ts"]);
            Assert.AreEqual("quest-abc", (string)doc["session_id"]);
        }

        [TestCase("Pin f1 (gutter, severe): sagging", "f1", null, true)]
        [TestCase("Tell the contractor the fascia is soft", null, null, true)]
        [TestCase("Customer wants the black sink", null, null, false)]
        [TestCase("Customer wants the black sink", null, true, true)]
        [TestCase("Pin f2: hail", "f2", false, false)]
        public void AddNote_SharesSurveyFindingsAndNotesForTheContractor(string text, string pin, bool? share, bool expected) =>
            Assert.AreEqual(expected, G1Actions.Shareable(text, pin, share));
    }

    public class GrokCoreTests
    {
        static PartSpec Part(params string[] finishes)
        {
            var spec = new PartSpec { id = "shower", name = "Shower head", dims_mm = new PartDims { w = 100, d = 50, h = 100 } };
            foreach (var f in finishes) spec.finishes.Add(new PartFinish { name = f, hex = "#000000" });
            return spec;
        }

        [Test]
        public void SetFinish_Payloads_FromTheBackend()
        {
            // tests/test_finish.py test_agent_set_finish_is_server_side / _tints_without_a_render
            var render = JsonConvert.DeserializeObject<AgentReply>(@"{""reply"": ""Here it is in matte black. That's not a listed finish, so check the seller has it."",
                ""actions"": [{""name"": ""set_finish"", ""args"": {""name"": ""matte black"", ""model_url"": ""/parts/shower/model-matte-black.glb"", ""label"": ""Not a listed finish""}}]}");
            var a = render.actions[0];
            Assert.AreEqual("matte black", a.Str("name"));
            Assert.AreEqual("/parts/shower/model-matte-black.glb", a.Str("model_url"));
            Assert.AreEqual("Not a listed finish", a.Str("label"));
            var tint = JsonConvert.DeserializeObject<AgentReply>(@"{""reply"": ""Tinted it chrome."", ""actions"": [{""name"": ""set_finish"", ""args"": {""name"": ""chrome""}}]}");
            Assert.IsNull(tint.actions[0].Str("model_url"));
            Assert.IsNull(tint.actions[0].Str("label"));
        }

        [Test]
        public void FinishNames_MatchTheListingLoosely()
        {
            Assert.AreEqual("Matte Black", GrokFinish.Match(Part("White", "Matte Black"), "black"));
            Assert.AreEqual("Black", GrokFinish.Match(Part("White", "Black"), "matte black"));
            Assert.AreEqual("White", GrokFinish.Match(Part("Matte White", "White"), "WHITE"), "exact first");
            Assert.AreEqual("Brushed Nickel", GrokFinish.Match(Part("Brushed Nickel"), "brushed-nickel"));
            Assert.IsNull(GrokFinish.Match(Part("White"), "navy"));
            Assert.IsNull(GrokFinish.Match(Part(), "black"));
            Assert.IsNull(GrokFinish.Match(Part("White"), " "));
        }

        [Test]
        public void AssetTiers_HaveHonestLabels()
        {
            Assert.AreEqual("AI-made model · exact size", Copy.TierLabel("llm"));
            Assert.AreEqual("AI-made model · exact size", Copy.TierLabel("scad"));
            Assert.AreEqual("AI mesh · exact size, approximate look", Copy.TierLabel("ai_mesh"));
            Assert.AreEqual("maker's 3D model", Copy.TierLabel("cad"));
            Assert.AreEqual("exact-size box", Copy.TierLabel("proxy"));
            var p = PartSpec.Parse(@"{""id"":""x"",""name"":""X"",""dims_mm"":{""w"":1,""d"":1,""h"":1},""asset"":{""tier"":""scad"",""status"":""ready""}}");
            Assert.AreEqual("scad", p.asset.tier);
            Assert.AreEqual("Model — exact size", p.asset.Badge);
        }

        [Test]
        public void Receipts_CarryTheSafetyVerdictAndPostcard()
        {
            // tests/test_safety.py test_checkout_receipt_carries_the_verdict + tests/test_postcard.py (api.md receipt)
            var r = PartSpec.ParseAny<CheckoutReceipt>(@"{""status"": ""OFFLINE_RECEIPT"", ""mode"": ""offline"", ""approval_code"": null, ""card_last4"": ""1111"",
                ""total_usd"": 448.0, ""currency"": ""USD"", ""receipt_id"": ""rcpt_a1b2c3d4"", ""part_id"": ""midea-u-shaped"", ""seller"": ""Home Depot"", ""qty"": 1,
                ""unit_price_usd"": 448.0, ""shipping_usd"": 0.0, ""bom_lines"": [], ""safety_verdict"": ""recalled"",
                ""postcard_url"": ""/parts/midea-u-shaped/postcard.jpg?v=1790412901175330649"",
                ""created_at"": ""2026-09-24T18:00:00+00:00"",
                ""label"": ""Offline receipt — no payment was authorized (sandbox not configured)""}");
            Assert.IsTrue(r.Recorded);
            Assert.AreEqual("recalled", r.safety_verdict);
            Assert.AreEqual("/parts/midea-u-shaped/postcard.jpg?v=1790412901175330649", r.postcard_url);
            Assert.AreEqual("Offline receipt — no payment was authorized (sandbox not configured)", r.label, "kept verbatim");
            var extras = CheckoutPanel.ReceiptExtras(r);
            Assert.AreEqual(2, extras.Count);
            StringAssert.Contains("RECALLED", extras[0]);
            StringAssert.Contains("AI preview, not to scale", extras[1]);
            // An older server's receipt: nothing extra.
            var old = PartSpec.ParseAny<CheckoutReceipt>(@"{""status"": ""AUTHORIZED"", ""mode"": ""sandbox"", ""total_usd"": 17.3, ""label"": ""Sandbox authorization""}");
            Assert.IsNull(old.safety_verdict);
            Assert.IsEmpty(CheckoutPanel.ReceiptExtras(old));
            Assert.AreEqual("", Copy.ReceiptSafety("clear"));
            Assert.AreEqual("", Copy.ReceiptSafety("unknown"));
            StringAssert.StartsWith("⚠ Caution", Copy.ReceiptSafety("caution"));
        }

        [Test]
        public void MultiActionReplies_ParseInOrder()
        {
            // tests/test_survey.py test_agent_survey_then_fix_pin: two actions each.
            var survey = JsonConvert.DeserializeObject<AgentReply>(@"{""reply"": ""2 problems found."", ""actions"": [
                {""name"": ""survey_started"", ""args"": {""survey_id"": ""84e27743a1c9""}},
                {""name"": ""show_survey"", ""args"": {""survey_id"": ""84e27743a1c9"", ""pins"": [{""id"": ""f1""}, {""id"": ""f2""}], ""label"": ""AI triage from drone frames, not an inspection""}}]}");
            CollectionAssert.AreEqual(new[] { "survey_started", "show_survey" }, survey.actions.ConvertAll(x => x.name));
            var fix = JsonConvert.DeserializeObject<AgentReply>(@"{""reply"": ""Searching the supply shops."", ""job_id"": ""job9"", ""actions"": [
                {""name"": ""search_started"", ""args"": {""job_id"": ""job9""}},
                {""name"": ""add_note"", ""args"": {""text"": ""Pin f1 (gutter, severe): sagging section"", ""pin_id"": ""f1"", ""frame_id"": ""0001""}}]}");
            Assert.AreEqual("job9", fix.actions[0].Str("job_id"));
            Assert.AreEqual("f1", fix.actions[1].Str("pin_id"));
            Assert.IsTrue(G1Actions.Shareable(fix.actions[1].Str("text"), fix.actions[1].Str("pin_id"), null));
        }

        [Test]
        public void RiderActions_AreCaptionedWithTheReply()
        {
            // tests/test_rules.py test_agent_slow_check_rides_on_the_next_command: show_rules leads "grab the tape".
            var rider = JsonConvert.DeserializeObject<AgentReply>(@"{""reply"": ""Tape's out."", ""actions"": [
                {""name"": ""show_rules"", ""args"": {""spoken"": ""done line"", ""label"": ""Not legal advice; confirm with the permitting office""}},
                {""name"": ""equip_tool"", ""args"": {""tool"": ""tape""}}]}");
            Assert.AreEqual("done line · Tape's out.", GrokReplies.Caption(rider.reply, rider.actions));
            // The fast path's own show_rules: its spoken line IS the reply — nothing extra.
            var own = JsonConvert.DeserializeObject<AgentReply>(@"{""reply"": ""You'll need a permit."", ""actions"": [
                {""name"": ""rules_started"", ""args"": {""check_id"": ""c1""}}, {""name"": ""show_rules"", ""args"": {""spoken"": ""You'll need a permit.""}}]}");
            Assert.IsNull(GrokReplies.Caption(own.reply, own.actions));
            Assert.IsNull(GrokReplies.Caption("ok", new List<AgentAction>()));
            Assert.IsNull(GrokReplies.Caption("ok", null));
            // A non-string spoken never throws.
            var odd = new AgentAction { name = "show_rules", args = JObject.Parse(@"{""spoken"": {""x"": 1}}") };
            Assert.IsNull(GrokReplies.Caption("ok", new List<AgentAction> { odd }));
        }

        [Test]
        public void ZabelScan_CarriesItsCredit()
        {
            Assert.AreEqual("\"Haus Schiller – Zabelgymnasium Gera – Drohnenflug\" by zabelgymnasium, CC BY 3.0, via Wikimedia Commons",
                SceneCredits.For("zabel-gymnasium"));
            Assert.IsNull(SceneCredits.For("kitchen"));
            Assert.IsNull(SceneCredits.For(null));
        }

        static SceneManifest Manifest(int revision, string quality, string frameId, bool? aligned, string up = "[0,1,0]") =>
            SceneManifest.Parse($@"{{""mesh"":{{""file"":""mesh.r{revision}.glb""}},""cameras"":""cameras.r{revision}.json"",""revision"":{revision},""quality"":""{quality}"",""up"":{up},
                ""frame"":{{""id"":""{frameId}"",""aligned_to_preview"":{(aligned.HasValue ? aligned.Value.ToString().ToLowerInvariant() : "null")}}}}}");

        [Test]
        public void Revisions_SwapInPlaceOnlyWhenAligned()
        {
            var r1 = Manifest(1, "preview", "zabel-9f3a", false);
            var r2 = Manifest(2, "full", "zabel-9f3a", true);
            var r3 = Manifest(3, "full", "zabel-9f3a", false);   // re-scaled onto the OSM footprint: its own frame
            Assert.IsTrue(SceneStreamer.SwapsInPlace(r1, r2), "the full revision aligned onto its preview");
            Assert.IsFalse(SceneStreamer.SwapsInPlace(r2, r3), "r3 isn't aligned to r2: a new scene, never a mix");
            Assert.IsTrue(SceneStreamer.SwapsInPlace(r3, Manifest(3, "full", "zabel-9f3a", false)), "Reload of the same revision");
            Assert.IsFalse(SceneStreamer.SwapsInPlace(r1, Manifest(2, "full", "zabel-0000", true)), "another frame id");
            Assert.IsFalse(SceneStreamer.SwapsInPlace(r1, Manifest(2, "full", "zabel-9f3a", true, "[0,0,1]")), "other axes");
            Assert.IsFalse(SceneStreamer.SwapsInPlace(r2, r1), "never back to an older revision in place");
            Assert.IsFalse(SceneStreamer.SwapsInPlace(null, r2));
        }

        [Test]
        public void Calibration_NeverCarriesIntoAnUnalignedRevision()
        {
            CollectionAssert.AreEqual(new[] { "airtools.scale.zabel.f.r3" }, SceneStreamer.CalibrationKeys("zabel", Manifest(3, "full", "f", false)));
            CollectionAssert.AreEqual(new[] { "airtools.scale.zabel.f.r2", "airtools.scale.zabel.f.r1", "airtools.scale.zabel.f" },
                SceneStreamer.CalibrationKeys("zabel", Manifest(2, "full", "f", true)));
            CollectionAssert.AreEqual(new[] { "airtools.scale.kitchen.f.r1", "airtools.scale.kitchen.f" },
                SceneStreamer.CalibrationKeys("kitchen", Manifest(1, "full", "f", false)));
        }

        [Test]
        public void HeadsetCache_KeepsPhotosPerRevision_AndSpotsARepublishedRevision()
        {
            Assert.AreEqual("mesh.r3.glb", SceneStreamer.CacheName("mesh.r3.glb", 3));
            Assert.AreEqual("r3/thumbs/0042.jpg", SceneStreamer.CacheName("thumbs/0042.jpg", 3));
            Assert.AreEqual("thumbs/0042.jpg", SceneStreamer.CacheName("thumbs/0042.jpg", 0));
            string a = @"{""mesh"":{""file"":""mesh.r3.glb"",""triangles"":10},""revision"":3}";
            string b = @"{""mesh"":{""file"":""mesh.r3.glb"",""triangles"":99},""revision"":3}";
            string c = @"{""mesh"":{""file"":""mesh.r4.glb""},""revision"":4}";
            Assert.IsFalse(SceneStreamer.IsRepublished(a, a.Replace(",", ", "), SceneManifest.Parse(a)), "same content, other whitespace");
            Assert.IsTrue(SceneStreamer.IsRepublished(a, b, SceneManifest.Parse(b)), "r3 again with new content");
            Assert.IsFalse(SceneStreamer.IsRepublished(a, c, SceneManifest.Parse(c)), "a new revision: its own files");
            Assert.IsFalse(SceneStreamer.IsRepublished(null, a, SceneManifest.Parse(a)));
        }
    }
}
