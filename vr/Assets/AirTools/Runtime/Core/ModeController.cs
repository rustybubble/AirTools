using AirTools.Scene;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Core
{
    /// Makes the visuals follow <see cref="AppState"/> with a ≤ 1 s transition: fade out → swap → hold → fade in.
    /// Passthrough: passthrough layer on, scene hidden, cameras clear to transparent.
    /// World/Tabletop: passthrough layer hidden, scene shown, cameras clear to the skybox.
    /// Driven by <see cref="Tick"/> (from Update) so tests can step it deterministically.
    /// presence.md S1: with a <see cref="TransitionDirector"/> and Reduce motion off, the changes it handles (pour,
    /// grow, shrink, scan) are animated by it instead of the fade; Reduce motion keeps this fade path unchanged. With a
    /// <see cref="SkyDome"/>, World shows the dome as its sky (cameras clear to black behind it) instead of the skybox.
    public class ModeController : MonoBehaviour
    {
        enum Phase { Idle, FadingOut, Holding, FadingIn }

        [Tooltip("Passthrough underlay from the Passthrough building block (may be empty in tests).")]
        public OVRPassthroughLayer passthroughLayer;
        public SceneRoot sceneRoot;
        [Tooltip("Cameras whose clear mode follows the app mode (the rig's eye cameras).")]
        public Camera[] cameras;
        public FadeOverlay fade;
        [Tooltip("Animated transitions (grow in / shrink out / pour). Optional: without it every change fades.")]
        public TransitionDirector director;
        [Tooltip("The World sky (replaces the skybox, so a transition can close it over the room). Optional.")]
        public SkyDome skyDome;

        [Header("Timing (s), total ≤ 1.0")]
        public float fadeOutSeconds = 0.35f;
        public float holdSeconds = 0.2f;
        public float fadeInSeconds = 0.35f;

        /// The mode the visuals currently show (switches at the midpoint of a transition).
        public AppMode VisualMode { get; private set; } = AppState.InitialMode;
        public bool IsTransitioning => m_Phase != Phase.Idle || (director != null && director.Playing);
        public float LastTransitionSeconds { get; private set; }
        public float TotalSeconds => fadeOutSeconds + holdSeconds + fadeInSeconds;
        public float FadeAlpha => fade != null ? fade.Alpha : m_Alpha;

        Phase m_Phase = Phase.Idle;
        float m_PhaseTime, m_Elapsed, m_Alpha;
        bool m_Subscribed;

        void OnEnable()
        {
            Services.Register(this);
            Subscribe();
            ApplyVisuals(AppState.Mode);
            SetAlpha(0f);
        }

        void OnDisable()
        {
            Unsubscribe();
            Services.Unregister(this);
            m_Phase = Phase.Idle;
        }

        public void Subscribe()
        {
            if (m_Subscribed) return;
            AppState.Changed += OnModeChanged;
            m_Subscribed = true;
        }

        public void Unsubscribe()
        {
            if (!m_Subscribed) return;
            AppState.Changed -= OnModeChanged;
            m_Subscribed = false;
        }

        void Update() => Tick(Time.unscaledDeltaTime);

        void OnModeChanged(AppMode previous, AppMode next)
        {
            // Mid-transition changes are picked up at the swap (or by a follow-up transition).
            if (m_Phase == Phase.Idle && !(director != null && director.Playing)) StartTransition(VisualMode, next);
        }

        /// The director's animation when it handles this change and Reduce motion is off; the fade otherwise.
        void StartTransition(AppMode from, AppMode to)
        {
            if (director != null && director.WouldPlay(from, to))
            {
                m_Elapsed = 0f;
                director.Play(from, to);
                return;
            }
            Begin();
        }

        /// Called by the director once its transition has applied the final visuals.
        public void DirectorFinished(TransitionDirector d)
        {
            LastTransitionSeconds = d.LastSeconds;
            // Same check line as the fade (tools/demo/hcheck.py m1.fade reads it); the kind is in the director's log line.
            Log.Check("M1.mode.transition", LastTransitionSeconds <= TransitionMath.Duration(d.LastKind) + 0.1f,
                $"to={VisualMode} seconds={LastTransitionSeconds:F2} budget={TransitionMath.Duration(d.LastKind):F2}");
            if (VisualMode != AppState.Mode) StartTransition(VisualMode, AppState.Mode);   // mode changed again meanwhile
        }

        void Begin()
        {
            m_Phase = Phase.FadingOut;
            m_PhaseTime = 0f;
            m_Elapsed = 0f;
        }

        public void Tick(float dt)
        {
            if (director != null && director.Playing) { director.Tick(dt); return; }
            if (m_Phase == Phase.Idle) return;
            m_PhaseTime += dt;
            m_Elapsed += dt;
            switch (m_Phase)
            {
                case Phase.FadingOut:
                    SetAlpha(Progress(fadeOutSeconds));
                    if (m_PhaseTime >= fadeOutSeconds) { ApplyVisuals(AppState.Mode); Next(Phase.Holding); }
                    break;
                case Phase.Holding:
                    if (m_PhaseTime >= holdSeconds) Next(Phase.FadingIn);
                    break;
                case Phase.FadingIn:
                    SetAlpha(1f - Progress(fadeInSeconds));
                    if (m_PhaseTime >= fadeInSeconds) Finish();
                    break;
            }
        }

        float Progress(float seconds) => seconds > 0f ? Mathf.Clamp01(m_PhaseTime / seconds) : 1f;

        void Next(Phase phase)
        {
            m_Phase = phase;
            m_PhaseTime = 0f;
        }

        void Finish()
        {
            SetAlpha(0f);
            m_Phase = Phase.Idle;
            LastTransitionSeconds = m_Elapsed;
            Log.Check("M1.mode.transition", LastTransitionSeconds <= TotalSeconds + 0.1f,
                $"to={VisualMode} seconds={LastTransitionSeconds:F2} budget={TotalSeconds:F2}");
            if (VisualMode != AppState.Mode) StartTransition(VisualMode, AppState.Mode); // mode changed again during the fade
        }

        void SetAlpha(float a)
        {
            m_Alpha = a;
            if (fade != null) fade.Alpha = a;
        }

        /// Swap passthrough/scene/camera state immediately (no fade).
        /// Raised at the visual swap (mid-fade, behind black): listeners change the scene then (e.g. tabletop scale).
        public event System.Action<AppMode> Swapped;

        /// raiseSwapped = false: the director has already put the scene where it belongs (no TabletopController.Apply).
        public void ApplyVisuals(AppMode mode, bool raiseSwapped = true)
        {
            // Tabletop shows the miniature scene in your room: passthrough stays on around it.
            bool world = mode == AppMode.World;
            bool sceneShown = mode != AppMode.Passthrough;
            if (passthroughLayer != null) passthroughLayer.hidden = world;
            if (sceneRoot != null) sceneRoot.SetVisible(sceneShown);
            if (cameras != null)
            {
                foreach (var cam in cameras)
                {
                    if (cam == null) continue;
                    if (world && skyDome == null)
                    {
                        cam.clearFlags = CameraClearFlags.Skybox;
                    }
                    else
                    {
                        // World with the dome: opaque black behind it (the dome covers every direction).
                        cam.clearFlags = CameraClearFlags.SolidColor;
                        cam.backgroundColor = new Color(0f, 0f, 0f, world ? 1f : 0f);
                    }
                }
            }
            if (skyDome != null)
            {
                skyDome.SetVisible(world);
                if (world) skyDome.SetFull();
            }
            VisualMode = mode;
            if (raiseSwapped) Swapped?.Invoke(mode);
        }

        /// During a director transition: the room (passthrough, cameras clear to transparent) and the scene both on,
        /// the sky dome on if asked; what covers the room is decided by the reveal (RevealField / the dome's cone).
        public void ShowTransitionVisuals(bool dome)
        {
            if (passthroughLayer != null) passthroughLayer.hidden = false;
            if (sceneRoot != null) sceneRoot.SetVisible(true);
            if (cameras != null)
            {
                foreach (var cam in cameras)
                {
                    if (cam == null) continue;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                }
            }
            if (skyDome != null) skyDome.SetVisible(dome);
        }
    }
}
