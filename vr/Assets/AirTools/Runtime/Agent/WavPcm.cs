using System;
using System.IO;

namespace AirTools.Agent
{
    /// RIFF/WAVE ↔ float samples without Unity (pure): the 16-bit PCM WAV that POST /voice/command takes, and reading
    /// a WAV file back (PCM 8/16/24/32-bit or IEEE float 32) for the harness and SimulateMic. WavUtil wraps these for
    /// AudioClips.
    public static class WavPcm
    {
        /// `count` interleaved samples from samples[offset] → a 16-bit PCM WAV.
        public static byte[] Encode(float[] samples, int offset, int count, int channels, int sampleRate)
        {
            samples ??= new float[0];
            offset = Math.Max(0, Math.Min(offset, samples.Length));
            int n = Math.Max(0, Math.Min(count, samples.Length - offset));
            channels = Math.Max(1, channels);
            using var ms = new MemoryStream(44 + n * 2);
            using var w = new BinaryWriter(ms);
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            w.Write(36 + n * 2);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            w.Write(16);
            w.Write((short)1);                       // PCM
            w.Write((short)channels);
            w.Write(sampleRate);
            w.Write(sampleRate * channels * 2);      // byte rate
            w.Write((short)(channels * 2));          // block align
            w.Write((short)16);
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            w.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                double v = Math.Round(samples[offset + i] * 32767.0);
                w.Write((short)Math.Max(-32768.0, Math.Min(32767.0, v)));
            }
            w.Flush();
            return ms.ToArray();
        }

        /// A WAV file → interleaved samples, channels and rate. Null when it isn't one.
        public static float[] Decode(byte[] bytes, out int channels, out int sampleRate)
        {
            channels = 1; sampleRate = 16000;
            if (bytes == null || bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F') return null;
            int pos = 12, bits = 16, format = 1;
            int dataStart = -1, dataLen = 0;
            while (pos + 8 <= bytes.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                int len = BitConverter.ToInt32(bytes, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(bytes, body);
                    channels = BitConverter.ToInt16(bytes, body + 2);
                    sampleRate = BitConverter.ToInt32(bytes, body + 4);
                    bits = BitConverter.ToInt16(bytes, body + 14);
                    if (format == -2 && len >= 26) format = BitConverter.ToInt16(bytes, body + 24);   // WAVE_FORMAT_EXTENSIBLE
                }
                else if (id == "data")
                {
                    dataStart = body;
                    // Streaming encoders write 0 / 0xFFFFFFFF sizes: take the rest of the file.
                    dataLen = len <= 0 || body + len > bytes.Length ? bytes.Length - body : len;
                    break;
                }
                pos = body + Math.Max(0, len) + (len & 1);
            }
            if (dataStart < 0 || channels < 1 || bits < 8) return null;
            int bytesPer = bits / 8;
            int frames = dataLen / (bytesPer * channels);
            if (frames <= 0) return null;
            var data = new float[frames * channels];
            for (int i = 0; i < data.Length; i++)
            {
                int o = dataStart + i * bytesPer;
                data[i] = format == 3 && bits == 32 ? BitConverter.ToSingle(bytes, o)
                        : bits == 16 ? BitConverter.ToInt16(bytes, o) / 32768f
                        : bits == 8 ? (bytes[o] - 128) / 128f
                        : bits == 24 ? ((bytes[o] | (bytes[o + 1] << 8) | ((sbyte)bytes[o + 2] << 16)) / 8388608f)
                        : BitConverter.ToInt32(bytes, o) / 2147483648f;
            }
            return data;
        }

        /// A WAV file → mono at `rate` (mixed down, linearly resampled). Null when it isn't one.
        public static float[] DecodeMono(byte[] bytes, int rate)
        {
            var data = Decode(bytes, out int channels, out int fileRate);
            if (data == null) return null;
            return VoiceMath.Resample(VoiceMath.MixDown(data, channels), fileRate, rate);
        }
    }
}
