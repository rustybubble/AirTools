using System.Collections.Generic;
using AirTools.Core;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Parts
{
    /// edit6dof: a placed part's context menu — Edit / View similar / Delete — a small glass card, facing you (built with
    /// UiBuild by EditViewBuilder). edit-touch: poke only, and within reach: 0.42 m from the eye, 18° down, on the line to
    /// the part turned 14° to your right (the part stays in view; EditViewMath.MenuPose), placed once when it opens and
    /// then still (a poke target doesn't move under the finger). Opened by the gesture on the part (EditView: the controller's grip
    /// squeeze, or a long pinch / trigger hold with the Part tool, Move or nothing in hand). After Delete it turns into
    /// "Deleted · Undo" at the same spot for a few seconds. It closes on a choice, on the next press elsewhere, after
    /// `autoClose` seconds, on a tool change and on leaving the world. One world chip: it claims one slot of the label pool
    /// (Focus class) while it shows.
    public class PartContextMenu : MonoBehaviour, IWorldLabelSource
    {
        public Transform head;
        [Tooltip("The card (its glass panel and buttons); shown and hidden.")]
        public GameObject card;
        public GlassButton edit, similar, delete;
        [Tooltip("\"Deleted\" + Undo, shown after a delete.")]
        public GameObject undoRow;
        public GlassButton undo;
        public TextMeshPro title;
        public float autoClose = 8f;
        public float undoSeconds = 6f;
        [Tooltip("edit-touch: the card this far from the eye (m): within reach.")]
        public float distance = 0.42f;
        [Tooltip("… this far below the eye line (degrees) …")]
        public float downDeg = 18f;
        [Tooltip("… and this far to the right of the line to the part (degrees), so the part stays in view.")]
        public float sideDeg = 14f;
        [Tooltip("edit-touch: presses are ignored this long after it opens (s): it may open where the pinching hand is.")]
        public float armDelay = 0.35f;

        public PartInstance Part { get; private set; }
        public bool Showing => card != null && card.activeSelf;
        public bool UndoShowing => Showing && undoRow != null && undoRow.activeSelf;
        public int Opens { get; private set; }
        /// edit-touch: its buttons act (not in the first `armDelay` after it opens: it may open where the pinching hand is).
        public bool AcceptingPresses => Showing && Time.unscaledTime - m_ShownAt >= armDelay;

        float m_Until, m_ShownAt = -999f;
        Vector3 m_UndoAt;
        bool m_Claimed;

        void OnEnable() => Services.Register(this);
        void OnDisable() { Services.Unregister(this); Release(); }

        /// Open beside `part`: Edit / View similar / Delete.
        public void Show(PartInstance part)
        {
            if (part == null || card == null) return;
            Part = part;
            if (title != null) title.text = Copy.Clip(Copy.Clean(part.DisplayName), 26);
            SetRows(undoMode: false);
            card.SetActive(true);
            m_Until = Time.unscaledTime + autoClose;
            m_ShownAt = Time.unscaledTime;
            Opens++;
            Place();
            Claim();
        }

        /// After a delete: "Deleted · Undo" where the card was (or, opened by voice, within reach toward `at`).
        public void ShowUndo(PartInstance part, Vector3 at)
        {
            if (card == null) return;
            bool wasShowing = Showing;
            Part = part;
            m_UndoAt = at;
            SetRows(undoMode: true);
            card.SetActive(true);
            m_Until = Time.unscaledTime + undoSeconds;
            if (!wasShowing) Place();
            Claim();
        }

        /// Harness and tests: accept presses now (skip the arm delay).
        public void ArmNow() => m_ShownAt = -999f;

        public void Hide()
        {
            if (card != null && card.activeSelf) card.SetActive(false);
            Part = null;
            Release();
        }

        void SetRows(bool undoMode)
        {
            foreach (var b in new[] { edit, similar, delete }) if (b != null && b.gameObject.activeSelf == undoMode) b.gameObject.SetActive(!undoMode);
            if (undoRow != null && undoRow.activeSelf != undoMode) undoRow.SetActive(undoMode);
        }

        void LateUpdate()
        {
            if (!Showing) return;
            if (Time.unscaledTime > m_Until || AppState.Mode != AppMode.World) { Hide(); return; }
            if (!UndoShowing && (Part == null || !Part.Placed || !Part.gameObject.activeInHierarchy)) { Hide(); return; }
            if (UndoShowing && (Part == null || Part.Placed)) Hide();   // delete-undo: back by the ring's Undo (or gone for good): the chip goes
        }

        /// edit-touch: within reach toward the part (or where it was, for Undo), facing the eye, at its built size.
        void Place()
        {
            var eyeT = head != null ? head : Camera.main != null ? Camera.main.transform : null;
            var eye = eyeT != null ? eyeT.position : Vector3.zero;
            var target = UndoShowing || Part == null ? m_UndoAt : Part.WorldBoxCentre;
            var pose = EditViewMath.MenuPose(eye, eyeT != null ? eyeT.forward : Vector3.forward, target, distance, downDeg, sideDeg);
            transform.SetPositionAndRotation(pose.position, pose.rotation);
            transform.localScale = Vector3.one;
        }

        // ---------------- the label pool ----------------

        void Claim() { m_Claimed = true; WorldLabels.Changed(this); }

        void Release()
        {
            if (!m_Claimed) return;
            m_Claimed = false;
            WorldLabels.Remove(this);
        }

        public void ClaimLabels(List<LabelClaim> claims)
        {
            if (m_Claimed && Showing) claims.Add(new LabelClaim(LabelClass.Focus, 1, 0, Time.unscaledTime));
        }

        public void ApplyLabels(LabelGrant[] grants, int first) { }
    }
}
