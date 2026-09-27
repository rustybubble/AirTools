using System.Linq;
using AirTools.Agent;
using AirTools.Agent.Grok;
using AirTools.Dev;
using AirTools.UI;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// Lane G3 checks that need the Editor (the handler registry reflects over every Runtime type, which loads the Meta
    /// and glTFast assemblies; the handlers log through Debug): run in the gate, not offline.
    public class GrokPanelsGateTests
    {
        [Test]
        public void EveryActionHasARegisteredHandler()
        {
            foreach (var name in GrokPanelActions.Names)
            {
                Assert.IsTrue(AgentActions.Handlers.ContainsKey(name), $"[AgentAction(\"{name}\")]");
                Assert.IsTrue(AgentActions.IsKnown(name));
            }
            // Only G2 registers show_survey (condition pins, else the B1 card); G3 never does.
            if (AgentActions.Handlers.TryGetValue("show_survey", out var survey))
                Assert.AreEqual("GrokOverlayActions", survey.Method.DeclaringType?.Name, "only G2 registers show_survey");
        }

        [Test]
        public void SafetyActionStoresTheVerdictWithoutAScene()
        {
            GrokState.ResetG3();
            try
            {
                Assert.IsTrue(AgentActions.Execute(GrokG3Examples.Action(GrokG3Examples.SafetyRecalled)));
                Assert.AreEqual("recalled", GrokState.VerdictFor("midea-maw08u1qwt-8d6d08"));
                Assert.IsTrue(AgentActions.Execute(GrokG3Examples.Action(GrokG3Examples.SafetyClear)));
                Assert.AreEqual("clear", GrokState.VerdictFor("lg-knsah121b-04da2e"));
            }
            finally { GrokState.ResetG3(); }
        }

        [Test]
        public void PanelActionsWithoutTheirViewSayNo()
        {
            // No GrokCard / ReimagineQuad in an empty edit-mode world: the action is refused (and logged), not dropped.
            Assert.IsFalse(AgentActions.Execute(GrokG3Examples.Action(GrokG3Examples.Postcard)));
            Assert.IsFalse(AgentActions.Execute(GrokG3Examples.Action(GrokG3Examples.ReimaginedKitchen)));
            Assert.IsFalse(AgentActions.Execute(new AgentAction { name = "show_postcard" }), "no args");
        }

        [Test]
        public void QrTextureHasTheQuietZoneAndPointFiltering()
        {
            var q = QrCode.Encode("http://127.0.0.1:8000/report/demo");
            var tex = q.ToTexture(QrCode.QuietZone, 4);
            try
            {
                Assert.AreEqual((q.Size + 8) * 4, tex.width);
                Assert.AreEqual(FilterMode.Point, tex.filterMode);
                Assert.AreEqual(Color.white, (Color)tex.GetPixel(0, 0), "quiet zone");
                Assert.AreEqual(Color.black, (Color)tex.GetPixel(4 * 4, tex.height - 1 - 4 * 4), "finder corner (top-left module)");
            }
            finally { Object.DestroyImmediate(tex); }
        }
    }
}
