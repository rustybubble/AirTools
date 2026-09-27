using System;
using System.Collections.Generic;
using System.Text;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Scene;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Dev
{
    /// D7 (UX W1.8): the headset end of the presenter page (tools/presenter/presenter_server.py on the laptop, :8766).
    /// About once a second it takes the queued commands (GET /presenter/next), runs them, acknowledges each
    /// (POST /presenter/ack), and posts the app's status (POST /state: mode, the guide rail's rule and status line, the
    /// coach line, the §2.7 beat, judge inputs, the last command and the last error).
    /// Commands: reset (AppCommands.ResetDemo), hint (the coach now), enter / exit world, beat (DemoBeats: skip to a beat
    /// of the judge's three minutes), demo on / off, ping. The presenter never pays: PresenterCommands refuses any
    /// command or argument about paying, checkout or the hold before anything runs, and nothing here can reach the
    /// payment (EditMode test PresenterLinkCannotPay checks the source).
    /// Listens while DemoMode is on, always in a Development player build (so the page can turn DemoMode back on), or
    /// when ForceActive is set (harness). The relay is the parts server's host on port 8766 unless `url`, the launch
    /// extra `-e presenter http://…` or UrlOverride says otherwise; USB needs `adb reverse tcp:8766 tcp:8766`.
    public class PresenterLink : MonoBehaviour
    {
        [Tooltip("Relay base URL. Empty: the parts server's host on port 8766 (USB tunnel: http://127.0.0.1:8766).")]
        public string url = "";
        public int port = PresenterCommands.DefaultPort;
        public float pollSeconds = 1f;
        public float stateSeconds = 1f;
        [Tooltip("While the relay doesn't answer: try again this often.")]
        public float offlineRetrySeconds = 3f;
        public int timeoutSeconds = 3;

        /// Harness: another relay (null = the default).
        public static string UrlOverride;
        /// Harness: listen even when DemoMode is off (the Editor).
        public static bool ForceActive;

        public bool Active => DemoMode.On || ForceActive || (Debug.isDebugBuild && !Application.isEditor);

        public string BaseUrl
        {
            get
            {
                if (!string.IsNullOrEmpty(UrlOverride)) return UrlOverride.TrimEnd('/');
                if (!string.IsNullOrEmpty(url)) return url.TrimEnd('/');
                if (!s_LaunchRead) { s_LaunchRead = true; s_LaunchUrl = DemoMode.LaunchExtra("presenter"); }
                if (!string.IsNullOrEmpty(s_LaunchUrl)) return s_LaunchUrl.TrimEnd('/');
                return PresenterCommands.BaseUrl(ServerConfig.Current, port);
            }
        }

        /// The last poll reached the relay.
        public bool Online { get; private set; }
        public int Polls { get; private set; }
        public int StatePosts { get; private set; }
        public int Received { get; private set; }
        public int Refused { get; private set; }
        public int Acks { get; private set; }
        public bool HasLast { get; private set; }
        public PresenterCommand LastCommand { get; private set; }
        public bool LastOk { get; private set; }
        public string LastDetail { get; private set; } = "";
        /// The newest warning / error the app logged (for the page; cleared by a reset).
        public string LastError { get; private set; }
        /// Deliberate inputs since the last reset (pinches / trigger presses and UI button presses), not the beats'.
        public int JudgeInputs { get; private set; }
        public bool BeatRunning => m_Beat != null;
        /// Longest a beat may run (the parts search it can wait for is ≤ 50 s).
        public const float BeatTimeoutSeconds = 75f;

        readonly Queue<PresenterCommand> m_Todo = new Queue<PresenterCommand>();
        UnityWebRequest m_Poll, m_State;
        float m_NextPoll, m_NextState, m_LastErrorAt = -1f, m_Fps = 72f, m_BeatStartedAt;
        bool m_StateDirty, m_WasOnline, m_Reported;
        Coroutine m_Beat;
        PresenterCommand m_BeatCmd;
        int m_BeatToken;
        ToolInputHub m_Hub;
        static string s_LaunchUrl;
        static bool s_LaunchRead;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { UrlOverride = null; ForceActive = false; s_LaunchRead = false; s_LaunchUrl = null; }

        /// A scene not re-wired since D7 still gets the demo controls in the expo build (Wire adds them to AirTools).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInScene()
        {
            if (!(DemoMode.On || (Debug.isDebugBuild && !Application.isEditor))) return;
            if (FindAnyObjectByType<PresenterLink>() != null) return;
            var go = new GameObject("DemoControls");
            go.AddComponent<PresenterLink>();
            if (FindAnyObjectByType<DemoResetGesture>() == null) go.AddComponent<DemoResetGesture>();
            Log.Info("DemoControls added at runtime (the scene predates D7: run AirTools ▸ Wire Main Scene)");
        }

        void OnEnable()
        {
            Services.Register(this);
            Application.logMessageReceived += OnLog;
            DemoReset.Completed += OnReset;
            FeedbackEvents.Fired += OnFeedback;
        }

        void OnDisable()
        {
            Services.Unregister(this);
            Application.logMessageReceived -= OnLog;
            DemoReset.Completed -= OnReset;
            FeedbackEvents.Fired -= OnFeedback;
            if (m_Hub != null) m_Hub.PressStart -= OnPress;
            m_Hub = null;
            Drop(ref m_Poll);
            Drop(ref m_State);
            m_Beat = null;
            Online = false;
        }

        static void Drop(ref UnityWebRequest r)
        {
            if (r == null) return;
            r.Abort();
            r = null;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 1e-4f) m_Fps = Mathf.Lerp(m_Fps, 1f / dt, 0.05f);
            if (!Active) return;
            if (m_Hub == null && Services.TryGet(out m_Hub)) m_Hub.PressStart += OnPress;
            // A beat that threw (Unity stops the coroutine) or hangs never answers: give up on it.
            if (m_Beat != null && Time.unscaledTime - m_BeatStartedAt > BeatTimeoutSeconds) CancelBeat("timed out (see the last error)");
            Pump();
            float now = Time.unscaledTime;
            if (m_Poll == null && now >= m_NextPoll) Poll();
            if (m_State == null && (m_StateDirty || now >= m_NextState)) PostState();
        }

        // ---------------- commands ----------------

        /// Run what came in, in order: one beat at a time (the next beat waits); a reset / enter / exit stops a running beat.
        void Pump()
        {
            while (m_Todo.Count > 0)
            {
                var c = m_Todo.Peek();
                if (m_Beat != null && c.Cmd == "beat") return;
                m_Todo.Dequeue();
                if (m_Beat != null && (c.Cmd == "reset" || c.Cmd == "enter" || c.Cmd == "exit")) CancelBeat($"stopped by {c.Cmd}");
                Execute(c, out _);
            }
        }

        /// Run one presenter command now (a beat runs over the next frames and is acknowledged when it ends). Refused and
        /// invalid commands return false before anything runs.
        public bool Execute(PresenterCommand c, out string detail)
        {
            var why = PresenterCommands.Validate(c);
            if (why != null)
            {
                if (why == PresenterCommands.Refusal) { Refused++; Log.Warn($"Presenter: {c} {why}"); }
                detail = why;
                Finish(c, false, why);
                return false;
            }
            bool ok;
            switch (c.Cmd)
            {
                case "ping": ok = true; detail = $"pong · {AppState.Mode}"; break;
                case "reset": ok = DemoReset.Run(out detail); break;
                case "hint": ok = Hint(out detail); break;
                case "enter": ok = Enter(out detail); break;
                case "exit": ok = Exit(out detail); break;
                case "demo": DemoMode.On = c.Arg == "on"; ok = true; detail = $"DemoMode {c.Arg}"; break;
                case "beat":
                    if (!Application.isPlaying) { ok = false; detail = "beats run in Play mode only"; break; }
                    m_BeatCmd = c;
                    m_BeatStartedAt = Time.unscaledTime;
                    int token = ++m_BeatToken;
                    var beat = c;
                    // Started directly (not nested), so StopCoroutine really stops it; DemoBeats answers on every path.
                    var co = StartCoroutine(DemoBeats.Run(c.Arg, (done, d) =>
                    {
                        if (token != m_BeatToken) return;   // stopped meanwhile: already answered
                        m_BeatToken++;
                        m_Beat = null;
                        Finish(beat, done, d);
                    }));
                    bool pending = token == m_BeatToken;   // false: it answered inside StartCoroutine (a one-frame beat)
                    m_Beat = pending ? co : null;
                    detail = pending ? $"running beat {c.Arg}" : LastDetail;
                    if (pending) m_StateDirty = true;
                    return pending || LastOk;
                default: ok = false; detail = $"unknown command '{c.Cmd}'"; break;
            }
            Finish(c, ok, detail);
            return ok;
        }

        void CancelBeat(string why)
        {
            if (m_Beat == null) return;
            StopCoroutine(m_Beat);
            m_Beat = null;
            m_BeatToken++;
            if (Services.TryGet<ToolInputHub>(out var hub)) hub.ClearOverrides();
            Finish(m_BeatCmd, false, why);
        }

        bool Hint(out string detail)
        {
            if (!GuideRail.Enabled) GuideRail.Enabled = true;   // a hint lives on the status line
            if (!Services.TryGet<CoachService>(out var coach)) { detail = "no coach in the scene"; return false; }
            coach.ShowNow();
            detail = Services.TryGet<GuideRail>(out var rail) ? $"hint at {rail.Current.Rule}: \"{rail.Current.Status}\"" : "hint requested";
            return true;
        }

        static bool Enter(out string detail)
        {
            switch (AppState.Mode)
            {
                case AppMode.Passthrough: AppCommands.OpenChest(); detail = "entering the world"; return true;
                case AppMode.Tabletop: AppCommands.StepIn(); detail = "stepping into the model"; return true;
                default: detail = "already in the world"; return true;
            }
        }

        static bool Exit(out string detail)
        {
            if (AppState.Mode == AppMode.Passthrough) { detail = "already in the room"; return true; }
            AppCommands.CloseChest();
            detail = "back to the room";
            return true;
        }

        void Finish(PresenterCommand c, bool ok, string detail)
        {
            LastCommand = c; LastOk = ok; LastDetail = detail ?? ""; HasLast = true;
            Log.Info($"Presenter: {c} → {(ok ? "ok" : "failed")} {detail}");
            Ack(c, ok, detail);
            m_StateDirty = true;
        }

        // ---------------- HTTP ----------------

        // fix-ux: every relay call runs on our clock (HttpDeadline): on 2026-09-26 the first poll of three headset
        // sessions never answered nor failed (its 3 s timeout never fired), so "not reachable" was never even logged.
        void Poll()
        {
            var req = UnityWebRequest.Get(BaseUrl + "/presenter/next?max=4");
            req.timeout = timeoutSeconds;
            m_Poll = req;
            StartCoroutine(PollRoutine(req));
        }

        System.Collections.IEnumerator PollRoutine(UnityWebRequest req)
        {
            yield return HttpDeadline.Send(req, HttpDeadline.SmallIdle, "GET /presenter/next");
            if (m_Poll != req) { req.Dispose(); yield break; }
            m_Poll = null;
            Polls++;
            bool ok = HttpDeadline.Ok(req);
            SetOnline(ok, HttpDeadline.Error(req));
            if (ok)
                foreach (var c in PresenterCommands.ParseNext(req.downloadHandler.text)) { Received++; m_Todo.Enqueue(c); }
            m_NextPoll = Time.unscaledTime + (ok ? pollSeconds : offlineRetrySeconds);
            req.Dispose();
        }

        void SetOnline(bool ok, string error)
        {
            Online = ok;
            if (ok == m_WasOnline && m_Reported) return;
            m_WasOnline = ok;
            m_Reported = true;
            Log.Info(ok ? $"Presenter relay connected: {BaseUrl}" : $"Presenter relay not reachable at {BaseUrl} ({error}); trying every {offlineRetrySeconds:0} s");
        }

        void PostState()
        {
            var req = Post("/state", StateJson());
            m_State = req;
            m_StateDirty = false;
            StartCoroutine(StateRoutine(req));
        }

        System.Collections.IEnumerator StateRoutine(UnityWebRequest req)
        {
            yield return HttpDeadline.Send(req, HttpDeadline.SmallIdle, "POST /state");
            if (m_State == req) m_State = null;
            if (HttpDeadline.Ok(req)) StatePosts++;
            m_NextState = Time.unscaledTime + (Online ? stateSeconds : offlineRetrySeconds);
            req.Dispose();
        }

        void Ack(PresenterCommand c, bool ok, string detail)
        {
            if (!Application.isPlaying || string.IsNullOrEmpty(c.Id) || c.Id.StartsWith("local", StringComparison.Ordinal)) return;
            var body = new JObject { ["id"] = c.Id, ["ok"] = ok, ["detail"] = detail ?? "" }.ToString(Newtonsoft.Json.Formatting.None);
            var req = Post("/presenter/ack", body);
            if (!isActiveAndEnabled) { req.Dispose(); return; }   // no coroutine without an active object: the relay times the command out
            StartCoroutine(AckRoutine(req));
        }

        System.Collections.IEnumerator AckRoutine(UnityWebRequest req)
        {
            yield return HttpDeadline.Send(req, HttpDeadline.SmallIdle, "POST /presenter/ack");
            if (HttpDeadline.Ok(req)) Acks++;
            req.Dispose();
        }

        UnityWebRequest Post(string path, string json) =>
            new UnityWebRequest(BaseUrl + path, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)) { contentType = "application/json" },
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = timeoutSeconds,
            };

        // ---------------- status ----------------

        /// What the page shows (and a future big-screen view reads): POST /state.
        public string StateJson()
        {
            Services.TryGet<GuideRail>(out var rail);
            AppSnapshot s = rail == null ? default : GuideRail.Enabled ? rail.Snapshot : rail.Assemble();
            Step step = default;
            if (rail != null)
            {
                step = NextStep.For(s);
                if (step.Frozen) step = rail.Current;
            }
            Services.TryGet<SceneRoot>(out var root);
            Services.TryGet<CoachService>(out var coach);
            var o = new JObject
            {
                ["v"] = 1,
                ["app"] = "airtools",
                ["source"] = "headset",
                ["sid"] = SessionInfo.Id,
                ["t"] = Math.Round(Time.realtimeSinceStartup, 1),
                ["demo"] = DemoMode.On,
                ["rail"] = GuideRail.Enabled,
                ["mode"] = AppState.Mode.ToString(),
                ["transitioning"] = s.Transitioning,
                ["site"] = root == null ? null : root.IsRuntimePackage ? root.Site : "built-in",
                ["scale_ok"] = s.ScaleCalibrated,
                ["tool"] = s.Tool.ToString(),
                ["rule"] = rail != null ? step.Rule.ToString() : null,
                ["status"] = step.Status,
                ["primary"] = step.Primary.IsNone ? null : step.Primary.Label,
                ["coach"] = coach != null && !string.IsNullOrEmpty(coach.Text) ? coach.Text : null,
                ["beat"] = rail != null ? PresenterCommands.BeatOf(step.Rule, AppState.Mode, s.EverEnteredWorld) : null,
                ["beat_running"] = m_Beat != null ? m_BeatCmd.Arg : null,
                ["judge"] = new JObject
                {
                    ["actions"] = JudgeInputs,
                    ["ring_opens"] = s.RingOpens,
                    ["tapes"] = s.Shapes,
                    ["levels"] = s.Levels,
                    ["parts"] = s.PlacedCount,
                    ["notebook"] = Notebook.Entries.Count,
                    ["purchases"] = s.Purchases,
                    ["idle_s"] = Math.Round(s.IdleSeconds, 1),
                },
                ["checkout"] = s.Checkout.ToString(),
                ["resets"] = DemoReset.Count,
                ["last_command"] = HasLast ? new JObject { ["id"] = LastCommand.Id, ["cmd"] = LastCommand.Cmd, ["arg"] = LastCommand.Arg, ["ok"] = LastOk, ["detail"] = LastDetail } : null,
                ["last_error"] = LastError,
                ["last_error_age_s"] = LastError != null ? Math.Round(Time.unscaledTime - m_LastErrorAt, 0) : (double?)null,
                ["fps"] = Math.Round(m_Fps),
            };
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        void OnLog(string condition, string stack, LogType type)
        {
            if (type == LogType.Log) return;
            if (type == LogType.Warning && (condition == null || !condition.StartsWith("[AirTools]", StringComparison.Ordinal))) return;
            if (condition != null && condition.StartsWith("[AirTools] Presenter:", StringComparison.Ordinal)) return;   // our own refusals
            LastError = condition == null ? type.ToString() : condition.Length > 160 ? condition.Substring(0, 160) + "…" : condition;
            m_LastErrorAt = Time.unscaledTime;
        }

        void OnReset(string report)
        {
            if (m_Beat != null) CancelBeat("stopped by a reset");   // e.g. the reset gesture while a beat waited for a search
            JudgeInputs = 0;
            LastError = null;
            m_StateDirty = true;
        }

        void OnPress(ToolHand hand, Pose pose) { if (m_Beat == null) JudgeInputs++; }

        void OnFeedback(Feedback f) { if (f == Feedback.Press && m_Beat == null) JudgeInputs++; }
    }
}
