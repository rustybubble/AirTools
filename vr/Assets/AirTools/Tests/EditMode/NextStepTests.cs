using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AirTools.Core;
using AirTools.Parts;
using AirTools.Tools;
using NUnit.Framework;

namespace AirTools.Tests
{
    /// UX W1.3 guide rail (docs/ux/specs/W1.3-nextstep.md §6.1): the NextStep rule table row by row (T01–T49 plus X rows
    /// for the rules the T rows don't reach), the property checks P1–P5, the coach rules CT1–CT8, and the judge's
    /// kitchen path (§0 / §6.2) in every D1 × D4 variant. Pure: no scene, no engine calls.
    public class NextStepTests
    {
        [SetUp]
        public void ResetHooks() => NextStep.ResetHooks();

        // ---------------- Base + rows (§6.1) ----------------

        /// Mode World, kitchen, calibrated, Measure in hand, hands; D1 Measure, D4 auto, D6 Pinch, D5 off, W0.4 off, W1.6 on,
        /// DemoMode on; entered, LastQuery "cabinet hinge", Newest = Tool; everything else zero / false / None.
        public static AppSnapshot Base() => new AppSnapshot
        {
            Mode = AppMode.World,
            Scene = SceneKind.Kitchen,
            StartSite = "kitchen",
            ScaleCalibrated = true,
            Tool = ToolKind.Measure,
            Modality = InputModality.Hands,
            EverEnteredWorld = true,
            LastQuery = "cabinet hinge",
            Newest = StepEvent.Tool,
            Ux = new UxFlags
            {
                D1Entry = ToolKind.Measure, D4AutoSave = true, D6Commit = RingCommit.Pinch, D5RayOnUi = false,
                W04Preserves = false, W16OfflineReceipt = true, DemoMode = true, GuideRailEnabled = true,
            },
        };

        public delegate void Delta(ref AppSnapshot s);

        /// One expected row. Null / unset fields are not checked.
        public sealed class Case
        {
            public string Id;
            public AppSnapshot S;
            public RuleId Rule;
            public RuleId? BaseRule;
            public string Status;
            public bool CheckPrimary;
            public StepCommand Primary;
            public string PrimaryArg, PrimaryLabel;
            public int? PrimaryIndex;
            public bool? PrimaryFlag;
            public bool CheckSec0, CheckSec1;
            public StepCommand Sec0, Sec1;
            public string Sec0Arg, Sec0Label;
            public int? Sec0Index;
            public StepFlags Flags;

            public Case Prim(StepCommand c, string arg = null, string label = null, int? index = null, bool? flag = null)
            {
                CheckPrimary = true; Primary = c; PrimaryArg = arg; PrimaryLabel = label; PrimaryIndex = index; PrimaryFlag = flag;
                return this;
            }

            public Case NoPrim() => Prim(StepCommand.None);

            public Case S0(StepCommand c, string arg = null, string label = null, int? index = null)
            {
                CheckSec0 = true; Sec0 = c; Sec0Arg = arg; Sec0Label = label; Sec0Index = index;
                return this;
            }

            public Case S1(StepCommand c) { CheckSec1 = true; Sec1 = c; return this; }
            public Case Flag(StepFlags f) { Flags |= f; return this; }
            public Case Base(RuleId r) { BaseRule = r; return this; }
            public override string ToString() => Id;
        }

        static Case T(string id, Delta d, RuleId rule, string status)
        {
            var s = Base();
            d?.Invoke(ref s);
            return new Case { Id = id, S = s, Rule = rule, Status = status };
        }

        static Case T(string id, Case from, Delta d, RuleId rule, string status)
        {
            var s = from.S;
            d?.Invoke(ref s);
            return new Case { Id = id, S = s, Rule = rule, Status = status };
        }

        static List<Case> s_Cases;

        public static List<Case> Cases()
        {
            if (s_Cases != null) return s_Cases;
            var c = new List<Case>();
            var t01 = T("T01", (ref AppSnapshot s) => { s.Mode = AppMode.Passthrough; s.EverEnteredWorld = false; }, RuleId.R05,
                "Touch Enter world to step into the kitchen").Prim(StepCommand.OpenWorld, label: "Enter the kitchen").Flag(StepFlags.PillHidden);
            c.Add(t01);
            c.Add(T("T02", t01, (ref AppSnapshot s) => s.Modality = InputModality.Controllers, RuleId.R05, "Push Enter world with your controller")
                .Prim(StepCommand.OpenWorld).Flag(StepFlags.PillHidden));
            c.Add(T("T03", (ref AppSnapshot s) => s.Transitioning = true, RuleId.R00, null));
            var t04 = T("T04", null, RuleId.R52, "Measure a door: pinch one corner").NoPrim().S0(StepCommand.EquipTool, "move", "Walk");
            c.Add(t04);
            c.Add(T("T05", (ref AppSnapshot s) => { s.Ux.D1Entry = ToolKind.Move; s.Tool = ToolKind.Move; }, RuleId.R49, "Pinch the floor to walk · or measure")
                .Prim(StepCommand.EquipTool, "measure", "Measure a door"));
            c.Add(T("T06", t04, (ref AppSnapshot s) => s.Modality = InputModality.Controllers, RuleId.R52, "Measure a door: trigger on one corner").NoPrim());
            c.Add(T("T07", (ref AppSnapshot s) => s.SessionPoints = 1, RuleId.R30, "Now pinch the other corner").NoPrim().S0(StepCommand.Undo, label: "Undo point"));
            var t08 = T("T08", (ref AppSnapshot s) => { s.Ux.D4AutoSave = false; s.SessionPoints = 2; s.SessionLengthM = 0.3891f; }, RuleId.R31,
                "Pinch your left hand to save 0.39 m").Prim(StepCommand.FinishShape, label: "Save 0.39 m").S0(StepCommand.Undo);
            c.Add(t08);
            c.Add(T("T09", t08, (ref AppSnapshot s) => s.Modality = InputModality.Controllers, RuleId.R31, "Press B to save 0.39 m").Prim(StepCommand.FinishShape));
            var t10 = T("T10", (ref AppSnapshot s) =>
            {
                s.Shapes = 1; s.LastReading = ReadingKind.Tape; s.LastTapeM = 0.3891f; s.LastTapeTarget = TapeTarget.Door; s.Newest = StepEvent.Tape;
            }, RuleId.R42, "Saved 0.39 m ✓").Prim(StepCommand.FindPart, "cabinet hinge", "Find hinges for this door").S0(StepCommand.ShowNotebook, label: "Notebook");
            c.Add(t10);
            c.Add(T("T11", t10, (ref AppSnapshot s) => s.LastTapeTarget = TapeTarget.Unknown, RuleId.R43, "Saved 0.39 m ✓")
                .Prim(StepCommand.ShowFindParts, label: "Find parts for 0.39 m"));
            c.Add(T("T12", (ref AppSnapshot s) => { s.MeasureMode = MeasureMode.Area; s.SessionPoints = 4; s.SessionAreaM2 = 0.108f; }, RuleId.R32,
                "Pinch your left hand to save 0.11 m²").Prim(StepCommand.FinishShape, label: "Save shape"));
            c.Add(T("T13", (ref AppSnapshot s) => { s.Searching = true; s.SearchElapsed = 2f; s.LastTapeM = 0.3891f; }, RuleId.R19, "Finding hinges that fit 0.39 m…").NoPrim());
            c.Add(T("T14", (ref AppSnapshot s) => { s.Searching = true; s.SearchElapsed = 12f; }, RuleId.R20, "Searching the supply shops…").NoPrim());
            var t15 = T("T15", (ref AppSnapshot s) => { s.Candidates = 3; s.Newest = StepEvent.Search; }, RuleId.R39, "Pick a hinge · shown at true size")
                .Prim(StepCommand.SelectCandidate, index: 0, label: "Hold the top pick").S0(StepCommand.SelectCandidate, index: 1, label: "Next one");
            c.Add(t15);
            c.Add(T("T16", t15, (ref AppSnapshot s) => s.ScaleCalibrated = false, RuleId.R39, "Pick a hinge to hold it")
                .Prim(StepCommand.SelectCandidate, index: 0).Flag(StepFlags.ScaleUnset));
            c.Add(T("T17", (ref AppSnapshot s) => { s.Candidates = 0; s.CandidatesOffline = true; s.Newest = StepEvent.Search; }, RuleId.R40,
                "Try again · parts search is offline").Prim(StepCommand.FindPart, "cabinet hinge").Flag(StepFlags.OfflineParts));
            c.Add(T("T18", (ref AppSnapshot s) => { s.PartHeld = true; s.LastTapeTarget = TapeTarget.Door; }, RuleId.R24, "Aim at the door to place the hinge")
                .NoPrim().S0(StepCommand.Undo, label: "Put it back"));
            c.Add(T("T19", (ref AppSnapshot s) =>
            {
                s.PartHeld = true; s.HeldOnSurface = true; s.HeldFit = FitStatus.Green; s.Modality = InputModality.Controllers;
            }, RuleId.R25, "Pull the trigger to place it · it fits ✓").NoPrim());
            c.Add(T("T20", (ref AppSnapshot s) => { s.PartHeld = true; s.HeldOnSurface = true; s.HeldFit = FitStatus.Red; }, RuleId.R26,
                "Try another spot · it won't fit here").NoPrim());
            var t21 = T("T21", (ref AppSnapshot s) =>
            {
                s.Newest = StepEvent.Placed; s.SelectedPlaced = true; s.SelectedFit = FitStatus.Green; s.SelectedHasTape = true; s.LastTapeTarget = TapeTarget.Door;
            }, RuleId.R34, "✓ Fits the door").Prim(StepCommand.ShowSellers, "price", "Compare prices").S0(StepCommand.Undo).S1(StepCommand.None);
            c.Add(t21);
            c.Add(T("T22", t21, (ref AppSnapshot s) => s.ArrayAllowed = true, RuleId.R34, "✓ Fits the door")
                .Prim(StepCommand.ShowSellers, "price").S0(StepCommand.PlaceArray, label: "Fill the run").S1(StepCommand.Undo));
            // ✗ (U+2717), not the spec's ✕: Inter has no U+2715 (Copy.Glyph).
            c.Add(T("T23", (ref AppSnapshot s) =>
            {
                s.Newest = StepEvent.Placed; s.SelectedPlaced = true; s.SelectedFit = FitStatus.Red; s.SelectedReason = FitReason.Hits; s.LastQuery = "shelf bracket";
            }, RuleId.R36, "✗ Won't fit here · see other brackets").Prim(StepCommand.FindPart, "shelf bracket", "See other brackets"));
            c.Add(T("T24", (ref AppSnapshot s) => { s.SellersOpen = true; s.SellersRefreshing = true; }, RuleId.R28, "Checking sellers…").NoPrim());
            c.Add(T("T25", (ref AppSnapshot s) =>
            {
                s.SellersOpen = true; s.BestSeller = "Home Depot"; s.BestPrice = 7.59f; s.RecommendedSeller = 1;
            }, RuleId.R29, "Buy from Home Depot · $7.59 delivered").Prim(StepCommand.StartCheckout, index: 1, label: "Pay with Visa")
                .S0(StepCommand.ShowSellers, "eta", "Fastest delivery").S1(StepCommand.CloseWindows));
            c.Add(T("T26", (ref AppSnapshot s) => { s.Checkout = CheckoutState.Ready; s.CheckoutTotal = 7.59f; s.SellersOpen = true; }, RuleId.R10,
                "Hold to pay $7.59 · keep pressing 1 s").NoPrim().Flag(StepFlags.PillHidden).S0(StepCommand.ShowSellers, "price").S1(StepCommand.CloseWindows));
            c.Add(T("T27", (ref AppSnapshot s) => s.Checkout = CheckoutState.Paying, RuleId.R08, "Authorizing with Visa…").NoPrim());
            var t28 = T("T28", (ref AppSnapshot s) => s.Checkout = CheckoutState.Failed, RuleId.R09, "Couldn't reach the laptop · nothing charged")
                .NoPrim().S0(StepCommand.UseOfflineReceipt).S1(StepCommand.CloseWindows);
            c.Add(t28);
            c.Add(T("T29", t28, (ref AppSnapshot s) => s.Ux.DemoMode = false, RuleId.R09, "Couldn't reach the laptop · nothing charged")
                .NoPrim().S0(StepCommand.CloseWindows).S1(StepCommand.None));
            c.Add(T("T30", (ref AppSnapshot s) => { s.Checkout = CheckoutState.Paid; s.ReceiptAuthorized = false; s.CheckoutTotal = 7.59f; }, RuleId.R12,
                "Saved an offline receipt · $7.59, no charge").Prim(StepCommand.TakeHome, label: "Take it home").S0(StepCommand.CloseWindows, label: "Keep working"));
            c.Add(T("T31", (ref AppSnapshot s) => { s.Checkout = CheckoutState.Paid; s.ReceiptAuthorized = true; s.CheckoutTotal = 7.59f; }, RuleId.R11,
                "Paid $7.59 · receipt in your notebook").Prim(StepCommand.TakeHome));
            c.Add(T("T32", (ref AppSnapshot s) => { s.Mode = AppMode.Passthrough; s.OnTable = 1; }, RuleId.R04, "See your hinge on the table")
                .Prim(StepCommand.ExportNotebook, label: "Send the report").S0(StepCommand.OpenWorld, label: "Back inside"));
            var t33 = T("T33", (ref AppSnapshot s) => { s.RingOpen = true; s.PartHeld = true; s.Ux.D6Commit = RingCommit.Settle; }, RuleId.R15,
                "Don't spin: the ring puts the part away").NoPrim();
            c.Add(t33);
            c.Add(T("T34", t33, (ref AppSnapshot s) => s.Ux.W04Preserves = true, RuleId.R16, "Spin to a tool · pinch the lens for actions").NoPrim());
            // Controllers copy names the menu button: Inter has no ☰.
            c.Add(T("T35", (ref AppSnapshot s) => { s.RingOpen = true; s.Modality = InputModality.Controllers; }, RuleId.R16,
                "Spin, trigger the lens · menu button closes it").NoPrim());
            var t36 = T("T36", (ref AppSnapshot s) =>
            {
                s.Tool = ToolKind.Move; s.Shapes = 1; s.Refusal = Refusal.TeleportNotFlat; s.RefusalAge = 1f;
            }, RuleId.R01, "Aim at the floor to walk").Base(RuleId.R50).Prim(StepCommand.EquipTool, "measure").Flag(StepFlags.Overlay);
            c.Add(t36);
            c.Add(T("T37", t36, (ref AppSnapshot s) => s.RefusalAge = 3f, RuleId.R50, "Pinch the floor to walk there").Prim(StepCommand.EquipTool, "measure", "Back to measuring"));
            c.Add(T("T38", (ref AppSnapshot s) => { s.ScaleCalibrated = false; s.Ux.DemoMode = false; }, RuleId.R47, "Set the scale: tape a door you know")
                .Prim(StepCommand.ShowScenePanel, flag: true, label: "Set scale"));
            c.Add(T("T39", (ref AppSnapshot s) => s.ScaleCalibrated = false, RuleId.R52, "Measure a door: pinch one corner").NoPrim().Flag(StepFlags.ScaleUnset));
            c.Add(T("T40", (ref AppSnapshot s) => { s.Scene = SceneKind.Facade; s.SceneFallback = true; }, RuleId.R46, "Reload the kitchen · this is the sample wall")
                .Prim(StepCommand.LoadSite, "kitchen", "Try again"));
            var t41 = T("T41", (ref AppSnapshot s) => { s.Mode = AppMode.Tabletop; s.Newest = StepEvent.Mode; }, RuleId.R45, "Measure the model · tapes read full size")
                .Prim(StepCommand.SetTabletop, flag: false, label: "Back to full size");
            c.Add(t41);
            c.Add(T("T42", t41, (ref AppSnapshot s) => s.SessionPoints = 1, RuleId.R30, "Now pinch the other corner"));
            c.Add(T("T43", (ref AppSnapshot s) => { s.Tool = ToolKind.Level; s.Levels = 0; }, RuleId.R48, "Aim at the counter · pinch to check level")
                .Prim(StepCommand.EquipTool, "measure"));
            c.Add(T("T44", (ref AppSnapshot s) => { s.NotebookOpen = true; s.NotebookCount = 3; }, RuleId.R17, "Send 3 readings to the laptop")
                .Prim(StepCommand.ExportNotebook).S0(StepCommand.ShowNotebook, label: "Close"));
            c.Add(T("T45", (ref AppSnapshot s) => { s.HeldLost = true; s.HeldLostAge = 2f; s.LastSelectedIndex = 0; s.Candidates = 3; }, RuleId.R23,
                "Hold the hinge again · it was put away").Prim(StepCommand.SelectCandidate, index: 0, label: "Hold it again"));
            c.Add(T("T46", (ref AppSnapshot s) => { s.Purchases = 1; s.Newest = StepEvent.Purchase; s.Checkout = CheckoutState.Closed; }, RuleId.R33,
                "Take your hinge home · it's paid for").Prim(StepCommand.TakeHome));
            c.Add(T("T47", (ref AppSnapshot s) => s.VoiceRecording = true, RuleId.R13, "Listening… tap to send").NoPrim());
            c.Add(T("T48", (ref AppSnapshot s) => s.Scene = SceneKind.Facade, RuleId.R52, "Measure the gutter: pinch one end").NoPrim());
            c.Add(T("T49", (ref AppSnapshot s) => s.Tool = ToolKind.None, RuleId.R54, "Turn your left palm up for tools").Prim(StepCommand.EquipTool, "measure", "Measure"));

            // X rows: every rule the T rows don't reach, the G1 (grow-in) variants and a few copy variants.
            c.Add(T("X01", (ref AppSnapshot s) =>
            {
                s.Echo = UndoEcho.Undo; s.EchoKind = ReadingKind.Tape; s.EchoValue = 0.3891f; s.EchoAge = 0.5f;
            }, RuleId.R02, "Undone · 0.39 m tape").Base(RuleId.R52).Flag(StepFlags.Overlay));
            c.Add(T("X02", (ref AppSnapshot s) => { s.Mode = AppMode.Passthrough; s.Export = ExportResult.Uploaded; s.ExportAge = 1f; }, RuleId.R03,
                "Sent to the laptop ✓").Prim(StepCommand.OpenWorld, label: "Back inside"));
            c.Add(T("X03", (ref AppSnapshot s) => { s.Mode = AppMode.Passthrough; s.Export = ExportResult.Error; s.ExportAge = 1f; }, RuleId.R03,
                "Couldn't save the report · try again").Prim(StepCommand.ExportNotebook, label: "Try again"));
            c.Add(T("X04", (ref AppSnapshot s) => { s.Mode = AppMode.Passthrough; s.Shapes = 1; }, RuleId.R06, "Step back in when you're ready")
                .Prim(StepCommand.OpenWorld).S0(StepCommand.ExportNotebook));
            c.Add(T("X05", (ref AppSnapshot s) => s.SceneLoading = true, RuleId.R07, "Loading the kitchen…").NoPrim());
            c.Add(T("X06", (ref AppSnapshot s) => s.AgentBusy = true, RuleId.R14, "Asking the assistant…").NoPrim());
            c.Add(T("X07", (ref AppSnapshot s) => { s.Export = ExportResult.SavedLocal; s.ExportAge = 2f; }, RuleId.R18,
                "Saved on the headset · laptop not connected").NoPrim());
            c.Add(T("X08", (ref AppSnapshot s) => s.Loading = true, RuleId.R21, "Getting the hinge ready…").NoPrim());
            c.Add(T("X09", (ref AppSnapshot s) => { s.LoadFailed = true; s.Candidates = 3; s.LastSelectedIndex = 0; }, RuleId.R22,
                "Couldn't load that hinge · try the next").Prim(StepCommand.SelectCandidate, index: 1, label: "Try the next one").S0(StepCommand.FindPart, "cabinet hinge"));
            c.Add(T("X10", (ref AppSnapshot s) => { s.PartHeld = true; s.HeldOnSurface = true; s.HeldFit = FitStatus.Amber; }, RuleId.R27, "Pinch to place it here"));
            c.Add(T("X11", (ref AppSnapshot s) => { s.Newest = StepEvent.Placed; s.SelectedPlaced = true; s.SelectedFit = FitStatus.Green; }, RuleId.R35,
                "✓ Fits · nothing in the way").Prim(StepCommand.ShowSellers, "price"));
            c.Add(T("X12", (ref AppSnapshot s) =>
            {
                s.Newest = StepEvent.Placed; s.SelectedPlaced = true; s.SelectedFit = FitStatus.Red; s.SelectedReason = FitReason.TooWide; s.LastTapeTarget = TapeTarget.Door;
            }, RuleId.R37, "✗ Too wide for the door · see others").Prim(StepCommand.FindPart, "cabinet hinge", "See other hinges"));
            c.Add(T("X13", (ref AppSnapshot s) =>
            {
                s.Newest = StepEvent.Placed; s.SelectedPlaced = true; s.SelectedFit = FitStatus.Amber; s.SelectedReason = FitReason.NeedsTape;
            }, RuleId.R38, "! Measure the window to check the fit").Prim(StepCommand.EquipTool, "measure", "Measure the window"));
            c.Add(T("X14", (ref AppSnapshot s) => { s.Newest = StepEvent.Search; s.LastTapeM = 0.3891f; }, RuleId.R41,
                "Try another part · no hinges for 0.39 m").Prim(StepCommand.ShowFindParts, label: "Find parts").S0(StepCommand.FindPart, "cabinet hinge"));
            c.Add(T("X15", (ref AppSnapshot s) =>
            {
                s.Shapes = 1; s.Newest = StepEvent.Tape; s.LastReading = ReadingKind.Area; s.LastAreaM2 = 0.5f;
            }, RuleId.R44, "Saved 0.50 m² ✓").Prim(StepCommand.ShowNotebook, flag: true));
            c.Add(T("X16", (ref AppSnapshot s) => s.Tool = ToolKind.Part, RuleId.R51, "Pinch a placed part to move it").Prim(StepCommand.EquipTool, "measure"));
            c.Add(T("X17", (ref AppSnapshot s) => s.Shapes = 1, RuleId.R53, "Pinch one corner to measure again").NoPrim().S0(StepCommand.EquipTool, "move"));
            c.Add(T("X18", (ref AppSnapshot s) => { s.Mode = AppMode.Tabletop; s.OnTable = 1; s.Newest = StepEvent.Mode; }, RuleId.R04,
                "See your hinge beside the model").Prim(StepCommand.ExportNotebook));
            c.Add(T("X19", (ref AppSnapshot s) => { s.Mode = AppMode.Tabletop; s.EverEnteredWorld = false; }, RuleId.R45, "Pinch the pin to step inside")
                .Prim(StepCommand.StepIn, label: "Step inside"));
            c.Add(T("X20", (ref AppSnapshot s) =>
            {
                s.Tool = ToolKind.Level; s.Levels = 1; s.Newest = StepEvent.Level; s.LastLevelDeg = 0.4f;
            }, RuleId.R48, "Logged 0.4° · pinch another surface"));
            c.Add(T("X21", t01, (ref AppSnapshot s) => s.Ux.D5RayOnUi = true, RuleId.R05, "Point at Enter world and pinch"));
            c.Add(T("X22", (ref AppSnapshot s) =>
            {
                s.Checkout = CheckoutState.Ready; s.Modality = InputModality.Controllers; s.Ux.D5RayOnUi = true;
            }, RuleId.R10, "Point at Pay, hold the trigger 1 s").NoPrim());
            c.Add(T("X23", (ref AppSnapshot s) => { s.SellersOpen = true; s.RecommendedSeller = -1; }, RuleId.R29, "No sellers listed for this part").NoPrim());
            c.Add(T("X24", (ref AppSnapshot s) => { s.Refusal = Refusal.NothingToUndo; s.RefusalAge = 0.2f; }, RuleId.R01, "Nothing to undo").Base(RuleId.R52));
            c.Add(T("X25", (ref AppSnapshot s) => { s.RingOpen = true; s.Ux.D6Commit = RingCommit.Settle; s.Modality = InputModality.Controllers; }, RuleId.R16,
                "Trigger-drag to spin · menu button closes it"));
            c.Add(T("X26", (ref AppSnapshot s) => { s.Scene = SceneKind.Facade; s.SessionPoints = 1; s.Modality = InputModality.Controllers; }, RuleId.R30,
                "Now trigger on the other end"));
            c.Add(T("X27", (ref AppSnapshot s) => { s.NotebookOpen = true; s.NotebookCount = 0; }, RuleId.R17, "Nothing saved yet · measure something")
                .NoPrim().S0(StepCommand.ShowNotebook));
            c.Add(T("X28", t10, (ref AppSnapshot s) => s.LastTapeTarget = TapeTarget.Panel, RuleId.R42, "Saved 0.39 m ✓")
                .Prim(StepCommand.FindPart, "shelf bracket", "Find brackets for this cabinet"));
            c.Add(T("X29", t10, (ref AppSnapshot s) => s.LastTapeTarget = TapeTarget.Drawer, RuleId.R42, "Saved 0.39 m ✓")
                .Prim(StepCommand.FindPart, "drawer slide", "Find slides for this drawer"));
            // Concave polygons: an Area-mode outline that crosses itself can't be saved.
            var x30 = T("X30", (ref AppSnapshot s) =>
            {
                s.MeasureMode = MeasureMode.Area; s.SessionPoints = 5; s.SessionCrossing = OutlineCrossing.Path;
            }, RuleId.R32, "Edges cross · undo the last point").Prim(StepCommand.Undo, label: "Undo point");
            c.Add(x30);
            c.Add(T("X31", x30, (ref AppSnapshot s) => s.SessionCrossing = OutlineCrossing.Closing, RuleId.R32,
                "Keep going · closing here would cross").NoPrim().S0(StepCommand.Undo, label: "Undo point"));
            c.Add(T("X32", x30, (ref AppSnapshot s) => { s.Refusal = Refusal.EdgesCross; s.RefusalAge = 0.5f; }, RuleId.R01,
                "Edges cross · undo the last point").Base(RuleId.R32));
            // voice: Talk held (hold-to-talk) says release; a tap (T47) says tap.
            c.Add(T("X33", (ref AppSnapshot s) => { s.VoiceRecording = true; s.VoiceHold = true; }, RuleId.R13, "Listening… release to send").NoPrim());
            return s_Cases = c;
        }

        public static IEnumerable<TestCaseData> Rows() => Cases().Select(c => new TestCaseData(c).SetName($"{c.Id} {c.Rule}"));

        [TestCaseSource(nameof(Rows))]
        public void Rule(Case c)
        {
            var s = c.S;
            Assert.AreEqual(c.Rule, NextStep.Match(s), $"{c.Id}: rule");
            var step = NextStep.For(s);
            Assert.AreEqual(c.Rule, step.Rule, $"{c.Id}: Step.Rule");
            if (c.Rule == RuleId.R00) { Assert.IsTrue(step.Frozen, $"{c.Id}: frozen"); return; }
            Assert.AreEqual(c.BaseRule ?? c.Rule, step.Base, $"{c.Id}: base rule");
            if (c.Status != null) Assert.AreEqual(c.Status, step.Status, $"{c.Id}: status");
            if (c.CheckPrimary) CheckAction(c.Id + " primary", step.Primary, c.Primary, c.PrimaryArg, c.PrimaryIndex, c.PrimaryFlag, c.PrimaryLabel);
            if (c.CheckSec0) CheckAction(c.Id + " secondary0", step.Secondary0, c.Sec0, c.Sec0Arg, c.Sec0Index, null, c.Sec0Label);
            if (c.CheckSec1) Assert.AreEqual(c.Sec1, step.Secondary1.Command, $"{c.Id}: secondary1");
            Assert.AreEqual(c.Flags, step.Flags & c.Flags, $"{c.Id}: flags {step.Flags}");
            if ((c.Flags & StepFlags.PillHidden) == 0 && c.CheckPrimary && c.Primary != StepCommand.None)
                Assert.IsTrue(step.PillVisible, $"{c.Id}: the pill shows");
        }

        static void CheckAction(string what, StepAction a, StepCommand cmd, string arg, int? index, bool? flag, string label)
        {
            Assert.AreEqual(cmd, a.Command, $"{what}: command ({a})");
            if (arg != null) Assert.AreEqual(arg, a.Arg, $"{what}: arg");
            if (index.HasValue) Assert.AreEqual(index.Value, a.Index, $"{what}: index");
            if (flag.HasValue) Assert.AreEqual(flag.Value, a.Flag, $"{what}: flag");
            if (label != null) Assert.AreEqual(label, a.Label, $"{what}: label");
        }

        // ---------------- P1 copy budgets ----------------

        static readonly Regex s_Forbidden = new Regex(@"(?i)http|\(mock\)|SPEC|Exception|#\d|palm toward|open your left palm");
        static readonly Regex s_Pinch = new Regex(@"(?i)pinch");

        /// The longest expansions (spec §3.1 budget): imperial length, $1,234.56, "appliance", a 14-character seller.
        static void LongestHooks()
        {
            NextStep.Len = m => "13′ 9⅜″";
            NextStep.Money = usd => "$1,234.56";
            NextStep.Area = a => "999.99 m²";
        }

        static AppSnapshot Longest(SceneKind scene, InputModality modality)
        {
            var s = Base();
            s.Scene = scene;
            s.Modality = modality;
            s.StartSite = "kitchen";
            s.LastQuery = "window ac";            // AC unit / AC units: the longest noun
            s.LastTapeTarget = TapeTarget.Appliance;
            s.BestSeller = "Home Depot Pro Supply Company (mock)";
            s.NotebookCount = 999;
            s.LastLevelDeg = -89.9f;
            return s;
        }

        [Test]
        public void P1_EveryStatusAndCoachLineFitsItsBudget()
        {
            LongestHooks();
            try
            {
                foreach (var scene in new[] { SceneKind.Kitchen, SceneKind.Facade, SceneKind.Package, SceneKind.None })
                foreach (var modality in new[] { InputModality.Hands, InputModality.Controllers })
                foreach (var target in new[] { TapeTarget.Appliance, TapeTarget.Panel, TapeTarget.Drawer, TapeTarget.Unknown })
                {
                    var s = Longest(scene, modality);
                    s.LastTapeTarget = target;
                    bool controllers = modality == InputModality.Controllers;
                    foreach (var row in NextStepCopy.StatusRows)
                    {
                        var text = NextStepCopy.Fill(row.For(controllers), s);
                        Assert.LessOrEqual(text.Length, NextStepCopy.StatusBudget, $"{row.Rule}/{row.Variant} ({scene}, {modality}): \"{text}\"");
                        Assert.IsFalse(s_Forbidden.IsMatch(text), $"{row.Rule}/{row.Variant}: dev text in \"{text}\"");
                        Assert.IsFalse(text.Contains("{"), $"{row.Rule}/{row.Variant}: unfilled placeholder in \"{text}\"");
                        if (controllers) Assert.IsFalse(s_Pinch.IsMatch(text), $"{row.Rule}/{row.Variant} controllers copy says pinch: \"{text}\"");
                    }
                    foreach (var row in NextStepCopy.CoachRows)
                    {
                        var text = NextStepCopy.Fill(row.For(controllers), s);
                        Assert.LessOrEqual(text.Length, NextStepCopy.CoachBudget, $"{row.Coach}/{row.Variant}: \"{text}\"");
                        Assert.IsFalse(s_Forbidden.IsMatch(text), $"{row.Coach}: dev text in \"{text}\"");
                        if (controllers) Assert.IsFalse(s_Pinch.IsMatch(text), $"{row.Coach} controllers copy says pinch: \"{text}\"");
                    }
                }
            }
            finally { NextStep.ResetHooks(); }
        }

        /// Every row × hands / controllers × the D4 / D5 / D6 / W0.4 / DemoMode variants, with the longest expansions:
        /// statuses ≤ 48, labels ≤ 30, no dev text, no "pinch" in the controllers copy, every action a known command.
        [Test]
        public void P1_EveryGeneratedStepFitsItsBudget()
        {
            LongestHooks();
            int checkedSteps = 0;
            try
            {
                foreach (var c in Cases())
                foreach (var variant in Variants(c.S))
                {
                    var s = variant;
                    s.LastQuery = "window ac";
                    s.BestSeller = string.IsNullOrEmpty(s.BestSeller) ? s.BestSeller : "Home Depot Pro Supply Company";
                    var step = NextStep.For(s);
                    if (step.Frozen) continue;
                    checkedSteps++;
                    string where = $"{c.Id} → {step.Rule}/{step.Variant} ({s.Modality})";
                    Assert.LessOrEqual(step.Status.Length, NextStepCopy.StatusBudget, $"{where}: \"{step.Status}\"");
                    Assert.IsFalse(s_Forbidden.IsMatch(step.Status), $"{where}: dev text");
                    if (s.Modality == InputModality.Controllers) Assert.IsFalse(s_Pinch.IsMatch(step.Status), $"{where}: controllers copy says pinch: \"{step.Status}\"");
                    foreach (var a in new[] { step.Primary, step.Secondary0, step.Secondary1 })
                    {
                        if (a.IsNone) continue;
                        Assert.LessOrEqual(a.Label.Length, NextStepCopy.LabelBudget, $"{where}: label \"{a.Label}\"");
                        Assert.IsFalse(a.Label.Contains("{"), $"{where}: unfilled label \"{a.Label}\"");
                        Assert.IsFalse(s_Pinch.IsMatch(a.Label), $"{where}: a label says pinch");
                        Assert.IsTrue(NextStepActions.Calls.Any(x => x.command == a.Command), $"{where}: {a.Command} has no AppCommands call");
                    }
                }
            }
            finally { NextStep.ResetHooks(); }
            Assert.Greater(checkedSteps, 500);
        }

        static IEnumerable<AppSnapshot> Variants(AppSnapshot s)
        {
            foreach (var modality in new[] { InputModality.Hands, InputModality.Controllers })
            foreach (var d4 in new[] { false, true })
            foreach (var d5 in new[] { false, true })
            foreach (var d6 in new[] { RingCommit.Settle, RingCommit.Pinch })
            foreach (var w04 in new[] { false, true })
            foreach (var demo in new[] { false, true })
            {
                var v = s;
                v.Modality = modality;
                v.Ux.D4AutoSave = d4;
                v.Ux.D5RayOnUi = d5;
                v.Ux.D6Commit = d6;
                v.Ux.W04Preserves = w04;
                v.Ux.DemoMode = demo;
                v.Ux.D1Entry = d4 ? ToolKind.Measure : ToolKind.Move;   // D1 is read only by the coach; both are covered
                yield return v;
            }
        }

        // ---------------- P2 no pay; every action is an AppCommands call ----------------

        [Test]
        public void P2_NoStepCanPay()
        {
            foreach (var name in Enum.GetNames(typeof(StepCommand)))
                Assert.IsFalse(name.IndexOf("pay", StringComparison.OrdinalIgnoreCase) >= 0, $"StepCommand.{name}");
            var r10 = Cases().First(c => c.Id == "T26");
            Assert.IsTrue(NextStep.For(r10.S).Primary.IsNone, "R10 (checkout ready) has no primary: only the physical hold pays");
            foreach (var c in Cases())
            foreach (var v in Variants(c.S))
            {
                var step = NextStep.For(v);
                if (step.Frozen) continue;
                foreach (var a in new[] { step.Primary, step.Secondary0, step.Secondary1 })
                    Assert.AreNotEqual("Pay", a.Label, $"{c.Id}: a step labelled Pay");
            }
        }

        [Test]
        public void P2_EveryStepCommandResolvesToItsAppCommandsMethod()
        {
            foreach (StepCommand cmd in Enum.GetValues(typeof(StepCommand)))
            {
                if (cmd == StepCommand.None) continue;
                var calls = NextStepActions.Calls.Where(x => x.command == cmd).ToList();
                Assert.AreEqual(1, calls.Count, $"{cmd}: one AppCommands call");
                var (_, method, args) = calls[0];
                var m = typeof(AppCommands).GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, args, null);
                Assert.IsNotNull(m, $"{cmd} → AppCommands.{method}({string.Join(", ", args.Select(a => a.Name))})");
            }
            Assert.IsNotNull(typeof(AppCommands).GetMethod(nameof(AppCommands.Redo), BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null));
        }

        // ---------------- P3 allocation-free matching ----------------

        [Test]
        public void P3_MatchAndKeyAllocateNothing()
        {
            var snaps = Cases().Select(c => c.S).ToArray();
            long sink = 0;
            foreach (var s in snaps) { sink += (long)NextStep.Match(s); sink += NextStep.Key(s); }   // JIT / static init
            long before;
            try { before = GC.GetAllocatedBytesForCurrentThread(); }
            catch (Exception e) when (e is NotSupportedException || e is NotImplementedException) { Assert.Ignore("GC.GetAllocatedBytesForCurrentThread unsupported"); return; }
            for (int i = 0; i < 1000; i++)
            {
                var s = snaps[i % snaps.Length];
                sink += (long)NextStep.Match(s);
                sink += NextStep.Key(s);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.AreEqual(0, after - before, $"bytes allocated by 1000 Match + Key (sink {sink})");
        }

        // ---------------- P4 determinism ----------------

        [Test]
        public void P4_ForIsDeterministic_KeyIgnoresTimeAndIdle()
        {
            foreach (var c in Cases())
            {
                var a = NextStep.For(c.S);
                var b = NextStep.For(c.S);
                Assert.AreEqual(a.Rule, b.Rule, c.Id);
                Assert.AreEqual(a.Status, b.Status, c.Id);
                Assert.AreEqual(a.Primary.ToString(), b.Primary.ToString(), c.Id);
                Assert.AreEqual(a.Secondary0.ToString(), b.Secondary0.ToString(), c.Id);
                Assert.AreEqual(a.Secondary1.ToString(), b.Secondary1.ToString(), c.Id);
                Assert.AreEqual(a.Flags, b.Flags, c.Id);
                Assert.AreEqual(a.Tone, b.Tone, c.Id);
                var later = c.S;
                later.Now += 17.5f;
                later.IdleSeconds += 42f;
                later.HoldProgress = 0.5f;
                Assert.AreEqual(NextStep.Key(c.S), NextStep.Key(later), $"{c.Id}: Key moved with Now / IdleSeconds");
            }
            var t = Cases().First(c => c.Id == "T10").S;
            var t2 = t; t2.LastTapeM = 0.4f;
            Assert.AreNotEqual(NextStep.Key(t), NextStep.Key(t2), "a new tape length changes the Key");
            var t3 = t; t3.Modality = InputModality.Controllers;
            Assert.AreNotEqual(NextStep.Key(t), NextStep.Key(t3), "the modality changes the Key");
            var t12 = Cases().First(c => c.Id == "T12").S;
            var t12x = t12; t12x.SessionCrossing = OutlineCrossing.Closing;
            Assert.AreNotEqual(NextStep.Key(t12), NextStep.Key(t12x), "a crossing outline changes the Key");
        }

        // ---------------- P5 every rule reachable ----------------

        [Test]
        public void P5_EveryRuleIsReachable()
        {
            var hit = new HashSet<RuleId>(Cases().Select(c => NextStep.Match(c.S)));
            foreach (var c in Cases()) hit.Add(NextStep.MatchBase(c.S));
            var missing = Enum.GetValues(typeof(RuleId)).Cast<RuleId>().Where(r => r != RuleId.R55 && !hit.Contains(r)).ToList();
            Assert.IsEmpty(missing, "rules no row reaches: " + string.Join(", ", missing));
            Assert.AreEqual(56, Enum.GetValues(typeof(RuleId)).Length, "56 rules, R00–R55");
        }

        // ---------------- pure helpers ----------------

        [TestCase(TapeTarget.Door, "cabinet hinge", "hinges")]
        [TestCase(TapeTarget.Drawer, "drawer slide", "slides")]
        [TestCase(TapeTarget.Panel, "shelf bracket", "brackets")]
        [TestCase(TapeTarget.Run, "gutter hanger", "hangers")]
        [TestCase(TapeTarget.Appliance, null, "parts")]
        [TestCase(TapeTarget.Unknown, null, "parts")]
        public void QueryForAndNoun(TapeTarget target, string query, string plural)
        {
            Assert.AreEqual(query, NextStepCopy.QueryFor(target));
            Assert.AreEqual(plural, NextStepCopy.Noun(query, true));
        }

        [Test]
        public void NounMatchesTheW09Table()
        {
            foreach (var q in new[] { "cabinet hinge", "drawer slide", "shelf bracket", "gutter hanger", "window ac", "cabinet knob", "your request", "soft-close hinge", null })
                Assert.AreEqual(AirTools.UI.Copy.Noun(null, q), NextStepCopy.Noun(q, false), $"noun for \"{q}\"");
            Assert.AreEqual("AC units", NextStepCopy.Noun("window ac", true));
            Assert.AreEqual("Home Depot Pr…", NextStepCopy.Seller("Home Depot Pro Supply"));
            Assert.AreEqual("Lowe's", NextStepCopy.Seller("Lowe's"));
        }

        [TestCase(FitStatus.Red, 1, 0, 3, "Hits Wall/Above", FitReason.Hits)]
        [TestCase(FitStatus.Red, 0, 0, 3, "Too wide by 12 mm for the 390 mm tape", FitReason.TooWide)]
        [TestCase(FitStatus.Red, 0, 0, 3, "Window too wide by 40 mm\n900 mm opening", FitReason.TooNarrow)]
        [TestCase(FitStatus.Red, 0, 0, 3, "Window too narrow by 40 mm\n500 mm opening", FitReason.TooWide)]
        [TestCase(FitStatus.Amber, 0, 0, -1, "Measure the window width to check the fit", FitReason.NeedsTape)]
        [TestCase(FitStatus.Amber, 0, 0, 0, "Not mounted on a surface", FitReason.NotMounted)]
        [TestCase(FitStatus.Amber, 0, 0, 1, "Overhangs: only the centre of the mounting face touches", FitReason.Overhang)]
        [TestCase(FitStatus.Amber, 0, 2, 4, "Needs 30 mm clear above (Wall/Above)", FitReason.Clearance)]
        [TestCase(FitStatus.Green, 0, 0, 4, "Fits, 12 mm spare", FitReason.None)]
        public void FitReasonFromTheReport(FitStatus status, int collisions, int clearance, int samples, string headline, FitReason expected) =>
            Assert.AreEqual(expected, NextStep.ReasonOf(status, collisions, clearance, samples, headline));

        [TestCase(ToolKind.Measure, "miss", 0, Refusal.MeasureMiss)]
        [TestCase(ToolKind.Measure, "ignored duplicate point", 1, Refusal.DuplicatePoint)]
        [TestCase(ToolKind.Measure, "finish ignored (need 2+ points)", 1, Refusal.FinishTooSoon)]
        [TestCase(ToolKind.Measure, "finish ignored (need 2+ points)", 0, Refusal.None)]
        [TestCase(ToolKind.Measure, "point 1 (corner)", 1, Refusal.None)]
        [TestCase(ToolKind.Measure, "finish refused: edges cross", 6, Refusal.EdgesCross)]
        [TestCase(ToolKind.Measure, "move refused: edges cross (#3)", 0, Refusal.None)]
        [TestCase(ToolKind.Move, "teleport: nothing there", 0, Refusal.TeleportNothing)]
        [TestCase(ToolKind.Move, "teleport: aim at the floor to walk", 0, Refusal.TeleportNotFlat)]
        [TestCase(ToolKind.Part, "no surface under the pointer", 0, Refusal.PartNoSurface)]
        [TestCase(ToolKind.Level, "miss", 0, Refusal.LevelMiss)]
        [TestCase(ToolKind.None, "miss", 0, Refusal.None)]
        public void RefusalFromTheToolsLastAction(ToolKind tool, string last, int points, Refusal expected) =>
            Assert.AreEqual(expected, NextStepActions.RefusalFromTool(tool, last, points));

        [Test]
        public void RefusalFromARefusedStepAction()
        {
            Assert.AreEqual(Refusal.NothingToUndo, NextStepActions.RefusalFor(StepCommand.Undo, null));
            Assert.AreEqual(Refusal.ArraySingleUnit, NextStepActions.RefusalFor(StepCommand.PlaceArray, "array: Euro hinge is a single unit (its listing has no spacing)"));
            Assert.AreEqual(Refusal.ArrayNoTape, NextStepActions.RefusalFor(StepCommand.PlaceArray, "array: measure the run first (two points along it)"));
            Assert.AreEqual(Refusal.SellersNoPart, NextStepActions.RefusalFor(StepCommand.ShowSellers, null));
            Assert.AreEqual(Refusal.None, NextStepActions.RefusalFor(StepCommand.EquipTool, null));
            // Every refusal has words.
            foreach (Refusal r in Enum.GetValues(typeof(Refusal)))
                if (r != Refusal.None) Assert.IsNotEmpty(NextStepCopy.Status(RuleId.R01, r.ToString(), false), $"R01 copy for {r}");
        }

        [Test]
        public void InputActivityCountsIdleTime()
        {
            InputActivity.Reset(10f);
            Assert.AreEqual(0f, InputActivity.IdleSeconds(10f));
            Assert.AreEqual(4f, InputActivity.IdleSeconds(14f), 1e-5f);
            InputActivity.Touch(14f);
            Assert.AreEqual(0.5f, InputActivity.IdleSeconds(14.5f), 1e-5f);
            InputActivity.Touch(12f);   // an older stamp never moves it back
            Assert.AreEqual(14f, InputActivity.LastAt);
            Assert.AreEqual(0f, InputActivity.IdleSeconds(13f), "never negative");
            InputActivity.Reset();
        }

        // ---------------- coach (§4, CT1–CT8) ----------------

        static AppSnapshot Fresh(float idle)
        {
            var s = Base();
            s.Mode = AppMode.Passthrough;
            s.EverEnteredWorld = false;
            s.Now = 100f;
            s.IdleSeconds = idle;
            return s;
        }

        [Test] public void CT1_EnterWaitsForFourIdleSeconds() => Assert.AreEqual(CoachRuleId.None, CoachRules.Pick(Fresh(3.9f), new CoachState()));
        [Test] public void CT2_EnterAfterFourIdleSeconds() => Assert.AreEqual(CoachRuleId.C01, CoachRules.Pick(Fresh(4f), new CoachState()));

        static AppSnapshot RingHint()
        {
            var s = Base();
            s.Shapes = 1;
            s.RingOpens = 0;
            s.Now = 100f;
            s.IdleSeconds = 6f;
            return s;
        }

        [Test]
        public void CT3_PalmUpForMoreTools()
        {
            var s = RingHint();
            Assert.AreEqual(CoachRuleId.C12, CoachRules.Pick(s, new CoachState()));
            Assert.AreEqual("More tools: turn your left palm up", CoachRules.Text(CoachRuleId.C12, s));
            s.Modality = InputModality.Controllers;
            StringAssert.Contains("menu button", CoachRules.Text(CoachRuleId.C12, s));
        }

        [Test]
        public void CT4_TwoShowsAreEnough()
        {
            var st = new CoachState();
            st.Shows[(int)CoachRuleId.C12] = 2;
            Assert.AreEqual(CoachRuleId.None, CoachRules.Pick(RingHint(), st));
        }

        [Test]
        public void CT5_RingWithAPartInHandPreempts()
        {
            var s = Base();
            s.RingOpen = true; s.PartHeld = true; s.Ux.D6Commit = RingCommit.Settle; s.IdleSeconds = 0f; s.RingInteracting = true; s.Now = 100f;
            var st = new CoachState { LastDismissAt = 99.5f };   // even right after another hint
            Assert.AreEqual(CoachRuleId.C13, CoachRules.Pick(s, st));
            // It pre-empts a hint already on screen.
            var st2 = new CoachState { Current = CoachRuleId.C09 };
            s.HeldOnSurface = true; s.RingInteracting = false;
            Assert.AreEqual(CoachChange.Replaced, CoachRules.Update(s, st2));
            Assert.AreEqual(CoachRuleId.C13, st2.Current);
            s.Ux.W04Preserves = true;   // once W0.4 parks the part the warning is moot
            Assert.AreNotEqual(CoachRuleId.C13, CoachRules.Pick(s, new CoachState()));
        }

        [Test]
        public void CT6_NoFinishHintWhenTapesAutoSave()
        {
            var s = Base();
            s.SessionPoints = 2; s.IdleSeconds = 10f; s.Now = 100f;
            s.Ux.D4AutoSave = true; s.MeasureMode = MeasureMode.Line;
            Assert.AreNotEqual(CoachRuleId.C05, CoachRules.Pick(s, new CoachState()));
            s.Ux.D4AutoSave = false; s.MeasureMode = MeasureMode.Explicit;
            Assert.AreEqual(CoachRuleId.C05, CoachRules.Pick(s, new CoachState()));
            Assert.AreEqual("Pinch your left hand once to save", CoachRules.Text(CoachRuleId.C05, s));
        }

        [Test]
        public void CT7_HoldToPayHint()
        {
            var s = Base();
            s.Checkout = CheckoutState.Ready; s.IdleSeconds = 3f; s.Now = 100f;
            Assert.AreEqual(CoachRuleId.C10, CoachRules.Pick(s, new CoachState()));
            s.Checkout = CheckoutState.Paying;
            Assert.AreEqual(CoachRuleId.None, CoachRules.Pick(s, new CoachState()));
        }

        [Test]
        public void CT8_NothingDuringATransition()
        {
            var a = Fresh(10f); a.Transitioning = true;
            Assert.AreEqual(CoachRuleId.None, CoachRules.Pick(a, new CoachState()));
            var b = Base(); b.RingOpen = true; b.PartHeld = true; b.Ux.D6Commit = RingCommit.Settle; b.Transitioning = true;
            Assert.AreEqual(CoachRuleId.None, CoachRules.Pick(b, new CoachState()));
        }

        [Test]
        public void CoachLifecycle_ShowDismissGapAndCount()
        {
            var st = new CoachState();
            var s = Fresh(5f);
            s.Now = 10f;
            Assert.AreEqual(CoachChange.Shown, CoachRules.Update(s, st));
            Assert.AreEqual(CoachRuleId.C01, st.Current);
            Assert.AreEqual(1, st.Shows[(int)CoachRuleId.C01]);
            s.Now = 10.25f; s.IdleSeconds = 0f;   // the wearer moves: the hint stays until it's done
            Assert.AreEqual(CoachChange.None, CoachRules.Update(s, st));
            s.Mode = AppMode.World; s.EverEnteredWorld = true; s.Newest = StepEvent.Mode; s.Now = 11f;   // entered: done
            Assert.AreEqual(CoachChange.Dismissed, CoachRules.Update(s, st));
            Assert.AreEqual(CoachRuleId.None, st.Current);
            s.IdleSeconds = 30f; s.Now = 12f;   // C03 is due, but not within 3 s of the last one
            Assert.AreEqual(CoachChange.None, CoachRules.Update(s, st));
            s.Now = 14.1f;
            Assert.AreEqual(CoachChange.Shown, CoachRules.Update(s, st));
            Assert.AreEqual(CoachRuleId.C03, st.Current);
            Assert.AreEqual("Point at a door corner and pinch", CoachRules.Text(st.Current, s));
            s.Transitioning = true; s.Now = 15f;   // a fade hides it without a ✓
            Assert.AreEqual(CoachChange.Hidden, CoachRules.Update(s, st));
        }

        [Test]
        public void CoachNextPillHintWaitsForAnUnchangedStep()
        {
            var st = new CoachState();
            var s = Base();
            s.Shapes = 1; s.LastReading = ReadingKind.Tape; s.LastTapeM = 0.39f; s.LastTapeTarget = TapeTarget.Door; s.Newest = StepEvent.Tape;
            s.RingOpens = 1;   // no C12
            s.Now = 50f;
            st.ObserveStep(NextStep.For(s), 50f);
            s.IdleSeconds = 9f; s.Now = 57.9f;
            Assert.AreEqual(CoachRuleId.None, CoachRules.Pick(s, st));
            s.Now = 58.1f;
            Assert.AreEqual(CoachRuleId.C07, CoachRules.Pick(s, st));
            st.ActionRan(58.2f);
            s.Now = 58.3f;
            Assert.IsFalse(CoachRules.Holds(CoachRuleId.C07, s, st), "a pill tap dismisses it");
        }

        // ---------------- the judge's kitchen path (§0, §6.2) ----------------

        /// A minimal model of the app's reaction to each judge action, driven only by what the rail offers: a judge
        /// taps the pill's primary (DoNext) or does the one world action the status line asks for.
        sealed class Judge
        {
            public AppSnapshot S;
            public int Actions, RingOpens;
            public readonly List<string> Trail = new List<string>();

            public Judge(ToolKind d1, bool d4)
            {
                S = Base();
                S.Mode = AppMode.Passthrough; S.EverEnteredWorld = false; S.Tool = ToolKind.None; S.Newest = StepEvent.None; S.LastQuery = null;
                S.Ux.D1Entry = d1; S.Ux.D4AutoSave = d4; S.MeasureMode = d4 ? MeasureMode.Line : MeasureMode.Explicit;
                S.Ux.W04Preserves = true; S.Ux.W16OfflineReceipt = false; S.Ux.DemoMode = true;   // as on backend-integration
            }

            public Step Expect(RuleId rule)
            {
                var step = NextStep.For(S);
                Trail.Add($"{step.Rule} \"{step.Status}\"{(step.PillVisible ? " [" + step.Primary.Label + "]" : "")}");
                Assert.AreEqual(rule, step.Rule, $"after {Actions} actions: {string.Join(" → ", Trail)}");
                Assert.AreEqual(0, RingOpens);
                return step;
            }

            void Act(string what) { Actions++; Trail.Add($"J{Actions} {what}"); }

            public void PressEnter()
            {
                Act("press Enter world");
                S.Mode = AppMode.World; S.EverEnteredWorld = true;
                S.Tool = S.Ux.D1Entry; S.Newest = StepEvent.Tool;   // ToolManager equips Default on entering
            }

            public void Pinch()
            {
                var rule = NextStep.Match(S);
                Act($"pinch ({rule})");
                switch (rule)
                {
                    case RuleId.R52:
                    case RuleId.R53:
                        S.SessionPoints = 1; break;
                    case RuleId.R30:
                        S.SessionPoints = 2; S.SessionLengthM = 0.3891f;
                        if (S.Ux.D4AutoSave && S.MeasureMode != MeasureMode.Area) SaveTape();
                        break;
                    case RuleId.R25:
                    case RuleId.R27:
                        S.PartHeld = false; S.HeldOnSurface = false; S.SelectedPlaced = true; S.SelectedFit = FitStatus.Green; S.SelectedHasTape = true;
                        S.PlacedCount++; S.Newest = StepEvent.Placed;
                        break;
                    default: Assert.Fail($"the rail asked for no pinch at {rule}"); break;
                }
            }

            void SaveTape()
            {
                S.SessionPoints = 0; S.Shapes++; S.LastReading = ReadingKind.Tape; S.LastTapeM = 0.3891f; S.LastTapeTarget = TapeTarget.Door;
                S.LastTapeAxis = TapeAxis.W; S.NotebookCount++; S.Newest = StepEvent.Tape;
            }

            public void DoNext()
            {
                var step = NextStep.For(S);
                Assert.IsTrue(step.PillVisible, $"no pill to tap at {step.Rule}");
                var a = step.Primary;
                Act($"tap \"{a.Label}\" → {a.Command}({a.ArgText})");
                switch (a.Command)
                {
                    case StepCommand.EquipTool: S.Tool = a.Arg == "move" ? ToolKind.Move : ToolKind.Measure; S.Newest = StepEvent.Tool; break;
                    case StepCommand.FinishShape: SaveTape(); break;
                    case StepCommand.FindPart: S.LastQuery = a.Arg; S.Searching = true; S.SearchElapsed = 0.5f; S.Candidates = 0; S.Newest = StepEvent.Search; break;
                    case StepCommand.SelectCandidate: S.Loading = true; S.LastSelectedIndex = a.Index; break;
                    case StepCommand.ShowSellers: S.SellersOpen = true; S.SellersRefreshing = true; break;
                    case StepCommand.StartCheckout:
                        S.SellersOpen = false; S.Checkout = CheckoutState.Ready; S.CheckoutTotal = S.BestPrice;   // the checkout takes the window slot
                        break;
                    case StepCommand.TakeHome:
                        Assert.AreNotEqual(AppMode.Passthrough, S.Mode);
                        S.Mode = AppMode.Passthrough; S.Checkout = CheckoutState.Closed; S.OnTable = 1; S.Newest = StepEvent.Mode;
                        break;
                    default: Assert.Fail($"unexpected primary {a}"); break;
                }
            }

            public void HoldPay()
            {
                Assert.AreEqual(RuleId.R10, NextStep.Match(S), "hold Pay only on the checkout");
                Act("hold Pay 1 s");
                S.Checkout = CheckoutState.Paying;
            }

            // The app's own progress (not judge actions).
            public void SearchReturns() { S.Searching = false; S.Candidates = 3; S.Newest = StepEvent.Search; }
            public void PartLoads() { S.Loading = false; S.PartHeld = true; S.Tool = ToolKind.Part; S.Newest = StepEvent.Tool; }
            public void AimAtTheDoor() { S.HeldOnSurface = true; S.HeldFit = FitStatus.Green; }
            public void SellersRefresh() { S.SellersRefreshing = false; S.BestSeller = "Home Depot"; S.BestPrice = 7.59f; S.RecommendedSeller = 0; }
            public void OfflineReceipt() { S.Checkout = CheckoutState.Paid; S.ReceiptAuthorized = false; S.Purchases = 1; S.NotebookCount++; S.Newest = StepEvent.Purchase; }
        }

        public static string LastPathReport = "";

        /// 10 judge actions and 0 ring openings with D1 = Measure + D4 = auto; each of D1 = Move / D4 = explicit adds one.
        [TestCase(ToolKind.Measure, true, 10)]
        [TestCase(ToolKind.Move, true, 11)]
        [TestCase(ToolKind.Measure, false, 11)]
        [TestCase(ToolKind.Move, false, 12)]
        public void KitchenPath(ToolKind d1, bool d4AutoSave, int expectedActions)
        {
            var j = new Judge(d1, d4AutoSave);
            j.Expect(RuleId.R05);
            j.PressEnter();
            if (d1 == ToolKind.Move) { j.Expect(RuleId.R49); j.DoNext(); }
            Assert.AreEqual("Measure a door: pinch one corner", j.Expect(RuleId.R52).Status);
            j.Pinch();
            j.Expect(RuleId.R30);
            j.Pinch();
            if (!d4AutoSave) { j.Expect(RuleId.R31); j.DoNext(); }
            var saved = j.Expect(RuleId.R42);
            Assert.AreEqual("Find hinges for this door", saved.Primary.Label);
            Assert.AreEqual("cabinet hinge", saved.Primary.Arg);
            j.DoNext();
            Assert.AreEqual("Finding hinges that fit 0.39 m…", j.Expect(RuleId.R19).Status);
            j.SearchReturns();
            Assert.AreEqual("Hold the top pick", j.Expect(RuleId.R39).Primary.Label);
            j.DoNext();
            j.Expect(RuleId.R21);
            j.PartLoads();
            Assert.AreEqual("Aim at the door to place the hinge", j.Expect(RuleId.R24).Status);
            j.AimAtTheDoor();
            j.Expect(RuleId.R25);
            j.Pinch();
            Assert.AreEqual("Compare prices", j.Expect(RuleId.R34).Primary.Label);
            j.DoNext();
            j.Expect(RuleId.R28);
            j.SellersRefresh();
            Assert.AreEqual("Pay with Visa", j.Expect(RuleId.R29).Primary.Label);
            j.DoNext();
            Assert.IsFalse(j.Expect(RuleId.R10).PillVisible, "the pill never pays");
            j.HoldPay();
            j.Expect(RuleId.R08);
            j.OfflineReceipt();
            Assert.AreEqual("Take it home", j.Expect(RuleId.R12).Primary.Label);
            j.DoNext();
            Assert.AreEqual("See your hinge on the table", j.Expect(RuleId.R04).Status);
            LastPathReport = $"D1={d1} D4={(d4AutoSave ? "auto" : "explicit")}: {j.Actions} actions, {j.RingOpens} ring openings\n  " + string.Join("\n  ", j.Trail);
            Say(LastPathReport);
            Assert.AreEqual(expectedActions, j.Actions, LastPathReport);
            Assert.AreEqual(0, j.RingOpens);
        }

        /// To the test's output (the console when run outside an NUnit test context, e.g. an offline runner).
        static void Say(string text)
        {
            try { TestContext.WriteLine(text); }
            catch (NullReferenceException) { Console.WriteLine(text); }
        }
    }
}
