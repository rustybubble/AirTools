using System.Text;
using AirTools.Core;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Parts
{
    /// The adjust panel (a main-slot card, declutter §3.3: a decision with buttons): the part's name, a live readout —
    /// the anchor's offset from the gap or the auto-placement ("Right 1⅜″ · Up 0″ · Out ¼″"), the turn / tilt / roll and
    /// the fit in its colour and words — then move and turn pads, the step (⅜″ · 5°, Fine ⅛″ · 1°), Snap, Reset to fit,
    /// the model arrows, Size & finish (assetgen: the size line "58½ × 46¾ × 3¼″ · made to size", W / H / D − + pads,
    /// Fit to opening, three finish chips), Save placement and the A–D slot chips. Done in the title row closes it (and
    /// adjust mode).
    /// The readout is rebuilt only when a shown number changes (quantised to the unit), into a reused StringBuilder: no
    /// allocation per frame.
    public class PlacementPanel : MonoBehaviour
    {
        public FloatingWindow window;
        public TextMeshPro title;
        public TextMeshPro readout;
        public TextMeshPro fit;
        public TextMeshPro model;
        public GlassButton done;
        public GlassButton fine;
        public GlassButton snap;
        public GlassButton prevModel, nextModel;
        public GlassButton save;
        public GlassButton[] slots = new GlassButton[0];
        [Header("assetgen: Size & finish")]
        [Tooltip("\"Size & finish · 58½ × 46¾ × 3¼″ · made to size\"")]
        public TextMeshPro size;
        [Tooltip("W −, W +, H −, H +, D −, D + (PlacementAction.SizeStep 0–5).")]
        public GlassButton[] sizeSteps = new GlassButton[0];
        public GlassButton fitToOpening;
        [Tooltip("Finish chips 0–2 (the part's finishes, then common frame colours).")]
        public GlassButton[] finishes = new GlassButton[0];

        public bool IsOpen => window != null && window.IsOpen;
        /// The readout as last drawn, without markup (the harness reads it).
        public string LastReadout { get; private set; } = "";

        readonly StringBuilder m_Sb = new StringBuilder(256);
        int m_Qx = int.MinValue, m_Qy, m_Qz, m_Turn, m_Tilt, m_Roll;
        UnitSystem m_Unit;
        FitReport m_Fit;
        PartInstance m_Part;
        bool m_Fine, m_Snap, m_Force = true;
        int m_SlotsVersion = -1;
        int m_LookVersion = -1;   // assetgen
        PartInstance m_LookPart;
        bool m_LookBusy;
        /// assetgen: the size line as last drawn (the harness reads it).
        public string LastSize { get; private set; } = "";

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);

        public void Open()
        {
            m_Qx = int.MinValue;   // redraw everything
            m_Part = null;
            m_Fit = null;
            m_SlotsVersion = -1;
            m_LookVersion = -1;   // assetgen
            m_Force = true;
            window?.Open();
        }

        public void Close() => window?.Close();

        /// Called every frame by the editor while adjusting. Cheap when nothing changed.
        public void Show(PlacementEditor e, PartInstance part, Vector3 offset, Vector3 turnTiltRoll)
        {
            if (!IsOpen || part == null) return;
            var u = UiSettings.UnitSystem;
            int qx = PlacementMath.Quantum(offset.x, u), qy = PlacementMath.Quantum(offset.y, u), qz = PlacementMath.Quantum(offset.z, u);
            int t = Mathf.RoundToInt(turnTiltRoll.x), ti = Mathf.RoundToInt(turnTiltRoll.y), r = Mathf.RoundToInt(turnTiltRoll.z);
            if (qx != m_Qx || qy != m_Qy || qz != m_Qz || t != m_Turn || ti != m_Tilt || r != m_Roll || u != m_Unit)
            {
                m_Qx = qx; m_Qy = qy; m_Qz = qz; m_Turn = t; m_Tilt = ti; m_Roll = r; m_Unit = u;
                m_Sb.Clear();
                PlacementMath.AppendReadout(m_Sb, offset, turnTiltRoll, u);
                if (readout != null) readout.SetText(m_Sb);
                LastReadout = null;   // made on demand (Readout)
            }
            if (part != m_Part)
            {
                m_Part = part;
                string sec = ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.textSecondary);
                if (title != null) title.text = $"Adjust <size=70%><color=#{sec}>{Copy.Clip(Copy.Clean(part.DisplayName), 30)}</color></size>";
            }
            if (!ReferenceEquals(part.Fit, m_Fit))
            {
                m_Fit = part.Fit;
                if (fit != null)
                {
                    string hex = ColorUtility.ToHtmlStringRGB(m_Fit != null ? PartOutline.ColorFor(m_Fit.Status) : UiTheme.Current.colors.textSecondary);
                    fit.text = m_Fit != null ? $"<color=#{hex}>{Copy.FitLine(m_Fit)}</color>" : "";
                }
            }
            if (m_Force || e.Fine != m_Fine || e.Snap != m_Snap)
            {
                m_Force = false;
                m_Fine = e.Fine; m_Snap = e.Snap;
                if (fine != null) { fine.SetText(PlacementMath.StepLabel(u, m_Fine)); fine.SetSelected(m_Fine); }
                if (snap != null) snap.SetSelected(m_Snap);
            }
            if (e.SlotsVersion != m_SlotsVersion)
            {
                m_SlotsVersion = e.SlotsVersion;
                RefreshSlots(e);
            }
            if (e.LookVersion != m_LookVersion || part != m_LookPart || e.LookBusy != m_LookBusy) RefreshLook(e, part);   // assetgen
        }

        /// assetgen: the Size & finish section — the size line, the chips' names and which is on, Fit to opening. Only
        /// when the look changed (LookVersion): no allocation per frame.
        void RefreshLook(PlacementEditor e, PartInstance part)
        {
            m_LookVersion = e.LookVersion;
            m_LookPart = part;
            m_LookBusy = e.LookBusy;
            string sec = ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.textSecondary);
            LastSize = e.SizeLine(part);
            if (size != null) size.text = $"<color=#{sec}>Size & finish</color> · {UiText.Tabular(LastSize)}";
            var choices = e.FinishChoices(part);
            for (int i = 0; i < finishes.Length; i++)
            {
                var b = finishes[i];
                if (b == null) continue;
                bool on = i < choices.Count;
                if (b.gameObject.activeSelf != on) b.gameObject.SetActive(on);
                if (!on) continue;
                b.SetText(choices[i]);
                b.SetSelected(PlacementEditor.FinishSelected(part, choices[i]));
            }
            if (fitToOpening != null) fitToOpening.SetInteractable(e.CanFitToOpening);
        }

        void RefreshSlots(PlacementEditor e)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) continue;
                string name = i < PlacementMath.Slots.Length ? PlacementMath.Slots[i] : null;
                var saved = name != null ? e.Saved(name) : null;
                slots[i].SetInteractable(saved != null);
                slots[i].SetSelected(saved != null && e.ActiveSlot == name);
            }
            if (model != null) model.text = e.ModelLine();
            if (prevModel != null) prevModel.SetInteractable(e.CanCycleModels);
            if (nextModel != null) nextModel.SetInteractable(e.CanCycleModels);
        }

        /// The readout without markup (allocates: harness and logs only).
        public string Readout()
        {
            if (LastReadout == null) LastReadout = PlacementMath.Plain(m_Sb);
            return LastReadout;
        }

        /// The unit changed (D2): redraw the numbers and the step chip.
        public void Relabel()
        {
            m_Qx = int.MinValue;
            m_Fit = null;
            m_Force = true;
            m_LookVersion = -1;   // assetgen: the size line in the new unit
        }
    }
}
