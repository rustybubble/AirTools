using System.Linq;
using AirTools.UI;
using Oculus.Interaction;
using Oculus.Interaction.Surfaces;
using TMPro;
using UnityEngine;

namespace AirTools.Editor
{
    /// Scene-building helpers for the design system: every button is a GlassButton over a clone of the Poke
    /// Interaction building block's interactable (its PokeInteractable + clipped plane surface). Buttons are poked
    /// (UI sits within reach); a RayInteractable can be added for far-away UI. Grab bars are ray-dragged.
    /// Liquid glass (glass lane, docs/UI.md §3): everything built here inherits the tool ring's look through its glass
    /// role — Panel → Window (Hud once a builder marks it heads-up), Button → Control, the selected bar and grab bar →
    /// Mark. Builders don't restyle; they pick a role only when Auto would guess wrong.
    public static class UiBuild
    {
        /// Distance the surface being built is read from (sets text sizes): its real placement distance (UX W0.6).
        public static float Distance = UiText.HandDistance;

        /// While true, every button built also gets a ray target that follows the D5 flag (GlassButton.RayOnWindows,
        /// default off = poke only): windows and the Enter control (UX W0.7 / D5).
        public static bool D5Rays;

        public static GlassButton Button(Transform parent, string name, string text, Vector2 size, ButtonStyle style,
            GameObject pokeTemplate, Vector3 localPos = default, TypeRole role = TypeRole.Label, RadiusRole radius = RadiusRole.Small,
            bool ray = false)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.localPosition = localPos;
            var b = root.AddComponent<GlassButton>();
            b.style = style;

            if (pokeTemplate != null)
            {
                var clone = Object.Instantiate(pokeTemplate, root.transform);
                clone.name = "Interaction";
                clone.SetActive(true);
                foreach (var bb in clone.GetComponents<MonoBehaviour>())
                    if (bb != null && bb.GetType().Name == "BuildingBlock") Object.DestroyImmediate(bb);
                var visuals = clone.transform.Find("Visuals");
                if (visuals != null) Object.DestroyImmediate(visuals.gameObject);
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.transform.localScale = new Vector3(size.x, size.y, Mathf.Min(size.x, size.y));
                b.poke = clone.GetComponent<PokeInteractable>();
                // Poke only by default: the hand ray is the measuring pointer, and letting it also press buttons made
                // pinches on the scene hit UI in the ray's path (e.g. Exit world). Opt in for far-away UI.
                var surface = clone.GetComponentInChildren<ClippedPlaneSurface>(true);
                if ((ray || D5Rays) && surface != null)
                {
                    var r = clone.AddComponent<RayInteractable>();
                    r.InjectAllRayInteractable(surface);
                    b.ray = r;
                    b.rayNeedsD5 = !ray;
                }
            }

            var visual = new GameObject("Visual").transform;
            visual.SetParent(root.transform, false);
            b.visual = visual;
            b.surface = GlassSurface.Create(visual, "Surface", size, GlassTier.GlassClear, radius);
            b.surface.role = GlassRole.Control;   // liquid glass: a glass capsule whose rim carries hover / press / selected
            b.surface.customTint = true;
            b.surface.tint = UiTheme.Current.colors.control;
            b.surface.rim = style == ButtonStyle.Borderless ? 0f : 0.6f;
            b.surface.Rebuild();
            b.label = UiText.Create(visual, "Label", text, role, Distance, align: TextAlignmentOptions.Center,
                localPos: new Vector3(0f, 0f, -0.0015f));
            FitLabel(b.label, size);
            b.SetText(text);
            // Selected indicator: a short bar along the bottom edge (state never by colour alone).
            b.indicator = GlassSurface.Create(visual, "Indicator", new Vector2(Mathf.Min(0.02f, size.x * 0.35f), 0.0022f),
                GlassTier.ElevatedSolid, RadiusRole.Pill, localPos: new Vector3(0f, -size.y * 0.5f + 0.0035f, -0.001f));
            b.indicator.customTint = true;
            b.indicator.role = GlassRole.Mark;
            b.indicator.layer = GlassSurface.LayerIndicator;
            b.indicator.tint = UiTheme.Current.colors.onInk;   // D3: a dark bar on the ink (selected) fill
            b.indicator.rim = 0f;
            b.indicator.Rebuild();
            b.indicator.gameObject.SetActive(false);
            return b;
        }

        /// Side and top/bottom insets of a button's label box (m).
        public const float LabelInsetX = 0.003f, LabelInsetY = 0.002f;

        /// Every button / chip label fits its button (gate capture: the finish chip "Brickmould Nail Fin Frame" ran past
        /// its chip): TMP auto size from the role's size down to the caption token at the build distance (never below the
        /// 16 dmm floor), wrapping to a second line when the box is tall enough, then an ellipsis. Text set later
        /// (GlassButton.SetText, a confirm's "Tap again to confirm") fits the same way. Builders that lay a label out
        /// themselves (Model view cards, world chips) still can: set the box after this.
        public static void FitLabel(TextMeshPro label, Vector2 buttonSize)
        {
            if (label == null) return;
            label.rectTransform.sizeDelta = new Vector2(Mathf.Max(0.004f, buttonSize.x - 2f * LabelInsetX), Mathf.Max(0.004f, buttonSize.y - 2f * LabelInsetY));
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Ellipsis;
            float max = label.fontSize;
            label.fontSizeMax = max;
            label.fontSizeMin = Mathf.Min(max, UiText.FontSizeForEm(UiText.EmMetres(TypeRole.Caption, Distance)));
            label.enableAutoSizing = true;
        }

        /// One glass panel per window. Its role stays Auto unless given: Window (Hud when the builder sets `hud`, Card on an
        /// ElevatedSolid tier), so a heads-up pill made from a panel picks up the heads-up look by itself.
        public static GlassSurface Panel(Transform parent, string name, Vector2 size, GlassTier tier = GlassTier.GlassRegular,
            Elevation elevation = Elevation.Panel, Vector3 localPos = default, RadiusRole radius = RadiusRole.Large,
            GlassRole role = GlassRole.Auto)
        {
            // Panels sit a few mm behind their content.
            var p = GlassSurface.Create(parent, name, size, tier, radius, elevation, localPos + new Vector3(0f, 0f, 0.003f));
            if (role != GlassRole.Auto) { p.role = role; p.Rebuild(); }
            return p;
        }

        public static TextMeshPro Text(Transform parent, string name, string text, TypeRole role, Vector3 localPos,
            ColorRole color = ColorRole.TextPrimary, TextAlignmentOptions align = TextAlignmentOptions.Left, float width = 0f, Weight? weight = null)
        {
            var t = UiText.Create(parent, name, text, role, Distance, color, align, width, weight, localPos);
            if (width > 0f)
            {
                // Left-aligned text boxes grow right from localPos.
                t.rectTransform.pivot = new Vector2(align == TextAlignmentOptions.Right || align == TextAlignmentOptions.TopRight ? 1f : 0f, 0.5f);
            }
            return t;
        }

        /// Quiet grab bar under a window (ray pinch-drag to move it).
        public static WindowHandle Handle(Transform window, Transform head, Vector3 localPos, GameObject pokeTemplate)
        {
            var root = new GameObject("GrabHandle");
            root.transform.SetParent(window, false);
            root.transform.localPosition = localPos;
            var h = root.AddComponent<WindowHandle>();
            h.window = window;
            h.head = head;
            h.bar = GlassSurface.Create(root.transform, "Bar", new Vector2(0.07f, 0.0065f), GlassTier.ElevatedSolid, RadiusRole.Pill);
            h.bar.customTint = true;
            h.bar.role = GlassRole.Mark;
            h.bar.tint = UiTheme.Current.colors.handle;   // D3: token (was a hard-coded white 35%)
            h.bar.rim = 0f;
            h.bar.Rebuild();
            if (pokeTemplate != null)
            {
                // A generous ray target around the thin bar.
                var clone = Object.Instantiate(pokeTemplate, root.transform);
                clone.name = "Target";
                clone.SetActive(true);
                foreach (var bb in clone.GetComponents<MonoBehaviour>())
                    if (bb != null && bb.GetType().Name == "BuildingBlock") Object.DestroyImmediate(bb);
                var visuals = clone.transform.Find("Visuals");
                if (visuals != null) Object.DestroyImmediate(visuals.gameObject);
                Object.DestroyImmediate(clone.GetComponent<PokeInteractable>());
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localScale = new Vector3(0.10f, 0.03f, 0.03f);
                var surface = clone.GetComponentInChildren<ClippedPlaneSurface>(true);
                var ray = clone.AddComponent<RayInteractable>();
                ray.InjectAllRayInteractable(surface);
                h.ray = ray;
            }
            return h;
        }
    }
}
