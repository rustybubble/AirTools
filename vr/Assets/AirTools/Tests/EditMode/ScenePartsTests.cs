using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using AirTools.Scene;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// Scene parts (backend docs/api.md "parts.r&lt;rev&gt;.json"): scene.json's `parts` entry and the airtools.parts/1 file.
    /// Pure: runs in the offline runner (no engine calls).
    public class ScenePartsParseTests
    {
        static string J(string s) => s.Replace('\'', '"');

        /// scene.json with the `parts` entry exactly as docs/api.md shows it.
        public static readonly string ApiMdScene = J(@"{
 'name': 'kitchen', 'units': 'meters', 'up': [0, 1, 0], 'revision': 1, 'quality': 'full',
 'mesh': {'file': 'mesh.r1.glb', 'triangles': 199997, 'texture_px': 4096},
 'collision': {'file': 'collision.r1.glb', 'triangles': 50000},
 'cameras': 'cameras.r1.json',
 'structure': {'file': 'structure.r1.json', 'schema': 'airtools.structure/1', 'edges': 671, 'corners': 594, 'planes': 61, 'objects': 40},
 'parts': {'file': 'parts.r1.json', 'schema': 'airtools.parts/1', 'components': 4,
           'mesh': 'mesh.parts.r1.glb', 'cavities': 'cavity.r1.glb',
           'collision': 'collision.parts.r1.glb'}
}");

        /// A hand-built kitchen run of 4 components in a rotated frame (d = (0.6, 0, 0.8), up = +Y, r = up × d =
        /// (0.8, 0, −0.6)), floor at y = −0.25, laid out like E1's result (dw1 between bc1 and rg1, rg1 open-topped),
        /// with the provenance fields the app must ignore. insert.p = r·mid + up·floor + d·front; box absolute (rud).
        public static readonly string Kitchen4 = J(@"{
 'schema': 'airtools.parts/1', 'frame': 'scene', 'revision': 1, 'method': 's4+vlm+sam2+vote',
 'mesh': 'mesh.parts.r1.glb', 'cavities': 'cavity.r1.glb', 'collision': 'collision.parts.r1.glb',
 'scale': {'method': 'altitude', 'residual_m': 0.05, 'note': 'sizes are in scene metres'},
 'cost': {'runtime_s': 532.1, 'stages_s': {'name_s': 236}, 'vlm': {'model': 'qwen'}},
 'components': [
  {'id': 'dw1', 'label': 'dishwasher', 'class': 'appliance', 'removable': true, 'label_source': 'vlm+s4',
   'node': 'part_dw1', 'collision_node': 'part_dw1', 'faces': 5563,
   'bbox': {'min': [0.1, -0.25, 0.5], 'max': [1.0, 0.34, 1.0]},
   'obb': {'center': [0.9, 0.05, 0.6], 'axes': [[0.8, 0, -0.6], [0, 1, 0], [0.6, 0, 0.8]], 'size': [0.43, 0.58, 0.44]},
   'structure_objects': ['o32'],
   'evidence': {'frames': ['0111'], 'vlm': 'qwen', 'masks': 'sam2', 'views_voted': 8, 'voted_faces': 3983, 'rect_iou': 0.93},
   'cut': {'holes_cm2': 33, 'cap_cm2': 117, 'leftover_faces': 0},
   'cavity': {'node': 'cavity_dw1', 'estimated': true, 'fill': 'flat',
     'size_m': {'w': 0.437, 'h': 0.591, 'd': 0.445}, 'sigma_m': {'w': 0.004, 'h': 0.02, 'd': 0.046},
     'insert': {'p': [0.9748, -0.25, 0.7689], 'axes': [[0.8, 0, -0.6], [0, 1, 0], [0.6, 0, 0.8]]},
     'box': {'min_rud': [0.1, -0.25, 0.755], 'max_rud': [0.537, 0.341, 1.2]},
     'bounds': {'left': {'value': 0.1, 'edges': ['e117', 'e83'], 'observed': false, 'sigma': 0.004},
                'top': {'value': 0.341, 'edges': ['e16'], 'observed': false, 'sigma': 0.02},
                'floor': {'value': -0.25, 'plane': 'p7', 'observed': false, 'sigma': 0.01}},
     'planes': ['p2', 'p4', 'p5', 'p7'],
     'colors': {'floor': [120, 110, 100]}}},
  {'id': 'bc1', 'label': 'sink cabinet', 'class': 'cabinet', 'removable': true, 'label_source': 'vlm+s4',
   'node': 'part_bc1', 'collision_node': 'part_bc1', 'faces': 16487, 'structure_objects': ['o30', 'o31'],
   'cavity': {'node': 'cavity_bc1', 'estimated': true,
     'size_m': {'w': 0.949, 'h': 0.583, 'd': 0.451}, 'sigma_m': {'w': 0.005, 'h': 0.022, 'd': 0.051},
     'insert': {'p': [0.4204, -0.25, 1.1847], 'axes': [[0.8, 0, -0.6], [0, 1, 0], [0.6, 0, 0.8]]},
     'box': {'min_rud': [-0.849, -0.25, 0.749], 'max_rud': [0.1, 0.333, 1.2]}}},
  {'id': 'rg1', 'label': 'range', 'class': 'appliance', 'removable': true, 'label_source': 'vlm+s4',
   'node': 'part_rg1', 'collision_node': 'part_rg1', 'faces': 19395, 'structure_objects': [],
   'cavity': {'node': 'cavity_rg1', 'estimated': true,
     'size_m': {'w': 0.517, 'h': 0.793, 'd': 0.44}, 'sigma_m': {'w': 0.003, 'h': 0.024, 'd': 0.044},
     'insert': {'p': [1.3564, -0.25, 0.4827], 'axes': [[0.8, 0, -0.6], [0, 1, 0], [0.6, 0, 0.8]]},
     'box': {'min_rud': [0.537, -0.25, 0.76], 'max_rud': [1.054, 0.543, 1.2]},
     'bounds': {'top': {'value': 0.543, 'sigma': 0.01, 'observed': false, 'source': 'component'}}}},
  {'id': 'fr1', 'label': 'fridge', 'class': 'appliance', 'removable': true, 'label_source': 'vlm',
   'node': 'part_fr1', 'collision_node': 'part_fr1', 'faces': 11731,
   'cavity': {'node': 'cavity_fr1', 'estimated': true,
     'size_m': {'w': 0.492, 'h': 1.178, 'd': 0.543}, 'sigma_m': {'w': 0.004, 'h': 0.023, 'd': 0.053},
     'insert': {'p': [2.1968, -0.25, -0.1476], 'axes': [[0.8, 0, -0.6], [0, 1, 0], [0.6, 0, 0.8]]},
     'box': {'min_rud': [1.6, -0.25, 0.657], 'max_rud': [2.092, 0.928, 1.2]}}}
 ]
}");

        static ScenePartsDoc Kitchen() => ScenePartsDoc.Parse(Kitchen4);

        static CavityBox Box(string id)
        {
            Assert.IsTrue(CavityBox.TryFrom(Kitchen().Find(id), out var box, out var why), why);
            return box;
        }

        static void Near(Vector3 want, Vector3 got, float tol, string what) =>
            Assert.Less(Vector3.Distance(want, got), tol, $"{what}: want {want.ToString("F4")} got {got.ToString("F4")}");

        [Test]
        public void SceneJson_PartsEntry_AsInApiMd()
        {
            var m = SceneManifest.Parse(ApiMdScene);
            Assert.IsTrue(m.HasParts);
            Assert.AreEqual("parts.r1.json", m.PartsFile);
            Assert.AreEqual("airtools.parts/1", m.parts.schema);
            Assert.AreEqual(4, m.parts.components);
            Assert.AreEqual("mesh.parts.r1.glb", m.parts.mesh);
            Assert.AreEqual("cavity.r1.glb", m.parts.cavities);
            Assert.AreEqual("collision.parts.r1.glb", m.parts.collision);
            // The one-piece files stay named (older apps, and the fallback).
            Assert.AreEqual("mesh.r1.glb", m.MeshFile);
            Assert.AreEqual("collision.r1.glb", m.CollisionFile);
        }

        [Test]
        public void SceneJson_WithoutParts_IsOnePiece()
        {
            var m = SceneManifest.Parse(J("{'mesh': {'file': 'mesh.r1.glb'}, 'collision': {'file': 'collision.r1.glb'}, 'revision': 1}"));
            Assert.IsFalse(m.HasParts);
            Assert.IsNull(m.parts);
            var n = SceneManifest.Parse(J("{'mesh': {'file': 'mesh.r1.glb'}, 'parts': null, 'revision': 1}"));
            Assert.IsFalse(n.HasParts);
        }

        [Test]
        public void FourComponents_ParseAndIgnoreProvenance()
        {
            var doc = Kitchen();
            Assert.AreEqual("airtools.parts/1", doc.schema);
            Assert.AreEqual("scene", doc.frame);
            Assert.AreEqual("mesh.parts.r1.glb", doc.mesh);
            Assert.AreEqual("cavity.r1.glb", doc.cavities);
            Assert.AreEqual("collision.parts.r1.glb", doc.collision);
            Assert.AreEqual("altitude", doc.scale.method);
            CollectionAssert.AreEqual(new[] { "dw1", "bc1", "rg1", "fr1" }, doc.components.Select(c => c.id).ToArray());
            Assert.AreEqual(4, doc.Removable.Count);
            var dw = doc.Find("dw1");
            Assert.AreEqual("dishwasher", dw.label);
            Assert.AreEqual("appliance", dw.kind);
            Assert.AreEqual("vlm+s4", dw.label_source);
            Assert.AreEqual(5563, dw.faces);
            CollectionAssert.AreEqual(new[] { "o32" }, dw.structure_objects);
            Assert.IsTrue(dw.cavity.estimated);
            Assert.AreEqual(0.437, dw.cavity.size_m.w, 1e-9);
            Assert.AreEqual(0.591, dw.cavity.size_m.h, 1e-9);
            Assert.AreEqual(0.445, dw.cavity.size_m.d, 1e-9);
            Assert.AreEqual(0.046, dw.cavity.sigma_m.d, 1e-9);
            Assert.AreEqual(3, dw.cavity.insert.axes.Length);
            Assert.IsNotNull(dw.cavity.bounds);
            Assert.IsFalse(dw.cavity.OpenTop, "dw1 has the counter above it");
            Assert.IsTrue(doc.Find("rg1").cavity.OpenTop, "the range's top is its own backguard");
            Assert.IsEmpty(doc.Find("fr1").structure_objects, "missing structure_objects → empty");
        }

        [Test]
        public void NodeNames_FromTheFile_ElseTheDocumentedOnes()
        {
            var dw = Kitchen().Find("dw1");
            Assert.AreEqual("part_dw1", dw.VisualNode);
            Assert.AreEqual("part_dw1", dw.CollisionNode);
            Assert.AreEqual("cavity_dw1", dw.CavityNode);
            var bare = ScenePartsDoc.Parse(J("{'components': [{'id': 'x9', 'cavity': {'size_m': {'w': 1, 'h': 1, 'd': 1}}}]}")).components[0];
            Assert.AreEqual("part_x9", bare.VisualNode);
            Assert.AreEqual("part_x9", bare.CollisionNode);
            Assert.AreEqual("cavity_x9", bare.CavityNode);
            Assert.IsTrue(bare.removable, "removable defaults to true");
            Assert.IsFalse(bare.VisualNode.Contains("/"), "node names use _, never / (Transform.Find reads / as a path)");
        }

        [Test]
        public void Find_ByIdLabelOrClass_AndDisplayNames()
        {
            var doc = Kitchen();
            Assert.AreEqual("dw1", doc.Find("DW1").id);
            Assert.AreEqual("dw1", doc.Find("dishwasher").id);
            Assert.AreEqual("bc1", doc.Find("sink_cabinet").id);
            Assert.AreEqual("bc1", doc.Find("Sink Cabinet").id);
            Assert.AreEqual("bc1", doc.Find("cabinet").id, "class match");
            Assert.AreEqual("fr1", doc.Find("fridge").id);
            Assert.IsNull(doc.Find("oven door"));
            Assert.IsNull(doc.Find(""));
            Assert.AreEqual("Dishwasher", doc.Find("dw1").DisplayName);
            Assert.AreEqual("Sink cabinet", doc.Find("bc1").DisplayName);
        }

        [Test]
        public void CavityBox_FlipsXLikeTheMesh()
        {
            var b = Box("dw1");
            Near(new Vector3(-0.9748f, -0.25f, 0.7689f), b.P, 1e-4f, "insert.p with X negated");
            Near(new Vector3(0f, 1f, 0f), b.U, 1e-5f, "up");
            Near(new Vector3(-0.6f, 0f, 0.8f), b.D, 1e-5f, "out, X negated");
            Near(new Vector3(-0.8f, 0f, -0.6f), b.R, 1e-5f, "right, X negated (the viewer's right)");
            // The viewer faces −D; Unity's right for that view is Cross(up, forward) = Cross(U, −D).
            Near(Vector3.Cross(b.U, -b.D), b.R, 1e-5f, "R is the right of someone facing the opening");
            // insert.p is the floor, front, centre of the box.
            var rud = b.Rud(b.P);
            Assert.AreEqual((0.1f + 0.537f) * 0.5f, rud.x, 1e-3f);
            Assert.AreEqual(-0.25f, rud.y, 1e-3f);
            Assert.AreEqual(1.2f, rud.z, 1e-3f);
            Assert.IsFalse(b.BoxWasRelative);
        }

        [Test]
        public void CavityBox_SizesAreTheFilesAndTheBoxAgrees()
        {
            foreach (var id in new[] { "dw1", "bc1", "rg1", "fr1" })
            {
                var c = Kitchen().Find(id);
                var b = Box(id);
                Assert.AreEqual((float)c.cavity.size_m.w, b.Width, 1e-6f, $"{id} W is the file's");
                Assert.AreEqual((float)c.cavity.size_m.h, b.Height, 1e-6f, $"{id} H");
                Assert.AreEqual((float)c.cavity.size_m.d, b.Depth, 1e-6f, $"{id} D");
                Assert.AreEqual(b.Width, Vector3.Distance(b.Corner(0), b.Corner(1)), 2e-3f, $"{id} corner 0 → 1 spans W");
                Assert.AreEqual(b.Height, Vector3.Distance(b.Corner(0), b.Corner(2)), 2e-3f, $"{id} corner 0 → 2 spans H");
                Assert.AreEqual(b.Depth, Vector3.Distance(b.Corner(0), b.Corner(4)), 2e-3f, $"{id} corner 0 → 4 spans D");
                Assert.IsTrue(b.Inside(b.Centre, 0.01f, 0f), $"{id} centre inside");
                Assert.IsFalse(b.Inside(b.P + b.D * 0.1f, 0f, 0.03f), $"{id} 10 cm in front is outside");
            }
            // Neighbours share a side: dw1's left is bc1's right.
            Near(Box("bc1").Corner(5), Box("dw1").Corner(4), 1e-4f, "shared side (front right of bc1 = front left of dw1)");
        }

        [Test]
        public void CavityBox_RelativeBox_IsDetected()
        {
            var doc = ScenePartsDoc.Parse(J(@"{'components': [{'id': 'c1', 'cavity': {
                'size_m': {'w': 0.6, 'h': 0.8, 'd': 0.5},
                'insert': {'p': [2.0, 0.0, 1.0], 'axes': [[1,0,0],[0,1,0],[0,0,1]]},
                'box': {'min_rud': [-0.3, 0.0, -0.5], 'max_rud': [0.3, 0.8, 0.0]}}}]}"));
            Assert.IsTrue(CavityBox.TryFrom(doc.components[0], out var b, out var why), why);
            Assert.IsTrue(b.BoxWasRelative);
            Near(new Vector3(-2f, 0f, 1f), b.P, 1e-5f, "p flipped");
            Near(new Vector3(-2f, 0.4f, 0.75f), b.Centre, 1e-4f, "centre half a depth behind p, half a height up");
        }

        [Test]
        public void CavityBox_WithoutBoxOrAxes_FromInsertAndSize()
        {
            var doc = ScenePartsDoc.Parse(J(@"{'components': [{'id': 'c1', 'cavity': {
                'size_m': {'w': 0.6, 'h': 0.8, 'd': 0.5}, 'insert': {'p': [1.0, 0.1, 2.0]}}}]}"));
            Assert.IsTrue(CavityBox.TryFrom(doc.components[0], out var b, out var why), why);
            Near(new Vector3(-1f, 0.1f, 2f), b.P, 1e-5f, "p");
            Near(new Vector3(0f, 0f, 1f), b.D, 1e-6f, "default out = +Z");
            Assert.AreEqual(0.6f, Vector3.Distance(b.Corner(0), b.Corner(1)), 1e-4f);
            Near(new Vector3(-1f, 0.5f, 1.75f), b.Centre, 1e-4f, "centre");
            Assert.IsFalse(CavityBox.TryFrom(ScenePartsDoc.Parse(J("{'components': [{'id': 'c2'}]}")).components[0], out _, out var none));
            Assert.AreEqual("no cavity", none);
        }

        [Test]
        public void Rejects_OtherSchemasFramesAndNonObjects()
        {
            Assert.Throws<FormatException>(() => ScenePartsDoc.Parse(J("{'schema': 'airtools.parts/2', 'components': []}")));
            Assert.Throws<FormatException>(() => ScenePartsDoc.Parse(J("{'schema': 'airtools.structure/1'}")));
            Assert.Throws<FormatException>(() => ScenePartsDoc.Parse(J("{'schema': 'airtools.parts/1', 'frame': 'bench'}")));
            Assert.Throws<FormatException>(() => ScenePartsDoc.Parse("[1, 2]"));
            Assert.Throws<FormatException>(() => ScenePartsDoc.Parse("{\"components\": ["));
            Assert.Throws<FormatException>(() => ScenePartsDoc.Parse(""));
            Assert.DoesNotThrow(() => ScenePartsDoc.Parse(J("{'schema': 'airtools.parts/1.1', 'components': []}")), "a minor revision is fine");
            Assert.DoesNotThrow(() => ScenePartsDoc.Parse(J("{'components': []}")), "schema and frame may be missing");
        }

        [Test]
        public void Files_TheSceneEntryWins_ElseTheDocNamesThem()
        {
            var m = SceneManifest.Parse(ApiMdScene);
            var doc = ScenePartsDoc.Parse(J("{'mesh': 'other.glb', 'cavities': 'c.glb', 'collision': 'k.glb', 'components': []}"));
            var f = ScenePartsFiles.For(m, doc);
            Assert.AreEqual("mesh.parts.r1.glb", f.Mesh);
            Assert.AreEqual("collision.parts.r1.glb", f.Collision);
            Assert.AreEqual("cavity.r1.glb", f.Cavities);
            var bare = SceneManifest.Parse(J("{'mesh': {'file': 'mesh.r2.glb'}, 'revision': 2, 'parts': {'file': 'parts.r2.json'}}"));
            f = ScenePartsFiles.For(bare, doc);
            Assert.AreEqual("other.glb", f.Mesh, "the doc names the split mesh when the entry doesn't");
            Assert.AreEqual("k.glb", f.Collision);
            Assert.AreEqual("c.glb", f.Cavities);
            f = ScenePartsFiles.For(bare, ScenePartsDoc.Parse(J("{'components': []}")));
            Assert.IsNull(f.Mesh, "no split mesh named anywhere: load in one piece");
            Assert.IsNull(f.Collision);
            Assert.IsNull(f.Cavities);
        }

        [Test]
        public void Tolerates_WrongTypedFieldsMissingIdsAndDuplicates()
        {
            var doc = ScenePartsDoc.Parse(J(@"{'components': [
                {'id': 'a', 'removable': 'yes please', 'faces': 'many', 'label': 'oven'},
                {'label': 'no id'},
                null,
                {'id': 'a', 'label': 'duplicate'},
                {'id': 'b', 'removable': false, 'label': 'wall'}]}"));
            CollectionAssert.AreEqual(new[] { "a", "b" }, doc.components.Select(c => c.id).ToArray());
            Assert.AreEqual("oven", doc.Find("a").label, "the first of a repeated id wins");
            Assert.IsTrue(doc.Find("a").removable, "an unreadable field keeps its default");
            CollectionAssert.AreEqual(new[] { "a" }, doc.Removable.Select(c => c.id).ToArray(), "removable: false is never offered");
        }
    }

    /// SceneParts binding against real transforms (Editor gate: needs the engine).
    public class ScenePartsBindTests
    {
        GameObject m_Root;
        SceneParts m_Parts;

        static GameObject Node(Transform parent, string name, bool mesh = true)
        {
            var go = mesh ? GameObject.CreatePrimitive(PrimitiveType.Cube) : new GameObject();
            go.name = name;
            go.transform.SetParent(parent, false);
            return go;
        }

        [SetUp]
        public void Build()
        {
            m_Root = new GameObject("PartsTest");
            m_Parts = m_Root.AddComponent<SceneParts>();
        }

        [TearDown]
        public void Teardown() => UnityEngine.Object.DestroyImmediate(m_Root);

        (GameObject visual, GameObject collision, GameObject cavities) Holders()
        {
            var visual = new GameObject("Mesh r1"); visual.transform.SetParent(m_Root.transform, false);
            var world = Node(visual.transform, "world", mesh: false).transform;   // trimesh's parent node
            Node(world, "background"); Node(world, "part_dw1"); Node(world, "part_bc1"); Node(world, "part_rg1"); Node(world, "part_fr1");
            var collision = new GameObject("Collision r1"); collision.transform.SetParent(m_Root.transform, false);
            Node(collision.transform, "background"); Node(collision.transform, "part_dw1"); Node(collision.transform, "part_bc1");
            Node(collision.transform, "part_rg1");   // fr1 has no collision faces
            var cavities = new GameObject("Cavities r1"); cavities.transform.SetParent(m_Root.transform, false);
            foreach (var id in new[] { "dw1", "bc1", "rg1", "fr1" }) Node(cavities.transform, $"cavity_{id}");
            return (visual, collision, cavities);
        }

        [Test]
        public void Bind_FindsEveryNodeAndHidesTheCavities()
        {
            var (visual, collision, cavities) = Holders();
            m_Parts.Bind("kitchen", 1, ScenePartsDoc.Parse(ScenePartsParseTests.Kitchen4), visual, collision, cavities);
            Assert.IsTrue(m_Parts.HasParts);
            Assert.AreEqual(4, m_Parts.Removable.Count);
            Assert.IsTrue(m_Parts.TryNodes("dw1", out var dw));
            Assert.AreEqual("part_dw1", dw.visual.name);
            Assert.IsTrue(dw.visual.IsChildOf(visual.transform));
            Assert.IsTrue(dw.collision.IsChildOf(collision.transform));
            Assert.AreEqual("cavity_dw1", dw.cavity.name);
            foreach (var id in new[] { "dw1", "bc1", "rg1", "fr1" })
            {
                Assert.IsTrue(m_Parts.TryNodes(id, out var n), id);
                Assert.IsFalse(n.cavity.gameObject.activeSelf, $"cavity_{id} starts hidden");
                Assert.IsTrue(n.visual.gameObject.activeSelf, $"part_{id} starts shown");
            }
            Assert.IsTrue(m_Parts.TryNodes("fr1", out var fr));
            Assert.IsNull(fr.collision, "no collision node for fr1");
            CollectionAssert.AreEqual(new[] { "collision part_fr1" }, m_Parts.Missing);
            StringAssert.Contains("dw1 Dishwasher in visual=on collision=on cavity=off", m_Parts.Report());
        }

        [Test]
        public void Unbind_ForgetsThePackage()
        {
            var (visual, collision, cavities) = Holders();
            m_Parts.Bind("kitchen", 1, ScenePartsDoc.Parse(ScenePartsParseTests.Kitchen4), visual, collision, cavities);
            int v = m_Parts.Version;
            m_Parts.Unbind();
            Assert.IsFalse(m_Parts.HasParts);
            Assert.IsFalse(m_Parts.TryNodes("dw1", out _));
            Assert.Greater(m_Parts.Version, v);
            Assert.AreEqual("no scene parts", m_Parts.Report());
        }

        [Test]
        public void FindNode_DirectChildOrDeeper()
        {
            var (visual, _, _) = Holders();
            Assert.AreEqual("part_rg1", SceneParts.FindNode(visual.transform, "part_rg1").name, "under the world node");
            Assert.AreEqual("world", SceneParts.FindNode(visual.transform, "world").name, "a direct child");
            Assert.IsNull(SceneParts.FindNode(visual.transform, "part_zz9"));
            Assert.IsNull(SceneParts.FindNode(null, "part_dw1"));
        }

        /// The cavity colours survive glTF export → import unchanged (linear COLOR_0 in, the same numbers in the mesh).
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator CavityColours_SurviveTheGlbRoundTrip()
        {
            var task = AirTools.Editor.ScenePackageExporter.CavityColourRoundTrip();
            double t0 = UnityEditor.EditorApplication.timeSinceStartup;
            while (!task.IsCompleted && UnityEditor.EditorApplication.timeSinceStartup - t0 < 20.0) yield return null;
            Assert.IsTrue(task.IsCompleted, "the round trip timed out");
            Assert.AreEqual("", task.Result);
        }

        /// Cavities are drawn with the scene's material multiplied by their linear COLOR_0 (SceneStreamer.PrepareCavities):
        /// the shader has the switch, and the project renders in Linear, so linear vertex colours multiply in unconverted.
        [Test]
        public void CavityColours_TheShaderSwitchAndLinearColourSpace()
        {
            var shader = Shader.Find("AirTools/SceneReveal");
            Assert.IsNotNull(shader, "AirTools/SceneReveal");
            var m = new Material(shader);
            try
            {
                Assert.IsTrue(m.HasProperty("_VertexColors"), "SceneReveal has _VertexColors");
                Assert.AreEqual(0f, m.GetFloat("_VertexColors"), "off unless a cavity turns it on");
            }
            finally { UnityEngine.Object.DestroyImmediate(m); }
            Assert.AreEqual(ColorSpace.Linear, UnityEditor.PlayerSettings.colorSpace, "the project renders in Linear");
        }
    }

    /// Scene parts copy, action args and chip words. Pure: the offline runner.
    public class ScenePartsCopyTests
    {
        [TearDown]
        public void Units() => AirTools.UI.UiSettings.UseUnits(AirTools.UI.UiSettings.DefaultUnits);

        [Test]
        public void Triple_OneUnitPerLabel()
        {
            Assert.AreEqual("437 × 591 × 445 mm", AirTools.Core.Units.FormatTriple(0.437, 0.591, 0.445, AirTools.Core.UnitSystem.Metric));
            Assert.AreEqual("17¼ × 23¼ × 17½″", AirTools.Core.Units.FormatTriple(0.437, 0.591, 0.445, AirTools.Core.UnitSystem.Imperial));
            Assert.AreEqual("437 × 591 × 445 mm", AirTools.Core.Units.TripleMm(0.437, 0.591, 0.445), "raw text is always mm");
            Assert.AreEqual("1′ 7⅜″ × 8′ 2⅜″ × 2′ 1⅜″", AirTools.Core.Units.FormatTriple(0.492, 2.498, 0.645, AirTools.Core.UnitSystem.Imperial),
                "8 ft and over: feet and inches");
            Assert.AreEqual(AirTools.Core.Units.Dash, AirTools.Core.Units.FormatTriple(double.NaN, 1, 1, AirTools.Core.UnitSystem.Metric));
        }

        [Test]
        public void CavityLabel_SaysEstimated_InTheUsersUnit()
        {
            AirTools.UI.UiSettings.UseUnits(AirTools.Core.UnitSystem.Metric);
            Assert.AreEqual("437 × 591 × 445 mm · estimated", AirTools.UI.Copy.Cavity(0.437, 0.591, 0.445));
            Assert.AreEqual("Dishwasher out · 437 × 591 × 445 mm · estimated", AirTools.UI.Copy.PartOut("dishwasher", 0.437, 0.591, 0.445));
            Assert.AreEqual("Dishwasher out", AirTools.UI.Copy.PartOut("dishwasher", null, null, null));
            Assert.AreEqual("Sink cabinet back", AirTools.UI.Copy.PartBack("Sink cabinet"));
            AirTools.UI.UiSettings.UseUnits(AirTools.Core.UnitSystem.Imperial);
            Assert.AreEqual("17¼ × 23¼ × 17½″ · estimated", AirTools.UI.Copy.Cavity(0.437, 0.591, 0.445));
            Assert.LessOrEqual(AirTools.UI.Copy.PartOut("Dishwasher", 0.437, 0.591, 0.445).Length, 48, "fits the status line's line 1");
        }

        [Test]
        public void CavityLabel_UsesTheFilesNumbers()
        {
            // The label is the file's size_m (× the calibration), whatever the file says: nothing is hard-coded.
            var doc = ScenePartsDoc.Parse(ScenePartsParseTests.Kitchen4);
            Assert.IsTrue(CavityBox.TryFrom(doc.Find("dw1"), out var box, out _));
            AirTools.UI.UiSettings.UseUnits(AirTools.Core.UnitSystem.Metric);
            Assert.AreEqual("437 × 591 × 445 mm · estimated", CavityView.Text(box, 1f));
            Assert.AreEqual("642 × 869 × 654 mm · estimated", CavityView.Text(box, 1.47f), "after Set scale ×1.47 it reads what the tape reads");
            Assert.IsTrue(CavityBox.TryFrom(doc.Find("fr1"), out var fridge, out _));
            Assert.AreEqual("492 × 1178 × 543 mm · estimated", CavityView.Text(fridge, 1f));
        }

        [Test]
        public void RemoveComponent_Args_AreTolerant()
        {
            JObject A(string json) => JObject.Parse(json.Replace('\'', '"'));
            Assert.AreEqual("dw1", AirTools.Agent.ScenePartsActions.ComponentId(A("{'component_id': 'dw1'}")));
            Assert.AreEqual("dw1", AirTools.Agent.ScenePartsActions.ComponentId(A("{'id': ' dw1 '}")));
            Assert.AreEqual("dishwasher", AirTools.Agent.ScenePartsActions.ComponentId(A("{'label': 'dishwasher'}")));
            Assert.AreEqual("bc1", AirTools.Agent.ScenePartsActions.ComponentId(A("{'component': {'id': 'bc1', 'label': 'sink cabinet'}}")));
            Assert.AreEqual("rg1", AirTools.Agent.ScenePartsActions.ComponentId(A("{'component_id': '', 'id': 'rg1'}")), "an empty id falls through");
            Assert.IsNull(AirTools.Agent.ScenePartsActions.ComponentId(A("{'component_id': null, 'ids': ['a']}")));
            Assert.IsNull(AirTools.Agent.ScenePartsActions.ComponentId(null));
        }

        [Test]
        public void ChipWords_TheFilesLabelClipped()
        {
            var doc = ScenePartsDoc.Parse(ScenePartsParseTests.Kitchen4);
            Assert.AreEqual("Dishwasher", AirTools.Structure.ScenePartsRow.ChipText(doc.Find("dw1")));
            Assert.AreEqual("Sink cabinet", AirTools.Structure.ScenePartsRow.ChipText(doc.Find("bc1")));
            var longer = ScenePartsDoc.Parse("{\"components\": [{\"id\": \"x\", \"label\": \"under-counter wine fridge\"}]}").components[0];
            Assert.LessOrEqual(AirTools.Structure.ScenePartsRow.ChipText(longer).Length, AirTools.Structure.ScenePartsRow.MaxChars);
        }
    }

    /// Taking parts out and putting them back on real transforms (Editor gate: needs the engine).
    public class ScenePartsRemoveTests
    {
        GameObject m_App, m_RootGo;
        SceneRoot m_Root;
        SceneParts m_Parts;
        GameObject m_Content;

        static GameObject Node(Transform parent, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            return go;
        }

        [SetUp]
        public void Build()
        {
            AirTools.UI.WorldLabels.Reset();
            m_RootGo = new GameObject("SceneRoot");
            m_Root = m_RootGo.AddComponent<SceneRoot>();
            m_Content = new GameObject("kitchen");
            m_Content.transform.SetParent(m_RootGo.transform, false);
            var manifest = SceneManifest.Parse(ScenePartsParseTests.ApiMdScene);
            m_Root.SetRuntimeContent("kitchen", manifest, null, m_Content, null);
            AirTools.Core.Services.Register(m_Root);
            var visual = new GameObject("Mesh r1"); visual.transform.SetParent(m_Content.transform, false);
            var collision = new GameObject("Collision r1"); collision.transform.SetParent(m_Content.transform, false);
            var cavities = new GameObject("Cavities r1"); cavities.transform.SetParent(m_Content.transform, false);
            foreach (var id in new[] { "dw1", "bc1", "rg1", "fr1" })
            {
                Node(visual.transform, $"part_{id}");
                Node(collision.transform, $"part_{id}");
                Node(cavities.transform, $"cavity_{id}");
            }
            Node(visual.transform, "background");
            m_App = new GameObject("App");
            m_Parts = m_App.AddComponent<SceneParts>();
            m_Parts.sceneRoot = m_Root;
            AirTools.Core.Services.Register(m_Parts);   // EditMode: OnEnable doesn't run for AddComponent
            AirTools.Tools.EditHistory.Register(m_Parts);
            m_Parts.Bind("kitchen", 1, ScenePartsDoc.Parse(ScenePartsParseTests.Kitchen4), visual, collision, cavities);
        }

        [TearDown]
        public void Teardown()
        {
            AirTools.Core.Services.Unregister(m_Parts);
            AirTools.Core.Services.Unregister(m_Root);
            UnityEngine.Object.DestroyImmediate(m_App);
            UnityEngine.Object.DestroyImmediate(m_RootGo);
            AirTools.Tools.EditHistory.Unregister(m_Parts);
            AirTools.UI.WorldLabels.Reset();
        }

        void AssertOut(string id, bool removed)
        {
            Assert.IsTrue(m_Parts.TryNodes(id, out var n), id);
            Assert.AreEqual(!removed, n.visual.gameObject.activeSelf, $"{id} visual");
            Assert.AreEqual(!removed, n.collision.gameObject.activeSelf, $"{id} collision (no ghost for the tape)");
            Assert.AreEqual(removed, n.cavity.gameObject.activeSelf, $"{id} cavity");
            Assert.AreEqual(removed, m_Parts.IsRemoved(id), $"{id} out");
            var v = m_Parts.ViewOf(id);
            if (removed) { Assert.IsNotNull(v, $"{id} outline"); Assert.IsTrue(v.Shown); }
            else if (v != null) Assert.IsFalse(v.Shown, $"{id} outline hidden when back");
        }

        [Test]
        public void Remove_TheThreeStepsAndTheEstimatedLabel()
        {
            Assert.IsTrue(AirTools.AppCommands.RemoveComponent("dw1"));
            AssertOut("dw1", true);
            AssertOut("bc1", false);
            Assert.AreEqual("dw1", m_Parts.LastRemoved.id);
            StringAssert.EndsWith("· estimated", m_Parts.ViewOf("dw1").LabelText);
            StringAssert.Contains("removed dw1", m_Parts.LastAction);
            Assert.IsTrue(AirTools.AppCommands.RemoveComponent("dw1"), "already out: not an error");
            Assert.IsTrue(AirTools.AppCommands.RestoreComponent("dishwasher"), "by label");
            AssertOut("dw1", false);
            Assert.IsNull(m_Parts.LastRemoved);
            Assert.IsFalse(AirTools.AppCommands.RemoveComponent("oven"), "no such part");
        }

        /// The collision part_<id> goes with the part: a ray that stopped on it now reaches the cavity behind (the brief:
        /// "so the tape and snapping don't hit a ghost").
        [Test]
        public void Remove_TheRayGoesThroughToTheCavity()
        {
            Assert.IsTrue(m_Parts.TryNodes("dw1", out var n));
            foreach (var c in m_Content.GetComponentsInChildren<Collider>(true)) c.enabled = false;   // only these two count
            n.collision.GetComponent<Collider>().enabled = true;
            n.cavity.GetComponent<Collider>().enabled = true;
            n.collision.position = new Vector3(0f, 0.5f, 0f);
            n.cavity.position = new Vector3(0f, 0.5f, -2f);
            Physics.SyncTransforms();
            var ray = new Ray(new Vector3(0f, 0.5f, 5f), Vector3.back);
            Assert.IsTrue(Physics.Raycast(ray, out var before, 20f));
            Assert.AreEqual("part_dw1", before.collider.name, "the part stops the ray");
            m_Parts.Remove("dw1");
            Assert.IsTrue(Physics.Raycast(ray, out var after, 20f));
            Assert.AreEqual("cavity_dw1", after.collider.name, "the removed part's collider is gone; the cavity takes the hit");
            m_Parts.Restore("dw1");
            Assert.IsTrue(Physics.Raycast(ray, out var back, 20f));
            Assert.AreEqual("part_dw1", back.collider.name);
        }

        /// While a part is out the scene root snaps to the edited layer (cavity features in); back → the published one.
        [Test]
        public void Remove_SwapsTheSnappingLayer_AndBack()
        {
            var published = StructureLayer.Parse(ScenePartsSnapTests.Structure);
            m_Root.SetStructure(m_Root.Manifest, published, null);
            Assert.AreSame(published, m_Root.Structure);
            m_Parts.Remove("dw1");
            Assert.AreNotSame(published, m_Root.Structure);
            Assert.AreSame(published, m_Root.BaseStructure);
            Assert.GreaterOrEqual(m_Root.Structure.PlaneIndex("dw1.floor"), 0, "the cavity's floor is a snap plane");
            m_Parts.Remove("fr1");
            Assert.GreaterOrEqual(m_Root.Structure.PlaneIndex("fr1.back"), 0, "both cavities");
            Assert.GreaterOrEqual(m_Root.Structure.PlaneIndex("dw1.back"), 0);
            m_Parts.Restore("dw1");
            Assert.Less(m_Root.Structure.PlaneIndex("dw1.floor"), 0);
            m_Parts.Restore("fr1");
            Assert.AreSame(published, m_Root.Structure, "nothing out: the published layer");
        }

        /// place_part without a pose: into the cavity of the part taken out last, front-bottom-centre at insert.p, facing
        /// out, with the server's verdict in the fit colours; one undoable placement.
        [Test]
        public void PlacePart_WithoutAPose_GoesIntoTheLastCavity()
        {
            var toolGo = new GameObject("PartTool");
            try
            {
                var tool = toolGo.AddComponent<AirTools.Parts.PartTool>();
                tool.frame = m_RootGo.transform;
                AirTools.Tools.EditHistory.Register(tool);
                m_Parts.Remove("dw1");
                var args = AirTools.Parts.PlacePartArgs.Parse(Newtonsoft.Json.Linq.JObject.Parse(
                    "{\"part_id\": \"dw-new\", \"fits\": true, \"clearance_mm\": {\"w\": 17, \"h\": 11, \"d\": 15}}"));
                Assert.IsTrue(AirTools.AppCommands.TryPlaceTarget(args, m_Root, out var target, out var why), why);
                StringAssert.Contains("cavity dw1 insert", target.Source);
                var spec = new AirTools.Parts.PartSpec
                {
                    id = "dw-new", name = "Dishwasher", dims_mm = new AirTools.Parts.PartDims { w = 420f, d = 430f, h = 580f },
                    mount = new AirTools.Parts.PartMount { face = "-y" },
                };
                var model = GameObject.CreatePrimitive(PrimitiveType.Cube);
                model.transform.localScale = spec.dims_mm.Metres;
                var part = AirTools.Parts.PartInstance.Create(spec, model, m_RootGo.transform, new AirTools.Tools.MeasureStyle(), "test");
                var pos = AirTools.Parts.PlacePartMath.OriginFor(target.Anchor, target.Rotation, AirTools.Parts.PartMath.LocalBox(spec));
                var fit = AirTools.Parts.PlacePartFit.From(args.Fits, args.Clearance);
                tool.PlaceAt(part, pos, target.Rotation, fit);
                Assert.IsTrue(part.Placed);
                Assert.AreSame(fit, part.Fit, "the server's verdict is shown");
                Assert.AreEqual(AirTools.Parts.PartOutline.Green, part.Outline.CurrentColor);
                StringAssert.Contains("Fits the gap", part.Outline.CalloutText);
                Assert.IsTrue(CavityBox.TryFrom(m_Parts.Find("dw1"), out var box, out _));
                var centre = m_Content.transform.InverseTransformPoint(part.WorldBoxCentre);
                Assert.IsTrue(box.Inside(centre, 0.001f, 0f), $"in the gap: {centre.ToString("F3")}");
                Assert.AreEqual(box.Rud(box.P).y, box.Rud(m_Content.transform.InverseTransformPoint(part.transform.TransformPoint(AirTools.Parts.PartMath.LocalBox(spec).min))).y, 1e-3f, "on the floor");
                Assert.IsTrue(AirTools.Tools.EditHistory.Undo(), "one undoable placement");
                Assert.IsFalse(part.gameObject.activeSelf);
                AirTools.Tools.EditHistory.Unregister(tool);
                tool.ClearAll();
            }
            finally { UnityEngine.Object.DestroyImmediate(toolGo); }
        }

        [Test]
        public void PlacePart_NothingOutAndNoPose_IsRefused()
        {
            var args = AirTools.Parts.PlacePartArgs.Parse(Newtonsoft.Json.Linq.JObject.Parse("{\"part_id\": \"x\"}"));
            Assert.IsFalse(AirTools.AppCommands.TryPlaceTarget(args, m_Root, out _, out var why));
            StringAssert.StartsWith("Take a part out first", why);
            var posed = AirTools.Parts.PlacePartArgs.Parse(Newtonsoft.Json.Linq.JObject.Parse("{\"part_id\": \"x\", \"pose\": {\"p\": [1, 0, 2], \"yaw_deg\": 0}}"));
            Assert.IsTrue(AirTools.AppCommands.TryPlaceTarget(posed, m_Root, out var t, out _));
            Assert.AreEqual(new Vector3(-1f, 0f, 2f), t.Anchor, "package → root (identity content): X flipped");
        }

        [Test]
        public void OneUndoableEdit_EachWay()
        {
            Assert.IsTrue(m_Parts.Remove("rg1"));
            Assert.IsTrue(AirTools.Tools.EditHistory.CanUndo);
            Assert.IsTrue(AirTools.Tools.EditHistory.Undo());
            AssertOut("rg1", false);
            Assert.IsTrue(AirTools.Tools.EditHistory.CanRedo);
            Assert.IsTrue(AirTools.Tools.EditHistory.Redo());
            AssertOut("rg1", true);
            Assert.IsTrue(m_Parts.Restore("rg1"));
            AssertOut("rg1", false);
            Assert.IsTrue(AirTools.Tools.EditHistory.Undo(), "undoing the restore takes it out again");
            AssertOut("rg1", true);
        }

        [Test]
        public void ResetSession_EverythingBack_NoHistory()
        {
            m_Parts.Remove("dw1");
            m_Parts.Remove("fr1");
            Assert.AreEqual(2, m_Parts.RemovedCount);
            Assert.AreEqual("fr1", m_Parts.LastRemoved.id, "the newest removal");
            m_Parts.ResetSession();
            Assert.AreEqual(0, m_Parts.RemovedCount);
            AssertOut("dw1", false);
            AssertOut("fr1", false);
            Assert.IsFalse(m_Parts.CanUndo);
            Assert.IsFalse(m_Parts.CanRedo);
            AirTools.DemoReset.Verify(out var clean);
            StringAssert.Contains("scene_parts_out=0", clean);
            m_Parts.Remove("bc1");
            Assert.IsFalse(AirTools.DemoReset.Verify(out var dirty));
            StringAssert.Contains("scene parts", dirty);
            StringAssert.Contains("scene_parts_out=1", dirty);
        }

        [Test]
        public void CavityLabel_IsATaskClaimInThePool()
        {
            m_Parts.Remove("dw1");
            var v = m_Parts.ViewOf("dw1");
            var claims = new System.Collections.Generic.List<AirTools.UI.LabelClaim>();
            v.ClaimLabels(claims);
            Assert.AreEqual(1, claims.Count);
            Assert.AreEqual(AirTools.UI.LabelClass.Task, claims[0].Class);
            Assert.AreEqual(1, claims[0].Items);
            AirTools.UI.WorldLabels.Refresh();
            Assert.IsTrue(v.LabelShown, "room in the pool: the label shows");
            m_Parts.Restore("dw1");
            claims.Clear();
            v.ClaimLabels(claims);
            Assert.AreEqual(0, claims[0].Items, "hidden: nothing claimed");
        }

        [Test]
        public void MoreRow_OneChipPerRemovablePart()
        {
            var window = new GameObject("Settings");
            try
            {
                var row = AirTools.Editor.ScenePartsBuilder.Build(window.transform, null, new Vector3(-0.154f, -0.2f, 0f), 0.308f);
                row.Refresh(force: true);
                CollectionAssert.AreEqual(new[] { "dw1", "bc1", "rg1", "fr1" }, row.ChipIds);
                CollectionAssert.AreEqual(new[] { "Dishwasher", "Sink cabinet", "Range", "Fridge" }, row.chips.Select(c => c.Text).ToArray());
                Assert.IsTrue(row.chips.All(c => c.gameObject.activeSelf));
                Assert.IsFalse(row.empty.gameObject.activeSelf);
                Assert.IsTrue(row.Press(0));
                AssertOut("dw1", true);
                Assert.IsTrue(row.chips[0].selected, "ink while it's out");
                Assert.IsTrue(row.Press(0));
                AssertOut("dw1", false);
                Assert.IsFalse(row.chips[0].selected);
                m_Parts.Unbind();
                row.Refresh();
                Assert.IsTrue(row.chips.All(c => !c.gameObject.activeSelf), "no parts: no chips");
                Assert.IsTrue(row.empty.gameObject.activeSelf, "… and the row says so");
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }
    }

    /// Cavity snapping (ScenePartsLayer): the edited structure layer and a tape across the opening. Pure: offline runner.
    /// A kitchen-like run in Unity coordinates (wall at z = 0 facing +Z): left cabinet x −0.7…0.2, the removable unit
    /// x 0.2…0.8 (its door rectangle o1 at x 0.21…0.79, z 0.58), right cabinet 0.8…1.7, counter lip at y 0.82; the
    /// structure file (glTF) is written with X negated, as the backend writes it.
    public class ScenePartsSnapTests
    {
        static string G(float x, float y, float z) => FormattableString.Invariant($"[{-x},{y},{z}]");
        static string Ring(params (float x, float y, float z)[] q) => "[" + string.Join(",", q.Select(p => G(p.x, p.y, p.z))) + "]";

        /// n in Unity; the file's normal is X-negated and the offset stays (n·x + offset = 0).
        static string Plane(string id, (float x, float y, float z) n, float offset, params (float x, float y, float z)[] ring) =>
            FormattableString.Invariant($"{{\"id\":\"{id}\",\"normal\":{G(n.x, n.y, n.z)},\"offset\":{offset},\"polygon3d\":{Ring(ring)}}}");

        public static readonly string Structure = "{\"schema\":\"airtools.structure/1\",\"frame\":\"scene\",\"axes\":[[1,0,0],[0,1,0],[0,0,1]],\"planes\":[" +
            Plane("p0", (0, 0, 1), -0.58f, (-0.7f, 0, 0.58f), (1.7f, 0, 0.58f), (1.7f, 0.82f, 0.58f), (-0.7f, 0.82f, 0.58f)) + "," +   // the run's front
            Plane("p1", (0, 0, 1), -0.58f, (0.21f, 0.02f, 0.58f), (0.79f, 0.02f, 0.58f), (0.79f, 0.8f, 0.58f), (0.21f, 0.8f, 0.58f)) + "," +   // the door
            Plane("p2", (1, 0, 0), -0.2f, (0.2f, 0, 0), (0.2f, 0, 0.58f), (0.2f, 0.82f, 0.58f), (0.2f, 0.82f, 0)) + "," +   // left cabinet's side
            Plane("p3", (0, 1, 0), 0f, (-3f, 0, -1f), (3f, 0, -1f), (3f, 0, 3f), (-3f, 0, 3f)) +   // the floor
            "],\"corners\":[" +
            FormattableString.Invariant($"{{\"id\":\"c0\",\"p\":{G(0.21f, 0.02f, 0.58f)},\"kind\":\"rect\",\"planes\":[\"p1\"]}},") +   // door corner
            FormattableString.Invariant($"{{\"id\":\"c1\",\"p\":{G(0.6f, 0.7f, 0.58f)},\"kind\":\"2edge\"}},") +   // control panel
            FormattableString.Invariant($"{{\"id\":\"c2\",\"p\":{G(0.2f, 0f, 0.58f)},\"kind\":\"3plane\",\"planes\":[\"p0\",\"p2\",\"p3\"]}},") +   // jamb foot
            FormattableString.Invariant($"{{\"id\":\"c3\",\"p\":{G(-0.7f, 0.86f, 0.62f)},\"kind\":\"2edge\"}}") +   // counter end
            "],\"edges\":[" +
            FormattableString.Invariant($"{{\"id\":\"e0\",\"a\":{G(0.21f, 0.8f, 0.58f)},\"b\":{G(0.79f, 0.8f, 0.58f)},\"kind\":\"boundary\",\"planes\":[\"p1\"]}},") +   // door top
            FormattableString.Invariant($"{{\"id\":\"e1\",\"a\":{G(0.2f, 0f, 0.58f)},\"b\":{G(0.2f, 0.82f, 0.58f)},\"kind\":\"crease\",\"planes\":[\"p0\",\"p2\"]}},") +   // left jamb
            FormattableString.Invariant($"{{\"id\":\"e2\",\"a\":{G(-0.7f, 0.82f, 0.62f)},\"b\":{G(1.7f, 0.82f, 0.62f)},\"kind\":\"line\"}},") +   // counter lip
            FormattableString.Invariant($"{{\"id\":\"e3\",\"a\":{G(0.21f, 0.02f, 0.58f)},\"b\":{G(0.21f, 0.8f, 0.58f)},\"kind\":\"boundary\",\"planes\":[\"p1\"]}}") +   // door's left side (1 cm in)
            "],\"objects\":[" +
            FormattableString.Invariant($"{{\"id\":\"o1\",\"label\":\"appliance_front\",\"plane\":\"p1\",\"corners3d\":{Ring((0.21f, 0.02f, 0.58f), (0.79f, 0.02f, 0.58f), (0.79f, 0.8f, 0.58f), (0.21f, 0.8f, 0.58f))},\"w_m\":0.58,\"h_m\":0.78}},") +
            FormattableString.Invariant($"{{\"id\":\"o2\",\"label\":\"cabinet_door\",\"plane\":\"p0\",\"corners3d\":{Ring((-0.69f, 0.02f, 0.58f), (0.19f, 0.02f, 0.58f), (0.19f, 0.8f, 0.58f), (-0.69f, 0.8f, 0.58f))},\"w_m\":0.88,\"h_m\":0.78}}") +
            "],\"groups\":[{\"id\":\"g0\",\"kind\":\"repeat\",\"members\":[\"o1\",\"o2\"]}]}";

        /// The unit's parts entry: cavity x 0.2…0.8 (Unity), floor 0, back 0 (the wall), front 0.58; glTF r = +X, so its
        /// r range is the negated x range (−0.8…−0.2).
        public static readonly string Parts = ("{'schema':'airtools.parts/1','frame':'scene','components':[{'id':'cab1','label':'cabinet','class':'cabinet'," +
            "'structure_objects':['o1'],'cavity':{'node':'cavity_cab1','estimated':true,'size_m':{'w':0.6,'h':0.82,'d':0.58}," +
            "'insert':{'p':[-0.5,0,0.58],'axes':[[1,0,0],[0,1,0],[0,0,1]]},'box':{'min_rud':[-0.8,0,0],'max_rud':[-0.2,0.82,0.58]}}}]}").Replace('\'', '"');

        static StructureLayer Base() => StructureLayer.Parse(Structure);

        static (ScenePartComponent, CavityBox) Removed(bool openTop = false)
        {
            var doc = ScenePartsDoc.Parse(openTop ? Parts.Replace("\"estimated\":true", "\"estimated\":true,\"bounds\":{\"top\":{\"value\":0.82,\"source\":\"component\"}}") : Parts);
            var c = doc.Find("cab1");
            Assert.IsTrue(CavityBox.TryFrom(c, out var box, out var why), why);
            return (c, box);
        }

        static StructureLayer Composed(bool openTop = false) => ScenePartsLayer.Compose(Base(), new[] { Removed(openTop) });

        [Test]
        public void TheCavityBox_IsWhereTheUnitWas()
        {
            var (_, box) = Removed();
            Assert.AreEqual(0.2f, Mathf.Min(box.Corner(0).x, box.Corner(1).x), 1e-5f);
            Assert.AreEqual(0.8f, Mathf.Max(box.Corner(0).x, box.Corner(1).x), 1e-5f);
            Assert.AreEqual(0.58f, box.Corner(4).z, 1e-5f, "front");
            Assert.AreEqual(0f, box.Corner(0).z, 1e-5f, "back = the wall");
            Assert.AreEqual(0.82f, box.Corner(2).y, 1e-5f, "top = the counter's underside");
            // Someone at the opening faces −Z; Unity's right for that view is Cross(up, −Z) = −X: corner 1 (max r) is at x 0.2.
            Assert.AreEqual(0.2f, box.Corner(1).x, 1e-5f, "corner 1 is the viewer's right");
            Assert.AreEqual(0.8f, box.Corner(0).x, 1e-5f, "corner 0 the viewer's left");
        }

        [Test]
        public void Compose_DropsTheRemovedPartsFeatures_KeepsTheNeighbours()
        {
            var b = Base();
            var e = Composed();
            string[] Ids<T>(T[] a, Func<T, string> id) => a.Select(id).ToArray();
            var planes = Ids(e.Planes, p => p.id); var corners = Ids(e.Corners, k => k.id); var edges = Ids(e.Edges, x => x.id); var objects = Ids(e.Objects, o => o.id);
            CollectionAssert.Contains(planes, "p0", "the run's front spans the neighbours");
            CollectionAssert.Contains(planes, "p3", "the floor");
            CollectionAssert.DoesNotContain(planes, "p1", "the door's plane");
            CollectionAssert.DoesNotContain(planes, "p2", "the side face the cavity's right (x 0.2) replaces");
            CollectionAssert.DoesNotContain(corners, "c0", "a door corner");
            CollectionAssert.DoesNotContain(corners, "c1", "the control panel, inside the box");
            CollectionAssert.Contains(corners, "c2", "the jamb's foot");
            CollectionAssert.Contains(corners, "c3");
            CollectionAssert.DoesNotContain(edges, "e0", "the door's top");
            CollectionAssert.DoesNotContain(edges, "e3", "the door's side, 1 cm inside the jamb: the ghost the tape used to find");
            CollectionAssert.Contains(edges, "e1", "the left jamb");
            CollectionAssert.Contains(edges, "e2", "the counter lip");
            CollectionAssert.AreEqual(new[] { "o2" }, objects, "the door object goes");
            Assert.AreEqual(e.PlaneIndex("p0"), e.Objects[0].plane, "o2's plane index remapped");
            CollectionAssert.AreEqual(new[] { "o2" }, e.Groups[0].members);
            // The jamb's plane refs lose the dropped side face and point at the new indices.
            var jamb = e.Edges.First(x => x.id == "e1");
            CollectionAssert.AreEqual(new[] { e.PlaneIndex("p0") }, jamb.planes);
            // The cavity: floor, back, left, right, top; 12 edges; 8 corners.
            CollectionAssert.IsSubsetOf(new[] { "cab1.floor", "cab1.back", "cab1.left", "cab1.right", "cab1.top" }, planes);
            Assert.AreEqual(12, e.Edges.Count(x => x.id.StartsWith("cab1.")));
            Assert.AreEqual(8, e.Corners.Count(x => x.id.StartsWith("cab1.")));
            Assert.AreEqual(4, e.Edges.Count(x => x.id.StartsWith("cab1.") && x.kind == "boundary"), "the opening's rim: 2 jambs, floor lip, underside lip");
            // The base is untouched.
            Assert.AreEqual(4, b.Planes.Length);
            Assert.AreEqual(2, b.Objects.Length);
            Assert.AreSame(b, ScenePartsLayer.Compose(b, new (ScenePartComponent, CavityBox)[0]), "nothing out: the published layer");
        }

        [Test]
        public void Compose_OpenTop_HasNoTopFace_AndWithoutAStructureLayerTheCavityAlone()
        {
            var e = Composed(openTop: true);
            CollectionAssert.DoesNotContain(e.Planes.Select(p => p.id).ToArray(), "cab1.top");
            var alone = ScenePartsLayer.Compose(null, new[] { Removed() });
            Assert.AreEqual(5, alone.Planes.Length);
            Assert.AreEqual(12, alone.Edges.Length);
            Assert.AreEqual(8, alone.Corners.Length);
            Assert.IsEmpty(alone.Objects);
        }

        [Test]
        public void CavityPlanes_AreBoundedFacesWithTheirNormalsIntoTheGap()
        {
            var e = Composed();
            // The viewer's left is +x here (they face −Z): the left face is the right cabinet's side at x = 0.8.
            var left = e.Planes[e.PlaneIndex("cab1.left")];
            Assert.AreEqual(0f, left.SignedDistance(new Vector3(0.8f, 0.4f, 0.3f)), 1e-5f, "on x = 0.8");
            Assert.Greater(left.SignedDistance(new Vector3(0.5f, 0.4f, 0.3f)), 0f, "normal into the gap");
            Assert.IsTrue(StructureLayer.Contains(left, left.ToPlane(new Vector3(0.8f, 0.4f, 0.3f))), "inside its outline");
            Assert.IsFalse(StructureLayer.Contains(left, left.ToPlane(new Vector3(0.8f, 1.2f, 0.3f))), "bounded: not above the counter");
            var right = e.Planes[e.PlaneIndex("cab1.right")];
            Assert.AreEqual(0f, right.SignedDistance(new Vector3(0.2f, 0.4f, 0.3f)), 1e-5f, "the right face on x = 0.2");
            var floor = e.Planes[e.PlaneIndex("cab1.floor")];
            Assert.AreEqual(Vector3.up, floor.normal);
        }

        /// A 2-point tape across the opening, from 1.3 m in front: each point snaps to a jamb (the cavity's front vertical
        /// edge), so the reading is the opening's width: 0.600 m.
        [Test]
        public void TapeAcrossTheOpening_SnapsToTheJambs()
        {
            var layer = Composed();
            var settings = StructureSnapper.Settings.Default;
            var eye = new Vector3(0.5f, 1.3f, 1.8f);
            StructureSnapper.Result Aim(Vector3 onFace, Vector3 faceNormal, Vector3 noise)
            {
                var hit = onFace + noise;
                var q = new StructureSnapper.Query { origin = eye, dir = (hit - eye).normalized, meshHit = hit, meshNormal = faceNormal };
                return StructureSnapper.Snap(layer, q, settings);
            }
            // The mesh hit is on the cavity's side face (its collider) a few mm behind the front, with hand noise.
            var a = Aim(new Vector3(0.2f, 0.35f, 0.575f), Vector3.right, new Vector3(0f, 0.004f, -0.003f));
            var b = Aim(new Vector3(0.8f, 0.35f, 0.575f), Vector3.left, new Vector3(0f, -0.003f, -0.004f));
            Assert.That(a.kind, Is.EqualTo(SnapKind.Edge).Or.EqualTo(SnapKind.Midpoint), $"left: {a.kind}");
            Assert.That(b.kind, Is.EqualTo(SnapKind.Edge).Or.EqualTo(SnapKind.Midpoint), $"right: {b.kind}");
            Assert.AreEqual(0.2f, a.point.x, 1e-4f, "on the left jamb");
            Assert.AreEqual(0.58f, a.point.z, 1e-4f);
            Assert.AreEqual(0.8f, b.point.x, 1e-4f, "on the right jamb");
            Assert.AreEqual(0.6f, Vector3.Distance(a.point, b.point), 0.002f, "the width of the gap");
            // Nothing snaps to the removed door's 1-cm-in rectangle any more.
            foreach (var k in layer.Corners) Assert.IsFalse(Mathf.Abs(k.p.x - 0.21f) < 1e-3f || Mathf.Abs(k.p.x - 0.79f) < 1e-3f, $"corner {k.id} on the door");
            foreach (var x in layer.Edges) Assert.IsFalse(Mathf.Abs(x.a.x - 0.21f) < 1e-3f && Mathf.Abs(x.b.x - 0.21f) < 1e-3f, $"edge {x.id} on the door");
        }

        /// Into the gap, a tape from the back wall to the front plane reads the depth off the cavity's faces.
        [Test]
        public void TapeIntoTheGap_ReadsTheDepth()
        {
            var layer = Composed();
            var settings = StructureSnapper.Settings.Default;
            var eye = new Vector3(0.5f, 1.3f, 1.8f);
            // The back wall's centre (a plane snap on the cavity's back), then the floor lip at the front (an edge).
            var back = new Vector3(0.5f, 0.3f, 0f);
            var r = StructureSnapper.Snap(layer, new StructureSnapper.Query { origin = eye, dir = (back - eye).normalized, meshHit = back, meshNormal = Vector3.forward }, settings);
            Assert.AreEqual(SnapKind.Plane, r.kind);
            Assert.AreEqual(0f, r.point.z, 1e-4f);
            var lip = new Vector3(0.5f, 0.003f, 0.576f);
            var f = StructureSnapper.Snap(layer, new StructureSnapper.Query { origin = eye, dir = (lip - eye).normalized, meshHit = lip, meshNormal = Vector3.up }, settings);
            Assert.That(f.kind, Is.EqualTo(SnapKind.Edge).Or.EqualTo(SnapKind.Midpoint));
            Assert.AreEqual(0.58f, f.point.z, 1e-3f);
            Assert.AreEqual(0f, f.point.y, 1e-3f);
        }
    }

    /// place_part: the pose maths (X flip, axes → rotation, front-bottom-centre), the tolerant args and the server's
    /// fits / clearance → the fit colours. Pure: offline runner.
    public class PlacePartTests
    {
        static string J(string s) => s.Replace('\'', '"');
        static void Near(Vector3 want, Vector3 got, float tol, string what) =>
            Assert.Less(Vector3.Distance(want, got), tol, $"{what}: want {want.ToString("F4")} got {got.ToString("F4")}");

        [TearDown]
        public void Units() => AirTools.UI.UiSettings.UseUnits(AirTools.UI.UiSettings.DefaultUnits);

        static AirTools.Parts.PartSpec Spec(float w, float d, float h, string face) => new AirTools.Parts.PartSpec
        {
            id = "p", name = "part", dims_mm = new AirTools.Parts.PartDims { w = w, d = d, h = h }, mount = new AirTools.Parts.PartMount { face = face },
        };

        [Test]
        public void GltfQuaternion_XFlip()
        {
            // glTF: +90° about +Y turns +Z into +X. The same turn seen in Unity (X negated) takes +Z to −X.
            float s = Mathf.Sqrt(0.5f);
            var q = AirTools.Parts.PlacePartMath.GltfToUnity(0, s, 0, s);
            Near(new Vector3(-1f, 0f, 0f), q * Vector3.forward, 1e-5f, "glTF +90° yaw in Unity");
            Near(Vector3.up, q * Vector3.up, 1e-5f, "stays upright");
            // A glTF tilt about +X keeps its axis (x is the mirror's own axis): +Y goes to +Z in both.
            var t = AirTools.Parts.PlacePartMath.GltfToUnity(s, 0, 0, s);
            Near(new Vector3(0f, 0f, 1f), t * Vector3.up, 1e-5f, "glTF +90° about X");
            Near(new Vector3(0f, 1f, 0f), AirTools.Parts.PlacePartMath.GltfToUnity(0, 0, 0, 2) * Vector3.up, 1e-5f, "normalised");
        }

        [Test]
        public void FromBasis_And_UprightFacing()
        {
            foreach (var outward in new[] { new Vector3(-0.6f, 0f, 0.8f), new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, -1f), new Vector3(0.3f, -0.2f, 0.9f) })
            {
                var q = AirTools.Parts.PlacePartMath.UprightFacing(outward, Vector3.up);
                var flat = new Vector3(outward.x, 0f, outward.z).normalized;
                Near(flat, q * Vector3.forward, 1e-5f, $"+Z faces {outward} (flattened)");
                Near(Vector3.up, q * Vector3.up, 1e-5f, "gravity-aligned");
                Near(Vector3.Cross(Vector3.up, flat), q * Vector3.right, 1e-5f, "+X = Cross(up, forward)");
                Assert.AreEqual(1f, q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w, 1e-4f, "unit quaternion");
            }
            var straightUp = AirTools.Parts.PlacePartMath.UprightFacing(Vector3.up, Vector3.up);
            Near(Vector3.forward, straightUp * Vector3.forward, 1e-5f, "a vertical 'out' keeps +Z");
        }

        [Test]
        public void CavityAxes_ToRotation_FacesOutOfTheOpening()
        {
            // The kitchen fixture's dw1: d = (0.6, 0, 0.8) in glTF → (−0.6, 0, 0.8) in Unity.
            var doc = ScenePartsDoc.Parse(ScenePartsParseTests.Kitchen4);
            Assert.IsTrue(CavityBox.TryFrom(doc.Find("dw1"), out var box, out _));
            var q = AirTools.Parts.PlacePartMath.UprightFacing(box.D, box.U);
            Near(box.D, q * Vector3.forward, 1e-5f, "the part's front faces out of the opening");
            Near(box.U, q * Vector3.up, 1e-5f, "up");
            Near(-box.R, q * Vector3.right, 1e-5f, "its own right is the viewer's left");
        }

        [Test]
        public void FrontBottomCentre_GoesAtInsertP_ForEveryMount()
        {
            var doc = ScenePartsDoc.Parse(ScenePartsParseTests.Kitchen4);
            Assert.IsTrue(CavityBox.TryFrom(doc.Find("dw1"), out var box, out _));
            var q = AirTools.Parts.PlacePartMath.UprightFacing(box.D, box.U);
            foreach (var face in new[] { "-z", "-y", "+x", "+z" })
            {
                var spec = Spec(420f, 430f, 580f, face);   // a dishwasher that fits the 437 × 591 × 445 gap
                var local = AirTools.Parts.PartMath.LocalBox(spec);
                var origin = AirTools.Parts.PlacePartMath.OriginFor(box.P, q, local, atOrigin: false);
                // The part's box, in the cavity's rud frame.
                var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue); var hi = -lo;
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) != 0 ? local.max.x : local.min.x, (i & 2) != 0 ? local.max.y : local.min.y, (i & 4) != 0 ? local.max.z : local.min.z);
                    var rud = box.Rud(origin + q * c);
                    lo = Vector3.Min(lo, rud); hi = Vector3.Max(hi, rud);
                }
                var pr = box.Rud(box.P);
                Assert.AreEqual(pr.x, (lo.x + hi.x) * 0.5f, 1e-4f, $"{face}: centred across the opening");
                Assert.AreEqual(pr.y, lo.y, 1e-4f, $"{face}: on the cavity floor");
                Assert.AreEqual(pr.z, hi.z, 1e-4f, $"{face}: front flush with the opening");
                Assert.AreEqual(0.42f, hi.x - lo.x, 1e-4f, $"{face}: its width across");
                Assert.AreEqual(0.43f, hi.z - lo.z, 1e-4f, $"{face}: its depth into the gap");
                Assert.AreEqual(0.58f, hi.y - lo.y, 1e-4f, $"{face}: its height");
                Assert.IsTrue(box.Inside(origin + q * local.center, 0.001f, 0f), $"{face}: inside the gap");
            }
            var atOrigin = AirTools.Parts.PlacePartMath.OriginFor(box.P, q, AirTools.Parts.PartMath.LocalBox(Spec(1, 1, 1, "-z")), atOrigin: true);
            Near(box.P, atOrigin, 1e-6f, "anchor 'origin': the model's origin goes at p");
        }

        [Test]
        public void Pose_Forms_AllInPackageSpace()
        {
            Assert.IsTrue(AirTools.Parts.PlacePartPose.TryParse(JToken.Parse("[1.0, 0.5, 2.0]"), out var a));
            Near(new Vector3(-1f, 0.5f, 2f), a.Position, 1e-6f, "array: X flipped");
            Assert.IsFalse(a.HasRotation);
            float s = Mathf.Sqrt(0.5f);
            string ss = s.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var b = PoseOf(J("{'p': [1, 0, 2], 'q': [0, " + ss + ", 0, " + ss + "]}"));
            Near(new Vector3(-1f, 0f, 2f), b.Position, 1e-6f, "p");
            Near(new Vector3(-1f, 0f, 0f), b.Rotation * Vector3.forward, 1e-5f, "q: glTF +90° yaw → Unity −X");
            var c = PoseOf(J("{'position': [0, 0, 0], 'yaw_deg': 90}"));
            Near(new Vector3(-1f, 0f, 0f), c.Rotation * Vector3.forward, 1e-5f, "yaw_deg agrees with q");
            var d = PoseOf(J("{'p': [0, 0, 0], 'axes': [[0.8, 0, -0.6], [0, 1, 0], [0.6, 0, 0.8]]}"));
            Near(new Vector3(-0.6f, 0f, 0.8f), d.Rotation * Vector3.forward, 1e-5f, "axes: +Z = d, X flipped");
            var e = PoseOf(J("{'pos': [0, 0, 0], 'forward': [0, 0.2, 1], 'anchor': 'origin'}"));
            Near(new Vector3(0f, 0f, 1f), e.Rotation * Vector3.forward, 1e-5f, "a tilted facing stands upright");
            Assert.IsTrue(e.AtOrigin);
            var f = PoseOf("[0, 0, 0, 0, " + s.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", 0, " + s.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]");
            Assert.IsTrue(f.HasRotation, "7 numbers: p + q");
            Assert.IsFalse(AirTools.Parts.PlacePartPose.TryParse(JToken.Parse(J("{'q': [0, 0, 0, 1]}")), out _), "no position: no pose");
            Assert.IsFalse(AirTools.Parts.PlacePartPose.TryParse(JToken.Parse(J("{'p': [1, 'x', 2]}")), out _));
            Assert.IsFalse(AirTools.Parts.PlacePartPose.TryParse(null, out _));
        }

        static AirTools.Parts.PlacePartPose PoseOf(string json)
        {
            Assert.IsTrue(AirTools.Parts.PlacePartPose.TryParse(JToken.Parse(json.Replace(",", ", ")), out var p), json);
            return p;
        }

        [Test]
        public void Args_AreTolerant()
        {
            var a = AirTools.Parts.PlacePartArgs.Parse(JObject.Parse(J("{'part_id': 'midea-1', 'model_url': '/parts/midea-1/model.glb', 'pose': {'p': [0, 0, 1]}, 'fits': true, 'clearance_mm': {'w': 17, 'h': 11, 'd': 15}}")));
            Assert.AreEqual("midea-1", a.PartId);
            Assert.AreEqual("/parts/midea-1/model.glb", a.ModelUrl);
            Assert.IsTrue(a.Pose.Valid);
            Assert.AreEqual(JTokenType.Boolean, a.Fits.Type);
            var b = AirTools.Parts.PlacePartArgs.Parse(JObject.Parse(J("{'part': {'id': 'x-2'}, 'pose': null, 'component_id': 'dw1'}")));
            Assert.AreEqual("x-2", b.PartId);
            Assert.IsFalse(b.Pose.Valid, "no pose: the cavity insert");
            Assert.AreEqual("dw1", b.ComponentId);
            Assert.IsNull(AirTools.Parts.PlacePartArgs.Parse(null).PartId);
            Assert.AreEqual("midea-123", AirTools.Parts.PartLoader.IdFromUrl("/parts/midea-123/model.glb?v=2"));
            Assert.AreEqual("thing", AirTools.Parts.PartLoader.IdFromUrl("https://cdn/x/thing.glb"));
        }

        [Test]
        public void ServerFit_ToTheFitColoursAndWords()
        {
            AirTools.UI.UiSettings.UseUnits(AirTools.Core.UnitSystem.Metric);
            var ok = AirTools.Parts.PlacePartFit.From(JToken.Parse("true"), JToken.Parse(J("{'w': 17, 'h': 11, 'd': 15}")));
            Assert.AreEqual(AirTools.Parts.FitStatus.Green, ok.Status);
            Assert.AreEqual(11f, ok.SpareMm.Value, 1e-6f, "the tightest side");
            Assert.AreEqual("Fits the gap · 11 mm spare", ok.Verdict);
            Assert.AreEqual("✓ Fits the gap · 11 mm spare", AirTools.UI.Copy.FitLine(ok));
            StringAssert.Contains("tightest=11 (h)", ok.Headline);
            var big = AirTools.Parts.PlacePartFit.From(JToken.Parse("false"), JToken.Parse("-30"));
            Assert.AreEqual(AirTools.Parts.FitStatus.Red, big.Status);
            Assert.AreEqual("Too big for the gap · 30 mm over", big.Verdict);
            var word = AirTools.Parts.PlacePartFit.From(JToken.Parse(J("'too_big'")), null);
            Assert.AreEqual(AirTools.Parts.FitStatus.Red, word.Status);
            Assert.AreEqual("Doesn't fit the gap", word.Verdict);
            var tight = AirTools.Parts.PlacePartFit.From(JToken.Parse(J("{'status': 'tight', 'spare_mm': 3, 'axis': 'w'}")), null);
            Assert.AreEqual(AirTools.Parts.FitStatus.Amber, tight.Status);
            var fromClearance = AirTools.Parts.PlacePartFit.From(null, JToken.Parse("[20, -4, 9]"));
            Assert.AreEqual(AirTools.Parts.FitStatus.Red, fromClearance.Status, "no fits: a negative clearance means it doesn't");
            Assert.AreEqual(-4f, fromClearance.SpareMm.Value, 1e-6f);
            Assert.IsNull(AirTools.Parts.PlacePartFit.From(null, null), "nothing said: the app checks it itself");
            Assert.IsNull(AirTools.Parts.PlacePartFit.From(JToken.Parse(J("'unknown'")), null));
            AirTools.UI.UiSettings.UseUnits(AirTools.Core.UnitSystem.Imperial);
            Assert.AreEqual("Fits the gap · ½″ spare", AirTools.Parts.PlacePartFit.From(JToken.Parse("true"), JToken.Parse("12")).Verdict);
        }
    }

    /// The synthetic parts package's files (ScenePartsFixtures, written by AirTools ▸ Export Synthetic Scene Packages as
    /// synthetic-facade-parts) read back through the app's own parsers. Pure: offline runner.
    public class SyntheticPartsPackageTests
    {
        static AirTools.Dev.ScenePartsFixtures.RunBox Run(string id) => AirTools.Dev.ScenePartsFixtures.Run().First(b => b.id == id);

        [Test]
        public void PartsJson_ParsesAndPutsEachCavityWhereItsBoxWas()
        {
            var doc = ScenePartsDoc.Parse(AirTools.Dev.ScenePartsFixtures.PartsJson());
            CollectionAssert.AreEqual(new[] { "cab1", "cab2" }, doc.components.Select(c => c.id).ToArray());
            Assert.AreEqual("mesh.parts.r1.glb", doc.mesh);
            Assert.AreEqual("cavity.r1.glb", doc.cavities);
            Assert.AreEqual("collision.parts.r1.glb", doc.collision);
            foreach (var c in doc.components)
            {
                var b = Run(c.id);
                Assert.IsTrue(CavityBox.TryFrom(c, out var box, out var why), why);
                Assert.IsFalse(box.BoxWasRelative);
                var lo = Vector3.Min(box.Corner(0), box.Corner(7)); var hi = Vector3.Max(box.Corner(0), box.Corner(7));
                Assert.Less(Vector3.Distance(b.box.min, lo), 1e-5f, $"{c.id} min {lo} vs {b.box.min}");
                Assert.Less(Vector3.Distance(b.box.max, hi), 1e-5f, $"{c.id} max {hi} vs {b.box.max}");
                Assert.AreEqual(b.box.size.x, box.Width, 1e-5f);
                Assert.AreEqual(b.box.size.y, box.Height, 1e-5f);
                Assert.AreEqual(b.box.size.z, box.Depth, 1e-5f);
                Assert.Less(Vector3.Distance(new Vector3(b.box.center.x, 0f, b.box.max.z), box.P), 1e-5f, "insert.p: floor, front, centre");
                Assert.AreEqual(Vector3.forward, box.D, "out of the opening = +Z (the wall faces +Z)");
                CollectionAssert.AreEqual(new[] { b.objectId }, c.structure_objects);
            }
            Assert.AreEqual(0.6f, Run("cab1").box.size.x, 1e-6f, "cab1 is a 600 mm opening");
        }

        [Test]
        public void SceneJson_NamesThePartsFiles()
        {
            var json = AirTools.Dev.PackageFixtures.SceneJson(AirTools.Dev.ScenePartsFixtures.Site, 1, "full", new Vector3(0, 1.6f, 4), Vector3.zero, true,
                parts: AirTools.Dev.ScenePartsFixtures.PartsEntry(1, 2));
            var m = SceneManifest.Parse(json);
            Assert.IsTrue(m.HasParts);
            Assert.AreEqual("parts.r1.json", m.PartsFile);
            Assert.AreEqual(2, m.parts.components);
            Assert.AreEqual("mesh.r1.glb", m.MeshFile, "the one-piece mesh stays for older apps");
            var f = ScenePartsFiles.For(m, ScenePartsDoc.Parse(AirTools.Dev.ScenePartsFixtures.PartsJson()));
            Assert.AreEqual("mesh.parts.r1.glb", f.Mesh);
            Assert.AreEqual("collision.parts.r1.glb", f.Collision);
            Assert.AreEqual("cavity.r1.glb", f.Cavities);
            Assert.IsFalse(SceneManifest.Parse(AirTools.Dev.PackageFixtures.SceneJson("synthetic-facade", 1, "full", Vector3.zero, Vector3.zero, true)).HasParts,
                "the other synthetic packages stay one piece");
        }

        [Test]
        public void CavityMesh_FiveFlatFacesBothWindings_LinearColours()
        {
            var v = new System.Collections.Generic.List<Vector3>(); var c = new System.Collections.Generic.List<Color>(); var t = new System.Collections.Generic.List<int>();
            AirTools.Dev.ScenePartsFixtures.CavityMesh(Run("cab1"), 1f, v, c, t);
            Assert.AreEqual(20, v.Count, "5 quads");
            Assert.AreEqual(v.Count, c.Count, "a colour per vertex (COLOR_0)");
            Assert.AreEqual(5 * 4 * 3, t.Count, "both windings");
            var bb = Run("cab1").box;
            foreach (var p in v)
                for (int i = 0; i < 3; i++) Assert.That(p[i], Is.InRange(bb.min[i] - 1e-6f, bb.max[i] + 1e-6f), "on the box");
            // Linear, like the backend's linear(): sRGB 150 → 0.305 (not 0.588).
            Assert.AreEqual(0.3049873f, AirTools.Dev.ScenePartsFixtures.SrgbToLinear(150f / 255f), 1e-5f);
            Assert.AreEqual(0f, AirTools.Dev.ScenePartsFixtures.SrgbToLinear(0f), 1e-7f);
            Assert.AreEqual(1f, AirTools.Dev.ScenePartsFixtures.SrgbToLinear(1f), 1e-6f);
            Assert.Less(AirTools.Dev.ScenePartsFixtures.BackColor.r, 150f / 255f, "stored linear (darker numbers than the sRGB swatch)");
        }
    }

    /// The whole path on the facade with the synthetic run, in EditMode (Editor gate: physics, the real measure tool):
    /// take cab1 out, then tape its opening with the real tool through ToolInputHub → SnapService → the edited layer.
    public class ScenePartsTapeTests
    {
        GameObject m_RootGo, m_Content, m_App, m_Rig;
        SceneRoot m_Root;
        SceneParts m_Parts;
        AirTools.Input.ToolInputHub m_Hub;
        AirTools.Tools.MeasureTool m_Tool;

        [SetUp]
        public void Build()
        {
            AirTools.UI.UiSettings.UseUnits(AirTools.Core.UnitSystem.Metric);
            AirTools.Notes.Notebook.Clear();
            AirTools.Core.AppState.Reset();
            AirTools.UI.WorldLabels.Reset();
            m_RootGo = new GameObject("SceneRoot");
            m_Root = m_RootGo.AddComponent<SceneRoot>();
            m_Content = new GameObject("Content");
            m_Content.transform.SetParent(m_RootGo.transform, false);
            var facade = AirTools.Editor.SyntheticFacadeBuilder.CreateHierarchy();
            facade.transform.SetParent(m_Content.transform, false);
            var run = new GameObject("Run").transform;
            run.SetParent(facade.transform, false);
            foreach (var b in AirTools.Dev.ScenePartsFixtures.Run())
                AirTools.Editor.SyntheticFacadeBuilder.Box(b.name, run, b.box, null, null).layer = SceneLayers.SceneSurface;
            var layer = StructureLayer.Parse(AirTools.Dev.PackageFixtures.StructureJson(facade.transform, 1f, AirTools.Dev.ScenePartsFixtures.Objects()));
            var cavities = new GameObject("Cavities r1");
            cavities.transform.SetParent(m_Content.transform, false);
            var v = new System.Collections.Generic.List<Vector3>(); var c = new System.Collections.Generic.List<Color>(); var t = new System.Collections.Generic.List<int>();
            foreach (var b in AirTools.Dev.ScenePartsFixtures.Run())
            {
                if (!b.removable) continue;
                AirTools.Dev.ScenePartsFixtures.CavityMesh(b, 1f, v, c, t);
                var mesh = new Mesh(); mesh.SetVertices(v); mesh.SetColors(c); mesh.SetTriangles(t, 0);
                var go = new GameObject($"cavity_{b.id}") { layer = SceneLayers.SceneSurface };
                go.transform.SetParent(cavities.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
            }
            m_Root.SetRuntimeContent(AirTools.Dev.ScenePartsFixtures.Site, SceneManifest.Parse(ScenePartsParseTests.ApiMdScene), null, m_Content, layer);
            AirTools.Core.Services.Register(m_Root);
            m_App = new GameObject("App");
            m_Parts = m_App.AddComponent<SceneParts>();
            m_Parts.sceneRoot = m_Root;
            AirTools.Core.Services.Register(m_Parts);
            AirTools.Tools.EditHistory.Register(m_Parts);
            // The one-piece facade doubles as visual and collider (the split mesh's nodes are the run's boxes).
            m_Parts.Bind(AirTools.Dev.ScenePartsFixtures.Site, 1, ScenePartsDoc.Parse(AirTools.Dev.ScenePartsFixtures.PartsJson()), facade, facade, cavities);
            m_Rig = new GameObject("tool-test");
            m_Hub = m_Rig.AddComponent<AirTools.Input.ToolInputHub>();
            m_Tool = m_Rig.AddComponent<AirTools.Tools.MeasureTool>();
            m_Tool.SetInput(m_Hub);
            m_Tool.Equip(true);
            Physics.SyncTransforms();
        }

        [TearDown]
        public void Teardown()
        {
            if (m_Tool != null && m_Tool.viewRoot != null) UnityEngine.Object.DestroyImmediate(m_Tool.viewRoot.gameObject);
            AirTools.Core.Services.Unregister(m_Parts);
            AirTools.Core.Services.Unregister(m_Root);
            AirTools.Tools.EditHistory.Unregister(m_Parts);
            UnityEngine.Object.DestroyImmediate(m_Rig);
            UnityEngine.Object.DestroyImmediate(m_App);
            UnityEngine.Object.DestroyImmediate(m_RootGo);
            AirTools.Notes.Notebook.Clear();
            AirTools.Core.AppState.Reset();
            AirTools.UI.WorldLabels.Reset();
            AirTools.UI.UiSettings.UseUnits(AirTools.UI.UiSettings.DefaultUnits);
        }

        AirTools.Dev.ScenarioResult Tape(Func<string, CavityBox, Func<Vector3, Vector3>, float, float, AirTools.Dev.MeasureScenario> make, string id, float expect, int seed)
        {
            Assert.IsTrue(CavityBox.TryFrom(m_Parts.Find(id), out var box, out var why), why);
            return AirTools.Dev.MeasureScenarios.Run(make(id, box, m_Root.PackageToRoot, expect, 0.003f), m_Tool, m_Hub, m_RootGo.transform, seed, removeAfter: true);
        }

        [Test]
        public void Cab1Out_TheRealTapeReadsTheOpening([Values(1, 2, 3)] int seed)
        {
            Assert.IsTrue(m_Parts.Remove("cab1"));
            var w = Tape(AirTools.Dev.ScenePartsFixtures.Width, "cab1", 0.6f, seed);
            Assert.IsTrue(w.Passed, w.ToString());
            var d = Tape(AirTools.Dev.ScenePartsFixtures.Depth, "cab1", 0.58f, seed);
            Assert.IsTrue(d.Passed, d.ToString());
            var h = Tape(AirTools.Dev.ScenePartsFixtures.Height, "cab1", 0.82f, seed);
            Assert.IsTrue(h.Passed, h.ToString());
        }

        [Test]
        public void Cab2Out_ItsOpenSideIsTheCavitysOwnFace()
        {
            // cab2's viewer's left (x 1.6) has no neighbour: the jamb there is the estimated cavity face.
            Assert.IsTrue(m_Parts.Remove("cab2"));
            var w = Tape(AirTools.Dev.ScenePartsFixtures.Width, "cab2", 0.9f, 1);
            Assert.IsTrue(w.Passed, w.ToString());
        }

        [Test]
        public void Cab1In_TheSameTapeStopsOnItsFront()
        {
            // Before it comes out, the tape's rays land on the cabinet's front, not the jambs behind it.
            var w = Tape(AirTools.Dev.ScenePartsFixtures.Width, "cab1", 0.6f, 1);
            StringAssert.Contains("part_cab1", w.Detail, "the rays hit the cabinet");
        }
    }
}
