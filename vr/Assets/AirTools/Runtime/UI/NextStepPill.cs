using AirTools.Core;
using UnityEngine;

namespace AirTools.UI
{
    /// UX W1.3 Next-step pill: one primary GlassButton ("Find hinges for this door") and up to two secondary chips under
    /// it, from NextStep's Step. A tap runs the action through NextStepActions (→ AppCommands only; nothing here can
    /// pay). UI is poke-only until D5, so the pill sits within reach — 0.45 m out, 24° down, 22° right of the window
    /// slot — rather than beside the 0.9 m status line; world-locked once placed, re-centred when you turn well away.
    /// Hidden while the Step hides it (Enter world / Pay are physical controls) or has no primary, and (declutter M3)
    /// while the main slot or Settings is open: the window's own primary is the next step then (one primary per view).
    public class NextStepPill : MonoBehaviour
    {
        public Transform head;
        public GameObject content;
        public GlassButton primary;
        public GlassButton secondary0, secondary1;
        public float distance = 0.45f;
        public float downDeg = 24f;
        public float yawDeg = 22f;
        public float recentreDeg = 40f;
        [Tooltip("Gap between the secondary chips (m).")]
        public float gap = 0.006f;

        /// The pill is on screen (the Step wants it and it isn't yielding to a window).
        public bool Visible { get; private set; }
        /// The Step has a pill to show (NextStep's PillVisible), whether or not it's yielding.
        public bool Wanted { get; private set; }
        /// Declutter M3: hidden because the main slot or Settings is open.
        public bool Yielding { get; private set; }
        public int Taps { get; private set; }
        public StepAction Primary => m_Actions[0];

        readonly StepAction[] m_Actions = new StepAction[3];
        Vector3 m_Home;
        bool m_HomeSet;
        AirTools.Structure.ScenePanel m_Settings;

        /// Declutter M3: the pill yields while a main-slot window (`mainSlot` = WindowSlot.Current) or Settings (the Scene
        /// window, the right side slot) is open. Pure.
        public static bool Yields(object mainSlot, bool moreOpen) => mainSlot != null || moreOpen;

        bool SettingsOpen()
        {
            if (m_Settings == null) Services.TryGet(out m_Settings);
            return m_Settings != null && m_Settings.IsOpen;
        }

        void OnEnable()
        {
            if (primary != null) primary.Clicked += OnPrimary;
            if (secondary0 != null) secondary0.Clicked += OnSecondary0;
            if (secondary1 != null) secondary1.Clicked += OnSecondary1;
        }

        void OnDisable()
        {
            if (primary != null) primary.Clicked -= OnPrimary;
            if (secondary0 != null) secondary0.Clicked -= OnSecondary0;
            if (secondary1 != null) secondary1.Clicked -= OnSecondary1;
        }

        void OnPrimary() => Press(0);
        void OnSecondary0() => Press(1);
        void OnSecondary1() => Press(2);

        /// Run the primary (0) or a secondary (1, 2) — the same path as a tap.
        public bool Press(int which)
        {
            if (which < 0 || which > 2 || m_Actions[which].IsNone) return false;
            Taps++;
            return NextStepActions.Run(m_Actions[which]);
        }

        public void Show(in Step step)
        {
            if (step.Frozen) return;
            m_Actions[0] = step.Primary;
            m_Actions[1] = step.Secondary0;
            m_Actions[2] = step.Secondary1;
            Wanted = step.PillVisible;
            Yielding = Yields(WindowSlot.Current, SettingsOpen());
            Apply();
        }

        /// Show the Step's actions when it wants the pill and no window has the next step; else hide it.
        void Apply()
        {
            bool show = Wanted && !Yielding;
            if (show && !Visible) m_HomeSet = false;   // appear where you're looking
            Visible = show;
            if (content != null && content.activeSelf != show) content.SetActive(show);
            if (!show) return;
            Set(primary, m_Actions[0]);
            Set(secondary0, m_Actions[1]);
            Set(secondary1, m_Actions[2]);
            LayoutChips();
        }

        public void Hide()
        {
            Visible = false;
            Wanted = false;
            for (int i = 0; i < m_Actions.Length; i++) m_Actions[i] = default;
            if (content != null) content.SetActive(false);
        }

        /// The coach points at the pill (C07 / C11): the primary shows as selected while it does.
        public void Highlight(bool on)
        {
            if (primary != null) primary.SetSelected(on);
        }

        static void Set(GlassButton b, in StepAction a)
        {
            if (b == null) return;
            bool on = !a.IsNone;
            if (b.gameObject.activeSelf != on) b.gameObject.SetActive(on);
            if (on && b.Text != a.Label) b.SetText(a.Label);
        }

        /// One chip: centred under the primary; two: side by side.
        void LayoutChips()
        {
            if (secondary0 == null || secondary1 == null || secondary0.surface == null) return;
            bool two = secondary0.gameObject.activeSelf && secondary1.gameObject.activeSelf;
            float w = secondary0.surface.size.x;
            var p0 = secondary0.transform.localPosition;
            var p1 = secondary1.transform.localPosition;
            secondary0.transform.localPosition = new Vector3(two ? -(w + gap) * 0.5f : 0f, p0.y, p0.z);
            secondary1.transform.localPosition = new Vector3(two ? (w + gap) * 0.5f : 0f, p1.y, p1.z);
        }

        /// Declutter M3: yield while a window has the next step; come back (with the Step's actions) when it closes.
        /// Every frame (LateUpdate); tests call it.
        public void RefreshYield()
        {
            bool yielding = Wanted && Yields(WindowSlot.Current, SettingsOpen());
            if (yielding != Yielding) { Yielding = yielding; Apply(); }
        }

        void LateUpdate()
        {
            RefreshYield();
            if (!Visible || head == null) return;
            var rig = head.parent != null ? head.parent.root : null;
            if (rig != null && head.position.y - rig.position.y < 0.5f) return;   // not tracked yet: keep the built pose
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            var slotFwd = Quaternion.AngleAxis(yawDeg, Vector3.up) * fwd;
            var home = HeadAnchor.PoseFor(head.position, head.forward, distance, downDeg, yawDeg).position;
            var toHome = Vector3.ProjectOnPlane(m_Home - head.position, Vector3.up);
            bool pressing = (primary != null && primary.State != Oculus.Interaction.InteractableState.Normal)
                            || (secondary0 != null && secondary0.State != Oculus.Interaction.InteractableState.Normal)
                            || (secondary1 != null && secondary1.State != Oculus.Interaction.InteractableState.Normal);
            if (!m_HomeSet || (!pressing && (Vector3.Angle(toHome, slotFwd) > recentreDeg || toHome.magnitude > distance * 1.8f)))
            {
                bool first = !m_HomeSet;
                m_Home = home;
                m_HomeSet = true;
                if (first) transform.position = home;
            }
            transform.position = Vector3.Lerp(transform.position, m_Home, 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime));
            var look = transform.position - head.position;   // faces the eyes: pitch + yaw
            if (look.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
        }
    }
}
