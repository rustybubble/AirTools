using System;
using System.Collections.Generic;
using System.Globalization;
using AirTools.Parts;
using AirTools.Tools;

namespace AirTools.Core
{
    // UX W1.3 "Guide rail" (docs/ux/specs/W1.3-nextstep.md): one pure function NextStep.For(snapshot) → the status line,
    // the one-tap Next-step pill and its secondaries. No MonoBehaviour and no engine calls in this file (EditMode /
    // offline-testable); GuideRail (Runtime/UI) assembles the snapshot and pushes the Step to the views.

    public enum SceneKind : byte { None, Kitchen, Package, Facade }
    public enum InputModality : byte { Hands, Controllers }
    /// Explicit = every tape is finished by hand (D4 off, today); Line = a tape saves at its 2nd point; Area = shapes.
    public enum MeasureMode : byte { Explicit, Line, Area }
    public enum ReadingKind : byte { None, Tape, Area, Level, Part, Purchase, Other }
    /// PartsClient.TapeAxis "w" | "h" | "length".
    public enum TapeAxis : byte { None, W, H, Length }
    public enum FitReason : byte { None, Hits, Clearance, NotMounted, Overhang, TooWide, TooNarrow, NeedsTape }
    /// The last silent "no" (R01 overlay).
    public enum Refusal : byte
    {
        None, MeasureMiss, DuplicatePoint, FinishTooSoon, TeleportNotFlat, TeleportNothing,
        PartNoSurface, LevelMiss, NothingToUndo, ArraySingleUnit, ArrayNoTape, SellersNoPart,
        /// A shape's finish was refused: its outline crosses itself (MeasureTool.CrossRefused).
        EdgesCross,
    }
    public enum ExportResult : byte { None, Uploaded, SavedLocal, Error }
    public enum RingCommit : byte { Settle, Pinch }
    /// What happened last (argmax of the assembler's event stamps).
    public enum StepEvent : byte { None, Tape, Search, Placed, Purchase, Mode, Tool, Level }
    /// Undo / redo echo (R02 overlay).
    public enum UndoEcho : byte { None, Undo, Redo }

    /// Decisions and landed/unlanded dependencies, copied into every snapshot so each rule is tested in every variant.
    public struct UxFlags
    {
        /// D1: the tool in hand on entering the world (ToolManager.Default).
        public ToolKind D1Entry;
        /// D4: a Line tape saves at point 2 (MeasureTool.AutoSaveTwoPointTapes).
        public bool D4AutoSave;
        /// D5: UI buttons also take the ray (GlassButton.RayOnWindows).
        public bool D5RayOnUi;
        /// W0.4: a tool switch parks the tape / held part instead of discarding it.
        public bool W04Preserves;
        /// W1.6: a labelled offline receipt can stand in when the laptop is unreachable.
        public bool W16OfflineReceipt;
        /// D6: the ring commits modes on settle (today) or everything on pinch (ToolRing.CommitOnPinch).
        public RingCommit D6Commit;
        /// W1.8 DemoMode (the judge path; coach counts not persisted).
        public bool DemoMode;
        public bool GuideRailEnabled;
    }

    /// Everything the rules read. A pure value: enums, numbers and references to strings that already exist.
    public struct AppSnapshot
    {
        public float Now;
        // A · mode + scene
        public AppMode Mode;
        public bool Transitioning, EverEnteredWorld, SceneLoading, SceneFallback, ScaleCalibrated;
        /// fix-ux: the last load of StartSite failed or timed out and that site isn't what's showing (R46 "failed": Retry).
        public bool SceneLoadFailed;
        public SceneKind Scene;
        public string StartSite;
        // B · tools + measure
        public ToolKind Tool;
        public MeasureMode MeasureMode;
        public int SessionPoints, Shapes, Levels;
        public float SessionLengthM, SessionAreaM2;
        /// The placed points' outline crosses itself (MeasureMath: Path = along the placed points, Closing = only the
        /// side back to the first point); SessionAreaM2 is 0 then.
        public AirTools.Tools.OutlineCrossing SessionCrossing;
        public ReadingKind LastReading;
        public float LastTapeM, LastAreaM2, LastLevelDeg;
        public int LastTapeId;
        public TapeAxis LastTapeAxis;
        public TapeTarget LastTapeTarget;
        // C · parts
        public bool Searching, Loading, LoadFailed, CandidatesOffline, PartHeld, HeldOnSurface, HeldLost;
        public int Candidates, LastSelectedIndex, PlacedCount;
        public float SearchElapsed, HeldLostAge;
        public string LastQuery, PartName;
        public FitStatus HeldFit, SelectedFit;
        public FitReason SelectedReason;
        public bool SelectedPlaced, SelectedHasTape, ArrayAllowed;
        // D · commerce
        public bool SellersOpen, SellersRefreshing;
        public int RecommendedSeller;
        public string BestSeller;
        public float BestPrice;
        public CheckoutState Checkout;
        public bool ReceiptAuthorized;
        public float CheckoutTotal, HoldProgress;
        public int Purchases, OnTable;
        // E · UI + input
        public bool RingOpen, RingInteracting, RingByController, NotebookOpen, VoiceRecording, AgentBusy;
        public bool VoiceHold;   // voice: listening while Talk is held ("release to send"; else a tap: "tap to send")
        public int RingOpens, NotebookCount;
        public ExportResult Export;
        public float ExportAge;
        public InputModality Modality;
        public float IdleSeconds;
        public Refusal Refusal;
        public float RefusalAge;
        public UndoEcho Echo;
        public ReadingKind EchoKind;
        public float EchoValue, EchoAge;
        // F · ordering, flags
        public StepEvent Newest;
        public UxFlags Ux;

        public bool InWorld => Mode != AppMode.Passthrough;
        public bool Controllers => Modality == InputModality.Controllers;
    }

    /// One row per rule of spec §3.2, in evaluation order (first match wins; R01/R02 are status-only overlays).
    public enum RuleId : byte
    {
        R00, R01, R02, R03, R04, R05, R06, R07, R08, R09, R10, R11, R12, R13, R14, R15, R16, R17, R18, R19,
        R20, R21, R22, R23, R24, R25, R26, R27, R28, R29, R30, R31, R32, R33, R34, R35, R36, R37, R38, R39,
        R40, R41, R42, R43, R44, R45, R46, R47, R48, R49, R50, R51, R52, R53, R54, R55,
    }

    public enum CoachRuleId : byte { None, C01, C02, C03, C04, C05, C06, C07, C08, C09, C10, C11, C12, C13, C14 }

    /// Everything a step may do; each maps to one AppCommands method (NextStepActions). There is no Pay member, by
    /// design: only the physical 1 s hold on the checkout's Pay button pays (SPEC M5).
    public enum StepCommand : byte
    {
        None, OpenWorld, TakeHome, EquipTool, FindPart, SelectCandidate, ShowSellers, StartCheckout, PlaceArray,
        SetTabletop, ShowNotebook, ExportNotebook, LoadSite,
        FinishShape, Undo, ShowFindParts, CloseWindows, ShowScenePanel, UseOfflineReceipt,
        /// presence.md S1 (grow-in, landed): step into the world from the model on the table.
        StepIn,
    }

    public readonly struct StepAction
    {
        public readonly string Label;
        public readonly StepCommand Command;
        public readonly string Arg;
        public readonly int Index;
        public readonly bool Flag;

        public StepAction(string label, StepCommand command, string arg = null, int index = 0, bool flag = false)
        {
            Label = label; Command = command; Arg = arg; Index = index; Flag = flag;
        }

        public bool IsNone => Command == StepCommand.None;

        /// The argument as the command takes it ("cabinet hinge", "1", "true"; "" for none).
        public string ArgText => Command switch
        {
            StepCommand.SelectCandidate or StepCommand.StartCheckout => Index.ToString(CultureInfo.InvariantCulture),
            StepCommand.ShowNotebook or StepCommand.SetTabletop or StepCommand.ShowScenePanel => Flag ? "true" : "false",
            _ => Arg ?? "",
        };

        /// "Find hinges for this door"→FindPart(cabinet hinge)
        public override string ToString() => IsNone ? "-" : $"\"{Label}\"→{Command}({ArgText})";
    }

    [Flags] public enum StepFlags : byte { None = 0, PillHidden = 1, ScaleUnset = 2, OfflineParts = 4, Overlay = 8 }
    public enum StepTone : byte { Neutral, Busy, Success, Caution, Bad }

    public struct Step
    {
        /// The rule that set the status (R01/R02 when an overlay is up, R00 = frozen: keep the previous Step).
        public RuleId Rule;
        /// The first non-overlay rule: it owns the actions.
        public RuleId Base;
        /// Copy variant of the base rule ("d5", "settle", "wide"…; "" = default). For logs and tests.
        public string Variant;
        public string Status;
        public StepAction Primary, Secondary0, Secondary1;
        public StepTone Tone;
        public StepFlags Flags;

        public bool Frozen => Rule == RuleId.R00;
        public bool PillVisible => !Primary.IsNone && (Flags & StepFlags.PillHidden) == 0;
    }

    public static class NextStep
    {
        public const float RefusalSeconds = 2.5f, EchoSeconds = 2f, ExportSeconds = 6f, SearchQuickSeconds = 8f, HeldLostSeconds = 20f;

        /// D2 hook: one length in a status (W1.9 → Units.FormatPrimary). Never the dual Units.Format in a status.
        public static Func<double, string> Len = m => Units.Metric(m);
        public static Func<double, string> Area = m2 => m2.ToString("0.00", CultureInfo.InvariantCulture) + " m²";
        /// Same as PartFormat.Price (Parts/PartSpec.cs); GuideRail points it there.
        public static Func<float, string> Money = usd => "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);
        /// Display scrub for strings from data (seller names). GuideRail points it at Copy.Clean.
        public static Func<string, string> Clean = s => s ?? "";

        /// Back to the built-in formatters (tests: GuideRail points the hooks at Copy / PartFormat in Play mode, and
        /// statics survive when domain reload is off).
        public static void ResetHooks()
        {
            Len = m => Units.Metric(m);
            Area = m2 => m2.ToString("0.00", CultureInfo.InvariantCulture) + " m²";
            Money = usd => "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);
            Clean = s => s ?? "";
        }

        // ---------------- matching (allocation-free) ----------------

        /// The rule for this snapshot: R00 while transitioning, R01/R02 while an overlay is up, else MatchBase.
        public static RuleId Match(in AppSnapshot s)
        {
            if (s.Transitioning) return RuleId.R00;
            if (s.Refusal != Refusal.None && s.RefusalAge < RefusalSeconds) return RuleId.R01;
            if (s.Echo != UndoEcho.None && s.EchoAge < EchoSeconds) return RuleId.R02;
            return MatchBase(s);
        }

        /// The first non-overlay rule (spec §3.2 table order, first match wins).
        public static RuleId MatchBase(in AppSnapshot s)
        {
            bool passthrough = s.Mode == AppMode.Passthrough;
            if (passthrough && s.EverEnteredWorld && s.Export != ExportResult.None && s.ExportAge < ExportSeconds) return RuleId.R03;
            if (s.OnTable > 0 && (passthrough || (s.Mode == AppMode.Tabletop && s.Newest == StepEvent.Mode))) return RuleId.R04;
            if (passthrough && !s.EverEnteredWorld) return RuleId.R05;
            if (passthrough) return RuleId.R06;
            // World or Tabletop from here on.
            if (s.SceneLoading && s.Shapes == 0) return RuleId.R07;
            if (s.SceneLoadFailed && s.Shapes == 0) return RuleId.R46;   // fix-ux: not "Loading…" forever — Retry
            if (s.Checkout == CheckoutState.Paying) return RuleId.R08;
            if (s.Checkout == CheckoutState.Failed) return RuleId.R09;
            if (s.Checkout == CheckoutState.Ready) return RuleId.R10;
            if (s.Checkout == CheckoutState.Paid) return s.ReceiptAuthorized ? RuleId.R11 : RuleId.R12;
            if (s.VoiceRecording) return RuleId.R13;
            if (s.AgentBusy) return RuleId.R14;
            if (s.RingOpen && s.PartHeld && !s.Ux.W04Preserves && s.Ux.D6Commit == RingCommit.Settle) return RuleId.R15;
            if (s.RingOpen) return RuleId.R16;
            if (s.NotebookOpen) return RuleId.R17;
            if (s.Export != ExportResult.None && s.ExportAge < ExportSeconds) return RuleId.R18;
            if (s.Searching) return s.SearchElapsed < SearchQuickSeconds ? RuleId.R19 : RuleId.R20;
            if (s.Loading) return RuleId.R21;
            if (s.LoadFailed && s.Candidates > 0) return RuleId.R22;
            if (s.HeldLost && s.HeldLostAge < HeldLostSeconds && s.LastSelectedIndex >= 0 && s.Candidates > 0) return RuleId.R23;
            if (s.PartHeld)
            {
                if (!s.HeldOnSurface) return RuleId.R24;
                if (s.HeldFit == FitStatus.Green) return RuleId.R25;
                if (s.HeldFit == FitStatus.Red) return RuleId.R26;
                return RuleId.R27;
            }
            if (s.SellersOpen) return s.SellersRefreshing ? RuleId.R28 : RuleId.R29;
            if (s.SessionPoints == 1) return RuleId.R30;
            if (s.SessionPoints == 2 && (!s.Ux.D4AutoSave || s.MeasureMode == MeasureMode.Area)) return RuleId.R31;
            if (s.SessionPoints >= 3) return RuleId.R32;
            if (s.Newest == StepEvent.Purchase && s.Checkout == CheckoutState.Closed) return RuleId.R33;
            if (s.Newest == StepEvent.Placed && s.SelectedPlaced)
            {
                if (s.SelectedFit == FitStatus.Green) return s.SelectedHasTape && s.LastTapeTarget != TapeTarget.Unknown ? RuleId.R34 : RuleId.R35;
                if (s.SelectedFit == FitStatus.Red) return s.SelectedReason == FitReason.Hits ? RuleId.R36 : RuleId.R37;
                if (s.SelectedFit == FitStatus.Amber) return RuleId.R38;
            }
            if (s.Newest == StepEvent.Search)
            {
                if (s.Candidates > 0) return RuleId.R39;
                return s.CandidatesOffline ? RuleId.R40 : RuleId.R41;
            }
            if (s.Newest == StepEvent.Tape)
            {
                if (s.LastReading == ReadingKind.Tape) return NextStepCopy.QueryFor(s.LastTapeTarget) != null ? RuleId.R42 : RuleId.R43;
                if (s.LastReading == ReadingKind.Area) return RuleId.R44;
            }
            if (s.Mode == AppMode.Tabletop && (s.Newest == StepEvent.Mode || !s.EverEnteredWorld)) return RuleId.R45;
            if (s.SceneFallback && s.Shapes == 0) return RuleId.R46;
            if (!s.ScaleCalibrated && !s.Ux.DemoMode && s.Shapes == 0) return RuleId.R47;
            switch (s.Tool)
            {
                case ToolKind.Level: return RuleId.R48;
                case ToolKind.Move: return s.Shapes == 0 && s.Levels == 0 ? RuleId.R49 : RuleId.R50;
                case ToolKind.Part: return s.PartHeld ? RuleId.R55 : RuleId.R51;
                case ToolKind.Measure: return s.Shapes == 0 ? RuleId.R52 : RuleId.R53;
                case ToolKind.None: return RuleId.R54;
            }
            return RuleId.R55;
        }

        /// Changes whenever the Step For() would build changes: the matched rules, the modality, the decision flags and
        /// every displayed value quantised to its display precision. Never Now / IdleSeconds / ages (their thresholds
        /// are already in the matched rule). Allocation-free; stable within a run.
        public static long Key(in AppSnapshot s)
        {
            var rule = Match(s);
            var b = rule == RuleId.R01 || rule == RuleId.R02 ? MatchBase(s) : rule;
            unchecked
            {
                long h = ((long)rule << 8) | (long)b;
                h = Mix(h, (int)s.Modality | (s.Ux.D5RayOnUi ? 2 : 0) | (s.Ux.D4AutoSave ? 4 : 0) | ((int)s.Ux.D6Commit << 3)
                           | (s.Ux.W04Preserves ? 16 : 0) | (s.Ux.W16OfflineReceipt ? 32 : 0) | (s.Ux.DemoMode ? 64 : 0) | (s.ScaleCalibrated ? 128 : 0)
                           | (s.SceneLoadFailed ? 256 : 0));
                h = Mix(h, (int)s.Mode | ((int)s.Scene << 2) | ((int)s.Tool << 5) | ((int)s.MeasureMode << 8) | ((int)s.Newest << 10) | (s.EverEnteredWorld ? 1 << 14 : 0));
                h = Mix(h, (int)s.Refusal | ((int)s.Echo << 5) | ((int)s.EchoKind << 7) | ((int)s.Export << 10) | ((int)s.LastReading << 12) | ((int)s.LastTapeTarget << 15));
                h = Mix(h, (int)s.HeldFit | ((int)s.SelectedFit << 2) | ((int)s.SelectedReason << 4) | (s.SelectedHasTape ? 1 << 8 : 0) | (s.ArrayAllowed ? 1 << 9 : 0)
                           | (s.CandidatesOffline ? 1 << 10 : 0) | (s.ReceiptAuthorized ? 1 << 11 : 0) | (s.RingByController ? 1 << 12 : 0));
                h = Mix(h, s.Candidates); h = Mix(h, s.LastSelectedIndex); h = Mix(h, s.RecommendedSeller);
                h = Mix(h, s.NotebookCount); h = Mix(h, s.Shapes); h = Mix(h, s.Levels); h = Mix(h, s.Purchases); h = Mix(h, s.OnTable);
                h = Mix(h, Q(s.LastTapeM, 1000)); h = Mix(h, Q(s.SessionLengthM, 1000)); h = Mix(h, Q(s.SessionAreaM2, 10000));
                h = Mix(h, (int)s.SessionCrossing);
                h = Mix(h, s.VoiceHold ? 1 : 0);   // voice: the R13 variant
                h = Mix(h, Q(s.LastAreaM2, 10000)); h = Mix(h, Q(s.LastLevelDeg, 10)); h = Mix(h, Q(s.EchoValue, 1000));
                h = Mix(h, Q(s.BestPrice, 100)); h = Mix(h, Q(s.CheckoutTotal, 100));
                h = Mix(h, Str(s.LastQuery)); h = Mix(h, Str(s.BestSeller)); h = Mix(h, Str(s.StartSite));
                return h;
            }
        }

        static long Mix(long h, long v) { unchecked { return (h ^ v) * 1099511628211L + unchecked((long)0x9E3779B97F4A7C15UL); } }
        static long Q(double v, double scale) => double.IsNaN(v) ? long.MinValue : (long)Math.Round(v * scale);
        static long Str(string s) => s == null ? 0 : s.GetHashCode();

        /// Why a placed part got its verdict, from a FitReport's lists and diagnostic headline (Parts/FitChecker.cs), until
        /// W1.4 gives FitReport a Reason. Red: collisions → Hits; a tape / window size limit → TooWide / TooNarrow. Amber:
        /// "Measure the window" → NeedsTape; "Not mounted" → NotMounted; "Overhangs" → Overhang; blocked clearance →
        /// Clearance. Pure.
        public static FitReason ReasonOf(FitStatus status, int collisions, int clearanceBlocked, int supportedSamples, string headline)
        {
            var h = headline ?? "";
            if (status == FitStatus.Red)
            {
                if (collisions > 0) return FitReason.Hits;
                if (h.StartsWith("Window too wide", StringComparison.Ordinal)) return FitReason.TooNarrow;   // the unit is too small for the opening
                if (h.StartsWith("Window too narrow", StringComparison.Ordinal)) return FitReason.TooWide;
                if (h.StartsWith("Too wide", StringComparison.Ordinal)) return FitReason.TooWide;
                return FitReason.None;
            }
            if (status == FitStatus.Amber)
            {
                if (h.StartsWith("Measure the window", StringComparison.Ordinal)) return FitReason.NeedsTape;
                if (h.StartsWith("Not mounted", StringComparison.Ordinal)) return FitReason.NotMounted;
                if (h.StartsWith("Overhangs", StringComparison.Ordinal)) return FitReason.Overhang;
                if (clearanceBlocked > 0) return FitReason.Clearance;
                if (supportedSamples == 0) return FitReason.NotMounted;
                if (supportedSamples == 1) return FitReason.Overhang;
            }
            return FitReason.None;
        }

        // ---------------- the Step ----------------

        /// Formats the Step for this snapshot (allocates: GuideRail calls it only when Key changes).
        public static Step For(in AppSnapshot s)
        {
            var rule = Match(s);
            if (rule == RuleId.R00) return new Step { Rule = RuleId.R00, Base = RuleId.R00, Variant = "", Status = null };
            var b = rule == RuleId.R01 || rule == RuleId.R02 ? MatchBase(s) : rule;
            var step = Build(b, s);
            step.Rule = b;
            step.Base = b;
            if (rule == RuleId.R01)
            {
                step.Rule = RuleId.R01;
                step.Status = NextStepCopy.Fill(NextStepCopy.Status(RuleId.R01, s.Refusal.ToString(), s.Controllers), s);
                step.Tone = StepTone.Caution;
                step.Flags |= StepFlags.Overlay;
            }
            else if (rule == RuleId.R02)
            {
                step.Rule = RuleId.R02;
                step.Status = NextStepCopy.Fill(NextStepCopy.Status(RuleId.R02, EchoVariant(s), s.Controllers), s, s.EchoValue);
                step.Tone = StepTone.Neutral;
                step.Flags |= StepFlags.Overlay;
            }
            if (!s.ScaleCalibrated) step.Flags |= StepFlags.ScaleUnset;
            return step;
        }

        static string EchoVariant(in AppSnapshot s)
        {
            string verb = s.Echo == UndoEcho.Redo ? "redo" : "undo";
            switch (s.EchoKind)
            {
                case ReadingKind.Tape: return verb + ".tape";
                case ReadingKind.Area: return verb + ".area";
                case ReadingKind.Level: return verb + ".level";
                case ReadingKind.Part: return verb + ".part";
                case ReadingKind.Other: return verb + ".point";
                default: return verb;
            }
        }

        static StepAction A(string labelTemplate, StepCommand cmd, in AppSnapshot s, string arg = null, int index = 0, bool flag = false, double? len = null) =>
            new StepAction(NextStepCopy.Fill(labelTemplate, s, len), cmd, arg, index, flag);

        static Step Build(RuleId r, in AppSnapshot s)
        {
            var st = new Step { Variant = "", Tone = StepTone.Neutral };
            double len = s.LastTapeM;
            double area = s.LastAreaM2;
            bool pt = s.Mode == AppMode.Passthrough;
            switch (r)
            {
                case RuleId.R03:
                case RuleId.R18:
                    st.Variant = s.Export == ExportResult.Uploaded ? "uploaded" : s.Export == ExportResult.SavedLocal ? "saved" : "error";
                    st.Tone = s.Export == ExportResult.Uploaded ? StepTone.Success : s.Export == ExportResult.Error ? StepTone.Bad : StepTone.Neutral;
                    if (s.Export == ExportResult.Error) st.Primary = A("Try again", StepCommand.ExportNotebook, s);
                    else if (r == RuleId.R03) st.Primary = A("Back inside", StepCommand.OpenWorld, s);
                    break;
                case RuleId.R04:
                    st.Variant = pt ? "" : "model";
                    st.Tone = StepTone.Success;
                    st.Primary = A("Send the report", StepCommand.ExportNotebook, s);
                    st.Secondary0 = A("Back inside", StepCommand.OpenWorld, s);
                    break;
                case RuleId.R05:
                    st.Variant = s.Ux.D5RayOnUi ? "d5" : "";
                    st.Primary = A("Enter {place}", StepCommand.OpenWorld, s);
                    st.Flags |= StepFlags.PillHidden;   // the Enter control is the primary; the pill only mirrors it
                    break;
                case RuleId.R06:
                    st.Primary = A("Back inside", StepCommand.OpenWorld, s);
                    if (s.Shapes + s.Levels > 0) st.Secondary0 = A("Send the report", StepCommand.ExportNotebook, s);
                    break;
                case RuleId.R07: st.Tone = StepTone.Busy; break;
                case RuleId.R08: st.Tone = StepTone.Busy; break;
                case RuleId.R09:
                    st.Tone = StepTone.Bad;
                    if (s.Ux.DemoMode && s.Ux.W16OfflineReceipt)
                    {
                        st.Secondary0 = A("Use a demo receipt", StepCommand.UseOfflineReceipt, s);
                        st.Secondary1 = A("Cancel", StepCommand.CloseWindows, s);
                    }
                    else st.Secondary0 = A("Cancel", StepCommand.CloseWindows, s);
                    break;
                case RuleId.R10:
                    st.Variant = s.Ux.D5RayOnUi ? "d5" : "";
                    st.Flags |= StepFlags.PillHidden;   // SPEC M5: only the physical 1 s hold pays
                    st.Secondary0 = A("Other sellers", StepCommand.ShowSellers, s, "price");
                    st.Secondary1 = A("Cancel", StepCommand.CloseWindows, s);
                    break;
                case RuleId.R11:
                case RuleId.R12:
                    st.Tone = StepTone.Success;
                    st.Primary = A("Take it home", StepCommand.TakeHome, s);
                    st.Secondary0 = A("Keep working", StepCommand.CloseWindows, s);
                    break;
                case RuleId.R13: st.Tone = StepTone.Busy; st.Variant = s.VoiceHold ? "hold" : ""; break;   // voice
                case RuleId.R14: st.Tone = StepTone.Busy; break;
                case RuleId.R15: st.Tone = StepTone.Caution; break;
                case RuleId.R16: st.Variant = s.Ux.D6Commit == RingCommit.Pinch ? "pinch" : "settle"; break;
                case RuleId.R17:
                    st.Variant = s.NotebookCount == 0 ? "empty" : s.NotebookCount == 1 ? "one" : "";
                    if (s.NotebookCount > 0) st.Primary = A("Send the report", StepCommand.ExportNotebook, s);
                    st.Secondary0 = A("Close", StepCommand.ShowNotebook, s, flag: false);
                    break;
                case RuleId.R19:
                    st.Variant = s.LastTapeM > 0f ? "tape" : "";
                    st.Tone = StepTone.Busy;
                    break;
                case RuleId.R20: st.Tone = StepTone.Busy; break;
                case RuleId.R21: st.Tone = StepTone.Busy; break;
                case RuleId.R22:
                    st.Tone = StepTone.Bad;
                    st.Primary = A("Try the next one", StepCommand.SelectCandidate, s, index: s.Candidates > 0 ? (Math.Max(s.LastSelectedIndex, -1) + 1) % s.Candidates : 0);
                    st.Secondary0 = A("Search again", StepCommand.FindPart, s, s.LastQuery);
                    break;
                case RuleId.R23:
                    st.Tone = StepTone.Caution;
                    st.Primary = A("Hold it again", StepCommand.SelectCandidate, s, index: s.LastSelectedIndex);
                    break;
                case RuleId.R24:
                    st.Variant = s.LastTapeTarget != TapeTarget.Unknown ? "target" : "";
                    st.Secondary0 = A("Put it back", StepCommand.Undo, s);
                    break;
                case RuleId.R25:
                    st.Tone = StepTone.Success;
                    st.Secondary0 = A("Put it back", StepCommand.Undo, s);
                    break;
                case RuleId.R26:
                    st.Tone = StepTone.Bad;
                    st.Secondary0 = A("Put it back", StepCommand.Undo, s);
                    break;
                case RuleId.R27:
                    st.Secondary0 = A("Put it back", StepCommand.Undo, s);
                    break;
                case RuleId.R28:
                    st.Tone = StepTone.Busy;
                    st.Secondary0 = A("Close", StepCommand.CloseWindows, s);
                    break;
                case RuleId.R29:
                    if (s.RecommendedSeller >= 0 && !string.IsNullOrEmpty(s.BestSeller))
                    {
                        st.Primary = A("Pay with Visa", StepCommand.StartCheckout, s, index: s.RecommendedSeller);
                        st.Secondary0 = A("Fastest delivery", StepCommand.ShowSellers, s, "eta");
                        st.Secondary1 = A("Close", StepCommand.CloseWindows, s);
                    }
                    else
                    {
                        st.Variant = "none";
                        st.Tone = StepTone.Caution;
                        st.Secondary0 = A("Close", StepCommand.CloseWindows, s);
                    }
                    break;
                case RuleId.R30:
                    st.Secondary0 = A("Undo point", StepCommand.Undo, s);
                    break;
                case RuleId.R31:
                    len = s.SessionLengthM;
                    st.Primary = A("Save {len}", StepCommand.FinishShape, s, len: len);
                    st.Secondary0 = A("Undo point", StepCommand.Undo, s);
                    break;
                case RuleId.R32:
                    area = s.SessionAreaM2;
                    if (s.SessionCrossing == AirTools.Tools.OutlineCrossing.Path)
                    {
                        // The placed points cross: no shape to save until a point comes off.
                        st.Variant = "cross";
                        st.Tone = StepTone.Caution;
                        st.Primary = A("Undo point", StepCommand.Undo, s);
                    }
                    else if (s.SessionCrossing == AirTools.Tools.OutlineCrossing.Closing)
                    {
                        // Only the closing side would cross: the next points may fix it.
                        st.Variant = "open";
                        st.Secondary0 = A("Undo point", StepCommand.Undo, s);
                    }
                    else
                    {
                        st.Primary = A("Save shape", StepCommand.FinishShape, s);
                        st.Secondary0 = A("Undo point", StepCommand.Undo, s);
                    }
                    break;
                case RuleId.R33:
                    st.Tone = StepTone.Success;
                    st.Primary = A("Take it home", StepCommand.TakeHome, s);
                    if (s.Mode == AppMode.Tabletop) st.Secondary0 = A("Back to full size", StepCommand.SetTabletop, s, flag: false);
                    break;
                case RuleId.R34:
                case RuleId.R35:
                    st.Tone = StepTone.Success;
                    st.Primary = A("Compare prices", StepCommand.ShowSellers, s, "price");
                    if (s.ArrayAllowed)
                    {
                        st.Secondary0 = A("Fill the run", StepCommand.PlaceArray, s);
                        st.Secondary1 = A("Undo", StepCommand.Undo, s);
                    }
                    else st.Secondary0 = A("Undo", StepCommand.Undo, s);
                    break;
                case RuleId.R36:
                    st.Tone = StepTone.Bad;
                    st.Primary = A("See other {parts}", StepCommand.FindPart, s, s.LastQuery);
                    st.Secondary0 = A("Undo", StepCommand.Undo, s);
                    break;
                case RuleId.R37:
                    st.Variant = s.SelectedReason == FitReason.TooWide ? "wide" : s.SelectedReason == FitReason.TooNarrow ? "narrow" : "";
                    st.Tone = StepTone.Bad;
                    st.Primary = A("See other {parts}", StepCommand.FindPart, s, s.LastQuery);
                    st.Secondary0 = A("Undo", StepCommand.Undo, s);
                    break;
                case RuleId.R38:
                    st.Variant = s.SelectedReason switch
                    {
                        FitReason.NeedsTape => "tape",
                        FitReason.Overhang => "overhang",
                        FitReason.NotMounted => "mount",
                        FitReason.Clearance => "clearance",
                        _ => "",
                    };
                    st.Tone = StepTone.Caution;
                    st.Primary = s.SelectedReason == FitReason.NeedsTape
                        ? A("Measure the window", StepCommand.EquipTool, s, "measure")
                        : A("Compare prices", StepCommand.ShowSellers, s, "price");
                    st.Secondary0 = A("Undo", StepCommand.Undo, s);
                    break;
                case RuleId.R39:
                    st.Variant = s.CandidatesOffline ? "offline" : s.ScaleCalibrated ? "true" : "";
                    if (s.CandidatesOffline) st.Flags |= StepFlags.OfflineParts;
                    st.Primary = A("Hold the top pick", StepCommand.SelectCandidate, s, index: 0);
                    if (s.Candidates >= 2) st.Secondary0 = A("Next one", StepCommand.SelectCandidate, s, index: 1);
                    break;
                case RuleId.R40:
                    st.Tone = StepTone.Caution;
                    st.Flags |= StepFlags.OfflineParts;
                    st.Primary = A("Try again", StepCommand.FindPart, s, s.LastQuery);
                    break;
                case RuleId.R41:
                    st.Variant = s.LastTapeM > 0f ? "tape" : "";
                    st.Tone = StepTone.Caution;
                    st.Primary = A("Find parts", StepCommand.ShowFindParts, s);
                    st.Secondary0 = A("Try again", StepCommand.FindPart, s, s.LastQuery);
                    break;
                case RuleId.R42:
                    st.Tone = StepTone.Success;
                    st.Primary = A("Find {tparts} for this {target}", StepCommand.FindPart, s, NextStepCopy.QueryFor(s.LastTapeTarget));
                    st.Secondary0 = A("Notebook", StepCommand.ShowNotebook, s, flag: true);
                    break;
                case RuleId.R43:
                    st.Tone = StepTone.Success;
                    st.Primary = A("Find parts for {len}", StepCommand.ShowFindParts, s);
                    st.Secondary0 = A("Notebook", StepCommand.ShowNotebook, s, flag: true);
                    break;
                case RuleId.R44:
                    st.Tone = StepTone.Success;
                    st.Primary = A("Open the notebook", StepCommand.ShowNotebook, s, flag: true);
                    break;
                case RuleId.R45:
                    if (!s.EverEnteredWorld)
                    {
                        st.Variant = "pin";
                        st.Primary = A("Step inside", StepCommand.StepIn, s);
                    }
                    else
                    {
                        st.Primary = A("Back to full size", StepCommand.SetTabletop, s, flag: false);
                        if (s.Purchases > 0) st.Secondary0 = A("Take it home", StepCommand.TakeHome, s);
                    }
                    break;
                case RuleId.R46:
                    st.Tone = StepTone.Caution;
                    st.Variant = s.SceneLoadFailed ? "failed" : "";
                    st.Primary = A(s.SceneLoadFailed ? "Retry" : "Try again", StepCommand.LoadSite, s, s.StartSite);
                    break;
                case RuleId.R47:
                    st.Tone = StepTone.Caution;
                    st.Primary = A("Set scale", StepCommand.ShowScenePanel, s, flag: true);
                    break;
                case RuleId.R48:
                    st.Variant = s.Levels > 0 && s.Newest == StepEvent.Level ? "logged" : "aim";
                    if (st.Variant == "logged") st.Tone = StepTone.Success;
                    st.Primary = A("Back to measuring", StepCommand.EquipTool, s, "measure");
                    break;
                case RuleId.R49:
                    st.Primary = A("Measure {entry}", StepCommand.EquipTool, s, "measure");
                    break;
                case RuleId.R50:
                    st.Primary = A("Back to measuring", StepCommand.EquipTool, s, "measure");
                    break;
                case RuleId.R51:
                    st.Primary = A("Measure", StepCommand.EquipTool, s, "measure");
                    break;
                case RuleId.R52:
                case RuleId.R53:
                    st.Secondary0 = A("Walk", StepCommand.EquipTool, s, "move");
                    break;
                case RuleId.R54:
                    st.Primary = A("Measure", StepCommand.EquipTool, s, "measure");
                    break;
            }
            string template = NextStepCopy.Status(r, st.Variant, s.Controllers);
            st.Status = NextStepCopy.Fill(template, s, len, area);
            return st;
        }
    }
}
