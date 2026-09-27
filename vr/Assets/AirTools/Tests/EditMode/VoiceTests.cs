using System;
using AirTools.Agent;
using NUnit.Framework;

namespace AirTools.Tests
{
    /// Tap-or-hold Talk (voice lane): the voice detector on synthetic signals, the tap/hold state machine, the whole
    /// capture pipeline on a simulated clock (pre-roll, trim, what is sent), the ring and trim arithmetic, and the WAV.
    /// All pure: no Unity audio.
    public class VoiceTests
    {
        const int Rate = 16000;
        const float Frame = VoiceActivity.FrameSeconds;

        static VoiceActivity Run(float[] signal, VoiceSettings set = null)
        {
            var vad = new VoiceActivity(set ?? new VoiceSettings());
            vad.Reset(Rate);
            int f = vad.FrameLength;
            for (int i = 0; i + f <= signal.Length; i += f) vad.Push(signal, i, f);
            return vad;
        }

        // ---------------- VoiceActivity ----------------

        [Test]
        public void Vad_SpeechThenSilence_FindsTheSpeechAndTheSilenceAfter()
        {
            // 0.3 s room noise, 1.5 s speech, 1.5 s noise.
            var vad = Run(VoiceSignals.Speech(Rate, 3.3f, 0.3f, 1.5f));
            Assert.IsTrue(vad.HasSpeech);
            Assert.AreEqual(0.3f, vad.FirstSpeechFrame * Frame, 0.06f, "speech starts at 0.3 s");
            Assert.AreEqual(1.8f, (vad.LastSpeechFrame + 1) * Frame, 0.08f, "speech ends at 1.8 s");
            Assert.AreEqual(1.5f, vad.SilenceSeconds, 0.1f, "silence after it");
            Assert.AreEqual(1.5f, vad.SpeechSeconds, 0.2f);
            Assert.IsFalse(vad.InSpeech);
            Assert.Less(VoiceActivity.Db(vad.Floor), -45f, "floor ≈ the noise (−54 dBFS)");
        }

        [TestCase(0.0005f)]
        [TestCase(0.002f)]
        [TestCase(0.01f)]
        [TestCase(0.05f)]
        public void Vad_NoiseOnly_IsNeverSpeech(float noise)
        {
            var vad = Run(VoiceSignals.Noise(Rate, 6f, noise, seed: 7));
            Assert.IsFalse(vad.HasSpeech, $"noise at {VoiceActivity.Db(noise):0} dBFS");
            Assert.AreEqual(0, vad.SpeechFrames);
        }

        [Test]
        public void Vad_AClickIsNotSpeech()
        {
            var s = VoiceSignals.WithClick(VoiceSignals.Noise(Rate, 3f, 0.002f), Rate, 1.0f, seconds: 0.02f);
            Assert.IsFalse(Run(s).HasSpeech);
        }

        [Test]
        public void Vad_DigitalSilence_IsNeverSpeech()
        {
            var vad = Run(new float[Rate * 2]);
            Assert.IsFalse(vad.HasSpeech);
            Assert.AreEqual(0f, vad.Level);
        }

        [Test]
        public void Vad_ZerosFromAColdMic_DontSetTheFloor()
        {
            // 0.3 s of zeros (the ring before the mic delivered), then −40 dBFS room noise: not speech ...
            var noise = VoiceSignals.Noise(Rate, 4f, 0.01f, seed: 5);
            Array.Clear(noise, 0, (int)(0.3f * Rate));
            Assert.IsFalse(Run(noise).HasSpeech);
            // ... and speech after it is found where it starts.
            var s = VoiceSignals.Speech(Rate, 4f, 1f, 1.5f, speechRms: 0.1f, noiseRms: 0.01f);
            Array.Clear(s, 0, (int)(0.3f * Rate));
            var vad = Run(s);
            Assert.IsTrue(vad.HasSpeech);
            Assert.AreEqual(1f, vad.FirstSpeechFrame * Frame, 0.08f);
        }

        [Test]
        public void Vad_SpeechInLoudNoise_StillFound()
        {
            // −34 dBFS babble-level noise, speech 12 dB over it.
            var vad = Run(VoiceSignals.Speech(Rate, 4f, 0.5f, 2f, speechRms: 0.08f, noiseRms: 0.02f, seed: 3));
            Assert.IsTrue(vad.HasSpeech);
            Assert.AreEqual(0.5f, vad.FirstSpeechFrame * Frame, 0.1f);
            Assert.AreEqual(1.5f, vad.SilenceSeconds, 0.15f);
        }

        [Test]
        public void Vad_LevelFollowsTheVoice()
        {
            var set = new VoiceSettings();
            var vad = new VoiceActivity(set);
            vad.Reset(Rate);
            var s = VoiceSignals.Speech(Rate, 3f, 0.5f, 1.5f);
            int f = vad.FrameLength;
            float inSpeech = 0f;
            for (int i = 0; i + f <= s.Length; i += f)
            {
                vad.Push(s, i, f);
                if (Math.Abs(i / (float)Rate - 1.0f) < 0.011f) inSpeech = vad.Level;
            }
            Assert.Greater(inSpeech, 0.5f, "level while talking");
            Assert.Less(vad.Level, 0.1f, "level back down in the silence");
        }

        [Test]
        public void Vad_ResetStartsOver()
        {
            var vad = Run(VoiceSignals.Speech(Rate, 3f, 0.5f, 1f));
            Assert.IsTrue(vad.HasSpeech);
            vad.Reset(Rate);
            Assert.IsFalse(vad.HasSpeech);
            Assert.AreEqual(0, vad.Frames);
            Assert.AreEqual(-1, vad.LastSpeechFrame);
        }

        // ---------------- TalkGesture (the tap / hold state machine) ----------------

        [Test]
        public void Gesture_Tap_ListensUntilTheSilenceAfterSpeech()
        {
            var g = new TalkGesture(new VoiceSettings());
            Assert.AreEqual(TalkAction.Start, g.Down(10f));
            Assert.AreEqual(TalkAction.None, g.Up(10.2f, false));
            Assert.AreEqual(TalkPhase.Toggled, g.Phase);
            Assert.AreEqual(TalkMode.Tap, g.Mode);
            Assert.AreEqual(0.2f, g.PressSeconds, 1e-4f);
            Assert.AreEqual(TalkStop.None, g.Tick(11f, false, 0f), "no speech yet: keep listening");
            Assert.AreEqual(TalkStop.None, g.Tick(12f, true, 0.5f), "a pause mid-sentence");
            Assert.AreEqual(TalkStop.EndOfSpeech, g.Tick(13f, true, 1.0f));
            Assert.AreEqual(TalkPhase.Idle, g.Phase);
            Assert.AreEqual(TalkStop.EndOfSpeech, g.LastStop);
            Assert.AreEqual(TalkAction.None, g.Up(13.1f, false), "a stray release after it is ignored");
        }

        [Test]
        public void Gesture_TapTap_Sends()
        {
            var g = new TalkGesture(new VoiceSettings());
            g.Down(0f); g.Up(0.15f, false);
            Assert.AreEqual(TalkStop.None, g.Tick(1.5f, true, 0.1f));
            Assert.AreEqual(TalkAction.Stop, g.Down(2f));
            Assert.AreEqual(TalkStop.SecondTap, g.LastStop);
            Assert.AreEqual(TalkMode.Tap, g.Mode);
            Assert.AreEqual(TalkAction.None, g.Up(2.1f, false), "the second tap's release does nothing");
            Assert.IsFalse(g.Listening);
        }

        [Test]
        public void Gesture_AFlickeringPinchIsNotASecondTap()
        {
            var g = new TalkGesture(new VoiceSettings());
            g.Down(0f);
            g.Up(0.1f, false);
            Assert.AreEqual(TalkAction.None, g.Down(0.2f), "the pinch came back 0.1 s later: same press");
            Assert.AreEqual(TalkAction.None, g.Up(0.3f, false));
            Assert.AreEqual(TalkPhase.Toggled, g.Phase);
        }

        [Test]
        public void Gesture_Hold_ReleaseSends()
        {
            var g = new TalkGesture(new VoiceSettings());
            g.Down(0f);
            g.Tick(0.2f, false, 0f);
            Assert.IsFalse(g.Held, "not a hold yet");
            g.Tick(0.4f, true, 0f);
            Assert.IsTrue(g.Held);
            Assert.AreEqual(TalkMode.Hold, g.Mode);
            Assert.AreEqual(TalkStop.None, g.Tick(2f, true, 1.5f), "a hold ignores silence");
            Assert.AreEqual(TalkAction.Stop, g.Up(2.5f, false));
            Assert.AreEqual(TalkStop.Release, g.LastStop);
            Assert.AreEqual(2.5f, g.PressSeconds, 1e-4f);
        }

        [Test]
        public void Gesture_Hold_ReleasedMidWord_FinishesTheWord()
        {
            var g = new TalkGesture(new VoiceSettings());
            g.Down(0f);
            g.Tick(0.5f, true, 0f);
            Assert.AreEqual(TalkAction.None, g.Up(1f, speakingNow: true), "the pinch dropped mid-word");
            Assert.AreEqual(TalkPhase.Releasing, g.Phase);
            Assert.AreEqual(TalkStop.None, g.Tick(1.4f, true, 0.2f));
            Assert.AreEqual(TalkStop.Release, g.Tick(1.9f, true, 0.6f));
            Assert.AreEqual(TalkMode.Hold, g.Mode);
        }

        [Test]
        public void Gesture_Hold_PinchComesBack_KeepsHolding()
        {
            var g = new TalkGesture(new VoiceSettings());
            g.Down(0f);
            g.Tick(0.5f, true, 0f);
            g.Up(1f, true);
            Assert.AreEqual(TalkAction.None, g.Down(1.1f));
            Assert.AreEqual(TalkPhase.Holding, g.Phase);
            Assert.AreEqual(TalkAction.Stop, g.Up(3f, false));
            Assert.AreEqual(3f, g.PressSeconds, 1e-4f);
        }

        [Test]
        public void Gesture_Tap_NoSpeech_GivesUp()
        {
            var set = new VoiceSettings();
            var g = new TalkGesture(set);
            g.Down(0f); g.Up(0.1f, false);
            Assert.AreEqual(TalkStop.None, g.Tick(set.noSpeechSeconds - 0.01f, false, 0f));
            Assert.AreEqual(TalkStop.NoSpeech, g.Tick(set.noSpeechSeconds, false, 0f));
        }

        [Test]
        public void Gesture_MaxLength_StopsAnyMode()
        {
            var set = new VoiceSettings();
            var g = new TalkGesture(set);
            g.Down(0f); g.Up(0.1f, false);
            Assert.AreEqual(TalkStop.MaxLength, g.Tick(set.maxSeconds, true, 0.1f));
            g.Down(20f);
            g.Tick(20.5f, true, 0f);
            Assert.AreEqual(TalkStop.MaxLength, g.Tick(20f + set.maxSeconds, true, 0f));
            Assert.AreEqual(TalkMode.Hold, g.Mode);
        }

        [Test]
        public void Gesture_CancelStops()
        {
            var g = new TalkGesture(new VoiceSettings());
            Assert.AreEqual(TalkStop.None, g.Cancel(), "nothing to cancel");
            g.Down(0f);
            Assert.AreEqual(TalkStop.Cancel, g.Cancel());
            Assert.IsFalse(g.Listening);
        }

        // ---------------- VoiceCapture (the whole pipeline, simulated clock) ----------------

        [Test]
        public void Capture_Tap_Speech_Silence_Sends()
        {
            var set = new VoiceSettings();
            var cap = new VoiceCapture(set);
            // Tap at 0.5 s; speech 0.7–2.7 s; then room noise.
            var u = cap.Simulate(VoiceSignals.Speech(Rate, 8f, 0.7f, 2f), Rate, pressAt: 0.5f, pressSeconds: 0.15f);
            Assert.AreEqual(TalkMode.Tap, u.Mode);
            Assert.AreEqual(TalkStop.EndOfSpeech, u.Stop);
            Assert.IsTrue(u.Send);
            Assert.AreEqual(0.15f, u.PressSeconds, 0.02f);
            Assert.AreEqual(2f, u.SpeechSeconds, 0.25f);
            Assert.AreEqual(set.endSilenceSeconds, u.TrailingSilence, 0.06f, "stopped 1 s after the speech");
            Assert.AreEqual(set.preRollSeconds, u.PreRollSeconds, 0.021f);
            // Kept: the speech plus 150 ms each side (trimmed from 0.4 + 2.2 + 1.0 s captured).
            Assert.AreEqual(2f + 2 * set.trimMarginSeconds, u.KeptSeconds, 0.1f);
            Assert.AreEqual(0.4f + 2.2f + 1.0f, u.TotalSeconds, 0.1f);
            // The kept audio starts 150 ms before the speech: 0.7 − 0.15 s on the mic's clock.
            float keptStartOnMic = 0.5f - u.PreRollSeconds + u.Start / (float)Rate;
            Assert.AreEqual(0.55f, keptStartOnMic, 0.05f);
        }

        [Test]
        public void Capture_TapTap_Sends()
        {
            var u = new VoiceCapture().Simulate(VoiceSignals.Speech(Rate, 8f, 0.7f, 4f), Rate, 0.5f, 0.15f, secondTapAt: 2.5f);
            Assert.AreEqual(TalkStop.SecondTap, u.Stop);
            Assert.AreEqual(TalkMode.Tap, u.Mode);
            Assert.IsTrue(u.Send);
            Assert.AreEqual(2.0f, u.TotalSeconds - u.PreRollSeconds, 0.05f, "stopped at the second tap");
        }

        [Test]
        public void Capture_Hold_Release_Sends()
        {
            var u = new VoiceCapture().Simulate(VoiceSignals.Speech(Rate, 8f, 0.7f, 2f), Rate, 0.5f, pressSeconds: 2.5f);
            Assert.AreEqual(TalkMode.Hold, u.Mode);
            Assert.AreEqual(TalkStop.Release, u.Stop);
            Assert.IsTrue(u.Send);
            Assert.AreEqual(2.5f, u.PressSeconds, 0.03f);
            Assert.AreEqual(2f, u.SpeechSeconds, 0.25f);
        }

        [Test]
        public void Capture_Hold_ReleasedMidSentence_KeepsTheEnd()
        {
            // Speech 0.7–3.7 s; the hold ends at 2.0 s (the pinch dropped): listening goes on to the end of the word run.
            var set = new VoiceSettings();
            var u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(Rate, 8f, 0.7f, 3f), Rate, 0.5f, pressSeconds: 1.5f);
            Assert.AreEqual(TalkStop.Release, u.Stop);
            Assert.AreEqual(3f + 2 * set.trimMarginSeconds, u.KeptSeconds, 0.15f, "the words after the release are in");
            Assert.Greater(u.SpeechSeconds, 2.4f);
            Assert.AreEqual(set.releaseTailSeconds, u.TrailingSilence, 0.06f);
        }

        [Test]
        public void Capture_NoiseOnly_Tap_SendsNothing()
        {
            var set = new VoiceSettings();
            var u = new VoiceCapture(set).Simulate(VoiceSignals.Noise(Rate, 10f, 0.01f), Rate, 0.5f, 0.15f);
            Assert.AreEqual(TalkStop.NoSpeech, u.Stop);
            Assert.IsFalse(u.Send);
            Assert.AreEqual(0, u.Count);
            Assert.AreEqual(set.noSpeechSeconds, u.TotalSeconds - u.PreRollSeconds, 0.05f);
        }

        [Test]
        public void Capture_VeryShortNoise_Hold_SendsNothing()
        {
            var u = new VoiceCapture().Simulate(VoiceSignals.Noise(Rate, 3f, 0.004f), Rate, 0.5f, pressSeconds: 0.45f);
            Assert.AreEqual(TalkMode.Hold, u.Mode);
            Assert.AreEqual(TalkStop.Release, u.Stop);
            Assert.IsFalse(u.Send);
        }

        [Test]
        public void Capture_WordsBeforeThePress_AreKept()
        {
            // The headset case: speech from 1.2 s, the press only lands at 2.0 s (0.8 s in).
            var set = new VoiceSettings();
            var u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(Rate, 8f, 1.2f, 2f), Rate, pressAt: 2.0f, pressSeconds: 0.15f);
            Assert.IsTrue(u.Send);
            Assert.GreaterOrEqual(u.PreRollSeconds, 0.8f + set.trimMarginSeconds - 0.03f, "back to the start of the speech");
            Assert.LessOrEqual(u.PreRollSeconds, set.lookBackSeconds + 0.02f);
            Assert.AreEqual(2f, u.SpeechSeconds, 0.25f);
            float keptStartOnMic = 2.0f - u.PreRollSeconds + u.Start / (float)Rate;
            Assert.AreEqual(1.2f - set.trimMarginSeconds, keptStartOnMic, 0.06f);
        }

        [Test]
        public void Capture_QuietBeforeThePress_FixedPreRoll()
        {
            var set = new VoiceSettings();
            var u = new VoiceCapture(set).Simulate(VoiceSignals.Speech(Rate, 8f, 2.3f, 1.5f), Rate, pressAt: 2.0f, pressSeconds: 0.15f);
            Assert.AreEqual(set.preRollSeconds, u.PreRollSeconds, 0.021f);
            Assert.IsTrue(u.Send);
        }

        [Test]
        public void Capture_ColdMic_PreRollIsWhatWasRecorded()
        {
            var u = new VoiceCapture().Simulate(VoiceSignals.Speech(Rate, 6f, 0.3f, 1.5f), Rate, pressAt: 0.1f, pressSeconds: 0.15f);
            Assert.LessOrEqual(u.PreRollSeconds, 0.1f + 0.021f);
            Assert.IsTrue(u.Send);
        }

        [Test]
        public void Capture_PumpAllocatesNothing()
        {
            var cap = new VoiceCapture();
            var mic = new ArrayMicSource(VoiceSignals.Speech(Rate, 14f, 0.5f, 10f), Rate);
            mic.AdvanceTo(1f);
            Assert.AreEqual(TalkAction.Start, cap.Down(1f, mic));
            float t = 1f;
            for (int i = 0; i < 20; i++) { t += 1f / 72f; mic.AdvanceTo(t); cap.Update(t); }   // JIT
            long before;
            try { before = GC.GetAllocatedBytesForCurrentThread(); }
            catch (Exception e) when (e is NotSupportedException || e is NotImplementedException) { Assert.Ignore("GC.GetAllocatedBytesForCurrentThread unsupported"); return; }
            for (int i = 0; i < 400; i++) { t += 1f / 72f; mic.AdvanceTo(t); cap.Update(t); }
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.IsTrue(cap.Listening);
            Assert.AreEqual(0, after - before, "bytes allocated by 400 frames of listening");
        }

        [Test]
        public void Capture_RingWrap_ReadsInOrder()
        {
            // A 2 s ring, 4.1 s into the mic (the head at 0.1 s of the ring): the 0.4 s pre-roll straddles the wrap and
            // must come back in order.
            var ramp = new float[Rate * 5];
            for (int i = 0; i < ramp.Length; i++) ramp[i] = i / (float)ramp.Length;
            var mic = new ArrayMicSource(ramp, Rate, ringSeconds: 2f);
            mic.AdvanceTo(4.1f);
            var set = new VoiceSettings { lookBackSeconds = 0.4f };
            var cap = new VoiceCapture(set);
            Assert.AreEqual(TalkAction.Start, cap.Down(4.1f, mic));
            mic.AdvanceTo(4.4f);
            cap.Update(4.4f);
            int n = cap.FramesCaptured;
            Assert.Greater(n, Rate / 2);
            for (int i = 1; i < n; i++) Assert.Greater(cap.Buffer[i], cap.Buffer[i - 1], $"sample {i} out of order");
            float firstOnMic = cap.Buffer[0] * ramp.Length / Rate;
            Assert.AreEqual(4.1f - set.preRollSeconds, firstOnMic, 0.021f);
        }

        // ---------------- the arithmetic ----------------

        [Test]
        public void Ring_AvailableAndBack()
        {
            Assert.AreEqual(35, VoiceMath.Available(970, 5, 1000));
            Assert.AreEqual(0, VoiceMath.Available(5, 5, 1000));
            Assert.AreEqual(990, VoiceMath.Wrap(-10, 1000));
            // 30 frames back from 5 in a 1000 ring (frames of 10): 975 → aligned down to 970.
            Assert.AreEqual(970, VoiceMath.Back(5, 1000, 10, 30, 1000));
            // Only 12 recorded: 993 → 990.
            Assert.AreEqual(990, VoiceMath.Back(5, 1000, 10, 30, 12));
            // Never more than the ring minus two frames.
            Assert.GreaterOrEqual(VoiceMath.Available(VoiceMath.Back(500, 1000, 10, 5000, 5000), 500, 1000), 980);
            Assert.AreEqual(0, VoiceMath.Back(0, 0, 10, 30, 30));
        }

        [Test]
        public void PreRoll_Choice()
        {
            float[] Rms(params (int frames, float rms)[] parts)
            {
                int n = 0; foreach (var p in parts) n += p.frames;
                var a = new float[n]; int k = 0;
                foreach (var p in parts) for (int i = 0; i < p.frames; i++) a[k++] = p.rms;
                return a;
            }
            const int baseF = 20, gap = 15, onset = 3, margin = 7;
            float on = 3.2f, abs = 0.003f;
            // Quiet: the fixed 0.4 s.
            var quiet = Rms((75, 0.002f));
            Assert.AreEqual(baseF, VoiceMath.ChoosePreRoll(quiet, quiet.Length, baseF, gap, onset, margin, on, abs));
            // Speech for the last 40 frames (0.8 s) up to the press: back to its start + the margin.
            var talking = Rms((35, 0.002f), (40, 0.1f));
            Assert.AreEqual(40 + margin, VoiceMath.ChoosePreRoll(talking, talking.Length, baseF, gap, onset, margin, on, abs));
            // Speech with a short pause (0.2 s) inside it: one run.
            var paused = Rms((20, 0.002f), (20, 0.1f), (10, 0.002f), (25, 0.1f));
            Assert.AreEqual(55 + margin, VoiceMath.ChoosePreRoll(paused, paused.Length, baseF, gap, onset, margin, on, abs));
            // Speech that ended 0.5 s before the press (someone else): the fixed pre-roll.
            var ended = Rms((20, 0.002f), (30, 0.1f), (25, 0.002f));
            Assert.AreEqual(baseF, VoiceMath.ChoosePreRoll(ended, ended.Length, baseF, gap, onset, margin, on, abs));
            // A click right before the press isn't speech.
            var click = Rms((73, 0.002f), (1, 0.5f), (1, 0.002f));
            Assert.AreEqual(baseF, VoiceMath.ChoosePreRoll(click, click.Length, baseF, gap, onset, margin, on, abs));
            // Loud for the whole look-back: no quiet frame to tell speech from noise by.
            var all = Rms((75, 0.1f));
            Assert.AreEqual(baseF, VoiceMath.ChoosePreRoll(all, all.Length, baseF, gap, onset, margin, on, abs), "no floor to tell speech by: the fixed pre-roll");
            // Zeros (the mic not delivering yet) then room noise above absMin: still the fixed pre-roll.
            var zeros = Rms((30, 0f), (45, 0.01f));
            Assert.AreEqual(baseF, VoiceMath.ChoosePreRoll(zeros, zeros.Length, baseF, gap, onset, margin, on, abs));
            // Shorter than the pre-roll (a cold mic): what there is.
            var cold = Rms((5, 0.002f));
            Assert.AreEqual(5, VoiceMath.ChoosePreRoll(cold, cold.Length, baseF, gap, onset, margin, on, abs));
            Assert.AreEqual(0, VoiceMath.ChoosePreRoll(cold, 0, baseF, gap, onset, margin, on, abs));
        }

        [Test]
        public void Trim_KeepsTheSpeechWithMargins()
        {
            VoiceMath.Trim(1000, 200, 600, 50, out int s, out int c);
            Assert.AreEqual((150, 500), (s, c));
            VoiceMath.Trim(1000, 20, 990, 50, out s, out c);
            Assert.AreEqual((0, 1000), (s, c), "clamped to the buffer");
            VoiceMath.Trim(1000, -1, -1, 50, out s, out c);
            Assert.AreEqual((0, 1000), (s, c), "no speech: all");
            VoiceMath.Trim(0, 0, 0, 50, out s, out c);
            Assert.AreEqual((0, 0), (s, c));
        }

        [Test]
        public void Wav_EncodeARange_DecodeBack()
        {
            var x = new float[1000];
            for (int i = 0; i < x.Length; i++) x[i] = (float)Math.Sin(i * 0.05) * 0.8f;
            var wav = WavPcm.Encode(x, 100, 500, 1, Rate);
            Assert.AreEqual(44 + 500 * 2, wav.Length);
            var back = WavPcm.Decode(wav, out int ch, out int rate);
            Assert.AreEqual(1, ch);
            Assert.AreEqual(Rate, rate);
            Assert.AreEqual(500, back.Length);
            for (int i = 0; i < 500; i++) Assert.AreEqual(x[100 + i], back[i], 1.0 / 16000, $"sample {i}");
            Assert.IsNull(WavPcm.Decode(new byte[10], out _, out _));
        }

        [Test]
        public void Wav_DecodeMono_MixesAndResamples()
        {
            // 48 kHz stereo, left = right = a 0.5 ramp → 16 kHz mono, a third of the frames.
            var st = new float[4800 * 2];
            for (int i = 0; i < 4800; i++) { st[2 * i] = 0.5f; st[2 * i + 1] = 0.5f; }
            var mono = WavPcm.DecodeMono(WavPcm.Encode(st, 0, st.Length, 2, 48000), Rate);
            Assert.AreEqual(1600, mono.Length);
            Assert.AreEqual(0.5f, mono[800], 1e-3f);
        }

        [Test]
        public void Utterance_LogLine()
        {
            var u = new Utterance
            {
                Mode = TalkMode.Tap, Stop = TalkStop.EndOfSpeech, HasSpeech = true, PressSeconds = 0.21f, SpeechSeconds = 2.34f,
                TrailingSilence = 1.02f, PreRollSeconds = 0.4f, TotalSeconds = 4.52f, KeptSeconds = 2.98f, FloorDb = -52.4f, PeakDb = -18.2f,
            };
            Assert.AreEqual("Voice utterance mode=tap stop=end-of-speech press=0.21s speech=2.34s trailing=1.02s preroll=0.40s kept=2.98s total=4.52s bytes=95404 floor=-52dB peak=-18dB",
                u.LogLine(95404));
        }

        [Test]
        public void ArrayMic_ReadsTheNewestLap()
        {
            var sig = new float[3000];
            for (int i = 0; i < sig.Length; i++) sig[i] = i;
            var mic = new ArrayMicSource(sig, 1000, ringSeconds: 1f);   // ring 1000
            mic.AdvanceTo(2.5f);   // 2500 written; the write head at 500
            Assert.AreEqual(500, mic.Position);
            Assert.AreEqual(1000, mic.Recorded);
            var f = new float[20];
            mic.Read(f, 480);
            Assert.AreEqual(2480f, f[0]);
            mic.Read(f, 600);
            Assert.AreEqual(1600f, f[0], "older than the head: the previous lap");
        }
    }
}
