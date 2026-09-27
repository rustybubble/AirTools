using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AirTools.Core;
using AirTools.Tools;
using UnityEngine;

namespace AirTools.Scene
{
    /// One tape across a gap, in package space (the cavity box's frame): from / to on the cavity's faces, and the eye it is
    /// aimed from (the snap ray starts there, like a hand in front of the opening).
    public struct CavityTape
    {
        /// "w", "d" or "h".
        public string Axis;
        public Vector3 From, To, Eye;
    }

    /// e2e "measure the gap" (after "take out the dishwasher"): the REAL measure tool tapes the removed part's cavity —
    /// height (floor to underside, when it has a top), depth (back wall to the front lip), width (jamb to jamb, last: the
    /// latest tape is the width the parts search fits against) — each a 2-point tape snapped like a user's ray (the
    /// cavity's faces are in the structure layer while the part is out), saved to the notebook as "Dishwasher gap width"
    /// and friends. The three share one request id, so one Undo takes them all off. Same points as the Editor gate's
    /// tape scenarios (ScenePartsFixtures.Width / Depth / Height), without the hand noise.
    public static class CavityTapes
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        /// A snapped end farther than this (scene metres × the world scale) from its target is placed raw (unverified).
        public const float SnapTolerance = 0.02f;
        static int s_Runs;

        /// The tapes for a cavity box, in the order they're drawn: h (closed tops only), d, w. Pure.
        public static List<CavityTape> Plan(CavityBox box)
        {
            var list = new List<CavityTape>(3);
            float mid = (box.Min.x + box.Max.x) * 0.5f, h = box.Max.y - box.Min.y;
            float up = Mathf.Min(box.Min.y + 0.45f * h + 0.35f, box.Max.y - 0.1f);
            var front = box.Point(mid, up, box.Max.z + 0.9f);
            if (!box.OpenTop)
                list.Add(new CavityTape
                {
                    Axis = "h", Eye = box.Point(mid, box.Min.y + 0.4f * h, box.Max.z + 0.6f),
                    From = box.Point(mid, box.Min.y + 0.002f, box.Max.z - 0.006f), To = box.Point(mid, box.Max.y - 0.003f, box.Max.z - 0.006f),
                });
            list.Add(new CavityTape
            {
                Axis = "d", Eye = front,
                From = box.Point(mid, box.Min.y + 0.004f, box.Min.z + 0.004f), To = box.Point(mid, box.Min.y + 0.001f, box.Max.z - 0.004f),
            });
            float y = box.Min.y + 0.45f * h;
            list.Add(new CavityTape
            {
                Axis = "w", Eye = front,
                From = box.Point(box.Min.x, y, box.Max.z - 0.004f), To = box.Point(box.Max.x, y, box.Max.z - 0.004f),
            });
            return list;
        }

        /// "width" / "depth" / "height".
        public static string AxisWord(string axis) => axis == "w" ? "width" : axis == "h" ? "height" : "depth";

        /// The notebook title of a gap tape: "Dishwasher gap width".
        public static string Title(string noun, string axis) => $"{AirTools.UI.Copy.Cap(noun)} gap {AxisWord(axis)}";

        /// Tape the gap now (synchronous). measuredM: the real width / height / depth read (NaN where a tape didn't take).
        /// Returns how many tapes were saved; `detail` says what each one read (raw, for the log and the harness).
        public static int Run(Gap gap, out Vector3 measuredM, out string detail)
        {
            measuredM = new Vector3(float.NaN, float.NaN, float.NaN);
            var sb = new StringBuilder();
            var tool = Services.Get<MeasureTool>();
            var root = Services.Get<SceneRoot>();
            if (tool == null || root == null || gap.Component == null) { detail = "no measure tool, scene or gap"; return 0; }
            // A tape or shape of the user's in progress: finish it (2+ points) or drop a lone point, as the survey does.
            if (tool.Session.Count >= MeasureMath.MinPoints) tool.Finish();
            if (tool.Session.Count > 0) tool.CancelSession();
            if (!tool.Equipped) AppCommands.EquipTool("measure");
            bool prevArea = tool.AreaMode;
            tool.AreaMode = true;   // D4 auto-save would finish at the 2nd point without the tag
            string request = $"gap-{gap.Id}-{++s_Runs}";
            float worldScale = Mathf.Max(root.transform.lossyScale.x, 1e-4f);
            int saved = 0;
            var tapes = new AirTools.Notes.NotebookEntry[3];   // w, h, d
            try
            {
                foreach (var t in Plan(gap.Box))
                {
                    Vector3 W(Vector3 p) => root.transform.TransformPoint(root.PackageToRoot(p));
                    var eye = W(t.Eye);
                    var a = SnapAt(eye, W(t.From), gap.Box, root, worldScale, out bool sa);
                    var b = SnapAt(eye, W(t.To), gap.Box, root, worldScale, out bool sb2);
                    tool.Click(a);
                    tool.Click(b);
                    var shape = tool.Session.Count >= MeasureMath.MinPoints
                        ? tool.Finish(new SurveyTag { RequestId = request, ObjectId = gap.Id, Label = Title(gap.Noun, t.Axis), Tape = true, Unverified = !(sa && sb2) })
                        : null;
                    if (shape == null)
                    {
                        tool.CancelSession();
                        sb.Append($"{t.Axis}=none ({tool.LastAction}) ");
                        continue;
                    }
                    saved++;
                    float m = (float)shape.Measurement.Distance;
                    if (t.Axis == "w") { measuredM.x = m; tapes[0] = shape.Entry; }
                    else if (t.Axis == "h") { measuredM.y = m; tapes[1] = shape.Entry; }
                    else { measuredM.z = m; tapes[2] = shape.Entry; }
                    sb.Append($"{t.Axis}={m.ToString("0.000", C)} m{(sa && sb2 ? "" : " (raw end)")} ");
                }
            }
            finally { tool.AreaMode = prevArea; }
            Gaps.RecordMeasured(gap.Id, measuredM, tapes);
            detail = $"{gap.Id} {request}: {sb.ToString().Trim()} file {Units.TripleMm(gap.SizeM.x, gap.SizeM.y, gap.SizeM.z)}";
            Log.Info($"Gap tapes: {detail}");
            return saved;
        }

        /// A ray from the eye at the target, snapped (structure features, then faces); within SnapTolerance of the target
        /// it counts, else the raw target is used (still on the cavity box: the file's numbers).
        static SurfaceHit SnapAt(Vector3 eye, Vector3 target, CavityBox box, SceneRoot root, float worldScale, out bool snapped)
        {
            snapped = false;
            var d = target - eye;
            float dist = d.magnitude;
            if (dist > 1e-4f && SnapService.TryRaySnap(new Ray(eye, d / dist), out var hit, dist + 1f)
                && Vector3.Distance(hit.point, target) <= SnapTolerance * worldScale)
            {
                snapped = true;
                return hit;
            }
            var n = root.transform.TransformDirection(root.PackageToRoot(box.D) - root.PackageToRoot(Vector3.zero)).normalized;
            return new SurfaceHit { point = target, rawPoint = target, normal = n, kind = SnapKind.None };
        }
    }
}
