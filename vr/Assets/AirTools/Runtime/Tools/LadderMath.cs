using System;
using System.Globalization;
using UnityEngine;

namespace AirTools.Tools
{
    public enum LadderVerdict { Green, Amber, Red }

    /// What the top of the ladder rests against (presence.md S4).
    public enum LadderSupport
    {
        /// A roof edge or other upper landing you step off onto: the rails must run 3 ft (0.914 m) past it
        /// (29 CFR 1926.1053(b)(1)), so the size includes that extension.
        Landing,
        /// Gutter work with no landing behind it: leaning on the gutter is amber ("use a standoff").
        GutterNoLanding,
        /// A wall, sill or ledge you work at and don't step onto: no extension needed.
        Wall,
    }

    /// The ladder for a support height h and a foot distance d (metres; h = support height above the ground under
    /// the foot, d = horizontal distance from the support face to the foot).
    public struct LadderSolution
    {
        public double Height;
        public double FootOut;
        /// Along the rails from the foot to the support (the working length Lw at 4:1).
        public double ContactLength;
        /// Along the rails past the support (landings only).
        public double Extension;
        /// Rail length needed R = contact + extension.
        public double RailNeeded;
        public double AngleDeg;
        /// How far the rails stand above the edge, vertically.
        public double AboveEdge;
        /// Smallest stock extension ladder that reaches (nominal feet); 0 = taller than any.
        public int SizeFt;
        public LadderSupport Support;

        /// Rise per unit run (4 at the 4:1 rule).
        public double Rise => FootOut > 1e-9 ? Height / FootOut : double.PositiveInfinity;
    }

    /// What the scene says about the setup (measured by LadderTool with raycasts; plain inputs here).
    public struct LadderChecks
    {
        /// Ground slope under the foot (degrees from level).
        public double FootingDeg;
        /// A surface within 0.3 m under the foot.
        public bool GroundFound;
        /// Something inside the rails' envelope (SceneSurface / Parts).
        public bool Collision;
        /// What it hits ("the gutter"), for the reason line.
        public string CollisionWith;

        public static LadderChecks Clear => new LadderChecks { GroundFound = true };
    }

    public struct LadderReport
    {
        public LadderVerdict Verdict;
        /// Why it isn't green (or what's right when it is), said like a tradesperson.
        public string Reason;
        /// "1 : 4.0" (run : rise).
        public string Ratio;
    }

    /// Ladder check maths (presence.md S4, P6): OSHA 29 CFR 1926.1053 — foot out a quarter of the working length
    /// (4:1, acos ¼ = 75.52°), rails 3 ft past an upper landing, and the stock extension ladder that reaches.
    /// Pure: no engine calls (EditMode-tested); LadderTool measures the scene and passes plain numbers in.
    public static class LadderMath
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public const double MetresPerFoot = 0.3048;
        /// 3 ft above the upper landing (1926.1053(b)(1)).
        public const double LandingExtensionM = 3 * MetresPerFoot;
        /// Rungs every 0.30 m (a 12 in rung pitch).
        public const double RungSpacingM = 0.30;
        public static readonly double Sqrt15 = Math.Sqrt(15.0);
        /// acos(1/4): the 4:1 angle, 75.5225°.
        public static readonly double IdealAngleDeg = Math.Acos(0.25) * 180.0 / Math.PI;

        /// Stock extension ladder sizes (nominal feet).
        public static readonly int[] SizesFt = { 16, 20, 24, 28, 32, 36, 40 };

        public const double GreenAngleDeg = 2.0, AmberAngleDeg = 5.0;
        public const double GreenFootingDeg = 3.0, AmberFootingDeg = 6.0;
        /// No ground within this distance under the foot is red.
        public const double GroundSearchM = 0.3;
        public const double MinAboveLandingM = 0.9;

        public const string Disclaimer = "Guide only — follow the ladder's label and OSHA 1926.1053.";

        // ---------------- 4:1 ----------------

        /// Working length at 4:1: Lw = h·4/√15 (≈ 1.0328 h).
        public static double WorkingLength(double h) => h * 4.0 / Sqrt15;

        /// Foot distance at 4:1: d = Lw/4 = h/√15.
        public static double FootDistance(double h) => h / Sqrt15;

        /// Along the rails past the edge so they stand 3 ft above it: 0.914 / sin(angle).
        public static double ExtensionAlongRails(double angleDeg) => LandingExtensionM / Math.Sin(angleDeg * Math.PI / 180.0);

        public static double AngleDeg(double h, double d) => Math.Atan2(h, d) * 180.0 / Math.PI;

        // ---------------- sizes ----------------

        /// Section overlap: 3 ft up to 36 ft, 4 ft above.
        public static int OverlapFt(int nominalFt) => nominalFt <= 36 ? 3 : 4;

        /// Max working length of an extension ladder (nominal − overlap): 24 ft → 21 ft.
        public static int MaxWorkingFt(int nominalFt) => nominalFt - OverlapFt(nominalFt);

        /// The smallest stock size whose max working length reaches `railM`; 0 when none does.
        public static int SizeFor(double railM)
        {
            double ft = railM / MetresPerFoot;
            foreach (var s in SizesFt)
                if (MaxWorkingFt(s) >= ft - 1e-9) return s;
            return 0;
        }

        // ---------------- solve ----------------

        /// The 4:1 ladder for a support h above the ground.
        public static LadderSolution Solve(double h, LadderSupport support) => SolveFoot(h, FootDistance(h), support);

        /// The ladder with its foot d out from the support face (dragged foot): angle, lengths and size re-solve.
        public static LadderSolution SolveFoot(double h, double d, LadderSupport support)
        {
            h = Math.Max(h, 1e-6);
            d = Math.Max(d, 1e-6);
            var s = new LadderSolution
            {
                Height = h,
                FootOut = d,
                ContactLength = Math.Sqrt(h * h + d * d),
                AngleDeg = AngleDeg(h, d),
                Support = support,
            };
            s.Extension = support == LadderSupport.Landing ? ExtensionAlongRails(s.AngleDeg) : 0.0;
            s.AboveEdge = s.Extension * Math.Sin(s.AngleDeg * Math.PI / 180.0);
            s.RailNeeded = s.ContactLength + s.Extension;
            s.SizeFt = SizeFor(s.RailNeeded);
            return s;
        }

        // ---------------- classes ----------------

        /// 75.5° ± 2° green, off by 2–5° amber, more red.
        public static LadderVerdict AngleClass(double angleDeg)
        {
            double off = Math.Abs(angleDeg - IdealAngleDeg);
            return off <= GreenAngleDeg + 1e-9 ? LadderVerdict.Green : off <= AmberAngleDeg + 1e-9 ? LadderVerdict.Amber : LadderVerdict.Red;
        }

        /// Ground slope under the foot: ≤ 3° green, ≤ 6° amber, more red.
        public static LadderVerdict FootingClass(double slopeDeg)
        {
            double s = Math.Abs(slopeDeg);
            return s <= GreenFootingDeg + 1e-9 ? LadderVerdict.Green : s <= AmberFootingDeg + 1e-9 ? LadderVerdict.Amber : LadderVerdict.Red;
        }

        /// Run : rise, "1 : 4.0" (one decimal under 10, whole numbers above: "1 : 12").
        public static string RatioText(double h, double d)
        {
            if (d <= 1e-9) return "vertical";
            double r = h / d;
            return "1 : " + (r >= 9.95 ? r.ToString("0", C) : r.ToString("0.0", C));
        }

        /// The verdict, red rules first (collision, no ground, angle > 5° off, footing > 6°, no stock size), then
        /// amber (angle 2–5° off, footing 3–6°, gutter work with no landing, short of 0.9 m above a landing).
        public static LadderReport Evaluate(in LadderSolution s, in LadderChecks c)
        {
            var r = new LadderReport { Ratio = RatioText(s.Height, s.FootOut) };
            double off = s.AngleDeg - IdealAngleDeg;
            var angle = AngleClass(s.AngleDeg);
            var footing = FootingClass(c.FootingDeg);
            string steep = off > 0 ? "too steep" : "too shallow";
            string foot = off > 0 ? "move the foot out" : "move the foot in";

            if (c.Collision) return Red(r, $"hits {(string.IsNullOrEmpty(c.CollisionWith) ? "something" : c.CollisionWith)} — move the ladder");
            if (!c.GroundFound) return Red(r, "no ground under the foot");
            if (angle == LadderVerdict.Red) return Red(r, $"{r.Ratio} — {steep}, {foot}");
            if (footing == LadderVerdict.Red) return Red(r, $"ground slopes {Deg(c.FootingDeg)} — level the feet");
            if (s.SizeFt == 0) return Red(r, $"taller than a {SizesFt[SizesFt.Length - 1]} ft ladder");

            if (angle == LadderVerdict.Amber) return Amber(r, $"{r.Ratio} — a bit {(off > 0 ? "steep" : "shallow")}, {foot}");
            if (footing == LadderVerdict.Amber) return Amber(r, $"ground slopes {Deg(c.FootingDeg)} — use levellers");
            if (s.Support == LadderSupport.GutterNoLanding) return Amber(r, "don't lean on the gutter — use a standoff");
            if (s.Support == LadderSupport.Landing && s.AboveEdge < MinAboveLandingM - 1e-6)
                return Amber(r, $"only {Len(s.AboveEdge)} above the edge — needs 0.9 m");

            r.Verdict = LadderVerdict.Green;
            r.Reason = "4 : 1, firm footing";
            return r;
        }

        static LadderReport Red(LadderReport r, string why) { r.Verdict = LadderVerdict.Red; r.Reason = why; return r; }
        static LadderReport Amber(LadderReport r, string why) { r.Verdict = LadderVerdict.Amber; r.Reason = why; return r; }

        // ---------------- words ----------------

        public static string VerdictWord(LadderVerdict v) => v switch
        {
            LadderVerdict.Green => "Green",
            LadderVerdict.Amber => "Amber",
            _ => "Red",
        };

        /// The card's verdict in words: "Set up right" / "Check" / "Don't climb".
        public static string VerdictTitle(LadderVerdict v) => v switch
        {
            LadderVerdict.Green => "Set up right",
            LadderVerdict.Amber => "Check",
            _ => "Don't climb",
        };

        /// ✓ / ! / ✗ (state never by colour alone).
        public static string Glyph(LadderVerdict v) => v switch
        {
            LadderVerdict.Green => "✓",
            LadderVerdict.Amber => "!",
            _ => "✗",
        };

        /// "28 ft extension ladder" (sizes are sold in feet).
        public static string SizeName(int sizeFt) => sizeFt > 0 ? $"{sizeFt} ft extension ladder" : "No stock extension ladder";

        /// "28 ft extension ladder · foot 1.60 m out · 75.5° · 0.91 m above the edge" (the last part for landings).
        public static string Label(in LadderSolution s)
        {
            string label = $"{SizeName(s.SizeFt)} · foot {Len(s.FootOut)} out · {Deg(s.AngleDeg)}";
            if (s.Support == LadderSupport.Landing) label += $" · {Len(s.AboveEdge)} above the edge";
            return label;
        }

        /// The search the card's "Find this ladder" runs.
        public static string SearchQuery(int sizeFt) => sizeFt > 0 ? $"{sizeFt} ft extension ladder" : "extension ladder";

        /// Label + verdict word, the harness line: "28 ft extension ladder · … · Green".
        public static string Summary(in LadderSolution s, in LadderReport r) => $"{Label(s)} · {VerdictWord(r.Verdict)}";

        /// "✓ 4 : 1, firm footing", "✗ 1 : 12 — too steep, move the foot out".
        public static string VerdictLine(in LadderReport r) => $"{Glyph(r.Verdict)} {r.Reason}";

        static string Len(double m) => m.ToString("0.00", C) + " m";
        static string Deg(double deg) => deg.ToString("0.0", C) + "°";

        // ---------------- geometry (pure; all points in one frame, +Y up) ----------------

        /// Where the foot stands: straight out from the support face by d, at the ground's height.
        public static Vector3 FootPoint(Vector3 top, Vector3 outward, double d, float groundY)
        {
            var n = Horizontal(outward);
            return new Vector3(top.x, groundY, top.z) + n * (float)d;
        }

        /// Horizontal unit vector (zero when there's no horizontal part).
        public static Vector3 Horizontal(Vector3 v)
        {
            v.y = 0f;
            float m = v.magnitude;
            return m > 1e-6f ? v / m : Vector3.zero;
        }

        /// Distance out from the support face for a point on the ground (a dragged foot), at least `min`.
        public static double FootOutFor(Vector3 top, Vector3 outward, Vector3 p, double min = 0.05)
        {
            var n = Horizontal(outward);
            double d = Vector3.Dot(new Vector3(p.x - top.x, 0f, p.z - top.z), n);
            return Math.Max(min, d);
        }

        /// Rung heights along the rails from the foot (every 0.30 m, first one 0.30 m up), for a rail length.
        public static int RungCount(double railM) => railM <= RungSpacingM ? 0 : (int)Math.Floor((railM - 0.05) / RungSpacingM);
    }
}
