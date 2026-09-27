using System.Collections.Generic;
using System.Linq;
using AirTools.Agent;
using AirTools.Agent.Grok;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AirTools.Tests
{
    // autonomy: one sentence → the whole replace (backend server/replace_job.py). The server runs the chain; the app runs
    // what arrives. RUN is a real run from the patched server (:8005, the kitchen at its site default ×1.63):
    // GET /job/run/{id}?after=0 for "Replace the dishwasher with a new one that fits."
    static class AutoReplaceFixtures
    {
        public const string Run = @"{""run_id"":""d46e05433845"",""status"":""done"",""steps"":[{""i"":0,""name"":""remove"",""status"":""done"",""spoken"":""Took out the dishwasher."",""seconds"":0.0,""cost_usd"":null},{""i"":1,""name"":""measure"",""status"":""done"",""spoken"":""Taped the gap: 71 × 96 × 72 cm on the scan."",""seconds"":0.0,""cost_usd"":null},{""i"":2,""name"":""scale"",""status"":""skipped"",""spoken"":""The scale's already set (×1.63); kept it."",""seconds"":0.0,""cost_usd"":null},{""i"":3,""name"":""search"",""status"":""done"",""spoken"":""Found 3 dishwashers for the 71 × 96 × 72 cm gap: 3 fits."",""seconds"":0.0,""cost_usd"":null},{""i"":4,""name"":""pick"",""status"":""done"",""spoken"":""Picked the Frigidaire FDPC4221AS: 74 mm to spare, $329."",""seconds"":0.0,""cost_usd"":null},{""i"":5,""name"":""model"",""status"":""done"",""spoken"":""3D model ready: built to size from the product photo."",""seconds"":0.0,""cost_usd"":null},{""i"":6,""name"":""place"",""status"":""done"",""spoken"":""Put the Frigidaire FDPC4221AS in. It fits with 74 millimetres to spare."",""seconds"":0.0,""cost_usd"":null}],""actions"":[{""name"":""job_started"",""args"":{""run_id"":""d46e05433845"",""steps"":[""remove"",""measure"",""scale"",""search"",""pick"",""model"",""place""],""kind"":""replace"",""title"":""Replace the dishwasher""}},{""name"":""job_step"",""args"":{""i"":0,""name"":""remove"",""status"":""done"",""spoken"":""Took out the dishwasher."",""seconds"":0.0,""cost_usd"":null}},{""name"":""remove_component"",""args"":{""component_id"":""dw1""}},{""name"":""job_step"",""args"":{""i"":1,""name"":""measure"",""status"":""done"",""spoken"":""Taped the gap: 71 × 96 × 72 cm on the scan."",""seconds"":0.0,""cost_usd"":null}},{""name"":""measure_cavity"",""args"":{""component_id"":""dw1""}},{""name"":""job_step"",""args"":{""i"":2,""name"":""scale"",""status"":""skipped"",""spoken"":""The scale's already set (×1.63); kept it."",""seconds"":0.0,""cost_usd"":null}},{""name"":""job_step"",""args"":{""i"":3,""name"":""search"",""status"":""running"",""spoken"":""Searching for dishwashers that fit 71 × 96 × 72 cm.""}},{""name"":""job_step"",""args"":{""i"":3,""name"":""search"",""status"":""done"",""spoken"":""Found 3 dishwashers for the 71 × 96 × 72 cm gap: 3 fits."",""seconds"":0.0,""cost_usd"":null}},{""name"":""search_started"",""args"":{""job_id"":""aaccf3afb680""}},{""name"":""job_step"",""args"":{""i"":4,""name"":""pick"",""status"":""done"",""spoken"":""Picked the Frigidaire FDPC4221AS: 74 mm to spare, $329."",""seconds"":0.0,""cost_usd"":null}},{""name"":""job_step"",""args"":{""i"":5,""name"":""model"",""status"":""done"",""spoken"":""3D model ready: built to size from the product photo."",""seconds"":0.0,""cost_usd"":null}},{""name"":""job_step"",""args"":{""i"":6,""name"":""place"",""status"":""done"",""spoken"":""Put the Frigidaire FDPC4221AS in. It fits with 74 millimetres to spare."",""seconds"":0.0,""cost_usd"":null}},{""name"":""place_part"",""args"":{""part_id"":""frigidaire-fdpc4221as-341c87"",""model_url"":""/parts/frigidaire-fdpc4221as-341c87/model.glb"",""name"":""24 in. Front Control Smart Built-In Tall Tub 62 dBA Dishwasher in Stainless Steel"",""component_id"":""dw1"",""fits"":""fits"",""clearance_mm"":{""w"":103,""h"":74,""d"":90},""cycle"":{""index"":0,""count"":3}}},{""name"":""job_done"",""args"":{""run_id"":""d46e05433845"",""status"":""done"",""kind"":""replace"",""title"":""Replace the dishwasher"",""spoken"":""Put a Frigidaire FDPC4221AS in the dishwasher gap, 74 mm to spare. Say next one to see the other two."",""summary"":""Frigidaire FDPC4221AS in the dishwasher gap · 74 mm to spare"",""component_id"":""dw1"",""part_id"":""frigidaire-fdpc4221as-341c87"",""fits"":""fits"",""models_ready"":3,""total_usd"":329.0,""after_rebates_usd"":null,""packet_url"":null,""cost_usd"":0}}],""next"":14}";

        public static JobPollBody Body() => GrokRailPayloads.Poll(Run);

        public static List<AgentAction> Actions() => Body().actions;

        public static AgentAction A(string name, string args = "{}") => new AgentAction { name = name, args = JObject.Parse(args) };
    }

    public class AutoReplaceOrderTests
    {
        static List<string> Names(IEnumerable<AgentAction> a) => a.Select(x => x.name).ToList();

        [Test]
        public void TheRecordedRunIsAWholeReplace()
        {
            var names = Names(AutoReplaceFixtures.Actions());
            Assert.IsTrue(AutoReplace.CheckOrder(names, null, out string detail), detail);
            Assert.AreEqual("remove_component → measure_cavity → search_started → place_part → job_done", detail, "no scale_gap at ×1.63");
        }

        [Test]
        public void WithTheScaleStepToo()
        {
            var names = new List<string> { "job_started", "job_step", "remove_component", "job_step", "measure_cavity", "job_step", "scale_gap", "job_step", "job_step",
                "search_started", "job_step", "job_step", "job_step", "place_part", "job_done" };
            Assert.IsTrue(AutoReplace.CheckOrder(names, new List<string>(), out string detail), detail);
            StringAssert.Contains("scale_gap", detail);
        }

        [Test]
        public void MissingOutOfOrderTwiceOrFailedIsNot()
        {
            Assert.IsFalse(AutoReplace.CheckOrder(new[] { "remove_component", "measure_cavity", "search_started", "job_done" }, null, out string d1));
            StringAssert.Contains("no place_part", d1);
            Assert.IsFalse(AutoReplace.CheckOrder(new[] { "measure_cavity", "remove_component", "search_started", "place_part", "job_done" }, null, out string d2));
            StringAssert.Contains("remove_component out of order", d2);
            Assert.IsFalse(AutoReplace.CheckOrder(new[] { "remove_component", "measure_cavity", "search_started", "place_part", "place_part", "job_done" }, null, out string d3));
            StringAssert.Contains("place_part twice", d3);
            var all = new[] { "remove_component", "measure_cavity", "search_started", "place_part", "job_done" };
            Assert.IsFalse(AutoReplace.CheckOrder(all, new[] { "place_part" }, out string d4));
            StringAssert.Contains("place_part failed", d4);
            Assert.IsFalse(AutoReplace.CheckOrder(all.Concat(new[] { "restore_component" }).ToList(), null, out string d5), "a put-back isn't part of the run");
        }

        [Test]
        public void RunIdComesFromTheReplysJobStarted()
        {
            var reply = new List<AgentAction> { AutoReplaceFixtures.A("show_limits"), AutoReplaceFixtures.A("job_started", @"{""run_id"":""r1"",""steps"":[""remove""]}") };
            Assert.AreEqual("r1", AutoReplace.RunId(reply));
            Assert.IsNull(AutoReplace.RunId(new List<AgentAction> { AutoReplaceFixtures.A("remove_component") }));
            CollectionAssert.AreEqual(AutoReplace.Steps, AutoReplaceFixtures.Body().steps.Select(s => s.name).ToArray(), "the server's step names");
        }
    }

    public class AutoReplaceRailTests
    {
        [SetUp] public void Clear() => GrokRails.Clear();
        [TearDown] public void After() => GrokRails.Clear();

        [Test]
        public void TheRailIsTitledAndEndsOnTheSummary()
        {
            var actions = AutoReplaceFixtures.Actions();
            Assert.IsTrue(GrokRails.Apply("job_started", actions[0].args, 0));
            var job = GrokRails.Job;
            Assert.IsTrue(job.IsReplace);
            Assert.AreEqual("Replace the dishwasher", job.Title);
            Assert.AreEqual("Replace the dishwasher · 0 of 7", GrokRailText.JobLine(job));
            Assert.AreEqual("Replace the dishwasher", GrokRailText.Title(job));
            double t = 1;
            foreach (var a in actions.Skip(1))
                if (System.Array.IndexOf(GrokRails.Actions, a.name) >= 0) GrokRails.Apply(a.name, a.args, t += 0.1);
            Assert.IsTrue(job.Finished);
            Assert.AreEqual("done", job.Status);
            Assert.AreEqual(DotStatus.Skipped, job.Dots[2].Status, "scale: already set");
            Assert.AreEqual(6, job.Dots.Count(d => d.Status == DotStatus.Done));
            Assert.AreEqual("✓ Frigidaire FDPC4221AS in the dishwasher gap · 74 mm to spare", GrokRailText.JobSummary(job.Done, job));
            Assert.AreEqual("Say next one for the others", GrokRailText.JobDetail(job.Done, job));
            StringAssert.StartsWith("Put a Frigidaire FDPC4221AS in the dishwasher gap", job.Done.spoken);
            Assert.AreEqual("frigidaire-fdpc4221as-341c87", job.Done.part_id);
            Assert.AreEqual(3, job.Done.models_ready);
        }

        [Test]
        public void ARunningLineCaptionsWithoutSettlingTheDot()
        {
            GrokRails.Apply("job_started", JObject.Parse(@"{""run_id"":""r2"",""steps"":[""remove"",""search""],""kind"":""replace"",""title"":""Replace the range""}"), 0);
            GrokRails.Apply("job_step", JObject.Parse(@"{""i"":1,""name"":""search"",""status"":""running"",""spoken"":""Searching for induction ranges · charting the candidates · 4 s""}"), 1);
            var job = GrokRails.Job;
            Assert.AreEqual(DotStatus.Pending, job.Dots[1].Status);
            Assert.AreEqual("Searching for induction ranges · charting the candidates · 4 s", job.Caption);
            Assert.AreEqual(0, job.Settled);
        }

        [Test]
        public void AStoppedReplaceSaysWhere()
        {
            GrokRails.Apply("job_started", JObject.Parse(@"{""run_id"":""r3"",""steps"":[""pick""],""kind"":""replace"",""title"":""Replace the dishwasher""}"), 0);
            GrokRails.Apply("job_done", JObject.Parse(@"{""run_id"":""r3"",""status"":""stopped"",""kind"":""replace"",""summary"":""Stopped: None of the 3 fits the 52 × 71 × 53 cm gap"",""spoken"":""None of the 3 fits."",""models_ready"":0}"), 1);
            Assert.AreEqual("✗ Stopped: None of the 3 fits the 52 × 71 × 53 cm gap", GrokRailText.JobSummary(GrokRails.Job.Done, GrokRails.Job));
            Assert.AreEqual("", GrokRailText.JobDetail(GrokRails.Job.Done, GrokRails.Job));
        }

        [Test]
        public void DoTheWholeJobKeepsItsTitle()
        {
            GrokRails.Apply("job_started", JObject.Parse(@"{""run_id"":""r4"",""steps"":[""part"",""checkout""]}"), 0);
            Assert.IsFalse(GrokRails.Job.IsReplace);
            Assert.AreEqual("Do the whole job · 0 of 2", GrokRailText.JobLine(GrokRails.Job));
        }

        /// The [AgentAction] names of the handler classes the replace run relies on (AgentActions.Handlers reflects over the
        /// whole assembly, which the offline runner can't load; these classes load fine).
        static HashSet<string> Handled()
        {
            var names = new HashSet<string>(AgentActions.Known);
            foreach (var t in new[] { typeof(ScenePartsActions), typeof(ReplaceFlowActions), typeof(PlacementActions), typeof(GrokRailActions), typeof(ModelViewActions) })
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    foreach (var attr in m.GetCustomAttributes(typeof(AgentActionAttribute), false)) names.Add(((AgentActionAttribute)attr).Name);
            return names;
        }

        [Test]
        public void TheAppKnowsEveryActionTheRunSends()
        {
            var known = Handled();
            foreach (var a in AutoReplaceFixtures.Actions()) Assert.IsTrue(known.Contains(a.name), a.name);
            foreach (var name in new[] { "undo_edit", "scale_gap", "cycle_model", "restore_component", "show_model", "show_limits" })
                Assert.IsTrue(known.Contains(name), name);
        }
    }

    public class AutoReplaceIntentsTests
    {
        static LocalState Gap() => new LocalState { GapOpen = true, CanRemove = t => t == "dishwasher" || t == "range" };
        static LocalState Closed() => new LocalState { CanRemove = t => t == "dishwasher" || t == "range" };

        [TestCase("The opening is 34 1⁄2 inches tall", "The opening is 34 1/2 inches tall")]
        [TestCase("the opening is thirty-four and a half inches tall", "the opening is 34 and a half inches tall")]
        [TestCase("under twelve hundred dollars", "under 1200 dollars")]
        [TestCase("the opening is 34½ inches tall", "the opening is 34 ½ inches tall")]
        [TestCase("next one", "next one")]
        [TestCase("show me option two", "show me option two")]
        [TestCase("put it back", "put it back")]
        public void Normalize(string said, string expected) => Assert.AreEqual(expected, LocalIntents.Normalize(said));

        [TestCase("The opening is 34 1⁄2 inches tall", "h", 0.8763)]
        [TestCase("The opening is thirty-four and a half inches tall.", "h", 0.8763)]
        [TestCase("the gap is 30 3/4 inches wide", "w", 0.78105)]
        [TestCase("the opening is 24 ¾ inches deep", "d", 0.62865)]
        [TestCase("it's 610 mm wide", "w", 0.61)]
        public void WhispersFractionsSetTheScale(string said, string axis, double metres)
        {
            var i = LocalIntents.Match(said);
            Assert.AreEqual(LocalIntentKind.ScaleGap, i.Kind, said);
            Assert.AreEqual(axis, i.Axis);
            Assert.AreEqual(metres, i.Metres, 1e-4);
        }

        [TestCase("undo")]
        [TestCase("undo that")]
        [TestCase("okay undo it please")]
        [TestCase("take that back")]
        public void Undo(string said) => Assert.AreEqual(LocalIntentKind.Undo, LocalIntents.Match(said).Kind);

        [Test]
        public void TwoSentencesAreTwoPhrases()
        {
            var two = LocalIntents.MatchAll("Remove the range. Find a 30-inch induction range that fits.");
            CollectionAssert.AreEqual(new[] { LocalIntentKind.Remove, LocalIntentKind.FindFitting }, two.Select(x => x.Kind).ToArray());
            Assert.AreEqual("range", two[0].Thing);
        }

        [Test]
        public void AServerJobOwnsTheUtterance()
        {
            // "take out the dishwasher and put in a new one" is Remove + PlaceIt here; the server ran the whole replace.
            var intents = LocalIntents.MatchAll("take out the dishwasher and put in a new one");
            Assert.AreEqual(LocalIntentKind.Remove, intents[0].Kind);
            var reply = new List<AgentAction> { AutoReplaceFixtures.A("job_started", @"{""run_id"":""r5"",""steps"":[""remove""],""kind"":""replace""}") };
            var plan = LocalIntents.Arbitrate(intents, Closed(), reply);
            Assert.IsFalse(plan.RunsLocal, "the job removes it; doing it here too would run it twice");
            Assert.AreEqual(1, plan.Actions.Count);
            Assert.AreEqual(0, plan.Dropped.Count);
            Assert.IsTrue(LocalIntents.StartsJob(reply));
            // with no reply at all (the server down) the headset still takes it out
            Assert.IsTrue(LocalIntents.Arbitrate(intents, Closed(), null).RunsLocal);
        }

        [Test]
        public void UndoRunsHereOnlyWhenTheServerDidnt()
        {
            var undo = LocalIntents.MatchAll("undo");
            Assert.IsFalse(LocalIntents.Arbitrate(undo, Gap(), new List<AgentAction> { AutoReplaceFixtures.A("undo_edit") }).RunsLocal);
            Assert.IsTrue(LocalIntents.Arbitrate(undo, Gap(), new List<AgentAction>()).RunsLocal);
            Assert.IsFalse(LocalIntents.Arbitrate(undo, Closed(), new List<AgentAction>()).RunsLocal, "no replace flow live: the server's words stand");
            Assert.IsFalse(LocalIntents.Arbitrate(undo, Gap(), new List<AgentAction> { AutoReplaceFixtures.A("undo_reimagine") }).RunsLocal, "the picture's undo");
        }
    }
}
