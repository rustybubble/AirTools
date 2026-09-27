using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Tools;
using UnityEngine;

namespace AirTools.UI
{
    /// Turns app events into toasts: a saved reading, an export result, a placed part, entering/leaving the world.
    /// Words per docs/ux/specs/W0.9-copy.md (the log line "Toast: …" is what tools/demo/hcheck.py reads).
    public class ToastEvents : MonoBehaviour
    {
        PartTool m_Parts;
        NotebookController m_Notebook;

        void OnEnable()
        {
            Notebook.Added += OnAdded;
            AppState.Changed += OnMode;
        }

        void OnDisable()
        {
            Notebook.Added -= OnAdded;
            AppState.Changed -= OnMode;
            if (m_Parts != null) { m_Parts.PartPlaced -= OnPlaced; m_Parts.ArrayPlaced -= OnArray; }
            if (m_Notebook != null) m_Notebook.ExportStatusChanged -= OnExport;
            m_Parts = null; m_Notebook = null;
        }

        void Update()
        {
            // Late-bound services (they register in their own OnEnable).
            if (m_Parts == null && Services.TryGet<PartTool>(out var p)) { m_Parts = p; p.PartPlaced += OnPlaced; p.ArrayPlaced += OnArray; }
            if (m_Notebook == null && Services.TryGet<NotebookController>(out var n)) { m_Notebook = n; n.ExportStatusChanged += OnExport; }
        }

        static void OnAdded(NotebookEntry e)
        {
            // Parts and arrays have their own toasts; BOM, scale and purchases are toasted by their producers.
            switch (e.Tool) { case "part": case "array": case "bom": case "scale": case "purchase": return; }
            if (GuideRail.Enabled && (e.Tool == "measure" || e.Tool == "level")) return;   // W1.3: the status line says it (R42–R44, R48)
            UiToast.Show(NotebookRow.SavedToast(e, TapeTargetOf(e)), ColorRole.Success);
        }

        /// "Door" when both ends of a new tape sit on the same door (structure layer), else null.
        static string TapeTargetOf(NotebookEntry e)
        {
            if (e.Tool != "measure" || e.Points == null || e.Points.Length != 2 || !Services.TryGet<AirTools.Scene.SceneRoot>(out var root)) return null;
            var t = SurfaceNames.ClassifyTape(e.Points[0], e.Points[1], root);
            return t == TapeTarget.Run ? null : SurfaceNames.TargetNoun(t);
        }

        public static ColorRole Tone(FitStatus status) =>
            status == FitStatus.Green ? ColorRole.Success : status == FitStatus.Amber ? ColorRole.Warning : ColorRole.Danger;

        static void OnPlaced(PartInstance part)
        {
            if (GuideRail.Enabled) return;   // W1.3: R34–R38
            var status = part.Fit?.Status ?? FitStatus.None;
            UiToast.Show(Copy.FitLine(part.Fit), Tone(status));
        }

        /// "✓ 8 hangers placed · every 600 mm · all fit".
        static void OnArray(PartTool.ArrayGroup g)
        {
            if (g?.Reference == null) return;
            var members = PartTool.ArrayMembers(g);
            int green = 0;
            foreach (var m in members) if (m.Fit != null && m.Fit.Status == FitStatus.Green) green++;
            var spec = g.Reference.Spec;
            string noun = Copy.Noun(spec, g.Reference.SearchQuery);
            float spacing = g.Plan.Count > 1 ? g.Plan.SpacingM * 1000f : spec.spacing_mm ?? 0f;
            string text = $"✓ {members.Count} {(members.Count == 1 ? noun : Copy.Plural(noun))} placed · every {Copy.Gap(spacing)}" + (green == members.Count ? " · all fit" : "");
            UiToast.Show(text, green == members.Count ? ColorRole.Success : ColorRole.Warning);
        }

        static void OnExport(string status)
        {
            if (GuideRail.Enabled) return;   // W1.3: R03 / R18
            if (status == "uploaded") UiToast.Show("✓ Report sent to the laptop", ColorRole.Success);
            else if (status == "up to date") UiToast.Show("✓ Report already sent", ColorRole.Success);
            else if (status.StartsWith("saved")) UiToast.Show("Report saved on the headset", ColorRole.Success);
            else if (status.StartsWith("error")) UiToast.Show("Couldn't save the report · try again", ColorRole.Danger);
        }

        static void OnMode(AppMode from, AppMode to)
        {
            if (to != AppMode.World || from != AppMode.Passthrough) return;
            if (GuideRail.Enabled) return;   // W1.3: R49 / R52 and coach C12 replace the entry hint
            UiToast.Show(EntryHint(), ColorRole.Info);
        }

        /// The first thing said on entering the world: on a scanned place, how to use the tool in hand (Move or
        /// Measure, see ToolManager.Default); on the built-in facade, how to open the tools.
        public static string EntryHint()
        {
            bool controllers = InputMode.Controllers;
            bool scanned = Services.TryGet<AirTools.Scene.SceneRoot>(out var root) && root.IsRuntimePackage;
            if (!scanned) return controllers ? "Press the left menu button for tools" : "Turn your left palm up for tools";
            var tool = Services.TryGet<ToolManager>(out var tools) && tools.Active != ToolKind.None ? tools.Active : ToolManager.Default;
            if (tool == ToolKind.Measure)
                return controllers ? "Measure: trigger one corner, then the other" : "Measure: pinch one corner, then the other";
            return controllers ? "Move: aim at the floor, pull the trigger" : "Move: pinch the floor to walk there";
        }
    }
}
