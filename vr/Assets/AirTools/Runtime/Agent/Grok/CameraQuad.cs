using AirTools.Scene;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// Where an image quad goes: centre, orientation axes and size. Right / Up / Forward form a proper (unmirrored)
    /// Unity basis, so Quaternion.LookRotation(Forward, Up) orients a Unity Quad (visible from −Z) with the image's
    /// top-left pixel on the −Right, +Up corner, facing back toward the viewer.
    public struct QuadPose
    {
        public Vector3 Centre, Right, Up, Forward;
        public float Width, Height;

        /// A point of the image (u, v in 0–1, top-left origin) on the quad.
        public Vector3 Point(float u, float v) => Centre + (u - 0.5f) * Width * Right + (0.5f - v) * Height * Up;
    }

    /// show_reimagined's placement (backend docs/api.md "Placing the quad in Unity"): in the scene frame
    /// (the mesh.r&lt;rev&gt;.glb / cameras.r&lt;rev&gt;.json frame), right = R[0], down = R[1], forward = R[2];
    /// size d·w/fx × d·h/fy; centre = position + d·forward + d·((w/2 − cx)/fx)·right + d·((h/2 − cy)/fy)·down; then the
    /// same glTF→Unity X flip the mesh gets (GltfFrame). Seen from the camera the quad covers exactly the photo's frustum,
    /// so the edit lines up with the mesh behind it. Pure (no engine calls): unit-tested offline on the kitchen cameras.
    public static class CameraQuad
    {
        /// How far in front of the camera the picture floats (scene metres).
        public const float Distance = 1.5f;

        /// The pose in package space (SceneRoot.Content's local frame). `d` in package units. The frame size falls back
        /// to 2·cx × 2·cy, then to the image's own size, when the camera entry leaves w / h out.
        public static bool TryCompute(SceneCameraJson c, float d, out QuadPose pose, int imageW = 0, int imageH = 0)
        {
            pose = default;
            if (c == null || c.R == null || c.R.Length < 3 || c.R[0] == null || c.R[1] == null || c.R[2] == null || c.R[0].Length < 3
                || c.R[1].Length < 3 || c.R[2].Length < 3 || !(c.fx > 0) || !(c.fy > 0) || !(d > 0)) return false;
            double w = c.w > 0 ? c.w : c.cx > 0 ? 2 * c.cx : imageW;
            double h = c.h > 0 ? c.h : c.cy > 0 ? 2 * c.cy : imageH;
            if (!(w > 0) || !(h > 0)) return false;
            double cx = c.cx > 0 ? c.cx : w / 2, cy = c.cy > 0 ? c.cy : h / 2;

            var right = Row(c.R[0]);
            var down = Row(c.R[1]);
            var fwd = Row(c.R[2]);
            var pos = GltfFrame.ToGltf(SceneCameras.Position(c));   // position, else −Rᵀt (scene frame)
            double ox = (w / 2 - cx) / c.fx, oy = (h / 2 - cy) / c.fy;
            var centre = pos + d * fwd + (float)(d * ox) * right + (float)(d * oy) * down;

            pose.Centre = GltfFrame.ToUnity(centre);
            pose.Right = GltfFrame.ToUnity(right).normalized;
            pose.Up = GltfFrame.ToUnity(-down).normalized;
            pose.Forward = GltfFrame.ToUnity(fwd).normalized;
            pose.Width = (float)(d * w / c.fx);
            pose.Height = (float)(d * h / c.fy);
            return true;
        }

        static Vector3 Row(double[] r) => new Vector3((float)r[0], (float)r[1], (float)r[2]);

        /// The loaded scan's camera for the edit's frame, but only when it is the same camera: the action's copy (when
        /// it has a pose) must sit within `tol` of the loaded one. Another scan with the same frame id, or no scan
        /// loaded → null, and the picture floats in front of the person instead of landing somewhere wrong.
        public static SceneCameraJson Match(SceneCameraJson loaded, SceneCameraJson fromAction, float tol = 0.05f)
        {
            if (loaded == null) return null;
            bool posed = fromAction != null && ((fromAction.position != null && fromAction.position.Length >= 3) || (fromAction.R != null && fromAction.t != null));
            if (!posed) return loaded;
            return (SceneCameras.Position(loaded) - SceneCameras.Position(fromAction)).magnitude <= tol ? loaded : null;
        }

        /// A picture floating in front of the person (the walk-in clip starts at another camera's pose, and a picture
        /// with no camera): `distance` along the level gaze, `downDeg` below the eye line, `width` wide at `aspect`.
        public static QuadPose Floating(Vector3 eye, Vector3 gaze, float distance, float downDeg, float width, float aspect)
        {
            var flat = new Vector3(gaze.x, 0f, gaze.z);
            flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward;
            float rad = downDeg * Mathf.Deg2Rad;
            var dir = (flat * Mathf.Cos(rad) + Vector3.down * Mathf.Sin(rad)).normalized;   // faces the eyes (pitch + yaw)
            var right = Vector3.Cross(Vector3.up, dir).normalized;
            return new QuadPose
            {
                Centre = eye + dir * distance,
                Forward = dir,
                Up = Vector3.Cross(dir, right),
                Right = right,
                Width = width,
                Height = width / Mathf.Max(0.1f, aspect),
            };
        }
    }
}
