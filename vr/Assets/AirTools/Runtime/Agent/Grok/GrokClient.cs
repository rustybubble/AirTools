using System;
using System.Collections;
using System.Collections.Generic;
using AirTools.Core;
using AirTools.Parts;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Agent.Grok
{
    /// The G3 panels' HTTP calls, on the parts server's base URL and helpers (PartsClient.Resolve / Post / PostJson):
    /// GET /parts/{id}/safety (the recall link), GET /scene/flythrough/{job_id}, GET /rules/check/{id},
    /// POST /booth/x/confirm (only from the hold-to-post), and the pictures (postcard, reimagine, poster, booth card).
    /// Never calls /checkout.
    public class GrokClient : MonoBehaviour
    {
        public int timeoutSeconds = 15;
        [Tooltip("Pictures kept for the before/after flip (least recently used go first).")]
        public int textureCache = 12;

        public int Requests { get; private set; }
        public string LastError { get; private set; }
        public string LastUrl { get; private set; }
        public int BoothConfirms { get; private set; }

        readonly Dictionary<string, Texture2D> m_Textures = new Dictionary<string, Texture2D>();
        readonly List<string> m_TextureOrder = new List<string>();

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);

        void OnDestroy()
        {
            foreach (var t in m_Textures.Values) if (t != null) Destroy(t);
            m_Textures.Clear();
        }

        /// GET a JSON path; done(status, body) — status 0 when nothing answered.
        public void GetJson(string path, Action<long, string> done)
        {
            if (!Application.isPlaying) { done?.Invoke(0, null); return; }
            StartCoroutine(GetRoutine(path, done));
        }

        IEnumerator GetRoutine(string path, Action<long, string> done)
        {
            Requests++;
            LastUrl = PartsClient.Resolve(path);
            using var req = UnityWebRequest.Get(LastUrl);
            req.timeout = timeoutSeconds;
            // fix-ux: our clock; some of these GETs are computed (safety, rules, quotes): no idle limit, timeout + 1 s.
            yield return HttpDeadline.Send(req, idleSeconds: 0f);
            LastError = HttpDeadline.Ok(req) ? null : $"GET {path}: {HttpDeadline.Error(req)}";
            if (LastError != null) Log.Warn($"G3 {LastError}");
            done?.Invoke(req.responseCode, req.downloadHandler?.text);
        }

        /// POST JSON through the parts client's helper (its test transport included); done(status, body).
        public void PostJson(string path, string json, Action<long, string> done)
        {
            Requests++;
            LastUrl = PartsClient.Resolve(path);
            if (Services.TryGet<PartsClient>(out var parts)) { parts.PostJson(path, json, timeoutSeconds, done); return; }
            if (!Application.isPlaying) { done?.Invoke(0, null); return; }
            StartCoroutine(PostRoutine(path, json, done));
        }

        IEnumerator PostRoutine(string path, string json, Action<long, string> done)
        {
            using var req = PartsClient.Post(path, json);
            req.timeout = timeoutSeconds;
            yield return HttpDeadline.Send(req, idleSeconds: 0f);   // fix-ux: our clock
            LastError = HttpDeadline.Ok(req) ? null : $"POST {path}: {HttpDeadline.Error(req)}";
            done?.Invoke(req.responseCode, req.downloadHandler?.text);
        }

        /// GET /parts/{id}/safety → the SafetyReport (null on failure).
        public void Safety(string partId, Action<JObject> done) =>
            GetJson($"/parts/{Uri.EscapeDataString(partId ?? "")}/safety", (code, body) => done?.Invoke(code >= 200 && code < 300 ? Parse(body) : null));

        /// POST /booth/x/confirm {confirm_token}: only HoldToConfirm.Confirmed calls this (never voice, never a tap).
        public void BoothConfirm(string token, Action<long, string> done)
        {
            BoothConfirms++;
            Log.Info("G3 booth: hold-to-post completed → POST /booth/x/confirm");
            PostJson("/booth/x/confirm", new JObject { ["confirm_token"] = token }.ToString(Newtonsoft.Json.Formatting.None), done);
        }

        public static JObject Parse(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try { return JObject.Parse(body); }
            catch (Newtonsoft.Json.JsonException) { return null; }
        }

        /// A picture by URL (server-relative or absolute), cached; done(null) on failure.
        public void Texture(string url, Action<Texture2D> done)
        {
            var abs = PartsClient.Resolve(url);
            if (string.IsNullOrEmpty(abs)) { done?.Invoke(null); return; }
            if (m_Textures.TryGetValue(abs, out var cached) && cached != null)
            {
                m_TextureOrder.Remove(abs);
                m_TextureOrder.Add(abs);
                done?.Invoke(cached);
                return;
            }
            if (!Application.isPlaying) { done?.Invoke(null); return; }
            StartCoroutine(TextureRoutine(abs, done));
        }

        IEnumerator TextureRoutine(string abs, Action<Texture2D> done)
        {
            Requests++;
            LastUrl = abs;
            using var req = UnityWebRequestTexture.GetTexture(abs, nonReadable: true);
            req.timeout = Math.Max(timeoutSeconds, 20);
            yield return HttpDeadline.Send(req, idleSeconds: HttpDeadline.DownloadIdle);   // fix-ux: a picture: bytes must keep coming
            if (!HttpDeadline.Ok(req))
            {
                LastError = $"GET {abs}: {HttpDeadline.Error(req)}";
                Log.Warn($"G3 picture: {LastError}");
                done?.Invoke(null);
                yield break;
            }
            var tex = DownloadHandlerTexture.GetContent(req);
            tex.name = abs;
            tex.wrapMode = TextureWrapMode.Clamp;
            m_Textures[abs] = tex;
            m_TextureOrder.Remove(abs);
            m_TextureOrder.Add(abs);
            while (m_TextureOrder.Count > Mathf.Max(2, textureCache))
            {
                var old = m_TextureOrder[0];
                m_TextureOrder.RemoveAt(0);
                if (m_Textures.TryGetValue(old, out var t) && t != null) Destroy(t);
                m_Textures.Remove(old);
            }
            done?.Invoke(tex);
        }
    }
}
