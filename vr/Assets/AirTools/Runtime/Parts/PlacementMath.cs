using System;
using System.Text;
using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// Which point of a part stays put when its model is swapped (and what a saved placement stores): a replacement in a
    /// cavity keeps its front flush with the cabinets (the insert contract: front-bottom-centre at cavity.insert.p); on a
    /// wall, a floor or a counter it keeps its back and bottom where they were.
    public enum PlacementAnchor { FrontBottomCentre, BottomBackCentre, BoundsCentre, FrontCentre /* assetgen: a taped opening */ }

    /// The axes the placement editor moves a part along, in SceneRoot space: Right (the viewer's right as they face the
    /// placement), Up, and Out (toward the viewer: out of a cavity's opening, out of a wall). Right = Cross(Out, Up), so
    /// (Right, Up, −Out) is a proper Unity frame (a viewer looking in) and (−Right, Up, Out) is the part facing out.
    public struct PlacementFrame
    {
        public Vector3 Origin, Right, Up, Out;
        /// "cavity", "surface" or "view" (world up and the user's facing).
        public string Kind;

        /// The rotation of a part standing upright and facing out (+Z = Out, +Y = Up).
        public Quaternion Facing => PlacementMath.FromBasis(-Right, Up, Out);
        /// The viewer's frame (+X = Right, +Y = Up, +Z = into the placement): Turn / Tilt / Roll are Euler angles in it.
        public Quaternion View => PlacementMath.FromBasis(Right, Up, -Out);

        /// A vector in (right, up, out) components.
        public Vector3 Local(Vector3 v) => new Vector3(Vector3.Dot(v, Right), Vector3.Dot(v, Up), Vector3.Dot(v, Out));
        /// (right, up, out) components back to a vector.
        public Vector3 Vector(Vector3 rud) => Right * rud.x + Up * rud.y + Out * rud.z;

        public override string ToString() => $"{Kind} o={Origin.ToString("F3")} r={Right.ToString("F2")} u={Up.ToString("F2")} out={Out.ToString("F2")}";
    }

    /// A package (scene-file) frame inside SceneRoot: root = Position + Rotation · (Scale · package). Saved placements
    /// live in package coordinates, so they stay on the same scene feature through a re-scale ("set scale"), a reload
    /// of the same site / revision and the tabletop (which scales SceneRoot itself).
    public struct PackageSpace
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float Scale;

        public static PackageSpace Identity => new PackageSpace { Position = Vector3.zero, Rotation = Quaternion.identity, Scale = 1f };

        public Vector3 ToRoot(Vector3 p) => Position + Rotation * (p * Scale);
        public Vector3 ToPackage(Vector3 r) => PlacementMath.Conj(Rotation) * (r - Position) / (Mathf.Abs(Scale) > 1e-9f ? Scale : 1f);
        public Vector3 DirToRoot(Vector3 d) => Rotation * d;
        public Vector3 DirToPackage(Vector3 d) => PlacementMath.Conj(Rotation) * d;
        public Quaternion RotToRoot(Quaternion q) => PlacementMath.Normalize(Rotation * q);
        public Quaternion RotToPackage(Quaternion q) => PlacementMath.Normalize(PlacementMath.Conj(Rotation) * q);
    }

    /// A cavity in SceneRoot space, as an oriented box: its centre, axes (the placement frame's Right, Up, Out) and
    /// half-extents along them.
    public struct CavityVolume
    {
        public Vector3 Centre, Right, Up, Out, Half;
        public bool OpenTop;
    }

    /// How a part's box sits in a cavity (metres, SceneRoot space; negative = into the wall / sticking out). Sides are
    /// measured on the part's axis-aligned extent in the cavity frame, so a turned part is judged by what it sweeps.
    public struct CavityClearance
    {
        public float Left, Right, Bottom, Top, Back, Front;
        /// The part's extent along the cavity's Right, Up and Out, and the opening's.
        public float PartW, PartH, PartD, GapW, GapH, GapD;
        public bool OpenTop;
        public float SpareW => GapW - PartW;
        public float SpareH => GapH - PartH;
        public override string ToString() =>
            $"L {Left * 1000f:0} R {Right * 1000f:0} B {Bottom * 1000f:0} T {Top * 1000f:0} back {Back * 1000f:0} front {Front * 1000f:0} mm; part {PartW * 1000f:0}×{PartH * 1000f:0}×{PartD * 1000f:0} in {GapW * 1000f:0}×{GapH * 1000f:0}×{GapD * 1000f:0}";
    }

    /// The placement editor's maths (docs: PlacementEditor). Pure: no engine calls — Quaternion * Quaternion / Vector3
    /// and the struct maths are managed; LookRotation, AngleAxis, Inverse, Euler and Slerp are not, so the quaternion
    /// helpers are written out here — and it all runs in the offline test runner.
    public static class PlacementMath
    {
        // ---------------- quaternions ----------------

        /// The rotation by `deg` about `axis` (Unity's convention: Quaternion.AngleAxis).
        public static Quaternion AxisAngle(Vector3 axis, float deg)
        {
            float m = axis.magnitude;
            if (m < 1e-9f || Mathf.Abs(deg) < 1e-9f) return Quaternion.identity;
            axis /= m;
            float h = deg * Mathf.Deg2Rad * 0.5f, s = Mathf.Sin(h);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(h));
        }

        /// The inverse of a unit quaternion.
        public static Quaternion Conj(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        public static Quaternion Normalize(Quaternion q)
        {
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return n > 1e-9f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : Quaternion.identity;
        }

        /// The rotation whose +X, +Y, +Z are the given orthonormal axes (PlacePartMath.FromBasis).
        public static Quaternion FromBasis(Vector3 x, Vector3 y, Vector3 z) => PlacePartMath.FromBasis(x, y, z);

        /// Unity's Euler order (Y · X · Z, degrees) of `q`: x = pitch about +X, y = yaw about +Y, z = roll about +Z.
        public static Vector3 EulerYXZ(Quaternion q)
        {
            q = Normalize(q);
            float sx = Mathf.Clamp(2f * (q.w * q.x - q.y * q.z), -1f, 1f);
            float x = Mathf.Asin(sx);
            float y = Mathf.Atan2(2f * (q.x * q.z + q.w * q.y), 1f - 2f * (q.x * q.x + q.y * q.y));
            float z = Mathf.Atan2(2f * (q.x * q.y + q.w * q.z), 1f - 2f * (q.x * q.x + q.z * q.z));
            return new Vector3(x, y, z) * Mathf.Rad2Deg;
        }

        /// Y(y) · X(x) · Z(z): the inverse of EulerYXZ.
        public static Quaternion FromEulerYXZ(Vector3 e) =>
            Normalize(AxisAngle(Vector3.up, e.y) * AxisAngle(Vector3.right, e.x) * AxisAngle(Vector3.forward, e.z));

        /// An angle in (−180, 180].
        public static float Wrap180(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            else if (deg <= -180f) deg += 360f;
            return deg;
        }

        // ---------------- frames ----------------

        static Vector3 Unit(Vector3 v, Vector3 fallback) => v.sqrMagnitude > 1e-12f ? v.normalized : fallback;

        /// Up as given (normalised); Out = `outward` made perpendicular to it (fallbacks: +Z, then +X); Right from them.
        public static PlacementFrame Make(Vector3 origin, Vector3 up, Vector3 outward, string kind)
        {
            up = Unit(up, Vector3.up);
            var o = outward - up * Vector3.Dot(outward, up);
            if (o.sqrMagnitude < 1e-10f) o = Vector3.forward - up * Vector3.Dot(Vector3.forward, up);
            if (o.sqrMagnitude < 1e-10f) o = Vector3.right - up * Vector3.Dot(Vector3.right, up);
            o.Normalize();
            var r = Vector3.Cross(o, up).normalized;
            return new PlacementFrame { Origin = origin, Right = r, Up = up, Out = o, Kind = kind };
        }

        /// A cavity's frame: origin at its insert point (floor, front, centre of the opening), up, out of the opening.
        public static PlacementFrame Cavity(Vector3 insert, Vector3 up, Vector3 outOfOpening) => Make(insert, up, outOfOpening, "cavity");

        /// The surface under a part: a floor, counter or ceiling (|normal · up| > 0.7) takes the surface's up and the
        /// direction toward the viewer as Out; a wall takes world up and its own normal as Out.
        public static PlacementFrame Surface(Vector3 point, Vector3 normal, Vector3 up, Vector3 toViewer)
        {
            up = Unit(up, Vector3.up);
            normal = Unit(normal, up);
            float d = Vector3.Dot(normal, up);
            if (Mathf.Abs(d) > 0.7f) return Make(point, d > 0f ? normal : -normal, toViewer, "surface");
            return Make(point, up, normal, "surface");
        }

        /// Nothing under it: world up and the user's facing (Out points back at the user).
        public static PlacementFrame View(Vector3 point, Vector3 up, Vector3 toViewer) => Make(point, up, toViewer, "view");

        /// The frame in the other space (a PackageSpace maps it rigidly and uniformly, so Right stays Cross(Out, Up)).
        public static PlacementFrame ToRoot(PlacementFrame f, PackageSpace s) =>
            Make(s.ToRoot(f.Origin), s.DirToRoot(f.Up), s.DirToRoot(f.Out), f.Kind);

        public static PlacementFrame ToPackage(PlacementFrame f, PackageSpace s) =>
            Make(s.ToPackage(f.Origin), s.DirToPackage(f.Up), s.DirToPackage(f.Out), f.Kind);

        // ---------------- anchors ----------------

        /// The anchor point in part space (PartMath: +Y up, +Z the front).
        public static Vector3 AnchorLocal(Bounds box, PlacementAnchor a)
        {
            switch (a)
            {
                case PlacementAnchor.FrontBottomCentre: return new Vector3(box.center.x, box.min.y, box.max.z);
                case PlacementAnchor.BottomBackCentre: return new Vector3(box.center.x, box.min.y, box.min.z);
                case PlacementAnchor.FrontCentre: return new Vector3(box.center.x, box.center.y, box.max.z);   // assetgen
                default: return box.center;
            }
        }

        /// Where the part's origin goes so its anchor sits at `anchor`, turned by `rotation`.
        public static Vector3 OriginFor(Vector3 anchor, Quaternion rotation, Bounds box, PlacementAnchor a) => anchor - rotation * AnchorLocal(box, a);

        /// Where the anchor of a part at (origin, rotation) is.
        public static Vector3 AnchorOf(Vector3 origin, Quaternion rotation, Bounds box, PlacementAnchor a) => origin + rotation * AnchorLocal(box, a);

        // ---------------- nudges ----------------

        /// A nudge of (right, up, out) metres along the frame.
        public static Vector3 Move(PlacementFrame f, Vector3 anchor, Vector3 rud) => anchor + f.Vector(rud);

        /// The rotation relative to the fit pose, as Turn / Tilt / Roll in degrees: turn about Up (clockwise seen from
        /// above), tilt about Right (the top tips away from you), roll about Out (clockwise as you face it). Euler order
        /// Y · X · Z in the viewer's frame, so each is the angle a button press adds.
        public static Quaternion Rotation(PlacementFrame f, Quaternion fitRotation, Vector3 turnTiltRoll)
        {
            var v = f.View;
            var e = FromEulerYXZ(new Vector3(turnTiltRoll.y, turnTiltRoll.x, -turnTiltRoll.z));
            return Normalize(v * e * Conj(v) * fitRotation);
        }

        /// The inverse: (turn, tilt, roll) in degrees of `rotation` relative to `fitRotation` in the frame.
        public static Vector3 TurnTiltRoll(PlacementFrame f, Quaternion fitRotation, Quaternion rotation)
        {
            var v = f.View;
            var d = Conj(v) * rotation * Conj(fitRotation) * v;
            var e = EulerYXZ(d);
            return new Vector3(Wrap180(e.y), Wrap180(e.x), Wrap180(-e.z));
        }

        /// Offset of the anchor from the fit anchor in (right, up, out) metres.
        public static Vector3 Offset(PlacementFrame f, Vector3 anchor, Vector3 fitAnchor) => f.Local(anchor - fitAnchor);

        // ---------------- grab ----------------

        /// The signed angle (degrees) from `a` to `b` about `up`, both flattened onto the plane across it (0 when either
        /// is along it): clockwise seen from above is positive, like AxisAngle(up, +deg).
        public static float SignedYaw(Vector3 a, Vector3 b, Vector3 up)
        {
            up = Unit(up, Vector3.up);
            var fa = a - up * Vector3.Dot(a, up);
            var fb = b - up * Vector3.Dot(b, up);
            if (fa.sqrMagnitude < 1e-10f || fb.sqrMagnitude < 1e-10f) return 0f;
            fa.Normalize(); fb.Normalize();
            float cos = Mathf.Clamp(Vector3.Dot(fa, fb), -1f, 1f);
            float sin = Vector3.Dot(Vector3.Cross(fa, fb), up);
            return Mathf.Atan2(sin, cos) * Mathf.Rad2Deg;
        }

        /// A pinch-grab (SceneRoot space): the grabbed point moves rigidly with the pointer (the hand and its ray,
        /// keeping the grab offset), and the part turns with the pointer about Up (yawOnly: its tilt and roll stay; the
        /// ray has no roll, so a free grab would tip the part as you aim up or down). Returns the anchor and the turn
        /// added (degrees); the full rotation delta when !yawOnly.
        public static Vector3 GrabAnchor(Pose pointer0, Pose pointer, Vector3 grab0, Vector3 anchor0, Vector3 up, bool yawOnly,
            out float addedTurn, out Quaternion delta)
        {
            var d = Normalize(pointer.rotation * Conj(pointer0.rotation));
            var grab = pointer.position + d * (grab0 - pointer0.position);
            addedTurn = SignedYaw(pointer0.rotation * Vector3.forward, pointer.rotation * Vector3.forward, up);
            delta = yawOnly ? AxisAngle(up, addedTurn) : d;
            return grab + delta * (anchor0 - grab0);
        }

        // ---------------- snapping ----------------

        /// Snap (on release) to the fit pose when within `metres` and every angle within `degrees` of it.
        public static bool NearFit(Vector3 offset, Vector3 turnTiltRoll, float metres = 0.03f, float degrees = 6f) =>
            offset.magnitude <= metres && Mathf.Abs(turnTiltRoll.x) <= degrees && Mathf.Abs(turnTiltRoll.y) <= degrees && Mathf.Abs(turnTiltRoll.z) <= degrees;

        /// Snap small offsets and angles to the cavity or surface: an offset within `metres` of 0 along an axis in `axes`
        /// (bits: 1 right, 2 up, 4 out) goes to 0 (centred, on the floor, flush), a turn within `degrees` of a multiple of
        /// 90° goes to it, a tilt or roll within `degrees` of 0 goes to 0. Returns true if anything moved.
        public static bool SnapSmall(ref Vector3 offset, ref Vector3 turnTiltRoll, int axes = 7, float metres = 0.012f, float degrees = 4f)
        {
            bool any = false;
            for (int i = 0; i < 3; i++)
                if ((axes & (1 << i)) != 0 && offset[i] != 0f && Mathf.Abs(offset[i]) <= metres) { offset[i] = 0f; any = true; }
            float turn = Mathf.Round(turnTiltRoll.x / 90f) * 90f;
            if (turnTiltRoll.x != turn && Mathf.Abs(turnTiltRoll.x - turn) <= degrees) { turnTiltRoll.x = Wrap180(turn); any = true; }
            for (int i = 1; i < 3; i++)
                if (turnTiltRoll[i] != 0f && Mathf.Abs(turnTiltRoll[i]) <= degrees) { turnTiltRoll[i] = 0f; any = true; }
            return any;
        }

        // ---------------- steps (D2 units) ----------------

        const float MetresPerInch = 0.0254f;

        /// A nudge step in the user's unit: ⅜″ (fine ⅛″) in imperial, 1 cm (fine 2 mm) in metric — steps that add up
        /// exactly in the readout (a 2 mm step would read ⅛″, then ¼″ after two presses).
        public static float StepMetres(UnitSystem u, bool fine) =>
            u == UnitSystem.Metric ? (fine ? 0.002f : 0.01f) : (fine ? MetresPerInch / 8f : MetresPerInch * 3f / 8f);

        public static float StepDegrees(bool fine) => fine ? 1f : 5f;

        /// "Step ⅜″ · 5°" / "Fine ⅛″ · 1°" / "Step 1 cm · 5°" / "Fine 2 mm · 1°" (the step chip).
        public static string StepLabel(UnitSystem u, bool fine)
        {
            string len = u == UnitSystem.Metric ? (fine ? "2 mm" : "1 cm") : (fine ? "⅛″" : "⅜″");
            return $"{(fine ? "Fine" : "Step")} {len} · {StepDegrees(fine):0}°";
        }

        // ---------------- readout (no allocation) ----------------

        static readonly string[] s_Eighths = { "", "⅛", "¼", "⅜", "½", "⅝", "¾", "⅞" };
        const string Tab = "<mspace=0.58em>", TabEnd = "</mspace>";

        /// The quantum the readout shows (eighths of an inch, or millimetres): a new text only when one changes.
        public static int Quantum(float metres, UnitSystem u) =>
            u == UnitSystem.Metric ? Mathf.RoundToInt(metres * 1000f) : Mathf.RoundToInt(metres / MetresPerInch * 8f);

        /// Digits of a non-negative int, tabular, without allocating.
        public static void AppendDigits(StringBuilder sb, int v)
        {
            if (v < 0) v = -v;
            sb.Append(Tab);
            if (v == 0) sb.Append('0');
            else
            {
                int div = 1;
                while (v / div >= 10) div *= 10;
                for (; div > 0; div /= 10) sb.Append((char)('0' + (v / div) % 10));
            }
            sb.Append(TabEnd);
        }

        /// |metres| in the user's unit, no sign: "1⅜″", "0″", "⅛″", "1′ 2⅜″" / "12 mm", "1.25 m" (tabular digits).
        public static void AppendLength(StringBuilder sb, float metres, UnitSystem u)
        {
            int q = Mathf.Abs(Quantum(metres, u));
            if (u == UnitSystem.Metric)
            {
                if (q < 1000) { AppendDigits(sb, q); sb.Append(" mm"); return; }
                int cm = Mathf.RoundToInt(q / 10f);
                AppendDigits(sb, cm / 100); sb.Append('.');
                sb.Append(Tab).Append((char)('0' + (cm / 10) % 10)).Append((char)('0' + cm % 10)).Append(TabEnd).Append(" m");
                return;
            }
            int feet = q / 96, rem = q % 96, inches = rem / 8, eighth = rem % 8;
            if (feet > 0) { AppendDigits(sb, feet); sb.Append("′ "); }
            if (inches > 0 || eighth == 0 || feet > 0) AppendDigits(sb, inches);
            sb.Append(s_Eighths[eighth]).Append('″');
        }

        /// Whole degrees with a real minus: "5°", "−12°".
        public static void AppendDegrees(StringBuilder sb, float deg)
        {
            int d = Mathf.RoundToInt(deg);
            if (d < 0) sb.Append(Units.Minus);
            AppendDigits(sb, d);
            sb.Append('°');
        }

        /// Line 1: "Right 1⅜″ · Up 0″ · Out ¼″" (Left / Down / In when negative), the anchor's offset from the fit.
        public static void AppendOffset(StringBuilder sb, Vector3 rud, UnitSystem u)
        {
            sb.Append(Quantum(rud.x, u) < 0 ? "Left " : "Right ");
            AppendLength(sb, rud.x, u);
            sb.Append(" · ").Append(Quantum(rud.y, u) < 0 ? "Down " : "Up ");
            AppendLength(sb, rud.y, u);
            sb.Append(" · ").Append(Quantum(rud.z, u) < 0 ? "In " : "Out ");
            AppendLength(sb, rud.z, u);
        }

        /// Line 2: "Turn 5° · Tilt 0° · Roll −2°".
        public static void AppendTurn(StringBuilder sb, Vector3 turnTiltRoll)
        {
            sb.Append("Turn ");
            AppendDegrees(sb, turnTiltRoll.x);
            sb.Append(" · Tilt ");
            AppendDegrees(sb, turnTiltRoll.y);
            sb.Append(" · Roll ");
            AppendDegrees(sb, turnTiltRoll.z);
        }

        /// Both lines, as the panel shows them.
        public static void AppendReadout(StringBuilder sb, Vector3 rud, Vector3 turnTiltRoll, UnitSystem u)
        {
            AppendOffset(sb, rud, u);
            sb.Append('\n');
            AppendTurn(sb, turnTiltRoll);
        }

        /// The readout without the tabular markup (logs, the harness, tests).
        public static string Plain(StringBuilder sb) => sb.ToString().Replace(Tab, "").Replace(TabEnd, "");

        // ---------------- the fit in a cavity ----------------

        /// The part's box (part space) at (origin, rotation) against the cavity: clearance on every side.
        public static CavityClearance Clearance(CavityVolume c, Bounds box, Vector3 origin, Quaternion rotation)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            var bmin = box.min; var bmax = box.max;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? bmin.x : bmax.x, (i & 2) == 0 ? bmin.y : bmax.y, (i & 4) == 0 ? bmin.z : bmax.z);
                var p = origin + rotation * corner - c.Centre;
                var q = new Vector3(Vector3.Dot(p, c.Right), Vector3.Dot(p, c.Up), Vector3.Dot(p, c.Out));
                min = Vector3.Min(min, q);
                max = Vector3.Max(max, q);
            }
            var h = c.Half;
            return new CavityClearance
            {
                Left = min.x + h.x, Right = h.x - max.x,
                Bottom = min.y + h.y, Top = h.y - max.y,
                Back = min.z + h.z, Front = h.z - max.z,
                PartW = max.x - min.x, PartH = max.y - min.y, PartD = max.z - min.z,
                GapW = 2f * h.x, GapH = 2f * h.y, GapD = 2f * h.z,
                OpenTop = c.OpenTop,
            };
        }

        /// The verdict in a cavity (the fit colours and words): red when it's wider or taller than the opening, or pokes
        /// into a side, the floor, the top or the back by more than `tolerance`; amber when it stands proud of the front
        /// by more than `proud` or has under `tight` to spare; green "Fits the gap · ⅜″ spare" otherwise. Metres in, the
        /// user's unit in the words (Copy); Headline stays metric for the logs.
        public static FitReport CavityFit(CavityClearance k, float tolerance = 0.003f, float proud = 0.02f, float tight = 0.003f, string noun = "gap")
        {
            // assetgen: `noun` — "gap" (a removed part's cavity) or "opening" (a taped window opening, OpeningMath.Fit).
            var r = new FitReport { Surface = $"the {noun}", OpeningSource = noun == "gap" ? "cavity" : noun, OpeningMm = k.GapW * 1000f };
            float Mm(float m) => m * 1000f;
            string G(float m) => Copy.Gap(Mm(m));
            float spare = k.OpenTop ? k.SpareW : Mathf.Min(k.SpareW, k.SpareH);
            r.SpareMm = Mm(spare);
            string Gap = noun == "gap" ? "Gap" : Copy.Cap(noun);   // assetgen: "Opening 4′ 11″ · part 4′ 11½″"
            if (k.SpareW < -tolerance)
            {
                r.Add(FitStatus.Red, $"Too wide by {Mm(-k.SpareW):0} mm for the {Mm(k.GapW):0} mm {noun}");
                r.Say(FitStatus.Red, $"Too wide by {G(-k.SpareW)}", $"{Gap} {Copy.Len(k.GapW)} · part {Copy.Len(k.PartW)}", FitAction.FindSmaller, "Find a smaller one");
            }
            if (!k.OpenTop && k.SpareH < -tolerance)
            {
                r.Add(FitStatus.Red, $"Too tall by {Mm(-k.SpareH):0} mm for the {Mm(k.GapH):0} mm {noun}");
                r.Say(FitStatus.Red, $"Too tall by {G(-k.SpareH)}", $"{Gap} {Copy.Len(k.GapH)} · part {Copy.Len(k.PartH)}", FitAction.FindSmaller, "Find a smaller one");
            }
            if (k.SpareW >= -tolerance && (k.Left < -tolerance || k.Right < -tolerance))
            {
                bool left = k.Left < k.Right;
                float over = -(left ? k.Left : k.Right);
                r.Add(FitStatus.Red, $"Into the {(left ? "left" : "right")} side by {Mm(over):0} mm");
                r.Say(FitStatus.Red, $"Into the {(left ? "left" : "right")} side by {G(over)}", $"Move it {(left ? "right" : "left")} {G(over)}", FitAction.MoveIt, "Move it");
            }
            if (k.Bottom < -tolerance)
            {
                r.Add(FitStatus.Red, $"Below the floor by {Mm(-k.Bottom):0} mm");
                r.Say(FitStatus.Red, $"Below the floor by {G(-k.Bottom)}", $"Move it up {G(-k.Bottom)}", FitAction.MoveIt, "Move it");
            }
            if (!k.OpenTop && k.SpareH >= -tolerance && k.Top < -tolerance)
            {
                r.Add(FitStatus.Red, $"Into the top by {Mm(-k.Top):0} mm");
                r.Say(FitStatus.Red, $"Into the top by {G(-k.Top)}", $"Move it down {G(-k.Top)}", FitAction.MoveIt, "Move it");
            }
            if (k.Back < -tolerance)
            {
                r.Add(FitStatus.Red, $"Into the back wall by {Mm(-k.Back):0} mm");
                r.Say(FitStatus.Red, $"Into the back by {G(-k.Back)}", $"Move it out {G(-k.Back)}", FitAction.MoveIt, "Move it");
            }
            if (k.Front < -proud)
            {
                r.Add(FitStatus.Amber, $"Stands {Mm(-k.Front):0} mm proud of the opening");
                r.Say(FitStatus.Amber, $"Sticks out {G(-k.Front)}", noun == "gap" ? $"Gap {Copy.Len(k.GapD)} deep · part {Copy.Len(k.PartD)}" : "Push it in flush with the wall",
                    FitAction.SeeOthers, "See other models");
            }
            if (spare >= -tolerance && spare < tight)
            {
                r.Add(FitStatus.Amber, $"Tight: {Mm(spare):0} mm spare");
                r.Say(FitStatus.Amber, $"Tight · {G(Mathf.Max(0f, spare))} spare", $"Check the {noun} with the tape", FitAction.MeasureIt, "Measure it");
            }
            if (r.Status == FitStatus.Green)
            {
                r.Add(FitStatus.Green, $"Fits the {Mm(k.GapW):0} mm {noun}, {Mm(spare):0} mm spare");
                r.Say(FitStatus.Green, $"Fits the {noun} · {G(spare)} spare", k.Front < -0.001f ? $"Stands {G(-k.Front)} proud" : "Sits in the opening",
                    FitAction.ComparePrices, "Compare prices", "price");
            }
            r.Lines.Add(k.ToString());
            return r;
        }

        // ---------------- slots and keys ----------------

        /// The slot names (the chips): A, B, C, D.
        public static readonly string[] Slots = { "A", "B", "C", "D" };

        /// A slot name from what was said or pressed ("a", "B", "slot c", "2" → B); null when it isn't one.
        public static string SlotName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().ToUpperInvariant();
            if (s.StartsWith("SLOT ", StringComparison.Ordinal)) s = s.Substring(5).Trim();
            if (s.StartsWith("PLACEMENT ", StringComparison.Ordinal)) s = s.Substring(10).Trim();
            if (s.Length == 1 && s[0] >= 'A' && s[0] <= 'Z') return s;
            if (int.TryParse(s, out int n) && n >= 1 && n <= 26) return ((char)('A' + n - 1)).ToString();
            return null;
        }

        /// The first of A–D not in use; when all four are, null (the caller overwrites the active one).
        public static string NextSlot(Func<string, bool> used)
        {
            foreach (var s in Slots) if (used == null || !used(s)) return s;
            return null;
        }

        /// Where saved placements are filed: the site and revision, and the cavity ("cavity:dw1") or the spot.
        public static string Key(string site, int revision, string target) => $"{(string.IsNullOrEmpty(site) ? "built-in" : site)}#r{revision}/{target}";

        // ---------------- glTF pose (the notebook, place_part) ----------------

        /// A package-space (Unity axes) pose as place_part's glTF pose: p (X negated) and q (x, −y, −z, w).
        public static double[] GltfPose(Vector3 position, Quaternion rotation)
        {
            var q = Normalize(rotation);
            return new double[] { -position.x, position.y, position.z, q.x, -q.y, -q.z, q.w };
        }
    }
}
