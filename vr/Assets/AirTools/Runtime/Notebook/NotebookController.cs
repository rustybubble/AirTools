using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AirTools.Core;
using AirTools.Scene;
using AirTools.Tools;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Notes
{
    /// Notebook operations used by the wrist panel, voice (AppCommands) and the agent harness:
    /// highlight an entry's points, export (CSV + HTML to persistentDataPath, then POST /notebook), add a text note.
    /// Every entry is stamped with the scene it was taken on (site, quality, the photo's camera id) and sent to the
    /// session's notebook on the server (append-only: only what it hasn't had) before each agent / voice command and on
    /// export, so the site report, job packet and booth wall see it. A scan loading adds {"type":"scan_loaded","ts"}.
    public class NotebookController : MonoBehaviour
    {
        public SceneRoot sceneRoot;
        [Tooltip("Send new entries to the server's session notebook before each agent command (the report, packet and booth wall read it).")]
        public bool syncWithServer = true;

        public string LastExportCsv { get; private set; }
        public string LastExportHtml { get; private set; }
        /// "idle" | "exporting" | "saved (server offline)" | "uploaded" | "error: …"
        public string ExportStatus { get; private set; } = "idle";
        public event Action<string> ExportStatusChanged;
        /// Successful POST /notebook calls, and the last body sent (the harness checks them).
        public int Uploads { get; private set; }
        public string LastUploadBody { get; private set; }

        SceneRoot Root => sceneRoot != null ? sceneRoot : Services.Get<SceneRoot>();

        void OnEnable()
        {
            Services.Register(this);
            Notebook.Added += Stamp;
            Notebook.Updated += Stamp;
        }

        void OnDisable()
        {
            Services.Unregister(this);
            Notebook.Added -= Stamp;
            Notebook.Updated -= Stamp;
            if (m_Streamer != null) m_Streamer.Loaded -= OnScanLoaded;
            m_Streamer = null;
        }

        SceneStreamer m_Streamer;

        void Start() => HookStreamer();

        void HookStreamer()
        {
            if (m_Streamer != null || !Services.TryGet(out m_Streamer)) return;
            m_Streamer.Loaded += OnScanLoaded;
        }

        public bool Highlight(NotebookEntry e)
        {
            if (e == null) return false;
            bool ok = Services.Get<MeasureTool>()?.Highlight(e) ?? false;
            ok |= Services.Get<LevelTool>()?.Highlight(e) ?? false;
            ok |= Services.Get<LadderTool>()?.Highlight(e) ?? false;   // P6/P7
            return ok;
        }

        public Texture2D ThumbnailFor(int cameraId)
        {
            var root = Root;
            var cams = root != null && root.Package != null ? root.Package.cameras : null;
            if (cams == null) return null;
            foreach (var c in cams)
                if (c.id == cameraId)
                    // Runtime packages load thumbnails on demand (null until the download lands; the panel refreshes).
                    return c.thumbnail != null ? c.thumbnail : Services.Get<SceneStreamer>()?.Thumbnail(c);
            return null;
        }

        // ---------------- scene stamp ----------------

        /// Which scene package an entry was taken on, that revision's quality, and its photo's camera id + thumb (the
        /// package's own id, "0042"). An entry taken on another site keeps its first stamp.
        void Stamp(NotebookEntry e)
        {
            if (e == null) return;
            var root = Root;
            if (root == null || !root.IsRuntimePackage || string.IsNullOrEmpty(root.Site)) return;
            if (e.Site != null && e.Site != root.Site) return;
            if (e.SiteKey != null && e.SiteKey != root.Site) return;   // sitescope: a built-in row updated on a scan stays unstamped
            if (root.Package == null && e.Site == root.Site) return;   // sitescope: mid-load (no cameras yet): keep its photo keys
            e.Site = root.Site;
            if (e.Quality == null && root.Manifest != null) e.Quality = root.Manifest.quality;
            e.CameraKey = null; e.ThumbPath = null;
            if (e.NearestCameraId < 0 || root.Package == null || root.Package.cameras == null) return;
            foreach (var c in root.Package.cameras)
                if (c.id == e.NearestCameraId) { e.CameraKey = c.key; e.ThumbPath = c.thumbPath; break; }
        }

        // ---------------- scan_loaded ----------------

        string m_LastScan;
        readonly List<string> m_PendingRaw = new List<string>();

        void OnScanLoaded(SceneManifest m)
        {
            var site = m_Streamer != null ? m_Streamer.Site : Root?.Site;
            if (m == null || string.IsNullOrEmpty(site)) return;
            string key = $"{site}#{m.revision}";
            if (key == m_LastScan) return;
            m_LastScan = key;
            LogScanLoaded(site, m.revision, DateTime.UtcNow);
        }

        /// Queue {"type":"scan_loaded","ts"} for the session notebook (sent with the next flush).
        public void LogScanLoaded(string site, int revision, DateTime whenUtc)
        {
            m_PendingRaw.Add(NotebookExporter.ScanLoadedJson(whenUtc, site, revision));
            Log.Info($"Notebook: scan loaded ({site} r{revision}) at {NotebookExporter.Timestamp(whenUtc)}");
        }

        public int PendingRaw => m_PendingRaw.Count;

        // ---------------- server sync ----------------

        int m_UploadedThrough;
        bool m_Flushing;

        /// A flush in flight (AgentClient waits on it for at most a couple of seconds before its command goes).
        public sealed class FlushHandle
        {
            public bool Done { get; internal set; }
            public bool Ok { get; internal set; }
        }

        /// Entries the server hasn't had yet.
        public bool HasUnsent
        {
            get
            {
                NoticeRenumbering();
                if (m_PendingRaw.Count > 0) return true;
                var last = Notebook.Last;
                return last != null && last.Id > m_UploadedThrough;
            }
        }

        int m_Generation;

        /// Notebook.Clear() starts the ids at 1 again: everything after that is new to the server.
        void NoticeRenumbering()
        {
            if (m_Generation == Notebook.Generation) return;
            m_Generation = Notebook.Generation;
            m_UploadedThrough = 0;
        }

        /// POST /notebook with what the server hasn't had (append-only). Done at once when there's nothing to send,
        /// sync is off, or outside Play mode.
        public FlushHandle Flush()
        {
            var h = new FlushHandle();
            if (!syncWithServer || !Application.isPlaying || !isActiveAndEnabled || !HasUnsent) { h.Done = true; h.Ok = true; return h; }
            StartCoroutine(FlushRoutine(h));
            return h;
        }

        IEnumerator FlushRoutine(FlushHandle h)
        {
            while (m_Flushing) yield return null;   // one POST at a time: never send an entry twice
            NoticeRenumbering();
            var fresh = new List<NotebookEntry>();
            foreach (var e in Notebook.Entries) if (e.Id > m_UploadedThrough) fresh.Add(e);
            int raw = m_PendingRaw.Count;
            if (fresh.Count == 0 && raw == 0) { h.Ok = true; h.Done = true; yield break; }
            m_Flushing = true;
            int through = fresh.Count > 0 ? fresh[fresh.Count - 1].Id : m_UploadedThrough;
            bool ok = false;
            // fix-ux: the POST runs on our clock (HttpDeadline), so a hung laptop can't hold m_Flushing — and every later
            // flush, and each command's preflight — forever.
            yield return Upload(NotebookExporter.ToJson(fresh, SiteName(), SessionInfo.Id, m_PendingRaw.GetRange(0, raw)), success => ok = success);
            if (ok)
            {
                m_UploadedThrough = Math.Max(m_UploadedThrough, through);
                m_PendingRaw.RemoveRange(0, Math.Min(raw, m_PendingRaw.Count));
            }
            m_Flushing = false;
            h.Ok = ok; h.Done = true;
        }

        string SiteName()
        {
            var root = Root;
            return root != null && root.Package != null ? root.Package.siteName : "site";
        }

        /// Writes the files synchronously, then uploads in the background. Returns the directory written to.
        public string Export(bool upload = true)
        {
            SetStatus("exporting");
            try
            {
                string site = SiteName();
                string dir = Path.Combine(Application.persistentDataPath, "Notebook");
                var (csv, html) = NotebookExporter.Export(Notebook.Entries, ThumbnailFor, dir, $"Site report · {AirTools.UI.Copy.SiteName(site)} · {DateTime.Now.ToString("ddd, MMM d", System.Globalization.CultureInfo.InvariantCulture)}");
                LastExportCsv = csv; LastExportHtml = html;
                Log.Info($"Notebook exported: {csv} | {html}");
                if (upload)
                {
                    // The server appends: send only what it hasn't had yet.
                    if (!HasUnsent) { SetStatus("up to date"); return dir; }
                    if (!Application.isPlaying || !isActiveAndEnabled) { SetStatus("saved"); return dir; }
                    var h = new FlushHandle();
                    StartCoroutine(ExportUpload(h));
                }
                else SetStatus("saved");
                return dir;
            }
            catch (Exception ex)
            {
                SetStatus($"error: {ex.Message}");
                Log.Error($"Notebook export failed: {ex}");
                return null;
            }
        }

        IEnumerator ExportUpload(FlushHandle h)
        {
            yield return FlushRoutine(h);
            // Offline is fine: the files are on the headset. Not an error.
            SetStatus(h.Ok ? "uploaded" : "saved (server offline)");
        }

        IEnumerator Upload(string json, Action<bool> done)
        {
            string url = ServerConfig.Current + "/notebook";
            LastUploadBody = json;
            var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Services.TryGet<ServerConfig>(out var cfg) ? cfg.timeoutSeconds : 5,
            };
            req.SetRequestHeader("Content-Type", "application/json");
            using (req)
            {
                yield return HttpDeadline.Send(req, HttpDeadline.SmallIdle, "POST /notebook");
                bool ok = HttpDeadline.Ok(req);
                if (ok)
                {
                    Uploads++;
                    Log.Info($"Notebook POST {url} → {Clip(req.downloadHandler.text)}");
                }
                else Log.Info($"Notebook POST {url} failed ({HttpDeadline.Error(req)}); kept local files");   // offline is fine (a hang also Warns, from HttpDeadline)
                done?.Invoke(ok);
            }
        }

        static string Clip(string s) => string.IsNullOrEmpty(s) || s.Length <= 300 ? s : s.Substring(0, 300) + "…";

        public NotebookEntry AddNote(string text) => AddNote(text, false);

        /// A text note; `share` puts it in the job packet (the packet leaves private notes out).
        public NotebookEntry AddNote(string text, bool share, string pinId = null, string frameId = null)
        {
            var e = new NotebookEntry("note", 0, "", Array.Empty<Vector3>(), DateTime.Now, -1, text ?? "") { Share = share, PinId = pinId, FrameId = frameId };
            Notebook.Add(e);
            return e;
        }

        void SetStatus(string s)
        {
            ExportStatus = s;
            ExportStatusChanged?.Invoke(s);
        }
    }
}
