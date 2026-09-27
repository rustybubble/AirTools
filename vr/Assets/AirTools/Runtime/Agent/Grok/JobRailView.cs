using AirTools.Core;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// "Do the whole job" (backend F15). Declutter M2 (DC1): the run is a mode of the one heads-up line — this view is
    /// the model → text bridge that pushes it to the status line (StatusLine.SetProgress): line 1 "Do the whole job ·
    /// 5 of 7  ✓ ✓ ✓ – • · ·" (GrokRailText.JobStatus; glyph + UiTheme colour, never colour alone), line 2 the newest
    /// spoken result; after job_done the summary for `summarySeconds`, then NextStep has the line again. It shows with
    /// the guide rail off too, never in passthrough. The per-step results go to the notebook and the packet.
    ///
    /// `standalone` (the fallback, off) draws the old heads-up rail instead: one dot per step on a vertical spine, each
    /// with its name and spoken result, the newest spoken line in full, then a summary chip; 0.9 m, 14° below the gaze
    /// and 25° to the left (38° while a main-slot window is open). Built by GrokRailsBuilder with UiBuild; draws
    /// GrokRails.Job.
    public class JobRailView : MonoBehaviour
    {
        [Tooltip("Declutter M2 (DC1): off = the run is the status line's progress (the job strip); on = the old heads-up rail (fallback).")]
        public bool standalone;
        [Tooltip("The one heads-up line (else StatusLine.Current).")]
        public StatusLine status;
        public Transform head;
        public GameObject content;
        public GlassSurface panel;
        public TextMeshPro title;
        public TextMeshPro count;
        public GlassSurface spine;
        public GlassSurface[] dots;
        public TextMeshPro[] glyphs;
        public TextMeshPro[] names;
        public TextMeshPro[] results;
        public TextMeshPro caption;
        public GlassSurface summary;
        public GlassSurface summaryDot;
        public TextMeshPro summaryText;
        public TextMeshPro detailText;

        public float distance = 0.9f;
        [Tooltip("Degrees below the gaze.")]
        public float belowGazeDeg = 14f;
        [Tooltip("Degrees to the side (negative = left).")]
        public float yawDeg = -25f;
        [Tooltip("While a main-slot window is open (e.g. the pay panel at the end of the run), the rail steps aside to here.")]
        public float yawDegBesideWindow = -38f;
        public float width = 0.32f;
        public float padding = 0.022f;
        public float rowHeight = 0.03f;
        public float dotSize = 0.016f;
        [Tooltip("Seconds the summary stays after job_done before the rail fades.")]
        public float summarySeconds = 8f;
        public float fadeSeconds = 0.6f;

        public bool Visible => m_Alpha > 0.01f;
        /// The rail's own panel is (fading) up: what the declutter census counts (never, unless standalone).
        public bool Showing => standalone && m_Target > 0f;
        public int Layouts { get; private set; }
        public string ShownRun { get; private set; }
        /// What this view last pushed to the status line (line 1 is rich text; "" when nothing).
        public string Line1 { get; private set; } = "";
        public string Line2 { get; private set; } = "";
        /// The run's strip as last pushed, plain ("✓ ✓ ✓ – • · ·").
        public string Strip { get; private set; } = "";

        int m_Version = -1;
        float m_Alpha = -1f, m_Target;
        Vector3 m_Pos;
        Quaternion m_Rot = Quaternion.identity;
        bool m_Placed, m_Retint;
        float m_Yaw;
        int m_Rows;

        void LateUpdate() => Refresh();

        /// The panel for a run of `steps` with a two-line caption (declutter S2 footprint; Layout sizes it at runtime).
        public Vector2 TypicalSize(int steps = 7)
        {
            float titleH = title != null ? title.fontSize * 0.1f * 1.25f : 0.02f;
            float captionH = caption != null ? caption.fontSize * 0.1f * 1.3f * 2f : 0.042f;
            return new Vector2(width, padding + titleH + 0.008f + steps * rowHeight + 0.006f + captionH + 0.008f + padding * 0.6f);
        }

        /// Declutter S1: the job's progress shows in the world and on the table, never in passthrough. Pure.
        public static bool ShowsIn(AppMode mode) => mode != AppMode.Passthrough;

        /// Show / fade the rail for the run and redraw it when it changed (every frame; the harness calls it too).
        public void Refresh()
        {
            var job = GrokRails.Job;
            double now = Time.realtimeSinceStartupAsDouble;
            // Declutter S1: never in passthrough (a run left over from the last judge must not greet the next one).
            float target = job == null || !ShowsIn(AppState.Mode) ? 0f : JobRailTiming.Alpha(now, job.DoneAt, summarySeconds, fadeSeconds);
            m_Target = target;
            if (!standalone) { Push(job, target); return; }
            if (target > 0f && content != null && !content.activeSelf) { content.SetActive(true); m_Version = -1; }
            if (target > 0f && GrokRails.Version != m_Version) { m_Version = GrokRails.Version; Layout(job); }
            Place();
            float dur = UiSettings.Duration(UiTheme.Current.motion.standard);
            float a = dur <= 0f ? target : Mathf.MoveTowards(Mathf.Max(0f, m_Alpha), target, Time.unscaledDeltaTime / dur);
            if (m_Retint || !Mathf.Approximately(a, m_Alpha) || job != null && !job.Finished) Tint(job, a, now);
            m_Retint = false;
            m_Alpha = a;
            if (content != null && content.activeSelf && a <= 0.001f && target <= 0f) content.SetActive(false);
        }

        int m_PushedVersion = -1;
        bool m_Pushed;

        /// Declutter M2: the run as the status line's progress, re-pushed when the rails change (no per-frame work);
        /// cleared once the summary's time is up, in passthrough, or when the run is gone.
        void Push(JobRunModel job, float target)
        {
            if (content != null && content.activeSelf) content.SetActive(false);
            var line = status != null ? status : StatusLine.Current;
            if (job == null || target <= 0f)
            {
                if (m_Pushed && line != null) line.ClearProgress();
                m_Pushed = false;
                m_PushedVersion = -1;
                Line1 = Line2 = Strip = "";
                return;
            }
            if (m_Pushed && GrokRails.Version == m_PushedVersion) return;
            m_PushedVersion = GrokRails.Version;
            m_Pushed = true;
            Layouts++;
            ShownRun = job.RunId;
            Line1 = GrokRailText.JobStatus(job, Hex);
            Line2 = GrokRailText.JobStatusDetail(job);
            Strip = GrokRailText.JobStrip(job);
            var tone = !job.Finished ? GrokRailTones.Info : GrokRailTones.Summary(job);
            if (line != null) line.SetProgress(Line1, Line2, tone);
        }

        /// A colour role as TMP's RRGGBB.
        static string Hex(ColorRole role) => ColorUtility.ToHtmlStringRGB(UiTheme.Current.Color(role));

        void Place()
        {
            if (head == null) return;
            // Step aside while the Wave 0 window slot is in use, so the rail never covers a window's left edge.
            float yaw = WindowSlot.Current != null ? yawDegBesideWindow : yawDeg;
            m_Yaw = m_Placed ? Mathf.MoveTowards(m_Yaw, yaw, 60f * Time.unscaledDeltaTime) : yaw;
            var fwd = Quaternion.AngleAxis(m_Yaw, Vector3.up) * head.forward;
            var pose = UiToast.PoseFor(head.position, fwd, distance, belowGazeDeg);
            float k = 1f - Mathf.Exp(-5f * Time.unscaledDeltaTime);
            m_Pos = m_Placed ? Vector3.Lerp(m_Pos, pose.position, k) : pose.position;
            m_Rot = m_Placed ? Quaternion.Slerp(m_Rot, pose.rotation, k) : pose.rotation;
            m_Placed = true;
            transform.SetPositionAndRotation(m_Pos, m_Rot);
        }

        /// Rows, caption and summary for the run; the panel hugs them.
        public void Layout(JobRunModel job)
        {
            if (job == null || panel == null) return;
            Layouts++;
            ShownRun = job.RunId;
            float left = -width * 0.5f + padding, right = width * 0.5f - padding, inner = right - left;
            float y = -padding;
            Set(title, GrokRailText.Title(job));   // autonomy: "Replace the dishwasher"
            Set(count, UiText.Tabular(GrokRailText.JobCount(job)));
            float titleH = Em(title) * 1.25f;
            Top(title, left, y, inner * 0.7f, TextAlignmentOptions.TopLeft);
            Top(count, right, y, inner * 0.3f, TextAlignmentOptions.TopRight);
            y -= titleH + 0.008f;

            m_Rows = Mathf.Min(job.Dots.Count, dots?.Length ?? 0);
            float rowTop = y;
            float textX = left + dotSize + 0.012f;
            for (int k = 0; k < (dots?.Length ?? 0); k++)
            {
                bool on = k < m_Rows;
                Show(dots[k], on); Show(glyphs, k, on); Show(names, k, on); Show(results, k, on);
                if (!on) continue;
                var d = job.Dots[k];
                float cy = y - rowHeight * 0.5f;
                dots[k].transform.localPosition = new Vector3(left + dotSize * 0.5f, cy, -0.001f);
                if (glyphs != null && glyphs[k] != null)
                {
                    glyphs[k].text = GrokRailTones.Glyph(job.Display(k));
                    glyphs[k].transform.localPosition = new Vector3(left + dotSize * 0.5f, cy, -0.002f);
                }
                if (names != null && names[k] != null)
                {
                    names[k].text = GrokRailText.StepName(d.Name);
                    Mid(names[k], textX, cy, 0.075f);
                }
                if (results != null && results[k] != null)
                {
                    results[k].text = Copy.Clean(d.Spoken ?? (job.IsActive(k) ? "…" : ""));
                    Mid(results[k], textX + 0.078f, cy, right - (textX + 0.078f));
                }
                y -= rowHeight;
            }
            if (spine != null)
            {
                float h = Mathf.Max(0.001f, (m_Rows - 1) * rowHeight);
                spine.gameObject.SetActive(m_Rows > 1);
                spine.SetSize(new Vector2(0.0018f, h));
                spine.transform.localPosition = new Vector3(left + dotSize * 0.5f, rowTop - rowHeight * 0.5f - h * 0.5f, -0.0005f);
            }
            y -= 0.006f;

            // The newest spoken line in full (≤ 2 lines).
            string cap = Copy.Clean(job.Caption);
            Show(caption, cap.Length > 0);
            if (cap.Length > 0)
            {
                caption.text = cap;
                Top(caption, left, y, inner, TextAlignmentOptions.TopLeft, 2);
                y -= Height(caption) + 0.008f;
            }

            // job_done: the summary chip (tone dot + words) and what's next.
            bool done = job.Finished;
            Show(summary, done); Show(summaryDot, done); Show(summaryText, done);
            string detail = done ? GrokRailText.JobDetail(job.Done, job) : "";
            Show(detailText, detail.Length > 0);
            if (done && summary != null && summaryText != null)
            {
                summaryText.text = UiText.Tabular(GrokRailText.JobSummary(job.Done, job));
                float chipX = left + 0.02f;
                Top(summaryText, chipX, y - 0.006f, inner - 0.03f, TextAlignmentOptions.TopLeft, 2);
                float th = Height(summaryText);
                float chipH = th + 0.012f;
                summary.SetSize(new Vector2(inner, chipH));
                summary.transform.localPosition = new Vector3(0f, y - chipH * 0.5f, -0.0008f);
                if (summaryDot != null)
                {
                    summaryDot.customTint = true;
                    summaryDot.tint = UiTheme.Current.Color(GrokRailTones.Summary(job));
                    summaryDot.Rebuild();
                    summaryDot.transform.localPosition = new Vector3(left + 0.01f, y - chipH * 0.5f, -0.0012f);
                }
                y -= chipH + 0.006f;
                if (detail.Length > 0)
                {
                    detailText.text = UiText.Tabular(detail);
                    Top(detailText, left, y, inner, TextAlignmentOptions.TopLeft, 2);
                    y -= Height(detailText) + 0.004f;
                }
            }
            float total = -y + padding * 0.6f;
            panel.SetSize(new Vector2(width, total));
            // Content hangs from the panel's top edge; centre the panel on the placement point.
            panel.transform.localPosition = new Vector3(0f, -total * 0.5f, panel.transform.localPosition.z);
            if (content != null) content.transform.localPosition = new Vector3(0f, total * 0.5f, 0f);
            m_Retint = true;   // new rows take the current alpha
        }

        /// Tints with the fade (heads-up surfaces keep α ≥ 0.94 behind text, Fieldglass), and the working dots pulse.
        void Tint(JobRunModel job, float a, double now)
        {
            var theme = UiTheme.Current;
            if (panel != null)
            {
                var b = theme.TierColor(panel.EffectiveTier);
                panel.SetTint(new Color(b.r, b.g, b.b, Mathf.Max(b.a, 0.94f) * a));
            }
            Fade(title, ColorRole.TextPrimary, a);
            Fade(count, ColorRole.TextSecondary, a);
            Fade(caption, ColorRole.TextPrimary, a);
            Fade(summaryText, ColorRole.TextPrimary, a);
            Fade(detailText, ColorRole.TextSecondary, a);
            if (spine != null) spine.SetTint(Alpha(theme.Color(ColorRole.Separator), a));
            if (summary != null) summary.SetTint(Alpha(theme.Color(ColorRole.Control), a));
            if (summaryDot != null && job != null) summaryDot.SetTint(Alpha(theme.Color(GrokRailTones.Summary(job)), a));
            if (job == null || dots == null) return;
            float pulse = 0.55f + 0.45f * Mathf.Sin((float)now * Mathf.PI * 1.6f) * (UiSettings.ReducedMotion ? 0f : 1f);
            for (int k = 0; k < m_Rows && k < dots.Length; k++)
            {
                var st = job.Display(k);
                var c = theme.Color(GrokRailTones.Dot(st));
                dots[k].SetTint(Alpha(c, a * (st == DotStatus.Active ? pulse : 1f)));
                if (glyphs != null && k < glyphs.Length) Fade(glyphs[k], ColorRole.Background, a);
                if (names != null && k < names.Length)
                    Fade(names[k], st == DotStatus.Pending || st == DotStatus.Skipped ? ColorRole.TextSecondary : ColorRole.TextPrimary, a);
                if (results != null && k < results.Length) Fade(results[k], ColorRole.TextSecondary, a);
            }
        }

        static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, c.a * a);

        static void Fade(TextMeshPro t, ColorRole role, float a)
        {
            if (t == null) return;
            var c = UiTheme.Current.Color(role);
            t.color = new Color(c.r, c.g, c.b, c.a * a);
        }

        static void Set(TextMeshPro t, string s) { if (t != null && t.text != s) t.text = s; }

        static void Show(Component c, bool on) { if (c != null && c.gameObject.activeSelf != on) c.gameObject.SetActive(on); }

        static void Show(TextMeshPro[] a, int k, bool on) { if (a != null && k < a.Length) Show(a[k], on); }

        /// The em of a text in metres (UiText: font size = em / 0.1).
        static float Em(TextMeshPro t) => t == null ? 0f : t.fontSize * 0.1f;

        /// A text box hanging from (x, y) (its top-left or top-right corner), wrapping at `w`, at most `lines` lines.
        static void Top(TextMeshPro t, float x, float y, float w, TextAlignmentOptions align, int lines = 1)
        {
            if (t == null) return;
            bool rightAligned = align == TextAlignmentOptions.TopRight;
            t.alignment = align;
            t.textWrappingMode = lines > 1 ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.maxVisibleLines = Mathf.Max(1, lines);
            t.rectTransform.pivot = new Vector2(rightAligned ? 1f : 0f, 1f);
            t.rectTransform.sizeDelta = new Vector2(w, t.fontSize * 0.1f * 1.35f * lines);
            t.transform.localPosition = new Vector3(x, y, -0.002f);
        }

        /// One line vertically centred on y, clipped with "…".
        static void Mid(TextMeshPro t, float x, float y, float w)
        {
            t.alignment = TextAlignmentOptions.Left;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.maxVisibleLines = 1;
            t.rectTransform.pivot = new Vector2(0f, 0.5f);
            t.rectTransform.sizeDelta = new Vector2(Mathf.Max(0.01f, w), t.fontSize * 0.1f * 1.3f);
            t.transform.localPosition = new Vector3(x, y, -0.002f);
        }

        /// The rendered height of a text laid out by Top (live text only).
        static float Height(TextMeshPro t)
        {
            if (t == null || string.IsNullOrEmpty(t.text)) return 0f;
            t.ForceMeshUpdate();
            float line = t.fontSize * 0.1f * 1.3f;
            int lines = Mathf.Clamp(t.textInfo.lineCount, 1, Mathf.Max(1, t.maxVisibleLines));
            return Mathf.Max(line, Mathf.Min(t.textBounds.size.y, line * lines));
        }
    }
}
