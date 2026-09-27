using System.Collections.Generic;
using AirTools.Core;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Tools
{
    /// Shared look for measurement graphics (assigned by MainSceneBuilder so materials ship in builds).
    [System.Serializable]
    public class MeasureStyle
    {
        public Material lineMaterial;
        public Material markerMaterial;
        public Material labelBackgroundMaterial;
        public Font font;
        public Color lineColor = new Color(1f, 0.78f, 0.1f);
        public Color previewColor = new Color(1f, 1f, 1f, 0.8f);
        public Color faceColor = Color.white;
        public Color edgeColor = new Color(0.3f, 0.9f, 1f);
        public Color cornerColor = new Color(1f, 0.85f, 0.2f);
        public Color midpointColor = new Color(0.2f, 1f, 0.85f);
        public Color planeColor = new Color(0.75f, 0.6f, 1f);
        public float lineWidth = 0.008f;
        public float markerSize = 0.025f;
        [Tooltip("Line width / marker size per metre of viewing distance (keeps them visible far away).")]
        public float lineWidthPerMetre = 0.0025f;
        public float markerSizePerMetre = 0.008f;
        [Tooltip("Label text height in metres when viewed from 2 m; grows with distance so it stays readable.")]
        public float labelHeightAt2m = 0.035f;

        public Color ColorFor(SnapKind k) => k switch
        {
            SnapKind.Corner => cornerColor,
            SnapKind.Edge => edgeColor,
            SnapKind.Midpoint => midpointColor,
            SnapKind.Plane => planeColor,
            _ => faceColor,
        };
    }

    /// How much of a shape's label set the world-label pool gives it (declutter S10, §5.1): the focused shape shows
    /// everything, a saved shape one summary (its length, area or W × H), and past the pool none (its outline stays).
    public enum LabelDetail { Full, Summary, None }

    /// Draws one measurement (placed points, optional preview point) in its parent's local space:
    /// a line / closed polygon, point markers coloured by snap kind, and billboard labels
    /// (distance, or side lengths + corner angles + area), each in ONE unit — the user's (D2, Copy.Len / Area).
    public class MeasureView : MonoBehaviour
    {
        MeasureStyle m_Style;
        LineRenderer m_Line;
        readonly List<Transform> m_Markers = new List<Transform>();
        readonly List<MeasureLabel> m_Labels = new List<MeasureLabel>();
        readonly List<Vector3> m_Outline = new List<Vector3>(8);
        MaterialPropertyBlock m_Block;
        /// How far labels float off the measured surface toward the viewer (metres).
        public const float LabelLift = 0.10f;
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        /// Label texts currently shown, for tests and the agent harness.
        public IReadOnlyList<MeasureLabel> Labels => m_Labels;
        public int ActiveLabelCount { get; private set; }
        public int PointCount { get; private set; }
        /// Whether the outline last shown crosses itself (the live preview's warning, the rail reads the session's own).
        public OutlineCrossing Crossing { get; private set; }
        /// The area label of an outline that crosses itself (UiAssetsBuilder.Charset has every glyph).
        public const string CrossLabel = "! Edges cross";

        public static MeasureView Create(Transform parent, MeasureStyle style, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var view = go.AddComponent<MeasureView>();
            view.Init(style);
            return view;
        }

        void Init(MeasureStyle style)
        {
            m_Style = style ?? new MeasureStyle();
            m_Block = new MaterialPropertyBlock();
            m_Line = gameObject.AddComponent<LineRenderer>();
            m_Line.useWorldSpace = false;
            m_Line.widthMultiplier = m_Style.lineWidth;
            m_Line.numCapVertices = 2;
            m_Line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Line.receiveShadows = false;
            if (m_Style.lineMaterial != null) m_Line.sharedMaterial = m_Style.lineMaterial;
        }

        /// Labels a shape shows in full: a distance 1; a polygon its sides, corner angles and area (2n + 1); a crossed
        /// outline its sides and the warning.
        public static int FullLabelCount(in PointMeasurement m) =>
            m.IsDistance ? 1 : !m.SelfIntersecting ? 2 * m.OutlineCount + 1 : m.OutlineCount + 1;

        /// points: placed + preview (preview is the last one when hasPreview). kinds: one per placed point.
        /// detail (the world-label pool, declutter S10): Full = every side, angle and the area; Summary = the one
        /// label that says what it is (the length, the area, or the crossing warning); None = the outline only.
        public void Show(IReadOnlyList<Vector3> points, IReadOnlyList<SnapKind> kinds, bool hasPreview, bool preview, LabelDetail detail = LabelDetail.Full)
        {
            PointCount = points.Count;
            IsSurvey = false;
            var color = preview ? m_Style.previewColor : m_Style.lineColor;
            m_LineBase = color;
            m_Line.startColor = m_Line.endColor = color;
            // Outline: the measured shape (click order for 3+ points, concave included; the extremes for a distance).
            PointMeasurement? measured = points.Count >= 2 ? MeasureMath.Measure(points) : (PointMeasurement?)null;
            Crossing = measured.HasValue ? measured.Value.Crossing : OutlineCrossing.None;
            if (measured.HasValue)
            {
                var outline = measured.Value.Outline;
                m_Line.loop = outline.Length >= 3;
                m_Line.positionCount = outline.Length;
                for (int i = 0; i < outline.Length; i++) m_Line.SetPosition(i, points[outline[i]]);
            }
            else m_Line.positionCount = 0;
            if (m_Style.lineMaterial != null)
            {
                m_Line.GetPropertyBlock(m_Block);
                m_Block.SetColor(s_BaseColor, color);
                m_Line.SetPropertyBlock(m_Block);
            }

            // Markers
            EnsureMarkers(points.Count);
            for (int i = 0; i < m_Markers.Count; i++)
            {
                bool on = i < points.Count;
                m_Markers[i].gameObject.SetActive(on);
                if (!on) continue;
                m_Markers[i].localPosition = points[i];
                bool isPreviewPoint = hasPreview && i == points.Count - 1;
                var kind = !isPreviewPoint && kinds != null && i < kinds.Count ? kinds[i] : SnapKind.Face;
                var r = m_Markers[i].GetComponent<MeshRenderer>();
                r.GetPropertyBlock(m_Block);
                m_Block.SetColor(s_BaseColor, isPreviewPoint ? m_Style.previewColor : m_Style.ColorFor(kind));
                r.SetPropertyBlock(m_Block);
            }

            // Labels float off the surface toward the viewer so recesses (jambs, sills) don't hide them.
            ActiveLabelCount = 0;
            if (measured.HasValue && detail != LabelDetail.None)
            {
                bool full = detail == LabelDetail.Full;
                var m = measured.Value;
                var hp = m_Outline;   // reused: Show runs every frame for the live preview (UX W0.11)
                hp.Clear();
                foreach (int i in m.Outline) hp.Add(points[i]);
                var sum = Vector3.zero;
                foreach (var q in hp) sum += q;
                var cam = Camera.main;
                var toViewer = cam != null ? transform.InverseTransformPoint(cam.transform.position) - sum / hp.Count : Vector3.back;
                toViewer = toViewer.sqrMagnitude > 1e-8f ? toViewer.normalized : Vector3.back;
                var lift = m.Normal.sqrMagnitude > 0.5f ? (Vector3.Dot(m.Normal, toViewer) >= 0 ? m.Normal : -m.Normal) : toViewer;
                lift *= LabelLift;
                if (m.IsDistance)
                {
                    var mid = (hp[0] + hp[1]) * 0.5f;
                    AddLabel(mid + Vector3.up * 0.04f + lift, AirTools.UI.Copy.Len(m.Distance), large: true);   // D2: one unit
                }
                else
                {
                    int n = hp.Count;
                    var centre = sum / n;
                    var nrm = m.Normal;
                    bool simple = !m.SelfIntersecting;
                    // Inside the outline even when it's an L or a U (their centroid can sit in the notch).
                    var labelAt = MeasureMath.LabelPoint(points, m);
                    // The outline runs counter-clockwise about the normal: side labels go out off each side, angle labels
                    // in along each corner's bisector (an L's 270° corner reads from inside). A crossed outline has no
                    // inside: its side labels just move away from the centre.
                    for (int i = 0; i < n && full; i++)
                    {
                        var a = hp[i]; var b = hp[(i + 1) % n];
                        var mid = (a + b) * 0.5f;
                        var outward = simple ? Vector3.Cross(b - a, nrm) : mid - centre;
                        outward = outward.sqrMagnitude > 1e-8f ? outward.normalized : Vector3.up;
                        AddLabel(mid + outward * 0.06f + lift, AirTools.UI.Copy.Len(m.Sides[i]), large: false);   // D2: one unit
                    }
                    if (simple)
                    {
                        for (int i = 0; i < n && full; i++)
                        {
                            var v = hp[i];
                            var inward = Vector3.Cross(nrm, v - hp[(i + n - 1) % n]).normalized + Vector3.Cross(nrm, hp[(i + 1) % n] - v).normalized;
                            if (inward.sqrMagnitude < 1e-4f) inward = labelAt - v;   // a spike: no bisector
                            float adj = (float)System.Math.Min(m.Sides[i], m.Sides[(i + n - 1) % n]);
                            float d = Mathf.Min(0.15f, 0.3f * adj);
                            AddLabel(v + (inward.sqrMagnitude > 1e-8f ? inward.normalized : Vector3.up) * d + lift, Units.FormatAngle(m.Angles[i]), large: false);
                        }
                        string area = AirTools.UI.Copy.Area(m.Area);
                        if (m.PlanarityError > 0.01) area += $"\n! Points {AirTools.UI.Copy.Gap((float)(m.PlanarityError * 1000.0))} off one surface";
                        AddLabel(labelAt + lift, area, large: true);
                    }
                    // Crossed: no angles, no area. While placing, a crossing only through the closing side stays quiet
                    // (the next points may fix it); a crossing along the placed path is a mistake to undo.
                    else if (!preview || m.Crossing == OutlineCrossing.Path) AddLabel(labelAt + lift, CrossLabel, large: true);
                }
            }
            for (int i = ActiveLabelCount; i < m_Labels.Count; i++) m_Labels[i].gameObject.SetActive(false);
        }

        /// A survey object (B1, UX W1.9 label diet): the outline and its corner markers with ONE compact label
        /// ("10⅜ × 11″" / "262 × 279 mm", Copy.WxH) at the centre — or none when `labelled` is false (the budget went to others). Amber when a
        /// corner didn't snap (unverified); a focused object's label is large.
        public void ShowSurvey(IReadOnlyList<Vector3> points, IReadOnlyList<SnapKind> kinds, string label, bool amber, bool focus, bool labelled)
        {
            Show(points, kinds, hasPreview: false, preview: false, LabelDetail.None);   // its own one label below
            IsSurvey = true;
            // Agent-measured outlines are light (the user's own tapes stay yellow); unverified ones amber (+ ⚠ on the label).
            var color = amber ? AirTools.UI.UiTheme.Current.colors.warning : AirTools.UI.UiTheme.Current.colors.textPrimary;
            m_LineBase = color;
            m_Line.startColor = m_Line.endColor = color;
            if (m_Style.lineMaterial != null)
            {
                m_Line.GetPropertyBlock(m_Block);
                m_Block.SetColor(s_BaseColor, color);
                m_Line.SetPropertyBlock(m_Block);
            }
            ActiveLabelCount = 0;
            if (labelled && points.Count >= 3)
            {
                var m = MeasureMath.Measure(points);
                var centroid = MeasureMath.LabelPoint(points, m);
                var cam = Camera.main;
                var toViewer = cam != null ? transform.InverseTransformPoint(cam.transform.position) - centroid : Vector3.back;
                toViewer = toViewer.sqrMagnitude > 1e-8f ? toViewer.normalized : Vector3.back;
                var lift = m.Normal.sqrMagnitude > 0.5f ? (Vector3.Dot(m.Normal, toViewer) >= 0 ? m.Normal : -m.Normal) : toViewer;
                AddLabel(centroid + lift * (LabelLift * 0.5f), amber ? $"⚠ {label}" : label, large: focus);
            }
            for (int i = ActiveLabelCount; i < m_Labels.Count; i++) m_Labels[i].gameObject.SetActive(false);
        }

        /// Drawn as a survey object (compact label).
        public bool IsSurvey { get; private set; }
        Color m_LineBase = Color.white;

        /// Keep lines and markers visible at building distances: widths grow with distance to the camera.
        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null || PointCount == 0) return;
            var centre = Vector3.zero; int n = 0;
            foreach (var mk in m_Markers)
                if (mk.gameObject.activeSelf) { centre += mk.position; n++; }
            if (n == 0) return;
            float dist = Vector3.Distance(cam.transform.position, centre / n);
            m_Line.widthMultiplier = Mathf.Max(m_Style.lineWidth, dist * m_Style.lineWidthPerMetre);
            float parentScale = transform.lossyScale.x > 1e-4f ? transform.lossyScale.x : 1f;
            float marker = Mathf.Max(m_Style.markerSize, dist * m_Style.markerSizePerMetre) / parentScale;
            foreach (var mk in m_Markers) mk.localScale = Vector3.one * marker;
        }

        float m_PulseUntil;

        /// Flash the line and markers (notebook "show").
        public void Pulse(float seconds) => m_PulseUntil = Time.unscaledTime + seconds;
        public bool IsPulsing => m_PulseUntil > Time.unscaledTime;

        void Update()
        {
            if (m_PulseUntil <= 0f) return;
            bool on = Time.unscaledTime < m_PulseUntil;
            var baseColor = m_LineBase;
            var c = on ? Color.Lerp(baseColor, Color.white, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 12f)) : baseColor;
            m_Line.startColor = m_Line.endColor = c;
            if (m_Style.lineMaterial != null)
            {
                m_Line.GetPropertyBlock(m_Block);
                m_Block.SetColor(s_BaseColor, c);
                m_Line.SetPropertyBlock(m_Block);
            }
            if (!on) m_PulseUntil = 0f;
        }

        public void Hide()
        {
            PointCount = 0;
            ActiveLabelCount = 0;
            m_Line.positionCount = 0;
            foreach (var mk in m_Markers) mk.gameObject.SetActive(false);
            foreach (var l in m_Labels) l.gameObject.SetActive(false);
        }

        void EnsureMarkers(int count)
        {
            while (m_Markers.Count < count)
            {
                var go = new GameObject($"Point{m_Markers.Count}");
                go.transform.SetParent(transform, false);
                go.transform.localScale = Vector3.one * m_Style.markerSize;
                go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
                var r = go.AddComponent<MeshRenderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (m_Style.markerMaterial != null) r.sharedMaterial = m_Style.markerMaterial;
                m_Markers.Add(go.transform);
            }
        }

        void AddLabel(Vector3 localPos, string text, bool large)
        {
            if (ActiveLabelCount >= m_Labels.Count)
                m_Labels.Add(MeasureLabel.Create(transform, m_Style, $"Label{m_Labels.Count}"));
            var label = m_Labels[ActiveLabelCount++];
            label.gameObject.SetActive(true);
            label.Set(text, large ? 1.25f : 1f);
            label.SetAnchorLocal(localPos);
        }
    }
}
