using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AirTools.Core;
using GLTFast;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Scene
{
    /// Loads scene packages from the laptop server (backend docs/api.md "GET /scenes", plan §1a) at runtime:
    /// GET /scenes → pick a site → GET scene.json (the only file polled, ETag / 304) → the files it names
    /// (collision, structure, cameras, mesh) → glTFast → under the SceneRoot. Polls scene.json every ~5 s; when the
    /// revision goes up the new collider + structure go in first and the visual one frame later, in the same frame
    /// (no jump: measurements, pins and parts stay put). Revision-named files are immutable, so they are cached on the
    /// headset and the last good package loads even when the laptop is unreachable. With no server and no cache the
    /// built-in scene (AppBootstrap's package) stays.
    /// Scene parts (docs/api.md parts.r&lt;rev&gt;.json): when scene.json has a `parts` entry the split mesh and collision
    /// (mesh.parts / collision.parts, one node per removable component) load instead of mesh.r / collision.r, plus the
    /// cavities (cavity.r&lt;rev&gt;.glb, hidden until their part is taken out), and SceneParts gets the id → node lookup.
    /// Without the entry, or when a parts file is missing or unreadable, the scene loads in one piece as before.
    /// fix-ux: a load logs one line per step (scene.json, each file, each import, the swap) and can't stick at "loading":
    /// Loading is derived from a SceneLoadWatchdog (plain fields, not the coroutine's finally, which Unity skips when a
    /// coroutine is stopped); no progress for 20 s, 60 s of work, a 15 s import / instantiate or the streamer being
    /// disabled all give up with a Warn naming the stage, LoadFailed + a Retry (the heads-up line and Settings say
    /// "Couldn't load the kitchen · Retry"), and a stale coroutine finishing later is ignored.
    public class SceneStreamer : MonoBehaviour
    {
        public SceneRoot sceneRoot;
        [Tooltip("OVRCameraRig root; placed at the package's recommended spawn on the first load of a site.")]
        public Transform rig;
        [Tooltip("Material for the textured scene mesh (URP/Unlit): photogrammetry has its lighting baked in.")]
        public Material visualMaterial;
        [Tooltip("Site to load at start ('' = none: keep the built-in scene until asked).")]
        public string startSite = "kitchen";
        public float pollSeconds = 5f;
        [Tooltip("Eye height assumed by the package's recommended_spawn.pos (plan: lowest mesh point + 1.6 m).")]
        public float spawnEyeHeight = 1.6f;
        [Tooltip("Step in from the recommended spawn until the scene spans ~90° of view, but never closer than this (m).")]
        public float minStandOff = 1.2f;
        [Tooltip("Sites to hide from the picker (outdated test packages).")]
        public string[] ignoreSites = { "strasbourg-cathedral-spire" };
        // scalemodels
        [Tooltip("Per-site default scale (SiteScales): a site listed here loads at this scale until a person sets its scale on this headset (a tape + Set scale). Reset goes back to it.")]
        public SiteScale[] siteScales = SiteScales.Defaults();

        public string Site { get; private set; }
        public SceneManifest Manifest { get; private set; }
        public List<SceneCameraJson> Cameras { get; private set; } = new List<SceneCameraJson>();
        public List<SceneListing> Sites { get; private set; } = new List<SceneListing>();
        [Header("Watchdog (fix-ux)")]
        [Tooltip("A load that makes no progress this long gives up (s); the heads-up line offers Retry.")]
        public float loadStallSeconds = 20f;
        [Tooltip("…or that works longer than this, downloads not counted (s).")]
        public float loadTotalSeconds = 60f;
        [Tooltip("The glTF imports (all files together) and each instantiate give up after this long (s).")]
        public float importTimeoutSeconds = 15f;

        readonly SceneLoadWatchdog m_Load = new SceneLoadWatchdog();
        static float Now => Time.realtimeSinceStartup;
        /// Our own deadline for the small requests (scene.json, /scenes): their 5 s timeout + 1 s (HttpDeadline). On the
        /// headset (2026-09-26, three sessions) no request ever answered or failed — not this one, not the presenter
        /// link's 3 s poll, not a parts search — so our clock aborts a hung request and the load goes on from the cache.
        public const float RequestDeadline = HttpDeadline.SmallIdle;
        /// A download that receives no bytes this long is aborted (its cached copy, if any, is used).
        public const float DownloadStallSeconds = HttpDeadline.DownloadIdle;
        /// A poll in flight blocks the next until this time (a stopped poll coroutine expires by the clock).
        float m_PollBusyUntil;

        /// A load is running and hasn't timed out (derived from the watchdog, so it can't stick at true).
        public bool Loading => (m_Load.Active && !m_Load.TimedOut(Now)) || WaitingForServer;
        /// The first load waits (≤ serverWaitSeconds) for ServerConfig's start-up probe to find the laptop.
        public bool WaitingForServer => Now < m_WaitForServerUntil && ServerConfig.Probing;
        [Tooltip("The first load waits at most this long for ServerConfig's start-up probe (s).")]
        public float serverWaitSeconds = 4f;
        float m_WaitForServerUntil = -1f;
        string m_PendingSite;
        /// The site being loaded (or waiting for the server probe to load), else null.
        public string LoadingSite => m_Load.Active ? m_Load.Site : WaitingForServer ? m_PendingSite : null;
        /// The probe changed the server while a load ran: retry once if that load fails.
        bool m_RetryIfFails;
        /// The last load of FailedSite failed, timed out or was abandoned; cleared when a load starts or succeeds.
        public bool LoadFailed { get; private set; }
        public string FailedSite { get; private set; }
        /// The step the running (or last) load reached.
        public string LoadStage => m_Load.Stage;
        public SceneLoadWatchdog Watchdog => m_Load;
        /// The site this app loads at start (launch extra / server.txt "site", else startSite).
        public string StartSite { get { var o = ServerConfig.SiteOverride; return string.IsNullOrEmpty(o) ? startSite : o; } }
        public string Status { get; private set; } = "built-in scene";
        public string LastError { get; private set; }
        /// Revisions swapped in while running (not counting the first load).
        public int Swaps { get; private set; }
        public float LastLoadSeconds { get; private set; }

        public event Action<SceneManifest> Loaded;

        string m_ETag;
        float m_NextPoll;
        GameObject m_Visual, m_Collision, m_Cavities;
        readonly List<IDisposable> m_Owners = new List<IDisposable>();
        readonly Dictionary<string, Texture2D> m_Thumbs = new Dictionary<string, Texture2D>();
        readonly HashSet<string> m_ThumbsLoading = new HashSet<string>();
        /// JPEG bytes of thumbs downloaded for the loaded revision (context.frame_jpg_b64, /scene/ask frames): ~20 KB each.
        readonly Dictionary<string, byte[]> m_ThumbBytes = new Dictionary<string, byte[]>();
        readonly HashSet<string> m_BytesLoading = new HashSet<string>();
        ScenePackage m_Package;
        GameObject m_BuiltInContent;
        /// A load given up because the streamer was disabled: started again when it's enabled.
        string m_ResumeSite;
        bool m_Quitting;

        static string CacheRoot => Path.Combine(Application.persistentDataPath, "scenes");

        void OnEnable()
        {
            Services.Register(this);
            if (Application.isPlaying && GetComponent<SceneParts>() == null) gameObject.AddComponent<SceneParts>().sceneRoot = sceneRoot;
            // scalemodels: the background download of every listed model (the scene builder adds it; older scenes get it here).
            if (Application.isPlaying && GetComponent<ScenePrefetcher>() == null) gameObject.AddComponent<ScenePrefetcher>().streamer = this;
            if (Application.isPlaying && !string.IsNullOrEmpty(m_ResumeSite))
            {
                var site = m_ResumeSite;
                m_ResumeSite = null;
                Log.Info($"SceneStreamer: enabled again, loading {site} again");
                Load(site);
            }
        }

        void OnDisable()
        {
            Services.Unregister(this);
            // Unity stops this object's coroutines when it's deactivated, and never runs their finally blocks.
            if (m_Load.Active && !m_Quitting)
            {
                m_ResumeSite = m_Load.Site;
                GiveUp("the streamer was disabled mid-load");
            }
        }

        void OnApplicationQuit() => m_Quitting = true;

        /// The removable components of the loaded package (null before the first OnEnable in Play mode).
        public SceneParts Parts => GetComponent<SceneParts>();

        /// What the last load did with scene parts: "none", "split: 4 components (cavities)", or why it fell back.
        public string PartsStatus { get; private set; } = "none";

        void Start()
        {
            if (!Application.isPlaying) return;
            ServerConfig.Changed += OnServerChanged;
            LoadKnownSites();   // scalemodels: the last good listing and the cached packages, before the server answers
            var site = StartSite;
            if (string.IsNullOrEmpty(site)) return;
            if (ServerConfig.Probing) StartCoroutine(FirstLoad(site));
            else Load(site);
        }

        void OnDestroy() => ServerConfig.Changed -= OnServerChanged;

        /// fix-ux: wait (≤ serverWaitSeconds) for ServerConfig's start-up probe, so a launch from the Quest library asks
        /// the server that answers (e.g. :8004) instead of the silent default.
        IEnumerator FirstLoad(string site)
        {
            m_WaitForServerUntil = Now + serverWaitSeconds;
            m_PendingSite = site;
            Log.Info($"SceneStreamer: waiting ≤ {serverWaitSeconds:0} s for the server probe before loading {site}");
            while (WaitingForServer) yield return null;
            m_WaitForServerUntil = -1f;
            Load(site);
        }

        /// The probe found another server after a load had started: a load that failed tries again there.
        void OnServerChanged(string url)
        {
            if (m_Load.Active) { m_RetryIfFails = true; return; }
            if (LoadFailed) { Log.Info($"SceneStreamer: the server is now {url}; loading {FailedSite} again"); Retry(); }
        }

        void Update()
        {
            CheckWatchdog();
            if (m_RetryIfFails && !m_Load.Active)
            {
                m_RetryIfFails = false;
                if (LoadFailed) { Log.Info($"SceneStreamer: the server is now {BaseUrl}; loading {FailedSite} again"); Retry(); }
            }
            if (string.IsNullOrEmpty(Site) || Loading || pollSeconds <= 0f || Time.unscaledTime < m_NextPoll || Now < m_PollBusyUntil) return;
            m_NextPoll = Time.unscaledTime + pollSeconds;
            StartCoroutine(Poll());
        }

        static string BaseUrl => ServerConfig.Current;
        static string SiteUrl(string site, string file) => $"{BaseUrl}/scenes/{site}/{file}";

        // ---------------- listing ----------------

        public void ListSites(Action<List<SceneListing>> done) => StartCoroutine(ListRoutine(done));

        IEnumerator ListRoutine(Action<List<SceneListing>> done)
        {
            using var req = UnityWebRequest.Get($"{BaseUrl}/scenes");
            req.timeout = 5;
            yield return HttpDeadline.Send(req, RequestDeadline, "GET /scenes");
            var list = new List<SceneListing>();
            bool answered = false;   // scalemodels
            if (HttpDeadline.GaveUp(req) != null) LastError = $"GET /scenes: {HttpDeadline.GaveUp(req)}";
            else if (req.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    foreach (var s in SceneManifest.ParseAny<List<SceneListing>>(req.downloadHandler.text) ?? list)
                        if (s != null && Array.IndexOf(ignoreSites, s.site) < 0) list.Add(s);
                    answered = true;
                }
                catch (Exception ex) { LastError = $"GET /scenes: {ex.Message}"; }
            }
            else LastError = $"GET /scenes: {req.error}";
            Sites = list;
            OnListed(answered, list);   // scalemodels: keep it on the headset; offline, the switcher lists what's cached
            done?.Invoke(list);
        }

        // ---------------- load / swap ----------------

        /// Load a site (first load: spawn at its recommended spawn). Returns false if a load is already running (one
        /// that has timed out is given up first, so this always recovers).
        public bool Load(string site)
        {
            if (string.IsNullOrEmpty(site)) return false;
            if (m_Load.Active && !CheckWatchdog()) return false;
            if (!isActiveAndEnabled)
            {
                Log.Warn($"SceneStreamer: can't load {site}: the streamer is disabled");
                return false;
            }
            StartCoroutine(LoadRoutine(site, initial: site != Site));
            return true;
        }

        /// Load the site that failed (else the loaded one, else the start site) again: scene.json from the server, else
        /// the headset's cached package. False while a load is running.
        public bool Retry()
        {
            var site = !string.IsNullOrEmpty(FailedSite) ? FailedSite : !string.IsNullOrEmpty(Site) ? Site : StartSite;
            Log.Info($"SceneStreamer: retry {site}");
            return Load(site);
        }

        /// Back to the scene baked into the app (the synthetic facade). A load in progress is abandoned (not a failure).
        public void LoadBuiltIn()
        {
            if (m_Load.Active)
            {
                var site = m_Load.Site;
                m_Load.Abort();
                Log.Info($"SceneStreamer: {site} load abandoned for the built-in scene");
            }
            RestoreBuiltIn();
            LoadFailed = false; FailedSite = null;
            Log.Info("SceneStreamer: back to the built-in scene");
        }

        void RestoreBuiltIn()
        {
            var boot = FindAnyObjectByType<AppBootstrap>();
            ClearRuntime();
            Site = null; Manifest = null; m_ETag = null;
            ScaleSource = ScaleSource.None;   // scalemodels: the built-in facade is built to size
            Cameras = new List<SceneCameraJson>();
            if (boot != null && boot.scenePackage != null && sceneRoot != null)
            {
                bool defer = DefersSpawn(out var table);   // modelview: in Model view the rig stays put
                SceneLoader.Load(boot.scenePackage, sceneRoot, defer ? null : rig);
                if (defer)
                {
                    if (SceneLoader.SpawnLocal(boot.scenePackage, out var feet, out float yaw)) table.SetSpawn(feet, yaw, ModelSites.BuiltIn);
                    else table.ClearSpawn();
                }
                // Home is the built-in scene's spawn now, not the last server site's (Home / a reset put you in the kitchen's spot).
                else if (rig != null && Services.TryGet<AirTools.Input.Locomotion>(out var loco)) loco.SetHome(new Pose(rig.position, rig.rotation));
                sceneRoot.SetVisible(AppState.Mode != AppMode.Passthrough);
            }
            Status = "built-in scene";
        }

        /// Give up on a load that has timed out (Update calls it every frame; Load before it refuses). True if it gave up.
        public bool CheckWatchdog()
        {
            if (!m_Load.Active || !m_Load.TimedOut(Now)) return false;
            GiveUp($"timed out at {m_Load.Stage} ({m_Load.Why(Now)})");
            return true;
        }

        /// Abandon the running load (timed out, the streamer disabled): Loading goes false now, the failure shows with a
        /// Retry, and the coroutine — if it's still alive — is ignored from here on (its generation is stale).
        void GiveUp(string why)
        {
            string site = m_Load.Site, stage = m_Load.Stage;
            float took = Now - m_Load.StartedAt;
            if (!m_Load.Abort()) return;
            MarkFailed(site, why);
            Log.Warn($"SceneStreamer: {site} load gave up after {took:0.0} s at '{stage}': {why} · Retry loads it again (the cached package if the laptop is away)");
            m_NextPoll = Time.unscaledTime + pollSeconds;
        }

        void MarkFailed(string site, string why)
        {
            LastError = $"{site}: {why}";
            Status = $"couldn't load {site}: {why}";
            LoadFailed = true;
            FailedSite = site;
        }

        /// A failure the load handled itself: the status (Warn, or Error for a bad package), the Retry state, done.
        void Fail(int gen, string site, string status, bool error = false)
        {
            if (!m_Load.IsCurrent(gen)) return;
            LastError ??= status;
            LoadFailed = true;
            FailedSite = site;
            Status = status;
            if (error) Log.Error($"SceneStreamer: {status}");
            else Log.Warn($"SceneStreamer: {status}");
            m_Load.End(gen);
        }

        /// One log line per step of the current load, with the time since it started; false for a stale load.
        bool Step(int gen, string stage, string detail = null)
        {
            if (!m_Load.Step(gen, stage, Now)) return false;
            Log.Info($"SceneStreamer: {m_Load.Site} · {stage}{(detail != null ? " · " + detail : "")} (+{Now - m_Load.StartedAt:0.00} s)");
            return true;
        }

        static string Size(long bytes) => bytes >= 1 << 20 ? $"{bytes / (1024f * 1024f):0.0} MB" : $"{bytes / 1024f:0} KB";

        IEnumerator Poll()
        {
            string site = Site;
            string etag = m_ETag;
            using var req = UnityWebRequest.Get(SiteUrl(site, "scene.json"));
            req.timeout = 5;
            if (!string.IsNullOrEmpty(etag)) req.SetRequestHeader("If-None-Match", etag);
            m_PollBusyUntil = Now + RequestDeadline + 1f;
            yield return HttpDeadline.Send(req, RequestDeadline, "GET scene.json (poll)");
            m_PollBusyUntil = 0f;
            if (HttpDeadline.GaveUp(req) != null) yield break;   // hung: try again at the next poll
            if (site != Site || Loading) yield break;
            if (req.responseCode == 304) yield break;
            if (req.result != UnityWebRequest.Result.Success) yield break;   // laptop away: keep what we have
            SceneManifest m;
            try { m = SceneManifest.Parse(req.downloadHandler.text); }
            catch (Exception ex) { LastError = $"scene.json: {ex.Message}"; yield break; }
            m_ETag = req.GetResponseHeader("ETag") ?? m_ETag;
            if (Manifest != null && m.revision <= Manifest.revision) yield break;
            Log.Info($"SceneStreamer: {site} revision {Manifest?.revision} → {m.revision} ({m.quality})");
            yield return LoadRoutine(site, initial: false, manifest: m, manifestText: req.downloadHandler.text);
        }

        IEnumerator LoadRoutine(string site, bool initial, SceneManifest manifest = null, string manifestText = null)
        {
            m_Load.StallSeconds = loadStallSeconds;
            m_Load.TotalSeconds = loadTotalSeconds;
            int gen = m_Load.Begin(site, Now);
            LastError = null;
            LoadFailed = false; FailedSite = null;
            float t0 = Now;
            Status = $"loading {site}…";
            Log.Info($"SceneStreamer: loading {site} from {BaseUrl}{(manifest != null ? $" (r{manifest.revision}, from the poll)" : "")} · " +
                     $"{(File.Exists(CachePath(site, "scene.json")) ? "a cached package is on the headset" : "nothing cached")}{(initial ? " · first load" : "")}");
            try
            {
                // 1. scene.json (server, else the last good copy on the headset).
                if (manifest == null)
                {
                    string text = null;
                    using (var req = UnityWebRequest.Get(SiteUrl(site, "scene.json")))
                    {
                        req.timeout = 5;
                        yield return HttpDeadline.Send(req, RequestDeadline, "GET scene.json", abandon: () => !m_Load.IsCurrent(gen));
                        if (!m_Load.IsCurrent(gen)) yield break;
                        if (HttpDeadline.GaveUp(req) != null) LastError = $"scene.json: {HttpDeadline.GaveUp(req)} ({req.url})";
                        else if (req.result == UnityWebRequest.Result.Success)
                        {
                            text = req.downloadHandler.text;
                            m_ETag = req.GetResponseHeader("ETag");
                            Step(gen, "scene.json", $"from the server (HTTP {req.responseCode}, {Size(text.Length)})");
                        }
                        else LastError = $"scene.json: {req.error} ({req.url})";
                    }
                    if (text == null)
                    {
                        text = ReadCache(site, "scene.json");
                        if (text == null) { Fail(gen, site, $"{site} unavailable: {LastError}"); yield break; }
                        Log.Warn($"SceneStreamer: server unreachable ({LastError}), loading the cached {site} package");
                        Step(gen, "scene.json", "from the headset's cache");
                    }
                    try { manifest = SceneManifest.Parse(text); }
                    catch (Exception ex) { LastError = $"scene.json: {ex.Message}"; Fail(gen, site, $"{site}: {LastError}", error: true); yield break; }
                    manifestText = text;
                }
                else Step(gen, "scene.json", $"r{manifest.revision} from the poll");

                // 2. Files (cached by name: revision-named files never change). A revision republished with new content
                // (a pipeline re-run that reused the number) must not come back from the headset's cache: every file of
                // this load is then fetched again, so the package loads as one revision, never a mix.
                bool refetch = IsRepublished(ReadCache(site, "scene.json"), manifestText, manifest);
                if (refetch) Log.Warn($"SceneStreamer: {site} r{manifest.revision} was republished with new content; not using the cached files");
                byte[] collisionBytes = null, meshBytes = null, camerasBytes = null, structureBytes = null, cavityBytes = null;
                string meshFile = manifest.MeshFile, collisionFile = manifest.CollisionFile, cavityFile = null;

                // 2a. Scene parts: the split mesh and collision replace the one-piece files (never both downloaded).
                ScenePartsDoc partsDoc = null;
                PartsStatus = "none";
                if (manifest.HasParts)
                {
                    byte[] partsBytes = null;
                    yield return Fetch(site, manifest.PartsFile, b => partsBytes = b, refetch, gen);
                    if (!m_Load.IsCurrent(gen)) yield break;
                    if (partsBytes == null) PartsStatus = $"{manifest.PartsFile} unavailable ({LastError}): one piece";
                    else
                    {
                        try { partsDoc = ScenePartsDoc.Parse(System.Text.Encoding.UTF8.GetString(partsBytes)); }
                        catch (Exception ex) { PartsStatus = $"{manifest.PartsFile} unreadable ({ex.Message}): one piece"; }
                    }
                    if (partsDoc != null)
                    {
                        var files = ScenePartsFiles.For(manifest, partsDoc);
                        byte[] splitMesh = null, splitCollision = null;
                        if (files.Mesh != null) yield return Fetch(site, files.Mesh, b => splitMesh = b, refetch, gen);
                        if (files.Collision != null) yield return Fetch(site, files.Collision, b => splitCollision = b, refetch, gen);
                        if (!m_Load.IsCurrent(gen)) yield break;
                        if (splitMesh == null || (files.Collision != null && splitCollision == null))
                        {
                            PartsStatus = $"{(splitMesh == null ? files.Mesh ?? "no split mesh named" : files.Collision)} unavailable ({LastError}): one piece";
                            partsDoc = null;
                        }
                        else
                        {
                            meshBytes = splitMesh; collisionBytes = splitCollision;
                            meshFile = files.Mesh; collisionFile = files.Collision;
                            if (files.Cavities != null) yield return Fetch(site, cavityFile = files.Cavities, b => cavityBytes = b, refetch, gen);
                            if (!m_Load.IsCurrent(gen)) yield break;
                            PartsStatus = $"split: {partsDoc.Removable.Count} removable of {partsDoc.components.Count}" +
                                          (cavityBytes != null ? " (cavities)" : files.Cavities != null ? $" ({files.Cavities} unavailable: no cavities)" : " (no cavities)");
                        }
                    }
                    Log.Info($"SceneStreamer: {site} parts: {PartsStatus}");
                }
                if (partsDoc == null)
                {
                    yield return Fetch(site, manifest.CollisionFile, b => collisionBytes = b, refetch, gen);
                    yield return Fetch(site, manifest.MeshFile, b => meshBytes = b, refetch, gen);
                }
                if (!string.IsNullOrEmpty(manifest.cameras)) yield return Fetch(site, manifest.cameras, b => camerasBytes = b, refetch, gen);
                if (manifest.HasStructure) yield return Fetch(site, manifest.StructureFile, b => structureBytes = b, refetch, gen);
                if (!m_Load.IsCurrent(gen)) yield break;
                if (meshBytes == null) { Fail(gen, site, $"{site}: mesh {meshFile} unavailable ({LastError})", error: true); yield break; }

                StructureLayer structure = null;
                if (structureBytes != null)
                {
                    try { structure = StructureLayer.Parse(System.Text.Encoding.UTF8.GetString(structureBytes)); }
                    catch (Exception ex) { Log.Warn($"SceneStreamer: structure {manifest.StructureFile} unreadable, mesh-only snapping: {ex.Message}"); }
                }
                else if (manifest.HasStructure) Log.Warn($"SceneStreamer: {manifest.StructureFile} missing, mesh-only snapping");

                var cameras = new List<SceneCameraJson>();
                if (camerasBytes != null)
                {
                    try { cameras = SceneCameras.Parse(System.Text.Encoding.UTF8.GetString(camerasBytes)); }
                    catch (Exception ex) { Log.Warn($"SceneStreamer: cameras unreadable: {ex.Message}"); }
                }

                // 3. glTF (all parsed before anything is swapped, so a bad file never leaves a half scene). No await
                // here may outlive importTimeoutSeconds: a hung import gives up with the files still pending named.
                Step(gen, "import", $"{meshFile} {Size(meshBytes.Length)}" +
                                    (collisionBytes != null ? $" + {collisionFile} {Size(collisionBytes.Length)}" : "") +
                                    (cavityBytes != null ? $" + {cavityFile} {Size(cavityBytes.Length)}" : ""));
                var collisionTask = collisionBytes != null ? Import(collisionBytes, SiteUrl(site, collisionFile)) : Task.FromResult<GltfImport>(null);
                var meshTask = Import(meshBytes, SiteUrl(site, meshFile), visual: true);
                var cavityTask = cavityBytes != null ? Import(cavityBytes, SiteUrl(site, cavityFile)) : Task.FromResult<GltfImport>(null);
                float ti = Now;
                bool collisionSeen = collisionBytes == null, meshSeen = false, cavitySeen = cavityBytes == null;
                while (true)
                {
                    if (!m_Load.IsCurrent(gen)) { DisposeWhenDone(collisionTask); DisposeWhenDone(meshTask); DisposeWhenDone(cavityTask); yield break; }
                    if (!collisionSeen && collisionTask.IsCompleted) { collisionSeen = true; StepImport(gen, collisionFile, collisionTask, ti); }
                    if (!meshSeen && meshTask.IsCompleted) { meshSeen = true; StepImport(gen, meshFile, meshTask, ti); }
                    if (!cavitySeen && cavityTask.IsCompleted) { cavitySeen = true; StepImport(gen, cavityFile, cavityTask, ti); }
                    if (collisionTask.IsCompleted && meshTask.IsCompleted && cavityTask.IsCompleted) break;
                    if (Now - ti > importTimeoutSeconds)
                    {
                        string pending = string.Join(", ", new[] { collisionTask.IsCompleted ? null : collisionFile, meshTask.IsCompleted ? null : meshFile,
                                                                   cavityTask.IsCompleted ? null : cavityFile }.Where(f => f != null));
                        DisposeWhenDone(collisionTask); DisposeWhenDone(meshTask); DisposeWhenDone(cavityTask);
                        LastError = $"glTF import timed out after {importTimeoutSeconds:0} s ({pending} still importing)";
                        Fail(gen, site, $"{site}: {LastError}");
                        yield break;
                    }
                    yield return null;
                }
                GltfImport collisionGltf = ResultOf(collisionTask), meshGltf = ResultOf(meshTask), cavityGltf = ResultOf(cavityTask);
                if (meshGltf == null) { collisionGltf?.Dispose(); cavityGltf?.Dispose(); Fail(gen, site, $"{site}: {meshFile} failed to import", error: true); yield break; }
                if (cavityBytes != null && cavityGltf == null) Log.Warn($"SceneStreamer: {cavityFile} failed to import; parts come out without their cavities");

                if (!string.IsNullOrEmpty(manifestText)) WriteCache(site, "scene.json", manifestText);
                yield return Swap(gen, site, manifest, collisionGltf, meshGltf, structure, cameras, initial, partsDoc, cavityGltf);
                if (!m_Load.IsCurrent(gen)) yield break;   // gave up / failed during the swap (it said why)
                LastLoadSeconds = Now - t0;
                Status = $"{site} · {manifest.Describe()}";
                LoadFailed = false; FailedSite = null;
                Log.Info($"SceneStreamer: {Status} in {LastLoadSeconds:0.0} s (collision {(collisionGltf != null ? "mesh" : "= visual")}, {cameras.Count} cameras)");
                Remember(site, manifest.revision);   // scalemodels: it's on the headset now
                Loaded?.Invoke(manifest);
            }
            finally
            {
                // Only reached when the coroutine runs to an end (Unity skips it for a stopped one: the watchdog covers that).
                m_Load.End(gen);
                m_NextPoll = Time.unscaledTime + pollSeconds;
            }
        }

        /// Log an import that finished (and count it as progress).
        void StepImport(int gen, string file, Task<GltfImport> t, float since)
        {
            var g = ResultOf(t);
            string how = g != null ? "imported" : t.IsFaulted ? $"import threw {t.Exception?.GetBaseException().Message}" : "import failed";
            Step(gen, "import", $"{file} {how} in {Now - since:0.00} s");
        }

        static GltfImport ResultOf(Task<GltfImport> t) => t != null && t.Status == TaskStatus.RanToCompletion ? t.Result : null;

        /// A glTF import we no longer want (timed out, or its load was abandoned): disposed on the main thread once it ends.
        static void DisposeWhenDone(Task<GltfImport> t)
        {
            if (t == null) return;
            if (t.IsCompleted) { ResultOf(t)?.Dispose(); return; }
            var ctx = System.Threading.SynchronizationContext.Current;
            t.ContinueWith(done =>
            {
                var g = ResultOf(done);
                if (g == null) return;
                if (ctx != null) ctx.Post(_ => g.Dispose(), null);
                else g.Dispose();
            }, TaskScheduler.Default);
        }

        /// Wait for a glTFast instantiate, at most importTimeoutSeconds and only while this load is current.
        IEnumerator WaitInstantiate(int gen, Task<bool> task, Action<bool> done)
        {
            float t = Now;
            while (!task.IsCompleted && Now - t < importTimeoutSeconds && m_Load.IsCurrent(gen)) yield return null;
            if (!task.IsCompleted && m_Load.IsCurrent(gen)) Log.Warn($"SceneStreamer: instantiate timed out after {importTimeoutSeconds:0} s at '{m_Load.Stage}'");
            done(task.Status == TaskStatus.RanToCompletion && task.Result);
        }

        /// Swap the imported package in (collider + structure, then the visual a frame later, then the cavities). Every
        /// wait is bounded (importTimeoutSeconds) and checks the load is still current; a load that went stale bails
        /// out, disposing what it imported.
        IEnumerator Swap(int gen, string site, SceneManifest manifest, GltfImport collisionGltf, GltfImport meshGltf, StructureLayer structure,
            List<SceneCameraJson> cameras, bool initial, ScenePartsDoc partsDoc = null, GltfImport cavityGltf = null)
        {
            void DisposeImports() { collisionGltf?.Dispose(); meshGltf?.Dispose(); cavityGltf?.Dispose(); }
            bool fresh = initial || sceneRoot.Content == null || !sceneRoot.IsRuntimePackage || Site != site;
            // A newer revision swaps in place only when it says it's in the loaded one's frame (plan §1a: the full
            // revision rigid-aligned onto its preview). Anything else — Zabel r3, re-scaled onto the OSM footprint, is
            // not aligned to r2 — loads as a new scene: its own orientation, calibration, spawn and photos.
            bool unaligned = !fresh && !SwapsInPlace(Manifest, manifest);
            if (unaligned)
            {
                Log.Warn($"SceneStreamer: {site} r{manifest.revision} isn't aligned to r{Manifest?.revision}'s frame " +
                         $"(frame {manifest.FrameId}, aligned_to_preview={manifest.frame?.aligned_to_preview?.ToString() ?? "null"}); loading it as a new scene");
                fresh = true;
            }
            bool newRevision = Manifest == null || manifest.revision != Manifest.revision || Site != site;
            Step(gen, "swap collider", fresh ? "a new scene" : $"in place (r{Manifest?.revision} → r{manifest.revision})");
            if (fresh)
            {
                ClearRuntime();
                if (sceneRoot.Content != null) { m_BuiltInContent = sceneRoot.Content; m_BuiltInContent.SetActive(false); Destroy(m_BuiltInContent); }
                var content = new GameObject(string.IsNullOrEmpty(manifest.name) ? site : manifest.name);
                content.transform.SetParent(sceneRoot.transform, false);
                content.transform.localRotation = PackageRotation(manifest);
                content.SetActive(AppState.Mode != AppMode.Passthrough);
                // sitescope: the arriving site's items come back once its scale is restored (never rescaled on the way in)
                SiteScope.HoldArrival();
                try
                {
                    sceneRoot.SetRuntimeContent(site, manifest, null, content, null);
                    RestoreCalibration(site, manifest);
                }
                finally { SiteScope.Arrive(sceneRoot.Calibration); }
            }
            var parent = sceneRoot.Content.transform;
            var package = BuildPackage(site, manifest, cameras);

            // Collider + structure first…
            var oldCollision = m_Collision;
            var collisionHolder = new GameObject($"Collision r{manifest.revision}");
            collisionHolder.transform.SetParent(parent, false);
            bool ok = false;
            var src = collisionGltf ?? meshGltf;
            yield return WaitInstantiate(gen, src.InstantiateMainSceneAsync(collisionHolder.transform), r => ok = r);
            if (!m_Load.IsCurrent(gen)) { Destroy(collisionHolder); DisposeImports(); yield break; }
            if (ok)
            {
                SceneLoader.PrepareSurfaces(collisionHolder, visible: false);
                if (oldCollision != null) { oldCollision.SetActive(false); Destroy(oldCollision); }
                m_Collision = collisionHolder;
                Physics.SyncTransforms();
            }
            else { Destroy(collisionHolder); Log.Warn("SceneStreamer: collision instantiate failed; keeping the old collider"); }
            // Photos belong to their revision's cameras (thumbs/ is shared across revisions on the server).
            if (newRevision) ClearThumbs();
            // An aligned revision keeps the calibration: carry it to the new revision's key. scalemodels: only a person's
            // scale is saved; a site default stays a default (it isn't saved, the new revision loads with it anyway).
            bool carryCalibration = !fresh && newRevision && Mathf.Abs(sceneRoot.Calibration - 1f) > 1e-6f && ScaleSource == ScaleSource.User;
            Cameras = cameras;
            Manifest = manifest;
            Site = site;
            if (carryCalibration) SaveCalibration();
            sceneRoot.SetStructure(manifest, structure, package);

            // …then the visual one frame later.
            yield return null;
            if (!m_Load.IsCurrent(gen)) { DisposeImports(); yield break; }
            Step(gen, "swap mesh");
            var oldVisual = m_Visual;
            var visualHolder = new GameObject($"Mesh r{manifest.revision}");
            visualHolder.transform.SetParent(parent, false);
            bool visualOk = false;
            yield return WaitInstantiate(gen, meshGltf.InstantiateMainSceneAsync(visualHolder.transform), r => visualOk = r);
            if (!m_Load.IsCurrent(gen)) { Destroy(visualHolder); DisposeImports(); yield break; }
            if (visualOk)
            {
                PrepareVisual(visualHolder);
                if (oldVisual != null) Destroy(oldVisual);
                m_Visual = visualHolder;
            }
            else if (fresh)
            {
                // A new scene with no visual is an empty world: back to the built-in scene, and say so (Retry).
                Destroy(visualHolder);
                DisposeImports();
                RestoreBuiltIn();
                Fail(gen, site, $"{site}: the mesh didn't instantiate; showing the built-in scene");
                yield break;
            }
            else { Destroy(visualHolder); Log.Warn("SceneStreamer: mesh instantiate failed; keeping the old visual"); }

            // Scene parts: the cavities (hidden until their part comes out) and the id → node lookup.
            if (m_Cavities != null) { m_Cavities.SetActive(false); Destroy(m_Cavities); m_Cavities = null; }
            if (partsDoc != null && cavityGltf != null)
            {
                var cavityHolder = new GameObject($"Cavities r{manifest.revision}");
                cavityHolder.transform.SetParent(parent, false);
                Step(gen, "swap cavities");
                bool cavitiesOk = false;
                yield return WaitInstantiate(gen, cavityGltf.InstantiateMainSceneAsync(cavityHolder.transform), r => cavitiesOk = r);
                if (!m_Load.IsCurrent(gen)) { Destroy(cavityHolder); DisposeImports(); yield break; }
                if (cavitiesOk)
                {
                    PrepareCavities(cavityHolder);
                    m_Cavities = cavityHolder;
                }
                else { Destroy(cavityHolder); Log.Warn("SceneStreamer: cavity instantiate failed; parts come out without their cavities"); }
            }
            var parts = Parts;
            if (parts != null)
            {
                parts.sceneRoot = sceneRoot;
                if (partsDoc != null) parts.Bind(site, manifest.revision, partsDoc, m_Visual, m_Collision, m_Cavities);
                else parts.Unbind();
            }

            // The previous revision's glTF data (meshes, textures) can go now.
            var keep = new List<IDisposable> { meshGltf };
            if (collisionGltf != null) keep.Add(collisionGltf);
            if (cavityGltf != null) keep.Add(cavityGltf);
            foreach (var o in m_Owners) if (!keep.Contains(o)) o.Dispose();
            m_Owners.Clear();
            m_Owners.AddRange(keep);

            if (!fresh) Swaps++;
            if (fresh && rig != null && !DeferSpawn(manifest)) PlaceRig(manifest);   // modelview: DeferSpawn
            Step(gen, "swap done");
        }

        void ClearRuntime()
        {
            Parts?.Unbind();
            if (m_Visual != null) Destroy(m_Visual);
            if (m_Collision != null) { m_Collision.SetActive(false); Destroy(m_Collision); }
            if (m_Cavities != null) { m_Cavities.SetActive(false); Destroy(m_Cavities); }
            m_Visual = m_Collision = m_Cavities = null;
            foreach (var o in m_Owners) o.Dispose();
            m_Owners.Clear();
            ClearThumbs();
        }

        void ClearThumbs()
        {
            foreach (var t in m_Thumbs.Values) if (t != null) Destroy(t);
            m_Thumbs.Clear();
            m_ThumbBytes.Clear();
        }

        /// A newer revision may replace the loaded one in place (collider, structure and mesh swapped under the same
        /// scene transform, so measurements, pins and parts stay on the same features) only when it is in the same
        /// frame: the same frame id and axes, and either the same revision again (Reload) or a newer one that says
        /// `aligned_to_preview: true`. Otherwise it loads as a new scene.
        public static bool SwapsInPlace(SceneManifest loaded, SceneManifest next)
        {
            if (loaded == null || next == null) return false;
            if ((loaded.FrameId ?? "") != (next.FrameId ?? "")) return false;
            if ((loaded.Up - next.Up).sqrMagnitude > 1e-8f || Mathf.Abs(Mathf.DeltaAngle(loaded.NorthDeg, next.NorthDeg)) > 1e-3f) return false;
            if (next.revision == loaded.revision) return true;
            return next.revision > loaded.revision && next.frame?.aligned_to_preview == true;
        }

        /// scene.json from the server names the same revision as the copy on the headset but says something else: the
        /// revision was republished, so its revision-named files on the headset may be stale.
        public static bool IsRepublished(string cachedText, string serverText, SceneManifest server)
        {
            if (string.IsNullOrEmpty(cachedText) || string.IsNullOrEmpty(serverText) || server == null) return false;
            if (Normalise(cachedText) == Normalise(serverText)) return false;
            SceneManifest cached;
            try { cached = SceneManifest.Parse(cachedText); }
            catch (Exception) { return false; }
            return cached.revision == server.revision;
        }

        static string Normalise(string json) => System.Text.RegularExpressions.Regex.Replace(json, @"\s+", "");

        static Quaternion PackageRotation(SceneManifest m) =>
            Quaternion.AngleAxis(m.NorthDeg, Vector3.up) * Quaternion.FromToRotation(m.Up, Vector3.up);

        ScenePackage BuildPackage(string site, SceneManifest m, List<SceneCameraJson> cameras)
        {
            if (m_Package == null) m_Package = ScriptableObject.CreateInstance<ScenePackage>();
            m_Package.name = site;
            m_Package.siteName = site;
            m_Package.up = m.Up;
            m_Package.northDeg = m.NorthDeg;
            m_Package.scaleResidualM = m.scale_residual_m ?? 0;
            var infos = new SceneCameraInfo[cameras.Count];
            for (int i = 0; i < cameras.Count; i++) infos[i] = SceneCameras.ToInfo(cameras[i], i);
            m_Package.cameras = infos;
            return m_Package;
        }

        /// Stand at the recommended spawn (feet on the floor below the eye point), looking at `look`.
        public void PlaceRig(SceneManifest m)
        {
            if (rig == null || sceneRoot == null || sceneRoot.Content == null) return;
            var content = sceneRoot.Content.transform;
            if (!m.TryGetSpawn(out var eye, out var look))
            {
                Log.Warn("SceneStreamer: no recommended_spawn; rig left where it is");
                return;
            }
            var feet = content.TransformPoint(eye - m.Up * spawnEyeHeight);
            var dir = Vector3.ProjectOnPlane(content.TransformPoint(look) - content.TransformPoint(eye), Vector3.up);
            float yaw = dir.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(dir.normalized, Vector3.up).eulerAngles.y : rig.eulerAngles.y;
            // The pipeline spawns 3 package-metres out from the scene's box: right for a facade, but a small indoor
            // scan (the kitchen) ends up a diorama 4 m away that the hand rays can barely use. Step in along the view.
            if (dir.sqrMagnitude > 1e-6f && m_Collision != null && WorldBounds(m_Collision, out var box))
                feet += dir.normalized * ApproachDistance(box, feet, dir.normalized, minStandOff);
            rig.SetPositionAndRotation(feet, Quaternion.Euler(0f, yaw, 0f));
            if (Services.TryGet<AirTools.Input.Locomotion>(out var loco)) loco.SetHome(new Pose(rig.position, rig.rotation));
            Log.Info($"SceneStreamer: spawn at {feet} yaw {yaw:0}°");
        }

        /// How far to walk forward (horizontally, along unit `forward`) from `from` so the scene's box spans about 90°
        /// of view: stand half its width (across the view) from its near face, at least `minStandOff`, never backward.
        public static float ApproachDistance(Bounds box, Vector3 from, Vector3 forward, float minStandOff)
        {
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-8f) return 0f;
            forward.Normalize();
            // Entry into the box's horizontal footprint (slab test on x and z).
            float tEnter = 0f, tExit = float.MaxValue;
            for (int axis = 0; axis < 3; axis += 2)
            {
                float o = from[axis], d = forward[axis], min = box.min[axis], max = box.max[axis];
                if (Mathf.Abs(d) < 1e-6f) { if (o < min || o > max) return 0f; continue; }
                float t0 = (min - o) / d, t1 = (max - o) / d;
                if (t0 > t1) (t0, t1) = (t1, t0);
                tEnter = Mathf.Max(tEnter, t0); tExit = Mathf.Min(tExit, t1);
            }
            if (tEnter > tExit || tEnter <= 0f) return 0f;   // looking past it, or already inside
            var across = new Vector3(-forward.z, 0f, forward.x);
            float halfWidth = box.extents.x * Mathf.Abs(across.x) + box.extents.z * Mathf.Abs(across.z);
            return Mathf.Max(0f, tEnter - Mathf.Max(minStandOff, halfWidth));
        }

        /// World-space bounds of every mesh under `go` (works while it's inactive, unlike Renderer.bounds).
        static bool WorldBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds; var m = mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var p = m.MultiplyPoint3x4(new Vector3(i % 2 == 0 ? b.min.x : b.max.x, (i / 2) % 2 == 0 ? b.min.y : b.max.y, i < 4 ? b.min.z : b.max.z));
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; } else bounds.Encapsulate(p);
                }
            }
            return any;
        }

        void PrepareVisual(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (visualMaterial == null) continue;
                var mats = r.sharedMaterials;
                bool converted = true;
                foreach (var m0 in mats) if (m0 == null || m0.shader != visualMaterial.shader) converted = false;
                if (converted) continue;   // UrpMaterialGenerator already made them
                for (int i = 0; i < mats.Length; i++)
                {
                    var src = mats[i];
                    var tex = src != null ? MainTexture(src) : null;
                    var m = new Material(visualMaterial) { name = $"{visualMaterial.name} ({(tex != null ? tex.name : "untextured")})" };
                    if (tex != null) { m.mainTexture = tex; if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex); }
                    var tint = src != null ? BaseColor(src) : Color.white;
                    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", tint);
                    mats[i] = m;
                }
                r.sharedMaterials = mats;
            }
        }

        /// Cavities: the scene's unlit material multiplied by their flat vertex colours (linear COLOR_0: glTFast copies
        /// it unconverted and the project is Linear, so SceneReveal multiplies it in as is), colliders on SceneSurface so
        /// the tape lands on the cavity's floor, back and sides once the part is out. SceneParts.Bind hides every
        /// cavity node until its part comes out.
        void PrepareCavities(GameObject go)
        {
            PrepareVisual(go);
            int colors = 0;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.HasProperty("_VertexColors")) { m.SetFloat("_VertexColors", 1f); colors++; }
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null && !mf.sharedMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color))
                    Log.Warn($"SceneStreamer: cavity mesh {mf.name} has no COLOR_0; it draws in the base colour");
            if (colors == 0) Log.Warn("SceneStreamer: the cavity material has no _VertexColors (not AirTools/SceneReveal?): cavities draw untinted");
            SceneLoader.PrepareSurfaces(go, visible: true);
        }

        static Color BaseColor(Material m)
        {
            foreach (var prop in new[] { "baseColorFactor", "_BaseColor", "_Color" })
                if (m.HasProperty(prop)) return m.GetColor(prop);
            return Color.white;
        }

        static Texture MainTexture(Material m)
        {
            foreach (var prop in new[] { "baseColorTexture", "_BaseMap", "_MainTex", "baseColorTexture_Texture" })
                if (m.HasProperty(prop) && m.GetTexture(prop) != null) return m.GetTexture(prop);
            return m.mainTexture;
        }

        /// Visual meshes get mipmaps, trilinear and 8× anisotropic filtering: glTFast's defaults (no mipmaps, 1× aniso)
        /// make a 4K photo atlas shimmer at any distance and smear on grazing walls. It costs a little load time (mips
        /// of the atlas), nothing per frame.
        public static ImportSettings VisualImportSettings() => new ImportSettings
        {
            GenerateMipMaps = true,
            AnisotropicFilterLevel = 8,
            DefaultMinFilterMode = GLTFast.Schema.Sampler.MinFilterMode.LinearMipmapLinear,
            DefaultMagFilterMode = GLTFast.Schema.Sampler.MagFilterMode.Linear,
        };

        async Task<GltfImport> Import(byte[] bytes, string url, bool visual = false)
        {
            // Our own unlit materials (baked photogrammetry lighting; glTFast's shader graphs aren't in player builds).
            var gltf = visualMaterial != null ? new GltfImport(null, null, new UrpMaterialGenerator(null, visualMaterial, forceUnlit: true)) : new GltfImport();
            bool ok;
            try { ok = await gltf.Load(bytes, new Uri(url), visual ? VisualImportSettings() : null); }
            catch (Exception ex) { LastError = $"glTF {url}: {ex.Message}"; ok = false; }
            if (ok) return gltf;
            gltf.Dispose();
            return null;
        }

        // ---------------- files + cache ----------------

        /// `refetch`: don't trust the headset's copy (a republished revision): download, and no stale fallback.
        IEnumerator Fetch(string site, string file, Action<byte[]> done, bool refetch = false, int gen = 0)
        {
            // gen != 0: a package file of that load — logged per file, its download bytes count as progress.
            if (string.IsNullOrEmpty(file)) { done(null); yield break; }
            bool immutable = IsRevisionNamed(file);
            var cached = CachePath(site, CacheName(file, Manifest != null && site == Site ? Manifest.revision : 0));
            if (immutable && !refetch && File.Exists(cached))
            {
                var bytes = ReadBytes(cached);
                if (bytes != null)
                {
                    if (gen != 0) Step(gen, $"file {file}", $"cache {Size(bytes.Length)}");
                    done(bytes);
                    yield break;
                }
            }
            using var req = UnityWebRequest.Get(SiteUrl(site, file));
            req.timeout = 120;
            float t = Now;
            if (gen != 0) { Step(gen, $"file {file}", "downloading"); m_Load.NetBegin(gen, Now); }
            // Our clock (HttpDeadline): bytes must keep arriving; a given-up load abandons its downloads.
            yield return HttpDeadline.Send(req, DownloadStallSeconds, $"GET {site}/{file}",
                abandon: gen != 0 ? () => !m_Load.IsCurrent(gen) : (Func<bool>)null,
                onProgress: gen != 0 ? () => m_Load.Touch(gen, Now) : (Action)null);
            if (gen != 0) m_Load.NetEnd(gen, Now);
            var hung = HttpDeadline.GaveUp(req);
            if (hung != null)
            {
                if (gen == 0 || m_Load.IsCurrent(gen)) LastError = $"{file}: {hung}";
                var cachedCopy = !refetch && File.Exists(cached) ? ReadBytes(cached) : null;
                if (gen != 0 && m_Load.IsCurrent(gen) && cachedCopy != null) Step(gen, $"file {file}", $"{hung}; the cached copy ({Size(cachedCopy.Length)})");
                done(cachedCopy);
            }
            else if (req.result == UnityWebRequest.Result.Success)
            {
                var data = req.downloadHandler.data;
                if (!SceneCache.WriteAtomic(cached, data)) Log.Warn($"SceneStreamer: cache write failed: {Path.GetFileName(cached)}");   // scalemodels: never a half file
                if (gen != 0) Step(gen, $"file {file}", $"net {Size(data.Length)} in {Now - t:0.0} s");
                done(data);
            }
            else
            {
                LastError = $"{file}: {req.error} ({req.responseCode})";
                var stale = !refetch && File.Exists(cached) ? ReadBytes(cached) : null;
                if (gen != 0)
                {
                    if (stale != null) Step(gen, $"file {file}", $"{req.error}; the cached copy ({Size(stale.Length)})");
                    else Log.Warn($"SceneStreamer: {site} · {file} unavailable: {req.error} ({req.responseCode})");
                }
                done(stale);
            }
        }

        static byte[] ReadBytes(string path)
        {
            try { return File.ReadAllBytes(path); }
            catch (Exception ex) { Log.Warn($"SceneStreamer: cached {Path.GetFileName(path)} unreadable: {ex.Message}"); return null; }
        }

        public static bool IsRevisionNamed(string file) => !string.IsNullOrEmpty(file) && System.Text.RegularExpressions.Regex.IsMatch(file, @"\.r\d+\.");

        /// Where a package file is kept on the headset. Revision-named files are their own key; the rest (thumbs/ is
        /// shared across revisions on the server, and a new revision may renumber its photos) are kept per revision,
        /// so an offline load never shows another revision's photo.
        public static string CacheName(string file, int revision) =>
            IsRevisionNamed(file) || revision <= 0 ? file : $"r{revision}/{file}";

        static string CachePath(string site, string file) => Path.Combine(CacheRoot, site, file.Replace('/', Path.DirectorySeparatorChar));

        static string ReadCache(string site, string file)
        {
            var p = CachePath(site, file);
            try { return File.Exists(p) ? File.ReadAllText(p) : null; }
            catch (Exception ex) { Log.Warn($"SceneStreamer: cached {site}/{file} unreadable: {ex.Message}"); return null; }
        }

        void WriteCache(string site, string file, string text)
        {
            if (!SceneCache.WriteAtomic(CachePath(site, file), System.Text.Encoding.UTF8.GetBytes(text ?? ""))) Log.Warn($"SceneStreamer: cache write failed: {site}/{file}");
            if (file == SceneCache.ManifestFile) ForgetOnHeadset(site);   // scalemodels
        }

        // ---------------- calibration persistence ----------------

        static string CalibrationKey(string site, SceneManifest m) => $"airtools.scale.{site}.{m.FrameId}.r{m.revision}";

        /// The saved calibrations that apply to this revision, best first: its own; for a revision aligned onto the one
        /// before it, that one's; and the older per-frame key (from before revisions were told apart) for r1 and aligned
        /// revisions only. A revision in its own frame (Zabel r3) never inherits another revision's scale.
        public static List<string> CalibrationKeys(string site, SceneManifest m)
        {
            var keys = new List<string> { CalibrationKey(site, m) };
            bool aligned = m.frame?.aligned_to_preview == true;
            if (aligned && m.revision > 1) keys.Add($"airtools.scale.{site}.{m.FrameId}.r{m.revision - 1}");
            if (aligned || m.revision <= 1) keys.Add($"airtools.scale.{site}.{m.FrameId}");
            return keys;
        }

        /// scalemodels: a scale a person set on this headset for this site, frame and revision, else the site's default
        /// (SiteScales.Choose), else the package's own.
        void RestoreCalibration(string site, SceneManifest m)
        {
            var c = SiteScales.Choose(CalibrationKeys(site, m), SavedScale, SavedStamp, SiteScales.DefaultFor(siteScales, site));
            ApplyScale(c.Scale, c.Source);
            if (c.Source == ScaleSource.User)
                Log.Info($"SceneStreamer: restored scale ×{c.Scale:0.0000} for {site} r{m.revision} (frame {m.FrameId}, {c.Key})");
            else if (c.Source == ScaleSource.SiteDefault)
                Log.Info($"SceneStreamer: {site} r{m.revision} at its default scale ×{SiteScales.Factor(c.Scale)} (SiteScales; a tape + Set scale overrides it)");
        }

        /// Remember the current calibration for this site, frame and revision (a person's: a tape + Set scale, a scale
        /// from a gap). scalemodels: stamped with SiteScales.Version, and never while the streamer applies a scale itself
        /// (a restore or a site default mid-swap would otherwise be saved as the person's, under the previous site's key).
        public void SaveCalibration()
        {
            if (m_ApplyingScale) return;
            if (Manifest == null || string.IsNullOrEmpty(Site) || sceneRoot == null) return;
            if (sceneRoot.IsRuntimePackage && sceneRoot.Site != Site) return;   // mid-swap: the root already shows another site
            var key = CalibrationKey(Site, Manifest);
            PlayerPrefs.SetFloat(key, sceneRoot.Calibration);
            PlayerPrefs.SetInt(SiteScales.StampKey(key), SiteScales.Version);
            PlayerPrefs.Save();
            ScaleSource = SiteScales.Consistent(sceneRoot.Calibration, ScaleSource.User);
        }

        public void ForgetCalibration()
        {
            if (Manifest == null || string.IsNullOrEmpty(Site)) return;
            foreach (var key in CalibrationKeys(Site, Manifest)) { PlayerPrefs.DeleteKey(key); PlayerPrefs.DeleteKey(SiteScales.StampKey(key)); }
        }

        // ---------------- scalemodels: the scale's source, and Reset to the site's default ----------------

        /// Where the loaded scene's scale came from: the site's default (SiteScales), a person (a tape + Set scale), or
        /// none (the package's own; the built-in facade).
        public ScaleSource ScaleSource { get; private set; }
        /// The loaded site's default scale (1 = none).
        public float SiteDefaultScale => SiteScales.DefaultFor(siteScales, Site);
        bool m_ApplyingScale;

        static float? SavedScale(string key) => PlayerPrefs.HasKey(key) ? PlayerPrefs.GetFloat(key, 1f) : (float?)null;
        static int SavedStamp(string key) => PlayerPrefs.GetInt(SiteScales.StampKey(key), 0);

        /// Set the calibration as the streamer's own doing (not saved as a person's scale).
        void ApplyScale(float k, ScaleSource source)
        {
            m_ApplyingScale = true;
            try { if (sceneRoot != null) sceneRoot.SetCalibration(k); }
            finally { m_ApplyingScale = false; }
            ScaleSource = source;
        }

        /// Settings ▸ Reset and Demo reset: forget the person's scale for this site and go back to its default (×1 without
        /// one; the built-in facade to its own). False with nothing loaded.
        public bool ResetCalibration()
        {
            if (sceneRoot == null || sceneRoot.Content == null) return false;
            if (!sceneRoot.IsRuntimePackage) { ApplyScale(1f, ScaleSource.None); return true; }
            ForgetCalibration();
            var c = SiteScales.ResetTo(SiteDefaultScale);
            ApplyScale(c.Scale, c.Source);
            Log.Info($"SceneStreamer: {Site} scale back to {(c.Source == ScaleSource.SiteDefault ? $"its default ×{SiteScales.Factor(c.Scale)}" : "the capture's own")}");
            return true;
        }

        // ---------------- thumbnails ----------------

        /// Evidence photo for a camera: cached texture, or null now and a download started (call again later).
        public Texture2D Thumbnail(SceneCameraInfo cam)
        {
            if (cam.thumbnail != null) return cam.thumbnail;
            if (string.IsNullOrEmpty(cam.thumbPath) || string.IsNullOrEmpty(Site)) return null;
            if (m_Thumbs.TryGetValue(cam.thumbPath, out var tex)) return tex;
            if (m_ThumbsLoading.Add(cam.thumbPath)) StartCoroutine(LoadThumb(Site, cam.thumbPath));
            return null;
        }

        /// JPEG bytes of a thumbnail (for /scene/ask frames): from memory, the headset cache or the server.
        public void ThumbnailBytes(string thumbPath, Action<byte[]> done)
        {
            if (!string.IsNullOrEmpty(thumbPath) && m_ThumbBytes.TryGetValue(thumbPath, out var have) && have != null) { done(have); return; }
            StartCoroutine(FetchThumb(Site, Manifest?.revision ?? 0, thumbPath, done));
        }

        /// JPEG bytes of a thumbnail already downloaded for the loaded revision, without waiting (the agent context's
        /// frame_jpg_b64). A miss starts the download, so the next command has it.
        public bool TryGetThumbBytes(string thumbPath, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(thumbPath) || string.IsNullOrEmpty(Site)) return false;
            if (m_ThumbBytes.TryGetValue(thumbPath, out bytes) && bytes != null) return true;
            if (Application.isPlaying && isActiveAndEnabled && m_BytesLoading.Add(thumbPath))
                StartCoroutine(FetchThumb(Site, Manifest?.revision ?? 0, thumbPath, _ => m_BytesLoading.Remove(thumbPath)));
            return false;
        }

        /// Download a thumb and keep its bytes for this revision (dropped when the site or revision changes meanwhile).
        IEnumerator FetchThumb(string site, int revision, string path, Action<byte[]> done)
        {
            byte[] bytes = null;
            yield return Fetch(site, path, b => bytes = b);
            if (bytes != null && site == Site && revision == (Manifest?.revision ?? 0)) m_ThumbBytes[path] = bytes;
            done?.Invoke(bytes);
        }

        IEnumerator LoadThumb(string site, string path)
        {
            byte[] bytes = null;
            int revision = Manifest?.revision ?? 0;
            yield return FetchThumb(site, revision, path, b => bytes = b);
            m_ThumbsLoading.Remove(path);
            if (bytes == null || site != Site || revision != (Manifest?.revision ?? 0)) yield break;
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = path };
            if (tex.LoadImage(bytes, markNonReadable: false)) m_Thumbs[path] = tex;
            else Destroy(tex);
        }

        public SceneCameraJson FindCamera(string id)
        {
            foreach (var c in Cameras) if (c.id == id) return c;
            return null;
        }

        // ---------------- modelview: Model view spawns and site pictures ----------------

        /// In Model view (the scene on the table in passthrough) the rig must not move: the table model would slide
        /// against the real room. A site loaded then hands its spawn to the tabletop, which lands you there when you step
        /// in (TabletopController.SetSpawn).
        static bool DefersSpawn(out TabletopController table) =>
            Services.TryGet(out table) && table != null && table.DefersSpawn;

        bool DeferSpawn(SceneManifest m)
        {
            if (!DefersSpawn(out var table)) return false;
            if (TrySpawnLocal(m, out var feet, out float yaw)) table.SetSpawn(feet, yaw, Site);
            else table.ClearSpawn();
            return true;
        }

        /// PlaceRig's spawn in SceneRoot space (scene metres; the root's own pose and table scale drop out): the feet
        /// under the recommended eye point, stepped in along the view like PlaceRig, and the view's heading (degrees about
        /// up, 0 = root +Z). False without a recommended spawn or content.
        public bool TrySpawnLocal(SceneManifest m, out Vector3 feetLocal, out float yawLocalDeg)
        {
            feetLocal = default;
            yawLocalDeg = 0f;
            if (m == null || sceneRoot == null || sceneRoot.Content == null || !m.TryGetSpawn(out var eye, out var look)) return false;
            var content = sceneRoot.Content.transform;
            var root = sceneRoot.transform;
            Vector3 ToRoot(Vector3 p) => root.InverseTransformPoint(content.TransformPoint(p));
            var feet = ToRoot(eye - m.Up * spawnEyeHeight);
            var dir = ToRoot(look) - ToRoot(eye);
            dir.y = 0f;
            if (dir.sqrMagnitude > 1e-6f)
            {
                dir.Normalize();
                if (m_Collision != null && RootBounds(m_Collision, root, out var box)) feet += dir * ApproachDistance(box, feet, dir, minStandOff);
                yawLocalDeg = TabletopFit.Heading(dir);
            }
            feetLocal = feet;
            return true;
        }

        /// Bounds of every mesh under `go` in `root`'s space (works while it's inactive).
        static bool RootBounds(GameObject go, Transform root, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            var toRoot = root.worldToLocalMatrix;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds; var mtx = toRoot * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var p = mtx.MultiplyPoint3x4(new Vector3(i % 2 == 0 ? b.min.x : b.max.x, (i / 2) % 2 == 0 ? b.min.y : b.max.y, i < 4 ? b.min.z : b.max.z));
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; } else bounds.Encapsulate(p);
                }
            }
            return any;
        }

        readonly Dictionary<string, Texture2D> m_SiteThumbs = new Dictionary<string, Texture2D>();
        readonly Dictionary<string, float> m_SiteThumbsLoading = new Dictionary<string, float>();

        /// A picture of a site for the Model view switcher: the scan's photo taken nearest its recommended spawn (about
        /// what you see when you walk in); the built-in facade's own render. Null now and a download started (call again
        /// later; a failed one is retried after 30 s). Kept for the session; scene.json, the cameras file and the photo
        /// come from the headset's cache when the laptop is away.
        public Texture2D SiteThumbnail(string site)
        {
            if (string.IsNullOrEmpty(site)) site = ModelSites.BuiltIn;
            if (m_SiteThumbs.TryGetValue(site, out var tex)) return tex;
            if (site == ModelSites.BuiltIn)
            {
                var built = BuiltInThumbnail();   // an asset: kept, never destroyed
                if (built != null) m_SiteThumbs[site] = built;
                return built;
            }
            if (!Application.isPlaying || !isActiveAndEnabled) return null;
            // scalemodels: the picture kept on the headset shows at once (and is all there is with the laptop away).
            var kept = KeptPicture(site, out bool nextFrame);
            if (kept != null || nextFrame) return kept;
            if (m_SiteThumbsLoading.TryGetValue(site, out float since) && Time.unscaledTime - since < 30f) return null;
            m_SiteThumbsLoading[site] = Time.unscaledTime;
            StartCoroutine(LoadSiteThumb(site));
            return null;
        }

        Texture2D BuiltInThumbnail()
        {
            var boot = FindAnyObjectByType<AppBootstrap>();
            var cams = boot != null && boot.scenePackage != null ? boot.scenePackage.cameras : null;
            if (cams == null || cams.Length == 0) return null;
            var spawn = boot.scenePackage.spawnPosition + Vector3.up * spawnEyeHeight;
            Texture2D best = null;
            float bestD = float.MaxValue;
            foreach (var c in cams)
            {
                if (c.thumbnail == null) continue;
                float d = (c.position - spawn).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c.thumbnail; }
            }
            return best;
        }

        IEnumerator LoadSiteThumb(string site)
        {
            string text = null;
            using (var req = UnityWebRequest.Get(SiteUrl(site, "scene.json")))
            {
                req.timeout = 5;
                yield return HttpDeadline.Send(req, RequestDeadline, "GET scene.json (thumbnail)");
                if (req.result == UnityWebRequest.Result.Success) text = req.downloadHandler.text;
            }
            text ??= ReadCache(site, "scene.json");
            SceneManifest m = null;
            try { if (text != null) m = SceneManifest.Parse(text); }
            catch (Exception ex) { Log.Warn($"SceneStreamer: {site} thumbnail: scene.json unreadable: {ex.Message}"); }
            if (m == null || string.IsNullOrEmpty(m.cameras)) yield break;
            byte[] camBytes = null;
            yield return Fetch(site, m.cameras, b => camBytes = b);
            List<SceneCameraJson> cams = null;
            try { if (camBytes != null) cams = SceneCameras.Parse(System.Text.Encoding.UTF8.GetString(camBytes)); }
            catch (Exception ex) { Log.Warn($"SceneStreamer: {site} thumbnail: {m.cameras} unreadable: {ex.Message}"); }
            if (cams == null || cams.Count == 0) yield break;
            var positions = new List<Vector3>(cams.Count);
            foreach (var c in cams) positions.Add(string.IsNullOrEmpty(c.thumb) ? new Vector3(float.MaxValue, 0f, 0f) : GltfFrame.ToUnity(c.position));
            int pick = m.TryGetSpawn(out var eye, out _) ? ModelSites.Nearest(positions, eye) : cams.Count / 2;
            if (pick < 0 || string.IsNullOrEmpty(cams[pick].thumb)) pick = cams.FindIndex(c => !string.IsNullOrEmpty(c.thumb));
            if (pick < 0) yield break;
            byte[] jpg = null;
            yield return Fetch(site, cams[pick].thumb, b => jpg = b);
            if (jpg == null) yield break;
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = $"{site} {cams[pick].thumb}" };
            if (tex.LoadImage(jpg, markNonReadable: true))
            {
                if (m_SiteThumbs.TryGetValue(site, out var old) && old != null) Destroy(old);
                m_SiteThumbs[site] = tex;
                m_SiteThumbsLoading.Remove(site);
                SceneCache.WriteAtomic(SceneCache.PathOf(CacheRoot, site, SceneCache.PictureName(m.revision)), jpg);   // scalemodels
                m_PicturePath.Remove(site);
            }
            else Destroy(tex);
        }
        // end modelview

        // ---------------- scalemodels: the models on the headset ----------------
        // The switcher mustn't go empty when the laptop is away (the headset, 2026-09-26: the USB tunnel dropped, GET /scenes
        // failed, and Model view offered only the built-in facade). The last good listing is kept on the headset
        // (SceneCache: listing.json and each site's picture); ScenePrefetcher downloads every listed model in the
        // background; offline, the switcher lists what's on the headset and dims the rest.

        /// The cache directory (persistentDataPath/scenes).
        public static string CacheDirectory => CacheRoot;
        /// Every model the app knows: the last good listing (kept on the headset), then cached packages it doesn't name.
        public List<SceneListing> KnownSites { get => m_Known; private set { m_Known = value ?? new List<SceneListing>(); KnownVersion++; } }
        List<SceneListing> m_Known = new List<SceneListing>();
        /// Bumped whenever KnownSites changes (the switcher re-reads it then).
        public int KnownVersion { get; private set; }
        /// The last GET /scenes answered. False before the first answer and while the laptop is away: the switcher then
        /// offers only what's on the headset.
        public bool ServerListed { get; private set; }
        /// Time.realtimeSinceStartup of the last answer (−1: none this session).
        public float ListedAt { get; private set; } = -1f;
        /// A listing came in (answered or not): (answered, the sites).
        public event Action<bool, List<SceneListing>> Listed;
        readonly Dictionary<string, (bool complete, int revision)> m_OnHeadset = new Dictionary<string, (bool, int)>();

        void LoadKnownSites()
        {
            try { KnownSites = SceneCache.Known(SceneCache.ReadListing(CacheRoot), SceneCache.CachedSites(CacheRoot), ignoreSites); }
            catch (Exception ex) { Log.Warn($"SceneStreamer: the kept listing is unreadable: {ex.Message}"); KnownSites = new List<SceneListing>(); }
            int on = 0;
            foreach (var s in KnownSites) if (OnHeadset(s.site)) on++;
            Log.Info($"SceneStreamer: {KnownSites.Count} models known on this headset ({on} downloaded): {string.Join(", ", KnownSites.ConvertAll(s => s.site))}");
        }

        void OnListed(bool answered, List<SceneListing> list)
        {
            ServerListed = answered;
            if (answered)
            {
                ListedAt = Now;
                if (list.Count > 0) SceneCache.WriteListing(CacheRoot, list);
                KnownSites = SceneCache.Known(list, SceneCache.CachedSites(CacheRoot), ignoreSites);
            }
            Listed?.Invoke(answered, list);
        }

        /// The site's package opens with the laptop away (SceneCache.IsComplete; remembered until the cache changes).
        public bool OnHeadset(string site)
        {
            if (string.IsNullOrEmpty(site) || site == ModelSites.BuiltIn) return true;
            if (!m_OnHeadset.TryGetValue(site, out var v))
            {
                bool complete = SceneCache.IsComplete(CacheRoot, site, out int rev);
                m_OnHeadset[site] = v = (complete, rev);
            }
            return v.complete;
        }

        /// The site's cached revision (0: none), and whether it opens offline.
        public bool OnHeadset(string site, out int revision)
        {
            bool complete = OnHeadset(site);
            revision = m_OnHeadset.TryGetValue(site ?? "", out var v) ? v.revision : 0;
            return complete;
        }

        /// The cache of `site` changed (a load, the prefetch): ask the disk again next time. Null: every site.
        public void ForgetOnHeadset(string site = null)
        {
            if (site == null) m_OnHeadset.Clear();
            else m_OnHeadset.Remove(site);
            KnownVersion++;   // the cards' Not downloaded states may change
        }

        /// A package was loaded or downloaded: known, and on the headset.
        public void Remember(string site, int revision)
        {
            ForgetOnHeadset(site);
            if (string.IsNullOrEmpty(site) || Array.IndexOf(ignoreSites, site) >= 0) return;
            if (KnownSites.Exists(s => s.site == site)) return;
            KnownSites = new List<SceneListing>(KnownSites) { new SceneListing { site = site, revision = revision } };
        }

        /// The switcher's models and how many open with the laptop away ("Models on the headset: 5 of 7").
        public void HeadsetCount(out int on, out int total)
        {
            var ids = new List<string>(KnownSites.Count);
            foreach (var s in KnownSites) ids.Add(s.site);
            PrefetchPlan.Count(ids, OnHeadset, out on, out total);
        }

        /// The models that open with the laptop away (on the headset, not hidden), in the switcher's order, without the
        /// built-in facade.
        public List<string> OfflineSites()
        {
            var ids = new List<string>(KnownSites.Count);
            foreach (var s in KnownSites) ids.Add(s.site);
            var list = ModelSites.OfflineList(ids, OnHeadset);
            list.Remove(ModelSites.BuiltIn);
            return list;
        }

        int m_PictureFrame = -1;
        /// site → its kept picture's path ("" = none), asked of the disk once per site until a picture is written.
        readonly Dictionary<string, string> m_PicturePath = new Dictionary<string, string>();

        /// The site's picture kept on the headset (for its known revision, else the newest older one), decoded once; null
        /// when there's none. At most one decode per frame (a ~20 KB JPEG), so a row of cards doesn't hitch: `nextFrame`
        /// says one is waiting its turn.
        Texture2D KeptPicture(string site, out bool nextFrame)
        {
            nextFrame = false;
            int rev = 0;
            foreach (var s in KnownSites) if (s.site == site) { rev = s.revision; break; }
            if (!m_PicturePath.TryGetValue(site, out var path)) m_PicturePath[site] = path = SceneCache.PicturePath(CacheRoot, site, rev) ?? "";
            if (path.Length == 0) return null;
            // Online, with a newer revision than the kept picture: the network fetches this revision's.
            if (ServerListed && rev > 0 && Path.GetFileName(path) != SceneCache.PictureName(rev)) return null;
            if (m_PictureFrame == Time.frameCount) { nextFrame = true; return null; }
            m_PictureFrame = Time.frameCount;
            byte[] jpg;
            try { jpg = File.ReadAllBytes(path); }
            catch (Exception) { m_PicturePath[site] = ""; return null; }
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = $"{site} {Path.GetFileName(path)} (kept)" };
            if (!tex.LoadImage(jpg, markNonReadable: true)) { Destroy(tex); m_PicturePath[site] = ""; return null; }
            m_SiteThumbs[site] = tex;
            return tex;
        }
        // end scalemodels
    }
}
