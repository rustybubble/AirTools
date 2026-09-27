using System.Collections.Generic;
using System.IO;
using System.Linq;
using AirTools.Agent;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Parts;
using NUnit.Framework;

namespace AirTools.Tests
{
    /// catalog: the Catalog's pure logic — the contract parsing, the lazy local filter and its ranking, the debounce and the
    /// stale-answer drop, the built-in fallback by kind of place, the Fits chip, the keyboard's field, and the catalog
    /// opening with no tape. No engine calls: these run in the offline runner too (the scene checks are CatalogSceneTests).
    public class CatalogTests
    {
        // ---------------- fixtures ----------------

        const string Kitchen = @"{
          ""site"": ""kitchen"", ""environment"": ""kitchen"", ""title"": ""Kitchen"", ""generated_at"": ""2026-09-26T22:00:00Z"",
          ""extra_field"": {""ignored"": true},
          ""categories"": [
            {""id"": ""fridges"", ""title"": ""Fridges"", ""query"": ""refrigerator"", ""items"": [
              {""part_id"": ""frigidaire-frfg1723av-873cff"", ""name"": ""31.5 in. 18 cu. ft. Counter Depth French Door Refrigerator"", ""price_usd"": 1499.0,
               ""dims_mm"": {""w"": 800.1, ""h"": 1724.7, ""d"": 787.4}, ""model_ready"": true, ""seller"": ""Home Depot""},
              {""part_id"": ""frigidaire-efr751-2646f5"", ""name"": ""7.5 cu. ft. Mini Refrigerator in Platinum"", ""price_usd"": 379.99,
               ""dims_mm"": {""w"": 546.1, ""h"": 1409.7, ""d"": 569.0}, ""model_ready"": false}
            ]},
            {""id"": ""dishwashers"", ""title"": ""Dishwashers"", ""query"": ""built-in dishwasher"", ""icon"": ""dish"", ""items"": [
              {""part_id"": ""whirlpool-wdp540hamw-cd82dd"", ""name"": ""24 in. Top Control Built-In Tall Tub 55 dBA Dishwasher in White"", ""price_usd"": 459.0,
               ""dims_mm"": {""w"": 606.3, ""h"": 876.3, ""d"": 622.3}, ""image_url"": ""/parts/whirlpool-wdp540hamw-cd82dd/image.jpg"",
               ""model_url"": ""/parts/whirlpool-wdp540hamw-cd82dd/model.glb"", ""model_ready"": true, ""seller"": ""Home Depot"", ""rating"": 4.5},
              {""part_id"": ""frigidaire-fdpc4221as-341c87"", ""name"": ""24 in. Front Control Smart Built-In Tall Tub 62 dBA Dishwasher"", ""price_usd"": 329.0,
               ""dims_mm"": {""w"": 609.6, ""h"": 889.0, ""d"": 635.0}, ""model_ready"": true},
              {""part_id"": ""black-decker-bcd6w-f0c793"", ""name"": ""21.5\"" W, 6-Place Setting, Countertop Dishwasher, White"", ""price_usd"": 299.99,
               ""dims_mm"": {""w"": 546.1, ""h"": 436.9, ""d"": 551.2}}
            ]},
            {""id"": ""microwaves"", ""title"": ""Microwaves"", ""query"": ""over the range microwave"", ""items"": []},
            {""id"": ""cabinet-hardware"", ""title"": ""Hardware"", ""query"": ""cabinet knob"", ""items"": [
              {""part_id"": ""liberty-p38532c-cz-cp-f68288"", ""name"": ""Charmaine 1-1/8 in. (28 mm) Classic Champagne Bronze Cabinet Knob"", ""price_usd"": 3.97,
               ""dims_mm"": {""w"": 27.9, ""h"": 27.9, ""d"": 27.9}, ""model_ready"": true},
              {""part_id"": ""washer-in-a-name"", ""name"": ""Dishwasher mounting bracket kit"", ""price_usd"": 12.5, ""dims_mm"": {""w"": 50, ""h"": 20, ""d"": 10}}
            ]}
          ]}";

        static CatalogResponse Data() => CatalogJson.Parse(Kitchen);

        static CatalogModel Model(float debounce = 0.5f)
        {
            var m = new CatalogModel(debounce);
            m.SetSite("kitchen");
            Assert.IsTrue(m.SetData("kitchen", Data(), CatalogSource.Server));
            return m;
        }

        static string[] Ids(IEnumerable<CatalogHit> hits) => hits.Select(h => h.Id).ToArray();

        // ---------------- the contract, tolerantly ----------------

        [Test]
        public void ParsesTheContract_IgnoringUnknownFields()
        {
            var r = Data();
            Assert.IsNotNull(r);
            Assert.AreEqual("kitchen", r.site);
            Assert.AreEqual("kitchen", r.environment);
            Assert.AreEqual("Kitchen", r.title);
            Assert.AreEqual(4, r.categories.Count);
            Assert.AreEqual(7, r.ItemCount);
            var dw = r.categories[1];
            Assert.AreEqual("dishwashers", dw.id);
            Assert.AreEqual("built-in dishwasher", dw.Query);
            var w = dw.items[0];
            Assert.AreEqual("whirlpool-wdp540hamw-cd82dd", w.part_id);
            Assert.AreEqual(459f, w.price_usd.Value, 1e-3f);
            Assert.AreEqual(606.3f, w.dims_mm.w, 1e-3f);
            Assert.AreEqual(876.3f, w.dims_mm.h, 1e-3f);
            Assert.IsTrue(w.ModelReady);
            Assert.AreEqual("Home Depot", w.seller);
            Assert.IsFalse(dw.items[2].ModelReady, "model_ready missing → no 3D badge");
            Assert.IsNull(dw.items[2].image_url, "missing → null");
        }

        [Test]
        public void ParsesTolerantly_BadFieldsSkipped_ItemsWithoutIdDropped()
        {
            const string json = @"{""site"": ""roof"", ""categories"": [
              null,
              {""title"": ""HVAC units"", ""items"": [
                 null,
                 {""name"": ""no id""},
                 {""part_id"": ""  midea-maw08u1qwt-8d6d08 "", ""price_usd"": ""not a number"", ""dims_mm"": {""w"": 486.9}, ""model_ready"": ""yes""},
                 {""part_id"": ""free"", ""price_usd"": 0}
              ]},
              {""items"": [{""part_id"": ""orphan""}]}
            ]}";
            var r = CatalogJson.Parse(json);
            Assert.IsNotNull(r);
            Assert.AreEqual(1, r.categories.Count, "null and nameless categories dropped");
            var c = r.categories[0];
            Assert.AreEqual("hvac-units", c.id, "an id from the title");
            Assert.AreEqual("HVAC units", c.Query, "no query: the title");
            CollectionAssert.AreEqual(new[] { "midea-maw08u1qwt-8d6d08", "free" }, c.items.Select(i => i.part_id).ToArray());
            Assert.IsNull(c.items[0].price_usd, "a wrong-typed price is skipped, not fatal");
            Assert.AreEqual(486.9f, c.items[0].dims_mm.w, 1e-3f);
            Assert.AreEqual(0f, c.items[0].dims_mm.h, "missing dims stay 0");
            Assert.IsNull(c.items[1].price_usd, "0 is no price");
            Assert.IsNull(r.environment);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not json")]
        [TestCase("[1, 2]")]
        [TestCase("{\"detail\": \"Not Found\"}")]
        public void NotACatalog_IsNullOrEmpty(string json)
        {
            var r = CatalogJson.Parse(json);
            Assert.IsTrue(r == null || r.categories.Count == 0, json);
        }

        [Test]
        public void ParsesSearchReplies()
        {
            var r = CatalogJson.ParseSearch(@"{""q"": ""dishw"", ""items"": [{""part_id"": ""a"", ""name"": ""A""}, {""name"": ""no id""}, null], ""categories"": [""dishwashers"", null, """"], ""took_ms"": 12}");
            Assert.AreEqual("dishw", r.q);
            CollectionAssert.AreEqual(new[] { "a" }, r.items.Select(i => i.part_id).ToArray());
            CollectionAssert.AreEqual(new[] { "dishwashers" }, r.categories.ToArray());
            Assert.IsNull(CatalogJson.ParseSearch("oops"));
        }

        [Test]
        public void AnItemBecomesThePartsFlowsSummary_WithTheBackendsUrls()
        {
            var i = new CatalogItem { part_id = "x-1", price_usd = 12f, dims_mm = new PartDims { w = 10, h = 20, d = 30 } };
            var s = i.ToSummary();
            Assert.AreEqual("x-1", s.id);
            Assert.AreEqual("x-1", s.name, "no name: the id");
            Assert.AreEqual("/parts/x-1/image.jpg", s.image_url);
            Assert.AreEqual("/parts/x-1/model.glb", s.model_url);
            Assert.AreEqual("/parts/x-1/part.json", s.part_url);
            Assert.AreEqual(12f, s.price_usd.Value, 1e-4f);
            Assert.AreEqual(20f, s.dims_mm.h);
        }

        /// The shipped fixtures (Assets/AirTools/Fixtures/Catalog) are the contract's shape, and the stand-in server
        /// answers /catalog by the site's kind and /catalog/search across them.
        [Test]
        public void TheFixturesAnswerLikeCatalogBackend()
        {
            string Read(string p) => File.Exists(p) ? File.ReadAllText(p) : null;
            var (code, body) = CatalogFixtures.Answer("/catalog?site=kitchen&session_id=quest-1", Read);
            Assert.AreEqual(200, code, body);
            var kitchen = CatalogJson.Parse(body);
            Assert.AreEqual("kitchen", kitchen.site);
            Assert.AreEqual(8, kitchen.categories.Count);
            CollectionAssert.IsSubsetOf(new[] { "Fridges", "Dishwashers", "Ranges", "Microwaves", "Cooktops" }, kitchen.categories.Select(c => c.Title).ToArray());
            Assert.Greater(kitchen.ItemCount, 20);
            var roof = CatalogJson.Parse(CatalogFixtures.Answer("/catalog?site=zabel-gymnasium", Read).body);
            Assert.AreEqual("zabel-gymnasium", roof.site);
            CollectionAssert.IsSubsetOf(new[] { "Gutter hangers", "HVAC units", "Solar panels", "Chimney caps" }, roof.categories.Select(c => c.Title).ToArray());
            var facade = CatalogJson.Parse(CatalogFixtures.Answer("/catalog?site=synthetic-facade", Read).body);
            Assert.AreEqual("facade", facade.environment);
            var search = CatalogJson.ParseSearch(CatalogFixtures.Answer("/catalog/search?q=dishw&site=kitchen&limit=5", Read).body);
            Assert.AreEqual(5, search.items.Count, "limited");
            Assert.IsTrue(search.items.All(i => i.name.ToLowerInvariant().Contains("dishwasher")), string.Join(", ", search.items.Select(i => i.name)));
            CollectionAssert.Contains(search.categories, "dishwashers");
            Assert.AreEqual(404, CatalogFixtures.Answer("/nope", Read).code);
        }

        // ---------------- the lazy local filter ----------------

        [Test]
        public void TypingPartOfAWordFindsIt_NameMatchesFirst()
        {
            var index = new CatalogIndex(Data());
            var hits = new List<CatalogHit>();
            CatalogSearch.Filter(index, "dishw", hits);
            // Every dishwasher, and the bracket kit whose name says "Dishwasher" too; category order breaks ties.
            CollectionAssert.AreEquivalent(new[] { "whirlpool-wdp540hamw-cd82dd", "frigidaire-fdpc4221as-341c87", "black-decker-bcd6w-f0c793", "washer-in-a-name" }, Ids(hits));
            Assert.AreEqual("whirlpool-wdp540hamw-cd82dd", hits[0].Id, "the catalog's order within a score");
            Assert.IsTrue(hits.All(h => h.Score > 0));
        }

        [Test]
        public void ACategoryWordFindsItsItems_AndPluralsAndStemsWork()
        {
            var index = new CatalogIndex(Data());
            var hits = new List<CatalogHit>();
            CatalogSearch.Filter(index, "fridge", hits);
            CollectionAssert.AreEquivalent(new[] { "frigidaire-frfg1723av-873cff", "frigidaire-efr751-2646f5" }, Ids(hits), "the Fridges category (the names say Refrigerator)");
            CatalogSearch.Filter(index, "dishwashers", hits);
            Assert.AreEqual(4, hits.Count, "the plural finds the singular names");
            CatalogSearch.Filter(index, "hardware", hits);
            CollectionAssert.AreEquivalent(new[] { "liberty-p38532c-cz-cp-f68288", "washer-in-a-name" }, Ids(hits));
        }

        [Test]
        public void EveryWordMustMatch_QuotedTextIsAPhrase()
        {
            var index = new CatalogIndex(Data());
            var hits = new List<CatalogHit>();
            CatalogSearch.Filter(index, "dishwasher white", hits);
            CollectionAssert.AreEquivalent(new[] { "whirlpool-wdp540hamw-cd82dd", "black-decker-bcd6w-f0c793" }, Ids(hits));
            CatalogSearch.Filter(index, "\"tall tub\"", hits);
            CollectionAssert.AreEquivalent(new[] { "whirlpool-wdp540hamw-cd82dd", "frigidaire-fdpc4221as-341c87" }, Ids(hits));
            CatalogSearch.Filter(index, "\"tub tall\"", hits);
            Assert.IsEmpty(hits, "a phrase is in order");
            CatalogSearch.Filter(index, "\"french door", hits);
            Assert.AreEqual(1, hits.Count, "an unclosed quote runs to the end");
            CatalogSearch.Filter(index, "hom", hits);
            Assert.AreEqual(2, hits.Count, "the seller's words count too (Home Depot)");
            CatalogSearch.Filter(index, "zzz", hits);
            Assert.IsEmpty(hits);
            CatalogSearch.Filter(index, "   ", hits);
            Assert.IsEmpty(hits, "no words: no search");
        }

        [Test]
        public void RankedByWhereTheWordsMatched()
        {
            var index = new CatalogIndex(Data());
            var hits = new List<CatalogHit>();
            // "white": a whole word in two names beats a word inside one (none) — both name matches, exact words.
            CatalogSearch.Filter(index, "tall", hits);
            Assert.AreEqual(2, hits.Count);
            // A name word starting with the text ranks above a category-only match.
            CatalogSearch.Filter(index, "refrig", hits);
            Assert.AreEqual("frigidaire-frfg1723av-873cff", hits[0].Id);
            var name = index.Entries.First(e => e.Item.part_id == "washer-in-a-name");
            var words = new List<string> { "dishw" };
            var none = new List<string>();
            int nameScore = CatalogSearch.Score(name, words, none);
            var dw = index.Entries.First(e => e.Item.part_id == "black-decker-bcd6w-f0c793");
            Assert.AreEqual(CatalogSearch.NamePrefix, nameScore);
            Assert.AreEqual(CatalogSearch.NamePrefix, CatalogSearch.Score(dw, words, none));
            var fridge = index.Entries.First(e => e.Item.part_id == "frigidaire-efr751-2646f5");
            Assert.AreEqual(CatalogSearch.CategoryPrefix, CatalogSearch.Score(fridge, new List<string> { "fridg" }, none));
            Assert.AreEqual(CatalogSearch.NamePrefix + CatalogSearch.NameExact, CatalogSearch.Score(fridge, new List<string> { "mini" }, none));
        }

        [Test]
        public void NumbersWithCommasAreOneWord()
        {
            var words = new List<string>();
            CatalogSearch.AddWords(words, "5,000 BTU 115-Volt Window Air Conditioner");
            CollectionAssert.AreEqual(new[] { "5000", "btu", "115-volt", "window", "air", "conditioner" }, words);
        }

        [Test]
        public void TheLaptopsResultsJoinAfterTheLocalOnes_WithoutDuplicates()
        {
            var index = new CatalogIndex(Data());
            var hits = new List<CatalogHit>();
            CatalogSearch.Filter(index, "dishw", hits);
            int local = hits.Count;
            var server = new List<CatalogItem>
            {
                new CatalogItem { part_id = "whirlpool-wdp540hamw-cd82dd", name = "dup" },
                new CatalogItem { part_id = "lg-ldfn3432t-4e8592", name = "24 in. LG Dishwasher" },
                null,
                new CatalogItem { part_id = "maytag-mdb4949skz-c9677a", name = "Maytag Dishwasher" },
            };
            Assert.AreEqual(2, CatalogSearch.Merge(hits, server, index));
            Assert.AreEqual(local + 2, hits.Count);
            CollectionAssert.AreEqual(new[] { "lg-ldfn3432t-4e8592", "maytag-mdb4949skz-c9677a" }, hits.Skip(local).Select(h => h.Id).ToArray(), "the server's order, after");
            Assert.IsTrue(hits.Skip(local).All(h => h.FromServer));
        }

        // ---------------- debounce and stale answers ----------------

        [Test]
        public void TheLaptopIsAskedHalfASecondAfterTheLastKey()
        {
            var d = new CatalogDebounce(0.5f);
            Assert.IsFalse(d.Due(10f, out _), "nothing typed");
            int g1 = d.Poke(10.0f);
            Assert.IsFalse(d.Due(10.3f, out _));
            int g2 = d.Poke(10.3f);   // another key: the wait starts again
            Assert.Greater(g2, g1);
            Assert.IsFalse(d.Due(10.6f, out _), "0.3 s after the last key");
            Assert.IsTrue(d.Waiting);
            Assert.IsTrue(d.Due(10.81f, out int sent));
            Assert.AreEqual(g2, sent);
            Assert.IsTrue(d.InFlight);
            Assert.IsFalse(d.Due(11.5f, out _), "once");
            Assert.IsTrue(d.Accept(sent));
            Assert.IsFalse(d.InFlight);
        }

        [Test]
        public void AnAnswerToOlderTextIsDropped()
        {
            var d = new CatalogDebounce(0.5f);
            d.Poke(0f);
            Assert.IsTrue(d.Due(0.6f, out int old));
            d.Poke(0.7f);   // typed on while the laptop thinks
            Assert.IsFalse(d.Accept(old), "stale");
            Assert.AreEqual(1, d.Dropped);
            Assert.IsTrue(d.Due(1.3f, out int fresh));
            Assert.IsTrue(d.Accept(fresh));
            d.Poke(2f);
            Assert.IsTrue(d.Due(2.6f, out int cleared));
            d.Cancel();   // the text was cleared
            Assert.IsFalse(d.Accept(cleared));
            Assert.IsFalse(d.Waiting);
        }

        [Test]
        public void TypingShowsLocalResultsAtOnce_TheLaptopsAfterThePause_StaleOnesDropped()
        {
            var m = Model();
            m.Open();
            float t = 5f;
            foreach (char ch in "dish") { Assert.AreEqual(CatalogKeyResult.Changed, m.Key(ch.ToString(), t)); t += 0.12f; }
            Assert.AreEqual(CatalogMode.Search, m.Mode);
            Assert.AreEqual("dish", m.Query);
            Assert.AreEqual(4, m.LocalCount, "local results before any answer");
            Assert.AreEqual(4, m.Visible.Count);
            Assert.AreEqual(CatalogServerState.Waiting, m.Server);
            Assert.IsFalse(m.TakeDueSearch(t, out _, out _), "right after the last key");
            Assert.IsTrue(m.TakeDueSearch(t + 0.5f, out int gen, out string q));
            Assert.AreEqual("dish", q);
            Assert.AreEqual(CatalogServerState.Asking, m.Server);
            // A key before the answer: the answer for "dish" is stale.
            m.Key("w", t + 0.6f);
            Assert.IsFalse(m.OnServerReply(gen, new CatalogSearchResponse { items = { new CatalogItem { part_id = "stale-one" } } }));
            Assert.IsFalse(m.Visible.Any(h => h.Id == "stale-one"));
            Assert.AreEqual(1, m.Debounce.Dropped);
            Assert.IsTrue(m.TakeDueSearch(t + 1.2f, out int gen2, out string q2));
            Assert.AreEqual("dishw", q2);
            Assert.IsTrue(m.OnServerReply(gen2, new CatalogSearchResponse { items = { new CatalogItem { part_id = "lg-ldfn3432t-4e8592", name = "LG Dishwasher" } } }));
            Assert.AreEqual(CatalogServerState.Answered, m.Server);
            Assert.AreEqual(1, m.ServerAdded);
            Assert.AreEqual(5, m.Visible.Count);
            Assert.AreEqual("lg-ldfn3432t-4e8592", m.Visible.Last().Id);
            StringAssert.Contains("+1 from the laptop", m.Status());
            // A failed ask keeps the local results.
            m.Key("z", t + 2f);
            Assert.AreEqual(0, m.Visible.Count, "nothing local for dishwz");
            Assert.IsTrue(m.TakeDueSearch(t + 2.6f, out int gen3, out _));
            Assert.IsTrue(m.OnServerReply(gen3, null));
            Assert.AreEqual(CatalogServerState.Failed, m.Server);
            StringAssert.Contains("No “dishwz”", m.Status(), "and the laptop is away");
        }

        [Test]
        public void ClearingTheTextGoesBackToTheCategory_AndCancelsTheAsk()
        {
            var m = Model();
            m.Open();
            m.SelectCategory(1);
            m.Key("f", 0f);
            m.Key("r", 0.1f);
            Assert.AreEqual(CatalogMode.Search, m.Mode);
            Assert.AreEqual(CatalogKeyResult.Changed, m.Key(CatalogTextField.Back, 0.2f));
            Assert.AreEqual(CatalogKeyResult.Cleared, m.Key(CatalogTextField.Back, 0.3f));
            Assert.AreEqual(CatalogMode.Browse, m.Mode);
            Assert.AreEqual(1, m.Category, "the category it was on");
            Assert.IsFalse(m.TakeDueSearch(5f, out _, out _), "nothing to ask");
            m.SetQuery("fridge", 6f);
            Assert.AreEqual(CatalogKeyResult.Cleared, m.Key(CatalogTextField.Clear, 6.1f));
            Assert.AreEqual(CatalogMode.Browse, m.Mode);
        }

        /// voice (user 09-27): "tap to talk puts what the user says into the search bar … keep that for the keyboard". A spoken
        /// query searches like typing but the field stays empty; the words show in the status line; a key press starts over.
        [Test]
        public void ASpokenQuerySearchesWithoutTouchingTheField()
        {
            var m = Model();
            m.Open();
            m.SetSpokenQuery("Fridges ", 0f);
            Assert.AreEqual("", m.Field.Text, "the search bar is the keyboard's");
            Assert.AreEqual("fridges", m.Spoken, "cleaned as the field would");
            Assert.AreEqual("fridges", m.Query, "searched for");
            Assert.AreEqual(CatalogMode.Search, m.Mode);
            StringAssert.StartsWith("“fridges”", m.Status(), "the status line says what was asked");
            Assert.IsTrue(m.TakeDueSearch(1f, out _, out var asked), "the laptop is asked, as for typing");
            Assert.AreEqual("fridges", asked);

            Assert.AreEqual(CatalogKeyResult.Changed, m.Key("d", 2f));
            Assert.IsNull(m.Spoken, "typing starts over from the keyboard's own text");
            Assert.AreEqual("d", m.Query);
            Assert.AreEqual("d", m.Field.Text);

            m.SetSpokenQuery("dishwasher", 3f);
            Assert.AreEqual("", m.Field.Text, "a new spoken query leaves the field empty");
            m.SelectCategory(1);
            Assert.IsNull(m.Spoken, "a category drops it");
            Assert.AreEqual(CatalogMode.Browse, m.Mode);

            m.SetSpokenQuery("dishwasher", 4f);
            m.Key(CatalogTextField.Back, 4.1f);
            Assert.IsNull(m.Spoken);
            Assert.AreEqual(CatalogMode.Browse, m.Mode, "a key on the empty field: back to browsing");
            m.SetSpokenQuery("  ", 5f);
            Assert.IsNull(m.Spoken);
            Assert.AreEqual(CatalogMode.Browse, m.Mode);
        }

        // ---------------- opening without a tape ----------------

        [Test]
        public void TheCatalogOpensWithoutATape()
        {
            var m = new CatalogModel();
            m.SetSite("kitchen");
            m.SetData("kitchen", CatalogFallback.Build("kitchen", SiteKind.Kitchen, null), CatalogSource.BuiltIn);
            Assert.IsFalse(m.Fit.Available, "no tape, no gap");
            m.Open();
            Assert.IsTrue(m.IsOpen);
            Assert.AreEqual(CatalogMode.Browse, m.Mode);
            Assert.AreEqual(8, m.CategoryCount, "the kitchen's categories");
            Assert.AreEqual("Kitchen", m.Environment);
            Assert.IsFalse(m.FitsOn);
            Assert.AreEqual(CatalogChip.Category, m.ChipAt(0, out int c0));
            Assert.AreEqual(0, c0);
            Assert.AreEqual("Fridges", m.ChipLabel(0));
            Assert.IsTrue(m.ChipSelected(0));
            Assert.IsNotNull(m.Status());
            // Even from the tape's pill with no tape: nothing to fit against, so Fits stays off.
            m.Close();
            m.Open(fromTape: true);
            Assert.IsTrue(m.IsOpen);
            Assert.IsFalse(m.FitsOn);
            // And typing searches with no tape either.
            m.Key("r", 0f);
            Assert.AreEqual(CatalogMode.Search, m.Mode);
        }

        [Test]
        public void ANamedSearchNeedsNoTapeOrGap_WhenTheCatalogIsThere()
        {
            var find = LocalIntents.MatchAll("find a gutter hanger");
            var empty = new List<AgentAction>();
            Assert.IsFalse(LocalIntents.Arbitrate(find, new LocalState(), empty).RunsLocal, "no catalog: the server's (as before)");
            Assert.IsTrue(LocalIntents.Arbitrate(find, new LocalState { Catalog = true }, empty).RunsLocal, "the catalog opens with it");
            Assert.IsFalse(LocalIntents.Arbitrate(find, new LocalState { Catalog = true }, new List<AgentAction> { new AgentAction { name = "search_started" } }).RunsLocal,
                "the server searched");
            Assert.IsFalse(LocalIntents.Arbitrate(find, new LocalState { Catalog = true }, new List<AgentAction> { new AgentAction { name = "show_catalog" } }).RunsLocal,
                "the server opened the catalog");
            Assert.IsFalse(LocalIntents.Arbitrate(LocalIntents.MatchAll("find me a replacement"), new LocalState { Catalog = true }, empty).RunsLocal, "nothing named");
        }

        // ---------------- the Fits chip ----------------

        [Test]
        public void FitsFollowsATapeOrAGap()
        {
            Assert.IsFalse(CatalogFit.None.Available);
            Assert.IsTrue(CatalogFit.None.Fits(new PartDims { w = 1, h = 1, d = 1 }), "no filter: everything");
            var tape = CatalogFit.Tape(0.61, "w");
            Assert.IsTrue(tape.Available);
            Assert.IsTrue(tape.Fits(new PartDims { w = 606.3f, h = 876f, d = 622f }));
            Assert.IsTrue(tape.Fits(new PartDims { w = 612.5f, h = 876f, d = 622f }), "within 3 mm");
            Assert.IsFalse(tape.Fits(new PartDims { w = 614f, h = 876f, d = 622f }));
            Assert.IsFalse(tape.Fits(new PartDims()), "unknown size never fits");
            Assert.IsTrue(CatalogFit.Tape(0.9, "h").Fits(new PartDims { w = 2000f, h = 876f, d = 1f }), "a height tape checks the height");
            Assert.IsFalse(CatalogFit.Tape(8.3, "length").Available, "a long run constrains nothing");
            var gap = CatalogFit.Gap(0.61, 0.87, 0.6);
            Assert.IsTrue(gap.Fits(new PartDims { w = 606f, h = 866f, d = 600f }));
            Assert.IsFalse(gap.Fits(new PartDims { w = 606f, h = 876f, d = 600f }), "too tall for the gap");
            Assert.IsFalse(gap.Fits(new PartDims { w = 606f, h = 866f, d = 640f }), "too deep");
            Assert.AreEqual("Fits 2′ 0″", tape.Label(UnitSystem.Imperial));
            Assert.AreEqual("Fits 0.61 m", gap.Label(UnitSystem.Metric));
        }

        [Test]
        public void TheFitsChipIsOnlyOnFromTheTapesPill_AndFilters()
        {
            var m = Model();
            m.SetFit(CatalogFit.Tape(0.61, "w"));
            m.Open();
            Assert.IsFalse(m.FitsOn, "the ring's Catalog: off by default");
            m.SelectCategory(1);
            Assert.AreEqual(3, m.Visible.Count);
            Assert.IsTrue(m.ToggleFits());
            CollectionAssert.AreEquivalent(new[] { "whirlpool-wdp540hamw-cd82dd", "frigidaire-fdpc4221as-341c87", "black-decker-bcd6w-f0c793" }, Ids(m.Visible));
            m.SetFit(CatalogFit.Tape(0.60, "w"));
            CollectionAssert.AreEqual(new[] { "black-decker-bcd6w-f0c793" }, Ids(m.Visible), "a narrower opening");
            StringAssert.Contains("that fit", m.Status());
            m.Close();
            var pill = Model();
            pill.SetFit(CatalogFit.Tape(0.61, "w"));
            pill.Open(fromTape: true);
            Assert.IsTrue(pill.FitsOn, "from the tape's pill: on");
            pill.SetFit(CatalogFit.None);
            Assert.IsFalse(pill.FitsOn, "the tape went (undo): the chip and the filter go");
        }

        // ---------------- categories, chips, pages, picks ----------------

        [Test]
        public void ChipsPageWhenThereAreMoreThanEight_AndALiveSearchAddsResultsFirst()
        {
            var r = CatalogFallback.Build("kitchen", SiteKind.Kitchen, null);
            r.categories.Add(new CatalogCategory { id = "extra", title = "Extra" });
            var m = new CatalogModel();
            m.SetData("kitchen", r, CatalogSource.BuiltIn);
            Assert.AreEqual(9, m.CategoryCount);
            Assert.AreEqual(CatalogChip.More, m.ChipAt(7, out _));
            Assert.AreEqual("More…", m.ChipLabel(7));
            m.PressChip(7);
            Assert.AreEqual(CatalogChip.Category, m.ChipAt(0, out int c));
            Assert.AreEqual(7, c, "the next page of categories");
            Assert.IsTrue(m.SelectCategory(1));
            Assert.AreEqual(0, m.ChipOffset, "a category's chip comes back on screen");
            m.ShowResults("dishwasher", false, new List<PartSummary> { new PartSummary { id = "a" }, new PartSummary { id = "b" } });
            Assert.AreEqual(CatalogChip.Results, m.ChipAt(0, out _));
            Assert.IsTrue(m.ChipSelected(0));
            Assert.AreEqual(CatalogMode.Results, m.Mode);
            CollectionAssert.AreEqual(new[] { "a", "b" }, Ids(m.Visible));
            Assert.IsNull(m.Status(), "the live search's own progress shows");
            m.PressChip(1);
            Assert.AreEqual(CatalogMode.Browse, m.Mode);
            m.PressChip(0);
            Assert.AreEqual(CatalogMode.Results, m.Mode, "Results comes back from its chip");
        }

        [Test]
        public void PagesOfSix_AndPickingSaysWhatTheListIs()
        {
            var cat = new CatalogCategory { id = "many", title = "Many", query = "many things" };
            for (int i = 0; i < 14; i++) cat.items.Add(new CatalogItem { part_id = $"p{i}", name = $"Part {i}" });
            var m = new CatalogModel();
            m.SetData("x", new CatalogResponse { categories = { cat } }, CatalogSource.Server);
            m.Open();
            Assert.AreEqual(3, m.PageCount);
            Assert.AreEqual("p0", m.PageItem(0).Id);
            Assert.IsFalse(m.PreviousPage());
            Assert.IsTrue(m.NextPage());
            Assert.IsTrue(m.NextPage());
            Assert.IsFalse(m.NextPage());
            Assert.AreEqual("p12", m.PageItem(0).Id);
            Assert.IsNull(m.PageItem(2), "14 items: the last page has two");
            Assert.IsTrue(m.Pick(1, out var hit, out int index, out string label));
            Assert.AreEqual("p13", hit.Id);
            Assert.AreEqual(13, index);
            Assert.AreEqual("many things", label, "the category's store query names the list");
            Assert.IsFalse(m.Pick(3, out _, out _, out _));
            m.SetQuery("part 1", 0f);
            Assert.AreEqual(0, m.Page, "a new search starts on page 1");
            Assert.IsTrue(m.Pick(0, out _, out _, out label));
            Assert.AreEqual("part 1", label);
        }

        [Test]
        public void AnEmptyCategoryOffersTheStores()
        {
            var m = Model();
            m.Open();
            Assert.IsNull(m.EmptyAction);
            Assert.IsTrue(m.SelectCategory(m.FindCategory("microwave")));
            Assert.AreEqual("microwaves", m.CurrentCategory.id);
            Assert.IsTrue(m.Empty);
            Assert.AreEqual("Find microwaves in stores", m.EmptyAction);
            Assert.AreEqual("over the range microwave", m.EmptyQuery);
            m.SetQuery("zzz", 0f);
            Assert.AreEqual("Search stores for “zzz”", m.EmptyAction);
            Assert.AreEqual("zzz", m.EmptyQuery);
        }

        [TestCase("dishwashers", "dishwashers")]
        [TestCase("Dishwasher", "dishwashers")]
        [TestCase("fridge", "fridges")]
        [TestCase("hardware", "cabinet-hardware")]
        [TestCase("cabinet-hardware", "cabinet-hardware")]
        [TestCase("rockets", null)]
        public void CategoriesAreFoundBySpokenName(string name, string id)
        {
            var m = Model();
            int i = m.FindCategory(name);
            Assert.AreEqual(id, i >= 0 ? m.Data.categories[i].id : null);
        }

        [Test]
        public void ANewSiteBringsItsOwnCatalog_TheTypedTextGoes()
        {
            var m = Model();
            m.Open();
            m.SetQuery("dish", 0f);
            Assert.IsTrue(m.SetSite("zabel-gymnasium"));
            Assert.AreEqual("", m.Query);
            Assert.AreEqual(CatalogMode.Browse, m.Mode);
            Assert.IsNull(m.Data);
            StringAssert.Contains("Loading", m.Status());
            Assert.IsFalse(m.SetData("kitchen", Data(), CatalogSource.Server), "a late answer for the old site");
            Assert.IsTrue(m.SetData("zabel-gymnasium", CatalogFallback.Build("zabel-gymnasium", SiteKind.Roof, null), CatalogSource.BuiltIn));
            Assert.AreEqual("Roof", m.Environment);
            Assert.IsFalse(m.SetSite("zabel-gymnasium"), "same site: nothing to do");
        }

        [Test]
        public void NewDataKeepsTheCategoryShown_AndReRunsTheSearch()
        {
            var m = Model();
            m.Open();
            m.SelectCategory(1);
            var fresh = Data();
            fresh.categories.Reverse();
            m.SetData("kitchen", fresh, CatalogSource.Server);
            Assert.AreEqual("dishwashers", m.CurrentCategory.id, "by id");
            m.SetQuery("mini", 0f);
            Assert.AreEqual(1, m.Visible.Count);
            var more = Data();
            more.categories[0].items.Add(new CatalogItem { part_id = "mini-2", name = "Mini fridge two" });
            m.SetData("kitchen", more, CatalogSource.Server);
            Assert.AreEqual(2, m.Visible.Count, "the text typed runs again on the new items");
        }

        [Test]
        public void ResetGoesBackToTheFirstRunState()
        {
            var m = Model();
            m.SetFit(CatalogFit.Tape(0.61, "w"));
            m.Open(fromTape: true);
            m.SelectCategory(2);
            m.SetKeyboard(true);
            m.SetQuery("dish", 0f);
            m.ShowResults("dishwasher", true, null);
            m.Picked = "x";
            m.ResetSession();
            Assert.IsFalse(m.IsOpen);
            Assert.IsFalse(m.KeyboardOpen);
            Assert.AreEqual(CatalogMode.Browse, m.Mode);
            Assert.AreEqual(0, m.Category);
            Assert.AreEqual("", m.Query);
            Assert.IsFalse(m.HasResults);
            Assert.IsFalse(m.FitsOn);
            Assert.IsNull(m.Picked);
            Assert.IsNotNull(m.Data, "the site's catalog stays loaded");
        }

        // ---------------- the built-in fallback ----------------

        [TestCase("kitchen", null, SiteKind.Kitchen)]
        [TestCase("zabel-gymnasium", null, SiteKind.Roof)]
        [TestCase("gt-lcc-canopy", null, SiteKind.Roof)]
        [TestCase("gt-lcc-tower", null, SiteKind.Roof)]
        [TestCase("hospital-bg", null, SiteKind.Roof)]
        [TestCase("synthetic-facade", null, SiteKind.Facade)]
        [TestCase("built-in", null, SiteKind.Facade)]
        [TestCase("warehouse-7", null, SiteKind.Generic)]
        [TestCase("warehouse-7", "Kitchen", SiteKind.Kitchen)]
        [TestCase("kitchen", "roof", SiteKind.Roof)]
        [TestCase(null, null, SiteKind.Generic)]
        public void TheKindOfPlace(string site, string environment, SiteKind kind) =>
            Assert.AreEqual(kind, CatalogFallback.KindOf(site, environment));

        [Test]
        public void TheBuiltInTableMapsTheShippedPartsByKindOfPlace()
        {
            var hanger = new PartSummary { id = "hidden-hanger-5k", name = "5\" K-style hidden gutter hanger with screw", price_usd = 4.27f };
            var ac = new PartSummary { id = "window-ac-small", name = "Small window air conditioner, 5,000 BTU", price_usd = 189.99f };
            var parts = new List<CatalogFallback.LocalPart>
            {
                new CatalogFallback.LocalPart(hanger, new[] { "bracket", "fascia", "gutter", "hanger", "k-style" }),
                new CatalogFallback.LocalPart(ac, new[] { "ac", "air", "conditioner", "cooling", "window" }),
            };
            var roof = CatalogFallback.Build("zabel-gymnasium", SiteKind.Roof, parts);
            Assert.AreEqual("Roof", roof.title);
            Assert.AreEqual("roof", roof.environment);
            CollectionAssert.IsSubsetOf(new[] { "Gutters", "Gutter hangers", "HVAC units", "Solar panels", "Chimney caps" }, roof.categories.Select(c => c.title).ToArray());
            Assert.AreEqual("hidden-hanger-5k", roof.categories.Single(c => c.id == "gutter-hangers").items.Single().part_id, "a hanger before a gutter");
            Assert.IsEmpty(roof.categories.Single(c => c.id == "gutters").items);
            Assert.AreEqual("window-ac-small", roof.categories.Single(c => c.id == "hvac").items.Single().part_id);
            Assert.IsTrue(roof.categories.SelectMany(c => c.items).All(i => i.ModelReady), "shipped with their models");
            var facade = CatalogFallback.Build("synthetic-facade", SiteKind.Facade, parts);
            Assert.AreEqual("window-ac-small", facade.categories.Single(c => c.id == "window-ac").items.Single().part_id);
            var kitchen = CatalogFallback.Build("kitchen", SiteKind.Kitchen, parts);
            CollectionAssert.AreEqual(new[] { "Fridges", "Dishwashers", "Ranges", "Microwaves", "Cooktops", "Range hoods", "Hardware", "Drawer slides" },
                kitchen.categories.Select(c => c.title).ToArray());
            Assert.AreEqual(0, kitchen.ItemCount, "no kitchen parts ship with the app: every category searches the stores");
            Assert.AreEqual(4, CatalogFallback.Build("x", SiteKind.Generic, parts).categories.Count);
            // The built-in words find things too: "fridge" → the Fridges category.
            var m = new CatalogModel();
            m.SetData("kitchen", kitchen, CatalogSource.BuiltIn);
            Assert.AreEqual("fridges", m.CurrentCategory.id);
            Assert.AreEqual(0, m.FindCategory("refrigerator"), "by the table's words (after the titles and ids)");
            Assert.AreEqual(2, m.FindCategory("stove"));
        }

        // ---------------- the keyboard's field ----------------

        [Test]
        public void TheFieldTakesTheKeyboardsKeys()
        {
            var f = new CatalogTextField();
            Assert.AreEqual(CatalogKeyResult.None, f.Press(CatalogTextField.Space), "no leading space");
            Assert.AreEqual(CatalogKeyResult.Changed, f.Press("D"));
            foreach (var k in new[] { "i", "s", "h", CatalogTextField.Space, CatalogTextField.Space, "-", "\"", "2", "4" }) f.Press(k);
            Assert.AreEqual("dish -\"24", f.Text, "lower case, one space");
            Assert.AreEqual(CatalogKeyResult.None, f.Press("é"), "not on the keyboard");
            Assert.AreEqual(CatalogKeyResult.None, f.Press("?"));
            Assert.AreEqual(CatalogKeyResult.Changed, f.Press(CatalogTextField.Back));
            Assert.AreEqual("dish -\"2", f.Text);
            Assert.AreEqual(CatalogKeyResult.Submitted, f.Press(CatalogTextField.Enter));
            Assert.AreEqual(CatalogKeyResult.Cleared, f.Press(CatalogTextField.Clear));
            Assert.AreEqual("", f.Text);
            Assert.AreEqual(CatalogKeyResult.None, f.Press(CatalogTextField.Clear));
            Assert.AreEqual(CatalogKeyResult.None, f.Press(CatalogTextField.Back));
            for (int i = 0; i < 60; i++) f.Press("a");
            Assert.AreEqual(CatalogTextField.MaxLength, f.Text.Length);
            f.Set("Dish washer  ");
            Assert.AreEqual("dish washer ", f.Text);
            Assert.AreEqual("dish washer", f.Query);
        }

        [Test]
        public void TheKeyboardHasEveryKeyAsked_TenUnitsARow()
        {
            var all = CatalogTextField.Rows.SelectMany(r => r).ToList();
            foreach (char ch in "abcdefghijklmnopqrstuvwxyz0123456789-\"") CollectionAssert.Contains(all, ch.ToString());
            foreach (var k in new[] { CatalogTextField.Space, CatalogTextField.Back, CatalogTextField.Clear, CatalogTextField.Enter }) CollectionAssert.Contains(all, k);
            Assert.AreEqual(all.Count, all.Distinct().Count(), "each key once");
            foreach (var row in CatalogTextField.Rows) Assert.AreEqual(10, row.Sum(CatalogTextField.Units), string.Join(" ", row));
            Assert.AreEqual("Q", CatalogTextField.Label("q"));
            Assert.AreEqual("Search", CatalogTextField.Label(CatalogTextField.Enter));
            Assert.AreEqual("7", CatalogTextField.Label("7"));
        }

        // ---------------- words ----------------

        [TestCase("find me a dishwasher that fits", "dishwasher")]
        [TestCase("Show me fridges.", "fridges")]
        [TestCase("I need a 24 inch range", "24 inch range")]
        [TestCase("can you search for gutter hangers for the roof", "gutter hangers")]
        [TestCase("okay, look for a new microwave please", "microwave")]
        [TestCase("dishwasher", "dishwasher")]
        [TestCase("5,000 BTU window AC", "5000 btu window ac")]
        [TestCase("", "")]
        public void WhatWasSaidBecomesTheSearch(string said, string query) => Assert.AreEqual(query, CatalogText.FromUtterance(said));

        [Test]
        public void CardLines()
        {
            var s = new PartSummary { price_usd = 459f, dims_mm = new PartDims { w = 606.3f, h = 876f, d = 622f } };
            Assert.AreEqual("$459 · 23⅞″ W", CatalogText.Detail(s, UnitSystem.Imperial));
            Assert.AreEqual("$459 · 606 mm W", CatalogText.Detail(s, UnitSystem.Metric));
            Assert.AreEqual("$4.27", CatalogText.Price(4.27f));
            Assert.AreEqual("", CatalogText.Price(null));
            Assert.AreEqual("23⅞″ W", CatalogText.Detail(new PartSummary { dims_mm = s.dims_mm }, UnitSystem.Imperial));
            Assert.AreEqual("Top Control Built-In Tall Tub 55 dBA Dishwasher", CatalogText.Name("24 in. Top Control Built-In Tall Tub 55 dBA Dishwasher"));
            Assert.AreEqual("6-Place Setting, Countertop Dishwasher", CatalogText.Name("21.5\" W, 6-Place Setting, Countertop Dishwasher"));
            Assert.AreEqual("Smart Inverter Window AC", CatalogText.Name("30 in Wide Smart Inverter Window AC"));
            Assert.AreEqual("Mini fridge", CatalogText.Name("Mini fridge"));
            Assert.AreEqual("12 items", CatalogText.Count(12));
            Assert.AreEqual("1 item", CatalogText.Count(1));
        }

        [TestCase("kitchen", "kitchen.json")]
        [TestCase("GT LCC/Canopy", "gt_lcc_canopy.json")]
        [TestCase("../../etc", "______etc.json")]
        [TestCase(null, "built-in.json")]
        public void ACatalogIsKeptPerSite(string site, string file) => Assert.AreEqual(file, CatalogText.CacheFile(site));

        [Test]
        public void ThePathsEscapeWhatWasTyped()
        {
            Assert.AreEqual("/catalog?site=kitchen&session_id=quest-1", CatalogText.CatalogPath("kitchen", "quest-1"));
            Assert.AreEqual("/catalog/search?q=%22tall%20tub%22%20%26%20more&site=kitchen&limit=20", CatalogText.SearchPath("\"tall tub\" & more", "kitchen", 20));
        }
    }
}
