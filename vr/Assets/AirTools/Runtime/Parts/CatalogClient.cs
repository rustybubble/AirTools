using System;
using System.Collections;
using AirTools.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Parts
{
    /// catalog: GET /catalog (the place's categories) and GET /catalog/search (the lazy search's laptop half) from the
    /// laptop server (catalog-backend's contract, CatalogData). A newer search aborts the one in flight; the window drops
    /// any reply whose generation is stale anyway. Replies are handed over as raw JSON (the window parses and caches).
    public class CatalogClient : MonoBehaviour
    {
        [Tooltip("Seconds a catalog call may take (the laptop answers from its cache).")]
        public int timeoutSeconds = 6;

        /// Tests and the harness: answer GETs here instead of the network (path + query → status, body). Null = network.
        public Func<string, (long code, string body)> testTransport;

        public string LastError { get; private set; }
        public int Fetches { get; private set; }
        public int Searches { get; private set; }
        /// Searches aborted because a newer one started.
        public int Aborted { get; private set; }

        UnityWebRequest m_Search;

        void OnEnable() => Services.Register(this);

        void OnDisable()
        {
            Services.Unregister(this);
            AbortSearch();
        }

        /// GET /catalog for `site`. done(json or null, error or null).
        public void Fetch(string site, Action<string, string> done) => Fetch(site, CatalogFocus.None, done);

        /// gaze-catalog: GET /catalog for `site` and what the wearer looks at (`focus`; None: the whole place).
        public void Fetch(string site, CatalogFocus focus, Action<string, string> done)
        {
            Fetches++;
            string path = CatalogText.CatalogPath(site, SessionInfo.Id, focus);
            if (Answer(path, done)) return;
            if (!Application.isPlaying) { done?.Invoke(null, "not running"); return; }
            StartCoroutine(Get(path, false, done));
        }

        /// GET /catalog/search for `q` on `site` (a newer call aborts this one). done(json or null, error or null).
        public void Search(string q, string site, int limit, Action<string, string> done)
        {
            Searches++;
            string path = CatalogText.SearchPath(q, site, limit);
            if (Answer(path, done)) return;
            if (!Application.isPlaying) { done?.Invoke(null, "not running"); return; }
            AbortSearch();
            StartCoroutine(Get(path, true, done));
        }

        public void AbortSearch()
        {
            if (m_Search == null) return;
            try { m_Search.Abort(); } catch (Exception) { }
            m_Search = null;
            Aborted++;
        }

        bool Answer(string path, Action<string, string> done)
        {
            if (testTransport == null) return false;
            var (code, body) = testTransport(path);
            bool ok = code >= 200 && code < 300;
            LastError = ok ? null : $"GET {path}: HTTP {code}";
            done?.Invoke(ok ? body : null, LastError);
            return true;
        }

        IEnumerator Get(string path, bool search, Action<string, string> done)
        {
            var req = UnityWebRequest.Get(PartsClient.Resolve(path));
            req.timeout = timeoutSeconds;
            if (search) m_Search = req;
            using (req)
            {
                yield return HttpDeadline.Send(req, HttpDeadline.SmallIdle);
                if (search && ReferenceEquals(m_Search, req)) m_Search = null;
                if (HttpDeadline.Ok(req))
                {
                    LastError = null;
                    done?.Invoke(req.downloadHandler.text, null);
                }
                else
                {
                    LastError = $"GET {path}: {HttpDeadline.Error(req)} ({req.responseCode})";
                    done?.Invoke(null, LastError);
                }
            }
        }
    }
}
