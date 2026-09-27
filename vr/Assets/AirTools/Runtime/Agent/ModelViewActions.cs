using AirTools.Core;

namespace AirTools.Agent
{
    /// Model view by voice (modelview): "show me the gym model", "next model". Found by AgentActions through
    /// [AgentAction]; they call AppCommands.ShowModel, the same path as the switcher's cards. The backend has to send the
    /// action; until it does, these are exercised by the harness (AgentHarness.ShowModel) and the EditMode tests.
    public static class ModelViewActions
    {
        /// show_model {site} (also `model` / `name` / `query` / `scene`): the site id ("zabel-gymnasium") or a spoken name
        /// ("the gym", "GT tower", "facade"); "next" / "previous" step through the switcher; no argument = Model view.
        [AgentAction("show_model")]
        static bool ShowModel(AgentAction a, string reply)
        {
            string site = a.Str("site") ?? a.Str("model") ?? a.Str("name") ?? a.Str("query") ?? a.Str("scene");
            bool ok = AppCommands.ShowModel(site);
            Log.Info($"Model view: show_model {site ?? "(none)"} → {(ok ? "ok" : "refused")}");
            return ok;
        }

        /// next_model {direction?: "next" | "previous"}: the next (or previous) model on the table.
        [AgentAction("next_model")]
        static bool NextModel(AgentAction a, string reply)
        {
            string dir = (a.Str("direction") ?? "next").Trim().ToLowerInvariant();
            bool ok = AppCommands.ShowModel(dir.StartsWith("prev") || dir == "back" ? "previous" : "next");
            Log.Info($"Model view: next_model {dir} → {(ok ? "ok" : "refused")}");
            return ok;
        }
    }
}
