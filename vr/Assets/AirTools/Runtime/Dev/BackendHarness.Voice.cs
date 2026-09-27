using System.Globalization;
using System.IO;
using System.Text;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Parts;
using AirTools.Structure;
using AirTools.UI;

namespace AirTools.Dev
{
    /// Voice (tap or hold Talk): checks for the capture pipeline, driven with `unity command eval`.
    ///   VoiceCheck()                 pure, no Play mode needed: synthetic tap/hold/noise runs → [AirTools.Check] voice.* lines.
    ///   VoiceVad(wav, press, at)     pure: a WAV through the pipeline on a simulated clock → the utterance line (nothing sent).
    ///   VoiceSim(wav, press, at, send)  Play mode: the WAV "spoken into the mic" in real time through VoiceClient (same
    ///                                   detector, trim, log line; send = POST /voice/command). Poll VoiceState().
    ///   VoicePress(true|false)       Play mode: the Talk press edges (the real mic).
    ///   VoiceState() / TalkButtons() what the client and the two Talk buttons show now.
    /// Make a WAV: `say -o c.aiff "the opening is 34 and a half inches tall" && afconvert -f WAVE -d LEI16@16000 c.aiff c.wav`.
    public static partial class BackendHarness
    {
        static VoiceSettings VoiceSettingsNow => Services.Get<VoiceClient>()?.settings?.Clone() ?? new VoiceSettings();

        /// A WAV through the pure pipeline (press `at` seconds into the WAV, lasting `press`; the mic warm 0.5 s before
        /// the WAV). Nothing is sent.
        public static string VoiceVad(string wavPath, float press = 0.2f, float at = 0f)
        {
            if (!File.Exists(wavPath)) return $"no such file {wavPath}";
            const int rate = 16000;
            var mono = WavPcm.DecodeMono(File.ReadAllBytes(wavPath), rate);
            if (mono == null) return "not a WAV";
            int lead = rate / 2;
            var signal = new float[lead + mono.Length];
            System.Array.Copy(mono, 0, signal, lead, mono.Length);
            var cap = new VoiceCapture(VoiceSettingsNow);
            var u = cap.Simulate(signal, rate, System.Math.Max(0f, 0.5f + at), press);
            int bytes = u.Send ? 44 + u.Count * u.Channels * 2 : 0;
            string line = u.LogLine(bytes);
            Log.Info($"{line} (VoiceVad {Path.GetFileName(wavPath)})");
            return line;
        }

        /// Synthetic runs of the whole pipeline (pure, no Play mode): tap → speech → silence sends; a second tap sends;
        /// a hold sends on release; words before the press are kept; noise only sends nothing.
        public static string VoiceCheck()
        {
            const int rate = 16000;
            var sb = new StringBuilder();
            int pass = 0, total = 0;
            void Check(string id, bool ok, string detail)
            {
                total++;
                if (ok) pass++;
                Log.Check(id, ok, detail);
                sb.Append(ok ? "PASS " : "FAIL ").Append(id).Append(' ').Append(detail).Append('\n');
            }
            var set = VoiceSettingsNow;
            set.style = TalkStyle.Auto;   // settings-assets: these are Auto's (tap-or-hold) checks; TalkStyleCheck() has the others

            // Tap at 0.5 s, speech 0.7–2.7 s → stops ~1 s after the speech, sends ~2.3 s.
            var u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(rate, 8f, 0.7f, 2f), rate, 0.5f, 0.15f);
            Check("voice.tap.end_of_speech", u.Mode == TalkMode.Tap && u.Stop == TalkStop.EndOfSpeech && u.Send
                && Near(u.TrailingSilence, set.endSilenceSeconds, 0.1f) && Near(u.KeptSeconds, 2f + 2 * set.trimMarginSeconds, 0.25f), u.LogLine(0));
            // Tap, then a second tap while still talking.
            u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(rate, 8f, 0.7f, 4f), rate, 0.5f, 0.15f, secondTapAt: 2.5f);
            Check("voice.tap.second_tap", u.Stop == TalkStop.SecondTap && u.Send, u.LogLine(0));
            // Hold 2.5 s over 2 s of speech → the release sends.
            u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(rate, 8f, 0.7f, 2f), rate, 0.5f, 2.5f);
            Check("voice.hold.release", u.Mode == TalkMode.Hold && u.Stop == TalkStop.Release && u.Send && Near(u.PressSeconds, 2.5f, 0.05f), u.LogLine(0));
            // Speech already going on 0.8 s before the press: kept.
            u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(rate, 8f, 1.2f, 2f), rate, 2.0f, 0.15f);
            Check("voice.preroll.speech_before_press", u.Send && u.PreRollSeconds >= 0.8f && u.KeptSeconds >= 2f, u.LogLine(0));
            // Quiet before the press: the fixed pre-roll.
            u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(rate, 8f, 2.2f, 1.5f), rate, 2.0f, 0.15f);
            Check("voice.preroll.fixed", Near(u.PreRollSeconds, set.preRollSeconds, 0.03f), u.LogLine(0));
            // Noise only: a tap gives up, a short hold sends nothing.
            u = new VoiceCapture(set).Simulate(VoiceSignals.Noise(rate, 10f, 0.01f), rate, 0.5f, 0.15f);
            Check("voice.noise.tap", u.Stop == TalkStop.NoSpeech && !u.Send, u.LogLine(0));
            u = new VoiceCapture(set).Simulate(VoiceSignals.Noise(rate, 4f, 0.01f), rate, 0.5f, 0.5f);
            Check("voice.noise.hold", u.Stop == TalkStop.Release && !u.Send, u.LogLine(0));
            sb.Insert(0, $"{pass}/{total} voice checks\n");
            return sb.ToString();
        }

        static bool Near(float a, float b, float tol) => System.Math.Abs(a - b) <= tol;

        /// Play mode: a WAV "spoken into the mic" through VoiceClient in real time. press &lt; 0.35 s = a tap.
        public static string VoiceSim(string wavPath, float press = 0.2f, float at = 0f, bool send = true)
        {
            var v = Services.Get<VoiceClient>();
            return v == null ? "no voice client" : v.SimulateMic(wavPath, press, at, send);
        }

        /// Play mode: press (true) or release (false) Talk with the real mic, as a button would.
        public static string VoicePress(bool down)
        {
            var v = Services.Get<VoiceClient>();
            if (v == null) return "no voice client";
            if (down) return v.PressDown("harness") ? "listening" : $"not started: {v.Status}";
            v.PressUp();
            return v.Recording ? $"still listening ({v.Phase})" : "stopped";
        }

        public static string VoiceState()
        {
            var v = Services.Get<VoiceClient>();
            return v == null ? "no voice client" : v.Describe();
        }

        /// What the Talk buttons (More, Find parts) and the heads-up line show now.
        public static string TalkButtons()
        {
            var c = CultureInfo.InvariantCulture;
            string Btn(string where, GlassButton b) => b == null ? $"{where}: none" :
                string.Format(c, "{0}: \"{1}\" style={2} selected={3} bar={4} state={5}", where, b.Text, b.style, b.selected,
                    b.indicator != null && b.indicator.gameObject.activeSelf ? b.indicator.transform.localScale.x.ToString("0.00", c) : "-", b.State);
            var more = Services.Get<ScenePanel>();
            var find = Services.Get<PartsBrowser>();
            var line = StatusLine.Current;
            return $"{Btn("Settings", more != null ? more.talk : null)} | {Btn("Find parts", find != null ? find.talk : null)} | " +
                   $"line=\"{line?.Message}\" dot={(line != null && line.dot != null ? line.dot.transform.localScale.x.ToString("0.00", c) : "-")}";
        }
    }
}
