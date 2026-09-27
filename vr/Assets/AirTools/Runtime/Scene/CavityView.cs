using System.Collections.Generic;
using AirTools.Core;
using AirTools.Tools;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Scene
{
    /// The box a removed scene part leaves behind (SceneParts): its outline (12 thin edges in the info colour —
    /// estimated, not measured: the tape's ink stays for what was measured) and one label, "W × H × D · estimated" in
    /// the user's units (Copy.Cavity; scene metres × the calibration, so it agrees with the tape). The label is a claim in
    /// WorldLabels' one pool of 12 at the task priority (declutter §5, P2); over the pool it drops and the outline stays.
    /// Lives under the scene content in package space, so it follows the calibration and the tabletop scale.
    public class CavityView : MonoBehaviour, IWorldLabelSource
    {
        public ScenePartComponent Component { get; private set; }
        public CavityBox Box { get; private set; }
        /// What the label says (raw, before tabular figures).
        public string LabelText => m_Label != null ? m_Label.Text : "";
        /// The label is granted a place in the pool and drawn.
        public bool LabelShown => m_Label != null && m_Label.gameObject.activeSelf && gameObject.activeInHierarchy;
        public bool Shown => gameObject.activeSelf;

        readonly Transform[] m_Edges = new Transform[12];
        Bounds m_Local;
        MeasureLabel m_Label;
        MaterialPropertyBlock m_Block;
        float m_Newest;
        bool m_Claimed;
        SceneRoot m_Root;
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        /// Builds the view hidden, under `space` (the scene content: package coordinates).
        public static CavityView Create(Transform space, ScenePartComponent component, CavityBox box, MeasureStyle style, SceneRoot root)
        {
            var go = new GameObject($"CavityView_{component.id}");
            go.SetActive(false);
            go.transform.SetParent(space, false);
            // The view's own frame is the cavity's: +Z out of the opening, +Y up (so the box is axis-aligned in it).
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.LookRotation(box.D, box.U);
            var v = go.AddComponent<CavityView>();
            v.Component = component;
            v.Box = box;
            v.m_Root = root;
            v.m_Block = new MaterialPropertyBlock();
            style ??= new MeasureStyle();
            var x = Vector3.Cross(box.U, box.D);   // the view's local +X in package space
            bool any = false;
            for (int i = 0; i < 8; i++)
            {
                var p = box.Corner(i);
                var local = new Vector3(Vector3.Dot(p, x), Vector3.Dot(p, box.U), Vector3.Dot(p, box.D));
                if (!any) { v.m_Local = new Bounds(local, Vector3.zero); any = true; } else v.m_Local.Encapsulate(local);
            }
            for (int i = 0; i < 12; i++)
            {
                var e = new GameObject($"Edge{i}");
                e.transform.SetParent(go.transform, false);
                e.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                var r = e.AddComponent<MeshRenderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (style.markerMaterial != null) r.sharedMaterial = style.markerMaterial;
                v.m_Edges[i] = e.transform;
            }
            v.m_Label = MeasureLabel.Create(go.transform, style, "Label");
            v.m_Label.Priority = 2;
            v.m_Label.tabular = true;
            v.Layout(0.004f);
            v.SetColor(UiTheme.Current.colors.info);
            v.Relabel();
            return v;
        }

        /// Show (part out) or hide (part back). Showing makes it the newest task label.
        public void Show(bool on)
        {
            if (on == gameObject.activeSelf) return;
            if (on) m_Newest = Time.unscaledTime;
            gameObject.SetActive(on);
            if (on) { Relabel(); WorldLabels.Changed(this); }
        }

        /// "437 × 591 × 445 mm · estimated" for the current units and calibration.
        public static string Text(CavityBox box, float calibration) =>
            Copy.Cavity(box.Width * calibration, box.Height * calibration, box.Depth * calibration);

        /// The size in scene metres (× the calibration): what the label says and what a tape across it should read.
        public Vector3 SizeScene => Box.Size * Calibration;

        float Calibration => m_Root != null && m_Root.Content != null ? m_Root.Calibration : 1f;

        /// Re-word for a units switch or a new calibration.
        public void Relabel()
        {
            if (m_Label != null) m_Label.Set(Text(Box, Calibration), 1f);
        }

        void OnDisable() => WorldLabels.Remove(this);

        // ---------------- the label pool (declutter S10, P2 task) ----------------

        public void ClaimLabels(List<LabelClaim> claims)
        {
            m_Claimed = m_Label != null && gameObject.activeInHierarchy && !string.IsNullOrEmpty(m_Label.Text);
            claims.Add(new LabelClaim(LabelClass.Task, 0, m_Claimed ? 1 : 0, m_Newest));
        }

        public void ApplyLabels(LabelGrant[] grants, int first)
        {
            if (m_Label == null || !m_Claimed) return;
            bool show = grants[first].Items > 0;
            if (m_Label.gameObject.activeSelf != show) m_Label.gameObject.SetActive(show);
        }

        // ---------------- drawing ----------------

        void SetColor(Color c)
        {
            foreach (var e in m_Edges)
            {
                if (e == null) continue;
                var r = e.GetComponent<MeshRenderer>();
                r.GetPropertyBlock(m_Block);
                m_Block.SetColor(s_BaseColor, c);
                r.SetPropertyBlock(m_Block);
            }
        }

        void Layout(float thickness)
        {
            var c = m_Local.center; var e = m_Local.extents;
            int i = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                for (int k = 0; k < 4; k++)
                {
                    var p = c;
                    p[a1] += (k & 1) == 0 ? -e[a1] : e[a1];
                    p[a2] += (k & 2) == 0 ? -e[a2] : e[a2];
                    var s = Vector3.one * thickness;
                    s[axis] = m_Local.size[axis] + thickness;
                    m_Edges[i].localPosition = p;
                    m_Edges[i].localScale = s;
                    i++;
                }
            }
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null || m_Edges[0] == null || m_Label == null) return;
            var centre = transform.TransformPoint(m_Local.center);
            float dist = Vector3.Distance(cam.transform.position, centre);
            float scale = Mathf.Max(transform.lossyScale.x, 1e-4f);
            Layout(Mathf.Max(0.003f, dist * 0.0015f) / scale);
            // The label above the opening's top front edge, pulled toward the viewer.
            var topFront = transform.TransformPoint(new Vector3(m_Local.center.x, m_Local.max.y, m_Local.max.z));
            var toCam = (cam.transform.position - topFront).normalized;
            m_Label.SetAnchorWorld(topFront + Vector3.up * (0.04f + dist * 0.02f) * Mathf.Min(1f, scale) + toCam * 0.06f);
        }
    }
}
