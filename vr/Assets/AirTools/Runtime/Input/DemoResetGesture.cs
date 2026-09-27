using AirTools.Core;
using AirTools.Parts;
using AirTools.UI;
using Oculus.Interaction.Input;
using UnityEngine;

namespace AirTools.Input
{
    /// D7 (UX W1.8): reset the demo from the headset when the laptop can't (AppCommands.ResetDemo), in DemoMode only.
    /// - Controllers: both thumbsticks clicked in and held 2 s.
    /// - Hands: "stop" with both hands, held still 2 s: both hands up at the eyes, flat, palms facing away, fingers up,
    ///   apart (ResetPose). Chosen over "both palms up + pinch" (a palm facing you plus a held pinch is Meta's system
    ///   gesture: the right hand would open the universal menu) and over a long press on a ring item (one more item on
    ///   an 11-item ring, and hands can't be exercised in the Simulator). Hard to hit by accident: no AirTools gesture
    ///   uses two flat hands, the palms must face away and the fingers point up, both hands must stay within 15 cm of
    ///   where they started, a UI button press during the hold cancels it, it's off while the Pay button is live, and a
    ///   "keep holding · lower your hands to cancel" toast shows after 0.5 s.
    /// Hands and head come from the palm menu (its left hand, other hand and head) unless set here.
    public class DemoResetGesture : MonoBehaviour
    {
        [Tooltip("ISDK hands and the head. Empty: taken from the PalmMenu (hand = left, otherHand = right, head).")]
        public Hand leftHand;
        public Hand rightHand;
        public Transform head;
        [Tooltip("Seconds to hold (both gestures).")]
        public float holdSeconds = 2f;
        [Tooltip("Listen outside DemoMode too.")]
        public bool alwaysOn;

        public readonly HoldChord Sticks = new HoldChord();
        public readonly HandsResetTracker Hands = new HandsResetTracker();

        public int Resets { get; private set; }
        /// "controllers" | "hands" — what fired the last reset.
        public string LastSource { get; private set; } = "";
        /// Harness / Operator-free checks: pretend both sticks are clicked (true) or not (false); null = the controllers.
        public bool? SticksOverride { get; set; }
        /// The same for the hands' pose; null = the tracked hands.
        public bool? PoseOverride { get; set; }
        /// The hands are in the reset pose this frame (before the payment gate).
        public bool PoseNow { get; private set; }

        static readonly HandJointId[] s_Knuckles = { HandJointId.HandIndex1, HandJointId.HandMiddle1, HandJointId.HandRing1, HandJointId.HandPinky1 };
        static readonly HandJointId[] s_Tips = { HandJointId.HandIndexTip, HandJointId.HandMiddleTip, HandJointId.HandRingTip, HandJointId.HandPinkyTip };
        static readonly Vector3[] s_KnucklePos = new Vector3[4];

        bool m_Bound;

        void OnEnable()
        {
            Services.Register(this);
            FeedbackEvents.Fired += OnFeedback;
        }

        void OnDisable()
        {
            Services.Unregister(this);
            FeedbackEvents.Fired -= OnFeedback;
        }

        /// A UI button press while the hands hold the pose: they're on a button, not asking for a reset.
        void OnFeedback(Feedback f)
        {
            if (f == Feedback.Press && Hands.Chord.Holding) { Hands.Reset(); Log.Info("Demo reset (hands): cancelled by a button press"); }
        }

        void Bind()
        {
            if (m_Bound && leftHand != null && rightHand != null && head != null) return;
            m_Bound = true;
            if (!Services.TryGet<PalmMenu>(out var palm)) return;
            if (leftHand == null) leftHand = palm.hand;
            if (rightHand == null) rightHand = palm.otherHand;
            if (head == null) head = palm.head;
        }

        void Update()
        {
            if (!DemoMode.On && !alwaysOn)
            {
                if (Sticks.Holding || Hands.Chord.Holding) { Sticks.Reset(); Hands.Reset(); }
                return;
            }
            Bind();
            Sticks.Seconds = Hands.Chord.Seconds = holdSeconds;
            float now = Time.unscaledTime;

            bool sticks = SticksOverride ?? BothSticksClicked();
            Handle(Sticks, Sticks.Update(sticks, now), "controllers");

            Vector3 l = default, r = default;
            PoseNow = PoseOverride ?? TryPose(out l, out r);
            // Off while the Pay button is live: flat hands must never lean on it.
            bool payLive = Services.TryGet<CheckoutPanel>(out var checkout) && (checkout.State == CheckoutState.Ready || checkout.State == CheckoutState.Paying);
            Handle(Hands.Chord, Hands.Update(PoseNow && !payLive, l, r, now), "hands");
        }

        void Handle(HoldChord chord, HoldChord.Change change, string source)
        {
            switch (change)
            {
                case HoldChord.Change.Started:
                    Log.Info($"Demo reset ({source}): holding");
                    break;
                case HoldChord.Change.Warned:
                    UiToast.Show(source == "hands" ? "Reset for the next person? Keep holding · lower your hands to cancel"
                                                   : "Reset for the next person? Keep holding both sticks · let go to cancel", ColorRole.Warning);
                    break;
                case HoldChord.Change.Cancelled:
                    if (chord.CancelledAfterWarning) UiToast.Show("Reset cancelled", ColorRole.Info);
                    Log.Info($"Demo reset ({source}): let go");
                    break;
                case HoldChord.Change.Fired:
                    LastSource = source;
                    bool ok = AppCommands.ResetDemo();
                    if (ok) Resets++;
                    else UiToast.Show("Can't reset while a payment is going through · try again in a moment", ColorRole.Warning);
                    Log.Info($"Demo reset ({source}, {holdSeconds:0.#} s hold): {(ok ? "done" : "refused")}");
                    break;
            }
        }

        static bool BothSticksClicked()
        {
            var connected = OVRInput.GetConnectedControllers();
            if ((connected & OVRInput.Controller.LTouch) == 0 || (connected & OVRInput.Controller.RTouch) == 0) return false;
            return OVRInput.Get(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch)
                && OVRInput.Get(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch);
        }

        bool TryPose(out Vector3 leftCentre, out Vector3 rightCentre)
        {
            leftCentre = rightCentre = default;
            if (leftHand == null || rightHand == null || head == null) return false;
            if (!leftHand.IsConnected || !rightHand.IsConnected || !leftHand.IsHighConfidence || !rightHand.IsHighConfidence) return false;
            if (!Palm(leftHand, out var lp, out int le) || !Palm(rightHand, out var rp, out int re)) return false;
            leftCentre = lp.Centre;
            rightCentre = rp.Centre;
            return ResetPose.Both(lp, le, rp, re, head.position, head.forward);
        }

        static bool Palm(Hand hand, out PalmFrame palm, out int extended)
        {
            palm = default;
            extended = 0;
            if (!hand.GetJointPose(HandJointId.HandWristRoot, out var wrist)) return false;
            var k = s_KnucklePos;
            for (int i = 0; i < 4; i++)
            {
                if (!hand.GetJointPose(s_Knuckles[i], out var kp) || !hand.GetJointPose(s_Tips[i], out var tp)) return false;
                k[i] = kp.position;
                if (PalmMath.IsExtended(wrist.position, kp.position, tp.position)) extended++;
            }
            palm = PalmMath.Palm(wrist.position, k[0], k[1], k[3], leftHand: hand.Handedness == Handedness.Left);
            return true;
        }
    }
}
