using System.Collections.Generic;
using System.IO;
using AirTools.Core;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Parts
{
    /// catalog: the Catalog window — the parts window (PartsMenu, the left side slot) rebuilt as the place's catalog. It
    /// opens from the ring's Catalog item, voice (show_catalog, "find a dishwasher"), a search, or the tape's "Find parts
    /// for …" pill; no tape is needed. Across the top the place's categories (from the laptop's GET /catalog for the loaded
    /// site, kept on the headset per site, else the built-in table over the shipped parts); a grid of cards (photo, name,
    /// price and width, a 3D badge); tap a card to take the part (PartsBrowser.Select → the usual spec card, place and fit
    /// check). The search field opens the glass keyboard: the loaded items filter at once as you type, the laptop's
    /// GET /catalog/search joins 0.5 s after the last key (stale answers dropped), and Search (Enter) runs the live store
    /// search (/parts/search, with the tape when Fits is on). Talk (the header) goes to the agent; the field is the keyboard's
    /// alone (user 09-27): a spoken query (show_catalog) searches without being written there (CatalogModel.Spoken).
    /// A tape or open gap adds the optional "Fits 2′ 4⅝″" chip. State and words are CatalogModel (pure); this renders it
    /// and does the I/O. No per-frame allocation: it renders when the model's Version moves.
    public class CatalogWindow : MonoBehaviour
    {
        [Header("Wiring")]
        public PartsBrowser browser;
        public CatalogClient client;
        public PartsClient parts;
        [Tooltip("The shipped parts: the built-in catalog when the laptop and the headset's copy have none.")]
        public PartCatalog shipped;
        public CatalogKeyboard keyboard;
        [Tooltip("gaze-catalog: works out what the wearer looks at (roof, wall, counter…); the catalog follows it.")]
        public GazeFocusTracker gaze;

        [Header("View (built by CatalogBuilder)")]
        public TextMeshPro title;
        public GlassButton field;
        public TextMeshPro fieldText;
        public GlassButton[] chips = new GlassButton[0];
        public CatalogCardView[] cards = new CatalogCardView[0];
        public GlassButton previous, next;
        public TextMeshPro pageText;
        public GlassButton fits;
        public TextMeshPro status;
        public GlassButton emptyButton;
        public GlassButton close;
        [Tooltip("gaze-catalog: \"Looking at: the roof ⌄\" — tap to pin / unpin the focus.")]
        public GlassButton focusChip;
        [Tooltip("The status line's left x when the Fits chip is showing / hidden, and its right edge (content space).")]
        public float statusXWithFits, statusXAlone, statusRight;

        [Header("Behaviour")]
        [Tooltip("Seconds after the last key before the laptop is asked (lazy search).")]
        public float debounceSeconds = 0.5f;
        [Tooltip("Ask the laptop again on opening when the catalog shown isn't its latest and this long has passed (s).")]
        public float refetchSeconds = 30f;
        public float caretSeconds = 0.5f;
        [Tooltip("Product photos kept in memory (the grid's pages).")]
        public int imageCache = 24;
        [Tooltip("gaze-catalog: seconds the chips and cards take to settle in when the focus brings another list.")]
        public float settleSeconds = 0.32f;

        CatalogModel m_Model;
        public CatalogModel Model => m_Model ??= new CatalogModel(debounceSeconds);

        /// Open and shown (in the world, its slot not yielded).
        public bool IsOpen => Model.IsOpen && browser != null && browser.Revealed;
        public bool Showing => IsOpen && browser.content != null && browser.content.activeInHierarchy;
        public int Fetches { get; private set; }
        /// GET /catalog calls answered (or failed) for the site shown.
        public int FetchesAnswered { get; private set; }
        public string LastFetchError { get; private set; }
        /// Server searches answered and used / dropped as stale (the harness reads them).
        public int ServerReplies { get; private set; }
        public int StaleReplies => Model.Debounce.Dropped;

        static float Now => Time.realtimeSinceStartup;

        AirTools.Scene.SceneRoot m_Root;
        AirTools.Scene.SceneParts m_Parts;
        int m_Rendered = -1;
        float m_LastFetchAt = -999f, m_CaretAt;
        bool m_FetchInFlight, m_CaretOn, m_BrowserOwnsStatus, m_FitDirty = true;
        string m_FetchSite, m_FieldPlain, m_FieldCaret;
        readonly Dictionary<string, Texture2D> m_Images = new Dictionary<string, Texture2D>();
        readonly List<string> m_ImageOrder = new List<string>();
        readonly HashSet<string> m_ImagePending = new HashSet<string>();
        readonly HashSet<string> m_ImageMissing = new HashSet<string>();
        // gaze-catalog: every (site, focus) list seen this session, for instant switches (the disk has them across runs).
        readonly Dictionary<string, (CatalogResponse data, CatalogSource source)> m_ByFocus = new Dictionary<string, (CatalogResponse, CatalogSource)>();
        bool m_FocusStale;
        int m_SettleSeen;
        float m_SettleAt = -1f;

        void OnEnable()
        {
            Services.Register(this);
            AirTools.Notes.Notebook.Added += OnNote;
            AirTools.Notes.Notebook.Removed += OnNote;
            AirTools.Notes.Notebook.Updated += OnNote;
            AirTools.Tools.EditHistory.Changed += OnEdit;
            m_FitDirty = true;
        }

        void OnDisable()
        {
            Services.Unregister(this);
            AirTools.Notes.Notebook.Added -= OnNote;
            AirTools.Notes.Notebook.Removed -= OnNote;
            AirTools.Notes.Notebook.Updated -= OnNote;
            AirTools.Tools.EditHistory.Changed -= OnEdit;
            if (m_Root != null) { m_Root.ContentChanged -= OnContentChanged; m_Root.Rescaled -= OnRescaled; }
            m_Root = null;
            if (m_Parts != null) m_Parts.Changed -= OnEdit;
            m_Parts = null;
        }

        // The Fits chip follows the tape and the gap: re-read them after a notebook change, an edit (undo takes a tape
        // off), a part taken out or put back, or a new scale — never polled per frame.
        void OnNote(AirTools.Notes.NotebookEntry e) => m_FitDirty = true;
        void OnEdit() => m_FitDirty = true;
        void OnRescaled(float factor) => m_FitDirty = true;

        void Start()
        {
            Model.LengthUnits = UiSettings.UnitSystem;
            BindRoot();
            SyncSite();
            Render();
        }

        void BindRoot()
        {
            if (m_Parts == null && Services.TryGet(out m_Parts)) m_Parts.Changed += OnEdit;
            if (m_Root != null || !Services.TryGet(out m_Root)) return;
            m_Root.ContentChanged += OnContentChanged;
            m_Root.Rescaled += OnRescaled;
        }

        /// A model switch or LoadSite put another site in: its catalog (the saved copy at once, then the laptop's).
        void OnContentChanged() => SyncSite();

        void Update()
        {
            if (m_Root == null || m_Parts == null) BindRoot();
            float now = Now;
            if (Model.TakeDueSearch(now, out int gen, out string q)) AskLaptop(gen, q);
            if (m_FitDirty && Model.IsOpen) UpdateFit();
            if (Model.KeyboardOpen && now - m_CaretAt >= caretSeconds)
            {
                m_CaretAt = now;
                m_CaretOn = !m_CaretOn;
                if (fieldText != null) fieldText.text = m_CaretOn ? m_FieldCaret : m_FieldPlain;
            }
            if (Model.Version != m_Rendered) Render();
            if (Model.Refocused != m_SettleSeen) { m_SettleSeen = Model.Refocused; m_SettleAt = now; }   // gaze-catalog
            if (m_SettleAt >= 0f) Settle(now);
        }

        // ---------------- the site's catalog ----------------

        /// The loaded site as GET /catalog names it: the package's site, else the built-in scene's ("synthetic-facade").
        public static string SiteOf(AirTools.Scene.SceneRoot root)
        {
            if (root == null) return "built-in";
            if (root.IsRuntimePackage && !string.IsNullOrEmpty(root.Site)) return root.Site;
            return !string.IsNullOrEmpty(root.Site) ? root.Site : "built-in";
        }

        public string Site => Model.Site;

        void SyncSite()
        {
            var root = m_Root != null ? m_Root : Services.Get<AirTools.Scene.SceneRoot>();
            string site = SiteOf(root);
            if (!Model.SetSite(site) && Model.Data != null) return;
            Log.Info($"Catalog: site {site}");
            m_FocusStale = false;
            if (TryLoadCache(site, out var saved)) Model.SetData(site, saved, CatalogSource.Cache);
            else Model.SetData(site, BuiltIn(site), CatalogSource.BuiltIn);
            m_ByFocus[CatalogText.FocusKey(site, CatalogFocus.None)] = (Model.Data, Model.Source);   // gaze-catalog
            if (root != null && root.Content == null) return;   // nothing loaded yet: the scene's own site asks when it arrives
            Fetch(site);
        }

        /// The built-in catalog for `site` over the shipped parts.
        public CatalogResponse BuiltIn(string site)
        {
            var local = new List<CatalogFallback.LocalPart>();
            if (shipped != null)
                foreach (var e in shipped.entries)
                    if (e != null && shipped.Spec(e.id) is PartSpec spec) local.Add(new CatalogFallback.LocalPart(spec.ToSummary(), e.keywords));
            return CatalogFallback.Build(site, CatalogFallback.KindOf(site), local);
        }

        /// Ask the laptop for `site`'s catalog (one at a time unless `force`); on an answer it replaces what's shown and is
        /// kept on the headset for next time. gaze-catalog: for what the wearer looks at (Model.RequestFocus).
        public void Fetch(string site, bool force = false) => FetchFor(site, site == Model.Site ? Model.RequestFocus : CatalogFocus.None, force);

        /// gaze-catalog: GET /catalog for `site` and `focus` (one per (site, focus) at a time unless `force`). The answer
        /// is kept per (site, focus) in memory and on the headset; it replaces what's shown while it's still what the
        /// catalog wants (the gaze may have moved on meanwhile: then it's only kept).
        void FetchFor(string site, CatalogFocus focus, bool force)
        {
            var c = client != null ? client : Services.Get<CatalogClient>();
            string key = CatalogText.FocusKey(site, focus);
            if (c == null || (!force && m_FetchInFlight && m_FetchSite == key)) return;
            m_FetchInFlight = true;
            m_FetchSite = key;
            m_LastFetchAt = Now;
            Fetches++;
            c.Fetch(site, focus, (json, error) =>
            {
                if (m_FetchSite == key) m_FetchInFlight = false;
                if (site != Model.Site) return;   // the site changed meanwhile
                FetchesAnswered++;
                var data = json != null ? CatalogJson.Parse(json) : null;
                string wire = CatalogFoci.Wire(focus);
                if (data == null || data.categories.Count == 0)
                {
                    LastFetchError = error ?? (json == null ? "no answer" : "no categories");
                    Log.Warn($"Catalog: GET /catalog?site={site}{(wire != null ? "&focus=" + wire : "")} → {LastFetchError}; showing the {(Model.Source == CatalogSource.Cache ? "saved" : Model.Source == CatalogSource.Server ? "last" : "built-in")} catalog");
                    return;
                }
                LastFetchError = null;
                m_ImageMissing.Clear();   // the laptop is back: photos may be too
                var answered = CatalogFoci.Parse(data.focus);   // None: the whole place (no table for it, or an older laptop)
                m_ByFocus[CatalogText.FocusKey(site, answered)] = (data, CatalogSource.Server);
                SaveCache(site, answered, json);
                bool current = focus == Model.RequestFocus || Model.Data == null;
                if (current) Model.SetData(site, data, CatalogSource.Server);
                LastFocusAnswer = answered;
                Log.Info($"Catalog: {site} ({data.environment ?? "-"}{(wire != null ? ", looking at " + (data.focus ?? "- (not answered)") : "")}) {data.categories.Count} categories, {data.ItemCount} items from the laptop{(current ? "" : " (kept: the focus moved on)")}");
                // The foci just arrived (a first answer for the whole place) or the gaze moved on meanwhile: follow. Never
                // ask again for the focus just answered (a laptop that didn't apply it would be asked forever).
                if (Model.RequestFocus != focus) Refocus();
            });
        }

        static string CacheDir => Path.Combine(Application.persistentDataPath, "catalog");

        static bool TryLoadCache(string site, out CatalogResponse data) => TryLoadCache(site, CatalogFocus.None, out data);

        static bool TryLoadCache(string site, CatalogFocus focus, out CatalogResponse data)
        {
            data = null;
            try
            {
                string path = Path.Combine(CacheDir, CatalogText.CacheFile(site, focus));
                if (!File.Exists(path)) return false;
                data = CatalogJson.Parse(File.ReadAllText(path));
                return data != null && data.categories.Count > 0;
            }
            catch (System.Exception ex) { Log.Warn($"Catalog: saved copy for {site} unreadable: {ex.Message}"); return false; }
        }

        static void SaveCache(string site, CatalogFocus focus, string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            AirTools.Scene.SceneCache.WriteAtomic(Path.Combine(CacheDir, CatalogText.CacheFile(site, focus)), System.Text.Encoding.UTF8.GetBytes(json));
        }

        // ---------------- open / close ----------------

        /// Open in the left side slot. `fromTape`: from the tape's pill (Fits starts on). No tape needed.
        public void Open(bool fromTape = false)
        {
            BindRoot();
            SyncSite();
            UpdateFit();
            Model.LengthUnits = UiSettings.UnitSystem;
            Model.Open(fromTape);
            m_BrowserOwnsStatus = false;
            if (browser != null) browser.Show();
            if (Model.Source != CatalogSource.Server && (LastFetchError != null || Now - m_LastFetchAt > refetchSeconds)) Fetch(Model.Site);
            if (m_FocusStale || Model.RequestFocus != Model.ShownFocus) Refocus();   // gaze-catalog: what you looked at before opening
            Render();
            Log.Info($"Catalog open: {Model.Site} · {Model.Environment} · {Model.CategoryCount} categories ({Model.Source}){(Model.FitsOn ? " · " + Model.Fit.Label(Model.LengthUnits) : "")}");
        }

        public void Hide()
        {
            Model.Close();
            if (browser != null) browser.Hide();
            Render();
        }

        // ---------------- input (CatalogButton) ----------------

        public void ToggleKeyboard()
        {
            Model.SetKeyboard(!Model.KeyboardOpen);
            m_BrowserOwnsStatus = false;
        }

        /// One key of the glass keyboard.
        public void Key(string key)
        {
            var r = Model.Key(key, Now);
            m_BrowserOwnsStatus = false;
            if (r == CatalogKeyResult.Submitted) SearchStores();
        }

        public void PressChip(int slot)
        {
            Model.PressChip(slot);
            m_BrowserOwnsStatus = false;
        }

        public void Page(int delta)
        {
            if (delta > 0) Model.NextPage(); else Model.PreviousPage();
        }

        public void ToggleFits()
        {
            UpdateFit();
            Model.ToggleFits();
            m_BrowserOwnsStatus = false;
        }

        /// The live store search (the existing /parts/search job): the text typed, else the category's store query.
        /// With Fits on, the tape / gap goes along. The results come back into the grid as Results.
        public bool SearchStores()
        {
            string q = Model.Mode == CatalogMode.Search && Model.Query.Length > 0 ? Model.Query : Model.Mode == CatalogMode.Browse ? Model.CurrentCategory?.Query : Model.Query;
            Model.SetKeyboard(false);
            if (string.IsNullOrWhiteSpace(q) || browser == null) return false;
            Log.Info($"Catalog: search the stores for \"{q}\"{(Model.FitsOn ? " with " + Model.Fit : " (no size)")}");
            browser.Search(q, Model.FitsOn);
            return true;
        }

        /// Take the part on card `slot` into the hand (the parts flow: spec card, place, fit check). The list shown becomes
        /// the parts window's candidates, so "next one" steps through it.
        public bool Pick(int slot)
        {
            if (browser == null || !Model.Pick(slot, out var hit, out int index, out string label)) return false;
            var list = new List<PartSummary>(Model.Visible.Count);
            foreach (var h in Model.Visible) list.Add(h.Summary);
            if (!SameIds(browser.Candidates, list))
                browser.Adopt(list, label, Model.Mode == CatalogMode.Results ? browser.Source : "catalog " + Model.Source.ToString().ToLowerInvariant());
            bool ok = browser.Select(index, edit: true);   // edit6dof: into the Edit view first (EditView.OpenOnTake)
            if (!ok) return false;
            Model.Picked = hit.Id;
            Model.SetKeyboard(false);
            m_BrowserOwnsStatus = true;
            Log.Info($"Catalog: took {hit.Id} (card {slot}, #{index} of {list.Count}, {Model.Mode})");
            Render();
            return true;
        }

        static bool SameIds(List<PartSummary> a, List<PartSummary> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i]?.id != b[i]?.id) return false;
            return true;
        }

        // ---------------- from the parts window / voice ----------------

        /// PartsBrowser: a live search started (`searching`) or answered.
        public void OnCandidatesChanged(string query, bool searching, List<PartSummary> candidates)
        {
            if (!Model.IsOpen) { SyncSite(); UpdateFit(); Model.Open(); }
            Model.ShowResults(query, searching, candidates);
            m_BrowserOwnsStatus = true;
        }

        /// PartsBrowser: this window's Talk is listening / thinking: its line holds the status until the next change here.
        public void OnVoiceLine() => m_BrowserOwnsStatus = true;

        /// show_catalog / the harness: a category by name and / or a query typed in.
        public void Show(string category, string query, bool fromTape = false)
        {
            Open(fromTape);
            if (!string.IsNullOrWhiteSpace(category))
            {
                int i = Model.FindCategory(category);
                if (i >= 0) Model.SelectCategory(i);
                else if (string.IsNullOrWhiteSpace(query)) query = category;
            }
            if (!string.IsNullOrWhiteSpace(query)) Model.SetSpokenQuery(query, Now);   // voice: searched, not typed into the field
            Render();
        }

        /// D7 reset: first-run state (closed, first category, no text, no results).
        public void ResetSession()
        {
            Model.ResetSession();
            m_BrowserOwnsStatus = false;
            // gaze-catalog: unpinned, following what the gaze holds now (the list follows on opening).
            if (gaze != null && Model.SetGazeFocus(gaze.Filter.Held, gaze.Filter.Subject)) m_FocusStale = true;
            Render();
        }

        /// D2: the unit changed.
        public void Relabel()
        {
            Model.LengthUnits = UiSettings.UnitSystem;
            foreach (var c in cards) if (c != null) c.Relabel(Model.LengthUnits);
            Model.Refresh();
        }

        // ---------------- lazy search: the laptop's half ----------------

        void AskLaptop(int generation, string q)
        {
            var c = client != null ? client : Services.Get<CatalogClient>();
            if (c == null) { Model.OnServerReply(generation, null); return; }
            c.Search(q, Model.Site, CatalogModel.ServerLimit, (json, error) =>
            {
                var reply = json != null ? CatalogJson.ParseSearch(json) : null;
                if (Model.OnServerReply(generation, reply)) ServerReplies++;
                else Log.Info($"Catalog: dropped a stale answer for \"{q}\" (gen {generation}, now {Model.Debounce.Generation})");
            });
        }

        // ---------------- the tape / gap ----------------

        void UpdateFit()
        {
            m_FitDirty = false;
            var fit = CatalogFit.None;
            if (AirTools.Scene.Gaps.TryGet(out var gap))
            {
                var s = AirTools.Scene.Gaps.FitSizeM(gap);
                fit = CatalogFit.Gap(s.x, s.y, s.z);
            }
            else
            {
                var m = PartsClient.MeasurementContext();
                if (m != null && m.TryGetValue("value_m", out var v) && v is double d)
                    fit = CatalogFit.Tape(d, m.TryGetValue("axis", out var a) ? a as string : "w");
            }
            Model.SetFit(fit);
        }

        // ---------------- gaze-catalog: the catalog follows what you look at ----------------

        /// The focus the laptop's last answer was for (None: the whole place).
        public CatalogFocus LastFocusAnswer { get; private set; }

        /// GazeFocusTracker: the held focus changed (already smoothed: ~1.2 s on a surface). The list follows unless pinned:
        /// the headset's copy of that (site, focus) at once, the laptop's answer after (only on a change, never per frame).
        /// Closed: it follows when it opens.
        public void OnGazeFocus(CatalogFocus focus, string subject)
        {
            if (!Model.SetGazeFocus(focus, subject) || !Model.FocusSupported) return;   // an older laptop: the whole place
            Log.Info($"Catalog: looking at {CatalogFoci.Wire(focus) ?? "nothing in particular"}{(subject != null ? $" ({subject})" : "")}{(Model.IsOpen ? "" : " (closed: on opening)")}");
            if (Model.IsOpen) Refocus(); else m_FocusStale = true;
        }

        /// The header chip: pin the focus shown (stop following the gaze) or unpin (catch up with the gaze).
        public void ToggleFocusPin()
        {
            bool follow = Model.TogglePin();
            m_BrowserOwnsStatus = false;
            Log.Info($"Catalog: focus {(Model.FocusPinned ? "pinned on" : "follows the gaze again:")} {CatalogFoci.Wire(Model.Focus) ?? "everything"}");
            if (follow) Refocus();
            Render();
        }

        /// Show the list for Model.RequestFocus: the copy kept on the headset at once (no flash: the chips and cards take
        /// the new words and settle in), and ask the laptop (while open).
        void Refocus()
        {
            string site = Model.Site;
            if (site == null) return;
            m_FocusStale = false;
            var f = Model.RequestFocus;
            if (TryFocusData(site, f, out var data, out var source) && !ReferenceEquals(data, Model.Data)) Model.SetData(site, data, source);
            if (Model.IsOpen) FetchFor(site, f, false);
            else m_FocusStale = true;
        }

        bool TryFocusData(string site, CatalogFocus focus, out CatalogResponse data, out CatalogSource source)
        {
            string key = CatalogText.FocusKey(site, focus);
            if (m_ByFocus.TryGetValue(key, out var kept)) { data = kept.data; source = kept.source; return data != null; }
            source = CatalogSource.Cache;
            if (!TryLoadCache(site, focus, out data)) return false;
            m_ByFocus[key] = (data, source);
            return true;
        }

        /// The chips, then the cards, grow back from 92 % one after another (ease-out) after a refocus: the list changes
        /// in place, gently. No allocation.
        void Settle(float now)
        {
            float d = Mathf.Max(0.05f, settleSeconds);
            bool done = true;
            for (int i = 0; i < chips.Length; i++) done &= SettleOne(chips[i] != null ? chips[i].transform : null, (now - m_SettleAt - i * 0.03f) / d);
            for (int i = 0; i < cards.Length; i++) done &= SettleOne(cards[i] != null ? cards[i].transform : null, (now - m_SettleAt - 0.1f - i * 0.04f) / d);
            if (done) m_SettleAt = -1f;
        }

        static bool SettleOne(Transform t, float k)
        {
            if (t == null) return true;
            k = Mathf.Clamp01(k);
            float e = 1f - (1f - k) * (1f - k) * (1f - k);
            float s = Mathf.Lerp(0.92f, 1f, e);
            if (Mathf.Abs(t.localScale.x - s) > 1e-4f) t.localScale = new Vector3(s, s, s);
            return k >= 1f;
        }

        // ---------------- render ----------------

        void Render()
        {
            var m = Model;
            m_Rendered = m.Version;
            var colors = UiTheme.Current.colors;
            if (title != null)
            {
                string sec = ColorUtility.ToHtmlStringRGBA(colors.textSecondary);
                title.text = $"Catalog <size=70%><color=#{sec}>{Copy.Clean(m.Environment)}</color></size>";
            }
            RenderField(colors);
            for (int i = 0; i < chips.Length; i++)
            {
                var b = chips[i];
                if (b == null) continue;
                bool on = m.ChipAt(i, out _) != CatalogChip.None;
                if (b.gameObject.activeSelf != on) b.gameObject.SetActive(on);
                if (!on) continue;
                string label = m.ChipLabel(i);
                if (b.Text != label) b.SetText(label);
                b.SetSelected(m.ChipSelected(i));
            }
            for (int i = 0; i < cards.Length; i++)
            {
                var card = cards[i];
                if (card == null) continue;
                var hit = m.PageItem(i);
                card.Show(hit, m.LengthUnits);
                if (hit == null) continue;
                card.SetSelected(hit.Id != null && hit.Id == m.Picked);
                if (!card.HasImage) RequestImage(card, hit);
            }
            bool pages = m.PageCount > 1;
            if (previous != null) { if (previous.gameObject.activeSelf != pages) previous.gameObject.SetActive(pages); previous.SetInteractable(m.Page > 0); }
            if (next != null) { if (next.gameObject.activeSelf != pages) next.gameObject.SetActive(pages); next.SetInteractable(m.Page + 1 < m.PageCount); }
            if (pageText != null)
            {
                if (pageText.gameObject.activeSelf != pages) pageText.gameObject.SetActive(pages);
                if (pages) pageText.text = UiText.Tabular($"{m.Page + 1} / {m.PageCount}");
            }
            bool fitChip = m.Fit.Available;
            if (fits != null)
            {
                if (fits.gameObject.activeSelf != fitChip) fits.gameObject.SetActive(fitChip);
                if (fitChip)
                {
                    string label = m.Fit.Label(m.LengthUnits);
                    if (fits.Text != label) fits.SetText(label);
                    fits.SetSelected(m.FitsOn);
                }
            }
            if (status != null)
            {
                var p = status.transform.localPosition;
                float x = fitChip ? statusXWithFits : statusXAlone;
                if (Mathf.Abs(p.x - x) > 1e-5f)
                {
                    status.transform.localPosition = new Vector3(x, p.y, p.z);
                    if (statusRight > x) status.rectTransform.sizeDelta = new Vector2(statusRight - x, status.rectTransform.sizeDelta.y);
                }
                string s = m.Status();
                if (s != null && !m_BrowserOwnsStatus && status.text != s) status.text = s;
            }
            if (focusChip != null)   // gaze-catalog
            {
                string label = m.FocusChipLabel;
                if (focusChip.Text != label) focusChip.SetText(label);
                focusChip.SetSelected(m.FocusPinned);
                focusChip.SetInteractable(m.FocusSupported);
            }
            if (emptyButton != null)
            {
                string e = m.EmptyAction;
                bool on = e != null;
                if (emptyButton.gameObject.activeSelf != on) emptyButton.gameObject.SetActive(on);
                if (on && emptyButton.Text != e) emptyButton.SetText(e);
            }
            if (keyboard != null) keyboard.Show(m.KeyboardOpen && m.IsOpen);
        }

        void RenderField(UiTheme.Colors colors)
        {
            var m = Model;
            if (field != null) field.SetSelected(m.KeyboardOpen);
            if (fieldText == null) return;
            bool empty = m.Field.Text.Length == 0;
            string plain = empty ? (m.KeyboardOpen ? "" : $"Search {Copy.Clean(m.Environment).ToLowerInvariant()}…") : m.Field.Text;
            if (plain != m_FieldPlain)
            {
                m_FieldPlain = plain;
                m_FieldCaret = plain + "|";
            }
            fieldText.text = m.KeyboardOpen && m_CaretOn ? m_FieldCaret : m_FieldPlain;
            fieldText.color = m.KeyboardOpen ? colors.onInk : empty ? colors.textSecondary : colors.textPrimary;
        }

        // ---------------- photos ----------------

        void RequestImage(CatalogCardView card, CatalogHit hit)
        {
            string id = hit.Id;
            if (id == null) return;
            if (m_Images.TryGetValue(id, out var cached))
            {
                card.SetImage(cached);
                Touch(id);
                return;
            }
            var c = parts != null ? parts : Services.Get<PartsClient>();
            if (c == null || !Application.isPlaying || m_ImageMissing.Contains(id) || !m_ImagePending.Add(id)) return;
            c.GetImage(hit.Summary, tex =>
            {
                m_ImagePending.Remove(id);
                if (tex == null) { m_ImageMissing.Add(id); return; }   // no photo anywhere: don't ask on every render
                Keep(id, tex);
                foreach (var k in cards) if (k != null && k.ItemId == id) k.SetImage(tex);
            });
        }

        void Touch(string id)
        {
            m_ImageOrder.Remove(id);
            m_ImageOrder.Add(id);
        }

        /// Keep a photo; past `imageCache`, forget the oldest one no card shows (downloaded ones are destroyed; the
        /// shipped parts' pictures are assets and stay).
        void Keep(string id, Texture2D tex)
        {
            m_Images[id] = tex;
            Touch(id);
            for (int i = 0; m_Images.Count > Mathf.Max(6, imageCache) && i < m_ImageOrder.Count; i++)
            {
                string old = m_ImageOrder[i];
                bool shown = false;
                foreach (var k in cards) if (k != null && k.ItemId == old) { shown = true; break; }
                if (shown) continue;
                var t = m_Images[old];
                m_Images.Remove(old);
                m_ImageOrder.RemoveAt(i);
                i--;
                bool asset = shipped != null && shipped.Find(old) is PartCatalog.Entry e && e.image == t;
                if (t != null && !asset) Destroy(t);
            }
        }
    }
}
