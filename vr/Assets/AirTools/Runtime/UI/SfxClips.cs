using UnityEngine;

namespace AirTools.UI
{
    /// The measuring sounds, synthesised once (no audio assets to ship): pentatonic point pips that rise with each
    /// point, a four-note "saved" arpeggio, a level glide, a part clunk, undo / redo glides, a low double "miss", a
    /// snap tick and the payment chime. UI presses use Meta's ISDK set when the scene wires it (SfxPlayer).
    public static class SfxClips
    {
        const int Rate = 44100;
        /// C major pentatonic from C6: C D E G A.
        static readonly float[] s_Pentatonic = { 1046.5f, 1174.66f, 1318.51f, 1567.98f, 1760f };
        static readonly AudioClip[] s_Pips = new AudioClip[s_Pentatonic.Length];
        static AudioClip s_Saved, s_Level, s_Clunk, s_Undo, s_Redo, s_Miss, s_Tick, s_CornerTick, s_Chime, s_Press, s_Teleport;

        /// The pip for the n-th point of a shape (1-based): up the scale, then round again an octave step lower.
        public static AudioClip Pip(int n)
        {
            int i = ((Mathf.Max(1, n) - 1) % s_Pentatonic.Length + s_Pentatonic.Length) % s_Pentatonic.Length;
            return s_Pips[i] ??= Tone($"Pip{i}", s_Pentatonic[i], 0.075f, 0.018f, 0.28f, harmonic: 0.18f);
        }

        public static AudioClip Saved => s_Saved ??= Arpeggio("Saved", new[] { 1046.5f, 1318.51f, 1567.98f, 2093f }, 0.055f, 0.16f);
        public static AudioClip Level => s_Level ??= Glide("Level", 620f, 930f, 0.16f, 0.26f);
        public static AudioClip Clunk => s_Clunk ??= Thump("Clunk", 150f, 0.12f);
        public static AudioClip Undo => s_Undo ??= Glide("Undo", 880f, 560f, 0.11f, 0.2f);
        public static AudioClip Redo => s_Redo ??= Glide("Redo", 560f, 880f, 0.11f, 0.2f);
        public static AudioClip Miss => s_Miss ??= DoublePulse("Miss", 190f, 0.05f, 0.06f);
        public static AudioClip Tick => s_Tick ??= Tone("SnapTick", 2400f, 0.012f, 0.0025f, 0.22f);
        public static AudioClip CornerTick => s_CornerTick ??= Tone("CornerTick", 3200f, 0.012f, 0.0025f, 0.22f);
        public static AudioClip Press => s_Press ??= Tone("UiTick", 1600f, 0.025f, 0.0025f, 0.5f);
        public static AudioClip Teleport => s_Teleport ??= Glide("Teleport", 420f, 260f, 0.14f, 0.18f);

        /// Two rising notes (payment authorised).
        public static AudioClip Chime
        {
            get
            {
                if (s_Chime != null) return s_Chime;
                int n = Rate / 2;
                var data = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float t = (float)i / Rate;
                    float f = t < 0.12f ? 880f : 1318.5f;
                    float env = Mathf.Exp(-(t < 0.12f ? t : t - 0.12f) / 0.18f) * (t < 0.005f ? t / 0.005f : 1f);
                    data[i] = 0.35f * env * Mathf.Sin(2f * Mathf.PI * f * t);
                }
                return s_Chime = Make("UiChime", data);
            }
        }

        static AudioClip Make(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// A struck tone: fast attack, exponential decay, a touch of the octave.
        static AudioClip Tone(string name, float freq, float seconds, float decay, float gain, float harmonic = 0f)
        {
            int n = Mathf.CeilToInt(Rate * seconds);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float env = Mathf.Exp(-t / Mathf.Max(decay, 1e-4f)) * Mathf.Clamp01(t / 0.002f);
                data[i] = gain * env * (Mathf.Sin(2f * Mathf.PI * freq * t) + harmonic * Mathf.Sin(4f * Mathf.PI * freq * t));
            }
            return Make(name, data);
        }

        static AudioClip Arpeggio(string name, float[] notes, float step, float decay)
        {
            int n = Mathf.CeilToInt(Rate * (step * notes.Length + decay * 3f));
            var data = new float[n];
            for (int k = 0; k < notes.Length; k++)
            {
                int start = Mathf.RoundToInt(Rate * step * k);
                for (int i = start; i < n; i++)
                {
                    float t = (float)(i - start) / Rate;
                    float env = Mathf.Exp(-t / decay) * Mathf.Clamp01(t / 0.003f);
                    data[i] += 0.16f * env * (Mathf.Sin(2f * Mathf.PI * notes[k] * t) + 0.15f * Mathf.Sin(4f * Mathf.PI * notes[k] * t));
                }
            }
            return Make(name, data);
        }

        /// A sine sweeping from f0 to f1 (phase-continuous), with a soft envelope.
        static AudioClip Glide(string name, float f0, float f1, float seconds, float gain)
        {
            int n = Mathf.CeilToInt(Rate * seconds);
            var data = new float[n];
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float u = (float)i / n;
                float f = Mathf.Lerp(f0, f1, u * u * (3f - 2f * u));
                phase += 2f * Mathf.PI * f / Rate;
                float env = Mathf.Sin(Mathf.PI * Mathf.Min(1f, u * 1.15f)) * (1f - 0.4f * u);
                data[i] = gain * env * Mathf.Sin(phase);
            }
            return Make(name, data);
        }

        /// A low body thump with a short noise click on top (a part seating).
        static AudioClip Thump(string name, float freq, float seconds)
        {
            int n = Mathf.CeilToInt(Rate * seconds);
            var data = new float[n];
            var rng = new System.Random(11);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float body = Mathf.Exp(-t / 0.035f) * Mathf.Sin(2f * Mathf.PI * freq * (1f - 0.3f * t / seconds) * t);
                float click = Mathf.Exp(-t / 0.004f) * (float)(rng.NextDouble() * 2.0 - 1.0);
                data[i] = 0.55f * body + 0.25f * click;
            }
            return Make(name, data);
        }

        static AudioClip DoublePulse(string name, float freq, float pulse, float gap)
        {
            int n = Mathf.CeilToInt(Rate * (pulse * 2f + gap));
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float local = t < pulse ? t : t >= pulse + gap ? t - pulse - gap : -1f;
                if (local < 0f) continue;
                float env = Mathf.Sin(Mathf.PI * local / pulse);
                data[i] = 0.3f * env * Mathf.Sin(2f * Mathf.PI * freq * t);
            }
            return Make(name, data);
        }
    }
}
