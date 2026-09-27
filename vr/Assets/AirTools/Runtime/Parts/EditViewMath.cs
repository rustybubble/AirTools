using AirTools.Tools;
using UnityEngine;

namespace AirTools.Parts
{
    /// edit6dof: where the Edit view is (docs/edit-view.md).
    ///   Idle → Opening (the item flies to the centre) → Orient (arrows, colour, buttons)
    ///   Orient → Closing (Save keeps the edits, Cancel reverts them; it flies back) → Idle
    ///   Orient → ToRoom (Move: it flies back) → Moving (drag it round the room) → Save / Cancel → Idle
    ///   a new part: Orient → Placing (Place: it rides the pointer, PartTool) → placed (auto-saves) → Idle
    public enum EditPhase { Idle, Opening, Orient, ToRoom, Moving, Placing, Closing }

    /// A placed part being edited, or a new one (from the Catalog) being oriented before it's placed.
    public enum EditKind { Placed, New }

    public enum EditStep { OpenPlaced, OpenNew, Opened, Move, Arrived, Place, Placed, Save, Cancel, Closed, Abort }

    /// edit6dof: the Edit view's state machine. Pure; the view does what each step means.
    public sealed class EditFlow
    {
        public EditPhase Phase { get; private set; } = EditPhase.Idle;
        public EditKind Kind { get; private set; }
        /// The last step that ended a session kept the edits (Save, a placement) or reverted them (Cancel).
        public bool Keep { get; private set; }
        public bool Active => Phase != EditPhase.Idle;
        public int Steps { get; private set; }

        public bool Can(EditStep s) => Next(Phase, Kind, s, out _, out _);

        /// Take a step; false (nothing changes) when it doesn't apply now.
        public bool Fire(EditStep s)
        {
            if (!Next(Phase, Kind, s, out var next, out bool keep)) return false;
            if (s == EditStep.OpenPlaced) Kind = EditKind.Placed;
            else if (s == EditStep.OpenNew) Kind = EditKind.New;
            if (s == EditStep.Save || s == EditStep.Cancel || s == EditStep.Placed || s == EditStep.Abort) Keep = keep;
            Phase = next;
            Steps++;
            return true;
        }

        public static bool Next(EditPhase p, EditKind k, EditStep s, out EditPhase next, out bool keep)
        {
            next = p;
            keep = s != EditStep.Cancel;
            switch (p)
            {
                case EditPhase.Idle:
                    if (s == EditStep.OpenPlaced || s == EditStep.OpenNew) { next = EditPhase.Opening; return true; }
                    return false;
                case EditPhase.Opening:
                    if (s == EditStep.Opened) { next = EditPhase.Orient; return true; }
                    if ((s == EditStep.Save && k == EditKind.Placed) || s == EditStep.Cancel) { next = EditPhase.Closing; return true; }
                    break;
                case EditPhase.Orient:
                    if (s == EditStep.Move && k == EditKind.Placed) { next = EditPhase.ToRoom; return true; }
                    if (s == EditStep.Place && k == EditKind.New) { next = EditPhase.Placing; return true; }
                    if ((s == EditStep.Save && k == EditKind.Placed) || s == EditStep.Cancel) { next = EditPhase.Closing; return true; }
                    break;
                case EditPhase.ToRoom:
                    if (s == EditStep.Arrived) { next = EditPhase.Moving; return true; }
                    if (s == EditStep.Save || s == EditStep.Cancel) { next = EditPhase.Idle; return true; }
                    break;
                case EditPhase.Moving:
                    if (s == EditStep.Save || s == EditStep.Cancel) { next = EditPhase.Idle; return true; }
                    break;
                case EditPhase.Placing:
                    if (s == EditStep.Placed || s == EditStep.Cancel) { next = EditPhase.Idle; return true; }
                    break;
                case EditPhase.Closing:
                    if (s == EditStep.Closed) { next = EditPhase.Idle; return true; }
                    break;
            }
            if (s == EditStep.Abort) { next = EditPhase.Idle; keep = true; return true; }
            return false;
        }

        public void Reset() { Phase = EditPhase.Idle; Keep = false; }

        // ---------------- what shows ----------------

        /// The stage (the item at the centre, its arrows and the panel) shows while it's there or flying.
        public static bool ShowsStage(EditPhase p) => p == EditPhase.Opening || p == EditPhase.Orient || p == EditPhase.Closing || p == EditPhase.ToRoom;
        /// The arrows and the panel take input only while the item sits at the centre.
        public static bool Interactive(EditPhase p) => p == EditPhase.Orient;
        /// The move bar (Save / Cancel and the fit, or "Aim and pinch to place") while the item is out in the room.
        public static bool ShowsMoveBar(EditPhase p) => p == EditPhase.Moving || p == EditPhase.Placing || p == EditPhase.ToRoom;
        /// The world stays dimmed through the whole session.
        public static bool Dims(EditPhase p) => p != EditPhase.Idle;
        /// The dim writes depth only while the item sits at the centre (it then draws over any wall nearer than it). While
        /// it flies, and out in the room, it's depth-tested against the room as usual (a part flying in from 5 m would
        /// otherwise be hidden behind the veil's 3 m depth until it came near).
        public static bool DimWritesDepth(EditPhase p) => p == EditPhase.Orient;
        /// The move arrows (right / up / out) only for a placed part (a new one has no room pose yet).
        public static bool ShowsMoveArrows(EditKind k) => k == EditKind.Placed;
    }

    /// edit6dof: one arrow of the Edit view: the axis, its direction, and where it sits round the item (degrees
    /// counter-clockwise from the viewer's right; the inner ring holds the curved ones and Out / In, the outer ring the
    /// straight right / up / left / down).
    public readonly struct EditArrow
    {
        public readonly EditAxis Axis;
        public readonly int Sign;
        public readonly float AngleDeg;
        public readonly bool Outer;
        public readonly string Name;

        public EditArrow(EditAxis axis, int sign, float angleDeg, bool outer, string name)
        {
            Axis = axis; Sign = sign; AngleDeg = angleDeg; Outer = outer; Name = name;
        }

        public bool Curved => Edit6DofMath.IsTurn(Axis);
    }

    /// edit-touch: the Edit view laid out on its knob plane (EditViewMath.Layout; metres, +x right, +y up).
    public struct EditLayout
    {
        /// The item's shown radius, the inner and outer rings (knob centres) and the knob size.
        public float Radius, Inner, Outer, Knob;
        /// How far the item's centre sits behind the knob plane (a turned item stays behind its arrows).
        public float Front;
        /// The ring's centre (the item's) and the panel's centre, from the anchor.
        public Vector2 Ring, Panel;
        /// The readout pill's centre, from the ring's centre.
        public Vector2 Readout;
        /// The whole composition's bounds, from the anchor (centred on it).
        public Vector2 Min, Max;
    }

    /// edit6dof: the Edit view's layout, animation and colour maths. Pure.
    public static class EditViewMath
    {
        // ---------------- the stage (edit-touch: within arm's reach) ----------------

        /// edit-touch: a frame in front of the eye: `distance` along a line `downDeg` below the eye's level heading, turned
        /// `yawDeg` to the right, facing the eye (+Z away from it, +Y up as far as it can be, +X the viewer's right, level).
        public static Pose EyeFrame(Vector3 eye, Vector3 forward, float distance, float downDeg, float yawDeg = 0f)
        {
            var f = forward;
            f.y = 0f;
            f = f.sqrMagnitude > 1e-8f ? f.normalized : Vector3.forward;
            if (Mathf.Abs(yawDeg) > 1e-6f) f = PlacementMath.AxisAngle(Vector3.up, yawDeg) * f;
            float r = downDeg * Mathf.Deg2Rad;
            var dir = (f * Mathf.Cos(r) - Vector3.up * Mathf.Sin(r)).normalized;
            var right = Vector3.Cross(Vector3.up, f).normalized;
            var up = Vector3.Cross(dir, right).normalized;
            return new Pose(eye + dir * distance, PlacementMath.FromBasis(right, up, dir));
        }

        /// edit-touch: the gaps of the layout (m): knob to item, inner to outer ring, knobs to the panel.
        public const float KnobGap = 0.008f, RingGap = 0.006f, PanelGap = 0.018f;

        /// edit-touch: the Edit view laid out on its plane — the knob plane, facing the eye (metres; +x right, +y up): the
        /// ring of arrows round the item, the panel tucked into the ring's lower right (clear of every knob and of the
        /// item's box), the readout pill beside the top knob, and the offsets that centre the whole composition on the
        /// anchor (EditView.touchDistance ahead, EditView.downDeg down). `radius`: half the shown item's largest side.
        public static EditLayout Layout(float radius, bool moves, float knob, Vector2 panel, Vector2 readout)
        {
            var l = new EditLayout { Radius = radius, Knob = knob };
            l.Inner = radius + KnobGap + knob * 0.5f;
            l.Outer = l.Inner + knob + RingGap;
            l.Front = radius;   // its box's near face on the knob plane: in view, it never covers an inner knob
            float ring = (moves ? l.Outer : l.Inner) + knob * 0.5f;
            // Left edge clear of the inner ring's lower-right knob (Out, −45°); top below the knobs at 0° (Turn →, Right).
            float left = l.Inner * 0.70710678f + knob * 0.5f + PanelGap;
            float top = -(knob * 0.5f + PanelGap * 0.75f);
            var p = new Vector2(left + panel.x * 0.5f, top - panel.y * 0.5f);
            // The pill on the top knob's right, at its height (above the inner ring's upper-right knob, Roll ↻).
            var pill = new Vector2(knob * 0.5f + PanelGap * 0.75f + readout.x * 0.5f, moves ? l.Outer : l.Inner + knob);
            var min = new Vector2(-ring, Mathf.Min(-ring, p.y - panel.y * 0.5f));
            var max = new Vector2(Mathf.Max(ring, p.x + panel.x * 0.5f), Mathf.Max(ring, pill.y + readout.y * 0.5f));
            var c = (min + max) * 0.5f;
            l.Ring = -c;
            l.Panel = p - c;
            l.Readout = pill;
            l.Min = min - c;
            l.Max = max - c;
            return l;
        }

        /// edit-touch: the stage (the item's centre, the arrows' holder): the ring's centre on the anchor's plane, `Front`
        /// behind it (the item's near face on the plane), facing the eye as the anchor does.
        public static Pose StagePose(Pose anchor, in EditLayout l) =>
            new Pose(anchor.position + anchor.rotation * new Vector3(l.Ring.x, l.Ring.y, l.Front), anchor.rotation);

        /// edit-touch: the panel: its place on the anchor's plane, drawn in along its line to `distance` from the eye,
        /// facing the eye (upright: its right stays level).
        public static Pose PanelPose(Vector3 eye, Pose anchor, Vector2 onPlane, float distance) =>
            FacingEye(eye, anchor.position + anchor.rotation * new Vector3(onPlane.x, onPlane.y, 0f), distance);

        /// edit-touch: a control seen at `at`, moved along its line from the eye to `distance` from it and turned to face
        /// the eye (upright): the arrows sit on a sphere round the eye, so the ring's edge is as near as its middle, and
        /// each knob is pressed straight in.
        public static Pose FacingEye(Vector3 eye, Vector3 at, float distance)
        {
            var dir = at - eye;
            dir = dir.sqrMagnitude > 1e-10f ? dir.normalized : Vector3.forward;
            var level = new Vector3(dir.x, 0f, dir.z);
            var right = Vector3.Cross(Vector3.up, level.sqrMagnitude > 1e-8f ? level.normalized : Vector3.forward).normalized;
            var up = Vector3.Cross(dir, right).normalized;
            return new Pose(eye + dir * distance, PlacementMath.FromBasis(right, up, dir));
        }

        /// edit-touch: a placed part's context menu within reach: `distance` from the eye, `downDeg` below the eye line, on
        /// the level line to the part turned `sideDeg` to your right (the part stays in view), facing the eye.
        public static Pose MenuPose(Vector3 eye, Vector3 forward, Vector3 part, float distance, float downDeg, float sideDeg)
        {
            var to = part - eye;
            to.y = 0f;
            return EyeFrame(eye, to.sqrMagnitude > 1e-6f ? to : forward, distance, downDeg, sideDeg);
        }

        /// The shown size: a part whose largest side is over `maxSide` (world metres) is shown scaled down to it (a fridge
        /// fits between your hands); smaller parts at true size. The readout keeps the true size.
        public static float FitScale(float largestSide, float maxSide = 0.25f) =>
            largestSide > maxSide && largestSide > 1e-6f ? maxSide / largestSide : 1f;

        /// The turn about Up (degrees) that brings the part's front (its frame's Out, level) round to face the viewer.
        public static float FacingYaw(Vector3 front, Vector3 toViewer) => PlacementMath.SignedYaw(front, toViewer, Vector3.up);

        /// The part as the Edit view shows it: turned by `yaw` and scaled by `scale` about its box centre, which goes to
        /// `centre`. For a part root at (position, rotation) whose box centre is `boxCentre` (world).
        public static Pose Display(Vector3 position, Quaternion rotation, Vector3 boxCentre, Vector3 centre, Quaternion yaw, float scale) =>
            new Pose(centre + yaw * ((position - boxCentre) * scale), PlacementMath.Normalize(yaw * rotation));

        /// An arrow's place on the stage (stage-local): round the item at `inner` (the curved ones, Out, In) or `outer`,
        /// `front` towards the viewer.
        public static Vector3 ArrowPosition(in EditArrow a, float inner, float outer, float front)
        {
            float r = a.Outer ? outer : inner;
            float rad = a.AngleDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(rad) * r, Mathf.Sin(rad) * r, -front);
        }

        /// The twelve arrows. Turn + turns the front to your left (clockwise seen from above), tilt + tips the top away
        /// (the front looks up), roll + is clockwise as you face it (the adjust panel's pads, PlacementMath.Rotation).
        public static readonly EditArrow[] Arrows =
        {
            new EditArrow(EditAxis.Tilt, +1, 90f, false, "TiltUp"),
            new EditArrow(EditAxis.Roll, +1, 45f, false, "RollRight"),
            new EditArrow(EditAxis.Turn, -1, 0f, false, "TurnRight"),
            new EditArrow(EditAxis.Out, +1, -45f, false, "Out"),
            new EditArrow(EditAxis.Tilt, -1, -90f, false, "TiltDown"),
            new EditArrow(EditAxis.Out, -1, -135f, false, "In"),
            new EditArrow(EditAxis.Turn, +1, 180f, false, "TurnLeft"),
            new EditArrow(EditAxis.Roll, -1, 135f, false, "RollLeft"),
            new EditArrow(EditAxis.Up, +1, 90f, true, "Up"),
            new EditArrow(EditAxis.Right, +1, 0f, true, "Right"),
            new EditArrow(EditAxis.Up, -1, -90f, true, "Down"),
            new EditArrow(EditAxis.Right, -1, 180f, true, "Left"),
        };

        /// Does a ray pass through a sphere (the shown item, for its free turn)? `t` along the ray.
        public static bool RaySphere(Vector3 origin, Vector3 dir, Vector3 centre, float radius, out float t)
        {
            dir = dir.sqrMagnitude > 1e-12f ? dir.normalized : Vector3.forward;
            t = Vector3.Dot(centre - origin, dir);
            if (t < 0f) return Vector3.Distance(origin, centre) <= radius;
            return Vector3.Distance(origin + dir * t, centre) <= radius;
        }

        // ---------------- animation ----------------

        /// Ease in and out (cubic).
        public static float Ease(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f ? 4f * t * t * t : 1f - (-2f * t + 2f) * (-2f * t + 2f) * (-2f * t + 2f) * 0.5f;
        }

        /// A pose part way from `a` to `b` (`t` already eased).
        public static Pose Blend(Pose a, Pose b, float t) =>
            new Pose(Vector3.LerpUnclamped(a.position, b.position, t), OneEuroRotation.Nlerp(a.rotation, b.rotation, t));

        /// How long the fly takes: `seconds`, or at once with Reduce motion.
        public static float FlySeconds(bool reduceMotion, float seconds = 0.5f) => reduceMotion ? 0f : Mathf.Max(0f, seconds);

        /// Progress of a fly that started at `start` (1 when done; a zero-length fly is done at once).
        public static float Progress(float now, float start, float seconds) => seconds <= 0f ? 1f : Mathf.Clamp01((now - start) / seconds);

        // ---------------- the context gesture ----------------

        /// A long pinch / trigger hold on a placed part opens its menu only when the tool in hand doesn't use presses on
        /// the scene for itself (measuring, levels, ladders): with the Part tool, Move or nothing in hand. The controller's
        /// grip (squeeze), unused elsewhere, opens it with any tool.
        public static bool HoldOpensMenu(ToolKind tool) => tool == ToolKind.None || tool == ToolKind.Part || tool == ToolKind.Move;
    }

    /// edit6dof: a colour shade over a part's texture (the Edit view's swatches): the model's base colour blended toward the
    /// swatch by `Strength` — a multiply over the texture, so the grain and detail stay. Default: none (the original).
    public struct PartShade
    {
        public string Name;
        public Color Color;
        public float Strength;

        public bool IsOriginal => string.IsNullOrEmpty(Name) || Strength <= 0f;

        public bool Same(PartShade o) =>
            (IsOriginal && o.IsOriginal) || (Name == o.Name && Mathf.Abs(Strength - o.Strength) < 1e-4f
                                             && Mathf.Abs(Color.r - o.Color.r) + Mathf.Abs(Color.g - o.Color.g) + Mathf.Abs(Color.b - o.Color.b) < 1e-4f);

        public override string ToString() => IsOriginal ? "original" : $"{Name} {Mathf.RoundToInt(Strength * 100f)}%";
    }

    /// edit6dof: the swatches and the shade maths. Pure.
    public static class EditShades
    {
        /// Original, then the finishes a kitchen or a facade part comes in.
        public static readonly string[] Names = { "Original", "Stainless", "Black", "White", "Slate", "Navy", "Red", "Bronze", "Wood" };

        static readonly Color32[] s_Colors =
        {
            new Color32(255, 255, 255, 255), new Color32(200, 204, 208, 255), new Color32(32, 33, 36, 255), new Color32(244, 244, 240, 255),
            new Color32(89, 98, 110, 255), new Color32(31, 47, 82, 255), new Color32(168, 50, 40, 255), new Color32(107, 75, 46, 255),
            new Color32(155, 106, 64, 255),
        };

        public static int Count => Names.Length;

        /// The shade strengths: Light and Full.
        public const float Light = 0.5f, Full = 0.85f;

        public static Color ColorOf(int i) => i >= 0 && i < s_Colors.Length ? (Color)s_Colors[i] : Color.white;

        /// Swatch i at `strength` (0: Original).
        public static PartShade Make(int i, float strength) =>
            i <= 0 || i >= Names.Length ? default : new PartShade { Name = Names[i], Color = ColorOf(i), Strength = Mathf.Clamp01(strength) };

        /// The swatch a shade is (0 = Original; −1 = none of these).
        public static int IndexOf(PartShade s)
        {
            if (s.IsOriginal) return 0;
            for (int i = 1; i < Names.Length; i++) if (Names[i] == s.Name) return i;
            return -1;
        }

        /// A material's colour under a shade: its own colour blended toward the swatch (alpha kept).
        public static Color Tint(Color original, Color swatch, float strength)
        {
            float k = Mathf.Clamp01(strength);
            return new Color(original.r + (swatch.r - original.r) * k, original.g + (swatch.g - original.g) * k, original.b + (swatch.b - original.b) * k, original.a);
        }

        /// "Navy · full shade" / "Original" (the panel's line, the notebook's finish).
        public static string Label(PartShade s) => s.IsOriginal ? "Original" : $"{s.Name} · {(s.Strength >= (Light + Full) * 0.5f ? "full" : "light")} shade";
    }
}
