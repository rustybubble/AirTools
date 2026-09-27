using AirTools.Scene;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// The one camera ray for "a box in a drone photo → a point on the scan", shared by scene_pin (SceneAsk.PinFromFrame)
    /// and show_labels: the ray from camera `frame_id`'s centre through the box centre, with that camera's full-frame
    /// intrinsics from cameras.r&lt;rev&gt;.json (u = x·w, v = y·h; (u − cx)/fx, (v − cy)/fy; OpenCV → scene by Rᵀ; then the
    /// same glTF → Unity X flip as the mesh: SceneCameras.PixelRay), taken from the package frame to the world through
    /// the scene content (calibration, package rotation, tabletop scale), and cast onto the collision mesh.
    public static class FrameRay
    {
        /// Box centre, normalised 0–1 with a top-left origin.
        public static Vector2 BoxCentre(float[] box) =>
            box == null || box.Length < 4 ? new Vector2(0.5f, 0.5f) : new Vector2((box[0] + box[2]) * 0.5f, (box[1] + box[3]) * 0.5f);

        /// The ray in the package frame (the content's local space). Pure.
        public static Ray Local(SceneCameraJson cam, float[] box)
        {
            var c = BoxCentre(box);
            return SceneCameras.PixelRay(cam, c.x, c.y);
        }

        /// The ray in the world, through the scene content's transform.
        public static Ray World(Transform content, SceneCameraJson cam, float[] box)
        {
            var local = Local(cam, box);
            return new Ray(content.TransformPoint(local.origin), content.TransformDirection(local.direction).normalized);
        }

        /// Cast it onto the scene's collision mesh (SceneSurface layer). False when the ray misses (or no camera/box).
        public static bool TryCast(Transform content, SceneCameraJson cam, float[] box, float maxDistance, out RaycastHit hit)
        {
            hit = default;
            if (content == null || cam == null || cam.R == null || box == null || box.Length < 4) return false;
            var ray = World(content, cam, box);
            float scale = Mathf.Max(content.lossyScale.x, 1e-4f);
            return Physics.Raycast(ray, out hit, maxDistance * scale, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore);
        }
    }
}
