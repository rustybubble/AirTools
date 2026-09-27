using System.Collections.Generic;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Agent
{
    /// check_slope's geometry (B1 hand-off §4, brain.md S1 SlopeProbe): which structure edge to tape, where to aim from,
    /// and the numbers from the 2-point tape. Pure.
    public static class SlopeProbe
    {
        /// Edges within this many degrees of level count as runs (gutters, sills, ledges).
        public const float LevelDeg = 10f;

        /// The edge to tape: the first of `edgeIds` the layer has (the server's candidates, best first); otherwise the
        /// app's own pick — gutter: the highest long near-level edge; sill / ledge / nearest_edge: the near-level edge
        /// closest to the gaze ray. −1 = none.
        public static int PickEdge(SceneRoot root, string target, IList<string> edgeIds, Pose head)
        {
            var layer = root?.Structure;
            if (layer == null || layer.Edges.Length == 0) return -1;
            if (edgeIds != null)
                foreach (var id in edgeIds)
                    for (int i = 0; i < layer.Edges.Length; i++)
                        if (layer.Edges[i].id == id) return i;
            var space = root.StructureSpace;
            float scale = space != null ? Mathf.Max(space.lossyScale.x, 1e-6f) : 1f;
            var runs = new List<int>();
            float longest = 0f;
            for (int i = 0; i < layer.Edges.Length; i++)
            {
                var e = layer.Edges[i];
                var a = space.TransformPoint(e.a); var b = space.TransformPoint(e.b);
                var d = b - a;
                float len = d.magnitude / scale;
                if (len < 0.3f) continue;
                if (Vector3.Angle(Vector3.ProjectOnPlane(d, Vector3.up), d) > LevelDeg) continue;
                runs.Add(i);
                longest = Mathf.Max(longest, len);
            }
            if (runs.Count == 0) return -1;
            int best = -1;
            if ((target ?? "gutter") == "gutter")
            {
                float bestY = float.MinValue;
                foreach (int i in runs)
                {
                    var e = layer.Edges[i];
                    if (e.Length * 1f < longest * 0.6f) continue;
                    float y = space.TransformPoint(e.Mid).y;
                    if (y > bestY + 1e-4f) { bestY = y; best = i; }
                }
                return best;
            }
            float bestD = float.MaxValue;
            var fwd = head.rotation * Vector3.forward;
            foreach (int i in runs)
            {
                var mid = space.TransformPoint(layer.Edges[i].Mid);
                var toMid = mid - head.position;
                if (Vector3.Dot(toMid, fwd) <= 0f) continue;
                float off = Vector3.Cross(fwd, toMid).magnitude;   // distance from the gaze ray
                if (off < bestD) { bestD = off; best = i; }
            }
            return best;
        }

        /// Aim from 0.6 m out toward the viewer (scaled for the tabletop).
        public static Vector3 Approach(Vector3 point, Vector3 viewer, float worldScale)
        {
            var d = viewer - point;
            return point + (d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.up) * 0.6f * worldScale;
        }

        /// Scene-root points (+Y up) → run (horizontal, m), fall (|Δy| mm) and the low end.
        public static (double runM, double fallMm, Vector3 lowEnd) Measure(Vector3 a, Vector3 b)
        {
            var d = b - a;
            double run = new Vector2(d.x, d.z).magnitude;
            double fall = System.Math.Abs(d.y) * 1000.0;
            return (run, fall, a.y <= b.y ? a : b);
        }
    }
}
