using System;
using System.Collections.Generic;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// The capture coach's ring and legs (docs/api.md §5 show_coverage), exactly as the contract states it: every
    /// point is computed in the scene frame (glTF, doubles), and only then goes through the same glTF→Unity X flip as
    /// the mesh (GltfFrame, x → −x). Bearings run clockwise seen from above in the scene frame:
    /// dir(b) = cos(b)·zero_dir + sin(b)·quarter_dir, p(b, r, y) = ring.centre + r·dir(b) + (0, y, 0).
    /// Results are in the scene content's local space (the package frame). Pure; unit-tested with the facade's cameras.
    public static class CoverageGeometry
    {
        public const double InnerFraction = 0.7;
        public const double HalfWedgeDeg = 22.5;
        /// Wedge labels sit between the inner and outer radius.
        public const double LabelFraction = 0.85;

        const double Deg = Math.PI / 180.0;

        /// dir(b) in the scene frame (glTF).
        public static double[] Dir(CoverageView c, double bearingDeg)
        {
            double b = bearingDeg * Deg, cb = Math.Cos(b), sb = Math.Sin(b);
            var z = c.ZeroDir; var q = c.QuarterDir;
            return new[] { cb * z[0] + sb * q[0], cb * z[1] + sb * q[1], cb * z[2] + sb * q[2] };
        }

        /// p(b, r, y) in the scene frame (glTF).
        public static double[] P(CoverageView c, double bearingDeg, double r, double y)
        {
            var d = Dir(c, bearingDeg);
            var o = c.RingCentre;
            return new[] { o[0] + r * d[0], o[1] + r * d[1] + y, o[2] + r * d[2] };
        }

        /// The flip, applied last: scene frame → the content's local space.
        public static Vector3 Flip(double[] p) => GltfFrame.ToUnity(p[0], p[1], p[2]);

        /// p(b, r, y), flipped: where Unity draws it.
        public static Vector3 PU(CoverageView c, double bearingDeg, double r, double y) => Flip(P(c, bearingDeg, r, y));

        public static double WedgeStart(CoverageSide s) => s.BearingDeg - HalfWedgeDeg;
        public static double WedgeEnd(CoverageSide s) => s.BearingDeg + HalfWedgeDeg;

        /// Wedge k (sides[k]) as a flat strip on the ground between 0.7·radius and radius, from bearing − 22.5° to + 22.5°:
        /// (inner, outer) vertex pairs, `steps` + 1 of them, and the triangles between them (both faces drawn). `lift`
        /// raises it off the ground mesh (scene units).
        public static void Wedge(CoverageView c, int k, int steps, double lift, List<Vector3> verts, List<int> tris)
        {
            verts.Clear();
            tris.Clear();
            if (!c.HasRing || k < 0 || k >= c.Sides.Count) return;
            steps = Math.Max(1, steps);
            var s = c.Sides[k];
            double r0 = c.RingRadius * InnerFraction, r1 = c.RingRadius;
            for (int i = 0; i <= steps; i++)
            {
                double b = WedgeStart(s) + (WedgeEnd(s) - WedgeStart(s)) * i / steps;
                verts.Add(PU(c, b, r0, lift));
                verts.Add(PU(c, b, r1, lift));
            }
            for (int i = 0; i < steps; i++)
            {
                int a = i * 2;
                tris.Add(a); tris.Add(a + 1); tris.Add(a + 3);
                tris.Add(a); tris.Add(a + 3); tris.Add(a + 2);
            }
        }

        /// The middle of wedge k (its label's anchor), flipped.
        public static Vector3 WedgeCentre(CoverageView c, int k, double lift) =>
            PU(c, c.Sides[k].BearingDeg, c.RingRadius * LabelFraction, lift);

        /// orbit / eave_pass: p(from + t, radius, altitude) for t in 0 … sweep, every ~stepDeg, flipped.
        public static List<Vector3> Arc(CoverageView c, CoverageLeg leg, double stepDeg = 3.0)
        {
            var list = new List<Vector3>();
            if (!c.HasRing || leg == null) return list;
            double from = leg.FromDeg ?? 0.0, sweep = leg.SweepDeg ?? 360.0;
            int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / Math.Max(0.5, stepDeg)));
            for (int i = 0; i <= n; i++) list.Add(PU(c, from + sweep * i / n, leg.Radius, leg.Altitude));
            // A full circle closes on itself; the dasher is told it's closed, so drop the duplicate end.
            if (Math.Abs(Math.Abs(sweep) - 360.0) < 1e-6 && list.Count > 2) list.RemoveAt(list.Count - 1);
            return list;
        }

        public static bool IsFullCircle(CoverageLeg leg) => leg != null && leg.IsArc && Math.Abs(Math.Abs(leg.SweepDeg ?? 360.0) - 360.0) < 1e-6;

        /// nadir_grid: a square of half-size `radius` around ring.centre at `altitude`, sides along zero_dir / quarter_dir
        /// (4 corners, closed), flipped.
        public static List<Vector3> NadirSquare(CoverageView c, CoverageLeg leg)
        {
            var list = new List<Vector3>();
            if (!c.HasRing || leg == null) return list;
            double r = leg.Radius;
            var z = c.ZeroDir; var q = c.QuarterDir; var o = c.RingCentre;
            foreach (var (sz, sq) in new[] { (1.0, 1.0), (1.0, -1.0), (-1.0, -1.0), (-1.0, 1.0) })
                list.Add(Flip(new[]
                {
                    o[0] + r * (sz * z[0] + sq * q[0]),
                    o[1] + r * (sz * z[1] + sq * q[1]) + leg.Altitude,
                    o[2] + r * (sz * z[2] + sq * q[2]),
                }));
            return list;
        }

        /// Where the leg's drone icon flies: the arc's middle, or above the centre for the nadir grid (scene frame).
        public static double[] DronePointScene(CoverageView c, CoverageLeg leg)
        {
            if (leg.IsArc) return P(c, (leg.FromDeg ?? 0.0) + (leg.SweepDeg ?? 360.0) * 0.5, leg.Radius, leg.Altitude);
            var o = c.RingCentre;
            return new[] { o[0], o[1] + leg.Altitude, o[2] };
        }

        public static Vector3 DronePoint(CoverageView c, CoverageLeg leg) => Flip(DronePointScene(c, leg));

        /// The drone camera's view: horizontally toward the ring's centre, tilted down by gimbal_pitch_deg (90 = straight
        /// down). Computed in the scene frame, flipped last (a direction flips like a point).
        public static Vector3 DroneForward(CoverageView c, CoverageLeg leg)
        {
            var at = DronePointScene(c, leg);
            var o = c.RingCentre;
            double hx = o[0] - at[0], hz = o[2] - at[2], h = Math.Sqrt(hx * hx + hz * hz);
            if (h < 1e-9) { hx = c.ZeroDir[0]; hz = c.ZeroDir[2]; h = Math.Sqrt(hx * hx + hz * hz); if (h < 1e-9) { hx = 1; h = 1; } }
            double pitch = leg.GimbalPitchDeg * Deg;
            double cp = Math.Cos(pitch), sp = Math.Sin(pitch);
            return Flip(new[] { cp * hx / h, -sp, cp * hz / h });
        }

        /// The sides the ring shows as seen (green), for logs and the harness.
        public static int SeenCount(CoverageView c)
        {
            int n = 0;
            foreach (var s in c.Sides) if (s.Seen) n++;
            return n;
        }
    }
}
