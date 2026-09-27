using UnityEngine;

namespace AirTools.Scene
{
    /// Model view's maths (modelview; pure, so it runs in the offline test runner):
    /// - the fit: a round "1:N" scale that puts a scan on the table with its longest side (width, depth or height) at
    ///   most 0.8 m — a 50 m gym and a 4 m kitchen both come out about 0.6–0.8 m across (a fixed 1:50 made the kitchen a
    ///   10 cm toy and the gym a metre);
    /// - where the model stands on a table (modelwheel: the table placement only; floating is ModelViewLayout);
    /// - the full-size pose that lands you at a site's recommended spawn when you step in after switching models on
    ///   the table (the rig never moves in passthrough: the scene moves under you instead).
    /// Yaw rotations are built from their components (no engine calls).
    public static class TabletopFit
    {
        /// The longest side a fitted model may have (m) and where it usually lands (the ladder's steps are ≤ 1.34× apart
        /// from 1:3 up).
        public const float DefaultMax = 0.8f, DefaultMin = 0.6f;

        /// Round denominators (architectural-style scales).
        public static readonly int[] Ladder =
            { 1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 75, 100, 125, 150, 200, 250, 300, 400, 500, 600, 750, 1000 };

        /// The smallest round N with longest / N ≤ max: the biggest model that still fits. 1 for anything already ≤ max;
        /// past the ladder, the next multiple of 250; 0 when there is no size to fit (nothing loaded).
        public static int Denominator(float longest, float max = DefaultMax)
        {
            if (!(longest > 0f) || float.IsInfinity(longest) || float.IsNaN(longest) || !(max > 0f)) return 0;
            foreach (int n in Ladder) if (longest / n <= max + 1e-5f) return n;
            return Mathf.CeilToInt(longest / max / 250f) * 250;
        }

        /// World metres per scene metre on the table for a denominator (0 → `fallback`).
        public static float ScaleFor(int denominator, float fallback) => denominator > 0 ? 1f / denominator : fallback;

        /// The longest of width, height and depth (a tower is fitted by its height).
        public static float LongestSide(Vector3 size) => Mathf.Max(size.x, Mathf.Max(size.y, size.z));

        /// A renderer box that is the synthetic facade's ground plate (or any big flat slab): skipped when measuring the
        /// building. A scan's own mesh is big but not flat, so it counts (the old "> 6 m" rule dropped whole scans).
        public static bool IsGroundPlate(Vector3 extents) =>
            (extents.x > 6f || extents.z > 6f) && extents.y < 0.05f * Mathf.Max(extents.x, extents.z);

        /// How far ahead of the eye (horizontal) the model's centre sits on an assumed or ray-cast table: `baseDistance`,
        /// pushed back so the model's near edge stays `nearClearance` ahead (room for the switcher row).
        public static float CentreDistance(float baseDistance, float halfDepth, float nearClearance) =>
            Mathf.Max(baseDistance, nearClearance + Mathf.Max(0f, halfDepth));

        /// "1:10"; "1:1" at full size.
        public static string Label(int denominator) => denominator <= 1 ? "1:1" : "1:" + denominator;

        /// The wrist strip's scale word in Model view: "Model 1:10".
        public static string ScaleWord(string label) => string.IsNullOrEmpty(label) ? "" : "Model " + label;

        /// Degrees below the horizontal eye line of a point (negative above).
        public static float BelowEyeDeg(Vector3 eye, Vector3 p)
        {
            var d = p - eye;
            return Mathf.Atan2(-d.y, Mathf.Sqrt(d.x * d.x + d.z * d.z)) * Mathf.Rad2Deg;
        }

        // ---------------- stepping in at a site's spawn ----------------

        /// A rotation of `deg` about world up (Quaternion.Euler(0, deg, 0), built without an engine call).
        public static Quaternion Yaw(float deg)
        {
            float h = deg * 0.5f * Mathf.Deg2Rad;
            return new Quaternion(0f, Mathf.Sin(h), 0f, Mathf.Cos(h));
        }

        /// The heading (degrees about up, 0 = +Z, 90 = +X) of a direction.
        public static float Heading(Vector3 forward) => Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;

        /// The full-size pose of the scene root that puts `feetLocal` (a root-space point on the scene's floor) under
        /// `feetWorld` at `worldScale`, with the root-space heading `yawLocalDeg` (the spawn's look) along the world
        /// heading `headingDeg` (where you face). Stepping in then lands you at the recommended spawn, looking where it
        /// looks, without moving the rig.
        public static Pose Rebased(Vector3 feetLocal, float yawLocalDeg, float worldScale, Vector3 feetWorld, float headingDeg)
        {
            var rot = Yaw(headingDeg - yawLocalDeg);
            return new Pose(feetWorld - rot * (feetLocal * worldScale), rot);
        }
    }
}
