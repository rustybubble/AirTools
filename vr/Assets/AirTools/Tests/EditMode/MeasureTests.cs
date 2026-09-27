using System;
using System.Linq;
using AirTools.Core;
using AirTools.Tools;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    public class UnitsTests
    {
        [Test] public void SpecExample() => Assert.AreEqual("4.20 m · 13′ 9⅜″", Units.Format(4.2));
        [Test] public void WindowWidth() => Assert.AreEqual("1.50 m · 4′ 11″", Units.Format(1.5)); // 59.055″
        [Test] public void ExactFoot() => Assert.AreEqual("0.30 m · 1′ 0″", Units.Format(0.3048));
        [Test] public void RoundingCarriesIntoNextFoot() => Assert.AreEqual("11⅞″", Units.FeetInches(11.9 * 0.0254)); // 11.9" → 11⅞" (no "0′")
        [Test] public void UnderAFootHasNoZeroFeet() => Assert.AreEqual("0.26 m · 10¼″", Units.Format(0.26));
        [Test] public void NegativesUseARealMinus()
        {
            Assert.AreEqual("\u22120.26 m", Units.Metric(-0.26));
            Assert.AreEqual("\u22121′ 0″", Units.FeetInches(-0.3048));
            Assert.AreEqual("0.00 m", Units.Metric(-0.0001), "no minus zero");
        }
        [Test] public void RoundingCarriesToWholeFoot() => Assert.AreEqual("1′ 0″", Units.FeetInches(11.97 * 0.0254));
        [Test] public void Area() => Assert.AreEqual("1.80 m² · 19.4 ft²", Units.FormatArea(1.8));
        [Test] public void Angle() => Assert.AreEqual("90.0°", Units.FormatAngle(89.96));

        // ---------------- D2 (SPEC §9): one unit per label, imperial first ----------------

        const UnitSystem Imp = UnitSystem.Imperial, Met = UnitSystem.Metric;

        [TestCase(1.5003, "4′ 11⅛″", "1.50 m")]        // 59.067″: rounds up to the next ⅛
        [TestCase(1.5, "4′ 11″", "1.50 m")]            // 59.055″: rounds down
        [TestCase(4.2, "13′ 9⅜″", "4.20 m")]
        [TestCase(0.26, "10¼″", "0.26 m")]             // under a foot: inches only
        [TestCase(0.3048, "1′ 0″", "0.30 m")]
        [TestCase(1.5227, "5′ 0″", "1.52 m")]          // 4′ 11.95″ carries into the next foot
        [TestCase(0.0, "0″", "0.00 m")]
        [TestCase(0.001, "0″", "0.00 m")]              // tiny: under 1/16″
        [TestCase(0.0016, "⅛″", "0.00 m")]             // just over 1/16″
        [TestCase(-0.3048, "−1′ 0″", "−0.30 m")]
        [TestCase(-0.26, "−10¼″", "−0.26 m")]
        [TestCase(-0.0001, "0″", "0.00 m")]            // no minus zero in either unit
        public void FormatPrimary(double metres, string imperial, string metric)
        {
            Assert.AreEqual(imperial, Units.FormatPrimary(metres, Imp));
            Assert.AreEqual(metric, Units.FormatPrimary(metres, Met));
            Assert.AreEqual(metric, Units.FormatSecondary(metres, Imp));
            Assert.AreEqual(imperial, Units.FormatSecondary(metres, Met));
        }

        [Test]
        public void FormatPrimaryShowsOneUnit()
        {
            foreach (var m in new[] { 0.0, 0.004, 0.26, 1.5, 4.2, 12.7, -2.0 })
            {
                string imp = Units.FormatPrimary(m, Imp), met = Units.FormatPrimary(m, Met);
                StringAssert.DoesNotContain(" m", imp, imp);
                StringAssert.DoesNotContain("·", imp, imp);
                StringAssert.DoesNotContain("″", met, met);
                StringAssert.DoesNotContain("·", met, met);
            }
        }

        [Test]
        public void NotANumberIsADash()
        {
            foreach (var u in new[] { Imp, Met })
            {
                Assert.AreEqual("—", Units.FormatPrimary(double.NaN, u));
                Assert.AreEqual("—", Units.FormatPrimary(double.PositiveInfinity, u));
                Assert.AreEqual("—", Units.FormatAreaPrimary(double.NaN, u));
                Assert.AreEqual("—", Units.FormatPair(double.NaN, 1.0, u));
            }
        }

        [Test]
        public void BothUnitsPrimaryFirst()
        {
            Assert.AreEqual("4′ 11″ · 1.50 m", Units.FormatBoth(1.5, Imp));
            Assert.AreEqual(Units.Format(1.5), Units.FormatBoth(1.5, Met), "metric = the dual Format (export, logs)");
            Assert.AreEqual(Units.Format(0.26), Units.FormatBoth(0.26, Met));
        }

        [TestCase(1.8, "19 ft²", "1.80 m²")]           // 19.38 ft²: whole square feet from 10 up
        [TestCase(0.3, "3.2 ft²", "0.30 m²")]          // one decimal below 10
        [TestCase(0.09, "1.0 ft²", "0.09 m²")]
        [TestCase(0.92, "9.9 ft²", "0.92 m²")]
        [TestCase(0.925, "10 ft²", "0.93 m²")]          // 9.96 ft² → "10", never "10.0"
        [TestCase(0.0, "0.0 ft²", "0.00 m²")]
        public void AreaPrimary(double m2, string imperial, string metric)
        {
            Assert.AreEqual(imperial, Units.FormatAreaPrimary(m2, Imp));
            Assert.AreEqual(metric, Units.FormatAreaPrimary(m2, Met));
        }

        [Test]
        public void AreaBoth()
        {
            Assert.AreEqual("19.4 ft² · 1.80 m²", Units.FormatAreaBoth(1.8, Imp));
            Assert.AreEqual(Units.FormatArea(1.8), Units.FormatAreaBoth(1.8, Met));
        }

        [TestCase(25.0, "1″", "25 mm")]
        [TestCase(38.0, "1½″", "38 mm")]
        [TestCase(600.0, "1′ 11⅝″", "600 mm")]
        [TestCase(4073.0, "13′ 4⅜″", "4073 mm")]
        [TestCase(0.4, "0″", "0 mm")]
        public void SmallLengthsFromMm(double mm, string imperial, string metric)
        {
            Assert.AreEqual(imperial, Units.FormatMm(mm, Imp));
            Assert.AreEqual(metric, Units.FormatMm(mm, Met));
        }

        [Test]
        public void PairsAndRanges()
        {
            Assert.AreEqual("262 × 279 mm", Units.FormatPair(0.262, 0.279, Met));
            Assert.AreEqual("10⅜ × 11″", Units.FormatPair(0.262, 0.279, Imp), "cabinet door: inches, as sold");
            Assert.AreEqual("59 × 47¼″", Units.FormatPair(1.5, 1.2, Imp), "window: inches under 8 ft");
            Assert.AreEqual("16′ 0⅞″ × 6′ 10⅝″", Units.FormatPair(4.9, 2.1, Imp), "garage door: feet and inches");
            Assert.AreEqual("1500 × 1200 mm", Units.PairMm(1.5, 1.2), "raw: always mm");
            Assert.AreEqual("590–1000 mm", Units.FormatRangeMm(590, 1000, Met));
            Assert.AreEqual("23¼–39⅜″", Units.FormatRangeMm(590, 1000, Imp));
            Assert.AreEqual("≥ 590 mm", Units.FormatRangeMm(590, null, Met));
            Assert.AreEqual("≥ 23¼″", Units.FormatRangeMm(590, null, Imp));
        }

        [Test]
        public void WeightsAndTheKeypad()
        {
            Assert.AreEqual("60 g", Units.FormatWeight(60, Met));
            Assert.AreEqual("1.5 kg", Units.FormatWeight(1500, Met));
            Assert.AreEqual("2.1 oz", Units.FormatWeight(60, Imp));
            Assert.AreEqual("14 oz", Units.FormatWeight(400, Imp));
            Assert.AreEqual("1.0 lb", Units.FormatWeight(450, Imp));
            Assert.AreEqual("3.3 lb", Units.FormatWeight(1500, Imp));
            Assert.AreEqual("inches", Units.KeypadUnit(Imp));
            Assert.AreEqual("cm", Units.KeypadUnit(Met));
            Assert.AreEqual(0.6096, Units.KeypadMetres(24, Imp), 1e-9, "a 24″ dishwasher");
            Assert.AreEqual(0.6096, Units.KeypadMetres(60.96, Met), 1e-9);
        }

        [Test]
        public void LengthsInsideServerText()
        {
            Assert.AreEqual("tape #3 13′ 9⅜″ ÷ 1′ 11⅝″ → 8 (reported by headset)",
                Units.ConvertLengths("tape #3 4.20 m ÷ 600 mm → 8 (reported by headset)", Imp));
            Assert.AreEqual("green, 1½″ spare", Units.ConvertLengths("green, 38 mm spare", Imp));
            Assert.AreEqual("only 2′ 0⅜″ above the edge — needs 2′ 11⅜″", Units.ConvertLengths("only 0.62 m above the edge — needs 0.9 m", Imp));
            const string untouched = "1.80 m² · wait 5 min · $4.20 · 2026-10-02 · ×1.49";
            Assert.AreEqual(untouched, Units.ConvertLengths(untouched, Imp), "areas, words, prices, dates, factors");
            Assert.AreEqual("tape #3 4.20 m ÷ 600 mm → 8", Units.ConvertLengths("tape #3 4.20 m ÷ 600 mm → 8", Met), "metric: as written");
            Assert.AreEqual("", Units.ConvertLengths(null, Imp));
        }

        /// Every glyph the D2 formatters print (both units) is in the Inter atlas charset (a missing one draws a box).
        [Test]
        public void D2GlyphsAreInTheCharset()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var u in new[] { Imp, Met })
            {
                for (int eighth = 0; eighth < 8 * 14; eighth++) sb.Append(Units.FormatPrimary(-eighth * 0.0254 / 8.0, u)).Append(Units.FormatBoth(eighth * 0.0254 / 8.0, u));
                sb.Append(Units.FormatAreaPrimary(1.8, u)).Append(Units.FormatAreaBoth(-0.3, u)).Append(Units.FormatPair(0.262, 0.279, u))
                  .Append(Units.FormatPair(4.9, 2.1, u)).Append(Units.FormatRangeMm(590, 1000, u)).Append(Units.FormatRangeMm(590, null, u))
                  .Append(Units.FormatWeight(60, u)).Append(Units.FormatWeight(1500, u)).Append(Units.KeypadUnit(u)).Append(Units.FormatMm(600, u))
                  .Append(Units.ConvertLengths("tape 4.20 m ÷ 600 mm", u)).Append(Units.FormatPrimary(double.NaN, u));
            }
            foreach (char ch in sb.ToString())
                Assert.IsTrue(AirTools.Editor.UiAssetsBuilder.Charset.IndexOf(ch) >= 0, $"'{ch}' (U+{(int)ch:X4}) is not in UiAssetsBuilder.Charset");
        }
    }

    public class MeasureMathTests
    {
        const double Mm = 1e-4;

        [Test]
        public void TwoPointsIsDistance()
        {
            var m = MeasureMath.Measure(new[] { new Vector3(-0.75f, 4.1f, -0.1f), new Vector3(0.75f, 4.1f, -0.1f) });
            Assert.IsTrue(m.IsDistance);
            Assert.AreEqual(1.5, m.Distance, Mm);
            Assert.IsEmpty(m.Angles);
            Assert.AreEqual(0, m.Area);
        }

        [Test]
        public void RightTriangle345()
        {
            var m = MeasureMath.Measure(new[] { Vector3.zero, new Vector3(3, 0, 0), new Vector3(3, 4, 0) });
            CollectionAssert.AreEqual(new[] { 3.0, 4.0, 5.0 }, m.Sides.Select(s => System.Math.Round(s, 6)));
            Assert.AreEqual(53.1301, m.Angles[0], 1e-3); // at (0,0): cos = 9/15
            Assert.AreEqual(90.0, m.Angles[1], 1e-3);
            Assert.AreEqual(36.8699, m.Angles[2], 1e-3); // at (3,4)
            Assert.AreEqual(180.0, m.Angles.Sum(), 1e-6);
            Assert.AreEqual(6.0, m.Area, 1e-6);
            Assert.AreEqual(12.0, m.Perimeter, 1e-6);
        }

        [Test]
        public void TriangleInAnyOrientation()
        {
            // Same 3-4-5 triangle, rotated arbitrarily and moved away from the origin.
            var r = Quaternion.Euler(33f, -71f, 12f);
            var o = new Vector3(5f, 2f, -7f);
            var m = MeasureMath.Measure(new[] { o + r * Vector3.zero, o + r * new Vector3(3, 0, 0), o + r * new Vector3(3, 4, 0) });
            Assert.AreEqual(6.0, m.Area, 1e-4);
            Assert.AreEqual(90.0, m.Angles[1], 1e-3);
        }

        [Test]
        public void WindowRectangleOnTheWall()
        {
            // Four corners of the synthetic window (1.5 × 1.2 m, in the XY plane).
            var m = MeasureMath.Measure(new[]
            {
                new Vector3(-0.75f, 3.5f, -0.1f), new Vector3(0.75f, 3.5f, -0.1f),
                new Vector3(0.75f, 4.7f, -0.1f), new Vector3(-0.75f, 4.7f, -0.1f),
            });
            Assert.AreEqual(1.8, m.Area, 1e-4);
            foreach (var a in m.Angles) Assert.AreEqual(90.0, a, 1e-3);
            Assert.AreEqual(1.5, m.Sides[0], Mm);
            Assert.AreEqual(1.2, m.Sides[1], Mm);
            Assert.AreEqual(0.0, m.PlanarityError, 1e-5);
            Assert.AreEqual(1.0, Mathf.Abs(m.Normal.z), 1e-4, "normal points out of the wall");
        }

        [Test]
        public void WindingDirectionDoesNotMatter()
        {
            var cw = MeasureMath.Measure(new[] { Vector3.zero, new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 0, 0) });
            var ccw = MeasureMath.Measure(new[] { Vector3.zero, new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) });
            Assert.AreEqual(1.0, cw.Area, 1e-6);
            Assert.AreEqual(1.0, ccw.Area, 1e-6);
            CollectionAssert.AreEqual(cw.Angles.Select(a => System.Math.Round(a, 4)), ccw.Angles.Select(a => System.Math.Round(a, 4)));
        }

        [Test]
        public void ArrowheadKeepsItsReflexCorner()
        {
            // Concave polygons follow the clicks (the convex hull used to swallow the dart's inside corner at (0.5, 1)).
            var m = MeasureMath.Measure(new[] { new Vector3(0, 0, 0), new Vector3(2, 1, 0), new Vector3(0, 2, 0), new Vector3(0.5f, 1, 0) });
            Assert.AreEqual(4, m.PointCount);
            Assert.AreEqual(4, m.OutlineCount);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, m.Outline);
            Assert.AreEqual(360.0, m.Angles.Sum(), 1e-3);
            Assert.Greater(m.Angles[3], 180.0);
            Assert.AreEqual(1.5, m.Area, 1e-4); // 2 triangles: 0.5*2*2 - 0.5*2*0.5
            Assert.AreEqual(OutlineCrossing.None, m.Crossing);
        }

        [Test]
        public void FivePointPolygonHasNoCap()
        {
            // A 2 × 1 m rectangle with a 0.5 m peak on top (a house end), placed in order.
            var m = MeasureMath.Measure(new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 1, 0), new Vector3(1, 1.5f, 0), new Vector3(0, 1, 0) });
            Assert.AreEqual(5, m.OutlineCount);
            Assert.AreEqual(2.5, m.Area, 1e-4);
            Assert.AreEqual(540.0, m.Angles.Sum(), 1e-3);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, m.Outline, "placement order kept for a convex shape");
            Assert.AreSame(m.Outline, m.Hull, "the old name still reads the outline");
            Assert.AreEqual(5, m.HullCount);
        }

        [Test]
        public void ClickOrderDoesNotMatter()
        {
            var a = new Vector3(-0.75f, 3.5f, -0.1f); var b = new Vector3(0.75f, 3.5f, -0.1f);
            var c = new Vector3(0.75f, 4.7f, -0.1f); var d = new Vector3(-0.75f, 4.7f, -0.1f);
            var crossed = MeasureMath.Measure(new[] { a, c, b, d });   // a bow-tie in click order
            Assert.AreEqual(1.8, crossed.Area, 1e-4, "hull, not a self-intersecting bow-tie");
            foreach (var x in crossed.Angles) Assert.AreEqual(90.0, x, 1e-3);
            // Every point is a hull corner, so the hull is the only outline that doesn't cross: it's used, from the first
            // click, toward the earlier-placed neighbour.
            Assert.IsTrue(crossed.Reordered);
            Assert.AreEqual(OutlineCrossing.None, crossed.Crossing);
            CollectionAssert.AreEqual(new[] { 0, 2, 1, 3 }, crossed.Outline);
            Assert.AreEqual(1.5, crossed.Sides[0], Mm, "a → b");
            Assert.AreEqual(5.4, crossed.Perimeter, Mm);
        }

        [Test]
        public void CollinearPointsAreADistance()
        {
            var m = MeasureMath.Measure(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(3, 0, 0) });
            Assert.IsTrue(m.IsDistance);
            Assert.AreEqual(3.0, m.Distance, 1e-6);
            CollectionAssert.AreEqual(new[] { 0, 2 }, m.Hull);
        }

        [Test]
        public void NonPlanarQuadReportsPlanarityError()
        {
            var m = MeasureMath.Measure(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0.1f), new Vector3(0, 1, 0) });
            Assert.Greater(m.PlanarityError, 0.02);
            Assert.Less(m.PlanarityError, 0.03);
            Assert.AreEqual(1.0, m.Area, 0.01);
        }

        [Test]
        public void CollinearTriangleHasZeroArea()
        {
            var m = MeasureMath.Measure(new[] { Vector3.zero, new Vector3(1, 0, 0), new Vector3(2, 0, 0) });
            Assert.AreEqual(0.0, m.Area, 1e-9);
        }

        [Test]
        public void RejectsWrongPointCounts()
        {
            Assert.Throws<System.ArgumentException>(() => MeasureMath.Measure(new[] { Vector3.zero }));
            Assert.DoesNotThrow(() => MeasureMath.Measure(new Vector3[5]), "5 points is a polygon now (no 4-point cap)");
            Assert.Throws<System.ArgumentException>(() => MeasureMath.Measure(new Vector3[MeasureMath.MaxPoints + 1]));
        }

        // ---------------- concave polygons: click order defines the outline ----------------

        static Vector3[] V(params float[] xy)
        {
            var v = new Vector3[xy.Length / 2];
            for (int i = 0; i < v.Length; i++) v[i] = new Vector3(xy[2 * i], xy[2 * i + 1], 0f);
            return v;
        }

        /// 2 × 2 square minus the 1 × 1 bottom-right corner, counter-clockwise from the origin: the reflex corner is (1, 1).
        static readonly Vector3[] L = V(0, 0, 1, 0, 1, 1, 2, 1, 2, 2, 0, 2);
        /// 3 × 2 minus a 1 × 1 slot from the top: two reflex corners.
        static readonly Vector3[] U = V(0, 0, 3, 0, 3, 2, 2, 2, 2, 1, 1, 1, 1, 2, 0, 2);
        /// A 4 × 2 wall with a 1 × 0.5 notch out of the middle of its top edge, clicked clockwise from the top left.
        static readonly Vector3[] Notched = V(0, 2, 1.5f, 2, 1.5f, 1.5f, 2.5f, 1.5f, 2.5f, 2, 4, 2, 4, 0, 0, 0);
        /// The same wall with a V-notch (45° flanks, 0.5 m deep) instead: two 135° corners and a 270° one.
        static readonly Vector3[] VNotched = V(0, 0, 4, 0, 4, 2, 2.5f, 2, 2, 1.5f, 1.5f, 2, 0, 2);

        static void AssertAngles(double[] expected, PointMeasurement m, double tol = 1e-3)
        {
            Assert.AreEqual(expected.Length, m.Angles.Length, "one angle per corner");
            for (int i = 0; i < expected.Length; i++) Assert.AreEqual(expected[i], m.Angles[i], tol, $"angle {i}: {string.Join(", ", m.Angles.Select(a => a.ToString("0.00")))}");
            Assert.AreEqual((expected.Length - 2) * 180.0, m.Angles.Sum(), Math.Max(1e-6, tol * expected.Length), "(n − 2)·180");
        }

        [Test]
        public void LShapeMeasuresItsOwnOutline()
        {
            var m = MeasureMath.Measure(L);
            Assert.AreEqual(3.0, m.Area, 1e-6, "2 × 2 − 1 × 1, not the hull's 3.5");
            AssertAngles(new double[] { 90, 90, 270, 90, 90, 90 }, m);
            Assert.AreEqual(8.0, m.Perimeter, 1e-6);
            CollectionAssert.AreEqual(new[] { 1.0, 1.0, 1.0, 1.0, 2.0, 2.0 }, m.Sides.Select(x => System.Math.Round(x, 6)));
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, m.Outline);
            Assert.AreEqual(OutlineCrossing.None, m.Crossing);
            Assert.IsFalse(m.Reordered);
            Assert.AreEqual(1.0, Mathf.Abs(m.Normal.z), 1e-5);
            Assert.AreEqual(0.0, m.PlanarityError, 1e-6);
        }

        [Test]
        public void LShapeClickedClockwiseReadsTheSame()
        {
            var cw = L.Reverse().ToArray();   // (0,2) (2,2) (2,1) (1,1) (1,0) (0,0)
            var m = MeasureMath.Measure(cw);
            Assert.AreEqual(3.0, m.Area, 1e-6);
            AssertAngles(new double[] { 90, 90, 90, 270, 90, 90 }, m);
            Assert.AreEqual(8.0, m.Perimeter, 1e-6);
        }

        [Test]
        public void UShapeHasTwoReflexCorners()
        {
            var m = MeasureMath.Measure(U);
            Assert.AreEqual(5.0, m.Area, 1e-6);
            AssertAngles(new double[] { 90, 90, 90, 90, 270, 270, 90, 90 }, m);
            Assert.AreEqual(12.0, m.Perimeter, 1e-6);
            Assert.AreEqual(OutlineCrossing.None, m.Crossing);
        }

        [Test]
        public void NotchedWalls()
        {
            var m = MeasureMath.Measure(Notched);
            Assert.AreEqual(7.5, m.Area, 1e-6);
            AssertAngles(new double[] { 90, 90, 270, 270, 90, 90, 90, 90 }, m);
            Assert.AreEqual(13.0, m.Perimeter, 1e-6);
            var v = MeasureMath.Measure(VNotched);
            Assert.AreEqual(7.75, v.Area, 1e-6);
            AssertAngles(new double[] { 90, 90, 90, 135, 270, 135, 90 }, v);
            Assert.AreEqual(11.0 + 2 * System.Math.Sqrt(0.5), v.Perimeter, 1e-6);
        }

        /// Rotation without the engine (Quaternion.Euler is a native call).
        static Quaternion Q(Vector3 axis, float degrees)
        {
            axis = axis.normalized;
            float h = degrees * Mathf.Deg2Rad * 0.5f, s = Mathf.Sin(h);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(h));
        }

        static Vector3[] Move(Vector3[] pts, Quaternion r, Vector3 o) => pts.Select(p => o + r * p).ToArray();

        [Test]
        public void ConcaveShapesInAnyOrientation()
        {
            // A tilted roof plane, a wall seen from the side, a floor: same numbers, the normal follows the plane.
            var cases = new[]
            {
                (Q(new Vector3(1, 0.3f, -0.2f), 33f), new Vector3(5f, 2f, -7f)),
                (Q(Vector3.up, 90f), new Vector3(-3f, 0.5f, 1f)),
                (Q(Vector3.right, 90f), new Vector3(0f, 0.02f, 0f)),
                (Q(new Vector3(-0.4f, 1f, 0.7f), -121f), new Vector3(12f, 6f, 9f)),
            };
            foreach (var (r, o) in cases)
            {
                var l = MeasureMath.Measure(Move(L, r, o));
                Assert.AreEqual(3.0, l.Area, 1e-4);
                AssertAngles(new double[] { 90, 90, 270, 90, 90, 90 }, l, 1e-2);
                Assert.AreEqual(8.0, l.Perimeter, 1e-4);
                Assert.AreEqual(1.0, Mathf.Abs(Vector3.Dot(l.Normal, r * Vector3.forward)), 1e-4, "normal ⟂ the plane");
                Assert.Less(l.PlanarityError, 1e-4);
                var u = MeasureMath.Measure(Move(U, r, o));
                Assert.AreEqual(5.0, u.Area, 1e-4);
                AssertAngles(new double[] { 90, 90, 90, 90, 270, 270, 90, 90 }, u, 1e-2);
                var n = MeasureMath.Measure(Move(Notched, r, o));
                Assert.AreEqual(7.5, n.Area, 1e-4);
                AssertAngles(new double[] { 90, 90, 270, 270, 90, 90, 90, 90 }, n, 1e-2);
            }
        }

        [Test]
        public void SlightlyOffPlaneConcaveShape()
        {
            // Snapped points a few millimetres off one surface (scan noise): the best-fit plane still reads the L.
            var jitter = new[] { 0.004f, -0.003f, 0.002f, -0.004f, 0.003f, -0.002f };
            var r = Q(new Vector3(0.2f, 1f, 0.1f), 40f);
            var pts = L.Select((p, i) => r * (p + new Vector3(0, 0, jitter[i]))).ToArray();
            var m = MeasureMath.Measure(pts);
            Assert.AreEqual(3.0, m.Area, 0.01);
            AssertAngles(new double[] { 90, 90, 270, 90, 90, 90 }, m, 0.5);
            Assert.AreEqual(OutlineCrossing.None, m.Crossing);
            Assert.Greater(m.PlanarityError, 0.001);
            Assert.Less(m.PlanarityError, 0.01, "under the 1 cm 'off one surface' warning");
        }

        [Test]
        public void BowTieIsFlaggedNotMeasured()
        {
            // Square corners in bow-tie order plus a point inside: no hull reorder (not every point is a corner), no area.
            var m = MeasureMath.Measure(V(0, 0, 2, 2, 2, 0, 0, 2, 1, 0.2f));
            Assert.IsTrue(m.SelfIntersecting);
            Assert.AreEqual(OutlineCrossing.Path, m.Crossing, "the placed path itself crosses");
            Assert.IsFalse(m.Reordered);
            Assert.AreEqual(0.0, m.Area, "no silent wrong area");
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, m.Outline, "drawn as clicked, so the crossing shows");
            Assert.AreEqual(5, m.Sides.Length);
            Assert.AreEqual(1.0, m.Normal.magnitude, 1e-5);

            // An L with its last two corners swapped: the path is fine, only closing it would cross.
            var closing = MeasureMath.Measure(V(0, 0, 1, 0, 1, 1, 2, 1, 0, 2, 2, 2));
            Assert.AreEqual(OutlineCrossing.Closing, closing.Crossing);
            Assert.AreEqual(0.0, closing.Area);
        }

        [Test]
        public void FigureEightThroughOneCornerCounts()
        {
            // Back through (0, 0) the other way round: the two loops' areas would cancel. Touching sides count as crossing.
            var m = MeasureMath.Measure(V(0, 0, 1, 0, 1, 1, 0, 0, -1, -1, -1, 0));
            Assert.IsTrue(m.SelfIntersecting);
            Assert.AreEqual(0.0, m.Area);
        }

        [Test]
        public void OutOfOrderQuadsAreReorderedOnlyWhenConvex()
        {
            // Corners 1, 3, 2, 4 of a 2 × 1 rectangle on a floor: reordered to the rectangle.
            var a = new Vector3(0, 0, 0); var b = new Vector3(2, 0, 0); var c = new Vector3(2, 0, 1); var d = new Vector3(0, 0, 1);
            foreach (var order in new[] { new[] { a, c, b, d }, new[] { a, b, d, c }, new[] { c, a, b, d } })
            {
                var m = MeasureMath.Measure(order);
                Assert.IsTrue(m.Reordered, string.Join(" ", order));
                Assert.AreEqual(2.0, m.Area, 1e-6);
                foreach (var x in m.Angles) Assert.AreEqual(90.0, x, 1e-4);
                Assert.AreEqual(6.0, m.Perimeter, 1e-6);
                Assert.AreEqual(0, m.Outline[0], "starts at the first click");
            }
            // In order: kept as clicked.
            Assert.IsFalse(MeasureMath.Measure(new[] { a, b, c, d }).Reordered);
            // A concave quad (one point inside the others' triangle) can't cross in any order: every order is its own dart.
            var dart = V(0, 0, 2, 1, 0, 2, 0.5f, 1);
            foreach (var order in new[] { new[] { 0, 1, 2, 3 }, new[] { 0, 2, 1, 3 }, new[] { 0, 1, 3, 2 } })
            {
                var m = MeasureMath.Measure(order.Select(i => dart[i]).ToArray());
                Assert.AreEqual(OutlineCrossing.None, m.Crossing, string.Join(",", order));
                Assert.IsFalse(m.Reordered);
                Assert.AreEqual(360.0, m.Angles.Sum(), 1e-3);
            }
            // A convex pentagon clicked out of order: its hull is still the only outline that doesn't cross.
            var house = V(0, 0, 2, 0, 2, 1, 1, 1.5f, 0, 1);
            var p = MeasureMath.Measure(new[] { house[0], house[2], house[1], house[3], house[4] });
            Assert.IsTrue(p.Reordered);
            Assert.AreEqual(2.5, p.Area, 1e-4);
        }

        [Test]
        public void TriangleIsAlwaysSimple()
        {
            foreach (var order in new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 2, 1, 0 } })
            {
                var t = V(0, 0, 3, 0, 3, 4);
                var m = MeasureMath.Measure(order.Select(i => t[i]).ToArray());
                Assert.AreEqual(OutlineCrossing.None, m.Crossing);
                Assert.IsFalse(m.Reordered);
                Assert.AreEqual(6.0, m.Area, 1e-6);
                Assert.AreEqual(180.0, m.Angles.Sum(), 1e-6);
            }
        }

        [Test]
        public void RepeatedPointsAreOneCorner()
        {
            // The live preview's cursor sits on the last point right after a click; a click on the first point closes.
            var pts = L.Concat(new[] { L[5] + new Vector3(0.0003f, 0, 0) }).ToArray();
            var m = MeasureMath.Measure(pts);
            Assert.AreEqual(7, m.PointCount);
            Assert.AreEqual(6, m.OutlineCount);
            Assert.AreEqual(3.0, m.Area, 1e-4);
            AssertAngles(new double[] { 90, 90, 270, 90, 90, 90 }, m, 0.1);
            var closing = MeasureMath.Measure(L.Concat(new[] { L[0] }).ToArray());
            Assert.AreEqual(6, closing.OutlineCount);
            Assert.AreEqual(3.0, closing.Area, 1e-6);
        }

        [Test]
        public void PointOnASideIsAStraightCorner()
        {
            var m = MeasureMath.Measure(V(0, 0, 1, 0, 2, 0, 2, 1, 0, 1));
            Assert.AreEqual(5, m.OutlineCount);
            Assert.AreEqual(180.0, m.Angles[1], 1e-6);
            Assert.AreEqual(2.0, m.Area, 1e-6);
            Assert.AreEqual(540.0, m.Angles.Sum(), 1e-6);
        }

        [Test]
        public void DrawingAValidShapeNeverWarnsOnThePath()
        {
            // The live preview = the placed points + the cursor. Tracing an L, a U and a U started mid-base (whose closing
            // side cuts through the slot for a while), the placed path never crosses; the cursor resting on the last point
            // (right after a click) changes nothing.
            var uMidBase = V(1.5f, 0, 3, 0, 3, 2, 2, 2, 2, 1, 1, 1, 1, 2, 0, 2, 0, 0);
            foreach (var (name, shape) in new[] { ("L", L), ("U", U), ("U from mid-base", uMidBase) })
            for (int k = 3; k <= shape.Length; k++)
            {
                var placed = shape.Take(k - 1).ToList();
                var live = MeasureMath.Measure(placed.Concat(new[] { shape[k - 1] }).ToArray());
                Assert.AreNotEqual(OutlineCrossing.Path, live.Crossing, $"{name}: cursor on point {k}");
                var resting = MeasureMath.Measure(placed.Concat(new[] { placed[placed.Count - 1] }).ToArray());
                Assert.AreNotEqual(OutlineCrossing.Path, resting.Crossing, $"{name}: cursor resting on point {k - 1}");
            }
            // The mid-base U does pass through a quiet "closing would cross" stage, and ends clean.
            var mid = MeasureMath.Measure(uMidBase.Take(7).ToArray());
            Assert.AreEqual(OutlineCrossing.Closing, mid.Crossing);
            var done = MeasureMath.Measure(uMidBase);
            Assert.AreEqual(OutlineCrossing.None, done.Crossing);
            Assert.AreEqual(5.0, done.Area, 1e-6);
            Assert.AreEqual(1080.0 + 180.0, done.Angles.Sum(), 1e-6, "9 corners (one straight, on the base)");
        }

        [Test]
        public void CrossingCopyFitsAndHasItsGlyphs()
        {
            foreach (var text in new[]
            {
                MeasureView.CrossLabel, MeasureTool.CrossHint, MeasureTool.MoveCrossHint, Units.FormatAngle(270),
                NextStepCopy.Status(RuleId.R32, "cross", false), NextStepCopy.Status(RuleId.R32, "open", true),
                NextStepCopy.Status(RuleId.R01, nameof(Refusal.EdgesCross), false),
            })
            {
                Assert.IsNotEmpty(text);
                Assert.LessOrEqual(text.Length, NextStepCopy.StatusBudget, text);
                foreach (char ch in text)
                    Assert.IsTrue(AirTools.Editor.UiAssetsBuilder.Charset.IndexOf(ch) >= 0, $"'{ch}' (U+{(int)ch:X4}) in \"{text}\" is not in UiAssetsBuilder.Charset");
            }
        }

        // ---------------- the area label goes inside ----------------

        static bool Inside(Vector3[] poly, Vector3 q)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > q.y) != (poly[j].y > q.y) && q.x < (poly[j].x - poly[i].x) * (q.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            return inside;
        }

        static float Room(Vector3[] poly, Vector3 q)
        {
            float best = float.MaxValue;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var e = poly[i] - poly[j];
                float t = Mathf.Clamp01(Vector3.Dot(q - poly[j], e) / e.sqrMagnitude);
                best = Mathf.Min(best, Vector3.Distance(q, poly[j] + e * t));
            }
            return best;
        }

        [Test]
        public void AreaLabelSitsInsideConcaveShapes()
        {
            foreach (var (name, poly, room) in new[] { ("L", L, 0.4f), ("U", U, 0.4f), ("notched", Notched, 0.5f), ("V-notched", VNotched, 0.5f) })
            {
                var m = MeasureMath.Measure(poly);
                var at = MeasureMath.LabelPoint(poly, m);
                Assert.AreEqual(0f, at.z, 1e-4f, $"{name}: on the plane");
                Assert.IsTrue(Inside(poly, at), $"{name}: label at {at} is outside");
                Assert.GreaterOrEqual(Room(poly, at), room, $"{name}: label at {at} hugs a side");
            }
            // The L's centroid (0.83, 1.17) is inside but 0.24 m from the reflex corner; the label moves into the bend.
            var l = MeasureMath.LabelPoint(L, MeasureMath.Measure(L));
            Assert.Less(l.x, 1f);
            Assert.Greater(l.y, 1f);
            // The U's label stays in the middle of its base, not in an arm.
            var u = MeasureMath.LabelPoint(U, MeasureMath.Measure(U));
            Assert.AreEqual(1.5f, u.x, 0.2f);
            Assert.Less(u.y, 1f);
        }

        [Test]
        public void AreaLabelOfConvexShapesIsTheCentroid()
        {
            var rect = V(-0.75f, 3.5f, 0.75f, 3.5f, 0.75f, 4.7f, -0.75f, 4.7f);
            var at = MeasureMath.LabelPoint(rect, MeasureMath.Measure(rect));
            Assert.AreEqual(0f, at.x, 1e-4f); Assert.AreEqual(4.1f, at.y, 1e-4f);
            var tri = V(0, 0, 3, 0, 3, 4);
            var t = MeasureMath.LabelPoint(tri, MeasureMath.Measure(tri));
            Assert.AreEqual(2f, t.x, 1e-4f); Assert.AreEqual(4f / 3f, t.y, 1e-4f);
            // Tilted in 3D, the label is still the centroid on the plane.
            var r = Q(new Vector3(1, 1, 0), 50f);
            var moved = Move(rect, r, new Vector3(1, 2, 3));
            var mt = MeasureMath.LabelPoint(moved, MeasureMath.Measure(moved));
            Assert.AreEqual(0f, Vector3.Distance(mt, new Vector3(1, 2, 3) + r * new Vector3(0, 4.1f, 0)), 1e-3f);
            // A distance: its midpoint.
            var d = new[] { Vector3.zero, new Vector3(2, 0, 0) };
            Assert.AreEqual(new Vector3(1, 0, 0), MeasureMath.LabelPoint(d, MeasureMath.Measure(d)));
        }

        [Test]
        public void LabelPointAllocatesNothing()
        {
            var m = MeasureMath.Measure(U);
            var sink = Vector3.zero;
            for (int i = 0; i < 300; i++) sink += MeasureMath.LabelPoint(U, m);   // JIT tiers + scratch
            long before;
            try { before = System.GC.GetAllocatedBytesForCurrentThread(); }
            catch (System.Exception e) when (e is System.NotSupportedException || e is System.NotImplementedException) { Assert.Ignore("GC.GetAllocatedBytesForCurrentThread unsupported"); return; }
            for (int i = 0; i < 100; i++) sink += MeasureMath.LabelPoint(U, m);
            long settled = System.GC.GetAllocatedBytesForCurrentThread();   // the runtime's one-off tier-up bytes land here
            for (int i = 0; i < 100; i++) sink += MeasureMath.LabelPoint(U, m);
            long after = System.GC.GetAllocatedBytesForCurrentThread();
            Assert.AreEqual(0, after - settled, $"bytes allocated by 100 LabelPoint calls (first window {settled - before}; {sink})");
        }
    }
}
