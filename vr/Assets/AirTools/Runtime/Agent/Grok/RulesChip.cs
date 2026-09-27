using AirTools.Core;
using AirTools.Parts;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// show_rules on the spec card (lane G3): when the selected part has a rules check (its part_ids), a strip above
    /// the palm inspector with the permit pill (yes amber, no green, else grey) and "Permits and rebates"; a poke
    /// reopens the rules card with its Permit / Money tabs. Sits above the RECALLED banner when that shows.
    public class RulesChip : MonoBehaviour
    {
        public SpecCard spec;
        [Tooltip("The safety banner under it: the chip moves up while that shows.")]
        public SafetyBanner below;
        public GameObject content;
        public GlassButton button;
        public GlassSurface pill;
        public TextMeshPro pillText;
        public TextMeshPro text;
        [Tooltip("Local y with the safety banner hidden, and how far up it moves while it shows.")]
        public float baseY, step = 0.062f;

        public RulesView Shown { get; private set; }
        float m_Next;
        bool m_Raised;

        void OnEnable() { if (button != null) button.Clicked += Open; m_Next = 0f; }
        void OnDisable() { if (button != null) button.Clicked -= Open; }

        void Update()
        {
            if (Time.unscaledTime < m_Next) return;
            m_Next = Time.unscaledTime + 0.25f;
            Refresh();
        }

        public void Refresh()
        {
            var part = spec != null && spec.Part != null ? spec.Part.Spec.id : null;
            var v = GrokState.RulesFor(part);
            bool show = v != null;
            if (content != null && content.activeSelf != show) content.SetActive(show);
            bool raised = below != null && below.content != null && below.content.activeSelf;
            if (raised != m_Raised || transform.localPosition.y != baseY + (raised ? step : 0f))
            {
                m_Raised = raised;
                var p = transform.localPosition;
                transform.localPosition = new Vector3(p.x, baseY + (raised ? step : 0f), p.z);
            }
            if (!show || ReferenceEquals(v, Shown)) return;
            Shown = v;
            var theme = UiTheme.Current;
            var tone = RulesView.PermitTone(v.PermitRequired);
            bool filled = tone != ColorRole.TextSecondary;
            if (pill != null) pill.SetTint(filled ? theme.Color(tone) : theme.Color(ColorRole.Control));
            if (pillText != null)
            {
                pillText.text = v.PermitPillText;
                pillText.color = filled ? theme.Color(ColorRole.Background) : theme.Color(ColorRole.TextPrimary);
            }
            if (text != null) text.text = "Permits and rebates";
        }

        public void Open()
        {
            var v = GrokState.RulesFor(spec != null && spec.Part != null ? spec.Part.Spec.id : null);
            if (v != null && Services.TryGet<GrokCard>(out var card)) card.ShowRules(v);
        }
    }
}
