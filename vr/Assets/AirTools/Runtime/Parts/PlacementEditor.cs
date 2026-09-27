using System;
using System.Collections.Generic;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// The placement editor: a focused mode for a placed part (the "generated asset" — a candidate's model at true size)
    /// where it can be moved and turned in real time, its placements saved and switched, and its model swapped in place.
    ///
    /// - **Toggle.** Adjust (the chip on the part's card, AppCommands.AdjustPlacement / ToggleAdjust, the agent's
    ///   adjust_placement) opens the adjust panel in the main slot and hands the pinch to the editor (no tool in hand);
    ///   Done, the chip again, another tool, another main-slot window or leaving the world closes it. The part shows its
    ///   frame (PlacementGizmo) and PlacementGuides' true-size box, and the rest of the world's text steps back
    ///   (PlacementFocus: the label pool). Outside adjust mode a placed part is locked (PartTool.CanPickUp): a stray pinch
    ///   only selects it.
    /// - **6 DoF.** Pinch the part (hands or trigger) and it moves with the hand and its ray, keeping the grab offset, and
    ///   turns with it about Up; the panel's pads nudge ± right / up / out by a step in the user's unit (⅜″, fine ⅛″;
    ///   1 cm, fine 2 mm) and turn / tilt / roll by 5° (fine 1°), about the anchor. Axes are the cavity's (insert frame),
    ///   else the surface's under the part, else world up and your facing (PlacementFrame). The readout shows the anchor's
    ///   offset from the gap's insert point (or the auto-placement), the angles, and the fit — in a cavity the part's box
    ///   against the opening (PlacementMath.CavityFit: "Fits the gap · ⅜″ spare", "Too wide by ¾″"), elsewhere FitChecker.
    ///   Snap (on by default): a release near the fit pose goes to it; small offsets and angles snap to centred / on the
    ///   floor / flush / square; off a cavity the part is seated back on its surface. Reset to fit puts it at the
    ///   auto-placement pose.
    /// - **Save.** Save placement files the pose (package coordinates: through re-scales, reloads of the same site and
    ///   revision and the tabletop) in slot A–D of its spot (the cavity, or the free placement); the chips switch between
    ///   them. Each save is a notebook row (type placement_pose, with a place_part-ready glTF pose) and its own undo step;
    ///   the whole adjust session is one undo step (EditHistory). A part placed again into a cavity with a saved placement
    ///   goes to the active slot; a reload of the same site and revision puts parts back on their scene features.
    /// - **Swap.** NextPlacedModel / ShowPlacedModel (cycle_model) load another candidate's model and put it at the same
    ///   placement, anchor to anchor (a cavity: front-bottom-centre, flush with the cabinets; elsewhere the bottom-back-
    ///   centre), one undo step (or part of the adjust session).
    public partial class PlacementEditor : MonoBehaviour, IEditable   // assetgen: partial (PlacementEditor.Look.cs)
    {
        public static PlacementEditor Current { get; private set; }

        [Header("Wiring")]
        public PartTool tool;
        public PartLoader loader;
        public PartsBrowser browser;
        public PlacementPanel panel;
        public PlacementGizmo gizmo;
        [Tooltip("The Adjust chip on the part's card (the spec inspector); shown while a placed part is selected.")]
        public GlassButton adjustChip;

        [Header("Behaviour")]
        [Tooltip("Outside adjust mode a pinch on a placed part only selects it.")]
        public bool lockPlaced = true;
        [Tooltip("A grab turns the part about Up only (its tilt and roll stay; the pads do those).")]
        public bool grabYawOnly = true;
        [Tooltip("The grabbed part follows its target at this rate (1/s).")]
        public float grabFollow = 20f;
        [Tooltip("A pinch whose ray passes this close to the part (scene metres) grabs it.")]
        public float grabReach = 0.35f;
        [Tooltip("Fit re-check interval while it moves (s).")]
        public float fitInterval = 0.1f;
        [Tooltip("Swapped-out models kept loaded for quick cycling back (beyond the ones undo needs).")]
        public int cacheSize = 3;

        public PartInstance Adjusting { get; private set; }
        public bool IsAdjusting => Adjusting != null;
        public bool Fine { get; private set; }
        public bool Snap { get; private set; } = true;
        public bool Grabbing => m_Grab.Active;
        public string LastAction { get; private set; } = "";
        /// A model is loading for a swap (NextPlacedModel waits for it; poll this or LastSwap).
        public bool SwapBusy { get; private set; }
        public string LastSwap { get; private set; } = "";
        public int Swaps { get; private set; }
        /// Bumped when slots, the active slot or the model change (the panel redraws its chips).
        public int SlotsVersion { get; private set; }
        public PlacementStore Store { get; } = new PlacementStore();
        /// Candidates for model swaps instead of Find parts' (the harness, tests); null = PartsBrowser.Candidates.
        public List<PartSummary> CandidatesOverride;
        /// Load catalog parts synchronously (the harness and tests: the shipped fixtures, no server).
        public bool SyncCatalogLoads;

        /// What the editor knows about a placed spot, shared by every model swapped in there. Package coordinates.
        public sealed class Spot
        {
            public string Key;
            /// The removed scene part whose cavity it's in (null: a surface or free placement).
            public string CavityId;
            public PlacementAnchor Anchor;
            public PlacementFrame FramePkg;
            public Vector3 FitAnchorPkg;
            public Quaternion FitRotationPkg = Quaternion.identity;
            public CavityVolume CavityPkg;
            public string Site;
            public int Revision;
            /// Where the part is now (anchor, rotation), for a reload or a re-scale.
            public Vector3 AnchorPkg;
            public Quaternion RotationPkg = Quaternion.identity;
            /// The part's fit is the editor's cavity check (PinnedFit), re-worded on a unit switch.
            public bool OwnFit;
            /// The frame axis along the surface's normal (1 Up: a floor or counter, 2 Out: a wall); −1 none.
            public int NormalAxis = -1;
            public override string ToString() => $"{Key} {(CavityId != null ? (CavityId.StartsWith("opening:") ? CavityId : "cavity " + CavityId) : FramePkg.Kind)} anchor={Anchor}";
        }

        struct PoseSnap { public Vector3 AnchorPkg; public Quaternion RotationPkg; }

        sealed class Op
        {
            public bool IsSave;
            public float At;
            // assetgen: a size / finish change (Size & finish), applied before the pose
            public bool HasLook;
            public PartLookState LookBefore, LookAfter;
            // a pose (and model) change
            public Spot Spot;
            public PartInstance PartBefore, PartAfter;
            public PoseSnap Before, After;
            // a save
            public string Key, Slot, PrevActive;
            public SavedPlacement Prev, Saved;
            public NotebookEntry Entry;
            // edit6dof: an Edit view session may move the part to another spot (Move) and change its shade
            public Spot SpotBefore;
            public bool HasShade;
            public PartShade ShadeBefore, ShadeAfter;
        }

        struct GrabState
        {
            public bool Active, Moved;
            public ToolHand Hand;
            public Pose Pointer0, Pointer;
            public Vector3 Grab0, Anchor0;
            public Vector3 TurnTiltRoll0;
            public Quaternion Rotation0;
        }

        readonly Dictionary<PartInstance, Spot> m_Spots = new Dictionary<PartInstance, Spot>();
        readonly List<Op> m_Undo = new List<Op>(), m_Redo = new List<Op>();
        readonly List<PartInstance> m_Hidden = new List<PartInstance>();
        PartInstance m_SessionPartBefore;
        PoseSnap m_SessionBefore;
        bool m_SessionDirty;
        float m_SessionAt;
        Vector3 m_Ttr;
        GrabState m_Grab;
        ToolKind m_PrevTool;
        bool m_Equipping, m_Closing, m_Reapply;
        float m_NextFit;
        bool m_FitDue;
        int m_SpotCounter, m_Session;
        IToolInput m_Input;
        PartTool m_Hooked;
        ToolManager m_Tools;
        SceneRoot m_Root;

        PartTool Tool => tool != null ? tool : Services.Get<PartTool>();
        Transform Frame { get { var t = Tool; return t != null ? t.frame : null; } }

        // ---------------- lifecycle ----------------

        bool m_Attached;

        void OnEnable() => Attach();
        void OnDisable() => Detach();

        /// Register and hook up (OnEnable; EditMode tests call it by hand, OnEnable doesn't run there).
        public void Attach()
        {
            Current = this;
            if (m_Attached) { Hook(); return; }
            m_Attached = true;
            Services.Register(this);
            EditHistory.Register(this);
            AppState.Changed += OnMode;
            DemoReset.Completed += OnDemoReset;
            UnitsSwitch.Switched += OnUnits;
            RegisterSite();   // sitescope (PlacementEditor.Sites.cs)
            Hook();
        }

        public void Detach()
        {
            if (Adjusting != null) Exit();
            if (Current == this) Current = null;
            if (!m_Attached) return;
            m_Attached = false;
            Services.Unregister(this);
            EditHistory.Unregister(this);
            AppState.Changed -= OnMode;
            DemoReset.Completed -= OnDemoReset;
            UnitsSwitch.Switched -= OnUnits;
            SiteScope.Unregister(this);   // sitescope
            SetInput(null);
            HookTool(null);
            HookTools(null);
            HookRoot(null);
        }

        void Start() => Hook();

        void Hook()
        {
            if (m_Input == null && Services.TryGet<IToolInput>(out var input)) SetInput(input);
            HookTool(Tool);
            if (m_Tools == null && Services.TryGet<ToolManager>(out var tm)) HookTools(tm);
            if (m_Root == null && Services.TryGet<SceneRoot>(out var root)) HookRoot(root);
        }

        public void SetInput(IToolInput input)
        {
            if (m_Input != null) { m_Input.PressStart -= OnPressStart; m_Input.PressMove -= OnPressMove; m_Input.PressEnd -= OnPressEnd; }
            m_Input = input;
            if (m_Input != null) { m_Input.PressStart += OnPressStart; m_Input.PressMove += OnPressMove; m_Input.PressEnd += OnPressEnd; }
        }

        void HookTool(PartTool t)
        {
            if (m_Hooked == t) return;
            if (m_Hooked != null) { m_Hooked.PartPlaced -= OnPartPlaced; m_Hooked.Cleared -= OnCleared; if (m_Hooked.CanPickUp == (Func<PartInstance, bool>)CanPickUp) m_Hooked.CanPickUp = null; }
            if (m_Hooked != null && m_Hooked.HeldSnap == (Func<PartInstance, bool>)SnapHeld) { m_Hooked.HeldSnap = null; m_Hooked.HeldSnapFit = null; }   // assetgen
            if (m_Hooked != null && m_Hooked.KeepRotation == (Func<PartInstance, Quaternion?>)KeptRotation) m_Hooked.KeepRotation = null;   // edit-touch
            m_Hooked = t;
            if (t != null) { t.PartPlaced += OnPartPlaced; t.Cleared += OnCleared; t.CanPickUp = CanPickUp; t.HeldSnap = SnapHeld; t.HeldSnapFit = SnapFit; }   // assetgen: HeldSnap
            if (t != null) t.KeepRotation = KeptRotation;   // edit-touch: the Edit view's new part is only translated
        }

        void HookTools(ToolManager tm)
        {
            if (m_Tools != null) m_Tools.Changed -= OnToolChanged;
            m_Tools = tm;
            if (m_Tools != null) m_Tools.Changed += OnToolChanged;
        }

        void HookRoot(SceneRoot root)
        {
            if (m_Root != null) { m_Root.ContentChanged -= OnContent; m_Root.Rescaled -= OnRescaled; }
            m_Root = root;
            if (m_Root != null) { m_Root.ContentChanged += OnContent; m_Root.Rescaled += OnRescaled; }
        }

        bool CanPickUp(PartInstance p) => !lockPlaced;

        void OnMode(AppMode from, AppMode to) { if (to != AppMode.World && Adjusting != null) Exit(); }

        void OnToolChanged(ToolKind kind) { if (!m_Equipping && Adjusting != null && kind != ToolKind.None) Exit(); }

        void OnContent()
        {
            if (Adjusting != null && m_Spots.TryGetValue(Adjusting, out var s) && !SameScene(s)) Exit();
            m_Reapply = true;
        }

        void OnRescaled(float factor) => m_Reapply = true;

        void OnUnits(UnitSystem u)
        {
            foreach (var kv in m_Spots)
                if (kv.Key != null && kv.Value.OwnFit && IsPlaced(kv.Key)) FitNow(kv.Key, record: true);
            panel?.Relabel();
        }

        void OnDemoReset(string report) => ResetSession();

        void OnCleared()
        {
            if (Adjusting != null) Abort();
            m_Undo.Clear();
            m_Redo.Clear();
            foreach (var p in m_Hidden) DestroyPart(p);
            m_Hidden.Clear();
            ClearLiveSpots();   // sitescope: was m_Spots.Clear(); parts parked with other sites keep their spots
            SwapBusy = false;
            m_Session++;
            ServerCantResize = false;   // assetgen: ask again (the server may have been patched or switched)
            EditHistory.NotifyChanged();
        }

        /// A demo reset: nothing adjusted, no saved placements, no history.
        public void ResetSession()
        {
            OnCleared();
            Store.Clear();
            Fine = false;
            Snap = true;
            SlotsVersion++;
            LastAction = "reset";
        }

        // ---------------- spaces and poses ----------------

        /// The loaded scene's package frame inside SceneRoot (identity without one).
        public PackageSpace Space()
        {
            var root = m_Root != null ? m_Root : Services.Get<SceneRoot>();
            var frame = Frame;
            if (root == null || root.Content == null || frame == null) return PackageSpace.Identity;
            var c = root.Content.transform;
            return new PackageSpace
            {
                Position = frame.InverseTransformPoint(c.position),
                Rotation = PlacementMath.Normalize(PlacementMath.Conj(frame.rotation) * c.rotation),
                Scale = c.lossyScale.x / Mathf.Max(frame.lossyScale.x, 1e-6f),
            };
        }

        Vector3 RootPos(PartInstance p) => Frame != null ? Frame.InverseTransformPoint(p.transform.position) : p.transform.position;
        Quaternion RootRot(PartInstance p) => Frame != null ? PlacementMath.Normalize(PlacementMath.Conj(Frame.rotation) * p.transform.rotation) : p.transform.rotation;
        Vector3 ToWorld(Vector3 root) => Frame != null ? Frame.TransformPoint(root) : root;
        Vector3 DirToWorld(Vector3 root) => Frame != null ? Frame.TransformDirection(root) : root;
        Pose ToRootPose(Pose world) => Frame == null ? world
            : new Pose(Frame.InverseTransformPoint(world.position), PlacementMath.Normalize(PlacementMath.Conj(Frame.rotation) * world.rotation));

        void SetRootPose(PartInstance p, Vector3 origin, Quaternion rotation)
        {
            if (Frame != null) p.transform.SetPositionAndRotation(Frame.TransformPoint(origin), Frame.rotation * rotation);
            else p.transform.SetPositionAndRotation(origin, rotation);
        }

        public PlacementFrame FrameOf(Spot s) => PlacementMath.ToRoot(s.FramePkg, Space());

        Vector3 AnchorRoot(PartInstance p, Spot s) => PlacementMath.AnchorOf(RootPos(p), RootRot(p), p.LocalBox, s.Anchor);

        PoseSnap SnapOf(PartInstance p, Spot s)
        {
            var sp = Space();
            return new PoseSnap { AnchorPkg = sp.ToPackage(AnchorRoot(p, s)), RotationPkg = sp.RotToPackage(RootRot(p)) };
        }

        void SetPose(PartInstance p, Spot s, PoseSnap pose)
        {
            var sp = Space();
            var rot = sp.RotToRoot(pose.RotationPkg);
            SetRootPose(p, PlacementMath.OriginFor(sp.ToRoot(pose.AnchorPkg), rot, p.LocalBox, s.Anchor), rot);
            s.AnchorPkg = pose.AnchorPkg;
            s.RotationPkg = pose.RotationPkg;
        }

        /// Put the part's anchor at `anchor` (root) turned by `turnTiltRoll` from the fit rotation in the spot's frame.
        void Apply(PartInstance p, Spot s, Vector3 anchor, Vector3 turnTiltRoll)
        {
            var sp = Space();
            var f = PlacementMath.ToRoot(s.FramePkg, sp);
            var rot = PlacementMath.Rotation(f, sp.RotToRoot(s.FitRotationPkg), turnTiltRoll);
            SetRootPose(p, PlacementMath.OriginFor(anchor, rot, p.LocalBox, s.Anchor), rot);
            s.AnchorPkg = sp.ToPackage(anchor);
            s.RotationPkg = sp.RotToPackage(rot);
        }

        /// The part's offset from its fit anchor (right, up, out; scene metres) and its turn / tilt / roll (degrees).
        public bool TryReadout(PartInstance p, out Vector3 offset, out Vector3 turnTiltRoll)
        {
            offset = turnTiltRoll = Vector3.zero;
            if (p == null || !m_Spots.TryGetValue(p, out var s)) return false;
            var sp = Space();
            var f = PlacementMath.ToRoot(s.FramePkg, sp);
            offset = PlacementMath.Offset(f, AnchorRoot(p, s), sp.ToRoot(s.FitAnchorPkg));
            turnTiltRoll = p == Adjusting ? m_Ttr : PlacementMath.TurnTiltRoll(f, sp.RotToRoot(s.FitRotationPkg), RootRot(p));
            return true;
        }

        public Spot SpotOf(PartInstance p) => p != null && m_Spots.TryGetValue(p, out var s) ? s : null;

        bool SameScene(Spot s)
        {
            var root = m_Root != null ? m_Root : Services.Get<SceneRoot>();
            return s != null && s.Site == SiteOf(root) && s.Revision == RevisionOf(root);
        }

        static string SiteOf(SceneRoot r) => r != null ? r.Site ?? "" : "";
        static int RevisionOf(SceneRoot r) => r != null && r.Manifest != null ? r.Manifest.revision : 0;

        bool IsPlaced(PartInstance p)
        {
            var t = Tool;
            if (p == null || t == null) return false;
            var list = t.PlacedParts;
            for (int i = 0; i < list.Count; i++) if (list[i] == p) return true;
            return false;
        }

        /// The part the commands act on: the one being adjusted, else the selected placed part, else the last placed.
        public PartInstance Target()
        {
            if (Adjusting != null) return Adjusting;
            var t = Tool;
            if (t == null) return null;
            if (t.Selected != null && IsPlaced(t.Selected)) return t.Selected;
            var list = t.PlacedParts;
            for (int i = list.Count - 1; i >= 0; i--) if (list[i] != null) return list[i];
            return null;
        }

        // ---------------- spots ----------------

        void OnPartPlaced(PartInstance part)
        {
            if (part == null) return;
            Prune();
            var s = BuildSpot(part);
            m_Spots[part] = s;
            // edit-touch: the Edit view's new part keeps the rotation it was given: no saved placement or opening snap here.
            if (part == HeldTurnPart) { ClearHeldTurn(); SlotsVersion++; return; }
            var saved = s.CavityId != null ? Store.Active(s.Key) : null;
            if (saved != null)
            {
                SetPose(part, s, new PoseSnap { AnchorPkg = saved.AnchorPkg, RotationPkg = saved.RotationPkg });
                FitNow(part, record: true);
                LastAction = $"re-placed {part.Spec.id} at saved placement {saved.Slot}";
                Log.Info($"Placement: {LastAction} ({s})");
                UiToast.Show($"Back at your placement {saved.Slot}", ColorRole.Info);
            }
            else SnapIntoOpening(part, s);   // assetgen: a part that fits a taped opening goes in square and centred
            SlotsVersion++;
        }

        /// The spot a part was just placed at: in a removed part's cavity (its insert frame and pose), else on the
        /// surface under it, else in front of you. Its fit pose is where it is now (or the cavity insert).
        Spot BuildSpot(PartInstance part)
        {
            var root = m_Root != null ? m_Root : Services.Get<SceneRoot>();
            var sp = Space();
            var origin = RootPos(part);
            var rot = RootRot(part);
            var s = new Spot { Site = SiteOf(root), Revision = RevisionOf(root) };
            if (TryCavity(part, origin, rot, sp, out var id, out var frame, out var volume))
            {
                s.CavityId = id;
                s.Anchor = PlacementAnchor.FrontBottomCentre;
                s.FramePkg = frame;
                s.CavityPkg = volume;
                s.FitAnchorPkg = frame.Origin;
                s.FitRotationPkg = frame.Facing;
                s.Key = PlacementMath.Key(s.Site, s.Revision, "cavity:" + id);
            }
            else if (BuildOpeningSpot(part, origin, rot, sp, s)) { }   // assetgen: at a taped opening it's made for
            else
            {
                s.Anchor = PlacementAnchor.BottomBackCentre;
                var anchor = PlacementMath.AnchorOf(origin, rot, part.LocalBox, s.Anchor);
                var viewer = ViewerRoot(anchor);
                var up = Vector3.up;   // SceneRoot's up is the world's (the tabletop only scales and moves it)
                var normal = part.SurfaceNormal.sqrMagnitude > 0.5f
                    ? (Frame != null ? Frame.InverseTransformDirection(part.SurfaceNormal) : part.SurfaceNormal) : Vector3.zero;
                var f = normal.sqrMagnitude > 0.5f ? PlacementMath.Surface(anchor, normal, up, viewer - anchor) : PlacementMath.View(anchor, up, viewer - anchor);
                if (f.Kind == "surface") s.NormalAxis = Mathf.Abs(Vector3.Dot(normal.normalized, up)) > 0.7f ? 1 : 2;
                s.FramePkg = PlacementMath.ToPackage(f, sp);
                s.FitAnchorPkg = sp.ToPackage(anchor);
                s.FitRotationPkg = sp.RotToPackage(rot);
                s.Key = PlacementMath.Key(s.Site, s.Revision, $"spot{++m_SpotCounter}:{part.Spec.id}");
            }
            s.AnchorPkg = sp.ToPackage(PlacementMath.AnchorOf(origin, rot, part.LocalBox, s.Anchor));
            s.RotationPkg = sp.RotToPackage(rot);
            return s;
        }

        Spot EnsureSpot(PartInstance part)
        {
            if (m_Spots.TryGetValue(part, out var s)) return s;
            s = BuildSpot(part);
            m_Spots[part] = s;
            return s;
        }

        Vector3 ViewerRoot(Vector3 fallbackNear)
        {
            var cam = Camera.main;
            if (cam == null) return fallbackNear + Vector3.forward;
            return Frame != null ? Frame.InverseTransformPoint(cam.transform.position) : cam.transform.position;
        }

        /// The removed scene part whose cavity holds the part's box centre (10 cm slack), nearest first.
        bool TryCavity(PartInstance part, Vector3 origin, Quaternion rot, PackageSpace sp, out string id, out PlacementFrame frame, out CavityVolume volume)
        {
            id = null; frame = default; volume = default;
            if (!Services.TryGet<SceneParts>(out var parts) || parts == null || parts.Doc == null || parts.RemovedCount == 0) return false;
            var centrePkg = sp.ToPackage(origin + rot * part.LocalBox.center);
            float slack = 0.1f / Mathf.Max(Mathf.Abs(sp.Scale), 1e-6f);
            float best = float.MaxValue;
            foreach (var rid in parts.RemovedIds)
            {
                var c = parts.Doc.Find(rid);
                if (c == null || !CavityBox.TryFrom(c, out var box, out _)) continue;
                var f = PlacementMath.Cavity(box.P, box.U, box.D);
                var half = (box.Max - box.Min) * 0.5f;
                var v = new CavityVolume { Centre = box.Centre, Right = f.Right, Up = f.Up, Out = f.Out, Half = half, OpenTop = box.OpenTop };
                var q = centrePkg - v.Centre;
                var rud = new Vector3(Mathf.Abs(Vector3.Dot(q, v.Right)), Mathf.Abs(Vector3.Dot(q, v.Up)), Mathf.Abs(Vector3.Dot(q, v.Out)));
                if (rud.x > half.x + slack || rud.y > half.y + slack || rud.z > half.z + slack) continue;
                float score = rud.x / Mathf.Max(half.x, 1e-4f) + rud.y / Mathf.Max(half.y, 1e-4f) + rud.z / Mathf.Max(half.z, 1e-4f);
                if (score >= best) continue;
                best = score;
                id = c.id; frame = f; volume = v;
            }
            return id != null;
        }

        static CavityVolume VolumeToRoot(CavityVolume v, PackageSpace sp) => new CavityVolume
        {
            Centre = sp.ToRoot(v.Centre), Right = sp.DirToRoot(v.Right), Up = sp.DirToRoot(v.Up), Out = sp.DirToRoot(v.Out),
            Half = v.Half * Mathf.Abs(sp.Scale), OpenTop = v.OpenTop,
        };

        void Prune()
        {
            List<PartInstance> dead = null;
            foreach (var kv in m_Spots) if (kv.Key == null) (dead ??= new List<PartInstance>()).Add(kv.Key);
            if (dead != null) foreach (var d in dead) m_Spots.Remove(d);
            m_Hidden.RemoveAll(p => p == null);
        }

        // ---------------- fit ----------------

        /// The part's fit where it is now: in a cavity the editor's check against the opening (shown as its pinned fit),
        /// elsewhere the app's own (FitChecker). `record`: also update its notebook row.
        public void FitNow(PartInstance part, bool record)
        {
            m_FitDue = false;
            m_NextFit = Time.unscaledTime + fitInterval;
            var t = Tool;
            if (part == null || t == null) return;
            var s = SpotOf(part);
            if (s != null && s.CavityId != null)
            {
                var k = PlacementMath.Clearance(VolumeToRoot(s.CavityPkg, Space()), part.LocalBox, RootPos(part), RootRot(part));
                part.PinnedFit = IsOpening(s) ? OpeningMath.Fit(k) : PlacementMath.CavityFit(k);   // assetgen: "Fits the opening"
                s.OwnFit = true;
            }
            else
            {
                part.PinnedFit = null;
                if (s != null) s.OwnFit = false;
            }
            t.Refit(part, record);
        }

        // ---------------- adjust mode ----------------

        /// Adjust `part` (default: the target). False when there's no placed part or the world isn't open. edit6dof:
        /// `openPanel` false is the Edit view's session (BeginEdit): no adjust panel, no toast.
        public bool Enter(PartInstance part = null, bool openPanel = true)
        {
            part = part != null ? part : Target();
            if (part == null || !IsPlaced(part))
            {
                LastAction = "adjust: place a part first";
                UiToast.Show("Place a part first, then adjust it", ColorRole.Info);
                return false;
            }
            if (AppState.Mode != AppMode.World)
            {
                LastAction = "adjust: open the world first";
                UiToast.Show("Adjust works in the world", ColorRole.Info);
                return false;
            }
            if (Adjusting == part) return true;
            if (Adjusting != null) Exit();
            var s = EnsureSpot(part);
            Adjusting = part;
            m_Grab = default;
            Rebaseline();
            Snap = SnapService.Enabled;
            m_PrevTool = m_Tools != null ? m_Tools.Active : ToolKind.None;
            if (m_Tools != null && m_Tools.Active != ToolKind.None) { m_Equipping = true; m_Tools.Equip(ToolKind.None); m_Equipping = false; }
            PlacementFocus.Instance.Set(true);
            if (openPanel) panel?.Open();   // edit6dof: the Edit view has its own
            Tool?.Select(part);
            SlotsVersion++;
            LastAction = $"adjusting {part.Spec.id} ({s})";
            Log.Info($"Placement: {LastAction}");
            if (openPanel) UiToast.Show(InputMode.Controllers ? "Adjusting · pull the trigger on it to move it" : "Adjusting · pinch it to move it", ColorRole.Info);
            return true;
        }

        /// Leave adjust mode: the session so far becomes one undo step, the part locks, the tool in hand comes back.
        public bool Exit()
        {
            if (Adjusting == null) return false;
            if (m_Grab.Active) EndGrab();
            CommitSession();
            var part = Adjusting;
            Adjusting = null;
            m_Grab = default;
            EditViewActive = false;   // edit6dof
            PlacementFocus.Instance.Set(false);
            gizmo?.Hide();
            if (panel != null && panel.IsOpen && !m_Closing) { m_Closing = true; panel.Close(); m_Closing = false; }
            if (m_Tools != null && m_Tools.Active == ToolKind.None && m_PrevTool != ToolKind.None) { m_Equipping = true; m_Tools.Equip(m_PrevTool); m_Equipping = false; }
            if (IsPlaced(part)) FitNow(part, record: true);
            SlotsVersion++;
            LastAction = $"done adjusting {part.Spec.id}";
            Log.Info($"Placement: {LastAction}");
            EditHistory.NotifyChanged();
            return true;
        }

        /// The adjusted part went away (undone, removed, cleared): leave without a commit.
        void Abort()
        {
            m_SessionDirty = false;
            m_Grab = default;
            Adjusting = null;
            EditViewActive = false;   // edit6dof
            PlacementFocus.Instance.Set(false);
            gizmo?.Hide();
            if (panel != null && panel.IsOpen && !m_Closing) { m_Closing = true; panel.Close(); m_Closing = false; }
            if (m_Tools != null && m_Tools.Active == ToolKind.None && m_PrevTool != ToolKind.None) { m_Equipping = true; m_Tools.Equip(m_PrevTool); m_Equipping = false; }
            SlotsVersion++;
            LastAction = "adjust closed: the part went away";
            EditHistory.NotifyChanged();
        }

        public bool Toggle() => Adjusting != null ? Exit() : Enter();

        void Rebaseline()
        {
            var p = Adjusting;
            if (p == null) return;
            var s = EnsureSpot(p);
            m_SessionPartBefore = p;
            m_SessionBefore = SnapOf(p, s);
            m_SessionSpotBefore = s;          // edit6dof
            m_SessionShadeBefore = p.Shade;   // edit6dof
            m_SessionDirty = false;
            var sp = Space();
            m_Ttr = PlacementMath.TurnTiltRoll(PlacementMath.ToRoot(s.FramePkg, sp), sp.RotToRoot(s.FitRotationPkg), RootRot(p));
        }

        /// The first change of a session is a new edit: this tool's redo goes, and every other tool's (EditHistory.Edited).
        void MarkDirty()
        {
            m_FitDue = true;
            if (m_SessionDirty) return;
            m_SessionDirty = true;
            m_SessionAt = EditHistory.Stamp();   // newer than every edit before it (a tie would undo the placement first)
            DropRedo();
            EditHistory.Edited(this);
        }

        void CommitSession()
        {
            if (!m_SessionDirty || Adjusting == null) { m_SessionDirty = false; return; }
            var s = EnsureSpot(Adjusting);
            var op = SessionOp(s);   // edit6dof: with the spot before and the shade
            m_SessionDirty = false;
            // A session that ends where it began (switched A → B → back) is no step at all.
            bool same = op.PartBefore == op.PartAfter && (op.Before.AnchorPkg - op.After.AnchorPkg).sqrMagnitude < 1e-12f
                        && Mathf.Abs(Quaternion.Dot(op.Before.RotationPkg, op.After.RotationPkg)) > 0.9999999f
                        && (op.SpotBefore == null || op.SpotBefore == op.Spot) && (!op.HasShade || op.ShadeBefore.Same(op.ShadeAfter));
            if (same) { EditHistory.NotifyChanged(); return; }
            Push(op);
            m_SessionPartBefore = Adjusting;
            m_SessionBefore = op.After;
        }

        // ---------------- moves ----------------

        /// Move by (right, up, out) metres and turn by (turn, tilt, roll) degrees in the part's frame, about its anchor.
        /// While adjusting it joins the session; otherwise it's its own undo step (a voice "move it left an inch").
        public bool Nudge(Vector3 rudMetres, Vector3 turnTiltRoll, PartInstance part = null)
        {
            part = part != null ? part : Target();
            if (part == null || !IsPlaced(part)) { LastAction = "nudge: no placed part"; return false; }
            var s = EnsureSpot(part);
            var before = SnapOf(part, s);
            var sp = Space();
            var f = PlacementMath.ToRoot(s.FramePkg, sp);
            var ttr = part == Adjusting ? m_Ttr : PlacementMath.TurnTiltRoll(f, sp.RotToRoot(s.FitRotationPkg), RootRot(part));
            ttr = new Vector3(PlacementMath.Wrap180(ttr.x + turnTiltRoll.x), PlacementMath.Wrap180(ttr.y + turnTiltRoll.y), PlacementMath.Wrap180(ttr.z + turnTiltRoll.z));
            var anchor = AnchorRoot(part, s) + f.Vector(rudMetres);
            Apply(part, s, anchor, ttr);
            Moved(part, s, before, ttr);
            LastAction = $"nudged {part.Spec.id} by ({rudMetres.x * 1000f:0.#}, {rudMetres.y * 1000f:0.#}, {rudMetres.z * 1000f:0.#}) mm, " +
                         $"({turnTiltRoll.x:0.#}, {turnTiltRoll.y:0.#}, {turnTiltRoll.z:0.#})°";
            return true;
        }

        /// A pad press: 0 right, 1 left, 2 up, 3 down, 4 out, 5 in — one step in the user's unit.
        public bool NudgeStep(int dir)
        {
            float m = PlacementMath.StepMetres(UiSettings.UnitSystem, Fine);
            var v = dir switch { 0 => new Vector3(m, 0, 0), 1 => new Vector3(-m, 0, 0), 2 => new Vector3(0, m, 0), 3 => new Vector3(0, -m, 0), 4 => new Vector3(0, 0, m), _ => new Vector3(0, 0, -m) };
            return Nudge(v, Vector3.zero);
        }

        /// A pad press: 0 turn +, 1 turn −, 2 tilt +, 3 tilt −, 4 roll +, 5 roll − — 5° (fine 1°).
        public bool RotateStep(int dir)
        {
            float d = PlacementMath.StepDegrees(Fine);
            var v = dir switch { 0 => new Vector3(d, 0, 0), 1 => new Vector3(-d, 0, 0), 2 => new Vector3(0, d, 0), 3 => new Vector3(0, -d, 0), 4 => new Vector3(0, 0, d), _ => new Vector3(0, 0, -d) };
            return Nudge(Vector3.zero, v);
        }

        /// After any move: the session (adjusting) or its own undo step; the active slot is left (the pose is new).
        void Moved(PartInstance part, Spot s, PoseSnap before, Vector3 ttr)
        {
            if (part == Adjusting) { m_Ttr = ttr; MarkDirty(); FitNow(part, record: false); }
            else { Push(new Op { Spot = s, PartBefore = part, PartAfter = part, Before = before, After = SnapOf(part, s) }); FitNow(part, record: true); }
            var spot = Store.Spot(s.Key);
            if (spot != null && spot.Active != null) { spot.Active = null; SlotsVersion++; }
        }

        /// Back to the auto-placement pose (the cavity's insert, or where it was first placed).
        public bool ResetToFit(PartInstance part = null)
        {
            part = part != null ? part : Target();
            if (part == null || !IsPlaced(part)) { LastAction = "reset: no placed part"; return false; }
            var s = EnsureSpot(part);
            var before = SnapOf(part, s);
            Apply(part, s, Space().ToRoot(s.FitAnchorPkg), Vector3.zero);
            Moved(part, s, before, Vector3.zero);
            LastAction = $"reset {part.Spec.id} to its fit pose";
            Log.Info($"Placement: {LastAction}");
            return true;
        }

        public void SetFine(bool on) { Fine = on; LastAction = on ? "fine steps" : "normal steps"; }
        public void SetSnap(bool on) { Snap = on; LastAction = on ? "snap on" : "snap off"; }

        // ---------------- grab ----------------

        void OnPressStart(ToolHand hand, Pose pointer)
        {
            if (Adjusting == null || m_Grab.Active || EditViewActive) return;   // edit6dof: the Edit view takes the presses
            if (!GrabPoint(Adjusting, pointer, out var grabWorld))
            {
                // A pinch on another placed part adjusts that one instead (this session is committed).
                if (Physics.Raycast(new Ray(pointer.position, pointer.forward), out var hit, 60f, PartLayers.PartsMask, QueryTriggerInteraction.Ignore)
                    && hit.collider.GetComponentInParent<PartInstance>() is PartInstance other && other != Adjusting && IsPlaced(other))
                    Enter(other);
                return;
            }
            var s = EnsureSpot(Adjusting);
            m_Grab = new GrabState
            {
                Active = true, Hand = hand, Pointer0 = ToRootPose(pointer), Pointer = ToRootPose(pointer),
                Grab0 = Frame != null ? Frame.InverseTransformPoint(grabWorld) : grabWorld, Anchor0 = AnchorRoot(Adjusting, s),
                TurnTiltRoll0 = m_Ttr, Rotation0 = RootRot(Adjusting),
            };
            LastAction = $"grabbed {Adjusting.Spec.id}";
        }

        void OnPressMove(ToolHand hand, Pose pointer)
        {
            if (!m_Grab.Active || hand != m_Grab.Hand || EditViewActive) return;
            m_Grab.Pointer = ToRootPose(pointer);
        }

        void OnPressEnd(ToolHand hand, Pose pointer)
        {
            if (!m_Grab.Active || hand != m_Grab.Hand) return;
            m_Grab.Pointer = ToRootPose(pointer);
            EndGrab();
        }

        /// Where a press grabs the part: the ray's hit on it, else (a near miss within grabReach) the ray's closest point
        /// to its box centre. False: the press missed it.
        bool GrabPoint(PartInstance part, Pose pointer, out Vector3 grab)
        {
            grab = default;
            var ray = new Ray(pointer.position, pointer.forward);
            if (Physics.Raycast(ray, out var hit, 60f, PartLayers.PartsMask, QueryTriggerInteraction.Ignore) && hit.collider.GetComponentInParent<PartInstance>() == part)
            { grab = hit.point; return true; }
            var c = part.WorldBoxCentre;
            float t = Mathf.Max(0f, Vector3.Dot(c - ray.origin, ray.direction));
            var closest = ray.origin + ray.direction * t;
            float scale = Mathf.Max(SnapService.Scale, 1e-4f);
            float reach = Mathf.Max(grabReach * scale, part.LocalBox.extents.magnitude * part.transform.lossyScale.x * 1.2f);
            if (Vector3.Distance(closest, c) > reach) return false;
            grab = closest;
            return true;
        }

        /// The grab's target: anchor and turn / tilt / roll (root space).
        void GrabTarget(Spot s, out Vector3 anchor, out Vector3 ttr)
        {
            var sp = Space();
            var f = PlacementMath.ToRoot(s.FramePkg, sp);
            anchor = PlacementMath.GrabAnchor(m_Grab.Pointer0, m_Grab.Pointer, m_Grab.Grab0, m_Grab.Anchor0, f.Up, grabYawOnly, out float turn, out var delta);
            if (grabYawOnly) ttr = new Vector3(PlacementMath.Wrap180(m_Grab.TurnTiltRoll0.x + turn), m_Grab.TurnTiltRoll0.y, m_Grab.TurnTiltRoll0.z);
            else ttr = PlacementMath.TurnTiltRoll(f, sp.RotToRoot(s.FitRotationPkg), PlacementMath.Normalize(delta * m_Grab.Rotation0));
        }

        void UpdateGrab(float dt)
        {
            var part = Adjusting;
            var s = EnsureSpot(part);
            GrabTarget(s, out var target, out var ttr);
            var anchor = AnchorRoot(part, s);
            float k = Application.isPlaying ? 1f - Mathf.Exp(-grabFollow * dt) : 1f;
            var a = Vector3.Lerp(anchor, target, k);
            var t = new Vector3(Mathf.LerpAngle(m_Ttr.x, ttr.x, k), Mathf.LerpAngle(m_Ttr.y, ttr.y, k), Mathf.LerpAngle(m_Ttr.z, ttr.z, k));
            if ((a - anchor).sqrMagnitude < 1e-12f && (t - m_Ttr).sqrMagnitude < 1e-8f) return;
            m_Grab.Moved = true;
            Apply(part, s, a, t);
            m_Ttr = t;
            MarkDirty();
        }

        /// Release: the exact target, then Snap (to the fit pose, small offsets and angles, or back onto the surface).
        void EndGrab()
        {
            var part = Adjusting;
            m_Grab.Active = false;
            if (part == null) return;
            var s = EnsureSpot(part);
            GrabTarget(s, out var anchor, out var ttr);
            bool moved = m_Grab.Moved || (anchor - m_Grab.Anchor0).sqrMagnitude > 1e-10f || (ttr - m_Grab.TurnTiltRoll0).sqrMagnitude > 1e-6f;
            if (!moved) { LastAction = $"tapped {part.Spec.id}"; return; }
            string snapped = Snap ? SnapPose(s, ref anchor, ref ttr) : null;
            Apply(part, s, anchor, ttr);
            if (Snap && s.CavityId == null && snapped != "fit" && SeatOnSurface(part)) snapped = snapped != null ? snapped + ", seated" : "seated";
            m_Ttr = ttr;
            MarkDirty();
            FitNow(part, record: false);
            var sp = Space();
            var off = PlacementMath.Offset(PlacementMath.ToRoot(s.FramePkg, sp), AnchorRoot(part, s), sp.ToRoot(s.FitAnchorPkg));
            var st = Store.Spot(s.Key);
            if (st != null && st.Active != null) { st.Active = null; SlotsVersion++; }
            LastAction = $"moved {part.Spec.id} to offset ({off.x * 1000f:0}, {off.y * 1000f:0}, {off.z * 1000f:0}) mm, turn {ttr.x:0.#}°" + (snapped != null ? $", snapped ({snapped})" : "");
            MaybeRespot(part);   // assetgen: into (or out of) a taped opening
            Log.Info($"Placement: {LastAction}");
        }

        /// Snap on release (pure rules, PlacementMath): near the fit pose → the fit pose; else small offsets along the
        /// cavity's axes (all three) or the surface's normal, and small angles, snap. Returns what snapped ("fit",
        /// "cavity", "surface") or null. Off a cavity the release then seats it back on the surface (SeatOnSurface).
        string SnapPose(Spot s, ref Vector3 anchor, ref Vector3 ttr)
        {
            var sp = Space();
            var f = PlacementMath.ToRoot(s.FramePkg, sp);
            var fit = sp.ToRoot(s.FitAnchorPkg);
            var off = PlacementMath.Offset(f, anchor, fit);   // SceneRoot units are scene metres at any tabletop scale
            // assetgen: an opening pulls a part that fits it in from further (8 cm), and never one that doesn't.
            bool opening = IsOpening(s);
            if ((!opening || FitsItsOpening(Adjusting, s)) && PlacementMath.NearFit(off, ttr, opening ? OpeningSnapRadius : 0.03f))
            { anchor = fit; ttr = Vector3.zero; return "fit"; }
            int mask = s.CavityId != null ? 7 : s.NormalAxis >= 0 ? 1 << s.NormalAxis : 0;
            bool any = PlacementMath.SnapSmall(ref off, ref ttr, mask);
            anchor = fit + f.Vector(off);
            return any ? (s.CavityId != null ? "cavity" : "surface") : null;
        }

        /// Off a cavity: the mount face back on the surface it's closest to (within 4 cm), along that surface's normal.
        /// True if it moved.
        bool SeatOnSurface(PartInstance part)
        {
            var mountWorld = part.WorldMountDirection;
            var faceWorld = part.transform.position;   // the origin is the mount-face centre (PartMath)
            float scale = Mathf.Max(SnapService.Scale, 1e-4f);
            if (!SnapService.TrySnap(faceWorld, out var hit, 0.04f * scale, features: false)) return false;
            if (Vector3.Dot(hit.normal, -mountWorld) < Mathf.Cos(20f * Mathf.Deg2Rad)) return false;
            var push = hit.normal * Vector3.Dot(hit.point - faceWorld, hit.normal);
            if (push.sqrMagnitude < 1e-10f) return false;
            part.transform.position += push;
            part.SurfaceNormal = hit.normal;
            part.Surface = hit.collider;
            var st = EnsureSpot(part);
            var sp = Space();
            st.AnchorPkg = sp.ToPackage(AnchorRoot(part, st));
            st.RotationPkg = sp.RotToPackage(RootRot(part));
            return true;
        }

        // ---------------- saved placements ----------------

        /// The target's saved placement in `slot` (null when none).
        public SavedPlacement Saved(string slot)
        {
            var p = Target();
            var s = p != null ? SpotOf(p) : null;
            return s != null ? Store.Get(s.Key, slot) : null;
        }

        /// The target's active slot (the one it's at), or null.
        public string ActiveSlot
        {
            get
            {
                var p = Target();
                var s = p != null ? SpotOf(p) : null;
                return s != null ? Store.Spot(s.Key)?.Active : null;
            }
        }

        /// Save the target's placement in `slot` ("A"…; null: the first free of A–D, else the active one). A notebook row
        /// (placement_pose) and one undo step; an adjust session in progress is committed first (moves, then the save).
        public bool SavePlacement(string slot = null)
        {
            var part = Target();
            if (part == null || !IsPlaced(part)) { LastAction = "save: no placed part"; UiToast.Show("Place a part first", ColorRole.Info); return false; }
            var s = EnsureSpot(part);
            if (Adjusting == part && m_SessionDirty) { CommitSession(); }
            slot = PlacementMath.SlotName(slot) ?? Store.NextSlot(s.Key);
            var sp = Space();
            var f = PlacementMath.ToRoot(s.FramePkg, sp);
            var anchorRoot = AnchorRoot(part, s);
            TryReadout(part, out var off, out var ttr);
            var saved = new SavedPlacement
            {
                Slot = slot, PartId = part.Spec.id, AnchorPkg = sp.ToPackage(anchorRoot), RotationPkg = sp.RotToPackage(RootRot(part)), Anchor = s.Anchor,
                Site = s.Site, Revision = s.Revision, Offset = off, TurnTiltRoll = ttr, At = DateTime.Now,
                Look = CurrentLook(part),   // assetgen: its size and finish too
            };
            var spot = Store.Spot(s.Key);
            string prevActive = spot?.Active;
            var prev = Store.Put(s.Key, saved);
            var entry = Entry(part, s, saved, sp);
            Notebook.Add(entry);
            Push(new Op { IsSave = true, Key = s.Key, Slot = slot, Prev = prev, Saved = saved, PrevActive = prevActive, Entry = entry });
            SlotsVersion++;
            LastAction = $"saved placement {slot} of {part.Spec.id}: {saved}";
            Log.Info($"Placement: {LastAction} [{s.Key}]");
            UiToast.Show($"Saved placement {slot}", ColorRole.Success);
            return true;
        }

        /// Move the target to its saved placement `slot` (a chip; "put it back to B"). Joins the adjust session, else its
        /// own undo step.
        public bool LoadPlacement(string slot)
        {
            var part = Target();
            slot = PlacementMath.SlotName(slot);
            if (part == null || !IsPlaced(part) || slot == null) { LastAction = "load: no placed part or slot"; return false; }
            var s = EnsureSpot(part);
            var saved = Store.Get(s.Key, slot);
            if (saved == null) { LastAction = $"load: no placement {slot}"; UiToast.Show($"No placement {slot} saved here", ColorRole.Info); return false; }
            var before = SnapOf(part, s);
            // assetgen: a placement saved with another size / finish of this part brings them back too (one step)
            var lookBefore = CurrentLook(part);
            bool look = saved.Look.HasValue && saved.PartId == part.Spec.id && !saved.Look.Value.Same(lookBefore);
            if (look && part == Adjusting && m_SessionDirty) { CommitSession(); before = SnapOf(part, s); }
            if (look) ApplyLook(part, s, saved.Look.Value);
            SetPose(part, s, new PoseSnap { AnchorPkg = saved.AnchorPkg, RotationPkg = saved.RotationPkg });
            var sp = Space();
            var ttr = PlacementMath.TurnTiltRoll(PlacementMath.ToRoot(s.FramePkg, sp), sp.RotToRoot(s.FitRotationPkg), RootRot(part));
            if (look)
            {
                Push(new Op { Spot = s, PartBefore = part, PartAfter = part, Before = before, After = SnapOf(part, s), HasLook = true, LookBefore = lookBefore, LookAfter = saved.Look.Value });
                FitNow(part, record: true);
                if (part == Adjusting) { m_Ttr = ttr; Rebaseline(); }
            }
            else if (part == Adjusting) { m_Ttr = ttr; MarkDirty(); FitNow(part, record: false); }
            else { Push(new Op { Spot = s, PartBefore = part, PartAfter = part, Before = before, After = SnapOf(part, s) }); FitNow(part, record: true); }
            Store.SetActive(s.Key, slot);
            SlotsVersion++;
            LastAction = $"loaded placement {slot} of {part.Spec.id}";
            Log.Info($"Placement: {LastAction}");
            return true;
        }

        /// The notebook row of a save: type placement_pose (NotebookExporter), the part, the slot, the anchor (root space)
        /// as its point, and the part's origin pose in the scene file's frame (glTF, anchor "origin": place_part-ready).
        NotebookEntry Entry(PartInstance part, Spot s, SavedPlacement saved, PackageSpace sp)
        {
            var anchorRoot = sp.ToRoot(saved.AnchorPkg);
            string where = IsOpening(s) ? "in the taped opening" : s.CavityId != null ? $"in the {CavityName(s.CavityId)} gap" : null;   // assetgen: openings
            var o = saved.Offset; var t = saved.TurnTiltRoll;
            string label = $"Placement {saved.Slot}: {part.Spec.name} · offset {o.x * 1000f:0} / {o.y * 1000f:0} / {o.z * 1000f:0} mm (right / up / out), " +
                           $"turn {t.x:0.#}° tilt {t.y:0.#}° roll {t.z:0.#}°{(where != null ? " " + where : "")}" + LookLabel(part);   // assetgen
            var e = new NotebookEntry("placement_pose", 0, "", new[] { anchorRoot }, DateTime.Now, -1, label)
            {
                PartId = part.Spec.id, Where = where, Count = 0,
                Slot = saved.Slot,
                Pose = PlacementMath.GltfPose(sp.ToPackage(RootPos(part)), sp.RotToPackage(RootRot(part))),
                DisplayTitle = $"Placement {saved.Slot} · {Copy.Cap(Copy.Noun(part.Spec, part.SearchQuery))}",
                DisplayValue = OffsetWords(o, t),
                DisplayDetail = Copy.FitLine(part.Fit) + LookWords(part),   // assetgen: " · 58½ × 46¾ × 3¼″ made to size · Bronze"
                DimsMm = new double[] { part.Spec.dims_mm.w, part.Spec.dims_mm.h, part.Spec.dims_mm.d },   // assetgen
                Finish = part.LookFinish ?? part.FinishName,   // assetgen
            };
            var root = m_Root != null ? m_Root : Services.Get<SceneRoot>();
            if (root != null) e.NearestCameraId = CameraEvidence.Nearest(e.Points, root.CamerasInRootSpace());
            return e;
        }

        static string CavityName(string id)
        {
            var c = Services.TryGet<SceneParts>(out var parts) ? parts.Doc?.Find(id) : null;
            return c != null ? c.DisplayName.ToLowerInvariant() : id;
        }

        /// "Right 1⅜″ · Out ¼″ · turned 5°" / "At the fit" (the notebook row's value).
        public static string OffsetWords(Vector3 o, Vector3 t)
        {
            var sb = new System.Text.StringBuilder();
            var u = UiSettings.UnitSystem;
            void Len(string pos, string neg, float m)
            {
                if (PlacementMath.Quantum(m, u) == 0) return;
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append(m < 0 ? neg : pos).Append(' ');
                PlacementMath.AppendLength(sb, m, u);
            }
            Len("Right", "Left", o.x); Len("Up", "Down", o.y); Len("Out", "In", o.z);
            if (Mathf.RoundToInt(t.x) != 0) { if (sb.Length > 0) sb.Append(" · "); sb.Append("turned "); PlacementMath.AppendDegrees(sb, t.x); }
            if (Mathf.RoundToInt(t.y) != 0 || Mathf.RoundToInt(t.z) != 0) { if (sb.Length > 0) sb.Append(" · "); sb.Append("tilted"); }
            return sb.Length == 0 ? "At the fit" : PlacementMath.Plain(sb);
        }

        // ---------------- model swaps ----------------

        static readonly List<PartSummary> s_None = new List<PartSummary>();

        public IReadOnlyList<PartSummary> Candidates
        {
            get
            {
                if (CandidatesOverride != null) return CandidatesOverride;
                var b = browser != null ? browser : Services.Get<PartsBrowser>();
                return b != null && b.Candidates != null ? b.Candidates : s_None;
            }
        }

        public bool CanCycleModels
        {
            get
            {
                var c = Candidates;
                var p = Target();
                string id = p != null ? p.Spec.id : null;
                for (int i = 0; i < c.Count; i++) if (c[i] != null && c[i].id != id) return true;
                return false;
            }
        }

        static int IndexOf(IReadOnlyList<PartSummary> list, string id)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].id == id) return i;
            return -1;
        }

        /// "Model 2 of 3" (the panel, under the arrows); "" with nothing to swap to.
        public string ModelLine()
        {
            if (SwapBusy) return "Loading the model…";
            var c = Candidates;
            var p = Target();
            if (p == null || c.Count == 0) return "";
            int i = IndexOf(c, p.Spec.id);
            return i >= 0 ? $"Model {i + 1} of {c.Count}" : $"{c.Count} other model{(c.Count == 1 ? "" : "s")}";
        }

        /// The next (delta +1) or previous (−1) candidate's model at the same placement. True when the swap is done or
        /// its model is loading (SwapBusy; LastSwap says how it went).
        public bool CycleModel(int delta)
        {
            var c = Candidates;
            if (c.Count == 0) { LastSwap = "no candidates"; UiToast.Show("Find parts first to try other models", ColorRole.Info); return false; }
            var part = Target();
            var held = part == null ? Tool?.Held : null;
            string id = part != null ? part.Spec.id : held != null ? held.Spec.id : null;
            int cur = IndexOf(c, id);
            int step = delta >= 0 ? 1 : -1;
            int n = c.Count;
            int i = cur < 0 ? (step > 0 ? 0 : n - 1) : ((cur + delta) % n + n) % n;
            for (int k = 0; k < n && c[i] != null && c[i].id == id; k++) i = ((i + step) % n + n) % n;
            return ShowModel(i);
        }

        /// Candidate i's model at the target's placement (a held part: candidate i into the hand, as Take does).
        public bool ShowModel(int i)
        {
            var c = Candidates;
            if (i < 0 || i >= c.Count || c[i] == null) { LastSwap = $"no candidate {i}"; return false; }
            var part = Target();
            if (part == null)
            {
                var b = browser != null ? browser : Services.Get<PartsBrowser>();
                if (Tool != null && Tool.Held != null && b != null && CandidatesOverride == null) { LastSwap = $"held part: taking candidate {i}"; return b.Select(i); }
                LastSwap = "no placed part";
                UiToast.Show("Place a part first to try other models there", ColorRole.Info);
                return false;
            }
            if (Tool.ArrayOf(part) != null) { LastSwap = "arrays keep their model"; UiToast.Show("A run of parts keeps its model", ColorRole.Info); return false; }
            var summary = c[i];
            if (summary.id == part.Spec.id) { LastSwap = $"already {summary.id}"; return true; }
            if (SwapBusy) { LastSwap = "still loading the last model"; return false; }
            var cached = TakeCached(summary.id);
            if (cached != null) { FinishSwap(part, cached, i); return true; }
            SwapBusy = true;
            SlotsVersion++;
            LastSwap = $"loading {summary.id}";
            int session = m_Session;
            string query = part.SearchQuery;
            bool waiting = true;
            LoadModel(summary, fresh =>
            {
                waiting = false;
                SwapBusy = false;
                SlotsVersion++;
                if (fresh == null) { LastSwap = $"{summary.id} didn't load"; UiToast.Show("Couldn't load that model · try another", ColorRole.Warning); return; }
                if (session != m_Session || !IsPlaced(part)) { Hide(fresh); LastSwap = $"{summary.id} loaded after the part went away"; return; }
                if (string.IsNullOrEmpty(fresh.SearchQuery)) fresh.SearchQuery = query;
                FinishSwap(part, fresh, i);
            });
            if (waiting) UiToast.Show($"Getting {Copy.Clip(string.IsNullOrEmpty(summary.name) ? "the next model" : summary.name, 28)} at real size…", ColorRole.Info);
            return true;
        }

        void LoadModel(PartSummary s, Action<PartInstance> done)
        {
            var l = loader != null ? loader : Services.Get<PartLoader>();
            if (l == null) { done(null); return; }
            if ((SyncCatalogLoads || !Application.isPlaying) && l.catalog != null && l.catalog.Spec(s.id) != null) { done(l.LoadFromCatalog(s.id)); return; }
            l.Load(s, done);
        }

        PartInstance TakeCached(string id)
        {
            for (int k = m_Hidden.Count - 1; k >= 0; k--)
            {
                // A model an undo step still refers to may come back too: steps undo in order, so the one that swapped
                // it out is undone only after the swap that brings it back.
                var p = m_Hidden[k];
                if (p == null || p.Spec.id != id || IsPlaced(p)) continue;
                m_Hidden.RemoveAt(k);
                return p;
            }
            return null;
        }

        /// `fresh` goes where `old` is: anchor on anchor, same turn; `old` is hidden (undo brings it back).
        void FinishSwap(PartInstance old, PartInstance fresh, int index)
        {
            var t = Tool;
            var s = EnsureSpot(old);
            var sp = Space();
            var anchor = AnchorRoot(old, s);
            var rot = RootRot(old);
            var before = SnapOf(old, s);
            if (Frame != null && fresh.transform.parent != Frame) fresh.transform.SetParent(Frame, false);
            fresh.transform.localScale = Vector3.one;
            SetRootPose(fresh, PlacementMath.OriginFor(anchor, rot, fresh.LocalBox, s.Anchor), rot);
            fresh.PinnedFit = null;
            fresh.SurfaceNormal = old.SurfaceNormal;
            fresh.Surface = old.Surface;
            if (!t.ReplacePlaced(old, fresh)) { Hide(fresh); LastSwap = "swap refused: the part isn't placed"; return; }
            m_Spots[fresh] = s;
            m_Hidden.Remove(fresh);
            if (!m_Hidden.Contains(old)) m_Hidden.Add(old);
            s.AnchorPkg = sp.ToPackage(anchor);
            s.RotationPkg = sp.RotToPackage(rot);
            FitNow(fresh, record: true);
            if (Adjusting == old)
            {
                Adjusting = fresh;
                MarkDirty();
                if (m_Grab.Active) m_Grab = default;
            }
            else
            {
                // Cycling through models outside adjust mode: one undo step back to the model you started from.
                var top = m_Undo.Count > 0 ? m_Undo[m_Undo.Count - 1] : null;
                if (top != null && !top.IsSave && top.PartAfter == old && top.Spot == s && m_Redo.Count == 0 && Time.unscaledTime - top.At < 30f)
                {
                    if (top.PartBefore == fresh && top.PartBefore != top.PartAfter)
                    {
                        // Cycled back to the model you started from: the step cancels out. Keeping it would make the next
                        // Undo a no-op (back to the model already showing); dropping it lets Undo reach the edit before.
                        m_Undo.RemoveAt(m_Undo.Count - 1);
                        EditHistory.Edited(this);
                    }
                    else { top.PartAfter = fresh; top.After = SnapOf(fresh, s); top.At = EditHistory.Stamp(); EditHistory.Edited(this); }
                }
                else Push(new Op { Spot = s, PartBefore = old, PartAfter = fresh, Before = before, After = SnapOf(fresh, s) });
            }
            Swaps++;
            SlotsVersion++;
            Collect();
            LastSwap = $"{old.Spec.id} → {fresh.Spec.id} (candidate {index}) at anchor {s.Anchor} {anchor.ToString("F3")}: {fresh.Fit}";
            LastAction = "swapped " + LastSwap;
            Log.Info($"Placement: model {LastSwap}");
            string name = Copy.Clip(string.IsNullOrEmpty(fresh.Spec.name) ? fresh.Spec.id : fresh.Spec.name, 28);
            var fit = fresh.Fit;
            UiToast.Show($"{name} · {Copy.FitLine(fit)}".Trim(' ', '·'),
                fit == null ? ColorRole.Info : fit.Status == FitStatus.Red ? ColorRole.Danger : fit.Status == FitStatus.Amber ? ColorRole.Warning : ColorRole.Success);
        }

        // settings-assets ------------------------------------------------------------------------------------------------

        /// The same part's model made another way (Settings ▸ 3D models, the card's Compare: backend asset modes) goes on
        /// `group` — the part and its array copies — in place (PartInstance.SwapModels: the model child is replaced, the
        /// root, its box, collider and outline stay), then every placed one is put back anchor on anchor like a candidate
        /// swap (FinishSwap): its spot's anchor point and turn don't move (a held part stays in the hand). A listed finish's
        /// tint is put back on. Not an undo step: which generator made the model is a view setting, like the unit.
        /// The count of parts swapped (0: nothing to swap, `model` and `owner` are released).
        public int SwapModelInPlace(IList<PartInstance> group, GameObject model, IDisposable owner, string label)
        {
            var live = new List<PartInstance>();
            if (group != null) foreach (var p in group) if (p != null && !live.Contains(p)) live.Add(p);
            if (model == null) { owner?.Dispose(); return 0; }
            if (live.Count == 0) { PartInstance.SwapModels(live, model, owner, null, null); return 0; }   // releases both
            var anchors = new List<(PartInstance p, Spot s, Vector3 anchor, Quaternion rot)>();
            var tints = new List<(PartInstance p, Color c)>();
            foreach (var p in live)
            {
                var s = SpotOf(p);
                if (s != null && IsPlaced(p) && !p.Held) anchors.Add((p, s, AnchorRoot(p, s), RootRot(p)));
                if (p.FinishColor.HasValue) tints.Add((p, p.FinishColor.Value));
            }
            string finish = live[0].FinishName;
            PartInstance.SwapModels(live, model, owner, finish, null);
            foreach (var (p, c) in tints) p.SetColor(c);
            var sp = Space();
            foreach (var (p, s, anchor, rot) in anchors)
            {
                SetRootPose(p, PlacementMath.OriginFor(anchor, rot, p.LocalBox, s.Anchor), rot);
                s.AnchorPkg = sp.ToPackage(anchor);
                s.RotationPkg = sp.RotToPackage(rot);
                if (s.CavityId != null) FitNow(p, record: false);   // its cavity's own fit (a free part's fit is unchanged)
            }
            Physics.SyncTransforms();
            SlotsVersion++;
            LastSwap = $"{live[0].Spec.id}: model {label} swapped in place on {live.Count} part(s), {anchors.Count} kept at their anchor";
            LastAction = "model " + label;
            Log.Info($"Placement: {LastSwap}");
            return live.Count;
        }

        /// settings-assets: a placed part's anchor point in the placement frame (root space) — the swap tests read it.
        public bool TryAnchor(PartInstance p, out Vector3 anchor, out Quaternion rotation)
        {
            anchor = default;
            rotation = Quaternion.identity;
            if (p == null || !IsPlaced(p)) return false;
            var s = EnsureSpot(p);
            anchor = AnchorRoot(p, s);
            rotation = RootRot(p);
            return true;
        }
        // end settings-assets -----------------------------------------------------------------------------------------

        void Hide(PartInstance p)
        {
            if (p == null) return;
            p.gameObject.SetActive(false);
            if (!m_Hidden.Contains(p)) m_Hidden.Add(p);
            Collect();
        }

        bool Referenced(PartInstance p)
        {
            if (p == null) return false;
            if (m_SessionPartBefore == p && Adjusting != null) return true;
            foreach (var op in m_Undo) if (op.PartBefore == p || op.PartAfter == p) return true;
            foreach (var op in m_Redo) if (op.PartBefore == p || op.PartAfter == p) return true;
            return false;
        }

        /// Destroy swapped-out models nothing can bring back, beyond the `cacheSize` newest (kept for cycling).
        void Collect()
        {
            int keep = cacheSize;
            for (int k = m_Hidden.Count - 1; k >= 0; k--)
            {
                var p = m_Hidden[k];
                if (p == null) { m_Hidden.RemoveAt(k); continue; }
                if (IsPlaced(p)) { m_Hidden.RemoveAt(k); continue; }
                if (Referenced(p)) continue;
                if (keep > 0) { keep--; continue; }
                m_Hidden.RemoveAt(k);
                DestroyPart(p);
            }
        }

        void DestroyPart(PartInstance p)
        {
            if (p == null) return;
            m_Spots.Remove(p);
            p.gameObject.SetActive(false);
            if (Application.isPlaying) Destroy(p.gameObject);
            else DestroyImmediate(p.gameObject);
        }

        /// Swapped-out models held for undo or cycling (the harness counts them).
        public int HiddenModels => m_Hidden.Count;

        // ---------------- history (IEditable) ----------------

        void Push(Op op)
        {
            op.At = EditHistory.Stamp();
            m_Undo.Add(op);
            m_Redo.Clear();
            Collect();
            EditHistory.Edited(this);
        }

        public bool CanUndo => m_Undo.Count > 0 || (Adjusting != null && m_SessionDirty);
        public bool CanRedo => m_Redo.Count > 0;
        public float LastEditAt => Adjusting != null && m_SessionDirty ? m_SessionAt : m_Undo.Count > 0 ? m_Undo[m_Undo.Count - 1].At : float.NegativeInfinity;
        public float LastUndoAt => m_Redo.Count > 0 ? m_Redo[m_Redo.Count - 1].At : float.NegativeInfinity;
        /// Undo steps held (the session in progress not counted).
        public int UndoCount => m_Undo.Count;

        /// Undo the newest step: an adjust session in progress goes back to where it started (adjusting goes on); else
        /// the last committed session, move, swap or save.
        public void Undo()
        {
            if (Adjusting != null && m_SessionDirty)
            {
                if (m_Grab.Active) m_Grab = default;
                var s = EnsureSpot(Adjusting);
                var op = SessionOp(s);   // edit6dof
                m_SessionDirty = false;
                if (ApplyOp(op, undo: true)) { op.At = EditHistory.Stamp(); m_Redo.Add(op); }
                Rebaseline();
                LastAction = "undo: the adjust session so far";
                return;
            }
            while (m_Undo.Count > 0)
            {
                var op = m_Undo[m_Undo.Count - 1];
                m_Undo.RemoveAt(m_Undo.Count - 1);
                if (!ApplyOp(op, undo: true)) continue;
                op.At = EditHistory.Stamp();
                m_Redo.Add(op);
                LastAction = op.IsSave ? $"undo save {op.Slot}" : "undo placement edit";
                return;
            }
        }

        public void Redo()
        {
            if (m_Redo.Count == 0) return;
            var op = m_Redo[m_Redo.Count - 1];
            m_Redo.RemoveAt(m_Redo.Count - 1);
            if (!ApplyOp(op, undo: false)) { Collect(); return; }
            op.At = EditHistory.Stamp();
            m_Undo.Add(op);
            LastAction = op.IsSave ? $"redo save {op.Slot}" : "redo placement edit";
        }

        public void DropRedo()
        {
            if (m_Redo.Count == 0) return;
            m_Redo.Clear();
            Collect();
        }

        bool ApplyOp(Op op, bool undo)
        {
            if (op.IsSave)
            {
                if (undo)
                {
                    if (op.Prev != null) Store.Put(op.Key, op.Prev); else Store.Remove(op.Key, op.Slot);
                    Store.SetActive(op.Key, op.PrevActive);
                    if (op.Entry != null) Notebook.Remove(op.Entry);
                }
                else
                {
                    Store.Put(op.Key, op.Saved);
                    if (op.Entry != null) Notebook.Restore(op.Entry);
                }
                SlotsVersion++;
                return true;
            }
            var from = undo ? op.PartAfter : op.PartBefore;
            var to = undo ? op.PartBefore : op.PartAfter;
            if (from == null || to == null || !IsPlaced(from)) return false;
            if (from != to)
            {
                var t = Tool;
                if (t == null || !t.ReplacePlaced(from, to)) return false;
                m_Spots[to] = op.Spot;
                m_Hidden.Remove(to);
                if (!m_Hidden.Contains(from)) m_Hidden.Add(from);
                if (Adjusting == from) Adjusting = to;
            }
            var spot = undo && op.SpotBefore != null ? op.SpotBefore : op.Spot;   // edit6dof: a Move may have changed its spot
            m_Spots[to] = spot;   // assetgen: the step's own spot (a release may have moved it into an opening since)
            if (op.HasLook) ApplyLook(to, spot, undo ? op.LookBefore : op.LookAfter);   // assetgen: size & finish first
            if (op.HasShade) to.SetShade(undo ? op.ShadeBefore : op.ShadeAfter);   // edit6dof
            SetPose(to, spot, undo ? op.Before : op.After);
            FitNow(to, record: true);
            if (Adjusting == to) Rebaseline();
            var st = Store.Spot(spot.Key);
            if (st != null && st.Active != null) st.Active = null;
            SlotsVersion++;
            return true;
        }

        // ---------------- per frame ----------------

        void Update()
        {
            if (m_Hooked == null || m_Input == null || m_Tools == null || m_Root == null) Hook();
            UpdateChip();
            if (m_Reapply) Reapply();
            var part = Adjusting;
            if (part == null) return;
            if (!IsPlaced(part)) { Abort(); return; }
            if (EditViewActive) return;   // edit6dof: the Edit view shows and checks it (its own panel, no gizmo)
            if (panel != null && !panel.IsOpen && !m_Closing) { Exit(); return; }
            if (m_Grab.Active) UpdateGrab(Time.unscaledDeltaTime);
            var s = EnsureSpot(part);
            var sp = Space();
            var f = PlacementMath.ToRoot(s.FramePkg, sp);
            var anchor = AnchorRoot(part, s);
            var off = PlacementMath.Offset(f, anchor, sp.ToRoot(s.FitAnchorPkg));
            if (panel != null) panel.Show(this, part, off, m_Ttr);
            if (gizmo != null) gizmo.Show(ToWorld(anchor), DirToWorld(f.Right), DirToWorld(f.Up), DirToWorld(f.Out));
            if (m_FitDue && Time.unscaledTime >= m_NextFit) FitNow(part, record: false);
        }

        bool m_ChipShown, m_ChipSelected;

        void UpdateChip()
        {
            if (adjustChip == null) return;
            var t = Tool;
            var sel = t != null ? t.Selected : null;
            bool show = sel != null && AppState.Mode == AppMode.World && (sel == Adjusting || IsPlaced(sel));
            if (show != m_ChipShown || adjustChip.gameObject.activeSelf != show) { m_ChipShown = show; adjustChip.gameObject.SetActive(show); }
            bool on = show && sel == Adjusting;
            if (on != m_ChipSelected) { m_ChipSelected = on; adjustChip.SetSelected(on); }
        }

        /// After a reload of the same site / revision or a re-scale: every placed part back on its scene feature (its
        /// package-space anchor). Parts in an array are left to the part tool.
        void Reapply()
        {
            m_Reapply = false;
            var t = Tool;
            if (t == null) return;
            int n = 0;
            foreach (var kv in m_Spots)
            {
                var p = kv.Key;
                if (p == null || !IsPlaced(p) || !SameScene(kv.Value) || t.ArrayOf(p) != null) continue;
                SetPose(p, kv.Value, new PoseSnap { AnchorPkg = kv.Value.AnchorPkg, RotationPkg = kv.Value.RotationPkg });
                n++;
            }
            if (n == 0) return;
            Physics.SyncTransforms();
            foreach (var kv in m_Spots) if (kv.Key != null && IsPlaced(kv.Key) && SameScene(kv.Value)) FitNow(kv.Key, record: true);
            if (Adjusting != null) Rebaseline();
            Log.Info($"Placement: {n} part(s) back on their scene features");
        }

        /// Tests and the harness: run the per-frame step now (EditMode has no player loop).
        public void Tick() => Update();

        /// One line for the harness: the mode, the part, its offset / angles / fit, slots, history.
        public string Report()
        {
            var p = Target();
            var s = SpotOf(p);
            string pose = "-";
            if (p != null && TryReadout(p, out var off, out var ttr))
                pose = $"offset_mm=({off.x * 1000f:0.#}, {off.y * 1000f:0.#}, {off.z * 1000f:0.#}) turn/tilt/roll=({ttr.x:0.#}, {ttr.y:0.#}, {ttr.z:0.#})";
            var slots = s != null ? Store.Spot(s.Key) : null;
            string list = slots == null ? "-" : string.Join(",", slots.Saved.ConvertAll(x => x.Slot == slots.Active ? $"[{x.Slot}]" : x.Slot));
            return $"adjusting={(Adjusting != null ? Adjusting.Spec.id : "-")} part={(p != null ? p.Spec.id : "-")} spot={(s != null ? s.ToString() : "-")} {pose} {LookReport(p)} " +
                   $"fit=\"{(p?.Fit != null ? Copy.FitLine(p.Fit) : "-")}\" slots={list} fine={Fine} snap={Snap} undo={m_Undo.Count}{(m_SessionDirty ? "+session" : "")} " +
                   $"redo={m_Redo.Count} hidden={m_Hidden.Count} swaps={Swaps} swap=\"{LastSwap}\" last=\"{LastAction}\"";
        }
    }
}
