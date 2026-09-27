using System.Collections.Generic;
using UnityEngine;

namespace AirTools.Tools
{
    /// One label as seen from the camera: centre (U, V) and half extents in view-angle units (x/z, y/z).
    public struct LabelBox
    {
        public float U, V, HalfW, HalfH;
        public int Priority;   // higher keeps its spot
        public int Order;      // older first among equals
    }

    /// Keeps billboard labels from overlapping on screen (pure logic, EditMode-tested). Labels are placed greedily
    /// by priority; each one that would overlap an already placed label slides up or down (whichever is shorter,
    /// up on ties) until it is clear. A label that can't find room within maxShiftHeights of its own height is
    /// decluttered (shift = NaN: hide it) unless its priority is at least keepPriority (those stay put).
    /// Returns each label's vertical shift in the same view-angle units.
    public static class LabelLayout
    {
        static readonly List<int> s_Order = new List<int>(64);
        static readonly List<LabelBox> s_Placed = new List<LabelBox>(64);

        public static void Solve(IReadOnlyList<LabelBox> boxes, float[] shifts, float pad = 0.004f, float maxShiftHeights = 3f, int keepPriority = 3)
        {
            s_Order.Clear();
            for (int i = 0; i < boxes.Count; i++) s_Order.Add(i);
            s_Order.Sort((a, b) =>
            {
                int c = boxes[b].Priority.CompareTo(boxes[a].Priority);
                return c != 0 ? c : boxes[a].Order.CompareTo(boxes[b].Order);
            });
            s_Placed.Clear();
            foreach (int i in s_Order)
            {
                var box = boxes[i];
                float up = Free(box, +1, pad), down = Free(box, -1, pad);
                float v = up - box.V <= box.V - down ? up : down;
                if (Mathf.Abs(v - box.V) > maxShiftHeights * 2f * box.HalfH)
                {
                    if (box.Priority < keepPriority) { shifts[i] = float.NaN; continue; }   // declutter
                    v = box.V;
                }
                shifts[i] = v - box.V;
                box.V = v;
                s_Placed.Add(box);
            }
        }

        /// Nearest clear V for the box moving only in direction dir.
        static float Free(LabelBox box, int dir, float pad)
        {
            float v = box.V;
            for (int iter = 0; iter < 32; iter++)
            {
                bool moved = false;
                foreach (var p in s_Placed)
                {
                    if (Mathf.Abs(box.U - p.U) >= box.HalfW + p.HalfW + pad) continue;
                    if (Mathf.Abs(v - p.V) >= box.HalfH + p.HalfH + pad) continue;
                    v = dir > 0 ? p.V + p.HalfH + box.HalfH + pad : p.V - p.HalfH - box.HalfH - pad;
                    moved = true;
                }
                if (!moved) return v;
            }
            return v;
        }

        public static bool Overlaps(LabelBox a, LabelBox b, float pad = 0f) =>
            Mathf.Abs(a.U - b.U) < a.HalfW + b.HalfW + pad && Mathf.Abs(a.V - b.V) < a.HalfH + b.HalfH + pad;
    }
}
