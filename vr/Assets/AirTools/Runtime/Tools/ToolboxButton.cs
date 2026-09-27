using AirTools.Core;
using AirTools.Input;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Tools
{
    public enum ToolboxAction { ToggleMeasure, Finish, Undo, Clear, ToggleLevel, ToggleNotebook, ToggleWorld, ToggleContrast, ToggleMotion, ToggleMove, Home, ToggleTabletop, ToggleScenePanel, Redo, StepIn, ToggleLadder, ToggleFallEdges,
        ToggleCatalog }   // catalog: appended (scene values of the others stay)

    /// A tool button on the palm menu. Toggles show their state as the button's selected state (ink fill +
    /// indicator bar + semibold label).
    public class ToolboxButton : MonoBehaviour
    {
        public GlassButton button;
        public ToolboxAction action;
        [Tooltip("If set, presses only count once this menu has been open a moment.")]
        public PalmMenu menu;

        void OnEnable() { if (button != null) button.Clicked += Press; }
        void OnDisable() { if (button != null) button.Clicked -= Press; }

        public void Press()
        {
            if (menu != null && !menu.AcceptingPresses) return;
            Run(action);
        }

        /// Do a toolbox action (buttons and the tool ring share this). Undo / Redo go through EditHistory, so they
        /// reach whichever tool made (or undid) the latest edit, in any mode.
        public static void Run(ToolboxAction action)
        {
            var tools = Services.Get<ToolManager>();
            var hub = Services.Get<ToolInputHub>();
            switch (action)
            {
                case ToolboxAction.ToggleMeasure: tools?.Toggle(ToolKind.Measure); break;
                case ToolboxAction.ToggleLevel: tools?.Toggle(ToolKind.Level); break;
                case ToolboxAction.ToggleNotebook: if (Services.TryGet<AirTools.Notes.NotebookPanel>(out var nb)) nb.Toggle(); break;
                case ToolboxAction.Finish: hub?.RaiseButton(hub.LastActiveHand, ToolButton.Finish); break;
                case ToolboxAction.Undo: EditHistory.Undo(); break;
                case ToolboxAction.Redo: EditHistory.Redo(); break;
                case ToolboxAction.Clear: hub?.RaiseButton(hub.LastActiveHand, ToolButton.Clear); break;
                case ToolboxAction.ToggleWorld: AppCommands.ToggleChest(); break;
                case ToolboxAction.ToggleContrast: UiSettings.HighContrast = !UiSettings.HighContrast; break;
                case ToolboxAction.ToggleMotion: UiSettings.ReducedMotion = !UiSettings.ReducedMotion; break;
                case ToolboxAction.ToggleMove: tools?.Toggle(ToolKind.Move); break;
                case ToolboxAction.Home:
                    // modelview: in Model view, Home walks into the model on the table at its recommended spawn.
                    if (AppState.Mode == AppMode.Tabletop && Services.TryGet<AirTools.Scene.TabletopController>(out var table) && table.WalkInAtSpawn()) break;
                    if (Services.TryGet<Locomotion>(out var loco)) loco.GoHome();
                    break;
                case ToolboxAction.ToggleTabletop: AppCommands.ToggleTabletop(); break;
                case ToolboxAction.ToggleScenePanel: if (Services.TryGet<AirTools.Structure.ScenePanel>(out var sp)) sp.Toggle(); break;
                case ToolboxAction.StepIn: AppCommands.StepIn(); break;
                case ToolboxAction.ToggleLadder: tools?.Toggle(ToolKind.Ladder); break;   // P6/P7
                case ToolboxAction.ToggleFallEdges: AppCommands.ShowFallEdges(!(Services.Get<AirTools.Structure.FallEdges>()?.Visible ?? false)); break;   // P6/P7
                case ToolboxAction.ToggleCatalog: AppCommands.ToggleCatalog(); break;   // catalog
            }
            Log.Info($"Menu: {action}");
        }

        /// Is this toggle on right now (null for plain actions)?
        public bool? IsOn() => IsOn(action);

        public static bool? IsOn(ToolboxAction action)
        {
            var tools = Services.Get<ToolManager>();
            return action switch
            {
                ToolboxAction.ToggleMeasure => tools != null && tools.Active == ToolKind.Measure,
                ToolboxAction.ToggleLevel => tools != null && tools.Active == ToolKind.Level,
                ToolboxAction.ToggleMove => tools != null && tools.Active == ToolKind.Move,
                ToolboxAction.ToggleTabletop => AppState.Mode == AppMode.Tabletop,
                ToolboxAction.ToggleNotebook => Services.TryGet<AirTools.Notes.NotebookPanel>(out var nb) && nb.IsOpen,
                ToolboxAction.ToggleContrast => UiSettings.HighContrast,
                ToolboxAction.ToggleMotion => UiSettings.ReducedMotion,
                ToolboxAction.ToggleScenePanel => Services.TryGet<AirTools.Structure.ScenePanel>(out var sp) && sp.IsOpen,
                ToolboxAction.ToggleLadder => tools != null && tools.Active == ToolKind.Ladder,   // P6/P7
                ToolboxAction.ToggleFallEdges => Services.TryGet<AirTools.Structure.FallEdges>(out var fe) && fe.Visible,   // P6/P7
                ToolboxAction.ToggleCatalog => Services.TryGet<AirTools.Parts.CatalogWindow>(out var cw) && cw.Showing,   // catalog
                _ => (bool?)null,
            };
        }

        void Update()
        {
            if (button == null) return;
            if (action == ToolboxAction.ToggleWorld)
            {
                string t = AppState.Mode == AppMode.Passthrough ? "Enter world" : "Exit world";
                if (button.Text != t) button.SetText(t);
                return;
            }
            var on = IsOn();
            if (on.HasValue) button.SetSelected(on.Value);
        }
    }
}
