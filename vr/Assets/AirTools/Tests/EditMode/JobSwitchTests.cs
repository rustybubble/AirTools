using System.Collections.Generic;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Scene;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AirTools.Tests
{
    /// switchclean (job), pure (the offline runner): a running job ("do the whole job", the autonomous replace) never acts
    /// on another model. A switch the job asked for itself (its show_model / next_model / load_site, noted as a token
    /// before it runs) moves the job along; the person's switch stops it on the headset — no more polls, a page in flight
    /// or a late page runs nothing, the strip ends cancelled, and the toast says so.
    public class JobSwitchTests
    {
        [SetUp]
        public void SetUp() { SiteScope.Reset(); GrokRails.Reset(); GrokRails.CancelOnServer = (run, reason) => { }; }

        [TearDown]
        public void TearDown() { SiteScope.Reset(); GrokRails.Reset(); }

        static AgentAction A(string name, string json = "{}") => new AgentAction { name = name, args = JObject.Parse(json) };

        static void StartReplace(double now = 0)
        {
            Assert.IsTrue(GrokRails.Apply("job_started", JObject.Parse(
                "{\"run_id\":\"r1\",\"steps\":[\"remove\",\"measure\",\"scale\",\"search\",\"pick\",\"model\",\"place\"],\"kind\":\"replace\",\"title\":\"Replace the dishwasher\"}"), now));
        }

        // ---------------- the verdict ----------------

        [Test]
        public void ThePersonsSwitchIsAUserSwitch_TheJobsOwnIsNot()
        {
            SiteScope.Switch("kitchen", 1f);
            StartReplace();
            Assert.AreEqual("kitchen", GrokRails.Site.Site, "the job works on the model it started on");
            Assert.AreEqual(JobSwitchVerdict.UserSwitch, GrokRails.OnSwitch("zabel-gymnasium", 1), "no token: the person's switch");

            GrokRails.Site.Issued("zabel-gymnasium", 2);
            Assert.AreEqual(JobSwitchVerdict.JobSwitch, GrokRails.OnSwitch("zabel-gymnasium", 3), "the job asked for the gym");
            Assert.AreEqual("zabel-gymnasium", GrokRails.Site.Site, "…and now works there");
            Assert.AreEqual(0, GrokRails.Site.Tokens, "the token is spent");
            Assert.AreEqual(JobSwitchVerdict.UserSwitch, GrokRails.OnSwitch("kitchen", 4), "one token, one switch");
            Assert.AreEqual(JobSwitchVerdict.SameSite, GrokRails.OnSwitch("zabel-gymnasium", 4));
        }

        [Test]
        public void ATokenMatchesItsModelByIdOrName_AnyForNext_AndExpires()
        {
            SiteScope.Switch("kitchen", 1f);
            StartReplace();
            GrokRails.Site.Issued("hospital-bg", 0);
            Assert.AreEqual(JobSwitchVerdict.UserSwitch, GrokRails.OnSwitch("zabel-gymnasium", 1), "a token for the hospital isn't the gym");
            Assert.AreEqual(1, GrokRails.Site.Tokens, "…and stays for the hospital");
            Assert.AreEqual(JobSwitchVerdict.JobSwitch, GrokRails.OnSwitch("hospital-bg", 1));

            GrokRails.Site.Issued("the gym", 10);   // a spoken name (ModelSites.Match)
            Assert.AreEqual(JobSwitchVerdict.JobSwitch, GrokRails.OnSwitch("zabel-gymnasium", 11));
            GrokRails.Site.Issued(null, 20);         // next_model: whichever comes
            Assert.AreEqual(JobSwitchVerdict.JobSwitch, GrokRails.OnSwitch("kitchen", 21));
            GrokRails.Site.Issued(null, 30);
            Assert.AreEqual(JobSwitchVerdict.UserSwitch, GrokRails.OnSwitch("zabel-gymnasium", 30 + JobSiteGuard.TokenSeconds + 1), "an old token has expired");
        }

        [Test]
        public void NoJobNoVerdict()
        {
            Assert.AreEqual(JobSwitchVerdict.NoJob, GrokRails.OnSwitch("kitchen", 0), "nothing on the rail");
            StartReplace();
            Assert.IsTrue(GrokRails.Apply("job_done", JObject.Parse("{\"run_id\":\"r1\",\"status\":\"done\",\"kind\":\"replace\",\"summary\":\"Put a Bosch in the gap\"}"), 5));
            Assert.AreEqual(JobSwitchVerdict.NoJob, GrokRails.OnSwitch("zabel-gymnasium", 6), "a finished job has nothing left to stop");
            Assert.IsNull(GrokRails.Site.RunId);
        }

        [Test]
        public void OnlyTheJobsSwitchActionsGetTokens()
        {
            StartReplace();
            // A poll page: the job's own actions.
            int n = GrokRails.Site.NoteIssued(new List<AgentAction>
            {
                A("job_step", "{\"i\":0,\"name\":\"remove\",\"status\":\"done\"}"), A("show_model", "{\"site\":\"zabel-gymnasium\"}"),
                A("next_model"), A("load_site", "{\"site\":\"kitchen\"}"), A("place_part", "{}"),
            }, 0);
            Assert.AreEqual(3, n);
            GrokRails.Site.End();
            // A reply: a show_model before job_started is the person's; after it, the job's.
            n = GrokRails.Site.NoteIssued(new List<AgentAction> { A("show_model", "{\"site\":\"hospital-bg\"}"), A("job_started", "{}"), A("show_model", "{\"site\":\"kitchen\"}") }, 0, afterJobStarted: true);
            Assert.AreEqual(1, n);
            n = GrokRails.Site.NoteIssued(new List<AgentAction> { A("show_model", "{\"site\":\"hospital-bg\"}") }, 0, afterJobStarted: true);
            Assert.AreEqual(0, n, "a reply that starts no job issues no job switch");
            Assert.AreEqual("zabel-gymnasium", JobSiteGuard.TargetOf(A("show_model", "{\"site\":\"zabel-gymnasium\"}")));
            Assert.AreEqual("the gym", JobSiteGuard.TargetOf(A("show_model", "{\"name\":\"the gym\"}")));
            Assert.IsNull(JobSiteGuard.TargetOf(A("show_model", "{\"site\":\"next\"}")));
            Assert.IsNull(JobSiteGuard.TargetOf(A("next_model", "{\"direction\":\"previous\"}")));
        }

        [Test]
        public void ATokenFromTheReplyThatStartsTheJobSurvivesItsJobStarted()
        {
            SiteScope.Switch("kitchen", 1f);
            var reply = new List<AgentAction>
            {
                A("job_started", "{\"run_id\":\"r2\",\"steps\":[\"survey\",\"part\"]}"), A("show_model", "{\"site\":\"zabel-gymnasium\"}"),
            };
            GrokRails.Site.NoteIssued(reply, 0, afterJobStarted: true);   // AgentClient, before ExecuteAll
            Assert.IsTrue(GrokRails.Apply("job_started", reply[0].args, 0));
            Assert.AreEqual(JobSwitchVerdict.JobSwitch, GrokRails.OnSwitch("zabel-gymnasium", 5));
        }

        // ---------------- the person's switch stops it ----------------

        [Test]
        public void AUserSwitchStopsTheJob_NoMorePolls_LatePagesRunNothing_TheStripEndsCancelled()
        {
            SiteScope.Switch("kitchen", 1f);
            StartReplace();
            Assert.IsTrue(GrokRails.Poll.Due(0), "polling");
            GrokRails.Poll.Sent();   // a page in flight when the person switches
            Assert.AreEqual(JobSwitchVerdict.UserSwitch, GrokRails.OnSwitch("zabel-gymnasium", 1));
            Assert.IsTrue(GrokRails.CancelForSwitch("zabel-gymnasium", 1, out string toast));
            Assert.AreEqual("Stopped the dishwasher job: you switched to the Zabel gym", toast);

            Assert.IsFalse(GrokRails.Poll.Active, "no more polls");
            Assert.IsFalse(GrokRails.Poll.InFlight, "JobRunPoller drops the page in flight (it checks InFlight)");
            Assert.AreEqual(JobPollState.SwitchedAway, GrokRails.Poll.StopReason);
            Assert.IsFalse(GrokRails.Poll.Due(100));
            // The late page: its actions never run.
            var late = GrokRailPayloads.Poll("{\"run_id\":\"r1\",\"status\":\"running\",\"steps\":[],\"actions\":[{\"name\":\"measure_cavity\",\"args\":{\"component_id\":\"dw1\"}},{\"name\":\"place_part\",\"args\":{}}],\"next\":4}");
            Assert.IsEmpty(GrokRails.Polled(late, 2), "a late page runs nothing");

            var job = GrokRails.Job;
            Assert.IsTrue(job.Finished && job.IsCancelled);
            Assert.AreEqual("cancelled", job.Status);
            Assert.AreEqual("✗ Cancelled · you switched to the Zabel gym", GrokRailText.JobStatus(job), "the strip's end state");
            Assert.AreEqual(GrokRailText.CancelledDetail, GrokRailText.JobStatusDetail(job));
            Assert.AreEqual(AirTools.UI.ColorRole.TextSecondary, GrokRailTones.Summary(job), "quiet: the person's own switch, not a failure");
            Assert.IsNull(GrokRails.Site.RunId);
            Assert.AreEqual(JobSwitchVerdict.NoJob, GrokRails.OnSwitch("kitchen", 3), "stopped once");
            Assert.IsFalse(GrokRails.CancelForSwitch("kitchen", 3, out _));
            // A job_done that still comes (a poll that was answered anyway) doesn't turn it back.
            Assert.IsFalse(job.Cancel("again", 4));
        }

        [Test]
        public void TheServerIsToldOnce_FireAndForget()
        {
            var told = new List<(string run, string reason)>();
            GrokRails.CancelOnServer = (run, reason) => told.Add((run, reason));
            SiteScope.Switch("kitchen", 1f);
            StartReplace();
            Assert.IsTrue(GrokRails.CancelForSwitch("zabel-gymnasium", 1, out _));
            CollectionAssert.AreEqual(new[] { ("r1", "switched to zabel-gymnasium") }, told, "POST /job/run/r1/cancel once");
            Assert.IsFalse(GrokRails.CancelForSwitch("kitchen", 2, out _));
            Assert.AreEqual(1, told.Count, "a stopped run isn't cancelled again");

            // A sender that throws (no network, a bad server) never undoes the headset's stop.
            GrokRails.Reset();
            GrokRails.CancelOnServer = (run, reason) => throw new System.InvalidOperationException("offline");
            StartReplace();
            Assert.IsTrue(GrokRails.CancelForSwitch("zabel-gymnasium", 3, out _));
            Assert.IsTrue(GrokRails.Job.IsCancelled && !GrokRails.Poll.Active);

            GrokRails.Reset();
            Assert.IsNotNull(GrokRails.CancelOnServer, "Reset puts the app's sender back (JobCancelClient.Send)");
            Assert.AreEqual("switched to built-in", GrokRails.CancelReason(null));
        }

        [Test]
        public void TheCancelRequestNamesTheRunTheSessionAndWhy()
        {
            Assert.AreEqual("/job/run/r1/cancel", JobCancelClient.PathFor("r1"));
            Assert.AreEqual("/job/run/a%2Fb/cancel", JobCancelClient.PathFor("a/b"), "a run id is one path segment");
            var body = JObject.Parse(JobCancelClient.Body("quest-1", "switched to zabel-gymnasium"));
            Assert.AreEqual("quest-1", (string)body["session_id"]);
            Assert.AreEqual("switched to zabel-gymnasium", (string)body["reason"]);
            Assert.AreEqual(JTokenType.Null, JObject.Parse(JobCancelClient.Body("quest-1", " "))["reason"].Type);
            // Nothing is sent without a run id or a PartsClient in the scene (the offline runner has none).
            JobCancelClient.ResetStats();
            JobCancelClient.Send(null, "x");
            JobCancelClient.Send("r1", "x");
            Assert.AreEqual(0, JobCancelClient.Sent);
        }

        [Test]
        public void TheToastNamesTheJob()
        {
            StartReplace();
            Assert.AreEqual("the dishwasher job", GrokRailText.JobNoun(GrokRails.Job));
            GrokRails.Reset();
            Assert.IsTrue(GrokRails.Apply("job_started", JObject.Parse("{\"run_id\":\"r3\",\"steps\":[\"survey\",\"part\"]}"), 0));
            Assert.AreEqual("Stopped the job: you switched to the kitchen", GrokRailText.CancelledToast(GrokRails.Job, "kitchen"));
            Assert.AreEqual("Stopped the job: you switched to the test facade", GrokRailText.CancelledToast(GrokRails.Job, ModelSites.BuiltIn));
            Assert.IsTrue(JobPollState.IsFinal("cancelled"), "a server that learns to cancel a run reports it as final");
        }
    }
}
