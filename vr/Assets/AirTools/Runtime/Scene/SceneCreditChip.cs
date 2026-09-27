using AirTools.Core;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Scene
{
    /// The loaded scan's source credit (SceneCredits). Declutter M9 (DC5): the credit lives on the wrist strip
    /// (LimitsChip) and in Settings' status line; this head-anchored caption only shows for the first `headSeconds` (8 s)
    /// after a scan that needs a credit becomes visible (entering the world or tabletop with it loaded, or loading it
    /// there). It sits low and centred in view (0.62 m, 32° down: under the status line and the pill, between the side
    /// slots) and gives way to a main-slot window. Hidden in passthrough and for scenes that need no credit.
    public class SceneCreditChip : MonoBehaviour
    {
        public Transform head;
        public GameObject content;
        public TextMeshPro text;
        public GlassSurface pill;
        [Tooltip("Distance (m), degrees below the eye line and to the right.")]
        public float distance = 0.62f, downDeg = 34f, yawDeg = 0f;   // 34° (not 32°): clears the Next-step pill (0.45 m, 24° down, 22° right) — census gate
        [Tooltip("Seconds the credit shows ahead after the scan becomes visible; then only the wrist strip and Settings carry it.")]
        public float headSeconds = 8f;

        /// The head-anchored credit showing now ("" when hidden).
        public string Shown => content != null && content.activeSelf && text != null ? text.text : "";
        /// The credit due now, wherever it shows (the wrist strip, Settings); "" when none.
        public string Due => m_Due ?? "";
        /// Seconds since the credit became due (−1 when none).
        public float DueAge => m_DueSince < 0 ? -1f : (float)(Time.realtimeSinceStartupAsDouble - m_DueSince);

        Vector3 m_Pos;
        bool m_Placed;
        float m_Next;
        string m_Due;
        double m_DueSince = -1;

        static string s_Site, s_Credit;

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);

        /// SceneCredits.For, cached on the site string (no allocation while the loaded site stays the same).
        public static string CreditFor(string site)
        {
            if (!ReferenceEquals(site, s_Site)) { s_Site = site; s_Credit = SceneCredits.For(site); }
            return s_Credit;
        }

        /// The credit a visible scan needs now (null: none): World or Tabletop, a scan package that needs one.
        public static string Current()
        {
            if (AppState.Mode == AppMode.Passthrough || !Services.TryGet<SceneRoot>(out var root) || !root.IsRuntimePackage || !root.IsVisible) return null;
            return CreditFor(root.Site);
        }

        /// Declutter M9: the head-anchored credit shows from the moment it became due for `seconds` (pure).
        public static bool HeadVisible(double dueSince, double now, float seconds) =>
            dueSince >= 0 && now >= dueSince && now - dueSince <= seconds;

        public void Refresh()
        {
            string credit = Current();
            double now = Time.realtimeSinceStartupAsDouble;
            if (credit == null) { m_Due = null; m_DueSince = -1; }
            else if (!ReferenceEquals(credit, m_Due))
            {
                m_Due = credit;
                m_DueSince = now;
                Log.Info($"Scan credit shown ahead for {headSeconds:0} s, then on the wrist strip and in Settings");
            }
            bool show = credit != null && HeadVisible(m_DueSince, now, headSeconds) && WindowSlot.Current == null;
            if (content != null && content.activeSelf != show) { content.SetActive(show); m_Placed = false; }
            if (!show || text == null || text.text == credit) return;
            text.text = credit;
            if (pill == null) return;
            text.ForceMeshUpdate();
            var b = text.textBounds;
            pill.SetSize(new Vector2(Mathf.Max(0.08f, b.size.x + 0.016f), Mathf.Max(0.022f, b.size.y + 0.010f)));
            text.transform.localPosition = new Vector3(-b.center.x, -b.center.y, text.transform.localPosition.z);
        }

        void LateUpdate()
        {
            if (Time.unscaledTime >= m_Next)
            {
                // While it shows, look again often so it leaves on time (and yields to a window at once).
                m_Next = Time.unscaledTime + (content != null && content.activeSelf ? 0.1f : 0.5f);
                Refresh();
            }
            if (content == null || !content.activeSelf || head == null) return;
            var target = HeadAnchor.PoseFor(head.position, head.forward, distance, downDeg, yawDeg).position;
            m_Pos = m_Placed ? Vector3.Lerp(m_Pos, target, 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime)) : target;
            m_Placed = true;
            var look = m_Pos - head.position;
            transform.SetPositionAndRotation(m_Pos, look.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(look.normalized, Vector3.up) : transform.rotation);
        }
    }
}
