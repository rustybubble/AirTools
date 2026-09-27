using System.Collections.Generic;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Parts
{
    /// Geometry for parts. Part space: metres, +Y up, origin at the centre of the mounting face, which faces
    /// <see cref="FaceDirection"/>(mount.face); the part body extends away from it.
    public static class PartMath
    {
        public static bool IsFace(string face) => FaceDirectionOrZero(face) != Vector3.zero;

        public static Vector3 FaceDirection(string face)
        {
            var v = FaceDirectionOrZero(face);
            return v == Vector3.zero ? Vector3.back : v;
        }

        static Vector3 FaceDirectionOrZero(string face)
        {
            switch ((face ?? "").Trim().ToLowerInvariant())
            {
                case "-x": return Vector3.left;
                case "+x": case "x": return Vector3.right;
                case "-y": return Vector3.down;
                case "+y": case "y": return Vector3.up;
                case "-z": return Vector3.back;
                case "+z": case "z": return Vector3.forward;
                default: return Vector3.zero;
            }
        }

        /// The part's true-size box (from dims_mm) in part space.
        public static Bounds LocalBox(PartSpec spec)
        {
            var size = spec.dims_mm.Metres;
            var mount = FaceDirection(spec.mount.face);
            return new Bounds(-Vector3.Scale(mount, size) * 0.5f, size);
        }

        /// Rotation that puts the mounting face flat against a surface with outward normal n.
        /// Wall-type mounts keep the part upright; floor/sill (±y) mounts turn the front (+z) toward the viewer.
        public static Quaternion PlacementRotation(PartSpec spec, Vector3 normal, Vector3 viewerDirection)
        {
            var a = FaceDirection(spec.mount.face);
            var targetA = -normal.normalized;
            bool yMount = Mathf.Abs(a.y) > 0.5f;
            var b = yMount ? Vector3.forward : Vector3.up;
            var targetB = yMount ? viewerDirection : Vector3.up;
            targetB = Vector3.ProjectOnPlane(targetB, targetA);
            if (targetB.sqrMagnitude < 1e-4f)
            {
                // Up is along the normal (a wall-type part on a floor/ceiling): face the viewer instead.
                targetB = Vector3.ProjectOnPlane(yMount ? Vector3.up : viewerDirection, targetA);
                if (targetB.sqrMagnitude < 1e-4f) targetB = Vector3.ProjectOnPlane(Vector3.forward, targetA);
                if (targetB.sqrMagnitude < 1e-4f) targetB = Vector3.ProjectOnPlane(Vector3.right, targetA);
            }
            var local = Quaternion.LookRotation(a, b);
            var world = Quaternion.LookRotation(targetA, targetB.normalized);
            return world * Quaternion.Inverse(local);
        }

        /// Rotation while held: the mounting face leads along the pointer, part kept upright.
        public static Quaternion HeldRotation(PartSpec spec, Vector3 pointerForward, Vector3 viewerDirection)
        {
            var flat = Vector3.ProjectOnPlane(pointerForward, Vector3.up);
            var n = flat.sqrMagnitude > 1e-4f ? -flat.normalized : Vector3.back;
            var a = FaceDirection(spec.mount.face);
            if (Mathf.Abs(a.y) > 0.5f) n = a.y < 0 ? Vector3.up : Vector3.down;
            return PlacementRotation(spec, n, viewerDirection);
        }

        /// Bounds of all renderers under root, in root's local space (meshes' own bounds, transformed exactly).
        public static bool RendererBounds(Transform root, out Bounds bounds) => RendererBounds(root, root, out bounds);

        /// Bounds of the renderers under subtree, expressed in space's local frame.
        public static bool RendererBounds(Transform root, Transform space, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            var toRoot = space.worldToLocalMatrix;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.GetComponent<MeshRenderer>() == null) continue;
                Encapsulate(ref bounds, ref any, mf.sharedMesh.bounds, toRoot * mf.transform.localToWorldMatrix);
            }
            foreach (var sk in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (sk.sharedMesh != null) Encapsulate(ref bounds, ref any, sk.sharedMesh.bounds, toRoot * sk.transform.localToWorldMatrix);
            return any;
        }

        static void Encapsulate(ref Bounds b, ref bool any, Bounds local, Matrix4x4 m)
        {
            var c = local.center; var e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                var p = m.MultiplyPoint3x4(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                else b.Encapsulate(p);
            }
        }

        /// Uniform scale that best matches measured size to the published size (median of the per-axis ratios,
        /// robust to one bad axis), and the worst remaining per-axis error in percent.
        public static float FitScale(Vector3 measured, Vector3 published, out float residualPct)
        {
            var r = new List<float>(3);
            for (int i = 0; i < 3; i++) if (measured[i] > 1e-6f) r.Add(published[i] / measured[i]);
            if (r.Count == 0) { residualPct = 100f; return 1f; }
            r.Sort();
            float s = r[r.Count / 2];
            residualPct = 0f;
            for (int i = 0; i < 3; i++)
                if (published[i] > 1e-6f) residualPct = Mathf.Max(residualPct, Mathf.Abs(measured[i] * s - published[i]) / published[i] * 100f);
            return s;
        }

        public static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float t = ab.sqrMagnitude > 1e-12f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector3.Distance(p, a + ab * t);
        }
    }
}
