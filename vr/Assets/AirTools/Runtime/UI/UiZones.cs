using System;
using System.Collections.Generic;
using UnityEngine;

namespace AirTools.UI
{
    /// The app modes a surface can show in (a pair of surfaces only collides when they share one).
    [Flags]
    public enum UiModes { None = 0, Passthrough = 1, World = 2, Tabletop = 4, Inside = World | Tabletop, All = Passthrough | World | Tabletop }

    /// How a zone draws: heads-up (overlay glass and text, above every panel), in the world, or on the hand.
    public enum ZoneDraw { Hud, World, Hand }

    /// An angular box as the eye sees it (degrees): yaw − left / + right of the heading, pitch + up / − down from the eye
    /// line.
    public struct UiFootprint
    {
        public float YawMin, YawMax, PitchMin, PitchMax;

        public float Width => YawMax - YawMin;
        public float Height => PitchMax - PitchMin;
        public float CentrePitch => (PitchMin + PitchMax) * 0.5f;
        public float CentreYaw => (YawMin + YawMax) * 0.5f;

        /// The smallest box holding both.
        public static UiFootprint Union(UiFootprint a, UiFootprint b) => new UiFootprint
        {
            YawMin = Mathf.Min(a.YawMin, b.YawMin), YawMax = Mathf.Max(a.YawMax, b.YawMax),
            PitchMin = Mathf.Min(a.PitchMin, b.PitchMin), PitchMax = Mathf.Max(a.PitchMax, b.PitchMax),
        };

        public override string ToString() =>
            $"yaw {YawMin:0.0}…{YawMax:0.0}° pitch {PitchMin:0.0}…{PitchMax:0.0}°";
    }

    /// One zone of the declutter map (docs/ux/declutter.md §3.1): where a class of surface lives, placed from the person
    /// (distance along a line `DownDeg` below the eye line — the status line: below the gaze — and `YawDeg` to the side),
    /// and the largest panel it may hold. One thing at a time per zone.
    public readonly struct UiZone
    {
        public readonly string Name;
        public readonly float Distance, DownDeg, YawDeg, MaxWidth, MaxHeight;
        /// Where the panel's centre sits above (+) or below (−) the placement point (the pill's chips hang below it).
        public readonly float CentreY;
        public readonly ZoneDraw Draws;

        public UiZone(string name, float distance, float downDeg, float yawDeg, float maxWidth, float maxHeight, ZoneDraw draws, float centreY = 0f)
        {
            Name = name; Distance = distance; DownDeg = downDeg; YawDeg = yawDeg; MaxWidth = maxWidth; MaxHeight = maxHeight; Draws = draws;
            CentreY = centreY;
        }

        /// Is a surface placed at (distance, down, yaw) in this zone? (1 cm, 0.5°.)
        public bool Holds(float distance, float downDeg, float yawDeg) =>
            Mathf.Abs(distance - Distance) < 0.01f && Mathf.Abs(downDeg - DownDeg) < 0.5f && Mathf.Abs(yawDeg - YawDeg) < 0.5f;

        public UiFootprint Footprint() =>
            UiZones.Footprint(Distance, DownDeg, YawDeg, -MaxWidth * 0.5f, MaxWidth * 0.5f, CentreY - MaxHeight * 0.5f, CentreY + MaxHeight * 0.5f);

        public override string ToString() => Name;
    }

    /// A head-relative surface as its builder places it (declutter §1.1): the placement, and the panel's rect in its own
    /// plane relative to the placement point (m; the coach card, say, hangs down from its point). A surface with two
    /// placements (the job rail steps aside while a window is open) is two entries with the same name.
    public struct UiSurface
    {
        public string Name;
        public float Distance, DownDeg, YawDeg;
        public float XMin, XMax, YMin, YMax;
        public UiModes Modes;
        public bool Hud;

        public static UiSurface Centred(string name, float distance, float downDeg, float yawDeg, float width, float height, UiModes modes, bool hud = false) =>
            new UiSurface
            {
                Name = name, Distance = distance, DownDeg = downDeg, YawDeg = yawDeg,
                XMin = -width * 0.5f, XMax = width * 0.5f, YMin = -height * 0.5f, YMax = height * 0.5f, Modes = modes, Hud = hud,
            };

        public UiFootprint Footprint() => UiZones.Footprint(Distance, DownDeg, YawDeg, XMin, XMax, YMin, YMax);

        /// The zone this surface sits in, or null.
        public UiZone? Zone => UiZones.ZoneOf(Distance, DownDeg, YawDeg);

        /// Surfaces in one zone are one surface for collisions (the zone holds one thing at a time): the zone's name, else
        /// the surface's own.
        public string Group => Zone?.Name ?? Name;

        public override string ToString() => $"{Name} {Distance:0.00} m · −{DownDeg:0}° · {YawDeg:+0;−0;0}°";
    }

    /// Declutter (docs/ux/declutter.md §3.1): the one map of head-relative placements. Builders and views read their
    /// placement here instead of literals; DeclutterTests fails on any new overlap between head-relative surfaces
    /// (UiZones.Collisions) and AgentHarness.Surfaces() counts what's up against the §2 budget (UiBudget). Pure: no
    /// engine calls, so it runs in the offline test runner too.
    public static class UiZones
    {
        /// Edges closer than this (in either direction) touch rather than overlap.
        public const float EdgeEpsilonDeg = 1f;
        /// Side slots sit 5 cm behind the main slot: their inner edge may overlap the main window by this much (§3.1).
        public const float SideSlotEdgeDeg = 6f;
        public const float SideSlotBehind = 0.05f;
        /// The docked status line's centre sits this far above a window's top rim (M3).
        public const float DockLift = 0.005f;
        /// The band under a window's top rim that holds its title and Close (W0.7 windows put the title at top − 0.026 m,
        /// Heading at 0.45 m: its line ends ≈ 0.034 m under the rim). The docked line may cover this band, nothing below.
        public const float TitleRow = 0.036f;
        /// The docked line's centre stays at or under this pitch above the eye line (M3).
        public const float DockCeilingDeg = 10f;

        public static readonly UiZone Status = new UiZone("Status", 0.9f, 17f, 0f, 0.44f, 0.12f, ZoneDraw.Hud);
        public static readonly UiZone Main = new UiZone("Main", 0.45f, 20f, 0f, 0.36f, 0.44f, ZoneDraw.World);
        public static readonly UiZone SideLeft = new UiZone("SideLeft", 0.5f, 15f, -35f, 0.30f, 0.40f, ZoneDraw.World);
        /// Settings: 0.46 m tall since scene parts added its Take out row (declutter M7 was 0.43); settings-assets: 0.528 m
        /// with the Talk and 3D-models rows (the zone overlaps are unchanged: Main × SideRight stays a 5.6° side edge).
        public static readonly UiZone SideRight = new UiZone("SideRight", 0.5f, 18f, 35f, 0.34f, 0.528f, ZoneDraw.World);
        /// The primary sits on the placement point and its two chips hang under it (GuideRailBuilder.BuildPill).
        public static readonly UiZone Pill = new UiZone("Pill", 0.45f, 24f, 22f, 0.20f, 0.084f, ZoneDraw.World, centreY: -0.02f);
        public static readonly UiZone Enter = new UiZone("Enter", 0.45f, 25f, 0f, 0.15f, 0.06f, ZoneDraw.World);

        /// Not head-relative (listed for the map): 0.05 m above the left wrist or controller, ≤ 0.16 × 0.05 m; 0.03 m off
        /// the palm, ring radius 0.105 m; the quad at the capture camera (floating fallback 1.5 m, 8° down, ≤ 1.2 m wide).
        public const float WristLift = 0.05f, WristMaxWidth = 0.16f, WristMaxHeight = 0.05f;
        public const float PalmLift = 0.03f, RingRadius = 0.105f;
        public const float QuadDistance = 1.5f, QuadDownDeg = 8f, QuadMaxWidth = 1.2f;

        /// modelwheel: Model view isn't a head-relative surface either — it's placed once from the person when it opens and
        /// then stays put (world-locked; ModelViewLayout): the model centred straight ahead at eye height ~1 m away (≈
        /// 40–50° wide), and the wheel of model cards right under it facing the eyes — its lens 0.78 m away, 16–34° below
        /// the eye line, the arc ±~30° of azimuth. Windows at 0.45–0.5 m draw in front of it as they do of the world;
        /// with the guide rail on (DemoMode, off by default) the status line 17° below the gaze can lie over the lens card.
        public const float ModelWheelMaxAzimuthDeg = 35f;

        /// Every head-relative zone.
        public static readonly UiZone[] HeadRelative = { Status, Main, SideLeft, SideRight, Pill, Enter };

        public static UiZone? ZoneOf(float distance, float downDeg, float yawDeg)
        {
            foreach (var z in HeadRelative) if (z.Holds(distance, downDeg, yawDeg)) return z;
            return null;
        }

        /// The angular box of a `w` × `h` panel centred on its placement point.
        public static UiFootprint Footprint(float distance, float downDeg, float yawDeg, float w, float h) =>
            Footprint(distance, downDeg, yawDeg, -w * 0.5f, w * 0.5f, -h * 0.5f, h * 0.5f);

        /// The angular box of a rect in a panel's plane (x right, y up, relative to the placement point), the panel facing
        /// the eye at `distance`, `downDeg` below the eye line and `yawDeg` to the side (declutter §1.3's method).
        public static UiFootprint Footprint(float distance, float downDeg, float yawDeg, float xMin, float xMax, float yMin, float yMax)
        {
            float d = Mathf.Max(0.01f, distance);
            return new UiFootprint
            {
                YawMin = yawDeg + Mathf.Atan(xMin / d) * Mathf.Rad2Deg,
                YawMax = yawDeg + Mathf.Atan(xMax / d) * Mathf.Rad2Deg,
                PitchMin = -downDeg + Mathf.Atan(yMin / d) * Mathf.Rad2Deg,
                PitchMax = -downDeg + Mathf.Atan(yMax / d) * Mathf.Rad2Deg,
            };
        }

        /// How far two boxes overlap (degrees of yaw, of pitch); negative is a gap.
        public static Vector2 Overlap(UiFootprint a, UiFootprint b) => new Vector2(
            Mathf.Min(a.YawMax, b.YawMax) - Mathf.Max(a.YawMin, b.YawMin),
            Mathf.Min(a.PitchMax, b.PitchMax) - Mathf.Max(a.PitchMin, b.PitchMin));

        /// They overlap by more than `epsilon` in both directions.
        public static bool Overlaps(UiFootprint a, UiFootprint b, float epsilon = EdgeEpsilonDeg)
        {
            var o = Overlap(a, b);
            return o.x > epsilon && o.y > epsilon;
        }

        /// A side slot's inner edge over the main window: tolerated when the side surface sits in a side slot (≥ ~5 cm
        /// behind the main slot) and overlaps it by ≤ 6° of yaw (the main window is in front).
        public static bool ToleratedSideEdge(in UiSurface a, in UiSurface b, Vector2 overlap)
        {
            bool aMain = Main.Holds(a.Distance, a.DownDeg, a.YawDeg), bMain = Main.Holds(b.Distance, b.DownDeg, b.YawDeg);
            if (aMain == bMain) return false;
            var side = aMain ? b : a;
            bool inSideSlot = SideLeft.Holds(side.Distance, side.DownDeg, side.YawDeg) || SideRight.Holds(side.Distance, side.DownDeg, side.YawDeg);
            return inSideSlot && side.Distance >= Main.Distance + SideSlotBehind - 0.01f && overlap.x <= SideSlotEdgeDeg;
        }

        /// "A × B" with the names in ordinal order (a pair's key).
        public static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? $"{a} × {b}" : $"{b} × {a}";

        /// Every pair of head-relative surface groups (UiSurface.Group) that overlaps: they share a mode, neither is
        /// `apart` from the other (never up together, e.g. the pill yields to a window), and the overlap is more than an
        /// edge touching and more than a tolerated side-slot edge. Sorted by key; the overlap is the largest over the
        /// groups' members. Allocates (tests and the harness only).
        public static List<(string key, Vector2 overlap)> Collisions(IList<UiSurface> surfaces, Func<string, string, bool> apart = null)
        {
            var best = new Dictionary<string, Vector2>();
            for (int i = 0; i < surfaces.Count; i++)
            for (int j = i + 1; j < surfaces.Count; j++)
            {
                var a = surfaces[i];
                var b = surfaces[j];
                string ga = a.Group, gb = b.Group;
                if (ga == gb || (a.Modes & b.Modes) == UiModes.None) continue;
                if (apart != null && (apart(ga, gb) || apart(gb, ga))) continue;
                var o = Overlap(a.Footprint(), b.Footprint());
                if (o.x <= EdgeEpsilonDeg || o.y <= EdgeEpsilonDeg || ToleratedSideEdge(a, b, o)) continue;
                string key = PairKey(ga, gb);
                if (!best.TryGetValue(key, out var old) || o.x * o.y > old.x * old.y) best[key] = o;
            }
            var list = new List<(string key, Vector2 overlap)>();
            foreach (var kv in best) list.Add((kv.Key, kv.Value));
            list.Sort((x, y) => string.CompareOrdinal(x.key, y.key));
            return list;
        }
    }

    /// What is up on screen at one moment (AgentHarness.Surfaces(), declutter §6 "census"). Plain counts.
    public struct UiCounts
    {
        /// Heads-up surfaces showing (status line, toast, reply card, job rail).
        public int Hud;
        public int Main, SideLeft, SideRight, Pill, Enter;
        /// Every head-relative surface showing (the heads-up ones included; the credit chip and the limits chip's head
        /// fallback count too).
        public int HeadRelative;
        public int GrokLayers;
        /// World labels (−1: not counted).
        public int Labels;
        public int Overlaps;
    }

    /// One row of the §2 screen-real-estate budget: the most of each that may be up during an activity.
    public readonly struct UiBudget
    {
        public readonly string Activity;
        public readonly int Hud, Main, SideLeft, SideRight, Pill, HeadRelative, Labels;

        public UiBudget(string activity, int hud, int main, int sideLeft, int sideRight, int pill, int headRelative, int labels)
        {
            Activity = activity; Hud = hud; Main = main; SideLeft = sideLeft; SideRight = sideRight; Pill = pill;
            HeadRelative = headRelative; Labels = labels;
        }

        /// Declutter §2 (maxima; "–" is 0). Passthrough entry's head-relative 2 is the line and the Enter control.
        public static readonly UiBudget[] Table =
        {
            new UiBudget("passthrough-entry", 1, 0, 0, 0, 0, 2, 0),
            new UiBudget("take-it-home", 1, 1, 0, 0, 1, 2, 4),
            new UiBudget("tabletop", 1, 1, 0, 1, 1, 3, 0),   // modelview: Model view shows the model only (no world labels)
            new UiBudget("measuring", 1, 0, 1, 0, 1, 3, 12),
            new UiBudget("finding-parts", 1, 0, 1, 0, 1, 3, 12),
            new UiBudget("checkout", 1, 1, 0, 0, 0, 2, 6),
            new UiBudget("grok-job", 1, 1, 1, 0, 0, 3, 12),
            new UiBudget("coaching", 1, 1, 1, 0, 0, 3, 8),
            new UiBudget("survey", 1, 1, 1, 0, 0, 3, 12),
            new UiBudget("settings", 1, 1, 0, 1, 0, 3, 12),
        };

        public static bool TryFind(string activity, out UiBudget budget)
        {
            foreach (var b in Table)
                if (string.Equals(b.Activity, activity, StringComparison.OrdinalIgnoreCase)) { budget = b; return true; }
            budget = default;
            return false;
        }

        /// The activities, comma-separated (for messages).
        public static string Activities()
        {
            var names = new string[Table.Length];
            for (int i = 0; i < Table.Length; i++) names[i] = Table[i].Activity;
            return string.Join(", ", names);
        }

        /// Does `c` fit this row and the §2 invariants (0 or 1 heads-up surface, no overlaps, ≤ 1 Grok layer, ≤ 12
        /// labels)? `why` names every excess ("" when it fits). Pure.
        public bool Check(in UiCounts c, out string why)
        {
            var over = new List<string>();
            void Max(string what, int have, int max) { if (have > max) over.Add($"{what} {have} > {max}"); }
            Max("hud", c.Hud, Hud);
            Max("main", c.Main, Main);
            Max("sideL", c.SideLeft, SideLeft);
            Max("sideR", c.SideRight, SideRight);
            Max("pill", c.Pill, Pill);
            Max("head-relative", c.HeadRelative, HeadRelative);
            if (c.Labels >= 0) Max("labels", c.Labels, Labels);
            Max("hud (invariant)", c.Hud, 1);
            Max("grok layers", c.GrokLayers, 1);
            Max("overlaps", c.Overlaps, 0);
            why = string.Join(", ", over);
            return over.Count == 0;
        }
    }
}
