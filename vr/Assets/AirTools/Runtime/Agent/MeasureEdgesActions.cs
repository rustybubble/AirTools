using System.Collections.Generic;
using System.Globalization;
using AirTools.Core;
using AirTools.Scene;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Agent
{
    /// measure_edges {label, request_id, what, segments: [{a, b}], edge_ids?} (backend server/structure_measure.py):
    /// "measure the length of the top roof from end to end" on a scan with planes but no labelled objects (the GT LCC
    /// scans). The headset tapes each segment a → b with the real tape — snapped to the scan, one notebook entry each
    /// titled `label` ("Top roof length"), one undo step for them all — like check_slope's tape. The endpoints are in the
    /// structure file's frame (glTF, like the context's `pointer`): GltfFrame.ToUnity makes them StructureLayer points.
    /// Pure parsing here (tested); the taping is SurveyRunner.MeasureSegments.
    public static class MeasureEdgesActions
    {
        public sealed class Args
        {
            public string Label, RequestId, What;
            /// Structure-layer points (Unity axes: the file's X flipped).
            public readonly List<Vector3> A = new List<Vector3>(), B = new List<Vector3>();
            public readonly List<string> EdgeIds = new List<string>();
            public int Count => A.Count;
        }

        /// The action's args; segments with a bad point are skipped. Null for none.
        public static Args Parse(JObject a)
        {
            if (a == null) return null;
            var args = new Args
            {
                Label = Str(a["label"]) ?? "Distance",
                RequestId = Str(a["request_id"]),
                What = Str(a["what"]) ?? "length",
            };
            if (a["segments"] is JArray segs)
                foreach (var s in segs)
                {
                    if (!(s is JObject o) || !TryPoint(o["a"], out var p) || !TryPoint(o["b"], out var q)) continue;
                    args.A.Add(GltfFrame.ToUnity(p));
                    args.B.Add(GltfFrame.ToUnity(q));
                }
            if (a["edge_ids"] is JArray ids)
                foreach (var t in ids) { var s = Str(t); if (!string.IsNullOrEmpty(s)) args.EdgeIds.Add(s); }
            return args.Count > 0 || args.EdgeIds.Count > 0 ? args : null;
        }

        static bool TryPoint(JToken t, out Vector3 p)
        {
            p = Vector3.zero;
            if (!(t is JArray arr) || arr.Count < 3) return false;
            var v = new float[3];
            for (int i = 0; i < 3; i++)
            {
                var x = arr[i];
                if (x.Type == JTokenType.Float || x.Type == JTokenType.Integer) v[i] = (float)x;
                else if (x.Type != JTokenType.String || !float.TryParse((string)x, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
                if (float.IsNaN(v[i]) || float.IsInfinity(v[i])) return false;
            }
            p = new Vector3(v[0], v[1], v[2]);
            return true;
        }

        static string Str(JToken t) => t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Object || t.Type == JTokenType.Array ? null : ((string)t)?.Trim();

        [AgentAction("measure_edges")]
        static bool MeasureEdges(AgentAction a, string reply)
        {
            var args = Parse(a.args);
            bool ok = args != null && AppCommands.MeasureEdges(args);
            Log.Info($"Measure edges: \"{args?.Label}\" {args?.Count ?? 0} segment(s) {args?.EdgeIds.Count ?? 0} edge id(s) → {(ok ? "taped" : "refused")}");
            return ok;
        }
    }
}
