using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AirTools.Agent.Grok;
using AirTools.Editor;
using AirTools.UI;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AirTools.Tests
{
    /// Declutter guard (docs/ux/declutter.md §3.3, S2): no two head-relative surfaces overlap, except the debts listed
    /// below that later slices pay off; plus the census budget (§2). The footprint maths, the collision rules and the
    /// budget are pure (they run in the offline runner); HeadRelativeSurfacesDontOverlap opens Main.unity (Editor gate).
    public class DeclutterTests
    {
        /// The overlaps still open (§1.3), as pairs of surface groups (a zone's name when the surface sits in a UiZones
        /// zone, else the surface's). A slice that clears one deletes its row here; a new overlap fails the test.
        /// Rows 18b, E1 and E2 aren't in §1.3: this test found them (see each comment).
        public static readonly (string row, string a, string b)[] KnownDebts =
        {
            // S9 (lane B) made the credit chip yield to a main-slot window at runtime, so its footprint still meets the
            // main slot on paper; the 8 s chip never shows over an open window.
            ("17", "Credit chip", "Main"),
            // Not in §1.3: a main-slot card open in passthrough (the Notebook after Take it home, the report's QR)
            // covers the Enter control (0.45 m, 25° down). No slice fixes it yet: the Enter control could yield to the
            // main slot like the pill (M3).
            ("E1", "Enter", "Main"),
            // Paid off: #2, #4, #6 and E2 (Enter × the reply in passthrough) with S3: toasts flash on the line and the
            // reply takes its place. #8–11 with S5: the job rail is the line's progress (#8 had become Status × Job
            // rail: the 28° reply at the line's place reached the rail's inner edge by 1.5°).
            // #1 (with #3), #12 and #13 with S6: the line docks on the window's rim and the pill yields to the main slot
            // and Settings (Apart).
            // #5, #14, #15 (with #7) with S4: the coach card is in the left side slot. #16, #18 and 18b with S9: the credit
            // moved to the wrist strip (its 8 s head chip sits at 34° down, clear of the pill) and the limits chip lost its
            // head fallback.
        };

        /// Pairs of groups that are never up together, or never overlap, by design (the test that proves it in the comment).
        public static readonly (string a, string b)[] Apart =
        {
            ("Status", "Main"),        // S6: the line (and a reply at its place) docks on the window's top rim, covering
                                       // only its title row (DockedLineClearsTheWindow)
            ("Pill", "Main"),          // S6: the pill yields while the main slot is open (PillYieldsToTheMainSlotAndSettings)
            ("Pill", "SideRight"),     // S6: … and while Settings is open
        };

        /// Surfaces that never show while the guide rail is on (the demo configuration this budget is for). The job rail
        /// isn't read unless it's `standalone` (S5).
        /// S3: a toast is a flash on the status line (ToastBecomesStatusFlash).
        public static readonly string[] HiddenWithTheRailOn = { "Toast" };

        static bool AreApart(string a, string b) => Apart.Any(p => p.a == a && p.b == b || p.a == b && p.b == a);

        // ---------------- the footprint maths (pure) ----------------

        static void AssertOverlap(UiFootprint a, UiFootprint b, float yaw, float pitch, string what)
        {
            var o = UiZones.Overlap(a, b);
            Assert.AreEqual(yaw, o.x, 0.15f, $"{what}: yaw overlap");
            Assert.AreEqual(pitch, o.y, 0.15f, $"{what}: pitch overlap");
        }

        /// §1.3's numbers from §1.1's placements and sizes (the method: an angular box per surface, eye at the
        /// placement's origin, gaze level; the coach card hangs down from its point).
        [Test]
        public void FootprintsReproduceTheInventory()
        {
            var main = UiZones.Footprint(0.45f, 20f, 0f, 0.36f, 0.44f);
            Assert.AreEqual(43.6f, main.Width, 0.1f, "main windows are 43.6° wide");
            Assert.AreEqual(52.1f, main.Height, 0.1f, "… and up to 52° tall");
            AssertOverlap(UiZones.Footprint(0.9f, 17f, 0f, 0.44f, 0.0754f), main, 27.5f, 4.8f, "#1 status line × main window");
            AssertOverlap(UiZones.Footprint(0.9f, 14f, -25f, 0.32f, 0.26f), main, 6.9f, 16.4f, "#9 job rail × main window");
            var parts = UiZones.Footprint(0.5f, 15f, -35f, 0.30f, 0.371f);
            AssertOverlap(UiZones.Footprint(0.9f, 14f, -38f, 0.32f, 0.26f), parts, 20.2f, 16.4f, "#10 job rail (−38°) × Find parts");
            AssertOverlap(UiZones.Footprint(0.45f, 12f, -44f, -0.125f, 0.125f, -0.2233f, 0f), parts, 23.2f, 23.4f, "#15 coach card (−44°) × Find parts");
            var scene = UiZones.Footprint(0.5f, 18f, 35f, 0.34f, 0.392f);
            AssertOverlap(UiZones.Footprint(0.45f, 24f, 22f, 0.2f, 0.084f), scene, 18.3f, 10.7f, "#13 pill × Scene window");
            AssertOverlap(UiZones.Footprint(0.62f, 36f, 30f, 0.26f, 0.034f), scene, 23.7f, 3.1f, "#16 credit chip × Scene window");
            AssertOverlap(UiZones.Footprint(0.62f, 36f, 30f, 0.26f, 0.034f), main, 3.6f, 3.1f, "#17 credit chip × main window");
            AssertOverlap(UiZones.Footprint(0.5f, 32f, -40f, 0.12f, 0.026f), parts, 13.7f, 3.0f, "#18 limits fallback × Find parts");
            Assert.AreEqual(3.5f, UiZones.Overlap(main, parts).x, 0.1f, "the tolerated edge: main × Find parts");
            Assert.AreEqual(5.6f, UiZones.Overlap(main, scene).x, 0.1f, "the tolerated edge: main × Scene");
        }

        [Test]
        public void EveryHeadRelativeZoneIsWhereTheSpecPutsIt()
        {
            Assert.AreEqual(UiZones.Status, UiZones.ZoneOf(0.9f, 17f, 0f));
            Assert.AreEqual(UiZones.Main, UiZones.ZoneOf(0.45f, 20f, 0f));
            Assert.AreEqual(UiZones.SideLeft, UiZones.ZoneOf(0.5f, 15f, -35f));
            Assert.AreEqual(UiZones.SideRight, UiZones.ZoneOf(0.5f, 18f, 35f));
            Assert.AreEqual(UiZones.Pill, UiZones.ZoneOf(0.45f, 24f, 22f));
            Assert.AreEqual(UiZones.Enter, UiZones.ZoneOf(0.45f, 25f, 0f));
            Assert.IsNull(UiZones.ZoneOf(0.45f, 12f, -24f), "today's coach card is in no zone");
            // No two zones overlap but the tolerated side-slot edges.
            var zones = UiZones.HeadRelative;
            for (int i = 0; i < zones.Length; i++)
            for (int j = i + 1; j < zones.Length; j++)
            {
                var a = zones[i]; var b = zones[j];
                if (a.Name == "Enter" || b.Name == "Enter") continue;   // passthrough only: the others are closed or elsewhere
                if (a.Name == "Status" && b.Name == "Main" || a.Name == "Main" && b.Name == "Status") continue;   // M3: the line docks on the window
                if ((a.Name == "Pill") != (b.Name == "Pill") && (a.Name == "Main" || b.Name == "Main" || a.Name == "SideRight" || b.Name == "SideRight")) continue;   // M3: the pill yields
                var o = UiZones.Overlap(a.Footprint(), b.Footprint());
                bool touching = o.x <= UiZones.EdgeEpsilonDeg || o.y <= UiZones.EdgeEpsilonDeg;
                bool sideEdge = (a.Name == "Main" || b.Name == "Main") && o.x <= UiZones.SideSlotEdgeDeg;
                Assert.IsTrue(touching || sideEdge, $"zones {a.Name} × {b.Name} overlap {o.x:0.0}° × {o.y:0.0}°");
            }
        }

        [Test]
        public void CollisionsMergeZonesTolerateSideEdgesAndRespectModes()
        {
            var surfaces = new List<UiSurface>
            {
                UiSurface.Centred("Notebook", 0.45f, 20f, 0f, 0.34f, 0.37f, UiModes.All),
                UiSurface.Centred("Checkout", 0.45f, 20f, 0f, 0.36f, 0.44f, UiModes.All),   // same zone: one surface
                UiSurface.Centred("Find parts", 0.5f, 15f, -35f, 0.30f, 0.371f, UiModes.World),   // 3.5° edge: tolerated
                UiSurface.Centred("Credit chip", 0.62f, 36f, 30f, 0.26f, 0.034f, UiModes.Inside),   // 3.6° edge, not a side slot
                UiSurface.Centred("Late card", 0.45f, 20f, 0f, 0.2f, 0.2f, UiModes.Passthrough),
                UiSurface.Centred("Enter", 0.45f, 25f, 0f, 0.15f, 0.06f, UiModes.Passthrough),
                UiSurface.Centred("Chip", 0.5f, 32f, -40f, 0.12f, 0.026f, UiModes.World),
            };
            var keys = UiZones.Collisions(surfaces).Select(c => c.key).ToList();
            CollectionAssert.AreEquivalent(new[] { "Credit chip × Main", "Chip × SideLeft", "Enter × Main" }, keys,
                "zones merge (no Notebook × Checkout), side edges are tolerated, Enter meets only passthrough surfaces");
            var apart = UiZones.Collisions(surfaces, (a, b) => a == "Enter" && b == "Main").Select(c => c.key);
            CollectionAssert.DoesNotContain(apart, "Enter × Main", "a pair that is never up together doesn't collide");
            Assert.AreEqual("A × B", UiZones.PairKey("B", "A"));
        }

        // ---------------- the budget (pure) ----------------

        [Test]
        public void TheBudgetHasEverySection2Row()
        {
            CollectionAssert.AreEqual(new[] { "passthrough-entry", "take-it-home", "tabletop", "measuring", "finding-parts", "checkout", "grok-job", "coaching", "survey", "settings" },
                UiBudget.Table.Select(b => b.Activity));
            foreach (var b in UiBudget.Table)
            {
                Assert.AreEqual(1, b.Hud, $"{b.Activity}: one heads-up line");
                Assert.LessOrEqual(b.HeadRelative, 3, $"{b.Activity}: ≤ 3 head-relative panels");
                Assert.LessOrEqual(b.Labels, 12, $"{b.Activity}: one pool of 12");
            }
            Assert.IsTrue(UiBudget.TryFind("Checkout", out var checkout));
            Assert.AreEqual(0, checkout.Pill, "checkout: the window's own primary is the next step");
            Assert.AreEqual(2, checkout.HeadRelative);
        }

        [Test]
        public void TheBudgetCheckNamesEveryExcess()
        {
            UiBudget.TryFind("checkout", out var b);
            var fits = new UiCounts { Hud = 1, Main = 1, HeadRelative = 2, Labels = 4 };
            Assert.IsTrue(b.Check(fits, out var none), none);
            Assert.AreEqual("", none);
            var worst = new UiCounts { Hud = 3, Main = 1, SideLeft = 1, Pill = 1, HeadRelative = 7, Labels = 14, GrokLayers = 2, Overlaps = 6 };
            Assert.IsFalse(b.Check(worst, out var why));
            foreach (var word in new[] { "hud 3 > 1", "sideL 1 > 0", "pill 1 > 0", "head-relative 7 > 2", "labels 14 > 6", "grok layers 2 > 1", "overlaps 6 > 0" })
                StringAssert.Contains(word, why);
            Assert.IsTrue(b.Check(new UiCounts { Hud = 1, Main = 1, HeadRelative = 2, Labels = -1 }, out _), "labels not counted: not checked");
            Assert.IsFalse(UiBudget.TryFind("juggling", out _));
        }

        // ---------------- S3: one heads-up line (the model: pure) ----------------

        [Test]
        public void AFlashTakesLineOneThenTheStepComesBack()
        {
            var m = new StatusLineModel();
            m.SetStep("Measure a door: pinch one corner", AirTools.Core.StepTone.Neutral);
            m.SetCoach("Pinch your left hand to save");
            m.ShowFlash("✓ Saved · Width 1′ 3⅜″", ColorRole.Success, 2.4f, 10f);
            Assert.AreEqual("✓ Saved · Width 1′ 3⅜″", m.Line1(10f));
            Assert.AreEqual("", m.Line2(10f, false), "no coach line under a flash");
            Assert.IsTrue(m.Showing(12.3f));
            Assert.AreEqual("Measure a door: pinch one corner", m.Line1(12.41f), "after the flash, the Step's text");
            Assert.AreEqual("Pinch your left hand to save", m.Line2(12.41f, false));
            Assert.AreEqual("", m.Line2(12.41f, docked: true), "docked: line 1 only");
        }

        [Test]
        public void AReplyTakesTheLinesPlaceAndAFlashWaitsForIt()
        {
            var m = new StatusLineModel();
            m.SetStep("Find hinges for this door", AirTools.Core.StepTone.Neutral);
            m.Yield(5f, 20f);                                                     // a 5 s reply at t = 20
            Assert.IsFalse(m.Showing(21f), "the line yields to the reply");
            m.ShowFlash("✓ Fits the door", ColorRole.Success, 2.4f, 21f);           // a toast during the reply
            Assert.IsFalse(m.Showing(24.9f), "still the reply's place");
            Assert.AreEqual("✓ Fits the door", m.Line1(25.1f), "the flash plays once the reply is gone …");
            Assert.IsTrue(m.Showing(25.1f));
            Assert.AreEqual("Find hinges for this door", m.Line1(27.5f), "… for its whole time");
            m.Yield(1f, 26f);
            Assert.AreEqual(25f + 2.4f, m.FlashUntil, 1e-4f, "a later reply doesn't move a flash already playing");
            m.Yield(0.5f, 26.1f);
            Assert.AreEqual(27f, m.YieldUntil, 1e-4f, "a shorter reply never cuts an earlier one short");
        }

        [Test]
        public void AnEmptyStepAndNoFlashHideTheLine()
        {
            var m = new StatusLineModel();
            Assert.IsFalse(m.Showing(0f));
            m.ShowFlash("Reset · ready for the next person", ColorRole.Info, 3f, 1f);
            Assert.IsTrue(m.Showing(1f), "a flash shows the line by itself");
            Assert.IsFalse(m.Showing(4.01f));
            Assert.AreEqual(1, m.Flashes);
        }

        [Test]
        public void TheJobsProgressHasLineOneUntilAFlash()
        {
            var m = new StatusLineModel();
            m.SetProgress("Do the whole job · 2 of 7  ✓ ✓ • • • · ·", "Picked the LG 12,000 BTU ductless mini-split indoor unit.", GrokRailTones.Info);
            Assert.IsTrue(m.Showing(0f), "the progress shows with no Step (the rail off)");
            Assert.AreEqual("Picked the LG 12,000 BTU ductless mini-split indoor unit.", m.Line2(0f, false), "line 2: the newest spoken result");
            m.SetStep("Find hinges for this door", AirTools.Core.StepTone.Neutral);
            StringAssert.StartsWith("Do the whole job", m.Line1(0f), "the job is the next step while it runs");
            m.ShowFlash("✓ Saved · Width 1′ 3⅜″", ColorRole.Success, 2.4f, 1f);
            Assert.AreEqual("✓ Saved · Width 1′ 3⅜″", m.Line1(1f), "a flash over the progress …");
            StringAssert.StartsWith("Do the whole job", m.Line1(3.5f), "… then the progress again");
            Assert.AreEqual("", m.Line2(3.5f, docked: true), "docked: line 1 only");
            m.ClearProgress();
            Assert.AreEqual("Find hinges for this door", m.Line1(3.5f), "after the summary's time, NextStep has the line");
        }

        // ---------------- S6: the line docks, the pill yields (pure) ----------------

        [Test]
        public void PillYieldsToTheMainSlotAndSettings()
        {
            Assert.IsFalse(NextStepPill.Yields(null, false), "nothing open: the pill shows");
            Assert.IsTrue(NextStepPill.Yields(new object(), false), "a main-slot window has the next step");
            Assert.IsTrue(NextStepPill.Yields(null, true), "… and so does Settings");
        }

        /// The main-slot windows (§1.1): Notebook, Sellers, Checkout (the tallest), Survey, Ladder, the Grok card at its
        /// tallest, the overlay card.
        static readonly (string name, float w, float h)[] MainWindows =
        {
            ("Notebook", 0.34f, 0.37f), ("Sellers", 0.36f, 0.37f), ("Checkout", 0.36f, 0.44f), ("Survey", 0.36f, 0.20f),
            ("Ladder", 0.32f, 0.23f), ("GrokCard", 0.36f, 0.44f), ("GrokOverlayCard", 0.36f, 0.22f),
            ("Placement", 0.36f, 0.42f),   // assetgen: the adjust panel with Size & finish (PlacementPanelBuilder.H)
            ("EditMoveBar", 0.26f, 0.10f),   // edit6dof: the Edit view's move bar (EditViewBuilder.BarW / BarH)
        };

        /// The line's pill docked (line 1 only: label at 0.9 m + padding) and a four-line reply at its place (body at 0.9 m,
        /// +8 % leading, + padding), both at their widest: GuideRailBuilder / MainSceneBuilder.BuildToast.
        const float LineW = 0.442f, LineH = 0.0162f * UiTheme.LineRatio + 0.03f;
        const float ReplyW = 0.53f, ReplyH = 4f * 0.018f * (UiTheme.LineRatio + 0.08f) + 0.03f;
        // glass: the fallback toast (rail off) docks too — two label lines at 0.6 m + padding, at its widest (+ the dot).
        const float ToastW = 0.26f + 2f * 0.022f + 0.02f, ToastH = 2f * 0.0108f * UiTheme.LineRatio + 0.022f;

        static float PitchDeg(Vector3 eye, Vector3 p) =>
            Mathf.Atan2(p.y - eye.y, Mathf.Sqrt((p.x - eye.x) * (p.x - eye.x) + (p.z - eye.z) * (p.z - eye.z))) * Mathf.Rad2Deg;

        /// M3, for every main window at eye heights 1.20–1.76 m: the docked line, a reply and (glass lane) the fallback toast
        /// sit wholly above the window's top rim — they cover none of the panel (the gate capture had the toast on the
        /// Adjust panel's Size & finish row) — the line's centre is ≤ +10° above the eye line (a reply's or toast's ≤ +13°),
        /// and each looks within 5 % of its floating size (it faces the eyes, scaled by distance).
        [Test]
        public void DockedLineClearsTheWindow() => DockedClears(MainWindows);

        static void DockedClears(IEnumerable<(string name, float w, float h)> windows) =>
            DockedClears(windows, (LineW, LineH), (ReplyW, ReplyH), (ToastW, ToastH));

        static void DockedClears(IEnumerable<(string name, float w, float h)> windows, (float w, float h) lineSize, (float w, float h) replySize,
            (float w, float h) toastSize)
        {
            const float d = 0.45f, down = 20f;
            float rad = down * Mathf.Deg2Rad;
            // A main-slot window faces the eyes (FloatingWindow: HeadAnchor.PoseFor, heading +z): its centre on the line
            // 20° below the eye line, its up tilted back by the same 20°.
            var dir = new Vector3(0f, -Mathf.Sin(rad), Mathf.Cos(rad));
            var up = new Vector3(0f, Mathf.Cos(rad), Mathf.Sin(rad));
            foreach (float eyeY in new[] { 1.20f, 1.42f, 1.60f, 1.76f })
            foreach (var (name, w, h) in windows)
            {
                var eye = new Vector3(0f, eyeY, 0f);
                var centre = eye + dir * d;
                var top = centre + up * (h * 0.5f);
                string at = $"{name} ({w:0.00} × {h:0.00} m) at eye {eyeY:0.00} m";
                float rim = PitchDeg(eye, top);
                foreach (var (what, pw, ph, dist, ceiling) in new[]
                {
                    ("the line", lineSize.w, lineSize.h, 0.9f, UiZones.DockCeilingDeg),
                    ("a reply", replySize.w, replySize.h, 0.9f, UiZones.DockCeilingDeg + 3f),
                    ("the toast", toastSize.w, toastSize.h, 0.6f, UiZones.DockCeilingDeg + 3f),
                })
                {
                    float scale = StatusLine.DockScale(eye, StatusLine.DockPoint(top, up), dist);
                    var dock = StatusLine.DockCentre(top, up, eye, ph, scale);
                    float r = (dock - eye).magnitude;
                    float pitch = PitchDeg(eye, dock);
                    Assert.LessOrEqual(pitch, ceiling, $"{at}: {what}'s docked centre at +{pitch:0.0}°");
                    // It faces the eyes, so its edges are atan(half size × scale / r) off its centre.
                    float half = Mathf.Atan(ph * 0.5f * scale / r) * Mathf.Rad2Deg;
                    Assert.GreaterOrEqual(pitch - half, rim + 0.05f,
                        $"{at}: {what} reaches {pitch - half:0.00}°, onto the window (its top rim at {rim:0.00}°)");
                    float floatingW = 2f * Mathf.Atan(pw * 0.5f / dist), dockedW = 2f * Mathf.Atan(pw * 0.5f * scale / r);
                    float floatingH = 2f * Mathf.Atan(ph * 0.5f / dist), dockedH = 2f * half * Mathf.Deg2Rad;
                    Assert.AreEqual(1f, dockedW / floatingW, 0.05f, $"{at}: {what}'s width");
                    Assert.AreEqual(1f, dockedH / floatingH, 0.05f, $"{at}: {what}'s height");
                }
            }
        }

        // ---------------- S3: one heads-up line (the components: Editor gate) ----------------

        float m_Now;
        readonly List<GameObject> m_Made = new List<GameObject>();

        [SetUp]
        public void Clock()
        {
            m_Now = 100f;
            UiClock.Override = () => m_Now;
        }

        [TearDown]
        public void Restore()
        {
            UiClock.Override = null;
            GuideRail.Enabled = false;
            foreach (var go in m_Made) if (go != null) Object.DestroyImmediate(go);
            m_Made.Clear();
        }

        StatusLine Line(string step)
        {
            var go = new GameObject("declutter-line");
            m_Made.Add(go);
            var line = go.AddComponent<StatusLine>();
            line.MakeCurrent();
            line.Show(new AirTools.Core.Step { Rule = AirTools.Core.RuleId.R52, Base = AirTools.Core.RuleId.R52, Status = step });
            return line;
        }

        UiToast Toast(bool reply)
        {
            var go = new GameObject(reply ? "declutter-reply" : "declutter-toast");
            m_Made.Add(go);
            var t = go.AddComponent<UiToast>();
            t.isReply = reply;
            t.maxLines = reply ? 4 : 2;
            t.maxWidth = reply ? 0.45f : 0.26f;
            t.seconds = reply ? 5f : 2.4f;
            t.surface = GlassSurface.Create(go.transform, "Pill", new Vector2(0.2f, 0.036f), GlassTier.ElevatedSolid, RadiusRole.Pill);
            t.text = UiText.Create(go.transform, "Text", "", reply ? TypeRole.Body : TypeRole.Label, reply ? 0.9f : 0.6f, width: t.maxWidth);
            t.surface.gameObject.SetActive(false);
            t.text.gameObject.SetActive(false);
            t.MakeCurrent();
            return t;
        }

        /// M1 (DC2): with the rail on, a toast is a flash on the status line; the toast pill stays down.
        [Test]
        public void ToastBecomesStatusFlash()
        {
            GuideRail.Enabled = true;
            var line = Line("Measure a door: pinch one corner");
            var toast = Toast(false);
            const string saved = "✓ Saved · Width 1′ 3⅜″";
            UiToast.Show(saved, ColorRole.Success);
            Assert.AreEqual(saved, line.Message);
            Assert.IsTrue(line.Showing);
            Assert.IsFalse(toast.Showing, "the toast pill stays down");
            Assert.AreEqual(0, toast.Presented);
            Assert.IsFalse(toast.surface.gameObject.activeSelf, "the toast's content is inactive");
            m_Now += UiToast.Duration(saved, toast.seconds, false) + 0.01f;
            Assert.AreEqual("Measure a door: pinch one corner", line.Message, "after Duration, the Step's text is back");
        }

        /// M1: a reply takes the line's place (the line yields for the reply's time), then the line is back.
        [Test]
        public void ReplyTakesTheLine()
        {
            GuideRail.Enabled = true;
            var line = Line("Find hinges for this door");
            var reply = Toast(true);
            UiToast.Reply("That's a 15-inch base cabinet door. Hinges: two, 35 mm cup.");
            Assert.IsTrue(reply.Showing, "the reply is up");
            Assert.IsFalse(line.Showing, "the line yields: one heads-up surface");
            Assert.IsTrue(line.Yielded);
            UiToast.Show("✓ Fits the door", ColorRole.Success);
            Assert.IsFalse(line.Showing, "a toast during the reply waits");
            m_Now += reply.ShownFor + 0.01f;
            Assert.IsFalse(reply.Showing);
            Assert.IsTrue(line.Showing, "the line is back after the reply");
            Assert.AreEqual("✓ Fits the door", line.Message, "… with the toast that waited");
        }

        /// DC2: with the rail off, today's toast is the fallback, and it waits while a reply is up.
        [Test]
        public void WithTheRailOffTheToastIsTheFallback()
        {
            GuideRail.Enabled = false;
            var line = Line("");
            var toast = Toast(false);
            var reply = Toast(true);
            UiToast.Show("Report saved on the headset", ColorRole.Success);
            Assert.IsTrue(toast.Showing, "the toast pill, as before");
            Assert.AreEqual(1, toast.Presented);
            Assert.AreEqual("", line.Message, "nothing on the (hidden) line");
            UiToast.Reply("Sure, the notebook is on its way to the laptop.");
            Assert.IsTrue(reply.Showing);
            Assert.IsFalse(toast.Showing, "the toast waits: one heads-up surface");
            m_Now += reply.ShownFor + 0.01f;
            Assert.IsFalse(reply.Showing);
        }

        /// M2 (DC1): the run is the status line's progress, with the rail off too; toasts flash on the line meanwhile.
        [Test]
        public void JobStripShowsOnTheLineWithTheRailOff()
        {
            GuideRail.Enabled = false;
            var mode = AirTools.Core.AppState.Mode;
            AirTools.Agent.Grok.GrokRails.Reset();
            try
            {
                AirTools.Core.AppState.Set(AirTools.Core.AppMode.World);
                var line = Line("");
                var toast = Toast(false);
                var go = new GameObject("declutter-job");
                m_Made.Add(go);
                var view = go.AddComponent<AirTools.Agent.Grok.JobRailView>();
                view.status = line;
                foreach (var a in Newtonsoft.Json.Linq.JArray.Parse(AirTools.Dev.GrokRailFixtures.JobHappy).Take(5))
                    AirTools.Agent.Grok.GrokRails.Apply((string)a["name"], a["args"] as Newtonsoft.Json.Linq.JObject ?? new Newtonsoft.Json.Linq.JObject(), 1);
                view.Refresh();
                Assert.IsTrue(line.InProgress);
                Assert.AreEqual("Do the whole job · 2 of 7  ✓ ✓ • • • · ·", AirTools.Agent.Grok.GrokRailText.StripTags(line.Message));
                Assert.IsTrue(line.Showing, "the progress shows with the guide rail off");
                Assert.IsFalse(view.Showing, "no rail panel of its own");
                UiToast.Show("✓ Fits the door", ColorRole.Success);
                Assert.AreEqual("✓ Fits the door", line.Message, "a toast flashes on the line: one heads-up surface");
                Assert.AreEqual(0, toast.Presented);
                AirTools.Core.AppState.Set(AirTools.Core.AppMode.Passthrough);
                view.Refresh();
                Assert.IsFalse(line.InProgress, "never in passthrough");
            }
            finally
            {
                AirTools.Agent.Grok.GrokRails.Reset();
                AirTools.Core.AppState.Set(mode);
            }
        }

        /// M3: the pill hides while a main-slot window is open and comes back with the Step's actions when it closes.
        [Test]
        public void PillYieldsWhileAWindowIsOpen()
        {
            WindowSlot.Reset();
            var go = new GameObject("declutter-pill");
            m_Made.Add(go);
            var pill = go.AddComponent<NextStepPill>();
            pill.content = new GameObject("Content");
            pill.content.transform.SetParent(go.transform, false);
            var window = new GameObject("declutter-window").AddComponent<FloatingWindow>();
            m_Made.Add(window.gameObject);
            try
            {
                var step = new AirTools.Core.Step
                {
                    Rule = AirTools.Core.RuleId.R34, Base = AirTools.Core.RuleId.R34, Status = "✓ Fits the door",
                    Primary = new AirTools.Core.StepAction("Compare prices", AirTools.Core.StepCommand.ShowSellers, "price"),
                };
                pill.Show(step);
                Assert.IsTrue(pill.Visible && pill.content.activeSelf, "nothing open: the pill shows");
                window.Open();
                pill.RefreshYield();
                Assert.IsFalse(pill.Visible, "the window's own primary is the next step");
                Assert.IsTrue(pill.Wanted && pill.Yielding);
                Assert.IsFalse(pill.content.activeSelf);
                window.Close();
                pill.RefreshYield();
                Assert.IsTrue(pill.Visible && pill.content.activeSelf, "back when the window closes");
                Assert.AreEqual("Compare prices", pill.Primary.Label);
            }
            finally { WindowSlot.Reset(); }
        }

        /// M3 in Main.unity: every main-slot window's own panel (Wire Main Scene) against the line and a reply as built.
        [Test]
        public void DockedLineClearsEveryWindowInMainUnity()
        {
            var scene = EditorSceneManager.OpenScene(MainSceneBuilder.ScenePath, OpenSceneMode.Additive);
            try
            {
                var roots = scene.GetRootGameObjects();
                var windows = new List<(string, float, float)>();
                foreach (var w in roots.SelectMany(r => r.GetComponentsInChildren<FloatingWindow>(true)).Where(w => w.mainSlot))
                {
                    Assert.AreEqual(0.45f, w.distance, 1e-3f, w.name);
                    Assert.AreEqual(20f, w.downDeg, 1e-3f, w.name);
                    Assert.AreEqual(0f, w.yawDeg, 1e-3f, w.name);
                    var p = FloatingWindow.FindPanel(w.transform);
                    if (p != null) windows.Add((w.name, p.size.x, Mathf.Max(p.size.y, w.name == "GrokCard" ? 0.44f : 0f)));   // the Grok card grows to fit
                }
                foreach (var n in roots.SelectMany(r => r.GetComponentsInChildren<AirTools.Notes.NotebookPanel>(true)))
                {
                    var p = FloatingWindow.FindPanel(n.transform);
                    if (p != null) windows.Add((n.name, p.size.x, p.size.y));
                }
                Assert.Greater(windows.Count, 5, "read the main-slot windows");
                var line = roots.SelectMany(r => r.GetComponentsInChildren<StatusLine>(true)).First();
                Assert.AreEqual(LineH, StatusLine.LineHeight(line.text) + line.padding, 0.002f, "the docked line as built");
                var reply = roots.SelectMany(r => r.GetComponentsInChildren<UiToast>(true)).First(t => t.isReply);
                Assert.AreEqual(ReplyH, reply.MaxSize.y, 0.002f, "the reply as built");
                Assert.AreEqual(ReplyW, reply.MaxSize.x, 0.002f, "the reply's width as built");
                // glass: the fallback toast docks as well; checked at its built size.
                var toast = roots.SelectMany(r => r.GetComponentsInChildren<UiToast>(true)).First(t => !t.isReply);
                Assert.AreEqual(0.6f, toast.distance, 1e-3f, "the toast's distance as built");
                DockedClears(windows, (LineW, LineH), (reply.MaxSize.x, reply.MaxSize.y), (toast.MaxSize.x, toast.MaxSize.y));
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        // ---------------- the scene (Editor gate) ----------------

        /// §3.3 rule 6: no head-relative surface overlaps another, except KnownDebts. Reads each surface's own placement
        /// and panel from Main.unity (run AirTools ▸ Wire Main Scene after a builder change), in the demo
        /// configuration (the guide rail on).
        [Test]
        public void HeadRelativeSurfacesDontOverlap()
        {
            var scene = EditorSceneManager.OpenScene(MainSceneBuilder.ScenePath, OpenSceneMode.Additive);
            try
            {
                var surfaces = ReadSurfaces(scene.GetRootGameObjects());
                var names = surfaces.Select(s => s.Name).Distinct().ToList();
                foreach (var need in new[] { "Status line", "Next-step pill", "Find parts", "Enter" })
                    CollectionAssert.Contains(names, need, $"read the {need} from Main.unity");
                Assert.Greater(surfaces.Count(s => s.Group == "Main"), 4, "read the main-slot windows");

                var found = UiZones.Collisions(surfaces, AreApart);
                var known = KnownDebts.ToDictionary(d => UiZones.PairKey(d.a, d.b), d => d.row);
                var fresh = found.Where(f => !known.ContainsKey(f.key))
                    .Select(f => $"{f.key}: {f.overlap.x:0.0}° × {f.overlap.y:0.0}°").ToList();
                var cleared = known.Where(k => found.All(f => f.key != k.Key)).Select(k => $"#{k.Value} {k.Key}").ToList();
                string all = string.Join("\n", found.Select(f => $"  {f.key}: {f.overlap.x:0.0}° × {f.overlap.y:0.0}°"));
                Assert.IsEmpty(fresh, $"new overlaps between head-relative surfaces (declutter §3.3):\n{string.Join("\n", fresh)}\nall:\n{all}");
                Assert.IsEmpty(cleared, $"debts that no longer overlap: delete their rows from DeclutterTests.KnownDebts:\n{string.Join("\n", cleared)}");
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        /// Every head-relative surface the builders place (declutter §1.1), with its own constants and panel.
        public static List<UiSurface> ReadSurfaces(IEnumerable<GameObject> roots)
        {
            var list = new List<UiSurface>();
            var rootList = roots.ToList();
            IEnumerable<T> All<T>() where T : Component => rootList.SelectMany(r => r.GetComponentsInChildren<T>(true));
            void Add(UiSurface s) { if (!HiddenWithTheRailOn.Contains(s.Name)) list.Add(s); }

            foreach (var l in All<StatusLine>())
            {
                var size = l.MaxSize;
                Add(UiSurface.Centred("Status line", l.distance, l.belowGazeDeg, 0f, size.x, size.y, UiModes.All, hud: true));
            }
            foreach (var t in All<UiToast>())
            {
                var size = t.MaxSize;
                Add(UiSurface.Centred(t.isReply ? "Reply card" : "Toast", t.distance, t.belowGazeDeg, 0f, size.x, size.y, UiModes.All, hud: true));
            }
            foreach (var j in All<AirTools.Agent.Grok.JobRailView>())
            {
                if (!j.standalone) continue;   // S5: the run is the status line's progress (M2); the rail's panel stays down
                var size = j.TypicalSize(7);
                foreach (float yaw in new[] { j.yawDeg, j.yawDegBesideWindow })
                    Add(UiSurface.Centred("Job rail", j.distance, j.belowGazeDeg, yaw, size.x, size.y, UiModes.Inside, hud: true));
            }
            foreach (var p in All<NextStepPill>())
            {
                var r = RectOf(p.transform, p.primary != null ? p.primary.surface : null,
                    p.secondary0 != null ? p.secondary0.surface : null, p.secondary1 != null ? p.secondary1.surface : null);
                Add(Placed("Next-step pill", p.distance, p.downDeg, p.yawDeg, r, UiModes.All));
            }
            var coaches = All<AirTools.Agent.Grok.CoachRailView>().ToList();
            foreach (var c in coaches)
            {
                if (c.window == null) continue;
                // The card hangs from its point (CoachRailView.Layout): its top edge sits on the placement line.
                float h = c.panel != null ? c.panel.size.y : 0.22f;
                // S4 (lane B): one pose, the left side slot; the old step-aside beside a main window is gone.
                Add(Placed("Coach card", c.window.distance, c.window.downDeg, c.yawDeg, new Rect(-c.width * 0.5f, -h, c.width, h), UiModes.Inside));
            }
            foreach (var w in All<FloatingWindow>())
            {
                if (coaches.Any(c => c.window == w)) continue;
                var r = RectOf(w.transform, PanelOf(w.transform));
                Add(Placed(w.gameObject.name, w.distance, w.downDeg, w.yawDeg, r, UiModes.All));
            }
            foreach (var n in All<AirTools.Notes.NotebookPanel>())
                Add(Placed(n.gameObject.name, n.distance, n.downDeg, 0f, RectOf(n.transform, PanelOf(n.transform)), UiModes.All));
            foreach (var b in All<AirTools.Parts.PartsBrowser>())
                Add(Placed("Find parts", b.distance, b.downDeg, b.yawDeg, RectOf(b.transform, b.panel), UiModes.World));
            foreach (var k in All<AirTools.Scene.SceneCreditChip>())
                Add(Placed("Credit chip", k.distance, k.downDeg, k.yawDeg, RectOf(k.transform, k.pill), UiModes.Inside));
            var fallback = LimitsFallback();
            if (fallback.HasValue)
                foreach (var chip in All<AirTools.Parts.LimitsChip>())
                    Add(Placed("Limits fallback", fallback.Value.x, fallback.Value.y, fallback.Value.z, RectOf(chip.transform, chip.pill), UiModes.Inside));
            foreach (var chest in All<AirTools.Core.ChestController>())
            {
                var anchor = chest.GetComponent<HeadAnchor>();
                if (anchor == null || chest.button == null) continue;
                var r = RectOf(anchor.transform, chest.button.surface);
                var hint = chest.hint != null ? chest.hint.GetComponent<TMPro.TextMeshPro>() : null;
                if (hint != null)
                {
                    float y = anchor.transform.InverseTransformPoint(hint.transform.position).y - StatusLine.LineHeight(hint) * 0.5f;
                    r.yMin = Mathf.Min(r.yMin, y);
                }
                Add(Placed("Enter", anchor.distance, anchor.downDeg, anchor.yawDeg, r, UiModes.Passthrough));
            }
            return list;
        }

        /// The limits chip's head fallback: (distance, down, yaw) from LimitsChip.LateUpdate's literal, or null once
        /// there is none (S9 removes it).
        static Vector3? LimitsFallback()
        {
            const string path = "Assets/AirTools/Runtime/Parts/LimitsChip.cs";
            if (!File.Exists(path)) return null;
            var m = Regex.Match(File.ReadAllText(path), @"HeadAnchor\.PoseFor\(head\.position, head\.forward, ([\d.]+)f, ([\d.]+)f, (-?[\d.]+)f\)");
            if (!m.Success) return null;
            float F(int g) => float.Parse(m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture);
            return new Vector3(F(1), F(2), F(3));
        }

        static UiSurface Placed(string name, float distance, float down, float yaw, Rect r, UiModes modes) => new UiSurface
        {
            Name = name, Distance = distance, DownDeg = down, YawDeg = yaw,
            XMin = r.xMin, XMax = r.xMax, YMin = r.yMin, YMax = r.yMax, Modes = modes,
        };

        static GlassSurface PanelOf(Transform window) =>
            window.GetComponentsInChildren<GlassSurface>(true).FirstOrDefault(s => s.name == "Panel");

        /// The rect the glass surfaces cover, in `root`'s plane (x right, y up).
        static Rect RectOf(Transform root, params GlassSurface[] parts)
        {
            bool any = false;
            float x0 = 0f, x1 = 0f, y0 = 0f, y1 = 0f;
            foreach (var s in parts)
            {
                if (s == null) continue;
                var c = root.InverseTransformPoint(s.transform.position);
                var sc = s.transform.lossyScale.x / Mathf.Max(1e-6f, root.lossyScale.x);
                float hx = s.size.x * 0.5f * sc, hy = s.size.y * 0.5f * sc;
                if (!any) { x0 = c.x - hx; x1 = c.x + hx; y0 = c.y - hy; y1 = c.y + hy; any = true; continue; }
                x0 = Mathf.Min(x0, c.x - hx); x1 = Mathf.Max(x1, c.x + hx); y0 = Mathf.Min(y0, c.y - hy); y1 = Mathf.Max(y1, c.y + hy);
            }
            return any ? Rect.MinMaxRect(x0, y0, x1, y1) : new Rect(-0.1f, -0.1f, 0.2f, 0.2f);
        }
    }
}
