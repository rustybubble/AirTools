using AirTools.Structure;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Editor
{
    /// Settings ▸ "Take out" (scene parts, backend docs/api.md parts.r&lt;rev&gt;.json): a caption and four chips, one per removable
    /// component of the loaded scene (the kitchen has four: dishwasher, sink cabinet, fridge, range); the runtime
    /// (ScenePartsRow) names them from the file and hides the rest. Declutter §3.2: scene settings and layers live in
    /// Settings, so the row is Settings' last row (MainSceneBuilder grew the panel by one 0.034 m row for it). Built only with
    /// UiBuild; colours and sizes from UiTheme.Current through UiBuild's chip style; behaviour on GlassButton.Clicked.
    /// Called once from MainSceneBuilder.BuildScenePanel (AirTools ▸ Wire Main Scene rebuilds the window, so the row).
    public static class ScenePartsBuilder
    {
        public const int Chips = 4;
        /// Wide enough for "Sink cabinet" in Caption at Settings' 0.5 m.
        public static readonly Vector2 ChipSize = new Vector2(0.062f, 0.026f);
        const float Gap = 0.004f, CaptionWidth = 0.044f;

        /// `left`: the row's left end in the window content's plane (Settings' x0) at the row's centre height; `width`: the
        /// room to the panel's right margin.
        public static ScenePartsRow Build(Transform windowContent, GameObject pokeTemplate, Vector3 left, float width)
        {
            var old = windowContent.Find("ScenePartsRow");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var go = new GameObject("ScenePartsRow");
            go.transform.SetParent(windowContent, false);
            go.transform.localPosition = left;
            var row = go.AddComponent<ScenePartsRow>();
            row.caption = UiBuild.Text(go.transform, "Caption", ScenePartsRow.Caption, TypeRole.Caption, Vector3.zero, ColorRole.TextSecondary,
                width: CaptionWidth);
            float x = CaptionWidth + Gap;
            row.empty = UiBuild.Text(go.transform, "Empty", ScenePartsRow.Empty, TypeRole.Caption, Vector3.zero, ColorRole.TextSecondary,
                width: width);
            row.empty.gameObject.SetActive(false);
            float pitch = Mathf.Min(ChipSize.x + Gap, (width - x + Gap) / Chips);
            var chips = new GlassButton[Chips];
            for (int i = 0; i < Chips; i++)
            {
                var pos = new Vector3(x + pitch * i + (pitch - Gap) * 0.5f, 0f, 0f);
                chips[i] = UiBuild.Button(go.transform, $"Part{i}", "Part", new Vector2(pitch - Gap, ChipSize.y), ButtonStyle.Chip, pokeTemplate,
                    pos, TypeRole.Caption, RadiusRole.Pill);
                chips[i].tooltip = "Take this part out of the scene, or put it back";
                chips[i].gameObject.SetActive(false);
            }
            row.chips = chips;
            return row;
        }
    }
}
