using System.Collections.Generic;
using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// The crate menu (SPEC M4): preset searches as poke buttons; results fan out as candidate cards; taking a card
    /// loads the part (PartLoader) and puts it in the hand (PartTool). Voice goes through AppCommands.FindPart /
    /// SelectCandidate, which land here too. Shown only while the world is open.
    /// catalog: the window is now the Catalog (CatalogWindow draws it: the place's categories, a grid, the search field
    /// and keyboard). This stays the engine under it — the live store search, a spoken search job, taking candidate i,
    /// the left side slot and yielding it — and it opens on demand (the ring's Catalog, voice, a search), never because a
    /// tape was taken.
    public class PartsBrowser : MonoBehaviour
    {
        public PartsClient client;
        public PartLoader loader;
        public PartTool tool;
        public GameObject content;
        public TMPro.TextMeshPro statusText;
        public CandidateCardView[] cards = new CandidateCardView[0];
        [Header("Panel sizing: the window grows to fit the rows it shows")]
        public AirTools.UI.GlassSurface panel;
        public float panelTop = 0.17f;
        public float headerHeight = 0.112f;
        public float rowPitch = 0.07f;
        public float bottomPad = 0.012f;
        public AirTools.UI.WindowHandle handle;
        [Tooltip("Segmented control of preset searches (PartsButton on each); the active query is shown selected.")]
        public AirTools.UI.GlassButton[] searchButtons = new AirTools.UI.GlassButton[0];
        [Tooltip("Talk: tap (listens until you stop) or hold; the agent's reply and search land in this window.")]
        public AirTools.UI.GlassButton talk;
        [Tooltip("catalog: the Catalog window drawn in this window (null: the old crate rows).")]
        public CatalogWindow catalog;
        [Header("Placement (UX W0.7): a side slot from the person, shown once it's useful")]
        public Transform head;
        public float distance = 0.5f;
        public float downDeg = 15f;
        public float yawDeg = -35f;   // left side slot, a little behind the main window slot (0.45 m)
        [Tooltip("Hidden in the world until opened (the ring's Catalog, voice, a search / pick). Off = always shown.")]
        public bool revealOnUse = true;

        /// Open in the world (catalog: opened from the ring / voice / a search; before, the first reading opened it).
        public bool Revealed { get; private set; }
        /// Declutter M4 (DC4): the left side slot is taken (the install coach card), or the main slot holds Sellers or
        /// Checkout. Find parts hides meanwhile and comes back when it's free.
        public bool Yielding { get; private set; }

        /// Shown right now: in the world, revealed, and not yielding its slot.
        bool ShouldShow => AppState.Mode == AppMode.World && (Revealed || !revealOnUse) && !Yielding;

        /// Declutter M4: Find parts gives way while the coach card is open (it takes the left side slot) and while
        /// Sellers or Checkout holds the main slot (the purchase is the one thing on screen). Pure: the main slot's
        /// owner against the two panels' windows.
        public static bool YieldsTo(bool coachOpen, object mainSlot, object sellersWindow, object checkoutWindow) =>
            coachOpen || (mainSlot != null && (ReferenceEquals(mainSlot, sellersWindow) || ReferenceEquals(mainSlot, checkoutWindow)));

        public List<PartSummary> Candidates { get; private set; } = new List<PartSummary>();
        public string Status { get; private set; } = "idle";
        public string LastQuery { get; private set; }
        public string Source { get; private set; }
        public bool Searching { get; private set; }
        public bool Loading { get; private set; }
        public int SearchCount { get; private set; }
        public PartInstance LastLoaded { get; private set; }

        PartTool m_Subscribed;

        void OnEnable()
        {
            Services.Register(this);
            AppState.Changed += OnMode;
            if (catalog == null) AirTools.Notes.Notebook.Added += OnReading;   // catalog: a tape no longer opens the window
            Subscribe();
        }

        void OnDisable()
        {
            Services.Unregister(this);
            AppState.Changed -= OnMode;
            AirTools.Notes.Notebook.Added -= OnReading;
            if (m_Subscribed != null) m_Subscribed.PartPlaced -= OnPlaced;
            m_Subscribed = null;
            if (m_Agent != null) m_Agent.Replied -= OnReplied;
            m_Agent = null;
        }

        void Subscribe()
        {
            var t = tool != null ? tool : Services.Get<PartTool>();
            if (t == null || t == m_Subscribed) return;
            if (m_Subscribed != null) m_Subscribed.PartPlaced -= OnPlaced;
            m_Subscribed = t;
            t.PartPlaced += OnPlaced;
        }

        void OnPlaced(PartInstance part) => SetStatus($"Placed · {AirTools.UI.Copy.FitLine(part.Fit)}");

        void Start()
        {
            Subscribe();
            OnMode(AppState.Mode, AppState.Mode);
            ShowCards();
            SetStatus(catalog != null ? "" : "Pick a part to try it at real size");
        }

        void OnMode(AppMode from, AppMode mode)
        {
            if (mode == AppMode.World && !Revealed && (!revealOnUse || (catalog == null && HasReading()))) Revealed = true;
            bool show = ShouldShow;
            if (content != null) content.SetActive(show);
            if (show && from != AppMode.World) Place();
        }

        static bool HasReading()
        {
            foreach (var e in AirTools.Notes.Notebook.Entries) if (e.Tool == "measure" && e.OnCurrentSite) return true;   // sitescope
            return false;
        }

        void OnReading(AirTools.Notes.NotebookEntry e) { if (e.Tool == "measure") Reveal(); }

        /// catalog: open it (again) in front of you: the left side slot from where you look now.
        public void Show()
        {
            if (!Revealed) { Reveal(); return; }
            if (content != null) content.SetActive(ShouldShow);
            Place();
        }

        /// catalog: close it (Close, the ring's Catalog again); a search or the ring opens it again.
        public void Hide()
        {
            if (!Revealed) return;
            Revealed = !revealOnUse;
            if (content != null) content.SetActive(ShouldShow);
            Log.Info("Catalog closed");
        }

        /// catalog: the Catalog's list becomes the candidates (a pick from a category or the typed search), so Select, "next
        /// one" and the placement editor's model swaps step through what was shown. Not a search: no results event.
        public void Adopt(List<PartSummary> list, string query, string source)
        {
            Candidates = list ?? new List<PartSummary>();
            LastQuery = string.IsNullOrWhiteSpace(query) ? "catalog" : query.Trim();
            Source = source;
        }

        /// Show Find parts (first reading, a search, a pick): placed in the left side slot from where you look.
        public void Reveal()
        {
            if (Revealed) return;
            Revealed = true;
            if (AppState.Mode != AppMode.World) return;
            if (content != null) content.SetActive(ShouldShow);
            Place();
            Log.Info(Yielding ? "Find parts revealed (waits while its slot is taken)" : "Find parts shown");
        }

        /// Declutter M4: hide while the coach card or a purchase panel has the screen; come back in the left side slot
        /// from where you look (unless you had moved it). No allocation: called every frame.
        void UpdateYield()
        {
            var coach = Services.Get<AirTools.Agent.Grok.CoachRailView>();
            var sellers = Services.Get<SellerPanel>();
            var checkout = Services.Get<CheckoutPanel>();
            bool y = YieldsTo(coach != null && coach.IsOpen, WindowSlot.Current,
                sellers != null ? sellers.window : null, checkout != null ? checkout.window : null);
            if (y == Yielding) return;
            Yielding = y;
            bool show = ShouldShow;
            if (content != null && content.activeSelf != show) content.SetActive(show);
            if (show && (handle == null || !handle.WasMoved)) Place();
            if (AppState.Mode == AppMode.World && (Revealed || !revealOnUse))
                Log.Info(y ? "Find parts yields its slot" : "Find parts back");
        }

        /// Into the side slot from the person: distance ahead, downDeg below the eye line, yawDeg to the left, facing
        /// the eyes. World-locked after (under the rig), movable by its grab bar.
        public void Place()
        {
            if (head == null) return;
            var rig = head.parent != null ? head.parent.root : null;
            if (rig != null && head.position.y - rig.position.y < 0.5f) return;   // not tracked yet: keep the built pose
            var p = AirTools.UI.HeadAnchor.PoseFor(head.position, head.forward, distance, downDeg, yawDeg);
            transform.SetPositionAndRotation(p.position, p.rotation);
            if (handle != null) handle.WasMoved = false;
        }

        readonly AirTools.Agent.TalkButton m_Talk = new AirTools.Agent.TalkButton();   // voice

        string m_LoaderStatus;

        void Update()
        {
            UpdateYield();
            // assetgen: while a taken part waits for its model, the status line says so ("Building the 3D model… 7 s").
            if (Loading)
            {
                var l = loader != null ? loader : Services.Get<PartLoader>();
                string st = l != null ? l.Status : null;
                if (st != null && !ReferenceEquals(st, m_LoaderStatus)) { m_LoaderStatus = st; SetStatus(st); }
            }
            // voice: tap or hold Talk (same as Settings'; TalkButton → VoiceClient). A tap listens until you stop talking.
            var voice = Services.Get<AirTools.Agent.VoiceClient>();
            if (voice == null) return;
            SubscribeAgent();
            m_Talk.Drive(talk, voice, talk != null && talk.isActiveAndEnabled);
            if (m_Talk.Started) m_AwaitingReply = true;
            if (!m_Talk.Changed || m_Talk.Line.Length == 0) return;
            SetStatus(m_Talk.Line);   // "Listening… tap to send" → "Thinking…" (the reply follows) or "Didn't catch that"
            if (catalog != null) catalog.OnVoiceLine();   // catalog: the talk line owns the status for now
            if (!voice.Recording && !voice.LastSent) m_AwaitingReply = false;
        }

        bool m_AwaitingReply;
        AirTools.Agent.AgentClient m_Agent;

        /// Show what was heard and the reply here (a search that follows replaces it with its own progress).
        void SubscribeAgent()
        {
            if (m_Agent != null || !Services.TryGet(out m_Agent)) return;
            m_Agent.Replied += OnReplied;
        }

        void OnReplied(string transcript, string reply)
        {
            if (!m_AwaitingReply || Searching) return;
            m_AwaitingReply = false;
            // voice (user 09-27): the words go to the agent and show on this line, not in the Catalog's search field.
            string said = AirTools.UI.Copy.Clip(AirTools.UI.Copy.Clean(reply), 60);
            SetStatus(string.IsNullOrEmpty(transcript) ? said : $"“{transcript.Trim()}” · {said}");
        }

        public void Search(string query) => Search(query, true);

        /// catalog: `useMeasurement` false leaves the tape / gap out of the store search (the Catalog's Fits chip is off).
        public void Search(string query, bool useMeasurement)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            var c = client != null ? client : Services.Get<PartsClient>();
            Reveal();
            LastQuery = query.Trim();
            Searching = true;
            foreach (var b in searchButtons)
                if (b != null) b.SetSelected(b.GetComponent<PartsButton>() is PartsButton pb && string.Equals(pb.text, LastQuery, System.StringComparison.OrdinalIgnoreCase));
            Candidates = new List<PartSummary>();
            ShowCards();
            SetStatus(useMeasurement ? SearchingText(LastQuery) : $"Finding {PluralQuery(LastQuery)}…");
            if (c == null) { OnResults(new List<PartSummary>(), "none"); return; }
            int session = m_Session;   // D7: results of a search from before a demo reset are dropped
            c.Search(LastQuery, (results, source) => { if (session == m_Session) OnResults(results, source); }, useMeasurement);
        }

        /// Show the candidates of a search the agent started (search_started {job_id}), with the job's progress stage.
        public void FollowJob(string jobId)
        {
            var c = client != null ? client : Services.Get<PartsClient>();
            if (c == null || string.IsNullOrEmpty(jobId)) return;
            Reveal();
            LastQuery = "your request";
            Searching = true;
            Candidates = new List<PartSummary>();
            ShowCards();
            SetStatus("Searching stores…");
            void Stage(string st) { if (Searching) { var t = AirTools.UI.Copy.Stage(st); if (t.Length > 0) SetStatus(t); } }
            c.StageChanged += Stage;
            c.PollJob(jobId, (results, source) =>
            {
                c.StageChanged -= Stage;
                if (results == null)
                {
                    Searching = false;
                    if (catalog != null) catalog.OnCandidatesChanged(LastQuery, false, Candidates);   // catalog: no longer searching
                    SetStatus(AirTools.UI.Copy.Error(AirTools.UI.ErrorSurface.Search, c.LastError, c.LastHttpCode));
                    return;
                }
                OnResults(results, source);
                if (!string.IsNullOrEmpty(c.LastSummary))
                {
                    string summary = AirTools.UI.Copy.Clip(AirTools.UI.Copy.Clean(c.LastSummary), 80);
                    SetStatus(summary);
                    AirTools.UI.UiToast.Reply(summary);
                }
            });
        }

        void OnResults(List<PartSummary> results, string source)
        {
            Searching = false;
            SearchCount++;
            Candidates = results ?? new List<PartSummary>();
            Source = source;
            ShowCards();
            SetStatus(ResultsText(Candidates.Count, source, PartsClient.MeasurementContext()));
            Log.Info($"Parts search \"{LastQuery}\": {Candidates.Count} candidates from {source}");
            WatchModels();   // assetgen
        }

        // assetgen: the server builds each candidate's model after the search (backend jobs._resolve_assets, a few seconds
        // each): the card says "Building the 3D model…" until its part.json says ready, then shows what made it.

        /// Candidates whose model is still being built (the harness waits on it).
        public int ModelsBuilding { get; private set; }
        /// One line per model that came in: id, tier, template, who made it, the server's and our seconds.
        public List<string> ModelLog { get; } = new List<string>();

        void WatchModels()
        {
            var c = client != null ? client : Services.Get<PartsClient>();
            if (c == null || !Application.isPlaying) return;
            int session = m_Session;
            int search = SearchCount;
            foreach (var s in Candidates)
            {
                var spec = s?.spec;
                if (spec?.asset == null || spec.asset.Ready || spec.asset.Failed) continue;
                ModelsBuilding++;
                float t0 = Time.realtimeSinceStartup;
                var summary = s;
                c.WaitForAsset(spec, status => Card(summary)?.SetModelStatus(CandidateCardView.BuildingText), (fresh, ready) =>
                {
                    ModelsBuilding = Mathf.Max(0, ModelsBuilding - 1);
                    if (session != m_Session || search != SearchCount) return;
                    if (fresh != null) { summary.spec = fresh; summary.tier = fresh.asset?.tier; }
                    var a = fresh?.asset;
                    string line = ready
                        ? $"{summary.id}: tier {a?.tier ?? "?"}{(a?.template != null ? $" ({a.template})" : "")} by {a?.made_by ?? "?"}{(a?.retried == true ? " after a Grok retry" : "")}, " +
                          $"server {(a?.seconds.HasValue == true ? a.seconds.Value.ToString("0.0") + " s" : "?")}, here {Time.realtimeSinceStartup - t0:0.0} s"
                        : $"{summary.id}: model not ready after {Time.realtimeSinceStartup - t0:0} s ({(a?.Failed == true ? "failed" : "timed out")})";
                    ModelLog.Add(line);
                    Log.Info($"Model {(ready ? "ready" : "missing")} · {line}");
                    Card(summary)?.SetModelStatus(ready ? null : "Proxy box · exact size");   // the tier label (summary.tier) once ready
                });
            }
        }

        CandidateCardView Card(PartSummary s)
        {
            foreach (var card in cards) if (card != null && card.Summary == s) return card;
            return null;
        }

        void ShowCards()
        {
            if (catalog != null && LastQuery != null) catalog.OnCandidatesChanged(LastQuery, Searching, Candidates);   // catalog: a search's results fill the grid
            FitPanel();
            var c = client != null ? client : Services.Get<PartsClient>();
            for (int i = 0; i < cards.Length; i++)
            {
                if (cards[i] == null) continue;
                var s = i < Candidates.Count ? Candidates[i] : null;
                cards[i].Show(s);
                if (s != null && c != null && Application.isPlaying)
                {
                    var card = cards[i];
                    c.GetImage(s, tex => { if (card != null && card.Summary == s) card.SetImage(tex); });
                }
            }
        }

        /// Resize the window to its content (top edge fixed): header + one row per candidate shown.
        void FitPanel()
        {
            if (panel == null || catalog != null) return;   // catalog: a fixed-size window
            int rows = Mathf.Min(Candidates.Count, cards.Length);
            float h = headerHeight + rows * rowPitch + bottomPad;
            panel.SetSize(new Vector2(panel.size.x, h));
            var p = panel.transform.localPosition;
            panel.transform.localPosition = new Vector3(p.x, panelTop - h * 0.5f, p.z);
            if (handle != null) handle.transform.localPosition = new Vector3(0f, panelTop - h - 0.016f, 0f);
        }

        /// Load candidate i and put it in the hand. False if there is no such candidate or a load is running. edit6dof:
        /// `edit` (the Catalog's Take): it opens in the Edit view first to orient and colour it (EditView.OpenOnTake).
        public bool Select(int i, bool edit = false)
        {
            if (i < 0 || i >= Candidates.Count || Loading) return false;
            var s = Candidates[i];
            // The part put back here by a tool switch (UX W0.4) comes straight back to the hand.
            var pt = tool != null ? tool : Services.Get<PartTool>();
            if (pt != null && pt.TryUnpark(s.id))
            {
                for (int k = 0; k < cards.Length; k++) if (cards[k] != null) cards[k].SetHighlight(k == i);
                LastLoaded = pt.Held;
                SetStatus(AirTools.Input.InputMode.Controllers ? $"Aim at {AirTools.UI.Copy.MountTarget(s.spec ?? pt.Held.Spec)}, pull the trigger" : $"Aim at {AirTools.UI.Copy.MountTarget(s.spec ?? pt.Held.Spec)}, pinch to place");
                return true;
            }
            var l = loader != null ? loader : Services.Get<PartLoader>();
            if (l == null) return false;
            Loading = true;
            for (int k = 0; k < cards.Length; k++) if (cards[k] != null) cards[k].SetHighlight(k == i);
            SetStatus("Getting it ready at real size…");
            string query = LastQuery == "your request" ? null : LastQuery;
            int session = m_Session;   // D7: a part that finishes loading after a demo reset is not put in the next judge's hand
            l.Load(s, part =>
            {
                if (session != m_Session) { if (part != null) { part.gameObject.SetActive(false); Destroy(part.gameObject); } return; }
                Loading = false;
                LastLoaded = part;
                if (part == null) { SetStatus("Couldn't load this part · try another"); return; }
                part.SearchQuery = query;
                var t = tool != null ? tool : Services.Get<PartTool>();
                if (edit && EditView.TryOpenNew(part)) { SetStatus("Turn it and pick a colour, then Place"); return; }   // edit6dof
                if (t != null) t.Hold(part);
                string target = AirTools.UI.Copy.MountTarget(part.Spec);
                SetStatus(AirTools.Input.InputMode.Controllers ? $"Aim at {target}, pull the trigger" : $"Aim at {target}, pinch to place");
            });
            return true;
        }

        // D7 (W1.8): AppCommands.ResetDemo
        int m_Session;

        /// Back to the first-run state: no query, no candidates, hidden in the world until the first reading (UX W0.7).
        /// A search or load still in flight is dropped when it answers.
        public void ResetSession()
        {
            m_Session++;
            Searching = false;
            Loading = false;
            LastQuery = null;
            Source = null;
            LastLoaded = null;
            Candidates = new List<PartSummary>();
            m_AwaitingReply = false;
            Revealed = !revealOnUse;
            foreach (var b in searchButtons) if (b != null) b.SetSelected(false);
            for (int k = 0; k < cards.Length; k++) if (cards[k] != null) cards[k].SetHighlight(false);
            if (catalog != null) catalog.ResetSession();   // catalog
            else ShowCards();
            SetStatus(catalog != null ? "" : "Pick a part to try it at real size");
            if (content != null) content.SetActive(ShouldShow);
        }

        /// "Finding cabinet hinges…" (+ " for 0.26 m" with a tape in the notebook).
        public static string SearchingText(string query)
        {
            var m = PartsClient.MeasurementContext();
            string what = PluralQuery(query);
            return m != null && m.TryGetValue("value_m", out var v) && v is double d ? $"Finding {what} for {AirTools.UI.Copy.Len(d)}…" : $"Finding {what}…";
        }

        /// "3 matches for your 0.26 m tape" / "2 saved parts · laptop not connected" / "No matches · try another part".
        public static string ResultsText(int count, string source, System.Collections.Generic.Dictionary<string, object> measurement)
        {
            bool offline = source != null && source.StartsWith("offline");
            if (count == 0) return offline ? "Not in the saved parts · connect the laptop" : "No matches · try another part";
            if (offline) return $"{count} saved part{(count == 1 ? "" : "s")} · laptop not connected";
            string m = count == 1 ? "1 match" : $"{count} matches";
            return measurement != null && measurement.TryGetValue("value_m", out var v) && v is double d ? $"{m} for your {AirTools.UI.Copy.Len(d)} tape" : m;
        }

        /// "cabinet hinge" → "cabinet hinges"; "window ac" → "window AC units".
        static string PluralQuery(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return "parts";
            var t = q.Trim();
            if (t.Equals("window ac", System.StringComparison.OrdinalIgnoreCase)) return "window AC units";
            if (t.Equals("your request", System.StringComparison.OrdinalIgnoreCase)) return "parts";
            if (t.EndsWith("shelf")) return t.Substring(0, t.Length - 5) + "shelves";
            return t.EndsWith("s") ? t : t + "s";
        }

        void SetStatus(string s)
        {
            Status = s;
            if (statusText != null) statusText.text = s;
        }
    }
}
