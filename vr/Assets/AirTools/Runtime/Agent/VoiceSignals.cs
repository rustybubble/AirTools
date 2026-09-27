using System;

namespace AirTools.Agent
{
    /// Synthetic mic signals for the voice detector's tests and BackendHarness.VoiceCheck (pure; allocates the signal).
    public static class VoiceSignals
    {
        /// Noise at `noiseRms`, with syllable-like voiced sound (a 150 Hz buzz with harmonics, ~4 syllables/s, short dips
        /// between them) at `speechRms` over [speechStart, speechStart + speechSeconds). Deterministic for a seed.
        public static float[] Speech(int rate, float totalSeconds, float speechStart, float speechSeconds,
            float speechRms = 0.1f, float noiseRms = 0.002f, int seed = 1)
        {
            int n = Math.Max(0, (int)Math.Round(totalSeconds * rate));
            var s = new float[n];
            var rng = new Random(seed);
            float noiseAmp = noiseRms * (float)Math.Sqrt(3.0);   // uniform [−a, a] has RMS a/√3
            // Harmonic buzz 1 : 0.6 : 0.4 : 0.25 → RMS = sqrt(Σa²/2); scale it to speechRms at full envelope.
            double buzzRms = Math.Sqrt((1 + 0.36 + 0.16 + 0.0625) / 2.0);
            double scale = speechRms / buzzRms / 0.8;   // the syllable envelope averages ~0.8 in RMS
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)rate;
                float v = (float)((rng.NextDouble() * 2 - 1) * noiseAmp);
                double u = t - speechStart;
                if (u >= 0 && u < speechSeconds)
                {
                    double syl = u % 0.25;   // 0.19 s voiced, 0.06 s dip
                    double env = syl < 0.19 ? Math.Sin(Math.PI * syl / 0.19) * 0.85 + 0.15 : 0.05;
                    double w = 2 * Math.PI * 150 * t;
                    double buzz = Math.Sin(w) + 0.6 * Math.Sin(2 * w) + 0.4 * Math.Sin(3 * w) + 0.25 * Math.Sin(4 * w);
                    v += (float)(buzz * env * scale);
                }
                s[i] = v;
            }
            return s;
        }

        /// Noise only.
        public static float[] Noise(int rate, float seconds, float rms, int seed = 1) => Speech(rate, seconds, 0f, 0f, 0f, rms, seed);

        /// Add a short loud burst (a click / a knock) at `at` seconds.
        public static float[] WithClick(float[] s, int rate, float at, float seconds = 0.02f, float amplitude = 0.5f)
        {
            int a = (int)(at * rate), b = Math.Min(s.Length, a + (int)(seconds * rate));
            for (int i = Math.Max(0, a); i < b; i++) s[i] += (i & 1) == 0 ? amplitude : -amplitude;
            return s;
        }
    }
}
