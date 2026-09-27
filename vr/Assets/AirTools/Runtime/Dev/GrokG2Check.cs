#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Core;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Dev
{
    /// AgentHarness.GrokCheck("g2"): the backend's example payloads for lane G2's actions (GrokG2Fixtures) through
    /// AgentActions.ExecuteAll on the loaded scene (kitchen or synthetic-facade), one [AirTools.Check] line per action
    /// with what was drawn and where (scene frame = the glTF frame, the flip undone). Synchronous: the survey arrives in
    /// the same reply as survey_started (no polling), the scan labels are fed from the docs example (GET is async).
    /// GrokLook(shot) moves the rig for the gate's captures.
    public static class GrokG2Check
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        const string Part = "hidden-hanger-5k";

        public static string Run()
        {
            var sb = new StringBuilder();
            int pass = 0, total = 0;
            void Check(string id, bool ok, string detail)
            {
                total++;
                if (ok) pass++;
                Log.Check(id, ok, detail);
                sb.AppendLine($"{id} {(ok ? "PASS" : "FAIL")} {detail}");
            }

            var o = Services.Get<GrokOverlays>();
            var root = Services.Get<SceneRoot>();
            if (o == null || root == null || root.Content == null)
            {
                Check("G2.scene", false, $"overlays={(o != null)} scene={(root != null && root.Content != null)} (Wire the scene, load kitchen or synthetic-facade)");
                return sb.ToString();
            }
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            string site = root.Site;
            Check("G2.scene", site == "kitchen" || site == "synthetic-facade", $"site={site} runtime={root.IsRuntimePackage} handlers={string.Join(",", new[] { "show_plan", "place_array", "survey_started", "show_survey", "show_coverage", "show_labels" }.Where(n => AgentActions.Handlers.ContainsKey(n)))}");
            o.ClearAll();
            if (site == "kitchen") Kitchen(o, root, Check);
            else if (site == "synthetic-facade") Facade(o, root, Check);
            if (site == "kitchen" || site == "synthetic-facade") OneLayer(o, root, Check, site == "kitchen");
            Check("G2.label_diet", o.VisibleLabelCount <= LabelBudget.Max, $"visible={o.VisibleLabelCount} max={LabelBudget.Max}");
            Log.Check("G2.harness.summary", pass == total, $"passed={pass} total={total}");
            sb.AppendLine($"G2 {pass}/{total}");
            return sb.ToString();
        }

        delegate void CheckFn(string id, bool ok, string detail);

        static bool Exec(string name, string json, string reply = null) =>
            AgentActions.Execute(new AgentAction { name = name, args = JObject.Parse(json) }, reply);

        static void Kitchen(GrokOverlays o, SceneRoot root, CheckFn check)
        {
            Plan(o, root, check, "G2.show_plan.kitchen", GrokG2Fixtures.KitchenPlanHooks, GrokG2Fixtures.KitchenPlanHooksReply, GrokG2Fixtures.KitchenPlaceHooks);
            // A run (the LED strip): segments, no points, no ghosts; "place them" would be a BOM.
            bool led = Exec("show_plan", GrokG2Fixtures.KitchenPlanLed, GrokG2Fixtures.KitchenPlanLedReply);
            check("G2.show_plan.run", led && o.LineCount(GrokOverlayKind.Plan) == 1 && o.PlanGhosts == 0 && o.PlanMarkers == 0 && o.Plan != null && !o.Plan.HasPoints,
                $"ok={led} lines={o.LineCount(GrokOverlayKind.Plan)} ghosts={o.PlanGhosts} card=\"{CardText(o)}\"");
            o.Hide(GrokOverlayKind.Plan);

            Survey(o, check, "G2.show_survey.kitchen", GrokG2Fixtures.KitchenSurveyStarted, GrokG2Fixtures.KitchenShowSurvey, GrokG2Fixtures.KitchenSurveyReply, 2);

            bool cov = Exec("show_coverage", GrokG2Fixtures.KitchenCoverage);
            check("G2.show_coverage.interior", cov && !o.IsShown(GrokOverlayKind.Coverage) && o.Coverage != null && o.Coverage.Interior
                && o.card != null && o.card.BodyText.StartsWith("This scan is only"), $"ok={cov} ring={o.IsShown(GrokOverlayKind.Coverage)} card=\"{CardText(o)}\"");
            // A coverage body for another site is ignored.
            var other = JObject.Parse(GrokG2Fixtures.FacadeCoverage);
            check("G2.show_coverage.other_site", !AgentActions.Execute(new AgentAction { name = "show_coverage", args = other }), $"last=\"{o.LastResult}\"");

            Labels(o, root, check, "G2.show_labels.kitchen", GrokG2Fixtures.KitchenLabels, GrokG2Fixtures.KitchenLabelsReply, 4);
            // The outlet's ray against the pre-labelled outlet anchor (a18, seen in 0221): the same place on the scan.
            var streamer = Services.Get<SceneStreamer>();
            var cam = streamer != null ? streamer.FindCamera("0221") : null;
            var outlet = LabelsView.Parse(JObject.Parse(GrokG2Fixtures.KitchenLabels)).Labels.First(l => l.Kind == "outlet");
            var anchor = LabelsView.Parse(JObject.Parse(GrokG2Fixtures.KitchenSceneLabels)).Labels[0];
            if (cam != null && FrameRay.TryCast(root.Content.transform, cam, outlet.Box, 500f, out var hit))
            {
                var scene = GltfFrame.ToGltf(root.Content.transform.InverseTransformPoint(hit.point));
                float d = Vector3.Distance(scene, GltfFrame.Vec(anchor.Pos));
                check("G2.show_labels.outlet_vs_anchor", d < 0.15f, $"hit_scene={F(scene)} anchor={F(GltfFrame.Vec(anchor.Pos))} d={d.ToString("0.000", C)}");
            }
            else check("G2.show_labels.outlet_vs_anchor", false, $"camera 0221 {(cam != null ? "ray missed the scan" : "missing")}");

            bool scan = o.ShowSceneLabels(LabelsView.Parse(JObject.Parse(GrokG2Fixtures.KitchenSceneLabels)));
            var pos = o.ScenePositions(GrokOverlayKind.SceneLabels);
            check("G2.scene_labels.kitchen", scan && o.SceneLabelsDrawn == 1 && pos.Count == 1 && Vector3.Distance(pos[0], new Vector3(-1.58f, -0.38f, 0.39f)) < 1e-3f,
                $"ok={scan} drawn={o.SceneLabelsDrawn} scene={(pos.Count > 0 ? F(pos[0]) : "-")} toggle={(o.sceneLabelsToggle != null ? o.sceneLabelsToggle.selected.ToString() : "n/a")}");
        }

        static void Facade(GrokOverlays o, SceneRoot root, CheckFn check)
        {
            Plan(o, root, check, "G2.show_plan.facade", GrokG2Fixtures.FacadePlanFascia, null, GrokG2Fixtures.FacadePlaceFascia);
            Survey(o, check, "G2.show_survey.facade", "{\"survey_id\":\"synthetic-facade-g2\"}", GrokG2Fixtures.FacadeSurvey, null, 2);

            bool cov = Exec("show_coverage", GrokG2Fixtures.FacadeCoverage, null);
            var c = o.Coverage;
            string side = "-";
            bool camSide = false;
            if (c != null && c.HasRing)
            {
                // docs/api.md: the green wedge sits on the side the flight's cameras are on (the loaded cameras, scene frame).
                var streamer = Services.Get<SceneStreamer>();
                var cams = streamer != null ? streamer.Cameras.Select(SceneCameras.Position).ToList() : new List<Vector3>();
                if (cams.Count == 0) cams = GrokG2Fixtures.FacadeCameraPositions.Select(p => GltfFrame.ToUnity(p[0], p[1], p[2])).ToList();
                var centre = CoverageGeometry.Flip(c.RingCentre); centre.y = 0f;
                var mean = Vector3.zero; foreach (var p in cams) mean += new Vector3(p.x, 0f, p.z);
                var toCams = (mean / cams.Count - centre).normalized;
                camSide = true;
                var dots = new List<string>();
                for (int k = 0; k < c.Sides.Count; k++)
                {
                    if (!c.Sides[k].Seen) continue;
                    var w = CoverageGeometry.WedgeCentre(c, k, 0.05); w.y = 0f;
                    float dot = Vector3.Dot((w - centre).normalized, toCams);
                    dots.Add($"{c.Sides[k].Label}:{dot.ToString("0.00", C)}");
                    camSide &= dot > 0.3f;
                }
                side = string.Join(" ", dots);
            }
            check("G2.show_coverage.facade", cov && o.WedgesDrawn == 8 && o.GreenWedges == 2 && o.LegsDrawn == 3 && camSide,
                $"ok={cov} wedges={o.WedgesDrawn} green={o.GreenWedges} legs={o.LegsDrawn} green_toward_cameras=[{side}] card=\"{CardText(o)}\"");

            if (root.IsRuntimePackage) Labels(o, root, check, "G2.show_labels.facade", GrokG2Fixtures.FacadeLabels, null, 2);
            else check("G2.show_labels.facade", !Exec("show_labels", GrokG2Fixtures.FacadeLabels), "built-in facade: no package cameras, labels refused (load synthetic-facade from the server to place them)");
        }

        static void Plan(GrokOverlays o, SceneRoot root, CheckFn check, string id, string planJson, string reply, string placeJson)
        {
            var tool = Services.Get<PartTool>();
            var loader = Services.Get<PartLoader>();
            // A part in hand: the ghosts are it, and place_array places it.
            if (tool != null && loader != null && tool.Held == null)
            {
                var p = loader.LoadFromCatalog(Part);
                if (p != null) tool.Hold(p);
            }
            var plan = PlanView.Parse(JObject.Parse(planJson));
            bool shown = Exec("show_plan", planJson, reply);
            var pos = o.ScenePositions(GrokOverlayKind.Plan);
            float worst = 0f;
            for (int i = 0; i < pos.Count && i < plan.Points.Count; i++) worst = Mathf.Max(worst, Vector3.Distance(pos[i], GltfFrame.Vec(plan.Points[i])));
            bool drawn = shown && o.LineCount(GrokOverlayKind.Plan) == plan.Segments.Count && o.PlanGhosts + o.PlanMarkers == plan.Points.Count && pos.Count == plan.Points.Count;
            check(id, drawn && worst < 0.10f,
                $"ok={shown} plan={plan.PlanId} lines={o.LineCount(GrokOverlayKind.Plan)} ghosts={o.PlanGhosts} markers={o.PlanMarkers} first_scene={(pos.Count > 0 ? F(pos[0]) : "-")} " +
                $"first_plan={F(GltfFrame.Vec(plan.Points[0]))} worst_seat_offset={worst.ToString("0.000", C)} state.plan_id={GrokState.PlanId} card=\"{CardText(o)}\" place_chip={(o.chipTemplate != null)}");

            if (tool == null) { check(id.Replace("show_plan", "place_array"), false, "no PartTool"); return; }
            int before = tool.PlacedParts.Count;
            bool placed = Exec("place_array", placeJson);
            int after = tool.PlacedParts.Count;
            bool undo = placed && EditHistory.Undo();   // never undo someone else's edit when nothing was placed
            int undone = tool.PlacedParts.Count;
            check(id.Replace("show_plan", "place_array"), placed && after - before == plan.Points.Count && undo && undone == before && !o.IsShown(GrokOverlayKind.Plan),
                $"ok={placed} placed={after - before}/{plan.Points.Count} one_undo_back_to={undone} (was {before}) last=\"{tool.LastAction}\"");
            // A plan_id that isn't on screen falls back to the tape array: with nothing placed it refuses ("place one part
            // first") — run only then, so the check never lays a real array along an old tape.
            if (tool.PlacedParts.Count == 0)
            {
                var fallback = JObject.Parse(placeJson); fallback["plan_id"] = "p-not-on-screen";
                AgentActions.Execute(new AgentAction { name = "place_array", args = fallback });
                check(id.Replace("show_plan", "place_array") + ".fallback", tool.LastAction != null && tool.LastAction.StartsWith("array"), $"last=\"{tool.LastAction}\"");
            }
        }

        static void Survey(GrokOverlays o, CheckFn check, string id, string startedJson, string showJson, string reply, int expectDrawn)
        {
            var actions = new List<AgentAction>
            {
                new AgentAction { name = "survey_started", args = JObject.Parse(startedJson) },
                new AgentAction { name = "show_survey", args = JObject.Parse(showJson) },
            };
            AgentActions.ExecuteAll(actions, reply);
            var s = SurveyView.Parse(JObject.Parse(showJson));
            var pos = o.ScenePositions(GrokOverlayKind.Survey);
            var roles = string.Join(" ", s.Ranked().Select(p => $"{p.Id}:{p.Severity}={SeverityStyle.Role(p.Severity)}"));
            check(id, o.PinsDrawn == expectDrawn && GrokState.SurveyId == s.SurveyId && o.PendingSurveyId == null && o.card != null && o.card.FooterText == s.Label,
                $"pins={o.PinsDrawn}/{s.Pins.Count} survey_id={GrokState.SurveyId} roles=[{roles}] first_scene={(pos.Count > 0 ? F(pos[0]) : "-")} label=\"{(o.card != null ? o.card.FooterText : "")}\"");
            // B1's door survey still opens its card.
            bool b1 = Exec("show_survey", "{\"request_id\":\"g2\",\"label\":\"cabinet_door\",\"groups\":[{\"w_mm\":262,\"h_mm\":279,\"count\":2,\"ids\":[\"o1\",\"o2\"]}],\"unverified\":[],\"focus\":[]}");
            check(id + ".b1_card", b1 && GrokState.SurveyId == s.SurveyId, $"ok={b1} (legacy B1 args with groups reach AppCommands.ShowSurvey)");
            bool neither = Exec("show_survey", "{\"survey_id\":\"g2-none\"}");
            check(id + ".refused", !neither && GrokState.SurveyId == s.SurveyId, "no pins and no groups: refused, the survey on screen kept");
        }

        static void Labels(GrokOverlays o, SceneRoot root, CheckFn check, string id, string json, string reply, int minPlaced)
        {
            bool ok = Exec("show_labels", json, reply);
            var pos = o.ScenePositions(GrokOverlayKind.Labels);
            check(id, ok && o.LabelsPlaced >= minPlaced, $"ok={ok} placed={o.LabelsPlaced} missed={o.LabelsMissed} scene=[{string.Join(" ", pos.Take(6).Select(F))}] last=\"{o.LastResult}\"");
        }

        /// DC6 (declutter M10, S8): one Grok layer at a time. Live labels (or, on the built-in facade without package
        /// cameras, a survey's pins), then a plan: the plan replaces them, and the scan's own labels (Settings ▸ Labels) stay
        /// as they were. The plan is hidden again afterwards.
        static void OneLayer(GrokOverlays o, SceneRoot root, CheckFn check, bool kitchen)
        {
            var first = GrokOverlayKind.Labels;
            bool firstOk = root.IsRuntimePackage && Exec("show_labels", kitchen ? GrokG2Fixtures.KitchenLabels : GrokG2Fixtures.FacadeLabels);
            if (!firstOk)
            {
                first = GrokOverlayKind.Survey;
                firstOk = Exec("show_survey", kitchen ? GrokG2Fixtures.KitchenShowSurvey : GrokG2Fixtures.FacadeSurvey);
            }
            bool firstShown = o.IsShown(first);
            bool scanBefore = o.IsShown(GrokOverlayKind.SceneLabels);
            string before = o.ShownLayers();
            bool plan = Exec("show_plan", kitchen ? GrokG2Fixtures.KitchenPlanLed : GrokG2Fixtures.FacadePlanFascia);
            bool planShown = o.IsShown(GrokOverlayKind.Plan), firstAfter = o.IsShown(first), scanAfter = o.IsShown(GrokOverlayKind.SceneLabels);
            string after = o.ShownLayers();
            o.Hide(GrokOverlayKind.Plan);
            check("G2.one_layer", firstOk && firstShown && plan && planShown && !firstAfter && scanAfter == scanBefore,
                $"{first}: {(firstShown ? "on" : "off")} → {(firstAfter ? "on" : "off")} after show_plan; plan={planShown} scene_labels_kept={scanAfter == scanBefore} layers [{before}] → [{after}]");
        }

        static string CardText(GrokOverlays o) => o.card != null ? Copy.Clip((o.card.BodyText ?? "").Replace('\n', ' '), 90) : "no card";

        // ---------------- capture angles ----------------

        /// Put the eye where a capture should look from (scene frame poses, through the content): "kitchen-labels" (the
        /// labels' own camera, 0221), "kitchen-survey" (camera 0001, which saw pin f1), "kitchen-plan" (1.2 m in front of
        /// the hooks under o10), "facade-plan" (a raised origin in front of the fascia), "facade-coverage" (high over the
        /// ring). Then: unity command capture_game_view --source screen.
        public static string Look(string shot)
        {
            var root = Services.Get<SceneRoot>();
            var rig = Object.FindFirstObjectByType<OVRCameraRig>();
            var eye = Camera.main != null ? Camera.main.transform : null;
            if (root == null || root.Content == null || rig == null || eye == null) return "no scene / rig / camera";
            var content = root.Content.transform;
            var streamer = Services.Get<SceneStreamer>();
            Vector3 from, at;
            switch ((shot ?? "").Trim().ToLowerInvariant())
            {
                case "kitchen-labels":
                case "kitchen-survey":
                {
                    var cam = streamer != null ? streamer.FindCamera(shot.EndsWith("labels") ? "0221" : "0001") : null;
                    if (cam == null) return "camera missing (load the kitchen package)";
                    from = SceneCameras.Position(cam);
                    at = from + SceneCameras.DirectionToUnity(cam, 0, 0, 1);
                    break;
                }
                case "kitchen-plan": from = new Vector3(0.15f, 0.25f, -0.95f); at = new Vector3(1.33f, 0.0f, -0.95f); break;
                case "facade-plan": from = new Vector3(0f, 6.6f, 3.5f); at = new Vector3(0f, 6.05f, 0f); break;
                case "facade-coverage": from = new Vector3(0f, 55f, 60f); at = new Vector3(0f, 0f, 2.5f); break;
                default: return "shots: kitchen-labels, kitchen-survey, kitchen-plan, facade-plan, facade-coverage";
            }
            var p = content.TransformPoint(from);
            var q = content.TransformPoint(at);
            var target = Quaternion.LookRotation((q - p).normalized, Vector3.up);
            rig.transform.rotation = target * Quaternion.Inverse(eye.rotation) * rig.transform.rotation;
            rig.transform.position += p - eye.position;
            Physics.SyncTransforms();
            return $"{shot}: eye={F(eye.position)} looking at {F(q)} (scene frame from={F(GltfFrame.ToGltf(from))})";
        }

        static string F(Vector3 v) => $"({v.x.ToString("0.000", C)}, {v.y.ToString("0.000", C)}, {v.z.ToString("0.000", C)})";
    }
}
#endif
