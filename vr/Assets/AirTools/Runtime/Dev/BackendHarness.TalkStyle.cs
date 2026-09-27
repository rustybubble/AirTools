using System.Collections.Generic;
using System.Text;
using AirTools.Agent;
using AirTools.Core;

namespace AirTools.Dev
{
    /// settings-assets: Settings ▸ Talk (Hold / Toggle / Auto) through the pure capture pipeline, driven with `unity command
    /// eval` (no Play mode needed): TalkStyleCheck() → [AirTools.Check] talk.* lines. AgentHarness.TalkStyle("hold") sets it.
    public static partial class BackendHarness
    {
        /// The Talk-style state machine on synthetic speech: Toggle ignores the release and sends at the end of speech or
        /// on a second press (not a flicker); Hold sends on the release, even a short one, and never stops at the end of
        /// speech; Auto is the tap-or-hold. (id, ok, detail) per check.
        public static List<(string id, bool ok, string detail)> TalkStyleChecks()
        {
            const int rate = 16000;
            var list = new List<(string, bool, string)>();
            VoiceSettings Set(TalkStyle s) { var v = new VoiceSettings(); v.style = s; return v; }

            // Toggle: a 2.5 s press over 2 s of speech — the release doesn't stop it; the silence after the speech does.
            var set = Set(TalkStyle.Toggle);
            var u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(rate, 8f, 0.7f, 2f), rate, 0.5f, 2.5f);
            list.Add(("talk.toggle.long_press_listens_on", u.Mode == TalkMode.Tap && u.Stop == TalkStop.EndOfSpeech && u.Send && Near(u.PressSeconds, 2.5f, 0.05f), u.LogLine(0)));
            // Toggle: a second press while still talking sends.
            u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(rate, 8f, 0.7f, 4f), rate, 0.5f, 0.15f, secondTapAt: 2.5f);
            list.Add(("talk.toggle.second_press", u.Stop == TalkStop.SecondTap && u.Send, u.LogLine(0)));
            // Toggle: a flicker 0.2 s after the press is not a second press.
            u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(rate, 8f, 0.7f, 2f), rate, 0.5f, 0.1f, secondTapAt: 0.7f);
            list.Add(("talk.toggle.flicker_ignored", u.Stop == TalkStop.EndOfSpeech && u.Send, u.LogLine(0)));
            // Toggle: nothing said → gives up, nothing sent.
            u = new VoiceCapture(set).Simulate(VoiceSignals.Noise(rate, 10f, 0.01f), rate, 0.5f, 0.15f);
            list.Add(("talk.toggle.no_speech", u.Stop == TalkStop.NoSpeech && !u.Send, u.LogLine(0)));

            // Hold: a 2.5 s press over 2 s of speech — the release sends.
            set = Set(TalkStyle.Hold);
            u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(rate, 8f, 0.7f, 2f), rate, 0.5f, 2.5f);
            list.Add(("talk.hold.release", u.Mode == TalkMode.Hold && u.Stop == TalkStop.Release && u.Send, u.LogLine(0)));
            // Hold: held 5 s over 1 s of speech — no end-of-speech stop, the release ends it.
            u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(rate, 8f, 0.7f, 1f), rate, 0.5f, 5f);
            list.Add(("talk.hold.no_end_of_speech", u.Stop == TalkStop.Release && u.Send && u.PressSeconds > 4.9f, u.LogLine(0)));
            // Hold: a quick tap is a (short) hold, not a toggle: it stops at the release.
            u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(rate, 8f, 1.5f, 2f), rate, 0.5f, 0.15f);
            list.Add(("talk.hold.tap_is_short_hold", u.Mode == TalkMode.Hold && u.Stop == TalkStop.Release && !u.Send && u.TotalSeconds < 1.2f, u.LogLine(0)));

            // Auto: the tap-or-hold.
            set = Set(TalkStyle.Auto);
            u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(rate, 8f, 0.7f, 2f), rate, 0.5f, 0.15f);
            list.Add(("talk.auto.tap", u.Mode == TalkMode.Tap && u.Stop == TalkStop.EndOfSpeech && u.Send, u.LogLine(0)));
            u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(rate, 8f, 0.7f, 2f), rate, 0.5f, 2.5f);
            list.Add(("talk.auto.hold", u.Mode == TalkMode.Hold && u.Stop == TalkStop.Release && u.Send, u.LogLine(0)));
            return list;
        }

        /// TalkStyleChecks as [AirTools.Check] lines + a report.
        public static string TalkStyleCheck()
        {
            var sb = new StringBuilder();
            int pass = 0, total = 0;
            foreach (var (id, ok, detail) in TalkStyleChecks())
            {
                total++;
                if (ok) pass++;
                Log.Check(id, ok, detail);
                sb.Append(ok ? "PASS " : "FAIL ").Append(id).Append(' ').Append(detail).Append('\n');
            }
            return sb.Insert(0, $"{pass}/{total} talk-style checks (setting: {UserPrefs.Label(UserPrefs.Talk)})\n").ToString();
        }
    }
}
