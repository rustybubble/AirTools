#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Input;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Dev
{
    /// Scene parts in the running app (docs/api.md parts.r&lt;rev&gt;.json), through AgentHarness: load a package with parts
    /// first (BackendHarness.LoadSite("synthetic-facade-parts") from the exporter, or the kitchen once it has them).
    /// Run() takes every removable component out through AppCommands (the Settings chips' and remove_component's path),
    /// checks the three steps, the "estimated" label, that a ray into the gap reaches the cavity (no ghost collider),
    /// undo / redo, then tapes the cavity's width (and depth, and height when it has a top) with the real measure tool
    /// through ToolInputHub, and puts it back. Every result is an [AirTools.Check] line.
    public static class ScenePartsCheck
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static SceneParts Parts => Services.Get<SceneParts>();
        static SceneRoot Root => Services.Get<SceneRoot>();

        public static string Status()
        {
            var p = Parts; var s = Services.Get<SceneStreamer>();
            return $"streamer parts: {s?.PartsStatus ?? "-"}\n{(p != null ? p.Report() : "no SceneParts")}\nlast: {p?.LastAction}";
        }

        /// keep: the component left out at the end with its width tape drawn (the capture: the box removed, its cavity
        /// and a tape across it); null = the first removable one (cab1 on synthetic-facade-parts, a 600 mm opening),
        /// "" = put everything back. It is checked last.
        public static string Run(string keep = null, int seed = 1)
        {
            var parts = Parts; var root = Root; var tool = Services.Get<MeasureTool>(); var hub = Services.Get<ToolInputHub>();
            if (parts == null || root == null || tool == null || hub == null) return "missing SceneParts / SceneRoot / MeasureTool / ToolInputHub";
            if (!parts.HasParts) return $"no scene parts in {root.Site ?? "the built-in scene"} ({Services.Get<SceneStreamer>()?.PartsStatus}): load one, e.g. BackendHarness.LoadSite(\"{ScenePartsFixtures.Site}\")";
            if (AppState.Mode == AppMode.Passthrough) AppCommands.OpenChest();
            root.SetVisible(true);
            Physics.SyncTransforms();
            var tools = Services.Get<ToolManager>();
            if (tools != null && tools.Active != ToolKind.Measure) tools.Equip(ToolKind.Measure);
            parts.ResetSession();

            var report = new StringBuilder();
            int pass = 0, total = 0;
            void Check(string id, bool ok, string detail)
            {
                total++;
                if (ok) pass++;
                Log.Check(id, ok, detail);
                report.Append($"\n{(ok ? "PASS" : "FAIL")} {id} {detail}");
            }

            var list = parts.Removable.ToList();
            var kept = keep == null ? list.FirstOrDefault() : list.FirstOrDefault(x => x.id == keep || x.label == keep);
            if (kept != null) { list.Remove(kept); list.Add(kept); }
            bool keepLast = kept != null;
            for (int k = 0; k < list.Count; k++)
            {
                var c = list[k];
                bool last = k == list.Count - 1;
                parts.TryNodes(c.id, out var n);

                // 1. Out, through the command the chip and the voice action use.
                bool removed = AppCommands.RemoveComponent(c.id);
                parts.TryNodes(c.id, out n);
                bool steps = removed && Off(n.visual) && Off(n.collision) && On(n.cavity) && parts.IsRemoved(c.id);
                Check($"parts.{c.id}.remove", steps, $"{n} last=\"{parts.LastAction}\"");

                var view = parts.ViewOf(c.id);
                WorldLabels.Refresh();
                bool label = view != null && view.LabelText.EndsWith("estimated") && view.LabelShown;
                Check($"parts.{c.id}.label", label, $"\"{view?.LabelText}\" shown={view?.LabelShown} pool={WorldLabels.Used}/{WorldLabels.Max}");

                // 2. One undoable edit: undo puts it back, redo takes it out again.
                bool undo = EditHistory.Undo() && !parts.IsRemoved(c.id) && On(n.visual) && On(n.collision) && Off(n.cavity);
                bool redo = EditHistory.Redo() && parts.IsRemoved(c.id) && Off(n.visual) && Off(n.collision) && On(n.cavity);
                Check($"parts.{c.id}.undo", undo && redo, $"undo={undo} redo={redo}");

                if (CavityBox.TryFrom(c, out var box, out var why))
                {
                    // 3. No ghost: a ray from in front into the middle of the gap stops on the cavity, not the part.
                    var eye = root.transform.TransformPoint(root.PackageToRoot(box.FrontCentre + box.D * 0.8f + box.U * 0.1f));
                    var at = root.transform.TransformPoint(root.PackageToRoot(box.Centre));
                    bool hitSomething = Physics.Raycast(eye, (at - eye).normalized, out var hit, 20f, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore);
                    bool ghost = hitSomething && (Under(hit.transform, n.collision) || Under(hit.transform, n.visual));
                    bool inGap = hitSomething && (n.cavity == null || Under(hit.transform, n.cavity) || !ghost);
                    Check($"parts.{c.id}.ghost", hitSomething && !ghost && inGap,
                        $"hit={(hitSomething ? hit.collider.name : "nothing")} at {(hitSomething ? F(root.transform.InverseTransformPoint(hit.point)) : "-")}");

                    // 4. The tape, the real tool: width (kept on the last one), depth, height.
                    Vector3 ToRoot(Vector3 p) => root.PackageToRoot(p);
                    var size = view != null ? view.SizeScene : box.Size * root.Calibration;
                    var width = MeasureScenarios.Run(ScenePartsFixtures.Width(c.id, box, ToRoot, size.x), tool, hub, root.transform, seed, removeAfter: !(keepLast && last));
                    Check(width.Id, width.Passed, width.Detail);
                    var depth = MeasureScenarios.Run(ScenePartsFixtures.Depth(c.id, box, ToRoot, size.z), tool, hub, root.transform, seed, removeAfter: true);
                    Check(depth.Id, depth.Passed, depth.Detail);
                    if (!box.OpenTop)
                    {
                        var height = MeasureScenarios.Run(ScenePartsFixtures.Height(c.id, box, ToRoot, size.y), tool, hub, root.transform, seed, removeAfter: true);
                        Check(height.Id, height.Passed, height.Detail);
                    }
                }
                else Check($"parts.{c.id}.cavity", false, $"no usable cavity: {why}");

                // 5. Back in (the last one stays out for the capture when keepLast).
                if (keepLast && last) continue;
                bool back = AppCommands.RestoreComponent(c.id) && On(n.visual) && On(n.collision) && Off(n.cavity) && !parts.IsRemoved(c.id);
                Check($"parts.{c.id}.restore", back, $"{n}");
            }
            string summary = $"Scene parts: {pass}/{total} passed ({list.Count} components, site {root.Site})";
            Log.Check("parts.summary", pass == total, $"passed={pass} total={total} components={list.Count} site={root.Site}");
            return summary + report;
        }

        static bool On(Transform t) => t == null || t.gameObject.activeSelf;
        static bool Off(Transform t) => t == null || !t.gameObject.activeSelf;
        static bool Under(Transform t, Transform node) => node != null && t != null && t.IsChildOf(node);
        static string F(Vector3 v) => $"({v.x.ToString("0.000", C)}, {v.y.ToString("0.000", C)}, {v.z.ToString("0.000", C)})";

        /// Settings ▸ Take out, chip i, like a poke: opens Settings, presses the chip (AppCommands.ToggleComponent).
        public static string Chip(int i)
        {
            AppCommands.ShowScenePanel(true);
            var row = Object.FindAnyObjectByType<AirTools.Structure.ScenePartsRow>(FindObjectsInactive.Exclude);
            if (row == null) return "no Take out row (Settings closed, or the scene not wired: AirTools ▸ Wire Main Scene)";
            row.Refresh(force: true);
            string id = i >= 0 && i < row.ChipIds.Length ? row.ChipIds[i] : null;
            bool ok = row.Press(i);
            return $"chip {i} ({id ?? "none"}): {(ok ? "pressed" : "nothing there")} | chips [{string.Join(", ", row.chips.Select(b => b.gameObject.activeSelf ? $"{b.Text}{(b.selected ? " (out)" : "")}" : "-"))}] | {Status()}";
        }

        /// Run one action the way the backend sends it (AgentActions, the same path as a voice reply).
        public static string Act(string name, JObject args)
        {
            bool ok = AgentActions.Execute(new AgentAction { name = name, args = args ?? new JObject() });
            return $"{name}: {(ok ? "ok" : "refused")} | {AgentActions.LastResult} | {Status()}";
        }

        /// "Measure and replace" as the voice flow would run it, through AgentActions.ExecuteAll: the job strip
        /// (job_started), remove_component, a job_step, place_part into the cavity (no pose: the insert frame), job_done.
        /// The part loads asynchronously: poll AgentHarness.PartInfo(). Needs the parts server for `partId`.
        public static string MeasureAndReplace(string componentId, string partId, string modelUrl = null, bool fits = true, float clearanceMm = 10f)
        {
            string run = $"parts-{Time.frameCount}";
            var actions = new List<AgentAction>
            {
                new AgentAction { name = "job_started", args = JObject.Parse($"{{\"run_id\": \"{run}\", \"steps\": [\"remove\", \"measure\", \"place\"]}}") },
                new AgentAction { name = "remove_component", args = new JObject { ["component_id"] = componentId } },
                new AgentAction { name = "job_step", args = JObject.Parse($"{{\"run_id\": \"{run}\", \"i\": 0, \"name\": \"remove\", \"status\": \"done\", \"spoken\": \"Took it out\"}}") },
                new AgentAction { name = "place_part", args = new JObject
                {
                    ["part_id"] = partId, ["model_url"] = modelUrl ?? $"/parts/{partId}/model.glb", ["fits"] = fits,
                    ["clearance_mm"] = new JObject { ["w"] = clearanceMm, ["h"] = clearanceMm * 2f, ["d"] = clearanceMm * 1.5f },
                } },
                new AgentAction { name = "job_step", args = JObject.Parse($"{{\"run_id\": \"{run}\", \"i\": 2, \"name\": \"place\", \"status\": \"done\", \"spoken\": \"Placed\"}}") },
                new AgentAction { name = "job_done", args = JObject.Parse($"{{\"run_id\": \"{run}\", \"status\": \"done\"}}") },
            };
            int ok = AgentActions.ExecuteAll(actions, "Measure and replace");
            return $"{ok}/{actions.Count} actions carried out; poll AgentHarness.PartInfo() for the part | {Status()}";
        }
    }
}
#endif
