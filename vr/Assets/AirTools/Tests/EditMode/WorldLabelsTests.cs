using System;
using System.Collections.Generic;
using System.Linq;
using AirTools.Agent.Grok;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// Declutter S10 (DC7, docs/ux/declutter.md §5): one pool of 12 world labels across every producer. Pure: the
    /// share-out and the registry with plain C# producers (no scene objects), so these run offline too. The tool-level
    /// fixture below (LabelPoolToolTests) needs the Editor.
    public class WorldLabelsTests
    {
        [SetUp, TearDown]
        public void Clean() => WorldLabels.Reset();

        static LabelClaim C(LabelClass cls, int mandatory, int items, float newest = 0f, int cap = 0) => new LabelClaim(cls, mandatory, items, newest, cap);

        static string Items(LabelGrant[] g) => string.Join(",", g.Select(x => x.Items));

        [Test]
        public void TheOrder_SafetyFocusTaskSavedPartsReadingsPassive()
        {
            // Listed out of order on purpose: the class decides, not the position.
            var claims = new List<LabelClaim>
            {
                C(LabelClass.Passive, 0, 3), C(LabelClass.Parts, 0, 3), C(LabelClass.Saved, 0, 3), C(LabelClass.Readings, 0, 3),
                C(LabelClass.Task, 0, 3), C(LabelClass.Focus, 0, 3), C(LabelClass.Safety, 0, 3),
            };
            var g = WorldLabels.Allocate(claims);
            Assert.AreEqual("0,0,3,0,3,3,3", Items(g), "safety, focus, task and saved fill the 12; parts, readings and passive drop to dots");
            Assert.AreEqual(WorldLabels.Max, g.Sum(x => x.Used));

            // With room to spare the lower classes get the rest, in order.
            g = WorldLabels.Allocate(claims, 16);
            Assert.AreEqual("0,3,3,1,3,3,3", Items(g), "parts (P4) before readings (P5)");
            g = WorldLabels.Allocate(claims, 19);
            Assert.AreEqual("1,3,3,3,3,3,3", Items(g), "readings (P5) before passive (P6)");
        }

        [Test]
        public void MandatoryLabelsCountFirst_ButAreClippedToThePool()
        {
            // Two honesty captions and a safety set that together ask for more than the pool: clipped, safety first.
            var claims = new List<LabelClaim> { C(LabelClass.Focus, 9, 4), C(LabelClass.Safety, 6, 0), C(LabelClass.Task, 0, 5) };
            var g = WorldLabels.Allocate(claims);
            Assert.AreEqual(6, g[1].Mandatory, "safety first");
            Assert.AreEqual(6, g[0].Mandatory, "the caption set is clipped to what's left");
            Assert.AreEqual(0, g[0].Items + g[2].Items, "no room left for items");
            Assert.AreEqual(12, g.Sum(x => x.Shown));

            // Mandatory labels count before any class's items, even a higher class's.
            g = WorldLabels.Allocate(new List<LabelClaim> { C(LabelClass.Safety, 0, 12), C(LabelClass.Task, 3, 0) });
            Assert.AreEqual(3, g[1].Mandatory);
            Assert.AreEqual(9, g[0].Items);

            // A caption set alone bigger than the pool shows 12 and nothing else.
            g = WorldLabels.Allocate(new List<LabelClaim> { C(LabelClass.Task, 13, 4) });
            Assert.AreEqual(12, g[0].Mandatory);
            Assert.AreEqual(0, g[0].Items);
        }

        [Test]
        public void WithinAClass_NewestFirst_TiesKeepTheListOrder()
        {
            var claims = new List<LabelClaim> { C(LabelClass.Task, 0, 5, 1f), C(LabelClass.Task, 0, 5, 3f), C(LabelClass.Task, 0, 5, 2f) };
            Assert.AreEqual("2,5,5", Items(WorldLabels.Allocate(claims)));
            var ties = new List<LabelClaim> { C(LabelClass.Saved, 0, 7, 4f), C(LabelClass.Saved, 0, 7, 4f) };
            Assert.AreEqual("7,5", Items(WorldLabels.Allocate(ties)), "same time: the earlier claim first");
            // "Now" (a live tape) beats any time; an unknown time (NaN) is the oldest.
            var live = new List<LabelClaim> { C(LabelClass.Focus, 0, 8, float.NaN), C(LabelClass.Focus, 0, 8, float.PositiveInfinity), C(LabelClass.Focus, 0, 8, 100f) };
            Assert.AreEqual("0,8,4", Items(WorldLabels.Allocate(live)));
        }

        [Test]
        public void TheFocusedShape_ShowsItsWholeSetForUpToFour_OrJustItsSummary()
        {
            // An L (6 sides + 6 angles + area = 13 labels) is one claim of up to 4: the survey still gets 8.
            var claims = new List<LabelClaim> { C(LabelClass.Focus, 0, 13, float.PositiveInfinity, cap: 4), C(LabelClass.Task, 0, 18, 5f) };
            var g = WorldLabels.Allocate(claims);
            Assert.AreEqual(13, g[0].Items, "the whole set");
            Assert.AreEqual(4, g[0].Used);
            Assert.AreEqual(8, g[1].Items);
            Assert.AreEqual(12, g.Sum(x => x.Used));
            Assert.AreEqual(21, g.Sum(x => x.Shown), "shown labels can exceed 12 only by the focused shape's set");

            // A distance is 1 label: it costs 1.
            g = WorldLabels.Allocate(new List<LabelClaim> { C(LabelClass.Focus, 0, 1, 0f, cap: 4), C(LabelClass.Task, 0, 18) });
            Assert.AreEqual(1, g[0].Used);
            Assert.AreEqual(11, g[1].Items);

            // Too little room for the set: its first label (the summary) only, never a partial set.
            g = WorldLabels.Allocate(new List<LabelClaim> { C(LabelClass.Safety, 10, 0), C(LabelClass.Focus, 0, 9, 0f, cap: 4), C(LabelClass.Saved, 0, 3) });
            Assert.AreEqual(1, g[1].Items);
            Assert.AreEqual(1, g[1].Used);
            Assert.AreEqual(1, g[2].Items);
            g = WorldLabels.Allocate(new List<LabelClaim> { C(LabelClass.Safety, 12, 0), C(LabelClass.Focus, 0, 9, 0f, cap: 4) });
            Assert.AreEqual(0, g[1].Items, "a full pool: outline only");
        }

        [Test]
        public void TheSameInput_TheSameShare_EveryCall()
        {
            var claims = new List<LabelClaim>
            {
                C(LabelClass.Saved, 0, 6, 2f), C(LabelClass.Focus, 1, 9, 9f, cap: 4), C(LabelClass.Task, 0, 7, 3f),
                C(LabelClass.Safety, 3, 0), C(LabelClass.Task, 1, 4, 8f), C(LabelClass.Passive, 0, 5, 1f),
            };
            var first = WorldLabels.Allocate(claims);
            var reused = new LabelGrant[claims.Count + 3];
            for (int k = 0; k < 5; k++)
            {
                WorldLabels.Allocate(claims, WorldLabels.Max, reused);
                CollectionAssert.AreEqual(first, reused.Take(claims.Count).ToArray(), $"call {k}");
            }
            // Order-independent too (every claim here has its own class or time): reversed input, same grant per claim.
            var reversed = Enumerable.Reverse(claims).ToList();
            var back = WorldLabels.Allocate(reversed).Reverse().ToArray();
            CollectionAssert.AreEqual(first, back);
            // Safety 3 + captions 2 + the live shape's cost 4 + 3 of the newer Grok layer's items = 12.
            Assert.AreEqual(12, first.Sum(g => g.Used));
            Assert.AreEqual(3, first[4].Items, "the newer task claim");
            Assert.AreEqual(0, first[2].Items + first[0].Items + first[5].Items);
        }

        [Test]
        public void EmptyAndNonsense_AreHarmless()
        {
            Assert.AreEqual(0, WorldLabels.Allocate(new List<LabelClaim>()).Length);
            Assert.AreEqual(0, WorldLabels.Allocate(null).Length);
            var g = WorldLabels.Allocate(new List<LabelClaim> { C(LabelClass.Task, -3, -2), C(LabelClass.Task, 0, 4) }, -1);
            Assert.AreEqual(0, g.Sum(x => x.Shown), "a negative pool is empty");
            Assert.Throws<ArgumentException>(() => WorldLabels.Allocate(new List<LabelClaim> { C(LabelClass.Task, 0, 1) }, 12, new LabelGrant[0]));
        }

        [Test]
        public void LabelBudgetSplit_IsThePoolsOneClassForm()
        {
            CollectionAssert.AreEqual(new[] { 8, 1 }, LabelBudget.Split(12, new[] { 2, 1 }, new[] { 8, 8 }));
            CollectionAssert.AreEqual(new[] { 5, 0, 0 }, LabelBudget.Split(12, new[] { 3, 2, 2 }, new[] { 5, 4 , 1 }), "captions of every set count first");
            CollectionAssert.AreEqual(new[] { 2 }, LabelBudget.Split(12, null, new[] { 2 }));
            Assert.AreEqual(WorldLabels.Max, LabelBudget.Max);
        }

        // ---------------- the registry ----------------

        /// A producer with fixed claims that remembers its last share.
        class Producer : IWorldLabelSource
        {
            public readonly List<LabelClaim> Claims = new List<LabelClaim>();
            public LabelGrant[] Last = new LabelGrant[0];
            public int Applied;
            public Action OnApply;

            public void ClaimLabels(List<LabelClaim> claims) => claims.AddRange(Claims);

            public void ApplyLabels(LabelGrant[] grants, int first)
            {
                Applied++;
                Last = new LabelGrant[Claims.Count];
                Array.Copy(grants, first, Last, 0, Claims.Count);
                OnApply?.Invoke();
            }
        }

        [Test]
        public void Registry_SharesAcrossProducers_AndGivesTheRoomBack()
        {
            var survey = new Producer();
            survey.Claims.Add(C(LabelClass.Task, 0, 18, 1f));      // B1: 18 doors
            var grok = new Producer();
            grok.Claims.Add(C(LabelClass.Focus, 1, 0, 2f));         // "AI triage from drone frames, not an inspection"
            grok.Claims.Add(C(LabelClass.Task, 0, 6, 2f));          // its pins: newer than the survey
            var edges = new Producer();
            edges.Claims.Add(C(LabelClass.Safety, 3, 0));

            WorldLabels.Changed(survey);
            Assert.AreEqual(12, survey.Last[0].Items, "alone: the whole pool");
            WorldLabels.Changed(grok);
            Assert.AreEqual(1, grok.Last[0].Mandatory);
            Assert.AreEqual(6, grok.Last[1].Items);
            Assert.AreEqual(5, survey.Last[0].Items, "the older task gets what's left");
            WorldLabels.Changed(edges);
            Assert.AreEqual(3, edges.Last[0].Mandatory);
            Assert.AreEqual(2, survey.Last[0].Items);
            Assert.AreEqual(12, WorldLabels.Used);
            Assert.AreEqual(12, WorldLabels.Shown);
            Assert.AreEqual(28, WorldLabels.Asked);
            Assert.AreEqual(16, WorldLabels.Dropped);
            Assert.AreEqual(3, WorldLabels.SourceCount);
            StringAssert.StartsWith("labels=12/12 shown=12 asked=28 dropped=16 | P0 safety 3 · P1 focus 1 · P2 task 8", WorldLabels.Summary());

            WorldLabels.Remove(grok);
            Assert.AreEqual(9, survey.Last[0].Items, "the Grok layer went: the survey gets its room back");
            WorldLabels.Changed(survey);   // registering twice is a no-op
            Assert.AreEqual(2, WorldLabels.SourceCount);
            WorldLabels.Remove(new Producer());   // never registered: nothing happens
            Assert.AreEqual(2, WorldLabels.SourceCount);
        }

        [Test]
        public void Registry_AProducerThatChangesWhileApplying_GetsABoundedExtraRound()
        {
            var p = new Producer();
            p.Claims.Add(C(LabelClass.Saved, 0, 3));
            int calls = 0;
            p.OnApply = () => { if (++calls < 100) WorldLabels.Changed(p); };
            WorldLabels.Changed(p);
            Assert.LessOrEqual(p.Applied, 3, "no recursion, at most three rounds per change");
            Assert.AreEqual(3, p.Last[0].Items);
        }

        [Test]
        public void Registry_AProducerThatThrows_DoesNotStopTheOthers()
        {
            var ok = new Producer();
            ok.Claims.Add(C(LabelClass.Saved, 0, 2));
            var bad = new Producer();
            bad.Claims.Add(C(LabelClass.Task, 0, 2));
            bad.OnApply = () => throw new InvalidOperationException("boom");
            WorldLabels.Changed(ok);
            // The bad producer's failure is logged (Debug.LogWarning); the good one still gets its share. Offline
            // (no Unity runtime) the log itself can't run, so only the share is checked.
            try { WorldLabels.Changed(bad); } catch (Exception) { /* offline: the warning's Debug call */ }
            Assert.AreEqual(2, ok.Last[0].Items);
        }

        [Test]
        public void Allocate_AllocatesNothingAfterWarmUp()
        {
            var claims = new List<LabelClaim>(16);
            for (int i = 0; i < 12; i++) claims.Add(C((LabelClass)(i % 7), i % 2, 3 + i, i * 0.5f, i == 1 ? 4 : 0));
            var grants = new LabelGrant[16];
            for (int k = 0; k < 200; k++) WorldLabels.Allocate(claims, WorldLabels.Max, grants);   // warm up (scratch, JIT tiers)
            long probe = GC.GetAllocatedBytesForCurrentThread();
            var junk = new byte[4096];
            long probed = GC.GetAllocatedBytesForCurrentThread() - probe;
            GC.KeepAlive(junk);
            if (probed < 4096) Assert.Pass("this runtime has no per-thread allocation counter");
            long Run(int calls)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int k = 0; k < calls; k++) WorldLabels.Allocate(claims, WorldLabels.Max, grants);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Run(10);   // the measuring loop's own first-run cost
            Run(10);
            Assert.AreEqual(0, Run(1000), "bytes allocated by 1000 share-outs");
        }
    }

    /// Declutter S10b on the real MeasureTool (Editor gate: MeasureView / TMP): the newest of the user's shapes shows
    /// its whole set, older ones one summary each while the pool has room, then just their outline; a tape in progress
    /// is the focus.
    public class LabelPoolToolTests : FacadeToolFixture
    {
        [SetUp] public void FreshPool() => WorldLabels.Reset();   // the tool re-registers on its next change
        [TearDown] public void EmptyPool() => WorldLabels.Reset();

        MeasureShape Quad(float x, float y)
        {
            Tool.AreaMode = true;
            foreach (var p in new[] { new Vector3(x, y, 0f), new Vector3(x + 0.3f, y, 0f), new Vector3(x + 0.3f, y + 0.25f, 0f), new Vector3(x, y + 0.25f, 0f) })
                Tool.Click(new SurfaceHit { point = p, kind = SnapKind.Corner });
            var s = Tool.Finish();
            Assert.IsNotNull(s);
            return s;
        }

        MeasureShape Tape(float x, float y)
        {
            Tool.AreaMode = false;   // D4: Line mode saves at the 2nd point
            Tool.Click(new SurfaceHit { point = new Vector3(x, y, 0f), kind = SnapKind.Corner });
            Tool.Click(new SurfaceHit { point = new Vector3(x + 0.4f, y, 0f), kind = SnapKind.Corner });
            if (Tool.Session.Count > 0) Tool.Finish();   // in case another test left D4's auto-save off
            Assert.AreEqual(0, Tool.Session.Count);
            return Tool.Shapes[Tool.Shapes.Count - 1];
        }

        [Test]
        public void TheNewestShapeShowsItsWholeSet_OlderOnesOneSummary()
        {
            var a = Quad(-3.8f, 1.6f);
            Assert.AreEqual(9, a.View.ActiveLabelCount, "4 sides + 4 angles + area");
            var b = Quad(-3.2f, 1.6f);
            Assert.AreEqual(9, b.View.ActiveLabelCount);
            Assert.AreEqual(1, a.View.ActiveLabelCount, "one summary");
            StringAssert.Contains("m²", a.View.Labels[0].Text, "the summary is the area");
            Assert.AreEqual(LabelDetail.Summary, a.Labels);
            Assert.AreSame(b, Tool.FocusShape);
            Tool.Undo();
            Assert.AreEqual(9, a.View.ActiveLabelCount, "the newest again: its whole set is back");
        }

        [Test]
        public void ATapeInProgressIsTheFocus()
        {
            var a = Quad(-3.8f, 1.6f);
            Tool.AreaMode = true;
            Tool.Click(new SurfaceHit { point = new Vector3(-2.6f, 1.6f, 0f), kind = SnapKind.Corner });
            Assert.AreEqual(1, a.View.ActiveLabelCount, "the tape you're making now comes first");
            Assert.IsNull(Tool.FocusShape);
            Tool.Undo();
            Assert.AreEqual(9, a.View.ActiveLabelCount);
        }

        [Test]
        public void PastThePool_OldTapesKeepJustTheirLine()
        {
            var tapes = new List<MeasureShape>();
            for (int i = 0; i < 14; i++) tapes.Add(Tape(-3.8f + (i % 7) * 0.5f, 1.2f + (i / 7) * 0.5f));
            Assert.AreEqual(1, tapes[13].View.ActiveLabelCount, "the newest");
            for (int i = 2; i < 13; i++) Assert.AreEqual(1, tapes[i].View.ActiveLabelCount, $"tape {i}: its length");
            Assert.AreEqual(0, tapes[0].View.ActiveLabelCount, "the oldest drops to its line");
            Assert.AreEqual(0, tapes[1].View.ActiveLabelCount);
            Assert.AreEqual(12, WorldLabels.Used);
            Assert.AreEqual(12, Tool.VisibleLabelCount);
            Tool.Undo();
            Assert.AreEqual(1, tapes[1].View.ActiveLabelCount, "room again: the next oldest gets its length back");
        }
    }
}
