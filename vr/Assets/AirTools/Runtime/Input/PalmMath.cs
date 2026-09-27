using UnityEngine;

namespace AirTools.Input
{
    /// Palm centre and the direction the palm faces, from hand joints (pure math, EditMode-tested).
    public struct PalmFrame
    {
        public Vector3 Centre;
        /// Out of the palm (the side you'd hold something on).
        public Vector3 Normal;
        /// Wrist → middle knuckle.
        public Vector3 Up;
    }

    public static class PalmMath
    {
        /// knuckles/tips: index, middle, ring, pinky.
        public static PalmFrame Palm(Vector3 wrist, Vector3 indexKnuckle, Vector3 middleKnuckle, Vector3 pinkyKnuckle, bool leftHand)
        {
            var up = middleKnuckle - wrist;
            var across = pinkyKnuckle - indexKnuckle;
            // Left hand, palm toward you, fingers up: thumb/index on your left, pinky on your right. (A first
            // version had this mirrored, so the menu opened with the back of the hand toward the face.)
            var n = leftHand ? Vector3.Cross(up, across) : Vector3.Cross(across, up);
            return new PalmFrame
            {
                Centre = Vector3.Lerp(wrist, middleKnuckle, 0.55f),
                Normal = n.sqrMagnitude > 1e-10f ? n.normalized : Vector3.forward,
                Up = up.sqrMagnitude > 1e-10f ? up.normalized : Vector3.up,
            };
        }

        /// A finger is extended when its knuckle→tip direction stays within maxBendDeg of the wrist→knuckle direction.
        public static bool IsExtended(Vector3 wrist, Vector3 knuckle, Vector3 tip, float maxBendDeg = 50f) =>
            Vector3.Angle(knuckle - wrist, tip - knuckle) <= maxBendDeg;

        /// The other hand's fingertip is on the tool ring (tipLocal in the ring's frame, metres): within `depth` of its
        /// plane and over its glass band (|r − radius| ≤ halfWidth + margin: the items, the lens, Undo / Redo). A hand
        /// merely near the menu hand — resting, or aiming its ray — is not on it.
        public static bool OnRing(Vector3 tipLocal, float radius, float halfWidth, float depth = 0.035f, float margin = 0.025f) =>
            Mathf.Abs(tipLocal.z) <= depth && Mathf.Abs(new Vector2(tipLocal.x, tipLocal.y).magnitude - radius) <= halfWidth + margin;

        /// The fingertip is on a flat panel (local in the panel's frame, `size` its local size, `scale` its lossy scale):
        /// within `depth` of its surface and at most `margin` outside its rectangle.
        public static bool OnPanel(Vector3 local, Vector2 size, Vector3 scale, float depth = 0.035f, float margin = 0.02f)
        {
            float dx = Mathf.Max(0f, Mathf.Abs(local.x) - size.x * 0.5f) * Mathf.Abs(scale.x);
            float dy = Mathf.Max(0f, Mathf.Abs(local.y) - size.y * 0.5f) * Mathf.Abs(scale.y);
            return dx <= margin && dy <= margin && Mathf.Abs(local.z * scale.z) <= depth;
        }

        /// How squarely the palm faces the viewer (1 = straight at the eyes).
        public static float FacingDot(PalmFrame palm, Vector3 head)
        {
            var toHead = head - palm.Centre;
            return toHead.sqrMagnitude > 1e-10f ? Vector3.Dot(palm.Normal, toHead.normalized) : 0f;
        }
    }

    /// Open-palm detector with hysteresis and debounce (pure logic, EditMode-tested).
    public class PalmGesture
    {
        public float OpenDot = 0.6f, StayDot = 0.4f;
        public int OpenFingers = 4, StayFingers = 3;
        public float ShowDelay = 0.12f, HideDelay = 0.25f;

        public bool IsOpen { get; private set; }
        float m_Since = -1f;

        /// Feed one frame: extended finger count, palm facing dot, whether the hand is tracked. Returns IsOpen.
        public bool Update(int extended, float facingDot, bool tracked, float now)
        {
            bool want = tracked && (IsOpen ? extended >= StayFingers && facingDot >= StayDot
                                           : extended >= OpenFingers && facingDot >= OpenDot);
            if (want == IsOpen) { m_Since = -1f; return IsOpen; }
            if (m_Since < 0f) m_Since = now;
            if (now - m_Since >= (want ? ShowDelay : HideDelay)) { IsOpen = want; m_Since = -1f; }
            return IsOpen;
        }

        /// Held open from outside (PalmHold): stays open and forgets a half-counted close, so it resumes from open.
        public void KeepOpen() { IsOpen = true; m_Since = -1f; }

        public void Reset() { IsOpen = false; m_Since = -1f; }
    }

    /// Why the palm menu closed (logged, so a headset log says which).
    public enum PalmCloseReason : byte
    {
        None,
        /// The menu hand's palm turned face-down (palm normal toward the floor).
        PalmDown,
        /// The palm turned away from the eyes (or, palm-up layout, no longer up).
        PalmAway,
        /// The menu hand was untracked / low-confidence for longer than PalmHold.LostGrace.
        HandLost,
        /// The fingers curled (fewer than PalmGesture.StayFingers).
        HandClosed,
        /// The hand dropped out of view (below the chest or off to the side).
        OutOfView,
    }

    /// Why the palm menu is held open (logged on the "held" line).
    public enum PalmHoldWhy : byte { None, Pressing, OnRing, HandLost }

    /// One frame of PalmHold's verdict.
    public struct PalmHoldState
    {
        /// Stay open regardless of the gesture.
        public bool Hold;
        /// Stop following the menu hand (the menu stays where it is).
        public bool Freeze;
        /// Not None: close the menu now, for this reason.
        public PalmCloseReason Close;
        public PalmHoldWhy Why;
    }

    /// Keeps an open palm menu steady while the other hand really works on it — that hand covers the palm, so the
    /// headset loses the menu hand's fingers and the plain gesture would flicker the menu shut — and through brief
    /// tracking drop-outs of the menu hand. Hold = stay open regardless of the gesture; freeze = stop following the menu
    /// hand. It never outlasts the user: a menu hand that's tracked and turned palm-down or away closes it within
    /// AwaySeconds even while held (the finger count isn't trusted while held: covered fingers read as curled, the
    /// palm's facing is still good), a hand lost for LostGrace closes it, and a hand merely near the ring without a
    /// press or hover holds it at most MaxIdleHold (headset log 2026-09-26: a right hand resting near the left one held
    /// the menu 3–23 s after the palm went down).
    public class PalmHold
    {
        /// Keep holding this long after the other hand leaves the ring / stops pressing.
        public float PokeGrace = 0.3f;
        /// Keep holding this long while the menu hand is untracked / low-confidence (then close).
        public float LostGrace = 0.8f;
        /// A tracked palm whose facing dot is below this (or whose normal points down past DownDot) is turned away…
        public float AwayDot = 0.1f, DownDot = 0.7f;
        /// …and closes the menu once it has been turned away this long, held or not.
        public float AwaySeconds = 0.2f;
        /// The other hand on the ring without a press / hover / pinch holds the menu at most this long.
        public float MaxIdleHold = 2.5f;

        float m_LastNear = -999f, m_LastPress = -999f, m_IdleSince = -1f, m_LostSince = -1f, m_AwaySince = -1f;

        /// Feed one frame. `isOpen`: the menu is open by hand. `onRing`: the other hand's fingertip is on the ring's band
        /// (or the panel), within a few cm of its surface. `pressing`: a real interaction — the ring pinched or still
        /// spinning, a menu button hovered or pressed. `tracked`: the menu hand is tracked with high confidence and its
        /// palm frame is valid. `facingDot` / `downDot`: that palm's facing (PalmMenu.FacingDot) and its normal · down
        /// (read only when tracked).
        public PalmHoldState Update(bool isOpen, bool onRing, bool pressing, bool tracked, float facingDot, float downDot, float now)
        {
            var st = new PalmHoldState();
            if (!isOpen) { Reset(); return st; }

            // The menu hand: lost, or turned away / down.
            if (tracked) m_LostSince = -1f;
            else if (m_LostSince < 0f) m_LostSince = now;
            float lostFor = m_LostSince < 0f ? 0f : now - m_LostSince;
            bool down = tracked && downDot > DownDot;
            bool away = tracked && (down || facingDot < AwayDot);
            // An untracked frame neither starts nor resets the count: a palm-down hand that flickers still closes it.
            if (tracked && !away) m_AwaySince = -1f;
            else if (away && m_AwaySince < 0f) m_AwaySince = now;
            if (away && now - m_AwaySince >= AwaySeconds)
            {
                st.Close = down ? PalmCloseReason.PalmDown : PalmCloseReason.PalmAway;
                Reset();
                return st;
            }

            // The other hand: pressing, or at least on the ring (for a while).
            if (pressing) m_LastPress = now;
            if (onRing) m_LastNear = now;
            bool pressed = now - m_LastPress < PokeGrace;
            bool near = now - m_LastNear < PokeGrace;
            if (near && !pressed) { if (m_IdleSince < 0f) m_IdleSince = now; }
            else m_IdleSince = -1f;
            bool working = pressed || (near && now - m_IdleSince < MaxIdleHold);

            if (!tracked && lostFor >= LostGrace && !working)
            {
                st.Close = PalmCloseReason.HandLost;
                Reset();
                return st;
            }
            bool lostHold = !tracked && lostFor < LostGrace;
            st.Hold = working || lostHold;
            st.Freeze = working || !tracked;
            st.Why = pressed ? PalmHoldWhy.Pressing : working ? PalmHoldWhy.OnRing : lostHold ? PalmHoldWhy.HandLost : PalmHoldWhy.None;
            return st;
        }

        public void Reset() { m_LastNear = -999f; m_LastPress = -999f; m_IdleSince = -1f; m_LostSince = -1f; m_AwaySince = -1f; }
    }

    /// One frame of the menu hand and the other hand, as PalmMenu sees them (hands mode).
    public struct PalmInput
    {
        /// The menu hand is connected, high-confidence and its palm frame is valid (never in controller mode).
        public bool Tracked;
        public int Fingers;
        /// PalmMenu.FacingDot: palm toward the eyes (or, palm-up layout, toward the sky).
        public float FacingDot;
        /// Palm normal · world down.
        public float DownDot;
        /// Raised in front of the eyes (PalmMenu's view-angle and chest-height test).
        public bool InView;
        /// The other hand's fingertip on the ring's band / the panel (tight: a few cm).
        public bool OnRing;
        /// The ring pinched or spinning, a menu button hovered or pressed.
        public bool Pressing;
    }

    /// The palm menu's hand decision (pure, EditMode-tested): PalmGesture opens and closes it, PalmHold keeps it open
    /// while the other hand works on it and closes it when the palm turns away or the hand is lost. PalmMenu feeds it
    /// one PalmInput per frame; tests replay headset logs through the same code.
    public class PalmMenuLogic
    {
        public readonly PalmGesture Gesture = new PalmGesture();
        public readonly PalmHold Hold = new PalmHold();
        /// A close right after a hold ended is logged as "released · …" within this many seconds.
        public float ReleasedWindow = 1f;

        public bool IsOpen => Gesture.IsOpen;
        public bool Holding { get; private set; }
        public bool Frozen { get; private set; }
        public PalmHoldWhy Why { get; private set; }
        /// Why it last closed, and whether that was within ReleasedWindow of a hold ending.
        public PalmCloseReason LastClose { get; private set; }
        public bool LastCloseAfterHold { get; private set; }
        /// The last close came while the menu was held (the palm turned down under the other hand, say).
        public bool LastCloseWhileHeld { get; private set; }

        float m_HoldEndedAt = -999f;

        /// Feed one frame; returns whether the menu is open by hand.
        public bool Update(in PalmInput f, float now)
        {
            bool wasOpen = Gesture.IsOpen;
            var st = Hold.Update(wasOpen, f.OnRing, f.Pressing, f.Tracked, f.FacingDot, f.DownDot, now);
            bool wasHolding = Holding;
            Holding = st.Hold; Frozen = st.Freeze; Why = st.Why;
            if (wasHolding && !Holding) m_HoldEndedAt = now;
            if (st.Close != PalmCloseReason.None)
            {
                Gesture.Reset();
                Close(st.Close, afterHold: !wasHolding && now - m_HoldEndedAt <= ReleasedWindow, whileHeld: wasHolding);
                return false;
            }
            if (st.Hold)
            {
                // The gesture isn't fed while held (the covered hand's fingers read as curled); it resumes from open.
                Gesture.KeepOpen();
                return true;
            }
            bool open = Gesture.Update(f.Tracked ? f.Fingers : 0, f.Tracked && f.InView ? f.FacingDot : 0f, f.Tracked, now);
            if (wasOpen && !open)
                Close(!f.Tracked ? PalmCloseReason.HandLost
                      : !f.InView ? PalmCloseReason.OutOfView
                      : f.DownDot > Hold.DownDot ? PalmCloseReason.PalmDown
                      : f.FacingDot < Gesture.StayDot ? PalmCloseReason.PalmAway
                      : PalmCloseReason.HandClosed,
                      afterHold: now - m_HoldEndedAt <= ReleasedWindow, whileHeld: false);
            return open;
        }

        void Close(PalmCloseReason why, bool afterHold, bool whileHeld)
        {
            LastClose = why;
            LastCloseAfterHold = afterHold;
            LastCloseWhileHeld = whileHeld;
            Holding = false; Frozen = false; Why = PalmHoldWhy.None;
            m_HoldEndedAt = -999f;
        }

        /// "palm down, while held", "released · hand closed"… for the "Palm menu closed (…)" log line.
        public static string Describe(PalmCloseReason why, bool afterHold, bool whileHeld = false)
        {
            string s = why switch
            {
                PalmCloseReason.PalmDown => "palm down",
                PalmCloseReason.PalmAway => "palm turned away",
                PalmCloseReason.HandLost => "hand lost",
                PalmCloseReason.HandClosed => "hand closed",
                PalmCloseReason.OutOfView => "hand out of view",
                _ => "closed",
            };
            return afterHold ? "released · " + s : whileHeld ? s + ", while held" : s;
        }

        public static string Describe(PalmHoldWhy why) => why switch
        {
            PalmHoldWhy.Pressing => "the ring or a button in use",
            PalmHoldWhy.OnRing => "other hand on the ring",
            PalmHoldWhy.HandLost => "menu hand lost for a moment",
            _ => "",
        };
    }
}
