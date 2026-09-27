using AirTools.Scene;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Editor
{
    /// Grok lane G1 scene wiring (context + core): the view tracker that keeps the nearest capture photo downloaded for
    /// context.frame_jpg_b64, and the scan credit chip (the Zabel footage is CC BY 3.0; declutter M9 / DC5: the credit
    /// lives on the wrist strip and in Settings while that scan is loaded, and this chip shows it ahead for its first 8 s).
    /// Built with UiBuild (docs/UI.md). Called once from MainSceneBuilder.WireTools (AirTools ▸ Wire Main Scene);
    /// idempotent (Wire rebuilds the AirTools root, and the chip is replaced here).
    public static class G1Builder
    {
        public static void Build(GameObject app, OVRCameraRig rig, SceneRoot sceneRoot, GameObject template)
        {
            if (app.GetComponent<AirTools.Agent.Grok.GrokViewTracker>() == null) app.AddComponent<AirTools.Agent.Grok.GrokViewTracker>();
            BuildCreditChip(rig);
        }

        /// A caption pill low and centred in view (SceneCreditChip: 0.62 m, 34° down; for 8 s after the scan becomes
        /// visible): "Haus Schiller – Zabelgymnasium Gera – Drohnenflug" by zabelgymnasium, CC BY 3.0, via Wikimedia
        /// Commons. Two lines at most; the pill is sized to the text at runtime.
        static void BuildCreditChip(OVRCameraRig rig)
        {
            const string name = "SceneCreditChip";
            var old = rig.transform.Find(name);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var go = new GameObject(name);
            go.transform.SetParent(rig.transform, false);
            var chip = go.AddComponent<SceneCreditChip>();
            chip.head = rig.centerEyeAnchor;
            UiBuild.Distance = chip.distance;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            chip.content = content;
            chip.pill = GlassSurface.Create(content.transform, "Pill", new Vector2(0.26f, 0.034f), GlassTier.GlassRegular, RadiusRole.Small, Elevation.Subtle);
            chip.text = UiBuild.Text(content.transform, "Text", SceneCredits.Zabel, TypeRole.Caption, new Vector3(0f, 0f, -0.002f),
                ColorRole.TextSecondary, TMPro.TextAlignmentOptions.Center, width: 0.24f);
            chip.text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            content.SetActive(false);
        }
    }
}
