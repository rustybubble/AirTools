using TMPro;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// Runtime layout for the G3 card's UiBuild-built parts: top-left anchored text boxes sized to their content, so
    /// one window can show a postcard, a QR, a rules table or a quote check (docs/UI.md: windows grow to fit).
    public static class GrokCardLayout
    {
        /// Height (m) `text` wraps to at `width` in this TMP's style.
        public static float TextHeight(TextMeshPro tmp, string text, float width)
        {
            if (tmp == null || string.IsNullOrEmpty(text)) return 0f;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            var size = tmp.GetPreferredValues(text, width, 0f);
            return Mathf.Max(size.y, tmp.fontSize * 0.1f * 1.2f);
        }

        /// Show `text` in a top-left box at `topLeft` (the parent's local x, y), `width` × `height`; hide it when empty.
        public static void Place(TextMeshPro tmp, string text, Vector2 topLeft, float width, float height)
        {
            if (tmp == null) return;
            bool on = !string.IsNullOrEmpty(text);
            if (tmp.gameObject.activeSelf != on) tmp.gameObject.SetActive(on);
            if (!on) return;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Overflow;
            var rt = tmp.rectTransform;
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(width, Mathf.Max(0.001f, height));
            var p = tmp.transform.localPosition;
            tmp.transform.localPosition = new Vector3(topLeft.x, topLeft.y, p.z);
            if (tmp.text != text) tmp.text = text;
        }

        /// A theme colour role as a rich-text hex (RRGGBBAA).
        public static string Hex(AirTools.UI.ColorRole role) => ColorUtility.ToHtmlStringRGBA(AirTools.UI.UiTheme.Current.Color(role));
    }
}
