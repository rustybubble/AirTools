using AirTools.Core;
using AirTools.Parts;
using Newtonsoft.Json;

namespace AirTools.Agent
{
    /// switchclean (job-cancel): tells the laptop a run the headset stopped is over — `POST /job/run/{run_id}/cancel`
    /// {session_id, reason} (backend patch docs/handoff/p4-jobcancel: the run's chain task is cancelled, it ends
    /// `cancelled`, and the session can start a new job at once instead of "a job is already running" for minutes).
    /// GrokRails.CancelForSwitch calls it (its CancelOnServer hook: lane G4's files never POST) when the user switches
    /// models mid-run. Fire and forget, on PartsClient.PostJson (HttpDeadline, a short timeout): the headset has already
    /// stopped the run; an older server without the route (404) or no answer changes nothing on screen.
    public static class JobCancelClient
    {
        public const int TimeoutSeconds = 5;

        /// Requests sent, and the last answer (status 0 = none; the harness logs them).
        public static int Sent { get; private set; }
        public static long LastCode { get; private set; } = -1;
        public static string LastRunId { get; private set; }

        public static string PathFor(string runId) => $"/job/run/{System.Uri.EscapeDataString(runId ?? "")}/cancel";

        public static string Body(string sessionId, string reason) =>
            JsonConvert.SerializeObject(new { session_id = sessionId, reason = string.IsNullOrWhiteSpace(reason) ? null : reason });

        /// POST the cancel for `runId` (nothing without a run id or a PartsClient in the scene).
        public static void Send(string runId, string reason)
        {
            if (string.IsNullOrEmpty(runId) || !Services.TryGet<PartsClient>(out var client) || client == null) return;
            Sent++;
            LastRunId = runId;
            LastCode = -1;
            client.PostJson(PathFor(runId), Body(SessionInfo.Id, reason), TimeoutSeconds, (code, body) =>
            {
                LastCode = code;
                if (code >= 200 && code < 300) Log.Info($"Job {runId}: the server's run is cancelled ({body})");
                else if (code == 404) Log.Info($"Job {runId}: the server has no such run or no cancel route (an older server): its run finishes on its own");
                else Log.Warn($"Job {runId}: cancel on the server didn't go through (HTTP {code}): its run finishes on its own");
            });
        }

        /// Tests.
        public static void ResetStats() { Sent = 0; LastCode = -1; LastRunId = null; }
    }
}
