using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Parts
{
    /// One seller card: name, delivered price, shipping / ETA / rating / stock, a "Recommended" tag, and the two
    /// actions — Pay with Visa (sandbox) and Open at seller.
    public class SellerRowView : MonoBehaviour
    {
        public TextMeshPro nameText;
        public TextMeshPro priceText;
        public TextMeshPro detailText;
        public GameObject recommendedTag;
        public GlassButton payButton;
        public GlassButton openButton;
        public GlassSurface backplate;

        public int SellerIndex { get; private set; } = -1;

        /// What one seller costs for `qty` pieces, delivered: listings (packs round up) × price + shipping — the same
        /// sum CheckoutPanel.Total charges (without the "what else" lines).
        public static float Delivered(PartSeller s, int qty) => s == null ? 0f : s.price_usd * s.PacksFor(qty) + SellerSort.Shipping(s);

        /// The row's text (docs/ux/specs/W0.9-copy.md S7–S9): cleaned name; price (qty 1) or the delivered total with
        /// "qty × price"; shipping, arrival and a believable rating (omitted when unknown); "Out of stock" only.
        public static (string name, string price, string detail) Compose(PartSeller s, int qty, string warningHex = "FFB529FF", string secondaryHex = "EDEEF2AD")
        {
            if (s == null) return ("", "", "");
            var c = System.Globalization.CultureInfo.InvariantCulture;
            float ship = SellerSort.Shipping(s);
            string each = s.pack_qty > 1
                ? $"{UiText.Tabular(s.PacksFor(qty).ToString(c))} × pack of {UiText.Tabular(s.pack_qty.ToString(c))}"
                : $"{UiText.Tabular(qty.ToString(c))} × {UiText.Tabular(PartFormat.Price(s.price_usd))}";
            string price = qty > 1
                ? UiText.Tabular(PartFormat.Price(Delivered(s, qty))) + $" <color=#{secondaryHex}>{each}</color>"
                : UiText.Tabular(PartFormat.Price(s.price_usd));
            var bits = new System.Collections.Generic.List<string>(4) { ship > 0f ? PartFormat.Price(ship) + " shipping" : "Free shipping" };
            if (!string.IsNullOrWhiteSpace(s.eta)) bits.Add("arrives " + s.eta.Trim());
            if (s.rating.HasValue && s.rating.Value > 0f && s.rating.Value <= 5f) bits.Add("rated " + s.rating.Value.ToString("0.0", c));
            if (!s.in_stock) bits.Add($"<color=#{warningHex}>Out of stock</color>");
            return (Copy.Clean(s.name), price, string.Join(" · ", bits));
        }

        public void Show(PartSeller s, int sellerIndex, bool recommended, int qty)
        {
            SellerIndex = sellerIndex;
            gameObject.SetActive(s != null);
            if (s == null) return;
            var text = Compose(s, qty, ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.warning), ColorUtility.ToHtmlStringRGBA(UiTheme.Current.colors.textSecondary));
            if (nameText != null) nameText.text = text.name;
            if (priceText != null) priceText.text = text.price;
            if (detailText != null) detailText.text = text.detail;
            if (recommendedTag != null) recommendedTag.SetActive(recommended);
            if (payButton != null) payButton.SetInteractable(s.in_stock);
        }
    }
}
