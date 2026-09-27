using UnityEngine;

namespace AirTools.Scene
{
    /// Snap policy against a <see cref="StructureLayer"/> (backend R6 §7.3, measured best in p2-structure-bench §5):
    /// 1. depth gate: a candidate must lie within max(3 cm, 2 % of the hit distance) of the mesh hit, or of the ray in
    ///    front of it (no snapping to corners hidden behind a cabinet);
    /// 2. corner (or edge midpoint) within 2.5 cm of the pointer tip, or 1.5° of the ray;
    /// 3. edge within 2 cm, or 1.2° of the ray (closest point on the segment); a corner beats an in-range edge only if
    ///    d_corner ≤ d_edge + 1 cm;
    /// 4. plane within 3 cm: ray/plane intersection inside the polygon, normal = the plane's;
    /// 5. else the mesh hit.
    /// Hysteresis: the current snap is kept until the pointer leaves 1.5× its acquire tolerance (a higher tier that
    /// comes into range still takes over). Everything is in one frame: the package's local space (the caller converts).
    public static class StructureSnapper
    {
        public struct Settings
        {
            /// Lengths in the query frame's units.
            public float cornerRadius, edgeRadius, planeRadius, cornerBias, gateMin, planeTolerance;
            /// Fraction of the (query-frame) hit distance used as the depth gate when larger than gateMin.
            public float gateFraction;
            public float cornerAngleDeg, edgeAngleDeg, hysteresis;
            public bool midpoints;
            /// Only the plane under the hit (exact point + normal for the level and part seating), no corners or edges.
            public bool planesOnly;
            /// planesOnly hysteresis: a held plane stays while the ray crosses it within this margin of its outline and
            /// isn't hidden behind the scanned surface — a part at a door's edge stays on the door instead of flicking
            /// to whatever is behind it (then back) with every millimetre of hand jitter.
            public float seatMargin;

            /// The measured numbers, in metres; multiply lengths with <see cref="Scaled"/> for other frames.
            public static Settings Default => new Settings
            {
                cornerRadius = 0.025f, edgeRadius = 0.02f, planeRadius = 0.03f, cornerBias = 0.01f, gateMin = 0.03f,
                planeTolerance = 0.01f, gateFraction = 0.02f, cornerAngleDeg = 1.5f, edgeAngleDeg = 1.2f, hysteresis = 1.5f,
                midpoints = true, seatMargin = 0.04f,
            };

            /// Lengths × s (angles and ratios unchanged).
            public Settings Scaled(float s)
            {
                var r = this;
                r.cornerRadius *= s; r.edgeRadius *= s; r.planeRadius *= s; r.cornerBias *= s; r.gateMin *= s; r.planeTolerance *= s;
                r.seatMargin *= s;
                return r;
            }
        }

        public struct Query
        {
            public Vector3 origin, dir;     // the ray (dir unit length)
            public Vector3 meshHit;         // where the ray hits the collision mesh (also the "pointer tip")
            public Vector3 meshNormal;
        }

        public struct Result
        {
            public SnapKind kind;
            public Vector3 point, normal;
            /// Index into Corners (Corner), Edges (Edge, Midpoint) or Planes (Plane); −1 for the mesh.
            public int feature;
            /// Distance used for ranking (lateral to the ray, or to the tip).
            public float distance;
        }

        /// What was snapped last time, for hysteresis (one per pointer).
        public class Memory
        {
            public SnapKind kind = SnapKind.None;
            public int feature = -1;
            public void Clear() { kind = SnapKind.None; feature = -1; }
        }

        static int Tier(SnapKind k) => k switch
        {
            SnapKind.Corner => 3, SnapKind.Midpoint => 3, SnapKind.Edge => 2, SnapKind.Plane => 1, _ => 0,
        };

        public static Result Snap(StructureLayer layer, in Query q, in Settings s, Memory memory = null)
        {
            var mesh = new Result { kind = SnapKind.Face, point = q.meshHit, normal = q.meshNormal, feature = -1 };
            if (layer == null) return mesh;
            float hitDist = Vector3.Distance(q.origin, q.meshHit);
            float gate = Mathf.Max(s.gateMin, s.gateFraction * hitDist);

            var fresh = Best(layer, q, s, gate, 1f, hitDist);
            if (memory != null && memory.kind != SnapKind.None && memory.feature >= 0)
            {
                // The held feature gets 1.5× everything, the depth gate included (a 3.75 cm hold can't live in a 3 cm gate).
                if (TryFeature(layer, q, s, gate * s.hysteresis, s.hysteresis, hitDist, memory.kind, memory.feature, out var kept)
                    && Tier(fresh.kind) <= Tier(kept.kind))
                    fresh = kept;
            }
            if (memory != null)
            {
                memory.kind = fresh.kind == SnapKind.Face ? SnapKind.None : fresh.kind;
                memory.feature = fresh.feature;
            }
            return fresh.kind == SnapKind.None ? mesh : fresh;
        }

        static Result Best(StructureLayer layer, in Query q, in Settings s, float gate, float grow, float hitDist)
        {
            var best = new Result { kind = SnapKind.Face, point = q.meshHit, normal = q.meshNormal, feature = -1 };

            // Corners (and edge midpoints, same tier).
            Result corner = default; corner.kind = SnapKind.None; corner.distance = float.MaxValue;
            Result edge = default; edge.kind = SnapKind.None; edge.distance = float.MaxValue;
            if (!s.planesOnly)
            {
                for (int i = 0; i < layer.Corners.Length; i++)
                    if (PointCandidate(layer.Corners[i].p, q, s.cornerRadius * grow, s.cornerAngleDeg * grow, gate, out float d) && d < corner.distance)
                        corner = new Result { kind = SnapKind.Corner, point = layer.Corners[i].p, feature = i, distance = d };
                if (s.midpoints)
                    for (int i = 0; i < layer.Edges.Length; i++)
                    {
                        if (layer.Edges[i].Length < 4f * s.cornerRadius) continue;
                        if (PointCandidate(layer.Edges[i].Mid, q, s.cornerRadius * grow, s.cornerAngleDeg * grow, gate, out float d) && d < corner.distance)
                            corner = new Result { kind = SnapKind.Midpoint, point = layer.Edges[i].Mid, feature = i, distance = d };
                    }

                // Edges.
                for (int i = 0; i < layer.Edges.Length; i++)
                    if (EdgeCandidate(layer.Edges[i], q, s.edgeRadius * grow, s.edgeAngleDeg * grow, gate, out var p, out float d) && d < edge.distance)
                        edge = new Result { kind = SnapKind.Edge, point = p, feature = i, distance = d };
            }

            if (corner.kind != SnapKind.None && (edge.kind == SnapKind.None || corner.distance <= edge.distance + s.cornerBias))
                best = corner;
            else if (edge.kind != SnapKind.None)
                best = edge;
            else
            {
                // Planes: the intersection nearest the mesh hit, inside the polygon.
                Result plane = default; plane.kind = SnapKind.None; plane.distance = float.MaxValue;
                float radius = Mathf.Max(s.planeRadius * grow, Mathf.Min(gate, s.planeRadius * grow * 2f));
                for (int i = 0; i < layer.Planes.Length; i++)
                    if (PlaneCandidate(layer.Planes[i], q, radius, s.planeTolerance, out var p, out float d) && d < plane.distance)
                        plane = new Result { kind = SnapKind.Plane, point = p, feature = i, distance = d, normal = layer.Planes[i].normal };
                if (plane.kind != SnapKind.None) best = plane;
            }

            if (best.kind != SnapKind.Face) best.normal = NormalFor(layer, best, q);
            return best;
        }

        /// Re-test one feature with grown tolerances (hysteresis).
        static bool TryFeature(StructureLayer layer, in Query q, in Settings s, float gate, float grow, float hitDist, SnapKind kind, int i, out Result r)
        {
            r = default;
            float d; Vector3 p;
            switch (kind)
            {
                case SnapKind.Corner when i < layer.Corners.Length:
                    if (!PointCandidate(layer.Corners[i].p, q, s.cornerRadius * grow, s.cornerAngleDeg * grow, gate, out d)) return false;
                    r = new Result { kind = kind, point = layer.Corners[i].p, feature = i, distance = d };
                    break;
                case SnapKind.Midpoint when i < layer.Edges.Length:
                    if (!PointCandidate(layer.Edges[i].Mid, q, s.cornerRadius * grow, s.cornerAngleDeg * grow, gate, out d)) return false;
                    r = new Result { kind = kind, point = layer.Edges[i].Mid, feature = i, distance = d };
                    break;
                case SnapKind.Edge when i < layer.Edges.Length:
                    if (!EdgeCandidate(layer.Edges[i], q, s.edgeRadius * grow, s.edgeAngleDeg * grow, gate, out p, out d)) return false;
                    r = new Result { kind = kind, point = p, feature = i, distance = d };
                    break;
                case SnapKind.Plane when i < layer.Planes.Length && s.planesOnly:
                    if (!SeatCandidate(layer.Planes[i], q, Mathf.Max(s.planeTolerance * grow, s.seatMargin), gate, hitDist, out p, out d)) return false;
                    r = new Result { kind = kind, point = p, feature = i, distance = d };
                    break;
                case SnapKind.Plane when i < layer.Planes.Length:
                    if (!PlaneCandidate(layer.Planes[i], q, s.planeRadius * grow, s.planeTolerance * grow, out p, out d)) return false;
                    r = new Result { kind = kind, point = p, feature = i, distance = d };
                    break;
                default:
                    return false;
            }
            r.normal = NormalFor(layer, r, q);
            return true;
        }

        // ---------------- candidates ----------------

        /// Depth gate: within `gate` of the mesh hit, or of the ray segment in front of it.
        public static bool PassesGate(Vector3 p, in Query q, float gate)
        {
            if ((p - q.meshHit).sqrMagnitude <= gate * gate) return true;
            var seg = q.meshHit - q.origin;
            float t = Mathf.Clamp01(Vector3.Dot(p - q.origin, seg) / Mathf.Max(seg.sqrMagnitude, 1e-12f));
            return (p - (q.origin + seg * t)).sqrMagnitude <= gate * gate;
        }

        static bool PointCandidate(Vector3 p, in Query q, float radius, float angleDeg, float gate, out float d)
        {
            d = float.MaxValue;
            float along = Vector3.Dot(p - q.origin, q.dir);
            if (along <= 0f) return false;
            float lateral = Vector3.Distance(p, q.origin + q.dir * along);
            float tip = Vector3.Distance(p, q.meshHit);
            bool ok = lateral <= along * Mathf.Tan(angleDeg * Mathf.Deg2Rad) || tip <= radius;
            if (!ok || !PassesGate(p, q, gate)) return false;
            d = Mathf.Min(lateral, tip);
            return true;
        }

        static bool EdgeCandidate(in StructureEdge e, in Query q, float radius, float angleDeg, float gate, out Vector3 point, out float d)
        {
            point = default; d = float.MaxValue;
            // Closest approach between the ray and the segment.
            var ab = e.b - e.a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-12f) return false;
            var w0 = q.origin - e.a;
            float b = Vector3.Dot(q.dir, ab), c = len2, dd = Vector3.Dot(q.dir, w0), ee = Vector3.Dot(ab, w0);
            float denom = c - b * b;   // a = |dir|² = 1
            float s = denom > 1e-9f ? Mathf.Clamp01((b * dd - ee) / denom) : Mathf.Clamp01(-ee / c);
            var onRay = e.a + ab * s;
            float along = Vector3.Dot(onRay - q.origin, q.dir);
            float lateral = along > 0f ? Vector3.Distance(onRay, q.origin + q.dir * along) : float.MaxValue;
            bool rayOk = along > 0f && lateral <= along * Mathf.Tan(angleDeg * Mathf.Deg2Rad);

            // Tip test: the point of the segment closest to the tip.
            float st = Mathf.Clamp01(Vector3.Dot(q.meshHit - e.a, ab) / len2);
            var onTip = e.a + ab * st;
            float tip = Vector3.Distance(onTip, q.meshHit);
            bool tipOk = tip <= radius;

            if (rayOk && (!tipOk || lateral <= tip)) { point = onRay; d = lateral; }
            else if (tipOk) { point = onTip; d = tip; }
            else return false;
            return PassesGate(point, q, gate);
        }

        /// Seating hysteresis: the ray crosses the plane within `margin` of its outline, not farther along the ray than
        /// the mesh hit + gate (nothing scanned is in front of it). d = distance from the mesh hit.
        static bool SeatCandidate(in StructurePlane pl, in Query q, float margin, float gate, float hitDist, out Vector3 point, out float d)
        {
            point = default; d = float.MaxValue;
            float denom = Vector3.Dot(pl.normal, q.dir);
            if (Mathf.Abs(denom) < 1e-4f) return false;
            float t = -(Vector3.Dot(pl.normal, q.origin) + pl.offset) / denom;
            if (t <= 0f || t > hitDist + gate) return false;
            point = q.origin + q.dir * t;
            d = Vector3.Distance(point, q.meshHit);
            return StructureLayer.Contains(pl, pl.ToPlane(point), margin);
        }

        static bool PlaneCandidate(in StructurePlane pl, in Query q, float radius, float tolerance, out Vector3 point, out float d)
        {
            point = default; d = float.MaxValue;
            float denom = Vector3.Dot(pl.normal, q.dir);
            if (Mathf.Abs(denom) < 1e-4f) return false;
            float t = -(Vector3.Dot(pl.normal, q.origin) + pl.offset) / denom;
            if (t <= 0f) return false;
            point = q.origin + q.dir * t;
            d = Vector3.Distance(point, q.meshHit);
            if (d > radius) return false;
            return StructureLayer.Contains(pl, pl.ToPlane(point), tolerance);
        }

        /// Plane normal for plane snaps; for corners/edges the listed plane best aligned with the mesh normal;
        /// always facing the ray origin. Falls back to the mesh normal.
        static Vector3 NormalFor(StructureLayer layer, in Result r, in Query q)
        {
            int[] planes = null;
            if (r.kind == SnapKind.Plane) planes = new[] { r.feature };
            else if (r.kind == SnapKind.Corner) planes = layer.Corners[r.feature].planes;
            else if (r.kind == SnapKind.Edge || r.kind == SnapKind.Midpoint) planes = layer.Edges[r.feature].planes;
            Vector3 n = q.meshNormal;
            if (planes != null && planes.Length > 0)
            {
                float best = -1f;
                foreach (int pi in planes)
                {
                    if (pi < 0 || pi >= layer.Planes.Length) continue;
                    var pn = layer.Planes[pi].normal;
                    float a = r.kind == SnapKind.Plane ? 1f : Mathf.Abs(Vector3.Dot(pn, q.meshNormal));
                    if (a > best) { best = a; n = pn; }
                }
            }
            if (Vector3.Dot(n, q.dir) > 0f) n = -n;
            return n;
        }

        /// The ray hits no mesh: it points just past an outer corner at the edge of a scan, or through a hole in the
        /// decimated collision mesh (the kitchen's has holes on 7 of its 40 door/drawer/panel centres, where the visual
        /// mesh is closed). The nearest plane the ray crosses inside its outline stands in for the missing mesh hit.
        /// Corners / edges within the angular tolerances along the ray win over it; the plane hides the ones behind it
        /// (depth gate) only where the ray crosses it at least gateMin inside its outline: near an outline the fitted
        /// polygon and the real surface disagree by centimetres, and a scan edge is exactly where rays miss the mesh.
        /// With no plane, nothing on the ray can hide a feature (a documented deviation from R6, which assumes a mesh
        /// hit). planesOnly: the plane alone (level, part seating).
        public static bool SnapWithoutHit(StructureLayer layer, Vector3 origin, Vector3 dir, in Settings s, float maxDist, out Result result,
            int keepPlane = -1)
        {
            result = default; result.kind = SnapKind.None; result.feature = -1;
            if (layer == null) return false;
            // Seating hysteresis (planesOnly): the plane the part is on stays while the ray crosses it within the seat margin.
            if (s.planesOnly && keepPlane >= 0 && keepPlane < layer.Planes.Length)
            {
                var kp = layer.Planes[keepPlane];
                float kd = Vector3.Dot(kp.normal, dir);
                if (Mathf.Abs(kd) >= 1e-4f)
                {
                    float kt = -(Vector3.Dot(kp.normal, origin) + kp.offset) / kd;
                    var kpt = origin + dir * kt;
                    if (kt > 0f && kt <= maxDist && StructureLayer.Contains(kp, kp.ToPlane(kpt), Mathf.Max(s.planeTolerance, s.seatMargin)))
                    {
                        result = new Result { kind = SnapKind.Plane, point = kpt, feature = keepPlane, distance = kt, normal = kd > 0f ? -kp.normal : kp.normal };
                        return true;
                    }
                }
            }
            Result plane = default; plane.kind = SnapKind.None; plane.distance = float.MaxValue;
            for (int i = 0; i < layer.Planes.Length; i++)
            {
                var pl = layer.Planes[i];
                float denom = Vector3.Dot(pl.normal, dir);
                if (Mathf.Abs(denom) < 1e-4f) continue;
                float t = -(Vector3.Dot(pl.normal, origin) + pl.offset) / denom;
                if (t <= 0f || t > maxDist || t >= plane.distance) continue;
                var hitPoint = origin + dir * t;
                if (!StructureLayer.Contains(pl, pl.ToPlane(hitPoint), s.planeTolerance)) continue;
                plane = new Result { kind = SnapKind.Plane, point = hitPoint, feature = i, distance = t, normal = denom > 0f ? -pl.normal : pl.normal };
            }
            if (s.planesOnly)
            {
                if (plane.kind == SnapKind.None) return false;
                result = plane;
                return true;
            }
            bool occludes = plane.kind != SnapKind.None
                && StructureLayer.InsideBy(layer.Planes[plane.feature], layer.Planes[plane.feature].ToPlane(plane.point), s.gateMin);
            float reach = occludes ? Mathf.Min(maxDist, plane.distance + Mathf.Max(s.gateMin, s.gateFraction * plane.distance)) : maxDist;
            float tanC = Mathf.Tan(s.cornerAngleDeg * Mathf.Deg2Rad), tanE = Mathf.Tan(s.edgeAngleDeg * Mathf.Deg2Rad);
            Result corner = default; corner.kind = SnapKind.None; corner.distance = float.MaxValue;
            for (int i = 0; i < layer.Corners.Length; i++)
            {
                var p = layer.Corners[i].p;
                float along = Vector3.Dot(p - origin, dir);
                if (along <= 0f || along > reach) continue;
                float lat = Vector3.Distance(p, origin + dir * along);
                if (lat <= along * tanC && lat < corner.distance) corner = new Result { kind = SnapKind.Corner, point = p, feature = i, distance = lat };
            }
            Result edge = default; edge.kind = SnapKind.None; edge.distance = float.MaxValue;
            for (int i = 0; i < layer.Edges.Length; i++)
            {
                var e = layer.Edges[i];
                var ab = e.b - e.a; float len2 = ab.sqrMagnitude;
                if (len2 < 1e-12f) continue;
                var w0 = origin - e.a;
                float b = Vector3.Dot(dir, ab), dd = Vector3.Dot(dir, w0), ee = Vector3.Dot(ab, w0);
                float den = len2 - b * b;
                float t = den > 1e-9f ? Mathf.Clamp01((b * dd - ee) / den) : Mathf.Clamp01(-ee / len2);
                var q = e.a + ab * t;
                float along = Vector3.Dot(q - origin, dir);
                if (along <= 0f || along > reach) continue;
                float lat = Vector3.Distance(q, origin + dir * along);
                if (lat <= along * tanE && lat < edge.distance) edge = new Result { kind = SnapKind.Edge, point = q, feature = i, distance = lat };
            }
            if (corner.kind != SnapKind.None && (edge.kind == SnapKind.None || corner.distance <= edge.distance + s.cornerBias)) result = corner;
            else if (edge.kind != SnapKind.None) result = edge;
            else if (plane.kind != SnapKind.None) { result = plane; return true; }
            else return false;
            // Normal: a listed plane facing the ray, else back along the ray.
            var planes = result.kind == SnapKind.Corner ? layer.Corners[result.feature].planes : layer.Edges[result.feature].planes;
            result.normal = -dir;
            if (planes != null)
                foreach (int pi in planes)
                    if (pi >= 0 && pi < layer.Planes.Length) { var n = layer.Planes[pi].normal; result.normal = Vector3.Dot(n, dir) > 0 ? -n : n; break; }
            return true;
        }

        /// Nearest point on any structure plane to p (projected onto the plane, inside its outline within `tolerance`)
        /// within `radius`: "the surface near my hand" on a scan, without touching the noisy collision mesh. The normal
        /// faces p. `keep` (a plane index or −1): stay on that plane while it's within 1.5× the radius (hysteresis).
        public static bool NearestPlanePoint(StructureLayer layer, Vector3 p, float radius, float tolerance, int keep, out Result result)
        {
            result = default; result.kind = SnapKind.None; result.feature = -1; result.distance = float.MaxValue;
            if (layer == null) return false;
            if (keep >= 0 && keep < layer.Planes.Length && PlanePoint(layer.Planes[keep], p, radius * 1.5f, tolerance, out var kp, out float kd))
            {
                result = new Result { kind = SnapKind.Plane, point = kp, feature = keep, distance = kd, normal = Facing(layer.Planes[keep].normal, p - kp) };
                return true;
            }
            for (int i = 0; i < layer.Planes.Length; i++)
                if (PlanePoint(layer.Planes[i], p, radius, tolerance, out var q, out float d) && d < result.distance)
                    result = new Result { kind = SnapKind.Plane, point = q, feature = i, distance = d, normal = Facing(layer.Planes[i].normal, p - q) };
            return result.kind != SnapKind.None;
        }

        static bool PlanePoint(in StructurePlane pl, Vector3 p, float radius, float tolerance, out Vector3 q, out float d)
        {
            float sd = pl.SignedDistance(p);
            d = Mathf.Abs(sd);
            q = p - pl.normal * sd;
            return d <= radius && StructureLayer.Contains(pl, pl.ToPlane(q), tolerance);
        }

        static Vector3 Facing(Vector3 n, Vector3 toward) => Vector3.Dot(n, toward) < 0f ? -n : n;

        // ---------------- inference while dragging (SketchUp-style) ----------------

        /// If the segment from `start` to `candidate` is within `maxDeg` of a dominant axis or an edge direction
        /// (edges within `edgeSearch` of the start), returns the candidate projected onto that direction.
        public static bool TryAxisLock(StructureLayer layer, Vector3 start, Vector3 candidate, float maxDeg, float edgeSearch,
            out Vector3 locked, out Vector3 direction)
        {
            locked = candidate; direction = default;
            var seg = candidate - start;
            float len = seg.magnitude;
            if (len < 1e-4f) return false;
            var dir = seg / len;
            float bestCos = Mathf.Cos(maxDeg * Mathf.Deg2Rad);
            bool found = false;
            Vector3 best = default;
            void Consider(Vector3 axis)
            {
                if (axis.sqrMagnitude < 1e-8f) return;
                axis.Normalize();
                float c = Mathf.Abs(Vector3.Dot(axis, dir));
                if (c > bestCos) { bestCos = c; best = Vector3.Dot(axis, dir) < 0 ? -axis : axis; found = true; }
            }
            Consider(Vector3.up);
            if (layer != null)
            {
                foreach (var a in layer.Axes) Consider(a);
                foreach (var e in layer.Edges)
                {
                    var ab = e.b - e.a;
                    float t = Mathf.Clamp01(Vector3.Dot(start - e.a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-12f));
                    if (Vector3.Distance(start, e.a + ab * t) <= edgeSearch) Consider(ab);
                }
            }
            if (!found) return false;
            direction = best;
            locked = start + direction * Vector3.Dot(seg, direction);
            return true;
        }
    }
}
