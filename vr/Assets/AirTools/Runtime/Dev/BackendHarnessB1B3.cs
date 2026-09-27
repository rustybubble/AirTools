#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using Newtonsoft.Json.Linq;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Dev
{
    /// Live B1 / B3 checks against a server with the hand-off endpoints (e.g. the patched real server on :8002, or the
    /// B1/B3 mock): the same paths a voice command takes (/agent/command → actions → the survey through the real tape →
    /// /agent/observe → show_survey), and the measured-mandate checkout (prepare → checks → the 1 s hold → receipt).
    /// Start with RunB1(server) / RunB3(server), poll B1B3Result(). One [AirTools.Check] line per step.
    public static partial class BackendHarness
    {
        public class Step { public string Id; public bool Pass; public string Detail; }
        public static readonly List<Step> Steps = new List<Step>();
        public static bool B1B3Running { get; private set; }
        static string s_B1B3Stage = "";
        static float s_B1B3Started;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static float NowRt => Time.realtimeSinceStartup;

        static void Add(string id, bool pass, string detail)
        {
            Steps.Add(new Step { Id = id, Pass = pass, Detail = detail });
            Log.Check(id, pass, detail);
        }

        public static string B1B3Result()
        {
            var sb = new StringBuilder(B1B3Running ? $"running ({s_B1B3Stage}, {NowRt - s_B1B3Started:0.0} s) " : "done ");
            sb.Append($"{Steps.Count(x => x.Pass)}/{Steps.Count} passed");
            foreach (var st in Steps) sb.Append($"\n{(st.Pass ? "PASS" : "FAIL")} {st.Id}: {st.Detail}");
            return sb.ToString();
        }

        static string Start(IEnumerator routine, string server)
        {
            if (!Application.isPlaying) return "Play mode only";
            if (B1B3Running || DemoRunner.Busy) return "busy — poll B1B3Result()";
            Steps.Clear();
            B1B3Running = true;
            s_B1B3Started = NowRt;
            if (!string.IsNullOrEmpty(server)) ServerConfig.SetOverride(server);
            if (!DemoRunner.Run(routine, ex => { Add($"{s_B1B3Stage}.exception", false, ex.GetType().Name + ": " + ex.Message); Debug.LogException(ex); },
                    () => { B1B3Running = false; s_B1B3Stage = ""; }))
            { B1B3Running = false; return "could not start"; }
            return $"started against {ServerConfig.Current} — poll B1B3Result()";
        }

        static CustomYieldInstruction Until(Func<bool> cond, float timeout)
        {
            float t0 = NowRt;
            return new WaitUntil(() => cond() || NowRt - t0 > timeout);
        }

        static IEnumerator LoadSiteRoutine(string site)
        {
            var r = Root;
            if (r != null && r.Site == site && r.Structure != null && r.IsRuntimePackage) yield break;
            AppCommands.OpenChest();
            AppCommands.LoadSite(site);
            yield return Until(() => Root != null && Root.Site == site && Root.Structure != null && !Streamer.Loading, 120f);
            yield return new WaitForSecondsRealtime(0.5f);
            Add($"B1B3.site.{site}", Root != null && Root.Site == site && Root.Structure != null, SceneStatus());
        }

        /// The last command's own reply (an observe reply can land before the harness looks: check_slope runs at once).
        static AgentReply s_CommandReply;

        /// Send a typed command (the /voice/command path's text twin) and wait for its reply.
        static IEnumerator CommandRoutine(string text)
        {
            var agent = Services.Get<AgentClient>();
            s_CommandReply = null;
            bool done = false;
            agent.SendText(text, r => { s_CommandReply = r; done = true; });
            yield return Until(() => done, 35f);
            yield return null;
        }

        static AgentAction CommandAction(string name) => s_CommandReply?.actions?.FirstOrDefault(a => a.name == name);
        static string CommandText => $"reply=\"{s_CommandReply?.reply}\" actions=[{string.Join("; ", s_CommandReply?.actions ?? new List<AgentAction>())}]";

        /// Wait for the survey the last command started, then its /agent/observe round trip.
        static IEnumerator SurveyRoutine(int observeBefore, float timeout = 90f)
        {
            var runner = Services.Get<SurveyRunner>(); var agent = Services.Get<AgentClient>();
            yield return Until(() => runner == null || !runner.Running, timeout);
            yield return Until(() => agent.ObserveReplies > observeBefore, 45f);
            yield return new WaitForSecondsRealtime(0.3f);
        }

        static double Median(List<double> v)
        {
            if (v == null || v.Count == 0) return 0;
            var s = v.OrderBy(x => x).ToList();
            int m = s.Count / 2;
            return s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) * 0.5;
        }

        // ---------------- B1 ----------------

        /// B1 live: part = "kitchen" (measure every cabinet door → survey card), "facade" (every window; the gutter
        /// slope), or "all".
        public static string RunB1(string server = "http://localhost:8002", string part = "all") => Start(B1(part), server);

        static IEnumerator B1(string part)
        {
            var tool = Services.Get<MeasureTool>();
            if (part == "all" || part == "kitchen")
            {
                s_B1B3Stage = "B1.kitchen";
                yield return LoadSiteRoutine("kitchen");
                tool.ClearAll();
                yield return KitchenSurvey();
            }
            if (part == "stop")
            {
                s_B1B3Stage = "B1.stop";
                yield return LoadSiteRoutine("kitchen");
                tool.ClearAll();
                yield return StopSurvey();
            }
            if (part == "all" || part == "facade")
            {
                s_B1B3Stage = "B1.facade";
                yield return LoadSiteRoutine("synthetic-facade");
                tool.ClearAll();
                yield return FacadeWindow();
                yield return FacadeSlope();
            }
        }

        static IEnumerator KitchenSurvey()
        {
            var agent = Services.Get<AgentClient>(); var runner = Services.Get<SurveyRunner>(); var tool = Services.Get<MeasureTool>();
            var card = Services.Get<SurveyCard>();
            int observe0 = agent.Observations, replies0 = agent.ObserveReplies, notes0 = Notebook.Entries.Count, shows0 = card != null ? card.Shows : 0;
            float t0 = NowRt;
            yield return CommandRoutine("measure every cabinet door");
            var survey = CommandAction("survey");
            Add("B1.kitchen.command", survey != null && survey.Str("label") == "cabinet_door", $"{CommandText} err={agent.LastError}");
            if (survey == null || runner == null) yield break;
            yield return SurveyRoutine(replies0);
            float seconds = runner.LastSeconds;
            int amber = runner.Results.Count(x => x.unverified);
            int surveyShapes = tool.Shapes.Count(s => s.Survey != null && s.Survey.RequestId == runner.RequestId);
            Add("B1.kitchen.survey", runner.LastStatus == "done" && runner.Results.Count == 18 && surveyShapes == 18 && Notebook.Entries.Count - notes0 >= 18,
                $"request={runner.RequestId} planned={runner.Planned} tapes={runner.Results.Count} shapes={surveyShapes} unverified={amber} skipped={runner.Skipped.Count} " +
                $"notebook+={Notebook.Entries.Count - notes0} seconds={seconds.ToString("0.0", Inv)} (command→report {(NowRt - t0).ToString("0.0", Inv)} s)");
            // Median width per verified structure group vs the layer's w_m × scale (S1 acceptance: within 5 mm).
            var byGroup = runner.Results.Where(x => !x.unverified && !string.IsNullOrEmpty(x.group)).GroupBy(x => x.group).ToList();
            var groupNotes = new List<string>();
            int groupsOk = 0;
            var layer = Root.Structure;
            foreach (var g in byGroup)
            {
                var w = g.Select(x => x.w_m).OrderBy(x => x).ToList();
                double med = Median(w);
                // The layer's group width (groups[].w_m) × calibration; else the median of its members' w_m.
                double want = layer.Groups.Where(x => x.id == g.Key && x.widthM > 0).Select(x => (double)x.widthM * Root.Calibration).FirstOrDefault();
                if (want <= 0)
                {
                    want = Median(runner.Targets.Where(t => t.Group == g.Key).Select(t => (double)t.WidthM).ToList());
                }
                double diff = (med - want) * 1000.0;
                if (Math.Abs(diff) <= 5) groupsOk++;
                groupNotes.Add($"{g.Key}:n={w.Count} median_w={med * 1000:0.0}mm group_w={want * 1000:0.0}mm Δ={diff:+0.0;-0.0}");
            }
            Add("B1.kitchen.accuracy", runner.Results.Count - amber >= 14 && groupsOk == byGroup.Count,
                $"verified={runner.Results.Count - amber}/18 groups_within_5mm={groupsOk}/{byGroup.Count} {string.Join(" ", groupNotes)}");
            // Exactly one observe for the request, answered with a show_survey.
            var reply = agent.LastObserve;
            var show = reply?.actions?.FirstOrDefault(a => a.name == "show_tape_survey" || a.name == "show_survey");   // p4-grok 0009 renamed it
            var groups = show?.args?["groups"] as JArray;
            string counts = groups != null ? string.Join(",", groups.Select(g => $"{(int)g["count"]}×{(int)g["w_mm"]}x{(int)g["h_mm"]}")) : "-";
            int unv = show?.args?["unverified"] is JArray u ? u.Count : -1;
            Add("B1.kitchen.observe", agent.Observations - observe0 == 1 && agent.LastObserveCode == 200 && show != null,
                $"observations={agent.Observations - observe0} http={agent.LastObserveCode} reply=\"{reply?.reply}\" audio={(string.IsNullOrEmpty(reply?.audio_b64) ? "none" : reply.audio_mime)} tts_error={reply?.tts_error}");
            Add("B1.kitchen.card", card != null && card.Shows > shows0 && card.IsOpen && groups != null && groups.Sum(g => (int)g["count"]) == runner.Results.Count,
                $"groups={groups?.Count ?? 0} counts=[{counts}] unverified={unv} card_open={card?.IsOpen} title=\"{card?.title?.text}\"");
            int labels = tool.VisibleLabelCount;
            Add("B1.kitchen.labels", labels <= 12 && labels > 0, $"visible_labels={labels} (survey shapes draw one W×H label, ≤ 12)");
            // One undo removes the whole survey; redo puts it back.
            int before = Notebook.Entries.Count;
            EditHistory.Undo();
            int afterUndo = Notebook.Entries.Count;
            EditHistory.Redo();
            Add("B1.kitchen.undo", before - afterUndo == 18 && Notebook.Entries.Count == before,
                $"entries {before} → undo {afterUndo} → redo {Notebook.Entries.Count}");
        }

        /// "measure every cabinet door", then "stop" a moment later: the survey aborts, finished doors stay, one
        /// observe (aborted, with planned).
        static IEnumerator StopSurvey()
        {
            var agent = Services.Get<AgentClient>(); var runner = Services.Get<SurveyRunner>(); var tool = Services.Get<MeasureTool>();
            int observe0 = agent.Observations, replies0 = agent.ObserveReplies;
            yield return CommandRoutine("measure every cabinet door");
            if (CommandAction("survey") == null) { Add("B1.stop", false, $"no survey: {CommandText}"); yield break; }
            yield return new WaitForSecondsRealtime(1.5f);
            yield return CommandRoutine("stop");
            var stop = CommandAction("stop_survey");
            yield return Until(() => agent.ObserveReplies > replies0, 30f);
            yield return new WaitForSecondsRealtime(0.3f);
            var body = agent.LastObserveBody != null ? JObject.Parse(agent.LastObserveBody) : null;
            int kept = tool.Shapes.Count(x => x.Survey != null && x.Survey.RequestId == runner.RequestId);
            Add("B1.stop", stop != null && !runner.Running && (string)body?["status"] == "aborted" && agent.Observations - observe0 == 1 && kept == runner.Results.Count && tool.Session.Count == 0,
                $"{CommandText} status={body?["status"]} planned={body?["planned"]} results={((JArray)body?["results"])?.Count} kept_shapes={kept} session={tool.Session.Count} observations={agent.Observations - observe0} reply=\"{agent.LastObserve?.reply}\"");
        }

        static IEnumerator FacadeWindow()
        {
            var agent = Services.Get<AgentClient>(); var runner = Services.Get<SurveyRunner>();
            int replies0 = agent.ObserveReplies;
            yield return CommandRoutine("measure every window");
            var survey = CommandAction("survey");
            if (survey == null) { Add("B1.facade.window", false, $"no survey action: {CommandText} err={agent.LastError}"); yield break; }
            yield return SurveyRoutine(replies0);
            var r = runner.Results.FirstOrDefault();
            bool ok = runner.Results.Count == 1 && r != null && Math.Abs(r.w_m - 1.5) <= 0.005 && Math.Abs(r.h_m - 1.2) <= 0.005;
            Add("B1.facade.window", ok, r == null ? $"results={runner.Results.Count} status={runner.LastStatus}"
                : $"{r.id} {r.label} {r.w_m.ToString("0.0000", Inv)} × {r.h_m.ToString("0.0000", Inv)} m angles=[{string.Join(",", r.angles_deg.Select(a => a.ToString("0.0", Inv)))}] snap=[{string.Join(",", r.snap)}] unverified={r.unverified} reply=\"{agent.LastObserve?.reply}\"");
        }

        static IEnumerator FacadeSlope()
        {
            var agent = Services.Get<AgentClient>(); var runner = Services.Get<SurveyRunner>();
            int replies0 = agent.ObserveReplies, notes0 = Notebook.Entries.Count;
            yield return CommandRoutine("check the gutter slope");
            var slope = CommandAction("check_slope");
            if (slope == null) { Add("B1.facade.slope", false, $"no check_slope: {CommandText} err={agent.LastError}"); yield break; }
            yield return Until(() => agent.ObserveReplies > replies0, 45f);
            yield return new WaitForSecondsRealtime(0.3f);
            var body = agent.LastObserveBody != null ? JObject.Parse(agent.LastObserveBody) : null;
            var reply = agent.LastObserve;
            bool note = reply?.actions != null && reply.actions.Any(a => a.name == "add_note") && Notebook.Last != null && Notebook.Last.Tool == "note";
            Add("B1.facade.slope", body != null && (string)body["kind"] == "slope_result" && reply != null && note,
                $"edges=[{string.Join(",", AgentActions.Strings(slope.args?["edge_ids"]))}] taped={runner.LastSlopeEdge} run_m={body?["run_m"]} fall_mm={body?["fall_mm"]} " +
                $"low_end={body?["low_end"]?.ToString(Newtonsoft.Json.Formatting.None)} verdict=\"{reply?.reply}\" note=\"{(note ? Notebook.Last.Label : "-")}\" notebook+={Notebook.Entries.Count - notes0}");
        }

        // ---------------- B3 ----------------

        /// B3 live on the facade: 8 hangers (a server part) along the 4.20 m gutter tape → checkout prepare (checks,
        /// server total, limits) → a 0.6 s hold (nothing sent) → the 1.0 s hold (one /checkout with the proof) → the
        /// receipt's mandate chain; then (limit = true) a $20 panel limit under the total → Pay off with the failing row.
        public static string RunB3(string server = "http://localhost:8002", bool limit = true, string site = "synthetic-facade") => Start(B3(limit, site), server);

        /// Only the limit step (after RunB3(limit: false)): reopen the checkout, set the limit, see Pay go off.
        public static string RunB3Limit(string server = "http://localhost:8002") => Start(B3LimitOnly(), server);

        static IEnumerator B3(bool limit, string site)
        {
            s_B1B3Stage = "B3.setup";
            if (!string.IsNullOrEmpty(site)) yield return LoadSiteRoutine(site);
            var client = Services.Get<PartsClient>(); var panel = Services.Get<CheckoutPanel>(); var tool = Services.Get<PartTool>();
            var browser = Services.Get<PartsBrowser>(); var root = Root;
            // Start from no limits (the panel may clear them).
            bool cleared = false;
            client.Limits(new Dictionary<string, object> { ["max_total_usd"] = null, ["deliver_by"] = null, ["seller_policy"] = null }, (r, e) => cleared = true);
            yield return Until(() => cleared, 10f);
            tool.ClearAll();
            // A hanger the server knows (its cached search), onto the fascia.
            int searches = browser.SearchCount;
            AppCommands.FindPart("gutter hanger");
            yield return Until(() => !browser.Searching && browser.SearchCount > searches, 60f);
            int pick = Math.Max(0, browser.Candidates.FindIndex(c => c.id.Contains("m0722b")));
            browser.Select(pick);
            yield return Until(() => !browser.Loading && tool.Held != null, 60f);
            var held = tool.Held;
            var frame = root.transform;
            var origin = new Vector3(0.22f, 6.16f, S.FasciaProud + 0.05f);
            bool placed = held != null && tool.Release(ToolInputHub.RayPose(frame.TransformPoint(origin), frame.TransformPoint(origin + Vector3.right)));
            var a = new Vector3(-2.1f, 6.1f, 0.152f); var b = new Vector3(2.1f, 6.1f, 0.152f);
            var tape = new NotebookEntry("measure", 4.2, "m", new[] { a, b }, DateTime.Now,
                CameraEvidence.Nearest(new[] { a, b }, root.CamerasInRootSpace()), "Distance 4.20 m (gutter)");
            Notebook.Add(tape);
            var g = placed ? tool.PlaceArray(600f) : null;
            Add("B3.setup", g != null && g.Plan.Count == 8, $"search={browser.Source} part={held?.Spec.id} source={held?.Source} placed={placed} array={g?.Plan.Count} tape=#{tape.Id} camera={tape.NearestCameraId} last=\"{tool.LastAction}\"");
            if (g == null) yield break;

            s_B1B3Stage = "B3.prepare";
            int prepares0 = client.PrepareRequests, pays0 = client.CheckoutRequests;
            AppCommands.StartCheckout(held.Spec.recommended_seller ?? 0);
            yield return Until(() => panel.Mandate != MandateState.Pending, 15f);
            var p = panel.Prepared;
            var req = client.LastPrepareBody != null ? JObject.Parse(client.LastPrepareBody) : null;
            Add("B3.prepare", panel.Mandate == MandateState.Prepared && p != null && p.all_ok && client.CheckoutRequests == pays0 && panel.CanPay,
                $"mandate={panel.Mandate} prepares={client.PrepareRequests - prepares0} cart={p?.cart?.id} total={p?.cart?.total_usd.ToString("0.00", Inv)} local={panel.Total.ToString("0.00", Inv)} packs={p?.Packs} " +
                $"hash=…{p?.cart_hash?.Substring(Math.Max(0, (p?.cart_hash?.Length ?? 4) - 4))} intent=\"{MandateText.Limits(p?.intent)}\" " +
                $"checks=[{string.Join(", ", panel.Checks.Select(c => $"{c.id}:{c.Status}"))}] evidence={req?["evidence"]?.ToString(Newtonsoft.Json.Formatting.None)}");
            Add("B3.checks.rows", panel.Checks.Count >= 7 && panel.checks != null && panel.checks.text.Split('\n').Length == panel.Checks.Count,
                "rows:\n    " + System.Text.RegularExpressions.Regex.Replace(panel.ChecksText(), "<[^>]*>", "").Replace("\n", "\n    "));
            if (panel.Mandate != MandateState.Prepared) yield break;
            yield return new WaitForSecondsRealtime(1.0f);   // (a capture window: the checks)

            s_B1B3Stage = "B3.hold";
            bool fired06 = panel.hold.Simulate(0.6f);
            Add("B3.hold_0.6s", !fired06 && client.CheckoutRequests == pays0, $"fired={fired06} sent={client.CheckoutRequests - pays0}");
            bool fired10 = panel.hold.Simulate(1.0f);
            var sent = client.LastCheckoutBody != null ? JObject.Parse(client.LastCheckoutBody) : null;
            var proof = client.LastProof;
            Add("B3.hold_1.0s", fired10 && client.CheckoutRequests == pays0 + 1 && proof != null && proof.HeldMs >= 1000 && (int)sent["qty"] == p.Packs && proof.CartHash == p.cart_hash,
                $"fired={fired10} sent={client.CheckoutRequests - pays0} proof={{cart_hash:…{proof?.CartHash?.Substring(proof.CartHash.Length - 4)}, nonce:{proof?.Nonce?.Substring(0, 7)}…, hold_ms:{proof?.HeldMs}}} qty={sent?["qty"]} (prepared packs {p.Packs})");
            yield return Until(() => panel.State != CheckoutState.Paying, 30f);
            var m = panel.Receipt?.mandate;
            Add("B3.receipt", panel.State == CheckoutState.Paid && m != null && m.cart?.hash == p.cart_hash && m.authorization != null && (m.authorization.hold_ms ?? 0) >= 1000,
                $"state={panel.State} receipt={panel.Receipt?.receipt_id} status={panel.Receipt?.status} mode={m?.authorization?.mode} approval={m?.authorization?.approval_code ?? "null"} " +
                $"hold_ms={m?.authorization?.hold_ms} label=\"{panel.Receipt?.label}\" chain:\n    " +
                System.Text.RegularExpressions.Regex.Replace(panel.ChecksText(), "<[^>]*>", "").Replace("\n", "\n    ") + $"\n    notebook=\"{Notebook.Last?.Label}\"");
            if (!limit) yield break;
            yield return new WaitForSecondsRealtime(1.5f);   // (a capture window: the receipt)
            yield return B3LimitOnly();
        }

        static IEnumerator B3LimitOnly()
        {
            s_B1B3Stage = "B3.limit";
            var client = Services.Get<PartsClient>(); var panel = Services.Get<CheckoutPanel>(); var tool = Services.Get<PartTool>();
            if (tool.Selected == null) { Add("B3.limit", false, "no part selected (run RunB3 first)"); yield break; }
            int pays0 = client.CheckoutRequests;
            AppCommands.StartCheckout(panel.SellerIndex);
            yield return Until(() => panel.Mandate != MandateState.Pending, 15f);
            float total = panel.DisplayTotal;
            float cap = Mathf.Max(1f, Mathf.Floor(total) - 3f);
            bool done = false;
            panel.SetLimits(new Dictionary<string, object> { ["max_total_usd"] = cap }, _ => done = true);
            yield return Until(() => done, 10f);
            yield return Until(() => panel.Mandate != MandateState.Pending, 15f);
            bool fired = panel.hold.Simulate(1.0f);
            Add("B3.limit", panel.Mandate == MandateState.Refused && !panel.CanPay && panel.Checks.Any(c => c.id == "within_limit" && c.Fails) && client.CheckoutRequests == pays0 && panel.hold.button != null && !panel.hold.button.interactable,
                $"cap=${cap.ToString("0", Inv)} total=${total.ToString("0.00", Inv)} mandate={panel.Mandate} can_pay={panel.CanPay} pay_interactable={panel.hold.button?.interactable} " +
                $"hold_fired={fired} sent={client.CheckoutRequests - pays0} refused_holds={panel.RefusedHolds} failing=\"{panel.FailingDetail}\" status=\"{panel.status?.text}\" chip=\"{LimitsChip.Current?.max_total_usd}\"");
        }

        /// Clear the session's limits (after RunB3's limit step).
        public static string ClearLimits()
        {
            var client = Services.Get<PartsClient>();
            if (client == null) return "no client";
            client.Limits(new Dictionary<string, object> { ["max_total_usd"] = null, ["deliver_by"] = null, ["seller_policy"] = null },
                (r, e) => { if (r?.intent != null) LimitsChip.Set(r.intent); });
            return "clearing";
        }
    }
}
#endif
