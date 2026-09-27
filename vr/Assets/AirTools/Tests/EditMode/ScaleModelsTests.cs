using System.Collections.Generic;
using AirTools.Agent.Grok;
using AirTools.Parts;
using AirTools.Scene;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// scalemodels (1): per-site default scales (SiteScales). Pure, all in the offline runner:
    /// - the kitchen loads at ×1.63;
    /// - a person's tape overrides it, and Reset returns to it;
    /// - other sites are unaffected;
    /// - the context carries scale_source, and the dw1 gap reads ≈ 712 × 963 × 725 mm.
    public class ScaleModelsTests
    {
        static readonly SiteScale[] Table = SiteScales.Defaults();

        static SceneManifest Manifest(int revision = 1, string frame = "f") =>
            SceneManifest.Parse($"{{\"mesh\":{{\"file\":\"mesh.r{revision}.glb\"}},\"revision\":{revision},\"frame\":{{\"id\":\"{frame}\"}}}}");

        /// A PlayerPrefs stand-in: key → scale, key.by → stamp.
        sealed class Store
        {
            public readonly Dictionary<string, float> Values = new Dictionary<string, float>();
            public readonly Dictionary<string, int> Stamps = new Dictionary<string, int>();
            public float? Get(string key) => Values.TryGetValue(key, out var v) ? v : (float?)null;
            public int Stamp(string key) => Stamps.TryGetValue(key, out var v) ? v : 0;
            /// SceneStreamer.SaveCalibration: the scale under the revision's own key, stamped.
            public void SaveUser(string site, SceneManifest m, float k)
            {
                var key = SceneStreamer.CalibrationKeys(site, m)[0];
                Values[key] = k;
                Stamps[key] = SiteScales.Version;
            }
            /// SceneStreamer.ForgetCalibration.
            public void Forget(string site, SceneManifest m)
            {
                foreach (var key in SceneStreamer.CalibrationKeys(site, m)) { Values.Remove(key); Stamps.Remove(key); }
            }
            public SiteScales.Choice Load(string site, SceneManifest m) =>
                SiteScales.Choose(SceneStreamer.CalibrationKeys(site, m), Get, Stamp, SiteScales.DefaultFor(Table, site));
        }

        [Test]
        public void TheKitchenLoadsAtItsDefault()
        {
            Assert.AreEqual(1.63f, SiteScales.DefaultFor(Table, "kitchen"), 1e-6f);
            Assert.IsTrue(SiteScales.HasDefault(Table, "kitchen"));
            var c = new Store().Load("kitchen", Manifest());
            Assert.AreEqual(1.63f, c.Scale, 1e-6f);
            Assert.AreEqual(ScaleSource.SiteDefault, c.Source);
            Assert.IsNull(c.Key);
        }

        [Test]
        public void APersonsTapeOverridesTheDefault_AndReloadsWithIt()
        {
            var store = new Store();
            var m = Manifest();
            store.SaveUser("kitchen", m, 1.52f);
            var c = store.Load("kitchen", m);
            Assert.AreEqual(1.52f, c.Scale, 1e-6f);
            Assert.AreEqual(ScaleSource.User, c.Source);
            Assert.AreEqual("airtools.scale.kitchen.f.r1", c.Key);
        }

        [Test]
        public void AScaleSavedBeforeTheDefault_YieldsToIt()
        {
            // A test tape saved by an older build (no stamp): the user's ×1.63 wins on the kitchen.
            var store = new Store();
            store.Values["airtools.scale.kitchen.f.r1"] = 1.47f;
            Assert.AreEqual(ScaleSource.SiteDefault, store.Load("kitchen", Manifest()).Source);
            Assert.AreEqual(1.63f, store.Load("kitchen", Manifest()).Scale, 1e-6f);
            // … and the older per-frame key too.
            store.Values.Clear();
            store.Values["airtools.scale.kitchen.f"] = 1.2f;
            Assert.AreEqual(1.63f, store.Load("kitchen", Manifest()).Scale, 1e-6f);
            // A stamp from an older table doesn't count either.
            store.Values["airtools.scale.kitchen.f.r1"] = 1.4f;
            store.Stamps["airtools.scale.kitchen.f.r1"] = SiteScales.Version - 1;
            Assert.AreEqual(ScaleSource.SiteDefault, store.Load("kitchen", Manifest()).Source);
        }

        [Test]
        public void ASavedTimesOne_MeansNotSet()
        {
            var store = new Store();
            store.SaveUser("kitchen", Manifest(), 1f);
            Assert.AreEqual(1.63f, store.Load("kitchen", Manifest()).Scale, 1e-6f, "the old Reset saved ×1: the default applies");
            store.SaveUser("zabel-gymnasium", Manifest(3), 1f);
            var z = store.Load("zabel-gymnasium", Manifest(3));
            Assert.AreEqual(1f, z.Scale);
            Assert.AreEqual(ScaleSource.None, z.Source);
        }

        [Test]
        public void ResetReturnsToTheDefault()
        {
            var store = new Store();
            var m = Manifest();
            store.SaveUser("kitchen", m, 1.52f);
            Assert.AreEqual(ScaleSource.User, store.Load("kitchen", m).Source);
            // SceneStreamer.ResetCalibration: forget the person's scale, then ResetTo the site's default.
            store.Forget("kitchen", m);
            var reset = SiteScales.ResetTo(SiteScales.DefaultFor(Table, "kitchen"));
            Assert.AreEqual(1.63f, reset.Scale, 1e-6f);
            Assert.AreEqual(ScaleSource.SiteDefault, reset.Source);
            Assert.AreEqual(ScaleSource.SiteDefault, store.Load("kitchen", m).Source, "and it loads at the default next time");
            // A site without a default goes back to the capture's own.
            var none = SiteScales.ResetTo(SiteScales.DefaultFor(Table, "zabel-gymnasium"));
            Assert.AreEqual(1f, none.Scale);
            Assert.AreEqual(ScaleSource.None, none.Source);
        }

        [Test]
        public void OtherSitesAreUnaffected()
        {
            foreach (var site in new[] { "zabel-gymnasium", "hospital-bg", "gt-lcc-canopy", "gt-lcc-pavilion", "gt-lcc-tower", "synthetic-facade", ModelSites.BuiltIn, null, "" })
            {
                Assert.AreEqual(1f, SiteScales.DefaultFor(Table, site), site);
                var c = new Store().Load(site ?? "x", Manifest(2));
                Assert.AreEqual(1f, c.Scale, site);
                Assert.AreEqual(ScaleSource.None, c.Source, site);
            }
            // Their saved scales still count without a stamp (as before this change), and the kitchen's doesn't leak.
            var store = new Store();
            store.Values["airtools.scale.zabel-gymnasium.f.r3"] = 1.2f;
            store.SaveUser("kitchen", Manifest(), 1.5f);
            var z = store.Load("zabel-gymnasium", Manifest(3));
            Assert.AreEqual(1.2f, z.Scale, 1e-6f);
            Assert.AreEqual(ScaleSource.User, z.Source);
            Assert.AreEqual(1f, store.Load("hospital-bg", Manifest(3)).Scale);
        }

        [Test]
        public void UnusableDefaultsAreIgnored()
        {
            var table = new[] { new SiteScale("a", 0f), new SiteScale("b", float.NaN), new SiteScale("c", 9f), new SiteScale("d", 1.3f) };
            Assert.AreEqual(1f, SiteScales.DefaultFor(table, "a"));
            Assert.AreEqual(1f, SiteScales.DefaultFor(table, "b"));
            Assert.AreEqual(1f, SiteScales.DefaultFor(table, "c"), "outside ScaleCalibration's ×0.2–×5");
            Assert.AreEqual(1.3f, SiteScales.DefaultFor(table, "d"), 1e-6f);
            Assert.AreEqual(1f, SiteScales.DefaultFor(null, "d"));
        }

        [Test]
        public void SourceWords()
        {
            Assert.AreEqual("site_default", SiteScales.Wire(ScaleSource.SiteDefault));
            Assert.AreEqual("user", SiteScales.Wire(ScaleSource.User));
            Assert.AreEqual("none", SiteScales.Wire(ScaleSource.None));
            Assert.AreEqual(ScaleSource.None, SiteScales.Consistent(1f, ScaleSource.SiteDefault), "×1 is never a site default");
            Assert.AreEqual(ScaleSource.User, SiteScales.Consistent(1.4f, ScaleSource.None), "a set scale nobody explained is the person's");
            Assert.AreEqual(ScaleSource.SiteDefault, SiteScales.Consistent(1.63f, ScaleSource.SiteDefault));
        }

        [Test]
        public void TheWristStripAndSettingsSayWhereTheScaleCameFrom()
        {
            Assert.AreEqual("Scale ×1.63 · set for this scan", SiteScales.DefaultLine(1.63f));
            string w = LimitsChip.ScaleWord(true, true, 1.63f, ScaleSource.SiteDefault);
            Assert.AreEqual("Scale ×1.63", w);
            Assert.AreSame(w, LimitsChip.ScaleWord(true, true, 1.63f, ScaleSource.SiteDefault), "one string while it holds: the strip doesn't recompose");
            Assert.AreEqual(LimitsChip.ScaleSet, LimitsChip.ScaleWord(true, true, 1.52f, ScaleSource.User), "a person's tape: Scale ✓");
            Assert.AreEqual(LimitsChip.ScaleNotSet, LimitsChip.ScaleWord(true, true, 1f, ScaleSource.None));
            Assert.AreEqual(LimitsChip.ScaleSet, LimitsChip.ScaleWord(true, false, 1f, ScaleSource.None), "the built-in facade");
            Assert.IsNull(LimitsChip.ScaleWord(false, false, 1f, ScaleSource.None));
            Assert.AreEqual("Scale ×1.63", LimitsChip.FirstLine(null, false, w));
        }

        [Test]
        public void TheContextSendsTheScaleAndItsSource()
        {
            var s = new ContextSnapshot { Site = "kitchen", Scale = 1.63, ScaleSource = SiteScales.Wire(ScaleSource.SiteDefault) };
            var ctx = GrokContext.Build(s);
            Assert.AreEqual(1.63, (double)ctx["scale"], 1e-9);
            Assert.AreEqual("site_default", ctx["scale_source"]);
            s.ScaleSource = null;
            Assert.IsFalse(GrokContext.Build(s).ContainsKey("scale_source"), "not known: left out");
            s.ScaleSource = "user"; s.Site = null;
            var builtIn = GrokContext.Build(s);
            Assert.IsFalse(builtIn.ContainsKey("scale") || builtIn.ContainsKey("scale_source"), "the built-in scene: no site, no scale");
        }

        /// The kitchen's dw1 (dishwasher) cavity from parts.r1.json: 437 × 591 × 445 mm raw.
        const string KitchenParts = @"{""schema"":""airtools.parts/1"",""components"":[{""id"":""dw1"",""label"":""dishwasher"",""removable"":true,
            ""cavity"":{""node"":""cavity_dw1"",""estimated"":true,""size_m"":{""w"":0.4371239555227864,""h"":0.5910683631857461,""d"":0.44471521460996},
            ""insert"":{""p"":[-1.1124338549617911,-1.1739590903323305,-0.45780372703636896],
                        ""axes"":[[0.05001103972710264,0.0,-0.9987486650331074],[0.0,1.0,0.0],[0.9987486650331074,0.0,0.05001103972710264]]},
            ""box"":{""min_rud"":[0.1830349097490933,-1.1739590903323305,-1.5786522825707137],""max_rud"":[0.6201588652718797,-0.5828907271465844,-1.1339370679607537]}}}]}";

        [Test]
        public void TheKitchenDishwasherGapReadsAboutSevenTwelveByNineSixtyThreeBySevenTwentyFive()
        {
            var doc = ScenePartsDoc.Parse(KitchenParts);
            var dw1 = doc.Find("dw1");
            Assert.IsNotNull(dw1);
            Assert.IsTrue(CavityBox.TryFrom(dw1, out var box, out var why), why);
            var gap = new Gap { Component = dw1, Box = box, Calibration = SiteScales.DefaultFor(Table, "kitchen") };
            var mm = gap.SizeMm;
            Assert.AreEqual(712f, mm.x, 1.5f, "W");
            Assert.AreEqual(963f, mm.y, 1.5f, "H");
            Assert.AreEqual(725f, mm.z, 1.5f, "D");
            // And what the context sends (Gaps.Context: real metres, to the millimetre).
            var ctx = Gaps.Context(gap.Id, gap.Noun, gap.SizeM, null);
            Assert.AreEqual(0.713, (double)ctx["w_m"], 0.0015);
            Assert.AreEqual(0.963, (double)ctx["h_m"], 0.0015);
            Assert.AreEqual(0.725, (double)ctx["d_m"], 0.0015);
        }
    }
}

namespace AirTools.Tests
{
    /// scalemodels (3): the toolbox ring's "More" is now "Settings", with a gear. Pure, in the offline runner (the builder
    /// source check runs from the project root). The ring as Wire builds it: ToolRingTests.MainRingHasSixItems.
    public class SettingsRenameTests
    {
        [Test]
        public void TheRingItemIsSettingsWithAGear()
        {
            Assert.AreEqual("", AirTools.UI.Icons.Settings, "Phosphor gear-six (checked with fontTools in both Phosphor fonts)");
            StringAssert.Contains(AirTools.UI.Icons.Settings, AirTools.UI.Icons.All, "in the icon atlas (AirTools ▸ Build UI Assets)");
            Assert.AreEqual("Settings", AirTools.Structure.ScenePanel.Title);
            StringAssert.Contains("Settings ▸ Take out", AirTools.UI.Copy.NoGap);
            StringAssert.DoesNotContain("More", AirTools.UI.Copy.NoGap);
        }

        [Test]
        public void TheBuilderNamesNoMoreOnTheRing()
        {
            const string builder = "Assets/AirTools/Editor/MainSceneBuilder.cs";
            Assert.IsTrue(System.IO.File.Exists(builder), "run from the project root");
            string code = System.IO.File.ReadAllText(builder);
            StringAssert.Contains("(\"Settings\", AirTools.UI.Icons.Settings, false, default, AirTools.Tools.ToolboxAction.ToggleScenePanel", code);
            StringAssert.DoesNotContain("(\"More\",", code);
            StringAssert.DoesNotContain("\"Title\", \"More\"", code);
        }
    }
}
