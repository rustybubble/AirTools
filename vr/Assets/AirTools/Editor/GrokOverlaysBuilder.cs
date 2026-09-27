using AirTools.Agent.Grok;
using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using UnityEditor;
using UnityEngine;

namespace AirTools.Editor
{
    /// Grok lane G2 scene wiring (3D overlays in the scene frame): the GrokOverlays component, its transparent glass
    /// material (coverage wedges, 30 % part ghosts), the pinchable world-label chip template, the overlay card (main
    /// window slot) and a "Labels" toggle in the Scene window's title row (the pre-labelled scan). UI is built only with
    /// UiBuild (docs/UI.md). Called once from MainSceneBuilder.WireTools (AirTools ▸ Wire Main Scene); idempotent.
    public static class GrokOverlaysBuilder
    {
        const string MaterialsFolder = "Assets/AirTools/Materials";

        public static void Build(GameObject app, OVRCameraRig rig, SceneRoot sceneRoot, GameObject template, MeasureStyle style)
        {
            var overlays = app.GetComponent<GrokOverlays>();
            if (overlays == null) overlays = app.AddComponent<GrokOverlays>();
            overlays.sceneRoot = sceneRoot;
            overlays.style = style;
            overlays.glassMaterial = Transparent("GrokGlass", Color.white);
            overlays.chipTemplate = BuildChipTemplate(rig, template);
            overlays.card = BuildCard(rig, template);
            overlays.sceneLabelsToggle = BuildSceneLabelsToggle(rig, template);
        }

        /// The chip every pinchable world label is cloned from (a pin's "issue · 90 %", a scan label, the plan's Place):
        /// a Chip-style GlassButton with poke and an always-on ray target, sized for a 1 m read distance (WorldChip keeps
        /// that angular size at any distance). Inactive under GrokTemplates.
        static GlassButton BuildChipTemplate(OVRCameraRig rig, GameObject template)
        {
            const string holderName = "GrokTemplates";
            var old = rig.transform.Find(holderName);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var holder = new GameObject(holderName);
            holder.transform.SetParent(rig.transform, false);
            UiBuild.Distance = 1f;
            var chip = UiBuild.Button(holder.transform, "OverlayChip", "Place", new Vector2(0.09f, 0.03f), ButtonStyle.Chip, template,
                role: TypeRole.Label, radius: RadiusRole.Pill, ray: true);
            var world = chip.gameObject.AddComponent<WorldChip>();
            world.button = chip;
            holder.SetActive(false);
            UiBuild.Distance = UiText.HandDistance;
            return chip;
        }

        /// The overlay card (main slot, 0.45 m, 20° down): title, caption, the backend's label, Place / Hide / Close;
        /// movable by its grab bar.
        static GrokOverlayCard BuildCard(OVRCameraRig rig, GameObject template)
        {
            const string name = "GrokOverlayCard";
            var old = rig.transform.Find(name);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            UiBuild.Distance = 0.45f;
            UiBuild.D5Rays = true;
            var go = new GameObject(name);
            go.transform.SetParent(rig.transform, false);
            var w = go.AddComponent<FloatingWindow>();
            w.head = rig.centerEyeAnchor;
            w.distance = 0.45f; w.downDeg = 20f; w.yawDeg = 0f; w.mainSlot = true;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            w.content = content;
            var card = go.AddComponent<GrokOverlayCard>();
            card.window = w;
            var c = content.transform;

            const float W = 0.36f, H = 0.22f;
            UiBuild.Panel(c, "Panel", new Vector2(W, H));
            float x0 = -W * 0.5f + 0.016f, top = H * 0.5f;
            card.title = UiBuild.Text(c, "Title", "Plan", TypeRole.Heading, new Vector3(x0, top - 0.026f, 0f), width: W - 0.1f);
            card.close = UiBuild.Button(c, "Close", "Close", new Vector2(0.06f, 0.026f), ButtonStyle.Borderless, template,
                new Vector3(W * 0.5f - 0.046f, top - 0.026f, 0f), TypeRole.Caption);
            card.body = UiBuild.Text(c, "Body", "", TypeRole.Label, new Vector3(x0, top - 0.062f, 0f), width: W - 0.03f,
                align: TMPro.TextAlignmentOptions.TopLeft);
            card.footer = UiBuild.Text(c, "Label", "", TypeRole.Caption, new Vector3(x0, -H * 0.5f + 0.058f, 0f), ColorRole.TextSecondary, width: W - 0.03f);
            float by = -H * 0.5f + 0.024f;
            card.place = UiBuild.Button(c, "Place", "Place", new Vector2(0.1f, 0.03f), ButtonStyle.Primary, template,
                new Vector3(x0 + 0.05f, by, 0f), TypeRole.Caption);
            card.hide = UiBuild.Button(c, "Hide", "Hide", new Vector2(0.08f, 0.03f), ButtonStyle.Secondary, template,
                new Vector3(x0 + 0.1f + 0.008f + 0.04f, by, 0f), TypeRole.Caption);
            w.handle = UiBuild.Handle(w.transform, rig.centerEyeAnchor, new Vector3(0f, -H * 0.5f - 0.016f, 0f), template);
            w.handle.transform.SetParent(c, true);
            w.content.SetActive(false);
            UiBuild.D5Rays = false;
            UiBuild.Distance = UiText.HandDistance;
            return card;
        }

        /// "Labels" (Toggle) in Settings' Layers row, between Show edges and Fall edges (declutter M8: title rows hold only
        /// the title and Close): shows the scan's pre-made labels (GET /scenes/{site}/labels) without asking Grok.
        static GlassButton BuildSceneLabelsToggle(OVRCameraRig rig, GameObject template)
        {
            var sceneWindow = rig.transform.Find("ScenePanel");
            var window = sceneWindow != null ? sceneWindow.GetComponent<FloatingWindow>() : null;
            if (window == null || window.content == null)
            {
                Debug.LogWarning("[AirTools] Grok G2: no Settings window; the scan labels toggle isn't built");
                return null;
            }
            var c = window.content.transform;
            var old = c.Find("GrokLabels");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var slot = c.Find(MainSceneBuilder.SettingsLabelsSlot);   // MainSceneBuilder.BuildScenePanel: the Layers row
            if (slot == null)
            {
                Debug.LogWarning("[AirTools] Grok G2: Settings has no Layers slot; the scan labels toggle isn't built");
                return null;
            }
            UiBuild.Distance = 0.5f;
            UiBuild.D5Rays = true;
            var b = UiBuild.Button(c, "GrokLabels", "Labels", new Vector2(0.098f, 0.028f), ButtonStyle.Toggle, template,
                slot.localPosition, TypeRole.Caption);
            var hook = b.gameObject.AddComponent<GrokOverlayButton>();
            hook.button = b;
            hook.action = GrokOverlayAction.ToggleSceneLabels;
            UiBuild.D5Rays = false;
            UiBuild.Distance = UiText.HandDistance;
            return b;
        }

        /// URP Unlit, alpha-blended, both faces, no depth write (tinted per renderer).
        static Material Transparent(string name, Color color)
        {
            System.IO.Directory.CreateDirectory(MaterialsFolder);
            string path = $"{MaterialsFolder}/{name}.mat";
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = shader;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_SrcBlendAlpha", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlendAlpha", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
