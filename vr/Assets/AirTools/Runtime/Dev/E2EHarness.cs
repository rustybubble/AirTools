#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Dev
{
    /// e2e: the whole "replace the dishwasher" flow against the live backend, with real HTTP, through the path a spoken
    /// command takes (AgentClient.SendText → /agent/command → the reply's actions → LocalIntents where the server sent
    /// nothing for a phrase). Each step waits (with a timeout) and logs one [AirTools.Check] line:
    ///   e2e.remove   "remove the dishwasher"        the component is out (its gap open)
    ///   e2e.measure  "measure the gap"              width / depth / height tapes in the notebook ≈ the file's gap
    ///   e2e.search   "find a dishwasher that fits"  N candidates, each with a model URL; e2e.search.cavity: the gap's
    ///                                               W × H × D went with it (context.cavity, and the search body's)
    ///   e2e.place    "put it in there"              a model stands at the cavity insert; e2e.fits: it fits (green)
    ///   e2e.cycle    "next one" ×2, "previous one"  ≥ 3 different models (or all there are) at the same spot
    ///   e2e.undo     Undo                           the model leaves the gap, the component stays out
    ///   e2e.restore  "put it back"                  the component is back, nothing in the gap
    /// and e2e.summary. Every phrase logs "via server" (the reply's own action did it) or "via headset" (LocalIntents).
    /// Run: AgentHarness.E2E(site, component[, query]) then poll AgentHarness.E2EResult(); the presenter's "replace" beat
    /// runs it on the loaded site (DemoBeats).
    public static partial class E2EHarness   // autonomy: the one-sentence mode is E2EHarness.Auto.cs
    {
        public const float ReplyTimeout = 45f, SiteTimeout = 90f, SearchTimeout = 150f, ModelTimeout = 180f, WorldTimeout = 8f;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static float Now => Time.realtimeSinceStartup;

        static readonly List<string> s_Log = new List<string>();
        static bool s_Done, s_Pass;
        static int s_PassN, s_Total;
        static string s_Summary = "not run";
        static string s_Via = "-";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_Log.Clear(); s_Done = s_Pass = false; s_PassN = s_Total = 0; s_Summary = "not run"; }

        public class Options
        {
            /// The scene package (null: the one loaded).
            public string Site;
            /// The component's id or label (null: the dishwasher if there is one, else the first removable).
            public string Component;
            /// What to search for (null: the component's label).
            public string Query;
            /// Start from a demo reset (a fresh server session too).
            public bool Reset = true;
            /// Models to show before "previous one".
            public int Models = 3;
            /// A phrase that sets the scale from the gap after it's taped ("the opening is 34 and a half inches tall": the
            /// kitchen scan reads ~1.47× small indoors, docs/demo-prompts.md); null: the scene's scale as it is.
            public string Scale;
        }

        /// Start the run (Play mode); poll Result().
        public static string Start(Options o)
        {
            if (!Application.isPlaying) return "Play mode only";
            s_Log.Clear(); s_Done = s_Pass = false; s_PassN = s_Total = 0; s_Summary = "running…";
            if (!DemoRunner.Run(Flow(o ?? new Options(), (ok, d) => { s_Pass = ok; s_Summary = d; }), ex => { s_Log.Add($"exception: {ex}"); s_Summary = $"exception: {ex.Message}"; },
                    () => s_Done = true))
                return "another routine is running (DemoRunner busy)";
            return $"e2e started ({o?.Site ?? "loaded site"}, {o?.Component ?? "default component"}) against {ServerConfig.Current} — poll AgentHarness.E2EResult()";
        }

        public static string Result() =>
            $"{(s_Done ? (s_Pass ? "PASS" : "FAIL") : "running…")} {s_Summary}\n{string.Join("\n", s_Log)}";

        /// The kitchen's scan reads ~1.47× small indoors (its scale is from the drone's altitude caption): the flow sets
        /// the scale from the dishwasher opening's standard height (34½″ under a 36″ counter) before the search, and the
        /// heads-up line says so ("Scale set from the gap's height · 34½″ · ×1.48").
        public const string KitchenScale = "the opening is 34 and a half inches tall";

        /// The presenter's "replace" beat: the flow on the loaded site, its default component (the dishwasher), no reset;
        /// on the kitchen, the scale from the opening first (KitchenScale).
        public static IEnumerator Beat(Action<bool, string> done)
        {
            s_Log.Clear(); s_PassN = s_Total = 0;
            var root = Services.Get<SceneRoot>();
            // On a scene with nothing to take out (the built-in facade after RunDemo, a GT scan), the beat goes to the kitchen.
            var parts = Services.Get<AirTools.Scene.SceneStreamer>()?.Parts;
            bool hasParts = parts != null && parts.Removable.Count > 0;
            string site = hasParts ? null : "kitchen";
            bool kitchen = site == "kitchen" || (root != null && root.Site == "kitchen");
            return Flow(new Options { Reset = false, Site = site, Scale = kitchen ? KitchenScale : null }, done);
        }

        static void Check(string id, bool ok, string detail)
        {
            s_Total++;
            if (ok) s_PassN++;
            Log.Check(id, ok, detail);
            s_Log.Add($"{(ok ? "PASS" : "FAIL")} {id} {detail}");
        }

        static IEnumerator Flow(Options o, Action<bool, string> done)
        {
            var agent = Services.Get<AgentClient>();
            var browser = Services.Get<PartsBrowser>();
            if (agent == null || browser == null) { done(false, "no AgentClient / PartsBrowser in the scene"); yield break; }
            s_Log.Add($"server {ServerConfig.Current} session {SessionInfo.Id}");

            // ---- 0. a clean start in the world, on the site
            if (o.Reset) { AppCommands.ResetDemo(); yield return null; }
            yield return EnsureWorld();
            var root = Services.Get<SceneRoot>();
            var streamer = Services.Get<SceneStreamer>();
            if (!string.IsNullOrEmpty(o.Site) && (root == null || root.Site != o.Site))
            {
                AppCommands.LoadSite(o.Site);
                float t0 = Now;
                yield return null;
                while (Now - t0 < SiteTimeout && (streamer == null || streamer.Loading || root == null || root.Site != o.Site)) yield return null;
                yield return EnsureWorld();
            }
            var parts = Services.Get<SceneParts>();
            string site = root != null ? root.Site ?? "built-in" : "-";
            if (parts == null || !parts.HasParts)
            {
                Check("e2e.site", false, $"no removable parts in {site} ({streamer?.PartsStatus ?? "no streamer"}): the package needs parts.r<rev>.json");
                done(false, Summary(site));
                yield break;
            }
            if (o.Reset) parts.ResetSession();
            root.SetVisible(true);
            var comp = o.Component != null ? parts.Find(o.Component) ?? parts.Find(LocalIntents.Singular(o.Component)) : parts.Find("dishwasher") ?? parts.Removable.FirstOrDefault();
            if (comp == null || !comp.removable)
            {
                Check("e2e.site", false, $"no removable '{o.Component}' in {site} (have {string.Join(", ", parts.Removable.Select(x => $"{x.id} {x.label}"))})");
                done(false, Summary(site));
                yield break;
            }
            string noun = comp.DisplayName.ToLowerInvariant();
            string query = string.IsNullOrWhiteSpace(o.Query) ? noun : o.Query.Trim();
            s_Log.Add($"site {site} component {comp.id} \"{noun}\" query \"{query}\" parts: {parts.Report().Split('\n')[0]}");

            // ---- 1. remove
            yield return Say($"remove the {noun}");
            var gap = default(Gap);
            bool removed = parts.IsRemoved(comp.id) && Gaps.TryGet(out gap, comp.id);
            Check("e2e.remove", removed, $"{comp.id} out={parts.IsRemoved(comp.id)} via {s_Via} | {parts.LastAction}");
            if (!removed) { done(false, Summary(site)); yield break; }
            var file = gap.SizeM;

            // ---- 2. measure the gap
            int entries = Notebook.Entries.Count;
            yield return Say("measure the gap");
            bool measured = Gaps.TryMeasured(comp.id, out var m);
            var gapEntries = Notebook.Entries.Skip(Math.Min(entries, Notebook.Entries.Count)).Where(e => e.Tool == "measure" && (e.DisplayTitle ?? "").Contains(" gap ")).ToList();
            bool wOk = measured && Near(m.x, file.x), dOk = measured && Near(m.z, file.z), hOk = measured && (gap.Box.OpenTop ? float.IsNaN(m.y) : Near(m.y, file.y));
            Check("e2e.measure", wOk && dOk && hOk && gapEntries.Count >= (gap.Box.OpenTop ? 2 : 3),
                $"cavity W {F(m.x)} D {F(m.z)} H {F(m.y)} m vs file {F(file.x)} × {F(file.z)} × {F(file.y)} (W × D × H, ±1 cm); notebook +{gapEntries.Count} [{string.Join("; ", gapEntries.Select(e => $"{e.DisplayTitle} {e.ValueSI.ToString("0.000", C)} m"))}] via {s_Via}");

            // ---- 2b. set the scale from the gap (a known opening size), when asked
            if (!string.IsNullOrWhiteSpace(o.Scale))
            {
                var said = LocalIntents.Match(o.Scale);
                float before = root.Calibration;
                yield return Say(o.Scale);
                Gaps.TryGet(out gap, comp.id);   // the calibration changed: the gap's real size too
                Gaps.TryMeasured(comp.id, out var sm);
                float got = said.Axis == "w" ? sm.x : said.Axis == "d" ? sm.z : sm.y;
                // (A second run starts at the calibration the first one set: the tape then already reads the size said.)
                bool scaled = said.Kind == LocalIntentKind.ScaleGap && Mathf.Abs(got - (float)said.Metres) <= 0.005f;
                file = gap.SizeM;
                Check("e2e.scale", scaled,
                    $"\"{o.Scale}\": calibration {before.ToString("0.####", C)} → {root.Calibration.ToString("0.####", C)} (×{(root.Calibration / Mathf.Max(before, 1e-6f)).ToString("0.000", C)}); " +
                    $"gap {said.Axis} tape {F(got)} m (said {said.Metres.ToString("0.0000", C)}); gap now {F(file.x)} × {F(file.y)} × {F(file.z)} m (W × H × D) via {s_Via} | {AirTools.Structure.ScaleCalibration.LastResult}");
            }

            // ---- 3. find parts that fit
            int searches = browser.SearchCount;
            var client = Services.Get<PartsClient>();
            string bodyBefore = client?.LastSearchBody;
            yield return Say($"find a {query} that fits");
            float s0 = Now;
            while (Now - s0 < SearchTimeout && (browser.Searching || browser.SearchCount == searches)) yield return null;
            var cands = browser.Candidates.ToList();
            var withModels = cands.Where(c => !string.IsNullOrEmpty(c.model_url)).ToList();
            var gapMm = Gaps.FitSizeM(gap) * 1000f;
            var dims = cands.Select(c => c.spec?.dims_mm ?? c.dims_mm).ToList();
            Check("e2e.search", cands.Count > 0 && withModels.Count == cands.Count,
                $"N={cands.Count} with models={withModels.Count} from {browser.Source ?? "-"} in {(Now - s0).ToString("0.0", C)} s, {CavityFit.CountFitting(dims, gapMm)} fit {CavityFit.MmText(gapMm)} (W × H × D): " +
                string.Join("; ", cands.Select((c, i) => $"[{i}] {c.id} {Dim(dims[i])} {(CavityFit.Fits(dims[i], gapMm) ? "fits" : "too big")}")) + $" via {s_Via}");
            bool ctxCavity = agent.LastContext != null && agent.LastContext.ContainsKey("cavity");
            bool bodyCavity = client != null && client.LastSearchBody != bodyBefore && (client.LastSearchBody ?? "").Contains("\"cavity\"");
            Check("e2e.search.cavity", ctxCavity && (s_Via == "server" || bodyCavity),
                $"context.cavity={ctxCavity} search body cavity={(client?.LastSearchBody == bodyBefore ? "(the server's search)" : bodyCavity.ToString())}");
            if (cands.Count == 0) { done(false, Summary(site)); yield break; }

            // ---- 4. put the best one in
            yield return Say("put it in there");
            yield return WaitModel(gap, null);
            var expect = ExpectedAnchor(comp.id, root);
            var first = Gaps.ModelIn(gap);
            float off = first != null && expect.HasValue ? Vector3.Distance(Anchor(first), expect.Value) : float.NaN;
            Check("e2e.place", first != null && off < 0.01f,
                $"{first?.Spec?.id ?? "nothing"} (candidate {IndexOf(cands, first)}) at the insert ±{(float.IsNaN(off) ? "-" : (off * 1000f).ToString("0", C))} mm via {s_Via} | {ModelCycler.LastAction}{(ModelCycler.LastError != null ? $" error: {ModelCycler.LastError}" : "")}");
            var fit = first != null ? first.Fit : null;
            int best = CavityFit.Best(dims, gapMm);
            // Green, or amber "tight" (within CavityFit.TightMm: the gap is an estimate and appliance legs adjust) when it is
            // the best candidate there is: the kitchen's Whirlpool is 876 mm in an opening scaled to 34½″ = 876 mm.
            bool fitsOrTight = fit != null && (fit.Status == FitStatus.Green || (fit.Status == FitStatus.Amber && IndexOf(cands, first) == best));
            Check("e2e.fits", fitsOrTight,
                $"\"{AirTools.UI.Copy.FitLine(fit)}\" {fit?.Headline ?? "-"}; best candidate {best} ({(best >= 0 && CavityFit.Fits(dims[best], gapMm) ? "fits" : "nothing fits")})");
            if (first == null) { done(false, Summary(site)); yield break; }
            var spot = Anchor(first);

            // ---- 5. switch models at the same spot
            var ids = new List<string> { first.Spec.id };
            float worst = 0f;
            int want = Math.Min(Math.Max(1, o.Models), cands.Count);
            for (int k = 1; k < want; k++)
            {
                string before = Gaps.ModelIn(gap)?.Spec?.id;
                yield return Say("next one");
                yield return WaitModel(gap, before);
                var cur = Gaps.ModelIn(gap);
                if (cur == null || cur.Spec.id == before) { s_Log.Add($"  next #{k}: no new model ({ModelCycler.LastAction}{(ModelCycler.LastError != null ? $", {ModelCycler.LastError}" : "")})"); continue; }
                ids.Add(cur.Spec.id);
                worst = Mathf.Max(worst, Vector3.Distance(Anchor(cur), spot));
                s_Log.Add($"  next #{k}: {cur.Spec.id} (candidate {IndexOf(cands, cur)}) {AirTools.UI.Copy.FitLine(cur.Fit)} via {s_Via} | {SwapNote()}");
            }
            int back = -1, from = -1;
            if (want > 1)
            {
                var beforeModel = Gaps.ModelIn(gap);
                from = IndexOf(cands, beforeModel);
                yield return Say("previous one");
                yield return WaitModel(gap, beforeModel?.Spec?.id);
                var prev = Gaps.ModelIn(gap);
                back = IndexOf(cands, prev);
                if (prev != null) worst = Mathf.Max(worst, Vector3.Distance(Anchor(prev), spot));
                s_Log.Add($"  previous: candidate {from} → {back} via {s_Via}");
            }
            int distinct = ids.Distinct().Count();
            bool previousOk = want < 2 || (back >= 0 && back == ModelCycler.Wrap(from - 1, cands.Count));
            Check("e2e.cycle", distinct >= want && worst < 0.01f && Gaps.ModelIn(gap) != null && PlacedInGap(gap) == 1 && previousOk,
                $"{distinct} different models of {cands.Count} [{string.Join(", ", ids)}] at the same spot ±{(worst * 1000f).ToString("0", C)} mm; models in the gap: {PlacedInGap(gap)}; previous: candidate {from} → {back}");

            // ---- 6. undo takes the model out of the gap (or, after the placement editor's swap, back to the one before)
            var shown = Gaps.ModelIn(gap);
            bool undone = AppCommands.Undo();
            yield return null;
            var after = Gaps.ModelIn(gap);
            bool changed = shown != null && (after == null || after != shown);
            Check("e2e.undo", undone && changed && parts.IsRemoved(comp.id),
                $"undo={undone} gap had {shown?.Spec?.id ?? "nothing"} → now {after?.Spec?.id ?? "empty"}; {comp.id} still out={parts.IsRemoved(comp.id)}");

            // ---- 7. put the original back
            yield return Say("put it back");
            yield return null;
            int left = PlacedInGap(gap);
            bool restored = !parts.IsRemoved(comp.id) && left == 0;
            Check("e2e.restore", restored, $"{comp.id} in={!parts.IsRemoved(comp.id)} models left at its spot={left} via {s_Via} | {parts.LastAction}");

            done(s_PassN == s_Total, Summary(site));
        }

        static string Summary(string site)
        {
            string s = $"e2e {s_PassN}/{s_Total} passed on {site}";
            Log.Check("e2e.summary", s_PassN == s_Total && s_Total > 0, $"passed={s_PassN} total={s_Total} site={site}");
            return s;
        }

        /// One phrase through the voice path's text twin; waits for the reply (its actions and LocalIntents have run).
        static IEnumerator Say(string text, Action<AgentReply> got = null)
        {
            var agent = Services.Get<AgentClient>();
            int runs = LocalIntents.Runs;
            bool done = false;
            AgentReply reply = null;
            float t0 = Now;
            while (agent.Busy && Now - t0 < ReplyTimeout) yield return null;
            t0 = Now;
            agent.SendText(text, r => { reply = r; done = true; });
            while (!done && Now - t0 < ReplyTimeout) yield return null;
            yield return null;
            s_Via = LocalIntents.Runs > runs ? "headset" : "server";
            string acts = reply?.actions != null ? string.Join(",", reply.actions.Select(a => a?.name)) : "-";
            s_Log.Add($"> \"{text}\" → \"{AirTools.UI.Copy.Clip(reply?.reply ?? "", 90)}\" [{acts}] via {s_Via} ({(Now - t0).ToString("0.0", C)} s)" +
                      (!done ? " TIMED OUT" : reply == null ? $" no reply: {agent.LastError}" : "") +
                      (s_Via == "headset" ? $" | {LocalIntents.LastRun}" : ""));
            got?.Invoke(reply);   // autonomy
        }

        /// Until the gap holds a model other than `beforeId` (a load can take a minute: the server builds the mesh), or a
        /// load failed, or the timeout. Whoever swaps it (ModelCycler, place_part, the placement editor).
        static IEnumerator WaitModel(Gap gap, string beforeId)
        {
            float t0 = Now;
            yield return null;
            var editor = PlacementEditor.Current != null ? PlacementEditor.Current : Services.Get<PlacementEditor>();
            while (Now - t0 < ModelTimeout)
            {
                bool busy = ModelCycler.Loading || (editor != null && editor.SwapBusy);
                var m = Gaps.ModelIn(gap);
                if (m != null && m.Spec != null && m.Spec.id != beforeId && !busy) yield break;
                // A load that failed (ModelCycler's, or the placement editor's swap) ends the wait.
                if (!busy && Now - t0 > 5f && (ModelCycler.LastError != null || (editor != null && (editor.LastSwap ?? "").Contains("didn't load")))) yield break;
                yield return null;
            }
        }

        /// Who switched the model: the placement editor's last swap, else ModelCycler's last action.
        static string SwapNote()
        {
            var e = PlacementEditor.Current != null ? PlacementEditor.Current : Services.Get<PlacementEditor>();
            return e != null ? $"editor: {e.LastSwap}" : $"cycler: {ModelCycler.LastAction}";
        }

        static int IndexOf(List<PartSummary> cands, PartInstance p) =>
            p == null || p.Spec == null ? -1 : cands.FindIndex(c => c != null && c.id == p.Spec.id);

        static IEnumerator EnsureWorld()
        {
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            else if (AppState.Mode == AppMode.Tabletop) AppCommands.SetTabletop(false);
            var mode = Services.Get<ModeController>();
            float t0 = Now;
            while (Now - t0 < WorldTimeout && (AppState.Mode != AppMode.World || (mode != null && (mode.IsTransitioning || mode.VisualMode != AppMode.World))))
                yield return null;
            var streamer = Services.Get<SceneStreamer>();
            while (streamer != null && streamer.Loading && Now - t0 < SiteTimeout) yield return null;
            Physics.SyncTransforms();
        }

        /// Where place_part / the switcher put a model in this gap (SceneRoot space): the cavity insert.
        static Vector3? ExpectedAnchor(string componentId, SceneRoot root) =>
            AppCommands.TryPlaceTarget(new PlacePartArgs { ComponentId = componentId }, root, out var t, out _) ? t.Anchor : (Vector3?)null;

        /// A placed part's front-bottom-centre in its frame (SceneRoot) space.
        static Vector3 Anchor(PartInstance p)
        {
            var frame = p.transform.parent;
            var w = p.transform.TransformPoint(PlacePartMath.FrontBottomCentre(p.LocalBox));
            return frame != null ? frame.InverseTransformPoint(w) : w;
        }

        /// Placed parts standing at the gap's insert right now (one model at a time).
        static int PlacedInGap(Gap gap)
        {
            var root = Services.Get<SceneRoot>();
            var spot = root != null ? ExpectedAnchor(gap.Id, root) : null;
            if (!Services.TryGet<PartTool>(out var tool) || !spot.HasValue) return 0;
            return tool.PlacedParts.Count(p => p != null && p.Placed && p.gameObject.activeInHierarchy && Vector3.Distance(Anchor(p), spot.Value) < Gaps.InGapRadius);
        }

        static bool Near(float got, float want) => !float.IsNaN(got) && Mathf.Abs(got - want) <= 0.01f;
        static string F(float v) => float.IsNaN(v) ? "—" : v.ToString("0.000", C);
        static string Dim(PartDims d) => d == null ? "?" : $"{d.w.ToString("0", C)}×{d.h.ToString("0", C)}×{d.d.ToString("0", C)}";
    }
}
#endif
