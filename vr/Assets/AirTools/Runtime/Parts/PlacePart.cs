using System;
using System.Globalization;
using AirTools.Scene;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Parts
{
    /// place_part {part_id, model_url, pose, fits, clearance_mm} ("measure and replace" by voice; a backend draft, so
    /// every field is read tolerantly). Pure: parsing, the pose maths and the server's fit → FitReport run in the offline
    /// test runner; AppCommands.PlacePart does the loading and placing.
    public class PlacePartArgs
    {
        public string PartId;
        /// Relative to the server base (PartsClient.Resolve), e.g. "/parts/midea-…/model.glb".
        public string ModelUrl;
        public string Name;
        /// The component whose cavity it goes into (optional; else the part taken out most recently).
        public string ComponentId;
        public PlacePartPose Pose;
        public JToken Fits;
        public JToken Clearance;

        public static PlacePartArgs Parse(JObject a)
        {
            if (a == null) return new PlacePartArgs();
            string S(params string[] keys)
            {
                foreach (var k in keys)
                {
                    var t = a[k];
                    if (t != null && (t.Type == JTokenType.String || t.Type == JTokenType.Integer)) { var s = ((string)t)?.Trim(); if (!string.IsNullOrEmpty(s)) return s; }
                }
                return null;
            }
            var p = new PlacePartArgs
            {
                PartId = S("part_id", "id", "part"),
                ModelUrl = S("model_url", "glb_url", "url"),
                Name = S("name", "label", "title"),
                ComponentId = S("component_id", "component", "cavity"),
                Fits = a["fits"] ?? a["fit"],
                Clearance = a["clearance_mm"] ?? a["clearance"] ?? a["spare_mm"],
            };
            if (PlacePartPose.TryParse(a["pose"], out var pose)) p.Pose = pose;
            if (p.PartId == null && a["part"] is JObject part) p.PartId = (string)part["id"];
            return p;
        }

        public override string ToString() => $"place_part {PartId ?? "-"} model={ModelUrl ?? "-"} pose={(Pose.Valid ? Pose.ToString() : "cavity insert")} fits={Fits?.ToString(Newtonsoft.Json.Formatting.None) ?? "-"} clearance_mm={Clearance?.ToString(Newtonsoft.Json.Formatting.None) ?? "-"}";
    }

    /// A pose from place_part, in Unity package coordinates (the glTF scene / structure frame with X negated).
    public struct PlacePartPose
    {
        public bool Valid;
        public Vector3 Position;
        /// The rotation, when the pose gave one (a quaternion, axes, a yaw or a facing direction).
        public bool HasRotation;
        public Quaternion Rotation;
        /// True: Position is the model's origin (its mount-face centre); false (default): its front-bottom-centre, like
        /// cavity.insert.p.
        public bool AtOrigin;

        /// Forms: {p|position|pos|t: [x,y,z]} with {q|quat|quaternion|rotation: [x,y,z,w]} or {axes: [r, up, d]} or
        /// {yaw_deg|yaw: deg about +Y, glTF right-handed: +90 turns +Z to +X} or {forward|out|facing: [x,y,z]}, and
        /// {anchor: "origin" | "front_bottom_centre"}; or an array [x,y,z] / [x,y,z, qx,qy,qz,qw]. glTF frame.
        public static bool TryParse(JToken t, out PlacePartPose pose)
        {
            pose = default;
            if (t == null || t.Type == JTokenType.Null) return false;
            if (t is JArray arr)
            {
                if (!Nums(arr, 3, out var v)) return false;
                pose.Position = GltfFrame.ToUnity(v[0], v[1], v[2]);
                pose.Valid = true;
                if (arr.Count >= 7 && Nums(arr, 7, out var all)) { pose.Rotation = PlacePartMath.GltfToUnity(all[3], all[4], all[5], all[6]); pose.HasRotation = true; }
                return true;
            }
            if (!(t is JObject o)) return false;
            foreach (var k in new[] { "p", "position", "pos", "t", "translation", "point" })
                if (o[k] is JArray pa && Nums(pa, 3, out var pv)) { pose.Position = GltfFrame.ToUnity(pv[0], pv[1], pv[2]); pose.Valid = true; break; }
            if (!pose.Valid) return false;
            foreach (var k in new[] { "q", "quat", "quaternion", "rotation" })
                if (o[k] is JArray qa && Nums(qa, 4, out var qv))
                {
                    pose.Rotation = PlacePartMath.GltfToUnity(qv[0], qv[1], qv[2], qv[3]);
                    pose.HasRotation = true;
                    break;
                }
            if (!pose.HasRotation && o["axes"] is JArray axes && axes.Count >= 3 && axes[1] is JArray ua && axes[2] is JArray da
                && Nums(ua, 3, out var up) && Nums(da, 3, out var d))
            {
                pose.Rotation = PlacePartMath.UprightFacing(GltfFrame.ToUnity(d[0], d[1], d[2]), GltfFrame.ToUnity(up[0], up[1], up[2]));
                pose.HasRotation = true;
            }
            if (!pose.HasRotation)
                foreach (var k in new[] { "yaw_deg", "yaw" })
                    if (o[k] != null && (o[k].Type == JTokenType.Float || o[k].Type == JTokenType.Integer))
                    {
                        double yaw = (double)o[k] * Math.PI / 180.0;
                        // glTF: +Z turned by yaw about +Y (right-handed) = (sin, 0, cos); X negated for Unity.
                        pose.Rotation = PlacePartMath.UprightFacing(GltfFrame.ToUnity(Math.Sin(yaw), 0, Math.Cos(yaw)), Vector3.up);
                        pose.HasRotation = true;
                        break;
                    }
            if (!pose.HasRotation)
                foreach (var k in new[] { "forward", "out", "facing", "d" })
                    if (o[k] is JArray fa && Nums(fa, 3, out var f))
                    {
                        pose.Rotation = PlacePartMath.UprightFacing(GltfFrame.ToUnity(f[0], f[1], f[2]), Vector3.up);
                        pose.HasRotation = true;
                        break;
                    }
            var at = o["anchor"] ?? o["origin"];
            var anchor = at != null && at.Type == JTokenType.String ? ((string)at).Trim().ToLowerInvariant() : null;
            pose.AtOrigin = anchor == "origin" || anchor == "model_origin" || anchor == "mount";
            return true;
        }

        static bool Nums(JArray a, int n, out double[] v)
        {
            v = null;
            if (a == null || a.Count < n) return false;
            v = new double[n];
            for (int i = 0; i < n; i++)
            {
                var t = a[i];
                if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer)) return false;
                v[i] = (double)t;
                if (double.IsNaN(v[i]) || double.IsInfinity(v[i])) return false;
            }
            return true;
        }

        public override string ToString() =>
            $"p={Position.ToString("F3")}{(HasRotation ? $" facing={(Rotation * Vector3.forward).ToString("F2")}" : "")}{(AtOrigin ? " (origin)" : "")}";
    }

    /// The pose maths for place_part. Part space (PartMath): metres, +Y up, +Z the front (out toward the viewer), the
    /// origin at the mount-face centre. The cavity frame (CavityBox): R the viewer's right, U up, D out of the opening.
    /// A part faces out of the opening when its +Z is D and its +Y is U; its +X then is Cross(U, D) = −R (a part's
    /// own right is the viewer's left, as for anything facing you). Pure: no engine calls (Quaternion * Vector3 and the
    /// struct constructors are managed; LookRotation / Inverse are not, so they aren't used).
    public static class PlacePartMath
    {
        /// A glTF rotation in Unity coordinates: the X mirror conjugates it to (x, −y, −z, w) (as glTFast does).
        public static Quaternion GltfToUnity(double x, double y, double z, double w)
        {
            var q = new Quaternion((float)x, (float)-y, (float)-z, (float)w);
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return n > 1e-8f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : Quaternion.identity;
        }

        /// The rotation whose +X, +Y, +Z are `right`, `up`, `forward` (orthonormal, right-handed in Unity's sense:
        /// right = Cross(up, forward)). Shepperd's method on the rotation matrix.
        public static Quaternion FromBasis(Vector3 right, Vector3 up, Vector3 forward)
        {
            float m00 = right.x, m10 = right.y, m20 = right.z;
            float m01 = up.x, m11 = up.y, m21 = up.z;
            float m02 = forward.x, m12 = forward.y, m22 = forward.z;
            float trace = m00 + m11 + m22;
            float x, y, z, w;
            if (trace > 0f)
            {
                float s = Mathf.Sqrt(trace + 1f) * 2f;
                w = 0.25f * s; x = (m21 - m12) / s; y = (m02 - m20) / s; z = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
                w = (m21 - m12) / s; x = 0.25f * s; y = (m01 + m10) / s; z = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
                w = (m02 - m20) / s; x = (m01 + m10) / s; y = 0.25f * s; z = (m12 + m21) / s;
            }
            else
            {
                float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
                w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25f * s;
            }
            float n = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            return new Quaternion(x / n, y / n, z / n, w / n);
        }

        /// Gravity-aligned: +Y = `up`, +Z = `outward` flattened onto the horizontal (a pose tilted by a noisy fit still
        /// stands the part upright). A vertical `outward` keeps the part facing +Z.
        public static Quaternion UprightFacing(Vector3 outward, Vector3 up)
        {
            up = up.sqrMagnitude > 1e-12f ? up.normalized : Vector3.up;
            var f = outward - up * Vector3.Dot(outward, up);
            if (f.sqrMagnitude < 1e-10f) f = Vector3.forward - up * Vector3.Dot(Vector3.forward, up);
            if (f.sqrMagnitude < 1e-10f) f = Vector3.right - up * Vector3.Dot(Vector3.right, up);
            f.Normalize();
            var r = Vector3.Cross(up, f).normalized;
            return FromBasis(r, up, f);
        }

        /// The front-bottom-centre of a part's true-size box (PartMath.LocalBox), in part space.
        public static Vector3 FrontBottomCentre(Bounds localBox) => new Vector3(localBox.center.x, localBox.min.y, localBox.max.z);

        /// Where the part's origin goes so its front-bottom-centre sits at `anchor` (or its origin, atOrigin), turned by
        /// `rotation`. All in one frame (SceneRoot space in the app).
        public static Vector3 OriginFor(Vector3 anchor, Quaternion rotation, Bounds localBox, bool atOrigin = false) =>
            atOrigin ? anchor : anchor - rotation * FrontBottomCentre(localBox);
    }

    /// place_part's `fits` and `clearance_mm` as a FitReport the outline and callout already know how to show (colour +
    /// glyph + words). Tolerant: fits as true / false, a word ("fits", "tight", "too_big", …) or an object {status,
    /// spare_mm, axis, note}; clearance as millimetres, {w, h, d} / {left, right, top, …}, or [w, h, d]. The tightest
    /// clearance is the one said. Null when neither says anything (the app's own fit check runs instead).
    public static class PlacePartFit
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public static FitReport From(JToken fits, JToken clearance, string where = "the gap")
        {
            FitStatus? status = null;
            string note = null, axis = null;
            float? spare = null;

            switch (fits?.Type)
            {
                case JTokenType.Boolean: status = (bool)fits ? FitStatus.Green : FitStatus.Red; break;
                case JTokenType.Integer: case JTokenType.Float: status = (double)fits > 0 ? FitStatus.Green : FitStatus.Red; break;
                case JTokenType.String: status = Word((string)fits); break;
                case JTokenType.Object:
                    var o = (JObject)fits;
                    var st = o["status"] ?? o["fits"] ?? o["verdict"];
                    if (st?.Type == JTokenType.Boolean) status = (bool)st ? FitStatus.Green : FitStatus.Red;
                    else if (st?.Type == JTokenType.String) status = Word((string)st);
                    if (o["spare_mm"] is JToken sm && (sm.Type == JTokenType.Float || sm.Type == JTokenType.Integer)) spare = (float)sm;
                    axis = (string)o["axis"];
                    note = (string)o["note"];
                    break;
            }
            if (Tightest(clearance, out float c, out string cAxis)) { spare = spare.HasValue ? Mathf.Min(spare.Value, c) : c; if (spare == c) axis = cAxis ?? axis; }
            if (!status.HasValue && spare.HasValue) status = spare.Value >= 0f ? FitStatus.Green : FitStatus.Red;
            if (!status.HasValue) return null;

            var r = new FitReport { Status = status.Value, SpareMm = spare, OpeningSource = "server" };
            string at = AxisWord(axis);
            switch (status.Value)
            {
                case FitStatus.Green:
                    r.Verdict = spare.HasValue ? $"Fits {where} · {Copy.Gap(spare.Value)} spare" : $"Fits {where}";
                    r.Reason = at != null ? $"Tightest {at}" : note ?? "";
                    r.Action = FitAction.ComparePrices; r.ActionLabel = "Compare prices";
                    break;
                case FitStatus.Amber:
                    r.Verdict = spare.HasValue ? $"Tight in {where} · {Copy.Gap(spare.Value)}" : $"Tight in {where}";
                    r.Reason = note ?? (at != null ? $"Tightest {at}" : "");
                    r.Action = FitAction.MeasureIt; r.ActionLabel = "Measure it";
                    break;
                default:
                    r.Verdict = spare.HasValue && spare.Value < 0f ? $"Too big for {where} · {Copy.Gap(-spare.Value)} over" : $"Doesn't fit {where}";
                    r.Reason = note ?? (at != null ? $"Tightest {at}" : "");
                    r.Action = FitAction.FindSmaller; r.ActionLabel = "Find a smaller one";
                    break;
            }
            r.Headline = $"server: fits={fits?.ToString(Newtonsoft.Json.Formatting.None) ?? "-"} clearance_mm={clearance?.ToString(Newtonsoft.Json.Formatting.None) ?? "-"}" +
                         (spare.HasValue ? $" tightest={spare.Value.ToString("0", C)}{(axis != null ? $" ({axis})" : "")}" : "");
            r.Lines.Add(r.Headline);
            return r;
        }

        static FitStatus? Word(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant().Replace(' ', '_'))
            {
                case "fits": case "fit": case "yes": case "true": case "ok": case "green": return FitStatus.Green;
                case "tight": case "check": case "maybe": case "amber": case "close": return FitStatus.Amber;
                case "too_big": case "too_wide": case "too_tall": case "too_deep": case "too_small": case "no": case "false": case "red": case "doesnt_fit": case "does_not_fit":
                    return FitStatus.Red;
                default: return null;   // "unknown": nothing to show
            }
        }

        /// The smallest clearance given, in mm, and which side it is.
        public static bool Tightest(JToken t, out float mm, out string axis)
        {
            mm = 0f; axis = null;
            if (t == null) return false;
            bool any = false;
            float best = 0f;
            string bestAxis = null;
            void Take(JToken v, string key)
            {
                if (v == null || (v.Type != JTokenType.Float && v.Type != JTokenType.Integer)) return;
                float f = (float)v;
                if (!any || f < best) { best = f; bestAxis = key; any = true; }
            }
            switch (t.Type)
            {
                case JTokenType.Float: case JTokenType.Integer: Take(t, null); break;
                case JTokenType.Array:
                    var a = (JArray)t;
                    string[] names = { "w", "h", "d" };
                    for (int i = 0; i < a.Count; i++) Take(a[i], i < 3 ? names[i] : null);
                    break;
                case JTokenType.Object:
                    foreach (var p in ((JObject)t).Properties()) Take(p.Value, p.Name);
                    break;
            }
            mm = best;
            axis = bestAxis;
            return any;
        }

        static string AxisWord(string axis) => (axis ?? "").Trim().ToLowerInvariant() switch
        {
            "w" or "width" or "x" => "across",
            "h" or "height" or "y" => "in height",
            "d" or "depth" or "z" => "in depth",
            "left" or "right" or "sides" or "side" => "at the sides",
            "top" or "above" => "above",
            "bottom" or "below" => "below",
            "front" => "in front",
            "back" or "behind" => "behind",
            _ => null,
        };
    }
}
