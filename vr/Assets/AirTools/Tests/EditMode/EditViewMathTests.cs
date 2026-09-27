using System.Text;
using AirTools.Core;
using AirTools.Parts;
using AirTools.Tools;
using AirTools.UI;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// edit6dof: the Edit view's pure maths (Edit6DofMath, EditViewMath, EditFlow, EditShades, the filters and the hold):
    /// no engine calls, so it runs in the offline runner as well as the Editor. EditViewSceneTests (Editor gate) drives the
    /// real view with a PartTool, the placement editor and the ToolInputHub.
    public class EditViewMathTests
    {
        [SetUp]
        public void Imperial() => UiSettings.UseUnits(UnitSystem.Imperial);

        static void Near(Vector3 expected, Vector3 actual, float tol, string what) =>
            Assert.That(Vector3.Distance(expected, actual), Is.LessThan(tol), $"{what}: expected {expected.ToString("F4")}, got {actual.ToString("F4")}");

        static float Angle(Quaternion a, Quaternion b)
        {
            float d = Mathf.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);
            return 2f * Mathf.Acos(Mathf.Min(1f, d)) * Mathf.Rad2Deg;
        }

        static string Plain(System.Action<StringBuilder> write)
        {
            var sb = new StringBuilder();
            write(sb);
            return PlacementMath.Plain(sb);
        }

        // ---------------- turn axes: a drag about an arrow's axis changes only its component ----------------

        [Test]
        public void TurningAboutAnAxisChangesOnlyItsComponent()
        {
            var f = PlacementMath.Cavity(new Vector3(1f, 0f, 2f), Vector3.up, new Vector3(1f, 0f, 1f));
            var fit = f.Facing;
            var rng = new System.Random(7);
            for (int n = 0; n < 40; n++)
            {
                var ttr = new Vector3((float)rng.NextDouble() * 120f - 60f, (float)rng.NextDouble() * 60f - 30f, (float)rng.NextDouble() * 60f - 30f);
                var rot = PlacementMath.Rotation(f, fit, ttr);
                for (int k = 0; k < 3; k++)
                {
                    var axis = Edit6DofMath.AxisOf(EditAxis.Turn + k, f, ttr);
                    float d = (float)rng.NextDouble() * 20f - 10f;
                    var turned = PlacementMath.Normalize(PlacementMath.AxisAngle(axis, d) * rot);
                    var back = PlacementMath.TurnTiltRoll(f, fit, turned);
                    var want = ttr;
                    want[k] += d;
                    Near(want, back, 0.02f, $"axis {k} by {d:0.0}° at {ttr}");
                }
            }
        }

        [Test]
        public void MoveAxesAreTheFramesAndOnlyOneComponentMoves()
        {
            var f = PlacementMath.Cavity(Vector3.zero, Vector3.up, Vector3.forward);
            Near(f.Right, Edit6DofMath.AxisOf(EditAxis.Right, f, Vector3.zero), 1e-5f, "right");
            Near(f.Up, Edit6DofMath.AxisOf(EditAxis.Up, f, Vector3.zero), 1e-5f, "up");
            Near(f.Out, Edit6DofMath.AxisOf(EditAxis.Out, f, Vector3.zero), 1e-5f, "out");
            Edit6DofMath.Delta(EditAxis.Up, 0.02f, out var rud, out var ttr);
            Near(new Vector3(0f, 0.02f, 0f), rud, 1e-6f, "up only");
            Near(Vector3.zero, ttr, 1e-6f, "no turn");
            Edit6DofMath.Delta(EditAxis.Roll, -5f, out rud, out ttr);
            Near(Vector3.zero, rud, 1e-6f, "no move");
            Near(new Vector3(0f, 0f, -5f), ttr, 1e-6f, "roll only");
            Assert.AreEqual(0.02f, Edit6DofMath.Component(EditAxis.Up, new Vector3(0f, 0.02f, 0f), Vector3.zero), 1e-6f);
            Assert.AreEqual(7f, Edit6DofMath.Component(EditAxis.Tilt, Vector3.zero, new Vector3(1f, 7f, 2f)), 1e-6f);
        }

        // ---------------- drags ----------------

        [Test]
        public void TheGrabbedPointRidesThePointer()
        {
            var p0 = new Pose(Vector3.zero, Quaternion.identity);
            var grab0 = new Vector3(0f, 0f, 2f);
            Near(grab0 + new Vector3(0.1f, 0f, 0f), Edit6DofMath.Lever(p0, new Pose(new Vector3(0.1f, 0f, 0f), Quaternion.identity), grab0), 1e-5f, "a hand move carries it");
            var turned = Edit6DofMath.Lever(p0, new Pose(Vector3.zero, PlacementMath.AxisAngle(Vector3.up, 90f)), grab0);
            Near(new Vector3(2f, 0f, 0f), turned, 1e-4f, "aiming 90° right swings it round at the same distance");
        }

        [Test]
        public void ArrowAndRingDragsMeasureAlongAndAboutTheAxis()
        {
            Assert.AreEqual(0.05f, Edit6DofMath.ArrowDrag(Vector3.right * 3f, Vector3.zero, new Vector3(0.05f, 0.4f, -0.2f)), 1e-6f, "only along the axis");
            Assert.AreEqual(-0.05f, Edit6DofMath.ArrowDrag(Vector3.right, new Vector3(1f, 0f, 0f), new Vector3(0.95f, 1f, 0f)), 1e-6f);
            var c = new Vector3(1f, 1f, 1f);
            var g0 = c + new Vector3(0f, 0f, 0.3f);
            var g1 = c + PlacementMath.AxisAngle(Vector3.up, 20f) * new Vector3(0f, 0.1f, 0.3f);
            Assert.AreEqual(20f, Edit6DofMath.RingDrag(c, Vector3.up, g0, g1), 1e-3f, "swept 20° about up (height ignored)");
            Assert.AreEqual(-20f, Edit6DofMath.RingDrag(c, Vector3.up, g1 - new Vector3(0f, 0.1f, 0f), g0), 1e-3f, "and back");
        }

        [Test]
        public void StepsAreThePanelsAndQuantizeRounds()
        {
            Assert.AreEqual(5f, Edit6DofMath.Step(EditAxis.Tilt, UnitSystem.Imperial, false), 1e-6f);
            Assert.AreEqual(1f, Edit6DofMath.Step(EditAxis.Roll, UnitSystem.Metric, true), 1e-6f);
            Assert.AreEqual(0.0254f * 3f / 8f, Edit6DofMath.Step(EditAxis.Right, UnitSystem.Imperial, false), 1e-7f, "⅜″");
            Assert.AreEqual(0.002f, Edit6DofMath.Step(EditAxis.Out, UnitSystem.Metric, true), 1e-7f, "2 mm");
            Assert.AreEqual(10f, Edit6DofMath.Quantize(11.9f, 5f), 1e-5f);
            Assert.AreEqual(15f, Edit6DofMath.Quantize(13.1f, 5f), 1e-5f);
            Assert.AreEqual(-0.009525f, Edit6DofMath.Quantize(-0.011f, 0.009525f), 1e-6f);
            Assert.AreEqual(0.123f, Edit6DofMath.Quantize(0.123f, 0f), 1e-6f, "no step: as is");
        }

        [Test]
        public void TheArrowReadoutSaysWhichWayAndHowMuch()
        {
            Assert.AreEqual("Right 1⅛″", Plain(sb => Edit6DofMath.AppendAxis(sb, EditAxis.Right, 3 * 0.009525f, UnitSystem.Imperial)));
            Assert.AreEqual("Left ⅜″", Plain(sb => Edit6DofMath.AppendAxis(sb, EditAxis.Right, -0.009525f, UnitSystem.Imperial)));
            Assert.AreEqual("Down 10 mm", Plain(sb => Edit6DofMath.AppendAxis(sb, EditAxis.Up, -0.01f, UnitSystem.Metric)));
            Assert.AreEqual("In ¼″", Plain(sb => Edit6DofMath.AppendAxis(sb, EditAxis.Out, -0.00635f, UnitSystem.Imperial)));
            Assert.AreEqual("Tilt 5°", Plain(sb => Edit6DofMath.AppendAxis(sb, EditAxis.Tilt, 5.2f, UnitSystem.Imperial)));
            Assert.AreEqual("Roll " + Units.Minus + "1°", Plain(sb => Edit6DofMath.AppendAxis(sb, EditAxis.Roll, -1f, UnitSystem.Imperial)));
            Assert.AreEqual(5, Edit6DofMath.ReadoutQuantum(EditAxis.Turn, 4.6f, UnitSystem.Imperial));
            Assert.AreEqual(9, Edit6DofMath.ReadoutQuantum(EditAxis.Right, 0.0286f, UnitSystem.Imperial), "eighths");
        }

        // ---------------- turning it by hand ----------------

        [Test]
        public void TheHandsTurnCarriesThePart()
        {
            var r0 = PlacementMath.AxisAngle(Vector3.up, 30f);
            var g = PlacementMath.AxisAngle(Vector3.forward, 15f);
            var turned = Edit6DofMath.TurnedBy(Quaternion.identity, g, Quaternion.identity, r0);
            Assert.Less(Angle(PlacementMath.Normalize(g * r0), turned), 0.01f, "no display turn: the hand's rotation on top");
            // Shown turned by 90° about up: the hand's roll about the view axis is a roll about the part's own axis as shown.
            var view = PlacementMath.AxisAngle(Vector3.up, 90f);
            var t = Edit6DofMath.TurnedBy(Quaternion.identity, g, view, r0);
            Assert.Less(Angle(PlacementMath.Normalize(g * view * r0), PlacementMath.Normalize(view * t)), 0.01f, "as shown, it turned with the hand");
            // FreeGrab: turning about the grabbed point keeps that point where the lever puts it.
            var p0 = new Pose(Vector3.zero, Quaternion.identity);
            var anchor = Edit6DofMath.FreeGrab(p0, p0, Quaternion.identity, g, new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, 1f), r0, out var rot);
            Near(new Vector3(0f, 0f, 1f), anchor, 1e-5f, "the anchor at the grab point stays");
            Assert.Less(Angle(PlacementMath.Normalize(g * r0), rot), 0.01f);
        }

        // ---------------- the context menu (edit-touch: within reach) ----------------

        [Test]
        public void TheMenuComesWithinReachOnYourRightOfThePart()
        {
            var eye = new Vector3(0f, 1.6f, 3f);
            foreach (var part in new[] { new Vector3(0f, 0.5f, 0f), new Vector3(-2f, 2.5f, 1f), new Vector3(0f, 0f, 3f) })
            {
                var pose = EditViewMath.MenuPose(eye, Vector3.back, part, 0.42f, 18f, 14f);
                Assert.AreEqual(0.42f, Vector3.Distance(eye, pose.position), 1e-4f, "within reach");
                Assert.AreEqual(0.42f * Mathf.Sin(18f * Mathf.Deg2Rad), eye.y - pose.position.y, 1e-4f, "18° below the eye line");
                Near((pose.position - eye).normalized, pose.rotation * Vector3.forward, 1e-4f, "facing the eye");
                Assert.AreEqual(0f, (pose.rotation * Vector3.right).y, 1e-5f, "upright");
                var to = part - eye;
                to.y = 0f;
                if (to.sqrMagnitude < 1e-6f) to = Vector3.back;
                var flat = pose.position - eye;
                flat.y = 0f;
                // You look along −Z: your right is −X; the card sits 14° right of the line to the part.
                Assert.AreEqual(14f, Vector3.SignedAngle(to, flat, Vector3.up), 0.01f, $"14° to your right of the part at {part}");
            }
        }

        // ---------------- the hold ----------------

        [Test]
        public void AHoldFiresOnceAfterItsTimeAndATravelCancelsIt()
        {
            var h = new EditHold { Seconds = 0.6f };
            h.Begin(10f, Vector3.zero, Vector3.forward);
            Assert.IsFalse(h.Update(10.3f, new Vector3(0.01f, 0f, 0f), Vector3.forward), "half way");
            Assert.AreEqual(0.5f, h.Progress, 1e-4f);
            Assert.IsTrue(h.Update(10.61f, new Vector3(0.01f, 0f, 0f), Vector3.forward), "held still long enough");
            Assert.IsTrue(h.Fired);
            Assert.IsFalse(h.Update(11f, Vector3.zero, Vector3.forward), "once");
            h.Begin(0f, Vector3.zero, Vector3.forward);
            Assert.IsFalse(h.Update(0.2f, new Vector3(0.06f, 0f, 0f), Vector3.forward), "a drag");
            Assert.IsTrue(h.Moved);
            Assert.IsFalse(h.Update(1f, new Vector3(0.06f, 0f, 0f), Vector3.forward), "cancelled for good");
            h.Begin(0f, Vector3.zero, Vector3.forward);
            Assert.IsFalse(h.Update(0.2f, Vector3.zero, PlacementMath.AxisAngle(Vector3.up, 10f) * Vector3.forward), "a sweep of the aim");
            Assert.IsTrue(h.Moved);
            h.Begin(0f, Vector3.zero, Vector3.forward);
            h.Cancel();
            Assert.IsFalse(h.Update(1f, Vector3.zero, Vector3.forward), "released");
        }

        // ---------------- filters ----------------

        [Test]
        public void TheOneEuroFiltersSmoothJitterAndFollowMoves()
        {
            var f = new OneEuro();
            var rng = new System.Random(3);
            float sumRaw = 0f, sumOut = 0f;
            Vector3 prev = Vector3.zero, prevOut = Vector3.zero;
            for (int i = 0; i < 200; i++)
            {
                var x = new Vector3((float)(rng.NextDouble() - 0.5) * 0.01f, 0f, 0f);   // ±5 mm of jitter at rest
                var y = f.Filter(x, 1f / 72f);
                if (i > 0) { sumRaw += (x - prev).magnitude; sumOut += (y - prevOut).magnitude; }
                prev = x; prevOut = y;
            }
            Assert.Less(sumOut, sumRaw * 0.35f, "still: the jitter mostly goes");
            // A fast move is followed with little lag.
            f.Reset(Vector3.zero);
            Vector3 last = Vector3.zero;
            for (int i = 1; i <= 36; i++) last = f.Filter(new Vector3(i * 0.02f, 0f, 0f), 1f / 72f);   // 1.44 m/s
            Assert.Less(0.72f - last.x, 0.06f, "moving: it keeps up");
            var r = new OneEuroRotation();
            var q = PlacementMath.AxisAngle(Vector3.up, 40f);
            Quaternion o = Quaternion.identity;
            r.Reset(Quaternion.identity);
            for (int i = 0; i < 144; i++) o = r.Filter(q, 1f / 72f);
            Assert.Less(Angle(q, o), 0.5f, "a rotation settles on its target");
            var v = new OneEuroValue();
            float val = 0f;
            for (int i = 0; i < 144; i++) val = v.Filter(12f, 1f / 72f);
            Assert.AreEqual(12f, val, 0.2f, "a value settles");
            Assert.Less(Angle(Quaternion.identity, OneEuroRotation.Nlerp(Quaternion.identity, q, 0.5f)), 20.5f);
        }

        // ---------------- the stage (edit-touch: within arm's reach) ----------------

        static float Azimuth(Vector3 eye, Vector3 level, Vector3 p)
        {
            var w = p - eye;
            return Vector3.SignedAngle(level, new Vector3(w.x, 0f, w.z), Vector3.up);
        }

        static void InView(Vector3 eye, Vector3 level, Vector3 p, string what)
        {
            var w = p - eye;
            float down = Mathf.Atan2(-w.y, new Vector2(w.x, w.z).magnitude) * Mathf.Rad2Deg;
            Assert.LessOrEqual(Mathf.Abs(Azimuth(eye, level, p)), 48f, $"{what}: azimuth");
            Assert.That(down, Is.InRange(-15f, 55f), $"{what}: below the eye line");
        }

        /// The distance of `p` from the eye and how far below it.
        static void FromEye(Vector3 eye, Vector3 p, out float distance, out float drop)
        {
            distance = Vector3.Distance(eye, p);
            drop = eye.y - p.y;
        }

        [TestCase(1.20f, 0f)]
        [TestCase(1.60f, 35f)]
        [TestCase(1.76f, -120f)]
        public void TheViewSitsWithinReachBelowTheEyesFacingYou(float eyeHeight, float headingDeg)
        {
            var eye = new Vector3(0.3f, eyeHeight, -1f);
            var fwd = PlacementMath.AxisAngle(Vector3.up, headingDeg) * new Vector3(0f, -0.4f, 1f);   // looking a little down
            var level = PlacementMath.AxisAngle(Vector3.up, headingDeg) * Vector3.forward;
            var anchor = EditViewMath.EyeFrame(eye, fwd, 0.42f, 20f);
            FromEye(eye, anchor.position, out float d, out float drop);
            Assert.AreEqual(0.42f, d, 1e-4f, "the anchor 0.42 m from the eye");
            Assert.AreEqual(0.42f * Mathf.Sin(20f * Mathf.Deg2Rad), drop, 1e-4f, "20° down (14 cm)");
            Near((anchor.position - eye).normalized, anchor.rotation * Vector3.forward, 1e-4f, "facing the eye");
            Assert.AreEqual(0f, (anchor.rotation * Vector3.right).y, 1e-5f, "its right is level");
            Assert.Greater(Vector3.Dot(anchor.rotation * Vector3.right, PlacementMath.AxisAngle(Vector3.up, 90f) * level), 0.999f, "+X is your right");
            foreach (float largest in new[] { 0.08f, 0.25f, 0.6f, 1.9f })
            foreach (bool moves in new[] { true, false })
            {
                // Scaled so its largest side is at most 0.25 m; ringed from half of that (at least 6 cm).
                float scale = EditViewMath.FitScale(largest);
                float shown = largest * scale;
                Assert.LessOrEqual(shown, 0.25f + 1e-5f, $"{largest} m shown at {shown}");
                if (largest <= 0.25f) Assert.AreEqual(1f, scale, 1e-6f, "small: true size");
                float r = Mathf.Max(shown * 0.5f, 0.06f);
                var l = EditViewMath.Layout(r, moves, 0.032f, new Vector2(0.225f, 0.228f), new Vector2(0.11f, 0.034f));
                var stage = EditViewMath.StagePose(anchor, l);
                FromEye(eye, stage.position, out float sd, out float sdrop);
                Assert.That(sdrop, Is.InRange(0.05f, 0.25f), "the item below the eyes");
                Assert.That(sd, Is.InRange(0.42f, 0.56f), "the item just behind its arrows");
                // Every knob on the sphere at touch distance, facing the eye, and below or near eye height.
                for (int i = 0; i < EditViewMath.Arrows.Length; i++)
                {
                    var a = EditViewMath.Arrows[i];
                    if (!moves && !a.Curved) continue;
                    var onPlane = stage.position + stage.rotation * EditViewMath.ArrowPosition(a, l.Inner, l.Outer, l.Front);
                    var k = EditViewMath.FacingEye(eye, onPlane, 0.42f);
                    FromEye(eye, k.position, out float kd, out float kdrop);
                    Assert.AreEqual(0.42f, kd, 1e-4f, $"{a.Name}: at touch distance");
                    Assert.Greater(kdrop, -0.12f, $"{a.Name}: no higher than 12 cm over the eyes");
                    Assert.Less(kdrop, 0.4f, $"{a.Name}: no lower than 40 cm under them");
                    Near((k.position - eye).normalized, k.rotation * Vector3.forward, 1e-4f, $"{a.Name}: facing the eye");
                    Assert.Less(Vector3.Distance(onPlane, eye), 0.53f, $"{a.Name}: on the plane already near");
                }
                // The panel: 0.44 m from the eye, 15–30 cm down, facing it, on your right.
                var panel = EditViewMath.PanelPose(eye, anchor, l.Panel, 0.44f);
                FromEye(eye, panel.position, out float pd, out float pdrop);
                Assert.AreEqual(0.44f, pd, 1e-4f, "the panel within reach");
                Assert.That(pdrop, Is.InRange(0.15f, 0.30f), "the panel below the eyes");
                Near((panel.position - eye).normalized, panel.rotation * Vector3.forward, 1e-4f, "the panel faces the eye");
                Assert.Greater(Vector3.Dot(panel.position - anchor.position, anchor.rotation * Vector3.right), 0f, "on your right");
                // It all stays in view, in front of you: the ring's centre within 15° of your heading, every knob and panel
                // corner within ±48° of it (the side slots reach 54°) and between 15° above and 55° below the eye line.
                var ringAt = stage.position + stage.rotation * new Vector3(0f, 0f, -l.Front);
                Assert.LessOrEqual(Mathf.Abs(Azimuth(eye, level, ringAt)), 15f, "the item ahead of you");
                for (int i = 0; i < EditViewMath.Arrows.Length; i++)
                {
                    if (!moves && !EditViewMath.Arrows[i].Curved) continue;
                    var k = EditViewMath.FacingEye(eye, stage.position + stage.rotation * EditViewMath.ArrowPosition(EditViewMath.Arrows[i], l.Inner, l.Outer, l.Front), 0.42f);
                    InView(eye, level, k.position, EditViewMath.Arrows[i].Name);
                }
                foreach (var c in new[] { new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(-1f, 1f), new Vector2(1f, 1f) })
                    InView(eye, level, panel.position + panel.rotation * new Vector3(c.x * 0.1125f, c.y * 0.114f, 0f), $"panel corner {c}");
            }
        }

        /// The panel sits in the ring's lower right, clear of every knob (with its gap) and of the item's box; the readout
        /// pill clear of the knobs; the whole composition centred on the anchor.
        [Test]
        public void ThePanelTucksIntoTheRingClearOfEveryKnobAndTheItem()
        {
            var panel = new Vector2(0.225f, 0.228f);
            var pill = new Vector2(0.11f, 0.034f);
            foreach (float r in new[] { 0.06f, 0.09f, 0.125f })
            foreach (bool moves in new[] { true, false })
            {
                var l = EditViewMath.Layout(r, moves, 0.032f, panel, pill);
                Near(Vector3.zero, (l.Min + l.Max) * 0.5f, 1e-5f, "centred on the anchor");
                Assert.AreEqual(r, l.Front, 1e-6f, "the item's near face on the knob plane");
                Assert.Greater(l.Inner - r, 0.016f, "the inner knobs clear the item");
                var pMin = l.Panel - panel * 0.5f - l.Ring;   // ring space
                var pMax = l.Panel + panel * 0.5f - l.Ring;
                Assert.Greater(pMin.x, r, "the panel clear of the item's box");
                var qMin = l.Readout - pill * 0.5f;
                var qMax = l.Readout + pill * 0.5f;
                for (int i = 0; i < EditViewMath.Arrows.Length; i++)
                {
                    var a = EditViewMath.Arrows[i];
                    if (!moves && !a.Curved) continue;
                    var k = EditViewMath.ArrowPosition(a, l.Inner, l.Outer, l.Front);
                    var kMin = new Vector2(k.x, k.y) - Vector2.one * 0.016f;
                    var kMax = new Vector2(k.x, k.y) + Vector2.one * 0.016f;
                    Assert.IsFalse(kMin.x < pMax.x + 0.01f && kMax.x > pMin.x - 0.01f && kMin.y < pMax.y + 0.01f && kMax.y > pMin.y - 0.01f,
                        $"r={r} moves={moves}: {a.Name} at ({k.x:0.000}, {k.y:0.000}) vs the panel ({pMin.x:0.000}, {pMin.y:0.000})–({pMax.x:0.000}, {pMax.y:0.000})");
                    Assert.IsFalse(kMin.x < qMax.x + 0.006f && kMax.x > qMin.x - 0.006f && kMin.y < qMax.y + 0.006f && kMax.y > qMin.y - 0.006f,
                        $"r={r} moves={moves}: {a.Name} vs the readout pill");
                    for (int j = 0; j < i; j++)
                    {
                        var b = EditViewMath.Arrows[j];
                        if (!moves && !b.Curved) continue;
                        Assert.Greater(Vector3.Distance(k, EditViewMath.ArrowPosition(b, l.Inner, l.Outer, l.Front)), 0.032f + 0.004f, $"{a.Name} vs {b.Name}");
                    }
                }
            }
        }

        [Test]
        public void FacingYawTurnsThePartsFrontToYou()
        {
            Assert.AreEqual(90f, EditViewMath.FacingYaw(Vector3.right, Vector3.back), 1e-3f, "a part facing +X turns to face −Z… ");
            Near(Vector3.back, PlacementMath.AxisAngle(Vector3.up, 90f) * Vector3.right, 1e-5f, "…by +90° about up");
        }

        // ---------------- a held poke (edit-touch) ----------------

        [Test]
        public void AHeldPokeRepeatsAfterPoint4SThenEveryPoint12S()
        {
            var r = new HoldRepeat();
            r.Begin(10f);
            Assert.IsTrue(r.Active);
            Assert.IsFalse(r.Tick(10.39f, true), "not before 0.4 s");
            Assert.IsTrue(r.Tick(10.40f, true), "the first repeat at 0.4 s");
            Assert.IsFalse(r.Tick(10.45f, true));
            Assert.IsFalse(r.Tick(10.519f, true));
            Assert.IsTrue(r.Tick(10.52f, true), "then every 0.12 s");
            Assert.IsTrue(r.Tick(10.64f, true));
            Assert.AreEqual(3, r.Repeats);
            Assert.IsFalse(r.Tick(10.80f, false), "let go: nothing more");
            Assert.IsFalse(r.Active);
            Assert.IsFalse(r.Tick(11.0f, true), "…even if it reads held again");
            // At 90 Hz for 1 s: the press + repeats at 0.4, 0.52, 0.64, 0.76, 0.88, 1.0 s.
            r.Begin(0f);
            int n = 1;
            for (int f = 1; f <= 90; f++) if (r.Tick(f / 90f, true)) n++;
            Assert.AreEqual(7, n, "seven steps in a second");
            Assert.AreEqual(7, HoldRepeat.Count(1f));
            Assert.AreEqual(1, HoldRepeat.Count(0.3f), "a tap: one step");
            Assert.AreEqual(2, HoldRepeat.Count(0.45f));
            // A 0.5 s hitch fires once, not a burst, and the rhythm picks up from there.
            r.Begin(0f);
            Assert.IsTrue(r.Tick(0.4f, true));
            Assert.IsTrue(r.Tick(0.9f, true), "one after the hitch");
            Assert.IsFalse(r.Tick(0.95f, true), "no burst");
            Assert.IsTrue(r.Tick(1.02f, true), "0.12 s on");
            r.End();
            Assert.IsFalse(r.Tick(2f, true), "ended");
        }

        // ---------------- a Move is a translation (edit-touch) ----------------

        [Test]
        public void SeatingKeepsTheRotationAndRestsTheBoxOnTheSurface()
        {
            var box = new Bounds(new Vector3(0f, 0.2f, 0.05f), new Vector3(0.6f, 0.4f, 0.5f));
            var rng = new System.Random(11);
            foreach (var normal in new[] { Vector3.up, Vector3.back, new Vector3(0.3f, 0.9f, -0.1f).normalized })
                for (int n = 0; n < 12; n++)
                {
                    var rot = PlacementMath.Normalize(PlacementMath.AxisAngle(new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f),
                        (float)rng.NextDouble() * 180f));
                    var point = new Vector3(1f, 0.9f, -2f);
                    float scale = n % 2 == 0 ? 1f : 1.63f;
                    var origin = PartPlacer.SeatOrigin(point, normal, rot, box, scale);
                    // The box's lowest corner along the normal is on the plane; its centre over the point.
                    float lowest = float.MaxValue;
                    for (int c = 0; c < 8; c++)
                    {
                        var corner = box.center + Vector3.Scale(box.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                        lowest = Mathf.Min(lowest, Vector3.Dot(origin + rot * (corner * scale) - point, normal));
                    }
                    Assert.AreEqual(0f, lowest, 1e-4f, "resting on the surface");
                    var centre = origin + rot * (box.center * scale);
                    var off = centre - point;
                    Near(Vector3.zero, off - normal * Vector3.Dot(off, normal), 1e-4f, "its centre over the point");
                }
        }

        [Test]
        public void TheShownPartKeepsItsBoxCentreOnTheStage()
        {
            var yaw = PlacementMath.AxisAngle(Vector3.up, 90f);
            var root = new Vector3(3f, 0f, 1f);
            var box = new Vector3(3f, 0.4f, 1.2f);
            var shown = EditViewMath.Display(root, Quaternion.identity, box, new Vector3(0f, 1.6f, 0f), yaw, 0.5f);
            // The box centre goes to the stage: the root sits at yaw · (root − box) · scale from it.
            Near(new Vector3(0f, 1.6f, 0f) + yaw * ((root - box) * 0.5f), shown.position, 1e-5f, "root");
            Assert.Less(Angle(yaw, shown.rotation), 0.01f, "turned by the display turn");
        }

        [Test]
        public void TwelveArrowsRoundTheItemEachAxisBothWays()
        {
            var arrows = EditViewMath.Arrows;
            Assert.AreEqual(12, arrows.Length);
            for (int a = 0; a < 6; a++)
            {
                int plus = 0, minus = 0;
                foreach (var x in arrows) if ((int)x.Axis == a) { if (x.Sign > 0) plus++; else minus++; }
                Assert.AreEqual(1, plus, $"axis {(EditAxis)a} +");
                Assert.AreEqual(1, minus, $"axis {(EditAxis)a} −");
            }
            var names = new System.Collections.Generic.HashSet<string>();
            foreach (var x in arrows) Assert.IsTrue(names.Add(x.Name), x.Name);
            // Curved ones turn; the outer ring is the straight right / up / left / down.
            foreach (var x in arrows)
            {
                Assert.AreEqual(x.Curved, Edit6DofMath.IsTurn(x.Axis));
                if (x.Outer) Assert.IsTrue(x.Axis == EditAxis.Right || x.Axis == EditAxis.Up, x.Name);
            }
            // Positions: on their ring, in front of the item; no two knobs on top of each other.
            for (int i = 0; i < arrows.Length; i++)
            {
                var p = EditViewMath.ArrowPosition(arrows[i], 0.25f, 0.32f, 0.05f);
                Assert.AreEqual(arrows[i].Outer ? 0.32f : 0.25f, new Vector2(p.x, p.y).magnitude, 1e-4f);
                Assert.AreEqual(-0.05f, p.z, 1e-6f);
                for (int j = 0; j < i; j++) Assert.Greater(Vector3.Distance(p, EditViewMath.ArrowPosition(arrows[j], 0.25f, 0.32f, 0.05f)), 0.06f, $"{arrows[i].Name} vs {arrows[j].Name}");
            }
            var tiltUp = EditViewMath.ArrowPosition(arrows[0], 0.25f, 0.32f, 0.05f);
            Assert.Greater(tiltUp.y, 0.2f, "tilt up is at the top");
            Assert.AreEqual(EditAxis.Tilt, arrows[0].Axis);
            Assert.AreEqual(+1, arrows[0].Sign);
        }

        [Test]
        public void APinchOnTheItemTakesItsSphere()
        {
            Assert.IsTrue(EditViewMath.RaySphere(Vector3.zero, Vector3.forward, new Vector3(0.1f, 0f, 1f), 0.2f, out _));
            Assert.IsFalse(EditViewMath.RaySphere(Vector3.zero, Vector3.forward, new Vector3(0.3f, 0f, 1f), 0.2f, out _));
            Assert.IsTrue(EditViewMath.RaySphere(new Vector3(0.05f, 0f, 1f), Vector3.back, new Vector3(0f, 0f, 1f), 0.2f, out _), "a hand inside it");
        }

        [Test]
        public void TheFlyEasesAndReduceMotionSkipsIt()
        {
            Assert.AreEqual(0f, EditViewMath.Ease(0f), 1e-6f);
            Assert.AreEqual(0.5f, EditViewMath.Ease(0.5f), 1e-6f);
            Assert.AreEqual(1f, EditViewMath.Ease(1f), 1e-6f);
            Assert.AreEqual(1f, EditViewMath.Ease(3f), 1e-6f);
            float prev = 0f;
            for (int i = 1; i <= 20; i++) { float e = EditViewMath.Ease(i / 20f); Assert.GreaterOrEqual(e, prev); prev = e; }
            Assert.Less(EditViewMath.Ease(0.1f), 0.1f, "eases in");
            var a = new Pose(Vector3.zero, Quaternion.identity);
            var b = new Pose(new Vector3(2f, 0f, 0f), PlacementMath.AxisAngle(Vector3.up, 90f));
            Near(new Vector3(1f, 0f, 0f), EditViewMath.Blend(a, b, 0.5f).position, 1e-5f, "half way");
            Assert.Less(Angle(b.rotation, EditViewMath.Blend(a, b, 1f).rotation), 0.01f);
            Assert.AreEqual(0f, EditViewMath.FlySeconds(true), 1e-6f, "Reduce motion: at once");
            Assert.AreEqual(0.5f, EditViewMath.FlySeconds(false), 1e-6f);
            Assert.AreEqual(1f, EditViewMath.Progress(5f, 5f, 0f), 1e-6f, "a zero fly is done");
            Assert.AreEqual(0.5f, EditViewMath.Progress(5.25f, 5f, 0.5f), 1e-6f);
        }

        [Test]
        public void OnlyToolsThatDontMeasureLetALongPinchOpenTheMenu()
        {
            Assert.IsTrue(EditViewMath.HoldOpensMenu(ToolKind.Part));
            Assert.IsTrue(EditViewMath.HoldOpensMenu(ToolKind.Move));
            Assert.IsTrue(EditViewMath.HoldOpensMenu(ToolKind.None));
            Assert.IsFalse(EditViewMath.HoldOpensMenu(ToolKind.Measure));
            Assert.IsFalse(EditViewMath.HoldOpensMenu(ToolKind.Level));
            Assert.IsFalse(EditViewMath.HoldOpensMenu(ToolKind.Ladder));
        }

        // ---------------- the flow ----------------

        static EditFlow Run(params EditStep[] steps)
        {
            var f = new EditFlow();
            foreach (var s in steps) Assert.IsTrue(f.Fire(s), $"{s} from {f.Phase}");
            return f;
        }

        [Test]
        public void APlacedPartOpensOrientsAndSavesOrCancels()
        {
            var f = Run(EditStep.OpenPlaced);
            Assert.AreEqual(EditPhase.Opening, f.Phase);
            Assert.AreEqual(EditKind.Placed, f.Kind);
            Assert.IsFalse(f.Can(EditStep.Move), "not while it flies in");
            Assert.IsTrue(f.Fire(EditStep.Opened));
            Assert.IsFalse(f.Can(EditStep.Place), "a placed part has no Place");
            Assert.IsTrue(f.Fire(EditStep.Save));
            Assert.AreEqual(EditPhase.Closing, f.Phase);
            Assert.IsTrue(f.Keep);
            Assert.IsTrue(f.Fire(EditStep.Closed));
            Assert.AreEqual(EditPhase.Idle, f.Phase);
            f = Run(EditStep.OpenPlaced, EditStep.Opened, EditStep.Cancel);
            Assert.AreEqual(EditPhase.Closing, f.Phase);
            Assert.IsFalse(f.Keep, "Cancel reverts");
            Assert.IsTrue(f.Fire(EditStep.Closed));
            Assert.IsFalse(f.Active);
        }

        [Test]
        public void MoveFliesBackThenSaveOrCancelEndsItInTheRoom()
        {
            var f = Run(EditStep.OpenPlaced, EditStep.Opened, EditStep.Move);
            Assert.AreEqual(EditPhase.ToRoom, f.Phase);
            Assert.IsTrue(f.Fire(EditStep.Arrived));
            Assert.AreEqual(EditPhase.Moving, f.Phase);
            Assert.IsFalse(f.Can(EditStep.Move));
            Assert.IsTrue(f.Fire(EditStep.Save));
            Assert.AreEqual(EditPhase.Idle, f.Phase, "saved where it is: no fly");
            Assert.IsTrue(f.Keep);
            f = Run(EditStep.OpenPlaced, EditStep.Opened, EditStep.Move, EditStep.Arrived, EditStep.Cancel);
            Assert.AreEqual(EditPhase.Idle, f.Phase);
            Assert.IsFalse(f.Keep);
        }

        [Test]
        public void ANewPartIsOrientedThenPlacedAndThePlacementSaves()
        {
            var f = Run(EditStep.OpenNew, EditStep.Opened);
            Assert.AreEqual(EditKind.New, f.Kind);
            Assert.IsFalse(f.Can(EditStep.Save), "no Save: placing it saves");
            Assert.IsFalse(f.Can(EditStep.Move), "no Move: Place");
            Assert.IsTrue(f.Fire(EditStep.Place));
            Assert.AreEqual(EditPhase.Placing, f.Phase);
            Assert.IsTrue(f.Fire(EditStep.Placed));
            Assert.AreEqual(EditPhase.Idle, f.Phase);
            Assert.IsTrue(f.Keep, "auto-saved");
            f = Run(EditStep.OpenNew, EditStep.Opened, EditStep.Place, EditStep.Cancel);
            Assert.AreEqual(EditPhase.Idle, f.Phase);
            Assert.IsFalse(f.Keep);
        }

        [Test]
        public void AbortEndsAnySessionAndIdleIgnoresTheRest()
        {
            var idle = new EditFlow();
            foreach (EditStep s in System.Enum.GetValues(typeof(EditStep)))
                if (s != EditStep.OpenPlaced && s != EditStep.OpenNew) Assert.IsFalse(idle.Fire(s), $"{s} from Idle");
            foreach (var steps in new[]
            {
                new[] { EditStep.OpenPlaced }, new[] { EditStep.OpenPlaced, EditStep.Opened }, new[] { EditStep.OpenPlaced, EditStep.Opened, EditStep.Move },
                new[] { EditStep.OpenNew, EditStep.Opened, EditStep.Place }, new[] { EditStep.OpenPlaced, EditStep.Opened, EditStep.Save },
            })
            {
                var f = Run(steps);
                Assert.IsTrue(f.Fire(EditStep.Abort), $"abort from {f.Phase}");
                Assert.AreEqual(EditPhase.Idle, f.Phase);
            }
        }

        [Test]
        public void WhatShowsInEachPhase()
        {
            Assert.IsTrue(EditFlow.ShowsStage(EditPhase.Orient));
            Assert.IsFalse(EditFlow.ShowsStage(EditPhase.Moving));
            Assert.IsTrue(EditFlow.Interactive(EditPhase.Orient));
            Assert.IsFalse(EditFlow.Interactive(EditPhase.Opening), "not while it flies");
            Assert.IsTrue(EditFlow.ShowsMoveBar(EditPhase.Moving));
            Assert.IsTrue(EditFlow.ShowsMoveBar(EditPhase.Placing));
            Assert.IsFalse(EditFlow.ShowsMoveBar(EditPhase.Orient));
            foreach (EditPhase p in System.Enum.GetValues(typeof(EditPhase))) Assert.AreEqual(p != EditPhase.Idle, EditFlow.Dims(p), p.ToString());
            Assert.IsTrue(EditFlow.DimWritesDepth(EditPhase.Orient), "at the centre the item draws over near walls");
            Assert.IsFalse(EditFlow.DimWritesDepth(EditPhase.Moving), "in the room it's depth-tested");
            Assert.IsFalse(EditFlow.DimWritesDepth(EditPhase.Placing));
            Assert.IsFalse(EditFlow.DimWritesDepth(EditPhase.Opening), "while it flies in from across the room, the room occludes it as usual");
            Assert.IsTrue(EditFlow.ShowsMoveArrows(EditKind.Placed));
            Assert.IsFalse(EditFlow.ShowsMoveArrows(EditKind.New), "a new part has no room pose to move yet");
        }

        // ---------------- shades ----------------

        [Test]
        public void AShadeBlendsTheModelsColourTowardTheSwatch()
        {
            var own = new Color(1f, 1f, 1f, 0.8f);
            var navy = EditShades.ColorOf(5);
            Assert.AreEqual(own, EditShades.Tint(own, navy, 0f), "none");
            var full = EditShades.Tint(own, navy, 1f);
            Assert.AreEqual(navy.r, full.r, 1e-5f);
            Assert.AreEqual(navy.b, full.b, 1e-5f);
            Assert.AreEqual(0.8f, full.a, 1e-6f, "alpha kept (glass stays glass)");
            var half = EditShades.Tint(own, navy, 0.5f);
            Assert.AreEqual((1f + navy.g) * 0.5f, half.g, 1e-5f);
            Assert.AreEqual(9, EditShades.Count);
            Assert.AreEqual("Original", EditShades.Names[0]);
            Assert.IsTrue(EditShades.Make(0, 1f).IsOriginal);
            var s = EditShades.Make(5, EditShades.Full);
            Assert.AreEqual("Navy", s.Name);
            Assert.AreEqual(5, EditShades.IndexOf(s));
            Assert.AreEqual(0, EditShades.IndexOf(default));
            Assert.IsTrue(s.Same(EditShades.Make(5, EditShades.Full)));
            Assert.IsFalse(s.Same(EditShades.Make(5, EditShades.Light)), "strength counts");
            Assert.IsTrue(default(PartShade).Same(EditShades.Make(0, 0.5f)), "originals are the same");
            Assert.AreEqual("Navy · full shade", EditShades.Label(s));
            Assert.AreEqual("Navy · light shade", EditShades.Label(EditShades.Make(5, EditShades.Light)));
            Assert.AreEqual("Original", EditShades.Label(default));
        }
    }
}
