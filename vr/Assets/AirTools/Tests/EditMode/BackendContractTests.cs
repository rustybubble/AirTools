using System.Collections.Generic;
using System.IO;
using System.Linq;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Structure;
using AirTools.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// The parts / agent / voice side of the backend contract (airtools-drone-backend docs/api.md), parsed from the
    /// real server's shapes (server/models.py, agent.py, checkout.py).
    public class BackendContractTests
    {
        const string RealPart = @"{""id"":""gutter-hanger-hidden-k-style"",""name"":""Hidden hanger"",""manufacturer"":null,""model_no"":null,
          ""dims_mm"":{""w"":127.0,""d"":38.0,""h"":45.0},""dims_source"":""home_depot"",""weight_g"":null,""color_hex"":""#F2F2F0"",
          ""finish"":""white"",""material"":null,""finishes"":[{""name"":""white"",""hex"":""#F2F2F0"",""part_id"":null}],
          ""mount"":{""face"":""-z"",""surface"":null},""clearance_mm"":{},""spacing_mm"":null,""fit_range_mm"":{""min"":584.0,""max"":914.0},
          ""asset"":{""tier"":""ai_mesh"",""source_url"":null,""license"":null,""scale_residual_pct"":2.1,""status"":""pending""},
          ""image_url"":""https://images.example.com/h.jpg"",""spec_url"":null,""citations"":[],
          ""sellers"":[{""name"":""Home Depot"",""title"":""(50-Pack)"",""price_usd"":54.5,""pack_qty"":50,""unit_price_usd"":1.09,""shipping_usd"":0.0,
                       ""shipping"":""Free delivery"",""total_usd"":54.5,""eta"":""Sep 26"",""eta_days"":1,""rating"":4.6,""reviews"":321,""in_stock"":true,
                       ""url"":""https://www.homedepot.com/p/1"",""verified"":true},
                      {""name"":""eBay"",""title"":null,""price_usd"":null,""pack_qty"":1,""unit_price_usd"":null,""shipping_usd"":null,""shipping"":null,
                       ""total_usd"":null,""eta"":null,""eta_days"":null,""rating"":null,""reviews"":null,""in_stock"":null,""url"":null,""verified"":false}],
          ""sellers_expanded"":false,""recommended_seller"":0,""recommendation_reason"":""cheapest delivered"",
          ""fit"":{""status"":""fits"",""spare_mm"":4.0,""axis"":""w"",""note"":""exact fit""},""fetched_at"":""2026-09-25T10:00:00Z"",""cached"":true}";

        [Test]
        public void MeasurementAxis_FollowsTheTape_VerticalIsH_ShortHorizontalIsW_LongRunIsLength()
        {
            Assert.AreEqual("h", PartsClient.TapeAxis(Vector3.zero, new Vector3(0.05f, 0.9f, 0f)), "a door's height");
            Assert.AreEqual("w", PartsClient.TapeAxis(Vector3.zero, new Vector3(0.262f, 0.004f, 0f)), "a cabinet door's width");
            Assert.AreEqual("w", PartsClient.TapeAxis(Vector3.zero, new Vector3(0f, 0f, 1.5f)), "a window opening");
            Assert.AreEqual("length", PartsClient.TapeAxis(Vector3.zero, new Vector3(4.2f, 0f, 0f)), "a gutter run (api.md's example)");
            Assert.AreEqual("w", PartsClient.TapeAxis(Vector3.zero, new Vector3(0.6f, 0.3f, 0f)), "a sloped short tape is still a width");
        }

        [Test]
        public void RealServerPart_Parses_WithStatusPacksFitAndRange()
        {
            var p = PartSpec.Parse(RealPart);
            Assert.IsFalse(p.asset.Ready);
            Assert.AreEqual("AI mesh — exact size, approximate look", p.asset.Badge);
            Assert.AreEqual(584f, p.min_window_width_mm);
            Assert.AreEqual("fits", p.fit.status);
            Assert.AreEqual(4f, p.fit.spare_mm);
            Assert.AreEqual(50, p.sellers[0].pack_qty);
            Assert.AreEqual(1, p.sellers[0].PacksFor(8), "8 hangers from a 50-pack listing = 1 pack");
            Assert.AreEqual(2, p.sellers[0].PacksFor(51));
            Assert.IsFalse(p.sellers[1].Priced, "a listing with no price");
            Assert.AreEqual(1, p.sellers[0].eta_days);
            Assert.AreEqual(54.5f, SellerSort.Delivered(p.sellers[0]), 1e-4f);
            Assert.AreEqual(0f, SellerSort.Shipping(p.sellers[0]));
            Assert.AreEqual("https://images.example.com/h.jpg", p.ToSummary().remote_image_url);
        }

        [Test]
        public void JobCandidates_AreFullParts_AndOldSummariesStillParse()
        {
            var job = PartSpec.ParseAny<SearchJobStatus>("{\"id\":\"j\",\"status\":\"done\",\"stage\":\"done\",\"summary\":\"One found.\",\"candidates\":[" + RealPart + "]}");
            var c = PartsClient.ParseCandidates(job.candidates);
            Assert.AreEqual(1, c.Count);
            Assert.AreEqual("gutter-hanger-hidden-k-style", c[0].id);
            Assert.IsNotNull(c[0].spec, "the full part rides along");
            Assert.AreEqual(54.5f, c[0].price_usd);
            Assert.AreEqual("/parts/gutter-hanger-hidden-k-style/model.glb", c[0].model_url);
            Assert.AreEqual("One found.", job.summary);
        }

        [Test]
        public void OfflineReceipt_IsRecordedNotAuthorized_WithItsLabel()
        {
            var r = PartSpec.ParseAny<CheckoutReceipt>(@"{""status"":""OFFLINE_RECEIPT"",""mode"":""offline"",""approval_code"":null,""card_last4"":""1111"",
              ""total_usd"":17.3,""currency"":""USD"",""receipt_id"":""rcpt_1"",""part_id"":""x"",""seller"":""Home Depot"",""qty"":1,""unit_price_usd"":17.3,
              ""shipping_usd"":0.0,""bom_lines"":[{""idx"":0,""name"":""Screws"",""qty"":1,""seller"":""Home Depot"",""price_usd"":12.0,""total_usd"":12.0}],
              ""created_at"":""2026-09-24T18:00:00+00:00"",""label"":""Offline receipt — no payment was authorized (sandbox not configured)""}");
            Assert.IsFalse(r.Authorized);
            Assert.IsTrue(r.Recorded);
            Assert.AreEqual("Offline receipt — no payment was authorized (sandbox not configured)", r.label);
            Assert.AreEqual(1, r.bom_lines.Count);
            var ok = PartSpec.ParseAny<CheckoutReceipt>(@"{""status"":""AUTHORIZED"",""mode"":""sandbox"",""label"":""Sandbox authorization""}");
            Assert.IsTrue(ok.Authorized && ok.Recorded);
            Assert.IsFalse(PartSpec.ParseAny<CheckoutReceipt>(@"{""status"":""DECLINED"",""mode"":""sandbox""}").Recorded);
        }

        [Test]
        public void ServerSortNames()
        {
            Assert.AreEqual("cheapest", PartsClient.ServerSort("price"));
            Assert.AreEqual("cheapest", PartsClient.ServerSort("cheapest"));
            Assert.AreEqual("fastest", PartsClient.ServerSort("eta"));
            Assert.AreEqual("fastest", PartsClient.ServerSort("fastest"));
            Assert.AreEqual("best", PartsClient.ServerSort("best"));
        }

        [Test]
        public void AgentReply_ParsesActionsWithRawArgs()
        {
            var r = JsonConvert.DeserializeObject<VoiceReply>(@"{""transcript"":""show sellers cheapest first"",""reply"":""Pulling up sellers."",
              ""audio_b64"":null,""audio_mime"":null,""tts_error"":""no tts"",""job_id"":null,
              ""actions"":[{""name"":""show_sellers"",""args"":{""part_id"":""x"",""sort"":""cheapest""}},
                           {""name"":""place_array"",""args"":{""spacing_mm"":null}},
                           {""name"":""scene_pin"",""args"":{""frame_id"":""0007"",""box"":[0.4,0.3,0.6,0.5]}}]}");
            Assert.AreEqual(3, r.actions.Count);
            Assert.AreEqual("cheapest", r.actions[0].Str("sort"));
            Assert.IsNull(r.actions[1].Float("spacing_mm"));
            CollectionAssert.AreEqual(new[] { 0.4f, 0.3f, 0.6f, 0.5f }, AgentActions.Box(r.actions[2].args["box"]));
        }

        [Test]
        public void EveryContractActionIsHandled_UnknownIsRefused_AndNoActionPays()
        {
            CollectionAssert.AreEquivalent(new[] { "search_started", "select_candidate", "set_finish", "place_array", "show_sellers",
                "start_checkout", "equip_tool", "add_note", "scene_pin", "show_bom",
                "survey", "check_slope", "show_tape_survey", "show_survey", "stop_survey", "show_limits" }, AgentActions.Known);
            var src = File.ReadAllText(Path.Combine(Application.dataPath, "AirTools/Runtime/Agent/AgentActions.cs"));
            foreach (var name in AgentActions.Known) StringAssert.Contains($"case \"{name}\":", src, name);
            StringAssert.DoesNotContain(".Checkout(", src);
            Notebook.Clear();
            Assert.IsFalse(AgentActions.Execute(new AgentAction { name = "launch_rocket" }));
            // add_note works without any scene objects (static notebook).
            Assert.IsTrue(AgentActions.Execute(new AgentAction { name = "add_note", args = JObject.Parse("{\"text\":\"check the downspout\"}") }));
            Assert.AreEqual("check the downspout", Notebook.Last.Label);
            Notebook.Clear();
        }

        [Test]
        public void ShowBom_StoresTheCartLines()
        {
            Notebook.Clear();
            var bom = JObject.Parse(@"{""bom_id"":""bom-1"",""total_usd"":12.0,""lines"":[{""idx"":0,""name"":""Screws"",""qty"":1,""reason"":""fasten"",
              ""seller"":{""name"":""Home Depot"",""price_usd"":12.0,""pack_qty"":100}}]}").ToObject<PartBom>();
            Assert.IsTrue(AgentActions.Execute(new AgentAction { name = "show_bom", args = JObject.FromObject(bom) }));
            Assert.AreEqual("bom-1", AppCommands.CurrentBom.Id);
            Assert.AreEqual("bom", Notebook.Last.Tool);
            Notebook.Clear();
        }

        [Test]
        public void Wav_RoundTrips_16BitMono()
        {
            var samples = Enumerable.Range(0, 1600).Select(i => Mathf.Sin(i * 0.1f) * 0.5f).ToArray();
            var wav = WavUtil.Encode(samples, 1, 16000);
            Assert.AreEqual(44 + 3200, wav.Length);
            Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            var clip = WavUtil.FromWav(wav);
            Assert.AreEqual(16000, clip.frequency);
            Assert.AreEqual(1600, clip.samples);
            var back = new float[1600];
            clip.GetData(back, 0);
            for (int i = 0; i < back.Length; i += 97) Assert.AreEqual(samples[i], back[i], 1e-3f);
            Assert.IsNull(WavUtil.FromWav(new byte[] { 1, 2, 3 }));
            Object.DestroyImmediate(clip);
        }

        [Test]
        public void VoicePicksTheBuiltInMic_NotAirPods()
        {
            Assert.AreEqual("MacBook Pro Microphone", VoiceClient.PickDevice(new[] { "AirPods Pro", "iPhone Microphone", "MacBook Pro Microphone" }));
            Assert.AreEqual("Android audio input", VoiceClient.PickDevice(new[] { "Android audio input" }));
            Assert.AreEqual("USB Mic", VoiceClient.PickDevice(new[] { "AirPods", "USB Mic" }));
            Assert.IsNull(VoiceClient.PickDevice(new string[0]));
        }

        [Test]
        public void NotebookUpload_CarriesTheSessionId()
        {
            Notebook.Clear();
            Notebook.Add(new NotebookEntry("note", 0, "", new Vector3[0], System.DateTime.Now, -1, "hello"));
            var json = JObject.Parse(NotebookExporter.ToJson(Notebook.Entries, "kitchen", "quest-abc"));
            Assert.AreEqual("quest-abc", (string)json["session_id"]);
            Assert.AreEqual(1, ((JArray)json["entries"]).Count);
            Assert.AreEqual("note", (string)json["entries"][0]["type"]);
            Notebook.Clear();
        }

        [Test]
        public void ScaleFactor_Bounds()
        {
            Assert.IsTrue(ScaleCalibration.TryFactor(0.258, 0.380, out var f, out _));
            Assert.AreEqual(1.4729f, f, 1e-3f);
            Assert.IsFalse(ScaleCalibration.TryFactor(0.258, 38.0, out _, out var err), "38 m typed for 38 cm");
            StringAssert.Contains("unit", err);
            Assert.IsFalse(ScaleCalibration.TryFactor(0, 1, out _, out _));
        }
    }

    /// Calibration change → the measure tool's stored shapes and notebook entries follow the scene.
    public class CalibrationSyncTests : FacadeToolFixture
    {
        [Test]
        public void Rescale_MovesTapePointsAndUpdatesTheReading()
        {
            var rootGo = new GameObject("SceneRoot");
            var root = rootGo.AddComponent<SceneRoot>();
            var content = new GameObject("Content");
            content.transform.SetParent(rootGo.transform, false);
            root.SetContent(null, content);
            var sync = rootGo.AddComponent<CalibrationSync>();
            sync.sceneRoot = root;
            Services.Register(Tool);
            try
            {
                Click(new Vector3(-0.75f, 3.5f + 0.6f, -0.05f));
                Click(new Vector3(0.75f, 3.5f + 0.6f, -0.05f));
                Tool.Finish();
                var entry = Notebook.Last;
                Assert.AreEqual(1.5, entry.ValueSI, 0.01);
                sync.OnRescaled(1.47f);
                Assert.AreEqual(1.5 * 1.47, entry.ValueSI, 0.015);
                Assert.AreEqual(1.5 * 1.47, Vector3.Distance(Tool.Shapes[0].Points[0], Tool.Shapes[0].Points[1]), 0.015);
                StringAssert.StartsWith("Distance 2.2", entry.Label);
            }
            finally
            {
                Services.Unregister(Tool);
                Object.DestroyImmediate(rootGo);
            }
        }
    }

    /// Search resilience: a search sent with the tape's measurement that fails (error / 429 / timeout) is retried once
    /// without it (the laptop's warmed cache is keyed on the query alone) before the offline catalog.
    public class SearchRetryTests
    {
        [Test]
        public void MeasuredSearchIsRetriedWithoutTheMeasurement()
        {
            CollectionAssert.AreEqual(new[] { true, false }, PartsClient.SearchAttempts(true));
            CollectionAssert.AreEqual(new[] { false }, PartsClient.SearchAttempts(false), "no tape: one attempt, as before");
        }
    }
}
