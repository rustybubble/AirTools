using Oculus.Interaction;
using UnityEngine;

namespace AirTools.UI
{
    /// A quiet grab bar under a movable window: point at it and pinch (or pull the trigger), then move — the window
    /// follows the ray at the distance it was grabbed and keeps facing you (Y only). Brightens on hover.
    public class WindowHandle : MonoBehaviour
    {
        public Transform window;
        public Transform head;
        public RayInteractable ray;
        public GlassSurface bar;

        public bool Dragging { get; private set; }
        public bool WasMoved { get; set; }

        RayInteractor m_Grabber;
        float m_Distance;
        Vector3 m_Offset;

        void Update()
        {
            if (ray == null || window == null) return;
            RayInteractor current = null;
            foreach (var r in ray.SelectingInteractors) { current = r; break; }
            if (current != null && m_Grabber != current)
            {
                m_Grabber = current;
                var rr = current.Ray;
                m_Distance = Vector3.Dot(window.position - rr.origin, rr.direction);
                m_Offset = window.position - (rr.origin + rr.direction * m_Distance);
            }
            else if (current == null) m_Grabber = null;
            Dragging = m_Grabber != null;
            if (Dragging)
            {
                var rr = m_Grabber.Ray;
                window.position = rr.origin + rr.direction * Mathf.Clamp(m_Distance, 0.3f, 2.5f) + m_Offset;
                if (head != null)
                {
                    var look = Vector3.ProjectOnPlane(window.position - head.position, Vector3.up);
                    if (look.sqrMagnitude > 1e-6f) window.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
                }
                WasMoved = true;
            }
            if (bar != null)
            {
                var c = UiTheme.Current.colors;
                bool hover = ray.State != InteractableState.Normal;
                bar.SetTint(Dragging ? c.ink : hover ? c.textSecondary : c.handle);   // D3: dragging is active (ink)
            }
        }
    }
}
