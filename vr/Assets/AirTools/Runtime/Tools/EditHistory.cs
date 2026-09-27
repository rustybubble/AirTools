using UnityEngine;
using System;
using System.Collections.Generic;

namespace AirTools.Tools
{
    /// A tool whose edits can be undone and redone (measure, level, parts).
    public interface IEditable
    {
        bool CanUndo { get; }
        bool CanRedo { get; }
        /// When its newest still-undoable edit happened (unscaled seconds); −∞ with nothing to undo.
        float LastEditAt { get; }
        /// When its newest still-redoable undo happened; −∞ with nothing to redo.
        float LastUndoAt { get; }
        void Undo();
        void Redo();
        /// A new edit happened somewhere: forget the undone future.
        void DropRedo();
    }

    /// Undo / Redo across tools (the tool ring's buttons work in any mode, Move included). Each tool keeps its own
    /// stacks; Undo goes to the tool with the most recent edit, Redo to the one with the most recent undo, and a new
    /// edit anywhere clears every tool's redo stack.
    public static class EditHistory
    {
        static float s_LastStamp = float.NegativeInfinity;

        /// A strictly increasing edit time for IEditable histories: Time.unscaledTime, or 10 ms after the previous stamp when
        /// two edits land in one frame (a voice reply's remove_component + place_part, or EditMode tests where time stands
        /// still). LastEditAt ties made Undo revert the first-registered tool's edit instead of the newest.
        public static float Stamp()
        {
            s_LastStamp = Mathf.Max(Clock(), s_LastStamp + 0.01f);
            if (s_StepDepth > 0 && float.IsNaN(s_StepFirst)) s_StepFirst = s_LastStamp;   // delete-undo
            return s_LastStamp;
        }

        /// The current edit time, without advancing it (for "an edit in progress counts as now"). delete-undo: just after
        /// every stamp so far (an edit in progress is newer than every finished one, and never inside a finished Step).
        public static float Now => Mathf.Max(Clock(), s_LastStamp + 0.001f);

        /// delete-undo: the clock edits are stamped with (Time.unscaledTime). Offline tests pin it: they run without the engine.
        public static Func<float> Clock = () => Time.unscaledTime;

        static readonly List<IEditable> s_Tools = new List<IEditable>();

        /// Undo / redo availability may have changed (the ring dims its buttons).
        public static event Action Changed;

        public static void Register(IEditable tool) { if (tool != null && !s_Tools.Contains(tool)) s_Tools.Add(tool); }
        public static void Unregister(IEditable tool) => s_Tools.Remove(tool);

        public static bool CanUndo { get { foreach (var t in s_Tools) if (t.CanUndo) return true; return false; } }
        public static bool CanRedo { get { foreach (var t in s_Tools) if (t.CanRedo) return true; return false; } }

        /// `source` made a new edit.
        public static void Edited(IEditable source)
        {
            foreach (var t in s_Tools) if (t != source && t.CanRedo) t.DropRedo();
            Changed?.Invoke();
        }

        public static void NotifyChanged() => Changed?.Invoke();

        public static bool Undo()
        {
            if (UndoStep() == 0) return false;   // delete-undo: the newest edit and the rest of its step
            Changed?.Invoke();
            AirTools.UI.FeedbackEvents.Undo();
            return true;
        }

        public static bool Redo()
        {
            if (RedoStep() == 0) return false;   // delete-undo: the newest undo and the rest of its step
            Changed?.Invoke();
            AirTools.UI.FeedbackEvents.Redo();
            return true;
        }

        // delete-undo ---------------------------------------------------------------------------------------------------
        // One action that edits more than once is one Undo / Redo step: a model swapped in a gap (the old one out, the new
        // one in), "put the dishwasher back" (the model out of its gap, the dishwasher in), a place_part that replaces the
        // model there. The edits stamped inside Step() are grouped (EditSteps); Undo / Redo take the whole group, newest
        // first, across tools. An undo's own stamps (the tools' redo entries) are grouped the same way, so Redo takes them
        // back together, and so on. Edits outside a Step are one step each, as before.

        static int s_StepDepth;
        static float s_StepFirst = float.NaN;
        static readonly EditSteps s_Steps = new EditSteps();

        /// Edits in one Undo / Redo step at most (a guard: a step is a handful).
        public const int MaxStepEdits = 64;

        /// `using (EditHistory.Step()) { … }`: every edit stamped inside is one undo step. Scopes nest (the outermost one
        /// is the step).
        public static StepScope Step()
        {
            s_StepDepth++;
            return new StepScope(true);
        }

        public readonly struct StepScope : IDisposable
        {
            readonly bool m_Open;
            public StepScope(bool open) => m_Open = open;
            public void Dispose() { if (m_Open) EndStep(); }
        }

        static void EndStep()
        {
            if (s_StepDepth == 0 || --s_StepDepth > 0) return;
            if (!float.IsNaN(s_StepFirst)) s_Steps.Add(s_StepFirst, s_LastStamp);
            s_StepFirst = float.NaN;
        }

        /// Edits at times `a` and `b` were made in one step.
        public static bool SameStep(float a, float b) => s_Steps.Same(a, b);

        /// Multi-edit steps remembered (the harness logs it).
        public static int StepCount => s_Steps.Count;

        static IEditable Newest(bool undo)
        {
            IEditable best = null;
            foreach (var t in s_Tools)
            {
                if (undo) { if (t.CanUndo && (best == null || t.LastEditAt > best.LastEditAt)) best = t; }
                else if (t.CanRedo && (best == null || t.LastUndoAt > best.LastUndoAt)) best = t;
            }
            return best;
        }

        /// Undo the newest edit and the rest of its step, newest first, across tools; how many edits went back (0:
        /// nothing to undo). No events or sounds (Undo adds them).
        public static int UndoStep() => Walk(undo: true);

        /// Redo the newest undo and the rest of its step; how many edits came back.
        public static int RedoStep() => Walk(undo: false);

        static int Walk(bool undo)
        {
            var best = Newest(undo);
            if (best == null) return 0;
            float at = undo ? best.LastEditAt : best.LastUndoAt;
            int n = 0;
            using (Step())   // the stamps this takes (the other stack's entries) are one step too
            {
                if (undo) best.Undo(); else best.Redo();
                n++;
                for (int guard = 1; guard < MaxStepEdits; guard++)
                {
                    var next = Newest(undo);
                    if (next == null) break;
                    float t = undo ? next.LastEditAt : next.LastUndoAt;
                    if (!s_Steps.Same(at, t)) break;
                    if (undo) next.Undo(); else next.Redo();
                    n++;
                    // It couldn't take that edit back: stop rather than spin.
                    if (undo ? next.CanUndo && next.LastEditAt == t : next.CanRedo && next.LastUndoAt == t) break;
                }
            }
            return n;
        }
        // end delete-undo -----------------------------------------------------------------------------------------------
    }
}
