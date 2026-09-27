using UnityEngine;

namespace AirTools.UI
{
    /// Words for inputs that did nothing (UX W0.5 / W0.9 "every no says why"): a miss, a teleport onto a wall, a
    /// release with no surface… One hint at a time, at most one per Interval seconds, so a nervous run of pinches
    /// doesn't turn into a stream of toasts. `after` = only on the n-th consecutive failure of the same kind.
    public static class InputHints
    {
        public static float Interval = 4f;
        static float s_LastAt = -999f;
        static string s_StreakKey;
        static int s_Streak;

        public static int Shown { get; private set; }
        public static string Last { get; private set; } = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset() { s_LastAt = -999f; s_StreakKey = null; s_Streak = 0; Shown = 0; Last = ""; }

        /// A failure of kind `key`; shows `text` (and the miss feedback) when the streak and the rate allow it.
        public static bool Say(string key, string text, int after = 1)
        {
            if (key == s_StreakKey) s_Streak++;
            else { s_StreakKey = key; s_Streak = 1; }
            FeedbackEvents.Miss();
            if (s_Streak < after) return false;
            float now = Time.realtimeSinceStartup;
            if (now - s_LastAt < Interval) return false;
            s_LastAt = now;
            s_Streak = 0;
            Last = text;
            Shown++;
            UiToast.Show(text, ColorRole.Warning);
            return true;
        }

        /// Something worked: the failure streak starts again.
        public static void Succeeded(string key = null)
        {
            if (key == null || key == s_StreakKey) { s_StreakKey = null; s_Streak = 0; }
        }
    }
}
