using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Parts
{
    /// e2e ("replace the dishwasher"): a part's true size against a removed component's gap, on all three axes — the
    /// part's width across the opening, its height up, its depth into it (place_part stands it that way: +Z out of the
    /// opening, +Y up). The gap is the cavity's size_m × the scene calibration, in millimetres (real size). Clearance per
    /// axis = gap − part; the tightest one decides: ≥ 0 fits (green "Fits the gap · ⅜″ spare"), up to 5 mm over is tight
    /// (amber "Tight in the gap · ⅛″"), more is too big (red).
    /// The result feeds PlacePartFit, so the outline, callout and notebook show it like a server fit. Pure.
    public static class CavityFit
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        /// Gap − part per axis (x = width, y = height, z = depth), mm.
        public static Vector3 Clearance(PartDims part, Vector3 gapMm) =>
            part == null ? new Vector3(float.NaN, float.NaN, float.NaN) : new Vector3(gapMm.x - part.w, gapMm.y - part.h, gapMm.z - part.d);

        /// The tightest clearance (mm; negative = over by that much) and its axis ("w" / "h" / "d").
        public static float Tightest(Vector3 clearance, out string axis)
        {
            axis = "w";
            float m = clearance.x;
            if (clearance.y < m) { m = clearance.y; axis = "h"; }
            if (clearance.z < m) { m = clearance.z; axis = "d"; }
            return m;
        }

        /// Every axis has room (and the part and the gap both have a size).
        public static bool Fits(PartDims part, Vector3 gapMm)
        {
            if (!Known(part) || !Valid(gapMm)) return false;
            return Tightest(Clearance(part, gapMm), out _) >= 0f;
        }

        public static bool Known(PartDims d) => d != null && d.w > 0f && d.h > 0f && d.d > 0f;
        public static bool Valid(Vector3 g) => g.x > 0f && g.y > 0f && g.z > 0f && !float.IsNaN(g.x + g.y + g.z) && !float.IsInfinity(g.x + g.y + g.z);

        /// Up to this much over on one axis is "tight" (amber), not "too big": the gap is an estimate (and appliance legs
        /// adjust). The e2e server patch uses the same 5 mm.
        public const float TightMm = 5f;

        /// place_part's `fits` word: "fits" / "tight" (≤ TightMm over) / "too_big" (null when a size is missing: the app's
        /// own check runs).
        public static JToken FitsToken(PartDims part, Vector3 gapMm)
        {
            if (!Known(part) || !Valid(gapMm)) return null;
            float t = Tightest(Clearance(part, gapMm), out _);
            return new JValue(t >= 0f ? "fits" : t >= -TightMm ? "tight" : "too_big");
        }

        /// place_part's `clearance_mm` {w, h, d}, rounded to the millimetre (null when a size is missing).
        public static JObject ClearanceToken(PartDims part, Vector3 gapMm)
        {
            if (!Known(part) || !Valid(gapMm)) return null;
            var c = Clearance(part, gapMm);
            return new JObject { ["w"] = Math.Round(c.x), ["h"] = Math.Round(c.y), ["d"] = Math.Round(c.z) };
        }

        /// The fit shown on a part placed in the gap: "Fits the gap · ⅜″ spare" / "Too big for the gap · 1″ over".
        public static FitReport Report(PartDims part, Vector3 gapMm) =>
            FitsToken(part, gapMm) is JToken fits ? PlacePartFit.From(fits, ClearanceToken(part, gapMm)) : null;

        /// The best candidate for the gap: the first one (in the search's own order: the server ranks them) that fits;
        /// none fits → the one that overruns least; none has a size → the first. -1 for an empty list.
        public static int Best(IList<PartDims> parts, Vector3 gapMm)
        {
            if (parts == null || parts.Count == 0) return -1;
            int least = -1;
            float leastOver = float.MaxValue;
            for (int i = 0; i < parts.Count; i++)
            {
                if (!Known(parts[i]) || !Valid(gapMm)) continue;
                float t = Tightest(Clearance(parts[i], gapMm), out _);
                if (t >= 0f) return i;
                if (-t < leastOver) { leastOver = -t; least = i; }
            }
            return least >= 0 ? least : 0;
        }

        /// How many of them fit.
        public static int CountFitting(IList<PartDims> parts, Vector3 gapMm)
        {
            int n = 0;
            if (parts != null) foreach (var p in parts) if (Fits(p, gapMm)) n++;
            return n;
        }

        /// "600 × 820 × 580 mm" for the logs (W × H × D).
        public static string MmText(Vector3 mm) => $"{mm.x.ToString("0", C)} × {mm.y.ToString("0", C)} × {mm.z.ToString("0", C)} mm";

        /// "w +12 h −3 d +40 mm" for the logs.
        public static string ClearanceText(Vector3 c) =>
            $"w {Signed(c.x)} h {Signed(c.y)} d {Signed(c.z)} mm";

        static string Signed(float v) => (v >= 0f ? "+" : "−") + Mathf.Abs(v).ToString("0", C);
    }
}
