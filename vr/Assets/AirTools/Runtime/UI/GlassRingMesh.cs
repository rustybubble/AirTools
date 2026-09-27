using System.Collections.Generic;
using UnityEngine;

namespace AirTools.UI
{
    /// Tight meshes for the Liquid Glass shader: UV0 carries the surface-local position in metres (the shader evaluates
    /// the exact shape there), and the triangles only cover the shape plus its halo, so the pixel shader doesn't run over
    /// the ring's empty middle. Faces −z (toward a viewer the object looks away from).
    public static class GlassRingMesh
    {
        /// An annulus sector around the origin: radii [inner, outer], angles from +y (clockwise positive) in
        /// [−halfAngle, +halfAngle] degrees.
        public static Mesh Arc(float inner, float outer, float halfAngleDeg, int segments = 64)
        {
            var mesh = new Mesh { name = "LiquidGlassArc" };
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            segments = Mathf.Max(2, segments);
            for (int i = 0; i <= segments; i++)
            {
                float a = Mathf.Deg2Rad * Mathf.Lerp(-halfAngleDeg, halfAngleDeg, i / (float)segments);
                var dir = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
                Add(dir * inner); Add(dir * outer);
                if (i == 0) continue;
                int b = v.Count - 4;
                tri.Add(b); tri.Add(b + 1); tri.Add(b + 2);
                tri.Add(b + 1); tri.Add(b + 3); tri.Add(b + 2);
            }
            mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(outer * 2f, outer * 2f, 0.01f));
            return mesh;

            void Add(Vector2 p) { v.Add(new Vector3(p.x, p.y, 0f)); uv.Add(p); }
        }

        /// A quad of the given size (metres) centred on the origin, grown by `margin` for the halo.
        public static Mesh Quad(Vector2 size, float margin)
        {
            var h = size * 0.5f + Vector2.one * margin;
            var mesh = new Mesh { name = "LiquidGlassQuad" };
            var p = new[] { new Vector2(-h.x, -h.y), new Vector2(-h.x, h.y), new Vector2(h.x, -h.y), new Vector2(h.x, h.y) };
            mesh.SetVertices(new List<Vector3> { p[0], p[1], p[2], p[3] });
            mesh.SetUVs(0, new List<Vector2>(p));
            mesh.SetTriangles(new[] { 0, 1, 2, 1, 3, 2 }, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
