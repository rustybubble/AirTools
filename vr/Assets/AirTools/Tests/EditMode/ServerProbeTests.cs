using AirTools.Core;
using NUnit.Framework;

namespace AirTools.Tests
{
    /// fix-ux: a launch from the Quest library has no server override, so the app asked its default :8000 while the demo
    /// backend ran on :8004 (2026-09-26). At start ServerConfig asks the default, then the candidates, in order.
    public class ServerProbeTests
    {
        static readonly string[] Candidates = { "http://127.0.0.1:8004", "http://127.0.0.1:8000" };
        const string Default = "http://127.0.0.1:8000";

        [Test]
        public void Order_DefaultFirst_ThenCandidates_WithoutRepeats()
        {
            var p = new ServerProbe(Default, Candidates);
            CollectionAssert.AreEqual(new[] { "http://127.0.0.1:8000", "http://127.0.0.1:8004" }, p.Order);
            CollectionAssert.AreEqual(new[] { "http://localhost:8000", "http://127.0.0.1:8004", "http://127.0.0.1:8000" },
                ServerProbe.OrderOf("http://localhost:8000/", new[] { "http://127.0.0.1:8004/", "", null, "http://127.0.0.1:8000", "http://127.0.0.1:8004" }));
        }

        [Test]
        public void DefaultSilent_8004Answers_UsesItForTheSession()
        {
            var p = new ServerProbe(Default, Candidates);
            Assert.AreEqual(Default, p.Next);
            p.Report(false);
            Assert.AreEqual("http://127.0.0.1:8004", p.Next);
            p.Report(true);
            Assert.IsTrue(p.Done);
            Assert.AreEqual("http://127.0.0.1:8004", p.Switch);
            Assert.AreEqual("ServerConfig: default http://127.0.0.1:8000 didn't answer; using http://127.0.0.1:8004", p.Message());
        }

        [Test]
        public void DefaultAnswers_NoSwitch()
        {
            var p = new ServerProbe(Default, Candidates);
            p.Report(true);
            Assert.IsTrue(p.Done);
            Assert.IsNull(p.Switch);
            Assert.AreEqual(Default, p.Chosen);
            Assert.AreEqual("ServerConfig: default http://127.0.0.1:8000 answered", p.Message());
        }

        [Test]
        public void NothingAnswers_StaysOnTheDefault()
        {
            var p = new ServerProbe(Default, Candidates);
            p.Report(false);
            p.Report(false);
            Assert.IsTrue(p.Done);
            Assert.IsNull(p.Chosen);
            Assert.IsNull(p.Switch);
            Assert.IsNull(p.Next);
            StringAssert.StartsWith("ServerConfig: no server answered (http://127.0.0.1:8000, http://127.0.0.1:8004); staying on http://127.0.0.1:8000", p.Message());
            p.Report(true);
            Assert.IsNull(p.Chosen, "a late answer after the end changes nothing");
        }

        [Test]
        public void TheFirstThatAnswers_Wins()
        {
            var p = new ServerProbe("http://127.0.0.1:8000", new[] { "http://127.0.0.1:8004", "http://127.0.0.1:8002" });
            p.Report(false);
            p.Report(true);
            Assert.AreEqual("http://127.0.0.1:8004", p.Switch);
            Assert.IsNull(p.Next);
        }

        [Test]
        public void ProbeOnlyWithoutAnOverride()
        {
            Assert.IsTrue(ServerProbe.ShouldProbe(hasOverride: false, candidates: 2));
            Assert.IsFalse(ServerProbe.ShouldProbe(hasOverride: true, candidates: 2), "a launch extra, server.txt or a saved pref decides");
            Assert.IsFalse(ServerProbe.ShouldProbe(hasOverride: false, candidates: 0));
        }

        [Test]
        public void WhatCountsAsAnswering()
        {
            Assert.IsTrue(ServerProbe.Answered(200));
            Assert.IsTrue(ServerProbe.Answered(204));
            Assert.IsFalse(ServerProbe.Answered(0), "no answer / our clock gave up");
            Assert.IsFalse(ServerProbe.Answered(404));
            Assert.IsFalse(ServerProbe.Answered(500));
            Assert.IsTrue(ServerProbe.AskScenes(404), "an older server without /health is asked /scenes");
            Assert.IsFalse(ServerProbe.AskScenes(0));
            Assert.IsFalse(ServerProbe.AskScenes(502));
        }
    }
}
