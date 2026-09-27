using System;
using System.Collections;
using System.Collections.Generic;
using AirTools.Core;
using AirTools.Parts;
using AirTools.UI;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace AirTools.Agent
{
    /// The quartermaster (backend docs/api.md §3–§6): POST /agent/command {session_id, text, context} and
    /// POST /voice/command (multipart: audio, session_id, context, tts) → {reply, actions[], job_id, transcript,
    /// audio_b64}. Shows the transcript and reply, plays the spoken reply, then runs actions[] through AgentActions
    /// exactly like button taps. The session id stays constant for the app run (SessionInfo).
    public class AgentClient : MonoBehaviour
    {
        [Tooltip("Plays the spoken reply (voice commands).")]
        public AudioSource voice;
        public int textTimeoutSeconds = 30;
        public int voiceTimeoutSeconds = 45;

        public bool Busy { get; private set; }
        public string LastTranscript { get; private set; }
        public string LastReply { get; private set; }
        public string LastError { get; private set; }
        /// HTTP status of the last request (0 = no response); display copy classifies errors with it.
        public long LastHttpCode { get; private set; } = -1;
        public List<AgentAction> LastActions { get; private set; } = new List<AgentAction>();
        public int Requests { get; private set; }

        /// (transcript or typed text, reply) whenever a command completes — the wrist/transcript pill listens.
        public event Action<string, string> Replied;

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);

        // ---------------- text ----------------

        public void SendText(string text, Action<AgentReply> done = null) => StartCoroutine(TextRoutine(text, done));

        [Tooltip("Longest wait (s) before a command for the notebook sync and, for \"what am I looking at?\"-type commands, the view's photo.")]
        public float preflightSeconds = 2f;

        /// The context sent with the last command (the harness and tests read it).
        public Dictionary<string, object> LastContext { get; private set; }

        /// Before a command: new notebook entries go to the server (the report / packet / booth read them), and a
        /// command that reads the view's photo gets it downloaded — both bounded by preflightSeconds.
        IEnumerator Preflight(string text)
        {
            float until = Time.realtimeSinceStartup + preflightSeconds;
            var flush = Services.TryGet<AirTools.Notes.NotebookController>(out var nb) ? nb.Flush() : null;
            bool wantFrame = AirTools.Agent.Grok.GrokView.WantsFrame(text);
            while (Time.realtimeSinceStartup < until)
            {
                bool frameReady = !wantFrame || HasFrame();
                if ((flush == null || flush.Done) && frameReady) yield break;
                yield return null;
            }
        }

        static bool HasFrame()
        {
            if (AppState.Mode == AppMode.Passthrough) return true;   // no capture photo is sent in passthrough
            if (!Services.TryGet<AirTools.Scene.SceneStreamer>(out var s) || !Services.TryGet<AirTools.Scene.SceneRoot>(out var root) || root.Content == null || !root.IsRuntimePackage) return true;
            var cam = AirTools.Agent.Grok.GrokView.ViewCamera(s, root.Content.transform);
            return cam == null || string.IsNullOrEmpty(cam.thumb) || s.TryGetThumbBytes(cam.thumb, out _);
        }

        IEnumerator TextRoutine(string text, Action<AgentReply> done)
        {
            Busy = true; LastError = null; Requests++;
            yield return Preflight(text);
            LastContext = AgentContext.Build(null, text);
            string site = AirTools.Scene.SiteScope.Current;   // switchclean: the model this command's context described
            var body = new Dictionary<string, object>
            {
                ["session_id"] = SessionInfo.Id, ["text"] = text ?? "", ["context"] = LastContext, ["wait_s"] = 0,
            };
            AgentReply reply = null;
            using (var req = PartsClient.Post("/agent/command", JsonConvert.SerializeObject(body)))
            {
                req.timeout = textTimeoutSeconds;
                // fix-ux: our clock (the server thinks: no idle limit, its timeout + 1 s).
                yield return HttpDeadline.Send(req, idleSeconds: 0f, label: "POST /agent/command");
                if (HttpDeadline.Ok(req))
                {
                    try { reply = JsonConvert.DeserializeObject<AgentReply>(req.downloadHandler.text); }
                    catch (Exception ex) { LastError = $"agent reply unreadable: {ex.Message}"; }
                }
                else LastError = $"/agent/command: {HttpDeadline.Error(req)} {PartsClient.ServerDetail(req.downloadHandler?.text)}";
                LastHttpCode = req.responseCode;
            }
            Busy = false;
            Handle(text, reply, site);   // switchclean: + site
            done?.Invoke(reply);
        }

        // ---------------- voice ----------------

        /// wav: a complete RIFF/WAVE file. frames: optional scene photos for "what is this?" questions.
        public void SendVoice(byte[] wav, IList<Dictionary<string, string>> frames = null, Action<VoiceReply> done = null) =>
            StartCoroutine(VoiceRoutine(wav, frames, done));

        IEnumerator VoiceRoutine(byte[] wav, IList<Dictionary<string, string>> frames, Action<VoiceReply> done)
        {
            Busy = true; LastError = null; Requests++;
            yield return Preflight(null);
            LastContext = AgentContext.Build(frames);
            string site = AirTools.Scene.SiteScope.Current;   // switchclean
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("audio", wav, "command.wav", "audio/wav"),
                new MultipartFormDataSection("session_id", SessionInfo.Id),
                new MultipartFormDataSection("context", JsonConvert.SerializeObject(LastContext)),
                new MultipartFormDataSection("tts", "true"),
            };
            VoiceReply reply = null;
            using (var req = UnityWebRequest.Post(PartsClient.Resolve("/voice/command"), form))
            {
                req.timeout = voiceTimeoutSeconds;
                yield return HttpDeadline.Send(req, idleSeconds: 0f, label: "POST /voice/command");   // fix-ux: our clock
                if (HttpDeadline.Ok(req))
                {
                    try { reply = JsonConvert.DeserializeObject<VoiceReply>(req.downloadHandler.text); }
                    catch (Exception ex) { LastError = $"voice reply unreadable: {ex.Message}"; }
                }
                else
                {
                    // 503 = speech-to-text unavailable (OFFLINE mode, no Groq key): say so plainly.
                    LastHttpCode = req.responseCode;
                    LastError = req.responseCode == 503
                        ? "Voice is offline on the laptop (no speech-to-text) — use the buttons"
                        : $"/voice/command: {HttpDeadline.Error(req)} {PartsClient.ServerDetail(req.downloadHandler?.text)}";
                }
            }
            Busy = false;
            if (reply != null)
            {
                LastTranscript = reply.transcript;
                if (Stale(site)) { }   // switchclean: an answer about the model you left isn't spoken either
                else if (!string.IsNullOrEmpty(reply.audio_b64)) yield return Speak(reply.audio_b64, reply.audio_mime);
                else if (!string.IsNullOrEmpty(reply.tts_error)) Log.Warn($"Voice reply had no audio: {reply.tts_error}");
            }
            Handle(reply?.transcript, reply, site);   // switchclean: + site
            done?.Invoke(reply);
        }

        IEnumerator Speak(string b64, string mime)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(b64); }
            catch (FormatException) { yield break; }
            AudioClip clip = null;
            yield return WavUtil.Decode(bytes, mime, c => clip = c);
            if (clip == null) { Log.Warn($"Couldn't decode the spoken reply ({mime}, {bytes.Length} bytes)"); yield break; }
            if (voice == null) voice = gameObject.AddComponent<AudioSource>();
            voice.spatialBlend = 0f;
            voice.clip = clip;
            voice.Play();
        }

        // ---------------- observe (B1) ----------------

        /// Reports sent to /agent/observe, and the last body / reply (the harness logs them).
        public int Observations { get; private set; }
        public string LastObserveBody { get; private set; }
        public string LastObserveReply { get; private set; }
        public long LastObserveCode { get; private set; } = -1;
        /// Observe round trips finished (answered or failed).
        public int ObserveReplies { get; private set; }
        public VoiceReply LastObserve { get; private set; }

        /// POST /agent/observe (hand-off §3–§4): what the headset's tools measured for the agent (a survey or a slope).
        /// The reply is handled like any agent reply: spoken (audio_b64 when tts), shown, its actions run (show_survey,
        /// add_note).
        public void Observe(Newtonsoft.Json.Linq.JObject body, Action<VoiceReply> done = null)
        {
            if (body == null) return;
            Observations++;
            LastObserveBody = body.ToString(Formatting.None);
            if (!Application.isPlaying) { done?.Invoke(null); return; }
            StartCoroutine(ObserveRoutine(LastObserveBody, (string)body["kind"], done));
        }

        IEnumerator ObserveRoutine(string json, string kind, Action<VoiceReply> done)
        {
            VoiceReply reply = null;
            string site = AirTools.Scene.SiteScope.Current;   // switchclean: the model the tools measured on
            using (var req = PartsClient.Post("/agent/observe", json))
            {
                req.timeout = voiceTimeoutSeconds;
                yield return HttpDeadline.Send(req, idleSeconds: 0f, label: "POST /agent/observe");   // fix-ux: our clock
                LastObserveCode = req.responseCode;
                if (HttpDeadline.Ok(req))
                {
                    LastObserveReply = req.downloadHandler.text;
                    try { reply = JsonConvert.DeserializeObject<VoiceReply>(req.downloadHandler.text); }
                    catch (Exception ex) { LastError = $"observe reply unreadable: {ex.Message}"; }
                }
                else LastError = $"/agent/observe: {HttpDeadline.Error(req)} {PartsClient.ServerDetail(req.downloadHandler?.text)}";
            }
            if (reply != null)
            {
                if (Stale(site)) { }   // switchclean
                else if (!string.IsNullOrEmpty(reply.audio_b64)) yield return Speak(reply.audio_b64, reply.audio_mime);
                else if (!string.IsNullOrEmpty(reply.tts_error)) Log.Warn($"Observe reply had no audio: {reply.tts_error}");
            }
            LastObserve = reply;
            ObserveReplies++;
            Handle($"({kind ?? "observe"})", reply, site);   // switchclean: + site
            done?.Invoke(reply);
        }

        // ---------------- reply ----------------

        // switchclean: a reply is about the model its command's context described. When another model was loaded while
        // it was on its way (a wheel pick, show_model, Next scene…), none of it lands here: no answer card or spoken line,
        // no actions (a measure_edges / check_slope tape, a survey, pins, a part, a card) on a scan they weren't for.
        // show_model / next_model still run (they are the switch the person asked for). Nothing is retried.

        /// Replies dropped because the model changed while they were on their way.
        public int StaleReplies { get; private set; }

        static bool Stale(string site) => site != null && !AirTools.Scene.SiteScope.IsCurrent(site);

        void Handle(string said, AgentReply reply, string site = null)
        {
            if (Stale(site))
            {
                StaleReplies++;
                var acts = reply?.actions ?? new List<AgentAction>();
                var still = acts.FindAll(a => a != null && AirTools.SwitchClose.RunsAfterSwitch(a.name));
                LastReply = reply?.reply;
                LastActions = acts;
                Log.Info($"Agent: \"{said}\" answered after {site} was left (now {AirTools.Scene.SiteScope.Current}): reply \"{reply?.reply}\" " +
                         $"and {acts.Count - still.Count} action(s) dropped [{string.Join("; ", acts)}]");
                if (reply != null && still.Count == 0)
                    UiToast.Show($"That answer was about {AirTools.Scene.ModelSites.Name(site)} · ask again here", ColorRole.Info);
                if (still.Count > 0) AgentActions.ExecuteAll(still, null);
                return;
            }
            if (reply == null)
            {
                // e2e: a typed replace-flow phrase ("take out the dishwasher", "next one") still works with the laptop's
                // agent down (a spoken one has no transcript without the server).
                var offline = LocalIntents.For(said, null);
                if (offline.RunsLocal)
                {
                    Log.Warn($"Agent: {LastError ?? "no reply"}; \"{said}\" done on the headset");
                    LocalIntents.Run(offline.Local);
                    return;
                }
                string e = LastError ?? "no reply";
                UiToast.Show(LastError == null ? "No reply · try again" : Copy.Error(ErrorSurface.Voice, e, LastHttpCode), ColorRole.Warning);
                Log.Warn($"Agent: {e}");   // raw (hcheck.py voice.talk)
                return;
            }
            LastReply = reply.reply;
            LastActions = reply.actions ?? new List<AgentAction>();
            Log.Info($"Agent: \"{said}\" → \"{reply.reply}\" actions [{string.Join("; ", LastActions)}]");
            // e2e: the replace flow's phrases the server didn't act on are done here (LocalIntents: after the reply's
            // actions; its heads-up line replaces a reply that didn't do what was asked).
            var plan = LocalIntents.For(said, LastActions);
            if (!string.IsNullOrEmpty(reply.reply) && !plan.RunsLocal) UiToast.Reply(Copy.Clip(Copy.Clean(reply.reply), 80));
            Replied?.Invoke(said, reply.reply);
            // switchclean: a switch in the reply that starts a job, after its job_started, is the job's own.
            AirTools.Agent.Grok.GrokRails.Site.NoteIssued(plan.Actions, Time.realtimeSinceStartupAsDouble, afterJobStarted: true);
            AgentActions.ExecuteAll(plan.Actions, reply.reply);
            if (plan.RunsLocal) LocalIntents.Run(plan.Local);   // e2e
            // A slow job's result can lead this reply's actions (show_rules / show_video from the last command) with its
            // own spoken line: caption it with this reply, after the actions ran, so neither line is lost.
            string caption = AirTools.Agent.Grok.GrokReplies.Caption(reply.reply, LastActions);
            if (caption != null) UiToast.Reply(Copy.Clip(Copy.Clean(caption), 140));
        }
    }
}
