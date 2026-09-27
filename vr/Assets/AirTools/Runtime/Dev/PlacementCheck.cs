#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Dev
{
    /// The placement editor in the running app (AgentHarness.PlacementCheck), on synthetic-facade-parts (load it first:
    /// BackendHarness.LoadSite("synthetic-facade-parts")): cab1 out, the catalog AC into its cavity at the insert, then
    /// Adjust — focus, panel, nudges and the readout in inches, a red fit when pushed into a side, Reset to fit, turns,
    /// saves A / B with their notebook rows and pose, switching slots, a grab through the ToolInputHub, a model swap
    /// anchor to anchor, one undo step per session, the lock, a re-place restoring the active slot, a re-scale keeping
    /// it on the cavity, and the agent actions. Every result is an [AirTools.Check] placement.* line.
    public static class PlacementCheck
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        const string Cab = "cab1", Ac = "window-ac-small", Hanger = "hidden-hanger-5k";

        static PlacementEditor Editor => PlacementEditor.Current != null ? PlacementEditor.Current : Services.Get<PlacementEditor>();

        public static string Run(bool keepAdjusting = true)
        {
            var editor = Editor; var tool = Services.Get<PartTool>(); var loader = Services.Get<PartLoader>();
            var parts = Services.Get<SceneParts>(); var root = Services.Get<SceneRoot>(); var hub = Services.Get<ToolInputHub>();
            if (editor == null || tool == null || loader == null || root == null || hub == null)
                return "missing PlacementEditor / PartTool / PartLoader / SceneRoot / ToolInputHub (AirTools ▸ Wire Main Scene)";
            if (parts == null || !parts.HasParts || parts.Doc.Find(Cab) == null)
                return $"no scene part {Cab}: load it first, BackendHarness.LoadSite(\"{ScenePartsFixtures.Site}\")";
            if (AppState.Mode != AppMode.World) AppCommands.OpenChest();
            root.SetVisible(true);
            UiSettings.UseUnits(UnitSystem.Imperial);
            parts.ResetSession();
            tool.ClearAll();
            editor.ResetSession();
            editor.CandidatesOverride = new List<PartSummary> { new PartSummary { id = Ac, name = "Small window AC" }, new PartSummary { id = Hanger, name = "Hidden hanger" } };
            editor.SyncCatalogLoads = true;
            Physics.SyncTransforms();

            var report = new StringBuilder();
            int pass = 0, total = 0;
            void Check(string id, bool ok, string detail)
            {
                total++;
                if (ok) pass++;
                Log.Check($"placement.{id}", ok, detail);
                report.Append($"\n{(ok ? "PASS" : "FAIL")} placement.{id} {detail}");
            }
            string F(Vector3 v, float k = 1000f) => $"({(v.x * k).ToString("0.#", C)}, {(v.y * k).ToString("0.#", C)}, {(v.z * k).ToString("0.#", C)})";

            try
            {
                // 1. cab1 out, the AC in at the cavity's insert (place_part's pose without a pose).
                Check("remove", AppCommands.RemoveComponent(Cab) && parts.IsRemoved(Cab), parts.LastAction);
                var ac = PlaceInCavity(loader, tool, root, Ac, out string why);
                if (ac == null) { Check("place", false, why); return Summary(pass, total, report); }
                var spot = editor.SpotOf(ac);
                Check("cavity", spot != null && spot.CavityId == Cab && spot.Anchor == PlacementAnchor.FrontBottomCentre, $"{spot}");
                editor.FitNow(ac, record: true);
                Check("fit.insert", ac.Fit != null && ac.Fit.Status == FitStatus.Green && Copy.FitLine(ac.Fit).Contains("Fits the gap"),
                    $"\"{Copy.FitLine(ac.Fit)}\" {ac.Fit?.Headline}");

                // 2. Adjust: the mode, the focus (the cavity's label steps back), the panel.
                var tools = Services.Get<ToolManager>();
                tools?.Equip(ToolKind.Measure);
                Check("enter", AppCommands.AdjustPlacement(true) && editor.Adjusting == ac && (tools == null || tools.Active == ToolKind.None), editor.LastAction);
                WorldLabels.Refresh();
                var view = parts.ViewOf(Cab);
                Check("focus", PlacementFocus.Instance.On && (view == null || !view.LabelShown), $"cavity label shown={view?.LabelShown} labels={WorldLabels.Used}/{WorldLabels.Max}");
                Check("panel", editor.panel == null || editor.panel.IsOpen, $"panel={(editor.panel != null ? "open" : "none")} slot={UiCensus.MainName()}");

                // 3. Nudges: 3 × ⅜″ right reads "Right 1⅛″".
                for (int i = 0; i < 3; i++) editor.NudgeStep(0);
                editor.Tick();
                editor.TryReadout(ac, out var off, out var ttr);
                string readout = editor.panel != null ? editor.panel.Readout() : "";
                Check("nudge", Mathf.Abs(off.x - 3 * 0.009525f) < 1e-4f && Mathf.Abs(off.y) < 1e-4f && (editor.panel == null || readout.Contains("Right 1⅛″")),
                    $"offset_mm={F(off)} readout=\"{readout.Replace('\n', '|')}\"");

                // 4. The agent: 100 mm left → into the left side (red), then Reset to fit (green).
                AgentActions.Execute(new AgentAction { name = "adjust_placement", args = new JObject { ["dx_mm"] = -100 } });
                editor.FitNow(ac, record: false);
                Check("fit.red", ac.Fit != null && ac.Fit.Status == FitStatus.Red && ac.Fit.Verdict.StartsWith("Into the left side"), $"\"{Copy.FitLine(ac.Fit)}\"");
                AppCommands.ResetPlacement();
                editor.TryReadout(ac, out off, out ttr);
                Check("reset", off.magnitude < 1e-4f && ttr.magnitude < 0.01f && ac.Fit.Status == FitStatus.Green, $"offset_mm={F(off)} fit=\"{Copy.FitLine(ac.Fit)}\"");

                // 5. Turn 5°, save A (notebook row + place_part-ready pose), move out ¾″, save B, switch.
                editor.RotateStep(0);
                editor.Tick();
                editor.TryReadout(ac, out _, out ttr);
                Check("turn", Mathf.Abs(ttr.x - 5f) < 0.01f && (editor.panel == null || editor.panel.Readout().Contains("Turn 5°")), $"turn/tilt/roll={ttr}");
                int rows = Notebook.Entries.Count;
                bool savedA = AppCommands.SavePlacement(null);
                var rowA = Notebook.Entries.LastOrDefault();
                var json = rowA != null ? JObject.Parse(NotebookExporter.ToJson(new[] { rowA }, root.Site, "check"))["entries"]?[0] as JObject : null;
                Check("save", savedA && editor.ActiveSlot == "A" && Notebook.Entries.Count == rows + 1 && rowA.Tool == "placement_pose"
                      && (string)json?["type"] == "placement_pose" && json?["pose"]?["q"] is JArray q && q.Count == 4,
                    $"slot={editor.ActiveSlot} row=\"{rowA?.Label}\" json={json?.ToString(Newtonsoft.Json.Formatting.None)}");
                var anchorA = Anchor(ac, spot);
                editor.NudgeStep(4); editor.NudgeStep(4);
                AppCommands.SavePlacement(null);
                var anchorB = Anchor(ac, spot);
                bool loadA = AppCommands.LoadPlacement("A");
                bool atA = Vector3.Distance(Anchor(ac, spot), anchorA) < 1e-4f;
                bool loadB = AppCommands.LoadPlacement("B");
                Check("slots", loadA && atA && loadB && Vector3.Distance(Anchor(ac, spot), anchorB) < 1e-4f && editor.ActiveSlot == "B",
                    $"A→B {F(anchorB - anchorA)} mm, active={editor.ActiveSlot}");

                // 6. A grab through the hub (the pinch path): 5 cm to the right along the cavity.
                AppCommands.LoadPlacement("A");
                var f = editor.FrameOf(spot);
                var eye = root.transform.TransformPoint(f.Origin + f.Out * 1.2f + f.Up * 1.1f);
                var aim = ToolInputHub.RayPose(eye, ac.WorldBoxCentre);
                var shift = root.transform.TransformVector(f.Right * 0.05f);
                var moved = new Pose(aim.position + shift, aim.rotation);
                editor.TryReadout(ac, out var before, out _);
                hub.RaisePressStart(ToolHand.Right, aim);
                bool grabbed = editor.Grabbing;
                hub.RaisePressMove(ToolHand.Right, moved);
                editor.Tick();
                hub.RaisePressEnd(ToolHand.Right, moved);
                editor.TryReadout(ac, out off, out _);
                Check("grab", grabbed && Mathf.Abs(off.x - before.x - 0.05f) < 0.002f, $"grabbed={grabbed} offset_mm {F(before)} → {F(off)} | {editor.LastAction}");

                // 7. One adjust session = one undo step (three nudges, the grab and a swap included).
                AppCommands.AdjustPlacement(false);
                int n0 = editor.UndoCount;
                var pose0 = LocalPose(ac, root);
                AppCommands.AdjustPlacement(true);
                editor.NudgeStep(1); editor.NudgeStep(3); editor.RotateStep(3);
                bool swapped = AppCommands.NextPlacedModel(1);
                var hanger = editor.Adjusting;
                bool anchorKept = hanger != null && hanger.Spec.id == Hanger && Vector3.Distance(Anchor(hanger, spot), Anchor(ac, spot)) < 1e-3f;
                Check("swap", swapped && anchorKept && !ac.gameObject.activeSelf && tool.QuantityOf(Hanger) == 1 && tool.QuantityOf(Ac) == 0,
                    $"{editor.LastSwap}");
                AppCommands.AdjustPlacement(false);
                bool one = editor.UndoCount == n0 + 1;
                bool undone = EditHistory.Undo() && editor.Target() == ac && ac.gameObject.activeSelf && Vector3.Distance(LocalPose(ac, root).position, pose0.position) < 1e-4f;
                Check("session.one_undo", one && undone, $"undo steps {n0} → {editor.UndoCount + 1}; after undo: {editor.Target()?.Spec.id} at {F(LocalPose(ac, root).position - pose0.position)} mm");

                // 8. Locked outside adjust mode: a pinch with the part tool only selects it.
                tools?.Equip(ToolKind.Part);
                var p0 = ac.transform.position;
                hub.RaisePressStart(ToolHand.Right, ToolInputHub.RayPose(eye, ac.WorldBoxCentre));
                hub.RaisePressEnd(ToolHand.Right, ToolInputHub.RayPose(eye, ac.WorldBoxCentre + Vector3.up * 0.3f));
                Check("lock", tool.Held == null && tool.PlacedParts.Contains(ac) && Vector3.Distance(p0, ac.transform.position) < 1e-5f, tool.LastAction);
                tools?.Equip(ToolKind.Measure);

                // 9. A new AC placed into cab1 goes to the spot's active slot (B).
                AppCommands.LoadPlacement("B");
                string active = editor.ActiveSlot;
                var saved = editor.Store.Active(spot.Key);
                tool.Remove(ac);
                var again = PlaceInCavity(loader, tool, root, Ac, out why);
                var sp = editor.Space();
                Check("replace.restores", again != null && saved != null && Vector3.Distance(Anchor(again, editor.SpotOf(again)), sp.ToRoot(saved.AnchorPkg)) < 1e-4f,
                    $"active={active} {editor.LastAction}");
                ac = again ?? ac;
                spot = editor.SpotOf(ac) ?? spot;

                // 10. "Set scale" ×1.05 and back: the placement stays on the cavity (package coordinates).
                var keyPkg = sp.ToPackage(Anchor(ac, spot));
                root.Rescale(1.05f);
                editor.Tick();
                bool onFeature = Vector3.Distance(editor.Space().ToPackage(Anchor(ac, spot)), keyPkg) < 1e-4f;
                root.Rescale(1f / 1.05f);
                editor.Tick();
                Check("rescale", onFeature && Vector3.Distance(editor.Space().ToPackage(Anchor(ac, spot)), keyPkg) < 1e-4f, $"calibration ×1.05 and back");

                // 11. The agent actions (voice): an inch right, save C, the next model.
                editor.TryReadout(ac, out before, out _);
                bool a1 = AgentActions.Execute(new AgentAction { name = "adjust_placement", args = new JObject { ["dx_mm"] = 25.4 } });
                editor.TryReadout(ac, out off, out _);
                bool a2 = AgentActions.Execute(new AgentAction { name = "save_placement", args = new JObject { ["slot"] = "C" } });
                bool a3 = AgentActions.Execute(new AgentAction { name = "cycle_model", args = new JObject { ["delta"] = 1 } });
                Check("agent", a1 && Mathf.Abs(off.x - before.x - 0.0254f) < 1e-4f && a2 && editor.Saved("C") != null && a3 && editor.Target()?.Spec.id == Hanger,
                    $"adjust={a1} ({F(off - before)} mm) save={a2} cycle={a3} → {editor.Target()?.Spec.id}");
                AgentActions.Execute(new AgentAction { name = "cycle_model", args = new JObject { ["delta"] = -1 } });

                // For the capture: adjusting the AC in cab1 at placement A.
                AppCommands.LoadPlacement("A");
                if (keepAdjusting) AppCommands.AdjustPlacement(true);
                editor.Tick();
            }
            finally
            {
                editor.CandidatesOverride = null;
                editor.SyncCatalogLoads = false;
            }
            return Summary(pass, total, report) + "\n" + editor.Report();
        }

        static string Summary(int pass, int total, StringBuilder report)
        {
            Log.Check("placement.summary", pass == total, $"passed={pass} total={total}");
            return $"Placement editor: {pass}/{total} passed" + report;
        }

        /// A catalog part placed the way place_part does without a pose: front-bottom-centre at the cavity's insert,
        /// facing out of the opening.
        public static PartInstance PlaceInCavity(PartLoader loader, PartTool tool, SceneRoot root, string partId, out string why)
        {
            why = null;
            var part = loader.LoadFromCatalog(partId);
            if (part == null) { why = loader.LastError; return null; }
            if (!AppCommands.TryPlaceTarget(new PlacePartArgs { PartId = partId, ComponentId = Cab }, root, out var target, out why))
            {
                part.gameObject.SetActive(false);
                Object.Destroy(part.gameObject);
                return null;
            }
            tool.PlaceAt(part, PlacePartMath.OriginFor(target.Anchor, target.Rotation, PartMath.LocalBox(part.Spec), target.AtOrigin), target.Rotation);
            return part;
        }

        static Vector3 Anchor(PartInstance p, PlacementEditor.Spot s)
        {
            var root = Services.Get<SceneRoot>();
            var local = root != null ? root.transform.InverseTransformPoint(p.transform.position) : p.transform.position;
            var rot = root != null ? Quaternion.Inverse(root.transform.rotation) * p.transform.rotation : p.transform.rotation;
            return PlacementMath.AnchorOf(local, rot, p.LocalBox, s != null ? s.Anchor : PlacementAnchor.FrontBottomCentre);
        }

        static Pose LocalPose(PartInstance p, SceneRoot root) =>
            new Pose(root.transform.InverseTransformPoint(p.transform.position), Quaternion.Inverse(root.transform.rotation) * p.transform.rotation);
    }
}
#endif
