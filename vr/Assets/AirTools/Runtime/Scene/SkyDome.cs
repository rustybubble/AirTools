using System.Collections.Generic;
using UnityEngine;

namespace AirTools.Scene
{
    /// The World sky (presence.md S1): an 80 m icosphere (1280 tris, drawn from inside) that follows the head,
    /// AirTools/SkyDome. It replaces the skybox in World (the cameras clear to black behind it), so a transition can
    /// close it over the passthrough room cone by cone (RevealField.SetSky) and end on exactly the World look with no
    /// pop. Hidden in Passthrough and Tabletop.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class SkyDome : MonoBehaviour
    {
        public Transform head;
        public float radius = 80f;
        [Range(0, 4)] public int subdivisions = 3;
        [Tooltip("Width of the accent band at the edge of the closing sky (radians).")]
        public float bandRadians = 0.06f;

        public bool Visible { get; private set; }
        MeshRenderer m_Renderer;

        void Awake() => EnsureMesh();

        public void EnsureMesh()
        {
            m_Renderer = GetComponent<MeshRenderer>();
            var mf = GetComponent<MeshFilter>();
            if (mf.sharedMesh == null || mf.sharedMesh.vertexCount == 0) mf.sharedMesh = Icosphere(subdivisions, radius);
        }

        public void SetVisible(bool visible)
        {
            if (m_Renderer == null) EnsureMesh();
            Visible = visible;
            m_Renderer.enabled = visible;
            if (visible) Follow();
        }

        /// Show the sky within `angle` radians of `direction` (π = all of it).
        public void SetReveal(Vector3 direction, float angle) => RevealField.SetSky(direction, angle, bandRadians);

        public void SetFull() => RevealField.SetSky(Vector3.up, Mathf.PI, bandRadians);

        void LateUpdate() { if (Visible) Follow(); }

        void Follow()
        {
            if (head != null) transform.position = head.position;
            transform.rotation = Quaternion.identity;
        }

        /// A unit icosahedron subdivided `subdivisions` times (20·4ⁿ triangles), scaled to `radius`, faces outward
        /// (the shader culls front faces, so it's seen from inside).
        public static Mesh Icosphere(int subdivisions, float radius)
        {
            float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            var v = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            for (int i = 0; i < v.Count; i++) v[i] = v[i].normalized;
            var f = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };
            var mid = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (mid.TryGetValue(key, out int m)) return m;
                v.Add(((v[a] + v[b]) * 0.5f).normalized);
                mid[key] = v.Count - 1;
                return v.Count - 1;
            }
            for (int s = 0; s < subdivisions; s++)
            {
                var nf = new List<int>(f.Count * 4);
                for (int i = 0; i < f.Count; i += 3)
                {
                    int a = f[i], b = f[i + 1], c = f[i + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    nf.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                f = nf;
            }
            // Front faces outward (Unity's triangle normal cross(b − a, c − a) points to the front side): flip any
            // triangle whose normal points in.
            for (int i = 0; i < f.Count; i += 3)
            {
                Vector3 a = v[f[i]], b = v[f[i + 1]], c = v[f[i + 2]];
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), a + b + c) < 0f) (f[i + 1], f[i + 2]) = (f[i + 2], f[i + 1]);
            }
            for (int i = 0; i < v.Count; i++) v[i] *= radius;
            var mesh = new Mesh { name = "SkyDome" };
            mesh.SetVertices(v);
            mesh.SetTriangles(f, 0);
            mesh.RecalculateNormals();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * radius * 2f);
            return mesh;
        }
    }
}
