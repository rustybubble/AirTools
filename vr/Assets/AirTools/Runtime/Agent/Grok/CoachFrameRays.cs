using AirTools.Scene;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// A pinhole camera as basis vectors in Unity package space (SceneRoot.Content local): a scene package camera
    /// (cameras.r&lt;rev&gt;.json) or a frame rendered from the eye. Pixel (u, v) (top-left origin, v down) looks along
    /// Right·(u−cx)/fx − Up·(v−cy)/fy + Forward. Plain vector maths (no engine calls), so it runs in offline tests.
    public struct PinholeFrame
    {
        public Vector3 Position, Right, Up, Forward;
        public double Fx, Fy, Cx, Cy;
        public int W, H;

        public bool IsValid => W > 0 && H > 0 && Fx > 0 && Fy > 0 && Forward.sqrMagnitude > 0.5f;
    }

    /// Camera-ray maths for the install coach's overlays: coach_check `results[].box` and coach_stop `box` / `drill_px`
    /// are 0–1 boxes and points in the checked frame; this turns them into rays and box corners in the scene, and
    /// projects the crosshair back into a frame for `drill_px`.
    ///
    /// Lane G2 (live labels, `show_labels`) needs the same frame-box → scene maths; this file is G4's copy (the two
    /// were written in parallel). Scene cameras follow SceneCameras (OpenCV → world by Rᵀ, then the glTF X flip).
    public static class CoachFrameRays
    {
        /// A scene package camera: centre and axes F·Rᵀ·x, F·Rᵀ·(−y), F·Rᵀ·z (F = the glTF → Unity X flip).
        public static PinholeFrame FromSceneCamera(SceneCameraJson c)
        {
            if (c == null || c.R == null) return default;
            return new PinholeFrame
            {
                Position = SceneCameras.Position(c),
                Right = SceneCameras.DirectionToUnity(c, 1, 0, 0),
                Up = SceneCameras.DirectionToUnity(c, 0, -1, 0),
                Forward = SceneCameras.DirectionToUnity(c, 0, 0, 1),
                Fx = c.fx, Fy = c.fy, Cx = c.cx, Cy = c.cy, W = c.w, H = c.h,
            };
        }

        /// A frame rendered from a pose (e.g. the eye): square pixels, principal point at the centre.
        public static PinholeFrame FromView(Vector3 position, Vector3 right, Vector3 up, Vector3 forward, float verticalFovDeg, float aspect,
            int height = 960)
        {
            int h = Mathf.Max(1, height);
            int w = Mathf.Max(1, Mathf.RoundToInt(h * Mathf.Max(0.1f, aspect)));
            double fy = h * 0.5 / System.Math.Tan(Mathf.Clamp(verticalFovDeg, 1f, 179f) * 0.5 * System.Math.PI / 180.0);
            return new PinholeFrame
            {
                Position = position, Right = right.normalized, Up = up.normalized, Forward = forward.normalized,
                Fx = fy, Fy = fy, Cx = w * 0.5, Cy = h * 0.5, W = w, H = h,
            };
        }

        /// The direction through a normalised image point (0–1, top-left origin).
        public static Vector3 Direction(in PinholeFrame f, float nx, float ny)
        {
            double dx = (nx * f.W - f.Cx) / f.Fx, dy = (ny * f.H - f.Cy) / f.Fy;
            var d = f.Right * (float)dx - f.Up * (float)dy + f.Forward;
            return d.normalized;
        }

        public static Ray PixelRay(in PinholeFrame f, float nx, float ny) => new Ray(f.Position, Direction(f, nx, ny));

        /// Distance along the camera's forward axis (the OpenCV z) of a point.
        public static float Depth(in PinholeFrame f, Vector3 p) => Vector3.Dot(p - f.Position, f.Forward);

        /// Projects a package-space point into the frame: normalised (0–1, top-left) coordinates. False behind the camera.
        public static bool Project(in PinholeFrame f, Vector3 p, out Vector2 normalised)
        {
            normalised = default;
            if (!f.IsValid) return false;
            var d = p - f.Position;
            double x = Vector3.Dot(d, f.Right), y = -Vector3.Dot(d, f.Up), z = Vector3.Dot(d, f.Forward);
            if (z <= 1e-6) return false;
            double u = f.Fx * x / z + f.Cx, v = f.Fy * y / z + f.Cy;
            normalised = new Vector2((float)(u / f.W), (float)(v / f.H));
            return true;
        }

        /// A usable 0–1 box: four finite numbers with some area (corners in either order).
        public static bool ValidBox(float[] b)
        {
            if (b == null || b.Length < 4) return false;
            for (int k = 0; k < 4; k++) if (float.IsNaN(b[k]) || float.IsInfinity(b[k])) return false;
            return Mathf.Abs(b[2] - b[0]) > 1e-4f && Mathf.Abs(b[3] - b[1]) > 1e-4f;
        }

        public static Vector2 Centre(float[] b) => new Vector2((b[0] + b[2]) * 0.5f, (b[1] + b[3]) * 0.5f);

        /// The box's corners (top-left, top-right, bottom-right, bottom-left in the image) on the plane `depth` in front
        /// of the camera, facing it: each corner ray scaled to that depth. Use the depth where the centre ray hits the
        /// scene, pulled in a little so the outline isn't swallowed by the surface.
        public static Vector3[] BoxCorners(in PinholeFrame f, float[] box, float depth)
        {
            if (!ValidBox(box) || depth <= 0f) return null;
            float x0 = Mathf.Min(box[0], box[2]), x1 = Mathf.Max(box[0], box[2]);
            float y0 = Mathf.Min(box[1], box[3]), y1 = Mathf.Max(box[1], box[3]);
            var uv = new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };
            var corners = new Vector3[4];
            for (int k = 0; k < 4; k++)
            {
                var dir = Direction(f, uv[k].x, uv[k].y);
                float along = Vector3.Dot(dir, f.Forward);
                if (along <= 1e-4f) return null;
                corners[k] = f.Position + dir * (depth / along);
            }
            return corners;
        }

        /// Clamp a 0–1 point (drill_px must be inside the frame, or the backend answers 400).
        public static float[] Clamp01(Vector2 n) => new[] { Mathf.Clamp01(n.x), Mathf.Clamp01(n.y) };

        /// The drill crosshair holds still while you look at the coach card (to press Check it by hand): the gaze is
        /// within `coneDeg` of the direction to the card.
        public static bool ShouldHoldCrosshair(Vector3 gaze, Vector3 toCard, float coneDeg) =>
            gaze.sqrMagnitude > 1e-8f && toCard.sqrMagnitude > 1e-8f && Vector3.Angle(gaze, toCard) < coneDeg;
    }
}
