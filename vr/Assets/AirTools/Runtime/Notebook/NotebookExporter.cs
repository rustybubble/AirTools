using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace AirTools.Notes
{
    /// Notebook export (SPEC M3): CSV for spreadsheets, a self-contained HTML report with each reading's evidence
    /// photo embedded, and JSON for POST /notebook.
    public static class NotebookExporter
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        public static readonly string[] CsvColumns =
            { "id", "tool", "value_si", "unit", "label", "time", "camera_id", "points", "sides_m", "angles_deg", "off_plane_m" };

        public static string ToCsv(IReadOnlyList<NotebookEntry> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", CsvColumns));
            foreach (var e in entries)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    e.Id.ToString(C), Csv(e.Tool), e.ValueSI.ToString("R", C), Csv(e.Unit), Csv(e.Label),
                    e.Time.ToString("yyyy-MM-ddTHH:mm:ss", C), e.NearestCameraId.ToString(C),
                    Csv(string.Join(" ; ", (e.Points ?? Array.Empty<Vector3>()).Select(P))),
                    Csv(string.Join(" ; ", e.Sides.Select(x => x.ToString("0.0000", C)))),
                    Csv(string.Join(" ; ", e.Angles.Select(x => x.ToString("0.00", C)))),
                    e.PlanarityError.ToString("0.0000", C),
                }));
            }
            return sb.ToString();
        }

        /// Parse our own CSV back (quoted fields, embedded commas/quotes/newlines). Used by tests and tooling.
        public static List<string[]> ParseCsv(string text)
        {
            var rows = new List<string[]>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else if (ch == '"') quoted = false;
                    else field.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == ',') { row.Add(field.ToString()); field.Clear(); }
                else if (ch == '\n' || ch == '\r')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row.ToArray()); row.Clear();
                }
                else field.Append(ch);
            }
            if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
            return rows;
        }

        public static string ToHtml(IReadOnlyList<NotebookEntry> entries, Func<int, Texture2D> thumbnailFor, string title)
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(Html(title)).Append("</title>");
            sb.Append("<style>body{font-family:-apple-system,Segoe UI,sans-serif;margin:24px;color:#222}" +
                      "table{border-collapse:collapse;width:100%}th,td{border-bottom:1px solid #ddd;padding:8px;text-align:left;vertical-align:top}" +
                      "th{background:#f4f4f6}td.v{font-weight:600;white-space:nowrap}img{width:160px;border-radius:4px}</style></head><body>");
            sb.Append("<h1>").Append(Html(title)).Append("</h1>");
            sb.Append($"<p>{entries.Count} reading{(entries.Count == 1 ? "" : "s")} · exported {DateTime.Now.ToString("MMM d, h:mm tt", C)}</p>");
            sb.Append("<table><tr><th>#</th><th>Photo</th><th>What</th><th>Value</th><th>Details</th><th>Time</th></tr>");
            foreach (var e in entries)
            {
                sb.Append("<tr class=\"entry\"><td>").Append(e.Id).Append("</td><td>");
                var tex = e.NearestCameraId >= 0 ? thumbnailFor?.Invoke(e.NearestCameraId) : null;
                var jpg = tex != null ? EncodeJpg(tex) : null;
                if (jpg != null) sb.Append("<img alt=\"camera ").Append(e.NearestCameraId).Append("\" src=\"data:image/jpeg;base64,").Append(Convert.ToBase64String(jpg)).Append("\">");
                else sb.Append("—");
                // Display words (NotebookRow); the full raw label stays in Details (and verbatim in the CSV / JSON).
                string value = NotebookRow.Value(e);
                if (string.IsNullOrEmpty(value)) value = e.ValueSI.ToString("0.000", C) + " " + e.Unit;
                string detail = AirTools.UI.Copy.Clean(e.Label);
                if (!string.IsNullOrEmpty(e.DisplayDetail)) detail = e.DisplayDetail + " · " + detail;
                sb.Append("</td><td>").Append(Html(NotebookRow.Title(e))).Append("</td><td class=\"v\">")
                  .Append(Html(value)).Append("</td><td>")
                  .Append(Html(detail)).Append("</td><td>").Append(e.Time.ToString("h:mm tt", C)).Append("</td></tr>");
            }
            sb.Append("</table></body></html>");
            return sb.ToString();
        }

        public static string ToJson(IReadOnlyList<NotebookEntry> entries, string site) => ToJson(entries, site, null);

        /// POST /notebook body: {session_id, site, exported_at, entries[]} (backend api.md: session_id + entries required).
        /// Each entry keeps the app's own fields (id, tool, value_si, unit, label, text, time, camera_id, points, sides_m,
        /// angles_deg) and adds the backend's (docs/api.md "Entry types the site-walk report reads"): `type`
        /// (measurement / note / placement, else the app's tool), `ts` (UTC), and per type: measurement → tool
        /// (tape / area / level / ladder), value_m or value + unit, site, nearest_camera_id, thumb, quality; note → text,
        /// share, pin_id, frame_id; placement → part_id, count, total, where. `raw`: extra entries already in JSON
        /// (e.g. {"type":"scan_loaded","ts":…}), sent first.
        public static string ToJson(IReadOnlyList<NotebookEntry> entries, string site, string sessionId, IReadOnlyList<string> raw = null)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            if (sessionId != null) sb.Append("\"session_id\":").Append(Json(sessionId)).Append(',');
            sb.Append("\"site\":").Append(Json(site)).Append(",\"exported_at\":").Append(Json(DateTime.Now.ToString("o", C))).Append(",\"entries\":[");
            int n = 0;
            if (raw != null) foreach (var r in raw) { if (string.IsNullOrEmpty(r)) continue; if (n++ > 0) sb.Append(','); sb.Append(r); }
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (n++ > 0) sb.Append(',');
                string type = BackendType(e);
                sb.Append("{\"id\":").Append(e.Id)
                  .Append(",\"tool\":").Append(Json(type == "measurement" ? BackendTool(e) : e.Tool))
                  .Append(",\"value_si\":").Append(e.ValueSI.ToString("R", C))
                  .Append(",\"unit\":").Append(Json(e.Unit))
                  .Append(",\"type\":").Append(Json(type))
                  .Append(",\"label\":").Append(Json(e.Label))
                  .Append(",\"text\":").Append(Json(e.Label))
                  .Append(",\"time\":").Append(Json(e.Time.ToString("o", C)))
                  .Append(",\"ts\":").Append(Json(Timestamp(e.Time)))
                  .Append(",\"camera_id\":").Append(e.NearestCameraId)
                  .Append(",\"points\":[").Append(string.Join(",", (e.Points ?? Array.Empty<Vector3>()).Select(p => $"[{F(p.x)},{F(p.y)},{F(p.z)}]"))).Append(']')
                  .Append(",\"sides_m\":[").Append(string.Join(",", e.Sides.Select(x => x.ToString("R", C)))).Append(']')
                  .Append(",\"angles_deg\":[").Append(string.Join(",", e.Angles.Select(x => x.ToString("R", C)))).Append(']');
                if (!string.IsNullOrEmpty(e.Site)) sb.Append(",\"site\":").Append(Json(e.Site));
                if (!string.IsNullOrEmpty(e.CameraKey)) sb.Append(",\"nearest_camera_id\":").Append(Json(e.CameraKey));
                if (!string.IsNullOrEmpty(e.ThumbPath)) sb.Append(",\"thumb\":").Append(Json(e.ThumbPath));
                switch (type)
                {
                    case "measurement":
                        // Metres as value_m; anything else (°, m², ×) as value + unit.
                        if (e.Unit == "m") sb.Append(",\"value_m\":").Append(e.ValueSI.ToString("R", C));
                        else sb.Append(",\"value\":").Append(e.ValueSI.ToString("R", C));
                        if (!string.IsNullOrEmpty(e.Quality)) sb.Append(",\"quality\":").Append(Json(e.Quality));
                        break;
                    case "note":
                        if (e.Share) sb.Append(",\"share\":true");
                        if (!string.IsNullOrEmpty(e.PinId)) sb.Append(",\"pin_id\":").Append(Json(e.PinId));
                        if (!string.IsNullOrEmpty(e.FrameId)) sb.Append(",\"frame_id\":").Append(Json(e.FrameId));
                        break;
                    case "placement":
                        sb.Append(",\"part_id\":").Append(Json(e.PartId)).Append(",\"count\":").Append(Math.Max(0, e.Count));
                        if (e.Total > 0) sb.Append(",\"total\":").Append(e.Total);
                        if (!string.IsNullOrEmpty(e.Where)) sb.Append(",\"where\":").Append(Json(e.Where));
                        break;
                    case "placement_pose":   // placement: a saved placement (not counted as a piece by the report)
                        sb.Append(",\"part_id\":").Append(Json(e.PartId)).Append(",\"slot\":").Append(Json(e.Slot));
                        if (!string.IsNullOrEmpty(e.Where)) sb.Append(",\"where\":").Append(Json(e.Where));
                        if (e.Pose != null && e.Pose.Length == 7)
                            sb.Append(",\"pose\":{\"p\":[").Append(D(e.Pose[0], "0.0000")).Append(',').Append(D(e.Pose[1], "0.0000")).Append(',').Append(D(e.Pose[2], "0.0000"))
                              .Append("],\"q\":[").Append(D(e.Pose[3], "0.000000")).Append(',').Append(D(e.Pose[4], "0.000000")).Append(',').Append(D(e.Pose[5], "0.000000")).Append(',').Append(D(e.Pose[6], "0.000000"))
                              .Append("],\"anchor\":\"origin\",\"frame\":\"scene\"}");
                        if (e.DimsMm != null && e.DimsMm.Length == 3)   // assetgen: its size and finish
                            sb.Append(",\"dims_mm\":{\"w\":").Append(D(e.DimsMm[0], "0.#")).Append(",\"h\":").Append(D(e.DimsMm[1], "0.#"))
                              .Append(",\"d\":").Append(D(e.DimsMm[2], "0.#")).Append('}');
                        if (!string.IsNullOrEmpty(e.Finish)) sb.Append(",\"finish\":").Append(Json(e.Finish));
                        break;
                }
                if (!string.IsNullOrEmpty(e.PostcardUrl)) sb.Append(",\"postcard_url\":").Append(Json(e.PostcardUrl)).Append(",\"postcard_label\":").Append(Json(PostcardLabel));
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// The postcard's honesty label (backend docs/api.md POST /parts/{id}/postcard), verbatim.
        public const string PostcardLabel = "AI preview, not to scale";

        /// The backend's entry type: readings are "measurement", notes "note", placed parts and arrays "placement" (with a
        /// part id); everything else keeps the app's tool name (purchase, bom, scale…: the report ignores them; the
        /// server writes its own "order" and "bom" entries).
        public static string BackendType(NotebookEntry e)
        {
            switch (e?.Tool)
            {
                case "measure": case "level": case "ladder": return "measurement";
                case "note": return "note";
                case "part": case "array": return string.IsNullOrEmpty(e.PartId) ? e.Tool : "placement";
                default: return e?.Tool ?? "note";
            }
        }

        /// The backend's measurement tool: tape (a distance), area (a shape), level, ladder.
        public static string BackendTool(NotebookEntry e)
        {
            switch (e?.Tool)
            {
                case "measure": return e.Unit == "m²" ? "area" : "tape";
                default: return e?.Tool ?? "tape";
            }
        }

        /// ISO 8601 in UTC with an explicit offset ("2026-09-26T14:03:07.120+00:00"), as the server's receipts write it.
        public static string Timestamp(DateTime t)
        {
            var u = t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
            return u.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", C) + "+00:00";
        }

        /// {"type":"scan_loaded","ts":…,"scan":site,"revision":n}: when a scan loads (the booth wall's "fastest scan to
        /// cart" starts at the session's first timestamp). The site goes in `scan`, not `site`: the report takes its
        /// scene from the first entry carrying `site`, which must stay a reading's.
        public static string ScanLoadedJson(DateTime when, string site, int revision) =>
            "{\"type\":\"scan_loaded\",\"ts\":" + Json(Timestamp(when)) + ",\"scan\":" + Json(site) + ",\"revision\":" + revision.ToString(C) + "}";

        /// Writes notebook-<stamp>.csv and .html into dir. Returns both paths.
        public static (string csvPath, string htmlPath) Export(IReadOnlyList<NotebookEntry> entries, Func<int, Texture2D> thumbnailFor, string dir, string title)
        {
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", C);
            string csv = Path.Combine(dir, $"notebook-{stamp}.csv");
            string html = Path.Combine(dir, $"notebook-{stamp}.html");
            File.WriteAllText(csv, ToCsv(entries), new UTF8Encoding(false));
            File.WriteAllText(html, ToHtml(entries, thumbnailFor, title), new UTF8Encoding(false));
            return (csv, html);
        }

        /// JPG bytes of any texture, readable or not (GPU blit + readback).
        public static byte[] EncodeJpg(Texture2D tex, int quality = 80)
        {
            if (tex == null) return null;
            if (tex.isReadable) return tex.EncodeToJPG(quality);
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGB24, false);
                copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                copy.Apply();
                var bytes = copy.EncodeToJPG(quality);
                UnityEngine.Object.DestroyImmediate(copy);
                return bytes;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        static string P(Vector3 p) => $"{F(p.x)} {F(p.y)} {F(p.z)}";
        static string F(float v) => v.ToString("0.0000", C);
        static string D(double v, string format) => v.ToString(format, C);   // placement

        static string Csv(string s)
        {
            s ??= "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        static string Html(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var ch in s ?? "")
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
