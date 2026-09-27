using AirTools.Core;
using AirTools.Parts;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// show_safety's pill (lane G3): a red RECALLED (or amber CAUTION) pill with the headline under it, on top of the
    /// spec card (palm inspector) and of the seller panel, for the part that panel shows. Nothing for clear / unknown.
    /// A pinch or poke anywhere on it opens recalls[0].url from GET /parts/{id}/safety in the Quest browser (saved to
    /// the notebook instead in DemoMode). Reads GrokState, so a verdict that arrives before the panel opens still shows.
    public class SafetyBanner : MonoBehaviour
    {
        [Tooltip("Follow the spec card's part (palm inspector)…")]
        public SpecCard spec;
        [Tooltip("…or the seller panel's part.")]
        public SellerPanel sellers;
        public GameObject content;
        [Tooltip("The whole banner is the target (borderless): tap for the recall notice.")]
        public GlassButton button;
        public GlassSurface pill;
        public TextMeshPro pillText;
        [Tooltip("\"Tap for the recall notice\" beside the pill.")]
        public TextMeshPro hint;
        public TextMeshPro headline;

        public string PartId { get; private set; }
        public string Shown { get; private set; }
        public int Taps { get; private set; }
        float m_Next;
        bool m_Fetching;
        string m_Headline;

        void OnEnable()
        {
            if (button != null) button.Clicked += Open;
            GrokState.SafetyChanged += OnChanged;
            m_Next = 0f;
        }

        void OnDisable()
        {
            if (button != null) button.Clicked -= Open;
            GrokState.SafetyChanged -= OnChanged;
        }

        void OnChanged(string partId) => m_Next = 0f;

        string CurrentPart()
        {
            if (spec != null) return spec.Part != null ? spec.Part.Spec.id : null;
            if (sellers != null) return sellers.IsOpen && sellers.Part != null ? sellers.Part.Spec.id : null;
            return null;
        }

        void Update()
        {
            if (Time.unscaledTime < m_Next) return;
            m_Next = Time.unscaledTime + 0.25f;
            Refresh();
        }

        public void Refresh()
        {
            PartId = CurrentPart();
            var v = GrokState.SafetyFor(PartId);
            bool show = v != null && v.Shows;
            if (content != null && content.activeSelf != show) content.SetActive(show);
            if (!show) { Shown = null; m_Headline = null; return; }
            if (Shown == v.Verdict && ReferenceEquals(m_Headline, v.Headline)) return;   // no per-frame strings
            Shown = v.Verdict;
            m_Headline = v.Headline;
            var theme = UiTheme.Current;
            // Recalled: the armed-destructive fill with white words (5.6:1); caution: amber with dark words.
            if (pill != null) pill.SetTint(v.Recalled ? theme.colors.dangerFill : theme.Color(ColorRole.Warning));
            if (pillText != null)
            {
                pillText.text = v.Recalled ? "RECALLED" : "CAUTION";
                pillText.color = v.Recalled ? theme.colors.onAccent : theme.Color(ColorRole.Background);
            }
            if (hint != null) hint.text = v.Recalled ? "Tap for the recall notice" : "Tap for the reports";
            if (headline != null) headline.text = GrokText.Esc(v.Headline);
        }

        /// Open the recall notice: the cached link, else GET /parts/{id}/safety → recalls[0].url.
        public void Open()
        {
            var v = GrokState.SafetyFor(PartId);
            if (v == null || !v.Shows) return;
            Taps++;
            if (!string.IsNullOrEmpty(v.RecallUrl)) { GrokLinks.Open(v.RecallUrl, v.Recalled ? "the recall notice" : "the report"); return; }
            if (m_Fetching || !Services.TryGet<GrokClient>(out var client)) return;
            m_Fetching = true;
            string part = PartId;
            client.Safety(part, report =>
            {
                m_Fetching = false;
                var url = SafetyView.LinkFrom(report);
                if (url == null) { UiToast.Show("No recall link came back for this part", ColorRole.Warning); return; }
                GrokState.SetRecallUrl(part, url);
                GrokLinks.Open(url, v.Recalled ? "the recall notice" : "the report");
            });
        }
    }
}
