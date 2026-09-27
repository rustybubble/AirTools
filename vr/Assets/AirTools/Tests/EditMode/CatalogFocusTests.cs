using System.Collections.Generic;
using AirTools.Parts;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// gaze-catalog: the catalog follows what the wearer looks at. Pure (the offline runner runs these): the focus
    /// classifier on synthetic hits (outside a building, in a kitchen, in a tilted scan, at the thresholds), the scan's
    /// frame, the ~1.2 s hold, the request and cache keys, the header chip's states and the model's focus / pin rules.
    public class CatalogFocusTests
    {
        // ---------------- helpers ----------------

        static SiteFrame Outdoor(float ground = 0f, float top = 30f) =>
            new SiteFrame { Valid = true, Up = Vector3.up, Ground = ground, Top = top, Indoor = false, UpFrom = "test" };

        static SiteFrame Kitchen(float floor = 0f) =>
            new SiteFrame { Valid = true, Up = Vector3.up, Ground = floor, Top = floor + 2.6f, Indoor = true, UpFrom = "test" };

        static GazeHit At(float x, float y, float z, Vector3 normal) =>
            new GazeHit { Hit = true, Point = new Vector3(x, y, z), Normal = normal.normalized };

        /// A normal tilted `deg` from up, towards +x.
        static Vector3 Tilted(float deg) => new Vector3(Mathf.Sin(deg * Mathf.Deg2Rad), Mathf.Cos(deg * Mathf.Deg2Rad), 0f);

        /// A rotation of `deg` about `axis` (managed: the offline runner has no engine for Quaternion.Euler / AngleAxis).
        static Quaternion Q(Vector3 axis, float deg)
        {
            var a = axis.normalized * Mathf.Sin(deg * 0.5f * Mathf.Deg2Rad);
            return new Quaternion(a.x, a.y, a.z, Mathf.Cos(deg * 0.5f * Mathf.Deg2Rad));
        }

        static CatalogFocus C(GazeHit h, SiteFrame f, CatalogFocus current = CatalogFocus.None) => GazeFocusMath.Classify(h, f, current);

        // ---------------- the classifier: outside a building (Zabel, the hospital) ----------------

        [Test]
        public void TiltIsMeasuredFromTheScansUp()
        {
            Assert.AreEqual(0f, GazeFocusMath.TiltDeg(Vector3.up, Vector3.up), 1e-3f);
            Assert.AreEqual(90f, GazeFocusMath.TiltDeg(Vector3.right, Vector3.up), 1e-3f);
            Assert.AreEqual(180f, GazeFocusMath.TiltDeg(Vector3.down, Vector3.up), 1e-3f);
            Assert.AreEqual(45f, GazeFocusMath.TiltDeg(Tilted(45f), Vector3.up), 1e-3f);
        }

        [Test]
        public void OutsideASlopedSurfaceHighUpIsTheRoof()
        {
            var f = Outdoor();   // a 30 m scan: roofs from 6 m (sloped) / 10.5 m (level)
            Assert.AreEqual(CatalogFocus.Roof, C(At(0, 25, 0, Tilted(44f)), f), "Zabel's 44° roof planes");
            Assert.AreEqual(CatalogFocus.Roof, C(At(0, 8, 0, Tilted(30f)), f));
            Assert.AreEqual(CatalogFocus.Roof, C(At(0, 20, 0, Vector3.up), f), "a flat roof high in the scan");
            Assert.AreEqual(CatalogFocus.Roof, C(At(0, 20, 0, Tilted(8f)), f), "roughly level");
        }

        [Test]
        public void OutsideLowSurfacesAreTheGround()
        {
            var f = Outdoor();
            Assert.AreEqual(CatalogFocus.Ground, C(At(0, 0.2f, 0, Vector3.up), f));
            Assert.AreEqual(CatalogFocus.Ground, C(At(0, 8, 0, Vector3.up), f), "a level terrace below the roof band (8 < 10.5 m)");
            Assert.AreEqual(CatalogFocus.Ground, C(At(0, 1.5f, 0, Tilted(25f)), f), "a bank: sloped but low");
            // A small scan: the roof needs at least 2.5 m.
            var small = Outdoor(0f, 4f);
            Assert.AreEqual(CatalogFocus.Ground, C(At(0, 2f, 0, Tilted(30f)), small));
            Assert.AreEqual(CatalogFocus.Roof, C(At(0, 3f, 0, Tilted(30f)), small));
        }

        [Test]
        public void OutsideNearVerticalIsTheWallAndFacingDownIsTheCeiling()
        {
            var f = Outdoor();
            Assert.AreEqual(CatalogFocus.Wall, C(At(0, 10, 0, Vector3.right), f));
            Assert.AreEqual(CatalogFocus.Wall, C(At(0, 1, 0, Tilted(70f)), f), "a wall at eye height is a wall, not ground");
            Assert.AreEqual(CatalogFocus.Wall, C(At(0, 28, 0, Tilted(110f)), f), "a wall high up is a wall, not roof");
            Assert.AreEqual(CatalogFocus.Ceiling, C(At(0, 3, 0, Vector3.down), f), "an eave's underside");
            Assert.AreEqual(CatalogFocus.Ceiling, C(At(0, 3, 0, Tilted(130f)), f));
            Assert.AreEqual(CatalogFocus.Wall, C(At(0, 3, 0, Tilted(120f)), f), "an overhang still reads as the wall");
        }

        [Test]
        public void AWindowOrDoorUnderTheRayIsAnOpeningWhateverItsNormal()
        {
            var h = At(0, 10, 0, Vector3.right);
            h.InOpening = true;
            Assert.AreEqual(CatalogFocus.Opening, C(h, Outdoor()));
            Assert.AreEqual(CatalogFocus.Opening, C(h, Kitchen()));
        }

        [Test]
        public void NoHitOrNoFrameIsNone()
        {
            Assert.AreEqual(CatalogFocus.None, C(default, Outdoor()), "the sky");
            Assert.AreEqual(CatalogFocus.None, C(At(0, 1, 0, Vector3.up), default), "no frame");
            Assert.AreEqual(CatalogFocus.None, C(new GazeHit { Hit = true, Point = Vector3.zero, Normal = Vector3.zero }, Outdoor()));
        }

        // ---------------- in a kitchen ----------------

        [Test]
        public void InAKitchenTheCounterIsLevelAt75To110cmAndTheFloorIsLow()
        {
            var f = Kitchen();
            Assert.AreEqual(CatalogFocus.Counter, C(At(0, 0.91f, 0, Vector3.up), f), "36\" counter");
            Assert.AreEqual(CatalogFocus.Counter, C(At(0, 0.96f, 0, Vector3.up), f), "the kitchen scan's counter at ×1.63");
            Assert.AreEqual(CatalogFocus.Ground, C(At(0, 0.02f, 0, Vector3.up), f), "the floor");
            Assert.AreEqual(CatalogFocus.None, C(At(0, 1.87f, 0, Vector3.up), f), "the top of the fridge");
            Assert.AreEqual(CatalogFocus.None, C(At(0, 0.55f, 0, Vector3.up), f), "a shelf inside a cabinet");
            Assert.AreEqual(CatalogFocus.None, C(At(0, 0.9f, 0, Tilted(40f)), f), "sloped things in a room");
            Assert.AreEqual(CatalogFocus.Ceiling, C(At(0, 2.5f, 0, Vector3.down), f));
        }

        [Test]
        public void InAKitchenAFrontIsTheFloorsBelowTheCounterTheCountersAtTheBacksplashAndTheWallAbove()
        {
            var f = Kitchen();
            Assert.AreEqual(CatalogFocus.Ground, C(At(0, 0.4f, 0, Vector3.right), f), "the dishwasher's / a base cabinet's front");
            Assert.AreEqual(CatalogFocus.Counter, C(At(0, 1.2f, 0, Vector3.right), f), "the backsplash");
            Assert.AreEqual(CatalogFocus.Wall, C(At(0, 1.8f, 0, Vector3.right), f), "the upper cabinets / the wall");
            var fridge = At(0, 1.6f, 0, Vector3.right);
            fridge.OnAppliance = true;
            Assert.AreEqual(CatalogFocus.Ground, C(fridge, f), "an appliance's front at eye height: the floor's things");
        }

        [Test]
        public void HeightsAreAboveTheFloorNotTheOrigin()
        {
            var f = Kitchen(-1.87f);   // the kitchen scan's floor at −1.15 package units × 1.63
            Assert.AreEqual(CatalogFocus.Counter, C(At(0, -1.87f + 0.96f, 0, Vector3.up), f));
            Assert.AreEqual(CatalogFocus.Ground, C(At(0, -1.86f, 0, Vector3.up), f));
            var o = Outdoor(-30.4f, 6.5f);   // Zabel: ground at −30.4, top +6.5
            Assert.AreEqual(CatalogFocus.Roof, C(At(0, -5f, 0, Tilted(44f)), o));
            Assert.AreEqual(CatalogFocus.Ground, C(At(0, -29.5f, 0, Vector3.up), o));
        }

        // ---------------- a tilted scan ----------------

        [Test]
        public void ATiltedScanClassifiesInItsOwnUpFrame()
        {
            var r = Q(Vector3.up, 35f) * Q(Vector3.right, 20f) * Q(Vector3.forward, 12f);   // a scan loaded 20° / 12° off gravity
            var f = Outdoor();
            f.Up = r * Vector3.up;
            GazeHit T(float y, Vector3 n, bool appliance = false) => new GazeHit { Hit = true, Point = r * new Vector3(3f, y, -2f), Normal = r * n.normalized, OnAppliance = appliance };
            Assert.AreEqual(CatalogFocus.Roof, C(T(25f, Tilted(44f)), f));
            Assert.AreEqual(CatalogFocus.Wall, C(T(10f, Vector3.right), f));
            Assert.AreEqual(CatalogFocus.Ground, C(T(0.3f, Vector3.up), f));
            Assert.AreEqual(CatalogFocus.Ceiling, C(T(3f, Vector3.down), f));
            // In world terms the "wall" normal is 20° off horizontal and the "ground" 20° off vertical: a world-up classifier
            // would have to guess; the scan's frame doesn't.
            var k = Kitchen();
            k.Up = r * Vector3.up;
            Assert.AreEqual(CatalogFocus.Counter, C(T(0.92f, Vector3.up), k));
            Assert.AreEqual(CatalogFocus.Ground, C(T(0.4f, Vector3.right), k));
        }

        [Test]
        public void TheFrameFindsATiltedScansUpFromItsAxisOrItsLevelPlanes()
        {
            var r = Q(Vector3.forward, 18f);
            var up = r * Vector3.up;
            var planes = new List<FramePlane>
            {
                new FramePlane(up, r * new Vector3(0, 0f, 0), 400f),       // the ground
                new FramePlane(up, r * new Vector3(0, 20f, 0), 150f),      // a flat roof
                new FramePlane(r * Vector3.right, r * new Vector3(5, 10f, 0), 80f),   // a wall
            };
            // The package's up is the world's (the loader didn't know): the planes put it right.
            var byPlanes = GazeFocusMath.Frame(planes, null, Vector3.up, false, Vector3.zero, Vector3.zero, indoor: false);
            Assert.IsTrue(byPlanes.Valid);
            Assert.AreEqual("planes", byPlanes.UpFrom);
            Assert.Less(Vector3.Angle(byPlanes.Up, up), 0.5f);
            Assert.AreEqual(0f, byPlanes.Ground, 1e-3f);
            Assert.AreEqual(20f, byPlanes.Top, 1e-3f);
            // A Manhattan axis close to the package's up wins (sign put right).
            var byAxis = GazeFocusMath.Frame(planes, new[] { r * Vector3.right, -(r * Vector3.up), r * Vector3.forward }, Vector3.up, false, Vector3.zero, Vector3.zero, false);
            Assert.AreEqual("axis", byAxis.UpFrom);
            Assert.Less(Vector3.Angle(byAxis.Up, up), 1e-3f);
            // A package levelled against gravity keeps its up (Zabel: its Manhattan axis is 2° off the regularised planes).
            var levelled = GazeFocusMath.Frame(planes, new[] { r * Vector3.up }, Vector3.up, true, Vector3.zero, Vector3.zero, false);
            Assert.AreEqual("package", levelled.UpFrom);
            Assert.AreEqual(Vector3.up, levelled.Up);
            // Too far from the package's up: not the up axis (a 40° tilt is a roof, not gravity).
            var far = GazeFocusMath.Frame(null, new[] { Q(Vector3.forward, 40f) * Vector3.up }, Vector3.up, false, new Vector3(-1, 0, -1), new Vector3(1, 5, 1), false);
            Assert.AreEqual("package", far.UpFrom);
        }

        [Test]
        public void TheGroundIsTheLowestLevelPlaneOrTheBoundsWhenTheScanReachesLower()
        {
            var planes = new List<FramePlane>
            {
                new FramePlane(Vector3.up, new Vector3(0, -174.1f, 0), 410f),   // the hospital's car park
                new FramePlane(Vector3.up, new Vector3(0, -133f, 0), 5943f),    // its roof
                new FramePlane(Vector3.up, new Vector3(0, -190f, 0), 1f),       // too small to stand on
            };
            var f = GazeFocusMath.Frame(planes, null, Vector3.up, true, new Vector3(-10, -181.2f, -10), new Vector3(10, -126.3f, 10), false);
            Assert.AreEqual(-180.7f, f.Ground, 1e-3f, "the street below: the collision's bottom + 0.5 m");
            Assert.AreEqual(-126.3f, f.Top, 1e-3f);
            var roof = At(0, -133f, 0, Vector3.up);
            Assert.AreEqual(CatalogFocus.Roof, C(roof, f), "47 m up");
            Assert.AreEqual(CatalogFocus.Ground, C(At(0, -174.1f, 0, Vector3.up), f), "the car park");
            // Indoors the floor plane stands, whatever the bounds (a scan's noise under the floor).
            var k = GazeFocusMath.Frame(new List<FramePlane> { new FramePlane(Vector3.up, new Vector3(0, -1.87f, 0), 2.7f) }, null, Vector3.up, true,
                new Vector3(-1, -1.96f, -1), new Vector3(1, 1.1f, 1), true);
            Assert.AreEqual(-1.87f, k.Ground, 1e-3f);
            Assert.IsTrue(k.Indoor);
            // Nothing at all: no frame.
            Assert.IsFalse(GazeFocusMath.Frame(null, null, Vector3.up, true, Vector3.zero, Vector3.zero, false).Valid);
        }

        [Test]
        public void APlanesAreaComesFromItsOutlineAndTheCalibration()
        {
            var rect = new[] { new Vector2(0, 0), new Vector2(2, 0), new Vector2(2, 3), new Vector2(0, 3) };
            Assert.AreEqual(6f, GazeFocusMath.OutlineArea(rect, 1f), 1e-4f);
            Assert.AreEqual(6f * 1.63f * 1.63f, GazeFocusMath.OutlineArea(rect, 1.63f), 1e-3f);
            Assert.AreEqual(0f, GazeFocusMath.OutlineArea(null, 1f));
        }

        [Test]
        public void AStructureWindowRectangleHoldsHitsOnItsFace()
        {
            // Zabel's o0 (a 0.415 × 1.296 m window on wall p102), corners in order u0v0, u1v0, u1v1, u0v1.
            var r = FocusRect.FromCorners(new Vector3(0, 0, 0), new Vector3(0.415f, 0, 0), new Vector3(0.415f, 1.296f, 0), new Vector3(0, 1.296f, 0),
                FocusRect.Opening, "a window");
            Assert.AreEqual(0.2075f, r.HalfW, 1e-4f);
            Assert.AreEqual(0.648f, r.HalfH, 1e-4f);
            Assert.IsTrue(r.Contains(new Vector3(0.2f, 0.6f, 0.1f), 0.05f, 0.35f));
            Assert.IsTrue(r.Contains(new Vector3(0.45f, 0.6f, 0f), 0.05f, 0.35f), "the frame's margin");
            Assert.IsFalse(r.Contains(new Vector3(0.6f, 0.6f, 0f), 0.05f, 0.35f), "the wall beside it");
            Assert.IsFalse(r.Contains(new Vector3(0.2f, 0.6f, 1f), 0.05f, 0.35f), "a metre in front of it");
            Assert.AreEqual(FocusRect.Opening, GazeFocusMath.RectKind("window", out var s));
            Assert.AreEqual("a window", s);
            Assert.AreEqual(FocusRect.Appliance, GazeFocusMath.RectKind("appliance", out _));
            Assert.AreEqual(-1, GazeFocusMath.RectKind("cabinet_door", out _), "a cabinet door is no opening");
        }

        // ---------------- hysteresis ----------------

        [Test]
        public void AtAThresholdTheCurrentFocusHolds()
        {
            var o = Outdoor();   // level roof band from 10.5 m
            Assert.AreEqual(CatalogFocus.Ground, C(At(0, 10f, 0, Vector3.up), o));
            Assert.AreEqual(CatalogFocus.Roof, C(At(0, 10f, 0, Vector3.up), o, CatalogFocus.Roof), "1 m in the roof's favour");
            Assert.AreEqual(CatalogFocus.Roof, C(At(0, 11f, 0, Vector3.up), o));
            Assert.AreEqual(CatalogFocus.Ground, C(At(0, 11f, 0, Vector3.up), o, CatalogFocus.Ground));
            // 62° off up: a steep roof, unless the wall is what's held (5°).
            Assert.AreEqual(CatalogFocus.Roof, C(At(0, 20, 0, Tilted(62f)), o));
            Assert.AreEqual(CatalogFocus.Wall, C(At(0, 20, 0, Tilted(62f)), o, CatalogFocus.Wall));
            var k = Kitchen();
            Assert.AreEqual(CatalogFocus.None, C(At(0, 1.15f, 0, Vector3.up), k));
            Assert.AreEqual(CatalogFocus.Counter, C(At(0, 1.15f, 0, Vector3.up), k, CatalogFocus.Counter), "8 cm in the counter's favour");
        }

        [Test]
        public void TheFocusHoldsForAboutASecondBeforeItChanges()
        {
            var g = new GazeFocusFilter();
            float t = 0f;
            bool Feed(CatalogFocus f, int n, string subject = null)
            {
                bool changed = false;
                for (int i = 0; i < n; i++) { changed |= g.Update(f, subject, t); t += 0.25f; }
                return changed;
            }
            // The first focus is taken quickly (half a second).
            Assert.IsFalse(Feed(CatalogFocus.Roof, 1));
            Assert.IsTrue(Feed(CatalogFocus.Roof, 1));
            Assert.AreEqual(CatalogFocus.Roof, g.Held);
            // A glance at the wall (1 s) doesn't move it.
            Assert.IsFalse(Feed(CatalogFocus.Wall, 4));
            Assert.AreEqual(CatalogFocus.Roof, g.Held);
            Assert.IsFalse(Feed(CatalogFocus.Roof, 6));
            // 1.25 s on the wall does.
            Assert.IsFalse(Feed(CatalogFocus.Wall, 4));
            Assert.IsTrue(Feed(CatalogFocus.Wall, 1));
            Assert.AreEqual(CatalogFocus.Wall, g.Held);
            // One stray sample only delays it.
            Assert.IsFalse(Feed(CatalogFocus.Roof, 3));
            Assert.IsFalse(Feed(CatalogFocus.Ground, 1));
            Assert.IsFalse(Feed(CatalogFocus.Roof, 1));
            Assert.IsTrue(Feed(CatalogFocus.Roof, 2));
            Assert.AreEqual(CatalogFocus.Roof, g.Held);
            Assert.AreEqual(3, g.Changes);
        }

        [Test]
        public void LookingAtNothingClearsTheFocusOnlyAfterTwoAndAHalfSeconds()
        {
            var g = new GazeFocusFilter();
            float t = 0f;
            for (int i = 0; i < 4; i++) { g.Update(CatalogFocus.Roof, null, t); t += 0.25f; }
            Assert.AreEqual(CatalogFocus.Roof, g.Held);
            for (int i = 0; i < 9; i++) { Assert.IsFalse(g.Update(CatalogFocus.None, null, t)); t += 0.25f; }   // 2.25 s of sky
            Assert.AreEqual(CatalogFocus.Roof, g.Held);
            Assert.IsTrue(g.Update(CatalogFocus.None, null, t));
            Assert.AreEqual(CatalogFocus.None, g.Held);
            // Nothing seen yet: none stays none, without a change.
            var fresh = new GazeFocusFilter();
            for (int i = 0; i < 20; i++) Assert.IsFalse(fresh.Update(CatalogFocus.None, null, i * 0.25f));
        }

        [Test]
        public void ASubjectWithinTheSameFocusFollowsAfterHalfTheHold()
        {
            var g = new GazeFocusFilter();
            float t = 0f;
            for (int i = 0; i < 3; i++) { g.Update(CatalogFocus.Ground, "the dishwasher", t); t += 0.25f; }
            Assert.AreEqual("the dishwasher", g.Subject);
            Assert.IsFalse(g.Update(CatalogFocus.Ground, "the fridge", t)); t += 0.25f;
            Assert.IsFalse(g.Update(CatalogFocus.Ground, "the dishwasher", t)); t += 0.25f;   // back: the pending one resets
            Assert.IsFalse(g.Update(CatalogFocus.Ground, "the fridge", t)); t += 0.25f;
            Assert.IsFalse(g.Update(CatalogFocus.Ground, "the fridge", t)); t += 0.25f;
            Assert.IsTrue(g.Update(CatalogFocus.Ground, "the fridge", t));
            Assert.AreEqual("the fridge", g.Subject);
            Assert.AreEqual(CatalogFocus.Ground, g.Held);
        }

        [Test]
        public void AHitchCountsAtMostHalfASecond()
        {
            var g = new GazeFocusFilter();
            g.Update(CatalogFocus.Roof, null, 0f);
            g.Update(CatalogFocus.Roof, null, 0.25f);
            Assert.AreEqual(CatalogFocus.Roof, g.Held);
            g.Update(CatalogFocus.Wall, null, 10f);   // the app was paused 9.75 s
            Assert.AreEqual(CatalogFocus.Roof, g.Held, "one sample after a pause isn't 10 s of evidence");
            Assert.AreEqual(0.5f, g.Evidence(CatalogFocus.Wall), 1e-4f);
            g.Reset();
            Assert.AreEqual(CatalogFocus.None, g.Held);
        }

        // ---------------- the request and the cache ----------------

        [Test]
        public void TheRequestCarriesTheFocusOnlyWhenThereIsOne()
        {
            Assert.AreEqual("/catalog?site=zabel-gymnasium&session_id=s1&focus=roof", CatalogText.CatalogPath("zabel-gymnasium", "s1", CatalogFocus.Roof));
            Assert.AreEqual("/catalog?site=kitchen&session_id=s1&focus=counter", CatalogText.CatalogPath("kitchen", "s1", CatalogFocus.Counter));
            Assert.AreEqual(CatalogText.CatalogPath("zabel-gymnasium", "s1"), CatalogText.CatalogPath("zabel-gymnasium", "s1", CatalogFocus.None));
            foreach (CatalogFocus f in System.Enum.GetValues(typeof(CatalogFocus)))
                if (f != CatalogFocus.None) Assert.AreEqual(f, CatalogFoci.Parse(CatalogFoci.Wire(f)), f.ToString());
            Assert.AreEqual(CatalogFocus.Ground, CatalogFoci.Parse("floor"));
            Assert.AreEqual(CatalogFocus.None, CatalogFoci.Parse(null));
            Assert.AreEqual(CatalogFocus.None, CatalogFoci.Parse("sky"));
        }

        [Test]
        public void EachSiteAndFocusHasItsOwnCopyOnTheHeadset()
        {
            Assert.AreEqual("zabel-gymnasium.json", CatalogText.CacheFile("zabel-gymnasium", CatalogFocus.None));
            Assert.AreEqual(CatalogText.CacheFile("zabel-gymnasium"), CatalogText.CacheFile("zabel-gymnasium", CatalogFocus.None));
            Assert.AreEqual("zabel-gymnasium__roof.json", CatalogText.CacheFile("zabel-gymnasium", CatalogFocus.Roof));
            Assert.AreEqual("kitchen__counter.json", CatalogText.CacheFile("kitchen", CatalogFocus.Counter));
            Assert.AreEqual("my_site__wall.json", CatalogText.CacheFile("My Site", CatalogFocus.Wall));
            Assert.AreEqual("built-in__ground.json", CatalogText.CacheFile(null, CatalogFocus.Ground));
            var keys = new HashSet<string>();
            foreach (var site in new[] { "zabel-gymnasium", "hospital-bg", "kitchen" })
                foreach (CatalogFocus f in System.Enum.GetValues(typeof(CatalogFocus)))
                {
                    Assert.IsTrue(keys.Add(CatalogText.FocusKey(site, f)), $"{site} {f}");
                    Assert.IsTrue(keys.Add("file:" + CatalogText.CacheFile(site, f)), $"{site} {f} file");
                }
        }

        [Test]
        public void TheAnswerSaysItsFocusAndTheSitesFoci()
        {
            var r = CatalogJson.Parse("{\"site\":\"zabel-gymnasium\",\"environment\":\"facade\",\"focus\":\"roof\",\"foci\":[\"roof\",\"wall\",\"ground\",\"ceiling\",\"opening\"]," +
                "\"categories\":[{\"id\":\"solar-panels\",\"title\":\"Solar panels\",\"items\":[]}]}");
            Assert.AreEqual("roof", r.focus);
            CollectionAssert.AreEqual(new[] { "roof", "wall", "ground", "ceiling", "opening" }, r.foci);
            var old = CatalogJson.Parse("{\"site\":\"kitchen\",\"categories\":[{\"id\":\"fridges\",\"items\":[]}]}");
            Assert.IsNull(old.focus, "an older laptop: no focus");
            Assert.IsNull(old.foci);
        }

        // ---------------- the header chip ----------------

        [Test]
        public void TheHeaderChipSaysWhatTheCatalogFollows()
        {
            string caret = CatalogText.Caret;
            Assert.AreEqual("All categories", CatalogText.FocusChip(CatalogFocus.Roof, null, false, supported: false, indoor: false));
            Assert.AreEqual("Looking at: the roof " + caret, CatalogText.FocusChip(CatalogFocus.Roof, null, false, true, false));
            Assert.AreEqual("Looking at: the wall " + caret, CatalogText.FocusChip(CatalogFocus.Wall, null, false, true, false));
            Assert.AreEqual("Looking at: the ground " + caret, CatalogText.FocusChip(CatalogFocus.Ground, null, false, true, false));
            Assert.AreEqual("Looking at: the floor " + caret, CatalogText.FocusChip(CatalogFocus.Ground, null, false, true, indoor: true));
            Assert.AreEqual("Looking at: the counter " + caret, CatalogText.FocusChip(CatalogFocus.Counter, null, false, true, true));
            Assert.AreEqual("Looking at: the dishwasher " + caret, CatalogText.FocusChip(CatalogFocus.Ground, "the dishwasher", false, true, true));
            Assert.AreEqual("Looking at: a window " + caret, CatalogText.FocusChip(CatalogFocus.Opening, "a window", false, true, false));
            Assert.AreEqual("Following your gaze " + caret, CatalogText.FocusChip(CatalogFocus.None, null, false, true, false));
            Assert.AreEqual("Pinned: the roof", CatalogText.FocusChip(CatalogFocus.Roof, null, true, true, false));
            Assert.AreEqual("Pinned: all categories", CatalogText.FocusChip(CatalogFocus.None, null, true, true, false));
            Assert.AreEqual("⌄", caret, "Inter's ⌄ (UiAssetsBuilder.Charset), full size");
        }

        [Test]
        public void TheChipsWordsAreInTheCharset()
        {
            foreach (CatalogFocus f in System.Enum.GetValues(typeof(CatalogFocus)))
                foreach (bool pinned in new[] { false, true })
                    foreach (bool indoor in new[] { false, true })
                    {
                        string text = System.Text.RegularExpressions.Regex.Replace(CatalogText.FocusChip(f, null, pinned, true, indoor), "<[^>]+>", "");
                        foreach (char ch in text)
                            Assert.IsTrue(AirTools.Editor.UiAssetsBuilder.Charset.IndexOf(ch) >= 0, $"'{ch}' (U+{(int)ch:X4}) in \"{text}\" is not in UiAssetsBuilder.Charset");
                    }
        }

        [Test]
        public void IndoorsIsTheLaptopsRoomElseAKitchenByName()
        {
            Assert.IsTrue(CatalogFoci.IsIndoor("kitchen", "zabel-gymnasium"));
            Assert.IsFalse(CatalogFoci.IsIndoor("facade", "kitchen"), "the laptop's word wins");
            Assert.IsFalse(CatalogFoci.IsIndoor("rooftop", null));
            Assert.IsTrue(CatalogFoci.IsIndoor("hospital", null), "a hospital room");
            Assert.IsTrue(CatalogFoci.IsIndoor(null, "kitchen"));
            Assert.IsFalse(CatalogFoci.IsIndoor(null, "zabel-gymnasium"));
            Assert.IsFalse(CatalogFoci.IsIndoor(null, "hospital-bg"), "a drone scan of a hospital is outside it");
        }

        // ---------------- the model ----------------

        static CatalogResponse Answer(string focus, params string[] ids)
        {
            var r = new CatalogResponse { site = "zabel-gymnasium", environment = "facade", title = "Facade", focus = focus, foci = new List<string> { "roof", "wall", "ground", "ceiling", "opening" } };
            foreach (var id in ids) r.categories.Add(new CatalogCategory { id = id, title = id, items = new List<CatalogItem> { new CatalogItem { part_id = id + "-1", name = id + " 1" } } });
            return r;
        }

        static CatalogModel Zabel(out CatalogResponse plain)
        {
            var m = new CatalogModel();
            m.SetSite("zabel-gymnasium");
            plain = Answer(null, "windows", "doors", "siding", "gutters", "exterior-lights", "window-ac", "hvac", "solar-panels");
            m.SetData("zabel-gymnasium", plain, CatalogSource.Server);
            return m;
        }

        [Test]
        public void TheModelFollowsTheGazeUnlessPinned()
        {
            var m = Zabel(out _);
            Assert.IsTrue(m.FocusSupported);
            Assert.AreEqual("Following your gaze " + CatalogText.Caret, m.FocusChipLabel);
            Assert.IsTrue(m.SetGazeFocus(CatalogFocus.Roof, null), "a new focus: follow");
            Assert.AreEqual(CatalogFocus.Roof, m.RequestFocus);
            Assert.IsFalse(m.SetGazeFocus(CatalogFocus.Roof, null), "the same: nothing to do");
            Assert.AreEqual("Looking at: the roof " + CatalogText.Caret, m.FocusChipLabel);
            // Pin: a glance elsewhere no longer moves the list.
            Assert.IsFalse(m.TogglePin());
            Assert.IsTrue(m.FocusPinned);
            Assert.AreEqual("Pinned: the roof", m.FocusChipLabel);
            Assert.IsFalse(m.SetGazeFocus(CatalogFocus.Wall, null));
            Assert.AreEqual(CatalogFocus.Roof, m.Focus);
            Assert.AreEqual(CatalogFocus.Wall, m.Gaze, "the gaze is still tracked");
            // Unpin: catch up with the gaze.
            Assert.IsTrue(m.TogglePin());
            Assert.AreEqual(CatalogFocus.Wall, m.Focus);
            Assert.IsFalse(m.FocusPinned);
        }

        [Test]
        public void TheModelAsksOnlyForFociTheSiteAnswers()
        {
            var m = new CatalogModel();
            m.SetSite("kitchen");
            var old = new CatalogResponse { site = "kitchen", environment = "kitchen" };
            old.categories.Add(new CatalogCategory { id = "fridges", title = "Fridges" });
            m.SetData("kitchen", old, CatalogSource.Server);
            m.SetGazeFocus(CatalogFocus.Counter, null);
            Assert.IsFalse(m.FocusSupported, "an older laptop");
            Assert.AreEqual(CatalogFocus.None, m.RequestFocus);
            Assert.AreEqual("All categories", m.FocusChipLabel);
            var k = Zabel(out _);
            k.SetGazeFocus(CatalogFocus.Counter, null);   // no counter outside
            Assert.AreEqual(CatalogFocus.None, k.RequestFocus);
            Assert.IsTrue(k.Serves(CatalogFocus.Wall));
        }

        [Test]
        public void AnotherFocusLeadsWithItsFirstCategoryAndSettles()
        {
            var m = Zabel(out _);
            int settle = m.Refocused;
            m.SetGazeFocus(CatalogFocus.Roof, null);
            Assert.IsTrue(m.SetData("zabel-gymnasium", Answer("roof", "solar-panels", "gutters", "roof-vents", "hvac"), CatalogSource.Server));
            Assert.AreEqual("solar-panels", m.CurrentCategory.id, "the roof leads with its own");
            Assert.AreEqual(CatalogFocus.Roof, m.ShownFocus);
            Assert.AreEqual(settle + 1, m.Refocused);
            // The same focus again (the laptop after the headset's copy): no settle, the category stays.
            m.SelectCategory(2);
            m.SetData("zabel-gymnasium", Answer("roof", "solar-panels", "gutters", "roof-vents", "hvac"), CatalogSource.Server);
            Assert.AreEqual("roof-vents", m.CurrentCategory.id);
            Assert.AreEqual(settle + 1, m.Refocused);
        }

        [Test]
        public void TheUsersCategoryStaysWhileTheNewFocusListsIt()
        {
            var m = Zabel(out _);
            m.SetGazeFocus(CatalogFocus.Roof, null);
            m.SetData("zabel-gymnasium", Answer("roof", "solar-panels", "gutters", "roof-vents", "hvac"), CatalogSource.Server);
            m.SelectCategory(1);   // the user taps Gutters
            m.NextPage();
            m.SetGazeFocus(CatalogFocus.Wall, null);
            m.SetData("zabel-gymnasium", Answer("wall", "wall-hvac", "windows", "gutters", "downspouts"), CatalogSource.Server);
            Assert.AreEqual("gutters", m.CurrentCategory.id, "still listed on the wall: kept");
            m.SetGazeFocus(CatalogFocus.Ground, null);
            m.SetData("zabel-gymnasium", Answer("ground", "pavers", "planters"), CatalogSource.Server);
            Assert.AreEqual("pavers", m.CurrentCategory.id, "not on the ground: the ground's first");
            Assert.AreEqual(0, m.Page);
            m.SetGazeFocus(CatalogFocus.Wall, null);
            m.SetData("zabel-gymnasium", Answer("wall", "wall-hvac", "windows", "gutters", "downspouts"), CatalogSource.Server);
            Assert.AreEqual("wall-hvac", m.CurrentCategory.id, "the pick was dropped with the ground: the wall leads again");
        }

        [Test]
        public void ASearchBeingTypedStaysWhenTheFocusMoves()
        {
            var m = Zabel(out _);
            m.SetQuery("gutt", 0f);
            Assert.AreEqual(CatalogMode.Search, m.Mode);
            m.SetGazeFocus(CatalogFocus.Wall, null);
            m.SetData("zabel-gymnasium", Answer("wall", "wall-hvac", "windows", "gutters"), CatalogSource.Server);
            Assert.AreEqual(CatalogMode.Search, m.Mode);
            Assert.AreEqual("gutt", m.Query);
        }

        [Test]
        public void AnotherSiteForgetsTheFocusAndThePin()
        {
            var m = Zabel(out _);
            m.SetGazeFocus(CatalogFocus.Roof, "a window");
            m.TogglePin();
            m.SetSite("kitchen");
            Assert.AreEqual(CatalogFocus.None, m.Focus);
            Assert.AreEqual(CatalogFocus.None, m.Gaze);
            Assert.IsFalse(m.FocusPinned);
            Assert.IsNull(m.FocusSubject);
            m.ResetSession();
            Assert.IsFalse(m.FocusPinned);
        }
    }
}
