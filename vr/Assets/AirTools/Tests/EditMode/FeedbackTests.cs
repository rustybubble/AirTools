using System.IO;
using System.Linq;
using AirTools.Dev;
using AirTools.Tools;
using AirTools.UI;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// UX W0.5 "every commit answers" (acceptance A11 / A12): each commit goes through the feedback hub, and inputs that
    /// did nothing say why (InputHints, one per 4 s).
    public class FeedbackTests : LevelFixture
    {
        [SetUp]
        public void ResetHub() { FeedbackEvents.ResetCounts(); InputHints.Reset(); }

        [Test]
        public void EveryCommitCallsTheFeedbackHub()
        {
            Level.Equip(false);
            Tool.Equip(true);
            EditHistory.Register(Tool); EditHistory.Register(Level);
            try
            {
                var win = MeasureScenarios.M2().First(s => s.Id == "M2.measure.window.width");
                var r = MeasureScenarios.Run(win, Tool, Hub, null, 1);
                Assert.IsTrue(r.Passed, r.Detail);
                Assert.AreEqual(2, FeedbackEvents.Count(Feedback.Point), "a pip per point");
                Assert.AreEqual(1, FeedbackEvents.Count(Feedback.Saved), "the save arpeggio");
                Tool.Equip(false);
                Level.Equip(true);
                LevelScenarios.Run(LevelScenarios.M3()[0], Level, Hub, null, 1);
                Assert.AreEqual(1, FeedbackEvents.Count(Feedback.Level));
                Assert.IsTrue(EditHistory.Undo());
                Assert.AreEqual(1, FeedbackEvents.Count(Feedback.Undo));
                Assert.IsTrue(EditHistory.Redo());
                Assert.AreEqual(1, FeedbackEvents.Count(Feedback.Redo));
            }
            finally { EditHistory.Unregister(Tool); EditHistory.Unregister(Level); }
        }

        /// UX W0.10: controller A is the global undo — it undoes a level while Move is in hand (tools put away).
        [Test]
        public void ControllerAUndoesALevelWhileInMove()
        {
            EditHistory.Register(Tool); EditHistory.Register(Level);
            try
            {
                LevelScenarios.Run(LevelScenarios.M3()[0], Level, Hub, null, 1);
                Assert.AreEqual(1, Level.Placements.Count);
                Level.Equip(false); Tool.Equip(false);             // Move: no tool in hand
                Assert.IsTrue(AirTools.Input.OvrToolInputSource.UndoButton());
                Assert.AreEqual(0, Level.Placements.Count, "undone from Move");
                Assert.IsFalse(AirTools.Input.OvrToolInputSource.UndoButton(), "nothing left: says so");
            }
            finally { EditHistory.Unregister(Tool); EditHistory.Unregister(Level); }
        }

        [Test]
        public void MissesSayWhyOncePerInterval()
        {
            Level.Equip(false);
            Tool.Equip(true);
            var sky = MeasureScenarios.SpawnEye + new Vector3(0f, 5f, 1f);
            Click(MeasureScenarios.SpawnEye, sky);
            Assert.AreEqual(0, InputHints.Shown, "one miss can be a slip: no words yet");
            Click(MeasureScenarios.SpawnEye, sky);
            Assert.AreEqual(1, InputHints.Shown);
            Assert.AreEqual("Aim at a surface to measure", InputHints.Last);
            Assert.AreEqual("miss", Tool.LastAction, "LastAction stays raw");
            Click(MeasureScenarios.SpawnEye, sky);
            Click(MeasureScenarios.SpawnEye, sky);
            Assert.AreEqual(1, InputHints.Shown, "at most one hint per interval");
            Assert.GreaterOrEqual(FeedbackEvents.Count(Feedback.Miss), 4, "every miss still answers (sound / haptic)");
        }

        [Test]
        public void LevelMissSaysPointAtAFlatSurface()
        {
            Hub.RaisePressStart(AirTools.Input.ToolHand.Right, From(MeasureScenarios.SpawnEye, MeasureScenarios.SpawnEye + Vector3.up));
            Hub.RaisePressEnd(AirTools.Input.ToolHand.Right, From(MeasureScenarios.SpawnEye, MeasureScenarios.SpawnEye + Vector3.up));
            Assert.AreEqual("Point at a flat surface", InputHints.Last);
        }

        /// No AudioSource.PlayClipAtPoint left in the app (a GameObject per call, no HRTF): everything goes through SfxPlayer.
        [Test]
        public void NoPlayClipAtPoint()
        {
            var offenders = Directory.GetFiles("Assets/AirTools/Runtime", "*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadAllLines(f).Any(l => l.Contains("PlayClipAtPoint(") && !l.TrimStart().StartsWith("//")))
                .ToList();
            Assert.IsEmpty(offenders, string.Join(", ", offenders));
        }
    }
}
