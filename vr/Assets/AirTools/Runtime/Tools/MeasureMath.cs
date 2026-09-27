using System;
using System.Collections.Generic;
using UnityEngine;

namespace AirTools.Tools
{
    /// Where a polygon's outline crosses itself (MeasureMath.Measure): nowhere; only through its closing side (last point
    /// back to the first: more points may still fix it); or along the placed path itself (a bow-tie: undo a point).
    public enum OutlineCrossing : byte { None, Closing, Path }

    /// Result of measuring placed points (SPEC §9: points replace the tape and protractor).
    /// 2 points (or any collinear set): a distance. 3+ points: the polygon through the points in the order they were
    /// placed, closing back to the first — concave shapes (L, U, notches) included — measured in the points' best-fit
    /// plane: a side length per edge, an interior angle at every corner (0–360°, reflex corners over 180°) and the area.
    /// If that outline crosses itself it has no area (Crossing, Area 0) — unless every point is a corner of the convex
    /// hull, where the hull order is the only outline that doesn't cross, so it is used instead (Reordered).
    /// All values SI (metres, m², degrees).
    public readonly struct PointMeasurement
    {
        public readonly int PointCount;
        /// Indices into the input points, in drawing order around the outline: click order (0, 1, …), minus repeats of
        /// the previous point; hull order when Reordered; the two extremes for a distance.
        public readonly int[] Outline;
        /// Side i runs from outline point i to outline point i+1 (the last side closes back to outline point 0).
        public readonly double[] Sides;
        /// Interior angle at outline point i, degrees in [0, 360) (empty for a distance). They sum to (n−2)·180.
        public readonly double[] Angles;
        /// Area of the outline projected onto its best-fit plane (0 for a distance or a crossed outline).
        public readonly double Area;
        public readonly double Perimeter;
        /// Largest distance of any point from the best-fit plane (0 for 2–3 points).
        public readonly double PlanarityError;
        /// The best-fit plane's normal; the outline runs counter-clockwise about it (zero for a distance).
        public readonly Vector3 Normal;
        /// The outline crosses itself: no area, and MeasureTool won't save it.
        public readonly OutlineCrossing Crossing;
        /// The clicks crossed but every point is a hull corner: the outline is the hull, not the click order.
        public readonly bool Reordered;

        public PointMeasurement(int count, int[] outline, double[] sides, double[] angles, double area, double perimeter, double planarity,
                                Vector3 normal, OutlineCrossing crossing = OutlineCrossing.None, bool reordered = false)
        {
            PointCount = count; Outline = outline; Sides = sides; Angles = angles; Area = area; Perimeter = perimeter;
            PlanarityError = planarity; Normal = normal; Crossing = crossing; Reordered = reordered;
        }

        public int OutlineCount => Outline?.Length ?? 0;
        public bool IsDistance => OutlineCount == 2;
        public bool SelfIntersecting => Crossing != OutlineCrossing.None;
        public double Distance => Sides.Length > 0 ? Sides[0] : 0;

        /// Old names from when the outline was always the convex hull (kept so parallel branches compile).
        public int[] Hull => Outline;
        public int HullCount => OutlineCount;
    }

    public static class MeasureMath
    {
        public const int MinPoints = 2;
        /// Sanity cap only (the polygon tool has no practical limit).
        public const int MaxPoints = 64;
        /// Metres: a point this close to the previous one is the same corner; sides this close to each other touch
        /// (a touch counts as a crossing: a figure-8 through one corner has no single area either).
        public const double Tolerance = 1e-3;

        public static double Distance(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

        public static PointMeasurement Measure(IReadOnlyList<Vector3> p)
        {
            if (p == null || p.Count < MinPoints || p.Count > MaxPoints)
                throw new ArgumentException($"measure needs {MinPoints}–{MaxPoints} points, got {p?.Count ?? 0}");
            int n = p.Count;
            if (n == 2) return Segment(p, 0, 1);

            // A working plane from three well-spread points: farthest from the centroid, farthest from that, and the one
            // making the largest triangle with them. (The final normal is Newell's, from the ordered outline.)
            var centroid = default(Vector3d);
            for (int i = 0; i < n; i++) centroid += new Vector3d(p[i]);
            centroid = centroid / n;
            int a0 = 0; double best = -1;
            for (int i = 0; i < n; i++) { double d = (new Vector3d(p[i]) - centroid).Magnitude; if (d > best) { best = d; a0 = i; } }
            int a1 = a0; best = -1;
            for (int i = 0; i < n; i++) { double d = (new Vector3d(p[i]) - new Vector3d(p[a0])).Magnitude; if (d > best) { best = d; a1 = i; } }
            var axis = new Vector3d(p[a1]) - new Vector3d(p[a0]);
            Vector3d normal = default; best = 0;
            for (int i = 0; i < n; i++)
            {
                var c = Vector3d.Cross(axis, new Vector3d(p[i]) - new Vector3d(p[a0]));
                if (c.Magnitude > best) { best = c.Magnitude; normal = c; }
            }
            // Collinear points: the distance between the two extremes.
            if (best < 1e-9 * Math.Max(1.0, axis.Magnitude * axis.Magnitude)) return Segment(p, Math.Min(a0, a1), Math.Max(a0, a1), n);
            normal = normal / normal.Magnitude;
            var u = axis / axis.Magnitude;
            var v = Vector3d.Cross(normal, u);

            var s = Scratch.Get();
            var o = new Vector3d(p[a0]);
            for (int i = 0; i < n; i++) { var q = new Vector3d(p[i]) - o; s.X[i] = Vector3d.Dot(q, u); s.Y[i] = Vector3d.Dot(q, v); }

            // Click order, without repeats: a point on top of the previous one (or the last on the first) is the same corner.
            int m = 0;
            for (int i = 0; i < n; i++)
                if (m == 0 || (new Vector3d(p[i]) - new Vector3d(p[s.Order[m - 1]])).Magnitude > Tolerance) s.Order[m++] = i;
            while (m > 1 && (new Vector3d(p[s.Order[m - 1]]) - new Vector3d(p[s.Order[0]])).Magnitude <= Tolerance) m--;
            if (m < 3) return Segment(p, Math.Min(a0, a1), Math.Max(a0, a1), n);

            bool reordered = false;
            bool crossed = Crosses(s.X, s.Y, s.Order, m, closed: true);
            if (crossed && m >= 4 && ConvexHull(s, m) == m)
            {
                // Every point is a hull corner: the hull is the only outline through them that doesn't cross (an
                // out-of-order click on a rectangle). Start at the earliest-placed point, go the way they were placed.
                int start = 0;
                for (int k = 1; k < m; k++) if (s.Hull[k] < s.Hull[start]) start = k;
                for (int k = 0; k < m; k++) s.Order[k] = s.Hull[(start + k) % m];
                if (s.Order[m - 1] < s.Order[1]) Array.Reverse(s.Order, 1, m - 1);
                reordered = true;
                crossed = false;
            }
            var crossing = !crossed ? OutlineCrossing.None
                : Crosses(s.X, s.Y, s.Order, m, closed: false) ? OutlineCrossing.Path : OutlineCrossing.Closing;

            var outline = new int[m];
            Array.Copy(s.Order, outline, m);
            var sides = new double[m];
            double perimeter = 0;
            Vector3d newell = default;
            for (int k = 0; k < m; k++)
            {
                var a = new Vector3d(p[outline[k]]); var b = new Vector3d(p[outline[(k + 1) % m]]);
                sides[k] = (b - a).Magnitude;
                perimeter += sides[k];
                newell += Vector3d.Cross(a, b);   // Newell: the best-fit plane's normal, |v| / 2 = the projected area
            }
            double area = 0;
            Vector3d outNormal;
            if (crossing == OutlineCrossing.None && newell.Magnitude > 1e-12)
            {
                area = newell.Magnitude * 0.5;
                outNormal = newell / newell.Magnitude;
            }
            else outNormal = Vector3d.Dot(newell, normal) < 0 ? normal * -1.0 : normal;

            // Interior angles in the plane: 180° minus the signed turn at each corner (the outline runs counter-clockwise
            // about the normal, so left turns are convex corners and right turns reflex ones).
            var angles = new double[m];
            for (int k = 0; k < m; k++)
            {
                var cur = new Vector3d(p[outline[k]]);
                var d1 = Flat(cur - new Vector3d(p[outline[(k + m - 1) % m]]), outNormal);
                var d2 = Flat(new Vector3d(p[outline[(k + 1) % m]]) - cur, outNormal);
                double turn = Math.Atan2(Vector3d.Dot(Vector3d.Cross(d1, d2), outNormal), Vector3d.Dot(d1, d2)) * 180.0 / Math.PI;
                angles[k] = 180.0 - turn;
            }
            double planarity = 0;
            if (n >= 4)
                for (int i = 0; i < n; i++) planarity = Math.Max(planarity, Math.Abs(Vector3d.Dot(new Vector3d(p[i]) - centroid, outNormal)));
            return new PointMeasurement(n, outline, sides, angles, area, perimeter, planarity, outNormal.ToVector3(), crossing, reordered);
        }

        static Vector3d Flat(Vector3d d, Vector3d n) => d - n * Vector3d.Dot(d, n);

        static PointMeasurement Segment(IReadOnlyList<Vector3> p, int i, int j, int count = 2)
        {
            double d = Distance(p[i], p[j]);
            return new PointMeasurement(count, new[] { i, j }, new[] { d }, Array.Empty<double>(), 0, d, 0, Vector3.zero);
        }

        // ---------------- crossing ----------------

        /// Do two sides of the outline order[0..m) cross or touch (closer than Tolerance)? Neighbouring sides share a
        /// corner and never count. closed: include the side from the last point back to the first. Pure, allocation-free.
        static bool Crosses(double[] x, double[] y, int[] order, int m, bool closed)
        {
            int sides = closed ? m : m - 1;
            for (int i = 0; i < sides; i++)
            {
                int a = order[i], b = order[(i + 1) % m];
                for (int j = i + 2; j < sides; j++)
                {
                    if (closed && i == 0 && j == m - 1) continue;   // the closing side meets side 0 at the first point
                    int c = order[j], d = order[(j + 1) % m];
                    if (SidesTouch(x[a], y[a], x[b], y[b], x[c], y[c], x[d], y[d])) return true;
                }
            }
            return false;
        }

        static bool SidesTouch(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
        {
            double o1 = Orient(ax, ay, bx, by, cx, cy), o2 = Orient(ax, ay, bx, by, dx, dy);
            double o3 = Orient(cx, cy, dx, dy, ax, ay), o4 = Orient(cx, cy, dx, dy, bx, by);
            if (((o1 > 0 && o2 < 0) || (o1 < 0 && o2 > 0)) && ((o3 > 0 && o4 < 0) || (o3 < 0 && o4 > 0))) return true;
            double t = Tolerance * Tolerance;
            return PointSide2(cx, cy, ax, ay, bx, by) <= t || PointSide2(dx, dy, ax, ay, bx, by) <= t
                || PointSide2(ax, ay, cx, cy, dx, dy) <= t || PointSide2(bx, by, cx, cy, dx, dy) <= t;
        }

        static double Orient(double ax, double ay, double bx, double by, double cx, double cy) => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

        /// Squared distance from (px, py) to the side a–b.
        static double PointSide2(double px, double py, double ax, double ay, double bx, double by)
        {
            double ex = bx - ax, ey = by - ay, l2 = ex * ex + ey * ey;
            double t = l2 > 0 ? Math.Max(0, Math.Min(1, ((px - ax) * ex + (py - ay) * ey) / l2)) : 0;
            double qx = ax + t * ex - px, qy = ay + t * ey - py;
            return qx * qx + qy * qy;
        }

        /// Convex hull (Andrew's monotone chain) of the points s.Order[0..m) into s.Hull, counter-clockwise; collinear
        /// points on a hull side are left out. Returns the corner count. Allocation-free.
        static int ConvexHull(Scratch s, int m)
        {
            var idx = s.Sorted; var x = s.X; var y = s.Y; var h = s.Hull;
            for (int k = 0; k < m; k++) idx[k] = s.Order[k];
            for (int k = 1; k < m; k++)   // insertion sort by (x, y): m ≤ 64
            {
                int t = idx[k], j = k - 1;
                while (j >= 0 && (x[idx[j]] > x[t] || (x[idx[j]] == x[t] && y[idx[j]] > y[t]))) { idx[j + 1] = idx[j]; j--; }
                idx[j + 1] = t;
            }
            double Turn(int o, int a, int b) => (x[a] - x[o]) * (y[b] - y[o]) - (y[a] - y[o]) * (x[b] - x[o]);
            const double eps = 1e-12;
            int count = 0;
            for (int k = 0; k < m; k++) { while (count >= 2 && Turn(h[count - 2], h[count - 1], idx[k]) <= eps) count--; h[count++] = idx[k]; }
            int lower = count + 1;
            for (int k = m - 2; k >= 0; k--) { while (count >= lower && Turn(h[count - 2], h[count - 1], idx[k]) <= eps) count--; h[count++] = idx[k]; }
            return count - 1;   // the last point repeats the first
        }

        // ---------------- area label ----------------

        /// Where a shape's area label goes: its area centroid when that sits well inside the outline, else the inside
        /// point farthest from every side (a grid-searched pole of inaccessibility, pulled toward the centroid) — the
        /// centroid of an L or a U can land in the notch or hug an inner edge. A distance: its midpoint; a crossed
        /// outline: the mean of its corners. In the points' space. Allocation-free (the live preview calls it every frame).
        public static Vector3 LabelPoint(IReadOnlyList<Vector3> p, in PointMeasurement m)
        {
            int count = m.OutlineCount;
            if (count == 0) return Vector3.zero;
            var mean = Vector3.zero;
            for (int k = 0; k < count; k++) mean += p[m.Outline[k]];
            mean /= count;
            if (count < 3 || m.SelfIntersecting || m.Normal.sqrMagnitude < 0.5f) return mean;

            // A frame in the plane: u along the first side (flattened), v = normal × u.
            var nrm = new Vector3d(m.Normal);
            var o = new Vector3d(mean);
            var u = Flat(new Vector3d(p[m.Outline[1]]) - new Vector3d(p[m.Outline[0]]), nrm);
            if (u.Magnitude < 1e-9) return mean;
            u = u / u.Magnitude;
            var v = Vector3d.Cross(nrm, u);
            var s = Scratch.Get();
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int k = 0; k < count; k++)
            {
                var q = new Vector3d(p[m.Outline[k]]) - o;
                double x = Vector3d.Dot(q, u), y = Vector3d.Dot(q, v);
                s.LX[k] = x; s.LY[k] = y;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }

            // Area centroid (shoelace).
            double a2 = 0, cx = 0, cy = 0;
            for (int k = 0; k < count; k++)
            {
                int k1 = (k + 1) % count;
                double cr = s.LX[k] * s.LY[k1] - s.LX[k1] * s.LY[k];
                a2 += cr; cx += (s.LX[k] + s.LX[k1]) * cr; cy += (s.LY[k] + s.LY[k1]) * cr;
            }
            if (Math.Abs(a2) < 1e-12) return mean;
            cx /= 3 * a2; cy /= 3 * a2;
            double centroidRoom = Room(s.LX, s.LY, count, cx, cy);

            // Grid search for the roomiest inside point (16 × 16 over the bounds, then 8 × 8 around the best cell).
            double size = Math.Max(maxX - minX, maxY - minY);
            if (size <= 0) return mean;
            double bx = cx, by = cy, bestRoom = centroidRoom, bestScore = double.MinValue;
            const int grid = 16;
            double cell = size / grid;
            for (int iy = 0; iy < grid; iy++)
            for (int ix = 0; ix < grid; ix++)
                Try(minX + (ix + 0.5) * cell, minY + (iy + 0.5) * cell);
            double fine = cell / 4, fx = bx, fy = by;
            for (int iy = -4; iy < 4; iy++)
            for (int ix = -4; ix < 4; ix++)
                Try(fx + (ix + 0.5) * fine, fy + (iy + 0.5) * fine);

            void Try(double x, double y)
            {
                double room = Room(s.LX, s.LY, count, x, y);
                if (room <= 0) return;
                // Among near-equal rooms prefer the one nearest the centroid (a U's label sits in the middle of its base).
                double score = room - 0.1 * Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (score > bestScore) { bestScore = score; bestRoom = room; bx = x; by = y; }
            }

            if (centroidRoom > 0 && centroidRoom >= 0.5 * bestRoom) { bx = cx; by = cy; }   // convex-ish: the centroid, as always
            else if (bestScore == double.MinValue) return mean;
            return (o + u * bx + v * by).ToVector3();
        }

        /// Distance from (x, y) to the nearest side if it is inside the polygon (even-odd), else −1.
        static double Room(double[] xs, double[] ys, int count, double x, double y)
        {
            bool inside = false;
            double d2 = double.MaxValue;
            for (int k = 0, j = count - 1; k < count; j = k++)
            {
                if ((ys[k] > y) != (ys[j] > y) && x < (xs[j] - xs[k]) * (y - ys[k]) / (ys[j] - ys[k]) + xs[k]) inside = !inside;
                d2 = Math.Min(d2, PointSide2(x, y, xs[j], ys[j], xs[k], ys[k]));
            }
            return inside ? Math.Sqrt(d2) : -1;
        }

        /// Reused working arrays (Measure runs every frame for the live preview; Unity calls it on the main thread,
        /// tests on theirs).
        sealed class Scratch
        {
            public readonly double[] X = new double[MaxPoints], Y = new double[MaxPoints];
            public readonly double[] LX = new double[MaxPoints], LY = new double[MaxPoints];
            public readonly int[] Order = new int[MaxPoints], Sorted = new int[MaxPoints], Hull = new int[2 * MaxPoints + 1];
            [ThreadStatic] static Scratch s_Instance;
            public static Scratch Get() => s_Instance ??= new Scratch();
        }

        /// Minimal double-precision vector so millimetre results don't depend on float rounding at 10 m scale.
        struct Vector3d
        {
            public double x, y, z;
            public Vector3d(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
            public Vector3d(Vector3 v) { x = v.x; y = v.y; z = v.z; }
            public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d(a.x + b.x, a.y + b.y, a.z + b.z);
            public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.x - b.x, a.y - b.y, a.z - b.z);
            public static Vector3d operator *(Vector3d a, double s) => new Vector3d(a.x * s, a.y * s, a.z * s);
            public static Vector3d operator /(Vector3d a, double s) => new Vector3d(a.x / s, a.y / s, a.z / s);
            public static Vector3d Cross(Vector3d a, Vector3d b) =>
                new Vector3d(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
            public static double Dot(Vector3d a, Vector3d b) => a.x * b.x + a.y * b.y + a.z * b.z;
            public double Magnitude => Math.Sqrt(x * x + y * y + z * z);
            public Vector3 ToVector3() => new Vector3((float)x, (float)y, (float)z);
        }
    }
}
