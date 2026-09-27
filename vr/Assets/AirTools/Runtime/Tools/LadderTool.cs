using System;
using System.Collections.Generic;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Structure;
using UnityEngine;

namespace AirTools.Tools
{
    /// A ladder setup: where it rests, its solve, what the scene says, and its view and notebook entry. Points are in
    /// the tool's frame (SceneRoot space).
    public class LadderPlacement
    {
        /// Where the rails rest on the support (pushed out past a gutter).
        public Vector3 Top;
        /// The support edge point the ladder was aimed at (the height the extension is measured from).
        public Vector3 Edge;
        /// Horizontal, off the support face.
        public Vector3 Outward;
        public float GroundY;
        public string SupportName;
        public LadderSolution Solution;
        public LadderChecks Checks;
        public LadderReport Report;
        public NotebookEntry Entry;
        public LadderView View;

        public Vector3 Foot => LadderMath.FootPoint(Top, Outward, Solution.FootOut, GroundY);
        public string Summary => LadderMath.Summary(Solution, Report);

        public LadderPlacement Copy() => (LadderPlacement)MemberwiseClone();
    }

    /// Ladder check (presence.md S4, P6): aim at the top support (gutter, eave or fascia; the sill for window work) and
    /// pinch: a true-size extension ladder stands at the OSHA 4:1 angle with its foot on the ground, square to the
    /// wall, sized for the reach (3 ft past a roof edge), green / amber / red. Press on its foot and drag to move it;
    /// the ladder re-solves live and shows the ratio. Every placement is a notebook entry ("ladder", ValueSI = the
    /// working length); placing and moving are undoable (EditHistory). Patterned on LevelTool: live ghost while aimed.
    public class LadderTool : MonoBehaviour, IEditable, ISiteScoped   // sitescope: ISiteScoped
    {
        [Tooltip("Scene root: ladders are solved relative to its up axis and stored in its space.")]
        public Transform frame;
        public Transform viewRoot;
        public MeasureStyle style = new MeasureStyle();
        [Tooltip("Transparent unlit material for the glass ladder (tinted per verdict).")]
        public Material material;
        public float maxRayDistance = 40f;
        [Tooltip("A known support edge within this distance of the ray (scene m) takes the ladder.")]
        public float supportMagnet = 0.25f;
        /// Supports lower than this above the ground (scene m) aren't ladder work.
        public const float MinSupportHeight = 0.5f;
        [Tooltip("A press whose ray meets the ground this close to a ladder's foot (scene m) drags the foot.")]
        public float footGrabRadius = 0.45f;
        [Tooltip("Placing a ladder turns the fall-edge overlay on (P7); putting the tool away turns it off again.")]
        public bool showFallEdgesOnPlace = true;

        public bool Equipped { get; private set; }
        /// The ladder under the pointer while aiming (not placed).
        public LadderPlacement Live { get; private set; }
        public IReadOnlyList<LadderPlacement> Placements => m_Placements;
        public LadderPlacement Last => m_Placements.Count > 0 ? m_Placements[m_Placements.Count - 1] : null;
        public LadderPlacement Dragging => m_Drag;
        public string LastAction { get; private set; } = "";
        /// A ladder was placed, moved (live while dragging), undone or redone; null when the last one went away.
        public event Action<LadderPlacement> Changed;

        enum EditKind { Place, Move }
        /// fromD / toD: the foot's distance out from the aimed edge before / after a move.
        struct Edit { public EditKind kind; public LadderPlacement p; public double fromD, toD; public float at; }

        readonly List<LadderPlacement> m_Placements = new List<LadderPlacement>();
        readonly List<Edit> m_Edits = new List<Edit>();
        readonly List<Edit> m_Redo = new List<Edit>();
        IToolInput m_Input;
        LadderView m_Ghost;
        LadderPlacement m_Drag;
        double m_DragFrom;
        Vector3 m_LastAimEdge;
        bool m_HasAim;

        float Scale => frame != null ? Mathf.Max(frame.lossyScale.x, 1e-6f) : 1f;
        Vector3 Up => frame != null ? frame.up : Vector3.up;

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

        public void Equip(bool on)
        {
            if (Equipped == on) return;
            Equipped = on;
            if (!on)
            {
                Live = null;
                m_HasAim = false;
                if (m_Drag != null) EndDrag();
                if (Services.TryGet<FallEdges>(out var edges)) edges.HideIfAutoShown();
            }
            RefreshGhost();
            if (on && Application.isPlaying && AppState.Mode == AppMode.World)
                AirTools.UI.UiToast.Show($"Ladder: aim at the gutter or a sill and {AirTools.UI.Copy.Pinch(InputMode.Controllers)}", AirTools.UI.ColorRole.Info);
            Log.Info($"Ladder tool {(on ? "equipped" : "put away")}");
        }

        void Update()
        {
            if (Equipped && m_Input != null && m_Drag == null) Tick();
        }

        /// Per-frame: the ladder under the pointer (tests call this directly).
        public void Tick()
        {
            if (m_Input == null) return;
            var hand = m_Input.LastActiveHand;
            // No ghost while the pointer is on a ladder's foot (a press there drags it).
            if (m_Input.HasPointer(hand) && FootUnder(m_Input.GetPointer(hand), out _) == null
                && TryPlan(m_Input.GetPointer(hand), out var plan, reuseAim: true)) Live = plan;
            else { Live = null; m_HasAim = false; }
            RefreshGhost();
        }

        // ---------------- input ----------------

        void OnPressStart(ToolHand hand, Pose pointer)
        {
            if (!Equipped) return;
            var p = FootUnder(pointer, out _);
            if (p == null) return;
            m_Drag = p;
            m_DragFrom = FootFromEdge(p);
            if (m_Ghost != null) m_Ghost.Hide();
            LastAction = "drag foot";
        }

        void OnPressMove(ToolHand hand, Pose pointer)
        {
            if (!Equipped || m_Drag == null) return;
            if (!GroundPoint(pointer, m_Drag.GroundY, out var g)) return;
            Resolve(m_Drag, LadderMath.FootOutFor(m_Drag.Edge, m_Drag.Outward, g), live: true);
        }

        void OnPressEnd(ToolHand hand, Pose pointer)
        {
            if (!Equipped) return;
            if (m_Drag != null)
            {
                if (GroundPoint(pointer, m_Drag.GroundY, out var g)) Resolve(m_Drag, LadderMath.FootOutFor(m_Drag.Edge, m_Drag.Outward, g), live: true);
                EndDrag();
                return;
            }
            if (TryPlan(pointer, out var plan, reuseAim: false)) Place(plan);
            else
            {
                LastAction = "miss";
                AirTools.UI.InputHints.Say("ladder.miss", "Point at the gutter, the roof edge or a sill");
            }
        }

        void OnButton(ToolHand hand, ToolButton button)
        {
            if (!Equipped) return;
            if (button == ToolButton.Undo) Undo();
            else if (button == ToolButton.Redo) Redo();
            else if (button == ToolButton.Clear) ClearAll();
        }

        void EndDrag()
        {
            var p = m_Drag;
            m_Drag = null;
            if (p == null) return;
            double to = FootFromEdge(p);
            if (Math.Abs(to - m_DragFrom) < 0.001) { LastAction = "foot unchanged"; return; }
            UpdateEntry(p);
            m_Edits.Add(new Edit { kind = EditKind.Move, p = p, fromD = m_DragFrom, toD = to, at = EditHistory.Stamp() });
            DropRedo();
            EditHistory.Edited(this);
            LastAction = $"moved foot: {p.Summary}";
            AirTools.UI.FeedbackEvents.PartSeated(frame != null ? frame.TransformPoint(p.Foot) : p.Foot, FitFor(p.Report.Verdict));
        }

        // ---------------- solving ----------------

        /// The ladder for a pointer ray (frame space result). A known support edge near the ray wins (the eave, a sill);
        /// otherwise the snapped surface hit, with a landing if there's a walkable surface behind it.
        public bool TryPlan(Pose pointer, out LadderPlacement plan, bool reuseAim = false)
        {
            plan = null;
            var worldRay = new Ray(pointer.position, pointer.forward);
            if (!TrySupport(worldRay, out var edge, out var outward, out var support, out var name)) return false;
            if (reuseAim && m_HasAim && Live != null && (edge - m_LastAimEdge).sqrMagnitude < 1e-4f) { plan = Live; return true; }
            m_LastAimEdge = edge;
            m_HasAim = true;

            var p = new LadderPlacement { Edge = edge, Outward = outward, SupportName = name };
            float floor = FloorY();
            p.Top = ToFrame(LadderProbe.Clearance(ToWorld(edge), ToWorldDir(outward), Up, Scale, maxDrop: MaxProbeDrop(edge.y, floor)));
            // Ground under the 4:1 foot: guess from the scene floor, probe, re-solve once for the real ground.
            float groundY = float.IsNaN(floor) ? p.Top.y - 3f : floor;
            p.Checks = LadderChecks.Clear;
            for (int i = 0; i < 2; i++)
            {
                double h = Math.Max(0.3, p.Top.y - groundY);
                var foot = LadderMath.FootPoint(p.Top, outward, LadderMath.FootDistance(h), groundY);
                if (LadderProbe.Ground(ToWorld(foot), Up, Scale, out var g, out float slope))
                {
                    groundY = ToFrame(g).y;
                    p.Checks.GroundFound = true;
                    p.Checks.FootingDeg = slope;
                }
                else { p.Checks.GroundFound = false; p.Checks.FootingDeg = 0; }
            }
            p.GroundY = groundY;
            if (p.Top.y - groundY < MinSupportHeight) return false;   // the ground, a step: not a ladder job
            p.Solution = LadderMath.Solve(p.Top.y - groundY, support);
            CheckCollision(p);
            p.Report = LadderMath.Evaluate(p.Solution, p.Checks);
            plan = p;
            return true;
        }

        /// Re-solve a ladder with its foot `footFromEdge` out from the aimed edge (dragging, undo / redo of a move): the
        /// ground is probed at the foot, the top pivots out on the same lip for the new lean (clearance), and the
        /// angle, lengths, size and verdict re-solve. The foot stays exactly where it was put.
        void Resolve(LadderPlacement p, double footFromEdge, bool live)
        {
            var footPos = LadderMath.FootPoint(p.Edge, p.Outward, Math.Max(0.05, footFromEdge), p.GroundY);
            if (LadderProbe.Ground(ToWorld(footPos), Up, Scale, out var g, out float slope))
            {
                p.GroundY = ToFrame(g).y;
                p.Checks.GroundFound = true;
                p.Checks.FootingDeg = slope;
            }
            else { p.Checks.GroundFound = false; p.Checks.FootingDeg = 0; }
            footPos.y = p.GroundY;
            double h = Math.Max(0.3, p.Edge.y - p.GroundY);
            double d = LadderMath.FootOutFor(p.Top, p.Outward, footPos);
            for (int i = 0; i < 2; i++)
            {
                p.Top = ToFrame(LadderProbe.Clearance(ToWorld(p.Edge), ToWorldDir(p.Outward), Up, Scale,
                    runPerRise: (float)(d / h), maxDrop: MaxProbeDrop(p.Edge.y, p.GroundY)));
                d = LadderMath.FootOutFor(p.Top, p.Outward, footPos);
            }
            p.Solution = LadderMath.SolveFoot(h, d, p.Solution.Support);
            CheckCollision(p);
            p.Report = LadderMath.Evaluate(p.Solution, p.Checks);
            ShowView(p, ghost: false);
            if (live) Changed?.Invoke(p);
        }

        void CheckCollision(LadderPlacement p)
        {
            bool hit = LadderProbe.Collides(ToWorld(p.Foot), ToWorld(p.Top), ToWorldDir(p.Outward), Up, Scale, out var with);
            p.Checks.Collision = hit;
            p.Checks.CollisionWith = hit ? LadderProbe.Name(with) : null;
        }

        bool TrySupport(Ray worldRay, out Vector3 edge, out Vector3 outward, out LadderSupport support, out string name)
        {
            edge = default; outward = default; support = LadderSupport.Wall; name = null;
            Services.TryGet<SceneRoot>(out var root);
            var o = ToFrame(worldRay.origin);
            var d = frame != null ? frame.InverseTransformDirection(worldRay.direction) : worldRay.direction;
            if (root != null && frame != null && root.transform == frame)
            {
                var candidates = SupportEdges.Candidates(root);
                int i = FallEdgeMath.NearestToRay(candidates, o, d, supportMagnet, out var point, out _);
                if (i >= 0)
                {
                    var c = candidates[i];
                    edge = point;
                    outward = c.Outward;
                    support = c.Support;
                    name = SupportEdges.IsSyntheticFacade(root) ? SurfaceNames.FromPath(c.Name) : c.Support == LadderSupport.Landing ? "the roof edge" : "the edge";
                    return true;
                }
            }
            if (!SnapService.TryRaySnap(worldRay, out var hit, maxRayDistance, features: true)) return false;
            var n = ToFrameDir(hit.normal);
            // A ladder leans on a face or an edge, not the middle of a floor or roof.
            if (Mathf.Abs(n.y) >= 0.7f && hit.kind != SnapKind.Edge && hit.kind != SnapKind.Corner) return false;
            edge = ToFrame(hit.point);
            outward = Mathf.Abs(n.y) < 0.7f ? LadderMath.Horizontal(n) : LadderMath.Horizontal(o - edge);
            if (outward.sqrMagnitude < 0.5f) return false;
            name = SurfaceNames.ForPoint(hit.point, hit.normal, hit.collider);
            bool landing = LadderProbe.HasLanding(hit.point, ToWorldDir(outward), Up, Scale);
            support = landing ? LadderSupport.Landing
                : name != null && name.IndexOf("gutter", StringComparison.OrdinalIgnoreCase) >= 0 ? LadderSupport.GutterNoLanding
                : LadderSupport.Wall;
            return true;
        }

        // ---------------- placing ----------------

        public LadderPlacement Place(LadderPlacement plan)
        {
            var p = plan.Copy();
            var s = p.Solution;
            var entry = new NotebookEntry("ladder", s.ContactLength, "m", new[] { p.Top, p.Foot }, DateTime.Now, -1, p.Summary);
            Describe(entry, p);
            if (frame != null && frame.GetComponent<SceneRoot>() is SceneRoot root)
                entry.NearestCameraId = CameraEvidence.Nearest(entry.Points, root.CamerasInRootSpace());
            Notebook.Add(entry);
            p.Entry = entry;
            EnsureViewRoot();
            p.View = LadderView.Create(viewRoot, material, style, $"Ladder{m_Placements.Count + 1}");
            ShowView(p, ghost: false);
            m_Placements.Add(p);
            m_Edits.Add(new Edit { kind = EditKind.Place, p = p, at = EditHistory.Stamp() });
            DropRedo();
            EditHistory.Edited(this);
            LastAction = $"placed #{entry.Id}: {p.Summary}";
            Live = null;
            m_HasAim = false;
            RefreshGhost();
            AirTools.UI.InputHints.Succeeded();
            AirTools.UI.FeedbackEvents.PartSeated(frame != null ? frame.TransformPoint(p.Top) : p.Top, FitFor(p.Report.Verdict));
            if (showFallEdgesOnPlace && Services.TryGet<FallEdges>(out var edges)) edges.ShowForLadder();
            Changed?.Invoke(p);
            return p;
        }

        static void Describe(NotebookEntry e, LadderPlacement p)
        {
            var s = p.Solution;
            e.Label = p.Summary;
            e.ValueSI = s.ContactLength;
            e.Points = new[] { p.Top, p.Foot };
            e.DisplayTitle = s.SizeFt > 0 ? $"Ladder · {s.SizeFt} ft" : "Ladder";
            e.DisplayValue = $"{LadderMath.Glyph(p.Report.Verdict)} {s.AngleDeg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}°";
            e.DisplayDetail = $"foot {AirTools.UI.Copy.Len(s.FootOut)} out" + (string.IsNullOrEmpty(p.SupportName) ? "" : $" · {p.SupportName}");   // D2
        }

        void UpdateEntry(LadderPlacement p)
        {
            if (p.Entry == null) return;
            Describe(p.Entry, p);
            Notebook.Update(p.Entry);
        }

        // ---------------- undo / redo ----------------

        public bool CanUndo => m_Edits.Count > 0;
        public bool CanRedo => m_Redo.Count > 0;
        public float LastEditAt => m_Edits.Count > 0 ? m_Edits[m_Edits.Count - 1].at : float.NegativeInfinity;
        public float LastUndoAt => m_Redo.Count > 0 ? m_Redo[m_Redo.Count - 1].at : float.NegativeInfinity;

        public void Undo()
        {
            if (m_Edits.Count == 0) return;
            if (m_Drag != null) m_Drag = null;
            var e = m_Edits[m_Edits.Count - 1];
            m_Edits.RemoveAt(m_Edits.Count - 1);
            if (e.kind == EditKind.Place)
            {
                m_Placements.Remove(e.p);
                Notebook.Remove(e.p.Entry);
                if (e.p.View != null) e.p.View.gameObject.SetActive(false);
                LastAction = "undo ladder";
                Changed?.Invoke(Last);
            }
            else
            {
                Resolve(e.p, e.fromD, live: false);
                UpdateEntry(e.p);
                LastAction = $"undo move: {e.p.Summary}";
                Changed?.Invoke(e.p);
            }
            e.at = EditHistory.Stamp();
            m_Redo.Add(e);
            EditHistory.NotifyChanged();
        }

        public void Redo()
        {
            if (m_Redo.Count == 0) return;
            var e = m_Redo[m_Redo.Count - 1];
            m_Redo.RemoveAt(m_Redo.Count - 1);
            if (e.kind == EditKind.Place)
            {
                m_Placements.Add(e.p);
                Notebook.Restore(e.p.Entry);
                if (e.p.View != null) e.p.View.gameObject.SetActive(true);
                LastAction = $"redo ladder: {e.p.Summary}";
            }
            else
            {
                Resolve(e.p, e.toD, live: false);
                UpdateEntry(e.p);
                LastAction = $"redo move: {e.p.Summary}";
            }
            e.at = EditHistory.Stamp();
            m_Edits.Add(e);
            Changed?.Invoke(e.p);
            EditHistory.NotifyChanged();
        }

        public void DropRedo()
        {
            foreach (var e in m_Redo)
                if (e.kind == EditKind.Place && e.p.View != null && !m_Placements.Contains(e.p)) DestroyObj(e.p.View.gameObject);
            m_Redo.Clear();
        }

        public void ClearAll()
        {
            m_Drag = null;
            foreach (var p in m_Placements)
            {
                Notebook.Remove(p.Entry);
                if (p.View != null) DestroyObj(p.View.gameObject);
            }
            m_Placements.Clear();
            m_Edits.Clear();
            DropRedo();
            LastAction = "clear";
            Changed?.Invoke(null);
            EditHistory.NotifyChanged();
        }

        /// The scene's calibration changed by `factor`: ladders move with the scene and re-solve at the new heights.
        public void RescaleAll(float factor)
        {
            foreach (var p in m_Placements)
            {
                double foot = FootFromEdge(p) * factor;
                p.Top *= factor;
                p.Edge *= factor;
                p.GroundY *= factor;
                Resolve(p, foot, live: false);
                UpdateEntry(p);
            }
            m_HasAim = false;
        }

        /// Pulse the ladder that produced this entry (notebook "show").
        public bool Highlight(NotebookEntry entry, float seconds = 3f)
        {
            foreach (var p in m_Placements)
                if (p.Entry == entry && p.View != null) { p.View.Pulse(seconds); return true; }
            return false;
        }

        // ---------------- view ----------------

        void ShowView(LadderPlacement p, bool ghost)
        {
            var view = ghost ? m_Ghost : p.View;
            if (view == null) return;
            var s = p.Solution;
            float overlap = s.SizeFt > 0 ? LadderMath.OverlapFt(s.SizeFt) * (float)LadderMath.MetresPerFoot : 0.914f;
            view.Show(p.Foot, p.Top, p.Outward, (float)s.RailNeeded, overlap, p.Report.Verdict, LabelFor(p, ghost), ghost);
        }

        /// World label: size + verdict, then foot / angle / extension, and the ratio line when it isn't green. Lengths in
        /// the user's unit (D2); ladder sizes stay in feet (they're sold that way).
        public static string LabelFor(LadderPlacement p, bool ghost = false)
        {
            var s = p.Solution; var r = p.Report;
            var c = System.Globalization.CultureInfo.InvariantCulture;
            string head = $"{LadderMath.Glyph(r.Verdict)} {LadderMath.SizeName(s.SizeFt)}";
            if (ghost) return head;
            string line2 = $"foot {AirTools.UI.Copy.Len(s.FootOut)} out · {s.AngleDeg.ToString("0.0", c)}°";
            if (s.Support == LadderSupport.Landing) line2 += $" · {AirTools.UI.Copy.Len(s.AboveEdge)} above the edge";
            string text = head + "\n" + line2;
            if (r.Verdict != LadderVerdict.Green) text += "\n" + AirTools.UI.Copy.Lengths(r.Reason);
            return text;
        }

        /// D2: the unit changed — re-word every ladder's label and notebook row (nothing re-solved).
        public void Relabel()
        {
            void One(LadderPlacement p)
            {
                if (p == null) return;
                bool hidden = p.View != null && !p.View.gameObject.activeSelf;   // an undone ladder stays hidden
                ShowView(p, false);
                if (hidden) p.View.gameObject.SetActive(false);
                if (p.Entry != null) Describe(p.Entry, p);
            }
            foreach (var p in m_Placements) One(p);
            foreach (var e in m_Redo) if (e.kind == EditKind.Place) One(e.p);
            RefreshGhost();
        }

        void RefreshGhost()
        {
            EnsureViewRoot();
            if (m_Ghost == null)
            {
                m_Ghost = LadderView.Create(viewRoot, material, style, "LadderGhost");
                m_Ghost.Hide();
            }
            if (Equipped && m_Drag == null && Live != null) ShowView(Live, ghost: true);
            else m_Ghost.Hide();
        }

        void EnsureViewRoot()
        {
            if (viewRoot != null) return;
            var go = new GameObject("Ladders");
            if (frame != null) go.transform.SetParent(frame, false);
            viewRoot = go.transform;
        }

        // ---------------- helpers ----------------

        /// The placement whose foot the pointer ray meets the ground near (nearest first).
        LadderPlacement FootUnder(Pose pointer, out float distance)
        {
            LadderPlacement best = null;
            distance = footGrabRadius;
            foreach (var p in m_Placements)
            {
                if (!GroundPoint(pointer, p.GroundY, out var g)) continue;
                float d = Vector3.Distance(new Vector3(g.x, 0f, g.z), new Vector3(p.Foot.x, 0f, p.Foot.z));
                if (d <= distance) { best = p; distance = d; }
            }
            return best;
        }

        /// Where a pointer ray meets the horizontal plane at frame height y (frame space).
        bool GroundPoint(Pose pointer, float y, out Vector3 point)
        {
            point = default;
            var o = ToFrame(pointer.position);
            var d = frame != null ? frame.InverseTransformDirection(pointer.forward) : pointer.forward;
            if (Mathf.Abs(d.y) < 1e-4f) return false;
            float t = (y - o.y) / d.y;
            if (t <= 0f) return false;
            point = o + d * t;
            return true;
        }

        /// The foot's distance out from the aimed edge (what a drag moves and an undo restores).
        static double FootFromEdge(LadderPlacement p) => LadderMath.FootOutFor(p.Edge, p.Outward, p.Foot);

        /// Clearance probes stay 15 cm above the ground (NaN floor: the full 1.2 m).
        static float MaxProbeDrop(float edgeY, float groundY) => float.IsNaN(groundY) ? 1.2f : Mathf.Max(0.05f, edgeY - groundY - 0.15f);

        float FloorY()
        {
            if (frame == null || !(frame.GetComponent<SceneRoot>() is SceneRoot root)) return float.NaN;
            return SurfaceNames.FloorY(root);
        }

        static FitStatus FitFor(LadderVerdict v) => v == LadderVerdict.Green ? FitStatus.Green : v == LadderVerdict.Amber ? FitStatus.Amber : FitStatus.Red;

        Vector3 ToFrame(Vector3 world) => frame != null ? frame.InverseTransformPoint(world) : world;
        Vector3 ToFrameDir(Vector3 world) => frame != null ? frame.InverseTransformDirection(world) : world;
        Vector3 ToWorld(Vector3 local) => frame != null ? frame.TransformPoint(local) : local;
        Vector3 ToWorldDir(Vector3 local) => frame != null ? frame.TransformDirection(local) : local;

        static void DestroyObj(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        // ---------------- sitescope: every ladder belongs to the site it was placed on ----------------

        /// One site's ladders while another site is loaded: placements, undo / redo stacks and the views it hid.
        sealed class SiteState
        {
            public readonly List<LadderPlacement> Placements = new List<LadderPlacement>();
            public readonly List<Edit> Edits = new List<Edit>();
            public readonly List<Edit> Redo = new List<Edit>();
            public UnitSystem Units;
            public readonly ParkedObjects Hidden = new ParkedObjects();
        }

        SiteShelf<SiteState> m_Sites;
        SiteShelf<SiteState> Sites => m_Sites ??= new SiteShelf<SiteState>(SiteScope.Current);

        public string LiveSite => Sites.Live;
        public int ParkedPlacements(string site) => Sites.Peek(site)?.Placements.Count ?? 0;

        public void RegisterSite()
        {
            SiteScope.Register(this, SiteScope.OrderLadder);
            if (Sites.Live != SiteScope.Current) SwitchSite(Sites.Live, SiteScope.Current, 1f);
        }

        /// A site that comes back at another scale re-solves its ladders (RescaleAll probes the scene it's on); at the
        /// same scale they come back untouched, solutions and all.
        public void SwitchSite(string from, string to, float factor)
        {
            m_Sites ??= new SiteShelf<SiteState>(from);
            if (string.IsNullOrEmpty(to) || to == Sites.Live) return;
            if (m_Drag != null) EndDrag();   // a foot mid-drag: the move so far is its edit (undoable on its site)
            Live = null;
            m_HasAim = false;
            SiteState park = null;
            if (m_Placements.Count > 0 || m_Edits.Count > 0 || m_Redo.Count > 0)
            {
                park = new SiteState { Units = AirTools.UI.UiSettings.UnitSystem };
                park.Placements.AddRange(m_Placements);
                park.Edits.AddRange(m_Edits);
                park.Redo.AddRange(m_Redo);
                foreach (var p in m_Placements) park.Hidden.Hide(p.View);
                m_Placements.Clear(); m_Edits.Clear(); m_Redo.Clear();
            }
            var back = Sites.Swap(to, park);
            if (back != null)
            {
                m_Placements.AddRange(back.Placements);
                m_Edits.AddRange(back.Edits);
                m_Redo.AddRange(back.Redo);
                back.Hidden.ShowAll();
                if (Mathf.Abs(factor - 1f) > 1e-6f) ScaleParked(factor);
                if (back.Units != AirTools.UI.UiSettings.UnitSystem) Relabel();   // the unit changed while it was parked
            }
            RefreshGhost();
            LastAction = $"site {to}";
            Changed?.Invoke(Last);
            EditHistory.NotifyChanged();
        }

        public int ParkedCount => m_Sites?.Count ?? 0;

        /// A site back at another scale: its ladders scale with the scene and re-solve on the stored geometry, without the
        /// clearance probes (the arriving scan's collider isn't in yet; a drag or a re-aim probes again). Undone ladders and
        /// the distances a move's undo restores scale too.
        void ScaleParked(float factor)
        {
            void One(LadderPlacement p)
            {
                if (p == null) return;
                double foot = FootFromEdge(p) * factor;
                p.Top *= factor;
                p.Edge *= factor;
                p.GroundY *= factor;
                var footPos = LadderMath.FootPoint(p.Edge, p.Outward, Math.Max(0.05, foot), p.GroundY);
                double h = Math.Max(0.3, p.Edge.y - p.GroundY);
                p.Solution = LadderMath.SolveFoot(h, LadderMath.FootOutFor(p.Top, p.Outward, footPos), p.Solution.Support);
                p.Report = LadderMath.Evaluate(p.Solution, p.Checks);
                bool hidden = p.View != null && !p.View.gameObject.activeSelf;
                ShowView(p, ghost: false);
                if (hidden) p.View.gameObject.SetActive(false);
                UpdateEntry(p);
            }
            var done = new HashSet<LadderPlacement>();
            foreach (var p in m_Placements) if (done.Add(p)) One(p);
            foreach (var e in m_Redo) if (e.kind == EditKind.Place && done.Add(e.p)) One(e.p);
            for (int i = 0; i < m_Edits.Count; i++) { var e = m_Edits[i]; e.fromD *= factor; e.toD *= factor; m_Edits[i] = e; }
            for (int i = 0; i < m_Redo.Count; i++) { var e = m_Redo[i]; e.fromD *= factor; e.toD *= factor; m_Redo[i] = e; }
            m_HasAim = false;
        }

        public void ClearParked()
        {
            foreach (var st in Sites.TakeAll())
            {
                foreach (var p in st.Placements)
                {
                    Notebook.Remove(p.Entry);
                    if (p.View != null) DestroyObj(p.View.gameObject);
                }
                foreach (var e in st.Redo) if (e.kind == EditKind.Place && e.p.View != null && !st.Placements.Contains(e.p)) DestroyObj(e.p.View.gameObject);
                st.Hidden.Clear();
            }
        }
        // end sitescope
    }
}
