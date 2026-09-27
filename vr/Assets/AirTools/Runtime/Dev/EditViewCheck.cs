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
using AirTools.UI;
using UnityEngine;

namespace AirTools.Dev
{
    /// edit6dof: the Edit view in the running app (AgentHarness.EditViewCheck), on synthetic-facade-parts (load it first:
    /// BackendHarness.LoadSite("synthetic-facade-parts")), through the real input path — the ToolInputHub (pointer and grip
    /// overrides, presses, the grip squeeze) and, edit-touch, every control **poked by name** through its own GlassButton
    /// (Poke: GlassButton.Press, the path a fingertip or a controller's poke tip takes; nothing falls back to the view's
    /// calls). cab1 out, the AC in its cavity, then: the context menu by the grip and by a long pinch, within reach; Edit →
    /// the isolated view (UI closed, the dim, the copy at the centre), every control within reach and poke-only; arrow
    /// pokes (steps, fine), held pokes (repeat after 0.4 s, every 0.12 s, one axis only), turning it by hand (grip roll); a
    /// swatch and the shade; Save → one undo step, back in the room with it all; Undo / Redo; Cancel (nothing kept); Move
    /// to the floor and Save (a new spot, the rotation untouched: a translation only; Undo puts it back in the gap); a new
    /// part oriented, shaded and placed with exactly that rotation (auto-saves); Delete and its Undo; View similar. Every
    /// result is an [AirTools.Check] editview.* line. Animations and holds are stepped on the view's clock (the eval is one
    /// frame).
    public static class EditViewCheck
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        const string Cab = "cab1", Ac = "window-ac-small";

        public static string Run(bool leaveOpen = true)
        {
            var view = EditView.Current != null ? EditView.Current : Services.Get<EditView>();
            var editor = PlacementEditor.Current != null ? PlacementEditor.Current : Services.Get<PlacementEditor>();
            var tool = Services.Get<PartTool>(); var loader = Services.Get<PartLoader>();
            var parts = Services.Get<SceneParts>(); var root = Services.Get<SceneRoot>(); var hub = Services.Get<ToolInputHub>();
            if (view == null || editor == null || tool == null || loader == null || root == null || hub == null)
                return "missing EditView / PlacementEditor / PartTool / PartLoader / SceneRoot / ToolInputHub (AirTools ▸ Wire Main Scene)";
            if (parts == null || !parts.HasParts || parts.Doc.Find(Cab) == null)
                return $"no scene part {Cab}: load it first, BackendHarness.LoadSite(\"{ScenePartsFixtures.Site}\")";
            if (AppState.Mode != AppMode.World) AppCommands.OpenChest();
            root.SetVisible(true);
            UiSettings.UseUnits(UnitSystem.Imperial);
            if (view.Active) view.Finish(keep: false);
            parts.ResetSession();
            tool.ClearAll();
            editor.ResetSession();
            Physics.SyncTransforms();

            float t = Time.unscaledTime;
            view.Clock = () => t;
            void Step(float seconds) { for (float s = 0f; s < seconds; s += 0.05f) { t += 0.05f; view.Tick(0.05f); } }

            var report = new StringBuilder();
            int pass = 0, total = 0;
            void Check(string id, bool ok, string detail)
            {
                total++;
                if (ok) pass++;
                Log.Check($"editview.{id}", ok, detail);
                report.Append($"\n{(ok ? "PASS" : "FAIL")} editview.{id} {detail}");
            }
            string V(Vector3 v, float k = 1f) => $"({(v.x * k).ToString("0.#", C)}, {(v.y * k).ToString("0.#", C)}, {(v.z * k).ToString("0.#", C)})";

            try
            {
                // 1. cab1 out, the AC in its cavity (place_part's pose).
                Check("remove", AppCommands.RemoveComponent(Cab) && parts.IsRemoved(Cab), parts.LastAction);
                var ac = PlacementCheck.PlaceInCavity(loader, tool, root, Ac, out string why);
                if (ac == null) { Check("place", false, why); return Summary(pass, total, report); }
                var spot = editor.SpotOf(ac);
                var f = editor.FrameOf(spot);
                var eye = root.transform.TransformPoint(f.Origin + f.Out * 1.2f + f.Up * 1.1f);
                Pose AimAt(Vector3 world) => ToolInputHub.RayPose(eye, world);

                // 2. The context menu: the grip squeeze at it, then a long pinch with the Part tool — within reach.
                var tools = Services.Get<ToolManager>();
                tools?.Equip(ToolKind.Measure);
                hub.SetPointerOverride(ToolHand.Right, AimAt(ac.WorldBoxCentre));
                hub.RaiseButton(ToolHand.Right, ToolButton.Context);
                Check("menu.grip", view.menu != null && view.menu.Showing && view.menu.Part == ac, view.LastAction);
                var head = view.head != null ? view.head : Camera.main.transform;
                {
                    bool shown = view.menu != null && view.menu.Showing;
                    var m = shown ? view.menu.transform.position : head.position;
                    float md = Vector3.Distance(head.position, m), drop = head.position.y - m.y;
                    Check("menu.reach", shown && md > 0.35f && md < 0.5f && drop > 0.05f && drop < 0.25f,
                        $"the card {md.ToString("0.00", C)} m from the eye, {(drop * 100f).ToString("0", C)} cm below it");
                }
                view.menu?.Hide();
                tools?.Equip(ToolKind.Part);
                hub.RaisePressStart(ToolHand.Right, AimAt(ac.WorldBoxCentre));
                Step(0.3f);
                bool early = view.menu != null && view.menu.Showing;
                Step(0.4f);
                hub.RaisePressEnd(ToolHand.Right, AimAt(ac.WorldBoxCentre));
                Check("menu.hold", !early && view.menu != null && view.menu.Showing && view.menu.Part == ac && tool.Held == null && tool.PlacedParts.Contains(ac),
                    $"after 0.3 s: {early}, after 0.7 s: {view.menu?.Showing} | {view.LastAction}");

                // 3. Edit (poked on the menu): the isolated view, within reach, every control poke-only.
                if (Services.TryGet<NotebookPanel>(out var notebook)) notebook.SetOpen(true);
                int undo0 = editor.UndoCount;
                var rot0 = ac.transform.rotation;
                bool poked = Poke(view, "menu:Edit");
                bool opening = view.Phase == EditPhase.Opening;
                Step(0.6f);
                bool hiddenReal = ac.Model != null && !ac.Model.gameObject.activeSelf;
                Check("open", poked && opening && view.Phase == EditPhase.Orient && view.Part == ac && hiddenReal && editor.EditViewActive,
                    $"poked={poked} opening={opening} → {view.Report()}");
                Check("isolated", (notebook == null || !notebook.IsOpen) && PlacementFocus.Instance.On && ModelViewDeclutter.EditViewHides
                                  && view.dim != null && view.dim.Target > 0f && view.dim.WritesDepth && view.panel.Showing,
                    $"notebook open={notebook?.IsOpen} focus={PlacementFocus.Instance.On} dim={view.dim?.Target} depth={view.dim?.WritesDepth} panel={view.panel?.Showing}");
                Check("reach", Reach(view, head, out string reach) && ac.transform.position != view.proxyRoot.position, reach);
                Check("poke_only", PokeOnly(view, out string buttons), buttons);

                // 4. Arrow pokes: tilt up = +5°, fine = +1°.
                Poke(view, "TiltUp");
                editor.TryReadout(ac, out var off, out var ttr);
                Check("tap.tilt", Mathf.Abs(ttr.y - 5f) < 0.05f && Mathf.Abs(ttr.x) < 0.05f && Mathf.Abs(ttr.z) < 0.05f && view.arrows.Readout().Contains("Tilt 5°"),
                    $"turn/tilt/roll={V(ttr)} readout=\"{view.arrows.Readout()}\"");
                Step(0.05f);   // the finger is out: the repeat ends
                editor.SetFine(true);
                Poke(view, "TiltDown");
                Step(0.05f);
                editor.TryReadout(ac, out off, out ttr);
                Check("tap.fine", Mathf.Abs(ttr.y - 4f) < 0.05f, $"tilt {ttr.y.ToString("0.##", C)}° (fine −1°)");
                editor.SetFine(false);

                // 5. Held pokes: Turn ← held 1 s repeats (after 0.4 s, then every 0.12 s) on the turn only; Right held 0.5 s.
                editor.TryReadout(ac, out var off0, out var ttr0);
                int reps0 = view.Repeats;
                Hold(view, "TurnLeft", 1.0f, Step);
                int turns = 1 + view.Repeats - reps0, want = HoldRepeat.Count(1.0f);
                editor.TryReadout(ac, out off, out ttr);
                Check("hold.turn", Mathf.Abs(turns - want) <= 1 && Mathf.Abs(ttr.x - ttr0.x - 5f * turns) < 0.1f && Mathf.Abs(ttr.y - ttr0.y) < 0.05f
                                   && Mathf.Abs(ttr.z - ttr0.z) < 0.05f && (off - off0).magnitude < 1e-4f && view.Pressed < 0,
                    $"{turns} steps in 1 s (want {want}): turn/tilt/roll {V(ttr0)} → {V(ttr)}");
                int reps1 = view.Repeats;
                Hold(view, "Right", 0.5f, Step);
                int rights = 1 + view.Repeats - reps1;
                float step = Edit6DofMath.Step(EditAxis.Right, UiSettings.UnitSystem, false);
                editor.TryReadout(ac, out var off2, out var ttr2);
                Check("hold.right", rights >= 2 && Mathf.Abs(off2.x - off.x - rights * step) < 0.002f && Mathf.Abs(off2.y - off.y) < 1e-4f && Mathf.Abs(off2.z - off.z) < 1e-4f
                                    && (ttr2 - ttr).magnitude < 0.05f,
                    $"{rights} steps in 0.5 s: offset_mm {V(off, 1000f)} → {V(off2, 1000f)} (want +{(rights * step * 1000f).ToString("0.#", C)} mm)");

                // 6. Turned by hand: pinch the item, roll the grip 15° about the view axis.
                var c = view.stage.position;
                var rot2 = ac.transform.rotation;
                var body = ToolInputHub.RayPose(head.position, c);
                hub.SetGripOverride(ToolHand.Right, new Pose(head.position, Quaternion.identity));
                hub.RaisePressStart(ToolHand.Right, body);
                hub.SetGripOverride(ToolHand.Right, new Pose(head.position, PlacementMath.AxisAngle(view.stage.forward, -15f)));
                view.Tick(0.05f);
                hub.RaisePressEnd(ToolHand.Right, body);
                hub.SetGripOverride(ToolHand.Right, null);
                editor.TryReadout(ac, out var off3, out var ttr3);
                // The item turns with the hand: 15° in all. The stage sits below the eye now (edit-touch), so a twist about the
                // line of sight is part roll, part tilt/turn in the item's own terms.
                float byHand = Quaternion.Angle(rot2, ac.transform.rotation);
                Check("hand.roll", Mathf.Abs(byHand - 15f) < 1.5f, $"turned {byHand.ToString("0.0", C)}° for a 15° grip twist; turn/tilt/roll {V(ttr2)} → {V(ttr3)}");

                // 7. Colour: Navy, then light.
                Poke(view, "Swatch5");
                var navy = ac.Shade;
                Poke(view, "Shade");
                Check("shade", navy.Name == "Navy" && ac.Shade.Name == "Navy" && ac.Shade.Strength < navy.Strength && ac.FinishColor.HasValue,
                    $"{navy} → {ac.Shade} finish={ac.FinishName}");

                // 8. Save: back in the room with all of it, one undo step, the UI back.
                var poseEdited = ac.transform.rotation;
                Poke(view, "Save");
                bool closing = view.Phase == EditPhase.Closing;
                Step(0.6f);
                Check("save", closing && view.Phase == EditPhase.Idle && editor.UndoCount == undo0 + 1 && !editor.IsAdjusting && ac.Model.gameObject.activeSelf
                              && Quaternion.Angle(ac.transform.rotation, poseEdited) < 0.1f && Quaternion.Angle(ac.transform.rotation, rot0) > 10f,
                    $"undo steps {undo0} → {editor.UndoCount}; turned {Quaternion.Angle(rot0, ac.transform.rotation).ToString("0.#", C)}° | {view.Report()}");
                Check("restored", !ModelViewDeclutter.EditViewHides && view.dim.Target == 0f && !PlacementFocus.Instance.On && (notebook == null || notebook.IsOpen),
                    $"dim={view.dim.Target} focus={PlacementFocus.Instance.On} notebook back={notebook?.IsOpen}");
                notebook?.SetOpen(false);

                // 9. Undo / Redo: the whole session (pose and shade) in one step.
                bool u = EditHistory.Undo();
                bool back = Quaternion.Angle(ac.transform.rotation, rot0) < 0.1f && ac.Shade.IsOriginal;
                bool r = EditHistory.Redo();
                Check("undo", u && back && r && Quaternion.Angle(ac.transform.rotation, poseEdited) < 0.1f && ac.Shade.Name == "Navy",
                    $"undo → at start={back}, redo → edited={Quaternion.Angle(ac.transform.rotation, poseEdited) < 0.1f} shade={ac.Shade}");

                // 10. Cancel keeps nothing.
                int undo1 = editor.UndoCount;
                var rot1 = ac.transform.rotation;
                view.OpenPlaced(ac);
                Step(0.6f);
                Poke(view, "RollRight");
                Step(0.05f);
                Poke(view, "Swatch2");
                Poke(view, "Cancel");
                Step(0.6f);
                Check("cancel", view.Phase == EditPhase.Idle && editor.UndoCount == undo1 && Quaternion.Angle(ac.transform.rotation, rot1) < 0.1f && ac.Shade.Name == "Navy",
                    $"undo steps {undo1} → {editor.UndoCount}; shade {ac.Shade} | {view.LastAction}");

                // 11. Move to the floor in front of the cabinets, Save (a new spot, its rotation untouched: a translation
                // only); Undo puts it back in the gap, turned as it was.
                int undo2 = editor.UndoCount;
                var inGap = root.transform.InverseTransformPoint(ac.transform.position);
                var turnGap = ac.transform.rotation;
                view.OpenPlaced(ac);
                Step(0.6f);
                Poke(view, "Move");
                Step(0.6f);
                bool moving = view.Phase == EditPhase.Moving && view.panel.MoveBarOpen && ac.Model.gameObject.activeSelf && !view.dim.WritesDepth;
                var turnMove = ac.transform.rotation;
                var floor = root.transform.TransformPoint(f.Origin + f.Out * 0.9f + f.Right * 0.4f);
                var aim = AimAt(floor);
                hub.RaisePressStart(ToolHand.Right, aim);
                view.Tick(0.05f);
                hub.RaisePressEnd(ToolHand.Right, aim);
                var moved = root.transform.InverseTransformPoint(ac.transform.position);
                Poke(view, "bar:Save");
                var newSpot = editor.SpotOf(ac);
                float turned = Quaternion.Angle(turnMove, ac.transform.rotation);
                Check("move", moving && view.Phase == EditPhase.Idle && Vector3.Distance(moved, inGap) > 0.2f && editor.UndoCount == undo2 + 1 && newSpot != null && newSpot.CavityId == null,
                    $"moving={moving} {V(inGap, 1000f)} → {V(moved, 1000f)} mm, spot={newSpot} bar=\"{view.panel.LastMoveText}\"");
                Check("move.keeps_turn", turned < 0.01f && Quaternion.Angle(turnGap, turnMove) < 0.01f, $"turned {turned.ToString("0.###", C)}° by the move (a translation only)");
                EditHistory.Undo();
                Check("move.undo", Vector3.Distance(root.transform.InverseTransformPoint(ac.transform.position), inGap) < 1e-3f && editor.SpotOf(ac)?.CavityId == Cab
                                   && Quaternion.Angle(ac.transform.rotation, turnGap) < 0.01f,
                    $"back at {V(root.transform.InverseTransformPoint(ac.transform.position), 1000f)} spot={editor.SpotOf(ac)}, turn off by {Quaternion.Angle(ac.transform.rotation, turnGap).ToString("0.###", C)}°");

                // 12. A new part: oriented (roll 5°), shaded and placed with exactly that rotation; the pinch saves and closes.
                var fresh = loader.LoadFromCatalog(Ac);
                bool opened = fresh != null && view.OpenNew(fresh);
                Step(0.6f);
                Poke(view, "RollRight");
                Step(0.05f);
                Poke(view, "Swatch2");
                bool rolled = Mathf.Abs(view.NewTurn.z - 5f) < 0.05f;
                var oriented = fresh != null ? fresh.transform.rotation : Quaternion.identity;
                Poke(view, "Place");
                bool inHand = tool.Held == fresh;
                var aimFloor = AimAt(root.transform.TransformPoint(f.Origin + f.Out * 1.0f - f.Right * 0.5f));
                hub.SetPointerOverride(ToolHand.Right, aimFloor);
                tool.Tick();
                hub.RaisePressStart(ToolHand.Right, aimFloor);
                hub.RaisePressEnd(ToolHand.Right, aimFloor);
                float tilt = fresh != null ? Vector3.Angle(fresh.transform.up, root.transform.up) : -1f;
                float kept = fresh != null ? Quaternion.Angle(oriented, fresh.transform.rotation) : -1f;
                Check("new", opened && rolled && inHand && fresh.Placed && view.Phase == EditPhase.Idle && fresh.Shade.Name == "Black" && Mathf.Abs(tilt - 5f) < 0.6f,
                    $"opened={opened} rolled={rolled} in hand={inHand} placed={fresh?.Placed} up off by {tilt.ToString("0.#", C)}° shade={fresh?.Shade} | {view.LastAction}");
                Check("new.keeps_turn", kept >= 0f && kept < 0.01f && editor.HeldTurnPart == null,
                    $"placed turned {kept.ToString("0.###", C)}° from the Edit view's orientation (a translation only)");

                // 13. Delete and its Undo, poked on the menu; View similar opens the Catalog.
                hub.SetPointerOverride(ToolHand.Right, AimAt(ac.WorldBoxCentre));
                hub.RaiseButton(ToolHand.Right, ToolButton.Context);
                Poke(view, "menu:Delete");
                bool gone = !tool.PlacedParts.Contains(ac) && !ac.gameObject.activeSelf && view.menu.UndoShowing;
                Poke(view, "menu:Undo");
                Check("delete", gone && tool.PlacedParts.Contains(ac) && ac.gameObject.activeSelf && !view.menu.Showing, $"gone={gone} back={tool.PlacedParts.Contains(ac)} | {view.LastAction}");
                hub.RaiseButton(ToolHand.Right, ToolButton.Context);
                Poke(view, "menu:Similar");
                bool catalog = Services.TryGet<CatalogWindow>(out var cat) && cat.IsOpen;
                Check("similar", catalog, view.LastAction);
                if (cat != null) cat.Hide();

                // For the captures: the AC in the Edit view, the world dimmed.
                hub.ClearOverrides();
                if (leaveOpen) { view.OpenPlaced(ac); Step(0.6f); }
            }
            finally
            {
                hub.ClearOverrides();
                view.Clock = null;
            }
            return Summary(pass, total, report) + "\n" + view.Report();
        }

        /// edit-touch: poke a control of the view by name through its own GlassButton (Press: what a fingertip's or a
        /// controller tip's poke calls): an arrow ("TiltUp"), a panel button ("Save", "Swatch5", "Step"), the move bar's
        /// ("bar:Save") or the menu's ("menu:Edit"). False when it isn't there, isn't showing or didn't press.
        public static bool Poke(EditView view, string name)
        {
            var b = Find(view, name);
            if (b == null || !b.isActiveAndEnabled || !b.interactable) { Log.Warn($"EditViewCheck: no {name} to poke"); return false; }
            if (name.StartsWith("menu:") && view.menu != null) view.menu.ArmNow();   // the check runs in one frame: past its arm delay
            return ForcePress(b);
        }

        /// edit-touch: poke arrow `name` and keep the finger in for `seconds` (the view's clock, `step`), then take it out.
        public static void Hold(EditView view, string name, float seconds, System.Action<float> step)
        {
            if (!Poke(view, name)) return;
            view.SimulateHold(true);
            step(seconds);
            view.SimulateHold(false);
            step(0.05f);
        }

        /// The button by name: "menu:…" on the context menu, "bar:…" on the move bar, else an arrow knob, else the panel.
        public static GlassButton Find(EditView view, string name)
        {
            if (view == null || string.IsNullOrEmpty(name)) return null;
            Component root;
            if (name.StartsWith("menu:")) { root = view.menu; name = name.Substring(5); }
            else if (name.StartsWith("bar:")) { root = view.panel != null ? view.panel.moveWindow : null; name = name.Substring(4); }
            else
            {
                var k = view.arrows != null ? view.arrows.Knob(name) : null;
                if (k != null) return k;
                root = view.panel != null && view.panel.root != null ? view.panel.root.transform : null;
            }
            if (root == null) return null;
            foreach (var b in root.GetComponentsInChildren<GlassButton>(true)) if (b.name == name) return b;
            return null;
        }

        /// Every enabled arrow and the panel within reach: the knob plane 0.38–0.47 m from the eye, the panel 0.40–0.47 m
        /// and below the eye, the shown item's largest side at most 0.25 m.
        static bool Reach(EditView view, Transform head, out string detail)
        {
            var eye = head.position;
            view.arrows.Tick();
            float near = float.MaxValue, far = 0f;
            for (int i = 0; i < view.arrows.Count; i++)
            {
                if (!view.arrows.Enabled[i]) continue;
                float d = Vector3.Distance(eye, view.arrows.World[i]);
                near = Mathf.Min(near, d);
                far = Mathf.Max(far, d);
            }
            var p = view.panel.root.transform.position;
            float pd = Vector3.Distance(eye, p), pdrop = eye.y - p.y, sdrop = eye.y - view.stage.position.y;
            var box = view.Part != null ? view.Part.LocalBox.size : Vector3.zero;
            float shown = Mathf.Max(box.x, Mathf.Max(box.y, box.z)) * (view.Part != null && view.Part.transform.parent != null ? view.Part.transform.parent.lossyScale.x : 1f) * view.ShownScale;
            var facing = Vector3.Dot(view.panel.root.transform.forward, (p - eye).normalized);
            detail = $"arrows {near.ToString("0.00", C)}–{far.ToString("0.00", C)} m, panel {pd.ToString("0.00", C)} m ({(pdrop * 100f).ToString("0", C)} cm down, facing {facing.ToString("0.000", C)}), " +
                     $"item {(sdrop * 100f).ToString("0", C)} cm down, largest side shown {(shown * 100f).ToString("0.#", C)} cm";
            return near > 0.36f && far < 0.48f && pd > 0.4f && pd < 0.47f && pdrop > 0.1f && pdrop < 0.35f && sdrop > 0f && facing > 0.99f && shown < 0.2505f;
        }

        /// Every button of the view, its move bar and the menu: an ISDK poke, and no ray.
        static bool PokeOnly(EditView view, out string detail)
        {
            var all = new System.Collections.Generic.List<GlassButton>();
            all.AddRange(view.GetComponentsInChildren<GlassButton>(true));
            if (view.panel != null && view.panel.moveWindow != null) all.AddRange(view.panel.moveWindow.GetComponentsInChildren<GlassButton>(true));
            if (view.menu != null) all.AddRange(view.menu.GetComponentsInChildren<GlassButton>(true));
            var bad = all.Where(b => b.poke == null || b.ray != null).Select(b => b.name).ToList();
            detail = $"{all.Count} buttons, {bad.Count} not poke-only{(bad.Count > 0 ? ": " + string.Join(", ", bad) : "")}";
            return all.Count >= 30 && bad.Count == 0;
        }

        /// GlassButton.Press has a cooldown on the real clock; the check presses faster than that, so it clears it.
        static bool ForcePress(GlassButton b)
        {
            var f = typeof(GlassButton).GetField("m_Last", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            f?.SetValue(b, -999f);
            return b.Press();
        }

        static string Summary(int pass, int total, StringBuilder report)
        {
            Log.Check("editview.summary", pass == total, $"passed={pass} total={total}");
            return $"Edit view: {pass}/{total} passed" + report;
        }
    }
}
#endif
