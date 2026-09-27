using System.Collections.Generic;
using AirTools.Core;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Structure
{
    /// Draws the package's structure layer over the scene (backend integration check 3): image lines cyan, creases
    /// yellow, plane outlines violet, corners as small white crosses. One line-topology mesh per kind, built once per
    /// revision under the scene content (so it follows the calibration and the tabletop scale). Toggle from the palm
    /// menu or AppCommands.ShowStructure.
    public class StructureOverlay : MonoBehaviour
    {
        public SceneRoot sceneRoot;
        [Tooltip("Unlit material template (URP/Unlit); tinted per kind.")]
        public Material material;
        public Color lineColor = new Color(0.25f, 0.9f, 1f);
        public Color creaseColor = new Color(1f, 0.85f, 0.2f);
        public Color boundaryColor = new Color(0.75f, 0.55f, 1f);
        public Color cornerColor = Color.white;
        [Tooltip("Corner cross half-size, package metres.")]
        public float cornerSize = 0.012f;

        public bool Visible { get; private set; }
        public int EdgeCount { get; private set; }
        public int CornerCount { get; private set; }

        GameObject m_Root;
        StructureLayer m_BuiltFor;
        Transform m_BuiltUnder;
        readonly List<Object> m_Owned = new List<Object>();

        void OnEnable() { Services.Register(this); Hook(); }
        void OnDisable() { Services.Unregister(this); Unhook(); }

        SceneRoot m_Hooked;
        void Hook()
        {
            var r = sceneRoot != null ? sceneRoot : Services.Get<SceneRoot>();
            if (r == null || r == m_Hooked) return;
            Unhook();
            r.ContentChanged += Rebuild;
            m_Hooked = r;
        }
        void Unhook() { if (m_Hooked != null) m_Hooked.ContentChanged -= Rebuild; m_Hooked = null; }
        void Start() => Hook();

        public void SetVisible(bool on)
        {
            Visible = on;
            Rebuild();
        }

        public void Toggle() => SetVisible(!Visible);

        public void Rebuild()
        {
            var root = m_Hooked != null ? m_Hooked : (sceneRoot != null ? sceneRoot : Services.Get<SceneRoot>());
            var layer = root != null ? root.Structure : null;
            var space = root != null ? root.StructureSpace : null;
            if (!Visible || layer == null || space == null)
            {
                if (m_Root != null) m_Root.SetActive(false);
                EdgeCount = CornerCount = 0;
                return;
            }
            if (m_Root != null && m_BuiltFor == layer && m_BuiltUnder == space) { m_Root.SetActive(true); return; }
            Clear();
            m_Root = new GameObject("StructureOverlay");
            m_Root.transform.SetParent(space, false);
            var byKind = new Dictionary<string, List<Vector3>>();
            foreach (var e in layer.Edges)
            {
                string k = e.kind == "crease" || e.kind == "boundary" ? e.kind : "line";
                if (!byKind.TryGetValue(k, out var list)) byKind[k] = list = new List<Vector3>();
                list.Add(e.a); list.Add(e.b);
            }
            var cross = new List<Vector3>();
            foreach (var c in layer.Corners)
            {
                cross.Add(c.p - Vector3.right * cornerSize); cross.Add(c.p + Vector3.right * cornerSize);
                cross.Add(c.p - Vector3.up * cornerSize); cross.Add(c.p + Vector3.up * cornerSize);
                cross.Add(c.p - Vector3.forward * cornerSize); cross.Add(c.p + Vector3.forward * cornerSize);
            }
            foreach (var kv in byKind) AddLines(kv.Key, kv.Value, kv.Key == "crease" ? creaseColor : kv.Key == "boundary" ? boundaryColor : lineColor);
            AddLines("corners", cross, cornerColor);
            EdgeCount = layer.Edges.Length;
            CornerCount = layer.Corners.Length;
            m_BuiltFor = layer;
            m_BuiltUnder = space;
            Log.Info($"Structure overlay: {EdgeCount} edges, {CornerCount} corners");
        }

        void AddLines(string name, List<Vector3> pts, Color color)
        {
            if (pts.Count < 2) return;
            var go = new GameObject(name);
            go.transform.SetParent(m_Root.transform, false);
            var mesh = new Mesh { name = $"Structure_{name}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(pts);
            var idx = new int[pts.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            mesh.SetIndices(idx, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (material != null)
            {
                var m = new Material(material) { name = $"Structure_{name}" };
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
                m.renderQueue = 3100;
                r.sharedMaterial = m;
                m_Owned.Add(m);
            }
            m_Owned.Add(mesh);
        }

        void Clear()
        {
            if (m_Root != null) Destroy(m_Root);
            foreach (var o in m_Owned) if (o != null) Destroy(o);
            m_Owned.Clear();
            m_Root = null; m_BuiltFor = null; m_BuiltUnder = null;
        }

        static new void Destroy(Object o)
        {
            if (Application.isPlaying) Object.Destroy(o); else Object.DestroyImmediate(o);
        }
    }
}
