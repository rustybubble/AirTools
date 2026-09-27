#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Dev
{
    /// switchclean: every open UI closes on a world-model switch, in the running app (AgentHarness.SwitchCloseCheck
    /// (modelView); the backend's kitchen and Zabel gym, ServerConfig.SetOverride("http://127.0.0.1:8004") before Play).
    /// Every result is an [AirTools.Check] switchclose.* line; SwitchCloseResult() has the report. Nothing pays: the
    /// checkout's Pay hold is held by PressedOverride with a one-hour requirement and let go after the switch.
    /// A. **A real switch** (LoadSite, or `modelView`: show_model's wheel path) with everything up that can be up at once:
    ///    the Catalog and its keyboard, Settings, the Notebook (All sites on), the spec card (a selected part), Find parts'
    ///    results, the install coach card, a toast and an answer, the palm ring. All closed after; the log line names
    ///    them; in Model view the wheel stays.
    /// B. **Every main-slot card** (they share one slot, so one at a time) closed by the switch's close run
    ///    (AppCommands.CloseAllForSwitch): Sellers, Checkout, the Grok card, the overlay card, the survey card, the ladder
    ///    card, Adjust.
    /// C. **The checkout mid-hold** across a real switch (back to the kitchen): kept up while Pay is held, closed right
    ///    after the hold ends without paying; no /checkout request.
    public static class SwitchCloseCheck
    {
        const string Kitchen = SiteScopeCheck.Kitchen, Gym = SiteScopeCheck.Gym;
        static readonly List<string> s_Report = new List<string>();
        static string s_Summary = "not run";
        static bool s_Done = true;
        static int s_Pass, s_Total;

        static SceneStreamer Streamer => Services.Get<SceneStreamer>();
        static SceneRoot Root => Services.Get<SceneRoot>();
        static PartTool Parts => Services.Get<PartTool>();

        public static string Result() => $"{(s_Done ? "" : "(running) ")}{s_Summary}\n{string.Join("\n", s_Report)}";

        static void Check(string id, bool ok, string detail)
        {
            s_Total++;
            if (ok) s_Pass++;
            Log.Check(id, ok, detail);
            s_Report.Add($"{(ok ? "PASS" : "FAIL")} {id} {detail}");
        }

        static IEnumerator Until(System.Func<bool> done, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!done() && Time.realtimeSinceStartup < end) yield return null;
        }

        static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

        public static string Run(bool modelView = false, float timeout = 90f)
        {
            if (!s_Done) return "SwitchCloseCheck is already running: poll SwitchCloseResult()";
            if (Streamer == null || Root == null || Parts == null) return "no SceneStreamer / SceneRoot / PartTool (AirTools ▸ Wire Main Scene)";
            s_Report.Clear();
            s_Pass = s_Total = 0;
            s_Summary = "SwitchCloseCheck running";
            s_Done = false;
            if (!DemoRunner.Run(Routine(modelView, timeout), ex => { s_Summary = $"SwitchCloseCheck error {ex.GetType().Name}: {ex.Message}"; s_Done = true; Cleanup(); },
                    () => { s_Summary = $"SwitchCloseCheck {s_Pass}/{s_Total}"; s_Done = true; Cleanup(); }))
            {
                s_Done = true;
                return "another routine is running (DemoRunner busy)";
            }
            return "SwitchCloseCheck started: poll SwitchCloseResult()";
        }

        static bool Loaded(string site) =>
            Root.IsRuntimePackage && Root.Site == site && !Streamer.Loading && SiteScope.IsCurrent(site) && !SiteScope.Pending;

        static IEnumerator Load(string site, bool modelView, float timeout)
        {
            if (Loaded(site)) yield break;
            if (modelView) ModelViewCheck.Show(site);
            else { if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest(); AppCommands.LoadSite(site); }
            yield return null;
            yield return Until(() => Loaded(site), timeout);
            yield return Frames(3);
        }

        static IEnumerator World()
        {
            if (AppState.Mode == AppMode.Tabletop) AppCommands.SetTabletop(false);
            else if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            yield return Until(() => AppState.Mode == AppMode.World, 10f);
            yield return Frames(2);
        }

        static PartInstance PlacePart()
        {
            var root = Root;
            Vector3 insert = new Vector3(0f, 0f, 1.5f);
            Quaternion facing = Quaternion.identity;
            if (AppCommands.TryPlaceTarget(new PlacePartArgs { ComponentId = "dw1" }, root, out var t, out _)) { insert = t.Anchor; facing = t.Rotation; }
            var part = Services.Get<PartLoader>()?.LoadFromCatalog(PartScenarios.Ac);
            if (part != null) Parts.PlaceAt(part, PlacePartMath.OriginFor(insert, facing, part.LocalBox), facing);
            return part;
        }

        const string ToastText = "Scope toast · kitchen", AnswerText = "Scope answer about the kitchen";

        static bool ShowsText(string text)
        {
            float now = UiClock.Now;
            if (UiToast.Toast != null && UiToast.Toast.Showing && UiToast.Toast.Message.Contains(text)) return true;
            if (UiToast.ReplyCard != null && UiToast.ReplyCard.Showing && UiToast.ReplyCard.Message.Contains(text)) return true;
            var line = StatusLine.Current;
            return line != null && line.Model.Flashing(now) && line.Model.Flash.Contains(text);
        }

        static void Cleanup()
        {
            GrokRails.Clear();
            if (Services.TryGet<PalmMenu>(out var palm)) palm.Force(null);
            if (Services.TryGet<CheckoutPanel>(out var checkout) && checkout.hold != null) { checkout.hold.PressedOverride = null; checkout.hold.required = 1f; }
        }

        static IEnumerator Routine(bool modelView, float timeout)
        {
            string view = modelView ? "model_view" : "world";
            yield return Load(Kitchen, false, timeout);
            if (!Loaded(Kitchen)) { Check("switchclose.kitchen.loaded", false, $"the kitchen didn't load: {Streamer.Status}"); yield break; }
            yield return World();
            var parts = Parts;
            var part = PlacePart();

            // A. Everything that can be up at once, then a real switch.
            Services.TryGet<CatalogWindow>(out var catalog);
            Services.TryGet<AirTools.Structure.ScenePanel>(out var settings);
            Services.TryGet<NotebookPanel>(out var notebook);
            Services.TryGet<PartsBrowser>(out var browser);
            Services.TryGet<CoachRailView>(out var coach);
            Services.TryGet<PalmMenu>(out var palm);
            Services.TryGet<SpecCard>(out var spec);
            if (catalog != null) { AppCommands.ShowCatalog(); catalog.Model.SetKeyboard(true); }
            AppCommands.ShowScenePanel(true);
            if (notebook != null) { notebook.SetOpen(true); notebook.SetAllSites(true); }
            if (part != null) parts.Select(part);
            if (browser != null && part != null) browser.Adopt(new List<PartSummary> { part.Spec.ToSummary() }, "scope", "catalog");
            GrokRailsHarness.Stage("coach-check");
            UiToast.Show(ToastText, ColorRole.Info);
            UiToast.Reply(AnswerText);
            if (palm != null) palm.Force(true);
            yield return Frames(3);
            yield return new WaitForSecondsRealtime(0.3f);   // the spec card refreshes every 0.2 s
            var up = new List<string>();
            if (catalog != null && catalog.IsOpen) up.Add("Catalog");
            if (catalog != null && catalog.Model.KeyboardOpen) up.Add("Keyboard");
            if (settings != null && settings.IsOpen) up.Add("Settings");
            if (notebook != null && notebook.IsOpen) up.Add("Notebook");
            if (parts.Selected != null) up.Add("Spec card");
            if (browser != null && browser.Candidates.Count > 0) up.Add("Find parts");
            if (coach != null && coach.IsOpen) up.Add("Coach card");
            if (UiToast.AnyShowing()) up.Add("Toast");
            if (palm != null && palm.IsOpen) up.Add("Palm ring");
            Check("switchclose.kitchen.opened", up.Count >= 7, $"up before the switch: [{string.Join(", ", up)}]");

            int runs = SwitchClose.Runs;
            yield return Load(Gym, modelView, timeout);
            if (!Loaded(Gym)) { Check("switchclose.gym.loaded", false, $"the gym didn't load: {Streamer.Status}"); yield break; }
            yield return new WaitForSecondsRealtime(0.3f);
            var left = new List<string>();
            if (catalog != null && (catalog.IsOpen || catalog.Model.IsOpen)) left.Add("Catalog");
            if (catalog != null && (catalog.Model.KeyboardOpen || (catalog.keyboard != null && catalog.keyboard.IsOpen))) left.Add("Keyboard");
            if (settings != null && settings.IsOpen) left.Add("Settings");
            if (notebook != null && (notebook.IsOpen || notebook.AllSites)) left.Add("Notebook");
            if (parts.Selected != null || (spec != null && spec.Part != null)) left.Add("Spec card");
            if (browser != null && (browser.Candidates.Count > 0 || browser.LastQuery != null)) left.Add("Find parts");
            if (coach != null && coach.IsOpen) left.Add("Coach card");
            if (ShowsText(ToastText) || ShowsText(AnswerText)) left.Add("Toast");
            if (palm != null && palm.IsOpen) left.Add("Palm ring");
            bool wheel = !modelView || (Services.TryGet<ModelWheel>(out var w) && w.Shown);
            Check($"switchclose.{view}.all_closed", left.Count == 0 && SwitchClose.Runs > runs && SwitchClose.LastSite == Gym && wheel,
                $"still up: [{string.Join(", ", left)}] | {SwitchClose.LastLine}{(modelView ? $" | wheel shown={wheel}" : "")}");
            var named = up.Where(n => !SwitchClose.LastClosed.Contains(n)).ToList();
            Check($"switchclose.{view}.logged", named.Count == 0 && SwitchClose.LastKept.Count == 0,
                $"closed but not named: [{string.Join(", ", named)}] kept=[{string.Join(", ", SwitchClose.LastKept)}]");
            GrokRails.Clear();

            // B. Every main-slot card, one at a time, closed by the switch's close run.
            yield return World();
            var gymPart = PlacePart();
            if (gymPart != null) parts.Select(gymPart);
            yield return Frames(2);
            var cards = new List<(string name, System.Func<bool> open, System.Func<bool> isOpen)>();
            if (Services.TryGet<SellerPanel>(out var sellers)) cards.Add(("Sellers", () => sellers.Show(gymPart, "price"), () => sellers.IsOpen));
            if (Services.TryGet<CheckoutPanel>(out var checkout0)) cards.Add(("Checkout", () => AppCommands.StartCheckout(0), () => checkout0.State != CheckoutState.Closed || checkout0.window.IsOpen));
            if (Services.TryGet<GrokCard>(out var grokCard))
                cards.Add(("Grok card", () => grokCard.ShowManual(new ManualAnswerView { PartId = gymPart != null ? gymPart.Spec.id : "x", Answer = "Scope manual answer", Quote = "Scope quote", Page = 3 }), () => grokCard.IsOpen));
            if (Services.TryGet<GrokOverlayCard>(out var overlayCard))
                cards.Add(("Overlay card", () => { overlayCard.Show(GrokOverlayKind.Plan, "Plan", "scope", "Scope plan card", "AI preview", false); return true; }, () => overlayCard.IsOpen));
            if (Services.TryGet<SurveyCard>(out var surveyCard))
                cards.Add(("Survey card", () => surveyCard.Show(Newtonsoft.Json.Linq.JObject.Parse("{\"request_id\":\"scope\",\"label\":\"doors\",\"groups\":[]}")), () => surveyCard.IsOpen));
            if (Services.TryGet<LadderCard>(out var ladderCard) && ladderCard.window != null)
                cards.Add(("Ladder card", () => { ladderCard.window.Open(); return true; }, () => ladderCard.IsOpen));
            cards.Add(("Adjust", () => AppCommands.AdjustPlacement(true), () => PlacementEditor.Current != null && PlacementEditor.Current.IsAdjusting));
            foreach (var (name, open, isOpen) in cards)
            {
                bool opened = false;
                if (gymPart != null) parts.Select(gymPart);   // the last close run deselected it (the spec card)
                try { opened = open() && isOpen(); } catch (System.Exception ex) { Log.Warn($"SwitchCloseCheck: {name} didn't open: {ex.Message}"); }
                yield return Frames(2);
                opened &= isOpen();
                string line = AppCommands.CloseAllForSwitch(Gym + " (probe)");
                yield return Frames(2);
                Check($"switchclose.card.{name.Replace(' ', '_').ToLowerInvariant()}", opened && !isOpen() && SwitchClose.LastClosed.Contains(name),
                    $"opened={opened} open after={isOpen()} | {line}");
            }

            // C. The checkout mid-hold across a real switch: kept while Pay is held, closed right after the hold ends.
            if (!Services.TryGet<CheckoutPanel>(out var checkout) || checkout.hold == null || gymPart == null)
            {
                Check("switchclose.checkout.hold", false, "no CheckoutPanel with a hold / no part on the gym");
                yield break;
            }
            var client = Services.Get<PartsClient>();
            int paid = client != null ? client.CheckoutRequests : 0;
            parts.Select(gymPart);
            bool started = AppCommands.StartCheckout(0);
            checkout.hold.required = 3600f;   // nothing can pay: the ring can't fill in this test
            checkout.hold.PressedOverride = true;
            yield return Until(() => checkout.hold.Holding, 5f);
            bool holding = checkout.hold.Holding;
            yield return Load(Kitchen, modelView, timeout);
            bool kept = checkout.State != CheckoutState.Closed && checkout.window.IsOpen && checkout.CloseAfterHold
                        && SwitchClose.LastKept.Contains($"Checkout ({SwitchClose.HoldingPay})");
            string keptLine = SwitchClose.LastLine;
            checkout.hold.PressedOverride = false;   // let go without paying
            yield return Frames(3);
            bool closedAfter = checkout.State == CheckoutState.Closed && !checkout.window.IsOpen && !checkout.CloseAfterHold;
            checkout.hold.PressedOverride = null;
            checkout.hold.required = 1f;
            int requests = client != null ? client.CheckoutRequests - paid : 0;
            Check($"switchclose.{view}.checkout_hold", started && holding && Loaded(Kitchen) && kept && closedAfter && requests == 0,
                $"opened={started} holding at the switch={holding} kept through it={kept} ({keptLine}) closed after letting go={closedAfter} /checkout requests={requests}");
            Check("switchclose.checkout.paying", true, "a payment being authorized is never closed (Paying): EditMode SwitchCloseSceneTests (nothing pays in the app)");

            // D. A running job (a fixture replace run: no server call is needed to stop it) and the person's switch.
            GrokRails.Reset();
            GrokRails.Sender = AppCommands.SendCommand;
            AgentActions.ExecuteAll(new[] { new AgentAction { name = "job_started", args = Newtonsoft.Json.Linq.JObject.Parse(
                "{\"run_id\":\"switchclean-fixture\",\"steps\":[\"remove\",\"measure\",\"scale\",\"search\",\"pick\",\"model\",\"place\"],\"kind\":\"replace\",\"title\":\"Replace the dishwasher\"}") } });
            bool running = GrokRails.Running && GrokRails.Site.Site == Kitchen;
            yield return Load(Gym, modelView, timeout);
            yield return Frames(2);
            var job = GrokRails.Job;
            bool stopped = job != null && job.IsCancelled && !GrokRails.Poll.Active && SwitchClose.LastClosed.Contains("Job");
            bool toast = ShowsText("Stopped the dishwasher job: you switched to the Zabel gym");
            Check($"switchclose.{view}.job", running && stopped && toast,
                $"running on the kitchen={running} stopped by the switch={stopped} (strip \"{GrokRailText.StripTags(GrokRailText.JobStatus(job))}\", poll {GrokRails.Poll.StopReason}) " +
                $"toast={toast} | {SwitchClose.LastLine}");
            GrokRails.Clear();
        }
    }
}
#endif
