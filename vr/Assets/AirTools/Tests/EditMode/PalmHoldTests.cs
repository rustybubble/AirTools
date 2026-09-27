using AirTools.Input;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// The palm menu's hand decision (PalmMenuLogic = PalmGesture + PalmHold), replayed at 72 Hz. The headset log of
    /// 2026-09-26 (quest-20260926-120346.log) had "Palm menu held (other hand on it)" 0.1–0.7 s after almost every
    /// open and holds of 3–23.5 s after the palm had gone down (open 16:47:18.685 → held 18.838 → released 42.422 →
    /// closed 42.673): a right hand merely near the left one held the menu and a held menu ignored the palm.
    public class PalmHoldTests
    {
        const float Dt = 1f / 72f;

        /// Palm-up layout: the palm faces the sky (FacingDot = normal · up), fingers out, raised in view.
        static PalmInput Up(bool onRing = false, bool pressing = false, int fingers = 4) => new PalmInput
        {
            Tracked = true, Fingers = fingers, FacingDot = 0.95f, DownDot = -0.95f, InView = true, OnRing = onRing, Pressing = pressing,
        };

        /// The same hand turned face-down.
        static PalmInput Down(bool onRing = false, bool pressing = false) => new PalmInput
        {
            Tracked = true, Fingers = 4, FacingDot = -0.95f, DownDot = 0.95f, InView = true, OnRing = onRing, Pressing = pressing,
        };

        /// Turned on its side, away (facing ≈ 0, not down).
        static PalmInput Sideways() => new PalmInput { Tracked = true, Fingers = 4, FacingDot = 0.02f, DownDot = 0f, InView = true };

        static PalmInput Lost(bool onRing = false, bool pressing = false) => new PalmInput { Tracked = false, OnRing = onRing, Pressing = pressing };

        /// Feed `f` every frame over [from, to); returns the time it first reads closed (or -1).
        static float Run(PalmMenuLogic l, float from, float to, PalmInput f)
        {
            for (float t = from; t < to; t += Dt)
                if (!l.Update(f, t)) return t;
            return -1f;
        }

        /// Opened by the gesture at t = 0 (ShowDelay 0.12 s); returns the logic and the time it opened.
        static PalmMenuLogic Opened(out float t)
        {
            var l = new PalmMenuLogic();
            t = 0f;
            while (!l.Update(Up(), t)) { t += Dt; Assert.Less(t, 1f, "opens"); }
            return l;
        }

        [Test]
        public void LoggedPattern_OtherHandPassesNear_ThenPalmDown_ClosesWithinHalfASecond()
        {
            var l = Opened(out float t0);
            // 0.15 s after opening the right hand passes over the ring (the log's "held" 0.15 s after "open")…
            Assert.AreEqual(-1f, Run(l, t0, t0 + 0.15f, Up()));
            Assert.AreEqual(-1f, Run(l, t0 + 0.15f, t0 + 0.6f, Up(onRing: true)));
            Assert.IsTrue(l.Holding, "held while it's on the ring");
            // …and is still there, resting on the band without pressing anything, when the palm turns face-down.
            float closed = Run(l, t0 + 0.6f, t0 + 3f, Down(onRing: true));
            Assert.Greater(closed, 0f, "closes");
            Assert.LessOrEqual(closed - (t0 + 0.6f), 0.5f, "within 0.5 s of the palm turning down");
            Assert.AreEqual(PalmCloseReason.PalmDown, l.LastClose);
            Assert.IsTrue(l.LastCloseWhileHeld);
            Assert.AreEqual("palm down, while held", PalmMenuLogic.Describe(l.LastClose, l.LastCloseAfterHold, l.LastCloseWhileHeld));
            Assert.IsFalse(l.Holding);
        }

        [Test]
        public void PalmDown_ClosesEvenWhileTheRingIsBeingSpun()
        {
            var l = Opened(out float t0);
            Assert.AreEqual(-1f, Run(l, t0, t0 + 2f, Up(onRing: true, pressing: true, fingers: 1)));
            float closed = Run(l, t0 + 2f, t0 + 4f, Down(onRing: true, pressing: true));
            Assert.Greater(closed, 0f);
            Assert.LessOrEqual(closed - (t0 + 2f), 0.5f, "the palm decides, not the other hand");
            Assert.AreEqual(PalmCloseReason.PalmDown, l.LastClose);
        }

        [Test]
        public void PalmTurnedAway_ClosesAHeldMenu()
        {
            var l = Opened(out float t0);
            Assert.AreEqual(-1f, Run(l, t0, t0 + 1f, Up(onRing: true, pressing: true)));
            float closed = Run(l, t0 + 1f, t0 + 3f, Sideways());
            Assert.Greater(closed, 0f);
            Assert.LessOrEqual(closed - (t0 + 1f), 0.5f);
            Assert.AreEqual(PalmCloseReason.PalmAway, l.LastClose);
        }

        [Test]
        public void OtherHandReallyPoking_StaysOpen_EvenWithTheFingersCovered()
        {
            var l = Opened(out float t0);
            // Working the ring for 10 s: the covered hand reads 1 finger (it would close by the gesture), palm still up.
            Assert.AreEqual(-1f, Run(l, t0, t0 + 10f, Up(onRing: true, pressing: true, fingers: 1)));
            Assert.IsTrue(l.Holding);
            Assert.IsTrue(l.Frozen, "held still, not following the hand");
            Assert.AreEqual(PalmHoldWhy.Pressing, l.Why);
            // Pressing a button on the inspector card (not on the ring's band) holds it too.
            Assert.AreEqual(-1f, Run(l, t0 + 10f, t0 + 12f, Up(pressing: true, fingers: 1)));
            // Let go with the palm still up and the fingers back: open, following the hand again.
            Assert.AreEqual(-1f, Run(l, t0 + 12f, t0 + 14f, Up()));
            Assert.IsFalse(l.Holding);
            Assert.IsFalse(l.Frozen);
        }

        [Test]
        public void OnTheRingWithoutAPress_HoldsAtMostMaxIdleHold()
        {
            var l = Opened(out float t0);
            // The right hand rests on the band, pressing nothing, and the covered left hand reads a fist.
            float closed = Run(l, t0, t0 + 6f, Up(onRing: true, fingers: 1));
            Assert.Greater(closed, 0f, "not held forever");
            Assert.That(closed - t0, Is.InRange(2.5f, 2.5f + 0.25f + 0.1f), "MaxIdleHold, then the gesture's HideDelay");
            Assert.AreEqual(PalmCloseReason.HandClosed, l.LastClose);
            Assert.IsTrue(l.LastCloseAfterHold);
            Assert.AreEqual("released · hand closed", PalmMenuLogic.Describe(l.LastClose, l.LastCloseAfterHold, l.LastCloseWhileHeld));
        }

        [Test]
        public void TheOtherHandLeaving_HoldsOnlyPokeGrace()
        {
            var l = Opened(out float t0);
            Assert.AreEqual(-1f, Run(l, t0, t0 + 1f, Up(onRing: true, pressing: true, fingers: 1)));
            // It leaves; the menu hand still reads a fist: closed after 0.3 s grace + the gesture's 0.25 s.
            float closed = Run(l, t0 + 1f, t0 + 3f, Up(fingers: 1));
            Assert.That(closed - (t0 + 1f), Is.InRange(0.3f, 0.3f + 0.25f + 0.05f));
            Assert.IsTrue(l.LastCloseAfterHold);
        }

        [Test]
        public void PalmDown_WithTrackingFlicker_StillCloses()
        {
            // The other hand on the ring costs the palm-down hand its tracking every other frame.
            var l = Opened(out float t0);
            Assert.AreEqual(-1f, Run(l, t0, t0 + 1f, Up(onRing: true, pressing: true)));
            float closed = -1f;
            int i = 0;
            for (float t = t0 + 1f; t < t0 + 3f && closed < 0f; t += Dt, i++)
                if (!l.Update(i % 2 == 0 ? Down(onRing: true, pressing: true) : Lost(onRing: true, pressing: true), t)) closed = t;
            Assert.Greater(closed, 0f);
            Assert.LessOrEqual(closed - (t0 + 1f), 0.5f);
            Assert.AreEqual(PalmCloseReason.PalmDown, l.LastClose);
        }

        [Test]
        public void HandLostForAMoment_StaysOpen()
        {
            var l = Opened(out float t0);
            Assert.AreEqual(-1f, Run(l, t0, t0 + 1f, Up()));
            Assert.AreEqual(-1f, Run(l, t0 + 1f, t0 + 1.3f, Lost()), "a 0.3 s drop-out");
            Assert.IsTrue(l.Holding);
            Assert.AreEqual(PalmHoldWhy.HandLost, l.Why);
            Assert.AreEqual(-1f, Run(l, t0 + 1.3f, t0 + 3f, Up()), "back: still open");
            Assert.IsFalse(l.Holding);
        }

        [Test]
        public void HandLostForTwoSeconds_Closes()
        {
            var l = Opened(out float t0);
            float closed = Run(l, t0, t0 + 2f, Lost());
            Assert.Greater(closed, 0f);
            Assert.That(closed - t0, Is.InRange(0.8f, 0.85f), "LostGrace");
            Assert.AreEqual(PalmCloseReason.HandLost, l.LastClose);
            Assert.IsFalse(l.Update(Lost(), t0 + 2f), "stays closed");
            Assert.IsFalse(l.IsOpen);
        }

        [Test]
        public void HandLostWhileTheOtherHandWorksTheRing_StaysOpen()
        {
            // The right hand covering the left can cost the left hand its tracking; a real press keeps it open.
            var l = Opened(out float t0);
            Assert.AreEqual(-1f, Run(l, t0, t0 + 3f, Lost(onRing: true, pressing: true)));
            // It lets go: the lost hand no longer has a reason to be held.
            float closed = Run(l, t0 + 3f, t0 + 5f, Lost());
            Assert.That(closed - (t0 + 3f), Is.InRange(0.25f, 0.35f), "PokeGrace, then the (long over) LostGrace closes it");
            Assert.AreEqual(PalmCloseReason.HandLost, l.LastClose);
        }

        [Test]
        public void GestureCloses_SayWhy()
        {
            var l = Opened(out float t0);
            Assert.Greater(Run(l, t0, t0 + 1f, new PalmInput { Tracked = true, Fingers = 4, FacingDot = 0.95f, DownDot = -0.95f, InView = false }), 0f);
            Assert.AreEqual(PalmCloseReason.OutOfView, l.LastClose);
            Assert.IsFalse(l.LastCloseAfterHold);
            Assert.AreEqual("hand out of view", PalmMenuLogic.Describe(l.LastClose, l.LastCloseAfterHold, l.LastCloseWhileHeld));
        }

        [Test]
        public void HeldGesture_ResumesFromOpen()
        {
            // A fist that started counting just before the hold must not close the menu the frame the hold ends.
            var l = Opened(out float t0);
            l.Update(Up(fingers: 1), t0 + 0.2f);   // HideDelay starts counting…
            Assert.AreEqual(-1f, Run(l, t0 + 0.21f, t0 + 1.5f, Up(onRing: true, pressing: true, fingers: 1)));
            Assert.IsTrue(l.Update(Up(fingers: 1), t0 + 1.9f), "…but the hold forgot it: the fist starts counting now");
        }

        [Test]
        public void OnRing_IsTight_NotMerelyNearTheHand()
        {
            const float r = 0.105f, hw = 0.017f;
            Assert.IsTrue(PalmMath.OnRing(new Vector3(0f, r, 0.03f), r, hw), "3 cm off the glass, over an item");
            Assert.IsTrue(PalmMath.OnRing(new Vector3(-r * 0.87f, -r * 0.5f, -0.01f), r, hw), "on Undo's side of the band");
            Assert.IsFalse(PalmMath.OnRing(new Vector3(0f, r, 0.06f), r, hw), "6 cm off the plane");
            Assert.IsFalse(PalmMath.OnRing(new Vector3(0f, 0f, 0f), r, hw), "the empty middle (the palm)");
            Assert.IsFalse(PalmMath.OnRing(new Vector3(0.2f, 0f, 0f), r, hw), "beside the ring");
            Assert.IsFalse(PalmMath.OnRing(new Vector3(0.05f, -0.2f, 0.08f), r, hw), "resting below the left hand");
            // The old check (12 cm around the ring's whole 24 cm disc) took every one of these.
            var size = new Vector2(0.25f, 0.1f);
            Assert.IsTrue(PalmMath.OnPanel(new Vector3(0.1f, 0f, -0.02f), size, Vector3.one), "over the card");
            Assert.IsTrue(PalmMath.OnPanel(new Vector3(0.14f, 0f, 0f), size, Vector3.one), "just past its edge");
            Assert.IsFalse(PalmMath.OnPanel(new Vector3(0.1f, 0f, 0.08f), size, Vector3.one), "8 cm in front");
            Assert.IsFalse(PalmMath.OnPanel(new Vector3(0.2f, 0f, 0f), size, Vector3.one), "7.5 cm past its edge");
        }
    }
}
