using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AirTools.Parts
{
    /// Parts shipped inside the app (the fixture parts): the offline fallback when the parts server is unreachable,
    /// and the synchronous path for EditMode tests. Built by AirTools ▸ Build Part Catalog.
    [CreateAssetMenu(menuName = "AirTools/Part Catalog")]
    public class PartCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public string id;
            public TextAsset partJson;
            public GameObject model;
            public Texture2D image;
            public string[] keywords = Array.Empty<string>();
        }

        public List<Entry> entries = new List<Entry>();

        public Entry Find(string id) => entries.Find(e => e.id == id);

        public PartSpec Spec(string id)
        {
            var e = Find(id);
            return e != null && e.partJson != null ? PartSpec.Parse(e.partJson.text) : null;
        }

        /// Same matching as the mock server: any query word in the entry's keywords; no match → everything.
        public List<PartSummary> Search(string query)
        {
            var words = new HashSet<string>();
            foreach (Match m in Regex.Matches((query ?? "").ToLowerInvariant(), "[a-z0-9-]+")) words.Add(m.Value);
            var hits = new List<PartSummary>();
            foreach (var e in entries)
            {
                bool match = false;
                foreach (var k in e.keywords) if (words.Contains(k)) { match = true; break; }
                if (match && Spec(e.id) is PartSpec s) hits.Add(s.ToSummary());
            }
            if (hits.Count == 0)
                foreach (var e in entries) if (Spec(e.id) is PartSpec s) hits.Add(s.ToSummary());
            return hits;
        }
    }
}
