using AirTools.Core;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Structure
{
    /// Snap feedback for the measure tool on a structure package (backend brief §4, R6 §7.3 "feedback"):
    /// a distinct glyph and colour per snap kind at the cursor — corner = square, edge = diamond, midpoint = triangle,
    /// plane = circle, surface = small cross — plus the snapped feature highlighted (the edge's full length, or the
    /// plane's outline), an "on axis" guide while the tape's second point is axis-locked, and a haptic tick whenever
    /// a new feature is acquired. Constant angular size, so it reads the same near and far.
    public class SnapCursor : MonoBehaviour
    {
        public MeasureTool tool;
        public MeasureStyle style = new MeasureStyle();
        [Tooltip("Glyph size in metres at 1 m viewing distance.")]
        public float sizePerMetre = 0.012f;
        public float lineWidthPerMetre = 0.0012f;

        LineRenderer m_Glyph, m_Feature, m_Axis;
        MaterialPropertyBlock m_Block;
        SnapKind m_LastKind = SnapKind.None;
        int m_LastFeature;
        public int Acquisitions { get; private set; }
        public SnapKind ShownKind { get; private set; }

        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly Vector3[] s_Cross = new Vector3[5];   // no per-frame allocation (UX W0.11)

        void Awake()
        {
            m_Block = new MaterialPropertyBlock();
            m_Glyph = MakeLine("Glyph", loop: true);
            m_Feature = MakeLine("Feature", loop: false);
            m_Axis = MakeLine("AxisGuide", loop: false);
        }

        LineRenderer MakeLine(string name, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = loop;
            lr.numCapVertices = 1;
            lr.positionCount = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            if (style.lineMaterial != null) lr.sharedMaterial = style.lineMaterial;
            return lr;
        }

        void LateUpdate()
        {
            var t = tool != null ? tool : Services.Get<MeasureTool>();
            var hit = t != null && t.Equipped ? t.Cursor : null;
            Show(hit);
        }

        public void Show(SurfaceHit? cursor)
        {
            if (m_Glyph == null) Awake();
            if (!cursor.HasValue || AppState.Mode == AppMode.Passthrough)
            {
                Hide();
                return;
            }
            var h = cursor.Value;
            var cam = Camera.main;
            float dist = cam != null ? Vector3.Distance(cam.transform.position, h.point) : 1f;
            float size = sizePerMetre * Mathf.Max(dist, 0.3f);
            float width = lineWidthPerMetre * Mathf.Max(dist, 0.3f);
            var n = h.normal.sqrMagnitude > 0.5f ? h.normal.normalized : Vector3.up;
            var toCam = cam != null ? (cam.transform.position - h.point).normalized : n;
            var u = Vector3.Cross(n, Mathf.Abs(Vector3.Dot(n, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var v = Vector3.Cross(n, u);
            var centre = h.point + n * (0.002f + 0.004f * dist) ;   // lift off the surface a little

            var color = style.ColorFor(h.kind);
            ShownKind = h.kind;
            switch (h.kind)
            {
                case SnapKind.Corner: Poly(m_Glyph, centre, u, v, size, 4, 45f); break;
                case SnapKind.Edge: Poly(m_Glyph, centre, u, v, size, 4, 0f); break;
                case SnapKind.Midpoint: Poly(m_Glyph, centre, u, v, size, 3, 90f); break;
                case SnapKind.Plane: Poly(m_Glyph, centre, u, v, size, 20, 0f); break;
                default:
                    // Surface: a small cross (two strokes drawn as a 4-point non-looped path through the centre).
                    m_Glyph.loop = false;
                    m_Glyph.positionCount = 5;
                    float s = size * 0.4f;
                    s_Cross[0] = centre - u * s; s_Cross[1] = centre + u * s; s_Cross[2] = centre; s_Cross[3] = centre - v * s; s_Cross[4] = centre + v * s;
                    m_Glyph.SetPositions(s_Cross);
                    break;
            }
            if (h.kind != SnapKind.Face && h.kind != SnapKind.None) m_Glyph.loop = true;
            Style(m_Glyph, color, width);

            // Feature highlight from the structure layer.
            m_Feature.positionCount = 0;
            var root = Services.Get<SceneRoot>();
            if (h.structureFeature > 0 && root != null && root.Structure != null && root.StructureSpace != null)
            {
                var space = root.StructureSpace;
                int i = h.structureFeature - 1;
                var layer = root.Structure;
                if ((h.kind == SnapKind.Edge || h.kind == SnapKind.Midpoint) && i < layer.Edges.Length)
                {
                    m_Feature.loop = false;
                    m_Feature.positionCount = 2;
                    m_Feature.SetPosition(0, space.TransformPoint(layer.Edges[i].a));
                    m_Feature.SetPosition(1, space.TransformPoint(layer.Edges[i].b));
                }
                else if (h.kind == SnapKind.Plane && i < layer.Planes.Length && layer.Planes[i].outline3d != null)
                {
                    var o = layer.Planes[i].outline3d;
                    m_Feature.loop = true;
                    m_Feature.positionCount = o.Length;
                    for (int k = 0; k < o.Length; k++) m_Feature.SetPosition(k, space.TransformPoint(o[k]) + n * 0.002f);
                }
                Style(m_Feature, color * new Color(1f, 1f, 1f, 0.7f), width * 0.7f);
            }

            // "On axis" guide while the tape's second point is locked.
            if (Time.frameCount - SnapService.LastAxisLockFrame <= 1)
            {
                var d = SnapService.LastAxisDirection;
                m_Axis.positionCount = 2;
                m_Axis.SetPosition(0, h.point - d * size * 12f);
                m_Axis.SetPosition(1, h.point + d * size * 12f);
                Style(m_Axis, style.edgeColor * new Color(1f, 1f, 1f, 0.6f), width * 0.6f);
            }
            else m_Axis.positionCount = 0;

            // Haptic tick when a new feature is acquired.
            bool feature = h.kind == SnapKind.Corner || h.kind == SnapKind.Edge || h.kind == SnapKind.Midpoint || h.kind == SnapKind.Plane;
            if (feature && (h.kind != m_LastKind || h.structureFeature != m_LastFeature))
            {
                Acquisitions++;
                if (h.kind != SnapKind.Plane) FeedbackEvents.SnapTick(h.kind == SnapKind.Corner);
            }
            m_LastKind = h.kind;
            m_LastFeature = h.structureFeature;
        }

        void Hide()
        {
            m_Glyph.positionCount = 0; m_Feature.positionCount = 0; m_Axis.positionCount = 0;
            m_LastKind = SnapKind.None; m_LastFeature = 0; ShownKind = SnapKind.None;
        }

        static void Poly(LineRenderer lr, Vector3 c, Vector3 u, Vector3 v, float r, int sides, float startDeg)
        {
            lr.loop = true;
            lr.positionCount = sides;
            for (int i = 0; i < sides; i++)
            {
                float a = (startDeg + 360f * i / sides) * Mathf.Deg2Rad;
                lr.SetPosition(i, c + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r);
            }
        }

        void Style(LineRenderer lr, Color c, float width)
        {
            lr.widthMultiplier = width;
            lr.startColor = lr.endColor = c;
            lr.GetPropertyBlock(m_Block);
            m_Block.SetColor(s_BaseColor, c);
            lr.SetPropertyBlock(m_Block);
        }
    }
}
