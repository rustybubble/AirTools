using AirTools.UI;
using UnityEngine;

namespace AirTools.Editor
{
    /// UX W1.3 guide rail in the Main scene: the status line (heads-up, 0.9 m, 17° below the gaze), the Next-step pill
    /// (poke range: 0.45 m, 24° down, 22° right of the window slot), and GuideRail + CoachService on the app object.
    /// Built only with UiBuild and UiTheme.Current (docs/UI.md). Called once from MainSceneBuilder.WireTools (AirTools ▸
    /// Wire Main Scene); idempotent: it replaces what a previous run built. The rail starts off (GuideRail.Enabled).
    public static class GuideRailBuilder
    {
        public static GuideRail Build(GameObject app, OVRCameraRig rig, GameObject pokeTemplate)
        {
            foreach (var oldName in new[] { "StatusLine", "NextStepPill" })
            {
                var old = rig.transform.Find(oldName);
                if (old != null) Object.DestroyImmediate(old.gameObject);
            }
            var status = BuildStatusLine(rig);
            var pill = BuildPill(rig, pokeTemplate);
            var coach = app.AddComponent<CoachService>();
            coach.status = status;
            coach.pill = pill;
            var rail = app.AddComponent<GuideRail>();
            rail.status = status;
            rail.pill = pill;
            rail.coach = coach;
            UiBuild.Distance = UiText.HandDistance;
            return rail;
        }

        /// Heads-up like the toast (overlay glass + overlay text), one status line plus a quieter coach line.
        static StatusLine BuildStatusLine(OVRCameraRig rig)
        {
            UiBuild.Distance = 0.9f;   // UX W0.6: sized for where it's read
            var go = new GameObject("StatusLine");
            go.transform.SetParent(rig.transform, false);
            go.transform.localPosition = new Vector3(0f, 1.34f, 0.86f);   // until the head is tracked: 0.9 m out, 17° down
            var line = go.AddComponent<StatusLine>();
            line.head = rig.centerEyeAnchor;
            line.surface = UiBuild.Panel(go.transform, "Pill", new Vector2(0.28f, 0.05f), elevation: Elevation.None, radius: RadiusRole.Pill);
            line.surface.tier = GlassTier.ElevatedSolid;   // near-opaque behind text
            line.surface.hud = true;
            line.surface.Rebuild();
            line.text = UiBuild.Text(go.transform, "Text", "", TypeRole.Label, new Vector3(0f, 0f, -0.002f),
                align: TMPro.TextAlignmentOptions.Center, width: line.maxWidth);
            line.text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            line.coachText = UiBuild.Text(go.transform, "Coach", "", TypeRole.Caption, new Vector3(0f, -0.03f, -0.002f), ColorRole.TextSecondary,
                TMPro.TextAlignmentOptions.Center, line.maxWidth);
            line.coachText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            line.coachText.gameObject.SetActive(false);
            var theme = UiTheme.Current;
            if (theme.type.hud != null)
            {
                line.text.font = theme.FontAsset(Weight.Medium);
                line.text.fontSharedMaterial = theme.type.hud;
                line.coachText.font = theme.FontAsset(Weight.Regular);
                line.coachText.fontSharedMaterial = theme.type.hud;
            }
            line.dot = GlassSurface.Create(go.transform, "Dot", new Vector2(0.011f, 0.011f), GlassTier.ElevatedSolid, RadiusRole.Pill);
            line.dot.customTint = true;
            line.dot.rim = 0f;
            line.dot.hud = true;
            line.dot.layer = GlassSurface.LayerIndicator;
            line.dot.Rebuild();
            return line;
        }

        /// One primary button and two secondary chips (the secondaries sit side by side under it; NextStepPill centres a
        /// lone one). Ray + poke once D5 is on (GlassButton.RayOnWindows), poke only until then.
        static NextStepPill BuildPill(OVRCameraRig rig, GameObject pokeTemplate)
        {
            UiBuild.Distance = 0.45f;
            UiBuild.D5Rays = true;
            var go = new GameObject("NextStepPill");
            go.transform.SetParent(rig.transform, false);
            // Until the head is tracked: 0.45 m out along a line 24° down and 22° right of a standing eye.
            go.transform.localPosition = new Vector3(0.155f, 1.42f, 0.38f);
            go.transform.localRotation = Quaternion.Euler(24f, 22f, 0f);
            var pill = go.AddComponent<NextStepPill>();
            pill.head = rig.centerEyeAnchor;
            pill.distance = 0.45f;
            pill.downDeg = 24f;
            pill.yawDeg = 22f;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            pill.content = content;
            var c = content.transform;
            const float W = 0.2f, H = 0.044f, chipW = 0.097f, chipH = 0.032f;
            pill.primary = UiBuild.Button(c, "Primary", "Next step", new Vector2(W, H), ButtonStyle.Primary, pokeTemplate,
                new Vector3(0f, 0f, 0f), TypeRole.Label, RadiusRole.Pill);
            float y = -H * 0.5f - 0.008f - chipH * 0.5f;
            pill.secondary0 = UiBuild.Button(c, "Secondary0", "", new Vector2(chipW, chipH), ButtonStyle.Secondary, pokeTemplate,
                new Vector3(-(chipW + pill.gap) * 0.5f, y, 0f), TypeRole.Caption, RadiusRole.Pill);
            pill.secondary1 = UiBuild.Button(c, "Secondary1", "", new Vector2(chipW, chipH), ButtonStyle.Secondary, pokeTemplate,
                new Vector3((chipW + pill.gap) * 0.5f, y, 0f), TypeRole.Caption, RadiusRole.Pill);
            UiBuild.D5Rays = false;
            content.SetActive(false);
            return pill;
        }
    }
}
