using UnityEngine;

namespace AirTools.Parts
{
    /// gaze-catalog: the head ray's focus, smoothed (pure). Each sample adds its time to its own focus's evidence and
    /// takes as much from every other; a focus becomes the held one when its evidence reaches HoldSeconds (NoneSeconds to
    /// fall back to none: looking at the sky for a moment keeps the roof). A glance shorter than the hold never changes it,
    /// and one stray sample in a run only delays the change. The first focus after a reset is taken at FirstSeconds.
    /// No allocation.
    public sealed class GazeFocusFilter
    {
        public float HoldSeconds = 1.2f, NoneSeconds = 2.5f, FirstSeconds = 0.5f;
        /// A sample farther apart than this counts as this long (a paused app, a hitch).
        public float MaxStep = 0.5f;

        readonly float[] m_Evidence = new float[CatalogFoci.Count];
        float m_Last = float.NaN, m_SubjectFor;
        string m_PendingSubject;
        bool m_Any;

        public CatalogFocus Held { get; private set; }
        /// The held focus's subject ("the dishwasher"; null: the focus's own name).
        public string Subject { get; private set; }
        /// The last sample's focus and subject.
        public CatalogFocus Raw { get; private set; }
        public string RawSubject { get; private set; }
        public int Changes { get; private set; }

        public float Evidence(CatalogFocus f) => m_Evidence[(int)f];

        public void Reset()
        {
            for (int i = 0; i < m_Evidence.Length; i++) m_Evidence[i] = 0f;
            m_Last = float.NaN;
            m_Any = false;
            Held = CatalogFocus.None;
            Subject = null;
            Raw = CatalogFocus.None;
            RawSubject = null;
            m_PendingSubject = null;
            m_SubjectFor = 0f;
        }

        /// A sample at `now` (s). True when the held focus (or its subject) changed.
        public bool Update(CatalogFocus sample, string subject, float now)
        {
            float dt = float.IsNaN(m_Last) ? 0.25f : Mathf.Clamp(now - m_Last, 0f, MaxStep);
            m_Last = now;
            Raw = sample;
            RawSubject = subject;
            int s = (int)sample;
            for (int i = 0; i < m_Evidence.Length; i++)
                m_Evidence[i] = i == s ? m_Evidence[i] + dt : Mathf.Max(0f, m_Evidence[i] - dt);
            if (sample == Held)
            {
                // Same focus: a new subject (the dishwasher → the fridge, both the floor's) follows once it has held for
                // half the hold (the chip's words only; the catalog stays).
                if (subject == Subject) { m_SubjectFor = 0f; return false; }
                m_SubjectFor = subject == m_PendingSubject ? m_SubjectFor + dt : dt;
                m_PendingSubject = subject;
                if (m_SubjectFor + 1e-4f < HoldSeconds * 0.5f) return false;
                Subject = subject;
                m_SubjectFor = 0f;
                Changes++;
                return true;
            }
            float need = !m_Any ? FirstSeconds : sample == CatalogFocus.None ? NoneSeconds : HoldSeconds;
            if (!m_Any && sample == CatalogFocus.None) return false;   // nothing seen yet: none stays none
            if (m_Evidence[s] + 1e-4f < need) return false;
            Held = sample;
            Subject = sample == CatalogFocus.None ? null : subject;
            m_SubjectFor = 0f;
            m_Any = true;
            for (int i = 0; i < m_Evidence.Length; i++) if (i != s) m_Evidence[i] = 0f;
            Changes++;
            return true;
        }
    }
}
