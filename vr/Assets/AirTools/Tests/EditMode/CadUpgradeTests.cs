using AirTools.Parts;
using NUnit.Framework;

namespace AirTools.Tests
{
    /// cad (pure, offline too): Grok's OpenSCAD models on the app side — the badges ("AI mesh · Hunyuan",
    /// "CAD · Grok 4.20 · OpenSCAD", "Template"), the writing line with its countdown ("Template · Grok is writing the CAD
    /// model… 2 min"), asset.cad parsing, and the poll/upgrade state machine (CadUpgrade: watch, wait, swap, stop, drop).
    /// The Unity side (the swap keeping the anchor while placed) is AgentHarness.AssetCadCheck in the Editor gate.
    public class CadUpgradeTests
    {
        static PartAsset A(string tier, string mode = "llm_scad", string by = null, PartCad cad = null, string note = null, string status = "ready") =>
            new PartAsset { tier = tier, mode = mode, made_by = by, cad = cad, note = note, status = status };

        static PartCad Writing(float eta, float receivedAt = 0f) => new PartCad { status = "writing", eta_s = eta, receivedAt = receivedAt };

        [Test]
        public void EachBadge_SaysWhoMadeTheModel()
        {
            Assert.AreEqual("AI mesh · Hunyuan", AssetModes.Badge(A("ai_mesh", "hf", "hf:tencent/Hunyuan3D-2.1")));
            Assert.AreEqual("CAD · Grok 4.20 · OpenSCAD", AssetModes.Badge(A("scad", by: "xai:grok-4.20-0309-non-reasoning + openscad")));
            Assert.AreEqual("CAD · Grok 4.7 · OpenSCAD", AssetModes.Badge(A("scad", by: "xai:grok-4.7 + openscad")));
            Assert.AreEqual("CAD · Grok 4.3 · OpenSCAD", AssetModes.Badge(A("scad", by: "xai:grok-4.3 + openscad")));
            Assert.AreEqual("CAD · Grok · OpenSCAD", AssetModes.Badge(A("scad")), "an older record without made_by");
            Assert.AreEqual("CAD · Grok · OpenSCAD", AssetModes.Badge(A("scad", by: "openscad")));
            Assert.AreEqual("CAD · qwen-coder · OpenSCAD", AssetModes.Badge(A("scad", by: "groq:qwen-coder + openscad")));
            Assert.AreEqual("Template", AssetModes.Badge(A("llm", by: "groq:qwen/qwen3.8-27b")));
            Assert.AreEqual("CAD · Grok 4.20 · OpenSCAD", AssetModes.TierLine(A("scad", by: "xai:grok-4.20-0309-non-reasoning + openscad", cad: new PartCad { status = "ready" }), 0f));
            Assert.AreEqual("3D model: CAD · Grok 4.20 · OpenSCAD", AssetModes.SwappedLine(A("scad", by: "xai:grok-4.20-0309-non-reasoning + openscad")));
        }

        [Test]
        public void WhileGrokWrites_TheTemplateSaysSo_WithTheTimeLeft()
        {
            Assert.AreEqual("Template · Grok is writing the CAD model… 2 min", AssetModes.TierLine(A("llm", cad: Writing(130f)), 0f));
            Assert.AreEqual("Template · Grok is writing the CAD model… 1 min", AssetModes.TierLine(A("llm", cad: Writing(75f)), 0f));
            Assert.AreEqual("Template · Grok is writing the CAD model… 40 s", AssetModes.TierLine(A("llm", cad: Writing(37f)), 0f));
            Assert.AreEqual("Template · Grok is writing the CAD model… almost done", AssetModes.TierLine(A("llm", cad: Writing(10f)), 0f));
            Assert.AreEqual("Template · Grok is writing the CAD model…", AssetModes.TierLine(A("llm", cad: new PartCad { status = "writing" }), 0f));
            // counted down from when the answer arrived (app clock)
            Assert.AreEqual("Template · Grok is writing the CAD model… 1 min", AssetModes.TierLine(A("llm", cad: Writing(130f, receivedAt: 100f)), 160f));
            Assert.AreEqual("Template · Grok is writing the CAD model… almost done", AssetModes.TierLine(A("llm", cad: Writing(30f, receivedAt: 100f)), 200f));
            // not writing: the note's short reason, as before
            Assert.AreEqual("Template · CAD failed", AssetModes.TierLine(A("llm", note: "Grok's OpenSCAD model didn't build: the LLM template instead",
                cad: new PartCad { status = "failed", reason = "x" }), 0f));
            Assert.AreEqual("Template · no OpenSCAD", AssetModes.TierLine(A("llm", note: "OpenSCAD isn't installed on the server: the LLM template instead",
                cad: new PartCad { status = "failed" }), 0f));
            Assert.AreEqual("AI mesh · Hunyuan", AssetModes.TierLine(A("ai_mesh", "hf", "hf:tencent/Hunyuan3D-2.1", cad: Writing(60f)), 0f), "a CAD model is only news on the template");
        }

        [Test]
        public void TheCadStatus_IsParsedFromPartJson()
        {
            var spec = PartSpec.Parse("{\"id\":\"dw\",\"name\":\"Dishwasher\",\"dims_mm\":{\"w\":609.6,\"d\":635,\"h\":889}," +
                "\"asset\":{\"tier\":\"llm\",\"status\":\"ready\",\"mode\":\"llm_scad\",\"note\":\"Grok is still writing the OpenSCAD model: the LLM template instead\"," +
                "\"cad\":{\"status\":\"writing\",\"made_by\":\"xai:grok-4.20-0309-non-reasoning + openscad\",\"started_at\":1790000000.5,\"eta_s\":42}}}");
            Assert.IsTrue(spec.asset.cad.Writing);
            Assert.AreEqual(42f, spec.asset.cad.eta_s);
            Assert.AreEqual(0f, spec.asset.cad.receivedAt, "stamped by the app when it arrives, never parsed");
            Assert.IsTrue(CadUpgrade.ShouldWatch(spec.asset));
            var old = PartSpec.Parse("{\"id\":\"dw\",\"name\":\"D\",\"dims_mm\":{\"w\":1,\"d\":1,\"h\":1},\"asset\":{\"tier\":\"llm\",\"mode\":\"llm_scad\"}}");
            Assert.IsNull(old.asset.cad, "an older server: no CAD status");
            Assert.IsFalse(CadUpgrade.ShouldWatch(old.asset), "nothing to watch without a status");
        }

        [Test]
        public void OnlyTheLlmCadTemplateWhileWriting_IsWatched()
        {
            Assert.IsTrue(CadUpgrade.ShouldWatch(A("llm", cad: Writing(30f))));
            Assert.IsFalse(CadUpgrade.ShouldWatch(A("scad", cad: new PartCad { status = "ready" })), "CAD already");
            Assert.IsFalse(CadUpgrade.ShouldWatch(A("llm", "hf", cad: Writing(30f))), "the HF model (Compare flipped it)");
            Assert.IsFalse(CadUpgrade.ShouldWatch(A("llm", cad: new PartCad { status = "failed" })));
            Assert.IsFalse(CadUpgrade.ShouldWatch(A("llm")));
            Assert.IsFalse(CadUpgrade.ShouldWatch(null));
        }

        [Test]
        public void PollsEvery10To15Seconds_SoonerWhenNearlyDone()
        {
            Assert.AreEqual(15f, CadUpgrade.NextPoll(Writing(300f)));
            Assert.AreEqual(12f, CadUpgrade.NextPoll(Writing(10f)));
            Assert.AreEqual(10f, CadUpgrade.NextPoll(Writing(3f)));
            Assert.AreEqual(15f, CadUpgrade.NextPoll(null));
        }

        [Test]
        public void TheStateMachine_WaitsSwapsStopsAndDrops()
        {
            var shown = A("llm", cad: Writing(60f));
            Assert.AreEqual(CadStep.Wait, CadUpgrade.Decide(shown, A("llm", cad: Writing(45f)), 0, 15f), "still writing");
            Assert.AreEqual(CadStep.Wait, CadUpgrade.Decide(shown, A("llm", cad: new PartCad { status = "ready" }), 0, 15f), "landed; its variant is being rebuilt");
            Assert.AreEqual(CadStep.Swap, CadUpgrade.Decide(shown, A("scad", by: "xai:grok-4.20-0309-non-reasoning + openscad", cad: new PartCad { status = "ready" }), 0, 30f));
            Assert.AreEqual(CadStep.Wait, CadUpgrade.Decide(shown, A("scad", status: "pending"), 0, 30f), "a CAD answer that isn't ready yet");
            Assert.AreEqual(CadStep.Stop, CadUpgrade.Decide(shown, A("llm", cad: new PartCad { status = "failed", reason = "didn't build" }), 0, 30f));
            Assert.AreEqual(CadStep.Stop, CadUpgrade.Decide(shown, A("llm", cad: Writing(10f)), 0, CadUpgrade.GiveUpAfter + 1f), "too long");
            Assert.AreEqual(CadStep.Swap, CadUpgrade.Decide(shown, A("scad"), 0, CadUpgrade.GiveUpAfter + 1f), "a late CAD model is still taken");
            // a quiet server: no answer, or no status ("none": the poll itself restarts a lost run) — a few tries, then stop
            Assert.AreEqual(CadStep.Wait, CadUpgrade.Decide(shown, null, 0, 15f));
            Assert.AreEqual(CadStep.Wait, CadUpgrade.Decide(shown, A("llm", cad: new PartCad { status = "none" }), CadUpgrade.MaxQuiet - 2, 15f));
            Assert.AreEqual(CadStep.Stop, CadUpgrade.Decide(shown, null, CadUpgrade.MaxQuiet - 1, 15f));
            Assert.IsTrue(CadUpgrade.Quiet(null) && CadUpgrade.Quiet(A("llm")) && !CadUpgrade.Quiet(A("llm", cad: Writing(5f))));
            // the part moved on
            Assert.AreEqual(CadStep.Drop, CadUpgrade.Decide(A("ai_mesh", "hf"), A("scad"), 0, 15f), "Compare flipped it to HF");
            Assert.AreEqual(CadStep.Drop, CadUpgrade.Decide(A("scad"), A("scad"), 0, 15f), "CAD already");
            Assert.AreEqual(CadStep.Drop, CadUpgrade.Decide(null, A("scad"), 0, 15f), "gone");
        }

        [Test]
        public void TheWriterName_IsShort()
        {
            Assert.AreEqual("Grok 4.20", AssetModes.WriterName("xai:grok-4.20-0309-non-reasoning + openscad"));
            Assert.AreEqual("Grok 4.20", AssetModes.WriterName("xai:grok-4.20-0309-reasoning"));
            Assert.AreEqual("Grok 4.7", AssetModes.WriterName("xai:grok-4.7 + openscad"));
            Assert.AreEqual("Grok", AssetModes.WriterName(null));
            Assert.AreEqual("Grok", AssetModes.WriterName("xai:grok-build"));
            Assert.AreEqual("Grok 4.3", AssetModes.WriterName("grok-4.3"));
            Assert.AreEqual("writing 40 s", CadUpgrade.Describe(Writing(37f), 0f));
            Assert.AreEqual("failed: no OpenSCAD", CadUpgrade.Describe(new PartCad { status = "failed", reason = "no OpenSCAD" }, 0f));
        }
    }
}
