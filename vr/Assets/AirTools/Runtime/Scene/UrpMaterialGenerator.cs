using GLTFast;
using GLTFast.Logging;
using GLTFast.Materials;
using UnityEngine;
using GltfMaterial = GLTFast.Schema.MaterialBase;

namespace AirTools.Scene
{
    /// glTFast material generator that builds materials from templates the app ships (URP/Lit, URP/Unlit) instead of
    /// glTFast's own shader graphs. Nothing in the project references those graphs, so a player build strips them and
    /// runtime-loaded GLBs (server parts, scene packages) would render with no shader on the Quest. Copies what the
    /// app needs: base colour factor + texture, metallic / roughness factors, alpha cutoff, double-sidedness.
    /// Unlit when the template is unlit, when the glTF material asks for KHR_materials_unlit, or when forceUnlit
    /// (photogrammetry has its lighting baked into the atlas).
    public class UrpMaterialGenerator : IMaterialGenerator
    {
        readonly Material m_Lit, m_Unlit;
        readonly bool m_ForceUnlit;
        static readonly int s_BaseMap = Shader.PropertyToID("_BaseMap");
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        public UrpMaterialGenerator(Material lit, Material unlit, bool forceUnlit = false)
        {
            m_Lit = lit; m_Unlit = unlit; m_ForceUnlit = forceUnlit;
        }

        public Material GetDefaultMaterial(bool pointsSupport = false)
        {
            var t = m_ForceUnlit && m_Unlit != null ? m_Unlit : m_Lit != null ? m_Lit : m_Unlit;
            return t != null ? new Material(t) { name = "glTF default" } : null;
        }

        public Material GenerateMaterial(GltfMaterial gltfMaterial, IGltfReadable gltf, bool pointsSupport = false)
        {
            bool unlit = m_ForceUnlit || gltfMaterial.Extensions?.KHR_materials_unlit != null || m_Lit == null;
            var template = unlit && m_Unlit != null ? m_Unlit : m_Lit;
            if (template == null) return null;
            var m = new Material(template) { name = string.IsNullOrEmpty(gltfMaterial.name) ? "glTF material" : gltfMaterial.name };
            var pbr = gltfMaterial.PbrMetallicRoughness;
            if (pbr != null)
            {
                // glTF factors are linear; Unity colour properties take sRGB values and linearise them.
                if (m.HasProperty(s_BaseColor)) m.SetColor(s_BaseColor, pbr.BaseColor.gamma);
                int ti = pbr.BaseColorTexture != null ? pbr.BaseColorTexture.index : -1;
                if (ti >= 0)
                {
                    var tex = gltf.GetTexture(ti);
                    if (tex != null)
                    {
                        if (m.HasProperty(s_BaseMap)) m.SetTexture(s_BaseMap, tex);
                        m.mainTexture = tex;
                    }
                }
                if (!unlit)
                {
                    if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", pbr.metallicFactor);
                    if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 1f - pbr.roughnessFactor);
                }
            }
            if (gltfMaterial.GetAlphaMode() == GltfMaterial.AlphaMode.Mask && m.HasProperty("_AlphaClip"))
            {
                m.SetFloat("_AlphaClip", 1f);
                m.SetFloat("_Cutoff", gltfMaterial.alphaCutoff);
                m.EnableKeyword("_ALPHATEST_ON");
            }
            if (gltfMaterial.doubleSided && m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            return m;
        }

        public void SetLogger(ICodeLogger logger) { }
    }
}
