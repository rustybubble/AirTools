using AirTools.Core;
using UnityEngine;

namespace AirTools.UI
{
    /// Ghost hands for coach hints (UX W1.3 §4): a 40 % alpha ISDK ghost hand looping between two HandPoses for ~1.4 s
    /// (a static pose with Reduce motion). A stub for now — it records the pose it was asked for so the harness and
    /// tests can see it, and draws nothing.
    /// TODO(W1.3 XR): instantiate ISDK OpenXRGhost-Hand{Left,Right}.prefab under the rig, drive the two HandPoses per
    /// CoachPose (poke, pinch, left pinch, point at the floor, palm-down → palm-up, press-and-hold), align it at the
    /// coach's anchor, then wire it in GuideRailBuilder (CoachService.hand). Alignment is a headset [H] check.
    public class CoachHand : MonoBehaviour
    {
        public CoachPose Pose { get; private set; }
        public int Plays { get; private set; }

        public void Play(CoachPose pose)
        {
            if (pose == Pose) return;
            Pose = pose;
            if (pose != CoachPose.None) Plays++;
        }

        public void Stop() => Pose = CoachPose.None;
    }
}
