using System.Collections;
using AirTools.Core;
using AirTools.Parts;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Agent.Grok
{
    /// Polls "do the whole job" (docs/api.md `GET /job/run/{run_id}?after=n`) while GrokRails.Poll says so, and runs each
    /// page's actions through AgentActions.ExecuteAll in order: this lane's job_step / job_done, and the steps' own
    /// actions for the lanes that own them. GET only, on the parts server's base URL (PartsClient.Resolve).
    public class JobRunPoller : MonoBehaviour
    {
        public int timeoutSeconds = 10;

        public int Requests { get; private set; }
        public string LastError { get; private set; }

        void OnEnable() => Services.Register(this);

        void OnDisable()
        {
            Services.Unregister(this);
            GrokRails.Poll.Abandon(Time.realtimeSinceStartupAsDouble);   // a stopped coroutine never answers
        }

        void Update()
        {
            var poll = GrokRails.Poll;
            if (!poll.Due(Time.realtimeSinceStartupAsDouble)) return;
            poll.Sent();
            StartCoroutine(PollOnce(poll.RunId, poll.Path));
        }

        IEnumerator PollOnce(string runId, string path)
        {
            Requests++;
            using (var req = UnityWebRequest.Get(PartsClient.Resolve(path)))
            {
                req.timeout = timeoutSeconds;
                yield return HttpDeadline.Send(req, HttpDeadline.SmallIdle, label: "GET /job/run");   // fix-ux: a small poll on our clock
                double now = Time.realtimeSinceStartupAsDouble;
                if (GrokRails.Poll.RunId != runId || !GrokRails.Poll.InFlight) yield break;   // a new run took over
                if (!HttpDeadline.Ok(req))
                {
                    LastError = $"{path}: {HttpDeadline.Error(req)} ({req.responseCode})";
                    GrokRails.Poll.Failed(req.responseCode, now);
                    Log.Warn($"Rail poll {LastError}{(GrokRails.Poll.Active ? "" : $"; stopped ({GrokRails.Poll.StopReason})")}");
                    yield break;
                }
                var body = GrokRailPayloads.Poll(req.downloadHandler.text);
                var actions = GrokRails.Polled(body, now);
                // switchclean: a switch this page asks for is the job's own (a switch by the person stops it instead).
                if (actions.Count > 0) GrokRails.Site.NoteIssued(actions, now);
                if (actions.Count > 0) AgentActions.ExecuteAll(actions);
                if (!GrokRails.Poll.Active) Log.Info($"Rail poll: run {runId} {GrokRails.Poll.StopReason} after {GrokRails.Poll.Polls} polls");
            }
        }
    }
}
