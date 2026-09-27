using AirTools.Parts;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Tools
{
    /// What the scene says about a ladder setup, by raycasts (presence.md S4): the ground under the foot and its
    /// slope, how far out the top must sit to clear a gutter, whether there's a landing behind an edge, and whether
    /// anything is inside the rails. World space in, world space out; `scale` = world units per scene metre
    /// (1 at full size, 0.02 on the tabletop model). No per-call allocations.
    public static class LadderProbe
    {
        static readonly Collider[] s_Overlap = new Collider[16];

        public static int CollisionMask => SceneLayers.SceneSurfaceMask | PartLayers.PartsMask;

        /// Ground under a foot point: the first surface from 0.5 m above it to 0.3 m below (the "no ground within
        /// 0.3 m" rule), and its slope from `up` averaged over a 5 cm ring (LevelMath.SampleNormal).
        public static bool Ground(Vector3 foot, Vector3 up, float scale, out Vector3 ground, out float slopeDeg)
        {
            ground = foot;
            slopeDeg = 0f;
            float above = 0.5f * scale;
            var ray = new Ray(foot + up * above, -up);
            if (!Physics.Raycast(ray, out var h, above + (float)LadderMath.GroundSearchM * scale, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore))
                return false;
            ground = h.point;
            var hit = new SurfaceHit { point = h.point, rawPoint = h.point, normal = h.normal, collider = h.collider, kind = SnapKind.Face };
            var n = LevelMath.SampleNormal(ray, hit, 0.05f * scale, 8, 2f * scale);
            slopeDeg = Vector3.Angle(n, up);
            return true;
        }

        /// Run per unit rise at the 4:1 angle (1/√15).
        public static readonly float IdealRunPerRise = 1f / Mathf.Sqrt(15f);

        /// The top contact pushed out along `outward` until a rail line leaning at `runPerRise` (4:1 by default) from it
        /// clears everything under the edge (a gutter lip, a sill nose), plus a centimetre: horizontal rays back toward
        /// the face every 2 cm down to 0.4 m, then every 10 cm to 1.2 m, never lower than `maxDrop` (scene m; keep the
        /// probes off the ground). A steeper ladder (a foot dragged in) pivots out on the same lip.
        public static Vector3 Clearance(Vector3 top, Vector3 outward, Vector3 up, float scale, float margin = 0.01f, float runPerRise = -1f, float maxDrop = 1.2f)
        {
            if (runPerRise < 0f) runPerRise = IdealRunPerRise;
            const float reach = 1.0f;
            float push = 0f;
            float lowest = Mathf.Min(1.2f, maxDrop);
            for (float dy = 0.01f; dy <= lowest; dy += dy < 0.4f ? 0.02f : 0.1f)
            {
                var from = top + (outward * reach - up * dy) * scale;
                if (!Physics.Raycast(from, -outward, out var h, (reach + 0.05f) * scale, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore))
                    continue;
                float proud = reach - h.distance / scale;   // how far past the edge's face the obstacle stands
                if (proud <= 0.001f) continue;
                float need = proud + margin - dy * runPerRise;
                if (need > push) push = need;
            }
            return top + outward * (push * scale);
        }

        /// A walkable surface (≤ 60° slope) behind the edge at about its height: a roof or floor you step onto, so
        /// the rails must run 3 ft past it. Probes 0.4 m in from the edge.
        public static bool HasLanding(Vector3 top, Vector3 outward, Vector3 up, float scale)
        {
            var from = top - outward * (0.4f * scale) + up * (0.4f * scale);
            if (!Physics.Raycast(from, -up, out var h, 0.8f * scale, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore)) return false;
            return Vector3.Dot(h.normal, up) > 0.5f;
        }

        /// Anything (scene or placed parts) inside the rails' envelope: a 0.45 × 0.10 m section on the air side of the
        /// rail line, from the foot to the top contact, shrunk 2 cm at the contact and 10 cm at the foot (the footing
        /// is its own check; scan noise there isn't a collision).
        public static bool Collides(Vector3 foot, Vector3 contact, Vector3 outward, Vector3 up, float scale, out Collider with)
        {
            with = null;
            var axis = contact - foot;
            float len = axis.magnitude;
            if (len < 0.2f * scale) return false;
            axis /= len;
            var side = Vector3.Cross(up, outward);
            if (side.sqrMagnitude < 1e-8f) return false;
            side.Normalize();
            var off = Vector3.Cross(axis, side).normalized;
            if (Vector3.Dot(off, outward) < 0f) off = -off;   // toward the air side (out and up)
            float top = 0.02f * scale, bottom = 0.10f * scale;
            var a = foot + axis * bottom;
            var b = contact - axis * top;
            float half = Vector3.Distance(a, b) * 0.5f;
            if (half <= 0f) return false;
            var centre = (a + b) * 0.5f + off * (0.05f * scale);
            var halfExtents = new Vector3(0.225f * scale, 0.05f * scale, half);
            var rot = Quaternion.LookRotation(axis, off);     // z along the rails, y off the ladder plane, x along the edge
            int n = Physics.OverlapBoxNonAlloc(centre, halfExtents, s_Overlap, rot, CollisionMask, QueryTriggerInteraction.Ignore);
            if (n <= 0) return false;
            with = s_Overlap[0];
            for (int i = 0; i < n; i++) s_Overlap[i] = null;
            return true;
        }

        /// "the gutter", "the wall", "a placed part": what the rails hit, in words.
        public static string Name(Collider c)
        {
            if (c == null) return "something";
            if (((1 << c.gameObject.layer) & PartLayers.PartsMask) != 0) return "a placed part";
            if (SnapService.IsScan(c)) return "the building";
            return SurfaceNames.FromPath(FitChecker.SurfaceName(c));
        }
    }
}
