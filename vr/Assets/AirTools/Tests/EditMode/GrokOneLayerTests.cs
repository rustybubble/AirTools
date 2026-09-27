using AirTools.Agent.Grok;
using AirTools.Dev;
using AirTools.Scene;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// Declutter S8 (DC6, M10): one Grok world layer at a time. GrokOverlays on a bare scene root with the kitchen's
    /// payloads (no package cameras, so no live labels here: the harness's G2.one_layer covers show_labels → show_plan).
    public class GrokOneLayerTests
    {
        GameObject m_Root, m_App;
        GrokOverlays m_Overlays;

        static JObject J(string json) => JObject.Parse(json);

        [SetUp]
        public void Build()
        {
            m_Root = new GameObject("SceneRoot");
            var root = m_Root.AddComponent<SceneRoot>();
            var content = new GameObject("Content");
            content.transform.SetParent(m_Root.transform, false);
            root.SetRuntimeContent("kitchen", SceneManifest.Parse(@"{""mesh"":{""file"":""mesh.r1.glb""},""revision"":1,""gravity_residual_deg"":0.0}"), null, content, null);
            m_App = new GameObject("grok-overlays");
            m_Overlays = m_App.AddComponent<GrokOverlays>();
            m_Overlays.sceneRoot = root;
        }

        [TearDown]
        public void Teardown()
        {
            Object.DestroyImmediate(m_App);
            Object.DestroyImmediate(m_Root);
            GrokState.ResetG2();
        }

        [Test]
        public void APlanReplacesThePins_ASurveyReplacesThePlan_TheScanLabelsStay()
        {
            var o = m_Overlays;
            Assert.IsTrue(o.ShowSurvey(SurveyView.Parse(J(GrokG2Fixtures.KitchenShowSurvey))));
            Assert.IsTrue(o.ShowSceneLabels(LabelsView.Parse(J(GrokG2Fixtures.KitchenSceneLabels))));
            Assert.AreEqual("Survey,SceneLabels", o.ShownLayers(), "the scan's labels don't replace a Grok layer");

            Assert.IsTrue(o.ShowPlan(PlanView.Parse(J(GrokG2Fixtures.KitchenPlanLed))));
            Assert.IsFalse(o.IsShown(GrokOverlayKind.Survey), "the plan replaces the pins");
            Assert.IsTrue(o.IsShown(GrokOverlayKind.SceneLabels), "Settings ▸ Labels stays on");
            Assert.AreEqual("Plan,SceneLabels", o.ShownLayers());
            Assert.AreEqual("51815765bd49", GrokState.SurveyId, "the survey's id stays for the agent (\"find a fix for pin f1\")");

            Assert.IsTrue(o.ShowSurvey(SurveyView.Parse(J(GrokG2Fixtures.KitchenShowSurvey))));
            Assert.IsFalse(o.IsShown(GrokOverlayKind.Plan), "a survey replaces the plan");
            Assert.IsNull(o.Plan);
            Assert.IsNull(GrokState.PlanId, "place_array can't match a plan that's gone");
            Assert.AreEqual("Survey,SceneLabels", o.ShownLayers());

            o.SetSceneLabels(false);
            Assert.AreEqual("Survey", o.ShownLayers(), "turning the scan's labels off leaves the Grok layer");
        }
    }
}
