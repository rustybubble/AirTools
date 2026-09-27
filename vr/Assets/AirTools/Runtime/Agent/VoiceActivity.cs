using System;

namespace AirTools.Agent
{
    /// Energy voice-activity detector for the Talk button (pure, allocation-free after Reset). Frames of 20 ms go in
    /// (Push); out come whether speech has started, where it starts and ends, how long the silence after it is, and a
    /// 0–1 level for the meter.
    ///
    /// The noise floor is the quietest frame RMS over a sliding window (floorWindowSeconds; the first 200 ms of the
    /// buffer, pre-roll included, calibrate it), never below 1e-4. A frame is loud at onFactor × the floor (and above
    /// absMinRms); inside an utterance (speech in the last 0.3 s) the lower offFactor applies (hysteresis). Speech
    /// starts after onsetSeconds of loud frames in a row (a click is not speech) and its first frame is where the run
    /// began. Digital silence (zeros: a mic that hasn't delivered yet) is never speech and never the floor.
    public sealed class VoiceActivity
    {
        public const float FrameSeconds = 0.02f;
        const float HangSeconds = 0.3f, FloorMin = 1e-4f, LevelSpanDb = 30f, LevelFallPerFrame = 0.06f, DigitalSilence = 1e-6f;

        readonly VoiceSettings m_Set;
        float[] m_Window = new float[0];
        int m_WinCount, m_WinPos, m_Run, m_OnsetFrames, m_HangFrames;
        bool m_RunCounted;

        public VoiceActivity(VoiceSettings settings) { m_Set = settings ?? new VoiceSettings(); Reset(16000); }

        /// Samples per channel in one frame (20 ms).
        public int FrameLength { get; private set; }
        public int SampleRate { get; private set; }
        /// Frames pushed so far.
        public int Frames { get; private set; }
        public int SpeechFrames { get; private set; }
        /// First frame of the first speech run (−1 = none yet).
        public int FirstSpeechFrame { get; private set; } = -1;
        /// Last frame that was speech (−1 = none yet).
        public int LastSpeechFrame { get; private set; } = -1;
        /// Frames since the last speech frame (0 before any speech).
        public int SilenceRun { get; private set; }
        public bool HasSpeech => FirstSpeechFrame >= 0;
        /// The last frame was speech.
        public bool InSpeech { get; private set; }
        public float Floor { get; private set; } = FloorMin;
        public float Rms { get; private set; }
        public float Peak { get; private set; }
        /// 0 at the floor, 1 at +30 dB over it; falls slowly (the meter).
        public float Level { get; private set; }

        public float SpeechSeconds => SpeechFrames * FrameSeconds;
        public float SilenceSeconds => SilenceRun * FrameSeconds;
        public float Seconds => Frames * FrameSeconds;

        /// Start over for a new utterance at this sample rate (allocates only when the window grows).
        public void Reset(int sampleRate)
        {
            SampleRate = Math.Max(1000, sampleRate);
            FrameLength = FrameLengthFor(SampleRate);
            int win = Math.Max(1, (int)Math.Round(m_Set.floorWindowSeconds / FrameSeconds));
            if (m_Window.Length != win) m_Window = new float[win];
            m_WinCount = m_WinPos = 0;
            m_OnsetFrames = Math.Max(1, (int)Math.Round(m_Set.onsetSeconds / FrameSeconds));
            m_HangFrames = (int)Math.Round(HangSeconds / FrameSeconds);
            m_Run = 0; m_RunCounted = false;
            Frames = SpeechFrames = SilenceRun = 0;
            FirstSpeechFrame = LastSpeechFrame = -1;
            InSpeech = false;
            Floor = FloorMin; Rms = Peak = Level = 0f;
        }

        public static int FrameLengthFor(int sampleRate) => Math.Max(1, (int)Math.Round(sampleRate * FrameSeconds));

        /// One frame: `count` interleaved samples from data[offset].
        public void Push(float[] data, int offset, int count)
        {
            float rms = VoiceMath.Rms(data, offset, count);
            Rms = rms;
            if (rms > Peak) Peak = rms;

            // The floor: the quietest frame over the window (its first frames calibrate it). Digital silence (a mic that
            // hasn't delivered yet: zeros in the ring) says nothing about the room and stays out of it.
            if (rms > DigitalSilence)
            {
                m_Window[m_WinPos] = rms;
                m_WinPos = (m_WinPos + 1) % m_Window.Length;
                if (m_WinCount < m_Window.Length) m_WinCount++;
                float min = float.MaxValue;
                for (int i = 0; i < m_WinCount; i++) if (m_Window[i] < min) min = m_Window[i];
                Floor = Math.Max(FloorMin, min);
            }

            bool inside = HasSpeech && SilenceRun <= m_HangFrames;
            float threshold = Math.Max(Floor * (inside ? m_Set.offFactor : m_Set.onFactor), m_Set.absMinRms);
            if (rms >= threshold && rms > DigitalSilence)
            {
                m_Run++;
                if (inside || m_Run >= m_OnsetFrames)
                {
                    if (!HasSpeech) FirstSpeechFrame = Frames - (m_Run - 1);
                    SpeechFrames += m_RunCounted ? 1 : m_Run;
                    m_RunCounted = true;
                    LastSpeechFrame = Frames;
                    SilenceRun = 0;
                    InSpeech = true;
                }
            }
            else
            {
                m_Run = 0; m_RunCounted = false;
                InSpeech = false;
                if (HasSpeech) SilenceRun++;
            }

            float db = 20f * (float)Math.Log10(Math.Max(rms, FloorMin) / Floor);
            float target = Math.Min(1f, Math.Max(0f, db / LevelSpanDb));
            Level = Math.Max(target, Level - LevelFallPerFrame);
            Frames++;
        }

        /// Full-scale dB of an RMS (−100 for silence).
        public static float Db(float rms) => rms <= 1e-5f ? -100f : 20f * (float)Math.Log10(rms);
    }
}
