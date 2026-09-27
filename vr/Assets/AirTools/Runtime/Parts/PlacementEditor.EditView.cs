using AirTools.Core;
using AirTools.Scene;
using AirTools.Tools;
using UnityEngine;

namespace AirTools.Parts
{
    /// edit6dof: the placement editor as the Edit view's model (docs/edit-view.md). The view never moves the part's
    /// transform itself: it shows a copy at the centre and changes the real part — always at its true pose in the room —
    /// through these, so undo, site scoping, swaps, the switch closing it and the fit all work as in Adjust. An Edit view
    /// session is an adjust session without the panel (BeginEdit): the arrows, the colour and a Move are one undo step
    /// (the spot before a Move and the shade before and after ride on the step).
    public partial class PlacementEditor
    {
        /// The Edit view is driving this session: no adjust panel, no gizmo, no body grab (presses belong to the view).
        public bool EditViewActive { get; private set; }

        Spot m_SessionSpotBefore;
        PartShade m_SessionShadeBefore;

        /// The session's step so far (from its start to now), with the spot before a Move and the shade.
        Op SessionOp(Spot s)
        {
            var part = Adjusting;
            var op = new Op { Spot = s, PartBefore = m_SessionPartBefore, PartAfter = part, Before = m_SessionBefore, After = SnapOf(part, s) };
            if (m_SessionSpotBefore != null && m_SessionSpotBefore != s) op.SpotBefore = m_SessionSpotBefore;
            var after = part != null ? part.Shade : default;
            if (!m_SessionShadeBefore.Same(after)) { op.HasShade = true; op.ShadeBefore = m_SessionShadeBefore; op.ShadeAfter = after; }
            return op;
        }

        /// Start an Edit view session on a placed part (Enter without the adjust panel). False: not placed, or not in the
        /// world.
        public bool BeginEdit(PartInstance part)
        {
            if (part == null || !IsPlaced(part)) { LastAction = "edit: place a part first"; return false; }
            if (Adjusting != null && Adjusting != part) Exit();
            if (Adjusting == part) { if (panel != null && panel.IsOpen) { m_Closing = true; panel.Close(); m_Closing = false; } EditViewActive = true; return true; }
            EditViewActive = true;
            if (!Enter(part, openPanel: false)) { EditViewActive = false; return false; }
            EditViewActive = true;
            LastAction = $"editing {part.Spec.id}";
            return true;
        }

        /// End it: `keep` commits the session as one undo step (Exit); otherwise it goes back to where it began first
        /// (pose, spot and shade) and leaves no step.
        public bool EndEdit(bool keep)
        {
            if (Adjusting == null) { EditViewActive = false; return false; }
            if (!keep) RevertSession();
            bool ok = Exit();
            EditViewActive = false;
            return ok;
        }

        /// Back to the session's start (pose, spot, shade, model); adjusting goes on, nothing to undo.
        public void RevertSession()
        {
            var part = Adjusting;
            if (part == null || !m_SessionDirty) return;
            var s = EnsureSpot(part);
            var op = SessionOp(s);
            m_SessionDirty = false;
            ApplyOp(op, undo: true);
            Rebaseline();
            LastAction = $"reverted {part.Spec.id}";
        }

        /// The session has changes.
        public bool SessionDirty => Adjusting != null && m_SessionDirty;

        /// Pose the part at `offset` (right, up, out; scene metres from its fit anchor) and `turnTiltRoll` in its frame —
        /// the arrows' continuous drags. Joins the session (or is its own step outside one). `fit`: re-check its fit now
        /// (a drag in progress passes false every frame and true on release: no fit check, and no allocation, per frame).
        public bool EditPose(PartInstance part, Vector3 offset, Vector3 turnTiltRoll, bool fit = true)
        {
            if (part == null || !IsPlaced(part)) return false;
            var s = EnsureSpot(part);
            var sp = Space();
            var f = PlacementMath.ToRoot(s.FramePkg, sp);
            var ttr = new Vector3(PlacementMath.Wrap180(turnTiltRoll.x), PlacementMath.Wrap180(turnTiltRoll.y), PlacementMath.Wrap180(turnTiltRoll.z));
            if (!fit && part == Adjusting)
            {
                Apply(part, s, sp.ToRoot(s.FitAnchorPkg) + f.Vector(offset), ttr);
                m_Ttr = ttr;
                MarkDirty();
                return true;
            }
            var before = SnapOf(part, s);
            Apply(part, s, sp.ToRoot(s.FitAnchorPkg) + f.Vector(offset), ttr);
            Moved(part, s, before, ttr);
            LastAction = $"posed {part.Spec.id} at offset ({offset.x * 1000f:0}, {offset.y * 1000f:0}, {offset.z * 1000f:0}) mm, ({ttr.x:0.#}, {ttr.y:0.#}, {ttr.z:0.#})°";
            return true;
        }

        /// The part's frame (SceneRoot space) and its fit rotation there: what its turn / tilt / roll are relative to.
        public bool TryFit(PartInstance part, out PlacementFrame frame, out Quaternion fitRotation)
        {
            frame = default;
            fitRotation = Quaternion.identity;
            var s = SpotOf(part);
            if (part == null || s == null) return false;
            var sp = Space();
            frame = PlacementMath.ToRoot(s.FramePkg, sp);
            fitRotation = sp.RotToRoot(s.FitRotationPkg);
            return true;
        }

        /// SceneRoot's world pose and scale (identity without one): the Edit view maps the part's frame through it.
        public void RootToWorld(out Vector3 position, out Quaternion rotation, out float scale)
        {
            var fr = Frame;
            position = fr != null ? fr.position : Vector3.zero;
            rotation = fr != null ? fr.rotation : Quaternion.identity;
            scale = fr != null ? fr.lossyScale.x : 1f;
        }

        /// Reset orientation: turn, tilt and roll back to 0 about the anchor (it stays where it is).
        public bool ResetTurn(PartInstance part = null)
        {
            part = part != null ? part : Target();
            if (part == null || !IsPlaced(part)) return false;
            TryReadout(part, out var off, out _);
            bool ok = EditPose(part, off, Vector3.zero);
            if (ok) LastAction = $"reset the turn of {part.Spec.id}";
            return ok;
        }

        /// A shade on the part (the Edit view's swatches): joins the session, else its own undo step.
        public bool SetShade(PartInstance part, PartShade shade)
        {
            if (part == null || !IsPlaced(part)) return false;
            var before = part.Shade;
            if (before.Same(shade)) return true;
            part.SetShade(shade);
            if (part == Adjusting) MarkDirty();
            else
            {
                var s = EnsureSpot(part);
                var pose = SnapOf(part, s);
                Push(new Op { Spot = s, PartBefore = part, PartAfter = part, Before = pose, After = pose, HasShade = true, ShadeBefore = before, ShadeAfter = shade });
            }
            LastAction = $"shade {shade} on {part.Spec.id}";
            return true;
        }

        // ---------------- Move ----------------

        /// Move: the part (in the Edit view's session) goes where the pointer hits a surface, seated on it — edit-touch: by
        /// translation only. Its rotation stays exactly what the Edit view gave it: no surface-normal alignment, no gap or
        /// opening snap, no facing the wall ("the whole point is to preserve orientation"). False: nothing under the pointer
        /// (it stays).
        public bool MoveAlong(PartInstance part, Pose pointer)
        {
            var t = Tool;
            if (part == null || t == null || part != Adjusting) return false;
            if (!PartPlacer.TryFind(part.Spec, pointer, t.holdDistance, t.snapRadius, t.maxRayDistance, out var hit, out _, m_MoveMemory)) return false;
            PartPlacer.Seat(part, hit, part.transform.rotation);
            Physics.SyncTransforms();
            MarkDirty();
            return true;
        }

        readonly StructureSnapper.Memory m_MoveMemory = new StructureSnapper.Memory();

        /// The fit where it is while moving: against the gap it's in, else the app's own check (FitChecker).
        public FitReport MoveFit(PartInstance part)
        {
            var t = Tool;
            if (part == null || t == null) return null;
            var sp = Space();
            var origin = RootPos(part);
            var rot = RootRot(part);
            if (TryCavity(part, origin, rot, sp, out _, out _, out var volume))
                return PlacementMath.CavityFit(PlacementMath.Clearance(VolumeToRoot(volume, sp), part.LocalBox, origin, rot));
            part.PinnedFit = null;
            return part.Evaluate(FitChecker.FindTape(part, t.frame));
        }

        /// After a Move: the part's spot is where it is now (a gap, an opening, a surface or free); its fit and notebook row
        /// follow. The session keeps the spot before for its undo.
        public void Respot(PartInstance part)
        {
            if (part == null || part != Adjusting) return;
            var fresh = BuildSpot(part);
            m_Spots[part] = fresh;
            m_Ttr = PlacementMath.TurnTiltRoll(FrameOf(fresh), Space().RotToRoot(fresh.FitRotationPkg), RootRot(part));
            MarkDirty();
            FitNow(part, record: false);
            SlotsVersion++;
            LastAction = $"moved {part.Spec.id} to {fresh}";
            Log.Info($"Placement: {LastAction}");
        }

        // ---------------- a new part's rotation (first placement) ----------------

        /// edit-touch: a new part keeps the rotation it was given in the Edit view (SceneRoot space) while it rides the
        /// pointer and when it's placed: PartTool only translates it (KeptRotation → PartTool.KeepRotation), and no saved
        /// placement or opening snap turns it once it's down (OnPartPlaced, which then clears it).
        public PartInstance HeldTurnPart { get; private set; }
        public Quaternion HeldRotation { get; private set; } = Quaternion.identity;

        /// The new part keeps `worldRotation` (as shown at the centre of the Edit view) until it's placed.
        public void SetHeldRotation(PartInstance part, Quaternion worldRotation)
        {
            HeldTurnPart = part;
            var fr = Frame;
            HeldRotation = PlacementMath.Normalize(fr != null ? PlacementMath.Conj(fr.rotation) * worldRotation : worldRotation);
        }

        public void ClearHeldTurn() { HeldTurnPart = null; HeldRotation = Quaternion.identity; }

        /// PartTool.KeepRotation: the world rotation a held part keeps (only the Edit view's new part), else none.
        Quaternion? KeptRotation(PartInstance part)
        {
            if (part == null || part != HeldTurnPart) return null;
            var fr = Frame;
            return PlacementMath.Normalize(fr != null ? fr.rotation * HeldRotation : HeldRotation);
        }
    }
}
