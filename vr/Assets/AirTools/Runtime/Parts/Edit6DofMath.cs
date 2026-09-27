using System.Text;
using AirTools.Core;
using UnityEngine;

namespace AirTools.Parts
{
    /// edit6dof: one of the six things the Edit view's arrows change — a move along the part's frame (Right, Up, Out) or
    /// a turn about it (Turn about Up, Tilt about the turned Right, Roll about the part's facing). The same components
    /// as the placement readout ("Right 1⅛″ · Up 0″ · Out ¼″ / Turn 5° · Tilt 0° · Roll 0°").
    public enum EditAxis { None = -1, Right = 0, Up = 1, Out = 2, Turn = 3, Tilt = 4, Roll = 5 }

    /// edit6dof: the 6-DoF editing maths (docs/edit-view.md). Pure: no engine calls beyond the managed vector and
    /// quaternion maths (PlacementMath's quaternion helpers), so it runs in the offline test runner.
    public static class Edit6DofMath
    {
        public static bool IsTurn(EditAxis a) => a >= EditAxis.Turn;

        // ---------------- the hand's rotation ----------------

        /// The grabbed point now: it rides the pointer ray, keeping its distance along it (the hand and its ray, rigidly).
        public static Vector3 Lever(Pose pointer0, Pose pointer, Vector3 grab0)
        {
            var d = PlacementMath.Normalize(pointer.rotation * PlacementMath.Conj(pointer0.rotation));
            return pointer.position + d * (grab0 - pointer0.position);
        }

        /// A free grab: the grabbed point rides the ray (Lever) and the part turns about it by the grip's rotation since
        /// the press (the controller's grip or the hand's wrist: the ray itself has no roll). Returns the anchor; the
        /// part's rotation comes out.
        public static Vector3 FreeGrab(Pose pointer0, Pose pointer, Quaternion grip0, Quaternion grip, Vector3 grab0, Vector3 anchor0,
            Quaternion rotation0, out Quaternion rotation)
        {
            var grab = Lever(pointer0, pointer, grab0);
            var g = PlacementMath.Normalize(grip * PlacementMath.Conj(grip0));
            rotation = PlacementMath.Normalize(g * rotation0);
            return grab + g * (anchor0 - grab0);
        }

        /// The hand's turn since the press, carried into another frame: `view` maps that frame into the one the hand
        /// turned in (the Edit view shows the part turned by its display yaw), so the part turns the way the hand does as
        /// seen. rotation = view⁻¹ · (grip · grip0⁻¹) · view · rotation0.
        public static Quaternion TurnedBy(Quaternion grip0, Quaternion grip, Quaternion view, Quaternion rotation0)
        {
            var g = PlacementMath.Normalize(grip * PlacementMath.Conj(grip0));
            return PlacementMath.Normalize(PlacementMath.Conj(view) * g * view * rotation0);
        }

        // ---------------- turn axes ----------------

        /// The axes turn / tilt / roll act about, so that turning the part by δ about axis k adds exactly δ to component k
        /// of (turn, tilt, roll) (PlacementMath.Rotation: R = R(Up, turn) · R(Right, tilt) · R(Out, roll) · fit): turn about
        /// Up; tilt about Right turned by the turn; roll about Out turned by the turn and the tilt (the part's own facing).
        public static void TurnAxes(PlacementFrame f, Vector3 turnTiltRoll, out Vector3 turn, out Vector3 tilt, out Vector3 roll)
        {
            var up = f.Up.normalized;
            var qt = PlacementMath.AxisAngle(up, turnTiltRoll.x);
            var qa = PlacementMath.AxisAngle(f.Right, turnTiltRoll.y);
            turn = up;
            tilt = (qt * f.Right).normalized;
            roll = (qt * (qa * f.Out)).normalized;
        }

        /// The axis an arrow works along (a move) or about (a turn), in the frame's space.
        public static Vector3 AxisOf(EditAxis a, PlacementFrame f, Vector3 turnTiltRoll)
        {
            switch (a)
            {
                case EditAxis.Right: return f.Right.normalized;
                case EditAxis.Up: return f.Up.normalized;
                case EditAxis.Out: return f.Out.normalized;
                case EditAxis.None: return Vector3.zero;
            }
            TurnAxes(f, turnTiltRoll, out var turn, out var tilt, out var roll);
            return a == EditAxis.Turn ? turn : a == EditAxis.Tilt ? tilt : roll;
        }

        // ---------------- drags ----------------

        /// A move drag: how far the grabbed point has moved along the axis.
        public static float ArrowDrag(Vector3 axis, Vector3 grab0, Vector3 grabNow) =>
            Vector3.Dot(grabNow - grab0, axis.sqrMagnitude > 1e-12f ? axis.normalized : Vector3.zero);

        /// A turn drag: the angle (degrees) the grabbed point has swept about the axis through `centre`, seen across it
        /// (positive: like AxisAngle(axis, +deg)).
        public static float RingDrag(Vector3 centre, Vector3 axis, Vector3 grab0, Vector3 grabNow) =>
            PlacementMath.SignedYaw(grab0 - centre, grabNow - centre, axis);

        /// An amount in whole steps.
        public static float Quantize(float value, float step) => step > 0f ? Mathf.Round(value / step) * step : value;

        /// One step on an axis: the panel's Step (⅜″ / 1 cm and 5°; Fine ⅛″ / 2 mm and 1°).
        public static float Step(EditAxis a, UnitSystem u, bool fine) =>
            IsTurn(a) ? PlacementMath.StepDegrees(fine) : PlacementMath.StepMetres(u, fine);

        /// The move (right, up, out; metres) and turn (turn, tilt, roll; degrees) an amount on one axis means; the other
        /// five stay zero.
        public static void Delta(EditAxis a, float amount, out Vector3 rud, out Vector3 turnTiltRoll)
        {
            rud = turnTiltRoll = Vector3.zero;
            if (IsTurn(a)) turnTiltRoll[(int)a - 3] = amount;
            else if (a >= EditAxis.Right) rud[(int)a] = amount;
        }

        /// One axis's value in a readout (offset metres, or turn / tilt / roll degrees).
        public static float Component(EditAxis a, Vector3 offset, Vector3 turnTiltRoll) =>
            a == EditAxis.None ? 0f : IsTurn(a) ? turnTiltRoll[(int)a - 3] : offset[(int)a];

        /// The live readout by an arrow: "Right 1⅛″" / "Left ⅜″" / "Up …" / "In …", "Tilt 5°" / "Roll −1°" (tabular markup:
        /// PlacementMath.Plain strips it).
        public static void AppendAxis(StringBuilder sb, EditAxis a, float amount, UnitSystem u)
        {
            switch (a)
            {
                case EditAxis.Right: sb.Append(PlacementMath.Quantum(amount, u) < 0 ? "Left " : "Right "); PlacementMath.AppendLength(sb, amount, u); break;
                case EditAxis.Up: sb.Append(PlacementMath.Quantum(amount, u) < 0 ? "Down " : "Up "); PlacementMath.AppendLength(sb, amount, u); break;
                case EditAxis.Out: sb.Append(PlacementMath.Quantum(amount, u) < 0 ? "In " : "Out "); PlacementMath.AppendLength(sb, amount, u); break;
                case EditAxis.Turn: sb.Append("Turn "); PlacementMath.AppendDegrees(sb, amount); break;
                case EditAxis.Tilt: sb.Append("Tilt "); PlacementMath.AppendDegrees(sb, amount); break;
                case EditAxis.Roll: sb.Append("Roll "); PlacementMath.AppendDegrees(sb, amount); break;
            }
        }

        /// The readout's quantum (a new text only when it changes): eighths / millimetres, or whole degrees.
        public static int ReadoutQuantum(EditAxis a, float amount, UnitSystem u) =>
            IsTurn(a) ? Mathf.RoundToInt(amount) : PlacementMath.Quantum(amount, u);

        // ---------------- a box along an axis ----------------

        /// The half extent of a box (half sizes `half`, turned by `rotation`) along `axis` (edit-touch: PartPlacer.SeatOrigin).
        public static float ExtentAlong(Vector3 axis, Quaternion rotation, Vector3 half) =>
            Mathf.Abs(Vector3.Dot(axis, rotation * Vector3.right)) * half.x
            + Mathf.Abs(Vector3.Dot(axis, rotation * Vector3.up)) * half.y
            + Mathf.Abs(Vector3.Dot(axis, rotation * Vector3.forward)) * half.z;
    }

    /// edit6dof: press-and-hold on a placed part — a press held still for Seconds fires once. Travel is the pointer's
    /// origin relative to the head (a Move-tool "grab the air" drag moves the rig with the hand, and still counts) and
    /// the ray's aim. Pure: the clock and the pointer come in.
    public sealed class EditHold
    {
        public float Seconds = 0.6f;
        public float MaxMove = 0.04f;
        public float MaxDegrees = 6f;

        public bool Holding { get; private set; }
        public bool Fired { get; private set; }
        /// The press travelled past the limits (a drag, not a hold).
        public bool Moved { get; private set; }
        public float Progress { get; private set; }
        float m_Start;
        Vector3 m_Pos0, m_Dir0;

        public void Begin(float now, Vector3 handFromHead, Vector3 aim)
        {
            Holding = true;
            Fired = Moved = false;
            Progress = 0f;
            m_Start = now;
            m_Pos0 = handFromHead;
            m_Dir0 = aim;
        }

        /// Feed while pressed; true exactly once, when the hold completes. Travel past the limits cancels it.
        public bool Update(float now, Vector3 handFromHead, Vector3 aim)
        {
            if (!Holding) return false;
            if ((handFromHead - m_Pos0).magnitude > MaxMove || Vector3.Angle(aim, m_Dir0) > MaxDegrees)
            {
                Moved = true;
                Holding = false;
                Progress = 0f;
                return false;
            }
            Progress = Seconds <= 0f ? 1f : Mathf.Clamp01((now - m_Start) / Seconds);
            if (Progress < 1f) return false;
            Holding = false;
            Fired = true;
            return true;
        }

        public void Cancel() { Holding = false; Progress = 0f; }
    }

    /// edit6dof: a one-euro filter (Casiez, Roussel & Vogel 2012) for positions: heavy smoothing while the hand is still
    /// (ray jitter doesn't shake the part), little lag while it moves fast. Pure.
    public sealed class OneEuro
    {
        public float MinCutoff = 1.5f, Beta = 4f, DCutoff = 1f;
        bool m_Has;
        Vector3 m_X, m_Dx;

        public OneEuro(float minCutoff = 1.5f, float beta = 4f) { MinCutoff = minCutoff; Beta = beta; }

        public static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(cutoff, 1e-4f));
            return 1f / (1f + tau / Mathf.Max(dt, 1e-5f));
        }

        public void Reset() => m_Has = false;
        public void Reset(Vector3 x) { m_Has = true; m_X = x; m_Dx = Vector3.zero; }

        public Vector3 Filter(Vector3 x, float dt)
        {
            if (!m_Has || dt <= 0f) { Reset(x); return x; }
            var dx = (x - m_X) / dt;
            m_Dx = Vector3.Lerp(m_Dx, dx, Alpha(DCutoff, dt));
            m_X = Vector3.Lerp(m_X, x, Alpha(MinCutoff + Beta * m_Dx.magnitude, dt));
            return m_X;
        }
    }

    /// edit6dof: the one-euro filter for a rotation (its speed in rad/s drives the cutoff; normalised lerp). Pure.
    public sealed class OneEuroRotation
    {
        public float MinCutoff = 1.5f, Beta = 0.8f, DCutoff = 1f;
        bool m_Has;
        Quaternion m_Q;
        float m_Speed;

        public OneEuroRotation(float minCutoff = 1.5f, float beta = 0.8f) { MinCutoff = minCutoff; Beta = beta; }

        public void Reset() => m_Has = false;
        public void Reset(Quaternion q) { m_Has = true; m_Q = PlacementMath.Normalize(q); m_Speed = 0f; }

        /// The rotation between two unit quaternions (radians).
        public static float Angle(Quaternion a, Quaternion b) =>
            2f * Mathf.Acos(Mathf.Min(1f, Mathf.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w)));

        /// Normalised lerp along the short way.
        public static Quaternion Nlerp(Quaternion a, Quaternion b, float t)
        {
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            if (dot < 0f) b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
            t = Mathf.Clamp01(t);
            return PlacementMath.Normalize(new Quaternion(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t));
        }

        public Quaternion Filter(Quaternion q, float dt)
        {
            q = PlacementMath.Normalize(q);
            if (!m_Has || dt <= 0f) { Reset(q); return q; }
            float speed = Angle(m_Q, q) / dt;
            m_Speed += (speed - m_Speed) * OneEuro.Alpha(DCutoff, dt);
            m_Q = Nlerp(m_Q, q, OneEuro.Alpha(MinCutoff + Beta * m_Speed, dt));
            return m_Q;
        }
    }

    /// edit6dof: the one-euro filter for a single value (a drag's amount: metres or degrees). Pure.
    public sealed class OneEuroValue
    {
        public float MinCutoff = 1.5f, Beta = 0.05f, DCutoff = 1f;
        bool m_Has;
        float m_X, m_Dx;

        public OneEuroValue(float minCutoff = 1.5f, float beta = 0.05f) { MinCutoff = minCutoff; Beta = beta; }

        public void Reset() => m_Has = false;
        public void Reset(float x) { m_Has = true; m_X = x; m_Dx = 0f; }

        public float Filter(float x, float dt)
        {
            if (!m_Has || dt <= 0f) { Reset(x); return x; }
            float dx = (x - m_X) / dt;
            m_Dx += (dx - m_Dx) * OneEuro.Alpha(DCutoff, dt);
            m_X += (x - m_X) * OneEuro.Alpha(MinCutoff + Beta * Mathf.Abs(m_Dx), dt);
            return m_X;
        }
    }
}
