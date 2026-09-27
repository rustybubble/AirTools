using System.Collections.Generic;
using AirTools.Core;
using AirTools.UI;
using Oculus.Interaction;
using TMPro;
using UnityEngine;

namespace AirTools.Scene
{
    /// Model view's wheel (modelwheel; the user's call, Sat 09-26: "recreate the toolbox without it being a super small
    /// circle, a much wider radius"): the palm ring's prize wheel, 0.55 m across, right under the floating model and facing
    /// your face. The cards are the models (the scan's picture and its name); the one under the lens at the top is
    /// previewed (its name, scale and what a pinch does, under it) and the model on view is the ink card.
    /// - An endless loop: the cards wrap round (seven models repeat without a seam; a short list shows each once).
    /// - Pinch the wheel with a hand or controller ray (or pull the trigger) and drag to spin it: the card under the ray
    ///   follows it; let go and it coasts with your flick, ticking at every card, then springs onto the nearest one —
    ///   the same wheel physics as the ring (DialPhysics, DialGesture, WheelMath). Offline "Not downloaded" cards are
    ///   dimmed and a coasting wheel never rests on them.
    /// - A quick pinch (a tap) on a side card spins it to the lens; on the lens card it opens that model on view
    ///   (ModelSwitcher.Choose: loading, up next, retry, exactly as a card press did on the old row). A tap needs the ray
    ///   to rest on the wheel for a moment first (D5), so a pinch sweeping past never opens anything.
    /// - Walk in, Recentre and Exit are chips under the wheel (poke or ray).
    /// - Placed from the model (TabletopController.TryWheelPose) every frame, so it follows a recentre's glide; shown only
    ///   while Model view is up and settled (ModelSwitcher.Shown). No allocation per frame after warm-up.
    public class ModelWheel : MonoBehaviour
    {
        public ModelSwitcher switcher;
        public TabletopController tabletop;
        public Transform head;
        [Tooltip("Everything the wheel shows (hidden outside Model view).")]
        public GameObject content;
        [Tooltip("Card slots left to right; the middle one sits under the lens. Visual only: the wheel's own pinch presses them.")]
        public GlassButton[] cards = new GlassButton[0];
        [Tooltip("Each card's picture quad (unlit; the texture goes in through a property block).")]
        public MeshRenderer[] thumbs = new MeshRenderer[0];
        [Tooltip("Each card's badge over its picture (\"Not downloaded\").")]
        public TextMeshPro[] badges = new TextMeshPro[0];
        [Tooltip("The liquid glass arc behind the cards (its mesh is made at runtime).")]
        public MeshRenderer glass;
        [Tooltip("Under the lens: the model's name, then its scale and what a pinch does.")]
        public TextMeshPro lensName, lensDetail;
        public GlassButton walkIn, recentre, exit;
        [Tooltip("The ray target over the wheel: hands and controllers pinch (trigger) and drag it.")]
        public RayInteractable target;

        [Header("Geometry (m, degrees)")]
        public float radius = ModelViewLayout.WheelRadius;
        public float spacingDeg = ModelViewLayout.WheelSpacingDeg;
        public float visibleHalfAngle = ModelViewLayout.WheelVisibleHalfDeg;
        public float fadeBandDeg = ModelViewLayout.WheelFadeBandDeg;
        public Vector2 cardSize = new Vector2(0.15f, 0.118f);
        [Tooltip("The card under the lens is magnified by this much; the others shrink to sideScale.")]
        public float lensScale = 1.12f, sideScale = 0.92f;
        public float halfWidth = 0.074f, lensRadius = 0.098f;
        [Tooltip("Read distance the text was built for (m): the side cards from a standing eye.")]
        public float readDistance = 0.9f;

        [Header("Feel")]
        public float tapMove = ModelViewLayout.WheelTapMove;
        public float tapSeconds = ModelViewLayout.WheelTapSeconds;
        public float tickInterval = 0.028f;

        public bool Shown { get; private set; }
        public DialPhysics Dial => m_Dial;
        /// Being pinched or still moving (Model view doesn't recentre meanwhile).
        public bool Interacting => m_Gesture.Active || (m_Dial != null && !m_Dial.Settled);
        /// The model under the lens and its state.
        public string LensSite { get; private set; }
        public ModelCardState LensState { get; private set; }
        public int Ticks { get; private set; }
        public int Taps { get; private set; }
        public int Commits { get; private set; }
        public string LastAction { get; private set; } = "";
        /// The lens card's top above the lens centre (m): what has to clear the model.
        public float LensTop => cardSize.y * 0.5f * lensScale;
        public int Slots => cards != null ? cards.Length : 0;
        public int LensSlot => Slots / 2;

        DialPhysics m_Dial;
        readonly DialGesture m_Gesture = new DialGesture();
        System.Func<int, bool> m_Allowed;
        SceneStreamer m_Streamer;
        int m_SitesVersion = -1, m_Base;
        float m_Offset, m_Pulse, m_LastTickAt = -1f, m_NextRefresh, m_Ink, m_Touch;
        string m_LastPending;
        RayInteractor m_Grabber, m_Hover;
        float m_HoverSince = -1f, m_Dwell;
        Vector2 m_TouchLocal;
        // Per slot: what it shows, where, how big.
        string[] m_SlotSite;
        ModelCardState[] m_SlotState;
        Texture[] m_SlotThumb;
        bool[] m_SlotVisible;
        Vector2[] m_SlotPos;
        float[] m_SlotScale;
        // The lens preview as last written.
        string m_ShownLensSite, m_ShownScale;
        ModelCardState m_ShownLensState = (ModelCardState)(-1);
        bool m_ShownControllers;
        // What the cards were last drawn from: a change redraws them at once (not on the next 0.1 s refresh), so the model
        // that just landed is the ink card the frame it's on view.
        string m_SeenCurrent, m_SeenLoading, m_SeenQueued;
        int m_SeenLoads = -1, m_SeenFailures = -1, m_SeenKnown = -1;
        bool m_SeenOnline;
        readonly Dictionary<string, string> m_Names = new Dictionary<string, string>();
        readonly Dictionary<string, string> m_Scales = new Dictionary<string, string>();
        MaterialPropertyBlock m_Block, m_GlassBlock;

        static readonly int s_BaseMap = Shader.PropertyToID("_BaseMap"), s_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int s_TouchId = Shader.PropertyToID("_Touch"), s_PulseId = Shader.PropertyToID("_Pulse"),
            s_KeyLightId = Shader.PropertyToID("_KeyLight"), s_ShapeId = Shader.PropertyToID("_Shape"),
            s_ArcRadiusId = Shader.PropertyToID("_ArcRadius"), s_ArcHalfWidthId = Shader.PropertyToID("_ArcHalfWidth"),
            s_ArcHalfAngleId = Shader.PropertyToID("_ArcHalfAngle"), s_LensCenterId = Shader.PropertyToID("_LensCenter"),
            s_LensRadiusId = Shader.PropertyToID("_LensRadius"), s_LensInkId = Shader.PropertyToID("_LensInk");

        void Awake()
        {
            EnsureArrays();
            if (glass != null)
            {
                var mf = glass.GetComponent<MeshFilter>();
                float extent = Mathf.Max(halfWidth, lensRadius) + 0.012f;
                if (mf != null) mf.sharedMesh = GlassRingMesh.Arc(radius - extent, radius + extent, visibleHalfAngle + 6f, 96);
            }
        }

        void OnEnable()
        {
            Services.Register(this);
            if (target == null) return;
            target.WhenSelectingInteractorAdded.Action += OnSelect;
            target.WhenSelectingInteractorRemoved.Action += OnUnselect;
            target.WhenInteractorAdded.Action += OnHover;
            target.WhenInteractorRemoved.Action += OnUnhover;
        }

        void OnDisable()
        {
            Services.Unregister(this);
            if (target != null)
            {
                target.WhenSelectingInteractorAdded.Action -= OnSelect;
                target.WhenSelectingInteractorRemoved.Action -= OnUnselect;
                target.WhenInteractorAdded.Action -= OnHover;
                target.WhenInteractorRemoved.Action -= OnUnhover;
            }
            m_Grabber = m_Hover = null;
            if (m_Gesture.Active) m_Gesture.End(false, Time.unscaledTime, m_Dial);
        }

        void EnsureArrays()
        {
            int n = Slots;
            if (m_SlotSite != null && m_SlotSite.Length == n) return;
            m_SlotSite = new string[n];
            m_SlotState = new ModelCardState[n];
            m_SlotThumb = new Texture[n];
            m_SlotVisible = new bool[n];
            m_SlotPos = new Vector2[n];
            m_SlotScale = new float[n];
        }

        // ---------------- frame ----------------

        void Update() => Tick(Time.unscaledTime, Time.unscaledDeltaTime);

        /// One frame (Update; EditMode tests step it with their own clock).
        public void Tick(float now, float dt)
        {
            if (switcher == null) return;
            Services.TryGet(out m_Streamer);
            bool show = switcher.Shown && switcher.Sites.Count > 0 && content != null;
            if (show != Shown)
            {
                Shown = show;
                if (content != null) content.SetActive(show);
                if (show) OnShown();
                else if (m_Gesture.Active) { m_Gesture.End(false, now, m_Dial); m_Grabber = null; }
            }
            if (!Shown) return;
            SyncDial();
            Place();
            HandleInput(now, dt);
            int crossed = m_Dial.Step(dt);
            if (crossed != 0) OnTicks(crossed, now);
            m_Pulse = Mathf.MoveTowards(m_Pulse, 0f, dt * 7f);
            m_Touch = Mathf.MoveTowards(m_Touch, m_Grabber != null || m_Hover != null ? 1f : 0f, dt * 6f);
            Layout();
            if (StateChanged()) m_NextRefresh = 0f;
            if (now >= m_NextRefresh) { m_NextRefresh = now + 0.1f; RefreshSlots(); }
            UpdateGlass(dt);
        }

        /// The model on view, the one loading or waiting, a load's end, or the laptop / headset listing changed since the
        /// cards were drawn. No allocation.
        bool StateChanged()
        {
            string current = switcher.Current, loading = switcher.Loading, queued = switcher.Queued;
            int loads = switcher.Loads, failures = switcher.Failures, known = m_Streamer != null ? m_Streamer.KnownVersion : 0;
            bool online = m_Streamer != null && m_Streamer.ServerListed;
            if (current == m_SeenCurrent && loading == m_SeenLoading && queued == m_SeenQueued && loads == m_SeenLoads
                && failures == m_SeenFailures && known == m_SeenKnown && online == m_SeenOnline) return false;
            m_SeenCurrent = current; m_SeenLoading = loading; m_SeenQueued = queued;
            m_SeenLoads = loads; m_SeenFailures = failures; m_SeenKnown = known; m_SeenOnline = online;
            return true;
        }

        /// Model view just came up: the model on view (or the one on its way) under the lens, no spin.
        void OnShown()
        {
            EnsureArrays();
            m_SitesVersion = -1;
            SyncDial();
            int i = ModelSites.IndexOf(switcher.Sites, switcher.Focus);
            if (i >= 0) m_Dial.Jump(i);
            m_LastPending = switcher.Queued ?? switcher.Loading;
            for (int s = 0; s < m_SlotSite.Length; s++) { m_SlotSite[s] = null; m_SlotThumb[s] = null; }
            m_ShownLensSite = null;
            m_NextRefresh = 0f;
            Place();
            Layout();
            RefreshSlots();
        }

        /// The list changed: a new dial over it with the same model under the lens. A model chosen elsewhere (voice,
        /// show_model, next_model): spin to it.
        void SyncDial()
        {
            var sites = switcher.Sites;
            if (m_Dial == null || m_SitesVersion != switcher.SitesVersion)
            {
                if (m_Gesture.Active) { m_Gesture.End(false, Time.unscaledTime, m_Dial); m_Grabber = null; }
                string keep = LensSite ?? switcher.Focus;
                int i = ModelSites.IndexOf(sites, keep);
                if (i < 0) i = Mathf.Max(0, ModelSites.IndexOf(sites, switcher.Focus));
                m_Allowed ??= IsAllowed;
                m_Dial = DialPhysics.Strip(Mathf.Max(1, sites.Count), i, spacingDeg * Mathf.Deg2Rad);
                m_Dial.Allowed = m_Allowed;
                m_SitesVersion = switcher.SitesVersion;
                for (int s = 0; s < (m_SlotSite?.Length ?? 0); s++) m_SlotSite[s] = null;
            }
            string pending = switcher.Queued ?? switcher.Loading;
            if (pending != null && pending != m_LastPending && !m_Gesture.Active)
            {
                int i = ModelSites.IndexOf(sites, pending);
                if (i >= 0 && i != m_Dial.Selected) m_Dial.SpinTo(i);
            }
            m_LastPending = pending;
        }

        /// A coasting wheel rests only on models that open now (offline: what's on the headset, and the built-in facade).
        bool IsAllowed(int i)
        {
            var sites = switcher != null ? switcher.Sites : null;
            return sites != null && i >= 0 && i < sites.Count && ModelSwitcher.IsAvailable(m_Streamer, sites[i]);
        }

        /// Under the model, facing the eyes (TabletopController.TryWheelPose).
        void Place()
        {
            if (tabletop != null && tabletop.TryWheelPose(LensTop, out var pose)) transform.SetPositionAndRotation(pose.position, pose.rotation);
        }

        void OnTicks(int crossed, float now)
        {
            Ticks += Mathf.Abs(crossed);
            m_Pulse = 1f;
            if (now - m_LastTickAt < tickInterval || !Application.isPlaying) return;
            m_LastTickAt = now;
            UiFeedback.Hover();   // the detent: a tick (sound and a light buzz)
        }

        // ---------------- input: a hand or controller ray on the wheel ----------------

        void OnHover(RayInteractor r)
        {
            if (m_Hover != null) return;
            m_Hover = r;
            m_HoverSince = Time.unscaledTime;
        }

        void OnUnhover(RayInteractor r) { if (m_Hover == r) m_Hover = null; }

        void OnSelect(RayInteractor r)
        {
            if (m_Grabber != null || m_Dial == null || !Shown) return;
            m_Grabber = r;
            m_Dwell = m_Hover == r && m_HoverSince >= 0f ? Time.unscaledTime - m_HoverSince : 0f;
            if (!TryPlane(r.Ray, out var p)) return;
            m_Gesture.TapMove = tapMove;
            m_Gesture.TapSeconds = tapSeconds;
            m_Gesture.Begin(p - WheelMath.Centre(radius), Time.unscaledTime);
        }

        void OnUnselect(RayInteractor r)
        {
            if (m_Grabber != r) return;
            if (TryPlane(r.Ray, out var p)) m_Gesture.Move(p - WheelMath.Centre(radius), 0f, m_Dial);
            m_Grabber = null;
            EndGesture(true, Time.unscaledTime);
        }

        void HandleInput(float now, float dt)
        {
            var ray = m_Grabber != null ? m_Grabber : m_Hover;
            if (ray != null && TryPlane(ray.Ray, out var p)) m_TouchLocal = p;
            if (m_Grabber == null) return;
            if (!TryPlane(m_Grabber.Ray, out p)) return;
            var c = p - WheelMath.Centre(radius);
            if (!m_Gesture.Active)
            {
                m_Gesture.TapMove = tapMove;
                m_Gesture.TapSeconds = tapSeconds;
                m_Gesture.Begin(c, now);
            }
            else m_Gesture.Move(c, dt, m_Dial);
        }

        void EndGesture(bool released, float now)
        {
            bool dragged = m_Gesture.Dragging;
            var start = m_Gesture.Start + WheelMath.Centre(radius);
            var r = m_Gesture.End(released, now, m_Dial);
            if (dragged)
            {
                LastAction = $"spun ({m_Dial.Velocity:0.0} rad/s)";
                Log.Info($"Model wheel: released at {m_Dial.Velocity:0.0} rad/s, {Ticks} ticks");
                if (UiSettings.ReducedMotion) m_Dial.SnapToNearest();
            }
            else if (r == DialGesture.Result.Tap)
            {
                // D5: a ray press counts only after a short rest on the wheel (a pinch sweeping past never opens a model).
                if (m_Dwell >= GlassButton.RayDwellSeconds) TapAt(start);
                else LastAction = "tap ignored (the ray didn't rest on the wheel)";
            }
        }

        /// Where a ray meets the wheel's plane, in its frame (the lens at the origin, x right, y up).
        bool TryPlane(Ray ray, out Vector2 local)
        {
            local = default;
            var o = transform.InverseTransformPoint(ray.origin);
            var d = transform.InverseTransformDirection(ray.direction);
            if (Mathf.Abs(d.z) < 1e-4f) return false;
            float t = -o.z / d.z;
            if (t <= 0f) return false;
            local = new Vector2(o.x + d.x * t, o.y + d.y * t);
            return true;
        }

        /// Seen from `eye`, does this world point sit on a card the wheel shows (`margin`: the thing's own half size, m)?
        /// The "You are here" pin hides then: the built-in facade's spawn is 4 m out front, on the lens card at 1:10.
        public bool Covers(Vector3 eye, Vector3 point, Vector2 margin)
        {
            if (!Shown || m_SlotVisible == null || !TryPlane(new Ray(eye, point - eye), out var p)) return false;
            for (int s = 0; s < m_SlotVisible.Length; s++)
                if (m_SlotVisible[s] && OnCard(p, m_SlotPos[s], cardSize * (0.5f * m_SlotScale[s]) + margin)) return true;
            return false;
        }

        /// Inside a card's rectangle (the wheel's frame; `half`: its half size plus any margin).
        public static bool OnCard(Vector2 p, Vector2 centre, Vector2 half) =>
            Mathf.Abs(p.x - centre.x) <= half.x && Mathf.Abs(p.y - centre.y) <= half.y;

        /// A tap at a point on the wheel (its frame): the card there is pressed (GlassButton.Press → Clicked → Press).
        public bool TapAt(Vector2 p)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int s = 0; s < Slots; s++)
            {
                if (!m_SlotVisible[s]) continue;
                var c = m_SlotPos[s];
                var half = cardSize * (0.55f * m_SlotScale[s]);
                if (Mathf.Abs(p.x - c.x) > half.x || Mathf.Abs(p.y - c.y) > half.y) continue;
                float d = (p - c).sqrMagnitude;
                if (d < bestD) { bestD = d; best = s; }
            }
            if (best < 0) { LastAction = "tap between the cards"; return false; }
            Taps++;
            if (cards[best] != null && cards[best].Press()) return true;
            PressCard(best);   // cooling down from the last press, or no button: the same thing
            return true;
        }

        // ---------------- the controls ----------------

        /// A card or a chip was pressed (ModelWheelButton).
        public void Press(ModelWheelAction action, int slot)
        {
            switch (action)
            {
                case ModelWheelAction.Card: PressCard(slot); break;
                case ModelWheelAction.WalkIn:
                    LastAction = "walk in";
                    if (switcher != null) switcher.WalkIn();
                    break;
                case ModelWheelAction.Recentre:
                    bool ok = tabletop != null && tabletop.Recentre("chip");
                    LastAction = ok ? "recentre" : "recentre refused (not in Model view)";
                    break;
                case ModelWheelAction.Exit:
                    LastAction = "exit";
                    if (switcher != null) switcher.Exit();
                    break;
            }
        }

        /// The card in `slot`: under the lens (the wheel resting on it or settling there) it opens that model on view;
        /// a side card spins to the lens; a model that isn't on the headset says so.
        public void PressCard(int slot)
        {
            if (m_Dial == null || slot < 0 || slot >= Slots || !m_SlotVisible[slot] || m_SlotSite[slot] == null) return;
            string site = m_SlotSite[slot];
            int k = slot - LensSlot;
            var state = switcher.StateOf(site, m_Streamer);
            bool atLens = k == 0 && (m_Dial.Settled || (m_Dial.TargetItem == m_Base && Mathf.Abs(m_Offset) < 0.15f));
            if (atLens)
            {
                Commits++;
                m_Pulse = 1f;
                bool ok = switcher.Choose(site);
                LastAction = ok ? $"open {site}: {switcher.LastAction}" : $"refused {site}: {switcher.LastAction}";
                Log.Info($"Model wheel: {LastAction}");
            }
            else if (state == ModelCardState.NotDownloaded)
            {
                switcher.Choose(site);   // refused: "isn't on the headset · connect the laptop to download it"
                LastAction = $"not downloaded: {site}";
            }
            else
            {
                m_Dial.SpinToPosition(m_Base + k);
                LastAction = $"spin to {site}";
            }
        }

        // ---------------- harness ----------------

        /// Spin `steps` models along (+ next, − previous), skipping the ones that aren't on the headset; returns the model
        /// it heads for.
        public string SpinBy(int steps)
        {
            if (m_Dial == null) return null;
            int to = m_Dial.SpinBy(steps);
            LastAction = $"spin {steps:+0;-0;0}";
            var sites = switcher.Sites;
            return sites.Count > 0 ? sites[DialPhysics.Wrap(to, sites.Count)] : null;
        }

        /// Spin to a model by id (the short way round).
        public bool SpinToSite(string site)
        {
            int i = m_Dial != null ? ModelSites.IndexOf(switcher.Sites, site) : -1;
            if (i < 0) return false;
            m_Dial.SpinTo(i);
            LastAction = $"spin to {site}";
            return true;
        }

        /// Throw the wheel at `velocity` rad/s as if flicked (− brings the next models in from the right).
        public void Flick(float velocity)
        {
            if (m_Dial == null) return;
            m_Dial.BeginDrag();
            for (int i = 0; i < 3; i++) m_Dial.Drag(velocity * 0.02f, 0.02f);
            m_Dial.EndDrag();
            LastAction = $"flick {velocity:0.0} rad/s";
        }

        /// Pinch the lens (open the model under it), as a hand would.
        public bool PinchLens() => Slots > 0 && m_SlotVisible[LensSlot] && TapAt(m_SlotPos[LensSlot]);

        /// The card showing `site` now (the one nearest the lens), or null.
        public GlassButton CardFor(string site)
        {
            int best = -1;
            for (int s = 0; s < Slots; s++)
                if (m_SlotVisible[s] && m_SlotSite[s] == site && (best < 0 || Mathf.Abs(s - LensSlot) < Mathf.Abs(best - LensSlot))) best = s;
            return best >= 0 ? cards[best] : null;
        }

        /// The models on the visible cards, left to right ("[x]" under the lens), and the lens preview.
        public string Brief()
        {
            if (m_Dial == null) return $"shown={Shown} (no dial yet)";
            var shown = new List<string>();
            for (int s = 0; s < Slots; s++)
                if (m_SlotVisible[s]) shown.Add(s == LensSlot ? $"[{m_SlotSite[s]}]" : m_SlotSite[s]);
            return $"shown={Shown} lens={LensSite ?? "-"} ({LensState}) \"{(lensDetail != null ? lensDetail.text : "")}\" pos={m_Dial.Position:0.00} " +
                   $"settled={m_Dial.Settled} ticks={Ticks} taps={Taps} commits={Commits} cards=[{string.Join(", ", shown)}] last=\"{LastAction}\"";
        }

        // ---------------- layout + look ----------------

        /// Cards along the arc: where the wheel is, which model each slot shows, the lens card magnified.
        void Layout()
        {
            var sites = switcher.Sites;
            int n = sites.Count;
            float pos = m_Dial.Position;
            m_Base = WheelMath.SlotBase(pos);
            m_Offset = WheelMath.SlotOffset(pos);
            float sp = spacingDeg * Mathf.Deg2Rad;
            for (int s = 0; s < Slots; s++)
            {
                var card = cards[s];
                if (card == null) continue;
                int k = s - LensSlot;
                float a = WheelMath.SlotAngle(k, m_Offset, sp);
                float fade = WheelMath.SlotShown(k, m_Offset, n) ? WheelMath.Fade(Mathf.Abs(a) * Mathf.Rad2Deg, visibleHalfAngle, fadeBandDeg) : 0f;
                bool visible = fade > 0.04f;
                if (card.gameObject.activeSelf != visible) card.gameObject.SetActive(visible);
                m_SlotVisible[s] = visible;
                if (!visible) continue;
                string site = sites[WheelMath.SlotItem(m_Base, k, n)];
                if (site != m_SlotSite[s]) { m_SlotSite[s] = site; m_SlotThumb[s] = null; m_SlotState[s] = (ModelCardState)(-1); m_NextRefresh = 0f; }
                var p = WheelMath.ArcPoint(a, radius);
                float near = WheelMath.Near(a, sp);
                float scale = Mathf.Lerp(sideScale, lensScale, near * near) * Mathf.Lerp(0.3f, 1f, fade);
                var t = card.transform;
                t.localPosition = new Vector3(p.x, p.y, -0.004f * near - 0.008f * (1f - fade));
                t.localScale = new Vector3(scale, scale, scale);
                m_SlotPos[s] = p;
                m_SlotScale[s] = scale;
            }
            LensSite = m_SlotVisible.Length > 0 && m_SlotVisible[LensSlot] ? m_SlotSite[LensSlot] : null;
        }

        /// Names, states, badges, pictures and the lens preview — written only when they change.
        void RefreshSlots()
        {
            // The scale of the model on view, remembered per model for the lens preview.
            if (tabletop != null && tabletop.OnTable && !tabletop.Refitting && switcher.Loading == null && tabletop.ScaleLabel.Length > 0)
                m_Scales[switcher.Current] = tabletop.ScaleLabel;
            for (int s = 0; s < Slots; s++)
            {
                if (!m_SlotVisible[s] || cards[s] == null) continue;
                string site = m_SlotSite[s];
                var state = switcher.StateOf(site, m_Streamer);
                if (state != m_SlotState[s])
                {
                    m_SlotState[s] = state;
                    var badge = s < badges.Length ? badges[s] : null;
                    cards[s].SetText(badge != null ? ModelSites.CardName(NameOf(site), state) : ModelSites.CardText(NameOf(site), state));
                    if (badge != null)
                    {
                        string b = ModelSites.Badge(state);
                        badge.text = b;
                        if (badge.gameObject.activeSelf != b.Length > 0) badge.gameObject.SetActive(b.Length > 0);
                    }
                    cards[s].SetSelected(state == ModelCardState.Current);
                    m_SlotThumb[s] = null;   // re-tint
                }
                var thumb = s < thumbs.Length ? thumbs[s] : null;
                if (thumb == null) continue;
                var tex = m_Streamer != null ? m_Streamer.SiteThumbnail(site) : null;
                if (tex == m_SlotThumb[s] && (tex != null || !thumb.enabled)) continue;
                m_SlotThumb[s] = tex;
                thumb.enabled = tex != null;
                if (tex == null) continue;
                m_Block ??= new MaterialPropertyBlock();
                thumb.GetPropertyBlock(m_Block);
                m_Block.SetTexture(s_BaseMap, tex);
                m_Block.SetColor(s_BaseColor, state == ModelCardState.NotDownloaded ? new Color(0.3f, 0.3f, 0.3f, 1f)
                    : state == ModelCardState.Loading || state == ModelCardState.Queued ? new Color(0.45f, 0.45f, 0.45f, 1f) : Color.white);
                thumb.SetPropertyBlock(m_Block);
            }
            RefreshLens();
        }

        void RefreshLens()
        {
            string site = LensSite;
            var state = site != null ? switcher.StateOf(site, m_Streamer) : ModelCardState.Idle;
            LensState = state;
            string scale = site != null && m_Scales.TryGetValue(site, out var sc) ? sc : null;
            bool controllers = AirTools.Input.InputMode.Controllers;
            if (site == m_ShownLensSite && state == m_ShownLensState && scale == m_ShownScale && controllers == m_ShownControllers) return;
            m_ShownLensSite = site; m_ShownLensState = state; m_ShownScale = scale; m_ShownControllers = controllers;
            if (lensName != null) lensName.text = site != null ? NameOf(site) : "";
            if (lensDetail != null) lensDetail.text = site != null ? ModelViewLayout.LensDetail(scale, state, controllers) : "";
        }

        string NameOf(string site)
        {
            if (site == null) return "";
            if (!m_Names.TryGetValue(site, out var name)) m_Names[site] = name = ModelSites.Name(site);
            return name;
        }

        /// The liquid glass arc: the ring's shader at the wheel's size, its lens at the top, inked while the model on view
        /// is under it, glowing where the ray points.
        void UpdateGlass(float dt)
        {
            if (glass == null) return;
            m_Ink = Mathf.MoveTowards(m_Ink, LensState == ModelCardState.Current && (m_Dial == null || m_Dial.Settled) ? 1f : 0f, dt * 4f);
            var key = Vector3.up;
            if (head != null) key = (Vector3.up + (head.position - transform.position).normalized * 0.55f).normalized;
            m_GlassBlock ??= new MaterialPropertyBlock();
            glass.GetPropertyBlock(m_GlassBlock);
            m_GlassBlock.SetFloat(s_ShapeId, 0f);
            m_GlassBlock.SetFloat(s_ArcRadiusId, radius);
            m_GlassBlock.SetFloat(s_ArcHalfWidthId, halfWidth);
            m_GlassBlock.SetFloat(s_ArcHalfAngleId, (visibleHalfAngle + 3f) * Mathf.Deg2Rad);
            m_GlassBlock.SetVector(s_LensCenterId, new Vector4(0f, radius, 0f, 0f));
            m_GlassBlock.SetFloat(s_LensRadiusId, lensRadius);
            m_GlassBlock.SetVector(s_KeyLightId, key);
            m_GlassBlock.SetFloat(s_PulseId, m_Pulse);
            // The touch point in the glass's frame (the circle's centre at its origin), 1 cm in front of it.
            m_GlassBlock.SetVector(s_TouchId, new Vector4(m_TouchLocal.x, m_TouchLocal.y + radius, 0.01f, m_Touch));
            var ink = UiTheme.Current.colors.ink;
            m_GlassBlock.SetColor(s_LensInkId, new Color(ink.r, ink.g, ink.b, m_Ink * 0.6f));
            glass.SetPropertyBlock(m_GlassBlock);
        }
    }
}
