using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AirTools.Core;
using AirTools.Parts;
using UnityEngine;

namespace AirTools.UI
{
    /// Where a raw error is shown (docs/ux/specs/W0.9-copy.md §5): each surface has its own words per error kind.
    public enum ErrorSurface { Checkout, Search, PartLoad, Voice, Ask, SceneList, Export }

    /// What went wrong, classified from raw error text + HTTP status (first match wins, W0.9 §5).
    public enum ErrorKind { Unavailable, Busy, Declined, Timeout, Offline, Server, Other }

    /// How a part relates to a tape: a fastener sits on a surface (a hinge, a knob), a spanning part runs along one
    /// (a slide, a shelf), a window unit sits in an opening. Decides whether "spare" means anything.
    public enum PartKind { Fastener, Spanning, WindowUnit }

    /// Display copy, "said like a tradesperson" (UX plan W0.9, docs/ux/specs/W0.9-copy.md). Pure and allocation-light.
    /// Rule 12: raw stays raw — logs, NotebookEntry.Label, LastAction / LastError, FitReport.Headline and statuses keep
    /// their text (tests, the harness and tools/demo/hcheck.py parse them); only what's drawn goes through here.
    /// Units go through the D2 hooks below (Len, LenFull, Area, Gap, …): one unit per label, the user's (SPEC §9 D2).
    public static class Copy
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        // ---------------- D2 hooks: ONE unit on screen (SPEC §9; UiSettings.UnitSystem, imperial first) ----------------
        // The formatting itself is Units.*Primary / Pair / FormatMm (pure). Raw text (logs, NotebookEntry.Label,
        // export, requests) never comes through here: it keeps Units.Format (dual, metric first) or metres.

        static UnitSystem U => UiSettings.UnitSystem;

        /// One length on a label, toast or status: "4′ 11⅛″" (imperial) / "1.50 m" (metric).
        public static string Len(double metres) => Units.FormatPrimary(metres, U);
        /// Both units, the primary first — the notebook keeps both: "4′ 11″ · 1.50 m" / "1.50 m · 4′ 11″".
        public static string LenFull(double metres) => Units.FormatBoth(metres, U);
        /// One area: "19 ft²" / "1.80 m²".
        public static string Area(double squareMetres) => Units.FormatAreaPrimary(squareMetres, U);
        /// Both areas, the primary first (the notebook): "19.4 ft² · 1.80 m²" / "1.80 m² · 19.4 ft²".
        public static string AreaFull(double squareMetres) => Units.FormatAreaBoth(squareMetres, U);
        /// A clearance, gap or overrun: "1″" / "25 mm".
        public static string Gap(float mm) => Units.FormatMm(Mathf.Abs(mm), U);
        /// One part dimension in a fit reason: "10⅜″" / "262 mm".
        public static string Size(float mm) => Units.FormatMm(mm, U);
        /// A part's catalogue W × D × H in the chosen unit: "5 × 1½ × 1¾ in" / "127 × 38 × 45 mm".
        public static string Dims(PartDims d) => U == UnitSystem.Metric ? PartFormat.DimsMm(d) : PartFormat.DimsIn(d);
        /// The other one (the spec card's quiet second line: the listing as sold).
        public static string DimsOther(PartDims d) => U == UnitSystem.Metric ? PartFormat.DimsIn(d) : PartFormat.DimsMm(d);
        /// A window-width range: "23¼–39⅜″" / "590–1000 mm".
        public static string Range(float? min, float? max) => Units.FormatRangeMm(min, max, U);
        /// "2.1 oz" / "60 g".
        public static string Weight(float grams) => Units.FormatWeight(grams, U);
        /// A slope as today's grade: "3.5% slope" (unitless: not a D2 unit).
        public static string Slope(double percentGrade) => percentGrade.ToString("0.0", C) + "% slope";
        /// The scale keypad's unit, a word ("Type the real size in inches"): "inches" / "cm".
        public static string KeypadUnit => Units.KeypadUnit(U);
        /// A typed keypad value in metres (inches or cm).
        public static double KeypadMetres(double typed) => Units.KeypadMetres(typed, U);
        /// Metric lengths inside text we don't word (server check details, the ladder rule), in the chosen unit.
        public static string Lengths(string text) => Units.ConvertLengths(text, U);

        // ---------------- fit ----------------

        /// ✓ fits · ! check · ✗ won't fit (the word comes from the verdict; never colour alone). ✗ (U+2717), not the spec's ✕:
        /// Inter has no U+2715, so it would fall back to a box.
        public static string Glyph(FitStatus s) => s switch
        {
            FitStatus.Green => "✓",
            FitStatus.Amber => "!",
            FitStatus.Red => "✗",
            _ => "",
        };

        /// "✓ Fits the door" — glyph + verdict (the one fit line shown in toasts, callouts and status).
        public static string FitLine(FitReport r) => r == null ? "" : (Glyph(r.Status) + " " + (r.Verdict ?? "")).Trim();

        // ---------------- part nouns (W0.9 §4.4) ----------------

        static readonly (string query, string noun)[] s_QueryNouns =
        {
            ("cabinet hinge", "hinge"), ("drawer slide", "slide"), ("shelf bracket", "bracket"), ("gutter hanger", "hanger"),
            ("window ac", "AC unit"), ("cabinet knob", "knob"),
        };

        static readonly string[] s_NameNouns = { "hinge", "knob", "pull", "handle", "hanger", "bracket", "slide", "shelf", "screw" };

        /// The short noun for a part: from the chip query that found it when known ("cabinet hinge" → hinge), else from
        /// its name (first hit wins), else "part". Lower case.
        public static string Noun(PartSpec spec, string query = null)
        {
            if (!string.IsNullOrEmpty(query))
            {
                var q = query.Trim().ToLowerInvariant();
                foreach (var (k, n) in s_QueryNouns) if (q == k) return n;
            }
            var name = spec?.name != null ? " " + spec.name.ToLowerInvariant() + " " : "";
            if (name.Contains("air conditioner") || name.Contains("window ac") || name.Contains(" btu")) return "AC unit";
            foreach (var n in s_NameNouns) if (name.Contains(n)) return n;
            if (!string.IsNullOrEmpty(query))
            {
                var q = query.Trim().ToLowerInvariant();
                foreach (var n in s_NameNouns) if (q.Contains(n)) return n;
            }
            return "part";
        }

        public static string Plural(string noun) =>
            string.IsNullOrEmpty(noun) ? "parts" : noun == "shelf" ? "shelves" : noun.EndsWith("s") ? noun : noun + "s";

        /// "hinge" → "Hinge" ("AC unit" stays).
        public static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        public static PartKind KindOf(PartSpec spec, string query = null)
        {
            if (spec != null && spec.HasWindowRange) return PartKind.WindowUnit;
            var noun = Noun(spec, query);
            var name = spec?.name?.ToLowerInvariant() ?? "";
            switch (noun)
            {
                case "hinge": case "knob": case "pull": case "handle": case "hanger": case "bracket": case "screw":
                    return PartKind.Fastener;
                case "slide": case "shelf": case "AC unit":
                    return PartKind.Spanning;
            }
            foreach (var w in new[] { "catch", "latch", "hook", "clip", "anchor" }) if (name.Contains(w)) return PartKind.Fastener;
            foreach (var w in new[] { "rod", "rail", "panel", "board" }) if (name.Contains(w)) return PartKind.Spanning;
            return PartKind.Fastener;   // so "spare" is never said about unknown hardware
        }

        /// What the part mounts on, from mount.surface: "the door", "the fascia", … else "a surface".
        public static string MountTarget(PartSpec spec)
        {
            var s = spec?.mount?.surface?.Trim().ToLowerInvariant();
            switch (s)
            {
                case "door": case "cabinet door": return "the door";
                case "window": return "the window";
                case "side": return "the cabinet side";
                case "bottom": return "the drawer bottom";
                case "fascia": return "the fascia";
                case "sill": return "the sill";
                default: return "a surface";
            }
        }

        /// "exact-size box" for proxies, "maker's 3D model" for CAD… (PartAsset.Badge stays for data).
        public static string TierBadge(string tier) => (tier ?? "").Trim().ToLowerInvariant() switch
        {
            "cad" => "maker's 3D model",
            "library" => "library 3D model",
            "ai_mesh" => "AI model · exact size",
            _ => "exact-size box",
        };

        // ---------------- scrub (W0.9 §6.1) ----------------

        static readonly Regex s_Mock = new Regex(@"\s*\(mock\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex s_Spec = new Regex(@"\s*\(?SPEC\s*§[\d.]+\)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex s_MockFixture = new Regex(@"^[ \t]*mock fixture\b.*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
        static readonly Regex s_Url = new Regex(@"(?:https?|mock)://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex s_Path = new Regex(@"\b(Wall|Gutter|Window|Ledge)/\w+\b", RegexOptions.CultureInvariant);
        static readonly Regex s_Collision = new Regex(@"Collision r\d+/geometry_\d+", RegexOptions.CultureInvariant);
        static readonly Regex s_Scan = new Regex(@"\bthe (\w+) scan\b", RegexOptions.CultureInvariant);
        static readonly Regex s_Paren = new Regex(@"\s*\((?:sandbox|server|calibration)[^)]*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex s_Dash = new Regex(@"[ \t]+(?:—|--)[ \t]+", RegexOptions.CultureInvariant);
        static readonly Regex s_Spaces = new Regex(@"[ \t]{2,}", RegexOptions.CultureInvariant);
        static readonly Regex s_EmptyLines = new Regex(@"\n[ \t]*(?=\n|$)", RegexOptions.CultureInvariant);

        /// Scrub developer text at display time: "(mock)", "SPEC §…", URLs, collider paths, "the kitchen scan",
        /// sandbox/server/calibration asides, " — " → " · ". Never touches rich-text tags.
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf("mock", StringComparison.OrdinalIgnoreCase) >= 0) s = s_Mock.Replace(s, "");
            if (s.IndexOf("SPEC", StringComparison.OrdinalIgnoreCase) >= 0) s = s_Spec.Replace(s, "");
            if (s.IndexOf("mock fixture", StringComparison.OrdinalIgnoreCase) >= 0) s = s_MockFixture.Replace(s, "");
            if (s.IndexOf("://", StringComparison.Ordinal) >= 0) s = s_Url.Replace(s, "");
            if (s.IndexOf('/') >= 0)
            {
                s = s_Path.Replace(s, m => SurfaceNames.FromPath(m.Value));
                s = s_Collision.Replace(s, "the scan");
            }
            if (s.IndexOf(" scan", StringComparison.Ordinal) >= 0) s = s_Scan.Replace(s, "the $1");
            if (s.IndexOf('(') >= 0) s = s_Paren.Replace(s, "");
            if (s.IndexOf("—", StringComparison.Ordinal) >= 0 || s.IndexOf("--", StringComparison.Ordinal) >= 0) s = s_Dash.Replace(s, " · ");
            s = s_Spaces.Replace(s, " ");
            s = s_EmptyLines.Replace(s, "");
            return TrimSeparators(s);
        }

        static string TrimSeparators(string s)
        {
            s = s.Trim();
            bool changed = true;
            while (changed && s.Length > 0)
            {
                changed = false;
                foreach (var sep in new[] { "·", ",", "—", "–" })
                {
                    if (s.StartsWith(sep, StringComparison.Ordinal)) { s = s.Substring(sep.Length).TrimStart(); changed = true; }
                    if (s.EndsWith(sep, StringComparison.Ordinal)) { s = s.Substring(0, s.Length - sep.Length).TrimEnd(); changed = true; }
                }
            }
            return s;
        }

        /// ≤ max characters, cut at the last sentence end, else the last space; "…" appended when cut.
        public static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            var head = s.Substring(0, Mathf.Max(1, max - 1));
            int cut = Mathf.Max(head.LastIndexOf(". ", StringComparison.Ordinal), Mathf.Max(head.LastIndexOf("! ", StringComparison.Ordinal), head.LastIndexOf("? ", StringComparison.Ordinal)));
            if (cut >= max / 2) return head.Substring(0, cut + 1);
            int space = head.LastIndexOf(' ');
            if (space >= max / 3) head = head.Substring(0, space);
            return TrimSeparators(head.TrimEnd(',', ';', ':', ' ')) + "…";
        }

        /// "MOCK1A2B3C" → "1A2B3C".
        public static string ApprovalCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            if (code.StartsWith("MOCK-", StringComparison.OrdinalIgnoreCase)) return code.Substring(5);
            if (code.StartsWith("MOCK", StringComparison.OrdinalIgnoreCase)) return code.Substring(4);
            return code;
        }

        /// A citation URL as a person reads it: "Home Depot listing", "Google Shopping"; "" for sample/mock links.
        public static string SourceLabel(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            var u = url.Trim();
            if (u.StartsWith("mock://", StringComparison.OrdinalIgnoreCase)) return "";
            string host;
            try { host = new Uri(u).Host.ToLowerInvariant(); }
            catch (UriFormatException) { return ""; }
            if (host.Contains("example.com")) return "";
            if (host.EndsWith("homedepot.com")) return "Home Depot listing";
            if (host.EndsWith("lowes.com")) return "Lowe's listing";
            if (host.Contains("amazon.")) return "Amazon listing";
            if (host.EndsWith("walmart.com")) return "Walmart listing";
            if (host.EndsWith("bestbuy.com")) return "Best Buy listing";
            if (host.Contains("google.")) return "Google Shopping";
            return host.StartsWith("www.") ? host.Substring(4) : host;
        }

        /// Sample data (the shipped fixtures): a mock asset, a "(mock)" seller or maker, or an example.com spec link.
        public static bool IsSample(PartSpec spec)
        {
            if (spec == null) return false;
            if (spec.asset?.source_url != null && spec.asset.source_url.StartsWith("mock://", StringComparison.OrdinalIgnoreCase)) return true;
            if (spec.manufacturer != null && spec.manufacturer.IndexOf("(mock)", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (spec.sellers != null) foreach (var s in spec.sellers) if (s?.name != null && s.name.IndexOf("(mock)", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return spec.spec_url != null && spec.spec_url.IndexOf("example.com", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static readonly Regex s_Stars = new Regex(@"^\s*([\d.]+)\s*★\s*(?:\(\s*\d+\s*reviews?\s*\))?\s*$", RegexOptions.CultureInvariant);

        /// The server's recommendation reason, cleaned: drops "best match" and impossible ratings ("4406.0★").
        public static string Reason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "";
            var parts = new List<string>();
            foreach (var raw in Clean(reason).Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = raw.Trim();
                if (p.Length == 0 || p.Equals("best match", StringComparison.OrdinalIgnoreCase)) continue;
                var m = s_Stars.Match(p);
                if (m.Success)
                {
                    if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, C, out var r) && r > 0 && r <= 5) parts.Add("rated " + r.ToString("0.0", C));
                    continue;
                }
                parts.Add(p);
            }
            return Clip(string.Join(", ", parts), 48);
        }

        // ---------------- errors (W0.9 §5) ----------------

        public static ErrorKind Classify(string raw, long http = -1)
        {
            var r = raw ?? "";
            bool Has(string s) => r.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
            if (http == 503 || Has("offline on the laptop") || Has("need the live model") || Has("needs the live model")) return ErrorKind.Unavailable;
            if (http == 429 || Has("429") || Has("Too Many Requests") || Has("rate limit")) return ErrorKind.Busy;
            if (r.TrimStart().StartsWith("declined", StringComparison.OrdinalIgnoreCase)) return ErrorKind.Declined;
            if (Has("timed out") || Has("timeout")) return ErrorKind.Timeout;
            if (http == 0 || Has("Cannot connect") || Has("Cannot resolve") || Has("Connection refused") || Has("Network is unreachable")
                || Has("Unable to complete SSL") || Has("the laptop server is offline") || Has("no response") || Has("not running")) return ErrorKind.Offline;
            if (http >= 500 || Has("500") || Has("502") || Has("504") || Has("Internal Server Error") || Has("Bad Gateway")) return ErrorKind.Server;
            return ErrorKind.Other;
        }

        /// Human copy for a raw error on a surface. The raw text stays in LastError and the logs.
        public static string Error(ErrorSurface where, string raw, long http = -1)
        {
            var k = Classify(raw, http);
            switch (where)
            {
                case ErrorSurface.Checkout:
                    return k switch
                    {
                        ErrorKind.Offline => "Couldn't reach the laptop · nothing was charged",
                        ErrorKind.Timeout => "The laptop didn't answer · nothing was charged",
                        ErrorKind.Busy => "Payments are busy · nothing was charged",
                        ErrorKind.Unavailable => "Payments are off on the laptop · nothing was charged",
                        ErrorKind.Declined => "Card declined (test card) · nothing was charged",
                        ErrorKind.Server => "Payment service error · nothing was charged",
                        _ => "Payment didn't go through · nothing was charged",
                    };
                case ErrorSurface.Search:
                    return k switch
                    {
                        ErrorKind.Offline => "Laptop not connected · try a part below",
                        ErrorKind.Timeout => "Search took too long · try a part below",
                        ErrorKind.Busy => "Search is busy · try again in a minute",
                        ErrorKind.Unavailable => "Search is off on the laptop · try a part below",
                        _ => "Search didn't finish · try a part below",
                    };
                case ErrorSurface.PartLoad:
                    return "Couldn't load this part · try another";
                case ErrorSurface.Voice:
                    return k switch
                    {
                        ErrorKind.Offline => "Couldn't reach the laptop · use the buttons",
                        ErrorKind.Timeout => "The assistant took too long · try again",
                        ErrorKind.Busy => "The assistant is busy · try again soon",
                        ErrorKind.Unavailable => "Voice is offline · use the buttons",
                        ErrorKind.Server => "Something went wrong · try again",
                        _ => "Didn't catch that · try again",
                    };
                case ErrorSurface.Ask:
                    return k switch
                    {
                        ErrorKind.Offline => "Couldn't reach the laptop · try again",
                        ErrorKind.Timeout => "The assistant took too long · try again",
                        ErrorKind.Busy => "The assistant is busy · try again soon",
                        ErrorKind.Unavailable => "Can't answer now · the assistant is offline",
                        _ => "Couldn't answer that · try again",
                    };
                case ErrorSurface.SceneList:
                    return "No scenes on the laptop · check the connection";
                default:
                    return k == ErrorKind.Offline || k == ErrorKind.Timeout ? "Saved on the headset · laptop not connected" : "Couldn't save the report · try again";
            }
        }

        // ---------------- measured mandate (B3) ----------------

        public const string MandateChecking = "Checking your order…";
        public const string MandateStillChecking = "Still checking your order · hold Pay again";

        /// Why Pay is off: the first failing check's words ("$23.84 is over your $20 limit").
        public static string MandateBlocked(string failingDetail) =>
            string.IsNullOrEmpty(failingDetail) ? "A check failed · nothing was charged" : $"Can't pay: {Lengths(Clean(failingDetail))}";

        /// A refused /checkout (hand-off §5.4): 400 short hold, 409 stale proof or changed cart, 422 a check failed.
        public static string MandateRefusal(long code, string detail) => code switch
        {
            400 => "Hold Pay for the full second",
            409 => "The order changed · check the new total",
            422 => MandateBlocked(detail),
            _ => Error(ErrorSurface.Checkout, detail, code),
        };

        /// The wrist / palm chip: "≤ $40 · by Fri · fastest".
        public static string LimitsChip(AirTools.Parts.MandateIntent intent) => AirTools.Parts.MandateText.Limits(intent);

        // ---------------- survey (B1) ----------------

        /// A survey object's one label (W × H, D2): "10⅜ × 11″" / "262 × 279 mm". Raw text uses Units.PairMm.
        public static string WxH(double wM, double hM) => Units.FormatPair(wM, hM, U);

        // ---------------- scene parts ----------------

        /// A removed part's cavity (one label, D2): "17¼ × 23¼ × 17½″ · estimated" / "437 × 591 × 445 mm · estimated".
        /// Always "estimated": the box is extended from the neighbours, never seen (docs/api.md cavity.estimated).
        public static string Cavity(double wM, double hM, double dM) => $"{Units.FormatTriple(wM, hM, dM, U)} · estimated";

        /// The status line when a part comes out: "Dishwasher out · 17¼ × 23¼ × 17½″ · estimated".
        public static string PartOut(string name, double? wM, double? hM, double? dM) =>
            wM.HasValue && hM.HasValue && dM.HasValue ? $"{Cap(name)} out · {Cavity(wM.Value, hM.Value, dM.Value)}" : $"{Cap(name)} out";

        /// … and when it goes back: "Dishwasher back".
        public static string PartBack(string name) => $"{Cap(name)} back";

        // ---------------- delete-undo: removals say they can be undone (the ring's Undo; controllers' A / X too) ----------------

        /// A placed part removed (the spec card's Remove, the context menu's Delete, voice): "Hanger removed · Undo on the ring".
        public static string PartRemoved(string name) => $"{Cap(string.IsNullOrWhiteSpace(name) ? "part" : name.Trim())} removed · Undo on the ring";

        /// Clear: "3 parts cleared · Undo on the ring".
        public static string PartsCleared(int n) => $"{(n == 1 ? "1 part" : $"{n} parts")} cleared · Undo on the ring";

        // e2e (the replace flow: docs/demo-prompts.md)
        /// "Measure the gap" with no part out.
        public const string NoGap = "Take a part out first · Settings ▸ Take out";
        /// None of the three tapes took (a gap too small to tape).
        public const string GapNotTaped = "Couldn't tape the gap · measure it yourself";

        /// After the gap tapes: "Dishwasher gap · 23⅝ × 32¼ × 22⅞″" (W × H × D, one unit).
        public static string GapMeasured(string noun, double wM, double hM, double dM) =>
            $"{Cap(noun)} gap · {Units.FormatTriple(wM, hM, dM, U)}";

        /// After "the opening is 34½ inches tall": "Scale set from the gap's height · 34½″ · ×1.48" (the assumption said).
        public static string GapScale(string axisWord, double realMetres, float factor) =>
            $"Scale set from the gap's {axisWord} · {Len(realMetres)} · ×{factor.ToString("0.00", C)}";

        /// While a model loads for the gap: "Loading GE 24 in. Built-In… · 2 of 3".
        public static string GapLoading(string name, int index, int count) => $"Loading {name} · {index + 1} of {count}";

        /// The model standing in the gap: "GE 24 in. Built-In · 2 of 3 · ✓ Fits the gap · ⅜″ spare".
        public static string GapModel(string name, int index, int count, string fitLine) =>
            string.IsNullOrEmpty(fitLine) ? $"{name} · {index + 1} of {count}" : $"{name} · {index + 1} of {count} · {fitLine}";

        /// The heads-up line when the headset did a spoken step the server didn't: "Took out the dishwasher · on the headset".
        public static string OnHeadset(string line) => string.IsNullOrEmpty(line) ? "Done on the headset" : $"{Cap(line)} · on the headset";

        /// "cabinet_door" → "cabinet door", plural "cabinet doors".
        public static string SurveyNoun(string label, int count = 1)
        {
            string n = string.IsNullOrEmpty(label) || label == "any" ? "object" : label.Replace('_', ' ');
            return count == 1 ? n : Plural(n);
        }

        /// VoiceClient.Status (raw, logged and parsed) → what the Scene window shows.
        public static string VoiceStatus(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw.StartsWith("Allow the microphone", StringComparison.Ordinal)) return "Allow the mic, then tap Talk again";   // voice
            if (raw == "No microphone") return "No microphone found";
            if (raw.StartsWith("Microphone ", StringComparison.Ordinal) && raw.EndsWith("didn't start", StringComparison.Ordinal)) return "The mic didn't start · try again";
            if (raw == "Hold Talk while you speak") return "Keep holding while you speak";
            if (raw == "No agent client") return "Voice isn't available";
            if (raw == "No reply") return "No reply · try again";
            if (raw == "Listening…" || raw == "Thinking…") return raw;
            if (raw.StartsWith("Listening…", StringComparison.Ordinal)) return raw;   // voice: "Listening… tap to send"
            if (raw == "Didn't catch that")   // voice: no speech heard; settings-assets: the verb follows Settings ▸ Talk
                return AirTools.Core.UserPrefs.Talk == AirTools.Core.TalkStyle.Hold ? "Didn't catch that · hold Talk and speak" : "Didn't catch that · tap Talk and speak";
            if (raw == "Simulated · not sent") return raw;   // voice: VoiceClient.SimulateMic(send: false)
            int arrow = raw.IndexOf("” → ", StringComparison.Ordinal);
            if (raw.StartsWith("“", StringComparison.Ordinal) && arrow > 0)
                return raw.Substring(0, arrow + 1) + " · " + Clip(Clean(raw.Substring(arrow + 4)), 60);
            return Error(ErrorSurface.Voice, raw);
        }

        /// NotebookController.ExportStatus (raw) → the notebook window's status line.
        public static string ExportStatus(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw == "idle") return "";
            if (raw == "exporting") return "Saving report…";
            if (raw == "uploaded") return "✓ Sent to the laptop";
            if (raw == "up to date") return "✓ Already sent";
            if (raw.StartsWith("saved (", StringComparison.Ordinal)) return "Saved on the headset · laptop not connected";
            if (raw.StartsWith("saved", StringComparison.Ordinal)) return "Saved on the headset";
            return "Couldn't save the report · try again";
        }

        /// A search job's stage ("checking the spec sheets") → a short status; "done" → "".
        public static string Stage(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return "";
            var s = stage.Trim().ToLowerInvariant();
            if (s == "done") return "";
            if (s.Contains("spec")) return "Checking specs…";
            if (s.Contains("candidate") || s.Contains("compar")) return "Comparing parts…";
            if (s.Contains("fit")) return "Checking the fit…";
            if (s.Contains("search") || s.Contains("shop") || s.Contains("store")) return "Searching stores…";
            return UiText.Sentence(Clean(stage)).TrimEnd('.', '…') + "…";
        }

        /// The place, for people: "kitchen", "test facade" (the built-in scene). modelview: the scans the switcher shows
        /// get short names that also read mid-sentence ("Loading the Zabel gym…"): "Zabel gym", "hospital", and the Georgia
        /// Tech LCC scans "GT canopy" / "GT pavilion" / "GT tower" (any gt-lcc-&lt;name&gt; → "GT &lt;name&gt;").
        public static string SiteName(string site)
        {
            if (string.IsNullOrWhiteSpace(site) || site == "built-in" || site.Contains("synthetic") || site.Contains("facade")) return "test facade";
            // modelview
            switch (site)
            {
                case "zabel-gymnasium": return "Zabel gym";
                case "hospital-bg": return "hospital";
            }
            if (site.StartsWith("gt-lcc-", System.StringComparison.Ordinal) && site.Length > 7) return "GT " + site.Substring(7).Replace('_', ' ').Replace('-', ' ');
            // end modelview
            return site.Replace('_', ' ').Replace('-', ' ');
        }

        /// Notebook title summary: "2 measurements · 1 level · 1 part"; "" when empty.
        public static string NotebookSummary(IReadOnlyList<AirTools.Notes.NotebookEntry> entries)
        {
            if (entries == null || entries.Count == 0) return "";
            int measure = 0, level = 0, part = 0, order = 0, note = 0;
            foreach (var e in entries)
            {
                switch (e?.Tool)
                {
                    case "measure": measure++; break;
                    case "level": level++; break;
                    case "part": case "array": part++; break;
                    case "purchase": order++; break;
                    case "note": note++; break;
                }
            }
            var bits = new List<string>(3);
            void Add(int n, string one, string many) { if (n > 0 && bits.Count < 3) bits.Add($"{n} {(n == 1 ? one : many)}"); }
            Add(measure, "measurement", "measurements");
            Add(level, "level", "levels");
            Add(part, "part", "parts");
            Add(order, "order", "orders");
            Add(note, "note", "notes");
            return string.Join(" · ", bits);
        }

        /// Hands vs controllers wording.
        public static string Pinch(bool controllers) => controllers ? "pull the trigger" : "pinch";

        // Grok G1 ---------------------------------------------------------------------------------------------------
        // Honesty labels (backend docs/api.md §2 asset tiers, §7, POST /parts/{id}/postcard), and the checkout's
        // recall words. Shown verbatim where the backend gives them.

        /// The spec card's model line by asset.tier: "maker's 3D model" (cad), "AI-made model · exact size" (llm: an
        /// LLM-chosen template; scad: Grok-written OpenSCAD), "AI mesh · exact size, approximate look" (ai_mesh),
        /// "exact-size box" (proxy).
        public static string TierLabel(string tier) => (tier ?? "").Trim().ToLowerInvariant() switch
        {
            "llm" => "AI-made model · exact size",
            "scad" => "AI-made model · exact size",
            "ai_mesh" => "AI mesh · exact size, approximate look",
            _ => TierBadge(tier),
        };

        /// The postcard's label, always shown with it.
        public const string PostcardLabel = "AI preview, not to scale";

        /// The receipt's safety line for receipt.safety_verdict; "" for clear / unknown / none.
        public static string ReceiptSafety(string verdict) => (verdict ?? "").Trim().ToLowerInvariant() switch
        {
            "recalled" => "⚠ RECALLED · this model has a safety recall",
            "caution" => "⚠ Caution · owners report a recurring defect",
            _ => "",
        };

        /// The checkout panel's notice for a part known to be recalled (the pay panel still opens; the agent said it).
        public static string CheckoutRecall(string headline) =>
            "⚠ RECALLED" + (string.IsNullOrWhiteSpace(headline) ? " · check the recall before you buy" : $" · {Clean(headline)}");
    }
}
