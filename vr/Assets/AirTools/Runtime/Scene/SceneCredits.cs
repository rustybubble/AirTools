namespace AirTools.Scene
{
    /// Attribution a scene package's source footage needs on screen while it's loaded (pure). The Zabel scan is built
    /// from public drone footage under CC BY 3.0 (backend docs/research/p1-buildings-bench.md; the demo script shows the
    /// credit under every Zabel shot).
    public static class SceneCredits
    {
        public const string Zabel =
            "\"Haus Schiller – Zabelgymnasium Gera – Drohnenflug\" by zabelgymnasium, CC BY 3.0, via Wikimedia Commons";

        /// The credit for a loaded site, or null when it needs none.
        public static string For(string site)
        {
            if (string.IsNullOrEmpty(site)) return null;
            return site.Trim().ToLowerInvariant().StartsWith("zabel") ? Zabel : null;
        }
    }
}
