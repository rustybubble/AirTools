#if UNITY_EDITOR || DEVELOPMENT_BUILD
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
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Dev
{
    /// delete-undo: removing a placed part is one undoable step, in the running app (AgentHarness.DeleteUndoCheck), on the
    /// built-in facade (the default scene; RunM4's). A catalog hanger goes on the fascia through the real release path and
    /// takes the Brown finish; then it's removed by the spec card's Remove (tap twice: the GlassButton's confirm, when the
    /// palm is open — force it open in an eval before, `Services.Get<PalmMenu>().Force(true)` — else the button's handler),
    /// by the context menu's Delete (and its "Deleted · Undo" chip), and by voice (delete_part). Each time: gone (out of the
    /// scene and the notebook, hidden and kept), the toast says "Undo on the ring", Undo (the ring's, EditHistory) brings it
    /// back exactly (pose, finish, fit, selection, the same notebook row) and Redo removes it again. Then an array member,
    /// Clear (one step for all eight), and a delete whose part is dropped for good once a new placement ends its redo. Every
    /// result is an [AirTools.Check] deleteundo.* line; it leaves the eight-hanger array on the fascia.
    public static class DeleteUndoCheck
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        const string Hanger = "hidden-hanger-5k";
        static readonly Vector3 Ladder = new Vector3(0.5f, 7.0f, 1.5f);   // a raised vantage: the gutter's lip hides the fascia from the ground

        /// The part the last run dropped for good (destroyed at the end of that frame): DeleteUndoStatus reports it.
        static PartInstance s_Dropped;
        static bool s_HadDropped;

        public static string Run()
        {
            var tool = Services.Get<PartTool>(); var loader = Services.Get<PartLoader>(); var root = Services.Get<SceneRoot>();
            if (tool == null || loader == null || root == null) return "missing PartTool / PartLoader / SceneRoot (AirTools ▸ Wire Main Scene)";
            var view = EditView.Current != null ? EditView.Current : Services.Get<EditView>();
            var editor = PlacementEditor.Current != null ? PlacementEditor.Current : Services.Get<PlacementEditor>();
            if (AppState.Mode != AppMode.World) AppCommands.OpenChest();
            root.SetVisible(true);
            UiSettings.UseUnits(UnitSystem.Imperial);
            if (view != null && view.Active) view.Finish(keep: false);
            view?.menu?.Hide();
            tool.ClearAll();
            editor?.ResetSession();
            Physics.SyncTransforms();
            var tools = Services.Get<ToolManager>();
            tools?.Equip(ToolKind.Part);

            var report = new StringBuilder();
            int pass = 0, total = 0;
            void Check(string id, bool ok, string detail)
            {
                total++;
                if (ok) pass++;
                Log.Check($"deleteundo.{id}", ok, detail);
                report.Append($"\n{(ok ? "PASS" : "FAIL")} deleteundo.{id} {detail}");
            }

            string toast = null;
            void OnLog(string message, string stack, LogType type)
            {
                const string k = "[AirTools] Toast: ";
                if (message != null && message.StartsWith(k)) toast = message.Substring(k.Length);
            }
            Application.logMessageReceived += OnLog;
            try
            {
                // 1. A hanger on the fascia, Brown.
                var a = Place(tool, loader, root.transform, 0.4f, out string why);
                if (a == null) { Check("place", false, $"{why} (the built-in facade: AppCommands.LoadSite(\"built-in\"))"); return Summary(pass, total, report); }
                tool.Select(a);
                bool brown = AppCommands.SetFinish("brown");
                var snap = Snapshot.Of(tool, a, root.transform);
                string diff = "";
                Check("place", a.Placed && brown && snap.Row != null, $"{snap} row #{snap.Row?.Id}");

                // 2. The spec card's Remove.
                toast = null;
                string how = PressRemove(tool, a);
                Check("spec.remove", Gone(tool, a, snap) && Says(toast), $"via {how}; {Gone(tool, a)} | toast \"{toast}\" | {tool.LastAction}");
                bool undo = EditHistory.Undo();
                Check("spec.undo", undo && snap.Same(tool, a, root.transform, out diff), diff);
                bool redo = EditHistory.Redo();
                Check("spec.redo", redo && Gone(tool, a, snap), Gone(tool, a));
                EditHistory.Undo();
                Check("spec.undo2", snap.Same(tool, a, root.transform, out diff), diff);

                // 3. The context menu's Delete, its "Deleted · Undo" chip, then the ring's Redo / Undo.
                if (view != null && view.menu != null)
                {
                    view.OpenMenu(a, "harness");
                    view.menu.ArmNow();   // edit-touch: the check runs in one frame, past the menu's 0.35 s arm delay
                    toast = null;
                    Press(view.menu.delete, () => view.Do(EditViewAction.MenuDelete));
                    Check("menu.delete", Gone(tool, a, snap) && view.menu.UndoShowing && Says(toast),
                        $"{Gone(tool, a)} chip={view.menu.UndoShowing} | toast \"{toast}\" | {view.LastAction}");
                    Press(view.menu.undo, () => view.Do(EditViewAction.MenuUndo));
                    Check("menu.undo", snap.Same(tool, a, root.transform, out diff), $"the chip's Undo: {diff}");
                    view.menu.Hide();
                    EditHistory.Redo();
                    Check("menu.redo", Gone(tool, a, snap), Gone(tool, a));
                    EditHistory.Undo();
                    Check("menu.ring", snap.Same(tool, a, root.transform, out diff), $"the ring's Undo: {diff}");
                }
                else Check("menu.delete", false, "no EditView in the scene (AirTools ▸ Wire Main Scene)");

                // 4. Voice: delete_part (the selected part).
                tool.Select(a);
                toast = null;
                bool said = AgentActions.Execute(new AgentAction { name = "delete_part", args = new JObject() });
                view?.menu?.Hide();
                Check("voice.delete", said && Gone(tool, a, snap) && Says(toast), $"{Gone(tool, a)} | {AgentActions.LastResult} | toast \"{toast}\"");
                EditHistory.Undo();
                Check("voice.undo", snap.Same(tool, a, root.transform, out diff), diff);

                // 5. An array member: one copy goes, Undo puts it back in its slot, Redo takes it again.
                Notebook.Add(new NotebookEntry("measure", 4.2, "m", new[] { new Vector3(-2.1f, 6.1f, 0.15f), new Vector3(2.1f, 6.1f, 0.15f) },
                    System.DateTime.Now, -1, "gutter (deleteundo)"));
                tool.Select(a);
                var g = tool.PlaceArray();
                if (g == null || g.Clones.Count == 0) Check("array", false, tool.LastAction);
                else
                {
                    int n = tool.PlacedParts.Count;
                    var clone = g.Clones[g.Clones.Count / 2];
                    tool.Select(clone);
                    var cs = Snapshot.Of(tool, clone, root.transform);
                    toast = null;
                    how = PressRemove(tool, clone);
                    bool one = tool.PlacedParts.Count == n - 1 && tool.QuantityOf(Hanger) == n - 1;
                    Check("array.remove", one && Gone(tool, clone, cs) && Says(toast), $"via {how}; {n} → {tool.PlacedParts.Count} placed, {Gone(tool, clone)}");
                    EditHistory.Undo();
                    Check("array.undo", tool.PlacedParts.Count == n && cs.Same(tool, clone, root.transform, out diff) && tool.ArrayOf(clone) == g, $"{tool.PlacedParts.Count} placed; {diff}");
                    EditHistory.Redo();
                    Check("array.redo", tool.PlacedParts.Count == n - 1 && !clone.gameObject.activeSelf, $"{tool.PlacedParts.Count} placed");
                    EditHistory.Undo();

                    // 6. Clear: all of them in one step, their rows too; one Undo brings every one back.
                    var poses = tool.PlacedParts.Select(p => Local(p, root.transform)).ToList();
                    var placed = tool.PlacedParts.ToList();
                    int rows = Notebook.Entries.Count(e => e.Tool == "part" || e.Tool == "array");
                    tool.Select(a);
                    toast = null;
                    int cleared = AppCommands.ClearParts();
                    int rowsNow = Notebook.Entries.Count(e => e.Tool == "part" || e.Tool == "array");
                    Check("clear", cleared == n && tool.PlacedParts.Count == 0 && rowsNow == 0 && placed.All(p => p != null && !p.gameObject.activeSelf)
                                   && toast != null && toast.Contains("Undo on the ring"),
                        $"{cleared} cleared, {tool.PlacedParts.Count} left, rows {rows} → {rowsNow} | toast \"{toast}\"");
                    EditHistory.Undo();
                    float worst = 0f;
                    for (int k = 0; k < placed.Count; k++) worst = Mathf.Max(worst, Vector3.Distance(Local(placed[k], root.transform).position, poses[k].position));
                    int rowsBack = Notebook.Entries.Count(e => e.Tool == "part" || e.Tool == "array");
                    Check("clear.undo", tool.PlacedParts.Count == n && rowsBack == rows && worst < 1e-4f && tool.Selected == a && tool.LastArray == g && a.FinishName == snap.Finish,
                        $"{tool.PlacedParts.Count} back, rows {rowsBack}/{rows}, worst {(worst * 1000f).ToString("0.###", C)} mm, selected {tool.Selected?.Spec.id}, finish {a.FinishName}");
                    EditHistory.Redo();
                    Check("clear.redo", tool.PlacedParts.Count == 0, $"{tool.PlacedParts.Count} placed");
                    EditHistory.Undo();
                }

                // 7. A deleted part is kept while it can come back, and dropped for good once a new placement ends its redo.
                var b = Place(tool, loader, root.transform, -1.4f, out why);
                if (b == null) Check("drop", false, why);
                else
                {
                    AppCommands.DeletePart(b);
                    view?.menu?.Hide();
                    int kept = tool.RemovedKept;
                    EditHistory.Undo();   // b back
                    EditHistory.Undo();   // b's placement undone: hidden, on the redo stack
                    bool waiting = tool.CanRedo && !b.gameObject.activeSelf;
                    var c = Place(tool, loader, root.transform, -1.9f, out why);
                    s_Dropped = b; s_HadDropped = true;
                    Check("drop", kept >= 1 && waiting && c != null && !tool.CanRedo && !b.gameObject.activeSelf && !tool.PlacedParts.Contains(b),
                        $"kept while undoable={kept >= 1}, on the redo stack={waiting}, after a new placement: redo={tool.CanRedo} (destroyed at the end of the frame: DeleteUndoStatus)");
                    if (c != null) tool.Undo();   // the array stays as it was
                }
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                view?.menu?.Hide();
            }
            return Summary(pass, total, report);
        }

        /// The part tool's census and whether the part the last run dropped is gone.
        public static string Status()
        {
            var tool = Services.Get<PartTool>();
            string dropped = !s_HadDropped ? "-" : s_Dropped == null ? "destroyed" : "still there";
            return $"placed={tool?.PlacedParts.Count} removed kept={tool?.RemovedKept} undo={EditHistory.CanUndo} redo={EditHistory.CanRedo} steps={EditHistory.StepCount} " +
                   $"dropped part={dropped} last=\"{tool?.LastAction}\"";
        }

        // ---------------- helpers ----------------

        /// A catalog hanger in the hand, released onto the fascia at x (root space) from the raised vantage.
        static PartInstance Place(PartTool tool, PartLoader loader, Transform frame, float x, out string why)
        {
            why = null;
            var p = loader.LoadFromCatalog(Hanger);
            if (p == null) { why = loader.LastError; return null; }
            tool.Hold(p);
            var target = new Vector3(x, 6.15f, S.FasciaProud);
            if (!tool.Release(ToolInputHub.RayPose(frame.TransformPoint(new Vector3(x, Ladder.y, Ladder.z)), frame.TransformPoint(target))))
            {
                why = $"not placed: {tool.LastAction}";
                tool.Undo();   // the part in hand goes back
                return null;
            }
            return p;
        }

        /// The spec card's Remove: its button twice (arm, fire) when the card is up in the open palm, else its handler.
        static string PressRemove(PartTool tool, PartInstance part)
        {
            tool.Select(part);
            var card = Services.Get<SpecCard>();
            card?.Refresh();
            var remove = card != null ? card.GetComponentsInChildren<PartsButton>(true).FirstOrDefault(b => b.action == PartsAction.Remove) : null;
            if (remove != null && remove.button != null && remove.isActiveAndEnabled && remove.button.interactable)
            {
                ForcePress(remove.button);   // arms it ("Tap to remove")
                bool still = part.Placed;
                ForcePress(remove.button);   // fires
                return still ? "the Remove button (tap twice)" : "the Remove button (fired on the first tap!)";
            }
            if (remove != null) { remove.Press(); return "the Remove handler (the palm is closed)"; }
            AppCommands.RemoveSelectedPart();
            return "AppCommands.RemoveSelectedPart (no spec card)";
        }

        static void Press(GlassButton b, System.Action fallback)
        {
            if (b != null && b.isActiveAndEnabled && b.interactable) { ForcePress(b); return; }
            fallback?.Invoke();
        }

        /// GlassButton.Press has a 0.3 s cooldown on the real clock; the check presses faster than that.
        static void ForcePress(GlassButton b)
        {
            var f = typeof(GlassButton).GetField("m_Last", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            f?.SetValue(b, -999f);
            b.Press();
        }

        static bool Says(string toast) => toast != null && toast.Contains("removed · Undo on the ring");

        static bool Gone(PartTool tool, PartInstance p, Snapshot s) =>
            p != null && !p.Placed && !p.gameObject.activeSelf && !tool.PlacedParts.Contains(p) && (s.Row == null || Notebook.Find(s.Row.Id) == null) && EditHistory.CanUndo;

        static string Gone(PartTool tool, PartInstance p) =>
            p == null ? "destroyed" : $"placed={p.Placed} active={p.gameObject.activeSelf} in list={tool.PlacedParts.Contains(p)}";

        static Pose Local(PartInstance p, Transform frame) =>
            new Pose(frame.InverseTransformPoint(p.transform.position), Quaternion.Inverse(frame.rotation) * p.transform.rotation);

        /// What Undo must bring back: the pose, the finish and shade, the fit, the selection and the notebook row.
        sealed class Snapshot
        {
            public Pose Pose;
            public string Finish, Shade, Fit;
            public FitStatus Status;
            public bool Selected;
            public NotebookEntry Row;

            public static Snapshot Of(PartTool tool, PartInstance p, Transform frame) => new Snapshot
            {
                Pose = Local(p, frame), Finish = p.FinishName, Shade = p.Shade.ToString(), Fit = p.Fit?.Headline, Status = p.Fit?.Status ?? FitStatus.None,
                Selected = tool.Selected == p,
                Row = Notebook.Entries.LastOrDefault(e => e.Tool == "part" && e.PartId == p.Spec.id && e.Points != null && e.Points.Length == 1
                                                          && Vector3.Distance(e.Points[0], frame.InverseTransformPoint(p.transform.position)) < 1e-3f),
            };

            public bool Same(PartTool tool, PartInstance p, Transform frame, out string diff)
            {
                if (p == null) { diff = "destroyed"; return false; }
                var now = Local(p, frame);
                float d = Vector3.Distance(now.position, Pose.position) * 1000f, ang = Quaternion.Angle(now.rotation, Pose.rotation);
                bool rowBack = Row == null || Notebook.Find(Row.Id) == Row;
                bool ok = p.Placed && p.gameObject.activeSelf && tool.PlacedParts.Contains(p) && d < 0.1f && ang < 0.01f
                          && p.FinishName == Finish && p.Shade.ToString() == Shade && (p.Fit?.Status ?? FitStatus.None) == Status && p.Fit?.Headline == Fit
                          && (!Selected || tool.Selected == p) && rowBack;
                diff = $"placed={p.Placed} moved {d.ToString("0.###", C)} mm / {ang.ToString("0.###", C)}° finish={p.FinishName} shade={p.Shade} " +
                       $"fit={(p.Fit != null ? p.Fit.Status.ToString() : "-")} selected={tool.Selected == p} row #{Row?.Id} back={rowBack}";
                return ok;
            }

            public override string ToString() => $"{Finish} {Status}: {Fit?.Replace('\n', ' ')}";
        }

        static string Summary(int pass, int total, StringBuilder report)
        {
            Log.Check("deleteundo.summary", pass == total, $"passed={pass} total={total}");
            return $"Delete / undo: {pass}/{total} passed" + report;
        }
    }
}
#endif
