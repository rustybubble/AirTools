using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using UnityEngine;

namespace AirTools.Parts
{
    /// Published size in millimetres. Model axes: w = x (left → right), h = y (up), d = z (back → front).
    public class PartDims
    {
        public float w, d, h;
        public Vector3 Metres => new Vector3(w, h, d) * 0.001f;
        public override string ToString() => $"{w:0} × {d:0} × {h:0} mm";
    }

    public class PartFinish
    {
        public string name;
        public string hex;
        public string part_id;
        public Color Color => PartColor.TryParse(hex, out var c) ? c : Color.white;
    }

    public class PartMount
    {
        /// Which face of the model goes against the surface: "-z" (a back face, e.g. on a fascia), "-y" (a bottom, e.g. on a sill)…
        public string face = "-z";
        public string surface;
    }

    /// Space the part needs around it, per face (model axes: top +y, bottom −y, front +z, back −z, right +x, left −x).
    public class PartClearance
    {
        public float front, back, top, bottom, left, right;
    }

    public class PartAsset
    {
        public string tier;
        public string source_url;
        public string license;
        public float? scale_residual_pct;
        /// "pending" → "ready" (or "failed"). Missing (older servers, the shipped catalog) = ready.
        public string status;
        public bool Ready => string.IsNullOrEmpty(status) || status == "ready";
        public bool Failed => status == "failed";
        // assetgen + settings-assets (backend docs/api.md "Asset modes"): who made it (backend asset.made_by: the Hunyuan
        // Space, Grok + openscad, or the LLM that picked the template), the template it picked, whether the Grok retry made
        // it, the mode that made it ("hf" | "llm_scad" | "auto"; null from older servers), why its preferred tier didn't
        // ("OpenSCAD isn't installed on the server: the LLM template instead"), and the server's seconds from start to GLB.
        public string made_by;
        public string template;
        public bool? retried;
        public string mode;
        public string note;
        public float? seconds;
        /// cad: on an "llm_scad" answer, how Grok's CAD model is doing (backend asset.cad): "writing" while the template
        /// stands in (the app polls and swaps the CAD model in when it's "ready"), "failed" (with the reason), null from
        /// older servers and other modes.
        public PartCad cad;

        /// Badge text (backend api.md §2): what the model is and how far to trust its look.
        public string Badge => tier switch
        {
            "cad" => "CAD model",
            "library" => "Library model",
            "llm" => "Model — exact size",
            "scad" => "Model — exact size",
            "ai_mesh" => "AI mesh — exact size, approximate look",
            "proxy" => "Proxy — exact size",
            _ => "Model",
        };
    }

    /// cad: backend asset.cad (docs/api.md "CAD models"): Grok writing OpenSCAD for a part in the LLM+CAD mode.
    public class PartCad
    {
        /// "writing" | "ready" | "failed" | "none".
        public string status;
        /// Server epoch seconds the run started; seconds left by the model's typical run (floored at 10 while running).
        public double? started_at;
        public float? eta_s;
        /// "xai:grok-4.20-0309-non-reasoning + openscad".
        public string made_by;
        /// Why it failed ("OpenSCAD isn't installed on the server", "Grok's OpenSCAD model didn't build: …").
        public string reason;
        public float? seconds;
        public float? cost_usd;
        /// App clock (Time.realtimeSinceStartup) when this answer arrived, so the card counts eta_s down between polls;
        /// 0 = not stamped (the eta as sent).
        [JsonIgnore] public float receivedAt;

        public bool Writing => status == "writing";
        public bool IsReady => status == "ready";
        public bool IsFailed => status == "failed";
    }

    /// Server-side fit against the tape reading sent with the search (backend models.py Fit).
    public class PartServerFit
    {
        public string status;       // fits | too_big | too_small | unknown
        public float? spare_mm;     // negative = too big by that much
        public string axis;
        public string note;
    }

    public class PartRange
    {
        public float? min;
        public float? max;
    }

    public class PartSeller
    {
        public string name;
        public string title;
        /// Per listing (a listing may be a pack of pack_qty units). 0 when the listing has no price.
        public float price_usd;
        public int pack_qty = 1;
        public float? unit_price_usd;
        /// 0 = free, null = unknown.
        public float? shipping_usd;
        /// Raw shipping text ("Free delivery", "+ $20.00") — older part.json files only have this.
        public string shipping;
        public float? total_usd;
        public string eta;
        public int? eta_days;
        public float? rating;
        public int? reviews;
        public bool in_stock = true;
        public string url;
        public bool verified;

        public bool Priced => price_usd > 0f;
        /// Listings to buy for `units` pieces (packs round up).
        public int PacksFor(int units) => Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(1, units) / (float)Mathf.Max(1, pack_qty)));
    }

    /// part.json (implementation plan §1b). Nullable fields may be missing or null.
    public class PartSpec
    {
        public string id;
        public string name;
        public string manufacturer;
        public string model_no;
        public PartDims dims_mm = new PartDims();
        public float? weight_g;
        public string color_hex;
        public string finish;
        public string material;
        public List<PartFinish> finishes = new List<PartFinish>();
        public PartMount mount = new PartMount();
        public PartClearance clearance_mm = new PartClearance();
        public float? spacing_mm;
        public float? min_window_width_mm;
        public float? max_window_width_mm;
        /// Published install range (e.g. a window-opening width), backend api.md; drives the window range when present.
        public PartRange fit_range_mm;
        public PartServerFit fit;
        public string dims_source;
        /// Remote product photo: show it while /parts/{id}/image.jpg is still 404.
        public string image_url;
        public bool sellers_expanded;
        public PartAsset asset = new PartAsset();
        public string spec_url;
        public List<string> citations = new List<string>();
        public List<PartSeller> sellers = new List<PartSeller>();
        public int? recommended_seller;
        public string recommendation_reason;
        public string fetched_at;
        public bool? cached;

        static readonly JsonSerializerSettings s_Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
        };

        public static PartSpec Parse(string json)
        {
            var spec = JsonConvert.DeserializeObject<PartSpec>(json, s_Settings) ?? throw new FormatException("empty part.json");
            if (string.IsNullOrEmpty(spec.id)) throw new FormatException("part.json has no id");
            if (spec.dims_mm == null || spec.dims_mm.w <= 0 || spec.dims_mm.d <= 0 || spec.dims_mm.h <= 0)
                throw new FormatException($"part {spec.id}: dims_mm must be positive");
            spec.mount ??= new PartMount();
            spec.clearance_mm ??= new PartClearance();
            spec.asset ??= new PartAsset();
            spec.finishes ??= new List<PartFinish>();
            spec.citations ??= new List<string>();
            spec.sellers ??= new List<PartSeller>();
            if (!PartMath.IsFace(spec.mount.face)) spec.mount.face = "-z";
            if (spec.fit_range_mm != null && !spec.HasWindowRange)
            {
                spec.min_window_width_mm = spec.fit_range_mm.min;
                spec.max_window_width_mm = spec.fit_range_mm.max;
            }
            spec.sellers.RemoveAll(x => x == null);
            return spec;
        }

        public static T ParseAny<T>(string json) => JsonConvert.DeserializeObject<T>(json, s_Settings);

        public PartSeller RecommendedSeller
        {
            get
            {
                int i = recommended_seller ?? 0;
                return i >= 0 && i < sellers.Count ? sellers[i] : (sellers.Count > 0 ? sellers[0] : null);
            }
        }

        public bool HasWindowRange => min_window_width_mm.HasValue || max_window_width_mm.HasValue;

        public PartFinish FindFinish(string finishName)
        {
            if (string.IsNullOrWhiteSpace(finishName)) return null;
            string n = finishName.Trim().ToLowerInvariant();
            foreach (var f in finishes) if ((f.name ?? "").ToLowerInvariant() == n) return f;
            return null;
        }

        /// assetgen: this part at another size (the Size & finish panel): a shallow copy with its own dims_mm; same id.
        public PartSpec WithDims(PartDims dims)
        {
            var copy = (PartSpec)MemberwiseClone();
            copy.dims_mm = new PartDims { w = dims.w, h = dims.h, d = dims.d };
            return copy;
        }

        public PartSummary ToSummary() => new PartSummary
        {
            id = id, name = name, dims_mm = dims_mm, price_usd = RecommendedSeller?.price_usd, tier = asset?.tier,
            image_url = $"/parts/{id}/image.jpg", model_url = $"/parts/{id}/model.glb", part_url = $"/parts/{id}/part.json",
            remote_image_url = image_url, spec = this,
        };
    }

    /// A search candidate (GET /parts/jobs/&lt;id&gt; → candidates[]).
    public class PartSummary
    {
        public string id;
        public string name;
        public PartDims dims_mm = new PartDims();
        public float? price_usd;
        public string tier;
        public string image_url;
        public string model_url;
        public string part_url;
        /// The seller's product photo (candidates from the real server are full Part objects that carry it).
        public string remote_image_url;
        /// The full part.json when the server sent it with the candidate (the real server does).
        [JsonIgnore] public PartSpec spec;
    }

    /// assetgen: POST /parts/{id}/resize (backend p4-asset): the part's model at a new size.
    public class ResizeReply
    {
        public string part_id;
        public PartDims dims_mm;
        public PartDims listed_mm;
        public string tier;
        public string template;
        public string finish;
        /// "imagine" (the server re-textured it), "tint" (the headset tints it), null (no finish asked).
        public string finish_source;
        /// "template" (rebuilt at the size), "box", "scaled" (the mesh stretched).
        public string source;
        public string reason;
        public string model_url;
        public float? seconds;
        public bool cached;
    }

    /// POST /checkout response (contract §1b; the mock and the Visa sandbox both return it).
    public class CheckoutReceipt
    {
        public string status;
        public string approval_code;
        public string card_last4;
        public float total_usd;
        public string receipt_id;
        public string part_id;
        public string seller;
        public int qty;
        public string sandbox;
        /// "sandbox" (Cybersource test authorization) or "offline" (nothing was authorized; backend api.md §7).
        public string mode;
        /// Honesty label — always shown verbatim.
        public string label;
        public string currency;
        public float? unit_price_usd;
        public float? shipping_usd;
        public List<CheckoutBomLine> bom_lines = new List<CheckoutBomLine>();
        public string created_at;
        /// Measured mandate (B3): intent → cart → authorization → evidence. Null from servers without /checkout/prepare.
        public ReceiptMandate mandate;
        /// The part's cached recall verdict at checkout (backend F6): "recalled" | "caution" | "clear" | "unknown"; null
        /// from older servers. A recalled part still checks out.
        public string safety_verdict;
        /// The "see it installed" picture (F5), e.g. "/parts/{id}/postcard.jpg?v=…": present only when one was made.
        /// Shown with "AI preview, not to scale".
        public string postcard_url;
        public bool Authorized => string.Equals(status, "AUTHORIZED", StringComparison.OrdinalIgnoreCase);
        /// A receipt the server stood behind: authorized, or the labelled offline receipt (no payment, still recorded).
        public bool Recorded => Authorized || string.Equals(mode, "offline", StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(status, "OFFLINE_RECEIPT", StringComparison.OrdinalIgnoreCase);
    }

    public class CheckoutBomLine
    {
        public int idx;
        public string name;
        public int qty;
        public string seller;
        public float? price_usd;
        public float? total_usd;
    }

    /// POST /parts/bom response and the show_bom action's args ("what else do I need?").
    public class PartBom
    {
        public string id;
        public string bom_id;
        public List<string> part_ids = new List<string>();
        public List<PartBomLine> lines = new List<PartBomLine>();
        public float total_usd;
        public string Id => !string.IsNullOrEmpty(bom_id) ? bom_id : id;
    }

    public class PartBomLine
    {
        public int idx;
        public string name;
        public int qty = 1;
        public string reason;
        public PartSeller seller;
    }

    public class SearchJobResponse
    {
        public string job_id;
        public string status;
        public string stage;
        public bool cached;
        public Newtonsoft.Json.Linq.JArray candidates;
    }

    public class SearchJobStatus
    {
        public string id;
        public string status;
        public string stage;
        public string query;
        public string error;
        /// Spoken quartermaster line, present when done.
        public string summary;
        public Newtonsoft.Json.Linq.JArray candidates;
        public bool Done => status == "done";
        public bool Failed => status == "failed";
    }

    public static class PartColor
    {
        public static bool TryParse(string hex, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(hex)) return false;
            return ColorUtility.TryParseHtmlString(hex.StartsWith("#") ? hex : "#" + hex, out color);
        }

        public static string ToHex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);
    }

    public static class PartFormat
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public static string DimsMm(PartDims d) => $"{d.w.ToString("0", C)} × {d.d.ToString("0", C)} × {d.h.ToString("0", C)} mm";

        /// Inches to the nearest eighth, e.g. "5 × 1½ × 1¾ in".
        public static string DimsIn(PartDims d) => $"{Inches(d.w)} × {Inches(d.d)} × {Inches(d.h)} in";

        public static string Inches(float mm)
        {
            int eighths = Mathf.RoundToInt(mm / 25.4f * 8f);
            int whole = eighths / 8, frac = eighths % 8;
            string f = frac switch { 1 => "⅛", 2 => "¼", 3 => "⅜", 4 => "½", 5 => "⅝", 6 => "¾", 7 => "⅞", _ => "" };
            return whole == 0 && f != "" ? f : whole.ToString(C) + f;
        }

        public static string Price(float? usd) => usd.HasValue ? "$" + usd.Value.ToString("0.00", C) : "—";
    }
}
