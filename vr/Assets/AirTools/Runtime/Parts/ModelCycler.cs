using System.Collections.Generic;
using AirTools.Core;
using AirTools.Scene;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// e2e: one model at a time in a removed component's gap, switched through the Find parts candidates at the same spot.
    /// - PlaceBest ("put it in there"): the first candidate that fits the gap (CavityFit.Best) at the cavity insert —
    ///   front-bottom-centre on insert.p, facing out.
    /// - Show(i) / Next(±1) ("option 2", "next one"): the candidate stands where the model in the gap stands now (a
    ///   nudge carries over). The new model loads first, then swaps in (no empty moment): the old one is deleted, the
    ///   new one placed with PartTool.PlaceAt (delete-undo: one undo step, EditHistory.Step: Undo puts the model before
    ///   back; the first model's Undo empties the gap), with its fit
    ///   on all three axes ("Fits the gap · ⅜″ spare"). Its Find parts card is highlighted.
    /// - Adopt: a server place_part into the same gap (AppCommands.PlacePart) replaces the model there too, so the
    ///   server can switch models with place_part.
    /// "The model in the gap" is looked up each time (Gaps.ModelIn: whichever placed part stands at the insert), so a swap
    /// by the placement editor (feat/placement-editor, PartTool.ReplacePlaced) or a hand is seen too.
    /// Merge note: the placement editor's AppCommands.NextPlacedModel / ShowPlacedModel replace Next / Show; PlaceBest and
    /// Adopt stay.
    public static class ModelCycler
    {
        public static string ComponentId { get; private set; }
        /// The candidates being switched through (a snapshot of Find parts when the cycle began).
        public static List<PartSummary> Candidates { get; private set; } = new List<PartSummary>();
        /// The candidate loading (−1: none).
        public static int Pending { get; private set; } = -1;
        public static bool Loading => Pending >= 0;
        /// Models put in a gap (by this or adopted from place_part), and their part ids in order.
        public static int Placements { get; private set; }
        public static readonly List<string> Shown = new List<string>();
        public static string LastAction { get; private set; } = "";
        /// The last model's error (a load that failed) — null when fine.
        public static string LastError { get; private set; }

        static int s_Token;

        /// The model standing in the gap being switched (null: none).
        public static PartInstance Current => Gaps.TryGet(out var g, ComponentId) ? Gaps.ModelIn(g) : null;

        /// A model stands in the gap right now.
        public static bool Active => Current != null;

        /// The candidate standing in the gap (−1: none, or a model that isn't one of the candidates).
        public static int Index
        {
            get
            {
                var c = Current;
                return c != null && c.Spec != null ? Candidates.FindIndex(x => x != null && x.id == c.Spec.id) : -1;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset()
        {
            ComponentId = null; Candidates = new List<PartSummary>(); Pending = -1;
            Placements = 0; Shown.Clear(); LastAction = ""; LastError = null; s_Token++;
        }

        static List<PartSummary> BrowserCandidates() =>
            Services.TryGet<PartsBrowser>(out var b) ? new List<PartSummary>(b.Candidates) : new List<PartSummary>();

        /// Start switching in `componentId`'s gap through `candidates` (null: Find parts' current list).
        public static bool Begin(string componentId, IList<PartSummary> candidates = null)
        {
            if (string.IsNullOrEmpty(componentId)) return false;
            ComponentId = componentId;
            Candidates = candidates != null ? new List<PartSummary>(candidates) : BrowserCandidates();
            return Candidates.Count > 0;
        }

        /// The best candidate (the first that fits the gap, else the one that overruns least) into the open gap.
        public static bool PlaceBest()
        {
            if (!Gaps.TryGet(out var gap)) { Say(Copy.NoGap, ColorRole.Warning, "no gap"); return false; }
            if (!Begin(gap.Id)) { Say($"Find a {gap.Noun} first", ColorRole.Warning, "no candidates"); return false; }
            var dims = Candidates.ConvertAll(c => c?.spec?.dims_mm ?? c?.dims_mm);
            return Show(CavityFit.Best(dims, Gaps.FitSizeM(gap) * 1000f));
        }

        /// Step through the candidates, wrapping; with nothing in the gap yet, the best one.
        public static bool Next(int delta)
        {
            if (!Gaps.TryGet(out var gap)) { Say(Copy.NoGap, ColorRole.Warning, "no gap"); return false; }
            if (gap.Id != ComponentId || Candidates.Count == 0 || (!Loading && Index < 0)) Begin(gap.Id);
            if (Candidates.Count == 0) { Say($"Find a {gap.Noun} first", ColorRole.Warning, "no candidates"); return false; }
            int from = Loading ? Pending : Index;
            if (from < 0) return PlaceBest();
            int n = Candidates.Count;
            if (n < 2) { Say("That's the only one · find more", ColorRole.Info, "only one candidate"); return false; }
            return Show(Wrap(from + (delta == 0 ? 1 : delta), n));
        }

        /// Pure: i into 0…n−1, wrapping both ways.
        public static int Wrap(int i, int n) => n <= 0 ? -1 : ((i % n) + n) % n;

        /// Candidate i into the gap (loads, then swaps with the model standing there).
        public static bool Show(int i)
        {
            if (!Gaps.TryGet(out var gap)) { Say(Copy.NoGap, ColorRole.Warning, "no gap"); return false; }
            if (gap.Id != ComponentId || Candidates.Count == 0) Begin(gap.Id);
            if (i < 0 || i >= Candidates.Count) { Say($"No option {i + 1} · there are {Candidates.Count}", ColorRole.Warning, $"no candidate {i}"); return false; }
            if (!Services.TryGet<PartLoader>(out var loader) || !Services.TryGet<PartTool>(out var tool) || !Services.TryGet<SceneRoot>(out var root) || root.Content == null)
            { LastAction = "needs a PartLoader, a PartTool and a loaded scene"; Log.Warn($"ModelCycler: {LastAction}"); return false; }

            // Where it goes: where the model in the gap stands now (moved by hand or the editor), else the cavity insert.
            Vector3 anchor; Quaternion rotation;
            var frame = tool.frame != null ? tool.frame : root.transform;
            var here = Gaps.ModelIn(gap);
            if (here != null && here.transform.parent == frame)
            {
                anchor = frame.InverseTransformPoint(here.transform.TransformPoint(PlacePartMath.FrontBottomCentre(here.LocalBox)));
                rotation = here.transform.localRotation;
            }
            else
            {
                if (!AppCommands.TryPlaceTarget(new PlacePartArgs { ComponentId = gap.Id }, root, out var target, out string why))
                { Say(why, ColorRole.Warning, why); return false; }
                anchor = target.Anchor;
                rotation = target.Rotation;
            }

            var s = Candidates[i];
            int token = ++s_Token;
            Pending = i;
            LastError = null;
            string name = Copy.Clip(Copy.Clean(string.IsNullOrEmpty(s.name) ? s.id : s.name), 28);
            string query = Services.TryGet<PartsBrowser>(out var browser) ? browser.LastQuery : null;
            string gapId = gap.Id;
            string site = SiteScope.Current;   // sitescope: the model is for this site's gap
            int count = Candidates.Count;
            LastAction = $"loading {s.id} ({i + 1} of {count})";
            Log.Info($"ModelCycler: {LastAction} into {gapId}");
            UiToast.Show(Copy.GapLoading(name, i, count), ColorRole.Info);
            loader.LoadForPlacement(s.id, s.model_url, s.name, part =>
            {
                if (token != s_Token)
                {
                    // Superseded (a newer "next one", an adopted place_part, a reset): drop it.
                    if (part != null) { part.gameObject.SetActive(false); Object.Destroy(part.gameObject); }
                    return;
                }
                if (!SiteScope.IsCurrent(site))
                {
                    // sitescope: another model was loaded meanwhile; a gap with the same id there isn't this one.
                    Pending = -1;
                    if (part != null) { part.gameObject.SetActive(false); Object.Destroy(part.gameObject); }
                    LastAction = $"{site} was left while {s.id} loaded";
                    return;
                }
                Pending = -1;
                if (part == null)
                {
                    LastError = loader.LastError ?? "load failed";
                    Say(Copy.Error(ErrorSurface.PartLoad, loader.LastError), ColorRole.Warning, $"{s.id} didn't load: {LastError}");
                    return;
                }
                if (!Gaps.TryGet(out var now, gapId))
                {
                    // The part went back meanwhile: nothing to fill.
                    part.gameObject.SetActive(false); Object.Destroy(part.gameObject);
                    LastAction = "the gap closed while it loaded";
                    return;
                }
                if (query != null && query != "your request") part.SearchQuery = query;
                var fit = CavityFit.Report(part.Spec.dims_mm, Gaps.FitSizeM(now) * 1000f);
                var position = PlacePartMath.OriginFor(anchor, rotation, part.LocalBox);
                var old = Gaps.ModelIn(now, except: part);
                using (AirTools.Tools.EditHistory.Step())   // delete-undo: one Undo puts the model before back
                {
                    if (old != null) tool.Delete(old);
                    tool.PlaceAt(part, position, rotation, fit);
                }
                Placements++;
                Shown.Add(part.Spec.id);
                Highlight(part.Spec.id);
                var shown = part.Fit;
                LastAction = $"showing {part.Spec.id} ({i + 1} of {count}) in {gapId}: {shown?.Headline ?? "no fit"}";
                Log.Info($"ModelCycler: {LastAction}");
                UiToast.Show(Copy.GapModel(name, i, count, Copy.FitLine(shown)),
                    shown == null ? ColorRole.Info : shown.Status == FitStatus.Red ? ColorRole.Danger : shown.Status == FitStatus.Amber ? ColorRole.Warning : ColorRole.Success);
            });
            return true;
        }

        /// A server place_part landed in `componentId`'s gap: it becomes the model there (the one before goes).
        public static void Adopt(PartInstance part, string componentId)
        {
            if (part == null || string.IsNullOrEmpty(componentId)) return;
            s_Token++;          // a switch still loading would land on top of it
            Pending = -1;
            if (componentId != ComponentId || Candidates.Count == 0) Begin(componentId);
            if (Gaps.TryGet(out var gap, componentId) && Services.TryGet<PartTool>(out var tool))
            {
                var old = Gaps.ModelIn(gap, except: part);
                if (old != null) tool.Delete(old);   // delete-undo: undoable (AppCommands.PlacePart makes it one step with the placement)
            }
            Placements++;
            if (part.Spec != null) { Shown.Add(part.Spec.id); Highlight(part.Spec.id); }
            LastAction = $"adopted {part.Spec?.id} in {componentId} (candidate {Index})";
            Log.Info($"ModelCycler: {LastAction}");
        }

        /// Stop switching; `removeModel`: take the model out of the gap too ("put the dishwasher back").
        public static void Clear(bool removeModel)
        {
            s_Token++;
            Pending = -1;
            var model = Current;
            if (removeModel && model != null && Services.TryGet<PartTool>(out var tool)) tool.Delete(model);   // delete-undo
            ComponentId = null;
            LastAction = removeModel ? "cleared (model removed)" : "cleared";
        }

        static void Highlight(string partId)
        {
            if (!Services.TryGet<PartsBrowser>(out var b) || b.cards == null) return;
            foreach (var card in b.cards)
                if (card != null) card.SetHighlight(card.Summary != null && card.Summary.id == partId);
        }

        static void Say(string toast, ColorRole tone, string action)
        {
            LastAction = action;
            if (!string.IsNullOrEmpty(toast)) UiToast.Show(toast, tone);
            Log.Info($"ModelCycler: {action}");
        }
    }
}
