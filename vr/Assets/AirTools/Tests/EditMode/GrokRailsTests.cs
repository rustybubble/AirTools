using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Scene;
using AirTools.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// Lane G4 (backend F15 "do the whole job", F17 install coach): the rail state machines, JSON → view models, the job
    /// polling cursor with injected time, the frame-box maths, and that nothing in this lane can pay. Pure: runs offline.
    /// Payloads are the backend's own (integration/grok-all @ c706f13), made by its code with its tests' recorded replies.
    public class GrokRailsTests
    {
        [SetUp]
        public void SetUp() => GrokRails.Reset();

        // ---------------- fixtures (copied from the backend's outputs) ----------------

        static List<AgentAction> Actions(string json) =>
            JArray.Parse(json).OfType<JObject>().Select(o => new AgentAction { name = (string)o["name"], args = o["args"] as JObject ?? new JObject() }).ToList();

        static List<JObject> Replies(string json) => JArray.Parse(json).OfType<JObject>().ToList();

        static List<AgentAction> ReplyActions(JObject reply) =>
            (reply["actions"] as JArray ?? new JArray()).OfType<JObject>()
            .Select(o => new AgentAction { name = (string)o["name"], args = o["args"] as JObject ?? new JObject() }).ToList();

        /// Apply this lane's actions; record every name in order (what AgentActions.ExecuteAll would run).
        static List<string> Run(IEnumerable<AgentAction> actions, double now = 0)
        {
            var ran = new List<string>();
            foreach (var a in actions)
            {
                ran.Add(a.name);
                if (Array.IndexOf(GrokRails.Actions, a.name) >= 0) Assert.IsTrue(GrokRails.Apply(a.name, a.args, now), $"{a}");
            }
            return ran;
        }

        static void Reply(JObject reply, double now = 0) => Run(ReplyActions(reply), now);

        // ---------------- JSON → models ----------------

        [Test]
        public void JobPayloadsParse()
        {
            var a = Actions(Fixtures.JobHappy);
            var started = GrokRailPayloads.JobStarted(a[0].args);
            Assert.AreEqual("676f9a201365", started.run_id);
            CollectionAssert.AreEqual(new[] { "survey", "part", "safety", "rules", "postcard", "packet", "checkout" }, started.steps);
            var part = GrokRailPayloads.JobStep(a[4].args);
            Assert.AreEqual((1, "part", "done"), (part.i, part.name, part.status));
            Assert.IsNull(part.cost_usd, "a step with no cost reads null");
            Assert.AreEqual(0.0, part.seconds);
            var done = GrokRailPayloads.JobDone(a.Last().args);
            Assert.AreEqual((899.0, 749.0), (done.total_usd.Value, done.after_rebates_usd.Value));
            Assert.AreEqual(0.361, done.cost_usd.Value, 1e-9);
            Assert.AreEqual("https://files-cdn.x.ai/t/f.pdf", done.packet_url);
        }

        [Test]
        public void PollBodyParsesStepsActionsAndNext()
        {
            var body = GrokRailPayloads.Poll(Fixtures.JobLivePoll);
            Assert.AreEqual(("0936311b5283", "done", 13), (body.run_id, body.status, body.next));
            Assert.AreEqual(6, body.steps.Count);
            Assert.AreEqual(13, body.actions.Count);
            Assert.AreEqual("skipped", body.steps[2].status);
            Assert.AreEqual("start_checkout", body.actions[11].name);
            Assert.AreEqual(0, body.actions[11].Int("seller_index"));
            Assert.IsNull(GrokRailPayloads.Poll("not json"));
            Assert.IsNull(GrokRailPayloads.Poll(""));
        }

        [Test]
        public void CoachPayloadsParse()
        {
            var led = Replies(Fixtures.CoachLed);
            var started = GrokRailPayloads.CoachStarted(ReplyActions(led[0])[0].args);
            Assert.AreEqual(("led_strip", "template", 5), (started.job, started.source, started.steps.Count));
            Assert.AreEqual("Coaching from the manual or our own checklist. Camera checks can miss things.", started.label_note);
            Assert.IsNull(started.steps[2].page);
            var drill = GrokRailPayloads.CoachStep(ReplyActions(led[2])[0].args);
            Assert.AreEqual((2, 5, "drill"), (drill.i, drill.of, drill.tool));
            CollectionAssert.AreEqual(new[] { "Is there a round hole in the cabinet's side panel?" }, drill.checks);
            var stop = GrokRailPayloads.CoachStop(ReplyActions(led[3])[1].args);
            Assert.AreEqual("outlet", stop.kind);
            CollectionAssert.AreEqual(new[] { 0.045f, 0.29f, 0.092f, 0.374f }, stop.box);
            CollectionAssert.AreEqual(new[] { 0.127f, 0.1f }, stop.drill_px);
            var faucet = Replies(Fixtures.CoachFaucet);
            var check = GrokRailPayloads.CoachCheck(ReplyActions(faucet[1])[0].args);
            Assert.AreEqual(("0221", "not_yet"), (check.frame_id, check.verdict));
            Assert.AreEqual(("yes", "no", false), (check.results[0].answer, check.results[0].expect, check.results[0].ok));
            CollectionAssert.AreEqual(new[] { 0.05f, 0.52f, 0.15f, 0.7f }, check.results[0].box);
            Assert.IsNull(GrokRailPayloads.CoachCheck(ReplyActions(faucet[3])[0].args).results[0].box, "box null stays null");
            var done = GrokRailPayloads.CoachDone(ReplyActions(faucet.Last())[0].args);
            Assert.AreEqual((1, 5), (done.@checked, done.overridden));
        }

        [Test]
        public void ParsingIsLenient()
        {
            var step = GrokRailPayloads.CoachStep(JObject.Parse(
                @"{""coach_id"":""c"",""i"":""2"",""of"":8.0,""page"":""13"",""checks"":[{""id"":""c1"",""question"":""Is it level?""},""Is a screw visible?"",null,7]}"));
            Assert.AreEqual((2, 8, 13), (step.i, step.of, step.page.Value));
            CollectionAssert.AreEqual(new[] { "Is it level?", "Is a screw visible?" }, step.checks, "object checks (GET /coach/{id}) and strings");
            Assert.IsNull(GrokRailPayloads.Box(JArray.Parse("[0.1, 0.2, 0.3]")), "three numbers aren't a box");
            Assert.IsNull(GrokRailPayloads.Box(JArray.Parse(@"[0.1, ""x"", 0.3, 0.4]")));
            Assert.IsNull(GrokRailPayloads.Point(JValue.CreateNull()));
            var s = GrokRailPayloads.JobStep(JObject.Parse(@"{""name"":""rules""}"));
            Assert.AreEqual(-1, s.i, "a missing index reads -1 (the rail then matches by name)");
            Assert.IsNull(GrokRailPayloads.JobStarted(null));
        }

        // ---------------- the job rail ----------------

        [Test]
        public void HappyRunTurnsEveryDotGreenAsStepsLandOutOfOrder()
        {
            var actions = Actions(Fixtures.JobHappy);
            Assert.IsTrue(GrokRails.Apply("job_started", actions[0].args, 10));
            var job = GrokRails.Job;
            Assert.AreEqual(7, job.Dots.Count);
            Assert.IsTrue(job.Dots.All(d => d.Status == DotStatus.Pending));
            Assert.AreEqual(DotStatus.Active, job.Display(0), "the survey works first");
            Run(actions.Skip(1).Take(6), 11);   // survey + part (and their own actions)
            Assert.AreEqual(new[] { DotStatus.Done, DotStatus.Done, DotStatus.Active, DotStatus.Active, DotStatus.Active, DotStatus.Pending, DotStatus.Pending },
                Enumerable.Range(0, 7).Select(job.Display).ToArray(), "safety, rules and postcard work in parallel");
            Assert.AreEqual("Picked the LG 12,000 BTU ductless mini-split indoor unit.", job.Caption);
            // rules (i=3) lands after postcard (i=4): each dot by its own index.
            var ran = Run(actions.Skip(7), 12);
            CollectionAssert.AreEqual(actions.Skip(7).Select(a => a.name), ran, "every action in the page's order");
            Assert.IsTrue(job.Dots.All(d => d.Status == DotStatus.Done));
            Assert.IsTrue(job.Finished);
            Assert.AreEqual("done", job.Status);
            Assert.AreEqual("Pay panel's open. Hold to pay when you're ready.", job.Caption);
            Assert.AreEqual(6, job.CaptionStep);
            Assert.AreEqual("✓ Done · $899.00 · $749.00 after rebates", GrokRailText.JobSummary(job.Done, job));
            Assert.AreEqual("Hold Pay to buy · Packet ready · AI cost $0.36", GrokRailText.JobDetail(job.Done, job));
            Assert.AreEqual("7 of 7", GrokRailText.JobCount(job));
            Assert.AreEqual(ColorRole.Success, GrokRailTones.Summary(job.Status));
            Assert.IsFalse(GrokRails.Poll.Active, "job_done stops polling");
        }

        // ---------------- the job strip (declutter M2, DC1): the run as the status line's progress ----------------

        [Test]
        public void JobStripShowsOneGlyphPerStepInOrder()
        {
            var actions = Actions(Fixtures.JobHappy);
            Run(actions.Take(1), 10);
            var job = GrokRails.Job;
            Assert.AreEqual("• · · · · · ·", GrokRailText.JobStrip(job), "the survey works first; nothing else reached yet");
            int first = actions.FindIndex(a => a.name == "job_step");
            Run(actions.Skip(1).Take(first), 11);   // up to its first job_step
            string strip = GrokRailText.JobStrip(job);
            Assert.AreEqual("✓ • · · · · ·", strip, "a ✓, then the working step and those not reached yet");
            Assert.AreEqual(job.Dots.Count, strip.Split(' ').Length, "one glyph per dot");
            var coloured = GrokRailText.JobStrip(job, r => r == ColorRole.Success ? "40D185" : "AAAAAA");
            StringAssert.StartsWith("<#40D185>✓</color> <#AAAAAA>•</color>", coloured, "each glyph in its tone's colour");
            Assert.AreEqual(strip, GrokRailText.StripTags(coloured), "with the tags stripped: the same glyphs");
            string line1 = GrokRailText.JobStatus(job);
            Assert.AreEqual("Do the whole job · 1 of 7", GrokRailText.JobLine(job));
            Assert.LessOrEqual(GrokRailText.JobLine(job).Length, 48, "line 1 ≤ 48 characters, then the strip");
            Assert.AreEqual($"{GrokRailText.JobLine(job)}  {strip}", line1);
            Assert.AreEqual("1 problem found.", GrokRailText.JobStatusDetail(job), "line 2: the newest spoken result");

            Run(actions.Skip(first + 1), 12);
            Assert.AreEqual("✓ ✓ ✓ ✓ ✓ ✓ ✓", GrokRailText.JobStrip(job), "JobHappy done: 7 × ✓");
            Assert.AreEqual("✓ Done · $899.00 · $749.00 after rebates", GrokRailText.JobStatus(job), "job_done: the summary on line 1");
            Assert.AreEqual("Hold Pay to buy · Packet ready · AI cost $0.36", GrokRailText.JobStatusDetail(job), "… and where things are under it");
        }

        [Test]
        public void JobStripEndsInACrossWhenTheRecallStopsIt()
        {
            Run(Actions(Fixtures.JobRecall));
            var strip = GrokRailText.JobStrip(GrokRails.Job);
            StringAssert.EndsWith("✗", strip);
            Assert.AreEqual(ColorRole.Danger, GrokRailText.StripTone(DotStatus.Stopped));
            StringAssert.StartsWith("✗ Stopped before checkout", GrokRailText.JobStatus(GrokRails.Job));
        }

        [Test]
        public void StripGlyphsSayTheStateByShape()
        {
            var glyphs = new[] { DotStatus.Done, DotStatus.Said, DotStatus.Skipped, DotStatus.Stopped, DotStatus.Active, DotStatus.Pending }
                .Select(GrokRailText.StripGlyph).ToArray();
            CollectionAssert.AreEqual(new[] { "✓", "✓", "–", "✗", "•", "·" }, glyphs);
            Assert.AreEqual(ColorRole.TextSecondary, GrokRailText.StripTone(DotStatus.Pending), "a step not reached yet reads as text, not a separator");
            Assert.AreEqual(GrokRailTones.Info, GrokRailText.StripTone(DotStatus.Active));
            Assert.AreEqual("", GrokRailText.JobStrip(null));
            Assert.AreEqual("", GrokRailText.JobStatus(null));
            Assert.AreEqual("a b", GrokRailText.StripTags("<#FFFFFF>a</color> <b>b</b>"));
        }

        [Test]
        public void RecalledPartStopsRedBeforeThePayPanel()
        {
            var actions = Actions(Fixtures.JobRecall);
            Run(actions);
            var job = GrokRails.Job;
            Assert.AreEqual("stopped", job.Status);
            var last = job.Dots.Last();
            Assert.AreEqual(("checkout", DotStatus.Stopped), (last.Name, last.Status));
            Assert.AreEqual("This model is recalled; I stopped before checkout.", last.Spoken);
            Assert.AreEqual("✗", GrokRailTones.Glyph(last.Status));
            Assert.AreEqual(ColorRole.Danger, GrokRailTones.Dot(last.Status));
            Assert.AreEqual("✗ Stopped before checkout: recalled · $899.00", GrokRailText.JobSummary(job.Done, job));
            Assert.AreEqual(ColorRole.Danger, GrokRailTones.Summary(job.Status));
            StringAssert.DoesNotContain("Hold Pay", GrokRailText.JobDetail(job.Done, job), "nothing to pay on a recall");
            Assert.IsFalse(actions.Any(a => a.name == "start_checkout"), "the backend never opens the pay panel on a recall");
        }

        [Test]
        public void SkippedStepsGoGreyWithTheirNote()
        {
            var body = GrokRailPayloads.Poll(Fixtures.JobLivePoll);
            Run(body.actions);
            var job = GrokRails.Job;
            var rules = job.Dots.First(d => d.Name == "rules");
            Assert.AreEqual(DotStatus.Skipped, rules.Status);
            Assert.AreEqual("No permit rules on file for this kind of job.", rules.Spoken);
            Assert.AreEqual(("–", ColorRole.TextDisabled), (GrokRailTones.Glyph(rules.Status), GrokRailTones.Dot(rules.Status)));
            Assert.AreEqual("✓ Done · $2.74", GrokRailText.JobSummary(job.Done, job));
            Assert.AreEqual("Hold Pay to buy · Packet ready", GrokRailText.JobDetail(job.Done, job), "cost 0 isn't shown");
        }

        [Test]
        public void OfflineRunWithNothingCachedSkipsEveryStep()
        {
            // The OFFLINE server, kitchen, no cached survey: POST /agent/command "do the whole job", then ?after=1.
            Run(Actions(Fixtures.JobKitchenOfflineStart));
            var page = GrokRailPayloads.Poll(Fixtures.JobKitchenOfflinePage);
            GrokRails.Poll.Sent();
            Run(GrokRails.Polled(page, 1));
            var job = GrokRails.Job;
            Assert.IsTrue(job.Dots.All(d => d.Status == DotStatus.Skipped));
            Assert.AreEqual("No survey: it needs the uplink.", job.Dots[0].Spoken);
            Assert.AreEqual("– Nothing done · every step was skipped", GrokRailText.JobSummary(job.Done, job));
            Assert.AreEqual("", GrokRailText.JobDetail(job.Done, job));
            Assert.AreEqual(ColorRole.TextDisabled, GrokRailTones.Summary(job));
            Assert.AreEqual((false, "done", 9), (GrokRails.Poll.Active, GrokRails.Poll.StopReason, GrokRails.Poll.After));
        }

        [Test]
        public void SameRunStartedAgainKeepsTheRail()
        {
            var a = Actions(Fixtures.JobHappy);
            GrokRails.Apply("job_started", a[0].args, 0);
            var job = GrokRails.Job;
            GrokRails.Apply("job_step", a[1].args, 1);
            Assert.IsTrue(GrokRails.Apply("job_started", a[0].args, 2), "a poll from after=0 repeats job_started");
            Assert.AreSame(job, GrokRails.Job);
            Assert.AreEqual(DotStatus.Done, job.Dots[0].Status);
            Assert.IsFalse(GrokRails.Apply("job_step", JObject.Parse(@"{""i"":9,""name"":""nope"",""status"":""done""}"), 3), "unknown step");
            Assert.IsFalse(GrokRails.Apply("job_done", JObject.Parse(@"{""run_id"":""other"",""status"":""done""}"), 3), "another run's done");
        }

        [Test]
        public void JobStepWithoutARailIsRefused()
        {
            Assert.IsFalse(GrokRails.Apply("job_step", Actions(Fixtures.JobHappy)[1].args, 0));
            Assert.IsFalse(GrokRails.Apply("job_done", Actions(Fixtures.JobHappy).Last().args, 0));
            Assert.IsFalse(GrokRails.Apply("job_started", JObject.Parse("{}"), 0), "no run id");
        }

        [Test]
        public void PollStepsReconcileMissedJobSteps()
        {
            var a = Actions(Fixtures.JobHappy);
            GrokRails.Apply("job_started", a[0].args, 0);
            var steps = new List<JobStepArgs>
            {
                new JobStepArgs { i = 0, name = "survey", status = "done", spoken = "1 problem found." },
                new JobStepArgs { i = 1, name = "part", status = "skipped", spoken = "Nothing in the survey needs a part." },
                new JobStepArgs { i = 2, name = "safety", status = "pending" },
            };
            Assert.AreEqual(2, GrokRails.Job.Reconcile(steps));
            Assert.AreEqual(DotStatus.Skipped, GrokRails.Job.Dots[1].Status);
            Assert.AreEqual(DotStatus.Pending, GrokRails.Job.Dots[2].Status, "pending never overwrites");
            Assert.AreEqual(0, GrokRails.Job.Reconcile(steps), "idempotent");
        }

        [TestCase(-1, 0f, 1f)]
        [TestCase(10, 5f, 1f)]
        [TestCase(10, 18f, 1f)]
        [TestCase(10, 18.3f, 0.5f)]
        [TestCase(10, 18.6f, 0f)]
        [TestCase(10, 30f, 0f)]
        public void TheRailFadesAfterTheSummary(double doneAt, float now, float alpha) =>
            Assert.AreEqual(alpha, JobRailTiming.Alpha(now, doneAt, 8f, 0.6f), 1e-4f);

        // ---------------- polling ----------------

        [Test]
        public void PollingStartsAfterJobStartedAndFollowsNext()
        {
            var a = Actions(Fixtures.JobHappy);
            GrokRails.Apply("job_started", a[0].args, 100);
            var p = GrokRails.Poll;
            Assert.IsTrue(p.Active);
            Assert.AreEqual(1, p.After, "job_started is the run's action 0 and already arrived");
            Assert.AreEqual("/job/run/676f9a201365?after=1", p.Path);
            Assert.IsTrue(p.Due(100));
            p.Sent();
            Assert.IsFalse(p.Due(100.5), "one request at a time");
            var page = new JobPollBody { run_id = "676f9a201365", status = "running", next = 4, actions = a.Skip(1).Take(3).ToList() };
            var got = GrokRails.Polled(page, 101);
            CollectionAssert.AreEqual(new[] { "job_step", "survey_started", "show_survey" }, got.Select(x => x.name));
            Assert.AreEqual(4, p.After);
            Assert.AreEqual("/job/run/676f9a201365?after=4", p.Path);
            Assert.IsFalse(p.Due(101.9));
            Assert.IsTrue(p.Due(102), "every second");
            p.Sent();
            Assert.AreEqual(0, GrokRails.Polled(new JobPollBody { run_id = "676f9a201365", status = "running", next = 4 }, 102).Count);
            Assert.AreEqual(4, p.After, "an empty page keeps the cursor");
            p.Sent();
            GrokRails.Polled(new JobPollBody { run_id = "676f9a201365", status = "running", next = 2 }, 103);
            Assert.AreEqual(4, p.After, "the cursor never steps back");
        }

        [Test]
        public void PagedRunReplaysEveryActionOnceInOrder()
        {
            var a = Actions(Fixtures.JobHappy);
            GrokRails.Apply("job_started", a[0].args, 0);
            var ran = new List<string>();
            double now = 0;
            foreach (var (from, to) in new[] { (1, 4), (4, 4), (4, 11), (11, a.Count) })
            {
                Assert.IsTrue(GrokRails.Poll.Due(now), $"due at {now}");
                Assert.AreEqual(from, GrokRails.Poll.After);
                GrokRails.Poll.Sent();
                bool last = to == a.Count;
                var page = new JobPollBody { run_id = GrokRails.Job.RunId, status = last ? "done" : "running", next = to, actions = a.Skip(from).Take(to - from).ToList() };
                ran.AddRange(Run(GrokRails.Polled(page, now), now));
                now += 1;
            }
            CollectionAssert.AreEqual(a.Skip(1).Select(x => x.name), ran);
            Assert.IsFalse(GrokRails.Poll.Active);
            Assert.AreEqual("done", GrokRails.Poll.StopReason);
            Assert.IsTrue(GrokRails.Job.Finished);
        }

        [Test]
        public void PollingBacksOffThenGivesUp()
        {
            var p = new JobPollState { IntervalSeconds = 1f, MaxBackoffSeconds = 8f, MaxFailures = 5 };
            p.Start("abc", 0);
            double now = 0;
            foreach (var wait in new[] { 2.0, 4.0, 8.0, 8.0 })
            {
                p.Sent();
                p.Failed(0, now);
                Assert.IsTrue(p.Active);
                Assert.AreEqual(now + wait, p.NextAt, 1e-9, $"backoff after {p.Failures}");
                now = p.NextAt;
            }
            p.Sent();
            p.Failed(503, now);
            Assert.IsFalse(p.Active);
            Assert.AreEqual("unreachable", p.StopReason);
            p.Start("abc", 0);
            p.Sent();
            p.Failed(404, 0);
            Assert.AreEqual((false, "unknown run"), (p.Active, p.StopReason), "runs live in memory: a restarted server forgot it");
        }

        [Test]
        public void PollingStopsOnStoppedTimeoutOrAnotherRun()
        {
            var p = new JobPollState { MaxRunSeconds = 60f };
            p.Start("abc", 0);
            p.Sent();
            Assert.AreEqual(0, p.Received(new JobPollBody { run_id = "zzz", status = "done", next = 9 }, 1).Count, "another run's page");
            Assert.IsTrue(p.Active);
            Assert.AreEqual(1, p.After);
            p.Sent();
            p.Received(new JobPollBody { run_id = "abc", status = "running", next = 3 }, 61);
            Assert.AreEqual((false, "timeout"), (p.Active, p.StopReason));
            p.Start("abc", 0);
            p.Sent();
            p.Received(new JobPollBody { run_id = "abc", status = "stopped", next = 17 }, 5);
            Assert.AreEqual((false, "stopped"), (p.Active, p.StopReason));
            p.Start("abc", 0);
            p.Sent();
            p.Received(null, 1);
            Assert.AreEqual(1, p.Failures, "an unreadable body counts as a failure");
            p.Abandon(2);
            Assert.IsFalse(p.InFlight);
            Assert.IsFalse(new JobPollState().Due(0), "never started");
        }

        // ---------------- the install coach ----------------

        [Test]
        public void CoachStartedDrawsOneDotPerStepWithTheNoteVerbatim()
        {
            var led = Replies(Fixtures.CoachLed);
            Reply(led[0]);
            var m = GrokRails.Coach;
            Assert.AreEqual(5, m.DotCount);
            Assert.AreEqual(0, m.Current);
            Assert.AreEqual("Install · under-cabinet LED strip", GrokRailText.CoachTitle(m));
            Assert.AreEqual("Step 1 of 5", GrokRailText.CoachStepLine(m));
            Assert.AreEqual("Coaching from the manual or our own checklist. Camera checks can miss things.", m.LabelNote);
            Assert.AreEqual(new[] { DotStatus.Active, DotStatus.Pending, DotStatus.Pending, DotStatus.Pending, DotStatus.Pending },
                Enumerable.Range(0, 5).Select(m.Dot).ToArray());
            Assert.AreEqual("• Is the underside of the cabinet clear of objects?", GrokRailText.Chip(m.Chips[0]), "grey chip before a check");
            Assert.AreEqual(ColorRole.TextSecondary, GrokRailTones.Chip(m.Chips[0].State));
            Assert.AreEqual("", GrokRailText.ManualLine(m.Step), "a template step has no page");
            Assert.IsTrue(GrokState.CoachActive);
            Assert.IsFalse(GrokState.DrillActive);
            Assert.IsNull(GrokState.DrillPx);
        }

        [Test]
        public void DrillStepPutsTheCrosshairInTheContext()
        {
            var led = Replies(Fixtures.CoachLed);
            for (int k = 0; k < 3; k++) Reply(led[k]);
            var m = GrokRails.Coach;
            Assert.IsTrue(m.IsDrill);
            Assert.AreEqual("Step 3 of 5 · drill", GrokRailText.CoachStepLine(m));
            Assert.IsTrue(GrokState.DrillActive);
            CollectionAssert.AreEqual(new[] { 0.5f, 0.5f }, GrokState.DrillPx, "the frame centre until the crosshair lands");
            Assert.AreEqual(new[] { DotStatus.Said, DotStatus.Said, DotStatus.Active }, Enumerable.Range(0, 3).Select(m.Dot).ToArray(),
                "steps left with \"next\" were the user's word");
        }

        [Test]
        public void StopPausesTheRailUntilTheCoachMoves()
        {
            var led = Replies(Fixtures.CoachLed);
            for (int k = 0; k < 4; k++) Reply(led[k]);   // … drill step, "check it" above the outlet
            var m = GrokRails.Coach;
            Assert.IsTrue(m.Paused);
            Assert.AreEqual(2, m.PausedStep);
            Assert.AreEqual(DotStatus.Stopped, m.Dot(2));
            Assert.AreEqual("✗ Stop · don't drill here: an outlet below", GrokRailText.VerdictLine(m));
            Assert.AreEqual("Step 3 of 5 · paused", GrokRailText.CoachStepLine(m));
            Assert.AreEqual("Don't drill here · an outlet below", GrokRailText.StopCard(m.Stop));
            Assert.AreEqual(ChipState.NotYet, m.Chips[0].State, "no hole yet");
            Reply(led[4]);   // checked again 20 % to the side: not yet, no hazard
            Assert.IsTrue(m.Paused, "a re-check doesn't lift the pause; only moving on does");
            Assert.AreEqual("not_yet", m.LastCheck.verdict);
            Reply(led[5]);   // "next"
            Assert.IsFalse(m.Paused);
            Assert.IsNull(m.Stop);
            Assert.AreEqual((3, DotStatus.Said, DotStatus.Active), (m.Current, m.Dot(2), m.Dot(3)));
            Assert.IsFalse(GrokState.DrillActive);
            Assert.IsNull(GrokState.DrillPx, "no drill_px off a drill step");
            Reply(led[6]);   // "back"
            Assert.AreEqual(2, m.Current);
            Assert.AreEqual("said", m.DoneHow[2], "back keeps the first record");
            Assert.AreEqual(DotStatus.Active, m.Dot(2), "the step you're on shows as current");
            Reply(led[7]);   // "repeat that"
            Assert.AreEqual(2, m.Current);
            Assert.IsTrue(GrokState.DrillActive);
        }

        [Test]
        public void RepeatOnTheStoppedStepKeepsThePause()
        {
            var led = Replies(Fixtures.CoachLed);
            for (int k = 0; k < 4; k++) Reply(led[k]);
            Reply(led[7]);   // "repeat that": the same step again
            Assert.IsTrue(GrokRails.Coach.Paused);
            Assert.AreEqual(DotStatus.Stopped, GrokRails.Coach.Dot(2));
        }

        [Test]
        public void ChecksTurnChipsAmberBlueGreenAndTheSummaryCountsCameraVsWord()
        {
            var f = Replies(Fixtures.CoachFaucet);
            Reply(f[0]);
            Reply(f[1]);   // not yet (frame 0221)
            var m = GrokRails.Coach;
            Assert.AreEqual(ChipState.NotYet, m.Chips[0].State);
            Assert.AreEqual(ColorRole.Warning, GrokRailTones.Chip(ChipState.NotYet));
            Assert.AreEqual("! Are any objects standing within a hand's width of the sink rim? · black shaker bottle and soap bottles near sink", GrokRailText.Chip(m.Chips[0]));
            Assert.AreEqual("! Not yet · black shaker bottle and soap bottles near sink", GrokRailText.VerdictLine(m));
            CollectionAssert.AreEqual(new[] { 0.05f, 0.52f, 0.15f, 0.7f }, m.Chips[0].Box);
            Assert.AreEqual("0221", GrokRails.FrameFor(m.LastCheck));
            Reply(f[2]);   // "next"
            Reply(f[3]);   // can't see (no frame id on the agent path)
            Assert.AreEqual(ChipState.CantSee, m.Chips[0].State);
            Assert.AreEqual(GrokRailTones.Info, GrokRailTones.Chip(ChipState.CantSee));
            Assert.AreEqual("? Can't see it from here · Look under the sink at the valves", GrokRailText.VerdictLine(m));
            GrokState.LastFrameId = "0141";
            Assert.AreEqual("0141", GrokRails.FrameFor(m.LastCheck), "no frame_id: the frame G1 last sent");
            for (int k = 4; k < f.Count - 1; k++) Reply(f[k]);   // three "next", then passed on step 5 (+ its coach_step)
            Assert.AreEqual(5, m.Current);
            Assert.AreEqual(DotStatus.Done, m.Dot(4), "passed: checked by camera");
            Assert.AreEqual(ColorRole.Success, GrokRailTones.Dot(m.Dot(4)));
            Reply(f.Last());   // "next" on the last step → coach_done
            Assert.IsTrue(m.Finished);
            Assert.AreEqual((1, 5), (m.CheckedByCamera, m.OnYourWord), "the same count the server gives");
            Assert.AreEqual((1, 5), (m.Done.@checked, m.Done.overridden));
            Assert.AreEqual("✓ All 6 steps · 1 checked by camera · 5 on your word", GrokRailText.CoachSummary(m.Done, m.DotCount));
            Assert.AreEqual("6 of 6", GrokRailText.CoachStepLine(m));
            Assert.IsFalse(GrokState.CoachActive);
            Assert.IsFalse(GrokRails.Apply("coach_check", ReplyActions(f[1])[0].args, 9), "a finished coach takes no checks");
        }

        [Test]
        public void ManualStepShowsPageQuoteAndOpensTheManual()
        {
            var step = GrokRailPayloads.CoachStep(JObject.Parse(
                @"{""coach_id"":""6fbf9576115f"",""i"":4,""of"":8,""say"":""Drill 1/8” pilot holes."",""page"":13,""quote"":""D. Check the level again and drill 1/8” pilot holes."",""pdf_url"":""/parts/midea-maw08u1qwt-8d6d08/manual.pdf#page=13"",""tool"":""drill"",""checks"":[""Are the cotter pins installed in the main support pins?""]}"));
            Assert.AreEqual("Manual p. 13 · “D. Check the level again and drill 1/8” pilot holes.”", GrokRailText.ManualLine(step));
            Assert.AreEqual("http://192.168.1.5:8003/parts/midea-maw08u1qwt-8d6d08/manual.pdf#page=13", GrokRailText.Absolute("http://192.168.1.5:8003/", step.pdf_url));
            Assert.AreEqual("https://x.test/m.pdf", GrokRailText.Absolute("http://h:1", "https://x.test/m.pdf"));
            Assert.IsTrue(GrokRails.CoachStep(step, 0), "a coach this headset never saw start (reconnect) still gets a rail");
            Assert.AreEqual(8, GrokRails.Coach.DotCount);
            Assert.AreEqual("Install coach", GrokRailText.CoachTitle(GrokRails.Coach));
            Assert.IsTrue(GrokRails.Coach.IsDrill);
        }

        [Test]
        public void ManualCoachShowsPagesQuotesAndTheDrillStep()
        {
            var r = Replies(Fixtures.CoachMidea);
            Reply(r[0]);
            var m = GrokRails.Coach;
            Assert.AreEqual(("manual", 8), (m.Source, m.DotCount));
            Assert.AreEqual("Install · Midea MAW08U1QWT", GrokRailText.CoachTitle(m));
            StringAssert.StartsWith("Manual p. 11 · “A. Measure the inside opening of your window sill", GrokRailText.ManualLine(m.Step));
            StringAssert.EndsWith("26-36” windows.”", GrokRailText.ManualLine(m.Step), "the quote verbatim, in quotation marks");
            Assert.AreEqual("/parts/midea-maw08u1qwt-8d6d08/manual.pdf#page=11", m.Step.pdf_url);
            Assert.AreEqual(2, m.Chips.Count);
            for (int k = 1; k < r.Count; k++) Reply(r[k]);
            Assert.AreEqual(4, m.Current);
            Assert.IsTrue(m.IsDrill);
            Assert.AreEqual("Step 5 of 8 · drill", GrokRailText.CoachStepLine(m));
            StringAssert.StartsWith("Manual p. 13 · “D. Check the level again", GrokRailText.ManualLine(m.Step));
            Assert.AreEqual("http://10.0.0.2:8003/parts/midea-maw08u1qwt-8d6d08/manual.pdf#page=13", GrokRailText.Absolute("http://10.0.0.2:8003", m.Step.pdf_url));
            Assert.AreEqual(4, m.OnYourWord);
        }

        [Test]
        public void LateCheckForAnotherStepIsIgnored()
        {
            var f = Replies(Fixtures.CoachFaucet);
            Reply(f[0]);
            Reply(f[2]);   // on to step 2
            Assert.IsFalse(GrokRails.Apply("coach_check", ReplyActions(f[1])[0].args, 1), "the step-1 answer arrives late");
            Assert.AreEqual(ChipState.Unchecked, GrokRails.Coach.Chips[0].State);
            Assert.IsFalse(GrokRails.Apply("coach_stop", JObject.Parse(@"{""coach_id"":""other"",""i"":1,""kind"":""outlet""}"), 1), "another coach's stop");
        }

        // ---------------- frame rays ----------------

        static SceneCameraJson Cam0141() => JsonConvert.DeserializeObject<SceneCameraJson>(Fixtures.Camera0141);

        [Test]
        public void PinholeFrameMatchesSceneCameras()
        {
            var cam = Cam0141();
            var f = CoachFrameRays.FromSceneCamera(cam);
            Assert.IsTrue(f.IsValid);
            foreach (var (x, y) in new[] { (0.5f, 0.5f), (0.127f, 0.1f), (0.9f, 0.8f), (0.05f, 0.52f) })
            {
                var a = SceneCameras.PixelRay(cam, x, y);
                var b = CoachFrameRays.PixelRay(f, x, y);
                Assert.Less((a.origin - b.origin).magnitude, 1e-5f);
                Assert.Less((a.direction - b.direction).magnitude, 1e-5f, $"direction at ({x}, {y})");
                var p = b.origin + b.direction * 1.7f;
                Assert.IsTrue(CoachFrameRays.Project(f, p, out var n));
                Assert.IsTrue(SceneCameras.Project(cam, p, out var n2));
                Assert.AreEqual(x, n.x, 1e-4f); Assert.AreEqual(y, n.y, 1e-4f);
                Assert.Less((n - n2).magnitude, 1e-4f, "same projection as SceneCameras");
            }
            Assert.IsFalse(CoachFrameRays.Project(f, f.Position - f.Forward, out _), "behind the camera");
        }

        [Test]
        public void BoxCornersLieOnTheDepthPlaneAndProjectBack()
        {
            var f = CoachFrameRays.FromSceneCamera(Cam0141());
            var box = new[] { 0.045f, 0.29f, 0.092f, 0.374f };   // the outlet coach_stop found
            var corners = CoachFrameRays.BoxCorners(f, box, 1.2f);
            Assert.AreEqual(4, corners.Length);
            var expect = new[] { new Vector2(0.045f, 0.29f), new Vector2(0.092f, 0.29f), new Vector2(0.092f, 0.374f), new Vector2(0.045f, 0.374f) };
            for (int k = 0; k < 4; k++)
            {
                Assert.AreEqual(1.2f, CoachFrameRays.Depth(f, corners[k]), 1e-4f);
                Assert.IsTrue(CoachFrameRays.Project(f, corners[k], out var n));
                Assert.Less((n - expect[k]).magnitude, 1e-4f, $"corner {k}");
            }
            Assert.IsNull(CoachFrameRays.BoxCorners(f, new[] { 0.1f, 0.1f, 0.1f, 0.5f }, 1f), "no area");
            Assert.IsNull(CoachFrameRays.BoxCorners(f, null, 1f));
            Assert.IsNull(CoachFrameRays.BoxCorners(f, box, 0f));
            var swapped = CoachFrameRays.BoxCorners(f, new[] { 0.092f, 0.374f, 0.045f, 0.29f }, 1.2f);
            Assert.Less((swapped[0] - corners[0]).magnitude, 1e-5f, "corners in either order");
        }

        [Test]
        public void DrillPxProjectsTheCrosshairIntoTheSentFrame()
        {
            var cam = Cam0141();
            var f = CoachFrameRays.FromSceneCamera(cam);
            var led = Replies(Fixtures.CoachLed);
            for (int k = 0; k < 3; k++) Reply(led[k]);   // the drill step
            CollectionAssert.AreEqual(new[] { 0.5f, 0.5f }, GrokState.DrillPxIn(f), "no crosshair point yet: the centre");
            var aim = SceneCameras.PixelRay(cam, 0.127f, 0.1f);
            GrokState.DrillPoint = aim.origin + aim.direction * 1.4f;   // the crosshair on the wall above the outlet
            var px = GrokState.DrillPxIn(f);
            Assert.AreEqual(0.127f, px[0], 1e-4f);
            Assert.AreEqual(0.1f, px[1], 1e-4f);
            var off = SceneCameras.PixelRay(cam, 1.4f, -0.2f);
            GrokState.DrillPoint = off.origin + off.direction * 2f;
            CollectionAssert.AreEqual(new[] { 1f, 0f }, GrokState.DrillPxIn(f), "clamped inside the frame (the backend 400s outside 0–1)");
            GrokState.DrillPoint = f.Position - f.Forward * 2f;
            Assert.IsNull(GrokState.DrillPxIn(f), "behind this camera");
            Reply(led[3]);   // coach_check + coach_stop keep the drill step
            Assert.IsTrue(GrokState.DrillActive);
            GrokRails.Clear();
            Assert.IsNull(GrokState.DrillPxIn(f), "no drill step, no drill_px");
        }

        [Test]
        public void ViewFrameLooksAlongForwardAtItsCentre()
        {
            var f = CoachFrameRays.FromView(new Vector3(1f, 1.6f, 0f), Vector3.right, Vector3.up, Vector3.forward, 90f, 1.25f);
            Assert.Less((CoachFrameRays.Direction(f, 0.5f, 0.5f) - Vector3.forward).magnitude, 1e-5f);
            var topLeft = CoachFrameRays.Direction(f, 0f, 0f);
            Assert.Less(topLeft.x, 0f); Assert.Greater(topLeft.y, 0f);
            Assert.AreEqual(45f, Vector3.Angle(Vector3.forward, CoachFrameRays.Direction(f, 0.5f, 0f)), 0.01f, "half the vertical fov to the top edge");
            Assert.IsTrue(CoachFrameRays.Project(f, new Vector3(1f, 1.6f, 3f), out var n));
            Assert.Less((n - new Vector2(0.5f, 0.5f)).magnitude, 1e-5f);
        }

        [Test]
        public void CrosshairHoldsWhileLookingAtTheCard()
        {
            var toCard = Dir(30f, -24f);   // the card: lower left
            Assert.IsTrue(CoachFrameRays.ShouldHoldCrosshair(toCard, toCard, 22f));
            Assert.IsTrue(CoachFrameRays.ShouldHoldCrosshair(Dir(25f, -15f), toCard, 22f));
            Assert.IsFalse(CoachFrameRays.ShouldHoldCrosshair(Vector3.forward, toCard, 22f), "looking at the wall: it follows");
            Assert.IsFalse(CoachFrameRays.ShouldHoldCrosshair(Vector3.zero, toCard, 22f));
        }

        /// A unit direction `down` degrees below the horizon and `yaw` degrees to the right (no engine calls).
        static Vector3 Dir(float down, float yaw)
        {
            float p = down * Mathf.Deg2Rad, y = yaw * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(y) * Mathf.Cos(p), -Mathf.Sin(p), Mathf.Cos(y) * Mathf.Cos(p));
        }

        // ---------------- colours ----------------

        [Test]
        public void TonesAreColourRolesWithGlyphs()
        {
            Assert.IsTrue(Enum.IsDefined(typeof(ColorRole), GrokRailTones.Info), "Info (after UX D3) or the accent before it");
            Assert.That(GrokRailTones.Info.ToString(), Is.EqualTo("Info").Or.EqualTo("Accent"));
            Assert.AreEqual(ColorRole.Success, GrokRailTones.Dot(DotStatus.Done));
            Assert.AreEqual(ColorRole.Success, GrokRailTones.Dot(DotStatus.Said));
            Assert.AreEqual(ColorRole.TextDisabled, GrokRailTones.Dot(DotStatus.Skipped));
            Assert.AreEqual(ColorRole.Danger, GrokRailTones.Dot(DotStatus.Stopped));
            Assert.AreEqual(GrokRailTones.Info, GrokRailTones.Dot(DotStatus.Active));
            Assert.AreEqual(ColorRole.Success, GrokRailTones.Chip(ChipState.Ok));
            Assert.AreEqual(ColorRole.Warning, GrokRailTones.Chip(ChipState.NotYet));
            Assert.AreEqual(GrokRailTones.Info, GrokRailTones.Chip(ChipState.CantSee));
            // Never colour alone: every settled state has a glyph.
            foreach (var s in new[] { DotStatus.Done, DotStatus.Said, DotStatus.Skipped, DotStatus.Stopped })
                Assert.IsNotEmpty(GrokRailTones.Glyph(s), s.ToString());
            foreach (ChipState s in Enum.GetValues(typeof(ChipState))) Assert.IsNotEmpty(GrokRailTones.ChipGlyph(s), s.ToString());
            Assert.AreEqual("Preview", GrokRailText.StepName("postcard"));
            Assert.AreEqual("Checkout", GrokRailText.StepName("checkout"));
            Assert.AreEqual("Newstep", GrokRailText.StepName("newstep"), "a step the backend adds later still shows");
        }

        [Test]
        public void RailGlyphsAreInTheFontAtlas()
        {
            const string charset = AirTools.Editor.UiAssetsBuilder.Charset;
            var shown = string.Concat(
                GrokRailText.JobSummary(new JobDoneArgs { status = "done", total_usd = 1, after_rebates_usd = 0.5 }),
                GrokRailText.JobSummary(new JobDoneArgs { status = "stopped", total_usd = 1 }),
                GrokRailText.ManualLine(new CoachStepArgs { page = 3, quote = "q" }),
                GrokRailText.StopCard(new CoachStopArgs { kind = "switch" }),
                GrokRailText.CoachSummary(new CoachDoneArgs { @checked = 1, overridden = 2 }, 3),
                "✓–✗!?•· …", GrokRailText.JobTitle, "Check it Back Repeat Next Open manual Close");
            foreach (var ch in shown) Assert.IsTrue(charset.IndexOf(ch) >= 0, $"'{ch}' (U+{(int)ch:X4}) is not in UiAssetsBuilder.Charset");
        }

        // ---------------- never pays ----------------

        /// The backend's coach fast paths (server/agent.py, whole-utterance), copied verbatim.
        static readonly Regex CoachCheckRe = new Regex(
            @"^\W*(?:ok(?:ay)?\W+)?(?:check (?:it|this|that|my work)|(?:all |i'?m )?done)\W*$", RegexOptions.IgnoreCase);
        static readonly Regex CoachMoveRe = new Regex(
            @"^\W*(?:ok(?:ay)?\W+)?(?:next(?: step)?|skip(?: it| this| that)?|i did it|i've done it"
            + @"|(?:go |step )?back(?: up)?(?: (?:one|one step|a step))?|previous(?: step)?"
            + @"|repeat(?: that)?|say (?:that|it) again)\W*$", RegexOptions.IgnoreCase);
        static readonly Regex RunJobRe = new Regex(
            @"\b(?:do|run) the (?:whole|entire) job\b|\brun it end to end\b"
            + @"|^\W*(?:(?:ok(?:ay)?|please|just|quartermaster|go ahead and)\W+)*"
            + @"(?:handle|take care of) (?:it|this|that|everything)\b[^?]*$", RegexOptions.IgnoreCase);

        [Test]
        public void RailButtonsSendOnlyTheCoachWordsAndEachHitsTheFastPath()
        {
            CollectionAssert.AreEqual(new[] { "check it", "back", "repeat that", "next" }, GrokRails.Words);
            Assert.IsTrue(CoachCheckRe.IsMatch(GrokRails.CheckIt));
            foreach (var w in new[] { GrokRails.Next, GrokRails.Back, GrokRails.Repeat })
            {
                Assert.IsTrue(CoachMoveRe.IsMatch(w), w);
                Assert.IsFalse(CoachCheckRe.IsMatch(w), w);
            }
            foreach (var w in GrokRails.Words) Assert.IsFalse(RunJobRe.IsMatch(w), $"\"{w}\" must not start a job");
            var sent = new List<string>();
            GrokRails.Sender = w => { sent.Add(w); return true; };
            foreach (var w in GrokRails.Words) Assert.IsTrue(GrokRails.Send(w));
            Assert.IsFalse(GrokRails.Send("pay"), "only the four words");
            Assert.IsFalse(GrokRails.Send("hold to pay"));
            Assert.IsFalse(GrokRails.Send("do the whole job"));
            CollectionAssert.AreEqual(GrokRails.Words, sent);
            GrokRails.Sender = null;
            Assert.IsFalse(GrokRails.Send(GrokRails.Next), "no sender, nothing sent");
        }

        [Test]
        public void AWholeRunLeavesPayingToTheHoldToPayPanel()
        {
            var a = Actions(Fixtures.JobHappy);
            var ran = Run(a);
            // The only checkout-shaped thing in a run is start_checkout, which this lane doesn't handle: AgentActions hands it
            // to AppCommands.StartCheckout, which only opens the panel (CheckoutTests.StartCheckoutOnlyOpensThePanel).
            CollectionAssert.AreEqual(new[] { "start_checkout" }, ran.Where(n => n.Contains("checkout") || n.Contains("pay")).ToArray());
            Assert.AreEqual(-1, Array.IndexOf(GrokRails.Actions, "start_checkout"));
            Assert.IsFalse(GrokRails.Actions.Any(n => n.Contains("pay") || n.Contains("checkout")));
            Assert.IsTrue(GrokRails.Poll.Path.StartsWith("/job/run/"), "the poller only reads the run");
        }

        static readonly string[] LaneFiles =
        {
            "Assets/AirTools/Runtime/Agent/Grok/GrokState.G4.cs",   // one partial file per lane since the merge
            "Assets/AirTools/Runtime/Agent/Grok/GrokRailPayloads.cs",
            "Assets/AirTools/Runtime/Agent/Grok/JobRunModel.cs",
            "Assets/AirTools/Runtime/Agent/Grok/JobPollState.cs",
            "Assets/AirTools/Runtime/Agent/Grok/CoachSessionModel.cs",
            "Assets/AirTools/Runtime/Agent/Grok/GrokRailText.cs",
            "Assets/AirTools/Runtime/Agent/Grok/CoachFrameRays.cs",
            "Assets/AirTools/Runtime/Agent/Grok/GrokRails.cs",
            "Assets/AirTools/Runtime/Agent/Grok/GrokRailActions.cs",
            "Assets/AirTools/Runtime/Agent/Grok/JobRunPoller.cs",
            "Assets/AirTools/Runtime/Agent/Grok/JobRailView.cs",
            "Assets/AirTools/Runtime/Agent/Grok/CoachRailView.cs",
            "Assets/AirTools/Runtime/Agent/Grok/CoachOverlay.cs",
            "Assets/AirTools/Runtime/Agent/Grok/DrillCrosshair.cs",
            "Assets/AirTools/Runtime/Dev/GrokRailsHarness.cs",
            "Assets/AirTools/Runtime/Dev/GrokRailFixtures.cs",
            "Assets/AirTools/Editor/GrokRailsBuilder.cs",
        };

        [Test]
        public void NothingInLaneG4CanCallCheckout()
        {
            // Source guard (like CheckoutTests.OnlyTheCheckoutPanelCanSendAPayment): no request to /checkout, no call to
            // PartsClient.Checkout, no hold-to-pay, no POST at all; the poller only GETs.
            string[] banned = { "/checkout", ".Checkout(", "HoldToConfirm", ".Simulate(", "OnHoldConfirmed", "PostJson", "PartsClient.Post(",
                                "UnityWebRequest.Post", "kHttpVerbPOST", "UseOfflineReceipt", "FeedbackEvents.Paid(" };
            foreach (var f in LaneFiles)
            {
                Assert.IsTrue(File.Exists(f), $"{f} (run from the project root)");
                var text = File.ReadAllText(f);
                foreach (var b in banned) StringAssert.DoesNotContain(b, text, $"{f} must not pay ({b})");
            }
            var poller = File.ReadAllText("Assets/AirTools/Runtime/Agent/Grok/JobRunPoller.cs");
            StringAssert.Contains("UnityWebRequest.Get(", poller);
        }

        [Test]
        public void EveryLaneActionHasAHandler()
        {
            var names = new List<string>();
            foreach (var m in typeof(GrokRailActions).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                foreach (var attr in m.GetCustomAttributes<AgentActionAttribute>())
                {
                    names.Add(attr.Name);
                    Assert.IsNotNull(Delegate.CreateDelegate(typeof(AgentActions.Handler), m, false), $"{m.Name}: bool (AgentAction, string)");
                }
            CollectionAssert.AreEquivalent(GrokRails.Actions, names);
            CollectionAssert.AreEquivalent(new[] { "job_started", "job_step", "job_done", "coach_started", "coach_step", "coach_check", "coach_stop", "coach_done" }, names);
        }

        // ---------------- recorded payloads ----------------

        internal static class Fixtures
        {
            /// Kitchen camera 0141 (GET /scenes/kitchen/cameras.r1.json), the frame the recorded drill check used.
            internal const string Camera0141 =
                @"{""id"":""0141"",""file"":""frames/0141.jpg"",""thumb"":""thumbs/0141.jpg"",""R"":[[-0.02572421528518842,0.0007535005479782266,-0.9996687936436173],[0.23560875459977526,-0.9718242318895544,-0.006795371087765291],[-0.9715074778425042,-0.2357055250714368,0.02482188443479037]],""t"":[-0.30358149127581147,-0.1886996934814121,-0.4837536695340632],""position"":[-0.4333204032497567,-0.297177598538096,-0.29275554991749425],""fx"":1457.6137201326615,""fy"":1457.6137201326615,""cx"":960.0,""cy"":539.5,""w"":1920,""h"":1079}";

            /// The OFFLINE server (kitchen, nothing cached): the /agent/command reply's actions, and GET ?after=1.
            internal const string JobKitchenOfflineStart =
                @"[{""name"":""job_started"",""args"":{""run_id"":""fd1270f80ebb"",""steps"":[""survey"",""part"",""safety"",""rules"",""postcard"",""packet"",""checkout""]}}]";

            internal const string JobKitchenOfflinePage =
                @"{""run_id"":""fd1270f80ebb"",""status"":""done"",""steps"":[{""i"":0,""name"":""survey"",""status"":""skipped"",""spoken"":""No survey: it needs the uplink."",""seconds"":0.0,""cost_usd"":null},{""i"":1,""name"":""part"",""status"":""skipped"",""spoken"":""Nothing in the survey needs a part."",""seconds"":0.0,""cost_usd"":null},{""i"":2,""name"":""safety"",""status"":""skipped"",""spoken"":""No part, so no recall check."",""seconds"":0.0,""cost_usd"":null},{""i"":3,""name"":""rules"",""status"":""skipped"",""spoken"":""No part, so no rules check."",""seconds"":0.0,""cost_usd"":null},{""i"":4,""name"":""postcard"",""status"":""skipped"",""spoken"":""No part, so no preview."",""seconds"":0.0,""cost_usd"":null},{""i"":5,""name"":""packet"",""status"":""skipped"",""spoken"":""Nothing in the notebook for a packet."",""seconds"":0.0,""cost_usd"":null},{""i"":6,""name"":""checkout"",""status"":""skipped"",""spoken"":""No part, so no checkout."",""seconds"":0.0,""cost_usd"":null}],""actions"":[{""name"":""job_step"",""args"":{""i"":0,""name"":""survey"",""status"":""skipped"",""spoken"":""No survey: it needs the uplink."",""seconds"":0.0,""cost_usd"":null}},{""name"":""job_step"",""args"":{""i"":1,""name"":""part"",""status"":""skipped"",""spoken"":""Nothing in the survey needs a part."",""seconds"":0.0,""cost_usd"":null}},{""name"":""job_step"",""args"":{""i"":2,""name"":""safety"",""status"":""skipped"",""spoken"":""No part, so no recall check."",""seconds"":0.0,""cost_usd"":null}},{""name"":""job_step"",""args"":{""i"":3,""name"":""rules"",""status"":""skipped"",""spoken"":""No part, so no rules check."",""seconds"":0.0,""cost_usd"":null}},{""name"":""job_step"",""args"":{""i"":4,""name"":""postcard"",""status"":""skipped"",""spoken"":""No part, so no preview."",""seconds"":0.0,""cost_usd"":null}},{""name"":""job_step"",""args"":{""i"":5,""name"":""packet"",""status"":""skipped"",""spoken"":""Nothing in the notebook for a packet."",""seconds"":0.0,""cost_usd"":null}},{""name"":""job_step"",""args"":{""i"":6,""name"":""checkout"",""status"":""skipped"",""spoken"":""No part, so no checkout."",""seconds"":0.0,""cost_usd"":null}},{""name"":""job_done"",""args"":{""run_id"":""fd1270f80ebb"",""status"":""done"",""total_usd"":null,""after_rebates_usd"":null,""packet_url"":null,""cost_usd"":0}}],""next"":9}";

            /// The Midea window AC coached from its cached manual (coach.start, then four "next").
            internal const string CoachMidea =
                @"[{""step"":""midea_start"",""spoken"":""Let's install the Midea MAW08U1QWT: 8 steps from its manual. Step 1 of 8: Remove the top foam insert containing the bracket and hardware from the carton. Measure the inside opening of your window sill and use that distance to set the total width of the bracket Extension Arms. To adjust the extension arms, press the Spring Push Pin, slide the Left Extension Arm out and then install the Right Extension Arm."",""actions"":[{""name"":""coach_started"",""args"":{""coach_id"":""3b5a49d54bdb"",""job"":""window_ac"",""label"":""Midea MAW08U1QWT"",""source"":""manual"",""steps"":[{""i"":0,""say"":""Remove the top foam insert containing the bracket and hardware from the carton. Measure the inside opening of your window sill and use that distance to set the total width of the bracket Extension Arms. To adjust the extension arms, press the Spring Push Pin, slide the Left Extension Arm out and then install the Right Extension Arm."",""page"":11},{""i"":1,""say"":""Place bracket on window sill to make sure you have measured and aligned the bracket arms correctly. Once your bracket arms are properly calibrated, apply bracket sealing foam strips to the bottom of the bracket as shown, based on your window type."",""page"":11},{""i"":2,""say"":""Install the Main Support Bracket into the window opening. Ensure that the Horizontal Bracket and Extension Arms are located on the indoor side of the window. Move the Angled Support Arms toward the exterior wall until the feet touch the wall."",""page"":12},{""i"":3,""say"":""Place the level on the bracket and adjust the Support Arms so that it is level or tilted 1/4 bubble downward and towards the outside. Insert the Main Support Pin through the holes in the Main Support and Angled Support Arm."",""page"":12},{""i"":4,""say"":""Check the level again and ensure the bracket feels secure. After making any necessary adjustments, insert the cotter pins into the Main Support Pins. Secure the bracket to the windowsill by drilling 1/8” pilot holes and installing the 1/2” Type A screws or 1” Type A screws as shown."",""page"":13},{""i"":5,""say"":""Set the air conditioner on top of the support bracket. Ensure the grooves on the bottom of the air conditioner align with the Main Supports. Using a level, check for proper tilt towards the outside. Pull the window down into the slot to help align the unit in the correct location."",""page"":14},{""i"":6,""say"":""Install the Open Window Brackets using the provided screws as shown. Measure the distance between the Side Arm Hinge and the closest part of the window frame in line with the Side Arm. Add 1/4” to this distance and cut the Side Arm Foam to length."",""page"":14},{""i"":7,""say"":""Apply Window Sealing Foam to the Side Arm Foam as shown. Insert Side Arm Foam into Side Arm Hinge until the top front of the Side Arm is flush with the top of the hinge. Extend the Anti-Tip Brackets into the window track opening until they stop and secure with the screw."",""page"":15}],""label_note"":""Coaching from the manual or our own checklist. Camera checks can miss things.""}},{""name"":""coach_step"",""args"":{""coach_id"":""3b5a49d54bdb"",""i"":0,""of"":8,""say"":""Remove the top foam insert containing the bracket and hardware from the carton. Measure the inside opening of your window sill and use that distance to set the total width of the bracket Extension Arms. To adjust the extension arms, press the Spring Push Pin, slide the Left Extension Arm out and then install the Right Extension Arm."",""page"":11,""quote"":""A. Measure the inside opening of your window sill and use that distance to set the total width of the bracket Extension Arms. To adjust the extension arms, press the Spring Push Pin, slide the Left Extension Arm out and then install the Right Extension Arm. Use the short arm for 22-26” windows, and the long arm for 26-36” windows."",""pdf_url"":""/parts/midea-maw08u1qwt-8d6d08/manual.pdf#page=11"",""tool"":null,""checks"":[""Is the bracket width set to match the measured window sill opening?"",""Are the left and right extension arms attached to the center bracket?""]}}]},{""step"":""midea_next1"",""spoken"":""Step 2 of 8: Place bracket on window sill to make sure you have measured and aligned the bracket arms correctly. Once your bracket arms are properly calibrated, apply bracket sealing foam strips to the bottom of the bracket as shown, based on your window type."",""actions"":[{""name"":""coach_step"",""args"":{""coach_id"":""3b5a49d54bdb"",""i"":1,""of"":8,""say"":""Place bracket on window sill to make sure you have measured and aligned the bracket arms correctly. Once your bracket arms are properly calibrated, apply bracket sealing foam strips to the bottom of the bracket as shown, based on your window type."",""page"":11,""quote"":""B. Place bracket on window sill to make sure you have measured and aligned the bracket arms correctly."",""pdf_url"":""/parts/midea-maw08u1qwt-8d6d08/manual.pdf#page=11"",""tool"":null,""checks"":[""Is the bracket sitting flat and aligned on the window sill?"",""Are the bracket sealing foam strips applied to the bottom of the bracket?""]}}]},{""step"":""midea_next2"",""spoken"":""Step 3 of 8: Install the Main Support Bracket into the window opening. Ensure that the Horizontal Bracket and Extension Arms are located on the indoor side of the window. Move the Angled Support Arms toward the exterior wall until the feet touch the wall."",""actions"":[{""name"":""coach_step"",""args"":{""coach_id"":""3b5a49d54bdb"",""i"":2,""of"":8,""say"":""Install the Main Support Bracket into the window opening. Ensure that the Horizontal Bracket and Extension Arms are located on the indoor side of the window. Move the Angled Support Arms toward the exterior wall until the feet touch the wall."",""page"":12,""quote"":""A. Install the Main Support Bracket into the window opening. Ensure that the Horizontal Bracket and Extension Arms are located on the indoor side of the window."",""pdf_url"":""/parts/midea-maw08u1qwt-8d6d08/manual.pdf#page=12"",""tool"":null,""checks"":[""Is the horizontal bracket and extension arms on the indoor side of the window?"",""Do the angled support arm feet touch the exterior wall?""]}}]},{""step"":""midea_next3"",""spoken"":""Step 4 of 8: Place the level on the bracket and adjust the Support Arms so that it is level or tilted 1/4 bubble downward and towards the outside. Insert the Main Support Pin through the holes in the Main Support and Angled Support Arm."",""actions"":[{""name"":""coach_step"",""args"":{""coach_id"":""3b5a49d54bdb"",""i"":3,""of"":8,""say"":""Place the level on the bracket and adjust the Support Arms so that it is level or tilted 1/4 bubble downward and towards the outside. Insert the Main Support Pin through the holes in the Main Support and Angled Support Arm."",""page"":12,""quote"":""B. Move the Angled Support Arms toward the exterior wall until the feet touch the wall. Place the level on the bracket and adjust the Support Arms so that it is level or tilted 1/4 bubble downward and towards the outside."",""pdf_url"":""/parts/midea-maw08u1qwt-8d6d08/manual.pdf#page=12"",""tool"":null,""checks"":[""Is the bracket level or tilted slightly downward toward the outside?"",""Are the main support pins inserted through the aligned holes?""]}}]},{""step"":""midea_next4"",""spoken"":""Step 5 of 8: Check the level again and ensure the bracket feels secure. After making any necessary adjustments, insert the cotter pins into the Main Support Pins. Secure the bracket to the windowsill by drilling 1/8” pilot holes and installing the 1/2” Type A screws or 1” Type A screws as shown. Before you drill, point at the spot and say check it."",""actions"":[{""name"":""coach_step"",""args"":{""coach_id"":""3b5a49d54bdb"",""i"":4,""of"":8,""say"":""Check the level again and ensure the bracket feels secure. After making any necessary adjustments, insert the cotter pins into the Main Support Pins. Secure the bracket to the windowsill by drilling 1/8” pilot holes and installing the 1/2” Type A screws or 1” Type A screws as shown."",""page"":13,""quote"":""D. Check the level again and ensure the bracket feels secure. After making any necessary adjustments, insert the cotter pins into the Main Support Pins."",""pdf_url"":""/parts/midea-maw08u1qwt-8d6d08/manual.pdf#page=13"",""tool"":""drill"",""checks"":[""Are the cotter pins installed in the main support pins?""]}}]}]";

            // Made by the backend's own code (server/runjob.py under tests/test_runjob.py's mocks; server/coach.py with
            // tests/fixtures/coach/checks.json; the OFFLINE server's GET /job/run page). Each coach entry is one reply.
        internal const string JobHappy =
            @"[{""name"":""job_started"",""args"":{""run_id"":""676f9a201365"",""steps"":[""survey"",""part"",""safety"",""rules"",""postcard"",""packet"",""checkout""]}},{""name"":""job_step"",""args"":{""i"":0,""name"":""survey"",""status"":""done"",""spoken"":""1 problem found."",""seconds"":0.0,""cost_usd"":0.01}},{""name"":""survey_started"",""args"":{""survey_id"":""2a821da0617a""}},{""name"":""show_survey"",""args"":{""survey_id"":""2a821da0617a"",""pins"":[{""id"":""f1"",""frame_id"":""0001"",""box"":[0.2,0.2,0.6,0.5],""element"":""roof_covering"",""severity"":""severe"",""issue"":""membrane failed"",""action"":""replace"",""part_query"":""ductless mini-split"",""p"":[0,0,0]}],""label"":""AI triage from drone frames, not an inspection""}},{""name"":""job_step"",""args"":{""i"":1,""name"":""part"",""status"":""done"",""spoken"":""Picked the LG 12,000 BTU ductless mini-split indoor unit."",""seconds"":0.0,""cost_usd"":null}},{""name"":""search_started"",""args"":{""job_id"":""b8c22bab267a""}},{""name"":""select_candidate"",""args"":{""index"":0}},{""name"":""job_step"",""args"":{""i"":2,""name"":""safety"",""status"":""done"",""spoken"":""No recalls on this one."",""seconds"":0.0,""cost_usd"":0.09}},{""name"":""show_safety"",""args"":{""part_id"":""lg-knsah121b-04da2e"",""verdict"":""clear"",""headline"":""No recalls""}},{""name"":""job_step"",""args"":{""i"":4,""name"":""postcard"",""status"":""done"",""spoken"":""Here's how it'll look installed."",""seconds"":0.0,""cost_usd"":0.07}},{""name"":""show_postcard"",""args"":{""part_id"":""lg-knsah121b-04da2e"",""image_url"":""/p.jpg"",""before_url"":""/before"",""label"":""AI preview, not to scale""}},{""name"":""job_step"",""args"":{""i"":3,""name"":""rules"",""status"":""done"",""spoken"":""You'll need a permit. Not legal advice."",""seconds"":0.0,""cost_usd"":0.19}},{""name"":""show_rules"",""args"":{""check_id"":""89fabb71d992"",""status"":""done"",""job"":""minisplit_install"",""spoken"":""You'll need a permit. Not legal advice."",""money"":{""incentives"":[{""name"":""HEIP"",""amount_usd"":150.0,""status"":""active"",""url"":""u""}]},""cost_usd"":0.19,""part_ids"":[""lg-knsah121b-04da2e""]}},{""name"":""job_step"",""args"":{""i"":5,""name"":""packet"",""status"":""done"",""spoken"":""The job packet's up: scan the code. The link is public for 7 days."",""seconds"":0.0,""cost_usd"":0.001}},{""name"":""show_packet"",""args"":{""url"":""https://files-cdn.x.ai/t/f.pdf"",""qr_png_url"":null,""expires_at"":""2026-10-03T00:00:00+00:00"",""public"":true,""packet_id"":""abc"",""label"":""Public link""}},{""name"":""job_step"",""args"":{""i"":6,""name"":""checkout"",""status"":""done"",""spoken"":""Pay panel's open. Hold to pay when you're ready."",""seconds"":0.0,""cost_usd"":null}},{""name"":""start_checkout"",""args"":{""seller_index"":0,""part_id"":""lg-knsah121b-04da2e""}},{""name"":""job_done"",""args"":{""run_id"":""676f9a201365"",""status"":""done"",""total_usd"":899.0,""after_rebates_usd"":749.0,""packet_url"":""https://files-cdn.x.ai/t/f.pdf"",""cost_usd"":0.361}}]";

        internal const string JobRecall =
            @"[{""name"":""job_started"",""args"":{""run_id"":""b6ed7f8e4340"",""steps"":[""survey"",""part"",""safety"",""rules"",""postcard"",""packet"",""checkout""]}},{""name"":""job_step"",""args"":{""i"":0,""name"":""survey"",""status"":""done"",""spoken"":""1 problem found."",""seconds"":0.0,""cost_usd"":0.01}},{""name"":""survey_started"",""args"":{""survey_id"":""a0831aa00887""}},{""name"":""show_survey"",""args"":{""survey_id"":""a0831aa00887"",""pins"":[{""id"":""f1"",""frame_id"":""0001"",""box"":[0.2,0.2,0.6,0.5],""element"":""roof_covering"",""severity"":""severe"",""issue"":""membrane failed"",""action"":""replace"",""part_query"":""ductless mini-split"",""p"":[0,0,0]}],""label"":""AI triage from drone frames, not an inspection""}},{""name"":""job_step"",""args"":{""i"":1,""name"":""part"",""status"":""done"",""spoken"":""Picked the LG 12,000 BTU ductless mini-split indoor unit."",""seconds"":0.0,""cost_usd"":null}},{""name"":""search_started"",""args"":{""job_id"":""22e71d351a83""}},{""name"":""select_candidate"",""args"":{""index"":0}},{""name"":""job_step"",""args"":{""i"":2,""name"":""safety"",""status"":""done"",""spoken"":""Heads up: recalled."",""seconds"":0.0,""cost_usd"":0.09}},{""name"":""show_safety"",""args"":{""part_id"":""lg-knsah121b-04da2e"",""verdict"":""recalled"",""headline"":""Recalled May 2026: fire hazard""}},{""name"":""job_step"",""args"":{""i"":4,""name"":""postcard"",""status"":""done"",""spoken"":""Here's how it'll look installed."",""seconds"":0.0,""cost_usd"":0.07}},{""name"":""show_postcard"",""args"":{""part_id"":""lg-knsah121b-04da2e"",""image_url"":""/p.jpg"",""before_url"":""/before"",""label"":""AI preview, not to scale""}},{""name"":""job_step"",""args"":{""i"":3,""name"":""rules"",""status"":""done"",""spoken"":""You'll need a permit. Not legal advice."",""seconds"":0.0,""cost_usd"":0.19}},{""name"":""show_rules"",""args"":{""check_id"":""bcc5cb1f75c5"",""status"":""done"",""job"":""minisplit_install"",""spoken"":""You'll need a permit. Not legal advice."",""money"":{""incentives"":[{""name"":""HEIP"",""amount_usd"":150.0,""status"":""active"",""url"":""u""}]},""cost_usd"":0.19,""part_ids"":[""lg-knsah121b-04da2e""]}},{""name"":""job_step"",""args"":{""i"":5,""name"":""packet"",""status"":""done"",""spoken"":""The job packet's up: scan the code. The link is public for 7 days."",""seconds"":0.0,""cost_usd"":0.001}},{""name"":""show_packet"",""args"":{""url"":""https://files-cdn.x.ai/t/f.pdf"",""qr_png_url"":null,""expires_at"":""2026-10-03T00:00:00+00:00"",""public"":true,""packet_id"":""abc"",""label"":""Public link""}},{""name"":""job_step"",""args"":{""i"":6,""name"":""checkout"",""status"":""stopped"",""spoken"":""This model is recalled; I stopped before checkout."",""seconds"":0.0,""cost_usd"":null}},{""name"":""job_done"",""args"":{""run_id"":""b6ed7f8e4340"",""status"":""stopped"",""total_usd"":899.0,""after_rebates_usd"":749.0,""packet_url"":""https://files-cdn.x.ai/t/f.pdf"",""cost_usd"":0.361}}]";

        internal const string JobLivePoll =
            @"{""run_id"":""0936311b5283"",""status"":""done"",""steps"":[{""i"":0,""name"":""part"",""status"":""done"",""spoken"":""Picked the 5 in. Aluminum Hidden Gutter Hanger with Screw."",""seconds"":0.0,""cost_usd"":null},{""i"":1,""name"":""safety"",""status"":""done"",""spoken"":""Couldn't run the safety check right now."",""seconds"":0.0,""cost_usd"":null},{""i"":2,""name"":""rules"",""status"":""skipped"",""spoken"":""No permit rules on file for this kind of job."",""seconds"":0.0,""cost_usd"":null},{""i"":3,""name"":""postcard"",""status"":""skipped"",""spoken"":""No camera view for the preview."",""seconds"":0.0,""cost_usd"":null},{""i"":4,""name"":""packet"",""status"":""done"",""spoken"":""The job packet is on the local network only (offline)."",""seconds"":3.0,""cost_usd"":null},{""i"":5,""name"":""checkout"",""status"":""done"",""spoken"":""Pay panel's open. Hold to pay when you're ready."",""seconds"":0.0,""cost_usd"":null}],""actions"":[{""name"":""job_started"",""args"":{""run_id"":""0936311b5283"",""steps"":[""part"",""safety"",""rules"",""postcard"",""packet"",""checkout""]}},{""name"":""job_step"",""args"":{""i"":0,""name"":""part"",""status"":""done"",""spoken"":""Picked the 5 in. Aluminum Hidden Gutter Hanger with Screw."",""seconds"":0.0,""cost_usd"":null}},{""name"":""search_started"",""args"":{""job_id"":""147860c447f5""}},{""name"":""select_candidate"",""args"":{""index"":0}},{""name"":""job_step"",""args"":{""i"":2,""name"":""rules"",""status"":""skipped"",""spoken"":""No permit rules on file for this kind of job."",""seconds"":0.0,""cost_usd"":null}},{""name"":""job_step"",""args"":{""i"":3,""name"":""postcard"",""status"":""skipped"",""spoken"":""No camera view for the preview."",""seconds"":0.0,""cost_usd"":null}},{""name"":""job_step"",""args"":{""i"":1,""name"":""safety"",""status"":""done"",""spoken"":""Couldn't run the safety check right now."",""seconds"":0.0,""cost_usd"":null}},{""name"":""show_safety"",""args"":{""part_id"":""amerimax-home-products-21812-846830"",""verdict"":""unknown"",""headline"":""Safety check unavailable""}},{""name"":""job_step"",""args"":{""i"":4,""name"":""packet"",""status"":""done"",""spoken"":""The job packet is on the local network only (offline)."",""seconds"":3.0,""cost_usd"":null}},{""name"":""show_packet"",""args"":{""url"":""/packet/8633fe6e6240c145.pdf"",""qr_png_url"":null,""expires_at"":null,""public"":false,""packet_id"":""8633fe6e6240c145"",""label"":""Offline: no public link. The PDF is on the local network only.""}},{""name"":""job_step"",""args"":{""i"":5,""name"":""checkout"",""status"":""done"",""spoken"":""Pay panel's open. Hold to pay when you're ready."",""seconds"":0.0,""cost_usd"":null}},{""name"":""start_checkout"",""args"":{""seller_index"":0,""part_id"":""amerimax-home-products-21812-846830""}},{""name"":""job_done"",""args"":{""run_id"":""0936311b5283"",""status"":""done"",""total_usd"":2.74,""after_rebates_usd"":null,""packet_url"":""/packet/8633fe6e6240c145.pdf"",""cost_usd"":0}}],""next"":13}";

        internal const string CoachLed =
            @"[{""step"":""led_start"",""spoken"":""Let's install the under-cabinet LED strip: 5 steps. Step 1 of 5: Wipe the underside of the cabinets clean and dry."",""actions"":[{""name"":""coach_started"",""args"":{""coach_id"":""2d617ca7581d"",""job"":""led_strip"",""label"":""under-cabinet LED strip"",""source"":""template"",""steps"":[{""i"":0,""say"":""Wipe the underside of the cabinets clean and dry."",""page"":null},{""i"":1,""say"":""Hold the strip along the front edge underneath and mark where it ends."",""page"":null},{""i"":2,""say"":""Drill a 10 millimetre hole through the cabinet side for the power lead."",""page"":null},{""i"":3,""say"":""Peel the backing, press the strip on along the marks, and feed the lead through the hole."",""page"":null},{""i"":4,""say"":""Plug the lead into the outlet and switch the strip on."",""page"":null}],""label_note"":""Coaching from the manual or our own checklist. Camera checks can miss things.""}},{""name"":""coach_step"",""args"":{""coach_id"":""2d617ca7581d"",""i"":0,""of"":5,""say"":""Wipe the underside of the cabinets clean and dry."",""page"":null,""quote"":null,""pdf_url"":null,""tool"":null,""checks"":[""Is the underside of the cabinet clear of objects?""]}}]},{""step"":""led_next1"",""spoken"":""Step 2 of 5: Hold the strip along the front edge underneath and mark where it ends."",""actions"":[{""name"":""coach_step"",""args"":{""coach_id"":""2d617ca7581d"",""i"":1,""of"":5,""say"":""Hold the strip along the front edge underneath and mark where it ends."",""page"":null,""quote"":null,""pdf_url"":null,""tool"":null,""checks"":[""Are pencil marks visible under the cabinet?""]}}]},{""step"":""led_next2"",""spoken"":""Step 3 of 5: Drill a 10 millimetre hole through the cabinet side for the power lead. Before you drill, point at the spot and say check it."",""actions"":[{""name"":""coach_step"",""args"":{""coach_id"":""2d617ca7581d"",""i"":2,""of"":5,""say"":""Drill a 10 millimetre hole through the cabinet side for the power lead."",""page"":null,""quote"":null,""pdf_url"":null,""tool"":""drill"",""checks"":[""Is there a round hole in the cabinet's side panel?""]}}]},{""step"":""led_stop"",""spoken"":""Stop: that spot is straight above an outlet. Wires usually run up from it; move about 15 centimetres sideways. I can't see inside the wall: check the spot with a stud and wire finder first."",""actions"":[{""name"":""coach_check"",""args"":{""coach_id"":""2d617ca7581d"",""i"":2,""frame_id"":null,""verdict"":""stop"",""results"":[{""id"":""c1"",""question"":""Is there a round hole in the cabinet's side panel?"",""expect"":""yes"",""look_at"":""Look at the cabinet side."",""answer"":""no"",""evidence"":""no hole"",""box"":null,""ok"":false}],""spoken"":""Stop: that spot is straight above an outlet. Wires usually run up from it; move about 15 centimetres sideways. I can't see inside the wall: check the spot with a stud and wire finder first.""}},{""name"":""coach_stop"",""args"":{""coach_id"":""2d617ca7581d"",""i"":2,""kind"":""outlet"",""box"":[0.045,0.29,0.092,0.374],""drill_px"":[0.127,0.1]}}]},{""step"":""led_recheck"",""spoken"":""I see no outlet or switch straight below that spot. I can't see inside the wall: check the spot with a stud and wire finder first. Not yet: no hole."",""actions"":[{""name"":""coach_check"",""args"":{""coach_id"":""2d617ca7581d"",""i"":2,""frame_id"":null,""verdict"":""not_yet"",""results"":[{""id"":""c1"",""question"":""Is there a round hole in the cabinet's side panel?"",""expect"":""yes"",""look_at"":""Look at the cabinet side."",""answer"":""no"",""evidence"":""no hole"",""box"":null,""ok"":false}],""spoken"":""I see no outlet or switch straight below that spot. I can't see inside the wall: check the spot with a stud and wire finder first. Not yet: no hole.""}}]},{""step"":""led_next3"",""spoken"":""Step 4 of 5: Peel the backing, press the strip on along the marks, and feed the lead through the hole."",""actions"":[{""name"":""coach_step"",""args"":{""coach_id"":""2d617ca7581d"",""i"":3,""of"":5,""say"":""Peel the backing, press the strip on along the marks, and feed the lead through the hole."",""page"":null,""quote"":null,""pdf_url"":null,""tool"":null,""checks"":[""Is an LED strip stuck along the underside of the cabinet?""]}}]},{""step"":""led_back"",""spoken"":""Step 3 of 5: Drill a 10 millimetre hole through the cabinet side for the power lead. Before you drill, point at the spot and say check it."",""actions"":[{""name"":""coach_step"",""args"":{""coach_id"":""2d617ca7581d"",""i"":2,""of"":5,""say"":""Drill a 10 millimetre hole through the cabinet side for the power lead."",""page"":null,""quote"":null,""pdf_url"":null,""tool"":""drill"",""checks"":[""Is there a round hole in the cabinet's side panel?""]}}]},{""step"":""led_repeat"",""spoken"":""Step 3 of 5: Drill a 10 millimetre hole through the cabinet side for the power lead. Before you drill, point at the spot and say check it."",""actions"":[{""name"":""coach_step"",""args"":{""coach_id"":""2d617ca7581d"",""i"":2,""of"":5,""say"":""Drill a 10 millimetre hole through the cabinet side for the power lead."",""page"":null,""quote"":null,""pdf_url"":null,""tool"":""drill"",""checks"":[""Is there a round hole in the cabinet's side panel?""]}}]}]";

        internal const string CoachFaucet =
            @"[{""step"":""faucet_start"",""spoken"":""Let's install the kitchen faucet: 6 steps. Step 1 of 6: Clear the counter around the sink and empty the cabinet underneath."",""actions"":[{""name"":""coach_started"",""args"":{""coach_id"":""d70074dccdea"",""job"":""faucet_swap"",""label"":""kitchen faucet"",""source"":""template"",""steps"":[{""i"":0,""say"":""Clear the counter around the sink and empty the cabinet underneath."",""page"":null},{""i"":1,""say"":""Close both shut-off valves under the sink, then open the faucet to let the pressure out."",""page"":null},{""i"":2,""say"":""Put a bucket under the valves and disconnect both supply lines from them."",""page"":null},{""i"":3,""say"":""Undo the mounting nut under the counter and lift the old faucet out."",""page"":null},{""i"":4,""say"":""Set the new faucet in place with its gasket and tighten the mounting nut from below."",""page"":null},{""i"":5,""say"":""Connect the supply lines, open the valves slowly and look for drips."",""page"":null}],""label_note"":""Coaching from the manual or our own checklist. Camera checks can miss things.""}},{""name"":""coach_step"",""args"":{""coach_id"":""d70074dccdea"",""i"":0,""of"":6,""say"":""Clear the counter around the sink and empty the cabinet underneath."",""page"":null,""quote"":null,""pdf_url"":null,""tool"":null,""checks"":[""Are any objects standing within a hand's width of the sink rim?""]}}]},{""step"":""faucet_not_yet"",""spoken"":""Not yet: black shaker bottle and soap bottles near sink."",""actions"":[{""name"":""coach_check"",""args"":{""coach_id"":""d70074dccdea"",""i"":0,""frame_id"":""0221"",""verdict"":""not_yet"",""results"":[{""id"":""c1"",""question"":""Are any objects standing within a hand's width of the sink rim?"",""expect"":""no"",""look_at"":""Look at the sink."",""answer"":""yes"",""evidence"":""black shaker bottle and soap bottles near sink"",""box"":[0.05,0.52,0.15,0.7],""ok"":false}],""spoken"":""Not yet: black shaker bottle and soap bottles near sink.""}}]},{""step"":""faucet_next"",""spoken"":""Step 2 of 6: Close both shut-off valves under the sink, then open the faucet to let the pressure out."",""actions"":[{""name"":""coach_step"",""args"":{""coach_id"":""d70074dccdea"",""i"":1,""of"":6,""say"":""Close both shut-off valves under the sink, then open the faucet to let the pressure out."",""page"":null,""quote"":null,""pdf_url"":null,""tool"":null,""checks"":[""Are the shut-off valves under the sink visible, with their handles turned fully closed?""]}}]},{""step"":""faucet_look"",""spoken"":""I can't see that from here. Look under the sink at the valves."",""actions"":[{""name"":""coach_check"",""args"":{""coach_id"":""d70074dccdea"",""i"":1,""frame_id"":null,""verdict"":""look"",""results"":[{""id"":""c1"",""question"":""Are the shut-off valves under the sink visible, with their handles turned fully closed?"",""expect"":""yes"",""look_at"":""Look under the sink at the valves."",""answer"":""cant_see"",""evidence"":""under sink not visible"",""box"":null,""ok"":false}],""spoken"":""I can't see that from here. Look under the sink at the valves.""}}]},{""step"":""faucet_move2"",""spoken"":""Step 3 of 6: Put a bucket under the valves and disconnect both supply lines from them."",""actions"":[{""name"":""coach_step"",""args"":{""coach_id"":""d70074dccdea"",""i"":2,""of"":6,""say"":""Put a bucket under the valves and disconnect both supply lines from them."",""page"":null,""quote"":null,""pdf_url"":null,""tool"":null,""checks"":[""Is a bucket or container sitting under the valves?"",""Are supply hoses still attached to the shut-off valves?""]}}]},{""step"":""faucet_move3"",""spoken"":""Step 4 of 6: Undo the mounting nut under the counter and lift the old faucet out."",""actions"":[{""name"":""coach_step"",""args"":{""coach_id"":""d70074dccdea"",""i"":3,""of"":6,""say"":""Undo the mounting nut under the counter and lift the old faucet out."",""page"":null,""quote"":null,""pdf_url"":null,""tool"":null,""checks"":[""Is a faucet standing on the sink deck?""]}}]},{""step"":""faucet_move4"",""spoken"":""Step 5 of 6: Set the new faucet in place with its gasket and tighten the mounting nut from below."",""actions"":[{""name"":""coach_step"",""args"":{""coach_id"":""d70074dccdea"",""i"":4,""of"":6,""say"":""Set the new faucet in place with its gasket and tighten the mounting nut from below."",""page"":null,""quote"":null,""pdf_url"":null,""tool"":null,""checks"":[""Is a faucet standing on the sink deck?""]}}]},{""step"":""faucet_passed"",""spoken"":""Looks right. Step 6 of 6: Connect the supply lines, open the valves slowly and look for drips."",""actions"":[{""name"":""coach_check"",""args"":{""coach_id"":""d70074dccdea"",""i"":4,""frame_id"":null,""verdict"":""passed"",""results"":[{""id"":""c1"",""question"":""Is a faucet standing on the sink deck?"",""expect"":""yes"",""look_at"":""Look at the sink."",""answer"":""yes"",""evidence"":""chrome faucet with handle on sink deck"",""box"":[0.39,0.46,0.5,0.59],""ok"":true}],""spoken"":""Looks right. Step 6 of 6: Connect the supply lines, open the valves slowly and look for drips.""}},{""name"":""coach_step"",""args"":{""coach_id"":""d70074dccdea"",""i"":5,""of"":6,""say"":""Connect the supply lines, open the valves slowly and look for drips."",""page"":null,""quote"":null,""pdf_url"":null,""tool"":null,""checks"":[""Are supply hoses connected to both shut-off valves?"",""Is water dripping or pooling under the sink?""]}}]},{""step"":""faucet_done"",""spoken"":""That was the last step. 1 checked by camera, 5 on your word."",""actions"":[{""name"":""coach_done"",""args"":{""coach_id"":""d70074dccdea"",""checked"":1,""overridden"":5}}]}]";
        }
    }
}
