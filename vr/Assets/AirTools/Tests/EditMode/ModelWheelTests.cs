using System.Collections.Generic;
using System.Linq;
using AirTools.Scene;
using AirTools.UI;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// modelwheel (pure; all of it runs in the offline runner): where Model view puts the model from the head pose (centre,
    /// height, distance, yaw only), the wheel under it facing the eyes, the endless strip's wrap-around slots, the
    /// detents that skip "Not downloaded" cards, the shared pinch gesture, the recentre triggers and the lens copy.
    public class ModelWheelTests
    {
        static readonly float[] EyeHeights = { 1.2f, 1.42f, 1.6f, 1.76f };
        static readonly float[] Headings = { 0f, 37f, -120f, 180f };

        /// Model sizes at their fitted scale (half extents, m): a flat gym, the kitchen, a tower, a cube at the fit's max.
        static readonly (string name, Vector3 half)[] Models =
        {
            ("gym", new Vector3(0.335f, 0.08f, 0.2f)), ("kitchen", new Vector3(0.35f, 0.215f, 0.29f)),
            ("tower", new Vector3(0.135f, 0.4f, 0.135f)), ("cube", new Vector3(0.45f, 0.45f, 0.45f)), ("tiny", new Vector3(0.05f, 0.02f, 0.05f)),
        };

        static float Deg(Vector3 a, Vector3 b) => Vector3.Angle(a, b);

        // ---------------- the model from the head pose ----------------

        [Test]
        public void TheHeadingIgnoresPitchAndRoll_AndKeepsTheLastOneLookingStraightDown()
        {
            Assert.AreEqual(0f, ModelViewLayout.HeadingDeg(Vector3.forward), 1e-4f);
            Assert.AreEqual(90f, ModelViewLayout.HeadingDeg(new Vector3(1f, -0.8f, 0f)), 1e-3f, "looking down and right: 90°");
            Assert.AreEqual(-45f, ModelViewLayout.HeadingDeg(new Vector3(-0.3f, 0.5f, 0.3f)), 1e-3f, "looking up and left");
            Assert.AreEqual(33f, ModelViewLayout.HeadingDeg(Vector3.down, 33f), 1e-4f, "straight down: the fallback");
            var d = ModelViewLayout.Dir(30f);
            Assert.AreEqual(0f, d.y, 1e-6f);
            Assert.AreEqual(30f, ModelViewLayout.HeadingDeg(d), 1e-3f);
        }

        /// The model's box centre is straight ahead of the eye (yaw only), 0.9–1.2 m away (flat), a few cm below eye
        /// height, its near edge ≥ 0.55 m ahead; the pose is level (yaw only) with the scan's front (+Z) toward you.
        [Test]
        public void TheModelIsCentredStraightAheadAtEyeHeight()
        {
            var boxLocal = new Vector3(3f, 4f, -2f);   // the scan's box centre in scene-root space
            const float scale = 1f / 75f;
            foreach (float eyeY in EyeHeights)
            foreach (float heading in Headings)
            foreach (var (name, half) in Models)
            {
                var eye = new Vector3(0.4f, eyeY, -1.3f);
                var pose = ModelViewLayout.ModelPose(eye, heading, boxLocal, scale, half, out var centre);
                string at = $"eye {eyeY} heading {heading} {name}";
                var landed = pose.position + pose.rotation * (boxLocal * scale);
                Assert.Less(Vector3.Distance(landed, centre), 1e-4f, $"{at}: the box centre lands on the centre");
                var flat = new Vector3(centre.x - eye.x, 0f, centre.z - eye.z);
                Assert.That(flat.magnitude, Is.InRange(ModelViewLayout.MinDistance - 1e-4f, ModelViewLayout.MaxDistance + 1e-4f), $"{at}: distance");
                Assert.Less(Deg(flat, ModelViewLayout.Dir(heading)), 0.01f, $"{at}: straight ahead");
                bool tall = name == "tower" || name == "cube";
                if (!tall) Assert.AreEqual(eyeY - ModelViewLayout.DropBelowEye, centre.y, 1e-5f, $"{at}: a few cm below the eye");
                else Assert.That(centre.y - eyeY, Is.InRange(-ModelViewLayout.DropBelowEye, 0.2f), $"{at}: a tall model rises a little");
                Assert.LessOrEqual(ModelViewLayout.ModelBottomDownDeg(eye, heading, centre, half), ModelViewLayout.MaxModelBottomDeg + 1e-3f,
                    $"{at}: its lowest edge leaves room for the wheel"); 
                Assert.GreaterOrEqual(flat.magnitude - half.z, ModelViewLayout.NearClearance - 1e-4f, $"{at}: its near edge stays ahead");
                Assert.Less(Deg(pose.rotation * Vector3.up, Vector3.up), 0.01f, $"{at}: level (yaw only)");
                Assert.Less(Deg(pose.rotation * Vector3.forward, -ModelViewLayout.Dir(heading)), 0.01f, $"{at}: its front toward you");
            }
            Assert.AreEqual(1.0f, ModelViewLayout.CentreDistance(0.2f), 1e-6f, "a shallow model: 1 m");
            Assert.AreEqual(1.0f, ModelViewLayout.CentreDistance(0.45f), 1e-6f, "the deepest fitted model still 1 m (near edge 0.55 m)");
            Assert.AreEqual(ModelViewLayout.MaxDistance, ModelViewLayout.CentreDistance(2f), 1e-6f, "never further than 1.2 m");
        }

        /// The fit for a floating model: a round 1:N with the longest side 0.67–0.9 m, 37–49° of view from 1 m.
        [Test]
        public void TheFloatingFitSpansAbout40To50Degrees()
        {
            for (float l = 2.4f; l < 4000f; l *= 1.07f)
            {
                int n = TabletopFit.Denominator(l, ModelViewLayout.FitMax);
                float side = l / n;
                Assert.LessOrEqual(side, ModelViewLayout.FitMax + 1e-4f, $"{l:0.0} m at 1:{n}");
                Assert.GreaterOrEqual(side, ModelViewLayout.FitMax / 1.34f - 1e-3f, $"{l:0.0} m at 1:{n} is only {side:0.00} m");
                float deg = 2f * Mathf.Atan(side * 0.5f / ModelViewLayout.Distance) * Mathf.Rad2Deg;
                Assert.That(deg, Is.InRange(37f, 50f), $"{l:0.0} m spans {deg:0}°");
            }
            Assert.AreEqual(5, TabletopFit.Denominator(4.2f, ModelViewLayout.FitMax), "the kitchen: 1:5 (0.84 m)");
            Assert.AreEqual(60, TabletopFit.Denominator(50.2f, ModelViewLayout.FitMax), "Zabel: 1:60 (0.84 m)");
        }

        // ---------------- the wheel under it ----------------

        static Vector3 World(Pose frame, Vector2 local) => frame.position + frame.rotation * new Vector3(local.x, local.y, 0f);

        static float Pitch(Vector3 eye, Vector3 p) => TabletopFit.BelowEyeDeg(eye, p);

        static float Azimuth(Vector3 eye, float heading, Vector3 p) =>
            Vector3.SignedAngle(ModelViewLayout.Dir(heading), new Vector3(p.x - eye.x, 0f, p.z - eye.z), Vector3.up);

        /// For standing and seated eyes and flat to tall models: the lens is 16–34° below the eye line, its top clear of the
        /// model's lowest edge; the wheel is straight ahead, square to the line of sight (facing the eyes) and level
        /// (no roll); every card that shows (±54°) stays within A7's 35° of azimuth, below the model and in front of it.
        [Test]
        public void TheWheelSitsUnderTheModelFacingTheEyes()
        {
            const float lensTop = 0.118f * 0.5f * 1.12f, cardHalfW = 0.075f, cardHalfH = 0.059f;
            float sp = ModelViewLayout.WheelSpacingDeg * Mathf.Deg2Rad, r = ModelViewLayout.WheelRadius;
            foreach (float eyeY in EyeHeights)
            foreach (float heading in Headings)
            foreach (var (name, half) in Models)
            {
                var eye = new Vector3(-0.2f, eyeY, 0.7f);
                ModelViewLayout.ModelPose(eye, heading, Vector3.zero, 1f, half, out var centre);
                float bottom = ModelViewLayout.ModelBottomDownDeg(eye, heading, centre, half);
                float down = ModelViewLayout.WheelDownDeg(bottom, lensTop);
                var frame = ModelViewLayout.WheelPose(eye, heading, down);
                string at = $"eye {eyeY} heading {heading} {name}";
                float lensPitch = Pitch(eye, frame.position);
                Assert.AreEqual(down, lensPitch, 1e-3f, $"{at}: the lens where it was asked");
                Assert.That(lensPitch, Is.InRange(ModelViewLayout.WheelMinDownDeg - 1e-3f, ModelViewLayout.WheelMaxDownDeg + 1e-3f), $"{at}: 16–34° down");
                Assert.AreEqual(ModelViewLayout.WheelDistance, Vector3.Distance(eye, frame.position), 1e-4f, $"{at}: distance");
                float nearFlat = Vector3.Dot(new Vector3(centre.x - eye.x, 0f, centre.z - eye.z), ModelViewLayout.Dir(heading)) - half.z;
                float top = Pitch(eye, World(frame, new Vector2(0f, lensTop)));
                Assert.GreaterOrEqual(top, bottom + ModelViewLayout.WheelGapDeg - 0.05f, $"{at}: the lens card's top ({top:0.0}°) under the model ({bottom:0.0}°)");
                // The cards as they rest (the lens and ±20°, ±40°): within A7's 35° of azimuth.
                foreach (float deg in new[] { -40f, -20f, 0f, 20f, 40f })
                {
                    float s = deg == 0f ? 1.12f : 0.92f;
                    var p = WheelMath.ArcPoint(deg * Mathf.Deg2Rad, r);
                    foreach (float sx in new[] { -1f, 1f })
                    {
                        var edge = World(frame, p + new Vector2(sx * cardHalfW * s, 0f));
                        Assert.LessOrEqual(Mathf.Abs(Azimuth(eye, heading, edge)), UiZones.ModelWheelMaxAzimuthDeg, $"{at}: the card at {deg:0}° ({Azimuth(eye, heading, edge):0.0}°)");
                    }
                }
                Assert.Less(Deg(frame.rotation * Vector3.forward, frame.position - eye), 0.05f, $"{at}: square to the line of sight");
                Assert.AreEqual(0f, (frame.rotation * Vector3.right).y, 1e-5f, $"{at}: level (no roll)");
                Assert.Less(Mathf.Abs(Azimuth(eye, heading, frame.position)), 0.01f, $"{at}: straight ahead");
                for (float deg = -ModelViewLayout.WheelVisibleHalfDeg; deg <= ModelViewLayout.WheelVisibleHalfDeg + 1e-3f; deg += 2f)
                {
                    // A card as ModelWheel draws it there: faded out toward the ends (it shrinks as it fades), the lens
                    // card magnified.
                    float fade = WheelMath.Fade(Mathf.Abs(deg), ModelViewLayout.WheelVisibleHalfDeg, ModelViewLayout.WheelFadeBandDeg);
                    if (fade <= 0.04f) continue;
                    float near = WheelMath.Near(deg * Mathf.Deg2Rad, sp);
                    float scale = Mathf.Lerp(0.92f, 1.12f, near * near) * Mathf.Lerp(0.3f, 1f, fade);
                    var p = WheelMath.ArcPoint(deg * Mathf.Deg2Rad, r);
                    foreach (float sx in new[] { -1f, 1f })
                    {
                        var edge = World(frame, p + new Vector2(sx * cardHalfW * scale, 0f));
                        Assert.LessOrEqual(Mathf.Abs(Azimuth(eye, heading, edge)), UiZones.ModelWheelMaxAzimuthDeg + 3f, $"{at}: a card passing {deg:0}° ({Azimuth(eye, heading, edge):0.0}°)");
                    }
                    var cardTop = World(frame, p + new Vector2(0f, cardHalfH * scale));
                    Assert.LessOrEqual(cardTop.y, World(frame, new Vector2(0f, lensTop)).y + 1e-4f, $"{at}: the arc droops from the lens");
                    // Clear of the model: in front of its near face, or under its bottom (so the line of sight to it passes
                    // under the model's near-bottom edge).
                    float depth = Vector3.Dot(new Vector3(cardTop.x - eye.x, 0f, cardTop.z - eye.z), ModelViewLayout.Dir(heading));
                    Assert.IsTrue(depth < nearFlat || cardTop.y < centre.y - half.y, $"{at}: the card at {deg:0}° is clear of the model (depth {depth:0.00} vs {nearFlat:0.00})");
                }
            }
        }

        [Test]
        public void TheWheelsAngleClampsBetween16And34Degrees()
        {
            Assert.AreEqual(ModelViewLayout.WheelMinDownDeg, ModelViewLayout.WheelDownDeg(-10f, 0.066f), 1e-4f, "a model above the eyes: 16°");
            Assert.AreEqual(ModelViewLayout.WheelMaxDownDeg, ModelViewLayout.WheelDownDeg(45f, 0.066f), 1e-4f, "a very tall model: 34°");
            float top = Mathf.Atan2(0.066f, ModelViewLayout.WheelDistance) * Mathf.Rad2Deg;
            Assert.AreEqual(15f + ModelViewLayout.WheelGapDeg + top, ModelViewLayout.WheelDownDeg(15f, 0.066f), 1e-3f, "just under the model");
        }

        /// With the status line up (the guide rail on: 0.9 m, 17° below the gaze), looking at the model puts the line where
        /// the lens card would be: the wheel steps down until the card's top clears the line's bottom edge (still ≤ 34°),
        /// and comes back up when the line goes.
        [Test]
        public void UnderALiveStatusLineTheWheelStepsDown()
        {
            const float lensTop = ModelViewLayout.WheelLensTop, below = 17f, half = 2.5f;
            float topDeg = Mathf.Atan2(lensTop, ModelViewLayout.WheelDistance) * Mathf.Rad2Deg;
            foreach (float eyeY in EyeHeights)
            foreach (var (name, h) in Models)
            {
                var eye = new Vector3(0f, eyeY, 0f);
                ModelViewLayout.ModelPose(eye, 0f, Vector3.zero, 1f, h, out var centre);
                float gaze = TabletopFit.BelowEyeDeg(eye, centre);
                float bottom = ModelViewLayout.ModelBottomDownDeg(eye, 0f, centre, h);
                float clear = ModelViewLayout.StatusClearDownDeg(gaze, below, half, lensTop);
                float plain = ModelViewLayout.WheelDownDeg(bottom, lensTop);
                float stepped = ModelViewLayout.WheelDownDeg(bottom, lensTop, ModelViewLayout.WheelDistance, clear);
                string at = $"eye {eyeY} {name}";
                Assert.GreaterOrEqual(stepped, plain - 1e-4f, $"{at}: never higher than without the line");
                Assert.LessOrEqual(stepped, ModelViewLayout.WheelMaxDownDeg + 1e-4f, $"{at}: still ≤ 34°");
                Assert.GreaterOrEqual(stepped - topDeg, gaze + below + half - 1e-3f, $"{at}: the lens card's top under the line's bottom edge");
                Assert.GreaterOrEqual(stepped - topDeg, bottom + ModelViewLayout.WheelGapDeg - 1e-3f, $"{at}: and still under the model");
            }
            Assert.AreEqual(ModelViewLayout.WheelDownDeg(10f, lensTop), ModelViewLayout.WheelDownDeg(10f, lensTop, ModelViewLayout.WheelDistance, ModelViewLayout.WheelMinDownDeg),
                1e-6f, "no line: the 16° floor, as before");
        }

        [Test]
        public void YawPitchIsEulerPitchThenYawWithoutRoll()
        {
            var q = ModelViewLayout.YawPitch(90f, 30f);
            var f = q * Vector3.forward;
            Assert.AreEqual(-Mathf.Sin(30f * Mathf.Deg2Rad), f.y, 1e-5f, "pitched 30° down");
            Assert.AreEqual(90f, ModelViewLayout.HeadingDeg(f), 1e-3f, "facing +X");
            Assert.AreEqual(0f, (q * Vector3.right).y, 1e-5f, "no roll");
            Assert.Greater((q * Vector3.up).y, 0.8f, "upright");
        }

        // ---------------- the endless strip ----------------

        /// Every window of slots shows each model once: seven models on seven slots repeat without a seam; a short list
        /// shows each of its models exactly once whatever the offset; the next model sits to the right.
        [Test]
        public void TheSlotsWrapAroundAndShowEachModelOnce()
        {
            const int slots = 7, half = slots / 2;
            foreach (int count in new[] { 1, 2, 3, 5, 7, 9 })
            for (float position = -9.5f; position <= 9.5f; position += 0.125f)
            {
                int b = WheelMath.SlotBase(position);
                float off = WheelMath.SlotOffset(position);
                Assert.That(off, Is.InRange(-0.5f, 0.5f));
                var items = new List<int>();
                for (int k = -half; k <= half; k++)
                {
                    if (!WheelMath.SlotShown(k, off, count)) continue;
                    int item = WheelMath.SlotItem(b, k, count);
                    Assert.That(item, Is.InRange(0, count - 1));
                    items.Add(item);
                }
                Assert.AreEqual(items.Count, items.Distinct().Count(), $"{count} models at {position}: [{string.Join(",", items)}] each once");
                Assert.AreEqual(Mathf.Min(count, slots), items.Count, 1, $"{count} models at {position}: as many as fit");
                Assert.IsTrue(WheelMath.SlotShown(0, off, count), "the lens slot always shows");
                Assert.AreEqual(DialPhysics.Wrap(b, count), WheelMath.SlotItem(b, 0, count), "the lens shows the base item");
            }
            // Seven models: slot k shows base + k, wrapping: after the facade (6) comes the kitchen (0).
            Assert.AreEqual(0, WheelMath.SlotItem(6, 1, 7));
            Assert.AreEqual(6, WheelMath.SlotItem(0, -1, 7));
            Assert.AreEqual(3, WheelMath.SlotItem(-11, 0, 7), "negative positions wrap too");
            float sp = 20f * Mathf.Deg2Rad;
            Assert.Greater(WheelMath.SlotAngle(1, 0f, sp), 0f, "the next model to the right");
            Assert.Greater(WheelMath.ArcPoint(WheelMath.SlotAngle(1, 0f, sp), 0.55f).x, 0f);
            Assert.AreEqual(Vector2.zero, WheelMath.ArcPoint(0f, 0.55f), "the lens at the origin");
            Assert.Less(WheelMath.ArcPoint(0.5f, 0.55f).y, 0f, "the arc droops away from the lens");
            Assert.AreEqual(0f, WheelMath.SlotAngle(1, 1f, sp), 1e-6f, "a full spacing along: the next model under the lens");
        }

        // ---------------- detents that skip "Not downloaded" ----------------

        [Test]
        public void TheNearestAllowedDetentSkipsNotDownloadedCards()
        {
            System.Func<int, bool> online = i => true, offline = i => i != 1 && i != 2 && i != 5;
            Assert.AreEqual(1, DialPhysics.NearestAllowed(1.3f, 7, 1f, online));
            Assert.AreEqual(0, DialPhysics.NearestAllowed(1.3f, 7, 1f, offline), "1 and 2 are skipped: 0 is nearer than 3");
            Assert.AreEqual(3, DialPhysics.NearestAllowed(1.8f, 7, -1f, offline), "3 is nearer than 0");
            Assert.AreEqual(3, DialPhysics.NearestAllowed(1.5f, 7, 1f, offline), "a tie goes the way it travels (+)");
            Assert.AreEqual(0, DialPhysics.NearestAllowed(1.5f, 7, -1f, offline), "a tie goes the way it travels (−)");
            Assert.AreEqual(6, DialPhysics.NearestAllowed(5f, 7, 1f, offline), "5 skipped: 4 and 6 tie; + travel → 6");
            Assert.AreEqual(12, DialPhysics.NearestAllowed(12.2f, 7, 0f, i => true), "unwrapped positions stay unwrapped");
            Assert.AreEqual(13, DialPhysics.NearestAllowed(12.2f, 7, 0f, offline), "12 (item 5) skipped: 13 (item 6, 0.8 away) beats 11 (1.2)");
            Assert.AreEqual(2, DialPhysics.NearestAllowed(2.2f, 7, 0f, i => false), "nothing allowed: the nearest item");
            Assert.AreEqual(2, DialPhysics.NearestAllowed(2.2f, 7, 0f, null), "no filter: the nearest item");
        }

        [Test]
        public void SpinByStepsOverSkippedCardsAndWrapsRound()
        {
            System.Func<int, bool> offline = i => i != 2 && i != 5;
            Assert.AreEqual(3, DialPhysics.StepAllowed(1, 1, 7, offline), "next from 1 skips 2");
            Assert.AreEqual(-1, DialPhysics.StepAllowed(1, -2, 7, offline), "two back from 1: 0, then −1 (item 6)");
            Assert.AreEqual(1 + 7, DialPhysics.StepAllowed(1, 5, 7, offline), "five allowed steps = one loop: the same model");
            Assert.AreEqual(4, DialPhysics.StepAllowed(4, 3, 7, i => false), "nothing allowed: stays");
            var d = DialPhysics.Strip(7, 1, 20f * Mathf.Deg2Rad);
            d.Allowed = offline;
            Assert.AreEqual(3, d.SpinBy(1));
            Settle(d);
            Assert.AreEqual(3, d.Selected);
            d.SpinBy(5);
            Settle(d);
            Assert.AreEqual(3, d.Selected, "a full loop lands on the same model");
            Assert.AreEqual(10f, d.Position, 1e-3f, "…seven items further along the endless strip");
        }

        /// Step at 72 Hz until it settles, then half a second more (it snaps exactly onto the detent).
        static int Settle(DialPhysics d, float seconds = 6f)
        {
            int ticks = 0;
            for (float t = 0f; t < seconds && !d.Settled; t += 1f / 72f) ticks += Mathf.Abs(d.Step(1f / 72f));
            Assert.IsTrue(d.Settled, $"settles (pos {d.Position:0.00})");
            for (int i = 0; i < 36; i++) ticks += Mathf.Abs(d.Step(1f / 72f));
            return ticks;
        }

        /// A flick coasts through the cards, ticking at each, and comes to rest on one that opens (the skipped ones are
        /// never a resting place), exactly on its detent; a hard flick passes about as many cards as it passes ring items.
        [Test]
        public void AFlickRestsOnlyOnModelsThatOpen()
        {
            System.Func<int, bool> offline = i => i == 0 || i == 3 || i == 6;
            float sp = 20f * Mathf.Deg2Rad;
            for (float v = -14f; v <= 14f; v += 0.37f)
            {
                var d = DialPhysics.Strip(7, 0, sp);
                d.Allowed = offline;
                d.BeginDrag();
                for (int i = 0; i < 3; i++) d.Drag(v * sp / DialPhysics.RingSpacing * 0.02f, 0.02f);
                d.EndDrag();
                int ticks = Settle(d);
                Assert.IsTrue(offline(d.Selected), $"v={v:0.00}: rests on {d.Selected}, which opens");
                Assert.AreEqual(0f, d.Position - Mathf.Round(d.Position), 1e-4f, "exactly on a card");
                if (Mathf.Abs(v) > 6f) Assert.Greater(ticks, 0, "ticks as the cards pass");
            }
            var ring = new DialPhysics(6);
            var strip = DialPhysics.Strip(7, 0, sp);
            Assert.AreEqual(ring.MaxSpeed / ring.Spacing, strip.MaxSpeed / strip.Spacing, 1e-3f, "the same flick cap in items per second");
            Assert.AreEqual(ring.SnapSpeed / ring.Spacing, strip.SnapSpeed / strip.Spacing, 1e-3f);
            Assert.AreEqual(7f * sp, strip.Period, 1e-5f, "seven cards repeat every 140°");
            Assert.AreEqual(2f * Mathf.PI, ring.Period, 1e-5f, "the ring: a full turn");
        }

        [Test]
        public void SpinToTakesTheShortWayRoundTheStrip()
        {
            var d = DialPhysics.Strip(7, 1, 20f * Mathf.Deg2Rad);
            d.SpinTo(6);
            Settle(d);
            Assert.AreEqual(6, d.Selected);
            Assert.AreEqual(-1f, d.Position, 1e-3f, "1 → 6 goes back two (through 0), not forward five");
            d.SpinToPosition(12);
            Settle(d);
            Assert.AreEqual(5, d.Selected);
            d.Jump(2);
            Assert.AreEqual(2f, d.Position, 1e-5f);
            Assert.IsTrue(d.Settled);
        }

        // ---------------- the pinch (shared with the ring) ----------------

        [Test]
        public void APinchIsATapUntilItMovesThenItTurnsTheWheel()
        {
            var g = new DialGesture { TapMove = 0.03f, TapSeconds = 0.4f };
            var d = DialPhysics.Strip(7, 0, 20f * Mathf.Deg2Rad);
            var top = new Vector2(0f, 0.55f);   // the lens, relative to the wheel's centre
            g.Begin(top, 0f);
            g.Move(top + new Vector2(0.02f, 0.005f), 0.1f, d);
            Assert.IsFalse(g.Dragging, "2 cm of ray wobble is still a tap");
            Assert.AreEqual(DialGesture.Result.Tap, g.End(true, 0.3f, d));
            Assert.AreEqual(0f, d.Position, 1e-6f, "a tap doesn't turn it");

            g.Begin(top, 1f);
            Assert.AreEqual(DialGesture.Result.None, g.End(true, 1.6f, d), "a long still pinch is nothing");
            g.Begin(top, 2f);
            Assert.AreEqual(DialGesture.Result.None, g.End(false, 2.1f, d), "a lost pinch is nothing");

            g.Begin(top, 3f);
            for (int i = 1; i <= 10; i++) g.Move(top + new Vector2(-0.02f * i, 0f), 1f / 72f, d);
            Assert.IsTrue(g.Dragging && d.Dragging);
            Assert.Greater(d.Position, 0.2f, "pulling left along the top brings the next models in from the right");
            Assert.AreEqual(DialGesture.Result.Released, g.End(true, 3.2f, d));
            Assert.IsFalse(d.Dragging, "released: it coasts");
            Assert.AreEqual(0.1f, WheelMath.DragAngle(new Vector2(0f, 0.1f), new Vector2(0.01f, 0f)), 1e-5f, "shared with the ring");
            Assert.AreEqual(ToolRingDrag(), WheelMath.DragAngle(new Vector2(0.1f, 0f), new Vector2(0f, -0.01f)), 1e-6f);
        }

        static float ToolRingDrag() => AirTools.Input.ToolRing.DragAngle(new Vector2(0.1f, 0f), new Vector2(0f, -0.01f));

        // ---------------- recentring ----------------

        [Test]
        public void RecentreWhenYouWalkOrTurnAwayButNotWhenYouLookAround()
        {
            var placed = new Vector3(0f, 1.6f, 0f);
            var model = new Vector3(0f, 1.54f, 1f);
            var none = ModelViewLayout.Recentre.None;
            Assert.AreEqual(none, ModelViewLayout.Why(placed, Vector3.forward, placed, model), "standing still, looking at it");
            Assert.AreEqual(none, ModelViewLayout.Why(placed, ModelViewLayout.Dir(55f), placed, model), "a glance 55° aside");
            Assert.AreEqual(ModelViewLayout.Recentre.Turned, ModelViewLayout.Why(placed, ModelViewLayout.Dir(65f) + Vector3.down * 0.3f, placed, model), "turned 65° away (looking a little down)");
            Assert.AreEqual(ModelViewLayout.Recentre.Turned, ModelViewLayout.Why(placed, Vector3.back, placed, model), "turned round");
            Assert.AreEqual(none, ModelViewLayout.Why(placed, new Vector3(0.05f, -1f, 0f), placed, model), "looking straight down: no heading");
            var farSide = new Vector3(0f, 1.6f, 2f);
            Assert.AreEqual(none, ModelViewLayout.Why(farSide, Vector3.back, placed, model), "walked round it to look at the back (2 m from the spot, 1 m from it)");
            var leaning = new Vector3(0.1f, 1.3f, 0.95f);
            Assert.AreEqual(none, ModelViewLayout.Why(leaning, Vector3.left, placed, model), "leaning over it");
            var away = new Vector3(1.6f, 1.6f, -0.4f);
            Assert.AreEqual(ModelViewLayout.Recentre.Walked, ModelViewLayout.Why(away, Vector3.forward, placed, model), "walked 1.6 m off");
            var back = new Vector3(0f, 1.6f, -0.6f);
            Assert.AreEqual(none, ModelViewLayout.Why(back, Vector3.forward, placed, model), "a step back to see it all");
        }

        [Test]
        public void ARecentreGlidesTheShortWayRound()
        {
            var a = new Vector3(0f, 1.6f, 0f);
            var b = new Vector3(2f, 1.5f, 1f);
            ModelViewLayout.Glide(a, 350f, b, 10f, 0f, out var e0, out float d0);
            Assert.AreEqual(a, e0);
            Assert.AreEqual(350f, d0, 1e-4f);
            ModelViewLayout.Glide(a, 350f, b, 10f, 0.5f, out var e1, out float d1);
            Assert.AreEqual(360f, d1, 1e-3f, "through north, not round the back");
            Assert.Less(Vector3.Distance(e1, Vector3.Lerp(a, b, 0.5f)), 1e-4f);
            ModelViewLayout.Glide(a, 350f, b, 10f, 1f, out var e2, out float d2);
            Assert.AreEqual(b, e2);
            Assert.AreEqual(0f, Mathf.DeltaAngle(d2, 10f), 1e-3f);
        }

        // ---------------- the lens copy ----------------

        [Test]
        public void TheLensDetailIsShortAndSaysWhatAPinchDoes()
        {
            Assert.AreEqual("1:6 · Pinch to open", ModelViewLayout.LensDetail("1:6", ModelCardState.Idle, false));
            Assert.AreEqual("Trigger to open", ModelViewLayout.LensDetail(null, ModelCardState.Idle, true));
            Assert.AreEqual("1:75 · On view", ModelViewLayout.LensDetail("1:75", ModelCardState.Current, false));
            Assert.AreEqual("Needs the laptop", ModelViewLayout.LensDetail("1:75", ModelCardState.NotDownloaded, false));
            Assert.AreEqual("Pinch to retry", ModelViewLayout.LensDetail("", ModelCardState.Failed, false));
            foreach (ModelCardState st in System.Enum.GetValues(typeof(ModelCardState)))
            foreach (bool c in new[] { false, true })
                Assert.LessOrEqual(ModelViewLayout.LensDetail("1:1000", st, c).Length, 24, $"{st}: fits the line under the lens");
        }
    }
}
