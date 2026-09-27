using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AirTools.Scene;
using NUnit.Framework;

namespace AirTools.Tests
{
    /// scalemodels (2): Model view never goes empty when the laptop is away. Pure, all in the offline runner (a temp dir
    /// stands in for persistentDataPath/scenes):
    /// - the listing is kept on the headset (round trip);
    /// - offline, the switcher lists what's cached plus the built-in facade, and the rest reads "Not downloaded";
    /// - the prefetch queue's order, skips and resume.
    public class HeadsetModelsTests
    {
        static readonly string[] Served = { "gt-lcc-canopy", "gt-lcc-pavilion", "gt-lcc-tower", "hospital-bg", "kitchen", "synthetic-facade",
                                            "synthetic-facade-parts", "synthetic-facade-preview", "synthetic-facade-small", "zabel-gymnasium" };

        static List<SceneListing> Listing() => Served.Select(s => new SceneListing
        {
            site = s, revision = s == "kitchen" || s.StartsWith("synthetic") ? 1 : s == "hospital-bg" || s == "zabel-gymnasium" ? 3 : 2, quality = "full",
        }).ToList();

        static string TempRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "airtools-scalemodels-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        static void Delete(string root) { try { Directory.Delete(root, true); } catch (Exception) { } }

        static void Put(string root, string site, string file, string text = "x")
        {
            var p = SceneCache.PathOf(root, site, file);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
        }

        /// The kitchen's scene.json as served: scene parts (split mesh, collision and cavities), cameras, structure.
        const string KitchenJson = @"{""name"":""kitchen"",""revision"":1,""quality"":""full"",
            ""mesh"":{""file"":""mesh.r1.glb""},""collision"":{""file"":""collision.r1.glb""},""cameras"":""cameras.r1.json"",
            ""structure"":{""file"":""structure.r1.json""},
            ""parts"":{""file"":""parts.r1.json"",""mesh"":""mesh.parts.r1.glb"",""cavities"":""cavity.r1.glb"",""collision"":""collision.parts.r1.glb""}}";
        const string ZabelJson = @"{""name"":""zabel-gymnasium"",""revision"":3,""mesh"":{""file"":""mesh.r3.glb""},""collision"":{""file"":""collision.r3.glb""},
            ""cameras"":""cameras.r3.json"",""structure"":{""file"":""structure.r3.json""}}";
        const string PartsJson = @"{""schema"":""airtools.parts/1"",""components"":[{""id"":""dw1"",""label"":""dishwasher""}]}";

        // ---------------- the kept listing ----------------

        [Test]
        public void TheListingRoundTripsThroughTheHeadset()
        {
            var root = TempRoot();
            try
            {
                var list = Listing();
                Assert.IsTrue(SceneCache.WriteListing(root, list));
                var back = SceneCache.ReadListing(root);
                CollectionAssert.AreEqual(list.Select(s => s.ToString()), back.Select(s => s.ToString()));
                Assert.AreEqual(list[3].updated_at, back[3].updated_at);
                Assert.IsFalse(File.Exists(SceneCache.ListingPath(root) + ".part"), "written through a temp file");
                // An empty answer doesn't forget every model.
                Assert.IsFalse(SceneCache.WriteListing(root, new List<SceneListing>()));
                Assert.AreEqual(list.Count, SceneCache.ReadListing(root).Count);
                // Garbage or nothing: no models, no throw.
                Assert.IsEmpty(SceneCache.ParseListing("{not json"));
                Assert.IsEmpty(SceneCache.ParseListing(""));
                Assert.AreEqual(1, SceneCache.ParseListing(@"[{""site"":""kitchen"",""revision"":1},{""revision"":2},{""site"":"" ""}]").Count);
                Assert.IsEmpty(SceneCache.ReadListing(Path.Combine(root, "nowhere")));
            }
            finally { Delete(root); }
        }

        [Test]
        public void TheKnownModelsAreTheListingThenCachedSitesItDoesntName()
        {
            var listing = new List<SceneListing> { new SceneListing { site = "zabel-gymnasium", revision = 3 }, new SceneListing { site = "strasbourg-cathedral-spire" } };
            var known = SceneCache.Known(listing, new[] { "kitchen", "zabel-gymnasium" }, new[] { "strasbourg-cathedral-spire" });
            CollectionAssert.AreEqual(new[] { "zabel-gymnasium", "kitchen" }, known.Select(k => k.site));
            Assert.AreEqual(3, known[0].revision);
            Assert.AreEqual(0, known[1].revision, "a cached site the listing doesn't name: revision unknown");
            Assert.IsEmpty(SceneCache.Known(null, null));
        }

        // ---------------- what's on the headset ----------------

        [Test]
        public void APackageFetchesItsSplitFilesWithParts_ElseOnePiece()
        {
            var m = SceneManifest.Parse(KitchenJson);
            var doc = ScenePartsDoc.Parse(PartsJson);
            CollectionAssert.AreEqual(new[] { "parts.r1.json", "cameras.r1.json", "structure.r1.json", "collision.parts.r1.glb", "cavity.r1.glb", "mesh.parts.r1.glb" },
                SceneCache.PackageFiles(m, doc), "the split package, small files first");
            CollectionAssert.AreEqual(new[] { "cameras.r1.json", "structure.r1.json", "collision.r1.glb", "mesh.r1.glb" },
                SceneCache.PackageFiles(m, null), "the parts file not read: in one piece, as the load falls back");
            CollectionAssert.AreEqual(new[] { "cameras.r3.json", "structure.r3.json", "collision.r3.glb", "mesh.r3.glb" },
                SceneCache.PackageFiles(SceneManifest.Parse(ZabelJson), null));
            Assert.IsEmpty(SceneCache.PackageFiles(null, null));
        }

        [Test]
        public void ASiteIsOnTheHeadsetOnceEveryFileAndItsSceneJsonAreIn()
        {
            var root = TempRoot();
            try
            {
                Assert.IsFalse(SceneCache.IsComplete(root, "kitchen", out _), "nothing cached");
                // The files, but no scene.json yet (the prefetch writes it last): not on the headset.
                foreach (var f in new[] { "parts.r1.json", "cameras.r1.json", "structure.r1.json", "collision.parts.r1.glb", "cavity.r1.glb", "mesh.parts.r1.glb" })
                    Put(root, "kitchen", f, f == "parts.r1.json" ? PartsJson : "x");
                Assert.IsFalse(SceneCache.IsComplete(root, "kitchen", out _));
                Put(root, "kitchen", SceneCache.ManifestFile, KitchenJson);
                Assert.IsTrue(SceneCache.IsComplete(root, "kitchen", out int rev));
                Assert.AreEqual(1, rev);
                // A file missing (or empty: a half write) and it isn't.
                File.WriteAllText(SceneCache.PathOf(root, "kitchen", "mesh.parts.r1.glb"), "");
                Assert.IsFalse(SceneCache.IsComplete(root, "kitchen", out _));
                // Zabel in one piece.
                Put(root, "zabel-gymnasium", SceneCache.ManifestFile, ZabelJson);
                foreach (var f in new[] { "cameras.r3.json", "structure.r3.json", "collision.r3.glb" }) Put(root, "zabel-gymnasium", f);
                Assert.IsFalse(SceneCache.IsComplete(root, "zabel-gymnasium", out _), "no mesh yet");
                Put(root, "zabel-gymnasium", "mesh.r3.glb");
                Assert.IsTrue(SceneCache.IsComplete(root, "zabel-gymnasium", out rev));
                Assert.AreEqual(3, rev);
                CollectionAssert.AreEqual(new[] { "kitchen", "zabel-gymnasium" }, SceneCache.CachedSites(root));
            }
            finally { Delete(root); }
        }

        [Test]
        public void TheCardPictureIsTheRevisionsElseTheNewestOlderOne()
        {
            var root = TempRoot();
            try
            {
                Assert.IsNull(SceneCache.PicturePath(root, "kitchen", 1));
                Put(root, "zabel-gymnasium", SceneCache.PictureName(2));
                Put(root, "zabel-gymnasium", SceneCache.PictureName(3));
                StringAssert.EndsWith("site-picture.r3.jpg", SceneCache.PicturePath(root, "zabel-gymnasium", 3));
                StringAssert.EndsWith("site-picture.r3.jpg", SceneCache.PicturePath(root, "zabel-gymnasium", 4), "r4's isn't in: the newest older one");
                StringAssert.EndsWith("site-picture.r2.jpg", SceneCache.PicturePath(root, "zabel-gymnasium", 2));
                StringAssert.EndsWith("site-picture.r3.jpg", SceneCache.PicturePath(root, "zabel-gymnasium", 0), "revision unknown: the newest");
            }
            finally { Delete(root); }
        }

        // ---------------- the switcher offline ----------------

        [Test]
        public void OfflineTheSwitcherOpensWhatsCachedPlusTheBuiltInFacade()
        {
            var known = Listing().Select(s => s.site).ToList();
            var cached = new HashSet<string> { "kitchen", "gt-lcc-tower", "synthetic-facade-parts" };
            CollectionAssert.AreEqual(new[] { "kitchen", "gt-lcc-tower", ModelSites.BuiltIn }, ModelSites.OfflineList(known, cached.Contains),
                "cached ∪ built-in, in the switcher's order; the test packages stay hidden");
            CollectionAssert.AreEqual(new[] { ModelSites.BuiltIn }, ModelSites.OfflineList(known, _ => false), "nothing cached: the facade");
            // The row still shows every model, the ones not on the headset dimmed.
            Assert.IsTrue(ModelSites.Available("kitchen", false, cached.Contains));
            Assert.IsFalse(ModelSites.Available("zabel-gymnasium", false, cached.Contains));
            Assert.IsTrue(ModelSites.Available("zabel-gymnasium", true, cached.Contains), "with the laptop there anything listed opens");
            Assert.IsTrue(ModelSites.Available(ModelSites.BuiltIn, false, _ => false));
            var failed = new List<string>();
            Assert.AreEqual(ModelCardState.NotDownloaded, ModelSites.StateOf("zabel-gymnasium", "kitchen", null, null, failed, false));
            Assert.AreEqual(ModelCardState.Current, ModelSites.StateOf("kitchen", "kitchen", null, null, failed, true));
            Assert.AreEqual(ModelCardState.Loading, ModelSites.StateOf("hospital-bg", "kitchen", "hospital-bg", null, failed, false), "a load that already started keeps its state");
            Assert.AreEqual(ModelCardState.NotDownloaded, ModelSites.StateOf("hospital-bg", "kitchen", null, null, new List<string> { "hospital-bg" }, false), "failed offline: not downloaded");
            Assert.AreEqual("Not downloaded", ModelSites.CardText("Zabel gym", ModelCardState.NotDownloaded));
            Assert.AreEqual("Zabel gym", ModelSites.CardName("Zabel gym", ModelCardState.NotDownloaded), "with a badge the name stays");
            Assert.AreEqual("Not downloaded", ModelSites.Badge(ModelCardState.NotDownloaded));
            Assert.AreEqual("", ModelSites.Badge(ModelCardState.Idle));
            Assert.AreEqual("Loading…", ModelSites.CardName("Zabel gym", ModelCardState.Loading));
        }

        [Test]
        public void OfflineTheArrowsStepOverWhatIsntOnTheHeadset()
        {
            var row = ModelSites.Order(Served);   // kitchen, zabel, hospital, canopy, pavilion, tower, built-in
            var cached = new HashSet<string> { "kitchen", "gt-lcc-tower" };
            bool Open(string s) => ModelSites.Available(s, false, cached.Contains);
            Assert.AreEqual("gt-lcc-tower", ModelSites.Step(row, "kitchen", +1, Open));
            Assert.AreEqual(ModelSites.BuiltIn, ModelSites.Step(row, "gt-lcc-tower", +1, Open));
            Assert.AreEqual("kitchen", ModelSites.Step(row, ModelSites.BuiltIn, +1, Open), "wraps");
            Assert.AreEqual(ModelSites.BuiltIn, ModelSites.Step(row, "kitchen", -1, Open));
            Assert.AreEqual("zabel-gymnasium", ModelSites.Step(row, "kitchen", +1, s => true), "online: every model");
            Assert.AreEqual("zabel-gymnasium", ModelSites.Step(row, "kitchen", +1, null));
            Assert.IsNull(ModelSites.Step(new[] { "a" }, "a", +1, _ => false));
        }

        [Test]
        public void TheSettingsCountIsTheSwitchersModels()
        {
            var cached = new HashSet<string> { "kitchen", "zabel-gymnasium", "hospital-bg", "gt-lcc-canopy" };
            PrefetchPlan.Count(Served, cached.Contains, out int on, out int total);
            Assert.AreEqual(7, total, "six scans and the built-in facade; the test packages don't count");
            Assert.AreEqual(5, on, "four scans on the headset and the facade");
            Assert.AreEqual("Models on the headset: 5 of 7", PrefetchPlan.CountText(on, total));
        }

        // ---------------- the prefetch queue ----------------

        [Test]
        public void ThePrefetchGoesInTheSwitchersOrder_SkippingWhatsOnTheHeadsetAndTheTestPackages()
        {
            var have = new Dictionary<string, int> { ["kitchen"] = 1, ["gt-lcc-canopy"] = 1 };
            bool OnHeadset(string site, int rev) => have.TryGetValue(site, out int r) && r >= rev;
            var order = PrefetchPlan.Order(Listing(), OnHeadset);
            CollectionAssert.AreEqual(new[] { "zabel-gymnasium", "hospital-bg", "gt-lcc-canopy", "gt-lcc-pavilion", "gt-lcc-tower" }, order,
                "the kitchen is on the headset; canopy's r1 is older than the listed r2; no synthetic-*, no built-in");
            // A newer revision on the laptop is fetched again.
            var listing = Listing();
            listing.First(s => s.site == "kitchen").revision = 2;
            CollectionAssert.Contains(PrefetchPlan.Order(listing, OnHeadset), "kitchen");
            Assert.IsEmpty(PrefetchPlan.Order(null, OnHeadset));
        }

        [Test]
        public void ThePrefetchWaitsForLoads_SkipsTheLoadingSite_AndRetriesAFailureLater()
        {
            var order = new List<string> { "zabel-gymnasium", "hospital-bg", "gt-lcc-tower" };
            var failed = new Dictionary<string, float>();
            Assert.AreEqual("zabel-gymnasium", PrefetchPlan.Next(order, false, null, failed, 100f, 120f));
            Assert.IsNull(PrefetchPlan.Next(order, true, "kitchen", failed, 100f, 120f), "never while a load runs");
            Assert.AreEqual("hospital-bg", PrefetchPlan.Next(order, false, "zabel-gymnasium", failed, 100f, 120f), "not the site a load is fetching");
            failed["zabel-gymnasium"] = 90f;
            Assert.AreEqual("hospital-bg", PrefetchPlan.Next(order, false, null, failed, 100f, 120f), "failed 10 s ago: later");
            Assert.AreEqual("zabel-gymnasium", PrefetchPlan.Next(order, false, null, failed, 211f, 120f), "tried again after 120 s");
            failed["hospital-bg"] = 100f; failed["gt-lcc-tower"] = 100f; failed["zabel-gymnasium"] = 100f;
            Assert.IsNull(PrefetchPlan.Next(order, false, null, failed, 101f, 120f), "all failed just now");
            Assert.IsNull(PrefetchPlan.Next(null, false, null, failed, 101f, 120f));
        }

        [Test]
        public void APausedDownloadResumesWithTheFilesStillMissing()
        {
            var root = TempRoot();
            try
            {
                var m = SceneManifest.Parse(KitchenJson);
                Put(root, "kitchen", "parts.r1.json", PartsJson);
                var doc = SceneCache.CachedParts(root, "kitchen", m);
                Assert.IsNotNull(doc);
                var files = SceneCache.PackageFiles(m, doc);
                // Paused after cameras and structure (a load started): those stay, the rest are still to come.
                Put(root, "kitchen", "cameras.r1.json");
                Put(root, "kitchen", "structure.r1.json");
                Put(root, "kitchen", "mesh.parts.r1.glb.part");   // the file in flight when it stopped doesn't count
                var missing = SceneCache.Missing(files, f => SceneCache.FileCached(root, "kitchen", f, m.revision));
                CollectionAssert.AreEqual(new[] { "collision.parts.r1.glb", "cavity.r1.glb", "mesh.parts.r1.glb" }, missing);
                Assert.IsEmpty(SceneCache.Missing(files, _ => true));
                CollectionAssert.AreEqual(files, SceneCache.Missing(files, null));
            }
            finally { Delete(root); }
        }

        [Test]
        public void CacheWritesAreAtomic()
        {
            var root = TempRoot();
            try
            {
                var p = SceneCache.PathOf(root, "kitchen", "cameras.r1.json");
                Assert.IsTrue(SceneCache.WriteAtomic(p, System.Text.Encoding.UTF8.GetBytes("one")));
                Assert.IsTrue(SceneCache.WriteAtomic(p, System.Text.Encoding.UTF8.GetBytes("two")), "over an existing file");
                Assert.AreEqual("two", File.ReadAllText(p));
                Assert.IsFalse(File.Exists(p + ".part"));
            }
            finally { Delete(root); }
        }
    }
}
