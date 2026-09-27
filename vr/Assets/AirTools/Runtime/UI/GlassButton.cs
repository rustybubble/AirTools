using System;
using Oculus.Interaction;
using TMPro;
using UnityEngine;

namespace AirTools.UI
{
    public enum ButtonStyle { Primary, Secondary, Borderless, Destructive, Toggle, Chip }

    /// The one button component (SPEC §9 UI design system). Driven by ISDK interactables — poke (direct touch) and
    /// ray (hand/controller pointing) on the same surface — with the design-system states:
    ///   rest · hover/focus (brighter veil, +2% scale) · press (compresses 3 mm, −3% scale) · selected (tape-yellow ink
    ///   fill + dark indicator bar + semibold dark label, never colour alone) · disabled (faint, dimmed label) ·
    ///   destructive (danger fill; with confirm, the first press arms it and only a second press within 3 s fires).
    /// UX D3: Primary is white glass with a dark label (the glass shader keeps its rim and top light on light fills).
    /// Liquid glass (glass lane): the surface is a glass capsule (GlassRole.Control); hover brightens its rim (glow 0.6),
    /// a press lights it fully and turns the bezel concave on top of the 3 mm press depth, selected keeps a faint glow
    /// (GlassLooks.Glow). The veil itself steps only a little, so the label keeps its contrast in every state.
    /// Clicked fires once per press (on entering Select). Visual motion follows the motion tokens and reduced motion.
    public class GlassButton : MonoBehaviour
    {
        public PokeInteractable poke;
        public RayInteractable ray;
        [Tooltip("Moves/scales with hover and press; holds the surface and label.")]
        public Transform visual;
        public GlassSurface surface;
        public TextMeshPro label;
        public GlassSurface indicator;
        public ButtonStyle style = ButtonStyle.Secondary;
        public bool selected;
        public bool interactable = true;
        [Tooltip("Destructive: require a second press within 3 s.")]
        public bool confirm;
        public string confirmText = "Tap again to confirm";
        [TextArea] public string tooltip;
        public float cooldownSeconds = 0.3f;
        [Tooltip("Its ray target follows the D5 flag (windows, Enter): off = poke only.")]
        public bool rayNeedsD5;

        /// UX decision D5 (docs/ux/README.md): ray + poke on windows and the Enter control. Today (false): poke only —
        /// the measuring ray once pressed a button in its path (e61c87f). On: a ray press needs a 120 ms hover first.
        public static bool RayOnWindows = true;   // D5 decided (SPEC §9): ray + poke on windows and Enter, 120 ms dwell
        public const float RayDwellSeconds = 0.12f;
        float m_RayHoverSince = -1f;

        public event Action Clicked;
        public event Action<bool> HoverChanged;

        public InteractableState State { get; private set; } = InteractableState.Normal;
        public bool Armed => m_ArmedUntil > Time.unscaledTime;
        public int ClickCount { get; private set; }

        string m_Text;
        float m_Last = -999f, m_ArmedUntil = -1f;
        Color m_Tint, m_LabelColor;
        float m_Scale = 1f, m_Depth, m_Glow, m_Press;
        bool m_WasSelectedLabel, m_Initialised;

        void OnEnable()
        {
            if (poke != null) poke.WhenStateChanged += OnState;
            if (ray != null) ray.WhenStateChanged += OnState;
            if (ray != null && rayNeedsD5) ray.enabled = RayOnWindows;
            UiSettings.Changed += Refresh;
            m_Initialised = false;
        }

        void OnDisable()
        {
            if (poke != null) poke.WhenStateChanged -= OnState;
            if (ray != null) ray.WhenStateChanged -= OnState;
            UiSettings.Changed -= Refresh;
            State = InteractableState.Normal;
        }

        /// Label text (kept for disarming a confirm).
        public void SetText(string text)
        {
            m_Text = text;
            if (label != null && !Armed) label.text = text;
        }

        public string Text => m_Text ?? (label != null ? label.text : "");

        public void SetSelected(bool on) { if (selected != on) { selected = on; Refresh(); } }
        public void SetInteractable(bool on) { if (interactable != on) { interactable = on; Refresh(); } }

        void OnState(InteractableStateChangeArgs args)
        {
            var s = Combined();
            var prev = State;
            State = s;
            if (s == InteractableState.Hover && prev == InteractableState.Normal) { HoverChanged?.Invoke(true); UiFeedback.Hover(); }
            if (s == InteractableState.Normal && prev != InteractableState.Normal) HoverChanged?.Invoke(false);
            if (ray != null && ray.State == InteractableState.Hover && m_RayHoverSince < 0f) m_RayHoverSince = Time.unscaledTime;
            if (ray != null && ray.State == InteractableState.Normal) m_RayHoverSince = -1f;
            if (args.NewState == InteractableState.Select)
            {
                bool byRay = !(poke != null && poke.State == InteractableState.Select);
                // D5: a ray press counts only after a short hover — a ray that merely sweeps across never presses.
                if (byRay && rayNeedsD5 && (m_RayHoverSince < 0f || Time.unscaledTime - m_RayHoverSince < RayDwellSeconds)) return;
                // Which input pressed it (headset checklist: proves the "by hand" checks from logcat).
                AirTools.Core.Log.Info($"Button {name} ({(poke != null && poke.State == InteractableState.Select ? "poke" : "ray")}, {((OVRInput.GetConnectedControllers() & OVRInput.Controller.Touch) != 0 ? "controller" : "hand")})");
                Press();
            }
        }

        InteractableState Combined()
        {
            var a = poke != null ? poke.State : InteractableState.Normal;
            var b = ray != null ? ray.State : InteractableState.Normal;
            if (a == InteractableState.Select || b == InteractableState.Select) return InteractableState.Select;
            if (a == InteractableState.Hover || b == InteractableState.Hover) return InteractableState.Hover;
            return InteractableState.Normal;
        }

        /// Same path as a physical poke / ray pinch (tests and the agent harness call this).
        public bool Press()
        {
            if (!interactable) return false;
            if (Time.unscaledTime - m_Last < cooldownSeconds) return false;
            m_Last = Time.unscaledTime;
            if (confirm && !Armed)
            {
                m_ArmedUntil = Time.unscaledTime + 3f;
                if (label != null) label.text = confirmText;
                UiFeedback.Press(transform.position);
                return false;
            }
            m_ArmedUntil = -1f;
            if (label != null && m_Text != null) label.text = m_Text;
            ClickCount++;
            UiFeedback.Press(transform.position);
            Clicked?.Invoke();
            return true;
        }

        public void Refresh() => m_Initialised = false;

        /// Target tint for the current state and style.
        public Color TargetTint()
        {
            var c = UiTheme.Current.colors;
            bool hover = State == InteractableState.Hover, press = State == InteractableState.Select;
            if (!interactable) return c.controlDisabled;
            switch (style)
            {
                case ButtonStyle.Primary:
                    // D3: white "do it". Selected (the coach pointing at it) inks it like any selected control.
                    if (selected) return press ? c.inkPressed : hover ? c.inkHover : c.ink;
                    return press ? c.primaryPressed : hover ? c.primaryHover : c.primary;
                case ButtonStyle.Destructive:
                    var d = Armed ? c.dangerFill : new Color(c.danger.r, c.danger.g, c.danger.b, 0.28f);
                    return press ? Darken(c.dangerFill, 0.15f) : hover ? new Color(d.r, d.g, d.b, Mathf.Min(1f, d.a + 0.15f)) : d;
                case ButtonStyle.Borderless:
                    return press ? c.controlHover : hover ? c.control : new Color(1f, 1f, 1f, 0f);
                default:
                    // D3: selected / on (toggles, chips, segments) is tape-yellow ink.
                    if (selected) return press ? c.inkPressed : hover ? c.inkHover : c.ink;
                    return press ? c.controlPressed : hover ? c.controlHover : c.control;
            }
        }

        /// Label colour for the current fill (D3): dark on the white primary and on ink, white on the armed destructive
        /// fill, dimmed when disabled, textPrimary on the veils.
        public Color TargetLabelColor()
        {
            var c = UiTheme.Current.colors;
            if (!interactable) return c.textDisabled;
            if (selected && style != ButtonStyle.Borderless && style != ButtonStyle.Destructive) return c.onInk;
            if (style == ButtonStyle.Primary) return c.onPrimary;
            if (style == ButtonStyle.Destructive && Armed) return c.onDanger;
            return c.textPrimary;
        }

        void Update()
        {
            if (ray != null && rayNeedsD5 && ray.enabled != RayOnWindows) ray.enabled = RayOnWindows;
            if (confirm && m_ArmedUntil > 0f && !Armed)
            {
                m_ArmedUntil = -1f;
                if (label != null && m_Text != null) label.text = m_Text;
            }
            var m = UiTheme.Current.motion;
            bool hover = State == InteractableState.Hover, press = State == InteractableState.Select;
            var tint = TargetTint();
            float scale = !interactable ? 1f : press ? m.pressScale : hover ? m.hoverScale : 1f;
            float depth = press ? m.pressDepth : 0f;
            if (UiSettings.ReducedMotion) scale = 1f;
            float dur = UiSettings.Duration(press ? m.fast : m.standard);
            float k = !m_Initialised || dur <= 0f ? 1f : 1f - Mathf.Exp(-Time.unscaledDeltaTime * 4.6f / dur);   // ~99% in dur
            // The label eases with the fill (D3 labels change between dark and light with the fill behind them).
            var labelColor = TargetLabelColor();
            bool snap = !m_Initialised;
            m_Tint = Color.Lerp(snap ? tint : m_Tint, tint, k);
            m_LabelColor = Color.Lerp(snap ? labelColor : m_LabelColor, labelColor, k);
            m_Scale = Mathf.Lerp(m_Initialised ? m_Scale : scale, scale, k);
            m_Depth = Mathf.Lerp(m_Initialised ? m_Depth : depth, depth, k);
            // Liquid glass: the rim's state glow and the concave press ease with the fill, then settle exactly (no mesh
            // writes once they're there). They are state, not motion, so reduced motion keeps them.
            float glow = TargetGlow();
            m_Glow = Settle(Mathf.Lerp(snap ? glow : m_Glow, glow, k), glow);
            float pressTarget = press && interactable ? 1f : 0f;
            m_Press = Settle(Mathf.Lerp(snap ? pressTarget : m_Press, pressTarget, k), pressTarget);
            m_Initialised = true;
            if (surface != null) { surface.SetTint(m_Tint); surface.SetState(m_Glow, m_Press); }
            if (visual != null)
            {
                visual.localScale = Vector3.one * m_Scale;
                visual.localPosition = new Vector3(0f, 0f, m_Depth);
            }
            ApplyLabelState();
        }

        /// The rim's state glow now (GlassLooks.Glow): press 1, hover 0.6, selected 0.3, disabled 0.
        public float TargetGlow() => GlassLooks.Glow(interactable, State == InteractableState.Hover, State == InteractableState.Select, selected);

        static float Settle(float value, float target) => Mathf.Abs(value - target) < 0.002f ? target : value;

        void ApplyLabelState()
        {
            if (label == null) return;
            var theme = UiTheme.Current;
            bool strong = selected || style == ButtonStyle.Primary || (style == ButtonStyle.Destructive && Armed);
            // D3: dark labels on the white primary (17.5:1) and on ink (12.8:1); white on the armed destructive fill.
            if (label.color != m_LabelColor) label.color = m_LabelColor;
            if (strong != m_WasSelectedLabel || label.fontSharedMaterial == null)
            {
                UiText.SetWeight(label, strong ? Weight.Semibold : Weight.Medium);
                m_WasSelectedLabel = strong;
            }
            if (indicator != null)
            {
                bool show = selected && style != ButtonStyle.Primary;
                if (indicator.gameObject.activeSelf != show) indicator.gameObject.SetActive(show);
                if (show) indicator.SetTint(theme.colors.onInk);   // D3: a dark bar on the ink fill
            }
        }

        static Color Darken(Color c, float amount) => new Color(c.r * (1f - amount), c.g * (1f - amount), c.b * (1f - amount), c.a);
    }
}
