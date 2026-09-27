using UnityEngine;

namespace AirTools.Input
{
    /// UX W1.2 "commit what you saw". Closing a pinch (or pulling a trigger) turns the hand, so the ray has already moved
    /// a degree or two by the time the press registers: the point, level or part used to land where the pointer went
    /// during the pinch, not where it was showing. This keeps the last 16 frames of one hand's live pointer with the
    /// analog press value (trigger / index-pinch strength) and picks the pointer from before the pinch.
    ///
    /// The commit time is onset − RewindSeconds: ~90 ms before the pinch onset, the last frame the analog value sat at
    /// rest (or at its floor in the window) before rising into the press. Pinch strength is computed from the tracked
    /// fingers, so it leaves rest after they start closing; the 90 ms lead covers that. Without an analog rise (a
    /// constant value) the onset is the press itself, so a commit is always at least 90 ms old. A window of 16 frames
    /// reaches 167 ms back at 90 Hz, 208 ms at 72 Hz; an older commit time takes the oldest frame.
    /// Pure (EditMode-tested offline); fixed array, no allocations.
    public sealed class PointerHistory
    {
        public const int Capacity = 16;

        /// Master switch for the rewind (and the part / level "last seat" rule). Off = every press commits the pose it
        /// registered with, exactly as before W1.2 (A/B on the headset). Angular radii have their own switch,
        /// <see cref="AirTools.Scene.AngularRadii.Enabled"/>.
        public static bool Rewind = true;

        /// How far before the pinch onset the commit goes (the spec's "~90 ms").
        public static float RewindSeconds = 0.09f;

        /// Analog value at or below which the trigger / pinch counts as at rest.
        public static float RestLevel = 0.1f;

        /// Hand-tracking pinch strength wobbles by about this much: a value within it of the window's floor counts as
        /// resting there (a finger hovering on the trigger, a half-curled hand).
        public static float OnsetJitter = 0.03f;

        /// A release within this many degrees of the committed pointer still takes the committed seat (Part, Level);
        /// a larger move is a deliberate drag and commits at the release, as before.
        public static float SeatDegrees = 3f;

        public struct Sample
        {
            public float time;
            public Pose pose;
            public bool valid;
            /// Trigger value or index-pinch strength, 0–1.
            public float strength;
        }

        readonly Sample[] m_Samples = new Sample[Capacity];
        int m_Next, m_Count;

        public int Count => m_Count;

        public void Clear() { m_Next = 0; m_Count = 0; }

        public void Push(float time, Pose pose, bool valid, float strength)
        {
            m_Samples[m_Next] = new Sample { time = time, pose = pose, valid = valid, strength = strength };
            m_Next = (m_Next + 1) % Capacity;
            if (m_Count < Capacity) m_Count++;
        }

        /// ago = 0: the newest sample; Count − 1: the oldest.
        public Sample this[int ago] => m_Samples[((m_Next - 1 - ago) % Capacity + Capacity) % Capacity];

        /// The last time the analog value was at rest before the press it is rising into (the newest sample): the
        /// newest sample at or below max(RestLevel, the window's floor + OnsetJitter). A constant value (no analog
        /// data) gives the newest sample's time; a rise longer than the window gives the oldest sample's time.
        public float OnsetTime()
        {
            if (m_Count == 0) return float.NegativeInfinity;
            float floor = float.MaxValue;
            for (int i = 0; i < m_Count; i++) floor = Mathf.Min(floor, this[i].strength);
            float rest = Mathf.Max(RestLevel, floor + OnsetJitter);
            for (int i = 0; i < m_Count; i++)
            {
                var s = this[i];
                if (s.strength <= rest) return s.time;
            }
            return this[m_Count - 1].time;
        }

        /// When a press on the newest sample commits: RewindSeconds before its onset.
        public float CommitTime() => OnsetTime() - RewindSeconds;

        /// The newest valid sample at or before `time`; if every valid sample is newer (the window doesn't reach back
        /// that far, or the ray only just came back on), the oldest valid one. False when no sample is valid.
        public bool TryAt(float time, out Sample sample)
        {
            sample = default;
            bool any = false;
            for (int i = 0; i < m_Count; i++)
            {
                var s = this[i];
                if (!s.valid) continue;
                sample = s;
                any = true;
                if (s.time <= time) return true;
            }
            return any;
        }

        /// The sample a press registered on the newest sample commits with. False with no valid sample.
        public bool TryCommit(out Sample sample)
        {
            sample = default;
            if (m_Count == 0) return false;
            return TryAt(CommitTime(), out sample);
        }

        /// The pose a press commits with: the rewound one when the rewind is on, the press came from the live source
        /// (no override aiming) and there is live history; else the pose the press registered with.
        public static bool TryRewind(PointerHistory history, bool overrideActive, out Pose pose)
        {
            pose = default;
            if (!Rewind || overrideActive || history == null || !history.TryCommit(out var s)) return false;
            pose = s.pose;
            return true;
        }

        /// The two pointers aim within maxDegrees of each other (the release didn't move on purpose).
        public static bool SameAim(Pose committed, Pose release, float maxDegrees) =>
            Vector3.Angle(committed.forward, release.forward) <= maxDegrees;
    }
}
