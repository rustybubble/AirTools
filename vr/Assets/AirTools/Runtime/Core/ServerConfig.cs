using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Core
{
    /// Where the laptop parts server lives (backend docs/api.md: http://&lt;laptop-ip&gt;:8000). Editor: localhost. Device: the
    /// USB tunnel (`adb reverse tcp:8000 tcp:8000`) by default. Override without rebuilding, first match wins:
    ///   1. launch extra:  adb shell am start -n com.airtools.quest/com.unity3d.player.UnityPlayerGameActivity -e server http://172.20.10.2:8000
    ///   2. a file:        adb push server.txt /sdcard/Android/data/com.airtools.quest/files/server.txt  (one line: the URL)
    ///   3. PlayerPrefs "airtools.server" (set by <see cref="SetOverride"/>).
    /// The same three sources accept "site" (launch extra `-e site kitchen`, or a second line in server.txt).
    /// fix-ux: with none of them (a launch from the Quest library), the default is asked GET /health at start and, if it
    /// doesn't answer within probeSeconds, candidateBaseUrls are asked in order; the first that answers is this session's
    /// server (never saved). SceneStreamer waits for that (≤ 4 s) before its first load.
    public class ServerConfig : MonoBehaviour
    {
        public const string PrefsKey = "airtools.server";
        public string editorBaseUrl = "http://localhost:8000";
        public string deviceBaseUrl = "http://127.0.0.1:8000";
        [Tooltip("Request timeout, seconds.")]
        public int timeoutSeconds = 5;
        [Tooltip("fix-ux: with no override, when the default doesn't answer GET /health, these are asked in order and the first that answers is used for this session (not saved). Set by the builder.")]
        public string[] candidateBaseUrls = { "http://127.0.0.1:8004", "http://127.0.0.1:8000" };
        [Tooltip("How long each server may take to answer the probe (s).")]
        public float probeSeconds = 2f;

        static string s_Override, s_Site;
        static bool s_Read;
        /// The server the start-up probe found, for this session only (null: the default).
        static string s_Session;
        static float s_ProbeUntil = -1f;

        /// The start-up probe is still asking (bounded by its own deadline, so it can't stick).
        public static bool Probing => s_ProbeUntil >= 0f && Time.realtimeSinceStartup < s_ProbeUntil;
        /// Where the probe ended up (null before it finished, or when it didn't run).
        public static string ProbeResult { get; private set; }
        /// The session's base URL changed (the probe picked a candidate).
        public static event Action<string> Changed;

        public string BaseUrl
        {
            get
            {
                ReadOverrides();
                if (!string.IsNullOrEmpty(s_Override)) return s_Override.TrimEnd('/');
                if (!string.IsNullOrEmpty(s_Session)) return s_Session;
                return DefaultBaseUrl;
            }
        }

        /// Site requested at launch ("" = the SceneStreamer's own default).
        public static string SiteOverride { get { ReadOverrides(); return s_Site ?? ""; } }

        /// The saved / launch server override (null = the default), so a harness that swaps servers can put it back.
        public static string OverrideUrl { get { ReadOverrides(); return s_Override; } }

        /// The built-in default for this platform (no override, no probe).
        public string DefaultBaseUrl => (Application.isEditor ? editorBaseUrl : deviceBaseUrl).TrimEnd('/');

        /// An override (launch extra, server.txt or saved pref) chose the server.
        public static bool HasOverride { get { ReadOverrides(); return !string.IsNullOrEmpty(s_Override); } }

        void OnEnable()
        {
            Services.Register(this);
            // Started here, before any Start(): SceneStreamer.Start sees Probing and waits for it.
            if (Application.isPlaying && s_ProbeUntil < 0f && ServerProbe.ShouldProbe(HasOverride, candidateBaseUrls?.Length ?? 0))
            {
                var probe = new ServerProbe(DefaultBaseUrl, candidateBaseUrls);
                s_ProbeUntil = Time.realtimeSinceStartup + probeSeconds * probe.Order.Count + 1f;
                StartCoroutine(Probe(probe));
            }
        }

        void OnDisable() => Services.Unregister(this);

        IEnumerator Probe(ServerProbe probe)
        {
            float t0 = Time.realtimeSinceStartup;
            while (!probe.Done)
            {
                string url = probe.Next;
                long code = 0;
                yield return Ask(url + "/health", c => code = c);
                if (!ServerProbe.Answered(code) && ServerProbe.AskScenes(code)) yield return Ask(url + "/scenes", c => code = c);
                probe.Report(ServerProbe.Answered(code));
            }
            s_ProbeUntil = 0f;
            ProbeResult = probe.Chosen ?? "";
            string msg = probe.Message() + $" ({(Time.realtimeSinceStartup - t0) * 1000f:0} ms)";
            if (probe.Switch != null)
            {
                s_Session = probe.Switch;
                Log.Warn(msg);
                Changed?.Invoke(s_Session);
            }
            else if (probe.Chosen == null) Log.Warn(msg);
            else Log.Info(msg);
        }

        IEnumerator Ask(string url, Action<long> done)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = Mathf.Max(1, Mathf.CeilToInt(probeSeconds));
            yield return HttpDeadline.Send(req, probeSeconds, $"probe {HttpDeadline.PathOf(url)}", totalSeconds: probeSeconds);
            done(HttpDeadline.GaveUp(req) != null ? 0 : req.responseCode);
        }

        /// Base URL from the scene's ServerConfig, or the editor default when there is none (tests).
        public static string Current => Services.TryGet<ServerConfig>(out var c) ? c.BaseUrl : "http://localhost:8000";

        public static void SetOverride(string url)
        {
            s_Override = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
            if (s_Override == null) PlayerPrefs.DeleteKey(PrefsKey); else PlayerPrefs.SetString(PrefsKey, s_Override);
            PlayerPrefs.Save();
            s_Read = true;
            Log.Info($"ServerConfig: server = {(s_Override ?? "default")}");
        }

        static void ReadOverrides()
        {
            if (s_Read) return;
            s_Read = true;
            try
            {
                string url = LaunchExtra("server"), site = LaunchExtra("site");
                var file = Path.Combine(Application.persistentDataPath, "server.txt");
                if (File.Exists(file))
                {
                    var lines = File.ReadAllLines(file);
                    if (string.IsNullOrEmpty(url) && lines.Length > 0 && lines[0].Trim().Length > 0) url = lines[0].Trim();
                    if (string.IsNullOrEmpty(site) && lines.Length > 1 && lines[1].Trim().Length > 0) site = lines[1].Trim();
                }
                if (string.IsNullOrEmpty(url)) url = PlayerPrefs.GetString(PrefsKey, "");
                s_Override = string.IsNullOrEmpty(url) ? null : url;
                s_Site = site;
                if (s_Override != null || !string.IsNullOrEmpty(s_Site)) Log.Info($"ServerConfig: server override {s_Override ?? "-"}, site {s_Site ?? "-"}");
            }
            catch (System.Exception ex) { Log.Warn($"ServerConfig: overrides unreadable: {ex.Message}"); }
        }

        static string LaunchExtra(string key)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                return intent.Call<string>("getStringExtra", key);
            }
            catch { return null; }
#else
            return null;
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_Read = false; s_Override = null; s_Site = null; s_Session = null; s_ProbeUntil = -1f; ProbeResult = null; Changed = null; }
    }
}
