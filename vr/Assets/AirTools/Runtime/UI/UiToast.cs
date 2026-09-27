using AirTools.Core;
using TMPro;
using UnityEngine;

namespace AirTools.UI
{
    /// Short status messages ("✓ Saved · Width 0.26 m", "✓ Fits the door") on one glass pill just below where you look,
    /// and agent / voice / "What is this?" answers on a reply card (UX W0.8). Both are heads-up: drawn above every panel
    /// (overlay glass + overlay text), follow the gaze's pitch as well as its heading (softly, never head-locked), wrap
    /// to at most two lines (a reply: four) and stay up longer for longer text. Every message is scrubbed for display
    /// (Copy.Clean); the "Toast: …" and "Reply: …" log lines stay raw for tools/demo/hcheck.py.
    ///
    /// Declutter M1 (DC2), one heads-up surface at a time: while the status line is live (the guide rail is on) a toast
    /// is a flash on it instead of this pill, and the reply card takes the line's place (0.9 m, 17° below the gaze: the
    /// line yields while it is up). With the rail off this toast is the fallback, and it waits while a reply is up.
    public class UiToast : MonoBehaviour
    {
        public Transform head;
        public GlassSurface surface;
        public TextMeshPro text;
        public GlassSurface dot;
        [Tooltip("The shortest time a message stays up (s); longer text stays longer (Duration).")]
        public float seconds = 2.4f;
        public float distance = 0.6f;
        [Tooltip("Degrees below the gaze (UX A9: the status stays within 30° of where you look, up or down).")]
        public float belowGazeDeg = 12f;
        [Tooltip("Text wraps at this width (m).")]
        public float maxWidth = 0.26f;
        public int maxLines = 2;
        [Tooltip("The reply card (agent / voice / ask answers) rather than the status toast.")]
        public bool isReply;
        public float padding = 0.022f;

        static UiToast s_Toast, s_Reply;
        float m_From = -1f, m_Until = -1f, m_YieldUntil = -1f, m_Alpha;
        Vector3 m_Pos;
        Quaternion m_Rot = Quaternion.identity;
        bool m_Placed;

        public string Message => text != null ? text.text : "";
        public bool Visible => m_Alpha > 0.01f;
        /// A message is (fading) up: what the declutter census counts, so a cross-fade never reads as two surfaces.
        public bool Showing
        {
            get
            {
                float now = UiClock.Now;
                return now >= m_From && now < m_Until && now >= m_YieldUntil;
            }
        }
        /// The largest pill: maxLines lines at maxWidth (declutter S2 footprint).
        public Vector2 MaxSize => new Vector2(maxWidth + padding * 2f + (dot != null ? 0.02f : 0f),
            Mathf.Max(1, maxLines) * StatusLine.LineHeight(text) + padding);
        /// Seconds the current message stays up.
        public float ShownFor { get; private set; }
        /// Docked above the open main-slot window's top rim with the status line (glass lane fix), not over the panel.
        public bool Docked { get; private set; }
        /// Toasts shown on this pill (not flashed on the status line).
        public int Presented { get; private set; }

        /// The status toast / the reply card in the scene (null without one).
        public static UiToast Toast => s_Toast;
        public static UiToast ReplyCard => s_Reply;

        void OnEnable()
        {
            MakeCurrent();
            if (!isReply) Services.Register(this);
        }

        void OnDisable()
        {
            if (s_Toast == this) { s_Toast = null; Services.Unregister(this); }
            if (s_Reply == this) s_Reply = null;
        }

        /// This is the scene's toast / reply card (OnEnable; EditMode tests call it, OnEnable doesn't run there).
        public void MakeCurrent()
        {
            if (isReply) s_Reply = this;
            else s_Toast = this;
        }

        public static void Show(string message, ColorRole tone = ColorRole.Info)
        {
            Log.Info($"Toast: {message}");   // raw: tools/demo/hcheck.py reads this line
            string clean = Copy.Clean(message);   // safety net: no dev text on screen
            var line = StatusLine.Current;
            if (line != null && line.Live)
            {
                // Declutter M1: one heads-up line. The flash stays as long as this toast would have.
                line.Flash(clean, tone, Duration(clean, s_Toast != null ? s_Toast.seconds : 2.4f, false));
                return;
            }
            if (s_Toast != null) s_Toast.Present(clean, tone);
        }

        /// An agent / voice / "What is this?" answer: the reply card (UX W0.8), else the toast. The reply takes the
        /// status line's place (it yields) and the fallback toast waits until it's gone (declutter M1).
        public static void Reply(string message)
        {
            Log.Info($"Reply: {message}");
            var card = s_Reply != null ? s_Reply : s_Toast;
            if (card == null) return;
            card.Present(Copy.Clean(message), ColorRole.Info);
            if (card != s_Reply) return;
            var line = StatusLine.Current;
            if (line != null) line.Yield(card.ShownFor);
            if (s_Toast != null) s_Toast.Yield(card.ShownFor);
        }

        // switchclean: a world-model switch clears what's up about the site left (a toast, an answer, the status line's
        // flash and a reply's hold on it); anything shown after the switch shows as usual.

        /// Take this message down now (it fades out); a toast waiting behind a reply goes too.
        public void Dismiss()
        {
            float now = UiClock.Now;
            if (m_Until > now) m_Until = now;
            m_YieldUntil = -1f;
        }

        /// A toast, an answer card or a status-line flash / reply is up.
        public static bool AnyShowing()
        {
            if (s_Toast != null && s_Toast.Showing) return true;
            if (s_Reply != null && s_Reply.Showing) return true;
            var line = StatusLine.Current;
            return line != null && (line.Model.Flashing(UiClock.Now) || line.Model.Yielded(UiClock.Now));
        }

        /// Take down the toast, the answer card and the status line's flash (and give the line back from a reply).
        /// True when something was up.
        public static bool DismissAll()
        {
            bool any = AnyShowing();
            if (s_Toast != null) s_Toast.Dismiss();
            if (s_Reply != null) s_Reply.Dismiss();
            var line = StatusLine.Current;
            if (line != null) { line.Model.EndFlash(); line.Model.EndYield(); }
            return any;
        }
        // end switchclean

        /// Stay hidden for `seconds` (a reply has the heads-up place); a message that outlasts it shows again after.
        public void Yield(float seconds)
        {
            float until = UiClock.Now + Mathf.Max(0f, seconds);
            if (until > m_YieldUntil) m_YieldUntil = until;
        }

        /// How long a message of this length stays up: the minimum, plus ~20 characters a second after the first 24.
        public static float Duration(string message, float min, bool reply) =>
            Mathf.Clamp(min + 0.05f * Mathf.Max(0, (message?.Length ?? 0) - 24), min, reply ? 10f : 6f);

        /// Where the toast sits for a head looking along `headForward` (pitch included): `belowGazeDeg` under the gaze,
        /// its elevation kept between 60° down and 10° up. Pure (A9 test).
        public static Pose PoseFor(Vector3 headPos, Vector3 headForward, float distance, float belowGazeDeg)
        {
            var f = headForward.sqrMagnitude > 1e-6f ? headForward.normalized : Vector3.forward;
            var flat = Vector3.ProjectOnPlane(f, Vector3.up);
            flat = flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.forward;
            float gazePitch = Vector3.SignedAngle(flat, f, Vector3.Cross(Vector3.up, flat));   // + = looking down
            float pitch = Mathf.Clamp(gazePitch + belowGazeDeg, -10f, 60f);
            var dir = Quaternion.AngleAxis(pitch, Vector3.Cross(Vector3.up, flat)) * flat;
            return new Pose(headPos + dir * distance, Quaternion.LookRotation(dir, Vector3.up));
        }

        public void Present(string message, ColorRole tone)
        {
            if (text == null || surface == null) return;
            // Wrap at maxWidth, at most maxLines (longer text is clipped at a word with "…" first); the pill hugs the
            // rendered text (textBounds), so a short message sits in a short pill.
            message = Copy.Clip(message, Mathf.Max(24, maxLines * 36));
            // Measure with the text live: an inactive TMP (faded out) has no mesh and no bounds.
            surface.gameObject.SetActive(true);
            text.gameObject.SetActive(true);
            if (dot != null) dot.gameObject.SetActive(true);
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.maxVisibleLines = Mathf.Max(1, maxLines);
            text.alignment = TextAlignmentOptions.Center;
            text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            text.rectTransform.sizeDelta = new Vector2(maxWidth, 1f);
            text.text = message;
            text.ForceMeshUpdate();
            var b = text.textBounds;
            float w = Mathf.Clamp(b.size.x, 0.03f, maxWidth);
            float h = Mathf.Max(b.size.y, text.fontSize * 0.1f);
            float dotRoom = dot != null ? 0.02f : 0f;
            float pillW = w + padding * 2f + dotRoom, pillH = h + padding;
            text.transform.localPosition = new Vector3(dotRoom * 0.5f - b.center.x, -b.center.y, -0.002f);
            surface.transform.localPosition = new Vector3(0f, 0f, surface.transform.localPosition.z);
            surface.SetSize(new Vector2(pillW, pillH));
            if (dot != null)
            {
                dot.customTint = true;
                dot.tint = UiTheme.Current.Color(tone);
                dot.Rebuild();
                dot.transform.localPosition = new Vector3(-pillW * 0.5f + padding * 0.75f, 0f, -0.001f);
            }
            ShownFor = Duration(message, seconds, isReply);
            float now = UiClock.Now;
            // A toast that arrives while a reply is up waits for it (one heads-up surface at a time).
            m_From = isReply || now >= m_YieldUntil ? now : m_YieldUntil;
            m_Until = m_From + ShownFor;
            if (!isReply) Presented++;
            gameObject.SetActive(true);
        }

        void LateUpdate()
        {
            if (head == null) return;
            float target = Showing ? 1f : 0f;
            float dur = UiSettings.Duration(UiTheme.Current.motion.standard);
            m_Alpha = dur <= 0f ? target : Mathf.MoveTowards(m_Alpha, target, Time.unscaledDeltaTime / dur);
            var docked = StatusLine.Current;
            if (docked != null && docked.Placed && docked.Docked && surface != null)
            {
                // Declutter M3, glass lane fix (gate capture: the fallback toast drew over the Adjust panel's Size & finish
                // row): while a main-slot window is open the toast and the reply dock on its top rim with the line, wholly
                // above it, each scaled so its own angular size holds; a toast stacks over a line that is showing.
                float s = StatusLine.DockScale(head.position, StatusLine.DockPoint(docked.DockRim, docked.DockUp), distance);
                float below = !isReply && docked.Visible && docked.surface != null
                    ? docked.surface.size.y * docked.PlacementScale + UiZones.DockLift : 0f;
                var p = StatusLine.DockCentre(docked.DockRim, docked.DockUp, head.position, surface.size.y, s, below);
                var look = p - head.position;
                float k = m_Placed ? 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime) : 1f;   // keep up with the window
                m_Pos = Vector3.Lerp(m_Pos, p, k);
                if (look.sqrMagnitude > 1e-6f) m_Rot = Quaternion.Slerp(m_Rot, Quaternion.LookRotation(look.normalized, Vector3.up), k);
                transform.localScale = Vector3.one * s;
                Docked = true;
            }
            else if ((isReply ? StatusLine.Current : null) is StatusLine line && line.Placed)
            {
                Docked = false;
                // Declutter M1: the reply takes the status line's place (and, docked, its scale).
                m_Pos = line.Placement.position;
                m_Rot = line.Placement.rotation;
                transform.localScale = Vector3.one * line.PlacementScale;
            }
            else
            {
                Docked = false;
                var pose = PoseFor(head.position, head.forward, distance, belowGazeDeg);
                float k = 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime);
                m_Pos = m_Placed ? Vector3.Lerp(m_Pos, pose.position, k) : pose.position;
                m_Rot = m_Placed ? Quaternion.Slerp(m_Rot, pose.rotation, k) : pose.rotation;
                if (transform.localScale != Vector3.one) transform.localScale = Vector3.one;
            }
            m_Placed = true;
            transform.SetPositionAndRotation(m_Pos, m_Rot);
            var theme = UiTheme.Current;
            var baseTint = theme.TierColor(surface.EffectiveTier);
            // Near-opaque behind text (Fieldglass: α ≥ 0.94), faded with the message.
            surface.SetTint(new Color(baseTint.r, baseTint.g, baseTint.b, Mathf.Max(baseTint.a, 0.94f) * m_Alpha));
            var tc = theme.colors.textPrimary;
            text.color = new Color(tc.r, tc.g, tc.b, tc.a * m_Alpha);
            if (dot != null) dot.SetTint(new Color(dot.tint.r, dot.tint.g, dot.tint.b, m_Alpha));
            bool show = m_Alpha > 0.001f;
            if (surface.gameObject.activeSelf != show) { surface.gameObject.SetActive(show); text.gameObject.SetActive(show); if (dot != null) dot.gameObject.SetActive(show); }
        }
    }
}
