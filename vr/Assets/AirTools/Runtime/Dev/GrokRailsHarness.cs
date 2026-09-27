#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Core;
using AirTools.Parts;
using AirTools.Scene;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Dev
{
    /// Lane G4's Play-mode check, AgentHarness.GrokCheck("g4"): feeds recorded "do the whole job" runs and install-coach
    /// sessions (GrokRailFixtures, made by the backend's own code) through AgentActions.ExecuteAll in the order replies
    /// and poll pages deliver them, and checks the rail, the coach card and overlay, the hands-only buttons, and that
    /// nothing paid. Logs [AirTools.Check] G4.* lines; returns the report.
    ///
    /// The other lanes' actions in a run (show_survey, select_candidate, show_safety, ...) go to their handlers too; on a
    /// branch without them AgentActions refuses them ("Can't do that yet"), which this check doesn't count.
    /// Boxes and the stop card need the kitchen scan loaded (its cameras 0141 / 0221 are the recorded frames).
    public static class GrokRailsHarness
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public static string Run()
        {
            var report = new StringBuilder();
            int pass = 0, total = 0;
            void Check(string id, bool ok, string detail)
            {
                total++;
                if (ok) pass++;
                Log.Check(id, ok, detail);
                report.AppendLine($"{id}: {(ok ? "ok" : "FAIL")} {detail}");
            }

            var sender = GrokRails.Sender;
            var sent = new List<string>();
            var client = Services.TryGet<PartsClient>(out var pc) ? pc : null;
            int checkouts = client != null ? client.CheckoutRequests : 0;
            int paid = AirTools.UI.FeedbackEvents.Count(AirTools.UI.Feedback.Paid);
            try
            {
                // Declutter S1: the rails never show in passthrough, so the views are checked in the world.
                if (EnterWorld()) report.AppendLine("entered the world (the rails don't show in passthrough)");
                GrokRails.Reset();
                GrokRails.Sender = w => { sent.Add(w); return true; };
                // Each section on its own: one that throws is a FAIL line, and the rest still run.
                void Section(string id, System.Action run)
                {
                    try { run(); }
                    catch (System.Exception e) { Check($"G4.{id}.error", false, $"{e.GetType().Name}: {e.Message}"); }
                }
                Section("job", () => RunJob(Check));
                Section("recall", () => RunRecall(Check));
                Section("poll", () => RunLivePoll(Check));
                Section("coach_led", () => RunCoachLed(Check, sent));
                Section("coach_faucet", () => RunCoachFaucet(Check));
                Section("coach_manual", () => RunCoachManual(Check));
                int paidNow = AirTools.UI.FeedbackEvents.Count(AirTools.UI.Feedback.Paid);
                Check("G4.never_pays", (client == null || client.CheckoutRequests == checkouts) && paidNow == paid,
                    $"checkout_requests={(client != null ? client.CheckoutRequests : -1)} before={checkouts} paid_events={paidNow - paid} (start_checkout only opens the hold-to-pay panel)");
            }
            finally
            {
                GrokRails.Sender = sender ?? AppCommands.SendCommand;
            }
            Log.Check("G4.harness.summary", pass == total, $"passed={pass} total={total}");
            report.AppendLine($"G4: {pass}/{total}");
            return report.ToString();
        }

        /// Put one state on screen for a capture: "job" (mid-run: parallel steps working), "job-done", "recall",
        /// "coach-stop" (the LED drill step stopped above an outlet), "coach-check" (faucet: not yet, box on the sink),
        /// "coach-done", or "clear". Live against the server: "live-job" says "do the whole job" (needs a loaded scan,
        /// context.site), "live-coach" says "teach me to install the faucet"; watch with Status(). "look:0141" moves the
        /// rig so the eye looks from that scene camera (for the overlay captures).
        public static string Stage(string what)
        {
            what = (what ?? "").Trim();
            if (what.StartsWith("look:")) return LookFrom(what.Substring(5));
            if (what == "live-job") return AppCommands.SendCommand("do the whole job") ? "sent \"do the whole job\"" : "no agent client";
            if (what == "live-coach") return AppCommands.SendCommand("teach me to install the faucet") ? "sent \"teach me to install the faucet\"" : "no agent client";
            if (!what.Equals("clear", System.StringComparison.OrdinalIgnoreCase)) EnterWorld();   // declutter S1: the rails never show in passthrough
            GrokRails.Reset();
            GrokRails.Sender = AppCommands.SendCommand;
            switch (what.ToLowerInvariant())
            {
                case "job": Feed(Actions(GrokRailFixtures.JobHappy), 7); GrokRails.Poll.Stop("harness"); break;
                case "job-done": Feed(Actions(GrokRailFixtures.JobHappy)); GrokRails.Poll.Stop("harness"); break;
                case "recall": Feed(Actions(GrokRailFixtures.JobRecall)); GrokRails.Poll.Stop("harness"); break;
                case "coach-stop": GrokState.LastFrameId = "0141"; FeedReplies(GrokRailFixtures.CoachLed, 4); break;
                case "coach-check": FeedReplies(GrokRailFixtures.CoachFaucet, 2); break;
                case "coach-done": FeedReplies(GrokRailFixtures.CoachFaucet, 99); break;
                case "clear": GrokRails.Clear(); break;
                default: return $"unknown stage '{what}' (job, job-done, recall, coach-stop, coach-check, coach-done, clear)";
            }
            Refresh();
            return Status();
        }

        /// One line per rail: the run's dots and the coach's step.
        public static string Status()
        {
            var sb = new StringBuilder();
            var job = GrokRails.Job;
            if (job != null)
            {
                sb.Append($"job {job.RunId} {job.Status}: ");
                for (int k = 0; k < job.Dots.Count; k++) sb.Append($"{job.Dots[k].Name}={job.Display(k)} ");
                sb.Append($"| \"{job.Caption}\" | poll {(GrokRails.Poll.Active ? "on" : GrokRails.Poll.StopReason)} after={GrokRails.Poll.After}");
                if (job.Finished) sb.Append($" | {GrokRailText.JobSummary(job.Done, job)}");
                sb.AppendLine();
            }
            var m = GrokRails.Coach;
            if (m != null)
            {
                sb.Append($"coach {m.CoachId} {GrokRailText.CoachStepLine(m)}: ");
                for (int k = 0; k < m.DotCount; k++) sb.Append($"{m.Dot(k)} ");
                sb.Append($"| chips {string.Join(", ", m.Chips.Select(c => c.State))} | {GrokRailText.VerdictLine(m)}");
                if (m.Finished) sb.Append($" | {GrokRailText.CoachSummary(m.Done, m.DotCount)}");
                sb.Append($" | drill={GrokState.DrillActive} px={(GrokState.DrillPx != null ? string.Join(",", GrokState.DrillPx.Select(v => v.ToString("0.###", C))) : "-")}");
                var ov = Overlay;
                if (ov != null) sb.Append($" | boxes={ov.BoxesShown} stop={ov.StopShown} frame={ov.LastFrame ?? "-"}{(ov.LastError != null ? $" ({ov.LastError})" : "")}");
                sb.AppendLine();
            }
            return sb.Length == 0 ? "no rails" : sb.ToString().TrimEnd();
        }

        /// Move the rig so the centre eye sits at a scene camera and faces its heading (yaw only), e.g. "0141" / "0221"
        /// (the recorded coach frames in the kitchen scan).
        public static string LookFrom(string frameId)
        {
            if (!Services.TryGet<SceneStreamer>(out var st) || !(st.FindCamera(frameId) is SceneCameraJson cam) || cam.R == null) return $"no camera '{frameId}'";
            if (!Services.TryGet<SceneRoot>(out var root) || root.Content == null) return "no scene loaded";
            var rig = Object.FindFirstObjectByType<OVRCameraRig>();
            if (rig == null || rig.centerEyeAnchor == null) return "no rig";
            var f = CoachFrameRays.FromSceneCamera(cam);
            var content = root.Content.transform;
            var eye = content.TransformPoint(f.Position);
            var fwd = content.TransformDirection(f.Forward);
            var flat = Vector3.ProjectOnPlane(fwd, Vector3.up);
            var headFlat = Vector3.ProjectOnPlane(rig.centerEyeAnchor.forward, Vector3.up);
            if (flat.sqrMagnitude > 1e-6f && headFlat.sqrMagnitude > 1e-6f)
                rig.transform.rotation = Quaternion.FromToRotation(headFlat.normalized, flat.normalized) * rig.transform.rotation;
            rig.transform.position += eye - rig.centerEyeAnchor.position;
            return $"eye at {eye.ToString("F3")} looking {fwd.ToString("F2")} (camera {frameId})";
        }

        // ---------------- the job ----------------

        static void RunJob(System.Action<string, bool, string> Check)
        {
            var actions = Actions(GrokRailFixtures.JobHappy);
            AgentActions.ExecuteAll(actions.Take(1), "On it: survey, part, safety, rules, preview, packet. I'll stop at the pay panel.");
            var job = GrokRails.Job;
            Check("G4.job.started", job != null && job.Dots.Count == 7 && GrokRails.Poll.Active && GrokRails.Poll.After == 1,
                $"run={job?.RunId} dots={job?.Dots.Count} poll={GrokRails.Poll.Path} active={GrokRails.Poll.Active}");
            GrokRails.Poll.Stop("harness");   // the recorded run isn't on this server
            bool order = true, parallel = false;
            foreach (var a in actions.Skip(1))
            {
                AgentActions.ExecuteAll(new[] { a });
                if (a.name == "job_step")
                {
                    int i = a.Int("i") ?? -1;
                    order &= i >= 0 && job.Dots[i].Status == JobRunModel.Parse(a.Str("status"));
                    if (a.Str("name") == "part")
                        parallel = job.Display(2) == DotStatus.Active && job.Display(3) == DotStatus.Active && job.Display(4) == DotStatus.Active
                                   && job.Display(5) == DotStatus.Pending;
                }
            }
            // History keeps the last 50: this run's are its last actions.Count entries.
            var ran = AgentActions.History.Skip(System.Math.Max(0, AgentActions.History.Count - actions.Count)).Select(h => h.Split(':')[0]).ToList();
            Check("G4.job.order", order && ran.SequenceEqual(actions.Select(x => x.name)),
                $"executed={string.Join(",", ran)}");
            Check("G4.job.parallel", parallel, "safety, rules and postcard work together after the part");
            string summary = GrokRailText.JobSummary(job.Done, job);
            Check("G4.job.done", job.Finished && job.Dots.All(d => d.Status == DotStatus.Done) && summary == "✓ Done · $899.00 · $749.00 after rebates",
                $"summary=\"{summary}\" detail=\"{GrokRailText.JobDetail(job.Done, job)}\" caption=\"{job.Caption}\"");
            // Declutter M2 (DC1): the run is the status line's progress mode (the job strip), not a rail of its own.
            var view = Object.FindFirstObjectByType<JobRailView>(FindObjectsInactive.Include);
            var line = view != null && view.status != null ? view.status : AirTools.UI.StatusLine.Current;
            if (view != null && line != null)
            {
                view.Refresh();
                string shown = GrokRailText.StripTags(line.ProgressLine);
                bool onLine = !view.standalone && view.ShownRun == job.RunId && line.InProgress && shown == summary && !view.Showing;
                bool strip = view.Strip == "✓ ✓ ✓ ✓ ✓ ✓ ✓";
                Check("G4.job.view", onLine && strip,
                    $"run={view.ShownRun} line1=\"{shown}\" line2=\"{line.ProgressDetail}\" strip=\"{view.Strip}\" rail_panel={(view.Showing ? "up" : "down")} line={(line.Showing ? "up" : "down")}");
            }
            else Check("G4.job.view", false, view == null ? "no JobRailView in the scene (Wire Main Scene)" : "no status line in the scene (Wire Main Scene)");
        }

        static void RunRecall(System.Action<string, bool, string> Check)
        {
            var actions = Actions(GrokRailFixtures.JobRecall);
            Feed(actions);
            GrokRails.Poll.Stop("harness");
            var job = GrokRails.Job;
            var last = job.Dots[job.Dots.Count - 1];
            Check("G4.job.recall", job.Status == "stopped" && last.Status == DotStatus.Stopped && !actions.Any(a => a.name == "start_checkout")
                                   && GrokRailText.JobSummary(job.Done, job).StartsWith("✗ Stopped before checkout"),
                $"checkout={last.Status} \"{last.Spoken}\" summary=\"{GrokRailText.JobSummary(job.Done, job)}\"");
        }

        /// The poll path: job_started, then GET /job/run/{id}?after=1's body through GrokRails.Polled like JobRunPoller.
        static void RunLivePoll(System.Action<string, bool, string> Check)
        {
            var o = JObject.Parse(GrokRailFixtures.JobLivePoll);
            var first = o["actions"][0] as JObject;
            AgentActions.ExecuteAll(new[] { new AgentAction { name = (string)first["name"], args = first["args"] as JObject } });
            ((JArray)o["actions"]).RemoveAt(0);   // the page from after=1
            var body = GrokRailPayloads.Poll(o.ToString());
            GrokRails.Poll.Sent();
            var actions = GrokRails.Polled(body, Time.realtimeSinceStartupAsDouble);
            AgentActions.ExecuteAll(actions);
            var job = GrokRails.Job;
            bool skipped = job.Dots.Where(d => d.Name == "rules" || d.Name == "postcard").All(d => d.Status == DotStatus.Skipped);
            Check("G4.job.poll", !GrokRails.Poll.Active && GrokRails.Poll.StopReason == "done" && GrokRails.Poll.After == 13 && skipped && job.Finished,
                $"after={GrokRails.Poll.After} stop={GrokRails.Poll.StopReason} skipped(rules,postcard)={skipped} summary=\"{GrokRailText.JobSummary(job.Done, job)}\"");
        }

        // ---------------- the coach ----------------

        static void RunCoachLed(System.Action<string, bool, string> Check, List<string> sent)
        {
            GrokState.LastFrameId = "0141";   // the recorded drill check's frame (the agent path sends no frame_id)
            var replies = Replies(GrokRailFixtures.CoachLed);
            FeedReply(replies[0]);
            var m = GrokRails.Coach;
            var card = Object.FindFirstObjectByType<CoachRailView>(FindObjectsInactive.Include);
            card?.Refresh();
            Check("G4.coach.started", m != null && m.DotCount == 5 && m.Current == 0 && m.LabelNote == "Coaching from the manual or our own checklist. Camera checks can miss things."
                                      && (card == null || card.IsOpen),
                $"coach={m?.CoachId} dots={m?.DotCount} title=\"{GrokRailText.CoachTitle(m)}\" card={(card != null ? card.IsOpen.ToString() : "none")}");
            FeedReply(replies[1]);
            FeedReply(replies[2]);
            Check("G4.coach.drill", m.IsDrill && GrokState.DrillActive && GrokState.DrillPx != null && GrokState.DrillPx[0] == 0.5f,
                $"{GrokRailText.CoachStepLine(m)} drill_px={(GrokState.DrillPx != null ? string.Join(",", GrokState.DrillPx) : "-")}");
            FeedReply(replies[3]);   // coach_check stop + coach_stop
            var ov = Overlay;
            ov?.Refresh();
            bool scene = Services.TryGet<SceneStreamer>(out var st) && st.FindCamera("0141") != null;
            Check("G4.coach.stop", m.Paused && m.Dot(2) == DotStatus.Stopped && GrokRailText.VerdictLine(m) == "✗ Stop · don't drill here: an outlet below"
                                   && (!scene || ov != null && ov.StopShown && ov.BoxesShown >= 1),
                $"paused={m.Paused} dot={m.Dot(2)} \"{GrokRailText.VerdictLine(m)}\" overlay={(ov == null ? "none" : $"boxes={ov.BoxesShown} stop={ov.StopShown} frame={ov.LastFrame} {ov.LastError}")}{(scene ? "" : " (no kitchen camera 0141: overlay not checked)")}");
            FeedReply(replies[4]);   // re-check elsewhere: not yet, still paused
            bool still = m.Paused;
            FeedReply(replies[5]);   // next
            Check("G4.coach.resume", still && !m.Paused && m.Current == 3 && m.Dot(2) == DotStatus.Said && (ov == null || !Refreshed(ov).StopShown),
                $"paused_after_recheck={still} now={GrokRailText.CoachStepLine(m)} dot2={m.Dot(2)}");
            FeedReply(replies[6]);   // back
            FeedReply(replies[7]);   // repeat
            Check("G4.coach.back_repeat", m.Current == 2 && m.IsDrill && !m.Paused, GrokRailText.CoachStepLine(m));
            // Hands-only: the card's buttons send the coach words as normal commands.
            sent.Clear();
            if (card != null)
            {
                card.Refresh();
                foreach (var b in new[] { card.checkIt, card.back, card.repeat, card.next }) b?.Press();
            }
            Check("G4.coach.buttons", card != null && sent.SequenceEqual(new[] { GrokRails.CheckIt, GrokRails.Back, GrokRails.Repeat, GrokRails.Next }),
                $"sent=[{string.Join(", ", sent)}]");
        }

        static void RunCoachFaucet(System.Action<string, bool, string> Check)
        {
            var replies = Replies(GrokRailFixtures.CoachFaucet);
            FeedReply(replies[0]);
            FeedReply(replies[1]);   // not yet, frame 0221, box on the sink
            var m = GrokRails.Coach;
            var ov = Overlay;
            ov?.Refresh();
            bool scene = Services.TryGet<SceneStreamer>(out var st) && st.FindCamera("0221") != null;
            Check("G4.coach.not_yet", m.Chips.Count == 1 && m.Chips[0].State == ChipState.NotYet && (!scene || ov != null && ov.BoxesShown == 1),
                $"chip={m.Chips.FirstOrDefault()?.State} \"{GrokRailText.VerdictLine(m)}\" boxes={ov?.BoxesShown} frame={ov?.LastFrame}{(scene ? "" : " (no kitchen camera 0221: box not checked)")}");
            FeedReply(replies[2]);
            FeedReply(replies[3]);   // can't see
            Check("G4.coach.cant_see", m.Chips[0].State == ChipState.CantSee && GrokRailTones.Chip(ChipState.CantSee) == GrokRailTones.Info,
                GrokRailText.VerdictLine(m));
            for (int k = 4; k < replies.Count; k++) FeedReply(replies[k]);
            string summary = GrokRailText.CoachSummary(m.Done, m.DotCount);
            Check("G4.coach.done", m.Finished && m.CheckedByCamera == 1 && m.OnYourWord == 5 && m.Dot(4) == DotStatus.Done
                                   && summary == "✓ All 6 steps · 1 checked by camera · 5 on your word" && !GrokState.CoachActive,
                $"\"{summary}\" dots={string.Join(" ", Enumerable.Range(0, m.DotCount).Select(m.Dot))}");
        }

        /// The demo's coach: the Midea from its manual (pages, quotes, Open manual), on to the drill step (page 13).
        static void RunCoachManual(System.Action<string, bool, string> Check)
        {
            var replies = Replies(GrokRailFixtures.CoachMidea);
            FeedReply(replies[0]);
            var m = GrokRails.Coach;
            var card = Object.FindFirstObjectByType<CoachRailView>(FindObjectsInactive.Include);
            GrokRails.LastOpenedUrl = null;
            if (card != null)
            {
                card.Refresh();
                card.openManual?.Press();
            }
            bool manualShown = card != null && card.manual != null && card.manual.gameObject.activeSelf && card.manual.text.StartsWith("Manual p. 11 · “")
                               && card.openManual != null && card.openManual.gameObject.activeSelf;
            Check("G4.coach.manual", m.Source == "manual" && m.DotCount == 8 && manualShown
                                     && (GrokRails.LastOpenedUrl ?? "").EndsWith("/parts/midea-maw08u1qwt-8d6d08/manual.pdf#page=11"),
                $"title=\"{GrokRailText.CoachTitle(m)}\" manual=\"{(card?.manual != null ? card.manual.text : "-")}\" opened={GrokRails.LastOpenedUrl ?? "-"}");
            for (int k = 1; k < replies.Count; k++) FeedReply(replies[k]);
            card?.Refresh();
            Check("G4.coach.manual_drill", m.Current == 4 && m.IsDrill && GrokState.DrillActive && GrokRailText.ManualLine(m.Step).StartsWith("Manual p. 13"),
                $"{GrokRailText.CoachStepLine(m)} | {GrokRailText.ManualLine(m.Step)} | card={(card != null ? $"{card.PanelHeight.ToString("0.000", C)} m tall" : "none")}");
        }

        // ---------------- plumbing ----------------

        static CoachOverlay Overlay => Object.FindFirstObjectByType<CoachOverlay>(FindObjectsInactive.Include);

        /// Into the world from passthrough (the rails' views only show there). True when it switched.
        static bool EnterWorld()
        {
            if (AppState.Mode != AppMode.Passthrough) return false;
            AppCommands.OpenChest();
            return true;
        }

        static CoachOverlay Refreshed(CoachOverlay o) { o.Refresh(); return o; }

        static void Refresh()
        {
            Object.FindFirstObjectByType<JobRailView>(FindObjectsInactive.Include)?.Refresh();
            Object.FindFirstObjectByType<CoachRailView>(FindObjectsInactive.Include)?.Refresh();
            Overlay?.Refresh();
        }

        static List<AgentAction> Actions(string json) =>
            JArray.Parse(json).OfType<JObject>().Select(o => new AgentAction { name = (string)o["name"], args = o["args"] as JObject ?? new JObject() }).ToList();

        static void Feed(List<AgentAction> actions, int count = int.MaxValue)
        {
            foreach (var a in actions.Take(count)) AgentActions.ExecuteAll(new[] { a });
        }

        static List<JObject> Replies(string json) => JArray.Parse(json).OfType<JObject>().ToList();

        /// One recorded reply: its actions, with its spoken line as the reply (as AgentClient runs them).
        static void FeedReply(JObject reply)
        {
            var actions = (reply["actions"] as JArray ?? new JArray()).OfType<JObject>()
                .Select(o => new AgentAction { name = (string)o["name"], args = o["args"] as JObject ?? new JObject() }).ToList();
            AgentActions.ExecuteAll(actions, (string)reply["spoken"]);
        }

        static void FeedReplies(string json, int count)
        {
            foreach (var r in Replies(json).Take(count)) FeedReply(r);
        }
    }
}
#endif
