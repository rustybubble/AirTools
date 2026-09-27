using System.Globalization;
using System.Text;
using AirTools.Core;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Parts
{
    /// Spec card for the selected part (SPEC M4), shown above the palm menu's tool ring: name, true size in mm and inches,
    /// material / finish / weight, price, window range, model tier, live fit, sources, finish swatches (chips that
    /// recolour the part; the current one is selected) and Remove (press twice; delete-undo: Undo brings it back).
    public class SpecCard : MonoBehaviour
    {
        public PartTool tool;
        [Tooltip("Palm menu hosting this inspector (its panel widens while a part is selected).")]
        public AirTools.Input.PalmMenu host;
        public GameObject content;
        public TextMeshPro title;
        public TextMeshPro dims;
        public TextMeshPro details;
        public TextMeshPro tier;
        public TextMeshPro fit;
        public TextMeshPro citations;
        public PartsButton[] swatches = new PartsButton[0];
        [Tooltip("Disabled for parts whose listing has no spacing (one-off units like a window AC).")]
        public GlassButton arrayButton;
        public GlassSurface[] swatchChips = new GlassSurface[0];

        public PartInstance Part { get; private set; }
        float m_Next;

        void OnEnable()
        {
            Services.Register(this);
            Refresh();   // size the palm panel before it pops in
        }
        void OnDisable() => Services.Unregister(this);

        void Update()
        {
            if (Time.unscaledTime < m_Next) return;
            m_Next = Time.unscaledTime + 0.2f;
            Refresh();
        }

        public void Refresh()
        {
            var t = tool != null ? tool : Services.Get<PartTool>();
            var part = t != null ? t.Selected : null;
            Part = part;
            bool show = part != null && AppState.Mode == AppMode.World;
            if (content != null && content.activeSelf != show) content.SetActive(show);
            if (host != null && host.InspectorShown != show) host.SetInspector(show);
            if (part == null) return;
            var s = part.Spec;
            var theme = UiTheme.Current;
            string sec = ColorUtility.ToHtmlStringRGBA(theme.colors.textSecondary);
            var text = Compose(part, t != null && part.Held && !t.OnSurface, sec, ColorUtility.ToHtmlStringRGB(part.Fit != null ? PartOutline.ColorFor(part.Fit.Status) : Color.white),
                AssetModeSwitcher.Current != null ? AssetModeSwitcher.Current.StatusFor(part) : null);   // settings-assets
            Set(title, text.title);
            Set(dims, text.dims);
            Set(details, text.details);
            Set(tier, text.tier);
            Set(fit, text.fit);
            Set(citations, text.sources);

            if (arrayButton != null) arrayButton.SetInteractable(s.spacing_mm.HasValue && s.spacing_mm.Value >= 10f);
            for (int i = 0; i < swatches.Length; i++)
            {
                if (swatches[i] == null) continue;
                bool on = i < s.finishes.Count;
                if (swatches[i].gameObject.activeSelf != on) swatches[i].gameObject.SetActive(on);
                if (!on) continue;
                var f = s.finishes[i];
                swatches[i].text = f.name;
                if (swatches[i].button != null)
                {
                    swatches[i].button.SetText(UiText.Sentence(f.name.ToUpperInvariant()));
                    swatches[i].button.SetSelected(f.name == part.FinishName);
                }
                if (i < swatchChips.Length && swatchChips[i] != null) swatchChips[i].SetTint(f.Color);
            }
        }

        /// The inspector's text (docs/ux/specs/W0.9-copy.md I1–I9), pure so it can be tested: title, dims (legend first),
        /// details (material · finish · weight · price · window range), tier (+ saved catalog / sample prices), fit
        /// (glyph + verdict, then the reason; never colour alone) and the one source label.
        /// settings-assets: the tier line is the model's badge when the server has asset modes ("AI mesh · Hunyuan",
        /// "Template · no OpenSCAD"), and `tierStatus` ("Rebuilding the 3D model…") while it's being swapped.
        public static (string title, string dims, string details, string tier, string fit, string sources) Compose(
            PartInstance part, bool heldOffSurface, string secondaryHex = "FFFFFFAD", string fitHex = "FFFFFF", string tierStatus = null)
        {
            var s = part.Spec;
            var c = CultureInfo.InvariantCulture;
            string title = Wrap(Copy.Clean(s.name), 34, 2);
            string dims = "W × D × H  " + UiText.Tabular(Copy.Dims(s.dims_mm)) + $"\n<color=#{secondaryHex}>" + UiText.Tabular(Copy.DimsOther(s.dims_mm)) + "</color>";
            var sb = new StringBuilder();
            var bits = new System.Collections.Generic.List<string>(3);
            if (!string.IsNullOrWhiteSpace(s.material)) bits.Add(UiText.Sentence(Cap(s.material)));
            string finish = part.FinishName ?? s.finish;
            if (!string.IsNullOrWhiteSpace(finish)) bits.Add(Cap(finish) + " finish");
            if (s.weight_g.HasValue) bits.Add(Copy.Weight(s.weight_g.Value));
            sb.Append(string.Join(" · ", bits));
            var seller = s.RecommendedSeller;
            if (seller != null && seller.Priced) sb.Append($"\n{PartFormat.Price(seller.price_usd)} each · {Copy.Clean(seller.name)}");
            if (s.HasWindowRange) sb.Append($"\nFor windows {Copy.Range(s.min_window_width_mm, s.max_window_width_mm)}");
            string tier = tierStatus ?? ((AssetModes.TierLine(s.asset) ?? Copy.TierLabel(s.asset?.tier)) + (part.Source == "catalog" ? " · saved catalog" : "") + (Copy.IsSample(s) ? " · Sample prices" : ""));
            string fit = heldOffSurface ? $"<color=#{secondaryHex}>Aim at {Copy.MountTarget(s)} to check the fit</color>"
                : part.Fit != null ? $"<color=#{fitHex}>{Copy.FitLine(part.Fit)}</color>" + (string.IsNullOrEmpty(part.Fit.Reason) ? "" : $"\n<color=#{secondaryHex}>{part.Fit.Reason}</color>") : "";
            string sources = "";
            if (!Copy.IsSample(s))
            {
                string label = s.citations.Count > 0 ? Copy.SourceLabel(s.citations[0]) : "";
                if (string.IsNullOrEmpty(label)) label = Copy.SourceLabel(s.spec_url);
                if (!string.IsNullOrEmpty(label)) sources = "Specs: " + label;
            }
            // Grok G1: set_finish's label ("Not a listed finish") under the card, ahead of the source.
            if (!string.IsNullOrWhiteSpace(part.FinishLabel)) sources = Copy.Clean(part.FinishLabel) + (sources.Length > 0 ? " · " + sources : "");
            return (title, dims, sb.ToString(), tier, fit, sources);
        }

        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();

        static void Set(TextMeshPro tm, string text)
        {
            if (tm != null && tm.text != text) tm.text = text;
        }

        /// Word-wrap to lines of at most maxChars, keeping at most maxLines (ellipsis when cut).
        public static string Wrap(string s, int maxChars, int maxLines)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var words = s.Split(' ');
            var sb = new StringBuilder();
            int line = 0, len = 0;
            foreach (var w in words)
            {
                if (len > 0 && len + 1 + w.Length > maxChars)
                {
                    if (++line >= maxLines) { sb.Append('…'); return sb.ToString(); }
                    sb.Append('\n');
                    len = 0;
                }
                if (len > 0) { sb.Append(' '); len++; }
                string word = w.Length > maxChars ? w.Substring(0, maxChars - 1) + "…" : w;
                sb.Append(word);
                len += word.Length;
            }
            return sb.ToString();
        }
    }
}
