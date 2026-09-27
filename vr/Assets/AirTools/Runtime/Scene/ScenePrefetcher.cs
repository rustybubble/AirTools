using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AirTools.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Scene
{
    /// scalemodels: downloads every model the laptop lists to the headset in the background, so Model view still offers
    /// them when the laptop is away. Packages are 15–23 MB each, about 110 MB for the six scans.
    /// - **One at a time, low priority.** One package, one file at a time, in the switcher's order (PrefetchPlan).
    /// - **Never while a load runs.** A load (a tap in Model view, Next scene, a new revision) stops the file in flight,
    ///   and the prefetch waits `settleSeconds` after the load before it goes on. No new file starts while the agent is
    ///   answering (AgentClient.Busy), so a spoken request never queues behind a 20 MB mesh.
    /// - **Resumable per file.** Each file is written straight to disk (DownloadHandlerFile: no 20 MB buffer, no
    ///   main-thread write) as `.part` and renamed when complete. Files already cached are skipped.
    /// - **scene.json last.** A site counts as on the headset only once every file is in.
    /// - **Never blocking.** Coroutines on our own clock (HttpDeadline); a failed site is tried again after
    ///   `retrySeconds`.
    /// Settings shows "Models on the headset: 5 of 7" (SceneStreamer.HeadsetCount); the log has one line per package.
    public class ScenePrefetcher : MonoBehaviour
    {
        public SceneStreamer streamer;
        [Tooltip("Download the laptop's models to the headset in the background.")]
        public bool prefetch = true;
        [Tooltip("Seconds after start, and after any load, before downloading.")]
        public float settleSeconds = 15f;
        [Tooltip("Ask the laptop for its models this often while the prefetch is idle (s).")]
        public float listSeconds = 60f;
        [Tooltip("A site whose download failed is tried again after this long (s).")]
        public float retrySeconds = 120f;
        [Tooltip("A file download's total limit (s); bytes must also keep arriving (HttpDeadline.DownloadIdle).")]
        public int fileTimeoutSeconds = 900;

        /// The site downloading now (null: none), its file, and that file's progress (0–1).
        public string Current { get; private set; }
        public string CurrentFile { get; private set; }
        public float FileProgress { get; private set; }
        /// Packages completed this session, and the bytes downloaded.
        public int Completed { get; private set; }
        public long Bytes { get; private set; }
        /// "idle" · "waiting for the laptop" · "downloading" · "paused for a load" · "done".
        public string State { get; private set; } = "idle";
        public string LastResult { get; private set; } = "";
        public IReadOnlyDictionary<string, float> FailedAt => m_FailedAt;

        readonly Dictionary<string, float> m_FailedAt = new Dictionary<string, float>();
        float m_Resume, m_NextList, m_NextPlan;
        bool m_Busy;
        static float Now => Time.realtimeSinceStartup;

        void OnEnable() { Services.Register(this); m_Resume = Mathf.Max(m_Resume, Now + settleSeconds); }
        void OnDisable() { Services.Unregister(this); m_Busy = false; Current = CurrentFile = null; }

        bool Stop => streamer == null || !prefetch || !isActiveAndEnabled || streamer.Loading;

        void Update()
        {
            if (!Application.isPlaying) return;
            if (streamer == null && !Services.TryGet(out streamer)) return;
            if (!prefetch) { State = "off"; return; }
            float now = Now;
            if (streamer.Loading)
            {
                m_Resume = now + settleSeconds;
                if (m_Busy) State = "paused for a load";
                return;
            }
            if (m_Busy || now < m_Resume || AgentBusy) return;
            if (now >= m_NextList)
            {
                m_NextList = now + listSeconds;
                m_NextPlan = now + HttpDeadline.SmallIdle + 1f;   // plan once it has answered (or given up)
                streamer.ListSites(null);
                return;
            }
            if (now < m_NextPlan) return;
            m_NextPlan = now + 5f;   // plans (a few small lists) at most every 5 s
            if (!streamer.ServerListed) { State = "waiting for the laptop"; return; }
            var order = PrefetchPlan.Order(streamer.Sites, OnHeadsetAt);
            string site = PrefetchPlan.Next(order, streamer.Loading, streamer.LoadingSite, m_FailedAt, now, retrySeconds);
            if (site == null) { State = order.Count == 0 ? "done" : "waiting to retry"; return; }
            StartCoroutine(Run(site));
        }

        static bool AgentBusy => Services.TryGet<AirTools.Agent.AgentClient>(out var agent) && agent.Busy;

        /// That revision (or a newer one) of the site is on the headset.
        bool OnHeadsetAt(string site, int revision) => streamer.OnHeadset(site, out int have) && have >= revision;

        static string Url(string site, string file) => $"{ServerConfig.Current}/scenes/{site}/{file}";

        IEnumerator Run(string site)
        {
            m_Busy = true;
            Current = site;
            State = "downloading";
            float t0 = Now;
            long bytes0 = Bytes;
            int files = 0;
            string root = SceneStreamer.CacheDirectory;
            try
            {
                // 1. scene.json (kept in memory: it is written last).
                string text = null, why = null;
                using (var req = UnityWebRequest.Get(Url(site, SceneCache.ManifestFile)))
                {
                    req.timeout = 5;
                    yield return HttpDeadline.Send(req, HttpDeadline.SmallIdle, "GET scene.json (prefetch)", abandon: () => Stop);
                    if (Stop) { Paused(site); yield break; }
                    if (HttpDeadline.Ok(req)) text = req.downloadHandler.text;
                    else why = HttpDeadline.Error(req);
                }
                if (text == null) { Failed(site, $"scene.json: {why}"); yield break; }
                SceneManifest m;
                try { m = SceneManifest.Parse(text); }
                catch (Exception ex) { Failed(site, $"scene.json: {ex.Message}"); yield break; }

                // 2. The parts file first: it names the split mesh, collision and cavities.
                bool ok = true, paused = false;
                ScenePartsDoc doc = null;
                if (m.HasParts)
                {
                    if (!SceneCache.FileCached(root, site, m.PartsFile, m.revision))
                    {
                        yield return Download(root, site, m.PartsFile, (r, p) => { ok = r; paused = p; });
                        if (paused) { Paused(site); yield break; }
                        if (ok) files++;
                    }
                    doc = SceneCache.CachedParts(root, site, m);   // null: the package goes in one piece, as the load would take it
                }

                // 3. Every file of the package that isn't on the headset yet (resume: the cached ones are skipped).
                var missing = SceneCache.Missing(SceneCache.PackageFiles(m, doc), f => SceneCache.FileCached(root, site, f, m.revision));
                foreach (var file in missing)
                {
                    float waitUntil = Now + 30f;   // the agent answers first (bounded: a stuck Busy can't stall the prefetch)
                    while (AgentBusy && Now < waitUntil && !Stop) yield return null;
                    if (Stop) { Paused(site); yield break; }
                    yield return Download(root, site, file, (r, p) => { ok = r; paused = p; });
                    if (paused) { Paused(site); yield break; }
                    if (!ok) { Failed(site, $"{file}: {LastResult}"); yield break; }
                    files++;
                }

                // 4. scene.json last: the site is on the headset.
                if (!SceneCache.WriteAtomic(SceneCache.PathOf(root, site, SceneCache.ManifestFile), System.Text.Encoding.UTF8.GetBytes(text)))
                { Failed(site, "couldn't write scene.json"); yield break; }
                streamer.Remember(site, m.revision);
                m_FailedAt.Remove(site);
                Completed++;
                LastResult = $"{site} r{m.revision} on the headset ({files} file{(files == 1 ? "" : "s")}, {Mb(Bytes - bytes0)}, {Now - t0:0} s)";
                streamer.HeadsetCount(out int on, out int total);
                Log.Info($"Prefetch: {LastResult} · {PrefetchPlan.CountText(on, total)}");
                // 5. Its picture for the switcher card (kept on the headset when it lands).
                streamer.SiteThumbnail(site);
            }
            finally
            {
                m_Busy = false;
                Current = CurrentFile = null;
                FileProgress = 0f;
                if (State == "downloading") State = "idle";
            }
        }

        /// One file straight to disk: `<file>.part`, renamed when complete. done(ok, paused).
        IEnumerator Download(string root, string site, string file, Action<bool, bool> done)
        {
            string path = SceneCache.PathOf(root, site, file), tmp = path + ".part";
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); }
            catch (Exception ex) { LastResult = ex.Message; done(false, false); yield break; }
            CurrentFile = file;
            FileProgress = 0f;
            using var req = new UnityWebRequest(Url(site, file), UnityWebRequest.kHttpVerbGET, new DownloadHandlerFile(tmp) { removeFileOnAbort = true }, null);
            req.timeout = fileTimeoutSeconds;
            yield return HttpDeadline.Send(req, HttpDeadline.DownloadIdle, $"GET {site}/{file} (prefetch)", abandon: () => Stop,
                onProgress: () => FileProgress = req.downloadProgress);
            if (Stop)
            {
                Discard(tmp);
                done(false, true);
                yield break;
            }
            if (!HttpDeadline.Ok(req))
            {
                LastResult = $"{HttpDeadline.Error(req)} ({req.responseCode})";
                Discard(tmp);
                done(false, false);
                yield break;
            }
            try
            {
                SceneCache.Promote(tmp, path);
                Bytes += (long)req.downloadedBytes;
                done(true, false);
            }
            catch (Exception ex)
            {
                LastResult = $"couldn't keep {file}: {ex.Message}";
                Discard(tmp);
                done(false, false);
            }
        }

        static void Discard(string tmp)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
        }

        void Paused(string site)
        {
            State = "paused for a load";
            Log.Info($"Prefetch: {site} paused for a load (files already on the headset are kept)");
        }

        void Failed(string site, string why)
        {
            m_FailedAt[site] = Now;
            LastResult = $"{site} failed: {why}";
            State = "idle";
            Log.Warn($"Prefetch: {LastResult} · tried again in {retrySeconds:0} s");
        }

        static string Mb(long bytes) => $"{bytes / (1024f * 1024f):0.0} MB";

        /// One line for the harness: the state, the site and file in flight, what's done, the headset count.
        public string Describe()
        {
            int on = 0, total = 0;
            if (streamer != null) streamer.HeadsetCount(out on, out total);
            return $"prefetch={State}{(Current != null ? $" {Current}/{CurrentFile} {FileProgress * 100f:0}%" : "")} completed={Completed} " +
                   $"{Mb(Bytes)} failed=[{string.Join(",", m_FailedAt.Keys)}] {PrefetchPlan.CountText(on, total)} last=\"{LastResult}\"";
        }
    }
}
