using System;
using System.Globalization;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Notes
{
    /// What a notebook entry shows (docs/ux/specs/W0.9-copy.md §3): Title / Value / Detail, derived from the entry's
    /// data when its producer didn't set DisplayTitle / DisplayValue / DisplayDetail. Label (export, logs, the harness)
    /// is never changed. Pure (EditMode-tested).
    public static class NotebookRow
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly float SinFive = Mathf.Sin(5f * Mathf.Deg2Rad), CosFive = Mathf.Cos(5f * Mathf.Deg2Rad);

        /// A two-point tape's name by its direction (scene-root points, +Y up): Width / Height / Distance.
        public static string TapeTitle(Vector3 a, Vector3 b)
        {
            var d = b - a;
            float len = d.magnitude;
            if (len < 1e-6f) return "Distance";
            float up = Mathf.Abs(d.y) / len;
            if (up <= SinFive) return "Width";
            if (up >= CosFive) return "Height";
            return "Distance";
        }

        /// A polygon's name: Rectangle (4 sides, right angles ±1°, opposite sides within 1 %), Triangle, "n-sided shape".
        public static string ShapeTitle(NotebookEntry e)
        {
            int n = e.Sides?.Length ?? 0;
            if (IsRectangle(e)) return "Rectangle";
            if (n == 3) return "Triangle";
            return n > 0 ? $"{n}-sided shape" : "Shape";
        }

        public static bool IsRectangle(NotebookEntry e)
        {
            if (e.Sides == null || e.Sides.Length != 4 || e.Angles == null || e.Angles.Length != 4) return false;
            foreach (var a in e.Angles) if (Math.Abs(a - 90.0) > 1.0) return false;
            bool Close(double x, double y) => Math.Abs(x - y) <= 0.01 * Math.Max(x, y);
            return Close(e.Sides[0], e.Sides[2]) && Close(e.Sides[1], e.Sides[3]);
        }

        static bool IsDistance(NotebookEntry e) => e.Tool == "measure" && e.Unit == "m" && e.Points != null && e.Points.Length == 2;

        static bool IsPlumb(NotebookEntry e) => e.Label != null && e.Label.StartsWith("Plumb", StringComparison.Ordinal);

        public static string Title(NotebookEntry e)
        {
            if (e == null) return "";
            if (!string.IsNullOrEmpty(e.DisplayTitle)) return e.DisplayTitle;
            switch (e.Tool)
            {
                case "measure": return IsDistance(e) ? TapeTitle(e.Points[0], e.Points[1]) : ShapeTitle(e);
                case "level":
                    bool plumb = IsPlumb(e), ok = Math.Abs(e.ValueSI) <= 0.2;
                    return plumb ? (ok ? "Plumb" : "Out of plumb") : (ok ? "Level" : "Slope");
                case "note": return Copy.Clip(e.Label ?? "", 30);
                case "scale": return Math.Abs(e.ValueSI - 1.0) < 1e-6 ? "Scale reset" : "Scale set";
                case "bom": return "Also needed";
                default: return Copy.Clip(Copy.Clean(e.Label ?? ""), 30);
            }
        }

        public static string Value(NotebookEntry e)
        {
            if (e == null) return "";
            if (e.DisplayValue != null) return e.DisplayValue;
            switch (e.Tool)
            {
                case "measure":
                    // D2: the notebook keeps both units, the user's first ("4′ 11″ · 1.50 m"); a rectangle's W × H shows
                    // the user's unit only (the row has room for one pair; the export's detail column has both).
                    if (IsDistance(e)) return Copy.LenFull(e.ValueSI);
                    if (IsRectangle(e)) return $"{Copy.Len(e.Sides[0])} × {Copy.Len(e.Sides[1])}";
                    return Copy.AreaFull(e.ValueSI);
                case "level":
                    if (Math.Abs(e.ValueSI) <= 0.2) return "✓";
                    string deg = e.ValueSI.ToString("0.0", C) + "°";
                    return IsPlumb(e) ? deg : $"{deg} · {Copy.Slope(Math.Tan(e.ValueSI * Math.PI / 180.0) * 100.0)}";
                case "scale": return Math.Abs(e.ValueSI - 1.0) < 1e-6 ? "" : "×" + e.ValueSI.ToString("0.00", C);
                case "bom": case "purchase": return "$" + e.ValueSI.ToString("0.00", C);
                default: return "";
            }
        }

        public static string Detail(NotebookEntry e) => e?.DisplayDetail;

        /// Row line 1: "{Title} · {Value}", at most maxChars; the title is cut, the value never.
        public static string RowTitle(NotebookEntry e, int maxChars = 34)
        {
            if (e == null) return "";
            string t = Title(e) ?? "", v = Value(e) ?? "";
            if (v.Length == 0) return t.Length <= maxChars ? t : t.Substring(0, maxChars - 1) + "…";
            int room = maxChars - v.Length - 3;
            if (t.Length > room) t = room >= 2 ? t.Substring(0, room - 1) + "…" : "";
            return t.Length > 0 ? $"{t} · {v}" : v;
        }

        /// Row line 2: "10:07 AM" (+ " · detail").
        public static string RowDetail(NotebookEntry e) =>
            e == null ? "" : e.Time.ToString("h:mm tt", C) + (string.IsNullOrEmpty(Detail(e)) ? "" : " · " + Detail(e));

        /// The toast when an entry is saved, in ONE unit (D2): "✓ Saved · Width 10¼″" (metric "… 0.26 m"),
        /// "✓ Saved · Rectangle 1.0 ft²", "✓ Saved · Level", "✓ Note saved". `tapeTarget` names the tape by what it
        /// spans ("Door"), if known.
        public static string SavedToast(NotebookEntry e, string tapeTarget = null)
        {
            if (e == null) return "";
            switch (e.Tool)
            {
                case "measure":
                    if (IsDistance(e))
                    {
                        string title = TapeTitle(e.Points[0], e.Points[1]);
                        if (!string.IsNullOrEmpty(tapeTarget) && title != "Distance") title = $"{tapeTarget} {title.ToLowerInvariant()}";
                        return $"✓ Saved · {title} {Copy.Len(e.ValueSI)}";
                    }
                    return $"✓ Saved · {ShapeTitle(e)} {Copy.Area(e.ValueSI)}";
                case "level":
                    bool plumb = IsPlumb(e), ok = Math.Abs(e.ValueSI) <= 0.2;
                    string deg = e.ValueSI.ToString("0.0", C) + "°";
                    if (plumb) return ok ? "✓ Saved · Plumb" : $"✓ Saved · Out of plumb {deg}";
                    return ok ? "✓ Saved · Level" : $"✓ Saved · Slope {deg} · {Copy.Slope(Math.Tan(e.ValueSI * Math.PI / 180.0) * 100.0)}";
                case "note": return "✓ Note saved";
                default: return "✓ Saved · " + Title(e);
            }
        }
    }
}
