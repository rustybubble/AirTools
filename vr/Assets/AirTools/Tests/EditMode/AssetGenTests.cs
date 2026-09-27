using System;
using System.Collections.Generic;
using AirTools.Agent.Grok;
using AirTools.Core;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// assetgen: a taped window opening, a part made for it, and the Size & finish rules — the pure half (no engine
    /// calls: runs in the offline runner too). AssetGenEditorTests (Editor gate) drives the real part tool and
    /// placement editor on the synthetic facade.
    public class AssetGenMathTests
    {
        const float Mm = 0.001f;
        // The synthetic facade's window, taped the way AssetE2E tapes it: jamb to jamb and sill to head, 5 cm into the
        // reveal; the viewer at the spawn eye.
        static readonly Vector3 W0 = new Vector3(0.75f, 4.1f, -0.05f), W1 = new Vector3(-0.75f, 4.1f, -0.05f);
        static readonly Vector3 H0 = new Vector3(0.3f, 3.5f, -0.05f), H1 = new Vector3(0.3f, 4.7f, -0.05f);
        static readonly Vector3 Eye = new Vector3(0f, 1.7f, 4f);

        [SetUp]
        public void Units() => UiSettings.UseUnits(UnitSystem.Imperial);

        static void Near(Vector3 expected, Vector3 actual, float tol, string what) =>
            Assert.That(Vector3.Distance(expected, actual), Is.LessThan(tol), $"{what}: expected {expected.ToString("F4")}, got {actual.ToString("F4")}");

        static TapedOpening Facade()
        {
            Assert.IsTrue(OpeningMath.FromTapes(W0, W1, H0, H1, Eye, out var o));
            return o;
        }

        // ---------------- recognising an opening ----------------

        [Test]
        public void AWidthAndAHeightTapeFrameTheOpening()
        {
            var o = Facade();
            Assert.AreEqual(1.5f, o.W, 1e-5f, "width = the width tape");
            Assert.AreEqual(1.2f, o.H, 1e-5f, "height = the height tape");
            Near(new Vector3(0f, 4.1f, -0.05f), o.Centre, 1e-5f, "centre: across from the width tape, up from the height tape, the tapes' depth");
            Near(Vector3.forward, o.Out, 1e-5f, "out of the wall, toward the viewer");
            Near(Vector3.left, o.Right, 1e-5f, "facing the wall from +Z your right is −X (Right = Out × Up)");
            Assert.IsTrue(float.IsNaN(o.D), "no depth taped");

            // Either direction, either order of the ends; the viewer decides which way is out.
            Assert.IsTrue(OpeningMath.FromTapes(W1, W0, H1, H0, Eye, out var r));
            Near(o.Centre, r.Centre, 1e-5f, "reversed tapes");
            Near(o.Out, r.Out, 1e-5f, "reversed tapes: the same out");
            Assert.IsTrue(OpeningMath.FromTapes(W0, W1, H0, H1, new Vector3(0f, 1.7f, -4f), out var inside));
            Near(Vector3.back, inside.Out, 1e-5f, "taped from inside: out is the inside");
        }

        [Test]
        public void TapesThatDontFrameOneHoleAreNoOpening()
        {
            Assert.IsFalse(OpeningMath.FromTapes(W0, W1, W0 + Vector3.up * 0.3f, W1 + Vector3.up * 0.3f, Eye, out _), "two widths");
            var farH0 = H0 + Vector3.right * 2f; var farH1 = H1 + Vector3.right * 2f;
            Assert.IsFalse(OpeningMath.FromTapes(W0, W1, farH0, farH1, Eye, out _), "the height of another window, 2 m along");
            var lowW0 = W0 + Vector3.down * 2.5f; var lowW1 = W1 + Vector3.down * 2.5f;
            Assert.IsFalse(OpeningMath.FromTapes(lowW0, lowW1, H0, H1, Eye, out _), "a width 1.9 m below the sill");
            Assert.IsFalse(OpeningMath.FromTapes(W0, W1, H0 + Vector3.forward * 0.4f, H1 + Vector3.forward * 0.4f, Eye, out _), "40 cm apart through the wall");
            Assert.IsFalse(OpeningMath.FromTapes(W0, W0 + Vector3.left * 0.1f, H0, H1, Eye, out _), "a 10 cm width");
            // A slightly sloped width (≤ 20°) and a slightly leaning height still count.
            Assert.IsTrue(OpeningMath.FromTapes(W0, W1 + Vector3.up * 0.2f, H0, H1 + Vector3.right * 0.1f, Eye, out _), "out of level a little");
        }

        [Test]
        public void ARectangleShapeOnAWallIsAnOpening()
        {
            var tl = new Vector3(-0.75f, 4.7f, -0.1f); var tr = new Vector3(0.75f, 4.7f, -0.1f);
            var br = new Vector3(0.75f, 3.5f, -0.1f); var bl = new Vector3(-0.75f, 3.5f, -0.1f);
            Assert.IsTrue(OpeningMath.FromQuad(new[] { tl, tr, br, bl }, Eye, out var a));
            Assert.AreEqual(1.5f, a.W, 1e-5f); Assert.AreEqual(1.2f, a.H, 1e-5f);
            Near(new Vector3(0f, 4.1f, -0.1f), a.Centre, 1e-5f, "the rectangle's centre");
            Assert.AreEqual("shape", a.Source);
            Assert.IsTrue(OpeningMath.FromQuad(new[] { tr, br, bl, tl }, Eye, out var b), "any starting corner");
            Near(a.Centre, b.Centre, 1e-5f, "same opening");
            Assert.AreEqual(1.5f, b.W, 1e-5f);
            Assert.IsFalse(OpeningMath.FromQuad(new[] { tl, tr, br + Vector3.right * 0.5f, bl }, Eye, out _), "not square");
            var floor = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) };
            Assert.IsFalse(OpeningMath.FromQuad(floor, Eye, out _), "a rectangle on the floor");
        }

        [Test]
        public void OpeningsPairTheNewestTapesAndSkipOthers()
        {
            var entries = new List<NotebookEntry>
            {
                Tape(new Vector3(-3f, 1f, 0f), new Vector3(-1f, 1f, 0f)),   // a run along the wall: no height tape for it
                Tape(H0, H1),
                Tape(W0, W1),
                new NotebookEntry("level", 0, "°", new[] { W0, W1 }, DateTime.Now, -1, "a level"),
            };
            var list = Openings.Pairs(entries, Eye);
            Assert.AreEqual(1, list.Count, "the width and the height of the window; the run and the level aren't");
            Assert.AreEqual(1.5f, list[0].W, 1e-5f);
            Assert.AreEqual(1.2f, list[0].H, 1e-5f);
        }

        /// The gate's live tapes on the facade (AssetE2E, :8004): the width's ends snapped at z −0.10 and 0.00 (a 3.8°
        /// tilt), the height's in the reveal. The wall around the window (front face z = 0 facing +Z, back z = −0.30
        /// facing −Z) as the probes would hit it.
        static readonly Vector3 LiveW0 = new Vector3(0.750f, 4.090f, -0.100f), LiveW1 = new Vector3(-0.750f, 4.099f, 0.000f);
        static readonly Vector3 LiveH0 = new Vector3(-0.140f, 3.500f, -0.100f), LiveH1 = new Vector3(-0.149f, 4.700f, -0.042f);

        static int WallHits(TapedOpening o, bool fromFront, Vector3[] pts, Vector3[] nrm)
        {
            float hw = o.W * 0.5f + 0.1f;
            var at = new[] { o.Centre + o.Right * hw, o.Centre - o.Right * hw, o.Centre + o.Up * (o.H * 0.5f + 0.1f), o.Centre + o.Right * hw + o.Up * 0.3f };
            for (int i = 0; i < at.Length; i++)
            {
                pts[i] = new Vector3(at[i].x, at[i].y, fromFront ? 0f : -0.30f);
                nrm[i] = fromFront ? Vector3.forward : Vector3.back;
            }
            return at.Length;
        }

        static TapedOpening OnTheWall(Vector3 viewer)
        {
            Assert.IsTrue(OpeningMath.FromTapes(LiveW0, LiveW1, LiveH0, LiveH1, viewer, out var o));
            var n = o.Out;
            var fp = new Vector3[9]; var fn = new Vector3[9]; var bp = new Vector3[9]; var bn = new Vector3[9];
            // Rays cast along −n hit the face toward n's side; along +n, the other one.
            bool nForward = Vector3.Dot(n, Vector3.forward) > 0f;
            int nf = WallHits(o, nForward, fp, fn), nb = WallHits(o, !nForward, bp, bn);
            bool front = OpeningMath.FitFace(fp, fn, nf, n, out var nF, out float dF);
            bool back = OpeningMath.FitFace(bp, bn, nb, -n, out var nB, out float dB);
            Assert.IsTrue(front && back, "both faces found");
            Assert.IsTrue(OpeningMath.ChooseFace(front, nF, dF, back, nB, dB, o.Centre, out var normal, out float offset));
            OpeningMath.OnFace(ref o, normal, offset);
            return o;
        }

        [Test]
        public void TheWallAroundTheOpeningSetsItsPlane()
        {
            Assert.IsTrue(OpeningMath.FromTapes(LiveW0, LiveW1, LiveH0, LiveH1, Eye, out var taped));
            Assert.Greater(Vector3.Angle(taped.Out, Vector3.forward), 3f, "the tapes alone: tilted 3.8°");
            Assert.AreEqual(-0.065f, taped.Centre.z, 0.01f, "… and 6.5 cm into the reveal");
            foreach (var viewer in new[] { Eye, new Vector3(0f, 1f, -10f) })   // the spawn eye, and a camera behind the wall
            {
                var o = OnTheWall(viewer);
                Near(new Vector3(0f, 4.1f, 0f), o.Centre, 0.002f, $"on the wall's face, centred (viewer {viewer})");
                Assert.Less(Vector3.Angle(Vector3.forward, o.Out), 0.1f, "Out = the wall's normal: square, toward the side it was taped from");
                Assert.Less(Vector3.Angle(Vector3.left, o.Right), 0.1f);
                Assert.AreEqual(1.5f, o.W, 0.001f, "the width along the wall (the tape read 1.503: its ends 10 cm apart in depth)");
                Assert.AreEqual(1.2f, o.H, 0.001f);
                Assert.IsTrue(o.OnWall);
                StringAssert.EndsWith("+ wall", o.Source);
            }
        }

        [Test]
        public void OnlyTheFaceTheTapesWereTakenAtCounts()
        {
            var c = new Vector3(0f, 4.1f, -0.065f);
            // The tapes lie in the face's reveal: a face 20 cm in front of them is a deep reveal (fine); one 20 cm behind them
            // would leave them floating in the air (not theirs); one 40 cm in front is another wall.
            Assert.IsTrue(OpeningMath.ChooseFace(true, Vector3.forward, 0.135f, false, default, 0f, c, out _, out _), "a 20 cm reveal");
            Assert.IsFalse(OpeningMath.ChooseFace(true, Vector3.forward, -0.265f, false, default, 0f, c, out _, out _), "20 cm behind the tapes");
            Assert.IsFalse(OpeningMath.ChooseFace(true, Vector3.forward, 0.335f, false, default, 0f, c, out _, out _), "40 cm in front");
            Assert.IsTrue(OpeningMath.ChooseFace(false, default, 0f, true, Vector3.back, 0.3f, c, out var n, out float d), "one-sided: the back");
            Assert.AreEqual(Vector3.back, n); Assert.AreEqual(0.3f, d, 1e-6f);
            // Hits that aren't the wall's face: a jamb (normal across), a sill 5 cm proud of the rest; two agreeing are enough.
            var pts = new[] { new Vector3(0.85f, 4.1f, 0f), new Vector3(-0.85f, 4.1f, 0f), new Vector3(0.8f, 3.5f, 0.05f), new Vector3(0.75f, 4f, -0.05f) };
            var nrm = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.left };
            Assert.IsTrue(OpeningMath.FitFace(pts, nrm, 4, Vector3.forward, out n, out d));
            Assert.AreEqual(0f, d, 1e-5f, "the sill's hit is left out (5 cm off the others); the jamb's normal isn't the face's");
            Assert.IsFalse(OpeningMath.FitFace(pts, nrm, 1, Vector3.forward, out _, out _), "one hit isn't a face");
        }

        static NotebookEntry Tape(Vector3 a, Vector3 b) => new NotebookEntry("measure", Vector3.Distance(a, b), "m", new[] { a, b }, DateTime.Now, -1, "tape");

        // ---------------- which parts are for it, and whether they fit ----------------

        [Test]
        public void APartIsForTheOpeningWhenItsMostOfIt()
        {
            var o = Facade();
            Assert.IsTrue(OpeningMath.ForOpening(1.487f, 1.187f, o), "a window frame");
            Assert.IsTrue(OpeningMath.ForOpening(0.9f, 0.72f, o), "60 % each way");
            Assert.IsFalse(OpeningMath.ForOpening(0.47f, 0.3f, o), "a window AC on the sill: a free placement, as before");
            Assert.IsTrue(OpeningMath.AtOpening(o, new Vector3(0.5f, 4.5f, -0.06f)), "seated on the glass inside the opening");
            Assert.IsTrue(OpeningMath.AtOpening(o, new Vector3(0.84f, 4.1f, 0.2f)), "a hand's width past the jamb, 20 cm proud");
            Assert.IsFalse(OpeningMath.AtOpening(o, new Vector3(1.75f, 4.1f, 0.04f)), "on the wall a metre beside it");
            Assert.IsFalse(OpeningMath.AtOpening(o, new Vector3(0f, 4.1f, 0.5f)), "half a metre in front");
        }

        [TestCase(1487.3f, 1187.3f, true, TestName = "made to measure: ½″ spare")]
        [TestCase(1503f, 1197f, true, TestName = "3 mm over: a tape's error")]
        [TestCase(1503.1f, 1197f, false, TestName = "more than 3 mm over")]
        [TestCase(1449.2f, 1149.2f, true, TestName = "2″ spare")]
        [TestCase(1449f, 1180f, false, TestName = "more than 2″ spare: made for a smaller opening")]
        [TestCase(1511.3f, 1206.5f, false, TestName = "a stock 59½ × 47½″ window")]
        public void FitsProperly(float w, float h, bool fits) => Assert.AreEqual(fits, OpeningMath.FitsProperly(w, h, 1500f, 1200f));

        [Test]
        public void FitToOpeningLeavesAQuarterInchEachSide()
        {
            var s = OpeningMath.FitToOpeningMm(new Vector3(1500f, 1200f, 0f), 82.55f);
            Assert.AreEqual(1487.3f, s.x, 1e-3f); Assert.AreEqual(1187.3f, s.y, 1e-3f); Assert.AreEqual(82.55f, s.z, 1e-4f);
            Assert.IsTrue(OpeningMath.FitsProperly(s.x, s.y, 1500f, 1200f));
        }

        static FitReport FitIn(TapedOpening o, float w, float h, float d, Vector3 offset = default)
        {
            var spec = new PartSpec { id = "win", dims_mm = new PartDims { w = w, h = h, d = d }, mount = new PartMount { face = "-z" } };
            var box = PartMath.LocalBox(spec);
            var rot = OpeningMath.Frame(o).Facing;
            var origin = PlacementMath.OriginFor(o.Centre + offset, rot, box, PlacementAnchor.FrontCentre);
            return OpeningMath.Fit(PlacementMath.Clearance(OpeningMath.Volume(o), box, origin, rot));
        }

        [Test]
        public void TheFitInTheOpeningInWords()
        {
            var o = Facade();
            var r = FitIn(o, 1487.3f, 1187.3f, 82.55f);
            Assert.AreEqual(FitStatus.Green, r.Status, r.Headline);
            Assert.AreEqual("✓ Fits the opening · ½″ spare", Copy.FitLine(r));
            Assert.AreEqual("the opening", r.Surface);
            Assert.AreEqual("Sits in the opening", r.Reason, "front flush with the wall");

            r = FitIn(o, 1511.3f, 1206.5f, 82.55f);
            Assert.AreEqual(FitStatus.Red, r.Status);
            Assert.AreEqual("Too wide by ½″", r.Verdict, "11 mm over");
            StringAssert.StartsWith("Opening ", r.Reason, "\"Opening 4′ 11″ · part …\"");

            r = FitIn(o, 1480f, 1260f, 82.55f);
            Assert.AreEqual("Too tall by 2⅜″", r.Verdict, "60 mm over the 1200 mm opening");

            r = FitIn(o, 1400f, 1100f, 82.55f);
            Assert.AreEqual(FitStatus.Amber, r.Status, "100 mm spare: made for a smaller opening");
            Assert.AreEqual("Loose in the opening · 3⅞″ spare", r.Verdict);

            r = FitIn(o, 1499f, 1190f, 82.55f);
            Assert.AreEqual(FitStatus.Amber, r.Status, "1 mm spare");
            StringAssert.StartsWith("Tight", r.Verdict);

            r = FitIn(o, 1487.3f, 1187.3f, 82.55f, Vector3.forward * 0.05f);
            Assert.AreEqual(FitStatus.Amber, r.Status, "5 cm proud of the wall");
            Assert.AreEqual("Push it in flush with the wall", r.Reason);

            UiSettings.UseUnits(UnitSystem.Metric);
            Assert.AreEqual("✓ Fits the opening · 13 mm spare", Copy.FitLine(FitIn(o, 1487.3f, 1187.3f, 82.55f)));
        }

        [Test]
        public void TheGapKeepsItsOwnWords()
        {
            // A removed part's cavity (e2e) reads as before: "the gap", OpeningSource "cavity".
            var f = PlacementMath.Cavity(new Vector3(0.4f, 0f, 0.58f), Vector3.up, Vector3.forward);
            var c = new CavityVolume { Centre = new Vector3(0.4f, 0.41f, 0.29f), Right = f.Right, Up = f.Up, Out = f.Out, Half = new Vector3(0.3f, 0.41f, 0.29f) };
            var spec = new PartSpec { id = "ac", dims_mm = new PartDims { w = 470f, h = 300f, d = 380f }, mount = new PartMount { face = "-y" } };
            var box = PartMath.LocalBox(spec);
            var r = PlacementMath.CavityFit(PlacementMath.Clearance(c, box, PlacementMath.OriginFor(f.Origin, f.Facing, box, PlacementAnchor.FrontBottomCentre), f.Facing));
            Assert.AreEqual("✓ Fits the gap · 5⅛″ spare", Copy.FitLine(r));
            Assert.AreEqual("the gap", r.Surface);
            Assert.AreEqual("cavity", r.OpeningSource);
        }

        [Test]
        public void TheFrontCentreIsTheOpeningsAnchor()
        {
            var box = new Bounds(new Vector3(0f, 0f, 0.04f), new Vector3(1.4f, 1.1f, 0.08f));
            Assert.AreEqual(new Vector3(0f, 0f, 0.08f), PlacementMath.AnchorLocal(box, PlacementAnchor.FrontCentre));
            var o = Facade();
            var rot = OpeningMath.Frame(o).Facing;
            var origin = PlacementMath.OriginFor(o.Centre, rot, box, PlacementAnchor.FrontCentre);
            Near(o.Centre, PlacementMath.AnchorOf(origin, rot, box, PlacementAnchor.FrontCentre), 1e-6f, "anchor on the centre");
            Near(new Vector3(0f, 4.1f, -0.13f), origin, 1e-5f, "the mount face 8 cm back in the reveal");
        }

        [Test]
        public void TheBestCandidateIsTheFirstThatFitsElseTheLeastOver()
        {
            var o = Facade();
            PartDims D(float w, float h) => new PartDims { w = w, h = h, d = 80f };
            Assert.AreEqual(1, OpeningMath.Best(new[] { D(1511f, 1206f), D(1490f, 1190f), D(1480f, 1180f) }, o), "the first that fits");
            Assert.AreEqual(1, OpeningMath.Best(new[] { D(1530f, 1210f), D(1511f, 1206f), D(700f, 1100f) }, o), "none fits: over by least (11 mm)");
            Assert.AreEqual(1, OpeningMath.Best(new[] { D(700f, 1100f), D(1600f, 1300f) }, o), "too small ranks after anything over (it could be made to measure)");
            Assert.AreEqual(-1, OpeningMath.Best(new PartDims[0], o));
        }

        [Test]
        public void TheOpeningGoesInTheContextAndTheSearch()
        {
            var o = Facade();
            var ctx = OpeningMath.Context(o);
            Assert.AreEqual(1.5, (double)ctx["w_m"], 1e-9);
            Assert.AreEqual(1.2, (double)ctx["h_m"], 1e-9);
            Assert.IsFalse(ctx.ContainsKey("d_m"), "no depth taped");
            var built = GrokContext.Build(new ContextSnapshot { Opening = ctx });
            Assert.AreSame(ctx, built["opening"]);
            Assert.IsFalse(GrokContext.Build(new ContextSnapshot()).ContainsKey("opening"), "no opening: no key");
            Assert.AreEqual("59 × 47¼″ opening", OpeningMath.Words(o));
        }

        // ---------------- Size & finish rules ----------------

        [Test]
        public void SizesStayWithinHalfToTwiceTheListing()
        {
            var listed = new PartDims { w = 1511.3f, h = 1207f, d = 82.55f };
            var c = PartLookMath.Clamp(new Vector3(5000f, 100f, 82.55f), listed);
            Assert.AreEqual(3022.6f, c.x, 1e-3f); Assert.AreEqual(603.5f, c.y, 1e-3f); Assert.AreEqual(82.55f, c.z, 1e-4f);
            Assert.AreEqual(10f, PartLookMath.Clamp(Vector3.one, new PartDims { w = 12f, h = 12f, d = 12f }).x, 1e-4f, "never under 10 mm");
            Assert.AreEqual("59½ × 47½ × 3¼″", PartLookMath.SizeWords(listed), "W × H × D in inches (windows are sold W × H)");
            UiSettings.UseUnits(UnitSystem.Metric);
            Assert.AreEqual("1511 × 1207 × 83 mm", PartLookMath.SizeWords(listed));
        }

        [Test]
        public void LookKeysAndStates()
        {
            var d = new PartDims { w = 1487.3f, h = 1187.3f, d = 82.55f };
            Assert.AreEqual("1487x1187x83|", PartLookMath.LookKey(d, null));
            Assert.AreEqual("1487x1187x83|matte black", PartLookMath.LookKey(d, " Matte Black "));
            Assert.IsTrue(PartLookMath.SameDims(d, new PartDims { w = 1487.6f, h = 1187f, d = 82.9f }));
            Assert.IsFalse(PartLookMath.SameDims(d, new PartDims { w = 1488f, h = 1187.3f, d = 82.55f }));
            var a = new PartLookState(new Vector3(1487.3f, 1187.3f, 82.55f), "Bronze");
            Assert.IsTrue(a.Same(new PartLookState(new Vector3(1487.5f, 1187.3f, 82.55f), "bronze")));
            Assert.IsFalse(a.Same(new PartLookState(new Vector3(1487.3f, 1187.3f, 82.55f), null)));
            Assert.IsNull(new PartLookState(Vector3.one, "  ").Finish, "blank = as listed");
        }

        [Test]
        public void FinishChipsAndTints()
        {
            var list = new List<string>();
            var white = new PartSpec { id = "w", finish = "White" };
            PartLookMath.FinishChoices(white, list);
            CollectionAssert.AreEqual(new[] { "White", "Black", "Bronze" }, list, "its own finish, then common frame colours");
            var listed = new PartSpec { id = "l", finish = "Desert Sand", finishes = new List<PartFinish> { new PartFinish { name = "Desert Sand", hex = "#D2C3A5" }, new PartFinish { name = "Black" } } };
            PartLookMath.FinishChoices(listed, list);
            CollectionAssert.AreEqual(new[] { "Desert Sand", "Black", "White" }, list, "listed first, no repeats");
            PartLookMath.FinishChoices(new PartSpec { id = "f", finish = "white", finishes = new List<PartFinish> { new PartFinish { name = "desert sand" } } }, list);
            CollectionAssert.AreEqual(new[] { "White", "Desert sand", "Black" }, list, "capitalised for the chips");
            Assert.IsTrue(PartLookMath.IsListedFinish(white, "white"), "its own finish is 'as listed'");
            Assert.IsTrue(PartLookMath.IsListedFinish(white, null));
            Assert.IsFalse(PartLookMath.IsListedFinish(white, "Bronze"));
            Assert.IsTrue(PartLookMath.TintFor(listed, "Desert Sand") is Color sand && Mathf.Abs(sand.r - 0xD2 / 255f) < 1e-4f, "the listing's hex");
            Assert.IsTrue(PartLookMath.TintFor(white, "Matte Black") is Color black && black.r < 0.2f, "a named colour");
            Assert.IsNull(PartLookMath.TintFor(white, "Unobtainium"));
            Assert.IsTrue(PartLookMath.Hex("#5B4636", out var c) && Mathf.Abs(c.g - 0x46 / 255f) < 1e-4f);
            Assert.IsFalse(PartLookMath.Hex("#12345", out _));
        }

        [Test]
        public void TheSavedPlacementsRowCarriesTheSizeAndFinish()
        {
            var e = new NotebookEntry("placement_pose", 0, "", new[] { new Vector3(0f, 4.1f, 0f) }, new DateTime(2026, 9, 26, 12, 0, 0), -1, "Placement A: window")
            { PartId = "jeld-wen", Slot = "A", Pose = PlacementMath.GltfPose(Vector3.zero, Quaternion.identity), DimsMm = new double[] { 1487.3, 1187.3, 82.55 }, Finish = "Bronze" };
            var row = (JObject)JObject.Parse(NotebookExporter.ToJson(new[] { e }, "synthetic-facade", "quest-test"))["entries"][0];
            Assert.AreEqual(1487.3, (double)row["dims_mm"]["w"], 1e-9);
            Assert.AreEqual(1187.3, (double)row["dims_mm"]["h"], 1e-9);
            Assert.AreEqual(82.6, (double)row["dims_mm"]["d"], 1e-9, "to the tenth of a mm");
            Assert.AreEqual("Bronze", (string)row["finish"]);
            e.DimsMm = null; e.Finish = null;
            row = (JObject)JObject.Parse(NotebookExporter.ToJson(new[] { e }, "synthetic-facade", "quest-test"))["entries"][0];
            Assert.IsNull(row["dims_mm"]); Assert.IsNull(row["finish"]);
        }

        [Test]
        public void TheResizeReplyParses()
        {
            var r = PartSpec.ParseAny<ResizeReply>("{\"part_id\":\"w\",\"dims_mm\":{\"w\":1487.3,\"d\":82.55,\"h\":1187.3},\"tier\":\"llm\",\"template\":\"window\"," +
                "\"finish\":\"Bronze\",\"finish_source\":\"imagine\",\"source\":\"template\",\"model_url\":\"/parts/w/model-size-1487x1187x83-bronze.glb\",\"seconds\":8.06,\"cached\":false}");
            Assert.AreEqual(1187.3f, r.dims_mm.h, 1e-4f);
            Assert.AreEqual("imagine", r.finish_source);
            Assert.AreEqual("template", r.source);
            var a = PartSpec.Parse("{\"id\":\"w\",\"dims_mm\":{\"w\":1,\"d\":1,\"h\":1},\"asset\":{\"tier\":\"llm\",\"status\":\"ready\",\"made_by\":\"xai:grok-4.20-0309-non-reasoning\",\"template\":\"window\",\"retried\":true,\"seconds\":2.4}}").asset;
            Assert.AreEqual("xai:grok-4.20-0309-non-reasoning", a.made_by);
            Assert.AreEqual("window", a.template);
            Assert.IsTrue(a.retried == true);
            Assert.AreEqual(2.4f, a.seconds.Value, 1e-4f);
            var spec = PartSpec.Parse("{\"id\":\"w\",\"name\":\"n\",\"dims_mm\":{\"w\":10,\"d\":20,\"h\":30}}");
            var big = spec.WithDims(new PartDims { w = 11, d = 21, h = 31 });
            Assert.AreEqual(10f, spec.dims_mm.w, "the listing's spec keeps its size");
            Assert.AreEqual(11f, big.dims_mm.w);
            Assert.AreEqual("w", big.id);
        }
    }

    /// assetgen: the part tool and the placement editor on the synthetic facade with a window taped (Editor gate): the
    /// held preview snaps into the opening only for a part that fits, the release makes an opening spot with its fit, and
    /// Size & finish steps, fits to the opening, tints and saves — each one undo step.
    public class AssetGenEditorTests : FacadeToolFixture
    {
        GameObject m_Go;
        PartTool m_Parts;
        PlacementEditor m_Editor;
        ToolManager m_Tools;
        SceneRoot m_Root;

        [SetUp]
        public void CreateEditor()
        {
            UiSettings.UseUnits(UnitSystem.Imperial);
            AppState.Set(AppMode.World);
            PartCatalogBuilderLayer();
            m_Go = new GameObject("assetgen-test");
            m_Root = m_Go.AddComponent<SceneRoot>();   // at the origin: SceneRoot space is the facade's (the plane probes need it)
            Services.Register(m_Root);
            m_Parts = m_Go.AddComponent<PartTool>();
            m_Parts.SetInput(Hub);
            Services.Register(m_Parts);
            EditHistory.Register(m_Parts);
            m_Tools = m_Go.AddComponent<ToolManager>();
            m_Tools.measure = Tool;
            m_Tools.parts = m_Parts;
            Services.Register(m_Tools);
            m_Editor = m_Go.AddComponent<PlacementEditor>();
            m_Editor.tool = m_Parts;
            m_Editor.Attach();
            m_Editor.SetInput(Hub);
            // The window taped: its width, then its height (Notebook ids in order).
            Notebook.Add(new NotebookEntry("measure", 1.5, "m", new[] { new Vector3(0.75f, 4.1f, -0.05f), new Vector3(-0.75f, 4.1f, -0.05f) }, DateTime.Now, -1, "width"));
            Notebook.Add(new NotebookEntry("measure", 1.2, "m", new[] { new Vector3(0.3f, 3.5f, -0.05f), new Vector3(0.3f, 4.7f, -0.05f) }, DateTime.Now, -1, "height"));
            Openings.Invalidate();
        }

        static void PartCatalogBuilderLayer() => AirTools.Editor.PartCatalogBuilder.EnsurePartsLayer();

        [TearDown]
        public void DestroyEditor()
        {
            m_Editor.Detach();
            m_Parts.ClearAll();
            Services.Unregister(m_Parts);
            Services.Unregister(m_Tools);
            Services.Unregister(m_Root);
            EditHistory.Unregister(m_Parts);
            foreach (var p in UnityEngine.Object.FindObjectsByType<PartInstance>(FindObjectsInactive.Include, FindObjectsSortMode.None)) UnityEngine.Object.DestroyImmediate(p.gameObject);
            UnityEngine.Object.DestroyImmediate(m_Go);
            Physics.SyncTransforms();
            PlacementFocus.Instance.Set(false);
            Openings.Invalidate();
        }

        /// A window-shaped test part: a "frame" slab and a "glass" pane (the template's node names), true size w × h × d mm.
        PartInstance Window(float w, float h, float d, string finish = "White")
        {
            var spec = new PartSpec
            {
                id = $"test-window-{w:0}", name = "Test Double Hung Vinyl Window", finish = finish,
                dims_mm = new PartDims { w = w, h = h, d = d }, mount = new PartMount { face = "-z" }, asset = new PartAsset { tier = "llm", status = "ready" },
            };
            var model = new GameObject("model");
            var frame = GameObject.CreatePrimitive(PrimitiveType.Cube);
            frame.name = "frame";
            frame.transform.SetParent(model.transform, false);
            frame.transform.localScale = new Vector3(w, h, d) * Mm;
            frame.transform.localPosition = new Vector3(0f, 0f, d * 0.5f * Mm);
            var glass = GameObject.CreatePrimitive(PrimitiveType.Cube);
            glass.name = "glass";
            glass.transform.SetParent(model.transform, false);
            glass.transform.localScale = new Vector3(w * 0.8f, h * 0.8f, 4f) * Mm;
            glass.transform.localPosition = new Vector3(0f, 0f, d * 0.5f * Mm);
            return PartInstance.Create(spec, model, null, null, "test");
        }

        const float Mm = 0.001f;
        static readonly Vector3 Eye = new Vector3(0f, 4.1f, 2.5f);

        void Aim(Vector3 target)
        {
            Hub.SetPointerOverride(ToolHand.Right, ToolInputHub.RayPose(Eye, target));
            m_Parts.Tick();
            Physics.SyncTransforms();
        }

        static Color Colour(Material m) => m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.GetColor("_Color") : Color.clear;

        static Vector3 FrontCentre(PartInstance p) => p.transform.TransformPoint(PlacementMath.AnchorLocal(p.LocalBox, PlacementAnchor.FrontCentre));

        [Test]
        public void TheWallsFaceIsTheOpeningsPlane()
        {
            Assert.IsTrue(Openings.TryCurrent(out var o), Openings.Report());
            Assert.AreEqual(0f, o.Centre.z, 0.002f, "refined from the tapes' −5 cm onto the wall's face (z = 0)");
            Assert.AreEqual(4.1f, o.Centre.y, 1e-4f);
            Assert.AreEqual(0f, o.Centre.x, 1e-4f);
            var ctx = Openings.CurrentContext();
            Assert.AreEqual(1.5, (double)ctx["w_m"], 1e-9);
        }

        [Test]
        public void ACameraBehindTheWallDoesntTurnTheOpeningRound()
        {
            var cam = new GameObject("behind-the-wall", typeof(Camera)) { tag = "MainCamera" };
            cam.transform.position = new Vector3(0f, 1f, -10f);
            try
            {
                Openings.Invalidate();
                Assert.IsTrue(Openings.TryCurrent(out var o), Openings.Report());
                Assert.AreEqual(0f, o.Centre.z, 0.002f, "on the face the tapes were taken at, not the wall's back (−0.30)");
                Assert.Less(Vector3.Angle(Vector3.forward, o.Out), 0.5f, "out of the wall on the tapes' side");
                Assert.Less(Vector3.Angle(Vector3.left, o.Right), 0.5f, "Right = Out × Up");
            }
            finally { UnityEngine.Object.DestroyImmediate(cam); Openings.Invalidate(); }
        }

        [Test]
        public void AFrameThatFitsSnapsInWhileHeldAndStaysThere()
        {
            var p = Window(1487.3f, 1187.3f, 82.55f);
            m_Parts.Hold(p);
            Aim(new Vector3(-1.75f, 4.1f, 0f));
            Assert.IsTrue(m_Parts.OnSurface, "on the wall beside the window");
            Assert.Greater(Vector3.Distance(FrontCentre(p), new Vector3(0f, 4.1f, 0f)), 1f, "a free placement: not pulled into the opening");
            Aim(new Vector3(0.2f, 4.2f, -0.1f));
            Assert.Less(Vector3.Distance(FrontCentre(p), new Vector3(0f, 4.1f, 0f)), 0.002f, $"held at the opening: front centre on its centre ({FrontCentre(p).ToString("F4")})");
            Assert.Less(Quaternion.Angle(p.transform.rotation, Quaternion.identity), 0.1f, "square to the opening (facing +Z)");
            Assert.AreEqual("✓ Fits the opening · ½″ spare", Copy.FitLine(p.Fit), "the preview shows the opening's fit");
            Assert.IsTrue(m_Parts.Release(ToolInputHub.RayPose(Eye, new Vector3(0.2f, 4.2f, -0.1f))));
            StringAssert.Contains("snapped into the opening", m_Parts.LastAction);
            Assert.Less(Vector3.Distance(FrontCentre(p), new Vector3(0f, 4.1f, 0f)), 0.002f, "placed where it snapped");
            StringAssert.Contains("opening:tape#", m_Editor.SpotOf(p).CavityId);
            Assert.AreEqual(PlacementAnchor.FrontCentre, m_Editor.SpotOf(p).Anchor);
            Assert.AreEqual(FitStatus.Green, p.Fit.Status, p.Fit.Headline);
        }

        [Test]
        public void AStockSizeThatDoesntFitIsNotPulledIn()
        {
            var p = Window(1511.3f, 1206.5f, 82.55f);
            m_Parts.Hold(p);
            Aim(new Vector3(0.2f, 4.2f, -0.1f));
            Assert.Greater(Vector3.Distance(FrontCentre(p), new Vector3(0f, 4.1f, 0f)), 0.005f, "not snapped");
            Assert.IsTrue(m_Parts.Release(ToolInputHub.RayPose(Eye, new Vector3(0.2f, 4.2f, -0.1f))));
            StringAssert.DoesNotContain("snapped", m_Parts.LastAction);
            Assert.AreEqual(FitStatus.Red, p.Fit.Status, "at the opening, it's judged against it");
            StringAssert.StartsWith("Too wide by", p.Fit.Verdict);
        }

        [Test]
        public void FitToOpeningMakesItToMeasureAndPutsItIn()
        {
            var p = Window(1511.3f, 1206.5f, 82.55f);
            m_Parts.Hold(p);
            Assert.IsTrue(m_Parts.Release(ToolInputHub.RayPose(Eye, new Vector3(-1.75f, 4.1f, 0f))), "on the wall beside the window");
            Assert.IsNull(m_Editor.SpotOf(p).CavityId, "a free placement");
            int undo = m_Editor.UndoCount;
            Assert.IsTrue(m_Editor.CanFitToOpening, "the window taped last");
            Assert.IsTrue(m_Editor.FitToOpening(), m_Editor.LastLook);
            Assert.AreEqual(1487.3f, p.Spec.dims_mm.w, 0.01f);
            Assert.AreEqual(1187.3f, p.Spec.dims_mm.h, 0.01f);
            Assert.AreEqual(82.55f, p.Spec.dims_mm.d, 0.01f, "its own depth");
            Assert.AreEqual("resized", p.SizeState, "stretched here (no server for a test part)");
            Assert.Less(Vector3.Distance(FrontCentre(p), new Vector3(0f, 4.1f, 0f)), 0.002f, "into the opening");
            Assert.AreEqual("✓ Fits the opening · ½″ spare", Copy.FitLine(p.Fit));
            Assert.AreEqual(1487.3f, p.MeasuredSizeMm().x, 0.5f, "the model is the new size");
            Assert.AreEqual(1487.3f, p.Box.size.x * 1000f, 0.01f, "and its collider");
            Assert.AreEqual(undo + 1, m_Editor.UndoCount, "one undo step");
            EditHistory.Undo();
            Assert.AreEqual(1511.3f, p.Spec.dims_mm.w, 0.01f, "undo: the listed size");
            Assert.AreEqual("as listed", p.SizeState);
            Assert.Greater(Vector3.Distance(FrontCentre(p), new Vector3(0f, 4.1f, 0f)), 1f, "… back where it was");
            EditHistory.Redo();
            Assert.AreEqual(1487.3f, p.Spec.dims_mm.w, 0.01f, "redo");
            Assert.Less(Vector3.Distance(FrontCentre(p), new Vector3(0f, 4.1f, 0f)), 0.002f);
        }

        [Test]
        public void SizeStepsAndFinishesAreEachOneStepAndSaved()
        {
            var p = Window(1487.3f, 1187.3f, 82.55f);
            m_Parts.Hold(p);
            Aim(new Vector3(0.2f, 4.2f, -0.1f));
            Assert.IsTrue(m_Parts.Release(ToolInputHub.RayPose(Eye, new Vector3(0.2f, 4.2f, -0.1f))));
            float before = p.Spec.dims_mm.w;
            var scale = p.Model.localScale;
            Assert.IsTrue(m_Editor.StepSize(0, +1));
            Assert.AreEqual(before + 9.525f, p.Spec.dims_mm.w, 0.01f, "⅜″ wider");
            Assert.AreNotEqual(scale, p.Model.localScale, "the model stretched");
            Assert.Less(Vector3.Distance(FrontCentre(p), new Vector3(0f, 4.1f, 0f)), 0.002f, "the anchor stays on the opening's centre");
            Assert.AreEqual(FitStatus.Green, p.Fit.Status, "3⅛ mm spare each side still fits");
            Assert.IsTrue(m_Editor.StepSize(2, -1));
            Assert.AreEqual(82.55f - 9.525f, p.Spec.dims_mm.d, 0.01f, "⅜″ shallower");
            Assert.IsTrue(m_Editor.SetLookFinish("Bronze"));
            Assert.AreEqual("Bronze", p.LookFinish);
            Assert.IsTrue(p.FinishColor.HasValue, "tinted (no server here)");
            foreach (var r in p.Model.GetComponentsInChildren<Renderer>(true))
            {
                var c = Colour(r.sharedMaterial);
                if (r.name == "glass") Assert.AreNotEqual(p.FinishColor.Value, c, "never the glass");
                else if (r.name == "frame")
                {
                    // Colours round-trip through the material (linear/gamma, float storage): compare within 1/255.
                    var want = p.FinishColor.Value;
                    Assert.Less(Mathf.Abs(want.r - c.r) + Mathf.Abs(want.g - c.g) + Mathf.Abs(want.b - c.b), 3f / 255f, $"the frame takes the finish ({want} vs {c})");
                }
            }
            Assert.IsTrue(m_Editor.SavePlacement("A"));
            var row = Notebook.Last;
            Assert.AreEqual("placement_pose", row.Tool);
            Assert.AreEqual(p.Spec.dims_mm.w, (float)row.DimsMm[0], 0.01f);
            Assert.AreEqual("Bronze", row.Finish);
            StringAssert.Contains("Bronze", row.DisplayDetail);
            // Change it, then load A: the saved size and finish come back with the pose (one step).
            Assert.IsTrue(m_Editor.SetSize(new Vector3(1480f, 1180f, 70f)));
            Assert.IsTrue(m_Editor.SetLookFinish(null));
            Assert.IsTrue(m_Editor.LoadPlacement("A"));
            Assert.AreEqual((float)row.DimsMm[0], p.Spec.dims_mm.w, 0.01f, "A's size");
            Assert.AreEqual("Bronze", p.LookFinish, "A's finish");
            // Undo all the way to as listed.
            for (int i = 0; i < 7; i++) EditHistory.Undo();
            Assert.AreEqual(1487.3f, p.Spec.dims_mm.w, 0.01f, "as placed");
            Assert.IsNull(p.LookFinish);
            Assert.IsFalse(p.FinishColor.HasValue, "the tint undone");
        }

        [Test]
        public void ADragOutOfTheOpeningMakesItFree()
        {
            var p = Window(1487.3f, 1187.3f, 82.55f);
            m_Parts.Hold(p);
            Aim(new Vector3(0.2f, 4.2f, -0.1f));
            Assert.IsTrue(m_Parts.Release(ToolInputHub.RayPose(Eye, new Vector3(0.2f, 4.2f, -0.1f))));
            Assert.IsTrue(m_Editor.Enter(p));
            Assert.IsTrue(m_Editor.Nudge(new Vector3(-1.9f, 0f, 0f), Vector3.zero));   // along the wall, 1.9 m to your left
            Assert.IsTrue(m_Editor.Exit());
            Assert.IsTrue(m_Editor.Enter(p));
            // A drag: grab it and let go where it is (the release re-spots it).
            var at = p.WorldBoxCentre;
            var pose = ToolInputHub.RayPose(Eye, at);
            Hub.RaisePressStart(ToolHand.Right, pose);
            Hub.RaisePressEnd(ToolHand.Right, ToolInputHub.RayPose(Eye, at + Vector3.left * 0.01f));
            Assert.IsNull(m_Editor.SpotOf(p).CavityId, $"out of the opening: a free placement ({m_Editor.LastLook})");
            m_Editor.Exit();
        }
    }
}
