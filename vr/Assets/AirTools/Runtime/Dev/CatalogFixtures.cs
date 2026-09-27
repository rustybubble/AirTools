using System;
using System.Collections.Generic;
using System.IO;
using AirTools.Parts;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AirTools.Dev
{
    /// catalog: a stand-in for catalog-backend's endpoints until they land (and for EditMode tests): answers
    /// GET /catalog (gaze-catalog: and ?focus=, from focus/<file>.<focus>.json) and GET /catalog/search from
    /// Assets/AirTools/Fixtures/Catalog/{kitchen,roof,facade}.json (the contract's
    /// shape, made from the demo backend's cached parts). /catalog picks the fixture by the site's kind; /catalog/search
    /// looks through every fixture (a store has more than the room's catalog) with the local filter's matching, limited.
    /// Pure: the file reader is passed in.
    public static class CatalogFixtures
    {
        public const string Folder = "Assets/AirTools/Fixtures/Catalog";
        public static readonly string[] Files = { "kitchen", "roof", "facade" };
        /// gaze-catalog: the foci a fixture may answer, in the laptop's order.
        public static readonly string[] FocusWires = { "roof", "wall", "ground", "ceiling", "counter", "opening" };

        public static string FileFor(SiteKind kind) => kind switch
        {
            SiteKind.Kitchen => "kitchen",
            SiteKind.Roof => "roof",
            SiteKind.Facade => "facade",
            _ => "facade",
        };

        /// (status, body) for a GET path + query ("/catalog?site=kitchen&session_id=…", "/catalog/search?q=dishw&…").
        public static (long code, string body) Answer(string pathAndQuery, Func<string, string> read)
        {
            if (string.IsNullOrEmpty(pathAndQuery) || read == null) return (400, null);
            int qm = pathAndQuery.IndexOf('?');
            string path = qm < 0 ? pathAndQuery : pathAndQuery.Substring(0, qm);
            var args = Query(qm < 0 ? "" : pathAndQuery.Substring(qm + 1));
            args.TryGetValue("site", out string site);
            try
            {
                if (path == "/catalog")
                {
                    string file = FileFor(CatalogFallback.KindOf(site));
                    // gaze-catalog: Fixtures/Catalog/focus/<file>.<focus>.json (made from the laptop's GET /catalog?focus=)
                    // answer a focus; the kind's foci are the ones that have a file.
                    args.TryGetValue("focus", out string focus);
                    var foci = new JArray();
                    string focused = null;
                    foreach (var f in FocusWires)
                    {
                        string t = read($"{Folder}/focus/{file}.{f}.json");
                        if (t == null) continue;
                        foci.Add(f);
                        if (f == focus) focused = t;
                    }
                    string text = focused ?? read($"{Folder}/{file}.json");
                    if (CatalogJson.Parse(text) == null) return (404, "{\"detail\":\"no fixture\"}");
                    var doc = JObject.Parse(text);   // the file as written (its JSON, not a round trip through the classes)
                    doc["site"] = site;
                    if (foci.Count > 0) { doc["foci"] = foci; doc["focus"] = focused != null ? focus : null; }
                    return (200, doc.ToString(Formatting.None));
                }
                if (path == "/catalog/search")
                {
                    args.TryGetValue("q", out string q);
                    int limit = args.TryGetValue("limit", out string l) && int.TryParse(l, out int n) ? n : 20;
                    var items = new JArray();
                    var cats = new JArray();
                    var seen = new HashSet<string>();
                    foreach (var f in Files)
                    {
                        string text = read($"{Folder}/{f}.json");
                        var doc = CatalogJson.Parse(text);
                        if (doc == null) continue;
                        var raw = JObject.Parse(text);
                        var hits = new List<CatalogHit>();
                        CatalogSearch.Filter(new CatalogIndex(doc), q, hits);
                        foreach (var h in hits)
                        {
                            if (items.Count >= limit || !seen.Add(h.Id)) continue;
                            if (Find(raw, h.Id) is JObject item) items.Add(item.DeepClone());
                            string cat = h.Category >= 0 && h.Category < doc.categories.Count ? doc.categories[h.Category].id : null;
                            if (cat != null && !cats.Any(c => (string)c == cat)) cats.Add(cat);
                        }
                    }
                    var reply = new JObject { ["q"] = q, ["items"] = items, ["categories"] = cats };
                    return (200, reply.ToString(Formatting.None));
                }
            }
            catch (Exception ex) { return (500, $"{{\"detail\":\"{ex.Message.Replace("\"", "'")}\"}}"); }
            return (404, "{\"detail\":\"Not Found\"}");
        }

        /// The Editor's reader (the project's files, relative to the project folder).
        public static (long code, string body) FromProject(string pathAndQuery) => Answer(pathAndQuery, p => File.Exists(p) ? File.ReadAllText(p) : null);

        static JObject Find(JObject doc, string id)
        {
            if (!(doc["categories"] is JArray cats)) return null;
            foreach (var c in cats)
                if (c is JObject co && co["items"] is JArray items)
                    foreach (var i in items)
                        if (i is JObject io && (string)io["part_id"] == id) return io;
            return null;
        }

        static Dictionary<string, string> Query(string q)
        {
            var d = new Dictionary<string, string>();
            foreach (var part in q.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = Uri.UnescapeDataString(eq < 0 ? part : part.Substring(0, eq));
                string v = eq < 0 ? "" : Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
                d[k] = v;
            }
            return d;
        }
    }
}
