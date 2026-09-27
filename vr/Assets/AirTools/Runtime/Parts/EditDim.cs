using UnityEngine;

namespace AirTools.Parts
{
    /// edit6dof: the Edit view's dimmed world — an inverted sphere round the eye (3 m, parented to the centre eye) drawn
    /// with AirTools/DimShell after the world (queue 3050, ZTest Always) as a translucent dark veil: the room greys out,
    /// cheaply (one full-screen translucent pass, no post-processing, stereo-instancing aware). The edited item draws
    /// after it (PartInstance.DrawOverDim), then the view's own glass. While the item sits at the centre the veil also
    /// writes its depth (3 m, behind the item), so the item, its arrows and panel draw over any wall nearer than them;
    /// out in the room (Move, placing a new part) it doesn't, and the item is depth-tested against the room as usual.
    /// Fades with the motion tokens (at once with Reduce motion). Its own material copy: no asset is changed at runtime.
    [RequireComponent(typeof(MeshRenderer))]
    public class EditDim : MonoBehaviour
    {
        [Range(0f, 1f)] public float strength = 0.55f;
        public float fadeSeconds = 0.25f;

        public float Alpha { get; private set; }
        public float Target { get; private set; }
        public bool WritesDepth { get; private set; }

        MeshRenderer m_Renderer;
        Material m_Material;
        static readonly int s_Color = Shader.PropertyToID("_Color");
        static readonly int s_ZWrite = Shader.PropertyToID("_ZWrite");

        void Awake() => Init();

        void Init()
        {
            if (m_Renderer != null) return;
            m_Renderer = GetComponent<MeshRenderer>();
            if (m_Renderer.sharedMaterial != null)
            {
                m_Material = new Material(m_Renderer.sharedMaterial) { name = m_Renderer.sharedMaterial.name + " (edit view)" };
                m_Renderer.sharedMaterial = m_Material;
            }
            Apply();
        }

        void OnDestroy()
        {
            if (m_Material == null) return;
            if (Application.isPlaying) Destroy(m_Material);
            else DestroyImmediate(m_Material);
        }

        /// Dim on (`on`) or off; `depth`: the veil writes its depth (the item at the centre).
        public void Set(bool on, bool depth)
        {
            Init();
            Target = on ? strength : 0f;
            if (depth != WritesDepth) { WritesDepth = depth; if (m_Material != null) m_Material.SetFloat(s_ZWrite, depth ? 1f : 0f); }
            if (AirTools.UI.UiSettings.ReducedMotion || fadeSeconds <= 0f || !Application.isPlaying) { Alpha = Target; Apply(); }
        }

        void Update() => Step(Time.unscaledDeltaTime);

        /// Fade toward the target (tests call it).
        public void Step(float dt)
        {
            if (Mathf.Approximately(Alpha, Target)) return;
            float rate = strength / Mathf.Max(fadeSeconds, 1e-3f);
            Alpha = Mathf.MoveTowards(Alpha, Target, rate * Mathf.Max(dt, 0f));
            Apply();
        }

        void Apply()
        {
            if (m_Renderer == null) return;
            m_Renderer.enabled = Alpha > 0.001f;
            if (m_Material == null) return;
            var c = AirTools.UI.UiTheme.Current.colors.background;
            c.a = Alpha;
            m_Material.SetColor(s_Color, c);
        }
    }
}
