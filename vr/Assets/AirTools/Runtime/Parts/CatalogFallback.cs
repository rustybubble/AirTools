using System;
using System.Collections.Generic;

namespace AirTools.Parts
{
    /// catalog: what kind of place a site is, for the built-in categories when there's no laptop and no saved catalog.
    public enum SiteKind { Generic, Kitchen, Roof, Facade }

    /// catalog: the catalog when neither the laptop nor the headset's saved copy has one — a small built-in table of
    /// categories per kind of place, filled with the parts shipped in the app (PartCatalog) whose words match. Pure.
    public static class CatalogFallback
    {
        /// One built-in category: id, title, the store query it stands for, and the words that put a part in it.
        public readonly struct Row
        {
            public readonly string Id, Title, Query;
            public readonly string[] Words;
            public Row(string id, string title, string query, params string[] words) { Id = id; Title = title; Query = query; Words = words; }
        }

        static readonly Row[] s_Kitchen =
        {
            new Row("fridges", "Fridges", "refrigerator", "fridge", "refrigerator", "freezer", "french-door"),
            new Row("dishwashers", "Dishwashers", "built-in dishwasher", "dishwasher", "dishwashers"),
            new Row("ranges", "Ranges", "slide-in range", "range", "oven", "stove", "induction"),
            new Row("microwaves", "Microwaves", "over the range microwave", "microwave"),
            new Row("cooktops", "Cooktops", "cooktop", "cooktop", "stovetop", "hob"),
            new Row("range-hoods", "Range hoods", "range hood", "hood", "vent"),
            new Row("cabinet-hardware", "Hardware", "cabinet knob", "knob", "pull", "handle", "hinge", "hinges"),
            new Row("drawer-slides", "Drawer slides", "drawer slide", "slide", "slides", "drawer"),
        };

        static readonly Row[] s_Roof =
        {
            new Row("gutters", "Gutters", "k-style gutter", "gutter", "downspout"),
            new Row("gutter-hangers", "Gutter hangers", "gutter hanger", "hanger", "fascia", "k-style"),
            new Row("hvac", "HVAC units", "rooftop hvac unit", "hvac", "condenser", "heat", "pump", "conditioner", "ac", "btu"),
            new Row("solar", "Solar panels", "solar panel", "solar", "pv", "panel"),
            new Row("chimneys", "Chimney caps", "chimney cap", "chimney", "flue", "cap"),
            new Row("roof-vents", "Roof vents", "roof vent", "vent", "turbine", "exhaust"),
            new Row("skylights", "Skylights", "skylight", "skylight"),
            new Row("snow-guards", "Snow guards", "snow guard", "snow", "guard"),
        };

        static readonly Row[] s_Facade =
        {
            new Row("gutter-hangers", "Gutter hangers", "gutter hanger", "hanger", "gutter", "fascia", "k-style"),
            new Row("window-ac", "Window AC", "window ac", "ac", "air", "conditioner", "btu", "cooling"),
            new Row("gutters", "Gutters", "k-style gutter", "gutter", "downspout"),
            new Row("windows", "Windows", "replacement window", "window", "sash"),
            new Row("lights", "Outdoor lights", "outdoor wall light", "light", "lamp", "sconce", "led"),
            new Row("shutters", "Shutters", "exterior shutters", "shutter", "shutters"),
            new Row("brackets", "Brackets", "shelf bracket", "bracket", "brackets"),
        };

        static readonly Row[] s_Generic =
        {
            new Row("hardware", "Hardware", "hardware", "bracket", "hinge", "hanger", "knob", "slide"),
            new Row("appliances", "Appliances", "appliance", "dishwasher", "refrigerator", "range", "microwave"),
            new Row("hvac", "Heating & cooling", "window ac", "ac", "air", "conditioner", "hvac", "btu"),
            new Row("lighting", "Lighting", "led light", "light", "lamp", "led"),
        };

        public static IReadOnlyList<Row> Rows(SiteKind kind) => kind switch
        {
            SiteKind.Kitchen => s_Kitchen,
            SiteKind.Roof => s_Roof,
            SiteKind.Facade => s_Facade,
            _ => s_Generic,
        };

        public static string Title(SiteKind kind) => kind switch
        {
            SiteKind.Kitchen => "Kitchen",
            SiteKind.Roof => "Roof",
            SiteKind.Facade => "House front",
            _ => "Parts",
        };

        /// The kind of place: the server's `environment` when it names one, else the site id's words (kitchen; a roof,
        /// canopy, gym, pavilion, tower or hospital scan from the drone; the synthetic / built-in house front).
        public static SiteKind KindOf(string site, string environment = null)
        {
            var e = Match(environment);
            if (e != SiteKind.Generic) return e;
            return Match(site);
        }

        static SiteKind Match(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return SiteKind.Generic;
            s = s.ToLowerInvariant();
            if (Has(s, "kitchen", "pantry", "galley")) return SiteKind.Kitchen;
            if (Has(s, "facade", "façade", "synthetic", "built-in", "builtin", "house-front", "house front", "exterior")) return SiteKind.Facade;
            if (Has(s, "roof", "canopy", "gym", "pavilion", "tower", "hospital", "lcc", "rooftop", "attic")) return SiteKind.Roof;
            return SiteKind.Generic;
        }

        static bool Has(string s, params string[] words)
        {
            foreach (var w in words) if (s.Contains(w)) return true;
            return false;
        }

        /// A shipped part: its summary and its words (PartCatalog.Entry.keywords).
        public readonly struct LocalPart
        {
            public readonly PartSummary Summary;
            public readonly IList<string> Words;
            public LocalPart(PartSummary summary, IList<string> words) { Summary = summary; Words = words; }
        }

        /// The built-in catalog for `site`: every category of its kind (empty ones too: they still search the stores),
        /// each with the shipped parts that match its words. A part goes in the category that shares the most words with
        /// it (the first on a tie): a gutter hanger is a hanger before it is a gutter.
        public static CatalogResponse Build(string site, SiteKind kind, IList<LocalPart> parts)
        {
            var r = new CatalogResponse { site = site, environment = kind.ToString().ToLowerInvariant(), title = Title(kind), generated_at = null };
            var rows = Rows(kind);
            foreach (var row in rows)
                r.categories.Add(new CatalogCategory { id = row.Id, title = row.Title, query = row.Query, keywords = new List<string>(row.Words) });
            if (parts == null) return r;
            var placed = new HashSet<string>();
            foreach (var p in parts)
            {
                if (p.Summary == null || string.IsNullOrEmpty(p.Summary.id) || !placed.Add(p.Summary.id)) continue;
                int best = BestRow(p, rows);
                if (best >= 0) r.categories[best].items.Add(Item(p));
            }
            return r;
        }

        /// The row sharing the most words with the part (−1: none).
        public static int BestRow(LocalPart p, IReadOnlyList<Row> rows)
        {
            var words = new HashSet<string>(StringComparer.Ordinal);
            if (p.Words != null) foreach (var w in p.Words) if (!string.IsNullOrEmpty(w)) words.Add(w.ToLowerInvariant());
            var nameWords = new List<string>();
            CatalogSearch.AddWords(nameWords, p.Summary?.name);
            foreach (var w in nameWords) words.Add(w);
            int best = -1, bestCount = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                int n = 0;
                foreach (var w in rows[i].Words) if (words.Contains(w)) n++;
                if (n > bestCount) { best = i; bestCount = n; }
            }
            return best;
        }

        static CatalogItem Item(LocalPart p) => new CatalogItem
        {
            part_id = p.Summary.id,
            name = p.Summary.name,
            price_usd = p.Summary.price_usd,
            dims_mm = p.Summary.dims_mm,
            image_url = p.Summary.image_url,
            model_url = p.Summary.model_url,
            model_ready = true,   // shipped with its model (PartLoader loads it from the catalog)
            keywords = p.Words != null ? new List<string>(p.Words) : null,
        };
    }
}
