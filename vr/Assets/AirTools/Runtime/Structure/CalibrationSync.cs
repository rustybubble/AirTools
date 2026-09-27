using AirTools.Core;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using UnityEngine;

namespace AirTools.Structure
{
    /// Keeps everything stored in SceneRoot space on its feature when the scene's calibration changes
    /// (SceneRoot.Rescaled): tape points, levels, placed parts, and the wearer (the rig moves with the scene, so you
    /// stay where you were standing relative to the room). Saves the calibration for the site.
    public class CalibrationSync : MonoBehaviour
    {
        public SceneRoot sceneRoot;
        [Tooltip("OVRCameraRig root (moves with the scene so the wearer keeps their place).")]
        public Transform rig;

        SceneRoot m_Subscribed;

        void OnEnable() { Services.Register(this); Subscribe(); }
        void Start() => Subscribe();

        void OnDisable()
        {
            Services.Unregister(this);
            if (m_Subscribed != null) m_Subscribed.Rescaled -= OnRescaled;
            m_Subscribed = null;
        }

        void Subscribe()
        {
            var root = sceneRoot != null ? sceneRoot : Services.Get<SceneRoot>();
            if (root == null || root == m_Subscribed) return;
            if (m_Subscribed != null) m_Subscribed.Rescaled -= OnRescaled;
            root.Rescaled += OnRescaled;
            m_Subscribed = root;
        }

        public void OnRescaled(float factor)
        {
            Services.Get<MeasureTool>()?.RescaleAll(factor);
            Services.Get<LevelTool>()?.RescaleAll(factor);
            Services.Get<PartTool>()?.RescaleAll(factor);
            Services.Get<LadderTool>()?.RescaleAll(factor);   // P6/P7
            var root = m_Subscribed != null ? m_Subscribed : sceneRoot;
            // modelview: with the scene on the table (Model view) you aren't in it: the rig stays put (moving it would
            // slide the model against the real room); the tabletop re-fits the model and scales its kept spawn instead.
            bool onTable = Services.TryGet<AirTools.Scene.TabletopController>(out var table) && table.DefersSpawn;
            if (rig != null && root != null && Application.isPlaying && !onTable)
            {
                var t = root.transform;
                rig.position = t.TransformPoint(t.InverseTransformPoint(rig.position) * factor);
                if (Services.TryGet<AirTools.Input.Locomotion>(out var loco))
                    loco.SetHome(new Pose(t.TransformPoint(t.InverseTransformPoint(loco.Home.position) * factor), loco.Home.rotation));
            }
            Services.Get<SceneStreamer>()?.SaveCalibration();
        }
    }
}
