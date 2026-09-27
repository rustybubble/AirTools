using AirTools.Core;
using UnityEngine;

namespace AirTools.Scene
{
    /// The real table in the room (presence.md S1/P2): where the 1:50 model and the bought parts go. Fallback chain:
    /// 1. the site mat, when its QR has been seen and locked (SiteMatTracker): exact, level, with fixed spots;
    /// 2. an environment raycast straight down at the point in front of you (MRUK EnvironmentRaycastManager, device
    ///    only): accepted when the surface is level (&lt; 10°) and 0.5–1.1 m above the floor;
    /// 3. an assumed height (0.75 m) above the floor.
    /// Frames are level (y = world up), z pointing away from you (your forward), x to your right.
    public class RealTable : MonoBehaviour
    {
        public const string SourceMat = "mat", SourceRaycast = "raycast", SourceAssumed = "assumed";

        public Transform rig;
        public Transform head;
        public SiteMatTracker mat;
        public float assumedHeight = 0.75f;
        [Tooltip("A raycast hit counts as a table between these heights above the floor (m) …")]
        public float minHeight = 0.5f, maxHeight = 1.1f;
        [Tooltip("… and when its normal is within this of vertical (degrees).")]
        public float maxTiltDeg = 10f;
        [Tooltip("Ignore a locked mat further than this from where you're looking (m): you've moved to another table.")]
        public float matMaxDistance = 2.5f;

        /// Environment raycast (installed on the device by SiteMatTracker; null in the Editor): true on a hit.
        public delegate bool Probe(Ray ray, float maxDistance, out Vector3 point, out Vector3 normal);
        public Probe raycast;

        public string LastSource { get; private set; } = "";
        public Pose LastFrame { get; private set; }

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);

        float FloorY => rig != null ? rig.position.y : 0f;

        Vector3 ViewerForward()
        {
            var h = head != null ? head : rig;
            var f = Vector3.ProjectOnPlane(h != null ? h.forward : Vector3.forward, Vector3.up);
            return f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        }

        /// The table frame near `near` (a point in front of you): always succeeds (the assumed height is the last
        /// resort); `source` says which rung of the chain it came from.
        public bool TryGetTableFrame(Vector3 near, out Pose frame, out string source)
        {
            Pose? matPose = null;
            if (mat != null && mat.TryGetMatPose(out var mp))
            {
                var flat = Vector3.ProjectOnPlane(mp.position - near, Vector3.up);
                if (flat.magnitude <= matMaxDistance) matPose = mp;
            }
            (Vector3 point, Vector3 normal)? hit = null;
            if (matPose == null && raycast != null)
            {
                float floor = FloorY;
                var origin = new Vector3(near.x, floor + maxHeight + 0.3f, near.z);
                if (raycast(new Ray(origin, Vector3.down), maxHeight + 0.3f - minHeight + 0.3f, out var p, out var n)) hit = (p, n);
            }
            frame = Resolve(matPose, hit, near, ViewerForward(), FloorY, assumedHeight, minHeight, maxHeight, maxTiltDeg, out source);
            LastSource = source;
            LastFrame = frame;
            return true;
        }

        /// The fallback order, pure: mat &gt; accepted raycast hit &gt; assumed height.
        public static Pose Resolve(Pose? mat, (Vector3 point, Vector3 normal)? hit, Vector3 near, Vector3 forward, float floorY,
            float assumedHeight, float minHeight, float maxHeight, float maxTiltDeg, out string source)
        {
            forward = Vector3.ProjectOnPlane(forward, Vector3.up);
            var facing = Quaternion.LookRotation(forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward, Vector3.up);
            if (mat.HasValue)
            {
                source = SourceMat;
                return mat.Value;
            }
            if (hit.HasValue && AcceptHit(hit.Value.point, hit.Value.normal, floorY, minHeight, maxHeight, maxTiltDeg))
            {
                source = SourceRaycast;
                return new Pose(hit.Value.point, facing);
            }
            source = SourceAssumed;
            return new Pose(new Vector3(near.x, floorY + assumedHeight, near.z), facing);
        }

        public static bool AcceptHit(Vector3 point, Vector3 normal, float floorY, float minHeight, float maxHeight, float maxTiltDeg)
        {
            float h = point.y - floorY;
            return normal.sqrMagnitude > 1e-6f && Vector3.Angle(normal, Vector3.up) < maxTiltDeg && h >= minHeight && h <= maxHeight;
        }

        /// Where the model stands on a mat frame (the mat's model spot; the frame itself without a mat spec).
        public Vector3 ModelSpot(Pose matFrame) =>
            matFrame.position + matFrame.rotation * (mat != null && mat.Spec != null ? mat.Spec.ModelSpot : Vector3.zero);

        /// Where the bought parts go on a mat frame.
        public Vector3 PartsSpot(Pose matFrame) =>
            matFrame.position + matFrame.rotation * (mat != null && mat.Spec != null ? mat.Spec.PartsSpot : Vector3.zero);
    }
}
