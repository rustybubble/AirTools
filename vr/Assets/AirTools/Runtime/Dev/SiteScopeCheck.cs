#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AirTools.Core;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using UnityEngine;

namespace AirTools.Dev
{
    /// sitescope, in the running app (through AgentHarness.SiteScopeCheck; needs the backend's kitchen and Zabel gym:
    /// ServerConfig.SetOverride("http://127.0.0.1:8004") before Play). Every result is an [AirTools.Check] sitescope.* line;
    /// SiteScopeResult() has the report.
    /// 1. **The kitchen.** dw1 out, its gap taped (CavityTapes: three tapes), a catalog part placed in the gap, one more tape,
    ///    and a Grok layer (the fixture kitchen survey's pins).
    /// 2. **The gym.** Loaded (LoadSite, or `modelView`: show_model's path). A census of every renderer and collider of the
    ///    kitchen's tapes and parts: none may draw or collide; the live lists and the context carry none of them.
    /// 3. **A gym tape.**
    /// 4. **The kitchen again.** Its tapes and part are back, same scene-root poses, at the same scale; dw1 is out again;
    ///    the gym tape is hidden.
    /// 5. **Undo** takes the kitchen's newest edit (the part) and nothing else; Redo puts it back.
    /// It leaves the kitchen's items up (AgentHarness.ResetDemo() clears every site).
    public static class SiteScopeCheck
    {
        public const string Kitchen = "kitchen", Gym = "zabel-gymnasium";
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly List<string> s_Report = new List<string>();
        static string s_Summary = "not run";
        static bool s_Done = true;
        static int s_Pass, s_Total;

        static SceneStreamer Streamer => Services.Get<SceneStreamer>();
        static SceneRoot Root => Services.Get<SceneRoot>();
        static MeasureTool Measure => Services.Get<MeasureTool>();
        static PartTool Parts => Services.Get<PartTool>();

        public static string Result() => $"{(s_Done ? "" : "(running) ")}{s_Summary}\n{string.Join("\n", s_Report)}";

        /// One line: the site, the live and parked counts of every owner.
        public static string Status()
        {
            var sb = new StringBuilder($"site={SiteScope.Current} switches={SiteScope.Switches} parked_sites={SiteScope.ParkedCount}");
            var m = Measure; var p = Parts;
            if (m != null) sb.Append($" | tapes live={m.Shapes.Count} parked(kitchen)={m.ParkedShapes(Kitchen)} parked(gym)={m.ParkedShapes(Gym)}");
            if (p != null) sb.Append($" | parts live={p.PlacedParts.Count} parked(kitchen)={p.ParkedParts(Kitchen)} parked(gym)={p.ParkedParts(Gym)}");
            if (Services.TryGet<AirTools.Scene.SceneParts>(out var sp)) sb.Append($" | scene_parts {sp.Site ?? "-"} out=[{string.Join(",", sp.RemovedIds)}] saved_sites={sp.SavedSites}");
            sb.Append($" | undo={EditHistory.CanUndo} redo={EditHistory.CanRedo}");
            return sb.ToString();
        }

        static void Check(string id, bool ok, string detail)
        {
            s_Total++;
            if (ok) s_Pass++;
            Log.Check(id, ok, detail);
            s_Report.Add($"{(ok ? "PASS" : "FAIL")} {id} {detail}");
        }

        static IEnumerator Until(System.Func<bool> done, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!done() && Time.realtimeSinceStartup < end) yield return null;
        }

        public static string Run(bool modelView = false, float timeout = 90f)
        {
            if (!s_Done) return "SiteScopeCheck is already running: poll SiteScopeResult()";
            if (Streamer == null || Root == null || Measure == null || Parts == null) return "no SceneStreamer / SceneRoot / MeasureTool / PartTool (AirTools ▸ Wire Main Scene)";
            s_Report.Clear();
            s_Pass = s_Total = 0;
            s_Summary = "SiteScopeCheck running";
            s_Done = false;
            if (!DemoRunner.Run(Routine(modelView, timeout), ex => { s_Summary = $"SiteScopeCheck error {ex.GetType().Name}: {ex.Message}"; s_Done = true; },
                    () => { s_Summary = $"SiteScopeCheck {s_Pass}/{s_Total}"; s_Done = true; }))
            {
                s_Done = true;
                return "another routine is running (DemoRunner busy)";
            }
            return "SiteScopeCheck started: poll SiteScopeResult()";
        }

        static bool Loaded(string site) =>
            Root.IsRuntimePackage && Root.Site == site && !Streamer.Loading && SiteScope.IsCurrent(site) && !SiteScope.Pending;

        static IEnumerator Load(string site, bool modelView, float timeout)
        {
            if (Loaded(site)) yield break;
            if (modelView) ModelViewCheck.Show(site);
            else AppCommands.LoadSite(site);
            yield return null;
            yield return Until(() => Loaded(site), timeout);
            // Scene parts bind a few frames after the swap (after the visual and the cavities).
            var sp = Streamer.Parts;
            yield return Until(() => sp == null || sp.Site == site || !Streamer.PartsStatus.StartsWith("split", System.StringComparison.Ordinal), 15f);
            yield return null;
        }

        /// Renderers that draw and colliders that collide under `roots` (Model view's forceRenderingOff counts as not drawing).
        static (int renderers, int colliders) Census(List<GameObject> roots)
        {
            int r = 0, c = 0;
            var rs = new List<Renderer>();
            var cs = new List<Collider>();
            foreach (var go in roots)
            {
                if (go == null) continue;
                go.GetComponentsInChildren(true, rs);
                foreach (var x in rs) if (x.enabled && x.gameObject.activeInHierarchy && !x.forceRenderingOff) r++;
                go.GetComponentsInChildren(true, cs);
                foreach (var x in cs) if (x.enabled && x.gameObject.activeInHierarchy) c++;
            }
            return (r, c);
        }

        static string F(Vector3 v) => $"({v.x.ToString("0.000", C)}, {v.y.ToString("0.000", C)}, {v.z.ToString("0.000", C)})";

        static MeasureShape Tape(Vector3 a, Vector3 b)
        {
            var m = Measure; var root = Root.transform;
            if (m.Session.Count > 0) m.CancelSession();
            bool area = m.AreaMode;
            m.AreaMode = false;
            int before = m.Shapes.Count;
            m.Click(new SurfaceHit { point = root.TransformPoint(a), rawPoint = root.TransformPoint(a), kind = SnapKind.Face });
            m.Click(new SurfaceHit { point = root.TransformPoint(b), rawPoint = root.TransformPoint(b), kind = SnapKind.Face });
            if (m.Session.Count >= MeasureMath.MinPoints) m.Finish();
            m.AreaMode = area;
            return m.Shapes.Count > before ? m.Shapes[m.Shapes.Count - 1] : null;
        }

        static IEnumerator Routine(bool modelView, float timeout)
        {
            var root = Root; var measure = Measure; var parts = Parts;
            // 1. The kitchen: a gap, its tapes, a part in it, one more tape.
            yield return Load(Kitchen, modelView, timeout);
            if (!Loaded(Kitchen)) { Check("sitescope.kitchen.loaded", false, $"the kitchen didn't load: {Streamer.Status}"); yield break; }
            float kitchenScale = root.Calibration;
            var sceneParts = Streamer.Parts;
            bool gap = sceneParts != null && sceneParts.HasParts && (sceneParts.IsRemoved("dw1") || AppCommands.RemoveComponent("dw1"));
            yield return null;
            int tapes = 0;
            Vector3 insert = new Vector3(0f, 0.9f, 1.5f);
            Quaternion facing = Quaternion.identity;
            if (gap && Gaps.TryGet(out var g, "dw1"))
            {
                tapes = CavityTapes.Run(g, out var measured, out string detail);
                if (AppCommands.TryPlaceTarget(new PlacePartArgs { ComponentId = "dw1" }, root, out var target, out _)) { insert = target.Anchor; facing = target.Rotation; }
                Log.Info($"SiteScopeCheck: dw1 taped ({tapes}): {detail}");
            }
            var extra = Tape(insert + new Vector3(-0.4f, 0.8f, 0f), insert + new Vector3(0.4f, 0.8f, 0f));
            var part = Services.Get<PartLoader>()?.LoadFromCatalog(PartScenarios.Ac);
            if (part != null) parts.PlaceAt(part, PlacePartMath.OriginFor(insert, facing, part.LocalBox), facing);
            // A Grok layer: the fixture survey's pins (show_survey) on the kitchen.
            var overlays = Services.Get<AirTools.Agent.Grok.GrokOverlays>();
            bool survey = overlays != null && overlays.ShowSurvey(AirTools.Agent.Grok.SurveyView.Parse(Newtonsoft.Json.Linq.JObject.Parse(GrokG2Fixtures.KitchenSurveyBody)));
            string surveyId = AirTools.Agent.Grok.GrokState.SurveyId;
            yield return null;
            var kShapes = new List<MeasureShape>(measure.Shapes);
            var kPoints = new List<Vector3[]>();
            foreach (var s in kShapes) kPoints.Add((Vector3[])s.Points.Clone());
            var kParts = new List<PartInstance>(parts.PlacedParts);
            var kPoses = new List<Pose>();
            foreach (var p in kParts) kPoses.Add(new Pose(p.transform.localPosition, p.transform.localRotation));
            var kRoots = new List<GameObject>();
            foreach (var s in kShapes) if (s.View != null) kRoots.Add(s.View.gameObject);
            foreach (var p in kParts) if (p != null) kRoots.Add(p.gameObject);
            var kCensus = Census(kRoots);
            Check("sitescope.kitchen.made", part != null && extra != null && kShapes.Count >= 1 && kParts.Contains(part) && kCensus.renderers > 0,
                $"gap={(gap ? "dw1 out" : "none (no scene parts)")} gap_tapes={tapes} tapes={kShapes.Count} parts={kParts.Count} scale=×{kitchenScale.ToString("0.0000", C)} " +
                $"drawing={kCensus.renderers} renderers, {kCensus.colliders} colliders | {Status()}");

            // 2. The gym: nothing of the kitchen's draws, collides, or is live.
            yield return Load(Gym, modelView, timeout);
            if (!Loaded(Gym)) { Check("sitescope.gym.loaded", false, $"the gym didn't load: {Streamer.Status}"); yield break; }
            var gCensus = Census(kRoots);
            bool liveClean = true;
            foreach (var s in kShapes) liveClean &= !measure.Shapes.Contains(s);
            foreach (var p in kParts) liveClean &= !parts.PlacedParts.Contains(p);
            var ctx = AirTools.Agent.Grok.GrokView.Snapshot();
            bool ctxClean = ctx.Removed == null || !ctx.Removed.Contains("dw1");
            // context.measurement names the tape it sends ("tape #12"): none of the kitchen's.
            string tapeCtx = PartsClient.MeasurementContext() is Dictionary<string, object> mc && mc.TryGetValue("label", out var l) ? l as string : null;
            var kitchenIds = new HashSet<string>();
            foreach (var s in kShapes) if (s.Entry != null) kitchenIds.Add($"tape #{s.Entry.Id}");
            ctxClean &= tapeCtx == null || !kitchenIds.Contains(tapeCtx);
            var setScaleTape = AirTools.Structure.ScaleCalibration.LatestTape();
            ctxClean &= setScaleTape == null || setScaleTape.SiteKey == Gym;
            if (survey)
                Check("sitescope.gym.grok_parked", !overlays.IsShown(AirTools.Agent.Grok.GrokOverlayKind.Survey) && overlays.ParkedSets(Kitchen) == 1
                                                   && AirTools.Agent.Grok.GrokState.SurveyId == null,
                    $"survey shown={overlays.IsShown(AirTools.Agent.Grok.GrokOverlayKind.Survey)} parked sets(kitchen)={overlays.ParkedSets(Kitchen)} survey_id={AirTools.Agent.Grok.GrokState.SurveyId ?? "null"} (was {surveyId})");
            Check("sitescope.gym.kitchen_hidden", gCensus.renderers == 0 && gCensus.colliders == 0 && liveClean && ctxClean,
                $"kitchen items drawing={gCensus.renderers} colliding={gCensus.colliders} live_clean={liveClean} context_clean={ctxClean} " +
                $"(removed={(ctx.Removed != null ? "[" + string.Join(",", ctx.Removed) + "]" : "null")} measurement={tapeCtx ?? "null"}) | {Status()}");

            // 3. A gym tape.
            var gymTape = Tape(new Vector3(0f, 1f, 2f), new Vector3(1f, 1f, 2f));
            Check("sitescope.gym.tape", gymTape != null && measure.Shapes.Contains(gymTape) && gymTape.Entry != null && gymTape.Entry.SiteKey == Gym,
                $"gym tape {(gymTape != null ? gymTape.Entry?.Label : "not saved")} site={gymTape?.Entry?.SiteKey} | {Status()}");

            // 4. The kitchen again: its items as they were, the gym tape hidden.
            yield return Load(Kitchen, modelView, timeout);
            if (!Loaded(Kitchen)) { Check("sitescope.kitchen.back", false, $"the kitchen didn't load again: {Streamer.Status}"); yield break; }
            float worst = 0f;
            bool same = measure.Shapes.Count == kShapes.Count;
            for (int i = 0; i < kShapes.Count && same; i++)
            {
                same &= measure.Shapes[i] == kShapes[i];
                for (int k = 0; k < kPoints[i].Length; k++) worst = Mathf.Max(worst, Vector3.Distance(kShapes[i].Points[k], kPoints[i][k]));
            }
            for (int i = 0; i < kParts.Count; i++)
            {
                var p = kParts[i];
                same &= p != null && parts.PlacedParts.Contains(p) && p.gameObject.activeInHierarchy;
                if (p != null) worst = Mathf.Max(worst, Vector3.Distance(p.transform.localPosition, kPoses[i].position));
            }
            var backCensus = Census(kRoots);
            var gymRoots = new List<GameObject>();
            if (gymTape?.View != null) gymRoots.Add(gymTape.View.gameObject);
            var gymCensus = Census(gymRoots);
            bool dwOut = !gap || (Streamer.Parts != null && Streamer.Parts.IsRemoved("dw1"));
            Check("sitescope.kitchen.back", same && worst < 1e-4f && Mathf.Abs(root.Calibration - kitchenScale) < 1e-5f && dwOut && backCensus.renderers > 0
                                            && gymCensus.renderers == 0 && !measure.Shapes.Contains(gymTape),
                $"same items={same} worst drift={(worst * 1000f).ToString("0.000", C)} mm scale=×{root.Calibration.ToString("0.0000", C)} dw1 out={dwOut} " +
                $"kitchen drawing={backCensus.renderers} (was {kCensus.renderers}) gym tape drawing={gymCensus.renderers} | {Status()}");

            if (survey)
            {
                yield return null;   // ContentChanged → RestoreParked (a held arrival puts it back at once)
                bool back = overlays.IsShown(AirTools.Agent.Grok.GrokOverlayKind.Survey) && overlays.ParkedSets(Kitchen) == 0
                            && AirTools.Agent.Grok.GrokState.SurveyId == surveyId && overlays.AnnotationRoot != null
                            && overlays.AnnotationRoot.parent == root.Content.transform;
                Check("sitescope.kitchen.grok_back", back,
                    $"survey shown={overlays.IsShown(AirTools.Agent.Grok.GrokOverlayKind.Survey)} survey_id={AirTools.Agent.Grok.GrokState.SurveyId ?? "null"} under the new content={overlays.AnnotationRoot != null && overlays.AnnotationRoot.parent == root.Content.transform}");
            }
            if (modelView)
            {
                // On the table the model shows its own parts and assets (geometry), never its annotations' text.
                var partRoots = new List<GameObject>();
                foreach (var p in kParts) if (p != null) partRoots.Add(p.gameObject);
                var partsDrawing = Census(partRoots);
                Check("sitescope.kitchen.model_view", partsDrawing.renderers > 0 && AirTools.UI.ModelView.HidesAnnotations,
                    $"on the table: the kitchen's parts drawing={partsDrawing.renderers}, annotations hidden={AirTools.UI.ModelView.HidesAnnotations}");
            }

            // 5. Undo: the kitchen's newest edit (the part), nothing else; Redo puts it back.
            int shapes = measure.Shapes.Count, gymParked = measure.ParkedShapes(Gym);
            bool undid = EditHistory.Undo();
            yield return null;
            bool partGone = part != null && !parts.PlacedParts.Contains(part) && !part.gameObject.activeSelf;
            Check("sitescope.undo.kitchen_newest", undid && partGone && measure.Shapes.Count == shapes && measure.ParkedShapes(Gym) == gymParked && dwOut == (!gap || Streamer.Parts.IsRemoved("dw1")),
                $"undo={undid} part gone={partGone} tapes {shapes}→{measure.Shapes.Count} gym parked {gymParked}→{measure.ParkedShapes(Gym)} | {Status()}");
            bool redid = EditHistory.Redo();
            yield return null;
            Check("sitescope.redo", redid && part != null && parts.PlacedParts.Contains(part) && part.gameObject.activeSelf,
                $"redo={redid} part back={(part != null && parts.PlacedParts.Contains(part))} | {Status()}");
        }
    }
}
#endif
