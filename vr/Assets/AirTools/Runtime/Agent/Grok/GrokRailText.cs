using System;
using System.Globalization;
using AirTools.UI;

namespace AirTools.Agent.Grok
{
    /// Colours (ColorRole only, never hard-coded) and glyphs for the rails. State is never colour alone: every tone
    /// has a glyph or a word.
    public static class GrokRailTones
    {
        /// Neutral information (blue): ColorRole.Info after UX D3, the accent before it. Resolved by name so this
        /// compiles on both sides of the re-theme without touching the obsolete Accent.
        public static readonly ColorRole Info = Resolve("Info", "Accent");

        static ColorRole Resolve(params string[] names)
        {
            foreach (var n in names)
                if (Enum.TryParse(n, out ColorRole r)) return r;
            return ColorRole.TextSecondary;
        }

        public static ColorRole Dot(DotStatus s) => s switch
        {
            DotStatus.Done => ColorRole.Success,
            DotStatus.Said => ColorRole.Success,
            DotStatus.Skipped => ColorRole.TextDisabled,
            DotStatus.Stopped => ColorRole.Danger,
            DotStatus.Active => Info,
            _ => ColorRole.Separator,
        };

        public static string Glyph(DotStatus s) => s switch
        {
            DotStatus.Done => "✓",
            DotStatus.Said => "✓",
            DotStatus.Skipped => "–",
            DotStatus.Stopped => "✗",
            _ => "",
        };

        public static ColorRole Chip(ChipState s) => s switch
        {
            ChipState.Ok => ColorRole.Success,
            ChipState.NotYet => ColorRole.Warning,
            ChipState.CantSee => Info,
            _ => ColorRole.TextSecondary,
        };

        public static string ChipGlyph(ChipState s) => s switch
        {
            ChipState.Ok => "✓",
            ChipState.NotYet => "!",
            ChipState.CantSee => "?",
            _ => "•",
        };

        public static ColorRole Summary(string status) => status == "stopped" ? ColorRole.Danger : ColorRole.Success;

        /// The summary chip's tone for a run: red stopped, green when a step got done, grey when every step was skipped.
        public static ColorRole Summary(JobRunModel m) =>
            m == null ? ColorRole.TextSecondary : m.IsCancelled ? ColorRole.TextSecondary   // switchclean: quiet, the person's own switch
            : m.Status == "stopped" ? ColorRole.Danger
            : m.Dots.Exists(d => d.Status == DotStatus.Done) ? ColorRole.Success : ColorRole.TextDisabled;

        public static ColorRole Verdict(string verdict) => verdict switch
        {
            "passed" => ColorRole.Success,
            "not_yet" => ColorRole.Warning,
            "look" => Info,
            "stop" => ColorRole.Danger,
            _ => ColorRole.TextSecondary,
        };
    }

    /// The rails' words (W0.9 copy rules: sentence case, "·" as the only separator, "…" while in progress).
    public static class GrokRailText
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public const string JobTitle = "Do the whole job";

        /// A job step's short name under its dot (runjob.py STEPS).
        public static string StepName(string name) => name switch
        {
            "survey" => "Survey",
            "part" => "Part",
            "safety" => "Safety",
            "rules" => "Rules",
            "postcard" => "Preview",
            "packet" => "Packet",
            "checkout" => "Checkout",
            null => "",
            _ => name.Length > 0 ? char.ToUpperInvariant(name[0]) + name.Substring(1) : name,
        };

        public static string Price(double usd) => "$" + usd.ToString("0.00", C);

        /// "3 of 7" steps settled.
        public static string JobCount(JobRunModel m) => m == null ? "" : $"{m.Settled} of {m.Dots.Count}";

        /// The job_done chip: "✓ Done · $899.00 · $749.00 after rebates" / "✗ Stopped before checkout: recalled".
        public static string JobSummary(JobDoneArgs d, JobRunModel m = null)
        {
            if (d == null) return "";
            // autonomy: a replace run says what went in, or where it stopped ("✗ Stopped: none of the 3 fits …").
            if (!string.IsNullOrWhiteSpace(d.summary))
            {
                string words = Copy.Clip(Copy.Clean(d.summary), 64);
                return (d.status == "stopped" || d.status == JobRunModel.Cancelled ? "✗ " : "✓ ") + words;   // switchclean: + cancelled
            }
            if (d.status == "stopped")
                return "✗ Stopped before checkout: recalled" + (d.total_usd.HasValue ? $" · {Price(d.total_usd.Value)}" : "");
            if (m != null && m.Dots.Count > 0 && m.Dots.TrueForAll(x => x.Status == DotStatus.Skipped))
                return "– Nothing done · every step was skipped";
            var s = "✓ Done";
            if (d.total_usd.HasValue)
            {
                s += $" · {Price(d.total_usd.Value)}";
                if (d.after_rebates_usd.HasValue) s += $" · {Price(d.after_rebates_usd.Value)} after rebates";
            }
            else if (m != null && m.Dots.Exists(x => x.Name == "part" && x.Status == DotStatus.Skipped)) s += " · no part found";
            return s;
        }

        // ---------------- the job strip (declutter M2, DC1): "do the whole job" as the status line's progress ----------------

        /// A step's glyph on the strip: ✓ done (or said), – skipped, ✗ stopped, • working, · not reached yet.
        public static string StripGlyph(DotStatus s) => s switch
        {
            DotStatus.Done => "✓",
            DotStatus.Said => "✓",
            DotStatus.Skipped => "–",
            DotStatus.Stopped => "✗",
            DotStatus.Active => "•",
            _ => "·",
        };

        /// A glyph's colour on the heads-up line: the rail's dot tones, and secondary text for a step not reached yet (the
        /// rail's separator grey would vanish as text). Every glyph says its state by shape too, never colour alone.
        public static ColorRole StripTone(DotStatus s) => s == DotStatus.Pending ? ColorRole.TextSecondary : GrokRailTones.Dot(s);

        /// "✓ ✓ ✓ – • · ·": one glyph per step in order, each wrapped in its colour (`hex` gives a role's RRGGBB; without
        /// it, plain glyphs).
        public static string JobStrip(JobRunModel m, Func<ColorRole, string> hex = null)
        {
            if (m == null) return "";
            var sb = new System.Text.StringBuilder(m.Dots.Count * 18);
            for (int k = 0; k < m.Dots.Count; k++)
            {
                if (k > 0) sb.Append(' ');
                var st = m.Display(k);
                if (hex != null) sb.Append("<#").Append(hex(StripTone(st))).Append('>');
                sb.Append(StripGlyph(st));
                if (hex != null) sb.Append("</color>");
            }
            return sb.ToString();
        }

        /// "Do the whole job · 5 of 7"; autonomy: the run's own title ("Replace the dishwasher · 5 of 7").
        public static string JobLine(JobRunModel m) => m == null ? "" : $"{Title(m)} · {JobCount(m)}";

        /// The rail's heading: the run's title (a replace run sends one), else "Do the whole job".
        public static string Title(JobRunModel m) => m != null && !string.IsNullOrWhiteSpace(m.Title) ? Copy.Clip(Copy.Clean(m.Title), 40) : JobTitle;

        /// The status line's line 1 for a run: "Do the whole job · 5 of 7  ✓ ✓ ✓ – • · ·" while it runs, then the job_done
        /// summary ("✓ Done · $899.00 · $749.00 after rebates").
        public static string JobStatus(JobRunModel m, Func<ColorRole, string> hex = null) =>
            m == null ? "" : m.Finished ? JobSummary(m.Done, m) : $"{JobLine(m)}  {JobStrip(m, hex)}";

        /// Line 2: while it runs, the newest spoken result; after job_done, where things are (else that last line).
        /// ≤ 72 characters.
        public static string JobStatusDetail(JobRunModel m)
        {
            if (m == null) return "";
            string detail = m.Finished ? JobDetail(m.Done, m) : "";
            if (string.IsNullOrEmpty(detail)) detail = Copy.Clean(m.Caption);
            return Copy.Clip(detail, 72);
        }

        /// The words without TMP rich-text tags.
        public static string StripTags(string s) => string.IsNullOrEmpty(s) ? "" : System.Text.RegularExpressions.Regex.Replace(s, "<[^>]*>", "");

        /// Under the chip: where things are (the pay panel is open for the hold; the packet link) and the AI cost.
        public static string JobDetail(JobDoneArgs d, JobRunModel m = null)
        {
            if (d == null) return "";
            if (d.status == JobRunModel.Cancelled) return CancelledDetail;   // switchclean
            var parts = new System.Collections.Generic.List<string>();
            // autonomy: after a replace, what to say next (the other models are built: "next one" is instant).
            if (d.kind == "replace" && d.status == "done" && d.models_ready.HasValue && d.models_ready.Value > 1) parts.Add("Say next one for the others");
            bool payOpen = d.status == "done" && m != null && m.Dots.Exists(x => x.Name == "checkout" && x.Status == DotStatus.Done);
            if (payOpen) parts.Add("Hold Pay to buy");
            if (!string.IsNullOrEmpty(d.packet_url)) parts.Add("Packet ready");
            if (d.cost_usd.HasValue && d.cost_usd.Value > 0) parts.Add($"AI cost {Price(d.cost_usd.Value)}");
            return string.Join(" · ", parts);
        }

        // switchclean: a job the headset stopped because the person switched models mid-run.

        /// Line 2 under a cancelled run.
        public const string CancelledDetail = "The rest didn't run · ask again on this model";

        /// The strip's end state: "Cancelled · you switched to the Zabel gym" (JobSummary adds the ✗).
        public static string CancelledSummary(string site) => $"Cancelled · you switched to the {Copy.SiteName(site)}";

        /// What the job was about, as the toast names it: a replace run's part ("the dishwasher job", from its title
        /// "Replace the dishwasher"), else "the job".
        public static string JobNoun(JobRunModel m)
        {
            string t = m?.Title?.Trim();
            const string Lead = "Replace the ";
            if (m != null && m.IsReplace && !string.IsNullOrEmpty(t) && t.StartsWith(Lead, StringComparison.OrdinalIgnoreCase) && t.Length > Lead.Length)
                return $"the {Copy.Clip(Copy.Clean(t.Substring(Lead.Length)), 28)} job";
            return "the job";
        }

        /// The quiet toast: "Stopped the dishwasher job: you switched to the Zabel gym".
        public static string CancelledToast(JobRunModel m, string site) => $"Stopped {JobNoun(m)}: you switched to the {Copy.SiteName(site)}";

        public static string CoachTitle(CoachSessionModel m) =>
            m == null ? "" : string.IsNullOrEmpty(m.Label) ? "Install coach" : $"Install · {m.Label}";

        /// "Step 3 of 8 · drill" / "Step 3 of 8 · paused" / "Done".
        public static string CoachStepLine(CoachSessionModel m)
        {
            if (m == null) return "";
            if (m.Finished) return $"{m.DotCount} of {m.DotCount}";
            if (m.Current < 0) return "";
            var s = $"Step {m.Current + 1} of {Math.Max(m.DotCount, m.Current + 1)}";
            if (m.Paused) s += " · paused";
            else if (m.IsDrill) s += " · drill";
            return s;
        }

        /// "Manual p. 13 · “D. Check the level again …”": the page and the verbatim quote in quotation marks; "" without
        /// a page (a template step).
        public static string ManualLine(CoachStepArgs s)
        {
            if (s == null || !s.page.HasValue) return "";
            var line = $"Manual p. {s.page.Value.ToString(C)}";
            if (!string.IsNullOrWhiteSpace(s.quote)) line += $" · “{s.quote.Trim()}”";
            return line;
        }

        /// The verdict line under the chips.
        public static string VerdictLine(CoachSessionModel m)
        {
            if (m == null) return "";
            if (m.Paused && m.Stop != null) return $"✗ Stop · don't drill here: {KindWord(m.Stop.kind)} below";
            var c = m.LastCheck;
            if (c == null || c.i != m.Current) return "";
            string first(Func<CoachResult, bool> pick, Func<CoachResult, string> say)
            {
                foreach (var r in c.results) if (pick(r)) return say(r);
                return null;
            }
            switch (c.verdict)
            {
                case "passed": return "✓ Looks right";
                case "not_yet":
                    var why = first(r => !r.ok && r.answer != "cant_see", r => string.IsNullOrWhiteSpace(r.evidence) ? null : r.evidence.Trim().TrimEnd('.'));
                    return why == null ? "! Not yet" : $"! Not yet · {why}";
                case "look":
                    var where = first(r => r.answer == "cant_see", r => r.look_at);
                    return string.IsNullOrWhiteSpace(where) ? "? Can't see it from here" : $"? Can't see it from here · {where.Trim().TrimEnd('.')}";
                case "stop": return "✗ Stop · don't drill here";
                default: return "";
            }
        }

        public static string KindWord(string kind) => kind == "switch" ? "a switch" : kind == "outlet" ? "an outlet" : "wiring";

        /// The red card over the drill point.
        public static string StopCard(CoachStopArgs s) => s == null ? "" : $"Don't drill here · {KindWord(s.kind)} below";

        /// coach_done: "✓ All 6 steps · 1 checked by camera · 5 on your word".
        public static string CoachSummary(CoachDoneArgs d, int steps)
        {
            if (d == null) return "";
            string n = steps > 0 ? $"All {steps} steps" : "All steps";
            return $"✓ {n} · {d.@checked} checked by camera · {d.overridden} on your word";
        }

        /// The chip line: glyph, question, and after a check its evidence.
        public static string Chip(CheckChip c)
        {
            if (c == null) return "";
            var s = $"{GrokRailTones.ChipGlyph(c.State)} {c.Question}";
            if (c.State != ChipState.Unchecked && c.State != ChipState.Ok && !string.IsNullOrWhiteSpace(c.Evidence))
                s += $" · {c.Evidence.Trim().TrimEnd('.')}";
            return s;
        }

        /// A server-relative URL (the manual's `pdf_url`) against the server base; absolute URLs pass through.
        public static string Absolute(string serverBase, string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal)) return url;
            var b = (serverBase ?? "").TrimEnd('/');
            return b + (url.StartsWith("/", StringComparison.Ordinal) ? url : "/" + url);
        }
    }
}
