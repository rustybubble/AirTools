using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// State the Grok integration lanes share (backend integration/grok-all). Lane G1's AgentContext reads what the other
    /// lanes set here and sends it as context (docs/api.md §6). Each lane keeps its fields in its own marked block.
    public static partial class GrokState
    {
        // ---------------- Grok G4: progress rails + install coach (F15 / F17) ----------------

        /// The install coach's drill crosshair as `drill_px` ([x, y], 0–1, top-left origin) for the frame sent with
        /// "check it" on a drill step (docs/api.md §6 `drill_px`). [0.5, 0.5] (the backend's default, the frame centre)
        /// until the crosshair lands on the scene; null when no drill step is up (then leave drill_px out).
        /// The crosshair holds still while you look at the coach card, so it is not always the view centre: when the
        /// frame you send is a scene camera's photo, use DrillPxIn(frame) instead (it projects DrillPoint).
        public static float[] DrillPx;

        /// The scene point under the crosshair: in package space (SceneRoot.Content local, the frame scene cameras
        /// use) and in world space. Null when the crosshair isn't on the scene.
        public static Vector3? DrillPoint, DrillWorld;

        /// The eye (world pose) when the crosshair last moved: it was at the centre of that view, so DrillPx
        /// ([0.5, 0.5]) is right for a frame rendered from this pose. (You look away to press Check it.)
        public static Pose? DrillEye;

        /// True while an install coach runs (the backend's check_step reads `frame_jpg_b64`, so G1 can attach a fresh
        /// frame to "check it" / "done").
        public static bool CoachActive;

        /// True while the current coach step is a drill step (`tool: "drill"`): drill_px goes with "check it".
        public static bool DrillActive;

        /// The coach and the "do the whole job" run on the rails (null when none).
        public static string CoachId, RunId;

        /// The id of the frame G1 last sent as `frame_jpg_b64` (a `cameras.r<rev>.json` id, or G1's own view-frame id).
        /// coach_check over /agent/command arrives with `frame_id: null` (the backend's check_step passes no frame id
        /// to coach.check), and coach_stop never has one, so the coach draws their boxes from this frame. G1 sets it.
        public static string LastFrameId;

        /// G1 may register the pose of a frame it rendered from the eye (not a scene camera) under its id, so boxes in
        /// that frame can be projected too. Package space, like scene cameras.
        public static string ViewFrameId;
        public static PinholeFrame ViewFrame;

        /// drill_px for a given frame (a scene camera via CoachFrameRays.FromSceneCamera, or a view frame): the
        /// crosshair's scene point projected into it, clamped to 0–1; DrillPx when the crosshair isn't on the scene;
        /// null when no drill step is up or the point is behind that camera.
        public static float[] DrillPxIn(in PinholeFrame frame)
        {
            if (!DrillActive) return null;
            if (!DrillPoint.HasValue) return DrillPx;
            if (!CoachFrameRays.Project(frame, DrillPoint.Value, out var n)) return null;
            return new[] { Mathf.Clamp01(n.x), Mathf.Clamp01(n.y) };
        }

        /// Forget the G4 block (tests; Play mode keeps statics between sessions).
        public static void ResetG4()
        {
            DrillPx = null; DrillPoint = null; DrillWorld = null; DrillEye = null;
            CoachActive = false; DrillActive = false;
            CoachId = null; RunId = null;
        }

        // ---------------- end Grok G4 ----------------
    }
}
