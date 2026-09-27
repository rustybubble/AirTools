using AirTools.Core;
using UnityEngine;

namespace AirTools.UI
{
    /// Places a control from the person, not the floor (UX W0.7): `distance` ahead along a line `downDeg` below the eye
    /// line (and `yawDeg` to the side), facing the eyes (pitch + yaw). Placed on the first tracked frame, on recentre,
    /// on coming back to passthrough, and when it has been out of view for a while in passthrough; otherwise it stays
    /// put (world-locked under the rig), so it never swims. Used by the Enter world control.
    public class HeadAnchor : MonoBehaviour
    {
        public Transform head;
        public float distance = 0.45f;
        public float downDeg = 25f;
        public float yawDeg;
        [Tooltip("Re-place after being this far off-gaze (degrees) for outOfViewSeconds, in passthrough.")]
        public float outOfViewDeg = 60f;
        public float outOfViewSeconds = 2f;
        [Tooltip("The head counts as tracked once it is this high above the rig (m): until then the builder's pose stays.")]
        public float trackedHeight = 0.5f;

        public int Placements { get; private set; }
        bool m_Placed;
        float m_OutSince = -1f;

        void OnEnable()
        {
            AppState.Changed += OnMode;
            if (OVRManager.display != null) OVRManager.display.RecenteredPose += OnRecentre;
        }

        void OnDisable()
        {
            AppState.Changed -= OnMode;
            if (OVRManager.display != null) OVRManager.display.RecenteredPose -= OnRecentre;
        }

        void OnRecentre() => m_Placed = false;
        void OnMode(AppMode from, AppMode to) { if (to == AppMode.Passthrough) m_Placed = false; }

        /// The pose for a head at `headPos` looking along `headForward` (only its heading counts): pure, for tests.
        public static Pose PoseFor(Vector3 headPos, Vector3 headForward, float distance, float downDeg, float yawDeg = 0f)
        {
            var flat = Vector3.ProjectOnPlane(headForward, Vector3.up);
            flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward;
            flat = Quaternion.AngleAxis(yawDeg, Vector3.up) * flat;
            var dir = Quaternion.AngleAxis(downDeg, Vector3.Cross(Vector3.up, flat)) * flat;
            return new Pose(headPos + dir * distance, Quaternion.LookRotation(dir, Vector3.up));
        }

        public void PlaceNow()
        {
            if (head == null) return;
            var p = PoseFor(head.position, head.forward, distance, downDeg, yawDeg);
            transform.SetPositionAndRotation(p.position, p.rotation);
            m_Placed = true;
            m_OutSince = -1f;
            Placements++;
        }

        void LateUpdate()
        {
            if (head == null) return;
            var rig = head.parent != null ? head.parent.root : null;
            bool tracked = rig == null || head.position.y - rig.position.y > trackedHeight;
            if (!tracked) return;
            if (!m_Placed) { PlaceNow(); return; }
            if (AppState.Mode != AppMode.Passthrough) return;
            float off = Vector3.Angle(head.forward, transform.position - head.position);
            if (off <= outOfViewDeg) { m_OutSince = -1f; return; }
            if (m_OutSince < 0f) m_OutSince = Time.unscaledTime;
            else if (Time.unscaledTime - m_OutSince > outOfViewSeconds) PlaceNow();
        }
    }
}
