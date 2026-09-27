using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    public enum PlacementAction { ToggleAdjust, Done, Nudge, Rotate, Fine, Snap, Reset, PrevModel, NextModel, Save, Slot,
        SizeStep, FitToOpening, Finish /* assetgen: Size & finish */ }

    /// A button of the placement editor: the Adjust chip on the part's card, and the adjust panel's controls. Press() is
    /// the path a poke or a ray press takes. Mode, reset, models and slots go through AppCommands (voice and the agent
    /// use the same); the pads and the Step / Snap chips act on the editor directly (a step is the panel's own unit).
    public class PlacementButton : MonoBehaviour
    {
        public GlassButton button;
        public PlacementAction action;
        [Tooltip("Nudge: 0 right, 1 left, 2 up, 3 down, 4 out, 5 in. Rotate: 0 turn +, 1 turn −, 2 tilt +, 3 tilt −, 4 roll +, " +
                 "5 roll −. Slot: 0 = A … 3 = D. SizeStep: 0 W −, 1 W +, 2 H −, 3 H +, 4 D −, 5 D +. Finish: chip 0–2.")]
        public int index;

        void OnEnable() { if (button != null) button.Clicked += Press; }
        void OnDisable() { if (button != null) button.Clicked -= Press; }

        public void Press()
        {
            var e = PlacementEditor.Current;
            switch (action)
            {
                case PlacementAction.ToggleAdjust: AppCommands.EditPart(); break;   // edit6dof: the card's chip opens the Edit view
                case PlacementAction.Done: AppCommands.AdjustPlacement(false); break;
                case PlacementAction.Nudge: e?.NudgeStep(index); break;
                case PlacementAction.Rotate: e?.RotateStep(index); break;
                case PlacementAction.Fine: e?.SetFine(!(e.Fine)); break;
                case PlacementAction.Snap: e?.SetSnap(!(e.Snap)); break;
                case PlacementAction.Reset: AppCommands.ResetPlacement(); break;
                case PlacementAction.PrevModel: AppCommands.NextPlacedModel(-1); break;
                case PlacementAction.NextModel: AppCommands.NextPlacedModel(1); break;
                case PlacementAction.Save: AppCommands.SavePlacement(null); break;
                case PlacementAction.Slot:
                    if (index >= 0 && index < PlacementMath.Slots.Length) AppCommands.LoadPlacement(PlacementMath.Slots[index]);
                    break;
                // assetgen: Size & finish
                case PlacementAction.SizeStep: e?.StepSize(index / 2, index % 2 == 0 ? -1 : 1); break;
                case PlacementAction.FitToOpening: AppCommands.FitPlacementToOpening(); break;
                case PlacementAction.Finish:
                    if (e != null && e.Target() is PartInstance p && index >= 0 && index < e.FinishChoices(p).Count)
                    {
                        string name = e.FinishChoices(p)[index];
                        AppCommands.SetPlacementFinish(PlacementEditor.FinishSelected(p, name) && p.LookFinish != null ? null : name);
                    }
                    break;
            }
            if (e == null && action != PlacementAction.ToggleAdjust && action != PlacementAction.Done) Log.Warn("PlacementButton: no placement editor in the scene");
        }
    }
}
