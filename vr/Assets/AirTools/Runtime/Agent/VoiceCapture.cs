using System;
using System.Globalization;

namespace AirTools.Agent
{
    /// One utterance as captured: how the press went, what the detector heard, and the part kept for sending
    /// ([Start, Start + Count) frames of VoiceCapture.Buffer, silence trimmed off both ends).
    public struct Utterance
    {
        public TalkMode Mode;
        public TalkStop Stop;
        public bool HasSpeech;
        public float PressSeconds, SpeechSeconds, TrailingSilence, PreRollSeconds, TotalSeconds, KeptSeconds;
        public float FloorDb, PeakDb;
        public int Start, Count, SampleRate, Channels;

        /// Sent to the server: speech was heard.
        public bool Send => HasSpeech && Count > 0;

        /// The per-utterance log line (grep "Voice utterance"): press, mode, speech, trailing silence, bytes sent.
        public string LogLine(int bytes)
        {
            var c = CultureInfo.InvariantCulture;
            return string.Format(c,
                "Voice utterance mode={0} stop={1} press={2:0.00}s speech={3:0.00}s trailing={4:0.00}s preroll={5:0.00}s kept={6:0.00}s total={7:0.00}s bytes={8} floor={9:0}dB peak={10:0}dB",
                ModeName(Mode), StopName(Stop), PressSeconds, SpeechSeconds, TrailingSilence, PreRollSeconds, KeptSeconds, TotalSeconds,
                bytes, FloorDb, PeakDb);
        }

        public static string ModeName(TalkMode m) => m == TalkMode.Tap ? "tap" : m == TalkMode.Hold ? "hold" : "none";

        public static string StopName(TalkStop s) => s switch
        {
            TalkStop.Release => "release",
            TalkStop.SecondTap => "second-tap",
            TalkStop.EndOfSpeech => "end-of-speech",
            TalkStop.MaxLength => "max-length",
            TalkStop.NoSpeech => "no-speech",
            TalkStop.Cancel => "cancel",
            _ => "none",
        };
    }

    /// The Talk pipeline without Unity (pure; the EditMode tests drive it with synthetic signals): the tap-or-hold
    /// gesture, the pre-roll from the mic's ring, 20 ms frames into a preallocated buffer and through the voice
    /// detector, and the trim at the end. VoiceClient owns one, feeds it the real mic (or a WAV) and sends the result.
    ///
    ///   Down(now, mic) → Start: capture begins at the press minus the pre-roll (preRollSeconds; back to the speech's
    ///   start, up to lookBackSeconds, when speech was already going on). Update(now) every frame → a TalkStop when to
    ///   stop. Up(now) / Down(now) again → Stop for a hold release / second tap. End() → the Utterance.
    public sealed class VoiceCapture
    {
        public readonly VoiceSettings Settings;
        public readonly TalkGesture Gesture;
        public readonly VoiceActivity Vad;

        IMicSource m_Mic;
        float[] m_Buffer = new float[0], m_Frame = new float[0], m_LookRms = new float[0];
        int m_Frames, m_Read, m_FrameLength, m_Channels, m_Rate, m_PreRollFrames;

        public VoiceCapture(VoiceSettings settings = null)
        {
            Settings = settings ?? new VoiceSettings();
            Gesture = new TalkGesture(Settings);
            Vad = new VoiceActivity(Settings);
        }

        /// Listening (the gesture is live and a mic is attached).
        public bool Listening => m_Mic != null && Gesture.Listening;
        /// The captured audio (interleaved), valid up to FramesCaptured × Channels.
        public float[] Buffer => m_Buffer;
        public int FramesCaptured => m_Frames;
        public int Channels => m_Channels;
        public int SampleRate => m_Rate;
        public float PreRollSeconds => m_Rate > 0 ? m_PreRollFrames / (float)m_Rate : 0f;
        /// Buffer frames reserved (grows only when the rate / max length grows).
        public int Capacity => m_Channels > 0 ? m_Buffer.Length / m_Channels : 0;

        /// The press went down. Start: listening began (pre-roll read from `mic`); Stop: a second tap (call End).
        public TalkAction Down(float now, IMicSource mic)
        {
            if (Gesture.Listening) return Gesture.Down(now);
            if (mic == null) return TalkAction.None;
            var a = Gesture.Down(now);
            if (a == TalkAction.Start) Begin(mic);
            return a;
        }

        /// The press came up. Stop: a hold was released (call End).
        public TalkAction Up(float now)
        {
            if (!Listening) return TalkAction.None;
            Pump();
            bool speaking = Vad.HasSpeech && Vad.SilenceSeconds < Settings.releaseSpeechSeconds;
            return Gesture.Up(now, speaking);
        }

        /// Every frame while listening: read what the mic wrote, then ask the gesture whether to stop.
        public TalkStop Update(float now)
        {
            if (!Listening) return TalkStop.None;
            Pump();
            return Gesture.Tick(now, Vad.HasSpeech, Vad.SilenceSeconds);
        }

        /// Stop now (reason Cancel / Release …) without a press. End() afterwards.
        public TalkStop Stop(TalkStop reason)
        {
            if (!Listening) return TalkStop.None;
            Pump();
            return Gesture.Force(reason);
        }

        void Begin(IMicSource mic)
        {
            m_Mic = mic;
            m_Rate = Math.Max(1000, mic.SampleRate);
            m_Channels = Math.Max(1, mic.Channels);
            Vad.Reset(m_Rate);
            m_FrameLength = Vad.FrameLength;
            int frameSamples = m_FrameLength * m_Channels;
            if (m_Frame.Length != frameSamples) m_Frame = new float[frameSamples];
            float seconds = Settings.maxSeconds + Math.Max(Settings.preRollSeconds, Settings.lookBackSeconds) + 1f;
            int capacity = (int)Math.Ceiling(seconds * m_Rate) * m_Channels;
            if (m_Buffer.Length < capacity) m_Buffer = new float[capacity];
            m_Frames = 0;

            // Pre-roll: look back over what the warm mic already holds; keep the fixed pre-roll, or back to the start of
            // speech that runs into the press.
            int ring = mic.RingLength, write = mic.Position;
            int lookFrames = (int)Math.Round(Math.Max(Settings.preRollSeconds, Settings.lookBackSeconds) / VoiceActivity.FrameSeconds);
            if (m_LookRms.Length < lookFrames + 1) m_LookRms = new float[lookFrames + 1];
            int start = VoiceMath.Back(write, ring, m_FrameLength, lookFrames * m_FrameLength, mic.Recorded);
            int n = 0;
            for (int pos = start; VoiceMath.Available(pos, write, ring) >= m_FrameLength && n < m_LookRms.Length; pos = VoiceMath.Wrap(pos + m_FrameLength, ring))
            {
                mic.Read(m_Frame, pos);
                m_LookRms[n++] = VoiceMath.Rms(m_Frame, 0, frameSamples);
            }
            int baseFrames = (int)Math.Round(Settings.preRollSeconds / VoiceActivity.FrameSeconds);
            int keep = VoiceMath.ChoosePreRoll(m_LookRms, n, baseFrames, gapFrames: (int)Math.Round(0.3f / VoiceActivity.FrameSeconds),
                onsetFrames: (int)Math.Round(Settings.onsetSeconds / VoiceActivity.FrameSeconds),
                marginFrames: (int)Math.Round(Settings.trimMarginSeconds / VoiceActivity.FrameSeconds), Settings.onFactor, Settings.absMinRms);
            m_Read = VoiceMath.Wrap(start + (n - keep) * m_FrameLength, ring);
            m_PreRollFrames = keep * m_FrameLength;
            Pump();
        }

        /// Read every whole frame the mic has written since the last read into the buffer and the detector.
        public int Pump()
        {
            if (m_Mic == null) return 0;
            int ring = m_Mic.RingLength, write = m_Mic.Position, read = 0;
            int frameSamples = m_FrameLength * m_Channels;
            while (VoiceMath.Available(m_Read, write, ring) >= m_FrameLength)
            {
                m_Mic.Read(m_Frame, m_Read);
                m_Read = VoiceMath.Wrap(m_Read + m_FrameLength, ring);
                int at = m_Frames * m_Channels;
                if (at + frameSamples > m_Buffer.Length) break;   // full (the max length stops it first)
                Array.Copy(m_Frame, 0, m_Buffer, at, frameSamples);
                m_Frames += m_FrameLength;
                Vad.Push(m_Frame, 0, frameSamples);
                read++;
            }
            return read;
        }

        /// Finish: the utterance with silence trimmed off both ends (150 ms margins). Detaches the mic.
        public Utterance End()
        {
            Pump();
            if (Gesture.Listening) Gesture.Force(TalkStop.Cancel);
            int margin = (int)Math.Round(Settings.trimMarginSeconds * m_Rate);
            int first = Vad.HasSpeech ? Vad.FirstSpeechFrame * m_FrameLength : -1;
            int speechEnd = Vad.HasSpeech ? (Vad.LastSpeechFrame + 1) * m_FrameLength : -1;
            VoiceMath.Trim(m_Frames, first, speechEnd, margin, out int start, out int count);
            float rate = Math.Max(1, m_Rate);
            var u = new Utterance
            {
                Mode = Gesture.Mode,
                Stop = Gesture.LastStop,
                HasSpeech = Vad.HasSpeech,
                PressSeconds = Gesture.PressSeconds,
                SpeechSeconds = Vad.SpeechSeconds,
                TrailingSilence = Vad.HasSpeech ? Math.Max(0, m_Frames - speechEnd) / rate : m_Frames / rate,
                PreRollSeconds = m_PreRollFrames / rate,
                TotalSeconds = m_Frames / rate,
                KeptSeconds = Vad.HasSpeech ? count / rate : 0f,
                FloorDb = VoiceActivity.Db(Vad.Floor),
                PeakDb = VoiceActivity.Db(Vad.Peak),
                Start = start,
                Count = Vad.HasSpeech ? count : 0,
                SampleRate = m_Rate,
                Channels = m_Channels,
            };
            m_Mic = null;
            return u;
        }

        /// Run a whole signal through the pipeline on a simulated clock (pure; tests and BackendHarness.VoiceVad): the mic
        /// starts at t = 0 with `signal`, the press goes down at `pressAt` and comes up `pressSeconds` later, frames
        /// arrive every `dt` (a headset frame). Returns the utterance (Buffer holds its audio).
        public Utterance Simulate(float[] signal, int sampleRate, float pressAt, float pressSeconds, float dt = 1f / 72f,
            float secondTapAt = -1f)
        {
            var mic = new ArrayMicSource(signal, sampleRate, Settings.maxSeconds + Settings.lookBackSeconds + 2f);
            float t = 0f, end = pressAt + Settings.maxSeconds + 1f;
            bool down = false, up = false, second = false;
            Gesture.Reset();
            while (t <= end)
            {
                mic.AdvanceTo(t);
                if (!down && t >= pressAt)
                {
                    down = true;
                    if (Down(t, mic) != TalkAction.Start) break;
                }
                if (down && !up && t >= pressAt + pressSeconds)
                {
                    up = true;
                    if (Up(t) == TalkAction.Stop) return End();
                }
                if (down && !second && secondTapAt >= 0f && t >= secondTapAt)
                {
                    second = true;
                    if (Down(t, mic) == TalkAction.Stop) return End();
                    Up(t + 0.1f);
                }
                if (down && Update(t) != TalkStop.None) return End();
                t += dt;
            }
            return End();
        }
    }
}
