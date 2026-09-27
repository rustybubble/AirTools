using UnityEngine;

namespace AirTools.Core
{
    /// Colour fade in front of the eyes: an inverted sphere parented to the centre-eye anchor, drawn last with ZTest Always.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class FadeOverlay : MonoBehaviour
    {
        public Color color = Color.black;
        [Range(0f, 1f)] [SerializeField] float m_Alpha;

        MeshRenderer m_Renderer;
        MaterialPropertyBlock m_Block;
        static readonly int s_Color = Shader.PropertyToID("_Color");

        public float Alpha
        {
            get => m_Alpha;
            set { m_Alpha = Mathf.Clamp01(value); Apply(); }
        }

        void Awake()
        {
            m_Renderer = GetComponent<MeshRenderer>();
            m_Block = new MaterialPropertyBlock();
            Apply();
        }

        void Apply()
        {
            if (m_Renderer == null) return;
            m_Renderer.enabled = m_Alpha > 0.001f;
            var c = color;
            c.a = m_Alpha;
            m_Block.SetColor(s_Color, c);
            m_Renderer.SetPropertyBlock(m_Block);
        }
    }
}
