using AirTools.Core;
using AirTools.Parts;
using AirTools.Structure;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Editor
{
    /// settings-assets: the UI and the switcher for Settings ▸ Talk / 3D models and the card's Compare chip. Built only with
    /// UiBuild; colours and sizes from UiTheme.Current through UiBuild's chip style; behaviour on GlassButton.Clicked
    /// (SettingsPrefsRows, AssetCompareChip). Called from MainSceneBuilder (AirTools ▸ Wire Main Scene rebuilds them):
    /// - BuildSettingsRows: two rows at the bottom of Settings (the panel grew by two 0.034 m rows, RowsHeight);
    /// - BuildCompareChip: "Compare" at the right end of the card's tier line (the tier text makes room for it);
    /// - AddSwitcher: Parts/AssetModeSwitcher on the app object (placed parts follow the setting).
    public static class SettingsAssetsBuilder
    {
        /// The two rows' height (added to MainSceneBuilder.SettingsPanelHeight).
        public const float RowPitch = 0.034f, RowsHeight = 2 * RowPitch;
        /// Wide enough for "LLM+CAD" / "Toggle" in Caption at Settings' 0.5 m.
        public static readonly Vector2 ChipSize = new Vector2(0.072f, 0.026f);
        const float Gap = 0.006f, CaptionWidth = 0.07f;
        public static readonly Vector2 CompareSize = new Vector2(0.066f, 0.02f);   // clears the title above and the dims below

        /// `left`: the first row's left end in the window content's plane (Settings' x0) at its centre height; `width`: the
        /// room to the panel's right margin.
        public static SettingsPrefsRows BuildSettingsRows(Transform windowContent, GameObject pokeTemplate, Vector3 left, float width)
        {
            var old = windowContent.Find("SettingsPrefsRows");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var go = new GameObject("SettingsPrefsRows");
            go.transform.SetParent(windowContent, false);
            go.transform.localPosition = left;
            var rows = go.AddComponent<SettingsPrefsRows>();
            float pitch = Mathf.Min(ChipSize.x + Gap, (width - CaptionWidth) / 3f);
            GlassButton[] Row(string caption, string[] labels, float y, string[] tips, out TextMeshPro captionText)
            {
                captionText = UiBuild.Text(go.transform, caption.Replace(" ", "") + "Caption", caption, TypeRole.Caption, new Vector3(0f, y, 0f),
                    ColorRole.TextSecondary, width: CaptionWidth);
                var chips = new GlassButton[labels.Length];
                for (int i = 0; i < labels.Length; i++)
                {
                    var pos = new Vector3(CaptionWidth + pitch * i + (pitch - Gap) * 0.5f, y, 0f);
                    chips[i] = UiBuild.Button(go.transform, $"{caption.Replace(" ", "")}{i}", labels[i], new Vector2(pitch - Gap, ChipSize.y), ButtonStyle.Chip,
                        pokeTemplate, pos, TypeRole.Caption, RadiusRole.Pill);
                    chips[i].tooltip = tips[i];
                }
                return chips;
            }
            rows.talk = Row(SettingsPrefsRows.TalkCaption,
                new[] { UserPrefs.Label(TalkStyle.Hold), UserPrefs.Label(TalkStyle.Toggle), UserPrefs.Label(TalkStyle.Auto) }, 0f,
                new[]
                {
                    "Talk listens while you hold it; letting go sends",
                    "Tap Talk to start; tap again, or stop talking, to send",
                    "A quick tap toggles, a long press holds",
                }, out rows.talkCaption);
            rows.models = Row(SettingsPrefsRows.ModelsCaption,
                new[] { UserPrefs.Label(AssetMode.Hf), UserPrefs.Label(AssetMode.LlmScad), UserPrefs.Label(AssetMode.Auto) }, -RowPitch,
                new[]
                {
                    "3D models from the product photo by Hunyuan3D (Hugging Face)",
                    "3D models Grok writes in OpenSCAD (the template when the laptop has no OpenSCAD)",
                    "The server picks: template, CAD or AI mesh",
                }, out rows.modelsCaption);
            return rows;
        }

        /// The Compare chip at the right end of the card's tier line; the tier text is narrowed to make room.
        public static AssetCompareChip BuildCompareChip(Transform specContent, GameObject pokeTemplate, SpecCard card, float textWidth)
        {
            var old = specContent.Find("Compare");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            float y = card.tier != null ? card.tier.transform.localPosition.y : 0.106f;
            var b = UiBuild.Button(specContent, "Compare", AssetCompareChip.Label, CompareSize, ButtonStyle.Chip, pokeTemplate,
                new Vector3(textWidth - CompareSize.x * 0.5f, y, 0f), TypeRole.Caption, RadiusRole.Pill);
            b.tooltip = "Show this part's other 3D model: HF (Hunyuan) or LLM + CAD";
            if (card.tier != null)
            {
                var r = card.tier.rectTransform;
                r.sizeDelta = new Vector2(textWidth - CompareSize.x - 0.006f, r.sizeDelta.y);
                card.tier.overflowMode = TextOverflowModes.Ellipsis;
            }
            var old2 = card.GetComponent<AssetCompareChip>();
            if (old2 != null) Object.DestroyImmediate(old2);
            var chip = card.gameObject.AddComponent<AssetCompareChip>();   // on the card (always active): it hides the chip
            chip.button = b;
            chip.card = card;
            return chip;
        }

        /// The switcher on the app object, wired to the part tool, the loader, the client and the placement editor.
        public static AssetModeSwitcher AddSwitcher(GameObject app, PartTool tool, PartLoader loader, PartsClient client)
        {
            var old = app.GetComponent<AssetModeSwitcher>();
            if (old != null) Object.DestroyImmediate(old);
            var s = app.AddComponent<AssetModeSwitcher>();
            s.tool = tool;
            s.loader = loader;
            s.client = client;
            s.editor = app.GetComponent<PlacementEditor>();
            return s;
        }
    }
}
