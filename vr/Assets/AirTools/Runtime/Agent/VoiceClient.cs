using System;
using System.Globalization;
using System.IO;
using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Agent
{
    /// Talk (backend docs/api.md "Voice command"): tap or hold Talk, speak → 16 kHz mono WAV → POST /voice/command with
    /// the context (and the nearest scene photos, so "what is this?" works) → the reply is shown and spoken, the actions
    /// run like button taps. Realtime voice (/voice/token) is not active on the server.
    ///
    /// Tap or hold (the headset showed a 1 s ray-pinch hold catching only "… and a half."): a press shorter than
    /// settings.tapSeconds toggles listening on and it stops by itself at the end of speech (1 s of silence after it),
    /// at 12 s, or on a second tap; a longer press is hold-to-talk (release stops it; a release in mid-word finishes the
    /// word). The mic is pre-warmed when a Talk button is hovered or the palm menu opens (a looping ring, stopped 5 s
    /// after the last hover without a press), so a press keeps ~0.4 s from before it — back to where speech started
    /// when it was already going on. VoiceActivity (energy, adaptive noise floor) finds the speech; leading and trailing
    /// silence are trimmed (150 ms margins) before sending; nothing is sent without speech ("Didn't catch that").
    /// Every utterance logs one "Voice utterance …" line (mode, press, speech, trailing silence, bytes). VoiceCapture is
    /// the pipeline (pure); SimulateMic(wav) plays a WAV through it in real time.
    /// The microphone is chosen explicitly (SPEC §9: the default device can be a silent AirPods / Continuity mic).
    public class VoiceClient : MonoBehaviour
    {
        public const string TapHint = "Listening… tap to send", HoldHint = "Listening… release to send", FinishingHint = "Listening…";
        public const string TapShort = "Tap to send", HoldShort = "Release to send";
        public const string DidntCatch = "Didn't catch that";
        public const string AllowMic = "Allow the microphone, then tap Talk again";

        public AgentClient agent;
        public int sampleRate = 16000;
        [Tooltip("Send the nearest scene photos with every voice command (for questions about the view).")]
        public bool sendFrames = true;
        [Tooltip("Tap or hold, silence detection, pre-roll and trim (tune live with eval: settings.endSilenceSeconds …).")]
        public VoiceSettings settings = new VoiceSettings();

        /// Listening for an utterance (not the pre-warm).
        public bool Recording => m_Capture != null && m_Capture.Listening;
        public bool Listening => Recording;
        /// The mic is running (pre-warmed or listening).
        public bool Warm => m_Mic != null || m_SimMic != null;
        /// Listening while the press is held (hold-to-talk).
        public bool Holding => Recording && m_Capture.Gesture.Held;
        public TalkPhase Phase => m_Capture != null ? m_Capture.Gesture.Phase : TalkPhase.Idle;
        /// 0–1 mic level over the noise floor while listening (the meters).
        public float Level { get; private set; }
        public string Device { get; private set; }
        public string Status { get; private set; } = "";
        public float LastSeconds { get; private set; }
        public byte[] LastWav { get; private set; }
        public Utterance LastUtterance { get; private set; }
        public string LastUtteranceLine { get; private set; } = "";
        /// The last utterance was sent (speech was heard).
        public bool LastSent { get; private set; }
        public int Utterances { get; private set; }
        /// Who started the current / last listening (a TalkButton, "sim", the harness).
        public object Owner { get; private set; }
        public VoiceCapture Capture => m_Capture ??= new VoiceCapture(settings);
        /// Listening ended: true = sent, false = nothing sent (no speech, cancelled).
        public event Action<bool> Ended;

        /// The heads-up / window line while listening.
        public string Hint => !Recording ? "" : Phase == TalkPhase.Holding ? HoldHint : Phase == TalkPhase.Releasing ? FinishingHint : TapHint;
        /// The Talk button's label while listening (narrow buttons).
        public string ShortHint => !Recording ? "" : Phase == TalkPhase.Holding ? HoldShort : Phase == TalkPhase.Releasing ? FinishingHint : TapShort;

        VoiceCapture m_Capture;
        UnityMicSource m_Mic;
        ArrayMicSource m_SimMic;
        float m_WarmUntil = -1f, m_NextMicTry;
        float m_SimStart, m_SimDownAt, m_SimUpAt;
        bool m_SimDowned, m_SimUpped, m_SimSend = true, m_DotScaled;
        AirTools.Input.PalmMenu m_Palm;

        static float Now => Time.realtimeSinceStartup;

        void OnEnable() => Services.Register(this);

        void OnDisable()
        {
            Services.Unregister(this);
            Cancel();
            StopMic("disabled");
            m_SimMic = null;
        }

        // Quest: taking the headset off pauses the app — never keep listening (or the mic on) through it.
        void OnApplicationPause(bool paused)
        {
            if (!paused) return;
            Cancel();
            StopMic("paused");
        }

        AgentClient Agent => agent != null ? agent : Services.Get<AgentClient>();

        /// Prefer the headset / built-in microphone; avoid Bluetooth earbuds and phone mics (M0 findings).
        public static string PickDevice(string[] devices)
        {
            if (devices == null || devices.Length == 0) return null;
            string[] prefer = { "android", "headset", "oculus", "quest", "built-in", "macbook" };
            string[] avoid = { "airpods", "iphone", "continuity", "bluetooth" };
            foreach (var p in prefer)
                foreach (var d in devices) if (d.ToLowerInvariant().Contains(p)) return d;
            foreach (var d in devices)
            {
                bool bad = false;
                foreach (var a in avoid) if (d.ToLowerInvariant().Contains(a)) bad = true;
                if (!bad) return d;
            }
            return devices[0];
        }

        // ---------------- the press ----------------

        /// The Talk press went down. True when it started listening (a second tap or a resumed hold returns false).
        public bool PressDown(object owner = null)
        {
            var cap = Capture;
            if (cap.Listening)
            {
                if (cap.Down(Now, null) == TalkAction.Stop) Finish();
                return false;
            }
            var mic = EnsureMic(prompt: true);
            if (mic == null) return false;
            if (cap.Down(Now, mic) != TalkAction.Start) return false;
            Owner = owner;
            m_WarmUntil = float.MaxValue;
            SetStatus(Hint);
            Log.Info(string.Format(CultureInfo.InvariantCulture, "Voice: listening (pre-roll {0:0.00} s, mic warm {1:0.0} s{2})",
                cap.PreRollSeconds, m_Mic != null && ReferenceEquals(mic, m_Mic) ? m_Mic.WarmSeconds : 0f, m_SimMic != null ? ", simulated" : ""));
            return true;
        }

        /// The Talk press came up (a hold ends here; a tap keeps listening).
        public void PressUp()
        {
            var cap = Capture;
            if (!cap.Listening) return;
            if (cap.Up(Now) == TalkAction.Stop) Finish();
        }

        /// Hold-to-talk from code (the old API): listen until EndTalk.
        public bool BeginTalk() => PressDown(this);

        /// Stop listening and send now (the old API). False when nothing was sent.
        public bool EndTalk()
        {
            if (!Recording) return false;
            Capture.Stop(TalkStop.Release);
            Finish();
            return LastSent;
        }

        /// Stop listening without sending.
        public void Cancel()
        {
            if (!Recording) return;
            Capture.Stop(TalkStop.Cancel);
            Finish();
        }

        /// Warm the mic up ahead of a likely press (a Talk hover, the palm menu): it stops prewarmSeconds after the last
        /// call without a press. Never asks for the mic permission (the press does).
        public void Prewarm()
        {
            if (!isActiveAndEnabled) return;
            float until = Now + settings.prewarmSeconds;
            if (until > m_WarmUntil) m_WarmUntil = until;
            if (!Warm && Now >= m_NextMicTry) EnsureMic(prompt: false);
        }

        IMicSource EnsureMic(bool prompt)
        {
            if (m_SimMic != null) return m_SimMic;
            if (m_Mic != null && m_Mic.Running) return m_Mic;
            m_Mic = null;
            m_NextMicTry = Now + 1f;   // a failing mic isn't retried every hover frame (Microphone.devices allocates)
#if UNITY_ANDROID && !UNITY_EDITOR
            // Quest: the first Microphone.Start without RECORD_AUDIO prompts; only a press asks.
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                if (prompt)
                {
                    UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                    SetStatus(AllowMic);
                }
                return null;
            }
#endif
            Device = PickDevice(Microphone.devices);
            if (Device == null) { if (prompt) SetStatus("No microphone"); return null; }
            int ring = Mathf.CeilToInt(settings.maxSeconds + Mathf.Max(settings.preRollSeconds, settings.lookBackSeconds) + 1.5f);
            m_Mic = UnityMicSource.Start(Device, ring, sampleRate);
            if (m_Mic == null) { if (prompt) SetStatus($"Microphone {Device} didn't start"); return null; }
            if (!prompt) m_WarmUntil = Mathf.Max(m_WarmUntil, Now + settings.prewarmSeconds);
            Log.Info($"Voice: mic on ({Device}, {m_Mic.SampleRate} Hz, {(prompt ? "press" : "pre-warm")})");
            return m_Mic;
        }

        void StopMic(string why)
        {
            if (m_Mic == null) return;
            m_Mic.Stop();
            m_Mic = null;
            Log.Info($"Voice: mic off ({why})");
        }

        void Update()
        {
            float now = Now;
            // settings-assets: the press follows Settings ▸ Talk (read when listening starts; an enum compare per frame).
            var talk = UserPrefs.Talk;
            if (settings.style != talk) settings.style = talk;
            if (m_SimMic != null) SimStep(now);
            var cap = Capture;
            if (cap.Listening && cap.Update(now) != TalkStop.None) Finish();
            Level = cap.Listening ? cap.Vad.Level : 0f;
            if (cap.Listening && Status != Hint) Status = Hint;   // tap → hold copy (not logged while listening)
            if (!cap.Listening && m_Mic != null && now > m_WarmUntil) StopMic("idle");
            if (m_Palm != null || Services.TryGet(out m_Palm))
                if (m_Palm.IsOpen) Prewarm();
            MeterOnStatusLine();
        }

        /// The heads-up line's tone dot swells with the mic level while listening (the live meter on the line).
        void MeterOnStatusLine()
        {
            var line = StatusLine.Current;
            if (line == null || line.dot == null) return;
            if (!Recording && !m_DotScaled) return;
            float s = Recording ? 1f + 0.9f * Level : 1f;
            var t = line.dot.transform;
            if (Mathf.Abs(t.localScale.x - s) > 0.01f || (!Recording && t.localScale.x != 1f)) t.localScale = new Vector3(s, s, 1f);
            m_DotScaled = Recording;
        }

        void Finish()
        {
            var cap = Capture;
            var u = cap.End();
            LastUtterance = u;
            Utterances++;
            LastSeconds = u.TotalSeconds;
            Level = 0f;
            m_WarmUntil = Now + settings.prewarmSeconds;   // a quick retry still gets its pre-roll
            bool send = u.Send && u.Stop != TalkStop.Cancel;
            if (!send)
            {
                LastSent = false;
                LastWav = null;
                LastUtteranceLine = u.LogLine(0);
                Log.Info(LastUtteranceLine);
                if (u.Stop != TalkStop.Cancel)
                {
                    SetStatus(DidntCatch);
                    UiToast.Show(DidntCatch, ColorRole.Warning);
                }
                else SetStatus("");
                Ended?.Invoke(false);
                return;
            }
            var wav = WavPcm.Encode(cap.Buffer, u.Start * u.Channels, u.Count * u.Channels, u.Channels, u.SampleRate);
            LastWav = wav;
            LastSent = true;
            LastUtteranceLine = u.LogLine(wav.Length);
            Log.Info(LastUtteranceLine);
            if (m_SimMic != null && !m_SimSend) SetStatus("Simulated · not sent");
            else Send(wav);
            Ended?.Invoke(true);
        }

        // ---------------- sending ----------------

        /// Send a recorded command (also used by the agent harness with a WAV file).
        public void Send(byte[] wav)
        {
            var a = Agent;
            if (a == null) { SetStatus("No agent client"); return; }
            SetStatus("Thinking…");
            void Go(System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, string>> frames) =>
                a.SendVoice(wav, frames, r =>
                {
                    if (r == null) { SetStatus(a.LastError ?? "No reply"); return; }
                    SetStatus($"“{r.transcript}” → {r.reply}");
                });
            if (sendFrames && Services.TryGet<SceneAsk>(out var ask)) ask.GatherFrames(ask.FocusPoint(), 3, Go);
            else Go(null);
        }

        public void SendFile(string path)
        {
            if (!File.Exists(path)) { SetStatus($"No such file {path}"); return; }
            Send(File.ReadAllBytes(path));
        }

        // ---------------- simulated mic (harness) ----------------

        /// settings-assets: the Talk style now in effect (Settings ▸ Talk).
        public TalkStyle Style => settings.style;

        /// Play a WAV through the live pipeline as if spoken into the mic: the mic is warm `leadSeconds` before the WAV,
        /// the press goes down `pressAt` seconds into the WAV (negative = before the words) and lasts `pressSeconds`
        /// (below settings.tapSeconds = a tap: the silence detector stops it). Same capture, detector, trim, log line and
        /// send (send = false: stop before POST). Poll VoiceState().
        public string SimulateMic(string wavPath, float pressSeconds = 0.2f, float pressAt = 0f, bool send = true, float leadSeconds = 0.5f)
        {
            if (!File.Exists(wavPath)) return $"no such file {wavPath}";
            var mono = WavPcm.DecodeMono(File.ReadAllBytes(wavPath), sampleRate);
            if (mono == null) return "not a WAV";
            Cancel();
            int lead = Mathf.Max(0, Mathf.RoundToInt(leadSeconds * sampleRate));
            var signal = new float[lead + mono.Length];
            Array.Copy(mono, 0, signal, lead, mono.Length);
            m_SimMic = new ArrayMicSource(signal, sampleRate, settings.maxSeconds + settings.lookBackSeconds + 2f);
            m_SimStart = Now;
            m_SimDownAt = Mathf.Max(0f, leadSeconds + pressAt);
            m_SimUpAt = m_SimDownAt + Mathf.Max(0.02f, pressSeconds);
            m_SimDowned = m_SimUpped = false;
            m_SimSend = send;
            return string.Format(CultureInfo.InvariantCulture, "simulating {0:0.00} s of {1}: press at {2:0.00} s for {3:0.00} s ({4}){5}; poll VoiceState()",
                mono.Length / (float)sampleRate, Path.GetFileName(wavPath), pressAt, pressSeconds,
                pressSeconds < settings.tapSeconds ? "tap" : "hold", send ? "" : ", not sent");
        }

        void SimStep(float now)
        {
            float t = now - m_SimStart;
            m_SimMic.AdvanceTo(t);
            if (!m_SimDowned && t >= m_SimDownAt)
            {
                m_SimDowned = true;
                if (!PressDown("sim")) { m_SimMic = null; return; }
            }
            if (m_SimDowned && !m_SimUpped && t >= m_SimUpAt) { m_SimUpped = true; PressUp(); }
            if (m_SimDowned && !Recording) m_SimMic = null;   // the utterance ended (Finish ran)
        }

        /// One line for the harness: state, level, the detector, the last utterance.
        public string Describe()
        {
            var cap = Capture;
            var c = CultureInfo.InvariantCulture;
            return string.Format(c, "listening={0} phase={1} mode={2} level={3:0.00} speech={4:0.00}s silence={5:0.00}s floor={6:0}dB warm={7} sim={8} status=\"{9}\" utterances={10} sent={11} last=\"{12}\" talk={13}",
                Recording, Phase, Utterance.ModeName(cap.Gesture.Mode), Level, cap.Vad.SpeechSeconds, cap.Vad.SilenceSeconds,
                VoiceActivity.Db(cap.Vad.Floor), Warm, m_SimMic != null, Status, Utterances, LastSent, LastUtteranceLine,
                UserPrefs.Label(settings.style));   // settings-assets
        }

        void SetStatus(string s)
        {
            Status = s;
            if (!string.IsNullOrEmpty(s) && !Recording) Log.Info($"Voice: {s}");
        }
    }
}
