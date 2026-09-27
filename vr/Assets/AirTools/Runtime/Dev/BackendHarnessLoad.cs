#if UNITY_EDITOR || DEVELOPMENT_BUILD
using AirTools.Core;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Dev
{
    /// fix-ux: the scene-load watchdog, live. LoadKill starts a load of the start site from the built-in scene and stops
    /// the streamer's coroutines at once — what Unity does to a deactivated object: the iterator's finally never runs —
    /// so before the fix "Loading the site…" stayed forever. Within `stall` seconds Loading is false, LoadFailed is set,
    /// a Warn names the stage, and the rail (AgentHarness.NextStep) reads "Couldn't load the kitchen · Retry";
    /// AgentHarness.DoNext(0) presses Retry. LoadWatch prints the watchdog.
    public static partial class BackendHarness
    {
        public static string LoadKill(float stall = 3f, bool builtInFirst = true)
        {
            var s = Streamer;
            if (s == null) return "no streamer";
            if (builtInFirst) s.LoadBuiltIn();
            float old = s.loadStallSeconds;
            s.loadStallSeconds = stall;   // read once when the load begins
            bool ok = s.Load(s.StartSite);
            s.loadStallSeconds = old;
            s.StopAllCoroutines();
            Log.Info($"BackendHarness.LoadKill: load of {s.StartSite} {(ok ? "started and its coroutine stopped" : "refused")}; the watchdog gives up after {stall:0.#} s");
            return $"{(ok ? "killed" : "refused")} | {LoadWatch()}";
        }

        public static string LoadWatch()
        {
            var s = Streamer;
            if (s == null) return "no streamer";
            var w = s.Watchdog;
            float now = Time.realtimeSinceStartup;
            return $"loading={s.Loading} active={w.Active} gen={w.Generation} stage=\"{w.Stage}\" " +
                   $"since={(w.StartedAt >= 0f ? now - w.StartedAt : 0f):0.0}s idle={(w.LastProgressAt >= 0f ? now - w.LastProgressAt : 0f):0.0}s " +
                   $"failed={s.LoadFailed} failed_site={s.FailedSite ?? "-"} err=\"{s.LastError}\" " +
                   $"server={ServerConfig.Current} probe={(ServerConfig.Probing ? "running" : ServerConfig.ProbeResult ?? "-")} | {SceneStatus()}";
        }
    }
}
#endif
