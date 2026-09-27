using System;
using System.Collections.Generic;
using AirTools.Scene;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// The UX label diet (W1.9): at most 12 world labels on screen. Every visible overlay set's mandatory captions (its
    /// honesty label) count first; the rest goes to item labels, newest set first. Pure. Since declutter S10 the
    /// overlays share WorldLabels' one pool with every other producer; this stays as its single-class form.
    public static class LabelBudget
    {
        public const int Max = WorldLabels.Max;

        /// captions[i] / items[i]: overlay set i, newest first. Returns how many of set i's items may show.
        public static int[] Split(int total, IReadOnlyList<int> captions, IReadOnlyList<int> items)
        {
            int n = items?.Count ?? 0;
            int nc = captions?.Count ?? 0;
            var claims = new List<LabelClaim>(Math.Max(n, nc));
            // Set i is newer than set i + 1: one class, newest first.
            for (int i = 0; i < Math.Max(n, nc); i++)
                claims.Add(new LabelClaim(LabelClass.Task, i < nc ? captions[i] : 0, i < n ? items[i] : 0, -i));
            var grants = WorldLabels.Allocate(claims, total);
            var allowed = new int[n];
            for (int i = 0; i < n; i++) allowed[i] = grants[i].Items;
            return allowed;
        }
    }

    /// Dashed polylines (plan segments, flight legs): cut once into dash sub-polylines in their parent's space; the
    /// renderer turns those into camera-facing ribbons each frame. Pure.
    public static class Dashes
    {
        public static float Length(IReadOnlyList<Vector3> pts, bool closed)
        {
            if (pts == null || pts.Count < 2) return 0f;
            float s = 0f;
            for (int i = 1; i < pts.Count; i++) s += Vector3.Distance(pts[i - 1], pts[i]);
            if (closed) s += Vector3.Distance(pts[pts.Count - 1], pts[0]);
            return s;
        }

        /// A dash and gap for a line of this length: about `targetDashes` dashes, never shorter than `minDash`.
        public static void Pattern(float length, int targetDashes, float minDash, out float dash, out float gap)
        {
            dash = Mathf.Max(minDash, length / (Mathf.Max(1, targetDashes) * 1.6f));
            gap = dash * 0.6f;
        }

        /// Cut `pts` (closed: back to the first point) into dashes of `dash` separated by `gap`, starting with a dash.
        /// Each dash is its own polyline (its cut ends plus any corners inside it). At most `maxDashes`.
        public static void Split(IReadOnlyList<Vector3> pts, bool closed, float dash, float gap, List<List<Vector3>> output, int maxDashes = 400)
        {
            output.Clear();
            if (pts == null || pts.Count < 2 || dash <= 1e-6f) return;
            int segs = closed ? pts.Count : pts.Count - 1;
            bool on = true;
            float left = dash;
            List<Vector3> current = new List<Vector3> { pts[0] };
            for (int i = 0; i < segs && output.Count < maxDashes; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                float len = Vector3.Distance(a, b);
                float t = 0f;
                while (len - t > 1e-6f && output.Count < maxDashes)
                {
                    float step = Mathf.Min(left, len - t);
                    t += step;
                    left -= step;
                    var p = Vector3.Lerp(a, b, len > 1e-9f ? t / len : 1f);
                    if (on) current.Add(p);
                    if (left <= 1e-6f)
                    {
                        if (on) { if (current.Count >= 2) output.Add(current); current = null; }
                        else current = new List<Vector3> { p };
                        on = !on;
                        left = on ? dash : gap;
                    }
                }
            }
            if (on && current != null && current.Count >= 2 && output.Count < maxDashes) output.Add(current);
        }
    }

    public enum SurveyPollStep { Poll, Done, Failed, GiveUp }

    /// survey_started without a show_survey in the same reply: poll GET /scene/survey/{id} every ~1.5 s (docs/api.md).
    public static class SurveyPoll
    {
        public const float IntervalSeconds = 1.5f;
        public const float TimeoutSeconds = 120f;

        /// What to do with one poll's answer. 404: the survey is unknown (the laptop restarted); 2xx done / failed end it;
        /// running, or no answer (the laptop hiccuped): poll again until the timeout.
        public static SurveyPollStep Next(long http, string status, double elapsedSeconds, double timeoutSeconds = TimeoutSeconds)
        {
            if (http == 404) return SurveyPollStep.Failed;
            if (http >= 200 && http < 300)
            {
                if (status == "done") return SurveyPollStep.Done;
                if (status == "failed") return SurveyPollStep.Failed;
            }
            return elapsedSeconds >= timeoutSeconds ? SurveyPollStep.GiveUp : SurveyPollStep.Poll;
        }
    }

    /// show_plan geometry: the plan's glTF points into the scene content's space (GltfFrame's X flip, the same one the
    /// mesh and the structure layer get) and on to the world. Pure.
    public static class PlanGeometry
    {
        public static Vector3 ToContent(double[] p) => GltfFrame.ToUnity(p[0], p[1], p[2]);

        public static List<Vector3> ContentPoints(PlanView plan)
        {
            var list = new List<Vector3>();
            if (plan != null) foreach (var p in plan.Points) list.Add(ToContent(p));
            return list;
        }

        /// Where the placed parts go: each point through the content's local-to-world matrix (calibration, the package
        /// rotation and the tabletop scale included).
        public static List<Vector3> WorldPoints(PlanView plan, Matrix4x4 contentToWorld)
        {
            var list = new List<Vector3>();
            if (plan != null) foreach (var p in plan.Points) list.Add(contentToWorld.MultiplyPoint3x4(ToContent(p)));
            return list;
        }

        public static Vector3 Midpoint(PlanSegment s) => ToContent(new[] { (s.A[0] + s.B[0]) * 0.5, (s.A[1] + s.B[1]) * 0.5, (s.A[2] + s.B[2]) * 0.5 });

        /// The middle of everything the plan draws (its caption and Place chip go above it).
        public static Vector3 Centroid(PlanView plan)
        {
            var sum = Vector3.zero;
            int n = 0;
            if (plan != null)
            {
                foreach (var p in plan.Points) { sum += ToContent(p); n++; }
                if (n == 0) foreach (var s in plan.Segments) { sum += ToContent(s.A) + ToContent(s.B); n += 2; }
            }
            return n > 0 ? sum / n : Vector3.zero;
        }

        /// Spacing between neighbouring points (m), for the array's record; 0 with fewer than two.
        public static float Spacing(PlanView plan)
        {
            if (plan == null || plan.Points.Count < 2) return 0f;
            double s = 0;
            for (int i = 1; i < plan.Points.Count; i++) s += PlanView.Distance(plan.Points[i - 1], plan.Points[i]);
            return (float)(s / (plan.Points.Count - 1));
        }
    }
}

namespace AirTools.Agent.Grok
{
    /// Destroy in Play mode, DestroyImmediate in edit mode (EditMode tests build overlays too).
    public static class GrokObjects
    {
        public static void Destroy(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        /// Deactivate first: Play-mode Destroy is deferred to the end of the frame.
        public static void DestroyGameObject(GameObject go)
        {
            if (go == null) return;
            go.SetActive(false);
            Destroy(go);
        }
    }
}
