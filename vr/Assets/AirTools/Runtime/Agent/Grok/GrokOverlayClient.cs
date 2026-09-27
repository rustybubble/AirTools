using System;
using System.Collections;
using AirTools.Core;
using AirTools.Parts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Agent.Grok
{
    /// The G2 overlays' HTTP calls (backend docs/api.md §4), on the parts server's base URL through PartsClient's helpers:
    /// GET /scene/survey/{id} (a survey that didn't arrive in the reply), GET /scenes/{site}/labels (the pre-labelled
    /// scan), GET /scenes/{site}/coverage (harness), and POST /voice/speak (a polled survey's `spoken`, played the way a
    /// /voice/command reply is: WavUtil.Decode on the agent's voice source). Never /checkout.
    public static class GrokOverlayClient
    {
        /// GET a JSON body: done(http code (0 = no answer), the body or null).
        public static IEnumerator GetJson(string path, int timeoutSeconds, Action<long, JObject> done)
        {
            long code;
            string text = null;
            using (var req = UnityWebRequest.Get(PartsClient.Resolve(path)))
            {
                req.timeout = timeoutSeconds;
                yield return HttpDeadline.Send(req, HttpDeadline.SmallIdle);   // fix-ux: small JSON (labels, survey, coverage) on our clock
                code = HttpDeadline.GaveUp(req) != null ? 0 : req.responseCode;
                if (HttpDeadline.Ok(req)) text = req.downloadHandler.text;
                else if (code != 404) Log.Warn($"Grok overlays: GET {path}: {HttpDeadline.Error(req)} {PartsClient.ServerDetail(req.downloadHandler?.text)}");
            }
            JObject body = null;
            if (!string.IsNullOrEmpty(text))
            {
                try { body = JObject.Parse(text); }
                catch (Exception ex) { Log.Warn($"Grok overlays: GET {path}: unreadable ({ex.Message})"); }
            }
            done?.Invoke(code, body);
        }

        /// Say `text` aloud: POST /voice/speak → audio/wav or audio/mpeg → the same decode and AudioSource the spoken
        /// /voice/command replies use (AgentClient.voice; one is added to the agent if it has none yet). done(played).
        public static IEnumerator Speak(string text, GameObject fallbackHost, Action<bool> done = null)
        {
            if (string.IsNullOrWhiteSpace(text)) { done?.Invoke(false); yield break; }
            byte[] bytes = null;
            string mime = null;
            using (var req = PartsClient.Post("/voice/speak", JsonConvert.SerializeObject(new { text })))
            {
                req.timeout = 20;
                yield return HttpDeadline.Send(req, idleSeconds: 0f);   // fix-ux: our clock (text to speech: no idle limit)
                if (HttpDeadline.Ok(req))
                {
                    bytes = req.downloadHandler.data;
                    mime = req.GetResponseHeader("Content-Type");
                }
                else Log.Warn($"Grok overlays: /voice/speak {req.responseCode}: {HttpDeadline.Error(req)} (the text stays on the reply card)");
            }
            if (bytes == null || bytes.Length == 0) { done?.Invoke(false); yield break; }
            AudioClip clip = null;
            yield return WavUtil.Decode(bytes, mime ?? "audio/wav", c => clip = c);
            if (clip == null) { Log.Warn($"Grok overlays: couldn't decode the spoken line ({mime}, {bytes.Length} bytes)"); done?.Invoke(false); yield break; }
            AudioSource source = null;
            if (Services.TryGet<AgentClient>(out var agent))
            {
                if (agent.voice == null) agent.voice = agent.gameObject.AddComponent<AudioSource>();
                source = agent.voice;
            }
            else if (fallbackHost != null)
            {
                source = fallbackHost.GetComponent<AudioSource>();
                if (source == null) source = fallbackHost.AddComponent<AudioSource>();
            }
            if (source == null) { done?.Invoke(false); yield break; }
            source.spatialBlend = 0f;
            source.clip = clip;
            source.Play();
            done?.Invoke(true);
        }
    }
}
