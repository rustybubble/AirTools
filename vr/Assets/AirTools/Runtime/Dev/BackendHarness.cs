using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Scene;
using AirTools.Structure;
using AirTools.Tools;
using UnityEngine;

namespace AirTools.Dev
{
    /// Live checks for the backend integration, driven through `unity command eval` in Play mode (see CLAUDE.md):
    ///   LoadSite("kitchen") → SceneStatus() → CheckAlignment() (X-flip check: rays aimed at structure corners hit the
    ///   collision mesh right there) → CheckObjects(3) (tape every door/drawer rectangle corner to corner n times with
    ///   aim noise: repeatability and agreement with w_m) → SetScale(real) → Ask(x,y,z) → Command(text) → Voice(path).
    /// Points are in SceneRoot space unless noted. Results are logged as [AirTools.Check] lines.
    public static partial class BackendHarness
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static SceneRoot Root => Services.Get<SceneRoot>();
        static SceneStreamer Streamer => Services.Get<SceneStreamer>();

        public static string LoadSite(string site) { AppCommands.OpenChest(); return AppCommands.LoadSite(site) ? $"loading {site}" : "busy or no streamer"; }

        public static string SceneStatus()
        {
            var s = Streamer; var r = Root;
            if (s == null || r == null) return "no streamer / root";
            var sb = new StringBuilder();
            sb.Append($"site={r.Site ?? "built-in"} runtime={r.IsRuntimePackage} loading={s.Loading} status=\"{s.Status}\" err={s.LastError} swaps={s.Swaps} load={s.LastLoadSeconds:0.00}s");
            sb.Append($" stage=\"{s.LoadStage}\" failed={(s.LoadFailed ? s.FailedSite : "-")}");   // fix-ux
            sb.Append($" calibration={r.Calibration.ToString("0.0000", C)} cameras={r.Package?.cameras?.Length ?? 0}");
            if (r.Structure != null) sb.Append($" structure=\"{r.Structure.Summary}\"");
            if (r.Content != null)
            {
                int tris = 0;
                foreach (var mf in r.Content.GetComponentsInChildren<MeshFilter>(true))
                    if (mf.sharedMesh != null && mf.GetComponent<Renderer>() is Renderer rd && rd.enabled) tris += (int)(mf.sharedMesh.GetIndexCount(0) / 3);
                sb.Append($" visual_tris={tris}");
            }
            var rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
            if (rig != null) sb.Append($" rig={rig.transform.position.ToString("F3")} yaw={rig.transform.eulerAngles.y:0.0}");
            return sb.ToString();
        }

        static Vector3 Eye()
        {
            var cam = Camera.main;
            var rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
            if (cam != null && cam.transform.position.y - (rig != null ? rig.transform.position.y : 0f) > 0.5f) return cam.transform.position;
            return (rig != null ? rig.transform.position : Vector3.zero) + Vector3.up * 1.6f;
        }

        /// X-flip / alignment check: for structure corners visible from `eye` (world; default the wearer's eye), the
        /// collision mesh hit along the ray to the corner must be within `tolerance` of it. A missing or doubled X flip
        /// puts the layer metres away from the mesh.
        public static string CheckAlignment(float tolerance = 0.03f, int maxCorners = 400)
        {
            var r = Root;
            if (r?.Structure == null) return "no structure layer";
            var space = r.StructureSpace;
            var eye = Eye();
            var dists = new List<float>();
            int visible = 0, tested = 0;
            foreach (var c in r.Structure.Corners.Take(maxCorners))
            {
                var p = space.TransformPoint(c.p);
                var dir = p - eye;
                float d = dir.magnitude;
                if (d < 0.2f) continue;
                tested++;
                if (!Physics.Raycast(eye, dir / d, out var hit, d + 1f, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore)) continue;
                float miss = Vector3.Distance(hit.point, p);
                if (hit.distance < d - 0.25f) continue;   // occluded by something well in front: not visible from here
                visible++;
                dists.Add(miss);
            }
            if (dists.Count == 0) return $"no visible corners out of {tested}";
            dists.Sort();
            float median = dists[dists.Count / 2], p90 = dists[(int)(dists.Count * 0.9f)];
            float within = dists.Count(x => x <= tolerance) / (float)dists.Count;
            bool pass = median <= tolerance && within >= 0.6f;
            Log.Check("backend.structure.alignment", pass, $"visible={visible}/{tested} median_mm={median * 1000:0.0} p90_mm={p90 * 1000:0.0} within{tolerance * 100:0}cm={within:P0}");
            return $"{(pass ? "PASS" : "FAIL")} corners visible {visible}/{tested}: mesh hit vs corner median {median * 1000:0.0} mm, p90 {p90 * 1000:0.0} mm, {within:P0} within {tolerance * 100:0} cm";
        }

        /// Snap a ray from the eye at a scene-root point.
        public static string Snap(float x, float y, float z)
        {
            var r = Root;
            var target = r != null ? r.transform.TransformPoint(new Vector3(x, y, z)) : new Vector3(x, y, z);
            var eye = Eye();
            if (!SnapService.TryRaySnap(new Ray(eye, (target - eye).normalized), out var h, 60f)) return "miss";
            var p = r != null ? r.transform.InverseTransformPoint(h.point) : h.point;
            return $"{h.kind} {p.ToString("F4")} feature={h.structureFeature} raw={(r != null ? r.transform.InverseTransformPoint(h.rawPoint) : h.rawPoint).ToString("F4")}";
        }

        /// Tape every structure object (door / drawer rectangle) along its bottom edge corner to corner `n` times with
        /// ±`noise` m aim noise through the real measure tool. Reports each object's spread and its difference from w_m.
        /// standoff > 0: tape each object from `standoff` m in front of it (along its plane normal, on the side the drone
        /// photographed it from), like a person standing at the cabinet; 0 = from the wearer's eye.
        public static string CheckObjects(int n = 3, float noise = 0.01f, int maxObjects = 12, int seed = 1, float standoff = 0.6f, bool verbose = false, int only = -1)
        {
            var r = Root; var tool = Services.Get<MeasureTool>(); var hub = Services.Get<ToolInputHub>();
            if (r?.Structure == null || tool == null || hub == null) return "needs a structure layer, the measure tool and the input hub";
            AppCommands.EquipTool("measure");
            var rng = new System.Random(seed);
            var eye = Eye();
            var space = r.StructureSpace;
            var sb = new StringBuilder();
            int passed = 0, tested = 0;
            foreach (var o in r.Structure.Objects.Take(maxObjects))
            {
                if (only >= 0 && o.id != $"o{only}") continue;
                var a = space.TransformPoint(o.corners[0]); var b = space.TransformPoint(o.corners[1]);
                if (standoff > 0f)
                {
                    // Stand at the cabinet, on the side the drone photographed it from (shared with the survey).
                    var centre = space.TransformPoint((o.corners[0] + o.corners[1] + o.corners[2] + o.corners[3]) * 0.25f);
                    eye = centre + AirTools.Agent.SurveyPlanner.FacingNormal(r, o, centre, Eye()) * standoff * r.transform.lossyScale.x;
                }
                var readings = new List<double>();
                for (int k = 0; k < n; k++)
                {
                    Vector3 Noise() => new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1)) * noise;
                    int before = tool.Shapes.Count;
                    tool.Session.Clear();   // a missed click must not leave a point for the next trial
                    var ta = a + Noise(); var tb = b + Noise();
                    ClickWorld(hub, eye, ta);
                    string ka = tool.Session.Count > 0 ? Describe(tool.Session.Kinds[tool.Session.Count - 1], tool.FromFrame(tool.Session.Points[tool.Session.Count - 1]), a) : "miss:" + tool.LastAction;
                    ClickWorld(hub, eye, tb);
                    string kb = tool.Session.Count > 1 ? Describe(tool.Session.Kinds[tool.Session.Count - 1], tool.FromFrame(tool.Session.Points[tool.Session.Count - 1]), b) : "miss:" + tool.LastAction;
                    hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
                    if (verbose) sb.Append($"\n    trial {k}: A {ka} | B {kb}");
                    if (tool.Shapes.Count > before) readings.Add(tool.Shapes[tool.Shapes.Count - 1].Measurement.Distance);
                }
                if (readings.Count == 0) { sb.Append($"\n{o.label} {o.id}: no reading (not visible?)"); continue; }
                tested++;
                double spread = readings.Max() - readings.Min(), mean = readings.Average();
                double wm = o.widthM * r.Calibration;
                bool pass = spread <= 0.005 && readings.Count == n;
                if (pass) passed++;
                sb.Append($"\n{o.label} {o.id}: {string.Join(" / ", readings.Select(v => (v * 1000).ToString("0.0", C)))} mm, spread {spread * 1000:0.0} mm, w_m {wm * 1000:0.0} mm (Δ {(mean - wm) * 1000:+0.0;-0.0} mm){(pass ? "" : "  ← check")}");
                Log.Check($"backend.tape.{o.label}.{o.id}", pass, $"readings_mm={string.Join(",", readings.Select(v => (v * 1000).ToString("0.0", C)))} spread_mm={spread * 1000:0.0} w_m_mm={wm * 1000:0.0}");
            }
            return $"{passed}/{tested} objects repeat within 5 mm{sb}";
        }

        static string Describe(SnapKind kind, Vector3 got, Vector3 want) => $"{kind} {Vector3.Distance(got, want) * 1000:0}mm off";

        static void ClickWorld(ToolInputHub hub, Vector3 from, Vector3 to)
        {
            var pose = ToolInputHub.RayPose(from, to);
            hub.RaisePressStart(ToolHand.Right, pose);
            hub.RaisePressEnd(ToolHand.Right, pose);
        }

        public static string SetScale(double realMetres) =>
            $"{(AppCommands.SetScale(realMetres) ? "ok" : "failed")}: {ScaleCalibration.LastResult}";

        public static string Ask(float x, float y, float z, string question = "What is this?")
        {
            var r = Root; var ask = Services.Get<SceneAsk>();
            if (ask == null) return "no SceneAsk";
            ask.AskAt(question, r != null ? r.transform.TransformPoint(new Vector3(x, y, z)) : new Vector3(x, y, z));
            return "asked; poll AskResult()";
        }

        public static string AskResult()
        {
            var ask = Services.Get<SceneAsk>(); var r = Root;
            if (ask == null) return "no SceneAsk";
            var sb = new StringBuilder($"busy={ask.Busy} frames=[{string.Join(",", ask.LastFrameIds)}] err={ask.LastError} answer=\"{ask.LastAnswer?.answer}\" frame={ask.LastAnswer?.frame_id} query=\"{ask.LastAnswer?.part_query}\"");
            foreach (var p in ask.Pins)
                sb.Append($"\npin frame {p.frameId} at {(r != null && r.Content != null ? r.PackageToRoot(p.packagePoint).ToString("F3") : p.packagePoint.ToString("F3"))} \"{p.text}\"");
            return sb.ToString();
        }

        public static string Command(string text) => AppCommands.SendCommand(text) ? "sent; poll Agent()" : "no agent client";

        public static string Voice(string wavPath)
        {
            var v = Services.Get<VoiceClient>();
            if (v == null) return "no voice client";
            v.SendFile(wavPath);
            return "sent; poll Agent()";
        }

        public static string Agent()
        {
            var a = Services.Get<AgentClient>();
            var b = Services.Get<AirTools.Parts.PartsBrowser>();
            if (a == null) return "no agent client";
            return $"busy={a.Busy} transcript=\"{a.LastTranscript}\" reply=\"{a.LastReply}\" actions=[{string.Join("; ", a.LastActions)}] err={a.LastError} " +
                   $"| last action {AgentActions.LastResult} | browser \"{b?.Status}\" candidates={b?.Candidates.Count}";
        }

        public static string Overlay(bool on) => AppCommands.ShowStructure(on) ? $"structure overlay {(on ? "on" : "off")}" : "no overlay";

        public static string Snapping(bool on) { AppCommands.SetSnapping(on); return $"snapping {(on ? "on" : "off")}"; }

        /// Point the app at another server for this session (e.g. the mock on :8001); null/"" = back to the default.
        public static string Server(string url) { ServerConfig.SetOverride(url); return ServerConfig.Current; }

        public static string LastNote() => Notebook.Last == null ? "empty" : $"#{Notebook.Last.Id} {Notebook.Last.Tool} {Notebook.Last.Label}";
    }
}
