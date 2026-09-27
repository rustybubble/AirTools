using System.Globalization;
using AirTools.Core;
using AirTools.Parts;
using Newtonsoft.Json.Linq;

namespace AirTools.Agent
{
    /// The placement editor by voice / agent (found by AgentActions through [AgentAction]); every handler calls the same
    /// AppCommands the adjust panel does. Tolerant: numbers may come as numbers or strings, every field is optional.
    ///
    /// adjust_placement {mode?, dx_mm?, dy_mm?, dz_mm?, yaw_deg?, pitch_deg?, roll_deg?, reset?, snap?, fine?, save?, slot?}
    ///   mode: "on" | "off" | "done" | "toggle" (none: only the moves below). dx right (the viewer's, facing the
    ///   placement), dy up, dz out toward you, millimetres, in the part's frame (the cavity's, the surface's, or up and
    ///   your facing). yaw_deg turns clockwise seen from above, pitch_deg tips the top away from you, roll_deg turns
    ///   clockwise as you face it; about the part's anchor. reset: back to the fit pose. snap / fine: the panel's toggles.
    ///   save: true or a slot name ("B") saves after the moves. Moves outside adjust mode are one undo step each.
    ///   "move it left an inch" → {dx_mm: -25.4}; "rotate it 90 degrees" → {yaw_deg: 90}; "save this placement" →
    ///   save_placement {} (or adjust_placement {save: true}).
    /// save_placement {slot?}: saves in slot A–D (none: the next free). load_placement {slot}: goes to a saved placement.
    /// cycle_model {delta? = 1, index?}: the next / previous candidate's model (or candidate `index`) at the same
    ///   placement, anchor on anchor. True when it swapped or the model is loading.
    /// edit6dof: "edit it", "adjust the dishwasher" open the Edit view (EditView): adjust_placement {mode: on | open |
    ///   adjust | edit | toggle} and a bare adjust_placement, and edit_part {part?}; delete_part {part?} (undoable) and
    ///   view_similar {part?} (the Catalog on its kind). `part` (any of them): a placed part's id, name or kind
    ///   ("dishwasher"); none: the selected one, else the last placed.
    public static class PlacementActions
    {
        [AgentAction("adjust_placement")]
        static bool Adjust(AgentAction a, string reply)
        {
            var args = a.args ?? new JObject();
            bool ok = true, any = false;
            string mode = Str(args, "mode", "state");
            if (mode != null)
            {
                any = true;
                switch (mode.Trim().ToLowerInvariant())
                {
                    // edit6dof: on / open → the Edit view (the adjust panel without one in the scene)
                    case "on": case "start": case "open": case "adjust": case "edit": ok &= AppCommands.EditPart(Target(args)); break;
                    case "off": case "done": case "close": case "stop": case "exit": ok &= AppCommands.SaveEdit() || AppCommands.AdjustPlacement(false); break;
                    case "toggle": ok &= EditViewOpen() ? AppCommands.SaveEdit() : AppCommands.EditPart(Target(args)); break;
                }
            }
            var e = PlacementEditor.Current;
            if (Bool(args, "fine") is bool fine && e != null) { e.SetFine(fine); any = true; }
            if (Bool(args, "snap") is bool snap && e != null) { e.SetSnap(snap); any = true; }
            if (Bool(args, "reset") == true) { ok &= AppCommands.ResetPlacement(); any = true; }
            float dx = Num(args, "dx_mm", "right_mm", "x_mm"), dy = Num(args, "dy_mm", "up_mm", "y_mm"), dz = Num(args, "dz_mm", "out_mm", "z_mm");
            float yaw = Num(args, "yaw_deg", "turn_deg", "yaw"), pitch = Num(args, "pitch_deg", "tilt_deg", "pitch"), roll = Num(args, "roll_deg", "roll");
            if (dx != 0f || dy != 0f || dz != 0f || yaw != 0f || pitch != 0f || roll != 0f)
            {
                ok &= AppCommands.NudgePlacement(dx, dy, dz, yaw, pitch, roll);
                any = true;
            }
            var save = args["save"];
            if (save != null && save.Type != JTokenType.Null && !(save.Type == JTokenType.Boolean && !(bool)save))
            {
                string slot = save.Type == JTokenType.String ? (string)save : Str(args, "slot");
                ok &= AppCommands.SavePlacement(slot);
                any = true;
            }
            else if (!any && Str(args, "slot") is string load) { ok &= AppCommands.LoadPlacement(load); any = true; }
            if (!any) ok = EditViewOpen() ? AppCommands.SaveEdit() : AppCommands.EditPart(Target(args));   // a bare "adjust it" (edit6dof: the Edit view)
            Log.Info($"Placement: adjust_placement {args.ToString(Newtonsoft.Json.Formatting.None)} → {(ok ? "done" : "refused")} | {e?.LastAction}");
            return ok;
        }

        // edit6dof ------------------------------------------------------------------------------------------------------

        [AgentAction("edit_part")]
        static bool EditPart(AgentAction a, string reply)
        {
            bool ok = AppCommands.EditPart(Target(a.args));
            Log.Info($"Placement: edit_part → {(ok ? "open" : "refused")} | {EditView.Current?.LastAction}");
            return ok;
        }

        [AgentAction("delete_part")]
        static bool DeletePart(AgentAction a, string reply)
        {
            bool ok = AppCommands.DeletePart(Target(a.args));
            Log.Info($"Placement: delete_part → {(ok ? "deleted" : "refused")}");
            return ok;
        }

        [AgentAction("view_similar")]
        static bool ViewSimilar(AgentAction a, string reply)
        {
            bool ok = AppCommands.ViewSimilar(Target(a.args));
            Log.Info($"Placement: view_similar → {(ok ? "catalog" : "refused")} | {EditView.Current?.LastAction}");
            return ok;
        }

        static bool EditViewOpen() => EditView.Current != null && EditView.Current.Active;

        /// The placed part `part` names (its id, name or kind, as said), else null (the commands' default).
        public static PartInstance Target(JObject args)
        {
            string q = Str(args, "part", "part_id", "target", "name", "component");
            if (q == null || !AirTools.Core.Services.TryGet<PartTool>(out var tool)) return null;
            q = q.Trim().ToLowerInvariant();
            if (q.StartsWith("the ")) q = q.Substring(4);
            PartInstance best = null;
            foreach (var p in tool.PlacedParts)
            {
                if (p == null || p.Spec == null) continue;
                string id = (p.Spec.id ?? "").ToLowerInvariant(), name = (p.Spec.name ?? "").ToLowerInvariant();
                string noun = AirTools.UI.Copy.Noun(p.Spec, p.SearchQuery).ToLowerInvariant();
                if (id == q) return p;
                if (name.Contains(q) || noun == q || noun.TrimEnd('s') == q.TrimEnd('s') || (q.Length > 3 && q.Contains(noun))) best = p;   // the newest match
            }
            return best;
        }
        // end edit6dof --------------------------------------------------------------------------------------------------

        [AgentAction("save_placement")]
        static bool Save(AgentAction a, string reply)
        {
            bool ok = AppCommands.SavePlacement(Str(a.args, "slot", "name"));
            Log.Info($"Placement: save_placement → {(ok ? "saved" : "refused")} | {PlacementEditor.Current?.LastAction}");
            return ok;
        }

        [AgentAction("load_placement")]
        static bool Load(AgentAction a, string reply)
        {
            bool ok = AppCommands.LoadPlacement(Str(a.args, "slot", "name"));
            Log.Info($"Placement: load_placement → {(ok ? "moved" : "refused")} | {PlacementEditor.Current?.LastAction}");
            return ok;
        }

        [AgentAction("cycle_model")]
        static bool Cycle(AgentAction a, string reply)
        {
            var args = a.args ?? new JObject();
            bool ok;
            if (args["index"] != null && args["index"].Type != JTokenType.Null) ok = AppCommands.ShowPlacedModel((int)Num(args, "index"));
            else
            {
                float d = Num(args, "delta", "step");
                ok = AppCommands.NextPlacedModel(d < 0f ? -1 : d > 0f ? (int)d : 1);
            }
            Log.Info($"Placement: cycle_model {args.ToString(Newtonsoft.Json.Formatting.None)} → {(ok ? "ok" : "refused")} | {PlacementEditor.Current?.LastSwap}");
            return ok;
        }

        static string Str(JObject a, params string[] keys)
        {
            if (a == null) return null;
            foreach (var k in keys)
            {
                var t = a[k];
                if (t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Object || t.Type == JTokenType.Array) continue;
                var s = ((string)t)?.Trim();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return null;
        }

        /// The first of `keys` that is a number (or a numeric string); 0 when none.
        public static float Num(JObject a, params string[] keys)
        {
            if (a == null) return 0f;
            foreach (var k in keys)
            {
                var t = a[k];
                if (t == null) continue;
                if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (float)t;
                if (t.Type == JTokenType.String && float.TryParse(((string)t).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return f;
            }
            return 0f;
        }

        static bool? Bool(JObject a, string key)
        {
            var t = a?[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            if (t.Type == JTokenType.String)
            {
                var s = ((string)t).Trim().ToLowerInvariant();
                if (s == "true" || s == "on" || s == "yes") return true;
                if (s == "false" || s == "off" || s == "no") return false;
            }
            if (t.Type == JTokenType.Integer) return (int)t != 0;
            return null;
        }
    }
}
