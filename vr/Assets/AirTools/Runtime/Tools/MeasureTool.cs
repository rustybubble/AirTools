using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Tools
{
    /// A finished measurement: its points (scene-root space), the result, its notebook entry and its graphics.
    public class MeasureShape
    {
        public Vector3[] Points;
        public SnapKind[] Kinds;
        public PointMeasurement Measurement;
        public NotebookEntry Entry;
        public MeasureView View;
        /// Set on shapes the agent measured (B1 survey / slope); null for the user's own.
        public SurveyTag Survey;
        /// What the world-label pool gives this shape (declutter S10): the newest of the user's shapes shows its whole
        /// set (Full), older ones one summary pill (Summary) while there's room, then just the outline (None); a survey
        /// object's one W × H label is Full or None.
        public LabelDetail Labels = LabelDetail.Full;
        /// The user's own shape (or an agent's slope tape, drawn like one); not a B1 survey object.
        public bool IsTape => Survey == null || Survey.Tape;
    }

    /// What the agent measured a shape for (B1): the survey request, the structure object and its reading. Survey
    /// shapes draw one compact "W × H" label (UX W1.9 label diet), amber when a corner didn't snap, and one Undo removes
    /// every shape of the same request.
    public class SurveyTag
    {
        public string RequestId;
        public string ObjectId;
        public string Label;
        /// Metres, scene space: width = mean of sides 0–1 and 2–3, height = mean of sides 1–2 and 3–0 (click order).
        public double W, H;
        public bool Unverified;
        /// Highlighted by show_survey's focus (keeps its label).
        public bool Focus;
        /// A 2-point tape for a slope (check_slope): drawn like a normal tape.
        public bool Tape;
    }

    /// Point measure (SPEC M2, reworked after the first headset test): mark points on the scene; 2 points = distance,
    /// 3 or more = a polygon through the points in click order (concave L / U / notched shapes too) with corner angles,
    /// sides and area. No point limit. An outline that crosses itself isn't saved: the finish is refused with a hint and
    /// the points stay for Undo (MeasureMath reorders a crossed click order itself when every point is a hull corner).
    /// Click (press + release without dragging) places a snapped point. Finish the shape — and log it — by any of:
    ///   a pinch / trigger with the OTHER hand (2+ points), clicking the first point again (3+, closes the polygon),
    ///   clicking the last point again, thumb + middle-finger pinch, B/Y, or Done in the palm menu.
    /// While the palm menu is open the live preview is hidden (and the input source ignores tool presses), so
    /// reaching for the menu never drags a stray point along. Press on a placed point and drag to move it.
    public class MeasureTool : MonoBehaviour, IEditable, AirTools.UI.IWorldLabelSource, ISiteScoped   // sitescope: ISiteScoped
    {
        [Tooltip("Scene root: points are stored and measured in its local space (readings survive tabletop scaling).")]
        public Transform frame;
        [Tooltip("Parent for the graphics (a child of the frame). Created if empty.")]
        public Transform viewRoot;
        public MeasureStyle style = new MeasureStyle();

        [Header("Behaviour (metres)")]
        public float finishRadius = 0.03f;
        public float grabRadius = 0.04f;
        public float dragStartDistance = 0.015f;
        public float maxRayDistance = 40f;

        public bool Equipped { get; private set; }

        /// UX decision D4 (docs/ux/README.md): true = Line mode saves a tape at its 2nd point (no finish gesture);
        /// shapes need Area mode (EquipTool "area" / "angle", voice "area", or a pinch on the Measure lens while measuring).
        /// Off: every tape and shape is finished explicitly (the other hand's pinch, B / Y, the last point again, closing
        /// the polygon).
        public static bool AutoSaveTwoPointTapes = true;   // D4 decided (SPEC §9): Line mode saves at point 2

        /// Area mode (only matters with AutoSaveTwoPointTapes): points keep coming until you finish the shape.
        public bool AreaMode { get; set; }

        public MeasureSession Session { get; } = new MeasureSession();
        public IReadOnlyList<MeasureShape> Shapes => m_Shapes;
        /// Snapped surface point under the pointer (world), refreshed every frame while equipped.
        public SurfaceHit? Cursor { get; private set; }
        public SurfaceHit? LastPlaced { get; private set; }
        public string LastAction { get; private set; } = "";

        public event Action<MeasureShape> ShapeCompleted;
        public event Action<MeasureShape> ShapeChanged;

        readonly List<MeasureShape> m_Shapes = new List<MeasureShape>();
        // Undo / redo (EditHistory): when each session point and each shape was made, and what was undone.
        readonly List<float> m_PointTimes = new List<float>();
        readonly List<float> m_ShapeTimes = new List<float>();
        struct RedoOp { public MeasureShape shape; public List<MeasureShape> group; public Vector3 point; public SnapKind kind; public float at; }
        readonly List<RedoOp> m_Redo = new List<RedoOp>();
        readonly List<Vector3> m_Buffer = new List<Vector3>(MeasureMath.MaxPoints);
        IToolInput m_Input;
        MeasureView m_Preview;
        ToolHand m_Hand = ToolHand.Right;
        ToolHand m_SessionHand = ToolHand.Right;
        ToolHand? m_FinishHand;

        // Press tracking
        bool m_PressActive;
        SurfaceHit m_PressHit;
        bool m_PressHadHit;
        MeasureShape m_GrabShape;   // null + m_GrabIndex >= 0 → a point of the in-progress session
        int m_GrabIndex = -1;
        Vector3 m_GrabFrom;         // where a finished shape's grabbed point was (put back if the move crosses the outline)
        SnapKind m_GrabFromKind;
        bool m_Dragging;
        // W1.2: a live press commits the hit from ~90 ms before the pinch onset (m_PressHit); a drag still starts from
        // where the press registered (m_DragFrom), and the click radii are angular at the press's ray distance.
        bool m_PressRewound;
        Vector3 m_DragFrom;
        float m_PressDistance;

        void Awake() => EnsureViewRoot();

        void OnEnable()
        {
            Services.Register(this);
            EditHistory.Register(this);
            if (m_Input == null && Services.TryGet<IToolInput>(out var input)) SetInput(input);
            AppState.Changed += OnModeChanged;
            RegisterSite();   // sitescope
        }

        void OnDisable()
        {
            SetInput(null);
            Services.Unregister(this);
            EditHistory.Unregister(this);
            AppState.Changed -= OnModeChanged;
            AirTools.UI.WorldLabels.Remove(this);
            SiteScope.Unregister(this);   // sitescope
        }

        void Start()
        {
            if (m_Input == null && Services.TryGet<IToolInput>(out var input)) SetInput(input);
            OnModeChanged(AppState.Mode, AppState.Mode);
        }

        void OnModeChanged(AppMode _, AppMode mode)
        {
            if (viewRoot != null) viewRoot.gameObject.SetActive(mode != AppMode.Passthrough);
            LabelsChanged();   // hidden in passthrough: the pool gets the room back
        }

        public void SetInput(IToolInput input)
        {
            if (m_Input != null)
            {
                m_Input.PressStart -= OnPressStart;
                m_Input.PressMove -= OnPressMove;
                m_Input.PressEnd -= OnPressEnd;
                m_Input.ButtonDown -= OnButton;
            }
            m_Input = input;
            if (m_Input != null)
            {
                m_Input.PressStart += OnPressStart;
                m_Input.PressMove += OnPressMove;
                m_Input.PressEnd += OnPressEnd;
                m_Input.ButtonDown += OnButton;
            }
        }

        /// Put away / take out. A tape in progress is parked, not lost (UX W0.4): its points stay in the session (hidden)
        /// and come back when Measure is picked again.
        public void Equip(bool on)
        {
            if (Equipped == on) return;
            Equipped = on;
            if (!on)
            {
                Cursor = null;
                CancelPress();
                m_FinishHand = null;
                if (Session.Count > 0)
                {
                    Log.Info($"Measure: tape with {Session.Count} point(s) parked");
                    AirTools.UI.UiToast.Show("Tape kept · pick Measure to finish it", AirTools.UI.ColorRole.Info);
                }
                EditHistory.NotifyChanged();
            }
            else if (Session.Count > 0) Log.Info($"Measure: parked tape back ({Session.Count} point(s))");
            RefreshPreview();
            Log.Info($"Measure tool {(on ? "equipped" : "put away")}");
        }

        /// Points of a tape parked by a tool switch (0 when none, or while Measure is in hand).
        public int ParkedPoints => Equipped ? 0 : Session.Count;

        /// The agent is placing points (a survey): no rubber band to the user's cursor, and the user's presses don't
        /// add points (a pinch aborts the survey instead; SurveyRunner listens for it).
        public bool AgentDriving { get; set; }

        void Update()
        {
            if (!Equipped || m_Input == null) return;
            Tick();
        }

        /// Per-frame update: cursor + live preview from the current pointer (tests call this directly).
        public void Tick()
        {
            if (m_Input == null) return;
            var hand = Session.Count > 0 ? m_SessionHand : m_Input.LastActiveHand;
            bool menuOpen = Services.TryGet<AirTools.Input.PalmMenu>(out var palm) && palm.IsOpen;
            if (!menuOpen && m_Input.HasPointer(hand) && TryHit(m_Input.GetPointer(hand), out var hit))
            {
                Cursor = m_PressActive && m_PressRewound && m_PressHadHit && !m_Dragging ? m_PressHit : hit;   // W1.2: holds what you saw
                Session.Preview = m_PressActive || AgentDriving ? (Vector3?)null : ToFrame(hit.point);
            }
            else
            {
                Cursor = null;
                Session.Preview = null;
            }
            RefreshPreview();
        }

        // ---------------- input ----------------

        void OnPressStart(ToolHand hand, Pose pointer)
        {
            if (!Equipped || AgentDriving) return;
            // The other hand finishes the shape in progress (no reaching for a button).
            if (Session.Count >= MeasureMath.MinPoints && hand != m_SessionHand && !m_PressActive)
            {
                m_FinishHand = hand;
                return;
            }
            m_Hand = hand;
            m_PressActive = true;
            m_Dragging = false;
            // W1.2: the pointer from before the pinch (a live press with the rewind on), else the press pose as before.
            m_PressRewound = ToolInputHub.TryGetCommitPointer(m_Input, hand, out var commit);
            bool liveHit = false; SurfaceHit live = default;
            if (m_PressRewound) liveHit = TryHit(pointer, out live);
            else commit = pointer;
            m_PressHadHit = TryHit(commit, out m_PressHit);
            if (m_PressRewound && !m_PressHadHit && liveHit) { m_PressHit = live; m_PressHadHit = true; commit = pointer; }   // nothing was under it then
            m_DragFrom = m_PressRewound && liveHit ? live.point : m_PressHit.point;
            m_PressDistance = m_PressHadHit ? Vector3.Distance(commit.position, m_PressHit.point) : 0f;
            m_GrabShape = null; m_GrabIndex = -1;
            if (m_PressHadHit) FindGrabTarget(m_PressHit.point, m_PressDistance);
            if (m_GrabShape != null) { m_GrabFrom = m_GrabShape.Points[m_GrabIndex]; m_GrabFromKind = m_GrabShape.Kinds[m_GrabIndex]; }
        }

        void OnPressMove(ToolHand hand, Pose pointer)
        {
            if (!Equipped || !m_PressActive || hand != m_Hand || m_GrabIndex < 0) return;
            if (!TryHit(pointer, out var hit)) return;
            if (!m_Dragging && Vector3.Distance(hit.point, m_DragFrom) < dragStartDistance * WorldScale) return;   // W1.2: from the press
            m_Dragging = true;
            MoveGrabbed(hit, commit: false);
        }

        void OnPressEnd(ToolHand hand, Pose pointer)
        {
            if (!Equipped || AgentDriving) return;
            if (m_FinishHand == hand)
            {
                m_FinishHand = null;
                var shape = Finish();
                if (shape != null) LastAction += " (other hand)";
                return;
            }
            if (!m_PressActive || hand != m_Hand) return;
            m_PressActive = false;
            if (m_Dragging)
            {
                if (TryHit(pointer, out var hit)) MoveGrabbed(hit, commit: true);
                else MoveGrabbed(null, commit: true);
                m_Dragging = false;
                m_GrabIndex = -1;
                return;
            }
            m_GrabIndex = -1;
            if (m_PressHadHit) Click(m_PressHit, m_PressDistance);
            else
            {
                LastAction = "miss";
                AirTools.UI.InputHints.Say("measure.miss", "Aim at a surface to measure", after: 2);
            }
        }

        void OnButton(ToolHand hand, ToolButton button)
        {
            if (!Equipped) return;
            switch (button)
            {
                case ToolButton.Finish: Finish(); break;
                case ToolButton.Undo: Undo(); break;
                case ToolButton.Redo: Redo(); break;
                case ToolButton.Clear: ClearAll(); break;
            }
        }

        void CancelPress()
        {
            m_PressActive = false;
            m_PressRewound = false;
            m_Dragging = false;
            m_GrabIndex = -1;
        }

        // ---------------- actions ----------------

        /// A click at a snapped surface point: finish (if it's the last point again) or place a point.
        /// rayDistance: how far the pointer ray travelled to the hit (0 = unknown, e.g. the agent's survey clicks); with it
        /// the finish / close / duplicate radius is angular beyond its metric size (UX W1.2).
        public void Click(SurfaceHit hit, float rayDistance = 0f)
        {
            var p = ToFrame(hit.point);
            float finish = AngularRadii.Finish(finishRadius * WorldScale, rayDistance);   // W1.2
            if (Session.Count == 1 && Vector3.Distance(FromFrame(Session.Points[0]), hit.point) <= finish)
            {
                LastAction = "ignored duplicate point";
                AirTools.UI.InputHints.Say("measure.duplicate", AirTools.Input.InputMode.Controllers ? "Now trigger the other end" : "Now pinch the other end");
                return;
            }
            if (Session.Count >= MeasureMath.MinPoints &&
                Vector3.Distance(FromFrame(Session.Points[Session.Count - 1]), hit.point) <= finish)
            {
                Finish();
                return;
            }
            // Clicking the first point again closes the polygon.
            if (Session.Count >= 3 && Vector3.Distance(FromFrame(Session.Points[0]), hit.point) <= finish)
            {
                if (Finish() != null) LastAction += " (closed)";
                return;
            }
            if (Session.IsFull) { Finish(); return; }
            if (Session.Count == 0) m_SessionHand = m_Hand;
            Session.Add(p, hit.kind);
            m_PointTimes.Add(Now);
            DropRedo();
            EditHistory.Edited(this);
            LastPlaced = hit;
            LastAction = $"point {Session.Count} ({hit.kind})";
            Log.Info($"Measure {LastAction}");
            AirTools.UI.InputHints.Succeeded();
            AirTools.UI.FeedbackEvents.Point(hit.point, Session.Count);
            if (AutoSaveTwoPointTapes && !AreaMode && Session.Count == MeasureMath.MinPoints) { Finish(); return; }
            RefreshPreview();
        }

        public MeasureShape Finish() => Finish(null);

        /// measure-edges: the title of the next tape the user finishes ("Roof length", from "how long is the roof?" on a scan
        /// with nothing to tape for them: equip_tool {tool: "tape", label}); used once, then cleared.
        public string NextLabel { get; set; }

        /// The hint (and R01 rail status) when a finish is refused because the outline crosses itself.
        public const string CrossHint = "Edges cross · undo the last point";
        /// LastAction of that refusal (NextStepActions.RefusalFromTool reads it).
        public const string CrossRefused = "finish refused: edges cross";
        /// The hint when dragging a saved shape's point would make its outline cross (the point goes back).
        public const string MoveCrossHint = "Edges would cross · point put back";

        /// Finish the shape in progress; `survey` marks it as the agent's (B1): compact label, grouped undo.
        public MeasureShape Finish(SurveyTag survey)
        {
            if (Session.Count < MeasureMath.MinPoints)
            {
                LastAction = "finish ignored (need 2+ points)";
                // Only when a tape is under way: a stray menu-hand pinch with nothing started stays silent.
                if (Session.Count == 1) AirTools.UI.InputHints.Say("measure.finish", "Place two points first");
                return null;
            }
            var measured = MeasureMath.Measure(Session.Points);
            if (measured.SelfIntersecting)
            {
                // A crossed outline has no area: keep the points (Undo fixes it) and say why. A survey reports a skip.
                LastAction = CrossRefused;
                m_FinishHand = null;
                if (survey == null) AirTools.UI.InputHints.Say("measure.cross", CrossHint);
                Log.Info($"Measure: finish refused, the outline crosses itself ({Session.Count} points)");
                RefreshPreview();
                return null;
            }
            if (survey == null && !string.IsNullOrEmpty(NextLabel) && Session.Count == MeasureMath.MinPoints)
            {
                survey = new SurveyTag { Label = NextLabel, Tape = true };   // measure-edges: a tape the agent asked for
                NextLabel = null;
            }
            var shape = new MeasureShape
            {
                Points = Session.Points.ToArray(),
                Kinds = Session.Kinds.ToArray(),
                Survey = survey,
                Measurement = measured,
            };
            if (survey != null && !survey.Tape) SurveyMath.Size(shape.Points, out survey.W, out survey.H);
            shape.Entry = BuildEntry(shape);
            Notebook.Add(shape.Entry);
            var centre = Vector3.zero;
            foreach (var q in shape.Points) centre += q;
            AirTools.UI.FeedbackEvents.Saved(FromFrame(centre / Mathf.Max(1, shape.Points.Length)));
            EnsureViewRoot();
            shape.View = MeasureView.Create(viewRoot, style, $"Measure{m_Shapes.Count + 1}");
            m_Shapes.Add(shape);
            m_ShapeTimes.Add(Now);
            if (survey != null && !survey.Tape) shape.Labels = LabelDetail.None;   // the pool labels it (below)
            ShowShape(shape);
            m_PointTimes.Clear();
            Session.Clear();
            DropRedo();
            EditHistory.Edited(this);
            m_FinishHand = null;
            LastAction = $"finished #{shape.Entry.Id}: {shape.Entry.Label}";
            RefreshPreview();
            LabelsChanged();   // with the tape saved: the new shape is the focus (a survey object waits for its share)
            ShapeCompleted?.Invoke(shape);
            return shape;
        }

        /// Remove the last placed point; with nothing in progress, remove the last finished shape (kept for Redo).
        public void Undo()
        {
            if (Session.Count > 0)
            {
                int last = Session.Count - 1;
                var op = new RedoOp { point = Session.Points[last], kind = Session.Kinds[last], at = Now };
                Session.RemoveLast();
                if (m_PointTimes.Count > 0) m_PointTimes.RemoveAt(m_PointTimes.Count - 1);
                m_Redo.Add(op);
                LastAction = "undo point";
            }
            else if (m_Shapes.Count > 0)
            {
                // A survey comes off in one step: every trailing shape of the same request.
                string group = m_Shapes[m_Shapes.Count - 1].Survey?.RequestId;
                var removed = new List<MeasureShape>();
                do
                {
                    var s = m_Shapes[m_Shapes.Count - 1];
                    m_Shapes.RemoveAt(m_Shapes.Count - 1);
                    if (m_ShapeTimes.Count > 0) m_ShapeTimes.RemoveAt(m_ShapeTimes.Count - 1);
                    Notebook.Remove(s.Entry);
                    if (s.View != null) s.View.gameObject.SetActive(false);
                    removed.Insert(0, s);
                } while (group != null && m_Shapes.Count > 0 && m_Shapes[m_Shapes.Count - 1].Survey?.RequestId == group);
                if (removed.Count == 1) { m_Redo.Add(new RedoOp { shape = removed[0], at = Now }); LastAction = "undo shape"; }
                else { m_Redo.Add(new RedoOp { group = removed, at = Now }); LastAction = $"undo survey ({removed.Count} shapes)"; }
                LabelsChanged();
            }
            RefreshPreview();
            EditHistory.NotifyChanged();
        }

        /// Put back the last undone point or shape.
        public void Redo()
        {
            if (m_Redo.Count == 0) return;
            var op = m_Redo[m_Redo.Count - 1];
            m_Redo.RemoveAt(m_Redo.Count - 1);
            if (op.group != null)
            {
                foreach (var g in op.group)
                {
                    m_Shapes.Add(g);
                    m_ShapeTimes.Add(Now);
                    Notebook.Restore(g.Entry);
                    if (g.View != null) g.View.gameObject.SetActive(true);
                }
                m_ForceSurveyRedraw = true;
                LabelsChanged();
                LastAction = $"redo survey ({op.group.Count} shapes)";
            }
            else if (op.shape != null)
            {
                m_Shapes.Add(op.shape);
                m_ShapeTimes.Add(Now);
                Notebook.Restore(op.shape.Entry);
                if (op.shape.View != null) op.shape.View.gameObject.SetActive(true);
                if (op.shape.Survey != null) m_ForceSurveyRedraw = true;
                LabelsChanged();
                LastAction = "redo shape";
            }
            else
            {
                Session.Add(op.point, op.kind);
                m_PointTimes.Add(Now);
                LastAction = "redo point";
            }
            RefreshPreview();
            EditHistory.NotifyChanged();
        }

        public bool CanUndo => Session.Count > 0 || m_Shapes.Count > 0;
        public bool CanRedo => m_Redo.Count > 0;
        public float LastEditAt => Session.Count > 0 && m_PointTimes.Count > 0 ? m_PointTimes[m_PointTimes.Count - 1]
            : m_ShapeTimes.Count > 0 ? m_ShapeTimes[m_ShapeTimes.Count - 1] : float.NegativeInfinity;
        public float LastUndoAt => m_Redo.Count > 0 ? m_Redo[m_Redo.Count - 1].at : float.NegativeInfinity;

        public void DropRedo()
        {
            foreach (var op in m_Redo)
            {
                if (op.shape?.View != null) DestroyObj(op.shape.View.gameObject);
                if (op.group != null) foreach (var g in op.group) if (g.View != null) DestroyObj(g.View.gameObject);
            }
            m_Redo.Clear();
        }

        static float Now => EditHistory.Stamp();   // records an edit time (strictly increasing)

        public void ClearAll()
        {
            DropRedo();
            m_PointTimes.Clear();
            m_ShapeTimes.Clear();
            Session.Clear();
            foreach (var s in m_Shapes)
            {
                Notebook.Remove(s.Entry);
                if (s.View != null) DestroyObj(s.View.gameObject);
            }
            m_Shapes.Clear();
            LastAction = "clear";
            LabelsChanged();
            RefreshPreview();
            EditHistory.NotifyChanged();
        }

        void FindGrabTarget(Vector3 worldPoint, float rayDistance)
        {
            float best = AngularRadii.Grab(grabRadius * WorldScale, rayDistance);   // W1.2
            for (int i = 0; i < Session.Count; i++)
            {
                float d = Vector3.Distance(FromFrame(Session.Points[i]), worldPoint);
                if (d <= best) { best = d; m_GrabShape = null; m_GrabIndex = i; }
            }
            foreach (var s in m_Shapes)
            for (int i = 0; i < s.Points.Length; i++)
            {
                float d = Vector3.Distance(FromFrame(s.Points[i]), worldPoint);
                if (d <= best) { best = d; m_GrabShape = s; m_GrabIndex = i; }
            }
        }

        void MoveGrabbed(SurfaceHit? hit, bool commit)
        {
            if (m_GrabIndex < 0) return;
            if (hit.HasValue)
            {
                var p = ToFrame(hit.Value.point);
                if (m_GrabShape == null)
                {
                    Session.Set(m_GrabIndex, p, hit.Value.kind);
                }
                else
                {
                    m_GrabShape.Points[m_GrabIndex] = p;
                    m_GrabShape.Kinds[m_GrabIndex] = hit.Value.kind;
                    m_GrabShape.Measurement = MeasureMath.Measure(m_GrabShape.Points);
                    if (m_GrabShape.Survey != null && !m_GrabShape.Survey.Tape) SurveyMath.Size(m_GrabShape.Points, out m_GrabShape.Survey.W, out m_GrabShape.Survey.H);
                    ShowShape(m_GrabShape);
                }
            }
            if (m_GrabShape == null) { RefreshPreview(); return; }
            if (commit && m_GrabShape.Measurement.SelfIntersecting)
            {
                // Never save a crossed outline over a good one: the point goes back where it was.
                m_GrabShape.Points[m_GrabIndex] = m_GrabFrom;
                m_GrabShape.Kinds[m_GrabIndex] = m_GrabFromKind;
                m_GrabShape.Measurement = MeasureMath.Measure(m_GrabShape.Points);
                if (m_GrabShape.Survey != null && !m_GrabShape.Survey.Tape) SurveyMath.Size(m_GrabShape.Points, out m_GrabShape.Survey.W, out m_GrabShape.Survey.H);
                ShowShape(m_GrabShape);
                LastAction = $"move refused: edges cross (#{m_GrabShape.Entry?.Id})";
                AirTools.UI.InputHints.Say("measure.cross.move", MoveCrossHint);
                return;
            }
            if (commit)
            {
                FillEntry(m_GrabShape.Entry, m_GrabShape);
                Notebook.Update(m_GrabShape.Entry);
                LastAction = $"moved point {m_GrabIndex + 1} of #{m_GrabShape.Entry.Id}: {m_GrabShape.Entry.Label}";
                ShapeChanged?.Invoke(m_GrabShape);
            }
        }

        /// Flash the shape that produced this notebook entry (notebook "show").
        public bool Highlight(NotebookEntry entry, float seconds = 3f)
        {
            foreach (var s in m_Shapes)
                if (s.Entry == entry && s.View != null) { s.View.Pulse(seconds); return true; }
            return false;
        }

        // ---------------- helpers ----------------

        /// Snapped hit under a pointer. On a scene package with a structure layer this is the R6 snap policy with
        /// hysteresis (this tool's own memory), and the tape's second point locks to an axis or a nearby edge direction
        /// within 3° (SketchUp-style inference) unless it already sits on a corner or edge.
        public bool TryHit(Pose pointer, out SurfaceHit hit)
        {
            if (!SnapService.TryRaySnap(new Ray(pointer.position, pointer.forward), out hit, maxRayDistance, memory: m_SnapMemory)) return false;
            if (Session.Count == 1 && !m_Dragging) SnapService.TryAxisLock(FromFrame(Session.Points[0]), ref hit);
            return true;
        }

        readonly StructureSnapper.Memory m_SnapMemory = new StructureSnapper.Memory();

        /// The scene's calibration changed by `factor` (SceneRoot.Rescaled, "set scale from a known dimension"):
        /// stored points move with the scene, shapes are re-measured and their notebook entries updated.
        public void RescaleAll(float factor)
        {
            for (int i = 0; i < Session.Count; i++) Session.Set(i, Session.Points[i] * factor, Session.Kinds[i]);
            foreach (var s in m_Shapes)
            {
                for (int i = 0; i < s.Points.Length; i++) s.Points[i] *= factor;
                s.Measurement = MeasureMath.Measure(s.Points);
                if (s.Survey != null && !s.Survey.Tape) SurveyMath.Size(s.Points, out s.Survey.W, out s.Survey.H);
                ShowShape(s);
                if (s.Entry == null) continue;
                var time = s.Entry.Time;
                FillEntry(s.Entry, s);
                s.Entry.Time = time;
                Notebook.Update(s.Entry);
            }
            RefreshPreview();
        }

        /// World metres per scene metre (the frame's scale: 0.02 on the 1:50 tabletop). Click/grab radii are scene-sized.
        float WorldScale => frame != null ? Mathf.Max(frame.lossyScale.x, 1e-4f) : 1f;

        public Vector3 ToFrame(Vector3 world) => frame != null ? frame.InverseTransformPoint(world) : world;
        public Vector3 FromFrame(Vector3 local) => frame != null ? frame.TransformPoint(local) : local;

        void EnsureViewRoot()
        {
            if (viewRoot != null) return;
            var go = new GameObject("Measurements");
            if (frame != null) go.transform.SetParent(frame, false);
            viewRoot = go.transform;
        }

        MeasureLabel m_FinishChip;

        /// The on-cursor chip that tells you how to save a tape / shape in progress (UX W0.3): "Pinch your left hand
        /// to save" / "Press B to save". Shown with 2+ points whenever a finish is needed.
        public string FinishHint => !Equipped || Session.Count < MeasureMath.MinPoints || (AutoSaveTwoPointTapes && !AreaMode) ? ""
            : AirTools.Input.InputMode.Controllers ? "Press B to save" : "Pinch your left hand to save";

        void RefreshChip()
        {
            string hint = FinishHint;
            bool menuOpen = Services.TryGet<AirTools.Input.PalmMenu>(out var palm) && palm.IsOpen;
            m_ChipShown = !(string.IsNullOrEmpty(hint) || menuOpen || AppState.Mode == AppMode.Passthrough);
            if (!m_ChipShown)
            {
                if (m_FinishChip != null && m_FinishChip.gameObject.activeSelf) m_FinishChip.gameObject.SetActive(false);
                return;
            }
            if (m_FinishChip == null)
            {
                m_FinishChip = MeasureLabel.Create(viewRoot, style, "FinishChip");
                m_FinishChip.tabular = false;
                m_FinishChip.Priority = 3;
            }
            if (!m_FinishChip.gameObject.activeSelf) m_FinishChip.gameObject.SetActive(true);
            // Just under the cursor (or the last point): where you're looking.
            var at = Cursor.HasValue ? Cursor.Value.point : FromFrame(Session.Points[Session.Count - 1]);
            var cam = Camera.main;
            float dist = cam != null ? Vector3.Distance(cam.transform.position, at) : 1.5f;
            m_FinishChip.SetAnchorWorld(at - (cam != null ? cam.transform.up : Vector3.up) * (0.07f * Mathf.Max(dist, 0.5f)));
            m_FinishChip.Set(hint, 0.85f);
        }

        void RefreshPreview()
        {
            EnsureViewRoot();
            if (m_Preview == null) m_Preview = MeasureView.Create(viewRoot, style, "MeasurePreview");
            RefreshChip();
            TrackLiveTape();
            if (!Equipped || (Session.Count == 0 && !Cursor.HasValue))
            {
                m_Preview.Hide();
                return;
            }
            var pts = Session.LivePoints(m_Buffer);
            bool hasPreview = Session.Preview.HasValue && !Session.IsFull && Session.Count > 0;
            if (Session.Count == 0 && Cursor.HasValue)
            {
                // Just the snap cursor.
                pts.Clear();
                pts.Add(ToFrame(Cursor.Value.point));
                m_CursorKind[0] = Cursor.Value.kind;   // reused: this runs every frame
                m_Preview.Show(pts, m_CursorKind, hasPreview: false, preview: true);
                return;
            }
            // The agent's survey corners draw without labels (its doors get their W × H when saved); the user's live
            // tape shows what the pool gives it (the focus: its whole set).
            m_Preview.Show(pts, Session.Kinds, hasPreview, preview: true, AgentDriving ? LabelDetail.None : m_PreviewDetail);
        }

        readonly SnapKind[] m_CursorKind = new SnapKind[1];

        NotebookEntry BuildEntry(MeasureShape shape)
        {
            var e = new NotebookEntry("measure", 0, "m", null, DateTime.Now, -1, "");
            FillEntry(e, shape);
            return e;
        }

        void FillEntry(NotebookEntry e, MeasureShape s)
        {
            var m = s.Measurement;
            // The outline's corners in drawing order: side i runs from point i to i + 1, angle i sits at point i (click
            // order unless MeasureMath reordered a crossed quad; a distance's two extremes).
            var outline = new Vector3[m.OutlineCount];
            for (int k = 0; k < outline.Length; k++) outline[k] = s.Points[m.Outline[k]];
            e.Points = outline;
            e.Sides = m.Sides.ToArray();
            e.Angles = m.Angles.ToArray();
            e.PlanarityError = m.PlanarityError;
            e.Time = DateTime.Now;
            if (m.IsDistance)
            {
                e.ValueSI = m.Distance; e.Unit = "m";
                e.Label = $"Distance {Units.Format(m.Distance)}";
            }
            else
            {
                e.ValueSI = m.Area; e.Unit = "m²";
                var c = CultureInfo.InvariantCulture;
                string kind = m.OutlineCount == 3 ? "Triangle" : m.OutlineCount == 4 ? "Quad" : $"Polygon ({m.OutlineCount} sides)";
                e.Label = $"{kind} {Units.FormatArea(m.Area)} · sides {string.Join(", ", m.Sides.Select(x => x.ToString("0.000", c)))} m" +
                          $" · angles {string.Join(", ", m.Angles.Select(x => x.ToString("0.0", c)))}°";
            }
            var root = frame != null ? frame.GetComponent<SceneRoot>() : null;
            var cams = root != null ? root.CamerasInRootSpace() : null;
            // sitescope: a site coming back at another scale is re-measured before its package's cameras are in: keep the photo
            if (cams == null || cams.Length > 0 || e.NearestCameraId < 0) e.NearestCameraId = cams != null ? CameraEvidence.Nearest(e.Points, cams) : -1;
            var t = s.Survey;
            if (t != null && !t.Tape)
            {
                // "o5 cabinet_door · 262 × 279 mm · Quad …" (raw, for export and logs); the notebook shows W × H.
                string size = Units.PairMm(t.W, t.H);   // D2: the raw label stays mm
                e.Label = $"{t.ObjectId} {t.Label} · {size}{(t.Unverified ? " · unverified" : "")} · {e.Label}";
                e.DisplayTitle = $"{AirTools.UI.Copy.Cap(AirTools.UI.Copy.SurveyNoun(t.Label))} {t.ObjectId}";
                e.DisplayValue = AirTools.UI.Copy.WxH(t.W, t.H);   // D2: the row in the user's unit (Relabel re-words it)
                e.DisplayDetail = t.Unverified ? "a corner didn't lock on · check it" : "measured by the agent";
            }
            else if (t != null)
            {
                e.Label = $"{t.Label} · {e.Label}";
                e.DisplayTitle = AirTools.UI.Copy.Cap(t.Label);
            }
        }

        // ---------------- survey shapes (B1) ----------------

        /// Draw a shape: the user's with its full labels; a survey object with one compact W × H label (or none, when
        /// the label budget is spent), amber when a corner didn't snap.
        void ShowShape(MeasureShape s)
        {
            if (s.View == null) return;
            if (s.IsTape) { s.View.Show(s.Points, s.Kinds, hasPreview: false, preview: false, s.Labels); return; }
            s.View.ShowSurvey(s.Points, s.Kinds, AirTools.UI.Copy.WxH(s.Survey.W, s.Survey.H), s.Survey.Unverified, s.Survey.Focus, s.Labels != LabelDetail.None);
        }

        /// At most this many survey objects keep their label (UX W1.9: ≤ 12 labels readable at once); the rest show
        /// just their outline. Declutter S10: the survey is one P2 claim on WorldLabels' pool, so it gets what safety,
        /// the live task and a newer Grok layer leave, never more than this.
        public static int SurveyLabelBudget = 12;

        /// Pick which survey objects stay labelled: amber (unverified) first, then show_survey's focus, then the most
        /// recent; redraw the survey shapes. How many: the pool's share (WorldLabels).
        public void RefreshSurveyLabels()
        {
            m_ForceSurveyRedraw = true;
            LabelsChanged();
        }

        readonly List<MeasureShape> m_SurveyScratch = new List<MeasureShape>();
        readonly List<int> m_OrderScratch = new List<int>();
        bool m_ForceSurveyRedraw;

        int SurveyRank(int i) => m_SurveyScratch[i].Survey.Unverified ? 0 : m_SurveyScratch[i].Survey.Focus ? 1 : 2;

        int CompareSurvey(int a, int b)
        {
            int ra = SurveyRank(a), rb = SurveyRank(b);
            return ra != rb ? ra.CompareTo(rb) : b.CompareTo(a);
        }

        Comparison<int> m_CompareSurvey;

        /// Label the first `allowed` survey objects in rank order; redraw the ones whose label came or went (all of
        /// them after a focus change or a redo).
        void SelectSurveyLabels(int allowed)
        {
            m_SurveyScratch.Clear();
            foreach (var s in m_Shapes) if (s.Survey != null && !s.Survey.Tape) m_SurveyScratch.Add(s);
            m_OrderScratch.Clear();
            for (int i = 0; i < m_SurveyScratch.Count; i++) m_OrderScratch.Add(i);
            m_OrderScratch.Sort(m_CompareSurvey ??= CompareSurvey);
            int budget = Math.Min(SurveyLabelBudget, Math.Max(0, allowed));
            bool force = m_ForceSurveyRedraw;
            m_ForceSurveyRedraw = false;
            for (int k = 0; k < m_OrderScratch.Count; k++)
            {
                var s = m_SurveyScratch[m_OrderScratch[k]];
                var detail = k < budget ? LabelDetail.Full : LabelDetail.None;
                if (s.Labels == detail && !force) continue;
                s.Labels = detail;
                ShowShape(s);
            }
            m_SurveyScratch.Clear();
        }

        // ---------------- the label pool (declutter S10, DC7, docs/ux/declutter.md §5) ----------------

        /// Share the pool out again: this tool's labels appeared, went or changed count.
        void LabelsChanged() => AirTools.UI.WorldLabels.Changed(this);

        /// The shapes are drawn (hidden in passthrough, where viewRoot is off).
        bool LabelsLive => viewRoot == null || viewRoot.gameObject.activeInHierarchy;

        float ShapeTime(int i) => i < m_ShapeTimes.Count ? m_ShapeTimes[i] : EditHistory.Now;

        /// The focused shape counts as one claim of up to this many labels however many sides it has (§5.1).
        public const int FocusCost = 4;

        bool m_ChipShown;
        int m_LiveKey = -1;
        LabelDetail m_PreviewDetail = LabelDetail.Full;
        // What the last claim was about (ApplyLabels reads it back in the same share-out).
        bool m_ClaimedLive, m_ClaimedView;
        MeasureShape m_ClaimedFocus;
        int m_ClaimedFocusItems;

        /// Labels the tape in progress would show in full: the user's own (the agent's survey corners draw none), 1 for
        /// a first point and the cursor, else sides + angles + area with the cursor as the next corner. 0 = none.
        int LiveTapeLabels => !Equipped || AgentDriving || Session.Count == 0 ? 0 : Session.Count == 1 ? 1 : 2 * (Session.Count + 1) + 1;

        /// The pool hears when a tape starts or ends, grows past its first point, or the save chip comes or goes;
        /// never per frame (Tick runs this every frame and compares one int).
        void TrackLiveTape()
        {
            int key = Mathf.Min(LiveTapeLabels, FocusCost) * 2 + (m_ChipShown ? 1 : 0);
            if (key == m_LiveKey) return;
            m_LiveKey = key;
            LabelsChanged();
        }

        /// The focus when no tape is in progress: the newest of the user's shapes.
        int NewestTapeIndex()
        {
            for (int i = m_Shapes.Count - 1; i >= 0; i--) if (m_Shapes[i].IsTape) return i;
            return -1;
        }

        /// The shape whose every label shows (none while a tape is in progress: the live tape is the focus).
        public MeasureShape FocusShape
        {
            get
            {
                if (LiveTapeLabels > 0) return null;
                int i = NewestTapeIndex();
                return i >= 0 ? m_Shapes[i] : null;
            }
        }

        /// Three claims: the focus (P1: the live tape, else the newest shape; its whole set costs ≤ 4, the save chip
        /// counts too), the B1 survey (P2: one claim that keeps its unverified → focus → newest order), and the other
        /// shapes' summaries (P3, newest first).
        public void ClaimLabels(List<AirTools.UI.LabelClaim> claims)
        {
            m_ClaimedView = LabelsLive;
            int live = m_ClaimedView ? LiveTapeLabels : 0;
            m_ClaimedLive = live > 0;
            int focusIndex = m_ClaimedView && !m_ClaimedLive ? NewestTapeIndex() : -1;
            m_ClaimedFocus = focusIndex >= 0 ? m_Shapes[focusIndex] : null;
            m_ClaimedFocusItems = m_ClaimedLive ? live : m_ClaimedFocus != null ? MeasureView.FullLabelCount(m_ClaimedFocus.Measurement) : 0;
            float focusTime = m_ClaimedLive ? float.PositiveInfinity : focusIndex >= 0 ? ShapeTime(focusIndex) : float.NegativeInfinity;
            claims.Add(new AirTools.UI.LabelClaim(AirTools.UI.LabelClass.Focus, m_ClaimedView && m_ChipShown ? 1 : 0, m_ClaimedFocusItems, focusTime, FocusCost));

            int survey = 0, saved = 0;
            float newestSurvey = float.NegativeInfinity, newestSaved = float.NegativeInfinity;
            if (m_ClaimedView)
                for (int i = 0; i < m_Shapes.Count; i++)
                {
                    var s = m_Shapes[i];
                    if (!s.IsTape) { survey++; newestSurvey = Mathf.Max(newestSurvey, ShapeTime(i)); }
                    else if (s != m_ClaimedFocus) { saved++; newestSaved = Mathf.Max(newestSaved, ShapeTime(i)); }
                }
            claims.Add(new AirTools.UI.LabelClaim(AirTools.UI.LabelClass.Task, 0, survey, newestSurvey));
            claims.Add(new AirTools.UI.LabelClaim(AirTools.UI.LabelClass.Saved, 0, saved, newestSaved));
        }

        public void ApplyLabels(AirTools.UI.LabelGrant[] grants, int first)
        {
            if (!m_ClaimedView) return;   // hidden (passthrough): keep what's drawn for the way back
            int granted = grants[first].Items;
            var focus = m_ClaimedFocusItems == 0 || granted >= m_ClaimedFocusItems ? LabelDetail.Full : granted > 0 ? LabelDetail.Summary : LabelDetail.None;
            if (m_ClaimedLive) m_PreviewDetail = focus;
            SelectSurveyLabels(grants[first + 1].Items);
            int summaries = grants[first + 2].Items;
            for (int i = m_Shapes.Count - 1; i >= 0; i--)
            {
                var s = m_Shapes[i];
                if (!s.IsTape) continue;
                var detail = s == m_ClaimedFocus ? focus : summaries-- > 0 ? LabelDetail.Summary : LabelDetail.None;
                if (s.Labels == detail) continue;
                s.Labels = detail;
                ShowShape(s);
            }
        }

        /// show_survey's focus: highlight these object ids (pulse + keep their labels); others lose the focus.
        public int FocusSurvey(ICollection<string> objectIds, string requestId = null)
        {
            int n = 0;
            foreach (var s in m_Shapes)
            {
                if (s.Survey == null || s.Survey.Tape) continue;
                if (requestId != null && s.Survey.RequestId != requestId) continue;
                bool on = objectIds != null && objectIds.Contains(s.Survey.ObjectId);
                s.Survey.Focus = on;
                if (on) { n++; s.View?.Pulse(3f); }
            }
            RefreshSurveyLabels();
            return n;
        }

        /// Labels currently drawn by finished shapes (the harness logs it: the survey must stay readable).
        public int VisibleLabelCount
        {
            get
            {
                int n = 0;
                foreach (var s in m_Shapes) if (s.View != null && s.View.gameObject.activeInHierarchy) n += s.View.ActiveLabelCount;
                return n;
            }
        }

        // ---------------- D2: units (SPEC §9) ----------------

        /// The on-screen unit changed (UnitsSwitch): re-word every shape's labels — undone ones too, so a redo shows the
        /// new unit — and the survey rows' W × H. Nothing is re-measured; ValueSI and the raw Label don't change.
        public void Relabel()
        {
            void One(MeasureShape s)
            {
                if (s == null) return;
                if (s.Survey != null && !s.Survey.Tape && s.Entry != null) s.Entry.DisplayValue = AirTools.UI.Copy.WxH(s.Survey.W, s.Survey.H);
                ShowShape(s);
            }
            foreach (var s in m_Shapes) One(s);
            foreach (var op in m_Redo) { One(op.shape); if (op.group != null) foreach (var g in op.group) One(g); }
            foreach (var st in Sites.All)   // sitescope: parked sites' rows re-word too
            {
                foreach (var s in st.Shapes) One(s);
                foreach (var op in st.Redo) { One(op.shape); if (op.group != null) foreach (var g in op.group) One(g); }
            }
            RefreshPreview();
        }

        /// Drop the points of the shape in progress (an aborted survey object; never a finished shape).
        public void CancelSession()
        {
            if (Session.Count == 0) return;
            Session.Clear();
            m_PointTimes.Clear();
            LastAction = "session cancelled";
            RefreshPreview();
            EditHistory.NotifyChanged();
        }

        static void DestroyObj(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        // ---------------- sitescope: every tape belongs to the site it was made in ----------------

        /// One site's tapes and areas while another site is loaded: the shapes, the tape in progress, the undo / redo
        /// stacks (per-site: Undo on another scan never reaches them) and the views it hid.
        sealed class SiteState
        {
            public readonly List<MeasureShape> Shapes = new List<MeasureShape>();
            public readonly List<float> PointTimes = new List<float>();
            public readonly List<float> ShapeTimes = new List<float>();
            public readonly List<RedoOp> Redo = new List<RedoOp>();
            public readonly List<Vector3> Session = new List<Vector3>();
            public readonly List<SnapKind> SessionKinds = new List<SnapKind>();
            public readonly ParkedObjects Hidden = new ParkedObjects();
        }

        SiteShelf<SiteState> m_Sites;
        SiteShelf<SiteState> Sites => m_Sites ??= new SiteShelf<SiteState>(SiteScope.Current);

        /// The site the live shapes belong to.
        public string LiveSite => Sites.Live;
        /// Sites with tapes parked (the harness census).
        public int ParkedSites => Sites.Count;
        /// Shapes parked for `site` (0 for the live site or none).
        public int ParkedShapes(string site) => Sites.Peek(site)?.Shapes.Count ?? 0;

        /// Register for site changes and catch up with one missed while disabled (nothing live: just follow).
        public void RegisterSite()
        {
            SiteScope.Register(this, SiteScope.OrderMeasure);
            if (Sites.Live != SiteScope.Current) SwitchSite(Sites.Live, SiteScope.Current, 1f);
        }

        public void SwitchSite(string from, string to, float factor)
        {
            m_Sites ??= new SiteShelf<SiteState>(from);   // first switch: the live shapes are `from`'s
            if (string.IsNullOrEmpty(to) || to == Sites.Live) return;
            if (m_Dragging && m_GrabShape != null && m_GrabIndex >= 0) MoveGrabbed(null, commit: true);   // a point mid-drag: kept (or put back if it crosses)
            CancelPress();
            m_FinishHand = null;
            m_GrabShape = null;
            if (AgentDriving) { Session.Clear(); m_PointTimes.Clear(); AgentDriving = false; }   // a survey's corners: it stops with the site
            SiteState park = null;
            if (m_Shapes.Count > 0 || m_Redo.Count > 0 || Session.Count > 0)
            {
                park = new SiteState();
                park.Shapes.AddRange(m_Shapes);
                park.PointTimes.AddRange(m_PointTimes);
                park.ShapeTimes.AddRange(m_ShapeTimes);
                park.Redo.AddRange(m_Redo);
                park.Session.AddRange(Session.Points);
                park.SessionKinds.AddRange(Session.Kinds);
                foreach (var s in m_Shapes) park.Hidden.Hide(s.View);
                m_Shapes.Clear(); m_PointTimes.Clear(); m_ShapeTimes.Clear(); m_Redo.Clear();
                Session.Clear();
            }
            var back = Sites.Swap(to, park);
            LastPlaced = null;
            // switchclean: the snap cursor and the rubber band were on the scan being left: gone until the pointer finds the
            // arriving one (Tick); a measure_edges title asked for on that scan doesn't name a tape on this one.
            Cursor = null;
            Session.Preview = null;
            NextLabel = null;
            if (back != null)
            {
                m_Shapes.AddRange(back.Shapes);
                m_PointTimes.AddRange(back.PointTimes);
                m_ShapeTimes.AddRange(back.ShapeTimes);
                m_Redo.AddRange(back.Redo);
                for (int i = 0; i < back.Session.Count; i++) Session.Add(back.Session[i], back.SessionKinds[i]);
                back.Hidden.ShowAll();
                if (Mathf.Abs(factor - 1f) > 1e-6f)
                {
                    RescaleAll(factor);
                    ScaleRedo(factor);
                }
                else foreach (var s in m_Shapes) ShowShape(s);   // in the unit showing now
                m_ForceSurveyRedraw = true;
            }
            LastAction = $"site {to}";
            LabelsChanged();
            RefreshPreview();
            EditHistory.NotifyChanged();
        }

        /// Undone shapes and points are stored in SceneRoot space too: a site's calibration factor moves them as well.
        void ScaleRedo(float factor)
        {
            for (int i = 0; i < m_Redo.Count; i++)
            {
                var op = m_Redo[i];
                op.point *= factor;
                ScaleShape(op.shape, factor);
                if (op.group != null) foreach (var g in op.group) ScaleShape(g, factor);
                m_Redo[i] = op;
            }
        }

        void ScaleShape(MeasureShape s, float factor)
        {
            if (s == null) return;
            for (int i = 0; i < s.Points.Length; i++) s.Points[i] *= factor;
            s.Measurement = MeasureMath.Measure(s.Points);
            if (s.Survey != null && !s.Survey.Tape) SurveyMath.Size(s.Points, out s.Survey.W, out s.Survey.H);
            ShowShape(s);
        }

        public int ParkedCount => m_Sites?.Count ?? 0;

        public void ClearParked()
        {
            foreach (var st in Sites.TakeAll())
            {
                foreach (var s in st.Shapes)
                {
                    Notebook.Remove(s.Entry);
                    if (s.View != null) DestroyObj(s.View.gameObject);
                }
                foreach (var op in st.Redo)
                {
                    if (op.shape?.View != null) DestroyObj(op.shape.View.gameObject);
                    if (op.group != null) foreach (var g in op.group) if (g.View != null) DestroyObj(g.View.gameObject);
                }
                st.Hidden.Clear();
            }
        }
        // end sitescope
    }
}
