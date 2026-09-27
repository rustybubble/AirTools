using System.Collections.Generic;

namespace AirTools.Agent.Grok
{
    /// A visual check chip: grey until checked, then green (ok), amber (not_yet) or blue (cant_see), with a glyph.
    public enum ChipState { Unchecked, Ok, NotYet, CantSee }

    public sealed class CheckChip
    {
        public string Question;
        public ChipState State;
        public string Evidence;
        /// Where the evidence is in the checked frame ([x0, y0, x1, y1], 0–1) or null.
        public float[] Box;
    }

    /// The install coach (backend F17, docs/api.md `coach_started` / `coach_step` / `coach_check` / `coach_stop` /
    /// `coach_done`): one dot per step; the current step's words, manual page and quote; its checks as chips. A
    /// coach_stop pauses the rail on that step until the coach moves ("next", or "back"). Pure: time is passed in.
    ///
    /// How each step was finished mirrors the server's record (coach.py `done`): "camera" when a check passed, "said"
    /// when the user moved on ("I did it"); the first record wins (back, then next again keeps it).
    public sealed class CoachSessionModel
    {
        public string CoachId { get; private set; }
        public string Job { get; private set; }
        public string Label { get; private set; }
        public string Source { get; private set; }
        /// The backend's honesty note ("Coaching from the manual or our own checklist. Camera checks can miss
        /// things."), shown verbatim.
        public string LabelNote { get; private set; }
        public readonly List<CoachStepInfo> Steps = new List<CoachStepInfo>();
        public int Of { get; private set; }
        public int Current { get; private set; } = -1;
        public CoachStepArgs Step { get; private set; }
        public readonly List<CheckChip> Chips = new List<CheckChip>();
        public readonly Dictionary<int, string> DoneHow = new Dictionary<int, string>();
        public CoachCheckArgs LastCheck { get; private set; }
        public bool Paused { get; private set; }
        public int PausedStep { get; private set; } = -1;
        public CoachStopArgs Stop { get; private set; }
        public CoachDoneArgs Done { get; private set; }
        public bool Finished => Done != null;
        public double UpdatedAt { get; private set; }
        public int Updates { get; private set; }

        /// A drill step is up: show the crosshair, send drill_px with "check it".
        public bool IsDrill => !Finished && Step != null && Step.tool == "drill";

        /// The verdict of the last check on the current step (null before one).
        public string Verdict => LastCheck != null && LastCheck.i == Current ? LastCheck.verdict : null;

        public static CoachSessionModel Start(CoachStartedArgs a, double now)
        {
            if (a == null || string.IsNullOrEmpty(a.coach_id)) return null;
            var m = new CoachSessionModel
            {
                CoachId = a.coach_id, Job = a.job, Label = a.label, Source = a.source, LabelNote = a.label_note, UpdatedAt = now,
            };
            if (a.steps != null) m.Steps.AddRange(a.steps);
            m.Of = m.Steps.Count;
            return m;
        }

        /// A coach_step for a coach this headset never saw start (a reconnect; "repeat that" after a restart): a rail
        /// sized by `of`, without the label note until one arrives.
        public static CoachSessionModel FromStep(CoachStepArgs s, double now)
        {
            if (s == null || string.IsNullOrEmpty(s.coach_id)) return null;
            return new CoachSessionModel { CoachId = s.coach_id, Of = s.of, UpdatedAt = now };
        }

        public bool Owns(string coachId) => string.IsNullOrEmpty(coachId) || coachId == CoachId;

        /// coach_step: moves to step `i` (next / back / repeat). Steps left forward without a passed check were the
        /// user's word. Moving off the stopped step ends the pause.
        public bool ApplyStep(CoachStepArgs s, double now)
        {
            if (s == null || !Owns(s.coach_id) || s.i < 0) return false;
            if (Finished) return false;
            if (s.of > Of) Of = s.of;
            if (s.i >= Of) Of = s.i + 1;
            if (Current >= 0 && s.i > Current)
                for (int k = Current; k < s.i; k++) Record(k, "said");
            if (Paused && s.i != PausedStep) { Paused = false; PausedStep = -1; Stop = null; }
            Current = s.i;
            Step = s;
            Chips.Clear();
            foreach (var q in s.checks ?? new List<string>()) Chips.Add(new CheckChip { Question = q, State = ChipState.Unchecked });
            LastCheck = null;
            Touch(now);
            return true;
        }

        /// coach_check: the chips take the answers; a pass records the step as checked by camera.
        public bool ApplyCheck(CoachCheckArgs c, double now)
        {
            if (c == null || !Owns(c.coach_id) || Finished) return false;
            if (c.i != Current) { Touch(now); return false; }   // a late answer about another step: ignore
            LastCheck = c;
            var results = c.results ?? new List<CoachResult>();
            for (int k = 0; k < results.Count; k++)
            {
                var r = results[k];
                var chip = ChipFor(r, k);
                if (chip == null) Chips.Add(chip = new CheckChip { Question = r.question });
                chip.State = State(r);
                chip.Evidence = r.evidence;
                chip.Box = r.box;
            }
            if (c.verdict == "passed") Record(c.i, "camera");
            Touch(now);
            return true;
        }

        /// coach_stop: a red "don't drill here" and the rail pauses on this step until the coach moves.
        public bool ApplyStop(CoachStopArgs s, double now)
        {
            if (s == null || !Owns(s.coach_id) || Finished) return false;
            Paused = true;
            PausedStep = s.i;
            Stop = s;
            Touch(now);
            return true;
        }

        /// coach_done: every step finished; the summary counts come from the server.
        public bool Finish(CoachDoneArgs d, double now)
        {
            if (d == null || !Owns(d.coach_id)) return false;
            if (Current >= 0) Record(Current, "said");
            Done = d;
            Paused = false;
            PausedStep = -1;
            Stop = null;
            Touch(now);
            return true;
        }

        public static ChipState State(CoachResult r)
        {
            if (r == null) return ChipState.Unchecked;
            if (r.ok) return ChipState.Ok;
            return r.answer == "cant_see" ? ChipState.CantSee : ChipState.NotYet;
        }

        /// The chip a result belongs to: the same question, else the same position.
        CheckChip ChipFor(CoachResult r, int index)
        {
            if (!string.IsNullOrEmpty(r.question))
                foreach (var chip in Chips) if (chip.Question == r.question) return chip;
            return index < Chips.Count ? Chips[index] : null;
        }

        void Record(int step, string how)
        {
            if (step >= 0 && !DoneHow.ContainsKey(step)) DoneHow[step] = how;
        }

        void Touch(double now) { UpdatedAt = now; Updates++; }

        public int DotCount => System.Math.Max(Of, Steps.Count);

        /// What dot k shows: the step you're on (red while paused), finished steps green (✓ by camera or on your
        /// word), the rest pending.
        public DotStatus Dot(int k)
        {
            if (!Finished && k == Current) return Paused ? DotStatus.Stopped : DotStatus.Active;   // the step you're on (also after "back")
            if (DoneHow.TryGetValue(k, out var how)) return how == "camera" ? DotStatus.Done : DotStatus.Said;
            return Finished ? DotStatus.Said : DotStatus.Pending;
        }

        public int CheckedByCamera
        {
            get { int n = 0; foreach (var v in DoneHow.Values) if (v == "camera") n++; return n; }
        }

        public int OnYourWord
        {
            get { int n = 0; foreach (var v in DoneHow.Values) if (v == "said") n++; return n; }
        }
    }
}
