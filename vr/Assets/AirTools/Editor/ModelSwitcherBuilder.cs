using AirTools.Scene;
using AirTools.UI;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using UnityEditor;
using UnityEngine;

namespace AirTools.Editor
{
    /// modelview: Model view's switcher and the "model only" declutter, built with UiBuild (docs/UI.md). Called once from
    /// MainSceneBuilder.WireTools (AirTools ▸ Wire Main Scene), after the Grok builders (the crosshair it hides);
    /// idempotent (Wire rebuilds the AirTools root, and the object under the rig is replaced here).
    /// modelwheel: the switcher is an endless wheel under the floating model, facing the eyes (ModelWheel): the palm
    /// ring's liquid glass arc at a 0.55 m radius, seven card slots along it (the scan's picture and its name; the middle
    /// one under the lens; visual-only Toggle glass buttons the wheel presses itself), the lens card's name and detail
    /// under it, and Walk in · Recentre · Exit chips below that (poke or ray). One ray target behind the cards takes the
    /// pinch / trigger and the drag (hands and controllers).
    public static class ModelSwitcherBuilder
    {
        public const string Name = "ModelSwitcher";
        /// Card slots: the lens card, two either side in full view, one more each side fading in / out at the ends.
        public const int Slots = 7;
        public static readonly Vector2 CardSize = new Vector2(0.15f, 0.118f);
        public static readonly Vector2 ThumbSize = new Vector2(0.138f, 0.0776f);   // 16:9, like the scans' photos
        public static readonly Vector2 ChipSize = new Vector2(0.086f, 0.03f);
        public const float ChipGap = 0.01f;
        /// Under the lens card: its name, the detail line, then the chips (m below the lens centre).
        public const float NameY = -0.086f, DetailY = -0.106f, ChipsY = -0.142f;
        /// The ray target behind the cards (the wheel's plane, lens at the origin): x ±0.5 m, y −0.30…+0.085 m.
        public static readonly Vector2 TargetSize = new Vector2(1.0f, 0.385f);
        public const float TargetCentreY = -0.1075f, TargetBehind = 0.008f;

        public static void Build(GameObject app, OVRCameraRig rig, SceneRoot sceneRoot, GameObject template)
        {
            var table = app.GetComponent<TabletopController>();
            var declutter = app.AddComponent<ModelViewDeclutter>();
            declutter.tabletop = table;
            declutter.sceneRoot = sceneRoot;
            declutter.crosshair = Object.FindFirstObjectByType<AirTools.Agent.Grok.DrillCrosshair>(FindObjectsInactive.Include);

            var old = rig.transform.Find(Name);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var go = new GameObject(Name);
            go.transform.SetParent(rig.transform, false);
            var s = go.AddComponent<ModelSwitcher>();
            s.tabletop = table;
            s.mode = app.GetComponent<AirTools.Core.ModeController>();
            s.head = rig.centerEyeAnchor;
            var w = go.AddComponent<ModelWheel>();
            w.switcher = s;
            w.tabletop = table;
            if (table != null) table.wheel = w;   // Model view stays quiet about the model: the wheel's lens says it
            w.head = rig.centerEyeAnchor;
            w.cardSize = CardSize;
            UiBuild.Distance = w.readDistance;   // the side cards ~0.9 m from a standing eye (UX W0.6)
            UiBuild.D5Rays = true;               // the chips: poke, and a hand or controller ray (D5)
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            w.content = content;
            var c = content.transform;

            // The liquid glass arc: its circle's centre R under the lens (the mesh is made at runtime by ModelWheel).
            var theme = UiTheme.Current;
            var glassGo = new GameObject("Glass");
            glassGo.transform.SetParent(c, false);
            glassGo.transform.localPosition = new Vector3(0f, -w.radius, 0.006f);
            glassGo.AddComponent<MeshFilter>();
            var glass = glassGo.AddComponent<MeshRenderer>();
            glass.sharedMaterial = theme.materials.liquidGlass;
            glass.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            glass.receiveShadows = false;
            w.glass = glass;

            // The cards: visual-only (no poke / ray of their own: the wheel's target takes the pinch and presses them).
            var thumbMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/AirTools/Materials/NotebookThumb.mat");
            w.cards = new GlassButton[Slots];
            w.thumbs = new MeshRenderer[Slots];
            w.badges = new TMPro.TextMeshPro[Slots];
            float sp = w.spacingDeg * Mathf.Deg2Rad;
            for (int i = 0; i < Slots; i++)
            {
                var p = WheelMath.ArcPoint((i - Slots / 2) * sp, w.radius);
                var card = UiBuild.Button(c, $"Card{i}", "Model", CardSize, ButtonStyle.Toggle, null, new Vector3(p.x, p.y, 0f),
                    TypeRole.Label, RadiusRole.Medium);
                // The name under the picture.
                card.label.transform.localPosition = new Vector3(0f, -CardSize.y * 0.5f + 0.017f, -0.0015f);
                card.label.rectTransform.sizeDelta = new Vector2(CardSize.x - 0.01f, 0.022f);
                card.label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
                card.label.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                var thumb = GameObject.CreatePrimitive(PrimitiveType.Quad);
                thumb.name = "Thumb";
                Object.DestroyImmediate(thumb.GetComponent<Collider>());
                thumb.transform.SetParent(card.visual, false);
                thumb.transform.localPosition = new Vector3(0f, CardSize.y * 0.5f - 0.006f - ThumbSize.y * 0.5f, -0.0012f);
                thumb.transform.localScale = new Vector3(ThumbSize.x, ThumbSize.y, 1f);
                var mr = thumb.GetComponent<MeshRenderer>();
                if (thumbMat != null) mr.sharedMaterial = thumbMat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.enabled = false;   // until its picture is in
                // "Not downloaded" over the dimmed picture while the laptop is away (the name stays under it).
                var badge = UiBuild.Text(card.visual, "Badge", ModelSites.NotDownloaded, TypeRole.Label,
                    thumb.transform.localPosition + new Vector3(0f, 0f, -0.001f), ColorRole.TextPrimary, TMPro.TextAlignmentOptions.Center,
                    weight: Weight.Medium);
                badge.rectTransform.sizeDelta = new Vector2(ThumbSize.x, 0.022f);
                badge.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
                badge.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                badge.gameObject.SetActive(false);
                w.badges[i] = badge;
                w.cards[i] = card;
                w.thumbs[i] = mr;
                Bind(card, w, ModelWheelAction.Card, i);
            }

            // Under the lens: the model's name, then its scale and what a pinch does.
            w.lensName = UiBuild.Text(c, "LensName", "", TypeRole.Title, new Vector3(0f, NameY, 0f), ColorRole.TextPrimary,
                TMPro.TextAlignmentOptions.Center, weight: Weight.Semibold);
            w.lensName.rectTransform.sizeDelta = new Vector2(0.23f, 0.026f);   // between the ±20° cards
            w.lensName.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            w.lensName.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            w.lensDetail = UiBuild.Text(c, "LensDetail", "", TypeRole.Caption, new Vector3(0f, DetailY, 0f), ColorRole.TextSecondary,
                TMPro.TextAlignmentOptions.Center);
            w.lensDetail.rectTransform.sizeDelta = new Vector2(0.34f, 0.02f);   // under the ±20° cards, between the ±40° ones
            w.lensDetail.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            w.lensDetail.overflowMode = TMPro.TextOverflowModes.Ellipsis;

            // Walk in · Recentre · Exit.
            float step = ChipSize.x + ChipGap;
            w.walkIn = Chip(c, "WalkIn", "Walk in", ButtonStyle.Primary, new Vector3(-step, ChipsY, 0f), template, w, ModelWheelAction.WalkIn);
            w.recentre = Chip(c, "Recentre", "Recentre", ButtonStyle.Secondary, new Vector3(0f, ChipsY, 0f), template, w, ModelWheelAction.Recentre);
            w.exit = Chip(c, "Exit", "Exit", ButtonStyle.Secondary, new Vector3(step, ChipsY, 0f), template, w, ModelWheelAction.Exit);

            w.target = Target(c, template);
            content.SetActive(false);
            UiBuild.D5Rays = false;
        }

        static GlassButton Chip(Transform c, string name, string text, ButtonStyle style, Vector3 pos, GameObject template, ModelWheel w, ModelWheelAction a)
        {
            var b = UiBuild.Button(c, name, text, ChipSize, style, template, pos, TypeRole.Caption, RadiusRole.Pill);
            Bind(b, w, a, 0);
            return b;
        }

        /// One generous ray target behind the cards (a clone of the poke block's clipped plane, ray only), 8 mm behind them
        /// so the chips in front of it win their own rays.
        static RayInteractable Target(Transform c, GameObject template)
        {
            if (template == null) return null;
            var clone = Object.Instantiate(template, c);
            clone.name = "Target";
            clone.SetActive(true);
            foreach (var bb in clone.GetComponents<MonoBehaviour>())
                if (bb != null && bb.GetType().Name == "BuildingBlock") Object.DestroyImmediate(bb);
            var visuals = clone.transform.Find("Visuals");
            if (visuals != null) Object.DestroyImmediate(visuals.gameObject);
            var poke = clone.GetComponent<PokeInteractable>();
            if (poke != null) Object.DestroyImmediate(poke);
            clone.transform.localPosition = new Vector3(0f, TargetCentreY, TargetBehind);
            clone.transform.localRotation = Quaternion.identity;
            clone.transform.localScale = new Vector3(TargetSize.x, TargetSize.y, 0.03f);
            var surface = clone.GetComponentInChildren<ClippedPlaneSurface>(true);
            var ray = clone.AddComponent<RayInteractable>();
            ray.InjectAllRayInteractable(surface);
            return ray;
        }

        static void Bind(GlassButton b, ModelWheel w, ModelWheelAction action, int slot)
        {
            var mb = b.gameObject.AddComponent<ModelWheelButton>();
            mb.button = b; mb.wheel = w; mb.action = action; mb.slot = slot;
        }
    }
}
