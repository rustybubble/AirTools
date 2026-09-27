using AirTools.Scene;
using AirTools.Tools;
using AirTools.UI;
using UnityEditor;
using UnityEngine;

namespace AirTools.Editor
{
    /// P6/P7 scene wiring (presence.md S4): the ladder tool, the fall-edge overlay, their glass material and the ladder
    /// card (UiBuild, docs/UI.md). Called once from
    /// MainSceneBuilder.WireTools (AirTools ▸ Wire Main Scene); idempotent (Wire rebuilds the AirTools root).
    public static class LadderBuilder
    {
        const string MaterialsFolder = "Assets/AirTools/Materials";

        public static void Build(GameObject app, OVRCameraRig rig, SceneRoot sceneRoot, GameObject template, MeasureStyle style)
        {
            var glass = Transparent("LadderGlass", Color.white);
            var tool = app.AddComponent<LadderTool>();
            tool.frame = sceneRoot.transform;
            tool.style = style;
            tool.material = glass;
            var manager = app.GetComponent<ToolManager>();
            if (manager != null) manager.ladder = tool;

            // P7: the fall-edge overlay (red / white dashed bands + "fall protection" labels) under SceneRoot.
            var edges = app.AddComponent<AirTools.Structure.FallEdges>();
            edges.sceneRoot = sceneRoot;
            edges.material = glass;
            edges.style = style;

            BuildCard(rig, template);
        }

        /// The ladder card in the main window slot (0.45 m, 20° below the eye line): size, verdict with a tone dot,
        /// foot / angle / ratio, the OSHA disclaimer, Find this ladder, Fall edges, Close; movable by its grab bar.
        static void BuildCard(OVRCameraRig rig, GameObject template)
        {
            const string name = "LadderCard";
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
            var card = go.AddComponent<LadderCard>();
            card.window = w;
            var c = content.transform;

            const float W = 0.32f, H = 0.23f;
            UiBuild.Panel(c, "Panel", new Vector2(W, H));
            float x0 = -W * 0.5f + 0.016f, top = H * 0.5f;
            card.title = UiBuild.Text(c, "Title", "Extension ladder", TypeRole.Heading, new Vector3(x0, top - 0.026f, 0f), width: W - 0.1f);
            var close = UiBuild.Button(c, "Close", "Close", new Vector2(0.06f, 0.026f), ButtonStyle.Borderless, template,
                new Vector3(W * 0.5f - 0.046f, top - 0.026f, 0f), TypeRole.Caption);
            Hook(close, card, LadderCardAction.Close);

            // Verdict: tone dot + words (never colour alone).
            card.dot = GlassSurface.Create(c, "Dot", new Vector2(0.009f, 0.009f), GlassTier.ElevatedSolid, RadiusRole.Pill,
                localPos: new Vector3(x0 + 0.0045f, top - 0.058f, -0.001f));
            card.dot.customTint = true;
            card.dot.tint = UiTheme.Current.colors.success;
            card.dot.rim = 0f;
            card.dot.layer = GlassSurface.LayerIndicator;
            card.dot.Rebuild();
            card.verdict = UiBuild.Text(c, "Verdict", "", TypeRole.Label, new Vector3(x0 + 0.016f, top - 0.058f, 0f), width: W - 0.048f, weight: Weight.Medium);
            card.detail = UiBuild.Text(c, "Detail", "", TypeRole.Body, new Vector3(x0, top - 0.098f, 0f), width: W - 0.03f);
            card.disclaimer = UiBuild.Text(c, "Disclaimer", LadderMath.Disclaimer, TypeRole.Caption, new Vector3(x0, top - 0.145f, 0f),
                ColorRole.TextSecondary, width: W - 0.03f);

            float by = -H * 0.5f + 0.026f;
            card.findButton = UiBuild.Button(c, "FindLadder", "Find this ladder", new Vector2(0.15f, 0.03f), ButtonStyle.Primary, template,
                new Vector3(x0 + 0.075f, by, 0f), TypeRole.Caption);
            Hook(card.findButton, card, LadderCardAction.FindLadder);
            card.edgesButton = UiBuild.Button(c, "FallEdges", "Fall edges", new Vector2(0.11f, 0.03f), ButtonStyle.Toggle, template,
                new Vector3(x0 + 0.15f + 0.008f + 0.055f, by, 0f), TypeRole.Caption);
            Hook(card.edgesButton, card, LadderCardAction.ToggleFallEdges);

            w.handle = UiBuild.Handle(w.transform, rig.centerEyeAnchor, new Vector3(0f, -H * 0.5f - 0.016f, 0f), template);
            w.handle.transform.SetParent(c, true);
            w.content.SetActive(false);
            UiBuild.D5Rays = false;
            UiBuild.Distance = UiText.HandDistance;
        }

        static void Hook(GlassButton b, LadderCard card, LadderCardAction action)
        {
            var cb = b.gameObject.AddComponent<LadderCardButton>();
            cb.button = b; cb.card = card; cb.action = action;
        }

        /// URP Unlit, alpha-blended, both faces, no depth write: glass rails / bands tinted per renderer.
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
