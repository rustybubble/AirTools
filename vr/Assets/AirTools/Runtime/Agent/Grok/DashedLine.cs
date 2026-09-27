using System.Collections.Generic;
using AirTools.Tools;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// A dashed line in its parent's space (the scene content) drawn like the measure lines: camera-facing ribbons in the
    /// measure line material, a core over a dark casing (1.8× wide, the UX annotation style) so it reads on brick, sky
    /// and white cabinets, pulled a little toward the viewer so the surface it lies on doesn't cut it. The width is
    /// `nominalWidth` scene metres (1 cm for a plan) or a constant angular width far away, whichever is wider. The dash
    /// polylines are cut once (Dashes.Split); the ribbons are rebuilt each frame into reused buffers.
    public class DashedLine : MonoBehaviour
    {
        [Tooltip("Line width in scene metres at full scale (the plan's 1 cm).")]
        public float nominalWidth = 0.01f;
        [Tooltip("Minimum width per metre of viewing distance (keeps a flight leg 40 m away visible).")]
        public float widthPerMetre = 0.0015f;
        public float casingFactor = 1.8f;

        public int DashCount => m_Dashes.Count;
        public float LengthLocal { get; private set; }
        public Color CoreColor { get; private set; }

        readonly List<List<Vector3>> m_Dashes = new List<List<Vector3>>();
        MeshFilter m_CoreFilter, m_CasingFilter;
        MeshRenderer m_Core, m_Casing;
        Mesh m_CoreMesh, m_CasingMesh;
        readonly List<Vector3> m_V = new List<Vector3>(512);
        readonly List<int> m_T = new List<int>(1024);
        MaterialPropertyBlock m_Block;
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        public static DashedLine Create(Transform parent, string name, MeasureStyle style)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<DashedLine>();
            line.m_Block = new MaterialPropertyBlock();
            line.m_Casing = line.Part("Casing", style, out line.m_CasingFilter, out line.m_CasingMesh);
            line.m_Core = line.Part("Core", style, out line.m_CoreFilter, out line.m_CoreMesh);
            return line;
        }

        MeshRenderer Part(string name, MeasureStyle style, out MeshFilter filter, out Mesh mesh)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            filter = go.AddComponent<MeshFilter>();
            mesh = new Mesh { name = $"Dashed{name}" };
            mesh.MarkDynamic();
            filter.sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (style != null && style.lineMaterial != null) r.sharedMaterial = style.lineMaterial;
            return r;
        }

        /// The polyline (parent space), closed or not, its dash pattern and colour (a theme role's colour).
        public void Set(IReadOnlyList<Vector3> points, bool closed, float dash, float gap, Color core)
        {
            LengthLocal = Dashes.Length(points, closed);
            Dashes.Split(points, closed, dash, gap, m_Dashes);
            CoreColor = core;
            Tint(m_Core, core);
            var casing = UiTheme.Current.colors.background;
            casing.a = 1f;
            Tint(m_Casing, casing);
            Rebuild();
        }

        void Tint(MeshRenderer r, Color c)
        {
            if (r == null) return;
            if (m_Block == null) m_Block = new MaterialPropertyBlock();
            r.GetPropertyBlock(m_Block);
            m_Block.SetColor(s_BaseColor, c);
            r.SetPropertyBlock(m_Block);
        }

        void LateUpdate() => Rebuild();

        void Rebuild()
        {
            var cam = Camera.main;
            if (m_Dashes.Count == 0 || m_CoreMesh == null) { if (m_CoreMesh != null) { m_CoreMesh.Clear(); m_CasingMesh.Clear(); } return; }
            float scale = Mathf.Max(transform.lossyScale.x, 1e-5f);
            var camLocal = cam != null ? transform.InverseTransformPoint(cam.transform.position) : new Vector3(0f, 0f, -10f);
            // Width from the distance to the line's first point (lines are short, or far enough for this to hold).
            float dist = cam != null ? Vector3.Distance(cam.transform.position, transform.TransformPoint(m_Dashes[m_Dashes.Count / 2][0])) : 2f;
            float worldWidth = Mathf.Max(nominalWidth * scale, dist * widthPerMetre);
            float w = worldWidth / scale;
            Ribbon(m_CoreMesh, camLocal, w * 0.5f, w * 1.6f);
            Ribbon(m_CasingMesh, camLocal, w * casingFactor * 0.5f, w * 0.8f);
        }

        /// Camera-facing strips through every dash, `half` wide on each side, moved `pull` toward the viewer.
        void Ribbon(Mesh mesh, Vector3 camLocal, float half, float pull)
        {
            m_V.Clear();
            m_T.Clear();
            foreach (var dash in m_Dashes)
            {
                int start = m_V.Count;
                for (int i = 0; i < dash.Count; i++)
                {
                    var p = dash[i];
                    var tangent = (i < dash.Count - 1 ? dash[i + 1] - p : p - dash[i - 1]);
                    if (i > 0 && i < dash.Count - 1) tangent = dash[i + 1] - dash[i - 1];
                    var view = camLocal - p;
                    float vd = view.magnitude;
                    var toCam = vd > 1e-6f ? view / vd : Vector3.up;
                    var side = Vector3.Cross(tangent, toCam);
                    side = side.sqrMagnitude > 1e-12f ? side.normalized * half : Vector3.zero;
                    var q = p + toCam * Mathf.Min(pull, vd * 0.5f);
                    m_V.Add(q - side);
                    m_V.Add(q + side);
                }
                for (int i = 0; i < dash.Count - 1; i++)
                {
                    int a = start + i * 2;
                    m_T.Add(a); m_T.Add(a + 1); m_T.Add(a + 3);
                    m_T.Add(a); m_T.Add(a + 3); m_T.Add(a + 2);
                    // Both faces (the view may flip the strip's winding).
                    m_T.Add(a); m_T.Add(a + 3); m_T.Add(a + 1);
                    m_T.Add(a); m_T.Add(a + 2); m_T.Add(a + 3);
                }
            }
            mesh.Clear();
            mesh.SetVertices(m_V);
            mesh.SetTriangles(m_T, 0);
            mesh.RecalculateBounds();
        }

        void OnDestroy()
        {
            GrokObjects.Destroy(m_CoreMesh);
            GrokObjects.Destroy(m_CasingMesh);
        }
    }
}
