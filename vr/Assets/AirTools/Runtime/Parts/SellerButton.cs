using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    public enum SellerAction { SortPrice, SortEta, Pay, Open, Close }

    /// A button on the seller panel.
    public class SellerButton : MonoBehaviour
    {
        public GlassButton button;
        public SellerPanel panel;
        public SellerAction action;
        public int row;

        void OnEnable() { if (button != null) button.Clicked += Press; }
        void OnDisable() { if (button != null) button.Clicked -= Press; }

        public void Press()
        {
            if (panel == null) return;
            switch (action)
            {
                case SellerAction.SortPrice: panel.Resort(SellerSort.ByPrice); break;
                case SellerAction.SortEta: panel.Resort(SellerSort.ByEta); break;
                case SellerAction.Pay: panel.PayRow(row); break;
                case SellerAction.Open: panel.OpenRow(row); break;
                case SellerAction.Close: panel.Close(); break;
            }
        }
    }
}
