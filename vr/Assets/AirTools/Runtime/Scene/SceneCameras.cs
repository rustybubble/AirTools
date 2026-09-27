using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using UnityEngine;

namespace AirTools.Scene
{
    /// One entry of cameras.r&lt;rev&gt;.json (plan §1): the drone photo's pose and full-frame intrinsics.
    /// R is world-to-camera (OpenCV: x right, y down, z forward), x_cam = R·x_world + t, in the glTF scene frame.
    public class SceneCameraJson
    {
        public string id;
        public string file;
        public string thumb;
        public double[][] R;
        public double[] t;
        public double[] position;
        public double fx, fy, cx, cy;
        public int w, h;
    }

    /// Converts package cameras to <see cref="SceneCameraInfo"/> (Unity frame) and casts rays through their pixels.
    public static class SceneCameras
    {
        public static List<SceneCameraJson> Parse(string json) =>
            JsonConvert.DeserializeObject<List<SceneCameraJson>>(json, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
            }) ?? new List<SceneCameraJson>();

        /// Camera centre in Unity package coordinates: position if given, else −Rᵀt; then the X flip.
        public static Vector3 Position(SceneCameraJson c)
        {
            if (c.position != null && c.position.Length >= 3) return GltfFrame.ToUnity(c.position);
            if (c.R == null || c.t == null) return Vector3.zero;
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < 3; i++)
            {
                x -= c.R[i][0] * c.t[i];
                y -= c.R[i][1] * c.t[i];
                z -= c.R[i][2] * c.t[i];
            }
            return GltfFrame.ToUnity(x, y, z);
        }

        /// A camera-space direction (OpenCV) → Unity world direction: F·Rᵀ·d with F the X flip.
        public static Vector3 DirectionToUnity(SceneCameraJson c, double dx, double dy, double dz)
        {
            // Rᵀ·d: column i of Rᵀ is row i of R.
            double x = c.R[0][0] * dx + c.R[1][0] * dy + c.R[2][0] * dz;
            double y = c.R[0][1] * dx + c.R[1][1] * dy + c.R[2][1] * dz;
            double z = c.R[0][2] * dx + c.R[1][2] * dy + c.R[2][2] * dz;
            return GltfFrame.ToUnity(x, y, z);
        }

        /// Unity rotation of a camera that looks along +Z with +Y up: forward = F·Rᵀ·(0,0,1), up = F·Rᵀ·(0,−1,0).
        /// With it, F·Rᵀ·(x, y, z)_cv = rotation · (x, −y, z) (the flip and OpenCV's y-down cancel into a proper rotation).
        public static Quaternion Rotation(SceneCameraJson c)
        {
            if (c.R == null) return Quaternion.identity;
            var fwd = DirectionToUnity(c, 0, 0, 1);
            var up = DirectionToUnity(c, 0, -1, 0);
            return fwd.sqrMagnitude > 1e-10f ? Quaternion.LookRotation(fwd, up) : Quaternion.identity;
        }

        /// Package-space ray through a normalised image point (0–1, top-left origin), e.g. the centre of a /scene/ask box.
        /// Uses the camera's own full-frame intrinsics, so the thumbnail size doesn't matter.
        public static Ray PixelRay(SceneCameraJson c, float nx, float ny)
        {
            double u = nx * c.w, v = ny * c.h;
            double dx = (u - c.cx) / c.fx, dy = (v - c.cy) / c.fy;
            var dir = DirectionToUnity(c, dx, dy, 1).normalized;
            return new Ray(Position(c), dir);
        }

        /// Projects a Unity package-space point into the camera: normalised (0–1, top-left origin) image coordinates.
        /// False when the point is behind the camera. (Inverse of PixelRay; used by tests and evidence checks.)
        public static bool Project(SceneCameraJson c, Vector3 unityPoint, out Vector2 normalised)
        {
            normalised = default;
            var g = GltfFrame.ToGltf(unityPoint);
            double xc = c.R[0][0] * g.x + c.R[0][1] * g.y + c.R[0][2] * g.z + c.t[0];
            double yc = c.R[1][0] * g.x + c.R[1][1] * g.y + c.R[1][2] * g.z + c.t[1];
            double zc = c.R[2][0] * g.x + c.R[2][1] * g.y + c.R[2][2] * g.z + c.t[2];
            if (zc <= 1e-6) return false;
            double u = c.fx * xc / zc + c.cx, v = c.fy * yc / zc + c.cy;
            normalised = new Vector2((float)(u / c.w), (float)(v / c.h));
            return true;
        }

        public static SceneCameraInfo ToInfo(SceneCameraJson c, int index)
        {
            float vfov = c.fy > 0 && c.h > 0 ? 2f * Mathf.Atan((float)(c.h / (2.0 * c.fy))) * Mathf.Rad2Deg : 60f;
            float aspect = c.fx > 0 && c.fy > 0 && c.h > 0 ? (float)((c.w / c.fx) / (c.h / c.fy)) : 1.5f;
            return new SceneCameraInfo
            {
                id = NumericId(c.id, index),
                key = c.id ?? index.ToString(CultureInfo.InvariantCulture),
                thumbPath = c.thumb,
                position = Position(c),
                rotation = Rotation(c),
                verticalFovDeg = vfov,
                aspect = aspect,
            };
        }

        /// Camera ids are strings ("0042"); the notebook keys evidence by int. Numeric ids keep their value.
        public static int NumericId(string id, int fallback) =>
            int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        /// The (up to) n cameras nearest to a package-space point that look toward it, nearest first
        /// (frames for /scene/ask: "at most 3, nearest first").
        public static List<int> NearestLookingAt(IReadOnlyList<SceneCameraJson> cams, Vector3 unityPoint, int n)
        {
            var scored = new List<(float d, int i)>();
            for (int i = 0; i < cams.Count; i++)
            {
                if (cams[i].R == null || cams[i].t == null) continue;
                if (!Project(cams[i], unityPoint, out var uv) || uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) continue;
                scored.Add((Vector3.Distance(Position(cams[i]), unityPoint), i));
            }
            scored.Sort((a, b) => a.d.CompareTo(b.d));
            var result = new List<int>();
            for (int k = 0; k < scored.Count && k < n; k++) result.Add(scored[k].i);
            return result;
        }

        /// The photos nearest the wearer's view, best first (package space): the cameras that see the focus point,
        /// nearest to it first; with no focus (or no photo sees it), the cameras taken nearest to where you stand.
        /// The one pick for /scene/ask and voice `frames`, and the agent context's `frame_id` (= frames[0].id).
        public static List<int> PickView(IReadOnlyList<SceneCameraJson> cams, Vector3? focusPackage, Vector3 headPackage, int n)
        {
            if (cams == null || cams.Count == 0 || n <= 0) return new List<int>();
            var pick = focusPackage.HasValue ? NearestLookingAt(cams, focusPackage.Value, n) : new List<int>();
            if (pick.Count > 0) return pick;
            var order = new List<int>();
            for (int i = 0; i < cams.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                int c = Vector3.Distance(Position(cams[a]), headPackage).CompareTo(Vector3.Distance(Position(cams[b]), headPackage));
                return c != 0 ? c : a.CompareTo(b);
            });
            return order.GetRange(0, Mathf.Min(n, order.Count));
        }
    }
}
