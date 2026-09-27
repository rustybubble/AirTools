namespace AirTools.Parts
{
    /// gaze-catalog: what the wearer is looking at, as the catalog understands it (GET /catalog?focus=…). None: nothing
    /// in particular (the sky, a shelf) — the site's whole catalog.
    public enum CatalogFocus { None, Roof, Wall, Ground, Ceiling, Counter, Opening }

    /// gaze-catalog: the focus's words (pure): its wire name, what the header chip calls it, parsing the server's answer.
    public static class CatalogFoci
    {
        public const int Count = 7;

        /// The query value ("roof"); null for None.
        public static string Wire(CatalogFocus f) => f switch
        {
            CatalogFocus.Roof => "roof",
            CatalogFocus.Wall => "wall",
            CatalogFocus.Ground => "ground",
            CatalogFocus.Ceiling => "ceiling",
            CatalogFocus.Counter => "counter",
            CatalogFocus.Opening => "opening",
            _ => null,
        };

        /// The server's `focus` ("roof", "floor", null) → the focus (None for anything else).
        public static CatalogFocus Parse(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "roof": case "roofs": case "rooftop": return CatalogFocus.Roof;
                case "wall": case "walls": case "facade": return CatalogFocus.Wall;
                case "ground": case "floor": return CatalogFocus.Ground;
                case "ceiling": return CatalogFocus.Ceiling;
                case "counter": case "countertop": case "backsplash": return CatalogFocus.Counter;
                case "opening": case "window": case "door": return CatalogFocus.Opening;
                default: return CatalogFocus.None;
            }
        }

        /// A room (a floor and a counter) rather than outside a building (a roof and the ground): the laptop's environment
        /// (kitchen, bathroom, laundry, garage, interior, gym, hospital: rooms; rooftop, facade, pavilion: outside), else
        /// the site's name (a kitchen).
        public static bool IsIndoor(string environment, string site)
        {
            switch ((environment ?? "").Trim().ToLowerInvariant())
            {
                case "kitchen": case "bathroom": case "laundry": case "garage": case "interior": case "gym": case "hospital":
                    return true;
                case "rooftop": case "facade": case "pavilion":
                    return false;
            }
            return CatalogFallback.KindOf(site) == SiteKind.Kitchen;
        }

        /// "the roof", "the floor" (ground indoors), "an opening"… (null for None).
        public static string Name(CatalogFocus f, bool indoor) => f switch
        {
            CatalogFocus.Roof => "the roof",
            CatalogFocus.Wall => "the wall",
            CatalogFocus.Ground => indoor ? "the floor" : "the ground",
            CatalogFocus.Ceiling => "the ceiling",
            CatalogFocus.Counter => "the counter",
            CatalogFocus.Opening => "an opening",
            _ => null,
        };
    }
}
