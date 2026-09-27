using Oculus.Interaction;
using UnityEngine;

namespace AirTools.Input
{
    /// Feeds the ToolInputHub from the real rig: pointer ray = the active ISDK RayInteractor for that side
    /// (controller ray or hand ray, whichever ISDK has enabled), press = controller trigger or index pinch.
    /// Controller B/Y → Finish, A/X → Undo. Hands: thumb + middle-finger pinch → Finish (the "done" gesture);
    /// an index pinch never fires while the middle finger is the one pinching.
    public class OvrToolInputSource : MonoBehaviour
    {
        public ToolInputHub hub;
        [Tooltip("Left then right. Each side lists its controller ray and hand ray interactors.")]
        public RayInteractor[] leftRays;
        public RayInteractor[] rightRays;
        public OVRHand leftHand;
        public OVRHand rightHand;
        [Tooltip("edit6dof: the grip poses (OVRCameraRig's hand anchors: the controller's grip, or the tracked hand's wrist). " +
                 "A 6-DoF grab turns the part with these; the ray has no roll.")]
        public Transform leftGrip, rightGrip;

        [Range(0f, 1f)] public float pressThreshold = 0.55f;
        [Range(0f, 1f)] public float releaseThreshold = 0.35f;
        [Range(0f, 1f)] public float donePinch = 0.8f;
        [Range(0f, 1f)] public float doneRelease = 0.4f;

        readonly bool[] m_Down = new bool[2];
        readonly bool[] m_MiddleDown = new bool[2];
        readonly bool[] m_Suppressed = new bool[2];   // a pinch that began on UI / the menu: ignored until released
        readonly DoubleTap[] m_DoubleTap = { new DoubleTap(), new DoubleTap() };
        readonly bool[] m_SecondaryDown = new bool[2];
        readonly bool[] m_FinishPinch = new bool[2];   // a menu-hand pinch that began while a tape was under way

        /// The menu hand's two quick pinches go back to Move (today's behaviour). A pinch that finishes a tape never
        /// counts toward it, so a nervous second "finish" pinch can't switch tools (UX W0.3). Set false to drop the
        /// gesture entirely (Move is one ring detent away).
        public static bool DoublePinchToMove = true;

        /// Pure decision for the hand gestures (EditMode-tested): given index and middle pinch strengths and the
        /// current states, returns (indexDown, middleDown, doneFired).
        /// systemGesture: Meta's system gesture (palm toward you, pinch → the universal menu) is in progress on this hand:
        /// nothing is a tool press, and anything held is released (UX W0.3 / R05 QW2).
        public static (bool index, bool middle, bool done) Gesture(float indexStrength, float middleStrength, bool indexDown, bool middleDown,
            float press = 0.55f, float release = 0.35f, float done = 0.8f, float doneRelease = 0.4f, bool systemGesture = false)
        {
            if (systemGesture) return (false, false, false);
            bool fired = false;
            if (!middleDown && !indexDown && middleStrength >= done && middleStrength > indexStrength) { middleDown = true; fired = true; }
            else if (middleDown && middleStrength <= doneRelease) middleDown = false;
            if (!indexDown && !middleDown && indexStrength >= press && middleStrength < 0.6f) indexDown = true;
            else if (indexDown && indexStrength <= release) indexDown = false;
            return (indexDown, middleDown, fired);
        }

        void Update()
        {
            if (hub == null) return;
            Poll(ToolHand.Left, leftRays, leftHand, OVRInput.Controller.LTouch);
            Poll(ToolHand.Right, rightRays, rightHand, OVRInput.Controller.RTouch);
        }

        void Poll(ToolHand hand, RayInteractor[] rays, OVRHand ovrHand, OVRInput.Controller controller)
        {
            bool valid = TryGetRay(rays, out var pose, out bool onUi);
            hub.SetLivePointer(hand, pose, valid);

            bool controllerConnected = OVRInput.IsControllerConnected(controller);
            // edit6dof: the grip pose (a controller's grip, a tracked hand's wrist) for 6-DoF grabs.
            var grip = hand == ToolHand.Left ? leftGrip : rightGrip;
            bool gripValid = grip != null && grip.gameObject.activeInHierarchy && (controllerConnected || (ovrHand != null && ovrHand.IsTracked));
            hub.SetLiveGrip(hand, gripValid ? new Pose(grip.position, grip.rotation) : default, gripValid);
            int i = (int)hand;
            var pointer = hub.GetPointer(hand);
            bool down = m_Down[i];
            float analog = 0f;   // W1.2: trigger value / index-pinch strength, for the pinch onset
            if (controllerConnected)
            {
                float trigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, controller);
                analog = trigger;
                if (!down && trigger >= pressThreshold) down = true;
                else if (down && trigger <= releaseThreshold) down = false;
                m_MiddleDown[i] = false;
            }
            else if (ovrHand != null && ovrHand.IsTracked)
            {
                bool system = ovrHand.IsSystemGestureInProgress;
                analog = ovrHand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
                var g = Gesture(analog, ovrHand.GetFingerPinchStrength(OVRHand.HandFinger.Middle),
                    down, m_MiddleDown[i], pressThreshold, releaseThreshold, donePinch, doneRelease, system);
                if (system && (down || m_Down[i])) m_Suppressed[i] = true;   // a system-menu pinch is never a finish / tool press
                down = g.index;
                m_MiddleDown[i] = g.middle;
                if (g.done)
                {
                    AirTools.Core.Log.Info($"Done gesture ({hand})");
                    hub.RaiseButton(hand, ToolButton.Finish);
                }
            }
            else down = false;
            hub.RecordLive(hand, Time.unscaledTime, pose, valid, analog);   // W1.2: pointer history (before any press)

            // A ray resting on a UI button belongs to the button, and while the palm menu is open (or just closed) the
            // hands are busy with it: don't start a tool press.
            bool menuBusy = AirTools.Core.Services.TryGet<PalmMenu>(out var palm) && palm.BlocksTools;
            if (!down) m_Suppressed[i] = false;
            else if (!m_Down[i] && (onUi || menuBusy)) m_Suppressed[i] = true;

            // The menu hand (not the tool hand) never aims or places: its pinch / trigger means "finish" — a fist or a
            // relaxed hand at your side can't drop points or level readings at your feet.
            if (hand != hub.primaryHand && PrimaryAvailable())
            {
                // Two quick pinches with the menu hand: back to Move — unless the pinch finished a tape.
                if (down && !m_SecondaryDown[i]) m_FinishPinch[i] = TapeUnderWay();
                else if (!down) m_FinishPinch[i] = false;
                if (m_DoubleTap[i].Update(down && !m_Suppressed[i] && !m_FinishPinch[i], Time.unscaledTime) && DoublePinchToMove)
                {
                    AirTools.Core.Log.Info($"Double pinch ({hand}): back to the default mode");
                    if (AirTools.Core.Services.TryGet<AirTools.Tools.ToolManager>(out var tools)) tools.Equip(AirTools.Tools.ToolKind.Move);
                    AirTools.UI.UiToast.Show("Back to Move", AirTools.UI.ColorRole.Info);
                }
                if (down && !m_SecondaryDown[i] && !m_Suppressed[i])
                {
                    m_SecondaryDown[i] = true;
                    hub.RaiseButton(hand, ToolButton.Finish);
                }
                else if (!down) m_SecondaryDown[i] = false;
                if (m_Down[i]) { m_Down[i] = false; hub.RaisePressEnd(hand, pointer); }
                return;
            }
            if (!m_Down[i] && down && valid && !m_Suppressed[i])
            {
                m_Down[i] = true;
                hub.RaiseLivePressStart(hand, pointer);   // W1.2: commits with the pointer from before the pinch
            }
            else if (m_Down[i] && !down)
            {
                m_Down[i] = false;
                hub.RaisePressEnd(hand, pointer);
            }
            else if (m_Down[i])
            {
                hub.RaisePressMove(hand, pointer);
            }

            if (controllerConnected)
            {
                if (OVRInput.GetDown(OVRInput.Button.Two, controller)) hub.RaiseButton(hand, ToolButton.Finish);
                if (OVRInput.GetDown(OVRInput.Button.One, controller)) UndoButton();
                // edit6dof: the grip squeeze (no tool uses it): a placed part's context menu (EditView).
                if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, controller)) hub.RaiseButton(hand, ToolButton.Context);
            }
        }

        /// Controller A / X: the global undo (UX W0.10) — the same as the ring's Undo, in any mode (Move included), not
        /// just the tool in hand.
        public static bool UndoButton()
        {
            bool ok = AirTools.Tools.EditHistory.Undo();
            AirTools.Core.Log.Info($"Controller undo: {(ok ? "undo" : "nothing to undo")}");
            if (!ok) AirTools.UI.UiToast.Show("Nothing to undo", AirTools.UI.ColorRole.Info);
            return ok;
        }

        /// A measure tape / shape has points: a menu-hand pinch now means "finish".
        static bool TapeUnderWay() =>
            AirTools.Core.Services.TryGet<AirTools.Tools.MeasureTool>(out var m) && m.Equipped && m.Session.Count > 0;

        /// The tool hand is there (tracked hand or connected controller) — even if its ray is momentarily off, e.g.
        /// while it pokes the palm menu.
        bool PrimaryAvailable()
        {
            bool right = hub.primaryHand == ToolHand.Right;
            var ovr = right ? rightHand : leftHand;
            var controller = right ? OVRInput.Controller.RTouch : OVRInput.Controller.LTouch;
            return OVRInput.IsControllerConnected(controller) || (ovr != null && ovr.IsTracked) || hub.HasPointer(hub.primaryHand);
        }

        static bool TryGetRay(RayInteractor[] rays, out Pose pose, out bool onUi)
        {
            pose = default;
            onUi = false;
            if (rays == null) return false;
            foreach (var ray in rays)
            {
                if (ray == null || !ray.isActiveAndEnabled || ray.State == InteractorState.Disabled) continue;
                var r = ray.Ray;
                if (r.direction.sqrMagnitude < 1e-6f) continue;
                pose = new Pose(r.origin, Quaternion.LookRotation(r.direction));
                onUi = ray.HasInteractable;
                return true;
            }
            return false;
        }
    }
}
