using System;
using AirTools.Core;
using AirTools.Tools;
using AirTools.UI;
using Oculus.Interaction.Input;
using TMPro;
using UnityEngine;

namespace AirTools.Input
{
    /// One entry on the tool ring.
    [Serializable]
    public class RingItem
    {
        public string label;
        /// Phosphor codepoint (Icons).
        public string icon;
        /// A mode is equipped when the wheel settles on it; an action fires on a pinch on the lens.
        public bool isMode;
        public ToolKind mode;
        public ToolboxAction action;
        /// Needs a second pinch (Exit world).
        public bool confirm;
        public Transform root;
        public TextMeshPro iconText, labelText;
        [NonSerialized] public bool filled;
    }

    /// The tool ring around the upturned left palm — Apple Photos' edit dial as a prize wheel, in Liquid Glass.
    /// A horseshoe of glass (open at the wrist) carries the tools; the one at the top, under the clear selection lens,
    /// is the current one. Pinch anywhere on the ring with the other hand and move to turn it (the item under your
    /// fingers follows them); let go and it coasts with your flick — a hard flick passes a few items, a soft one just
    /// settles — ticking (sound, lens pulse, controller buzz) at every item, then springs onto the nearest one. A quick
    /// pinch (or a poke) on a side item spins it to the top. Modes (Move, Measure, Level) are previewed when the wheel
    /// settles on them and equipped by a pinch on the lens (D6); actions (Notebook, Model view, Settings) fire when you
    /// pinch the lens. Ring v2 (declutter DC3, M6): six fixed items 60° apart — Move · Measure · Level · Notebook ·
    /// Model view · Settings; Home, Ladder, Exit world and the layer toggles live in Settings. Undo / Redo sit at the ends of
    /// the arc and go dim when there's nothing to undo / redo. While you work the ring it holds still and open
    /// (PalmMenu.KeepOpen).
    public class ToolRing : MonoBehaviour
    {
        [Header("Wiring")]
        public PalmMenu menu;
        [Tooltip("The spinning hand: ISDK Hand (joint poses) and OVRHand (pinch strength).")]
        public Hand pinchHand;
        public OVRHand pinchOvrHand;
        [Tooltip("The spinning controller (right hand anchor): trigger = pinch.")]
        public Transform pinchController;
        public Transform head;
        public MeshRenderer glass;
        public MeshRenderer undoGlass, redoGlass;
        public TextMeshPro undoIcon, redoIcon, hint;
        public RingItem[] items = new RingItem[0];

        [Header("Geometry (m)")]
        public float radius = 0.105f;
        public float halfWidth = 0.017f;
        [Tooltip("Half aperture of the glass arc from the top (degrees).")]
        public float arcHalfAngle = 118f;
        public float lensRadius = 0.024f;
        [Tooltip("Undo / Redo sit this far round from the top (degrees), just past the ends of the arc.")]
        public float buttonAngle = 150f;   // ring v2: 5.4 cm from the ±120° items
        public Vector2 buttonSize = new Vector2(0.036f, 0.03f);
        [Tooltip("Items fade out toward the ends of the arc (degrees from the top).")]
        public float visibleHalfAngle = 128f;   // ring v2: the ±120° items stay on, the 180° one is hidden
        [Tooltip("Width of the fade before visibleHalfAngle (degrees): ±120° items stay 74% opaque with 12°.")]
        public float fadeBandDeg = 12f;
        [Tooltip("The item under the lens is magnified by this much (the lens).")]
        public float lensScale = 1.3f;

        [Header("Feel")]
        public float pinchOn = 0.75f, pinchOff = 0.45f;
        [Tooltip("A pinch that moves less than this and ends within tapSeconds is a tap (m).")]
        public float tapMove = 0.012f;
        public float tapSeconds = 0.35f;
        [Tooltip("Pinches start on the ring within this distance of its plane (m).")]
        public float grabDepth = 0.07f;
        public float tapRadius = 0.032f;
        public float tickInterval = 0.028f;
        [Tooltip("A mode is equipped once the wheel has rested on it this long (s).")]
        public float settleDelay = 0.1f;
        public float tickVolume = 0.28f;

        /// UX decision D6 (docs/ux/README.md): true = settling on a mode only previews it and a pinch on the lens equips
        /// it (one commit model for modes and actions). Today (false): modes equip when the wheel settles.
        public static bool CommitOnPinch = true;   // D6 decided (SPEC §9): settling only previews; a pinch commits

        public DialPhysics Dial { get; private set; }
        public int Selected => Dial != null ? Dial.Selected : 0;
        /// Being dragged or still moving: the palm menu holds still and open.
        public bool Interacting => m_Gesture.Active || (Dial != null && !Dial.Settled);
        public int Ticks { get; private set; }
        public string LastAction { get; private set; } = "";
        public bool UndoAvailable { get; private set; }
        public bool RedoAvailable { get; private set; }

        MaterialPropertyBlock m_Mpb, m_ButtonMpb;
        AudioSource m_Audio;
        static AudioClip s_Tick;
        bool m_PinchDown;
        /// modelwheel: tap-vs-drag of one pinch, shared with Model view's wheel (DialGesture).
        readonly DialGesture m_Gesture = new DialGesture();
        float m_LastTickAt = -1f, m_Pulse, m_Touch, m_SettledSince = -1f, m_ArmedUntil = -1f;
        int m_LastEquipped = -1, m_Armed = -1;
        bool m_TipArmed;
        float m_PokeCooldown;
        Vector3 m_TouchLocal = new Vector3(0f, 0f, 1f);
        bool m_WasOpen;
        /// D3: how much the lens is inked (1 while the tool in hand sits under it).
        float m_LensInk;
        public float LensInk => m_LensInk;

        static readonly int s_TouchId = Shader.PropertyToID("_Touch"), s_PulseId = Shader.PropertyToID("_Pulse"),
            s_KeyLightId = Shader.PropertyToID("_KeyLight"), s_ShapeId = Shader.PropertyToID("_Shape"),
            s_ArcRadiusId = Shader.PropertyToID("_ArcRadius"), s_ArcHalfWidthId = Shader.PropertyToID("_ArcHalfWidth"),
            s_ArcHalfAngleId = Shader.PropertyToID("_ArcHalfAngle"), s_LensCenterId = Shader.PropertyToID("_LensCenter"),
            s_LensRadiusId = Shader.PropertyToID("_LensRadius"), s_RectSizeId = Shader.PropertyToID("_RectSize"),
            s_CornerId = Shader.PropertyToID("_CornerRadius"), s_TintId = Shader.PropertyToID("_Tint"),
            s_OpacityId = Shader.PropertyToID("_Opacity"), s_LensInkId = Shader.PropertyToID("_LensInk");

        ToolManager m_Tools;

        void OnEnable() => Services.Register(this);

        void OnDisable()
        {
            Services.Unregister(this);
            if (m_Tools != null) m_Tools.Changed -= OnToolChanged;
            m_Tools = null;
        }

        /// The tool changed from elsewhere (voice, a part taken into the hand, the menu hand's double pinch): show it,
        /// unless the ring is being worked or it was the ring's own doing.
        void OnToolChanged(ToolKind kind)
        {
            int i = IndexOfMode(kind);
            if (i < 0 || Interacting || i == Dial.Selected) return;
            if (menu != null && !menu.IsOpen) { Dial.Jump(i); m_LastEquipped = i; return; }
            Dial.SpinTo(i);
            m_LastEquipped = i;
        }

        void Awake()
        {
            Dial = new DialPhysics(Mathf.Max(1, items.Length), Mathf.Max(0, IndexOfMode(ToolManager.Default)));
            m_Mpb = new MaterialPropertyBlock();
            m_ButtonMpb = new MaterialPropertyBlock();
            float extent = Mathf.Max(halfWidth, lensRadius) + 0.012f;
            if (glass != null)
            {
                var mf = glass.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = GlassRingMesh.Arc(radius - extent, radius + extent, arcHalfAngle + Mathf.Rad2Deg * (halfWidth + 0.006f) / radius, 72);
            }
            foreach (var b in new[] { undoGlass, redoGlass })
            {
                if (b == null) continue;
                var mf = b.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = GlassRingMesh.Quad(buttonSize, 0.006f);
            }
            m_Audio = GetComponent<AudioSource>();
            if (m_Audio == null) m_Audio = gameObject.AddComponent<AudioSource>();
            m_Audio.playOnAwake = false;
            m_Audio.spatialBlend = 1f;
            m_Audio.minDistance = 0.3f;
            m_Audio.volume = tickVolume;
            Layout(1f);
        }

        int IndexOfMode(ToolKind kind)
        {
            for (int i = 0; i < items.Length; i++) if (items[i].isMode && items[i].mode == kind) return i;
            return -1;
        }

        // ---------------- frame ----------------

        void Update()
        {
            if (Dial == null || items.Length == 0) return;
            if (m_Tools == null && Services.TryGet(out m_Tools)) m_Tools.Changed += OnToolChanged;
            float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            bool open = menu == null || menu.IsOpen;
            if (open && !m_WasOpen) OnOpened();
            m_WasOpen = open;
            if (open) HandleInput(now, dt);
            else if (m_Gesture.Active) EndPinch(false, now);

            int crossed = Dial.Step(dt);
            if (crossed != 0) OnTicks(crossed, now);
            SettleAndEquip(now);
            if (menu != null) menu.KeepOpen = Interacting;
            if (m_ArmedUntil > 0f && now > m_ArmedUntil) { m_Armed = -1; m_ArmedUntil = -1f; }
            m_Pulse = Mathf.MoveTowards(m_Pulse, 0f, dt * 7f);
            m_Touch = Mathf.MoveTowards(m_Touch, m_Gesture.Active ? 1f : 0f, dt * (m_Gesture.Active ? 6f : 3f));
            Layout(dt);
            RefreshUndoRedo();
            UpdateGlass();
            UiFeedback.Tick(now);
        }

        /// The menu just opened: show the tool that's in hand at the top (no spin).
        void OnOpened()
        {
            if (!Services.TryGet<ToolManager>(out var tools)) return;
            int i = IndexOfMode(tools.Active == ToolKind.None ? ToolManager.Default : tools.Active);
            if (i >= 0 && i != Dial.Selected) Dial.Jump(i);
            m_LastEquipped = Dial.Selected;
            m_Armed = -1;
        }

        void SettleAndEquip(float now)
        {
            if (!Dial.Settled) { m_SettledSince = -1f; return; }
            if (m_SettledSince < 0f) m_SettledSince = now;
            int sel = Dial.Selected;
            if (sel == m_LastEquipped || now - m_SettledSince < settleDelay) return;
            m_LastEquipped = sel;
            var it = items[sel];
            if (CommitOnPinch) return;   // D6: settling only previews; the lens pinch commits
            if (it.isMode && Services.TryGet<ToolManager>(out var tools) && tools.Active != it.mode)
            {
                tools.Equip(it.mode);
                LastAction = $"equipped {it.label}";
                Log.Info($"Ring: {LastAction}");
                AfterPick();
            }
        }

        void OnTicks(int crossed, float now)
        {
            Ticks += Mathf.Abs(crossed);
            m_Pulse = 1f;
            m_Armed = -1;
            if (now - m_LastTickAt < tickInterval || !Application.isPlaying) return;
            m_LastTickAt = now;
            if (s_Tick == null) s_Tick = MakeTick();
            m_Audio.pitch = 1f + Mathf.Clamp01(Mathf.Abs(Dial.Velocity) / Dial.MaxSpeed) * 0.12f;
            m_Audio.PlayOneShot(s_Tick, 1f);
            UiFeedback.Hover();
        }

        // ---------------- input ----------------

        void HandleInput(float now, float dt)
        {
            if (!TryPinch(out bool down, out var pinchWorld, out var tipWorld, out float strength))
            {
                if (m_Gesture.Active) EndPinch(false, now);
                m_TouchLocal.z = 1f;
                return;
            }
            var local = transform.InverseTransformPoint(pinchWorld);
            m_TouchLocal = transform.InverseTransformPoint(tipWorld);
            m_TouchLocal.z = Mathf.Abs(m_TouchLocal.z);
            if (!m_Gesture.Active && down)
            {
                if (InZone(local) && (menu == null || menu.AcceptingPresses)) BeginPinch(local, now);
            }
            else if (m_Gesture.Active && down) MovePinch(local, dt);
            else if (m_Gesture.Active) EndPinch(true, now);
            if (!m_Gesture.Active && strength < 0.3f) Poke(transform.InverseTransformPoint(tipWorld), now);
        }

        bool TryPinch(out bool down, out Vector3 pinchPoint, out Vector3 tip, out float strength)
        {
            down = false; pinchPoint = tip = default; strength = 0f;
            bool controller = pinchController != null && (OVRInput.GetConnectedControllers() & OVRInput.Controller.RTouch) != 0;
            if (controller)
            {
                strength = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
                pinchPoint = tip = pinchController.position;
            }
            else if (pinchHand != null && pinchHand.IsConnected && pinchOvrHand != null && pinchOvrHand.IsTracked
                     && pinchHand.GetJointPose(HandJointId.HandIndexTip, out var ip) && pinchHand.GetJointPose(HandJointId.HandThumbTip, out var tp))
            {
                strength = pinchOvrHand.GetFingerPinchStrength(OVRHand.HandFinger.Index);
                pinchPoint = (ip.position + tp.position) * 0.5f;
                tip = ip.position;
            }
            else { m_PinchDown = false; return false; }
            if (!m_PinchDown && strength >= pinchOn) m_PinchDown = true;
            else if (m_PinchDown && strength <= pinchOff) m_PinchDown = false;
            down = m_PinchDown;
            return true;
        }

        bool InZone(Vector3 local) =>
            Mathf.Abs(local.z) < grabDepth && new Vector2(local.x, local.y).magnitude < radius + halfWidth + 0.045f;

        void BeginPinch(Vector3 local, float now)
        {
            m_Gesture.TapMove = tapMove;
            m_Gesture.TapSeconds = tapSeconds;
            m_Gesture.Begin(local, now);
        }

        /// Turn the wheel by the pinch's angle around its centre: the item under the fingers follows them.
        void MovePinch(Vector3 local, float dt) => m_Gesture.Move(local, dt, Dial);

        /// dψ for a move dp at p, ψ = the clockwise angle from the top (modelwheel: WheelMath, shared with the model wheel).
        public static float DragAngle(Vector2 p, Vector2 dp, float minRadius = 0.05f) => WheelMath.DragAngle(p, dp, minRadius);

        void EndPinch(bool released, float now)
        {
            bool dragged = m_Gesture.Dragging;
            var r = m_Gesture.End(released, now, Dial);
            if (dragged)
            {
                Log.Info($"Ring: released at {Dial.Velocity:0.0} rad/s, {Ticks} ticks");
                if (UiSettings.ReducedMotion) Dial.SpinTo(Dial.Selected);
            }
            else if (r == DialGesture.Result.Tap) Tap(m_Gesture.Start);
        }

        /// A fingertip pushed through the ring's plane from the front: a tap where it went in.
        void Poke(Vector3 tipLocal, float now)
        {
            bool inRange = new Vector2(tipLocal.x, tipLocal.y).magnitude < radius + halfWidth + 0.04f;
            if (!inRange) { m_TipArmed = false; return; }
            if (tipLocal.z < -0.015f) m_TipArmed = true;   // in front of the glass (the viewer's side is −z)
            else if (m_TipArmed && tipLocal.z > -0.003f && now >= m_PokeCooldown && (menu == null || menu.AcceptingPresses))
            {
                m_TipArmed = false;
                m_PokeCooldown = now + 0.35f;
                Tap(tipLocal);
            }
        }

        /// A tap at a ring-local point: Undo / Redo, the lens (fires an action), or a side item (spins to it).
        public void Tap(Vector2 p)
        {
            if (Vector2.Distance(p, ButtonPos(-1)) < Mathf.Max(buttonSize.x, buttonSize.y) * 0.75f) { PressUndo(); return; }
            if (Vector2.Distance(p, ButtonPos(+1)) < Mathf.Max(buttonSize.x, buttonSize.y) * 0.75f) { PressRedo(); return; }
            int best = -1; float bd = tapRadius;
            for (int i = 0; i < items.Length; i++)
            {
                float a = Dial.ItemAngle(i);
                if (Mathf.Abs(a) * Mathf.Rad2Deg > visibleHalfAngle) continue;
                float d = Vector2.Distance(ItemPos(a), p);
                if (d < bd) { bd = d; best = i; }
            }
            if (best < 0) return;
            if (best == Dial.Selected && Dial.Settled) Activate(best);
            else { Dial.SpinTo(best); m_Armed = -1; LastAction = $"spin to {items[best].label}"; Log.Info($"Ring: {LastAction}"); }
        }

        /// Spin to item i (harness / tests).
        public void SpinTo(int i) { if (i >= 0 && i < items.Length) Dial.SpinTo(i); }

        /// Throw the wheel at `velocity` rad/s as if flicked (harness / tests).
        public void Flick(float velocity)
        {
            Dial.BeginDrag();
            Dial.Drag(velocity * 0.02f, 0.02f);
            Dial.Drag(velocity * 0.02f, 0.02f);
            Dial.Drag(velocity * 0.02f, 0.02f);
            Dial.EndDrag();
        }

        /// The lens was pinched: modes are already on; actions fire (Exit world asks for a second pinch).
        public void Activate(int i)
        {
            var it = items[i];
            UiFeedback.Press(transform.position);
            m_Pulse = 1f;
            if (it.isMode)
            {
                if (Services.TryGet<ToolManager>(out var tools) && tools.Active == it.mode && it.mode == ToolKind.Measure
                    && tools.measure != null && MeasureTool.AutoSaveTwoPointTapes)
                {
                    // D4: with Line mode saving at point 2, pinching the Measure lens while measuring switches Line ↔ Area
                    // (the hands-only way into shapes; voice "area" does the same).
                    tools.measure.AreaMode = !tools.measure.AreaMode;
                    m_LastEquipped = i;
                    LastAction = tools.measure.AreaMode ? "Measure: area mode" : "Measure: line mode";
                    UiToast.Show(tools.measure.AreaMode ? "Area · tap the first point or pinch your left hand to close"
                                                        : "Line · a tape saves at its 2nd point", ColorRole.Info);
                    Log.Info($"Ring: {LastAction}");
                    AfterPick();
                    return;
                }
                if (tools != null && tools.Active != it.mode) tools.Equip(it.mode);
                m_LastEquipped = i;
                LastAction = $"equipped {it.label}";
                if (CommitOnPinch) Log.Info($"Ring: {LastAction}");
                AfterPick();
                return;
            }
            if (it.confirm && m_Armed != i)
            {
                m_Armed = i;
                m_ArmedUntil = Time.unscaledTime + 3f;
                LastAction = $"armed {it.label}";
                Log.Info($"Ring: {LastAction}");
                return;
            }
            m_Armed = -1;
            ToolboxButton.Run(it.action);
            LastAction = $"ran {it.action}";
            Log.Info($"Ring: {LastAction}");
            AfterPick();
        }

        /// A controller-opened ring closes after a pick (UX W0.10): it no longer sits there holding the trigger.
        void AfterPick()
        {
            if (menu != null && menu.OpenedBy == "controller") menu.CloseFromController();
        }

        public void PressUndo()
        {
            bool ok = EditHistory.Undo();
            if (ok) m_Pulse = 0.6f;   // the undo sound + haptic come from EditHistory (FeedbackEvents.Undo)
            LastAction = ok ? "undo" : "nothing to undo";
            Log.Info($"Ring: {LastAction}");
            if (!ok) UiToast.Show("Nothing to undo", ColorRole.Info);
        }

        public void PressRedo()
        {
            bool ok = EditHistory.Redo();
            if (ok) m_Pulse = 0.6f;
            LastAction = ok ? "redo" : "nothing to redo";
            Log.Info($"Ring: {LastAction}");
            if (!ok) UiToast.Show("Nothing to redo", ColorRole.Info);
        }

        // ---------------- layout + look ----------------

        public Vector2 ItemPos(float angle) => new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * radius;

        /// How opaque an item `deg` degrees from the top is: 1 up to the fade band, easing to 0 at visibleHalf (hidden
        /// beyond). Pure (EditMode-tested; modelwheel: WheelMath, shared with the model wheel).
        public static float Fade(float deg, float visibleHalf, float band) => WheelMath.Fade(deg, visibleHalf, band);
        Vector2 ButtonPos(int side) => ItemPos(side * buttonAngle * Mathf.Deg2Rad);
        Vector3 ButtonWorld(int side) => transform.TransformPoint(ButtonPos(side));

        void Layout(float dt)
        {
            if (Dial == null) return;
            var colors = UiTheme.Current.colors;
            var icons = UiTheme.Current.icons;
            ToolKind active = Services.TryGet<ToolManager>(out var tools) ? tools.Active : ToolKind.None;
            float spacing = Dial.Spacing;
            float activeNear = 0f;
            for (int i = 0; i < items.Length; i++)
            {
                var it = items[i];
                if (it.root == null) continue;
                float a = Dial.ItemAngle(i);
                float deg = Mathf.Abs(a) * Mathf.Rad2Deg;
                bool visible = deg < visibleHalfAngle;
                if (it.root.gameObject.activeSelf != visible) it.root.gameObject.SetActive(visible);
                if (!visible) continue;
                float near = 1f - Mathf.Clamp01(Mathf.Abs(a) / (spacing * 0.9f));   // 1 under the lens
                if (it.isMode && it.mode == active) activeNear = near;
                float fade = Fade(deg, visibleHalfAngle, fadeBandDeg);
                var pos = ItemPos(a);
                it.root.localPosition = new Vector3(pos.x, pos.y, 0f);
                it.root.localRotation = Quaternion.identity;
                it.root.localScale = Vector3.one * Mathf.Lerp(0.9f, lensScale, near * near);   // ≥ 16 dmm off the lens (UX W0.6)
                bool top = near > 0.5f;
                if (it.iconText != null)
                {
                    if (top != it.filled && icons.fill != null && icons.regular != null)
                    {
                        it.filled = top;
                        it.iconText.font = top ? icons.fill : icons.regular;
                        if (icons.fillMaterial != null && icons.regularMaterial != null)
                            it.iconText.fontSharedMaterial = top ? icons.fillMaterial : icons.regularMaterial;
                    }
                    var c = Color.white;
                    c.a = Mathf.Lerp(0.66f, 1f, near) * fade;
                    it.iconText.color = c;
                }
                if (it.labelText != null)
                {
                    bool isActive = it.isMode && it.mode == active;
                    var lc = isActive ? colors.ink : colors.textPrimary;   // D3: the active tool is ink
                    lc.a = Mathf.Lerp(0.62f, 1f, near) * fade;
                    it.labelText.color = lc;
                }
            }
            // D3: the lens is inked as the tool in hand comes under it (a previewed mode under D6, or an action, isn't).
            m_LensInk = activeNear;
            if (hint != null)
            {
                string h = "";
                int sel = Dial.Selected;
                if (Dial.Settled && sel >= 0 && sel < items.Length && !items[sel].isMode)
                {
                    bool c = InputMode.Controllers;
                    h = m_Armed == sel ? (c ? "Trigger again to leave" : "Pinch again to leave") : (c ? "Trigger to open" : "Pinch to open");
                }
                else if (CommitOnPinch && Dial.Settled && sel >= 0 && sel < items.Length && items[sel].isMode && items[sel].mode != active)
                    h = InputMode.Controllers ? "Trigger to use" : "Pinch to use";
                else if (Dial.Settled && sel >= 0 && sel < items.Length && items[sel].isMode && items[sel].mode == ToolKind.Measure
                         && active == ToolKind.Measure && MeasureTool.AutoSaveTwoPointTapes && Services.TryGet<ToolManager>(out var tm) && tm.measure != null)
                    h = (InputMode.Controllers ? "Trigger for " : "Pinch for ") + (tm.measure.AreaMode ? "line" : "area");
                if (hint.text != h) hint.text = h;
            }
        }

        void RefreshUndoRedo()
        {
            UndoAvailable = EditHistory.CanUndo;
            RedoAvailable = EditHistory.CanRedo;
            // Available: a solid glyph on denser glass. Nothing to do: washed out and lighter.
            if (undoIcon != null) undoIcon.color = new Color(1f, 1f, 1f, UndoAvailable ? 1f : 0.32f);
            if (redoIcon != null) redoIcon.color = new Color(1f, 1f, 1f, RedoAvailable ? 1f : 0.32f);
        }

        void UpdateGlass()
        {
            var key = Vector3.up;
            if (head != null) key = (Vector3.up + (head.position - transform.position).normalized * 0.55f).normalized;
            if (glass != null)
            {
                glass.GetPropertyBlock(m_Mpb);
                m_Mpb.SetFloat(s_ShapeId, 0f);
                m_Mpb.SetFloat(s_ArcRadiusId, radius);
                m_Mpb.SetFloat(s_ArcHalfWidthId, halfWidth);
                m_Mpb.SetFloat(s_ArcHalfAngleId, arcHalfAngle * Mathf.Deg2Rad);
                m_Mpb.SetVector(s_LensCenterId, new Vector4(0f, radius, 0f, 0f));
                m_Mpb.SetFloat(s_LensRadiusId, lensRadius);
                m_Mpb.SetVector(s_KeyLightId, key);
                m_Mpb.SetFloat(s_PulseId, m_Pulse);
                m_Mpb.SetVector(s_TouchId, new Vector4(m_TouchLocal.x, m_TouchLocal.y, m_TouchLocal.z, m_Touch));
                // SetColor (not SetVector): the sRGB ink token is linearised for the Linear project; a = how much.
                var ink = UiTheme.Current.colors.ink;
                m_Mpb.SetColor(s_LensInkId, new Color(ink.r, ink.g, ink.b, m_LensInk));
                glass.SetPropertyBlock(m_Mpb);
            }
            SetButton(undoGlass, key, UndoAvailable);
            SetButton(redoGlass, key, RedoAvailable);
        }

        void SetButton(MeshRenderer r, Vector3 key, bool available)
        {
            if (r == null) return;
            r.GetPropertyBlock(m_ButtonMpb);
            m_ButtonMpb.SetFloat(s_ShapeId, 1f);
            m_ButtonMpb.SetVector(s_RectSizeId, new Vector4(buttonSize.x, buttonSize.y, 0f, 0f));
            m_ButtonMpb.SetFloat(s_CornerId, Mathf.Min(buttonSize.x, buttonSize.y) * 0.5f);
            m_ButtonMpb.SetVector(s_KeyLightId, key);
            // SetColor (not SetVector): colour properties are converted from sRGB in the Linear project.
            m_ButtonMpb.SetColor(s_TintId, available ? new Color(0.07f, 0.08f, 0.10f, 0.4f) : new Color(0.2f, 0.21f, 0.24f, 0.14f));
            m_ButtonMpb.SetFloat(s_OpacityId, available ? 1f : 0.7f);
            m_ButtonMpb.SetVector(s_TouchId, new Vector4(0f, 0f, 1f, 0f));
            m_ButtonMpb.SetFloat(s_PulseId, 0f);
            r.SetPropertyBlock(m_ButtonMpb);
        }

        /// A soft detent click: a 4 ms burst at ~2.9 kHz with a fast decay and a little noise.
        static AudioClip MakeTick()
        {
            const int rate = 44100;
            int n = rate / 60;
            var data = new float[n];
            var rng = new System.Random(7);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float env = Mathf.Exp(-t / 0.0022f);
                data[i] = env * (0.55f * Mathf.Sin(2f * Mathf.PI * 2900f * t) + 0.25f * (float)(rng.NextDouble() * 2.0 - 1.0));
            }
            var clip = AudioClip.Create("RingTick", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
