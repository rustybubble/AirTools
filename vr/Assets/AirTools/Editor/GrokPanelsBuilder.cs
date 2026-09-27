using System.Collections.Generic;
using AirTools.Agent.Grok;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

namespace AirTools.Editor
{
    /// Lane G3 of the Grok integration (backend docs/api.md §5 panels): the G3 card in the main window slot, the
    /// reimagine / walk-in quad in the world, the RECALLED / CAUTION banners on the spec card and the seller panel, and
    /// the GrokClient. Built with UiBuild (docs/UI.md); called once from the end of MainSceneBuilder.WireTools
    /// (AirTools ▸ Wire Main Scene), after the palm menu and the seller panel exist. Idempotent: Wire rebuilds the
    /// AirTools root, and the rig-parented parts are replaced by name.
    public static class GrokPanelsBuilder
    {
        const string MaterialsFolder = "Assets/AirTools/Materials";

        public static void Build(GameObject app, OVRCameraRig rig, SceneRoot sceneRoot, GameObject template)
        {
            app.AddComponent<GrokClient>();
            var picture = Unlit("GrokPicture");
            var ring = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialsFolder}/HoldRing.mat");
            BuildCard(rig, template, picture, ring);
            BuildQuad(app, rig, template, picture, ring);
            BuildSpecBanner(rig, template);
            BuildSellerBanner(rig, template);
            UiBuild.Distance = UiText.HandDistance;
            UiBuild.D5Rays = false;
        }

        // ---------------- the card (main window slot) ----------------

        /// One window for every G3 panel: header (title + Close), two tabs, a picture with a pinch target over it, a QR
        /// quad, body text, five rows (text + two link chips), notes, the label footer, Primary / Secondary / More and
        /// the round hold-to-post button with its ring. GrokCard lays them out to fit at runtime.
        static void BuildCard(OVRCameraRig rig, GameObject template, Material picture, Material ring)
        {
            const float W = 0.36f, pad = 0.016f, inner = W - 2f * pad;
            UiBuild.Distance = 0.45f;
            UiBuild.D5Rays = true;
            var w = Window(rig, "GrokCard", 0.45f, 20f, 0f);
            var card = w.gameObject.AddComponent<GrokCard>();
            card.window = w;
            card.width = W;
            var c = w.content.transform;
            card.panel = UiBuild.Panel(c, "Panel", new Vector2(W, 0.3f));
            var stack = new GameObject("Stack").transform;
            stack.SetParent(c, false);
            card.stack = stack;
            float x0 = -W * 0.5f + pad;

            card.title = UiBuild.Text(stack, "Title", "", TypeRole.Heading, new Vector3(x0, -0.029f, 0f), width: inner - 0.075f);
            card.close = UiBuild.Button(stack, "Close", "Close", new Vector2(0.06f, 0.026f), ButtonStyle.Borderless, template,
                new Vector3(W * 0.5f - pad - 0.03f, -0.029f, 0f), TypeRole.Caption);
            card.tabA = UiBuild.Button(stack, "TabA", "Permit", new Vector2(0.1f, 0.028f), ButtonStyle.Toggle, template, new Vector3(x0 + 0.05f, -0.066f, 0f), TypeRole.Caption);
            card.tabB = UiBuild.Button(stack, "TabB", "Money", new Vector2(0.1f, 0.028f), ButtonStyle.Toggle, template, new Vector3(x0 + 0.154f, -0.066f, 0f), TypeRole.Caption);

            card.image = Quad(stack, "Image", picture, new Vector3(0f, -0.15f, -0.0005f), new Vector3(inner, inner * 9f / 16f, 1f));
            // Built 1 × 1 m and scaled over the picture at runtime: a pinch (ray) or a poke on it flips before / after.
            card.imageToggle = UiBuild.Button(stack, "ImageToggle", "", Vector2.one, ButtonStyle.Borderless, template, new Vector3(0f, -0.15f, -0.001f), ray: true);
            card.imageToggle.transform.localScale = new Vector3(inner, inner * 9f / 16f, 1f);
            card.imageToggle.rayNeedsD5 = true;   // D5: a ray press needs a 120 ms hover (a sweeping ray never flips it)
            NoLabel(card.imageToggle);
            card.qr = Quad(stack, "Qr", picture, new Vector3(0f, -0.15f, -0.0005f), new Vector3(card.qrSize, card.qrSize, 1f));

            card.body = UiBuild.Text(stack, "Body", "", TypeRole.Label, new Vector3(x0, -0.1f, 0f), width: inner, align: TMPro.TextAlignmentOptions.TopLeft);
            var rows = new List<GrokCardRow>();
            for (int i = 0; i < 5; i++) rows.Add(Row(stack, template, i, inner));
            card.rows = rows.ToArray();
            card.notes = UiBuild.Text(stack, "Notes", "", TypeRole.Caption, new Vector3(x0, -0.2f, 0f), width: inner, align: TMPro.TextAlignmentOptions.TopLeft);
            card.footer = UiBuild.Text(stack, "Footer", "", TypeRole.Caption, new Vector3(x0, -0.22f, 0f), ColorRole.TextSecondary, width: inner, align: TMPro.TextAlignmentOptions.TopLeft);

            card.primary = UiBuild.Button(stack, "Primary", "Open", new Vector2(0.12f, 0.03f), ButtonStyle.Primary, template, new Vector3(x0 + 0.06f, -0.26f, 0f), TypeRole.Caption);
            card.secondary = UiBuild.Button(stack, "Secondary", "Show before", new Vector2(0.1f, 0.03f), ButtonStyle.Secondary, template, new Vector3(x0 + 0.178f, -0.26f, 0f), TypeRole.Caption);
            card.more = UiBuild.Button(stack, "More", "More", new Vector2(0.09f, 0.03f), ButtonStyle.Borderless, template, new Vector3(W * 0.5f - pad - 0.045f, -0.26f, 0f), TypeRole.Caption);

            // Hold-to-post (booth wall → X): a round primary button whose ring fills over 1 s; the only path that posts.
            card.hold = UiBuild.Button(stack, "HoldToPost", "Post", new Vector2(0.056f, 0.056f), ButtonStyle.Primary, template,
                new Vector3(0f, -0.3f, 0f), TypeRole.Title, RadiusRole.Pill);
            var hold = card.hold.gameObject.AddComponent<HoldToConfirm>();
            hold.button = card.hold; hold.required = 1.0f; hold.radius = 0.036f;
            var ringGo = new GameObject("Ring");
            ringGo.transform.SetParent(card.hold.transform, false);
            var lr = ringGo.AddComponent<LineRenderer>();
            lr.useWorldSpace = false; lr.widthMultiplier = 0.004f; lr.numCapVertices = 2; lr.positionCount = 0;
            lr.sharedMaterial = ring;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.sortingOrder = GlassSurface.LayerIndicator;
            hold.ring = lr;
            card.holdConfirm = hold;
            card.holdCaption = UiBuild.Text(stack, "HoldCaption", "Hold 1 s to post it on X", TypeRole.Caption, new Vector3(0f, -0.35f, 0f),
                ColorRole.TextSecondary, TMPro.TextAlignmentOptions.Center);

            foreach (var t in new Component[] { card.tabA, card.tabB, card.image, card.imageToggle, card.qr, card.primary, card.secondary, card.more, card.hold, card.holdCaption })
                t.gameObject.SetActive(false);
            w.handle = UiBuild.Handle(w.transform, rig.centerEyeAnchor, new Vector3(0f, -0.15f - 0.016f, 0f), template);
            w.handle.transform.SetParent(c, true);
            w.content.SetActive(false);
            UiBuild.D5Rays = false;
        }

        static GrokCardRow Row(Transform parent, GameObject template, int i, float width)
        {
            var go = new GameObject($"Row{i}");
            go.transform.SetParent(parent, false);
            var row = go.AddComponent<GrokCardRow>();
            row.backplate = GlassSurface.Create(go.transform, "Row", new Vector2(width, 0.04f), GlassTier.ElevatedSolid, RadiusRole.Medium, localPos: new Vector3(0f, 0f, 0.001f));
            row.text = UiBuild.Text(go.transform, "Text", "", TypeRole.Caption, new Vector3(-width * 0.5f + row.pad, 0f, 0f),
                width: width - 3f * row.pad - row.linkWidth, align: TMPro.TextAlignmentOptions.TopLeft);
            var links = new List<GlassButton>();
            for (int k = 0; k < 2; k++)
            {
                var b = UiBuild.Button(go.transform, $"Link{k}", "Link", new Vector2(row.linkWidth, row.linkHeight), ButtonStyle.Chip, template,
                    new Vector3(width * 0.5f - row.pad - row.linkWidth * 0.5f, -k * (row.linkHeight + 0.004f), 0f), TypeRole.Caption, RadiusRole.Pill);
                b.label.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                links.Add(b);
            }
            row.links = links.ToArray();
            go.SetActive(false);
            return row;
        }

        // ---------------- the reimagine / walk-in quad (world) ----------------

        /// A world quad (1 × 1 m unit Quad, scaled to the camera's frustum at runtime) with a borderless ray target over
        /// it (pinch: before / after), a label bar under it (the honesty label, the step / undo line, Show before, Close),
        /// a spinner ring and a VideoPlayer (URL source into a RenderTexture). Read from ~1.5 m: ray targets.
        static void BuildQuad(GameObject app, OVRCameraRig rig, GameObject template, Material picture, Material ring)
        {
            UiBuild.Distance = CameraQuad.Distance;
            UiBuild.D5Rays = false;
            var go = new GameObject("ReimagineQuad");
            go.transform.SetParent(app.transform, false);
            var quad = go.AddComponent<ReimagineQuad>();
            quad.head = rig.centerEyeAnchor;
            var video = go.AddComponent<VideoPlayer>();
            video.playOnAwake = false;
            video.isLooping = true;
            video.source = VideoSource.Url;
            video.renderMode = VideoRenderMode.RenderTexture;
            video.audioOutputMode = VideoAudioOutputMode.None;
            video.skipOnDrop = true;
            quad.video = video;
            quad.image = Quad(go.transform, "Image", picture, Vector3.zero, Vector3.one);
            quad.toggle = UiBuild.Button(go.transform, "Toggle", "", Vector2.one, ButtonStyle.Borderless, template, new Vector3(0f, 0f, -0.002f), ray: true);
            quad.toggle.rayNeedsD5 = true;   // D5 dwell: the measuring ray passing over the picture never flips it
            NoLabel(quad.toggle);

            const float barW = 1.0f, barH = 0.15f;
            var bar = new GameObject("LabelBar").transform;
            bar.SetParent(go.transform, false);
            quad.labelBar = bar;
            quad.labelPill = GlassSurface.Create(bar, "Pill", new Vector2(barW, barH), GlassTier.GlassRegular, RadiusRole.Large, Elevation.Subtle,
                new Vector3(0f, -barH * 0.5f, 0.003f));
            float x0 = -barW * 0.5f + 0.03f, textW = 0.66f;
            quad.label = UiBuild.Text(bar, "Label", GrokLabels.AiPreview, TypeRole.Label, new Vector3(x0, -0.035f, 0f), width: textW, weight: Weight.Semibold);
            quad.state = UiBuild.Text(bar, "State", "", TypeRole.Caption, new Vector3(x0, -0.095f, 0f), ColorRole.TextSecondary, width: textW, align: TMPro.TextAlignmentOptions.TopLeft);
            quad.state.rectTransform.pivot = new Vector2(0f, 1f);
            quad.state.transform.localPosition = new Vector3(x0, -0.065f, 0f);
            quad.state.rectTransform.sizeDelta = new Vector2(textW, 0.075f);
            quad.flipButton = UiBuild.Button(bar, "Flip", "Show before", new Vector2(0.22f, 0.055f), ButtonStyle.Chip, template,
                new Vector3(barW * 0.5f - 0.03f - 0.11f, -0.042f, 0f), TypeRole.Caption, RadiusRole.Pill, ray: true);
            quad.closeButton = UiBuild.Button(bar, "Close", "Close", new Vector2(0.22f, 0.055f), ButtonStyle.Borderless, template,
                new Vector3(barW * 0.5f - 0.03f - 0.11f, -0.105f, 0f), TypeRole.Caption, ray: true);

            var spin = new GameObject("Spinner");
            spin.transform.SetParent(go.transform, false);
            spin.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            var lr = spin.AddComponent<LineRenderer>();
            lr.useWorldSpace = false; lr.widthMultiplier = 0.012f; lr.numCapVertices = 3; lr.loop = false;
            const int n = 40;
            lr.positionCount = n;
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.PI * 0.5f - 1.5f * Mathf.PI * i / (n - 1);   // a 270° arc
                lr.SetPosition(i, new Vector3(Mathf.Cos(a) * 0.08f, Mathf.Sin(a) * 0.08f, 0f));
            }
            lr.sharedMaterial = ring;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.sortingOrder = GlassSurface.LayerIndicator;
            quad.spinner = lr;
            for (int i = 0; i < go.transform.childCount; i++) go.transform.GetChild(i).gameObject.SetActive(false);
        }

        // ---------------- safety banners ----------------

        /// RECALLED / CAUTION on the palm inspector: a glass strip just above the inspector card (child of the spec
        /// card's content, so it shows only with a selected part). Poke to open the recall notice.
        static void BuildSpecBanner(OVRCameraRig rig, GameObject template)
        {
            var spec = rig.transform.Find("PalmMenu/Content/SpecCard")?.GetComponent<SpecCard>();
            var inspector = rig.transform.Find("PalmMenu/Content/InspectorPanel")?.GetComponent<GlassSurface>();
            if (spec == null || spec.content == null || inspector == null) { Debug.LogWarning("[AirTools] G3: no spec card to put the safety pill on"); return; }
            UiBuild.Distance = UiText.HandDistance;
            UiBuild.D5Rays = false;   // the palm menu is poke-only
            var centre = inspector.transform.localPosition - spec.transform.localPosition;
            float w = inspector.size.x;
            float y = centre.y + inspector.size.y * 0.5f + 0.006f + 0.028f;
            var banner = Banner(spec.content.transform, template, w, new Vector3(centre.x, y, 0f));
            banner.spec = spec;
            BuildRulesChip(spec, banner, template, w, new Vector3(centre.x, y - 0.008f, 0f));
        }

        /// show_rules on the spec card: "Permit: yes · Permits and rebates" above the inspector (above the safety banner
        /// while that shows); a poke reopens the rules card's tabs.
        static void BuildRulesChip(SpecCard spec, SafetyBanner below, GameObject template, float w, Vector3 centre)
        {
            const float h = 0.04f;
            var go = new GameObject("RulesChip");
            go.transform.SetParent(spec.content.transform, false);
            go.transform.localPosition = centre;
            var chip = go.AddComponent<RulesChip>();
            chip.spec = spec;
            chip.below = below;
            chip.baseY = centre.y;
            chip.step = 0.056f + 0.006f;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            chip.content = content;
            var t = content.transform;
            UiBuild.Panel(t, "Panel", new Vector2(w, h), elevation: Elevation.Subtle);
            chip.button = UiBuild.Button(t, "Open", "", new Vector2(w - 0.006f, h - 0.006f), ButtonStyle.Borderless, template, Vector3.zero, TypeRole.Caption);
            NoLabel(chip.button);
            float x0 = -w * 0.5f + 0.012f;
            chip.pill = GlassSurface.Create(t, "Pill", new Vector2(0.1f, 0.022f), GlassTier.ElevatedSolid, RadiusRole.Pill, localPos: new Vector3(x0 + 0.05f, 0f, -0.001f));
            chip.pill.customTint = true;
            chip.pill.tint = UiTheme.Current.colors.warning;
            chip.pill.rim = 0f;
            chip.pill.layer = GlassSurface.LayerIndicator;
            chip.pill.Rebuild();
            chip.pillText = UiBuild.Text(chip.pill.transform, "Text", "Permit: yes", TypeRole.Caption, new Vector3(0f, 0f, -0.001f),
                align: TMPro.TextAlignmentOptions.Center, weight: Weight.Semibold);
            chip.text = UiBuild.Text(t, "Text", "Permits and rebates", TypeRole.Caption, new Vector3(x0 + 0.108f, 0f, -0.001f), width: w - 0.024f - 0.108f, weight: Weight.Medium);
            content.SetActive(false);
        }

        /// The same banner on top of the seller panel (a window: ray + poke).
        static void BuildSellerBanner(OVRCameraRig rig, GameObject template)
        {
            var sellers = rig.transform.Find("SellerPanel")?.GetComponent<SellerPanel>();
            var panel = rig.transform.Find("SellerPanel/Content/Panel")?.GetComponent<GlassSurface>();
            if (sellers == null || panel == null) { Debug.LogWarning("[AirTools] G3: no seller panel to put the safety pill on"); return; }
            UiBuild.Distance = 0.45f;
            UiBuild.D5Rays = true;
            var banner = Banner(panel.transform.parent, template, panel.size.x, new Vector3(0f, panel.size.y * 0.5f + 0.006f + 0.028f, 0f));
            banner.sellers = sellers;
            UiBuild.D5Rays = false;
        }

        static SafetyBanner Banner(Transform parent, GameObject template, float w, Vector3 centre)
        {
            const float h = 0.056f;
            var go = new GameObject("SafetyBanner");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = centre;
            var banner = go.AddComponent<SafetyBanner>();
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            banner.content = content;
            var t = content.transform;
            UiBuild.Panel(t, "Panel", new Vector2(w, h), elevation: Elevation.Subtle);
            banner.button = UiBuild.Button(t, "Open", "", new Vector2(w - 0.006f, h - 0.006f), ButtonStyle.Borderless, template, Vector3.zero, TypeRole.Caption);
            NoLabel(banner.button);
            float x0 = -w * 0.5f + 0.012f;
            banner.pill = GlassSurface.Create(t, "Pill", new Vector2(0.084f, 0.022f), GlassTier.ElevatedSolid, RadiusRole.Pill,
                localPos: new Vector3(x0 + 0.042f, 0.013f, -0.001f));
            banner.pill.customTint = true;
            banner.pill.tint = UiTheme.Current.colors.dangerFill;
            banner.pill.rim = 0f;
            banner.pill.layer = GlassSurface.LayerIndicator;
            banner.pill.Rebuild();
            banner.pillText = UiBuild.Text(banner.pill.transform, "Text", "RECALLED", TypeRole.Caption, new Vector3(0f, 0f, -0.001f),
                align: TMPro.TextAlignmentOptions.Center, weight: Weight.Semibold);
            banner.pillText.color = UiTheme.Current.colors.onAccent;
            banner.hint = UiBuild.Text(t, "Hint", "Tap for the recall notice", TypeRole.Caption, new Vector3(x0 + 0.092f, 0.013f, -0.001f),
                ColorRole.TextSecondary, width: w - 0.024f - 0.092f);
            banner.headline = UiBuild.Text(t, "Headline", "", TypeRole.Caption, new Vector3(x0, -0.013f, -0.001f), width: w - 0.024f, weight: Weight.Medium);
            banner.headline.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            banner.headline.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            content.SetActive(false);
            return banner;
        }

        // ---------------- helpers ----------------

        /// A target with no words (a picture, a whole banner): drop the empty label, which a 1 m button scaled down to
        /// the picture would otherwise shrink below the type floor.
        static void NoLabel(GlassButton b)
        {
            if (b.label != null) Object.DestroyImmediate(b.label.gameObject);
            b.label = null;
        }

        /// A main-slot floating window (UX W0.7): `distance` along a line `downDeg` below the eye line, facing the eyes.
        static FloatingWindow Window(OVRCameraRig rig, string name, float distance, float downDeg, float yawDeg)
        {
            var old = rig.transform.Find(name);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var go = new GameObject(name);
            go.transform.SetParent(rig.transform, false);
            var w = go.AddComponent<FloatingWindow>();
            w.head = rig.centerEyeAnchor;
            w.distance = distance; w.downDeg = downDeg; w.yawDeg = yawDeg; w.mainSlot = true;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            w.content = content;
            return w;
        }

        /// A unit Quad (visible from −Z, like the UI) with no collider.
        static MeshRenderer Quad(Transform parent, string name, Material mat, Vector3 localPos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        /// URP Unlit, opaque white: pictures and QR codes get their texture through a MaterialPropertyBlock.
        static Material Unlit(string name)
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
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
