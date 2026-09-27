using System;
using System.Collections.Generic;
using System.Linq;
using AirTools.Core;
using AirTools.Input;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Agent
{
    /// "The agent measures for you" (B1 hand-off §2–§4, brain.md S1). survey: every structure-layer object with the
    /// label (and the `where` filter) is taped with the REAL MeasureTool, one object after another so you watch the
    /// tapes appear — 4 corners each, clicked through SnapService like a user's ray (the same snap policy and depth
    /// gate), then the shape is finished: one notebook entry per object. A corner that doesn't snap within 2 cm of the
    /// object's corner is placed raw and the object is marked unverified (amber). A pinch or stop_survey aborts:
    /// completed entries stay, the half-done object is dropped. One Undo removes the whole survey. Exactly one
    /// POST /agent/observe per request_id: done, aborted (with planned) or no_structure. check_slope: a 2-point tape
    /// along the chosen structure edge → slope_result.
    public class SurveyRunner : MonoBehaviour, ISiteScoped   // sitescope: ISiteScoped
    {
        public MeasureTool tool;
        public SceneRoot root;
        [Tooltip("Seconds per placed point; fastStep above fastAbove objects.")]
        public float step = 0.12f;
        public float fastStep = 0.06f;
        public int fastAbove = 12;
        [Tooltip("A corner snapped farther than this (scene metres) from the object's corner counts as unverified.")]
        public float snapTolerance = 0.02f;

        public bool Running { get; private set; }
        public string RequestId { get; private set; }
        public string Label { get; private set; }
        public string Where { get; private set; }
        public string Measure { get; private set; }
        public int Planned => m_Targets.Count;
        public int Completed => Results.Count;
        public List<SurveyResult> Results { get; } = new List<SurveyResult>();
        public List<SurveySkip> Skipped { get; } = new List<SurveySkip>();
        public IReadOnlyList<SurveyTarget> Targets => m_Targets;
        /// done | aborted | no_structure | refused ("" while running or before the first run).
        public string LastStatus { get; private set; } = "";
        /// The last /agent/observe body (sent or, in edit mode, only built).
        public JObject LastReport { get; private set; }
        /// Reports built (one per request id).
        public int Reports { get; private set; }
        public event Action<JObject> Reported;
        /// Seconds the last survey took (start → report).
        public float LastSeconds { get; private set; }

        readonly List<SurveyTarget> m_Targets = new List<SurveyTarget>();
        readonly HashSet<string> m_Reported = new HashSet<string>();
        readonly List<string> m_Snaps = new List<string>();
        int m_Target, m_Corner;
        bool m_Unverified;
        float m_Clock, m_StartedAt, m_Step;
        bool m_PrevArea;
        IToolInput m_Input;
        MeasureLabel m_Progress;
        static int s_LocalIds;

        void OnEnable() { Services.Register(this); SiteScope.Register(this, SiteScope.OrderSurvey); }   // sitescope: Register
        void OnDisable() { Services.Unregister(this); SiteScope.Unregister(this); if (Running) Abort(); }   // sitescope: Unregister

        /// sitescope: a survey in progress stops when its site is left (its targets are that scan's structure); the
        /// objects it measured are tapes, parked with the site by MeasureTool.
        public void SwitchSite(string from, string to, float factor) { if (Running) Abort(); }
        public void ClearParked() { }
        public int ParkedCount => 0;

        MeasureTool Tool => tool != null ? tool : Services.Get<MeasureTool>();
        SceneRoot Root => root != null ? root : Services.Get<SceneRoot>();

        /// The wearer's eye and gaze (world): the main camera, else the rig at eye height.
        public static Pose HeadPose()
        {
            var cam = Camera.main;
            if (cam != null) return new Pose(cam.transform.position, cam.transform.rotation);
            return new Pose(Vector3.up * 1.6f, Quaternion.identity);
        }

        // ---------------- survey ----------------

        /// Start a survey (the agent's survey action). False when it couldn't start (it has already reported why).
        public bool Run(string label, string where, string measure, string requestId, Pose? head = null)
        {
            if (Running) Abort();
            RequestId = string.IsNullOrEmpty(requestId) ? $"sv-app-{++s_LocalIds}" : requestId;
            Label = string.IsNullOrEmpty(label) ? "any" : label;
            Where = string.IsNullOrEmpty(where) ? "all" : where;
            Measure = string.IsNullOrEmpty(measure) ? "size" : measure;
            Results.Clear(); Skipped.Clear(); m_Targets.Clear();
            m_Target = 0; m_Corner = 0; m_Unverified = false; m_Snaps.Clear();
            m_StartedAt = Time.realtimeSinceStartup;
            LastStatus = "";
            var t = Tool; var r = Root;
            if (t == null || r == null || r.Structure == null || r.StructureSpace == null || r.Structure.Objects.Length == 0)
            {
                Log.Info($"Survey {RequestId}: no structure layer");
                UiToast.Show("No structure layer here · mark the corners yourself", ColorRole.Warning);
                Report("no_structure");
                return false;
            }
            var pose = head ?? HeadPose();
            var frustum = Camera.main != null ? GeometryUtility.CalculateFrustumPlanes(Camera.main) : null;
            m_Targets.AddRange(SurveyPlanner.Plan(r, Label, Where, pose, frustum));
            if (m_Targets.Count == 0) { Log.Info($"Survey {RequestId}: no {Label} ({Where})"); Report("done"); return true; }
            // The user's own shape in progress: finish a tape/shape (2+ points); refuse over a single point, or a shape
            // whose outline crosses itself (its finish was refused: the points stay for the user's Undo).
            if (t.Session.Count >= MeasureMath.MinPoints) t.Finish();
            if (t.Session.Count > 0)
            {
                UiToast.Show(t.Session.Count == 1 ? "Finish your shape first" : MeasureTool.CrossHint, ColorRole.Warning);
                Log.Info($"Survey {RequestId}: refused, the user's shape is unfinished ({t.Session.Count} point(s))");
                Report("aborted");
                LastStatus = "refused";
                return false;
            }
            if (Services.TryGet<ToolManager>(out var tm) && tm.measure == t) AppCommands.EquipTool("measure");
            else t.Equip(true);
            m_PrevArea = t.AreaMode;
            t.AreaMode = true;          // D4 auto-save must not finish a door at its 2nd corner
            t.AgentDriving = true;
            m_Input = Services.TryGet<IToolInput>(out var input) ? input : null;
            if (m_Input != null) m_Input.PressStart += OnUserPress;
            m_Step = m_Targets.Count > fastAbove ? fastStep : step;
            m_Clock = 0f;
            Running = true;
            Log.Info($"Survey {RequestId}: {m_Targets.Count} × {Label} ({Where}, {Measure}) — {string.Join(" ", m_Targets.Select(x => x.Id))}");
            UiToast.Show($"Surveying {m_Targets.Count} {Copy.SurveyNoun(Label, m_Targets.Count)} · pinch to stop", ColorRole.Info);
            ShowProgress();
            return true;
        }

        /// A pinch (or trigger) anywhere while the agent works stops it.
        void OnUserPress(ToolHand hand, Pose pose)
        {
            if (!Running) return;
            Log.Info($"Survey {RequestId}: stopped by a pinch");
            Abort();
        }

        /// stop_survey / a pinch: completed entries stay, the half-done object's points go; reports "aborted".
        public void Abort()
        {
            if (!Running) return;
            Tool?.CancelSession();
            End();
            UiToast.Show($"Stopped · {Results.Count} of {m_Targets.Count} measured", ColorRole.Info);
            Report("aborted");
        }

        void Update()
        {
            if (!Running) return;
            m_Clock += Time.unscaledDeltaTime;
            int guard = 0;
            while (Running && m_Clock >= m_Step && guard++ < 8) { m_Clock -= m_Step; Step(); }
        }

        /// One point (tests drive it directly). The 4th corner finishes the object. False once the survey is over.
        public bool Step()
        {
            if (!Running) return false;
            var t = Tool;
            if (t == null) { Abort(); return false; }
            var target = m_Targets[m_Target];
            if (m_Corner == 0 && TooSmall(target, t))
            {
                Skipped.Add(new SurveySkip { id = target.Id, reason = "too small to tape" });
                return Next();
            }
            PlaceCorner(t, target, m_Corner++);
            ShowProgress();
            if (m_Corner < 4) return true;
            var tag = new SurveyTag { RequestId = RequestId, ObjectId = target.Id, Label = target.Label, Unverified = m_Unverified };
            var shape = t.Session.Count >= 3 ? t.Finish(tag) : null;
            if (shape == null)
            {
                bool crossed = t.LastAction == MeasureTool.CrossRefused;
                t.CancelSession();
                Skipped.Add(new SurveySkip { id = target.Id, reason = crossed ? "its corners cross" : "couldn't place its corners" });
            }
            else Results.Add(ResultFor(shape, target, m_Snaps));
            return Next();
        }

        bool Next()
        {
            m_Target++; m_Corner = 0; m_Unverified = false; m_Snaps.Clear();
            if (m_Target < m_Targets.Count) return true;
            End();
            int amber = Results.Count(x => x.unverified);
            Log.Info($"Survey {RequestId}: done, {Results.Count}/{m_Targets.Count} measured, {amber} unverified, {Skipped.Count} skipped in {Time.realtimeSinceStartup - m_StartedAt:0.0} s");
            Report("done");
            return false;
        }

        /// Run the whole survey now (edit mode tests; no animation).
        public void RunToEnd(int maxSteps = 10000) { while (Running && maxSteps-- > 0) Step(); }

        void End()
        {
            Running = false;
            var t = Tool;
            if (t != null) { t.AgentDriving = false; t.AreaMode = m_PrevArea; }
            if (m_Input != null) { m_Input.PressStart -= OnUserPress; m_Input = null; }
            if (m_Progress != null) m_Progress.gameObject.SetActive(false);
        }

        bool TooSmall(SurveyTarget target, MeasureTool t)
        {
            float min = t.finishRadius * 2f * WorldScale;
            for (int i = 0; i < 4; i++) if (Vector3.Distance(target.Corners[i], target.Corners[(i + 1) % 4]) < min) return true;
            return false;
        }

        float WorldScale => Root != null ? Mathf.Max(Root.transform.lossyScale.x, 1e-4f) : 1f;

        /// Snap a ray from the approach point at the corner; farther than 2 cm off (or no hit): the raw corner, unverified.
        void PlaceCorner(MeasureTool t, SurveyTarget target, int i)
        {
            var hit = SnapAt(target.Approach, target.Corners[i], target.Normal, out bool snapped);
            if (!snapped) m_Unverified = true;
            m_Snaps.Add(snapped ? SurveyReport.SnapName(hit.kind) : "raw");
            t.Click(hit);
        }

        public SurfaceHit SnapAt(Vector3 from, Vector3 point, Vector3 normal, out bool snapped)
        {
            var d = point - from;
            float dist = d.magnitude;
            snapped = false;
            if (dist > 1e-4f && SnapService.TryRaySnap(new Ray(from, d / dist), out var hit, dist + 1f)
                && Vector3.Distance(hit.point, point) <= snapTolerance * WorldScale)
            {
                snapped = true;
                return hit;
            }
            return new SurfaceHit { point = point, rawPoint = point, normal = normal, kind = SnapKind.None };
        }

        SurveyResult ResultFor(MeasureShape shape, SurveyTarget target, List<string> snaps)
        {
            var m = shape.Measurement;
            return new SurveyResult
            {
                id = target.Id, label = target.Label, group = target.Group,
                w_m = shape.Survey.W, h_m = shape.Survey.H, area_m2 = m.Area, off_plane_m = m.PlanarityError,
                angles_deg = m.Angles.ToArray(), snap = snaps.ToArray(), unverified = shape.Survey.Unverified,
                notebook_id = shape.Entry != null ? shape.Entry.Id : -1,
                camera_id = shape.Entry != null ? shape.Entry.NearestCameraId : -1,
            };
        }

        void ShowProgress()
        {
            var t = Tool;
            if (!Running || t == null || !Application.isPlaying || m_Target >= m_Targets.Count) return;
            if (m_Progress == null)
            {
                if (t.viewRoot == null) return;
                m_Progress = MeasureLabel.Create(t.viewRoot, t.style, "SurveyProgress");
                m_Progress.tabular = true;
                m_Progress.Priority = 3;
            }
            m_Progress.gameObject.SetActive(true);
            var target = m_Targets[m_Target];
            m_Progress.SetAnchorWorld(target.Centre + target.Normal * 0.12f * WorldScale + Vector3.up * 0.08f * WorldScale);
            m_Progress.Set($"Survey {Results.Count + Skipped.Count}/{m_Targets.Count}", 1f);
        }

        /// Exactly one report per request id (hand-off §3).
        void Report(string status)
        {
            if (string.IsNullOrEmpty(status)) return;
            LastStatus = status;
            LastSeconds = Time.realtimeSinceStartup - m_StartedAt;
            if (!m_Reported.Add(RequestId)) return;
            var body = SurveyReport.Survey(SessionInfo.Id, RequestId, status, m_Targets.Count,
                status == "no_structure" ? new List<SurveyResult>() : Results, Skipped, Label, Measure);
            Send(body);
        }

        void Send(JObject body)
        {
            LastReport = body;
            Reports++;
            Reported?.Invoke(body);
            if (Services.TryGet<AgentClient>(out var agent)) agent.Observe(body);
        }

        // ---------------- check_slope ----------------

        public string LastSlopeEdge { get; private set; }
        public MeasureShape LastSlopeShape { get; private set; }

        /// check_slope {target, request_id, edge_ids}: tape the chosen structure edge end to end (a 2-point tape),
        /// fall = |Δy| × 1000, run = horizontal length; report slope_result. False when there is no edge to tape.
        public bool CheckSlope(string target, string requestId, IList<string> edgeIds, Pose? head = null)
        {
            if (Running) Abort();
            var t = Tool; var r = Root;
            string id = string.IsNullOrEmpty(requestId) ? $"sl-app-{++s_LocalIds}" : requestId;
            target = string.IsNullOrEmpty(target) ? "gutter" : target;
            if (t == null || r == null || r.Structure == null || r.StructureSpace == null)
            {
                UiToast.Show("No structure layer here · tape it yourself", ColorRole.Warning);
                Log.Info($"Slope {id}: no structure layer");
                return false;
            }
            var pose = head ?? HeadPose();
            int e = SlopeProbe.PickEdge(r, target, edgeIds, pose);
            if (e < 0)
            {
                UiToast.Show($"No {target} edge found · tape it yourself", ColorRole.Warning);
                Log.Info($"Slope {id}: no edge for {target}");
                return false;
            }
            var edge = r.Structure.Edges[e];
            LastSlopeEdge = edge.id;
            var a = r.StructureSpace.TransformPoint(edge.a);
            var b = r.StructureSpace.TransformPoint(edge.b);
            if (t.Session.Count >= MeasureMath.MinPoints) t.Finish();
            if (t.Session.Count >= MeasureMath.MinPoints)
            {
                // The user's shape crosses itself (its finish was refused): leave their points for Undo, don't tape.
                UiToast.Show(MeasureTool.CrossHint, ColorRole.Warning);
                Log.Info($"Slope {id}: refused, the user's shape crosses itself");
                return false;
            }
            t.CancelSession();
            bool prevArea = t.AreaMode;
            t.AreaMode = true;
            var ha = SnapAt(SlopeProbe.Approach(a, pose.position, WorldScale), a, Vector3.up, out bool sa);
            var hb = SnapAt(SlopeProbe.Approach(b, pose.position, WorldScale), b, Vector3.up, out bool sb);
            t.Click(ha);
            t.Click(hb);
            var shape = t.Finish(new SurveyTag { RequestId = id, ObjectId = edge.id, Label = $"{target} slope", Tape = true, Unverified = !(sa && sb) });
            t.AreaMode = prevArea;
            LastSlopeShape = shape;
            if (shape == null) { Log.Warn($"Slope {id}: the tape didn't take"); return false; }
            var s = SlopeProbe.Measure(shape.Points[0], shape.Points[1]);
            double? gravity = r.Manifest?.gravity_residual_deg;
            Log.Info($"Slope {id}: {target} edge {edge.id} run {s.runM:0.000} m, fall {s.fallMm:0.0} mm (snapped {sa}/{sb}), low end {s.lowEnd}");
            if (!m_Reported.Add(id)) return true;
            Send(SurveyReport.Slope(SessionInfo.Id, id, target, s.runM, s.fallMm, s.lowEnd, shape.Entry != null ? shape.Entry.Id : -1, gravity));
            return true;
        }

        // ---------------- measure_edges (structure measure) ----------------

        public MeasureShape LastMeasureShape { get; private set; }
        public string LastMeasure { get; private set; } = "";

        /// measure_edges: tape each structure-layer segment a → b (or, without segments, each named edge) with the real
        /// tape, snapped to the scan like check_slope's, one notebook entry each titled `label`, one undo step for all
        /// (same request id). The number is the tape's (toast + notebook); nothing is reported back. Returns how many took.
        public int MeasureSegments(AirTools.Agent.MeasureEdgesActions.Args args, Pose? head = null)
        {
            if (args == null) return 0;
            if (Running) Abort();
            var t = Tool; var r = Root;
            if (t == null || r == null || r.StructureSpace == null)
            {
                UiToast.Show("No scan to measure on · tape it yourself", ColorRole.Warning);
                LastMeasure = "no scan";
                return 0;
            }
            string id = string.IsNullOrEmpty(args.RequestId) ? $"me-app-{++s_LocalIds}" : args.RequestId;
            var segs = new List<(Vector3 a, Vector3 b, string edge)>();
            for (int i = 0; i < args.Count; i++) segs.Add((args.A[i], args.B[i], null));
            if (segs.Count == 0 && r.Structure != null)
                foreach (var e in r.Structure.Edges)
                    if (args.EdgeIds.Contains(e.id)) segs.Add((e.a, e.b, e.id));
            if (segs.Count == 0) { UiToast.Show("Nothing to tape there · tape it yourself", ColorRole.Warning); LastMeasure = "no segments"; return 0; }
            if (t.Session.Count >= MeasureMath.MinPoints) t.Finish();
            if (t.Session.Count >= MeasureMath.MinPoints)
            {
                UiToast.Show(MeasureTool.CrossHint, ColorRole.Warning);   // the user's crossing shape stays for Undo
                LastMeasure = "refused: the user's shape crosses itself";
                return 0;
            }
            t.CancelSession();
            var pose = head ?? HeadPose();
            bool prevArea = t.AreaMode;
            t.AreaMode = true;
            int taped = 0;
            double total = 0;
            foreach (var (sa, sb, edge) in segs)
            {
                var a = r.StructureSpace.TransformPoint(sa);
                var b = r.StructureSpace.TransformPoint(sb);
                var ha = SnapAt(SlopeProbe.Approach(a, pose.position, WorldScale), a, Vector3.up, out bool snapA);
                var hb = SnapAt(SlopeProbe.Approach(b, pose.position, WorldScale), b, Vector3.up, out bool snapB);
                t.Click(ha);
                t.Click(hb);
                var shape = t.Finish(new SurveyTag { RequestId = id, ObjectId = edge, Label = args.Label, Tape = true, Unverified = !(snapA && snapB) });
                if (shape == null) { t.CancelSession(); continue; }
                LastMeasureShape = shape;
                total = shape.Measurement.Distance;
                taped++;
            }
            t.AreaMode = prevArea;
            LastMeasure = $"{args.Label}: {taped} of {segs.Count} taped, last {total:0.000} m";
            Log.Info($"Measure edges {id}: {LastMeasure}");
            if (taped > 0) UiToast.Show($"{args.Label} · {Units.Format(total)}", ColorRole.Success);
            return taped;
        }
    }
}
