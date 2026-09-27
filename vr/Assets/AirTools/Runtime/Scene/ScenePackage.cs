using System;
using UnityEngine;

namespace AirTools.Scene
{
    /// One capture site (contract §1 of the implementation plan, adapted: mesh-only visuals per the M0 decision).
    /// Coordinates are the package's own frame; <see cref="SceneLoader"/> applies <see cref="up"/> and <see cref="northDeg"/>.
    [CreateAssetMenu(menuName = "AirTools/Scene Package", fileName = "ScenePackage")]
    public class ScenePackage : ScriptableObject
    {
        public string siteName = "site";

        [Tooltip("Textured mesh the user sees (mesh.glb imported as a prefab).")]
        public GameObject visualPrefab;

        [Tooltip("Optional collision mesh. If empty, the visual meshes double as the snap surface.")]
        public GameObject collisionPrefab;

        [Tooltip("The package's up vector, in package coordinates.")]
        public Vector3 up = Vector3.up;

        [Tooltip("Yaw applied after levelling, in degrees, so that package north ends up on world +Z.")]
        public float northDeg;

        [Tooltip("Where the rig starts, in package coordinates (floor level).")]
        public Vector3 spawnPosition;

        [Tooltip("Rig yaw at spawn, degrees about up. 180 = facing -Z.")]
        public float spawnYawDeg;

        [Tooltip("Known scale error of the capture, metres (0 for synthetic scenes).")]
        public double scaleResidualM;

        public SceneCameraInfo[] cameras = Array.Empty<SceneCameraInfo>();

        [Tooltip("cameras.json as shipped with the package (kept for export / debugging).")]
        public TextAsset camerasJson;
    }

    /// A source photo position, used by the notebook to attach evidence to readings.
    [Serializable]
    public struct SceneCameraInfo
    {
        public int id;
        public Vector3 position;
        public Quaternion rotation;
        public float verticalFovDeg;
        public float aspect;
        public Texture2D thumbnail;
        [Tooltip("Runtime packages: the camera's id in cameras.r<rev>.json (also the /scene/ask frame id).")]
        public string key;
        [Tooltip("Runtime packages: thumbnail path inside the package (thumbs/NNNN.jpg), loaded on demand.")]
        public string thumbPath;
    }
}
