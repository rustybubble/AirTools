using System.Collections.Generic;
using System.Linq;
using AirTools.Core;
using AirTools.Scene;
using Oculus.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AirTools.Editor
{
    /// AirTools ▸ Wire Main Scene: adds the app objects to Main.unity on top of the Building Blocks
    /// (Camera Rig, Passthrough, Hand/Controller Tracking, Interactions Rig, Poke Interaction). Safe to re-run.
    public static class MainSceneBuilder
    {
        public const string ScenePath = "Assets/AirTools/Scenes/Main.unity";
        const string MaterialsFolder = "Assets/AirTools/Materials";
        const string PokeBlockName = "[BuildingBlock] ISDK_PokeInteraction";

        [MenuItem("AirTools/Wire Main Scene")]
        public static void WireMenu() => Wire();

        public static string Wire()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var package = AssetDatabase.LoadAssetAtPath<ScenePackage>(SyntheticFacadeBuilder.PackagePath)
                          ?? SyntheticFacadeBuilder.Build();

            var rig = Object.FindFirstObjectByType<OVRCameraRig>();
            if (rig == null) return "No OVRCameraRig — install the Camera Rig building block first";
            var manager = rig.GetComponent<OVRManager>();
            manager.trackingOriginType = OVRManager.TrackingOrigin.FloorLevel;
            manager.isInsightPassthroughEnabled = true;
            // UX W0.11: dynamic resolution may raise the eye buffer but never drop it below native (text blurs first).
            manager.quest3MinDynamicResolutionScale = 1.0f;
            manager.quest2MinDynamicResolutionScale = 1.0f;
            EditorUtility.SetDirty(manager);
            rig.transform.SetPositionAndRotation(SyntheticFacadeSpec.SpawnPosition, Quaternion.Euler(0f, SyntheticFacadeSpec.SpawnYawDeg, 0f));

            var passthrough = Object.FindFirstObjectByType<OVRPassthroughLayer>();
            if (passthrough != null)
            {
                passthrough.overlayType = OVROverlay.OverlayType.Underlay;
                passthrough.hidden = false;
                EditorUtility.SetDirty(passthrough);
            }

            // Idempotent: remove what a previous run created.
            foreach (var name in new[] { "AirTools", "SceneRoot", "Sun" })
                if (GameObject.Find(name) is GameObject old && old.transform.parent == null) Object.DestroyImmediate(old);
            var centerEye = rig.centerEyeAnchor;
            var oldFade = centerEye.Find("FadeOverlay");
            if (oldFade != null) Object.DestroyImmediate(oldFade.gameObject);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            sun.shadows = LightShadows.None;
            sun.transform.rotation = Quaternion.Euler(45f, 160f, 0f);

            var sceneRoot = new GameObject("SceneRoot").AddComponent<SceneRoot>();

            var fade = CreateFade(centerEye);
            UiAssetsBuilder.Build();
            var template = PokeTemplate(rig.transform);
            var chest = CreateWorldButton(rig, template);

            var app = new GameObject("AirTools");
            var boot = app.AddComponent<AppBootstrap>();
            boot.scenePackage = package;
            boot.sceneRoot = sceneRoot;
            boot.rig = rig.transform;

            // No artificial locomotion (room-scale only): the ISDK FirstPersonLocomotor owns the rig pose and fights
            // the spawn placement (and wants a ground collider, which is hidden in passthrough).
            var locomotor = Object.FindFirstObjectByType<Oculus.Interaction.Locomotion.FirstPersonLocomotor>(FindObjectsInactive.Include);
            if (locomotor != null)
            {
                var locomotorRoot = locomotor.transform.parent != null && locomotor.transform.parent.name == "Locomotor"
                    ? locomotor.transform.parent.gameObject : locomotor.gameObject;
                locomotorRoot.SetActive(false);
            }
            app.AddComponent<RenderValve>();   // FFR Low in the world only (UX W0.11)
            var mode = app.AddComponent<ModeController>();
            mode.passthroughLayer = passthrough;
            mode.sceneRoot = sceneRoot;
            mode.cameras = rig.GetComponentsInChildren<Camera>(true);
            mode.fade = fade;

            WireTools(app, rig, sceneRoot, template);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            string result = $"Main wired: package={package.name} cameras={mode.cameras.Length} passthrough={(passthrough != null)} chestButton={(chest.button != null)}";
            Debug.Log($"[AirTools] {result}");
            return result;
        }

        static FadeOverlay CreateFade(Transform centerEye)
        {
            var go = new GameObject("FadeOverlay");
            go.transform.SetParent(centerEye, false);
            go.transform.localScale = Vector3.one * 0.6f; // radius 0.3 m, outside the near plane
            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = LoadOrCreateMaterial("FadeOverlay", Shader.Find("AirTools/FadeOverlay"), Color.black);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = false;
            return go.AddComponent<FadeOverlay>();
        }

        /// The Poke Interaction building block's button is kept (inactive) under the rig as the template every UI
        /// button clones its interactable from.
        static GameObject PokeTemplate(Transform rigRoot)
        {
            var holder = rigRoot.Find("UiTemplates");
            if (holder == null)
            {
                holder = new GameObject("UiTemplates").transform;
                holder.SetParent(rigRoot, false);
            }
            holder.gameObject.SetActive(false);
            var poke = Object.FindObjectsByType<PokeInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(p => p.name == PokeBlockName);
            if (poke == null) { Debug.LogWarning("[AirTools] Poke Interaction building block not found; UI buttons have no interaction"); return null; }
            var oldParent = poke.transform.parent;
            poke.transform.SetParent(holder, false);
            poke.transform.localPosition = Vector3.zero;
            poke.transform.localRotation = Quaternion.identity;
            poke.transform.localScale = Vector3.one;
            if (oldParent != null && oldParent != holder && oldParent.childCount == 0 && (oldParent.name == "Poke Interaction" || oldParent.name == "Chest"))
                Object.DestroyImmediate(oldParent.gameObject);
            return poke.gameObject;
        }

        /// Entry point (replaces the ship's chest): one primary "Enter world" pill hovering low in front of the spawn,
        /// with a first-use hint under it while in passthrough. Becomes a quiet "Exit world" once inside.
        static ChestController CreateWorldButton(OVRCameraRig rig, GameObject template)
        {
            foreach (var oldName in new[] { "Chest", "WorldButton" })
            {
                var old = rig.transform.Find(oldName);
                if (old != null) Object.DestroyImmediate(old.gameObject);
            }
            UiBuild.Distance = AirTools.UI.UiText.HandDistance;
            var root = new GameObject("WorldButton");
            root.transform.SetParent(rig.transform, false);
            // Until the head is tracked: 45 cm ahead of a standing eye, ~25° down. Then HeadAnchor places it from the
            // person (UX W0.7): 0.45 m along a line 25° below the eye line, facing the eyes; again on recentre.
            root.transform.localPosition = new Vector3(0f, 1.41f, 0.41f);
            root.transform.localRotation = Quaternion.Euler(25f, 0f, 0f);
            var anchor = root.AddComponent<AirTools.UI.HeadAnchor>();
            anchor.head = rig.centerEyeAnchor; anchor.distance = 0.45f; anchor.downDeg = 25f;
            UiBuild.D5Rays = true;   // ray + poke once D5 is on (GlassButton.RayOnWindows)
            var button = UiBuild.Button(root.transform, "Button", "Enter world", new Vector2(0.15f, 0.042f), AirTools.UI.ButtonStyle.Primary,
                template, radius: AirTools.UI.RadiusRole.Pill, role: AirTools.UI.TypeRole.Title);
            UiBuild.D5Rays = false;
            var hint = UiBuild.Text(root.transform, "Hint", "Then turn your left palm up for tools", AirTools.UI.TypeRole.Caption,
                new Vector3(0f, -0.04f, 0f), AirTools.UI.ColorRole.TextSecondary, TMPro.TextAlignmentOptions.Center);
            var controller = root.AddComponent<ChestController>();
            controller.button = button;
            controller.hint = hint.gameObject;
            controller.hideInWorld = true;   // inside the world, exit from the palm menu (it sat in the pointer's path)
            return controller;
        }

        /// M2–M4: input hub + live OVR source, tools, notebook, parts, and the design-system UI: palm menu (with the
        /// spec inspector), notebook window, crate menu, toast.
        static void WireTools(GameObject app, OVRCameraRig rig, SceneRoot sceneRoot, GameObject template)
        {
            var hub = app.AddComponent<AirTools.Input.ToolInputHub>();
            var source = app.AddComponent<AirTools.Input.OvrToolInputSource>();
            source.hub = hub;
            var rays = Object.FindObjectsByType<RayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            source.leftRays = rays.Where(r => HasAncestor(r.transform, "ComprehensiveInteractorsLeft")).OrderBy(r => r.name.StartsWith("Hand") ? 1 : 0).ToArray();
            source.rightRays = rays.Where(r => HasAncestor(r.transform, "ComprehensiveInteractorsRight")).OrderBy(r => r.name.StartsWith("Hand") ? 1 : 0).ToArray();
            var hands = rig.GetComponentsInChildren<OVRHand>(true);
            source.leftHand = hands.FirstOrDefault(h => h.name.Contains("Hand Tracking left"));
            source.rightHand = hands.FirstOrDefault(h => h.name.Contains("Hand Tracking right"));
            source.leftGrip = rig.leftHandAnchor;    // edit6dof: grip poses (controller grip / hand wrist) for turning a part by hand
            source.rightGrip = rig.rightHandAnchor;

            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            var style = new AirTools.Tools.MeasureStyle
            {
                lineMaterial = LoadOrCreateMaterial("MeasureLine", unlit, Color.white),
                markerMaterial = LoadOrCreateMaterial("MeasureMarker", unlit, Color.white),
                lineColor = AirTools.UI.UiTheme.Current.colors.ink,   // D3: measured = tape-yellow ink (was #FFC71A)
            };
            var measure = app.AddComponent<AirTools.Tools.MeasureTool>();
            measure.frame = sceneRoot.transform;
            measure.style = style;
            var level = app.AddComponent<AirTools.Tools.LevelTool>();
            level.frame = sceneRoot.transform;
            level.style = style;
            var manager = app.AddComponent<AirTools.Tools.ToolManager>();
            manager.measure = measure;
            manager.level = level;
            var table = app.AddComponent<AirTools.Scene.TabletopController>();
            table.root = sceneRoot; table.rig = rig.transform; table.head = rig.centerEyeAnchor;
            table.mode = app.GetComponent<ModeController>();
            var loco = app.AddComponent<AirTools.Input.Locomotion>();
            loco.rig = rig.transform;
            loco.head = rig.centerEyeAnchor;
            loco.discMaterial = style.lineMaterial;
            manager.locomotion = loco;
            // UX W0.5: one pooled, spatialised player for every sound, with Meta's ISDK UI sounds for buttons.
            var sfx = app.AddComponent<AirTools.UI.SfxPlayer>();
            sfx.press = IsdkSound("Interaction_BasicPoke_ButtonPress");
            sfx.rayPress = IsdkSound("Interaction_BasicRay_Press");
            sfx.hover = IsdkSound("Interaction_BasicRay_Hover");
            sfx.release = IsdkSound("Interaction_BasicGrab_Release_03");
            var serverConfig = app.AddComponent<AirTools.Core.ServerConfig>();
            // fix-ux: with no override (a launch from the Quest library), the default is probed at start and these are
            // tried in order if it doesn't answer; the first that answers is this session's server (never saved).
            serverConfig.candidateBaseUrls = new[] { "http://127.0.0.1:8004", "http://127.0.0.1:8000" };
            // Backend scene packages over HTTP (kitchen etc.); the synthetic facade stays as the built-in fallback.
            var streamer = app.AddComponent<AirTools.Scene.SceneStreamer>();
            streamer.sceneRoot = sceneRoot;
            streamer.rig = rig.transform;
            // Captures draw unlit (baked photogrammetry light) with the grow / pour reveal (AirTools/SceneReveal).
            streamer.visualMaterial = LoadOrCreateMaterial("SceneUnlit", Shader.Find("AirTools/SceneReveal"), Color.white, smoothness: 0f);
            streamer.visualMaterial.SetFloat("_FaceShading", 0f);
            EditorUtility.SetDirty(streamer.visualMaterial);
            // scalemodels: per-site default scales (the kitchen loads at ×1.63 until a person sets it; Reset goes back to it),
            // and every listed model downloaded to the headset in the background (Model view offline).
            streamer.siteScales = AirTools.Scene.SiteScales.Defaults();
            app.AddComponent<AirTools.Scene.ScenePrefetcher>().streamer = streamer;
            // Scene parts (docs/api.md parts.r<rev>.json): the removable components' node lookup; the streamer binds it.
            app.AddComponent<AirTools.Scene.SceneParts>().sceneRoot = sceneRoot;
            var notebook = app.AddComponent<AirTools.Notes.NotebookController>();
            notebook.sceneRoot = sceneRoot;
            app.AddComponent<AirTools.UI.ToastEvents>();

            // M4: parts in hand — client (server + offline catalog), loader, part tool.
            var catalog = PartCatalogBuilder.Build();
            var client = app.AddComponent<AirTools.Parts.PartsClient>();
            client.catalog = catalog;
            var loader = app.AddComponent<AirTools.Parts.PartLoader>();
            loader.client = client;
            loader.catalog = catalog;
            loader.parent = sceneRoot.transform;
            loader.style = style;
            loader.litTemplate = LoadOrCreateMaterial("PartLit", Shader.Find("Universal Render Pipeline/Lit"), Color.white, smoothness: 0.4f);
            loader.unlitTemplate = LoadOrCreateMaterial("PartUnlit", unlit, Color.white, smoothness: 0f);
            var partTool = app.AddComponent<AirTools.Parts.PartTool>();
            partTool.frame = sceneRoot.transform;
            partTool.style = style;
            manager.parts = partTool;
            var home = app.AddComponent<AirTools.Parts.TakeItHome>();
            home.rig = rig.transform; home.head = rig.centerEyeAnchor; home.tool = partTool; home.loader = loader; home.style = style;

            foreach (var oldName in new[] { "Toolbox", "SpecCard", "Toast" })
            {
                var old = rig.transform.Find(oldName);
                if (old != null) Object.DestroyImmediate(old.gameObject);
            }
            var palm = BuildPalmMenu(rig, template, partTool);
            BuildNotebookPanel(rig, template, unlit);
            CatalogBuilder.Build(app, rig, template, unlit, client, loader, partTool, catalog);   // catalog: the Catalog replaces the crate menu (BuildPartsMenu)
            BuildToast(rig);
            BuildSellerPanel(rig, template, partTool);
            BuildCheckoutPanel(rig, template, client, unlit);
            BuildSurveyCard(rig, template);
            BuildLimitsChip(rig);
            var placementEditor = PlacementPanelBuilder.Build(app, rig, template, partTool, loader, style.lineMaterial);   // placement: the editor, its panel, the gizmo
            EditViewBuilder.Build(app, rig, template, partTool, placementEditor);   // edit6dof: the Edit view, its arrows, panel, move bar, dim, context menu
            SettingsAssetsBuilder.AddSwitcher(app, partTool, loader, client);   // settings-assets: placed parts follow Settings ▸ 3D models
            WireBackend(app, rig, sceneRoot, template, style, unlit, measure);
            WirePresence(app, rig, sceneRoot, template);
            GuideRailBuilder.Build(app, rig, template);   // W1.3 guide rail (status line, Next-step pill, coach); off until GuideRail.Enabled
            LadderBuilder.Build(app, rig, sceneRoot, template, style);   // P6/P7: ladder tool, fall edges, ladder card
            // D7 (UX W1.8): demo controls. The reset gestures (hands and head come from the palm menu) and the presenter
            // link (tools/presenter/presenter_server.py, :8766); both idle outside DemoMode (on in Development builds).
            app.AddComponent<AirTools.Input.DemoResetGesture>();
            app.AddComponent<AirTools.Dev.PresenterLink>();
            GrokRailsBuilder.Build(app, rig, sceneRoot, template, style);   // Grok G4: job rail, install coach card, coach overlay, drill crosshair
            G1Builder.Build(app, rig, sceneRoot, template);   // Grok G1: view tracker (context frame), scan credit chip
            GrokPanelsBuilder.Build(app, rig, sceneRoot, template);   // Grok G3: panels, cards, image / video quad, QR codes, safety pills
            GrokOverlaysBuilder.Build(app, rig, sceneRoot, template, style);   // Grok G2: plan / survey / coverage / labels overlays
            ModelSwitcherBuilder.Build(app, rig, sceneRoot, template);   // modelview: Model view's model-only declutter and the switcher row
        }

        /// Skybox colours the World sky dome copies (Default-Skybox under the scene's Sun, sampled 2026-09-26 at
        /// elevations 0 / 89 / −2 / −30° and averaged over four headings), so World looks as before.
        const string SkyZenith = "#506588", SkyHorizon = "#ECFAFB", SkyGroundHorizon = "#ABB9BB", SkyGround = "#69635F";

        /// presence.md S1: the World sky dome, the transition director (grow in / shrink out / pour), the real table
        /// (site mat → environment raycast → assumed height) and the "You are here" pin on the tabletop model.
        static void WirePresence(GameObject app, OVRCameraRig rig, SceneRoot sceneRoot, GameObject template)
        {
            var mode = app.GetComponent<ModeController>();
            var table = app.GetComponent<AirTools.Scene.TabletopController>();
            var home = app.GetComponent<AirTools.Parts.TakeItHome>();

            var skyGo = new GameObject("SkyDome");
            skyGo.transform.SetParent(app.transform, false);
            skyGo.AddComponent<MeshFilter>();
            var skyRenderer = skyGo.AddComponent<MeshRenderer>();
            var skyMat = LoadOrCreateMaterial("SkyDome", Shader.Find("AirTools/SkyDome"), Color.white);
            foreach (var (prop, hex) in new[] { ("_Zenith", SkyZenith), ("_Horizon", SkyHorizon), ("_GroundHorizon", SkyGroundHorizon), ("_Ground", SkyGround) })
                if (ColorUtility.TryParseHtmlString(hex, out var col)) skyMat.SetColor(prop, col);
            skyMat.SetFloat("_Exponent", 0.4f);
            EditorUtility.SetDirty(skyMat);
            skyRenderer.sharedMaterial = skyMat;
            skyRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            skyRenderer.receiveShadows = false;
            skyRenderer.enabled = false;
            var sky = skyGo.AddComponent<AirTools.Scene.SkyDome>();
            sky.head = rig.centerEyeAnchor;
            mode.skyDome = sky;

            var realTable = app.AddComponent<AirTools.Scene.RealTable>();
            realTable.rig = rig.transform;
            realTable.head = rig.centerEyeAnchor;
            var mat = app.AddComponent<AirTools.Scene.SiteMatTracker>();
            mat.table = realTable;
            realTable.mat = mat;
            table.table = realTable;

            var director = app.AddComponent<TransitionDirector>();
            director.mode = mode; director.sceneRoot = sceneRoot; director.tabletop = table; director.sky = sky;
            director.rig = rig.transform; director.head = rig.centerEyeAnchor;
            mode.director = director;

            home.table = realTable; home.tabletop = table; home.director = director;
            BuildSpawnMarker(rig, template, table, mode);

            // Truth bar: a 1.000 m ruler from the mat's corner along the table edge (shown once the mat is locked).
            var barGo = new GameObject("TruthBar");
            barGo.transform.SetParent(app.transform, false);
            barGo.AddComponent<MeshFilter>();
            var barRenderer = barGo.AddComponent<MeshRenderer>();
            barRenderer.sharedMaterial = LoadOrCreateMaterial("TruthBar", Shader.Find("Universal Render Pipeline/Unlit"), Color.white, smoothness: 0f);
            barRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            barRenderer.receiveShadows = false;
            barRenderer.enabled = false;
            var bar = barGo.AddComponent<AirTools.Scene.TruthBar>();
            bar.mat = mat;
            UiBuild.Distance = 0.7f;   // read on the table, ~70 cm away (UX W0.6: size text for its real distance)
            var label = new GameObject("Label");
            label.transform.SetParent(barGo.transform, false);
            label.transform.localPosition = new Vector3(bar.length, 0.004f, -0.02f);
            label.transform.localRotation = Quaternion.Euler(60f, 0f, 0f);   // leaning back toward you, readable from above
            UiBuild.Text(label.transform, "Text", "1.000 m", AirTools.UI.TypeRole.Caption, Vector3.zero, align: TMPro.TextAlignmentOptions.Center,
                weight: AirTools.UI.Weight.Medium);
            label.SetActive(false);
            bar.label = label;
        }

        /// The "You are here" pin: a 3 cm ink glass stem on the model where you'll stand, a round foot, and a
        /// primary glass chip on top (poke or ray pinch → Step in).
        static void BuildSpawnMarker(OVRCameraRig rig, GameObject template, AirTools.Scene.TabletopController table, ModeController mode)
        {
            var old = rig.transform.Find("SpawnMarker");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            UiBuild.Distance = 0.6f;   // the model on the table, ~60 cm away
            var go = new GameObject("SpawnMarker");
            go.transform.SetParent(rig.transform, false);
            var marker = go.AddComponent<AirTools.Scene.SpawnMarker>();
            marker.tabletop = table; marker.mode = mode; marker.head = rig.centerEyeAnchor;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            marker.content = content;
            var c = content.transform;
            var ink = AirTools.UI.UiTheme.Current.colors.ink;   // D3: "you are here" is ink
            var stem = AirTools.UI.GlassSurface.Create(c, "Stem", new Vector2(0.0016f, marker.height), AirTools.UI.GlassTier.ElevatedSolid,
                AirTools.UI.RadiusRole.Pill, localPos: new Vector3(0f, marker.height * 0.5f, 0f));
            stem.customTint = true; stem.tint = ink; stem.rim = 0f; stem.layer = AirTools.UI.GlassSurface.LayerIndicator; stem.Rebuild();
            var foot = AirTools.UI.GlassSurface.Create(c, "Foot", new Vector2(0.009f, 0.009f), AirTools.UI.GlassTier.ElevatedSolid,
                AirTools.UI.RadiusRole.Pill, localPos: new Vector3(0f, 0.0005f, 0f));
            foot.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);   // lies on the model's ground
            foot.customTint = true; foot.tint = ink; foot.rim = 0f; foot.layer = AirTools.UI.GlassSurface.LayerIndicator; foot.Rebuild();
            var chip = UiBuild.Button(c, "Chip", "You are here", new Vector2(0.08f, 0.026f), AirTools.UI.ButtonStyle.Primary, template,
                new Vector3(0f, marker.height + 0.013f, 0f), AirTools.UI.TypeRole.Caption, AirTools.UI.RadiusRole.Pill, ray: true);
            marker.button = chip;
            content.SetActive(false);
        }

        /// Backend integration (airtools-drone-backend): calibration sync, snap cursor, structure overlay, the agent,
        /// voice and ask-the-scene clients, and the Scene window.
        static void WireBackend(GameObject app, OVRCameraRig rig, SceneRoot sceneRoot, GameObject template,
            AirTools.Tools.MeasureStyle style, Shader unlit, AirTools.Tools.MeasureTool measure)
        {
            var sync = app.AddComponent<AirTools.Structure.CalibrationSync>();
            sync.sceneRoot = sceneRoot; sync.rig = rig.transform;
            var cursor = new GameObject("SnapCursor").AddComponent<AirTools.Structure.SnapCursor>();
            cursor.transform.SetParent(app.transform, false);
            cursor.tool = measure; cursor.style = style;
            var overlay = app.AddComponent<AirTools.Structure.StructureOverlay>();
            overlay.sceneRoot = sceneRoot;
            overlay.material = LoadOrCreateMaterial("StructureOverlay", unlit, Color.white, smoothness: 0f);
            var guides = app.AddComponent<AirTools.Parts.PlacementGuides>();
            guides.material = overlay.material;
            var agent = app.AddComponent<AirTools.Agent.AgentClient>();
            var voice = app.AddComponent<AirTools.Agent.VoiceClient>();
            voice.agent = agent;
            var ask = app.AddComponent<AirTools.Agent.SceneAsk>();
            ask.sceneRoot = sceneRoot; ask.style = style;
            // B1: the agent measures with the real tape (survey / check_slope).
            var survey = app.AddComponent<AirTools.Agent.SurveyRunner>();
            survey.tool = measure; survey.root = sceneRoot;
            BuildScenePanel(rig, template);
        }

        /// Settings (scalemodels: was "More"; declutter DC3, M7: the Scene window, renamed; the right side slot). Settings
        /// first, scene actions below. Rows, top to bottom:
        /// - "Settings" + Close;
        /// - status (scene · scale · the scan credit · models on the headset);
        /// - Snapping / Contrast / Less motion;
        /// - the scale keypad and the units chip;
        /// - Layers: Show edges / Labels (GrokOverlaysBuilder fills the "LabelsSlot") / Fall edges;
        /// - Ask + hold to talk;
        /// - Home / Ladder / Exit world (tap twice);
        /// - sites: Next scene / Reload / Built-in;
        /// - Take out: one chip per removable scene part (ScenePartsBuilder);
        /// - settings-assets: Talk (Hold · Toggle · Auto) and 3D models (HF · LLM+CAD · Auto) (SettingsAssetsBuilder).
        /// Picking Ladder or Exit closes it.
        static void BuildScenePanel(OVRCameraRig rig, GameObject template)
        {
            UiBuild.Distance = 0.5f;
            UiBuild.D5Rays = true;
            var w = Window(rig, "ScenePanel", 0.5f, 18f, 35f, mainSlot: false);   // right side slot, behind the main one (UX W0.7)
            var panel = w.gameObject.AddComponent<AirTools.Structure.ScenePanel>();
            panel.window = w;
            var c = w.content.transform;
            const float W = 0.34f, H = SettingsPanelHeight;
            UiBuild.Panel(c, "Panel", new Vector2(W, H));
            float x0 = -W * 0.5f + 0.016f, top = H * 0.5f;
            // Title rows hold only the title and Close (M8).
            panel.title = UiBuild.Text(c, "Title", AirTools.Structure.ScenePanel.Title, AirTools.UI.TypeRole.Heading, new Vector3(x0, top - 0.026f, 0f), width: W - 0.1f);
            panel.status = UiBuild.Text(c, "Status", "", AirTools.UI.TypeRole.Caption, new Vector3(x0, top - 0.058f, 0f), AirTools.UI.ColorRole.TextSecondary, width: W - 0.03f);
            AirTools.UI.GlassButton Btn(string name, string text, Vector2 size, AirTools.UI.ButtonStyle st, Vector3 pos, AirTools.Structure.ScenePanelAction a, string arg = null)
            {
                var b = UiBuild.Button(c, name, text, size, st, template, pos, AirTools.UI.TypeRole.Caption,
                    st == AirTools.UI.ButtonStyle.Chip ? AirTools.UI.RadiusRole.Pill : AirTools.UI.RadiusRole.Small);
                var sb = b.gameObject.AddComponent<AirTools.Structure.ScenePanelButton>();
                sb.button = b; sb.panel = panel; sb.action = a; sb.arg = arg;
                return b;
            }
            var bw = new Vector2(0.098f, 0.028f);
            // scalemodels: settings first (snapping, the display settings, scale and units), then layers, then the scene
            // actions (Home / Ladder / Exit world, the sites, Take out). The same rows as before, so the same height.
            // Settings: snapping and the two display settings (they lived on the old palm toolbox).
            float row = top - 0.103f;
            var chip = new Vector2(0.098f, 0.026f);
            panel.snapping = Btn("Snapping", "Snapping", chip, AirTools.UI.ButtonStyle.Chip, new Vector3(-0.104f, row, 0f), AirTools.Structure.ScenePanelAction.ToggleSnapping);
            AddToolButton(c, "Contrast", AirTools.Tools.ToolboxAction.ToggleContrast, chip, new Vector3(0f, row, 0f), AirTools.UI.ButtonStyle.Chip, template, null, AirTools.UI.TypeRole.Caption);
            AddToolButton(c, "Less motion", AirTools.Tools.ToolboxAction.ToggleMotion, chip, new Vector3(0.104f, row, 0f), AirTools.UI.ButtonStyle.Chip, template, null, AirTools.UI.TypeRole.Caption);
            // Scale from a known dimension: the latest tape vs the real length typed on the keypad (cm).
            row -= 0.03f;
            float keypadRow = row;
            panel.scaleText = UiBuild.Text(c, "Scale", "", AirTools.UI.TypeRole.Caption, new Vector3(x0, row - 0.012f, 0f), width: 0.15f, weight: AirTools.UI.Weight.Medium);
            var kw = new Vector2(0.036f, 0.026f);
            string[] keys = { "7", "8", "9", "4", "5", "6", "1", "2", "3", ".", "0", "Del" };
            for (int i = 0; i < keys.Length; i++)
            {
                int r = i / 3, col = i % 3;
                var pos = new Vector3(0.028f + col * 0.04f, row - 0.004f - r * 0.03f, 0f);
                var a = keys[i] == "." ? AirTools.Structure.ScenePanelAction.Point : keys[i] == "Del" ? AirTools.Structure.ScenePanelAction.Backspace : AirTools.Structure.ScenePanelAction.Digit;
                Btn($"Key{i}", keys[i], kw, AirTools.UI.ButtonStyle.Secondary, pos, a, keys[i]);
            }
            Btn("SetScale", "Set scale", new Vector2(0.07f, 0.028f), AirTools.UI.ButtonStyle.Primary, new Vector3(x0 + 0.035f, row - 0.064f, 0f), AirTools.Structure.ScenePanelAction.SetScale);
            var reset = Btn("ResetScale", "Reset", new Vector2(0.06f, 0.028f), AirTools.UI.ButtonStyle.Borderless, new Vector3(x0 + 0.11f, row - 0.064f, 0f), AirTools.Structure.ScenePanelAction.ResetScale);
            reset.confirm = true; reset.confirmText = "Tap to reset";
            // Layers: structure edges, the scan's labels (built by GrokOverlaysBuilder into this slot), fall edges.
            row -= 0.132f;
            panel.structure = Btn("Structure", "Show edges", bw, AirTools.UI.ButtonStyle.Toggle, new Vector3(-0.104f, row, 0f), AirTools.Structure.ScenePanelAction.ToggleStructure);
            var labelsSlot = new GameObject(SettingsLabelsSlot);
            labelsSlot.transform.SetParent(c, false);
            labelsSlot.transform.localPosition = new Vector3(0f, row, 0f);
            panel.fallEdges = Btn("FallEdges", "Fall edges", bw, AirTools.UI.ButtonStyle.Toggle, new Vector3(0.104f, row, 0f), AirTools.Structure.ScenePanelAction.FallEdges);
            row -= 0.034f;
            Btn("Ask", "What is this?", new Vector2(0.15f, 0.032f), AirTools.UI.ButtonStyle.Secondary, new Vector3(-0.078f, row, 0f), AirTools.Structure.ScenePanelAction.Ask);
            panel.talk = UiBuild.Button(c, "Talk", AirTools.Agent.TalkButton.IdleText, new Vector2(0.15f, 0.032f), AirTools.UI.ButtonStyle.Primary, template, new Vector3(0.078f, row, 0f), AirTools.UI.TypeRole.Caption);   // voice: tap or hold
            row -= 0.032f;
            panel.voiceText = UiBuild.Text(c, "Voice", "", AirTools.UI.TypeRole.Caption, new Vector3(x0, row, 0f), AirTools.UI.ColorRole.TextSecondary, width: W - 0.03f);
            // Scene actions. Recovery: Home, Ladder (a tool that isn't on the ring), Exit world (asks twice, like a
            // destructive action).
            row -= 0.034f;
            panel.home = Btn("Home", "Home", bw, AirTools.UI.ButtonStyle.Secondary, new Vector3(-0.104f, row, 0f), AirTools.Structure.ScenePanelAction.Home);
            panel.ladder = Btn("Ladder", "Ladder", bw, AirTools.UI.ButtonStyle.Toggle, new Vector3(0f, row, 0f), AirTools.Structure.ScenePanelAction.Ladder);
            panel.exitWorld = Btn("ExitWorld", "Exit world", bw, AirTools.UI.ButtonStyle.Secondary, new Vector3(0.104f, row, 0f), AirTools.Structure.ScenePanelAction.ExitWorld);
            panel.exitWorld.confirm = true; panel.exitWorld.confirmText = "Tap to leave";
            // Sites (recovery for the wrong scene or server).
            row -= 0.034f;
            Btn("NextSite", "Next scene", bw, AirTools.UI.ButtonStyle.Secondary, new Vector3(-0.104f, row, 0f), AirTools.Structure.ScenePanelAction.NextSite);
            panel.reload = Btn("Reload", "Reload", bw, AirTools.UI.ButtonStyle.Secondary, new Vector3(0f, row, 0f), AirTools.Structure.ScenePanelAction.Reload);   // fix-ux: reads Retry after a failed load
            Btn("BuiltIn", "Built-in", bw, AirTools.UI.ButtonStyle.Secondary, new Vector3(0.104f, row, 0f), AirTools.Structure.ScenePanelAction.BuiltIn);
            UnitsChipBuilder.Build(c, template, new Vector3(x0 + 0.05f, keypadRow - 0.097f, 0f));   // D2: units chip, under Set scale
            ScenePartsBuilder.Build(c, template, new Vector3(x0, row - 0.034f, 0f), W - 0.032f);   // scene parts: Take out
            SettingsAssetsBuilder.BuildSettingsRows(c, template, new Vector3(x0, row - 0.068f, 0f), W - 0.032f);   // settings-assets: Talk · 3D models (the last two rows)
            Btn("Close", "Close", new Vector2(0.06f, 0.026f), AirTools.UI.ButtonStyle.Borderless, new Vector3(W * 0.5f - 0.046f, top - 0.026f, 0f), AirTools.Structure.ScenePanelAction.Close);
            w.handle = UiBuild.Handle(w.transform, rig.centerEyeAnchor, new Vector3(0f, -H * 0.5f - 0.016f, 0f), template);
            w.handle.transform.SetParent(c, true);
            w.content.SetActive(false);
            UiBuild.D5Rays = false;
        }

        /// Settings' panel height (declutter M7: the Scene window's 0.392 m plus one 0.034 m row; scene parts: one more for
        /// Take out) and the empty transform in its Layers row that GrokOverlaysBuilder puts the scan-labels toggle on.
        /// scalemodels: Settings was "More".
        public const float SettingsPanelHeight = 0.46f + SettingsAssetsBuilder.RowsHeight;   // settings-assets: + Talk · 3D models
        public const string SettingsLabelsSlot = "LabelsSlot";

        /// A floating window placed from the person (UX W0.7): `distance` along a line `downDeg` below the eye line,
        /// `yawDeg` to the side (side slots), facing the eyes; main-slot windows close each other (WindowSlot).
        static AirTools.UI.FloatingWindow Window(OVRCameraRig rig, string name, float distance, float downDeg, float yawDeg, bool mainSlot = true)
        {
            var old = rig.transform.Find(name);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var go = new GameObject(name);
            go.transform.SetParent(rig.transform, false);
            var w = go.AddComponent<AirTools.UI.FloatingWindow>();
            w.head = rig.centerEyeAnchor;
            w.distance = distance; w.downDeg = downDeg; w.yawDeg = yawDeg; w.mainSlot = mainSlot;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            w.content = content;
            return w;
        }

        /// Sellers (SPEC M5): title + recommendation, a Price / Delivery segmented sort, three seller cards
        /// (ElevatedSolid) with Pay with Visa and Open at seller, Close; movable.
        static void BuildSellerPanel(OVRCameraRig rig, GameObject template, AirTools.Parts.PartTool tool)
        {
            UiBuild.Distance = 0.45f;
            UiBuild.D5Rays = true;
            var w = Window(rig, "SellerPanel", 0.45f, 20f, 0f);
            var panel = w.gameObject.AddComponent<AirTools.Parts.SellerPanel>();
            panel.window = w; panel.tool = tool;
            var c = w.content.transform;
            const float W = 0.36f, H = 0.37f;
            UiBuild.Panel(c, "Panel", new Vector2(W, H));
            float x0 = -W * 0.5f + 0.016f;
            panel.title = UiBuild.Text(c, "Title", "Sellers", AirTools.UI.TypeRole.Heading, new Vector3(x0, H * 0.5f - 0.026f, 0f), width: W - 0.03f);
            panel.reason = UiBuild.Text(c, "Reason", "", AirTools.UI.TypeRole.Caption, new Vector3(x0, H * 0.5f - 0.052f, 0f), AirTools.UI.ColorRole.TextSecondary, width: W - 0.03f);
            float segW = 0.1f;
            var sp = UiBuild.Button(c, "SortPrice", "Cheapest", new Vector2(segW, 0.028f), AirTools.UI.ButtonStyle.Toggle, template, new Vector3(x0 + segW * 0.5f, H * 0.5f - 0.084f, 0f), AirTools.UI.TypeRole.Caption);
            var se = UiBuild.Button(c, "SortEta", "Fastest", new Vector2(segW, 0.028f), AirTools.UI.ButtonStyle.Toggle, template, new Vector3(x0 + segW * 1.5f + 0.004f, H * 0.5f - 0.084f, 0f), AirTools.UI.TypeRole.Caption);
            foreach (var (b, a) in new[] { (sp, AirTools.Parts.SellerAction.SortPrice), (se, AirTools.Parts.SellerAction.SortEta) })
            { var sbn = b.gameObject.AddComponent<AirTools.Parts.SellerButton>(); sbn.button = b; sbn.panel = panel; sbn.action = a; }
            panel.sortPrice = sp; panel.sortEta = se;
            var close = UiBuild.Button(c, "Close", "Close", new Vector2(0.07f, 0.028f), AirTools.UI.ButtonStyle.Borderless, template, new Vector3(W * 0.5f - 0.016f - 0.035f, H * 0.5f - 0.084f, 0f), AirTools.UI.TypeRole.Caption);
            var cb = close.gameObject.AddComponent<AirTools.Parts.SellerButton>(); cb.button = close; cb.panel = panel; cb.action = AirTools.Parts.SellerAction.Close;
            var rows = new List<AirTools.Parts.SellerRowView>();
            const float rowH = 0.074f;
            for (int i = 0; i < 3; i++)
            {
                var rowGo = new GameObject($"Seller{i}");
                rowGo.transform.SetParent(c, false);
                rowGo.transform.localPosition = new Vector3(0f, H * 0.5f - 0.143f - i * (rowH + 0.006f), 0f);
                var row = rowGo.AddComponent<AirTools.Parts.SellerRowView>();
                row.backplate = AirTools.UI.GlassSurface.Create(rowGo.transform, "Row", new Vector2(W - 0.024f, rowH), AirTools.UI.GlassTier.ElevatedSolid, AirTools.UI.RadiusRole.Medium, localPos: new Vector3(0f, 0f, 0.001f));
                float tx = -W * 0.5f + 0.024f, tw = 0.2f;
                row.nameText = UiBuild.Text(rowGo.transform, "Name", "", AirTools.UI.TypeRole.Label, new Vector3(tx, 0.021f, 0f), width: tw, weight: AirTools.UI.Weight.Semibold);
                row.priceText = UiBuild.Text(rowGo.transform, "Price", "", AirTools.UI.TypeRole.Label, new Vector3(tx, 0.0f, 0f), width: tw, weight: AirTools.UI.Weight.Medium);
                row.detailText = UiBuild.Text(rowGo.transform, "Detail", "", AirTools.UI.TypeRole.Caption, new Vector3(tx, -0.022f, 0f), AirTools.UI.ColorRole.TextSecondary, width: tw);
                var tag = new GameObject("Recommended");
                tag.transform.SetParent(rowGo.transform, false);
                tag.transform.localPosition = new Vector3(tx + 0.155f, 0.021f, 0f);
                var tagPill = AirTools.UI.GlassSurface.Create(tag.transform, "Pill", new Vector2(0.06f, 0.018f), AirTools.UI.GlassTier.ElevatedSolid, AirTools.UI.RadiusRole.Pill);
                tagPill.customTint = true; tagPill.tint = AirTools.UI.UiTheme.Current.colors.ink; tagPill.rim = 0f; tagPill.layer = AirTools.UI.GlassSurface.LayerIndicator; tagPill.Rebuild();   // D3 badge: ink + onInk
                var tagText = UiBuild.Text(tag.transform, "Text", "Best pick", AirTools.UI.TypeRole.Caption, new Vector3(0f, 0f, -0.001f), align: TMPro.TextAlignmentOptions.Center, weight: AirTools.UI.Weight.Semibold);
                tagText.color = AirTools.UI.UiTheme.Current.colors.onInk;
                row.recommendedTag = tag;
                var bx = W * 0.5f - 0.012f - 0.052f;
                row.payButton = UiBuild.Button(rowGo.transform, "Pay", "Pay with Visa", new Vector2(0.1f, 0.028f), AirTools.UI.ButtonStyle.Primary, template, new Vector3(bx, 0.017f, 0f), AirTools.UI.TypeRole.Caption);
                row.openButton = UiBuild.Button(rowGo.transform, "Open", "See listing", new Vector2(0.1f, 0.028f), AirTools.UI.ButtonStyle.Secondary, template, new Vector3(bx, -0.017f, 0f), AirTools.UI.TypeRole.Caption);
                foreach (var (b, a) in new[] { (row.payButton, AirTools.Parts.SellerAction.Pay), (row.openButton, AirTools.Parts.SellerAction.Open) })
                { var sbn = b.gameObject.AddComponent<AirTools.Parts.SellerButton>(); sbn.button = b; sbn.panel = panel; sbn.action = a; sbn.row = i; }
                rows.Add(row);
            }
            panel.rows = rows.ToArray();
            w.handle = UiBuild.Handle(w.transform, rig.centerEyeAnchor, new Vector3(0f, -H * 0.5f - 0.016f, 0f), template);
            w.handle.transform.SetParent(c, true);
            w.content.SetActive(false);
            UiBuild.D5Rays = false;
        }

        /// Checkout (SPEC M5 + the measured mandate, B3): order lines, quantity −/+, the session's limits, the server's
        /// checks (✓ / ⚠ / ✗ rows), Visa •••• 1111, and a round hold-to-pay button with a ring that fills over 1 s; the
        /// receipt (with the mandate chain in the rows block, the evidence photo and a Take it home primary) replaces it
        /// once authorized.
        static void BuildCheckoutPanel(OVRCameraRig rig, GameObject template, AirTools.Parts.PartsClient client, Shader unlit)
        {
            UiBuild.Distance = 0.45f;
            UiBuild.D5Rays = true;
            var w = Window(rig, "CheckoutPanel", 0.45f, 20f, 0f);   // takes the Sellers slot (Back returns)
            var panel = w.gameObject.AddComponent<AirTools.Parts.CheckoutPanel>();
            panel.window = w; panel.client = client;
            var c = w.content.transform;
            const float W = 0.36f, H = 0.44f;
            float top = H * 0.5f;
            UiBuild.Panel(c, "Panel", new Vector2(W, H), elevation: AirTools.UI.Elevation.Modal);
            float x0 = -W * 0.5f + 0.016f;
            panel.title = UiBuild.Text(c, "Title", "Checkout", AirTools.UI.TypeRole.Heading, new Vector3(x0, top - 0.026f, 0f), width: W - 0.03f);
            panel.lines = UiBuild.Text(c, "Lines", "", AirTools.UI.TypeRole.Caption, new Vector3(x0, top - 0.075f, 0f), width: W - 0.03f, weight: AirTools.UI.Weight.Medium);
            panel.limits = UiBuild.Text(c, "Limits", "", AirTools.UI.TypeRole.Caption, new Vector3(x0, top - 0.117f, 0f), AirTools.UI.ColorRole.TextSecondary, width: W - 0.03f);
            // Checks (and the receipt's mandate chain): rows growing down from a fixed top.
            panel.checks = UiBuild.Text(c, "Checks", "", AirTools.UI.TypeRole.Caption, new Vector3(x0, top - 0.136f, 0f), width: W - 0.03f, align: TMPro.TextAlignmentOptions.TopLeft);
            panel.card = UiBuild.Text(c, "Card", "", AirTools.UI.TypeRole.Caption, new Vector3(x0, 0.004f, 0f), AirTools.UI.ColorRole.TextSecondary, width: W - 0.03f);
            panel.status = UiBuild.Text(c, "Status", "", AirTools.UI.TypeRole.Caption, new Vector3(x0, -0.02f, 0f), width: W - 0.03f);
            panel.qtyMinus = UiBuild.Button(c, "QtyMinus", "–", new Vector2(0.03f, 0.03f), AirTools.UI.ButtonStyle.Secondary, template, new Vector3(W * 0.5f - 0.016f - 0.051f, top - 0.03f, 0f), AirTools.UI.TypeRole.Title, AirTools.UI.RadiusRole.Pill);
            panel.qtyPlus = UiBuild.Button(c, "QtyPlus", "+", new Vector2(0.03f, 0.03f), AirTools.UI.ButtonStyle.Secondary, template, new Vector3(W * 0.5f - 0.016f - 0.015f, top - 0.03f, 0f), AirTools.UI.TypeRole.Title, AirTools.UI.RadiusRole.Pill);
            // Hold-to-pay: a round primary button with a ring that fills while it's held.
            var payPos = new Vector3(0f, -H * 0.5f + 0.11f, 0f);
            var pay = UiBuild.Button(c, "HoldToPay", "Pay", new Vector2(0.056f, 0.056f), AirTools.UI.ButtonStyle.Primary, template, payPos, AirTools.UI.TypeRole.Title, AirTools.UI.RadiusRole.Pill);
            var hold = pay.gameObject.AddComponent<AirTools.UI.HoldToConfirm>();
            hold.button = pay; hold.required = 1.0f; hold.radius = 0.036f;
            var ringGo = new GameObject("Ring");
            ringGo.transform.SetParent(pay.transform, false);
            var ring = ringGo.AddComponent<LineRenderer>();
            ring.useWorldSpace = false; ring.widthMultiplier = 0.004f; ring.numCapVertices = 2; ring.positionCount = 0;
            ring.sharedMaterial = LoadOrCreateMaterial("HoldRing", Shader.Find("Universal Render Pipeline/Unlit"), AirTools.UI.UiTheme.Current.colors.ink);   // D3: ink fills round the white Pay
            ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ring.sortingOrder = AirTools.UI.GlassSurface.LayerIndicator;
            hold.ring = ring;
            panel.hold = hold;
            var receipt = new GameObject("Receipt");
            receipt.transform.SetParent(c, false);
            const float cardH = 0.1f, thumbW = 0.072f;
            AirTools.UI.GlassSurface.Create(receipt.transform, "Card", new Vector2(W - 0.03f, cardH), AirTools.UI.GlassTier.ElevatedSolid, AirTools.UI.RadiusRole.Medium, localPos: new Vector3(0f, payPos.y, 0.001f));
            panel.receiptText = UiBuild.Text(receipt.transform, "Text", "", AirTools.UI.TypeRole.Caption, new Vector3(x0 + 0.01f, payPos.y, 0f), width: W - 0.06f - thumbW, weight: AirTools.UI.Weight.Medium);
            var thumb = GameObject.CreatePrimitive(PrimitiveType.Quad);
            thumb.name = "EvidenceThumb";
            Object.DestroyImmediate(thumb.GetComponent<Collider>());
            thumb.transform.SetParent(receipt.transform, false);
            thumb.transform.localPosition = new Vector3(W * 0.5f - 0.015f - 0.008f - thumbW * 0.5f, payPos.y, -0.001f);
            thumb.transform.localScale = new Vector3(thumbW, thumbW * 0.75f, 1f);
            thumb.GetComponent<MeshRenderer>().sharedMaterial = LoadOrCreateMaterial("NotebookThumb", unlit, Color.white);
            thumb.SetActive(false);
            panel.evidenceThumb = thumb.GetComponent<MeshRenderer>();
            // Declutter M3 (the W1.6 CTA): the receipt's own primary, bottom centre between Back and Close. The Next-step
            // pill yields while this window is open, so Take it home lives here (the judge's path stays at 10 actions).
            panel.takeHome = UiBuild.Button(receipt.transform, "TakeHome", "Take it home", new Vector2(0.12f, 0.032f), AirTools.UI.ButtonStyle.Primary,
                template, new Vector3(0f, -H * 0.5f + 0.022f, 0f), AirTools.UI.TypeRole.Label, AirTools.UI.RadiusRole.Pill);
            panel.receipt = receipt;
            receipt.SetActive(false);
            var close = UiBuild.Button(c, "Close", "Close", new Vector2(0.06f, 0.026f), AirTools.UI.ButtonStyle.Borderless, template, new Vector3(W * 0.5f - 0.016f - 0.03f, -H * 0.5f + 0.02f, 0f), AirTools.UI.TypeRole.Caption);
            var closer = close.gameObject.AddComponent<AirTools.Parts.CheckoutClose>(); closer.button = close; closer.panel = panel;
            var back = UiBuild.Button(c, "Back", "Back", new Vector2(0.06f, 0.026f), AirTools.UI.ButtonStyle.Borderless, template, new Vector3(-W * 0.5f + 0.016f + 0.03f, -H * 0.5f + 0.02f, 0f), AirTools.UI.TypeRole.Caption);
            var backer = back.gameObject.AddComponent<AirTools.Parts.CheckoutClose>(); backer.button = back; backer.panel = panel; backer.back = true;
            w.handle = UiBuild.Handle(w.transform, rig.centerEyeAnchor, new Vector3(0f, -H * 0.5f - 0.016f, 0f), template);
            w.handle.transform.SetParent(c, true);
            w.content.SetActive(false);
            UiBuild.D5Rays = false;
        }

        /// Survey card (B1 show_survey): title, up to 6 size-group rows (most common first, amber unverified ids, the
        /// focused group marked), a footer (amber / skipped) and Close. A main-slot window.
        static void BuildSurveyCard(OVRCameraRig rig, GameObject template)
        {
            UiBuild.Distance = 0.45f;
            UiBuild.D5Rays = true;
            var w = Window(rig, "SurveyCard", 0.45f, 20f, 0f);
            var card = w.gameObject.AddComponent<AirTools.Agent.SurveyCard>();
            card.window = w;
            var c = w.content.transform;
            const float W = 0.36f, H = 0.2f;
            float top = H * 0.5f, x0 = -W * 0.5f + 0.016f;
            UiBuild.Panel(c, "Panel", new Vector2(W, H));
            card.title = UiBuild.Text(c, "Title", "Survey", AirTools.UI.TypeRole.Heading, new Vector3(x0, top - 0.026f, 0f), width: W - 0.1f);
            card.body = UiBuild.Text(c, "Rows", "", AirTools.UI.TypeRole.Label, new Vector3(x0, top - 0.062f, 0f), width: W - 0.03f, align: TMPro.TextAlignmentOptions.TopLeft);
            card.footer = UiBuild.Text(c, "Footer", "", AirTools.UI.TypeRole.Caption, new Vector3(x0, -top + 0.024f, 0f), AirTools.UI.ColorRole.TextSecondary, width: W - 0.03f);
            card.close = UiBuild.Button(c, "Close", "Close", new Vector2(0.06f, 0.026f), AirTools.UI.ButtonStyle.Borderless, template, new Vector3(W * 0.5f - 0.016f - 0.03f, top - 0.026f, 0f), AirTools.UI.TypeRole.Caption);
            w.handle = UiBuild.Handle(w.transform, rig.centerEyeAnchor, new Vector3(0f, -H * 0.5f - 0.016f, 0f), template);
            w.handle.transform.SetParent(c, true);
            w.content.SetActive(false);
            UiBuild.D5Rays = false;
        }

        /// The wrist strip (declutter M9, DC5; was the B3 limits chip): 0.16 m wide, 5 cm above the left wrist or
        /// controller. Line 1: the limits ("≤ $40 · by Fri · fastest") and the scale state; under it, the loaded scan's
        /// credit. LimitsChip stacks the two and fits the pill at runtime; it hides when the wrist isn't tracked.
        static void BuildLimitsChip(OVRCameraRig rig)
        {
            var old = rig.transform.Find("LimitsChip");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            UiBuild.Distance = AirTools.UI.UiText.HandDistance;
            var go = new GameObject("LimitsChip");
            go.transform.SetParent(rig.transform, false);
            var chip = go.AddComponent<AirTools.Parts.LimitsChip>();
            chip.head = rig.centerEyeAnchor;
            chip.controllerAnchor = rig.leftHandAnchor;
            chip.hand = rig.transform.GetComponentsInChildren<Oculus.Interaction.Input.Hand>(true)
                .FirstOrDefault(h => h.GetType() == typeof(Oculus.Interaction.Input.Hand) && h.name == "ComprehensiveInteractorsLeft");
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            chip.content = content;
            float inner = chip.width - 2f * chip.padding;
            chip.pill = AirTools.UI.GlassSurface.Create(content.transform, "Pill", new Vector2(chip.width, 0.026f), AirTools.UI.GlassTier.GlassRegular, AirTools.UI.RadiusRole.Medium, AirTools.UI.Elevation.Subtle);
            chip.text = UiBuild.Text(content.transform, "Text", "", AirTools.UI.TypeRole.Label, new Vector3(0f, 0.006f, -0.002f),
                align: TMPro.TextAlignmentOptions.Top, width: inner, weight: AirTools.UI.Weight.Medium);
            chip.text.rectTransform.pivot = new Vector2(0.5f, 1f);
            chip.creditText = UiBuild.Text(content.transform, "Credit", "", AirTools.UI.TypeRole.Caption, new Vector3(0f, -0.006f, -0.002f),
                AirTools.UI.ColorRole.TextSecondary, TMPro.TextAlignmentOptions.Top, inner);
            chip.creditText.rectTransform.pivot = new Vector2(0.5f, 1f);
            content.SetActive(false);
        }

        /// Palm menu: one glass panel that opens just above the left palm (hand tracking) or the left controller
        /// (menu button). Tools curve gently around the hand; actions below; a gesture hint; two accessibility
        /// chips. When a part is selected its spec inspector shows on a glass card above the ring.
        static AirTools.Input.PalmMenu BuildPalmMenu(OVRCameraRig rig, GameObject template, AirTools.Parts.PartTool partTool)
        {
            var old = rig.transform.Find("PalmMenu");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            UiBuild.Distance = AirTools.UI.UiText.HandDistance;
            var go = new GameObject("PalmMenu");
            go.transform.SetParent(rig.transform, false);
            var menu = go.AddComponent<AirTools.Input.PalmMenu>();
            var left = rig.transform.GetComponentsInChildren<Oculus.Interaction.Input.Hand>(true)
                .FirstOrDefault(h => h.GetType() == typeof(Oculus.Interaction.Input.Hand) && h.name == "ComprehensiveInteractorsLeft");
            if (left == null) Debug.LogWarning("[AirTools] Left ISDK Hand not found; the palm menu opens with the controller menu button only");
            menu.hand = left;
            menu.head = rig.centerEyeAnchor;
            menu.controllerAnchor = rig.leftHandAnchor;
            menu.otherHand = rig.transform.GetComponentsInChildren<Oculus.Interaction.Input.Hand>(true)
                .FirstOrDefault(h => h.GetType() == typeof(Oculus.Interaction.Input.Hand) && h.name == "ComprehensiveInteractorsRight");
            menu.otherAnchor = rig.rightHandAnchor;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            menu.content = content;
            var c = content.transform;

            // The Liquid Glass tool ring around the upturned palm (ToolRing: a prize wheel of tools, Undo / Redo at the
            // ends of the arc); the spec inspector gets its own glass card above it, shown with a selected part.
            menu.palmUp = true;
            menu.ringLayout = true;
            menu.lift = 0.03f;
            var theme = AirTools.UI.UiTheme.Current;
            var ringGo = new GameObject("ToolRing");
            ringGo.transform.SetParent(c, false);
            var ring = ringGo.AddComponent<AirTools.Input.ToolRing>();
            ring.menu = menu;
            menu.ring = ring;
            ring.pinchHand = menu.otherHand;
            ring.pinchOvrHand = rig.GetComponentsInChildren<OVRHand>(true).FirstOrDefault(h => h.name.Contains("Hand Tracking right"));
            ring.pinchController = rig.rightHandAnchor;
            ring.head = rig.centerEyeAnchor;
            ring.glass = LiquidGlass(ringGo.transform, "Glass", Vector3.zero, theme.materials.liquidGlass);
            Vector3 At(float deg, float z = 0f) => new Vector3(Mathf.Sin(deg * Mathf.Deg2Rad), Mathf.Cos(deg * Mathf.Deg2Rad), 0f) * ring.radius + new Vector3(0f, 0f, z);
            ring.undoGlass = LiquidGlass(ringGo.transform, "UndoGlass", At(-ring.buttonAngle), theme.materials.liquidGlass);
            ring.redoGlass = LiquidGlass(ringGo.transform, "RedoGlass", At(ring.buttonAngle), theme.materials.liquidGlass);
            ring.undoIcon = IconText(ringGo.transform, "UndoIcon", AirTools.UI.Icons.Undo, 0.014f, At(-ring.buttonAngle, -0.001f), theme);
            ring.redoIcon = IconText(ringGo.transform, "RedoIcon", AirTools.UI.Icons.Redo, 0.014f, At(ring.buttonAngle, -0.001f), theme);
            ring.hint = UiBuild.Text(ringGo.transform, "Hint", "", AirTools.UI.TypeRole.Caption,
                new Vector3(0f, ring.radius + ring.lensRadius + 0.009f, -0.001f), AirTools.UI.ColorRole.TextSecondary, TMPro.TextAlignmentOptions.Center);
            ring.hint.rectTransform.sizeDelta = new Vector2(0.12f, 0.012f);
            ring.hint.enableWordWrapping = false;
            if (theme.type.halo != null) ring.hint.fontSharedMaterial = theme.type.halo;
            // catalog: seven items, 51.4° apart — Move · Measure · Level · Catalog · Notebook · Model view · Settings. With Measure on
            // the lens: Move and Level at ±51°, Settings and Catalog at ±103° (fully opaque), Notebook and Model view hidden one
            // detent from the back (±154°, behind Undo / Redo at ±150°). Before (ring v2):
            // Ring v2 (declutter DC3, M6): six fixed items, 60° apart. With Measure on the lens, Move and Level sit at ±60°,
            // Settings and Notebook at ±120°, and Model view one detent away at 180°. Ladder, Fall edges, Home and Exit world
            // moved to Settings (the Scene window, BuildScenePanel; scalemodels: "More" → "Settings" with a gear); Step in
            // is cut (the "You are here" pin and Model view's toggle both step in). Model view is the Tabletop action, in the
            // Parts seat until a Parts mode exists.
            var defs = new (string label, string icon, bool mode, AirTools.Tools.ToolKind kind, AirTools.Tools.ToolboxAction action, bool confirm)[]
            {
                ("Move", AirTools.UI.Icons.Move, true, AirTools.Tools.ToolKind.Move, default, false),
                ("Measure", AirTools.UI.Icons.Measure, true, AirTools.Tools.ToolKind.Measure, default, false),
                ("Level", AirTools.UI.Icons.Level, true, AirTools.Tools.ToolKind.Level, default, false),
                ("Catalog", AirTools.UI.Icons.Catalog, false, default, AirTools.Tools.ToolboxAction.ToggleCatalog, false),   // catalog: W1.5's Parts seat
                ("Notebook", AirTools.UI.Icons.Notebook, false, default, AirTools.Tools.ToolboxAction.ToggleNotebook, false),
                ("Model view", AirTools.UI.Icons.Tabletop, false, default, AirTools.Tools.ToolboxAction.ToggleTabletop, false),
                ("Settings", AirTools.UI.Icons.Settings, false, default, AirTools.Tools.ToolboxAction.ToggleScenePanel, false),   // scalemodels: was More
            };
            var items = new List<AirTools.Input.RingItem>();
            foreach (var d in defs)
            {
                var root = new GameObject($"Item_{d.label.Replace(' ', '_')}");
                root.transform.SetParent(ringGo.transform, false);
                var item = new AirTools.Input.RingItem
                {
                    label = d.label, icon = d.icon, isMode = d.mode, mode = d.kind, action = d.action, confirm = d.confirm, root = root.transform,
                    iconText = IconText(root.transform, "Icon", d.icon, 0.016f, new Vector3(0f, 0.004f, -0.001f), theme),
                };
                // UX W0.6: ring labels at Label size (≥ 16 dmm even off the lens) with a soft halo over a bright room.
                item.labelText = UiBuild.Text(root.transform, "Label", d.label, AirTools.UI.TypeRole.Label, new Vector3(0f, -0.0115f, -0.001f),
                    AirTools.UI.ColorRole.TextPrimary, TMPro.TextAlignmentOptions.Center, weight: AirTools.UI.Weight.Medium);
                if (theme.type.halo != null) item.labelText.fontSharedMaterial = theme.type.halo;
                item.labelText.rectTransform.sizeDelta = new Vector2(0.06f, 0.01f);
                item.labelText.enableWordWrapping = false;
                items.Add(item);
            }
            ring.items = items.ToArray();

            // Spec inspector card, centred above the ring (headset 2026-09-27: left of the ring it was out of place and harder
            // to read); its bottom clears the lens and the hint line over it.
            float ringHalf = ring.radius + ring.halfWidth + 0.012f;
            menu.toolsSize = new Vector2(ringHalf * 2f, 0.244f);
            menu.toolsCentre = new Vector3(0f, 0.026f, 0f);
            menu.inspectorWidth = 0.28f;
            menu.inspectorExtraHeight = 0f;
            const float inspectorHeight = 0.244f;
            float hintTop = ring.radius + ring.lensRadius + 0.009f + 0.006f;
            menu.panel = UiBuild.Panel(c, "InspectorPanel", new Vector2(menu.inspectorWidth, inspectorHeight),
                localPos: new Vector3(0f, hintTop + 0.012f + inspectorHeight * 0.5f, 0f));
            menu.panel.gameObject.SetActive(false);

            BuildSpecInspector(c, template, partTool, menu);
            content.SetActive(false);
            return menu;
        }

        /// A Liquid Glass surface (mesh made at runtime by ToolRing), 2 mm behind its content.
        static MeshRenderer LiquidGlass(Transform parent, string name, Vector3 localPos, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos + new Vector3(0f, 0f, 0.002f);
            go.AddComponent<MeshFilter>();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        /// A Phosphor icon glyph (em size in metres), centred.
        static TMPro.TextMeshPro IconText(Transform parent, string name, string glyph, float em, Vector3 localPos, AirTools.UI.UiTheme theme)
        {
            var t = UiBuild.Text(parent, name, glyph, AirTools.UI.TypeRole.Title, localPos, AirTools.UI.ColorRole.TextPrimary, TMPro.TextAlignmentOptions.Center);
            if (theme.icons.regular != null) t.font = theme.icons.regular;
            if (theme.icons.regularMaterial != null) t.fontSharedMaterial = theme.icons.regularMaterial;
            t.fontSize = em / 0.1f;
            t.enableWordWrapping = false;
            t.rectTransform.sizeDelta = new Vector2(em * 1.6f, em * 1.6f);
            // Glassy glyph: white at the top shading to a cool, slightly deeper tone at the bottom (with the icon
            // material's soft underlay it reads as set into the glass).
            t.enableVertexGradient = true;
            var bottom = theme.colors.iconShade;   // D3: token (was hard-coded)
            t.colorGradient = new TMPro.VertexGradient(Color.white, Color.white, bottom, bottom);
            return t;
        }

        static AirTools.Tools.ToolboxButton AddToolButton(Transform parent, string label, AirTools.Tools.ToolboxAction action, Vector2 size,
            Vector3 pos, AirTools.UI.ButtonStyle style, GameObject template, AirTools.Input.PalmMenu menu, AirTools.UI.TypeRole role = AirTools.UI.TypeRole.Label)
        {
            var b = UiBuild.Button(parent, $"Button_{label.Replace(' ', '_')}", label, size, style, template, pos, role,
                style == AirTools.UI.ButtonStyle.Chip ? AirTools.UI.RadiusRole.Pill : AirTools.UI.RadiusRole.Small);
            var tb = b.gameObject.AddComponent<AirTools.Tools.ToolboxButton>();
            tb.button = b;
            tb.action = action;
            tb.menu = menu;
            return tb;
        }

        /// The selected part's specs, on the palm menu's inspector card (above the ring).
        static void BuildSpecInspector(Transform palmContent, GameObject template, AirTools.Parts.PartTool tool, AirTools.Input.PalmMenu menu)
        {
            var go = new GameObject("SpecCard");
            go.transform.SetParent(palmContent, false);
            // The rows below are laid out for a card centred at y 0.026 with the text from its left edge + 12 mm.
            float w = menu.inspectorWidth;
            var at = menu.panel.transform.localPosition;
            go.transform.localPosition = new Vector3(at.x - w * 0.5f + 0.012f, at.y - 0.026f, 0f);
            var card = go.AddComponent<AirTools.Parts.SpecCard>();
            card.tool = tool;
            card.host = menu;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            card.content = content;
            var c = content.transform;
            float tw = w - 0.03f;
            card.title = UiBuild.Text(c, "Title", "", AirTools.UI.TypeRole.Title, new Vector3(0f, 0.128f, 0f), width: tw);
            card.title.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            card.title.rectTransform.sizeDelta = new Vector2(tw, 0.026f);
            card.tier = UiBuild.Text(c, "Tier", "", AirTools.UI.TypeRole.Caption, new Vector3(0f, 0.106f, 0f), AirTools.UI.ColorRole.TextSecondary, width: tw);
            card.dims = UiBuild.Text(c, "Dims", "", AirTools.UI.TypeRole.Label, new Vector3(0f, 0.080f, 0f), width: tw, weight: AirTools.UI.Weight.Medium);
            card.details = UiBuild.Text(c, "Details", "", AirTools.UI.TypeRole.Label, new Vector3(0f, 0.038f, 0f), AirTools.UI.ColorRole.TextSecondary, width: tw);
            card.fit = UiBuild.Text(c, "Fit", "", AirTools.UI.TypeRole.Label, new Vector3(0f, 0.003f, 0f), width: tw, weight: AirTools.UI.Weight.Medium);
            card.citations = UiBuild.Text(c, "Sources", "", AirTools.UI.TypeRole.Caption, new Vector3(0f, -0.019f, 0f), AirTools.UI.ColorRole.TextSecondary, width: tw);
            card.citations.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            card.citations.rectTransform.sizeDelta = new Vector2(tw, 0.014f);
            var swatches = new List<AirTools.Parts.PartsButton>();
            var chips = new List<AirTools.UI.GlassSurface>();
            var sw = new Vector2(0.056f, 0.026f);
            for (int i = 0; i < 2; i++)
            {
                var b = UiBuild.Button(c, $"Swatch{i}", "", sw, AirTools.UI.ButtonStyle.Chip, template,
                    new Vector3(0.028f + i * 0.062f, -0.045f, 0f), AirTools.UI.TypeRole.Caption, AirTools.UI.RadiusRole.Pill);
                // Colour dot at the chip's left, label shifted right.
                var dot = AirTools.UI.GlassSurface.Create(b.visual, "Chip", new Vector2(0.012f, 0.012f), AirTools.UI.GlassTier.ElevatedSolid,
                    AirTools.UI.RadiusRole.Pill, localPos: new Vector3(-sw.x * 0.5f + 0.011f, 0f, -0.001f));
                dot.customTint = true; dot.layer = AirTools.UI.GlassSurface.LayerIndicator; dot.Rebuild();
                b.label.transform.localPosition += new Vector3(0.006f, 0f, 0f);
                chips.Add(dot);
                var pb = b.gameObject.AddComponent<AirTools.Parts.PartsButton>();
                pb.button = b; pb.action = AirTools.Parts.PartsAction.Finish; pb.index = i;
                swatches.Add(pb);
            }
            card.swatches = swatches.ToArray();
            card.swatchChips = chips.ToArray();
            PlacementPanelBuilder.BuildAdjustChip(c, template, tw, -0.045f);   // placement: Adjust, right of the swatches
            SettingsAssetsBuilder.BuildCompareChip(c, template, card, tw);   // settings-assets: Compare (HF ⇄ LLM+CAD), right of the tier badge
            // Actions row: Array (along the last tape), Sellers (→ checkout), Remove (destructive, tap twice).
            var rowY = -0.077f; var bw = new Vector2(0.082f, 0.028f);
            var arr = UiBuild.Button(c, "Array", "Fill the run", bw, AirTools.UI.ButtonStyle.Secondary, template, new Vector3(bw.x * 0.5f, rowY, 0f), AirTools.UI.TypeRole.Caption);
            var ab = arr.gameObject.AddComponent<AirTools.Parts.PartsButton>(); ab.button = arr; ab.action = AirTools.Parts.PartsAction.Array;
            card.arrayButton = arr;
            var buy = UiBuild.Button(c, "Sellers", "Compare prices", bw, AirTools.UI.ButtonStyle.Primary, template, new Vector3(bw.x * 1.5f + 0.006f, rowY, 0f), AirTools.UI.TypeRole.Caption);
            var sb = buy.gameObject.AddComponent<AirTools.Parts.PartsButton>(); sb.button = buy; sb.action = AirTools.Parts.PartsAction.Sellers;
            var remove = UiBuild.Button(c, "Remove", "Remove", bw, AirTools.UI.ButtonStyle.Destructive, template,
                new Vector3(bw.x * 2.5f + 0.012f, rowY, 0f), AirTools.UI.TypeRole.Caption);
            remove.confirm = true;
            remove.confirmText = "Tap to remove";
            var rb = remove.gameObject.AddComponent<AirTools.Parts.PartsButton>();
            rb.button = remove; rb.action = AirTools.Parts.PartsAction.Remove;
            content.SetActive(false);
        }

        /// catalog: superseded by CatalogBuilder.Build (the Catalog is the same PartsMenu in the left side slot); kept unused
        /// for the merge with the lanes that were in flight.
        /// Crate menu ("Find parts"): a world-locked glass window left of the entry point at hand distance —
        /// title, a segmented control of preset searches, status, up to three candidate rows (ElevatedSolid),
        /// and a grab handle to move it.
        static void BuildPartsMenu(OVRCameraRig rig, GameObject template, Shader unlit,
            AirTools.Parts.PartsClient client, AirTools.Parts.PartLoader loader, AirTools.Parts.PartTool tool)
        {
            var old = rig.transform.Find("PartsMenu");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            UiBuild.Distance = 0.5f;   // read from its side slot, 0.5 m away
            UiBuild.D5Rays = true;
            var go = new GameObject("PartsMenu");
            go.transform.SetParent(rig.transform, false);
            // Built pose for an untracked head; shown later in the left side slot from the person (UX W0.7).
            go.transform.localPosition = new Vector3(-0.24f, 1.40f, 0.37f);
            go.transform.localRotation = Quaternion.Euler(15f, -32f, 0f);
            var browser = go.AddComponent<AirTools.Parts.PartsBrowser>();
            browser.client = client; browser.loader = loader; browser.tool = tool;
            browser.head = rig.centerEyeAnchor;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            browser.content = content;
            var c = content.transform;
            const float W = 0.30f, H = 0.371f;
            browser.panel = UiBuild.Panel(c, "Panel", new Vector2(W, H));
            browser.panelTop = H * 0.5f;
            browser.headerHeight = 0.148f;
            float x0 = -W * 0.5f + 0.016f;
            UiBuild.Text(c, "Title", "Find parts", AirTools.UI.TypeRole.Heading, new Vector3(x0, H * 0.5f - 0.026f, 0f), width: 0.15f);
            // Anything else: hold and say it ("find me a …"); the agent's search_started lands in this window.
            var talkSize = new Vector2(0.105f, 0.028f);
            browser.talk = UiBuild.Button(c, "Talk", AirTools.Agent.TalkButton.IdleText, talkSize, AirTools.UI.ButtonStyle.Primary, template,   // voice: tap or hold
                new Vector3(W * 0.5f - 0.012f - talkSize.x * 0.5f, H * 0.5f - 0.026f, 0f), AirTools.UI.TypeRole.Caption);
            // Preset searches, kitchen then outdoor (each one checked against the real server for results).
            var segs = new (string label, string query)[]
            {
                ("Cabinet hinge", "cabinet hinge"), ("Drawer slide", "drawer slide"), ("Shelf bracket", "shelf bracket"),
                ("Gutter hanger", "gutter hanger"), ("Window AC", "window ac"), ("Cabinet knob", "cabinet knob"),
            };
            var segButtons = new List<AirTools.UI.GlassButton>();
            const int perRow = 3;
            float segW = (W - 0.032f - 0.004f * (perRow - 1)) / perRow;
            for (int i = 0; i < segs.Length; i++)
            {
                int row = i / perRow, col = i % perRow;
                var b = UiBuild.Button(c, $"Search_{segs[i].label.Replace(' ', '_')}", segs[i].label, new Vector2(segW, 0.032f), AirTools.UI.ButtonStyle.Toggle,
                    template, new Vector3(x0 + segW * 0.5f + col * (segW + 0.004f), H * 0.5f - 0.064f - row * 0.036f, 0f), AirTools.UI.TypeRole.Label);
                var pb = b.gameObject.AddComponent<AirTools.Parts.PartsButton>();
                pb.button = b; pb.action = AirTools.Parts.PartsAction.Search; pb.text = segs[i].query;
                segButtons.Add(b);
            }
            browser.searchButtons = segButtons.ToArray();
            browser.statusText = UiBuild.Text(c, "Status", "", AirTools.UI.TypeRole.Caption, new Vector3(x0, H * 0.5f - 0.130f, 0f),
                AirTools.UI.ColorRole.TextSecondary, width: W - 0.03f);
            var photoMat = LoadOrCreateMaterial("PartPhoto", unlit, Color.white);
            var cards = new List<AirTools.Parts.CandidateCardView>();
            const float rowH = 0.064f;
            for (int i = 0; i < 3; i++)
            {
                var cardGo = new GameObject($"Card{i}");
                cardGo.transform.SetParent(c, false);
                cardGo.transform.localPosition = new Vector3(0f, H * 0.5f - 0.181f - i * (rowH + 0.006f), 0f);
                var card = cardGo.AddComponent<AirTools.Parts.CandidateCardView>();
                card.backplate = AirTools.UI.GlassSurface.Create(cardGo.transform, "Row", new Vector2(W - 0.024f, rowH), AirTools.UI.GlassTier.ElevatedSolid,
                    AirTools.UI.RadiusRole.Medium, localPos: new Vector3(0f, 0f, 0.001f));
                var photo = GameObject.CreatePrimitive(PrimitiveType.Quad);
                photo.name = "Photo";
                Object.DestroyImmediate(photo.GetComponent<Collider>());
                photo.transform.SetParent(cardGo.transform, false);
                photo.transform.localPosition = new Vector3(-W * 0.5f + 0.012f + 0.03f, 0f, -0.001f);
                photo.transform.localScale = new Vector3(0.048f, 0.048f, 1f);
                photo.GetComponent<MeshRenderer>().sharedMaterial = photoMat;
                card.photo = photo.GetComponent<MeshRenderer>();
                float tx = -W * 0.5f + 0.074f, tw = 0.135f;
                card.nameText = UiBuild.Text(cardGo.transform, "Name", "", AirTools.UI.TypeRole.Caption, new Vector3(tx, 0.016f, 0f), width: tw, weight: AirTools.UI.Weight.Medium);
                card.nameText.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                card.nameText.rectTransform.sizeDelta = new Vector2(tw, 0.018f);
                card.dimsText = UiBuild.Text(cardGo.transform, "Dims", "", AirTools.UI.TypeRole.Caption, new Vector3(tx, -0.004f, 0f), AirTools.UI.ColorRole.TextSecondary, width: tw);
                card.priceText = UiBuild.Text(cardGo.transform, "Price", "", AirTools.UI.TypeRole.Caption, new Vector3(tx, -0.02f, 0f), width: tw, weight: AirTools.UI.Weight.Medium);
                var take = UiBuild.Button(cardGo.transform, "Take", "Take", new Vector2(0.058f, 0.032f), AirTools.UI.ButtonStyle.Primary, template,
                    new Vector3(W * 0.5f - 0.012f - 0.035f, 0f, 0f));
                var pb = take.gameObject.AddComponent<AirTools.Parts.PartsButton>();
                pb.button = take; pb.action = AirTools.Parts.PartsAction.Take; pb.index = i;
                cardGo.SetActive(false);
                cards.Add(card);
            }
            browser.cards = cards.ToArray();
            browser.handle = UiBuild.Handle(go.transform, rig.centerEyeAnchor, new Vector3(0f, -H * 0.5f - 0.016f, 0f), template);
            browser.handle.transform.SetParent(c, true);
            content.SetActive(false);
            UiBuild.D5Rays = false;
        }

        /// Notebook window: opens 50 cm ahead in the lower field of view and stays put (re-centres if you turn
        /// away); grab handle to move it. Rows are ElevatedSolid with the evidence photo.
        static void BuildNotebookPanel(OVRCameraRig rig, GameObject template, Shader unlit)
        {
            var old = rig.transform.Find("NotebookPanel");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            UiBuild.Distance = 0.45f;
            UiBuild.D5Rays = true;
            var go = new GameObject("NotebookPanel");
            go.transform.SetParent(rig.transform, false);
            var panel = go.AddComponent<AirTools.Notes.NotebookPanel>();
            panel.anchor = null;
            panel.head = rig.centerEyeAnchor;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            panel.content = content;
            var c = content.transform;
            const float W = 0.34f, H = 0.37f;
            UiBuild.Panel(c, "Panel", new Vector2(W, H));
            float x0 = -W * 0.5f + 0.016f;
            panel.title = UiBuild.Text(c, "Title", "Notebook", AirTools.UI.TypeRole.Heading, new Vector3(x0, H * 0.5f - 0.026f, 0f), width: W - 0.03f);
            panel.status = UiBuild.Text(c, "Status", "", AirTools.UI.TypeRole.Caption, new Vector3(x0, H * 0.5f - 0.05f, 0f), AirTools.UI.ColorRole.TextSecondary, width: W - 0.03f);
            var thumbMat = LoadOrCreateMaterial("NotebookThumb", unlit, Color.white);
            var rows = new List<AirTools.Notes.NotebookRowView>();
            const float rowH = 0.054f;
            for (int i = 0; i < 4; i++)
            {
                var rowGo = new GameObject($"Row{i}");
                rowGo.transform.SetParent(c, false);
                rowGo.transform.localPosition = new Vector3(0f, H * 0.5f - 0.092f - i * (rowH + 0.006f), 0f);
                var row = rowGo.AddComponent<AirTools.Notes.NotebookRowView>();
                AirTools.UI.GlassSurface.Create(rowGo.transform, "Row", new Vector2(W - 0.024f, rowH), AirTools.UI.GlassTier.ElevatedSolid,
                    AirTools.UI.RadiusRole.Medium, localPos: new Vector3(0f, 0f, 0.001f));
                var thumb = GameObject.CreatePrimitive(PrimitiveType.Quad);
                thumb.name = "Thumb";
                Object.DestroyImmediate(thumb.GetComponent<Collider>());
                thumb.transform.SetParent(rowGo.transform, false);
                thumb.transform.localPosition = new Vector3(-W * 0.5f + 0.012f + 0.032f, 0f, -0.001f);
                thumb.transform.localScale = new Vector3(0.056f, 0.042f, 1f);
                thumb.GetComponent<MeshRenderer>().sharedMaterial = thumbMat;
                row.thumbnail = thumb.GetComponent<MeshRenderer>();
                float tx = -W * 0.5f + 0.076f, tw = 0.17f;
                row.titleText = UiBuild.Text(rowGo.transform, "Title", "", AirTools.UI.TypeRole.Caption, new Vector3(tx, 0.009f, 0f), width: tw, weight: AirTools.UI.Weight.Medium);
                row.titleText.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                row.titleText.rectTransform.sizeDelta = new Vector2(tw, 0.018f);
                row.detailText = UiBuild.Text(rowGo.transform, "Detail", "", AirTools.UI.TypeRole.Caption, new Vector3(tx, -0.011f, 0f), AirTools.UI.ColorRole.TextSecondary, width: tw);
                var show = UiBuild.Button(rowGo.transform, "Show", "Show", new Vector2(0.056f, 0.03f), AirTools.UI.ButtonStyle.Secondary, template,
                    new Vector3(W * 0.5f - 0.012f - 0.034f, 0f, 0f), AirTools.UI.TypeRole.Caption);
                var nb = show.gameObject.AddComponent<AirTools.Notes.NotebookButton>();
                nb.panel = panel; nb.action = AirTools.Notes.NotebookAction.Show; nb.row = i; nb.button = show;
                row.showButton = show.gameObject;
                rows.Add(row);
            }
            panel.rows = rows.ToArray();
            var controls = new (string label, AirTools.Notes.NotebookAction action, AirTools.UI.ButtonStyle style, float x)[]
            {
                ("Previous", AirTools.Notes.NotebookAction.Prev, AirTools.UI.ButtonStyle.Secondary, -0.117f),
                ("Next", AirTools.Notes.NotebookAction.Next, AirTools.UI.ButtonStyle.Secondary, -0.041f),
                ("Send report", AirTools.Notes.NotebookAction.Export, AirTools.UI.ButtonStyle.Primary, 0.041f),
                ("Close", AirTools.Notes.NotebookAction.Close, AirTools.UI.ButtonStyle.Borderless, 0.117f),
            };
            foreach (var (label, action, st, x) in controls)
            {
                var b = UiBuild.Button(c, $"Button_{label}", label, new Vector2(0.07f, 0.032f), st, template, new Vector3(x, -H * 0.5f + 0.03f, 0f));
                var nb = b.gameObject.AddComponent<AirTools.Notes.NotebookButton>();
                nb.panel = panel; nb.action = action; nb.button = b;
            }
            // sitescope: All sites (the notebook lists the loaded site's readings by default), top right of the title row;
            // the title and status make room for it.
            const float allW = 0.075f;
            foreach (var t in new[] { panel.title, panel.status })
            {
                t.rectTransform.sizeDelta = new Vector2(W - 0.03f - allW - 0.01f, t.rectTransform.sizeDelta.y);
                t.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            }
            var allSites = UiBuild.Button(c, "Button_AllSites", "All sites", new Vector2(allW, 0.026f), AirTools.UI.ButtonStyle.Toggle, template,
                new Vector3(W * 0.5f - 0.012f - allW * 0.5f, H * 0.5f - 0.03f, 0f), AirTools.UI.TypeRole.Caption);
            var nbAll = allSites.gameObject.AddComponent<AirTools.Notes.NotebookButton>();
            nbAll.panel = panel; nbAll.action = AirTools.Notes.NotebookAction.AllSites; nbAll.button = allSites;
            panel.allSitesToggle = allSites;
            // end sitescope
            panel.handle = UiBuild.Handle(go.transform, rig.centerEyeAnchor, new Vector3(0f, -H * 0.5f - 0.016f, 0f), template);
            panel.handle.transform.SetParent(c, true);
            content.SetActive(false);
            UiBuild.D5Rays = false;
        }

        /// Toast (status) and reply card (agent / voice / ask answers): heads-up glass pills below the gaze, drawn above
        /// every panel (UX W0.8: overlay glass + overlay text). Declutter M1 (DC2): the reply card takes the status line's
        /// place (0.9 m, 17° below the gaze, text 0.45 m wide: the same 28° as the old 0.30 m at 0.6 m; the line yields
        /// while it's up); the toast (0.6 m, 12°, 26 cm) is the fallback while the guide rail is off (on, it's a flash on
        /// the line).
        static void BuildToast(OVRCameraRig rig)
        {
            foreach (var (name, reply) in new[] { ("Toast", false), ("ReplyCard", true) })
            {
                var old = rig.transform.Find(name);
                if (old != null) Object.DestroyImmediate(old.gameObject);
                UiBuild.Distance = reply ? 0.9f : 0.6f;   // UX W0.6: sized for where it's read
                var go = new GameObject(name);
                go.transform.SetParent(rig.transform, false);
                var toast = go.AddComponent<AirTools.UI.UiToast>();
                toast.head = rig.centerEyeAnchor;
                toast.isReply = reply;
                toast.distance = reply ? 0.9f : 0.6f;
                toast.belowGazeDeg = reply ? 17f : 12f;
                toast.maxLines = reply ? 4 : 2;
                toast.maxWidth = reply ? 0.45f : 0.26f;
                toast.padding = reply ? 0.03f : 0.022f;   // the status line's padding at 0.9 m
                toast.seconds = reply ? 5f : 2.4f;
                toast.surface = UiBuild.Panel(go.transform, "Pill", new Vector2(0.2f, 0.036f), elevation: AirTools.UI.Elevation.None, radius: reply ? AirTools.UI.RadiusRole.Large : AirTools.UI.RadiusRole.Pill);
                toast.surface.tier = AirTools.UI.GlassTier.ElevatedSolid;   // near-opaque behind text
                toast.surface.hud = true; toast.surface.Rebuild();
                toast.text = UiBuild.Text(go.transform, "Text", "", reply ? AirTools.UI.TypeRole.Body : AirTools.UI.TypeRole.Label, new Vector3(0f, 0f, -0.002f),
                    align: TMPro.TextAlignmentOptions.Center, width: toast.maxWidth);
                toast.text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                var theme = AirTools.UI.UiTheme.Current;
                if (theme.type.hud != null) { toast.text.font = theme.FontAsset(AirTools.UI.Weight.Medium); toast.text.fontSharedMaterial = theme.type.hud; }
                float dotSize = reply ? 0.011f : 0.008f;   // the status line's dot at 0.9 m
                toast.dot = AirTools.UI.GlassSurface.Create(go.transform, "Dot", new Vector2(dotSize, dotSize), AirTools.UI.GlassTier.ElevatedSolid, AirTools.UI.RadiusRole.Pill);
                toast.dot.customTint = true; toast.dot.rim = 0f; toast.dot.hud = true; toast.dot.layer = AirTools.UI.GlassSurface.LayerIndicator; toast.dot.Rebuild();
            }
            UiBuild.Distance = AirTools.UI.UiText.HandDistance;
        }

        /// A sound from the Interaction SDK's sample audio (Meta Sound Collection terms: usable in Quest content).
        static AudioClip IsdkSound(string name)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>($"Packages/com.meta.xr.sdk.interaction/Runtime/Sample/Audio/Content/{name}.wav");
            if (clip == null) Debug.LogWarning($"[AirTools] ISDK sound {name} not found; the synthesised fallback plays instead");
            return clip;
        }

        static bool HasAncestor(Transform t, string name)
        {
            for (var p = t; p != null; p = p.parent) if (p.name == name) return true;
            return false;
        }

        static Material LoadOrCreateTransparent(string name, Shader shader, Color color)
        {
            var mat = LoadOrCreateMaterial(name, shader, color);
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_SrcBlendAlpha", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlendAlpha", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static GameObject Cube(string name, Transform parent, Vector3 pos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static Material LoadOrCreateMaterial(string name, Shader shader, Color color, float smoothness = 0.5f, float metallic = 0f)
        {
            System.IO.Directory.CreateDirectory(MaterialsFolder);
            string path = $"{MaterialsFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = shader;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
