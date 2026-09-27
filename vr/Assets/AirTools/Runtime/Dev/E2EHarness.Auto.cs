#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Core;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Dev
{
    /// autonomy: one sentence → the whole replace, autonomously, driven by the backend's Grok agent (server/replace_job.py).
    /// The sentence goes through the real command path — typed (AgentClient.SendText → /agent/command) or a WAV through the
    /// voice path (AgentClient.SendVoice → /voice/command) — and the harness only watches: the reply's job_started starts
    /// the rail and JobRunPoller, whose pages run through AgentActions.ExecuteAll as they arrive. Checks:
    ///   e2e.auto.route    the reply started a replace run (job_started, kind replace), nothing done on the headset
    ///   e2e.auto.job      job_done within JobTimeout, status done (the dots and their spoken lines logged)
    ///   e2e.auto.order    remove_component → measure_cavity → [scale_gap] → search_started → place_part → job_done, each
    ///                     once, none failed (AutoReplace.CheckOrder over AgentActions.Ran)
    ///   e2e.auto.remove   the named part is out, its gap open
    ///   e2e.auto.measure  the gap's tapes are in the notebook
    ///   e2e.auto.scale    scale_gap applied → the calibration changed; skipped → unchanged (the kitchen starts at ×1.63)
    ///   e2e.auto.search   N candidates on Find parts, each with a model URL
    ///   e2e.auto.place    job_done's part stands at the cavity insert (±1 cm), green or amber
    ///   e2e.auto.spoken   job_done's line was said (GrokOverlays.Say) and the chip shows the summary
    ///   e2e.auto.next     "next one" puts another model at the same spot
    ///   e2e.auto.restore  "put it back": the original is in, nothing in the gap
    /// Run: AgentHarness.E2EAuto(site, sentence) / E2EAutoWav(site, wavPath), then poll AgentHarness.E2EResult().
    public static partial class E2EHarness
    {
        /// A fresh search (SerpApi limited → web fallback) plus a new model can take two minutes.
        public const float JobTimeout = 240f;

        public class AutoOptions
        {
            /// The scene package (null: the one loaded).
            public string Site;
            /// What the user says.
            public string Sentence = "replace the dishwasher with a new one that fits";
            /// Say it as this WAV through the voice path instead (the server transcribes it).
            public string WavPath;
            public bool Reset = true;
            /// "next one" and "put it back" after the run.
            public bool FollowUps = true;
        }

        public static string StartAuto(AutoOptions o)
        {
            if (!Application.isPlaying) return "Play mode only";
            o = o ?? new AutoOptions();
            s_Log.Clear(); s_Done = s_Pass = false; s_PassN = s_Total = 0; s_Summary = "running…";
            if (!DemoRunner.Run(AutoFlow(o, (ok, d) => { s_Pass = ok; s_Summary = d; }), ex => { s_Log.Add($"exception: {ex}"); s_Summary = $"exception: {ex.Message}"; },
                    () => s_Done = true))
                return "another routine is running (DemoRunner busy)";
            string said = string.IsNullOrEmpty(o.WavPath) ? $"\"{o.Sentence}\"" : $"WAV {o.WavPath}";
            return $"e2e auto started ({o.Site ?? "loaded site"}, {said}) against {ServerConfig.Current} — poll AgentHarness.E2EResult()";
        }

        static IEnumerator AutoFlow(AutoOptions o, Action<bool, string> done)
        {
            var agent = Services.Get<AgentClient>();
            var browser = Services.Get<PartsBrowser>();
            if (agent == null || browser == null) { done(false, "no AgentClient / PartsBrowser in the scene"); yield break; }
            s_Log.Add($"server {ServerConfig.Current} session {SessionInfo.Id} (autonomous)");

            // ---- 0. a clean start in the world, on the site
            if (o.Reset) { AppCommands.ResetDemo(); yield return null; }
            yield return EnsureWorld();
            var root = Services.Get<SceneRoot>();
            var streamer = Services.Get<SceneStreamer>();
            if (!string.IsNullOrEmpty(o.Site) && (root == null || root.Site != o.Site))
            {
                AppCommands.LoadSite(o.Site);
                float t0 = Now;
                yield return null;
                while (Now - t0 < SiteTimeout && (streamer == null || streamer.Loading || root == null || root.Site != o.Site)) yield return null;
                yield return EnsureWorld();
            }
            var parts = Services.Get<SceneParts>();
            string site = root != null ? root.Site ?? "built-in" : "-";
            if (parts == null || !parts.HasParts)
            {
                Check("e2e.auto.site", false, $"no removable parts in {site} ({streamer?.PartsStatus ?? "no streamer"})");
                done(false, Summary(site));
                yield break;
            }
            if (o.Reset) parts.ResetSession();
            root.SetVisible(true);
            var outBefore = new HashSet<string>(parts.Removable.Where(c => parts.IsRemoved(c.id)).Select(c => c.id));
            float calBefore = root.Calibration;
            int searches = browser.SearchCount, entries = Notebook.Entries.Count, localRuns = LocalIntents.Runs;
            s_Log.Add($"site {site} calibration ×{calBefore:0.####} parts: {parts.Report().Split('\n')[0]}");

            var ran = new List<string>();
            var failed = new HashSet<string>();
            void OnRan(AgentAction a, bool ok) { ran.Add(a?.name); if (!ok && a != null) failed.Add(a.name); }
            AgentActions.Ran += OnRan;
            try
            {
                // ---- 1. the sentence, typed or spoken
                AgentReply reply = null;
                if (!string.IsNullOrEmpty(o.WavPath)) yield return SayWav(o.WavPath, r => reply = r);
                else yield return Say(o.Sentence, r => reply = r);
                string runId = AutoReplace.RunId(reply?.actions);
                var job = GrokRails.Job;
                Check("e2e.auto.route", runId != null && job != null && job.RunId == runId && job.IsReplace && LocalIntents.Runs == localRuns,
                    $"run {runId ?? "none"} \"{job?.Title}\" steps [{(job != null ? string.Join(", ", job.Dots.Select(d => d.Name)) : "-")}] reply \"{reply?.reply}\" local runs +{LocalIntents.Runs - localRuns}");
                if (runId == null) { done(false, Summary(site)); yield break; }

                // ---- 2. the run, as JobRunPoller delivers it
                float j0 = Now;
                while (Now - j0 < JobTimeout && !(GrokRails.Job != null && GrokRails.Job.RunId == runId && GrokRails.Job.Finished)) yield return null;
                job = GrokRails.Job;
                foreach (var d in job.Dots) s_Log.Add($"  {d.Name} {d.Status} {(d.Seconds.HasValue ? $"{d.Seconds.Value:0.0} s" : "")}: {d.Spoken}");
                bool finished = job.RunId == runId && job.Finished;
                Check("e2e.auto.job", finished && job.Status == "done",
                    $"{(finished ? job.Status : "not finished")} after {Now - j0:0.0} s, {GrokRails.Poll.Polls} polls | {GrokRailText.StripTags(GrokRailText.JobStatus(job))} | {GrokRailText.JobStatusDetail(job)}");
                if (!finished) { done(false, Summary(site)); yield break; }
                Check("e2e.auto.order", AutoReplace.CheckOrder(ran, failed, out string order), order);

                // ---- 3. what it did, on the headset
                var comp = parts.Removable.FirstOrDefault(c => parts.IsRemoved(c.id) && !outBefore.Contains(c.id)) ?? parts.LastRemoved;
                var gap = default(Gap);
                bool open = comp != null && Gaps.TryGet(out gap, comp.id);
                Check("e2e.auto.remove", open, $"{comp?.id ?? "nothing"} {comp?.DisplayName} out={comp != null && parts.IsRemoved(comp.id)} | {parts.LastAction}");
                if (!open) { done(false, Summary(site)); yield break; }

                var tapes = Notebook.Entries.Skip(Math.Min(entries, Notebook.Entries.Count)).Where(e => e.Tool == "measure" && (e.DisplayTitle ?? "").Contains(" gap ")).ToList();
                bool taped = Gaps.TryMeasured(comp.id, out var m);
                Check("e2e.auto.measure", taped && tapes.Count >= (gap.Box.OpenTop ? 2 : 3),
                    $"taped W {F(m.x)} D {F(m.z)} H {F(m.y)} m; notebook +{tapes.Count} [{string.Join("; ", tapes.Select(e => e.DisplayTitle))}]");

                var scaleDot = job.Dots.Find(d => d.Name == "scale");
                bool scaled = scaleDot != null && scaleDot.Status == DotStatus.Done;
                float ratio = root.Calibration / Mathf.Max(calBefore, 1e-6f);
                bool scaleOk = scaled ? ran.Contains("scale_gap") && Mathf.Abs(ratio - 1f) > 0.02f : !ran.Contains("scale_gap") && Mathf.Abs(ratio - 1f) < 0.001f;
                Check("e2e.auto.scale", scaleOk, $"{scaleDot?.Status}: \"{scaleDot?.Spoken}\" | calibration ×{calBefore:0.####} → ×{root.Calibration:0.####} (×{ratio:0.###}) | {AirTools.Structure.ScaleCalibration.LastResult}");

                float s0 = Now;
                while (Now - s0 < SearchTimeout && (browser.Searching || browser.SearchCount == searches)) yield return null;
                var cands = browser.Candidates.ToList();
                var gapMm = Gaps.FitSizeM(gap) * 1000f;
                var dims = cands.Select(c => c.spec?.dims_mm ?? c.dims_mm).ToList();
                Check("e2e.auto.search", cands.Count > 0 && cands.All(c => !string.IsNullOrEmpty(c.model_url)),
                    $"N={cands.Count} with models={cands.Count(c => !string.IsNullOrEmpty(c.model_url))} for {CavityFit.MmText(gapMm)}: " +
                    string.Join("; ", cands.Select((c, i) => $"[{i}] {c.id} {Dim(dims[i])} {(CavityFit.Fits(dims[i], gapMm) ? "fits" : "too big")}")));

                yield return WaitModel(gap, null);
                var model = Gaps.ModelIn(gap);
                var expect = ExpectedAnchor(comp.id, root);
                float off = model != null && expect.HasValue ? Vector3.Distance(Anchor(model), expect.Value) : float.NaN;
                var fit = model?.Fit;
                Check("e2e.auto.place", model != null && off < 0.01f && model.Spec.id == job.Done.part_id && fit != null && (fit.Status == FitStatus.Green || fit.Status == FitStatus.Amber),
                    $"{model?.Spec?.id ?? "nothing"} (job_done said {job.Done.part_id}, {job.Done.fits}) at the insert ±{(float.IsNaN(off) ? "-" : (off * 1000f).ToString("0"))} mm \"{AirTools.UI.Copy.FitLine(fit)}\" | {ModelCycler.LastAction}");

                var overlays = Services.Get<GrokOverlays>();
                Check("e2e.auto.spoken", !string.IsNullOrWhiteSpace(job.Done.spoken) && (overlays == null || overlays.LastSpoken == job.Done.spoken),
                    $"said \"{job.Done.spoken}\" | chip \"{GrokRailText.JobSummary(job.Done, job)}\" | {GrokRailText.JobDetail(job.Done, job)}");
                if (model == null || !o.FollowUps) { done(s_PassN == s_Total, Summary(site)); yield break; }

                // ---- 4. the follow-ups keep working
                var spot = Anchor(model);
                if (cands.Count > 1)
                {
                    string before = model.Spec.id;
                    yield return Say("next one");
                    yield return WaitModel(gap, before);
                    var cur = Gaps.ModelIn(gap);
                    float moved = cur != null ? Vector3.Distance(Anchor(cur), spot) : float.NaN;
                    Check("e2e.auto.next", cur != null && cur.Spec.id != before && moved < 0.01f, $"{before} → {cur?.Spec?.id ?? "nothing"} ±{(float.IsNaN(moved) ? "-" : (moved * 1000f).ToString("0"))} mm via {s_Via} | {SwapNote()}");
                }
                yield return Say("put it back");
                yield return null;
                Check("e2e.auto.restore", !parts.IsRemoved(comp.id) && PlacedInGap(gap) == 0, $"{comp.id} in={!parts.IsRemoved(comp.id)} models left={PlacedInGap(gap)} via {s_Via}");
                done(s_PassN == s_Total, Summary(site));
            }
            finally { AgentActions.Ran -= OnRan; }
        }

        /// A WAV through the voice path (the server transcribes it; Groq Whisper, else xAI grok-transcribe).
        static IEnumerator SayWav(string path, Action<AgentReply> got)
        {
            var agent = Services.Get<AgentClient>();
            if (!File.Exists(path)) { s_Log.Add($"no WAV at {path}"); got(null); yield break; }
            byte[] wav = File.ReadAllBytes(path);
            float t0 = Now;
            while (agent.Busy && Now - t0 < ReplyTimeout) yield return null;
            bool done = false;
            VoiceReply reply = null;
            t0 = Now;
            agent.SendVoice(wav, null, r => { reply = r; done = true; });
            while (!done && Now - t0 < ReplyTimeout) yield return null;
            yield return null;
            string acts = reply?.actions != null ? string.Join(",", reply.actions.Select(a => a?.name)) : "-";
            s_Log.Add($"> WAV {Path.GetFileName(path)} heard \"{reply?.transcript?.Trim()}\" → \"{AirTools.UI.Copy.Clip(reply?.reply ?? "", 90)}\" [{acts}] ({Now - t0:0.0} s)" +
                      (!done ? " TIMED OUT" : reply == null ? $" no reply: {agent.LastError}" : ""));
            got(reply);
        }
    }
}
#endif
