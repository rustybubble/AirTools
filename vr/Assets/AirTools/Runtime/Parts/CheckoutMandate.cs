using System;
using System.Collections.Generic;
using System.Globalization;
using AirTools.Notes;
using AirTools.Scene;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Parts
{
    /// Measured mandate (B1/B3 hand-off §5, brain.md S2): the shapes of POST /checkout/prepare, the receipt's `mandate`
    /// and POST /commerce/limits. Plain data; CheckoutPanel renders them, PartsClient sends and parses them.

    /// One row the panel shows before the hold: ok (green ✓), warn (amber ⚠, doesn't block) or fail (red ✗, blocks Pay).
    public class CheckoutCheck
    {
        public string id;
        public string status;
        public bool ok = true;
        public string detail;
        /// "ok" | "warn" | "fail" (old servers send only `ok`).
        public string Status => !string.IsNullOrEmpty(status) ? status : ok ? "ok" : "fail";
        public bool Fails => Status == "fail";
    }

    /// The session's limits (IntentMandate.summary()). Nulls mean no limit.
    public class MandateIntent
    {
        public string id;
        public float? max_total_usd;
        public string deliver_by;
        public string seller_policy;
        public string source;
        public string text;
        public bool Any => max_total_usd.HasValue || !string.IsNullOrEmpty(deliver_by) || !string.IsNullOrEmpty(seller_policy);
    }

    public class MandateCartLine
    {
        public string part_id;
        public int seller_idx;
        public string seller;
        public int units_needed;
        public int pack_qty = 1;
        public int packs;
        public float unit_price_usd;
        public float shipping_usd;
        public float line_total_usd;
    }

    public class EvidenceArray
    {
        public float? spacing_mm;
        public int? count;
    }

    /// A headset measurement behind the quantity: the array's tape entry, its length, spacing, count and photo.
    public class CheckoutEvidence
    {
        /// int (NotebookEntry.Id) or string.
        public JToken notebook_id;
        public string label;
        public float? value_m;
        public int? camera_id;
        public string photo;
        public EvidenceArray array;
    }

    public class PreparedCart
    {
        public string id;
        public string intent_id;
        public List<MandateCartLine> lines = new List<MandateCartLine>();
        public string bom_id;
        public List<JObject> bom_lines = new List<JObject>();
        public float shipping_usd;
        public float total_usd;
        public List<CheckoutEvidence> evidence = new List<CheckoutEvidence>();
        public string created_at;
    }

    /// POST /checkout/prepare → the server-priced cart, its hash, the single-use hold nonce (120 s), limits, checks.
    public class CheckoutPrepared
    {
        public PreparedCart cart;
        public string cart_hash;
        public string hold_nonce;
        public string nonce_expires_at;
        public MandateIntent intent;
        public List<CheckoutCheck> checks = new List<CheckoutCheck>();
        public bool all_ok;
        /// Listings to charge (what /checkout calls qty).
        public int Packs => cart != null && cart.lines != null && cart.lines.Count > 0 ? cart.lines[0].packs : 0;
    }

    /// What CheckoutPanel sent to /checkout/prepare (the /checkout that redeems its nonce must match it exactly).
    public class PrepareRequest
    {
        public string session_id;
        public string part_id;
        public int seller_idx;
        public int units_needed;
        public string bom_id;
        public List<int> bom_lines = new List<int>();
        public List<CheckoutEvidence> evidence = new List<CheckoutEvidence>();
    }

    /// The physical hold's proof: made only by CheckoutPanel's hold handler, sent only by PartsClient.Checkout.
    public class HoldProof
    {
        public string CartHash;
        public string Nonce;
        public int HeldMs;
    }

    public class MandateCart
    {
        public string id;
        public string hash;
        public float total_usd;
        public int? units_needed;
        public int? packs;
    }

    public class MandateAuthorization
    {
        /// "sandbox" | "offline"
        public string mode;
        public string status;
        public string approval_code;
        public int? hold_ms;
        public bool Sandbox => string.Equals(mode, "sandbox", StringComparison.OrdinalIgnoreCase);
    }

    /// receipt.mandate: intent → cart → authorization → evidence (+ the checks as they were at payment).
    public class ReceiptMandate
    {
        public MandateIntent intent;
        public MandateCart cart;
        public MandateAuthorization authorization;
        public List<CheckoutEvidence> evidence = new List<CheckoutEvidence>();
        public List<CheckoutCheck> checks = new List<CheckoutCheck>();
    }

    /// POST /commerce/limits response.
    public class LimitsResult
    {
        public MandateIntent intent;
        public List<JObject> refused = new List<JObject>();
        public List<string> changed = new List<string>();
    }

    /// A refused /checkout or /checkout/prepare: 400/409 carry `detail` as a string, 422 as {error, checks[]}.
    public class CheckoutError
    {
        public long Code;
        public string Detail;
        public List<CheckoutCheck> Checks;
        public override string ToString() => $"{Code} {Detail}";

        public static CheckoutError Parse(long code, string body, string fallback = null)
        {
            var e = new CheckoutError { Code = code, Detail = fallback };
            if (string.IsNullOrEmpty(body)) return e;
            try
            {
                var d = JObject.Parse(body)["detail"];
                if (d is JObject o)
                {
                    e.Detail = (string)o["error"] ?? o.ToString(Formatting.None);
                    if (o["checks"] is JArray a) e.Checks = a.ToObject<List<CheckoutCheck>>();
                }
                else if (d != null && d.Type == JTokenType.String) e.Detail = (string)d;
                else if (d is JArray list)
                {
                    // FastAPI body validation: [{"type", "loc", "msg"}, …].
                    var msgs = new List<string>();
                    foreach (var item in list) msgs.Add(item is JObject io && io["msg"] != null ? (string)io["msg"] : item.ToString(Formatting.None));
                    e.Detail = string.Join("; ", msgs);
                }
                else if (d != null) e.Detail = d.ToString(Formatting.None);
            }
            catch (Exception) { e.Detail = body.Length > 200 ? body.Substring(0, 200) : body; }
            return e;
        }
    }

    /// Rows and lines for the panel and the receipt (pure, for tests).
    public static class MandateText
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public static string Glyph(string status) => status == "fail" ? "✗" : status == "warn" ? "⚠" : "✓";

        public static string Title(string id) => id switch
        {
            "price_reread" => "Price re-read",
            "within_limit" => "Within your limit",
            "delivery" => "Delivery",
            "qty_evidence" => "Quantity",
            "seller_verified" => "Seller",
            "fit" => "Fit",
            "card" => "Card",
            _ => AirTools.UI.Copy.Cap((id ?? "").Replace('_', ' ')),
        };

        static string Hex(Color c) => ColorUtility.ToHtmlStringRGBA(c);

        /// "✓ Price re-read · $2.98 × 8 from the saved listing", glyph coloured by status (words carry the state too).
        public static string Row(CheckoutCheck c)
        {
            var col = AirTools.UI.UiTheme.Current.colors;
            string s = c.Status;
            var tint = s == "fail" ? col.danger : s == "warn" ? col.warning : col.success;
            // D2: the server words its details in metric ("tape #3 4.20 m ÷ 600 mm → 8"); show them in the user's unit.
            return $"<color=#{Hex(tint)}>{Glyph(s)}</color> {Title(c.id)} <color=#{Hex(col.textSecondary)}>· {AirTools.UI.Copy.Lengths(AirTools.UI.Copy.Clean(c.detail))}</color>";
        }

        public static string Rows(IEnumerable<CheckoutCheck> checks)
        {
            var lines = new List<string>();
            if (checks != null) foreach (var c in checks) if (c != null) lines.Add(Row(c));
            return string.Join("\n", lines);
        }

        /// "≤ $40 · by Fri · fastest"; "No limits" when nothing is set.
        public static string Limits(MandateIntent i)
        {
            if (i == null || !i.Any) return "No limits";
            var bits = new List<string>();
            if (i.max_total_usd.HasValue) bits.Add($"≤ {Money(i.max_total_usd.Value)}");
            if (!string.IsNullOrEmpty(i.deliver_by)) bits.Add($"by {Day(i.deliver_by)}");
            if (!string.IsNullOrEmpty(i.seller_policy)) bits.Add(i.seller_policy);
            return string.Join(" · ", bits);
        }

        public static string Money(float usd) => Mathf.Approximately(usd, Mathf.Round(usd)) ? "$" + usd.ToString("0", C) : "$" + usd.ToString("0.00", C);

        /// ISO date → weekday ("2026-10-02" → "Fri"); anything else verbatim.
        public static string Day(string iso) =>
            DateTime.TryParseExact(iso, "yyyy-MM-dd", C, DateTimeStyles.None, out var d) ? d.ToString("ddd", C) : iso;

        /// The receipt's chain rows (hand-off §5.3): Intent ✓ · Cart ✓ …last 4 · Authorization ✓ code / ✗ offline · Evidence.
        public static string Chain(ReceiptMandate m)
        {
            if (m == null) return "";
            var col = AirTools.UI.UiTheme.Current.colors;
            string ok = $"<color=#{Hex(col.success)}>✓</color>", no = $"<color=#{Hex(col.danger)}>✗</color>";
            string sec(string s) => $"<color=#{Hex(col.textSecondary)}>{s}</color>";
            var rows = new List<string>();
            string intent = m.intent == null || !m.intent.Any ? "no limits" : !string.IsNullOrEmpty(m.intent.text) ? AirTools.UI.Copy.Clean(m.intent.text) : Limits(m.intent);
            rows.Add($"{ok} Intent {sec("· " + intent)}");
            string hash = m.cart?.hash ?? "";
            rows.Add($"{ok} Cart {sec("· …" + (hash.Length >= 4 ? hash.Substring(hash.Length - 4) : hash) + (m.cart != null ? " · " + PartFormat.Price(m.cart.total_usd) : ""))}");
            var a = m.authorization;
            if (a != null && a.Sandbox && !string.IsNullOrEmpty(a.approval_code))
                rows.Add($"{ok} Authorization {sec($"· approval {a.approval_code} · sandbox")}");
            else rows.Add($"{no} Authorization {sec("· offline, no payment")}");
            var e = m.evidence != null && m.evidence.Count > 0 ? m.evidence[0] : null;
            if (e != null)
            {
                string what = !string.IsNullOrEmpty(e.label) ? AirTools.UI.Copy.Clean(e.label) : "measurement";
                if (e.value_m.HasValue) what += $" {AirTools.UI.Copy.Len(e.value_m.Value)}";   // D2: the user's unit (value_m stays metres)
                if (e.array?.count != null) what += $" → {e.array.count}";
                string photo = !string.IsNullOrEmpty(e.photo) ? " · photo" : e.camera_id.HasValue ? $" · camera {e.camera_id}" : "";
                rows.Add($"{ok} Evidence {sec("· " + what + photo)}");
            }
            else rows.Add($"{sec("– Evidence · none sent")}");
            return string.Join("\n", rows);
        }
    }

    /// The prepare request's evidence (hand-off §5.1): the array group's tape entry, its length, spacing, count and the
    /// tape's evidence camera (its thumbnail as the photo). Without an array: the latest tape reading.
    public static class MandateEvidence
    {
        public static List<CheckoutEvidence> For(PartTool tool, string partId, SceneRoot root)
        {
            var list = new List<CheckoutEvidence>();
            var g = tool != null ? tool.LastArray : null;
            if (g != null && g.Reference != null && g.Reference.Spec != null && g.Reference.Spec.id == partId)
            {
                var tape = g.TapeEntryId > 0 ? Notebook.Find(g.TapeEntryId) : null;
                int cam = tape != null && tape.NearestCameraId >= 0 ? tape.NearestCameraId : g.Entry != null ? g.Entry.NearestCameraId : -1;
                list.Add(new CheckoutEvidence
                {
                    notebook_id = g.TapeEntryId > 0 ? new JValue(g.TapeEntryId) : g.Entry != null ? new JValue(g.Entry.Id) : null,
                    label = g.TapeEntryId > 0 ? $"tape #{g.TapeEntryId}" : "tape",
                    value_m = (float)Math.Round(tape != null ? tape.ValueSI : g.Plan.LengthM, 4),
                    camera_id = cam >= 0 ? cam : (int?)null,
                    photo = Photo(root, cam),
                    array = new EvidenceArray { spacing_mm = Mathf.Round(g.Plan.SpacingM * 1000f), count = g.Plan.Count },
                });
                return list;
            }
            var entries = Notebook.Entries;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (e.Tool != "measure" || e.Points == null || e.Points.Length != 2) continue;
                if (!e.OnCurrentSite) continue;   // sitescope: this scan's tape is the evidence
                list.Add(new CheckoutEvidence
                {
                    notebook_id = new JValue(e.Id), label = $"tape #{e.Id}", value_m = (float)Math.Round(e.ValueSI, 4),
                    camera_id = e.NearestCameraId >= 0 ? e.NearestCameraId : (int?)null, photo = Photo(root, e.NearestCameraId),
                });
                break;
            }
            return list;
        }

        /// /scenes/<site>/thumbs/0316.jpg for a package camera (null on the built-in scene).
        public static string Photo(SceneRoot root, int cameraId)
        {
            if (root == null || cameraId < 0 || !root.IsRuntimePackage || string.IsNullOrEmpty(root.Site)) return null;
            string thumb = null;
            if (root.Package != null && root.Package.cameras != null)
                foreach (var c in root.Package.cameras) if (c.id == cameraId) { thumb = c.thumbPath; break; }
            thumb ??= $"thumbs/{cameraId.ToString("0000", CultureInfo.InvariantCulture)}.jpg";
            return $"/scenes/{root.Site}/{thumb}";
        }
    }
}
