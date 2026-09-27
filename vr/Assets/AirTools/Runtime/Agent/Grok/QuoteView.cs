using System;
using System.Collections.Generic;
using System.Linq;
using AirTools.UI;
using Newtonsoft.Json.Linq;

namespace AirTools.Agent.Grok
{
    /// show_quote_check: the POST /quote/check body (backend docs/api.md, F20). One row per line: printed values plain,
    /// inferred ones grey with "~" (only printed values were read; the model filled the rest in); each line's flags as
    /// chips (warn red, info grey) and compare.text as a link to compare.seller_url; the quote-level flags (line: null)
    /// under the table, with rules.label beside the permit / rebate ones; `label` always.
    public sealed class QuoteView
    {
        public string QuoteId, Job, Label, RulesLabel, RulesNote, Spoken, PermitRequired;
        public Val Contractor, Subtotal, TaxRate, Tax, Total;
        public readonly List<Line> Lines = new List<Line>();
        public readonly List<Flag> QuoteFlags = new List<Flag>();
        public int Inferred, Demoted;

        /// A read value: printed on the paper, or inferred by the model (shown "~", grey).
        public struct Val
        {
            public bool Present, Printed, IsNumber;
            public double Number;
            public string Text;

            public static Val From(JToken t)
            {
                if (!(t is JObject o)) return default;
                var v = o["value"];
                var val = new Val { Printed = GrokText.Str(o, "source") == "printed_text" };
                if (v == null || v.Type == JTokenType.Null) return val;
                val.Present = true;
                if (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) { val.IsNumber = true; val.Number = (double)v; val.Text = GrokText.Number(val.Number); }
                else val.Text = v.Type == JTokenType.String ? (string)v : v.ToString(Newtonsoft.Json.Formatting.None);
                return val;
            }
        }

        public sealed class Flag
        {
            public string Code, Severity, Text, Say;
            public int? Line;
            public bool Warn => Severity == "warn";
        }

        public sealed class Line
        {
            public int I;
            public string Description, Kind, PartId, CompareText, CompareUrl;
            public Val Brand, Model, Qty, UnitPrice, LineTotal;
            public readonly List<Flag> Flags = new List<Flag>();
        }

        public static QuoteView Parse(JObject b)
        {
            if (b == null || !(b["lines"] is JArray lines)) return null;
            var v = new QuoteView
            {
                QuoteId = GrokText.Str(b, "quote_id"),
                Job = GrokText.Str(b, "job"),
                Label = GrokText.Str(b, "label") ?? GrokLabels.Quote,
                RulesNote = GrokText.Str(b, "rules_note"),
                Spoken = GrokText.Str(b, "spoken"),
                Contractor = Val.From(b["contractor"]),
                Inferred = GrokText.Int(b, "inferred") ?? 0,
                Demoted = GrokText.Int(b, "demoted") ?? 0,
            };
            if (b["rules"] is JObject r) { v.RulesLabel = GrokText.Str(r, "label"); v.PermitRequired = GrokText.Str(r, "permit_required"); }
            if (b["totals"] is JObject t)
            {
                v.Subtotal = Val.From(t["subtotal"]); v.TaxRate = Val.From(t["tax_rate_pct"]);
                v.Tax = Val.From(t["tax"]); v.Total = Val.From(t["total"]);
            }
            foreach (var l in lines)
            {
                var line = new Line
                {
                    I = GrokText.Int(l, "i") ?? v.Lines.Count + 1,
                    Description = GrokText.Str(l, "description") ?? "",
                    Kind = GrokText.Str(l, "kind"),
                    PartId = GrokText.Str(l, "part_id"),
                    Brand = Val.From(l["brand"]), Model = Val.From(l["model_no"]), Qty = Val.From(l["qty"]),
                    UnitPrice = Val.From(l["unit_price"]), LineTotal = Val.From(l["line_total"]),
                };
                if (l["compare"] is JObject c) { line.CompareText = GrokText.Str(c, "text"); line.CompareUrl = GrokText.Str(c, "seller_url"); }
                line.Flags.AddRange(Flags(l["flags"]));
                v.Lines.Add(line);
            }
            v.QuoteFlags.AddRange(Flags(b["flags"]).Where(f => !f.Line.HasValue));
            return v;
        }

        static IEnumerable<Flag> Flags(JToken t)
        {
            if (!(t is JArray a)) yield break;
            foreach (var f in a)
                if (f is JObject o)
                    yield return new Flag
                    {
                        Code = GrokText.Str(o, "code") ?? "", Severity = GrokText.Str(o, "severity") ?? "info",
                        Text = GrokText.Str(o, "text") ?? "", Say = GrokText.Str(o, "say"), Line = GrokText.Int(o, "line"),
                    };
        }

        // ---------------- formatting ----------------

        public static ColorRole FlagTone(Flag f) => f.Warn ? ColorRole.Danger : ColorRole.TextSecondary;

        /// The chip's words for a flag code.
        public static string FlagName(string code) => code switch
        {
            "math_line" => "Math",
            "math_subtotal" => "Subtotal math",
            "math_total" => "Total math",
            "tax_rate" => "Tax rate",
            "recall" => "Recall",
            "price_spread" => "Price spread",
            "missing_permit" => "No permit line",
            "rebate" => "Rebate",
            "energy_star_pair" => "ENERGY STAR pair",
            _ => string.IsNullOrEmpty(code) ? "Note" : char.ToUpperInvariant(code[0]) + code.Substring(1).Replace('_', ' '),
        };

        /// Printed values plain; inferred ones grey with "~"; missing ones "?".
        public static string Value(Val v, bool money, Func<ColorRole, string> hex)
        {
            if (!v.Present) return v.Printed ? "?" : GrokText.Color("?", hex(ColorRole.TextSecondary));
            string s = v.IsNumber ? (money ? GrokText.Money(v.Number) : v.Text) : GrokText.Esc(v.Text);
            return v.Printed ? s : GrokText.Color("~" + s, hex(ColorRole.TextSecondary));
        }

        static string Chips(IEnumerable<Flag> flags, Func<ColorRole, string> hex) =>
            string.Join(" ", flags.Select(f => GrokText.Chip(FlagName(f.Code), hex(FlagTone(f)), hex(ColorRole.Control))));

        /// A line's row: "5. Wall sleeve / line hide cover", "2 × $45.00 = $100.00 · model [chips]", the compare text,
        /// then each warn flag's sentence.
        public string LineText(Line l, Func<ColorRole, string> hex)
        {
            string sec = hex(ColorRole.TextSecondary);
            var s = $"<b>{l.I}.</b> {GrokText.Esc(GrokText.Clip(l.Description, 64))}";
            var math = $"{Value(l.Qty, false, hex)} × {Value(l.UnitPrice, true, hex)} = {Value(l.LineTotal, true, hex)}";
            if (l.Model.Present) math += " · " + Value(l.Model, false, hex);
            if (l.Flags.Count > 0) math += "  " + Chips(l.Flags, hex);
            s += "\n" + math;
            if (!string.IsNullOrWhiteSpace(l.CompareText)) s += "\n" + GrokText.Color(GrokText.Esc(l.CompareText), sec);
            foreach (var f in l.Flags.Where(f => f.Warn)) s += "\n" + GrokText.Color(GrokText.Esc(f.Text), hex(ColorRole.Danger));
            return s;
        }

        public List<GrokRow> Rows(Func<ColorRole, string> hex) =>
            Lines.Select(l => new GrokRow { Text = LineText(l, hex) }.Link("Compare", l.CompareUrl)).ToList();

        /// Totals, then the quote-level flags (with rules.label beside the permit / rebate / pair ones), then rules_note.
        public string Notes(Func<ColorRole, string> hex)
        {
            string sec = hex(ColorRole.TextSecondary);
            var lines = new List<string>();
            var totals = new List<string>();
            if (Subtotal.Present || Subtotal.Printed) totals.Add("Subtotal " + Value(Subtotal, true, hex));
            if (Tax.Present) totals.Add("tax " + (TaxRate.Present ? Value(TaxRate, false, hex) + "% " : "") + Value(Tax, true, hex));
            if (Total.Present) totals.Add("total " + Value(Total, true, hex));
            if (totals.Count > 0) lines.Add(string.Join(" · ", totals));
            foreach (var f in QuoteFlags) lines.Add(GrokText.Color("• " + GrokText.Esc(f.Text), hex(f.Warn ? ColorRole.Danger : ColorRole.TextPrimary)));
            if (QuoteFlags.Any(f => f.Code == "missing_permit" || f.Code == "rebate" || f.Code == "energy_star_pair") && !string.IsNullOrWhiteSpace(RulesLabel))
                lines.Add(GrokText.Color(GrokText.Esc(RulesLabel), sec));
            if (!string.IsNullOrWhiteSpace(RulesNote)) lines.Add(GrokText.Color(GrokText.Esc(RulesNote), sec));
            if (Inferred > 0) lines.Add(GrokText.Color($"~ = not printed: {Inferred} value{(Inferred == 1 ? "" : "s")} filled in by the model", sec));
            return string.Join("\n", lines);
        }

        public string Subtitle => Contractor.Present ? Contractor.Text : "";
        public int WarnCount => Lines.Sum(l => l.Flags.Count(f => f.Warn)) + QuoteFlags.Count(f => f.Warn);
    }
}
