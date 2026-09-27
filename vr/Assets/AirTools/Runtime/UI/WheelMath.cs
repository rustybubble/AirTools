using UnityEngine;

namespace AirTools.UI
{
    /// modelwheel: the geometry the tool ring and Model view's wheel share (pure, so the offline runner tests it; no engine
    /// calls). Angles are from the top of the circle, clockwise positive (to the right); points are in the wheel's plane
    /// (x right, y up).
    /// - The drag: the angle a pinch moving along the wheel turns it by (the item under the fingers follows them).
    /// - The fade toward the ends of the visible arc.
    /// - The endless strip (the model wheel): N items on an arc that shows fewer than N at a time repeat without a seam —
    ///   card slot k (k = 0 under the lens) shows item Wrap(base + k), `base` the item nearest the lens. A short list shows
    ///   each item once (the slots in a window N wide).
    public static class WheelMath
    {
        /// dψ for a move dp at p (p relative to the wheel's centre), ψ = the clockwise angle from the top:
        /// (y·dx − x·dy) / r², r clamped so a pinch near the middle can't whip the wheel round.
        public static float DragAngle(Vector2 p, Vector2 dp, float minRadius = 0.05f)
        {
            float r2 = Mathf.Max(p.sqrMagnitude, minRadius * minRadius);
            return (p.y * dp.x - p.x * dp.y) / r2;
        }

        /// How opaque an item `deg` degrees from the top is: 1 up to the fade band, easing to 0 at visibleHalf (hidden
        /// beyond).
        public static float Fade(float deg, float visibleHalf, float band) =>
            deg >= visibleHalf ? 0f : 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(visibleHalf - Mathf.Max(0.01f, band), visibleHalf, deg));

        /// A point `angle` round a circle of `radius` whose top is the origin (the lens): (r sin a, r cos a − r).
        public static Vector2 ArcPoint(float angle, float radius) =>
            new Vector2(Mathf.Sin(angle) * radius, (Mathf.Cos(angle) - 1f) * radius);

        /// The circle's centre in the same frame (the lens is its top).
        public static Vector2 Centre(float radius) => new Vector2(0f, -radius);

        /// How close to the lens an item `angle` from the top is: 1 on it, 0 from 90 % of a spacing away.
        public static float Near(float angle, float spacing) => 1f - Mathf.Clamp01(Mathf.Abs(angle) / Mathf.Max(1e-5f, spacing * 0.9f));

        // ---------------- the endless strip ----------------

        /// The item nearest the lens for a dial position (in items, unwrapped).
        public static int SlotBase(float position) => Mathf.RoundToInt(position);

        /// How far the wheel is past that item (−0.5…0.5 of a spacing).
        public static float SlotOffset(float position) => position - Mathf.RoundToInt(position);

        /// The item card slot k shows (wrapped into 0…count−1).
        public static int SlotItem(int slotBase, int k, int count) => DialPhysics.Wrap(slotBase + k, count);

        /// Where card slot k sits (radians from the top): the next item (k + 1) to the right.
        public static float SlotAngle(int k, float offset, float spacing) => (k - offset) * spacing;

        /// Does card slot k show at all: the slots in a window `count` items wide around the lens, so a list shorter
        /// than the arc shows each item once. Half-open on the side the wheel is past its item, so an item at the
        /// window's edge shows on one side only and the lens slot always shows.
        public static bool SlotShown(int k, float offset, int count)
        {
            if (count <= 0) return false;
            float x = k - offset, half = count * 0.5f;
            return offset >= 0f ? x >= -half && x < half : x > -half && x <= half;
        }
    }
}
