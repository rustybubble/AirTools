using AirTools.Scene;
using UnityEngine;

namespace AirTools.Core
{
    /// Scene entry point: loads the configured scene package under the SceneRoot and moves the rig to its spawn.
    /// The app starts in Passthrough (AppState's initial mode); the chest opens the world.
    [DefaultExecutionOrder(-100)]
    public class AppBootstrap : MonoBehaviour
    {
        public ScenePackage scenePackage;
        public SceneRoot sceneRoot;
        [Tooltip("OVRCameraRig root; moved to the package's recommended spawn.")]
        public Transform rig;

        void Awake()
        {
            if (sceneRoot == null || scenePackage == null)
            {
                Log.Error("AppBootstrap: sceneRoot or scenePackage not assigned");
                return;
            }
            SceneLoader.Load(scenePackage, sceneRoot, rig);
            sceneRoot.SetVisible(AppState.Mode != AppMode.Passthrough);
        }
    }
}
