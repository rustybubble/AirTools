using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace AirTools.Agent.Grok
{
    /// The kinds of 3D overlay the Grok actions draw in the scene frame (lane G2).
    public enum GrokOverlayKind { None, Plan, Survey, Coverage, Labels, SceneLabels }

    /// JSON → view models for the G2 actions (backend docs/api.md §4–§5): pure, tolerant of missing fields, no Unity
    /// objects, so they are unit-tested offline against payloads copied from the backend's tests. Every point stays in
    /// the glTF scene frame (double[3], metres or scene units, +Y up) until the renderer flips it (GltfFrame).
    public static class GrokJson
    {
        public static string Str(JToken t, string key)
        {
            var v = t is JObject o ? o[key] : null;
            return v == null || v.Type == JTokenType.Null ? null : (string)v;
        }

        public static double? Num(JToken t, string key)
        {
            var v = t is JObject o ? o[key] : null;
            if (v == null || v.Type == JTokenType.Null) return null;
            if (v.Type == JTokenType.Float || v.Type == JTokenType.Integer) return (double)v;
            return double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
        }

        public static bool Bool(JToken t, string key) =>
            t is JObject o && o[key] != null && o[key].Type == JTokenType.Boolean && (bool)o[key];

        /// [x, y, z] → double[3]; null when missing, null-valued or short (a pin whose rays all missed).
        public static double[] Vec(JToken v)
        {
            if (!(v is JArray a) || a.Count < 3) return null;
            var r = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (a[i] == null || (a[i].Type != JTokenType.Float && a[i].Type != JTokenType.Integer)) return null;
                r[i] = (double)a[i];
            }
            return r;
        }

        /// [x0, y0, x1, y1] (0–1 of the frame, top-left origin) → float[4]; null when missing or short.
        public static float[] Box(JToken v)
        {
            if (!(v is JArray a) || a.Count < 4) return null;
            var r = new float[4];
            for (int i = 0; i < 4; i++)
            {
                if (a[i] == null || (a[i].Type != JTokenType.Float && a[i].Type != JTokenType.Integer)) return null;
                r[i] = (float)a[i];
            }
            return r;
        }

        public static List<string> Strings(JToken v)
        {
            var list = new List<string>();
            if (v is JArray a) foreach (var x in a) if (x != null && x.Type != JTokenType.Null) list.Add((string)x);
            return list;
        }

        public static string Percent(double confidence) =>
            Math.Round(Math.Max(0.0, Math.Min(1.0, confidence)) * 100.0).ToString("0", CultureInfo.InvariantCulture) + " %";
    }

    // ---------------- show_plan (F7 placement planner) ----------------

    public sealed class PlanSegment
    {
        public double[] A, B;
        public double LengthM;
    }

    /// show_plan {plan_id, segments[{a, b, length_m}], points[[x,y,z]], label}. A plan without points is a run (an LED
    /// strip under the cabinets): "place them" turns it into a BOM, not parts.
    public sealed class PlanView
    {
        public string PlanId;
        public string Label;
        public readonly List<PlanSegment> Segments = new List<PlanSegment>();
        public readonly List<double[]> Points = new List<double[]>();

        public bool HasPoints => Points.Count > 0;
        public double TotalLengthM { get { double s = 0; foreach (var g in Segments) s += g.LengthM; return s; } }

        public static PlanView Parse(JObject args)
        {
            if (args == null) return null;
            var p = new PlanView { PlanId = GrokJson.Str(args, "plan_id"), Label = GrokJson.Str(args, "label") };
            if (args["segments"] is JArray segs)
                foreach (var s in segs)
                {
                    var a = GrokJson.Vec(s["a"]); var b = GrokJson.Vec(s["b"]);
                    if (a == null || b == null) continue;
                    double len = GrokJson.Num(s, "length_m") ?? Distance(a, b);
                    p.Segments.Add(new PlanSegment { A = a, B = b, LengthM = len });
                }
            if (args["points"] is JArray pts)
                foreach (var v in pts) { var q = GrokJson.Vec(v); if (q != null) p.Points.Add(q); }
            return p;
        }

        public static double Distance(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    // ---------------- survey_started / show_survey (F10 condition survey) ----------------

    public sealed class SurveyPin
    {
        public string Id, FrameId, Element, Severity, Issue, Action, PartQuery;
        public double Confidence;
        /// Scene frame point; null when every ray missed the collision mesh (list the pin, don't draw it).
        public double[] P;
        public float[] Box;
        public bool Suspected;
        public List<string> AlsoIn = new List<string>();

        /// The gaze label: "issue · 90 %" (docs/api.md §5 show_survey).
        public string LabelText => Confidence > 0 ? $"{Issue ?? Element ?? Id} · {GrokJson.Percent(Confidence)}" : (Issue ?? Element ?? Id);   // no confidence sent: no "0 %"
    }

    /// show_survey {survey_id, pins, label}, or the GET /scene/survey/{id} body (the same pins plus status, site,
    /// spoken, looked_fine, coverage_gaps, error).
    public sealed class SurveyView
    {
        public string SurveyId, Status, Site, Label, Spoken, Error;
        public readonly List<SurveyPin> Pins = new List<SurveyPin>();
        public List<string> LookedFine = new List<string>();
        public List<string> CoverageGaps = new List<string>();

        public int Drawable { get { int n = 0; foreach (var p in Pins) if (p.P != null) n++; return n; } }

        /// The backend's condition survey (F10) carries `pins` (an empty list too).
        public static bool IsCondition(JObject args) => args != null && args["pins"] != null;

        /// B1's door survey card {request_id, label, groups, unverified, focus} (now `show_tape_survey` on the backend;
        /// a legacy `show_survey` with `groups` still opens the card).
        public static bool IsDoorSurvey(JObject args) => args != null && args["groups"] != null;

        public static SurveyView Parse(JObject args)
        {
            if (args == null) return null;
            var s = new SurveyView
            {
                SurveyId = GrokJson.Str(args, "survey_id"), Status = GrokJson.Str(args, "status"), Site = GrokJson.Str(args, "site"),
                Label = GrokJson.Str(args, "label"), Spoken = GrokJson.Str(args, "spoken"), Error = GrokJson.Str(args, "error"),
                LookedFine = GrokJson.Strings(args["looked_fine"]), CoverageGaps = GrokJson.Strings(args["coverage_gaps"]),
            };
            if (args["pins"] is JArray pins)
            {
                int n = 0;
                foreach (var t in pins)
                {
                    if (!(t is JObject o)) continue;
                    n++;
                    s.Pins.Add(new SurveyPin
                    {
                        Id = GrokJson.Str(o, "id") ?? $"f{n}", FrameId = GrokJson.Str(o, "frame_id"), Element = GrokJson.Str(o, "element"),
                        Severity = (GrokJson.Str(o, "severity") ?? "").Trim().ToLowerInvariant(), Issue = GrokJson.Str(o, "issue"),
                        Action = GrokJson.Str(o, "action"), PartQuery = GrokJson.Str(o, "part_query"),
                        Confidence = GrokJson.Num(o, "confidence") ?? 0, P = GrokJson.Vec(o["p"]), Box = GrokJson.Box(o["box"]),
                        Suspected = GrokJson.Bool(o, "suspected"), AlsoIn = GrokJson.Strings(o["also_in"]),
                    });
                }
            }
            return s;
        }

        /// Pins in label order: worst first (severity, then confidence).
        public List<SurveyPin> Ranked()
        {
            var list = new List<SurveyPin>(Pins);
            list.Sort((a, b) =>
            {
                int r = SeverityStyle.Rank(b.Severity).CompareTo(SeverityStyle.Rank(a.Severity));
                return r != 0 ? r : b.Confidence.CompareTo(a.Confidence);
            });
            return list;
        }
    }

    // ---------------- show_coverage (F12 capture coach) ----------------

    public sealed class CoverageSide
    {
        public string Label;
        public double BearingDeg, Views;
        public bool Seen;
    }

    public sealed class CoverageLeg
    {
        /// "orbit", "eave_pass" or "nadir_grid".
        public string Pattern;
        public double? FromDeg, SweepDeg;
        public double GimbalPitchDeg, Radius, Altitude;
        public List<int> Sides = new List<int>();
        public bool IsArc => Pattern == "orbit" || Pattern == "eave_pass";
    }

    /// The GET /scenes/{site}/coverage body (also show_coverage's args).
    public sealed class CoverageView
    {
        public string Site, Mode, Units, Zero, Verdict, Spoken, Note;
        public int Seen;
        public double[] RingCentre, ZeroDir, QuarterDir;
        public double RingRadius;
        public readonly List<CoverageSide> Sides = new List<CoverageSide>();
        public readonly List<CoverageLeg> Legs = new List<CoverageLeg>();

        public bool Interior => Mode == "interior";
        public bool HasRing => !Interior && RingCentre != null && ZeroDir != null && QuarterDir != null && RingRadius > 0;

        public static CoverageView Parse(JObject args)
        {
            if (args == null) return null;
            var c = new CoverageView
            {
                Site = GrokJson.Str(args, "site"), Mode = GrokJson.Str(args, "mode") ?? "exterior", Units = GrokJson.Str(args, "units"),
                Zero = GrokJson.Str(args, "zero"), Verdict = GrokJson.Str(args, "verdict"), Spoken = GrokJson.Str(args, "spoken"),
                Note = GrokJson.Str(args, "note"), Seen = (int)(GrokJson.Num(args, "seen") ?? 0),
            };
            if (args["ring"] is JObject ring)
            {
                c.RingCentre = GrokJson.Vec(ring["centre"]);
                c.RingRadius = GrokJson.Num(ring, "radius") ?? 0;
                c.ZeroDir = GrokJson.Vec(ring["zero_dir"]);
                c.QuarterDir = GrokJson.Vec(ring["quarter_dir"]);
            }
            if (args["sides"] is JArray sides)
                foreach (var t in sides)
                    if (t is JObject o)
                        c.Sides.Add(new CoverageSide
                        {
                            Label = GrokJson.Str(o, "label"), BearingDeg = GrokJson.Num(o, "bearing_deg") ?? c.Sides.Count * 45.0,
                            Views = GrokJson.Num(o, "views") ?? 0, Seen = GrokJson.Bool(o, "seen"),
                        });
            if (args["legs"] is JArray legs)
                foreach (var t in legs)
                    if (t is JObject o)
                    {
                        var leg = new CoverageLeg
                        {
                            Pattern = GrokJson.Str(o, "pattern"), FromDeg = GrokJson.Num(o, "from_deg"), SweepDeg = GrokJson.Num(o, "sweep_deg"),
                            GimbalPitchDeg = GrokJson.Num(o, "gimbal_pitch_deg") ?? 0, Radius = GrokJson.Num(o, "radius") ?? 0,
                            Altitude = GrokJson.Num(o, "altitude") ?? 0,
                        };
                        if (o["sides"] is JArray s) foreach (var k in s) if (k != null && k.Type == JTokenType.Integer) leg.Sides.Add((int)k);
                        c.Legs.Add(leg);
                    }
            return c;
        }

        /// The caption: `spoken` (the agent's reply), or the interior `note`.
        public string Caption => Interior ? (Note ?? Spoken ?? "") : (Spoken ?? Note ?? "");
    }

    // ---------------- show_labels / GET /scenes/{site}/labels (F16 live labels) ----------------

    public sealed class SceneLabel
    {
        public string Id, Name, Kind, Detail, Source, Query;
        public double Confidence;
        /// Live labels: [x0, y0, x1, y1] in the frame. Pre-labelled scans: null.
        public float[] Box;
        /// Pre-labelled scans: the anchor in the scene frame. Live labels: null (raycast from the frame's camera).
        public double[] Pos;
        public List<string> Frames = new List<string>();

        /// Greyed below 0.7 confidence (docs/api.md "Unity contract (live labels)").
        public bool Greyed => Confidence < 0.7;
        /// What a tap searches for (POST /parts/search {"query"}).
        public string SearchQuery => !string.IsNullOrWhiteSpace(Query) ? Query : Name;
    }

    /// show_labels {frame_id, labels} or the GET /scenes/{site}/labels body {site, rev, label, labels[{…, pos, frames}]}.
    public sealed class LabelsView
    {
        public string FrameId, Site, Label;
        public readonly List<SceneLabel> Labels = new List<SceneLabel>();

        public static LabelsView Parse(JObject args)
        {
            if (args == null) return null;
            var v = new LabelsView { FrameId = GrokJson.Str(args, "frame_id"), Site = GrokJson.Str(args, "site"), Label = GrokJson.Str(args, "label") };
            if (args["labels"] is JArray labels)
                foreach (var t in labels)
                {
                    if (!(t is JObject o)) continue;
                    string name = GrokJson.Str(o, "name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    v.Labels.Add(new SceneLabel
                    {
                        Id = GrokJson.Str(o, "id") ?? $"l{v.Labels.Count}", Name = name, Kind = GrokJson.Str(o, "kind"),
                        Detail = GrokJson.Str(o, "detail"), Source = GrokJson.Str(o, "source"), Query = GrokJson.Str(o, "query"),
                        Confidence = GrokJson.Num(o, "confidence") ?? 0, Box = GrokJson.Box(o["box"]), Pos = GrokJson.Vec(o["pos"]),
                        Frames = GrokJson.Strings(o["frames"]),
                    });
                }
            return v;
        }

        /// Label order for the budget: surest first (more frames, then confidence).
        public List<SceneLabel> Ranked()
        {
            var list = new List<SceneLabel>(Labels);
            list.Sort((a, b) =>
            {
                int f = b.Frames.Count.CompareTo(a.Frames.Count);
                return f != 0 ? f : b.Confidence.CompareTo(a.Confidence);
            });
            return list;
        }
    }
}
