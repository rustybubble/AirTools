using UnityEngine;

namespace AirTools.UI
{
    /// A rounded glass (or solid) surface of a given size, drawn with the shared tier material and a per-surface
    /// quad mesh (shape in vertex data, tint in vertex colour). Optional elevation adds a soft shadow behind it.
    /// Follows UiSettings.HighContrast (glass → ElevatedSolid).
    /// Liquid glass (glass lane): every surface draws the tool ring's look for its role (GlassRole; Auto resolves from the
    /// fields below, GlassLooks.Resolve) — the look is vertex data too, so the material stays shared. SetState(glow, press)
    /// is the buttons' hover / press / selected rim.
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class GlassSurface : MonoBehaviour
    {
        public Vector2 size = new Vector2(0.2f, 0.1f);
        public GlassTier tier = GlassTier.GlassRegular;
        public RadiusRole radius = RadiusRole.Large;
        public Elevation elevation = Elevation.None;
        [Tooltip("Use this tint instead of the tier's colour (controls, chips, accents).")]
        public bool customTint;
        public Color tint = Color.white;
        [Tooltip("Rim highlight multiplier (0 = no rim, e.g. flat chips).")]
        public float rim = 1f;
        [Tooltip("Corner radius in local units, overriding the radius role (≥ 0). For surfaces in scaled spaces, e.g. labels.")]
        public float radiusOverride = -1f;
        [Tooltip("Draw order within the UI (−1 = automatic: panel 0, row 1, control 2). Fixed layers keep a small control from\n" +
                 "sorting behind the larger row it sits on (transparent sorting is by object centre distance).")]
        public int layer = -1;
        [Tooltip("World annotation: drawn as an overlay (never hidden by scene geometry), under the UI panels.")]
        public bool overlay;
        [Tooltip("Heads-up (toast, reply card): drawn above every UI panel, never hidden by one.")]
        public bool hud;
        [Tooltip("Rim width in local units, overriding the theme (≥ 0).")]
        public float rimWidthOverride = -1f;
        [Tooltip("Liquid Glass role (Auto: heads-up → Hud, overlay → Label, ≤ 12 mm → Mark, tinted → Control, ElevatedSolid → Card, " +
                 "else Window).")]
        public GlassRole role = GlassRole.Auto;

        Mesh m_Mesh, m_ShadowMesh;
        MeshRenderer m_Shadow;
        Color m_Current;
        bool m_Built;
        GlassMesh.Shape m_Shape;

        public Color CurrentTint => m_Current;
        /// The role it draws with (Auto resolved).
        public GlassRole EffectiveRole => GlassLooks.Resolve(role, tier, hud, overlay, customTint, size);
        /// The Liquid Glass look it draws with now (its role under the current settings).
        public GlassLook Look => UiTheme.Current.Look(EffectiveRole);
        /// The drawn state glow and press (GlassButton).
        public float Glow => m_Shape.Glow;
        public float Press => m_Shape.Press;

        /// UI draw layers: shadows −1, panels 0, rows/cards 1, controls 2, indicators/swatch dots 3, text 5 (UiText).
        public const int LayerPanel = 0, LayerRow = 1, LayerControl = 2, LayerIndicator = 3, LayerText = 5;

        public int SortingOrder => overlay ? 0 : layer >= 0 ? layer
            : customTint ? LayerControl : tier == GlassTier.ElevatedSolid ? LayerRow : LayerPanel;
        public GlassTier EffectiveTier => UiSettings.Effective(tier);

        public static GlassSurface Create(Transform parent, string name, Vector2 size, GlassTier tier, RadiusRole radius,
            Elevation elevation = Elevation.None, Vector3 localPos = default)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            var s = go.AddComponent<GlassSurface>();
            s.size = size; s.tier = tier; s.radius = radius; s.elevation = elevation;
            s.Rebuild();
            return s;
        }

        void OnEnable()
        {
            UiSettings.Changed += Rebuild;
            Rebuild();
        }

        void OnDisable() => UiSettings.Changed -= Rebuild;

        void OnDestroy()
        {
            DestroyMesh(m_Mesh);
            DestroyMesh(m_ShadowMesh);
        }

        /// Re-create the quad (size, radius, tier or settings changed).
        public void Rebuild()
        {
            var theme = UiTheme.Current;
            var eff = EffectiveTier;
            var r = GetComponent<MeshRenderer>();
            var mat = hud && theme.materials.hudGlass != null ? theme.materials.hudGlass
                : overlay && theme.materials.labelGlass != null ? theme.materials.labelGlass : theme.TierMaterial(eff);
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortingOrder = SortingOrder;
            if (m_Mesh == null) { m_Mesh = GlassMesh.Create(name); GetComponent<MeshFilter>().sharedMesh = m_Mesh; }
            var baseTint = customTint ? tint : theme.TierColor(eff);
            if (!m_Built) m_Current = baseTint;
            if (!customTint) m_Current = baseTint;
            var look = theme.Look(EffectiveRole);
            m_Shape = new GlassMesh.Shape
            {
                Size = size,
                Radius = radiusOverride >= 0f ? radiusOverride : theme.Radius(radius, size.y),
                RimWidth = rimWidthOverride >= 0f ? rimWidthOverride : theme.shape.rimWidth,
                RimStrength = theme.shape.rimStrength * rim * (eff == GlassTier.ElevatedSolid ? 0.6f : 1f),
                // Liquid glass: the role's look, its bezel fitted to this size.
                Gradient = look.gradient,
                Bezel = GlassLooks.Bezel(look, size),
                Highlight = look.highlight,
                EdgeClear = look.edgeClear,
                Sheen = look.sheen,
                Frost = look.frost,
                Glow = m_Shape.Glow,
                Press = m_Shape.Press,
            };
            GlassMesh.Write(m_Mesh, m_Shape, m_Current);
            m_Built = true;
            BuildShadow(theme);
        }

        void BuildShadow(UiTheme theme)
        {
            var (drop, soft, alpha) = UiTheme.ElevationShadow(elevation);
            if (alpha <= 0f)
            {
                if (m_Shadow != null) m_Shadow.gameObject.SetActive(false);
                return;
            }
            if (m_Shadow == null)
            {
                var existing = transform.Find("Shadow");
                var go = existing != null ? existing.gameObject : new GameObject("Shadow");
                go.transform.SetParent(transform, false);
                if (go.GetComponent<MeshFilter>() == null) go.AddComponent<MeshFilter>();
                m_Shadow = go.GetComponent<MeshRenderer>();
                if (m_Shadow == null) m_Shadow = go.AddComponent<MeshRenderer>();
                m_Shadow.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_Shadow.receiveShadows = false;
            }
            m_Shadow.gameObject.SetActive(true);
            m_Shadow.sortingOrder = SortingOrder - 1;
            m_Shadow.transform.localPosition = new Vector3(0f, -drop, 0.004f);
            if (theme.materials.shadow != null) m_Shadow.sharedMaterial = theme.materials.shadow;
            if (m_ShadowMesh == null) { m_ShadowMesh = GlassMesh.Create(name + " shadow"); m_Shadow.GetComponent<MeshFilter>().sharedMesh = m_ShadowMesh; }
            GlassMesh.Write(m_ShadowMesh, new GlassMesh.Shape
            {
                Size = size, Radius = radiusOverride >= 0f ? radiusOverride : theme.Radius(radius, size.y), Softness = soft,
            }, new Color(0f, 0f, 0f, alpha));
        }

        /// Set the drawn tint now (animations call this every frame they run; no allocation).
        public void SetTint(Color c)
        {
            if (m_Mesh == null) Rebuild();
            if (c == m_Current) return;
            m_Current = c;
            GlassMesh.SetTint(m_Mesh, c);
        }

        /// Set the state glow (brighter rim, stroke and highlight) and press (the bezel turns concave) now: GlassButton's
        /// hover / press / selected. Writes only on a change; no allocation.
        public void SetState(float glow, float press)
        {
            if (m_Mesh == null) Rebuild();
            if (glow == m_Shape.Glow && press == m_Shape.Press) return;
            m_Shape.Glow = glow;
            m_Shape.Press = press;
            GlassMesh.SetState(m_Mesh, m_Shape);
        }

        public void SetSize(Vector2 newSize)
        {
            if (newSize == size && m_Built) return;
            size = newSize;
            Rebuild();
        }

        static void DestroyMesh(Mesh m)
        {
            if (m == null) return;
            if (Application.isPlaying) Destroy(m);
            else DestroyImmediate(m);
        }
    }
}
