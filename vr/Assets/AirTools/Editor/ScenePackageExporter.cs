using System;
using System.IO;
using System.Threading.Tasks;
using AirTools.Dev;
using AirTools.Scene;
using GLTFast.Export;
using UnityEditor;
using UnityEngine;

namespace AirTools.Editor
{
    /// AirTools ▸ Export Synthetic Scene Packages: writes the synthetic facade as backend scene packages
    /// (docs/api.md: scene.json + mesh.r&lt;rev&gt;.glb + collision.r&lt;rev&gt;.glb + cameras.r&lt;rev&gt;.json + structure.r&lt;rev&gt;.json +
    /// thumbs/) under tools/.scenes/, for the real parts server to serve (SCENE_DIR=tools/.scenes):
    ///   synthetic-facade        true scale, with structure
    ///   synthetic-facade-small  everything × 1/1.47 (like the kitchen's altitude scale), for "set scale from a known dimension"
    ///   synthetic-facade-preview  no structure (a preview revision), mesh-only snapping
    ///   synthetic-facade-parts  the facade plus a kitchen-like run with two removable cabinets (ScenePartsFixtures):
    ///                           scene parts (docs/api.md parts.r&lt;rev&gt;.json) — parts.r1.json, mesh.parts.r1.glb
    ///                           (part_cab1, part_cab2, background), collision.parts.r1.glb (the same split),
    ///                           cavity.r1.glb (cavity_cab1, cavity_cab2 with flat linear COLOR_0), next to the one-piece
    ///                           mesh.r1.glb / collision.r1.glb older apps load
    /// Every number is exact, so loading, the X flip, snapping, pins, rescaling and cavities have ground truth.
    public static class ScenePackageExporter
    {
        public const float SmallScale = 1f / 1.47f;
        public static string OutputRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "tools", ".scenes"));
        public static string LastResult { get; private set; } = "";

        [MenuItem("AirTools/Export Synthetic Scene Packages")]
        public static void ExportMenu() => _ = ExportAll();

        /// Kicks off the export; poll <see cref="LastResult"/> (it starts with "done" or "error").
        public static string Start() { LastResult = "running"; _ = ExportAll(); return LastResult; }

        public static async Task ExportAll()
        {
            try
            {
                await Export("synthetic-facade", 1f, 1, withStructure: true);
                await Export("synthetic-facade-small", SmallScale, 1, withStructure: true);
                await Export("synthetic-facade-preview", 1f, 1, withStructure: false, quality: "preview");
                await ExportParts(ScenePartsFixtures.Site, 1f, 1);
                LastResult = $"done: {OutputRoot}";
            }
            catch (Exception ex)
            {
                LastResult = $"error: {ex}";
            }
            Debug.Log($"[AirTools] Scene package export {LastResult}");
        }

        /// One package revision. Revisions > 1 keep the older files (the server serves whatever scene.json names).
        public static async Task Export(string site, float scale, int revision, bool withStructure, string quality = "full")
        {
            string dir = Path.Combine(OutputRoot, site);
            Directory.CreateDirectory(Path.Combine(dir, "thumbs"));
            // The built prefab carries the textured materials (brick etc.); a bare hierarchy would export untextured.
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SyntheticFacadeBuilder.OutputFolder + "/Facade.prefab");
            var root = prefab != null ? UnityEngine.Object.Instantiate(prefab) : SyntheticFacadeBuilder.CreateHierarchy();
            root.name = "Facade";
            try
            {
                root.transform.localScale = Vector3.one * scale;
                Physics.SyncTransforms();
                await WriteGlb(root, Path.Combine(dir, $"mesh.r{revision}.glb"));
                await WriteGlb(root, Path.Combine(dir, $"collision.r{revision}.glb"));

                var cams = SyntheticFacadeBuilder.CreateCameras();
                File.WriteAllText(Path.Combine(dir, $"cameras.r{revision}.json"),
                    PackageFixtures.CamerasJson(cams, SyntheticFacadeBuilder.ThumbWidth, SyntheticFacadeBuilder.ThumbHeight, scale));
                foreach (var c in cams)
                {
                    string src = Path.Combine(Application.dataPath, "..", SyntheticFacadeBuilder.OutputFolder, "thumbs", $"{c.id:0000}.jpg");
                    if (File.Exists(src)) File.Copy(src, Path.Combine(dir, "thumbs", $"{c.id:0000}.jpg"), true);
                }

                int planes = 0, edges = 0, corners = 0, objects = 0;
                if (withStructure)
                {
                    root.transform.localScale = Vector3.one;   // the fixture scales points itself
                    var json = PackageFixtures.StructureJson(root.transform, scale, PackageFixtures.FacadeObjects());
                    File.WriteAllText(Path.Combine(dir, $"structure.r{revision}.json"), json);
                    var layer = StructureLayer.Parse(json);
                    planes = layer.Planes.Length; edges = layer.Edges.Length; corners = layer.Corners.Length; objects = layer.Objects.Length;
                }
                // Like pipeline/package.py recommended_spawn: the ground point in package units, plus 1.6 m eye height.
                var eye = SyntheticFacadeSpec.SpawnPosition * scale + Vector3.up * 1.6f;
                var look = SyntheticFacadeSpec.WindowCentre * scale;
                string sceneJson = PackageFixtures.SceneJson(site, revision, quality, eye, look, withStructure, planes, edges, corners, objects,
                    scaleMethod: Mathf.Approximately(scale, 1f) ? "known_dimension" : "altitude", scaleResidual: Mathf.Approximately(scale, 1f) ? 0.0 : 0.3);
                // scene.json last (the commit point, like the pipeline's atomic publish).
                string tmp = Path.Combine(dir, "scene.json.tmp");
                File.WriteAllText(tmp, sceneJson);
                File.Copy(tmp, Path.Combine(dir, "scene.json"), true);
                File.Delete(tmp);
                Debug.Log($"[AirTools] Exported {site} r{revision} (scale {scale:0.####}, structure {planes}p/{edges}e/{corners}c/{objects}o) → {dir}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static Task WriteGlb(GameObject root, string path) => WriteGlb(new[] { root }, path);

        /// Every object in `roots` becomes a top-level node named after it (part_cab1, background, cavity_cab1…).
        static async Task WriteGlb(GameObject[] roots, string path)
        {
            var export = new GameObjectExport(new ExportSettings { Format = GltfFormat.Binary, FileConflictResolution = FileConflictResolution.Overwrite });
            if (!export.AddScene(roots, "scene")) throw new Exception($"glTF export: AddScene failed for {path}");
            if (!await export.SaveToFileAndDispose(path)) throw new Exception($"glTF export failed: {path}");
        }

        /// synthetic-facade-parts r`revision`: the facade plus ScenePartsFixtures' run (a fixed cabinet, the removable
        /// cab1 and cab2, a counter), as a scene package with scene parts in the documented layout (docs/api.md
        /// parts.r&lt;rev&gt;.json): the one-piece mesh / collision (the fallback), structure (the run's boxes and doors
        /// included, like the kitchen's dishwasher rectangle), cameras + thumbs, then the split mesh and collision
        /// (part_<id> nodes beside `background`), the cavities (flat linear COLOR_0) and parts.r&lt;rev&gt;.json; scene.json last.
        public static async Task ExportParts(string site, float scale, int revision)
        {
            string dir = Path.Combine(OutputRoot, site);
            Directory.CreateDirectory(Path.Combine(dir, "thumbs"));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SyntheticFacadeBuilder.OutputFolder + "/Facade.prefab");
            var root = prefab != null ? UnityEngine.Object.Instantiate(prefab) : SyntheticFacadeBuilder.CreateHierarchy();
            root.name = "Facade";
            var partRoots = new System.Collections.Generic.List<GameObject>();
            var cavityRoots = new System.Collections.Generic.List<GameObject>();
            var runMeshes = new System.Collections.Generic.List<Mesh>();
            Material cavityMaterial = null;
            try
            {
                // The run, textured like the facade (cabinets like the door, the counter like the sill).
                var cabinet = MaterialOf(root.transform, "Door");
                var counter = MaterialOf(root.transform, "Sill");
                var run = new GameObject("Run").transform;
                run.SetParent(root.transform, false);
                foreach (var b in ScenePartsFixtures.Run())
                    SyntheticFacadeBuilder.Box(b.name, run, b.box, b.kind == "counter" ? counter : cabinet, runMeshes);
                foreach (var t in run.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = SceneLayers.SceneSurface;

                // One piece: what an app without scene parts loads (and this app's fallback).
                root.transform.localScale = Vector3.one * scale;
                Physics.SyncTransforms();
                await WriteGlb(root, Path.Combine(dir, $"mesh.r{revision}.glb"));
                await WriteGlb(root, Path.Combine(dir, $"collision.r{revision}.glb"));

                var cams = SyntheticFacadeBuilder.CreateCameras();
                File.WriteAllText(Path.Combine(dir, $"cameras.r{revision}.json"),
                    PackageFixtures.CamerasJson(cams, SyntheticFacadeBuilder.ThumbWidth, SyntheticFacadeBuilder.ThumbHeight, scale));
                foreach (var c in cams)
                {
                    string src = Path.Combine(Application.dataPath, "..", SyntheticFacadeBuilder.OutputFolder, "thumbs", $"{c.id:0000}.jpg");
                    if (File.Exists(src)) File.Copy(src, Path.Combine(dir, "thumbs", $"{c.id:0000}.jpg"), true);
                }

                root.transform.localScale = Vector3.one;   // the fixture scales points itself
                var structure = PackageFixtures.StructureJson(root.transform, scale, ScenePartsFixtures.Objects());
                File.WriteAllText(Path.Combine(dir, $"structure.r{revision}.json"), structure);
                var layer = StructureLayer.Parse(structure);

                // Split: each removable box becomes a top-level node part_<id> beside `background` (everything else).
                root.transform.localScale = Vector3.one * scale;
                foreach (var b in ScenePartsFixtures.Run())
                {
                    if (!b.removable) continue;
                    var t = run.Find(b.name);
                    t.SetParent(null, true);
                    partRoots.Add(t.gameObject);
                }
                root.name = "background";
                var split = new System.Collections.Generic.List<GameObject> { root };
                split.AddRange(partRoots);
                await WriteGlb(split.ToArray(), Path.Combine(dir, $"mesh.parts.r{revision}.glb"));
                await WriteGlb(split.ToArray(), Path.Combine(dir, $"collision.parts.r{revision}.glb"));

                // Cavities: flat, linear vertex colours (glTF COLOR_0 is linear; the backend samples sRGB and linearises).
                cavityMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "Cavity" };
                var vertices = new System.Collections.Generic.List<Vector3>();
                var colors = new System.Collections.Generic.List<Color>();
                var triangles = new System.Collections.Generic.List<int>();
                foreach (var b in ScenePartsFixtures.Run())
                {
                    if (!b.removable) continue;
                    ScenePartsFixtures.CavityMesh(b, scale, vertices, colors, triangles);
                    var mesh = new Mesh { name = $"cavity_{b.id}" };
                    mesh.SetVertices(vertices);
                    mesh.SetColors(colors);
                    mesh.SetTriangles(triangles, 0);
                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                    var go = new GameObject($"cavity_{b.id}");
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = cavityMaterial;
                    cavityRoots.Add(go);
                }
                await WriteGlb(cavityRoots.ToArray(), Path.Combine(dir, $"cavity.r{revision}.glb"));
                int components = partRoots.Count;
                File.WriteAllText(Path.Combine(dir, $"parts.r{revision}.json"), ScenePartsFixtures.PartsJson(revision, scale));

                var eye = SyntheticFacadeSpec.SpawnPosition * scale + Vector3.up * 1.6f;
                var look = new Vector3((ScenePartsFixtures.Cab1Left + ScenePartsFixtures.Cab1Right) * 0.5f, ScenePartsFixtures.Top * 0.5f, 0f) * scale;
                string sceneJson = PackageFixtures.SceneJson(site, revision, "full", eye, look, true,
                    layer.Planes.Length, layer.Edges.Length, layer.Corners.Length, layer.Objects.Length,
                    scaleMethod: Mathf.Approximately(scale, 1f) ? "known_dimension" : "altitude", scaleResidual: Mathf.Approximately(scale, 1f) ? 0.0 : 0.3,
                    parts: ScenePartsFixtures.PartsEntry(revision, components));
                string tmp = Path.Combine(dir, "scene.json.tmp");
                File.WriteAllText(tmp, sceneJson);
                File.Copy(tmp, Path.Combine(dir, "scene.json"), true);
                File.Delete(tmp);
                Debug.Log($"[AirTools] Exported {site} r{revision} with {components} scene parts (scale {scale:0.####}, structure {layer.Summary}) → {dir}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                foreach (var go in partRoots) if (go != null) UnityEngine.Object.DestroyImmediate(go);
                foreach (var go in cavityRoots)
                {
                    if (go == null) continue;
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) UnityEngine.Object.DestroyImmediate(mf.sharedMesh);
                    UnityEngine.Object.DestroyImmediate(go);
                }
                if (cavityMaterial != null) UnityEngine.Object.DestroyImmediate(cavityMaterial);
                foreach (var m in runMeshes) if (m != null) UnityEngine.Object.DestroyImmediate(m);
            }
        }

        /// The cavity colours through glTF and back, as the app loads them: a quad with the fixture's linear back-wall
        /// colour is exported (glTFast writes mesh colours as COLOR_0 unchanged) and imported (glTFast copies COLOR_0
        /// into the mesh unchanged), so what SceneReveal multiplies in a Linear project is the linear value the backend
        /// wrote. "" when it survives; else what went wrong. (ScenePartsBindTests runs it.)
        public static async Task<string> CavityColourRoundTrip()
        {
            var want = ScenePartsFixtures.BackColor;
            byte[] bytes;
            var go = new GameObject("cavity_roundtrip");
            var mesh = new Mesh { name = "cavity_roundtrip" };
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            try
            {
                mesh.SetVertices(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) });
                mesh.SetColors(new[] { want, want, want, want });
                mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
                mesh.RecalculateNormals();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                var export = new GameObjectExport(new ExportSettings { Format = GltfFormat.Binary });
                if (!export.AddScene(new[] { go }, "scene")) return "export: AddScene failed";
                using var stream = new MemoryStream();
                if (!await export.SaveToStreamAndDispose(stream, true)) return "export failed";
                bytes = stream.ToArray();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(material);
            }
            var gltf = new GLTFast.GltfImport(null, new GLTFast.UninterruptedDeferAgent());
            var holder = new GameObject("cavity_roundtrip_import");
            try
            {
                if (!await gltf.Load(bytes)) return "import failed";
                if (!await gltf.InstantiateMainSceneAsync(holder.transform)) return "instantiate failed";
                var mf = holder.GetComponentInChildren<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) return "no mesh after import";
                if (!mf.sharedMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color)) return "no COLOR_0 after import";
                var got = mf.sharedMesh.colors[0];
                float d = Mathf.Max(Mathf.Abs(got.r - want.r), Mathf.Max(Mathf.Abs(got.g - want.g), Mathf.Abs(got.b - want.b)));
                return d <= 1.5f / 255f ? "" : $"colour changed on the way: wrote {want} (linear), read {got} (a gamma step?)";
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(holder);
                gltf.Dispose();
            }
        }

        static Material MaterialOf(Transform root, string name)
        {
            var t = AirTools.Scene.SceneParts.FindNode(root, name);
            var r = t != null ? t.GetComponent<Renderer>() : null;
            return r != null ? r.sharedMaterial : null;
        }

        /// Bumps a package to the next revision with the same content (the revision-swap check).
        public static string BumpRevision(string site)
        {
            string dir = Path.Combine(OutputRoot, site);
            string scenePath = Path.Combine(dir, "scene.json");
            var m = SceneManifest.Parse(File.ReadAllText(scenePath));
            int next = m.revision + 1;
            void CopyAs(string file, string ext)
            {
                if (string.IsNullOrEmpty(file)) return;
                File.Copy(Path.Combine(dir, file), Path.Combine(dir, file.Replace($".r{m.revision}.", $".r{next}.")), true);
            }
            CopyAs(m.MeshFile, "glb"); CopyAs(m.CollisionFile, "glb"); CopyAs(m.cameras, "json"); CopyAs(m.StructureFile, "json");
            string text = File.ReadAllText(scenePath).Replace($".r{m.revision}.", $".r{next}.")
                .Replace($"\"revision\": {m.revision}", $"\"revision\": {next}");
            File.WriteAllText(scenePath, text);
            return $"{site} r{m.revision} → r{next}";
        }
    }
}
