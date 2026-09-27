using System.Collections.Generic;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Tools
{
    /// The in-progress measurement: placed points (scene-root space) plus an optional live preview point.
    public class MeasureSession
    {
        readonly List<Vector3> m_Points = new List<Vector3>(MeasureMath.MaxPoints);
        readonly List<SnapKind> m_Kinds = new List<SnapKind>(MeasureMath.MaxPoints);

        public IReadOnlyList<Vector3> Points => m_Points;
        public IReadOnlyList<SnapKind> Kinds => m_Kinds;
        public int Count => m_Points.Count;
        public bool IsFull => m_Points.Count >= MeasureMath.MaxPoints;
        public Vector3? Preview { get; set; }

        public bool Add(Vector3 p, SnapKind kind)
        {
            if (IsFull) return false;
            m_Points.Add(p);
            m_Kinds.Add(kind);
            return true;
        }

        public void Set(int index, Vector3 p, SnapKind kind)
        {
            m_Points[index] = p;
            m_Kinds[index] = kind;
        }

        public bool RemoveLast()
        {
            if (m_Points.Count == 0) return false;
            m_Points.RemoveAt(m_Points.Count - 1);
            m_Kinds.RemoveAt(m_Kinds.Count - 1);
            return true;
        }

        public void Clear()
        {
            m_Points.Clear();
            m_Kinds.Clear();
            Preview = null;
        }

        /// Points to draw/measure right now: placed points, plus the preview point while the shape isn't full.
        public List<Vector3> LivePoints(List<Vector3> buffer = null)
        {
            buffer ??= new List<Vector3>(MeasureMath.MaxPoints);
            buffer.Clear();
            buffer.AddRange(m_Points);
            if (Preview.HasValue && !IsFull && m_Points.Count > 0) buffer.Add(Preview.Value);
            return buffer;
        }

        public bool TryMeasureLive(out PointMeasurement m, List<Vector3> buffer = null)
        {
            var pts = LivePoints(buffer);
            m = default;
            if (pts.Count < MeasureMath.MinPoints) return false;
            m = MeasureMath.Measure(pts);
            return true;
        }
    }
}
