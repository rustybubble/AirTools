using UnityEngine;

namespace AirTools.UI
{
    /// The quad every glass surface draws with. Shape and look travel in vertex data, the tint in vertex colour, so all
    /// surfaces share one material per role, batch under the SRP batcher, and change state without material instances
    /// (or MaterialPropertyBlocks, which would take them out of the SRP batcher):
    ///   UV0 shape-local position · UV1 width, height, corner radius, rim width · UV2 rim strength, gradient, softness,
    ///   state glow · UV3 bezel, highlight, edge clearness, press · UV4 sheen, frost (the Liquid Glass look, GlassLook).
    public static class GlassMesh
    {
        public struct Shape
        {
            public Vector2 Size;
            public float Radius, RimWidth, RimStrength, Gradient, Softness;
            /// Liquid glass (all 0 = the flat glass): the bezel width (local units, already fitted to the size), highlight
            /// 0..1, edge clearness 0..1, body sheen, frost grain.
            public float Bezel, Highlight, EdgeClear, Sheen, Frost;
            /// State (GlassButton): glow 0..1 brightens the rim, stroke and highlight; press 0..1 turns the bezel concave.
            public float Glow, Press;
        }

        public static Mesh Create(string name)
        {
            var m = new Mesh { name = name };
            m.MarkDynamic();
            return m;
        }

        static Vector4 P2(in Shape s) => new Vector4(s.RimStrength, s.Gradient, s.Softness, s.Glow);
        static Vector4 P3(in Shape s) => new Vector4(s.Bezel, s.Highlight, s.EdgeClear, s.Press);
        static Vector4 P4(in Shape s) => new Vector4(s.Sheen, s.Frost, 0f, 0f);

        /// Writes a quad centred on the origin in the XY plane, facing -Z; the quad is grown by the softness so soft
        /// shadows have room to fade out.
        public static void Write(Mesh mesh, Shape s, Color tint)
        {
            float pad = Mathf.Max(0f, s.Softness);
            float hx = s.Size.x * 0.5f + pad, hy = s.Size.y * 0.5f + pad;
            var v = new[] { new Vector3(-hx, -hy, 0), new Vector3(-hx, hy, 0), new Vector3(hx, hy, 0), new Vector3(hx, -hy, 0) };
            // uv0 in shape-local metres (origin at the centre) so the shader's distance field is exact at any size.
            var uv0 = new[] { new Vector2(-hx, -hy), new Vector2(-hx, hy), new Vector2(hx, hy), new Vector2(hx, -hy) };
            var p1 = new Vector4(s.Size.x, s.Size.y, Mathf.Min(s.Radius, Mathf.Min(s.Size.x, s.Size.y) * 0.5f), s.RimWidth);
            var p2 = P2(s);
            var p3 = P3(s);
            var p4 = P4(s);
            mesh.Clear();
            mesh.vertices = v;
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(1, new[] { p1, p1, p1, p1 });
            mesh.SetUVs(2, new[] { p2, p2, p2, p2 });
            mesh.SetUVs(3, new[] { p3, p3, p3, p3 });
            mesh.SetUVs(4, new[] { p4, p4, p4, p4 });
            mesh.colors = new[] { tint, tint, tint, tint };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
        }

        static readonly Color[] s_Colors = new Color[4];
        static readonly Vector4[] s_P2 = new Vector4[4], s_P3 = new Vector4[4];

        /// Recolour only (state changes): no allocations beyond the shared buffer.
        public static void SetTint(Mesh mesh, Color tint)
        {
            for (int i = 0; i < 4; i++) s_Colors[i] = tint;
            mesh.colors = s_Colors;
        }

        /// Glow and press only (GlassButton's hover / press / selected): rewrites UV2 and UV3 from shared buffers, no allocation.
        public static void SetState(Mesh mesh, in Shape s)
        {
            var p2 = P2(s);
            var p3 = P3(s);
            for (int i = 0; i < 4; i++) { s_P2[i] = p2; s_P3[i] = p3; }
            mesh.SetUVs(2, s_P2);
            mesh.SetUVs(3, s_P3);
        }
    }
}
