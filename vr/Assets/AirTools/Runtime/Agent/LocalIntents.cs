using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AirTools.Core;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.UI;

namespace AirTools.Agent
{
    public enum LocalIntentKind { None, Remove, PutBack, MeasureGap, FindFitting, PlaceIt, Next, Previous, ShowOption, ScaleGap, Undo }

    /// One phrase of the replace flow, recognised on the headset.
    public struct LocalIntent
    {
        public LocalIntentKind Kind;
        /// Remove / PutBack / FindFitting / PlaceIt: the thing named ("dishwasher"); null for "it" / "one".
        public string Thing;
        /// ShowOption: the candidate, 0-based.
        public int Index;
        /// FindFitting: the words asked for a fit ("that fits", "for the gap").
        public bool Fits;
        /// ScaleGap: the gap's real size on Axis ("w" / "h" / "d"), metres.
        public double Metres;
        public string Axis;

        public override string ToString() => Kind switch
        {
            LocalIntentKind.ShowOption => $"ShowOption {Index + 1}",
            LocalIntentKind.ScaleGap => $"ScaleGap {Axis}={Metres.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)} m",
            _ => Thing != null ? $"{Kind} \"{Thing}\"" : Kind.ToString(),
        };
    }

    /// What the app knows when a reply comes back (LocalIntents.Capture); pure data so the arbitration is testable.
    public struct LocalState
    {
        /// A removed component's gap is open (a cavity to measure / fill).
        public bool GapOpen;
        /// The component a Remove names is removable here.
        public Func<string, bool> CanRemove;
        /// Find parts has candidates.
        public bool HasCandidates;
        /// A model stands in the gap (ModelCycler / the placement editor).
        public bool Cycling;
        /// catalog: the scene has the Catalog — a plain "find a dishwasher" (no gap, no fit asked) opens it with that
        /// search when the server didn't search: no tape or gap is needed to look for something.
        public bool Catalog;
    }

    /// e2e: the replace flow's phrases, done on the headset when the server doesn't do them ("remove / take out the
    /// dishwasher", "put it back", "measure it / the gap", "find a dishwasher that fits", "put it in there", "next /
    /// previous one", "show me option 2"). Narrow and deterministic: the WHOLE utterance must be one of these (or a few of
    /// them joined by "and" / "then"), with optional fillers ("okay", "please", "grok"…).
    ///
    /// AgentClient hooks it after the reply (voice: the transcript; typed: the text):
    /// - The server did the intent itself (its own action is in the reply: remove_component, restore_component,
    ///   measure_cavity, search_started, place_part, cycle_model): nothing more happens here.
    /// - The reply had no action, or only ones that contradict the flow while it is live (a survey of "the appliances in
    ///   view" for "measure the gap", a new parts search or "select candidate" for "put it in there" / "next one"): those
    ///   are dropped, the same AppCommands run here, and the heads-up line says it was done on the headset.
    /// - Anything else in the reply (a coach step for "next", a reimagine undo…): the server's reply stands.
    /// A phrase only counts when the app's state makes it unambiguous (a gap open for "measure it", candidates for "put it
    /// in there", a model in the gap for "next one").
    public static class LocalIntents
    {
        const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        const string Lead = @"^\W*(?:(?:ok(?:ay)?|hey|so|now|and|then|alright|all\s+right|please|grok|quartermaster|can\s+you|could\s+you|would\s+you|will\s+you|let'?s|go\s+ahead\s+and|i\s+want\s+to|i'?d\s+like\s+to|we\s+need\s+to|i\s+need\s+you\s+to)\W+)*";
        const string Tail = @"(?:\W+(?:please|for\s+me|now|then|instead|again|thanks|thank\s+you|grok|quartermaster))*\W*$";
        const string Det = @"(?:(?:the|that|this|my|our|your|a|an|some|new|old|existing|current)\s+)*";
        /// 1–5 words, never "and" / "then" (those split an utterance into phrases).
        const string ThingRe = @"(?<thing>(?!(?:and|then)\b)[a-z][a-z0-9'\-]*(?:\s+(?!(?:and|then)\b)[a-z0-9'\-]+){0,4}?)";
        const string GapWord = @"(?:gap|hole|opening|cavity|space|spot|slot|void|empty\s+space|niche)";

        static readonly Regex s_PutBack = new Regex(Lead +
            @"(?:(?:put|bring|set|move)\s+(?:it|that|this|them|(?:the\s+)?original(?:\s+one)?|" + Det + ThingRe + @")\s+back(?:\s+in(?:\s+(?:there|place|its\s+place))?)?" +
            @"|(?:restore|reinstall)\s+(?:it|that|this|(?:the\s+)?original(?:\s+one)?|" + Det + ThingRe + @")" +
            @"|undo\s+the\s+removal)" + Tail, Opt);

        static readonly Regex s_Remove = new Regex(Lead +
            @"(?:(?:remove|take\s+out|pull\s+out|get\s+rid\s+of|take\s+away|rip\s+out|yank\s+out|uninstall)\s+" + Det + ThingRe + @"(?:\s+out)?" +
            @"|(?:take|pull|rip|yank)\s+" + Det + ThingRe + @"\s+out)" + Tail, Opt);

        static readonly Regex s_Measure = new Regex(Lead +
            @"(?:(?:measure|size\s+up|tape|get\s+the\s+(?:size|dimensions)\s+of|check\s+the\s+size\s+of|how\s+big\s+is|what(?:'s|\s+is|\s+are)\s+the\s+(?:size|dimensions?)\s+of)\s+" +
            @"(?:the\s+(?:dimensions|size)\s+of\s+)?" +
            @"(?:it|that|this|there|the\s+" + GapWord + @"|that\s+" + GapWord + @"|the\s+[a-z]+(?:\s+[a-z]+)?\s+" + GapWord + @"|where\s+(?:it|the\s+[a-z]+(?:\s+[a-z]+)?)\s+(?:was|used\s+to\s+be))" +
            @"|what(?:'s|\s+is|\s+are)\s+the\s+(?:size|dimensions)(?:\s+of\s+the\s+" + GapWord + @")?|how\s+big\s+is\s+(?:it|the\s+" + GapWord + @"))" + Tail, Opt);

        static readonly Regex s_Option = new Regex(Lead +
            @"(?:(?:show\s+me|try|go\s+to|switch\s+to|give\s+me|put\s+in|use|pick|let'?s\s+see|let\s+me\s+see|i'?ll\s+take)\s+)?(?:the\s+)?" +
            @"(?:(?:option|number|model|choice|candidate|pick)\s+(?:#\s*)?(?<n>\d{1,2}|one|two|three|four|five)|(?<ord>first|second|third|fourth|fifth)(?:\s+(?:one|option|model|choice|pick))?)" + Tail, Opt);

        static readonly Regex s_Previous = new Regex(Lead +
            @"(?:(?:show\s+me|try|go\s+back\s+to|back\s+to|switch\s+to|give\s+me)\s+)?(?:the\s+)?(?:previous|prior|one\s+before)(?:\s+(?:one|model|option|choice|pick|candidate))?" + Tail, Opt);

        static readonly Regex s_Next = new Regex(Lead +
            @"(?:(?:(?:show\s+me|try|let'?s\s+see|let\s+me\s+see|go\s+to|switch\s+to|give\s+me|put\s+in|how\s+about)\s+)?(?:the\s+|a\s+)?(?:next|another|different|other)(?:\s+(?:one|model|option|choice|pick|candidate|brand))?" +
            @"|(?:swap|switch|change)\s+(?:it|models?|them|it\s+out)(?:\s+(?:out|for\s+(?:another|the\s+next)(?:\s+one)?))?)" + Tail, Opt);

        static readonly Regex s_Place = new Regex(Lead +
            @"(?:put|place|install|drop|stick|slot|slide|fit|try|set|pop)\s+" +
            @"(?:it|that|this|that\s+one|this\s+one|one|them|the\s+(?:best|top|first|recommended)(?:\s+(?:one|fit|match|pick))?|" + Det + ThingRe + @")" +
            @"(?:\s+(?:in|into|there|in\s+there|in\s+here|here|in\s+(?:the\s+)?" + GapWord + @"|into\s+the\s+" + GapWord + @"|in\s+place|in\s+its\s+place))?" + Tail, Opt);

        static readonly Regex s_Find = new Regex(Lead +
            @"(?:find|get|show|look\s+for|search\s+for|shop\s+for|search|browse|pick\s+out)(?:\s+me)?\s+" + Det + ThingRe +
            @"(?<fits>\s+(?:that|which|to)\s+(?:will\s+|would\s+|can\s+|could\s+)?fits?(?:\s+(?:in\s+)?(?:there|here|it|the\s+" + GapWord + @"))?|\s+for\s+(?:the\s+" + GapWord + @"|it|there))?" + Tail, Opt);

        /// "The opening is 34 and a half inches tall", "the gap's width is 24 inches", "it's 610 mm wide": the gap's real
        /// size on one axis (set the scale from it). Digits only (with "and a half" / ½).
        const string Amount = @"(?<n>\d+(?:\.\d+)?)(?<half>\s*(?:and\s+(?:a\s+)?(?:half|quarter)|and\s+three\s+quarters|[½¼¾⅛⅜⅝⅞]|\d{1,2}/\d{1,2}))?\s*(?<u>inches|inch|in\b|""|″|centimet(?:re|er)s?|cm|millimet(?:re|er)s?|mm)";
        static readonly Regex s_Scale = new Regex(Lead +
            @"(?:(?:set\s+the\s+scale|scale\s+it)\W+)?" +
            @"(?:(?:the\s+)?(?:gap|opening|hole|space|cavity|cutout)(?:'s)?\s+(?<axis2>width|height|depth)\s+is\s+" + Amount +
            @"|(?:(?:the\s+)?(?:gap|opening|hole|space|cavity|cutout)\s+(?:is|should\s+be|was)|it(?:'s|\s+is|\s+should\s+be))\s+" + Amount + @"\s+(?<axis>wide|tall|high|deep))" + Tail, Opt);

        /// "and" / "then" / commas / sentences join phrases ("34 and a half inches" is one amount; "Remove the range. Find a
        /// range that fits." is two).
        static readonly Regex s_Split = new Regex(@"\s*(?:,\s*(?:and\s+)?(?:then\s+)?|[.;!?]\s+(?:and\s+)?(?:then\s+)?|\s+and\s+then\s+|\s+and\s+(?!(?:a\s+)?(?:half|quarter)\b|three\s+quarters\b)|\s+then\s+)\s*", Opt);

        /// "undo", "undo that", "take that back" (autonomy: the backend sends undo_edit for it while a gap is open).
        static readonly Regex s_Undo = new Regex(Lead + @"(?:undo|undo\s+(?:that|it|the\s+last\s+(?:one|step|change))|take\s+that\s+back)" + Tail, Opt);

        static readonly HashSet<string> s_Pronouns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "it", "that", "this", "them", "these", "those", "one", "ones", "everything", "all", "something", "replacement", "replacements", "a replacement", "another", "another one" };

        /// The intents of an utterance, in order: one phrase, or several joined by "and" / "then" / commas (each must be
        /// a phrase; otherwise none). Pure.
        public static List<LocalIntent> MatchAll(string text)
        {
            var list = new List<LocalIntent>();
            text = Normalize(text);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return list;
            var one = Match(text);
            if (one.Kind != LocalIntentKind.None) { list.Add(one); return list; }
            var pieces = new List<string>();
            foreach (var piece in s_Split.Split(text.Trim().TrimEnd('.', '!', '?', ';', ',', ' ')))
                if (!string.IsNullOrWhiteSpace(piece)) pieces.Add(piece);
            if (pieces.Count < 2 || pieces.Count > 4) return list;
            foreach (var p in pieces)
            {
                var m = Match(p);
                if (m.Kind == LocalIntentKind.None) { list.Clear(); return list; }
                list.Add(m);
            }
            return list;
        }

        /// One phrase (the whole utterance). Pure.
        public static LocalIntent Match(string text)
        {
            var none = new LocalIntent { Kind = LocalIntentKind.None, Index = -1 };
            if (string.IsNullOrWhiteSpace(text)) return none;
            string t = Regex.Replace(Normalize(text).Trim(), @"\s+", " ");
            Match m;
            if ((m = s_Scale.Match(t)).Success && ScaleOf(m, out string axis, out double metres))
                return new LocalIntent { Kind = LocalIntentKind.ScaleGap, Axis = axis, Metres = metres, Index = -1 };
            if ((m = s_PutBack.Match(t)).Success) return new LocalIntent { Kind = LocalIntentKind.PutBack, Thing = Thing(m), Index = -1 };
            if ((m = s_Measure.Match(t)).Success) return new LocalIntent { Kind = LocalIntentKind.MeasureGap, Index = -1 };
            if (s_Undo.IsMatch(t)) return new LocalIntent { Kind = LocalIntentKind.Undo, Index = -1 };
            if ((m = s_Remove.Match(t)).Success)
            {
                var thing = Thing(m);
                return thing == null ? none : new LocalIntent { Kind = LocalIntentKind.Remove, Thing = thing, Index = -1 };
            }
            if ((m = s_Option.Match(t)).Success) return new LocalIntent { Kind = LocalIntentKind.ShowOption, Index = OptionIndex(m) };
            if ((m = s_Previous.Match(t)).Success) return new LocalIntent { Kind = LocalIntentKind.Previous, Index = -1 };
            if ((m = s_Next.Match(t)).Success) return new LocalIntent { Kind = LocalIntentKind.Next, Index = -1 };
            if ((m = s_Place.Match(t)).Success)
            {
                var thing = Thing(m);
                if (thing != null && Regex.IsMatch(thing, @"\b(?:note|pin|tape|point|marker|label)s?\b|^(?:it|that|this|them|these|those)\b|\b(?:every|each)\b|\d", Opt)) return none;
                return new LocalIntent { Kind = LocalIntentKind.PlaceIt, Thing = thing, Index = -1 };
            }
            if ((m = s_Find.Match(t)).Success)
            {
                var thing = Thing(m);
                bool fits = m.Groups["fits"].Success && m.Groups["fits"].Length > 0;
                if (thing != null && Regex.IsMatch(thing, @"^(?:next|previous|option|number|other|another|it|that|this|them|me|us)\b|^" + GapWord + "$", Opt)) return none;
                return new LocalIntent { Kind = LocalIntentKind.FindFitting, Thing = thing, Fits = fits, Index = -1 };
            }
            return none;
        }

        static string Thing(Match m)
        {
            var g = m.Groups["thing"];
            if (!g.Success) return null;
            string s = g.Value.Trim().ToLowerInvariant();
            s = Regex.Replace(s, @"^(?:the|a|an|my|our|that|this|some|new|old|existing|current)\s+", "");
            return s.Length == 0 || s_Pronouns.Contains(s) ? null : s;
        }

        static bool ScaleOf(Match m, out string axis, out double metres)
        {
            string a = (m.Groups["axis"].Success ? m.Groups["axis"].Value : m.Groups["axis2"].Value).ToLowerInvariant();
            axis = a.StartsWith("wid") ? "w" : a.StartsWith("dep") || a == "deep" ? "d" : "h";
            metres = 0;
            if (!double.TryParse(m.Groups["n"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n)) return false;
            if (m.Groups["half"].Success && m.Groups["half"].Length > 0) n += Fraction(m.Groups["half"].Value);
            string u = m.Groups["u"].Value.ToLowerInvariant();
            double perUnit = u.StartsWith("c") ? 0.01 : u.StartsWith("mi") || u == "mm" ? 0.001 : 0.0254;
            metres = n * perUnit;
            return metres > 0.01 && metres < 5.0;
        }

        static int OptionIndex(Match m)
        {
            string n = m.Groups["n"].Success ? m.Groups["n"].Value.ToLowerInvariant() : m.Groups["ord"].Value.ToLowerInvariant();
            switch (n)
            {
                case "one": case "first": return 0;
                case "two": case "second": return 1;
                case "three": case "third": return 2;
                case "four": case "fourth": return 3;
                case "five": case "fifth": return 4;
            }
            return int.TryParse(n, out int k) && k >= 1 ? k - 1 : -1;
        }

        /// " and a half" / "½" / " 3/4" → 0.5 / 0.5 / 0.75 (an amount's fraction; 0.5 when unreadable).
        public static double Fraction(string text)
        {
            string t = (text ?? "").Trim().ToLowerInvariant();
            switch (t)
            {
                case "½": return 0.5;
                case "¼": return 0.25;
                case "¾": return 0.75;
                case "⅛": return 0.125;
                case "⅜": return 0.375;
                case "⅝": return 0.625;
                case "⅞": return 0.875;
            }
            int slash = t.IndexOf('/');
            if (slash > 0 && double.TryParse(t.Substring(0, slash), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double a)
                && double.TryParse(t.Substring(slash + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double b) && b > 0)
                return a / b;
            if (t.Contains("three quarters")) return 0.75;
            return t.Contains("quarter") ? 0.25 : 0.5;
        }

        static readonly string[] s_Units = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
        static readonly string[] s_Tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };
        const string NumWord = "twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen";
        static readonly Regex s_NumberWords = new Regex(
            @"\b(?<num>(?:" + NumWord + @")(?:[\s-]+(?:" + NumWord + @"))?(?:\s+hundred(?:\s+(?:and\s+)?(?:" + NumWord + @")(?:[\s-]+(?:" + NumWord + @"))?)?)?)" +
            @"(?<rest>(?:\s+and\s+(?:a\s+)?(?:half|quarter)|\s+and\s+three\s+quarters)?\s+(?:inch(?:es)?|centimet(?:re|er)s?|millimet(?:re|er)s?|cm|mm|feet|foot|dollars?|bucks)\b)", Opt);

        static int? WordsValue(string words)
        {
            int current = 0;
            foreach (var w in Regex.Split(words.ToLowerInvariant(), @"[\s-]+"))
            {
                if (w.Length == 0 || w == "and") continue;
                if (w == "hundred") { current = Math.Max(current, 1) * 100; continue; }
                int k = Array.IndexOf(s_Tens, w);
                if (k >= 2) { current += k * 10; continue; }
                k = Array.IndexOf(s_Units, w);
                if (k < 0) return null;
                current += k;
            }
            return current;
        }

        /// A transcript as the phrases expect it (mirrors the backend's replace.normalize): Whisper's "34 1⁄2" (U+2044
        /// FRACTION SLASH) is "34 1/2", "34½" is "34 ½", number words before a unit are digits ("thirty-four and a half
        /// inches" → "34 and a half inches"; "next one" / "option two" keep their words), curly quotes straight. Pure.
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string t = text.Replace('\u2044', '/').Replace('\u2215', '/').Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u201c', '"').Replace('\u201d', '"');
            t = Regex.Replace(t, @"(\d)\s*([½¼¾⅛⅜⅝⅞])", "$1 $2");
            return s_NumberWords.Replace(t, m => WordsValue(m.Groups["num"].Value) is int v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) + m.Groups["rest"].Value : m.Value);
        }

        /// A plural noun as the label / query the search and the scene parts use ("dishwashers" → "dishwasher").
        public static string Singular(string noun)
        {
            if (string.IsNullOrEmpty(noun)) return noun;
            if (noun.EndsWith("ches") || noun.EndsWith("shes") || noun.EndsWith("sses") || noun.EndsWith("xes")) return noun.Substring(0, noun.Length - 2);
            if (noun.EndsWith("ies") && noun.Length > 4) return noun.Substring(0, noun.Length - 3) + "y";
            if (noun.EndsWith("s") && !noun.EndsWith("ss") && noun.Length > 3) return noun.Substring(0, noun.Length - 1);
            return noun;
        }

        // ---------------- arbitration (pure) ----------------

        /// The server actions that already do each intent.
        public static bool Satisfies(LocalIntentKind k, string action)
        {
            switch (k)
            {
                case LocalIntentKind.Remove: return action == "remove_component";
                case LocalIntentKind.PutBack: return action == "restore_component";
                case LocalIntentKind.MeasureGap: return action == "measure_cavity";
                case LocalIntentKind.ScaleGap: return action == "scale_gap";
                case LocalIntentKind.Undo: return action == "undo_edit";   // autonomy
                case LocalIntentKind.FindFitting: return action == "search_started" || action == "show_catalog";   // catalog
                case LocalIntentKind.PlaceIt: return action == "place_part";
                case LocalIntentKind.Next: case LocalIntentKind.Previous: case LocalIntentKind.ShowOption:
                    return action == "cycle_model" || action == "place_part";
                default: return false;
            }
        }

        /// Server actions that contradict an intent while the flow is live (dropped): a survey of what's in view for
        /// "measure the gap" / "find one that fits"; a new search or "take candidate i into the hand" for "put it in
        /// there" / "next one" / "option 2" (they'd replace the candidates being switched through).
        public static bool Contradicts(LocalIntentKind k, string action)
        {
            switch (k)
            {
                case LocalIntentKind.MeasureGap: case LocalIntentKind.FindFitting: case LocalIntentKind.ScaleGap:
                    return action == "survey" || action == "show_tape_survey";
                case LocalIntentKind.PlaceIt: case LocalIntentKind.Next: case LocalIntentKind.Previous: case LocalIntentKind.ShowOption:
                    return action == "search_started" || action == "select_candidate" || action == "survey" || action == "show_tape_survey";
                default: return false;
            }
        }

        /// Actions that ride along with any reply and never stand for the command (a slow job's leftovers, a note).
        public static bool Passive(string action) =>
            action == "add_note" || action == "show_video" || action == "show_rules" || action == "show_limits"
            || action == "job_started" || action == "job_step" || action == "job_done";

        /// The reply started a server job (job_started): the job does the utterance's work, step by step.
        public static bool StartsJob(IList<AgentAction> actions)
        {
            if (actions == null) return false;
            foreach (var a in actions) if (a != null && a.name == "job_started") return true;
            return false;
        }

        /// Whether the app's state makes the intent unambiguous.
        public static bool Applies(LocalIntent i, LocalState s)
        {
            switch (i.Kind)
            {
                case LocalIntentKind.Remove: return s.CanRemove != null && i.Thing != null && s.CanRemove(i.Thing);
                case LocalIntentKind.PutBack: return s.GapOpen;
                case LocalIntentKind.MeasureGap: return s.GapOpen;
                case LocalIntentKind.ScaleGap: return s.GapOpen;
                case LocalIntentKind.Undo: return s.GapOpen || s.Cycling;   // autonomy: only while the replace flow is live
                case LocalIntentKind.FindFitting: return s.GapOpen || (i.Fits && i.Thing != null) || (s.Catalog && i.Thing != null);   // catalog
                case LocalIntentKind.PlaceIt: return s.GapOpen && s.HasCandidates;
                case LocalIntentKind.Next: case LocalIntentKind.Previous: case LocalIntentKind.ShowOption:
                    return s.Cycling || (s.GapOpen && s.HasCandidates);
                default: return false;
            }
        }

        /// The decision for one reply.
        public struct Plan
        {
            /// The reply's actions to run (the contradicting ones dropped).
            public List<AgentAction> Actions;
            /// Run these here after the actions (empty: nothing local).
            public List<LocalIntent> Local;
            /// Names dropped (logged).
            public List<string> Dropped;
            public bool RunsLocal => Local != null && Local.Count > 0;
        }

        /// Pure: intents (MatchAll), the state, the reply's actions → what runs. The first intent's state decides whether
        /// the flow is live (a later one — "measure it" after "take out the dishwasher and" — becomes true as it runs).
        public static Plan Arbitrate(IList<LocalIntent> intents, LocalState state, IList<AgentAction> actions)
        {
            var plan = new Plan { Actions = new List<AgentAction>(), Local = new List<LocalIntent>(), Dropped = new List<string>() };
            if (actions != null) plan.Actions.AddRange(actions);
            if (intents == null || intents.Count == 0 || !Applies(intents[0], state)) return plan;
            // autonomy: a server job took the utterance ("take out the dishwasher and put in a new one" → the replace job's
            // job_started; its remove_component, place_part… arrive by poll): nothing runs here, or it would run twice.
            if (StartsJob(actions)) return plan;
            var missing = new List<LocalIntent>();
            foreach (var i in intents)
            {
                bool done = false;
                foreach (var a in plan.Actions) if (a != null && Satisfies(i.Kind, a.name)) { done = true; break; }
                if (!done) missing.Add(i);
            }
            if (missing.Count == 0) return plan;
            var kept = new List<AgentAction>();
            bool blocked = false;
            foreach (var a in plan.Actions)
            {
                if (a == null) continue;
                bool contradicts = false;
                foreach (var i in missing) if (Contradicts(i.Kind, a.name)) { contradicts = true; break; }
                if (contradicts) { plan.Dropped.Add(a.name); continue; }
                kept.Add(a);
                bool satisfies = false;
                foreach (var i in intents) if (Satisfies(i.Kind, a.name)) { satisfies = true; break; }
                if (!satisfies && !Passive(a.name)) blocked = true;
            }
            if (blocked)
            {
                // Something else is going on (a coach step for "next"): the server's reply stands as it was.
                plan.Dropped.Clear();
                return plan;
            }
            plan.Actions = kept;
            plan.Local = missing;
            return plan;
        }

        // ---------------- runtime ----------------

        /// The last intents run here (the harness reads it) and when.
        public static string LastRun { get; private set; } = "";
        public static int Runs { get; private set; }
        public static string LastLine { get; private set; }

        /// The live state.
        public static LocalState Capture()
        {
            // Putting a model in the gap and switching it happen in the world (in Model view on the table, "next model"
            // is the model view's switcher).
            bool world = AppState.Mode == AppMode.World;
            var s = new LocalState
            {
                GapOpen = Gaps.Open,
                HasCandidates = world && Services.TryGet<PartsBrowser>(out var b) && b.Candidates.Count > 0,
                Cycling = world && Gaps.ModelInOpenGap() != null,
                Catalog = Services.TryGet<CatalogWindow>(out _),   // catalog
            };
            s.CanRemove = thing =>
            {
                if (!Services.TryGet<SceneParts>(out var parts) || !parts.HasParts) return false;
                var comp = parts.Find(thing) ?? parts.Find(Singular(thing));
                return comp != null && comp.removable;
            };
            return s;
        }

        /// Decide for a reply (said: the transcript / typed text; actions: the reply's).
        public static Plan For(string said, IList<AgentAction> actions)
        {
            var intents = MatchAll(said);
            if (intents.Count == 0) return new Plan { Actions = actions != null ? new List<AgentAction>(actions) : new List<AgentAction>(), Local = new List<LocalIntent>(), Dropped = new List<string>() };
            var plan = Arbitrate(intents, Capture(), actions);
            if (plan.RunsLocal || plan.Dropped.Count > 0)
                Log.Info($"Local intents: \"{said}\" → [{string.Join(", ", intents)}]; server sent [{Names(actions)}]" +
                         (plan.Dropped.Count > 0 ? $", dropped [{string.Join(", ", plan.Dropped)}]" : "") +
                         (plan.RunsLocal ? $", doing [{string.Join(", ", plan.Local)}] on the headset" : ""));
            return plan;
        }

        static string Names(IList<AgentAction> actions)
        {
            if (actions == null) return "";
            var n = new List<string>();
            foreach (var a in actions) n.Add(a?.name ?? "?");
            return string.Join(", ", n);
        }

        /// Run the intents here, in order; the heads-up line says what the headset did. Returns how many worked.
        public static int Run(IList<LocalIntent> intents)
        {
            int ok = 0;
            var lines = new List<string>();
            foreach (var i in intents)
            {
                string line = Run(i);
                if (line != null) { ok++; lines.Add(line); }
            }
            Runs++;
            LastRun = $"{string.Join(", ", intents)} → {ok}/{intents.Count}";
            if (lines.Count > 0)
            {
                LastLine = Copy.OnHeadset(string.Join(" · ", lines));
                UiToast.Reply(Copy.Clip(LastLine, 90));
            }
            Log.Info($"Local intents: {LastRun}");
            return ok;
        }

        /// One intent; the words for the heads-up line (what the command's own toast would have said: the reply card
        /// covers it), or null when it couldn't be done (the command toasted why).
        static string Run(LocalIntent i)
        {
            switch (i.Kind)
            {
                case LocalIntentKind.Remove:
                {
                    var parts = Services.Get<SceneParts>();
                    var comp = parts?.Find(i.Thing) ?? parts?.Find(Singular(i.Thing));
                    if (comp == null) { AppCommands.RemoveComponent(i.Thing); return null; }
                    if (!AppCommands.RemoveComponent(comp.id)) return null;
                    if (!Gaps.TryGet(out var g, comp.id)) return Copy.PartOut(comp.DisplayName, null, null, null);
                    var size = g.SizeM;
                    return Copy.PartOut(comp.DisplayName, size.x, size.y, size.z);
                }
                case LocalIntentKind.PutBack:
                {
                    var parts = Services.Get<SceneParts>();
                    var comp = i.Thing != null ? parts?.Find(i.Thing) ?? parts?.Find(Singular(i.Thing)) : parts?.LastRemoved;
                    if (comp == null) return null;
                    return AppCommands.RestoreComponent(comp.id) ? Copy.PartBack(comp.DisplayName) : null;
                }
                case LocalIntentKind.MeasureGap:
                {
                    if (!AppCommands.MeasureCavity(null) || !Gaps.TryGet(out var g)) return null;
                    var size = Gaps.FitSizeM(g);
                    return Copy.GapMeasured(g.Noun, size.x, size.y, size.z);
                }
                case LocalIntentKind.ScaleGap:
                {
                    var root = Services.Get<SceneRoot>();
                    float before = root != null ? root.Calibration : 1f;
                    if (!AppCommands.ScaleFromGap(i.Axis, i.Metres)) return null;
                    float factor = root != null && before > 0f ? root.Calibration / before : 1f;
                    return Copy.GapScale(CavityTapes.AxisWord(i.Axis), i.Metres, factor);
                }
                case LocalIntentKind.FindFitting:
                {
                    string q = i.Thing != null ? Singular(i.Thing) : null;
                    // catalog: nothing to fit against and no fit asked: the Catalog, with the search typed in.
                    if (!i.Fits && !Gaps.Open && q != null && Services.TryGet<CatalogWindow>(out _))
                        return AppCommands.ShowCatalog(null, q) ? $"showing {Copy.Plural(q)} in the catalog" : null;
                    if (!AppCommands.FindForGap(q)) return null;
                    if (!Gaps.TryGet(out var g)) return $"finding {Copy.Plural(q ?? "part")}";
                    var size = Gaps.FitSizeM(g);
                    return $"finding {Copy.Plural(q ?? g.Noun)} for the {Units.FormatTriple(size.x, size.y, size.z, UiSettings.UnitSystem)} gap";
                }
                case LocalIntentKind.PlaceIt:
                    return AppCommands.PlaceBestInGap() ? "putting the best fit in the gap" : null;
                case LocalIntentKind.Next:
                    return AppCommands.NextPlacedModel(1) ? "next model" : null;
                case LocalIntentKind.Previous:
                    return AppCommands.NextPlacedModel(-1) ? "previous model" : null;
                case LocalIntentKind.ShowOption:
                    return AppCommands.ShowPlacedModel(i.Index) ? $"option {i.Index + 1}" : null;
                case LocalIntentKind.Undo:
                    return AppCommands.Undo() ? "undone" : null;   // autonomy
                default:
                    return null;
            }
        }
    }
}
