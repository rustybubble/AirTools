using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AirTools.Parts
{
    // catalog: the environment catalog's wire shapes (catalog-backend contract) and a tolerant parser. Pure: no engine
    // calls, so the offline runner tests it.
    //
    //   GET /catalog?site=<site>&session_id=<id>[&focus=roof|wall|ground|ceiling|counter|opening]
    //   → {"site","environment","title","generated_at","focus","foci":[…],"categories":[{"id","title","icon","query","items":[item…]}]}
    //   GET /catalog/search?q=<text>&site=<site>&limit=20
    //   → {"q","items":[item…],"categories":[ids that matched]}
    //   item = {"part_id","name","price_usd","dims_mm":{"w","h","d"},"image_url","model_url","model_ready","seller"}

    /// One item of the catalog (a part the laptop knows: its photo, name, price, size and whether its 3D model is ready).
    public class CatalogItem
    {
        public string part_id;
        public string name;
        public float? price_usd;
        public PartDims dims_mm;
        public string image_url;
        public string model_url;
        public bool? model_ready;
        public string seller;
        /// Not in the contract (the built-in fallback fills it): extra words the local filter matches.
        public List<string> keywords;

        /// "3D" badge: the model is on the laptop (or the item is in the app's shipped catalog).
        public bool ModelReady => model_ready == true;

        /// The parts flow's shape (PartsBrowser.Select → PartLoader.Load): the part.json / model / photo URLs follow the
        /// backend's /parts/{id}/… layout when the item doesn't name them.
        public PartSummary ToSummary() => new PartSummary
        {
            id = part_id,
            name = string.IsNullOrWhiteSpace(name) ? part_id : name,
            dims_mm = dims_mm ?? new PartDims(),
            price_usd = price_usd,
            image_url = string.IsNullOrEmpty(image_url) ? $"/parts/{part_id}/image.jpg" : image_url,
            model_url = string.IsNullOrEmpty(model_url) ? $"/parts/{part_id}/model.glb" : model_url,
            part_url = $"/parts/{part_id}/part.json",
        };
    }

    /// One category of the environment ("Dishwashers"): its items and the store query that finds more of them.
    public class CatalogCategory
    {
        public string id;
        public string title;
        public string icon;
        public string query;
        public List<CatalogItem> items = new List<CatalogItem>();
        /// Not in the contract (the built-in table fills it): words that name the category ("fridge", "refrigerator").
        public List<string> keywords;

        public string Title => !string.IsNullOrWhiteSpace(title) ? title : !string.IsNullOrWhiteSpace(id) ? id : "Items";
        /// The live search this category stands for (its query, else its title).
        public string Query => !string.IsNullOrWhiteSpace(query) ? query.Trim() : Title;
    }

    /// GET /catalog: the environment's categories for the loaded site.
    public class CatalogResponse
    {
        public string site;
        public string environment;
        public string title;
        public string generated_at;
        public List<CatalogCategory> categories = new List<CatalogCategory>();
        /// gaze-catalog: the focus this answer is for ("roof", "wall"…; null: the whole place, or a laptop that doesn't
        /// know focus) and the foci the site answers (empty / null: none).
        public string focus;
        public List<string> foci;

        public int ItemCount
        {
            get
            {
                int n = 0;
                foreach (var c in categories) n += c.items.Count;
                return n;
            }
        }
    }

    /// GET /catalog/search.
    public class CatalogSearchResponse
    {
        public string q;
        public List<CatalogItem> items = new List<CatalogItem>();
        public List<string> categories = new List<string>();
    }

    /// Parses the catalog endpoints tolerantly: unknown fields are ignored, missing ones stay null, a field of the wrong
    /// type is skipped rather than failing the whole reply, items without a part_id and null entries are dropped.
    public static class CatalogJson
    {
        static readonly JsonSerializerSettings s_Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            Error = (sender, e) => e.ErrorContext.Handled = true,
        };

        /// null when the text isn't a JSON object.
        public static CatalogResponse Parse(string json)
        {
            var o = Object(json);
            if (o == null) return null;
            CatalogResponse r;
            try { r = o.ToObject<CatalogResponse>(JsonSerializer.Create(s_Settings)); }
            catch (Exception) { return null; }
            if (r == null) return null;
            r.categories ??= new List<CatalogCategory>();
            r.categories.RemoveAll(c => c == null);
            foreach (var c in r.categories) Clean(c);
            // A category without an id or a title can't be named or shown.
            r.categories.RemoveAll(c => string.IsNullOrWhiteSpace(c.id) && string.IsNullOrWhiteSpace(c.title));
            return r;
        }

        /// null when the text isn't a JSON object.
        public static CatalogSearchResponse ParseSearch(string json)
        {
            var o = Object(json);
            if (o == null) return null;
            CatalogSearchResponse r;
            try { r = o.ToObject<CatalogSearchResponse>(JsonSerializer.Create(s_Settings)); }
            catch (Exception) { return null; }
            if (r == null) return null;
            r.items = CleanItems(r.items);
            r.categories ??= new List<string>();
            r.categories.RemoveAll(string.IsNullOrWhiteSpace);
            return r;
        }

        static JObject Object(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JToken.Parse(json) as JObject; }
            catch (Exception) { return null; }
        }

        static void Clean(CatalogCategory c)
        {
            c.items = CleanItems(c.items);
            if (string.IsNullOrWhiteSpace(c.id) && !string.IsNullOrWhiteSpace(c.title)) c.id = Slug(c.title);
        }

        static List<CatalogItem> CleanItems(List<CatalogItem> items)
        {
            var list = items ?? new List<CatalogItem>();
            list.RemoveAll(i => i == null || string.IsNullOrWhiteSpace(i.part_id));
            foreach (var i in list)
            {
                i.part_id = i.part_id.Trim();
                if (i.price_usd.HasValue && !(i.price_usd.Value > 0f)) i.price_usd = null;   // 0 / negative / NaN: no price
            }
            return list;
        }

        /// "Range hoods" → "range-hoods".
        public static string Slug(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            bool dash = false;
            foreach (char ch in s.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch)) { sb.Append(ch); dash = false; }
                else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
            }
            if (sb.Length > 0 && sb[sb.Length - 1] == '-') sb.Length--;
            return sb.ToString();
        }
    }
}
