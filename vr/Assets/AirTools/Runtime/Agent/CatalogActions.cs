using AirTools.Core;

namespace AirTools.Agent
{
    /// catalog: the Catalog by voice ("show me dishwashers", "open the catalog", "search the catalog for a 24 inch
    /// range"). Found by AgentActions through [AgentAction]; the same path as the ring's Catalog item (AppCommands).
    public static class CatalogActions
    {
        /// show_catalog {category?, query?} (also `q` / `text` / `name`): open the Catalog; a category ("dishwashers") shows
        /// that one; a query goes in the search field as if typed. No tape needed.
        [AgentAction("show_catalog")]
        static bool ShowCatalog(AgentAction a, string reply)
        {
            string category = a.Str("category") ?? a.Str("category_id");
            string query = a.Str("query") ?? a.Str("q") ?? a.Str("text") ?? a.Str("name");
            bool ok = AppCommands.ShowCatalog(category, query);
            Log.Info($"Catalog: show_catalog category={category ?? "-"} query={query ?? "-"} → {(ok ? "ok" : "refused")}");
            return ok;
        }

        /// hide_catalog: close it.
        [AgentAction("hide_catalog")]
        static bool HideCatalog(AgentAction a, string reply) => AppCommands.HideCatalog();
    }
}
