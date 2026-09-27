using System;
using System.Collections.Generic;
using AirTools.Tools;
using AirTools.UI;
using NUnit.Framework;

namespace AirTools.Tests
{
    /// delete-undo: the pure parts — EditSteps (the stamp ranges of multi-edit steps), EditHistory's step walk across tools
    /// (with fake tools and a pinned clock) and the removal copy. No engine calls: runs in the offline runner as well as the
    /// Editor. DeleteUndoSceneTests (Editor gate) drives the real PartTool: the spec card's Remove, voice, an array member,
    /// Clear, drops, a model swapped in a gap and "put it back".
    public class DeleteUndoTests
    {
        // ---------------- EditSteps ----------------

        [Test]
        public void Steps_OneRangePerStep_SingleStampsAreNotSteps()
        {
            var s = new EditSteps();
            s.Add(1f, 1f);
            Assert.AreEqual(0, s.Count, "one stamp: nothing to group");
            s.Add(2f, 1f);
            Assert.AreEqual(0, s.Count, "backwards: ignored");
            s.Add(1f, 1.02f);
            s.Add(1.05f, 1.07f);
            Assert.AreEqual(2, s.Count);
            Assert.IsTrue(s.Same(1f, 1.02f));
            Assert.IsTrue(s.Same(1.01f, 1f), "inside the range");
            Assert.IsFalse(s.Same(1.02f, 1.05f), "two steps");
            Assert.IsFalse(s.Same(0.99f, 1f), "before it");
            Assert.IsFalse(s.Same(1.1f, 1.1f), "no step at all: not grouped with itself");
            Assert.IsTrue(s.TryFind(1.06f, out float first, out float last));
            Assert.AreEqual(1.05f, first);
            Assert.AreEqual(1.07f, last);
            s.Clear();
            Assert.AreEqual(0, s.Count);
        }

        [Test]
        public void Steps_KeepTheNewestMax()
        {
            var s = new EditSteps();
            for (int i = 0; i < EditSteps.Max + 10; i++) s.Add(i, i + 0.5f);
            Assert.AreEqual(EditSteps.Max, s.Count);
            Assert.IsFalse(s.Same(0f, 0.5f), "the oldest went");
            Assert.IsTrue(s.Same(EditSteps.Max + 9f, EditSteps.Max + 9.5f));
        }

        // ---------------- EditHistory: steps across tools ----------------

        /// A tool with a stack of stamped edits (EditHistory.Stamp for every entry, like the real ones).
        sealed class Fake : IEditable
        {
            public readonly string Name;
            public readonly List<(string edit, float at)> Done = new List<(string, float)>(), Undone = new List<(string, float)>();
            public Fake(string name) { Name = name; }
            public void Edit(string what) { Done.Add((what, EditHistory.Stamp())); DropRedo(); EditHistory.Edited(this); }
            public bool CanUndo => Done.Count > 0;
            public bool CanRedo => Undone.Count > 0;
            public float LastEditAt => Done.Count > 0 ? Done[Done.Count - 1].at : float.NegativeInfinity;
            public float LastUndoAt => Undone.Count > 0 ? Undone[Undone.Count - 1].at : float.NegativeInfinity;
            public void Undo() { var e = Done[Done.Count - 1]; Done.RemoveAt(Done.Count - 1); Undone.Add((e.edit, EditHistory.Stamp())); }
            public void Redo() { var e = Undone[Undone.Count - 1]; Undone.RemoveAt(Undone.Count - 1); Done.Add((e.edit, EditHistory.Stamp())); }
            public void DropRedo() => Undone.Clear();
            public string State => $"{Name}: [{string.Join(", ", Done.ConvertAll(d => d.edit))}] redo [{string.Join(", ", Undone.ConvertAll(d => d.edit))}]";
        }

        /// A part in hand: an edit in progress counts as now (PartTool.LastEditAt with Held).
        sealed class Holding : IEditable
        {
            public bool Held = true;
            public bool CanUndo => Held;
            public bool CanRedo => false;
            public float LastEditAt => Held ? EditHistory.Now : float.NegativeInfinity;
            public float LastUndoAt => float.NegativeInfinity;
            public void Undo() => Held = false;
            public void Redo() { }
            public void DropRedo() { }
        }

        /// Fakes registered and the clock pinned (offline there's no engine clock); both undone afterwards.
        static void With(Action<Fake, Fake> test)
        {
            var clock = EditHistory.Clock;
            EditHistory.Clock = () => 0f;
            var a = new Fake("parts");
            var b = new Fake("scene");
            EditHistory.Register(a);
            EditHistory.Register(b);
            try { test(a, b); }
            finally
            {
                EditHistory.Unregister(a);
                EditHistory.Unregister(b);
                EditHistory.Clock = clock;
            }
        }

        [Test]
        public void History_EditsOutsideAStepAreOneEach()
        {
            With((a, b) =>
            {
                a.Edit("place A");
                b.Edit("take out dw1");
                a.Edit("place B");
                Assert.AreEqual(1, EditHistory.UndoStep());
                Assert.AreEqual(1, a.Done.Count, a.State);
                Assert.AreEqual(1, EditHistory.UndoStep());
                Assert.AreEqual(0, b.Done.Count, b.State);
                Assert.AreEqual(1, EditHistory.RedoStep());
                Assert.AreEqual(1, b.Done.Count, "redo: newest undo first");
            });
        }

        /// "Put the dishwasher back": the model out (parts) and the dishwasher in (scene) are one step each way.
        [Test]
        public void History_AStepAcrossToolsUndoesAndRedoesTogether()
        {
            With((a, b) =>
            {
                a.Edit("place model");
                using (EditHistory.Step())
                {
                    a.Edit("delete model");
                    b.Edit("restore dw1");
                }
                Assert.AreEqual(2, EditHistory.UndoStep(), $"{a.State} | {b.State}");
                Assert.AreEqual(new[] { "place model" }, a.Done.ConvertAll(d => d.edit).ToArray(), "the model's placement stays");
                Assert.AreEqual(0, b.Done.Count);
                Assert.AreEqual(2, EditHistory.RedoStep(), $"{a.State} | {b.State}");
                Assert.AreEqual(2, a.Done.Count);
                Assert.AreEqual(1, b.Done.Count);
                Assert.AreEqual(2, EditHistory.UndoStep(), "and again: the redo's stamps are one step too");
                Assert.AreEqual(1, EditHistory.UndoStep(), "then the placement alone");
                Assert.AreEqual(0, a.Done.Count);
            });
        }

        /// A model swapped in a gap: the old one out and the new one in, both in the part tool, one step.
        [Test]
        public void History_AStepInOneToolUndoesAsOne()
        {
            With((a, b) =>
            {
                a.Edit("model 1");
                using (EditHistory.Step()) { a.Edit("delete model 1"); a.Edit("model 2"); }
                using (EditHistory.Step()) { a.Edit("delete model 2"); a.Edit("model 3"); }
                Assert.AreEqual(2, EditHistory.UndoStep());
                Assert.AreEqual("model 2", a.Done[a.Done.Count - 1].edit, a.State);
                Assert.AreEqual(2, EditHistory.UndoStep());
                Assert.AreEqual("model 1", a.Done[a.Done.Count - 1].edit, a.State);
                Assert.AreEqual(2, EditHistory.RedoStep());
                Assert.AreEqual("model 2", a.Done[a.Done.Count - 1].edit, a.State);
                Assert.AreEqual(2, a.Undone.Count, "one swap left to redo: its two edits");
            });
        }

        [Test]
        public void History_StepsNest_TheOutermostIsTheStep()
        {
            With((a, b) =>
            {
                using (EditHistory.Step())
                {
                    a.Edit("1");
                    using (EditHistory.Step()) { b.Edit("2"); }
                    a.Edit("3");
                }
                Assert.AreEqual(3, EditHistory.UndoStep());
                Assert.AreEqual(0, a.Done.Count + b.Done.Count);
                Assert.AreEqual(3, EditHistory.RedoStep());
            });
        }

        [Test]
        public void History_AnEmptyOrSingleStepChangesNothing()
        {
            With((a, b) =>
            {
                int steps = EditHistory.StepCount;
                using (EditHistory.Step()) { }
                using (EditHistory.Step()) { a.Edit("alone"); }
                Assert.AreEqual(steps, EditHistory.StepCount, "nothing to group");
                b.Edit("other");
                Assert.AreEqual(1, EditHistory.UndoStep());
                Assert.AreEqual(1, EditHistory.UndoStep());
                Assert.AreEqual(0, a.Done.Count + b.Done.Count);
            });
        }

        /// A part in hand is newer than every finished edit, and never part of a finished step (EditHistory.Now): its
        /// Undo (put it back) doesn't take the step before it along.
        [Test]
        public void History_AnEditInProgressIsNewestAndNeverInAStep()
        {
            With((a, b) =>
            {
                using (EditHistory.Step()) { a.Edit("delete model"); b.Edit("restore dw1"); }
                var held = new Holding();
                EditHistory.Register(held);
                try
                {
                    Assert.Greater(held.LastEditAt, b.LastEditAt, "in progress: after the last stamp");
                    Assert.IsFalse(EditHistory.SameStep(b.LastEditAt, held.LastEditAt));
                    Assert.AreEqual(1, EditHistory.UndoStep(), "only the part in hand");
                    Assert.IsFalse(held.Held);
                    Assert.AreEqual(2, a.Done.Count + b.Done.Count, "the step is still there");
                    Assert.AreEqual(2, EditHistory.UndoStep());
                }
                finally { EditHistory.Unregister(held); }
            });
        }

        [Test]
        public void History_StampsStayStrictlyIncreasingWithAPinnedClock()
        {
            With((a, b) =>
            {
                float s1 = EditHistory.Stamp(), s2 = EditHistory.Stamp();
                Assert.Greater(s2, s1);
                float now = EditHistory.Now;
                Assert.Greater(now, s2, "in progress: after the last stamp");
                Assert.Greater(EditHistory.Stamp(), now, "the next stamp: after it");
            });
        }

        // ---------------- copy ----------------

        [Test]
        public void Copy_RemovalsSayTheyCanBeUndone()
        {
            Assert.AreEqual("Hanger removed · Undo on the ring", Copy.PartRemoved("hanger"));
            Assert.AreEqual("Part removed · Undo on the ring", Copy.PartRemoved("  "));
            Assert.AreEqual("1 part cleared · Undo on the ring", Copy.PartsCleared(1));
            Assert.AreEqual("8 parts cleared · Undo on the ring", Copy.PartsCleared(8));
        }
    }
}
