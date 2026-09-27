using System.Collections.Generic;
using UnityEngine;

namespace AirTools.Parts
{
    /// gaze-catalog: the loaded scan's own frame, in SceneRoot space (scene metres, the scale calibration applied): its up
    /// (scans can be tilted: from the structure's Manhattan axis or its level planes, else the package's up), where the
    /// ground is and how tall the scan is along that up, and whether it's a room (a counter and a floor) or outside a
    /// building (a roof and the ground).
    public struct SiteFrame
    {
        public bool Valid;
        public Vector3 Up;
        /// Heights along Up (Vector3.Dot(Up, p)) of the ground / floor and of the scan's top.
        public float Ground, Top;
        public bool Indoor;
        /// Where Up came from: "axis", "planes", "package".
        public string UpFrom;

        public float HeightOf(Vector3 p) => Vector3.Dot(Up, p) - Ground;
        public float Span => Mathf.Max(0f, Top - Ground);

        public override string ToString() =>
            Valid ? $"up {Up.x:0.000},{Up.y:0.000},{Up.z:0.000} ({UpFrom}) ground {Ground:0.00} top {Top:0.00} span {Span:0.0} m {(Indoor ? "indoor" : "outdoor")}" : "no frame";
    }

    /// gaze-catalog: one structure plane for the frame, in SceneRoot space.
    public struct FramePlane
    {
        public Vector3 Normal, Centroid;
        public float Area;
        public FramePlane(Vector3 normal, Vector3 centroid, float area) { Normal = normal; Centroid = centroid; Area = area; }
    }

    /// gaze-catalog: a rectangle on a surface (a structure window or door, a taped opening, an appliance's front), in
    /// SceneRoot space: a hit inside it (with a margin, near its plane) is on that thing.
    public struct FocusRect
    {
        public Vector3 Centre, Right, UpDir, Normal;
        public float HalfW, HalfH;
        /// What it is: 0 an opening (window, door), 1 an appliance.
        public int Kind;
        public string Subject;

        public const int Opening = 0, Appliance = 1;

        /// From 4 corners in order (u0 v0, u1 v0, u1 v1, u0 v1), as the structure's objects give them.
        public static FocusRect FromCorners(Vector3 c0, Vector3 c1, Vector3 c2, Vector3 c3, int kind, string subject)
        {
            var r = new FocusRect { Centre = (c0 + c1 + c2 + c3) * 0.25f, Kind = kind, Subject = subject };
            var w = c1 - c0;
            var h = c3 - c0;
            r.HalfW = w.magnitude * 0.5f;
            r.HalfH = h.magnitude * 0.5f;
            r.Right = r.HalfW > 1e-6f ? w / (2f * r.HalfW) : Vector3.right;
            r.UpDir = r.HalfH > 1e-6f ? h / (2f * r.HalfH) : Vector3.up;
            var n = Vector3.Cross(r.Right, r.UpDir);
            r.Normal = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.forward;
            return r;
        }

        public bool Contains(Vector3 p, float margin, float depth)
        {
            var d = p - Centre;
            return Mathf.Abs(Vector3.Dot(d, Right)) <= HalfW + margin && Mathf.Abs(Vector3.Dot(d, UpDir)) <= HalfH + margin
                && Mathf.Abs(Vector3.Dot(d, Normal)) <= depth;
        }
    }

    /// gaze-catalog: what the head ray met (SceneRoot space). Normal: the structure plane's when the hit is on one, else
    /// the scan's triangles averaged over a few rays.
    public struct GazeHit
    {
        public bool Hit;
        public Vector3 Point, Normal;
        /// Inside a structure window / door or a taped opening.
        public bool InOpening;
        /// On an appliance (a scene part of class appliance, a structure "appliance" rectangle).
        public bool OnAppliance;
    }

    /// gaze-catalog: the head ray's surface, classified in the scan's own up frame (pure; EditMode-tested):
    /// - opening: inside a taped or structure window / door;
    /// - ceiling: facing down;
    /// - wall: near vertical (outside: always; in a room: above the backsplash — below it the base cabinets and the
    ///   dishwasher's front are the floor's things, the backsplash band is the counter's);
    /// - roof (outside): high in the scan and roughly level, or sloped and above the ground band;
    /// - ground: low and level (outside, below the roof band; in a room, the floor);
    /// - counter (in a room): level at 0.75–1.10 m above the floor;
    /// - otherwise none (the sky, a shelf, the top of the fridge).
    /// The current focus gets a margin at every boundary (hysteresis), so a surface near a threshold doesn't flip it.
    public static class GazeFocusMath
    {
        /// Tilt of the surface normal from the scan's up (degrees): 0 level (facing up), 90 vertical, 180 facing down.
        public const float LevelMaxDeg = 15f, WallMinDeg = 65f, WallMaxDeg = 115f, CeilingMinDeg = 125f;
        /// Outside: a roof is at least this high, and (level) in the upper RoofBandLevel of the scan's height span or
        /// (sloped) above RoofBandSloped of it.
        public const float RoofMinM = 2.5f, RoofBandLevel = 0.35f, RoofBandSloped = 0.2f;
        /// In a room: the floor band, the counter band, the top of the backsplash (the upper cabinets start there).
        public const float FloorMaxM = 0.35f, CounterMinM = 0.75f, CounterMaxM = 1.10f, BacksplashTopM = 1.40f;
        /// A vertical surface in a room below this is a base cabinet / appliance front (the floor's things).
        public const float LowFrontMaxM = 0.85f;
        /// Hysteresis in favour of the current focus.
        public const float MarginDeg = 5f, MarginIndoorM = 0.08f, MarginOutdoorM = 1.0f;

        /// Degrees between the normal and up (0–180).
        public static float TiltDeg(Vector3 normal, Vector3 up)
        {
            float c = Vector3.Dot(normal.normalized, up.normalized);
            return Mathf.Acos(Mathf.Clamp(c, -1f, 1f)) * Mathf.Rad2Deg;
        }

        public static CatalogFocus Classify(in GazeHit hit, in SiteFrame frame, CatalogFocus current = CatalogFocus.None)
        {
            if (!hit.Hit || !frame.Valid || hit.Normal.sqrMagnitude < 1e-8f) return CatalogFocus.None;
            if (hit.InOpening) return CatalogFocus.Opening;
            float tilt = TiltDeg(hit.Normal, frame.Up);
            float h = frame.HeightOf(hit.Point);
            float m = MarginDeg;

            // Facing down: a ceiling (or an eave's underside).
            float ceilingMin = CeilingMinDeg - (current == CatalogFocus.Ceiling ? m : current == CatalogFocus.Wall ? -m : 0f);
            if (tilt >= ceilingMin) return CatalogFocus.Ceiling;

            // Near vertical.
            float wallMin = WallMinDeg - (IsWallish(current, frame.Indoor) ? m : 0f);
            if (tilt >= wallMin) return frame.Indoor ? IndoorFront(hit, h, current) : CatalogFocus.Wall;

            bool level = tilt <= LevelMaxDeg + (current == CatalogFocus.Ground || current == CatalogFocus.Counter ? m : current == CatalogFocus.Roof ? -m : 0f);
            if (frame.Indoor)
            {
                if (!level) return CatalogFocus.None;   // a sloped surface in a room: a chair back, clutter
                float mi = MarginIndoorM;
                if (h <= FloorMaxM + (current == CatalogFocus.Ground ? mi : 0f)) return CatalogFocus.Ground;
                float lo = CounterMinM - (current == CatalogFocus.Counter ? mi : 0f), hi = CounterMaxM + (current == CatalogFocus.Counter ? mi : 0f);
                if (h >= lo && h <= hi) return CatalogFocus.Counter;
                return CatalogFocus.None;   // a shelf, the top of the fridge
            }

            // Outside: roof or ground by height in the scan's span.
            float mo = current == CatalogFocus.Roof ? -MarginOutdoorM : current == CatalogFocus.Ground ? MarginOutdoorM : 0f;
            float roofAt = Mathf.Max(RoofMinM, (level ? RoofBandLevel : RoofBandSloped) * frame.Span) + mo;
            return h >= roofAt ? CatalogFocus.Roof : CatalogFocus.Ground;
        }

        static bool IsWallish(CatalogFocus f, bool indoor) =>
            f == CatalogFocus.Wall || (indoor && (f == CatalogFocus.Counter || f == CatalogFocus.Ground));

        /// A vertical surface in a room: an appliance or a low front (base cabinets) → the floor's things; the backsplash
        /// band → the counter's; above → the wall.
        static CatalogFocus IndoorFront(in GazeHit hit, float h, CatalogFocus current)
        {
            if (hit.OnAppliance) return CatalogFocus.Ground;
            float mi = MarginIndoorM;
            float low = LowFrontMaxM + (current == CatalogFocus.Ground ? mi : current == CatalogFocus.Counter ? -mi : 0f);
            if (h < low) return CatalogFocus.Ground;
            float top = BacksplashTopM + (current == CatalogFocus.Counter ? mi : current == CatalogFocus.Wall ? -mi : 0f);
            return h < top ? CatalogFocus.Counter : CatalogFocus.Wall;
        }

        // ---------------- the frame ----------------

        /// Cos of the angle from the package's up within which a structure axis, or a plane (first pass), counts as up:
        /// a scan may be loaded that far off (not a 40° roof).
        public const float UpAxisCos = 0.9063f;   // 25°
        /// A level plane: within 12° of the (found) up.
        public const float LevelPlaneCos = 0.9781f;   // 12°

        /// The scan's frame from its structure planes (SceneRoot space), its Manhattan axes, the package's up and the
        /// collision's bounds (their bottom and top along up give the fallback ground and the top). `trustPackageUp`: the
        /// package says where up is and was levelled against gravity (scene.json `up` with a small gravity residual: the
        /// structure's level planes were regularised to it) — then it stands. Otherwise (a tilted or unlevelled scan) up is
        /// the structure's Manhattan axis nearest to it (within 25°), else the level planes' area-weighted normal (within
        /// 25°, then refined within 12°). `indoor`: a room.
        public static SiteFrame Frame(IReadOnlyList<FramePlane> planes, IReadOnlyList<Vector3> axes, Vector3 packageUp, bool trustPackageUp,
            Vector3 boundsMin, Vector3 boundsMax, bool indoor)
        {
            var f = new SiteFrame { Indoor = indoor };
            var up = packageUp.sqrMagnitude > 1e-8f ? packageUp.normalized : Vector3.up;
            f.UpFrom = "package";
            bool found = trustPackageUp;
            if (!found && axes != null)
                foreach (var a in axes)
                {
                    if (a.sqrMagnitude < 1e-8f) continue;
                    var n = a.normalized;
                    float d = Vector3.Dot(n, up);
                    if (Mathf.Abs(d) >= UpAxisCos) { up = d > 0f ? n : -n; f.UpFrom = "axis"; found = true; break; }
                }
            if (!found && planes != null)
            {
                // The level planes' area-weighted normal: first within 25° of the package's up, then within 12° of that.
                for (int pass = 0; pass < 2; pass++)
                {
                    float within = pass == 0 ? UpAxisCos : LevelPlaneCos;
                    var sum = Vector3.zero;
                    foreach (var p in planes)
                    {
                        float d = Vector3.Dot(p.Normal, up);
                        if (Mathf.Abs(d) >= within) sum += (d > 0f ? p.Normal : -p.Normal) * Mathf.Max(p.Area, 1e-4f);
                    }
                    if (sum.sqrMagnitude < 1e-10f) break;
                    up = sum.normalized;
                    f.UpFrom = "planes";
                }
            }
            f.Up = up;

            // The bounds' bottom and top along up, through their middle (a big box's corners would reach metres lower
            // along a slightly tilted up than the scan does).
            bool hasBounds = boundsMax.x > boundsMin.x || boundsMax.y > boundsMin.y || boundsMax.z > boundsMin.z;
            var mid = (boundsMin + boundsMax) * 0.5f;
            float lo = Vector3.Dot(up, new Vector3(mid.x, boundsMin.y, mid.z)), hi = Vector3.Dot(up, new Vector3(mid.x, boundsMax.y, mid.z));

            // The ground: the lowest level plane facing up, big enough to stand on; a scan whose collision reaches lower
            // outside (the street below a hospital's car park) goes by its bounds (half a metre above the lowest noise).
            float minArea = indoor ? 0.3f : 4f;
            float ground = float.MaxValue, top = float.MinValue;
            if (planes != null)
                foreach (var p in planes)
                {
                    float h = Vector3.Dot(up, p.Centroid);
                    top = Mathf.Max(top, h);
                    if (Vector3.Dot(p.Normal, up) >= LevelPlaneCos && p.Area >= minArea) ground = Mathf.Min(ground, h);
                }
            if (hasBounds)
            {
                if (ground == float.MaxValue || (!indoor && lo + 0.5f < ground)) ground = lo + (indoor ? 0f : 0.5f);
                top = Mathf.Max(top, hi);
            }
            if (ground == float.MaxValue) return f;   // nothing to stand on: no frame
            f.Ground = ground;
            f.Top = top == float.MinValue ? ground : Mathf.Max(top, ground);
            f.Valid = true;
            return f;
        }

        /// A structure object's rectangle kind: an opening (window, door) or an appliance; −1 for the rest (cabinet
        /// doors are no openings).
        public static int RectKind(string label, out string subject)
        {
            subject = null;
            switch ((label ?? "").Trim().ToLowerInvariant())
            {
                case "window": case "dormer": case "skylight": subject = "a window"; return FocusRect.Opening;
                case "door": case "doorway": case "garage_door": case "garage door": case "opening": subject = "a door"; return FocusRect.Opening;
                case "appliance": subject = "an appliance"; return FocusRect.Appliance;
                default: return -1;
            }
        }

        /// A plane's area from its outline in its own (u, v) (package units) and the calibration.
        public static float OutlineArea(Vector2[] ring, float calibration)
        {
            if (ring == null || ring.Length < 3) return 0f;
            float a = 0f;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++) a += ring[j].x * ring[i].y - ring[i].x * ring[j].y;
            return Mathf.Abs(a) * 0.5f * calibration * calibration;
        }
    }
}
