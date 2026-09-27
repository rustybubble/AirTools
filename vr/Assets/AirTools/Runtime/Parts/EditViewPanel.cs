using System.Text;
using AirTools.Core;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Parts
{
    /// edit6dof: the Edit view's controls (built with UiBuild by EditViewBuilder; behaviour on GlassButton.Clicked through
    /// EditViewButton).
    /// - **The panel** (edit-touch: poke only, within reach — tucked into the arrows' lower right, 0.45 m from the eye,
    ///   facing it; EditView.PlaceStage): the part's name and true size ("24 × 34 × 24″ · shown at 45 %"), the readout
    ///   ("Right 0″ · Up 0″ · Out ¼″ / Turn 5° · Tilt 0° · Roll 0°"), the swatches (Original + eight colours) and Shade
    ///   (light / full), Step (⅜″ · 5° / fine ⅛″ · 1°), Reset turn, Fit to opening (only when there's a taped opening it
    ///   can be made for), then Cancel · Move · Save for a placed part and Cancel · Place for a new one (one primary each).
    /// - **The move bar** (a main-slot card while the item is out in the room, poke only): the fit where it is ("Fits the
    ///   gap · ⅜″ spare") or "Aim and pinch to place", with Cancel and Save (Save hidden while placing: the pinch places it).
    /// Text is rebuilt only when something shown changes: no allocation per frame.
    public class EditViewPanel : MonoBehaviour
    {
        [Header("Panel (on the stage)")]
        public GameObject root;
        [Tooltip("edit-touch: the panel's size as built (m): EditViewMath.Layout tucks it into the arrows' lower right.")]
        public Vector2 panelSize = new Vector2(0.225f, 0.228f);
        public TextMeshPro title;
        public TextMeshPro size;
        public TextMeshPro readout;
        [Tooltip("Original first, then EditShades.Names[1..].")]
        public GlassButton[] swatches = new GlassButton[0];
        public GlassButton step, shade, reset, fitToOpening;
        public GlassButton cancel, move, save, place;

        [Header("Move bar (main slot)")]
        public FloatingWindow moveWindow;
        public TextMeshPro moveText;
        public GlassButton moveSave, moveCancel;

        public bool Showing => root != null && root.activeSelf;
        public bool MoveBarOpen => moveWindow != null && moveWindow.IsOpen;
        /// The readout as last drawn, without markup (the harness).
        public string LastReadout { get; private set; } = "";
        public string LastMoveText { get; private set; } = "";

        readonly StringBuilder m_Sb = new StringBuilder(128);
        int m_Qx = int.MinValue, m_Qy, m_Qz, m_T, m_Ti, m_R;
        UnitSystem m_Unit;
        bool m_Fine, m_Full, m_Force = true;
        int m_Swatch = -2;
        string m_Move;

        public void Show(bool on)
        {
            if (root != null && root.activeSelf != on) root.SetActive(on);
            if (on) m_Force = true;
        }

        /// The kind's buttons (a placed part: Move and Save; a new one: Place) and the title / size line.
        public void SetUp(PartInstance part, EditKind kind, float shownScale, bool canFit)
        {
            m_Force = true;
            m_Qx = int.MinValue;
            m_Swatch = -2;
            if (title != null)
            {
                string sec = ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.textSecondary);
                string name = part != null ? Copy.Clip(Copy.Clean(part.DisplayName), 30) : "";
                title.text = $"{(kind == EditKind.New ? "Place" : "Edit")} <size=70%><color=#{sec}>{name}</color></size>";
            }
            if (size != null && part != null)
            {
                string pct = shownScale < 0.995f ? $" · shown at {Mathf.RoundToInt(shownScale * 100f)} %" : "";
                size.text = UiText.Tabular(Copy.Dims(part.Spec.dims_mm)) + pct;
            }
            Active(move, kind == EditKind.Placed);
            Active(save, kind == EditKind.Placed);
            Active(place, kind == EditKind.New);
            Active(fitToOpening, kind == EditKind.Placed && canFit);
        }

        static void Active(GlassButton b, bool on) { if (b != null && b.gameObject.activeSelf != on) b.gameObject.SetActive(on); }

        /// Called every frame while the panel shows. Cheap when nothing changed.
        public void Refresh(Vector3 offset, Vector3 turnTiltRoll, bool moves, bool fine, bool full, int swatch)
        {
            var u = UiSettings.UnitSystem;
            int qx = PlacementMath.Quantum(offset.x, u), qy = PlacementMath.Quantum(offset.y, u), qz = PlacementMath.Quantum(offset.z, u);
            int t = Mathf.RoundToInt(turnTiltRoll.x), ti = Mathf.RoundToInt(turnTiltRoll.y), r = Mathf.RoundToInt(turnTiltRoll.z);
            if (m_Force || qx != m_Qx || qy != m_Qy || qz != m_Qz || t != m_T || ti != m_Ti || r != m_R || u != m_Unit)
            {
                m_Qx = qx; m_Qy = qy; m_Qz = qz; m_T = t; m_Ti = ti; m_R = r; m_Unit = u;
                m_Sb.Clear();
                if (moves) { PlacementMath.AppendOffset(m_Sb, offset, u); m_Sb.Append('\n'); }
                PlacementMath.AppendTurn(m_Sb, turnTiltRoll);
                if (readout != null) readout.SetText(m_Sb);
                LastReadout = null;
            }
            if (m_Force || fine != m_Fine || full != m_Full)
            {
                m_Fine = fine; m_Full = full;
                if (step != null) { step.SetText(PlacementMath.StepLabel(u, fine)); step.SetSelected(fine); }
                if (shade != null) shade.SetText(full ? "Shade: full" : "Shade: light");
            }
            if (m_Force || swatch != m_Swatch)
            {
                m_Swatch = swatch;
                for (int i = 0; i < swatches.Length; i++) if (swatches[i] != null) swatches[i].SetSelected(i == swatch);
            }
            m_Force = false;
        }

        /// The readout without markup (allocates: harness and logs only).
        public string Readout()
        {
            if (LastReadout == null) LastReadout = PlacementMath.Plain(m_Sb);
            return LastReadout;
        }

        // ---------------- the move bar ----------------

        /// Open the bar: `placing` a new part ("Aim and pinch to place", no Save) or moving a placed one.
        public void OpenMoveBar(bool placing)
        {
            if (moveWindow == null) return;
            Active(moveSave, !placing);
            m_Move = null;
            SetMoveText(placing ? (AirTools.Input.InputMode.Controllers ? "Aim and pull the trigger to place it" : "Aim and pinch to place it")
                                : (AirTools.Input.InputMode.Controllers ? "Pull the trigger and drag it to a spot · then Save" : "Pinch and drag it to a spot · then Save"));
            if (!moveWindow.IsOpen) moveWindow.Open();
        }

        public void CloseMoveBar() { if (moveWindow != null && moveWindow.IsOpen) moveWindow.Close(); }

        /// The bar's line (only when it changes).
        public void SetMoveText(string line)
        {
            if (line == m_Move) return;
            m_Move = line;
            LastMoveText = line ?? "";
            if (moveText != null) moveText.text = line ?? "";
        }
    }
}
