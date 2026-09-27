using System.Collections.Generic;
using System.Linq;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Parts;
using AirTools.Scene;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// e2e (docs/demo-prompts.md): the replace flow's phrases the headset does itself when the server doesn't
    /// (LocalIntents). Pure: runs in the offline runner.
    public class LocalIntentsMatchTests
    {
        static LocalIntent M(string text) => LocalIntents.Match(text);

        [TestCase("remove the dishwasher", "dishwasher")]
        [TestCase("Remove the dishwasher.", "dishwasher")]
        [TestCase("take out the dishwasher", "dishwasher")]
        [TestCase("Take the dishwasher out", "dishwasher")]
        [TestCase("okay grok, pull out the old dishwasher please", "dishwasher")]
        [TestCase("hey grok can you remove the dishwasher?", "dishwasher")]
        [TestCase("get rid of the fridge", "fridge")]
        [TestCase("take out the base cabinet", "base cabinet")]
        [TestCase("rip out the sink cabinet", "sink cabinet")]
        public void Remove(string text, string thing)
        {
            var m = M(text);
            Assert.AreEqual(LocalIntentKind.Remove, m.Kind, text);
            Assert.AreEqual(thing, m.Thing, text);
        }

        [TestCase("put it back", null)]
        [TestCase("Put it back in, please", null)]
        [TestCase("put the dishwasher back", "dishwasher")]
        [TestCase("put the dishwasher back in there", "dishwasher")]
        [TestCase("bring the original back", null)]
        [TestCase("restore the dishwasher", "dishwasher")]
        [TestCase("undo the removal", null)]
        public void PutBack(string text, string thing)
        {
            var m = M(text);
            Assert.AreEqual(LocalIntentKind.PutBack, m.Kind, text);
            Assert.AreEqual(thing, m.Thing, text);
        }

        [TestCase("measure it")]
        [TestCase("Measure the gap.")]
        [TestCase("measure the dimensions of it")]
        [TestCase("measure the hole")]
        [TestCase("measure the opening")]
        [TestCase("measure the dishwasher gap")]
        [TestCase("measure where it was")]
        [TestCase("measure where the dishwasher was")]
        [TestCase("how big is the gap?")]
        [TestCase("what are the dimensions?")]
        [TestCase("what's the size of the gap")]
        [TestCase("okay now measure that space")]
        public void MeasureGap(string text) => Assert.AreEqual(LocalIntentKind.MeasureGap, M(text).Kind, text);

        [TestCase("find a dishwasher that fits", "dishwasher", true)]
        [TestCase("find dishwashers that fit", "dishwashers", true)]
        [TestCase("Find me a new dishwasher that will fit in there.", "dishwasher", true)]
        [TestCase("look for new dishwashers", "dishwashers", false)]
        [TestCase("find one that fits", null, true)]
        [TestCase("find me a replacement", null, false)]
        [TestCase("search for a dishwasher for the gap", "dishwasher", true)]
        [TestCase("find a gutter hanger", "gutter hanger", false)]
        [TestCase("show me dishwashers that fit", "dishwashers", true)]
        public void FindFitting(string text, string thing, bool fits)
        {
            var m = M(text);
            Assert.AreEqual(LocalIntentKind.FindFitting, m.Kind, text);
            Assert.AreEqual(thing, m.Thing, text);
            Assert.AreEqual(fits, m.Fits, text);
        }

        [TestCase("put it in there")]
        [TestCase("place it")]
        [TestCase("install it")]
        [TestCase("Put the best one in.")]
        [TestCase("try it in the gap")]
        [TestCase("put it in the gap please")]
        [TestCase("put the dishwasher in")]
        [TestCase("slot it in there")]
        public void PlaceIt(string text) => Assert.AreEqual(LocalIntentKind.PlaceIt, M(text).Kind, text);

        [TestCase("next one")]
        [TestCase("next")]
        [TestCase("Next model.")]
        [TestCase("show me the next model")]
        [TestCase("try another one")]
        [TestCase("another one")]
        [TestCase("a different one")]
        [TestCase("swap it")]
        [TestCase("switch models")]
        public void Next(string text) => Assert.AreEqual(LocalIntentKind.Next, M(text).Kind, text);

        [TestCase("previous one")]
        [TestCase("go back to the previous one")]
        [TestCase("the one before")]
        [TestCase("previous model please")]
        public void Previous(string text) => Assert.AreEqual(LocalIntentKind.Previous, M(text).Kind, text);

        [TestCase("show me option 2", 1)]
        [TestCase("option three", 2)]
        [TestCase("the first one", 0)]
        [TestCase("try number 3", 2)]
        [TestCase("Show me the second one.", 1)]
        [TestCase("model #1", 0)]
        public void ShowOption(string text, int index)
        {
            var m = M(text);
            Assert.AreEqual(LocalIntentKind.ShowOption, m.Kind, text);
            Assert.AreEqual(index, m.Index, text);
        }

        [TestCase("the opening is 34 and a half inches tall", "h", 0.8763)]
        [TestCase("The gap is 24 inches wide.", "w", 0.6096)]
        [TestCase("it's 610 mm wide", "w", 0.61)]
        [TestCase("the opening's height is 87.6 cm", "h", 0.876)]
        [TestCase("okay, the gap should be 24 inches wide", "w", 0.6096)]
        [TestCase("set the scale, the opening is 34.5 inches tall", "h", 0.8763)]
        [TestCase("the gap is 24½ in deep", "d", 0.6223)]
        public void ScaleGap(string text, string axis, double metres)
        {
            var m = M(text);
            Assert.AreEqual(LocalIntentKind.ScaleGap, m.Kind, text);
            Assert.AreEqual(axis, m.Axis, text);
            Assert.AreEqual(metres, m.Metres, 1e-4, text);
        }

        [TestCase("the gap is huge")]
        [TestCase("it's 5 feet tall")]
        [TestCase("the opening is 0 inches wide")]
        public void NotAScale(string text) => Assert.AreNotEqual(LocalIntentKind.ScaleGap, M(text).Kind, text);

        /// Everything else stays the server's: other fast paths, questions, compound requests with limits or finishes.
        [TestCase("what am I looking at?")]
        [TestCase("measure every cabinet door")]
        [TestCase("measure the upper drawers")]
        [TestCase("is this gutter sloped enough to drain?")]
        [TestCase("place them every 60 cm")]
        [TestCase("put a note here")]
        [TestCase("do the whole job")]
        [TestCase("show it in stainless")]
        [TestCase("find a 30-inch induction range under $1,200 that fits")]
        [TestCase("next step")]
        [TestCase("go back")]
        [TestCase("remove it")]
        [TestCase("take it out")]
        [TestCase("select the first")]
        [TestCase("reimagine with navy cabinets")]
        [TestCase("send it to my contractor")]
        [TestCase("is it recalled?")]
        [TestCase("buy it")]
        [TestCase("")]
        [TestCase("   ")]
        public void NotAFlowPhrase(string text) => Assert.AreEqual(LocalIntentKind.None, M(text).Kind, text);

        [Test]
        public void CompoundPhrasesSplitOnAndThen()
        {
            var two = LocalIntents.MatchAll("remove the dishwasher and measure the gap");
            CollectionAssert.AreEqual(new[] { LocalIntentKind.Remove, LocalIntentKind.MeasureGap }, two.Select(i => i.Kind).ToArray());
            Assert.AreEqual("dishwasher", two[0].Thing);

            var three = LocalIntents.MatchAll("Take out the dishwasher, measure it, and find one that fits.");
            CollectionAssert.AreEqual(new[] { LocalIntentKind.Remove, LocalIntentKind.MeasureGap, LocalIntentKind.FindFitting }, three.Select(i => i.Kind).ToArray());
            Assert.IsNull(three[2].Thing, "\"one\" = the removed part's label");

            var scale = LocalIntents.MatchAll("the opening is 34 and a half inches tall and find a dishwasher that fits");
            CollectionAssert.AreEqual(new[] { LocalIntentKind.ScaleGap, LocalIntentKind.FindFitting }, scale.Select(i => i.Kind).ToArray(), "\"and a half\" is one amount");
            Assert.AreEqual(0.8763, scale[0].Metres, 1e-4);

            var then = LocalIntents.MatchAll("put it in there then next one");
            CollectionAssert.AreEqual(new[] { LocalIntentKind.PlaceIt, LocalIntentKind.Next }, then.Select(i => i.Kind).ToArray());

            Assert.AreEqual(0, LocalIntents.MatchAll("find a hinge and a screw").Count, "every piece must be a phrase");
            Assert.AreEqual(0, LocalIntents.MatchAll("remove the dishwasher and tell me a joke").Count);
            Assert.AreEqual(1, LocalIntents.MatchAll("remove the dishwasher").Count);
        }

        [TestCase("dishwashers", "dishwasher")]
        [TestCase("ranges", "range")]
        [TestCase("benches", "bench")]
        [TestCase("pantries", "pantry")]
        [TestCase("glass", "glass")]
        [TestCase("dishwasher", "dishwasher")]
        public void Singular(string plural, string single) => Assert.AreEqual(single, LocalIntents.Singular(plural));
    }

    /// Which of a reply's actions run and whether the headset does the phrase itself.
    public class LocalIntentsArbitrationTests
    {
        static AgentAction A(string name) => new AgentAction { name = name };
        static List<LocalIntent> I(string text) => LocalIntents.MatchAll(text);

        static LocalState State(bool gap = false, bool candidates = false, bool cycling = false, params string[] removable) => new LocalState
        {
            GapOpen = gap, HasCandidates = candidates, Cycling = cycling,
            CanRemove = t => removable.Contains(t) || removable.Contains(LocalIntents.Singular(t)),
        };

        static string[] Names(List<AgentAction> list) => list.Select(a => a.name).ToArray();

        [Test]
        public void NoActionFromTheServer_TheHeadsetDoesIt()
        {
            var plan = LocalIntents.Arbitrate(I("remove the dishwasher"), State(removable: "dishwasher"), new List<AgentAction>());
            Assert.IsTrue(plan.RunsLocal);
            Assert.AreEqual(LocalIntentKind.Remove, plan.Local[0].Kind);
        }

        [Test]
        public void TheServersOwnActionWins()
        {
            var plan = LocalIntents.Arbitrate(I("remove the dishwasher"), State(removable: "dishwasher"), new[] { A("remove_component") });
            Assert.IsFalse(plan.RunsLocal);
            CollectionAssert.AreEqual(new[] { "remove_component" }, Names(plan.Actions));

            var place = LocalIntents.Arbitrate(I("next one"), State(gap: true, candidates: true, cycling: true), new[] { A("place_part") });
            Assert.IsFalse(place.RunsLocal, "place_part does the switch");
            var cycle = LocalIntents.Arbitrate(I("show me option 2"), State(gap: true, candidates: true, cycling: true), new[] { A("cycle_model") });
            Assert.IsFalse(cycle.RunsLocal);
        }

        /// Seen on :8004 (docs/demo-prompts.md probe): "measure it" → survey {label: appliance, where: visible}; "put it in
        /// there" → a new search; "next one" → a new search.
        [Test]
        public void ContradictingActionsAreDroppedWhileTheFlowIsLive()
        {
            var measure = LocalIntents.Arbitrate(I("measure it"), State(gap: true), new[] { A("survey") });
            Assert.IsTrue(measure.RunsLocal);
            Assert.AreEqual(0, measure.Actions.Count);
            CollectionAssert.AreEqual(new[] { "survey" }, measure.Dropped);

            var place = LocalIntents.Arbitrate(I("put it in there"), State(gap: true, candidates: true), new[] { A("search_started") });
            Assert.IsTrue(place.RunsLocal);
            CollectionAssert.AreEqual(new[] { "search_started" }, place.Dropped);

            var next = LocalIntents.Arbitrate(I("next one"), State(gap: true, candidates: true, cycling: true), new[] { A("search_started") });
            Assert.IsTrue(next.RunsLocal);
            Assert.AreEqual(LocalIntentKind.Next, next.Local[0].Kind);

            var option = LocalIntents.Arbitrate(I("the first one"), State(gap: true, candidates: true, cycling: true), new[] { A("select_candidate") });
            Assert.IsTrue(option.RunsLocal, "into the gap, not into the hand");
        }

        [Test]
        public void AnotherLegitimateActionLeavesTheReplyAlone()
        {
            // A coach runs: "next" is its step.
            var coach = LocalIntents.Arbitrate(I("next"), State(gap: true, candidates: true, cycling: true), new[] { A("coach_step") });
            Assert.IsFalse(coach.RunsLocal);
            CollectionAssert.AreEqual(new[] { "coach_step" }, Names(coach.Actions));
            Assert.AreEqual(0, coach.Dropped.Count);
            // A search plus a finish: not ours to second-guess.
            var mixed = LocalIntents.Arbitrate(I("put it in there"), State(gap: true, candidates: true), new[] { A("search_started"), A("set_finish") });
            Assert.IsFalse(mixed.RunsLocal);
            CollectionAssert.AreEqual(new[] { "search_started", "set_finish" }, Names(mixed.Actions), "nothing dropped");
        }

        [Test]
        public void PassiveActionsRideAlong()
        {
            var plan = LocalIntents.Arbitrate(I("measure the gap"), State(gap: true), new[] { A("show_video"), A("add_note") });
            Assert.IsTrue(plan.RunsLocal);
            CollectionAssert.AreEqual(new[] { "show_video", "add_note" }, Names(plan.Actions));
        }

        [Test]
        public void OnlyWhenTheStateMakesItUnambiguous()
        {
            var noGap = LocalIntents.Arbitrate(I("measure it"), State(), new[] { A("survey") });
            Assert.IsFalse(noGap.RunsLocal, "no gap: \"measure it\" is the server's");
            CollectionAssert.AreEqual(new[] { "survey" }, Names(noGap.Actions));

            Assert.IsFalse(LocalIntents.Arbitrate(I("remove the dishwasher"), State(removable: "fridge"), new List<AgentAction>()).RunsLocal, "no dishwasher here");
            Assert.IsFalse(LocalIntents.Arbitrate(I("put it in there"), State(gap: true), new List<AgentAction>()).RunsLocal, "no candidates");
            Assert.IsFalse(LocalIntents.Arbitrate(I("next one"), State(), new List<AgentAction>()).RunsLocal, "nothing to switch");
            Assert.IsFalse(LocalIntents.Arbitrate(I("find a gutter hanger"), State(), new List<AgentAction>()).RunsLocal, "a plain search is the server's");
            Assert.IsTrue(LocalIntents.Arbitrate(I("find a hinge that fits"), State(), new List<AgentAction>()).RunsLocal, "asked for a fit");
            Assert.IsTrue(LocalIntents.Arbitrate(I("look for new dishwashers"), State(gap: true), new List<AgentAction>()).RunsLocal, "a gap is open");
            Assert.IsTrue(LocalIntents.Arbitrate(I("remove the dishwashers"), State(removable: "dishwasher"), null).RunsLocal, "no reply at all (typed)");
        }

        [Test]
        public void ScaleFromTheGapWhileItIsOpen()
        {
            Assert.IsTrue(LocalIntents.Arbitrate(I("the opening is 34 and a half inches tall"), State(gap: true), new[] { A("add_note") }).RunsLocal);
            Assert.IsFalse(LocalIntents.Arbitrate(I("the opening is 34 and a half inches tall"), State(gap: true), new[] { A("scale_gap") }).RunsLocal);
            Assert.IsFalse(LocalIntents.Arbitrate(I("the opening is 34 and a half inches tall"), State(), new List<AgentAction>()).RunsLocal, "no gap");
        }

        [Test]
        public void CompoundDoesWhatTheServerLeftOut()
        {
            var plan = LocalIntents.Arbitrate(I("remove the dishwasher and measure the gap"), State(removable: "dishwasher"), new[] { A("remove_component") });
            Assert.IsTrue(plan.RunsLocal);
            CollectionAssert.AreEqual(new[] { LocalIntentKind.MeasureGap }, plan.Local.Select(i => i.Kind).ToArray());
            CollectionAssert.AreEqual(new[] { "remove_component" }, Names(plan.Actions));
        }

        [Test]
        public void NoPhraseNoChange()
        {
            var actions = new[] { A("survey"), A("search_started") };
            var plan = LocalIntents.Arbitrate(I("measure every cabinet door"), State(gap: true, candidates: true, cycling: true), actions);
            Assert.IsFalse(plan.RunsLocal);
            CollectionAssert.AreEqual(new[] { "survey", "search_started" }, Names(plan.Actions));
        }
    }

    /// A candidate's true size against the gap on all three axes.
    public class CavityFitTests
    {
        static PartDims D(float w, float h, float d) => new PartDims { w = w, h = h, d = d };

        [TearDown]
        public void Units() => AirTools.UI.UiSettings.UseUnits(AirTools.UI.UiSettings.DefaultUnits);

        [Test]
        public void ClearancePerAxisAndTheTightest()
        {
            var gap = new Vector3(610f, 860f, 620f);
            var c = CavityFit.Clearance(D(603.25f, 847.72f, 609.6f), gap);
            Assert.AreEqual(6.75f, c.x, 1e-3f);
            Assert.AreEqual(12.28f, c.y, 1e-3f);
            Assert.AreEqual(10.4f, c.z, 1e-3f);
            Assert.AreEqual(6.75f, CavityFit.Tightest(c, out string axis), 1e-3f);
            Assert.AreEqual("w", axis);
            Assert.IsTrue(CavityFit.Fits(D(603.25f, 847.72f, 609.6f), gap));
            Assert.IsFalse(CavityFit.Fits(D(609.6f, 889f, 635f), gap), "the tall tub is too tall and deep");
        }

        [Test]
        public void TokensAndTheReportTheCalloutShows()
        {
            AirTools.UI.UiSettings.UseUnits(AirTools.Core.UnitSystem.Metric);
            var gap = new Vector3(600f, 820f, 580f);
            Assert.AreEqual("fits", (string)CavityFit.FitsToken(D(450f, 800f, 560f), gap));
            Assert.AreEqual("too_big", (string)CavityFit.FitsToken(D(603f, 850f, 610f), gap));
            var clearance = CavityFit.ClearanceToken(D(603f, 850f, 610f), gap);
            Assert.AreEqual(-3, (int)clearance["w"]); Assert.AreEqual(-30, (int)clearance["h"]); Assert.AreEqual(-30, (int)clearance["d"]);

            var fits = CavityFit.Report(D(450f, 800f, 560f), gap);
            Assert.AreEqual(FitStatus.Green, fits.Status);
            StringAssert.StartsWith("Fits the gap", fits.Verdict);
            Assert.AreEqual(20f, fits.SpareMm.Value, 1e-3f, "the tightest: 20 mm (height and depth)");

            var big = CavityFit.Report(D(603f, 850f, 610f), gap);
            Assert.AreEqual(FitStatus.Red, big.Status);
            StringAssert.StartsWith("Too big for the gap", big.Verdict);

            Assert.IsNull(CavityFit.Report(D(0f, 800f, 560f), gap), "no size: the app's own check runs");

            // Within 5 mm over: tight (amber), not red.
            Assert.AreEqual("tight", (string)CavityFit.FitsToken(D(603f, 820.3f, 570f), gap));
            var tight = CavityFit.Report(D(603f, 820.3f, 570f), gap);
            Assert.AreEqual(FitStatus.Amber, tight.Status);
            StringAssert.StartsWith("Tight in the gap", tight.Verdict);
            Assert.AreEqual("too_big", (string)CavityFit.FitsToken(D(606f, 800f, 570f), gap), "6 mm over is too big");
            Assert.IsNull(CavityFit.Report(D(450f, 800f, 560f), new Vector3(float.NaN, 1f, 1f)));
        }

        [Test]
        public void TheBestIsTheFirstThatFitsElseTheLeastOver()
        {
            var gap = new Vector3(610f, 870f, 620f);
            var list = new List<PartDims> { D(609.6f, 889f, 635f), D(603.25f, 847.72f, 609.6f), D(603.25f, 854.2f, 625.6f) };
            Assert.AreEqual(1, CavityFit.Best(list, gap));
            Assert.AreEqual(1, CavityFit.CountFitting(list, gap));
            var none = new List<PartDims> { D(700f, 900f, 700f), D(611f, 880f, 621f), null };
            Assert.AreEqual(1, CavityFit.Best(none, gap), "10 mm over beats 90 mm over");
            Assert.AreEqual(0, CavityFit.Best(new List<PartDims> { null, D(0, 0, 0) }, gap), "no sizes: the first");
            Assert.AreEqual(-1, CavityFit.Best(new List<PartDims>(), gap));
        }
    }

    /// The gap tapes (the real measure tool's points) and the context the server gets.
    public class GapTapesTests
    {
        static CavityBox Box(bool openTop = false) => new CavityBox
        {
            R = Vector3.right, U = Vector3.up, D = Vector3.forward,
            Min = new Vector3(0.1f, 0f, 0f), Max = new Vector3(0.7f, 0.82f, 0.58f), Size = new Vector3(0.6f, 0.82f, 0.58f), OpenTop = openTop,
        };

        [Test]
        public void HeightDepthThenWidth_OnTheCavityFaces()
        {
            var plan = CavityTapes.Plan(Box());
            CollectionAssert.AreEqual(new[] { "h", "d", "w" }, plan.Select(t => t.Axis).ToArray(), "the width last: the search fits against the latest tape");
            var h = plan[0]; var d = plan[1]; var w = plan[2];
            Assert.AreEqual(0.815f, Vector3.Distance(h.From, h.To), 1e-3f, "floor to underside, 2–3 mm in");
            Assert.AreEqual(0.572f, Vector3.Distance(d.From, d.To), 1e-3f, "back wall to the front lip, 4 mm in");
            Assert.AreEqual(0.6f, Vector3.Distance(w.From, w.To), 1e-4f, "jamb to jamb");
            Assert.AreEqual(0.1f, w.From.x, 1e-5f); Assert.AreEqual(0.7f, w.To.x, 1e-5f);
            foreach (var t in plan) Assert.Greater(t.Eye.z, 0.58f, $"{t.Axis}: aimed from in front of the opening");
        }

        [Test]
        public void AnOpenTopHasNoHeightTape()
        {
            CollectionAssert.AreEqual(new[] { "d", "w" }, CavityTapes.Plan(Box(openTop: true)).Select(t => t.Axis).ToArray());
            Assert.AreEqual("Dishwasher gap width", CavityTapes.Title("dishwasher", "w"));
            Assert.AreEqual("Sink cabinet gap height", CavityTapes.Title("sink cabinet", "h"));
        }

        [Test]
        public void TheCavityContext()
        {
            var ctx = Gaps.Context("dw1", "dishwasher", new Vector3(0.6004f, 0.8196f, 0.58f), new Vector3(0.601f, float.NaN, 0.579f));
            Assert.AreEqual("dw1", ctx["component_id"]);
            Assert.AreEqual("dishwasher", ctx["label"]);
            Assert.AreEqual(0.6, (double)ctx["w_m"], 1e-9);
            Assert.AreEqual(0.82, (double)ctx["h_m"], 1e-9);
            Assert.AreEqual(true, ctx["estimated"]);
            var measured = (Dictionary<string, object>)ctx["measured"];
            Assert.AreEqual(0.601, (double)measured["w_m"], 1e-9);
            Assert.IsFalse(measured.ContainsKey("h_m"), "a tape that didn't take isn't sent");
            Assert.IsFalse(Gaps.Context("dw1", "dishwasher", Vector3.one, null).ContainsKey("measured"));
        }

        [Test]
        public void TheAgentContextCarriesTheGap()
        {
            var s = new ContextSnapshot
            {
                Cavity = Gaps.Context("dw1", "dishwasher", new Vector3(0.6f, 0.82f, 0.58f), null),
                Removed = new List<string> { "dw1" },
            };
            var ctx = GrokContext.Build(s);
            Assert.IsTrue(ctx.ContainsKey("cavity"));
            CollectionAssert.AreEqual(new[] { "dw1" }, (List<string>)ctx["removed"]);
            var json = JObject.FromObject(ctx);
            Assert.AreEqual(0.82, (double)json["cavity"]["h_m"], 1e-9);
            var none = GrokContext.Build(new ContextSnapshot());
            Assert.IsFalse(none.ContainsKey("cavity"));
            Assert.IsFalse(none.ContainsKey("removed"), "a scene without parts says nothing");
            var nothingOut = GrokContext.Build(new ContextSnapshot { Removed = new List<string>() });
            CollectionAssert.IsEmpty((List<string>)nothingOut["removed"], "parts, none out: [] (the server's gap closes)");
        }

        [TestCase(0, 3, 0)] [TestCase(3, 3, 0)] [TestCase(-1, 3, 2)] [TestCase(4, 3, 1)] [TestCase(-4, 3, 2)] [TestCase(1, 0, -1)]
        public void SwitchingWraps(int i, int n, int want) => Assert.AreEqual(want, ModelCycler.Wrap(i, n));
    }
}
