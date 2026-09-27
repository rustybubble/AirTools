using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Dev
{
    /// Scene parts for the built-in facade (backend docs/api.md "parts.r&lt;rev&gt;.json"), so remove → measure → replace can
    /// be tested end to end before the real kitchen files arrive: a kitchen-like run in front of the wall, under the
    /// window — a fixed cabinet on the left (x −0.8…0.1), the removable base cabinet `cab1` (x 0.1…0.7, the 600 mm
    /// opening a dishwasher takes) and the removable wide cabinet `cab2` (x 0.7…1.6), all 0.82 m tall and 0.58 m deep
    /// against the wall, under a counter (0.82…0.86 m, 0.62 m deep). Each removable box leaves a cavity bounded by the
    /// ground, the wall, its neighbours' sides and the counter's underside: exact ground truth (cab1 600 × 820 × 580 mm).
    /// Facade root space, Unity coordinates, metres; the files are written in the glTF frame (X negated). Used by the
    /// AirTools ▸ Export Synthetic Scene Packages exporter (site synthetic-facade-parts), the EditMode tests and the
    /// harness (the tape scenarios). No engine calls apart from Bounds / Vector3 maths.
    public static class ScenePartsFixtures
    {
        public const string Site = "synthetic-facade-parts";
        public const float Front = 0.58f, Top = 0.82f, CounterTop = 0.86f, CounterFront = 0.62f;
        public const float LeftEnd = -0.8f, Cab1Left = 0.1f, Cab1Right = 0.7f, RightEnd = 1.6f;
        /// The door rectangles sit 1 cm in from each box's edges (their structure objects: the ghost corners the tape
        /// must not find once the box is out).
        public const float DoorInset = 0.01f;

        /// One box of the run. `removable`: a scene part (node part_<id>, cavity cavity_<id>).
        public class RunBox
        {
            public string id, label, kind, name;
            public Bounds box;
            public bool removable;
            /// Its structure object's id in structure.r&lt;rev&gt;.json (PackageFixtures numbers objects in list order).
            public string objectId;
            public Vector3[] Door => new[]
            {
                new Vector3(box.min.x + DoorInset, box.min.y + 2 * DoorInset, box.max.z), new Vector3(box.max.x - DoorInset, box.min.y + 2 * DoorInset, box.max.z),
                new Vector3(box.max.x - DoorInset, box.max.y - 2 * DoorInset, box.max.z), new Vector3(box.min.x + DoorInset, box.max.y - 2 * DoorInset, box.max.z),
            };
        }

        static Bounds MinMax(Vector3 min, Vector3 max) { var b = new Bounds(); b.SetMinMax(min, max); return b; }

        /// The run's boxes, left to right (the counter last). Object ids follow PackageFixtures.FacadeObjects (o0 window,
        /// o1 door): o2 left cabinet, o3 cab1, o4 cab2.
        public static List<RunBox> Run() => new List<RunBox>
        {
            new RunBox { id = "left", name = "LeftCabinet", label = "cabinet", kind = "cabinet", box = MinMax(new Vector3(LeftEnd, 0, 0), new Vector3(Cab1Left, Top, Front)), objectId = "o2" },
            new RunBox { id = "cab1", name = "part_cab1", label = "base cabinet", kind = "cabinet", removable = true, box = MinMax(new Vector3(Cab1Left, 0, 0), new Vector3(Cab1Right, Top, Front)), objectId = "o3" },
            new RunBox { id = "cab2", name = "part_cab2", label = "wide cabinet", kind = "cabinet", removable = true, box = MinMax(new Vector3(Cab1Right, 0, 0), new Vector3(RightEnd, Top, Front)), objectId = "o4" },
            new RunBox { id = "counter", name = "Counter", label = "counter", kind = "counter", box = MinMax(new Vector3(LeftEnd, Top, 0), new Vector3(RightEnd, CounterTop, CounterFront)) },
        };

        /// The facade's labelled rectangles plus the run's doors (structure objects o2…o4).
        public static List<PackageFixtures.Rect3> Objects()
        {
            var list = PackageFixtures.FacadeObjects();
            foreach (var b in Run())
                if (b.objectId != null) list.Add(new PackageFixtures.Rect3 { label = "cabinet_door", corners = b.Door });
            return list;
        }

        // ---------------- parts.r<rev>.json ----------------

        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static string F(double v) => v.ToString("0.######", C);
        static string G(Vector3 u) { var g = GltfFrame.ToGltf(u); return $"[{F(g.x)},{F(g.y)},{F(g.z)}]"; }

        /// The scene.json entry for the parts files of `revision`.
        public static string PartsEntry(int revision, int components) =>
            $"{{\"file\": \"parts.r{revision}.json\", \"schema\": \"{ScenePartsDoc.Schema}\", \"components\": {components}, " +
            $"\"mesh\": \"mesh.parts.r{revision}.glb\", \"cavities\": \"cavity.r{revision}.glb\", \"collision\": \"collision.parts.r{revision}.glb\"}}";

        /// parts.r&lt;rev&gt;.json in the documented layout (glTF frame; axes r = +X, up = +Y, d = +Z: the wall faces +Z, and
        /// X is negated, so a box's r range is its negated x range), scaled by `scale` like the rest of the package.
        public static string PartsJson(int revision = 1, float scale = 1f)
        {
            var sb = new StringBuilder();
            sb.Append("{\n \"schema\": \"").Append(ScenePartsDoc.Schema).Append("\", \"frame\": \"scene\", \"revision\": ").Append(revision)
              .Append(", \"method\": \"synthetic-boxes\",\n")
              .Append($" \"mesh\": \"mesh.parts.r{revision}.glb\", \"cavities\": \"cavity.r{revision}.glb\", \"collision\": \"collision.parts.r{revision}.glb\",\n")
              .Append(" \"scale\": {\"method\": \"known_dimension\", \"residual_m\": 0.0, \"note\": \"synthetic: every size is exact\"},\n")
              .Append(" \"components\": [");
            bool first = true;
            foreach (var b in Run())
            {
                if (!b.removable) continue;
                var min = b.box.min * scale; var max = b.box.max * scale;
                float rl = -max.x, rr = -min.x;
                sb.Append(first ? "\n" : ",\n");
                first = false;
                sb.Append($"  {{\"id\": \"{b.id}\", \"label\": \"{b.label}\", \"class\": \"{b.kind}\", \"removable\": true, \"label_source\": \"synthetic\",\n");
                sb.Append($"   \"node\": \"part_{b.id}\", \"collision_node\": \"part_{b.id}\", \"faces\": 12,\n");
                sb.Append($"   \"bbox\": {{\"min\": [{F(rl)},{F(min.y)},{F(min.z)}], \"max\": [{F(rr)},{F(max.y)},{F(max.z)}]}},\n");
                sb.Append($"   \"structure_objects\": [\"{b.objectId}\"],\n");
                sb.Append($"   \"cavity\": {{\"node\": \"cavity_{b.id}\", \"estimated\": true, \"fill\": \"flat\",\n");
                sb.Append($"    \"size_m\": {{\"w\": {F(max.x - min.x)}, \"h\": {F(max.y - min.y)}, \"d\": {F(max.z - min.z)}}},\n");
                sb.Append("    \"sigma_m\": {\"w\": 0.0, \"h\": 0.0, \"d\": 0.0},\n");
                sb.Append($"    \"insert\": {{\"p\": {G(new Vector3((min.x + max.x) * 0.5f, min.y, max.z))}, \"axes\": [[1,0,0],[0,1,0],[0,0,1]]}},\n");
                sb.Append($"    \"box\": {{\"min_rud\": [{F(rl)},{F(min.y)},{F(min.z)}], \"max_rud\": [{F(rr)},{F(max.y)},{F(max.z)}]}},\n");
                sb.Append($"    \"bounds\": {{\"left\": {{\"value\": {F(rl)}, \"sigma\": 0.0, \"observed\": false}}, \"right\": {{\"value\": {F(rr)}, \"sigma\": 0.0, \"observed\": false}}, " +
                          $"\"top\": {{\"value\": {F(max.y)}, \"sigma\": 0.0, \"observed\": false}}, \"floor\": {{\"value\": {F(min.y)}, \"sigma\": 0.0, \"observed\": false}}, " +
                          $"\"back\": {{\"value\": {F(min.z)}, \"sigma\": 0.0, \"observed\": false}}, \"front\": {{\"value\": {F(max.z)}, \"sigma\": 0.0, \"observed\": true}}}},\n");
                sb.Append("    \"planes\": []}}");
            }
            sb.Append("\n ]\n}\n");
            return sb.ToString();
        }

        // ---------------- cavity.r<rev>.glb ----------------

        /// sRGB (0–1) → linear, the backend's `linear()`: glTF COLOR_0 is linear.
        public static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

        public static Color Linear(byte r, byte g, byte b) => new Color(SrgbToLinear(r / 255f), SrgbToLinear(g / 255f), SrgbToLinear(b / 255f), 1f);

        /// Flat fills "sampled from the neighbours" (here: the ground, the brick, the cabinets, the counter in shadow).
        public static readonly Color FloorColor = Linear(118, 112, 104), BackColor = Linear(150, 84, 62), SideColor = Linear(196, 176, 150), TopColor = Linear(120, 116, 110);

        /// A cavity mesh like the backend's cavity_mesh: floor, back, left, right, top as flat-coloured quads, both
        /// windings, one colour per face (linear). Facade root space (Unity), × `scale`.
        public static void CavityMesh(RunBox b, float scale, List<Vector3> vertices, List<Color> colors, List<int> triangles)
        {
            vertices.Clear(); colors.Clear(); triangles.Clear();
            var mn = b.box.min * scale; var mx = b.box.max * scale;
            Vector3 P(int i) => new Vector3((i & 1) != 0 ? mx.x : mn.x, (i & 2) != 0 ? mx.y : mn.y, (i & 4) != 0 ? mx.z : mn.z);
            void Quad(int a, int bq, int c, int d, Color col)
            {
                int s = vertices.Count;
                foreach (var i in new[] { a, bq, c, d }) { vertices.Add(P(i)); colors.Add(col); }
                triangles.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3, s, s + 2, s + 1, s, s + 3, s + 2 });
            }
            Quad(0, 1, 5, 4, FloorColor);   // y min
            Quad(0, 1, 3, 2, BackColor);    // z min (the wall)
            Quad(0, 4, 6, 2, SideColor);    // x min
            Quad(1, 5, 7, 3, SideColor);    // x max
            Quad(2, 3, 7, 6, TopColor);     // y max (the counter's underside)
        }

        // ---------------- tape scenarios (the real measure tool) ----------------

        /// Width: from 0.9 m in front, a jamb each side (a point on each side face 4 mm behind the opening, ±3 mm hand
        /// noise off the face) → the opening's width. `box` in package space, `toRoot` = SceneRoot.PackageToRoot,
        /// `expect` in scene metres (the file's W × the calibration).
        public static MeasureScenario Width(string id, CavityBox box, Func<Vector3, Vector3> toRoot, float expect, float noise = 0.003f)
        {
            float mid = (box.Min.x + box.Max.x) * 0.5f, h = box.Max.y - box.Min.y;
            float up = Mathf.Min(box.Min.y + 0.45f * h + 0.35f, box.Max.y - 0.1f);
            var eye = toRoot(box.Point(mid, up, box.Max.z + 0.9f));
            var y = box.Min.y + 0.45f * h;
            return new MeasureScenario
            {
                Id = $"parts.{id}.tape.width",
                Description = "A jamb each side from 0.9 m in front → the opening's width",
                RayOrigin = eye,
                Targets = new[] { toRoot(box.Point(box.Min.x, y, box.Max.z - 0.004f)), toRoot(box.Point(box.Max.x, y, box.Max.z - 0.004f)) },
                Noise = (r, i) => (toRoot(box.U * U(r, noise) + box.D * U(r, noise)) - toRoot(Vector3.zero)),
                ExpectDistance = expect, DistanceTol = 0.005,
            };
        }

        /// Depth: the crease where the floor meets the back wall, then the floor's front lip → front to back.
        public static MeasureScenario Depth(string id, CavityBox box, Func<Vector3, Vector3> toRoot, float expect, float noise = 0.003f)
        {
            float mid = (box.Min.x + box.Max.x) * 0.5f, h = box.Max.y - box.Min.y;
            var eye = toRoot(box.Point(mid, Mathf.Min(box.Min.y + 0.45f * h + 0.35f, box.Max.y - 0.1f), box.Max.z + 0.9f));
            return new MeasureScenario
            {
                Id = $"parts.{id}.tape.depth",
                Description = "Floor × back wall, then the floor's front lip → the depth",
                RayOrigin = eye,
                Targets = new[] { toRoot(box.Point(mid, box.Min.y + 0.004f, box.Min.z + 0.004f)), toRoot(box.Point(mid, box.Min.y + 0.001f, box.Max.z - 0.004f)) },
                Noise = (r, i) => (toRoot(box.R * U(r, noise)) - toRoot(Vector3.zero)),
                ExpectDistance = expect, DistanceTol = 0.005,
            };
        }

        /// Height (closed tops only): from low in front, the floor's front lip, then the underside's front lip.
        public static MeasureScenario Height(string id, CavityBox box, Func<Vector3, Vector3> toRoot, float expect, float noise = 0.003f)
        {
            float mid = (box.Min.x + box.Max.x) * 0.5f, h = box.Max.y - box.Min.y;
            var eye = toRoot(box.Point(mid, box.Min.y + 0.4f * h, box.Max.z + 0.6f));
            return new MeasureScenario
            {
                Id = $"parts.{id}.tape.height",
                Description = "The floor's front lip, then the underside's front lip → floor to underside",
                RayOrigin = eye,
                Targets = new[] { toRoot(box.Point(mid, box.Min.y + 0.002f, box.Max.z - 0.006f)), toRoot(box.Point(mid, box.Max.y - 0.003f, box.Max.z - 0.006f)) },
                Noise = (r, i) => (toRoot(box.R * U(r, noise)) - toRoot(Vector3.zero)),
                ExpectDistance = expect, DistanceTol = 0.005,
            };
        }

        static float U(System.Random r, float a) => (float)(r.NextDouble() * 2 - 1) * a;
    }
}
