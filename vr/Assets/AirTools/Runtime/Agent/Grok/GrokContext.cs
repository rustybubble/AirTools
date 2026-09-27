using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// Everything the agent context is built from, as plain values (so building it is pure and testable).
    /// Points are in package space: the scene package's own frame in Unity axes (SceneRoot.Content local, the space
    /// the structure layer and the cameras live in).
    public sealed class ContextSnapshot
    {
        public Dictionary<string, object> Measurement;
        public string SelectedPartId;
        public List<string> CandidateIds = new List<string>();
        /// (part_id, count) per placed part, first placement first.
        public List<KeyValuePair<string, int>> Placed = new List<KeyValuePair<string, int>>();
        public string Tool;
        /// Voice / scene questions: the nearest photos [{id, jpg_b64}], nearest first.
        public IList<Dictionary<string, string>> Frames;
        /// The loaded scene package as GET /scenes names it; null for the built-in scene (the server doesn't know it).
        public string Site;
        public double Scale = 1.0;
        /// scalemodels: where Scale came from: "site_default" (SiteScales, e.g. the kitchen's ×1.63), "user" (a tape +
        /// Set scale) or "none" (the package's own). Null = not known (left out).
        public string ScaleSource;
        /// The scan is what the wearer sees (World, Tabletop). False in passthrough: no capture frame matches the view.
        public bool SceneInView;
        /// cameras.r&lt;rev&gt;.json id of the photo nearest the view (= frames[0].id when frames are sent).
        public string FrameId;
        /// That photo's JPEG thumb, base64 (its original bytes: the server's replay cache keys on them) — only for
        /// commands that read it (see <see cref="GrokContext.NeedsFrame"/>) and every command while a coach runs.
        public string FrameJpgB64;
        /// The aimed point (right-hand tool ray on the scene), package space. Null when nothing is aimed.
        public Vector3? PointerPackage;
        /// The selected part's box [x0, y0, x1, y1] (0–1, top-left origin) in FrameId's photo.
        public float[] PlacedBox;
        /// The aimed point [x, y] (0–1) in FrameId's photo.
        public float[] DrillPx;
        public string Location;
        public string Address;
        public string Placement;
        public string SurveyId;
        /// e2e: the open gap (a removed component's cavity: {component_id, label, w_m, h_m, d_m, estimated, measured?})
        /// and the ids taken out (Gaps.CurrentContext / RemovedIds; null when the scene has no parts, [] when none is
        /// out). The e2e backend patch fits "one that fits" to it.
        public Dictionary<string, object> Cavity;
        public List<string> Removed;
        /// assetgen: the opening taped last (a window's width and height tapes: {w_m, h_m, d_m?, label}; Parts.Openings).
        /// The p4-asset backend patch fits "a window frame that fits" to it.
        public Dictionary<string, object> Opening;
    }

    /// The agent context (backend docs/api.md §6), sent with every /agent/command and /voice/command. Pure: the live
    /// snapshot is gathered by <see cref="GrokView"/>.
    /// - `site` + `scale`: the loaded package (left out for the built-in scene, B1/B3 hand-off §1); `scale_source`
    ///   (scalemodels): "site_default" | "user" | "none".
    /// - `frame_id`: the capture photo nearest the view; `placed_box` / `drill_px`: the selected part and the aimed
    ///   point projected into that photo with its pose and full-frame intrinsics; `pointer`: the aimed point in the
    ///   structure file's frame (glTF: the Unity X flip undone). None of these in passthrough or on the built-in scene.
    /// - `frame_jpg_b64`: that photo's thumb, only for the commands that read it (label_view, check_quote, the install
    ///   coach's check_step): ~27 KB of base64 each (kitchen thumbs: median 20 KB JPEG, n = 21) against a ~1 KB context,
    ///   and on the server it also unhides label_view / check_quote for the LLM and runs a vision call on every
    ///   find_part. Voice commands carry `frames` instead (the server falls back to frames[0]).
    /// - `location` (a setting, "Atlanta, GA" by default), `address` (a setting, empty = left out), `placement`,
    ///   `survey_id` (the survey whose pins are on screen, from G2).
    public static class GrokContext
    {
        public static Dictionary<string, object> Build(ContextSnapshot s)
        {
            var ctx = new Dictionary<string, object>();
            if (s == null) return ctx;
            if (s.Measurement != null) ctx["measurement"] = s.Measurement;
            if (!string.IsNullOrEmpty(s.SelectedPartId)) ctx["selected_part_id"] = s.SelectedPartId;
            if (s.Placed != null && s.Placed.Count > 0)
            {
                var placed = new List<Dictionary<string, object>>();
                var seen = new HashSet<string>();
                foreach (var kv in s.Placed)
                {
                    if (string.IsNullOrEmpty(kv.Key) || !seen.Add(kv.Key)) continue;
                    placed.Add(new Dictionary<string, object> { ["part_id"] = kv.Key, ["count"] = Math.Max(1, kv.Value) });
                }
                if (placed.Count > 0) ctx["placed"] = placed;
            }
            if (s.CandidateIds != null && s.CandidateIds.Count > 0) ctx["candidate_ids"] = new List<string>(s.CandidateIds);
            if (!string.IsNullOrEmpty(s.Tool)) ctx["tool"] = s.Tool;
            // Drone photos describe the scan, not the room you see in passthrough.
            if (s.SceneInView && s.Frames != null && s.Frames.Count > 0) ctx["frames"] = s.Frames;
            if (!string.IsNullOrEmpty(s.Site))
            {
                ctx["site"] = s.Site;
                ctx["scale"] = Math.Round(s.Scale, 5);
                if (!string.IsNullOrEmpty(s.ScaleSource)) ctx["scale_source"] = s.ScaleSource;   // scalemodels
                if (s.SceneInView)
                {
                    string frameId = s.FrameId;
                    if (string.IsNullOrEmpty(frameId) && s.Frames != null && s.Frames.Count > 0 && s.Frames[0] != null)
                        s.Frames[0].TryGetValue("id", out frameId);
                    if (!string.IsNullOrEmpty(frameId))
                    {
                        ctx["frame_id"] = frameId;
                        if (!string.IsNullOrEmpty(s.FrameJpgB64)) ctx["frame_jpg_b64"] = s.FrameJpgB64;
                        if (ValidBox(s.PlacedBox)) ctx["placed_box"] = s.PlacedBox;
                        if (s.DrillPx != null && s.DrillPx.Length == 2) ctx["drill_px"] = s.DrillPx;
                    }
                    if (s.PointerPackage.HasValue) ctx["pointer"] = ToStructureFrame(s.PointerPackage.Value);
                }
            }
            ctx["location"] = string.IsNullOrWhiteSpace(s.Location) ? GrokState.DefaultLocation : s.Location.Trim();
            if (!string.IsNullOrWhiteSpace(s.Address)) ctx["address"] = s.Address.Trim();
            if (!string.IsNullOrWhiteSpace(s.Placement)) ctx["placement"] = s.Placement.Trim();
            if (!string.IsNullOrEmpty(s.SurveyId)) ctx["survey_id"] = s.SurveyId;
            if (s.Cavity != null) ctx["cavity"] = s.Cavity;                          // e2e
            if (s.Removed != null) ctx["removed"] = new List<string>(s.Removed);   // e2e: [] = nothing out (the scene has parts)
            if (s.Opening != null) ctx["opening"] = s.Opening;                      // assetgen
            return ctx;
        }

        // ---------------- frames ----------------

        /// A package-space point (Unity axes) in the structure file's frame (glTF: X flipped back), to the millimetre.
        public static double[] ToStructureFrame(Vector3 packagePoint)
        {
            var g = GltfFrame.ToGltf(packagePoint);
            return new[] { Math.Round(g.x, 3), Math.Round(g.y, 3), Math.Round(g.z, 3) };
        }

        /// The eight corners of a box (its own local space).
        public static Vector3[] Corners(Bounds b)
        {
            var mn = b.min; var mx = b.max;
            var r = new Vector3[8];
            for (int i = 0; i < 8; i++)
                r[i] = new Vector3((i & 1) == 0 ? mn.x : mx.x, (i & 2) == 0 ? mn.y : mx.y, (i & 4) == 0 ? mn.z : mx.z);
            return r;
        }

        /// Screen box [x0, y0, x1, y1] (0–1, top-left origin) of package-space points in a capture photo, clipped to the
        /// photo; null when a point is behind the camera or the box misses the photo.
        public static float[] BoxInFrame(SceneCameraJson cam, IReadOnlyList<Vector3> packagePoints)
        {
            if (cam == null || cam.R == null || cam.t == null || cam.w <= 0 || cam.h <= 0 || packagePoints == null || packagePoints.Count == 0) return null;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var p in packagePoints)
            {
                if (!SceneCameras.Project(cam, p, out var uv)) return null;
                x0 = Math.Min(x0, uv.x); y0 = Math.Min(y0, uv.y);
                x1 = Math.Max(x1, uv.x); y1 = Math.Max(y1, uv.y);
            }
            x0 = Clamp01(x0); y0 = Clamp01(y0); x1 = Clamp01(x1); y1 = Clamp01(y1);
            if (x1 - x0 < 1e-4f || y1 - y0 < 1e-4f) return null;
            return new[] { R4(x0), R4(y0), R4(x1), R4(y1) };
        }

        /// A package-space point [x, y] (0–1, top-left origin) in a capture photo; null when it's behind the camera, or
        /// (unless `clamp`, which pins it to the photo's edge instead) outside the picture.
        public static float[] PointInFrame(SceneCameraJson cam, Vector3 packagePoint, bool clamp = false)
        {
            if (cam == null || cam.R == null || cam.t == null || cam.w <= 0 || cam.h <= 0) return null;
            if (!SceneCameras.Project(cam, packagePoint, out var uv)) return null;
            if (clamp) return new[] { R4(Clamp01(uv.x)), R4(Clamp01(uv.y)) };
            if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) return null;
            return new[] { R4(uv.x), R4(uv.y) };
        }

        static bool ValidBox(float[] b) => b != null && b.Length == 4 && b[2] > b[0] && b[3] > b[1];
        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        static float R4(float v) => (float)Math.Round(v, 4);

        // ---------------- which commands read the frame ----------------

        const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        // The server's fast paths (server/agent.py _LABEL_RE, _QUOTE_RE, _COACH_CHECK_RE), mirrored.
        static readonly Regex s_Label = new Regex(@"\b(?:label (?:this|that|these|it|everything|the view)|what am i looking at|what(?:'s| is) (?:all )?this stuff)\b", Opt);
        static readonly Regex s_Quote = new Regex(@"^(?!.*\bmanual\b).*\b(?:check|read|review|look (?:at|over)|go over)\b.*\b(?:quote|estimate|bid)s?\b", Opt);
        static readonly Regex s_CheckWork = new Regex(@"^\W*(?:ok(?:ay)?\W+)?check (?:it|this|that|my work)\W*$", Opt);
        static readonly Regex s_Done = new Regex(@"^\W*(?:ok(?:ay)?\W+)?(?:all |i'?m )?done\W*$", Opt);

        /// True for a typed command that reads context.frame_jpg_b64: "what am I looking at?" / "label this"
        /// (label_view), "check this quote" (check_quote), and the install coach's "check it" / "done" (check_step).
        public static bool NeedsFrame(string text, bool coachRunning)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (s_Label.IsMatch(text) || s_Quote.IsMatch(text) || s_CheckWork.IsMatch(text)) return true;
            return coachRunning && s_Done.IsMatch(text);
        }
    }
}
