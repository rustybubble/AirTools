using System.Collections.Generic;
using System.IO;
using System.Linq;
using AirTools.Editor;
using AirTools.UI;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AirTools.Tests
{
    /// Glass lane: the tool ring's Liquid Glass on every UI surface (docs/UI.md §3). The pure half — roles, looks,
    /// contrast on the glass's own light, the button states, the shader source — runs in the offline runner too.
    public class LiquidGlassTests
    {
        // ---------------- roles ----------------

        [Test]
        public void RolesResolveFromTheSurface()
        {
            var panel = new Vector2(0.36f, 0.44f);
            Assert.AreEqual(GlassRole.Window, GlassLooks.Resolve(GlassRole.Auto, GlassTier.GlassRegular, false, false, false, panel), "UiBuild.Panel");
            Assert.AreEqual(GlassRole.Hud, GlassLooks.Resolve(GlassRole.Auto, GlassTier.ElevatedSolid, true, false, false, new Vector2(0.28f, 0.05f)), "the status line / toast pill");
            Assert.AreEqual(GlassRole.Label, GlassLooks.Resolve(GlassRole.Auto, GlassTier.GlassRegular, false, true, false, new Vector2(0.3f, 0.13f)), "a world label's plate");
            Assert.AreEqual(GlassRole.Control, GlassLooks.Resolve(GlassRole.Auto, GlassTier.ElevatedSolid, false, false, true, new Vector2(0.1f, 0.022f)), "a tinted chip pill");
            Assert.AreEqual(GlassRole.Card, GlassLooks.Resolve(GlassRole.Auto, GlassTier.ElevatedSolid, false, false, false, new Vector2(0.33f, 0.064f)), "a row");
            Assert.AreEqual(GlassRole.Window, GlassLooks.Resolve(GlassRole.Auto, GlassTier.GlassRegular, false, false, false, new Vector2(0.16f, 0.026f)), "the wrist strip");
            foreach (var mark in new[] { new Vector2(0.011f, 0.011f), new Vector2(0.02f, 0.0022f), new Vector2(0.0008f, 0.225f), new Vector2(0.07f, 0.0065f) })
            {
                Assert.AreEqual(GlassRole.Mark, GlassLooks.Resolve(GlassRole.Auto, GlassTier.ElevatedSolid, false, false, true, mark), $"{mark}: dot / bar / separator / grab bar");
                Assert.AreEqual(GlassRole.Mark, GlassLooks.Resolve(GlassRole.Auto, GlassTier.ElevatedSolid, true, false, true, mark), $"{mark}: a heads-up dot");
            }
            Assert.AreEqual(GlassRole.Mark, GlassLooks.Resolve(GlassRole.Auto, GlassTier.Shadow, false, false, false, panel));
            Assert.AreEqual(GlassRole.Control, GlassLooks.Resolve(GlassRole.Control, GlassTier.GlassClear, false, false, true, new Vector2(0.01f, 0.01f)), "an explicit role wins");
        }

        [Test]
        public void EveryLiquidRoleLooksLikeTheRingAndMarksStayFlat()
        {
            var g = new UiTheme.GlassTokens();
            foreach (var role in new[] { GlassRole.Window, GlassRole.Card, GlassRole.Control, GlassRole.Hud, GlassRole.Label })
            {
                var l = g.For(role);
                Assert.IsTrue(l.IsLiquid, $"{role}: a bezel and a highlight");
                Assert.That(l.highlight, Is.InRange(0.3f, 1f), $"{role}: highlight (1 = the ring)");
                Assert.That(l.edgeClear, Is.InRange(0f, 0.6f), $"{role}: the edge clears, the body stays a floor");
                Assert.That(l.frost, Is.InRange(0f, 0.012f), $"{role}: frost (the blur stand-in)");
                Assert.GreaterOrEqual(l.gradient, 0f);
            }
            Assert.IsFalse(g.For(GlassRole.Mark).IsLiquid, "marks are flat");
            Assert.IsFalse(g.For(GlassRole.Auto).IsLiquid);
            // Windows are where body text sits: the most translucent edge, no body glint (their interior costs what the
            // flat glass did); controls glint.
            Assert.AreEqual(0f, g.For(GlassRole.Window).sheen);
            Assert.AreEqual(0f, g.For(GlassRole.Card).sheen);
            Assert.AreEqual(0f, g.For(GlassRole.Hud).sheen);
            Assert.Greater(g.For(GlassRole.Control).sheen, 0f);
            Assert.GreaterOrEqual(g.For(GlassRole.Window).edgeClear, g.For(GlassRole.Card).edgeClear, "the window's edge is the clearest dark glass");
            // High contrast: solid — no clear edge, sheen or frost; the rim stays so a control still reads as one.
            foreach (var role in new[] { GlassRole.Window, GlassRole.Card, GlassRole.Control, GlassRole.Hud, GlassRole.Label })
            {
                var hc = g.For(role, highContrast: true);
                Assert.AreEqual(0f, hc.edgeClear, $"{role} HC"); Assert.AreEqual(0f, hc.sheen); Assert.AreEqual(0f, hc.frost); Assert.AreEqual(0f, hc.gradient);
                Assert.IsTrue(hc.IsLiquid, $"{role} HC keeps a rim");
            }
            Assert.IsFalse(g.For(GlassRole.Mark, highContrast: true).IsLiquid);
            // The ring and every panel share one room and one key light (UiAssetsBuilder sets both materials from these).
            Assert.AreEqual(20f, g.specPower);
            Assert.AreEqual(0.55f, g.keyLift, 1e-6f, "ToolRing.UpdateGlass tilts its key light toward the eye by 0.55 too");
        }

        [Test]
        public void TheBezelFitsTheSurfaceAndStaysClearOfContent()
        {
            var g = new UiTheme.GlassTokens();
            Assert.AreEqual(0.0065f, GlassLooks.Bezel(g.window, new Vector2(0.36f, 0.44f)), 1e-6f, "a window: the ring's 6.5 mm bezel");
            Assert.AreEqual(0.25f * 0.013f, GlassLooks.Bezel(g.window, new Vector2(0.16f, 0.026f)), 1e-6f, "the wrist strip: a quarter of its half height");
            Assert.AreEqual(0.0055f, GlassLooks.Bezel(g.control, new Vector2(0.066f, 0.028f)), 1e-6f, "a chip");
            Assert.AreEqual(0.3f * 0.065f, GlassLooks.Bezel(g.label, new Vector2(0.3f, 0.13f)), 1e-6f, "a label plate in its scaled space: the fraction");
            Assert.AreEqual(0f, GlassLooks.Bezel(GlassLook.None, new Vector2(0.3f, 0.3f)));
            // Window content sits ≥ 12 mm in from the panel's edge (DeclutterLayoutSceneTests): the clear bezel stays out of it.
            foreach (var role in new[] { GlassRole.Window, GlassRole.Card, GlassRole.Hud })
                Assert.LessOrEqual(GlassLooks.Bezel(g.For(role), new Vector2(0.4f, 0.4f)), 0.012f * 0.6f, $"{role}");
            // A chip's rim band keeps to its outer fifth top and bottom: its centred label sits in the clear middle.
            Assert.LessOrEqual(GlassLooks.Bezel(g.control, new Vector2(0.066f, 0.028f)), 0.028f * 0.2f + 1e-6f);
        }

        // ---------------- contrast on the glass's own light ----------------
        // WCAG 2 relative luminance, blended in linear space (the Linear project's sRGB eye buffer), over a white wall, mid
        // grey and black. Each surface adds its role's InteriorLift (gradient top + frost/2 + sheen: linear, every channel).

        static double Lin(float v) => v <= 0.04045 ? v / 12.92 : System.Math.Pow((v + 0.055) / 1.055, 2.4);
        static double Lum(Color c) => 0.2126 * Lin(c.r) + 0.7152 * Lin(c.g) + 0.0722 * Lin(c.b);
        static double Ratio(double a, double b) => (System.Math.Max(a, b) + 0.05) / (System.Math.Min(a, b) + 0.05);
        static double Over(Color c, double under, double lift) => c.a * System.Math.Min(1.0, Lum(c) + lift) + (1.0 - c.a) * under;
        static readonly double[] s_Backdrops = { 1.0, 0.18, 0.0 };

        static double OnStack(Color text, params (Color c, GlassLook look)[] surfaces)
        {
            double worst = double.MaxValue;
            foreach (double b in s_Backdrops)
            {
                double under = b;
                foreach (var (c, look) in surfaces) under = Over(c, under, GlassLooks.InteriorLift(look));
                worst = System.Math.Min(worst, Ratio(Over(text, under, 0.0), under));
            }
            return worst;
        }

        /// A light, opaque fill (the white primary, ink) at its darkest: the shader's top light turned multiplier at the
        /// bottom (1 − 3 × gradient) and the lensing bevel's bottom edge (−10 %), on linear luminance.
        static double DarkestFill(Color fill, GlassLook look) => Lum(fill) * (1.0 - 3.0 * look.gradient) * (1.0 - 0.10);

        public static List<string> ContrastProblems(UiTheme.Colors c, UiTheme.GlassTokens g)
        {
            var bad = new List<string>();
            void Need(string what, double ratio, double min) { if (ratio < min - 1e-6) bad.Add($"{what}: {ratio:0.00}:1 < {min}:1"); }
            var window = (c.glassRegular, g.window);
            var row = (c.elevatedSolid, g.card);
            var selected = (UiTheme.Wash(c.elevatedSolid, c.inkWash), g.card);
            // The heads-up pills draw the ElevatedSolid tint at α ≥ 0.94 (StatusLine / UiToast).
            var hud = (new Color(c.elevatedSolid.r, c.elevatedSolid.g, c.elevatedSolid.b, Mathf.Max(c.elevatedSolid.a, 0.94f)), g.hud);
            Need("textPrimary on the window", OnStack(c.textPrimary, window), 4.5);
            Need("textSecondary on the window", OnStack(c.textSecondary, window), 4.5);
            Need("ink text on the window", OnStack(c.ink, window), 4.5);
            Need("textSecondary on a row", OnStack(c.textSecondary, window, row), 4.5);
            Need("textSecondary on a selected row", OnStack(c.textSecondary, window, selected), 4.5);
            Need("textPrimary on a selected row", OnStack(c.textPrimary, window, selected), 4.5);
            Need("textPrimary on the heads-up pill", OnStack(c.textPrimary, hud), 4.5);
            Need("textSecondary on the heads-up pill (coach line)", OnStack(c.textSecondary, hud), 4.5);
            // Button labels on their veil on the window: 4.5:1 at rest and hover; the 100 ms press ≥ 3:1.
            Need("a button label at rest", OnStack(c.textPrimary, window, (c.control, g.control)), 4.5);
            Need("a button label on hover", OnStack(c.textPrimary, window, (c.controlHover, g.control)), 4.5);
            Need("a button label pressed", OnStack(c.textPrimary, window, (c.controlPressed, g.control)), 3.0);
            // D3's light fills stay crisp at their darkest (≥ 7:1).
            foreach (var (name, fill) in new[] { ("primary", c.primary), ("primaryHover", c.primaryHover), ("primaryPressed", c.primaryPressed) })
                Need($"onPrimary on {name} (glass bevel)", Ratio(Lum(c.onPrimary), DarkestFill(fill, g.control)), 7.0);
            foreach (var (name, fill) in new[] { ("ink", c.ink), ("inkHover", c.inkHover), ("inkPressed", c.inkPressed) })
                Need($"onInk on {name} (glass bevel)", Ratio(Lum(c.onInk), DarkestFill(fill, g.control)), 7.0);
            return bad;
        }

        [Test]
        public void TextKeepsItsContrastOnTheLiquidGlass()
        {
            var bad = ContrastProblems(new UiTheme.Colors(), new UiTheme.GlassTokens());
            Assert.IsEmpty(bad, string.Join("\n", bad));
        }

        /// Why the tokens moved: the old smoked window (#1C1F27 95 %) with the old 0.035 top light (still the legacy
        /// shape.gradient) drops secondary text under 4.5:1 over a white wall, and the old 22 % pressed veil left a label
        /// at 2.5:1. The darker tint behind body text and the gentler veils fix both.
        [Test]
        public void TheOldTokensWouldFailOnTheGlassLight()
        {
            var old = new UiTheme.Colors
            {
                glassRegular = new Color(0.110f, 0.122f, 0.153f, 0.95f),
                controlPressed = new Color(1f, 1f, 1f, 0.22f),
            };
            var g = new UiTheme.GlassTokens();
            g.window.gradient = new UiTheme.Shape().gradient;
            g.window.frost = 0.012f;
            var bad = ContrastProblems(old, g);
            Assert.IsTrue(bad.Any(b => b.StartsWith("textSecondary on the window")), string.Join("\n", bad));
            Assert.IsTrue(bad.Any(b => b.StartsWith("a button label pressed")), string.Join("\n", bad));
            Assert.Less(new UiTheme.Colors().glassRegular.r, old.glassRegular.r, "a darker tint behind body text");
        }

        // ---------------- button states ----------------

        [Test]
        public void TheRimCarriesHoverPressAndSelected()
        {
            Assert.AreEqual(0f, GlassLooks.Glow(true, false, false, false), "rest");
            Assert.AreEqual(0.6f, GlassLooks.Glow(true, true, false, false), "hover: a brighter rim");
            Assert.AreEqual(1f, GlassLooks.Glow(true, true, true, false), "press");
            Assert.AreEqual(1f, GlassLooks.Glow(true, false, true, true), "press wins over selected");
            Assert.AreEqual(0.3f, GlassLooks.Glow(true, false, false, true), "selected keeps a faint glow");
            Assert.AreEqual(0.6f, GlassLooks.Glow(true, true, false, true), "hovering a selected chip");
            Assert.AreEqual(0f, GlassLooks.Glow(false, true, true, true), "disabled: nothing lights");
            // The veils step less than before (8 / 14 / 22 %): the rim says hover, the label keeps its contrast.
            var c = new UiTheme.Colors();
            Assert.Less(c.control.a, c.controlHover.a);
            Assert.Less(c.controlHover.a, c.controlPressed.a);
            Assert.LessOrEqual(c.controlPressed.a, 0.12f);
        }

        // ---------------- the shader source ----------------

        const string ShaderDir = "Assets/AirTools/Runtime/Shaders";

        static string Source(string file)
        {
            string path = Path.Combine(ShaderDir, file);
            Assert.IsTrue(File.Exists(path), $"{path} (run from the project root)");
            return File.ReadAllText(path);
        }

        /// CLAUDE.md: custom shaders need the stereo-instancing macros or they're invisible in XR (single-pass instanced).
        [TestCase("Glass.shader")]
        [TestCase("LiquidGlass.shader")]
        public void UiShadersCarryTheStereoInstancingMacros(string file)
        {
            string src = Source(file);
            foreach (var needle in new[]
            {
                "#pragma multi_compile_instancing", "UNITY_VERTEX_INPUT_INSTANCE_ID", "UNITY_VERTEX_OUTPUT_STEREO",
                "UNITY_SETUP_INSTANCE_ID(input)", "UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o)", "UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i)",
                "\"RenderPipeline\" = \"UniversalPipeline\"", "CBUFFER_START(UnityPerMaterial)",
            })
                StringAssert.Contains(needle, src, $"{file}: {needle}");
        }

        /// Quest-cheap by construction: no grab pass, no opaque / scene colour texture, no blur taps, no texture at all;
        /// the Lite keyword is the one GlassQuality switches; the vertex layout is GlassMesh's (UV0–UV4 + colour).
        [Test]
        public void TheGlassShaderSamplesNothingAndHasTheLiteSwitch()
        {
            string src = Source("Glass.shader");
            foreach (var banned in new[] { "GrabPass", "_CameraOpaqueTexture", "_GrabTexture", "SampleSceneColor", "DeclareOpaqueTexture",
                                           "SAMPLE_TEXTURE", "tex2D", "TEXTURE2D(" })
                StringAssert.DoesNotContain(banned, src, $"Glass.shader must not sample the scene or a texture: {banned}");
            StringAssert.Contains($"#pragma multi_compile _ {GlassQuality.LiteKeyword}", src);
            StringAssert.Contains($"#if !defined({GlassQuality.LiteKeyword})", src);
            foreach (var channel in new[] { "TEXCOORD0", "TEXCOORD1", "TEXCOORD2", "TEXCOORD3", "TEXCOORD4", "COLOR" })
                StringAssert.Contains($": {channel};", src, $"reads {channel} (GlassMesh's layout)");
            foreach (var prop in new[] { "_EnvTop", "_EnvBottom", "_SpecPower", "_KeyLift" })
                StringAssert.Contains(prop, src, $"the shared lighting {prop} (UiAssetsBuilder sets it from UiTheme.glass)");
            // The ring's shader stays the reference and samples nothing either.
            string ring = Source("LiquidGlass.shader");
            foreach (var banned in new[] { "GrabPass", "_CameraOpaqueTexture", "SampleSceneColor" })
                StringAssert.DoesNotContain(banned, ring);
        }

        [Test]
        public void TheLiteSwitchReadsTheLaunchExtra()
        {
            Assert.IsTrue(GlassQuality.ParseLite("lite"));
            Assert.IsTrue(GlassQuality.ParseLite(" Flat "));
            Assert.IsTrue(GlassQuality.ParseLite("off"));
            Assert.IsFalse(GlassQuality.ParseLite("liquid"));
            Assert.IsFalse(GlassQuality.ParseLite("on"));
            Assert.IsFalse(GlassQuality.ParseLite(null));
            Assert.AreEqual("AIRTOOLS_GLASS_LITE", GlassQuality.LiteKeyword);
        }

        [Test]
        public void PicturesAreContentNotBackgrounds()
        {
            foreach (var n in new[] { "Thumb", "EvidenceThumb", "Photo", "Image", "Qr" }) Assert.IsTrue(GlassCensus.IsPicture(n), n);
            foreach (var n in new[] { "Panel", "Backdrop", "Cube", "Quad", "", null }) Assert.IsFalse(GlassCensus.IsPicture(n), n ?? "null");
        }
    }

    /// Glass lane, Unity only (the Editor gate): what UiBuild makes and what Wire built into Main.unity.
    public class LiquidGlassSceneTests
    {
        readonly List<GameObject> m_Made = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in m_Made) if (go != null) Object.DestroyImmediate(go);
            m_Made.Clear();
            UiBuild.Distance = UiText.HandDistance;
            UiSettings.HighContrast = false;
        }

        static Vector4 Uv(Mesh m, int channel)
        {
            var list = new List<Vector4>();
            m.GetUVs(channel, list);
            Assert.AreEqual(4, list.Count, $"UV{channel}");
            return list[0];
        }

        [Test]
        public void UiBuildPanelsAndButtonsAreLiquidGlass()
        {
            var theme = UiTheme.Current;
            var root = new GameObject("glass-root");
            m_Made.Add(root);
            var panel = UiBuild.Panel(root.transform, "Panel", new Vector2(0.36f, 0.3f));
            Assert.AreEqual(GlassRole.Window, panel.EffectiveRole);
            Assert.AreEqual(theme.materials.glassRegular, panel.GetComponent<MeshRenderer>().sharedMaterial, "one shared material per role");
            var mesh = panel.GetComponent<MeshFilter>().sharedMesh;
            var p3 = Uv(mesh, 3);
            Assert.AreEqual(GlassLooks.Bezel(theme.glass.window, panel.size), p3.x, 1e-6f, "bezel in vertex data");
            Assert.AreEqual(theme.glass.window.highlight, p3.y, 1e-6f);
            Assert.AreEqual(theme.glass.window.edgeClear, p3.z, 1e-6f);
            Assert.AreEqual(theme.glass.window.frost, Uv(mesh, 4).y, 1e-6f);
            Assert.AreEqual(theme.glass.window.gradient, Uv(mesh, 2).y, 1e-6f);

            // A heads-up pill made from a panel (the status line, toasts) picks up the heads-up look by itself.
            var hud = UiBuild.Panel(root.transform, "Pill", new Vector2(0.28f, 0.05f), elevation: Elevation.None, radius: RadiusRole.Pill);
            hud.tier = GlassTier.ElevatedSolid; hud.hud = true; hud.Rebuild();
            Assert.AreEqual(GlassRole.Hud, hud.EffectiveRole);
            Assert.AreEqual(theme.materials.hudGlass, hud.GetComponent<MeshRenderer>().sharedMaterial);

            foreach (ButtonStyle style in System.Enum.GetValues(typeof(ButtonStyle)))
            {
                var b = UiBuild.Button(root.transform, $"B{style}", "Brickmould Nail Fin Frame", new Vector2(0.066f, 0.028f), style, null,
                    role: TypeRole.Caption, radius: RadiusRole.Pill);
                Assert.AreEqual(GlassRole.Control, b.surface.EffectiveRole, $"{style}");
                Assert.AreEqual(theme.materials.glassClear, b.surface.GetComponent<MeshRenderer>().sharedMaterial, $"{style}: shared, never instanced");
                Assert.AreEqual(GlassRole.Mark, b.indicator.EffectiveRole, $"{style}: the selected bar is a flat mark");
                var m = b.surface.GetComponent<MeshFilter>().sharedMesh;
                Assert.AreEqual(theme.glass.control.highlight, Uv(m, 3).y, 1e-6f, $"{style}: the control look");
                // States on the glass: the rim glows, a press turns the bezel concave.
                b.surface.SetState(0.6f, 0f);
                Assert.AreEqual(0.6f, Uv(m, 2).w, 1e-6f, "hover glow");
                b.surface.SetState(1f, 1f);
                Assert.AreEqual(1f, Uv(m, 2).w, 1e-6f);
                Assert.AreEqual(1f, Uv(m, 3).w, 1e-6f, "press");
                Assert.AreEqual(theme.glass.control.highlight, Uv(m, 3).y, 1e-6f, "the look survives a state write");
                b.surface.SetState(0f, 0f);
                // The label fits its chip (the gate's "Brickmould Nail Fin Frame"): auto size down to the caption floor, two
                // lines, then an ellipsis — never past the chip.
                var t = b.label;
                Assert.IsTrue(t.enableAutoSizing, $"{style}: auto size");
                Assert.AreEqual(TextOverflowModes.Ellipsis, t.overflowMode);
                Assert.AreEqual(TextWrappingModes.Normal, t.textWrappingMode);
                Assert.GreaterOrEqual(t.fontSizeMin, UiText.FontSizeForEm(UiText.EmMetres(TypeRole.Caption, UiBuild.Distance)) - 1e-4f, "never below the floor");
                t.ForceMeshUpdate();
                Assert.LessOrEqual(t.textBounds.size.x, 0.066f - 2f * UiBuild.LabelInsetX + 1e-4f, $"{style}: \"{t.text}\" fits the chip's width");
                Assert.LessOrEqual(t.textInfo.lineCount, 2, $"{style}: at most two lines");
            }

            // High contrast: the same surfaces draw solid (no clear edge).
            UiSettings.HighContrast = true;
            panel.Rebuild();
            Assert.AreEqual(theme.materials.elevatedSolid, panel.GetComponent<MeshRenderer>().sharedMaterial);
            Assert.AreEqual(0f, Uv(panel.GetComponent<MeshFilter>().sharedMesh, 3).z, 1e-6f, "HC: no clear edge");
        }

        [Test]
        public void ThemeAssetCarriesTheGlassTokensAndLighting()
        {
            var theme = Resources.Load<UiTheme>(UiTheme.ResourcePath);
            Assert.IsNotNull(theme, "AirTools ▸ Build UI Assets");
            Assert.IsNotNull(theme.glass, "Build UI Assets wrote UiTheme.glass");
            var code = new UiTheme.GlassTokens();
            Assert.AreEqual(code.window.bezel, theme.glass.window.bezel, 1e-6f, "built theme is current (Build UI Assets)");
            Assert.AreEqual(code.control.highlight, theme.glass.control.highlight, 1e-6f, "built theme is current (Build UI Assets)");
            Assert.AreEqual(new UiTheme.Colors().glassRegular.r, theme.colors.glassRegular.r, 1e-4f, "the darker window tint (Build UI Assets)");
            var bad = LiquidGlassTests.ContrastProblems(theme.colors, theme.glass);
            Assert.IsEmpty(bad, string.Join("\n", bad));
            foreach (var m in new[] { theme.materials.glassRegular, theme.materials.glassClear, theme.materials.elevatedSolid, theme.materials.shadow,
                                      theme.materials.hudGlass, theme.materials.labelGlass })
            {
                Assert.IsNotNull(m);
                Assert.AreEqual("AirTools/Glass", m.shader.name, m.name);
                Assert.IsTrue(m.enableInstancing, $"{m.name}: instancing (single-pass instanced)");
                Assert.AreEqual(code.specPower, m.GetFloat("_SpecPower"), 1e-4f, $"{m.name}: the shared key light");
                Assert.AreEqual(code.keyLift, m.GetFloat("_KeyLift"), 1e-4f, m.name);
            }
            Assert.AreEqual(code.specPower, theme.materials.liquidGlass.GetFloat("_SpecPower"), 1e-4f, "the ring shares the lighting");
        }

        /// Every renderer in the Wire-built UI is glass with its role's shared material, text, the ring, or a picture — no
        /// stray opaque backgrounds and no per-instance material copies. Every button draws as a Control, every window panel
        /// as a Window (or a heads-up pill as Hud).
        [Test]
        public void TheWireBuiltSceneHasNoStrayBackgrounds()
        {
            var scene = EditorSceneManager.OpenScene(MainSceneBuilder.ScenePath, OpenSceneMode.Additive);
            try
            {
                var theme = UiTheme.Current;
                var roots = scene.GetRootGameObjects();
                var report = GlassCensus.Take(roots, theme);
                Debug.Log($"[AirTools] glass census: {report.Line()}");
                Assert.Greater(report.Roots, 10, "walked the UI (windows, pills, the palm menu, Model view)");
                Assert.Greater(report.Liquid, 80, "liquid surfaces");
                Assert.IsEmpty(report.Stray, "stray backgrounds:\n" + string.Join("\n", report.Stray));
                Assert.IsEmpty(report.WrongMaterial, "surfaces off their role's material:\n" + string.Join("\n", report.WrongMaterial));

                var offRole = new List<string>();
                foreach (var b in roots.SelectMany(r => r.GetComponentsInChildren<GlassButton>(true)))
                    if (b.surface != null && b.surface.EffectiveRole != GlassRole.Control) offRole.Add($"button {b.name}: {b.surface.EffectiveRole}");
                foreach (var w in roots.SelectMany(r => r.GetComponentsInChildren<FloatingWindow>(true)))
                {
                    var p = FloatingWindow.FindPanel(w.transform);
                    if (p != null && p.EffectiveRole != GlassRole.Window && p.EffectiveRole != GlassRole.Hud) offRole.Add($"window {w.name}: {p.EffectiveRole}");
                }
                Assert.IsEmpty(offRole, string.Join("\n", offRole));
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
