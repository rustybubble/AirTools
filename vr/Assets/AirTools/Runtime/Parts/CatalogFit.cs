using AirTools.Core;

namespace AirTools.Parts
{
    /// catalog: the optional "Fits 2′ 4⅝″" filter — what the last tape or the open gap allows. Pure (the window reads the
    /// tape and the gap and hands them in). Not a requirement: the catalog opens and searches without either.
    public struct CatalogFit
    {
        /// Size readings are a few mm noisy: a part this much bigger than the reading still counts as fitting.
        public const float ToleranceMm = 3f;

        public bool HasTape;
        /// The tape's axis as the parts search sends it: "w" (a width ≤ 1.5 m), "h" (a height) or "length" (a run).
        public string Axis;
        public double TapeM;
        public bool HasGap;
        /// The gap's W × H × D (m; D ≤ 0: unknown).
        public double GapW, GapH, GapD;

        public static CatalogFit None => default;

        public static CatalogFit Tape(double metres, string axis) => new CatalogFit { HasTape = metres > 0.0, TapeM = metres, Axis = axis ?? "w" };

        public static CatalogFit Gap(double w, double h, double d) => new CatalogFit { HasGap = w > 0.0 && h > 0.0, GapW = w, GapH = h, GapD = d };

        /// There's something to fit against: an open gap, or a width / height tape (a long run constrains nothing).
        public bool Available => HasGap || (HasTape && (Axis == "w" || Axis == "h"));

        /// The chip: "Fits 2′ 4⅝″" (the gap's width, else the tape).
        public string Label(UnitSystem units) => !Available ? "" : "Fits " + Units.FormatPrimary(HasGap ? GapW : TapeM, units);

        /// Does a part of these published dims go in? Unknown dims never "fit" (the filter can't vouch for them).
        public bool Fits(PartDims d)
        {
            if (!Available) return true;
            if (d == null || d.w <= 0f || d.h <= 0f) return false;
            if (HasGap)
                return d.w <= GapW * 1000.0 + ToleranceMm && d.h <= GapH * 1000.0 + ToleranceMm && (GapD <= 0.0 || d.d <= 0f || d.d <= GapD * 1000.0 + ToleranceMm);
            return Axis == "h" ? d.h <= TapeM * 1000.0 + ToleranceMm : d.w <= TapeM * 1000.0 + ToleranceMm;
        }

        public bool SameAs(CatalogFit o) =>
            HasTape == o.HasTape && HasGap == o.HasGap && Axis == o.Axis && System.Math.Abs(TapeM - o.TapeM) < 1e-4
            && System.Math.Abs(GapW - o.GapW) < 1e-4 && System.Math.Abs(GapH - o.GapH) < 1e-4 && System.Math.Abs(GapD - o.GapD) < 1e-4;

        public override string ToString() =>
            HasGap ? $"gap {GapW:0.000}×{GapH:0.000}×{GapD:0.000} m" : HasTape ? $"tape {TapeM:0.000} m ({Axis})" : "none";
    }
}
