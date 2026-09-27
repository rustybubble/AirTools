using System;
using System.Collections.Generic;

namespace AirTools.Scene
{
    /// scalemodels: which model the background prefetch (ScenePrefetcher) downloads next. Pure (EditMode-tested).
    /// - **Order.** The switcher's (ModelSites.Order): kitchen, Zabel gym, hospital, the GT scans, the rest A–Z.
    /// - **Skipped:**
    ///   - the hidden test packages and the built-in facade;
    ///   - what's already on the headset at the listed revision;
    ///   - the site a load is fetching;
    ///   - a site that failed within `retrySeconds`.
    /// - **Never while a load runs:** the load has the network to itself.
    /// - **Resume.** Per file: a package's cached files are kept, and only the missing ones are fetched
    ///   (SceneCache.Missing).
    public static class PrefetchPlan
    {
        /// The listed sites still to download, in the switcher's order. `onHeadset(site, revision)`: that revision (or a
        /// newer one) is complete in the cache.
        public static List<string> Order(IEnumerable<SceneListing> listed, Func<string, int, bool> onHeadset)
        {
            var revisions = new Dictionary<string, int>();
            var ids = new List<string>();
            if (listed != null)
                foreach (var s in listed)
                {
                    if (s == null || string.IsNullOrWhiteSpace(s.site) || revisions.ContainsKey(s.site)) continue;
                    revisions[s.site] = s.revision;
                    ids.Add(s.site);
                }
            var order = new List<string>();
            foreach (var site in ModelSites.Order(ids))
            {
                if (site == ModelSites.BuiltIn || !revisions.TryGetValue(site, out int rev)) continue;
                if (onHeadset != null && onHeadset(site, rev)) continue;
                order.Add(site);
            }
            return order;
        }

        /// The site to download now, or null: nothing while a load runs; the one loading and the ones that failed in the
        /// last `retrySeconds` are skipped.
        public static string Next(IReadOnlyList<string> order, bool loadRunning, string loadingSite, IReadOnlyDictionary<string, float> failedAt,
                                  float now, float retrySeconds)
        {
            if (loadRunning || order == null) return null;
            foreach (var site in order)
            {
                if (site == loadingSite) continue;
                if (failedAt != null && failedAt.TryGetValue(site, out float t) && now - t < retrySeconds) continue;
                return site;
            }
            return null;
        }

        /// "Models on the headset: 5 of 7": the switcher's models (listed and not hidden, plus the built-in facade, which
        /// is always on the headset) and how many of them open with the laptop away.
        public static void Count(IEnumerable<string> known, Func<string, bool> onHeadset, out int on, out int total)
        {
            on = total = 0;
            foreach (var site in ModelSites.Order(known))
            {
                total++;
                if (site == ModelSites.BuiltIn || (onHeadset != null && onHeadset(site))) on++;
            }
        }

        public static string CountText(int on, int total) => $"Models on the headset: {on} of {total}";
    }
}
