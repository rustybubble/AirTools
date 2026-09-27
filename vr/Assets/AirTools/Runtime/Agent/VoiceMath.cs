using System;

namespace AirTools.Agent
{
    /// The voice capture's arithmetic (pure): the microphone's looping ring, how much audio from before the press to
    /// keep, trimming silence off both ends, and WAV helpers. Positions are sample frames (per channel).
    public static class VoiceMath
    {
        /// RMS of `count` samples from data[offset] (0 for none).
        public static float Rms(float[] data, int offset, int count)
        {
            if (data == null || count <= 0) return 0f;
            double sum = 0;
            int end = Math.Min(data.Length, offset + count);
            for (int i = Math.Max(0, offset); i < end; i++) sum += data[i] * data[i];
            return (float)Math.Sqrt(sum / count);
        }

        /// Wrap a position into [0, ring).
        public static int Wrap(int pos, int ring) => ring <= 0 ? 0 : ((pos % ring) + ring) % ring;

        /// Frames written but not read yet in a looping ring (read → write, forwards).
        public static int Available(int read, int write, int ring) => ring <= 0 ? 0 : Wrap(write - read, ring);

        /// Where to start reading so at least `frames` before the write head are included, aligned down to a whole
        /// frame of `frameLength` (the ring's length is a multiple of it, so reads never straddle the wrap). `frames` is
        /// capped by what the ring holds (ring − 2 frames) and by `recorded` (audio since the mic started).
        public static int Back(int write, int ring, int frameLength, int frames, int recorded)
        {
            if (ring <= 0 || frameLength <= 0) return 0;
            int n = Math.Max(0, Math.Min(frames, Math.Min(recorded, ring - 2 * frameLength)));
            int start = Wrap(write - n, ring);
            return start - start % frameLength;
        }

        /// How many whole frames of the look-back to keep before the press. `rms` holds `n` frames' RMS, oldest first,
        /// ending at the press. Normally `baseFrames` (the fixed pre-roll); when speech runs into the press (no gap
        /// longer than `gapFrames` between it and the press, and at least `onsetFrames` loud frames), back to where it
        /// started plus `marginFrames` — up to all `n`. Loud = onFactor × the look-back's quietest non-zero frame, ≥ absMin.
        public static int ChoosePreRoll(float[] rms, int n, int baseFrames, int gapFrames, int onsetFrames, int marginFrames,
            float onFactor, float absMin)
        {
            if (rms == null || n <= 0) return 0;
            n = Math.Min(n, rms.Length);
            int keep = Math.Min(Math.Max(0, baseFrames), n);
            float floor = float.MaxValue;
            for (int i = 0; i < n; i++) if (rms[i] > 1e-6f && rms[i] < floor) floor = rms[i];   // zeros: the mic not delivering yet
            if (floor == float.MaxValue) floor = 1e-4f;
            float threshold = Math.Max(Math.Max(floor, 1e-4f) * onFactor, absMin);
            int start = -1, silent = 0, loud = 0;
            for (int i = n - 1; i >= 0; i--)
            {
                if (rms[i] >= threshold && rms[i] > 0f) { start = i; silent = 0; loud++; }
                else if (++silent > gapFrames) break;
            }
            if (start < 0 || loud < Math.Max(1, onsetFrames)) return keep;
            return Math.Min(n, Math.Max(keep, n - start + marginFrames));
        }

        /// Trim silence off both ends of `total` frames: keep [firstSpeech − margin, speechEnd + margin) clamped to the
        /// buffer. No speech (firstSpeech < 0) keeps everything.
        public static void Trim(int total, int firstSpeech, int speechEnd, int margin, out int start, out int count)
        {
            total = Math.Max(0, total);
            if (firstSpeech < 0 || speechEnd <= firstSpeech) { start = 0; count = total; return; }
            start = Math.Max(0, Math.Min(total, firstSpeech - Math.Max(0, margin)));
            int end = Math.Max(start, Math.Min(total, speechEnd + Math.Max(0, margin)));
            count = end - start;
        }

        /// Interleaved → mono (average of the channels). Allocates (file loads only).
        public static float[] MixDown(float[] interleaved, int channels)
        {
            if (interleaved == null) return new float[0];
            if (channels <= 1) return interleaved;
            int frames = interleaved.Length / channels;
            var mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float s = 0f;
                for (int c = 0; c < channels; c++) s += interleaved[f * channels + c];
                mono[f] = s / channels;
            }
            return mono;
        }

        /// Linear resample of a mono signal. Allocates (file loads only).
        public static float[] Resample(float[] mono, int fromRate, int toRate)
        {
            if (mono == null) return new float[0];
            if (fromRate <= 0 || toRate <= 0 || fromRate == toRate) return mono;
            int n = (int)Math.Floor((long)mono.Length * toRate / (double)fromRate);
            var o = new float[n];
            double step = fromRate / (double)toRate;
            for (int i = 0; i < n; i++)
            {
                double x = i * step;
                int a = (int)x;
                float t = (float)(x - a);
                float s0 = mono[Math.Min(a, mono.Length - 1)], s1 = mono[Math.Min(a + 1, mono.Length - 1)];
                o[i] = s0 + (s1 - s0) * t;
            }
            return o;
        }
    }
}
