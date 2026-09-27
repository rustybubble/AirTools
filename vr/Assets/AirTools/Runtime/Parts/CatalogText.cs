using System.Globalization;
using System.Text.RegularExpressions;
using AirTools.Core;

namespace AirTools.Parts
{
    /// catalog: the catalog's words (pure): card lines, the status line, a spoken request as a search, the cache file.
    public static class CatalogText
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        /// "$459" (whole dollars from $100), "$4.27", or "" with no price.
        public static string Price(float? usd)
        {
            if (!usd.HasValue || !(usd.Value > 0f)) return "";
            return usd.Value >= 100f ? "$" + System.Math.Round(usd.Value).ToString("0", C) : "$" + usd.Value.ToString("0.00", C);
        }

        /// The width a card shows: "23⅞″ W" (imperial, inches) or "606 mm W".
        public static string Width(PartDims d, UnitSystem units)
        {
            if (d == null || !(d.w > 0f)) return "";
            return units == UnitSystem.Metric ? d.w.ToString("0", C) + " mm W" : Units.InchesOnly(d.w / 1000.0) + "″ W";
        }

        /// A card's second line: "$459 · 23⅞″ W" (either part may be missing).
        public static string Detail(PartSummary s, UnitSystem units)
        {
            if (s == null) return "";
            string p = Price(s.price_usd), w = Width(s.dims_mm, units);
            return p.Length > 0 && w.Length > 0 ? p + " · " + w : p + w;
        }

        /// A card's name, without the size the second line already gives ("24 in. Top Control …" → "Top Control …").
        public static string Name(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var n = Regex.Replace(name.Trim(), @"^\d+(?:[.,]\d+)?\s*(?:in\.?|inch(?:es)?|""|″|cm|mm)\s+(?:W\b[.,]?\s*|wide\s+)?", "", RegexOptions.IgnoreCase);
            return n.Length >= 4 ? n : name.Trim();
        }

        /// "dishwashers" / "1 dishwasher" style count: "12 items", "1 item".
        public static string Count(int n) => n == 1 ? "1 item" : $"{n} items";

        /// Where the catalog came from, for the status line.
        public static string SourceNote(CatalogSource source) => source switch
        {
            CatalogSource.Cache => "saved on the headset",
            CatalogSource.BuiltIn => "built-in · laptop not connected",
            _ => "",
        };

        /// A spoken request as a catalog search: "find me a dishwasher that fits" → "dishwasher", "show me fridges" →
        /// "fridges", "I need a 24 inch range" → "24 inch range". Words the field can't hold are dropped.
        public static string FromUtterance(string said)
        {
            if (string.IsNullOrWhiteSpace(said)) return "";
            var t = said.Trim().ToLowerInvariant();
            t = Regex.Replace(t, @"[.!?]+$", "");
            t = Regex.Replace(t, @"^(?:(?:hey|ok|okay|so|um|uh|please|can you|could you|would you)[\s,]+)+", "");
            t = Regex.Replace(t, @"^(?:(?:please\s+)?(?:find|get|show|search(?:\s+for)?|look(?:\s+for)?|shop(?:\s+for)?|browse|buy|order|i\s+(?:need|want)(?:\s+to\s+(?:buy|find|see))?|i'?d\s+like|let'?s\s+see|give)(?:\s+me)?\s+)", "");
            t = Regex.Replace(t, @"^(?:(?:a|an|the|some|any|new|another|other)\s+)+", "");
            t = Regex.Replace(t, @"\s+(?:that|which|to)\s+(?:will\s+|would\s+|can\s+|could\s+)?fits?\b.*$", "");
            t = Regex.Replace(t, @"\s+(?:for|in)\s+(?:the|this|my|here|there|it)\b.*$", "");
            t = Regex.Replace(t, @"\s+(?:please|for me|in the catalog)$", "");
            var field = new CatalogTextField();
            field.Set(t);
            return field.Query;
        }

        /// The file a site's catalog is kept in on the headset: "kitchen.json" (anything but a–z, 0–9, '-', '_' → '_').
        public static string CacheFile(string site)
        {
            if (string.IsNullOrWhiteSpace(site)) site = "built-in";
            var sb = new System.Text.StringBuilder(site.Length + 5);
            foreach (char ch in site.Trim().ToLowerInvariant())
                sb.Append((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_' ? ch : '_');
            return sb.Append(".json").ToString();
        }

        /// GET /catalog's path.
        public static string CatalogPath(string site, string sessionId) =>
            $"/catalog?site={System.Uri.EscapeDataString(site ?? "")}&session_id={System.Uri.EscapeDataString(sessionId ?? "")}";

        // ---------------- gaze-catalog ----------------

        /// GET /catalog's path for what the wearer looks at (no focus: the site's whole catalog, as before).
        public static string CatalogPath(string site, string sessionId, CatalogFocus focus)
        {
            string wire = CatalogFoci.Wire(focus);
            return wire == null ? CatalogPath(site, sessionId) : CatalogPath(site, sessionId) + "&focus=" + wire;
        }

        /// The headset's copy of a site's catalog for a focus: "zabel-gymnasium__roof.json" (none: "zabel-gymnasium.json").
        public static string CacheFile(string site, CatalogFocus focus)
        {
            string wire = CatalogFoci.Wire(focus);
            return wire == null ? CacheFile(site) : CacheFile((string.IsNullOrWhiteSpace(site) ? "built-in" : site.Trim()) + "__" + wire);
        }

        /// The in-memory key of a site's catalog for a focus.
        public static string FocusKey(string site, CatalogFocus focus) => (site ?? "") + "|" + (CatalogFoci.Wire(focus) ?? "");

        /// The Catalog header's focus chip:
        /// - the laptop doesn't answer focus for this site (or the catalog is the built-in one): "All categories";
        /// - pinned: "Pinned: the roof" ("Pinned: all categories" with nothing in focus);
        /// - following the gaze: "Looking at: the roof ⌄" (or what's there: "Looking at: the dishwasher ⌄"), and
        ///   "Following your gaze ⌄" while nothing in particular is.
        public static string FocusChip(CatalogFocus focus, string subject, bool pinned, bool supported, bool indoor)
        {
            if (!supported) return "All categories";
            string name = focus == CatalogFocus.None ? null : !string.IsNullOrWhiteSpace(subject) ? subject.Trim() : CatalogFoci.Name(focus, indoor);
            if (pinned) return "Pinned: " + (name ?? "all categories");
            return name == null ? "Following your gaze " + Caret : "Looking at: " + name + " " + Caret;
        }

        /// The chip's "open" mark: Inter's ⌄ (U+2304; Inter has no ▾, and no size tag may shrink text below the floor).
        public const string Caret = "⌄";

        /// GET /catalog/search's path.
        public static string SearchPath(string q, string site, int limit) =>
            $"/catalog/search?q={System.Uri.EscapeDataString(q ?? "")}&site={System.Uri.EscapeDataString(site ?? "")}&limit={limit}";
    }
}
