using System;
using System.Collections.Generic;

namespace AirTools.Parts
{
    /// catalog: one card's worth of the catalog grid — the part (as the parts flow takes it), whether its 3D model is ready,
    /// the category it came from (−1: a live search or the stores) and its seller.
    public sealed class CatalogHit
    {
        public PartSummary Summary;
        public bool ModelReady;
        public int Category = -1;
        public string Seller;
        /// The local filter's score (0 in browse mode and for the server's extra results).
        public int Score;
        /// From GET /catalog/search, not the loaded catalog.
        public bool FromServer;

        public string Id => Summary?.id;

        public static CatalogHit From(CatalogItem item, int category, bool fromServer = false) => new CatalogHit
        {
            Summary = item.ToSummary(), ModelReady = item.ModelReady, Category = category, Seller = item.seller, FromServer = fromServer,
        };

        /// A live-search candidate (full part.json from the real server: its model is ready when the asset says so).
        public static CatalogHit From(PartSummary s) => new CatalogHit
        {
            Summary = s,
            ModelReady = s?.spec?.asset != null ? s.spec.asset.Ready && s.spec.asset.tier != "proxy" : false,
            Seller = s?.spec?.RecommendedSeller?.name,
        };
    }

    /// catalog: the loaded catalog flattened for the lazy local filter — one entry per item, with its lower-case words
    /// worked out once (name; category title, id and keywords; the item's keywords and seller), so filtering on each key
    /// press only compares strings. Built when the catalog arrives.
    public sealed class CatalogIndex
    {
        public sealed class Entry
        {
            public CatalogItem Item;
            public int Category;
            public int Order;
            public string[] NameWords;
            public string NameLower;
            public string[] CategoryWords;
            public string[] OtherWords;
        }

        public readonly List<Entry> Entries = new List<Entry>();
        public readonly CatalogResponse Data;

        public CatalogIndex(CatalogResponse data)
        {
            Data = data;
            if (data?.categories == null) return;
            var seen = new HashSet<string>();
            int order = 0;
            for (int c = 0; c < data.categories.Count; c++)
            {
                var cat = data.categories[c];
                var catWords = new List<string>();
                CatalogSearch.AddWords(catWords, cat.title);
                CatalogSearch.AddWords(catWords, cat.id);
                if (cat.keywords != null) foreach (var k in cat.keywords) CatalogSearch.AddWords(catWords, k);
                foreach (var item in cat.items)
                {
                    // An item listed in two categories is found once (its first).
                    if (!seen.Add(item.part_id)) continue;
                    var name = new List<string>();
                    CatalogSearch.AddWords(name, item.name);
                    var other = new List<string>();
                    CatalogSearch.AddWords(other, cat.query);
                    CatalogSearch.AddWords(other, item.seller);
                    if (item.keywords != null) foreach (var k in item.keywords) CatalogSearch.AddWords(other, k);
                    Entries.Add(new Entry
                    {
                        Item = item, Category = c, Order = order++, NameWords = name.ToArray(),
                        NameLower = (item.name ?? "").ToLowerInvariant(), CategoryWords = catWords.ToArray(), OtherWords = other.ToArray(),
                    });
                }
            }
        }
    }

    /// catalog: the lazy local search (pure). Every query word must match the item somewhere; a word matches when it
    /// starts a word of the item (as typed so far: "dishw" finds "Dishwasher") or, from three letters, sits inside the
    /// name. Quoted text ("french door") must appear in the name as typed. Ranked by where the words matched (the name
    /// first, then the category, then the rest), then by the catalog's own order.
    public static class CatalogSearch
    {
        public const int NamePrefix = 6, NameExact = 2, NameInside = 3, CategoryPrefix = 4, OtherPrefix = 1, Phrase = 5;

        /// Lower-case words of `text` (letters, digits and '-' runs; "5,000" → "5000"), appended to `into`.
        public static void AddWords(List<string> into, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i <= text.Length; i++)
            {
                char ch = i < text.Length ? char.ToLowerInvariant(text[i]) : ' ';
                if (char.IsLetterOrDigit(ch) || (ch == '-' && sb.Length > 0)) { sb.Append(ch); continue; }
                if (ch == ',' && sb.Length > 0 && i + 1 < text.Length && char.IsDigit(text[i + 1]) && char.IsDigit(sb[sb.Length - 1])) continue;   // 5,000
                if (sb.Length == 0) continue;
                string w = sb.ToString().Trim('-');
                if (w.Length > 0) into.Add(w);
                sb.Clear();
            }
        }

        /// The query as words and quoted phrases (lower case). An unclosed quote runs to the end.
        public static void Parse(string query, List<string> words, List<string> phrases)
        {
            words.Clear();
            phrases.Clear();
            if (string.IsNullOrWhiteSpace(query)) return;
            var q = query.ToLowerInvariant();
            int i = 0;
            var rest = new System.Text.StringBuilder();
            while (i < q.Length)
            {
                if (q[i] == '"')
                {
                    int end = q.IndexOf('"', i + 1);
                    string phrase = (end < 0 ? q.Substring(i + 1) : q.Substring(i + 1, end - i - 1)).Trim();
                    if (phrase.Length > 0) phrases.Add(CollapseSpaces(phrase));
                    i = end < 0 ? q.Length : end + 1;
                    rest.Append(' ');
                    continue;
                }
                rest.Append(q[i]);
                i++;
            }
            AddWords(words, rest.ToString());
        }

        static string CollapseSpaces(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            bool space = false;
            foreach (char ch in s)
            {
                if (char.IsWhiteSpace(ch)) { if (!space) sb.Append(' '); space = true; }
                else { sb.Append(ch); space = false; }
            }
            return sb.ToString();
        }

        /// How well one entry matches (0: it doesn't).
        public static int Score(CatalogIndex.Entry e, List<string> words, List<string> phrases)
        {
            if (words.Count == 0 && phrases.Count == 0) return 0;
            int total = 0;
            foreach (var p in phrases)
            {
                if (e.NameLower.IndexOf(p, StringComparison.Ordinal) < 0) return 0;
                total += Phrase;
            }
            foreach (var w in words)
            {
                int s = WordScore(e, w);
                if (s == 0) return 0;
                total += s;
            }
            return total;
        }

        static int WordScore(CatalogIndex.Entry e, string w)
        {
            int best = 0;
            foreach (var n in e.NameWords)
                if (n.StartsWith(w, StringComparison.Ordinal)) best = Math.Max(best, NamePrefix + (n.Length == w.Length ? NameExact : 0));
            if (best > 0) return best;
            foreach (var c in e.CategoryWords)
                if (c.StartsWith(w, StringComparison.Ordinal) || Stem(c).StartsWith(w, StringComparison.Ordinal)) return CategoryPrefix;
            if (w.Length >= 3 && e.NameLower.IndexOf(w, StringComparison.Ordinal) >= 0) return NameInside;
            foreach (var o in e.OtherWords)
                if (o.StartsWith(w, StringComparison.Ordinal)) return OtherPrefix;
            // "fridges" typed in full against "fridge": the typed word's singular.
            string sw = Stem(w);
            if (sw.Length != w.Length && sw.Length >= 3)
            {
                foreach (var n in e.NameWords) if (n.StartsWith(sw, StringComparison.Ordinal)) return NamePrefix;
                foreach (var c in e.CategoryWords) if (c.StartsWith(sw, StringComparison.Ordinal)) return CategoryPrefix;
            }
            return 0;
        }

        /// "dishwashers" → "dishwasher", "shelves" → "shelf", "boxes" → "box" (the plural the category titles use).
        public static string Stem(string w)
        {
            if (w == null || w.Length <= 3) return w ?? "";
            if (w.EndsWith("ves", StringComparison.Ordinal)) return w.Substring(0, w.Length - 3) + "f";
            if (w.EndsWith("ies", StringComparison.Ordinal) && w.Length > 4) return w.Substring(0, w.Length - 3) + "y";
            if (w.EndsWith("ches", StringComparison.Ordinal) || w.EndsWith("shes", StringComparison.Ordinal) || w.EndsWith("xes", StringComparison.Ordinal) || w.EndsWith("sses", StringComparison.Ordinal))
                return w.Substring(0, w.Length - 2);
            if (w.EndsWith("s", StringComparison.Ordinal) && !w.EndsWith("ss", StringComparison.Ordinal)) return w.Substring(0, w.Length - 1);
            return w;
        }

        [ThreadStatic] static List<string> s_Words, s_Phrases;

        /// The local results for `query`, best first (ties keep the catalog's order), into `results` (cleared).
        /// Allocates only the hits it returns.
        public static void Filter(CatalogIndex index, string query, List<CatalogHit> results)
        {
            results.Clear();
            if (index == null) return;
            s_Words ??= new List<string>();
            s_Phrases ??= new List<string>();
            Parse(query, s_Words, s_Phrases);
            if (s_Words.Count == 0 && s_Phrases.Count == 0) return;
            foreach (var e in index.Entries)
            {
                int score = Score(e, s_Words, s_Phrases);
                if (score <= 0) continue;
                var hit = CatalogHit.From(e.Item, e.Category);
                hit.Score = score * 10000 - Math.Min(e.Order, 9999);   // stable within a score
                results.Add(hit);
            }
            results.Sort((a, b) => b.Score.CompareTo(a.Score));
        }

        /// The server's results after the local ones: those the local filter didn't already find (by part id), in the
        /// server's order. Returns how many were added.
        public static int Merge(List<CatalogHit> results, IList<CatalogItem> server, CatalogIndex index)
        {
            if (server == null) return 0;
            var have = new HashSet<string>();
            foreach (var h in results) if (h.Id != null) have.Add(h.Id);
            int added = 0;
            foreach (var item in server)
            {
                if (item == null || string.IsNullOrEmpty(item.part_id) || !have.Add(item.part_id)) continue;
                results.Add(CatalogHit.From(item, CategoryOf(index, item.part_id), fromServer: true));
                added++;
            }
            return added;
        }

        static int CategoryOf(CatalogIndex index, string partId)
        {
            if (index == null) return -1;
            foreach (var e in index.Entries) if (e.Item.part_id == partId) return e.Category;
            return -1;
        }
    }
}
