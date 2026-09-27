using System.Collections.Generic;
using AirTools.Core;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Parts
{
    /// gaze-catalog: what the wearer is looking at, for the catalog. About 4 times a second (never per frame) the centre
    /// eye's ray meets the scan (SceneLayers.SceneSurfaceMask: never a placed part or the UI); the surface's normal is the
    /// structure plane's when the hit is on one, else the scan's triangles averaged over 5 rays; a structure window / door,
    /// a taped opening, an appliance (a scene part of class appliance, a structure "appliance") are recognised under the
    /// hit. GazeFocusMath classifies it in the scan's own up frame (SiteFrame: rebuilt when the content, its structure or
    /// its scale changes) and GazeFocusFilter holds it ~1.2 s before the catalog (CatalogWindow.OnGazeFocus) follows.
    /// Paused (the held focus stays) while the palm menu is up or the ray goes through the catalog itself: reading the
    /// catalog must not turn it to the ground behind it. World mode only. No allocation per sample (a new collider under
    /// the ray looks its scene part up once).
    public class GazeFocusTracker : MonoBehaviour
    {
        public CatalogWindow window;
        [Tooltip("The centre eye; the main camera when empty.")]
        public Transform head;
        public float samplesPerSecond = 4f;
        [Tooltip("Seconds a focus must hold before the catalog follows it / before looking at nothing clears it.")]
        public float holdSeconds = 1.2f, noneSeconds = 2.5f;
        [Tooltip("Metres the ray reaches (a drone scan's roof can be a hundred metres off).")]
        public float maxDistance = 150f;
        [Tooltip("Degrees between the centre ray and the four rays that smooth a mesh normal.")]
        public float spreadDeg = 2f;
        [Tooltip("The catalog panel (its content's local size): a ray through it, plus the margin, pauses the tracker.")]
        public Vector2 panelSize = new Vector2(0.30f, 0.40f);
        public float panelMargin = 0.06f;
        [Tooltip("How far below the panel the open keyboard reaches (m).")]
        public float keyboardDrop = 0.24f;

        public readonly GazeFocusFilter Filter = new GazeFocusFilter();
        public SiteFrame Frame => m_Frame;
        public GazeHit LastHit => m_Hit;
        public CatalogFocus LastSample { get; private set; }
        public string LastSubject { get; private set; }
        /// "plane p22", "mesh", "miss" (the harness).
        public string LastSurface { get; private set; } = "";
        public int Samples { get; private set; }
        /// Why sampling is paused ("catalog", "palm", "mode", "no scan", "no frame"), null while sampling.
        public string Paused { get; private set; }
        /// The harness: sample from this eye pose instead of the head (and never pause for the UI).
        public Pose? eyeOverride;

        SceneRoot m_Root;
        SiteFrame m_Frame;
        GazeHit m_Hit;
        bool m_Dirty = true;
        float m_Next;
        string m_FrameEnv, m_FrameSite;
        readonly List<FramePlane> m_Planes = new List<FramePlane>();
        readonly List<Vector3> m_Axes = new List<Vector3>();
        readonly List<FocusRect> m_Rects = new List<FocusRect>();
        Collider m_Collider;
        string m_ColliderSubject;
        bool m_ColliderAppliance;

        const string Surface_Mesh = "mesh", Surface_Miss = "miss";
        /// A package levelled within this (scene.json gravity_residual_deg) keeps its own up.
        public const double LevelledDeg = 2.0;

        void OnEnable()
        {
            Services.Register(this);
            Bind();
        }

        void OnDisable()
        {
            Services.Unregister(this);
            if (m_Root != null) { m_Root.ContentChanged -= OnContent; m_Root.Rescaled -= OnRescaled; }
            m_Root = null;
        }

        void Bind()
        {
            if (m_Root != null || !Services.TryGet(out m_Root)) return;
            m_Root.ContentChanged += OnContent;
            m_Root.Rescaled += OnRescaled;
        }

        /// Another scan (or its structure): a new frame, and nothing looked at yet.
        void OnContent()
        {
            m_Dirty = true;
            m_Collider = null;
            Filter.Reset();
            if (window != null) window.OnGazeFocus(CatalogFocus.None, null);
        }

        void OnRescaled(float factor) => m_Dirty = true;

        void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < m_Next) return;
            m_Next = now + 1f / Mathf.Max(0.5f, samplesPerSecond);
            if (m_Root == null) Bind();
            Filter.HoldSeconds = holdSeconds;
            Filter.NoneSeconds = noneSeconds;
            if (!Ready(out string why)) { Paused = why; return; }
            if (m_Dirty || FrameStale()) RebuildFrame();
            if (!m_Frame.Valid) { Paused = "no frame"; return; }
            if (!Eye(out var origin, out var dir)) { Paused = "no head"; return; }
            if (eyeOverride == null && ReadingTheCatalog(origin, dir)) { Paused = "catalog"; return; }
            Paused = null;
            Sample(origin, dir, now);
        }

        bool Ready(out string why)
        {
            why = null;
            if (window == null) { why = "no catalog"; return false; }
            if (AppState.Mode != AppMode.World) { why = "mode"; return false; }
            if (m_Root == null || m_Root.Content == null || !m_Root.IsVisible) { why = "no scan"; return false; }
            if (eyeOverride == null && Services.TryGet<AirTools.Input.PalmMenu>(out var palm) && (palm.IsOpen || Time.unscaledTime - palm.ClosedAt < 0.6f))
            {
                why = "palm";
                return false;
            }
            return true;
        }

        bool FrameStale()
        {
            string env = window != null ? window.Model.Data?.environment : null;
            return env != m_FrameEnv || m_Root.Site != m_FrameSite;
        }

        /// The eye's ray (world). Play Without XR leaves the camera at the rig's feet: aim from a standing eye there.
        bool Eye(out Vector3 origin, out Vector3 dir)
        {
            if (eyeOverride is Pose p)
            {
                origin = p.position;
                dir = p.rotation * Vector3.forward;
                return true;
            }
            var h = head != null ? head : (Camera.main != null ? Camera.main.transform : null);
            origin = dir = default;
            if (h == null) return false;
            origin = h.position;
            if (h.localPosition.y < 0.5f && !UnityEngine.XR.XRSettings.isDeviceActive) origin += Vector3.up * 1.6f;
            dir = h.forward;
            return true;
        }

        /// The ray goes through the open catalog (or its keyboard): the wearer is reading it.
        bool ReadingTheCatalog(Vector3 origin, Vector3 dir)
        {
            if (!window.Showing || window.browser == null || window.browser.content == null) return false;
            var t = window.browser.content.transform;
            var plane = new Plane(t.forward, t.position);
            if (!plane.Raycast(new Ray(origin, dir), out float d) || d <= 0f || d > 3f) return false;
            var local = t.InverseTransformPoint(origin + dir * d);
            float hw = panelSize.x * 0.5f + panelMargin, top = panelSize.y * 0.5f + panelMargin;
            float bottom = -panelSize.y * 0.5f - panelMargin - (window.Model.KeyboardOpen ? keyboardDrop : 0f);
            return Mathf.Abs(local.x) <= hw && local.y <= top && local.y >= bottom;
        }

        /// One sample along (origin, dir) (world): classify and feed the filter. Public for the harness.
        public CatalogFocus Sample(Vector3 origin, Vector3 dir, float now)
        {
            m_Hit = Cast(origin, dir, out string subject);
            var f = GazeFocusMath.Classify(m_Hit, m_Frame, Filter.Held);
            if (f == CatalogFocus.None || (subject != null && !SubjectFits(f))) subject = null;
            LastSample = f;
            LastSubject = subject;
            Samples++;
            if (Filter.Update(f, subject, now) && window != null) window.OnGazeFocus(Filter.Held, Filter.Subject);
            return f;
        }

        /// Classify the surface along (origin, dir) (world) without feeding the hold (the harness looks for targets).
        public CatalogFocus Probe(Vector3 origin, Vector3 dir, out GazeHit hit)
        {
            if (m_Dirty || !m_Frame.Valid) RebuildFrame();
            hit = Cast(origin, dir, out _);
            return GazeFocusMath.Classify(hit, m_Frame, CatalogFocus.None);
        }

        // A thing's name goes with the focus it explains: a window with an opening, an appliance with the floor's things.
        bool SubjectFits(CatalogFocus f) => m_Hit.InOpening ? f == CatalogFocus.Opening : f == CatalogFocus.Ground;

        GazeHit Cast(Vector3 origin, Vector3 dir, out string subject)
        {
            subject = null;
            int mask = SceneLayers.SceneSurfaceMask;
            if (!Physics.Raycast(origin, dir, out var hit, maxDistance, mask, QueryTriggerInteraction.Ignore))
            {
                LastSurface = Surface_Miss;
                return default;
            }
            var rootT = m_Root.transform;
            Vector3 nWorld;
            if (PlaneAt(hit.point, out nWorld, out int plane)) LastSurface = m_Root.Structure.Planes[plane].id ?? "plane";
            else { nWorld = SmoothedNormal(origin, dir, hit); LastSurface = Surface_Mesh; }
            if (Vector3.Dot(nWorld, dir) > 0f) nWorld = -nWorld;   // the side facing the eye
            var g = new GazeHit
            {
                Hit = true,
                Point = rootT.InverseTransformPoint(hit.point),
                Normal = rootT.InverseTransformDirection(nWorld).normalized,
            };
            // A structure window / door, an appliance's front.
            for (int i = 0; i < m_Rects.Count; i++)
            {
                var r = m_Rects[i];
                if (!r.Contains(g.Point, 0.05f, 0.35f)) continue;
                if (r.Kind == FocusRect.Opening) { g.InOpening = true; subject = r.Subject; break; }
                g.OnAppliance = true;
                subject ??= r.Subject;
            }
            // A taped opening.
            if (!g.InOpening)
            {
                var taped = Openings.All;
                for (int i = 0; i < taped.Count; i++)
                    if (InTaped(taped[i], g.Point)) { g.InOpening = true; subject = "your opening"; break; }
            }
            // A scene part under the ray (the kitchen's dishwasher, fridge, range).
            if (!g.InOpening && ColliderPart(hit.collider, out string part, out bool appliance) && appliance)
            {
                g.OnAppliance = true;
                subject = part;
            }
            return g;
        }

        static bool InTaped(in TapedOpening o, Vector3 p)
        {
            if (!o.Valid) return false;
            var d = p - o.Centre;
            return Mathf.Abs(Vector3.Dot(d, o.Right)) <= o.W * 0.5f + 0.05f && Mathf.Abs(Vector3.Dot(d, o.Up)) <= o.H * 0.5f + 0.05f
                && Mathf.Abs(Vector3.Dot(d, o.Out)) <= Mathf.Max(0.35f, o.D + 0.1f);
        }

        /// The structure plane under a world point (within 30 cm of it and inside its outline), its normal in world space.
        bool PlaneAt(Vector3 world, out Vector3 normal, out int index)
        {
            normal = default;
            index = -1;
            var s = m_Root.Structure;
            var content = m_Root.StructureSpace;
            if (s == null || content == null || s.Planes.Length == 0) return false;
            float cal = Mathf.Max(1e-4f, m_Root.Calibration);
            float tol = 0.3f / cal;
            var pk = content.InverseTransformPoint(world);
            float best = tol;
            for (int i = 0; i < s.Planes.Length; i++)
            {
                float d = Mathf.Abs(s.Planes[i].SignedDistance(pk));
                if (d >= best) continue;
                if (!StructureLayer.Contains(s.Planes[i], s.Planes[i].ToPlane(pk), tol)) continue;
                best = d;
                index = i;
            }
            if (index < 0) return false;
            normal = content.TransformDirection(s.Planes[index].normal).normalized;
            return true;
        }

        /// The scan's triangles are noisy: average the hit normal with four rays spreadDeg around it (those that land on
        /// about the same surface).
        Vector3 SmoothedNormal(Vector3 origin, Vector3 dir, RaycastHit centre)
        {
            var sum = centre.normal;
            var side = Vector3.Cross(dir, Vector3.up);
            if (side.sqrMagnitude < 1e-6f) side = Vector3.Cross(dir, Vector3.right);
            side.Normalize();
            var upish = Vector3.Cross(side, dir).normalized;
            int mask = SceneLayers.SceneSurfaceMask;
            for (int i = 0; i < 4; i++)
            {
                var axis = i < 2 ? upish : side;
                var d = Quaternion.AngleAxis(i % 2 == 0 ? spreadDeg : -spreadDeg, axis) * dir;
                if (!Physics.Raycast(origin, d, out var h, maxDistance, mask, QueryTriggerInteraction.Ignore)) continue;
                if (Mathf.Abs(h.distance - centre.distance) > 0.15f * centre.distance + 0.05f) continue;   // another surface
                sum += h.normal;
            }
            return sum.sqrMagnitude > 1e-8f ? sum.normalized : centre.normal;
        }

        /// The scene part a collider belongs to ("the dishwasher"), looked up once per new collider.
        bool ColliderPart(Collider c, out string subject, out bool appliance)
        {
            if (c != m_Collider)
            {
                m_Collider = c;
                m_ColliderSubject = null;
                m_ColliderAppliance = false;
                if (c != null && Services.TryGet<SceneParts>(out var parts) && parts.HasParts)
                    foreach (var comp in parts.Removable)
                    {
                        if (comp == null || !parts.TryNodes(comp.id, out var nodes) || nodes.collision == null) continue;
                        if (!c.transform.IsChildOf(nodes.collision)) continue;
                        m_ColliderSubject = "the " + (comp.DisplayName ?? comp.id).ToLowerInvariant();
                        m_ColliderAppliance = string.Equals(comp.kind, "appliance", System.StringComparison.OrdinalIgnoreCase);
                        break;
                    }
            }
            subject = m_ColliderSubject;
            appliance = m_ColliderAppliance;
            return subject != null;
        }

        /// A SceneRoot-space point is inside a structure window / door (the harness keeps its wall target off them).
        public bool InOpening(Vector3 rootPoint)
        {
            for (int i = 0; i < m_Rects.Count; i++)
                if (m_Rects[i].Kind == FocusRect.Opening && m_Rects[i].Contains(rootPoint, 0.05f, 0.35f)) return true;
            return false;
        }

        // ---------------- the frame ----------------

        /// The scan's frame in SceneRoot space: its structure planes (normal, centroid, area), Manhattan axes, the package's
        /// up and the collision's bounds; window / door / appliance rectangles for the hit checks.
        public void RebuildFrame()
        {
            m_Dirty = false;
            m_Planes.Clear();
            m_Axes.Clear();
            m_Rects.Clear();
            m_FrameSite = m_Root != null ? m_Root.Site : null;
            m_FrameEnv = window != null ? window.Model.Data?.environment : null;
            m_Frame = default;
            if (m_Root == null || m_Root.Content == null) return;
            var rootT = m_Root.transform;
            var content = m_Root.Content.transform;
            float cal = Mathf.Max(1e-4f, m_Root.Calibration);
            var s = m_Root.Structure;
            if (s != null)
            {
                foreach (var p in s.Planes)
                {
                    if (p.outline3d == null || p.outline3d.Length < 3) continue;
                    var c = Vector3.zero;
                    foreach (var q in p.outline3d) c += q;
                    c /= p.outline3d.Length;
                    m_Planes.Add(new FramePlane(
                        rootT.InverseTransformDirection(content.TransformDirection(p.normal)).normalized,
                        rootT.InverseTransformPoint(content.TransformPoint(c)),
                        GazeFocusMath.OutlineArea(p.outline, cal)));
                }
                foreach (var a in s.Axes) m_Axes.Add(rootT.InverseTransformDirection(content.TransformDirection(a)).normalized);
                foreach (var o in s.Objects)
                {
                    if (o.corners == null || o.corners.Length < 4) continue;
                    int kind = GazeFocusMath.RectKind(o.label, out string subject);
                    if (kind < 0) continue;
                    Vector3 C(int i) => rootT.InverseTransformPoint(content.TransformPoint(o.corners[i]));
                    m_Rects.Add(FocusRect.FromCorners(C(0), C(1), C(2), C(3), kind, subject));
                }
            }
            var manifest = m_Root.Manifest;
            var up = rootT.InverseTransformDirection(content.TransformDirection(manifest != null ? manifest.Up : Vector3.up)).normalized;
            // Levelled against gravity (a small residual): the package's up stands (the structure's level planes were
            // regularised to it). Otherwise the structure says where up is.
            bool levelled = manifest != null && manifest.up != null && manifest.up.Length >= 3
                && manifest.gravity_residual_deg.HasValue && manifest.gravity_residual_deg.Value <= LevelledDeg;
            Bounds(rootT, out var min, out var max);
            bool indoor = CatalogFoci.IsIndoor(m_FrameEnv, m_FrameSite);
            m_Frame = GazeFocusMath.Frame(m_Planes, m_Axes, up, levelled, min, max, indoor);
            Log.Info($"Gaze focus: {m_FrameSite ?? "-"} frame {m_Frame} · {m_Planes.Count} planes, {m_Rects.Count} windows / appliances");
        }

        /// The scan's colliders' bounds (the scene-surface ones under the content), as an AABB in SceneRoot space.
        void Bounds(Transform rootT, out Vector3 min, out Vector3 max)
        {
            min = max = Vector3.zero;
            bool any = false;
            var world = new Bounds();
            int layer = SceneLayers.SceneSurface;
            foreach (var c in m_Root.Content.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.gameObject.layer != layer || !c.enabled) continue;
                if (!any) { world = c.bounds; any = true; } else world.Encapsulate(c.bounds);
            }
            if (!any) return;
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = -min;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? world.min.x : world.max.x, (i & 2) == 0 ? world.min.y : world.max.y, (i & 4) == 0 ? world.min.z : world.max.z);
                var r = rootT.InverseTransformPoint(corner);
                min = Vector3.Min(min, r);
                max = Vector3.Max(max, r);
            }
        }
    }
}
