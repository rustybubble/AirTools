using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AirTools.Scene
{
    /// parts.r&lt;rev&gt;.json, schema airtools.parts/1 (backend docs/api.md "parts.r&lt;rev&gt;.json: removable components and their
    /// cavities"; research docs/research/p1-parts/e1-remove-dishwasher.md): the components the user can take out of a
    /// scene, the node names of each in the split mesh / collision / cavity GLBs, and the box each one leaves behind.
    /// Same frame as the mesh (glTF: metres, right-handed, +Y up); Unity coordinates come from CavityBox (X flip).
    /// Unknown fields (evidence, cut, colors, cost, …) are ignored; missing optional ones stay null. Pure: no engine
    /// calls, so it runs in the offline test runner.
    public class ScenePartsDoc
    {
        public const string Schema = "airtools.parts/1";

        public string schema;
        public string frame;
        public int revision;
        public string method;
        public string mesh;
        public string cavities;
        public string collision;
        public ScenePartsScale scale;
        public List<ScenePartComponent> components = new List<ScenePartComponent>();

        static JsonSerializer Serializer() => JsonSerializer.Create(new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            // A field whose type changed (e.g. "removable": "yes") loses that field, not the whole file. Safe here: the
            // text was already parsed into a token tree, so a skipped value can't desynchronise a reader.
            Error = (sender, args) => { if (args.ErrorContext.Member != null) args.ErrorContext.Handled = true; },
        });

        /// Throws FormatException when the text isn't an airtools.parts/1 document in the scene frame (the caller then
        /// loads the scene in one piece, as without parts). Components without an id are dropped; a repeated id keeps
        /// its first entry.
        public static ScenePartsDoc Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("empty parts file");
            JObject root;
            try { root = JObject.Parse(json, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore }); }
            catch (JsonException ex) { throw new FormatException($"parts file is not a JSON object: {ex.Message}"); }
            ScenePartsDoc doc;
            try { doc = root.ToObject<ScenePartsDoc>(Serializer()); }
            catch (JsonException ex) { throw new FormatException($"parts file unreadable: {ex.Message}"); }
            if (doc == null) throw new FormatException("empty parts file");
            if (!string.IsNullOrEmpty(doc.schema) && !IsSchema1(doc.schema))
                throw new FormatException($"parts schema '{doc.schema}' is not {Schema}");
            if (!string.IsNullOrEmpty(doc.frame) && doc.frame != "scene")
                throw new FormatException($"parts frame '{doc.frame}' is not 'scene'");
            var seen = new HashSet<string>();
            var list = new List<ScenePartComponent>();
            foreach (var c in doc.components ?? new List<ScenePartComponent>())
            {
                if (c == null || string.IsNullOrEmpty(c.id) || !seen.Add(c.id)) continue;
                c.structure_objects ??= new List<string>();
                list.Add(c);
            }
            doc.components = list;
            return doc;
        }

        /// "airtools.parts/1" and minor revisions of it ("airtools.parts/1.1").
        public static bool IsSchema1(string schema) =>
            schema == Schema || (schema != null && schema.StartsWith(Schema + ".", StringComparison.Ordinal));

        /// The components the user may take out, in file order.
        public List<ScenePartComponent> Removable
        {
            get
            {
                var list = new List<ScenePartComponent>();
                foreach (var c in components) if (c.removable) list.Add(c);
                return list;
            }
        }

        /// By id ("dw1"), else by label or class ("dishwasher", "sink cabinet" / "sink_cabinet"), case-insensitive.
        public ScenePartComponent Find(string idOrLabel)
        {
            if (string.IsNullOrWhiteSpace(idOrLabel)) return null;
            string q = idOrLabel.Trim();
            foreach (var c in components) if (string.Equals(c.id, q, StringComparison.OrdinalIgnoreCase)) return c;
            string n = Normalise(q);
            foreach (var c in components) if (Normalise(c.label) == n || Normalise(c.kind) == n) return c;
            foreach (var c in components) if (!string.IsNullOrEmpty(c.label) && Normalise(c.label).Contains(n)) return c;
            return null;
        }

        static string Normalise(string s) => (s ?? "").Trim().Replace('_', ' ').ToLowerInvariant();
    }

    /// Which files a parts package loads: scene.json's `parts` entry names them, else parts.r&lt;rev&gt;.json does. Mesh is
    /// required (null = load in one piece); Collision is null when the package has no collision mesh at all (the split
    /// mesh then doubles as the collider, as the one-piece mesh does); Cavities may be null (parts come out, no box shows).
    public struct ScenePartsFiles
    {
        public string Mesh, Collision, Cavities;

        public static ScenePartsFiles For(SceneManifest m, ScenePartsDoc doc)
        {
            string Pick(string a, string b) => !string.IsNullOrEmpty(a) ? a : !string.IsNullOrEmpty(b) ? b : null;
            var f = new ScenePartsFiles
            {
                Mesh = Pick(m?.parts?.mesh, doc?.mesh),
                Collision = Pick(m?.parts?.collision, doc?.collision),
                Cavities = Pick(m?.parts?.cavities, doc?.cavities),
            };
            // No split collision named but a one-piece collision exists: hiding a part could not hide its collider, so
            // the tape would stop on a ghost. The split mesh doubles as the collider instead.
            return f;
        }

        public override string ToString() => $"mesh={Mesh ?? "-"} collision={Collision ?? "(the split mesh)"} cavities={Cavities ?? "-"}";
    }

    public class ScenePartsScale
    {
        public string method;
        public double? residual_m;
        public string note;
    }

    /// One `components[]` entry.
    public class ScenePartComponent
    {
        public string id;
        public string label;
        /// `class` in the file (appliance, cabinet, …).
        [JsonProperty("class")] public string kind;
        public bool removable = true;
        public string label_source;
        public string node;
        public string collision_node;
        public int faces;
        public ScenePartsBBox bbox;
        public JToken obb;
        public List<string> structure_objects = new List<string>();
        public ScenePartCavity cavity;

        /// Node names in the split mesh, the split collision and the cavity GLB (the file's, else the documented
        /// `part_<id>` / `cavity_<id>`).
        public string VisualNode => !string.IsNullOrEmpty(node) ? node : $"part_{id}";
        public string CollisionNode => !string.IsNullOrEmpty(collision_node) ? collision_node : VisualNode;
        public string CavityNode => !string.IsNullOrEmpty(cavity?.node) ? cavity.node : $"cavity_{id}";

        /// What a chip or a sentence calls it: the file's label, capitalised ("sink cabinet" → "Sink cabinet").
        public string DisplayName
        {
            get
            {
                string s = !string.IsNullOrEmpty(label) ? label : !string.IsNullOrEmpty(kind) ? kind : id ?? "part";
                s = s.Replace('_', ' ').Trim();
                return s.Length == 0 ? "Part" : char.ToUpperInvariant(s[0]) + s.Substring(1);
            }
        }

        public override string ToString() => $"{id} {label}";
    }

    public class ScenePartsBBox
    {
        public double[] min;
        public double[] max;
    }

    /// `cavity`: the box a component leaves behind. Always estimated (flat colours sampled from the neighbours).
    public class ScenePartCavity
    {
        public string node;
        public bool estimated = true;
        public string fill;
        /// Opening width, floor to underside, front to back wall, in scene metres.
        public ScenePartsSize size_m;
        /// Geometric σ per dimension (the scene's scale error comes on top).
        public ScenePartsSize sigma_m;
        public ScenePartsInsert insert;
        public ScenePartsRudBox box;
        /// Which structure planes / edges bound each side (`observed: false` = extended behind the part).
        public JObject bounds;
        public JToken planes;

        /// The top bound came from the component itself, not a cap above it (a freestanding range): the opening is
        /// open-topped and has no top face (the backend's cavity_mesh drops it the same way).
        public bool OpenTop => bounds?["top"] is JObject top && top["source"] != null && top["source"].Type != JTokenType.Null;
    }

    public class ScenePartsSize
    {
        public double w, h, d;
    }

    /// `insert`: p = floor, front, centre of the opening; axes = rows r (right), up, d (out of the opening, toward the
    /// viewer). A replacement's front-bottom-centre goes at p.
    public class ScenePartsInsert
    {
        public double[] p;
        public double[][] axes;
    }

    /// `box`: min_rud / max_rud in the axes frame.
    public class ScenePartsRudBox
    {
        public double[] min_rud;
        public double[] max_rud;
    }

    /// A component's cavity in Unity package coordinates (the glTF frame with X negated, like the mesh, the structure
    /// layer and the cameras). R, U, D are unit vectors (right as the viewer facing the opening sees it, up, out of the
    /// opening); a point's rud coordinates are its dot products with them (the backend's Frame.to), so Min / Max are
    /// absolute along each axis. glTF dot products survive the flip: r·p = F(r)·F(p).
    public struct CavityBox
    {
        public Vector3 P, R, U, D;
        public Vector3 Min, Max;
        /// The file's size_m (what labels show), else the box's extents; metres (scene units).
        public Vector3 Size;
        public Vector3 Sigma;
        public bool OpenTop;
        /// The file gave `box` relative to insert.p rather than absolute (tolerated; see TryFrom).
        public bool BoxWasRelative;

        public float Width => Size.x;
        public float Height => Size.y;
        public float Depth => Size.z;

        public Vector3 Point(float r, float u, float d) => R * r + U * u + D * d;
        public Vector3 Point(Vector3 rud) => Point(rud.x, rud.y, rud.z);
        public Vector3 Rud(Vector3 p) => new Vector3(Vector3.Dot(p, R), Vector3.Dot(p, U), Vector3.Dot(p, D));

        /// Corner i: bit 1 = right side (max r), bit 2 = top, bit 4 = front.
        public Vector3 Corner(int i) => Point((i & 1) != 0 ? Max.x : Min.x, (i & 2) != 0 ? Max.y : Min.y, (i & 4) != 0 ? Max.z : Min.z);
        public Vector3 Centre => Point((Min + Max) * 0.5f);
        /// The middle of the opening's front plane.
        public Vector3 FrontCentre => Point((Min.x + Max.x) * 0.5f, (Min.y + Max.y) * 0.5f, Max.z);

        /// Strictly inside the box by `margin` on every side, with the front face extended outward by `front` (a door
        /// that stands proud of the opening's plane).
        public bool Inside(Vector3 p, float margin, float front)
        {
            var q = Rud(p);
            return q.x > Min.x + margin && q.x < Max.x - margin && q.y > Min.y + margin && q.y < Max.y - margin
                   && q.z > Min.z + margin && q.z < Max.z + front;
        }

        /// The cavity of `c` in Unity package space. Tolerant: no axes → the glTF identity frame (r +X, up +Y, d +Z);
        /// no box → from insert.p and size_m; a box given relative to insert.p is detected (insert.p's own rud coordinates
        /// sit at the box's front-bottom-centre when the box is absolute, at the origin when it is relative).
        public static bool TryFrom(ScenePartComponent c, out CavityBox box, out string why)
        {
            box = default;
            why = null;
            var cav = c?.cavity;
            if (cav == null) { why = "no cavity"; return false; }
            var ins = cav.insert;
            bool hasP = ins?.p != null && ins.p.Length >= 3;

            // Axes: rows r, up, d (glTF), flipped, orthonormalised with up first (gravity), d next, r from them.
            Vector3 r = new Vector3(1, 0, 0), u = new Vector3(0, 1, 0), d = new Vector3(0, 0, 1);
            if (ins?.axes != null && ins.axes.Length >= 3 && Vec3(ins.axes[0], out var ra) && Vec3(ins.axes[1], out var ua) && Vec3(ins.axes[2], out var da))
            { r = ra; u = ua; d = da; }
            u = GltfFrame.ToUnity(u);
            d = GltfFrame.ToUnity(d);
            var rFile = GltfFrame.ToUnity(r);
            if (u.sqrMagnitude < 1e-12f || d.sqrMagnitude < 1e-12f) { why = "degenerate axes"; return false; }
            u.Normalize();
            d = d - u * Vector3.Dot(d, u);
            if (d.sqrMagnitude < 1e-10f) { why = "out axis parallel to up"; return false; }
            d.Normalize();
            // The file's r = up × d in its right-handed frame; after the flip that is d' × up' (Unity's Cross is the same
            // formula, and a mirror reverses cross products).
            var rr = Vector3.Cross(d, u).normalized;
            if (rFile.sqrMagnitude > 1e-12f && Vector3.Dot(rFile, rr) < 0f) rr = -rr;   // trust the file's handedness
            box.R = rr; box.U = u; box.D = d;
            box.P = hasP ? GltfFrame.ToUnity(ins.p) : Vector3.zero;
            var pRud = box.Rud(box.P);

            var s = cav.size_m;
            bool hasSize = s != null && s.w > 0 && s.h > 0 && s.d > 0;
            bool hasBox = cav.box?.min_rud != null && cav.box.max_rud != null && cav.box.min_rud.Length >= 3 && cav.box.max_rud.Length >= 3;
            if (hasBox)
            {
                var mn = new Vector3((float)cav.box.min_rud[0], (float)cav.box.min_rud[1], (float)cav.box.min_rud[2]);
                var mx = new Vector3((float)cav.box.max_rud[0], (float)cav.box.max_rud[1], (float)cav.box.max_rud[2]);
                var lo = Vector3.Min(mn, mx); var hi = Vector3.Max(mn, mx);
                if (hasP)
                {
                    var fbcBox = new Vector3((lo.x + hi.x) * 0.5f, lo.y, hi.z);
                    float tol = 0.02f + 0.02f * (hi - lo).magnitude;
                    bool absolute = (fbcBox - pRud).magnitude <= tol;
                    bool relative = !absolute && fbcBox.magnitude <= tol;
                    if (relative) { lo += pRud; hi += pRud; box.BoxWasRelative = true; }
                }
                box.Min = lo; box.Max = hi;
            }
            else if (hasP && hasSize)
            {
                box.Min = new Vector3(pRud.x - (float)s.w * 0.5f, pRud.y, pRud.z - (float)s.d);
                box.Max = new Vector3(pRud.x + (float)s.w * 0.5f, pRud.y + (float)s.h, pRud.z);
            }
            else { why = "no box and no insert + size"; return false; }
            if (!hasP) box.P = box.Point((box.Min.x + box.Max.x) * 0.5f, box.Min.y, box.Max.z);

            var ext = box.Max - box.Min;
            if (ext.x <= 1e-4f || ext.y <= 1e-4f || ext.z <= 1e-4f) { why = "empty box"; return false; }
            box.Size = hasSize ? new Vector3((float)s.w, (float)s.h, (float)s.d) : ext;
            var g = cav.sigma_m;
            box.Sigma = g != null ? new Vector3((float)g.w, (float)g.h, (float)g.d) : Vector3.zero;
            box.OpenTop = cav.OpenTop;
            return true;
        }

        static bool Vec3(double[] a, out Vector3 v)
        {
            v = default;
            if (a == null || a.Length < 3) return false;
            v = new Vector3((float)a[0], (float)a[1], (float)a[2]);
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z));
        }
    }
}
