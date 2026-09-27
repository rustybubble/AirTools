using System;
using AirTools.Core;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Parts
{
    /// What a spot in the scene is, as the facts on hand describe it (docs/ux/specs/W0.9-copy.md §4.3).
    public struct SurfaceSample
    {
        public bool IsScan;
        /// Facade collider path below the content root ("Wall/Above", "Fascia"); null on a scan.
        public string Path;
        /// Structure-layer object label under the point ("cabinet_door", "drawer", "panel", "appliance"); null if none.
        public string ObjectLabel;
        /// Surface normal · up (1 faces up, 0 vertical, −1 faces down); NaN when unknown.
        public float UpDot;
        /// Height of the point above the scene's floor, in scene metres (calibration-aware, tabletop-proof); NaN if unknown.
        public float HeightAboveFloorM;
        /// The nearest structure plane (within 2 cm and 15°) hosts at least one object (a cabinet face).
        public bool OnObjectPlane;
        public string Site;
    }

    /// What the structure layer says a two-point tape spans.
    public enum TapeTarget { Unknown, Door, Drawer, Panel, Appliance, Run }

    /// Human names for surfaces: "the door", "the toe-kick", "the counter", "the wall" — never "scan" or a collider path.
    /// Classify and FromPath are pure (EditMode table-tested); ForPoint / ForPart / TapeTargets read the live scene.
    public static class SurfaceNames
    {
        public const float ToeKickMaxM = 0.20f, FloorMaxM = 0.05f, CounterMaxM = 1.2f;

        /// Facade collider path → name.
        public static string FromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "the building";
            var head = path.Split('/')[0].Trim();
            switch (head)
            {
                case "Wall": return "the wall";
                case "Window": return "the window";
                case "Sill": return "the sill";
                case "Fascia": return "the fascia";
                case "Gutter": return "the gutter";
                case "Door": return "the door";
                case "Ledge": return "the ledge";
                case "Ground": return "the ground";
                default: return "the building";
            }
        }

        /// Structure object label → name.
        public static string FromObjectLabel(string label)
        {
            switch ((label ?? "").Trim().ToLowerInvariant())
            {
                case "cabinet_door": case "door": return "the door";
                case "drawer": return "the drawer";
                case "appliance": return "the appliance";
                case "panel": return "the cabinet";
                case "": return null;
                default: return "the " + label.Trim().Replace('_', ' ');
            }
        }

        static bool IsKitchen(string site) => site != null && site.IndexOf("kitchen", StringComparison.OrdinalIgnoreCase) >= 0;

        /// Rules, first match wins (W0.9 §4.3).
        public static string Classify(in SurfaceSample s)
        {
            if (!s.IsScan) return FromPath(s.Path);
            var obj = FromObjectLabel(s.ObjectLabel);
            if (obj != null) return obj;
            if (float.IsNaN(s.UpDot)) return "the " + AirTools.UI.Copy.SiteName(s.Site);
            bool heightKnown = !float.IsNaN(s.HeightAboveFloorM);
            float h = s.HeightAboveFloorM;
            if (s.UpDot >= 0.9f)
            {
                if (heightKnown && h <= FloorMaxM) return "the floor";
                if (IsKitchen(s.Site)) return heightKnown && h > CounterMaxM ? "the top of the cabinet" : "the counter";
                return "the ledge";
            }
            if (s.UpDot <= -0.9f) return "the underside";
            if (Mathf.Abs(s.UpDot) < 0.3f)
            {
                if (heightKnown && h <= ToeKickMaxM) return IsKitchen(s.Site) ? "the toe-kick" : "the base of the wall";
                if (s.OnObjectPlane) return "the cabinet";
                return "the wall";
            }
            return "the surface";
        }

        // ---------------- live scene ----------------

        static GameObject s_FloorContent;
        static float s_FloorY, s_FloorCalibration;

        /// The scene's floor height in SceneRoot-local metres (lowest scene-surface collider under the content), cached
        /// per content. NaN without a scene.
        public static float FloorY(SceneRoot root)
        {
            if (root == null || root.Content == null) return float.NaN;
            if (s_FloorContent == root.Content && Mathf.Approximately(s_FloorCalibration, root.Calibration)) return s_FloorY;
            float min = float.PositiveInfinity;
            foreach (var c in root.Content.GetComponentsInChildren<Collider>(false))
            {
                if (((1 << c.gameObject.layer) & SceneLayers.SceneSurfaceMask) == 0) continue;
                var b = c.bounds;
                min = Mathf.Min(min, root.transform.InverseTransformPoint(new Vector3(b.center.x, b.min.y, b.center.z)).y);
            }
            s_FloorContent = root.Content;
            s_FloorCalibration = root.Calibration;
            s_FloorY = float.IsInfinity(min) ? float.NaN : min;
            return s_FloorY;
        }

        /// Forget the cached floor (new content).
        public static void ResetCache() { s_FloorContent = null; s_FloorY = float.NaN; }

        /// A sample for a world point + outward normal on a collider.
        public static SurfaceSample Sample(Vector3 worldPoint, Vector3 worldNormal, Collider col)
        {
            var s = new SurfaceSample { UpDot = float.NaN, HeightAboveFloorM = float.NaN };
            Services.TryGet<SceneRoot>(out var root);
            s.Site = root != null ? root.Site : null;
            s.IsScan = col != null && SnapService.IsScan(col);
            if (!s.IsScan) { s.Path = col != null ? FitChecker.SurfaceName(col) : null; return s; }
            if (root == null) return s;
            var up = root.transform.up;
            if (worldNormal.sqrMagnitude > 1e-6f) s.UpDot = Vector3.Dot(worldNormal.normalized, up);
            float floor = FloorY(root);
            if (!float.IsNaN(floor)) s.HeightAboveFloorM = root.transform.InverseTransformPoint(worldPoint).y - floor;
            var layer = root.Structure;
            var space = root.StructureSpace;
            if (layer != null && space != null)
            {
                var p = space.InverseTransformPoint(worldPoint);
                var n = worldNormal.sqrMagnitude > 1e-6f ? space.InverseTransformDirection(worldNormal).normalized : Vector3.zero;
                float perScene = 1f / Mathf.Max(root.Calibration, 1e-4f);   // package units per scene metre
                s.ObjectLabel = ObjectAt(layer, p, 0.02f * perScene, 0.01f * perScene);
                s.OnObjectPlane = OnObjectPlane(layer, p, n, 0.02f * perScene, 15f);
            }
            return s;
        }

        public static string ForPoint(Vector3 worldPoint, Vector3 worldNormal, Collider col) => Classify(Sample(worldPoint, worldNormal, col));

        /// Where a placed or previewing part sits ("the door"); null when it isn't on anything.
        public static string ForPart(PartInstance part)
        {
            if (part == null || part.Surface == null) return null;
            return ForPoint(part.transform.position, part.SurfaceNormal, part.Surface);
        }

        /// Label of the structure object whose quad (expanded by `expand`) contains p within `tol` of its plane.
        public static string ObjectAt(StructureLayer layer, Vector3 p, float tol, float expand)
        {
            if (layer?.Objects == null) return null;
            foreach (var o in layer.Objects)
                if (InQuad(o.corners, p, tol, expand)) return o.label;
            return null;
        }

        static bool OnObjectPlane(StructureLayer layer, Vector3 p, Vector3 n, float tol, float maxDeg)
        {
            if (layer?.Planes == null || layer.Objects == null || layer.Objects.Length == 0) return false;
            int best = -1; float bestD = tol;
            for (int i = 0; i < layer.Planes.Length; i++)
            {
                var pl = layer.Planes[i];
                float d = Mathf.Abs(pl.SignedDistance(p));
                if (d > bestD) continue;
                if (n.sqrMagnitude > 0.5f && Vector3.Angle(pl.normal, n) > maxDeg && Vector3.Angle(-pl.normal, n) > maxDeg) continue;
                best = i; bestD = d;
            }
            if (best < 0) return false;
            foreach (var o in layer.Objects) if (o.plane == best) return true;
            return false;
        }

        /// p lies within tol of the quad's plane and inside the quad grown by `expand` (convex, any winding).
        public static bool InQuad(Vector3[] c, Vector3 p, float tol, float expand)
        {
            if (c == null || c.Length < 3) return false;
            var centroid = Vector3.zero;
            foreach (var q in c) centroid += q;
            centroid /= c.Length;
            var n = Vector3.Cross(c[1] - c[0], c[c.Length - 1] - c[0]);
            if (n.sqrMagnitude < 1e-12f) return false;
            n.Normalize();
            if (Mathf.Abs(Vector3.Dot(p - c[0], n)) > tol) return false;
            for (int i = 0; i < c.Length; i++)
            {
                var a = c[i]; var b = c[(i + 1) % c.Length];
                var inward = Vector3.Cross(n, b - a);
                if (inward.sqrMagnitude < 1e-12f) continue;
                inward.Normalize();
                if (Vector3.Dot(centroid - a, inward) < 0f) inward = -inward;
                if (Vector3.Dot(p - a, inward) < -expand) return false;
            }
            return true;
        }

        /// What a tape between two SceneRoot-local points spans: both ends within 3 cm of the same object quad → that
        /// object; a long horizontal run on the facade → Run; else Unknown.
        public static TapeTarget ClassifyTape(Vector3 aRoot, Vector3 bRoot, SceneRoot root)
        {
            if (root == null) return TapeTarget.Unknown;
            var layer = root.Structure;
            if (layer != null && root.Content != null && layer.Objects != null)
            {
                var pa = root.RootToPackage(aRoot);
                var pb = root.RootToPackage(bRoot);
                float perScene = 1f / Mathf.Max(root.Calibration, 1e-4f);
                float tol = 0.03f * perScene;
                foreach (var o in layer.Objects)
                {
                    if (!InQuad(o.corners, pa, tol, tol) || !InQuad(o.corners, pb, tol, tol)) continue;
                    switch ((o.label ?? "").ToLowerInvariant())
                    {
                        case "cabinet_door": case "door": return TapeTarget.Door;
                        case "drawer": return TapeTarget.Drawer;
                        case "panel": return TapeTarget.Panel;
                        case "appliance": return TapeTarget.Appliance;
                    }
                }
            }
            if (!root.IsRuntimePackage && PartsClient.TapeAxis(aRoot, bRoot) == "length") return TapeTarget.Run;
            return TapeTarget.Unknown;
        }

        /// "Door" / "Drawer" / "Cabinet" / "Appliance" / "Run"; null for Unknown.
        public static string TargetNoun(TapeTarget t) => t switch
        {
            TapeTarget.Door => "Door",
            TapeTarget.Drawer => "Drawer",
            TapeTarget.Panel => "Cabinet",
            TapeTarget.Appliance => "Appliance",
            TapeTarget.Run => "Run",
            _ => null,
        };
    }
}
