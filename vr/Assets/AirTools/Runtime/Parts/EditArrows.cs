using System.Text;
using AirTools.Core;
using AirTools.UI;
using Oculus.Interaction;
using TMPro;
using UnityEngine;

namespace AirTools.Parts
{
    /// edit6dof: the Edit view's twelve arrows round the item (EditViewMath.Arrows) — Phosphor arrow glyphs on glass
    /// knobs: curved for turn / tilt / roll, straight for right / up / left / down, "Out" / "In" towards you and away —
    /// and the live readout pill ("Tilt 5°") beside the top knob.
    /// edit-touch: every knob is a poke button (GlassButton over the ISDK poke, built by UiBuild.Button: a fingertip, or a
    /// controller's poke tip; no pinch, no ray), all at touch distance facing the eye (Curve). A poke steps once and holding it repeats (EditView, HoldRepeat); the
    /// button draws its own hover and press, the knob in use is selected (ink + bar: D3 active, never colour alone), and
    /// a finger near a knob shows its axis in the pill (GlassButton.HoverChanged). No allocation per frame.
    public class EditArrows : MonoBehaviour
    {
        [Tooltip("One per EditViewMath.Arrows, in that order.")]
        public GlassButton[] knobs = new GlassButton[0];
        public TextMeshPro[] glyphs = new TextMeshPro[0];
        [Tooltip("The readout pill beside the top knob (\"Tilt 5°\").")]
        public GlassSurface readoutPlate;
        public TextMeshPro readoutText;
        [Tooltip("Knob diameter (m) at touch distance (docs/UI.md: targets ≥ 3 cm).")]
        public float knobSize = 0.032f;
        [Tooltip("The readout pill's size (m).")]
        public Vector2 readoutSize = new Vector2(0.11f, 0.034f);

        /// Knob centres (world): valid after Layout / Tick.
        public readonly Vector3[] World = new Vector3[12];
        public readonly bool[] Enabled = new bool[12];
        public int Count => Mathf.Min(knobs.Length, EditViewMath.Arrows.Length);
        /// The knob a finger is near (−1: none) and the one in use (−1: none).
        public int Hover { get; private set; } = -1;
        public int Active { get; private set; } = -1;
        /// The readout as last drawn, without markup (the harness reads it).
        public string LastReadout { get; private set; } = "";

        readonly StringBuilder m_Sb = new StringBuilder(32);
        int m_Quantum = int.MinValue;
        EditAxis m_Axis = EditAxis.None;

        /// Place the knobs on their rings (EditViewMath.Layout: stage-local, the knob plane `Front` in front of the item);
        /// `moves`: the move arrows show.
        public void Layout(in EditLayout l, bool moves)
        {
            for (int i = 0; i < Count; i++)
            {
                var a = EditViewMath.Arrows[i];
                bool on = moves || a.Curved;
                Enabled[i] = on;
                if (knobs[i] == null) continue;
                knobs[i].transform.localPosition = EditViewMath.ArrowPosition(a, l.Inner, l.Outer, l.Front);
                if (knobs[i].gameObject.activeSelf != on) knobs[i].gameObject.SetActive(on);
            }
            if (readoutPlate != null) readoutPlate.transform.localPosition = new Vector3(l.Readout.x, l.Readout.y, -l.Front);
            Hover = -1;
            SetState(-1);
            HideReadout();
            Tick();
        }

        /// edit-touch: each knob (and the pill) moved along its line from the eye to `distance` from it, facing the eye:
        /// the ring as laid out on its plane, seen the same, every knob as near as the next (EditViewMath.FacingEye).
        public void Curve(Vector3 eye, float distance)
        {
            for (int i = 0; i < Count; i++)
            {
                if (knobs[i] == null) continue;
                var t = knobs[i].transform;
                var p = EditViewMath.FacingEye(eye, t.position, distance);
                t.SetPositionAndRotation(p.position, p.rotation);
            }
            if (readoutPlate != null)
            {
                var t = readoutPlate.transform;
                var p = EditViewMath.FacingEye(eye, t.position, distance);
                t.SetPositionAndRotation(p.position, p.rotation);
            }
            Tick();
        }

        /// The knobs' world centres now (the stage may have moved).
        public void Tick()
        {
            for (int i = 0; i < Count; i++) World[i] = knobs[i] != null ? knobs[i].transform.position : Vector3.zero;
        }

        /// Is knob i pressed in now (a finger or a controller tip resting in its poke)?
        public bool Held(int i) => i >= 0 && i < Count && knobs[i] != null && knobs[i].isActiveAndEnabled && knobs[i].State == InteractableState.Select;

        /// The knob in use (−1: none): selected (ink, its bar; the glyph dark on the ink).
        public void SetState(int active)
        {
            if (active == Active) return;
            var c = UiTheme.Current.colors;
            for (int i = 0; i < Count; i++)
            {
                bool on = i == active;
                if (knobs[i] != null) knobs[i].SetSelected(on);
                if (i < glyphs.Length && glyphs[i] != null) glyphs[i].color = on ? c.onInk : c.textPrimary;
            }
            Active = active;
        }

        /// A finger came near knob i (or left it: `on` false).
        public void SetHover(int i, bool on)
        {
            if (on) Hover = i;
            else if (Hover == i) Hover = -1;
        }

        /// "Tilt 5°" beside the top knob (only when the value's quantum changes: no allocation).
        public void ShowReadout(EditAxis axis, float amount, UnitSystem u)
        {
            int q = Edit6DofMath.ReadoutQuantum(axis, amount, u);
            if (readoutPlate != null && !readoutPlate.gameObject.activeSelf) { readoutPlate.gameObject.SetActive(true); m_Quantum = int.MinValue; }
            if (axis == m_Axis && q == m_Quantum) return;
            m_Axis = axis;
            m_Quantum = q;
            m_Sb.Clear();
            Edit6DofMath.AppendAxis(m_Sb, axis, amount, u);
            if (readoutText != null) readoutText.SetText(m_Sb);
            LastReadout = null;
        }

        public void HideReadout()
        {
            if (readoutPlate != null && readoutPlate.gameObject.activeSelf) readoutPlate.gameObject.SetActive(false);
            m_Axis = EditAxis.None;
        }

        /// The readout without markup (allocates: harness and logs only).
        public string Readout()
        {
            if (LastReadout == null) LastReadout = PlacementMath.Plain(m_Sb);
            return LastReadout;
        }

        /// Knob i's button by arrow name (the harness pokes it), or null.
        public GlassButton Knob(string name)
        {
            int i = EditView.ArrowIndex(name);
            return i >= 0 && i < Count ? knobs[i] : null;
        }
    }
}
