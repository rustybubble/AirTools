using System;
using System.Collections.Generic;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// switchclean (1), pure (the offline runner): a world-model switch closes every UI in front of you. The run over the
    /// close list (order, what's open, a surface kept up, one that throws), the log line, the checkout rules (never
    /// closed while Pay is held or a payment is being authorized; closed right after a hold that didn't pay), the hook
    /// (SiteScope.Leaving: once per real switch, before any owner parks, with Current still the site left), the palm
    /// ring's dismissal, the agent replies that still run after a switch, and the guide rail's reading lookup.
    public class SwitchCloseTests
    {
        [SetUp]
        public void SetUp() { SiteScope.Reset(); SwitchClose.ResetStats(); }

        [TearDown]
        public void TearDown() { SiteScope.Reset(); SwitchClose.ResetStats(); }

        /// A stand-in window: open or not, and how often it was closed.
        sealed class FakeWindow
        {
            public readonly string Name;
            public bool Open;
            public int Closes;
            public FakeWindow(string name, bool open) { Name = name; Open = open; }
            public SwitchClose.Surface Surface(Func<string> keep = null, Action<string> kept = null) =>
                new SwitchClose.Surface(Name, () => Open, () => { Closes++; Open = false; }, keep, kept);
        }

        [Test]
        public void EveryOpenSurfaceClosesInOrder_AndClosedOnesAreLeftAlone()
        {
            var catalog = new FakeWindow("Catalog", true);
            var settings = new FakeWindow("Settings", false);
            var notebook = new FakeWindow("Notebook", true);
            var palm = new FakeWindow("Palm ring", true);
            var closed = new List<string>(); var kept = new List<string>();
            SwitchClose.Run(new[] { catalog.Surface(), settings.Surface(), notebook.Surface(), palm.Surface() }, closed, kept);
            CollectionAssert.AreEqual(new[] { "Catalog", "Notebook", "Palm ring" }, closed, "the open ones, in the list's order");
            Assert.IsEmpty(kept);
            Assert.AreEqual(1, catalog.Closes); Assert.AreEqual(0, settings.Closes, "a closed window isn't closed again");
            Assert.IsFalse(catalog.Open || notebook.Open || palm.Open);
            // A second switch with nothing open closes nothing.
            SwitchClose.Run(new[] { catalog.Surface(), settings.Surface(), notebook.Surface(), palm.Surface() }, closed, kept);
            Assert.IsEmpty(closed);
        }

        [Test]
        public void AKeptSurfaceStaysUp_SaysWhy_AndHearsAboutIt()
        {
            var checkout = new FakeWindow("Checkout", true);
            var sellers = new FakeWindow("Sellers", true);
            string heard = null;
            var closed = new List<string>(); var kept = new List<string>();
            SwitchClose.Run(new[] { sellers.Surface(), checkout.Surface(() => SwitchClose.HoldingPay, why => heard = why) }, closed, kept);
            CollectionAssert.AreEqual(new[] { "Sellers" }, closed);
            CollectionAssert.AreEqual(new[] { "Checkout (holding Pay)" }, kept);
            Assert.AreEqual(0, checkout.Closes, "never closed mid-hold");
            Assert.IsTrue(checkout.Open);
            Assert.AreEqual(SwitchClose.HoldingPay, heard, "told it was kept (it closes after the hold)");
        }

        [Test]
        public void ASurfaceThatThrowsNeverStopsTheRest()
        {
            var after = new FakeWindow("Notebook", true);
            var bad = new SwitchClose.Surface("Grok card", () => true, () => throw new InvalidOperationException("boom"));
            var badOpen = new SwitchClose.Surface("Coach card", () => throw new InvalidOperationException("no coach"), () => { });
            var closed = new List<string>(); var kept = new List<string>(); var failed = new List<string>();
            SwitchClose.Run(new[] { bad, badOpen, after.Surface() }, closed, kept, failed);
            CollectionAssert.AreEqual(new[] { "Grok card", "Notebook" }, closed, "a close that threw still counts as closed; the rest go on");
            Assert.AreEqual(2, failed.Count, string.Join("; ", failed));
            StringAssert.Contains("boom", failed[0]);
            Assert.IsFalse(after.Open);
        }

        [Test]
        public void TheLogLineNamesTheSiteWhatClosedAndWhatStayed()
        {
            Assert.AreEqual("Switched to zabel-gymnasium: closed [Catalog, Settings]",
                SwitchClose.Line("zabel-gymnasium", new[] { "Catalog", "Settings" }, new string[0]));
            Assert.AreEqual("Switched to kitchen: closed []", SwitchClose.Line("kitchen", new string[0], null));
            Assert.AreEqual("Switched to built-in: closed [Notebook]; kept [Checkout (paying)]",
                SwitchClose.Line(null, new[] { "Notebook" }, new[] { "Checkout (paying)" }));
            string line = SwitchClose.Record("kitchen", new[] { "Sellers" }, new[] { "Checkout (holding Pay)" });
            Assert.AreEqual(line, SwitchClose.LastLine);
            Assert.AreEqual("kitchen", SwitchClose.LastSite);
            CollectionAssert.AreEqual(new[] { "Sellers" }, SwitchClose.LastClosed);
            CollectionAssert.AreEqual(new[] { "Checkout (holding Pay)" }, SwitchClose.LastKept);
            Assert.AreEqual(1, SwitchClose.Runs);
        }

        [Test]
        public void TheCheckoutIsNeverClosedMidHoldOrWhilePaying()
        {
            Assert.AreEqual(SwitchClose.Paying, SwitchClose.CheckoutKeep(CheckoutState.Paying, false), "a payment being authorized");
            Assert.AreEqual(SwitchClose.Paying, SwitchClose.CheckoutKeep(CheckoutState.Paying, true));
            Assert.AreEqual(SwitchClose.HoldingPay, SwitchClose.CheckoutKeep(CheckoutState.Ready, true), "the Pay ring filling");
            Assert.AreEqual(SwitchClose.HoldingPay, SwitchClose.CheckoutKeep(CheckoutState.Failed, true), "retrying after a failure");
            Assert.IsNull(SwitchClose.CheckoutKeep(CheckoutState.Ready, false), "reviewing the order: it closes");
            Assert.IsNull(SwitchClose.CheckoutKeep(CheckoutState.Failed, false));
            Assert.IsNull(SwitchClose.CheckoutKeep(CheckoutState.Paid, false), "the receipt: it closes");
            Assert.IsNull(SwitchClose.CheckoutKeep(CheckoutState.Closed, false));
        }

        [Test]
        public void AHoldKeptThroughASwitchClosesRightAfter_UnlessItPaid()
        {
            // Still holding: wait.
            Assert.AreEqual(SwitchClose.AfterHold.Wait, SwitchClose.StepAfterHold(CheckoutState.Ready, true));
            // Let go early (or a hold refused before anything was sent): nothing paid, close it now.
            Assert.AreEqual(SwitchClose.AfterHold.Close, SwitchClose.StepAfterHold(CheckoutState.Ready, false));
            Assert.AreEqual(SwitchClose.AfterHold.Close, SwitchClose.StepAfterHold(CheckoutState.Failed, false));
            // The hold paid: the payment's answer and receipt are yours to see; the deferred close is dropped.
            Assert.AreEqual(SwitchClose.AfterHold.Stay, SwitchClose.StepAfterHold(CheckoutState.Paying, false));
            Assert.AreEqual(SwitchClose.AfterHold.Stay, SwitchClose.StepAfterHold(CheckoutState.Paid, false));
            // Closed meanwhile (Keep working): nothing to do.
            Assert.AreEqual(SwitchClose.AfterHold.Stay, SwitchClose.StepAfterHold(CheckoutState.Closed, false));
        }

        /// A stand-in owner that records what the switch looked like when it parked.
        sealed class Owner : ISiteScoped
        {
            public readonly List<string> Seen = new List<string>();
            public bool SwitchingSeen;
            public void SwitchSite(string from, string to, float factor) { Seen.Add($"{from}>{to}"); SwitchingSeen |= SiteScope.Switching; }
            public void ClearParked() { }
            public int ParkedCount => 0;
        }

        [Test]
        public void LeavingIsRaisedOncePerRealSwitch_BeforeAnyOwnerParks_OnTheSiteLeft()
        {
            var owner = new Owner();
            SiteScope.Register(owner);
            var order = new List<string>();
            string currentWhenLeaving = null;
            bool switchingWhenLeaving = false;
            SiteScope.Leaving += (from, to) =>
            {
                order.Add($"leaving {from}>{to} (owner calls so far {owner.Seen.Count})");
                currentWhenLeaving = SiteScope.Current;
                switchingWhenLeaving = SiteScope.Switching;
            };
            Assert.IsFalse(SiteScope.Switching);
            Assert.IsTrue(SiteScope.Switch("kitchen", 1f));
            CollectionAssert.AreEqual(new[] { "leaving built-in>kitchen (owner calls so far 0)" }, order, "before any owner parks");
            Assert.AreEqual(ModelSites.BuiltIn, currentWhenLeaving, "Current is still the site left");
            Assert.IsTrue(switchingWhenLeaving, "the windows close inside the switch");
            Assert.IsTrue(owner.SwitchingSeen, "the owners park and come back inside it too");
            Assert.IsFalse(SiteScope.Switching, "and it's over after");
            Assert.IsFalse(SiteScope.Switch("kitchen", 1f), "the same site again");
            Assert.AreEqual(1, order.Count, "no switch, no close");
            // A held arrival (SceneStreamer's fresh load): the close happens as the switch starts, not when it arrives.
            SiteScope.HoldArrival();
            SiteScope.Switch("zabel-gymnasium", 0f);
            Assert.AreEqual(2, order.Count);
            StringAssert.StartsWith("leaving kitchen>zabel-gymnasium", order[1]);
            SiteScope.Arrive(1f);
            Assert.AreEqual(2, order.Count, "arriving closes nothing more");
            CollectionAssert.AreEqual(new[] { "built-in>(arriving)", "(arriving)>kitchen", "kitchen>(arriving)", "(arriving)>zabel-gymnasium" }, owner.Seen);
        }

        [Test]
        public void ALeavingListenerThatThrowsNeverStopsTheSwitch()
        {
            var owner = new Owner();
            SiteScope.Register(owner);
            int after = 0;
            SiteScope.Leaving += (f, t) => throw new InvalidOperationException("a window that won't close");
            SiteScope.Leaving += (f, t) => after++;
            // Log.Warn needs Unity: SiteScope swallows a logger that throws, so this runs offline as well as in the Editor.
            Assert.IsTrue(SiteScope.Switch("kitchen", 1f));
            Assert.AreEqual(1, after, "the next listener still ran");
            Assert.AreEqual(2, owner.Seen.Count, "the owners still switched");
            Assert.AreEqual("kitchen", SiteScope.Current);
            Assert.IsFalse(SiteScope.Switching);
        }

        [Test]
        public void ResetForgetsTheListeners()
        {
            int calls = 0;
            SiteScope.Leaving += (f, t) => calls++;
            SiteScope.Reset();
            SiteScope.Switch("kitchen", 1f);
            Assert.AreEqual(0, calls, "a new Play session subscribes again (SwitchClose.Install, BeforeSceneLoad)");
        }

        [Test]
        public void TheRingStaysShutUntilThePalmHasDropped()
        {
            bool dismissed = true;
            Assert.IsFalse(SwitchClose.PalmAfterDismiss(true, ref dismissed), "the palm still up after the switch: shut");
            Assert.IsTrue(dismissed);
            Assert.IsFalse(SwitchClose.PalmAfterDismiss(true, ref dismissed));
            Assert.IsFalse(SwitchClose.PalmAfterDismiss(false, ref dismissed), "the palm drops");
            Assert.IsFalse(dismissed, "the dismissal is spent");
            Assert.IsTrue(SwitchClose.PalmAfterDismiss(true, ref dismissed), "raised again: it opens as usual");
            bool none = false;
            Assert.IsTrue(SwitchClose.PalmAfterDismiss(true, ref none), "no switch: the gesture decides");
            Assert.IsFalse(SwitchClose.PalmAfterDismiss(false, ref none));
        }

        [Test]
        public void AReplyForAModelYouLeftOnlyKeepsTheSwitchItAskedFor()
        {
            Assert.IsTrue(SwitchClose.RunsAfterSwitch("show_model"));
            Assert.IsTrue(SwitchClose.RunsAfterSwitch("next_model"));
            foreach (var a in new[] { "measure_edges", "check_slope", "survey", "show_survey", "scene_pin", "place_part", "remove_component",
                                      "measure_cavity", "show_plan", "show_labels", "coach_step", "show_postcard", "search_started", "show_sellers", "add_note", "equip_tool" })
                Assert.IsFalse(SwitchClose.RunsAfterSwitch(a), $"{a} was for the model left");
        }

        [Test]
        public void TheGuideRailReadsTheLoadedModelsLastReading()
        {
            NotebookEntry E(string tool, double v, string unit, string site) =>
                new NotebookEntry(tool, v, unit, new Vector3[0], DateTime.Now, -1, tool) { SiteKey = site };
            var kitchenArea = E("measure", 12.5, "m²", "kitchen");
            var gymTape = E("measure", 3.2, "m", "zabel-gymnasium");
            var list = new List<NotebookEntry> { gymTape, kitchenArea };
            SiteScope.Switch("zabel-gymnasium", 1f);
            Assert.AreSame(gymTape, Notebook.NewestOnSite(list), "the gym's own tape, not the kitchen's newer area");
            SiteScope.Switch("kitchen", 1f);
            Assert.AreSame(kitchenArea, Notebook.NewestOnSite(list));
            SiteScope.Switch("hospital-bg", 1f);
            Assert.IsNull(Notebook.NewestOnSite(list), "nothing measured here yet");
        }
    }
}
