using AirTools.Core;
using AirTools.Scene;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// The install coach's drill crosshair (backend F17 `tool: "drill"`): while a drill step is up, a ring with a centre
    /// dot where your gaze meets the scene. Aim it at the spot you mean to drill and "check it" (said, or the coach card's
    /// Check it button): lane G1 sends a fresh frame and the crosshair as `drill_px` from GrokState. Hands-free and
    /// hands-only: it follows the head, and holds still while you look at the coach card to press the button.
    public class DrillCrosshair : MonoBehaviour
    {
        public Transform head;
        [Tooltip("The coach card: looking at it holds the crosshair.")]
        public Transform card;
        public CoachRailView cardView;
        public LineRenderer ring;
        public LineRenderer centre;
        public int segments = 32;
        [Tooltip("Ring radius per metre of viewing distance (angular size ≈ 1.2°).")]
        public float radiusPerMetre = 0.011f;
        public float widthPerMetre = 0.0022f;
        public float holdConeDeg = 22f;
        public float maxDistance = 20f;

        public bool Shown { get; private set; }
        public bool Holding { get; private set; }
        public int Moves { get; private set; }

        Vector3 m_Point, m_Normal = Vector3.forward;
        string m_Site;   // sitescope
        MaterialPropertyBlock m_Block;
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        void LateUpdate()
        {
            var coach = GrokRails.Coach;
            // Only on a visible scan (in passthrough the hidden mesh would catch the gaze).
            bool scene = Services.TryGet<SceneRoot>(out var shown) && shown.Content != null && shown.IsVisible;
            bool want = coach != null && coach.IsDrill && head != null && scene;
            // sitescope: the drill step belongs to the site the coach drew on; another site's aim never goes out as drill_px.
            if (m_Site == null) m_Site = SiteScope.Current;
            else if (!ReferenceEquals(m_Site, SiteScope.Current))
            {
                m_Site = SiteScope.Current;
                GrokState.DrillWorld = null; GrokState.DrillPoint = null; GrokState.DrillPx = null;
            }
            if (want && Services.TryGet<CoachOverlay>(out var overlay) && overlay.DrawnSite != null && !SiteScope.IsCurrent(overlay.DrawnSite)) want = false;
            if (!want) { SetShown(false); return; }
            Holding = card != null && card.gameObject.activeInHierarchy
                      && (CoachFrameRays.ShouldHoldCrosshair(head.forward, card.position - head.position, holdConeDeg) || Pressing());
            if (!Holding && Physics.Raycast(new Ray(head.position, head.forward), out var hit, maxDistance, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore))
            {
                m_Point = hit.point;
                m_Normal = hit.normal;
                Moves++;
                GrokState.DrillWorld = hit.point;
                GrokState.DrillPoint = Services.TryGet<SceneRoot>(out var root) && root.Content != null
                    ? root.Content.transform.InverseTransformPoint(hit.point) : (Vector3?)null;
                GrokState.DrillEye = new Pose(head.position, head.rotation);
                if (GrokState.DrillPx == null) GrokState.DrillPx = new[] { 0.5f, 0.5f };   // the centre of the view at DrillEye
            }
            bool onScene = GrokState.DrillWorld.HasValue;
            SetShown(onScene);
            if (onScene) Draw(coach.Paused);
        }

        /// A finger or ray is on one of the card's buttons (no allocation: called every frame).
        bool Pressing() => cardView != null
            && (Busy(cardView.checkIt) || Busy(cardView.back) || Busy(cardView.repeat) || Busy(cardView.next));

        static bool Busy(GlassButton b) => b != null && b.State != Oculus.Interaction.InteractableState.Normal;

        void SetShown(bool on)
        {
            Shown = on;
            if (ring != null && ring.gameObject.activeSelf != on) ring.gameObject.SetActive(on);
            if (centre != null && centre.gameObject.activeSelf != on) centre.gameObject.SetActive(on);
        }

        void Draw(bool stopped)
        {
            float d = Mathf.Max(0.2f, Vector3.Distance(head.position, m_Point));
            var n = m_Normal.sqrMagnitude > 1e-6f ? m_Normal.normalized : -head.forward;
            var c = m_Point + n * 0.004f * d;
            var u = Vector3.Cross(n, Mathf.Abs(Vector3.Dot(n, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up).normalized;
            var v = Vector3.Cross(n, u);
            float r = radiusPerMetre * d;
            // Blue for "aim here", red on the step the coach stopped (never colour alone: the card says Stop).
            var color = UiTheme.Current.Color(stopped ? ColorRole.Danger : GrokRailTones.Info);
            if (ring != null)
            {
                ring.useWorldSpace = true;
                ring.loop = true;
                ring.positionCount = segments;
                for (int k = 0; k < segments; k++)
                {
                    float a = k * Mathf.PI * 2f / segments;
                    ring.SetPosition(k, c + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r);
                }
                ring.widthMultiplier = widthPerMetre * d;
                Tint(ring, color);
            }
            if (centre != null)
            {
                centre.useWorldSpace = true;
                centre.loop = false;
                centre.positionCount = 2;
                centre.SetPosition(0, c - u * r * 0.18f);
                centre.SetPosition(1, c + u * r * 0.18f);
                centre.widthMultiplier = r * 0.36f;
                Tint(centre, color);
            }
        }

        void Tint(LineRenderer lr, Color col)
        {
            lr.startColor = lr.endColor = col;
            if (lr.sharedMaterial == null) return;
            m_Block ??= new MaterialPropertyBlock();
            lr.GetPropertyBlock(m_Block);
            m_Block.SetColor(s_BaseColor, col);
            lr.SetPropertyBlock(m_Block);
        }
    }
}
