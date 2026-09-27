using AirTools.Parts;
using AirTools.UI;
using UnityEditor;
using UnityEngine;

namespace AirTools.Editor
{
    /// edit6dof: the Edit view (Parts/EditView, docs/edit-view.md), built only with UiBuild; colours from UiTheme.Current;
    /// behaviour on GlassButton.Clicked (EditViewButton, EditArrowButton). Called from MainSceneBuilder (AirTools ▸ Wire
    /// Main Scene). edit-touch: every control is a poke button (UiBuild.Button over the poke template, D5Rays off: a
    /// fingertip or a controller's poke tip, no pinch, no ray), and it's all within reach, text built for 0.45 m
    /// (EditView.ReadDistance):
    /// - the view on `app`, with a world-locked **stage** (placed in front of you when it opens: EditViewMath.Layout)
    ///   holding the twelve **arrows** (3.2 cm glass poke knobs, Phosphor glyphs; 0.42 m from the eye, facing it) with
    ///   their readout pill, and the **panel** (0.225 × 0.228 m, tucked into the arrows' lower right, 0.44 m from the
    ///   eye, facing it): title and true size, the readout, Original + four swatches, four swatches + Shade, Step ·
    ///   Reset turn · Fit to opening, then Cancel · Move · Save (a new part: Cancel · Place);
    /// - the **move bar**, a main-slot card (UiZones.Main): the fit or "Aim and pinch to place", Cancel and Save;
    /// - the **dim**: an inverted 3 m sphere on the centre eye with AirTools/DimShell (Materials/EditDim.mat);
    /// - a placed part's **context menu**: Edit / View similar / Delete, and "Deleted · Undo" (placed within reach when
    ///   it opens: PartContextMenu).
    public static class EditViewBuilder
    {
        public const string Name = "EditView";
        public const float PanelW = 0.225f, PanelH = 0.228f;
        public const float MenuW = 0.15f, MenuH = 0.168f;
        /// A knob's press cooldown (s): a quick second tap still steps (the button default is 0.3 s).
        public const float KnobCooldown = 0.1f;
        public const float BarW = 0.26f, BarH = 0.1f;
        public const string DimMaterialPath = "Assets/AirTools/Materials/EditDim.mat";

        public static EditView Build(GameObject app, OVRCameraRig rig, GameObject template, PartTool tool, PlacementEditor editor)
        {
            foreach (var n in new[] { Name, "PartContextMenu" })
            {
                var old = app.transform.Find(n);
                if (old != null) Object.DestroyImmediate(old.gameObject);
            }
            var oldBar = rig.transform.Find("EditMoveBar");
            if (oldBar != null) Object.DestroyImmediate(oldBar.gameObject);
            var oldDim = rig.centerEyeAnchor.Find("EditDim");
            if (oldDim != null) Object.DestroyImmediate(oldDim.gameObject);

            var root = new GameObject(Name);
            root.transform.SetParent(app.transform, false);
            var view = root.AddComponent<EditView>();
            view.editor = editor;
            view.tool = tool;
            view.head = rig.centerEyeAnchor;

            // The copy of a placed part rides here in world space: keep this holder at the world's identity.
            var copy = new GameObject("EditCopy").transform;
            copy.SetParent(root.transform, false);
            view.proxyRoot = copy;

            var stage = new GameObject("Stage").transform;
            stage.SetParent(root.transform, false);
            view.stage = stage;
            view.arrows = BuildArrows(stage, template);
            view.panel = BuildPanel(stage, template);
            BuildMoveBar(rig, template, view.panel);
            stage.gameObject.SetActive(false);

            view.dim = BuildDim(rig.centerEyeAnchor);
            view.menu = BuildMenu(app, rig, template);
            return view;
        }

        // ---------------- arrows ----------------

        static EditArrows BuildArrows(Transform stage, GameObject template)
        {
            var go = new GameObject("Arrows");
            go.transform.SetParent(stage, false);
            var arrows = go.AddComponent<EditArrows>();
            var theme = UiTheme.Current;
            int n = EditViewMath.Arrows.Length;
            arrows.knobs = new GlassButton[n];
            arrows.glyphs = new TMPro.TextMeshPro[n];
            float k = arrows.knobSize;
            UiBuild.Distance = EditView.ReadDistance;
            UiBuild.D5Rays = false;   // edit-touch: poke only
            for (int i = 0; i < n; i++)
            {
                var a = EditViewMath.Arrows[i];
                string glyph = Glyph(a, out float spin);
                // edit-touch: a round poke button; the view lays it out (and curves it round the eye) when it opens.
                var b = UiBuild.Button(go.transform, a.Name, glyph != null ? "" : a.Sign > 0 ? "Out" : "In", new Vector2(k, k), ButtonStyle.Secondary, template,
                    EditViewMath.ArrowPosition(a, 0.15f, 0.19f, 0.1f), TypeRole.Label, RadiusRole.Pill);
                b.cooldownSeconds = KnobCooldown;
                TMPro.TextMeshPro t = b.label;
                if (glyph != null)
                {
                    t = UiBuild.Text(b.visual, "Glyph", glyph, TypeRole.Title, new Vector3(0f, 0f, -0.0015f), ColorRole.TextPrimary, TMPro.TextAlignmentOptions.Center);
                    if (theme.icons.regular != null) t.font = theme.icons.regular;
                    if (theme.icons.regularMaterial != null) t.fontSharedMaterial = theme.icons.regularMaterial;
                    t.fontSize = k * 0.55f / 0.1f;
                    t.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
                    t.rectTransform.sizeDelta = new Vector2(k, k);
                    t.transform.localRotation = Quaternion.Euler(0f, 0f, spin);
                }
                var eb = b.gameObject.AddComponent<EditArrowButton>();
                eb.button = b;
                eb.index = i;
                arrows.knobs[i] = b;
                arrows.glyphs[i] = t;
            }
            // The readout pill ("Tilt 5°"), beside the top knob.
            var plate = UiBuild.Panel(go.transform, "Readout", arrows.readoutSize, GlassTier.GlassRegular, Elevation.None, radius: RadiusRole.Pill);
            arrows.readoutPlate = plate;
            arrows.readoutText = UiBuild.Text(plate.transform, "Text", "Tilt 0°", TypeRole.Label, new Vector3(0f, 0f, -0.004f), ColorRole.TextPrimary,
                TMPro.TextAlignmentOptions.Center, weight: Weight.Medium);
            arrows.readoutText.rectTransform.sizeDelta = new Vector2(arrows.readoutSize.x - 0.01f, arrows.readoutSize.y - 0.004f);
            plate.gameObject.SetActive(false);
            UiBuild.Distance = UiText.HandDistance;
            return arrows;
        }

        /// A knob's Phosphor glyph and its turn in the knob's plane (degrees); null: a word (Out / In).
        static string Glyph(EditArrow a, out float spin)
        {
            spin = 0f;
            switch (a.Axis)
            {
                case EditAxis.Turn: return a.Sign > 0 ? Icons.ArcLeft : Icons.ArcRight;       // the front turns to your left / right
                case EditAxis.Tilt: spin = a.Sign > 0 ? 90f : -90f; return Icons.ArcRight;    // the top away / towards you
                case EditAxis.Roll: return a.Sign > 0 ? Icons.RotateRight : Icons.RotateLeft; // clockwise / anticlockwise
                case EditAxis.Right: return a.Sign > 0 ? Icons.ArrowRight : Icons.ArrowLeft;
                case EditAxis.Up: return a.Sign > 0 ? Icons.ArrowUp : Icons.ArrowDown;
                default: return null;
            }
        }

        // ---------------- the panel ----------------

        static EditViewPanel BuildPanel(Transform stage, GameObject template)
        {
            var go = new GameObject("Panel");
            go.transform.SetParent(stage, false);
            // edit-touch: posed when the view opens (tucked into the arrows' lower right, 0.44 m from the eye, facing it).
            var panel = stage.gameObject.AddComponent<EditViewPanel>();
            panel.root = go;
            panel.panelSize = new Vector2(PanelW, PanelH);
            var c = go.transform;
            UiBuild.Distance = EditView.ReadDistance;
            UiBuild.D5Rays = false;   // edit-touch: poke only
            float top = PanelH * 0.5f, x0 = -PanelW * 0.5f + 0.014f, inner = PanelW - 0.028f;
            UiBuild.Panel(c, "Panel", new Vector2(PanelW, PanelH));

            panel.title = UiBuild.Text(c, "Title", "Edit", TypeRole.Heading, new Vector3(x0, top - 0.021f, 0f), width: inner);
            panel.size = UiBuild.Text(c, "Size", "", TypeRole.Caption, new Vector3(x0, top - 0.040f, 0f), ColorRole.TextSecondary, width: inner);
            panel.readout = UiBuild.Text(c, "Readout", "Right 0″ · Up 0″ · Out 0″\nTurn 0° · Tilt 0° · Roll 0°", TypeRole.Label,
                new Vector3(x0, top - 0.064f, 0f), width: inner, weight: Weight.Medium);

            // Swatches (3 × 3 cm dots, the selected one inked): Original + four, then four + Shade (light / full).
            var swatches = new GlassButton[EditShades.Count];
            var chip = new Vector2(0.056f, 0.03f);
            var dot = new Vector2(0.03f, 0.03f);
            const float gap = 0.0045f;
            float row = top - 0.096f;
            swatches[0] = Btn(c, template, "Swatch0", "Original", chip, ButtonStyle.Chip, new Vector3(x0 + chip.x * 0.5f, row, 0f), EditViewAction.Swatch, 0, radius: RadiusRole.Pill);
            for (int i = 1; i < swatches.Length; i++)
            {
                bool first = i <= 4;
                float x = first ? x0 + chip.x + gap + dot.x * 0.5f + (i - 1) * (dot.x + gap) : x0 + dot.x * 0.5f + (i - 5) * (dot.x + gap);
                var b = Btn(c, template, $"Swatch{i}", "", dot, ButtonStyle.Chip, new Vector3(x, first ? row : row - 0.034f, 0f), EditViewAction.Swatch, i, radius: RadiusRole.Pill);
                b.tooltip = EditShades.Names[i];
                var mark = GlassSurface.Create(b.visual, "Colour", new Vector2(0.018f, 0.018f), GlassTier.ElevatedSolid, RadiusRole.Pill, localPos: new Vector3(0f, 0.001f, -0.001f));
                mark.customTint = true;
                mark.tint = EditShades.ColorOf(i);
                mark.layer = GlassSurface.LayerIndicator;
                mark.role = GlassRole.Mark;
                mark.Rebuild();
                swatches[i] = b;
            }
            panel.swatches = swatches;
            panel.shade = Btn(c, template, "Shade", "Shade: full", chip, ButtonStyle.Chip, new Vector3(x0 + inner - chip.x * 0.5f, row - 0.034f, 0f), EditViewAction.Shade,
                radius: RadiusRole.Pill);

            // Step · Reset turn · Fit to opening.
            row = top - 0.164f;
            float w3 = (inner - 2f * gap) / 3f;
            float xs = x0;
            GlassButton Next(string name, string text, ButtonStyle style, EditViewAction action)
            {
                var b = Btn(c, template, name, text, new Vector2(w3, 0.03f), style, new Vector3(xs + w3 * 0.5f, row, 0f), action,
                    radius: style == ButtonStyle.Chip ? RadiusRole.Pill : RadiusRole.Small);
                xs += w3 + gap;
                return b;
            }
            panel.step = Next("Step", PlacementMath.StepLabel(UiSettings.DefaultUnits, false), ButtonStyle.Chip, EditViewAction.Step);
            panel.reset = Next("Reset", "Reset turn", ButtonStyle.Secondary, EditViewAction.ResetTurn);
            panel.fitToOpening = Next("FitToOpening", "Fit to opening", ButtonStyle.Secondary, EditViewAction.FitToOpening);

            // Cancel · Move · Save (a new part: Cancel · Place). One primary.
            row = top - 0.199f;
            var small = new Vector2(0.06f, 0.032f);
            var primary = new Vector2(inner - 2f * small.x - 2f * gap, 0.032f);
            panel.cancel = Btn(c, template, "Cancel", "Cancel", small, ButtonStyle.Secondary, new Vector3(x0 + small.x * 0.5f, row, 0f), EditViewAction.Cancel);
            panel.move = Btn(c, template, "Move", "Move", small, ButtonStyle.Secondary, new Vector3(x0 + small.x * 1.5f + gap, row, 0f), EditViewAction.Move);
            panel.move.tooltip = "Move it round the room, then Save";
            float px = x0 + inner - primary.x * 0.5f;
            panel.save = Btn(c, template, "Save", "Save", primary, ButtonStyle.Primary, new Vector3(px, row, 0f), EditViewAction.Save,
                role: TypeRole.Label, radius: RadiusRole.Pill);
            panel.place = Btn(c, template, "Place", "Place", primary, ButtonStyle.Primary, new Vector3(px, row, 0f), EditViewAction.Place,
                role: TypeRole.Label, radius: RadiusRole.Pill);
            panel.place.tooltip = "Put it on the pointer; pinch where it goes";
            panel.place.gameObject.SetActive(false);
            UiBuild.Distance = UiText.HandDistance;
            go.SetActive(false);
            return panel;
        }

        // ---------------- the move bar ----------------

        static void BuildMoveBar(OVRCameraRig rig, GameObject template, EditViewPanel panel)
        {
            var go = new GameObject("EditMoveBar");
            go.transform.SetParent(rig.transform, false);
            var w = go.AddComponent<FloatingWindow>();
            w.head = rig.centerEyeAnchor;
            w.distance = UiZones.Main.Distance; w.downDeg = UiZones.Main.DownDeg; w.yawDeg = UiZones.Main.YawDeg; w.mainSlot = true;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            w.content = content;
            var c = content.transform;
            UiBuild.Distance = UiZones.Main.Distance;
            UiBuild.D5Rays = false;   // edit-touch: poke only (it's in the main slot, within reach)
            float top = BarH * 0.5f, x0 = -BarW * 0.5f + 0.016f, right = BarW * 0.5f - 0.016f;
            UiBuild.Panel(c, "Panel", new Vector2(BarW, BarH));
            panel.moveText = UiBuild.Text(c, "Line", "Pinch and drag it to a spot · then Save", TypeRole.Label, new Vector3(x0, top - 0.028f, 0f),
                width: BarW - 0.032f, weight: Weight.Medium);
            float row = -BarH * 0.5f + 0.028f;
            panel.moveCancel = Btn(c, template, "Cancel", "Cancel", new Vector2(0.09f, 0.032f), ButtonStyle.Secondary, new Vector3(x0 + 0.045f, row, 0f), EditViewAction.Cancel);
            panel.moveSave = Btn(c, template, "Save", "Save", new Vector2(0.12f, 0.032f), ButtonStyle.Primary, new Vector3(right - 0.06f, row, 0f), EditViewAction.Save,
                role: TypeRole.Label, radius: RadiusRole.Pill);
            panel.moveWindow = w;
            content.SetActive(false);
            UiBuild.Distance = UiText.HandDistance;
        }

        // ---------------- the dim ----------------

        static EditDim BuildDim(Transform centerEye)
        {
            var go = new GameObject("EditDim");
            go.transform.SetParent(centerEye, false);
            go.transform.localScale = Vector3.one * 6f;   // radius 3 m: behind the item at the centre (1 m)
            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = DimMaterial();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = false;
            return go.AddComponent<EditDim>();
        }

        public static Material DimMaterial()
        {
            var shader = Shader.Find("AirTools/DimShell");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(DimMaterialPath);
            if (mat == null)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DimMaterialPath));
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, DimMaterialPath);
            }
            if (shader != null) mat.shader = shader;
            var c = UiTheme.Current.colors.background;
            c.a = 0f;
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", c);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ---------------- the context menu ----------------

        static PartContextMenu BuildMenu(GameObject app, OVRCameraRig rig, GameObject template)
        {
            var go = new GameObject("PartContextMenu");
            go.transform.SetParent(app.transform, false);
            var menu = go.AddComponent<PartContextMenu>();
            menu.head = rig.centerEyeAnchor;
            var card = new GameObject("Card");
            card.transform.SetParent(go.transform, false);
            menu.card = card;
            var c = card.transform;
            UiBuild.Distance = EditView.ReadDistance;   // edit-touch: within reach (0.42 m), poke only
            UiBuild.D5Rays = false;
            float top = MenuH * 0.5f, x0 = -MenuW * 0.5f + 0.012f;
            UiBuild.Panel(c, "Panel", new Vector2(MenuW, MenuH));
            menu.title = UiBuild.Text(c, "Title", "Part", TypeRole.Caption, new Vector3(x0, top - 0.02f, 0f), ColorRole.TextSecondary, width: MenuW - 0.024f);
            var size = new Vector2(MenuW - 0.024f, 0.032f);
            float y = top - 0.056f;
            menu.edit = Btn(c, template, "Edit", "Edit", size, ButtonStyle.Secondary, new Vector3(0f, y, 0f), EditViewAction.MenuEdit, role: TypeRole.Label);
            menu.similar = Btn(c, template, "Similar", "View similar", size, ButtonStyle.Secondary, new Vector3(0f, y - 0.038f, 0f), EditViewAction.MenuSimilar, role: TypeRole.Label);
            menu.delete = Btn(c, template, "Delete", "Delete", size, ButtonStyle.Destructive, new Vector3(0f, y - 0.076f, 0f), EditViewAction.MenuDelete, role: TypeRole.Label);
            // After a delete: "Deleted" and Undo.
            var undoRow = new GameObject("UndoRow");
            undoRow.transform.SetParent(c, false);
            UiBuild.Text(undoRow.transform, "Deleted", "Deleted", TypeRole.Label, new Vector3(x0, y, 0f), ColorRole.TextPrimary, width: MenuW - 0.024f);
            menu.undo = Btn(undoRow.transform, template, "Undo", "Undo", size, ButtonStyle.Primary, new Vector3(0f, y - 0.038f, 0f), EditViewAction.MenuUndo,
                role: TypeRole.Label, radius: RadiusRole.Pill);
            menu.undoRow = undoRow;
            undoRow.SetActive(false);
            card.SetActive(false);
            UiBuild.Distance = UiText.HandDistance;
            return menu;
        }

        static GlassButton Btn(Transform parent, GameObject template, string name, string text, Vector2 size, ButtonStyle style, Vector3 pos,
            EditViewAction action, int index = 0, TypeRole role = TypeRole.Caption, RadiusRole? radius = null)
        {
            var b = UiBuild.Button(parent, name, text, size, style, template, pos, role,
                radius ?? (style == ButtonStyle.Chip ? RadiusRole.Pill : RadiusRole.Small));
            var eb = b.gameObject.AddComponent<EditViewButton>();
            eb.button = b; eb.action = action; eb.index = index;
            return b;
        }
    }
}
