using System.Collections.Generic;
using AirTools.Scene;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// sitescope (1): every world item belongs to the site it was made in. Pure, all in the offline runner: the site
    /// key, the switch and its calibration factor, the shelf, and the owner contract on a stand-in tool (tag on create,
    /// hide and show, the same poses back, per-site undo / redo, the context, reset).
    public class SiteScopeTests
    {
        /// A stand-in owner with the tools' shape: live items, an undo stack (the items, newest last) and a redo stack,
        /// parked per site with SiteShelf. Items are points in SceneRoot space; "shown" stands for GameObject.activeSelf.
        sealed class FakeTapes : ISiteScoped
        {
            public sealed class Item { public string Site; public Vector3 Point; public bool Shown = true; }

            sealed class State
            {
                public readonly List<Item> Items = new List<Item>();
                public readonly List<Item> Redo = new List<Item>();
                public readonly List<Item> Hidden = new List<Item>();
            }

            public readonly List<Item> Items = new List<Item>();
            public readonly List<Item> Redo = new List<Item>();
            public readonly List<(string from, string to, float factor)> Calls = new List<(string, string, float)>();
            SiteShelf<State> m_Sites;
            SiteShelf<State> Sites => m_Sites ??= new SiteShelf<State>(SiteScope.Current);

            public string Live => Sites.Live;

            public Item Add(Vector3 p)
            {
                var it = new Item { Site = SiteScope.Current, Point = p };
                Items.Add(it);
                Redo.Clear();
                return it;
            }

            public bool Undo()
            {
                if (Items.Count == 0) return false;
                var it = Items[Items.Count - 1];
                Items.RemoveAt(Items.Count - 1);
                it.Shown = false;
                Redo.Add(it);
                return true;
            }

            public bool RedoLast()
            {
                if (Redo.Count == 0) return false;
                var it = Redo[Redo.Count - 1];
                Redo.RemoveAt(Redo.Count - 1);
                it.Shown = true;
                Items.Add(it);
                return true;
            }

            public void Rescale(float k) { foreach (var it in Items) it.Point *= k; }

            /// The context's items: the live list only.
            public List<Vector3> Context()
            {
                var l = new List<Vector3>();
                foreach (var it in Items) l.Add(it.Point);
                return l;
            }

            public void SwitchSite(string from, string to, float factor)
            {
                Calls.Add((from, to, factor));
                m_Sites ??= new SiteShelf<State>(from);
                if (to == Sites.Live) return;
                State park = null;
                if (Items.Count > 0 || Redo.Count > 0)
                {
                    park = new State();
                    park.Items.AddRange(Items);
                    park.Redo.AddRange(Redo);
                    foreach (var it in Items) if (it.Shown) { it.Shown = false; park.Hidden.Add(it); }
                    Items.Clear(); Redo.Clear();
                }
                var back = Sites.Swap(to, park);
                if (back == null) return;
                Items.AddRange(back.Items);
                Redo.AddRange(back.Redo);
                foreach (var it in back.Hidden) it.Shown = true;
                foreach (var it in Items) it.Point *= factor;
                foreach (var it in Redo) it.Point *= factor;
            }

            public void ClearParked() { Sites.TakeAll(); }
            public int ParkedCount => Sites.Count;
            public void ClearAll() { Items.Clear(); Redo.Clear(); }
            public int Parked => Sites.Count;
        }

        FakeTapes m_Tapes;

        [SetUp]
        public void SetUp()
        {
            SiteScope.Reset();
            m_Tapes = new FakeTapes();
            SiteScope.Register(m_Tapes);
        }

        [TearDown]
        public void TearDown() => SiteScope.Reset();

        /// SceneRoot's switch: a fresh load starts at ×1, then the site's scale is restored (SceneRoot.Rescaled).
        void Load(string site, float scale = 1f)
        {
            SiteScope.Switch(site, 1f);
            if (Mathf.Abs(scale - 1f) > 1e-6f)
            {
                SiteScope.NoteCalibration(scale);
                m_Tapes.Rescale(scale);   // CalibrationSync → RescaleAll: the live site's items only
            }
        }

        [Test]
        public void TheKeyIsTheSiteId_OrBuiltIn()
        {
            Assert.AreEqual("kitchen", SiteScope.Key(true, "kitchen"));
            Assert.AreEqual(ModelSites.BuiltIn, SiteScope.Key(false, "synthetic-facade"), "the built-in facade, whatever its package calls it");
            Assert.AreEqual(ModelSites.BuiltIn, SiteScope.Key(true, ""));
            Assert.AreEqual(ModelSites.BuiltIn, SiteScope.KeyOf(null));
            Assert.AreEqual(ModelSites.BuiltIn, SiteScope.Current, "the app starts on the built-in facade");
            Assert.IsTrue(SiteScope.IsCurrent(null));
        }

        [Test]
        public void ASwitchCallsEveryOwnerOnce_AndTheSameSiteIsNoSwitch()
        {
            var other = new FakeTapes();
            SiteScope.Register(other);
            SiteScope.Register(other);   // twice: still once
            string seen = null;
            SiteScope.Changed += (a, b) => seen = a + "→" + b;
            Assert.IsTrue(SiteScope.Switch("kitchen", 1f));
            Assert.AreEqual("built-in→kitchen", seen);
            // Two steps: every owner parks the site left (onto the empty Arriving site), then every owner brings the new one back.
            Assert.AreEqual(2, m_Tapes.Calls.Count);
            Assert.AreEqual((ModelSites.BuiltIn, SiteScope.Arriving, 1f), m_Tapes.Calls[0]);
            Assert.AreEqual((SiteScope.Arriving, "kitchen", 1f), m_Tapes.Calls[1]);
            Assert.AreEqual(2, other.Calls.Count);
            Assert.AreEqual("kitchen", m_Tapes.Live);
            Assert.IsFalse(SiteScope.Switch("kitchen", 1f), "a revision of the same site keeps its items");
            Assert.AreEqual(2, m_Tapes.Calls.Count);
            Assert.IsTrue(SiteScope.Switch(null, 1f), "null is the built-in facade");
            Assert.AreEqual(ModelSites.BuiltIn, SiteScope.Current);
            Assert.AreEqual(2, SiteScope.Switches);
        }

        [Test]
        public void ItemsAreTaggedWithTheSiteTheyWereMadeIn()
        {
            Load("kitchen");
            var a = m_Tapes.Add(new Vector3(1, 0, 0));
            Load("zabel-gymnasium");
            var b = m_Tapes.Add(new Vector3(2, 0, 0));
            Assert.AreEqual("kitchen", a.Site);
            Assert.AreEqual("zabel-gymnasium", b.Site);
            Assert.AreEqual("zabel-gymnasium", m_Tapes.Live);
        }

        [Test]
        public void OtherSitesItemsAreHidden_NotDestroyed_AndComeBackWithTheSamePoses()
        {
            Load("kitchen");
            var a = m_Tapes.Add(new Vector3(1.25f, 0.5f, -2f));
            var a2 = m_Tapes.Add(new Vector3(0.1f, 2f, 3f));
            Load("zabel-gymnasium");
            Assert.IsFalse(a.Shown, "hidden while the gym is loaded");
            Assert.IsFalse(a2.Shown);
            Assert.IsEmpty(m_Tapes.Items, "not in the live list: nothing snaps to them, the context doesn't carry them");
            Assert.AreEqual(1, m_Tapes.Parked);
            Load("kitchen");
            Assert.IsTrue(a.Shown && a2.Shown, "back with the kitchen");
            CollectionAssert.AreEqual(new[] { a, a2 }, m_Tapes.Items, "same items, same order");
            Assert.AreEqual(new Vector3(1.25f, 0.5f, -2f), a.Point);
            Assert.AreEqual(0, m_Tapes.Parked, "the gym had nothing to park");
        }

        [Test]
        public void ASiteWithItsOwnScaleComesBackOnItsFeatures()
        {
            // The kitchen loads at ×1.63: its items are stored at 1.63; leaving and loading it again starts at ×1
            // (factor 1/1.63), and the restored scale takes them back.
            Load("kitchen", 1.63f);
            var p = new Vector3(1.63f, 0.815f, -3.26f);
            var a = m_Tapes.Add(p);
            Load("zabel-gymnasium");
            var g = m_Tapes.Add(new Vector3(5, 0, 5));
            Load("kitchen", 1.63f);
            Assert.AreEqual(1f / 1.63f, m_Tapes.Calls[m_Tapes.Calls.Count - 1].factor, 1e-6f, "calibration now (×1) / at leave (×1.63)");
            Assert.AreEqual(p.x, a.Point.x, 1e-5f);
            Assert.AreEqual(p.y, a.Point.y, 1e-5f);
            Assert.AreEqual(p.z, a.Point.z, 1e-5f);
            Assert.AreEqual(new Vector3(5, 0, 5), g.Point, "the gym's tape wasn't rescaled by the kitchen's scale");
        }

        [Test]
        public void ASiteRescaledByAPersonComesBackAtTheNewScale()
        {
            // Leave the built-in facade at ×1.1 (a tape + Set scale): it loads again at ×1 (not saved): the items move
            // with the scene, back on their features.
            SiteScope.NoteCalibration(1f);
            var a = m_Tapes.Add(new Vector3(1, 1, 1));
            SiteScope.NoteCalibration(1.1f);
            m_Tapes.Rescale(1.1f);
            Load("kitchen", 1.63f);
            Load(ModelSites.BuiltIn);
            Assert.AreEqual(1f / 1.1f, m_Tapes.Calls[m_Tapes.Calls.Count - 1].factor, 1e-6f);
            Assert.AreEqual(1f, a.Point.x, 1e-5f, "at the facade's ×1: where it was on the scene");
        }

        [Test]
        public void AHeldArrivalComesBackAfterTheScaleIsRestored_Untouched()
        {
            // SceneStreamer's fresh load: SetRuntimeContent (×1) → RestoreCalibration (Rescaled ×1.63) → Arrive.
            Load("kitchen", 1.63f);
            var a = m_Tapes.Add(new Vector3(1.63f, 0f, 3.26f));
            Load("zabel-gymnasium");
            SiteScope.HoldArrival();
            SiteScope.Switch("kitchen", 1f);
            Assert.IsTrue(SiteScope.Pending);
            Assert.AreEqual("kitchen", SiteScope.Current, "items made now are the kitchen's");
            Assert.AreEqual(SiteScope.Arriving, m_Tapes.Live, "the owners sit on the empty arriving site");
            SiteScope.NoteCalibration(1.63f);
            m_Tapes.Rescale(1.63f);   // the restored scale: nothing live to move
            Assert.IsFalse(a.Shown);
            SiteScope.Arrive(1.63f);
            Assert.IsFalse(SiteScope.Pending);
            Assert.AreEqual("kitchen", m_Tapes.Live);
            Assert.AreEqual(1f, m_Tapes.Calls[m_Tapes.Calls.Count - 1].factor, "same scale as when it was left: no factor at all");
            Assert.AreEqual(new Vector3(1.63f, 0f, 3.26f), a.Point, "bit for bit");
            Assert.IsTrue(a.Shown);
        }

        [Test]
        public void AHoldWithNoSiteChangeIsANoOp_AndAnUnreleasedHoldCompletesOnTheNextSwitch()
        {
            SiteScope.HoldArrival();
            Assert.IsFalse(SiteScope.Switch(ModelSites.BuiltIn, 1f), "same site (an unaligned revision): nothing parks");
            SiteScope.Arrive(1f);
            Assert.IsFalse(SiteScope.Pending);
            Assert.AreEqual(0, m_Tapes.Calls.Count);
            SiteScope.HoldArrival();
            SiteScope.Switch("kitchen", 1f);
            Assert.IsTrue(SiteScope.Pending);
            SiteScope.Arrive(1f);
            Assert.AreEqual("kitchen", m_Tapes.Live);
        }

        [Test]
        public void UndoOnB_NeverTouchesAsItems()
        {
            Load("kitchen");
            var a = m_Tapes.Add(Vector3.one);
            Load("zabel-gymnasium");
            Assert.IsFalse(m_Tapes.Undo(), "nothing to undo in the gym");
            var b = m_Tapes.Add(Vector3.up);
            Assert.IsTrue(m_Tapes.Undo());
            Assert.IsFalse(b.Shown);
            Assert.IsFalse(m_Tapes.Undo(), "the kitchen's tape is out of reach");
            Load("kitchen");
            Assert.IsTrue(a.Shown, "the kitchen's tape was never undone");
            Assert.IsTrue(m_Tapes.Undo(), "back in the kitchen, its own undo reaches it");
            Assert.IsFalse(a.Shown);
        }

        [Test]
        public void RedoNeverResurrectsAnItemIntoAnotherScan()
        {
            Load("kitchen");
            var a = m_Tapes.Add(Vector3.one);
            m_Tapes.Undo();
            Load("zabel-gymnasium");
            Assert.IsFalse(m_Tapes.RedoLast(), "the kitchen's undone tape can't come back in the gym");
            Assert.IsFalse(a.Shown);
            Load("kitchen");
            Assert.IsFalse(a.Shown, "undone stays undone on the way back");
            Assert.IsTrue(m_Tapes.RedoLast(), "…and redoes in its own scan");
            Assert.IsTrue(a.Shown);
            Assert.AreEqual("kitchen", a.Site);
        }

        [Test]
        public void TheContextCarriesOnlyTheCurrentSite()
        {
            Load("kitchen");
            m_Tapes.Add(new Vector3(1, 0, 0));
            m_Tapes.Add(new Vector3(2, 0, 0));
            Load("zabel-gymnasium");
            m_Tapes.Add(new Vector3(9, 0, 0));
            CollectionAssert.AreEqual(new[] { new Vector3(9, 0, 0) }, m_Tapes.Context());
            Load("kitchen");
            Assert.AreEqual(2, m_Tapes.Context().Count);
        }

        [Test]
        public void ResetClearsEverySite()
        {
            Load("kitchen", 1.63f);
            m_Tapes.Add(Vector3.one);
            Load("zabel-gymnasium");
            m_Tapes.Add(Vector3.up);
            Assert.AreEqual(1, SiteScope.ParkedCount, "the kitchen is parked");
            SiteScope.ClearParked();
            m_Tapes.ClearAll();
            Assert.AreEqual(0, m_Tapes.Parked);
            Assert.AreEqual(0, SiteScope.ParkedCount);
            Load("kitchen", 1.63f);
            Assert.IsEmpty(m_Tapes.Items, "nothing comes back after a demo reset");
            Assert.AreEqual(1.63f, SiteScope.CalibrationOf("kitchen"), 1e-6f);
        }

        [Test]
        public void FactorIsOneForASiteNeverSeenOrABadScale()
        {
            Assert.AreEqual(1f, SiteScope.FactorFor("hospital-bg", 1f));
            SiteScope.NoteCalibration(float.NaN);
            SiteScope.NoteCalibration(0f);
            Assert.AreEqual(0f, SiteScope.CalibrationOf(ModelSites.BuiltIn), "nothing noted");
            SiteScope.NoteCalibration(2f);
            Assert.AreEqual(1f, SiteScope.FactorFor(ModelSites.BuiltIn, float.PositiveInfinity));
            Assert.AreEqual(0.5f, SiteScope.FactorFor(ModelSites.BuiltIn, 1f), 1e-6f);
        }

        [Test]
        public void AFailingOwnerDoesNotStopTheOthers()
        {
            var bad = new Throwing();
            SiteScope.Reset();
            SiteScope.Register(bad);
            SiteScope.Register(m_Tapes);
            SiteScope.Switch("kitchen", 1f);
            Assert.AreEqual(2, m_Tapes.Calls.Count, "the next owner still switched, both steps");
            Assert.AreEqual((ModelSites.BuiltIn, SiteScope.Arriving, 1f), m_Tapes.Calls[0]);
            Assert.AreEqual("kitchen", SiteScope.Current);
            Assert.AreEqual("kitchen", m_Tapes.Live);
        }

        /// Records the order owners are called in, and what Current was then.
        sealed class Recorder : ISiteScoped
        {
            readonly string m_Name; readonly List<string> m_Log;
            public Recorder(string name, List<string> log) { m_Name = name; m_Log = log; }
            public void SwitchSite(string from, string to, float factor) => m_Log.Add($"{m_Name}:{to}:{SiteScope.Current}");
            public void ClearParked() { }
            public int ParkedCount => 0;
        }

        [Test]
        public void OwnersSwitchInOrder_AndParkWhileTheSiteLeftIsStillCurrent()
        {
            SiteScope.Reset();
            var log = new List<string>();
            SiteScope.Register(new Recorder("parts", log), SiteScope.OrderParts);
            SiteScope.Register(new Recorder("measure", log), SiteScope.OrderMeasure);
            SiteScope.Register(new Recorder("grok", log), SiteScope.OrderGrok);
            SiteScope.Register(new Recorder("edges", log));
            SiteScope.Register(new Recorder("placement", log), SiteScope.OrderPlacement);
            SiteScope.Switch("kitchen", 1f);
            CollectionAssert.AreEqual(new[]
            {
                "grok:(arriving):built-in", "measure:(arriving):built-in", "parts:(arriving):built-in", "placement:(arriving):built-in", "edges:(arriving):built-in",
                "grok:kitchen:kitchen", "measure:kitchen:kitchen", "parts:kitchen:kitchen", "placement:kitchen:kitchen", "edges:kitchen:kitchen",
            }, log, "the Grok layer lets go first; tapes before parts; a commit on the way out belongs to the site left");
        }

        sealed class Throwing : ISiteScoped
        {
            public void SwitchSite(string from, string to, float factor) => throw new System.InvalidOperationException("boom");
            public void ClearParked() { }
            public int ParkedCount => 0;
        }

        [Test]
        public void TheShelfParksAndHandsBack()
        {
            var shelf = new SiteShelf<List<int>>();
            Assert.AreEqual(ModelSites.BuiltIn, shelf.Live);
            Assert.IsNull(shelf.Swap("kitchen", new List<int> { 1 }));
            Assert.AreEqual("kitchen", shelf.Live);
            Assert.IsTrue(shelf.Has(ModelSites.BuiltIn));
            Assert.IsNull(shelf.Swap("kitchen", new List<int> { 9 }), "same site: nothing moves");
            Assert.IsNull(shelf.Swap("gym", null), "nothing to park");
            Assert.AreEqual(1, shelf.Count);
            var back = shelf.Swap(ModelSites.BuiltIn, new List<int> { 2 });
            CollectionAssert.AreEqual(new[] { 1 }, back);
            Assert.IsFalse(shelf.Has(ModelSites.BuiltIn), "taken out when it's live");
            Assert.IsTrue(shelf.Has("gym"));
            CollectionAssert.AreEqual(new[] { 2 }, shelf.Peek("gym"));
            Assert.AreEqual(1, shelf.TakeAll().Count);
            Assert.AreEqual(0, shelf.Count);
        }
    }
}
