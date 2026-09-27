#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Core;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Dev
{
    /// Grok lane G1 checks in the running app (AgentHarness.GrokContext / GrokCheck("g1")). The backend's example
    /// payloads (integration/grok-all tests/) go through AgentActions.ExecuteAll exactly like a reply; results are
    /// [AirTools.Check] G1.* lines. The finish-model swap and the notebook sync finish later: GrokCheck("g1.status").
    /// Never pays: the checkout panel is only opened (and closed) to see the recall line.
    public static class GrokHarnessG1
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly List<string> s_Report = new List<string>();
        static int s_Pass, s_Total;
        static NotebookController.FlushHandle s_Flush;
        static string s_ModelUrl;
        static int s_UploadsBefore;

        /// The context JSON the app would send with a command now (`text`: as if typed; null = a voice command).
        /// frame_jpg_b64 is shortened to its size.
        public static string Context(string text = null)
        {
            var ctx = AgentContext.Build(null, text);
            var json = JObject.FromObject(ctx);
            if (json["frame_jpg_b64"] is JValue b64) json["frame_jpg_b64"] = $"<{((string)b64).Length} base64 chars>";
            return json.ToString(Formatting.None);
        }

        public static string Settings(string location, string address, string placement)
        {
            if (location != null) GrokState.Location = location;
            if (address != null) GrokState.Address = address;
            if (placement != null) GrokState.Placement = placement.Length == 0 ? null : placement;
            return $"location=\"{GrokState.Location}\" address=\"{GrokState.Address}\" placement=\"{GrokState.Placement}\"";
        }

        static void Check(string id, bool ok, string detail)
        {
            s_Total++;
            if (ok) s_Pass++;
            Log.Check(id, ok, detail);
            s_Report.Add($"{id}: {(ok ? "ok" : "FAIL")} {detail}");
        }

        static AgentAction A(string json) => JsonConvert.DeserializeObject<AgentAction>(json);

        public static string Run()
        {
            s_Report.Clear(); s_Pass = s_Total = 0;
            var tool = Services.Get<PartTool>();
            var loader = Services.Get<PartLoader>();
            var root = Services.Get<SceneRoot>();
            var streamer = Services.Get<SceneStreamer>();
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            GrokView.Prefetch();

            // 1. A part to work on: the selected one, else the catalog hanger in the hand.
            var part = tool != null ? (tool.Held != null ? tool.Held : tool.Selected) : null;
            if (part == null && tool != null && loader != null)
            {
                var p = loader.LoadFromCatalog("hidden-hanger-5k");
                if (p != null) { tool.Hold(p); part = p; }
            }
            Check("G1.setup", part != null, $"part={part?.Spec.id} source={part?.Source} site={root?.Site ?? "built-in"} mode={AppState.Mode}");

            // 2. set_finish without model_url: the listed finish, matched loosely ("dark brown" → "brown").
            if (part != null)
            {
                string listed = part.Spec.finishes.Count > 0 ? part.Spec.finishes[part.Spec.finishes.Count - 1].name : null;
                bool tinted = AgentActions.Execute(A($@"{{""name"":""set_finish"",""args"":{{""name"":""dark {listed}""}}}}"));
                Check("G1.set_finish.tint", listed == null || (tinted && part.FinishName == listed), $"listed={listed} finish={part.FinishName} last=\"{G1Actions.LastFinish}\"");
                // A finish the listing doesn't have, labelled: the label shows under the spec card.
                AgentActions.Execute(A(@"{""name"":""set_finish"",""args"":{""name"":""matte black"",""label"":""Not a listed finish""}}"));
                var card = SpecCard.Compose(part, false);
                Check("G1.set_finish.label", part.FinishLabel == "Not a listed finish" && card.sources.StartsWith("Not a listed finish"),
                    $"label=\"{part.FinishLabel}\" under_card=\"{card.sources}\" tier=\"{card.tier}\"");
                // With model_url: the part's own model stands in for a rendered variant (same size + origin contract).
                s_ModelUrl = part.Source == "server" ? $"/parts/{part.Spec.id}/model.glb" : null;
                if (s_ModelUrl != null)
                {
                    bool started = AgentActions.Execute(A($@"{{""name"":""set_finish"",""args"":{{""name"":""matte black"",""model_url"":""{s_ModelUrl}"",""label"":""Not a listed finish""}}}}"));
                    Check("G1.set_finish.model.started", started, $"url={s_ModelUrl} group={tool.FinishGroup(part).Count} (GrokCheck(\"g1.status\") for the swap)");
                }
                else Log.Info("[AirTools.Check] G1.set_finish.model SKIP catalog part (no server model): ServerLoad a part first");
            }

            // 3. Several actions in one reply, one of them unknown: all the others run, in order.
            int before = Notebook.Entries.Count;
            var fix = JsonConvert.DeserializeObject<AgentReply>(@"{""reply"": ""Searching the supply shops."", ""actions"": [
                {""name"": ""add_note"", ""args"": {""text"": ""Pin f1 (gutter, severe): sagging section"", ""pin_id"": ""f1"", ""frame_id"": ""0001""}},
                {""name"": ""launch_rocket"", ""args"": {}},
                {""name"": ""add_note"", ""args"": {""text"": ""Tell the contractor the fascia is soft""}}]}");
            int ran = AgentActions.ExecuteAll(fix.actions, fix.reply);
            var added = Notebook.Entries.Skip(before).ToList();
            Check("G1.actions.multi", ran == 2 && added.Count == 2 && added[0].PinId == "f1" && added[0].Share && added[1].Share
                                      && added[1].Label.StartsWith("Tell the contractor"),
                $"ran={ran}/3 added=[{string.Join(" | ", added.Select(e => $"{e.Label} share={e.Share}"))}]");
            var rider = JsonConvert.DeserializeObject<AgentReply>(@"{""reply"": ""Tape's out."", ""actions"": [
                {""name"": ""show_rules"", ""args"": {""spoken"": ""You'll need an electrical permit."", ""label"": ""Not legal advice; confirm with the permitting office""}},
                {""name"": ""equip_tool"", ""args"": {""tool"": ""tape""}}]}");
            string caption = GrokReplies.Caption(rider.reply, rider.actions);
            Check("G1.actions.rider_caption", caption == "You'll need an electrical permit. · Tape's out.", $"caption=\"{caption}\"");

            // 4. The context the app sends now, for a command that reads the frame.
            var ctx = AgentContext.Build(null, "what am I looking at?");
            bool runtime = root != null && root.IsRuntimePackage && !string.IsNullOrEmpty(root.Site);
            bool view = runtime && AppState.Mode != AppMode.Passthrough;
            bool ctxOk = ctx.ContainsKey("location") && (part == null || ctx.ContainsKey("selected_part_id"))
                         && ctx.ContainsKey("site") == runtime && (!view || ctx.ContainsKey("frame_id"));
            Check("G1.context", ctxOk, $"keys=[{string.Join(",", ctx.Keys)}] frame={Get(ctx, "frame_id")} pointer={Json(ctx, "pointer")} " +
                                       $"placed_box={Json(ctx, "placed_box")} drill_px={Json(ctx, "drill_px")} " +
                                       $"frame_jpg={(ctx.TryGetValue("frame_jpg_b64", out var b) ? ((string)b).Length + " chars" : "none (download started; run again)")}");
            var voiceCtx = AgentContext.Build();
            Check("G1.context.voice_no_frame", !voiceCtx.ContainsKey("frame_jpg_b64"), $"keys=[{string.Join(",", voiceCtx.Keys)}]");

            // 5. Notebook: backend types, then a sync to the session notebook.
            var doc = JsonConvert.DeserializeObject<JObject>(NotebookExporter.ToJson(Notebook.Entries, "site", SessionInfo.Id), new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
            var types = ((JArray)doc["entries"]).Select(e => (string)e["type"]).GroupBy(t => t).Select(g => $"{g.Key}×{g.Count()}");
            bool notesTyped = ((JArray)doc["entries"]).All(e => e["ts"] != null) && ((JArray)doc["entries"]).Where(e => (string)e["tool"] == "note").All(e => (string)e["type"] == "note");
            Check("G1.notebook.types", notesTyped, $"types=[{string.Join(", ", types)}]");
            var nb = Services.Get<NotebookController>();
            s_UploadsBefore = nb != null ? nb.Uploads : 0;
            s_Flush = nb?.Flush();
            Check("G1.notebook.sync.started", nb != null, $"unsent={nb?.HasUnsent} pending_raw={nb?.PendingRaw} (GrokCheck(\"g1.status\") for the upload)");

            // 6. Receipts and the recall line (the panel only opens; nothing pays).
            var receipt = PartSpec.ParseAny<CheckoutReceipt>(@"{""status"": ""OFFLINE_RECEIPT"", ""mode"": ""offline"", ""total_usd"": 448.0, ""part_id"": ""midea-u-shaped"",
                ""safety_verdict"": ""recalled"", ""postcard_url"": ""/parts/midea-u-shaped/postcard.jpg?v=1"",
                ""label"": ""Offline receipt — no payment was authorized (sandbox not configured)""}");
            var extras = CheckoutPanel.ReceiptExtras(receipt);
            Check("G1.receipt.extras", extras.Count == 2 && receipt.label.StartsWith("Offline receipt"), $"extras=[{string.Join(" | ", extras)}]");
            var panel = Services.Get<CheckoutPanel>();
            var client = Services.Get<PartsClient>();
            if (part != null && panel != null && part.Spec.sellers.Count > 0 && !GrokLanes.HasSafety)
                Log.Info("[AirTools.Check] G1.checkout.recall SKIP no G3 safety verdicts in this build (GrokState.VerdictFor)");
            else if (part != null && panel != null && part.Spec.sellers.Count > 0)
            {
                int payments = client != null ? client.CheckoutRequests : 0;
                string oldVerdict = GrokLanes.Verdict(part.Spec.id), oldHeadline = GrokLanes.Headline(part.Spec.id);
                GrokLanes.SetSafety(part.Spec.id, "recalled", "CPSC recall: fire hazard (harness)");
                bool opened = AppCommands.StartCheckout(0);
                string rows = panel.ChecksText();
                Check("G1.checkout.recall", opened && rows.Contains("RECALLED") && client.CheckoutRequests == payments,
                    $"opened={opened} state={panel.State} rows=\"{rows.Replace("\n", " | ")}\" payments={client.CheckoutRequests - payments}");
                panel.Close();
                GrokLanes.SetSafety(part.Spec.id, oldVerdict ?? "unknown", oldHeadline);
            }

            // 7. The scan: loaded as one revision, and its credit on screen.
            var m = streamer != null ? streamer.Manifest : null;
            string rev = m != null ? $".r{m.revision}." : null;
            var files = m == null ? new List<string>() : new List<string> { m.MeshFile, m.CollisionFile, m.cameras, m.StructureFile }.Where(f => !string.IsNullOrEmpty(f)).ToList();
            Check("G1.scene.one_revision", m == null || files.All(f => f.Contains(rev)),
                $"site={streamer?.Site ?? "built-in"} r{m?.revision} frame={m?.FrameId} aligned={m?.frame?.aligned_to_preview?.ToString() ?? "-"} files=[{string.Join(",", files)}] swaps={streamer?.Swaps}");
            // Declutter M9 (DC5): the credit's home is the wrist strip (and Settings); the head-anchored chip shows it only for
            // its first 8 s after the scan becomes visible, so it may or may not be up now.
            string credit = SceneCredits.For(root != null && root.IsRuntimePackage ? root.Site : null);
            var chip = Services.Get<SceneCreditChip>();
            chip?.Refresh();
            var strip = Services.Get<LimitsChip>();
            strip?.Refresh();
            bool creditOk = credit == null
                ? (chip == null || chip.Shown == "") && (strip == null || strip.Credit == "")
                : strip != null && strip.Credit == credit && (chip == null || chip.Shown == "" || chip.Shown == credit);
            Check("G1.scene.credit", creditOk,
                $"site={root?.Site} credit=\"{credit ?? "-"}\" wrist=\"{strip?.Credit}\" wrist_shows={strip != null && strip.HasContent} tracked={strip != null && strip.Tracked} " +
                $"head=\"{chip?.Shown}\" head_age={(chip != null ? chip.DueAge.ToString("0.0", C) : "-")}s");

            string summary = $"G1 checks (sync): {s_Pass}/{s_Total} passed — GrokCheck(\"g1.status\") for the finish model and the notebook sync";
            Log.Check("G1.harness.sync", s_Pass == s_Total, $"passed={s_Pass} total={s_Total}");
            return summary + "\n" + string.Join("\n", s_Report);
        }

        /// The async halves: the finish model swapped onto the part and its copies; the notebook upload.
        public static string Status()
        {
            var sb = new StringBuilder();
            var tool = Services.Get<PartTool>();
            var part = tool != null ? (tool.Held != null ? tool.Held : tool.Selected) : null;
            if (s_ModelUrl != null && part != null)
            {
                var group = tool.FinishGroup(part);
                bool swapped = group.All(p => p != null && p.FinishModelUrl == s_ModelUrl);
                var size = part.MeasuredSizeMm();
                var dims = part.Spec.dims_mm;
                float want = Mathf.Max(dims.w, dims.h, dims.d);
                bool trueSize = Mathf.Abs(Mathf.Max(size.x, size.y, size.z) - want) <= Mathf.Max(1f, 0.02f * want);   // same size contract
                Log.Check("G1.set_finish.model", swapped && trueSize, $"parts={group.Count} swapped={swapped} size_mm=({size.x:0.0}, {size.y:0.0}, {size.z:0.0}) last=\"{G1Actions.LastFinish}\"");
                sb.AppendLine($"G1.set_finish.model: {(swapped && trueSize ? "ok" : "pending/FAIL")} parts={group.Count} last=\"{G1Actions.LastFinish}\"");
            }
            var nb = Services.Get<NotebookController>();
            if (s_Flush != null && nb != null)
            {
                bool sent = s_Flush.Done && s_Flush.Ok && nb.Uploads > s_UploadsBefore;
                string body = nb.LastUploadBody ?? "";
                Log.Check("G1.notebook.sync", sent, $"done={s_Flush.Done} ok={s_Flush.Ok} uploads={nb.Uploads - s_UploadsBefore} scan_loaded={body.Contains("\"scan_loaded\"")} bytes={body.Length}");
                sb.AppendLine($"G1.notebook.sync: {(sent ? "ok" : s_Flush.Done ? "FAIL (server offline?)" : "pending")} body={Clip(body)}");
            }
            return sb.Length == 0 ? "nothing pending (run GrokCheck(\"g1\") first)" : sb.ToString();
        }

        static string Get(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v?.ToString() : "-";
        static string Json(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? JsonConvert.SerializeObject(v) : "-";
        static string Clip(string s) => s.Length <= 400 ? s : s.Substring(0, 400) + "…";
    }
}
#endif
