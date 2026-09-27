using AirTools.Core;
using TMPro;
using UnityEngine;

namespace AirTools.UI
{
    /// UX W1.3 status line, and since the declutter (docs/ux/declutter.md M1–M3) the app's one heads-up line: the guide
    /// rail's one sentence (NextStep's Step.Status) on a glass pill ~0.9 m out and ~17° below the gaze, drawn as an
    /// overlay (hud glass + hud text: never hidden behind panels or the scene), following the gaze's pitch and heading
    /// softly. A second, quieter line carries the coach hint. A tone dot says what kind of line it is (busy / done /
    /// check / no), never colour alone: the copy carries ✓ / ! / ✗ as well.
    ///
    /// While it is live (the guide rail is on, or the job's progress has it), a toast is a flash on line 1 (UiToast.Show
    /// → Flash) and a reply takes the line's place (UiToast.Reply → Yield): one heads-up surface at a time. "Do the whole
    /// job" is its progress mode (JobRailView → SetProgress: the count and the 7-glyph strip, then the summary), which
    /// shows with the rail off too. While a main-slot window is open the line docks on its top rim (M3: line 1 only,
    /// scaled so its angular size holds), and the reply card docks with it. StatusLineModel has the rules. Built by
    /// GuideRailBuilder (UiBuild); driven by GuideRail, CoachService, UiToast and JobRailView.
    public class StatusLine : MonoBehaviour
    {
        public Transform head;
        public GlassSurface surface;
        public TextMeshPro text;
        public TextMeshPro coachText;
        public GlassSurface dot;
        public float distance = 0.9f;
        [Tooltip("Degrees below the gaze (spec §5.1: −17°).")]
        public float belowGazeDeg = 17f;
        [Tooltip("Text wraps at this width (m).")]
        public float maxWidth = 0.36f;
        public float padding = 0.03f;
        [Tooltip("Seconds of the ✓ pulse when a coach hint is done.")]
        public float pulseSeconds = 0.6f;

        /// The one heads-up line in the scene (null without one).
        public static StatusLine Current { get; private set; }

        readonly StatusLineModel m_Model = new StatusLineModel();
        public StatusLineModel Model => m_Model;

        /// Line 1 as shown now: a flash, else the job's progress, else the Step's status.
        public string Message => m_Model.Line1(UiClock.Now);
        /// Line 2 as shown now ("" = none).
        public string Detail => m_Model.Line2(UiClock.Now, false);
        /// The Step's status (GuideRail), whatever line 1 shows now.
        public string StepMessage => m_Model.Step;
        public string CoachMessage => m_Model.Coach;
        public StepTone Tone => m_Model.StepTone;
        public bool Visible => m_Alpha > 0.01f;
        /// The line is (fading) up: what the declutter census counts, so a cross-fade never reads as two surfaces.
        public bool Showing => m_Model.Showing(UiClock.Now);
        /// A reply has the line's place.
        public bool Yielded => m_Model.Yielded(UiClock.Now);
        /// Toasts flash here instead of on the toast pill: the guide rail is on (declutter M1, DC2), or the job's progress
        /// has the line (it shows with the rail off too), so there is never a second heads-up surface.
        public bool Live => enabled && gameObject.activeInHierarchy && (GuideRail.Enabled || m_Model.InProgress);
        /// Declutter M2 (DC1): the job strip has line 1.
        public bool InProgress => m_Model.InProgress;
        public string ProgressLine => m_Model.Progress;
        public string ProgressDetail => m_Model.ProgressDetail;
        public int Pulses { get; private set; }

        /// Where the line sits now (the reply card takes this place) and its scale (below 1 while docked).
        public Pose Placement => new Pose(m_Pos, m_Rot);
        public float PlacementScale => m_Scale;
        public bool Placed => m_Placed;
        /// Declutter M3: the line sits on the open main-slot window's top rim (line 1 only) — since the glass lane's fix
        /// wholly above it, so no heads-up pill ever covers a main-slot panel (the gate capture had the toast on Adjust).
        public bool Docked { get; private set; }
        /// While docked: the centre of the window panel's top edge and the window's up (UiToast docks its pills there too).
        public Vector3 DockRim { get; private set; }
        public Vector3 DockUp { get; private set; } = Vector3.up;
        /// The window it's docked on (null when it floats).
        public object DockedOn => Docked ? WindowSlot.Current : null;

        /// The largest pill the line draws: two lines at maxWidth (declutter S2 footprint).
        public Vector2 MaxSize => new Vector2(maxWidth + padding * 2f + (dot != null ? 0.022f : 0f),
            LineHeight(text) + (coachText != null ? LineHeight(coachText) + padding * 0.35f : 0f) + padding);

        /// One rendered line of a text (m): its em × the theme's line ratio, plus its extra line spacing.
        public static float LineHeight(TextMeshPro t) =>
            t == null ? 0f : t.fontSize * 0.1f * (UiTheme.LineRatio + t.lineSpacing * 0.01f);

        float m_Alpha, m_PulseUntil = -1f, m_Scale = 1f;
        Vector3 m_Pos;
        Quaternion m_Rot = Quaternion.identity;
        bool m_Placed, m_Dirty = true;
        string m_Line1 = "", m_Line2 = "";
        ColorRole m_Tone1 = ColorRole.TextSecondary;

        public static ColorRole ToneRole(StepTone tone) => tone switch
        {
            StepTone.Busy => ColorRole.Info,
            StepTone.Success => ColorRole.Success,
            StepTone.Caution => ColorRole.Warning,
            StepTone.Bad => ColorRole.Danger,
            _ => ColorRole.TextSecondary,
        };

        void OnEnable() => MakeCurrent();

        void OnDisable() { if (Current == this) Current = null; }

        /// This is the scene's heads-up line (OnEnable; EditMode tests call it, OnEnable doesn't run there).
        public void MakeCurrent() => Current = this;

        /// Show a Step's status (empty hides the line; a frozen Step keeps the current one).
        public void Show(in Step step)
        {
            if (step.Frozen) return;
            string m = step.Status ?? "";
            string before = m_Model.Step;
            if (!m_Model.SetStep(m, step.Tone)) return;
            if (m != before && m.Length > 0) Log.Info($"Status: {m}");   // raw, like the "Toast:" line (hcheck-style greps)
            m_Dirty = true;
        }

        /// The coach line under the status ("" hides it).
        public void SetCoach(string line)
        {
            if (!m_Model.SetCoach(line)) return;
            if (m_Model.Coach.Length > 0) Log.Info($"Coach: {m_Model.Coach}");
            m_Dirty = true;
        }

        /// A coach hint was followed: a short ✓ pulse on the line.
        public void Pulse()
        {
            m_PulseUntil = Time.unscaledTime + pulseSeconds;
            Pulses++;
        }

        public void Hide()
        {
            m_Model.ClearStep();
            m_Dirty = true;
        }

        /// Declutter M1: a toast as a flash on line 1 for `seconds` (after a reply that holds the line); then the Step's
        /// text is back. UiToast.Show logs the raw "Toast:" line.
        public void Flash(string message, ColorRole tone, float seconds)
        {
            m_Model.ShowFlash(message ?? "", tone, seconds, UiClock.Now);
            m_Dirty = true;
        }

        /// Declutter M1: a reply takes the line's place for `seconds` (UiToast.Reply).
        public void Yield(float seconds) => m_Model.Yield(seconds, UiClock.Now);

        /// Declutter M2 (DC1): "do the whole job" as the line's progress mode — line 1 the count and the 7-glyph strip
        /// (then the summary), line 2 the newest spoken result. Shows even with the guide rail off (JobRailView).
        public void SetProgress(string line1, string line2, ColorRole tone)
        {
            if (line1 == m_Model.Progress && line2 == m_Model.ProgressDetail && tone == m_Model.ProgressTone) return;
            m_Model.SetProgress(line1, line2, tone);
            m_Dirty = true;
        }

        public void ClearProgress()
        {
            if (!m_Model.InProgress) return;
            m_Model.ClearProgress();
            m_Dirty = true;
        }

        /// Declutter M3: the pose on the open main-slot window's rim — the pill's bottom edge DockLift above the panel's top
        /// edge along the window's up (glass lane: wholly above the rim, it never covers the panel), facing the eyes,
        /// scaled by its distance over the line's so its angular size holds. False when no main-slot window is open (or it
        /// has no panel).
        bool TryDock(out Pose pose, out float scale)
        {
            pose = default;
            scale = 1f;
            if (head == null || !(WindowSlot.Current is IDockTarget window) || !window.TryPanelTop(out var top, out var rotation)) return false;
            var up = rotation * Vector3.up;
            scale = DockScale(head.position, DockPoint(top, up), distance);
            var p = DockCentre(top, up, head.position, surface != null ? surface.size.y : 0f, scale);
            var look = p - head.position;
            if (look.sqrMagnitude < 1e-6f) return false;
            pose = new Pose(p, Quaternion.LookRotation(look.normalized, Vector3.up));
            DockRim = top;
            DockUp = up;
            return true;
        }

        /// The docked pills' base: DockLift above the panel's top edge, along the window's up. Pure.
        public static Vector3 DockPoint(Vector3 panelTop, Vector3 windowUp) => panelTop + windowUp.normalized * UiZones.DockLift;

        /// Where a docked heads-up pill's centre goes (glass lane): its bottom edge on the base (DockPoint) as the eye sees
        /// it, plus `below` for a pill stacked on another, so a pill `height` tall at `scale` stays wholly above the window.
        /// The pill faces the eye, so it is lifted along the window's up with the line of sight taken out. Pure.
        public static Vector3 DockCentre(Vector3 panelTop, Vector3 windowUp, Vector3 eye, float height, float scale, float below = 0f)
        {
            var b = DockPoint(panelTop, windowUp);
            var los = b - eye;
            var up = los.sqrMagnitude > 1e-8f ? Vector3.ProjectOnPlane(windowUp, los.normalized) : windowUp;
            up = up.sqrMagnitude > 1e-8f ? up.normalized : Vector3.up;
            return b + up * (Mathf.Max(0f, below) + Mathf.Max(0f, height) * 0.5f * scale);
        }

        /// The docked line's scale: its distance from the eye over the floating line's, so it looks the same size. Pure.
        public static float DockScale(Vector3 eye, Vector3 dockPoint, float lineDistance) =>
            lineDistance > 1e-4f ? (dockPoint - eye).magnitude / lineDistance : 1f;

        /// Line 1's words, line 2's words and line 1's tone as they should be now.
        void Want(float now, out string line1, out string line2, out ColorRole tone)
        {
            line1 = m_Model.Line1(now);
            line2 = m_Model.Line2(now, Docked);
            tone = m_Model.Flashing(now) ? m_Model.FlashTone : m_Model.InProgress ? m_Model.ProgressTone : ToneRole(m_Model.StepTone);
        }

        void Layout(string line1, string line2, ColorRole tone)
        {
            m_Dirty = false;
            m_Line1 = line1; m_Line2 = line2; m_Tone1 = tone;
            if (text == null || surface == null) return;
            // Measure with the text live: an inactive TMP (faded out) has no mesh and no bounds.
            surface.gameObject.SetActive(true);
            text.gameObject.SetActive(true);
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.maxVisibleLines = 2;
            text.alignment = TextAlignmentOptions.Center;
            text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            text.rectTransform.sizeDelta = new Vector2(maxWidth, 1f);
            text.text = line1.IndexOf('<') >= 0 ? line1 : Copy.Clip(line1, 72);   // the job strip is rich text: never cut a tag
            text.ForceMeshUpdate();
            var b = text.textBounds;
            float w = Mathf.Clamp(b.size.x, 0.04f, maxWidth);
            float h = Mathf.Max(b.size.y, text.fontSize * 0.1f);
            float coachH = 0f;
            if (coachText != null)
            {
                bool second = line2.Length > 0;
                coachText.gameObject.SetActive(second);
                if (second)
                {
                    coachText.textWrappingMode = TextWrappingModes.Normal;
                    coachText.alignment = TextAlignmentOptions.Center;
                    coachText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                    coachText.rectTransform.sizeDelta = new Vector2(maxWidth, 1f);
                    coachText.text = line2;
                    coachText.ForceMeshUpdate();
                    var cb = coachText.textBounds;
                    coachH = Mathf.Max(cb.size.y, coachText.fontSize * 0.1f) + padding * 0.35f;
                    w = Mathf.Max(w, Mathf.Clamp(cb.size.x, 0.04f, maxWidth));
                }
            }
            float dotRoom = dot != null ? 0.022f : 0f;
            float pillW = w + padding * 2f + dotRoom, pillH = h + coachH + padding;
            float top = pillH * 0.5f - padding * 0.5f;
            text.transform.localPosition = new Vector3(dotRoom * 0.5f - b.center.x, top - h * 0.5f - b.center.y, -0.002f);
            if (coachText != null && coachText.gameObject.activeSelf)
                coachText.transform.localPosition = new Vector3(dotRoom * 0.5f, top - h - coachH * 0.5f, -0.002f);
            surface.SetSize(new Vector2(pillW, pillH));
            if (dot != null)
            {
                dot.customTint = true;
                dot.tint = UiTheme.Current.Color(tone);
                dot.Rebuild();
                dot.transform.localPosition = new Vector3(-pillW * 0.5f + padding * 0.7f, top - h * 0.5f, -0.001f);
            }
        }

        void LateUpdate()
        {
            float now = UiClock.Now;
            Want(now, out var line1, out var line2, out var tone);
            // Relayout when the words change; while line 1 is empty the old words fade out with the pill.
            if (line1.Length > 0 && (m_Dirty || line1 != m_Line1 || line2 != m_Line2 || tone != m_Tone1)) Layout(line1, line2, tone);
            if (head == null || surface == null || text == null) return;
            float target = m_Model.Showing(now) ? 1f : 0f;
            float dur = UiSettings.Duration(UiTheme.Current.motion.standard);
            m_Alpha = dur <= 0f ? target : Mathf.MoveTowards(m_Alpha, target, Time.unscaledDeltaTime / dur);
            // Declutter M3: dock on the open main-slot window's top rim, else float 17° below the gaze.
            bool docked = TryDock(out var pose, out float scale);
            if (!docked) { pose = UiToast.PoseFor(head.position, head.forward, distance, belowGazeDeg); scale = 1f; }
            if (docked != Docked) { Docked = docked; m_Dirty = true; }   // line 2 hides while docked
            float k = 1f - Mathf.Exp((docked ? -12f : -5f) * Time.unscaledDeltaTime);   // docked: keep up with the window
            m_Pos = m_Placed ? Vector3.Lerp(m_Pos, pose.position, k) : pose.position;
            m_Rot = m_Placed ? Quaternion.Slerp(m_Rot, pose.rotation, k) : pose.rotation;
            m_Scale = m_Placed ? Mathf.Lerp(m_Scale, scale, k) : scale;
            m_Placed = true;
            float pulse = m_PulseUntil > Time.unscaledTime ? Mathf.Sin((m_PulseUntil - Time.unscaledTime) / pulseSeconds * Mathf.PI) : 0f;
            transform.SetPositionAndRotation(m_Pos, m_Rot);
            transform.localScale = Vector3.one * (m_Scale * (1f + 0.06f * pulse));
            var theme = UiTheme.Current;
            var baseTint = theme.TierColor(surface.EffectiveTier);
            // Near-opaque behind text (Fieldglass: α ≥ 0.94), faded with the line; the ✓ pulse warms it toward success.
            var tint = Color.Lerp(baseTint, theme.colors.success, 0.35f * pulse);
            surface.SetTint(new Color(tint.r, tint.g, tint.b, Mathf.Max(baseTint.a, 0.94f) * m_Alpha));
            var tc = theme.colors.textPrimary;
            text.color = new Color(tc.r, tc.g, tc.b, tc.a * m_Alpha);
            if (coachText != null)
            {
                var sc = theme.colors.textSecondary;
                coachText.color = new Color(sc.r, sc.g, sc.b, sc.a * m_Alpha);
            }
            if (dot != null) dot.SetTint(new Color(dot.tint.r, dot.tint.g, dot.tint.b, m_Alpha));
            bool show = m_Alpha > 0.001f;
            if (surface.gameObject.activeSelf != show)
            {
                surface.gameObject.SetActive(show);
                text.gameObject.SetActive(show);
                if (dot != null) dot.gameObject.SetActive(show);
                if (coachText != null && !show) coachText.gameObject.SetActive(false);
            }
        }
    }
}
