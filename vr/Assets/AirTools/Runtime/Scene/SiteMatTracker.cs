using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using AirTools.Core;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace AirTools.Scene
{
    /// The printed site mat's QR code → the mat pose (presence.md S1/P2). On the headset it starts MRUK QR-code
    /// tracking (world lock off, no scene load), parses `airtools:mat:v1;…` payloads, and locks the level mat frame
    /// once its pose has held still for 1 s; then QR tracking is switched off (static anchor, no per-frame cost). It
    /// also starts MRUK's environment raycast (OpenXR provider, no Depth API) and hands it to RealTable as the second
    /// rung of the fallback chain. Both only on an Android player and only when supported: in the Editor, the
    /// Simulator or a device without support the chain falls through to the assumed table height.
    /// Deviation from the spec: MRUK and the raycast manager are created here at runtime (device only) rather than
    /// baked into Main.unity, so the Editor / Simulator never start MRUK.
    public class SiteMatTracker : MonoBehaviour
    {
        public RealTable table;
        [Tooltip("Start MRUK QR tracking and environment raycasts on the headset.")]
        public bool enableOnDevice = true;
        [Tooltip("The QR pose must hold still this long before the mat locks (s) …")]
        public float lockSeconds = 1f;
        [Tooltip("… within these limits (m, degrees).")]
        public float stableMetres = 0.01f, stableDegrees = 2f;
        public SiteMatSpec defaultSpec = new SiteMatSpec();

        public bool Locked { get; private set; }
        public Pose MatPose { get; private set; }
        public SiteMatSpec Spec { get; private set; }
        public string Status { get; private set; } = "off";
        public string LastPayload { get; private set; } = "";
        public bool DeviceStarted { get; private set; }
        public bool RaycastAvailable { get; private set; }
        public float LockedAt { get; private set; } = -1f;

        Pose m_Candidate;
        float m_CandidateSince = -1f;
        bool m_HasCandidate, m_LoggedAxes;
        MRUK m_Mruk;
        Meta.XR.EnvironmentRaycastManager m_Raycaster;
        readonly List<MRUKTrackable> m_Trackables = new List<MRUKTrackable>();

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);

        void Start()
        {
            Spec ??= defaultSpec;
            if (!enableOnDevice || Application.isEditor || Application.platform != RuntimePlatform.Android)
            {
                Status = "off (not on a headset): table from the fallback chain";
                return;
            }
            try { StartDevice(); }
            catch (Exception e) { Status = $"error: {e.Message}"; Log.Warn($"Site mat: could not start MRUK ({e.GetType().Name}: {e.Message})"); }
        }

        void Update()
        {
            if (m_Mruk != null && !Locked) PollDevice(Time.unscaledTime);
        }

        public bool TryGetMatPose(out Pose pose)
        {
            pose = MatPose;
            return Locked;
        }

        /// One QR sighting (MRUK trackable, or tests / harness): parses the payload and locks the mat once the level
        /// frame has held within stableMetres / stableDegrees for lockSeconds. False for other QR codes.
        public bool Sample(Pose qrPose, string payload, float now)
        {
            if (Locked) return true;
            if (!SiteMatSpec.TryParse(payload, out var spec)) return false;
            if (!SiteMatSpec.MatFrame(qrPose, out var frame)) { Status = "a mat QR, but not lying flat"; return false; }
            Spec = spec;
            LastPayload = payload;
            if (!m_HasCandidate || Vector3.Distance(frame.position, m_Candidate.position) > stableMetres
                || Quaternion.Angle(frame.rotation, m_Candidate.rotation) > stableDegrees)
            {
                m_Candidate = frame;
                m_CandidateSince = now;
                m_HasCandidate = true;
                Status = "mat seen, hold still";
                return false;
            }
            if (now - m_CandidateSince >= lockSeconds) Lock(frame, spec, now);
            return Locked;
        }

        public void Lock(Pose frame, SiteMatSpec spec, float now = -1f)
        {
            Locked = true;
            MatPose = frame;
            Spec = spec ?? defaultSpec;
            LockedAt = now >= 0f ? now : Time.unscaledTime;
            Status = "locked";
            Log.Info($"Site mat locked at {frame.position:F3} yaw {frame.rotation.eulerAngles.y:0.0}° ({LastPayload})");
            if (m_Mruk != null) SetQrTracking(false);
        }

        /// Forget the mat (moved it, or another table): tracking restarts on the device.
        public void Unlock()
        {
            Locked = false;
            m_HasCandidate = false;
            Status = m_Mruk != null ? "searching" : "off";
            if (m_Mruk != null) SetQrTracking(true);
        }

        // ---------------- device only (MRUK) ----------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        void StartDevice()
        {
            var go = new GameObject("MRUK");
            go.SetActive(false);
            go.transform.SetParent(transform, false);
            var mruk = go.AddComponent<MRUK>();
            mruk.EnableWorldLock = false;   // it would take over the TrackingSpace and fight Locomotion / Home / spawn
            mruk.SceneSettings ??= new MRUK.MRUKSettings();
            mruk.SceneSettings.DataSource = MRUK.SceneDataSource.Device;
            mruk.SceneSettings.LoadSceneOnStartup = false;
            go.SetActive(true);
            m_Mruk = mruk;
            DeviceStarted = true;
            bool qr = mruk.QRCodeTrackingSupported;
            if (qr) SetQrTracking(true);
            Status = qr ? "searching" : "QR tracking not supported: table from the fallback chain";

            if (Meta.XR.EnvironmentRaycastManager.IsSupported)
            {
                var rgo = new GameObject("EnvironmentRaycast");
                rgo.transform.SetParent(transform, false);
                m_Raycaster = rgo.AddComponent<Meta.XR.EnvironmentRaycastManager>();
                RaycastAvailable = true;
                if (table != null) table.raycast = Probe;
            }
            Log.Info($"Site mat: MRUK started (QR {(qr ? "on" : "unsupported")}, environment raycast {(RaycastAvailable ? "on" : "unsupported")})");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void SetQrTracking(bool on)
        {
            if (m_Mruk == null) return;
            m_Mruk.SceneSettings.TrackerConfiguration = new OVRAnchor.TrackerConfiguration { QRCodeTrackingEnabled = on };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void PollDevice(float now)
        {
            m_Mruk.GetTrackables(m_Trackables);
            foreach (var t in m_Trackables)
            {
                if (t == null || t.TrackableType != OVRAnchor.TrackableType.QRCode || !t.IsTracked) continue;
                var tr = t.transform;
                if (!m_LoggedAxes && SiteMatSpec.TryParse(t.MarkerPayloadString, out _))
                {
                    // The QR anchor's axis convention isn't documented: log it once for the first headset run.
                    m_LoggedAxes = true;
                    Log.Info($"Site mat QR axes: pos={tr.position:F3} right={tr.right:F2} up={tr.up:F2} forward={tr.forward:F2} payload=\"{t.MarkerPayloadString}\"");
                }
                if (Sample(new Pose(tr.position, tr.rotation), t.MarkerPayloadString, now)) break;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        bool Probe(Ray ray, float maxDistance, out Vector3 point, out Vector3 normal)
        {
            point = normal = default;
            if (m_Raycaster == null || !m_Raycaster.isActiveAndEnabled) return false;
            if (!m_Raycaster.Raycast(ray, out var hit, maxDistance)) return false;
            point = hit.point;
            normal = hit.normal;
            return true;
        }
    }
}
