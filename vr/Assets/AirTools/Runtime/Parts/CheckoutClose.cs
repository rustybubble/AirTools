using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// Close (or Back to the sellers) button of the checkout panel.
    public class CheckoutClose : MonoBehaviour
    {
        public GlassButton button;
        public CheckoutPanel panel;
        [Tooltip("Back: close the checkout and show the sellers again (one window slot, UX W0.7).")]
        public bool back;
        void OnEnable() { if (button != null) button.Clicked += Close; }
        void OnDisable() { if (button != null) button.Clicked -= Close; }
        void Close() { if (panel == null) return; if (back) panel.Back(); else panel.Close(); }
    }
}
