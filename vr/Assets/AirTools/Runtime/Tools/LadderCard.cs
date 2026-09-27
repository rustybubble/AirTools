using System.Globalization;
using AirTools.Core;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Tools
{
    /// The ladder card (presence.md S4): the size, foot, angle and ratio of the ladder you just placed (live while you
    /// drag its foot), the verdict in words with a tone dot, the disclaimer "Guide only — follow the ladder's label and
    /// OSHA 1926.1053.", *Find this ladder* (AppCommands.FindLadder → the parts search for "28 ft extension ladder"),
    /// a Fall edges toggle and Close. Main window slot (one window at a time); closes when the ladder tool is put away.
    /// Built by LadderBuilder with UiBuild; behaviour on GlassButton.Clicked (LadderCardButton).
    public class LadderCard : MonoBehaviour
    {
        public FloatingWindow window;
        public TextMeshPro title, verdict, detail, disclaimer;
        public GlassSurface dot;
        public GlassButton findButton, edgesButton;

        public LadderPlacement Shown { get; private set; }
        public bool IsOpen => window != null && window.IsOpen;
        /// The last search "Find this ladder" ran (harness / tests).
        public string LastSearch { get; private set; }

        LadderTool m_Tool;
        ToolManager m_Tools;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        void OnEnable() { Services.Register(this); Hook(); }
        void Start() => Hook();

        void OnDisable()
        {
            Services.Unregister(this);
            if (m_Tool != null) m_Tool.Changed -= OnLadderChanged;
            if (m_Tools != null) m_Tools.Changed -= OnToolChanged;
            m_Tool = null; m_Tools = null;
        }

        void Hook()
        {
            if (m_Tool == null && Services.TryGet<LadderTool>(out var tool)) { m_Tool = tool; tool.Changed += OnLadderChanged; }
            if (m_Tools == null && Services.TryGet<ToolManager>(out var tools)) { m_Tools = tools; tools.Changed += OnToolChanged; }
        }

        void OnLadderChanged(LadderPlacement p)
        {
            if (p == null) { Shown = null; Close(); return; }
            // switchclean: a world switch brings the arriving site's last ladder back (LadderTool.SwitchSite raises Changed):
            // that isn't a ladder you just placed, so the card doesn't open for it (and one still up for another ladder closes).
            if (AirTools.Scene.SiteScope.Switching) { if (p != Shown) { Shown = null; Close(); } return; }
            Show(p);
        }

        void OnToolChanged(ToolKind kind)
        {
            if (kind != ToolKind.Ladder) Close();
        }

        public void Show(LadderPlacement p)
        {
            Shown = p;
            Refresh();
            if (window != null && !window.IsOpen) window.Open();
        }

        public void Close() => window?.Close();

        public void Refresh()
        {
            var p = Shown;
            if (p == null) return;
            var s = p.Solution; var r = p.Report;
            if (title != null) title.text = LadderMath.SizeName(s.SizeFt);
            if (verdict != null) verdict.text = $"{LadderMath.Glyph(r.Verdict)} {LadderMath.VerdictTitle(r.Verdict)} · {Copy.Lengths(r.Reason)}";   // D2
            if (dot != null) dot.SetTint(LadderView.ColorFor(r.Verdict));
            if (detail != null)
            {
                string line1 = $"Foot {Copy.Len(s.FootOut)} out · {s.AngleDeg.ToString("0.0", C)}° · {r.Ratio}";   // D2: the user's unit
                string line2 = s.SizeFt > 0 ? $"Reaches {LadderMath.MaxWorkingFt(s.SizeFt)} ft" : "Taller than any stock ladder";
                if (s.Support == LadderSupport.Landing) line2 = $"Rails {Copy.Len(s.AboveEdge)} above the edge · " + line2.ToLowerInvariant();
                if (!string.IsNullOrEmpty(p.SupportName)) line2 += $" · on {p.SupportName}";
                detail.text = UiText.Tabular(line1 + "\n" + line2);
            }
            if (disclaimer != null) disclaimer.text = LadderMath.Disclaimer;
            if (findButton != null) findButton.SetInteractable(s.SizeFt > 0);
        }

        void Update()
        {
            if (edgesButton != null) edgesButton.SetSelected(ToolboxButton.IsOn(ToolboxAction.ToggleFallEdges) == true);
        }

        public void Press(LadderCardAction action)
        {
            switch (action)
            {
                case LadderCardAction.FindLadder:
                    if (Shown == null || Shown.Solution.SizeFt <= 0) return;
                    LastSearch = LadderMath.SearchQuery(Shown.Solution.SizeFt);
                    AppCommands.FindLadder(Shown.Solution.SizeFt);
                    Log.Info($"Ladder card: find \"{LastSearch}\"");
                    break;
                case LadderCardAction.ToggleFallEdges: ToolboxButton.Run(ToolboxAction.ToggleFallEdges); break;
                case LadderCardAction.Close: Close(); break;
            }
        }
    }
}
