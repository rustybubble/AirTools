using AirTools.Core;

namespace AirTools.UI
{
    /// What the one heads-up line shows (declutter M1–M3), without the view: pure, time passed in (tests run it offline).
    ///
    /// Line 1, newest-first by kind: a flash (a toast while the line is live: 2.4–6 s), else the job's progress (M2),
    /// else the Step's status. Line 2: the coach hint under the Step (or the job's newest spoken line), never under a
    /// flash, and hidden while the line is docked on a window (only line 1 then). A reply takes the whole line's place
    /// for 5–10 s (Yield): the line hides, and a flash that arrives meanwhile waits until the reply is gone.
    public sealed class StatusLineModel
    {
        public string Step { get; private set; } = "";
        public StepTone StepTone { get; private set; }
        public string Coach { get; private set; } = "";

        public string Flash { get; private set; } = "";
        public ColorRole FlashTone { get; private set; } = ColorRole.Info;
        public float FlashFrom { get; private set; } = -1f;
        public float FlashUntil { get; private set; } = -1f;
        public int Flashes { get; private set; }

        public float YieldUntil { get; private set; } = -1f;
        public int Yields { get; private set; }

        /// Declutter M2 (DC1): the job's progress ("Do the whole job · 5 of 7  ✓ ✓ ✓ – • · ·", then its summary) and
        /// the newest spoken line under it. Shows even with the guide rail off.
        public string Progress { get; private set; } = "";
        public string ProgressDetail { get; private set; } = "";
        public ColorRole ProgressTone { get; private set; } = ColorRole.Info;
        public bool InProgress => Progress.Length > 0;

        public void SetProgress(string line1, string line2, ColorRole tone)
        {
            Progress = line1 ?? "";
            ProgressDetail = line2 ?? "";
            ProgressTone = tone;
        }

        public void ClearProgress() => SetProgress("", "", ColorRole.Info);

        /// The Step's status (from GuideRail). False when nothing changed.
        public bool SetStep(string status, StepTone tone)
        {
            status ??= "";
            if (status == Step && tone == StepTone) return false;
            Step = status;
            StepTone = tone;
            return true;
        }

        /// The coach hint under the status ("" hides it). False when nothing changed.
        public bool SetCoach(string line)
        {
            line ??= "";
            if (line == Coach) return false;
            Coach = line;
            return true;
        }

        public void ClearStep()
        {
            Step = "";
            Coach = "";
        }

        /// A toast as a flash on line 1 for `seconds`, from now or, while a reply holds the line, from when it's gone.
        public void ShowFlash(string text, ColorRole tone, float seconds, float now)
        {
            Flash = text ?? "";
            FlashTone = tone;
            FlashFrom = now > YieldUntil ? now : YieldUntil;
            FlashUntil = FlashFrom + (seconds > 0f ? seconds : 0f);
            Flashes++;
        }

        public void EndFlash() { Flash = ""; FlashFrom = FlashUntil = -1f; }

        /// A reply takes the line's place for `seconds` (the line hides; it never shortens an earlier yield).
        public void Yield(float seconds, float now)
        {
            float until = now + (seconds > 0f ? seconds : 0f);
            if (until > YieldUntil) YieldUntil = until;
            Yields++;
        }

        public void EndYield() => YieldUntil = -1f;

        public bool Yielded(float now) => now < YieldUntil;
        public bool Flashing(float now) => Flash.Length > 0 && now >= FlashFrom && now < FlashUntil;

        /// Line 1 as shown now ("" = nothing on line 1): a flash, else the progress, else the Step.
        public string Line1(float now) => Flashing(now) ? Flash : InProgress ? Progress : Step;

        /// Line 2 as shown now ("" = none): the coach under the Step, the newest spoken line under the progress; nothing
        /// under a flash or while docked.
        public string Line2(float now, bool docked)
        {
            if (docked || Flashing(now)) return "";
            if (InProgress) return ProgressDetail;
            return Step.Length > 0 ? Coach : "";
        }

        /// The line is up: not yielded to a reply, and line 1 has words.
        public bool Showing(float now) => !Yielded(now) && Line1(now).Length > 0;
    }
}
