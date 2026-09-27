using AirTools.Core;
using UnityEngine;

namespace AirTools.Scene
{
    /// modelwheel: where Model view puts the model — floating centred in front of you (the default: the user's call,
    /// Sat 09-26), or on the real table (the M7 placement, kept as an option).
    public enum ModelPlacement { FrontOfMe, OnTable }

    /// Tabletop (SPEC M7), "Model view" on the ring: the scene as a model on a table in front of you, in your room
    /// (passthrough on). Tools keep measuring in scene metres because they work in SceneRoot space; snapping and click
    /// radii scale with the model. Deviation from the plan: a toggle (palm menu / voice) instead of a two-hand scale
    /// gesture — the left hand is the menu hand.
    /// presence.md S1: the poses are pure (<see cref="TablePose"/>, <see cref="WorldPose"/>) so the TransitionDirector
    /// can grow the model between them continuously and then <see cref="Commit"/>; with Reduce motion (or no director)
    /// <see cref="Apply"/> swaps instantly behind the mode fade, as before.
    /// modelview:
    /// - The scale is fitted (<see cref="fitToTable"/>): a round 1:N with the model's longest side ≤ 0.8 m
    ///   (TabletopFit), so a gym and a kitchen both fill the table; <see cref="ScaleLabel"/> is what the wrist strip
    ///   shows. On an assumed or ray-cast table the model stands back far enough for the switcher row in front of it.
    /// - Switching models on the table (ModelSwitcher, show_model): the new scan is hidden until it's in, then re-fitted
    ///   (<see cref="Refit"/>). The rig never moves in passthrough; the new site's recommended spawn is kept
    ///   (<see cref="SetSpawn"/>) and stepping in lands you there, facing where it looks (the full-size pose is re-based
    ///   under your feet), and the "You are here" pin stands on it meanwhile.
    /// modelwheel (the user's call: "the model at the bottom, almost in one's lap, is very difficult to use"):
    /// - By default (<see cref="placement"/> FrontOfMe) the model floats centred in your view: its bounding box straight
    ///   ahead at eye height, ~1 m away, fitted to ≤ 0.9 m (ModelViewLayout). No table is needed.
    /// - It's placed once when Model view opens, from where you stand and look (the anchor), and stays put (world-locked).
    ///   Recentre (the wheel's chip) puts it back in front of you; so does walking more than 1.5 m away or turning more
    ///   than 60° from it (auto, after a short dwell). A recentre glides (Reduce motion: instant).
    /// - A new scan loading onto it keeps the anchor (it appears where the old one was).
    /// - The Model view wheel stands under it, facing you (<see cref="TryWheelPose"/>, ModelWheel).
    /// - OnTable keeps the M7 table placement (site mat → raycast → assumed height); the wheel then floats 24° below
    ///   the eye line.
    public class TabletopController : MonoBehaviour
    {
        public SceneRoot root;
        public Transform rig;
        public Transform head;
        public ModeController mode;
        [Tooltip("The real table under the model (site mat, environment raycast, or an assumed height). Optional.")]
        public RealTable table;
        [Tooltip("Model scale on the table when it isn't fitted (1:50).")]
        public float scale = 0.02f;
        [Tooltip("Without a RealTable: table height above the floor, and how far in front of you the model's centre sits (m).")]
        public float tableHeight = 0.8f;
        public float distance = 0.55f;

        [Header("Model view fit (modelview)")]
        [Tooltip("Fit the model to the table: a round 1:N with its longest side (width, depth or height) at most fitMax. Off: the fixed scale.")]
        public bool fitToTable = true;
        public float fitMax = TabletopFit.DefaultMax;
        [Tooltip("On an assumed or ray-cast table the model's near edge stays this far ahead of the eye (room for the switcher row).")]
        public float nearClearance = 0.32f;

        [Header("Model view placement (modelwheel)")]
        [Tooltip("FrontOfMe: the model floats centred in your view (default). OnTable: on the real table (M7).")]
        public ModelPlacement placement = ModelPlacement.FrontOfMe;
        [Tooltip("A floating model's longest side at most (m): ~40–50° of view at 1 m.")]
        public float floatFitMax = ModelViewLayout.FitMax;
        [Tooltip("Bring the floating model back in front of you when you walk away (> 1.5 m) or turn away (> 60°).")]
        public bool autoRecentre = true;
        [Tooltip("A recentre glides over this long (s); Reduce motion: instant.")]
        public float glideSeconds = 0.35f;
        [Tooltip("Model view's wheel (ModelSwitcherBuilder sets it; found in the scene when not). While there is one, or the\n" +
                 "model floats in front of you, Model view doesn't toast its name and scale: the wheel's lens says them.")]
        public ModelWheel wheel;

        public bool OnTable { get; private set; }
        /// Where the model last went on the table, and what that table point came from ("mat", "raycast", "assumed").
        public Pose TableFrame { get; private set; }
        public string TableSource { get; private set; } = "";
        /// modelview: the model's scale on the table (world metres per scene metre) as last computed, its N ("1:N") and
        /// the label ("1:10"), and half the model's depth along your view (m, at that scale).
        public float Scale { get; private set; }
        public int Denominator { get; private set; }
        public string ScaleLabel { get; private set; } = "";
        /// The wrist strip's word for it in Model view ("Model 1:10"), cached with the label.
        public string ScaleWord { get; private set; } = "";
        public float ModelHalfDepth { get; private set; }
        /// A new scan is loading onto the table: hidden until it's in, then re-fitted.
        public bool Refitting => m_RefitPending;
        public int Refits { get; private set; }
        /// A site's spawn to land on when you step in (after switching models on the table).
        public bool HasPendingSpawn => m_HasSpawn;
        public string PendingSpawnSite { get; private set; }
        /// Raised after the model was re-fitted on the table (a new scan, a new calibration).
        public event System.Action Refitted;

        // modelwheel
        public const string SourceFront = "front";
        /// The model floats in front of you (not on a table).
        public bool Floating => placement == ModelPlacement.FrontOfMe;
        /// The fit's longest side for the placement now.
        public float ActiveFitMax => Floating ? floatFitMax : fitMax;
        /// Where Model view was placed from: your eye and flat heading then (world-locked until a recentre).
        public bool Anchored { get; private set; }
        public Vector3 AnchorEye { get; private set; }
        public float AnchorHeadingDeg { get; private set; }
        /// The model's bounding-box centre and half size (world, at its scale) as last placed.
        public Vector3 ModelCentreWorld { get; private set; }
        public Vector3 ModelHalfSize { get; private set; }
        public int Recentres { get; private set; }
        public string LastRecentre { get; private set; } = "";
        /// Model view's own toasts ("Model view · Kitchen · 1:5", "Kitchen · 1:5") shown so far, and the last one or the
        /// line logged instead (harness: none may show over the wheel).
        public int Toasts { get; private set; }
        public string LastNote { get; private set; } = "";
        public bool Gliding => m_Gliding;
        /// Raised once a recentre has landed (after its glide): the model and its wheel are in their new place.
        public event System.Action Recentred;
        bool m_Gliding;
        float m_GlideT, m_OffSince = -1f;
        Vector3 m_GlideFromEye, m_GlideToEye;
        float m_GlideFromDeg, m_GlideToDeg;
        // end modelwheel

        Pose m_WorldPose;
        Vector3 m_WorldScale = Vector3.one;
        bool m_Captured;
        GameObject m_Content;
        bool m_RefitPending, m_HasSpawn;
        Vector3 m_SpawnFeet;
        float m_SpawnYaw;

        void OnEnable()
        {
            Services.Register(this);
            if (mode != null) mode.Swapped += OnSwap;
            if (root != null) { root.ContentChanged += OnContentChanged; root.Rescaled += OnRescaled; m_Content = root.Content; }
        }

        void OnDisable()
        {
            Services.Unregister(this);
            if (mode != null) mode.Swapped -= OnSwap;
            if (root != null) { root.ContentChanged -= OnContentChanged; root.Rescaled -= OnRescaled; }
        }

        void OnSwap(AppMode m) => Apply(m == AppMode.Tabletop);

        /// Put the scene on the table (true) or back at full size where it was (false), instantly.
        public void Apply(bool onTable)
        {
            if (root == null) return;
            if ((onTable && !OnTable) || !m_Captured) CaptureWorld();
            var t = root.transform;
            if (onTable)
            {
                TablePose(out var pose, out float s);
                t.SetPositionAndRotation(pose.position, pose.rotation);
                t.localScale = Vector3.one * s;
            }
            else
            {
                if (m_HasSpawn) { WorldPose(out m_WorldPose, out _); m_Captured = true; }   // modelview: land at the new site's spawn
                t.SetPositionAndRotation(m_WorldPose.position, m_WorldPose.rotation);
                t.localScale = m_WorldScale;
            }
            Commit(onTable);
        }

        /// Remember where the scene is at full size (before it leaves for the table).
        public void CaptureWorld()
        {
            if (root == null) return;
            var t = root.transform;
            m_WorldPose = new Pose(t.position, t.rotation);
            m_WorldScale = t.localScale;
            m_Captured = true;
        }

        /// The full-size pose the model returns to (the captured one; the current one if never captured). With a pending
        /// spawn (modelview): the pose that puts that spawn under your feet, its view along yours.
        public void WorldPose(out Pose pose, out float worldScale)
        {
            if (m_HasSpawn)
            {
                worldScale = m_Captured ? m_WorldScale.x : 1f;
                pose = TabletopFit.Rebased(m_SpawnFeet, m_SpawnYaw, worldScale, FeetWorld(), TabletopFit.Heading(Heading()));
                return;
            }
            if (!m_Captured && root != null) { var t = root.transform; pose = new Pose(t.position, t.rotation); worldScale = t.localScale.x; return; }
            pose = m_WorldPose;
            worldScale = m_WorldScale.x;
        }

        /// Where the model sits on the table right now: its footprint centre (scene ground, y = 0) on the table point
        /// in front of you — the site mat's model spot when the mat is locked — its front (+Z, the side the scene was
        /// viewed from) turned toward you. modelview: at the fitted scale, pushed back on an assumed or ray-cast table so
        /// its near edge stays `nearClearance` ahead of you.
        public void TablePose(out Pose pose, out float tableScale)
        {
            var box = FootprintLocal(out bool any);
            if (!Anchored) SetAnchorFromHead();   // modelwheel: placed once per entry (and per recentre)
            if (Floating) { FloatPose(box, any, out pose, out tableScale); return; }
            tableScale = FitScale(box, any);
            ModelHalfDepth = any ? box.size.z * 0.5f * tableScale : 0f;
            var h = head != null ? head : rig;
            var eye = h != null ? h.position : Vector3.zero;
            var fwd = Heading();
            var near = eye + fwd * TabletopFit.CentreDistance(distance, ModelHalfDepth, nearClearance);
            Vector3 centre;
            if (table != null && table.TryGetTableFrame(near, out var frame, out var source))
            {
                centre = source == RealTable.SourceMat ? table.ModelSpot(frame) : frame.position;
                TableFrame = frame;
                TableSource = source;
            }
            else
            {
                centre = near;
                centre.y = (rig != null ? rig.position.y : 0f) + tableHeight;
                TableFrame = new Pose(centre, Quaternion.LookRotation(fwd, Vector3.up));
                TableSource = RealTable.SourceAssumed;
            }
            var toViewer = Vector3.ProjectOnPlane(eye - centre, Vector3.up);
            var facing = toViewer.sqrMagnitude > 1e-4f ? toViewer.normalized : -fwd;
            var rot = Quaternion.LookRotation(facing, Vector3.up);
            var centreLocal = any ? new Vector3(box.center.x, 0f, box.center.z) : Vector3.zero;
            pose = new Pose(centre - rot * (centreLocal * tableScale), rot);
            ModelCentreWorld = centre + Vector3.up * ((any ? box.center.y : 0f) * tableScale);
            ModelHalfSize = any ? box.extents * tableScale : Vector3.zero;
        }

        // ---------------- modelwheel: the model in front of you ----------------

        /// The floating pose: the model's box centred straight ahead of the anchor at eye height (ModelViewLayout).
        void FloatPose(Bounds box, bool any, out Pose pose, out float scale)
        {
            scale = FitScale(box, any);
            ModelHalfDepth = any ? box.size.z * 0.5f * scale : 0f;
            ModelHalfSize = any ? box.extents * scale : Vector3.zero;
            pose = ModelViewLayout.ModelPose(AnchorEye, AnchorHeadingDeg, any ? box.center : Vector3.zero, scale, ModelHalfSize, out var centre);
            ModelCentreWorld = centre;
            TableFrame = new Pose(centre, TabletopFit.Yaw(AnchorHeadingDeg));
            TableSource = SourceFront;
        }

        /// Anchor Model view where you are now: your eye and flat heading.
        void SetAnchorFromHead()
        {
            var h = head != null ? head : rig;
            AnchorEye = h != null ? h.position : Vector3.zero;
            AnchorHeadingDeg = ModelViewLayout.HeadingDeg(h != null ? h.forward : Vector3.forward, Anchored ? AnchorHeadingDeg : 0f);
            Anchored = true;
        }

        /// Recentre (the wheel's chip, walking or turning away): place Model view in front of you again — a floating
        /// model glides there (Reduce motion: at once); on a table it goes to the table in front of you. False outside
        /// Model view.
        public bool Recentre(string why = "chip")
        {
            if (root == null || !OnTable || AppState.Mode != AppMode.Tabletop) return false;
            var fromEye = AnchorEye;
            float fromDeg = AnchorHeadingDeg;
            bool had = Anchored;
            SetAnchorFromHead();   // (looking straight down keeps the old heading)
            Recentres++;
            LastRecentre = why;
            m_OffSince = -1f;
            if (Floating && had && glideSeconds > 0f && !AirTools.UI.UiSettings.ReducedMotion && Application.isPlaying)
            {
                m_GlideFromEye = fromEye; m_GlideFromDeg = fromDeg;
                m_GlideToEye = AnchorEye; m_GlideToDeg = AnchorHeadingDeg;
                AnchorEye = fromEye; AnchorHeadingDeg = fromDeg;
                m_GlideT = 0f;
                m_Gliding = true;
            }
            else PlaceNow();
            Log.Info($"Model view: recentred ({why}) {(Floating ? "in front of you" : "on the table")}, heading {AnchorHeadingDeg:0}°{(m_Gliding ? " (gliding)" : "")}");
            if (!m_Gliding) Recentred?.Invoke();
            return true;
        }

        /// Put the model where the anchor says, now (scale unchanged).
        void PlaceNow()
        {
            TablePose(out var pose, out float s);
            var t = root.transform;
            t.SetPositionAndRotation(pose.position, pose.rotation);
            t.localScale = Vector3.one * s;
            SnapService.Scale = t.lossyScale.x;
            Physics.SyncTransforms();
        }

        /// Model view's wheel: the builder's reference, else the registered one, else one in the scene (cached). Not per
        /// frame: Commit only.
        ModelWheel FindWheel()
        {
            if (wheel != null) return wheel;
            if (Services.TryGet<ModelWheel>(out var registered) && registered != null) return wheel = registered;
            return wheel = FindAnyObjectByType<ModelWheel>(FindObjectsInactive.Include);
        }

        /// The Model view wheel's frame (ModelWheel): its lens under the model, facing the eyes. False before Model view
        /// has been placed.
        public bool TryWheelPose(float lensTop, out Pose pose)
        {
            pose = default;
            if (!Anchored) return false;
            float down = Floating
                ? ModelViewLayout.WheelDownDeg(ModelViewLayout.ModelBottomDownDeg(AnchorEye, AnchorHeadingDeg, ModelCentreWorld, ModelHalfSize), lensTop,
                    ModelViewLayout.WheelDistance, StatusClearDeg(lensTop))
                : Mathf.Max(ModelViewLayout.TableWheelDownDeg, StatusClearDeg(lensTop));
            pose = ModelViewLayout.WheelPose(AnchorEye, AnchorHeadingDeg, down);
            return true;
        }

        /// The lowest the status line reaches while it's up (the guide rail on, a job's progress), looking at the model:
        /// the wheel's lens steps down under it (ModelViewLayout.StatusClearDownDeg). The 16° floor when it's down.
        float StatusClearDeg(float lensTop)
        {
            var line = AirTools.UI.StatusLine.Current;
            if (line == null || !line.Live) return ModelViewLayout.WheelMinDownDeg;
            float gaze = TabletopFit.BelowEyeDeg(AnchorEye, ModelCentreWorld);
            float half = Mathf.Atan2(line.MaxSize.y * 0.5f, Mathf.Max(0.1f, line.distance)) * Mathf.Rad2Deg;
            return ModelViewLayout.StatusClearDownDeg(gaze, line.belowGazeDeg, half, lensTop);
        }

        /// The status line is up and the wheel has stepped down under it (harness).
        public bool WheelUnderStatusLine => AirTools.UI.StatusLine.Current != null && AirTools.UI.StatusLine.Current.Live;

        /// Walked or turned away from the floating model for a moment: recentre (not while the wheel is being worked).
        void AutoRecentre(float now)
        {
            if (!autoRecentre || !Floating || !OnTable || !Anchored || m_Gliding || AppState.Mode != AppMode.Tabletop
                || (mode != null && mode.IsTransitioning) || (Services.TryGet<ModelWheel>(out var wheel) && wheel.Interacting))
            {
                m_OffSince = -1f;
                return;
            }
            var h = head != null ? head : rig;
            if (h == null) return;
            var why = ModelViewLayout.Why(h.position, h.forward, AnchorEye, ModelCentreWorld);
            if (why == ModelViewLayout.Recentre.None) { m_OffSince = -1f; return; }
            if (m_OffSince < 0f) { m_OffSince = now; return; }
            if (now - m_OffSince < ModelViewLayout.RecentreDwell) return;
            Recentre(why == ModelViewLayout.Recentre.Walked ? "walked away" : "turned away");
        }

        void Glide(float dt)
        {
            m_GlideT += dt / Mathf.Max(0.01f, glideSeconds);
            ModelViewLayout.Glide(m_GlideFromEye, m_GlideFromDeg, m_GlideToEye, m_GlideToDeg, m_GlideT, out var eye, out float deg);
            AnchorEye = eye;
            AnchorHeadingDeg = deg;
            bool landed = m_GlideT >= 1f;
            if (landed) { AnchorEye = m_GlideToEye; AnchorHeadingDeg = m_GlideToDeg; m_Gliding = false; }
            if (root != null && OnTable) PlaceNow();
            if (landed) Recentred?.Invoke();
        }
        // end modelwheel

        /// modelview: the table scale for a footprint — fitted (1:N, longest side ≤ fitMax) or the fixed `scale` — and
        /// its label, cached until N changes.
        float FitScale(Bounds box, bool any)
        {
            int n = fitToTable && any ? TabletopFit.Denominator(TabletopFit.LongestSide(box.size), ActiveFitMax) : 0;
            float s = TabletopFit.ScaleFor(n, scale);
            if (n <= 0) n = Mathf.Max(1, Mathf.RoundToInt(1f / Mathf.Max(s, 1e-6f)));
            Scale = s;
            if (n != Denominator || ScaleLabel.Length == 0) { Denominator = n; ScaleLabel = TabletopFit.Label(n); ScaleWord = TabletopFit.ScaleWord(ScaleLabel); }
            return s;
        }

        /// Your flat heading (the head's, else the rig's).
        Vector3 Heading()
        {
            var h = head != null ? head : rig;
            var fwd = Vector3.ProjectOnPlane(h != null ? h.forward : Vector3.forward, Vector3.up);
            return fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
        }

        /// Settle on the table (true) or at full size (false): snapping and click radii follow the model's scale.
        public void Commit(bool onTable) => Commit(onTable, false);

        void Commit(bool onTable, bool refit)
        {
            if (root == null) return;
            OnTable = onTable;
            if (!onTable) { Anchored = false; m_Gliding = false; m_OffSince = -1f; }   // modelwheel: the next entry places afresh
            SnapService.Scale = root.transform.lossyScale.x;
            Physics.SyncTransforms();
            // hcheck (tools/demo/hcheck.py tabletop.toggle) reads "Tabletop on (1:N)".
            Log.Info($"Tabletop {(onTable ? $"on ({ScaleLabel}) {(Floating ? "in front of you" : $"table from {TableSource}")}{(refit ? " (re-fitted)" : "")}" : "off (1:1)")}");
            if (onTable)
            {
                string name = ModelSites.Name(ModelSites.Current(root.IsRuntimePackage, root.Site));
                string note = refit ? $"{name} · {ScaleLabel}" : $"Model view · {name} · {ScaleLabel}";
                // modelwheel: the wheel's lens already says it ("1:5 · On view" under "Kitchen"), right under the model, and
                // the wrist strip says "Model 1:5"; a toast 12° below your gaze would sit on the wheel. Quiet whenever the
                // model floats or there is a wheel — however it was reached (entry, a re-fit, the built-in facade) and
                // whether or not the wheel has registered yet. Only the table placement without a wheel toasts, as before.
                bool quiet = Floating || FindWheel() != null;
                LastNote = note;
                if (quiet) Log.Info($"Model view: {note} (on the wheel's lens, no toast)");
                else
                {
                    Toasts++;
                    AirTools.UI.UiToast.Show(note, AirTools.UI.ColorRole.Info);
                }
            }
            else if (m_HasSpawn)
            {
                // modelview: stepped in after switching models: the full-size pose already puts the spawn under you.
                CaptureWorld();
                m_HasSpawn = false;
                if (rig != null && Services.TryGet<AirTools.Input.Locomotion>(out var loco)) loco.SetHome(new Pose(rig.position, rig.rotation));
                Log.Info($"Model view: stepped in at the {PendingSpawnSite ?? "scene"}'s spawn");
            }
        }

        /// The floor point under the head (the room's floor is the rig's height): where you stand.
        public Vector3 FeetWorld()
        {
            var h = head != null ? head : rig;
            var p = h != null ? h.position : Vector3.zero;
            p.y = rig != null ? rig.position.y : 0f;
            return p;
        }

        /// The SceneRoot-local point that will be under your feet when you step in (the "You are here" pin).
        public Vector3 StepInAnchorLocal()
        {
            WorldPose(out var world, out float ws);
            return TransitionMath.AnchorLocal(world, ws, FeetWorld());
        }

        /// The point of the scene that sits on the table: the middle of the scene's footprint at ground level (y = 0),
        /// in SceneRoot space.
        public Vector3 SceneCentreLocal()
        {
            var b = FootprintLocal(out bool any);
            return any ? new Vector3(b.center.x, 0f, b.center.z) : Vector3.zero;
        }

        /// The building's bounds in SceneRoot space (the big flat ground plate skipped; modelview: a scan's own big mesh
        /// counts — the old "> 6 m" rule left a gym or a hospital with no footprint).
        public Bounds FootprintLocal(out bool any)
        {
            any = false;
            var b = new Bounds();
            if (root == null) return b;
            var content = root.Content != null ? root.Content.transform : root.transform;
            float s = Mathf.Max(root.transform.lossyScale.x, 1e-4f);
            Bounds plates = default;
            bool anyPlate = false;
            foreach (var r in content.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || r is LineRenderer) continue;   // hidden collision meshes; overlay lines
                var lb = root.transform.InverseTransformPoint(r.bounds.center);
                var ext = r.bounds.extents / s;
                if (TabletopFit.IsGroundPlate(ext))
                {
                    if (!anyPlate) { plates = new Bounds(lb, ext * 2f); anyPlate = true; } else plates.Encapsulate(new Bounds(lb, ext * 2f));
                    continue;
                }
                if (!any) { b = new Bounds(lb, ext * 2f); any = true; } else b.Encapsulate(new Bounds(lb, ext * 2f));
            }
            if (!any && anyPlate) { b = plates; any = true; }   // a flat scan (all slab) still has a footprint
            return b;
        }

        // ---------------- modelview: switching models on the table ----------------

        /// A load now must not move the rig (Model view, or on the way there): SceneStreamer hands the spawn over.
        public bool DefersSpawn => OnTable || AppState.Mode == AppMode.Tabletop;

        /// The site loaded on the table: land at this spawn (SceneRoot space: the feet and the view's heading) when you
        /// step in.
        public void SetSpawn(Vector3 feetLocal, float yawLocalDeg, string site = null)
        {
            m_HasSpawn = true;
            m_SpawnFeet = feetLocal;
            m_SpawnYaw = yawLocalDeg;
            PendingSpawnSite = site;
            Log.Info($"Model view: {site ?? "scene"} spawn kept for stepping in (root {feetLocal}, heading {yawLocalDeg:0}°)");
        }

        public void ClearSpawn() { m_HasSpawn = false; PendingSpawnSite = null; }

        /// Settings ▸ Home in Model view: step into the model on the table at its recommended spawn (the one chosen on the
        /// table already, else the loaded site's). False when not on the table.
        public bool WalkInAtSpawn()
        {
            if (!OnTable || root == null) return false;
            if (!m_HasSpawn)
            {
                Vector3 feet = default;
                float yaw = 0f;
                bool got = root.IsRuntimePackage
                    ? Services.TryGet<SceneStreamer>(out var streamer) && streamer.TrySpawnLocal(root.Manifest, out feet, out yaw)
                    : SceneLoader.SpawnLocal(root.Package, out feet, out yaw);
                if (got) SetSpawn(feet, yaw, ModelSites.Current(root.IsRuntimePackage, root.Site));
            }
            return AppCommands.StepIn();
        }

        /// Re-fit the model on the table now (a new scan or calibration): the fitted scale, its place, snapping.
        public void Refit()
        {
            if (root == null || !OnTable) return;
            TablePose(out var pose, out float s);
            var t = root.transform;
            t.SetPositionAndRotation(pose.position, pose.rotation);
            t.localScale = Vector3.one * s;
            Refits++;
            Commit(true, refit: true);
            Refitted?.Invoke();
        }

        void OnContentChanged()
        {
            if (root == null || root.Content == m_Content) return;   // a revision swapped in place: nothing to re-fit
            m_Content = root.Content;
            if (!OnTable && AppState.Mode != AppMode.Tabletop) return;
            // The new scan would show at the old model's scale (a 50 m gym at a kitchen's 1:5) until it's in: hide it.
            m_RefitPending = true;
            if (OnTable) root.SetVisible(false);
        }

        void OnRescaled(float factor)
        {
            // Content scales about the root origin: the kept spawn (root space) scales with it.
            if (m_HasSpawn && factor > 0f) m_SpawnFeet *= factor;
            if (OnTable) m_RefitPending = true;
        }

        void LateUpdate()
        {
            // modelwheel: a recentre gliding; walked / turned away from the floating model.
            if (m_Gliding) Glide(Time.unscaledDeltaTime);
            else AutoRecentre(Time.unscaledTime);
            if (!m_RefitPending || root == null) return;
            if (Services.TryGet<SceneStreamer>(out var streamer) && streamer.Loading) return;
            if (mode != null && mode.IsTransitioning) return;
            m_RefitPending = false;
            // Shown again whatever happened meanwhile (it may have left the table while the scan came in).
            root.SetVisible(AppState.Mode != AppMode.Passthrough);
            if (OnTable) Refit();
        }
    }
}
