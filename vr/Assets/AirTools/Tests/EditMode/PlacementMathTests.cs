using System;
using System.Text;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// The placement editor's pure maths (PlacementMath, PlacementStore, the notebook row and the agent contract): no
    /// engine calls, so it runs in the offline runner as well as the Editor. PlacementEditorTests (Editor gate) drives the
    /// real editor with a PartTool.
    public class PlacementMathTests
    {
        const float Mm = 0.001f;

        [SetUp]
        public void Units() => UiSettings.UseUnits(UnitSystem.Imperial);

        static void Near(Vector3 expected, Vector3 actual, float tol, string what) =>
            Assert.That(Vector3.Distance(expected, actual), Is.LessThan(tol), $"{what}: expected {expected.ToString("F4")}, got {actual.ToString("F4")}");

        static float Angle(Quaternion a, Quaternion b)
        {
            float d = Mathf.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);
            return 2f * Mathf.Acos(Mathf.Min(1f, d)) * Mathf.Rad2Deg;
        }

        /// A cavity whose opening faces +X (turned 90° from the default), insert point at (2, 0, 1).
        static PlacementFrame TurnedCavity() => PlacementMath.Cavity(new Vector3(2f, 0f, 1f), Vector3.up, Vector3.right);

        // ---------------- frames ----------------

        [Test]
        public void CavityFrameIsTheInsertFrame()
        {
            var f = PlacementMath.Cavity(Vector3.zero, Vector3.up, Vector3.forward);
            Near(Vector3.forward, f.Out, 1e-5f, "out of the opening");
            Near(Vector3.up, f.Up, 1e-5f, "up");
            Near(Vector3.Cross(Vector3.forward, Vector3.up), f.Right, 1e-5f, "right = out × up (CavityBox.R)");
            Near(Vector3.left, f.Right, 1e-5f, "facing the opening from +Z, your right is −X");
            Near(f.Out, f.Facing * Vector3.forward, 1e-5f, "a part facing out: +Z = Out");
            Near(f.Up, f.Facing * Vector3.up, 1e-5f, "… upright");
            Near(-f.Out, f.View * Vector3.forward, 1e-5f, "the viewer looks in");
            Near(f.Right, f.View * Vector3.right, 1e-5f, "the viewer's right");
            // A tilted "out" (a noisy fit) is flattened against up.
            var g = PlacementMath.Cavity(Vector3.zero, Vector3.up, new Vector3(0f, 0.2f, 1f));
            Near(Vector3.forward, g.Out, 1e-5f, "out made level");
        }

        [Test]
        public void SurfaceFramesTakeTheWallsNormalOrTheFloorsUp()
        {
            var wall = PlacementMath.Surface(Vector3.zero, new Vector3(0f, 0f, 1f), Vector3.up, new Vector3(0.3f, 0f, 2f));
            Assert.AreEqual("surface", wall.Kind);
            Near(Vector3.forward, wall.Out, 1e-5f, "a wall: out of the wall");
            Near(Vector3.up, wall.Up, 1e-5f, "a wall: world up");
            var floor = PlacementMath.Surface(Vector3.zero, Vector3.up, Vector3.up, new Vector3(2f, 1.5f, 0f));
            Near(Vector3.up, floor.Up, 1e-5f, "a floor: its normal is up");
            Near(Vector3.right, floor.Out, 1e-5f, "a floor: out toward the viewer, level");
            var view = PlacementMath.View(Vector3.zero, Vector3.up, new Vector3(0f, -1f, -3f));
            Assert.AreEqual("view", view.Kind);
            Near(Vector3.back, view.Out, 1e-5f, "nothing under it: out toward you, level");
        }

        // ---------------- nudges in the cavity frame ----------------

        [Test]
        public void NudgesMoveAlongTheCavitysAxes()
        {
            var f = TurnedCavity();   // out = +X, up = +Y, right = out × up = +Z (you stand at +X looking toward −X)
            Near(new Vector3(0f, 0f, 1f), f.Right, 1e-5f, "right");
            var a0 = f.Origin;
            var a = PlacementMath.Move(f, a0, new Vector3(10f * Mm, 0f, 0f));
            Near(a0 + new Vector3(0f, 0f, 0.01f), a, 1e-6f, "1 cm right");
            a = PlacementMath.Move(f, a, new Vector3(0f, 2f * Mm, -3f * Mm));
            Near(a0 + new Vector3(-0.003f, 0.002f, 0.01f), a, 1e-6f, "2 mm up, 3 mm in");
            Near(new Vector3(10f * Mm, 2f * Mm, -3f * Mm), PlacementMath.Offset(f, a, a0), 1e-6f, "the readout's offset");
            // Steps in the user's unit add up exactly in the readout: four fine steps are ½″.
            float step = PlacementMath.StepMetres(UnitSystem.Imperial, true);
            var sb = new StringBuilder();
            PlacementMath.AppendLength(sb, 4 * step, UnitSystem.Imperial);
            Assert.AreEqual("½″", PlacementMath.Plain(sb));
            Assert.AreEqual(0.01f, PlacementMath.StepMetres(UnitSystem.Metric, false), 1e-6f);
            Assert.AreEqual(0.002f, PlacementMath.StepMetres(UnitSystem.Metric, true), 1e-6f);
            Assert.AreEqual(0.009525f, PlacementMath.StepMetres(UnitSystem.Imperial, false), 1e-6f, "⅜″");
            Assert.AreEqual("Step ⅜″ · 5°", PlacementMath.StepLabel(UnitSystem.Imperial, false));
            Assert.AreEqual("Fine 2 mm · 1°", PlacementMath.StepLabel(UnitSystem.Metric, true));
        }

        // ---------------- rotations ----------------

        [Test]
        public void QuaternionHelpersMatchUnitysConventions()
        {
            // AngleAxis: +90° about +Y takes +Z to +X (clockwise seen from above).
            Near(Vector3.right, PlacementMath.AxisAngle(Vector3.up, 90f) * Vector3.forward, 1e-5f, "yaw");
            Near(Vector3.forward, PlacementMath.AxisAngle(Vector3.right, 90f) * Vector3.up, 1e-5f, "pitch: up tips to +Z");
            Near(Vector3.up, PlacementMath.AxisAngle(Vector3.forward, 90f) * Vector3.right, 1e-5f, "roll: +X to +Y");
            var q = PlacementMath.AxisAngle(new Vector3(1f, 2f, 3f), 40f);
            Assert.Less(Angle(Quaternion.identity, PlacementMath.Normalize(q * PlacementMath.Conj(q))), 0.01f, "conj is the inverse");
            foreach (var e in new[] { new Vector3(10f, 20f, 30f), new Vector3(-35f, 170f, -80f), new Vector3(0f, -90f, 45f), new Vector3(60f, 0f, 0f) })
            {
                var r = PlacementMath.FromEulerYXZ(e);
                var back = PlacementMath.EulerYXZ(r);
                Assert.Less(Angle(r, PlacementMath.FromEulerYXZ(back)), 0.01f, $"Euler {e} round trip ({back})");
                Near(e, back, 0.01f, $"Euler {e}");
            }
        }

        [Test]
        public void TurnTiltRollComposeAndRoundTrip()
        {
            var f = PlacementMath.Cavity(Vector3.zero, Vector3.up, Vector3.forward);
            var fit = f.Facing;
            // Each button's direction, as the panel and the agent contract name it.
            var turn = PlacementMath.Rotation(f, fit, new Vector3(10f, 0f, 0f));
            Assert.Less(Vector3.Dot(turn * Vector3.forward, f.Right), 0f, "turn +: clockwise from above, the front swings to your left");
            var tilt = PlacementMath.Rotation(f, fit, new Vector3(0f, 10f, 0f));
            Assert.Less(Vector3.Dot(tilt * Vector3.up, f.Out), 0f, "tilt +: the top tips away from you");
            var roll = PlacementMath.Rotation(f, fit, new Vector3(0f, 0f, 10f));
            Assert.Greater(Vector3.Dot(roll * Vector3.up, f.Right), 0f, "roll +: clockwise as you face it, the top goes right");
            Assert.Less(Angle(PlacementMath.Rotation(f, fit, Vector3.zero), fit), 0.01f, "zero is the fit rotation");
            // Turn 90 then a tilt: the tilt is about the part's own (turned) right, so the readout keeps each number.
            var both = PlacementMath.Rotation(f, fit, new Vector3(90f, 5f, 0f));
            Near(new Vector3(90f, 5f, 0f), PlacementMath.TurnTiltRoll(f, fit, both), 0.01f, "turn 90, tilt 5");
            foreach (var ttr in new[] { new Vector3(30f, 10f, -5f), new Vector3(-120f, -20f, 15f), new Vector3(179f, 0f, 1f) })
                Near(ttr, PlacementMath.TurnTiltRoll(f, fit, PlacementMath.Rotation(f, fit, ttr)), 0.02f, $"round trip {ttr}");
            // The same in a turned cavity, where the frame isn't the world's.
            var g = TurnedCavity();
            Near(new Vector3(-45f, 3f, 2f), PlacementMath.TurnTiltRoll(g, g.Facing, PlacementMath.Rotation(g, g.Facing, new Vector3(-45f, 3f, 2f))), 0.02f, "turned cavity");
            Assert.AreEqual(-170f, PlacementMath.Wrap180(190f), 1e-4f);
            Assert.AreEqual(180f, PlacementMath.Wrap180(-180f), 1e-4f);
        }

        // ---------------- anchors and model swaps ----------------

        static Bounds Box(Vector3 size, string face)
        {
            var spec = new PartSpec { id = "x", dims_mm = new PartDims { w = size.x * 1000f, h = size.y * 1000f, d = size.z * 1000f }, mount = new PartMount { face = face } };
            return PartMath.LocalBox(spec);
        }

        [TestCase(PlacementAnchor.FrontBottomCentre, "-y")]
        [TestCase(PlacementAnchor.BottomBackCentre, "-z")]
        [TestCase(PlacementAnchor.BoundsCentre, "-y")]
        public void ASwappedModelKeepsTheAnchor(PlacementAnchor kind, string face)
        {
            var f = TurnedCavity();
            var rot = PlacementMath.Rotation(f, f.Facing, new Vector3(7f, 0f, 0f));
            var anchor = f.Origin + f.Vector(new Vector3(0.02f, 0f, -0.005f));
            var dishwasher = Box(new Vector3(0.596f, 0.815f, 0.57f), face);
            var smaller = Box(new Vector3(0.45f, 0.81f, 0.55f), face);
            var o1 = PlacementMath.OriginFor(anchor, rot, dishwasher, kind);
            var o2 = PlacementMath.OriginFor(anchor, rot, smaller, kind);
            Near(anchor, PlacementMath.AnchorOf(o1, rot, dishwasher, kind), 1e-5f, "model 1's anchor");
            Near(anchor, PlacementMath.AnchorOf(o2, rot, smaller, kind), 1e-5f, "model 2's anchor, same spot");
            if (kind == PlacementAnchor.FrontBottomCentre)
            {
                // Fronts flush, both on the floor: the front-bottom-centre of each box is the anchor.
                Near(anchor, o2 + rot * new Vector3(smaller.center.x, smaller.min.y, smaller.max.z), 1e-5f, "flush front");
                Assert.AreNotEqual(o1, o2, "the origins differ (different depths)");
            }
        }

        // ---------------- grab ----------------

        [Test]
        public void AGrabKeepsItsOffsetAndTurnsWithTheHand()
        {
            var p0 = new Pose(new Vector3(0f, 1.2f, 0f), PlacementMath.FromBasis(Vector3.right, Vector3.up, Vector3.forward));
            var grab0 = new Vector3(0f, 1.0f, 1.5f);
            var anchor0 = new Vector3(0.1f, 0.8f, 1.6f);
            // The hand moves 10 cm right, same aim: everything moves with it.
            var p1 = new Pose(p0.position + new Vector3(0.1f, 0f, 0f), p0.rotation);
            var a = PlacementMath.GrabAnchor(p0, p1, grab0, anchor0, Vector3.up, true, out float turn, out _);
            Near(anchor0 + new Vector3(0.1f, 0f, 0f), a, 1e-5f, "moved with the hand");
            Assert.AreEqual(0f, turn, 1e-3f);
            // The hand turns 10° (clockwise from above): the grab point swings on the ray and the part turns 10° about it.
            var turned = PlacementMath.AxisAngle(Vector3.up, 10f);
            var p2 = new Pose(p0.position, turned * p0.rotation);
            a = PlacementMath.GrabAnchor(p0, p2, grab0, anchor0, Vector3.up, true, out turn, out var delta);
            Assert.AreEqual(10f, turn, 1e-3f, "the added turn");
            var grab = p0.position + turned * (grab0 - p0.position);
            Near(grab + turned * (anchor0 - grab0), a, 1e-5f, "turned about the grab point");
            Assert.AreEqual(Vector3.Distance(grab0, anchor0), Vector3.Distance(grab, a), 1e-5f, "the grab offset is kept");
            // Aiming up only lifts it (yaw only: no tilt), and the offset from the grab point doesn't turn.
            var p3 = new Pose(p0.position, PlacementMath.AxisAngle(Vector3.right, -10f) * p0.rotation);
            a = PlacementMath.GrabAnchor(p0, p3, grab0, anchor0, Vector3.up, true, out turn, out delta);
            Assert.AreEqual(0f, turn, 1e-3f, "no turn from aiming up");
            Assert.Less(Angle(delta, Quaternion.identity), 0.01f, "yaw only");
            Assert.Greater(a.y, anchor0.y, "lifted");
            Assert.AreEqual(10f, PlacementMath.SignedYaw(Vector3.forward, PlacementMath.AxisAngle(Vector3.up, 10f) * Vector3.forward, Vector3.up), 1e-3f);
            Assert.AreEqual(0f, PlacementMath.SignedYaw(Vector3.up, Vector3.forward, Vector3.up), 1e-6f, "along up: no yaw");
        }

        // ---------------- snapping ----------------

        [Test]
        public void SnapRules()
        {
            Assert.IsTrue(PlacementMath.NearFit(new Vector3(0.01f, 0.01f, 0.02f), new Vector3(4f, 0f, -2f)), "2.4 cm, 4°: back to the fit");
            Assert.IsFalse(PlacementMath.NearFit(new Vector3(0.04f, 0f, 0f), Vector3.zero), "4 cm away stays");
            Assert.IsFalse(PlacementMath.NearFit(Vector3.zero, new Vector3(10f, 0f, 0f)), "turned 10° stays");
            var off = new Vector3(0.008f, 0.02f, -0.01f);
            var ttr = new Vector3(88f, 3f, -5f);
            Assert.IsTrue(PlacementMath.SnapSmall(ref off, ref ttr));
            Near(new Vector3(0f, 0.02f, 0f), off, 1e-6f, "centred and flush; 2 cm up stays");
            Near(new Vector3(90f, 0f, -5f), ttr, 1e-4f, "square turn, level tilt; −5° roll stays");
            off = new Vector3(0.008f, 0.005f, -0.01f);
            ttr = Vector3.zero;
            PlacementMath.SnapSmall(ref off, ref ttr, axes: 4);
            Near(new Vector3(0.008f, 0.005f, 0f), off, 1e-6f, "a wall: only out (flush) snaps");
        }

        // ---------------- the fit in a cavity ----------------

        /// cab1 of synthetic-facade-parts: 600 × 820 × 580 mm, opening toward +Z, insert at (0.4, 0, 0.58).
        static CavityVolume Cab1(out PlacementFrame f)
        {
            f = PlacementMath.Cavity(new Vector3(0.4f, 0f, 0.58f), Vector3.up, Vector3.forward);
            return new CavityVolume { Centre = new Vector3(0.4f, 0.41f, 0.29f), Right = f.Right, Up = f.Up, Out = f.Out, Half = new Vector3(0.3f, 0.41f, 0.29f) };
        }

        static FitReport FitAt(Vector3 size, Vector3 offset, Vector3 turnTiltRoll = default)
        {
            var c = Cab1(out var f);
            var box = Box(size, "-y");
            var rot = PlacementMath.Rotation(f, f.Facing, turnTiltRoll);
            var origin = PlacementMath.OriginFor(PlacementMath.Move(f, f.Origin, offset), rot, box, PlacementAnchor.FrontBottomCentre);
            return PlacementMath.CavityFit(PlacementMath.Clearance(c, box, origin, rot));
        }

        [Test]
        public void CavityFitWordsAndColours()
        {
            var ac = new Vector3(0.47f, 0.3f, 0.38f);
            var r = FitAt(ac, Vector3.zero);
            Assert.AreEqual(FitStatus.Green, r.Status, r.Headline);
            Assert.AreEqual("✓ Fits the gap · 5⅛″ spare", Copy.FitLine(r), "130 mm spare across");
            Assert.AreEqual(130f, r.SpareMm.Value, 0.5f);

            r = FitAt(new Vector3(0.65f, 0.8f, 0.5f), Vector3.zero);
            Assert.AreEqual(FitStatus.Red, r.Status);
            Assert.AreEqual("Too wide by 2″", r.Verdict, "50 mm over");

            r = FitAt(ac, new Vector3(-0.1f, 0f, 0f));
            Assert.AreEqual(FitStatus.Red, r.Status);
            Assert.AreEqual("Into the left side by 1⅜″", r.Verdict, "moved 100 mm left of centre: 35 mm into the side");
            Assert.AreEqual("Move it right 1⅜″", r.Reason);

            r = FitAt(ac, new Vector3(0f, 0f, 0.05f));
            Assert.AreEqual(FitStatus.Amber, r.Status);
            Assert.AreEqual("Sticks out 2″", r.Verdict);

            r = FitAt(new Vector3(0.598f, 0.8f, 0.5f), Vector3.zero);
            Assert.AreEqual(FitStatus.Amber, r.Status, "2 mm spare");
            StringAssert.StartsWith("Tight", r.Verdict);

            r = FitAt(new Vector3(0.5f, 0.85f, 0.5f), Vector3.zero);
            Assert.AreEqual("Too tall by 1⅛″", r.Verdict, "30 mm over the 820 mm opening");

            // Turned 90° about its front-bottom-centre, a 580 mm deep box still fits across the 600 mm opening, but it
            // swings 580 mm to your right from the centre: 280 mm into the right side (the check sees what it sweeps).
            r = FitAt(new Vector3(0.47f, 0.3f, 0.58f), Vector3.zero, new Vector3(90f, 0f, 0f));
            Assert.AreEqual(FitStatus.Red, r.Status, $"turned: {r.Headline} {string.Join(" | ", r.Lines)}");
            Assert.AreEqual("Into the right side by 11″", r.Verdict);
            Assert.AreEqual("Move it left 11″", r.Reason);

            UiSettings.UseUnits(UnitSystem.Metric);
            Assert.AreEqual("✓ Fits the gap · 130 mm spare", Copy.FitLine(FitAt(ac, Vector3.zero)));
        }

        // ---------------- readout (imperial first, D2) ----------------

        [Test]
        public void ReadoutInImperialAndMetric()
        {
            var sb = new StringBuilder(256);
            PlacementMath.AppendReadout(sb, new Vector3(0.0349f, 0f, -0.00635f), new Vector3(5f, 0f, -2f), UnitSystem.Imperial);
            Assert.AreEqual("Right 1⅜″ · Up 0″ · In ¼″\nTurn 5° · Tilt 0° · Roll −2°", PlacementMath.Plain(sb));
            StringAssert.Contains("<mspace=0.58em>1</mspace>⅜″", sb.ToString(), "live numbers are tabular");
            sb.Clear();
            PlacementMath.AppendOffset(sb, new Vector3(-0.4f, -0.0001f, 0.3048f), UnitSystem.Imperial);
            Assert.AreEqual("Left 1′ 3¾″ · Up 0″ · Out 1′ 0″", PlacementMath.Plain(sb), "feet from a foot up; a hair under 0 reads Up 0″");
            sb.Clear();
            PlacementMath.AppendOffset(sb, new Vector3(0.0349f, 0.0021f, -1.2504f), UnitSystem.Metric);
            Assert.AreEqual("Right 35 mm · Up 2 mm · In 1.25 m", PlacementMath.Plain(sb));
            Assert.AreEqual("Right 1⅜″ · turned 5°", PlacementEditor.OffsetWords(new Vector3(0.0349f, 0f, 0f), new Vector3(5f, 0f, 0f)));
            Assert.AreEqual("At the fit", PlacementEditor.OffsetWords(Vector3.zero, Vector3.zero));
        }

        [Test]
        public void ReadoutDoesNotAllocate()
        {
            var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            if (method == null) Assert.Ignore("GC.GetAllocatedBytesForCurrentThread isn't available on this runtime");
            var probe = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
            var sb = new StringBuilder(512);
            var off = new Vector3(0.0349f, -0.0123f, 0.4f);
            var ttr = new Vector3(5f, -1f, 12f);
            for (int i = 0; i < 3; i++) { sb.Clear(); PlacementMath.AppendReadout(sb, off, ttr, UnitSystem.Imperial); PlacementMath.AppendReadout(sb, off, ttr, UnitSystem.Metric); }
            long before = probe();
            for (int i = 0; i < 100; i++)
            {
                sb.Clear();
                PlacementMath.AppendReadout(sb, off + Vector3.one * (i * Mm), ttr, UnitSystem.Imperial);
                PlacementMath.AppendReadout(sb, off, ttr + Vector3.one * i, UnitSystem.Metric);
                PlacementMath.Quantum(off.x, UnitSystem.Imperial);
            }
            long after = probe();
            Assert.AreEqual(0L, after - before, "per-frame readout: no allocation");
        }

        // ---------------- saved placements ----------------

        [Test]
        public void SavedPlacementsSurviveARescaleAndAReload()
        {
            // The scene file's frame inside SceneRoot: turned 30°, moved, 1.0 calibration.
            var s1 = new PackageSpace { Position = new Vector3(1f, 0f, -2f), Rotation = PlacementMath.AxisAngle(Vector3.up, 30f), Scale = 1f };
            var feature = new Vector3(0.4f, 0f, 0.58f);   // cab1's insert point, package units
            var anchorRoot = s1.ToRoot(feature) + s1.DirToRoot(new Vector3(-0.02f, 0f, 0.005f));
            var rotRoot = s1.RotToRoot(PlacementMath.AxisAngle(Vector3.up, 5f));
            var store = new PlacementStore();
            string key = PlacementMath.Key("synthetic-facade-parts", 1, "cavity:cab1");
            Assert.AreEqual("synthetic-facade-parts#r1/cavity:cab1", key);
            Assert.AreEqual("A", store.NextSlot(key));
            Assert.IsNull(store.Put(key, new SavedPlacement { Slot = "A", PartId = "dw", AnchorPkg = s1.ToPackage(anchorRoot), RotationPkg = s1.RotToPackage(rotRoot) }));
            Assert.AreEqual("B", store.NextSlot(key));
            Assert.AreEqual("A", store.Spot(key).Active);

            // "Set scale" (×1.08) and a reload that puts the content somewhere else: the placement stays on the feature.
            var s2 = new PackageSpace { Position = new Vector3(-0.5f, 0.1f, 3f), Rotation = PlacementMath.AxisAngle(Vector3.up, -60f), Scale = 1.08f };
            var a = store.Active(key);
            var anchor2 = s2.ToRoot(a.AnchorPkg);
            Near(s2.ToRoot(feature) + s2.DirToRoot(new Vector3(-0.02f, 0f, 0.005f)) * 1.08f, anchor2, 1e-5f, "same place on the scene");
            Assert.Less(Angle(s2.RotToRoot(PlacementMath.AxisAngle(Vector3.up, 5f)), s2.RotToRoot(a.RotationPkg)), 0.01f, "same turn relative to the scene");
            Near(anchorRoot, s1.ToRoot(s1.ToPackage(anchorRoot)), 1e-5f, "round trip");

            // Overwrite, undo (Put the old one back), remove.
            var b = new SavedPlacement { Slot = "A", PartId = "dw2" };
            var old = store.Put(key, b);
            Assert.AreEqual("dw", old.PartId);
            store.Put(key, old);
            Assert.AreEqual("dw", store.Get(key, "A").PartId);
            store.Put(key, new SavedPlacement { Slot = "C", PartId = "x" });
            Assert.AreEqual("B", store.NextSlot(key), "B is still free");
            Assert.AreEqual("C", store.Spot(key).Active);
            Assert.AreEqual("x", store.Remove(key, "C").PartId);
            Assert.IsNull(store.Spot(key).Active, "the removed slot was active");
            foreach (var slot in new[] { "B", "C", "D" }) store.Put(key, new SavedPlacement { Slot = slot });
            store.SetActive(key, "B");
            Assert.AreEqual("B", store.NextSlot(key), "all four taken: the active one is overwritten");
            Assert.AreEqual(4, store.Count);
        }

        [Test]
        public void SlotNamesAndKeys()
        {
            Assert.AreEqual("B", PlacementMath.SlotName("b"));
            Assert.AreEqual("C", PlacementMath.SlotName(" slot c "));
            Assert.AreEqual("B", PlacementMath.SlotName("2"));
            Assert.AreEqual("A", PlacementMath.SlotName("Placement A"));
            Assert.IsNull(PlacementMath.SlotName("the good one"));
            Assert.IsNull(PlacementMath.SlotName(null));
            Assert.AreEqual("built-in#r0/spot1:x", PlacementMath.Key(null, 0, "spot1:x"));
        }

        // ---------------- the notebook row and the agent contract ----------------

        [Test]
        public void SavedPlacementRowIsPlacePartReady()
        {
            var pos = new Vector3(0.4f, 0f, 0.29f);
            var rot = PlacementMath.AxisAngle(Vector3.up, 20f);
            var pose = PlacementMath.GltfPose(pos, rot);
            var e = new NotebookEntry("placement_pose", 0, "", new[] { pos }, new DateTime(2026, 9, 26, 12, 0, 0), -1, "Placement A: dishwasher")
            { PartId = "midea-dw", Slot = "A", Pose = pose, Where = "in the base cabinet gap" };
            Assert.AreEqual("placement_pose", NotebookExporter.BackendType(e), "not a 'placement' (the report counts those as pieces)");
            var json = JObject.Parse(NotebookExporter.ToJson(new[] { e }, "synthetic-facade-parts", "quest-test"));
            var row = (JObject)json["entries"][0];
            Assert.AreEqual("placement_pose", (string)row["type"]);
            Assert.AreEqual("midea-dw", (string)row["part_id"]);
            Assert.AreEqual("A", (string)row["slot"]);
            Assert.AreEqual("origin", (string)row["pose"]["anchor"]);
            // The pose goes straight back into place_part.
            Assert.IsTrue(PlacePartPose.TryParse(row["pose"], out var back));
            Assert.IsTrue(back.AtOrigin && back.HasRotation);
            Near(pos, back.Position, 1e-4f, "position (glTF → Unity)");
            Assert.Less(Angle(rot, back.Rotation), 0.1f, "rotation (glTF → Unity; float acos near 1)");
        }

        [Test]
        public void AgentNumbersAreTolerant()
        {
            var a = JObject.Parse("{\"dx_mm\": -25.4, \"yaw_deg\": \"90\", \"dz_mm\": null, \"up_mm\": 3}");
            Assert.AreEqual(-25.4f, PlacementActions.Num(a, "dx_mm"), 1e-4f, "move it left an inch");
            Assert.AreEqual(90f, PlacementActions.Num(a, "yaw_deg"), 1e-4f, "a numeric string");
            Assert.AreEqual(0f, PlacementActions.Num(a, "dz_mm"), "null");
            Assert.AreEqual(3f, PlacementActions.Num(a, "dy_mm", "up_mm"), "an alias");
        }
    }
}
