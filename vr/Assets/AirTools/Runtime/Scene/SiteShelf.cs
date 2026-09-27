using System.Collections.Generic;

namespace AirTools.Scene
{
    /// What one owner keeps for the sites that aren't loaded (sitescope; pure). The owner's live lists belong to Live;
    /// Swap parks them under Live and hands back what `to` had parked. T is the owner's own bundle of lists (items, undo
    /// and redo stacks, the objects it hid).
    public sealed class SiteShelf<T> where T : class
    {
        readonly Dictionary<string, T> m_Parked = new Dictionary<string, T>();
        readonly List<string> m_Order = new List<string>();

        /// The site the owner's live items belong to.
        public string Live { get; private set; }

        public SiteShelf(string live = null) { Live = string.IsNullOrEmpty(live) ? ModelSites.BuiltIn : live; }

        /// Park `live` (the owner's state for Live; null = nothing to keep) and take out `to`'s (null when it has none).
        public T Swap(string to, T live)
        {
            if (string.IsNullOrEmpty(to)) to = ModelSites.BuiltIn;
            if (to == Live) return null;
            if (live != null) Put(Live, live);
            Live = to;
            return Take(to);
        }

        /// The live site is `site` now without parking anything (an owner that had nothing when it missed a switch).
        public void Follow(string site) { if (!string.IsNullOrEmpty(site)) Live = site; }

        public bool Has(string site) => site != null && m_Parked.ContainsKey(site);

        public T Peek(string site) => site != null && m_Parked.TryGetValue(site, out var t) ? t : null;

        /// Sites with something parked, oldest park first.
        public IReadOnlyList<string> Sites => m_Order;

        public int Count => m_Parked.Count;

        /// Every parked bundle (a units change re-words them; Demo reset destroys them).
        public IEnumerable<T> All
        {
            get { foreach (var s in m_Order) yield return m_Parked[s]; }
        }

        /// Empties the shelf and returns what was on it (for the owner to destroy).
        public List<T> TakeAll()
        {
            var all = new List<T>(m_Parked.Count);
            foreach (var s in m_Order) all.Add(m_Parked[s]);
            m_Parked.Clear();
            m_Order.Clear();
            return all;
        }

        void Put(string site, T state)
        {
            if (!m_Parked.ContainsKey(site)) m_Order.Add(site);
            m_Parked[site] = state;
        }

        T Take(string site)
        {
            if (!m_Parked.TryGetValue(site, out var t)) return null;
            m_Parked.Remove(site);
            m_Order.Remove(site);
            return t;
        }
    }
}
