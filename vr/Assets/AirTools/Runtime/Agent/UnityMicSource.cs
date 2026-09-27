using UnityEngine;

namespace AirTools.Agent
{
    /// The real microphone as a looping ring: Microphone.Start(device, loop: true, ringSeconds, rate). Warm (running)
    /// from a hover or the palm menu, so a press can keep audio from just before it. Reads go through
    /// AudioClip.GetData into the caller's preallocated frame (no per-frame allocation).
    public sealed class UnityMicSource : IMicSource
    {
        public readonly string Device;
        readonly AudioClip m_Clip;
        readonly float m_StartedAt;

        UnityMicSource(string device, AudioClip clip)
        {
            Device = device;
            m_Clip = clip;
            m_StartedAt = Time.realtimeSinceStartup;
        }

        /// Start the device (null when it doesn't). ringSeconds is whole seconds so the ring is a whole number of 20 ms
        /// frames at any rate that is a multiple of 50 Hz.
        public static UnityMicSource Start(string device, int ringSeconds, int sampleRate)
        {
            var clip = Microphone.Start(device, true, ringSeconds, sampleRate);
            return clip == null ? null : new UnityMicSource(device, clip);
        }

        public bool Running => m_Clip != null && Microphone.IsRecording(Device);
        public int SampleRate => m_Clip.frequency;
        public int Channels => m_Clip.channels;
        public int RingLength => m_Clip.samples;
        public int Position => Mathf.Max(0, Microphone.GetPosition(Device));
        /// Estimated from the clock (the first samples can take ~0.1 s to arrive; those read as silence).
        public int Recorded => Mathf.Min(RingLength, (int)((Time.realtimeSinceStartup - m_StartedAt) * SampleRate));
        public float WarmSeconds => Time.realtimeSinceStartup - m_StartedAt;

        public void Read(float[] frame, int offset) => m_Clip.GetData(frame, offset);

        public void Stop()
        {
            if (Microphone.IsRecording(Device)) Microphone.End(Device);
        }
    }
}
