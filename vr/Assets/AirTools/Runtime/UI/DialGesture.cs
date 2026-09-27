using UnityEngine;

namespace AirTools.UI
{
    /// modelwheel: one pinch on a wheel (the tool ring and Model view's wheel share it; pure). Points are in the wheel's
    /// plane relative to its centre. A pinch that moves less than TapMove and ends within TapSeconds is a tap where it
    /// began; once it moves further it drags the dial — the item under the fingers follows them — and on release the dial
    /// coasts with the flick (DialPhysics.EndDrag).
    public class DialGesture
    {
        public float TapMove = 0.012f, TapSeconds = 0.35f, MinRadius = 0.05f;

        public enum Result { None, Tap, Released }

        /// A pinch is on the wheel.
        public bool Active { get; private set; }
        /// It has moved far enough to turn the wheel.
        public bool Dragging { get; private set; }
        /// Where it began (a tap lands here).
        public Vector2 Start { get; private set; }
        /// The furthest it has been from Start.
        public float Moved { get; private set; }
        public float StartTime { get; private set; }

        Vector2 m_Prev;

        public void Begin(Vector2 p, float now)
        {
            Active = true;
            Dragging = false;
            Start = m_Prev = p;
            StartTime = now;
            Moved = 0f;
        }

        /// The pinch is at p now (dt since the last move): turns the dial once it has moved TapMove.
        public void Move(Vector2 p, float dt, DialPhysics dial)
        {
            if (!Active) return;
            var dp = p - m_Prev;
            Moved = Mathf.Max(Moved, (p - Start).magnitude);
            if (!Dragging && Moved > TapMove)
            {
                Dragging = true;
                dial?.BeginDrag();
            }
            if (Dragging && dial != null) dial.Drag(WheelMath.DragAngle(m_Prev, dp, MinRadius), dt);
            m_Prev = p;
        }

        /// The pinch ended (`released`: let go; false: lost, e.g. the hand left view). A drag lets the dial coast
        /// (Released); a short, still pinch that was let go is a Tap at Start.
        public Result End(bool released, float now, DialPhysics dial)
        {
            if (!Active) return Result.None;
            var r = Result.None;
            if (Dragging)
            {
                dial?.EndDrag();
                r = Result.Released;
            }
            else if (released && now - StartTime <= TapSeconds) r = Result.Tap;
            Active = false;
            Dragging = false;
            return r;
        }
    }
}
