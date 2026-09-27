using System;
using System.Collections.Generic;
using AirTools.UI;

namespace AirTools.Scene
{
    // scalemodels appends NotDownloaded: offline, a model that isn't on the headset (dimmed; a tap asks for the laptop).
    public enum ModelCardState { Idle, Current, Loading, Queued, Failed, NotDownloaded }

    /// Which models the Model view switcher offers, in what order, and how a spoken name finds one (modelview; pure).
    /// The list is the server's GET /scenes (SceneStreamer.Sites, already without its ignoreSites) minus the test
    /// variants, plus the scene built into the app, which is the one facade offered: kitchen, Zabel gym, hospital, the
    /// Georgia Tech scans, any other scan (A–Z), then the facade. The hidden test packages stay reachable by name
    /// (show_model, Settings ▸ Next scene, LoadSite).
    public static class ModelSites
    {
        /// The scene baked into the app (SceneStreamer.LoadBuiltIn; AppCommands.LoadSite("built-in")).
        public const string BuiltIn = "built-in";

        /// The order the switcher shows the known scans in; the rest follow A–Z, then the built-in facade.
        public static readonly string[] Preferred = { "kitchen", "zabel-gymnasium", "hospital-bg", "gt-lcc-canopy", "gt-lcc-pavilion", "gt-lcc-tower" };

        /// Test variants the switcher leaves out: the server's synthetic-facade packages (the built-in facade is the
        /// same model; -parts, -preview and -small are fixtures for the scene-parts, streaming and scale checks).
        public static bool Hidden(string site) =>
            string.IsNullOrWhiteSpace(site) || site.StartsWith("synthetic-", StringComparison.Ordinal) || site == "synthetic";

        /// The switcher's list: every listed site that isn't hidden, in order, then the built-in facade. No duplicates.
        public static List<string> Order(IEnumerable<string> listed)
        {
            var sites = new List<string>();
            if (listed != null)
                foreach (var s in listed)
                    if (!Hidden(s) && s != BuiltIn && !sites.Contains(s)) sites.Add(s);
            sites.Sort(Compare);
            sites.Add(BuiltIn);
            return sites;
        }

        static int Rank(string site)
        {
            int i = Array.IndexOf(Preferred, site);
            return i >= 0 ? i : Preferred.Length;
        }

        static int Compare(string a, string b)
        {
            int ra = Rank(a), rb = Rank(b);
            return ra != rb ? ra.CompareTo(rb) : string.CompareOrdinal(a, b);
        }

        /// A card's state: the model on the table (highlighted), loading, next in line (a tap while another loads), failed
        /// (tap to retry), or idle. A failed site that is loading again shows Loading.
        public static ModelCardState StateOf(string site, string current, string loading, string queued, ICollection<string> failed)
        {
            if (site == null) return ModelCardState.Idle;
            if (site == loading) return ModelCardState.Loading;
            if (site == queued) return ModelCardState.Queued;
            if (failed != null && failed.Contains(site)) return ModelCardState.Failed;
            return site == current ? ModelCardState.Current : ModelCardState.Idle;
        }

        /// What a card says under its picture: the model's name, or its state while that matters.
        public static string CardText(string name, ModelCardState state) => state switch
        {
            ModelCardState.Loading => "Loading…",
            ModelCardState.Queued => "Up next",
            ModelCardState.Failed => "Retry",
            ModelCardState.NotDownloaded => NotDownloaded,
            _ => name,
        };

        // ---------------- scalemodels: the models on the headset ----------------

        public const string NotDownloaded = "Not downloaded";

        /// A card's state when the laptop may be away: offline, a model that isn't on the headset is NotDownloaded (unless
        /// it's the one showing or loading).
        public static ModelCardState StateOf(string site, string current, string loading, string queued, ICollection<string> failed, bool available)
        {
            var state = StateOf(site, current, loading, queued, failed);
            return !available && (state == ModelCardState.Idle || state == ModelCardState.Failed) ? ModelCardState.NotDownloaded : state;
        }

        /// The name on a card that has a badge over its picture (the badge carries the state): the model's name while it's
        /// not downloaded, else CardText.
        public static string CardName(string name, ModelCardState state) => state == ModelCardState.NotDownloaded ? name : CardText(name, state);

        /// The badge over a card's picture ("" = none).
        public static string Badge(ModelCardState state) => state == ModelCardState.NotDownloaded ? NotDownloaded : "";

        /// Can the switcher open `site` now: the built-in facade always; with the laptop there, anything listed; with it
        /// away, only what's on the headset.
        public static bool Available(string site, bool online, Func<string, bool> onHeadset) =>
            site == BuiltIn || online || (onHeadset != null && onHeadset(site));

        /// With the laptop away: the models that open (every model on the headset, then the built-in facade), in order.
        public static List<string> OfflineList(IEnumerable<string> known, Func<string, bool> onHeadset)
        {
            var on = new List<string>();
            if (known != null) foreach (var s in known) if (onHeadset != null && onHeadset(s)) on.Add(s);
            return Order(on);
        }

        /// Like Step, over the models that are `available` only (offline: ← → skip what isn't on the headset). Null when
        /// none is.
        public static string Step(IReadOnlyList<string> list, string current, int step, Func<string, bool> available)
        {
            if (list == null || list.Count == 0) return null;
            if (available == null) return Step(list, current, step);
            string at = current;
            for (int k = 0; k < list.Count; k++)
            {
                at = Step(list, at, step);
                if (at == null) return null;
                if (available(at)) return at;
            }
            return null;
        }
        // end scalemodels

        /// The card's name: "Kitchen", "Zabel gym", "Hospital", "GT tower", "Test facade".
        public static string Name(string site) => Copy.Cap(Copy.SiteName(site == BuiltIn ? null : site));

        /// The site showing now as the switcher names it: the loaded scan, or the built-in facade.
        public static string Current(bool runtimePackage, string site) => runtimePackage && !string.IsNullOrEmpty(site) ? site : BuiltIn;

        /// The site `step` places after `current` in `list`, wrapping (+1 next, −1 previous). A current site that isn't in
        /// the list (a hidden test package) steps from the start. Null for an empty list.
        public static string Step(IReadOnlyList<string> list, string current, int step)
        {
            if (list == null || list.Count == 0) return null;
            int i = IndexOf(list, current);
            if (i < 0) return step >= 0 ? list[0] : list[list.Count - 1];
            int n = list.Count;
            return list[((i + step) % n + n) % n];
        }

        /// The index of the position nearest `target` (the site picture: the photo taken nearest the recommended spawn);
        /// −1 for none.
        public static int Nearest(IReadOnlyList<UnityEngine.Vector3> positions, UnityEngine.Vector3 target)
        {
            int best = -1;
            float bestD = float.PositiveInfinity;
            if (positions == null) return best;
            for (int i = 0; i < positions.Count; i++)
            {
                var d = positions[i] - target;
                float d2 = d.x * d.x + d.y * d.y + d.z * d.z;
                if (d2 < bestD) { bestD = d2; best = i; }
            }
            return best;
        }

        public static int IndexOf(IReadOnlyList<string> list, string site)
        {
            if (list == null) return -1;
            for (int i = 0; i < list.Count; i++) if (list[i] == site) return i;
            return -1;
        }

        /// The first card of a window of `visible` cards that keeps card `focus` in view, as close to the middle as the
        /// ends allow.
        public static int WindowStart(int count, int focus, int visible)
        {
            if (visible <= 0 || count <= visible) return 0;
            int start = focus - visible / 2;
            return Math.Max(0, Math.Min(start, count - visible));
        }

        /// A site from what someone said or an agent sent: the site id ("zabel-gymnasium"), its name ("Zabel gym"), or a
        /// word of either ("gym", "tower", "facade", "hospital model"). Searched in `candidates` (the listed sites, hidden
        /// ones included) and the built-in facade; exact id first, then the name, then the best word overlap. Null when
        /// nothing matches.
        public static string Match(IEnumerable<string> candidates, string said)
        {
            if (string.IsNullOrWhiteSpace(said)) return null;
            string q = Normalise(said);
            var all = new List<string>();
            if (candidates != null) foreach (var c in candidates) if (!string.IsNullOrEmpty(c) && !all.Contains(c)) all.Add(c);
            // The switcher's models first, then the hidden test packages: the visible one wins a tie ("facade", "test
            // facade" → the built-in, not synthetic-facade-small, which shares its name).
            var ordered = Order(all);
            foreach (var c in all) if (Hidden(c)) ordered.Add(c);
            foreach (var c in ordered) if (Normalise(c) == q) return c;
            foreach (var c in ordered) if (Normalise(Name(c)) == q) return c;
            var words = Words(q);
            if (words.Count == 0) return null;
            string best = null;
            int bestScore = 0;
            foreach (var c in ordered)
            {
                int score = Score(words, c);
                if (score > bestScore) { best = c; bestScore = score; }
            }
            return best;
        }

        static readonly HashSet<string> s_Filler = new HashSet<string> { "the", "a", "an", "model", "scene", "scan", "site", "show", "me", "of", "to", "go", "load", "open", "view" };

        static int Score(List<string> words, string site)
        {
            var own = Words(Normalise(site) + " " + Normalise(Name(site)) + (site == BuiltIn ? " facade test builtin" : ""));
            if (site == "zabel-gymnasium") own.Add("gymnasium");
            int score = 0;
            foreach (var w in words)
                foreach (var o in own)
                    if (o == w || (w.Length >= 4 && o.StartsWith(w, StringComparison.Ordinal)) || (o.Length >= 4 && w.StartsWith(o, StringComparison.Ordinal))) { score++; break; }
            return score;
        }

        static string Normalise(string s)
        {
            var chars = (s ?? "").Trim().ToLowerInvariant().ToCharArray();
            for (int i = 0; i < chars.Length; i++) if (!char.IsLetterOrDigit(chars[i])) chars[i] = ' ';
            return string.Join(" ", new string(chars).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        static List<string> Words(string normalised)
        {
            var list = new List<string>();
            foreach (var w in normalised.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (!s_Filler.Contains(w) && !list.Contains(w)) list.Add(w);
            return list;
        }
    }
}
