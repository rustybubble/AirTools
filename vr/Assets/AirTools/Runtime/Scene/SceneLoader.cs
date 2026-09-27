using AirTools.Core;
using UnityEngine;

namespace AirTools.Scene
{
    /// Instantiates a <see cref="ScenePackage"/> under a <see cref="SceneRoot"/>: levels it (up), turns it (north),
    /// puts its surfaces on the SceneSurface layer with colliders, and moves the rig to the recommended spawn.
    public static class SceneLoader
    {
        /// Rotation that maps package coordinates to scene coordinates.
        public static Quaternion PackageToScene(ScenePackage package)
        {
            Vector3 up = package.up.sqrMagnitude > 1e-8f ? package.up.normalized : Vector3.up;
            Quaternion level = Quaternion.FromToRotation(up, Vector3.up);
            return Quaternion.AngleAxis(package.northDeg, Vector3.up) * level;
        }

        public static GameObject Load(ScenePackage package, SceneRoot root, Transform rig = null)
        {
            if (package == null || package.visualPrefab == null)
            {
                Log.Error("SceneLoader: package or its visual prefab is missing");
                return null;
            }

            if (root.Content != null)
            {
                if (Application.isPlaying) Object.Destroy(root.Content);
                else Object.DestroyImmediate(root.Content);
            }

            Quaternion toScene = PackageToScene(package);
            var content = Object.Instantiate(package.visualPrefab, root.transform);
            content.name = package.visualPrefab.name;
            content.transform.localPosition = Vector3.zero;
            content.transform.localRotation = toScene;
            content.transform.localScale = Vector3.one;

            if (package.collisionPrefab != null)
            {
                var collision = Object.Instantiate(package.collisionPrefab, content.transform);
                collision.name = "Collision";
                PrepareSurfaces(collision, visible: false);
            }
            else
            {
                PrepareSurfaces(content, visible: true);
            }

            root.SetContent(package, content);

            if (rig != null) PlaceRig(package, root.transform, rig);
            Log.Info($"SceneLoader: loaded '{package.siteName}'");
            return content;
        }

        /// Every mesh gets a collider on SceneSurface; invisible collision meshes lose their renderers.
        public static void PrepareSurfaces(GameObject go, bool visible)
        {
            int layer = SceneLayers.SceneSurface;
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = layer;
                var filter = t.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                if (t.GetComponent<Collider>() == null)
                    t.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
                if (!visible && t.TryGetComponent<Renderer>(out var r)) r.enabled = false;
            }
        }

        public static void PlaceRig(ScenePackage package, Transform sceneRoot, Transform rig)
        {
            Quaternion toScene = PackageToScene(package);
            rig.position = sceneRoot.TransformPoint(toScene * package.spawnPosition);
            float yaw = package.spawnYawDeg + package.northDeg + sceneRoot.eulerAngles.y;
            rig.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        /// modelview: PlaceRig's spawn in SceneRoot space (the feet and the heading about up), for Model view, where the
        /// rig stays put and the tabletop lands you there when you step in (TabletopController.SetSpawn).
        public static bool SpawnLocal(ScenePackage package, out Vector3 feetLocal, out float yawLocalDeg)
        {
            feetLocal = default;
            yawLocalDeg = 0f;
            if (package == null) return false;
            feetLocal = PackageToScene(package) * package.spawnPosition;
            yawLocalDeg = package.spawnYawDeg + package.northDeg;
            return true;
        }
    }
}
