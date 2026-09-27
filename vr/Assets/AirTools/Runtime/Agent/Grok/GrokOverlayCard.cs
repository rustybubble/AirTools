using AirTools.Core;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// The overlays' card (a main-slot window, built by GrokOverlaysBuilder with UiBuild): what the scene overlay is,
    /// its long caption (the coverage coach's `spoken`, the survey's findings, the plan's spoken line), the backend's
    /// honesty label verbatim, and its controls: Place (a plan: sends "place them"), Hide (removes that overlay from the
    /// scene) and Close (just the card). While a survey runs it shows a spinner line instead of the findings.
    public class GrokOverlayCard : MonoBehaviour
    {
        public FloatingWindow window;
        public TextMeshPro title;
        public TextMeshPro body;
        public TextMeshPro footer;
        public GlassButton place;
        public GlassButton hide;
        public GlassButton close;

        public GrokOverlayKind Kind { get; private set; }
        public bool Busy { get; private set; }
        public bool IsOpen => window != null && window.IsOpen;
        public string BodyText => body != null ? body.text : m_Body;
        public string FooterText => footer != null ? footer.text : m_Footer;
        public int Shows { get; private set; }

        string m_Body, m_Footer, m_BusyText;
        float m_BusySince;

        void OnEnable()
        {
            Services.Register(this);
            if (place != null) place.Clicked += OnPlace;
            if (hide != null) hide.Clicked += OnHide;
            if (close != null) close.Clicked += Close;
        }

        void OnDisable()
        {
            Services.Unregister(this);
            if (place != null) place.Clicked -= OnPlace;
            if (hide != null) hide.Clicked -= OnHide;
            if (close != null) close.Clicked -= Close;
        }

        void OnPlace() { if (Services.TryGet<GrokOverlays>(out var o)) o.RequestPlace(); }
        void OnHide() { if (Services.TryGet<GrokOverlays>(out var o)) o.Hide(Kind); Close(); }

        public void Close() => window?.Close();

        /// Fill and open the card. `footerText` is the backend's label, shown as it came.
        public void Show(GrokOverlayKind kind, string titleText, string subtitle, string bodyText, string footerText, bool canPlace, string placeText = null)
        {
            Kind = kind;
            Busy = false;
            Shows++;
            m_Body = Copy.Clip(bodyText ?? "", 330);
            m_Footer = footerText ?? "";
            string sec = ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.textSecondary);
            if (title != null) title.text = string.IsNullOrEmpty(subtitle) ? titleText : $"{titleText} <size=70%><color=#{sec}>{subtitle}</color></size>";
            if (body != null) body.text = m_Body;
            if (footer != null) footer.text = m_Footer;
            if (place != null)
            {
                place.gameObject.SetActive(canPlace);
                if (canPlace && !string.IsNullOrEmpty(placeText)) place.SetText(placeText);
            }
            if (hide != null) hide.gameObject.SetActive(kind != GrokOverlayKind.None);
            window?.Open();
            Log.Info($"Grok card: {Kind} \"{titleText}\" ({subtitle}) | {Copy.Clip(m_Body.Replace('\n', ' '), 160)} | {m_Footer}");
        }

        /// A survey is running: the card says so with a spinner line until SetBusy(false) or the next Show.
        public void SetBusy(GrokOverlayKind kind, string titleText, string busyText, string footerText)
        {
            Show(kind, titleText, "running", busyText, footerText, false);
            Busy = true;
            m_BusyText = busyText ?? "";
            m_BusySince = Time.unscaledTime;
        }

        public void StopBusy() => Busy = false;

        void Update()
        {
            if (!Busy || body == null) return;
            // Spinner: a dot runs along "· · ·" (static atlas glyphs only) with the seconds waited.
            float t = Time.unscaledTime - m_BusySince;
            int phase = (int)(t * 3f) % 4;
            string dots = phase == 0 ? "" : phase == 1 ? "·" : phase == 2 ? "· ·" : "· · ·";
            string text = $"{m_BusyText}  {dots}\n{Mathf.FloorToInt(t)} s";
            if (body.text != text) body.text = text;
        }
    }
}
