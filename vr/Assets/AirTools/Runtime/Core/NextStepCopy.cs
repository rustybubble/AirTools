using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AirTools.Parts;

namespace AirTools.Core
{
    /// One status (or coach) line: `Hands` and `Controllers` (null = same as hands) for a rule and a copy variant.
    public readonly struct CopyRow
    {
        public readonly RuleId Rule;
        public readonly CoachRuleId Coach;
        public readonly string Variant, Hands, Controllers;

        public CopyRow(RuleId rule, CoachRuleId coach, string variant, string hands, string controllers)
        {
            Rule = rule; Coach = coach; Variant = variant ?? ""; Hands = hands; Controllers = controllers;
        }

        public string For(bool controllers) => controllers && Controllers != null ? Controllers : Hands;
    }

    /// Every word the guide rail says (UX W1.3 §3.2 / §3.6 / §4), keyed by (rule, variant, modality), so W0.9 copy and
    /// the D2 units decision edit one file. Placeholders ({len}, {part}, {target}…) are filled by Fill. Budgets
    /// (tested): a status ≤ 48 characters with the longest expansions, a pill label ≤ 30, a coach line ≤ 48; the
    /// controllers copy never says "pinch". Glyphs: ✓ and ✗ only (Inter has no ✕ or ☰, so the menu button is named).
    public static class NextStepCopy
    {
        public const int StatusBudget = 48, LabelBudget = 30, CoachBudget = 48, SellerChars = 14;

        static CopyRow S(RuleId r, string variant, string hands, string controllers = null) => new CopyRow(r, CoachRuleId.None, variant, hands, controllers);
        static CopyRow C(CoachRuleId c, string variant, string hands, string controllers = null) => new CopyRow(RuleId.R55, c, variant, hands, controllers);

        /// Status copy (R01 variants are Refusal names; R02 variants are "undo[.kind]" / "redo[.kind]").
        public static readonly CopyRow[] StatusRows =
        {
            S(RuleId.R01, nameof(Refusal.MeasureMiss), "Nothing there · aim at a surface"),
            S(RuleId.R01, nameof(Refusal.DuplicatePoint), "Same spot · pinch the other {point}", "Same spot · trigger on the other {point}"),
            S(RuleId.R01, nameof(Refusal.FinishTooSoon), "Pinch two points first", "Trigger on two points first"),
            S(RuleId.R01, nameof(Refusal.TeleportNotFlat), "Aim at the floor to walk"),
            S(RuleId.R01, nameof(Refusal.TeleportNothing), "Nothing there · aim at the floor"),
            S(RuleId.R01, nameof(Refusal.PartNoSurface), "Aim at a surface to place it"),
            S(RuleId.R01, nameof(Refusal.LevelMiss), "Aim at a surface to check level"),
            S(RuleId.R01, nameof(Refusal.NothingToUndo), "Nothing to undo"),
            S(RuleId.R01, nameof(Refusal.ArraySingleUnit), "One-off part · place each one by hand"),
            S(RuleId.R01, nameof(Refusal.ArrayNoTape), "Tape the run first, then fill it"),
            S(RuleId.R01, nameof(Refusal.SellersNoPart), "Place a part first"),
            S(RuleId.R01, nameof(Refusal.EdgesCross), AirTools.Tools.MeasureTool.CrossHint),

            S(RuleId.R02, "undo", "Undone"),
            S(RuleId.R02, "undo.tape", "Undone · {len} tape"),
            S(RuleId.R02, "undo.area", "Undone · area"),
            S(RuleId.R02, "undo.level", "Undone · level"),
            S(RuleId.R02, "undo.point", "Undone · point"),
            S(RuleId.R02, "undo.part", "Undone · {part}"),
            S(RuleId.R02, "redo", "Redone"),
            S(RuleId.R02, "redo.tape", "Redone · {len} tape"),
            S(RuleId.R02, "redo.area", "Redone · area"),
            S(RuleId.R02, "redo.level", "Redone · level"),
            S(RuleId.R02, "redo.point", "Redone · point"),
            S(RuleId.R02, "redo.part", "Redone · {part}"),

            S(RuleId.R03, "uploaded", "Sent to the laptop ✓"),
            S(RuleId.R03, "saved", "Saved on the headset · laptop not connected"),
            S(RuleId.R03, "error", "Couldn't save the report · try again"),
            S(RuleId.R04, "", "See your {part} on the table"),
            S(RuleId.R04, "model", "See your {part} beside the model"),
            S(RuleId.R05, "", "Touch Enter world to step into {place}", "Push Enter world with your controller"),
            S(RuleId.R05, "d5", "Point at Enter world and pinch", "Point at Enter world, pull the trigger"),
            S(RuleId.R06, "", "Step back in when you're ready"),
            S(RuleId.R07, "", "Loading {place}…"),
            S(RuleId.R08, "", "Authorizing with Visa…"),
            S(RuleId.R09, "", "Couldn't reach the laptop · nothing charged"),
            S(RuleId.R10, "", "Hold to pay {total} · keep pressing 1 s", "Push Pay with the controller, hold 1 s"),
            S(RuleId.R10, "d5", "Hold to pay {total} · keep pressing 1 s", "Point at Pay, hold the trigger 1 s"),
            S(RuleId.R11, "", "Paid {total} · receipt in your notebook"),
            S(RuleId.R12, "", "Saved an offline receipt · {total}, no charge"),
            S(RuleId.R13, "", "Listening… tap to send"),   // voice: a tap listens until you stop talking
            S(RuleId.R13, "hold", "Listening… release to send"),   // voice: hold-to-talk
            S(RuleId.R14, "", "Asking the assistant…"),
            S(RuleId.R15, "", "Don't spin: the ring puts the part away"),
            S(RuleId.R16, "settle", "Spin to a tool · pinch the lens for actions", "Trigger-drag to spin · menu button closes it"),
            S(RuleId.R16, "pinch", "Spin to a tool, then pinch the lens", "Spin, trigger the lens · menu button closes it"),
            S(RuleId.R17, "", "Send {n} readings to the laptop"),
            S(RuleId.R17, "one", "Send 1 reading to the laptop"),
            S(RuleId.R17, "empty", "Nothing saved yet · measure something"),
            S(RuleId.R18, "uploaded", "Sent to the laptop ✓"),
            S(RuleId.R18, "saved", "Saved on the headset · laptop not connected"),
            S(RuleId.R18, "error", "Couldn't save the report · try again"),
            S(RuleId.R19, "tape", "Finding {parts} that fit {len}…"),
            S(RuleId.R19, "", "Finding {parts}…"),
            S(RuleId.R20, "", "Searching the supply shops…"),
            S(RuleId.R21, "", "Getting the {part} ready…"),
            S(RuleId.R22, "", "Couldn't load that {part} · try the next"),
            S(RuleId.R23, "", "Hold the {part} again · it was put away"),
            S(RuleId.R24, "target", "Aim at the {target} to place the {part}"),
            S(RuleId.R24, "", "Aim at a surface to place the {part}"),
            S(RuleId.R25, "", "Pinch to place it · it fits here ✓", "Pull the trigger to place it · it fits ✓"),
            S(RuleId.R26, "", "Try another spot · it won't fit here"),
            S(RuleId.R27, "", "Pinch to place it here", "Pull the trigger to place it here"),
            S(RuleId.R28, "", "Checking sellers…"),
            S(RuleId.R29, "", "Buy from {seller} · {price} delivered"),
            S(RuleId.R29, "none", "No sellers listed for this part"),
            S(RuleId.R30, "", "Now pinch the other {point}", "Now trigger on the other {point}"),
            S(RuleId.R31, "", "Pinch your left hand to save {len}", "Press B to save {len}"),
            S(RuleId.R32, "", "Pinch your left hand to save {area}", "Press B to save {area}"),
            S(RuleId.R32, "cross", AirTools.Tools.MeasureTool.CrossHint),
            S(RuleId.R32, "open", "Keep going · closing here would cross"),
            S(RuleId.R33, "", "Take your {part} home · it's paid for"),
            S(RuleId.R34, "", "✓ Fits the {target}"),
            S(RuleId.R35, "", "✓ Fits · nothing in the way"),
            S(RuleId.R36, "", "✗ Won't fit here · see other {parts}"),
            S(RuleId.R37, "wide", "✗ Too wide for the {target} · see others"),
            S(RuleId.R37, "narrow", "✗ Too narrow for the {target} · see others"),
            S(RuleId.R37, "", "✗ Won't fit here · see other {parts}"),
            S(RuleId.R38, "tape", "! Measure the window to check the fit"),
            S(RuleId.R38, "overhang", "! Check it: overhangs the edge"),
            S(RuleId.R38, "mount", "! Check it: not on a surface"),
            S(RuleId.R38, "clearance", "! Check it: needs clearance"),
            S(RuleId.R38, "", "! Check the fit before you buy"),
            S(RuleId.R39, "true", "Pick a {part} · shown at true size"),
            S(RuleId.R39, "", "Pick a {part} to hold it"),
            S(RuleId.R39, "offline", "Pick a sample {part} · laptop offline"),
            S(RuleId.R40, "", "Try again · parts search is offline"),
            S(RuleId.R41, "tape", "Try another part · no {parts} for {len}"),
            S(RuleId.R41, "", "Try another part · no {parts} found"),
            S(RuleId.R42, "", "Saved {len} ✓"),
            S(RuleId.R43, "", "Saved {len} ✓"),
            S(RuleId.R44, "", "Saved {area} ✓"),
            S(RuleId.R45, "", "Measure the model · tapes read full size"),
            S(RuleId.R45, "pin", "Pinch the pin to step inside", "Trigger the pin to step inside"),
            S(RuleId.R46, "", "Reload {start} · this is the sample wall"),
            S(RuleId.R46, "failed", "Couldn't load {start} · Retry"),   // fix-ux: a load that failed or timed out
            S(RuleId.R47, "", "Set the scale: tape a door you know"),
            S(RuleId.R48, "aim", "Aim at {flat} · pinch to check level", "Aim at {flat} · pull the trigger"),
            S(RuleId.R48, "logged", "Logged {angle} · pinch another surface", "Logged {angle} · trigger on another surface"),
            S(RuleId.R49, "", "Pinch the floor to walk · or measure", "Trigger at the floor to walk · or measure"),
            S(RuleId.R50, "", "Pinch the floor to walk there", "Pull the trigger at the floor to walk"),
            S(RuleId.R51, "", "Pinch a placed part to move it", "Trigger on a placed part to move it"),
            S(RuleId.R52, "", "Measure {entry}: pinch one {point}", "Measure {entry}: trigger on one {point}"),
            S(RuleId.R53, "", "Pinch one {point} to measure again", "Trigger on one {point} to measure again"),
            S(RuleId.R54, "", "Turn your left palm up for tools", "Press the left menu button for tools"),
            S(RuleId.R55, "", ""),
        };

        /// Coach copy (§4), same conventions.
        public static readonly CopyRow[] CoachRows =
        {
            C(CoachRuleId.C01, "", "Touch the Enter world button", "Push Enter world with your controller"),
            C(CoachRuleId.C01, "d5", "Point at Enter world and pinch", "Point at Enter world, pull the trigger"),
            C(CoachRuleId.C02, "", "Pinch the floor to walk there", "Point at the floor, pull the trigger"),
            C(CoachRuleId.C03, "", "Point at {corner} and pinch", "Point at {corner}, pull the trigger"),
            C(CoachRuleId.C04, "", "Now pinch the other {point}", "Now pull the trigger on the other {point}"),
            C(CoachRuleId.C05, "", "Pinch your left hand once to save", "Press B to save"),
            C(CoachRuleId.C06, "", "Tap the first point or pinch left to save", "Press B to save the shape"),
            C(CoachRuleId.C07, "", "Tap the button below to go on", "Push the button below to go on"),
            C(CoachRuleId.C08, "target", "Point at the {target} to seat the {part}"),
            C(CoachRuleId.C08, "", "Point at a surface to seat the {part}"),
            C(CoachRuleId.C09, "", "Pinch to place it", "Pull the trigger to place it"),
            C(CoachRuleId.C10, "", "Press Pay and keep holding 1 second", "Push Pay with the controller, hold 1 s"),
            C(CoachRuleId.C10, "d5", "Press Pay and keep holding 1 second", "Point at Pay, hold the trigger 1 s"),
            C(CoachRuleId.C11, "", "Tap Take it home to see it on your table", "Push Take it home to see it on your table"),
            C(CoachRuleId.C12, "", "More tools: turn your left palm up", "More tools: press the left menu button"),
            C(CoachRuleId.C13, "", "Don't spin now · it puts the part away"),
            C(CoachRuleId.C14, "", "Press the menu button again to close it"),
        };

        static Dictionary<(RuleId, string), CopyRow> s_Status;
        static Dictionary<(CoachRuleId, string), CopyRow> s_Coach;

        static void EnsureIndex()
        {
            if (s_Status != null) return;
            var st = new Dictionary<(RuleId, string), CopyRow>();
            foreach (var r in StatusRows) st[(r.Rule, r.Variant)] = r;
            var co = new Dictionary<(CoachRuleId, string), CopyRow>();
            foreach (var r in CoachRows) co[(r.Coach, r.Variant)] = r;
            s_Coach = co;
            s_Status = st;
        }

        /// The status template for a rule + variant (falls back to the rule's default variant; "" if none).
        public static string Status(RuleId rule, string variant, bool controllers)
        {
            EnsureIndex();
            if (s_Status.TryGetValue((rule, variant ?? ""), out var row) || s_Status.TryGetValue((rule, ""), out row)) return row.For(controllers);
            return "";
        }

        public static string Coach(CoachRuleId id, string variant, bool controllers)
        {
            EnsureIndex();
            if (s_Coach.TryGetValue((id, variant ?? ""), out var row) || s_Coach.TryGetValue((id, ""), out row)) return row.For(controllers);
            return "";
        }

        // ---------------- tape target → query → noun (§3.3) ----------------

        /// The Find-parts query for what the tape is on. Equal to the demo chips (demo.md §7) so the backend cache hits.
        /// Appliance / Unknown: null (R43: Find parts shows the chips instead).
        public static string QueryFor(TapeTarget t) => t switch
        {
            TapeTarget.Door => "cabinet hinge",
            TapeTarget.Drawer => "drawer slide",
            TapeTarget.Panel => "shelf bracket",
            TapeTarget.Run => "gutter hanger",
            _ => null,
        };

        static readonly (string query, string noun)[] s_QueryNouns =
        {
            ("cabinet hinge", "hinge"), ("drawer slide", "slide"), ("shelf bracket", "bracket"), ("gutter hanger", "hanger"),
            ("window ac", "AC unit"), ("cabinet knob", "knob"),
        };

        static readonly string[] s_Nouns = { "hinge", "knob", "pull", "handle", "hanger", "bracket", "slide", "shelf", "screw" };

        /// "cabinet hinge" → hinge / hinges; a query naming a known noun → that noun; else part / parts. Same table as
        /// Copy.Noun (W0.9) for a query without a part spec.
        public static string Noun(string query, bool plural)
        {
            string noun = "part";
            if (!string.IsNullOrWhiteSpace(query))
            {
                var q = query.Trim().ToLowerInvariant();
                bool found = false;
                foreach (var (k, n) in s_QueryNouns) if (q == k) { noun = n; found = true; break; }
                if (!found) foreach (var n in s_Nouns) if (q.Contains(n)) { noun = n; break; }
            }
            return plural ? Plural(noun) : noun;
        }

        public static string Plural(string noun) =>
            string.IsNullOrEmpty(noun) ? "parts" : noun == "shelf" ? "shelves" : noun.EndsWith("s", StringComparison.Ordinal) ? noun : noun + "s";

        /// {target}: door / drawer / cabinet (a structure "panel", as W0.9 names it) / appliance / run; "space" when unknown.
        public static string TargetNoun(TapeTarget t) => t switch
        {
            TapeTarget.Door => "door",
            TapeTarget.Drawer => "drawer",
            TapeTarget.Panel => "cabinet",
            TapeTarget.Appliance => "appliance",
            TapeTarget.Run => "run",
            _ => "space",
        };

        public static string Place(SceneKind scene) => scene == SceneKind.Kitchen ? "the kitchen" : "the site";
        public static string StartPlace(string site) =>
            site != null && site.IndexOf("kitchen", StringComparison.OrdinalIgnoreCase) >= 0 ? "the kitchen" : "the site";
        public static string Entry(SceneKind scene) => scene == SceneKind.Kitchen ? "a door" : scene == SceneKind.Facade ? "the gutter" : "something";
        public static string Point(SceneKind scene) => scene == SceneKind.Kitchen ? "corner" : "end";
        public static string Corner(SceneKind scene) => scene == SceneKind.Kitchen ? "a door corner" : scene == SceneKind.Facade ? "the gutter's end" : "a corner";
        public static string Flat(SceneKind scene) => scene == SceneKind.Kitchen ? "the counter" : "a surface";

        /// A seller name for a status: scrubbed, at most 14 characters ("…" when cut).
        public static string Seller(string name)
        {
            var s = NextStep.Clean(name ?? "").Trim();
            return s.Length <= SellerChars ? s : s.Substring(0, SellerChars - 1).TrimEnd() + "…";
        }

        // ---------------- placeholders ----------------

        /// Fill a template from the snapshot. `len` / `area` override the values {len} / {area} read (default: the latest
        /// tape / area reading). Only allocates when the template has a placeholder.
        public static string Fill(string template, in AppSnapshot s, double? len = null, double? area = null)
        {
            if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0) return template ?? "";
            var sb = new StringBuilder(template.Length + 24);
            int i = 0;
            while (i < template.Length)
            {
                char ch = template[i];
                int close = ch == '{' ? template.IndexOf('}', i + 1) : -1;
                if (close < 0) { sb.Append(ch); i++; continue; }
                string key = template.Substring(i + 1, close - i - 1);
                sb.Append(Resolve(key, s, len, area));
                i = close + 1;
            }
            return sb.ToString();
        }

        static string Resolve(string key, in AppSnapshot s, double? len, double? area)
        {
            switch (key)
            {
                case "len": return NextStep.Len(len ?? s.LastTapeM);
                case "area": return NextStep.Area(area ?? s.LastAreaM2);
                case "angle": return Units.FormatAngle(s.LastLevelDeg);
                case "total": return NextStep.Money(s.CheckoutTotal);
                case "price": return NextStep.Money(s.BestPrice);
                case "part": return Noun(s.LastQuery, false);
                case "parts": return Noun(s.LastQuery, true);
                case "tparts": return Noun(QueryFor(s.LastTapeTarget), true);
                case "target": return TargetNoun(s.LastTapeTarget);
                case "place": return Place(s.Scene);
                case "start": return StartPlace(s.StartSite);
                case "entry": return Entry(s.Scene);
                case "point": return Point(s.Scene);
                case "corner": return Corner(s.Scene);
                case "flat": return Flat(s.Scene);
                case "seller": return Seller(s.BestSeller);
                case "n": return s.NotebookCount.ToString(CultureInfo.InvariantCulture);
                default: return "{" + key + "}";
            }
        }
    }
}
