using AirTools.Parts;
using AirTools.Tools;

namespace AirTools.Core
{
    /// Where a coach line draws (spec §4). Until W1.1 (reticle) and the ring hint slot are wired, CoachService draws
    /// every anchor on the status line's second line; Pill also pulses the pill.
    public enum CoachAnchor : byte { Status, Reticle, Ring, Pill, Target }

    /// The ghost-hand loop to play with a coach line (CoachHand; a stub until the ghost hands land).
    public enum CoachPose : byte { None, PokeRight, PinchRight, PinchLeft, PointFloor, PalmUpLeft, HoldRight }

    public enum CoachChange : byte { None, Shown, Dismissed, Hidden, Replaced }

    public readonly struct CoachRule
    {
        public readonly CoachRuleId Id;
        public readonly float Delay;
        public readonly byte Max;
        public readonly CoachAnchor Anchor;
        public readonly CoachPose Pose;

        public CoachRule(CoachRuleId id, float delay, byte max, CoachAnchor anchor, CoachPose pose)
        {
            Id = id; Delay = delay; Max = max; Anchor = anchor; Pose = pose;
        }
    }

    /// What CoachService owns: shows per rule, the coach on screen, and the Step timing C07 needs. Plain data (tests
    /// build one directly).
    public sealed class CoachState
    {
        public const int Count = 15;   // CoachRuleId.None .. C14
        public readonly byte[] Shows = new byte[Count];
        public CoachRuleId Current;
        public float ShownAt = -999f, LastDismissAt = -999f;
        /// The Step on screen: its rule, whether its pill shows, and since when (C07 "Step unchanged ≥ 8 s").
        public RuleId StepRule = RuleId.R55;
        public bool StepHasPill;
        public float StepSince;

        /// GuideRail calls this with every new Step.
        public void ObserveStep(in Step step, float now)
        {
            if (step.Rule != StepRule) { StepRule = step.Rule; StepSince = now; }
            StepHasPill = step.PillVisible;
        }

        /// A step action ran (C07 is dismissed by any NextStepActions.Run).
        public void ActionRan(float now) => StepSince = now;

        public void Reset()
        {
            System.Array.Clear(Shows, 0, Shows.Length);
            Current = CoachRuleId.None;
            ShownAt = LastDismissAt = -999f;
            StepRule = RuleId.R55; StepHasPill = false; StepSince = 0f;
        }
    }

    /// UX W1.3 §4: idle-delayed coach hints. Pure (EditMode-tested). At most one on screen; priority is table order
    /// except C13, which pre-empts (a safety warning). A coach may show when its condition holds, the wearer has been
    /// idle for its delay, ≥ 3 s have passed since the last one went away, and it has been shown fewer than Max
    /// times. It stays until its condition stops holding: the success event (a pinch, a tap, the ring opening…).
    /// Never during a transition; never during a payment, a voice request or a ring spin, except C13 / C14.
    public static class CoachRules
    {
        public const float Gap = 3f, PillIdle = 8f;

        public static readonly CoachRule[] Table =
        {
            new CoachRule(CoachRuleId.C01, 4f, 2, CoachAnchor.Target, CoachPose.PokeRight),
            new CoachRule(CoachRuleId.C02, 5f, 2, CoachAnchor.Reticle, CoachPose.PointFloor),
            new CoachRule(CoachRuleId.C03, 4f, 2, CoachAnchor.Reticle, CoachPose.PinchRight),
            new CoachRule(CoachRuleId.C04, 5f, 2, CoachAnchor.Reticle, CoachPose.PinchRight),
            new CoachRule(CoachRuleId.C05, 2.5f, 2, CoachAnchor.Reticle, CoachPose.PinchLeft),
            new CoachRule(CoachRuleId.C06, 3f, 2, CoachAnchor.Reticle, CoachPose.PinchLeft),
            new CoachRule(CoachRuleId.C07, PillIdle, 2, CoachAnchor.Pill, CoachPose.PokeRight),
            new CoachRule(CoachRuleId.C08, 4f, 2, CoachAnchor.Reticle, CoachPose.PinchRight),
            new CoachRule(CoachRuleId.C09, 3f, 2, CoachAnchor.Reticle, CoachPose.PinchRight),
            new CoachRule(CoachRuleId.C10, 3f, 2, CoachAnchor.Target, CoachPose.HoldRight),
            new CoachRule(CoachRuleId.C11, 5f, 1, CoachAnchor.Pill, CoachPose.PokeRight),
            new CoachRule(CoachRuleId.C12, 6f, 2, CoachAnchor.Status, CoachPose.PalmUpLeft),
            new CoachRule(CoachRuleId.C13, 0f, 3, CoachAnchor.Ring, CoachPose.None),
            new CoachRule(CoachRuleId.C14, 3f, 2, CoachAnchor.Ring, CoachPose.None),
        };

        public static CoachRule RuleOf(CoachRuleId id) => id == CoachRuleId.None ? default : Table[(int)id - 1];

        /// The coach's condition (no idle / count / gap checks).
        public static bool Holds(CoachRuleId id, in AppSnapshot s, CoachState st)
        {
            bool world = s.Mode != AppMode.Passthrough;
            switch (id)
            {
                case CoachRuleId.C01: return s.Mode == AppMode.Passthrough && !s.EverEnteredWorld;
                case CoachRuleId.C02: return NextStep.MatchBase(s) == RuleId.R49;
                case CoachRuleId.C03: return world && s.Tool == ToolKind.Measure && s.SessionPoints == 0 && s.Shapes == 0;
                case CoachRuleId.C04: return world && s.SessionPoints == 1;
                case CoachRuleId.C05: return world && s.SessionPoints == 2 && !s.Ux.D4AutoSave && s.MeasureMode != MeasureMode.Area;
                case CoachRuleId.C06: return world && s.SessionPoints >= 3;
                case CoachRuleId.C07: return s.Mode == AppMode.World && st != null && st.StepHasPill && s.Now - st.StepSince >= PillIdle;
                case CoachRuleId.C08: return world && s.PartHeld && !s.HeldOnSurface;
                case CoachRuleId.C09: return world && s.PartHeld && s.HeldOnSurface;
                case CoachRuleId.C10: return s.Checkout == CheckoutState.Ready;
                case CoachRuleId.C11: return s.Checkout == CheckoutState.Paid;
                case CoachRuleId.C12:
                    return s.Mode == AppMode.World && s.Shapes >= 1 && s.RingOpens == 0 && !s.PartHeld && !s.SellersOpen && s.Checkout == CheckoutState.Closed;
                case CoachRuleId.C13: return s.RingOpen && s.PartHeld && !s.Ux.W04Preserves && s.Ux.D6Commit == RingCommit.Settle;
                case CoachRuleId.C14: return s.RingOpen && s.RingByController && !s.RingInteracting;
                default: return false;
            }
        }

        /// Busy moments when no coach (except C13 / C14) may show.
        public static bool Quiet(in AppSnapshot s) =>
            s.Checkout == CheckoutState.Paying || s.VoiceRecording || s.AgentBusy || s.RingInteracting;

        static bool Exempt(CoachRuleId id) => id == CoachRuleId.C13 || id == CoachRuleId.C14;

        /// May `id` come up now?
        public static bool Eligible(CoachRuleId id, in AppSnapshot s, CoachState st, bool ignoreGap = false)
        {
            if (id == CoachRuleId.None || s.Transitioning) return false;
            if (!Exempt(id) && Quiet(s)) return false;
            var rule = RuleOf(id);
            if (st != null)
            {
                if (st.Shows[(int)id] >= rule.Max) return false;
                if (!ignoreGap && s.Now - st.LastDismissAt < Gap) return false;
            }
            return s.IdleSeconds >= rule.Delay && Holds(id, s, st);
        }

        /// The coach that should come up now (None: nothing, or wait).
        public static CoachRuleId Pick(in AppSnapshot s, CoachState st)
        {
            if (s.Transitioning) return CoachRuleId.None;
            if (Eligible(CoachRuleId.C13, s, st, ignoreGap: true)) return CoachRuleId.C13;
            foreach (var r in Table)
                if (r.Id != CoachRuleId.C13 && Eligible(r.Id, s, st)) return r.Id;
            return CoachRuleId.None;
        }

        /// Advance the coach: keep the one on screen while its condition holds, dismiss it when it stops (the success
        /// event), let C13 pre-empt, else pick a new one. Updates `st` (Shows, Current, times).
        public static CoachChange Update(in AppSnapshot s, CoachState st)
        {
            float now = s.Now;
            var cur = st.Current;
            if (cur != CoachRuleId.None)
            {
                bool blocked = s.Transitioning || (!Exempt(cur) && Quiet(s));
                bool holds = Holds(cur, s, st);
                if (holds && !blocked)
                {
                    if (cur != CoachRuleId.C13 && Eligible(CoachRuleId.C13, s, st, ignoreGap: true))
                    {
                        Show(CoachRuleId.C13, st, now);
                        return CoachChange.Replaced;
                    }
                    return CoachChange.None;
                }
                st.Current = CoachRuleId.None;
                st.LastDismissAt = now;
                return holds ? CoachChange.Hidden : CoachChange.Dismissed;
            }
            var next = Pick(s, st);
            if (next == CoachRuleId.None) return CoachChange.None;
            Show(next, st, now);
            return CoachChange.Shown;
        }

        static void Show(CoachRuleId id, CoachState st, float now)
        {
            st.Current = id;
            st.ShownAt = now;
            if (st.Shows[(int)id] < byte.MaxValue) st.Shows[(int)id]++;
        }

        /// The coach line for the wearer's hands / controllers and the decisions in force.
        public static string Text(CoachRuleId id, in AppSnapshot s)
        {
            string variant = id switch
            {
                CoachRuleId.C01 or CoachRuleId.C10 => s.Ux.D5RayOnUi ? "d5" : "",
                CoachRuleId.C08 => s.LastTapeTarget != TapeTarget.Unknown ? "target" : "",
                _ => "",
            };
            return NextStepCopy.Fill(NextStepCopy.Coach(id, variant, s.Controllers), s);
        }
    }
}
