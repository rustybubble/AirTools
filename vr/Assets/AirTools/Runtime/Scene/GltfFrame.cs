using System.Collections.Generic;
using UnityEngine;

namespace AirTools.Scene
{
    /// Scene packages are in glTF convention (metres, right-handed, +Y up). glTFast imports meshes into Unity by
    /// negating X, so every point and direction we parse ourselves (structure, cameras, spawn) goes through the same
    /// flip: unity = (−x, y, z). Plane offsets are unchanged (n·x + offset = 0 still holds with both negated).
    public static class GltfFrame
    {
        public static Vector3 ToUnity(double x, double y, double z) => new Vector3((float)-x, (float)y, (float)z);

        public static Vector3 ToUnity(IReadOnlyList<double> a) =>
            a == null || a.Count < 3 ? Vector3.zero : ToUnity(a[0], a[1], a[2]);

        public static Vector3 ToUnity(Vector3 gltf) => new Vector3(-gltf.x, gltf.y, gltf.z);

        /// The inverse is the same flip.
        public static Vector3 ToGltf(Vector3 unity) => new Vector3(-unity.x, unity.y, unity.z);

        public static Vector3 Vec(IReadOnlyList<double> a) =>
            a == null || a.Count < 3 ? Vector3.zero : new Vector3((float)a[0], (float)a[1], (float)a[2]);
    }
}
