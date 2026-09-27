using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Scene
{
    /// The "You are here" pin on the tabletop model (presence.md S1): a 3 cm glass stem standing on the model at the
    /// point that will be under your feet once you step in (live: walk round the table and it follows), topped by a
    /// glass chip. Poke or pinch it to step in (AppCommands.StepIn → the grow-in transition). Shown only while the
    /// model rests on the table; faces you (Y-billboard); hidden while it would sit on a model wheel card (Covered: the
    /// facade's spawn, 4 m out front, lands on the lens). Built by MainSceneBuilder with UiBuild.
    public class SpawnMarker : MonoBehaviour
    {
        public TabletopController tabletop;
        public ModeController mode;
        public Transform head;
        public GlassButton button;
        [Tooltip("The visual (stem, foot dot, chip); hidden when the model isn't on the table.")]
        public GameObject content;
        [Tooltip("Stem height above the model's ground (m).")]
        public float height = 0.03f;

        [Tooltip("The chip's half size (m): the pin hides while, from your eye, the chip would sit on a model wheel card.")]
        public Vector2 chipHalf = new Vector2(0.04f, 0.013f);

        public bool Shown { get; private set; }
        /// Shown, but hidden behind the switcher wheel: from your eye it would sit on a card (the wheel's Walk in steps in too).
        public bool Covered { get; private set; }
        public Vector3 FootWorld { get; private set; }
        public int Presses { get; private set; }

        void OnEnable()
        {
            Services.Register(this);
            if (button != null) button.Clicked += Press;
        }

        void OnDisable()
        {
            Services.Unregister(this);
            if (button != null) button.Clicked -= Press;
        }

        public void Press()
        {
            if (!Shown) return;
            Presses++;
            Log.Info($"You are here pin pressed (#{Presses})");
            AppCommands.StepIn();
        }

        void LateUpdate() => Refresh();

        public void Refresh()
        {
            bool show = tabletop != null && tabletop.root != null && tabletop.OnTable && AppState.Mode == AppMode.Tabletop
                        && (mode == null || (mode.VisualMode == AppMode.Tabletop && !mode.IsTransitioning));
            Shown = show;
            if (!show)
            {
                Covered = false;
                if (content != null && content.activeSelf) content.SetActive(false);
                return;
            }
            var root = tabletop.root.transform;
            FootWorld = root.TransformPoint(tabletop.StepInAnchorLocal());
            transform.position = FootWorld;
            var look = head != null ? Vector3.ProjectOnPlane(transform.position - head.position, Vector3.up) : Vector3.zero;
            if (look.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
            var chip = FootWorld + Vector3.up * (height + chipHalf.y);
            Covered = head != null && Services.TryGet<ModelWheel>(out var wheel) && wheel.Covers(head.position, chip, chipHalf);
            if (content != null && content.activeSelf == Covered) content.SetActive(!Covered);
        }
    }
}
