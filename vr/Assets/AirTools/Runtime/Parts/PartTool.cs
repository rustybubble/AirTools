using System;
using System.Collections.Generic;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Scene;
using AirTools.Tools;
using UnityEngine;

namespace AirTools.Parts
{
    /// Parts in hand (SPEC M4). A part taken from a candidate card rides the pointer; over a surface it previews
    /// seated there with a live fit outline. Trigger/pinch release places it (snap + click + haptic), runs the fit
    /// check against the nearest tape reading and logs it. Press on a placed part to pick it up again.
    /// Undo removes the last placed part; Clear removes all. delete-undo: removing a placed part (the spec card's Remove,
    /// the context menu's Delete, voice, a model swapped out of a gap) and Clear are undoable steps too (Delete, RemoveAll).
    public class PartTool : MonoBehaviour, AirTools.Tools.IEditable, ISiteScoped   // sitescope: ISiteScoped
    {
        [Tooltip("Parts are parented here (the scene root) so they stay put on the scene.")]
        public Transform frame;
        public MeasureStyle style = new MeasureStyle();
        [Tooltip("How far in front of the pointer a held part rides (m).")]
        public float holdDistance = 0.15f;
        [Tooltip("Release within this distance of a surface snaps onto it (m).")]
        public float snapRadius = 0.15f;
        public float maxRayDistance = 40f;
        public float evaluateInterval = 0.1f;

        public bool Equipped { get; private set; }
        public PartInstance Held { get; private set; }
        public PartInstance Selected { get; private set; }
        public IReadOnlyList<PartInstance> PlacedParts => m_Placed;
        /// While held: the part is previewing on a surface (vs riding in the hand).
        public bool OnSurface { get; private set; }
        public string LastAction { get; private set; } = "";
        public event Action<PartInstance> SelectionChanged;
        public event Action<PartInstance> PartPlaced;

        readonly List<PartInstance> m_Placed = new List<PartInstance>();
        readonly Dictionary<PartInstance, NotebookEntry> m_Entries = new Dictionary<PartInstance, NotebookEntry>();
        /// Undo history: a PartInstance (one placement) or an ArrayGroup (a whole array, undone in one step).
        readonly List<object> m_History = new List<object>();
        readonly List<float> m_HistoryTimes = new List<float>();
        /// Redo history: a PartInstance (an undone placement, hidden) or an ArrayGroup (an undone array, clones hidden).
        readonly List<(object op, float at)> m_Redo = new List<(object, float)>();

        /// An array placed from a reference part: the clones, the reference's pose before (and in) the array, and its
        /// notebook entry. Poses are frame-local, so undo / redo put things back right at tabletop scale too.
        public class ArrayGroup
        {
            public PartInstance Reference;
            public Pose ReferencePose;
            public Pose ReferenceArrayPose;
            public bool HasArrayPose;
            public List<PartInstance> Clones = new List<PartInstance>();
            public NotebookEntry Entry;
            public ArrayPlan Plan;
            /// The tape the run was measured with (its notebook id; 0 = none): the checkout's quantity evidence.
            public int TapeEntryId;
        }

        public ArrayGroup LastArray { get; private set; }
        IToolInput m_Input;
        float m_NextEvaluate;

        void OnEnable()
        {
            Services.Register(this);
            AirTools.Tools.EditHistory.Register(this);
            if (m_Input == null && Services.TryGet<IToolInput>(out var input)) SetInput(input);
            RegisterSite();   // sitescope
        }

        void OnDisable()
        {
            SetInput(null);
            Services.Unregister(this);
            AirTools.Tools.EditHistory.Unregister(this);
            SiteScope.Unregister(this);   // sitescope
        }

        void Start()
        {
            if (m_Input == null && Services.TryGet<IToolInput>(out var input)) SetInput(input);
        }

        public void SetInput(IToolInput input)
        {
            if (m_Input != null) { m_Input.PressStart -= OnPressStart; m_Input.PressEnd -= OnPressEnd; m_Input.ButtonDown -= OnButton; }
            m_Input = input;
            if (m_Input != null) { m_Input.PressStart += OnPressStart; m_Input.PressEnd += OnPressEnd; m_Input.ButtonDown += OnButton; }
        }

        /// A part in hand when the tool is put away (a ring spin, voice): kept, not discarded (UX W0.4). It goes back to
        /// its Find parts card — Take it again, or pick the Part tool, and it's back in the hand.
        public PartInstance Parked { get; private set; }

        public void Equip(bool on)
        {
            if (Equipped == on) return;
            Equipped = on;
            m_SeatPointer = null;   // W1.2
            if (!on && Held != null) Park();
            else if (on && Held == null && Parked != null) Unpark();
            Log.Info($"Part tool {(on ? "equipped" : "put away")}");
        }

        void Park()
        {
            var part = Held;
            Held = null;
            OnSurface = false;
            if (part == null) return;
            if (Parked != null && Parked != part) DestroyPart(Parked);
            Parked = part;
            part.Held = false;
            part.ClearFit();
            part.gameObject.SetActive(false);
            Physics.SyncTransforms();
            if (Selected == part) Select(m_Placed.Count > 0 ? m_Placed[m_Placed.Count - 1] : null);
            LastAction = $"parked {part.Spec.id}";
            Log.Info($"Part {part.Spec.id} put back in Find parts");
            AirTools.UI.UiToast.Show($"{AirTools.UI.Copy.Cap(AirTools.UI.Copy.Noun(part.Spec, part.SearchQuery))} put back in Find parts", AirTools.UI.ColorRole.Info);
            AirTools.Tools.EditHistory.NotifyChanged();
        }

        /// delete-undo: the spec card's Remove on the part in hand: back to its Find parts card (Take it again, or pick the
        /// Part tool, and it's back), not destroyed. False with nothing in hand.
        public bool PutBack()
        {
            if (Held == null) return false;
            Park();
            return true;
        }

        /// The parked part back in the hand (null if none).
        public PartInstance Unpark()
        {
            var part = Parked;
            if (part == null) return null;
            Parked = null;
            part.gameObject.SetActive(true);
            Hold(part);
            Log.Info($"Part {part.Spec.id} back in hand");
            return part;
        }

        /// Taking a card for the part that's parked: it comes straight back (no reload). True if it did.
        public bool TryUnpark(string partId)
        {
            if (Parked == null || Parked.Spec.id != partId) return false;
            Unpark();
            return true;
        }

        /// Put a freshly loaded part in the hand (replaces an unplaced held part). Equips the part tool.
        public void Hold(PartInstance part)
        {
            if (part == null) return;
            if (Held != null && Held != part) Discard();
            if (Parked != null && Parked != part) { DestroyPart(Parked); Parked = null; }
            if (frame != null && part.transform.parent != frame) part.transform.SetParent(frame, true);
            Held = part;
            m_PlaceMemory.Clear();
            part.PinnedFit = null;
            part.Held = true;
            part.Placed = false;
            part.ClearFit();
            Select(part);
            if (!Equipped)
            {
                var tools = Services.Get<ToolManager>();
                if (tools != null) tools.Equip(ToolKind.Part);
                else Equip(true);
            }
            if (TryPointer(out var pointer)) UpdateHeld(pointer);
            else FloatInView(part, KeepRotation?.Invoke(part));   // edit-touch
            LastAction = $"holding {part.Spec.id}";
            Log.Info($"Part {part.Spec.id} in hand ({part.Source})");
        }

        public void Select(PartInstance part)
        {
            if (Selected == part) return;
            Selected = part;
            SelectionChanged?.Invoke(part);
        }

        void Update()
        {
            if (Equipped && Held != null && m_Input != null) Tick();
        }

        /// Per-frame: move the held part with the pointer (tests call this directly).
        public void Tick()
        {
            if (Held == null || m_Input == null) return;
            // The ISDK ray switches off near poke buttons (e.g. right after TAKE); then use the other hand's ray,
            // else leave the part where it is.
            if (TryPointer(out var pointer)) UpdateHeld(SeatPointer(pointer, m_Input.LastActiveHand));
        }

        // W1.2: a live press remembers the pointer from ~90 ms before the pinch onset. Until the release, and at the
        // release, a ray still within PointerHistory.SeatDegrees of it keeps the seat previewed there (the part doesn't
        // follow the pinch's dip); a larger move is a drag and places at the release, as before.
        Pose? m_SeatPointer;
        ToolHand m_SeatHand;

        Pose SeatPointer(Pose pointer, ToolHand hand) =>
            m_SeatPointer.HasValue && m_SeatHand == hand && PointerHistory.SameAim(m_SeatPointer.Value, pointer, PointerHistory.SeatDegrees)
                ? m_SeatPointer.Value : pointer;

        bool TryPointer(out Pose pointer)
        {
            pointer = default;
            if (m_Input == null) return false;
            var hand = m_Input.LastActiveHand;
            var other = hand == ToolHand.Right ? ToolHand.Left : ToolHand.Right;
            if (m_Input.HasPointer(hand)) { pointer = m_Input.GetPointer(hand); return true; }
            if (m_Input.HasPointer(other)) { pointer = m_Input.GetPointer(other); return true; }
            return false;
        }

        /// No pointer at all: show the new part 45 cm in front of the eyes, upright (or with the rotation it keeps:
        /// edit-touch), until a ray comes back.
        static void FloatInView(PartInstance part, Quaternion? keep = null)
        {
            var cam = Camera.main;
            if (cam == null) return;
            var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            var pos = cam.transform.position + fwd * 0.45f - Vector3.up * 0.1f;
            part.transform.SetPositionAndRotation(pos, keep ?? PartMath.HeldRotation(part.Spec, fwd, cam.transform.position - pos));
        }

        [Tooltip("Held-part preview follows its target at this rate (1/s): a surface change glides instead of flicking.")]
        public float previewFollow = 18f;
        [Tooltip("A target jump larger than this (m) snaps straight there (a new surface, not jitter).")]
        public float previewSnapDistance = 0.25f;

        /// The held part's structure-plane memory: it stays on the plane it's on until the pointer clearly leaves it.
        readonly AirTools.Scene.StructureSnapper.Memory m_PlaceMemory = new AirTools.Scene.StructureSnapper.Memory();

        public void UpdateHeld(Pose pointer)
        {
            if (Held == null) return;
            var keep = KeepRotation?.Invoke(Held);   // edit-touch: the Edit view's new part is only translated
            if (PartPlacer.TryFind(Held.Spec, pointer, holdDistance, snapRadius, maxRayDistance, out var hit, out _, m_PlaceMemory))
            {
                var fromPos = Held.transform.position; var fromRot = Held.transform.rotation;
                bool snapped = false;
                if (keep.HasValue) PartPlacer.Seat(Held, hit, keep.Value);   // edit-touch
                else
                {
                    PartPlacer.Apply(Held, hit, ViewerPosition());
                    snapped = HeldSnap != null && HeldSnap(Held);   // assetgen: into a taped opening it fits
                }
                if (OnSurface && Application.isPlaying)
                {
                    var toPos = Held.transform.position; var toRot = Held.transform.rotation;
                    float scale = Mathf.Max(AirTools.Scene.SnapService.Scale, 1e-4f);
                    if (Vector3.Distance(fromPos, toPos) < previewSnapDistance * scale)
                    {
                        float k = 1f - Mathf.Exp(-previewFollow * Time.unscaledDeltaTime);
                        Held.transform.SetPositionAndRotation(Vector3.Lerp(fromPos, toPos, k), Quaternion.Slerp(fromRot, toRot, k));
                    }
                }
                OnSurface = true;
                if (Time.unscaledTime >= m_NextEvaluate || !Application.isPlaying)
                {
                    m_NextEvaluate = Time.unscaledTime + evaluateInterval;
                    var snapFit = snapped ? HeldSnapFit?.Invoke(Held) : null;   // assetgen: the opening's fit while it's snapped in
                    if (snapFit != null) Held.ShowFit(snapFit);
                    else Held.Evaluate(FitChecker.FindTape(Held, frame));
                }
            }
            else
            {
                OnSurface = false;
                var viewer = ViewerPosition();
                var pos = pointer.position + pointer.forward * holdDistance;
                Held.transform.SetPositionAndRotation(pos, keep ?? PartMath.HeldRotation(Held.Spec, pointer.forward, viewer - pos));   // edit-touch: keep
                if (Held.Fit != null) Held.ClearFit();
            }
        }

        void OnPressStart(ToolHand hand, Pose pointer)
        {
            m_SeatPointer = Equipped && ToolInputHub.TryGetCommitPointer(m_Input, hand, out var seat) ? seat : (Pose?)null;   // W1.2
            m_SeatHand = hand;
            if (!Equipped || Held != null) return;
            pointer = m_SeatPointer ?? pointer;   // W1.2: pick up the part you were pointing at
            // Pick up a placed part under the pointer.
            if (Physics.Raycast(new Ray(pointer.position, pointer.forward), out var rh, maxRayDistance, PartLayers.PartsMask, QueryTriggerInteraction.Ignore)
                && rh.collider.GetComponentInParent<PartInstance>() is PartInstance part && m_Placed.Contains(part))
            {
                // placement: a placed part is locked — only the placement editor (Adjust) moves it; a press selects it.
                if (CanPickUp != null && !CanPickUp(part))
                {
                    Select(part);
                    LastAction = $"selected {part.Spec.id} (locked: Adjust moves it)";
                    return;
                }
                m_Placed.Remove(part);
                Held = part;
                m_PlaceMemory.Clear();
                part.PinnedFit = null;   // moved: the server's verdict was for where it put it
                part.Held = true;
                part.Placed = false;
                Select(part);
                LastAction = $"picked up {part.Spec.id}";
                Log.Info($"Part {LastAction}");
            }
        }

        void OnPressEnd(ToolHand hand, Pose pointer)
        {
            var seat = SeatPointer(pointer, hand);   // W1.2
            m_SeatPointer = null;
            if (!Equipped || Held == null) return;
            if (!Release(seat, hand) && Held != null && !seat.Equals(pointer)) Release(pointer, hand);   // W1.2: nothing under it then
        }

        /// Place the held part at the surface for this pointer. False (still held) if no surface is in reach.
        public bool Release(Pose pointer, ToolHand hand = ToolHand.Right)
        {
            var part = Held;
            if (part == null) return false;
            if (!PartPlacer.TryFind(part.Spec, pointer, holdDistance, snapRadius, maxRayDistance, out var hit, out bool viaRay, m_PlaceMemory))
            {
                LastAction = "no surface under the pointer";
                AirTools.UI.InputHints.Say("part.nosurface", $"Aim at {AirTools.UI.Copy.MountTarget(part.Spec)} to place it");
                return false;
            }
            AirTools.UI.InputHints.Succeeded("part.nosurface");
            var keep = KeepRotation?.Invoke(part);   // edit-touch: the Edit view's new part keeps its rotation, only translated
            float slide = keep.HasValue ? PartPlacer.Seat(part, hit, keep.Value) : PartPlacer.Apply(part, hit, ViewerPosition());
            bool snapped = !keep.HasValue && HeldSnap != null && HeldSnap(part);   // assetgen: into a taped opening it fits
            m_PlaceMemory.Clear();
            part.Held = false;
            part.Placed = true;
            Held = null;
            OnSurface = false;
            m_Placed.Add(part);
            m_History.Add(part);
            m_HistoryTimes.Add(AirTools.Tools.EditHistory.Stamp());
            DropRedo();
            AirTools.Tools.EditHistory.Edited(this);
            var fit = part.Evaluate(FitChecker.FindTape(part, frame));
            Record(part);
            PartFeedback.Seated(part.transform.position, hand, fit.Status);
            LastAction = $"placed {part.Spec.id} on {FitChecker.SurfaceName(hit.collider)}{(viaRay ? " (ray)" : "")}"
                         + (slide > 0.001f ? $", slid {slide * 1000f:0} mm clear" : "") + (snapped ? ", snapped into the opening" : "") + $": {fit}";
            Log.Info($"Part {LastAction}");
            PartPlaced?.Invoke(part);
            return true;
        }

        /// place_part: put a loaded part at a pose — SceneRoot-local position and rotation, no pointer and no surface
        /// search (the pose is the server's, or the removed part's cavity insert). One undoable placement like a release,
        /// with its notebook row. `fit` (the server's fits / clearance_mm) is shown when given; else the app's own check.
        public void PlaceAt(PartInstance part, Vector3 localPosition, Quaternion localRotation, FitReport fit = null)
        {
            if (part == null) return;
            if (Held == part) { Held = null; OnSurface = false; }
            if (Parked == part) Parked = null;
            part.gameObject.SetActive(true);
            if (frame != null && part.transform.parent != frame) part.transform.SetParent(frame, false);
            part.transform.localPosition = localPosition;
            part.transform.localRotation = localRotation;
            part.transform.localScale = Vector3.one;
            part.Held = false;
            part.Placed = true;
            part.PinnedFit = fit;
            if (!m_Placed.Contains(part)) m_Placed.Add(part);
            m_History.Add(part);
            m_HistoryTimes.Add(AirTools.Tools.EditHistory.Stamp());
            DropRedo();
            AirTools.Tools.EditHistory.Edited(this);
            Physics.SyncTransforms();
            var shown = part.Evaluate(FitChecker.FindTape(part, frame));
            Record(part);
            Select(part);
            PartFeedback.Seated(part.transform.position, ToolHand.Right, shown.Status);
            LastAction = $"placed {part.Spec.id} at a pose{(fit != null ? " (server fit)" : "")}: {shown}";
            Log.Info($"Part {LastAction}");
            PartPlaced?.Invoke(part);
        }

        /// Re-run the fit check on every placed part (e.g. after a new tape reading).
        public void EvaluateAll()
        {
            foreach (var p in m_Placed) { p.Evaluate(FitChecker.FindTape(p, frame)); Record(p); }
        }

        /// One notebook entry per placed part (updated when it moves).
        void Record(PartInstance part)
        {
            var point = frame != null ? frame.InverseTransformPoint(part.transform.position) : part.transform.position;
            string label = $"{part.Spec.name}: {part.Fit?.Headline.Replace('\n', ' ')}";
            if (m_Entries.TryGetValue(part, out var e) && Contains(Notebook.Entries, e))
            {
                e.Points = new[] { point };
                e.Label = label;
                e.Where = Where(part);
                e.Finish = FinishOf(part);   // edit6dof
                SetDisplay(e, part);
                Notebook.Update(e);
                return;
            }
            // Backend "placement" entry (docs/api.md POST /notebook): one piece of this part, and where it went.
            e = new NotebookEntry("part", 0, "", new[] { point }, DateTime.Now, -1, label) { PartId = part.Spec.id, Count = 1, Where = Where(part), Finish = FinishOf(part) };   // edit6dof: Finish
            SetDisplay(e, part);
            var root = frame != null ? frame.GetComponent<SceneRoot>() : null;
            if (root != null) e.NearestCameraId = CameraEvidence.Nearest(e.Points, root.CamerasInRootSpace());
            Notebook.Add(e);
            m_Entries[part] = e;
        }

        /// Where a placed part went, in words for the notebook's placement entry ("on the fascia").
        static string Where(PartInstance part)
        {
            var surface = part.Fit?.Surface;
            if (string.IsNullOrEmpty(surface) && part.Surface != null) surface = FitChecker.SurfaceName(part.Surface);
            return string.IsNullOrEmpty(surface) ? null : $"on {surface}";
        }

        /// The parts a finish goes on: this one and every copy in an array made from it (or with it), placed or undone.
        public List<PartInstance> FinishGroup(PartInstance part)
        {
            var group = new List<PartInstance>();
            if (part == null) return group;
            group.Add(part);
            void AddGroup(ArrayGroup g)
            {
                if (g == null || (g.Reference != part && !g.Clones.Contains(part))) return;
                if (g.Reference != null && !group.Contains(g.Reference)) group.Add(g.Reference);
                foreach (var c in g.Clones) if (c != null && !group.Contains(c)) group.Add(c);
            }
            foreach (var op in m_History) AddGroup(op as ArrayGroup);
            foreach (var (op, _) in m_Redo) AddGroup(op as ArrayGroup);
            return group;
        }

        /// Notebook row words (UX W0.9 F19): "Hinge" · "✓ Fits the door" · "the door". Label stays raw.
        static void SetDisplay(NotebookEntry e, PartInstance part)
        {
            e.DisplayTitle = AirTools.UI.Copy.Cap(AirTools.UI.Copy.Noun(part.Spec, part.SearchQuery));
            e.DisplayValue = AirTools.UI.Copy.FitLine(part.Fit);
            e.DisplayDetail = part.Fit?.Surface;
        }

        static bool Contains(IReadOnlyList<NotebookEntry> list, NotebookEntry e)
        {
            foreach (var x in list) if (x == e) return true;
            return false;
        }

        void OnButton(ToolHand hand, ToolButton button)
        {
            if (!Equipped) return;
            if (button == ToolButton.Undo) Undo();
            else if (button == ToolButton.Redo) Redo();
            else if (button == ToolButton.Clear) RemoveAll();   // delete-undo: one undo step (ClearAll is the hard reset)
        }

        /// Undo the last placement — a whole array in one step — or put back the part in hand.
        public void Undo()
        {
            if (Held != null) { Discard(); AirTools.Tools.EditHistory.NotifyChanged(); return; }
            while (m_History.Count > 0)
            {
                var op = m_History[m_History.Count - 1];
                m_History.RemoveAt(m_History.Count - 1);
                if (m_HistoryTimes.Count > 0) m_HistoryTimes.RemoveAt(m_HistoryTimes.Count - 1);
                if (op is ArrayGroup g) { UndoArray(g); AirTools.Tools.EditHistory.NotifyChanged(); return; }
                if (op is PartInstance p && p != null && m_Placed.Contains(p)) { Hide(p); AirTools.Tools.EditHistory.NotifyChanged(); return; }
                if (op is Deletion d && d.Part != null) { Undelete(d); AirTools.Tools.EditHistory.NotifyChanged(); return; }   // edit6dof
                if (op is Clearing c) { Unclear(c); AirTools.Tools.EditHistory.NotifyChanged(); return; }   // delete-undo
            }
            if (m_Placed.Count > 0) Remove(m_Placed[m_Placed.Count - 1]);
            AirTools.Tools.EditHistory.NotifyChanged();
        }

        /// Undo of a placement: the part leaves the scene and the notebook but is kept for Redo.
        void Hide(PartInstance part)
        {
            m_Placed.Remove(part);
            if (m_Entries.TryGetValue(part, out var e)) Notebook.Remove(e);
            if (Selected == part) Select(m_Placed.Count > 0 ? m_Placed[m_Placed.Count - 1] : null);
            part.gameObject.SetActive(false);
            Physics.SyncTransforms();
            m_Redo.Add((part, AirTools.Tools.EditHistory.Stamp()));
            LastAction = $"undo {part.Spec.id}";
        }

        /// Put back the last undone placement (or the whole array, in one step).
        public void Redo()
        {
            if (m_Redo.Count == 0) return;
            var (op, _) = m_Redo[m_Redo.Count - 1];
            m_Redo.RemoveAt(m_Redo.Count - 1);
            if (op is ArrayGroup g) { RedoArray(g); AirTools.Tools.EditHistory.NotifyChanged(); return; }
            if (op is Deletion d) { if (d.Part != null && m_Placed.Contains(d.Part)) DeleteNow(d); AirTools.Tools.EditHistory.NotifyChanged(); return; }   // edit6dof
            if (op is Clearing cl) { ClearNow(cl); AirTools.Tools.EditHistory.NotifyChanged(); return; }   // delete-undo
            var part = op as PartInstance;
            if (part == null) return;
            part.gameObject.SetActive(true);
            Physics.SyncTransforms();
            m_Placed.Add(part);
            m_History.Add(part);
            m_HistoryTimes.Add(AirTools.Tools.EditHistory.Stamp());
            if (m_Entries.TryGetValue(part, out var e)) Notebook.Restore(e);
            part.Evaluate(FitChecker.FindTape(part, frame));
            Select(part);
            LastAction = $"redo {part.Spec.id}";
            AirTools.Tools.EditHistory.NotifyChanged();
        }

        public bool CanUndo => Held != null || m_Placed.Count > 0 || (m_History.Count > 0 && m_History[m_History.Count - 1] is Deletion or Clearing);   // edit6dof, delete-undo
        public bool CanRedo => m_Redo.Count > 0;
        public float LastEditAt => Held != null ? AirTools.Tools.EditHistory.Now
            : m_HistoryTimes.Count > 0 ? m_HistoryTimes[m_HistoryTimes.Count - 1] : m_Placed.Count > 0 ? 0f : float.NegativeInfinity;
        public float LastUndoAt => m_Redo.Count > 0 ? m_Redo[m_Redo.Count - 1].at : float.NegativeInfinity;

        public void DropRedo()
        {
            foreach (var (op, _) in m_Redo)
            {
                if (op is PartInstance part && part != null)
                {
                    if (m_Entries.TryGetValue(part, out _)) m_Entries.Remove(part);
                    DestroyObj(part.gameObject);
                }
                else if (op is ArrayGroup g)
                    foreach (var c in g.Clones) if (c != null) DestroyObj(c.gameObject);
            }
            m_Redo.Clear();
        }

        /// SPEC M5 array: copies of the selected placed part along the last tape segment every spacingMm (default: the
        /// listing's spacing_mm), each seated on the surface and fit-checked. Quantity = ⌊L/s⌋ + 1. The selected part
        /// becomes the member nearest to it; one notebook entry records the run; Undo removes the array in one step.
        public ArrayGroup PlaceArray(float? spacingMm = null, TapeReading? tapeOverride = null)
        {
            var reference = Selected != null && Selected.Placed ? Selected : (m_Placed.Count > 0 ? m_Placed[m_Placed.Count - 1] : null);
            if (reference == null) { LastAction = "array: place one part first"; return null; }
            float? mm = spacingMm ?? reference.Spec.spacing_mm;
            if (!mm.HasValue || mm.Value < 10f) { LastAction = $"array: {reference.Spec.name} is a single unit (its listing has no spacing)"; return null; }
            var tape = tapeOverride ?? FitChecker.FindTape(reference, frame, 1.5f) ?? LatestTape();
            if (!tape.HasValue) { LastAction = "array: measure the run first (two points along it)"; return null; }
            var plan = ArrayPlanner.Plan(tape.Value.A, tape.Value.B, reference.transform.position, reference.SurfaceNormal, mm.Value * 0.001f,
                frame != null ? frame.up : Vector3.up);

            var g = new ArrayGroup { Reference = reference, ReferencePose = LocalPose(reference.transform), Plan = plan, TapeEntryId = tape.Value.EntryId };
            int nearest = 0; float best = float.MaxValue;
            for (int k = 0; k < plan.Count; k++)
            {
                float d = Vector3.Distance(plan.Positions[k], reference.transform.position);
                if (d < best) { best = d; nearest = k; }
            }
            var viewer = ViewerPosition();
            for (int k = 0; k < plan.Count; k++)
            {
                var part = k == nearest ? reference : reference.Clone(frame, style);
                part.transform.SetPositionAndRotation(plan.Positions[k], reference.transform.rotation);
                // Seat each one on the surface under its slot (walls aren't perfectly flat in real captures).
                var n = reference.SurfaceNormal.sqrMagnitude > 0.5f ? reference.SurfaceNormal : -reference.WorldMountDirection;
                if (SnapService.TrySnap(plan.Positions[k], out var hit, 0.08f, features: false) && Vector3.Dot(hit.normal, n) > 0.9f)
                    PartPlacer.Apply(part, hit, viewer);
                else { part.SurfaceNormal = n; part.Surface = reference.Surface; }
                part.Held = false;
                part.Placed = true;
                if (part != reference) { m_Placed.Add(part); g.Clones.Add(part); }
            }
            Physics.SyncTransforms();
            foreach (var p in g.Clones) p.Evaluate(FitChecker.FindTape(p, frame));
            reference.Evaluate(FitChecker.FindTape(reference, frame));
            Record(reference);

            var c = System.Globalization.CultureInfo.InvariantCulture;
            string label = $"Array: {plan.Count} × {reference.Spec.name} every {mm.Value.ToString("0", c)} mm along {Units.Format(plan.LengthM)}";
            var pts = new[] { ToFrame(tape.Value.A), ToFrame(tape.Value.B) };
            string arrNoun = AirTools.UI.Copy.Noun(reference.Spec, reference.SearchQuery);
            g.Entry = new NotebookEntry("array", plan.Count, "pcs", pts, DateTime.Now, -1, label)
            {
                DisplayTitle = $"{plan.Count} × {arrNoun}",
                DisplayValue = $"every {AirTools.UI.Copy.Gap(mm.Value)}",
                DisplayDetail = $"along {AirTools.UI.Copy.Len(plan.LengthM)}",
                // Backend "placement": the copies this adds (the part it was made from has its own entry), of a run of
                // plan.Count, so the report's per-part total is the run's count.
                PartId = reference.Spec.id, Count = g.Clones.Count, Total = plan.Count,
                Where = $"every {mm.Value.ToString("0", c)} mm along {plan.LengthM.ToString("0.00", c)} m{(Where(reference) is string w ? " " + w : "")}",
            };
            var root = frame != null ? frame.GetComponent<SceneRoot>() : null;
            if (root != null) g.Entry.NearestCameraId = CameraEvidence.Nearest(pts, root.CamerasInRootSpace());
            Notebook.Add(g.Entry);
            m_History.Add(g);
            m_HistoryTimes.Add(AirTools.Tools.EditHistory.Stamp());
            DropRedo();
            AirTools.Tools.EditHistory.Edited(this);
            LastArray = g;
            int green = 0;
            foreach (var p in ArrayMembers(g)) if (p.Fit != null && p.Fit.Status == FitStatus.Green) green++;
            LastAction = $"array: {plan.Count} × {reference.Spec.id} every {mm.Value.ToString("0", c)} mm along {plan.LengthM.ToString("0.000", c)} m, {green} green";
            Log.Info($"Part {LastAction}");
            ArrayPlaced?.Invoke(g);
            return g;
        }

        public event Action<ArrayGroup> ArrayPlaced;

        // Grok G2 ------------------------------------------------------------------------------------------------------
        /// Grok F7 (backend show_plan → "place them" → place_array {plan_id}): the part at each of a plan's points (world),
        /// as one array group, so one Undo removes the set. Template: the part in hand, which becomes the first member
        /// (all members are then undone together); else the selected placed part, which moves into the nearest point and
        /// goes back on Undo, as in PlaceArray. Each part is seated on the surface within 8 cm of its point.
        public ArrayGroup PlaceAtPoints(IReadOnlyList<Vector3> worldPoints, float? spacingMm = null, string planId = null)
        {
            if (worldPoints == null || worldPoints.Count == 0) { LastAction = "plan: no points"; return null; }
            var held = Held;
            var reference = held != null ? null : Selected != null && Selected.Placed ? Selected : (m_Placed.Count > 0 ? m_Placed[m_Placed.Count - 1] : null);
            var template = held != null ? held : reference;
            if (template == null) { LastAction = "plan: take a part first"; return null; }
            int n = worldPoints.Count;
            var pts = new Vector3[n];
            for (int k = 0; k < n; k++) pts[k] = worldPoints[k];
            var run = pts[n - 1] - pts[0];
            float length = run.magnitude;
            float spacingM = spacingMm.HasValue && spacingMm.Value > 0f ? spacingMm.Value * 0.001f : n > 1 ? length / (n - 1) : 0f;
            var plan = new ArrayPlan { Count = n, SpacingM = spacingM, LengthM = length, Direction = length > 1e-6f ? run / length : Vector3.right, Positions = pts };
            var g = new ArrayGroup { Reference = reference, Plan = plan };
            int nearest = -1;
            if (reference != null)
            {
                g.ReferencePose = LocalPose(reference.transform);
                float best = float.MaxValue;
                for (int k = 0; k < n; k++)
                {
                    float d = Vector3.Distance(pts[k], reference.transform.position);
                    if (d < best) { best = d; nearest = k; }
                }
            }
            if (held != null) { Held = null; OnSurface = false; m_PlaceMemory.Clear(); m_SeatPointer = null; }
            var rotation = template.transform.rotation;
            var normal = template.SurfaceNormal;
            var surface = template.Surface;
            var viewer = ViewerPosition();
            float radius = 0.08f * Mathf.Max(SnapService.Scale, 1e-4f);
            for (int k = 0; k < n; k++)
            {
                var part = k == nearest ? reference : (k == 0 && held != null) ? held : template.Clone(frame, style);
                part.transform.SetPositionAndRotation(pts[k], rotation);
                if (SnapService.TrySnap(pts[k], out var hit, radius, features: false)) PartPlacer.Apply(part, PartPlacer.MatchSurface(part.Spec, hit), viewer);
                else { part.SurfaceNormal = normal; part.Surface = surface; }
                part.Held = false;
                part.Placed = true;
                if (part != reference) { m_Placed.Add(part); g.Clones.Add(part); }
            }
            Physics.SyncTransforms();
            foreach (var p in ArrayMembers(g)) p.Evaluate(FitChecker.FindTape(p, frame));
            if (reference != null) Record(reference);

            string noun = AirTools.UI.Copy.Noun(template.Spec, template.SearchQuery);
            var ends = new[] { ToFrame(pts[0]), ToFrame(pts[n - 1]) };
            g.Entry = new NotebookEntry("array", n, "pcs", ends, DateTime.Now, -1,
                $"Plan{(string.IsNullOrEmpty(planId) ? "" : " " + planId)}: {n} × {template.Spec.name} at the plan's points along {Units.Format(length)}")
            {
                DisplayTitle = $"{n} × {noun}",
                DisplayValue = n > 1 ? $"every {AirTools.UI.Copy.Gap(spacingM * 1000f)}" : "from the plan",
                DisplayDetail = $"along {AirTools.UI.Copy.Len(length)}",
            };
            var root = frame != null ? frame.GetComponent<SceneRoot>() : null;
            if (root != null) g.Entry.NearestCameraId = CameraEvidence.Nearest(ends, root.CamerasInRootSpace());
            Notebook.Add(g.Entry);
            m_History.Add(g);
            m_HistoryTimes.Add(AirTools.Tools.EditHistory.Stamp());
            DropRedo();
            AirTools.Tools.EditHistory.Edited(this);
            LastArray = g;
            Select(reference != null ? reference : g.Clones[0]);
            var c = System.Globalization.CultureInfo.InvariantCulture;
            LastAction = $"plan: {n} × {template.Spec.id} at the plan's points{(string.IsNullOrEmpty(planId) ? "" : $" ({planId})")} along {length.ToString("0.000", c)} m";
            Log.Info($"Part {LastAction}");
            ArrayPlaced?.Invoke(g);
            return g;
        }
        // end Grok G2 --------------------------------------------------------------------------------------------------

        // Declutter C (S10) -------------------------------------------------------------------------------------------
        /// The placed array a part belongs to (its reference or a clone), or null: its callout joins the array's one
        /// group label in the world-label pool (PartOutline). An undone array isn't placed, so it doesn't group.
        public ArrayGroup ArrayOf(PartInstance p)
        {
            if (p == null) return null;
            for (int i = m_History.Count - 1; i >= 0; i--)
                if (m_History[i] is ArrayGroup g && (g.Reference == p || g.Clones.Contains(p))) return g;
            return null;
        }
        // end Declutter C ---------------------------------------------------------------------------------------------

        /// Every member of an array, in order along the run.
        public static List<PartInstance> ArrayMembers(ArrayGroup g)
        {
            var all = new List<PartInstance>(g.Clones);
            if (g.Reference != null) all.Add(g.Reference);
            var d = g.Plan.Direction;
            all.RemoveAll(p => p == null);
            all.Sort((a, b) => Vector3.Dot(a.transform.position, d).CompareTo(Vector3.Dot(b.transform.position, d)));
            return all;
        }

        /// Undo of an array: the clones leave the scene (hidden, kept for Redo), the reference goes back to where it was.
        void UndoArray(ArrayGroup g)
        {
            g.Clones.RemoveAll(p => p == null);
            foreach (var p in g.Clones) { m_Placed.Remove(p); if (Selected == p) Select(null); p.gameObject.SetActive(false); }
            Physics.SyncTransforms();
            if (g.Entry != null) Notebook.Remove(g.Entry);
            g.HasArrayPose = false;
            if (g.Reference != null && m_Placed.Contains(g.Reference))
            {
                g.ReferenceArrayPose = LocalPose(g.Reference.transform);
                g.HasArrayPose = true;
                SetLocalPose(g.Reference.transform, g.ReferencePose);
                Physics.SyncTransforms();
                g.Reference.Evaluate(FitChecker.FindTape(g.Reference, frame));
                Record(g.Reference);
                Select(g.Reference);
            }
            if (LastArray == g) LastArray = null;
            m_Redo.Add((g, AirTools.Tools.EditHistory.Stamp()));
            LastAction = "undo array";
        }

        /// Redo of an array: the clones come back, the reference returns to its slot, the notebook entry is restored.
        void RedoArray(ArrayGroup g)
        {
            g.Clones.RemoveAll(p => p == null);
            foreach (var p in g.Clones) { p.gameObject.SetActive(true); if (!m_Placed.Contains(p)) m_Placed.Add(p); }
            bool reference = g.Reference != null && m_Placed.Contains(g.Reference);
            if (reference && g.HasArrayPose) SetLocalPose(g.Reference.transform, g.ReferenceArrayPose);
            Physics.SyncTransforms();
            foreach (var p in ArrayMembers(g)) if (m_Placed.Contains(p)) p.Evaluate(FitChecker.FindTape(p, frame));
            if (reference) Record(g.Reference);
            if (g.Entry != null) Notebook.Restore(g.Entry);
            m_History.Add(g);
            m_HistoryTimes.Add(AirTools.Tools.EditHistory.Stamp());
            LastArray = g;
            Select(reference ? g.Reference : (g.Clones.Count > 0 ? g.Clones[0] : Selected));
            LastAction = "redo array";
        }

        Pose LocalPose(Transform t) => frame != null
            ? new Pose(frame.InverseTransformPoint(t.position), Quaternion.Inverse(frame.rotation) * t.rotation)
            : new Pose(t.position, t.rotation);

        void SetLocalPose(Transform t, Pose p)
        {
            if (frame != null) t.SetPositionAndRotation(frame.TransformPoint(p.position), frame.rotation * p.rotation);
            else t.SetPositionAndRotation(p.position, p.rotation);
        }

        /// How many of this part are placed (the checkout quantity).
        /// The scene's calibration changed by `factor` (SceneRoot.Rescaled): placed parts keep their true size and move
        /// with the scene; fits are re-evaluated.
        public void RescaleAll(float factor)
        {
            void Move(PartInstance p)
            {
                if (p == null) return;
                if (frame != null && p.transform.parent == frame) p.transform.localPosition *= factor;
                else p.transform.position = frame != null ? frame.TransformPoint(frame.InverseTransformPoint(p.transform.position) * factor) : p.transform.position * factor;
            }
            foreach (var p in m_Placed) Move(p);
            // Undone (hidden) parts and array poses move with the scene too, so a later redo puts them back right.
            foreach (var (op, _) in m_Redo)
            {
                if (op is PartInstance part) Move(part);
                else if (op is ArrayGroup rg) foreach (var c in rg.Clones) Move(c);
            }
            foreach (var p in RemovedParts(m_History)) Move(p);   // delete-undo: removed (hidden, kept for Undo) parts too
            var groups = new HashSet<ArrayGroup>();
            foreach (var op in m_History) if (op is ArrayGroup hg) groups.Add(hg);
            foreach (var (op, _) in m_Redo) if (op is ArrayGroup rg) groups.Add(rg);
            foreach (var g in groups) { g.ReferencePose.position *= factor; g.ReferenceArrayPose.position *= factor; }   // frame-local
            Physics.SyncTransforms();
            EvaluateAll();
        }

        public int QuantityOf(string partId)
        {
            int n = 0;
            foreach (var p in m_Placed) if (p != null && p.Spec.id == partId) n++;
            return n;
        }

        static TapeReading? LatestTape()
        {
            var entries = Notebook.Entries;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (e.Tool != "measure" || e.Points == null || e.Points.Length != 2) continue;
                if (!e.OnCurrentSite) continue;   // sitescope: another scan's tape
                var frame = Services.TryGet<SceneRoot>(out var r) ? r.transform : null;
                var a = frame != null ? frame.TransformPoint(e.Points[0]) : e.Points[0];
                var b = frame != null ? frame.TransformPoint(e.Points[1]) : e.Points[1];
                return new TapeReading { LengthMm = (float)e.ValueSI * 1000f, A = a, B = b, EntryId = e.Id };
            }
            return null;
        }

        Vector3 ToFrame(Vector3 world) => frame != null ? frame.InverseTransformPoint(world) : world;

        public bool SetFinish(string finishName)
        {
            var part = Held != null ? Held : Selected;
            return part != null && part.SetFinish(finishName);
        }

        /// A hard removal, with no undo (harness clean-up). delete-undo: people's removals go through Delete / RemoveAll.
        public void Remove(PartInstance part)
        {
            if (part == null) return;
            m_Placed.Remove(part);
            if (m_Entries.TryGetValue(part, out var e)) { Notebook.Remove(e); m_Entries.Remove(part); }
            if (Selected == part) Select(m_Placed.Count > 0 ? m_Placed[m_Placed.Count - 1] : null);
            if (Held == part) Held = null;
            LastAction = $"removed {part.Spec.id}";
            DestroyPart(part);
        }

        public void ClearAll()
        {
            if (Held != null) Discard();
            if (Parked != null) { DestroyPart(Parked); Parked = null; }
            for (int i = m_Placed.Count - 1; i >= 0; i--) Remove(m_Placed[i]);
            foreach (var op in m_History) if (op is ArrayGroup g && g.Entry != null) Notebook.Remove(g.Entry);
            foreach (var p in RemovedParts(m_History)) { m_Entries.Remove(p); DestroyPart(p); }   // edit6dof, delete-undo: deleted and cleared parts
            m_History.Clear();
            m_HistoryTimes.Clear();
            DropRedo();
            LastArray = null;
            LastAction = "clear";
            Cleared?.Invoke();   // placement
            AirTools.Tools.EditHistory.NotifyChanged();
        }

        void Discard()
        {
            var part = Held;
            Held = null;
            OnSurface = false;
            if (part == null) return;
            if (Selected == part) Select(m_Placed.Count > 0 ? m_Placed[m_Placed.Count - 1] : null);
            if (m_Entries.TryGetValue(part, out var e)) { Notebook.Remove(e); m_Entries.Remove(part); }
            LastAction = $"put back {part.Spec.id}";
            DestroyPart(part);
        }

        static Vector3 ViewerPosition()
        {
            var cam = Camera.main;
            return cam != null ? cam.transform.position : SyntheticFacadeSpec.SpawnPosition + Vector3.up * 1.6f;
        }

        /// Deactivate first: in Play mode Destroy is deferred to the end of the frame, and a removed part's collider
        /// must not affect placements or fit checks later in the same frame.
        static void DestroyPart(PartInstance part)
        {
            part.gameObject.SetActive(false);
            Physics.SyncTransforms();
            DestroyObj(part.gameObject);
        }

        static void DestroyObj(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        // placement ------------------------------------------------------------------------------------------------------
        // The placement editor (PlacementEditor: Adjust, nudges, saved placements, model swaps) owns moving a placed part;
        // these are the hooks it needs into the placed list, the history and the notebook rows.

        /// Null: a press on a placed part picks it up (the old M4 behaviour). Set by the placement editor: false = locked,
        /// the press only selects it.
        public Func<PartInstance, bool> CanPickUp;

        /// assetgen: after the held part is seated on a surface (preview and release), the placement editor may pose it
        /// into a taped opening it fits (PlacementEditor.SnapHeld: true when it did; every frame, no allocation), and
        /// HeldSnapFit gives the opening's fit to show meanwhile (at the fit-check rate).
        public Func<PartInstance, bool> HeldSnap;
        public Func<PartInstance, FitReport> HeldSnapFit;
        /// edit-touch: a held part that keeps its own rotation (PlacementEditor.KeptRotation: the Edit view's new part, as
        /// it was oriented there): the preview and the release only translate it (PartPlacer.Seat) — no surface-normal
        /// alignment, no opening snap. Null (or a null result): the usual placement.
        public Func<PartInstance, Quaternion?> KeepRotation;

        /// Clear (the ring's Clear, a demo reset) removed every part.
        public event Action Cleared;

        // ---------------- sitescope: every placed part and generated asset belongs to the site it was placed on ----------------

        /// One site's parts while another site is loaded: the placed parts (arrays' copies included), the undo / redo
        /// stacks (per-site), the last array, the selection and the parts it hid. Notebook rows stay (m_Entries is keyed by
        /// the part, whatever its site).
        sealed class SiteState
        {
            public readonly List<PartInstance> Placed = new List<PartInstance>();
            public readonly List<object> History = new List<object>();
            public readonly List<float> HistoryTimes = new List<float>();
            public readonly List<(object op, float at)> Redo = new List<(object, float)>();
            public ArrayGroup LastArray;
            public PartInstance Selected;
            /// The unit showing when the site was left (a switch meanwhile re-words its fits and rows on the way back).
            public UnitSystem Units;
            public readonly ParkedObjects Hidden = new ParkedObjects();
        }

        SiteShelf<SiteState> m_Sites;
        SiteShelf<SiteState> Sites => m_Sites ??= new SiteShelf<SiteState>(SiteScope.Current);

        public string LiveSite => Sites.Live;
        /// Sites with parts parked.
        public int ParkedSiteCount => m_Sites?.Count ?? 0;
        /// Parts parked for `site` (0 for the live site or none).
        public int ParkedParts(string site) => Sites.Peek(site)?.Placed.Count ?? 0;
        /// Every parked part, whatever its site (the harness census: none may be showing).
        public IEnumerable<PartInstance> AllParkedParts()
        {
            foreach (var st in Sites.All)
            {
                foreach (var p in st.Placed) if (p != null) yield return p;
                foreach (var (op, _) in st.Redo)
                    if (op is PartInstance rp && rp != null) yield return rp;
                    else if (op is ArrayGroup g) foreach (var c in g.Clones) if (c != null) yield return c;
                foreach (var p in RemovedParts(st.History)) yield return p;   // delete-undo
            }
        }

        public void RegisterSite()
        {
            SiteScope.Register(this, SiteScope.OrderParts);
            if (Sites.Live != SiteScope.Current) SwitchSite(Sites.Live, SiteScope.Current, 1f);
        }

        public void SwitchSite(string from, string to, float factor)
        {
            m_Sites ??= new SiteShelf<SiteState>(from);
            if (string.IsNullOrEmpty(to) || to == Sites.Live) return;
            // The part in hand was seated on the scan being left: back to its Find parts card (Take it again there).
            if (Held != null) Park();
            m_SeatPointer = null;
            SiteState park = null;
            if (m_Placed.Count > 0 || m_History.Count > 0 || m_Redo.Count > 0)
            {
                park = new SiteState { LastArray = LastArray, Selected = Selected, Units = AirTools.UI.UiSettings.UnitSystem };
                park.Placed.AddRange(m_Placed);
                park.History.AddRange(m_History);
                park.HistoryTimes.AddRange(m_HistoryTimes);
                park.Redo.AddRange(m_Redo);
                foreach (var p in m_Placed) park.Hidden.Hide(p);
                m_Placed.Clear(); m_History.Clear(); m_HistoryTimes.Clear(); m_Redo.Clear();
                LastArray = null;
            }
            Select(null);
            var back = Sites.Swap(to, park);
            if (back != null)
            {
                m_Placed.AddRange(back.Placed);
                m_History.AddRange(back.History);
                m_HistoryTimes.AddRange(back.HistoryTimes);
                m_Redo.AddRange(back.Redo);
                LastArray = back.LastArray;
                back.Hidden.ShowAll();
                Physics.SyncTransforms();
                if (Mathf.Abs(factor - 1f) > 1e-6f) RescaleAll(factor);
                else if (back.Units != AirTools.UI.UiSettings.UnitSystem) Relabel();
                if (back.Selected != null && m_Placed.Contains(back.Selected)) Select(back.Selected);
            }
            else Physics.SyncTransforms();
            LastAction = $"site {to}";
            AirTools.Tools.EditHistory.NotifyChanged();
        }

        public int ParkedCount => m_Sites?.Count ?? 0;

        public void ClearParked()
        {
            foreach (var st in Sites.TakeAll())
            {
                foreach (var p in st.Placed)
                {
                    if (p == null) continue;
                    if (m_Entries.TryGetValue(p, out var e)) { Notebook.Remove(e); m_Entries.Remove(p); }
                    DestroyPart(p);
                }
                foreach (var op in st.History) if (op is ArrayGroup g && g.Entry != null) Notebook.Remove(g.Entry);
                foreach (var (op, _) in st.Redo)
                {
                    if (op is PartInstance part && part != null) { m_Entries.Remove(part); DestroyObj(part.gameObject); }
                    else if (op is ArrayGroup g) foreach (var c in g.Clones) if (c != null) DestroyObj(c.gameObject);
                }
                foreach (var p in RemovedParts(st.History)) { m_Entries.Remove(p); DestroyPart(p); }   // delete-undo
                st.Hidden.Clear();
            }
        }
        // end sitescope

        /// A different model at the same spot: `fresh` takes `old`'s place in the placed list, the undo / redo history
        /// (so undoing that placement removes whichever model is there) and its notebook row (re-worded for the new part);
        /// `old` is hidden, not destroyed (the caller keeps it for its own undo). The caller has already posed `fresh`.
        /// False when `old` isn't placed.
        public bool ReplacePlaced(PartInstance old, PartInstance fresh)
        {
            if (old == null || fresh == null || old == fresh) return false;
            int i = m_Placed.IndexOf(old);
            if (i < 0) return false;
            if (Held == fresh) { Held = null; OnSurface = false; }
            if (Parked == fresh) Parked = null;
            m_Placed.Remove(fresh);
            i = m_Placed.IndexOf(old);
            if (frame != null && fresh.transform.parent != frame) fresh.transform.SetParent(frame, true);
            fresh.gameObject.SetActive(true);
            fresh.Held = false;
            fresh.Placed = true;
            m_Placed[i] = fresh;
            for (int k = 0; k < m_History.Count; k++) if (ReferenceEquals(m_History[k], old)) m_History[k] = fresh;
            for (int k = 0; k < m_Redo.Count; k++) if (ReferenceEquals(m_Redo[k].op, old)) m_Redo[k] = (fresh, m_Redo[k].at);
            old.Placed = false;
            old.ClearFit();
            old.gameObject.SetActive(false);
            if (m_Entries.TryGetValue(old, out var e)) { m_Entries.Remove(old); m_Entries[fresh] = e; e.PartId = fresh.Spec.id; }
            Physics.SyncTransforms();
            fresh.Evaluate(FitChecker.FindTape(fresh, frame));
            Record(fresh);
            if (Selected == old || Selected == null) Select(fresh);
            LastAction = $"swapped {old.Spec.id} for {fresh.Spec.id}";
            Log.Info($"Part {LastAction}");
            AirTools.Tools.EditHistory.NotifyChanged();
            return true;
        }

        /// A placed part moved in place (the placement editor): re-run its fit (its PinnedFit, if it has one, shows) and,
        /// with `record`, update its notebook row. Not an edit of its own (the editor keeps that history).
        public void Refit(PartInstance part, bool record = true)
        {
            if (part == null || !m_Placed.Contains(part)) return;
            Physics.SyncTransforms();
            part.Evaluate(FitChecker.FindTape(part, frame));
            if (record) Record(part);
        }
        // end placement --------------------------------------------------------------------------------------------------

        // edit6dof --------------------------------------------------------------------------------------------------------
        // Delete from a placed part's context menu (PartContextMenu): the part leaves the scene and the notebook, and Undo
        // (the menu's own Undo, A / X, the ring) brings it back where it was. One step in this tool's history, after its
        // placement, so undoing past it takes the placement as usual.

        /// A deletion in the history: the part (hidden, kept for Undo) and its place in the placed list.
        public sealed class Deletion
        {
            public PartInstance Part;
            public int Index;
            /// delete-undo: what was selected when it went (Undo selects it again).
            public PartInstance SelectedBefore;
        }

        public event Action<PartInstance> Deleted;

        /// Delete a placed part (undoable). False when it isn't placed.
        public bool Delete(PartInstance part)
        {
            if (part == null || !m_Placed.Contains(part)) return false;
            var d = new Deletion { Part = part, Index = m_Placed.IndexOf(part) };
            DeleteNow(d);
            DropRedo();
            AirTools.Tools.EditHistory.Edited(this);
            return true;
        }

        void DeleteNow(Deletion d)
        {
            var part = d.Part;
            d.Index = Mathf.Max(0, m_Placed.IndexOf(part));
            d.SelectedBefore = Selected;   // delete-undo
            m_Placed.Remove(part);
            if (m_Entries.TryGetValue(part, out var e)) Notebook.Remove(e);
            if (Selected == part) Select(m_Placed.Count > 0 ? m_Placed[m_Placed.Count - 1] : null);
            part.Placed = false;
            part.gameObject.SetActive(false);
            Physics.SyncTransforms();
            m_History.Add(d);
            m_HistoryTimes.Add(AirTools.Tools.EditHistory.Stamp());
            LastAction = $"deleted {part.Spec.id}";
            Log.Info($"Part {LastAction}");
            Deleted?.Invoke(part);
        }

        void Undelete(Deletion d)
        {
            var part = d.Part;
            part.gameObject.SetActive(true);
            part.Placed = true;
            m_Placed.Insert(Mathf.Clamp(d.Index, 0, m_Placed.Count), part);
            Physics.SyncTransforms();
            if (m_Entries.TryGetValue(part, out var e)) Notebook.Restore(e);
            part.Evaluate(FitChecker.FindTape(part, frame));
            Select(RestoredSelection(d.SelectedBefore, part));   // delete-undo: the selection it had (else the part)
            m_Redo.Add((d, AirTools.Tools.EditHistory.Stamp()));
            LastAction = $"undo delete {part.Spec.id}";
        }

        /// The menu's Undo: bring `part` back if its deletion is this tool's newest step. False otherwise.
        public bool UndoDelete(PartInstance part)
        {
            if (part == null || m_History.Count == 0 || !(m_History[m_History.Count - 1] is Deletion d) || d.Part != part) return false;
            Undo();
            return true;
        }

        // delete-undo ------------------------------------------------------------------------------------------------------
        // Clear as one undo step (the Clear button / ToolButton.Clear): every placed part — arrays' copies too — leaves the
        // scene and the notebook, hidden and kept, and one Undo brings them all back where they were with their finishes,
        // fits, rows and the selection. ClearAll stays the hard reset (Demo reset, the harness): nothing kept.

        /// A Clear in the history: the parts it took off (placed order), the array rows it took out of the notebook, the
        /// selection and the last array.
        public sealed class Clearing
        {
            public readonly List<PartInstance> Parts = new List<PartInstance>();
            public readonly List<NotebookEntry> Rows = new List<NotebookEntry>();
            public PartInstance SelectedBefore;
            public ArrayGroup LastArray;
        }

        /// Clear, undoably: every placed part off (one undo step), "3 parts cleared · Undo on the ring". A part in hand
        /// stays in the hand. How many went (0: nothing placed, no step).
        public int RemoveAll()
        {
            if (m_Placed.Count == 0) { LastAction = "clear: nothing placed"; return 0; }
            var c = new Clearing();
            ClearNow(c);
            DropRedo();
            AirTools.Tools.EditHistory.Edited(this);
            AirTools.UI.UiToast.Show(AirTools.UI.Copy.PartsCleared(c.Parts.Count), AirTools.UI.ColorRole.Info);
            return c.Parts.Count;
        }

        void ClearNow(Clearing c)
        {
            c.SelectedBefore = Selected;
            c.LastArray = LastArray;
            c.Parts.Clear();
            c.Rows.Clear();
            foreach (var p in m_Placed) if (p != null) c.Parts.Add(p);
            m_Placed.Clear();
            foreach (var p in c.Parts)
            {
                if (m_Entries.TryGetValue(p, out var e)) Notebook.Remove(e);
                p.Placed = false;
                p.gameObject.SetActive(false);
            }
            foreach (var op in m_History) if (op is ArrayGroup g && g.Entry != null && Notebook.Remove(g.Entry)) c.Rows.Add(g.Entry);
            Physics.SyncTransforms();
            LastArray = null;
            if (Selected != null && Selected != Held) Select(null);
            m_History.Add(c);
            m_HistoryTimes.Add(AirTools.Tools.EditHistory.Stamp());
            LastAction = $"cleared {c.Parts.Count} part(s)";
            Log.Info($"Part {LastAction}");
        }

        void Unclear(Clearing c)
        {
            c.Parts.RemoveAll(p => p == null);
            int at = 0;
            foreach (var p in c.Parts)
            {
                p.gameObject.SetActive(true);
                p.Placed = true;
                if (!m_Placed.Contains(p)) m_Placed.Insert(Mathf.Min(at++, m_Placed.Count), p);
            }
            Physics.SyncTransforms();
            foreach (var p in c.Parts)
            {
                p.Evaluate(FitChecker.FindTape(p, frame));
                if (m_Entries.TryGetValue(p, out var e)) Notebook.Restore(e);
            }
            foreach (var e in c.Rows) Notebook.Restore(e);
            LastArray = c.LastArray;
            if (Held == null) Select(c.SelectedBefore != null && m_Placed.Contains(c.SelectedBefore) ? c.SelectedBefore : null);
            m_Redo.Add((c, AirTools.Tools.EditHistory.Stamp()));
            LastAction = $"undo clear ({c.Parts.Count} part(s) back)";
        }

        /// The parts `history` holds removed — deleted or cleared, hidden and kept for Undo — whatever their site.
        static IEnumerable<PartInstance> RemovedParts(List<object> history)
        {
            foreach (var op in history)
            {
                if (op is Deletion d && d.Part != null) yield return d.Part;
                else if (op is Clearing c) foreach (var p in c.Parts) if (p != null) yield return p;
            }
        }

        /// Removed parts kept for Undo on the live site (the harness counts them; none may show).
        public int RemovedKept { get { int n = 0; foreach (var _ in RemovedParts(m_History)) n++; return n; } }

        /// What Undo selects when a deleted part comes back: what was selected when it went, if that's still placed (it may
        /// be the part itself); else the part.
        PartInstance RestoredSelection(PartInstance before, PartInstance part) =>
            before != null && before != part && m_Placed.Contains(before) ? before : part;
        // end delete-undo --------------------------------------------------------------------------------------------------

        /// A shaded part's finish for its notebook row (the Edit view's swatch), else none.
        static string FinishOf(PartInstance part) => part != null && !part.Shade.IsOriginal ? part.Shade.Name : null;
        // end edit6dof ----------------------------------------------------------------------------------------------------

        // ---------------- D2: units (SPEC §9) ----------------

        /// The on-screen unit changed (UnitsSwitch): every placed part's fit is re-worded (its reason is in the user's
        /// unit: callout, spec card, notebook row), a held part's size callout too, and the array rows (undone ones as
        /// well). Nothing moves; FitReport.Headline, the raw Labels and the logs don't change.
        public void Relabel()
        {
            foreach (var p in m_Placed) if (p != null) p.Evaluate(FitChecker.FindTape(p, frame));
            if (Held != null) { if (Held.Fit != null) Held.Evaluate(FitChecker.FindTape(Held, frame)); else Held.ClearFit(); }
            foreach (var kv in m_Entries) if (kv.Key != null && kv.Value != null) SetDisplay(kv.Value, kv.Key);
            void Row(ArrayGroup g)
            {
                if (g?.Entry == null) return;
                g.Entry.DisplayValue = $"every {AirTools.UI.Copy.Gap(g.Plan.SpacingM * 1000f)}";
                g.Entry.DisplayDetail = $"along {AirTools.UI.Copy.Len(g.Plan.LengthM)}";
            }
            foreach (var op in m_History) Row(op as ArrayGroup);
            foreach (var (op, _) in m_Redo) Row(op as ArrayGroup);
            foreach (var st in Sites.All)   // sitescope: parked sites' array rows too
            {
                foreach (var op in st.History) Row(op as ArrayGroup);
                foreach (var (op, _) in st.Redo) Row(op as ArrayGroup);
            }
        }
    }
}
