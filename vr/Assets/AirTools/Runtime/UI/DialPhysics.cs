using System;
using UnityEngine;

namespace AirTools.UI
{
    /// A prize wheel of `Count` evenly spaced items (the tool ring; modelwheel: Model view's wheel). Angle is the wheel's
    /// rotation in radians, clockwise positive; item i sits at i·Spacing + Angle from the top, so the selected item (at
    /// the top) is the one nearest −Angle. Drag it directly; on release it coasts with the flick's speed (capped) under
    /// exponential friction, then springs onto the nearest item. Step() reports how many items passed the top (for a tick
    /// each). Pure and deterministic (EditMode-tested).
    /// modelwheel: the items may sit any angle apart (Spacing); they repeat every Period = Count · Spacing (a full turn on
    /// the ring; a shorter endless strip on the model wheel, where 7 cards 20° apart loop seamlessly). Items that aren't
    /// Allowed (offline "Not downloaded" models) are never rested on: a coasting wheel settles on the nearest allowed one.
    public class DialPhysics
    {
        public readonly int Count;
        /// The angle between neighbouring items (radians): 2π / Count on the ring.
        public readonly float Spacing;
        /// Count · Spacing: the angle after which the items repeat (2π on the ring).
        public float Period => Count * Spacing;

        /// The ring's spacing the feel below was tuned for (six items, 60°).
        public const float RingSpacing = Mathf.PI / 3f;

        /// Velocity decay (1/s): a flick at ω coasts ≈ ω / Friction radians.
        public float Friction = 3.4f;
        /// Flick speed cap (rad/s): a hard spin passes ~4–5 items, not a blur.
        public float MaxSpeed = 13f;
        /// Below this speed the wheel stops coasting and springs to an item (rad/s).
        public float SnapSpeed = 2.2f;
        /// Detent spring (1/s²) and damping ratio (just under critical: a hint of settle, no wobble).
        public float Stiffness = 170f, DampingRatio = 0.85f;
        /// Release speed = the average drag speed over the last this-many seconds (a flick is ~50–150 ms).
        public float VelocityWindow = 0.06f;
        /// modelwheel: the items a coasting wheel may come to rest on (null: every item). Gets a wrapped index. A spin to a
        /// named item (SpinTo / Jump / SpinToPosition) still goes anywhere.
        public Func<int, bool> Allowed;

        public float Angle { get; private set; }
        public float Velocity { get; private set; }
        public bool Dragging { get; private set; }
        /// Settled on an item: not dragged, not moving.
        public bool Settled => !Dragging && m_Target.HasValue && Mathf.Abs(Velocity) < 0.02f && Mathf.Abs(Angle - m_Target.Value) < 0.002f;
        public int Selected => Wrap(Mathf.RoundToInt(-Angle / Spacing));
        /// modelwheel: where the wheel is, in items (unwrapped; the item at the top when it's a whole number).
        public float Position => -Angle / Spacing;
        /// The unwrapped item the wheel is heading for (settling), or null while dragged / coasting.
        public int? TargetItem => m_Target.HasValue ? Mathf.RoundToInt(-m_Target.Value / Spacing) : (int?)null;

        float? m_Target;
        int m_Detent;
        const int Samples = 16;
        readonly float[] m_SampleAngle = new float[Samples], m_SampleDt = new float[Samples];
        int m_SampleCount, m_SampleHead;

        public DialPhysics(int count, int selected = 0) : this(count, selected, 0f) { }

        /// `spacing` (radians) ≤ 0: a full turn over the items (the ring).
        public DialPhysics(int count, int selected, float spacing)
        {
            Count = Mathf.Max(1, count);
            Spacing = spacing > 0f ? spacing : 2f * Mathf.PI / Count;
            Angle = -selected * Spacing;
            m_Target = Angle;
            m_Detent = Detent(Angle);
        }

        /// modelwheel: an endless strip of `count` items `spacing` radians apart with the ring's feel measured in items: the
        /// flick cap and the snap speed scale with the spacing (a flick passes as many cards as it passes ring items).
        public static DialPhysics Strip(int count, int selected, float spacing)
        {
            var d = new DialPhysics(count, selected, spacing);
            float k = d.Spacing / RingSpacing;
            d.MaxSpeed *= k;
            d.SnapSpeed *= k;
            return d;
        }

        public int Wrap(int i) => Wrap(i, Count);

        public static int Wrap(int i, int count) => count <= 0 ? 0 : ((i % count) + count) % count;

        /// Angle of item i from the top right now (radians, clockwise positive, wrapped to (−Period/2, Period/2]; on the
        /// ring (−π, π]).
        public float ItemAngle(int i)
        {
            float p = Period;
            float a = i * Spacing + Angle;
            a = Mathf.Repeat(a + p * 0.5f, p) - p * 0.5f;
            return a;
        }

        public void BeginDrag()
        {
            Dragging = true;
            Velocity = 0f;
            m_Target = null;
            m_SampleCount = 0;
        }

        /// Move the wheel with the hand by dAngle over dt seconds.
        public void Drag(float dAngle, float dt)
        {
            if (!Dragging) BeginDrag();
            Angle += dAngle;
            if (dt <= 1e-5f) return;
            m_SampleAngle[m_SampleHead] = dAngle;
            m_SampleDt[m_SampleHead] = dt;
            m_SampleHead = (m_SampleHead + 1) % Samples;
            m_SampleCount = Mathf.Min(m_SampleCount + 1, Samples);
            // Average over the most recent VelocityWindow seconds of samples.
            float a = 0f, t = 0f;
            for (int k = 0; k < m_SampleCount && t < VelocityWindow; k++)
            {
                int i = (m_SampleHead - 1 - k + Samples) % Samples;
                a += m_SampleAngle[i];
                t += m_SampleDt[i];
            }
            Velocity = t > 1e-5f ? a / t : 0f;
        }

        /// Let go: keep the flick's speed (capped) and coast.
        public void EndDrag()
        {
            Dragging = false;
            Velocity = Mathf.Clamp(Velocity, -MaxSpeed, MaxSpeed);
            m_Target = null;
        }

        /// Show item i at the top at once (no spin, no ticks).
        public void Jump(int i)
        {
            Dragging = false;
            Velocity = 0f;
            Angle = -Wrap(i) * Spacing;
            m_Target = Angle;
            m_Detent = Detent(Angle);
        }

        /// Spin to item i the short way round (a tap on a side item).
        public void SpinTo(int i)
        {
            Dragging = false;
            float p = Period;
            float target = -i * Spacing;
            float diff = Mathf.Repeat(target - Angle + p * 0.5f, p) - p * 0.5f;
            m_Target = Angle + diff;
        }

        /// modelwheel: spin to the unwrapped position `item` (a card k places along: Position rounded + k).
        public void SpinToPosition(int item)
        {
            Dragging = false;
            m_Target = -item * Spacing;
        }

        /// modelwheel: spin `steps` allowed items along (+ next, − previous), skipping the ones that aren't Allowed; from
        /// where it's heading if it's settling. Returns the unwrapped item it spins to.
        public int SpinBy(int steps)
        {
            int from = TargetItem ?? Mathf.RoundToInt(Position);
            int to = StepAllowed(from, steps, Count, Allowed);
            SpinToPosition(to);
            return to;
        }

        /// Stop where it is and spring onto the nearest allowed item (Reduce motion after a drag; the harness).
        public void SnapToNearest()
        {
            Dragging = false;
            Velocity = 0f;
            m_Target = -NearestAllowed(Position, Count, 0f, Allowed) * Spacing;
        }

        /// Advance dt seconds. Returns the signed number of items that crossed the top since the last Step — dragging
        /// included — one tick each.
        public int Step(float dt)
        {
            if (dt <= 0f) return 0;
            if (!Dragging)
            {
                if (!m_Target.HasValue)
                {
                    Velocity *= Mathf.Exp(-Friction * dt);
                    Angle += Velocity * dt;
                    if (Mathf.Abs(Velocity) < SnapSpeed)
                    {
                        // Settle on the nearest item (modelwheel: the nearest allowed one; a tie goes the way it's
                        // travelling — the position runs opposite to the angle).
                        int i = NearestAllowed(-Angle / Spacing, Count, -Velocity, Allowed);
                        m_Target = -i * Spacing;
                    }
                }
                if (m_Target.HasValue)
                {
                    float x = Angle - m_Target.Value;
                    float c = 2f * DampingRatio * Mathf.Sqrt(Stiffness);
                    // Semi-implicit Euler in small substeps (stable for any frame time).
                    int n = Mathf.CeilToInt(dt / 0.004f);
                    float h = dt / n;
                    for (int s = 0; s < n; s++)
                    {
                        Velocity += (-Stiffness * x - c * Velocity) * h;
                        x += Velocity * h;
                    }
                    Angle = m_Target.Value + x;
                    if (Mathf.Abs(x) < 0.001f && Mathf.Abs(Velocity) < 0.02f) { Angle = m_Target.Value; Velocity = 0f; }
                }
            }
            int now = Detent(Angle), crossed = now - m_Detent;
            m_Detent = now;
            return crossed;
        }

        /// Which item boundary the top is in: changes by one as each item passes the top.
        int Detent(float angle) => Mathf.FloorToInt(-angle / Spacing + 0.5f);

        // ---------------- modelwheel: detents over allowed items (pure) ----------------

        /// The unwrapped item nearest `position` whose wrapped index is `allowed` (all, when null); a tie goes the way
        /// `travel` points (+: increasing position). With nothing allowed, the nearest item.
        public static int NearestAllowed(float position, int count, float travel, Func<int, bool> allowed)
        {
            int r = Mathf.RoundToInt(position);
            if (allowed == null || count <= 0) return r;
            int best = r;
            float bestD = float.MaxValue;
            bool found = false;
            for (int k = -count; k <= count; k++)
            {
                int c = r + k;
                if (!allowed(Wrap(c, count))) continue;
                float d = Mathf.Abs(c - position);
                bool tie = Mathf.Abs(d - bestD) <= 1e-4f;
                if (d < bestD - 1e-4f || (tie && (c - position) * travel > 0f))
                {
                    best = c;
                    bestD = d;
                    found = true;
                }
            }
            return found ? best : r;
        }

        /// From the unwrapped item `from`, `steps` allowed items along (skipping the rest); `from` itself when nothing
        /// else is allowed.
        public static int StepAllowed(int from, int steps, int count, Func<int, bool> allowed)
        {
            if (steps == 0 || count <= 0) return from;
            int dir = steps > 0 ? 1 : -1, left = Mathf.Abs(steps), at = from;
            for (int guard = 0; guard < count * (left + 1) && left > 0; guard++)
            {
                at += dir;
                if (allowed == null || allowed(Wrap(at, count))) left--;
            }
            return left == 0 ? at : from;
        }
    }
}
