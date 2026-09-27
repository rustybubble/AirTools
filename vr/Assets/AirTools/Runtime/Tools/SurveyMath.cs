using System.Collections.Generic;
using UnityEngine;

namespace AirTools.Tools
{
    /// Rectangle sizes for the agent's survey (B1): the structure layer lists an object's 4 corners in order
    /// (0→1 = width, 1→2 = height), and the survey clicks them in that order.
    public static class SurveyMath
    {
        /// Width = mean of sides 0–1 and 2–3, height = mean of sides 1–2 and 3–0 (metres, the points' space). Fewer than
        /// 4 points: the first side and the second.
        public static void Size(IReadOnlyList<Vector3> p, out double w, out double h)
        {
            w = h = 0;
            if (p == null || p.Count < 2) return;
            if (p.Count < 4)
            {
                w = Vector3.Distance(p[0], p[1]);
                h = p.Count > 2 ? Vector3.Distance(p[1], p[2]) : 0;
                return;
            }
            w = (Vector3.Distance(p[0], p[1]) + Vector3.Distance(p[2], p[3])) * 0.5;
            h = (Vector3.Distance(p[1], p[2]) + Vector3.Distance(p[3], p[0])) * 0.5;
        }
    }
}
