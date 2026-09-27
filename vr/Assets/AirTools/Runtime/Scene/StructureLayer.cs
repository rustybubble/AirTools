using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Scene
{
    /// A fitted plane, bounded by an outline, in Unity package coordinates (the glTF frame with X negated).
    public struct StructurePlane
    {
        public string id, label;
        public Vector3 normal;          // unit
        public float offset;            // normal·x + offset = 0
        public Vector3 origin, u, v;    // in-plane basis; (u, v) coordinates equal the file's (v = −(normal × u) after the X flip)
        public Vector2[] outline;       // outer ring in (u, v)
        public Vector2[][] holes;       // inner rings in (u, v)
        public Vector3[] outline3d;     // the outer ring in 3D, for drawing
        public Vector2 min, max;        // outline AABB in (u, v)
        public float? regularizedDeltaDeg;

        public float SignedDistance(Vector3 p) => Vector3.Dot(normal, p) + offset;
        public Vector2 ToPlane(Vector3 p) { var d = p - origin; return new Vector2(Vector3.Dot(d, u), Vector3.Dot(d, v)); }
        public Vector3 FromPlane(Vector2 q) => origin + u * q.x + v * q.y;
    }

    public struct StructureEdge
    {
        public string id, kind;         // kind: line | crease | boundary
        public Vector3 a, b;
        public int[] planes;            // indices into StructureLayer.Planes
        public Vector3 Mid => (a + b) * 0.5f;
        public float Length => Vector3.Distance(a, b);
    }

    public struct StructureCorner
    {
        public string id, kind;         // kind: 2edge | rect | line_plane | 3plane | endpoint
        public Vector3 p;
        public int[] planes;
    }

    public struct StructureObject
    {
        public string id, label, group;
        public int plane;
        public Vector3[] corners;       // 4 points
        public float widthM, heightM;
    }

    public struct StructureGroup
    {
        public string id, kind;
        public string[] members;
        public float pitchM, widthM, heightM;
    }

    /// structure.r&lt;rev&gt;.json (schema airtools.structure/1, backend docs/research/structure/schema.md), parsed once per
    /// revision into flat arrays in Unity package coordinates. Only the fields the tools need are read; everything else
    /// (confidence, src, params, cost…) is ignored.
    public class StructureLayer
    {
        public const string Schema = "airtools.structure/1";

        public StructurePlane[] Planes = Array.Empty<StructurePlane>();
        public StructureEdge[] Edges = Array.Empty<StructureEdge>();
        public StructureCorner[] Corners = Array.Empty<StructureCorner>();
        public StructureObject[] Objects = Array.Empty<StructureObject>();
        public StructureGroup[] Groups = Array.Empty<StructureGroup>();
        /// Dominant (Manhattan) directions, Unity frame (may be empty).
        public Vector3[] Axes = Array.Empty<Vector3>();
        public string Method;
        public int SkippedPlanes;

        public string Summary => $"{Planes.Length} planes, {Edges.Length} edges, {Corners.Length} corners, {Objects.Length} objects";

        public int PlaneIndex(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < Planes.Length; i++) if (Planes[i].id == id) return i;
            return -1;
        }

        public static StructureLayer Parse(string json)
        {
            var root = JObject.Parse(json);
            string schema = (string)root["schema"];
            if (schema != null && schema != Schema) throw new FormatException($"structure schema '{schema}' is not {Schema}");
            string frame = (string)root["frame"];
            if (frame != null && frame != "scene") throw new FormatException($"structure frame '{frame}' is not 'scene' (bench-only file?)");

            var layer = new StructureLayer { Method = (string)root["method"] };
            if (root["axes"] is JArray axes)
            {
                var list = new List<Vector3>();
                foreach (var a in axes) if (TryVec(a, out var v)) list.Add(GltfFrame.ToUnity(v).normalized);
                layer.Axes = list.ToArray();
            }

            var planes = new List<StructurePlane>();
            var planeIds = new Dictionary<string, int>();
            if (root["planes"] is JArray pa)
                foreach (var p in pa)
                {
                    if (TryPlane(p, out var plane)) { planeIds[plane.id ?? ""] = planes.Count; planes.Add(plane); }
                    else layer.SkippedPlanes++;
                }
            layer.Planes = planes.ToArray();

            int[] PlaneRefs(JToken t)
            {
                if (!(t is JArray arr)) return Array.Empty<int>();
                var r = new List<int>();
                foreach (var x in arr) if (planeIds.TryGetValue((string)x ?? "", out var i)) r.Add(i);
                return r.ToArray();
            }

            var edges = new List<StructureEdge>();
            if (root["edges"] is JArray ea)
                foreach (var e in ea)
                {
                    if (!TryVec(e["a"], out var a) || !TryVec(e["b"], out var b)) continue;
                    edges.Add(new StructureEdge
                    {
                        id = (string)e["id"], kind = (string)e["kind"] ?? "line",
                        a = GltfFrame.ToUnity(a), b = GltfFrame.ToUnity(b), planes = PlaneRefs(e["planes"]),
                    });
                }
            layer.Edges = edges.ToArray();

            var corners = new List<StructureCorner>();
            if (root["corners"] is JArray ca)
                foreach (var c in ca)
                {
                    if (!TryVec(c["p"], out var p)) continue;
                    corners.Add(new StructureCorner
                    {
                        id = (string)c["id"], kind = (string)c["kind"] ?? "",
                        p = GltfFrame.ToUnity(p), planes = PlaneRefs(c["planes"]),
                    });
                }
            layer.Corners = corners.ToArray();

            var objects = new List<StructureObject>();
            if (root["objects"] is JArray oa)
                foreach (var o in oa)
                {
                    int plane = planeIds.TryGetValue((string)o["plane"] ?? "", out var pi) ? pi : -1;
                    Vector3[] pts = null;
                    if (o["corners3d"] is JArray c3 && c3.Count >= 4)
                    {
                        pts = new Vector3[4];
                        for (int i = 0; i < 4; i++) { TryVec(c3[i], out var q); pts[i] = GltfFrame.ToUnity(q); }
                    }
                    else if (o["rect"] is JArray rect && rect.Count >= 4 && plane >= 0)
                    {
                        // rect = [u0, v0, u1, v1] in the plane's own (u, v) frame, which we rebuilt from the file's origin/u.
                        var pl = layer.Planes[plane];
                        float u0 = (float)rect[0], v0 = (float)rect[1], u1 = (float)rect[2], v1 = (float)rect[3];
                        pts = new[] { pl.FromPlane(new Vector2(u0, v0)), pl.FromPlane(new Vector2(u1, v0)), pl.FromPlane(new Vector2(u1, v1)), pl.FromPlane(new Vector2(u0, v1)) };
                    }
                    if (pts == null) continue;
                    objects.Add(new StructureObject
                    {
                        id = (string)o["id"], label = (string)o["label"] ?? "", group = (string)o["group"], plane = plane, corners = pts,
                        widthM = (float?)o["w_m"] ?? Vector3.Distance(pts[0], pts[1]),
                        heightM = (float?)o["h_m"] ?? Vector3.Distance(pts[1], pts[2]),
                    });
                }
            layer.Objects = objects.ToArray();

            var groups = new List<StructureGroup>();
            if (root["groups"] is JArray ga)
                foreach (var g in ga)
                {
                    var members = new List<string>();
                    if (g["members"] is JArray ma) foreach (var m in ma) members.Add((string)m);
                    groups.Add(new StructureGroup
                    {
                        id = (string)g["id"], kind = (string)g["kind"] ?? "", members = members.ToArray(),
                        pitchM = (float?)g["pitch_m"] ?? 0f, widthM = (float?)g["width_m"] ?? 0f, heightM = (float?)g["height_m"] ?? 0f,
                    });
                }
            layer.Groups = groups.ToArray();
            return layer;
        }

        // ---------------- planes ----------------

        static bool TryPlane(JToken p, out StructurePlane plane)
        {
            plane = default;
            if (!TryVec(p["normal"], out var nG) || p["offset"] == null) return false;
            if (nG.sqrMagnitude < 1e-12f) return false;
            float len = nG.magnitude;
            nG /= len;
            float offset = (float)p["offset"] / len;
            var n = GltfFrame.ToUnity(nG);

            // Outline in glTF 3D first (the schema's three forms), then flip.
            var ring = RingToGltf(p["polygon3d"], p, nG) ?? RingToGltf(p["polygon"], p, nG);
            if (ring == null || ring.Count < 3) return false;   // unbounded planes would capture every snap (schema rule)

            var outline3d = new Vector3[ring.Count];
            for (int i = 0; i < ring.Count; i++) outline3d[i] = GltfFrame.ToUnity(ring[i]);

            // Origin: the file's (it satisfies the plane equation), else the ring centroid; basis u from the file or the first side.
            Vector3 origin;
            if (TryVec(p["origin"], out var oG)) origin = GltfFrame.ToUnity(oG);
            else { origin = Vector3.zero; foreach (var q in outline3d) origin += q; origin /= outline3d.Length; }
            origin -= n * (Vector3.Dot(n, origin) + offset);   // exactly on the plane

            Vector3 u = TryVec(p["u"], out var uG) ? GltfFrame.ToUnity(uG) : outline3d[1] - outline3d[0];
            u = Vector3.ProjectOnPlane(u, n);
            if (u.sqrMagnitude < 1e-10f) u = Vector3.ProjectOnPlane(Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right, n);
            u.Normalize();
            // The file's v = n × u (glTF, right-handed). Under the X flip F, F(a × b) = −(Fa × Fb), so in Unity v = −(n × u):
            // plane-local (u, v) coordinates (2D outlines, object rects) then mean exactly what they mean in the file.
            var v = -Vector3.Cross(n, u).normalized;

            plane = new StructurePlane
            {
                id = (string)p["id"], label = (string)p["label"], normal = n, offset = offset,
                origin = origin, u = u, v = v, outline3d = outline3d,
                regularizedDeltaDeg = (float?)p["regularized"]?["delta_deg"],
            };
            plane.outline = new Vector2[outline3d.Length];
            var min = new Vector2(float.MaxValue, float.MaxValue); var max = -min;
            for (int i = 0; i < outline3d.Length; i++)
            {
                var q = plane.ToPlane(outline3d[i]);
                plane.outline[i] = q;
                min = Vector2.Min(min, q); max = Vector2.Max(max, q);
            }
            plane.min = min; plane.max = max;

            var holes = new List<Vector2[]>();
            if (p["holes"] is JArray ha)
                foreach (var h in ha)
                {
                    var hr = RingPoints(h, p, nG);
                    if (hr == null || hr.Count < 3) continue;
                    var hole = new Vector2[hr.Count];
                    for (int i = 0; i < hr.Count; i++) hole[i] = plane.ToPlane(GltfFrame.ToUnity(hr[i]));
                    holes.Add(hole);
                }
            plane.holes = holes.ToArray();
            return true;
        }

        /// A ring in glTF 3D from `polygon3d` or `polygon` (3-number points = 3D; 2-number = (u, v) with origin/u).
        static List<Vector3> RingToGltf(JToken ring, JToken plane, Vector3 nG) => ring is JArray ? RingPoints(ring, plane, nG) : null;

        static List<Vector3> RingPoints(JToken ring, JToken plane, Vector3 nG)
        {
            if (!(ring is JArray arr) || arr.Count == 0) return null;
            var pts = new List<Vector3>(arr.Count);
            bool planar2d = arr[0] is JArray first && first.Count == 2;
            Vector3 o = default, u = default, v = default;
            if (planar2d)
            {
                if (!TryVec(plane["origin"], out o) || !TryVec(plane["u"], out u)) return null;
                u = (u - nG * Vector3.Dot(nG, u)).normalized;
                v = Vector3.Cross(nG, u).normalized;   // schema: v = normal × u, in the file's right-handed frame
            }
            foreach (var t in arr)
            {
                if (!(t is JArray q)) continue;
                if (q.Count >= 3) pts.Add(new Vector3((float)q[0], (float)q[1], (float)q[2]));
                else if (q.Count == 2 && planar2d) pts.Add(o + u * (float)q[0] + v * (float)q[1]);
            }
            return pts;
        }

        static bool TryVec(JToken t, out Vector3 v)
        {
            v = default;
            if (!(t is JArray a) || a.Count < 3) return false;
            v = new Vector3((float)a[0], (float)a[1], (float)a[2]);
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z));
        }

        // ---------------- geometry ----------------

        /// Even-odd point in polygon (outer ring minus holes), in the plane's (u, v).
        public static bool Contains(in StructurePlane plane, Vector2 q, float tolerance = 0f)
        {
            if (q.x < plane.min.x - tolerance || q.y < plane.min.y - tolerance || q.x > plane.max.x + tolerance || q.y > plane.max.y + tolerance) return false;
            if (!InRing(plane.outline, q) && (tolerance <= 0f || DistanceToRing(plane.outline, q) > tolerance)) return false;
            if (plane.holes != null)
                foreach (var h in plane.holes)
                    if (InRing(h, q) && (tolerance <= 0f || DistanceToRing(h, q) > tolerance)) return false;
            return true;
        }

        public static bool InRing(Vector2[] ring, Vector2 q)
        {
            bool inside = false;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                var a = ring[i]; var b = ring[j];
                if ((a.y > q.y) != (b.y > q.y) && q.x < (b.x - a.x) * (q.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        /// True when q is inside the outline (and outside its holes) by at least `margin`.
        public static bool InsideBy(in StructurePlane plane, Vector2 q, float margin)
        {
            if (!InRing(plane.outline, q) || DistanceToRing(plane.outline, q) < margin) return false;
            if (plane.holes != null)
                foreach (var h in plane.holes)
                    if (InRing(h, q) || DistanceToRing(h, q) < margin) return false;
            return true;
        }

        static float DistanceToRing(Vector2[] ring, Vector2 q)
        {
            float best = float.MaxValue;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
            {
                var a = ring[j]; var ab = ring[i] - a;
                float t = Mathf.Clamp01(Vector2.Dot(q - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-12f));
                best = Mathf.Min(best, Vector2.Distance(q, a + ab * t));
            }
            return best;
        }
    }
}
