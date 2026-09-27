using System;

namespace AirTools.Agent
{
    /// A looping microphone ring the voice capture reads from: the real mic (UnityMicSource) or a WAV / synthetic
    /// signal played in on a clock (ArrayMicSource: VoiceClient.SimulateMic and the EditMode tests).
    public interface IMicSource
    {
        int SampleRate { get; }
        int Channels { get; }
        /// Frames in the ring (a multiple of the 20 ms frame).
        int RingLength { get; }
        /// The write head (frames, in [0, RingLength)).
        int Position { get; }
        /// Frames recorded since the mic started (capped at RingLength): how much pre-roll exists.
        int Recorded { get; }
        /// Fill `frame` (whole frames, interleaved) from ring position `offset`; never straddles the wrap.
        void Read(float[] frame, int offset);
    }

    /// A mono signal played into a virtual ring on a clock: position = (now − start) × rate, then silence. Pure.
    public sealed class ArrayMicSource : IMicSource
    {
        readonly float[] m_Signal;
        readonly int m_Ring;
        long m_Written;

        /// `signal` starts at the mic's start; the ring holds `ringSeconds`.
        public ArrayMicSource(float[] signal, int sampleRate, float ringSeconds = 15f)
        {
            m_Signal = signal ?? new float[0];
            SampleRate = Math.Max(1000, sampleRate);
            int frame = VoiceActivity.FrameLengthFor(SampleRate);
            int ring = (int)Math.Ceiling(ringSeconds * SampleRate);
            m_Ring = Math.Max(frame * 4, ring - ring % frame);
        }

        public int SampleRate { get; }
        public int Channels => 1;
        public int RingLength => m_Ring;
        public int Position => (int)(m_Written % m_Ring);
        public int Recorded => (int)Math.Min(m_Written, m_Ring);
        /// Seconds of the signal played in so far.
        public float Seconds => m_Written / (float)SampleRate;
        public int SignalLength => m_Signal.Length;

        /// Play the signal in up to `seconds` since the mic started (never backwards).
        public void AdvanceTo(float seconds)
        {
            long target = (long)Math.Floor(Math.Max(0f, seconds) * (double)SampleRate);
            if (target > m_Written) m_Written = target;
        }

        public void Read(float[] frame, int offset)
        {
            // Ring position `offset` holds the newest sample written there: the absolute index ≡ offset (mod ring), ≤ written.
            long lap = (m_Written - 1 - offset) / m_Ring;
            long abs0 = offset + Math.Max(0, lap) * m_Ring;
            for (int i = 0; i < frame.Length; i++)
            {
                long abs = abs0 + i;
                frame[i] = abs >= 0 && abs < m_Signal.Length && abs < m_Written ? m_Signal[abs] : 0f;
            }
        }
    }
}
