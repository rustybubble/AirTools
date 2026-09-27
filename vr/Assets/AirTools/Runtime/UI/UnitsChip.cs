using AirTools.Core;
using UnityEngine;

namespace AirTools.UI
{
    /// The units chip (UX D2, Scene window): a small glass chip that reads the unit labels show — "Units · ft·in" or
    /// "Units · m" — and switches it on a press (UnitsSwitch.Toggle: saved per device, everything re-labelled in place).
    /// Built by Editor/UnitsChipBuilder (AirTools ▸ Wire Main Scene).
    public class UnitsChip : MonoBehaviour
    {
        public GlassButton button;

        /// The chip's words for a unit.
        public static string Label(UnitSystem u) => $"Units · {UnitsSwitch.Short(u)}";

        void OnEnable()
        {
            if (button != null) button.Clicked += Press;
            UiSettings.Changed += Refresh;
            Refresh();
        }

        void OnDisable()
        {
            if (button != null) button.Clicked -= Press;
            UiSettings.Changed -= Refresh;
        }

        /// Same path as a poke / ray pinch on the chip.
        public void Press()
        {
            UnitsSwitch.Toggle();
            Refresh();
        }

        public void Refresh()
        {
            if (button == null) return;
            string t = Label(UiSettings.UnitSystem);
            if (button.Text != t) button.SetText(t);
        }
    }
}
