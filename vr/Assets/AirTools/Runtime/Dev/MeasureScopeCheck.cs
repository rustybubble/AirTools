#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Core;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Structure;
using AirTools.Tools;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Dev
{
    /// switchclean: measurements are fully world-model specific (the user, Sat 09-26: "measurements and lines that we drew
    /// are still visible across different world models … measurements should be fully world model specific"). Through
    /// AgentHarness.MeasureScopeCheck(modelView); needs the backend's kitchen and Zabel gym (ServerConfig.SetOverride
    /// ("http://127.0.0.1:8004") before Play). Every result is an [AirTools.Check] measurescope.* line; MeasureScopeResult()
    /// has the report.
    /// 1. **The kitchen (World).** Every measurement-like kind drawn by its real owner: a tape, an area, a B1 survey object,
    ///    a check_slope tape, a measure_edges tape (SurveyRunner.MeasureSegments on a structure edge), the gap's three tapes
    ///    (dw1 out, CavityTapes), a taped opening (a width + a height tape), a level, a ladder (and the fall edges it turns
    ///    on), a placed part (outline and callout) in Adjust (the placement guides), an answer pin, a Grok layer (the
    ///    fixture survey's pins), the install coach's boxes, and last a tape started (one point: mid-draw) with Measure in
    ///    hand. A kind that can't be made on this scan is reported as skipped with why.
    /// 2. **The gym** (LoadSite, or `modelView`: show_model's wheel path, the gym on the table). Per kind, a strict census
    ///    of the kitchen's objects: renderers that are active and enabled (Model view's forceRenderingOff is NOT counted as
    ///    hidden: lines and dots count too) and colliders; none may remain. Also: no kitchen shape / level / ladder / part /
    ///    pin is live (none snappable or grabbable), no opening is built from a kitchen tape, the label pool holds no kitchen
    ///    producer, the notebook panel lists no kitchen row, the snap cursor / rubber band / level ghost were cleared at the
    ///    switch, the mid-draw tape was parked (not carried), and a generic census of every annotation renderer that drew
    ///    on the kitchen (ModelViewDeclutter.CollectRoots) finds none drawing.
    /// 3. **Undo right after the switch** never reveals a kitchen item (then Redo puts back what it took).
    /// 4. **The kitchen again:** every kind is back (drawing) and the tape in progress is back with its one point.
    /// 5. **The gym again** (the capture state: the gym after drawing on the kitchen): the census once more.
    public static class MeasureScopeCheck
    {
        public const string Kitchen = SiteScopeCheck.Kitchen, Gym = SiteScopeCheck.Gym;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly List<string> s_Report = new List<string>();
        static string s_Summary = "not run";
        static bool s_Done = true;
        static int s_Pass, s_Total;

        static SceneStreamer Streamer => Services.Get<SceneStreamer>();
        static SceneRoot Root => Services.Get<SceneRoot>();
        static MeasureTool Measure => Services.Get<MeasureTool>();
        static LevelTool Level => Services.Get<LevelTool>();
        static LadderTool Ladder => Services.Get<LadderTool>();
        static PartTool Parts => Services.Get<PartTool>();

        public static string Result() => $"{(s_Done ? "" : "(running) ")}{s_Summary}\n{string.Join("\n", s_Report)}";

        /// One kind of measurement-like item drawn on the kitchen: the objects that must neither draw nor collide on
        /// another model, what was made, or why it couldn't be.
        sealed class Kind
        {
            public readonly string Name;
            public readonly List<GameObject> Roots = new List<GameObject>();
            public string Made = "";
            public bool Skipped;
            public bool ComesBack = true;
            public Kind(string name) { Name = name; }
            public void Add(GameObject go) { if (go != null && !Roots.Contains(go)) Roots.Add(go); }
            public void Add(Component c) { if (c != null) Add(c.gameObject); }
            public void Skip(string why) { Skipped = true; Made = why; }
        }

        static void Check(string id, bool ok, string detail)
        {
            s_Total++;
            if (ok) s_Pass++;
            Log.Check(id, ok, detail);
            s_Report.Add($"{(ok ? "PASS" : "FAIL")} {id} {detail}");
        }

        static void Note(string line)
        {
            Log.Info($"MeasureScopeCheck: {line}");
            s_Report.Add($"     {line}");
        }

        static IEnumerator Until(System.Func<bool> done, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!done() && Time.realtimeSinceStartup < end) yield return null;
        }

        public static string Run(bool modelView = false, float timeout = 90f)
        {
            if (!s_Done) return "MeasureScopeCheck is already running: poll MeasureScopeResult()";
            if (Streamer == null || Root == null || Measure == null || Parts == null) return "no SceneStreamer / SceneRoot / MeasureTool / PartTool (AirTools ▸ Wire Main Scene)";
            s_Report.Clear();
            s_Pass = s_Total = 0;
            s_Summary = "MeasureScopeCheck running";
            s_Done = false;
            if (!DemoRunner.Run(Routine(modelView, timeout), ex => { s_Summary = $"MeasureScopeCheck error {ex.GetType().Name}: {ex.Message}"; s_Done = true; Unhook(); },
                    () => { s_Summary = $"MeasureScopeCheck {s_Pass}/{s_Total}"; s_Done = true; Unhook(); }))
            {
                s_Done = true;
                return "another routine is running (DemoRunner busy)";
            }
            return "MeasureScopeCheck started: poll MeasureScopeResult()";
        }

        // ---------------- loads ----------------

        static bool Loaded(string site) =>
            Root.IsRuntimePackage && Root.Site == site && !Streamer.Loading && SiteScope.IsCurrent(site) && !SiteScope.Pending;

        static IEnumerator Load(string site, bool modelView, float timeout)
        {
            if (Loaded(site)) yield break;
            if (modelView) ModelViewCheck.Show(site);
            else { if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest(); AppCommands.LoadSite(site); }
            yield return null;
            yield return Until(() => Loaded(site), timeout);
            var sp = Streamer.Parts;
            yield return Until(() => sp == null || sp.Site == site || !Streamer.PartsStatus.StartsWith("split", System.StringComparison.Ordinal), 15f);
            yield return null;
            yield return null;   // LateUpdate followers (the coach's boxes, the guides, fall edges) have run
        }

        // ---------------- what the switch looked like (SiteScope hooks) ----------------

        static bool s_Watching, s_LeavingSeen, s_ArrivedSeen;
        static int s_SessionAtLeaving = -1, s_SessionAtArrival = -1, s_PreviewPointsAtArrival = -1;
        static bool s_CursorAtArrival, s_LevelGhostAtArrival, s_NextLabelAtArrival;
        static string s_CurrentAtLeaving;

        static void Hook()
        {
            Unhook();
            s_Watching = true; s_LeavingSeen = s_ArrivedSeen = false;
            SiteScope.Leaving += OnLeaving;
            SiteScope.Changed += OnChanged;
        }

        static void Unhook()
        {
            s_Watching = false;
            SiteScope.Leaving -= OnLeaving;
            SiteScope.Changed -= OnChanged;
        }

        static void OnLeaving(string from, string to)
        {
            if (!s_Watching || s_LeavingSeen) return;
            s_LeavingSeen = true;
            s_CurrentAtLeaving = SiteScope.Current;
            s_SessionAtLeaving = Measure != null ? Measure.Session.Count : -1;
        }

        static void OnChanged(string from, string to)
        {
            if (!s_Watching || s_ArrivedSeen) return;
            s_ArrivedSeen = true;
            var m = Measure;
            s_SessionAtArrival = m != null ? m.Session.Count : -1;
            s_CursorAtArrival = m != null && m.Cursor.HasValue;
            s_NextLabelAtArrival = m != null && m.NextLabel != null;
            var preview = m != null && m.viewRoot != null ? m.viewRoot.Find("MeasurePreview") : null;
            var pv = preview != null ? preview.GetComponent<MeasureView>() : null;
            s_PreviewPointsAtArrival = pv != null ? pv.PointCount : 0;
            var ghost = Level != null && Level.viewRoot != null ? Level.viewRoot.Find("LevelGhost") : null;
            s_LevelGhostAtArrival = ghost != null && ghost.gameObject.activeInHierarchy;
        }

        // ---------------- census ----------------

        /// Renderers that are active and enabled under `roots` (strict: Model view's forceRenderingOff still counts — a
        /// line hidden only because the model is on the table would show once you step in), how many of those Model view
        /// forces off, and colliders that collide.
        static (int drawing, int forced, int colliders) Census(IEnumerable<GameObject> roots)
        {
            int r = 0, f = 0, c = 0;
            var rs = new List<Renderer>();
            var cs = new List<Collider>();
            foreach (var go in roots)
            {
                if (go == null) continue;
                go.GetComponentsInChildren(true, rs);
                foreach (var x in rs) if (x != null && x.enabled && x.gameObject.activeInHierarchy) { r++; if (x.forceRenderingOff) f++; }
                go.GetComponentsInChildren(true, cs);
                foreach (var x in cs) if (x != null && x.enabled && x.gameObject.activeInHierarchy) c++;
            }
            return (r, f, c);
        }

        /// Objects the live tools redraw from the pointer (the rubber band and its cursor dot, the save chip, the level
        /// and ladder ghosts, the survey's progress): they may draw on the gym, for the gym. Checked by content instead.
        static readonly string[] s_Live = { "MeasurePreview", "FinishChip", "LevelGhost", "LadderGhost", "SurveyProgress" };

        static bool UnderLive(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
                foreach (var n in s_Live) if (p.name == n) return true;
            return false;
        }

        static string PathOf(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        /// Every annotation renderer drawing now under the owners' roots (ModelViewDeclutter's list) and the tools' view
        /// roots, the live tools' own objects left out.
        static HashSet<Renderer> AnnotationRenderers()
        {
            var set = new HashSet<Renderer>();
            var roots = new List<Transform>();
            if (Services.TryGet<ModelViewDeclutter>(out var d)) d.CollectRoots(roots);
            if (Measure != null && Measure.viewRoot != null) roots.Add(Measure.viewRoot);
            if (Level != null && Level.viewRoot != null) roots.Add(Level.viewRoot);
            if (Ladder != null && Ladder.viewRoot != null) roots.Add(Ladder.viewRoot);
            if (Services.TryGet<FallEdges>(out var fe) && fe.AnnotationRoot != null) roots.Add(fe.AnnotationRoot);
            var rs = new List<Renderer>();
            foreach (var t in roots)
            {
                if (t == null) continue;
                t.GetComponentsInChildren(true, rs);
                foreach (var r in rs) if (r != null && r.enabled && r.gameObject.activeInHierarchy && !UnderLive(r.transform)) set.Add(r);
            }
            return set;
        }

        // ---------------- drawing on the kitchen ----------------

        static MeasureShape Shape(IList<Vector3> rootPoints, SurveyTag tag = null, bool area = false)
        {
            var m = Measure; var root = Root.transform;
            if (m.Session.Count > 0) m.CancelSession();
            bool prevArea = m.AreaMode;
            m.AreaMode = area || tag != null;   // a tagged tape is finished with its tag (D4 would save it untagged at point 2)
            int before = m.Shapes.Count;
            foreach (var p in rootPoints)
                m.Click(new SurfaceHit { point = root.TransformPoint(p), rawPoint = root.TransformPoint(p), kind = SnapKind.Face });
            if (m.Session.Count >= MeasureMath.MinPoints) m.Finish(tag);
            if (m.Session.Count > 0) m.CancelSession();
            m.AreaMode = prevArea;
            return m.Shapes.Count > before ? m.Shapes[m.Shapes.Count - 1] : null;
        }

        static void AddShape(Kind k, MeasureShape s, string what)
        {
            if (s == null || s.View == null) { k.Skip($"{what}: not saved ({Measure.LastAction})"); return; }
            k.Add(s.View);
            k.Made = $"{what} #{s.Entry?.Id} \"{s.Entry?.Label}\"";
        }

        static IEnumerator Routine(bool modelView, float timeout)
        {
            var root = Root; var measure = Measure; var parts = Parts;
            // 1. The kitchen, in the world (the tools draw there).
            yield return Load(Kitchen, false, timeout);
            if (!Loaded(Kitchen)) { Check("measurescope.kitchen.loaded", false, $"the kitchen didn't load: {Streamer.Status}"); yield break; }
            if (AppState.Mode != AppMode.World) { if (AppState.Mode == AppMode.Tabletop) AppCommands.SetTabletop(false); else AppCommands.OpenChest(); }
            yield return Until(() => AppState.Mode == AppMode.World, 10f);
            AppCommands.EquipTool("measure");

            // Where to draw: the dishwasher's insert (floor, front, centre of its opening), facing out of it.
            Vector3 insert = new Vector3(0f, 0.9f, 1.5f), outDir = Vector3.forward;
            Quaternion facing = Quaternion.identity;
            if (AppCommands.TryPlaceTarget(new PlacePartArgs { ComponentId = "dw1" }, root, out var target, out _))
            {
                insert = target.Anchor; facing = target.Rotation; outDir = facing * Vector3.forward;
            }
            var right = Vector3.Cross(Vector3.up, outDir).normalized;
            Vector3 P(float r, float u, float o) => insert + right * r + Vector3.up * u + outDir * o;

            var kinds = new List<Kind>();
            Kind New(string name) { var k = new Kind(name); kinds.Add(k); return k; }

            // Tapes and areas.
            var tape = New("tape"); AddShape(tape, Shape(new[] { P(-0.4f, 1.2f, 0.02f), P(0.4f, 1.2f, 0.02f) }), "tape");
            var area = New("area"); AddShape(area, Shape(new[] { P(-0.4f, 0.01f, 0.4f), P(0.4f, 0.01f, 0.4f), P(0.4f, 0.01f, 1.0f), P(-0.4f, 0.01f, 1.0f) }, area: true), "area");
            var survey = New("survey");
            AddShape(survey, Shape(new[] { P(0.7f, 0.1f, 0.01f), P(1.1f, 0.1f, 0.01f), P(1.1f, 0.7f, 0.01f), P(0.7f, 0.7f, 0.01f) },
                new SurveyTag { RequestId = "scope-survey", ObjectId = "o1", Label = "cabinet_door" }), "B1 survey object");
            var slope = New("check_slope");
            AddShape(slope, Shape(new[] { P(-0.6f, 0.92f, -0.2f), P(0.6f, 0.93f, -0.2f) },
                new SurveyTag { RequestId = "scope-slope", ObjectId = "e-scope", Label = "counter slope", Tape = true }), "check_slope tape");

            // measure_edges: the real path (SurveyRunner.MeasureSegments) on one of the scan's structure edges.
            var edges = New("measure_edges");
            var runner = Services.Get<SurveyRunner>();
            var layer = root.Structure;
            int edgeIndex = -1;
            if (layer != null && layer.Edges != null)
                for (int i = 0; i < layer.Edges.Length && edgeIndex < 0; i++)
                {
                    float len = Vector3.Distance(layer.Edges[i].a, layer.Edges[i].b) * root.Calibration;
                    if (len > 0.3f && len < 4f) edgeIndex = i;
                }
            if (runner != null && edgeIndex >= 0)
            {
                var args = new MeasureEdgesActions.Args { Label = "Scope edge length", RequestId = "scope-edges", What = "length" };
                args.A.Add(layer.Edges[edgeIndex].a); args.B.Add(layer.Edges[edgeIndex].b);
                int taped = runner.MeasureSegments(args);
                if (taped > 0) AddShape(edges, runner.LastMeasureShape, $"measure_edges on {layer.Edges[edgeIndex].id}");
                else edges.Skip($"measure_edges: {runner.LastMeasure}");
            }
            if (edges.Roots.Count == 0)
            {
                // No structure edge here: the same labelled tape the agent's equip_tool {tape, label} makes.
                measure.NextLabel = "Scope edge length";
                AddShape(edges, Shape(new[] { P(-0.5f, 1.5f, 0.02f), P(0.5f, 1.5f, 0.02f) }), "labelled tape (measure_edges' equip_tool path)");
                edges.Skipped = false;
            }

            // The gap's tapes: dw1 out, CavityTapes (three tapes, one request).
            var gapTapes = New("gap tapes");
            var sceneParts = Streamer.Parts;
            bool gapOpen = sceneParts != null && sceneParts.HasParts && (sceneParts.IsRemoved("dw1") || AppCommands.RemoveComponent("dw1"));
            yield return null;
            if (gapOpen && Gaps.TryGet(out var gap, "dw1"))
            {
                int before = measure.Shapes.Count;
                int n = CavityTapes.Run(gap, out _, out string detail);
                for (int i = before; i < measure.Shapes.Count; i++) if (measure.Shapes[i].View != null) gapTapes.Add(measure.Shapes[i].View);
                gapTapes.Made = $"{n} gap tapes: {detail}";
                if (n == 0) gapTapes.Skip($"no gap tape took: {detail}");
                AppCommands.EquipTool("measure");
            }
            else gapTapes.Skip("no scene parts / dw1 gap on this scan");

            // A taped opening: a width and a height tape framing the same hole (Parts.Openings).
            var opening = New("opening");
            var wTape = Shape(new[] { P(-0.35f, 1.9f, 0.01f), P(0.35f, 1.9f, 0.01f) });
            var hTape = Shape(new[] { P(0f, 1.55f, 0.01f), P(0f, 2.25f, 0.01f) });
            Openings.Invalidate();
            var openingIds = new HashSet<int>();
            if (wTape?.Entry != null) openingIds.Add(wTape.Entry.Id);
            if (hTape?.Entry != null) openingIds.Add(hTape.Entry.Id);
            bool framed = Openings.All.Any(o => openingIds.Contains(o.EntryA) || openingIds.Contains(o.EntryB));
            if (wTape?.View != null) opening.Add(wTape.View);
            if (hTape?.View != null) opening.Add(hTape.View);
            opening.Made = framed ? $"opening from tapes #{wTape?.Entry?.Id} + #{hTape?.Entry?.Id} ({Openings.All.Count} on the kitchen)" : "w + h tapes (no opening formed here)";
            if (opening.Roots.Count == 0) opening.Skip("the opening's tapes didn't save");

            // A level on the floor in front of the gap.
            var level = New("level");
            var lt = Level;
            if (lt != null)
            {
                var from = root.transform.TransformPoint(P(0f, 1.3f, 0.9f));
                var dir = root.transform.TransformDirection((Vector3.down * 1f - outDir * 0.25f).normalized);
                if (lt.TryRead(new Pose(from, Quaternion.LookRotation(dir)), out var reading))
                {
                    var placed = lt.Place(reading);
                    level.Add(placed.Gizmo);
                    level.Made = $"level #{placed.Entry?.Id} {placed.Entry?.Label}";
                }
                else level.Skip("no surface under the level's ray");
            }
            else level.Skip("no LevelTool");

            // A ladder against the counter / wall (and the fall edges it turns on).
            var ladder = New("ladder");
            var fall = New("fall edges");
            var ld = Ladder;
            if (ld != null)
            {
                LadderPlacement plan = null;
                foreach (var aim in new[] { P(0f, 0.9f, 0f), P(0f, 1.8f, 0f), P(0.6f, 0.9f, 0f), P(-0.6f, 0.9f, 0f) })
                {
                    var eye = P(0f, 1.6f, 2.2f);
                    var from = root.transform.TransformPoint(eye);
                    var dir = root.transform.TransformDirection((aim - eye).normalized);
                    if (ld.TryPlan(new Pose(from, Quaternion.LookRotation(dir)), out plan)) break;
                }
                if (plan != null)
                {
                    var placed = ld.Place(plan);
                    ladder.Add(placed.View);
                    ladder.Made = $"ladder #{placed.Entry?.Id} {placed.Summary}";
                    yield return null;
                    if (Services.TryGet<FallEdges>(out var fe) && fe.Visible && fe.AutoShown && fe.AnnotationRoot != null)
                    {
                        fall.Add(fe.AnnotationRoot.gameObject);
                        fall.Made = $"auto-shown by the ladder ({fe.FlaggedCount} flagged)";
                        fall.ComesBack = false;   // shown again on the kitchen, rebuilt (a new mesh): checked by FallEdges.Visible below
                    }
                    else fall.Skip(fe == null ? "no FallEdges" : $"not auto-shown (visible={fe.Visible} auto={fe.AutoShown})");
                }
                else { ladder.Skip("no support for a ladder on this scan"); fall.Skip("no ladder"); }
            }
            else { ladder.Skip("no LadderTool"); fall.Skip("no LadderTool"); }

            // A placed part (its outline and callout), adjusted (the placement guides show around it).
            var part = New("part outline");
            var guides = New("placement guides");
            var ac = Services.Get<PartLoader>()?.LoadFromCatalog(PartScenarios.Ac);
            if (ac != null)
            {
                parts.PlaceAt(ac, PlacePartMath.OriginFor(insert, facing, ac.LocalBox), facing);
                foreach (var o in ac.GetComponentsInChildren<PartOutline>(true)) part.Add(o);
                part.Made = $"{ac.Spec.id} placed ({part.Roots.Count} outline)";
                if (part.Roots.Count == 0) part.Add(ac);
                parts.Select(ac);
                AppCommands.AdjustPlacement(true);
                yield return null; yield return null;
                var g = Services.Get<PlacementGuides>();
                if (g != null && g.Showing && g.AnnotationRoot != null)
                {
                    guides.Add(g.AnnotationRoot.gameObject);
                    guides.Made = $"guides around {ac.Spec.id} ({g.EdgesShown} edges)";
                    guides.ComesBack = false;   // Adjust ends on a switch; the guides only show while adjusting
                }
                else guides.Skip($"guides not showing (adjusting={PlacementEditor.Current?.IsAdjusting} structure={root.Structure != null})");
            }
            else { part.Skip("the catalog AC didn't load"); guides.Skip("no part"); }

            // An answer pin.
            var pin = New("ask pin");
            if (Services.TryGet<SceneAsk>(out var ask) && ask.PinAtFocus(root.transform.TransformPoint(P(0f, 1.0f, 0.02f)), "Scope pin"))
            {
                var last = ask.Pins.Count > 0 ? ask.Pins[ask.Pins.Count - 1] : null;
                pin.Add(last?.marker);
                pin.Made = "an answer pin";
            }
            else pin.Skip("no SceneAsk / the pin didn't land");

            // A Grok layer: the fixture kitchen survey's pins.
            var grok = New("grok layer");
            var overlays = Services.Get<GrokOverlays>();
            if (overlays != null && overlays.ShowSurvey(SurveyView.Parse(Newtonsoft.Json.Linq.JObject.Parse(GrokG2Fixtures.KitchenSurveyBody))))
            {
                yield return null;
                if (overlays.AnnotationRoot != null) foreach (Transform c in overlays.AnnotationRoot) grok.Add(c.gameObject);
                grok.Made = $"survey pins ({grok.Roots.Count} set)";
                if (grok.Roots.Count == 0) grok.Skip("the survey drew nothing");
            }
            else grok.Skip("no GrokOverlays");

            // The install coach's boxes (and its card: the switch closes it).
            var coach = New("coach boxes");
            GrokRailsHarness.Stage("coach-check");
            yield return null; yield return null;
            if (Services.TryGet<CoachOverlay>(out var co) && co.boxes != null)
            {
                foreach (var b in co.boxes) if (b != null && b.gameObject.activeInHierarchy) coach.Add(b);
                if (co.stopCard != null && co.stopCard.gameObject.activeInHierarchy) coach.Add(co.stopCard);
            }
            coach.Made = $"{coach.Roots.Count} coach box(es)";
            if (coach.Roots.Count == 0) coach.Skip("the coach drew no box here");

            // Last: a tape started (one point) with Measure in hand, and a pending measure_edges title.
            AppCommands.EquipTool("measure");
            if (measure.Session.Count > 0) measure.CancelSession();
            measure.AreaMode = false;
            var mid = P(0.2f, 1.35f, 0.02f);
            measure.Click(new SurfaceHit { point = root.transform.TransformPoint(mid), rawPoint = root.transform.TransformPoint(mid), kind = SnapKind.Face });
            measure.NextLabel = "Scope pending title";
            int midDraw = measure.Session.Count;
            yield return null;

            var kShapes = new List<MeasureShape>(measure.Shapes);
            var kLevels = lt != null ? new List<LevelPlacement>(lt.Placements) : new List<LevelPlacement>();
            var kLadders = ld != null ? new List<LadderPlacement>(ld.Placements) : new List<LadderPlacement>();
            var kParts = new List<PartInstance>(parts.PlacedParts);
            var kPins = ask != null ? new List<SceneAsk.Pin>(ask.Pins) : new List<SceneAsk.Pin>();
            var kEntryIds = new HashSet<int>(Notebook.Entries.Where(e => e.SiteKey == Kitchen).Select(e => e.Id));
            var kAnnotations = AnnotationRenderers();
            var kObjects = new HashSet<GameObject>(kinds.SelectMany(k => k.Roots).Where(g => g != null));
            foreach (var k in kinds)
            {
                var c = Census(k.Roots);
                if (!k.Skipped && c.drawing == 0) k.Made += " (drew nothing?)";
                Note($"kitchen {k.Name}: {(k.Skipped ? "skipped: " : "")}{k.Made} drawing={c.drawing} colliders={c.colliders}");
            }
            Check("measurescope.kitchen.made", kinds.Count(k => !k.Skipped) >= 6 && midDraw == 1 && kAnnotations.Count > 0,
                $"kinds made {kinds.Count(k => !k.Skipped)}/{kinds.Count} [{string.Join(", ", kinds.Where(k => !k.Skipped).Select(k => k.Name))}] " +
                $"skipped [{string.Join(", ", kinds.Where(k => k.Skipped).Select(k => k.Name))}] mid-draw points={midDraw} annotation renderers={kAnnotations.Count} " +
                $"shapes={kShapes.Count} levels={kLevels.Count} ladders={kLadders.Count} parts={kParts.Count} pins={kPins.Count}");

            // 2. The gym.
            Hook();
            yield return Load(Gym, modelView, timeout);
            Unhook();
            if (!Loaded(Gym)) { Check("measurescope.gym.loaded", false, $"the gym didn't load: {Streamer.Status}"); yield break; }
            string view = modelView ? "model_view" : "world";
            Census(kinds, view, "gym");

            // Live lists: nothing of the kitchen's is live (grabbable, snappable, undoable, in the context).
            bool live = kShapes.All(s => !measure.Shapes.Contains(s)) && kParts.All(p => !parts.PlacedParts.Contains(p))
                        && (lt == null || kLevels.All(p => !lt.Placements.Contains(p))) && (ld == null || kLadders.All(p => !ld.Placements.Contains(p)))
                        && (ask == null || kPins.All(p => !ask.Pins.Contains(p)));
            Check($"measurescope.{view}.live_lists", live,
                $"kitchen shapes live={kShapes.Count(s => measure.Shapes.Contains(s))} parts live={kParts.Count(p => parts.PlacedParts.Contains(p))} " +
                $"levels live={(lt == null ? 0 : kLevels.Count(p => lt.Placements.Contains(p)))} ladders live={(ld == null ? 0 : kLadders.Count(p => ld.Placements.Contains(p)))} " +
                $"pins live={(ask == null ? 0 : kPins.Count(p => ask.Pins.Contains(p)))}");

            // Openings: none built from a kitchen tape.
            Openings.Invalidate();
            var foreign = Openings.All.Where(o => kEntryIds.Contains(o.EntryA) || kEntryIds.Contains(o.EntryB)).ToList();
            Check($"measurescope.{view}.openings", foreign.Count == 0,
                $"openings on the gym={Openings.All.Count} from kitchen tapes={foreign.Count} current={(Openings.TryCurrent(out var cur) ? cur.Id : "none")}");

            // The switch itself: windows closed first on the kitchen; the cursor, rubber band, ghost and pending title gone.
            Check($"measurescope.{view}.live_tools", s_LeavingSeen && s_ArrivedSeen && s_CurrentAtLeaving == Kitchen && s_SessionAtLeaving == 1
                                                      && s_SessionAtArrival == 0 && !s_CursorAtArrival && s_PreviewPointsAtArrival == 0 && !s_LevelGhostAtArrival && !s_NextLabelAtArrival,
                $"leaving on {s_CurrentAtLeaving ?? "-"} with {s_SessionAtLeaving} point(s); at arrival: session={s_SessionAtArrival} cursor={s_CursorAtArrival} " +
                $"rubber band points={s_PreviewPointsAtArrival} level ghost={s_LevelGhostAtArrival} pending title={s_NextLabelAtArrival}");
            Check($"measurescope.{view}.mid_draw", measure.Session.Count == 0 && measure.ParkedShapes(Kitchen) >= kShapes.Count,
                $"tape in progress on the gym: {measure.Session.Count} point(s) (the kitchen's one point is parked with its {measure.ParkedShapes(Kitchen)} shapes)");

            // The label pool: no producer of the kitchen's; the shared producers claim nothing for it.
            LabelAudit(kObjects, view, kShapes, kPins, grok.Skipped ? null : overlays);

            // The notebook lists the gym's rows (All sites is off after a switch).
            if (Services.TryGet<NotebookPanel>(out var nbp))
            {
                int kitchenRows = nbp.Visible.Count(e => e.SiteKey == Kitchen);
                Check($"measurescope.{view}.notebook", !nbp.AllSites && kitchenRows == 0,
                    $"notebook rows listed={nbp.Visible.Count} kitchen rows={kitchenRows} all_sites={nbp.AllSites} (thumbnails only for this model's rows)");
            }

            // Every annotation renderer that drew on the kitchen: none draws now.
            var still = kAnnotations.Where(r => r != null && r.enabled && r.gameObject.activeInHierarchy).ToList();
            Check($"measurescope.{view}.annotations", still.Count == 0,
                $"of {kAnnotations.Count} kitchen annotation renderers, drawing now={still.Count}{(still.Count > 0 ? ": " + string.Join(", ", still.Take(6).Select(r => PathOf(r.transform))) : "")}");

            // 3. Undo right after switching: never a kitchen item.
            bool undid = EditHistory.Undo();
            yield return null;
            var afterUndo = Census(kObjects);
            bool redid = undid && EditHistory.Redo();
            yield return null;
            Check($"measurescope.{view}.undo", afterUndo.drawing == 0 && afterUndo.colliders == 0 && kShapes.All(s => !measure.Shapes.Contains(s)),
                $"undo on the gym={undid}{(undid ? $" (redo={redid})" : " (nothing of the gym's to undo)")}: kitchen drawing={afterUndo.drawing} colliding={afterUndo.colliders}");

            // 4. The kitchen again: every kind back, the tape in progress too.
            yield return Load(Kitchen, modelView, timeout);
            if (!Loaded(Kitchen)) { Check("measurescope.kitchen.back", false, $"the kitchen didn't load again: {Streamer.Status}"); yield break; }
            var back = new List<string>();
            bool allBack = true;
            foreach (var k in kinds)
            {
                if (k.Skipped || !k.ComesBack) continue;
                var c = Census(k.Roots);
                bool ok = c.drawing > 0;
                allBack &= ok;
                back.Add($"{k.Name}={c.drawing}{(ok ? "" : "!")}");
            }
            bool fallBack = fall.Skipped || (Services.TryGet<FallEdges>(out var feBack) && feBack.Visible && feBack.AutoShown);
            Check($"measurescope.{view}.kitchen_back", allBack && fallBack && measure.Session.Count == midDraw,
                $"drawing again: {string.Join(" ", back)}{(fall.Skipped ? "" : $" fall_edges={(fallBack ? "shown" : "off!")}")} | tape in progress back with {measure.Session.Count} point(s)");
            if (measure.Session.Count > 0) measure.CancelSession();
            measure.NextLabel = null;

            // 5. The gym again (what a capture shows: the gym after drawing on the kitchen).
            yield return Load(Gym, modelView, timeout);
            if (!Loaded(Gym)) { Check("measurescope.gym.again", false, $"the gym didn't load again: {Streamer.Status}"); yield break; }
            Census(kinds, view, "gym_again");
            var again = kAnnotations.Where(r => r != null && r.enabled && r.gameObject.activeInHierarchy).ToList();
            Check($"measurescope.{view}.gym_again.annotations", again.Count == 0, $"kitchen annotation renderers drawing={again.Count}");
            GrokRails.Clear();   // the fixture coach (its boxes stay parked with the kitchen)
            Note($"left on the gym ({view}) for the capture; the kitchen's items are parked (AgentHarness.ResetDemo() clears every site)");
        }

        /// Per kind: the kitchen's objects neither draw (strict) nor collide on the gym.
        static void Census(List<Kind> kinds, string view, string phase)
        {
            foreach (var k in kinds)
            {
                if (k.Skipped) { Check($"measurescope.{view}.{phase}.{Id(k.Name)}", true, $"skipped on the kitchen: {k.Made}"); continue; }
                var c = Census(k.Roots);
                Check($"measurescope.{view}.{phase}.{Id(k.Name)}", c.drawing == 0 && c.colliders == 0,
                    $"kitchen {k.Name}: drawing={c.drawing} (forced off by Model view={c.forced}) colliding={c.colliders} of {k.Roots.Count} object(s)");
            }
        }

        static string Id(string name) => name.Replace(' ', '_').Replace("-", "_");

        static bool Under(Transform t, HashSet<GameObject> roots)
        {
            for (var p = t; p != null; p = p.parent) if (roots.Contains(p.gameObject)) return true;
            return false;
        }

        /// The label pool after the switch: no producer is a kitchen object (level gizmos, ladder views, part outlines,
        /// cavity views park inactive and leave the pool), the shared producers (tapes, pins, the Grok layer, the coach)
        /// claim nothing for a kitchen item.
        static void LabelAudit(HashSet<GameObject> kitchen, string view, List<MeasureShape> kShapes, List<SceneAsk.Pin> kPins, GrokOverlays overlays)
        {
            WorldLabels.Refresh();
            var claims = new List<LabelClaim>();
            var sb = new StringBuilder();
            bool clean = true;
            foreach (var src in WorldLabels.Sources.ToList())
            {
                if (src == null || (src is Object uo && uo == null)) continue;
                claims.Clear();
                try { src.ClaimLabels(claims); } catch { continue; }
                int n = 0;
                foreach (var c in claims) n += c.Mandatory + c.Items;
                string name = src.GetType().Name;
                if (src is Component comp && Under(comp.transform, kitchen))
                {
                    if (n > 0) clean = false;   // registered but claiming nothing is harmless (it left the pool's share)
                    sb.Append($" {name}(kitchen{(n > 0 ? "!" : ", idle")})={n}");
                    continue;
                }
                if (n > 0) sb.Append($" {name}={n}");
            }
            WorldLabels.Refresh();   // the audit called ClaimLabels outside a share-out: share again
            bool shapes = kShapes.All(s => !Measure.Shapes.Contains(s));
            bool pins = !Services.TryGet<SceneAsk>(out var ask) || kPins.All(p => !ask.Pins.Contains(p));
            bool grok = overlays == null || (!overlays.IsShown(GrokOverlayKind.Survey) && overlays.ParkedSets(Kitchen) >= 1);
            Check($"measurescope.{view}.label_pool", clean && shapes && pins && grok,
                $"sources={WorldLabels.SourceCount} claims:{(sb.Length > 0 ? sb.ToString() : " none")} | pool used={WorldLabels.Used} shown={WorldLabels.Shown} asked={WorldLabels.Asked} " +
                $"| kitchen producers={(clean ? 0 : 1)} tapes live={!shapes} pins live={!pins} grok survey shown here={(overlays != null && overlays.IsShown(GrokOverlayKind.Survey))}");
        }
    }
}
#endif
