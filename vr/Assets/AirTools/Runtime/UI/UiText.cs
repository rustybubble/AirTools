using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace AirTools.UI
{
    /// World-space TextMeshPro from typography tokens. Sizes are dmm (see TypeRole) converted for the distance the
    /// text is designed to be read at, so a caption on a hand menu and a caption on a 1 m panel look the same size.
    public static class UiText
    {
        /// Typical reading distances for the app's surfaces (metres).
        public const float HandDistance = 0.45f, PanelDistance = 0.7f, FarDistance = 1.0f;

        /// Line height (metres) → TextMeshPro fontSize. 3D TextMeshPro renders 0.1 units per point of em; Inter's
        /// line height is ~1.21 em.
        public static float FontSizeFor(float lineHeightMetres, TMP_FontAsset font)
        {
            float ratio = font != null && font.faceInfo.pointSize > 0 ? font.faceInfo.lineHeight / font.faceInfo.pointSize : 1.21f;
            return lineHeightMetres / (0.1f * ratio);
        }

        public static float LineHeightMetres(TypeRole role, float distance) => UiTheme.Current.LineHeightDmm(role) * 0.001f * distance;

        /// Em height in metres for a role read at `distance` (UX W0.6: tokens are em dmm).
        public static float EmMetres(TypeRole role, float distance) => UiTheme.Current.EmDmm(role) * 0.001f * distance;

        /// TMP fontSize for an em height in metres (3D TextMeshPro renders 0.1 units per point of em).
        public static float FontSizeForEm(float emMetres) => emMetres / 0.1f;

        /// The em a TMP renders at, in dmm, read from `distance` (its world scale included): the legibility floor test.
        public static float EmDmmAt(TMPro.TMP_Text t, float distance) =>
            t == null || distance <= 0f ? 0f : t.fontSize * 0.1f * t.transform.lossyScale.y / distance * 1000f;

        public static TextMeshPro Create(Transform parent, string name, string text, TypeRole role, float distance,
            ColorRole color = ColorRole.TextPrimary, TextAlignmentOptions align = TextAlignmentOptions.Center,
            float width = 0f, Weight? weight = null, Vector3 localPos = default)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var tmp = go.AddComponent<TextMeshPro>();
            Style(tmp, role, distance, color, weight);
            tmp.alignment = align;
            tmp.textWrappingMode = width > 0f ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.rectTransform.sizeDelta = new Vector2(width > 0f ? width : 1f, LineHeightMetres(role, distance) * 1.2f);
            tmp.text = text;
            return tmp;
        }

        /// Apply a type role, weight and colour role to existing text.
        public static void Style(TextMeshPro tmp, TypeRole role, float distance, ColorRole color = ColorRole.TextPrimary, Weight? weight = null)
        {
            var theme = UiTheme.Current;
            SetWeight(tmp, weight ?? UiTheme.DefaultWeight(role));
            tmp.fontSize = FontSizeForEm(EmMetres(role, distance));
            tmp.color = theme.Color(color);
            tmp.richText = true;
            tmp.lineSpacing = role == TypeRole.Body || role == TypeRole.Caption ? 8f : 0f;   // generous body leading
            tmp.enableKerning = true;
            tmp.extraPadding = false;
            tmp.fontStyle = FontStyles.Normal;
            var r = tmp.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.sortingOrder = GlassSurface.LayerText;   // always over the surface it sits on
        }

        // Rich-text tags are matched first and left alone (a colour like #FF382E must not get digits wrapped).
        /// Switch a text to a weight: that weight's font asset and its material (they share an atlas).
        public static void SetWeight(TextMeshPro tmp, Weight weight)
        {
            var theme = UiTheme.Current;
            var font = theme.FontAsset(weight);
            if (font != null && tmp.font != font) tmp.font = font;
            var mat = theme.FontMaterial(weight);
            if (mat != null && tmp.fontSharedMaterial != mat) tmp.fontSharedMaterial = mat;
        }

        // Digits only: separators ("." and ",") keep their own advance, so "0.26" doesn't read "0 . 26".
        static readonly Regex s_Numbers = new Regex(@"<[^>]*>|[0-9]+");

        /// Tabular figures for live values: digit runs are monospaced so a changing reading doesn't jitter.
        public static string Tabular(string s) => string.IsNullOrEmpty(s) ? s
            : s_Numbers.Replace(s, m => m.Value[0] == '<' ? m.Value : $"<mspace=0.58em>{m.Value}</mspace>");

        /// Sentence case for labels ("MEASURE" → "Measure"); leaves mixed-case text alone.
        public static string Sentence(string s)
        {
            if (string.IsNullOrEmpty(s) || s != s.ToUpperInvariant()) return s;
            var lower = s.ToLowerInvariant();
            return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
        }
    }
}
