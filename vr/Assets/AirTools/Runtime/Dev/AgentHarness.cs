#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Globalization;
using System.Linq;
using System.Text;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using UnityEngine;

namespace AirTools.Dev
{
    /// Agent-facing control surface for the running app (Editor Play mode / development builds).
    /// Call from the Unity CLI, e.g.  unity command eval --code 'return AirTools.Dev.AgentHarness.RunM2();'
    /// Everything goes through the same ToolInputHub → tool path that controller trigger / hand pinch uses; only the
    /// pointer ray is injected. Results are logged as [AirTools.Check] lines (SPEC §7) and returned as text.
    public static partial class AgentHarness   // catalog: partial (the Catalog's checks are AgentHarness.Catalog.cs)
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        static ToolInputHub Hub => Services.Get<ToolInputHub>();
        static MeasureTool Measure => Services.Get<MeasureTool>();
        static ToolManager Tools => Services.Get<ToolManager>();
        static SceneRoot Root => Services.Get<SceneRoot>();

        public static string Status()
        {
            var sb = new StringBuilder();
            sb.Append($"mode={AppState.Mode} tool={Tools?.Active.ToString() ?? "n/a"}");
            var m = Measure;
            if (m != null)
            {
                sb.Append($" session={m.Session.Count}pts shapes={m.Shapes.Count} last=\"{m.LastAction}\"");
                if (m.Cursor.HasValue) sb.Append($" cursor={F(ToRoot(m.Cursor.Value.point))}({m.Cursor.Value.kind})");
            }
            sb.Append($" notebook={Notebook.Entries.Count}");
            if (Notebook.Last != null) sb.Append($" lastEntry=\"#{Notebook.Last.Id} {Notebook.Last.Label}\" cam={Notebook.Last.NearestCameraId}");
            return sb.ToString();
        }

        public static string OpenWorld() { AppCommands.OpenChest(); return Status(); }
        public static string ClosePassthrough() { AppCommands.CloseChest(); return Status(); }
        public static string Equip(string tool) { AppCommands.EquipTool(tool); return Status(); }

        /// Click at a scene-root-space target with the ray starting at the current head position.
        public static string Click(float x, float y, float z)
        {
            var cam = Camera.main;
            var origin = cam != null ? cam.transform.position : Vector3.zero;
            return ClickFromWorld(origin, ToWorld(new Vector3(x, y, z)));
        }

        /// Click with an explicit ray origin (scene-root space), e.g. to "stand on a ladder".
        public static string ClickFrom(float ox, float oy, float oz, float x, float y, float z) =>
            ClickFromWorld(ToWorld(new Vector3(ox, oy, oz)), ToWorld(new Vector3(x, y, z)));

        static string ClickFromWorld(Vector3 origin, Vector3 target)
        {
            var hub = Hub;
            if (hub == null) return "no ToolInputHub";
            var pose = ToolInputHub.RayPose(origin, target);
            hub.SetPointerOverride(ToolHand.Right, pose);
            hub.RaisePressStart(ToolHand.Right, pose);
            hub.RaisePressEnd(ToolHand.Right, pose);
            hub.SetPointerOverride(ToolHand.Right, null);
            var m = Measure;
            string placed = m != null && m.LastPlaced.HasValue ? $" placed={F(ToRoot(m.LastPlaced.Value.point))}({m.LastPlaced.Value.kind})" : "";
            return $"{m?.LastAction}{placed} | {Status()}";
        }

        /// Point the injected ray at a target and leave it there (live preview / cursor), without clicking.
        public static string Aim(float x, float y, float z)
        {
            var cam = Camera.main;
            var hub = Hub;
            if (hub == null || cam == null) return "no hub/camera";
            hub.SetPointerOverride(ToolHand.Right, ToolInputHub.RayPose(cam.transform.position, ToWorld(new Vector3(x, y, z))));
            return "aiming (call Release() to hand the pointer back to the controller)";
        }

        public static string Release() { Hub?.ClearOverrides(); return "pointer released"; }

        /// Drag: press on a scene-space point, move to another, release.
        public static string Drag(float fx, float fy, float fz, float tx, float ty, float tz)
        {
            var hub = Hub; var cam = Camera.main;
            if (hub == null || cam == null) return "no hub/camera";
            var origin = cam.transform.position;
            var a = ToolInputHub.RayPose(origin, ToWorld(new Vector3(fx, fy, fz)));
            var b = ToolInputHub.RayPose(origin, ToWorld(new Vector3(tx, ty, tz)));
            hub.SetPointerOverride(ToolHand.Right, a);
            hub.RaisePressStart(ToolHand.Right, a);
            for (int i = 1; i <= 5; i++)
            {
                var mid = ToolInputHub.RayPose(origin, Vector3.Lerp(a.position + a.forward * 10f, b.position + b.forward * 10f, i / 5f));
                hub.SetPointerOverride(ToolHand.Right, mid);
                hub.RaisePressMove(ToolHand.Right, mid);
            }
            hub.RaisePressEnd(ToolHand.Right, b);
            hub.SetPointerOverride(ToolHand.Right, null);
            return $"{Measure?.LastAction} | {Status()}";
        }

        public static string Finish() { Hub?.RaiseButton(ToolHand.Right, ToolButton.Finish); return $"{Measure?.LastAction} | {Status()}"; }
        public static string Undo() { Hub?.RaiseButton(ToolHand.Right, ToolButton.Undo); return $"{Measure?.LastAction} | {Status()}"; }
        public static string Clear() { Hub?.RaiseButton(ToolHand.Right, ToolButton.Clear); return Status(); }

        /// Text of every label on the last finished shape (what the user reads).
        public static string Labels()
        {
            var m = Measure;
            if (m == null || m.Shapes.Count == 0) return "no shapes";
            var v = m.Shapes[m.Shapes.Count - 1].View;
            return string.Join(" | ", v.Labels.Take(v.ActiveLabelCount).Select(l => l.Text.Replace("\n", " ")));
        }

        /// Runs every M2 ground-truth scenario `seeds` times through the live app and logs one check line each.
        /// Opens the world and equips the tool if needed. keepLast leaves the last shape of each scenario drawn.
        /// near: the UX W1.2 variant (each point from 0.6–2 m with ±1° aim noise, MeasureScenarios.M2Near).
        public static string RunM2(int seeds = 5, bool keepLast = true, bool near = false)
        {
            var tool = Measure; var hub = Hub; var root = Root;
            if (tool == null || hub == null || root == null) return "missing MeasureTool / ToolInputHub / SceneRoot";
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            // Colliders must be live even if the fade hasn't reached the swap yet.
            root.SetVisible(true);
            Physics.SyncTransforms();
            if (Tools != null && Tools.Active != ToolKind.Measure) Tools.Equip(ToolKind.Measure);

            var report = new StringBuilder();
            int pass = 0, total = 0;
            foreach (var s in near ? MeasureScenarios.M2Near() : MeasureScenarios.M2())
            {
                int sPass = 0;
                for (int seed = 1; seed <= seeds; seed++)
                {
                    bool last = seed == seeds;
                    var r = MeasureScenarios.Run(s, tool, hub, root.transform, seed, removeAfter: !(keepLast && last));
                    total++;
                    if (r.Passed) { pass++; sPass++; }
                    Log.Check(s.Id, r.Passed, $"seed={seed} {r.Detail}");
                    if (!r.Passed) report.AppendLine(r.ToString());
                }
                report.Insert(0, $"{s.Id}: {sPass}/{seeds}\n");
            }
            string summary = $"M2{(near ? " near" : "")} harness: {pass}/{total} passed";
            Log.Check(near ? "M2.near.harness.summary" : "M2.harness.summary", pass == total, $"passed={pass} total={total}");
            return summary + "\n" + report;
        }

        // ---------------- concave polygons ----------------

        /// Concave Area-mode shapes through the real input path, on the built-in facade's open wall (load it first:
        /// BackendHarness.LoadSite("built-in")). From the spawn eye: an L (1 × 1 m minus a 0.5 m square) closed on its
        /// first point → 0.75 m², one 270° corner, 13 labels; then a square's corners in bow-tie order plus a point inside
        /// → the finish is refused and the 5 points stay; Undo takes the inside point off → the 4 corners reorder to a
        /// 1 m² square. keepL: removes the square again and leaves the L drawn for a capture.
        public static string RunConcave(bool keepL = true)
        {
            var tool = Measure; var hub = Hub; var root = Root;
            if (tool == null || hub == null || root == null) return "missing MeasureTool / ToolInputHub / SceneRoot";
            if (root.IsRuntimePackage) return "a scene package is loaded: BackendHarness.LoadSite(\"built-in\") first (the L is on the facade wall)";
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            root.SetVisible(true);
            Physics.SyncTransforms();
            AppCommands.EquipTool("area");
            tool.CancelSession();
            var eye = ToWorld(MeasureScenarios.SpawnEye);
            string Click(float x, float y) => ClickFromWorld(eye, ToWorld(new Vector3(x, y, 0f)));
            string Angles(PointMeasurement m) => string.Join("/", m.Angles.Select(a => a.ToString("0.0", C)));
            var report = new StringBuilder();
            int pass = 0;

            // 1. The L, closed on its first point.
            int before = tool.Shapes.Count;
            foreach (var (x, y) in new[] { (0.5f, 1.0f), (1.5f, 1.0f), (1.5f, 1.5f), (1.0f, 1.5f), (1.0f, 2.0f), (0.5f, 2.0f) }) Click(x, y);
            Click(0.5f, 1.0f);
            bool lSaved = tool.Shapes.Count == before + 1;
            var l = lSaved ? tool.Shapes[tool.Shapes.Count - 1] : null;
            bool lOk = lSaved && System.Math.Abs(l.Measurement.Area - 0.75) <= 0.01 && l.Measurement.Angles.Length == 6
                       && l.Measurement.Angles.Count(a => System.Math.Abs(a - 270) <= 1) == 1 && l.Measurement.Angles.Count(a => System.Math.Abs(a - 90) <= 1) == 5
                       && l.View != null && l.View.ActiveLabelCount == 13;
            string lDetail = lSaved
                ? $"area={l.Measurement.Area.ToString("0.0000", C)} angles={Angles(l.Measurement)} perimeter={l.Measurement.Perimeter.ToString("0.000", C)} labels={l.View?.ActiveLabelCount} entry=\"#{l.Entry?.Id} {l.Entry?.Label}\""
                : $"not saved ({tool.LastAction})";
            Log.Check("concave.L.wall", lOk, lDetail);
            report.AppendLine($"{(lOk ? "PASS" : "FAIL")} L: {lDetail}");
            if (lOk) pass++;

            // 2. A bow-tie with a point inside: refused, points kept.
            int shapes = tool.Shapes.Count;
            foreach (var (x, y) in new[] { (0.5f, 1.0f), (1.5f, 2.0f), (1.5f, 1.0f), (0.5f, 2.0f), (1.0f, 1.2f) }) Click(x, y);
            hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            bool refused = tool.Shapes.Count == shapes && tool.Session.Count == 5 && tool.LastAction == MeasureTool.CrossRefused;
            string rDetail = $"session={tool.Session.Count} shapes={tool.Shapes.Count - shapes} last=\"{tool.LastAction}\" hint=\"{AirTools.UI.InputHints.Last}\"";
            Log.Check("concave.bowtie.refused", refused, rDetail);
            report.AppendLine($"{(refused ? "PASS" : "FAIL")} bow-tie refused: {rDetail}");
            if (refused) pass++;

            // 3. Undo the inside point: four hull corners, reordered and saved.
            hub.RaiseButton(ToolHand.Right, ToolButton.Undo);
            hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            bool fixedOk = tool.Shapes.Count == shapes + 1 && tool.Shapes[tool.Shapes.Count - 1].Measurement.Reordered
                           && System.Math.Abs(tool.Shapes[tool.Shapes.Count - 1].Measurement.Area - 1.0) <= 0.01;
            string fDetail = tool.Shapes.Count == shapes + 1
                ? $"area={tool.Shapes[tool.Shapes.Count - 1].Measurement.Area.ToString("0.0000", C)} reordered={tool.Shapes[tool.Shapes.Count - 1].Measurement.Reordered} angles={Angles(tool.Shapes[tool.Shapes.Count - 1].Measurement)}"
                : $"not saved ({tool.LastAction}, session={tool.Session.Count})";
            Log.Check("concave.bowtie.undo", fixedOk, fDetail);
            report.AppendLine($"{(fixedOk ? "PASS" : "FAIL")} undo → square: {fDetail}");
            if (fixedOk) pass++;
            if (keepL && tool.Shapes.Count == shapes + 1) tool.Undo();
            tool.CancelSession();

            Log.Check("concave.harness.summary", pass == 3, $"passed={pass} total=3");
            return $"concave harness: {pass}/3 passed\n{report}";
        }

        // ---------------- UX W1.2: commit what you saw ----------------

        /// A/B switches: the pinch rewind + last seat (PointerHistory.Rewind) and the angular radii
        /// (AngularRadii.Enabled). Both off = the behaviour before W1.2.
        public static string W12(bool rewind = true, bool angular = true)
        {
            PointerHistory.Rewind = rewind;
            AngularRadii.Enabled = angular;
            return $"rewind={PointerHistory.Rewind} angular={AngularRadii.Enabled}";
        }

        /// A pinch fed through the hub's live path the way the controller / hand source feeds it (90 Hz frames, no
        /// waiting): the right ray aims from a scene-root origin at a target and pitches down dipDeg over the 80 ms
        /// before the press while the pinch strength rises; then a live press and a release at the dipped pose. The tool
        /// in hand (Measure, Level, or Part with a part held) commits it. Reports how far the committed point is from
        /// where the undipped and the dipped rays land (mm). Clears the right hand's aim override.
        public static string DipClick(float ox, float oy, float oz, float x, float y, float z, float dipDeg = 2f)
        {
            var hub = Hub;
            if (hub == null) return "no ToolInputHub";
            hub.SetPointerOverride(ToolHand.Right, null);
            var origin = ToWorld(new Vector3(ox, oy, oz));
            var look = Quaternion.LookRotation(ToWorld(new Vector3(x, y, z)) - origin);
            var undipped = new Pose(origin, look);
            var dipped = new Pose(origin, look * Quaternion.AngleAxis(dipDeg, Vector3.right));
            var m = Measure; var level = Level; var part = PartTool;
            bool isLevel = level != null && level.Equipped, isPart = part != null && part.Equipped && part.Held != null;
            Vector3? Lands(Pose p)
            {
                if (isPart) return PartPlacer.TryFind(part.Held.Spec, p, part.holdDistance, part.snapRadius, part.maxRayDistance, out var seat, out _) ? seat.point : (Vector3?)null;
                return SnapService.TryRaySnap(new Ray(p.position, p.forward), out var hit, 60f, features: !isLevel) ? hit.point : (Vector3?)null;
            }
            var u = Lands(undipped); var d = Lands(dipped);
            int points = m != null ? m.Session.Count : 0, levels = level != null ? level.Placements.Count : 0;
            int parts = part != null ? part.PlacedParts.Count : 0;

            var h = hub.History(ToolHand.Right);
            h.Clear();
            float t0 = Time.unscaledTime;
            for (int k = 39; k >= 0; k--)
            {
                float before = k / 90f, f = Mathf.Clamp01((0.08f - before) / 0.08f);
                hub.RecordLive(ToolHand.Right, t0 - before, new Pose(origin, look * Quaternion.AngleAxis(dipDeg * f, Vector3.right)), true, 0.55f * f);
            }
            hub.SetLivePointer(ToolHand.Right, dipped, true);
            hub.RaiseLivePressStart(ToolHand.Right, dipped);
            hub.RaisePressEnd(ToolHand.Right, dipped);
            h.Clear();   // the live source's next frame has an older timestamp than these

            Vector3? c = null; string what = "nothing committed";
            if (isPart && part.PlacedParts.Count > parts) { c = part.PlacedParts[part.PlacedParts.Count - 1].transform.position; what = "part"; }
            else if (isLevel && level.Placements.Count > levels)
            {
                var r = level.Placements[level.Placements.Count - 1].Reading.Point;
                c = level.frame != null ? level.frame.TransformPoint(r) : r; what = "level";
            }
            else if (m != null && m.Equipped && m.Session.Count > points && m.LastPlaced.HasValue) { c = m.LastPlaced.Value.point; what = "measure point"; }
            string Mm(Vector3? a) => c.HasValue && a.HasValue ? (Vector3.Distance(c.Value, a.Value) * 1000f).ToString("0.0", C) : "-";
            string result = c.HasValue
                ? $"{what} at {F(ToRoot(c.Value))}: {Mm(u)} mm from the undipped hit, {Mm(d)} mm from the dipped one"
                : $"{what} ({(isPart ? part.LastAction : isLevel ? level.LastAction : m?.LastAction)})";
            Log.Check("W12.dipclick", c.HasValue, $"dip={dipDeg.ToString("0.0", C)} rewind={PointerHistory.Rewind} angular={AngularRadii.Enabled} {result}");
            return $"{result} | rewind={PointerHistory.Rewind} angular={AngularRadii.Enabled}";
        }

        // ---------------- M3: level + notebook ----------------

        static LevelTool Level => Services.Get<LevelTool>();
        static NotebookController NotebookCtl => Services.Get<NotebookController>();
        static NotebookPanel Panel => Services.Get<NotebookPanel>();

        /// Entries, export status and last export paths.
        public static string Notes()
        {
            var sb = new StringBuilder($"entries={Notebook.Entries.Count}");
            foreach (var e in Notebook.Entries) sb.Append($" | #{e.Id} {e.Tool} {e.ValueSI.ToString("0.000", C)}{e.Unit} cam={e.NearestCameraId}");
            var nb = NotebookCtl;
            if (nb != null) sb.Append($" || export={nb.ExportStatus} csv={nb.LastExportCsv} html={nb.LastExportHtml}");
            return sb.ToString();
        }

        public static string ShowNotebook(bool open) { AppCommands.ShowNotebook(open); return PanelText(); }

        /// What the wrist panel currently displays.
        public static string PanelText()
        {
            var p = Panel;
            if (p == null) return "no panel";
            var sb = new StringBuilder($"open={p.IsOpen} title=\"{p.title?.text}\" status=\"{p.status?.text}\"");
            if (p.rows != null)
                foreach (var r in p.rows)
                    if (r.gameObject.activeSelf) sb.Append($" | {r.titleText?.text} / {r.detailText?.text}");
            return sb.ToString();
        }

        public static string ShowRow(int row) => Panel != null && Panel.Show(row) ? $"highlighted row {row}" : $"row {row}: nothing to show";
        public static string Export() => $"dir={AppCommands.ExportNotebook()} | {Notes()}";

        /// Runs every M3 check through the live app: level scenarios (seeds each), evidence-camera frustums for all
        /// entries, export files written + parsed. Upload (POST /notebook) completes asynchronously; check Notes().
        public static string RunM3(int seeds = 5)
        {
            var level = Level; var hub = Hub; var root = Root;
            if (level == null || hub == null || root == null) return "missing LevelTool / ToolInputHub / SceneRoot";
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            root.SetVisible(true);
            Physics.SyncTransforms();
            Tools?.Equip(ToolKind.Level);

            var report = new StringBuilder();
            int pass = 0, total = 0;
            foreach (var s in LevelScenarios.M3())
            {
                int sPass = 0;
                for (int seed = 1; seed <= seeds; seed++)
                {
                    var r = LevelScenarios.Run(s, level, hub, root.transform, seed, removeAfter: seed != seeds);
                    total++;
                    if (r.Passed) { pass++; sPass++; }
                    Log.Check(s.Id, r.Passed, $"seed={seed} {r.Detail}");
                    if (!r.Passed) report.AppendLine(r.ToString());
                }
                report.AppendLine($"{s.Id}: {sPass}/{seeds}");
            }

            // Evidence photos: every entry with points has a camera whose frustum contains them.
            var cams = root.CamerasInRootSpace();
            int withPoints = 0, seen = 0;
            foreach (var e in Notebook.Entries)
            {
                if (e.Points == null || e.Points.Length == 0) continue;
                withPoints++;
                var cam = System.Array.Find(cams, c => c.id == e.NearestCameraId);
                if (e.NearestCameraId >= 0 && CameraEvidence.ContainsAll(cam, e.Points)) seen++;
            }
            bool evidence = withPoints >= 3 && seen == withPoints;
            Log.Check("M3.notebook.evidence", evidence, $"entries_with_points={withPoints} camera_sees_points={seen}");
            report.AppendLine($"M3.notebook.evidence: {seen}/{withPoints} (need >= 3)");
            total++; if (evidence) pass++;

            // Export: files exist and parse.
            var dir = AppCommands.ExportNotebook();
            var nb = NotebookCtl;
            bool files = nb != null && System.IO.File.Exists(nb.LastExportCsv) && System.IO.File.Exists(nb.LastExportHtml);
            int csvRows = files ? NotebookExporter.ParseCsv(System.IO.File.ReadAllText(nb.LastExportCsv)).Count - 1 : -1;
            string html = files ? System.IO.File.ReadAllText(nb.LastExportHtml) : "";
            int htmlRows = files ? (html.Length - html.Replace("<tr class=\"entry\">", "").Length) / "<tr class=\"entry\">".Length : -1;
            bool export = files && csvRows == Notebook.Entries.Count && htmlRows == Notebook.Entries.Count;
            Log.Check("M3.notebook.export", export, $"dir={dir} csv_rows={csvRows} html_rows={htmlRows} entries={Notebook.Entries.Count}");
            report.AppendLine($"M3.notebook.export: {(export ? "ok" : "FAIL")} csv_rows={csvRows} html_rows={htmlRows} entries={Notebook.Entries.Count} dir={dir}");
            total++; if (export) pass++;

            string summary = $"M3 harness: {pass}/{total} passed (upload status: {nb?.ExportStatus} — recheck with Notes())";
            Log.Check("M3.harness.summary", pass == total, $"passed={pass} total={total}");
            return summary + "\n" + report;
        }

        // ---------------- P6/P7: ladder check + fall edges (presence.md S4) ----------------

        static LadderTool Ladders => Services.Get<LadderTool>();

        /// Scene-root point of the spawn eye (spawn + 1.6 m): where "from spawn" rays start.
        static Vector3 SpawnEye()
        {
            var root = Root;
            var spawn = SyntheticFacadeSpec.SpawnPosition;
            if (root != null && root.Package != null)
                spawn = (root.Content != null ? root.Content.transform.localRotation : Quaternion.identity) * root.Package.spawnPosition;
            return spawn + Vector3.up * 1.6f;
        }

        /// Pinch once along a ray from `origin` through `target` (scene-root space), through the hub like a trigger.
        static void PressAlong(Vector3 origin, Vector3 target)
        {
            var hub = Hub;
            var pose = ToolInputHub.RayPose(ToWorld(origin), ToWorld(target));
            hub.SetPointerOverride(ToolHand.Right, pose);
            hub.RaisePressStart(ToolHand.Right, pose);
            hub.RaisePressEnd(ToolHand.Right, pose);
            hub.SetPointerOverride(ToolHand.Right, null);
        }

        /// Place a ladder aimed from spawn at a scene point, e.g. Ladder(0, 6.2, 0.13) for the gutter / eave.
        /// Opens the world and equips the ladder tool if needed.
        public static string Ladder(float x, float y, float z)
        {
            var tool = Ladders;
            if (tool == null || Hub == null) return "no LadderTool / ToolInputHub";
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            Root?.SetVisible(true);
            Physics.SyncTransforms();
            if (Tools != null && Tools.Active != ToolKind.Ladder) Tools.Equip(ToolKind.Ladder);
            int before = tool.Placements.Count;
            PressAlong(SpawnEye(), new Vector3(x, y, z));
            bool placed = tool.Placements.Count == before + 1;
            Log.Check("S4.ladder.place", placed, $"target={F(new Vector3(x, y, z))} last=\"{tool.LastAction}\"");
            return LadderInfo();
        }

        /// Press on the last ladder's foot and drag it by (dx, dz) scene metres (only the distance out counts).
        public static string DragFoot(float dx, float dz)
        {
            var tool = Ladders; var hub = Hub;
            var p = tool?.Last;
            if (p == null || hub == null) return "no ladder placed";
            if (Tools != null && Tools.Active != ToolKind.Ladder) Tools.Equip(ToolKind.Ladder);
            var eye = SpawnEye();
            var from = p.Foot;
            var to = from + new Vector3(dx, 0f, dz);
            var a = ToolInputHub.RayPose(ToWorld(eye), ToWorld(from));
            hub.SetPointerOverride(ToolHand.Right, a);
            hub.RaisePressStart(ToolHand.Right, a);
            bool grabbed = tool.Dragging == p;
            for (int i = 1; i <= 5; i++)
            {
                var mid = ToolInputHub.RayPose(ToWorld(eye), ToWorld(Vector3.Lerp(from, to, i / 5f)));
                hub.SetPointerOverride(ToolHand.Right, mid);
                hub.RaisePressMove(ToolHand.Right, mid);
            }
            var b = ToolInputHub.RayPose(ToWorld(eye), ToWorld(to));
            hub.RaisePressEnd(ToolHand.Right, b);
            hub.SetPointerOverride(ToolHand.Right, null);
            Log.Check("S4.ladder.drag", grabbed, $"from={F(from)} to={F(to)} last=\"{tool.LastAction}\"");
            return LadderInfo();
        }

        /// Drag the last ladder's foot until it stands `metres` out from the support face, in one press (the top pivots
        /// out on a gutter lip as the ladder steepens, so the pointer is corrected a few times before release).
        public static string DragFootOut(float metres)
        {
            var tool = Ladders; var hub = Hub;
            var p = tool?.Last;
            if (p == null || hub == null) return "no ladder placed";
            if (Tools != null && Tools.Active != ToolKind.Ladder) Tools.Equip(ToolKind.Ladder);
            var eye = ToWorld(SpawnEye());
            var from = p.Foot;
            var last = ToolInputHub.RayPose(eye, ToWorld(from));
            hub.SetPointerOverride(ToolHand.Right, last);
            hub.RaisePressStart(ToolHand.Right, last);
            bool grabbed = tool.Dragging == p;
            var target = from;
            for (int i = 0; i < 4 && grabbed; i++)
            {
                target += p.Outward * (float)(metres - p.Solution.FootOut);
                last = ToolInputHub.RayPose(eye, ToWorld(target));
                hub.SetPointerOverride(ToolHand.Right, last);
                hub.RaisePressMove(ToolHand.Right, last);
            }
            hub.RaisePressEnd(ToolHand.Right, last);
            hub.SetPointerOverride(ToolHand.Right, null);
            Log.Check("S4.ladder.drag", grabbed && System.Math.Abs(p.Solution.FootOut - metres) <= 0.02,
                $"target_d={metres.ToString("0.000", C)} d={p.Solution.FootOut.ToString("0.000", C)} last=\"{tool.LastAction}\"");
            return LadderInfo();
        }

        /// The last ladder: "28 ft extension ladder · foot 1.60 m out · 75.5° · 0.91 m above the edge · Green | …".
        public static string LadderInfo()
        {
            var tool = Ladders;
            if (tool == null) return "no LadderTool";
            var p = tool.Last;
            if (p == null) return $"no ladder placed | last=\"{tool.LastAction}\" | {Status()}";
            var s = p.Solution; var c = p.Checks; var r = p.Report;
            string info = $"{p.Summary} | {LadderMath.VerdictLine(r)} | ratio={r.Ratio} h={s.Height.ToString("0.000", C)} d={s.FootOut.ToString("0.000", C)} " +
                          $"angle={s.AngleDeg.ToString("0.00", C)} Lw={s.ContactLength.ToString("0.000", C)} R={s.RailNeeded.ToString("0.000", C)}m " +
                          $"({(s.RailNeeded / LadderMath.MetresPerFoot).ToString("0.0", C)}ft) above={s.AboveEdge.ToString("0.000", C)} " +
                          $"support={s.Support}({p.SupportName}) edge={F(p.Edge)} top={F(p.Top)} foot={F(p.Foot)} ground={c.GroundFound} " +
                          $"footing={c.FootingDeg.ToString("0.0", C)}deg collision={(c.Collision ? c.CollisionWith : "none")} " +
                          $"ladders={tool.Placements.Count} tris={(p.View != null ? p.View.TriangleCount : 0)} notebook=\"#{p.Entry?.Id} {p.Entry?.Tool} {p.Entry?.ValueSI.ToString("0.000", C)}{p.Entry?.Unit}\"";
            Log.Check("S4.ladder.info", true, info);
            return info;
        }

        /// What the ladder card shows (open, title, verdict, detail, disclaimer).
        public static string LadderCardText()
        {
            var card = Services.Get<AirTools.Tools.LadderCard>();
            if (card == null) return "no LadderCard";
            string T(TMPro.TextMeshPro t) => t != null ? t.text.Replace("\n", " / ") : "-";
            return $"open={card.IsOpen} title=\"{T(card.title)}\" verdict=\"{T(card.verdict)}\" detail=\"{T(card.detail)}\" disclaimer=\"{T(card.disclaimer)}\" " +
                   $"find={(card.findButton != null ? card.findButton.Text : "-")} last_search=\"{card.LastSearch}\"";
        }

        /// Press *Find this ladder* on the card: the parts search for the sized ladder ("28 ft extension ladder").
        /// Results arrive asynchronously; read them with Parts().
        public static string FindThisLadder()
        {
            var card = Services.Get<AirTools.Tools.LadderCard>();
            if (card == null || card.Shown == null) return "no ladder on the card";
            card.Press(LadderCardAction.FindLadder);
            bool ok = card.LastSearch == LadderMath.SearchQuery(card.Shown.Solution.SizeFt);
            Log.Check("S4.ladder.find", ok, $"search=\"{card.LastSearch}\"");
            return $"searched \"{card.LastSearch}\" | {LadderCardText()}";
        }

        /// Turns the fall-edge overlay on and lists every candidate edge (name, height, drop, flagged). On the synthetic
        /// facade it checks S4: the fascia top (6.2 m) and the sill (3.5 m) flagged, the 1.0 m ledge not.
        public static string FallEdges()
        {
            var root = Root;
            if (root == null) return "no SceneRoot";
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            bool shown = AppCommands.ShowFallEdges(true);
            var edges = AirTools.Structure.SupportEdges.Edges(root);
            var overlay = Services.Get<AirTools.Structure.FallEdges>();
            var sb = new StringBuilder();
            int flagged = 0;
            foreach (var e in edges)
            {
                if (e.Flagged) flagged++;
                sb.Append($" | {e.Edge.Name} y={e.Edge.Mid.y.ToString("0.00", C)} len={e.Edge.Length.ToString("0.00", C)} drop={e.DropM.ToString("0.00", C)} " +
                          $"tilt={e.TiltDeg.ToString("0.0", C)} {(e.Flagged ? "FLAGGED" : "no")}");
            }
            string head = $"shown={shown && overlay != null && overlay.Visible} edges={edges.Count} flagged={flagged} " +
                          $"dashes={overlay?.DashCount ?? 0} labels={overlay?.LabelCount ?? 0}";
            if (AirTools.Structure.SupportEdges.IsSyntheticFacade(root))
            {
                bool fascia = edges.Any(e => e.Flagged && e.Edge.Name == "Fascia" && System.Math.Abs(e.DropM - SyntheticFacadeSpec.FasciaTop) < 0.01f);
                bool sill = edges.Any(e => e.Flagged && e.Edge.Name == "Sill" && System.Math.Abs(e.DropM - SyntheticFacadeSpec.SillHeight) < 0.01f);
                bool ledge = edges.Any(e => e.Flagged && e.Edge.Name.StartsWith("Ledge"));
                Log.Check("S4.fall_edges.facade", fascia && sill && !ledge && overlay != null && overlay.Visible,
                    $"fascia={fascia} sill={sill} ledge_flagged={ledge} {head}");
            }
            else Log.Check("S4.fall_edges", shown, head);
            return head + sb;
        }

        /// The S4 [A] Live script in one call (on the built-in synthetic facade; loads it if a scan is showing): equip,
        /// ladder on the eave (28 ft, green, 1.60 m out), its notebook entry,
        /// foot dragged to 0.5 m (red, 1 : 12), undo / redo, a sill ladder (16 ft), fall edges. Leaves the eave ladder
        /// standing, green, with the edges on (ready for a capture).
        public static string RunS4()
        {
            var tool = Ladders; var root = Root;
            if (tool == null || Hub == null || root == null) return "missing LadderTool / ToolInputHub / SceneRoot";
            var report = new StringBuilder();
            int pass = 0, total = 0;
            void Check(string id, bool ok, string detail)
            {
                total++; if (ok) pass++;
                Log.Check(id, ok, detail);
                report.AppendLine($"{id}: {(ok ? "ok" : "FAIL")} {detail}");
            }
            // The S4 numbers are the synthetic facade's: switch from a scanned site (the kitchen auto-loads) to it.
            if (!AirTools.Structure.SupportEdges.IsSyntheticFacade(root)) AppCommands.LoadSite("built-in");
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            root.SetVisible(true);
            Physics.SyncTransforms();
            Check("S4.scene", AirTools.Structure.SupportEdges.IsSyntheticFacade(root), $"site={root.Site} runtime={root.IsRuntimePackage}");
            AppCommands.EquipTool("ladder");
            Check("S4.equip", Tools != null && Tools.Active == ToolKind.Ladder, $"tool={Tools?.Active}");
            tool.ClearAll();

            Ladder(0f, 6.2f, 0.13f);
            var p = tool.Last;
            bool eave = p != null && p.Solution.SizeFt == 28 && p.Report.Verdict == LadderVerdict.Green
                        && System.Math.Abs(p.Solution.FootOut - 1.601) <= 0.02 && System.Math.Abs(p.Solution.AngleDeg - LadderMath.IdealAngleDeg) <= 0.3;
            Check("S4.ladder.eave", eave, p != null ? $"{p.Summary} d={p.Solution.FootOut.ToString("0.000", C)} contact={F(p.Top)}" : tool.LastAction);
            if (p == null) return report.ToString();
            var entry = Notebook.Last;
            Check("S4.ladder.notebook", entry != null && entry == p.Entry && entry.Tool == "ladder" && System.Math.Abs(entry.ValueSI - p.Solution.ContactLength) < 1e-6,
                $"entry=\"#{entry?.Id} {entry?.Tool} {entry?.ValueSI.ToString("0.000", C)}{entry?.Unit} {entry?.Label}\"");
            var card = Services.Get<AirTools.Tools.LadderCard>();
            Check("S4.ladder.card", card != null && card.IsOpen && card.Shown == p && card.title != null && card.title.text == "28 ft extension ladder"
                                     && card.disclaimer != null && card.disclaimer.text == LadderMath.Disclaimer, LadderCardText());
            Check("S4.ladder.view", p.View != null && p.View.gameObject.activeInHierarchy && p.View.TriangleCount > 0 && p.View.TriangleCount < 1000,
                $"tris={(p.View != null ? p.View.TriangleCount : 0)} label=\"{p.View?.LabelText?.Replace("\n", " / ")}\"");

            // Drag the foot to 0.5 m out: red, 1 : 12.
            DragFootOut(0.5f);
            Check("S4.ladder.drag_red", p.Report.Verdict == LadderVerdict.Red && p.Report.Ratio == "1 : 12" && System.Math.Abs(p.Solution.FootOut - 0.5) <= 0.02,
                $"d={p.Solution.FootOut.ToString("0.000", C)} {LadderMath.VerdictLine(p.Report)}");
            EditHistory.Undo();
            Check("S4.ladder.undo_move", p.Report.Verdict == LadderVerdict.Green && System.Math.Abs(p.Solution.FootOut - 1.601) <= 0.02,
                $"d={p.Solution.FootOut.ToString("0.000", C)} last=\"{tool.LastAction}\"");
            EditHistory.Redo();
            Check("S4.ladder.redo_move", System.Math.Abs(p.Solution.FootOut - 0.5) <= 0.02, $"d={p.Solution.FootOut.ToString("0.000", C)}");
            EditHistory.Undo();

            // A sill ladder for window work: no landing, 16 ft. Then undo it.
            Ladder(0.2f, SyntheticFacadeSpec.SillHeight + 0.05f, 0.03f);
            var sill = tool.Last;
            Check("S4.ladder.sill", sill != null && sill != p && sill.Solution.SizeFt == 16 && sill.Solution.Support == LadderSupport.Wall && sill.Report.Verdict == LadderVerdict.Green,
                sill != null ? sill.Summary : tool.LastAction);
            EditHistory.Undo();
            Check("S4.ladder.undo_place", tool.Placements.Count == 1 && tool.Last == p && !Notebook.Entries.Contains(sill?.Entry),
                $"ladders={tool.Placements.Count} last=\"{tool.LastAction}\"");

            string edges = FallEdges();
            bool facade = AirTools.Structure.SupportEdges.IsSyntheticFacade(root);
            var list = AirTools.Structure.SupportEdges.Edges(root);
            Check("S4.fall_edges", !facade || (list.Any(e => e.Flagged && e.Edge.Name == "Fascia") && list.Any(e => e.Flagged && e.Edge.Name == "Sill")
                                               && !list.Any(e => e.Flagged && e.Edge.Name.StartsWith("Ledge"))), edges);

            string summary = $"S4 harness: {pass}/{total} passed — {LadderInfo()}";
            Log.Check("S4.harness.summary", pass == total, $"passed={pass} total={total}");
            return summary + "\n" + report;
        }

        // ---------------- M4: parts in hand ----------------

        static PartTool PartTool => Services.Get<PartTool>();
        static PartsBrowser Browser => Services.Get<PartsBrowser>();
        static PartLoader Loader => Services.Get<PartLoader>();

        /// Crate menu / part tool state: search status, candidates, held and selected part.
        public static string Parts()
        {
            var sb = new StringBuilder();
            var b = Browser; var t = PartTool;
            if (b != null)
            {
                sb.Append($"browser: \"{b.Status}\" searching={b.Searching} loading={b.Loading} source={b.Source} candidates=[");
                sb.Append(string.Join(", ", b.Candidates.Select(c => $"{c.id} {PartFormat.Price(c.price_usd)}")));
                sb.Append("]\n");
            }
            if (t != null)
            {
                sb.Append($"tool: equipped={t.Equipped} held={(t.Held != null ? t.Held.Spec.id : "-")} onSurface={t.OnSurface} placed={t.PlacedParts.Count} last=\"{t.LastAction}\"\n");
                if (t.Selected != null) sb.Append(PartInfo());
            }
            return sb.ToString();
        }

        /// The selected part: source, pose, true size, back-face plane, fit, colour, callout.
        public static string PartInfo()
        {
            var p = PartTool?.Selected;
            if (p == null) return "no part selected";
            var size = p.MeasuredSizeMm();
            var pos = ToRoot(p.transform.position);
            var mount = Root != null ? Root.transform.InverseTransformDirection(p.WorldMountDirection) : p.WorldMountDirection;
            var col = p.MaterialColor();
            return $"part {p.Spec.id} source={p.Source} held={p.Held} placed={p.Placed} origin={F(pos)} mountDir={F(mount)} " +
                   $"size_mm=({size.x.ToString("0.0", C)}, {size.y.ToString("0.0", C)}, {size.z.ToString("0.0", C)}) scale={p.ModelScale.ToString("0.####", C)} " +
                   $"finish={p.FinishName} color={(col.HasValue ? PartColor.ToHex(col.Value) : "-")} " +
                   $"fit={(p.Fit != null ? p.Fit.Status + ": " + p.Fit.Headline.Replace('\n', ' ') : "-")} callout=\"{p.Outline.CalloutText.Replace("\n", " | ")}\"";
        }

        public static string FindPart(string query) { AppCommands.FindPart(query); return Parts(); }
        public static string Take(int i) => AppCommands.SelectCandidate(i) ? Parts() : $"no candidate {i} (or a load is running) | {Parts()}";
        public static string SetFinish(string name) => AppCommands.SetFinish(name) ? PartInfo() : $"no finish '{name}' | {PartInfo()}";

        /// Take a part straight from the shipped catalog (no server) into the hand.
        public static string TakeFromCatalog(string id)
        {
            var l = Loader; var t = PartTool;
            if (l == null || t == null) return "no PartLoader / PartTool";
            var p = l.LoadFromCatalog(id);
            if (p == null) return l.LastError;
            t.Hold(p);
            return Parts();
        }

        /// Click (place / pick up) with an explicit ray origin, both scene-root space; reports the part tool.
        public static string PlaceFrom(float ox, float oy, float oz, float x, float y, float z)
        {
            ClickFromWorld(ToWorld(new Vector3(ox, oy, oz)), ToWorld(new Vector3(x, y, z)));
            return Parts();
        }

        /// SPEC M4 [A] checks in the running app, through the real PartTool/PartLoader/FitChecker path
        /// (parts from the catalog so the run is synchronous; ServerLoad/ServerCheck cover the network path).
        /// Leaves a hanger on the fascia (green), an AC in the window (red) and the brown finish for a screenshot.
        public static string RunM4(int seeds = 5)
        {
            var t = PartTool; var l = Loader; var root = Root;
            if (t == null || l == null || root == null) return "missing PartTool / PartLoader / SceneRoot";
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            root.SetVisible(true);
            Physics.SyncTransforms();
            t.ClearAll();
            var report = new StringBuilder();
            int pass = 0, total = 0;

            void Record(ScenarioResult r)
            {
                total++;
                if (r.Passed) pass++;
                Log.Check(r.Id, r.Passed, $"seed={r.Seed} {r.Detail}");
                if (!r.Passed) report.AppendLine(r.ToString());
            }

            foreach (var (name, run) in new (string, System.Func<int, bool, ScenarioResult>)[]
            {
                ("M4.hanger.fascia.near", (seed, keep) => PartScenarios.HangerOnFascia(t, l, root.transform, seed, true, !keep)),
                ("M4.hanger.fascia.ray", (seed, keep) => PartScenarios.HangerOnFascia(t, l, root.transform, seed, false, !keep)),
                ("M4.ac.window.scan", (seed, keep) => PartScenarios.AcOnSill(t, l, root.transform, seed, false, !keep)),
                ("M4.ac.window.tape", (seed, keep) => PartScenarios.AcOnSill(t, l, root.transform, seed, true, !keep)),
            })
            {
                int before = pass;
                for (int seed = 1; seed <= seeds; seed++)
                    Record(run(seed, seed == seeds && (name == "M4.hanger.fascia.near" || name == "M4.ac.window.tape")));
                report.AppendLine($"{name}: {pass - before}/{seeds}");
            }

            // True size and the brown finish on the hanger that stays on the fascia.
            var hanger = t.PlacedParts.FirstOrDefault(p => p.Spec.id == PartScenarios.Hanger);
            if (hanger != null)
            {
                var sz = hanger.MeasuredSizeMm(); var d = hanger.Spec.dims_mm;
                bool size = Mathf.Abs(sz.x - d.w) <= 1f && Mathf.Abs(sz.y - d.h) <= 1f && Mathf.Abs(sz.z - d.d) <= 1f;
                Log.Check("M4.part.truesize", size, $"size_mm={sz.x:0.0}x{sz.y:0.0}x{sz.z:0.0} dims={d}");
                total++; if (size) pass++;
                report.AppendLine($"M4.part.truesize: {(size ? "ok" : "FAIL")} {sz.x:0.0}×{sz.z:0.0}×{sz.y:0.0} mm vs {PartFormat.DimsMm(d)}");
                t.Select(hanger);
                bool brown = AppCommands.SetFinish("brown") && hanger.MaterialColor() is Color c && PartColor.ToHex(c) == "#5A3E2B";
                Log.Check("M4.part.finish.brown", brown, $"color={(hanger.MaterialColor() is Color c2 ? PartColor.ToHex(c2) : "-")}");
                total++; if (brown) pass++;
                report.AppendLine($"M4.part.finish.brown: {(brown ? "ok" : "FAIL")}");
            }
            else { total += 2; report.AppendLine("no hanger left on the fascia"); }

            string summary = $"M4 harness: {pass}/{total} passed";
            Log.Check("M4.harness.summary", pass == total, $"passed={pass} total={total}");
            return summary + "\n" + report;
        }

        /// Network path: search the parts server and take candidate i (async). Check with ServerCheck().
        public static string ServerLoad(string query, int candidate = 0)
        {
            var b = Browser;
            if (b == null) return "no PartsBrowser";
            s_ServerCandidate = candidate;
            s_ServerSearches = b.SearchCount;
            AppCommands.FindPart(query);
            return Parts();
        }

        static int s_ServerCandidate, s_ServerSearches;

        /// Call repeatedly after ServerLoad: takes the candidate once results are in, then reports the loaded part.
        public static string ServerCheck()
        {
            var b = Browser;
            if (b == null) return "no PartsBrowser";
            if (b.Searching || b.SearchCount == s_ServerSearches) return "searching… " + Parts();
            if (b.Loading) return "loading… " + Parts();
            if (b.LastLoaded == null || b.LastLoaded.Spec.id != (s_ServerCandidate < b.Candidates.Count ? b.Candidates[s_ServerCandidate].id : null))
            {
                if (!b.Select(s_ServerCandidate)) return $"cannot take {s_ServerCandidate} | {Parts()}";
                return "loading… " + Parts();
            }
            var p = b.LastLoaded;
            var sz = p.MeasuredSizeMm(); var d = p.Spec.dims_mm;
            bool ok = p.Source == "server" && b.Source.StartsWith("server") && Mathf.Abs(sz.x - d.w) <= 1f && Mathf.Abs(sz.y - d.h) <= 1f && Mathf.Abs(sz.z - d.d) <= 1f;
            Log.Check("M4.server.load", ok, $"search={b.Source} part={p.Spec.id} source={p.Source} size_mm={sz.x:0.0}x{sz.y:0.0}x{sz.z:0.0}");
            return $"{(ok ? "PASS" : "FAIL")} {PartInfo()}";
        }

        // ---------------- M5: array + sellers + checkout ----------------

        static int s_CheckoutLogBefore = -1;

        /// POST /checkout lines in the mock server's request log (tools/.mock_server_data/requests.log) — payments only:
        /// the measured mandate's POST /checkout/prepare (B3, a 404 on older mocks) is not one.
        public static int MockCheckoutCount()
        {
            var path = System.IO.Path.Combine(Application.dataPath, "..", "tools", ".mock_server_data", "requests.log");
            if (!System.IO.File.Exists(path)) return 0;
            int n = 0;
            foreach (var line in System.IO.File.ReadAllLines(path)) if (IsPayment(line)) n++;
            return n;
        }

        public static bool IsPayment(string logLine) =>
            logLine != null && System.Text.RegularExpressions.Regex.IsMatch(logLine, @"POST /checkout(\s|\?|$)");

        /// SPEC M5 [A] checks, synchronous part: array along the 4.200 m gutter tape (8, all green, 600 ± 2 mm),
        /// sellers, checkout opened (no request), a 0.6 s hold (no request), a full 1 s hold (exactly one request).
        /// Then call M5Check() once the request has returned.
        public static string RunM5()
        {
            var t = PartTool; var l = Loader; var root = Root;
            if (t == null || l == null || root == null) return "missing PartTool / PartLoader / SceneRoot";
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            root.SetVisible(true);
            Physics.SyncTransforms();
            t.ClearAll();
            var report = new StringBuilder();
            int pass = 0, total = 0;
            void Check(string id, bool ok, string detail) { total++; if (ok) pass++; Log.Check(id, ok, detail); report.AppendLine($"{id}: {(ok ? "ok" : "FAIL")} {detail}"); }

            var r = PartScenarios.HangerOnFascia(t, l, root.transform, 1, proximity: true, removeAfter: false);
            Check("M5.reference", r.Passed, r.Detail);
            var tape = new NotebookEntry("measure", 4.2, "m", new[] { new Vector3(-2.1f, 6.1f, 0.15f), new Vector3(2.1f, 6.1f, 0.15f) }, System.DateTime.Now, -1, "Distance 4.20 m (gutter)");
            Notebook.Add(tape);
            var g = t.PlaceArray();
            var members = g != null ? PartTool.ArrayMembers(g) : new System.Collections.Generic.List<PartInstance>();
            int green = members.Count(p => p.Fit != null && p.Fit.Status == AirTools.Parts.FitStatus.Green);
            float worst = 0f;
            for (int k = 1; k < members.Count; k++)
                worst = Mathf.Max(worst, Mathf.Abs(Vector3.Distance(members[k].transform.position, members[k - 1].transform.position) * 1000f - 600f));
            Check("M5.array", members.Count == 8 && green == 8 && worst <= 2f, $"count={members.Count} green={green} worst_spacing_error_mm={worst:0.00}");

            bool sellers = AppCommands.ShowSellers("cheapest");
            var sp = Services.Get<SellerPanel>();
            Check("M5.sellers", sellers && sp != null && sp.IsOpen, $"order={(sp != null ? string.Join(",", sp.Order) : "-")} qty={(sp != null ? sp.Quantity() : 0)}");

            var client = Services.Get<PartsClient>(); var panel = Services.Get<CheckoutPanel>();
            int requests0 = client != null ? client.CheckoutRequests : -1;
            s_CheckoutLogBefore = MockCheckoutCount();
            int rec = t.Selected != null ? (t.Selected.Spec.recommended_seller ?? 0) : 0;
            bool opened = AppCommands.StartCheckout(rec);
            Check("M5.checkout.open_only", opened && panel != null && panel.State == CheckoutState.Ready && client.CheckoutRequests == requests0,
                $"state={panel?.State} qty={panel?.Quantity} total={panel?.Total:0.00} requests={client?.CheckoutRequests - requests0}");
            s_M5Requests0 = requests0;
            s_M5HoldsDone = false;
            // The measured mandate (B3) prepares on open; the holds run once it has answered (M5Check), since a hold while
            // the server is still checking is refused by design.
            if (panel.Mandate != MandateState.Pending) M5Holds(Check);
            string summary = $"M5 harness (sync part): {pass}/{total} passed — call M5Check() for {(s_M5HoldsDone ? "the receipt" : "the holds and the receipt")}";
            Log.Check("M5.harness.sync", pass == total, $"passed={pass} total={total}");
            return summary + "\n" + report;
        }

        static int s_M5Requests0;
        static bool s_M5HoldsDone;

        /// A 0.6 s hold (no request), then a full 1 s hold (exactly one request).
        static void M5Holds(System.Action<string, bool, string> check)
        {
            var client = Services.Get<PartsClient>(); var panel = Services.Get<CheckoutPanel>();
            s_M5HoldsDone = true;
            bool fired06 = panel.hold.Simulate(0.6f);
            check("M5.checkout.hold_0.6s", !fired06 && client.CheckoutRequests == s_M5Requests0, $"fired={fired06} requests={client.CheckoutRequests - s_M5Requests0} mandate={panel.Mandate}");
            bool fired10 = panel.hold.Simulate(1.0f);
            check("M5.checkout.hold_1.0s", fired10 && client.CheckoutRequests == s_M5Requests0 + 1, $"fired={fired10} requests={client.CheckoutRequests - s_M5Requests0} mandate={panel.Mandate}");
        }

        /// After RunM5: the receipt came back, the notebook has the purchase, and the mock server saw exactly one
        /// POST /checkout.
        public static string M5Check()
        {
            var panel = Services.Get<CheckoutPanel>();
            if (panel == null) return "no CheckoutPanel";
            if (!s_M5HoldsDone)
            {
                if (panel.Mandate == MandateState.Pending) return "still preparing…";
                M5Holds((id, ok, detail) => Log.Check(id, ok, detail));
                return "holds done — call M5Check() again for the receipt";
            }
            if (panel.State == CheckoutState.Paying) return "still authorizing…";
            int sent = s_CheckoutLogBefore >= 0 ? MockCheckoutCount() - s_CheckoutLogBefore : -1;
            // Authorized (sandbox) or the labelled offline receipt the real server gives without Cybersource keys.
            bool ok = panel.State == CheckoutState.Paid && panel.Receipt != null && panel.Receipt.Recorded && !string.IsNullOrEmpty(panel.Receipt.label ?? panel.Receipt.sandbox)
                      && Notebook.Last != null && Notebook.Last.Tool == "purchase" && sent == 1;
            Log.Check("M5.checkout.receipt", ok, $"state={panel.State} receipt={panel.Receipt?.receipt_id} total={panel.Receipt?.total_usd:0.00} mode={panel.Receipt?.mode ?? panel.Receipt?.sandbox} mock_requests={sent} notebook=\"{Notebook.Last?.Label}\"");
            return $"{(ok ? "PASS" : "FAIL")} state={panel.State} receipt={panel.Receipt?.receipt_id} total={panel.Receipt?.total_usd:0.00} mock_requests={sent} last=\"{Notebook.Last?.Label}\"";
        }

        // ---------------- the whole demo ----------------

        /// The judge's walkthrough end to end on the built-in facade (DemoWalkthrough): passthrough → world → ring →
        /// measure → level → notebook + export → find/take/place → array → sellers → checkout hold → Undo/Redo →
        /// exit → take it home → tabletop 1:50 → 1:1. Parts/checkout use `server` (the mock on :8001; "" = the
        /// current server); the default server is restored afterwards. Run it at the start of a Play session and
        /// poll DemoResult(). One [AirTools.Check] Demo.<beat> line per beat.
        public static string RunDemo(string server = DemoWalkthrough.MockServer) => DemoWalkthrough.Start(server);

        public static string DemoResult() => DemoWalkthrough.Report();

        // ---------------- presence.md S1: grow in / shrink out + site mat ----------------

        static TransitionDirector Director => Services.Get<TransitionDirector>();

        /// The model on the table (from passthrough: scans in; from the world: shrinks out).
        public static string TableTop() { AppCommands.SetTabletop(true); return TransitionStatus(); }
        /// Step into the model (grow in), as the "You are here" pin / the ring's Step in do.
        public static string StepIn() { AppCommands.StepIn(); return TransitionStatus(); }
        /// Back onto the table (shrink out).
        public static string StepOut() { AppCommands.StepOut(); return TransitionStatus(); }

        /// Mode, visuals, the director's kind / phase / progress, the model's scale and the table source.
        public static string TransitionStatus()
        {
            var m = Services.Get<ModeController>();
            var t = Services.Get<TabletopController>();
            var d = Director;
            var pin = Services.Get<SpawnMarker>();
            var home = Services.Get<TakeItHome>();
            float scale = Root != null ? Root.transform.lossyScale.x : 0f;
            return $"mode={AppState.Mode} visual={m?.VisualMode} transitioning={m?.IsTransitioning} scale={scale.ToString("0.0000", C)} " +
                   $"{(d != null ? d.Status() : "no director")} table={t?.TableSource} onTable={t?.OnTable} pin={(pin != null && pin.Shown ? F(pin.FootWorld) : "hidden")} " +
                   $"home={home?.OnTable.Count ?? 0}{(home != null && home.Pending ? " (pending)" : "")} {home?.LastPlacement} reducedMotion={AirTools.UI.UiSettings.ReducedMotion}";
        }

        /// The site mat and the table fallback chain.
        public static string Mat()
        {
            var mat = Services.Get<SiteMatTracker>();
            var table = Services.Get<RealTable>();
            if (mat == null && table == null) return "no SiteMatTracker / RealTable";
            string m = mat == null ? "no tracker" : $"status=\"{mat.Status}\" locked={mat.Locked}{(mat.Locked ? $" pose={F(mat.MatPose.position)} yaw={mat.MatPose.rotation.eulerAngles.y.ToString("0.0", C)}" : "")} device={mat.DeviceStarted} raycast={mat.RaycastAvailable} payload=\"{mat.LastPayload}\"";
            string t = table == null ? "no table" : $"last={table.LastSource} frame={F(table.LastFrame.position)} assumed={table.assumedHeight.ToString("0.00", C)} m";
            return $"{m} | {t}";
        }

        /// Pretend the site mat's QR was seen and held still (Editor: no MRUK): mat frame origin at world (x, y, z),
        /// its long edge along yaw `yawDeg`.
        public static string MatLock(float x, float y, float z, float yawDeg)
        {
            var mat = Services.Get<SiteMatTracker>();
            if (mat == null) return "no SiteMatTracker";
            SiteMatSpec.TryParse("airtools:mat:v1;w=0.420;h=0.297;qr=0.120;pad=0.300,0.150", out var spec);
            mat.Lock(new Pose(new Vector3(x, y, z), Quaternion.Euler(0f, yawDeg, 0f)), spec);
            return Mat();
        }

        public static string MatUnlock() { Services.Get<SiteMatTracker>()?.Unlock(); return Mat(); }

        static readonly System.Collections.Generic.List<string> s_Watch = new System.Collections.Generic.List<string>();
        static string s_WatchSummary = "";

        /// Do `action` ("tabletop", "stepin", "stepout", "open", "close", "exit") and sample the transition at 5 Hz in
        /// the app (scale, phase, visuals) until it ends; optionally save a Game-view screenshot `captureAt` seconds in
        /// (absolute path). Poll WatchResult().
        public static string WatchTransition(string action, float captureAt = -1f, string capturePath = null)
        {
            if (!Application.isPlaying) return "Play mode only";
            s_Watch.Clear();
            s_WatchSummary = "running";
            if (!DemoRunner.Run(Watch(action, captureAt, capturePath), e => s_WatchSummary = $"error {e.GetType().Name}: {e.Message}", null))
                return "busy (another routine is running)";
            return "watching — poll WatchResult()";
        }

        public static string WatchResult() => s_WatchSummary + "\n" + string.Join("\n", s_Watch);

        static System.Collections.IEnumerator Watch(string action, float captureAt, string capturePath)
        {
            var d = Director;
            var m = Services.Get<ModeController>();
            if (d == null || m == null) { s_WatchSummary = "no director / mode controller"; yield break; }
            var from = AppState.Mode;
            float t0 = Time.realtimeSinceStartup;
            switch (action)
            {
                case "tabletop": AppCommands.SetTabletop(true); break;
                case "stepin": AppCommands.StepIn(); break;
                case "stepout": AppCommands.StepOut(); break;
                case "open": AppCommands.OpenChest(); break;
                case "close": AppCommands.CloseChest(); break;
                case "exit": AppCommands.ToggleChest(); break;
                default: s_WatchSummary = $"unknown action '{action}'"; yield break;
            }
            var kind = d.Kind;
            bool captured = captureAt < 0f || string.IsNullOrEmpty(capturePath);
            float next = 0f, prevScale = -1f;
            int rising = 0, falling = 0;
            var phases = new System.Collections.Generic.List<string>();
            while (true)
            {
                float t = Time.realtimeSinceStartup - t0;
                float scale = Root != null ? Root.transform.lossyScale.x : 0f;
                if (!captured && t >= captureAt)
                {
                    ScreenCapture.CaptureScreenshot(capturePath);
                    captured = true;
                    s_Watch.Add($"capture at t={t.ToString("0.00", C)} phase={d.Phase} progress={d.Progress.ToString("0.00", C)} sky={(RevealField.SkyAngle * Mathf.Rad2Deg).ToString("0", C)}° → {capturePath}");
                }
                if (t >= next)
                {
                    next += 0.2f;
                    string ph = d.Playing ? d.Phase.ToString().ToLowerInvariant() : "idle";
                    if (phases.Count == 0 || phases[phases.Count - 1] != ph) phases.Add(ph);
                    if (prevScale >= 0f) { if (scale > prevScale + 1e-6f) rising++; else if (scale < prevScale - 1e-6f) falling++; }
                    prevScale = scale;
                    s_Watch.Add($"t={t.ToString("0.00", C)} phase={ph} progress={d.Progress.ToString("0.00", C)} scale={scale.ToString("0.0000", C)} visual={m.VisualMode} sky={(RevealField.SkyAngle * Mathf.Rad2Deg).ToString("0", C)}° reveal={(RevealField.SphereOn ? RevealField.Radius.ToString("0.00", C) : "off")}");
                }
                if (!m.IsTransitioning && captured && t > 0.05f) break;
                if (t > 8f) { s_Watch.Add("timeout"); break; }
                yield return null;
            }
            float end = Root != null ? Root.transform.lossyScale.x : 0f;
            if (phases.Count == 0 || phases[phases.Count - 1] != "idle") phases.Add("idle");
            s_WatchSummary = $"{action}: {from} → {AppState.Mode} kind={kind} seconds={d.LastSeconds.ToString("0.00", C)} visual={m.VisualMode} phases={string.Join("→", phases)} " +
                             $"scale_end={end.ToString("0.0000", C)} samples_rising={rising} falling={falling} | {TransitionStatus()}";
            Log.Check($"S1.watch.{action}", AppState.Mode == m.VisualMode && !m.IsTransitioning, s_WatchSummary);
        }

        // ---------------- W1.3 guide rail (docs/ux/specs/W1.3-nextstep.md §5.6, §6.2) ----------------
        // The judge's path through the harness: every [J] call below counts one judge action; ring openings come from
        // the palm menu (PalmMenu.Changed(true), any opener). NextStep() works with the rail off, too (it assembles the
        // snapshot on demand); GuideOn(true) shows the status line and the pill.

        static AirTools.UI.GuideRail GuideRailSvc => Services.Get<AirTools.UI.GuideRail>();
        static int s_JudgeActions, s_RingOpensAtReset;
        static readonly System.Collections.Generic.List<string> s_JudgeTrail = new System.Collections.Generic.List<string>();

        /// W1.3: switch the guide rail (and W1.8 DemoMode, D7) on or off. DemoMode first: it switches the rail with itself.
        public static string GuideOn(bool on, bool demo = true)
        {
            AirTools.Core.DemoMode.On = demo;
            AirTools.UI.GuideRail.Enabled = on;
            GuideRailSvc?.Invalidate();
            return NextStep();
        }

        static global::AirTools.Core.Step CurrentStep(AirTools.UI.GuideRail rail, out AppSnapshot s)
        {
            s = rail.Assemble();
            var step = global::AirTools.Core.NextStep.For(s);
            return step.Frozen ? rail.Current : step;
        }

        /// W1.3: the rail's step now, e.g. rule=R42 status="Saved 0.39 m ✓" primary="Find hinges for this door"→FindPart(cabinet
        /// hinge) secondary=["Notebook"→ShowNotebook(true)] flags=- coach=C12(1); also logged as [AirTools.Check] NextStep.&lt;rule&gt;.
        public static string NextStep()
        {
            var rail = GuideRailSvc;
            if (rail == null) return "no GuideRail in scene (AirTools ▸ Wire Main Scene)";
            var step = CurrentStep(rail, out var s);
            var st = rail.CoachState;
            string coach = st.Current == CoachRuleId.None ? "-" : $"{st.Current}({st.Shows[(int)st.Current]})";
            var secs = new System.Collections.Generic.List<string>(2);
            if (!step.Secondary0.IsNone) secs.Add(step.Secondary0.ToString());
            if (!step.Secondary1.IsNone) secs.Add(step.Secondary1.ToString());
            string text = $"rule={step.Rule}{(step.Base != step.Rule ? "→" + step.Base : "")} status=\"{step.Status}\" primary={step.Primary} " +
                          $"secondary=[{string.Join(", ", secs)}] flags={(step.Flags == StepFlags.None ? "-" : step.Flags.ToString())} " +
                          $"pill={(step.PillVisible ? "shown" : "hidden")} coach={coach} newest={s.Newest} rail={(AirTools.UI.GuideRail.Enabled ? "on" : "off")}";
            Log.Check($"NextStep.{step.Rule}", true, text);
            return text;
        }

        /// W1.3 [J]: tap the pill — its primary (0) or a secondary chip (1, 2) — through NextStepActions (the pill's own path).
        public static string DoNext(int which = 0)
        {
            var rail = GuideRailSvc;
            if (rail == null) return "no GuideRail in scene";
            var step = CurrentStep(rail, out _);
            if (!step.PillVisible) return $"the pill is hidden at {step.Rule} (use PressEnter / HoldPay) | {NextStep()}";
            var a = which == 0 ? step.Primary : which == 1 ? step.Secondary0 : step.Secondary1;
            if (a.IsNone) return $"nothing at {which} | {NextStep()}";
            Judge($"DoNext({which}) {a}");
            bool ok = NextStepActions.Run(a);
            return $"{(ok ? "ok" : "refused")} | {NextStep()}";
        }

        /// W1.3: the coach on screen, its text and the shows per rule.
        public static string Coach()
        {
            var rail = GuideRailSvc;
            if (rail == null) return "no GuideRail in scene";
            var st = rail.CoachState;
            var svc = Services.Get<AirTools.UI.CoachService>() ?? Object.FindFirstObjectByType<AirTools.UI.CoachService>();
            var shows = new System.Collections.Generic.List<string>();
            for (int i = 1; i < CoachState.Count; i++) if (st.Shows[i] > 0) shows.Add($"{(CoachRuleId)i}:{st.Shows[i]}");
            return $"coach={st.Current} text=\"{svc?.Text}\" shows=[{string.Join(", ", shows)}] idle={rail.Snapshot.IdleSeconds.ToString("0.0", C)}s";
        }

        /// W1.3: judge actions so far and ring openings since ResetJudge.
        public static string JudgeLog()
        {
            var rail = GuideRailSvc;
            int rings = rail != null ? rail.RingOpensAll - s_RingOpensAtReset : -1;
            string text = $"actions={s_JudgeActions} ringOpens={rings}";
            Log.Check("NextStep.judge", true, $"{text} trail=[{string.Join(" | ", s_JudgeTrail)}]");
            return $"{text}\n  {string.Join("\n  ", s_JudgeTrail)}";
        }

        public static string ResetJudge()
        {
            s_JudgeActions = 0;
            s_JudgeTrail.Clear();
            s_RingOpensAtReset = GuideRailSvc != null ? GuideRailSvc.RingOpensAll : 0;
            return JudgeLog();
        }

        /// W1.3 end-of-path check (§6.2): actions == expected and no ring openings.
        public static string JudgeCheck(int expectedActions = 10)
        {
            var rail = GuideRailSvc;
            int rings = rail != null ? rail.RingOpensAll - s_RingOpensAtReset : -1;
            bool pass = s_JudgeActions == expectedActions && rings == 0;
            Log.Check("NextStep.demo_path", pass, $"actions={s_JudgeActions} expected={expectedActions} ringOpens={rings}");
            return JudgeLog();
        }

        static void Judge(string what)
        {
            s_JudgeActions++;
            s_JudgeTrail.Add($"J{s_JudgeActions} {what}");
        }

        /// W1.3 [J]: press the Enter / Exit world control (the physical press path, ChestController.Press).
        public static string PressEnter()
        {
            var chest = Object.FindFirstObjectByType<ChestController>();
            if (chest == null) return "no world button";
            Judge("PressEnter");
            chest.Press();
            return NextStep();
        }

        /// A structure object's corners in scene-root space ("o5" = a kitchen door), its centre and the normal toward the
        /// camera.
        static bool TryObject(string objectId, out Vector3[] corners, out Vector3 centre, out Vector3 normal)
        {
            corners = null; centre = normal = Vector3.zero;
            var root = Root;
            var layer = root != null ? root.Structure : null;
            if (layer == null) return false;
            foreach (var o in layer.Objects)
            {
                if (o.id != objectId || o.corners == null || o.corners.Length < 3) continue;
                corners = o.corners.Select(p => root.PackageToRoot(p)).ToArray();
                foreach (var p in corners) centre += p;
                centre /= corners.Length;
                normal = Vector3.Cross(corners[1] - corners[0], corners[corners.Length - 1] - corners[0]).normalized;
                var cam = Camera.main;
                if (cam != null && Vector3.Dot(normal, ToRoot(cam.transform.position) - centre) < 0f) normal = -normal;
                return true;
            }
            return false;
        }

        /// W1.3 [J]: click one top corner of a structure object (click 0 / 1: the two highest corners, left to right in
        /// scene space), from 0.6 m in front of it, through the tool input path — one pinch.
        public static string TapeObject(string objectId, int click)
        {
            if (!TryObject(objectId, out var corners, out var centre, out var normal)) return $"no structure object {objectId}";
            var top = corners.OrderByDescending(p => p.y).Take(2).OrderBy(p => p.x).ToArray();
            var corner = top[Mathf.Clamp(click, 0, 1)];
            var target = corner + (centre - corner).normalized * 0.01f;   // 1 cm inside: the snap pulls it onto the corner
            Judge($"TapeObject({objectId}, {click})");
            string r = ClickFromWorld(ToWorld(target + normal * 0.6f), ToWorld(target));
            return $"{r} | {NextStep()}";
        }

        /// W1.3 [J]: place the held part at the middle of a structure object (from 0.6 m in front) — one pinch.
        public static string PlaceOnObject(string objectId)
        {
            if (!TryObject(objectId, out _, out var centre, out var normal)) return $"no structure object {objectId}";
            var origin = centre + normal * 0.6f;
            Judge($"PlaceOnObject({objectId})");
            string r = PlaceFrom(origin.x, origin.y, origin.z, centre.x, centre.y, centre.z);
            return $"{r} | {NextStep()}";
        }

        /// W1.3 [J]: hold the checkout's Pay button for `seconds` (the physical hold path, HoldToConfirm.Simulate).
        public static string HoldPay(float seconds = 1f)
        {
            var checkout = Services.Get<CheckoutPanel>();
            if (checkout == null || checkout.hold == null) return "no checkout";
            Judge($"HoldPay({seconds.ToString("0.0", C)})");
            bool fired = checkout.hold.Simulate(seconds);
            return $"confirmed={fired} state={checkout.State} | {NextStep()}";
        }

        /// W1.3: every coach hint may show again ("Show tips again").
        public static string CoachReset()
        {
            (Services.Get<AirTools.UI.CoachService>() ?? Object.FindFirstObjectByType<AirTools.UI.CoachService>())?.ResetShows();
            return Coach();
        }

        // ---------------- D2: one unit per label (SPEC §9) ----------------

        /// Switch the on-screen unit like the units chip: "imperial" / "metric" / "toggle" (saved on this device).
        /// Returns the unit and the last shape's labels.
        public static string SetUnits(string which)
        {
            var w = (which ?? "").Trim().ToLowerInvariant();
            if (w.StartsWith("t")) AirTools.UI.UnitsSwitch.Toggle();
            else if (w.StartsWith("m")) AirTools.UI.UnitsSwitch.Set(UnitSystem.Metric);
            else if (w.StartsWith("i") || w.StartsWith("f")) AirTools.UI.UnitsSwitch.Set(UnitSystem.Imperial);
            else return $"units={AirTools.UI.UiSettings.UnitSystem} (imperial | metric | toggle)";
            return $"units={AirTools.UI.UiSettings.UnitSystem} labels=\"{Labels()}\"";
        }

        /// D2 check: tape the window (1.500 m), then switch units both ways. The tape's label reads ONE unit — feet and
        /// inches, then metres — re-labelled in place (same shape, same ValueSI, same raw notebook Label, no new entry);
        /// the unit you started with is restored. Logs [AirTools.Check] D2.units.relabel (numbers stay metric).
        public static string RunD2()
        {
            var tool = Measure; var hub = Hub; var root = Root;
            if (tool == null || hub == null || root == null) return "missing MeasureTool / ToolInputHub / SceneRoot";
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            root.SetVisible(true);
            Physics.SyncTransforms();
            if (Tools != null && Tools.Active != ToolKind.Measure) Tools.Equip(ToolKind.Measure);
            var was = AirTools.UI.UiSettings.UnitSystem;
            var win = MeasureScenarios.M2().First(s => s.Id == "M2.measure.window.width");
            var r = MeasureScenarios.Run(win, tool, hub, root.transform, 1, removeAfter: false);
            var shape = tool.Shapes.Count > 0 ? tool.Shapes[tool.Shapes.Count - 1] : null;
            if (shape?.Entry == null || shape.View == null || shape.View.ActiveLabelCount == 0)
            {
                Log.Check("D2.units.relabel", false, $"no tape: {r.Detail}");
                return "FAIL no tape";
            }
            var e = shape.Entry;
            string raw = e.Label;
            double v = e.ValueSI;
            int entries = Notebook.Entries.Count;
            AirTools.UI.UnitsSwitch.Set(UnitSystem.Imperial);
            string imp = shape.View.Labels[0].Text;
            AirTools.UI.UnitsSwitch.Set(UnitSystem.Metric);
            string met = shape.View.Labels[0].Text;
            AirTools.UI.UnitsSwitch.Set(was);
            bool ok = r.Passed && imp == Units.FormatPrimary(v, UnitSystem.Imperial) && met == Units.FormatPrimary(v, UnitSystem.Metric)
                      && imp.Contains("′") && !imp.Contains(" m") && !met.Contains("″")
                      && e.Label == raw && e.ValueSI == v && Notebook.Entries.Count == entries && tool.Shapes.Contains(shape);
            Log.Check("D2.units.relabel", ok, $"value_m={v.ToString("0.0000", C)} imperial=\"{imp}\" metric=\"{met}\" label=\"{raw}\" restored={was}");
            return $"{(ok ? "PASS" : "FAIL")} imperial=\"{imp}\" metric=\"{met}\" value_m={v.ToString("0.000", C)} | {r.Detail}";
        }

        // ---------------- D7 (UX W1.8): demo controls — DemoMode, ResetDemo, reset gestures, presenter link ----------------

        static PresenterLink Link => Services.Get<PresenterLink>() ?? Object.FindFirstObjectByType<PresenterLink>();
        static DemoResetGesture Gesture => Services.Get<DemoResetGesture>() ?? Object.FindFirstObjectByType<DemoResetGesture>();

        /// D7: DemoMode on / off (the guide rail and coach follow it). Returns DemoStatus().
        public static string DemoOn(bool on = true)
        {
            AppCommands.SetDemoMode(on);
            GuideRailSvc?.Invalidate();
            return DemoStatus();
        }

        /// D7: DemoMode, the rail, resets, the reset gestures and the presenter link at a glance.
        public static string DemoStatus()
        {
            var link = Link; var g = Gesture;
            string gesture = g == null ? "none (AirTools ▸ Wire Main Scene)" : $"resets={g.Resets} last={g.LastSource} hands_pose={g.PoseNow}";
            string last = link != null && link.HasLast ? $"{link.LastCommand} {(link.LastOk ? "ok" : "failed")} {link.LastDetail}" : "-";
            string presenter = link == null ? "none (AirTools ▸ Wire Main Scene)"
                : $"{link.BaseUrl} active={link.Active} online={link.Online} polls={link.Polls} posts={link.StatePosts} received={link.Received} acks={link.Acks} refused={link.Refused} beat={(link.BeatRunning ? "running" : "-")} last=\"{last}\"";
            return $"demo={(DemoMode.On ? "on" : "off")} rail={(AirTools.UI.GuideRail.Enabled ? "on" : "off")} resets={DemoReset.Count} gesture=[{gesture}] presenter=[{presenter}]";
        }

        /// D7 [A]: AppCommands.ResetDemo (logs [AirTools.Check] D7.reset), the judge counters zeroed, then Status() and the
        /// rail's step (R05 "Touch Enter world…").
        public static string ResetDemo()
        {
            bool ok = AppCommands.ResetDemo();
            ResetJudge();
            return $"{(ok ? "ok" : "REFUSED")} {DemoReset.LastReport}\n{Status()}\n{NextStep()}";
        }

        /// D7 [A]: is the app in its first-run state right now? ([AirTools.Check] D7.reset.verify)
        public static string ResetCheck()
        {
            bool clean = DemoReset.Verify(out var detail);
            Log.Check("D7.reset.verify", clean, detail);
            return $"{(clean ? "PASS" : "FAIL")} {detail}";
        }

        /// D7: hold a reset gesture's input for `seconds` through the gesture's own detector ("sticks" or "hands"; hands
        /// can't be simulated, so this overrides the pose check). ≥ 2 s resets; less doesn't. Poll DemoStatus().
        public static string HoldResetGesture(string which = "sticks", float seconds = 2.3f)
        {
            var g = Gesture;
            if (g == null) return "no DemoResetGesture (AirTools ▸ Wire Main Scene)";
            if (!DemoMode.On && !g.alwaysOn) return "DemoMode is off: DemoOn(true) first";
            bool hands = which == "hands";
            int before = DemoReset.Count;
            if (!DemoRunner.Run(HoldGesture(g, hands, seconds), Debug.LogException, () => { })) return "another routine is running";
            return $"holding {(hands ? "the hands' pose" : "both stick clicks")} for {seconds.ToString("0.0", C)} s (resets before: {before}) — then DemoStatus()";
        }

        static System.Collections.IEnumerator HoldGesture(DemoResetGesture g, bool hands, float seconds)
        {
            if (hands) g.PoseOverride = true; else g.SticksOverride = true;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < seconds) yield return null;
            if (hands) g.PoseOverride = false; else g.SticksOverride = false;
            yield return new WaitForSecondsRealtime(0.4f);
            if (hands) g.PoseOverride = null; else g.SticksOverride = null;
        }

        /// D7: run one presenter command on the headset directly (no relay), e.g. PresenterRun("beat", "measure").
        public static string PresenterRun(string cmd, string arg = null)
        {
            var link = Link;
            if (link == null) return "no PresenterLink (AirTools ▸ Wire Main Scene)";
            bool ok = link.Execute(new PresenterCommand("local-harness", cmd, arg), out var detail);
            return $"{(ok ? "ok" : "failed")} {detail}{(link.BeatRunning ? " (the beat finishes over the next frames: DemoStatus())" : "")}";
        }

        static readonly System.Collections.Generic.List<string> s_Presenter = new System.Collections.Generic.List<string>();
        static bool s_PresenterDone, s_PresenterPass;

        /// D7 [A]: the presenter round trip over the relay (python3 tools/presenter/presenter_server.py; on the headset
        /// through adb reverse tcp:8766): the relay refuses "pay" (403); a "ping" queued on the relay reaches PresenterLink,
        /// runs, and its ack shows on the relay's /state with the headset online; PresenterLink refuses "pay" itself too.
        /// `url` = another relay (default: the parts server's host on :8766). Poll PresenterResult().
        public static string PresenterCheck(string url = null)
        {
            if (!Application.isPlaying) return "Play mode only";
            var link = Link;
            if (link == null) link = new GameObject("PresenterLink (harness)").AddComponent<PresenterLink>();
            PresenterLink.ForceActive = true;
            if (!string.IsNullOrEmpty(url)) PresenterLink.UrlOverride = url;
            s_Presenter.Clear();
            s_PresenterDone = s_PresenterPass = false;
            if (!DemoRunner.Run(PresenterRoundTrip(link), ex => s_Presenter.Add("exception: " + ex.Message), () => s_PresenterDone = true))
                return "another routine is running";
            return $"presenter round trip started against {link.BaseUrl} — poll PresenterResult()";
        }

        public static string PresenterResult() =>
            $"{(s_PresenterDone ? (s_PresenterPass ? "PASS" : "FAIL") : "running…")}\n{string.Join("\n", s_Presenter)}";

        static System.Collections.IEnumerator PresenterRoundTrip(PresenterLink link)
        {
            string relay = link.BaseUrl;
            int pass = 0, total = 0;
            void Check(string id, bool ok, string detail) { total++; if (ok) pass++; Log.Check(id, ok, detail); s_Presenter.Add($"{id}: {(ok ? "ok" : "FAIL")} {detail}"); }

            // 1. The relay won't queue a payment.
            var refuse = PostJson(relay + "/presenter/cmd", "{\"cmd\":\"pay\",\"source\":\"harness\"}");
            yield return HttpDeadline.Send(refuse, HttpDeadline.SmallIdle);   // fix-ux: our clock
            Check("D7.presenter.relay_refuses_pay", refuse.responseCode == 403, $"POST /presenter/cmd pay → {refuse.responseCode} {refuse.downloadHandler?.text}");
            refuse.Dispose();

            // 2. relay → PresenterLink → ack → relay.
            var ping = PostJson(relay + "/presenter/cmd", "{\"cmd\":\"ping\",\"source\":\"harness\"}");
            yield return HttpDeadline.Send(ping, HttpDeadline.SmallIdle);
            string id = null;
            if (HttpDeadline.Ok(ping))
                id = (string)Newtonsoft.Json.Linq.JObject.Parse(ping.downloadHandler.text)["command"]?["id"];
            Check("D7.presenter.queued", id != null, $"POST /presenter/cmd ping → {ping.responseCode} id={id ?? "-"} ({ping.error})");
            ping.Dispose();
            if (id != null)
            {
                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 8f && !(link.HasLast && link.LastCommand.Id == id)) yield return null;
                Check("D7.presenter.link_ran", link.HasLast && link.LastCommand.Id == id && link.LastOk,
                      $"{id} ran after {(Time.realtimeSinceStartup - t0).ToString("0.00", C)} s: \"{link.LastDetail}\" online={link.Online} polls={link.Polls}");
                Newtonsoft.Json.Linq.JObject st = null, cmd = null;
                t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < 6f)
                {
                    var get = UnityEngine.Networking.UnityWebRequest.Get(relay + "/state");
                    yield return HttpDeadline.Send(get, HttpDeadline.SmallIdle);
                    if (HttpDeadline.Ok(get))
                    {
                        st = Newtonsoft.Json.Linq.JObject.Parse(get.downloadHandler.text);
                        cmd = (st["commands"] as Newtonsoft.Json.Linq.JArray)?.OfType<Newtonsoft.Json.Linq.JObject>().FirstOrDefault(c => (string)c["id"] == id);
                    }
                    get.Dispose();
                    if (cmd != null && (string)cmd["status"] == "ok" && (bool?)st["online"] == true) break;
                    yield return new WaitForSecondsRealtime(0.3f);
                }
                Check("D7.presenter.acked", cmd != null && (string)cmd["status"] == "ok", $"relay: {id} {cmd?["status"]} \"{cmd?["detail"]}\"");
                Check("D7.presenter.headset_online", st != null && (bool?)st["online"] == true && (string)st["headset"]?["app"] == "airtools",
                      $"online={st?["online"]} age={st?["headset_age_s"]} mode={st?["headset"]?["mode"]} rule={st?["headset"]?["rule"]} beat={st?["headset"]?["beat"]}");
            }

            // 3. PresenterLink refuses a payment on its own (a relay that let one through).
            int refused0 = link.Refused;
            bool ran = link.Execute(new PresenterCommand("local-pay", "pay", null), out var why);
            Check("D7.presenter.link_refuses_pay", !ran && link.Refused == refused0 + 1 && why == PresenterCommands.Refusal, why);
            s_PresenterPass = total > 0 && pass == total;
            Log.Check("D7.presenter.roundtrip", s_PresenterPass, $"passed={pass} total={total} relay={relay}");
        }

        static UnityEngine.Networking.UnityWebRequest PostJson(string url, string json) =>
            new UnityEngine.Networking.UnityWebRequest(url, UnityEngine.Networking.UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(Encoding.UTF8.GetBytes(json)) { contentType = "application/json" },
                downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer(),
                timeout = 5,
            };
        // ---------------- Declutter (docs/ux/declutter.md §6): the census ----------------

        /// Declutter S2: what is on screen now, e.g. `hud=1 (max 1) docked=Checkout main=Checkout sideL=- sideR=- pill=0
        /// enter=0 head=2 (max 3) wrist=limits palm=closed quad=0 layers=[plan] labels=9/12 overlaps=0 | heads-up: line "…"`.
        /// The maxima count since SurfacesReset() (UiCensusWatch samples every frame).
        public static string Surfaces()
        {
            UiCensus.Take(out var line);
            return line;
        }

        /// Declutter S2: start the census maxima over.
        public static string SurfacesReset()
        {
            UiCensus.ResetMax();
            return Surfaces();
        }

        /// Declutter S6 [J]: tap Take it home on the checkout's receipt (M3: the Next-step pill yields while the checkout
        /// window is open, so on the headset this is J10 of the W1.3 path; DoNext(0) runs the same step action).
        public static string ReceiptTakeHome()
        {
            if (!Services.TryGet<CheckoutPanel>(out var checkout)) return "no checkout panel";
            if (checkout.State != CheckoutState.Paid) return $"no receipt yet (checkout {checkout.State})";
            Judge("ReceiptTakeHome() \"Take it home\"→TakeHome()");
            bool ok = checkout.takeHome != null ? checkout.takeHome.Press() : checkout.TakeHome();   // the button's own path
            return $"{(ok ? "ok" : "refused")} | {Status()} | {NextStep()}";
        }

        /// Declutter S2: [AirTools.Check] UI.budget.&lt;activity&gt; — the census against that activity's §2 row
        /// (passthrough-entry, take-it-home, tabletop, measuring, finding-parts, checkout, grok-job, coaching, survey,
        /// settings) and the invariants (≤ 1 heads-up surface, no overlaps, ≤ 1 Grok layer, ≤ 12 labels).
        public static string SurfaceCheck(string activity) => UiCensus.Check(activity);
        // end Declutter

        // ---------------- Grok lanes ----------------
        // Grok G2
        /// Grok lanes: the backend's example payloads for a lane's actions through AgentActions.ExecuteAll on the loaded
        /// scene, one [AirTools.Check] line per action. "g2": 3D overlays (load kitchen or synthetic-facade first).

        /// Grok G2 capture angles: "kitchen-labels", "kitchen-survey", "kitchen-plan", "facade-plan", "facade-coverage".
        public static string GrokLook(string shot) => GrokG2Check.Look(shot);
        // end Grok G2

        // ---------------- Declutter C (S10): one world-label pool (docs/ux/declutter.md §5) ----------------

        /// The world-label pool (WorldLabels): "labels=9/12 shown=14 asked=30 dropped=16 | P0 safety 3 · … | each
        /// producer's claims → grants", plus the newest tape's labels. Logs [AirTools.Check] UI.labels: the pool holds
        /// ≤ 12 and the newest of the user's shapes shows its whole set (sides, angles and area; a tape in progress is
        /// the focus instead). Older shapes show one summary each while there's room, survey objects one W × H.
        public static string LabelCount()
        {
            AirTools.UI.WorldLabels.Refresh();
            string summary = AirTools.UI.WorldLabels.Summary();
            bool pool = AirTools.UI.WorldLabels.Used <= AirTools.UI.WorldLabels.Max;
            bool full = true;
            string newest = "no tapes";
            var m = Measure;
            if (m != null)
            {
                MeasureShape last = null;
                for (int i = m.Shapes.Count - 1; i >= 0; i--) if (m.Shapes[i].IsTape) { last = m.Shapes[i]; break; }
                if (m.Equipped && m.Session.Count > 0 && !m.AgentDriving) newest = $"a tape in progress ({m.Session.Count} points) is the focus";
                else if (last != null)
                {
                    int want = MeasureView.FullLabelCount(last.Measurement);
                    int have = last.View != null ? last.View.ActiveLabelCount : 0;
                    full = last.Labels == LabelDetail.Full && have == want && m.FocusShape == last;
                    newest = $"newest #{last.Entry?.Id} {have}/{want} labels ({last.Labels})";
                }
                int summaries = m.Shapes.Count(s => s.IsTape && s.Labels == LabelDetail.Summary);
                int surveyed = m.Shapes.Count(s => !s.IsTape && s.Labels == LabelDetail.Full);
                newest += $"; shapes={m.Shapes.Count} summaries={summaries} survey_labelled={surveyed}/{m.Shapes.Count(s => !s.IsTape)}";
            }
            bool ok = pool && full;
            Log.Check("UI.labels", ok, $"{summary} | {newest} reshares={AirTools.UI.WorldLabels.Reshares}");
            return $"{(ok ? "PASS" : "FAIL")} {summary} | {newest}";
        }
        // end Declutter C

        // ---------------- real-controller aiming (Meta XR Operator) ----------------
        // The ISDK controller ray follows the OpenXR *grip* pose with a fixed offset (it points ~60° below the grip's
        // forward). Calibrate once with the grip at identity orientation, then ask for the grip orientation that points
        // the ray at a scene target. Operator poses are in tracking space = rig-local, OpenXR handedness (z flipped).

        static Vector3 s_RayDirAtIdentity;     // rig-local Unity
        static Vector3 s_RayOriginOffset;      // rig-local Unity, ray origin − grip position, at identity grip
        static bool s_Calibrated;

        static Transform Rig => Object.FindFirstObjectByType<OVRCameraRig>()?.transform;

        static Ray? RightControllerRay()
        {
            var src = Object.FindFirstObjectByType<OvrToolInputSource>();
            if (src == null || src.rightRays == null) return null;
            foreach (var r in src.rightRays)
                if (r != null && r.isActiveAndEnabled && r.State != Oculus.Interaction.InteractorState.Disabled) return r.Ray;
            return null;
        }

        /// Call right after setting the right grip to (gx,gy,gz) OpenXR with identity orientation.
        public static string CalibrateRightRay(float gx, float gy, float gz)
        {
            var rig = Rig; var ray = RightControllerRay();
            if (rig == null || ray == null) return "no rig / active controller ray";
            var grip = new Vector3(gx, gy, -gz);
            s_RayDirAtIdentity = rig.InverseTransformDirection(ray.Value.direction).normalized;
            s_RayOriginOffset = rig.InverseTransformPoint(ray.Value.origin) - grip;
            s_Calibrated = true;
            return $"dir={F(s_RayDirAtIdentity)} originOffset={F(s_RayOriginOffset)}";
        }

        /// OpenXR quaternion "x,y,z,w" for the right grip at (gx,gy,gz) (OpenXR, local_floor) so the controller ray
        /// passes through the scene-space target.
        public static string GripForTarget(float gx, float gy, float gz, float tx, float ty, float tz)
        {
            var rig = Rig;
            if (!s_Calibrated || rig == null) return "calibrate first";
            var grip = new Vector3(gx, gy, -gz);
            var target = rig.InverseTransformPoint(ToWorld(new Vector3(tx, ty, tz)));
            var q = Quaternion.identity;
            for (int i = 0; i < 4; i++) // the ray origin rotates with the grip; converge in a few steps
            {
                var origin = grip + q * s_RayOriginOffset;
                q = Quaternion.FromToRotation(s_RayDirAtIdentity, (target - origin).normalized);
            }
            // Unity → OpenXR quaternion: negate x and y.
            return $"{(-q.x).ToString("0.00000", C)},{(-q.y).ToString("0.00000", C)},{q.z.ToString("0.00000", C)},{q.w.ToString("0.00000", C)}";
        }

        /// Where the live controller ray currently hits (scene space, snapped) — to verify aiming before pressing.
        public static string RightRayHit()
        {
            var ray = RightControllerRay();
            if (ray == null) return "no active controller ray";
            if (!SnapService.TryRaySnap(ray.Value, out var hit)) return "ray hits nothing";
            return $"{F(ToRoot(hit.point))}({hit.kind}) raw={F(ToRoot(hit.rawPoint))}";
        }

        // Grok G4 — progress rails ("do the whole job") and the install coach
        /// Grok integration checks by lane: "g4" feeds recorded job runs and coach sessions through AgentActions.ExecuteAll
        /// and logs [AirTools.Check] G4.* lines (GrokRailsHarness). GrokStage("g4:<state>") puts one state on screen for a
        /// capture (job, job-done, recall, coach-stop, coach-check, coach-done, clear, look:0141, live-job, live-coach);
        /// GrokRailsStatus() prints the rails.
        public static string GrokStage(string what) =>
            (what ?? "").StartsWith("g4:") ? GrokRailsHarness.Stage(what.Substring(3)) : $"GrokStage: no lane in '{what}'";   // Grok G4

        public static string GrokRailsStatus() => GrokRailsHarness.Status();   // Grok G4
        // end Grok G4
        // Grok G1 ----------------------------------------------------------------------------------------------------

        /// The agent context the app would send with a command now (backend docs/api.md §6), as JSON; `text` as if typed
        /// (e.g. "what am I looking at?" attaches frame_jpg_b64, shown by size), null = a voice command.
        public static string GrokContext(string text = null) => GrokHarnessG1.Context(text);

        /// The context's settings, kept on the headset: location (default "Atlanta, GA"), address (empty = not sent),
        /// placement (this session only). Null leaves one as it is; "" clears it.
        public static string GrokSettings(string location = null, string address = null, string placement = null) =>
            GrokHarnessG1.Settings(location, address, placement);

        /// Grok lane checks, one [AirTools.Check] line each. "g1": the backend's example payloads through
        /// AgentActions.ExecuteAll (set_finish, add_note, a multi-action reply, a rider), the context, the notebook types
        /// and sync, the receipt extras, the recall line on the checkout panel (opened, never paid), the scan's revision
        /// and credit; "g1.status": the async halves (finish model swap, notebook upload). The other lanes' checks are
        /// found by class name when they're in the build ("g2" GrokG2Check, "g3" GrokG3Check, "g4" GrokRailsHarness;
        /// "<lane>.status" calls their Status()), so merging the lanes needs this one GrokCheck only.
        public static string GrokCheck(string lane = "g1")
        {
            string l = (lane ?? "g1").Trim().ToLowerInvariant();
            if (l == "g1") return GrokHarnessG1.Run();
            if (l == "g1.status") return GrokHarnessG1.Status();
            string key = l.Split('.')[0], method = l.EndsWith(".status") ? "Status" : "Run";
            string cls = key switch { "g2" => "GrokG2Check", "g3" => "GrokG3Check", "g4" => "GrokRailsHarness", _ => $"GrokHarness{key.ToUpperInvariant()}" };
            var type = typeof(AgentHarness).Assembly.GetType($"AirTools.Dev.{cls}");
            var m = type?.GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, System.Type.EmptyTypes, null);
            return m != null ? m.Invoke(null, null) as string : $"GrokCheck: no lane '{lane}' in this build (AirTools.Dev.{cls}.{method}())";
        }
        // Grok G3 (panels, cards, image / video quad, QR codes)
        /// Feeds lane `lane`'s example actions (copied from the backend's tests) through AgentActions.ExecuteAll and logs
        /// one [AirTools.Check] G3.<action> line each. Other lanes add their own case.

        /// The in-app QR encoder: reference codewords, format bits read back, texture size ([AirTools.Check] G3.qr.*).
        public static string QrSelfTest() => GrokG3Check.QrSelfTest();

        /// Press a G3 control like a poke / pinch: "tab money", "more", "flip", "open", "close", "link 0 1", "quad flip",
        /// "quad close", "safety", "hold" (see GrokG3Check.Press).
        public static string GrokPress(string what) => GrokG3Check.Press(what);

        /// What the G3 card and the reimagine quad show now.
        public static string GrokPanels()
        {
            var card = Services.Get<AirTools.Agent.Grok.GrokCard>();
            var quad = Services.Get<AirTools.Agent.Grok.ReimagineQuad>();
            return $"card: {(card == null ? "missing" : $"{card.Kind} open={card.IsOpen} {card.Summary}")}\nquad: {(quad == null ? "missing" : quad.Describe())}";
        }
        // end Grok G3

        // ---------------- scene parts (docs/api.md parts.r<rev>.json; ScenePartsCheck) ----------------
        // Load a package with parts first: BackendHarness.LoadSite("synthetic-facade-parts") (AirTools ▸ Export Synthetic
        // Scene Packages writes it) or the kitchen once it has a parts entry.

        /// What the streamer loaded and every component's nodes (in / out, label).
        public static string ScenePartsStatus() => ScenePartsCheck.Status();

        /// Every removable component: out (the three steps, the estimated label, no ghost collider, undo / redo), its
        /// cavity taped with the real tool (width, depth, height), back in. `keep` (default: the first one, cab1) stays
        /// out with its width tape for a capture; "" puts everything back. [AirTools.Check] parts.<id>.* and parts.summary.
        public static string PartsCheck(string keep = null, int seed = 1) => ScenePartsCheck.Run(keep, seed);

        /// Settings ▸ Take out: press chip i like a poke (opens Settings).
        public static string PartsChip(int i) => ScenePartsCheck.Chip(i);

        /// The voice actions, as the backend sends them: remove_component {component_id} / restore_component.
        public static string RemoveComponent(string id) => ScenePartsCheck.Act("remove_component", new Newtonsoft.Json.Linq.JObject { ["component_id"] = id });
        public static string RestoreComponent(string id) => ScenePartsCheck.Act("restore_component", new Newtonsoft.Json.Linq.JObject { ["component_id"] = id });

        /// place_part {part_id, model_url, fits, clearance_mm} without a pose (into the last cavity), or with `poseJson`
        /// ({"p": [x,y,z], "yaw_deg": 0}, glTF frame). Loads asynchronously: poll PartInfo().
        public static string PlacePart(string partId, string modelUrl = null, bool fits = true, float clearanceMm = 10f, string poseJson = null)
        {
            var args = new Newtonsoft.Json.Linq.JObject
            {
                ["part_id"] = partId, ["model_url"] = modelUrl ?? $"/parts/{partId}/model.glb", ["fits"] = fits,
                ["clearance_mm"] = new Newtonsoft.Json.Linq.JObject { ["w"] = clearanceMm, ["h"] = clearanceMm * 2f, ["d"] = clearanceMm * 1.5f },
            };
            if (!string.IsNullOrEmpty(poseJson)) args["pose"] = Newtonsoft.Json.Linq.JToken.Parse(poseJson);
            return ScenePartsCheck.Act("place_part", args);
        }

        /// The whole voice flow through AgentActions.ExecuteAll: job_started, remove_component, job_step, place_part,
        /// job_step, job_done (the status line's job strip shows the progress).
        public static string MeasureAndReplace(string componentId, string partId, string modelUrl = null) =>
            ScenePartsCheck.MeasureAndReplace(componentId, partId, modelUrl);

        // ---------------- e2e: remove → measure the gap → find one that fits → put it in → switch models (E2EHarness) ----------------

        /// The whole replace flow against the live backend, phrase by phrase through the command path (AgentClient →
        /// /agent/command → actions, LocalIntents where the server sent nothing): e.g.
        /// E2E("synthetic-facade-parts", "base cabinet", "countertop dishwasher") and
        /// E2E("kitchen", "dishwasher", null, true, 3, "the opening is 34 and a half inches tall") (the kitchen scan reads
        /// ~1.47× small: `scale` sets it from the gap first). `query`: what to search for (default: the component's
        /// label). Resets the demo first unless reset is false. Poll E2EResult().
        public static string E2E(string site = null, string component = null, string query = null, bool reset = true, int models = 3, string scale = null) =>
            E2EHarness.Start(new E2EHarness.Options { Site = string.IsNullOrEmpty(site) ? null : site, Component = string.IsNullOrEmpty(component) ? null : component,
                Query = string.IsNullOrEmpty(query) ? null : query, Reset = reset, Models = models, Scale = string.IsNullOrEmpty(scale) ? null : scale });

        /// PASS / FAIL / running…, then every phrase (reply, actions, via server | headset) and [AirTools.Check] e2e.* line.
        public static string E2EResult() => E2EHarness.Result();

        /// One phrase through the command path, like voice (the reply's actions, then LocalIntents): e.g. Say("next one").
        public static string Say(string text) => AppCommands.SendCommand(text) ? $"sent \"{text}\" — then E2EState()" : "no AgentClient";

        /// The replace flow's state: the gap, its tapes, the candidates, the model in the gap, the last local intent.
        public static string E2EState()
        {
            string gap = Gaps.TryGet(out var g) ? $"{g.Id} {g.Noun} {CavityFit.MmText(g.SizeMm)}{(Gaps.TryMeasured(g.Id, out var m) ? $" taped {F(m)} m" : "")}" : "none";
            var b = Services.Get<PartsBrowser>();
            return $"gap: {gap} | candidates: {(b != null ? string.Join(", ", b.Candidates.Select(c => c.id)) : "-")} | model: {ModelCycler.Current?.Spec?.id ?? "none"} " +
                   $"(candidate {ModelCycler.Index}, loading {ModelCycler.Pending}, {ModelCycler.Placements} placed) {ModelCycler.LastAction} | local: {AirTools.Agent.LocalIntents.LastRun} | agent: {Services.Get<AirTools.Agent.AgentClient>()?.LastReply}";
        }

        // autonomy: one sentence → the whole replace (backend server/replace_job.py), E2EHarness.Auto.cs
        /// The autonomous replace: says `sentence` through the real command path, waits for the server job's job_done (its
        /// actions arrive by JobRunPoller), then checks removed → taped → scale (if applied) → search with models → placed +
        /// fits → "next one" cycles → "put it back". E.g. E2EAuto("kitchen") or E2EAuto("kitchen", "the fridge is broken,
        /// find me a new one and put it in"). Poll E2EResult().
        public static string E2EAuto(string site = "kitchen", string sentence = null, bool reset = true, bool followUps = true) =>
            E2EHarness.StartAuto(new E2EHarness.AutoOptions { Site = string.IsNullOrEmpty(site) ? null : site, Reset = reset, FollowUps = followUps,
                Sentence = string.IsNullOrWhiteSpace(sentence) ? "replace the dishwasher with a new one that fits" : sentence });

        /// The same with the sentence spoken: a WAV through the voice path (/voice/command → transcript → the same routing),
        /// e.g. E2EAutoWav("kitchen", "SpikeData/prompts/replace-dishwasher.wav").
        public static string E2EAutoWav(string site, string wavPath, bool reset = true) =>
            E2EHarness.StartAuto(new E2EHarness.AutoOptions { Site = string.IsNullOrEmpty(site) ? null : site, WavPath = wavPath, Reset = reset });
        // end autonomy

        // ---------------- end e2e ----------------

        // measure-edges: "measure the length of the top roof from end to end" (backend measure_edges / equip_tool tape)
        /// After Say("measure the length of the top roof from end to end") on gt-lcc-canopy: what the structure tape did
        /// (SurveyRunner.LastMeasure), the last notebook row and the next tape's pending title.
        public static string MeasureEdgesState()
        {
            var r = Services.Get<AirTools.Agent.SurveyRunner>();
            var t = Services.Get<AirTools.Tools.MeasureTool>();
            var last = AirTools.Notes.Notebook.Last;
            return $"measure: {r?.LastMeasure ?? "no runner"} | notebook: {(last == null ? "empty" : $"#{last.Id} {last.DisplayTitle} {last.Label}")} | next tape title: {t?.NextLabel ?? "-"} | last action {AirTools.Agent.AgentActions.LastResult}";
        }
        // end measure-edges

        // assetgen ---------------- measure a window → find a frame that fits → generate it → put it in → make it to size (AssetE2E) ----------------

        /// The whole flow against the live backend: tape the window's height and width (the real Measure tool), "find a
        /// window frame that fits" through the command path, wait for the server's pipeline to build the best-sized
        /// candidate's model (logs who made it: Groq, or Grok), hold it off the opening (free) then at it (snaps in when it
        /// fits), Adjust ▸ Size & finish (W +, Fit to opening, the made-to-size model, a finish chip), save, undo ×3,
        /// redo ×3. [AirTools.Check] asset.*; poll AssetE2EResult(). `site`: "built-in" (default) = the synthetic facade
        /// (its window: 1.500 × 1.200 m; Main auto-loads the kitchen), "" = the loaded scene, else a package; `window`: a
        /// scan's structure-object id or "auto" (the
        /// house-window-sized rect nearest you): on zabel-gymnasium AssetE2E("zabel-gymnasium", "o7") tapes its
        /// 0.79 × 1.27 m window (all four sides have structure edges). Leaves the frame in adjust mode for the capture
        /// unless keepAdjusting is false.
        public static string AssetE2E(string site = "built-in", string window = null, bool reset = true, int seed = 1, bool keepAdjusting = true,
            string phrase = "find a window frame that fits") =>
            AirTools.Dev.AssetE2E.Start(new AirTools.Dev.AssetE2E.Options
            {
                Site = string.IsNullOrEmpty(site) ? null : site, Window = string.IsNullOrEmpty(window) ? null : window, Reset = reset, Seed = seed,
                KeepAdjusting = keepAdjusting, Phrase = string.IsNullOrWhiteSpace(phrase) ? "find a window frame that fits" : phrase,
            });

        /// PASS / FAIL / running…, then every step's [AirTools.Check] asset.* line and the phrases sent.
        public static string AssetE2EResult() => AirTools.Dev.AssetE2E.Result();

        /// The openings the notebook's tapes frame (Parts.Openings), and which one searches carry.
        public static string Openings() =>
            $"{AirTools.Parts.Openings.Report()}\ncurrent: {(AirTools.Parts.Openings.TryCurrent(out var o) ? o.ToString() : "none")}";

        /// The placed part's Size & finish: the size line, the look, the finish chips, and the last size / finish action.
        public static string SizeFinish()
        {
            var e = PlacementEd;
            if (e == null) return "no PlacementEditor";
            var p = e.Target();
            return p == null ? "no placed part" :
                $"{e.SizeLine(p)} | finish {p.LookFinish ?? "as listed"} | chips [{string.Join(", ", e.FinishChoices(p))}] | fit to opening {(e.CanFitToOpening ? "on" : "off")} " +
                $"| busy {e.LookBusy} requests {e.LookRequests} | {e.LastLook}";
        }

        /// Only AssetE2E's first step (the capture of the tapes): the facade window's height, then its width, taped with the
        /// real Measure tool (seeded aim noise); returns the opening they frame.
        public static string AssetTapes(int seed = 1) => AirTools.Dev.AssetE2E.TapeFacadeWindow(seed);

        /// Capture poses on the facade window (Play Without XR / Simulator: the rig is moved and turned so the eye is where
        /// the shot needs it): "tape" (2.6 m in front, level with the window: both tapes and their labels), "frame" (1.6 m
        /// out and 0.8 m to the side, a little above: the frame flush in the opening, its outline and fit callout),
        /// "panel" (2 m out, the window in view above the adjust panel; the panel re-opens in front of the new view).
        /// AssetLookReset() puts the rig back.
        public static string AssetLook(string shot = "frame") => AirTools.Dev.AssetE2E.Look(shot);
        public static string AssetLookReset() => AirTools.Dev.AssetE2E.LookReset();

        /// Size & finish like the panel's buttons: StepSize("w", +1), FitToOpening(), Finish("Bronze").
        public static string StepSize(string axis, int dir) { AppCommands.StepPlacementSize(axis, dir); return SizeFinish(); }
        public static string FitToOpening() { AppCommands.FitPlacementToOpening(); return SizeFinish(); }
        public static string Finish(string name) { AppCommands.SetPlacementFinish(string.IsNullOrEmpty(name) ? null : name); return SizeFinish(); }
        // end assetgen ---------------------------------------------------------------------------------------------------

        // modelview
        // ---------------- Model view: the model only, and the model switcher (ModelViewCheck) ----------------
        // The scans come from the backend: ServerConfig.SetOverride("http://127.0.0.1:8004") before Play (without it only
        // the built-in facade is listed).

        /// Model view on (the scene on the table, world text hidden, the switcher in front of it) or off (grow back in).
        public static string ModelView(bool on) => ModelViewCheck.ModelView(on);

        /// The switcher's models in order (the server's listing minus the test packages, then the built-in facade), the
        /// one on the table ([brackets]), loading / queued / failed, the fitted scale, and how many renderers Model view hides.
        public static string ModelSites() => ModelViewCheck.Status();

        /// show_model's path: a site id ("zabel-gymnasium"), a spoken name ("the gym", "GT tower", "facade"), or "next" /
        /// "previous". Enters Model view first; loads asynchronously (poll ModelSites()).
        public static string ShowModel(string site) => ModelViewCheck.Show(site);

        /// Press a card on the wheel like a pinch on it (modelwheel: a side card spins to the lens; the lens card opens).
        public static string ModelCard(string site)
        {
            var w = Services.Get<AirTools.Scene.ModelWheel>();
            var card = w != null ? w.CardFor(site) : null;
            if (card == null) return $"no card for {site} in view | {ModelViewCheck.Status()}";
            card.Press();
            return $"pressed {site} | {ModelViewCheck.Status()}";
        }

        /// World labels up → Model view → no visible world text → every listed model in turn (timeout each; the table
        /// model changes and fits ≤ 0.8 m; its card is highlighted) → back to the first → walk in at its spawn with the
        /// labels back. [AirTools.Check] model.*; poll ModelCheckResult().
        public static string ModelCheck(float timeoutPerSite = 90f) => ModelViewCheck.Run(timeoutPerSite);

        public static string ModelCheckResult() => ModelViewCheck.Result();

        /// Capture pose (Play Without XR): turn the rig about the eye to look at the model and the wheel under it
        /// (modelwheel: halfway between them, `pitchBias` degrees further down); ModelLookReset() undoes it.
        public static string ModelLook(float pitchBias = 0f) => ModelViewCheck.Look(pitchBias);
        public static string ModelLookReset() => ModelViewCheck.LookReset();
        // end modelview

        // modelwheel ------------------------------------------------------------------------------------------------------
        // Model view's wheel under the floating model (ModelWheel) and the placement (TabletopController). ModelCheck()
        // covers them ([AirTools.Check] model.front / model.wheel_under / model.wheel_flick / model.wheel_loops /
        // model.recentre_turn / model.recentre_chip).

        /// Spin the wheel `spin` models along (+ next, − previous; "Not downloaded" ones are skipped) and say where it heads.
        public static string ModelWheel(int spin) => ModelViewCheck.Spin(spin);

        /// Throw the wheel at `velocity` rad/s as if flicked (− brings the next models in from the right); poll ModelWheelState().
        public static string ModelWheelFlick(float velocity) => ModelViewCheck.Flick(velocity);

        /// Pinch the lens: open the model under it (loading → on view; poll ModelSites()).
        public static string ModelWheelPinch() => ModelViewCheck.Pinch();

        /// The wheel (the lens model and its preview, the visible cards, ticks, taps, opens) and where the model and the
        /// wheel are from the eye (ahead, height, yaw, span; the lens's angle below the eye line and the model's bottom).
        public static string ModelWheelState() => ModelViewCheck.WheelState();

        /// Recentre Model view in front of you now (the chip's path).
        public static string ModelRecentre() => ModelViewCheck.Recentre();

        /// Where Model view puts the model: "front" (default) or "table" (M7's real-table placement).
        public static string ModelPlacement(string where) => ModelViewCheck.Placement(where);

        /// Turn the rig `deg` about the eye (Play Without XR) to see the auto-recentre (> 60° for 0.75 s).
        public static string ModelTurn(float deg) => ModelViewCheck.Turn(deg);
        // end modelwheel --------------------------------------------------------------------------------------------------

        // placement -----------------------------------------------------------------------------------------------------
        // The placement editor (Parts/PlacementEditor): adjust a placed part in 6 DoF, save / load placements, swap models.
        // Load synthetic-facade-parts first for PlacementCheck: BackendHarness.LoadSite("synthetic-facade-parts").

        static PlacementEditor PlacementEd => PlacementEditor.Current != null ? PlacementEditor.Current : Services.Get<PlacementEditor>();

        /// The editor's state: adjusting, the part, its spot (cavity / surface / view), offset (mm), turn / tilt / roll,
        /// fit, slots (the active one in [ ]), steps, history, swaps.
        public static string Placement() => PlacementEd != null ? PlacementEd.Report() : "no PlacementEditor (AirTools ▸ Wire Main Scene)";

        /// Adjust mode on / off (AppCommands.AdjustPlacement) for the selected placed part.
        public static string Adjust(bool on) { AppCommands.AdjustPlacement(on); return Placement(); }

        /// Move (right, up, out; mm) and turn (turn, tilt, roll; degrees) in the part's frame (AppCommands.NudgePlacement).
        public static string Nudge(float dx, float dy, float dz, float yaw = 0f, float pitch = 0f, float roll = 0f)
        { AppCommands.NudgePlacement(dx, dy, dz, yaw, pitch, roll); return Placement(); }

        /// Save in slot `name` ("A"–"D"; null / "": the next free one).
        public static string SavePlacement(string name = null) { AppCommands.SavePlacement(string.IsNullOrEmpty(name) ? null : name); return Placement(); }

        /// Go to saved placement `name`.
        public static string LoadPlacement(string name) { AppCommands.LoadPlacement(name); return Placement(); }

        /// The next (+1) / previous (−1) candidate's model at the same placement (AppCommands.NextPlacedModel). Loads may
        /// be asynchronous (server models): poll Placement() for swap="…".
        public static string CycleModel(int delta = 1) { AppCommands.NextPlacedModel(delta); return Placement(); }

        /// The whole editor on synthetic-facade-parts: [AirTools.Check] placement.* lines (PlacementCheck). Leaves the AC
        /// in cab1's cavity at placement A in adjust mode for a capture (keepAdjusting).
        public static string PlacementCheck(bool keepAdjusting = true) => AirTools.Dev.PlacementCheck.Run(keepAdjusting);
        // end placement -------------------------------------------------------------------------------------------------

        // edit6dof ------------------------------------------------------------------------------------------------------
        // The Edit view (Parts/EditView, docs/edit-view.md): a part's isolated edit and its context menu. Load
        // synthetic-facade-parts first for EditViewCheck: BackendHarness.LoadSite("synthetic-facade-parts").

        static EditView EditV => EditView.Current != null ? EditView.Current : Services.Get<EditView>();

        /// The view's state: phase, kind, part, shown scale, readout, shade, hover / press, dim, copy, bar, menu, counters.
        public static string EditState() => EditV != null ? EditV.Report() : "no EditView (AirTools ▸ Wire Main Scene)";

        /// Open the Edit view on the selected placed part (AppCommands.EditPart).
        public static string EditOpen() { AppCommands.EditPart(); return EditState(); }

        /// One step on an arrow by name: TiltUp TiltDown TurnLeft TurnRight RollLeft RollRight Up Down Left Right Out In.
        /// edit-touch: poked through the knob's own GlassButton (the path a fingertip takes).
        public static string EditTap(string arrow) { if (EditV != null) AirTools.Dev.EditViewCheck.Poke(EditV, arrow); return EditState(); }

        // edit-touch ----------------------------------------------------------------------------------------------------
        /// Poke any Edit view control by name through its GlassButton: an arrow ("TiltUp"), a panel button ("Save",
        /// "Swatch5", "Step", "Shade", "Reset", "FitToOpening", "Cancel", "Move", "Place"), the move bar's ("bar:Save",
        /// "bar:Cancel") or the context menu's ("menu:Edit", "menu:Similar", "menu:Delete", "menu:Undo").
        public static string EditPoke(string name)
        {
            bool ok = EditV != null && AirTools.Dev.EditViewCheck.Poke(EditV, name);
            return $"poke {name}: {(ok ? "pressed" : "no such control showing")} | {EditState()}";
        }

        /// Poke arrow `arrow` and keep the finger in for `seconds` on the view's clock (it repeats after 0.4 s, then every
        /// 0.12 s). `release` false leaves it held (the knob inked, the pill up) for a capture; EditHoldEnd() lets go.
        public static string EditHold(string arrow, float seconds, bool release = true)
        {
            var v = EditV;
            if (v == null || !AirTools.Dev.EditViewCheck.Poke(v, arrow)) return $"no arrow '{arrow}' to hold | {EditState()}";
            float t = Time.unscaledTime;
            v.Clock = () => t;
            v.SimulateHold(true);
            for (float s = 0f; s < seconds; s += 0.05f) { t += 0.05f; v.Tick(0.05f); }
            if (release) { v.SimulateHold(false); t += 0.05f; v.Tick(0.05f); v.Clock = null; }
            return EditState();
        }

        /// Let go of an arrow left held by EditHold(…, release: false).
        public static string EditHoldEnd()
        {
            var v = EditV;
            if (v == null) return EditState();
            v.SimulateHold(false);
            v.Clock = null;
            v.Tick(0.02f);
            return EditState();
        }

        /// Where the view's controls are from the eye: every enabled arrow's distance, the panel's and the menu's (m).
        public static string EditReach()
        {
            var v = EditV;
            var cam = v != null && v.head != null ? v.head : Camera.main != null ? Camera.main.transform : null;
            if (v == null || cam == null || v.arrows == null) return EditState();
            var sb = new System.Text.StringBuilder();
            v.arrows.Tick();
            for (int i = 0; i < v.arrows.Count; i++)
                if (v.arrows.Enabled[i]) sb.Append($"{EditViewMath.Arrows[i].Name} {Vector3.Distance(cam.position, v.arrows.World[i]):0.000} m; ");
            if (v.panel != null && v.panel.root != null) sb.Append($"panel {Vector3.Distance(cam.position, v.panel.root.transform.position):0.000} m, {(cam.position.y - v.panel.root.transform.position.y) * 100f:0} cm down; ");
            if (v.menu != null && v.menu.Showing) sb.Append($"menu {Vector3.Distance(cam.position, v.menu.transform.position):0.000} m; ");
            sb.Append($"stage {Vector3.Distance(cam.position, v.stage.position):0.000} m, {(cam.position.y - v.stage.position.y) * 100f:0} cm down, shown {v.ShownScale * 100f:0}%");
            return sb.ToString();
        }
        // end edit-touch ------------------------------------------------------------------------------------------------

        /// Swatch i (0 Original, 1 Stainless, 2 Black, 3 White, 4 Slate, 5 Navy, 6 Red, 7 Bronze, 8 Wood).
        public static string EditSwatch(int i) { EditV?.SetSwatch(i); return EditState(); }

        public static string EditSave() { AppCommands.SaveEdit(); return EditState(); }
        public static string EditCancel() { AppCommands.CancelEdit(); return EditState(); }
        public static string EditMove() { EditV?.StartMove(); return EditState(); }
        public static string EditPlace() { EditV?.Place(); return EditState(); }

        /// The context menu on the selected placed part (what the grip squeeze / long pinch open).
        public static string EditMenu() { var t = PartTool; if (EditV != null && t != null) EditV.OpenMenu(t.Selected, "harness"); return EditState(); }

        /// Where arrow `arrow` is (world and scene-root space) — to aim the Operator's controller ray at it.
        public static string EditKnob(string arrow)
        {
            var v = EditV;
            int i = EditView.ArrowIndex(arrow);
            if (v == null || v.arrows == null || i < 0) return $"no arrow '{arrow}' | {EditState()}";
            v.arrows.Tick();
            var w = v.arrows.World[i];
            return $"{arrow}: world {w.ToString("F3")} root {ToRoot(w).ToString("F3")} enabled={v.arrows.Enabled[i]}";
        }

        /// Every path of the Edit view on synthetic-facade-parts: [AirTools.Check] editview.* lines (EditViewCheck). Leaves
        /// the AC open in the Edit view, the world dimmed, for a capture (leaveOpen).
        public static string EditViewCheck(bool leaveOpen = true) => AirTools.Dev.EditViewCheck.Run(leaveOpen);
        // end edit6dof --------------------------------------------------------------------------------------------------

        // scalemodels ---------------------------------------------------------------------------------------------------
        // Per-site default scale (SiteScales: the kitchen at ×1.63) and the models on the headset (the kept listing, the
        // background prefetch, the switcher with the laptop away). Checks are [AirTools.Check] scale.* / models.* lines.

        /// The kitchen (loaded if it isn't): ×1.63 as the default, the context's scale / scale_source, Settings and the wrist
        /// strip, the dw1 gap ≈ 712 × 963 × 725 mm and the tape across it, a Set scale override, Reset back to ×1.63. Poll
        /// ScaleModelsResult().
        public static string ScaleCheck() => ScaleModelsCheck.ScaleCheck();

        /// The laptop gone (ServerConfig.SetOverride("http://10.255.255.1:8004")): the listing fails, the switcher still lists
        /// every model on the headset (the rest "Not downloaded"), and `load` opens a cached one (the kitchen) from the
        /// headset. The server is put back at the end. Poll ScaleModelsResult().
        public static string ModelsOffline(bool load = true) => ScaleModelsCheck.ModelsOffline(load);

        public static string ScaleModelsResult() => ScaleModelsCheck.Result();

        /// Capture setup: the palm ring forced open in front of the camera with Settings (the gear) on the lens; `window`
        /// opens the Settings window too (SettingsLookAt() turns the view to it). SettingsLookReset() undoes it.
        public static string SettingsLook(bool window = false) => ScaleModelsCheck.SettingsLook(window);
        public static string SettingsLookAt() => ScaleModelsCheck.SettingsLookAt();
        public static string SettingsLookReset() => ScaleModelsCheck.SettingsLookReset();

        /// The scale now: "kitchen ×1.6300 site_default (default ×1.63)".
        public static string Scale()
        {
            var st = Services.Get<SceneStreamer>(); var r = Root;
            if (st == null || r == null) return "no SceneStreamer / SceneRoot";
            return $"{(r.IsRuntimePackage ? r.Site : "built-in")} ×{r.Calibration.ToString("0.0000", C)} {SiteScales.Wire(st.ScaleSource)} " +
                   $"(default ×{SiteScales.Factor(st.SiteDefaultScale)}) · \"{AirTools.Structure.ScenePanel.ScaleChip(r)}\"";
        }

        /// The models the headset knows, which are on it, the prefetch, and the switcher.
        public static string Models()
        {
            var st = Services.Get<SceneStreamer>();
            if (st == null) return "no SceneStreamer";
            st.HeadsetCount(out int on, out int total);
            var sites = st.KnownSites.ConvertAll(k => $"{k.site}{(st.OnHeadset(k.site, out int rev) ? $"✓r{rev}" : "")}");
            return $"online={st.ServerListed} {PrefetchPlan.CountText(on, total)} known=[{string.Join(", ", sites)}] cache={SceneStreamer.CacheDirectory}" +
                   $"{(Services.TryGet<ScenePrefetcher>(out var pf) ? " · " + pf.Describe() : "")}" +
                   $"{(Services.TryGet<ModelSwitcher>(out var sw) ? " · " + sw.Describe() : "")}";
        }
        // end scalemodels -----------------------------------------------------------------------------------------------

        // glass ---------------------------------------------------------------------------------------------------------------
        // Liquid glass on every UI surface (docs/UI.md §3, GlassCheck).

        /// The glass census of the live UI: roles, liquid surfaces / flat marks, text, the ring, pictures, and any stray
        /// background or surface off its role's shared material; whether Lite or high contrast is on.
        public static string GlassState() => GlassCheck.State();

        /// The headset frame-time A/B: true draws every AirTools/Glass surface as the flat D3 glass (AIRTOOLS_GLASS_LITE; the
        /// ring is unchanged), false the liquid glass. Logs "Glass: lite" / "Glass: liquid" for tools/demo/fps.py.
        public static string GlassLite(bool on) => GlassCheck.Lite(on);

        /// Capture setups: "settings" · "parts" · "notebook" · "toast" (a toast over an open window) · "wrist" · "ring" ·
        /// "model" — opens it and turns the rig about the eye to centre it. The Adjust panel: AssetLook("panel").
        public static string GlassLook(string shot) => GlassCheck.Look(shot);
        public static string GlassLookReset() => GlassCheck.LookReset();

        /// The gate's toast-over-Adjust bug: rail off, the notebook open, a toast → it docks wholly above the window's rim at
        /// ≥ 16 dmm. [AirTools.Check] glass.dock.*; poll GlassDockResult().
        public static string GlassDock() => GlassCheck.ToastDockCheck();
        public static string GlassDockResult() => GlassCheck.Result();
        // end glass -----------------------------------------------------------------------------------------------------------

        // settings-assets: Settings ▸ Talk (Hold / Toggle / Auto) and 3D models (HF / LLM+CAD / Auto) ---------------------
        // The 3D models need the backend with asset modes: ServerConfig.SetOverride("http://127.0.0.1:8009") before Play.

        /// Talk: "hold" | "toggle" | "auto" (the Settings chip's path; saved outside DemoMode). The Talk buttons follow.
        public static string TalkStyle(string style)
        {
            var s = UserPrefs.ParseTalk(style);
            if (s == null) return $"not a talk style: {style} (hold | toggle | auto)";
            UserPrefs.Talk = s.Value;
            return $"talk={UserPrefs.Label(UserPrefs.Talk)} | {BackendHarness.TalkButtons()}";
        }

        /// The pure Talk-style checks (talk.* lines; no Play mode needed).
        public static string TalkStyleCheck() => BackendHarness.TalkStyleCheck();

        /// 3D models: "hf" | "llm_scad" | "auto" (the Settings chip's path): placed and held server parts rebuild in that
        /// mode. Poll AssetModeState().
        public static string AssetMode(string mode)
        {
            UserPrefs.Assets = UserPrefs.ParseAssetMode(mode);
            return AssetModeState();
        }

        /// The setting, the switcher and every server part's model (mode, badge, made_by, note, anchor).
        public static string AssetModeState() => AirTools.Dev.AssetModeCheck.State();

        /// The card's Compare on the selected part (HF ⇄ LLM+CAD in place). Poll AssetModeState().
        public static string AssetCompare()
        {
            var s = Services.Get<AssetModeSwitcher>();
            if (s == null) return "no AssetModeSwitcher (run AirTools ▸ Wire Main Scene)";
            return $"{(s.Compare() ? "comparing" : "not started")} | {AssetModeState()}";
        }

        /// The whole check (assetmode.* + talk.* lines): the placed server part (or `partId` placed into the gap of the part
        /// taken out last) → HF → LLM+CAD → Compare twice, anchor kept each time; `twin`: a second copy in HF in front of it.
        /// Poll AssetModeResult().
        public static string AssetModeCheck(string partId = null, bool twin = false) => AirTools.Dev.AssetModeCheck.Run(partId, twin);
        public static string AssetModeResult() => AirTools.Dev.AssetModeCheck.Result();

        /// A capture pose on the checked part (and its twin); AssetModeLookReset() puts the rig back.
        public static string AssetModeLook(float distance = 0f) => AirTools.Dev.AssetModeCheck.Look(distance);
        public static string AssetModeLookReset() => AirTools.Dev.AssetModeCheck.LookReset();
        // end settings-assets ---------------------------------------------------------------------------------------------

        // cad: LLM+CAD → Grok's CAD model while placed -------------------------------------------------------------------
        /// The placed server part (or partId, placed into the last take-out gap) in LLM+CAD: the template with "Grok is
        /// writing the CAD model… N s", then the CAD model swapped in place (tier scad, "CAD · Grok 4.20 · OpenSCAD", same
        /// anchor). Poll AssetModeResult().
        public static string AssetCadCheck(string partId = null, float timeout = 240f) => AirTools.Dev.AssetModeCheck.CadRun(partId, timeout);

        /// The CAD watches: parts waiting for their CAD model, polls, upgrades.
        public static string AssetCadState()
        {
            var s = Services.Get<AssetModeSwitcher>();
            return s == null ? "no AssetModeSwitcher (run AirTools ▸ Wire Main Scene)" : s.DescribeCad();
        }
        // end cad --------------------------------------------------------------------------------------------------------

        // sitescope -----------------------------------------------------------------------------------------------------
        // Every world item belongs to the site it was made in (SiteScope): another site's tapes, parts, pins and Grok layers
        // are parked (hidden, colliders off, out of the undo stacks and the context) and come back with their site.
        // Needs the backend's kitchen and Zabel gym: ServerConfig.SetOverride("http://127.0.0.1:8004") before Play.

        /// Kitchen: dw1 out, its gap taped, a catalog part in it, a tape → the gym (a census: none of it draws or
        /// collides) → a gym tape → the kitchen again (the same poses, the gym tape hidden) → Undo takes the kitchen's
        /// newest only → Redo. `modelView`: switch with show_model (Model view on the table) instead of LoadSite.
        /// [AirTools.Check] sitescope.*; poll SiteScopeResult().
        public static string SiteScopeCheck(bool modelView = false) => AirTools.Dev.SiteScopeCheck.Run(modelView);

        public static string SiteScopeResult() => AirTools.Dev.SiteScopeCheck.Result();

        /// The site, and every owner's live and parked counts.
        public static string SiteScopeStatus() => AirTools.Dev.SiteScopeCheck.Status();
        // end sitescope -------------------------------------------------------------------------------------------------

        // delete-undo ---------------------------------------------------------------------------------------------------
        // Removing a placed part is one undoable step (the spec card's Remove, the context menu's Delete, voice delete_part,
        // an array member, Clear). On the built-in facade; force the palm open in an eval before
        // (Services.Get<PalmMenu>().Force(true)) so the spec card's own Remove button (tap twice) is pressed.

        /// Place → remove (spec card, menu, voice) → Undo restores pose / finish / fit / selection / row → Redo; an array
        /// member; Clear; a drop. [AirTools.Check] deleteundo.* lines. Leaves the eight-hanger array on the fascia.
        public static string DeleteUndoCheck() => AirTools.Dev.DeleteUndoCheck.Run();

        /// The part tool's census (placed, removed kept for Undo, steps) and whether the last run's dropped part is destroyed.
        public static string DeleteUndoStatus() => AirTools.Dev.DeleteUndoCheck.Status();

        /// Clear every placed part as one undo step (the ring's Undo brings them back).
        public static string ClearParts() => $"{AppCommands.ClearParts()} cleared | {Parts()}";
        // end delete-undo -----------------------------------------------------------------------------------------------

        static Vector3 ToWorld(Vector3 local) => Root != null ? Root.transform.TransformPoint(local) : local;
        static Vector3 ToRoot(Vector3 world) => Root != null ? Root.transform.InverseTransformPoint(world) : world;
        static string F(Vector3 v) => $"({v.x.ToString("0.000", C)}, {v.y.ToString("0.000", C)}, {v.z.ToString("0.000", C)})";
    }
}
#endif
