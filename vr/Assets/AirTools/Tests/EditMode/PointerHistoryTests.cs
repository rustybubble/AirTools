using System;
using AirTools.Input;
using AirTools.Scene;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// UX W1.2 "commit what you saw": the pointer history, pinch-onset detection, the rewind lookup and the last-seat
    /// rule. Pure maths (no PhysX, GameObject or Time): these also run offline.
    public class PointerHistoryTests
    {
        const float Mm = 0.001f;

        /// Rotation about +x (Unity: positive = the ray pitches down). Built by hand: Quaternion.AngleAxis is native.
        static Quaternion Pitch(float degrees)
        {
            float h = degrees * Mathf.Deg2Rad * 0.5f;
            return new Quaternion(Mathf.Sin(h), 0f, 0f, Mathf.Cos(h));
        }

        static readonly Vector3 Eye = new Vector3(0f, 1.5f, 0f);

        /// Where a pointer lands on a wall facing the eye at `distance` (z = distance), and how far that is from where
        /// the undisturbed ray (straight ahead) lands.
        public static float WallError(Pose pointer, float distance)
        {
            var dir = pointer.forward;
            var hit = pointer.position + dir * ((distance - pointer.position.z) / dir.z);
            return Vector3.Distance(hit, new Vector3(Eye.x, Eye.y, distance));
        }

        /// A pinch simulated frame by frame up to its press (the newest frame, at `pressAt`): the analog value rises
        /// linearly from 0 (`riseSeconds` before the press) to 0.55, the press threshold; the ray pitches down linearly
        /// from 0 (`dipSeconds` before the press) to `dipDegrees` at the press. `lagless`: the value jumps to 1 on the
        /// press frame instead (a trigger pulled in one frame).
        public static PointerHistory Pinch(float hz, float dipDegrees, float dipSeconds, float riseSeconds, bool lagless = false,
            int frames = 40, float pressAt = 100f)
        {
            var h = new PointerHistory();
            for (int k = frames - 1; k >= 0; k--)
            {
                float t = pressAt - k / hz;
                float before = pressAt - t;
                float strength = lagless ? (k == 0 ? 1f : 0f) : 0.55f * Mathf.Clamp01((riseSeconds - before) / riseSeconds);
                float dip = dipDegrees * Mathf.Clamp01((dipSeconds - before) / dipSeconds);
                h.Push(t, new Pose(Eye, Pitch(dip)), true, strength);
            }
            return h;
        }

        /// Committed-point error (m) on a wall at `distance`: with the rewind (the pose it commits with) or without it
        /// (the press pose, as before W1.2).
        public static float CommitError(PointerHistory h, float distance, bool rewind)
        {
            bool was = PointerHistory.Rewind;
            try
            {
                PointerHistory.Rewind = rewind;
                var pose = PointerHistory.TryRewind(h, false, out var p) ? p : h[0].pose;
                return WallError(pose, distance);
            }
            finally { PointerHistory.Rewind = was; }
        }

        // ---------------- ring buffer ----------------

        [Test]
        public void RingKeepsTheNewestSixteenFrames()
        {
            var h = new PointerHistory();
            Assert.AreEqual(0, h.Count);
            for (int i = 0; i < 20; i++) h.Push(i, new Pose(new Vector3(i, 0, 0), Quaternion.identity), true, 0f);
            Assert.AreEqual(PointerHistory.Capacity, h.Count);
            Assert.AreEqual(16, PointerHistory.Capacity, "the spec's 16-entry history");
            Assert.AreEqual(19f, h[0].time);
            Assert.AreEqual(4f, h[15].time, "the oldest kept frame");
            Assert.AreEqual(19f, h[0].pose.position.x);
            h.Clear();
            Assert.AreEqual(0, h.Count);
            Assert.IsFalse(h.TryCommit(out _));
            h.Push(1f, default, true, 0f);
            Assert.AreEqual(1, h.Count);
            Assert.AreEqual(1f, h[0].time);
        }

        // ---------------- pinch onset ----------------

        static PointerHistory Strengths(params float[] values)
        {
            var h = new PointerHistory();
            for (int i = 0; i < values.Length; i++) h.Push(i, default, true, values[i]);
            return h;
        }

        [Test]
        public void OnsetIsTheLastFrameAtRest()
        {
            Assert.AreEqual(3f, Strengths(0f, 0f, 0f, 0.05f, 0.2f, 0.4f, 0.6f).OnsetTime());
        }

        [Test]
        public void OnsetOfAHoveringFingerIsTheEndOfTheHover()
        {
            // A finger resting on the trigger / a half-curled hand at ~0.3, then the press.
            Assert.AreEqual(4f, Strengths(0.3f, 0.31f, 0.3f, 0.29f, 0.3f, 0.45f, 0.6f).OnsetTime());
        }

        [Test]
        public void NoAnalogRiseMeansTheOnsetIsThePress()
        {
            var h = Strengths(0f, 0f, 0f, 0f);
            Assert.AreEqual(3f, h.OnsetTime());
            Assert.AreEqual(3f - PointerHistory.RewindSeconds, h.CommitTime(), 1e-5f, "at least ~90 ms before the press");
        }

        [Test]
        public void ARiseLongerThanTheWindowStartsAtItsOldestFrame()
        {
            var v = new float[20];
            for (int i = 0; i < v.Length; i++) v[i] = 0.05f * i;   // 0 … 0.95, the window keeps 0.2 … 0.95
            var h = Strengths(v);
            Assert.AreEqual(h[h.Count - 1].time, h.OnsetTime());
        }

        [Test]
        public void CommitIsNinetyMillisecondsBeforeTheOnset()
        {
            var h = Pinch(90f, 0f, 0.08f, 0.08f);
            float onset = h.OnsetTime();
            Assert.That(100f - onset, Is.InRange(0.06f, 0.08f), "the value left rest ~70 ms before the press");
            Assert.AreEqual(onset - 0.09f, h.CommitTime(), 1e-4f);
        }

        // ---------------- rewind lookup ----------------

        [Test]
        public void LookupTakesTheNewestValidFrameAtOrBeforeTheTime()
        {
            var h = new PointerHistory();
            for (int i = 0; i < 10; i++) h.Push(i, new Pose(new Vector3(i, 0, 0), Quaternion.identity), i != 4, 0f);
            Assert.IsTrue(h.TryAt(5.5f, out var s)); Assert.AreEqual(5f, s.time);
            Assert.IsTrue(h.TryAt(4.5f, out s)); Assert.AreEqual(3f, s.time, "an invalid frame (ray off) is skipped");
            Assert.IsTrue(h.TryAt(-5f, out s)); Assert.AreEqual(0f, s.time, "older than the window: the oldest valid frame");
            var off = new PointerHistory();
            for (int i = 0; i < 5; i++) off.Push(i, default, false, 0f);
            Assert.IsFalse(off.TryAt(3f, out _), "no valid frame, no rewind");
        }

        [Test]
        public void RayThatJustCameBackCommitsItsFirstFrame()
        {
            var h = new PointerHistory();
            for (int i = 0; i < 10; i++) h.Push(i * 0.011f, new Pose(new Vector3(i, 0, 0), Quaternion.identity), i >= 8, 0f);
            Assert.IsTrue(h.TryCommit(out var s));
            Assert.AreEqual(8f, s.pose.position.x, "nothing was shown before the ray came back on");
        }

        [Test]
        public void RewindNeedsTheFlagLiveHistoryAndNoOverride()
        {
            var h = Pinch(90f, 2f, 0.08f, 0.08f);
            bool was = PointerHistory.Rewind;
            try
            {
                PointerHistory.Rewind = true;
                Assert.IsTrue(PointerHistory.TryRewind(h, false, out _));
                Assert.IsFalse(PointerHistory.TryRewind(h, true, out _), "an override (harness Aim) is aiming");
                Assert.IsFalse(PointerHistory.TryRewind(new PointerHistory(), false, out _), "no live history (tests, agent)");
                Assert.IsFalse(PointerHistory.TryRewind(null, false, out _));
                PointerHistory.Rewind = false;
                Assert.IsFalse(PointerHistory.TryRewind(h, false, out _), "flag off: the press pose, as before");
            }
            finally { PointerHistory.Rewind = was; }
        }

        // ---------------- the injected 2° pinch dip ----------------

        /// The spec's test: a 2° dip over the 80 ms before the press. With the rewind the committed point is where the
        /// pointer showed before the pinch; without it, d × tan 2° off (34.9 mm at 1 m, 69.8 mm at 2 m).
        [TestCase(90f, 1f)]
        [TestCase(90f, 2f)]
        [TestCase(72f, 1f)]
        [TestCase(72f, 2f)]
        public void InjectedTwoDegreePinchDipIsUndone(float hz, float distance)
        {
            var h = Pinch(hz, 2f, 0.08f, 0.08f);
            float off = CommitError(h, distance, rewind: false);
            float on = CommitError(h, distance, rewind: true);
            Assert.AreEqual(distance * Mathf.Tan(2f * Mathf.Deg2Rad), off, 0.5f * Mm, "flag off: the dipped press pose");
            Assert.Less(on, 0.5f * Mm, $"flag on: {on * 1000f:0.0} mm (off: {off * 1000f:0.0} mm)");
        }

        [TestCase(90f)]
        [TestCase(72f)]
        public void TriggerPulledInOneFrame(float hz)
        {
            // A trigger slammed in one frame, the controller dipping 1° over 30 ms: the commit is 90 ms before the last
            // frame at rest, well before the dip.
            var h = Pinch(hz, 1f, 0.03f, 0.03f, lagless: true);
            Assert.Less(CommitError(h, 2f, rewind: true), 0.5f * Mm);
            Assert.Greater(CommitError(h, 2f, rewind: false), 30f * Mm);
        }

        [TestCase(90f)]
        [TestCase(72f)]
        public void PinchStrengthLaggingTheFingersIsCovered(float hz)
        {
            // Hand tracking: the fingers (and the ray) start moving 60 ms before the pinch strength leaves rest.
            var h = Pinch(hz, 2f, 0.12f, 0.06f);
            Assert.Less(CommitError(h, 2f, rewind: true), 0.5f * Mm);
        }

        [Test]
        public void SlowPinchLongerThanTheWindowStillHelps()
        {
            // A 300 ms pinch at 90 Hz: the window (167 ms) doesn't reach its onset; the oldest frame is part-way down.
            var h = Pinch(90f, 2f, 0.3f, 0.3f);
            float on = CommitError(h, 2f, rewind: true), off = CommitError(h, 2f, rewind: false);
            Assert.Less(on, off * 0.5f, $"on {on * 1000f:0.0} mm vs off {off * 1000f:0.0} mm");
        }

        [Test]
        public void SteadyPointerCommitsWhereItIs()
        {
            var h = Pinch(90f, 0f, 0.08f, 0.08f);
            Assert.Less(CommitError(h, 2f, rewind: true), 0.01f * Mm);
        }

        /// Prints the before / after table (the W1.2 report numbers).
        [Test]
        public void DipErrorTable()
        {
            foreach (float hz in new[] { 90f, 72f })
            foreach (float d in new[] { 1f, 2f })
            {
                var h = Pinch(hz, 2f, 0.08f, 0.08f);
                Console.WriteLine($"W1.2 dip 2° / 80 ms, {hz:0} Hz, {d:0} m: off {CommitError(h, d, false) * 1000f:0.0} mm, on {CommitError(h, d, true) * 1000f:0.0} mm");
            }
            Assert.Pass();
        }

        // ---------------- last seat ----------------

        [Test]
        public void SameAimWithinThreeDegrees()
        {
            var a = new Pose(Eye, Quaternion.identity);
            Assert.AreEqual(3f, PointerHistory.SeatDegrees);
            Assert.IsTrue(PointerHistory.SameAim(a, new Pose(Eye + Vector3.right, Pitch(2.9f)), PointerHistory.SeatDegrees), "the dip: keep the seat");
            Assert.IsFalse(PointerHistory.SameAim(a, new Pose(Eye, Pitch(3.1f)), PointerHistory.SeatDegrees), "a drag: place at the release");
            Assert.IsFalse(PointerHistory.SameAim(a, new Pose(Eye, Pitch(-20f)), PointerHistory.SeatDegrees));
        }
    }

    public class AngularRadiiTests
    {
        [Test]
        public void RadiiGrowWithDistanceBeyondTheirMetricSize()
        {
            bool was = AngularRadii.Enabled;
            try
            {
                AngularRadii.Enabled = true;
                // Facade corner 6 cm / edge 4 cm: the fascia at 6 m gets 10.5 / 6.3 cm.
                Assert.AreEqual(0.1047f, AngularRadii.Corner(0.06f, 6f), 0.0005f);
                Assert.AreEqual(0.0629f, AngularRadii.Edge(0.04f, 6f), 0.0005f);
                // Arm's length: the metric radius still rules.
                Assert.AreEqual(0.06f, AngularRadii.Corner(0.06f, 1f), 1e-6f);
                Assert.AreEqual(0.04f, AngularRadii.Edge(0.04f, 2f), 1e-6f);
                // 1:50 tabletop at 0.6 m: 10.5 mm instead of 1.2 mm.
                Assert.AreEqual(0.0105f, AngularRadii.Corner(0.06f * 0.02f, 0.6f), 0.0002f);
                // Measure grab 4 cm at 1.2°, finish 3 cm at 1.0°.
                Assert.AreEqual(0.0838f, AngularRadii.Grab(0.04f, 4f), 0.0005f);
                Assert.AreEqual(0.03f, AngularRadii.Finish(0.03f, 1f), 1e-6f);
                Assert.AreEqual(0.0698f, AngularRadii.Finish(0.03f, 4f), 0.0005f);
                // Unknown distance (a point query, the agent's clicks): metric.
                Assert.AreEqual(0.06f, AngularRadii.Corner(0.06f, 0f), 1e-6f);
            }
            finally { AngularRadii.Enabled = was; }
        }

        [Test]
        public void OffMeansMetricOnly()
        {
            bool was = AngularRadii.Enabled;
            try
            {
                AngularRadii.Enabled = false;
                Assert.AreEqual(0.06f, AngularRadii.Corner(0.06f, 6f), 1e-6f);
                Assert.AreEqual(0.04f, AngularRadii.Grab(0.04f, 40f), 1e-6f);
            }
            finally { AngularRadii.Enabled = was; }
        }

        [Test]
        public void CrossoverDistances()
        {
            // Where the angular radius takes over from the metric one (facade corner 3.4 m, edge 3.8 m).
            Assert.AreEqual(3.44f, 0.06f / Mathf.Tan(AngularRadii.CornerDegrees * Mathf.Deg2Rad), 0.01f);
            Assert.AreEqual(3.82f, 0.04f / Mathf.Tan(AngularRadii.EdgeDegrees * Mathf.Deg2Rad), 0.01f);
        }
    }
}
