using System;
using AirTools.Core;
using AirTools.UI;
using Oculus.Interaction.Input;
using UnityEngine;

namespace AirTools.Input
{
    /// Hand menu: turn the left palm up (palmUp, the tool ring) — or toward your face in the older layout — and the
    /// tools appear just above it; work them with the other hand; close the hand or turn the palm away and it goes. With controllers, the left menu button toggles
    /// it above the left controller. Content faces the eyes and follows smoothly, so it doesn't swim with hand jitter.
    /// While the other hand really works it (on the ring's glass, or a button hovered / pressed) it holds open and
    /// still; turning the palm down or away closes it within 0.2 s even then (PalmMenuLogic, EditMode-tested).
    public class PalmMenu : MonoBehaviour
    {
        [Tooltip("ISDK Hand for the left hand (ComprehensiveInteractorsLeft).")]
        public Hand hand;
        public Transform head;
        [Tooltip("Left controller anchor (used when a controller is in the hand).")]
        public Transform controllerAnchor;
        [Tooltip("The poking hand (ISDK Hand, right): while its index tip is near the menu, the menu holds still and open.")]
        public Hand otherHand;
        [Tooltip("The poking controller (right hand anchor), for the same hold with a controller.")]
        public Transform otherAnchor;
        [Tooltip("Controller-opened menu: the right controller counts as on the menu within this distance of it (m) — a trigger press there is the ring's, not a tool's.")]
        public float holdRadius = 0.12f;
        [Tooltip("Hand-opened menu: the other hand's fingertip holds the menu open only within this distance of the ring's / panel's surface (m)…")]
        public float onMenuDepth = 0.035f;
        [Tooltip("…and at most this far off the ring's glass band / outside the panel (m).")]
        public float onMenuMargin = 0.025f;
        [Tooltip("Open with the palm facing up (the tool ring) instead of toward the face.")]
        public bool palmUp;
        [Tooltip("The tool ring (its geometry decides 'the other hand is on the menu'; it holds the menu while spinning).")]
        public ToolRing ring;
        [Tooltip("Ring layout: the panel is only the spec inspector's card above the ring, shown with it; no sliding.")]
        public bool ringLayout;
        /// Set by the ring while it's being worked or still spinning: stay open and still.
        public bool KeepOpen { get; set; }
        public GameObject content;
        [Tooltip("How far the menu floats off the palm (m).")]
        public float lift = 0.06f;
        [Tooltip("Presses are ignored until the menu has been open this long (a menu popping up under a finger mustn't fire).")]
        public float armDelay = 0.35f;
        public float followSpeed = 18f;
        [Header("Panel (one glass surface; widens left for the spec inspector)")]
        public GlassSurface panel;
        public Vector2 toolsSize = new Vector2(0.22f, 0.17f);
        public Vector3 toolsCentre;
        public float inspectorWidth = 0.25f;
        [Tooltip("Extra height (added below) while the inspector is shown.")]
        public float inspectorExtraHeight = 0.045f;
        public bool InspectorShown { get; private set; }

        /// Content offset target: with the inspector shown the whole panel slides right so the inspector sits centred
        /// over the palm (easy to read and reach) and the tools move toward the poking hand.
        Vector3 m_ContentOffset;

        public Vector3 ContentOffsetTarget => InspectorShown && !ringLayout ? new Vector3(toolsSize.x * 0.5f + inspectorWidth * 0.5f, 0f, 0f) : Vector3.zero;

        /// Widen the panel to the left to host the spec inspector (one glass surface, not two). Ring layout: show the
        /// inspector's own card above the ring.
        public void SetInspector(bool on)
        {
            InspectorShown = on;
            if (panel == null) return;
            if (ringLayout) { panel.gameObject.SetActive(on); return; }
            var size = on ? new Vector2(toolsSize.x + inspectorWidth, toolsSize.y + inspectorExtraHeight) : toolsSize;
            panel.SetSize(size);
            var c = toolsCentre + (on ? new Vector3(-inspectorWidth * 0.5f, -inspectorExtraHeight * 0.5f, 0f) : Vector3.zero);
            panel.transform.localPosition = new Vector3(c.x, c.y, panel.transform.localPosition.z);
        }

        public bool IsOpen { get; private set; }
        /// "hand" | "controller" | "forced" | "" — what opened it.
        public string OpenedBy { get; private set; } = "";
        public bool AcceptingPresses => IsOpen && Time.unscaledTime - m_OpenedAt >= armDelay;
        /// When it last closed (unscaled time); tools ignore presses for a moment after, see BlocksTools.
        public float ClosedAt { get; private set; } = -999f;
        /// Tool presses are ignored while the menu is open and for a moment after it closes, so reaching for the
        /// menu (or dropping the hand after it) never places a stray point. Opened with the controller's menu button,
        /// only a trigger press that starts on the ring is the ring's (UX W0.10): the right trigger keeps working
        /// for tools everywhere else.
        public bool BlocksTools => Blocks(IsOpen, OpenedBy, Time.unscaledTime - ClosedAt, OpenedBy == "controller" && OtherHandNear());

        /// Pure truth table for BlocksTools (EditMode-tested).
        public static bool Blocks(bool open, string openedBy, float sinceClosed, bool otherOnMenu)
        {
            if (open && openedBy == "controller") return otherOnMenu;
            return open || sinceClosed < 0.4f;
        }

        /// Close a menu opened with the controller's menu button (after a pick on the ring: UX W0.10).
        public void CloseFromController()
        {
            if (!m_ControllerOpen) return;
            m_ControllerOpen = false;
            Log.Info("Palm menu: closed after a pick (controller)");
        }
        [Tooltip("The palm must be within this angle of where you're looking (and not below your chest) to open.")]
        public float openViewAngle = 45f;
        public float stayViewAngle = 60f;
        public int ExtendedFingers { get; private set; }
        public float ViewAngle { get; private set; }
        public float FacingDot { get; private set; }
        public event Action<bool> Changed;

        readonly PalmMenuLogic m_Logic = new PalmMenuLogic();
        /// The hand decision (gesture + hold), for the harness.
        public PalmMenuLogic Logic => m_Logic;
        /// True while the menu is held open and still because the other hand is on it (or the menu hand dropped out).
        public bool Holding { get; private set; }
        /// Why it last closed ("palm down, while held", "hand lost", "released · hand closed", "controller"…).
        public string LastCloseReason { get; private set; } = "";
        /// The menu's own buttons (the inspector card's), looked up when it opens: a hovered / pressed one holds it.
        GlassButton[] m_Buttons = Array.Empty<GlassButton>();
        string m_CloseReason = "";
        bool m_ControllerOpen;
        bool? m_Forced;
        float m_OpenedAt;
        float m_Scale;
        Vector3 m_Pos;
        Quaternion m_Rot = Quaternion.identity;

        static readonly HandJointId[] s_Knuckles = { HandJointId.HandIndex1, HandJointId.HandMiddle1, HandJointId.HandRing1, HandJointId.HandPinky1 };
        static readonly HandJointId[] s_Tips = { HandJointId.HandIndexTip, HandJointId.HandMiddleTip, HandJointId.HandRingTip, HandJointId.HandPinkyTip };

        static readonly Vector3[] s_KnucklePos = new Vector3[4];

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);

        void Start() => Apply(false, "", immediate: true);

        /// Harness / tests: force open (true), closed (false) or back to gestures (null).
        public void Force(bool? open) => m_Forced = open;

        // switchclean: a world-model switch closes the ring (and the inspector card beside it). A hand-opened menu stays
        // shut until that palm has dropped (the gesture closed) and comes up again; a controller-opened one until the menu
        // button again; one the harness forced open goes back to the gestures.
        bool m_Dismissed;

        /// The menu is shut by a switch and waits for the palm to drop before it can open by hand again.
        public bool Dismissed => m_Dismissed;

        /// Close it now (see above). True when it was open.
        public bool Dismiss()
        {
            bool was = IsOpen;
            m_ControllerOpen = false;
            if (m_Forced == true) m_Forced = null;
            if (m_Logic.IsOpen || (IsOpen && OpenedBy == "hand")) m_Dismissed = true;
            if (IsOpen)
            {
                m_CloseReason = "world switch";
                Apply(false, "", immediate: false);
            }
            return was;
        }

        // end switchclean

        void Update()
        {
            bool controllerMode = (OVRInput.GetConnectedControllers() & OVRInput.Controller.LTouch) != 0;
            if (controllerMode && OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch)) m_ControllerOpen = !m_ControllerOpen;
            if (!controllerMode) m_ControllerOpen = false;

            Vector3 target = default;
            bool havePose = false;
            PalmFrame palm = default;
            // The palm frame (FacingDot, ViewAngle) is computed whenever the hand is tracked, held or not: a held menu
            // still closes when its palm turns down or away.
            bool reliable = !controllerMode && hand != null && hand.IsConnected && hand.IsHighConfidence && TryPalm(out palm);
            bool byHand = IsOpen && OpenedBy == "hand";
            var input = new PalmInput
            {
                Tracked = reliable,
                Fingers = ExtendedFingers,
                FacingDot = FacingDot,
                DownDot = reliable ? Vector3.Dot(palm.Normal, Vector3.down) : 0f,
                // Only a deliberately raised hand counts: in front of the eyes, not hanging at your side.
                InView = reliable && head != null && ViewAngle <= (IsOpen ? stayViewAngle : openViewAngle) && palm.Centre.y > head.position.y - 0.55f,
                OnRing = byHand && OtherHandOnMenu(),
                Pressing = byHand && (KeepOpen || MenuButtonActive()),
            };
            bool wasHolding = m_Logic.Holding;
            bool handOpen = m_Logic.Update(input, Time.unscaledTime);
            handOpen = AirTools.SwitchClose.PalmAfterDismiss(handOpen, ref m_Dismissed);   // switchclean
            if (m_Logic.Holding != wasHolding)
                Log.Info(m_Logic.Holding ? $"Palm menu held ({PalmMenuLogic.Describe(m_Logic.Why)})" : "Palm menu released");
            Holding = m_Logic.Holding;
            if (byHand && !handOpen) m_CloseReason = PalmMenuLogic.Describe(m_Logic.LastClose, m_Logic.LastCloseAfterHold, m_Logic.LastCloseWhileHeld);
            if (!Holding && reliable)
            {
                target = palm.Centre + palm.Normal * lift;
                havePose = true;
            }
            else if (!Holding && !reliable && controllerAnchor != null) { target = controllerAnchor.position + Vector3.up * 0.12f; havePose = true; }

            bool open;
            string by;
            if (m_Forced.HasValue) { open = m_Forced.Value; by = "forced"; }
            else if (handOpen) { open = true; by = "hand"; }
            else if (m_ControllerOpen) { open = true; by = "controller"; }
            else { open = false; by = ""; }
            if (!open && IsOpen && OpenedBy != "hand") m_CloseReason = OpenedBy;

            if (m_Forced.HasValue && head != null && !havePose)
            {
                var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
                target = head.position + fwd * 0.4f - Vector3.up * 0.2f;
                havePose = true;
            }
            if (havePose && !(m_Logic.Frozen && IsOpen && open)) Follow(target, !IsOpen && open);
            Apply(open, by, immediate: false);
        }

        /// The other hand's index tip (or the right controller) really on the menu: within a few cm of the ring's glass
        /// band, or of the panel when it shows (the inspector card). Not merely near the menu hand.
        bool OtherHandOnMenu()
        {
            if (!IsOpen || !TryOtherTip(out var tip)) return false;
            if (ring != null && ring.isActiveAndEnabled
                && PalmMath.OnRing(ring.transform.InverseTransformPoint(tip), ring.radius, ring.halfWidth, onMenuDepth, onMenuMargin)) return true;
            if (panel != null && panel.gameObject.activeInHierarchy)
            {
                var t = panel.transform;
                return PalmMath.OnPanel(t.InverseTransformPoint(tip), panel.size, t.lossyScale, onMenuDepth, onMenuMargin);
            }
            return false;
        }

        /// One of the menu's own buttons hovered or pressed by a fingertip (poke), or by anything when it has no poke.
        bool MenuButtonActive()
        {
            for (int i = 0; i < m_Buttons.Length; i++)
            {
                var b = m_Buttons[i];
                if (b == null || !b.isActiveAndEnabled) continue;
                var st = b.poke != null ? b.poke.State : b.State;
                if (st != Oculus.Interaction.InteractableState.Normal && st != Oculus.Interaction.InteractableState.Disabled) return true;
            }
            return false;
        }

        bool TryOtherTip(out Vector3 tip)
        {
            tip = default;
            if (otherHand != null && otherHand.IsConnected && otherHand.GetJointPose(HandJointId.HandIndexTip, out var tp)) { tip = tp.position; return true; }
            if (otherAnchor != null && (OVRInput.GetConnectedControllers() & OVRInput.Controller.RTouch) != 0) { tip = otherAnchor.position; return true; }
            return false;
        }

        /// The other hand's index tip (or controller) within holdRadius of the menu panel (generous: BlocksTools for a
        /// controller-opened menu, UX W0.10). The hand-opened hold uses the tight OtherHandOnMenu.
        bool OtherHandNear()
        {
            if (!IsOpen || (panel == null && ring == null)) return false;
            if (!TryOtherTip(out var tip)) return false;
            if (ring != null)
            {
                var rl = ring.transform.InverseTransformPoint(tip);
                return Mathf.Abs(rl.z) <= holdRadius && new Vector2(rl.x, rl.y).magnitude <= ring.radius + ring.halfWidth + holdRadius;
            }
            var t = panel.transform;
            var local = t.InverseTransformPoint(tip);
            var scale = t.lossyScale;
            var half = panel.size * 0.5f;
            float dx = Mathf.Max(0f, Mathf.Abs(local.x) - half.x) * scale.x;
            float dy = Mathf.Max(0f, Mathf.Abs(local.y) - half.y) * scale.y;
            float dz = Mathf.Abs(local.z) * scale.z;
            return dx * dx + dy * dy + dz * dz <= holdRadius * holdRadius;
        }

        bool TryPalm(out PalmFrame palm)
        {
            palm = default;
            if (!hand.GetJointPose(HandJointId.HandWristRoot, out var wrist)) return false;
            var k = s_KnucklePos;
            int extended = 0;
            for (int i = 0; i < 4; i++)
            {
                if (!hand.GetJointPose(s_Knuckles[i], out var kp) || !hand.GetJointPose(s_Tips[i], out var tp)) return false;
                k[i] = kp.position;
                if (PalmMath.IsExtended(wrist.position, kp.position, tp.position)) extended++;
            }
            palm = PalmMath.Palm(wrist.position, k[0], k[1], k[3], leftHand: hand.Handedness == Handedness.Left);
            ExtendedFingers = extended;
            FacingDot = palmUp ? Vector3.Dot(palm.Normal, Vector3.up) : head != null ? PalmMath.FacingDot(palm, head.position) : 0f;
            ViewAngle = head != null ? Vector3.Angle(head.forward, palm.Centre - head.position) : 180f;
            return true;
        }

        void Follow(Vector3 target, bool snap)
        {
            float k = snap ? 1f : 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
            m_Pos = Vector3.Lerp(m_Pos, target, k);
            var toMenu = head != null ? m_Pos - head.position : Vector3.forward;
            if (toMenu.sqrMagnitude > 1e-6f) m_Rot = Quaternion.Slerp(m_Rot, Quaternion.LookRotation(toMenu.normalized, Vector3.up), k);
            transform.SetPositionAndRotation(m_Pos, m_Rot);
        }

        void Apply(bool open, string by, bool immediate)
        {
            if (open != IsOpen)
            {
                IsOpen = open;
                OpenedBy = open ? by : "";
                if (open) m_OpenedAt = Time.unscaledTime;
                else ClosedAt = Time.unscaledTime;
                if (open) Log.Info($"Palm menu open ({by})");
                else
                {
                    LastCloseReason = string.IsNullOrEmpty(m_CloseReason) ? "closed" : m_CloseReason;
                    Log.Info($"Palm menu closed ({LastCloseReason})");
                }
                m_CloseReason = "";
                if (open && content != null) { content.SetActive(true); m_Buttons = content.GetComponentsInChildren<GlassButton>(true); }
                Changed?.Invoke(open);
            }
            // Quick pop in/out.
            float target = IsOpen ? 1f : 0f;
            float dur = UiSettings.Duration(UiTheme.Current.motion.standard);
            m_Scale = immediate || dur <= 0f ? target : Mathf.MoveTowards(m_Scale, target, Time.unscaledDeltaTime / dur);
            if (content != null)
            {
                float k = immediate || dur <= 0f || m_Scale < 0.05f ? 1f : 1f - Mathf.Exp(-Time.unscaledDeltaTime * 4.6f / dur);
                m_ContentOffset = Vector3.Lerp(m_ContentOffset, ContentOffsetTarget, k);
                content.transform.localPosition = m_ContentOffset;
                content.transform.localScale = Vector3.one * Mathf.Max(m_Scale, 0.001f);
                if (m_Scale <= 0f && content.activeSelf) content.SetActive(false);
            }
        }
    }
}
