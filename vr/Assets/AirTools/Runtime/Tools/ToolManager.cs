using System;
using AirTools.Core;
using AirTools.Input;
using Oculus.Interaction;
using UnityEngine;

namespace AirTools.Tools
{
    public enum ToolKind { None, Measure, Level, Part, Move, Ladder }

    /// Which tool is in hand. AppCommands.EquipTool (voice + UI) and the toolbox buttons both come through here.
    public class ToolManager : MonoBehaviour
    {
        public MeasureTool measure;
        public LevelTool level;
        public AirTools.Parts.PartTool parts;
        public Locomotion locomotion;
        public LadderTool ladder;   // P6/P7

        public ToolKind Active { get; private set; } = ToolKind.None;
        public event Action<ToolKind> Changed;

        /// The tool in hand when you enter the world (and what the ring shows first).
        /// UX decision D1 (SPEC §9): Move since Sun 09-27 (headset: the tape armed on entry got in the way — it was only
        /// the first tool we built); Measure is one ring detent away. Before, from Sat 09-26, the tape (Line mode).
        /// DemoWalkthrough "tool.default" expects it.
        public const ToolKind Default = ToolKind.Move;

        /// After a part lands — from the hand, the Edit view's new part, or Grok's place_part — the default tool is in hand
        /// again (D1), not the part tool or a tape.
        public bool DefaultAfterPlace = true;
        bool m_AfterPlace;

        void OnEnable()
        {
            Services.Register(this);
            AppState.Changed += OnMode;
            if (parts != null) parts.PartPlaced += OnPartPlaced;
        }

        void OnDisable()
        {
            Services.Unregister(this);
            AppState.Changed -= OnMode;
            if (parts != null) parts.PartPlaced -= OnPartPlaced;
        }

        void OnMode(AppMode from, AppMode to)
        {
            if (to == AppMode.World && Active == ToolKind.None) Equip(Default);
        }

        void OnPartPlaced(AirTools.Parts.PartInstance part) => m_AfterPlace = DefaultAfterPlace;

        // A frame later, so the placement's own listeners (the Edit view closing, the snap into an opening) run first.
        void Update()
        {
            if (!m_AfterPlace) return;
            m_AfterPlace = false;
            AfterPlace();
        }

        /// The switch after a placement: the default tool, unless you're out of the world or already hold the next part.
        public bool AfterPlace()
        {
            if (AppState.Mode != AppMode.World || Active == Default || (parts != null && parts.Held != null)) return false;
            Equip(Default);
            Log.Info($"Tools: {Default} in hand after placing a part");
            return true;
        }

        /// Back to the default mode.
        public void ResetToDefault() => Equip(Default);

        public void Equip(ToolKind kind)
        {
            Active = kind;
            if (measure != null) measure.Equip(kind == ToolKind.Measure);
            if (level != null) level.Equip(kind == ToolKind.Level);
            if (parts != null) parts.Equip(kind == ToolKind.Part);
            if (locomotion != null) locomotion.Equip(kind == ToolKind.Move);
            if (ladder != null) ladder.Equip(kind == ToolKind.Ladder);   // P6/P7
            Changed?.Invoke(kind);
        }

        public void Toggle(ToolKind kind) => Equip(Active == kind ? ToolKind.None : kind);

        public static bool TryParse(string name, out ToolKind kind)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "measure": case "tape": case "ruler": case "distance": case "area": case "angle": case "protractor":
                    kind = ToolKind.Measure; return true;
                case "level": kind = ToolKind.Level; return true;
                case "part": case "parts": case "hand": kind = ToolKind.Part; return true;
                case "move": case "fly": case "walk": case "teleport": kind = ToolKind.Move; return true;
                case "ladder": case "ladders": case "ladder check": kind = ToolKind.Ladder; return true;   // P6/P7
                case "none": case "": case "off": kind = ToolKind.None; return true;
                default: kind = ToolKind.None; return false;
            }
        }
    }
}
