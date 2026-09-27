using System.Collections.Generic;
using AirTools.Core;

namespace AirTools.Parts
{
    /// catalog: what the grid shows — a category of the place (Browse), the lazy search as you type (Search), or a live
    /// store search / a spoken request (Results).
    public enum CatalogMode { Browse, Search, Results }

    /// catalog: where the loaded catalog came from.
    public enum CatalogSource { None, Server, Cache, BuiltIn }

    /// catalog: the laptop's part of the lazy search.
    public enum CatalogServerState { Idle, Waiting, Asking, Answered, Failed }

    /// catalog: what one chip slot holds.
    public enum CatalogChip { None, Results, Category, More }

    /// catalog: the catalog window's state and everything it derives (pure: CatalogWindow renders it and does the I/O).
    /// Opening needs no tape: the categories of the place are the start; a tape or gap only adds the optional Fits chip.
    /// Typing filters the loaded items at once (CatalogSearch) and asks the laptop 0.5 s after the last key
    /// (CatalogDebounce, stale replies dropped); Enter hands the text to the live store search (Results).
    public sealed class CatalogModel
    {
        public const int CardsPerPage = 6, ChipSlots = 8, ServerLimit = 20;

        // ---------------- the data ----------------

        public string Site { get; private set; }
        public CatalogResponse Data { get; private set; }
        public CatalogIndex Index { get; private set; }
        public CatalogSource Source { get; private set; }
        /// The place's name for the title ("Kitchen").
        public string Environment =>
            !string.IsNullOrWhiteSpace(Data?.title) ? Data.title.Trim() : CatalogFallback.Title(CatalogFallback.KindOf(Site, Data?.environment));
        public int CategoryCount => Data?.categories?.Count ?? 0;

        // ---------------- the view ----------------

        public bool IsOpen { get; private set; }
        public CatalogMode Mode { get; private set; }
        /// The category shown in Browse (index into Data.categories).
        public int Category { get; private set; }
        public int Page { get; private set; }
        public int ChipOffset { get; private set; }
        public bool FitsOn { get; private set; }
        public CatalogFit Fit { get; private set; }
        public bool KeyboardOpen { get; private set; }
        public readonly CatalogTextField Field = new CatalogTextField();
        /// voice: what was asked for by voice (Talk, Grok's show_catalog, "show me … in the catalog"). It searches as typing
        /// does, but the field is the keyboard's alone (user 09-27): the words show in the status line ("“fridges” · 6"),
        /// never in the search bar. A key press, typed text, a category, a site change or a reset drops it.
        public string Spoken { get; private set; }
        public string Query => Spoken ?? Field.Query;
        public readonly CatalogDebounce Debounce;
        public CatalogServerState Server { get; private set; }
        /// Items the laptop's last answer added to the local ones.
        public int ServerAdded { get; private set; }
        /// The part in hand (its card shows selected).
        public string Picked;
        /// Lengths on the Fits chip and the cards.
        public UnitSystem LengthUnits = UnitSystem.Imperial;
        /// Bumped on every change: the window re-renders when it moves.
        public int Version { get; private set; }

        // ---------------- a live search's results ----------------

        /// The query of the last live search shown here (null: none yet). "your request" for a spoken one.
        public string ResultsQuery { get; private set; }
        public bool ResultsSearching { get; private set; }
        public readonly List<CatalogHit> Results = new List<CatalogHit>();
        public bool HasResults => ResultsQuery != null;

        readonly List<CatalogHit> m_Found = new List<CatalogHit>();
        /// What the grid pages through now.
        public readonly List<CatalogHit> Visible = new List<CatalogHit>();

        public CatalogModel(float debounceSeconds = 0.5f) { Debounce = new CatalogDebounce(debounceSeconds); }

        public CatalogCategory CurrentCategory =>
            Data?.categories != null && Category >= 0 && Category < Data.categories.Count ? Data.categories[Category] : null;

        // ---------------- data in ----------------

        /// The loaded site changed (a model switch, LoadSite): its catalog comes next; the search text goes (another place).
        public bool SetSite(string site)
        {
            if (site == Site) return false;
            Site = site;
            Data = null;
            Index = null;
            Source = CatalogSource.None;
            Category = 0;
            ChipOffset = 0;
            Field.Set("");
            Spoken = null;
            Debounce.Cancel();
            Server = CatalogServerState.Idle;
            m_Found.Clear();
            if (Mode == CatalogMode.Search) Mode = CatalogMode.Browse;
            ResetFocus();   // gaze-catalog: another place, nothing looked at yet
            Refresh();
            return true;
        }

        /// A catalog for `site` arrived (the laptop, the headset's copy or the built-in table). Ignored for another site.
        /// The category shown stays (by id) and a search being typed re-runs on the new items.
        public bool SetData(string site, CatalogResponse data, CatalogSource source)
        {
            if (data == null || (Site != null && site != Site)) return false;
            Site = site;
            string keep = CurrentCategory?.id;
            // gaze-catalog: another focus's list leads with its own first category, unless the user chose the one shown
            // and it's still there.
            bool refocused = Data != null && (Data.focus ?? "") != (data.focus ?? "");
            if (refocused && !m_UserCategory) keep = null;
            Data = data;
            Source = source;
            Index = new CatalogIndex(data);
            Category = 0;
            bool kept = false;
            if (keep != null)
                for (int i = 0; i < data.categories.Count; i++) if (data.categories[i].id == keep) { Category = i; kept = true; break; }
            if (refocused)
            {
                Refocused++;
                if (!kept) { m_UserCategory = false; ChipOffset = 0; if (Mode == CatalogMode.Browse) Page = 0; }
                else KeepChipInView(Category);
            }
            if (ChipOffset >= ChipEntries) ChipOffset = 0;
            if (Mode == CatalogMode.Search) RunLocal();
            Refresh();
            return true;
        }

        // ---------------- open / close ----------------

        /// Open the catalog. No tape or gap is needed. `fromTape`: opened from the tape's "Find parts for …" pill, so the
        /// Fits chip starts on (when there is something to fit against).
        public void Open(bool fromTape = false)
        {
            IsOpen = true;
            if (fromTape && Fit.Available) FitsOn = true;
            if (Mode == CatalogMode.Results && !HasResults) Mode = CatalogMode.Browse;
            Refresh();
        }

        public void Close()
        {
            IsOpen = false;
            KeyboardOpen = false;
            Version++;
        }

        public void SetKeyboard(bool open)
        {
            if (KeyboardOpen == open) return;
            KeyboardOpen = open;
            Version++;
        }

        // ---------------- the Fits chip ----------------

        /// The tape / gap now. Without one the chip goes (and the filter with it).
        public void SetFit(CatalogFit fit)
        {
            if (fit.SameAs(Fit)) return;
            Fit = fit;
            if (!fit.Available) FitsOn = false;
            Refresh();
        }

        public bool ToggleFits()
        {
            if (!Fit.Available) return false;
            FitsOn = !FitsOn;
            Refresh();
            return true;
        }

        // ---------------- categories and chips ----------------

        int ChipEntries => (HasResults ? 1 : 0) + CategoryCount;

        /// Slot `slot`'s chip: Results (the last live search), a category (index), More (more categories), or none.
        public CatalogChip ChipAt(int slot, out int category)
        {
            category = -1;
            int n = ChipEntries;
            if (slot < 0 || slot >= ChipSlots || n == 0) return CatalogChip.None;
            bool paged = n > ChipSlots;
            if (paged && slot == ChipSlots - 1) return CatalogChip.More;
            int perPage = paged ? ChipSlots - 1 : ChipSlots;
            if (slot >= perPage) return CatalogChip.None;
            int entry = paged ? ChipOffset + slot : slot;
            if (entry >= n) return CatalogChip.None;
            if (HasResults)
            {
                if (entry == 0) return CatalogChip.Results;
                entry--;
            }
            category = entry;
            return CatalogChip.Category;
        }

        public string ChipLabel(int slot)
        {
            switch (ChipAt(slot, out int c))
            {
                case CatalogChip.Results: return "Results";
                case CatalogChip.More: return "More…";
                case CatalogChip.Category: return Data.categories[c].Title;
                default: return "";
            }
        }

        public bool ChipSelected(int slot)
        {
            switch (ChipAt(slot, out int c))
            {
                case CatalogChip.Results: return Mode == CatalogMode.Results;
                case CatalogChip.Category: return Mode == CatalogMode.Browse && c == Category;
                default: return false;
            }
        }

        /// A chip was tapped.
        public void PressChip(int slot)
        {
            switch (ChipAt(slot, out int c))
            {
                case CatalogChip.Results: ShowResultsMode(); break;
                case CatalogChip.Category: SelectCategory(c); break;
                case CatalogChip.More:
                    int perPage = ChipSlots - 1;
                    ChipOffset += perPage;
                    if (ChipOffset >= ChipEntries) ChipOffset = 0;
                    Version++;
                    break;
            }
        }

        /// Browse category i (the search text goes).
        public bool SelectCategory(int i)
        {
            if (i < 0 || i >= CategoryCount) return false;
            Field.Set("");
            Spoken = null;
            Debounce.Cancel();
            Server = CatalogServerState.Idle;
            m_Found.Clear();
            Mode = CatalogMode.Browse;
            Category = i;
            Page = 0;
            m_UserCategory = true;   // gaze-catalog: a focus change keeps it while it's listed
            KeepChipInView(i);
            Refresh();
            return true;
        }

        /// Keep category i's chip on screen.
        void KeepChipInView(int i)
        {
            int n = ChipEntries;
            if (n <= ChipSlots) return;
            int entry = i + (HasResults ? 1 : 0), perPage = ChipSlots - 1;
            if (entry < ChipOffset || entry >= ChipOffset + perPage) ChipOffset = entry / perPage * perPage;
        }

        /// A category by id, title or a spoken name ("dishwasher" finds "Dishwashers"). −1: none.
        public int FindCategory(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Data?.categories == null) return -1;
            string n = name.Trim().ToLowerInvariant(), slug = CatalogJson.Slug(name), stem = CatalogSearch.Stem(n);
            var cats = Data.categories;
            for (int i = 0; i < cats.Count; i++)
                if ((cats[i].id ?? "").ToLowerInvariant() == n || cats[i].id == slug || cats[i].Title.ToLowerInvariant() == n) return i;
            for (int i = 0; i < cats.Count; i++)
            {
                string t = cats[i].Title.ToLowerInvariant();
                if (t.StartsWith(stem) || CatalogSearch.Stem(t).StartsWith(stem) || (cats[i].id ?? "").StartsWith(CatalogJson.Slug(stem))) return i;
            }
            for (int i = 0; i < cats.Count; i++)
                if (cats[i].keywords != null && cats[i].keywords.Contains(stem)) return i;
            return -1;
        }

        public void ShowResultsMode()
        {
            if (!HasResults) return;
            Mode = CatalogMode.Results;
            Page = 0;
            Refresh();
        }

        // ---------------- typing (lazy search) ----------------

        /// A key of the glass keyboard at `now` (s). Submitted: the window hands Query to the live store search.
        public CatalogKeyResult Key(string key, float now)
        {
            bool hadSpoken = Spoken != null;   // voice: typing starts over from the keyboard's own text
            Spoken = null;
            var r = Field.Press(key);
            if (r == CatalogKeyResult.Submitted)
            {
                KeyboardOpen = false;
                Version++;
                if (hadSpoken) OnTextChanged(now);
                return r;
            }
            if (r == CatalogKeyResult.Changed || r == CatalogKeyResult.Cleared || hadSpoken) OnTextChanged(now);
            return r;
        }

        /// Put `text` in the field as if typed (the keyboard's; tests and the harness).
        public void SetQuery(string text, float now)
        {
            string before = Field.Text;
            bool hadSpoken = Spoken != null;
            Spoken = null;
            Field.Set(text);
            if (Field.Text != before || hadSpoken || Mode == CatalogMode.Results) OnTextChanged(now);
        }

        /// voice: search for what was asked by voice without writing it in the field (see Spoken). Empty: back to browsing.
        public void SetSpokenQuery(string text, float now)
        {
            s_Clean.Set(text ?? "");
            string q = s_Clean.Query;
            Field.Set("");
            Spoken = q.Length > 0 ? q : null;
            OnTextChanged(now);
        }

        static readonly CatalogTextField s_Clean = new CatalogTextField();

        void OnTextChanged(float now)
        {
            Page = 0;
            ServerAdded = 0;
            if (Query.Length == 0)
            {
                Debounce.Cancel();
                Server = CatalogServerState.Idle;
                m_Found.Clear();
                Mode = CatalogMode.Browse;
                Refresh();
                return;
            }
            Mode = CatalogMode.Search;
            RunLocal();
            Debounce.Poke(now);
            Server = CatalogServerState.Waiting;
            Refresh();
        }

        void RunLocal()
        {
            CatalogSearch.Filter(Index, Query, m_Found);
        }

        /// True once the typing has paused: ask the laptop for `query` with `generation`.
        public bool TakeDueSearch(float now, out int generation, out string query)
        {
            query = null;
            if (Mode != CatalogMode.Search || !Debounce.Due(now, out generation)) { generation = 0; return false; }
            query = Query;
            Server = CatalogServerState.Asking;
            Version++;
            return true;
        }

        /// The laptop's answer for `generation` (null: it failed). False: stale (a newer key press), dropped.
        public bool OnServerReply(int generation, CatalogSearchResponse reply)
        {
            if (!Debounce.Accept(generation)) return false;
            if (Mode != CatalogMode.Search) return false;
            m_Found.RemoveAll(h => h.FromServer);
            if (reply == null) { Server = CatalogServerState.Failed; ServerAdded = 0; }
            else
            {
                ServerAdded = CatalogSearch.Merge(m_Found, reply.items, Index);
                Server = CatalogServerState.Answered;
            }
            Refresh();
            return true;
        }

        /// Local results found for the text as typed (before the laptop's).
        public int LocalCount
        {
            get
            {
                int n = 0;
                foreach (var h in m_Found) if (!h.FromServer) n++;
                return n;
            }
        }

        // ---------------- live search results ----------------

        /// A live search started (`searching`) or answered: its candidates become the Results the grid shows.
        public void ShowResults(string query, bool searching, IList<PartSummary> candidates)
        {
            ResultsQuery = string.IsNullOrWhiteSpace(query) ? "your request" : query.Trim();
            ResultsSearching = searching;
            Results.Clear();
            if (candidates != null) foreach (var c in candidates) if (c != null) Results.Add(CatalogHit.From(c));
            Debounce.Cancel();
            Server = CatalogServerState.Idle;
            Mode = CatalogMode.Results;
            KeyboardOpen = false;
            Page = 0;
            Refresh();
        }

        // ---------------- the grid ----------------

        public int PageCount => Visible.Count == 0 ? 1 : (Visible.Count + CardsPerPage - 1) / CardsPerPage;

        public CatalogHit PageItem(int slot)
        {
            int i = Page * CardsPerPage + slot;
            return slot >= 0 && slot < CardsPerPage && i >= 0 && i < Visible.Count ? Visible[i] : null;
        }

        public bool NextPage()
        {
            if (Page + 1 >= PageCount) return false;
            Page++;
            Version++;
            return true;
        }

        public bool PreviousPage()
        {
            if (Page <= 0) return false;
            Page--;
            Version++;
            return true;
        }

        /// Recompute what the grid shows (the Fits filter applies to the catalog's items, not to a live search, which
        /// already sent the tape).
        public void Refresh()
        {
            Visible.Clear();
            switch (Mode)
            {
                case CatalogMode.Results:
                    Visible.AddRange(Results);
                    break;
                case CatalogMode.Search:
                    foreach (var h in m_Found) if (!FitsOn || Fit.Fits(h.Summary.dims_mm)) Visible.Add(h);
                    break;
                default:
                    var cat = CurrentCategory;
                    if (cat != null)
                        foreach (var item in cat.items)
                            if (!FitsOn || Fit.Fits(item.dims_mm)) Visible.Add(CatalogHit.From(item, Category));
                    break;
            }
            if (Page >= PageCount) Page = PageCount - 1;
            if (Page < 0) Page = 0;
            Version++;
        }

        /// The grid is empty (not while a live search runs): the window offers the store search instead.
        public bool Empty => Visible.Count == 0 && Data != null && !(Mode == CatalogMode.Results && ResultsSearching);

        /// The empty grid's button: "Find dishwashers in stores" / "Search stores for “xyz”" (null: none).
        public string EmptyAction
        {
            get
            {
                if (!Empty) return null;
                if (Mode == CatalogMode.Search && Query.Length > 0) return $"Search stores for “{Query}”";
                var cat = CurrentCategory;
                if (Mode == CatalogMode.Browse && cat != null) return $"Find {cat.Title.ToLowerInvariant()} in stores";
                return null;
            }
        }

        /// What the empty grid's button searches for.
        public string EmptyQuery => Mode == CatalogMode.Search ? Query : CurrentCategory?.Query;

        /// Picking the card in `slot`: the hit, its place in the list shown, and what to call the list (the parts flow's
        /// query: the live search's, the text typed, or the category's store query). False: no card there.
        public bool Pick(int slot, out CatalogHit hit, out int index, out string label)
        {
            hit = PageItem(slot);
            index = Page * CardsPerPage + slot;
            label = Mode == CatalogMode.Results ? (ResultsQuery == "your request" ? null : ResultsQuery)
                  : Mode == CatalogMode.Search ? Query
                  : CurrentCategory?.Query;
            return hit != null;
        }

        // ---------------- words ----------------

        /// The catalog's own status line (null in Results: the live search's progress shows there instead).
        public string Status()
        {
            if (Data == null) return "Loading the catalog…";
            string src = CatalogText.SourceNote(Source);
            string fits = FitsOn ? " that fit " + Units_(Fit.HasGap ? Fit.GapW : Fit.TapeM) : "";
            switch (Mode)
            {
                case CatalogMode.Results:
                    return null;
                case CatalogMode.Search:
                {
                    string head = $"“{Query}”";
                    string server = Server switch
                    {
                        CatalogServerState.Waiting or CatalogServerState.Asking => " · asking the laptop…",
                        CatalogServerState.Answered => ServerAdded > 0 ? $" · +{ServerAdded} from the laptop" : "",
                        CatalogServerState.Failed => " · laptop not connected",
                        _ => "",
                    };
                    if (Visible.Count == 0 && (Server == CatalogServerState.Answered || Server == CatalogServerState.Failed))
                        return $"No {head}{fits} in the catalog · Search asks the stores";
                    return $"{head} · {Visible.Count}{fits}{server}";
                }
                default:
                {
                    var cat = CurrentCategory;
                    if (cat == null) return "No categories here · type to search the stores";
                    if (Visible.Count == 0)
                        return cat.items.Count > 0 && FitsOn ? $"None{fits} · turn off Fits to see all {cat.items.Count}" : "Nothing saved here yet";
                    string n = CatalogText.Count(Visible.Count) + fits;
                    return src.Length > 0 ? $"{n} · {src}" : n;
                }
            }
        }

        string Units_(double metres) => Units.FormatPrimary(metres, LengthUnits);

        /// Back to the first-run state (a demo reset): closed, browsing the first category, no text, no results, Fits off.
        public void ResetSession()
        {
            IsOpen = false;
            KeyboardOpen = false;
            Mode = CatalogMode.Browse;
            Category = 0;
            Page = 0;
            ChipOffset = 0;
            FitsOn = false;
            Field.Set("");
            Spoken = null;
            Debounce.Cancel();
            Server = CatalogServerState.Idle;
            ServerAdded = 0;
            m_Found.Clear();
            ResultsQuery = null;
            ResultsSearching = false;
            Results.Clear();
            Picked = null;
            ResetFocus();   // gaze-catalog
            Refresh();
        }

        // ---------------- gaze-catalog: what the wearer looks at ----------------

        /// What the catalog follows now: the gaze's held focus (GazeFocusTracker, already smoothed), or the pinned one.
        public CatalogFocus Focus { get; private set; }
        /// Its subject when the gaze knows what it is ("the dishwasher"); null: the focus's own name.
        public string FocusSubject { get; private set; }
        /// The gaze's latest held focus (kept while pinned: unpinning catches up with it).
        public CatalogFocus Gaze { get; private set; }
        string m_GazeSubject;
        /// Pinned: the catalog stops following the gaze (the header chip).
        public bool FocusPinned { get; private set; }
        /// Bumped whenever an answer for another focus replaced the list (the window's gentle settle).
        public int Refocused { get; private set; }
        /// The user chose the category shown (a chip, a spoken category): a focus change keeps it while it's listed.
        bool m_UserCategory;

        /// The loaded catalog says which foci its site answers (an older laptop or the built-in table: none).
        public bool FocusSupported => Data?.foci != null && Data.foci.Count > 0;

        public bool Indoor => CatalogFoci.IsIndoor(Data?.environment, Site);

        /// The site's catalog answers this focus.
        public bool Serves(CatalogFocus f)
        {
            string wire = CatalogFoci.Wire(f);
            if (wire == null || Data?.foci == null) return false;
            for (int i = 0; i < Data.foci.Count; i++) if (Data.foci[i] == wire) return true;
            return false;
        }

        /// The focus to ask the laptop for: Focus when the site answers it, else none (the whole place).
        public CatalogFocus RequestFocus => Serves(Focus) ? Focus : CatalogFocus.None;

        /// The focus the list shown is for.
        public CatalogFocus ShownFocus => CatalogFoci.Parse(Data?.focus);

        /// The gaze's held focus changed (or its subject). True: the list should follow (not pinned, another focus).
        public bool SetGazeFocus(CatalogFocus focus, string subject)
        {
            Gaze = focus;
            m_GazeSubject = focus == CatalogFocus.None ? null : subject;
            if (FocusPinned) return false;
            return Follow(Gaze, m_GazeSubject);
        }

        /// The header chip: pin what's shown (the list stops following the gaze), or unpin (it catches up with the gaze).
        /// True: the focus changed (the list should follow).
        public bool TogglePin()
        {
            FocusPinned = !FocusPinned;
            Version++;
            return !FocusPinned && Follow(Gaze, m_GazeSubject);
        }

        bool Follow(CatalogFocus focus, string subject)
        {
            bool changed = focus != Focus;
            if (!changed && subject == FocusSubject) return false;
            Focus = focus;
            FocusSubject = subject;
            Version++;
            return changed;
        }

        /// The header chip's words.
        public string FocusChipLabel => CatalogText.FocusChip(Focus, FocusSubject, FocusPinned, FocusSupported, Indoor);

        void ResetFocus()
        {
            Focus = Gaze = CatalogFocus.None;
            FocusSubject = m_GazeSubject = null;
            FocusPinned = false;
            m_UserCategory = false;
        }
    }
}
