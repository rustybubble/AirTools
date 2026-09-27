using System;
using AirTools.Core;

namespace AirTools.UI
{
    /// UX decision D2 (SPEC §9): ONE unit on every on-screen label, imperial first for the HackGT build, one tap away
    /// from metric (the units chip in the Scene window). Set / Toggle save the choice (UiSettings.UnitSystem, per
    /// device) and re-label what's already drawn — tapes and shapes, survey outlines, part callouts and fits, ladders,
    /// fall edges and the open windows — without re-measuring anything: ValueSI, the raw notebook Labels, export,
    /// requests and the logs stay metric.
    public static class UnitsSwitch
    {
        /// After a switch has re-labelled everything (the chip, tests).
        public static event Action<UnitSystem> Switched;

        public static UnitSystem Current => UiSettings.UnitSystem;

        /// "ft·in" / "m": what the chip and the log call a unit.
        public static string Short(UnitSystem u) => u == UnitSystem.Metric ? "m" : "ft·in";

        /// Switch to `u` (saved on this headset) and re-label. False when it was already `u` (nothing to do).
        public static bool Set(UnitSystem u)
        {
            if (UiSettings.UnitSystem == u) return false;
            UiSettings.UnitSystem = u;
            Apply(u);
            return true;
        }

        /// The chip: imperial ↔ metric.
        public static UnitSystem Toggle()
        {
            var next = Units.Other(UiSettings.UnitSystem);
            Set(next);
            return next;
        }

        /// Use `u` for this session only (the device's saved choice is untouched): tests, a demo reset.
        public static void Use(UnitSystem u)
        {
            if (UiSettings.UnitSystem == u) return;
            UiSettings.UseUnits(u);
            Apply(u);
        }

        static void Apply(UnitSystem u)
        {
            Relabel();
            Log.Info($"Units: {Short(u)} ({u})");
            Switched?.Invoke(u);
        }

        /// Re-word everything drawn in the current unit (no re-measure; safe with nothing in the scene).
        public static void Relabel()
        {
            if (Live<AirTools.Tools.MeasureTool>(out var measure)) measure.Relabel();
            if (Live<AirTools.Parts.PartTool>(out var parts)) parts.Relabel();
            if (Live<AirTools.Tools.LadderTool>(out var ladders)) ladders.Relabel();
            if (Live<AirTools.Structure.FallEdges>(out var edges)) edges.Relabel();
            if (Live<AirTools.Scene.SceneParts>(out var sceneParts)) sceneParts.Relabel();
            if (Live<AirTools.Tools.LadderCard>(out var ladderCard) && ladderCard.IsOpen) ladderCard.Refresh();
            if (Live<AirTools.Agent.SurveyCard>(out var survey)) survey.Relabel();
            if (Live<AirTools.Parts.SpecCard>(out var spec)) spec.Refresh();
            if (Live<AirTools.Parts.PartsBrowser>(out var browser))
                foreach (var card in browser.cards) if (card != null) card.Relabel();
            if (Live<AirTools.Parts.CatalogWindow>(out var catalog)) catalog.Relabel();   // catalog
            if (Live<AirTools.Parts.CheckoutPanel>(out var checkout) && checkout.checks != null) checkout.checks.text = checkout.ChecksText();
            if (Live<AirTools.Structure.ScenePanel>(out var scene) && scene.IsOpen) scene.Refresh();
            if (Live<AirTools.Notes.NotebookPanel>(out var notebook) && notebook.IsOpen) notebook.Refresh();
            if (Live<GuideRail>(out var rail)) rail.Invalidate();
        }

        /// A registered service that hasn't been destroyed (Unity's null check).
        static bool Live<T>(out T service) where T : UnityEngine.Object
        {
            service = Services.Get<T>();
            return service != null;
        }
    }
}
