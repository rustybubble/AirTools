using System.Collections.Generic;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Tools
{
    /// A billboard value label: TMP text (Medium, tabular figures) on a glass pill (GlassRegular; ElevatedSolid in
    /// high contrast). Faces the camera and keeps a constant angular size
    /// (sized for readability at 2 m, never smaller than that when closer). Owners set an anchor; every frame all
    /// visible labels are laid out together (LabelLayout) so they never overlap on screen — a label pushed off its
    /// anchor gets a thin leader line back to it.
    [DefaultExecutionOrder(500)]
    public class MeasureLabel : MonoBehaviour
    {
        public string Text => m_Raw;
        /// Higher keeps its place when labels collide (default: 1, large labels 2; part callouts set 3).
        public int Priority = 1;
        /// World-space offset the layout currently applies (for tests / the harness).
        public Vector3 LayoutOffset => m_Offset;

        TextMeshPro m_Text;
        GlassSurface m_Plate;
        /// Digit runs render monospaced so live readings don't jitter.
        public bool tabular = true;
        string m_Raw;
        LineRenderer m_Leader;
        MeasureStyle m_Style;
        float m_Scale = 1f;
        Vector3 m_AnchorLocal;
        Vector3 m_Offset, m_TargetOffset;
        bool m_Placed;
        int m_Order;

        static int s_NextOrder;
        static readonly List<MeasureLabel> s_Active = new List<MeasureLabel>();
        static readonly List<MeasureLabel> s_Visible = new List<MeasureLabel>();
        static readonly List<LabelBox> s_Boxes = new List<LabelBox>();
        static float[] s_Shifts = new float[64];
        static int s_SolvedFrame = -1;

        public static MeasureLabel Create(Transform parent, MeasureStyle style, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<MeasureLabel>();
            label.m_Style = style;
            label.m_Order = s_NextOrder++;

            // One text line ≈ 0.1 local units at unit scale; Orient() scales the label to its angular size.
            label.m_Text = UiText.Create(go.transform, "Text", "", TypeRole.Label, 1f, localPos: new Vector3(0f, 0f, -0.004f));
            label.m_Text.fontSize = UiText.FontSizeFor(0.1f, label.m_Text.font);
            // Overlay: a label facing you at an angle would otherwise swing half into the wall it annotates.
            if (UiTheme.Current.type.annotation != null) { label.m_Text.font = UiTheme.Current.FontAsset(Weight.Medium); label.m_Text.fontSharedMaterial = UiTheme.Current.type.annotation; }
            label.m_Text.rectTransform.sizeDelta = new Vector2(4f, 0.12f);
            label.m_Plate = GlassSurface.Create(go.transform, "Plate", new Vector2(0.3f, 0.13f), GlassTier.GlassRegular, RadiusRole.Pill);
            label.m_Plate.overlay = true;
            // World annotations sort by render queue under all UI (UI text uses a higher sorting layer).
            label.m_Text.GetComponent<MeshRenderer>().sortingOrder = 0;
            label.m_Plate.rimWidthOverride = 0.006f;
            label.m_Plate.Rebuild();
            return label;
        }

        void OnEnable() { if (!s_Active.Contains(this)) s_Active.Add(this); }
        void OnDisable() { s_Active.Remove(this); m_Placed = false; }

        public void Set(string text, float scale)
        {
            if (m_Raw != text)
            {
                m_Raw = text;
                m_Text.text = tabular ? UiText.Tabular(text) : text;
                m_PlateDirty = true;
            }
            m_Scale = scale;
            if (Priority < 3) Priority = scale > 1.01f ? 2 : 1;
            Orient();
        }

        /// Where the label wants to be, in its parent's space.
        public void SetAnchorLocal(Vector3 local)
        {
            m_AnchorLocal = local;
            ApplyPosition();
        }

        public void SetAnchorWorld(Vector3 world) =>
            SetAnchorLocal(transform.parent != null ? transform.parent.InverseTransformPoint(world) : world);

        public Vector3 AnchorWorld => transform.parent != null ? transform.parent.TransformPoint(m_AnchorLocal) : m_AnchorLocal;

        void ApplyPosition() => transform.position = AnchorWorld + m_Offset;

        bool m_PlateDirty = true;

        /// Fit the glass pill to the text's rendered bounds.
        void FitPlate()
        {
            if (m_Plate == null || !m_PlateDirty) return;
            m_Text.ForceMeshUpdate();
            var b = m_Text.textBounds;
            if (b.size.x <= 0f) return;
            var size = new Vector2(b.size.x + 0.07f, b.size.y + 0.05f);
            m_Plate.transform.localPosition = new Vector3(b.center.x, b.center.y, 0f);
            m_Plate.radiusOverride = Mathf.Min(size.y * 0.5f, 0.07f);
            m_Plate.SetSize(size);
            m_PlateDirty = false;
        }

        /// Label size in world metres (the fitted pill).
        Vector2 WorldSize()
        {
            float s = transform.lossyScale.x;
            if (m_Plate != null) return m_Plate.size * s;
            return new Vector2(0.3f, 0.13f) * s;
        }

        void LateUpdate()
        {
            if (s_SolvedFrame != Time.frameCount) SolveAll();
        }

        /// Lay out every visible label together: orient/scale, then resolve screen overlaps, then position.
        static void SolveAll()
        {
            s_SolvedFrame = Time.frameCount;
            var cam = Camera.main;
            s_Visible.Clear();
            s_Boxes.Clear();
            foreach (var l in s_Active)
            {
                if (l == null || !l.isActiveAndEnabled) continue;
                l.FitPlate();
                l.Orient();
                if (cam == null || string.IsNullOrEmpty(l.Text)) { l.m_TargetOffset = Vector3.zero; continue; }
                var ct = cam.transform;
                var p = ct.InverseTransformPoint(l.AnchorWorld);
                if (p.z < 0.1f) { l.m_TargetOffset = Vector3.zero; continue; }
                // Project the pill's corners (placed at the anchor) so off-axis labels, which a wide field of view
                // stretches, get their true on-screen extent.
                var size = l.WorldSize();
                var right = l.transform.right * (size.x * 0.5f);
                var up = l.transform.up * (size.y * 0.5f);
                var centre = l.AnchorWorld + (l.m_Plate != null ? l.transform.TransformVector(l.m_Plate.transform.localPosition) : Vector3.zero);
                float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
                bool behind = false;
                for (int c = 0; c < 4; c++)
                {
                    var w = centre + ((c & 1) == 0 ? -right : right) + ((c & 2) == 0 ? -up : up);
                    var q = ct.InverseTransformPoint(w);
                    if (q.z < 0.05f) { behind = true; break; }
                    float u = q.x / q.z, v = q.y / q.z;
                    uMin = Mathf.Min(uMin, u); uMax = Mathf.Max(uMax, u); vMin = Mathf.Min(vMin, v); vMax = Mathf.Max(vMax, v);
                }
                if (behind) { l.m_TargetOffset = Vector3.zero; continue; }
                s_Visible.Add(l);
                s_Boxes.Add(new LabelBox
                {
                    U = (uMin + uMax) * 0.5f, V = (vMin + vMax) * 0.5f, HalfW = (uMax - uMin) * 0.5f, HalfH = (vMax - vMin) * 0.5f,
                    Priority = l.Priority, Order = l.m_Order,
                });
            }
            if (s_Shifts.Length < s_Boxes.Count) s_Shifts = new float[s_Boxes.Count * 2];
            if (s_Boxes.Count > 0) LabelLayout.Solve(s_Boxes, s_Shifts);
            foreach (var l in s_Active) if (l != null) l.m_Decluttered = false;
            for (int i = 0; i < s_Visible.Count; i++)
            {
                var l = s_Visible[i];
                if (float.IsNaN(s_Shifts[i])) { l.m_Decluttered = true; continue; }
                float depth = cam.transform.InverseTransformPoint(l.AnchorWorld).z;
                l.m_TargetOffset = cam.transform.up * (s_Shifts[i] * depth);
            }
            float k = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
            foreach (var l in s_Active)
            {
                if (l == null || !l.isActiveAndEnabled) continue;
                l.m_Offset = l.m_Placed ? Vector3.Lerp(l.m_Offset, l.m_TargetOffset, k) : l.m_TargetOffset;
                l.m_Placed = true;
                l.ApplyPosition();
                l.SetShown(!l.m_Decluttered);
                l.UpdateLeader(cam);
            }
        }

        bool m_Decluttered, m_Shown = true;

        /// Crowded out by higher-priority labels (hidden until there is room, e.g. when you move closer).
        public bool Decluttered => m_Decluttered;

        void SetShown(bool on)
        {
            if (m_Shown == on) return;
            m_Shown = on;
            if (m_Text != null) m_Text.enabled = on;
            if (m_Plate != null) m_Plate.GetComponent<MeshRenderer>().enabled = on;
        }

        /// Leader line from the anchor to the label when the layout moved it off its anchor.
        void UpdateLeader(Camera cam)
        {
            float h = WorldSize().y;
            bool show = m_Shown && cam != null && m_Offset.magnitude > h * 0.6f && m_Style != null && m_Style.lineMaterial != null;
            if (!show) { if (m_Leader != null) m_Leader.enabled = false; return; }
            if (m_Leader == null)
            {
                var go = new GameObject("Leader");
                go.transform.SetParent(transform, false);
                m_Leader = go.AddComponent<LineRenderer>();
                m_Leader.useWorldSpace = true;
                m_Leader.positionCount = 2;
                m_Leader.sharedMaterial = m_Style.lineMaterial;
                m_Leader.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_Leader.receiveShadows = false;
                var block = new MaterialPropertyBlock();
                block.SetColor(Shader.PropertyToID("_BaseColor"), new Color(0.85f, 0.85f, 0.9f));
                m_Leader.SetPropertyBlock(block);
            }
            m_Leader.enabled = true;
            var anchor = AnchorWorld;
            var end = transform.position - m_Offset.normalized * (h * 0.5f);
            m_Leader.SetPosition(0, anchor);
            m_Leader.SetPosition(1, end);
            m_Leader.widthMultiplier = Mathf.Max(0.001f, Vector3.Distance(cam.transform.position, anchor) * 0.0012f);
        }

        void Orient()
        {
            var cam = Camera.main;
            if (cam == null || m_Style == null) return;
            var toCam = transform.position - cam.transform.position;
            float dist = toCam.magnitude;
            if (dist < 1e-4f) return;
            // Gravity-aligned (UX W0.11): labels stay upright when you tilt your head; only looking almost straight
            // down / up falls back to the camera's up.
            var dir = toCam / dist;
            transform.rotation = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) < 0.97f ? Vector3.up : cam.transform.up);
            // Text line ≈ 0.1 units tall at unit scale; world height = labelHeightAt2m * max(dist, 2) / 2.
            float worldHeight = m_Style.labelHeightAt2m * Mathf.Max(dist, 2f) / 2f * m_Scale;
            float parentScale = transform.parent != null ? transform.parent.lossyScale.x : 1f;
            transform.localScale = Vector3.one * (worldHeight / 0.1f / Mathf.Max(parentScale, 1e-4f));
        }
    }
}
