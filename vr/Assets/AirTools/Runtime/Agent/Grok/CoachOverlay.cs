using AirTools.Core;
using AirTools.Scene;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// The install coach in the scene: each coach_check `results[].box` drawn on the scene from the checked frame's
    /// camera pose (a scene package camera by `frame_id`, else the frame G1 last sent, GrokState.LastFrameId), in its
    /// chip's colour; and on coach_stop a red box on the outlet or switch plus a red "Don't drill here" card over the
    /// drill point. Boxes lie on the plane where the box centre's ray meets the scene, facing that camera
    /// (CoachFrameRays). They clear when the step changes.
    ///
    /// The overlay root follows SceneRoot.Content (position, rotation, scale), so box corners stay in package space
    /// through calibration and the tabletop, and nothing is destroyed with a scene swap.
    public class CoachOverlay : MonoBehaviour, IWorldLabelSource
    {
        public Transform head;
        [Tooltip("Box outlines (check results first, the stop box last). Local positions are package space.")]
        public LineRenderer[] boxes;
        /// The last box drawn at the fallback depth (its ray missed the scan).
        public bool LastFallback { get; private set; }
        [Tooltip("The red card over the drill point (world annotation: overlay glass + overlay text).")]
        public Transform stopCard;
        public GlassSurface stopSurface;
        public TextMeshPro stopText;
        public LineRenderer drillMark;
        public float lineWidthPerMetre = 0.0025f;
        public float minLineWidth = 0.003f;
        [Tooltip("Card scale per metre of viewing distance (it's built for reading at 1 m, so it keeps its angular size).")]
        public float cardScalePerMetre = 1f;
        [Tooltip("The card's centre sits this far above the drill point, at 1 m (m).")]
        public float cardLift = 0.05f;
        [Tooltip("Boxes sit this fraction nearer than the surface their centre hits.")]
        public float pullIn = 0.02f;

        public int BoxesShown { get; private set; }
        public bool StopShown => stopCard != null && stopCard.gameObject.activeSelf;
        public string LastFrame { get; private set; }
        public string LastError { get; private set; }
        /// The drill point's scene position (world) while the stop card shows.
        public Vector3 StopPoint { get; private set; }

        int m_Version = -1;
        MaterialPropertyBlock m_Block;
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        void OnEnable() => Services.Register(this);
        void OnDisable() { Services.Unregister(this); WorldLabels.Remove(this); }

        // Declutter C (S10): the "Don't drill here" card is safety (P0, mandatory) in WorldLabels' pool: it always shows
        // and the other labels make room. The boxes are outlines and don't count.
        bool m_ClaimedStop;

        public void ClaimLabels(System.Collections.Generic.List<LabelClaim> claims) =>
            claims.Add(new LabelClaim(LabelClass.Safety, StopShown && stopCard.gameObject.activeInHierarchy ? 1 : 0, 0, 0f));

        public void ApplyLabels(LabelGrant[] grants, int first) { }

        void ClaimStop()
        {
            if (StopShown == m_ClaimedStop) return;
            m_ClaimedStop = StopShown;
            WorldLabels.Changed(this);
        }
        // end Declutter C

        void LateUpdate() => Refresh();

        /// Follow the scene and redraw when the coach changed (every frame; the harness calls it too).
        public void Refresh()
        {
            Follow();
            if (!ReferenceEquals(m_Site, SiteScope.Current)) FollowSite();   // sitescope
            if (GrokRails.Version != m_Version)
            {
                m_Version = GrokRails.Version;
                Redraw(GrokRails.Coach);
            }
            if (StopShown && head != null) Billboard();
        }

        // ---------------- sitescope: the coach's boxes belong to the site they were drawn on ----------------

        string m_Site, m_DrawnSite;
        readonly ParkedObjects m_Parked = new ParkedObjects();

        /// The boxes and the stop card were drawn on this site (null: nothing drawn yet).
        public string DrawnSite => m_DrawnSite;

        /// Another site loaded: the boxes and card drawn on the site left hide (package-space outlines would land on the
        /// new scan); back on their site they show again as they were. A coach step that arrives meanwhile redraws on the
        /// site loaded then.
        void FollowSite()
        {
            m_Site = SiteScope.Current;
            if (m_DrawnSite == null) return;
            if (m_Site == m_DrawnSite) m_Parked.ShowAll();
            else if (m_Parked.Count == 0)
            {
                if (boxes != null) foreach (var b in boxes) m_Parked.Hide(b);
                m_Parked.Hide(stopCard);
                m_Parked.Hide(drillMark);
            }
            ClaimStop();
        }
        // end sitescope

        /// Mirror the scene content's transform (package space → world).
        void Follow()
        {
            if (!Services.TryGet<SceneRoot>(out var root) || root.Content == null) return;
            var c = root.Content.transform;
            transform.SetPositionAndRotation(c.position, c.rotation);
            var s = c.lossyScale;
            transform.localScale = new Vector3(s.x, s.y, s.z);
        }

        public void Redraw(CoachSessionModel m)
        {
            m_Parked.Clear();   // sitescope: a new step draws on the site loaded now
            m_Site = m_DrawnSite = SiteScope.Current;
            DrawCoach(m);
            ClaimStop();   // Declutter C
        }

        void DrawCoach(CoachSessionModel m)
        {
            HideAll();
            if (m == null || m.Finished) return;
            var check = m.LastCheck;
            bool paused = m.Paused && m.Stop != null;
            if (check == null && !paused) return;
            var frameId = GrokRails.FrameFor(check);
            LastFrame = frameId;
            bool hasFrame = TryFrame(frameId, out var frame);
            LastError = hasFrame ? null : $"no camera for frame '{frameId ?? "-"}'";
            if (!hasFrame) Log.Warn($"Coach overlay: {LastError}{(paused ? "; the stop card goes on the crosshair" : "")}");
            int used = 0;
            if (hasFrame && check != null)
                foreach (var r in check.results)
                {
                    if (used >= (boxes?.Length ?? 0) - 1) break;   // keep the last outline for the stop box
                    if (!CoachFrameRays.ValidBox(r.box)) continue;
                    if (Draw(boxes[used], frame, r.box, UiTheme.Current.Color(GrokRailTones.Chip(CoachSessionModel.State(r))))) used++;
                }
            if (paused)
            {
                if (hasFrame && boxes != null && boxes.Length > 0 && CoachFrameRays.ValidBox(m.Stop.box)
                    && Draw(boxes[boxes.Length - 1], frame, m.Stop.box, UiTheme.Current.Color(ColorRole.Danger))) used++;
                ShowStop(hasFrame, frame, m.Stop);
            }
            BoxesShown = used;
        }

        void HideAll()
        {
            if (boxes != null) foreach (var b in boxes) if (b != null) b.gameObject.SetActive(false);
            if (stopCard != null) stopCard.gameObject.SetActive(false);
            if (drillMark != null) drillMark.gameObject.SetActive(false);
            BoxesShown = 0;
        }

        /// A scene package camera by id, else the view frame G1 registered under that id.
        public static bool TryFrame(string frameId, out PinholeFrame frame)
        {
            frame = default;
            if (string.IsNullOrEmpty(frameId)) return false;
            if (Services.TryGet<SceneStreamer>(out var streamer) && streamer.FindCamera(frameId) is SceneCameraJson cam && cam.R != null)
            {
                frame = CoachFrameRays.FromSceneCamera(cam);
                return frame.IsValid;
            }
            if (frameId == GrokState.ViewFrameId && GrokState.ViewFrame.IsValid) { frame = GrokState.ViewFrame; return true; }
            return false;
        }

        /// Where a frame point meets the scene, in package space (null when the ray misses).
        Vector3? Hit(in PinholeFrame f, float nx, float ny)
        {
            var local = CoachFrameRays.PixelRay(f, nx, ny);
            var ray = new Ray(transform.TransformPoint(local.origin), transform.TransformDirection(local.direction).normalized);
            if (!Physics.Raycast(ray, out var hit, 500f, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore)) return null;
            return transform.InverseTransformPoint(hit.point);
        }

        bool Draw(LineRenderer lr, in PinholeFrame f, float[] box, Color color)
        {
            if (lr == null) return false;
            var c = CoachFrameRays.Centre(box);
            var p = Hit(f, c.x, c.y);
            if (!p.HasValue)
            {
                // The box's ray leaves the scan (e.g. the photo's edge, beyond the captured mesh): still draw it — a stop
                // box is a safety cue — at the depth where the frame's centre ray lands, else 1.5 m out.
                var mid = Hit(f, 0.5f, 0.5f);
                float fallback = mid.HasValue ? CoachFrameRays.Depth(f, mid.Value) : 1.5f;
                p = f.Position + CoachFrameRays.Direction(f, c.x, c.y) * fallback;
                LastFallback = true;
            }
            float depth = CoachFrameRays.Depth(f, p.Value) * (1f - pullIn);
            var corners = CoachFrameRays.BoxCorners(f, box, depth);
            if (corners == null) return false;
            lr.gameObject.SetActive(true);
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.positionCount = 4;
            for (int k = 0; k < 4; k++) lr.SetPosition(k, corners[k]);
            float d = head != null ? Vector3.Distance(head.position, transform.TransformPoint(p.Value)) : 1.5f;
            lr.widthMultiplier = Mathf.Max(minLineWidth, lineWidthPerMetre * d);
            Tint(lr, color);
            return true;
        }

        /// The red card over the drill point: drill_px in the checked frame, else the crosshair's own scene point.
        void ShowStop(bool hasFrame, in PinholeFrame f, CoachStopArgs stop)
        {
            if (stopCard == null) return;
            var px = stop.drill_px != null && stop.drill_px.Length >= 2 ? stop.drill_px : new[] { 0.5f, 0.5f };
            var p = hasFrame ? Hit(f, px[0], px[1]) : null;
            if (!p.HasValue && GrokState.DrillPoint.HasValue) p = GrokState.DrillPoint;   // the crosshair's own point
            if (!p.HasValue) return;
            StopPoint = transform.TransformPoint(p.Value);
            stopCard.gameObject.SetActive(true);
            if (stopText != null) stopText.text = GrokRailText.StopCard(stop);
            if (stopSurface != null)
            {
                stopSurface.customTint = true;
                stopSurface.tint = UiTheme.Current.Color(ColorRole.Danger);
                stopSurface.Rebuild();
            }
            if (stopText != null) stopText.color = UiTheme.Current.Color(ColorRole.Background);
            if (drillMark != null)
            {
                drillMark.gameObject.SetActive(true);
                drillMark.useWorldSpace = true;
                Tint(drillMark, UiTheme.Current.Color(ColorRole.Danger));
            }
            Billboard();
        }

        /// The card faces the eyes above the drill point at a constant angular size; the mark is an ✗ on the point.
        void Billboard()
        {
            if (head == null || stopCard == null) return;
            var to = StopPoint - head.position;
            float d = Mathf.Max(0.3f, to.magnitude);
            var dir = to / d;
            float s = cardScalePerMetre * d;
            stopCard.SetPositionAndRotation(StopPoint - dir * (0.03f * d) + Vector3.up * (cardLift * s), Quaternion.LookRotation(dir, Vector3.up));
            stopCard.localScale = Vector3.one * s / Mathf.Max(1e-4f, transform.lossyScale.x);
            if (drillMark != null && drillMark.gameObject.activeSelf)
            {
                var right = Vector3.Cross(Vector3.up, dir).normalized * 0.02f * d;
                var up = Vector3.up * 0.02f * d;
                var c = StopPoint - dir * 0.01f;
                drillMark.positionCount = 5;
                drillMark.loop = false;
                drillMark.SetPosition(0, c - right - up);
                drillMark.SetPosition(1, c + right + up);
                drillMark.SetPosition(2, c);
                drillMark.SetPosition(3, c - right + up);
                drillMark.SetPosition(4, c + right - up);
                drillMark.widthMultiplier = Mathf.Max(minLineWidth, lineWidthPerMetre * d);
            }
        }

        void Tint(LineRenderer lr, Color c)
        {
            lr.startColor = lr.endColor = c;
            if (lr.sharedMaterial == null) return;
            m_Block ??= new MaterialPropertyBlock();
            lr.GetPropertyBlock(m_Block);
            m_Block.SetColor(s_BaseColor, c);
            lr.SetPropertyBlock(m_Block);
        }
    }
}
