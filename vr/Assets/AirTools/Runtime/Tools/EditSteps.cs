using System.Collections.Generic;

namespace AirTools.Tools
{
    /// delete-undo: the edit-time ranges of steps made of several edits (EditHistory.Step), so Undo / Redo take a whole
    /// step at once, across tools. Stamps are strictly increasing (EditHistory.Stamp), so a range [first, last] holds
    /// exactly the edits stamped inside that step and no other. Pure (EditMode-tested offline).
    public sealed class EditSteps
    {
        /// Ranges kept (the oldest go first; a step that old is long past undoing in one go).
        public const int Max = 256;

        readonly List<(float first, float last)> m_Ranges = new List<(float, float)>();

        public int Count => m_Ranges.Count;

        /// A step that stamped from `first` to `last`. One stamp (or none) is not a group: nothing to remember.
        public void Add(float first, float last)
        {
            if (!(last > first)) return;
            m_Ranges.Add((first, last));
            if (m_Ranges.Count > Max) m_Ranges.RemoveAt(0);
        }

        /// The step `at` belongs to (false: none, it was an edit of its own).
        public bool TryFind(float at, out float first, out float last)
        {
            for (int i = m_Ranges.Count - 1; i >= 0; i--)
            {
                var r = m_Ranges[i];
                if (at >= r.first && at <= r.last) { first = r.first; last = r.last; return true; }
                if (r.last < at) break;   // ranges are in time order: nothing older can hold it
            }
            first = last = float.NaN;
            return false;
        }

        /// Edits at `a` and `b` were made in one step.
        public bool Same(float a, float b) => TryFind(a, out float first, out float last) && b >= first && b <= last;

        public void Clear() => m_Ranges.Clear();
    }
}
