using AirTools.UI;
using Oculus.Interaction;

namespace AirTools.Agent
{
    /// Drives one Talk GlassButton against the VoiceClient (a plain object each window owns; call Drive every frame).
    ///   • Press edges → VoiceClient.PressDown / PressUp (tap or hold: the client decides).
    ///   • Hover (ray or finger near) → VoiceClient.Prewarm, so the press keeps the words said just before it.
    ///   • While listening the button is an ink toggle that is on ("Listening… tap to send", or "Tap to send" on a
    ///     narrow button; "Release to send" while held) and its indicator bar is the live level meter; idle it is the
    ///     white primary "Tap to talk". Allocation-free per frame (text only changes on state changes).
    public sealed class TalkButton
    {
        public const string IdleText = "Tap to talk";
        /// settings-assets: the idle label follows Settings ▸ Talk — "Hold to talk" (Hold), else "Tap to talk".
        public const string HoldIdleText = "Hold to talk";
        public static string Idle(AirTools.Core.TalkStyle style) => style == AirTools.Core.TalkStyle.Hold ? HoldIdleText : IdleText;
        /// Buttons at least this wide show the full hint (More's 15 cm Talk); narrower ones the short one.
        public const float WideButton = 0.14f;
        const float MeterMin = 0.35f;

        bool m_Down, m_Listening, m_Styled, m_WasListening;
        ButtonStyle m_Style;
        string m_Line = "";

        /// This button's press started listening this frame.
        public bool Started { get; private set; }
        /// The current / last listening was started from this button.
        public bool Mine { get; private set; }
        /// Line (the window's status for this button's talk) changed this frame.
        public bool Changed { get; private set; }
        /// "Listening… tap to send" while listening from here; then "Thinking…" (sent) or "Didn't catch that"; a failed
        /// press says why ("Allow the mic, then tap Talk again").
        public string Line => m_Line;

        /// `active`: the button's window is showing (a closed window releases a hold).
        public void Drive(GlassButton b, VoiceClient v, bool active)
        {
            Started = false;
            Changed = false;
            if (v == null) return;
            bool live = active && b != null && b.isActiveAndEnabled && b.interactable;
            var state = live ? b.State : InteractableState.Normal;
            bool down = state == InteractableState.Select;
            if (live && state != InteractableState.Normal) v.Prewarm();
            if (down != m_Down)
            {
                m_Down = down;
                if (down)
                {
                    if (v.PressDown(this)) Started = true;
                    else if (!v.Recording && !string.IsNullOrEmpty(v.Status)) SetLine(Copy.VoiceStatus(v.Status));   // no mic / permission
                }
                else v.PressUp();
            }
            Mine = ReferenceEquals(v.Owner, this);
            UpdateLine(v);
            if (b != null) Show(b, v);
        }

        void UpdateLine(VoiceClient v)
        {
            bool listening = Mine && v.Recording;
            if (listening) SetLine(v.Hint);   // constant strings (the tap → hold copy)
            else if (m_WasListening) SetLine(v.LastSent ? "Thinking…" : Copy.VoiceStatus(v.Status));   // once, as it ends
            m_WasListening = listening;
        }

        void SetLine(string line)
        {
            line ??= "";
            if (line == m_Line) return;
            m_Line = line;
            Changed = true;
        }

        void Show(GlassButton b, VoiceClient v)
        {
            bool listening = v.Recording;
            if (listening != m_Listening || !m_Styled)
            {
                if (!m_Styled) { m_Style = b.style; m_Styled = true; }
                m_Listening = listening;
                // Listening = a toggle that is on (D3 ink + its bar); idle = the button as built (the white primary).
                b.style = listening ? ButtonStyle.Toggle : m_Style;
                b.SetSelected(listening);
            }
            bool wide = b.surface != null && b.surface.size.x >= WideButton;
            string text = listening ? (wide ? v.Hint : v.ShortHint) : Idle(AirTools.Core.UserPrefs.Talk);   // settings-assets
            if (b.Text != text) b.SetText(text);
            if (b.indicator != null)
            {
                float sx = 1f;
                if (listening)
                {
                    // The bar grows from a stub to the button's width with the level.
                    float max = b.surface != null && b.indicator.size.x > 1e-4f ? (b.surface.size.x - 0.012f) / b.indicator.size.x : 3f;
                    sx = MeterMin + (UnityEngine.Mathf.Max(1f, max) - MeterMin) * v.Level;
                }
                var t = b.indicator.transform;
                if (UnityEngine.Mathf.Abs(t.localScale.x - sx) > 0.02f || (!listening && t.localScale.x != 1f))
                    t.localScale = new UnityEngine.Vector3(sx, 1f, 1f);
            }
        }
    }
}
