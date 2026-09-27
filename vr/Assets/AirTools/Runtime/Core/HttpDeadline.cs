using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Core
{
    /// Our own clock on every web request (fix-ux). On the headset (2026-09-26, three sessions) no request ever answered
    /// or failed — not the scene load's scene.json, not the presenter link's 3 s poll, not a parts search — although each
    /// had a UnityWebRequest.timeout: the requests hung and everything waiting on them hung with them. Send() waits for a
    /// request by Time.realtimeSinceStartup and aborts it when it goes `idleSeconds` without a byte moving (small JSON
    /// calls: SmallIdle) or runs past its own timeout + Slack; the abort's reason is kept for the caller (Ok / Error) and
    /// logged as one Warn (at most one per call kind every WarnEverySeconds). Pure parts (Verdict, TotalFor, PathOf,
    /// WarnGate) are EditMode-tested.
    public static class HttpDeadline
    {
        /// No-progress deadline for small JSON calls that answer at once (health, polls, scene.json, notebook sync…).
        public const float SmallIdle = 6f;
        /// No-progress deadline for file downloads (GLBs, pictures): bytes must keep arriving.
        public const float DownloadIdle = 15f;
        /// Our clock allows this much past the request's own timeout.
        public const float Slack = 1f;
        /// A request without a timeout of its own gets this total.
        public const float DefaultTotal = 60f;
        public const float WarnEverySeconds = 30f;

        static float Now => Time.realtimeSinceStartup;

        // ---------------- pure ----------------

        /// Why to give up now, or null to keep waiting. `anyBytes`: something was received. A limit ≤ 0 is off.
        public static string Verdict(float now, float startedAt, float lastProgressAt, bool anyBytes, float idleSeconds, float totalSeconds)
        {
            if (totalSeconds > 0f && now - startedAt >= totalSeconds)
                return anyBytes ? $"not finished in {totalSeconds:0} s" : $"no answer in {totalSeconds:0} s";
            if (idleSeconds > 0f && now - lastProgressAt >= idleSeconds)
                return anyBytes ? $"no data for {idleSeconds:0} s" : $"no answer in {idleSeconds:0} s";
            return null;
        }

        /// The total our clock allows a request: its own timeout + Slack, or `fallback` when it has none.
        public static float TotalFor(int timeoutSeconds, float fallback = DefaultTotal) => timeoutSeconds > 0 ? timeoutSeconds + Slack : fallback;

        /// "http://127.0.0.1:8004/parts/jobs/abc?x=1" → "/parts/jobs/abc" (the Warn's name for a call).
        public static string PathOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int start = 0;
            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                int slash = url.IndexOf('/', scheme + 3);
                if (slash < 0) return "/";
                start = slash;
            }
            int q = url.IndexOfAny(new[] { '?', '#' }, start);
            return q < 0 ? url.Substring(start) : url.Substring(start, q - start);
        }

        /// At most one Warn per key every `every` seconds (a relay that hangs is polled every few seconds).
        public sealed class WarnGate
        {
            readonly Dictionary<string, float> m_Last = new Dictionary<string, float>();
            public float Every = WarnEverySeconds;

            public bool ShouldWarn(string key, float now)
            {
                key ??= "";
                if (m_Last.TryGetValue(key, out var last) && now - last < Every) return false;
                m_Last[key] = now;
                return true;
            }

            public void Clear() => m_Last.Clear();
        }

        static readonly WarnGate s_Gate = new WarnGate();
        static readonly ConditionalWeakTable<UnityWebRequest, string> s_GaveUp = new ConditionalWeakTable<UnityWebRequest, string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_Gate.Clear();

        // ---------------- results ----------------

        /// Why our clock gave up on `req` ("no answer in 6 s"), or null when it didn't.
        public static string GaveUp(UnityWebRequest req) => req != null && s_GaveUp.TryGetValue(req, out var why) ? why : null;

        /// The request finished by itself and succeeded.
        public static bool Ok(UnityWebRequest req) => req != null && GaveUp(req) == null && req.result == UnityWebRequest.Result.Success;

        /// What went wrong, for a status line or LastError: our clock's reason, else the request's own error.
        public static string Error(UnityWebRequest req) => GaveUp(req) ?? req?.error;

        // ---------------- send ----------------

        /// Send `req` and wait for it by our own clock (yield return it from a coroutine). It is aborted after `idleSeconds`
        /// without a byte moving (≤ 0: no idle limit — calls a server thinks about, like /agent/command) or past
        /// `totalSeconds` (≤ 0: the request's timeout + Slack). `abandon` returning true aborts it quietly (its caller moved
        /// on); `onProgress` hears every change in the bytes moved. `label` names it in the Warn ("GET /parts/jobs").
        public static IEnumerator Send(UnityWebRequest req, float idleSeconds = SmallIdle, string label = null, float totalSeconds = 0f,
                                       Func<bool> abandon = null, Action onProgress = null)
        {
            if (req == null) yield break;
            UnityWebRequestAsyncOperation op;
            try { op = req.SendWebRequest(); }
            catch (Exception ex)
            {
                GiveUp(req, $"couldn't send: {ex.Message}", label, warn: true);
                yield break;
            }
            float total = totalSeconds > 0f ? totalSeconds : TotalFor(req.timeout);
            float t0 = Now, last = t0;
            ulong down = 0, up = 0;
            bool any = false;
            while (!op.isDone)
            {
                ulong d = req.downloadedBytes, u = req.uploadedBytes;
                if (d != down || u != up)
                {
                    down = d; up = u; last = Now;
                    if (d > 0) any = true;
                    onProgress?.Invoke();
                }
                if (abandon != null && abandon()) { GiveUp(req, "abandoned", label, warn: false); yield break; }
                var why = Verdict(Now, t0, last, any, idleSeconds, total);
                if (why != null) { GiveUp(req, why, label, warn: true); yield break; }
                yield return null;
            }
        }

        static void GiveUp(UnityWebRequest req, string why, string label, bool warn)
        {
            s_GaveUp.Remove(req);
            s_GaveUp.Add(req, why);
            try { req.Abort(); } catch (Exception) { }
            if (!warn) return;
            string name = label ?? $"{req.method} {PathOf(req.url)}";
            if (s_Gate.ShouldWarn(name, Now))
                Log.Warn($"HTTP {name}: {why} (our clock; its timeout is {(req.timeout > 0 ? $"{req.timeout} s" : "none")}) · gave up");
        }
    }
}
