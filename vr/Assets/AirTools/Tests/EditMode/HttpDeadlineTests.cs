using System.IO;
using System.Linq;
using AirTools.Core;
using NUnit.Framework;

namespace AirTools.Tests
{
    /// fix-ux: our own clock on every web request (HttpDeadline). On the headset (2026-09-26) no request of three
    /// sessions answered or failed despite each one's timeout; now each call gives up by Time.realtimeSinceStartup.
    public class HttpDeadlineTests
    {
        [Test]
        public void Verdict_SmallCall_NoAnswerIn6s()
        {
            // A 5 s-timeout JSON call that never answers: our clock gives up at 6 s (idle and total agree).
            float total = HttpDeadline.TotalFor(5);
            Assert.AreEqual(6f, total);
            Assert.IsNull(HttpDeadline.Verdict(5.9f, 0f, 0f, false, HttpDeadline.SmallIdle, total));
            Assert.AreEqual("no answer in 6 s", HttpDeadline.Verdict(6f, 0f, 0f, false, HttpDeadline.SmallIdle, total));
        }

        [Test]
        public void Verdict_LongCall_NoIdleLimit_OnlyItsTimeout()
        {
            // /agent/command (the server thinks for seconds before a byte comes back): idle off, 30 s timeout + 1.
            float total = HttpDeadline.TotalFor(30);
            Assert.IsNull(HttpDeadline.Verdict(25f, 0f, 0.01f, false, 0f, total), "a long silence is fine");
            Assert.AreEqual("no answer in 31 s", HttpDeadline.Verdict(31f, 0f, 0.01f, false, 0f, total));
        }

        [Test]
        public void Verdict_Download_MustKeepReceiving()
        {
            float total = HttpDeadline.TotalFor(120);
            Assert.IsNull(HttpDeadline.Verdict(40f, 0f, 30f, true, HttpDeadline.DownloadIdle, total), "bytes 10 s ago");
            Assert.AreEqual("no data for 15 s", HttpDeadline.Verdict(45f, 0f, 30f, true, HttpDeadline.DownloadIdle, total));
            Assert.AreEqual("not finished in 121 s", HttpDeadline.Verdict(121f, 0f, 120.5f, true, HttpDeadline.DownloadIdle, total));
        }

        [Test]
        public void Verdict_LimitsOff_NeverGivesUp()
        {
            Assert.IsNull(HttpDeadline.Verdict(1e6f, 0f, 0f, false, 0f, 0f));
        }

        [Test]
        public void TotalFor_UsesTheRequestsOwnTimeout()
        {
            Assert.AreEqual(4f, HttpDeadline.TotalFor(3), "the presenter link's 3 s");
            Assert.AreEqual(HttpDeadline.DefaultTotal, HttpDeadline.TotalFor(0), "no timeout of its own");
            Assert.AreEqual(10f, HttpDeadline.TotalFor(0, 10f));
        }

        [Test]
        public void PathOf_NamesTheCall()
        {
            Assert.AreEqual("/parts/jobs/abc", HttpDeadline.PathOf("http://127.0.0.1:8004/parts/jobs/abc?after=3"));
            Assert.AreEqual("/presenter/next", HttpDeadline.PathOf("http://127.0.0.1:8766/presenter/next?max=4"));
            Assert.AreEqual("/", HttpDeadline.PathOf("http://localhost:8000"));
            Assert.AreEqual("/scenes/kitchen/scene.json", HttpDeadline.PathOf("/scenes/kitchen/scene.json"));
            Assert.AreEqual("", HttpDeadline.PathOf(null));
        }

        [Test]
        public void WarnGate_OneWarnPerCallKindEvery30s()
        {
            var g = new HttpDeadline.WarnGate();
            Assert.IsTrue(g.ShouldWarn("GET /presenter/next", 0f));
            Assert.IsFalse(g.ShouldWarn("GET /presenter/next", 4f), "the next poll's hang is quiet");
            Assert.IsTrue(g.ShouldWarn("GET scene.json", 4f), "another call warns");
            Assert.IsTrue(g.ShouldWarn("GET /presenter/next", 30.5f));
        }

        [Test]
        public void EveryRequestGoesThroughOurClock()
        {
            // Source guard: a raw SendWebRequest() waits on UnityWebRequest.timeout alone, which the headset ignored.
            var raw = Directory.GetFiles("Assets/AirTools/Runtime", "*.cs", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f) != "HttpDeadline.cs" && File.ReadAllText(f).Contains("SendWebRequest("))
                .Select(Path.GetFileName).ToList();
            Assert.IsEmpty(raw, "use HttpDeadline.Send: " + string.Join(", ", raw));
        }
    }
}
