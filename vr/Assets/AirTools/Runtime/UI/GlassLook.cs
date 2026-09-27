using System;
using UnityEngine;

namespace AirTools.UI
{
    /// What a glass surface is for (docs/UI.md §3). The role picks its Liquid Glass look (GlassLook, UiTheme.glass); the
    /// tier still picks the shared material (pipeline state: queue, depth). Auto (the default, so every surface built
    /// before roles existed, and every builder that doesn't say, gets one) resolves from the surface's other fields —
    /// see GlassLooks.Resolve.
    public enum GlassRole { Auto, Window, Card, Control, Hud, Label, Mark }

    /// The Liquid Glass look of one surface role: the tool ring's material (AirTools/LiquidGlass) made cheap enough for
    /// every panel. All of it travels in vertex data (GlassMesh), so the role's material stays shared.
    [Serializable]
    public struct GlassLook
    {
        [Tooltip("Widest bezel (the lensing rim), in the surface's local units (metres for UI at unit scale).")]
        public float bezel;
        [Tooltip("Bezel as a fraction of the smaller half-extent; the narrower of the two wins (labels in scaled spaces use this).")]
        public float bezelFraction;
        [Tooltip("Rim light, 0..1: the key-light specular, the opposite-edge fill, Fresnel, the studio reflection and the inner " +
                 "stroke, where 1 = the tool ring's own amounts.")]
        public float highlight;
        [Tooltip("How much clearer the outermost bezel is than the body, 0..1 (0 = as dense as the body). Keeps the solid floor " +
                 "behind text while the edge reads as glass.")]
        public float edgeClear;
        [Tooltip("Body glint from the noise-normal ripple catching the key light (linear, added). Counts toward the text lift.")]
        public float sheen;
        [Tooltip("Frost grain amplitude (linear): the stand-in for a background blur (no grab pass on Quest). Half of it counts " +
                 "toward the text lift.")]
        public float frost;
        [Tooltip("Top-lit body gradient (linear, added at the top edge, removed at the bottom). Counts toward the text lift.")]
        public float gradient;

        public GlassLook(float bezel, float bezelFraction, float highlight, float edgeClear, float sheen, float frost, float gradient)
        {
            this.bezel = bezel; this.bezelFraction = bezelFraction; this.highlight = highlight; this.edgeClear = edgeClear;
            this.sheen = sheen; this.frost = frost; this.gradient = gradient;
        }

        /// No liquid: a flat mark (dots, bars, separators).
        public static GlassLook None => default;

        public bool IsLiquid => bezel > 0f && bezelFraction > 0f && highlight > 0f;
    }

    /// Pure rules for the glass roles (offline-testable; GlassSurface and the shader follow them).
    public static class GlassLooks
    {
        /// Surfaces this small (smaller side, local units) are marks — status dots, indicator bars, separators, grab
        /// bars, swatch dots: flat, no bezel.
        public const float MarkMaxSize = 0.012f;

        /// The role a surface draws with. An explicit role wins; Auto reads the surface: heads-up → Hud, world annotation
        /// (overlay) → Label, anything with a side ≤ 12 mm → Mark, a tinted control/chip (customTint) → Control, a row or
        /// card (ElevatedSolid) → Card, else a window / menu panel → Window.
        public static GlassRole Resolve(GlassRole requested, GlassTier tier, bool hud, bool overlay, bool customTint, Vector2 size)
        {
            if (requested != GlassRole.Auto) return requested;
            if (tier == GlassTier.Shadow) return GlassRole.Mark;
            if (overlay) return GlassRole.Label;
            if (Mathf.Min(size.x, size.y) <= MarkMaxSize) return GlassRole.Mark;
            if (hud) return GlassRole.Hud;
            if (customTint) return GlassRole.Control;
            if (tier == GlassTier.ElevatedSolid) return GlassRole.Card;
            return GlassRole.Window;
        }

        /// The bezel a surface of this size draws (local units): the role's cap or its fraction of the smaller half-extent.
        public static float Bezel(GlassLook look, Vector2 size)
        {
            if (!look.IsLiquid) return 0f;
            return Mathf.Max(0f, Mathf.Min(look.bezel, look.bezelFraction * 0.5f * Mathf.Min(size.x, size.y)));
        }

        /// The most the body can brighten behind text (linear, added to every channel): the top of the gradient, half the
        /// frost grain, the whole body sheen. The contrast checks lay this over the tint (ErgonomicsTests, LiquidGlassTests).
        public static float InteriorLift(GlassLook look) => Mathf.Max(0f, look.gradient) + Mathf.Max(0f, look.frost) * 0.5f + Mathf.Max(0f, look.sheen);

        /// The state glow of a button (brighter rim, stroke and highlight): press 1, hover 0.6, selected 0.3, else 0;
        /// nothing while it can't be pressed.
        public static float Glow(bool interactable, bool hover, bool press, bool selected)
        {
            if (!interactable) return 0f;
            if (press) return 1f;
            if (hover) return 0.6f;
            return selected ? 0.3f : 0f;
        }
    }
}
