using AirTools.Core;
using Newtonsoft.Json.Linq;

namespace AirTools.Agent
{
    /// "Measure and replace" by voice (a draft on the backend, not merged yet: kept tolerant). Found by AgentActions
    /// through [AgentAction]; they call the same AppCommands as the Settings chips. Progress around them uses the existing
    /// job_started / job_step / job_done rail (the status line's job strip), which runs in the same actions[] in order.
    public static class ScenePartsActions
    {
        /// remove_component {component_id}: hide part_<id> (mesh and collider), show cavity_<id> with its
        /// "W × H × D · estimated" label; one undoable edit. Also takes `id` / `component` / `label` / `name`, and a label
        /// ("dishwasher") for the id.
        [AgentAction("remove_component")]
        static bool RemoveComponent(AgentAction a, string reply)
        {
            string id = ComponentId(a.args);
            bool ok = AppCommands.RemoveComponent(id);
            Log.Info($"Scene parts: remove_component {id ?? "(none)"} → {(ok ? "out" : "refused")}");
            return ok;
        }

        /// restore_component {component_id}: put it back (the undo of remove_component, by voice). Not in the draft:
        /// harmless when the backend never sends it.
        [AgentAction("restore_component")]
        static bool RestoreComponent(AgentAction a, string reply)
        {
            string id = ComponentId(a.args);
            bool ok = AppCommands.RestoreComponent(id);
            Log.Info($"Scene parts: restore_component {id ?? "(none)"} → {(ok ? "back" : "refused")}");
            return ok;
        }

        /// place_part {part_id, model_url, pose, fits, clearance_mm}: load the part and set its pose (scene / structure
        /// frame, glTF with the X flip, gravity-aligned); no pose → the cavity insert of the part taken out last. Shows
        /// `fits` and the clearance with the fit colours. True when the load started.
        [AgentAction("place_part")]
        static bool PlacePart(AgentAction a, string reply)
        {
            var args = AirTools.Parts.PlacePartArgs.Parse(a.args);
            bool ok = AppCommands.PlacePart(args);
            Log.Info($"Scene parts: {args} → {(ok ? "loading" : "refused")}");
            return ok;
        }

        /// The component named by an action's args: component_id, else id / component / component_label / label / name.
        public static string ComponentId(JObject args)
        {
            if (args == null) return null;
            foreach (var key in new[] { "component_id", "id", "component", "component_label", "label", "name" })
            {
                var t = args[key];
                if (t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Array) continue;
                if (t.Type == JTokenType.Object)
                {
                    var inner = ComponentId((JObject)t);
                    if (!string.IsNullOrWhiteSpace(inner)) return inner;
                    continue;
                }
                var s = ((string)t)?.Trim();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return null;
        }
    }
}
