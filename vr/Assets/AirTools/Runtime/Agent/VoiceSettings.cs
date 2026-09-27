using System;

namespace AirTools.Agent
{
    /// Tap-or-hold voice capture tunables (VoiceClient.settings; tune live with `unity command eval`). Pure data.
    [Serializable]
    public class VoiceSettings
    {
        // ---- the press ----
        /// settings-assets: how a press listens — Hold (while pressed), Toggle (press, press again or stop talking) or Auto
        /// (the tap-or-hold below). VoiceClient keeps it equal to the user's setting (UserPrefs.Talk, default Toggle); the
        /// pure pipeline and its tests default to Auto.
        public AirTools.Core.TalkStyle style = AirTools.Core.TalkStyle.Auto;
        /// A press shorter than this is a tap: it toggles listening on (stops at the end of speech, the max or a second
        /// tap). Longer is hold-to-talk: the release stops it.
        public float tapSeconds = 0.35f;
        /// A second tap counts only this long after the first press (a flickering ray pinch can't stop it at once).
        public float minToggleSeconds = 0.5f;
        /// Tap mode: this much silence after speech ends the utterance.
        public float endSilenceSeconds = 1.0f;
        /// Any mode: listening stops here.
        public float maxSeconds = 12f;
        /// Tap mode: no speech by now → give up ("Didn't catch that"), nothing sent.
        public float noSpeechSeconds = 6f;
        /// Hold mode: a release in mid-word (the pinch dropped) keeps listening until this much silence (0 = stop at once).
        public float releaseTailSeconds = 0.6f;
        /// Hold mode: "mid-word" = speech in the last this-many seconds before the release.
        public float releaseSpeechSeconds = 0.25f;

        // ---- the microphone ----
        /// Audio kept from before the press (the mic is pre-warmed on hover / palm-open).
        public float preRollSeconds = 0.4f;
        /// Up to this much before the press when speech was already going on at the press (first words aren't lost).
        public float lookBackSeconds = 1.5f;
        /// Kept around the speech when leading and trailing silence are trimmed off before sending.
        public float trimMarginSeconds = 0.15f;
        /// The pre-warmed mic stops after this long without a press (privacy, battery).
        public float prewarmSeconds = 5f;

        // ---- voice activity (VoiceActivity) ----
        /// Speech starts at this factor over the noise floor (3.2 ≈ +10 dB) ...
        public float onFactor = 3.2f;
        /// ... and continues above this one (2.0 ≈ +6 dB) while inside an utterance.
        public float offFactor = 2.0f;
        /// Nothing quieter than this is speech (RMS of full scale; 0.003 ≈ −50 dBFS).
        public float absMinRms = 0.003f;
        /// Frames above the threshold in a row before speech counts (a click is not speech).
        public float onsetSeconds = 0.06f;
        /// The noise floor is the quietest frame over this sliding window (its first 200 ms calibrate it).
        public float floorWindowSeconds = 1.5f;

        public VoiceSettings Clone() => (VoiceSettings)MemberwiseClone();
    }
}
