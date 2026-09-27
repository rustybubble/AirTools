using AirTools.Scene;
using UnityEngine;

namespace AirTools.Parts
{
    /// Where a released part goes (SPEC M4): the surface the pointer ray hits within reach (hold distance + snap
    /// radius), else the nearest surface within the snap radius (15 cm) of the part, else — for surfaces out of
    /// reach, like a fascia 6 m up — the surface the ray hits at any distance. The part is rotated so its mounting
    /// face lies flat against the surface, seated on it, then slid within the surface plane out of any overlap.
    public static class PartPlacer
    {
        static readonly Collider[] s_Hits = new Collider[32];

        /// memory: the pointer's structure-plane memory (keeps a part on the plane it's on until the pointer clearly
        /// leaves it, so it doesn't flicker between the many small planes of a scan).
        public static bool TryFind(PartSpec spec, Pose pointer, float holdDistance, float snapRadius, float maxRay, out SurfaceHit hit, out bool viaRay,
            StructureSnapper.Memory memory = null)
        {
            var ray = new Ray(pointer.position, pointer.forward);
            viaRay = false;
            // A miss of the short-range probes says nothing about the plane the part is on: keep the memory for the ray.
            var heldKind = memory != null ? memory.kind : SnapKind.None;
            int heldFeature = memory != null ? memory.feature : -1;
            if (!SnapService.TryRaySnap(ray, out hit, holdDistance + snapRadius, features: false, memory: memory))
            {
                if (memory != null) { memory.kind = heldKind; memory.feature = heldFeature; }
                if (!SnapService.TrySnap(pointer.position + pointer.forward * holdDistance, out hit, snapRadius, features: false, memory: memory))
                {
                    if (memory != null) { memory.kind = heldKind; memory.feature = heldFeature; }
                    if (!SnapService.TryRaySnap(ray, out hit, maxRay, features: false, memory: memory)) return false;
                    viaRay = true;
                }
            }
            hit = StabiliseScanNormal(MatchSurface(spec, hit), memory);
            return true;
        }

        /// On a scan, a raw mesh hit (no fitted plane under it) has a triangle-noise normal — ~45° on the rounded edge of
        /// a countertop — that swings the part around as the pointer moves: seat it on the nearest fitted plane within
        /// 4 cm instead, else snap the normal to vertical or one of the scene's dominant axes when within 20°.
        public static SurfaceHit StabiliseScanNormal(SurfaceHit hit, StructureSnapper.Memory memory = null)
        {
            if (hit.kind != SnapKind.Face || !SnapService.IsScan(hit.collider)) return hit;
            if (!AirTools.Core.Services.TryGet<SceneRoot>(out var root) || root.StructureSpace == null) return hit;
            // With the part's memory: the plane it's already on wins while within 6 cm (no flipping at corners).
            if (SnapService.TrySnap(hit.point, out var near, 0.04f * SnapService.Scale, features: false, memory: memory)) return near;
            var best = hit.normal;
            float bestDot = Mathf.Cos(20f * Mathf.Deg2Rad);
            Consider(Vector3.up, hit.normal, ref best, ref bestDot);
            if (root.Structure != null)
                foreach (var axis in root.Structure.Axes) Consider(root.StructureSpace.TransformDirection(axis), hit.normal, ref best, ref bestDot);
            hit.normal = best;
            return hit;
        }

        static void Consider(Vector3 axis, Vector3 n, ref Vector3 best, ref float bestDot)
        {
            if (axis.sqrMagnitude < 1e-8f) return;
            axis.Normalize();
            float d = Vector3.Dot(axis, n);
            if (Mathf.Abs(d) <= bestDot) return;
            bestDot = Mathf.Abs(d);
            best = d > 0f ? axis : -axis;
        }

        /// Parts that sit on something (mount −y, e.g. an AC on a sill) prefer an up-facing surface: pointing at
        /// the front edge of a sill puts the part on top of it.
        public static SurfaceHit MatchSurface(PartSpec spec, SurfaceHit hit)
        {
            var mount = PartMath.FaceDirection(spec.mount.face);
            if (mount.y < -0.5f && hit.normal.y < 0.7f
                && SnapService.TrySnap(hit.point + Vector3.up * 0.08f, out var top, 0.2f, features: false) && top.normal.y > 0.7f)
                return top;
            return hit;
        }

        /// Put the part on the surface; returns how far it had to slide to clear overlaps (metres).
        public static float Apply(PartInstance part, SurfaceHit hit, Vector3 viewer, float maxSlide = 0.3f)
        {
            var rot = PartMath.PlacementRotation(part.Spec, hit.normal, FacingDirection(part.Spec, hit, viewer));
            part.transform.SetPositionAndRotation(hit.point, rot);
            part.SurfaceNormal = hit.normal;
            part.Surface = hit.collider;
            return Depenetrate(part, hit.normal, maxSlide);
        }

        /// edit-touch: put the part on the surface keeping `rotation` exactly — a translation only (the Edit view's Move and
        /// its new part: "the whole point is to preserve orientation"). Its box rests on the surface along the hit normal
        /// (SeatOrigin), then slides within the surface plane out of any overlap; returns how far it slid (metres).
        public static float Seat(PartInstance part, SurfaceHit hit, Quaternion rotation, float maxSlide = 0.3f)
        {
            var t = part.transform;
            t.SetPositionAndRotation(SeatOrigin(hit.point, hit.normal, rotation, part.LocalBox, t.lossyScale.x), rotation);
            part.SurfaceNormal = hit.normal;
            part.Surface = hit.collider;
            return Depenetrate(part, hit.normal, maxSlide);
        }

        /// edit-touch: the root position that rests a part turned `rotation` on a surface at `point` (normal `normal`): its
        /// box (`box` in part space, world `scale`) just touches the surface plane on the normal's side, its centre over
        /// `point`. Pure.
        public static Vector3 SeatOrigin(Vector3 point, Vector3 normal, Quaternion rotation, Bounds box, float scale)
        {
            var n = normal.sqrMagnitude > 1e-10f ? normal.normalized : Vector3.up;
            var centre = point + n * Edit6DofMath.ExtentAlong(n, rotation, box.extents * scale);
            return centre - rotation * (box.center * scale);
        }

        /// Which way a sill/floor part's front (+z) should face: square off the nearest wall or glass around the spot
        /// (a window unit sits square in its opening; 8 horizontal probes, 60 cm), else toward the viewer.
        public static Vector3 FacingDirection(PartSpec spec, SurfaceHit hit, Vector3 viewer)
        {
            var toViewer = viewer - hit.point;
            if (PartMath.FaceDirection(spec.mount.face).y > -0.5f) return toViewer;
            var start = Vector3.ProjectOnPlane(-toViewer, hit.normal);
            if (start.sqrMagnitude < 1e-6f) start = Vector3.ProjectOnPlane(Vector3.forward, hit.normal);
            if (start.sqrMagnitude < 1e-6f) return toViewer;
            var origin = hit.point + hit.normal * 0.05f;
            float best = float.MaxValue;
            Vector3 front = Vector3.zero;
            for (int i = 0; i < 8; i++)
            {
                var dir = Quaternion.AngleAxis(i * 45f, hit.normal) * start.normalized;
                if (!Physics.Raycast(origin, dir, out var wall, 0.6f, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore)) continue;
                var n = Vector3.ProjectOnPlane(wall.normal, hit.normal);
                if (n.sqrMagnitude < 0.8f || wall.distance >= best) continue;
                best = wall.distance;
                front = n.normalized;
            }
            return best < float.MaxValue ? front : toViewer;
        }

        /// Slide the part within the surface plane until it no longer overlaps scene surfaces or other parts
        /// (penetrations that point along the normal — the mounting surface itself — are ignored).
        public static float Depenetrate(PartInstance part, Vector3 normal, float maxSlide, int iterations = 6)
        {
            var t = part.transform;
            var box = part.Box;
            var moved = Vector3.zero;
            for (int it = 0; it < iterations; it++)
            {
                Physics.SyncTransforms();
                var half = Vector3.Scale(box.size * 0.5f - Vector3.one * 0.0005f, t.lossyScale);
                int n = Physics.OverlapBoxNonAlloc(t.TransformPoint(box.center), half, s_Hits, t.rotation, PartLayers.FitMask, QueryTriggerInteraction.Ignore);
                var push = Vector3.zero;
                for (int i = 0; i < n; i++)
                {
                    var col = s_Hits[i];
                    if (col == null || col.transform.IsChildOf(t)) continue;
                    var other = col.GetComponentInParent<PartInstance>();
                    if (other != null && other.Held) continue;
                    if (!Physics.ComputePenetration(box, t.position, t.rotation, col, col.transform.position, col.transform.rotation, out var dir, out float dist)) continue;
                    if (dist < (SnapService.IsScan(col) ? SnapService.ScanToleranceWorld : 0.0005f)) continue;
                    var inPlane = Vector3.ProjectOnPlane(dir, normal);
                    if (inPlane.magnitude < 0.3f) continue;
                    var step = inPlane.normalized * (dist / inPlane.magnitude + 0.0005f);
                    if (step.sqrMagnitude > push.sqrMagnitude) push = step;
                }
                if (push == Vector3.zero) break;
                if ((moved + push).magnitude > maxSlide) break;
                t.position += push;
                moved += push;
            }
            Physics.SyncTransforms();
            return moved.magnitude;
        }
    }
}
