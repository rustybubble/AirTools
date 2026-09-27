using System;
using System.Collections.Generic;
using System.Globalization;
using AirTools.Scene;
using AirTools.UI;
using Newtonsoft.Json.Linq;

namespace AirTools.Agent.Grok
{
    // Lane G3 (Grok integration): what the backend's panel actions carry (backend docs/api.md §5), turned into plain
    // view models. Pure (JSON in, strings and colour roles out) so every panel is unit-tested offline against the
    // backend's own example payloads; the MonoBehaviours only lay the result out.

    /// The honesty labels the backend always sends (docs/api.md §7); used verbatim, and only as a fallback when an older
    /// server leaves `label` out.
    public static class GrokLabels
    {
        public const string AiPreview = "AI preview, not to scale";
        public const string Rules = "Research with sources, not a permit determination or tax advice. Not legal advice; confirm with the permitting office.";
        public const string Quote = "AI reading of the quote: check it against the paper.";
    }

    /// URL helpers: relative server paths get the server base URL (ServerConfig.Current at runtime).
    public static class GrokUrls
    {
        public static bool IsAbsolute(string url) =>
            !string.IsNullOrEmpty(url) && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        /// "/report/quest-1" + "http://192.168.1.20:8000" → "http://192.168.1.20:8000/report/quest-1"; absolute URLs pass through.
        public static string Absolute(string url, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();
            if (IsAbsolute(url)) return url;
            var b = (baseUrl ?? "").TrimEnd('/');
            return b + (url.StartsWith("/") ? url : "/" + url);
        }

        /// A link only the headset (or the laptop) can open: localhost / 127.0.0.1 is the USB tunnel (adb reverse),
        /// not an address a phone scanning the QR can reach.
        public static bool IsLoopback(string url)
        {
            var h = Host(url);
            return h == "localhost" || h == "127.0.0.1" || h == "[::1]" || h.EndsWith(".localhost");
        }

        /// The site a link goes to, as a chip label: "https://www.estesair.com/ductless" → "estesair.com".
        public static string Host(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            var s = url.Trim();
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);
            int end = s.IndexOfAny(new[] { '/', '?', '#', ':' });
            if (end >= 0) s = s.Substring(0, end);
            s = s.ToLowerInvariant();
            return s.StartsWith("www.") ? s.Substring(4) : s;
        }
    }

    /// Small text helpers shared by the G3 panels (no UnityEngine calls: tests run them offline).
    public static class GrokText
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        /// Server text shown as is: rich-text tags inside it are not interpreted (an honesty label is verbatim).
        public static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : "<noparse>" + s.Replace("</noparse>", "") + "</noparse>";

        public static string Color(string text, string hex) => string.IsNullOrEmpty(hex) ? text : $"<color=#{hex}>{text}</color>";

        /// A chip inside a line of text: a tinted highlight behind the words (TMP mark), words in the tone.
        public static string Chip(string text, string toneHex, string markHex) =>
            $"<mark=#{markHex}> <color=#{toneHex}>{text}</color> </mark>";

        /// "$1,195.00".
        public static string Money(double usd) => "$" + usd.ToString("#,0.00", C);

        /// "$500" for whole dollars, else "$499.50".
        public static string MoneyShort(double usd) => Math.Abs(usd - Math.Round(usd)) < 0.005 ? "$" + Math.Round(usd).ToString("#,0", C) : Money(usd);

        public static string Number(double v) => Math.Abs(v - Math.Round(v)) < 1e-9 ? Math.Round(v).ToString("0", C) : v.ToString("0.##", C);

        public static string Str(JToken t, string key)
        {
            var v = t?[key];
            if (v == null || v.Type == JTokenType.Null) return null;
            if (v.Type == JTokenType.String) return (string)v;
            if (v.Type == JTokenType.Date) return DateText(v);
            return v.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// Json.NET turns ISO date strings into dates while parsing (the agent reply uses its defaults): give them back
        /// as text — "2027-01-01" for a bare date, else the instant in UTC ("2026-10-03T10:32:16Z").
        static string DateText(JToken v)
        {
            if (((JValue)v).Value is DateTimeOffset dto) return dto.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", C);
            var dt = (DateTime)v;
            if (dt.Kind == DateTimeKind.Unspecified && dt.TimeOfDay == TimeSpan.Zero) return dt.ToString("yyyy-MM-dd", C);
            if (dt.Kind == DateTimeKind.Unspecified) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", C);
        }

        public static bool Bool(JToken t, string key) => t?[key] != null && t[key].Type == JTokenType.Boolean && (bool)t[key];

        public static int? Int(JToken t, string key)
        {
            var v = t?[key];
            if (v == null || v.Type == JTokenType.Null) return null;
            if (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) return (int)Math.Round((double)v);
            return int.TryParse((string)v, NumberStyles.Integer, C, out var n) ? n : (int?)null;
        }

        public static double? Num(JToken t, string key)
        {
            var v = t?[key];
            if (v == null || v.Type == JTokenType.Null) return null;
            if (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) return (double)v;
            return double.TryParse((string)v, NumberStyles.Float, C, out var d) ? d : (double?)null;
        }

        public static List<string> Strings(JToken t)
        {
            var list = new List<string>();
            if (t is JArray a)
                foreach (var x in a)
                    if (x != null && x.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)x)) list.Add((string)x);
            return list;
        }

        /// ISO time → "3 Oct 2026, 10:32 UTC" (null when unreadable).
        public static string When(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return null;
            if (!DateTimeOffset.TryParse(iso, C, DateTimeStyles.AssumeUniversal, out var t)) return null;
            return t.ToUniversalTime().ToString("d MMM yyyy, HH:mm", C) + " UTC";
        }

        /// ≤ max characters on a word boundary, "…" when cut.
        public static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            var head = s.Substring(0, Math.Max(1, max - 1));
            int space = head.LastIndexOf(' ');
            if (space >= max / 2) head = head.Substring(0, space);
            return head.TrimEnd(',', ';', ':', ' ', '·') + "…";
        }
    }

    /// One row of a G3 list panel: rich text plus up to two links (pinch/poke chips).
    public sealed class GrokRow
    {
        public string Text = "";
        public readonly List<GrokLink> Links = new List<GrokLink>();

        public GrokRow Link(string label, string url)
        {
            if (GrokUrls.IsAbsolute(url) && Links.Count < 2) Links.Add(new GrokLink { Label = label, Url = url });
            return this;
        }
    }

    public struct GrokLink
    {
        public string Label, Url;
    }

    // ---------------- show_postcard ----------------

    /// show_postcard {part_id, image_url, before_url, label}: the "see it installed" picture; a pinch flips to the
    /// frame it was drawn on (before_url; null for a headset frame, then there's nothing to flip to).
    public sealed class PostcardView
    {
        public string PartId, ImageUrl, BeforeUrl, Label;
        public bool CanFlip => !string.IsNullOrEmpty(BeforeUrl);

        public static PostcardView Parse(JObject a)
        {
            if (a == null) return null;
            var v = new PostcardView
            {
                PartId = GrokText.Str(a, "part_id"),
                ImageUrl = GrokText.Str(a, "image_url"),
                BeforeUrl = GrokText.Str(a, "before_url"),
                Label = GrokText.Str(a, "label") ?? GrokLabels.AiPreview,
            };
            return string.IsNullOrEmpty(v.ImageUrl) ? null : v;
        }
    }

    // ---------------- show_safety ----------------

    /// show_safety {part_id, verdict, headline}: recalled → a red RECALLED pill, caution → amber CAUTION, clear /
    /// unknown → nothing. The pill links to GET /parts/{id}/safety's recalls[0].url.
    public sealed class SafetyView
    {
        public string PartId, Verdict, Headline;

        public bool Shows => Verdict == "recalled" || Verdict == "caution";
        public string Pill => Verdict == "recalled" ? "RECALLED" : Verdict == "caution" ? "CAUTION" : null;
        /// Danger (red) for a recall, Warning (amber) for caution.
        public ColorRole Tone => Verdict == "recalled" ? ColorRole.Danger : ColorRole.Warning;

        public static SafetyView Parse(JObject a)
        {
            if (a == null) return null;
            var v = new SafetyView
            {
                PartId = GrokText.Str(a, "part_id"),
                Verdict = (GrokText.Str(a, "verdict") ?? "unknown").Trim().ToLowerInvariant(),
                Headline = GrokText.Str(a, "headline") ?? "",
            };
            return string.IsNullOrEmpty(v.PartId) ? null : v;
        }

        /// The link the pill opens from a SafetyReport: recalls[0].url, else the first complaint's source (a caution
        /// from owner complaints has no recall), else null.
        public static string LinkFrom(JObject report)
        {
            foreach (var key in new[] { "recalls", "complaints" })
                if (report?[key] is JArray arr)
                    foreach (var r in arr)
                        if (GrokText.Str(r, "url") is string u && GrokUrls.IsAbsolute(u)) return u;
            return null;
        }
    }

    // ---------------- show_manual_answer ----------------

    /// show_manual_answer {part_id, answer, page, quote, pdf_url}: the answer, then "“quote” · p. 9" when the manual
    /// covers it (page null = not covered / no manual / offline: the answer only), and Open manual for pdf_url.
    public sealed class ManualAnswerView
    {
        public string PartId, Answer, Quote, PdfUrl;
        public int? Page;
        public bool Cited => Page.HasValue && !string.IsNullOrWhiteSpace(Quote);

        public static ManualAnswerView Parse(JObject a)
        {
            if (a == null) return null;
            var v = new ManualAnswerView
            {
                PartId = GrokText.Str(a, "part_id"),
                Answer = GrokText.Str(a, "answer") ?? "",
                Page = GrokText.Int(a, "page"),
                Quote = GrokText.Str(a, "quote"),
                PdfUrl = GrokText.Str(a, "pdf_url"),
            };
            return string.IsNullOrWhiteSpace(v.Answer) ? null : v;
        }

        /// The citation line: “quote” · p. 9 (empty when not cited).
        public string Citation() => Cited ? $"“{GrokText.Esc(Quote.Trim().Trim('"', '“', '”'))}” · p. {Page.Value.ToString(CultureInfo.InvariantCulture)}" : "";
    }

    // ---------------- show_packet / packet_revoked / show_report ----------------

    /// show_packet {url, qr_png_url, expires_at, public, packet_id, label}. qr_png_url is null (no server QR): the app
    /// draws the code. public: false → url is the LAN path (/packet/&lt;id&gt;.pdf): prefix the server base URL.
    public sealed class PacketView
    {
        public string PacketId, Url, ExpiresAt, Label, QrPngUrl;
        public bool Public;

        public static PacketView Parse(JObject a)
        {
            if (a == null) return null;
            var v = new PacketView
            {
                PacketId = GrokText.Str(a, "packet_id"),
                Url = GrokText.Str(a, "url"),
                ExpiresAt = GrokText.Str(a, "expires_at"),
                Label = GrokText.Str(a, "label") ?? "",
                QrPngUrl = GrokText.Str(a, "qr_png_url"),
                Public = GrokText.Bool(a, "public"),
            };
            return string.IsNullOrEmpty(v.Url) ? null : v;
        }

        /// What the QR encodes and Open opens.
        public string Link(string baseUrl) => Public && GrokUrls.IsAbsolute(Url) ? Url : GrokUrls.Absolute(Url, baseUrl);

        public string Details()
        {
            var when = GrokText.When(ExpiresAt);
            string reach = Public ? "Public link" : "Local network only";
            return when != null ? $"{reach} · until {when}" : reach;
        }
    }

    /// show_report {url}: a relative URL (/report/&lt;session&gt;): prefix the server base URL; QR + Open.
    public sealed class ReportView
    {
        public string Url;

        public static ReportView Parse(JObject a)
        {
            var url = GrokText.Str(a, "url");
            return string.IsNullOrWhiteSpace(url) ? null : new ReportView { Url = url };
        }

        public string Link(string baseUrl) => GrokUrls.Absolute(Url, baseUrl);
    }

    // ---------------- show_share_preview ----------------

    /// show_share_preview {entry_id, card_url, caption, x_ready, preview_text, confirm_token}: the card on the booth
    /// wall; with x_ready, preview_text and a hold-to-post ring that sends POST /booth/x/confirm {confirm_token} (valid
    /// 5 min, one use). Nothing posts without the hold.
    public sealed class ShareView
    {
        public string EntryId, CardUrl, Caption, PreviewText, ConfirmToken;
        public bool XReady;
        /// The token only exists (and only counts) when X is set up.
        public bool CanPost => XReady && !string.IsNullOrEmpty(ConfirmToken);

        /// The confirm token's lifetime (backend: 5 min, one use).
        public const float TokenSeconds = 300f;

        /// Still worth offering the ring `age` seconds after the preview arrived (a little margin for the round trip).
        public static bool TokenFresh(float age) => age >= 0f && age < TokenSeconds - 5f;

        public static ShareView Parse(JObject a)
        {
            if (a == null) return null;
            var v = new ShareView
            {
                EntryId = GrokText.Str(a, "entry_id"),
                CardUrl = GrokText.Str(a, "card_url"),
                Caption = GrokText.Str(a, "caption") ?? "",
                XReady = GrokText.Bool(a, "x_ready"),
                PreviewText = GrokText.Str(a, "preview_text"),
                ConfirmToken = GrokText.Str(a, "confirm_token"),
            };
            return string.IsNullOrEmpty(v.EntryId) && string.IsNullOrEmpty(v.CardUrl) ? null : v;
        }

        /// POST /booth/x/confirm's body.
        public string ConfirmBody() => new JObject { ["confirm_token"] = ConfirmToken }.ToString(Newtonsoft.Json.Formatting.None);

        /// The confirm reply {tweet_id, url} → the post's URL (null when unreadable).
        public static string PostedUrl(string body)
        {
            try { return GrokText.Str(JObject.Parse(body ?? ""), "url"); }
            catch (Newtonsoft.Json.JsonException) { return null; }
        }

        /// What a failed confirm means for the person (409 expired/used, 503 X not set up or offline, else X's error).
        public static string ConfirmError(long code, string detail) => code switch
        {
            409 => "That post expired or was already used · say “add it to the wall” again",
            503 => "Posting to X isn't set up on the laptop",
            0 => "Couldn't reach the laptop · nothing was posted",
            _ => "X didn't take the post" + (string.IsNullOrWhiteSpace(detail) ? "" : $" · {GrokText.Clip(detail, 60)}"),
        };
    }

    // ---------------- show_installers ----------------

    /// One installer from POST /intel/installers (website, phone, area, quote may be null; evidence_urls ≥ 1).
    public sealed class InstallerRow
    {
        public string Name, Website, Phone, Area, Sentiment, Quote, Source;
        public List<string> Evidence = new List<string>();

        /// The row's text: name · area, phone · what reviews say · source, then the review snippet (model-extracted, so
        /// labelled a snippet, not a verified quote).
        public string Text(Func<ColorRole, string> hex)
        {
            string sec = hex(ColorRole.TextSecondary);
            var line1 = $"<b>{GrokText.Esc(Name)}</b>" + (string.IsNullOrWhiteSpace(Area) ? "" : GrokText.Color($" · {GrokText.Esc(Area)}", sec));
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(Phone)) bits.Add(GrokText.Esc(Phone));
            var mood = SentimentWords(Sentiment);
            if (mood != null) bits.Add(GrokText.Color(mood, hex(SentimentTone(Sentiment))));
            bits.Add(Source == "x" ? "from an X post" : "from the web");
            var s = line1 + "\n" + GrokText.Color(string.Join(" · ", bits), sec);
            if (!string.IsNullOrWhiteSpace(Quote)) s += "\n" + GrokText.Color($"Review snippet: “{GrokText.Esc(GrokText.Clip(Quote.Trim(), 90))}”", sec);
            return s;
        }

        public static string SentimentWords(string s) => (s ?? "").ToLowerInvariant() switch
        {
            "positive" => "good reviews",
            "mixed" => "mixed reviews",
            "negative" => "poor reviews",
            _ => null,
        };

        public static ColorRole SentimentTone(string s) => (s ?? "").ToLowerInvariant() switch
        {
            "positive" => ColorRole.Success,
            "negative" => ColorRole.Danger,
            "mixed" => ColorRole.Warning,
            _ => ColorRole.TextSecondary,
        };
    }

    /// show_installers {installers, summary}: a panel of installers, each one's evidence_urls as links.
    public sealed class InstallersView
    {
        public string Summary;
        public List<InstallerRow> Rows = new List<InstallerRow>();

        /// Every installer keeps at least one evidence URL (the backend drops the rest); shown under the list.
        public const string Note = "Every business here links to where it was found. Review snippets are the model's excerpts, not checked word for word.";

        public static InstallersView Parse(JObject a)
        {
            if (a == null) return null;
            var v = new InstallersView { Summary = GrokText.Str(a, "summary") ?? "" };
            if (a["installers"] is JArray arr)
                foreach (var i in arr)
                {
                    var row = new InstallerRow
                    {
                        Name = GrokText.Str(i, "name") ?? "Installer",
                        Website = GrokText.Str(i, "website"),
                        Phone = GrokText.Str(i, "phone"),
                        Area = GrokText.Str(i, "area"),
                        Sentiment = GrokText.Str(i, "sentiment"),
                        Quote = GrokText.Str(i, "quote"),
                        Source = GrokText.Str(i, "source"),
                        Evidence = GrokText.Strings(i["evidence_urls"]),
                    };
                    row.Evidence.RemoveAll(u => !GrokUrls.IsAbsolute(u));
                    v.Rows.Add(row);
                }
            return v;
        }

        /// One row per installer; its first two evidence URLs as link chips named after their sites.
        public List<GrokRow> RowsFor(Func<ColorRole, string> hex)
        {
            var rows = new List<GrokRow>();
            foreach (var i in Rows)
            {
                var row = new GrokRow { Text = i.Text(hex) };
                foreach (var u in i.Evidence) row.Link(GrokUrls.Host(u), u);
                rows.Add(row);
            }
            return rows;
        }
    }

    // ---------------- show_video / flythrough ----------------

    /// show_video {video_url, poster_url, label, share_url?}: loop the clip on the reimagine quad, label always on.
    public sealed class VideoView
    {
        public string VideoUrl, PosterUrl, Label, ShareUrl;

        public static VideoView Parse(JObject a)
        {
            if (a == null) return null;
            var v = new VideoView
            {
                VideoUrl = GrokText.Str(a, "video_url"),
                PosterUrl = GrokText.Str(a, "poster_url"),
                Label = GrokText.Str(a, "label") ?? GrokLabels.AiPreview,
                ShareUrl = GrokText.Str(a, "share_url"),
            };
            return string.IsNullOrEmpty(v.VideoUrl) && string.IsNullOrEmpty(v.PosterUrl) ? null : v;
        }
    }

    /// GET /scene/flythrough/{job_id}: pending (poster_url known) → done (video_url) or failed (error, spoken; the
    /// poster is the still to show instead).
    public sealed class FlythroughStatus
    {
        public string JobId, Status, VideoUrl, PosterUrl, Label, ShareUrl, Error, Spoken;
        public bool Done => Status == "done";
        public bool Failed => Status == "failed";

        public static FlythroughStatus Parse(string json)
        {
            JObject o;
            try { o = JObject.Parse(json ?? ""); }
            catch (Newtonsoft.Json.JsonException) { return null; }
            return new FlythroughStatus
            {
                JobId = GrokText.Str(o, "job_id"),
                Status = GrokText.Str(o, "status") ?? "pending",
                VideoUrl = GrokText.Str(o, "video_url"),
                PosterUrl = GrokText.Str(o, "poster_url"),
                Label = GrokText.Str(o, "label") ?? GrokLabels.AiPreview,
                ShareUrl = GrokText.Str(o, "share_url"),
                Error = GrokText.Str(o, "error"),
                Spoken = GrokText.Str(o, "spoken"),
            };
        }

        public VideoView ToVideo() => new VideoView { VideoUrl = VideoUrl, PosterUrl = PosterUrl, Label = Label, ShareUrl = ShareUrl };
    }

    // ---------------- show_reimagined ----------------

    /// show_reimagined {image_url, frame_id, camera, label, step, can_undo, drift}: the edit on a quad at the frame's
    /// camera; step 0 = the original frame again (image_url is its thumb), 1 = the reimagine, 2+ = refinements.
    public sealed class ReimagineView
    {
        public string ImageUrl, FrameId, Label;
        public SceneCameraJson Camera;
        public int Step = 1;
        public bool CanUndo;
        public double? Drift;

        public bool IsOriginal => Step == 0;

        public static ReimagineView Parse(JObject a)
        {
            if (a == null) return null;
            SceneCameraJson cam = null;
            if (a["camera"] is JObject c)
            {
                try { cam = c.ToObject<SceneCameraJson>(); }
                catch (Newtonsoft.Json.JsonException) { cam = null; }
            }
            var v = new ReimagineView
            {
                ImageUrl = GrokText.Str(a, "image_url"),
                FrameId = GrokText.Str(a, "frame_id"),
                Camera = cam,
                Label = GrokText.Str(a, "label") ?? GrokLabels.AiPreview,
                Step = GrokText.Int(a, "step") ?? 1,
                CanUndo = GrokText.Bool(a, "can_undo"),
                Drift = GrokText.Num(a, "drift"),
            };
            return string.IsNullOrEmpty(v.ImageUrl) ? null : v;
        }

        /// The line under the picture besides the label: which step, and the undo hint.
        public string StateLine()
        {
            if (IsOriginal) return "The original photo";
            string step = Step <= 1 ? "Reimagined" : $"Step {Step.ToString(CultureInfo.InvariantCulture)}";
            return CanUndo ? $"{step} · say “undo” to step back" : step;
        }

        /// The source frame's thumb ("thumbs/0481.jpg" from cameras.r&lt;rev&gt;.json) → its URL under /scenes/{site}/.
        public static string ThumbUrl(string site, string thumbPath, string frameId)
        {
            if (string.IsNullOrEmpty(site)) return null;
            var path = !string.IsNullOrEmpty(thumbPath) ? thumbPath.TrimStart('/') : string.IsNullOrEmpty(frameId) ? null : $"thumbs/{frameId}.jpg";
            return path == null ? null : $"/scenes/{site}/{path}";
        }
    }
}
