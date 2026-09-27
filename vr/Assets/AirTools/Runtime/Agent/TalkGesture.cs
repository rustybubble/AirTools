namespace AirTools.Agent
{
    /// How the Talk press was used: a tap toggles listening on, a hold listens while pressed.
    public enum TalkMode : byte { None, Tap, Hold }

    /// Idle → Pressed (listening; tap or hold not known yet) → Toggled (a tap: listening until the end of speech) or
    /// Holding (a hold: listening until the release) → Releasing (released in mid-word: finishing the word) → Idle.
    public enum TalkPhase : byte { Idle, Pressed, Toggled, Holding, Releasing }

    /// Why listening stopped.
    public enum TalkStop : byte { None, Release, SecondTap, EndOfSpeech, MaxLength, NoSpeech, Cancel }

    /// What the caller does after a press edge: start listening, stop and send, or nothing.
    public enum TalkAction : byte { None, Start, Stop }

    /// The Talk button's tap-or-hold state machine (pure; times in seconds on any one clock).
    ///   • A press shorter than tapSeconds toggles listening on; it stops at the end of speech (endSilenceSeconds of
    ///     silence after speech), at maxSeconds, at noSpeechSeconds without any speech, or on a second tap (counted only
    ///     minToggleSeconds after the first press, so a flickering pinch can't stop it at once).
    ///   • A longer press is hold-to-talk: the release stops it — unless it came in mid-word (speech within
    ///     releaseSpeechSeconds), then it finishes the word (releaseTailSeconds of silence). A press back within that
    ///     tail resumes the hold (the pinch dropped and came back).
    /// settings-assets: that is settings.style Auto. The style is read when listening starts (a change mid-utterance
    /// applies to the next one):
    ///   • Toggle — every press toggles, however long: listening starts at the press and stops on a second press (after
    ///     minToggleSeconds), at the end of speech, at noSpeechSeconds without speech or at maxSeconds. A release does
    ///     nothing.
    ///   • Hold — every press holds: listening until the release (finishing a word in progress, as above) or maxSeconds;
    ///     no end-of-speech stop.
    public sealed class TalkGesture
    {
        readonly VoiceSettings m_Set;
        AirTools.Core.TalkStyle m_Style;
        bool m_FirstUp;

        /// The style the current / last listening started with.
        public AirTools.Core.TalkStyle Style => m_Style;

        public TalkGesture(VoiceSettings settings) { m_Set = settings ?? new VoiceSettings(); }

        public TalkPhase Phase { get; private set; }
        public TalkMode Mode { get; private set; }
        public TalkStop LastStop { get; private set; }
        /// When listening started (the first press).
        public float StartedAt { get; private set; }
        /// How long the (first) press lasted; while still pressed, how long so far at the last event.
        public float PressSeconds { get; private set; }
        public bool Listening => Phase != TalkPhase.Idle;
        /// The press is (still) held: hold-to-talk copy ("release to send").
        public bool Held => Phase == TalkPhase.Holding;

        float m_DownAt;

        /// The button went down.
        public TalkAction Down(float now)
        {
            switch (Phase)
            {
                case TalkPhase.Idle:
                    m_Style = m_Set.style;   // settings-assets
                    m_FirstUp = false;
                    Phase = m_Style == AirTools.Core.TalkStyle.Toggle ? TalkPhase.Toggled
                          : m_Style == AirTools.Core.TalkStyle.Hold ? TalkPhase.Holding : TalkPhase.Pressed;
                    Mode = m_Style == AirTools.Core.TalkStyle.Toggle ? TalkMode.Tap
                         : m_Style == AirTools.Core.TalkStyle.Hold ? TalkMode.Hold : TalkMode.None;
                    LastStop = TalkStop.None;
                    StartedAt = m_DownAt = now;
                    PressSeconds = 0f;
                    return TalkAction.Start;
                case TalkPhase.Toggled:
                    if (now - m_DownAt < m_Set.minToggleSeconds) return TalkAction.None;   // the same press flickering
                    return Stop(TalkStop.SecondTap);
                case TalkPhase.Releasing:
                    Phase = TalkPhase.Holding;   // the pinch came back: still holding
                    return TalkAction.None;
                default:
                    return TalkAction.None;
            }
        }

        /// The button came up. speakingNow: the voice detector heard speech within releaseSpeechSeconds.
        public TalkAction Up(float now, bool speakingNow)
        {
            switch (Phase)
            {
                case TalkPhase.Pressed:
                    PressSeconds = now - m_DownAt;
                    if (PressSeconds < m_Set.tapSeconds)
                    {
                        Phase = TalkPhase.Toggled;
                        Mode = TalkMode.Tap;
                        return TalkAction.None;
                    }
                    Mode = TalkMode.Hold;
                    return Release(speakingNow);
                case TalkPhase.Holding:
                    PressSeconds = now - m_DownAt;
                    return Release(speakingNow);
                case TalkPhase.Toggled:
                    // settings-assets: Toggle — the first press's release only records how long it was.
                    if (m_Style == AirTools.Core.TalkStyle.Toggle && !m_FirstUp) { m_FirstUp = true; PressSeconds = now - m_DownAt; }
                    return TalkAction.None;
                default:
                    return TalkAction.None;
            }
        }

        TalkAction Release(bool speakingNow)
        {
            if (speakingNow && m_Set.releaseTailSeconds > 0f) { Phase = TalkPhase.Releasing; return TalkAction.None; }
            return Stop(TalkStop.Release);
        }

        /// Every frame while listening: the detector's state → why to stop now (None = keep listening).
        public TalkStop Tick(float now, bool hasSpeech, float silenceSeconds)
        {
            if (Phase == TalkPhase.Idle) return TalkStop.None;
            if (now - StartedAt >= m_Set.maxSeconds) return StopReason(TalkStop.MaxLength);
            switch (Phase)
            {
                case TalkPhase.Pressed:
                    if (now - m_DownAt >= m_Set.tapSeconds) { Phase = TalkPhase.Holding; Mode = TalkMode.Hold; PressSeconds = now - m_DownAt; }
                    break;
                case TalkPhase.Holding:
                    PressSeconds = now - m_DownAt;
                    break;
                case TalkPhase.Releasing:
                    if (!hasSpeech || silenceSeconds >= m_Set.releaseTailSeconds) return StopReason(TalkStop.Release);
                    break;
                case TalkPhase.Toggled:
                    if (hasSpeech && silenceSeconds >= m_Set.endSilenceSeconds) return StopReason(TalkStop.EndOfSpeech);
                    if (!hasSpeech && now - StartedAt >= m_Set.noSpeechSeconds) return StopReason(TalkStop.NoSpeech);
                    break;
            }
            return TalkStop.None;
        }

        /// Stop now (window closed, app paused, a harness call).
        public TalkStop Cancel() => Listening ? StopReason(TalkStop.Cancel) : TalkStop.None;

        /// Stop now with a reason (EndTalk: an explicit release).
        public TalkStop Force(TalkStop reason) => Listening ? StopReason(reason) : TalkStop.None;

        TalkAction Stop(TalkStop reason) { StopReason(reason); return TalkAction.Stop; }

        TalkStop StopReason(TalkStop reason)
        {
            if (Mode == TalkMode.None) Mode = TalkMode.Hold;   // stopped while still pressed (max length, cancel)
            Phase = TalkPhase.Idle;
            LastStop = reason;
            return reason;
        }

        public void Reset() { Phase = TalkPhase.Idle; Mode = TalkMode.None; LastStop = TalkStop.None; PressSeconds = 0f; m_FirstUp = false; }
    }
}
