using UnityEngine;

namespace AirTools.Input
{
    /// D7 (UX W1.8): a hold-to-fire detector for the demo reset. The condition has to hold for `Seconds` without a break
    /// longer than `Grace` (a tracking blink); it warns after `WarnAfter` (the wearer sees what's about to happen and
    /// can let go), fires once, and must be released before it can fire again. Pure (EditMode-tested).
    public sealed class HoldChord
    {
        public enum Change : byte { None, Started, Warned, Fired, Cancelled }

        public float Seconds = 2f;
        public float Grace = 0.2f;
        public float WarnAfter = 0.5f;

        /// Holding now (started, not fired, not cancelled).
        public bool Holding { get; private set; }
        public float StartedAt { get; private set; } = -1f;
        public bool Warned { get; private set; }
        /// Fired, and the condition still held: waiting for the release.
        public bool Latched { get; private set; }
        /// The last Cancelled came after the warning (the wearer saw "keep holding" and let go).
        public bool CancelledAfterWarning { get; private set; }
        public int Fires { get; private set; }

        float m_LastTrue = -1f;

        /// 0..1 through the hold (0 when not holding).
        public float Progress(float now) => Holding && Seconds > 0f ? Mathf.Clamp01((now - StartedAt) / Seconds) : 0f;

        public Change Update(bool on, float now)
        {
            if (Latched)
            {
                if (on) m_LastTrue = now;
                else if (now - m_LastTrue > Grace) Latched = false;
                return Change.None;
            }
            if (on)
            {
                m_LastTrue = now;
                if (!Holding) { Holding = true; StartedAt = now; Warned = false; return Seconds <= 0f ? Fire() : Change.Started; }
                if (now - StartedAt >= Seconds) return Fire();
                if (!Warned && now - StartedAt >= WarnAfter) { Warned = true; return Change.Warned; }
                return Change.None;
            }
            if (Holding && now - m_LastTrue > Grace)
            {
                CancelledAfterWarning = Warned;
                Holding = false;
                Warned = false;
                StartedAt = -1f;
                return Change.Cancelled;
            }
            return Change.None;
        }

        Change Fire()
        {
            Holding = false;
            Warned = false;
            Latched = true;
            Fires++;
            return Change.Fired;
        }

        /// Forget any hold in progress (e.g. the reset was refused, or a UI button was pressed during the hold).
        public void Reset()
        {
            Holding = false; Warned = false; Latched = false; StartedAt = -1f; m_LastTrue = -1f;
        }
    }

    /// D7: the hands' reset pose, "stop" with both hands: both hands raised to face height in front of you, flat (≥ 4
    /// fingers straight), palms facing away from you, fingers pointing up, the hands apart. Nothing in AirTools uses it:
    /// tools take a pinch or a poke (≤ 3 straight fingers), the ring takes the left palm facing up, and Meta's system
    /// gesture takes a palm facing you. Palms at eye height sit above where windows and the Enter button are centred
    /// (15–25° below the eye line at 0.45 m, i.e. 12–20 cm below the eyes). Pure.
    public static class ResetPose
    {
        public const int MinFingers = 4;
        /// Palm normal · direction head → palm (1 = facing straight away from you).
        public const float MinOutward = 0.6f;
        /// Palm "up" (wrist → middle knuckle) · world up (1 = fingers straight up).
        public const float MinFingersUp = 0.6f;
        /// The palm centre at least this high: no more than 10 cm below the eyes.
        public const float MaxBelowEyes = 0.10f;
        /// Within this angle of where you look (the hands are tracked and in view).
        public const float MaxViewAngle = 60f;
        /// Two hands, not one hand seen twice or crossed arms.
        public const float MinApart = 0.2f;
        /// A palm moving more than this from where the hold started restarts the hold (waving isn't holding).
        public const float MaxDrift = 0.15f;

        public static bool Hand(in PalmFrame palm, int extended, Vector3 head, Vector3 headForward)
        {
            if (extended < MinFingers) return false;
            var toPalm = palm.Centre - head;
            if (toPalm.sqrMagnitude < 1e-6f) return false;
            if (Vector3.Dot(palm.Normal, toPalm.normalized) < MinOutward) return false;
            if (Vector3.Dot(palm.Up, Vector3.up) < MinFingersUp) return false;
            if (palm.Centre.y < head.y - MaxBelowEyes) return false;
            return Vector3.Angle(headForward, toPalm) <= MaxViewAngle;
        }

        public static bool Both(in PalmFrame left, int leftExtended, in PalmFrame right, int rightExtended, Vector3 head, Vector3 headForward) =>
            Hand(left, leftExtended, head, headForward) && Hand(right, rightExtended, head, headForward)
            && Vector3.Distance(left.Centre, right.Centre) >= MinApart;
    }

    /// D7: the pose held still for the chord's time. A palm drifting more than ResetPose.MaxDrift from where the hold
    /// started ends the hold (it starts again from the new place). Pure.
    public sealed class HandsResetTracker
    {
        public readonly HoldChord Chord = new HoldChord();
        Vector3 m_Left0, m_Right0;

        public HoldChord.Change Update(bool pose, Vector3 leftCentre, Vector3 rightCentre, float now)
        {
            if (pose && Chord.Holding &&
                (Vector3.Distance(leftCentre, m_Left0) > ResetPose.MaxDrift || Vector3.Distance(rightCentre, m_Right0) > ResetPose.MaxDrift))
            {
                Chord.Reset();   // moved: start over from here
            }
            var change = Chord.Update(pose, now);
            if (change == HoldChord.Change.Started) { m_Left0 = leftCentre; m_Right0 = rightCentre; }
            return change;
        }

        public void Reset() => Chord.Reset();
    }
}
