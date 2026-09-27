using System;
using System.Collections.Generic;
using UnityEngine;

namespace AirTools.Scene
{
    /// The structure layer tools snap to while scene parts are out (SceneParts → SceneRoot.SetEditedStructure): the
    /// published layer (docs/api.md structure.r&lt;rev&gt;.json) minus what belonged to the removed components, plus each
    /// removed component's cavity as planes, edges and corners — the faces live in cavity.r&lt;rev&gt;.glb and in
    /// parts.r&lt;rev&gt;.json's `cavity.box`, not in the structure file. So a two-point tape across an opening snaps to its
    /// jambs (the cavity's front vertical edges) instead of to the removed door's rectangle floating in the gap.
    ///
    /// Taken out, per removed component:
    /// - corners and edges strictly inside its cavity box (by 1 cm; the front face extended 3 cm out for a proud door);
    /// - corners within 1.5 cm of, and edges lying on, its `structure_objects` rectangles (the door, the front panel);
    /// - planes that lie entirely inside the box grown by 2 cm (the door's own plane, a neighbour's side face that the
    ///   cavity's side replaces), and the planes of its structure objects when they don't reach beyond it;
    /// - its structure objects (surveys and labels don't list a door that's gone).
    /// Features on the neighbours (a cabinet front that spans the run, the counter lip) are kept. Plane indices are
    /// remapped. Pure: no engine calls (offline runner); Unity package coordinates throughout.
    public static class ScenePartsLayer
    {
        public const float InsideMargin = 0.01f, FrontReach = 0.03f, ObjectTolerance = 0.015f, PlaneGrow = 0.02f, PlaneFrontGrow = 0.05f;
        public const string CavityLabel = "cavity";

        /// `removed`: each component taken out with its cavity (CavityBox.TryFrom). Null / empty → `baseLayer` itself.
        /// A null base (no structure published) gives a layer of the cavities alone.
        public static StructureLayer Compose(StructureLayer baseLayer, IList<(ScenePartComponent c, CavityBox box)> removed)
        {
            if (removed == null || removed.Count == 0) return baseLayer;
            var b = baseLayer ?? new StructureLayer();

            // The removed components' structure objects (their corners and rectangles).
            var objectIds = new HashSet<string>();
            foreach (var (c, _) in removed)
                if (c?.structure_objects != null) foreach (var id in c.structure_objects) if (!string.IsNullOrEmpty(id)) objectIds.Add(id);
            var removedObjects = new List<StructureObject>();
            var objectPlanes = new HashSet<int>();
            foreach (var o in b.Objects)
                if (o.id != null && objectIds.Contains(o.id) && o.corners != null && o.corners.Length >= 4)
                {
                    removedObjects.Add(o);
                    if (o.plane >= 0) objectPlanes.Add(o.plane);
                }

            bool InsideAny(Vector3 p, float margin, float front)
            {
                foreach (var (_, box) in removed) if (box.Inside(p, margin, front)) return true;
                return false;
            }

            // Planes.
            var planeMap = new int[b.Planes.Length];
            var planes = new List<StructurePlane>(b.Planes.Length + 5 * removed.Count);
            for (int i = 0; i < b.Planes.Length; i++)
            {
                var pl = b.Planes[i];
                bool within = pl.outline3d != null && pl.outline3d.Length > 0;
                if (within) foreach (var q in pl.outline3d) if (!InsideAny(q, -PlaneGrow, PlaneFrontGrow)) { within = false; break; }
                if (!within && objectPlanes.Contains(i) && pl.outline3d != null && pl.outline3d.Length > 0)
                {
                    // A door's plane that reaches a little past the opening (fitted outline slop) still goes.
                    var centroid = Vector3.zero;
                    foreach (var q in pl.outline3d) centroid += q;
                    centroid /= pl.outline3d.Length;
                    within = InsideAny(centroid, -PlaneGrow, PlaneFrontGrow) && AllNearObjects(pl.outline3d, removedObjects, 0.05f);
                }
                if (within) { planeMap[i] = -1; continue; }
                planeMap[i] = planes.Count;
                planes.Add(pl);
            }

            int[] Remap(int[] refs)
            {
                if (refs == null || refs.Length == 0) return refs ?? Array.Empty<int>();
                var list = new List<int>(refs.Length);
                foreach (int r in refs) if (r >= 0 && r < planeMap.Length && planeMap[r] >= 0) list.Add(planeMap[r]);
                return list.ToArray();
            }

            // Corners and edges.
            var corners = new List<StructureCorner>(b.Corners.Length + 8 * removed.Count);
            foreach (var k in b.Corners)
            {
                if (InsideAny(k.p, InsideMargin, FrontReach) || NearObjectCorner(k.p, removedObjects, ObjectTolerance)) continue;
                var kk = k; kk.planes = Remap(k.planes);
                corners.Add(kk);
            }
            var edges = new List<StructureEdge>(b.Edges.Length + 12 * removed.Count);
            foreach (var e in b.Edges)
            {
                if (InsideAny(e.a, InsideMargin, FrontReach) && InsideAny(e.b, InsideMargin, FrontReach)) continue;
                if (OnObject(e.a, removedObjects, ObjectTolerance) && OnObject(e.b, removedObjects, ObjectTolerance)) continue;
                var ee = e; ee.planes = Remap(e.planes);
                edges.Add(ee);
            }

            // Objects and groups.
            var objects = new List<StructureObject>(b.Objects.Length);
            foreach (var o in b.Objects)
            {
                if (o.id != null && objectIds.Contains(o.id)) continue;
                var oo = o; oo.plane = o.plane >= 0 && o.plane < planeMap.Length ? planeMap[o.plane] : -1;
                objects.Add(oo);
            }
            var groups = new List<StructureGroup>(b.Groups.Length);
            foreach (var g in b.Groups)
            {
                var gg = g;
                if (g.members != null && objectIds.Count > 0)
                {
                    var m = new List<string>();
                    foreach (var id in g.members) if (!objectIds.Contains(id)) m.Add(id);
                    gg.members = m.ToArray();
                }
                groups.Add(gg);
            }

            // The cavities.
            var axes = new List<Vector3>(b.Axes);
            foreach (var (c, box) in removed) AddCavity(c, box, planes, edges, corners, axes);

            return new StructureLayer
            {
                Planes = planes.ToArray(), Edges = edges.ToArray(), Corners = corners.ToArray(), Objects = objects.ToArray(),
                Groups = groups.ToArray(), Axes = axes.ToArray(), SkippedPlanes = b.SkippedPlanes,
                Method = string.IsNullOrEmpty(b.Method) ? "parts" : b.Method + "+parts",
            };
        }

        /// A cavity's faces (floor, back, left, right, top unless open-topped), its 12 edges (the 4 around the opening
        /// are "boundary" edges: the jambs, the floor lip, the underside lip; the rest "crease") and 8 corners.
        public static void AddCavity(ScenePartComponent c, CavityBox box, List<StructurePlane> planes, List<StructureEdge> edges,
            List<StructureCorner> corners, List<Vector3> axes)
        {
            string id = c?.id ?? "part";
            var k = new Vector3[8];
            for (int i = 0; i < 8; i++) k[i] = box.Corner(i);
            // Corner bits: 1 = right (max r), 2 = top, 4 = front. Faces as (bit, value) with the normal into the cavity.
            int first = planes.Count;
            var faceIndex = new Dictionary<string, int>();
            void Face(string name, Vector3 normal, int a, int bq, int cq, int d)
            {
                faceIndex[name] = planes.Count;
                planes.Add(Plane($"{id}.{name}", normal, new[] { k[a], k[bq], k[cq], k[d] }));
            }
            Face("floor", box.U, 0, 1, 5, 4);
            Face("back", box.D, 0, 1, 3, 2);
            Face("left", box.R, 0, 4, 6, 2);
            Face("right", -box.R, 1, 5, 7, 3);
            if (!box.OpenTop) Face("top", -box.U, 2, 3, 7, 6);

            int[] Faces(params string[] names)
            {
                var list = new List<int>(names.Length);
                foreach (var n in names) if (faceIndex.TryGetValue(n, out int i)) list.Add(i);
                return list.ToArray();
            }
            string Side(int i, int bit, string lo, string hi) => (i & bit) != 0 ? hi : lo;
            for (int i = 0; i < 8; i++)
            {
                var names = new List<string> { Side(i, 1, "left", "right"), Side(i, 2, "floor", "top") };
                if ((i & 4) == 0) names.Add("back");
                corners.Add(new StructureCorner { id = $"{id}.c{i}", kind = CavityLabel, p = k[i], planes = Faces(names.ToArray()) });
            }
            for (int i = 0; i < 8; i++)
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                int j = i | bit;
                if (j == i) continue;
                // The faces this edge borders: the ones fixed by the other two bits.
                var names = new List<string>();
                foreach (int ob in new[] { 1, 2, 4 })
                {
                    if (ob == bit) continue;
                    if (ob == 1) names.Add(Side(i, 1, "left", "right"));
                    else if (ob == 2) names.Add(Side(i, 2, "floor", "top"));
                    else if ((i & 4) == 0) names.Add("back");
                }
                bool front = bit != 4 && (i & 4) != 0;   // lies in the opening's plane
                edges.Add(new StructureEdge { id = $"{id}.e{i}{j}", kind = front ? "boundary" : "crease", a = k[i], b = k[j], planes = Faces(names.ToArray()) });
            }
            foreach (var axis in new[] { box.R, box.U, box.D })
            {
                bool have = false;
                foreach (var a in axes) if (Mathf.Abs(Vector3.Dot(a, axis)) > 0.999f) { have = true; break; }
                if (!have) axes.Add(axis);
            }
        }

        /// A bounded plane through 4 points (a box face) in the StructureLayer's conventions: origin on the plane, u along
        /// the first side, v = −(n × u) (as parsed files), the outline in (u, v).
        public static StructurePlane Plane(string id, Vector3 normal, Vector3[] ring)
        {
            var n = normal.normalized;
            var origin = Vector3.zero;
            foreach (var q in ring) origin += q;
            origin /= ring.Length;
            var u = Vector3.ProjectOnPlane(ring[1] - ring[0], n);
            if (u.sqrMagnitude < 1e-12f) u = Vector3.ProjectOnPlane(Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right, n);
            u.Normalize();
            var v = -Vector3.Cross(n, u).normalized;
            var pl = new StructurePlane
            {
                id = id, label = CavityLabel, normal = n, offset = -Vector3.Dot(n, origin), origin = origin, u = u, v = v,
                outline3d = (Vector3[])ring.Clone(), holes = Array.Empty<Vector2[]>(),
            };
            pl.outline = new Vector2[ring.Length];
            var min = new Vector2(float.MaxValue, float.MaxValue); var max = -min;
            for (int i = 0; i < ring.Length; i++)
            {
                var q = pl.ToPlane(ring[i]);
                pl.outline[i] = q;
                min = Vector2.Min(min, q); max = Vector2.Max(max, q);
            }
            pl.min = min; pl.max = max;
            return pl;
        }

        static bool NearObjectCorner(Vector3 p, List<StructureObject> objects, float tol)
        {
            foreach (var o in objects)
                foreach (var q in o.corners)
                    if ((q - p).sqrMagnitude <= tol * tol) return true;
            return false;
        }

        /// On (within `tol` of) a removed object's rectangle: close to its plane and inside its outline grown by `tol`.
        static bool OnObject(Vector3 p, List<StructureObject> objects, float tol)
        {
            foreach (var o in objects)
            {
                var c0 = o.corners[0];
                var e1 = o.corners[1] - c0; var e2 = o.corners[3] - c0;
                float l1 = e1.magnitude, l2 = e2.magnitude;
                if (l1 < 1e-5f || l2 < 1e-5f) continue;
                var n = Vector3.Cross(e1, e2);
                if (n.sqrMagnitude < 1e-12f) continue;
                n.Normalize();
                var d = p - c0;
                if (Mathf.Abs(Vector3.Dot(d, n)) > tol) continue;
                float s = Vector3.Dot(d, e1 / l1), t = Vector3.Dot(d, e2 / l2);
                if (s >= -tol && s <= l1 + tol && t >= -tol && t <= l2 + tol) return true;
            }
            return false;
        }

        static bool AllNearObjects(Vector3[] ring, List<StructureObject> objects, float tol)
        {
            foreach (var q in ring) if (!OnObject(q, objects, tol)) return false;
            return true;
        }
    }
}
