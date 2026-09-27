using System.Linq;
using AirTools.Parts;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Editor
{
    /// The placement editor's UI (Parts/PlacementEditor), built only with UiBuild; colours from UiTheme.Current; behaviour
    /// on GlassButton.Clicked (PlacementButton). Two pieces, both called from MainSceneBuilder (AirTools ▸ Wire Main Scene):
    /// - the Adjust chip on the part's card (the spec inspector in the palm menu), right of the finish swatches;
    /// - the adjust panel, a main-slot card (declutter §3.3: a decision with buttons; 0.45 m, 20° down, 0.36 × 0.32 m,
    ///   UiZones.Main): title + Done (the title row holds only those), the readout (offset · angles), the fit line, a move
    ///   pad (← → ↑ ↓ Out In), a turn pad (Turn ← → · Tilt ↑ ↓ · Roll ← →), Step / Snap / Reset to fit, the model arrows
    ///   with "Model 2 of 3", Size & finish (assetgen: the size line, W / H / D − + pads, Fit to opening and three finish
    ///   chips; 0.42 m tall, inside UiZones.Main's 0.44), and Save placement (the one primary) with the A–D slot chips.
    ///   Movable by its grab bar.
    /// Poke first (hands-only works); window buttons also take the ray under D5, like the other main-slot cards.
    public static class PlacementPanelBuilder
    {
        public const float W = 0.36f, H = 0.42f;   // assetgen: 0.32 + the Size & finish rows
        /// The chip's size and its spot on the card's swatch row (the inspector's section is 0.25 m wide; text 0.22 m).
        static readonly Vector2 ChipSize = new Vector2(0.07f, 0.026f);

        /// The Adjust chip, a Chip-style toggle (ink while adjusting). `swatchRowY`: the finish swatches' row.
        public static GlassButton BuildAdjustChip(Transform specContent, GameObject template, float textWidth, float swatchRowY)
        {
            var old = specContent.Find("Adjust");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            // edit6dof: the chip opens the Edit view (EditView) — it reads "Edit"; the name stays (the scene's references).
            var b = UiBuild.Button(specContent, "Adjust", "Edit", ChipSize, ButtonStyle.Chip, template,
                new Vector3(textWidth - ChipSize.x * 0.5f, swatchRowY, 0f), TypeRole.Caption, RadiusRole.Pill);
            b.tooltip = "Turn, colour and move this part";
            var pb = b.gameObject.AddComponent<PlacementButton>();
            pb.button = b; pb.action = PlacementAction.ToggleAdjust;
            return b;
        }

        /// The panel and the editor (on `app`), wired to the part tool, the loader, Find parts, the Adjust chip, a gizmo.
        public static PlacementEditor Build(GameObject app, OVRCameraRig rig, GameObject template, PartTool tool, PartLoader loader, Material lineMaterial)
        {
            var editor = app.AddComponent<PlacementEditor>();
            editor.tool = tool;
            editor.loader = loader;
            editor.browser = rig.GetComponentInChildren<PartsBrowser>(true);
            editor.adjustChip = rig.GetComponentsInChildren<PlacementButton>(true).FirstOrDefault(p => p.action == PlacementAction.ToggleAdjust)?.button;
            editor.panel = BuildPanel(rig, template);

            var gizmoGo = new GameObject("PlacementGizmo");
            gizmoGo.transform.SetParent(app.transform, false);
            var gizmo = gizmoGo.AddComponent<PlacementGizmo>();
            gizmo.lineMaterial = lineMaterial;
            gizmo.head = rig.centerEyeAnchor;
            editor.gizmo = gizmo;
            return editor;
        }

        static PlacementPanel BuildPanel(OVRCameraRig rig, GameObject template)
        {
            const string name = "PlacementPanel";
            var old = rig.transform.Find(name);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            UiBuild.Distance = 0.45f;
            UiBuild.D5Rays = true;
            var go = new GameObject(name);
            go.transform.SetParent(rig.transform, false);
            var w = go.AddComponent<FloatingWindow>();
            w.head = rig.centerEyeAnchor;
            w.distance = 0.45f; w.downDeg = 20f; w.yawDeg = 0f; w.mainSlot = true;   // the main slot (UiZones.Main)
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            w.content = content;
            var panel = go.AddComponent<PlacementPanel>();
            panel.window = w;
            var c = content.transform;
            float top = H * 0.5f, x0 = -W * 0.5f + 0.016f;
            UiBuild.Panel(c, "Panel", new Vector2(W, H));

            // Title row: the title and Done only (declutter M8).
            panel.title = UiBuild.Text(c, "Title", "Adjust", TypeRole.Heading, new Vector3(x0, top - 0.026f, 0f), width: W - 0.1f);
            panel.done = Btn(c, template, "Close", "Done",   // named Close: the title row's close (declutter M8); it reads Done
                new Vector2(0.06f, 0.026f), ButtonStyle.Borderless, new Vector3(W * 0.5f - 0.016f - 0.03f, top - 0.026f, 0f), PlacementAction.Done);

            // Readout: offset and angles (live, tabular), then the fit in its colour and words.
            panel.readout = UiBuild.Text(c, "Readout", "Right 0″ · Up 0″ · Out 0″\nTurn 0° · Tilt 0° · Roll 0°", TypeRole.Label,
                new Vector3(x0, top - 0.068f, 0f), width: W - 0.032f, weight: Weight.Medium);
            panel.fit = UiBuild.Text(c, "Fit", "", TypeRole.Label, new Vector3(x0, top - 0.102f, 0f), width: W - 0.032f, weight: Weight.Medium);

            // Move pad: ← → ↑ ↓ Out In (index: 0 right, 1 left, 2 up, 3 down, 4 out, 5 in).
            var pad = new Vector2(0.052f, 0.03f);
            float PadX(int k) => -W * 0.5f + 0.014f + pad.x * 0.5f + k * (pad.x + 0.004f);
            float row = top - 0.138f;
            (string text, int index)[] move = { ("←", 1), ("→", 0), ("↑", 2), ("↓", 3), ("Out", 4), ("In", 5) };
            for (int k = 0; k < move.Length; k++)
                Btn(c, template, $"Move{k}", move[k].text, pad, ButtonStyle.Secondary, new Vector3(PadX(k), row, 0f), PlacementAction.Nudge, move[k].index);

            // Turn pad (index: 0 turn + = clockwise from above: the front turns to your left, 1 turn −, 2 tilt + = the top
            // away: the front looks up, 3 tilt −, 4 roll + = clockwise as you face it, 5 roll −).
            row -= 0.036f;
            (string text, int index)[] turn = { ("Turn ←", 0), ("Turn →", 1), ("Tilt ↑", 2), ("Tilt ↓", 3), ("Roll ←", 5), ("Roll →", 4) };
            for (int k = 0; k < turn.Length; k++)
                Btn(c, template, $"Turn{k}", turn[k].text, pad, ButtonStyle.Secondary, new Vector3(PadX(k), row, 0f), PlacementAction.Rotate, turn[k].index);

            // Step (fine) · Snap · Reset to fit.
            row -= 0.036f;
            var fine = Btn(c, template, "Fine", AirTools.Parts.PlacementMath.StepLabel(UiSettings.DefaultUnits, false), new Vector2(0.12f, 0.028f), ButtonStyle.Chip,
                new Vector3(x0 + 0.06f, row, 0f), PlacementAction.Fine);
            panel.fine = fine;
            panel.snap = Btn(c, template, "Snap", "Snap", new Vector2(0.066f, 0.028f), ButtonStyle.Chip, new Vector3(x0 + 0.126f + 0.033f, row, 0f), PlacementAction.Snap);
            Btn(c, template, "Reset", "Reset to fit", new Vector2(0.1f, 0.028f), ButtonStyle.Secondary, new Vector3(W * 0.5f - 0.016f - 0.05f, row, 0f), PlacementAction.Reset);

            // Models: ← Model · "Model 2 of 3" · Model →.
            row -= 0.036f;
            panel.prevModel = Btn(c, template, "PrevModel", "← Model", new Vector2(0.086f, 0.028f), ButtonStyle.Secondary, new Vector3(x0 + 0.043f, row, 0f), PlacementAction.PrevModel);
            panel.model = UiBuild.Text(c, "Model", "", TypeRole.Caption, new Vector3(0f, row, 0f), ColorRole.TextSecondary, TMPro.TextAlignmentOptions.Center);
            panel.nextModel = Btn(c, template, "NextModel", "Model →", new Vector2(0.086f, 0.028f), ButtonStyle.Secondary, new Vector3(W * 0.5f - 0.016f - 0.043f, row, 0f), PlacementAction.NextModel);

            // assetgen: Size & finish — the size line, the W / H / D pad (index: 0 W −, 1 W +, 2 H −, 3 H +, 4 D −, 5 D +),
            // Fit to opening and three finish chips (named from the part when the panel shows it).
            row -= 0.034f;
            panel.size = UiBuild.Text(c, "Size", "Size & finish · 58½ × 46¾ × 3¼″ · as listed", TypeRole.Label, new Vector3(x0, row, 0f),
                width: W - 0.032f, weight: Weight.Medium);
            row -= 0.032f;
            (string text, int index)[] sizes = { ("W −", 0), ("W +", 1), ("H −", 2), ("H +", 3), ("D −", 4), ("D +", 5) };
            var sizeSteps = new GlassButton[sizes.Length];
            for (int k = 0; k < sizes.Length; k++)
                sizeSteps[k] = Btn(c, template, $"Size{k}", sizes[k].text, pad, ButtonStyle.Secondary, new Vector3(PadX(k), row, 0f), PlacementAction.SizeStep, sizes[k].index);
            panel.sizeSteps = sizeSteps;
            row -= 0.036f;
            panel.fitToOpening = Btn(c, template, "FitToOpening", "Fit to opening", new Vector2(0.11f, 0.028f), ButtonStyle.Secondary,
                new Vector3(x0 + 0.055f, row, 0f), PlacementAction.FitToOpening);
            var finishChips = new GlassButton[3];
            var fchip = new Vector2(0.066f, 0.028f);
            for (int k = 0; k < finishChips.Length; k++)
            {
                float x = W * 0.5f - 0.016f - fchip.x * 0.5f - (finishChips.Length - 1 - k) * (fchip.x + 0.006f);
                finishChips[k] = Btn(c, template, $"Finish{k}", k == 0 ? "White" : k == 1 ? "Black" : "Bronze", fchip, ButtonStyle.Chip,
                    new Vector3(x, row, 0f), PlacementAction.Finish, k, radius: RadiusRole.Pill);
            }
            panel.finishes = finishChips;

            // Save placement (the one primary) and the A–D slots.
            row -= 0.04f;
            panel.save = Btn(c, template, "Save", "Save placement", new Vector2(0.13f, 0.032f), ButtonStyle.Primary, new Vector3(x0 + 0.065f, row, 0f), PlacementAction.Save,
                role: TypeRole.Label, radius: RadiusRole.Pill);
            var slots = new GlassButton[PlacementMath.Slots.Length];
            var chip = new Vector2(0.036f, 0.028f);
            for (int k = 0; k < slots.Length; k++)
            {
                float x = W * 0.5f - 0.016f - chip.x * 0.5f - (slots.Length - 1 - k) * (chip.x + 0.006f);
                slots[k] = Btn(c, template, $"Slot{PlacementMath.Slots[k]}", PlacementMath.Slots[k], chip, ButtonStyle.Chip, new Vector3(x, row, 0f), PlacementAction.Slot, k,
                    radius: RadiusRole.Pill);
                slots[k].interactable = false;
            }
            panel.slots = slots;

            w.handle = UiBuild.Handle(w.transform, rig.centerEyeAnchor, new Vector3(0f, -H * 0.5f - 0.016f, 0f), template);
            w.handle.transform.SetParent(c, true);
            content.SetActive(false);
            UiBuild.D5Rays = false;
            return panel;
        }

        static GlassButton Btn(Transform parent, GameObject template, string name, string text, Vector2 size, ButtonStyle style, Vector3 pos,
            PlacementAction action, int index = 0, TypeRole role = TypeRole.Caption, RadiusRole? radius = null)
        {
            var b = UiBuild.Button(parent, name, text, size, style, template, pos, role,
                radius ?? (style == ButtonStyle.Chip ? RadiusRole.Pill : RadiusRole.Small));
            var pb = b.gameObject.AddComponent<PlacementButton>();
            pb.button = b; pb.action = action; pb.index = index;
            return b;
        }
    }
}
