using System;
using TMPro;
using UnityEngine;

namespace AirTools.UI
{
    /// Surface material tiers (the only three panel materials, plus the shadow used for elevation).
    public enum GlassTier { GlassRegular, GlassClear, ElevatedSolid, Shadow }

    /// Semantic colour roles.
    public enum ColorRole
    {
        Background, Surface, GlassRegular, GlassClear, ElevatedSolid, TextPrimary, TextSecondary, TextDisabled,
        [Obsolete("UX D3: Info for a neutral status tone; Primary / Ink for fills")] Accent,
        [Obsolete("UX D3: InkPressed")] AccentPressed,
        Danger, Success, Warning, Separator, Control, ControlHover, ControlPressed,
        // D3: white "do it", tape-yellow "selected / active / measured", blue for neutral information only.
        Primary, OnPrimary, Ink, InkPressed, OnInk, Info,
    }

    /// Typography roles. Sizes are em heights in dmm (distance-independent millimetres: mm of em seen at 1 m, UX W0.6 /
    /// Meta's legibility guidance); UiText converts them for the distance a surface is read from. Nothing is drawn
    /// below the 16 dmm floor (UiTheme.EmFloorDmm).
    public enum TypeRole { Display, Heading, Title, Body, Label, Caption, Numeric }

    public enum Weight { Regular, Medium, Semibold }

    public enum Elevation { None, Subtle, Panel, Modal }

    public enum RadiusRole { Small, Medium, Large, Pill }

    /// The AirTools spatial design tokens (SPEC §9 "UI design system"). One asset, loaded from Resources; code that
    /// builds UI reads tokens from here instead of hard-coding colours, sizes, radii or durations.
    [CreateAssetMenu(menuName = "AirTools/UI Theme")]
    public class UiTheme : ScriptableObject
    {
        /// Colours are authored in sRGB (hex); the glass shader linearises vertex colours (the project is Linear), and
        /// TMP and material colour properties convert themselves. Retuned after that fix (UI research §3.1): smoked
        /// panels #1C1F27, text #EDEEF2; armed destructive fills #C62828 (5.6:1 with white).
        /// UX D3 (decided Sat 09-26, docs/ux/README.md §0, Fieldglass tokens in its Appendix A): **white = "do it"** —
        /// primary actions are #F5F6F8 with #0F1115 labels (17.5:1, the Horizon OS Quest Dark style); **tape-yellow ink =
        /// selected / active / measured** — #FFD23F with #17140A text (12.8:1); blue is kept for neutral information
        /// only (toasts, busy status). Caution is orange #FF9F43, 30° of OKLCH hue away from the ink (the old amber
        /// #FFB529 was 14° away), so "check" never reads as "selected". Contrast checks: ErgonomicsTests (A4, D3).
        [Serializable]
        public class Colors
        {
            public Color background = new Color(0.04f, 0.045f, 0.06f, 1f);
            public Color surface = new Color(0.10f, 0.11f, 0.14f, 1f);
            /// D3: window / menu body. Fieldglass "solid floor": α 0.95 (was 0.84), so secondary text stays ≥ 4.5:1
            /// even with a white wall or the sky horizon behind the window (3.1:1 at 0.84; ErgonomicsTests).
            /// Liquid glass (glass lane): the tool ring's dark neutral dimming layer, #14161B at 94 % (was #1C1F27 at 95 %) —
            /// a slightly darker tint behind body text pays for the glass's own light (the role's InteriorLift) and lets the
            /// bezel run clearer; secondary text 5.0:1 over a white wall with the lift (LiquidGlassTests).
            public Color glassRegular = new Color(0.078f, 0.086f, 0.106f, 0.94f);
            public Color glassClear = new Color(0.165f, 0.180f, 0.220f, 0.55f);
            /// Rows / cards on a window and the heads-up pills: #22252D at 95 % (was #262A33 at 96 %), still a step lighter
            /// than the window so a row reads as a row.
            public Color elevatedSolid = new Color(0.133f, 0.145f, 0.176f, 0.95f);
            public Color textPrimary = new Color(0.929f, 0.933f, 0.949f, 1.00f);
            public Color textSecondary = new Color(0.929f, 0.933f, 0.949f, 0.68f);
            public Color textDisabled = new Color(0.929f, 0.933f, 0.949f, 0.40f);
            // ---- D3 (UX decision, docs/ux/README.md Appendix A) ----
            /// Primary ("do it"): the one filled action per view. Hover lifts to pure white, press sinks to a cool grey.
            public Color primary = new Color(0.9608f, 0.9647f, 0.9725f, 1f);          // #F5F6F8
            public Color primaryHover = new Color(1f, 1f, 1f, 1f);                      // #FFFFFF
            public Color primaryPressed = new Color(0.8353f, 0.8510f, 0.8784f, 1f);   // #D5D9E0
            /// Label / icon on a primary fill (17.5:1; ≥ 13:1 in every state).
            public Color onPrimary = new Color(0.0588f, 0.0667f, 0.0824f, 1f);        // #0F1115
            /// Ink (tape yellow): selected toggles and chips, the active tool, "you are here", measured lines.
            public Color ink = new Color(1f, 0.8235f, 0.2471f, 1f);                     // #FFD23F
            public Color inkHover = new Color(1f, 0.8588f, 0.4000f, 1f);                // #FFDB66
            public Color inkPressed = new Color(0.9098f, 0.7255f, 0.1373f, 1f);       // #E8B923
            /// Text / icon on an ink fill (12.8:1; ≥ 9.9:1 pressed).
            public Color onInk = new Color(0.0902f, 0.0784f, 0.0392f, 1f);            // #17140A
            /// A selected row / card: the ink laid over the row colour at this alpha (see UiTheme.Wash).
            public Color inkWash = new Color(1f, 0.8235f, 0.2471f, 0.20f);
            /// Neutral information (toasts, busy status, the ask pin): the only blue left after D3.
            public Color info = new Color(0.4863f, 0.7686f, 1f, 1f);                    // #7CC4FF
            /// Label on the armed destructive fill (5.6:1).
            public Color onDanger = new Color(1f, 1f, 1f, 1f);
            /// Fill of an armed destructive button (danger stays the bright text / status red).
            public Color dangerFill = new Color(0.7765f, 0.1569f, 0.1569f, 1f);        // #C62828
            public Color danger = new Color(1.00f, 0.36f, 0.33f, 1f);
            public Color success = new Color(0.25f, 0.82f, 0.52f, 1f);
            /// D3: caution ("check") is orange, clear of the tape-yellow ink (was amber #FFB529).
            public Color warning = new Color(1f, 0.6235f, 0.2627f, 1f);                 // #FF9F43
            public Color separator = new Color(1f, 1f, 1f, 0.12f);
            /// Controls sit on glass as a light veil that brightens on hover and press. Liquid glass: the rim carries the
            /// state now (GlassLooks.Glow), so the veil steps less (was 8 / 14 / 22 %) and the label keeps its contrast —
            /// white on the veil ≥ 4.5:1 at rest and hover, ≥ 3:1 during the 100 ms press, white wall behind (LiquidGlassTests).
            public Color control = new Color(1f, 1f, 1f, 0.06f);
            public Color controlHover = new Color(1f, 1f, 1f, 0.07f);
            public Color controlPressed = new Color(1f, 1f, 1f, 0.11f);
            // ---- D3: tokens for colours that were hard-coded, and the retired accent ----
            /// A disabled control's veil (its label is textDisabled).
            public Color controlDisabled = new Color(1f, 1f, 1f, 0.05f);
            /// A window's grab bar at rest (hover: textSecondary; dragging: ink).
            public Color handle = new Color(1f, 1f, 1f, 0.35f);
            /// The bottom of a glassy icon glyph's vertical gradient (white at the top).
            public Color iconShade = new Color(0.80f, 0.87f, 0.97f, 1f);

            // Before D3 one blue "accent" meant both "do it" and "selected". Not serialized; kept only so code written
            // against it on parallel branches still compiles (with a warning). A fill that used it now draws ink.
            [Obsolete("UX D3: primary (actions), ink (selected / active) or info (neutral status)")] public Color accent => ink;
            [Obsolete("UX D3: primaryPressed or inkPressed")] public Color accentPressed => inkPressed;
            [Obsolete("UX D3: onPrimary, onInk or onDanger")] public Color onAccent => onInk;
        }

        [Serializable]
        public class Shape
        {
            /// Corner radii in metres (compact controls / cards / primary panels) — 12 / 18 / 26 at the 1 dmm = 0.5 mm
            /// design scale used for hand-distance UI.
            public float radiusSmall = 0.006f;
            public float radiusMedium = 0.009f;
            public float radiusLarge = 0.013f;
            /// Pill: radius = half the height (resolved per surface).
            public float rimWidth = 0.0009f;
            public float rimStrength = 0.22f;
            /// Legacy: the top-lit gradient every surface used before liquid glass; each role's gradient is in `glass` now.
            public float gradient = 0.035f;
        }

        /// Liquid glass for every surface (glass lane; docs/UI.md §3): the tool ring's look (AirTools/LiquidGlass) — a
        /// squircle bezel that bends the key light at the rim, a dimmer fill on the opposite edge, Fresnel and a
        /// studio-gradient reflection (a pre-blurred room), a thin inner stroke, a clearer edge over a solid floor behind
        /// text — drawn by AirTools/Glass from vertex data, one shared material per role, no grab pass or blur.
        /// Per role: bezel · bezel fraction · highlight (1 = the ring) · edge clearness · sheen · frost (the blur stand-in)
        /// · gradient. The body's own light (InteriorLift = gradient + frost/2 + sheen) is counted in the contrast checks.
        [Serializable]
        public class GlassTokens
        {
            /// Windows and menu panels (Settings, Find parts, notebook, Adjust, sellers, checkout, the spec card, Grok and
            /// coach cards) and floating glass pills (the wrist strip). Body text sits here: lift 0.011. No body glint on the
            /// big roles (its interior then costs what the flat glass did; the ripple still moves the rim highlight).
            public GlassLook window = new GlassLook(0.0065f, 0.25f, 0.8f, 0.45f, 0f, 0.006f, 0.008f);
            /// Rows and cards on a window (candidates, notebook rows, the receipt): a quieter bezel, nearly solid.
            public GlassLook card = new GlassLook(0.004f, 0.3f, 0.4f, 0.15f, 0f, 0.004f, 0.008f);
            /// Buttons, chips, toggles, segments, Model view cards: a glass capsule on the glass (the ring's Undo / Redo), with
            /// a faint body glint that moves as you do.
            public GlassLook control = new GlassLook(0.0055f, 0.45f, 1f, 0.35f, 0.012f, 0.004f, 0.012f);
            /// Heads-up pills (the status line, toasts, the job rail): near-opaque behind text, a light rim.
            public GlassLook hud = new GlassLook(0.0045f, 0.3f, 0.6f, 0.25f, 0f, 0.004f, 0.005f);
            /// Pills behind world annotations (scaled spaces: the fraction sets the bezel).
            public GlassLook label = new GlassLook(1000f, 0.3f, 0.5f, 0.2f, 0f, 0.004f, 0.02f);
            /// High contrast: every liquid role draws solid — the bezel shape and a faint rim, no clear edge, sheen or frost.
            public GlassLook highContrast = new GlassLook(0.004f, 0.25f, 0.3f, 0f, 0f, 0f, 0f);

            /// The pre-blurred room the rims reflect (a studio gradient): sky side / floor side. Shared with the ring.
            public Color envTop = new Color(0.92f, 0.96f, 1f, 1f);
            public Color envBottom = new Color(0.20f, 0.22f, 0.26f, 1f);
            /// Key-light highlight tightness (the ring's 20).
            public float specPower = 20f;
            /// The key light is world up tilted toward the eye by this much (ToolRing.UpdateGlass uses the same 0.55).
            public float keyLift = 0.55f;

            public GlassLook For(GlassRole role, bool highContrast = false)
            {
                if (role == GlassRole.Mark || role == GlassRole.Auto) return GlassLook.None;
                if (highContrast)
                {
                    var hc = this.highContrast;
                    if (role == GlassRole.Label) hc.bezel = 1000f;
                    return hc;
                }
                return role switch
                {
                    GlassRole.Window => window,
                    GlassRole.Card => card,
                    GlassRole.Control => control,
                    GlassRole.Hud => hud,
                    GlassRole.Label => label,
                    _ => GlassLook.None,
                };
            }
        }

        [Serializable]
        public class Spacing
        {
            /// The 4/8/12/16/24/32 scale, in metres at hand distance (1 unit = 1 mm).
            public float xxs = 0.004f, xs = 0.008f, s = 0.012f, m = 0.016f, l = 0.024f, xl = 0.032f;
        }

        [Serializable]
        public class Motion
        {
            public float fast = 0.10f;
            public float standard = 0.16f;
            public float modal = 0.22f;
            public float hoverScale = 1.02f;
            public float pressDepth = 0.003f;
            public float pressScale = 0.97f;
        }

        [Serializable]
        public class Typography
        {
            /// Inter Regular / Medium / SemiBold SDF (real weights, one atlas each).
            public TMP_FontAsset font, fontMedium, fontSemibold;
            /// Each weight's material (its own atlas).
            public Material regular, medium, semibold;
            /// World annotation text (measurements, callouts): Medium, drawn as an overlay so walls never cut it.
            public Material annotation;
            /// Em heights in dmm (UX W0.6): floor 16 (captions, metadata), labels 18, body 20, titles 22–26.
            public float display = 32f, heading = 26f, title = 22f, body = 20f, label = 18f, caption = 16f, numeric = 22f;
            /// Heads-up text (toast, reply card): drawn above every panel (overlay, queue 3210), never hidden.
            public Material hud;
            /// Medium with a soft dark halo, for labels that sit on liquid glass over a bright room (the ring).
            public Material halo;
        }

        /// One shared material per surface role (glass lane): Glass Regular = windows, Glass Clear = controls, Elevated
        /// Solid = cards / rows / marks, Glass HUD = heads-up, Glass Label = world labels, Glass Shadow = elevation. All are
        /// AirTools/Glass; the look per surface is vertex data (GlassMesh), so nothing is ever instanced.
        [Serializable]
        public class Materials
        {
            public Material glassRegular, glassClear, elevatedSolid, shadow;
            /// Heads-up glass (toast, reply card): overlay, no depth write, drawn after all UI panels (queue 3200).
            public Material hudGlass;
            /// Pill behind world annotations: same glass, overlay depth test, no depth write.
            public Material labelGlass;
            /// Liquid Glass (AirTools/LiquidGlass): the tool ring's lensing band, highlights and selection lens.
            public Material liquidGlass;
        }

        /// Phosphor Icons (MIT) as SDF fonts: Regular for idle icons, Fill for the selected one (see Icons).
        [Serializable]
        public class IconTokens
        {
            public TMP_FontAsset regular, fill;
            public Material regularMaterial, fillMaterial;
        }

        public Colors colors = new Colors();
        public Shape shape = new Shape();
        public Spacing spacing = new Spacing();
        public Motion motion = new Motion();
        public Typography type = new Typography();
        public Materials materials = new Materials();
        public IconTokens icons = new IconTokens();
        public GlassTokens glass = new GlassTokens();

        public const string ResourcePath = "AirToolsTheme";
        static UiTheme s_Current;

        /// The project theme (Resources/AirToolsTheme), or code defaults when it isn't built yet (tests).
        public static UiTheme Current
        {
            get
            {
                if (s_Current != null) return s_Current;
                s_Current = Resources.Load<UiTheme>(ResourcePath);
                if (s_Current == null) { s_Current = CreateInstance<UiTheme>(); s_Current.name = "UiTheme (defaults)"; }
                return s_Current;
            }
            set => s_Current = value;
        }

        public Color Color(ColorRole role) => role switch
        {
            ColorRole.Background => colors.background,
            ColorRole.Surface => colors.surface,
            ColorRole.GlassRegular => colors.glassRegular,
            ColorRole.GlassClear => colors.glassClear,
            ColorRole.ElevatedSolid => colors.elevatedSolid,
            ColorRole.TextPrimary => colors.textPrimary,
            ColorRole.TextSecondary => colors.textSecondary,
            ColorRole.TextDisabled => colors.textDisabled,
#pragma warning disable 618   // D3 aliases: every Accent tone in the code was a neutral status toast
            ColorRole.Accent => colors.info,
            ColorRole.AccentPressed => colors.inkPressed,
#pragma warning restore 618
            ColorRole.Danger => colors.danger,
            ColorRole.Success => colors.success,
            ColorRole.Warning => colors.warning,
            ColorRole.Separator => colors.separator,
            ColorRole.Control => colors.control,
            ColorRole.ControlHover => colors.controlHover,
            ColorRole.ControlPressed => colors.controlPressed,
            ColorRole.Primary => colors.primary,
            ColorRole.OnPrimary => colors.onPrimary,
            ColorRole.Ink => colors.ink,
            ColorRole.InkPressed => colors.inkPressed,
            ColorRole.OnInk => colors.onInk,
            ColorRole.Info => colors.info,
            _ => UnityEngine.Color.magenta,
        };

        /// `wash` laid over `surface` at the wash's alpha (sRGB, like the theme); keeps the surface's alpha. A selected
        /// row is Wash(elevatedSolid, inkWash). Pure (contrast tests).
        public static Color Wash(Color surface, Color wash) =>
            new Color(Mathf.Lerp(surface.r, wash.r, wash.a), Mathf.Lerp(surface.g, wash.g, wash.a), Mathf.Lerp(surface.b, wash.b, wash.a), surface.a);

        public Color TierColor(GlassTier tier) => tier switch
        {
            GlassTier.GlassRegular => colors.glassRegular,
            GlassTier.GlassClear => colors.glassClear,
            GlassTier.ElevatedSolid => colors.elevatedSolid,
            _ => new Color(0f, 0f, 0f, 0.35f),
        };

        public float Radius(RadiusRole role, float height) => role switch
        {
            RadiusRole.Small => shape.radiusSmall,
            RadiusRole.Medium => shape.radiusMedium,
            RadiusRole.Large => shape.radiusLarge,
            _ => height * 0.5f,
        };

        /// The smallest em any UI text may have at its read distance (dmm).
        public const float EmFloorDmm = 16f;
        /// Inter's line height over its em.
        public const float LineRatio = 1.21f;

        public float EmDmm(TypeRole role) => role switch
        {
            TypeRole.Display => type.display,
            TypeRole.Heading => type.heading,
            TypeRole.Title => type.title,
            TypeRole.Body => type.body,
            TypeRole.Label => type.label,
            TypeRole.Caption => type.caption,
            _ => type.numeric,
        };

        /// Line height (dmm) for layout: em × Inter's line ratio.
        public float LineHeightDmm(TypeRole role) => EmDmm(role) * LineRatio;

        /// Default weight per role: Semibold for titles, Medium for controls/labels/values, Regular for body.
        public static Weight DefaultWeight(TypeRole role) => role switch
        {
            TypeRole.Display or TypeRole.Heading or TypeRole.Title => Weight.Semibold,
            TypeRole.Label or TypeRole.Numeric => Weight.Medium,
            _ => Weight.Regular,
        };

        public TMP_FontAsset FontAsset(Weight w) => w switch
        {
            Weight.Semibold => type.fontSemibold != null ? type.fontSemibold : type.font,
            Weight.Medium => type.fontMedium != null ? type.fontMedium : type.font,
            _ => type.font,
        };

        public Material FontMaterial(Weight w) => w switch
        {
            Weight.Semibold => type.semibold != null ? type.semibold : type.regular,
            Weight.Medium => type.medium != null ? type.medium : type.regular,
            _ => type.regular,
        };

        /// The Liquid Glass look a surface of this role draws with under the current settings (high contrast: solid).
        public GlassLook Look(GlassRole role) => (glass ?? (glass = new GlassTokens())).For(role, UiSettings.HighContrast);

        public Material TierMaterial(GlassTier tier) => tier switch
        {
            GlassTier.GlassRegular => materials.glassRegular,
            GlassTier.GlassClear => materials.glassClear,
            GlassTier.ElevatedSolid => materials.elevatedSolid,
            _ => materials.shadow,
        };

        /// Elevation → (shadow drop in metres, shadow softness in metres, shadow alpha).
        public static (float drop, float soft, float alpha) ElevationShadow(Elevation e) => e switch
        {
            Elevation.Subtle => (0.0015f, 0.004f, 0.22f),
            Elevation.Panel => (0.003f, 0.010f, 0.30f),
            Elevation.Modal => (0.005f, 0.018f, 0.40f),
            _ => (0f, 0f, 0f),
        };
    }
}
