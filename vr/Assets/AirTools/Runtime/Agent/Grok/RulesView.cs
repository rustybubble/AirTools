using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AirTools.UI;
using Newtonsoft.Json.Linq;

namespace AirTools.Agent.Grok
{
    /// show_rules: the GET /rules/check/{id} body (backend docs/api.md "POST /rules/check"), for the two tabs of the
    /// rules card. Permit tab: a pill (yes amber, no green, else grey), the office, item rows "name · office" with an
    /// "unverified quote" tag unless quote_status is verified, who can pull it, inspections, caveats, code editions.
    /// Money tab: rows "name · amount or 'see page' · status" (active green; ended / closed / not eligible grey) with
    /// eligibility_reason under a not_eligible or unverified row; energy_star.missing → a warning row and a
    /// "Find KUSAH121B" button. `label` ("… Not legal advice; confirm with the permitting office.") always shows.
    public sealed class RulesView
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public string CheckId, Status, Job, Label, Disclaimer, Spoken, Research;
        public string Place, State;
        public bool CityOnly;
        public bool PermitResearched;
        public string PermitRequired = "unknown";
        public string OfficeName, OfficePhone, HomeownerAllowed, Licence;
        public readonly List<PermitItem> Items = new List<PermitItem>();
        public readonly List<string> Inspections = new List<string>(), Caveats = new List<string>();
        public readonly List<CodeRow> Codes = new List<CodeRow>();
        public readonly List<MoneyRow> Money = new List<MoneyRow>();
        public string EnergyStarModel, EnergyStarMissing;
        public bool? EnergyStarCertified;
        public double? Seer2, Hspf2;
        public double RebatesUsd;
        public List<string> PartIds = new List<string>();

        public sealed class PermitItem
        {
            public string Name, Office, Url, QuoteStatus, QuoteNote, Fee;
            public bool Unverified => QuoteStatus != "verified";
        }

        public sealed class CodeRow
        {
            public string Code, Name, Applies, NextEdition, Effective;
        }

        public sealed class MoneyRow
        {
            public string Name, Provider, Amount, Status, Eligibility, Reason, Url, Note;
            public bool Counted;
        }

        public bool Done => Status == "done";
        public bool Failed => Status == "failed";

        /// The "find KUSAH121B" command the warning row's button sends (null without a missing half).
        public string FindCommand => string.IsNullOrEmpty(EnergyStarMissing) ? null : $"find {EnergyStarMissing}";

        public static RulesView Parse(JObject b)
        {
            if (b == null) return null;
            var v = new RulesView
            {
                CheckId = GrokText.Str(b, "check_id"),
                Status = GrokText.Str(b, "status") ?? "done",
                Job = GrokText.Str(b, "job"),
                Label = GrokText.Str(b, "label") ?? GrokLabels.Rules,
                Disclaimer = GrokText.Str(b, "disclaimer"),
                Spoken = GrokText.Str(b, "spoken"),
            };
            v.PartIds = GrokText.Strings(b["part_ids"]);
            if (b["jurisdiction"] is JObject j)
            {
                v.State = GrokText.Str(j, "state");
                string where = GrokText.Str(j, "place") ?? GrokText.Str(j, "county") ?? GrokText.Str(j, "authority");
                v.Place = string.Join(", ", new[] { where, v.State }.Where(s => !string.IsNullOrWhiteSpace(s)));
                v.CityOnly = GrokText.Str(j, "confidence") == "city_only";
            }
            if (b["permit"] is JObject p)
            {
                v.PermitResearched = true;
                v.PermitRequired = (GrokText.Str(p, "required") ?? "unknown").ToLowerInvariant();
                if (p["office"] is JObject o) { v.OfficeName = GrokText.Str(o, "name"); v.OfficePhone = GrokText.Str(o, "phone"); }
                if (p["items"] is JArray items)
                    foreach (var i in items)
                        v.Items.Add(new PermitItem
                        {
                            Name = GrokText.Str(i, "name") ?? "Permit",
                            Office = GrokText.Str(i, "office"),
                            Url = GrokText.Str(i, "url"),
                            QuoteStatus = GrokText.Str(i, "quote_status"),
                            QuoteNote = GrokText.Str(i, "quote_note"),
                            Fee = GrokText.Str(i, "fee"),
                        });
                if (p["who_can_pull"] is JObject w) { v.HomeownerAllowed = GrokText.Str(w, "homeowner_allowed"); v.Licence = GrokText.Str(w, "licence"); }
                v.Inspections.AddRange(GrokText.Strings(p["inspections"]));
                v.Caveats.AddRange(GrokText.Strings(p["caveats"]));
            }
            if (b["codes"] is JArray codes)
                foreach (var c in codes)
                    v.Codes.Add(new CodeRow
                    {
                        Code = GrokText.Str(c, "code"), Name = GrokText.Str(c, "name"), Applies = GrokText.Str(c, "applies"),
                        NextEdition = GrokText.Str(c, "next_edition"), Effective = GrokText.Str(c, "effective"),
                    });
            if (b["money"] is JObject m)
            {
                v.Research = GrokText.Str(m, "research");
                v.RebatesUsd = GrokText.Num(m, "rebates_usd") ?? 0;
                if (m["energy_star"] is JObject es)
                {
                    v.EnergyStarModel = GrokText.Str(es, "model");
                    v.EnergyStarMissing = GrokText.Str(es, "missing");
                    v.EnergyStarCertified = es["certified"]?.Type == JTokenType.Boolean ? (bool)es["certified"] : (bool?)null;
                    v.Seer2 = GrokText.Num(es, "seer2");
                    v.Hspf2 = GrokText.Num(es, "hspf2");
                }
                if (m["incentives"] is JArray inc)
                    foreach (var i in inc)
                    {
                        double? amount = GrokText.Num(i, "amount_usd");
                        v.Money.Add(new MoneyRow
                        {
                            Name = GrokText.Str(i, "name") ?? GrokText.Str(i, "program") ?? "Program",
                            Provider = GrokText.Str(i, "provider"),
                            Amount = amount.HasValue ? GrokText.MoneyShort(amount.Value) : "see page",
                            Status = (GrokText.Str(i, "status") ?? "unknown").ToLowerInvariant(),
                            Eligibility = GrokText.Str(i, "eligibility"),
                            Reason = GrokText.Str(i, "eligibility_reason"),
                            Url = GrokText.Str(i, "url") ?? GrokText.Str(i, "status_source"),
                            Note = GrokText.Str(i, "note"),
                            Counted = GrokText.Bool(i, "counted"),
                        });
                    }
            }
            return v;
        }

        // ---------------- tones ----------------

        /// Permit pill: yes amber, no green, anything else grey.
        public static ColorRole PermitTone(string required) => (required ?? "").ToLowerInvariant() switch
        {
            "yes" => ColorRole.Warning,
            "no" => ColorRole.Success,
            _ => ColorRole.TextSecondary,
        };

        /// The status a money row shows: "not eligible" wins, an unverified eligibility is said so, else the status.
        public static string MoneyStatus(MoneyRow r)
        {
            if (r.Eligibility == "not_eligible" || r.Status == "not_eligible") return "not eligible";
            string s = (r.Status ?? "unknown").Replace('_', ' ');
            return r.Eligibility == "unverified" ? (s == "unknown" ? "unverified" : s + " · unverified") : s;
        }

        /// Active (and not unverified / not eligible) is green; everything else grey.
        public static ColorRole MoneyTone(MoneyRow r) =>
            r.Status == "active" && r.Eligibility != "not_eligible" && r.Eligibility != "unverified" ? ColorRole.Success : ColorRole.TextSecondary;

        /// eligibility_reason shows under a not_eligible or unverified row.
        public static bool ShowsReason(MoneyRow r) =>
            !string.IsNullOrWhiteSpace(r.Reason) && (r.Eligibility == "not_eligible" || r.Eligibility == "unverified" || r.Status == "not_eligible");

        // ---------------- permit tab ----------------

        public string PermitPillText => $"Permit: {PermitRequired}";

        public string PermitHeader(Func<ColorRole, string> hex)
        {
            string sec = hex(ColorRole.TextSecondary);
            var lines = new List<string>();
            string pill = GrokText.Chip(PermitPillText, hex(PermitTone(PermitRequired)), hex(ColorRole.Control));
            string where = string.IsNullOrWhiteSpace(Place) ? "" : "  " + GrokText.Esc(Place) + (CityOnly ? GrokText.Color(" · from the city only; say the address to be sure", sec) : "");
            lines.Add(pill + where);
            if (!PermitResearched) lines.Add(GrokText.Color("Couldn't research the permit rules right now.", sec));
            if (!string.IsNullOrWhiteSpace(OfficeName))
                lines.Add("Office: " + GrokText.Esc(OfficeName) + (string.IsNullOrWhiteSpace(OfficePhone) ? "" : " · " + GrokText.Esc(OfficePhone)));
            if (!string.IsNullOrWhiteSpace(HomeownerAllowed))
                lines.Add(GrokText.Color("Homeowner can pull it: " + GrokText.Esc(HomeownerAllowed) + (string.IsNullOrWhiteSpace(Licence) ? "" : " · " + GrokText.Esc(GrokText.Clip(Licence, 60))), sec));
            if (Inspections.Count > 0) lines.Add(GrokText.Color("Inspections: " + GrokText.Esc(string.Join(", ", Inspections)), sec));
            foreach (var c in Caveats.Take(2)) lines.Add(GrokText.Color("• " + GrokText.Esc(GrokText.Clip(c, 110)), sec));
            foreach (var code in Codes.Take(3))
            {
                string next = string.IsNullOrWhiteSpace(code.NextEdition) ? "" : $"; {code.NextEdition}" + (string.IsNullOrWhiteSpace(code.Effective) ? "" : $" from {code.Effective}");
                lines.Add(GrokText.Color($"{GrokText.Esc(code.Code)}: {GrokText.Esc(code.Applies)}{GrokText.Esc(next)}", sec));
            }
            return string.Join("\n", lines);
        }

        public List<GrokRow> PermitRows(Func<ColorRole, string> hex)
        {
            var rows = new List<GrokRow>();
            string sec = hex(ColorRole.TextSecondary);
            foreach (var i in Items)
            {
                var t = GrokText.Esc(i.Name) + (string.IsNullOrWhiteSpace(i.Office) ? "" : GrokText.Color(" · " + GrokText.Esc(i.Office), sec));
                if (!string.IsNullOrWhiteSpace(i.Fee)) t += "\nFee " + GrokText.Esc(i.Fee);
                if (i.Unverified) t += "\n" + GrokText.Chip("unverified quote", hex(ColorRole.Warning), hex(ColorRole.Control))
                                      + (string.IsNullOrWhiteSpace(i.QuoteNote) ? "" : GrokText.Color(" " + GrokText.Esc(i.QuoteNote), sec));
                rows.Add(new GrokRow { Text = t }.Link("Office page", i.Url));
            }
            return rows;
        }

        // ---------------- money tab ----------------

        public string MoneyHeader(Func<ColorRole, string> hex)
        {
            string sec = hex(ColorRole.TextSecondary);
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(EnergyStarModel))
            {
                string cert = EnergyStarCertified == true ? "ENERGY STAR certified" : EnergyStarCertified == false ? "not ENERGY STAR listed" : "ENERGY STAR not checked";
                var specs = new List<string>();
                if (Seer2.HasValue) specs.Add("SEER2 " + GrokText.Number(Seer2.Value));
                if (Hspf2.HasValue) specs.Add("HSPF2 " + GrokText.Number(Hspf2.Value));
                lines.Add($"{GrokText.Esc(EnergyStarModel)}: {cert}" + (specs.Count > 0 ? GrokText.Color(" · " + string.Join(" · ", specs), sec) : ""));
            }
            if (!string.IsNullOrWhiteSpace(EnergyStarMissing))
                lines.Add(GrokText.Color($"⚠ Rebate needs the outdoor unit {GrokText.Esc(EnergyStarMissing)}", hex(ColorRole.Warning)));
            lines.Add(RebatesUsd > 0 ? $"Rebates that count: {GrokText.MoneyShort(RebatesUsd)}" : GrokText.Color("No rebate counted yet: only eligible, active amounts come off the price", sec));
            if (Research == "unavailable") lines.Add(GrokText.Color("Rebate research is unavailable right now; programs from our own table.", sec));
            return string.Join("\n", lines);
        }

        public List<GrokRow> MoneyRows(Func<ColorRole, string> hex)
        {
            var rows = new List<GrokRow>();
            string sec = hex(ColorRole.TextSecondary);
            foreach (var r in Money)
            {
                var t = GrokText.Esc(r.Name) + " · " + GrokText.Esc(r.Amount) + " · " + GrokText.Color(MoneyStatus(r), hex(MoneyTone(r)));
                if (ShowsReason(r)) t += "\n" + GrokText.Color(GrokText.Esc(r.Reason), sec);
                rows.Add(new GrokRow { Text = t }.Link("Program", r.Url));
            }
            return rows;
        }

        public string Subtitle => string.IsNullOrWhiteSpace(Job) ? Place ?? "" : Job.Replace('_', ' ') + (string.IsNullOrWhiteSpace(Place) ? "" : " · " + Place);
    }
}
