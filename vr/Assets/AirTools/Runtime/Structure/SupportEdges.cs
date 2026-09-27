using System.Collections.Generic;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using UnityEngine;

namespace AirTools.Structure
{
    /// The scene's walking / working surface edges, in SceneRoot space (presence.md S4): ladder supports (LadderTool
    /// snaps onto them) and fall-edge candidates (FallEdges draws the flagged ones). The synthetic facade uses its
    /// known boxes (FallEdgeMath.SyntheticFacade); a scan uses its structure layer's horizontal edges, sided and
    /// measured with downward raycasts (a surface at the edge's level on one side, the drop on the other). Rebuilt
    /// when the content, structure or calibration changes.
    public static class SupportEdges
    {
        public const string SyntheticSite = "synthetic-facade";
        /// Probes (scene metres): the surface side 0.1 m in, the drop 0.3 m out (past a gutter), a landing 0.4 m in.
        const float NearIn = 0.1f, FarOut = 0.3f, LandingIn = 0.4f, ProbeAbove = 0.3f;

        static GameObject s_Content;
        static StructureLayer s_Layer;
        static float s_Calibration;
        static readonly List<EdgeCandidate> s_Candidates = new List<EdgeCandidate>();
        static readonly List<FallEdge> s_Edges = new List<FallEdge>();

        /// Bumped on every rebuild (FallEdges redraws).
        public static int Version { get; private set; }

        public static bool IsSyntheticFacade(SceneRoot root) =>
            root != null && !root.IsRuntimePackage && root.Site == SyntheticSite && root.Content != null;

        /// Support / fall-edge candidates (SceneRoot space; Outward is horizontal, off the surface).
        public static IReadOnlyList<EdgeCandidate> Candidates(SceneRoot root) { Refresh(root); return s_Candidates; }

        /// Every candidate classified (same order as Candidates).
        public static IReadOnlyList<FallEdge> Edges(SceneRoot root) { Refresh(root); return s_Edges; }

        public static void Invalidate() => s_Content = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { s_Content = null; s_Layer = null; s_Candidates.Clear(); s_Edges.Clear(); }

        static void Refresh(SceneRoot root)
        {
            var content = root != null ? root.Content : null;
            if (content == null)
            {
                if (s_Content != null || s_Candidates.Count > 0) { s_Content = null; s_Candidates.Clear(); s_Edges.Clear(); Version++; }
                return;
            }
            if (s_Content == content && s_Layer == root.Structure && Mathf.Approximately(s_Calibration, root.Calibration)) return;
            s_Content = content;
            s_Layer = root.Structure;
            s_Calibration = root.Calibration;
            s_Candidates.Clear();
            s_Edges.Clear();
            Version++;
            if (IsSyntheticFacade(root)) FromFacade(root);
            else if (root.Structure != null && root.StructureSpace != null) FromStructure(root);
        }

        static void FromFacade(SceneRoot root)
        {
            var local = new List<EdgeCandidate>();
            var edges = FallEdgeMath.SyntheticFacadeEdges(local);   // content frame, ground at y = 0
            var content = root.Content.transform;
            var rt = root.transform;
            float k = root.Calibration;
            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i].Edge;
                e.A = root.PackageToRoot(e.A);
                e.B = root.PackageToRoot(e.B);
                e.Outward = LadderMath.Horizontal(rt.InverseTransformDirection(content.TransformDirection(e.Outward)));
                var f = edges[i];
                f.Edge = e;
                f.DropM *= k;
                s_Candidates.Add(e);
                s_Edges.Add(f);
            }
        }

        static void FromStructure(SceneRoot root)
        {
            var space = root.StructureSpace;
            var rt = root.transform;
            float floor = SurfaceNames.FloorY(root);
            foreach (var se in root.Structure.Edges)
            {
                var a = rt.InverseTransformPoint(space.TransformPoint(se.a));
                var b = rt.InverseTransformPoint(space.TransformPoint(se.b));
                if (Vector3.Distance(a, b) < FallEdgeMath.MinLengthM || FallEdgeMath.TiltDeg(a, b) > FallEdgeMath.MaxTiltDeg) continue;
                var along = LadderMath.Horizontal(b - a);
                var perp = new Vector3(along.z, 0f, -along.x);
                var mid = (a + b) * 0.5f;
                float y = mid.y;
                float d1 = DropAt(root, mid + perp * NearIn, y, floor), d2 = DropAt(root, mid - perp * NearIn, y, floor);
                bool firstIsInside = Mathf.Abs(d1) <= Mathf.Abs(d2);
                float inside = Mathf.Abs(firstIsInside ? d1 : d2);
                if (inside > FallEdgeMath.LandingTolM) continue;   // no surface at the edge's level: a line on a wall
                var outward = firstIsInside ? -perp : perp;
                float outside = DropAt(root, mid + outward * FarOut, y, floor);
                bool landing = Mathf.Abs(DropAt(root, mid - outward * LandingIn, y, floor)) <= FallEdgeMath.LandingTolM;
                var c = new EdgeCandidate
                {
                    Name = string.IsNullOrEmpty(se.id) ? "edge" : se.id, A = a, B = b, Outward = outward,
                    Support = landing ? LadderSupport.Landing : LadderSupport.Wall,
                };
                s_Candidates.Add(c);
                s_Edges.Add(FallEdgeMath.Classify(c, inside, outside));
            }
        }

        /// How far below `edgeY` the first surface under a SceneRoot-space point is (negative = above the edge);
        /// nothing under it: down to the scene's floor.
        static float DropAt(SceneRoot root, Vector3 p, float edgeY, float floor)
        {
            var rt = root.transform;
            float scale = Mathf.Max(rt.lossyScale.x, 1e-6f);
            var from = rt.TransformPoint(new Vector3(p.x, edgeY + ProbeAbove, p.z));
            if (Physics.Raycast(from, -rt.up, out var h, 80f * scale, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore))
                return edgeY - rt.InverseTransformPoint(h.point).y;
            return float.IsNaN(floor) ? float.PositiveInfinity : edgeY - floor;
        }
    }
}
