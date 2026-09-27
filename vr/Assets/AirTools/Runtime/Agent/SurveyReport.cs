using System;
using System.Collections.Generic;
using System.Linq;
using AirTools.Scene;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Agent
{
    /// One measured object, as reported to POST /agent/observe (B1 hand-off §3). Metres.
    public class SurveyResult
    {
        public string id, label, group;
        public double w_m, h_m, area_m2, off_plane_m;
        public double[] angles_deg = Array.Empty<double>();
        /// Per corner: corner | edge | midpoint | plane | face | raw (didn't snap within 2 cm: the raw corner was used).
        public string[] snap = Array.Empty<string>();
        public bool unverified;
        public int notebook_id = -1;
        public int camera_id = -1;
    }

    public class SurveySkip
    {
        public string id, reason;
    }

    /// The /agent/observe bodies (B1 hand-off §3 survey_result, §4 slope_result). Pure, so the JSON is tested.
    public static class SurveyReport
    {
        public const int MaxItems = 500;

        /// status: done | aborted | no_structure. Only session_id, kind and each result's id are required; everything
        /// the app knows is sent anyway.
        public static JObject Survey(string sessionId, string requestId, string status, int planned, IList<SurveyResult> results,
            IList<SurveySkip> skipped, string label = null, string measure = null, bool tts = true)
        {
            var body = new JObject
            {
                ["session_id"] = sessionId,
                ["request_id"] = requestId,
                ["kind"] = "survey_result",
                ["status"] = status,
                ["planned"] = planned,
                ["tts"] = tts,
            };
            if (!string.IsNullOrEmpty(label)) body["label"] = label;
            if (!string.IsNullOrEmpty(measure)) body["measure"] = measure;
            var arr = new JArray();
            foreach (var r in (results ?? new List<SurveyResult>()).Take(MaxItems)) arr.Add(Result(r));
            body["results"] = arr;
            var sk = new JArray();
            foreach (var s in (skipped ?? new List<SurveySkip>()).Take(MaxItems)) sk.Add(new JObject { ["id"] = s.id, ["reason"] = s.reason });
            body["skipped"] = sk;
            return body;
        }

        static double R(double v, int digits) => Math.Round(v, digits);

        public static JObject Result(SurveyResult r)
        {
            var o = new JObject { ["id"] = r.id };
            if (!string.IsNullOrEmpty(r.label)) o["label"] = r.label;
            if (!string.IsNullOrEmpty(r.group)) o["group"] = r.group;
            o["w_m"] = R(r.w_m, 4);
            o["h_m"] = R(r.h_m, 4);
            o["area_m2"] = R(r.area_m2, 4);
            o["angles_deg"] = new JArray(r.angles_deg.Select(a => (object)R(a, 1)).ToArray());
            o["off_plane_m"] = R(r.off_plane_m, 4);
            o["snap"] = new JArray(r.snap.Cast<object>().ToArray());
            o["unverified"] = r.unverified;
            if (r.notebook_id >= 0) o["notebook_id"] = r.notebook_id;
            if (r.camera_id >= 0) o["camera_id"] = r.camera_id;
            return o;
        }

        /// slope_result (§4): run_m and fall_mm are required; low_end in scene-root metres.
        public static JObject Slope(string sessionId, string requestId, string target, double runM, double fallMm, Vector3 lowEnd,
            int notebookId, double? gravityResidualDeg = null, double? uncertaintyMm = null, bool tts = true)
        {
            var body = new JObject
            {
                ["session_id"] = sessionId,
                ["request_id"] = requestId,
                ["kind"] = "slope_result",
                ["target"] = target,
                ["run_m"] = R(runM, 4),
                ["fall_mm"] = R(fallMm, 1),
                ["low_end"] = new JArray(R(lowEnd.x, 3), R(lowEnd.y, 3), R(lowEnd.z, 3)),
                ["tts"] = tts,
            };
            if (gravityResidualDeg.HasValue) body["gravity_residual_deg"] = R(gravityResidualDeg.Value, 4);
            if (uncertaintyMm.HasValue) body["uncertainty_mm"] = R(uncertaintyMm.Value, 1);
            if (notebookId >= 0) body["notebook_id"] = notebookId;
            return body;
        }

        public static string SnapName(SnapKind k) => k switch
        {
            SnapKind.Corner => "corner",
            SnapKind.Edge => "edge",
            SnapKind.Midpoint => "midpoint",
            SnapKind.Plane => "plane",
            SnapKind.Face => "face",
            _ => "raw",
        };
    }
}
