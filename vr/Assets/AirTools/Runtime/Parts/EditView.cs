using System;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// edit6dof: the isolated Edit view for one part (docs/edit-view.md).
    /// - **Isolated.** Opening it closes every window (the Catalog, the notebook, Settings, sellers, cards, toasts; the
    ///   Catalog and the notebook come back afterwards), takes the world's text off (the label pool: PlacementFocus; the
    ///   annotations: ModelViewDeclutter.EditViewHides), puts the tool down and greys the world (EditDim). The item flies
    ///   (0.5 s ease, at once with Reduce motion) from where it stands to the centre of the view — edit-touch: within
    ///   arm's reach, its arrows 0.42 m and the panel 0.44 m from the eye, 20° down (EditViewMath.Layout), its front
    ///   turned to you — scaled down so its largest side is at most 0.25 m (a fridge; the readout keeps the true size).
    /// - **Orient.** Twelve arrows round it (EditArrows): curved ones turn / tilt / roll it, straight ones move it right /
    ///   up / out. edit-touch: every one is a poke button — a poke steps (5° / ⅜″, fine 1° / ⅛″ with Step), holding it
    ///   repeats (after 0.4 s, then every 0.12 s: HoldRepeat). Pinching the item itself still turns it with your hand
    ///   (the controller's grip, the hand's wrist). Swatches shade it (a tint over its texture; Light / Full). Reset turn,
    ///   Fit to opening when it applies.
    /// - **A placed part** is shown as a copy (the real part stays at its true pose in the room and is what the edits
    ///   change, through PlacementEditor: one undo step for the session, site scoping, swaps and the switch closing it
    ///   all as in Adjust). Save flies it back with its new turn and colour; Cancel reverts and flies it back. Move flies
    ///   it back and lets you drag it round the room — only it moves; surfaces, gaps and openings snap it, the move bar
    ///   shows the fit — until Save (or Cancel). edit-touch: a Move only translates it (its turn is what you just set:
    ///   no surface, gap or opening snap turns it; PlacementEditor.MoveAlong).
    /// - **A new part** (taken from the Catalog, EditView.OpenOnTake) opens here first to orient and colour it; Place puts
    ///   it on the pointer (PartTool) and the pinch that places it saves and closes the view; edit-touch: it keeps the
    ///   rotation it was given here, only translated (PlacementEditor.SetHeldRotation). Grok's place_part skips all this
    ///   (PartTool.PlaceAt).
    /// - **After placement** a part's context menu (PartContextMenu: Edit / View similar / Delete) opens on the controller's
    ///   grip squeeze at it, or a long pinch / trigger hold on it (0.6 s) with the Part tool, Move or nothing in hand.
    /// Input: every control of the view is an ISDK poke button (GlassButton: a fingertip or a controller's poke tip; no
    /// pinch, no ray: edit-touch). The ToolInputHub still carries the context gesture, the turn by hand, Move's drag and
    /// Finish (= Save). No allocation per frame while it runs.
    [DefaultExecutionOrder(600)]
    public class EditView : MonoBehaviour
    {
        public static EditView Current { get; private set; }
        /// A part taken from the Catalog opens here first (orient and colour it, then place it). Off: straight to the hand.
        public static bool OpenOnTake = true;

        [Header("Wiring")]
        public PlacementEditor editor;
        public PartTool tool;
        public Transform head;
        [Tooltip("World-locked, set in front of you when the view opens (the item's centre at its origin): the arrows and the panel.")]
        public Transform stage;
        [Tooltip("Holds the placed part's copy while it's shown at the centre (world space, identity parent).")]
        public Transform proxyRoot;
        public EditArrows arrows;
        public EditViewPanel panel;
        public EditDim dim;
        public PartContextMenu menu;

        [Header("Stage (edit-touch: within arm's reach)")]
        [Tooltip("The arrows this far from the eye (m), like the palm menu and the main slot.")]
        public float touchDistance = 0.42f;
        [Tooltip("… on a line this far below the eye line (degrees): the composition's centre.")]
        public float downDeg = 20f;
        [Tooltip("The panel drawn in to this distance from the eye (m), facing it.")]
        public float panelDistance = 0.44f;
        [Tooltip("A shown item's largest side at most (m): bigger parts are shown scaled down to it.")]
        public float maxSide = 0.25f;
        public float flySeconds = 0.5f;
        [Tooltip("A new part grows in from this fraction of its shown size.")]
        public float newScaleFrom = 0.6f;
        public int itemQueue = 3060;
        [Tooltip("The arrows ring a small item at least this far out (m, the item's shown radius).")]
        public float minRadius = 0.06f;

        /// Text in the view (the panel, Out / In, the pill) and the context menu is built for this read distance: the
        /// farthest of its parts (UiBuild.Distance; ErgonomicsTests reads it).
        public const float ReadDistance = 0.45f;

        [Header("Gesture")]
        public float holdSeconds = 0.6f;

        public EditFlow Flow { get; } = new EditFlow();
        public EditPhase Phase => Flow.Phase;
        public EditKind Kind => Flow.Kind;
        public bool Active => Flow.Active;
        public PartInstance Part { get; private set; }
        /// The shown size relative to true (1 for most parts; a fridge ≈ 0.2).
        public float ShownScale { get; private set; } = 1f;
        public string LastAction { get; private set; } = "";
        public int Opens { get; private set; }
        public int Saves { get; private set; }
        public int Cancels { get; private set; }
        public int Places { get; private set; }
        public int Steps { get; private set; }
        public int Drags { get; private set; }
        /// The arrow being pressed (held, repeating) / hovered (−1: none).
        public int Pressed => m_RepeatArrow;
        public int Hovered => arrows != null ? arrows.Hover : -1;
        /// The new part's turn / tilt / roll at the centre (the stage's frame).
        public Vector3 NewTurn => m_Ttr;
        public bool FullShade => m_Strength >= (EditShades.Light + EditShades.Full) * 0.5f;
        /// edit-touch: the layout on the knob plane as the view last opened (EditViewMath.Layout).
        public EditLayout Layout => m_Layout;
        /// edit-touch: steps fired by holding an arrow in (a counter, like Steps).
        public int Repeats { get; private set; }
        /// Tests and the harness: the clock (default Time.unscaledTime).
        public Func<float> Clock;

        float Now => Clock != null ? Clock() : Time.unscaledTime;
        PlacementEditor Editor => editor != null ? editor : PlacementEditor.Current;
        PartTool Tool => tool != null ? tool : Services.Get<PartTool>();

        IToolInput m_Input;
        ToolInputHub Hub => m_Input as ToolInputHub;
        PartTool m_HookedTool;
        ToolManager m_Tools;

        // the context gesture
        readonly EditHold m_Hold = new EditHold();
        PartInstance m_HoldPart;
        ToolHand m_HoldHand;

        // an arrow held in (edit-touch: a poke steps once, holding it repeats)
        readonly HoldRepeat m_Repeat = new HoldRepeat();
        int m_RepeatArrow = -1;
        bool m_HoldOverride;
        EditLayout m_Layout;
        Vector3 m_Off0, m_Ttr0;

        // the item turned by hand
        bool m_Body;
        ToolHand m_BodyHand;
        Quaternion m_Grip0, m_Rot0;
        readonly OneEuroRotation m_BodyFilter = new OneEuroRotation();

        // Move
        bool m_MoveDrag, m_Moved;
        ToolHand m_MoveHand;
        Pose m_MovePointer;
        float m_NextFit;

        // the new part's turn (stage frame) and shade strength
        Vector3 m_Ttr;
        float m_Strength = EditShades.Full;

        // the fly
        Quaternion m_Yaw = Quaternion.identity;
        float m_FlyStart, m_FlySeconds;
        Pose m_FlyFrom, m_FlyTo;
        float m_ScaleFrom, m_ScaleTo;
        float m_Radius;

        // the placed part's copy
        GameObject m_Proxy;
        Transform m_ProxyChild;
        Vector3 m_ProxyScale, m_ProxyPos;

        // what comes back afterwards
        bool m_ReopenCatalog, m_ReopenNotebook, m_ToolSaved;
        ToolKind m_PrevTool;

        // ---------------- lifecycle ----------------

        void OnEnable()
        {
            Current = this;
            Services.Register(this);
            AppState.Changed += OnMode;
            DemoReset.Completed += OnDemoReset;
            Hook();
        }

        void OnDisable()
        {
            if (Flow.Active) Finish(keep: true);
            if (Current == this) Current = null;
            Services.Unregister(this);
            AppState.Changed -= OnMode;
            DemoReset.Completed -= OnDemoReset;
            SetInput(null);
            HookTool(null);
            if (m_Tools != null) { m_Tools.Changed -= OnToolChanged; m_Tools = null; }
        }

        void Start() => Hook();

        /// Wire the input and the part tool (OnEnable; EditMode tests call it: OnEnable doesn't run there for AddComponent).
        public void Hook()
        {
            Current = this;
            if (m_Input == null && Services.TryGet<IToolInput>(out var input)) SetInput(input);
            HookTool(Tool);
            if (m_Tools == null && Services.TryGet<ToolManager>(out var tm)) { m_Tools = tm; m_Tools.Changed += OnToolChanged; }
        }

        public void SetInput(IToolInput input)
        {
            if (m_Input != null) { m_Input.PressStart -= OnPressStart; m_Input.PressMove -= OnPressMove; m_Input.PressEnd -= OnPressEnd; m_Input.ButtonDown -= OnButton; }
            m_Input = input;
            if (m_Input != null) { m_Input.PressStart += OnPressStart; m_Input.PressMove += OnPressMove; m_Input.PressEnd += OnPressEnd; m_Input.ButtonDown += OnButton; }
        }

        void HookTool(PartTool t)
        {
            if (m_HookedTool == t) return;
            if (m_HookedTool != null) m_HookedTool.PartPlaced -= OnPlaced;
            m_HookedTool = t;
            if (t != null) t.PartPlaced += OnPlaced;
        }

        void OnMode(AppMode from, AppMode to)
        {
            if (to != AppMode.World) { if (Flow.Active) Finish(keep: Kind == EditKind.Placed); menu?.Hide(); }
        }

        void OnDemoReset(string report) { if (Flow.Active) Finish(keep: false); menu?.Hide(); }

        void OnToolChanged(ToolKind kind)
        {
            menu?.Hide();
            // A tool picked from the ring while a new part is at the centre: it goes (its card still has it).
            if (Flow.Active && Kind == EditKind.New && (Phase == EditPhase.Opening || Phase == EditPhase.Orient) && kind != ToolKind.None && !m_Busy)
                Finish(keep: false);
        }

        bool m_Busy;

        // ---------------- open ----------------

        /// Edit a placed part (default: the selected one, else the last placed). False when there's none or the world
        /// isn't open.
        public bool OpenPlaced(PartInstance part = null)
        {
            var e = Editor;
            if (e == null) { LastAction = "edit: no placement editor"; return false; }
            part = part != null ? part : e.Target();
            if (part == null || !part.Placed) { LastAction = "edit: place a part first"; UiToast.Show("Place a part first, then edit it", ColorRole.Info); return false; }
            if (AppState.Mode != AppMode.World) { LastAction = "edit: open the world first"; UiToast.Show("Edit works in the world", ColorRole.Info); return false; }
            if (Flow.Active)
            {
                if (Part == part && Kind == EditKind.Placed) return true;
                Finish(keep: true);
            }
            menu?.Hide();
            m_Busy = true;
            bool ok = e.BeginEdit(part);
            m_Busy = false;
            if (!ok) { LastAction = "edit: " + e.LastAction; return false; }
            Part = part;
            Flow.Fire(EditStep.OpenPlaced);
            Begin();
            LastAction = $"editing {part.Spec.id}";
            Log.Info($"Edit view: {LastAction} (shown at {ShownScale * 100f:0}%)");
            return true;
        }

        /// A new part (just loaded, not in the hand): orient and colour it at the centre, then Place.
        public bool OpenNew(PartInstance part)
        {
            if (part == null || AppState.Mode != AppMode.World) return false;
            if (Flow.Active) Finish(keep: Kind == EditKind.Placed);
            menu?.Hide();
            Part = part;
            part.gameObject.SetActive(true);
            part.Held = false;
            part.Placed = false;
            part.ClearFit();
            m_Ttr = Vector3.zero;
            if (Services.TryGet<ToolManager>(out var tm))
            {
                m_PrevTool = tm.Active;
                m_ToolSaved = true;
                m_Busy = true;
                if (tm.Active != ToolKind.None) tm.Equip(ToolKind.None);
                m_Busy = false;
            }
            PlacementFocus.Instance.Set(true);
            Flow.Fire(EditStep.OpenNew);
            Begin();
            LastAction = $"new {part.Spec.id}: orient it";
            Log.Info($"Edit view: {LastAction} (shown at {ShownScale * 100f:0}%)");
            return true;
        }

        /// The Catalog's Take: open a freshly loaded part here (OpenOnTake). False: put it in the hand as before.
        public static bool TryOpenNew(PartInstance part) =>
            OpenOnTake && part != null && Current != null && Current.isActiveAndEnabled && AppState.Mode == AppMode.World && Current.OpenNew(part);

        void Begin()
        {
            Opens++;
            CloseUi();
            ModelViewDeclutter.EditViewHides = true;
            WorldLabels.Refresh();
            Part.DrawOverDim(true, itemQueue);
            float rootScale = Part.transform.parent != null ? Part.transform.parent.lossyScale.x : 1f;
            var size = Part.LocalBox.size;
            float largest = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * rootScale;
            ShownScale = EditViewMath.FitScale(largest, maxSide);
            // edit-touch: the arrows ring the item's largest side (at least 6 cm out: a hinge doesn't crowd them), the
            // panel tucks into the ring's lower right, all on a plane within reach (EditViewMath.Layout).
            m_Radius = Mathf.Max(Mathf.Min(largest, maxSide) * 0.5f, minRadius);
            bool moves = EditFlow.ShowsMoveArrows(Kind);
            m_Layout = EditViewMath.Layout(m_Radius, moves, arrows != null ? arrows.knobSize : 0.032f,
                panel != null ? panel.panelSize : new Vector2(0.225f, 0.228f), arrows != null ? arrows.readoutSize : new Vector2(0.11f, 0.034f));
            PlaceStage(moves);
            var e = Editor;
            if (panel != null)
            {
                panel.SetUp(Part, Kind, ShownScale, Kind == EditKind.Placed && e != null && e.CanFitToOpening);
                panel.Show(true);
            }
            ShowStage(true);
            if (Kind == EditKind.Placed)
            {
                m_Yaw = FacingYaw();
                BuildProxy();
                var t = Part.transform;
                StartFly(new Pose(t.position, t.rotation), t.lossyScale.x, DisplayPose(out float s), s);
                ApplyFly(0f);
            }
            else
            {
                m_Yaw = Quaternion.identity;
                float ps = Part.transform.parent != null ? Part.transform.parent.lossyScale.x : 1f;
                Part.transform.localScale = Vector3.one * ShownScale * newScaleFrom;
                var from = NewPose();
                Part.transform.localScale = Vector3.one * ShownScale;
                StartFly(from, ps * ShownScale * newScaleFrom, NewPose(), ps * ShownScale);   // world scales
                ApplyFly(0f);
            }
            m_Strength = Part.Shade.IsOriginal ? EditShades.Full : Part.Shade.Strength;
            SyncDim();
        }

        /// edit-touch: the stage, its arrows and the panel in front of you, within reach (world-locked until it closes):
        /// laid out on a plane `touchDistance` ahead and `downDeg` down, the knobs then drawn onto the sphere of that radius
        /// round the eye, the panel at `panelDistance`, each facing the eye.
        void PlaceStage(bool moves)
        {
            if (stage == null) return;
            var eye = EyeTransform();
            var at = eye != null ? eye.position : Vector3.zero;
            var anchor = EditViewMath.EyeFrame(at, eye != null ? eye.forward : Vector3.forward, touchDistance, downDeg);
            var pose = EditViewMath.StagePose(anchor, m_Layout);
            stage.SetPositionAndRotation(pose.position, pose.rotation);
            if (arrows != null)
            {
                arrows.Layout(m_Layout, moves);
                arrows.Curve(at, touchDistance);
            }
            if (panel != null && panel.root != null)
            {
                var p = EditViewMath.PanelPose(at, anchor, m_Layout.Panel, panelDistance);
                panel.root.transform.SetPositionAndRotation(p.position, p.rotation);
            }
        }

        Transform EyeTransform() => head != null ? head : Camera.main != null ? Camera.main.transform : null;
        Vector3 EyePos { get { var e = EyeTransform(); return e != null ? e.position : Vector3.zero; } }

        /// The display turn that brings the part's front (its frame's Out) round to face you.
        Quaternion FacingYaw()
        {
            var e = Editor;
            if (e == null || !e.TryFit(Part, out var f, out _) || stage == null) return Quaternion.identity;
            e.RootToWorld(out _, out var rootRot, out _);
            float yaw = EditViewMath.FacingYaw(rootRot * f.Out, -stage.forward);
            return PlacementMath.AxisAngle(Vector3.up, yaw);
        }

        // ---------------- close ----------------

        /// Save: a placed part's edits are kept (one undo step) and it flies back; out in the room (Move) the new spot is
        /// kept at once. False when there's nothing to save now.
        public bool Save()
        {
            if (!Flow.Active || Kind != EditKind.Placed) return false;
            switch (Phase)
            {
                case EditPhase.Opening:
                case EditPhase.Orient:
                    EndPress();
                    if (!Flow.Fire(EditStep.Save)) return false;
                    StartClose();
                    LastAction = $"saving {Part.Spec.id}";
                    return true;
                case EditPhase.ToRoom:
                case EditPhase.Moving:
                    Flow.Fire(EditStep.Save);
                    if (m_Moved) Editor?.Respot(Part);
                    Editor?.EndEdit(keep: true);
                    Saves++;
                    LastAction = $"saved {Part.Spec.id}{(m_Moved ? " at its new spot" : "")}";
                    Log.Info($"Edit view: {LastAction}");
                    Teardown();
                    return true;
            }
            return false;
        }

        /// Cancel: everything goes back (a placed part to where it was, a new part back to its card).
        public bool Cancel()
        {
            if (!Flow.Active) return false;
            var e = Editor;
            switch (Phase)
            {
                case EditPhase.Opening:
                case EditPhase.Orient:
                    EndPress();
                    if (!Flow.Fire(EditStep.Cancel)) return false;
                    if (Kind == EditKind.Placed) e?.RevertSession();
                    StartClose();
                    LastAction = $"cancelling {Part.Spec.id}";
                    return true;
                case EditPhase.ToRoom:
                case EditPhase.Moving:
                    Flow.Fire(EditStep.Cancel);
                    e?.EndEdit(keep: false);
                    Cancels++;
                    LastAction = $"cancelled: {Part.Spec.id} back where it was";
                    Teardown();
                    return true;
                case EditPhase.Placing:
                    Flow.Fire(EditStep.Cancel);
                    var t = Tool;
                    string id = Part != null ? Part.Spec.id : "?";
                    if (t != null && t.Held == Part) t.Undo();   // the part in hand goes back
                    else if (Part != null && !Part.Placed) DestroyPart(Part);
                    e?.ClearHeldTurn();
                    Cancels++;
                    LastAction = $"cancelled placing {id}";
                    Teardown();
                    RestoreTool();
                    return true;
            }
            return false;
        }

        /// Move (a placed part): it flies back to the room; then drag it to a new spot and Save.
        public bool StartMove()
        {
            if (Kind != EditKind.Placed || Phase != EditPhase.Orient) return false;
            EndPress();
            Flow.Fire(EditStep.Move);
            ShowStage(false);
            SyncDim();
            var t = Part.transform;
            StartFly(CurrentFlyPose(out float s), s, new Pose(t.position, t.rotation), t.lossyScale.x);
            LastAction = $"moving {Part.Spec.id}";
            return true;
        }

        /// Place (a new part): it goes on the pointer with the rotation it was given here (edit-touch: only translated from
        /// now on — PlacementEditor.SetHeldRotation); the pinch that places it saves and closes.
        public bool Place()
        {
            if (Kind != EditKind.New || Phase != EditPhase.Orient) return false;
            var t = Tool;
            if (t == null) return false;
            EndPress();
            Flow.Fire(EditStep.Place);
            ShowStage(false);
            var f = StageFrame();
            var rot = PlacementMath.Rotation(f, f.Facing, m_Ttr);
            Editor?.SetHeldRotation(Part, rot);
            Part.transform.localScale = Vector3.one;
            SyncDim();
            panel?.OpenMoveBar(placing: true);
            m_Busy = true;
            t.Hold(Part);
            m_Busy = false;
            LastAction = $"placing {Part.Spec.id}";
            Log.Info($"Edit view: {LastAction} (turn {m_Ttr})");
            return true;
        }

        /// Close now, without the fly (a switch, leaving the world, another part's edit): `keep` the edits or not.
        public void Finish(bool keep)
        {
            if (!Flow.Active) return;
            var e = Editor;
            EndPress();
            var part = Part;
            if (Kind == EditKind.Placed)
            {
                if (e != null && e.EditViewActive && e.Adjusting == part)
                {
                    if (keep && m_Moved && (Phase == EditPhase.Moving || Phase == EditPhase.ToRoom)) e.Respot(part);
                    e.EndEdit(keep);
                }
            }
            else if (Phase == EditPhase.Placing)
            {
                if (!keep) { var t = Tool; if (t != null && t.Held == part) t.Undo(); }
                e?.ClearHeldTurn();
            }
            else if (part != null && !part.Placed) DestroyPart(part);
            Flow.Fire(EditStep.Abort);
            if (keep) Saves++; else Cancels++;
            LastAction = $"closed ({(keep ? "kept" : "reverted")})";
            Teardown();
            if (Kind == EditKind.New) RestoreTool();
        }

        void StartClose()
        {
            ShowStage(false);
            SyncDim();
            if (Kind == EditKind.Placed)
            {
                var t = Part.transform;
                StartFly(CurrentFlyPose(out float s), s, new Pose(t.position, t.rotation), t.lossyScale.x);
            }
            else
            {
                var at = new Pose(Part.transform.position, Part.transform.rotation);
                float s = Part.transform.lossyScale.x;
                StartFly(at, s, NewPose(), s * newScaleFrom);
            }
        }

        /// A fly ended.
        void Arrive()
        {
            switch (Phase)
            {
                case EditPhase.Opening:
                    Flow.Fire(EditStep.Opened);
                    SyncDim();   // at the centre now: the veil writes its depth
                    break;
                case EditPhase.ToRoom:
                    Flow.Fire(EditStep.Arrived);
                    ShowReal();
                    SyncDim();
                    panel?.OpenMoveBar(placing: false);
                    m_Moved = false;
                    m_NextFit = 0f;
                    UpdateMoveFit();
                    break;
                case EditPhase.Closing:
                    bool keep = Flow.Keep;
                    if (Kind == EditKind.Placed)
                    {
                        ShowReal();
                        Editor?.EndEdit(keep);
                        LastAction = keep ? $"saved {Part.Spec.id}" : $"cancelled: {Part.Spec.id} back as it was";
                    }
                    else
                    {
                        LastAction = $"cancelled the new {Part.Spec.id}";
                        DestroyPart(Part);
                    }
                    if (keep) Saves++; else Cancels++;
                    Log.Info($"Edit view: {LastAction}");
                    var kind = Kind;
                    Flow.Fire(EditStep.Closed);
                    Teardown();
                    if (kind == EditKind.New) RestoreTool();
                    break;
            }
        }

        /// PartTool placed a part: the new one being placed from here → saved, the view closes.
        void OnPlaced(PartInstance part)
        {
            if (!Flow.Active || Kind != EditKind.New || Phase != EditPhase.Placing || part != Part) return;
            Flow.Fire(EditStep.Placed);   // edit-touch: the editor clears the held rotation once it has placed it (OnPartPlaced)
            Places++;
            LastAction = $"placed {part.Spec.id} (saved)";
            Log.Info($"Edit view: {LastAction}");
            Teardown();
        }

        /// Everything back: the dim lifts, the text and windows return, the item draws as before.
        void Teardown()
        {
            if (Part != null)
            {
                Part.DrawOverDim(false);
                if (Part.Model != null && !Part.Model.gameObject.activeSelf) Part.Model.gameObject.SetActive(true);
            }
            DestroyProxy();
            if (dim != null) dim.Set(false, depth: false);
            ModelViewDeclutter.EditViewHides = false;
            if (Kind == EditKind.New) PlacementFocus.Instance.Set(false);
            ShowStage(false);
            panel?.CloseMoveBar();
            StopRepeat();
            m_Body = false;
            m_MoveDrag = false;
            m_Moved = false;
            ReopenUi();
            Part = null;
            if (Flow.Active) Flow.Fire(EditStep.Abort);
            WorldLabels.Refresh();
        }

        void RestoreTool()
        {
            if (!m_ToolSaved) return;
            m_ToolSaved = false;
            if (Services.TryGet<ToolManager>(out var tm) && tm.Active == ToolKind.None && m_PrevTool != ToolKind.None)
            {
                m_Busy = true;
                tm.Equip(m_PrevTool);
                m_Busy = false;
            }
        }

        static void DestroyPart(PartInstance p)
        {
            if (p == null) return;
            p.gameObject.SetActive(false);
            if (Application.isPlaying) Destroy(p.gameObject);
            else DestroyImmediate(p.gameObject);
        }

        // ---------------- the rest of the UI ----------------

        void CloseUi()
        {
            m_ReopenCatalog = Services.TryGet<CatalogWindow>(out var catalog) && catalog.IsOpen;
            if (m_ReopenCatalog) catalog.Hide();
            m_ReopenNotebook = Services.TryGet<NotebookPanel>(out var notebook) && notebook.IsOpen;
            AppCommands.CloseWindows();
            UiToast.DismissAll();
            menu?.Hide();
        }

        void ReopenUi()
        {
            if (m_ReopenCatalog && Services.TryGet<CatalogWindow>(out var catalog) && !catalog.IsOpen) catalog.Open();
            if (m_ReopenNotebook && Services.TryGet<NotebookPanel>(out var notebook)) notebook.SetOpen(true);
            m_ReopenCatalog = m_ReopenNotebook = false;
        }

        /// The veil for the phase: on while the view is open, writing its depth only while the item sits at the centre.
        void SyncDim()
        {
            if (dim != null) dim.Set(EditFlow.Dims(Phase), EditFlow.DimWritesDepth(Phase));
        }

        void ShowStage(bool on)
        {
            if (stage != null && stage.gameObject.activeSelf != on) stage.gameObject.SetActive(on);
            if (!on) { StopRepeat(); arrows?.SetState(-1); }
        }

        // ---------------- the shown copy ----------------

        void BuildProxy()
        {
            DestroyProxy();
            if (Part == null || Part.Model == null || proxyRoot == null) return;
            var src = Part.Model;
            m_Proxy = Instantiate(src.gameObject, proxyRoot, false);
            m_Proxy.name = "EditCopy";
            m_Proxy.SetActive(true);
            foreach (var tr in m_Proxy.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = 0;
            m_ProxyChild = src.childCount > 0 ? src.GetChild(0) : null;
            m_ProxyScale = src.localScale;
            m_ProxyPos = src.localPosition;
            src.gameObject.SetActive(false);   // the real one stays in the room, hidden
            Part.DrawOverDim(true, itemQueue);
        }

        void DestroyProxy()
        {
            if (m_Proxy == null) return;
            m_Proxy.SetActive(false);
            if (Application.isPlaying) Destroy(m_Proxy);
            else DestroyImmediate(m_Proxy);
            m_Proxy = null;
            m_ProxyChild = null;
        }

        void ShowReal()
        {
            DestroyProxy();
            if (Part != null && Part.Model != null && !Part.Model.gameObject.activeSelf) Part.Model.gameObject.SetActive(true);
        }

        /// The placed part's model changed under the copy (a swap, a resize): copy it again.
        void CheckProxy()
        {
            if (m_Proxy == null || Part == null || Part.Model == null) return;
            var src = Part.Model;
            var child = src.childCount > 0 ? src.GetChild(0) : null;
            if (child != m_ProxyChild || src.localScale != m_ProxyScale || src.localPosition != m_ProxyPos)
            {
                src.gameObject.SetActive(true);
                BuildProxy();
            }
        }

        /// The placed part as shown at the centre (world pose of its root; `worldScale` its world scale).
        Pose DisplayPose(out float worldScale)
        {
            var t = Part.transform;
            worldScale = t.lossyScale.x * ShownScale;
            var c = stage != null ? stage.position : t.position;
            return EditViewMath.Display(t.position, t.rotation, Part.WorldBoxCentre, c, m_Yaw, ShownScale);
        }

        /// The stage's frame (world): up, and Out towards you — a new part's turn / tilt / roll are in it.
        PlacementFrame StageFrame()
        {
            var c = stage != null ? stage.position : Vector3.zero;
            var toEye = stage != null ? -stage.forward : Vector3.back;
            return PlacementMath.View(c, Vector3.up, toEye);
        }

        /// A new part at the centre with its turn (its box centre on the stage).
        Pose NewPose()
        {
            var f = StageFrame();
            var rot = PlacementMath.Rotation(f, f.Facing, m_Ttr);
            var c = stage != null ? stage.position : Vector3.zero;
            float s = Part.transform.lossyScale.x;
            return new Pose(c - rot * (Part.LocalBox.center * s), rot);
        }

        Pose CurrentFlyPose(out float scale)
        {
            var root = Kind == EditKind.Placed && m_Proxy != null ? proxyRoot : Part.transform;
            scale = root.lossyScale.x;
            return new Pose(root.position, root.rotation);
        }

        void StartFly(Pose from, float scaleFrom, Pose to, float scaleTo)
        {
            m_FlyFrom = from; m_FlyTo = to;
            m_ScaleFrom = scaleFrom; m_ScaleTo = scaleTo;
            m_FlyStart = Now;
            m_FlySeconds = EditViewMath.FlySeconds(UiSettings.ReducedMotion || !Application.isPlaying, flySeconds);
        }

        /// The copy's root at a world pose and world scale (its holder may be scaled).
        void SetCopy(Pose pose, float worldScale)
        {
            if (proxyRoot == null) return;
            var parent = proxyRoot.parent;
            float ps = parent != null ? parent.lossyScale.x : 1f;
            proxyRoot.SetPositionAndRotation(pose.position, pose.rotation);
            proxyRoot.localScale = Vector3.one * (worldScale / Mathf.Max(ps, 1e-6f));
        }

        void ApplyFly(float t)
        {
            var pose = EditViewMath.Blend(m_FlyFrom, m_FlyTo, t);
            float s = Mathf.LerpUnclamped(m_ScaleFrom, m_ScaleTo, t);
            if (Kind == EditKind.Placed && m_Proxy != null && proxyRoot != null) SetCopy(pose, s);
            else if (Part != null)
            {
                var parent = Part.transform.parent;
                float ps = parent != null ? parent.lossyScale.x : 1f;
                Part.transform.localScale = Vector3.one * (s / Mathf.Max(ps, 1e-6f));
                Part.transform.SetPositionAndRotation(pose.position, pose.rotation);
            }
        }

        // ---------------- per frame ----------------

        void Update() => Tick(Time.unscaledDeltaTime);

        /// The per-frame step (tests and the harness call it; EditMode has no player loop).
        public void Tick(float dt)
        {
            if (m_Input == null || m_HookedTool == null || m_Tools == null) Hook();
            if (!Flow.Active) { UpdateHold(); return; }
            if (Part == null) { Flow.Fire(EditStep.Abort); Teardown(); return; }
            if (Kind == EditKind.Placed && !Watch()) return;
            switch (Phase)
            {
                case EditPhase.Opening:
                case EditPhase.Closing:
                case EditPhase.ToRoom:
                    float p = EditViewMath.Progress(Now, m_FlyStart, m_FlySeconds);
                    if (Phase == EditPhase.Opening && Kind == EditKind.Placed) m_FlyTo = DisplayPose(out m_ScaleTo);
                    ApplyFly(EditViewMath.Ease(p));
                    if (p >= 1f) Arrive();
                    break;
                case EditPhase.Orient:
                    UpdateOrient(dt);
                    break;
                case EditPhase.Moving:
                    UpdateMove();
                    break;
                case EditPhase.Placing:
                    var t = Tool;
                    if (t == null || (t.Held != Part && !Part.Placed)) { Finish(keep: false); }   // put away by a tool switch
                    break;
            }
        }

        /// A placed part's session is still the editor's: follow a model swap; close if the editor ended it.
        bool Watch()
        {
            var e = Editor;
            if (Phase == EditPhase.Closing && e != null && e.Adjusting == Part) return true;
            if (e == null || !e.EditViewActive || e.Adjusting == null)
            {
                LastAction = "closed: the edit session ended (a tool, a switch, a reset)";
                Log.Info($"Edit view: {LastAction}");
                Flow.Fire(EditStep.Abort);
                Teardown();
                return false;
            }
            if (e.Adjusting != Part)
            {
                // A model swap (cycle_model, Compare): the new model is the one edited.
                Part.DrawOverDim(false);
                if (Part.Model != null) Part.Model.gameObject.SetActive(true);
                Part = e.Adjusting;
                Part.DrawOverDim(true, itemQueue);
                if (m_Proxy != null) BuildProxy();
                else if (Phase == EditPhase.Moving) Part.Model.gameObject.SetActive(true);
            }
            return true;
        }

        void UpdateOrient(float dt)
        {
            if (Kind == EditKind.Placed)
            {
                CheckProxy();
                var d = DisplayPose(out float s);
                SetCopy(d, s);
            }
            else
            {
                var pose = NewPose();
                Part.transform.SetPositionAndRotation(pose.position, pose.rotation);
            }
            arrows?.Tick();
            if (m_RepeatArrow >= 0) UpdateRepeat();
            if (m_Body) UpdateBody(dt);
            Values(out var off, out var ttr);
            panel?.Refresh(off, ttr, Kind == EditKind.Placed, Editor != null && Editor.Fine, FullShade, EditShades.IndexOf(Part.Shade));
        }

        /// The offset and turn now (a new part: its turn at the centre, no offset).
        void Values(out Vector3 offset, out Vector3 turnTiltRoll)
        {
            offset = Vector3.zero;
            turnTiltRoll = m_Ttr;
            if (Kind == EditKind.Placed && Editor != null) Editor.TryReadout(Part, out offset, out turnTiltRoll);
        }

        // ---------------- input ----------------

        void OnPressStart(ToolHand hand, Pose pointer)
        {
            if (!Flow.Active)
            {
                if (menu != null && menu.Showing) menu.Hide();
                BeginHold(hand, pointer);
                return;
            }
            if (Phase == EditPhase.Orient) BeginBody(hand, pointer);
            else if (Phase == EditPhase.Moving && !m_MoveDrag)
            {
                m_MoveDrag = true;
                m_MoveHand = hand;
                m_MovePointer = pointer;
            }
        }

        void OnPressMove(ToolHand hand, Pose pointer)
        {
            if (m_MoveDrag && hand == m_MoveHand) m_MovePointer = pointer;
        }

        void OnPressEnd(ToolHand hand, Pose pointer)
        {
            if (m_Hold.Holding && hand == m_HoldHand) m_Hold.Cancel();
            if (m_Body && hand == m_BodyHand) { UpdateBody(0f, final: true); m_Body = false; }
            else if (m_MoveDrag && hand == m_MoveHand)
            {
                m_MovePointer = pointer;
                MoveNow();
                m_MoveDrag = false;
                m_NextFit = 0f;
                UpdateMoveFit();
            }
        }

        void OnButton(ToolHand hand, ToolButton button)
        {
            if (button == ToolButton.Context)
            {
                if (Flow.Active) return;
                var hub = Hub;
                if (hub == null || !hub.HasPointer(hand)) return;
                var part = PlacedUnder(hub.GetPointer(hand));
                if (part != null) OpenMenu(part, "grip");
                return;
            }
            // B / Y, the other hand's pinch, thumb + middle: "done" = Save.
            if (button == ToolButton.Finish && Flow.Active && Kind == EditKind.Placed && (Phase == EditPhase.Orient || Phase == EditPhase.Moving)) Save();
        }

        // ---------------- arrows (edit-touch: poke buttons) ----------------

        /// A press (pinch / trigger) on the item itself: it turns with your hand until the release. The arrows aren't
        /// taken by presses any more: they are poke buttons (PokeArrow).
        void BeginBody(ToolHand hand, Pose pointer)
        {
            if (stage == null || !EditViewMath.RaySphere(pointer.position, pointer.forward, stage.position, m_Radius, out _)) return;
            m_Body = true;
            m_BodyHand = hand;
            m_Grip0 = Grip(hand, pointer).rotation;
            m_Rot0 = Kind == EditKind.Placed ? Part.transform.rotation : PlacementMath.Rotation(StageFrame(), StageFrame().Facing, m_Ttr);
            Values(out m_Off0, out m_Ttr0);
            m_BodyFilter.Reset();
        }

        /// edit-touch: arrow i poked (its knob's GlassButton.Clicked through EditArrowButton): one step now, and while the
        /// finger stays in, a step after 0.4 s and every 0.12 s after that (HoldRepeat, in Tick). False: not now (not
        /// orienting, or the arrow isn't shown for this part).
        public bool PokeArrow(int i)
        {
            if (Phase != EditPhase.Orient || i < 0 || i >= EditViewMath.Arrows.Length) return false;
            if (arrows != null && i < arrows.Count && !arrows.Enabled[i]) return false;
            bool ok = StepArrow(i);
            m_RepeatArrow = i;
            m_Repeat.Begin(Now);
            arrows?.SetState(i);
            ShowArrowReadout(i);
            return ok;
        }

        /// edit-touch: a finger came near arrow i (or left it): its axis and value show in the pill.
        public void HoverArrow(int i, bool on)
        {
            if (arrows == null || Phase != EditPhase.Orient || i < 0 || i >= arrows.Count) return;
            arrows.SetHover(i, on);
            if (on && m_RepeatArrow < 0) ShowArrowReadout(i);
        }

        /// edit-touch (harness and tests): keep the poked arrow held in without a finger (`on`), or let it go.
        public void SimulateHold(bool on) => m_HoldOverride = on;

        void UpdateRepeat()
        {
            int i = m_RepeatArrow;
            bool held = m_HoldOverride || (arrows != null && arrows.Held(i));
            if (m_Repeat.Tick(Now, held))
            {
                if (StepArrow(i))
                {
                    Repeats++;
                    if (arrows != null && i < arrows.Count) UiFeedback.Press(arrows.World[i]);   // a tick per step
                }
                ShowArrowReadout(i);
            }
            if (!m_Repeat.Active) StopRepeat();
        }

        void StopRepeat()
        {
            if (m_RepeatArrow < 0) return;
            m_Repeat.End();
            m_RepeatArrow = -1;
            m_HoldOverride = false;
            arrows?.SetState(-1);
        }

        void ShowArrowReadout(int i)
        {
            if (arrows == null || i < 0 || i >= EditViewMath.Arrows.Length) return;
            var a = EditViewMath.Arrows[i].Axis;
            Values(out var off, out var ttr);
            arrows.ShowReadout(a, Edit6DofMath.Component(a, off, ttr), UiSettings.UnitSystem);
        }

        void EndPress()
        {
            StopRepeat();
            m_Body = false;
        }

        static Vector3 Wrap(Vector3 v) => new Vector3(PlacementMath.Wrap180(v.x), PlacementMath.Wrap180(v.y), PlacementMath.Wrap180(v.z));

        /// One step on arrow i (a tap): 5° / ⅜″, fine 1° / ⅛″.
        public bool StepArrow(int i)
        {
            if (i < 0 || i >= EditViewMath.Arrows.Length || Phase != EditPhase.Orient) return false;
            var a = EditViewMath.Arrows[i];
            if (!a.Curved && Kind != EditKind.Placed) return false;
            var e = Editor;
            float step = Edit6DofMath.Step(a.Axis, UiSettings.UnitSystem, e != null && e.Fine) * a.Sign;
            Edit6DofMath.Delta(a.Axis, step, out var rud, out var dttr);
            if (Kind == EditKind.Placed) { if (e == null || !e.Nudge(rud, dttr, Part)) return false; }
            else m_Ttr = Wrap(m_Ttr + dttr);
            Steps++;
            LastAction = $"stepped {a.Name}";
            return true;
        }

        /// The index of an arrow by name ("TiltUp", "Right" …; EditViewMath.Arrows), −1 if none.
        public static int ArrowIndex(string name)
        {
            for (int i = 0; i < EditViewMath.Arrows.Length; i++) if (EditViewMath.Arrows[i].Name == name) return i;
            return -1;
        }

        // ---------------- the item turned by hand ----------------

        Pose Grip(ToolHand hand, Pose pointer) => ToolInputHub.TryGetGrip(m_Input, hand, out var g) ? g : pointer;

        void UpdateBody(float dt, bool final = false)
        {
            var hub = Hub;
            if (hub == null) return;
            var pointer = hub.GetPointer(m_BodyHand);
            var grip = Grip(m_BodyHand, pointer).rotation;
            if (Kind == EditKind.Placed)
            {
                var e = Editor;
                if (e == null || !e.TryFit(Part, out var f, out var fit)) return;
                var world = Edit6DofMath.TurnedBy(m_Grip0, grip, m_Yaw, m_Rot0);
                if (!final && Application.isPlaying) world = m_BodyFilter.Filter(world, dt);
                e.RootToWorld(out _, out var rootRot, out _);
                var root = PlacementMath.Normalize(PlacementMath.Conj(rootRot) * world);
                e.EditPose(Part, m_Off0, PlacementMath.TurnTiltRoll(f, fit, root), final);
            }
            else
            {
                var f = StageFrame();
                var rot = Edit6DofMath.TurnedBy(m_Grip0, grip, Quaternion.identity, m_Rot0);
                if (!final && Application.isPlaying) rot = m_BodyFilter.Filter(rot, dt);
                m_Ttr = PlacementMath.TurnTiltRoll(f, f.Facing, rot);
            }
            if (final) { Drags++; LastAction = "turned it by hand"; }
        }

        // ---------------- colour, step, reset ----------------

        /// Swatch i (0 = Original) at the current strength.
        public bool SetSwatch(int i)
        {
            if (!Flow.Active || Part == null || Phase != EditPhase.Orient) return false;
            var shade = EditShades.Make(i, m_Strength);
            if (Kind == EditKind.Placed) { if (Editor == null || !Editor.SetShade(Part, shade)) return false; }
            else Part.SetShade(shade);
            LastAction = $"shade {shade}";
            return true;
        }

        /// Light ⇄ full shade (the swatch on it takes the new strength).
        public void ToggleStrength()
        {
            m_Strength = FullShade ? EditShades.Light : EditShades.Full;
            int i = Part != null ? EditShades.IndexOf(Part.Shade) : 0;
            if (i > 0) SetSwatch(i);
        }

        public bool ResetTurn()
        {
            if (!Flow.Active || Phase != EditPhase.Orient) return false;
            if (Kind == EditKind.Placed) return Editor != null && Editor.ResetTurn(Part);
            m_Ttr = Vector3.zero;
            return true;
        }

        /// A button of the panel, the move bar or the context menu (EditViewButton).
        public void Do(EditViewAction action, int index = 0)
        {
            // edit-touch: the menu may open where the pinching hand is — a poke in its first 0.35 s doesn't count.
            bool menuAction = action == EditViewAction.MenuEdit || action == EditViewAction.MenuSimilar || action == EditViewAction.MenuDelete || action == EditViewAction.MenuUndo;
            if (menuAction && menu != null && !menu.AcceptingPresses) { LastAction = "menu: not armed yet"; return; }
            switch (action)
            {
                case EditViewAction.Move: StartMove(); break;
                case EditViewAction.Save: Save(); break;
                case EditViewAction.Place: Place(); break;
                case EditViewAction.Cancel: Cancel(); break;
                case EditViewAction.ResetTurn: ResetTurn(); break;
                case EditViewAction.Step: if (Editor != null) Editor.SetFine(!Editor.Fine); break;
                case EditViewAction.Shade: ToggleStrength(); break;
                case EditViewAction.Swatch: SetSwatch(index); break;
                case EditViewAction.FitToOpening: if (Phase == EditPhase.Orient && Kind == EditKind.Placed) Editor?.FitToOpening(); break;
                case EditViewAction.MenuEdit: { var p = menu != null ? menu.Part : null; menu?.Hide(); OpenPlaced(p); break; }
                case EditViewAction.MenuSimilar: { var p = menu != null ? menu.Part : null; menu?.Hide(); ViewSimilar(p); break; }
                case EditViewAction.MenuDelete: Delete(menu != null ? menu.Part : null); break;
                case EditViewAction.MenuUndo: UndoDelete(); break;
            }
        }

        // ---------------- Move ----------------

        void UpdateMove()
        {
            if (m_MoveDrag) MoveNow();
            if (Now >= m_NextFit) UpdateMoveFit();
        }

        void MoveNow()
        {
            var e = Editor;
            if (e != null && e.MoveAlong(Part, m_MovePointer)) m_Moved = true;   // edit-touch: a translation only
        }

        void UpdateMoveFit()
        {
            m_NextFit = Now + 0.1f;
            var e = Editor;
            if (panel == null || e == null || !m_Moved) return;
            var fit = e.MoveFit(Part);
            panel.SetMoveText(fit != null ? Copy.FitLine(fit) + " · Save to keep it here" : "Save to keep it here");
        }

        // ---------------- the context menu ----------------

        void BeginHold(ToolHand hand, Pose pointer)
        {
            var t = Tool;
            if (AppState.Mode != AppMode.World || (t != null && t.Held != null)) return;
            if (!EditViewMath.HoldOpensMenu(m_Tools != null ? m_Tools.Active : ToolKind.None)) return;
            var part = PlacedUnder(pointer);
            if (part == null) return;
            m_Hold.Seconds = holdSeconds;
            m_Hold.Begin(Now, pointer.position - EyePos, pointer.forward);
            m_HoldPart = part;
            m_HoldHand = hand;
        }

        void UpdateHold()
        {
            if (!m_Hold.Holding) return;
            var hub = Hub;
            if (hub == null || !hub.IsPressed(m_HoldHand)) { m_Hold.Cancel(); return; }
            var p = hub.GetPointer(m_HoldHand);
            if (m_Hold.Update(Now, p.position - EyePos, p.forward) && m_HoldPart != null && m_HoldPart.Placed) OpenMenu(m_HoldPart, "long pinch");
        }

        /// The placed part under a pointer ray, or null.
        public PartInstance PlacedUnder(Pose pointer)
        {
            var t = Tool;
            if (t == null) return null;
            if (!Physics.Raycast(new Ray(pointer.position, pointer.forward), out var hit, t.maxRayDistance, PartLayers.PartsMask, QueryTriggerInteraction.Ignore)) return null;
            var part = hit.collider.GetComponentInParent<PartInstance>();
            if (part == null || !part.Placed) return null;
            var list = t.PlacedParts;
            for (int i = 0; i < list.Count; i++) if (list[i] == part) return part;
            return null;
        }

        /// The context menu beside a placed part (Edit / View similar / Delete).
        public bool OpenMenu(PartInstance part, string how = "harness")
        {
            if (part == null || menu == null || Flow.Active) return false;
            Tool?.Select(part);
            menu.Show(part);
            UiFeedback.Press(part.WorldBoxCentre);
            LastAction = $"menu on {part.Spec.id} ({how})";
            Log.Info($"Edit view: {LastAction}");
            return true;
        }

        /// View similar: the Catalog on this part's kind (its category, else a search for it), with Fits on when it's in a
        /// gap.
        public bool ViewSimilar(PartInstance part)
        {
            if (part == null) return false;
            string noun = Copy.Noun(part.Spec, part.SearchQuery);
            var spot = Editor != null ? Editor.SpotOf(part) : null;
            bool gap = spot != null && spot.CavityId != null;
            bool ok = AppCommands.ShowCatalog(category: noun, query: null, fromTape: gap);
            LastAction = $"similar to {part.Spec.id}: the Catalog on \"{noun}\"{(gap ? " (fits the gap)" : "")}";
            Log.Info($"Edit view: {LastAction}");
            return ok;
        }

        /// Delete (undoable): the part goes; the menu turns into "Deleted · Undo" where it was.
        public bool Delete(PartInstance part)
        {
            var t = Tool;
            if (part == null || t == null) return false;
            var at = menu != null && menu.Showing ? menu.transform.position : part.WorldBoxCentre;
            string name = Copy.Noun(part.Spec, part.SearchQuery);   // delete-undo: the noun, as the other part toasts
            if (!t.Delete(part)) return false;
            menu?.ShowUndo(part, at);
            UiToast.Show(Copy.PartRemoved(name), ColorRole.Info);   // delete-undo: "Hanger removed · Undo on the ring"
            LastAction = $"deleted {part.Spec.id}";
            return true;
        }

        public bool UndoDelete()
        {
            var t = Tool;
            var part = menu != null ? menu.Part : null;
            bool ok = t != null && t.UndoDelete(part);
            menu?.Hide();
            LastAction = ok ? $"brought {part.Spec.id} back" : "nothing to bring back";
            return ok;
        }

        // ---------------- harness ----------------

        /// One line: the phase, the part, its shown scale, readout, arrows, the dim and the menu.
        public string Report()
        {
            Values(out var off, out var ttr);
            string readout = Part != null ? $"offset_mm=({off.x * 1000f:0.#}, {off.y * 1000f:0.#}, {off.z * 1000f:0.#}) turn/tilt/roll=({ttr.x:0.#}, {ttr.y:0.#}, {ttr.z:0.#})" : "-";
            return $"edit phase={Phase} kind={Kind} part={(Part != null ? Part.Spec.id : "-")} shown={ShownScale * 100f:0}% {readout} " +
                   $"shade={(Part != null ? Part.Shade.ToString() : "-")} hover={Hovered} press={m_RepeatArrow} repeats={Repeats} dim={(dim != null ? dim.Alpha : 0f):0.00}{(dim != null && dim.WritesDepth ? " depth" : "")} " +
                   $"copy={(m_Proxy != null)} stage={(stage != null && stage.gameObject.activeSelf)} bar={(panel != null && panel.MoveBarOpen)} " +
                   $"menu={(menu != null && menu.Showing ? (menu.UndoShowing ? "undo" : menu.Part?.Spec.id) : "-")} opens={Opens} saves={Saves} cancels={Cancels} " +
                   $"places={Places} steps={Steps} drags={Drags} last=\"{LastAction}\"";
        }
    }
}
