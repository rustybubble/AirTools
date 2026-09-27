using AirTools.UI;
using UnityEngine;

namespace AirTools.Editor
{
    /// UX decision D2: the units chip in the Scene window, with the other display settings (UX research R03 P7 puts it
    /// beside High contrast / Reduce motion, which moved from the palm menu to this window). It sits under Set scale,
    /// next to the keypad whose unit it also sets ("Real size: 24 inches"). Built only with UiBuild; colours from
    /// UiTheme.Current (UiBuild's chip style); behaviour on GlassButton.Clicked (UnitsChip). Called once from
    /// MainSceneBuilder.BuildScenePanel (AirTools ▸ Wire Main Scene); the window is rebuilt on each Wire, so is the chip.
    public static class UnitsChipBuilder
    {
        /// The window's chip height (as Contrast / Less motion), wide enough for "Units · ft·in" in Caption.
        static readonly Vector2 Size = new Vector2(0.1f, 0.026f);

        public static UnitsChip Build(Transform windowContent, GameObject pokeTemplate, Vector3 localPos)
        {
            var old = windowContent.Find("UnitsChip");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var b = UiBuild.Button(windowContent, "UnitsChip", UnitsChip.Label(UiSettings.DefaultUnits), Size, ButtonStyle.Chip, pokeTemplate,
                localPos, TypeRole.Caption, RadiusRole.Pill);
            b.tooltip = "Units on every label: feet and inches or metres";
            var chip = b.gameObject.AddComponent<UnitsChip>();
            chip.button = b;
            return chip;
        }
    }
}
