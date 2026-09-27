using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// The adjust mode's handles on the part: three axis lines from its anchor along the placement frame — Right (danger
    /// red), Up (success green), Out (info blue), the usual x / y / z colours from the theme — and a ring about Up (ink:
    /// what a turn does). Scene-scaled (× SnapService.Scale) so it reads the same on the tabletop; line widths grow with
    /// distance. Moved every frame without allocating (fixed position arrays).
    public class PlacementGizmo : MonoBehaviour
    {
        [Tooltip("Unlit line material (the measure line's); tinted per line with a property block.")]
        public Material lineMaterial;
        [Tooltip("Axis length (scene metres).")]
        public float axisLength = 0.22f;
        [Tooltip("Turn ring radius (scene metres).")]
        public float ringRadius = 0.16f;
        public Transform head;

        public bool Showing { get; private set; }

        LineRenderer m_Right, m_Up, m_Out, m_Ring;
        static readonly Vector3[] s_Axis = new Vector3[2];
        static readonly Vector3[] s_Ring = new Vector3[40];
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        LineRenderer Line(string name, Color c, bool loop, int count)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = loop;
            lr.positionCount = count;
            lr.numCapVertices = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            if (lineMaterial != null) lr.sharedMaterial = lineMaterial;
            lr.startColor = lr.endColor = c;
            var block = new MaterialPropertyBlock();
            lr.GetPropertyBlock(block);
            block.SetColor(s_BaseColor, c);   // SetColor: the token is sRGB, the project Linear
            lr.SetPropertyBlock(block);
            lr.enabled = false;
            return lr;
        }

        void Build()
        {
            var colors = UiTheme.Current.colors;
            m_Right = Line("Right", colors.danger, false, 2);
            m_Up = Line("Up", colors.success, false, 2);
            m_Out = Line("Out", colors.info, false, 2);
            m_Ring = Line("Turn", colors.ink, true, s_Ring.Length);
        }

        /// Draw at `anchor` (world) along the frame's axes (world); hidden with Hide().
        public void Show(Vector3 anchor, Vector3 right, Vector3 up, Vector3 outward)
        {
            if (m_Right == null) Build();
            float scale = Mathf.Max(AirTools.Scene.SnapService.Scale, 1e-4f);
            float dist = head != null ? Vector3.Distance(head.position, anchor) : 1f;
            float width = Mathf.Max(0.002f, 0.0025f * dist);
            Axis(m_Right, anchor, right, axisLength * scale, width);
            Axis(m_Up, anchor, up, axisLength * scale, width);
            Axis(m_Out, anchor, outward, axisLength * scale, width);
            var u = right.normalized; var v = outward.normalized;
            float r = ringRadius * scale;
            for (int i = 0; i < s_Ring.Length; i++)
            {
                float a = i * Mathf.PI * 2f / s_Ring.Length;
                s_Ring[i] = anchor + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r;
            }
            m_Ring.SetPositions(s_Ring);
            m_Ring.widthMultiplier = width * 0.8f;
            if (!m_Ring.enabled) m_Ring.enabled = true;
            Showing = true;
        }

        static void Axis(LineRenderer lr, Vector3 from, Vector3 dir, float length, float width)
        {
            s_Axis[0] = from;
            s_Axis[1] = from + dir.normalized * length;
            lr.SetPositions(s_Axis);
            lr.widthMultiplier = width;
            if (!lr.enabled) lr.enabled = true;
        }

        public void Hide()
        {
            if (!Showing) return;
            Showing = false;
            if (m_Right != null) m_Right.enabled = false;
            if (m_Up != null) m_Up.enabled = false;
            if (m_Out != null) m_Out.enabled = false;
            if (m_Ring != null) m_Ring.enabled = false;
        }
    }
}
