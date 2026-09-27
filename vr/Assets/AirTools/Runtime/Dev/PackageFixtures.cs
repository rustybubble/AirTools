using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Dev
{
    /// Builds scene-package files (backend contract, glTF frame) from known geometry, so the whole runtime path —
    /// structure parsing, the X flip, snapping, camera pins — can be tested against exact ground truth:
    /// structure.r&lt;rev&gt;.json from box colliders (every face a plane, every box edge an edge, every box corner a corner,
    /// including the hidden ones behind the wall, which the depth gate must reject) and cameras.r&lt;rev&gt;.json from
    /// Unity cameras (OpenCV R, t and pinhole intrinsics). `scale` shrinks or grows everything (a mis-scaled capture,
    /// to test "set scale from a known dimension").
    public static class PackageFixtures
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static string F(double v) => v.ToString("0.######", C);
        static string V(Vector3 u) { var g = GltfFrame.ToGltf(u); return $"[{F(g.x)},{F(g.y)},{F(g.z)}]"; }

        /// Rectangles with a label, e.g. the window opening, in root space (4 corners in order).
        public struct Rect3 { public string label; public Vector3[] corners; }

        public static string StructureJson(Transform root, float scale = 1f, IList<Rect3> objects = null)
        {
            var planes = new StringBuilder(); var edges = new StringBuilder(); var corners = new StringBuilder();
            int pi = 0, ei = 0, ci = 0;
            foreach (var box in root.GetComponentsInChildren<BoxCollider>())
            {
                var t = box.transform;
                Vector3 P(float sx, float sy, float sz) =>
                    root.InverseTransformPoint(t.TransformPoint(box.center + Vector3.Scale(box.size * 0.5f, new Vector3(sx, sy, sz)))) * scale;
                // 8 corners, index bits: x = 1, y = 2, z = 4.
                var k = new Vector3[8];
                for (int i = 0; i < 8; i++) k[i] = P((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1);
                var centre = (k[0] + k[7]) * 0.5f;
                // 6 faces: (axis, sign) → the 4 corners with that bit set / clear.
                var faceIds = new string[6];
                for (int axis = 0; axis < 3; axis++)
                for (int s = 0; s < 2; s++)
                {
                    int bit = 1 << axis;
                    var quad = new List<Vector3>();
                    foreach (int i in new[] { 0, 1, 3, 2, 0 + 4, 1 + 4, 3 + 4, 2 + 4 })
                        if (((i & bit) != 0) == (s == 1)) quad.Add(k[i]);
                    // Order the 4 points around their centre.
                    var fc = (quad[0] + quad[1] + quad[2] + quad[3]) * 0.25f;
                    var n = (fc - centre).normalized;
                    var u = (quad[1] - quad[0]).normalized; var w = Vector3.Cross(n, u);
                    quad.Sort((a, b) => Mathf.Atan2(Vector3.Dot(a - fc, w), Vector3.Dot(a - fc, u)).CompareTo(Mathf.Atan2(Vector3.Dot(b - fc, w), Vector3.Dot(b - fc, u))));
                    string id = $"p{pi++}";
                    faceIds[axis * 2 + s] = id;
                    var ng = GltfFrame.ToGltf(n);
                    float offset = -Vector3.Dot(n, fc);   // n·x + offset = 0 (the flip keeps it)
                    if (planes.Length > 0) planes.Append(",\n");
                    planes.Append($"    {{\"id\":\"{id}\",\"normal\":[{F(ng.x)},{F(ng.y)},{F(ng.z)}],\"offset\":{F(offset)},\"polygon3d\":[{V(quad[0])},{V(quad[1])},{V(quad[2])},{V(quad[3])}],\"label\":\"{box.name}\",\"confidence\":\"high\"}}");
                }
                string Face(int axis, bool pos) => faceIds[axis * 2 + (pos ? 1 : 0)];
                for (int i = 0; i < 8; i++)
                {
                    if (corners.Length > 0) corners.Append(",\n");
                    corners.Append($"    {{\"id\":\"c{ci++}\",\"p\":{V(k[i])},\"kind\":\"3plane\",\"planes\":[\"{Face(0, (i & 1) != 0)}\",\"{Face(1, (i & 2) != 0)}\",\"{Face(2, (i & 4) != 0)}\"]}}");
                }
                for (int i = 0; i < 8; i++)
                for (int axis = 0; axis < 3; axis++)
                {
                    int j = i | (1 << axis);
                    if (j == i) continue;
                    int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3;
                    if (edges.Length > 0) edges.Append(",\n");
                    edges.Append($"    {{\"id\":\"e{ei++}\",\"a\":{V(k[i])},\"b\":{V(k[j])},\"kind\":\"crease\",\"planes\":[\"{Face(a1, (i & (1 << a1)) != 0)}\",\"{Face(a2, (i & (1 << a2)) != 0)}\"],\"dihedral_deg\":90}}");
                }
            }
            var objs = new StringBuilder();
            if (objects != null)
                for (int i = 0; i < objects.Count; i++)
                {
                    var o = objects[i]; var q = o.corners;
                    // Like S4 rectangles: each object's corners are also snap corners (kind "rect").
                    foreach (var qc in q)
                    {
                        if (corners.Length > 0) corners.Append(",\n");
                        corners.Append($"    {{\"id\":\"c{ci++}\",\"p\":{V(qc * scale)},\"kind\":\"rect\",\"planes\":[]}}");
                    }
                    if (objs.Length > 0) objs.Append(",\n");
                    objs.Append($"    {{\"id\":\"o{i}\",\"label\":\"{o.label}\",\"corners3d\":[{V(q[0] * scale)},{V(q[1] * scale)},{V(q[2] * scale)},{V(q[3] * scale)}]," +
                                $"\"w_m\":{F(Vector3.Distance(q[0], q[1]) * scale)},\"h_m\":{F(Vector3.Distance(q[1], q[2]) * scale)}}}");
                }
            return "{\n  \"schema\":\"airtools.structure/1\",\n  \"frame\":\"scene\",\n  \"method\":\"synthetic-boxes\",\n" +
                   "  \"axes\":[[1,0,0],[0,1,0],[0,0,1]],\n" +
                   $"  \"planes\":[\n{planes}\n  ],\n  \"edges\":[\n{edges}\n  ],\n  \"corners\":[\n{corners}\n  ],\n  \"objects\":[\n{objs}\n  ],\n  \"groups\":[]\n}}\n";
        }

        /// cameras.r&lt;rev&gt;.json: world-to-camera R (OpenCV axes) and t in the glTF frame, pinhole intrinsics for w×h.
        public static string CamerasJson(IReadOnlyList<SceneCameraInfo> cams, int w, int h, float scale = 1f)
        {
            var sb = new StringBuilder("[\n");
            for (int i = 0; i < cams.Count; i++)
            {
                var c = cams[i];
                double f = h * 0.5 / Mathf.Tan(c.verticalFovDeg * 0.5f * Mathf.Deg2Rad);
                // Rows of R = the camera's OpenCV axes (x right, y down, z forward) in glTF world coordinates.
                var xr = GltfFrame.ToGltf(c.rotation * Vector3.right);
                var yd = GltfFrame.ToGltf(c.rotation * Vector3.down);
                var zf = GltfFrame.ToGltf(c.rotation * Vector3.forward);
                var pos = GltfFrame.ToGltf(c.position * scale);
                var t = new Vector3(-Vector3.Dot(xr, pos), -Vector3.Dot(yd, pos), -Vector3.Dot(zf, pos));
                string id = c.id.ToString("0000", C);
                sb.Append($"  {{\"id\":\"{id}\",\"file\":\"{id}.jpg\",\"thumb\":\"thumbs/{id}.jpg\"," +
                          $"\"R\":[[{F(xr.x)},{F(xr.y)},{F(xr.z)}],[{F(yd.x)},{F(yd.y)},{F(yd.z)}],[{F(zf.x)},{F(zf.y)},{F(zf.z)}]]," +
                          $"\"t\":[{F(t.x)},{F(t.y)},{F(t.z)}],\"position\":[{F(pos.x)},{F(pos.y)},{F(pos.z)}]," +
                          $"\"fx\":{F(f)},\"fy\":{F(f)},\"cx\":{F(w * 0.5)},\"cy\":{F(h * 0.5)},\"w\":{w},\"h\":{h}}}");
                sb.Append(i < cams.Count - 1 ? ",\n" : "\n");
            }
            return sb.Append("]\n").ToString();
        }

        /// scene.json for a synthetic package (revision-named layout, glTF frame). `parts`: the scene parts entry's JSON
        /// (ScenePartsFixtures.PartsEntry), or null for a package without removable components.
        public static string SceneJson(string name, int revision, string quality, Vector3 spawnEye, Vector3 look, bool structure,
            int planes = 0, int edges = 0, int corners = 0, int objects = 0, string scaleMethod = "known_dimension", double scaleResidual = 0.0,
            string parts = null)
        {
            string s = structure
                ? $"{{\"file\":\"structure.r{revision}.json\",\"schema\":\"airtools.structure/1\",\"edges\":{edges},\"corners\":{corners},\"planes\":{planes},\"objects\":{objects}}}"
                : "null";
            return "{\n" +
                   $"  \"name\": \"{name}\",\n  \"units\": \"meters\",\n  \"up\": [0, 1, 0],\n  \"north\": null,\n" +
                   $"  \"scale_method\": \"{scaleMethod}\",\n  \"scale_residual_m\": {F(scaleResidual)},\n  \"scale_candidates\": [],\n" +
                   "  \"gravity_residual_deg\": 0.0,\n  \"origin_gps\": null,\n" +
                   $"  \"recommended_spawn\": {{\"pos\": {V(spawnEye)}, \"look\": {V(look)}}},\n" +
                   $"  \"mesh\": {{\"file\": \"mesh.r{revision}.glb\", \"triangles\": 0, \"texture_px\": 0}},\n" +
                   $"  \"collision\": {{\"file\": \"collision.r{revision}.glb\", \"triangles\": 0}},\n" +
                   $"  \"cameras\": \"cameras.r{revision}.json\",\n  \"structure\": {s},\n" +
                   (parts != null ? $"  \"parts\": {parts},\n" : "") +
                   $"  \"quality\": \"{quality}\",\n  \"revision\": {revision},\n" +
                   $"  \"frame\": {{\"id\": \"{name}-synthetic\", \"aligned_to_preview\": true, \"alignment_residual_m\": 0.0}},\n" +
                   "  \"pipeline_version\": \"synthetic\"\n}\n";
        }

        /// The window opening and the door as labelled rectangles (facade root space).
        public static List<Rect3> FacadeObjects()
        {
            float hw = SyntheticFacadeSpec.WindowWidth / 2f, y0 = SyntheticFacadeSpec.SillHeight, y1 = y0 + SyntheticFacadeSpec.WindowHeight;
            float dx = SyntheticFacadeSpec.DoorCentreX, dw = SyntheticFacadeSpec.DoorWidth / 2f;
            return new List<Rect3>
            {
                new Rect3 { label = "window", corners = new[] { new Vector3(-hw, y0, 0), new Vector3(hw, y0, 0), new Vector3(hw, y1, 0), new Vector3(-hw, y1, 0) } },
                new Rect3 { label = "door", corners = new[] { new Vector3(dx - dw, 0, 0), new Vector3(dx + dw, 0, 0), new Vector3(dx + dw, SyntheticFacadeSpec.DoorHeight, 0), new Vector3(dx - dw, SyntheticFacadeSpec.DoorHeight, 0) } },
            };
        }
    }
}
