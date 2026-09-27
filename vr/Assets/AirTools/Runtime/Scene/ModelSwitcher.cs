using System.Collections.Generic;
using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Scene
{
    /// Model view's switcher (modelview): which models there are and which one is on view. modelwheel: the view is the
    /// endless wheel under the model (ModelWheel, on the same object); this is the logic behind it, and behind the
    /// show_model / next_model actions and the voice. Each model is the scan's photo from near its recommended spawn and a
    /// short name (ModelSites.Name: Kitchen, Zabel gym, Hospital, GT canopy, …, Test facade). Choosing one puts it on view
    /// (SceneStreamer.Load / LoadBuiltIn, then TabletopController re-fits it); Step goes to the previous / next model; Walk
    /// in steps into the model on view (at its recommended spawn when you switched to it here; otherwise back where you
    /// were, like the "You are here" pin).
    /// - While a model loads the old one stays until the swap; its card says "Loading…" (its picture dimmed) and every
    ///   other card still answers: a tap meanwhile is "Up next" and loads when this one is done (the last tap wins).
    /// - A failed load says "Retry" on its card (tap it again) and flashes why on the status line.
    /// - The site is global: the world you step into (Walk in, the pin, the ring's Model view, Settings ▸ Home) is the one
    ///   chosen here.
    /// Shown only in Model view once it has settled (not during the grow / shrink).
    /// scalemodels: the row never goes empty when the laptop is away.
    /// - **At start** it shows the models the headset knows (SceneStreamer.KnownSites: the last good listing, kept on
    ///   the headset, and the cached packages), then refreshes when the server answers.
    /// - **Offline,** a model that isn't on the headset shows "Not downloaded": its picture dimmed, a badge over it, and
    ///   ← → skip it. A tap says "Connect the laptop to download it".
    /// - ScenePrefetcher downloads every listed model in the background while the laptop is there.
    public class ModelSwitcher : MonoBehaviour
    {
        public TabletopController tabletop;
        public ModeController mode;
        public Transform head;
        [Tooltip("Seconds between listing refreshes while shown (GET /scenes).")]
        public float listSeconds = 20f;

        /// Model view is up and settled (the wheel shows).
        public bool Shown { get; private set; }
        /// modelwheel: the wheel's visuals (the old row's `content`; the glass lane's GlassLook("model") looks at it).
        public GameObject content
        {
            get { var w = GetComponent<ModelWheel>(); return w != null ? w.content : null; }
        }
        /// The switcher's models, in order (ModelSites.Order over the server's listing, then the built-in facade).
        public IReadOnlyList<string> Sites => m_Sites;
        /// modelwheel: bumped whenever the list is replaced (the wheel re-reads it).
        public int SitesVersion { get; private set; }
        /// modelwheel: the model the view should point at: the one waiting, else the one loading, else the one on view.
        public string Focus => m_State.Want ?? m_State.Loading ?? Current;
        /// The model on the table now ("built-in" for the facade baked into the app).
        public string Current
        {
            get
            {
                var root = tabletop != null ? tabletop.root : null;
                return root != null ? ModelSites.Current(root.IsRuntimePackage, root.Site) : ModelSites.BuiltIn;
            }
        }
        /// The model loading now, the one waiting for it, and the ones that failed (their cards say Retry).
        public string Loading => m_State.Loading;
        public string Queued => m_State.Want;
        public IReadOnlyCollection<string> Failed => m_State.Failed;
        public string LastAction { get; private set; } = "";
        public int Loads => m_State.Loads;
        public int Failures => m_State.Failures;
        /// The last load's result ("kitchen ok 3.2 s", "gt-lcc-tower failed: …").
        public string LastResult { get; private set; } = "";
        public bool Listed { get; private set; }

        readonly List<string> m_Sites = new List<string> { ModelSites.BuiltIn };
        readonly ModelSwitchState m_State = new ModelSwitchState();
        System.Func<string, bool> m_Start;
        float m_NextList;
        bool m_WalkInWhenReady;
        bool m_Listing;

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);

        /// Step into the model on the table: a model switched to here lands you at its recommended spawn (the one you came
        /// from, where you were); while a model is loading (or waiting), once it's on the table.
        public void WalkIn()
        {
            if (m_State.Loading != null || m_State.Want != null || (tabletop != null && tabletop.Refitting))
            {
                m_WalkInWhenReady = true;
                LastAction = "walk in once it's on the table";
                string next = m_State.Loading ?? m_State.Want;
                if (next != null) UiToast.Show($"Walking in once the {Copy.SiteName(next)} is in", ColorRole.Info);
                return;
            }
            m_WalkInWhenReady = false;
            LastAction = "walk in";
            Log.Info($"Model view: walk in ({Current})");
            AppCommands.StepIn();
        }

        /// Put `site` on the table: now, or once the model loading now is in (the last choice wins). A failed site is
        /// tried again. Already there: nothing to do. True when it is (or will be) shown.
        public bool Choose(string site)
        {
            if (string.IsNullOrEmpty(site)) return false;
            // scalemodels: offline, a model that isn't on the headset can't open.
            if (!Available(site) && site != Current && site != m_State.Loading)
            {
                LastAction = $"not downloaded: {site}";
                Log.Info($"Model view: {site} isn't on the headset and the laptop is away");
                UiToast.Show($"The {Copy.SiteName(site)} isn't on the headset · connect the laptop to download it", ColorRole.Warning);
                return false;
            }
            LastAction = m_State.Choose(site, Current);
            Log.Info($"Model view: {LastAction}");
            Pump();
            return true;
        }

        /// show_model: a site id or what someone said ("the gym", "GT tower", "facade"), matched over the server's listing
        /// (hidden test packages too) and the built-in facade. With no listing yet it asks for one and chooses when it
        /// comes. False when nothing matches.
        public bool ShowNamed(string said)
        {
            Services.TryGet<SceneStreamer>(out var streamer);
            string site = Resolve(said, streamer);
            if (site != null) return Choose(site);
            if (streamer == null || streamer.Sites.Count > 0)
            {
                LastAction = $"no model called \"{said}\"";
                UiToast.Show($"No model called “{Copy.Clip(said, 30)}”", ColorRole.Warning);
                return false;
            }
            LastAction = $"listing to find \"{said}\"";
            streamer.ListSites(list =>
            {
                var ids = new List<string>(list.Count);
                foreach (var s in list) ids.Add(s.site);
                foreach (var s in streamer.KnownSites) if (!ids.Contains(s.site)) ids.Add(s.site);   // scalemodels: offline too
                ApplyKnown(streamer);
                Listed = true;
                string found = ModelSites.Match(ids, said);
                if (found != null) Choose(found);
                else { LastAction = $"no model called \"{said}\""; UiToast.Show($"No model called “{Copy.Clip(said, 30)}”", ColorRole.Warning); }
            });
            return true;
        }

        static string Resolve(string said, SceneStreamer streamer)
        {
            var ids = new List<string>();
            if (streamer != null) foreach (var s in streamer.Sites) ids.Add(s.site);
            if (streamer != null) foreach (var s in streamer.KnownSites) if (!ids.Contains(s.site)) ids.Add(s.site);   // scalemodels: offline too
            return ModelSites.Match(ids, said);
        }

        /// The model before (−1) or after (+1) the one on the table (or the one being loaded), wrapping.
        public string Step(int step)
        {
            string from = m_State.Want ?? m_State.Loading ?? Current;
            m_Available ??= Available;
            string to = ModelSites.Step(m_Sites, from, step, m_Available);   // scalemodels: offline, only what's on the headset
            if (to != null) Choose(to);
            return to;
        }

        /// Ask the server for its models again (GET /scenes); the row updates when it answers.
        public void RefreshList()
        {
            if (m_Listing || !Services.TryGet<SceneStreamer>(out var streamer)) return;
            m_Listing = true;
            m_NextList = Time.unscaledTime + listSeconds;
            streamer.ListSites(list =>
            {
                m_Listing = false;
                // scalemodels: the server's models ∪ what's on the headset (answered), or the kept listing (offline).
                ApplyKnown(streamer);
                Listed = true;
            });
        }

        // ---------------- scalemodels: the models on the headset ----------------

        int m_KnownVersion = -1;
        System.Func<string, bool> m_Available;

        /// The row's models from what the headset knows (SceneStreamer.KnownSites).
        void ApplyKnown(SceneStreamer streamer)
        {
            m_KnownVersion = streamer.KnownVersion;
            var ids = new List<string>(streamer.KnownSites.Count);
            foreach (var s in streamer.KnownSites) ids.Add(s.site);
            SetSites(ids);
        }

        /// The laptop answered the last listing (SceneStreamer.ServerListed).
        public bool Online => Services.TryGet<SceneStreamer>(out var s) && s.ServerListed;

        /// Can `site` open now: the built-in facade always; with the laptop there anything listed; with it away only what's
        /// on the headset.
        public bool Available(string site) => IsAvailable(Services.TryGet<SceneStreamer>(out var s) ? s : null, site);

        /// ModelSites.Available without a delegate (the cards refresh ten times a second).
        public static bool IsAvailable(SceneStreamer streamer, string site) =>
            streamer == null || site == ModelSites.BuiltIn || streamer.ServerListed || streamer.OnHeadset(site);
        // end scalemodels

        /// modelwheel: a model's card state now (on view, loading, up next, failed, not downloaded, idle). No allocation.
        public ModelCardState StateOf(string site, SceneStreamer streamer) =>
            ModelSites.StateOf(site, Current, m_State.Loading, m_State.Want, m_State.Failed, IsAvailable(streamer, site));

        /// modelwheel: leave Model view for the room (the wheel's Exit): the model scans out, passthrough stays.
        public void Exit()
        {
            m_WalkInWhenReady = false;
            LastAction = "exit";
            Log.Info("Model view: exit to the room");
            AppCommands.CloseChest();
        }

        /// The row's models from a listing (ModelSites.Order: the hidden test packages out, the built-in facade last).
        public void SetSites(IEnumerable<string> listed)
        {
            var order = ModelSites.Order(listed);
            if (SameAs(order)) return;   // modelwheel: an unchanged listing keeps the wheel where it is
            m_Sites.Clear();
            m_Sites.AddRange(order);
            SitesVersion++;
        }

        bool SameAs(List<string> order)
        {
            if (order.Count != m_Sites.Count) return false;
            for (int i = 0; i < order.Count; i++) if (order[i] != m_Sites[i]) return false;
            return true;
        }

        bool Settled => tabletop != null && tabletop.OnTable && AppState.Mode == AppMode.Tabletop
                        && (mode == null || (mode.VisualMode == AppMode.Tabletop && !mode.IsTransitioning));

        void Update() => Tick();

        /// One frame (Update; modelwheel: EditMode tests step it).
        public void Tick()
        {
            // scalemodels: the kept listing shows at once (before the server answers), and follows the prefetch.
            if (Services.TryGet<SceneStreamer>(out var known) && known.KnownVersion != m_KnownVersion) ApplyKnown(known);
            bool show = Settled;
            if (show != Shown)
            {
                Shown = show;
                if (show) RefreshList();
                else if (AppState.Mode != AppMode.Tabletop) m_State.Forget();   // left Model view: nothing waits for the table
            }
            Pump();
            if (!Shown) { m_WalkInWhenReady = false; return; }
            if (m_WalkInWhenReady && m_State.Loading == null && m_State.Want == null && !tabletop.Refitting) WalkIn();
            if (Time.unscaledTime >= m_NextList && !m_Listing) RefreshList();
        }

        /// The load state (ModelSwitchState): finish the running load, then start the one waiting — only with the model
        /// settled on the table, so the rig never moves and the swap happens on the table.
        void Pump()
        {
            if (!Services.TryGet<SceneStreamer>(out var streamer)) return;
            m_Start ??= StartLoad;
            var outcome = m_State.Pump(streamer.Loading, streamer.Site, Settled, Time.unscaledTime, m_Start);
            switch (outcome)
            {
                case ModelSwitchState.Outcome.Loaded:
                    LastResult = $"{m_State.LastSite} ok {m_State.LastSeconds:0.0} s";
                    Log.Info($"Model view: {m_State.LastSite} on the table ({m_State.LastSeconds:0.0} s)");
                    break;
                case ModelSwitchState.Outcome.Failed:
                    LastResult = $"{m_State.LastSite} failed: {streamer.Status}";
                    Log.Warn($"Model view: {m_State.LastSite} didn't load ({streamer.Status})");
                    UiToast.Show($"Couldn't load the {Copy.SiteName(m_State.LastSite)} · pinch it again to retry", ColorRole.Warning);
                    break;
                case ModelSwitchState.Outcome.Started:
                    Log.Info($"Model view: loading {m_State.Loading}");
                    break;
                default: return;
            }
        }

        /// Start loading a site (the built-in facade loads at once). False when the streamer is busy.
        static bool StartLoad(string site)
        {
            if (site != ModelSites.BuiltIn) return AppCommands.LoadSite(site);
            if (!Services.TryGet<SceneStreamer>(out var streamer) || streamer.Loading) return false;
            streamer.LoadBuiltIn();   // the tabletop re-fits it this frame
            return true;
        }

        /// One line for the harness: the models, the one on view, loading / queued / failed, and the wheel.
        public string Describe()
        {
            var names = new List<string>(m_Sites.Count);
            foreach (var s in m_Sites) names.Add(s == Current ? $"[{s}]" : s);
            var off = new List<string>();   // scalemodels: offline, the models that aren't on the headset
            foreach (var s in m_Sites) if (!Available(s)) off.Add(s);
            var wheel = GetComponent<ModelWheel>();   // modelwheel
            return $"shown={Shown} current={Current} loading={m_State.Loading ?? "-"} queued={m_State.Want ?? "-"} failed=[{string.Join(",", m_State.Failed)}] " +
                   $"models=[{string.Join(", ", names)}] online={Online} not_downloaded=[{string.Join(",", off)}] last=\"{LastAction}\" result=\"{LastResult}\"" +
                   (wheel != null ? $" | wheel: {wheel.Brief()}" : "");
        }
    }
}
