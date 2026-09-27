using System.Collections.Generic;
using AirTools.Core;
using AirTools.Scene;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// assetgen: the placement editor's openings and its Size & finish section.
    ///
    /// **Openings.** A taped opening (Openings: a window's width and height tapes) is an insert target like a removed
    /// part's cavity. A part made for it (OpeningMath.ForOpening) placed at it gets an opening spot: its anchor is its
    /// front-face centre, the fit pose puts that on the opening's centre in the wall's plane, square to the opening. When
    /// the part fits properly (OpeningMath.FitsProperly: ≤ 3 mm over, ≤ 2″ under) it snaps there — while held
    /// (PartTool.HeldSnap), when placed, and when an adjust drag lets go near it (8 cm); one that doesn't stays where it
    /// was put and shows why ("Too wide by ⅜″"). Anywhere else a part is a free placement, as before.
    ///
    /// **Size & finish.** Width / height / depth steps (the editor's step: ⅜″ / ⅛″ fine, 1 cm / 2 mm), "Fit to opening"
    /// (the opening less ¼″ each side, and into it) and finish chips. The part's true size changes at once — its model
    /// stretched, marked "resized" — and, for a parametric part from the server, the server rebuilds the model at that
    /// size (POST /parts/{id}/resize: "made to size") and it's swapped in when it arrives. A finish the server renders
    /// (F11, Grok Imagine) comes with that model; otherwise the frame is tinted (never the glass). Each change is one
    /// undo step (EditHistory.Stamp), is saved with a placement, and goes in its notebook row. Sizes stay within
    /// 0.5–2× the listing (PartLookMath.Clamp).
    public partial class PlacementEditor
    {
        /// An opening pulls a part that fits it into place from this close (scene metres) when a drag lets go.
        public const float OpeningSnapRadius = 0.08f;

        /// Bumped when a size, a finish or its model changes (the panel redraws its Size & finish section).
        public int LookVersion { get; private set; }
        /// The last size / finish action and the last server answer (the harness).
        public string LastLook { get; private set; } = "";
        /// A made-to-size model is on its way.
        public bool LookBusy => m_LookPending > 0;
        int m_LookPending;
        /// Server rebuilds asked for (tests / the harness).
        public int LookRequests { get; private set; }
        /// The server answered /resize with 404 / 405: it predates the p4-asset patch (parts stay "resized").
        public bool ServerCantResize { get; private set; }

        static bool IsOpening(Spot s) => s != null && s.CavityId != null && s.CavityId.StartsWith("opening:");

        // ---------------- openings ----------------

        static CavityVolume VolumeToPackage(CavityVolume v, PackageSpace sp) => new CavityVolume
        {
            Centre = sp.ToPackage(v.Centre), Right = sp.DirToPackage(v.Right), Up = sp.DirToPackage(v.Up), Out = sp.DirToPackage(v.Out),
            Half = v.Half / Mathf.Max(Mathf.Abs(sp.Scale), 1e-6f), OpenTop = v.OpenTop,
        };

        /// The opening spot for a part whose box centre is at a taped opening it's made for. False: none.
        bool BuildOpeningSpot(PartInstance part, Vector3 origin, Quaternion rot, PackageSpace sp, Spot s)
        {
            var d = part.Spec.dims_mm;
            if (!Openings.TryAt(origin + rot * part.LocalBox.center, d.w * 0.001f, d.h * 0.001f, out var o)) return false;
            FillOpeningSpot(s, o, sp);
            return true;
        }

        void FillOpeningSpot(Spot s, TapedOpening o, PackageSpace sp)
        {
            var f = OpeningMath.Frame(o);
            s.CavityId = "opening:" + o.Id;
            s.Anchor = PlacementAnchor.FrontCentre;
            s.FramePkg = PlacementMath.ToPackage(f, sp);
            s.CavityPkg = VolumeToPackage(OpeningMath.Volume(o), sp);
            s.FitAnchorPkg = s.FramePkg.Origin;
            s.FitRotationPkg = sp.RotToPackage(f.Facing);
            s.Key = PlacementMath.Key(s.Site, s.Revision, "opening:" + o.Id);
            s.NormalAxis = -1;
        }

        /// The opening's width and height (scene metres) from a spot.
        Vector2 OpeningSize(Spot s)
        {
            var v = VolumeToRoot(s.CavityPkg, Space());
            return new Vector2(v.Half.x * 2f, v.Half.y * 2f);
        }

        bool FitsItsOpening(PartInstance part, Spot s)
        {
            if (part == null || !IsOpening(s)) return false;
            var size = OpeningSize(s);
            return OpeningMath.FitsProperly(part.Spec.dims_mm.w, part.Spec.dims_mm.h, size.x * 1000f, size.y * 1000f);
        }

        /// Is the part at its opening's fit pose (within a millimetre and a tenth of a degree)?
        bool AtFit(PartInstance part, Spot s)
        {
            var sp = Space();
            var f = PlacementMath.ToRoot(s.FramePkg, sp);
            var off = PlacementMath.Offset(f, AnchorRoot(part, s), sp.ToRoot(s.FitAnchorPkg));
            return off.magnitude < 0.001f && Quaternion.Angle(RootRot(part), sp.RotToRoot(s.FitRotationPkg)) < 0.1f;
        }

        /// Just placed: into its opening, square and centred, when it fits properly.
        void SnapIntoOpening(PartInstance part, Spot s)
        {
            if (!IsOpening(s)) return;
            if (!FitsItsOpening(part, s))
            {
                FitNow(part, record: true);
                LastLook = $"at {s.CavityId}, not snapped: {Copy.FitLine(part.Fit)}";
                Log.Info($"Placement: {part.Spec.id} {LastLook}");
                return;
            }
            if (!AtFit(part, s)) Apply(part, s, Space().ToRoot(s.FitAnchorPkg), Vector3.zero);
            Physics.SyncTransforms();
            FitNow(part, record: true);
            LastLook = $"snapped {part.Spec.id} into {s.CavityId}: {Copy.FitLine(part.Fit)}";
            Log.Info($"Placement: {LastLook}");
        }

        /// PartTool.HeldSnap: the held part, seated on a surface, goes into a taped opening it's made for and fits
        /// properly — its front-face centre on the opening's centre, square to it. True when it did. Runs every frame
        /// while held: no allocation.
        bool SnapHeld(PartInstance part)
        {
            if (!HeldOpening(part, out var o)) return false;
            var rot = OpeningMath.Frame(o).Facing;
            SetRootPose(part, PlacementMath.OriginFor(o.Centre, rot, part.LocalBox, PlacementAnchor.FrontCentre), rot);
            part.SurfaceNormal = DirToWorld(o.Out);
            return true;
        }

        /// PartTool.HeldSnapFit: the opening's fit for a held part snapped into it (null: not in one).
        FitReport SnapFit(PartInstance part)
        {
            if (!HeldOpening(part, out var o)) return null;
            return OpeningMath.Fit(PlacementMath.Clearance(OpeningMath.Volume(o), part.LocalBox, RootPos(part), RootRot(part)));
        }

        bool HeldOpening(PartInstance part, out TapedOpening o)
        {
            o = default;
            if (part == null || part.Spec == null || !snapIntoOpenings) return false;
            var d = part.Spec.dims_mm;
            var pos = RootPos(part);
            var rot = RootRot(part);
            if (!Openings.TryAt(pos + rot * part.LocalBox.center, d.w * 0.001f, d.h * 0.001f, out o)) return false;
            if (!OpeningMath.FitsProperly(d, o)) return false;
            return !TryCavity(part, pos, rot, Space(), out _, out _, out _);   // a removed part's gap wins (BuildSpot's order)
        }

        [Tooltip("assetgen: a part that fits a taped opening snaps into it while held (the preview) and when placed.")]
        public bool snapIntoOpenings = true;

        /// After an adjust drag: a part let go at a taped opening it's made for takes that opening's spot (and snaps in
        /// when it fits); one dragged out of its opening becomes a free placement. The moves so far are one undo step.
        void MaybeRespot(PartInstance part)
        {
            if (part == null || part != Adjusting) return;
            var s = EnsureSpot(part);
            bool opening = IsOpening(s);
            if (s.CavityId != null && !opening) return;   // a removed part's cavity keeps its spot
            var sp = Space();
            var origin = RootPos(part);
            var rot = RootRot(part);
            var d = part.Spec.dims_mm;
            bool at = Openings.TryAt(origin + rot * part.LocalBox.center, d.w * 0.001f, d.h * 0.001f, out var o);
            if (at && opening && s.CavityId == "opening:" + o.Id) return;
            if (!at && !opening) return;
            CommitSession();
            var fresh = new Spot { Site = s.Site, Revision = s.Revision };
            if (at) FillOpeningSpot(fresh, o, sp);
            else
            {
                fresh = BuildSpot(part);
                m_Spots[part] = fresh;
                Rebaseline();
                LastLook = $"{part.Spec.id} out of the opening: a free placement";
                FitNow(part, record: false);
                return;
            }
            fresh.AnchorPkg = sp.ToPackage(PlacementMath.AnchorOf(origin, rot, part.LocalBox, fresh.Anchor));
            fresh.RotationPkg = sp.RotToPackage(rot);
            m_Spots[part] = fresh;
            Rebaseline();
            if (Snap && FitsItsOpening(part, fresh))
            {
                Apply(part, fresh, sp.ToRoot(fresh.FitAnchorPkg), Vector3.zero);
                m_Ttr = Vector3.zero;
                MarkDirty();
                LastLook = $"dragged {part.Spec.id} into {fresh.CavityId}: snapped";
            }
            else LastLook = $"dragged {part.Spec.id} to {fresh.CavityId}: {(Snap ? "doesn't fit, not snapped" : "snap off")}";
            FitNow(part, record: false);
            SlotsVersion++;
        }

        // ---------------- size & finish ----------------

        /// The part's size and finish now.
        public static PartLookState CurrentLook(PartInstance p) =>
            p == null ? default : new PartLookState(new Vector3(p.Spec.dims_mm.w, p.Spec.dims_mm.h, p.Spec.dims_mm.d), p.LookFinish);

        /// Width (0), height (1) or depth (2) one step bigger (dir +1) or smaller (−1): the editor's step in the user's
        /// unit (⅜″ / 1 cm; fine ⅛″ / 2 mm). One undo step.
        public bool StepSize(int axis, int dir)
        {
            var p = Target();
            if (p == null) { LastLook = "size: no placed part"; return false; }
            float mm = PlacementMath.StepMetres(UiSettings.UnitSystem, Fine) * 1000f * (dir >= 0 ? 1f : -1f);
            var dims = CurrentLook(p).DimsMm;
            dims[Mathf.Clamp(axis, 0, 2)] += mm;
            return SetSize(dims, $"{"WHD"[Mathf.Clamp(axis, 0, 2)]} {(dir >= 0 ? "+" : "−")}{Mathf.Abs(mm):0.#} mm");
        }

        /// The part at `dimsMm` (width, height, depth; clamped to 0.5–2× the listing). One undo step. False: no placed
        /// part, an array member, or no change.
        public bool SetSize(Vector3 dimsMm, string why = "size") => ChangeLook(p => new PartLookState(PartLookMath.Clamp(dimsMm, p.ListedDims), p.LookFinish), why, toFit: false);

        /// "Fit to opening": the opening less ¼″ each side (its own depth), and into it — the part's opening, else the
        /// opening taped last. One undo step. False when there's no opening.
        public bool FitToOpening()
        {
            var p = Target();
            if (p == null) { LastLook = "fit to opening: no placed part"; return false; }
            if (!TryOpeningFor(p, out var size, out var o, out bool own))
            {
                LastLook = "fit to opening: tape the opening's width and height first";
                UiToast.Show("Tape the opening's width and height first", ColorRole.Info);
                return false;
            }
            var target = OpeningMath.FitToOpeningMm(new Vector3(size.x * 1000f, size.y * 1000f, 0f), p.Spec.dims_mm.d);
            if (!own)
            {
                // Into the opening first (its spot), then sized there.
                var s = EnsureSpot(p);
                if (Adjusting == p && m_SessionDirty) CommitSession();
                var sp = Space();
                var fresh = new Spot { Site = s.Site, Revision = s.Revision };
                FillOpeningSpot(fresh, o, sp);
                fresh.AnchorPkg = sp.ToPackage(AnchorRoot(p, fresh));
                fresh.RotationPkg = sp.RotToPackage(RootRot(p));
                m_Spots[p] = fresh;
            }
            return ChangeLook(x => new PartLookState(PartLookMath.Clamp(target, x.ListedDims), x.LookFinish), "fit to opening", toFit: true);
        }

        /// The opening "Fit to opening" means for `p` (scene metres): its own spot's, else the current / newest taped one.
        bool TryOpeningFor(PartInstance p, out Vector2 size, out TapedOpening o, out bool own)
        {
            var s = SpotOf(p);
            o = default;
            own = IsOpening(s);
            if (own) { size = OpeningSize(s); return true; }
            size = default;
            if (!Openings.TryCurrent(out o))
            {
                var all = Openings.All;
                if (all.Count == 0) return false;
                o = all[0];
            }
            size = new Vector2(o.W, o.H);
            return true;
        }

        /// Whether "Fit to opening" has an opening to fit.
        public bool CanFitToOpening
        {
            get
            {
                var p = Target();
                return p != null && TryOpeningFor(p, out _, out _, out _);
            }
        }

        /// A finish chip: `name` (null / the listing's own: as listed). One undo step.
        public bool SetLookFinish(string name)
        {
            var p = Target();
            if (p == null) { LastLook = "finish: no placed part"; return false; }
            string finish = PartLookMath.IsListedFinish(p.Spec, name) ? null : name;
            return ChangeLook(x => new PartLookState(CurrentLook(x).DimsMm, finish), $"finish {finish ?? "as listed"}", toFit: false);
        }

        /// A look change as one undo step: the adjust session so far is committed first; the anchor stays put (toFit: the
        /// part goes to the fit pose too).
        bool ChangeLook(System.Func<PartInstance, PartLookState> next, string why, bool toFit)
        {
            var part = Target();
            if (part == null || !IsPlaced(part)) { LastLook = $"{why}: no placed part"; return false; }
            if (Tool != null && Tool.ArrayOf(part) != null)
            {
                LastLook = "a run of parts keeps its size";
                UiToast.Show("A run of parts keeps its size", ColorRole.Info);
                return false;
            }
            var s = EnsureSpot(part);
            var before = CurrentLook(part);
            var after = next(part);
            if (Adjusting == part && m_SessionDirty) CommitSession();
            var poseBefore = SnapOf(part, s);
            bool moves = toFit && !AtFit(part, s);
            if (after.Same(before) && !moves) { LastLook = $"{why}: no change"; return false; }
            ApplyLook(part, s, after);
            if (toFit) { Apply(part, s, Space().ToRoot(s.FitAnchorPkg), Vector3.zero); if (part == Adjusting) m_Ttr = Vector3.zero; }
            Physics.SyncTransforms();
            FitNow(part, record: true);
            Push(new Op { Spot = s, PartBefore = part, PartAfter = part, Before = poseBefore, After = SnapOf(part, s), HasLook = true, LookBefore = before, LookAfter = after });
            if (part == Adjusting) Rebaseline();
            var st = Store.Spot(s.Key);
            if (st != null && st.Active != null) { st.Active = null; SlotsVersion++; }
            LastLook = $"{why}: {after} ({part.SizeState}) · {Copy.FitLine(part.Fit)}";
            LastAction = LastLook;
            Log.Info($"Placement: {part.Spec.id} {LastLook}");
            UiToast.Show($"{PartLookMath.SizeWords(part.Spec.dims_mm)} · {Copy.FitLine(part.Fit)}".Trim(' ', '·'),
                part.Fit == null ? ColorRole.Info : part.Fit.Status == FitStatus.Red ? ColorRole.Danger : part.Fit.Status == FitStatus.Amber ? ColorRole.Warning : ColorRole.Success);
            return true;
        }

        /// Put `look` on the part: the size (anchor kept), a parked model made for it if there is one, the finish (the
        /// model's own, else a tint), and — for a server part the server can rebuild — the made-to-size model asked for.
        void ApplyLook(PartInstance part, Spot s, PartLookState look)
        {
            var anchor = AnchorRoot(part, s);
            var rot = RootRot(part);
            var dims = look.Dims;
            if (!PartInstance.SameDims(part.Spec.dims_mm, dims)) part.SetDims(dims);
            string baked = PartInstance.LookKey(dims, look.Finish), plain = PartInstance.LookKey(dims, null);
            bool exact = part.TryShowVariant(baked) || (look.Finish != null && part.TryShowVariant(plain));
            // A model with another finish baked in (the server's re-textured one) goes; the listing's own colours, stretched,
            // until the right one comes. A plain model of another size just stretches.
            string modelFinish = part.ModelKey != null && part.ModelKey.IndexOf('|') >= 0 ? part.ModelKey.Substring(part.ModelKey.IndexOf('|') + 1) : "";
            if (!exact && modelFinish.Length > 0 && modelFinish != (look.Finish ?? "").Trim().ToLowerInvariant())
                part.TryShowVariant(PartInstance.LookKey(part.ListedDims, null));
            part.ClearTint();
            bool finishBaked = look.Finish != null && part.ModelKey == baked;
            if (look.Finish != null && !finishBaked && PartLookMath.TintFor(part.Spec, look.Finish) is Color c) part.TintExceptGlass(c, look.Finish);
            part.LookFinish = look.Finish;
            SetRootPose(part, PlacementMath.OriginFor(anchor, rot, part.LocalBox, s.Anchor), rot);
            var sp = Space();
            s.AnchorPkg = sp.ToPackage(anchor);
            s.RotationPkg = sp.RotToPackage(rot);
            LookVersion++;
            bool haveModel = part.ModelKey == baked || (part.ModelKey == plain && !PartLookMath.ServerRenders(part, look.Finish));
            if (!haveModel && !ServerCantResize && PartLookMath.CanRebuild(part, look.Finish)) RequestModel(part, look);
        }

        /// Ask the server for the part at `look` and swap it in when it arrives (if the part still wants that look).
        void RequestModel(PartInstance part, PartLookState look)
        {
            var client = Services.Get<PartsClient>();
            var l = loader != null ? loader : Services.Get<PartLoader>();
            if (client == null || l == null || !Application.isPlaying) return;
            LookRequests++;
            m_LookPending++;
            int session = m_Session;
            float t0 = Time.realtimeSinceStartup;
            LastLook = $"asking the server for {part.Spec.id} at {look}";
            client.Resize(part.Spec.id, look.DimsMm, look.Finish, reply =>
            {
                if (reply == null)
                {
                    m_LookPending--;
                    ServerCantResize = client.LastHttpCode == 404 || client.LastHttpCode == 405;
                    LastLook = $"server rebuild of {part?.Spec?.id} failed ({client.LastError}){(ServerCantResize ? " — the server has no /resize (docs/handoff/p4-asset)" : "")}: stays resized";
                    Log.Warn($"Placement: {LastLook}");
                    LookVersion++;
                    return;
                }
                l.LoadVariant(part.Spec, reply.model_url, (model, owner) =>
                {
                    m_LookPending--;
                    bool stale = part == null || session != m_Session || !IsPlaced(part) || !CurrentLook(part).Same(look);
                    if (model == null || stale)
                    {
                        if (model != null) { model.SetActive(false); Destroy(model); }
                        owner?.Dispose();
                        LastLook = model == null ? $"made-to-size model {reply.model_url} didn't load: stays resized" : $"made-to-size model {reply.model_url} arrived after the look changed";
                        LookVersion++;
                        return;
                    }
                    var s = EnsureSpot(part);
                    var anchor = AnchorRoot(part, s);
                    var rot = RootRot(part);
                    bool baked = look.Finish != null && reply.finish_source == "imagine";
                    part.AdoptModel(model, owner, reply.dims_mm ?? look.Dims, PartInstance.LookKey(look.Dims, baked ? look.Finish : null), reply.model_url);
                    if (look.Finish != null && !baked && PartLookMath.TintFor(part.Spec, look.Finish) is Color c) part.TintExceptGlass(c, look.Finish);
                    part.LookFinishSource = reply.finish_source;
                    SetRootPose(part, PlacementMath.OriginFor(anchor, rot, part.LocalBox, s.Anchor), rot);
                    Physics.SyncTransforms();
                    FitNow(part, record: true);
                    LookVersion++;
                    LastLook = $"made to size: {part.Spec.id} {look} — {reply.source} ({reply.tier}{(reply.template != null ? " " + reply.template : "")}), " +
                               $"finish {reply.finish_source ?? "-"}, server {reply.seconds ?? 0f:0.00} s{(reply.cached ? " cached" : "")}, here {Time.realtimeSinceStartup - t0:0.0} s";
                    Log.Info($"Placement: {LastLook}");
                });
            });
        }

        // ---------------- words (the panel, the notebook, the harness) ----------------

        /// "58½ × 46¾ × 3¼″ · made to size" (W × H × D in the user's unit), or "" with no part.
        public string SizeLine(PartInstance p) =>
            p == null ? "" : $"{PartLookMath.SizeWords(p.Spec.dims_mm)} · {(LookBusy ? "making it to size…" : p.SizeState)}";

        readonly List<string> m_Choices = new List<string>(3);
        PartInstance m_ChoicesFor;

        /// The finish chips for `p` (up to 3; kept per part: no allocation per frame).
        public IReadOnlyList<string> FinishChoices(PartInstance p)
        {
            if (p != m_ChoicesFor)
            {
                m_ChoicesFor = p;
                m_Choices.Clear();
                if (p != null) PartLookMath.FinishChoices(p.Spec, m_Choices);
            }
            return m_Choices;
        }

        /// Whether chip `name` is the finish on `p` (the listing's own when none was chosen).
        public static bool FinishSelected(PartInstance p, string name) =>
            p != null && (p.LookFinish != null ? string.Equals(p.LookFinish, name, System.StringComparison.OrdinalIgnoreCase) : PartLookMath.IsListedFinish(p.Spec, name));

        static string LookLabel(PartInstance p) =>
            p == null ? "" : $" · size {p.Spec.dims_mm.w:0} × {p.Spec.dims_mm.h:0} × {p.Spec.dims_mm.d:0} mm ({p.SizeState}){(p.LookFinish != null ? " · finish " + p.LookFinish : "")}";

        static string LookWords(PartInstance p)
        {
            if (p == null) return "";
            bool sized = p.SizeState != "as listed";
            if (!sized && p.LookFinish == null) return "";
            return (sized ? $" · {PartLookMath.SizeWords(p.Spec.dims_mm)} {p.SizeState}" : "") + (p.LookFinish != null ? $" · {p.LookFinish}" : "");
        }

        string LookReport(PartInstance p) =>
            p == null ? "look=-" : $"look=\"{p.Spec.dims_mm.w:0}×{p.Spec.dims_mm.h:0}×{p.Spec.dims_mm.d:0} mm {p.SizeState} finish={p.LookFinish ?? "listed"} model={p.ModelKey}\" lookBusy={LookBusy}";
    }
}
