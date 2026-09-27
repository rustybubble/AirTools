using System.Collections.Generic;
using AirTools.Core;
using AirTools.Scene;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// While a part is in hand over a scanned scene: the fitted structure edges around it (cyan, clipped to a sphere
    /// around the part), the part's true-size box in its fit colour, and in red the edges its box cuts through and the
    /// scan contacts deeper than the scan's noise. A point of reference for why a spot fits or doesn't. Radii are in
    /// scene metres (× SnapService.Scale), so it works on the tabletop too. Hidden on the built-in facade.
    public class PlacementGuides : MonoBehaviour
    {
        public PartTool tool;
        [Tooltip("Unlit material template (URP/Unlit); tinted per kind.")]
        public Material material;
        [Tooltip("Show structure edges within this distance of the part (scene metres).")]
        public float radius = 0.35f;
        [Tooltip("Rebuild the edges at most this often (s); the box follows every frame.")]
        public float refreshInterval = 0.08f;
        public Color edgeColor = new Color(0.25f, 0.9f, 1f, 1f);
        [Tooltip("Half-size of a contact cross (scene metres).")]
        public float contactSize = 0.015f;

        public bool Showing => m_Root != null && m_Root.activeSelf;
        public int EdgesShown { get; private set; }
        public int HitsShown { get; private set; }

        GameObject m_Root;
        /// modelview: the guides' lines (Model view hides them while the scene is on the table).
        public Transform AnnotationRoot => m_Root != null ? m_Root.transform : null;
        Mesh m_EdgeMesh, m_HitMesh, m_BoxMesh;
        Material m_BoxMat;
        float m_Next;
        readonly List<Vector3> m_EdgePts = new List<Vector3>(256), m_HitPts = new List<Vector3>(64), m_BoxPts = new List<Vector3>(24);
        readonly List<int> m_Index = new List<int>(256);
        static readonly Collider[] s_Hits = new Collider[32];
        readonly List<Object> m_Owned = new List<Object>();

        void OnEnable() => Services.Register(this);
        void OnDisable() { Services.Unregister(this); if (m_Root != null) m_Root.SetActive(false); }

        void OnDestroy()
        {
            if (m_Root != null) Destroy(m_Root);
            foreach (var o in m_Owned) if (o != null) Destroy(o);
            m_Owned.Clear();
        }

        void Build()
        {
            m_Root = new GameObject("PlacementGuides");   // world space: no parent
            m_EdgeMesh = AddLines("Edges", edgeColor, out _);
            m_HitMesh = AddLines("Hits", UiTheme.Current.colors.danger, out _);
            m_BoxMesh = AddLines("Box", UiTheme.Current.colors.ink, out m_BoxMat);
            m_Root.SetActive(false);
        }

        Mesh AddLines(string name, Color color, out Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(m_Root.transform, false);
            var mesh = new Mesh { name = $"PlacementGuides_{name}" };
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            mat = null;
            if (material != null)
            {
                mat = new Material(material) { name = $"PlacementGuides_{name}", renderQueue = 3150 };
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                r.sharedMaterial = mat;
                m_Owned.Add(mat);
            }
            m_Owned.Add(mesh);
            return mesh;
        }

        void LateUpdate()
        {
            var t = tool != null ? tool : Services.Get<PartTool>();
            var part = t != null ? t.Held : null;
            bool seated = part != null && t.OnSurface;
            // placement: the part in the placement editor (Adjust) gets the same guides while it's moved.
            if (part == null && PlacementEditor.Current != null && PlacementEditor.Current.Adjusting != null) { part = PlacementEditor.Current.Adjusting; seated = true; }
            SceneRoot root = null;
            bool show = part != null && seated && Services.TryGet(out root) && root.IsRuntimePackage && root.Structure != null
                        && root.StructureSpace != null && root.StructureSpace.gameObject.activeInHierarchy;
            if (!show)
            {
                if (m_Root != null && m_Root.activeSelf) m_Root.SetActive(false);
                return;
            }
            if (m_Root == null) Build();
            if (!m_Root.activeSelf) { m_Root.SetActive(true); m_Next = 0f; }
            m_Root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            DrawBox(part);
            if (Time.unscaledTime < m_Next) return;
            m_Next = Time.unscaledTime + refreshInterval;
            Refresh(part, root);
        }

        /// The part's true-size box, coloured by its fit (green / orange / red; ink, the part in hand, before the first check).
        void DrawBox(PartInstance part)
        {
            var b = part.LocalBox;
            var tr = part.transform;
            m_BoxPts.Clear();
            for (int axis = 0; axis < 3; axis++)
                for (int i = 0; i < 4; i++)
                {
                    var a = b.min; var c = b.min;
                    int u = (axis + 1) % 3, v = (axis + 2) % 3;
                    a[u] = c[u] = (i & 1) == 0 ? b.min[u] : b.max[u];
                    a[v] = c[v] = (i & 2) == 0 ? b.min[v] : b.max[v];
                    a[axis] = b.min[axis]; c[axis] = b.max[axis];
                    m_BoxPts.Add(tr.TransformPoint(a)); m_BoxPts.Add(tr.TransformPoint(c));
                }
            SetLines(m_BoxMesh, m_BoxPts);
            if (m_BoxMat != null && m_BoxMat.HasProperty("_BaseColor"))
            {
                var colors = UiTheme.Current.colors;
                var status = part.Fit?.Status ?? FitStatus.None;
                m_BoxMat.SetColor("_BaseColor", status == FitStatus.Green ? colors.success : status == FitStatus.Amber ? colors.warning
                    : status == FitStatus.Red ? colors.danger : colors.ink);
            }
        }

        void Refresh(PartInstance part, SceneRoot root)
        {
            var layer = root.Structure;
            var space = root.StructureSpace;
            var tr = part.transform;
            var box = part.LocalBox;
            float scale = Mathf.Max(SnapService.Scale, 1e-4f);
            float r = radius * scale;
            var centre = part.WorldBoxCentre;
            m_EdgePts.Clear(); m_HitPts.Clear();
            int hits = 0;
            var inflated = box; inflated.Expand(0.004f * scale / Mathf.Max(tr.lossyScale.x, 1e-6f));
            foreach (var e in layer.Edges)
            {
                var a = space.TransformPoint(e.a); var b = space.TransformPoint(e.b);
                if (!ClipToSphere(ref a, ref b, centre, r)) continue;
                if (SegmentHitsBox(tr.InverseTransformPoint(a), tr.InverseTransformPoint(b), inflated)) { m_HitPts.Add(a); m_HitPts.Add(b); hits++; }
                else { m_EdgePts.Add(a); m_EdgePts.Add(b); }
            }
            hits += AddContacts(part, scale);
            SetLines(m_EdgeMesh, m_EdgePts);
            SetLines(m_HitMesh, m_HitPts);
            EdgesShown = m_EdgePts.Count / 2;
            HitsShown = hits;
        }

        /// Crosses where the box goes deeper into the scan than its noise, or into another part.
        int AddContacts(PartInstance part, float scale)
        {
            var box = part.Box;
            if (box == null) return 0;
            var tr = part.transform;
            var lb = part.LocalBox;
            var half = Vector3.Scale(lb.size * 0.5f, tr.lossyScale);
            int n = Physics.OverlapBoxNonAlloc(tr.TransformPoint(lb.center), half, s_Hits, tr.rotation, PartLayers.FitMask, QueryTriggerInteraction.Ignore);
            int added = 0;
            float cross = contactSize * scale;
            for (int i = 0; i < n; i++)
            {
                var col = s_Hits[i];
                if (col == null || col.transform.IsChildOf(tr)) continue;
                bool scan = SnapService.IsScan(col);
                if (scan && !FitChecker.HitsDeeper(col, tr, lb.center, lb.size, SnapService.ScanToleranceWorld)) continue;
                if (!Physics.ComputePenetration(box, box.transform.position, box.transform.rotation, col, col.transform.position, col.transform.rotation,
                        out var dir, out float dist) || dist <= 0f) continue;
                // The deepest point: the box's support point against the push direction.
                var local = tr.InverseTransformDirection(-dir);
                var e = lb.extents;
                var p = tr.TransformPoint(lb.center + new Vector3(Mathf.Sign(local.x) * e.x, Mathf.Sign(local.y) * e.y, Mathf.Sign(local.z) * e.z));
                p += dir * dist;
                m_HitPts.Add(p - Vector3.right * cross); m_HitPts.Add(p + Vector3.right * cross);
                m_HitPts.Add(p - Vector3.up * cross); m_HitPts.Add(p + Vector3.up * cross);
                m_HitPts.Add(p - Vector3.forward * cross); m_HitPts.Add(p + Vector3.forward * cross);
                added++;
            }
            return added;
        }

        void SetLines(Mesh mesh, List<Vector3> pts)
        {
            mesh.Clear();
            if (pts.Count < 2) return;
            mesh.SetVertices(pts);
            m_Index.Clear();
            for (int i = 0; i < pts.Count; i++) m_Index.Add(i);
            mesh.SetIndices(m_Index, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
        }

        /// Keep the part of segment ab inside the sphere (c, r); false if none of it is.
        public static bool ClipToSphere(ref Vector3 a, ref Vector3 b, Vector3 c, float r)
        {
            var d = b - a;
            float len2 = d.sqrMagnitude;
            if (len2 < 1e-12f) return (a - c).sqrMagnitude <= r * r;
            var f = a - c;
            float B = Vector3.Dot(f, d), C = f.sqrMagnitude - r * r;
            float disc = B * B - len2 * C;
            if (disc < 0f) return false;
            float sq = Mathf.Sqrt(disc);
            float t0 = Mathf.Max(0f, (-B - sq) / len2), t1 = Mathf.Min(1f, (-B + sq) / len2);
            if (t0 >= t1) return false;
            var a0 = a;
            a = a0 + d * t0; b = a0 + d * t1;
            return true;
        }

        /// Segment pq (box-local) against an axis-aligned box (slab test).
        public static bool SegmentHitsBox(Vector3 p, Vector3 q, Bounds box)
        {
            var d = q - p;
            float t0 = 0f, t1 = 1f;
            for (int i = 0; i < 3; i++)
            {
                if (Mathf.Abs(d[i]) < 1e-9f) { if (p[i] < box.min[i] || p[i] > box.max[i]) return false; continue; }
                float a = (box.min[i] - p[i]) / d[i], b = (box.max[i] - p[i]) / d[i];
                if (a > b) (a, b) = (b, a);
                t0 = Mathf.Max(t0, a); t1 = Mathf.Min(t1, b);
                if (t0 > t1) return false;
            }
            return true;
        }
    }
}
