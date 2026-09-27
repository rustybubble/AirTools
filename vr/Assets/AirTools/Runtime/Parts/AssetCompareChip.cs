using AirTools.Core;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// settings-assets: the card's "Compare" chip, right of the tier badge: flips the selected part between its HF
    /// (Hunyuan image-to-3D) and LLM+CAD (Grok OpenSCAD, or the template when the server has no OpenSCAD) models in place
    /// (AssetModeSwitcher.Compare), to judge which generator looks better. Shown for parts the laptop made; ink while the
    /// part shows another mode's model than Settings ▸ 3D models. Lives on the card (always active) so it can hide the chip.
    /// Built by Editor/SettingsAssetsBuilder (AirTools ▸ Wire Main Scene).
    public class AssetCompareChip : MonoBehaviour
    {
        public GlassButton button;
        public SpecCard card;

        public const string Label = "Compare";
        float m_Next;

        void OnEnable()
        {
            if (button != null) button.Clicked += OnClicked;
            Refresh();
        }

        void OnDisable()
        {
            if (button != null) button.Clicked -= OnClicked;
        }

        void OnClicked() => Press();

        void Update()
        {
            if (Time.unscaledTime < m_Next) return;
            m_Next = Time.unscaledTime + 0.2f;
            Refresh();
        }

        /// Same path as a poke / ray pinch on the chip.
        public bool Press()
        {
            var s = AssetModeSwitcher.Current != null ? AssetModeSwitcher.Current : Services.Get<AssetModeSwitcher>();
            if (s == null) return false;
            bool ok = s.Compare(card != null ? card.Part : null);
            Refresh();
            return ok;
        }

        public void Refresh()
        {
            if (button == null) return;
            var part = card != null ? card.Part : null;
            bool show = AssetModeSwitcher.IsServerPart(part);
            if (button.gameObject.activeSelf != show) button.gameObject.SetActive(show);
            if (!show) return;
            var s = AssetModeSwitcher.Current;
            bool busy = s != null && s.StatusFor(part) != null;
            button.SetSelected(AssetModeSwitcher.Comparing(part));
            button.SetInteractable(!busy);
        }
    }
}
