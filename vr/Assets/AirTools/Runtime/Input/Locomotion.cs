using AirTools.Core;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Input
{
    /// Getting around the world without walking (in the world only; passthrough stays room-scale).
    ///   Controllers (always): left stick flies along where you look (horizontal), right stick up/down rises and
    ///   sinks, right stick left/right snap-turns 30°.
    ///   Hands (Move tool): pinch and drag to pull yourself through the world — pull toward you to go forward,
    ///   pull down to rise ("grab the air"). A quick pinch on the floor teleports you there (UX W0.2: floor-like only —
    ///   facing up and within FloorTolerance of the floor — so a pinch at a countertop or a wall never puts you on it;
    ///   those say "Aim at the floor to walk"). A disc on the floor shows where you'd land.
    ///   Home puts you back at the spawn.
    /// Moves the camera rig only; the scene, tools and readings are unaffected.
    public class Locomotion : MonoBehaviour
    {
        public Transform rig;
        public Transform head;
        public float stickSpeed = 2.0f;
        public float verticalSpeed = 1.5f;
        public float snapTurnDegrees = 30f;
        [Tooltip("Hand drag multiplier (1 = the world moves exactly with your hand).")]
        public float grabGain = 2.0f;
        [Tooltip("A pinch shorter than this that barely moves is a teleport tap.")]
        public float tapSeconds = 0.35f;
        public float tapMoveMetres = 0.03f;
        public float maxTeleportDistance = 40f;
        [Tooltip("Teleport targets must face up this much (normal · up).")]
        public float floorNormalMin = 0.9f;
        [Tooltip("…and sit at most this high above the floor (scene metres).")]
        public float floorTolerance = 0.3f;
        [Header("Floor disc (where a pinch would land)")]
        public Material discMaterial;
        public float discRadius = 0.16f;

        public bool Equipped { get; private set; }
        public string LastAction { get; private set; } = "";
        public Pose Home { get; private set; }

        IToolInput m_Input;
        bool m_Grabbing, m_Moved;
        ToolHand m_GrabHand;
        Vector3 m_GrabStartLocal, m_RigStart;
        float m_GrabTime;
        bool m_TurnLatched;
        bool m_HomeSet;

        void OnEnable()
        {
            Services.Register(this);
            if (m_Input == null && Services.TryGet<IToolInput>(out var input)) SetInput(input);
        }

        void OnDisable()
        {
            SetInput(null);
            Services.Unregister(this);
        }

        void Start()
        {
            if (m_Input == null && Services.TryGet<IToolInput>(out var input)) SetInput(input);
            if (rig != null && !m_HomeSet) { Home = new Pose(rig.position, rig.rotation); m_HomeSet = true; }
        }

        public void SetInput(IToolInput input)
        {
            if (m_Input != null) { m_Input.PressStart -= OnPressStart; m_Input.PressMove -= OnPressMove; m_Input.PressEnd -= OnPressEnd; }
            m_Input = input;
            if (m_Input != null) { m_Input.PressStart += OnPressStart; m_Input.PressMove += OnPressMove; m_Input.PressEnd += OnPressEnd; }
        }

        public void Equip(bool on)
        {
            if (Equipped == on) return;
            Equipped = on;
            m_Grabbing = false;
            Log.Info($"Move tool {(on ? "equipped" : "put away")}");
        }

        void Update()
        {
            if (rig == null || AppState.Mode != AppMode.World) { ShowDisc(null); return; }
            Sticks(Time.unscaledDeltaTime);
            UpdateDisc();
        }

        // ---------------- floor disc ----------------

        LineRenderer m_Disc;
        MaterialPropertyBlock m_DiscBlock;
        static readonly Vector3[] s_DiscPoints = new Vector3[28];
        /// Where a quick pinch would land right now (null: not a floor spot / not in Move).
        public Vector3? DiscPoint { get; private set; }

        void UpdateDisc()
        {
            Vector3? target = null;
            bool menuOpen = Services.TryGet<PalmMenu>(out var palm) && palm.IsOpen;
            if (Equipped && !m_Grabbing && !menuOpen && m_Input != null)
            {
                var hand = m_Input.LastActiveHand;
                if (m_Input.HasPointer(hand) && TryFloor(m_Input.GetPointer(hand), out var hit, out _)) target = hit.point;
            }
            ShowDisc(target);
        }

        void ShowDisc(Vector3? at)
        {
            DiscPoint = at;
            if (!at.HasValue) { if (m_Disc != null && m_Disc.enabled) m_Disc.enabled = false; return; }
            if (m_Disc == null)
            {
                var go = new GameObject("FloorDisc");
                go.transform.SetParent(transform, false);
                m_Disc = go.AddComponent<LineRenderer>();
                m_Disc.useWorldSpace = true;
                m_Disc.loop = true;
                m_Disc.positionCount = s_DiscPoints.Length;
                m_Disc.numCornerVertices = 2;
                m_Disc.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                m_Disc.receiveShadows = false;
                if (discMaterial != null) m_Disc.sharedMaterial = discMaterial;
                m_DiscBlock = new MaterialPropertyBlock();
                var c = AirTools.UI.UiTheme.Current.colors.ink;   // D3: "you'll stand here" is ink
                m_Disc.startColor = m_Disc.endColor = c;
                m_Disc.GetPropertyBlock(m_DiscBlock);
                m_DiscBlock.SetColor(s_BaseColor, c);   // SetColor: sRGB → linear (the project is Linear)
                m_Disc.SetPropertyBlock(m_DiscBlock);
            }
            var p = at.Value + Vector3.up * 0.01f;
            float dist = head != null ? Vector3.Distance(head.position, p) : 2f;
            for (int i = 0; i < s_DiscPoints.Length; i++)
            {
                float a = i * Mathf.PI * 2f / s_DiscPoints.Length;
                s_DiscPoints[i] = p + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * discRadius;
            }
            m_Disc.SetPositions(s_DiscPoints);
            m_Disc.widthMultiplier = Mathf.Max(0.006f, 0.004f * dist);
            if (!m_Disc.enabled) m_Disc.enabled = true;
        }

        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");

        void Sticks(float dt)
        {
            var left = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            var right = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            if (left.sqrMagnitude > 0.02f)
            {
                var fwd = Flat(head != null ? head.forward : rig.forward);
                var side = Vector3.Cross(Vector3.up, fwd);
                rig.position += (fwd * left.y + side * left.x) * (stickSpeed * dt);
            }
            if (Mathf.Abs(right.y) > 0.2f) rig.position += Vector3.up * (right.y * verticalSpeed * dt);
            if (!m_TurnLatched && Mathf.Abs(right.x) > 0.7f) { SnapTurn(Mathf.Sign(right.x) * snapTurnDegrees); m_TurnLatched = true; }
            else if (Mathf.Abs(right.x) < 0.3f) m_TurnLatched = false;
        }

        /// Turn the rig about the head (so you turn in place).
        public void SnapTurn(float degrees)
        {
            var pivot = head != null ? head.position : rig.position;
            rig.RotateAround(pivot, Vector3.up, degrees);
            LastAction = $"turned {degrees:0}°";
            Log.Info($"Locomotion: {LastAction}");
        }

        void OnPressStart(ToolHand hand, Pose pointer)
        {
            if (!Equipped || rig == null || AppState.Mode != AppMode.World) return;
            m_Grabbing = true;
            m_Moved = false;
            m_GrabHand = hand;
            m_GrabStartLocal = rig.InverseTransformPoint(pointer.position);
            m_RigStart = rig.position;
            m_GrabTime = Time.unscaledTime;
        }

        void OnPressMove(ToolHand hand, Pose pointer)
        {
            if (!m_Grabbing || hand != m_GrabHand) return;
            // The hand's tracking-space motion (what you physically moved), applied inversely to the rig.
            var deltaLocal = rig.InverseTransformPoint(pointer.position) - m_GrabStartLocal;
            if (!m_Moved && deltaLocal.magnitude < tapMoveMetres) return;
            m_Moved = true;
            rig.position = m_RigStart - rig.TransformVector(deltaLocal) * grabGain;
        }

        void OnPressEnd(ToolHand hand, Pose pointer)
        {
            if (!m_Grabbing || hand != m_GrabHand) return;
            m_Grabbing = false;
            if (m_Moved) { LastAction = $"moved to {rig.position:F2}"; Log.Info($"Locomotion: {LastAction}"); return; }
            if (Time.unscaledTime - m_GrabTime <= tapSeconds) TeleportAlong(pointer);
        }

        /// A floor-like spot: facing up (normal · up ≥ floorNormalMin) and at most floorTolerance above the floor.
        public static bool IsFloor(Vector3 normal, float heightAboveFloor, float normalMin = 0.9f, float tolerance = 0.3f) =>
            normal.y >= normalMin && heightAboveFloor <= tolerance;

        /// The floor under the ray, if the ray hits a floor-like spot. `reason` says why not ("nothing", "not floor").
        public bool TryFloor(Pose pointer, out SurfaceHit hit, out string reason)
        {
            reason = null;
            if (!SnapService.TryRaySnap(new Ray(pointer.position, pointer.forward), out hit, maxTeleportDistance, features: false))
            { reason = "nothing"; return false; }
            if (!IsFloor(hit.normal, HeightAboveFloor(hit.point), floorNormalMin, floorTolerance)) { reason = "not floor"; return false; }
            return true;
        }

        /// Scene metres above the scene's floor (SurfaceNames.FloorY); without a scene, above the rig (your floor).
        float HeightAboveFloor(Vector3 world)
        {
            if (Services.TryGet<SceneRoot>(out var root) && root.Content != null)
            {
                float floor = AirTools.Parts.SurfaceNames.FloorY(root);
                if (!float.IsNaN(floor)) return root.transform.InverseTransformPoint(world).y - floor;
            }
            return world.y - (rig != null ? rig.position.y : 0f);
        }

        /// Stand on the floor the ray hits (head above the point, feet on it). False — with words — if it isn't floor.
        public bool TeleportAlong(Pose pointer)
        {
            if (!TryFloor(pointer, out var hit, out var why))
            {
                if (why == "nothing")
                {
                    LastAction = "teleport: nothing there";
                    AirTools.UI.InputHints.Say("move.nothing", "Nothing to stand on there");
                }
                else
                {
                    LastAction = "teleport: aim at the floor to walk";
                    AirTools.UI.InputHints.Say("move.notfloor", "Aim at the floor to walk");
                }
                Log.Info($"Locomotion: {LastAction}");
                return false;
            }
            AirTools.UI.InputHints.Succeeded();
            TeleportTo(hit.point);
            AirTools.UI.FeedbackEvents.Teleport(hit.point);
            return true;
        }

        public void TeleportTo(Vector3 floorPoint)
        {
            var headXZ = head != null ? head.position : rig.position;
            rig.position += new Vector3(floorPoint.x - headXZ.x, floorPoint.y - rig.position.y, floorPoint.z - headXZ.z);
            LastAction = $"teleported to {floorPoint:F2}";
            Log.Info($"Locomotion: {LastAction}");
        }

        public void GoHome()
        {
            if (rig == null) return;
            if (!m_HomeSet) { Home = new Pose(rig.position, rig.rotation); m_HomeSet = true; }
            rig.SetPositionAndRotation(Home.position, Home.rotation);
            LastAction = "home";
            Log.Info("Locomotion: home");
        }

        /// Tests: set where Home is.
        public void SetHome(Pose home) { Home = home; m_HomeSet = true; }

        static Vector3 Flat(Vector3 v)
        {
            v = Vector3.ProjectOnPlane(v, Vector3.up);
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }
    }
}
