using AirTools.Core;
using UnityEngine;

namespace AirTools.UI
{
    /// UX W1.3 §4 coach: shows the CoachRules hint that applies (idle-delayed, one at a time, ≤ 2 shows each) and
    /// takes it away when it has been followed, with a ✓ pulse and the success sound. Every anchor draws on the status
    /// line's second line for now (the reticle is W1.1, the ring hint slot is ToolRing's own); a Pill anchor also
    /// highlights the pill. Counts persist in PlayerPrefs (airtools.coach.&lt;id&gt;) outside DemoMode.
    public class CoachService : MonoBehaviour
    {
        public StatusLine status;
        public NextStepPill pill;
        [Tooltip("Ghost hands (optional; a stub until the ghost-hand loops land).")]
        public CoachHand hand;
        public bool persist = true;

        const string PrefsPrefix = "airtools.coach.";

        public CoachRuleId Current { get; private set; }
        public string Text { get; private set; } = "";
        public int ShownCount { get; private set; }
        public int DismissedCount { get; private set; }

        CoachState m_State;
        bool m_Loaded, m_ShowNow;

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);
        InputModality m_TextModality;
        bool m_TextD5;

        /// Advance the coach (GuideRail calls this every 0.25 s with the frame's snapshot).
        public void Tick(in AppSnapshot s, CoachState st)
        {
            m_State = st;
            if (!m_Loaded) { Load(st, s.Ux.DemoMode); m_Loaded = true; }
            var snap = s;
            if (m_ShowNow) { snap.IdleSeconds = 999f; st.LastDismissAt = -999f; m_ShowNow = false; }
            var change = CoachRules.Update(snap, st);
            switch (change)
            {
                case CoachChange.Shown:
                case CoachChange.Replaced:
                    Present(st.Current, snap);
                    ShownCount++;
                    if (persist && !s.Ux.DemoMode) Save(st, st.Current);
                    break;
                case CoachChange.Dismissed:
                    DismissedCount++;
                    if (status != null) status.Pulse();
                    var sfx = Application.isPlaying ? SfxPlayer.Instance : null;
                    if (sfx != null && status != null) sfx.Play(SfxClips.Saved, status.transform.position, 0.5f);
                    Clear();
                    break;
                case CoachChange.Hidden:
                    Clear();
                    break;
                default:
                    // Same coach: re-word it if the hands / controllers or D5 changed.
                    if (Current != CoachRuleId.None && (m_TextModality != s.Modality || m_TextD5 != s.Ux.D5RayOnUi)) Present(Current, snap);
                    break;
            }
        }

        void Present(CoachRuleId id, in AppSnapshot s)
        {
            Current = id;
            Text = CoachRules.Text(id, s);
            m_TextModality = s.Modality;
            m_TextD5 = s.Ux.D5RayOnUi;
            var rule = CoachRules.RuleOf(id);
            if (status != null) status.SetCoach(Text);
            if (pill != null) pill.Highlight(rule.Anchor == CoachAnchor.Pill);
            if (hand != null) hand.Play(rule.Pose);
        }

        /// Take the hint off screen (no ✓).
        public void Clear()
        {
            Current = CoachRuleId.None;
            Text = "";
            if (status != null) status.SetCoach("");
            if (pill != null) pill.Highlight(false);
            if (hand != null) hand.Stop();
        }

        /// Presenter "Hint" (W1.8): the best hint right now, ignoring the idle delay and the gap.
        public void ShowNow() => m_ShowNow = true;

        // D7 (W1.8): AppCommands.ResetDemo
        /// The next person starts fresh: every hint may show again, nothing on screen. In memory only: PlayerPrefs are
        /// neither read nor written (unlike ResetShows).
        public void ResetSession()
        {
            m_State?.Reset();
            m_ShowNow = false;
            Clear();
        }

        /// "Show tips again": every hint may show twice more.
        public void ResetShows()
        {
            if (m_State != null) m_State.Reset();
            for (int i = 1; i < CoachState.Count; i++) PlayerPrefs.DeleteKey(PrefsPrefix + (CoachRuleId)i);
            Clear();
        }

        void Load(CoachState st, bool demo)
        {
            if (!persist || demo) return;
            for (int i = 1; i < CoachState.Count; i++)
                st.Shows[i] = (byte)Mathf.Clamp(PlayerPrefs.GetInt(PrefsPrefix + (CoachRuleId)i, 0), 0, 255);
        }

        static void Save(CoachState st, CoachRuleId id)
        {
            PlayerPrefs.SetInt(PrefsPrefix + id, st.Shows[(int)id]);
            PlayerPrefs.Save();
        }
    }
}
