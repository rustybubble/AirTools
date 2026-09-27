using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// Keeps the thumb of the capture photo nearest the view downloaded (twice a second, only while a scan is in view),
    /// so a command that sends it as context.frame_jpg_b64 ("what am I looking at?", "check this quote", the coach's
    /// "check it") never waits for the download. ~20 KB per new photo; nothing when the pick doesn't change.
    public class GrokViewTracker : MonoBehaviour
    {
        public float interval = 0.5f;
        float m_Next;

        void Update()
        {
            if (Time.unscaledTime < m_Next) return;
            m_Next = Time.unscaledTime + interval;
            GrokView.Prefetch();
        }
    }
}
