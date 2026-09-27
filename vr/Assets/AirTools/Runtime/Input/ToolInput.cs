using System;
using AirTools.Core;
using UnityEngine;

namespace AirTools.Input
{
    public enum ToolHand { Left = 0, Right = 1 }

    /// Non-pointer tool actions (controller B/Y = Finish, A/X = Undo; toolbox buttons raise the same). edit6dof: Context —
    /// the controller's grip squeeze, unused by every tool: on a placed part it opens its menu (Edit / View similar /
    /// Delete); tools ignore it.
    public enum ToolButton { Finish, Undo, Clear, Redo, Context }

    /// One input abstraction for all tools (SPEC §3.2). Hands: index pinch. Controllers: trigger.
    /// Poses are pointer rays: position = ray origin, forward = ray direction (tools raycast against SceneSurface).
    public interface IToolInput
    {
        event Action<ToolHand, Pose> PressStart;
        event Action<ToolHand, Pose> PressMove;
        event Action<ToolHand, Pose> PressEnd;
        event Action<ToolHand, ToolButton> ButtonDown;
        Pose GetPointer(ToolHand hand);
        bool HasPointer(ToolHand hand);
        ToolHand LastActiveHand { get; }
    }
}
