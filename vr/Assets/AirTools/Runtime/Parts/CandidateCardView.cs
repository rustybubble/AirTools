using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Parts
{
    /// One candidate card (an ElevatedSolid row on the crate panel): photo, name, W×D×H, price and model tier, and a
    /// Take button (PartsButton). The card being loaded / in hand is shown selected.
    public class CandidateCardView : MonoBehaviour
    {
        public MeshRenderer photo;
        public TextMeshPro nameText;
        public TextMeshPro dimsText;
        public TextMeshPro priceText;
        public GlassSurface backplate;

        public PartSummary Summary { get; private set; }
        public bool Highlighted { get; private set; }
        /// assetgen: "Building the 3D model…" while the server makes this candidate's model; null once it's ready.
        public string ModelStatus { get; private set; }
        MaterialPropertyBlock m_Block;
        static readonly int s_BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        public void Show(PartSummary s)
        {
            Summary = s;
            gameObject.SetActive(s != null);
            if (s == null) return;
            if (nameText != null) nameText.text = SpecCard.Wrap(Copy.Clean(s.name), 40, 2);
            if (dimsText != null) dimsText.text = s.dims_mm != null ? UiText.Tabular(Copy.Dims(s.dims_mm)) : "";
            ModelStatus = s.spec?.asset != null && !s.spec.asset.Ready && !s.spec.asset.Failed ? BuildingText : null;   // assetgen
            ShowPrice();
            SetImage(null);
            SetHighlight(false);
        }

        /// assetgen: what the card says while the server builds the candidate's model.
        public const string BuildingText = "Building the 3D model…";

        /// assetgen: the price line's second half: the model's tier, or ModelStatus while it's being built.
        void ShowPrice()
        {
            var s = Summary;
            if (priceText == null || s == null) return;
            var sec = ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.textSecondary);
            string what = ModelStatus ?? (string.IsNullOrEmpty(s.tier) ? null : Copy.TierLabel(s.tier));
            string tier = what == null ? "" : $" <color=#{sec}>· {what}</color>";
            priceText.text = (s.price_usd.HasValue && s.price_usd.Value > 0f ? UiText.Tabular(PartFormat.Price(s.price_usd)) : "No price") + tier;
        }

        /// assetgen: the server's model is being built (status) or ready (null: the tier label again).
        public void SetModelStatus(string status)
        {
            if (status == ModelStatus) return;
            ModelStatus = status;
            ShowPrice();
        }

        /// D2: the unit changed — re-word the size line (the image and highlight stay).
        public void Relabel()
        {
            if (Summary != null && dimsText != null) dimsText.text = Summary.dims_mm != null ? UiText.Tabular(Copy.Dims(Summary.dims_mm)) : "";
        }

        public void SetImage(Texture2D tex)
        {
            if (photo == null) return;
            m_Block ??= new MaterialPropertyBlock();
            photo.GetPropertyBlock(m_Block);
            if (tex != null) { m_Block.SetTexture(s_BaseMap, tex); m_Block.SetColor(s_BaseColor, Color.white); }
            else m_Block.SetColor(s_BaseColor, new Color(0.22f, 0.23f, 0.27f));
            photo.SetPropertyBlock(m_Block);
        }

        public void SetHighlight(bool on)
        {
            Highlighted = on;
            if (backplate == null) return;
            var c = UiTheme.Current.colors;
            backplate.SetTint(on ? UiTheme.Wash(c.elevatedSolid, c.inkWash) : c.elevatedSolid);   // D3: selected = ink wash
        }
    }
}
