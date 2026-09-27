using System;
using AirTools.Scene;
using AirTools.UI;
using UnityEngine;

namespace AirTools.Core
{
    /// Plays the animated mode changes (presence.md S1) instead of the black fade, when Reduce motion is off:
    /// - Pour in / out (Passthrough ⇄ World, the Enter world pill): the world appears inside a sphere growing from
    ///   1.2 m ahead of you (0 → 90 m in 1.2 s) with an accent scan band, then the sky closes over the room; exit is
    ///   the mirror.
    /// - Grow in (Tabletop → World, "Step in"): over 1.4 s the 1:50 model grows log-linearly to 1:1, the point under
    ///   your feet gliding from its spot on the model to your feet and the yaw turning to the world's; the room stays
    ///   visible for the first 60 %, then the sky closes over it from the building outward.
    /// - Shrink out (World → Tabletop): the sky opens from above (0.4 s), then the building shrinks back onto the
    ///   table (1.2 s) and lands with a soft thud.
    /// - Scan in / out (Passthrough ⇄ Tabletop): the model appears on (leaves) the table inside a growing sphere.
    /// It never shows a black frame: the passthrough underlay stays on during the effect and the scene / sky dome
    /// cover it where revealed (alpha 1); at the end ModeController.ApplyVisuals switches to the final state, which
    /// looks identical. ModeController delegates to it and ticks it (deterministic in EditMode tests).
    public class TransitionDirector : MonoBehaviour
    {
        public ModeController mode;
        public SceneRoot sceneRoot;
        public TabletopController tabletop;
        public SkyDome sky;
        public Transform rig;
        public Transform head;
        [Tooltip("Scan band around the model on the table (m).")]
        public float tableBand = 0.004f;
        public float thudVolume = 0.5f;

        public TransitionKind Kind { get; private set; }
        public TransitionPhase Phase { get; private set; }
        public AppMode From { get; private set; }
        public AppMode To { get; private set; }
        public float Elapsed { get; private set; }
        public float Duration { get; private set; }
        public bool Playing => Kind != TransitionKind.None;
        /// 0..1 through the current transition (1 when idle).
        public float Progress => Playing && Duration > 0f ? Mathf.Clamp01(Elapsed / Duration) : 1f;
        public TransitionKind LastKind { get; private set; }
        public float LastSeconds { get; private set; }
        public int Played { get; private set; }

        /// (kind, from, to) when a transition starts / ends. Finished is raised after the final visuals are applied.
        public event Action<TransitionKind, AppMode, AppMode> Started, Finished;

        // Per-transition state.
        Pose m_TablePose, m_WorldPose;
        float m_TableScale = 0.02f, m_WorldScale = 1f;
        Vector3 m_Feet, m_AnchorLocal, m_Centre, m_SkyDir;
        float m_ModelRadius;
        AudioSource m_Audio;
        static AudioClip s_Thud;

        void OnEnable() => Services.Register(this);

        void OnDisable()
        {
            Services.Unregister(this);
            if (Playing) RevealField.Clear();
        }

        public static TransitionKind KindFor(AppMode from, AppMode to) => (from, to) switch
        {
            (AppMode.Passthrough, AppMode.World) => TransitionKind.PourIn,
            (AppMode.World, AppMode.Passthrough) => TransitionKind.PourOut,
            (AppMode.Tabletop, AppMode.World) => TransitionKind.GrowIn,
            (AppMode.World, AppMode.Tabletop) => TransitionKind.ShrinkOut,
            (AppMode.Passthrough, AppMode.Tabletop) => TransitionKind.Materialize,
            (AppMode.Tabletop, AppMode.Passthrough) => TransitionKind.Dematerialize,
            _ => TransitionKind.None,
        };

        /// Can this change be animated (a scene is loaded; tabletop changes need the TabletopController)?
        public bool Handles(AppMode from, AppMode to)
        {
            var kind = KindFor(from, to);
            if (kind == TransitionKind.None || mode == null || sceneRoot == null || sceneRoot.Content == null) return false;
            bool needsTable = kind != TransitionKind.PourIn && kind != TransitionKind.PourOut;
            return !needsTable || tabletop != null;
        }

        /// Will ModeController hand this change to the director (not Reduce motion, enabled, handled)?
        public bool WouldPlay(AppMode from, AppMode to) => enabled && !UiSettings.ReducedMotion && Handles(from, to);

        /// Start the transition from → to (the caller checked Handles). The visuals keep showing `from` in
        /// ModeController.VisualMode until the end.
        public void Play(AppMode from, AppMode to)
        {
            Kind = KindFor(from, to);
            From = from; To = to;
            Elapsed = 0f;
            Duration = TransitionMath.Duration(Kind);
            var h = head != null ? head : rig;
            var eye = h != null ? h.position : Vector3.zero;
            var fwd = Vector3.ProjectOnPlane(h != null ? h.forward : Vector3.forward, Vector3.up);
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            var root = sceneRoot.transform;
            switch (Kind)
            {
                case TransitionKind.PourIn:
                case TransitionKind.PourOut:
                    m_Centre = eye + fwd * TransitionMath.PourAhead;
                    m_SkyDir = fwd;
                    mode.ShowTransitionVisuals(true);
                    break;
                case TransitionKind.GrowIn:
                    m_TablePose = new Pose(root.position, root.rotation);
                    m_TableScale = root.localScale.x;
                    tabletop.WorldPose(out m_WorldPose, out m_WorldScale);
                    m_Feet = tabletop.FeetWorld();
                    m_AnchorLocal = TransitionMath.AnchorLocal(m_WorldPose, m_WorldScale, m_Feet);
                    m_SkyDir = BuildingDirection(eye, fwd);
                    mode.ShowTransitionVisuals(true);
                    break;
                case TransitionKind.ShrinkOut:
                    tabletop.CaptureWorld();
                    tabletop.WorldPose(out m_WorldPose, out m_WorldScale);
                    tabletop.TablePose(out m_TablePose, out m_TableScale);
                    m_Feet = tabletop.FeetWorld();
                    m_AnchorLocal = TransitionMath.AnchorLocal(m_WorldPose, m_WorldScale, m_Feet);
                    m_SkyDir = Vector3.down;   // the hole opens at the zenith: the visible sky shrinks toward the nadir
                    mode.ShowTransitionVisuals(true);
                    break;
                case TransitionKind.Materialize:
                    mode.ShowTransitionVisuals(false);   // the content must be active to measure it
                    tabletop.Apply(true);
                    ModelSphere(out m_Centre, out m_ModelRadius);
                    break;
                case TransitionKind.Dematerialize:
                    mode.ShowTransitionVisuals(false);
                    ModelSphere(out m_Centre, out m_ModelRadius);
                    break;
            }
            Log.Info($"Transition {Kind} ({from} → {to}) {Duration:0.0} s");
            Started?.Invoke(Kind, from, to);
            Tick(0f);
        }

        public void Tick(float dt)
        {
            if (!Playing) return;
            Elapsed += Mathf.Max(0f, dt);
            float u = Duration > 0f ? Mathf.Clamp01(Elapsed / Duration) : 1f;
            var band = UiTheme.Current.colors.ink;   // D3: the scan band is the tape-yellow ink (the measured world arriving)
            switch (Kind)
            {
                case TransitionKind.PourIn:
                {
                    float r = TransitionMath.PourRadius(u);
                    RevealField.SetSphere(m_Centre, r, TransitionMath.PourBand(r), band);
                    SetSky(m_SkyDir, TransitionMath.SkyAngle(u, TransitionMath.PourSkyStart));
                    Phase = u < TransitionMath.PourSkyStart ? TransitionPhase.Pour : TransitionPhase.Sky;
                    break;
                }
                case TransitionKind.PourOut:
                {
                    float v = 1f - u;
                    float r = TransitionMath.PourRadius(v);
                    RevealField.SetSphere(m_Centre, r, TransitionMath.PourBand(r), band);
                    SetSky(m_SkyDir, TransitionMath.SkyAngle(v, TransitionMath.PourSkyStart));
                    Phase = v >= TransitionMath.PourSkyStart ? TransitionPhase.Sky : TransitionPhase.Pour;
                    break;
                }
                case TransitionKind.GrowIn:
                    PoseAt(TransitionMath.Ease(u));
                    SetSky(m_SkyDir, TransitionMath.SkyAngle(u, TransitionMath.GrowSkyStart));
                    Phase = u < TransitionMath.GrowSkyStart ? TransitionPhase.Grow : TransitionPhase.Sky;
                    break;
                case TransitionKind.ShrinkOut:
                    if (Elapsed < TransitionMath.ShrinkSkySeconds)
                    {
                        float open = TransitionMath.Smooth(0f, TransitionMath.ShrinkSkySeconds, Elapsed);
                        SetSky(m_SkyDir, Mathf.PI * (1f - open));
                        PoseAt(1f);
                        Phase = TransitionPhase.Sky;
                    }
                    else
                    {
                        float s = Mathf.Clamp01((Elapsed - TransitionMath.ShrinkSkySeconds) / TransitionMath.ShrinkScaleSeconds);
                        SetSky(m_SkyDir, 0f);
                        PoseAt(1f - TransitionMath.Ease(s));
                        Phase = TransitionPhase.Shrink;
                    }
                    break;
                case TransitionKind.Materialize:
                    RevealField.SetSphere(m_Centre, TransitionMath.ScanRadius(u, m_ModelRadius), tableBand, band);
                    Phase = TransitionPhase.Scan;
                    break;
                case TransitionKind.Dematerialize:
                    RevealField.SetSphere(m_Centre, TransitionMath.ScanRadius(1f - u, m_ModelRadius), tableBand, band);
                    Phase = TransitionPhase.Scan;
                    break;
            }
            if (Elapsed >= Duration) Finish();
        }

        void SetSky(Vector3 dir, float angle)
        {
            if (sky != null) sky.SetReveal(dir, angle);
            else RevealField.SetSky(dir, angle, 0.06f);
        }

        /// SceneRoot at eased progress e between the table (0) and full size (1).
        void PoseAt(float e)
        {
            var p = TransitionMath.GrowPose(e, m_TablePose, m_TableScale, m_WorldPose, m_WorldScale, m_AnchorLocal, m_Feet, out float s);
            var t = sceneRoot.transform;
            t.SetPositionAndRotation(p.position, p.rotation);
            t.localScale = Vector3.one * s;
        }

        void Finish()
        {
            var kind = Kind;
            var t = sceneRoot.transform;
            RevealField.Clear();
            switch (kind)
            {
                case TransitionKind.GrowIn:
                    t.SetPositionAndRotation(m_WorldPose.position, m_WorldPose.rotation);
                    t.localScale = Vector3.one * m_WorldScale;
                    tabletop.Commit(false);
                    break;
                case TransitionKind.ShrinkOut:
                    t.SetPositionAndRotation(m_TablePose.position, m_TablePose.rotation);
                    t.localScale = Vector3.one * m_TableScale;
                    tabletop.Commit(true);
                    Thud(m_TablePose.position);
                    break;
                case TransitionKind.Dematerialize:
                    tabletop.Apply(false);
                    break;
            }
            // Pour raises Swapped once (TabletopController re-applies the pose it already has); the others placed the
            // scene themselves.
            bool pour = kind == TransitionKind.PourIn || kind == TransitionKind.PourOut;
            mode.ApplyVisuals(To, raiseSwapped: pour);
            LastKind = kind;
            LastSeconds = Elapsed;
            Played++;
            Kind = TransitionKind.None;
            Phase = TransitionPhase.Idle;
            Log.Info($"Transition {kind} done in {LastSeconds:0.00} s → {To}");
            mode.DirectorFinished(this);
            Finished?.Invoke(kind, From, To);
        }

        /// Jump to the end of the running transition (tests, or a new change that can't wait).
        public void Complete()
        {
            if (Playing) Tick(Duration - Elapsed + 1e-4f);
        }

        /// Where the building is from you once you're inside (the sky closes from there outward): toward the model's
        /// footprint at full size, raised a little.
        Vector3 BuildingDirection(Vector3 eye, Vector3 fallback)
        {
            if (tabletop == null) return fallback;
            var c = m_WorldPose.position + m_WorldPose.rotation * (tabletop.SceneCentreLocal() * m_WorldScale);
            var d = Vector3.ProjectOnPlane(c - eye, Vector3.up);
            if (d.sqrMagnitude < 0.25f) d = fallback;
            return (d.normalized + Vector3.up * 0.3f).normalized;
        }

        /// The model's centre and radius as it sits on the table (for the scan-in / out sphere).
        void ModelSphere(out Vector3 centre, out float radius)
        {
            var root = sceneRoot.transform;
            var b = tabletop.FootprintLocal(out bool any);
            if (!any) b = new Bounds(Vector3.zero, Vector3.one * 10f);
            centre = root.TransformPoint(new Vector3(b.center.x, 0f, b.center.z));
            // Reach every renderer (the ground plate included) so nothing pops in when the reveal switches off.
            radius = 0.02f;
            var content = sceneRoot.Content != null ? sceneRoot.Content.transform : root;
            foreach (var r in content.GetComponentsInChildren<Renderer>())
            {
                var rb = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(i % 2 == 0 ? rb.min.x : rb.max.x, (i / 2) % 2 == 0 ? rb.min.y : rb.max.y, i < 4 ? rb.min.z : rb.max.z);
                    radius = Mathf.Max(radius, Vector3.Distance(corner, centre));
                }
            }
            radius += tableBand;
        }

        void Thud(Vector3 at)
        {
            if (!Application.isPlaying) return;
            if (s_Thud == null) s_Thud = MakeThud();
            if (m_Audio == null)
            {
                m_Audio = gameObject.AddComponent<AudioSource>();
                m_Audio.playOnAwake = false;
                m_Audio.spatialBlend = 1f;
                m_Audio.minDistance = 0.4f;
            }
            m_Audio.transform.position = transform.position;
            m_Audio.volume = thudVolume;
            m_Audio.PlayOneShot(s_Thud);
        }

        /// A soft landing: 90 ms of a low (70 → 45 Hz) sine with a quick attack and exponential decay.
        static AudioClip MakeThud()
        {
            const int rate = 44100;
            int n = rate * 9 / 100;
            var data = new float[n];
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float f = Mathf.Lerp(70f, 45f, t / 0.09f);
                phase += 2f * Mathf.PI * f / rate;
                float env = Mathf.Clamp01(t / 0.004f) * Mathf.Exp(-t / 0.028f);
                data[i] = 0.8f * env * Mathf.Sin(phase);
            }
            var clip = AudioClip.Create("Thud", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// One line for the harness: kind, phase, progress, the model's scale.
        public string Status()
        {
            float s = sceneRoot != null ? sceneRoot.transform.lossyScale.x : 0f;
            return Playing
                ? $"kind={Kind} phase={Phase} progress={Progress:0.00} t={Elapsed:0.00}/{Duration:0.00}s scale={s:0.0000} sky={RevealField.SkyAngle * Mathf.Rad2Deg:0}° reveal={(RevealField.SphereOn ? RevealField.Radius.ToString("0.00") + " m" : "off")}"
                : $"kind=None phase=Idle last={LastKind} in {LastSeconds:0.00}s played={Played} scale={s:0.0000}";
        }
    }
}
