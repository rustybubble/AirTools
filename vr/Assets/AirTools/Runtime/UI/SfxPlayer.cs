using System.Collections.Generic;
using AirTools.Core;
using UnityEngine;

namespace AirTools.UI
{
    /// Controller haptic shapes (Meta's SystemHaptics vocabulary): Press, Hover, Tick, Success, Warning, Error, PressLong.
    public enum HapticPattern { None, Hover, Tick, Press, Success, Warning, Error, PressLong }

    /// One pooled, spatialised sound player for the whole app (UX W0.5): a ring of AudioSources routed through the
    /// project's spatializer (Meta XR Audio), so a pip plays *at the point on the surface*. Replaces
    /// AudioSource.PlayClipAtPoint (a GameObject per call, no HRTF). Also runs controller haptic patterns (headset only:
    /// in the Editor the Meta XR Simulator mishandles haptic traffic). Created by the scene builder with Meta's ISDK
    /// UI sounds; created on demand (synth sounds only) when a scene lacks one.
    public class SfxPlayer : MonoBehaviour
    {
        [Header("ISDK UI sounds (Meta Sound Collection terms, Quest content)")]
        public AudioClip press;
        public AudioClip rayPress;
        public AudioClip hover;
        public AudioClip release;
        [Range(0f, 1f)] public float volume = 0.6f;
        public int voices = 8;

        static SfxPlayer s_Instance;
        readonly List<AudioSource> m_Pool = new List<AudioSource>();
        int m_Next;

        struct Pulse { public float at, until, amplitude; public OVRInput.Controller controller; }
        readonly List<Pulse> m_Pulses = new List<Pulse>();
        OVRInput.Controller m_Buzzing;

        public int Played { get; private set; }

        void OnEnable() { s_Instance = this; Services.Register(this); }
        void OnDisable() { if (s_Instance == this) s_Instance = null; Services.Unregister(this); StopBuzz(); }

        /// The scene's player, or a new synth-only one (Play mode only; null in EditMode).
        public static SfxPlayer Instance
        {
            get
            {
                if (s_Instance != null || !Application.isPlaying) return s_Instance;
                var go = new GameObject("SfxPlayer (auto)");
                return s_Instance = go.AddComponent<SfxPlayer>();
            }
        }

        void EnsurePool()
        {
            if (m_Pool.Count > 0) return;
            bool spatializer = !string.IsNullOrEmpty(AudioSettings.GetSpatializerPluginName());
            for (int i = 0; i < Mathf.Max(1, voices); i++)
            {
                var go = new GameObject($"Voice{i}");
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.spatialize = spatializer;
                src.minDistance = 0.35f;
                src.maxDistance = 25f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.dopplerLevel = 0f;
                m_Pool.Add(src);
            }
        }

        /// Play a clip at a world point (round-robin voices; the oldest is reused).
        public void Play(AudioClip clip, Vector3 at, float gain = 1f, float pitch = 1f)
        {
            if (clip == null || !Application.isPlaying) return;
            EnsurePool();
            var src = m_Pool[m_Next];
            m_Next = (m_Next + 1) % m_Pool.Count;
            src.transform.position = at;
            src.pitch = pitch;
            src.clip = clip;
            src.volume = Mathf.Clamp01(volume * gain);
            src.Play();
            Played++;
        }

        // ---------------- haptics ----------------

        /// Queue a haptic pattern on a controller (Touch only; skipped in the Editor and without an OVRManager).
        public void Haptic(HapticPattern p, OVRInput.Controller controller = OVRInput.Controller.None)
        {
            if (p == HapticPattern.None || Application.isEditor || OVRManager.instance == null) return;
            if (controller == OVRInput.Controller.None) controller = OVRInput.GetActiveController();
            if ((controller & OVRInput.Controller.Touch) == 0) return;
            float now = Time.unscaledTime;
            void Add(float delay, float seconds, float amp) => m_Pulses.Add(new Pulse { at = now + delay, until = now + delay + seconds, amplitude = amp, controller = controller });
            switch (p)
            {
                case HapticPattern.Hover: Add(0f, 0.015f, 0.12f); break;
                case HapticPattern.Tick: Add(0f, 0.012f, 0.2f); break;
                case HapticPattern.Press: Add(0f, 0.03f, 0.35f); break;
                case HapticPattern.Success: Add(0f, 0.03f, 0.45f); Add(0.07f, 0.045f, 0.7f); break;
                case HapticPattern.Warning: Add(0f, 0.06f, 0.55f); break;
                case HapticPattern.Error: Add(0f, 0.04f, 0.8f); Add(0.1f, 0.04f, 0.8f); break;
                case HapticPattern.PressLong: Add(0f, 0.12f, 0.6f); break;
            }
        }

        void Update()
        {
            if (m_Pulses.Count == 0) { if (m_Buzzing != OVRInput.Controller.None) StopBuzz(); return; }
            float now = Time.unscaledTime;
            float amp = 0f;
            var controller = OVRInput.Controller.None;
            for (int i = m_Pulses.Count - 1; i >= 0; i--)
            {
                var p = m_Pulses[i];
                if (now >= p.until) { m_Pulses.RemoveAt(i); continue; }
                if (now >= p.at && p.amplitude > amp) { amp = p.amplitude; controller = p.controller; }
            }
            if (amp > 0f) { OVRInput.SetControllerVibration(0.8f, amp, controller); m_Buzzing = controller; }
            else if (m_Buzzing != OVRInput.Controller.None) StopBuzz();
        }

        void StopBuzz()
        {
            if (m_Buzzing != OVRInput.Controller.None && !Application.isEditor && OVRManager.instance != null)
                OVRInput.SetControllerVibration(0f, 0f, m_Buzzing);
            m_Buzzing = OVRInput.Controller.None;
        }
    }
}
