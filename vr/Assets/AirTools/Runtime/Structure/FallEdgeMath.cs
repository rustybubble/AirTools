using System.Collections.Generic;
using System.Globalization;
using AirTools.Core;
using AirTools.Tools;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Structure
{
    /// A solid box in the scene content's frame: local corners Min..Max, placed by Position + Rotation (unit).
    public struct KnownBox
    {
        public string Name;
        public Vector3 Min, Max;
        public Vector3 Position;
        public Quaternion Rotation;
        /// Its top face is a walking / working surface (roof edge, sill, ledge): its top edges are fall-edge and
        /// ladder-support candidates. Other boxes only block (a wall rising beside an edge).
        public bool EdgeSource;
        /// What a ladder resting on its top edge stands against.
        public LadderSupport Support;

        public Vector3 ToParent(Vector3 local) => Position + Rotation * local;

        public Vector3 ToLocal(Vector3 parent) => Conjugate(Rotation) * (parent - Position);

        public bool Contains(Vector3 parent, float eps = 1e-3f)
        {
            var p = ToLocal(parent);
            return p.x >= Min.x - eps && p.x <= Max.x + eps && p.y >= Min.y - eps && p.y <= Max.y + eps && p.z >= Min.z - eps && p.z <= Max.z + eps;
        }

        static Quaternion Conjugate(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);
    }

    /// A horizontal-ish edge of a walking / working surface, with the horizontal direction off the surface.
    public struct EdgeCandidate
    {
        public string Name;
        public Vector3 A, B;
        public Vector3 Outward;
        public LadderSupport Support;

        public Vector3 Mid => (A + B) * 0.5f;
        public float Length => Vector3.Distance(A, B);
    }

    public struct FallEdge
    {
        public EdgeCandidate Edge;
        /// Height of the edge above the lower level outside it (m).
        public float DropM;
        /// Slope of the edge from horizontal (degrees).
        public float TiltDeg;
        /// Horizontal within 10° with a lower level ≥ 1.8 m below: fall protection (1926.501(b)(1)).
        public bool Flagged;
    }

    /// Fall-edge classification (presence.md S4, P7): edges horizontal within 10° whose outside drops ≥ 1.8 m (6 ft,
    /// 29 CFR 1926.501(b)(1)). Pure: edges, boxes and drop heights are passed in (EditMode-tested on the synthetic
    /// facade's known boxes); FallEdges measures the live scene.
    public static class FallEdgeMath
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public const float MinDropM = 1.8f;
        public const float MaxTiltDeg = 10f;
        /// Shorter edges are box ends (a 2.5 cm fascia end), not edges you could walk off.
        public const float MinLengthM = 0.4f;
        /// The surface side of an edge must be within this of the edge's height (a landing, a sill, a gutter).
        public const float LandingTolM = 0.3f;
        /// How far outside an edge we look for a wall rising beside it.
        public const float ProbeM = 0.05f;

        public const string Rule = "1926.501(b)(1)";
        /// The label on a flagged edge (at most 3 are shown). Raw / metric; on screen see LabelFor (D2).
        public const string LabelText = "1.8 m+ edge · fall protection";

        /// A pinched edge reads its drop: "6.20 m drop · fall protection" (raw: LastPick, the harness).
        public static string DropLabel(float dropM) => dropM.ToString("0.00", C) + " m drop · fall protection";

        /// D2: the rule's threshold as the user reads it — OSHA words it "6 feet (1.8 m)": "6 ft+" / "1.8 m+".
        public static string Threshold(UnitSystem u) => u == UnitSystem.Metric ? "1.8 m+" : "6 ft+";

        /// D2: the flagged-edge label in the user's unit ("6 ft+ edge · fall protection"; metric = LabelText).
        public static string LabelFor(UnitSystem u) => $"{Threshold(u)} edge · fall protection";

        /// D2: a pinched edge's drop in the user's unit: "20′ 4″ drop · fall protection".
        public static string DropLabel(float dropM, UnitSystem u) => $"{Units.FormatPrimary(dropM, u)} drop · fall protection";

        /// Slope of a segment from horizontal, degrees.
        public static float TiltDeg(Vector3 a, Vector3 b)
        {
            var d = b - a;
            float run = new Vector2(d.x, d.z).magnitude;
            return Mathf.Atan2(Mathf.Abs(d.y), run) * Mathf.Rad2Deg;
        }

        /// Classify one edge: the surface side must be at the edge's level (insideDrop ≤ 0.3 m) and the outside must
        /// drop ≥ 1.8 m; the edge must be horizontal within 10° and at least 0.4 m long.
        public static FallEdge Classify(in EdgeCandidate e, float insideDrop, float outsideDrop)
        {
            var f = new FallEdge { Edge = e, DropM = outsideDrop, TiltDeg = TiltDeg(e.A, e.B) };
            f.Flagged = f.TiltDeg <= MaxTiltDeg && e.Length >= MinLengthM && insideDrop <= LandingTolM && outsideDrop >= MinDropM;
            return f;
        }

        /// The four edges of a box's top face (in the parent frame), each with its horizontal outward direction.
        public static void TopEdges(in KnownBox box, List<EdgeCandidate> into)
        {
            Vector3 mn = box.Min, mx = box.Max;
            float y = mx.y;
            var c00 = new Vector3(mn.x, y, mn.z); var c10 = new Vector3(mx.x, y, mn.z);
            var c11 = new Vector3(mx.x, y, mx.z); var c01 = new Vector3(mn.x, y, mx.z);
            Add(box, c00, c10, Vector3.back, into);      // z = min
            Add(box, c01, c11, Vector3.forward, into);   // z = max
            Add(box, c00, c01, Vector3.left, into);      // x = min
            Add(box, c10, c11, Vector3.right, into);     // x = max
        }

        static void Add(in KnownBox box, Vector3 a, Vector3 b, Vector3 localOut, List<EdgeCandidate> into)
        {
            var outward = LadderMath.Horizontal(box.Rotation * localOut);
            into.Add(new EdgeCandidate { Name = box.Name, A = box.ToParent(a), B = box.ToParent(b), Outward = outward, Support = box.Support });
        }

        /// A wall rises right beside the edge: the point just outside it, a centimetre above, is inside another box.
        public static bool Blocked(in EdgeCandidate e, IReadOnlyList<KnownBox> solids)
        {
            var probe = e.Mid + e.Outward * ProbeM + Vector3.up * 0.01f;
            foreach (var b in solids)
                if (b.Contains(probe)) return true;
            return false;
        }

        /// Every top edge of the edge-source boxes that isn't blocked or a box end, classified against the ground
        /// height outside it (the surface side is the box top itself). `candidates` (optional) collects the same
        /// edges for ladder supports.
        public static List<FallEdge> FromBoxes(IReadOnlyList<KnownBox> boxes, float groundY, List<EdgeCandidate> candidates = null)
        {
            var result = new List<FallEdge>();
            var edges = new List<EdgeCandidate>(4);
            foreach (var box in boxes)
            {
                if (!box.EdgeSource) continue;
                edges.Clear();
                TopEdges(box, edges);
                foreach (var e in edges)
                {
                    if (e.Length < MinLengthM || Blocked(e, boxes)) continue;
                    float edgeY = Mathf.Min(e.A.y, e.B.y);
                    result.Add(Classify(e, 0f, edgeY - groundY));
                    candidates?.Add(e);
                }
            }
            return result;
        }

        /// The flagged edges that get a label (at most `max`, highest drop first; the rest still glow).
        public static List<int> LabelOrder(IReadOnlyList<FallEdge> edges, int max = 3)
        {
            var idx = new List<int>();
            for (int i = 0; i < edges.Count; i++) if (edges[i].Flagged) idx.Add(i);
            idx.Sort((a, b) => edges[b].DropM.CompareTo(edges[a].DropM));
            if (idx.Count > max) idx.RemoveRange(max, idx.Count - max);
            return idx;
        }

        // ---------------- ray ↔ edge ----------------

        /// Closest approach between a ray and a segment: distance, and the point on the segment.
        public static float RaySegmentDistance(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, out Vector3 onSegment, out float alongRay)
        {
            dir = dir.normalized;
            var u = b - a;
            var w0 = a - origin;
            float uu = Vector3.Dot(u, u), ud = Vector3.Dot(u, dir), uw = Vector3.Dot(u, w0), dw = Vector3.Dot(dir, w0);
            float denom = uu - ud * ud;   // |u|²·|d|² − (u·d)², |d| = 1
            float s = denom > 1e-9f ? (ud * dw - uw) / denom : 0f;   // along the segment (0..1)
            s = Mathf.Clamp01(s);
            onSegment = a + u * s;
            alongRay = Mathf.Max(0f, Vector3.Dot(onSegment - origin, dir));
            var onRay = origin + dir * alongRay;
            return Vector3.Distance(onRay, onSegment);
        }

        /// The candidate edge nearest the ray within maxDist (−1 if none); `point` is on the edge.
        public static int NearestToRay(IReadOnlyList<EdgeCandidate> edges, Vector3 origin, Vector3 dir, float maxDist, out Vector3 point, out float distance)
        {
            int best = -1;
            point = default;
            distance = maxDist;
            for (int i = 0; i < edges.Count; i++)
            {
                float d = RaySegmentDistance(origin, dir, edges[i].A, edges[i].B, out var p, out _);
                if (d <= distance) { best = i; distance = d; point = p; }
            }
            return best;
        }

        // ---------------- the synthetic facade (SPEC §4) ----------------

        /// Top of the synthetic facade's ground box.
        public const float SyntheticGroundY = 0f;

        /// The synthetic facade's collider boxes, exactly as AirTools.Editor.SyntheticFacadeBuilder.CreateHierarchy
        /// builds them (content frame; LadderTests cross-checks against the builder). Edge sources: the fascia (the
        /// eave: a roof edge, so a ladder there is sized 3 ft past it), the sill and the ledge. The wall top above the
        /// eave, the door head and the gutter are not walking surfaces; they only block.
        public static KnownBox[] SyntheticFacade()
        {
            float halfW = S.WallWidth / 2f, halfWin = S.WindowWidth / 2f, t = S.WallThickness;
            float winTop = S.SillHeight + S.WindowHeight;
            float sillBottom = S.SillHeight - S.SillSize.y;
            float sillFront = S.SillSize.z - S.WindowRecess;
            float halfF = S.FasciaLength / 2f;
            float g0 = S.FasciaProud, g1 = S.FasciaProud + S.GutterWidth, gBottom = S.FasciaBottom - 0.02f, gTop = S.FasciaBottom + 0.10f, wt = 0.006f;
            var none = Quaternion.identity;
            float ledgeRad = S.LedgeTiltDeg * Mathf.Deg2Rad;
            var ledgeRot = new Quaternion(Mathf.Sin(ledgeRad * 0.5f), 0f, 0f, Mathf.Cos(ledgeRad * 0.5f));   // Euler(tilt, 0, 0)

            KnownBox Box(string name, Vector3 min, Vector3 max, Vector3 pos, Quaternion rot, bool source = false, LadderSupport support = LadderSupport.Wall) =>
                new KnownBox { Name = name, Min = min, Max = max, Position = pos, Rotation = rot, EdgeSource = source, Support = support };

            return new[]
            {
                Box("Wall/Left", new Vector3(-halfW, 0, -t), new Vector3(-halfWin, S.WallHeight, 0), Vector3.zero, none),
                Box("Wall/Right", new Vector3(halfWin, 0, -t), new Vector3(halfW, S.WallHeight, 0), Vector3.zero, none),
                Box("Wall/Below", new Vector3(-halfWin, 0, -t), new Vector3(halfWin, sillBottom, 0), Vector3.zero, none),
                Box("Wall/Above", new Vector3(-halfWin, winTop, -t), new Vector3(halfWin, S.WallHeight, 0), Vector3.zero, none),
                Box("Window/Glass", new Vector3(-halfWin, -S.WindowHeight / 2f, -0.02f), new Vector3(halfWin, S.WindowHeight / 2f, 0f), S.WindowCentre, none),
                Box("Window/Backing", new Vector3(-halfWin, -S.WindowHeight / 2f, -(t - S.WindowRecess)), new Vector3(halfWin, S.WindowHeight / 2f, -0.02f), S.WindowCentre, none),
                Box("Sill", new Vector3(-S.SillSize.x / 2f, sillBottom, -S.WindowRecess), new Vector3(S.SillSize.x / 2f, S.SillHeight, sillFront), Vector3.zero, none, true, LadderSupport.Wall),
                Box("Fascia", new Vector3(-halfF, S.FasciaBottom, 0), new Vector3(halfF, S.FasciaTop, S.FasciaProud), Vector3.zero, none, true, LadderSupport.Landing),
                Box("Gutter/Back", new Vector3(-halfF, gBottom, g0), new Vector3(halfF, gTop, g0 + wt), Vector3.zero, none),
                Box("Gutter/Bottom", new Vector3(-halfF, gBottom, g0), new Vector3(halfF, gBottom + wt, g1), Vector3.zero, none),
                Box("Gutter/Front", new Vector3(-halfF, gBottom, g1 - wt), new Vector3(halfF, gTop, g1), Vector3.zero, none),
                Box("Door", new Vector3(S.DoorCentreX - S.DoorWidth / 2f, 0, 0), new Vector3(S.DoorCentreX + S.DoorWidth / 2f, S.DoorHeight, 0.03f), Vector3.zero, none),
                Box("Ledge/Slab", new Vector3(-S.LedgeLength / 2f, -0.06f, 0f), new Vector3(S.LedgeLength / 2f, 0f, S.LedgeDepth), new Vector3(S.LedgeCentreX, S.LedgeHeight, 0f), ledgeRot, true, LadderSupport.Wall),
                Box("Ground", new Vector3(-15f, -0.05f, -10f), new Vector3(15f, 0f, 15f), Vector3.zero, none),
            };
        }

        /// Fall edges of the synthetic facade on its ground (the S4 acceptance set).
        public static List<FallEdge> SyntheticFacadeEdges(List<EdgeCandidate> candidates = null) =>
            FromBoxes(SyntheticFacade(), SyntheticGroundY, candidates);
    }
}
