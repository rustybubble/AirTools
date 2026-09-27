using AirTools.Agent.Grok;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Editor
{
    /// Grok lane G4 in the Main scene (backend F15 "do the whole job" and F17 install coach): the job's progress
    /// (JobRailView → the status line's job strip; the heads-up rail is its fallback), the coach card (CoachRailView, a
    /// left-side-slot FloatingWindow with Check it / Back / Repeat / Next), the
    /// coach overlay (result boxes, the red stop box and card), the drill crosshair, and the job poller. Built only with
    /// UiBuild and UiTheme tokens (docs/UI.md). Called once from MainSceneBuilder.WireTools (AirTools ▸ Wire Main Scene);
    /// idempotent: it replaces what a previous run built.
    public static class GrokRailsBuilder
    {
        public static void Build(GameObject app, OVRCameraRig rig, AirTools.Scene.SceneRoot sceneRoot, GameObject template,
            AirTools.Tools.MeasureStyle style)
        {
            foreach (var oldName in new[] { "GrokJobRail", "GrokCoachCard" })
            {
                var old = rig.transform.Find(oldName);
                if (old != null) Object.DestroyImmediate(old.gameObject);
            }
            foreach (var oldName in new[] { "GrokCoachOverlay", "GrokDrillCrosshair" })
            {
                var old = app.transform.Find(oldName);
                if (old != null) Object.DestroyImmediate(old.gameObject);
            }
            app.AddComponent<JobRunPoller>();
            BuildJobRail(rig);
            var card = BuildCoachCard(rig, template);
            var lineMat = style != null ? style.lineMaterial : null;
            BuildOverlay(app, rig, lineMat);
            BuildCrosshair(app, rig, card, lineMat);
            UiBuild.Distance = UiText.HandDistance;
            UiBuild.D5Rays = false;
        }

        /// Heads-up text: drawn above every panel like the status line (overlay glass + overlay text). The HUD material is
        /// made on Inter Medium's atlas (UiAssetsBuilder), so heads-up text is Medium whatever its role's weight.
        static TextMeshPro Hud(TextMeshPro t)
        {
            var theme = UiTheme.Current;
            if (theme.type.hud != null)
            {
                t.font = theme.FontAsset(Weight.Medium);
                t.fontSharedMaterial = theme.type.hud;
            }
            return t;
        }

        static GlassSurface HudSurface(Transform parent, string name, Vector2 size, RadiusRole radius, int layer, bool custom)
        {
            var s = GlassSurface.Create(parent, name, size, GlassTier.ElevatedSolid, radius);
            s.hud = true;
            if (custom) { s.customTint = true; s.rim = 0f; s.layer = layer; }
            s.Rebuild();
            return s;
        }

        /// The job rail. Declutter M2 (DC1): the run is the status line's progress mode (JobRailView pushes it there), so
        /// the rail's own panel stays down; it is still built as the `standalone` fallback — read-only, 0.9 m, 14° below
        /// the gaze, 25° left, rows laid out at runtime (JobRailView.Layout).
        static void BuildJobRail(OVRCameraRig rig)
        {
            UiBuild.Distance = 0.9f;   // UX W0.6: sized for where it's read
            var go = new GameObject("GrokJobRail");
            go.transform.SetParent(rig.transform, false);
            // Until the head is tracked: 0.9 m out, 14° down, 25° left of a standing eye.
            go.transform.localPosition = new Vector3(-0.37f, 1.38f, 0.79f);
            var view = go.AddComponent<JobRailView>();
            view.head = rig.centerEyeAnchor;
            view.standalone = false;   // the job strip on the status line (GuideRailBuilder builds it first)
            var line = rig.transform.Find("StatusLine");
            view.status = line != null ? line.GetComponent<StatusLine>() : null;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            view.content = content;
            var c = content.transform;
            float W = view.width;
            view.panel = UiBuild.Panel(c, "Panel", new Vector2(W, 0.2f), elevation: Elevation.None, radius: RadiusRole.Large);
            view.panel.tier = GlassTier.ElevatedSolid;   // near-opaque behind text
            view.panel.hud = true;
            view.panel.Rebuild();
            view.title = Hud(UiBuild.Text(c, "Title", GrokRailText.JobTitle, TypeRole.Label, Vector3.zero, width: W * 0.6f, weight: Weight.Semibold));
            view.count = Hud(UiBuild.Text(c, "Count", "", TypeRole.Caption, Vector3.zero, ColorRole.TextSecondary, TextAlignmentOptions.TopRight, W * 0.3f));
            view.spine = HudSurface(c, "Spine", new Vector2(0.0018f, 0.1f), RadiusRole.Pill, GlassSurface.LayerRow, true);
            const int rows = 8;
            view.dots = new GlassSurface[rows];
            view.glyphs = new TextMeshPro[rows];
            view.names = new TextMeshPro[rows];
            view.results = new TextMeshPro[rows];
            for (int k = 0; k < rows; k++)
            {
                view.dots[k] = HudSurface(c, $"Dot{k}", new Vector2(view.dotSize, view.dotSize), RadiusRole.Pill, GlassSurface.LayerIndicator, true);
                view.glyphs[k] = Hud(UiBuild.Text(c, $"Glyph{k}", "", TypeRole.Caption, Vector3.zero, ColorRole.Background, TextAlignmentOptions.Center, weight: Weight.Semibold));
                view.glyphs[k].rectTransform.pivot = new Vector2(0.5f, 0.5f);
                view.names[k] = Hud(UiBuild.Text(c, $"Name{k}", "", TypeRole.Label, Vector3.zero, width: 0.075f, weight: Weight.Medium));
                view.results[k] = Hud(UiBuild.Text(c, $"Result{k}", "", TypeRole.Caption, Vector3.zero, ColorRole.TextSecondary, width: 0.2f));
            }
            view.caption = Hud(UiBuild.Text(c, "Caption", "", TypeRole.Label, Vector3.zero, width: W - 0.04f));
            view.summary = HudSurface(c, "Summary", new Vector2(W - 0.04f, 0.03f), RadiusRole.Medium, GlassSurface.LayerRow, true);
            view.summaryDot = HudSurface(c, "SummaryDot", new Vector2(0.01f, 0.01f), RadiusRole.Pill, GlassSurface.LayerIndicator, true);
            view.summaryText = Hud(UiBuild.Text(c, "SummaryText", "", TypeRole.Label, Vector3.zero, width: W - 0.07f, weight: Weight.Medium));
            view.detailText = Hud(UiBuild.Text(c, "Detail", "", TypeRole.Caption, Vector3.zero, ColorRole.TextSecondary, width: W - 0.04f));
            content.SetActive(false);
        }

        /// The coach card: the left side slot (DC4, declutter M4), never the main slot: 0.5 m, hanging from 15° below the
        /// eye line, 35° left (Find parts' slot; Find parts yields while the card is up). Ray + poke buttons (D5);
        /// movable with its grab bar.
        static CoachRailView BuildCoachCard(OVRCameraRig rig, GameObject template)
        {
            UiBuild.Distance = 0.5f;   // read from the left side slot
            UiBuild.D5Rays = true;
            var go = new GameObject("GrokCoachCard");
            go.transform.SetParent(rig.transform, false);
            var w = go.AddComponent<FloatingWindow>();
            w.head = rig.centerEyeAnchor;
            w.distance = 0.5f; w.downDeg = 15f; w.yawDeg = -35f; w.mainSlot = false;   // declutter SideLeft zone
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            w.content = content;
            var view = go.AddComponent<CoachRailView>();
            view.window = w;
            view.yawDeg = w.yawDeg;
            var c = content.transform;
            float W = view.width, inner = W - 2f * view.padding;
            view.panel = UiBuild.Panel(c, "Panel", new Vector2(W, 0.22f));
            view.title = UiBuild.Text(c, "Title", "Install coach", TypeRole.Label, Vector3.zero, width: inner - 0.07f, weight: Weight.Semibold);
            view.stepLine = UiBuild.Text(c, "Step", "", TypeRole.Caption, Vector3.zero, ColorRole.TextSecondary, TextAlignmentOptions.TopRight, 0.09f);
            view.close = UiBuild.Button(c, "Close", "Close", new Vector2(0.056f, 0.026f), ButtonStyle.Borderless, template, Vector3.zero, TypeRole.Caption);
            view.spine = GlassSurface.Create(c, "Spine", new Vector2(0.1f, 0.0016f), GlassTier.ElevatedSolid, RadiusRole.Pill);
            view.spine.customTint = true; view.spine.rim = 0f; view.spine.layer = GlassSurface.LayerRow; view.spine.Rebuild();
            const int maxDots = 8;   // the backend writes at most 8 steps (coach.MAX_STEPS)
            view.dots = new GlassSurface[maxDots];
            view.glyphs = new TextMeshPro[maxDots];
            for (int k = 0; k < maxDots; k++)
            {
                var d = GlassSurface.Create(c, $"Dot{k}", new Vector2(view.dotSize, view.dotSize), GlassTier.ElevatedSolid, RadiusRole.Pill);
                d.customTint = true; d.rim = 0f; d.layer = GlassSurface.LayerIndicator; d.Rebuild();
                view.dots[k] = d;
                view.glyphs[k] = UiBuild.Text(c, $"Glyph{k}", "", TypeRole.Caption, Vector3.zero, ColorRole.Background, TextAlignmentOptions.Center, weight: Weight.Semibold);
                view.glyphs[k].rectTransform.pivot = new Vector2(0.5f, 0.5f);
            }
            view.say = UiBuild.Text(c, "Say", "", TypeRole.Label, Vector3.zero, width: inner);
            view.manual = UiBuild.Text(c, "Manual", "", TypeRole.Caption, Vector3.zero, ColorRole.TextSecondary, width: inner - 0.084f);
            view.openManual = UiBuild.Button(c, "OpenManual", "Open manual", new Vector2(0.078f, 0.028f), ButtonStyle.Secondary, template, Vector3.zero, TypeRole.Caption);
            const int maxChips = 3;   // coach.MAX_CHECKS
            view.chipRows = new GlassSurface[maxChips];
            view.chipDots = new GlassSurface[maxChips];
            view.chipTexts = new TextMeshPro[maxChips];
            for (int k = 0; k < maxChips; k++)
            {
                view.chipRows[k] = GlassSurface.Create(c, $"Chip{k}", new Vector2(inner, 0.03f), GlassTier.ElevatedSolid, RadiusRole.Medium);
                var dot = GlassSurface.Create(c, $"ChipDot{k}", new Vector2(0.009f, 0.009f), GlassTier.ElevatedSolid, RadiusRole.Pill);
                dot.customTint = true; dot.rim = 0f; dot.layer = GlassSurface.LayerIndicator; dot.Rebuild();
                view.chipDots[k] = dot;
                view.chipTexts[k] = UiBuild.Text(c, $"ChipText{k}", "", TypeRole.Caption, Vector3.zero, width: inner - 0.024f);
            }
            view.verdict = UiBuild.Text(c, "Verdict", "", TypeRole.Caption, Vector3.zero, width: inner, weight: Weight.Medium);
            view.summary = UiBuild.Text(c, "Summary", "", TypeRole.Label, Vector3.zero, width: inner, weight: Weight.Medium);
            float bw = (inner - 3 * 0.006f) / 4f;
            var bs = new Vector2(bw, 0.03f);
            view.checkIt = UiBuild.Button(c, "CheckIt", "Check it", bs, ButtonStyle.Primary, template, Vector3.zero, TypeRole.Caption, RadiusRole.Pill);
            view.back = UiBuild.Button(c, "Back", "Back", bs, ButtonStyle.Secondary, template, Vector3.zero, TypeRole.Caption, RadiusRole.Pill);
            view.repeat = UiBuild.Button(c, "Repeat", "Repeat", bs, ButtonStyle.Secondary, template, Vector3.zero, TypeRole.Caption, RadiusRole.Pill);
            view.next = UiBuild.Button(c, "Next", "Next", bs, ButtonStyle.Secondary, template, Vector3.zero, TypeRole.Caption, RadiusRole.Pill);
            view.note = UiBuild.Text(c, "Note", "", TypeRole.Caption, Vector3.zero, ColorRole.TextSecondary, width: inner);
            w.handle = UiBuild.Handle(w.transform, rig.centerEyeAnchor, new Vector3(0f, -0.24f, 0f), template);
            w.handle.transform.SetParent(c, true);
            content.SetActive(false);
            UiBuild.D5Rays = false;
            return view;
        }

        static LineRenderer Line(Transform parent, string name, Material mat, bool world)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = world;
            lr.widthMultiplier = 0.004f;
            lr.numCapVertices = 2;
            lr.positionCount = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            if (mat != null) lr.sharedMaterial = mat;
            go.SetActive(false);
            return lr;
        }

        /// Result boxes and the stop card: the overlay root follows the scene content at runtime (package space).
        static void BuildOverlay(GameObject app, OVRCameraRig rig, Material lineMat)
        {
            var go = new GameObject("GrokCoachOverlay");
            go.transform.SetParent(app.transform, false);
            var overlay = go.AddComponent<CoachOverlay>();
            overlay.head = rig.centerEyeAnchor;
            overlay.boxes = new LineRenderer[4];   // up to 3 check boxes + the stop box
            for (int k = 0; k < overlay.boxes.Length; k++) overlay.boxes[k] = Line(go.transform, $"Box{k}", lineMat, false);
            overlay.drillMark = Line(go.transform, "DrillMark", lineMat, true);
            // The red card: built for reading at 1 m, scaled with distance at runtime (a world annotation).
            UiBuild.Distance = 1f;
            var card = new GameObject("StopCard");
            card.transform.SetParent(go.transform, false);
            overlay.stopCard = card.transform;
            var theme = UiTheme.Current;
            overlay.stopSurface = GlassSurface.Create(card.transform, "Card", new Vector2(0.34f, 0.05f), GlassTier.ElevatedSolid, RadiusRole.Pill);
            overlay.stopSurface.overlay = true;
            overlay.stopSurface.customTint = true;
            overlay.stopSurface.tint = theme.Color(ColorRole.Danger);
            overlay.stopSurface.rim = 0f;
            overlay.stopSurface.Rebuild();
            overlay.stopText = UiBuild.Text(card.transform, "Text", "Don't drill here", TypeRole.Label, new Vector3(0f, 0f, -0.002f),
                ColorRole.Background, TextAlignmentOptions.Center, 0.32f, Weight.Semibold);
            overlay.stopText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            if (theme.type.annotation != null)
            {
                overlay.stopText.font = theme.FontAsset(Weight.Medium);
                overlay.stopText.fontSharedMaterial = theme.type.annotation;
            }
            card.SetActive(false);
        }

        static void BuildCrosshair(GameObject app, OVRCameraRig rig, CoachRailView card, Material lineMat)
        {
            var go = new GameObject("GrokDrillCrosshair");
            go.transform.SetParent(app.transform, false);
            var x = go.AddComponent<DrillCrosshair>();
            x.head = rig.centerEyeAnchor;
            x.card = card != null && card.panel != null ? card.panel.transform : null;   // the card's centre (it hangs from its slot point)
            x.cardView = card;
            x.ring = Line(go.transform, "Ring", lineMat, true);
            x.centre = Line(go.transform, "Centre", lineMat, true);
        }
    }
}
