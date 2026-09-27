using System.Collections.Generic;
using AirTools.Core;
using UnityEngine;

namespace AirTools.Scene
{
    /// Face = the surface itself (mesh hit). Midpoint and Plane come from a package's structure layer.
    public enum SnapKind { None, Face, Edge, Corner, Midpoint, Plane }

    public struct SurfaceHit
    {
        public Vector3 point;
        public Vector3 normal;
        public Collider collider;
        public SnapKind kind;
        /// Where the ray / query actually touched the surface, before feature snapping.
        public Vector3 rawPoint;
        /// Structure-layer feature that was snapped: index + 1 into the layer's Corners (Corner), Edges (Edge, Midpoint)
        /// or Planes (Plane); 0 = none (box or mesh snapping).
        public int structureFeature;
    }

    /// Snapping against the scene mesh (colliders on layer SceneSurface).
    /// 1. Find the surface point (ray hit, or nearest surface point within a radius).
    /// 2. Feature snap *within the surface that was hit*: to the nearest corner within <see cref="CornerRadius"/>,
    ///    else the nearest edge within <see cref="EdgeRadius"/>. Corners/edges are the hit face's own boundary and the
    ///    lines/points where neighbouring surfaces meet it (inside corners, e.g. jamb × sill × wall face).
    /// Box colliders get full feature snapping (the synthetic facade); mesh colliders snap to the hit triangle's
    /// vertices (real captures; richer mesh snapping is an M7 item).
    public static class SnapService
    {
        public const float DefaultRadius = 0.15f;
        public static float CornerRadius = 0.06f;
        public static float EdgeRadius = 0.04f;
        const float OnFaceTolerance = 0.002f;
        /// World units per scene metre (1 normally, 0.02 on the 1:50 tabletop): feature radii and tolerances are
        /// scene-scale, so snapping behaves the same on the miniature.
        public static float Scale = 1f;
        static float Cr => CornerRadius * Scale;
        static float Er => EdgeRadius * Scale;
        static float Tol => OnFaceTolerance * Scale;

        static readonly Collider[] s_Overlap = new Collider[64];
        static readonly List<Face> s_Faces = new List<Face>(128);

        /// Snapping toggle (palm menu): off → measure points land on the raw surface hit, never on features.
        public static bool Enabled = true;

        /// Contacts with a scanned scene shallower than this (scene metres) don't count as collisions: photogrammetry
        /// surfaces are ±1–2 cm noise, so a part seated on the fitted plane always grazes a few bumps.
        public static float ScanTolerance = 0.02f;
        public static float ScanToleranceWorld => ScanTolerance * Scale;

        /// A collider of the loaded scan (a runtime package's collision mesh), as opposed to the built-in facade's
        /// exact boxes or placed parts.
        public static bool IsScan(Collider c) =>
            c != null && ((1 << c.gameObject.layer) & SceneLayers.SceneSurfaceMask) != 0
            && Services.TryGet<SceneRoot>(out var root) && root.IsRuntimePackage && root.StructureSpace != null
            && c.transform.IsChildOf(root.StructureSpace);

        /// features = true: corner / edge / plane snapping. false: the surface point only — but on a package with a
        /// structure layer the point and normal still come from the fitted plane under it (exact level and seating).
        /// memory: per-pointer hysteresis for structure snaps (keeps the current snap until 1.5× its acquire radius).
        public static bool TryRaySnap(Ray ray, out SurfaceHit hit, float maxDist = 20f, bool features = true,
            StructureSnapper.Memory memory = null)
        {
            hit = default;
            if (!Physics.Raycast(ray, out var rh, maxDist, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore))
                return (!features || Enabled) && TryPackageSnapWithoutHit(ray, maxDist, features, memory, ref hit);
            hit = new SurfaceHit { point = rh.point, rawPoint = rh.point, normal = rh.normal, collider = rh.collider, kind = SnapKind.Face };
            if (TryPackageSnap(ray, rh, features, memory, ref hit)) return true;
            if (features && Enabled) hit = SnapToFeatures(hit, rh.triangleIndex, rh.distance);   // W1.2: radii grow with the ray
            return true;
        }

        /// No mesh under the ray (past the edge of a scan, or a hole in the collision mesh): a structure corner / edge
        /// along the ray, or the plane it crosses, still snaps. features = false: the plane only (level, part seating).
        /// The hit's collider is the package's collision mesh, so callers that name or keep the surface still work.
        static bool TryPackageSnapWithoutHit(Ray ray, float maxDist, bool features, StructureSnapper.Memory memory, ref SurfaceHit hit)
        {
            if (!Services.TryGet<SceneRoot>(out var root) || !root.IsRuntimePackage || root.Structure == null || root.StructureSpace == null) return false;
            if (!root.StructureSpace.gameObject.activeInHierarchy) return false;
            var space = root.StructureSpace;
            float worldPerLocal = Mathf.Max(space.lossyScale.x, 1e-6f);
            var settings = StructureSnapper.Settings.Default.Scaled(Scale / worldPerLocal);
            if (!features) settings.planesOnly = true;
            var o = space.InverseTransformPoint(ray.origin);
            var d = space.InverseTransformDirection(ray.direction).normalized;
            int keep = memory != null && memory.kind == SnapKind.Plane ? memory.feature : -1;
            if (!StructureSnapper.SnapWithoutHit(root.Structure, o, d, settings, maxDist / worldPerLocal, out var r, keep)) { memory?.Clear(); return false; }
            var p = space.TransformPoint(r.point);
            hit = new SurfaceHit
            {
                point = p, rawPoint = p, normal = space.TransformDirection(r.normal).normalized, kind = r.kind,
                structureFeature = r.feature + 1, collider = PackageCollider(space),
            };
            if (memory != null) { memory.kind = r.kind; memory.feature = r.feature; }
            return true;
        }

        static readonly List<MeshCollider> s_PackageColliders = new List<MeshCollider>(4);

        static Collider PackageCollider(Transform space)
        {
            space.GetComponentsInChildren(s_PackageColliders);
            foreach (var c in s_PackageColliders)
                if (((1 << c.gameObject.layer) & SceneLayers.SceneSurfaceMask) != 0) return c;
            return null;
        }

        /// Hits on a runtime scene package (collision GLB): structure-layer snapping when the package has one, else the
        /// plain mesh hit (snapping to photogrammetry triangle vertices would only add noise). False for other colliders.
        static bool TryPackageSnap(Ray ray, RaycastHit rh, bool features, StructureSnapper.Memory memory, ref SurfaceHit hit)
        {
            if (!Services.TryGet<SceneRoot>(out var root) || !root.IsRuntimePackage || root.StructureSpace == null) return false;
            var space = root.StructureSpace;
            if (!rh.collider.transform.IsChildOf(space)) return false;
            var layer = root.Structure;
            if (layer == null || (features && !Enabled)) { memory?.Clear(); return true; }

            float worldPerLocal = Mathf.Max(space.lossyScale.x, 1e-6f);
            var settings = StructureSnapper.Settings.Default.Scaled(Scale / worldPerLocal);
            if (!features) settings.planesOnly = true;
            var q = new StructureSnapper.Query
            {
                origin = space.InverseTransformPoint(ray.origin),
                dir = space.InverseTransformDirection(ray.direction).normalized,
                meshHit = space.InverseTransformPoint(rh.point),
                meshNormal = space.InverseTransformDirection(rh.normal).normalized,
            };
            var r = StructureSnapper.Snap(layer, q, settings, memory);
            hit.point = space.TransformPoint(r.point);
            hit.normal = space.TransformDirection(r.normal).normalized;
            hit.kind = r.kind;
            hit.structureFeature = r.feature + 1;
            return true;
        }

        /// Nearest surface point within radius of p (nothing beyond it).
        public static bool TrySnap(Vector3 p, out SurfaceHit hit, float radius = DefaultRadius, bool features = true,
            StructureSnapper.Memory memory = null)
        {
            hit = default;
            // A scan: the nearest fitted plane. The collision mesh is ±2 cm noise and 50k triangles (its nearest
            // triangle jumps around, and a brute-force search every frame stalls the headset).
            if (Services.TryGet<SceneRoot>(out var root) && root.IsRuntimePackage && root.StructureSpace != null
                && root.StructureSpace.gameObject.activeInHierarchy)
            {
                var layer = root.Structure;
                if (layer == null) return false;
                var space = root.StructureSpace;
                float worldPerLocal = Mathf.Max(space.lossyScale.x, 1e-6f);
                var settings = StructureSnapper.Settings.Default.Scaled(Scale / worldPerLocal);
                int keep = memory != null && memory.kind == SnapKind.Plane ? memory.feature : -1;
                if (!StructureSnapper.NearestPlanePoint(layer, space.InverseTransformPoint(p), radius / worldPerLocal, settings.planeTolerance, keep, out var r))
                {
                    memory?.Clear();
                    return false;
                }
                var q = space.TransformPoint(r.point);
                hit = new SurfaceHit
                {
                    point = q, rawPoint = q, normal = space.TransformDirection(r.normal).normalized, kind = SnapKind.Plane,
                    structureFeature = r.feature + 1, collider = PackageCollider(space),
                };
                if (memory != null) { memory.kind = SnapKind.Plane; memory.feature = r.feature; }
                return true;
            }
            int n = Physics.OverlapSphereNonAlloc(p, radius, s_Overlap, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var c = s_Overlap[i];
                if (!ClosestSurfacePoint(c, p, out var cp, out var normal)) continue;
                float d = Vector3.Distance(cp, p);
                if (d > radius || d >= best) continue;
                best = d;
                hit = new SurfaceHit { point = cp, rawPoint = cp, normal = normal, collider = c, kind = SnapKind.Face };
            }
            if (hit.collider == null) return false;
            if (features) hit = SnapToFeatures(hit, -1);
            return true;
        }

        /// Frame of the last successful axis lock (SnapCursor shows "on axis" while it's recent) and its direction.
        public static int LastAxisLockFrame { get; private set; } = -1;
        public static Vector3 LastAxisDirection { get; private set; }
        /// Degrees within which the tape's second point locks to an axis or edge direction (R6 §7.3: 3°).
        public static float AxisLockDegrees = 3f;

        /// SketchUp-style inference while placing a tape's second point: if the segment from `start` (world) to the hit
        /// is within 3° of a dominant axis (or vertical) or of an edge that runs through the start, the point moves onto
        /// that direction. Only for surface / plane hits (a corner or edge snap is already exact) on a structure package.
        public static bool TryAxisLock(Vector3 start, ref SurfaceHit hit)
        {
            if (!Enabled || (hit.kind != SnapKind.Face && hit.kind != SnapKind.Plane)) return false;
            if (!Services.TryGet<SceneRoot>(out var root) || root.Structure == null || root.StructureSpace == null) return false;
            if (hit.collider == null || !hit.collider.transform.IsChildOf(root.StructureSpace)) return false;
            var space = root.StructureSpace;
            float worldPerLocal = Mathf.Max(space.lossyScale.x, 1e-6f);
            if (!StructureSnapper.TryAxisLock(root.Structure, space.InverseTransformPoint(start), space.InverseTransformPoint(hit.point),
                    AxisLockDegrees, 0.05f * Scale / worldPerLocal, out var locked, out var dir)) return false;
            hit.point = space.TransformPoint(locked);
            LastAxisLockFrame = Time.frameCount;
            LastAxisDirection = space.TransformDirection(dir).normalized;
            return true;
        }

        // ---------------- surface point ----------------

        static bool ClosestSurfacePoint(Collider c, Vector3 p, out Vector3 point, out Vector3 normal)
        {
            point = default; normal = Vector3.up;
            if (c is BoxCollider box)
            {
                GetFaces(box, s_Faces, clear: true);
                float best = float.MaxValue;
                foreach (var f in s_Faces)
                {
                    var q = f.ClosestPoint(p);
                    float d = (q - p).sqrMagnitude;
                    if (d < best) { best = d; point = q; normal = f.normal; }
                }
                return true;
            }
            if (c is MeshCollider mc && !mc.convex)
                return ClosestPointOnMesh(mc, p, out point, out normal);

            point = c.ClosestPoint(p);
            var dir = p - point;
            normal = dir.sqrMagnitude > 1e-10f ? dir.normalized : Vector3.up;
            return true;
        }

        /// Mesh.vertices / triangles copy the whole mesh on every call: read them once per mesh.
        static readonly Dictionary<Mesh, (Vector3[] v, int[] tri)> s_MeshArrays = new Dictionary<Mesh, (Vector3[] v, int[] tri)>();

        static bool ClosestPointOnMesh(MeshCollider mc, Vector3 p, out Vector3 point, out Vector3 normal)
        {
            point = default; normal = Vector3.up;
            var mesh = mc.sharedMesh;
            if (mesh == null) return false;
            var t = mc.transform;
            var local = t.InverseTransformPoint(p);
            if (!s_MeshArrays.TryGetValue(mesh, out var arrays)) s_MeshArrays[mesh] = arrays = (mesh.vertices, mesh.triangles);
            var (v, tri) = arrays;
            float best = float.MaxValue;
            for (int i = 0; i < tri.Length; i += 3)
            {
                var q = ClosestPointOnTriangle(local, v[tri[i]], v[tri[i + 1]], v[tri[i + 2]]);
                float d = (q - local).sqrMagnitude;
                if (d < best)
                {
                    best = d; point = q;
                    normal = Vector3.Cross(v[tri[i + 1]] - v[tri[i]], v[tri[i + 2]] - v[tri[i]]);
                }
            }
            point = t.TransformPoint(point);
            normal = t.TransformDirection(normal).normalized;
            return best < float.MaxValue;
        }

        static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            // Ericson, Real-Time Collision Detection 5.1.5
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }

        // ---------------- feature snapping ----------------

        /// Face of a box collider: rectangle with centre, outward normal, in-plane axes and half extents (world).
        struct Face
        {
            public Vector3 centre, normal, u, v;
            public float hu, hv;
            public float Offset => Vector3.Dot(normal, centre);

            public Vector3 ClosestPoint(Vector3 p)
            {
                var d = p - centre;
                float pu = Mathf.Clamp(Vector3.Dot(d, u), -hu, hu);
                float pv = Mathf.Clamp(Vector3.Dot(d, v), -hv, hv);
                return centre + u * pu + v * pv;
            }

            public float Distance(Vector3 p) => Vector3.Distance(p, ClosestPoint(p));
        }

        static void GetFaces(BoxCollider box, List<Face> faces, bool clear)
        {
            if (clear) faces.Clear();
            var t = box.transform;
            var c = t.TransformPoint(box.center);
            var axes = new[]
            {
                t.TransformVector(new Vector3(box.size.x * 0.5f, 0, 0)),
                t.TransformVector(new Vector3(0, box.size.y * 0.5f, 0)),
                t.TransformVector(new Vector3(0, 0, box.size.z * 0.5f)),
            };
            for (int i = 0; i < 3; i++)
            {
                var a = axes[i]; var b = axes[(i + 1) % 3]; var d = axes[(i + 2) % 3];
                for (int s = -1; s <= 1; s += 2)
                {
                    faces.Add(new Face
                    {
                        centre = c + a * s, normal = a.normalized * s,
                        u = b.normalized, hu = b.magnitude, v = d.normalized, hv = d.magnitude,
                    });
                }
            }
        }

        static void GetBoxCornersAndEdges(BoxCollider box, List<Vector3> corners, List<(Vector3, Vector3)> edges)
        {
            var t = box.transform;
            var c = t.TransformPoint(box.center);
            var ax = t.TransformVector(new Vector3(box.size.x * 0.5f, 0, 0));
            var ay = t.TransformVector(new Vector3(0, box.size.y * 0.5f, 0));
            var az = t.TransformVector(new Vector3(0, 0, box.size.z * 0.5f));
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            for (int sz = -1; sz <= 1; sz += 2)
                corners.Add(c + ax * sx + ay * sy + az * sz);
            for (int s1 = -1; s1 <= 1; s1 += 2)
            for (int s2 = -1; s2 <= 1; s2 += 2)
            {
                edges.Add((c - ax + ay * s1 + az * s2, c + ax + ay * s1 + az * s2));
                edges.Add((c + ax * s1 - ay + az * s2, c + ax * s1 + ay + az * s2));
                edges.Add((c + ax * s1 + ay * s2 - az, c + ax * s1 + ay * s2 + az));
            }
        }

        static readonly List<Vector3> s_Corners = new List<Vector3>(128);
        static readonly List<(Vector3, Vector3)> s_Edges = new List<(Vector3, Vector3)>(192);
        static readonly List<Face> s_Near = new List<Face>(64);

        /// rayDistance: how far along the pointer ray the hit is (0 = not a ray query). With it the corner / edge radii are
        /// angular beyond their metric size (UX W1.2, <see cref="AngularRadii"/>): a hand ray's jitter is an angle.
        public static SurfaceHit SnapToFeatures(SurfaceHit hit, int triangleIndex = -1, float rayDistance = 0f)
        {
            if (hit.collider is MeshCollider mesh) return SnapToMeshVertices(hit, mesh, triangleIndex);

            Vector3 p = hit.point;
            float cr = AngularRadii.Corner(Cr, rayDistance), er = AngularRadii.Edge(Er, rayDistance);   // W1.2
            float search = Mathf.Max(cr, er) + 0.01f * Scale;
            int n = Physics.OverlapSphereNonAlloc(p, search, s_Overlap, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore);

            s_Near.Clear(); s_Corners.Clear(); s_Edges.Clear();
            for (int i = 0; i < n; i++)
            {
                if (!(s_Overlap[i] is BoxCollider box)) continue;
                GetFaces(box, s_Faces, clear: true);
                foreach (var f in s_Faces)
                    if (f.Distance(p) <= search) s_Near.Add(f);
                GetBoxCornersAndEdges(box, s_Corners, s_Edges);
            }

            // The face we hit: coplanar with the hit and containing the hit point.
            int hitFace = -1; float bestFace = float.MaxValue;
            for (int i = 0; i < s_Near.Count; i++)
            {
                var f = s_Near[i];
                if (Vector3.Dot(f.normal, hit.normal) < 0.99f) continue;
                float d = f.Distance(p);
                if (d < bestFace) { bestFace = d; hitFace = i; }
            }
            if (hitFace < 0 || bestFace > Tol * 5f) return hit;
            var face = s_Near[hitFace];

            bool OnHitFace(Vector3 q) =>
                Mathf.Abs(Vector3.Dot(face.normal, q) - face.Offset) <= Tol && face.Distance(q) <= Tol;

            // Corners: box corners on the hit face, and points where two other surfaces meet the hit face.
            Vector3 bestCorner = default; float bestCornerD = float.MaxValue;
            void ConsiderCorner(Vector3 q)
            {
                float d = Vector3.Distance(q, p);
                if (d <= cr && d < bestCornerD && OnHitFace(q)) { bestCornerD = d; bestCorner = q; }
            }
            foreach (var q in s_Corners) ConsiderCorner(q);
            for (int a = 0; a < s_Near.Count; a++)
            for (int b = a + 1; b < s_Near.Count; b++)
            {
                if (a == hitFace || b == hitFace) continue;
                if (!IntersectPlanes(face, s_Near[a], s_Near[b], out var q)) continue;
                if (s_Near[a].Distance(q) > Tol || s_Near[b].Distance(q) > Tol) continue;
                ConsiderCorner(q);
            }
            if (bestCornerD <= cr)
                return With(hit, bestCorner, SnapKind.Corner);

            // Edges: box edges lying in the hit face, and lines where another surface meets the hit face.
            Vector3 bestEdge = default; float bestEdgeD = float.MaxValue;
            void ConsiderEdge(Vector3 q)
            {
                float d = Vector3.Distance(q, p);
                if (d <= er && d < bestEdgeD && OnHitFace(q)) { bestEdgeD = d; bestEdge = q; }
            }
            foreach (var (e0, e1) in s_Edges) ConsiderEdge(ClosestPointOnSegment(p, e0, e1));
            for (int a = 0; a < s_Near.Count; a++)
            {
                if (a == hitFace) continue;
                if (!IntersectPlanes(face, s_Near[a], out var origin, out var dir)) continue;
                var q = origin + dir * Vector3.Dot(p - origin, dir);
                if (s_Near[a].Distance(q) > Tol) continue;
                ConsiderEdge(q);
            }
            if (bestEdgeD <= er)
                return With(hit, bestEdge, SnapKind.Edge);

            return hit;
        }

        static SurfaceHit SnapToMeshVertices(SurfaceHit hit, MeshCollider mc, int triangleIndex)
        {
            var mesh = mc.sharedMesh;
            if (mesh == null || triangleIndex < 0) return hit;
            var tri = mesh.triangles; var v = mesh.vertices; var t = mc.transform;
            float best = Cr; Vector3 q = default; bool found = false;
            for (int k = 0; k < 3; k++)
            {
                var w = t.TransformPoint(v[tri[triangleIndex * 3 + k]]);
                float d = Vector3.Distance(w, hit.point);
                if (d <= best) { best = d; q = w; found = true; }
            }
            return found ? With(hit, q, SnapKind.Corner) : hit;
        }

        static SurfaceHit With(SurfaceHit hit, Vector3 point, SnapKind kind)
        {
            hit.point = point;
            hit.kind = kind;
            return hit;
        }

        static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-12f));
            return a + ab * t;
        }

        static bool IntersectPlanes(Face f1, Face f2, out Vector3 origin, out Vector3 dir)
        {
            origin = default;
            dir = Vector3.Cross(f1.normal, f2.normal);
            float len2 = dir.sqrMagnitude;
            if (len2 < 0.04f) return false; // nearly parallel (< ~11.5°)
            float d1 = f1.Offset, d2 = f2.Offset;
            origin = Vector3.Cross(d1 * f2.normal - d2 * f1.normal, dir) / len2;
            dir /= Mathf.Sqrt(len2);
            return true;
        }

        static bool IntersectPlanes(Face f1, Face f2, Face f3, out Vector3 point)
        {
            point = default;
            Vector3 n1 = f1.normal, n2 = f2.normal, n3 = f3.normal;
            float det = Vector3.Dot(n1, Vector3.Cross(n2, n3));
            if (Mathf.Abs(det) < 0.1f) return false;
            point = (f1.Offset * Vector3.Cross(n2, n3) + f2.Offset * Vector3.Cross(n3, n1) + f3.Offset * Vector3.Cross(n1, n2)) / det;
            return true;
        }
    }
}
