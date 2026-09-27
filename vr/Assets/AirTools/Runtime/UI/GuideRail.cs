using AirTools.Agent;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using UnityEngine;

namespace AirTools.UI
{
    /// UX W1.3 guide rail: assembles the AppSnapshot from the running app once per frame (property reads plus a few
    /// event stamps, no allocations), asks the pure NextStep for the Step when its Key changes, and pushes it to the
    /// status line and the Next-step pill; every 0.25 s it advances the coach (CoachRules → CoachService).
    /// Behind `Enabled` (default off): off, nothing is shown and the toasts behave exactly as before (spec §5.4).
    /// Wires no scene objects itself; GuideRailBuilder (Editor) adds it with its views.
    [DefaultExecutionOrder(1000)]
    public class GuideRail : MonoBehaviour
    {
        /// GuideRailEnabled (UX W1.3 §1). Off by default until the Editor gate passes; flip it here or via the harness.
        /// D7: DemoMode (AirTools.Core.DemoMode, on in the expo build) switches it on with itself.
        public static bool Enabled = false;
        /// UX W0.4 has landed on this branch: a tool switch parks the tape (MeasureTool.Equip) and the held part
        /// (PartTool.Park) instead of discarding them, so R15 / R23 / C13 never apply.
        public const bool W04Landed = true;

        public StatusLine status;
        public NextStepPill pill;
        public CoachService coach;
        [Tooltip("Seconds between coach updates.")]
        public float coachInterval = 0.25f;

        public static GuideRail Instance { get; private set; }

        /// The Step on screen (the last one For() built; kept through transitions).
        public Step Current => m_Step;
        /// The snapshot assembled this frame (a copy).
        public AppSnapshot Snapshot => m_S;
        public CoachState CoachState => m_Coach;
        /// Palm menu / ring openings this run: all of them, and those by a hand or controller (not the harness's Force).
        public int RingOpensAll { get; private set; }
        public int RingOpensJudge { get; private set; }
        public int StepsBuilt { get; private set; }

        AppSnapshot m_S;
        Step m_Step;
        long m_Key;
        bool m_HasStep, m_Shown;
        readonly CoachState m_Coach = new CoachState();
        float m_NextCoach;

        // Services (late-bound: they register in their own OnEnable).
        ToolManager m_Tools; MeasureTool m_Measure; LevelTool m_Level; PartTool m_Parts; PartsBrowser m_Browser;
        SellerPanel m_Sellers; CheckoutPanel m_Checkout; TakeItHome m_Home; PalmMenu m_Palm; NotebookPanel m_NotebookPanel;
        NotebookController m_NotebookCtl; VoiceClient m_Voice; AgentClient m_Agent; SceneRoot m_Root; SceneStreamer m_Streamer;
        ModeController m_Mode; ToolInputHub m_Hub; Locomotion m_Loco;

        // Event stamps → Newest.
        readonly int[] m_Stamps = new int[8];
        int m_Stamp;
        bool m_Entered;

        // Dirty-flag caches.
        bool m_NotebookDirty = true;
        int m_LastSessionPoints = -1;
        float m_SessionArea;
        OutlineCrossing m_SessionCrossing;
        string m_StartSite = "";
        ExportResult m_Export; float m_ExportAt = -999f;
        Refusal m_Refusal; float m_RefusalAt = -999f;
        UndoEcho m_Echo; ReadingKind m_EchoKind; float m_EchoValue, m_EchoAt = -999f;
        NotebookEntry m_LastEntryChanged; int m_LastEntryFrame = -1;
        bool m_WasSearching, m_HadHeld, m_PlacedSeen;
        float m_SearchStart;
        PartInstance m_LastLoaded; System.Collections.Generic.List<PartSummary> m_LastCandidates; int m_LastSelected = -1;

        // ---------------- lifecycle ----------------

        void OnEnable()
        {
            Instance = this;
            Services.Register(this);
            AppState.Changed += OnMode;
            Notebook.Added += OnEntryAdded;
            Notebook.Updated += OnEntryUpdated;
            Notebook.Removed += OnEntryRemoved;
            FeedbackEvents.Fired += OnFeedback;
            NextStepActions.Ran += OnRan;
            if (AppState.Mode != AppMode.Passthrough) m_Entered = true;
            m_NotebookDirty = true;
            if (Application.isPlaying) NextStepHooks();
        }

        void OnDisable()
        {
            if (Instance == this) Instance = null;
            Services.Unregister(this);
            AppState.Changed -= OnMode;
            Notebook.Added -= OnEntryAdded;
            Notebook.Updated -= OnEntryUpdated;
            Notebook.Removed -= OnEntryRemoved;
            FeedbackEvents.Fired -= OnFeedback;
            NextStepActions.Ran -= OnRan;
            Unbind();
        }

        /// Point the pure formatters at the app's (D2 units, prices, the display scrub).
        static void NextStepHooks()
        {
            NextStep.Len = m => Copy.Len(m);
            NextStep.Area = m2 => Copy.Area(m2);   // D2: {area} in the user's unit too
            NextStep.Money = usd => PartFormat.Price(usd);
            NextStep.Clean = s => Copy.Clean(s);
        }

        void Bind()
        {
            if (m_Tools == null && Services.TryGet(out m_Tools)) m_Tools.Changed += OnTool;
            if (m_Measure == null && Services.TryGet(out m_Measure)) { m_Measure.ShapeCompleted += OnShape; m_Measure.ShapeChanged += OnShapeChanged; }
            if (m_Level == null && Services.TryGet(out m_Level)) m_Level.Placed += OnLevel;
            if (m_Parts == null && Services.TryGet(out m_Parts)) { m_Parts.PartPlaced += OnPlaced; m_Parts.ArrayPlaced += OnArray; }
            if (m_Checkout == null && Services.TryGet(out m_Checkout)) m_Checkout.Purchased += OnPurchased;
            if (m_Palm == null && Services.TryGet(out m_Palm)) m_Palm.Changed += OnPalm;
            if (m_NotebookCtl == null && Services.TryGet(out m_NotebookCtl)) m_NotebookCtl.ExportStatusChanged += OnExport;
            if (m_Root == null && Services.TryGet(out m_Root)) { m_Root.Rescaled += OnRescaled; m_Root.ContentChanged += OnContent; }
            if (m_Streamer == null && Services.TryGet(out m_Streamer)) { m_Streamer.Loaded += OnLoaded; RefreshStartSite(); }
            if (m_Hub == null && Services.TryGet(out m_Hub)) { m_Hub.PressStart += OnPress; m_Hub.ButtonDown += OnButton; }
            if (m_Browser == null) Services.TryGet(out m_Browser);
            if (m_Sellers == null) Services.TryGet(out m_Sellers);
            if (m_Home == null) Services.TryGet(out m_Home);
            if (m_NotebookPanel == null) Services.TryGet(out m_NotebookPanel);
            if (m_Voice == null) Services.TryGet(out m_Voice);
            if (m_Agent == null) Services.TryGet(out m_Agent);
            if (m_Mode == null) Services.TryGet(out m_Mode);
            if (m_Loco == null) Services.TryGet(out m_Loco);
        }

        void Unbind()
        {
            if (m_Tools != null) m_Tools.Changed -= OnTool;
            if (m_Measure != null) { m_Measure.ShapeCompleted -= OnShape; m_Measure.ShapeChanged -= OnShapeChanged; }
            if (m_Level != null) m_Level.Placed -= OnLevel;
            if (m_Parts != null) { m_Parts.PartPlaced -= OnPlaced; m_Parts.ArrayPlaced -= OnArray; }
            if (m_Checkout != null) m_Checkout.Purchased -= OnPurchased;
            if (m_Palm != null) m_Palm.Changed -= OnPalm;
            if (m_NotebookCtl != null) m_NotebookCtl.ExportStatusChanged -= OnExport;
            if (m_Root != null) { m_Root.Rescaled -= OnRescaled; m_Root.ContentChanged -= OnContent; }
            if (m_Streamer != null) m_Streamer.Loaded -= OnLoaded;
            if (m_Hub != null) { m_Hub.PressStart -= OnPress; m_Hub.ButtonDown -= OnButton; }
            m_Tools = null; m_Measure = null; m_Level = null; m_Parts = null; m_Checkout = null; m_Palm = null; m_NotebookCtl = null;
            m_Root = null; m_Streamer = null; m_Hub = null; m_Browser = null; m_Sellers = null; m_Home = null; m_NotebookPanel = null;
            m_Voice = null; m_Agent = null; m_Mode = null; m_Loco = null;
        }

        // ---------------- events ----------------

        void Stamp(StepEvent e) => m_Stamps[(int)e] = ++m_Stamp;
        static float Now => Time.unscaledTime;

        void OnMode(AppMode from, AppMode to)
        {
            if (to == AppMode.World) m_Entered = true;
            Stamp(StepEvent.Mode);
        }

        void OnTool(ToolKind kind) => Stamp(StepEvent.Tool);
        void OnShape(MeasureShape shape) { Stamp(StepEvent.Tape); m_NotebookDirty = true; }
        void OnShapeChanged(MeasureShape shape) => m_NotebookDirty = true;
        void OnLevel(LevelPlacement p) => Stamp(StepEvent.Level);
        void OnPlaced(PartInstance part) { Stamp(StepEvent.Placed); m_PlacedSeen = true; }
        void OnArray(PartTool.ArrayGroup g) { Stamp(StepEvent.Placed); m_PlacedSeen = true; }
        void OnPurchased(CheckoutReceipt r, PartSpec spec) => Stamp(StepEvent.Purchase);
        void OnRescaled(float factor) => m_NotebookDirty = true;
        void OnContent() { m_NotebookDirty = true; RefreshStartSite(); }
        void OnLoaded(SceneManifest m) { m_NotebookDirty = true; RefreshStartSite(); }

        void OnEntryAdded(NotebookEntry e) { m_NotebookDirty = true; m_LastEntryChanged = e; m_LastEntryFrame = Time.frameCount; }
        void OnEntryUpdated(NotebookEntry e) => m_NotebookDirty = true;
        void OnEntryRemoved(NotebookEntry e) { m_NotebookDirty = true; m_LastEntryChanged = e; m_LastEntryFrame = Time.frameCount; }

        void OnPalm(bool open)
        {
            if (!open) return;
            RingOpensAll++;
            if (m_Palm != null && (m_Palm.OpenedBy == "hand" || m_Palm.OpenedBy == "controller")) RingOpensJudge++;
            InputActivity.Touch(Now);
        }

        void OnPress(ToolHand hand, Pose pose) => InputActivity.Touch(Now);
        void OnButton(ToolHand hand, ToolButton button) => InputActivity.Touch(Now);

        void OnExport(string status)
        {
            var s = status ?? "";
            if (s == "uploaded" || s == "up to date") m_Export = ExportResult.Uploaded;
            else if (s.StartsWith("saved", System.StringComparison.Ordinal)) m_Export = ExportResult.SavedLocal;
            else if (s.StartsWith("error", System.StringComparison.Ordinal)) m_Export = ExportResult.Error;
            else return;   // "exporting"
            m_ExportAt = Now;
        }

        /// Every feedback event but hover / snap ticks is deliberate input. A miss is a tool's silent "no" (every
        /// InputHints.Say fires one): read which from the equipped tool. Undo / redo start the R02 echo.
        void OnFeedback(Feedback f)
        {
            if (f == Feedback.Hover || f == Feedback.SnapTick) return;
            InputActivity.Touch(Now);
            if (f == Feedback.Miss)
            {
                var r = ProbeRefusal();
                if (r != Refusal.None) { m_Refusal = r; m_RefusalAt = Now; }
            }
            else if (f == Feedback.Undo || f == Feedback.Redo)
            {
                m_Echo = f == Feedback.Undo ? UndoEcho.Undo : UndoEcho.Redo;
                m_EchoAt = Now;
                m_EchoKind = ReadingKind.None;
                m_EchoValue = 0f;
                if (m_LastEntryFrame == Time.frameCount && m_LastEntryChanged != null)
                {
                    m_EchoKind = KindOf(m_LastEntryChanged);
                    m_EchoValue = (float)m_LastEntryChanged.ValueSI;
                }
                else if (m_Measure != null && (m_Measure.LastAction == "undo point" || m_Measure.LastAction == "redo point")) m_EchoKind = ReadingKind.Other;
                else if (m_Parts != null && m_Parts.LastAction != null && m_Parts.LastAction.StartsWith("put back", System.StringComparison.Ordinal)) m_EchoKind = ReadingKind.Part;
            }
        }

        Refusal ProbeRefusal()
        {
            var tool = m_Tools != null ? m_Tools.Active : ToolKind.None;
            string last = tool switch
            {
                ToolKind.Measure => m_Measure != null ? m_Measure.LastAction : null,
                ToolKind.Move => m_Loco != null ? m_Loco.LastAction : null,
                ToolKind.Part => m_Parts != null ? m_Parts.LastAction : null,
                ToolKind.Level => m_Level != null ? m_Level.LastAction : null,
                _ => null,
            };
            int points = m_Measure != null ? m_Measure.Session.Count : 0;
            return NextStepActions.RefusalFromTool(tool, last, points);
        }

        void OnRan(StepAction a, bool ok)
        {
            m_Coach.ActionRan(Now);
            if (ok) return;
            var r = NextStepActions.RefusalFor(a.Command, m_Parts != null ? m_Parts.LastAction : null);
            if (r != Refusal.None) { m_Refusal = r; m_RefusalAt = Now; }
        }

        void RefreshStartSite()
        {
            var o = ServerConfig.SiteOverride;
            m_StartSite = !string.IsNullOrEmpty(o) ? o : m_Streamer != null ? (m_Streamer.startSite ?? "") : "";
        }

        static ReadingKind KindOf(NotebookEntry e)
        {
            switch (e?.Tool)
            {
                case "measure": return e.Unit == "m²" ? ReadingKind.Area : ReadingKind.Tape;
                case "level": return ReadingKind.Level;
                case "part": case "array": return ReadingKind.Part;
                case "purchase": return ReadingKind.Purchase;
                case null: return ReadingKind.None;
                default: return ReadingKind.Other;
            }
        }

        // ---------------- per frame ----------------

        void Update() => Bind();

        void LateUpdate()
        {
            if (!Enabled)
            {
                if (m_Shown) HideAll();
                return;
            }
            float now = Now;
            Fill(ref m_S, now);
            if (!m_S.Transitioning)
            {
                long key = NextStep.Key(m_S);
                if (!m_HasStep || key != m_Key)
                {
                    m_Key = key;
                    m_Step = NextStep.For(m_S);
                    m_HasStep = true;
                    StepsBuilt++;
                    m_Coach.ObserveStep(m_Step, now);
                    if (status != null) status.Show(m_Step);
                    if (pill != null) pill.Show(m_Step);
                    m_Shown = true;
                }
            }
            if (now >= m_NextCoach)
            {
                m_NextCoach = now + coachInterval;
                if (coach != null) coach.Tick(m_S, m_Coach);
            }
        }

        void HideAll()
        {
            m_Shown = false;
            m_HasStep = false;
            m_Coach.Current = CoachRuleId.None;
            if (status != null) status.Hide();
            if (pill != null) pill.Hide();
            if (coach != null) coach.Clear();
        }

        /// Rebuild the Step on the next frame (e.g. after the harness changed a decision flag).
        public void Invalidate() => m_HasStep = false;

        // D7 (W1.8): AppCommands.ResetDemo
        /// Forget this judge: never entered the world, no event stamps, no ring openings, no refusal / undo echo / export
        /// result, coach counts and timing cleared (in memory: nothing is written to PlayerPrefs). The Step is rebuilt on
        /// the next frame.
        public void ResetSession()
        {
            m_Entered = AppState.Mode != AppMode.Passthrough;
            System.Array.Clear(m_Stamps, 0, m_Stamps.Length);
            m_Stamp = 0;
            RingOpensAll = 0;
            RingOpensJudge = 0;
            m_Export = ExportResult.None; m_ExportAt = -999f;
            m_Refusal = Refusal.None; m_RefusalAt = -999f;
            m_Echo = UndoEcho.None; m_EchoKind = ReadingKind.None; m_EchoValue = 0f; m_EchoAt = -999f;
            m_LastEntryChanged = null; m_LastEntryFrame = -1;
            m_WasSearching = false; m_HadHeld = false; m_PlacedSeen = false;
            m_LastLoaded = null; m_LastCandidates = null; m_LastSelected = -1;
            m_LastSessionPoints = -1; m_SessionArea = 0f; m_SessionCrossing = OutlineCrossing.None;
            m_NotebookDirty = true;
            m_Coach.Reset();
            m_NextCoach = 0f;
            m_HasStep = false;
            if (coach != null) coach.ResetSession();
        }

        /// Assemble this frame's snapshot now and return a copy (the harness: works with the rail off, too).
        public AppSnapshot Assemble()
        {
            Fill(ref m_S, Time.unscaledTime);
            return m_S;
        }

        /// Fill the snapshot from the live app (spec §2.1). Property reads only; the notebook, tape target and area are
        /// recomputed on their events (so always fill the same snapshot, m_S).
        void Fill(ref AppSnapshot s, float now)
        {
            Bind();
            s.Now = now;
            // A · mode + scene
            s.Mode = AppState.Mode;
            s.Transitioning = m_Mode != null && m_Mode.IsTransitioning;
            s.EverEnteredWorld = m_Entered;
            s.Scene = m_Root == null ? SceneKind.None
                : !m_Root.IsRuntimePackage ? SceneKind.Facade
                : m_Root.Site != null && m_Root.Site.IndexOf("kitchen", System.StringComparison.OrdinalIgnoreCase) >= 0 ? SceneKind.Kitchen
                : SceneKind.Package;
            s.StartSite = m_StartSite;
            s.SceneLoading = m_Streamer != null && m_Streamer.Loading;
            // fix-ux: a load that failed / timed out shows "Couldn't load the kitchen · Retry" (R46 failed), and Retry
            // loads the site that failed (almost always the start site).
            s.SceneLoadFailed = !s.SceneLoading && m_Streamer != null && m_Streamer.LoadFailed
                                && (m_Root == null || !m_Root.IsRuntimePackage || m_Root.Site != m_Streamer.FailedSite);
            if (s.SceneLoadFailed && !string.IsNullOrEmpty(m_Streamer.FailedSite)) s.StartSite = m_Streamer.FailedSite;
            s.SceneFallback = s.Scene == SceneKind.Facade && !string.IsNullOrEmpty(m_StartSite) && m_StartSite != "built-in"
                              && !s.SceneLoading && m_Streamer != null && m_Streamer.LastError != null;
            s.ScaleCalibrated = m_Root == null || !m_Root.IsRuntimePackage || Mathf.Abs(m_Root.Calibration - 1f) > 1e-4f;

            // B · tools + measure
            s.Tool = m_Tools != null ? m_Tools.Active : ToolKind.None;
            bool area = m_Measure != null && m_Measure.AreaMode;
            s.MeasureMode = !MeasureTool.AutoSaveTwoPointTapes ? MeasureMode.Explicit : area ? MeasureMode.Area : MeasureMode.Line;
            // A tape parked by a tool switch (W0.4) isn't "in progress" for the rail until Measure is back in hand.
            s.SessionPoints = m_Measure != null && m_Measure.Equipped ? m_Measure.Session.Count : 0;
            s.Shapes = m_Measure != null ? m_Measure.Shapes.Count : 0;
            s.Levels = m_Level != null ? m_Level.Placements.Count : 0;
            if (s.SessionPoints >= 2)
            {
                var pts = m_Measure.Session.Points;
                s.SessionLengthM = Vector3.Distance(pts[0], pts[1]);
                if (s.SessionPoints != m_LastSessionPoints && s.SessionPoints >= 3)
                {
                    var m = MeasureMath.Measure(pts);   // allocates: only when a point is added / removed
                    m_SessionArea = (float)m.Area;
                    m_SessionCrossing = m.Crossing;
                }
                s.SessionAreaM2 = s.SessionPoints >= 3 ? m_SessionArea : 0f;
                s.SessionCrossing = s.SessionPoints >= 3 ? m_SessionCrossing : OutlineCrossing.None;
            }
            else { s.SessionLengthM = 0f; s.SessionAreaM2 = 0f; s.SessionCrossing = OutlineCrossing.None; }
            m_LastSessionPoints = s.SessionPoints;
            if (m_NotebookDirty) FillNotebook(ref s);

            // C · parts
            FillParts(ref s, now);

            // D · commerce
            FillCommerce(ref s);

            // E · UI + input
            s.RingOpen = m_Palm != null && m_Palm.IsOpen;
            s.RingByController = s.RingOpen && m_Palm.OpenedBy == "controller";
            s.RingInteracting = s.RingOpen && m_Palm.ring != null && m_Palm.ring.Interacting;
            if (s.RingInteracting) InputActivity.Touch(now);
            s.RingOpens = RingOpensJudge;
            s.NotebookOpen = m_NotebookPanel != null && m_NotebookPanel.IsOpen;
            s.NotebookCount = Notebook.Entries.Count;
            s.Export = m_Export;
            s.ExportAge = now - m_ExportAt;
            s.VoiceRecording = m_Voice != null && m_Voice.Recording;
            s.VoiceHold = s.VoiceRecording && m_Voice.Holding;   // voice
            s.AgentBusy = m_Agent != null && m_Agent.Busy;
            InputActivity.Modality = AirTools.Input.InputMode.Controllers ? InputModality.Controllers : InputModality.Hands;
            s.Modality = InputActivity.Modality;
            s.IdleSeconds = InputActivity.IdleSeconds(now);
            s.Refusal = m_Refusal;
            s.RefusalAge = now - m_RefusalAt;
            s.Echo = m_Echo;
            s.EchoKind = m_EchoKind;
            s.EchoValue = m_EchoValue;
            s.EchoAge = now - m_EchoAt;

            // F · ordering + flags
            int best = 0; var newest = StepEvent.None;
            for (int i = 1; i < m_Stamps.Length; i++) if (m_Stamps[i] > best) { best = m_Stamps[i]; newest = (StepEvent)i; }
            s.Newest = newest;
            s.Ux.D1Entry = ToolManager.Default;
            s.Ux.D4AutoSave = MeasureTool.AutoSaveTwoPointTapes;
            s.Ux.D5RayOnUi = GlassButton.RayOnWindows;
            s.Ux.D6Commit = ToolRing.CommitOnPinch ? RingCommit.Pinch : RingCommit.Settle;
            s.Ux.W04Preserves = W04Landed;
            s.Ux.W16OfflineReceipt = false;   // W1.6 not built
            s.Ux.DemoMode = AirTools.Core.DemoMode.On;   // D7 (W1.8): skips R47 "Set the scale", keeps coach counts out of PlayerPrefs
            s.Ux.GuideRailEnabled = Enabled;
        }

        void FillNotebook(ref AppSnapshot s)
        {
            m_NotebookDirty = false;
            var entries = Notebook.Entries;
            var last = Notebook.NewestOnSite(entries);   // switchclean: was Notebook.Last (another model's area / level read out here)
            s.LastReading = KindOf(last);
            s.LastAreaM2 = s.LastReading == ReadingKind.Area ? (float)last.ValueSI : 0f;
            s.LastLevelDeg = 0f;
            for (int i = entries.Count - 1; i >= 0; i--)
                if (entries[i].Tool == "level" && entries[i].OnCurrentSite) { s.LastLevelDeg = (float)entries[i].ValueSI; break; }   // switchclean: && OnCurrentSite
            // The tape FindPart sends (PartsClient.MeasurementContext → the latest 2-point measure entry): the same one.
            var tape = AirTools.Structure.ScaleCalibration.LatestTape();
            if (tape != null)
            {
                s.LastTapeM = (float)tape.ValueSI;
                s.LastTapeId = tape.Id;
                s.LastTapeAxis = PartsClient.TapeAxis(tape.Points[0], tape.Points[1]) switch { "h" => TapeAxis.H, "w" => TapeAxis.W, _ => TapeAxis.Length };
                s.LastTapeTarget = SurfaceNames.ClassifyTape(tape.Points[0], tape.Points[1], m_Root);
            }
            else
            {
                s.LastTapeM = 0f; s.LastTapeId = -1; s.LastTapeAxis = TapeAxis.None; s.LastTapeTarget = TapeTarget.Unknown;
            }
        }

        void FillParts(ref AppSnapshot s, float now)
        {
            var b = m_Browser;
            bool searching = b != null && b.Searching;
            if (searching != m_WasSearching)
            {
                if (searching) m_SearchStart = now;
                Stamp(StepEvent.Search);   // a search started, or its results came in
                m_WasSearching = searching;
            }
            s.Searching = searching;
            s.SearchElapsed = searching ? now - m_SearchStart : 0f;
            s.Loading = b != null && b.Loading;
            s.Candidates = b != null ? b.Candidates.Count : 0;
            s.CandidatesOffline = b != null && b.Source != null && b.Source.StartsWith("offline", System.StringComparison.Ordinal);
            s.LastQuery = b != null ? b.LastQuery : null;
            s.LoadFailed = b != null && !b.Loading && b.LastLoaded == null && b.Status != null
                           && b.Status.StartsWith("Couldn't load", System.StringComparison.Ordinal);
            if (b != null && (b.LastLoaded != m_LastLoaded || !ReferenceEquals(b.Candidates, m_LastCandidates)))
            {
                m_LastLoaded = b.LastLoaded;
                m_LastCandidates = b.Candidates;
                m_LastSelected = -1;
                if (m_LastLoaded != null && m_LastLoaded.Spec != null)
                    for (int i = 0; i < m_LastCandidates.Count; i++)
                        if (m_LastCandidates[i] != null && m_LastCandidates[i].id == m_LastLoaded.Spec.id) { m_LastSelected = i; break; }
            }
            s.LastSelectedIndex = m_LastSelected;

            var t = m_Parts;
            var held = t != null ? t.Held : null;
            s.PartHeld = held != null;
            s.HeldOnSurface = held != null && t.OnSurface;
            s.HeldFit = held != null && held.Fit != null ? held.Fit.Status : FitStatus.None;
            // A deliberate put-back (Undo while holding) re-opens the search results; a park (tool switch, W0.4) doesn't.
            if (m_HadHeld && held == null && !m_PlacedSeen && (t == null || t.Parked == null) && s.Candidates > 0) Stamp(StepEvent.Search);
            m_HadHeld = held != null;
            m_PlacedSeen = false;
            s.HeldLost = false;   // pre-W0.4 discard detection is moot: W04Landed
            s.HeldLostAge = 0f;
            var sel = t != null ? t.Selected : null;
            s.PartName = held != null ? held.Spec?.name : sel != null ? sel.Spec?.name : null;
            s.SelectedPlaced = sel != null && sel.Placed;
            var fit = sel != null ? sel.Fit : null;
            s.SelectedFit = fit != null ? fit.Status : FitStatus.None;
            s.SelectedReason = fit != null ? NextStep.ReasonOf(fit.Status, fit.Collisions.Count, fit.ClearanceBlocked.Count, fit.SupportedSamples, fit.Headline) : FitReason.None;
            s.SelectedHasTape = fit != null && (fit.SpareMm.HasValue || fit.OpeningMm.HasValue);
            s.ArrayAllowed = sel != null && sel.Spec != null && sel.Spec.spacing_mm.HasValue && sel.Spec.spacing_mm.Value >= 10f;
            s.PlacedCount = t != null ? t.PlacedParts.Count : 0;
        }

        void FillCommerce(ref AppSnapshot s)
        {
            var sp = m_Sellers;
            s.SellersOpen = sp != null && sp.IsOpen;
            s.SellersRefreshing = s.SellersOpen && sp.Refreshing;
            s.RecommendedSeller = -1; s.BestSeller = null; s.BestPrice = 0f;
            var part = s.SellersOpen ? sp.Part : null;
            if (part != null && part.Spec != null && part.Spec.sellers != null && part.Spec.sellers.Count > 0)
            {
                var sellers = part.Spec.sellers;
                int rec = part.Spec.recommended_seller ?? -1;
                if (rec < 0 || rec >= sellers.Count) rec = sp.Order.Count > 0 ? sp.Order[0] : 0;
                if (rec >= 0 && rec < sellers.Count && sellers[rec] != null)
                {
                    var seller = sellers[rec];
                    s.RecommendedSeller = rec;
                    s.BestSeller = seller.name;
                    s.BestPrice = seller.price_usd * seller.PacksFor(sp.Quantity()) + SellerSort.Shipping(seller);
                }
            }

            var c = m_Checkout;
            var state = c != null ? c.State : CheckoutState.Closed;
            // The checkout's window can lose the one main slot to another window (UX W0.7) without closing the checkout:
            // then it isn't in front of the wearer, so the rail treats it as closed (a payment in flight still counts).
            if (state != CheckoutState.Paying && state != CheckoutState.Closed && c.window != null && !c.window.IsOpen) state = CheckoutState.Closed;
            s.Checkout = state;
            s.ReceiptAuthorized = c != null && c.Receipt != null && c.Receipt.Authorized;
            s.CheckoutTotal = state == CheckoutState.Closed ? 0f : state == CheckoutState.Paid && c.Receipt != null ? c.Receipt.total_usd : c.Total;
            s.HoldProgress = c != null && c.hold != null ? c.hold.Progress : 0f;
            if (s.HoldProgress > 0.001f) InputActivity.Touch(s.Now);
            s.Purchases = m_Home != null ? m_Home.Purchases.Count : 0;
            s.OnTable = m_Home != null ? m_Home.OnTable.Count : 0;
        }
    }
}
