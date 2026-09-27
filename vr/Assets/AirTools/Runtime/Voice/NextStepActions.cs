using System;
using AirTools.Core;

namespace AirTools
{
    /// The one dispatcher for guide-rail actions (UX W1.3 §3.5): the Next-step pill, AgentHarness.DoNext and the
    /// presenter (W1.8) all run a StepAction here, and every StepCommand is exactly one AppCommands call. Nothing here
    /// can pay: StepCommand has no Pay member, and StartCheckout only opens the checkout (the 1 s hold pays).
    public static class NextStepActions
    {
        /// (action, ok) after every run. GuideRail turns a refusal into the R01 overlay and restarts the coach clock.
        public static event Action<StepAction, bool> Ran;

        public static int Runs { get; private set; }
        public static string LastRun { get; private set; } = "";

        /// The AppCommands method (and parameter types) each StepCommand calls — the §3.5 table, checked by reflection
        /// in NextStepTests (P2).
        public static readonly (StepCommand command, string method, Type[] args)[] Calls =
        {
            (StepCommand.OpenWorld, nameof(AppCommands.OpenChest), Type.EmptyTypes),
            (StepCommand.TakeHome, nameof(AppCommands.ToggleChest), Type.EmptyTypes),
            (StepCommand.EquipTool, nameof(AppCommands.EquipTool), new[] { typeof(string) }),
            (StepCommand.FindPart, nameof(AppCommands.FindPart), new[] { typeof(string) }),
            (StepCommand.SelectCandidate, nameof(AppCommands.SelectCandidate), new[] { typeof(int) }),
            (StepCommand.ShowSellers, nameof(AppCommands.ShowSellers), new[] { typeof(string) }),
            (StepCommand.StartCheckout, nameof(AppCommands.StartCheckout), new[] { typeof(int) }),
            (StepCommand.PlaceArray, nameof(AppCommands.PlaceArray), new[] { typeof(float?) }),
            (StepCommand.SetTabletop, nameof(AppCommands.SetTabletop), new[] { typeof(bool) }),
            (StepCommand.ShowNotebook, nameof(AppCommands.ShowNotebook), new[] { typeof(bool) }),
            (StepCommand.ExportNotebook, nameof(AppCommands.ExportNotebook), Type.EmptyTypes),
            (StepCommand.LoadSite, nameof(AppCommands.LoadSite), new[] { typeof(string) }),
            (StepCommand.FinishShape, nameof(AppCommands.FinishShape), Type.EmptyTypes),
            (StepCommand.Undo, nameof(AppCommands.Undo), Type.EmptyTypes),
            (StepCommand.ShowFindParts, nameof(AppCommands.ShowFindParts), Type.EmptyTypes),
            (StepCommand.CloseWindows, nameof(AppCommands.CloseWindows), Type.EmptyTypes),
            (StepCommand.ShowScenePanel, nameof(AppCommands.ShowScenePanel), new[] { typeof(bool) }),
            (StepCommand.UseOfflineReceipt, nameof(AppCommands.UseOfflineReceipt), Type.EmptyTypes),
            (StepCommand.StepIn, nameof(AppCommands.StepIn), Type.EmptyTypes),
        };

        /// Run a step action through AppCommands. False when it was refused (or is None).
        public static bool Run(in StepAction a)
        {
            if (a.IsNone) return false;
            bool ok;
            switch (a.Command)
            {
                case StepCommand.OpenWorld: AppCommands.OpenChest(); ok = true; break;
                // Offered only inside (World / Tabletop): from passthrough ToggleChest would open the world instead.
                case StepCommand.TakeHome: ok = AppState.Mode != AppMode.Passthrough; if (ok) AppCommands.ToggleChest(); break;
                case StepCommand.EquipTool: ok = AppCommands.EquipTool(a.Arg); break;
                case StepCommand.FindPart: ok = AppCommands.FindPart(a.Arg); break;
                case StepCommand.SelectCandidate: ok = AppCommands.SelectCandidate(a.Index); break;
                case StepCommand.ShowSellers: ok = AppCommands.ShowSellers(a.Arg); break;
                case StepCommand.StartCheckout: ok = AppCommands.StartCheckout(a.Index); break;
                case StepCommand.PlaceArray: ok = AppCommands.PlaceArray(null); break;
                case StepCommand.SetTabletop: AppCommands.SetTabletop(a.Flag); ok = true; break;
                case StepCommand.ShowNotebook: AppCommands.ShowNotebook(a.Flag); ok = true; break;
                case StepCommand.ExportNotebook: ok = AppCommands.ExportNotebook() != null; break;
                case StepCommand.LoadSite: ok = AppCommands.LoadSite(a.Arg); break;
                case StepCommand.FinishShape: ok = AppCommands.FinishShape(); break;
                case StepCommand.Undo: ok = AppCommands.Undo(); break;
                case StepCommand.ShowFindParts: ok = AppCommands.ShowFindParts(); break;
                case StepCommand.CloseWindows: AppCommands.CloseWindows(); ok = true; break;
                case StepCommand.ShowScenePanel: ok = AppCommands.ShowScenePanel(a.Flag); break;
                case StepCommand.UseOfflineReceipt: ok = AppCommands.UseOfflineReceipt(); break;
                case StepCommand.StepIn: ok = AppCommands.StepIn(); break;
                default: ok = false; break;
            }
            Runs++;
            LastRun = $"{a.Label} → {a.Command}({a.ArgText}) {(ok ? "ok" : "failed")}";
            Log.Info($"NextStep: ran {LastRun}");
            Ran?.Invoke(a, ok);
            return ok;
        }

        /// Which silent "no" a tool just gave (spec §3.6), from the equipped tool's raw LastAction at the moment it
        /// signalled a miss (FeedbackEvents.Miss, fired by every InputHints.Say): MeasureTool "miss" / "ignored duplicate
        /// point" / "finish ignored…" (only with one point down, as MeasureTool itself) / "finish refused: edges cross",
        /// Locomotion "teleport: …",
        /// PartTool "no surface under the pointer", LevelTool "miss". Pure.
        public static Refusal RefusalFromTool(AirTools.Tools.ToolKind tool, string lastAction, int sessionPoints)
        {
            var a = lastAction ?? "";
            switch (tool)
            {
                case AirTools.Tools.ToolKind.Measure:
                    if (a == "miss") return Refusal.MeasureMiss;
                    if (a.StartsWith("ignored duplicate", StringComparison.Ordinal)) return Refusal.DuplicatePoint;
                    if (a.StartsWith("finish ignored", StringComparison.Ordinal) && sessionPoints == 1) return Refusal.FinishTooSoon;
                    if (a.StartsWith(AirTools.Tools.MeasureTool.CrossRefused, StringComparison.Ordinal)) return Refusal.EdgesCross;
                    return Refusal.None;
                case AirTools.Tools.ToolKind.Move:
                    if (a.StartsWith("teleport: nothing", StringComparison.Ordinal)) return Refusal.TeleportNothing;
                    if (a.StartsWith("teleport: aim at the floor", StringComparison.Ordinal)) return Refusal.TeleportNotFlat;
                    return Refusal.None;
                case AirTools.Tools.ToolKind.Part:
                    return a.StartsWith("no surface", StringComparison.Ordinal) ? Refusal.PartNoSurface : Refusal.None;
                case AirTools.Tools.ToolKind.Level:
                    return a == "miss" ? Refusal.LevelMiss : Refusal.None;
                default:
                    return Refusal.None;
            }
        }

        /// Why a refused step action said no, for the R01 overlay. `partToolLastAction` = PartTool.LastAction (the raw
        /// array refusal, Parts/PartTool.cs PlaceArray). Pure.
        public static Refusal RefusalFor(StepCommand command, string partToolLastAction)
        {
            switch (command)
            {
                case StepCommand.Undo: return Refusal.NothingToUndo;
                case StepCommand.PlaceArray:
                    var a = partToolLastAction ?? "";
                    if (a.Contains("single unit")) return Refusal.ArraySingleUnit;
                    if (a.Contains("measure the run first")) return Refusal.ArrayNoTape;
                    if (a.Contains("place one part first")) return Refusal.SellersNoPart;
                    return Refusal.None;
                case StepCommand.ShowSellers:
                case StepCommand.StartCheckout:
                    return Refusal.SellersNoPart;
                default:
                    return Refusal.None;
            }
        }
    }
}
