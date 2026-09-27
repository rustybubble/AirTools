using AirTools.Core;
using Newtonsoft.Json.Linq;

namespace AirTools.Agent
{
    /// e2e ("take out the dishwasher, measure the gap, find one that fits, put it in, next one"): the action the replace
    /// flow adds to remove_component / place_part (ScenePartsActions) and cycle_model (the placement editor's
    /// PlacementActions). Sent by the e2e backend patch (docs/handoff/p4-e2e); the app's own fallback (LocalIntents) calls
    /// the same AppCommands.
    public static class ReplaceFlowActions
    {
        /// measure_cavity {component_id?}: the real tape across the gap — height, depth, width — saved to the notebook.
        /// No id: the part taken out most recently.
        [AgentAction("measure_cavity")]
        static bool MeasureCavity(AgentAction a, string reply)
        {
            string id = ScenePartsActions.ComponentId(a.args);
            bool ok = AppCommands.MeasureCavity(id);
            Log.Info($"Replace flow: measure_cavity {id ?? "(last removed)"} → {(ok ? "taped" : "refused")}");
            return ok;
        }

        /// undo_edit {}: "undo" while the replace flow is live (autonomy; backend sends it with a gap open): the app's own
        /// Undo (EditHistory) — the last edit comes off (the model put in the gap, a swap, the scale, the tapes).
        [AgentAction("undo_edit")]
        static bool UndoEdit(AgentAction a, string reply)
        {
            bool ok = AppCommands.Undo();
            Log.Info($"Replace flow: undo_edit → {(ok ? "undone" : "nothing to undo")}");
            return ok;
        }

        /// scale_gap {axis: w|h|d, real_m}: "the opening is 34½ inches tall" — set the scene's scale from the gap's tape on
        /// that axis (tapes the gap first if it hasn't been), like the Scene window's Set scale.
        [AgentAction("scale_gap")]
        static bool ScaleGap(AgentAction a, string reply)
        {
            string axis = a.Str("axis");
            float? real = a.Float("real_m");
            bool ok = real.HasValue && AppCommands.ScaleFromGap(axis, real.Value);
            Log.Info($"Replace flow: scale_gap {axis} = {real} m → {(ok ? "scaled" : "refused")}");
            return ok;
        }
    }
}
