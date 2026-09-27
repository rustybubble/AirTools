using System.Collections.Generic;
using System.IO;
using System.Text;
using AirTools.Scene;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Editor
{
    /// AirTools ▸ Build Synthetic Facade (SPEC §4): procedural ground-truth building → Fixtures/SyntheticFacade/
    /// (mesh prefab, materials, cameras.json, thumbnails, ScenePackage). Regenerate any time; every number is exact.
    public static class SyntheticFacadeBuilder
    {
        public const string OutputFolder = "Assets/AirTools/Fixtures/SyntheticFacade";
        public const string PackagePath = OutputFolder + "/SyntheticFacade.asset";
        const string PrefabPath = OutputFolder + "/Facade.prefab";
        const string MeshesPath = OutputFolder + "/FacadeMeshes.asset";
        public const int ThumbWidth = 320, ThumbHeight = 240;
        const float CameraFovDeg = 60f;

        public class Materials
        {
            public Material brick, glass, sill, fascia, gutter, door, ledge, ground;
        }

        [MenuItem("AirTools/Build Synthetic Facade")]
        public static void BuildMenu() => Build();

        /// Full rebuild of the fixture folder. Returns the ScenePackage.
        public static ScenePackage Build()
        {
            Directory.CreateDirectory(OutputFolder);
            Directory.CreateDirectory(OutputFolder + "/thumbs");
            var mats = CreateMaterials();

            var meshes = new List<Mesh>();
            var root = CreateHierarchy(mats, meshes);
            SaveMeshes(meshes);
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);

            var cameras = CreateCameras();
            RenderThumbnails(prefab, cameras);
            var json = WriteCamerasJson(cameras);

            var package = AssetDatabase.LoadAssetAtPath<ScenePackage>(PackagePath);
            if (package == null)
            {
                package = ScriptableObject.CreateInstance<ScenePackage>();
                AssetDatabase.CreateAsset(package, PackagePath);
            }
            package.siteName = "synthetic-facade";
            package.visualPrefab = prefab;
            package.collisionPrefab = null;
            package.up = Vector3.up;
            package.northDeg = 0f;
            package.spawnPosition = S.SpawnPosition;
            package.spawnYawDeg = S.SpawnYawDeg;
            package.scaleResidualM = 0;
            package.camerasJson = json;
            for (int i = 0; i < cameras.Length; i++)
                cameras[i].thumbnail = AssetDatabase.LoadAssetAtPath<Texture2D>(ThumbPath(i));
            package.cameras = cameras;
            EditorUtility.SetDirty(package);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AirTools] Synthetic facade built: {PackagePath} ({cameras.Length} cameras)");
            return package;
        }

        // ---------- geometry ----------

        /// Builds the facade hierarchy in the open scene. Used by Build() and by the EditMode ground-truth tests.
        /// Every part has an exact BoxCollider on SceneSurface. Pass meshes = null to skip collecting generated meshes.
        public static GameObject CreateHierarchy(Materials mats = null, List<Mesh> meshes = null)
        {
            mats ??= new Materials();
            var root = new GameObject("Facade");
            float halfW = S.WallWidth / 2f, halfWin = S.WindowWidth / 2f, t = S.WallThickness;
            float winTop = S.SillHeight + S.WindowHeight;
            float sillBottom = S.SillHeight - S.SillSize.y;

            var wall = Group("Wall", root.transform);
            // Four pieces around the opening; front face at z = 0, back at z = -t.
            Box("Left", wall, MinMax(new Vector3(-halfW, 0, -t), new Vector3(-halfWin, S.WallHeight, 0)), mats.brick, meshes);
            Box("Right", wall, MinMax(new Vector3(halfWin, 0, -t), new Vector3(halfW, S.WallHeight, 0)), mats.brick, meshes);
            Box("Below", wall, MinMax(new Vector3(-halfWin, 0, -t), new Vector3(halfWin, sillBottom, 0)), mats.brick, meshes);
            Box("Above", wall, MinMax(new Vector3(-halfWin, winTop, -t), new Vector3(halfWin, S.WallHeight, 0)), mats.brick, meshes);

            // Window marker at the glass centre; glass is a thin pane recessed behind the wall face.
            var window = Group("Window", root.transform);
            window.localPosition = S.WindowCentre;
            Box("Glass", window, MinMax(new Vector3(-halfWin, -S.WindowHeight / 2f, -0.02f), new Vector3(halfWin, S.WindowHeight / 2f, 0f)), mats.glass, meshes);
            // Behind the glass: close the recess so the opening doesn't show sky.
            Box("Backing", window, MinMax(new Vector3(-halfWin, -S.WindowHeight / 2f, -(t - S.WindowRecess)), new Vector3(halfWin, S.WindowHeight / 2f, -0.02f)), mats.glass, meshes);

            // Sill: top at SillHeight, spanning the recess and projecting in front of the wall.
            float sillFront = S.SillSize.z - S.WindowRecess;
            Box("Sill", root.transform, MinMax(new Vector3(-S.SillSize.x / 2f, sillBottom, -S.WindowRecess), new Vector3(S.SillSize.x / 2f, S.SillHeight, sillFront)), mats.sill, meshes);

            // Fascia board along the top, proud of the wall.
            float halfF = S.FasciaLength / 2f;
            Box("Fascia", root.transform, MinMax(new Vector3(-halfF, S.FasciaBottom, 0), new Vector3(halfF, S.FasciaTop, S.FasciaProud)), mats.fascia, meshes);

            // Gutter: K-style proxy as a U channel (back, bottom, front lip), 127 mm wide, hung on the fascia.
            var gutter = Group("Gutter", root.transform);
            float g0 = S.FasciaProud, g1 = S.FasciaProud + S.GutterWidth, gBottom = S.FasciaBottom - 0.02f, gTop = S.FasciaBottom + 0.10f, wt = 0.006f;
            Box("Back", gutter, MinMax(new Vector3(-halfF, gBottom, g0), new Vector3(halfF, gTop, g0 + wt)), mats.gutter, meshes);
            Box("Bottom", gutter, MinMax(new Vector3(-halfF, gBottom, g0), new Vector3(halfF, gBottom + wt, g1)), mats.gutter, meshes);
            Box("Front", gutter, MinMax(new Vector3(-halfF, gBottom, g1 - wt), new Vector3(halfF, gTop, g1)), mats.gutter, meshes);

            // Door (scale reference), slightly proud of the wall.
            Box("Door", root.transform, MinMax(new Vector3(S.DoorCentreX - S.DoorWidth / 2f, 0, 0), new Vector3(S.DoorCentreX + S.DoorWidth / 2f, S.DoorHeight, 0.03f)), mats.door, meshes);

            // Ledge: hinge at the wall at LedgeHeight, pitched LedgeTiltDeg about X (slopes down, away from the wall).
            var ledge = Group("Ledge", root.transform);
            ledge.localPosition = new Vector3(S.LedgeCentreX, S.LedgeHeight, 0f);
            ledge.localRotation = Quaternion.Euler(S.LedgeTiltDeg, 0f, 0f);
            Box("Slab", ledge, MinMax(new Vector3(-S.LedgeLength / 2f, -0.06f, 0f), new Vector3(S.LedgeLength / 2f, 0f, S.LedgeDepth)), mats.ledge, meshes);

            Box("Ground", root.transform, MinMax(new Vector3(-15f, -0.05f, -10f), new Vector3(15f, 0f, 15f)), mats.ground, meshes, uvTile: 2f);

            int layer = SceneLayers.SceneSurface;
            foreach (var tr in root.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = layer;
            return root;
        }

        static Bounds MinMax(Vector3 min, Vector3 max)
        {
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        static Transform Group(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        /// A box whose vertices are in the parent's space (transform = identity), with world-scale UVs:
        /// one texture tile covers 0.9 m × 0.6 m (4 bricks × 8 courses) unless uvTile is given.
        public static GameObject Box(string name, Transform parent, Bounds b, Material mat, List<Mesh> meshes, float uvTile = 0f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mesh = BoxMesh(b, uvTile > 0 ? new Vector2(uvTile, uvTile) : new Vector2(0.9f, 0.6f));
            mesh.name = parent.name + "_" + name;
            meshes?.Add(mesh);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            if (mat != null) r.sharedMaterial = mat;
            var col = go.AddComponent<BoxCollider>();
            col.center = b.center;
            col.size = b.size;
            return go;
        }

        static Mesh BoxMesh(Bounds b, Vector2 tile)
        {
            Vector3 mn = b.min, mx = b.max;
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var tri = new List<int>();
            void Face(Vector3 normal, Vector3 a, Vector3 bb, Vector3 c, Vector3 d, int uAxis, int vAxis)
            {
                int i = v.Count;
                foreach (var p in new[] { a, bb, c, d })
                {
                    v.Add(p); n.Add(normal);
                    uv.Add(new Vector2(p[uAxis] / tile.x, p[vAxis] / tile.y));
                }
                tri.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
            // Winding is clockwise seen from outside (Unity front faces).
            Face(Vector3.forward, new Vector3(mx.x, mn.y, mx.z), new Vector3(mx.x, mx.y, mx.z), new Vector3(mn.x, mx.y, mx.z), new Vector3(mn.x, mn.y, mx.z), 0, 1);
            Face(Vector3.back, new Vector3(mn.x, mn.y, mn.z), new Vector3(mn.x, mx.y, mn.z), new Vector3(mx.x, mx.y, mn.z), new Vector3(mx.x, mn.y, mn.z), 0, 1);
            Face(Vector3.right, new Vector3(mx.x, mn.y, mn.z), new Vector3(mx.x, mx.y, mn.z), new Vector3(mx.x, mx.y, mx.z), new Vector3(mx.x, mn.y, mx.z), 2, 1);
            Face(Vector3.left, new Vector3(mn.x, mn.y, mx.z), new Vector3(mn.x, mx.y, mx.z), new Vector3(mn.x, mx.y, mn.z), new Vector3(mn.x, mn.y, mn.z), 2, 1);
            Face(Vector3.up, new Vector3(mn.x, mx.y, mn.z), new Vector3(mn.x, mx.y, mx.z), new Vector3(mx.x, mx.y, mx.z), new Vector3(mx.x, mx.y, mn.z), 0, 2);
            Face(Vector3.down, new Vector3(mn.x, mn.y, mx.z), new Vector3(mn.x, mn.y, mn.z), new Vector3(mx.x, mn.y, mn.z), new Vector3(mx.x, mn.y, mx.z), 0, 2);
            var mesh = new Mesh();
            mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetUVs(0, uv); mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        static void SaveMeshes(List<Mesh> meshes)
        {
            // First mesh is the main asset, the rest are sub-assets (a plain Mesh container loads fine in players).
            if (AssetDatabase.LoadMainAssetAtPath(MeshesPath) != null) AssetDatabase.DeleteAsset(MeshesPath);
            AssetDatabase.CreateAsset(meshes[0], MeshesPath);
            for (int i = 1; i < meshes.Count; i++) AssetDatabase.AddObjectToAsset(meshes[i], meshes[0]);
            AssetDatabase.SaveAssets();
        }

        // ---------- materials ----------

        static Materials CreateMaterials()
        {
            var brickTex = CreateBrickTexture();
            return new Materials
            {
                brick = Mat("Brick", Color.white, 0.15f, brickTex),
                glass = Mat("Glass", new Color(0.10f, 0.16f, 0.22f), 0.9f),
                sill = Mat("Stone", new Color(0.78f, 0.76f, 0.72f), 0.2f),
                fascia = Mat("Fascia", new Color(0.93f, 0.93f, 0.90f), 0.35f),
                gutter = Mat("Gutter", new Color(0.85f, 0.86f, 0.88f), 0.6f),
                door = Mat("Door", new Color(0.36f, 0.22f, 0.13f), 0.3f),
                ledge = Mat("Ledge", new Color(0.62f, 0.62f, 0.60f), 0.2f),
                ground = Mat("Ground", new Color(0.42f, 0.44f, 0.40f), 0.1f),
            };
        }

        /// Facade shade per face (presence.md S1): the boxes are axis-aligned, so fixed factors stand in for the sun
        /// (sides 0.7, top 1.0, front/back 0.85), and the facade draws with the same unlit reveal shader as captures.
        public static readonly Vector4 FaceShade = new Vector4(0.7f, 1.0f, 0.85f, 0f);

        static Material Mat(string name, Color color, float smoothness, Texture2D tex = null)
        {
            string path = $"{OutputFolder}/{name}.mat";
            var shader = Shader.Find("AirTools/SceneReveal");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = shader;
            mat.SetColor("_BaseColor", color);
            mat.SetTexture("_BaseMap", tex);
            mat.SetVector("_FaceShade", FaceShade);
            mat.SetFloat("_FaceShading", 1f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// 4 bricks × 8 courses per tile (0.9 × 0.6 m): 215 × 65 mm bricks, 10 mm mortar, running bond.
        static Texture2D CreateBrickTexture()
        {
            string path = OutputFolder + "/BrickTile.png";
            const int w = 900, h = 600; // 1 px = 1 mm
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            var px = new Color32[w * h];
            var rng = new System.Random(7);
            var mortar = new Color32(196, 188, 176, 255);
            var brickShades = new Color[32];
            for (int i = 0; i < brickShades.Length; i++)
            {
                float k = (float)rng.NextDouble();
                brickShades[i] = Color.Lerp(new Color(0.55f, 0.20f, 0.14f), new Color(0.70f, 0.32f, 0.22f), k);
            }
            for (int y = 0; y < h; y++)
            {
                int course = y / 75, yIn = y % 75;
                int offset = (course % 2) * 112;
                for (int x = 0; x < w; x++)
                {
                    int xs = (x + offset) % w;
                    int brick = xs / 225, xIn = xs % 225;
                    bool isMortar = yIn >= 65 || xIn >= 215;
                    Color c = isMortar ? (Color)mortar : brickShades[(course * 5 + brick) % brickShades.Length];
                    float noise = ((x * 73856093 ^ y * 19349663) & 15) / 255f;
                    px[y * w + x] = (Color32)(c * (0.95f + noise));
                }
            }
            tex.SetPixels32(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.mipmapEnabled = true;
            importer.anisoLevel = 4;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ---------- cameras + thumbnails ----------

        /// 12 cameras on a 7 m arc in front of the wall (-55°..+55°), 1.5–2.6 m high, looking at the wall centre.
        public static SceneCameraInfo[] CreateCameras()
        {
            var cams = new SceneCameraInfo[S.CameraCount];
            var target = new Vector3(0f, S.WallHeight / 2f, 0f);
            for (int i = 0; i < cams.Length; i++)
            {
                float a = Mathf.Lerp(-55f, 55f, i / (float)(cams.Length - 1)) * Mathf.Deg2Rad;
                var pos = new Vector3(7f * Mathf.Sin(a), 1.5f + 1.1f * (i % 3) / 2f, 7f * Mathf.Cos(a));
                cams[i] = new SceneCameraInfo
                {
                    id = i,
                    position = pos,
                    rotation = Quaternion.LookRotation(target - pos, Vector3.up),
                    verticalFovDeg = CameraFovDeg,
                    aspect = ThumbWidth / (float)ThumbHeight,
                };
            }
            return cams;
        }

        static string ThumbPath(int i) => $"{OutputFolder}/thumbs/{i:0000}.jpg";

        static void RenderThumbnails(GameObject prefab, SceneCameraInfo[] cams)
        {
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var facade = (GameObject)PrefabUtility.InstantiatePrefab(prefab, preview);
                var lightGo = new GameObject("Sun");
                SceneManager_Move(lightGo, preview);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.3f;
                lightGo.transform.rotation = Quaternion.Euler(40f, 200f, 0f);

                var camGo = new GameObject("ThumbCamera");
                SceneManager_Move(camGo, preview);
                var cam = camGo.AddComponent<Camera>();
                cam.scene = preview;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.62f, 0.74f, 0.88f);
                cam.fieldOfView = CameraFovDeg;
                cam.nearClipPlane = 0.1f; cam.farClipPlane = 100f;
                camGo.AddComponent<UniversalAdditionalCameraData>();

                var rt = new RenderTexture(ThumbWidth, ThumbHeight, 24, RenderTextureFormat.ARGB32);
                var tex = new Texture2D(ThumbWidth, ThumbHeight, TextureFormat.RGB24, false);
                cam.targetTexture = rt;
                for (int i = 0; i < cams.Length; i++)
                {
                    camGo.transform.SetPositionAndRotation(cams[i].position, cams[i].rotation);
                    cam.Render();
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, ThumbWidth, ThumbHeight), 0, 0);
                    tex.Apply();
                    RenderTexture.active = null;
                    File.WriteAllBytes(ThumbPath(i), tex.EncodeToJPG(85));
                }
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(tex);
                Object.DestroyImmediate(facade);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
            for (int i = 0; i < cams.Length; i++) AssetDatabase.ImportAsset(ThumbPath(i));
        }

        static void SceneManager_Move(GameObject go, UnityEngine.SceneManagement.Scene scene) =>
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);

        /// cameras.json in Unity scene coordinates (metres, left-handed, +Y up; rotation = x,y,z,w).
        static TextAsset WriteCamerasJson(SceneCameraInfo[] cams)
        {
            string path = OutputFolder + "/cameras.json";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string F(float f) => f.ToString("0.#####", ci);
            var sb = new StringBuilder();
            sb.Append("{\n  \"frame\": \"unity\",\n  \"cameras\": [\n");
            for (int i = 0; i < cams.Length; i++)
            {
                var c = cams[i];
                sb.Append($"    {{\"id\": {c.id}, \"position\": [{F(c.position.x)}, {F(c.position.y)}, {F(c.position.z)}], " +
                          $"\"rotation\": [{F(c.rotation.x)}, {F(c.rotation.y)}, {F(c.rotation.z)}, {F(c.rotation.w)}], " +
                          $"\"vfov_deg\": {F(c.verticalFovDeg)}, \"width\": {ThumbWidth}, \"height\": {ThumbHeight}, \"thumb\": \"thumbs/{c.id:0000}.jpg\"}}");
                sb.Append(i < cams.Length - 1 ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");
            File.WriteAllText(path, sb.ToString());
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<TextAsset>(path);
        }
    }
}
