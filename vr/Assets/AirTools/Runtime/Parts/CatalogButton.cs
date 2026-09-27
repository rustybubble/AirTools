using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// catalog: what a button of the catalog window does.
    public enum CatalogAction { Field, Chip, Card, Previous, Next, Fits, Close, Key, EmptySearch, FocusPin }

    /// catalog: a button of the catalog window or its keyboard; behaviour hangs on GlassButton.Clicked (a poke, a hand
    /// ray pinch or a controller trigger all land here). Press() is the same path (the harness types through it).
    public class CatalogButton : MonoBehaviour
    {
        public GlassButton button;
        public CatalogWindow window;
        public CatalogAction action;
        [Tooltip("Chip / card: the slot. Key: unused.")]
        public int index;
        [Tooltip("Key: the key (a–z, 0–9, -, \", space, back, clear, enter).")]
        public string key;

        void OnEnable() { if (button != null) button.Clicked += Press; }
        void OnDisable() { if (button != null) button.Clicked -= Press; }

        public void Press()
        {
            if (window == null) return;
            switch (action)
            {
                case CatalogAction.Field: window.ToggleKeyboard(); break;
                case CatalogAction.Chip: window.PressChip(index); break;
                case CatalogAction.Card: window.Pick(index); break;
                case CatalogAction.Previous: window.Page(-1); break;
                case CatalogAction.Next: window.Page(+1); break;
                case CatalogAction.Fits: window.ToggleFits(); break;
                case CatalogAction.Close: AppCommands.HideCatalog(); break;
                case CatalogAction.Key: window.Key(key); break;
                case CatalogAction.EmptySearch: window.SearchStores(); break;
                case CatalogAction.FocusPin: window.ToggleFocusPin(); break;   // gaze-catalog
            }
        }
    }
}
