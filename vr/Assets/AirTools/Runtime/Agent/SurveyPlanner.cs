using System.Collections.Generic;
using System.Linq;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Agent
{
    /// One structure object the survey will tape (world space at planning time).
    public class SurveyTarget
    {
        public string Id, Label, Group;
        /// The 4 corners in the structure file's order (0→1 width, 1→2 height), world space.
        public Vector3[] Corners;
        public Vector3 Centre;
        /// Plane normal, turned toward the side the object is measured from.
        public Vector3 Normal;
        /// Where the agent's rays start: `standoff` along the normal (like a person standing at the cabinet).
        public Vector3 Approach;
        /// The structure layer's own size × calibration (scene metres), for comparison only.
        public float WidthM, HeightM;
    }

    /// Which structure objects a survey measures and in what order (B1 hand-off §2; brain.md S1 SurveyPlanner). Pure:
    /// `where` = all | visible (view frustum) | upper / lower (centre height against the label's split, see Split) |
    /// left / right (of the head's right vector) | nearest (one); the order is a greedy nearest-neighbour sweep from
    /// the head.
    public static class SurveyPlanner
    {
        public const float Standoff = 0.6f;

        public static readonly string[] Labels = { "cabinet_door", "drawer", "panel", "appliance", "window", "door", "any" };
        public static readonly string[] Wheres = { "all", "visible", "upper", "lower", "left", "right", "nearest" };

        /// head: the wearer's eye (world) and gaze. frustum: view planes for "visible" (null: a 90° cone around the gaze).
        public static List<SurveyTarget> Plan(SceneRoot root, string label, string where, Pose head, Plane[] frustum = null, float standoff = Standoff)
        {
            var result = new List<SurveyTarget>();
            var layer = root != null ? root.Structure : null;
            if (layer == null || root.StructureSpace == null) return result;
            string want = string.IsNullOrEmpty(label) ? "any" : label.Trim().ToLowerInvariant();
            var all = new List<SurveyTarget>();
            foreach (var o in layer.Objects)
            {
                if (o.corners == null || o.corners.Length < 4) continue;
                if (want != "any" && !string.Equals(o.label, want, System.StringComparison.OrdinalIgnoreCase)) continue;
                all.Add(Target(root, o, head.position, standoff));
            }
            var picked = Filter(all, (where ?? "all").Trim().ToLowerInvariant(), head, frustum);
            return Sweep(picked, head.position);
        }

        public static SurveyTarget Target(SceneRoot root, StructureObject o, Vector3 viewer, float standoff = Standoff)
        {
            var space = root.StructureSpace;
            var corners = new Vector3[4];
            for (int i = 0; i < 4; i++) corners[i] = space.TransformPoint(o.corners[i]);
            var centre = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
            var n = FacingNormal(root, o, centre, viewer);
            return new SurveyTarget
            {
                Id = o.id, Label = o.label, Group = o.group, Corners = corners, Centre = centre, Normal = n,
                Approach = centre + n * standoff * root.transform.lossyScale.x,
                WidthM = o.widthM * root.Calibration, HeightM = o.heightM * root.Calibration,
            };
        }

        /// The object's plane normal (world), turned toward the side it was photographed from — the nearest package
        /// camera — or, without cameras, toward the viewer. Shared with BackendHarness.CheckObjects.
        public static Vector3 FacingNormal(SceneRoot root, StructureObject o, Vector3 centreWorld, Vector3 viewer)
        {
            var layer = root.Structure; var space = root.StructureSpace;
            var nLocal = o.plane >= 0 && o.plane < layer.Planes.Length ? layer.Planes[o.plane].normal
                : Vector3.Cross(o.corners[1] - o.corners[0], o.corners[3] - o.corners[0]).normalized;
            var n = space.TransformDirection(nLocal).normalized;
            if (n.sqrMagnitude < 0.5f) n = (viewer - centreWorld).normalized;
            Vector3 toward = viewer; float bestD = float.MaxValue;
            foreach (var cam in root.CamerasInRootSpace())
            {
                var cw = root.transform.TransformPoint(cam.position);
                float d = Vector3.Distance(cw, centreWorld);
                if (d < bestD) { bestD = d; toward = cw; }
            }
            return Vector3.Dot(n, toward - centreWorld) < 0f ? -n : n;
        }

        public static List<SurveyTarget> Filter(List<SurveyTarget> all, string where, Pose head, Plane[] frustum = null)
        {
            switch (where)
            {
                case "visible":
                    return all.Where(t => Visible(t.Centre, head, frustum)).ToList();
                case "upper":
                case "lower":
                {
                    float split = Split(all.Select(t => t.Centre.y).ToList());
                    return all.Where(t => where == "upper" ? t.Centre.y > split : t.Centre.y <= split).ToList();
                }
                case "left":
                case "right":
                {
                    var right = head.rotation * Vector3.right;
                    return all.Where(t => (Vector3.Dot(t.Centre - head.position, right) > 0f) == (where == "right")).ToList();
                }
                case "nearest":
                {
                    var best = all.OrderBy(t => Vector3.Distance(t.Centre, head.position)).FirstOrDefault();
                    return best != null ? new List<SurveyTarget> { best } : new List<SurveyTarget>();
                }
                default:
                    return all;
            }
        }

        /// Upper / lower threshold on centre heights: the middle of the largest gap between neighbouring heights when
        /// it is clear (> 15 cm: wall cabinets vs base cabinets, whatever their counts), else the median.
        public static float Split(List<float> heights)
        {
            if (heights == null || heights.Count == 0) return 0f;
            var h = heights.OrderBy(x => x).ToList();
            float bestGap = 0f, at = 0f;
            for (int i = 1; i < h.Count; i++)
                if (h[i] - h[i - 1] > bestGap) { bestGap = h[i] - h[i - 1]; at = (h[i] + h[i - 1]) * 0.5f; }
            if (bestGap > 0.15f) return at;
            int m = h.Count / 2;
            return h.Count % 2 == 1 ? h[m] : (h[m - 1] + h[m]) * 0.5f;
        }

        static bool Visible(Vector3 p, Pose head, Plane[] frustum)
        {
            if (frustum != null) return GeometryUtility.TestPlanesAABB(frustum, new Bounds(p, Vector3.one * 0.01f));
            var d = p - head.position;
            return d.sqrMagnitude > 1e-6f && Vector3.Angle(head.rotation * Vector3.forward, d) <= 45f;
        }

        /// Greedy nearest-neighbour order from the viewer.
        public static List<SurveyTarget> Sweep(List<SurveyTarget> targets, Vector3 from)
        {
            var left = new List<SurveyTarget>(targets);
            var order = new List<SurveyTarget>();
            var at = from;
            while (left.Count > 0)
            {
                int best = 0; float bestD = float.MaxValue;
                for (int i = 0; i < left.Count; i++)
                {
                    float d = Vector3.Distance(left[i].Centre, at);
                    if (d < bestD) { bestD = d; best = i; }
                }
                order.Add(left[best]);
                at = left[best].Centre;
                left.RemoveAt(best);
            }
            return order;
        }
    }
}
