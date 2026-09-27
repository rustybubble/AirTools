using System.Collections.Generic;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Tools;
using AirTools.UI;
using UnityEngine;

namespace AirTools
{
    /// D7 (UX W1.8): what AppCommands.ResetDemo does — the app back to its first-run state between judges, without a
    /// restart (docs/ux/research/04-journey-and-flows.md §3.4 Q9):
    /// - passthrough, no world; the rig back at the scene's spawn (Locomotion.GoHome); no tool in hand, so entering the
    ///   world equips ToolManager.Default again (decision D1);
    /// - no tapes (finished, in progress or parked), levels, ladders, parts (held, parked, placed, arrays), scene pins;
    ///   every scene part back in (none taken out); every tool's Undo / Redo history empty;
    /// - the notebook's session entries gone (the exported CSV / HTML files on disk are not touched), the checkout closed,
    ///   the "what else do I need?" lines and the limits chip cleared, nothing bought, nothing on the table;
    /// - Find parts back to hidden-until-the-first-reading, windows closed, fall edges / structure overlay off, snapping on;
    /// - no Grok state on screen (declutter S1): no job rail or install coach, no scene overlays, no Grok cards, no quad;
    /// - the guide rail and coach forgotten (never entered, no ring openings, every hint may show again), accessibility
    ///   toggles off, and a new server session id, so the agent's memory and limits don't carry over;
    /// - the scene's scale back to its site's default (scalemodels: the kitchen's ×1.63, SiteScales), the last person's
    ///   Set scale forgotten.
    /// It refuses while a payment is being authorized: a receipt arriving afterwards would land in the next person's
    /// notebook. It never pays and never touches the hold.
    public static class DemoReset
    {
        public static int Count { get; private set; }
        public static string LastReport { get; private set; } = "";
        public static float LastAt { get; private set; } = -999f;

        /// (report) after every completed reset (PresenterLink and the harness zero their judge counters).
        public static event System.Action<string> Completed;

        public static bool Run(out string report)
        {
            Services.TryGet<CheckoutPanel>(out var checkout);
            if (checkout != null && checkout.State == CheckoutState.Paying)
            {
                report = "refused: a payment is being authorized · reset again in a few seconds";
                LastReport = report;
                Log.Warn($"ResetDemo {report}");
                return false;
            }

            // Stop what is running.
            AppCommands.StopSurvey();

            // Purchases off first: leaving the world below must not put this judge's parts on the table.
            Services.TryGet<TakeItHome>(out var home);
            if (home != null) home.ResetSession();

            // Windows (the checkout is not paying, so it closes too; CloseWindows also closes every Grok card).
            AppCommands.CloseWindows();
            if (Services.TryGet<AirTools.Agent.SurveyCard>(out var survey)) survey.Close();
            if (Services.TryGet<PalmMenu>(out var palm)) palm.CloseFromController();

            // Declutter S1: the last judge's Grok state — the job rail and the install coach (GrokRails), the scene
            // overlays (plan, survey pins, coverage, labels), the picture / video quad.
            ResetGrok();

            // Tools, with their notebook rows and their undo / redo stacks.
            var tools = Services.Get<ToolManager>();
            var measure = tools != null && tools.measure != null ? tools.measure : Services.Get<MeasureTool>();
            var level = tools != null && tools.level != null ? tools.level : Services.Get<LevelTool>();
            var ladder = tools != null && tools.ladder != null ? tools.ladder : Services.Get<LadderTool>();
            var parts = tools != null && tools.parts != null ? tools.parts : Services.Get<PartTool>();
            int before = Notebook.Entries.Count;
            if (measure != null) { measure.ClearAll(); measure.AreaMode = false; }
            if (level != null) level.ClearAll();
            if (ladder != null) ladder.ClearAll();
            if (parts != null) parts.ClearAll();
            if (Services.TryGet<AirTools.Scene.SceneParts>(out var sceneParts)) sceneParts.ResetSession();
            ModelCycler.Reset();           // e2e: no model switching, no gap tapes remembered
            AirTools.Scene.Gaps.Forget();  // e2e
            if (Services.TryGet<AirTools.Agent.SceneAsk>(out var ask)) ask.ClearPins();
            AirTools.Scene.SiteScope.ClearParked();   // sitescope: every other site's items, stacks and pins too
            if (Services.TryGet<AirTools.Structure.FallEdges>(out var edges) && edges.Visible) edges.SetVisible(false);
            if (Services.TryGet<AirTools.Structure.StructureOverlay>(out var overlay) && overlay.Visible) overlay.SetVisible(false);
            AirTools.Scene.SnapService.Enabled = true;
            // scalemodels: the scene back to its site's default scale (the kitchen's ×1.63; SiteScales), the last person's
            // tape + Set scale forgotten. After the tools are cleared (nothing left to rescale), before the notebook goes.
            AirTools.Structure.ScaleCalibration.Reset();

            // Commerce.
            AppCommands.ClearBom();
            if (checkout != null) checkout.Close();
            if (LimitsChip.Current != null || LimitsChip.LastRefused) LimitsChip.Set(null);
            if (Services.TryGet<PartsBrowser>(out var browser)) browser.ResetSession();

            // The rest of the notebook (notes, purchases, links, BOM, scale…). Files on disk stay.
            int removed = before - Notebook.Entries.Count + Notebook.ClearSession();

            // Out of the world, back at the spawn, nothing in hand (entering equips ToolManager.Default: D1).
            if (AppState.Mode != AppMode.Passthrough) AppCommands.CloseChest();
            if (home != null) home.ResetSession();   // a transition that had deferred the table has nothing to show
            if (Services.TryGet<Locomotion>(out var loco)) loco.GoHome();
            if (tools != null) tools.Equip(ToolKind.None);
            EditHistory.NotifyChanged();

            // Guide rail, coach, input clocks, accessibility, server session.
            float now = Time.unscaledTime;
            if (Services.TryGet<GuideRail>(out var rail)) rail.ResetSession();
            else if (Services.TryGet<CoachService>(out var coach)) coach.ResetSession();
            InputActivity.Reset(now);
            InputHints.Reset();
            UiSettings.ResetForNextPerson();
            string session = SessionInfo.Renew();

            Count++;
            LastAt = now;
            bool clean = Verify(out var state);
            report = $"reset #{Count}: removed {removed} notebook entries; {state}; session={session}";
            LastReport = report;
            Log.Check("D7.reset", clean, report);
            UiToast.Show("Reset · ready for the next person", ColorRole.Success);
            Completed?.Invoke(report);
            return true;
        }

        /// The first-run state, checked against the live services: `detail` lists every value, and names what isn't
        /// clean. Services that aren't in the scene are skipped.
        public static bool Verify(out string detail)
        {
            var bad = new List<string>();
            void Need(bool ok, string what) { if (!ok) bad.Add(what); }
            var tools = Services.Get<ToolManager>();
            var measure = tools != null && tools.measure != null ? tools.measure : Services.Get<MeasureTool>();
            var level = tools != null && tools.level != null ? tools.level : Services.Get<LevelTool>();
            var ladder = tools != null && tools.ladder != null ? tools.ladder : Services.Get<LadderTool>();
            var parts = tools != null && tools.parts != null ? tools.parts : Services.Get<PartTool>();
            Services.TryGet<TakeItHome>(out var home);
            Services.TryGet<CheckoutPanel>(out var checkout);
            Services.TryGet<PartsBrowser>(out var browser);
            Services.TryGet<GuideRail>(out var rail);
            Services.TryGet<AirTools.Scene.SceneParts>(out var sceneParts);

            Need(AppState.Mode == AppMode.Passthrough, "mode");
            if (tools != null) Need(tools.Active == ToolKind.None, "tool");
            if (measure != null) Need(measure.Session.Count == 0 && measure.Shapes.Count == 0 && !measure.AreaMode, "tapes");
            if (level != null) Need(level.Placements.Count == 0, "levels");
            if (ladder != null) Need(ladder.Placements.Count == 0, "ladders");
            if (parts != null) Need(parts.Held == null && parts.Parked == null && parts.PlacedParts.Count == 0, "parts");
            if (sceneParts != null) Need(sceneParts.RemovedCount == 0 && sceneParts.SavedSites == 0, "scene parts");   // sitescope: && SavedSites
            Need(AirTools.Scene.SiteScope.ParkedCount == 0, "other sites");   // sitescope
            Need(Notebook.Entries.Count == 0, "notebook");
            Need(!EditHistory.CanUndo && !EditHistory.CanRedo, "undo/redo");
            Need(AppCommands.CurrentBom == null, "bom");
            if (home != null) Need(home.Purchases.Count == 0 && home.OnTable.Count == 0, "purchases");
            if (checkout != null) Need(checkout.State == CheckoutState.Closed, "checkout");
            if (browser != null) Need(browser.Candidates.Count == 0 && browser.LastQuery == null && !browser.Searching, "find parts");
            if (rail != null) Need(rail.RingOpensAll == 0 && rail.CoachState.Current == CoachRuleId.None && ShowsClear(rail.CoachState), "guide rail");
            Need(!UiSettings.HighContrast && !UiSettings.ReducedMotion, "accessibility");
            string grok = GrokLeft();
            Need(grok.Length == 0, "grok");
            string scale = ScaleLeft(out bool scaleClean);   // scalemodels
            Need(scaleClean, "scale");

            detail = $"mode={AppState.Mode} tool={(tools != null ? tools.Active.ToString() : "-")} " +
                     $"tapes={(measure != null ? measure.Shapes.Count : 0)}+{(measure != null ? measure.Session.Count : 0)}pts " +
                     $"levels={(level != null ? level.Placements.Count : 0)} ladders={(ladder != null ? ladder.Placements.Count : 0)} " +
                     $"parts={(parts != null ? parts.PlacedParts.Count : 0)}{(parts != null && parts.Held != null ? "+held" : "")} " +
                     $"scene_parts_out={(sceneParts != null ? sceneParts.RemovedCount : 0)} " +
                     $"parked_sites={AirTools.Scene.SiteScope.ParkedCount} " +   // sitescope
                     $"notebook={Notebook.Entries.Count} undo={EditHistory.CanUndo} redo={EditHistory.CanRedo} " +
                     $"bom={(AppCommands.CurrentBom != null)} purchases={(home != null ? home.Purchases.Count : 0)} " +
                     $"checkout={(checkout != null ? checkout.State.ToString() : "-")} " +
                     $"rail_ring_opens={(rail != null ? rail.RingOpensAll : 0)} grok={(grok.Length == 0 ? "-" : grok)} scale={scale}" +
                     (bad.Count > 0 ? $" NOT CLEAN: {string.Join(", ", bad)}" : " clean");
            return bad.Count == 0;
        }

        /// scalemodels: the loaded scan's scale and its source ("×1.63 site_default"); clean when it is the site's default
        /// (SiteScales; ×1 for a site without one). "-" with nothing loaded or on the built-in scene.
        public static string ScaleLeft(out bool clean)
        {
            clean = true;
            if (!Services.TryGet<AirTools.Scene.SceneRoot>(out var root) || root.Content == null || !root.IsRuntimePackage) return "-";
            Services.TryGet<AirTools.Scene.SceneStreamer>(out var streamer);
            float want = AirTools.Scene.SiteScales.ResetTo(streamer != null ? streamer.SiteDefaultScale : 1f).Scale;
            var source = AirTools.Scene.SiteScales.Consistent(root.Calibration, streamer != null ? streamer.ScaleSource : AirTools.Scene.ScaleSource.None);
            clean = Mathf.Abs(root.Calibration - want) < 1e-4f && source != AirTools.Scene.ScaleSource.User;
            return $"×{AirTools.Scene.SiteScales.Factor(root.Calibration)}_{AirTools.Scene.SiteScales.Wire(source)}";
        }

        /// Declutter S1: forget the Grok lanes' on-screen state. Safety verdicts stay (they are facts about a part, and the
        /// checkout's recall line must keep saying why it won't sell a recalled one).
        static void ResetGrok()
        {
            AirTools.Agent.Grok.GrokRails.Clear();
            AirTools.Agent.Grok.GrokState.SurveyId = null;
            if (Services.TryGet<AirTools.Agent.Grok.GrokOverlays>(out var overlays)) overlays.ClearAll();
            if (Services.TryGet<AirTools.Agent.Grok.GrokCard>(out var card)) card.Close();
            if (Services.TryGet<AirTools.Agent.Grok.GrokOverlayCard>(out var overlayCard)) overlayCard.Close();
            if (Services.TryGet<AirTools.Agent.Grok.ReimagineQuad>(out var quad) && quad.Mode != AirTools.Agent.Grok.ReimagineQuad.QuadMode.Hidden) quad.Hide();
        }

        /// What of the Grok lanes is still up, space-separated ("" when nothing): job, coach, overlay kinds, cards, quad.
        public static string GrokLeft()
        {
            var left = new List<string>();
            if (AirTools.Agent.Grok.GrokRails.Job != null) left.Add("job");
            if (AirTools.Agent.Grok.GrokRails.Coach != null) left.Add("coach");
            if (Services.TryGet<AirTools.Agent.Grok.GrokOverlays>(out var overlays))
                foreach (AirTools.Agent.Grok.GrokOverlayKind k in System.Enum.GetValues(typeof(AirTools.Agent.Grok.GrokOverlayKind)))
                    if (k != AirTools.Agent.Grok.GrokOverlayKind.None && overlays.IsShown(k)) left.Add(k.ToString().ToLowerInvariant());
            if (Services.TryGet<AirTools.Agent.Grok.GrokCard>(out var card) && card.IsOpen) left.Add("card");
            if (Services.TryGet<AirTools.Agent.Grok.GrokOverlayCard>(out var overlayCard) && overlayCard.IsOpen) left.Add("overlay-card");
            if (Services.TryGet<AirTools.Agent.Grok.ReimagineQuad>(out var quad) && quad.Mode != AirTools.Agent.Grok.ReimagineQuad.QuadMode.Hidden) left.Add("quad");
            return string.Join(" ", left);
        }

        static bool ShowsClear(CoachState st)
        {
            for (int i = 0; i < st.Shows.Length; i++) if (st.Shows[i] != 0) return false;
            return true;
        }
    }
}
