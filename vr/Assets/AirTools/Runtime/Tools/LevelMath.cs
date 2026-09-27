using System;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Tools
{
    public enum LevelMode { Level, Plumb }

    public readonly struct LevelReading
    {
        public readonly LevelMode Mode;
        /// Level: slope of the surface from horizontal. Plumb: deviation of the surface from vertical. Degrees.
        public readonly double Degrees;
        public readonly Vector3 Point;
        public readonly Vector3 Normal;
        /// Direction the surface falls (level) / leans (plumb), in the plane of the surface. Zero when dead level.
        public readonly Vector3 Downhill;

        public LevelReading(LevelMode mode, double degrees, Vector3 point, Vector3 normal, Vector3 downhill)
        {
            Mode = mode; Degrees = degrees; Point = point; Normal = normal; Downhill = downhill;
        }

        /// Slope as a percentage grade (rise over run), e.g. 2.0° → 3.5 %.
        public double PercentGrade => Math.Tan(Degrees * Math.PI / 180.0) * 100.0;
    }

    public static class LevelMath
    {
        /// Surfaces within this many degrees of horizontal (either facing up or down) read as "Level"; steeper as "Plumb".
        public const float ModeSplitDeg = 45f;

        public static LevelReading Read(Vector3 point, Vector3 normal, Vector3 up)
        {
            normal = normal.normalized; up = up.normalized;
            float fromUp = Vector3.Angle(normal, up);           // 0 = floor, 90 = wall, 180 = ceiling
            var downhill = Vector3.ProjectOnPlane(-up, normal);
            downhill = downhill.sqrMagnitude > 1e-10f ? downhill.normalized : Vector3.zero;
            if (fromUp <= ModeSplitDeg || fromUp >= 180f - ModeSplitDeg)
            {
                double slope = fromUp <= 90f ? fromUp : 180.0 - fromUp;
                return new LevelReading(LevelMode.Level, slope, point, normal, downhill);
            }
            return new LevelReading(LevelMode.Plumb, Math.Abs(90.0 - fromUp), point, normal, downhill);
        }

        /// Average the surface normal over a small disc around the ray hit (robust on noisy capture meshes).
        /// Samples are extra rays from the same origin towards points on a ring in the hit's tangent plane; only hits
        /// on roughly the same surface (within 20° and 3 cm of the plane) count.
        public static Vector3 SampleNormal(Ray ray, SurfaceHit hit, float radius = 0.03f, int samples = 8, float maxDist = 40f)
        {
            var n = hit.normal.normalized;
            var t1 = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var t2 = Vector3.Cross(n, t1);
            var sum = n;
            for (int i = 0; i < samples; i++)
            {
                float a = i * Mathf.PI * 2f / samples;
                var target = hit.rawPoint + (t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a)) * radius;
                var dir = (target - ray.origin).normalized;
                if (!Physics.Raycast(ray.origin, dir, out var h, maxDist, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore))
                    continue;
                if (Vector3.Angle(h.normal, n) > 20f) continue;
                if (Mathf.Abs(Vector3.Dot(h.point - hit.rawPoint, n)) > 0.03f) continue;
                sum += h.normal;
            }
            return sum.normalized;
        }
    }
}
