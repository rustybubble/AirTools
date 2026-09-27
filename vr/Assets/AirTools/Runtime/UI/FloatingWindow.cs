using AirTools.Core;
using UnityEngine;

namespace AirTools.UI
{
    /// A window that opens in front of you in the lower field of view and stays put (world-locked) until you turn
    /// well away, when it re-centres; once moved with its grab bar it stays where it was put until closed. Opens with
    /// a short fade/scale settle (motion tokens; instant with reduced motion). UX W0.7: placed from the person —
    /// `distance` along a line `downDeg` below the eye line (`yawDeg` to the side for side slots), facing the eyes
    /// (pitch + yaw); main-slot windows close each other (WindowSlot).
    public class FloatingWindow : MonoBehaviour, IDockTarget
    {
        public Transform head;
        public GameObject content;
        public WindowHandle handle;
        public float distance = 0.45f;
        [Tooltip("Degrees below the eye line (UX W0.7: windows 20° down).")]
        public float downDeg = 20f;
        [Tooltip("Degrees to the side (side slots, e.g. the Scene window).")]
        public float yawDeg;
        public float recentreDeg = 40f;
        [Tooltip("Shares the one main window slot (Notebook / Sellers / Checkout): opening it closes the other.")]
        public bool mainSlot = true;

        public bool IsOpen { get; private set; }
        GlassSurface m_Panel;
        bool m_PanelSearched;
        Vector3 m_Home;
        bool m_HomeSet, m_Placed;
        float m_Scale;

        void OnEnable() => AppState.Changed += OnMode;
        void OnDisable() => AppState.Changed -= OnMode;

        /// Leaving the world closes it (sellers and checkout belong to the world session).
        void OnMode(AppMode from, AppMode to)
        {
            if (to == AppMode.Passthrough && IsOpen) Close();
        }

        public void Open()
        {
            if (!IsOpen) { m_HomeSet = false; m_Placed = false; if (handle != null) handle.WasMoved = false; }
            IsOpen = true;
            if (mainSlot) WindowSlot.Claim(this, Close);
            if (content != null) content.SetActive(true);
        }

        public void Close()
        {
            IsOpen = false;
            WindowSlot.Release(this);
        }

        /// The window's one glass panel ("Content/Panel"), found once.
        public GlassSurface Panel
        {
            get
            {
                if (m_Panel == null && !m_PanelSearched) { m_Panel = FindPanel(content != null ? content.transform : transform); m_PanelSearched = true; }
                return m_Panel;
            }
        }

        /// Declutter M3: where the status line docks — the centre of the panel's top edge, live (cards resize).
        public bool TryPanelTop(out Vector3 top, out Quaternion rotation) => PanelTop(IsOpen, Panel, out top, out rotation);

        /// The first GlassSurface named "Panel" under `root` (allocates: call once).
        public static GlassSurface FindPanel(Transform root)
        {
            if (root == null) return null;
            foreach (var s in root.GetComponentsInChildren<GlassSurface>(true))
                if (s.name == "Panel") return s;
            return null;
        }

        /// The centre of `panel`'s top edge and its rotation, while `open`.
        public static bool PanelTop(bool open, GlassSurface panel, out Vector3 top, out Quaternion rotation)
        {
            if (!open || panel == null) { top = default; rotation = Quaternion.identity; return false; }
            var t = panel.transform;
            top = t.TransformPoint(new Vector3(0f, panel.size.y * 0.5f, 0f));
            rotation = t.rotation;
            return true;
        }

        void LateUpdate()
        {
            float target = IsOpen ? 1f : 0f;
            float dur = UiSettings.Duration(UiTheme.Current.motion.standard);
            m_Scale = dur <= 0f ? target : Mathf.MoveTowards(m_Scale, target, Time.unscaledDeltaTime / dur);
            if (content != null)
            {
                content.transform.localScale = Vector3.one * Mathf.Lerp(0.96f, 1f, m_Scale);
                bool show = m_Scale > 0.01f;
                if (content.activeSelf != show) content.SetActive(show);
            }
            if (!IsOpen || head == null || (handle != null && (handle.Dragging || handle.WasMoved))) return;
            var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            var slotFwd = Quaternion.AngleAxis(yawDeg, Vector3.up) * fwd;
            var home = HeadAnchor.PoseFor(head.position, head.forward, distance, downDeg, yawDeg).position;
            var toHome = Vector3.ProjectOnPlane(m_Home - head.position, Vector3.up);
            if (!m_HomeSet || Vector3.Angle(toHome, slotFwd) > recentreDeg || toHome.magnitude > distance * 1.8f) { m_Home = home; m_HomeSet = true; }
            var pos = m_Placed ? Vector3.Lerp(transform.position, m_Home, 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime)) : m_Home;
            m_Placed = true;
            transform.position = pos;
            var look = pos - head.position;   // faces the eyes: pitch + yaw
            if (look.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
        }
    }
}
