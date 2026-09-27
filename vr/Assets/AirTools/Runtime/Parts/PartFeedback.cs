using AirTools.Input;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Parts
{
    /// The "clunk + haptic" when a part seats on a surface (SPEC M4), through the feedback hub: the fit decides the
    /// controller pattern (success / warning / error), never a buzzer for a good fit.
    public static class PartFeedback
    {
        public static void Seated(Vector3 position, ToolHand hand, FitStatus status = FitStatus.Green) =>
            FeedbackEvents.PartSeated(position, status, hand == ToolHand.Left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch);
    }
}
