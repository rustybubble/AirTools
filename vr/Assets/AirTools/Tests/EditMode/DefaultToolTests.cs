using AirTools.Core;
using AirTools.Tools;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// D1, reversed 2026-09-27 (user, on the headset: "measure is really annoying to have as a default"): Move is in hand
    /// on entering the world and again right after a part lands; Measure is one ring detent away.
    public class DefaultToolTests
    {
        GameObject m_Go;
        ToolManager m_Tools;

        [SetUp]
        public void SetUp()
        {
            AppState.Reset();
            m_Go = new GameObject("tools");
            m_Tools = m_Go.AddComponent<ToolManager>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(m_Go);
            AppState.Reset();
        }

        [Test]
        public void TheDefaultIsMove()
        {
            Assert.AreEqual(ToolKind.Move, ToolManager.Default);
            m_Tools.ResetToDefault();
            Assert.AreEqual(ToolKind.Move, m_Tools.Active);
        }

        [Test]
        public void AfterAPlacement_MoveIsInHand_OnlyInTheWorld()
        {
            AppState.Set(AppMode.World);
            m_Tools.Equip(ToolKind.Part);   // the part was in hand
            Assert.IsTrue(m_Tools.AfterPlace());
            Assert.AreEqual(ToolKind.Move, m_Tools.Active);
            Assert.IsFalse(m_Tools.AfterPlace(), "already Move: nothing to do");

            m_Tools.Equip(ToolKind.Measure);   // Grok's place_part after taping the gap
            Assert.IsTrue(m_Tools.AfterPlace());
            Assert.AreEqual(ToolKind.Move, m_Tools.Active);

            AppState.Set(AppMode.Tabletop);   // Model view keeps what you had
            m_Tools.Equip(ToolKind.Measure);
            Assert.IsFalse(m_Tools.AfterPlace());
            Assert.AreEqual(ToolKind.Measure, m_Tools.Active);
        }
    }
}
