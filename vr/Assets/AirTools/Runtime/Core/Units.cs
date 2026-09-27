using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AirTools.Core
{
    /// Which unit an on-screen label shows (UX decision D2, SPEC §9): ONE unit per label, imperial first for the
    /// HackGT build. The choice lives in UiSettings.UnitSystem (per device; the units chip switches it through
    /// UnitsSwitch). Export, CSV/HTML, the notebook's raw Label and the logs keep the dual Units.Format.
    public enum UnitSystem { Imperial = 0, Metric = 1 }

    /// SI internally (metres, m², degrees for angles). Machine paths (requests, logs, [AirTools.Check] numbers) stay
    /// metric. Format / FormatArea: the dual string, metric first (export, logs, the M2 acceptance). *Primary / Pair /
    /// FormatMm / FormatRangeMm / FormatWeight: what a label shows in the chosen unit (D2) — pure, the unit passed in.
    public static class Units
    {
        const double MetresPerInch = 0.0254;
        const double SquareFeetPerSquareMetre = 10.763910416709722;
        const double GramsPerOunce = 28.349523125, GramsPerPound = 453.59237;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly string[] Eighths = { "", "⅛", "¼", "⅜", "½", "⅝", "¾", "⅞" };

        /// A real minus sign (U+2212), not a hyphen.
        public const string Minus = "−";
        /// What a label shows for a missing / non-finite value.
        public const string Dash = "—";

        /// 4.2 → "4.20 m · 13′ 9⅜″" (inches rounded to the nearest 1/8; no "0′" under a foot).
        public static string Format(double metres) => $"{Metric(metres)} · {FeetInches(metres)}";

        public static string Metric(double metres) => $"{Signed(metres, "0.00")} m";

        /// 0.26 → "10¼″" (feet omitted when 0), 4.2 → "13′ 9⅜″", −0.3048 → "−1′ 0″".
        public static string FeetInches(double metres)
        {
            long eighths = (long)Math.Round(Math.Abs(metres) / MetresPerInch * 8.0, MidpointRounding.AwayFromZero);
            string sign = metres < 0 && eighths > 0 ? Minus : "";
            long feet = eighths / (12 * 8);
            long rem = eighths % (12 * 8);
            long inches = rem / 8;
            string frac = Eighths[rem % 8];
            string inchPart = inches == 0 && frac.Length > 0 ? frac : $"{inches}{frac}";
            return feet == 0 ? $"{sign}{inchPart}″" : $"{sign}{feet}′ {inches}{frac}″";
        }

        /// Whole inches to the nearest 1/8, no mark and no feet: 1.5 → "59", 0.2 → "7⅞", 0.009 → "⅜".
        public static string InchesOnly(double metres)
        {
            long eighths = (long)Math.Round(Math.Abs(metres) / MetresPerInch * 8.0, MidpointRounding.AwayFromZero);
            string sign = metres < 0 && eighths > 0 ? Minus : "";
            long whole = eighths / 8;
            string frac = Eighths[eighths % 8];
            return sign + (whole == 0 && frac.Length > 0 ? frac : $"{whole}{frac}");
        }

        /// 1.8 → "1.80 m² · 19.4 ft²"
        public static string FormatArea(double squareMetres) =>
            $"{Signed(squareMetres, "0.00")} m² · {Signed(squareMetres * SquareFeetPerSquareMetre, "0.0")} ft²";

        /// Number with a real minus sign ("−0.26"); never "−0.00".
        static string Signed(double v, string format)
        {
            string s = Math.Abs(v).ToString(format, C);
            return v < 0 && s.Trim('0', '.').Length > 0 ? Minus + s : s;
        }

        /// 89.96 → "90.0°"
        public static string FormatAngle(double degrees) => $"{degrees.ToString("0.0", C)}°";

        // ---------------- D2: one unit per label ----------------

        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        public static UnitSystem Other(UnitSystem system) => system == UnitSystem.Metric ? UnitSystem.Imperial : UnitSystem.Metric;

        /// One length for a label. Imperial: feet and inches to the nearest ⅛″, inches only under a foot
        /// (1.5003 → "4′ 11⅛″", 0.26 → "10¼″", 0 → "0″"); metric: "1.50 m". NaN / ∞ → "—".
        public static string FormatPrimary(double metres, UnitSystem system) =>
            !Finite(metres) ? Dash : system == UnitSystem.Metric ? Metric(metres) : FeetInches(metres);

        /// The other unit (a focus line, the second half of FormatBoth).
        public static string FormatSecondary(double metres, UnitSystem system) => FormatPrimary(metres, Other(system));

        /// Both units, the primary first (the notebook keeps both): "4′ 11″ · 1.50 m" / "1.50 m · 4′ 11″" (= Format).
        public static string FormatBoth(double metres, UnitSystem system) =>
            !Finite(metres) ? Dash : $"{FormatPrimary(metres, system)} · {FormatSecondary(metres, system)}";

        /// One area for a label: "1.80 m²" / "19 ft²" (whole square feet from 10 up, one decimal below: "3.2 ft²").
        public static string FormatAreaPrimary(double squareMetres, UnitSystem system)
        {
            if (!Finite(squareMetres)) return Dash;
            if (system == UnitSystem.Metric) return $"{Signed(squareMetres, "0.00")} m²";
            double ft2 = squareMetres * SquareFeetPerSquareMetre;
            return $"{Signed(ft2, Math.Abs(ft2) >= 9.95 ? "0" : "0.0")} ft²";
        }

        /// Both areas, the primary first: "19.4 ft² · 1.80 m²" / "1.80 m² · 19.4 ft²" (= FormatArea).
        public static string FormatAreaBoth(double squareMetres, UnitSystem system) =>
            !Finite(squareMetres) ? Dash
            : system == UnitSystem.Metric ? FormatArea(squareMetres)
            : $"{Signed(squareMetres * SquareFeetPerSquareMetre, "0.0")} ft² · {Signed(squareMetres, "0.00")} m²";

        /// A small length the code keeps in mm (a gap, a clearance, a part size, a spacing): "25 mm" / "1″" (feet and
        /// inches from a foot up: 600 → "1′ 11⅝″").
        public static string FormatMm(double mm, UnitSystem system) =>
            !Finite(mm) ? Dash : system == UnitSystem.Metric ? $"{Mm0(mm)} mm" : FeetInches(mm / 1000.0);

        /// A W × H pair (survey objects, B1): "262 × 279 mm" / "10⅜ × 11″" (inches only while both sides are under
        /// 8 ft, like windows and cabinet doors are sold; else "16′ 0″ × 7′ 0″").
        public static string FormatPair(double wM, double hM, UnitSystem system)
        {
            if (!Finite(wM) || !Finite(hM)) return Dash;
            if (system == UnitSystem.Metric) return $"{Mm0(wM * 1000.0)} × {Mm0(hM * 1000.0)} mm";
            return Math.Max(Math.Abs(wM), Math.Abs(hM)) < 96 * MetresPerInch
                ? $"{InchesOnly(wM)} × {InchesOnly(hM)}″"
                : $"{FeetInches(wM)} × {FeetInches(hM)}";
        }

        /// The raw pair for logs, export and the notebook Label: always "262 × 279 mm".
        public static string PairMm(double wM, double hM) => FormatPair(wM, hM, UnitSystem.Metric);

        /// A W × H × D triple (a scene part's cavity): "437 × 591 × 445 mm" / "17¼ × 23¼ × 17½″" (inches only while every
        /// side is under 8 ft, as FormatPair; else feet and inches).
        public static string FormatTriple(double wM, double hM, double dM, UnitSystem system)
        {
            if (!Finite(wM) || !Finite(hM) || !Finite(dM)) return Dash;
            if (system == UnitSystem.Metric) return $"{Mm0(wM * 1000.0)} × {Mm0(hM * 1000.0)} × {Mm0(dM * 1000.0)} mm";
            return Math.Max(Math.Abs(wM), Math.Max(Math.Abs(hM), Math.Abs(dM))) < 96 * MetresPerInch
                ? $"{InchesOnly(wM)} × {InchesOnly(hM)} × {InchesOnly(dM)}″"
                : $"{FeetInches(wM)} × {FeetInches(hM)} × {FeetInches(dM)}";
        }

        /// The raw triple for logs: always "437 × 591 × 445 mm".
        public static string TripleMm(double wM, double hM, double dM) => FormatTriple(wM, hM, dM, UnitSystem.Metric);

        /// A width range (window units): "590–1000 mm" / "23¼–39⅜″"; open-ended "≥ 590 mm" / "≥ 23¼″".
        public static string FormatRangeMm(double? minMm, double? maxMm, UnitSystem system)
        {
            double min = minMm ?? 0.0;
            if (system == UnitSystem.Metric)
                return maxMm.HasValue ? $"{Mm0(min)}–{Mm0(maxMm.Value)} mm" : $"≥ {Mm0(min)} mm";
            return maxMm.HasValue ? $"{InchesOnly(min / 1000.0)}–{InchesOnly(maxMm.Value / 1000.0)}″" : $"≥ {InchesOnly(min / 1000.0)}″";
        }

        /// A part's weight: "60 g" / "1.2 kg" (metric) · "2.1 oz" / "3.3 lb" (imperial).
        public static string FormatWeight(double grams, UnitSystem system)
        {
            if (!Finite(grams)) return Dash;
            if (system == UnitSystem.Metric)
                return grams >= 1000.0 ? (grams / 1000.0).ToString("0.0", C) + " kg" : grams.ToString("0", C) + " g";
            double oz = grams / GramsPerOunce;
            if (oz < 15.5) return oz.ToString(oz >= 9.95 ? "0" : "0.0", C) + " oz";
            return (grams / GramsPerPound).ToString("0.0", C) + " lb";
        }

        /// The scale keypad's unit (a word: it's read in "Type the real size in inches") and a typed value in metres.
        public static string KeypadUnit(UnitSystem system) => system == UnitSystem.Metric ? "cm" : "inches";
        public static double KeypadMetres(double typed, UnitSystem system) => system == UnitSystem.Metric ? typed / 100.0 : typed * MetresPerInch;

        static readonly Regex s_Lengths = new Regex(@"(?<![\w.,$−-])(\d+(?:\.\d+)?) ?(mm|cm|m)(?![\w²³])", RegexOptions.CultureInvariant);

        /// Metric lengths inside text we don't word ourselves (server check details, the ladder rule) in the chosen
        /// unit: "tape #3 4.20 m ÷ 600 mm → 8" → "tape #3 13′ 9⅜″ ÷ 1′ 11⅝″ → 8". Metric text comes back as is;
        /// areas (m²) and words ("5 min") are left alone.
        public static string ConvertLengths(string text, UnitSystem system)
        {
            if (string.IsNullOrEmpty(text) || system == UnitSystem.Metric) return text ?? "";
            return s_Lengths.Replace(text, m =>
            {
                if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, C, out var v)) return m.Value;
                double metres = m.Groups[2].Value == "mm" ? v / 1000.0 : m.Groups[2].Value == "cm" ? v / 100.0 : v;
                return FeetInches(metres);
            });
        }

        /// Whole millimetres the way the mm labels always rounded them (float, then banker's rounding): 262.4 → "262".
        static string Mm0(double mm) => Math.Round((double)(float)mm).ToString("0", C);
    }
}
