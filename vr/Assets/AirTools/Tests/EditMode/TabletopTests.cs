using System.Linq;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Editor;
using AirTools.Input;
using AirTools.Notes;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Tests
{
    /// SPEC M7 tabletop: the scene at 1:50 on a table; the tape still reads scene metres. modelwheel: Model view floats
    /// the model in front of you by default (ModelViewSceneTests); the tape, array and snapping checks hold wherever the
    /// model is, and the table placement is checked here as the option it now is.
    public class TabletopTests
    {
        GameObject m_Root, m_Rig, m_Head, m_App;
        SceneRoot m_Scene;
        TabletopController m_Table;
        ToolInputHub m_Hub;
        MeasureTool m_Measure;

        [SetUp]
        public void SetUp()
        {
            Notebook.Clear();
            AppState.Reset();
            m_Root = new GameObject("SceneRoot");
            m_Scene = m_Root.AddComponent<SceneRoot>();
            var facade = SyntheticFacadeBuilder.CreateHierarchy();
            facade.transform.SetParent(m_Root.transform, false);
            m_Scene.SetContent(null, facade);
            m_Rig = new GameObject("rig");
            m_Rig.transform.SetPositionAndRotation(S.SpawnPosition, Quaternion.Euler(0f, S.SpawnYawDeg, 0f));
            m_Head = new GameObject("head");
            m_Head.transform.SetParent(m_Rig.transform, false);
            m_Head.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            m_App = new GameObject("app");
            m_Table = m_App.AddComponent<TabletopController>();
            m_Table.root = m_Scene; m_Table.rig = m_Rig.transform; m_Table.head = m_Head.transform;
            m_Table.fitToTable = false;   // modelview: these checks are about the fixed 1:50 (ModelViewSceneTests covers the fit)
            m_Hub = m_App.AddComponent<ToolInputHub>();
            m_Measure = m_App.AddComponent<MeasureTool>();
            m_Measure.frame = m_Root.transform;
            m_Measure.SetInput(m_Hub);
            m_Measure.Equip(true);
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            m_Table.Apply(false);
            if (m_Measure.viewRoot != null) Object.DestroyImmediate(m_Measure.viewRoot.gameObject);
            Object.DestroyImmediate(m_App); Object.DestroyImmediate(m_Rig); Object.DestroyImmediate(m_Root);
            SnapService.Scale = 1f;
            Notebook.Clear();
            AppState.Reset();
        }

        void ClickScene(Vector3 eyeLocal, Vector3 targetLocal)
        {
            var t = m_Root.transform;
            var p = ToolInputHub.RayPose(t.TransformPoint(eyeLocal), t.TransformPoint(targetLocal));
            m_Hub.RaisePressStart(ToolHand.Right, p);
            m_Hub.RaisePressEnd(ToolHand.Right, p);
        }

        [Test]
        public void ModelSitsOnTheTableAtOneFiftieth()
        {
            m_Table.placement = ModelPlacement.OnTable;   // modelwheel: the table is an option now (default: in front of you)
            m_Table.Apply(true);
            Assert.IsTrue(m_Table.OnTable);
            Assert.AreEqual(0.02f, m_Root.transform.lossyScale.x, 1e-6f);
            Assert.AreEqual(0.02f, SnapService.Scale, 1e-6f);
            Assert.AreEqual(0.8f, m_Root.transform.position.y, 1e-4f, "scene ground (y = 0) on the table");
            var modelCentre = m_Root.transform.TransformPoint(Vector3.zero);
            Assert.That(Vector3.Distance(new Vector3(m_Head.transform.position.x, 0, m_Head.transform.position.z), new Vector3(modelCentre.x, 0, modelCentre.z)), Is.EqualTo(0.55f).Within(0.02f));
            m_Table.Apply(false);
            Assert.AreEqual(Vector3.one, m_Root.transform.localScale);
            Assert.AreEqual(Vector3.zero, m_Root.transform.position);
            Assert.AreEqual(1f, SnapService.Scale);
        }

        /// modelwheel: by default the 1:50 model floats at eye height in front of you, not on a table.
        [Test]
        public void ModelFloatsInFrontOfYouAtOneFiftiethByDefault()
        {
            m_Table.Apply(true);
            Assert.IsTrue(m_Table.OnTable && m_Table.Floating);
            Assert.AreEqual(0.02f, m_Root.transform.lossyScale.x, 1e-6f);
            var eye = m_Head.transform.position;
            var c = m_Table.ModelCentreWorld;
            Assert.AreEqual(eye.y - ModelViewLayout.DropBelowEye, c.y, 1e-4f, "the model's centre at eye height, a few cm below");
            Assert.That(Vector2.Distance(new Vector2(eye.x, eye.z), new Vector2(c.x, c.z)), Is.InRange(0.9f, 1.2f));
            Assert.Greater(m_Root.transform.position.y, 0.8f + 0.2f, "its ground well above a table");
            m_Table.Apply(false);
            Assert.AreEqual(Vector3.one, m_Root.transform.localScale);
            Assert.AreEqual(Vector3.zero, m_Root.transform.position);
        }

        [Test]
        public void TapeReadsTheSameMetresAtOneFiftieth()
        {
            m_Table.Apply(true);
            var eye = MeasureScenarios.SpawnEye;
            ClickScene(eye, new Vector3(-S.WindowWidth / 2, 4.1f, -0.05f));
            ClickScene(eye, new Vector3(S.WindowWidth / 2, 4.1f, -0.05f));
            m_Hub.RaiseButton(ToolHand.Right, ToolButton.Finish);
            var e = Notebook.Last;
            Assert.IsNotNull(e, m_Measure.LastAction);
            Assert.AreEqual("m", e.Unit);
            Assert.AreEqual(S.WindowWidth, e.ValueSI, 0.010, "1.500 m on a 3 cm wide model window");
        }

        [Test]
        public void ArrayUndoRedoAtOneFiftiethKeepsPartsOnTheModel()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath) ?? PartCatalogBuilder.Build();
            var loader = m_App.AddComponent<PartLoader>();
            loader.catalog = catalog;
            var tool = m_App.AddComponent<PartTool>();
            tool.frame = m_Root.transform;
            tool.SetInput(m_Hub);
            tool.Equip(true);
            var t = m_Root.transform;
            var hanger = loader.LoadFromCatalog(PartScenarios.Hanger);
            tool.Hold(hanger);
            Assert.IsTrue(tool.Release(ToolInputHub.RayPose(t.TransformPoint(new Vector3(0.3f, 7f, 1.5f)), t.TransformPoint(new Vector3(0.3f, 6.15f, S.FasciaProud)))), tool.LastAction);
            var before = t.InverseTransformPoint(hanger.transform.position);
            Notebook.Add(new NotebookEntry("measure", 4.2, "m", new[] { new Vector3(-2.1f, 6.1f, 0.15f), new Vector3(2.1f, 6.1f, 0.15f) }, System.DateTime.Now, -1, "gutter"));
            var g = tool.PlaceArray();
            Assert.IsNotNull(g, tool.LastAction);
            var slots = PartTool.ArrayMembers(g).Select(p => t.InverseTransformPoint(p.transform.position)).ToList();

            m_Table.Apply(true);
            tool.Undo();
            Assert.That(Vector3.Distance(t.InverseTransformPoint(hanger.transform.position), before), Is.LessThan(1e-4f), "undo at 1:50 puts the reference back on the model");
            tool.Redo();
            var members = PartTool.ArrayMembers(g);
            Assert.AreEqual(8, members.Count);
            for (int k = 0; k < members.Count; k++)
                Assert.That(Vector3.Distance(t.InverseTransformPoint(members[k].transform.position), slots[k]), Is.LessThan(1e-4f), $"#{k} redo at 1:50");
            m_Table.Apply(false);
            for (int k = 0; k < members.Count; k++)
                Assert.That(Vector3.Distance(members[k].transform.position, slots[k]), Is.LessThan(1e-4f), $"#{k} back at 1:1");
            tool.ClearAll();
        }

        [Test]
        public void SnappingScalesWithTheModel()
        {
            m_Table.Apply(true);
            // A click 3 cm (scene) off the window's top-left corner still snaps to the corner on the model.
            var eye = MeasureScenarios.SpawnEye;
            var t = m_Root.transform;
            var corner = new Vector3(-S.WindowWidth / 2, S.SillHeight + S.WindowHeight, -S.WindowRecess);
            var ray = new Ray(t.TransformPoint(eye), (t.TransformPoint(corner + new Vector3(0.03f, -0.03f, 0f)) - t.TransformPoint(eye)).normalized);
            Assert.IsTrue(SnapService.TryRaySnap(ray, out var hit));
            Assert.AreEqual(SnapKind.Corner, hit.kind);
            Assert.That(Vector3.Distance(t.InverseTransformPoint(hit.point), corner), Is.LessThan(0.002f));
        }
    }

    public class TakeItHomeTests
    {
        [Test]
        public void BoughtPartsAppearOnTheTableAtTrueSizeInPassthrough()
        {
            AppState.Reset();
            var catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath) ?? PartCatalogBuilder.Build();
            var rig = new GameObject("rig");
            var head = new GameObject("head"); head.transform.SetParent(rig.transform, false); head.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            var go = new GameObject("home");
            try
            {
                var loader = go.AddComponent<PartLoader>(); loader.catalog = catalog;
                var home = go.AddComponent<TakeItHome>();
                home.rig = rig.transform; home.head = head.transform; home.loader = loader;
                var spec = catalog.Spec(PartScenarios.Hanger);
                home.OnPurchased(new CheckoutReceipt { status = "AUTHORIZED", qty = 8, total_usd = 34.16f, seller = "Home Depot (mock)" }, spec);
                home.ShowOnTable();
                Assert.AreEqual(1, home.OnTable.Count, "one of each bought part");
                var p = home.OnTable[0];
                var size = p.MeasuredSizeMm();
                Assert.AreEqual(127f, size.x, 1f); Assert.AreEqual(45f, size.y, 1f); Assert.AreEqual(38f, size.z, 1f);
                Assert.AreEqual(1f, p.transform.lossyScale.x, 1e-5f, "true size");
                var bottom = p.transform.TransformPoint(new Vector3(0f, p.LocalBox.min.y, p.LocalBox.center.z));
                Assert.AreEqual(0.75f, bottom.y, 0.001f, "sits on the (assumed) table");
                Assert.That(Vector3.Angle(p.transform.up, Vector3.up), Is.LessThan(0.5f), "upright");
                Assert.AreEqual(8, home.Purchases[0].Qty);
                home.ClearTable();
                Assert.AreEqual(0, home.OnTable.Count);
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(rig); AppState.Reset(); }
        }
    }
}
