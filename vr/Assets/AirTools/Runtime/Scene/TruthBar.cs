using System.Collections.Generic;
using AirTools.Core;
using UnityEngine;

namespace AirTools.Scene
{
    /// The truth bar (presence.md S1/P2, optional): a virtual 1.000 m ruler with centimetre ticks that starts at the
    /// site mat's near-left corner and runs along the table edge (the mat's x axis), lying on the printed 1 m scale
    /// bar from the capture kit — a visible check that the app's metre is the real metre. Shown on the table
    /// (Passthrough / Tabletop) once the mat is locked; hidden in the world. One static mesh, 1 draw.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class TruthBar : MonoBehaviour
    {
        public SiteMatTracker mat;
        [Tooltip("Optional label at the far end (\"1.000 m\").")]
        public GameObject label;
        public float length = 1f;
        [Tooltip("Height above the table (m), so it doesn't z-fight the printed bar.")]
        public float lift = 0.001f;

        public bool Shown { get; private set; }
        MeshRenderer m_Renderer;

        void Awake()
        {
            m_Renderer = GetComponent<MeshRenderer>();
            var mf = GetComponent<MeshFilter>();
            if (mf.sharedMesh == null) mf.sharedMesh = BuildMesh(length);
            SetShown(false);
        }

        void LateUpdate() => Refresh();

        public void Refresh()
        {
            bool show = mat != null && mat.Locked && AppState.Mode != AppMode.World;
            if (show != Shown) SetShown(show);
            if (!show) return;
            var frame = mat.MatPose;
            var spec = mat.Spec ?? mat.defaultSpec;
            transform.SetPositionAndRotation(frame.position + frame.rotation * spec.Corner + Vector3.up * lift, frame.rotation);
        }

        void SetShown(bool on)
        {
            Shown = on;
            if (m_Renderer != null) m_Renderer.enabled = on;
            if (label != null) label.SetActive(on);
        }

        /// A 1 mm line along +x from 0 to `length`, with ticks toward −z (your side): 4 mm every cm, 8 mm every 5 cm,
        /// 12 mm every 10 cm and at both ends. Flat on the table (y = 0).
        public static Mesh BuildMesh(float length)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            void Quad(float x0, float x1, float z0, float z1)
            {
                int i = v.Count;
                v.Add(new Vector3(x0, 0f, z0)); v.Add(new Vector3(x0, 0f, z1)); v.Add(new Vector3(x1, 0f, z1)); v.Add(new Vector3(x1, 0f, z0));
                t.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
            const float w = 0.001f;
            Quad(0f, length, -w * 0.5f, w * 0.5f);
            int cm = Mathf.RoundToInt(length * 100f);
            for (int k = 0; k <= cm; k++)
            {
                float x = k * 0.01f;
                float len = k == 0 || k == cm || k % 10 == 0 ? 0.012f : k % 5 == 0 ? 0.008f : 0.004f;
                Quad(Mathf.Clamp(x - w * 0.5f, 0f, length), Mathf.Clamp(x + w * 0.5f, 0f, length), -len, 0f);
            }
            var mesh = new Mesh { name = "TruthBar" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
