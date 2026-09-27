using AirTools.Core;
using AirTools.UI;
using TMPro;
using UnityEngine;

namespace AirTools.Agent.Grok
{
    /// The install coach card (backend F17): "Install · Midea MAW08U1QWT", a rail with one dot per step, the step's
    /// words (`say`), "Manual p. 13 · “quote”" with Open manual, the step's checks as chips (grey → green ✓ / amber ! /
    /// blue ?), the verdict, the backend's label note verbatim, and four hands-only buttons — Check it, Back, Repeat,
    /// Next — that send those words as normal /agent/command commands (the push-to-talk path, with every coach fast
    /// path), so a judge can drive the coach without speaking. After coach_done: steps checked by camera vs on your word.
    ///
    /// The left side slot (DC4, declutter M4; never the Wave 0 main slot): 0.5 m, 35° left, its top edge 15° below the
    /// eye line, hanging down toward the hands; movable with its grab bar. It no longer steps aside for a main-slot
    /// window (its inner edge meets a 0.36 m main window by < 1°, and the side slot sits 5 cm behind it); Find parts,
    /// which shares the slot, yields while the card is open (PartsBrowser.YieldsTo). Built by GrokRailsBuilder with
    /// UiBuild.
    public class CoachRailView : MonoBehaviour
    {
        public FloatingWindow window;
        public GlassSurface panel;
        public TextMeshPro title;
        public TextMeshPro stepLine;
        public GlassSurface spine;
        public GlassSurface[] dots;
        public TextMeshPro[] glyphs;
        public TextMeshPro say;
        public TextMeshPro manual;
        public GlassButton openManual;
        public GlassSurface[] chipRows;
        public GlassSurface[] chipDots;
        public TextMeshPro[] chipTexts;
        public TextMeshPro verdict;
        public TextMeshPro summary;
        public GlassButton checkIt, back, repeat, next, close;
        public TextMeshPro note;

        public float width = 0.25f;
        public float padding = 0.014f;
        public float dotSize = 0.012f;
        public float dotGap = 0.008f;
        [Tooltip("Seconds the card stays after coach_done.")]
        public float doneSeconds = 12f;
        [Tooltip("The card's slot: degrees to the side (negative = left). Declutter SideLeft: −35°.")]
        public float yawDeg = -35f;

        public int Layouts { get; private set; }
        public bool IsOpen => window != null && window.IsOpen;
        public float PanelHeight { get; private set; }

        int m_Version = -1;
        string m_Dismissed;
        int m_DismissedAt = -1;
        double m_DoneAt = -1;

        void OnEnable()
        {
            Services.Register(this);
            GrokRails.Sender ??= AppCommands.SendCommand;   // the rail's words go out as normal /agent/command commands
            Hook(checkIt, OnCheck); Hook(back, OnBack); Hook(repeat, OnRepeat); Hook(next, OnNext);
            Hook(openManual, OnManual); Hook(close, OnClose);
        }

        void OnDisable()
        {
            Services.Unregister(this);
            Unhook(checkIt, OnCheck); Unhook(back, OnBack); Unhook(repeat, OnRepeat); Unhook(next, OnNext);
            Unhook(openManual, OnManual); Unhook(close, OnClose);
        }

        static void Hook(GlassButton b, System.Action a) { if (b != null) b.Clicked += a; }
        static void Unhook(GlassButton b, System.Action a) { if (b != null) b.Clicked -= a; }

        void OnCheck() => Press(GrokRails.CheckIt);
        void OnBack() => Press(GrokRails.Back);
        void OnRepeat() => Press(GrokRails.Repeat);
        void OnNext() => Press(GrokRails.Next);

        /// A rail button: the word goes out as a normal command (GrokRails.Send → AppCommands.SendCommand).
        public bool Press(string word)
        {
            bool ok = GrokRails.Send(word);
            Log.Info($"Coach button \"{word}\" → {(ok ? "sent" : "not sent")}");
            return ok;
        }

        void OnManual()
        {
            var s = GrokRails.Coach?.Step;
            if (s == null || string.IsNullOrEmpty(s.pdf_url)) return;
            var url = GrokRailText.Absolute(ServerConfig.Current, s.pdf_url);
            GrokRails.LastOpenedUrl = url;   // always recorded (the harness and DemoMode read it)
            Log.Info($"Open manual: {url}");
            // On the headset the Quest browser opens it; in the Editor and in DemoMode (stage-safe) only the URL is kept.
            bool open = Application.isPlaying && !Application.isEditor && !AirTools.Core.DemoMode.On;   // D7: DemoMode saves the URL instead of opening a browser
            UiToast.Show(open ? $"Opening the manual{(s.page.HasValue ? $" at p. {s.page.Value}" : "")}…" : "Manual link saved", GrokRailTones.Info);
            if (open) Application.OpenURL(url);
        }

        /// Close hides the card until the coach moves again (a new step, check or stop reopens it).
        void OnClose()
        {
            m_Dismissed = GrokRails.Coach?.CoachId;
            m_DismissedAt = GrokRails.Coach?.Updates ?? -1;
            window?.Close();
        }

        /// switchclean: a world-model switch closes the card the way its Close does (until the coach moves again).
        public void Dismiss() => OnClose();

        void LateUpdate() => Refresh();

        /// Open / close the card for the coach and redraw it when it changed (every frame; the harness calls it too).
        public void Refresh()
        {
            var m = GrokRails.Coach;
            double now = Time.realtimeSinceStartupAsDouble;
            if (m != null && m.Finished && m_DoneAt < 0) m_DoneAt = now;
            if (m == null || !m.Finished) m_DoneAt = -1;
            bool dismissed = m != null && m.CoachId == m_Dismissed && m.Updates == m_DismissedAt;
            bool want = m != null && !dismissed && (!m.Finished || now - m_DoneAt < doneSeconds);
            want &= AppState.Mode != AppMode.Passthrough;   // Declutter A (S1): never in passthrough (a coach left over from the last judge)
            if (window == null) return;
            if (want && !window.IsOpen) { window.yawDeg = yawDeg; window.Open(); m_Version = -1; }
            else if (!want && window.IsOpen) window.Close();
            if (!want) return;
            if (m_Version != GrokRails.Version)
            {
                m_Version = GrokRails.Version;
                m_Dismissed = null;
                Layout(m);
            }
            else if (m.Current >= 0 && m.Dot(m.Current) == DotStatus.Active) Pulse(m, now);
        }

        /// The card for the coach as it is; the panel hugs its content (hanging from the top edge).
        public void Layout(CoachSessionModel m)
        {
            if (m == null || panel == null) return;
            Layouts++;
            float left = -width * 0.5f + padding, right = width * 0.5f - padding, inner = right - left;
            float y = -padding;

            title.text = Copy.Clean(GrokRailText.CoachTitle(m));
            Top(title, left, y, inner - 0.07f, TextAlignmentOptions.TopLeft, 1);
            if (close != null) close.transform.localPosition = new Vector3(right - 0.028f, y - 0.011f, 0f);
            y -= Height(title) + 0.006f;

            // The rail: one dot per step (as many as fit), and "Step 3 of 8 · drill" beside it.
            int n = Mathf.Min(m.DotCount, dots?.Length ?? 0);
            float lineH = stepLine != null ? stepLine.fontSize * 0.1f * 1.25f : dotSize;
            float rowH = Mathf.Max(dotSize * 1.25f, lineH);
            float cy = y - rowH * 0.5f;
            float railW = n * dotSize + Mathf.Max(0, n - 1) * dotGap;
            if (stepLine != null)
            {
                stepLine.text = UiText.Tabular(GrokRailText.CoachStepLine(m));
                Top(stepLine, right, cy + lineH * 0.5f, Mathf.Max(0.02f, inner - railW - 0.01f), TextAlignmentOptions.TopRight, 1);
            }
            for (int k = 0; k < (dots?.Length ?? 0); k++)
            {
                bool on = k < n;
                Show(dots[k], on);
                if (glyphs != null && k < glyphs.Length) Show(glyphs[k], on);
                if (!on) continue;
                float x = left + dotSize * 0.5f + k * (dotSize + dotGap);
                dots[k].transform.localPosition = new Vector3(x, cy, -0.001f);
                var st = m.Dot(k);
                float s = st == DotStatus.Active || st == DotStatus.Stopped ? 1.25f : 1f;
                dots[k].transform.localScale = new Vector3(s, s, 1f);
                if (glyphs != null && k < glyphs.Length && glyphs[k] != null)
                {
                    glyphs[k].text = GrokRailTones.Glyph(st);
                    glyphs[k].transform.localPosition = new Vector3(x, cy, -0.002f);
                }
            }
            if (spine != null)
            {
                float w = Mathf.Max(0.001f, (n - 1) * (dotSize + dotGap));
                spine.gameObject.SetActive(n > 1);
                spine.SetSize(new Vector2(w, 0.0016f));
                spine.transform.localPosition = new Vector3(left + dotSize * 0.5f + w * 0.5f, cy, -0.0005f);
            }
            y -= rowH + 0.006f;

            bool done = m.Finished;
            var step = m.Step;
            // The step's words (also spoken by the backend).
            string words = done ? "" : Copy.Clean(step?.say ?? (m.Current >= 0 && m.Current < m.Steps.Count ? m.Steps[m.Current].say : ""));
            Show(say, words.Length > 0);
            if (words.Length > 0)
            {
                say.text = words;
                Top(say, left, y, inner, TextAlignmentOptions.TopLeft, 4);   // spoken in full; the card keeps 4 lines
                y -= Height(say) + 0.006f;
            }

            // Manual p. N · “quote” + Open manual (a manual step only).
            string man = done ? "" : GrokRailText.ManualLine(step);
            bool hasPdf = !done && step != null && !string.IsNullOrEmpty(step.pdf_url);
            Show(manual, man.Length > 0);
            if (openManual != null) Show(openManual, hasPdf);
            if (man.Length > 0 || hasPdf)
            {
                float bw = hasPdf && openManual != null && openManual.surface != null ? openManual.surface.size.x + 0.006f : 0f;
                float rowTop = y;
                if (man.Length > 0)
                {
                    manual.text = man;
                    Top(manual, left, y, inner - bw, TextAlignmentOptions.TopLeft, 2);
                    y -= Mathf.Max(Height(manual), hasPdf ? 0.028f : 0f) + 0.006f;
                }
                else y -= 0.034f;
                if (hasPdf) openManual.transform.localPosition = new Vector3(right - (bw - 0.006f) * 0.5f, rowTop - 0.014f, 0f);
            }

            // Checks as chips (then their answers), or the summary once the coach is done.
            int chips = done ? 0 : Mathf.Min(m.Chips.Count, chipRows?.Length ?? 0);
            for (int k = 0; k < (chipRows?.Length ?? 0); k++)
            {
                bool on = k < chips;
                Show(chipRows[k], on);
                if (chipDots != null && k < chipDots.Length) Show(chipDots[k], on);
                if (chipTexts != null && k < chipTexts.Length) Show(chipTexts[k], on);
                if (!on) continue;
                var chip = m.Chips[k];
                var t = chipTexts[k];
                t.text = Copy.Clean(GrokRailText.Chip(chip));
                Top(t, left + 0.018f, y - 0.005f, inner - 0.024f, TextAlignmentOptions.TopLeft, 2);
                float h = Height(t) + 0.01f;
                chipRows[k].SetSize(new Vector2(inner, h));
                chipRows[k].transform.localPosition = new Vector3(0f, y - h * 0.5f, 0.001f);
                if (chipDots != null && k < chipDots.Length && chipDots[k] != null)
                    chipDots[k].transform.localPosition = new Vector3(left + 0.009f, y - h * 0.5f, -0.0008f);
                y -= h + 0.004f;
            }

            string v = done ? "" : GrokRailText.VerdictLine(m);
            Show(verdict, v.Length > 0);
            if (v.Length > 0)
            {
                verdict.text = Copy.Clean(v);
                Top(verdict, left, y - 0.002f, inner, TextAlignmentOptions.TopLeft, 2);
                y -= Height(verdict) + 0.008f;
            }

            string sum = done ? GrokRailText.CoachSummary(m.Done, m.DotCount) : "";
            Show(summary, sum.Length > 0);
            if (sum.Length > 0)
            {
                summary.text = UiText.Tabular(sum);
                Top(summary, left, y, inner, TextAlignmentOptions.TopLeft, 3);
                y -= Height(summary) + 0.008f;
            }

            // Hands-only controls: Check it · Back · Repeat · Next (hidden once done).
            var row = new[] { checkIt, back, repeat, next };
            float bwid = (inner - 3 * 0.006f) / 4f;
            float bh = checkIt != null && checkIt.surface != null ? checkIt.surface.size.y : 0.03f;
            for (int k = 0; k < row.Length; k++)
            {
                if (row[k] == null) continue;
                Show(row[k], !done);
                row[k].transform.localPosition = new Vector3(left + bwid * 0.5f + k * (bwid + 0.006f), y - bh * 0.5f, 0f);
            }
            if (!done) y -= bh + 0.008f;

            // The backend's honesty note, verbatim.
            string noteText = m.LabelNote ?? "";
            Show(note, noteText.Length > 0);
            if (noteText.Length > 0)
            {
                note.text = noteText;
                Top(note, left, y, inner, TextAlignmentOptions.TopLeft, 2);
                y -= Height(note) + 0.004f;
            }

            PanelHeight = -y + padding * 0.6f;
            panel.SetSize(new Vector2(width, PanelHeight));
            panel.transform.localPosition = new Vector3(0f, -PanelHeight * 0.5f, panel.transform.localPosition.z);
            var handle = window != null ? window.handle : null;
            if (handle != null) handle.transform.localPosition = new Vector3(0f, -PanelHeight - 0.014f, 0f);
            Tint(m, Time.realtimeSinceStartupAsDouble);
        }

        /// The current dot breathes while its step is up (no allocation; dots only).
        void Pulse(CoachSessionModel m, double now)
        {
            var theme = UiTheme.Current;
            float pulse = UiSettings.ReducedMotion ? 1f : 0.6f + 0.4f * Mathf.Sin((float)now * Mathf.PI * 1.6f);
            for (int k = 0; k < (dots?.Length ?? 0); k++)
            {
                if (dots[k] == null || !dots[k].gameObject.activeSelf) continue;
                var st = m.Dot(k);
                var c = theme.Color(GrokRailTones.Dot(st));
                dots[k].SetTint(new Color(c.r, c.g, c.b, c.a * (st == DotStatus.Active ? pulse : 1f)));
            }
        }

        void Tint(CoachSessionModel m, double now)
        {
            var theme = UiTheme.Current;
            Pulse(m, now);
            for (int k = 0; k < (glyphs?.Length ?? 0); k++)
                if (glyphs[k] != null) glyphs[k].color = theme.Color(ColorRole.Background);
            for (int k = 0; k < (chipDots?.Length ?? 0) && k < m.Chips.Count; k++)
                if (chipDots[k] != null) chipDots[k].SetTint(theme.Color(GrokRailTones.Chip(m.Chips[k].State)));
            if (verdict != null) verdict.color = theme.Color(m.Paused ? ColorRole.Danger : GrokRailTones.Verdict(m.Verdict));
            if (stepLine != null) stepLine.color = theme.Color(m.Paused ? ColorRole.Danger : ColorRole.TextSecondary);
            if (spine != null) spine.SetTint(theme.Color(ColorRole.Separator));
        }

        static void Show(Component c, bool on) { if (c != null && c.gameObject.activeSelf != on) c.gameObject.SetActive(on); }

        static void Top(TextMeshPro t, float x, float y, float w, TextAlignmentOptions align, int lines)
        {
            if (t == null) return;
            t.alignment = align;
            t.textWrappingMode = lines > 1 ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.maxVisibleLines = Mathf.Max(1, lines);
            t.rectTransform.pivot = new Vector2(align == TextAlignmentOptions.TopRight ? 1f : 0f, 1f);
            t.rectTransform.sizeDelta = new Vector2(Mathf.Max(0.01f, w), t.fontSize * 0.1f * 1.35f * lines);
            t.transform.localPosition = new Vector3(x, y, -0.002f);
        }

        static float Height(TextMeshPro t)
        {
            if (t == null || string.IsNullOrEmpty(t.text) || !t.gameObject.activeInHierarchy) return t == null ? 0f : t.fontSize * 0.1f * 1.25f;
            t.ForceMeshUpdate();
            float line = t.fontSize * 0.1f * 1.3f;
            int lines = Mathf.Clamp(t.textInfo.lineCount, 1, Mathf.Max(1, t.maxVisibleLines));
            return Mathf.Max(line, Mathf.Min(t.textBounds.size.y, line * lines));
        }
    }
}
