using System.Collections.Generic;
using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Tools
{
    /// A 30 cm spirit level lying on the surface (along the fall line), a bubble that drifts uphill with the tilt,
    /// and a reading label. Green ≤ 0.5°, amber ≤ 2°, red beyond. The reading is a P5 label in WorldLabels' pool
    /// (declutter S10): over the pool it drops to the bar itself.
    public class LevelGizmo : MonoBehaviour, IWorldLabelSource
    {
        public const float BarLength = 0.30f;
        MeasureStyle m_Style;
        Transform m_Bar, m_Bubble;
        MeasureLabel m_Label;
        MaterialPropertyBlock m_Block;
        float m_PulseUntil;
        Color m_Color;
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        public string LabelText => m_Label != null ? m_Label.Text : null;

        public static LevelGizmo Create(Transform parent, MeasureStyle style, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var g = go.AddComponent<LevelGizmo>();
            g.m_Style = style ?? new MeasureStyle();
            g.m_Block = new MaterialPropertyBlock();
            g.m_Bar = MakePart(go.transform, "Bar", new Vector3(BarLength, 0.025f, 0.035f), "Cube.fbx", g.m_Style.markerMaterial);
            g.m_Bubble = MakePart(go.transform, "Bubble", Vector3.one * 0.02f, "Sphere.fbx", g.m_Style.markerMaterial);
            g.m_Label = MeasureLabel.Create(go.transform, g.m_Style, "Label");
            return g;
        }

        static Transform MakePart(Transform parent, string name, Vector3 scale, string mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>(mesh);
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (mat != null) r.sharedMaterial = mat;
            return go.transform;
        }

        /// reading is in the parent's (frame) space.
        public void Show(LevelReading r, bool ghost)
        {
            gameObject.SetActive(true);
            var n = r.Normal.normalized;
            // Bar runs along the fall line; on a dead-level surface pick any tangent.
            var along = r.Downhill.sqrMagnitude > 1e-8f ? r.Downhill : Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            transform.localPosition = r.Point + n * 0.013f;
            // Local +X = along the fall line (downhill), +Y = surface normal.
            transform.localRotation = Quaternion.LookRotation(Vector3.Cross(along, n), n);
            // Bubble floats toward the high end: full travel (±12 cm) at 4°.
            float travel = Mathf.Clamp((float)r.Degrees / 4f, 0f, 1f) * (BarLength * 0.4f);
            m_Bubble.localPosition = new Vector3(-travel, 0.014f, 0f);
            m_Color = r.Degrees <= 0.5 ? new Color(0.3f, 0.9f, 0.4f) : r.Degrees <= 2.0 ? new Color(1f, 0.75f, 0.2f) : new Color(1f, 0.35f, 0.3f);
            if (ghost) m_Color.a = 0.7f;
            Tint(m_Color);
            string mode = r.Mode == LevelMode.Level ? "Level" : "Plumb";
            m_Label.SetAnchorLocal(new Vector3(0f, 0.08f, 0f));
            // Within the ±0.2° acceptance it reads "Level ✓" / "Plumb ✓" (UX W0.9 L1).
            m_Label.Set(r.Degrees <= 0.2 ? $"{mode} ✓" : $"{mode} {Units.FormatAngle(r.Degrees)}", ghost ? 1f : 1.15f);
            if (!m_Claimed) { m_Claimed = true; m_Newest = Time.unscaledTime; WorldLabels.Changed(this); }   // once, not per frame
        }

        public void Hide()
        {
            gameObject.SetActive(false);
            if (m_Claimed) { m_Claimed = false; WorldLabels.Changed(this); }
        }

        // ---------------- the label pool (declutter S10) ----------------

        bool m_Claimed;
        float m_Newest;

        /// Back in view (its tool's root was off in passthrough): the reading counts again.
        void OnEnable()
        {
            if (m_Claimed || m_Label == null || string.IsNullOrEmpty(m_Label.Text)) return;
            m_Claimed = true;
            WorldLabels.Changed(this);
        }

        void OnDisable() { m_Claimed = false; WorldLabels.Remove(this); }

        public void ClaimLabels(List<LabelClaim> claims) =>
            claims.Add(new LabelClaim(LabelClass.Readings, 0, m_Claimed && m_Label != null && gameObject.activeInHierarchy && !string.IsNullOrEmpty(m_Label.Text) ? 1 : 0, m_Newest));

        public void ApplyLabels(LabelGrant[] grants, int first)
        {
            if (!m_Claimed || m_Label == null) return;
            bool show = grants[first].Items > 0;
            if (m_Label.gameObject.activeSelf != show) m_Label.gameObject.SetActive(show);
        }

        public void Pulse(float seconds) => m_PulseUntil = Time.unscaledTime + seconds;

        void Update()
        {
            if (m_PulseUntil <= 0f) return;
            if (Time.unscaledTime > m_PulseUntil) { m_PulseUntil = 0f; Tint(m_Color); return; }
            float k = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 12f);
            Tint(Color.Lerp(m_Color, Color.white, k));
        }

        MeshRenderer m_BarRenderer, m_BubbleRenderer;

        /// Tint bar and bubble (no per-frame allocation: renderers cached, UX W0.11).
        void Tint(Color c)
        {
            if (m_BarRenderer == null) m_BarRenderer = m_Bar.GetComponent<MeshRenderer>();
            if (m_BubbleRenderer == null) m_BubbleRenderer = m_Bubble.GetComponent<MeshRenderer>();
            m_BarRenderer.GetPropertyBlock(m_Block);
            m_Block.SetColor(s_BaseColor, c);
            m_BarRenderer.SetPropertyBlock(m_Block);
            m_BubbleRenderer.GetPropertyBlock(m_Block);
            m_Block.SetColor(s_BaseColor, Color.Lerp(c, Color.white, 0.6f));
            m_BubbleRenderer.SetPropertyBlock(m_Block);
        }
    }
}
