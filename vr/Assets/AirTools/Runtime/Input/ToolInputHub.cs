using System;
using AirTools.Core;
using UnityEngine;

namespace AirTools.Input
{
    /// The scene's single IToolInput. Live sources (OVR controllers/hands) and injected sources (tests, agent
    /// harness) both feed it, so tools see exactly one code path.
    public class ToolInputHub : MonoBehaviour, IToolInput
    {
        public event Action<ToolHand, Pose> PressStart;
        public event Action<ToolHand, Pose> PressMove;
        public event Action<ToolHand, Pose> PressEnd;
        public event Action<ToolHand, ToolButton> ButtonDown;

        readonly Pose[] m_Live = new Pose[2];
        readonly bool[] m_LiveValid = new bool[2];
        readonly Pose?[] m_Override = new Pose?[2];
        readonly bool[] m_Pressed = new bool[2];

        [Tooltip("The tool hand: its ray aims the tools. The other hand is the menu hand; it only becomes active when the\n" +
                 "tool hand has no pointer at all (not tracked).")]
        public ToolHand primaryHand = ToolHand.Right;

        public ToolHand LastActiveHand { get; private set; } = ToolHand.Right;

        /// A press/button from this hand may make it the active (aiming) hand.
        public bool MayBecomeActive(ToolHand hand) => hand == primaryHand || !HasPointer(primaryHand);

        void OnEnable()
        {
            Services.Register<IToolInput>(this);
            Services.Register(this);
        }

        void OnDisable()
        {
            Services.Unregister<IToolInput>(this);
            Services.Unregister(this);
        }

        public bool IsPressed(ToolHand hand) => m_Pressed[(int)hand];

        /// Live pointer from the tracked controller/hand ray (called every frame by the live source).
        public void SetLivePointer(ToolHand hand, Pose pose, bool valid)
        {
            m_Live[(int)hand] = pose;
            m_LiveValid[(int)hand] = valid;
        }

        /// Injected pointer (tests / agent harness). Takes precedence over the live pointer until cleared.
        public void SetPointerOverride(ToolHand hand, Pose? pose) => m_Override[(int)hand] = pose;

        public void ClearOverrides()
        {
            m_Override[0] = m_Override[1] = null;
            m_GripOverride[0] = m_GripOverride[1] = null;   // edit6dof
        }

        // ---------------- edit6dof: the grip pose ----------------
        // The pointer is a ray (LookRotation of its direction): it has no roll. A 6-DoF grab (PlacementEditor, Rotate: Free)
        // turns the part with the hand instead: the controller's grip pose, or the tracked hand's wrist (OVRCameraRig's
        // hand anchors). The live source sets it every frame; tests and the harness override it like the pointer.

        readonly Pose[] m_LiveGrip = new Pose[2];
        readonly bool[] m_LiveGripValid = new bool[2];
        readonly Pose?[] m_GripOverride = new Pose?[2];

        public void SetLiveGrip(ToolHand hand, Pose pose, bool valid)
        {
            m_LiveGrip[(int)hand] = pose;
            m_LiveGripValid[(int)hand] = valid;
        }

        /// Injected grip (tests / agent harness): takes precedence over the live one until cleared.
        public void SetGripOverride(ToolHand hand, Pose? pose) => m_GripOverride[(int)hand] = pose;

        /// The hand's grip pose (world), if there is one.
        public bool TryGetGrip(ToolHand hand, out Pose pose)
        {
            int i = (int)hand;
            if (m_GripOverride[i].HasValue) { pose = m_GripOverride[i].Value; return true; }
            pose = m_LiveGrip[i];
            return m_LiveGripValid[i];
        }

        /// For tools, which hold an IToolInput: the hand's grip pose, if the hub has one.
        public static bool TryGetGrip(IToolInput input, ToolHand hand, out Pose pose)
        {
            pose = default;
            return input is ToolInputHub hub && hub.TryGetGrip(hand, out pose);
        }
        // end edit6dof

        public Pose GetPointer(ToolHand hand) => m_Override[(int)hand] ?? m_Live[(int)hand];
        public bool HasPointer(ToolHand hand) => m_Override[(int)hand].HasValue || m_LiveValid[(int)hand];

        public void RaisePressStart(ToolHand hand, Pose pose) => StartPress(hand, pose, null);

        void StartPress(ToolHand hand, Pose pose, Pose? commit)
        {
            if (m_Pressed[(int)hand]) RaisePressEnd(hand, pose);
            m_Pressed[(int)hand] = true;
            m_Commit[(int)hand] = commit;   // W1.2
            if (MayBecomeActive(hand)) LastActiveHand = hand;
            PressStart?.Invoke(hand, pose);
        }

        public void RaisePressMove(ToolHand hand, Pose pose)
        {
            if (!m_Pressed[(int)hand]) return;
            PressMove?.Invoke(hand, pose);
        }

        public void RaisePressEnd(ToolHand hand, Pose pose)
        {
            if (!m_Pressed[(int)hand]) return;
            m_Pressed[(int)hand] = false;
            PressEnd?.Invoke(hand, pose);
            m_Commit[(int)hand] = null;   // W1.2
        }

        // ---------------- W1.2: commit what you saw ----------------

        readonly PointerHistory[] m_History = { new PointerHistory(), new PointerHistory() };
        readonly Pose?[] m_Commit = new Pose?[2];

        /// The live source, every frame, before it raises a press: the live ray, whether it's valid, and the analog
        /// press value (trigger, or index-pinch strength; 0–1) at `time` (unscaled seconds).
        public void RecordLive(ToolHand hand, float time, Pose pose, bool valid, float strength) =>
            m_History[(int)hand].Push(time, pose, valid, strength);

        public PointerHistory History(ToolHand hand) => m_History[(int)hand];

        /// A trigger pull / pinch from the live source. Like RaisePressStart, and the press commits with the pointer
        /// from ~90 ms before the pinch onset (PointerHistory) — unless PointerHistory.Rewind is off or an override
        /// (harness Aim) is aiming this hand, when it commits with `pose` as before.
        public void RaiseLivePressStart(ToolHand hand, Pose pose)
        {
            int i = (int)hand;
            StartPress(hand, pose, PointerHistory.TryRewind(m_History[i], m_Override[i].HasValue, out var commit) ? commit : (Pose?)null);
        }

        /// The pointer the current press commits with (rewound, W1.2). False for presses injected by tests, the
        /// harness or the agent, with an override aiming, or with the rewind off: then the press pose, as before.
        /// Valid from PressStart until the PressEnd handlers have run.
        public bool TryGetCommitPointer(ToolHand hand, out Pose pose)
        {
            var c = m_Commit[(int)hand];
            pose = c ?? default;
            return c.HasValue;
        }

        /// For tools, which hold an IToolInput: the rewound pointer of this press, if the hub has one.
        public static bool TryGetCommitPointer(IToolInput input, ToolHand hand, out Pose pose)
        {
            pose = default;
            return input is ToolInputHub hub && hub.TryGetCommitPointer(hand, out pose);
        }

        public void RaiseButton(ToolHand hand, ToolButton button)
        {
            if (MayBecomeActive(hand)) LastActiveHand = hand;
            ButtonDown?.Invoke(hand, button);
        }

        /// Convenience for tests and the harness: a pointer ray from origin toward a target point.
        public static Pose RayPose(Vector3 origin, Vector3 target)
        {
            var dir = target - origin;
            return new Pose(origin, dir.sqrMagnitude > 1e-12f ? Quaternion.LookRotation(dir.normalized) : Quaternion.identity);
        }
    }
}
