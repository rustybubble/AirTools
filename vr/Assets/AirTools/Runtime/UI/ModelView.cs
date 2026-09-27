using System;
using AirTools.Core;

namespace AirTools.UI
{
    /// Model view shows the model only (modelview; the user's call, Sat 09-26): while the scene is (going) on the table,
    /// every world annotation owner is hidden — its text and the lines, dots and boxes that go with it — and on leaving it
    /// comes back exactly as it was. The wrist strip, the status line, windows, the ring, the "You are here" pin, the
    /// truth bar, the switcher (modelwheel: the wheel under the model) and placed parts' geometry stay. This is the gate (pure); ModelViewDeclutter applies it
    /// centrally (Renderer.forceRenderingOff, separate from the owners' own enabled / active state), so the owners only
    /// say where their annotations live. `Owners` is the census ModelViewTests checks against the code.
    public static class ModelView
    {
        /// World annotations are hidden now.
        public static bool HidesAnnotations { get; private set; }
        /// Raised when that changes (true = hidden).
        public static event Action<bool> Changed;
        public static int Changes { get; private set; }

        /// Annotations hide from the moment the scene heads for the table until it has grown back to full size.
        public static bool Hides(AppMode mode, bool onTable) => mode == AppMode.Tabletop || onTable;

        /// Hide (true) or bring back; raises Changed only on a change. Returns whether it changed.
        public static bool Set(bool hide)
        {
            if (hide == HidesAnnotations) return false;
            HidesAnnotations = hide;
            Changes++;
            Changed?.Invoke(hide);
            return true;
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset() { HidesAnnotations = false; Changed = null; Changes = 0; }

        /// Every type that puts text (or a label's lines and dots) into the scene, and that Model view hides: the tapes and
        /// areas (MeasureTool / MeasureView, the finish chip, the B1 survey labels and SurveyRunner's progress), level
        /// readouts (LevelTool / LevelGizmo), ladders (LadderTool / LadderView), ask pins (SceneAsk), Grok overlays and
        /// their chips (GrokOverlays / WorldChip), the coach's boxes and "Don't drill here" card (CoachOverlay) and its
        /// crosshair (DrillCrosshair), fall edges (FallEdges), part callouts and outlines (PartOutline; the parts stay),
        /// cavity labels (CavityView; the cavity mesh is scene), placement guides (PlacementGuides) and the bought parts'
        /// labels (TakeItHome; the parts stay).
        public static readonly string[] Owners =
        {
            "MeasureTool", "MeasureView", "SurveyRunner", "LevelTool", "LevelGizmo", "LadderTool", "LadderView", "SceneAsk",
            "GrokOverlays", "WorldChip", "CoachOverlay", "DrillCrosshair", "FallEdges", "PartOutline", "CavityView",
            "PlacementGuides", "TakeItHome",
        };

        /// Text in the world that stays in Model view, and why.
        public static readonly (string owner, string why)[] Stays =
        {
            ("SpawnMarker", "the \"You are here\" pin is the step-in control"),
            ("TruthBar", "the real table's 1 m ruler, not part of the model"),
            ("ModelSwitcher", "the model cards are Model view's own controls"),
            ("ModelWheel", "the model wheel (modelwheel) is Model view's own control"),
        };
    }
}
