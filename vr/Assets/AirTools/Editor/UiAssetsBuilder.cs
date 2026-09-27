using System.IO;
using AirTools.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace AirTools.Editor
{
    /// AirTools ▸ Build UI Assets: the design system's shared assets.
    ///   Inter Regular / Medium / SemiBold SDF (one static 1024² atlas each, Latin + the app's symbols,
    ///   LiberationSans fallback) from the real Inter 4.1 font files, with their Mobile SDF materials
    ///   Glass Regular / Clear / Elevated Solid / Shadow / HUD / Label materials (AirTools/Glass): one shared material per
    ///   surface role, lit by the same Liquid Glass tokens as the ring's Liquid Glass material (UiTheme.glass)
    ///   Resources/AirToolsTheme.asset (tokens + references to the above)
    public static class UiAssetsBuilder
    {
        const string UiFolder = "Assets/AirTools/UI";
        const string FontsFolder = UiFolder + "/Fonts";
        const string MaterialsFolder = UiFolder + "/Materials";
        public const string ThemePath = "Assets/AirTools/Resources/" + UiTheme.ResourcePath + ".asset";

        /// Everything the app prints: Latin, punctuation, units, fractions, arrows, status marks.
        public const string Charset =
            " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~" +
            "×÷·–—…•′″°²³±≈≤≥€£¢¥©®™‘’“”«»¼½¾⅛⅜⅝⅞←↑→↓↔✓✗⚠µÀÁÂÄÇÈÉÊËÍÎÏÑÓÔÖÚÛÜàáâäçèéêëíîïñóôöúûüß" +
            "\u2212" +   // real minus (Units, UX W0.9 M12)
            "⌄";         // gaze-catalog: the catalog's focus chip ("Looking at: the roof ⌄"; Inter has U+2304, not ▾)

        [MenuItem("AirTools/Build UI Assets")]
        public static void BuildMenu() => Build();

        public static UiTheme Build()
        {
            Directory.CreateDirectory(MaterialsFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(ThemePath));
            var fontAsset = BuildFont("Inter-Regular", out string missing);
            var fontMedium = BuildFont("Inter-Medium", out _);
            var fontSemibold = BuildFont("Inter-SemiBold", out _);
            var theme = AssetDatabase.LoadAssetAtPath<UiTheme>(ThemePath);
            if (theme == null)
            {
                theme = ScriptableObject.CreateInstance<UiTheme>();
                AssetDatabase.CreateAsset(theme, ThemePath);
            }
            // Tokens live in code (UiTheme defaults); the asset carries them plus the asset references.
            theme.colors = new UiTheme.Colors();
            theme.shape = new UiTheme.Shape();
            theme.spacing = new UiTheme.Spacing();
            theme.motion = new UiTheme.Motion();
            theme.glass = new UiTheme.GlassTokens();   // glass lane: the Liquid Glass look per role
            var sizes = new UiTheme.Typography();
            theme.type.display = sizes.display; theme.type.heading = sizes.heading; theme.type.title = sizes.title;
            theme.type.body = sizes.body; theme.type.label = sizes.label; theme.type.caption = sizes.caption; theme.type.numeric = sizes.numeric;
            theme.type.font = fontAsset;
            theme.type.fontMedium = fontMedium;
            theme.type.fontSemibold = fontSemibold;
            // Render order: world annotations (overlay pills 3040, overlay text 3045) → UI shadows 3090 → UI glass
            // 3100 (writes depth, so panels cover annotations behind them) → UI text 3110.
            theme.type.regular = FontMaterial("Inter Regular", fontAsset, 0f, "TextMeshPro/Mobile/Distance Field", 3110);
            theme.type.medium = FontMaterial("Inter Medium", fontMedium, 0f, "TextMeshPro/Mobile/Distance Field", 3110);
            theme.type.semibold = FontMaterial("Inter Semibold", fontSemibold, 0f, "TextMeshPro/Mobile/Distance Field", 3110);
            theme.type.annotation = FontMaterial("Inter Label", fontMedium, 0f, "TextMeshPro/Mobile/Distance Field Overlay", 3045);
            // UX W0.8: the toast / reply card draw above every panel (overlay glass 3200, overlay text 3210).
            theme.type.hud = FontMaterial("Inter HUD", fontMedium, 0f, "TextMeshPro/Mobile/Distance Field Overlay", 3210);
            // UX W0.6: ring labels on liquid glass get a soft dark halo so they read over a bright room.
            theme.type.halo = HaloMaterial("Inter Medium Halo", fontMedium);
            // One shared material per role (glass lane): the Liquid Glass look itself (bezel, highlight, clear edge, sheen,
            // frost, gradient) is per-surface vertex data from UiTheme.glass; the materials carry the pipeline state and
            // the shared lighting (the room reflection, the key light), the same values the ring's material gets.
            theme.materials.glassRegular = GlassMaterial("Glass Regular", theme.glass, zwrite: true, queue: 3100);
            theme.materials.glassClear = GlassMaterial("Glass Clear", theme.glass, zwrite: true, queue: 3100);
            theme.materials.elevatedSolid = GlassMaterial("Elevated Solid", theme.glass, zwrite: true, queue: 3100);
            theme.materials.shadow = GlassMaterial("Glass Shadow", theme.glass, zwrite: false, queue: 3090);
            theme.materials.labelGlass = GlassMaterial("Glass Label", theme.glass, zwrite: false, queue: 3040, overlay: true);
            theme.materials.hudGlass = GlassMaterial("Glass HUD", theme.glass, zwrite: false, queue: 3200, overlay: true);
            theme.materials.liquidGlass = LiquidGlassMaterial("Liquid Glass", theme.glass, queue: 3104);
            // Icons: Phosphor Regular / Fill SDF (static atlases of Icons.All), drawn just above the liquid glass with a
            // soft dark underlay so they read as glyphs set into the glass.
            var iconRegular = BuildIconFont("Phosphor-Regular");
            var iconFill = BuildIconFont("Phosphor-Fill");
            theme.icons.regular = iconRegular;
            theme.icons.fill = iconFill;
            theme.icons.regularMaterial = IconMaterial("Icon Regular", iconRegular);
            theme.icons.fillMaterial = IconMaterial("Icon Fill", iconFill);
            EditorUtility.SetDirty(theme);
            AssetDatabase.SaveAssets();
            UiTheme.Current = theme;
            Debug.Log($"[AirTools] UI assets built: font glyphs={fontAsset.characterTable.Count} missing='{missing}'");
            return theme;
        }

        static TMP_FontAsset BuildFont(string file, out string missing)
        {
            string assetPath = $"{FontsFolder}/{file} SDF.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
            if (existing != null)
            {
                missing = Missing(existing);
                if (missing.Length > 0) missing = AddGlyphs(existing, missing);
                // Re-upload the atlas: the GPU copy can go stale in a long Editor session (glyphs drawn as blocks).
                if (existing.atlasTexture != null && existing.atlasTexture.isReadable) existing.atlasTexture.Apply(false, false);
                return existing;
            }
            var font = AssetDatabase.LoadAssetAtPath<Font>($"{FontsFolder}/{file}.ttf");
            var fa = TMP_FontAsset.CreateFontAsset(font, 72, 7, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, false);
            fa.name = $"{file} SDF";
            fa.TryAddCharacters(Charset, out _);
            AssetDatabase.CreateAsset(fa, assetPath);
            fa.atlasTexture.name = $"{file} SDF Atlas";
            AssetDatabase.AddObjectToAsset(fa.atlasTexture, fa);
            fa.material.name = $"{file} SDF Material";
            AssetDatabase.AddObjectToAsset(fa.material, fa);
            // Freeze the atlas (no runtime glyph generation); anything unexpected falls back to LiberationSans.
            fa.atlasPopulationMode = AtlasPopulationMode.Static;
            var fallback = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            if (fallback != null) fa.fallbackFontAssetTable = new System.Collections.Generic.List<TMP_FontAsset> { fallback };
            EditorUtility.SetDirty(fa);
            AssetDatabase.SaveAssets();
            missing = Missing(fa);
            return fa;
        }

        /// New Charset glyphs into an existing static atlas (briefly dynamic, then frozen again). Returns what's still missing.
        static string AddGlyphs(TMP_FontAsset fa, string chars)
        {
            try
            {
                fa.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                fa.TryAddCharacters(chars, out string still);
                fa.atlasPopulationMode = AtlasPopulationMode.Static;
                EditorUtility.SetDirty(fa);
                AssetDatabase.SaveAssets();
                Debug.Log($"[AirTools] {fa.name}: added glyphs '{chars}'{(string.IsNullOrEmpty(still) ? "" : $", still missing '{still}'")}");
                return still ?? "";
            }
            catch (System.Exception ex)
            {
                fa.atlasPopulationMode = AtlasPopulationMode.Static;
                Debug.LogWarning($"[AirTools] {fa.name}: couldn't add '{chars}' ({ex.Message}); they render from the fallback font");
                return chars;
            }
        }

        /// An icon font's SDF atlas: only the codepoints in Icons.All (they live in the Private Use Area).
        static TMP_FontAsset BuildIconFont(string file)
        {
            string assetPath = $"{FontsFolder}/{file} SDF.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
            if (existing != null && ContainsAll(existing, Icons.All))
            {
                if (existing.atlasTexture != null && existing.atlasTexture.isReadable) existing.atlasTexture.Apply(false, false);
                return existing;
            }
            if (existing != null) AssetDatabase.DeleteAsset(assetPath);
            var font = AssetDatabase.LoadAssetAtPath<Font>($"{FontsFolder}/{file}.ttf");
            if (font == null) { Debug.LogWarning($"[AirTools] {file}.ttf missing: icons fall back to nothing"); return null; }
            var fa = TMP_FontAsset.CreateFontAsset(font, 96, 9, GlyphRenderMode.SDFAA, 512, 512, AtlasPopulationMode.Dynamic, false);
            fa.name = $"{file} SDF";
            fa.TryAddCharacters(Icons.All, out string missing);
            if (!string.IsNullOrEmpty(missing)) Debug.LogWarning($"[AirTools] {file}: missing icon codepoints {missing.Length}");
            AssetDatabase.CreateAsset(fa, assetPath);
            fa.atlasTexture.name = $"{file} SDF Atlas";
            AssetDatabase.AddObjectToAsset(fa.atlasTexture, fa);
            fa.material.name = $"{file} SDF Material";
            AssetDatabase.AddObjectToAsset(fa.material, fa);
            fa.atlasPopulationMode = AtlasPopulationMode.Static;
            EditorUtility.SetDirty(fa);
            AssetDatabase.SaveAssets();
            return fa;
        }

        static bool ContainsAll(TMP_FontAsset fa, string chars)
        {
            foreach (char ch in chars) if (!fa.characterLookupTable.ContainsKey(ch)) return false;
            return true;
        }

        /// Text with a soft dark halo (underlay): legible on liquid glass over a bright room.
        static Material HaloMaterial(string name, TMP_FontAsset fa)
        {
            if (fa == null) return null;
            var mat = FontMaterial(name, fa, 0.1f, "TextMeshPro/Mobile/Distance Field", 3110);
            mat.EnableKeyword("UNDERLAY_ON");
            mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.6f));
            mat.SetFloat("_UnderlayOffsetX", 0f); mat.SetFloat("_UnderlayOffsetY", -0.1f);
            mat.SetFloat("_UnderlayDilate", 0.35f); mat.SetFloat("_UnderlaySoftness", 0.6f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// Icon glyph material: the Mobile SDF shader with a soft dark underlay (depth against the glass).
        static Material IconMaterial(string name, TMP_FontAsset fa)
        {
            if (fa == null) return null;
            var mat = FontMaterial(name, fa, 0.05f, "TextMeshPro/Mobile/Distance Field", 3108);
            mat.EnableKeyword("UNDERLAY_ON");
            mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.45f));
            mat.SetFloat("_UnderlayOffsetX", 0.12f); mat.SetFloat("_UnderlayOffsetY", -0.22f);
            mat.SetFloat("_UnderlayDilate", 0.15f); mat.SetFloat("_UnderlaySoftness", 0.55f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material LiquidGlassMaterial(string name, UiTheme.GlassTokens glass, int queue)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            var shader = Shader.Find("AirTools/LiquidGlass");
            if (shader == null) { Debug.LogWarning("[AirTools] AirTools/LiquidGlass shader missing"); return null; }
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(shader) { name = name }; AssetDatabase.CreateAsset(mat, path); }
            mat.shader = shader;
            // The ring and every panel reflect the same room and share the key light's tightness (glass lane).
            mat.SetColor("_EnvTop", glass.envTop);
            mat.SetColor("_EnvBottom", glass.envBottom);
            mat.SetFloat("_SpecPower", glass.specPower);
            mat.enableInstancing = true;
            mat.renderQueue = queue;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static string Missing(TMP_FontAsset fa)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in Charset) if (!fa.characterLookupTable.ContainsKey(ch)) sb.Append(ch);
            return sb.ToString();
        }

        static Material FontMaterial(string name, TMP_FontAsset fa, float dilate, string shaderName, int queue)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            var shader = Shader.Find(shaderName);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(fa.material) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = shader;
            mat.SetTexture("_MainTex", fa.atlasTexture);
            mat.SetFloat("_TextureWidth", fa.atlasWidth);
            mat.SetFloat("_TextureHeight", fa.atlasHeight);
            mat.SetFloat("_GradientScale", fa.atlasPadding + 1);
            mat.SetFloat("_FaceDilate", dilate);
            mat.SetFloat("_OutlineWidth", 0f);
            mat.SetFloat("_OutlineSoftness", 0f);
            mat.SetFloat("_UnderlayOffsetX", 0f); mat.SetFloat("_UnderlayOffsetY", 0f); mat.SetFloat("_UnderlayDilate", 0f); mat.SetFloat("_UnderlaySoftness", 0f);
            mat.DisableKeyword("UNDERLAY_ON"); mat.DisableKeyword("OUTLINE_ON");
            mat.enableInstancing = true;
            mat.renderQueue = queue;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material GlassMaterial(string name, UiTheme.GlassTokens glass, bool zwrite, int queue, bool overlay = false)
        {
            string path = $"{MaterialsFolder}/{name}.mat";
            var shader = Shader.Find("AirTools/Glass");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = shader;
            mat.SetColor("_RimColor", Color.white);
            // Liquid glass lighting (shared by every role and the ring). Frost moved to vertex data (per role).
            mat.SetColor("_EnvTop", glass.envTop);
            mat.SetColor("_EnvBottom", glass.envBottom);
            mat.SetFloat("_SpecPower", glass.specPower);
            mat.SetFloat("_KeyLift", glass.keyLift);
            mat.SetFloat("_ZWriteOn", zwrite ? 1f : 0f);
            mat.SetFloat("_ZTest", (float)(overlay ? UnityEngine.Rendering.CompareFunction.Always : UnityEngine.Rendering.CompareFunction.LessEqual));
            mat.renderQueue = queue;
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
