using System.Collections.Generic;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Tools
{
    /// A true-size extension ladder (presence.md S4): base and fly sections of glass rails with rungs every 0.30 m,
    /// one procedural mesh (≈ 250 vertices, < 400 triangles), tinted by the verdict (UiTheme success / warning /
    /// danger), with a label on the air side a third of the way up. Everything in the parent's space (SceneRoot);
    /// the mesh is rebuilt only when the length or section split changes, into reused buffers.
    /// The label is a P5 reading in WorldLabels' pool (declutter S10): over the pool the tinted ladder says it alone.
    public class LadderView : MonoBehaviour, IWorldLabelSource
    {
        public const float Width = 0.45f, RailWidth = 0.045f, RailDepth = 0.07f, RungSize = 0.032f;

        MeshFilter m_Filter;
        MeshRenderer m_Renderer;
        Mesh m_Mesh;
        MeasureLabel m_Label;
        MaterialPropertyBlock m_Block;
        Color m_Color;
        float m_PulseUntil;
        float m_BuiltRail = -1f, m_BuiltBase = -1f;
        readonly List<Vector3> m_V = new List<Vector3>(512);
        readonly List<int> m_T = new List<int>(1024);
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        public string LabelText => m_Label != null ? m_Label.Text : null;
        public Color CurrentColor => m_Color;
        public int TriangleCount => m_T.Count / 3;
        /// Foot and top of the rails, parent space (for the harness / captures).
        public Vector3 Foot { get; private set; }
        public Vector3 RailTop { get; private set; }

        public static LadderView Create(Transform parent, Material material, MeasureStyle style, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<LadderView>();
            v.m_Filter = go.AddComponent<MeshFilter>();
            v.m_Renderer = go.AddComponent<MeshRenderer>();
            v.m_Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            v.m_Renderer.receiveShadows = false;
            if (material != null) v.m_Renderer.sharedMaterial = material;
            v.m_Mesh = new Mesh { name = "Ladder" };
            v.m_Mesh.MarkDynamic();
            v.m_Filter.sharedMesh = v.m_Mesh;
            v.m_Block = new MaterialPropertyBlock();
            v.m_Label = MeasureLabel.Create(go.transform, style ?? new MeasureStyle(), "Label");
            return v;
        }

        public static Color ColorFor(LadderVerdict v)
        {
            var c = UiTheme.Current.colors;
            return v == LadderVerdict.Green ? c.success : v == LadderVerdict.Amber ? c.warning : c.danger;
        }

        /// foot / contact: where the rails meet the ground and the support (parent space, +Y up); outward: off the
        /// support face; railLength: foot to the top of the rails; overlapM: how much the fly overlaps the base.
        public void Show(Vector3 foot, Vector3 contact, Vector3 outward, float railLength, float overlapM, LadderVerdict verdict, string label, bool ghost)
        {
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            var axis = contact - foot;
            float contactLen = axis.magnitude;
            if (contactLen < 1e-4f) { Hide(); return; }
            axis /= contactLen;
            var side = Vector3.Cross(Vector3.up, LadderMath.Horizontal(outward));
            if (side.sqrMagnitude < 1e-8f) side = Vector3.right;
            var off = Vector3.Cross(axis, side.normalized).normalized;
            if (Vector3.Dot(off, outward) < 0f) off = -off;
            transform.localPosition = foot;
            transform.localRotation = Quaternion.LookRotation(axis, off);   // z up the rails, y off the ladder's face
            Foot = foot;
            RailTop = foot + axis * railLength;

            float baseLen = Mathf.Min(railLength, (railLength + overlapM) * 0.5f);
            if (Mathf.Abs(railLength - m_BuiltRail) > 0.005f || Mathf.Abs(baseLen - m_BuiltBase) > 0.005f) Build(railLength, baseLen);

            m_Color = ColorFor(verdict);
            m_Color.a = ghost ? 0.35f : 0.62f;
            Tint(m_Color);

            m_Label.SetAnchorLocal(new Vector3(0f, 0.25f, contactLen * 0.35f));
            m_Label.Set(label ?? "", ghost ? 0.9f : 1.1f);
            if (!m_Claimed) { m_Claimed = true; m_Newest = Time.unscaledTime; WorldLabels.Changed(this); }   // once, not per frame
        }

        public void Hide()
        {
            if (gameObject.activeSelf) gameObject.SetActive(false);
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
            Tint(Color.Lerp(m_Color, Color.white, k * 0.6f));
        }

        void Tint(Color c)
        {
            m_Renderer.GetPropertyBlock(m_Block);
            m_Block.SetColor(s_BaseColor, c);
            m_Renderer.SetPropertyBlock(m_Block);
        }

        /// Base section (0 → baseLen) against the support side, fly section (railLength − baseLen → railLength) in
        /// front of it; rungs every 0.30 m on each.
        void Build(float railLength, float baseLen)
        {
            m_BuiltRail = railLength;
            m_BuiltBase = baseLen;
            m_V.Clear();
            m_T.Clear();
            float xr = Width * 0.5f - RailWidth * 0.5f;
            float flyStart = railLength - baseLen;
            Section(0f, baseLen, 0f, xr);
            if (flyStart > 0.01f) Section(flyStart, railLength, RailDepth, xr - RailWidth * 0.6f);
            m_Mesh.Clear();
            m_Mesh.SetVertices(m_V);
            m_Mesh.SetTriangles(m_T, 0);
            m_Mesh.RecalculateBounds();
        }

        void Section(float z0, float z1, float y0, float xr)
        {
            Box(new Vector3(-xr - RailWidth * 0.5f, y0, z0), new Vector3(-xr + RailWidth * 0.5f, y0 + RailDepth, z1));
            Box(new Vector3(xr - RailWidth * 0.5f, y0, z0), new Vector3(xr + RailWidth * 0.5f, y0 + RailDepth, z1));
            float ym = y0 + RailDepth * 0.5f, h = RungSize * 0.5f, xi = xr - RailWidth * 0.5f;
            int n = LadderMath.RungCount(z1 - z0);
            for (int i = 1; i <= n; i++)
            {
                float z = z0 + i * (float)LadderMath.RungSpacingM;
                if (z > z1 - 0.05f) break;
                Box(new Vector3(-xi, ym - h, z - h), new Vector3(xi, ym + h, z + h));
            }
        }

        /// An axis-aligned box: 8 shared vertices, 12 triangles (unlit; no normals needed).
        void Box(Vector3 mn, Vector3 mx)
        {
            int i = m_V.Count;
            m_V.Add(new Vector3(mn.x, mn.y, mn.z)); m_V.Add(new Vector3(mx.x, mn.y, mn.z));
            m_V.Add(new Vector3(mx.x, mx.y, mn.z)); m_V.Add(new Vector3(mn.x, mx.y, mn.z));
            m_V.Add(new Vector3(mn.x, mn.y, mx.z)); m_V.Add(new Vector3(mx.x, mn.y, mx.z));
            m_V.Add(new Vector3(mx.x, mx.y, mx.z)); m_V.Add(new Vector3(mn.x, mx.y, mx.z));
            // Clockwise seen from outside (Unity front faces).
            Quad(i + 0, i + 3, i + 2, i + 1);   // −z
            Quad(i + 4, i + 5, i + 6, i + 7);   // +z
            Quad(i + 0, i + 1, i + 5, i + 4);   // −y
            Quad(i + 3, i + 7, i + 6, i + 2);   // +y
            Quad(i + 0, i + 4, i + 7, i + 3);   // −x
            Quad(i + 1, i + 2, i + 6, i + 5);   // +x
        }

        void Quad(int a, int b, int c, int d)
        {
            m_T.Add(a); m_T.Add(b); m_T.Add(c);
            m_T.Add(a); m_T.Add(c); m_T.Add(d);
        }

        void OnDestroy()
        {
            if (m_Mesh == null) return;
            if (Application.isPlaying) Destroy(m_Mesh); else DestroyImmediate(m_Mesh);
        }
    }
}
