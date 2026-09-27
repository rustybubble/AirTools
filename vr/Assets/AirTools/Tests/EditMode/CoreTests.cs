using System.Collections.Generic;
using AirTools.Core;
using AirTools.Scene;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    public class AppStateTests
    {
        [SetUp] public void SetUp() => AppState.Reset();
        [TearDown] public void TearDown() => AppState.Reset();

        [Test]
        public void StartsInPassthrough() => Assert.AreEqual(AppMode.Passthrough, AppState.Mode);

        [Test]
        public void SetRaisesChangedOnceWithPreviousAndNext()
        {
            var events = new List<(AppMode, AppMode)>();
            AppState.Changed += (a, b) => events.Add((a, b));
            AppState.Set(AppMode.World);
            AppState.Set(AppMode.World);
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual((AppMode.Passthrough, AppMode.World), events[0]);
        }

        [Test]
        public void ChestCommandsToggleModes()
        {
            AppCommands.OpenChest();
            Assert.AreEqual(AppMode.World, AppState.Mode);
            AppCommands.CloseChest();
            Assert.AreEqual(AppMode.Passthrough, AppState.Mode);
            AppCommands.ToggleChest();
            Assert.AreEqual(AppMode.World, AppState.Mode);
            AppCommands.ToggleChest();
            Assert.AreEqual(AppMode.Passthrough, AppState.Mode);
        }

        [Test]
        public void ChestPressHasCooldown()
        {
            var go = new GameObject("chest");
            try
            {
                var chest = go.AddComponent<ChestController>();
                chest.Press();
                chest.Press();
                Assert.AreEqual(1, chest.PressCount);
                Assert.AreEqual(AppMode.World, AppState.Mode);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }

    public class ModeControllerTests
    {
        GameObject m_Root;
        ModeController m_Mode;
        SceneRoot m_Scene;
        Camera m_Camera;

        [SetUp]
        public void SetUp()
        {
            AppState.Reset();
            m_Root = new GameObject("mode-test");
            m_Scene = new GameObject("SceneRoot").AddComponent<SceneRoot>();
            m_Scene.transform.SetParent(m_Root.transform);
            var content = new GameObject("Content");
            content.transform.SetParent(m_Scene.transform);
            m_Scene.SetContent(null, content);
            m_Camera = new GameObject("Cam").AddComponent<Camera>();
            m_Camera.transform.SetParent(m_Root.transform);
            m_Mode = m_Root.AddComponent<ModeController>();
            m_Mode.sceneRoot = m_Scene;
            m_Mode.cameras = new[] { m_Camera };
            m_Mode.Subscribe();           // OnEnable doesn't run in EditMode
            m_Mode.ApplyVisuals(AppState.Mode);
        }

        [TearDown]
        public void TearDown()
        {
            m_Mode.Unsubscribe();
            Object.DestroyImmediate(m_Root);
            AppState.Reset();
        }

        [Test]
        public void PassthroughHidesSceneAndClearsTransparent()
        {
            Assert.IsFalse(m_Scene.IsVisible);
            Assert.AreEqual(CameraClearFlags.SolidColor, m_Camera.clearFlags);
            Assert.AreEqual(0f, m_Camera.backgroundColor.a);
        }

        [Test]
        public void OpeningReachesWorldWithinOneSecond()
        {
            AppState.Set(AppMode.World);
            float t = 0f;
            const float dt = 1f / 72f;
            bool sawBlack = false;
            while (m_Mode.IsTransitioning && t < 3f)
            {
                m_Mode.Tick(dt);
                t += dt;
                sawBlack |= m_Mode.FadeAlpha > 0.99f;
            }
            Assert.IsFalse(m_Mode.IsTransitioning);
            Assert.LessOrEqual(t, 1.0f + dt, "transition time");
            Assert.IsTrue(sawBlack, "fades fully to black at the swap");
            Assert.AreEqual(AppMode.World, m_Mode.VisualMode);
            Assert.IsTrue(m_Scene.IsVisible);
            Assert.AreEqual(CameraClearFlags.Skybox, m_Camera.clearFlags);
            Assert.AreEqual(0f, m_Mode.FadeAlpha);
        }

        [Test]
        public void SwapHappensOnlyAtTheMidpoint()
        {
            AppState.Set(AppMode.World);
            m_Mode.Tick(m_Mode.fadeOutSeconds * 0.5f);
            Assert.AreEqual(AppMode.Passthrough, m_Mode.VisualMode, "still passthrough while fading out");
            m_Mode.Tick(m_Mode.fadeOutSeconds);
            Assert.AreEqual(AppMode.World, m_Mode.VisualMode);
        }

        [Test]
        public void ChangingBackMidFadeEndsInLatestMode()
        {
            AppState.Set(AppMode.World);
            m_Mode.Tick(0.1f);
            AppState.Set(AppMode.Passthrough);
            for (int i = 0; i < 400 && m_Mode.IsTransitioning; i++) m_Mode.Tick(0.01f);
            Assert.AreEqual(AppMode.Passthrough, m_Mode.VisualMode);
            Assert.IsFalse(m_Scene.IsVisible);
        }
    }

    public class SceneLoaderTests
    {
        GameObject m_Root, m_Prefab, m_Rig;
        ScenePackage m_Package;
        SceneRoot m_SceneRoot;

        [SetUp]
        public void SetUp()
        {
            m_Root = new GameObject("loader-test");
            m_SceneRoot = new GameObject("SceneRoot").AddComponent<SceneRoot>();
            m_SceneRoot.transform.SetParent(m_Root.transform);
            m_Prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            m_Prefab.name = "Site";
            Object.DestroyImmediate(m_Prefab.GetComponent<Collider>());
            m_Rig = new GameObject("Rig");
            m_Package = ScriptableObject.CreateInstance<ScenePackage>();
            m_Package.visualPrefab = m_Prefab;
            m_Package.spawnPosition = new Vector3(0f, 0f, 4f);
            m_Package.spawnYawDeg = 180f;
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(m_Root);
            Object.DestroyImmediate(m_Prefab);
            Object.DestroyImmediate(m_Rig);
            Object.DestroyImmediate(m_Package);
        }

        [Test]
        public void LoadsUnderRootOnSceneSurfaceWithColliders()
        {
            var content = SceneLoader.Load(m_Package, m_SceneRoot, m_Rig.transform);
            Assert.AreEqual("Site", content.name);
            Assert.AreEqual(m_SceneRoot.transform, content.transform.parent);
            Assert.AreEqual(SceneLayers.SceneSurface, content.layer);
            Assert.IsNotNull(content.GetComponent<MeshCollider>());
            Assert.That(Vector3.Distance(m_Rig.transform.position, new Vector3(0f, 0f, 4f)), Is.LessThan(1e-4f));
            Assert.AreEqual(180f, m_Rig.transform.eulerAngles.y, 0.01f);
        }

        [Test]
        public void ZUpPackageIsLevelled()
        {
            m_Package.up = Vector3.forward; // e.g. a photogrammetry export with +Z up
            m_Package.spawnPosition = new Vector3(0f, 4f, 0f);
            var content = SceneLoader.Load(m_Package, m_SceneRoot, m_Rig.transform);
            Assert.Less(Vector3.Angle(content.transform.rotation * Vector3.forward, Vector3.up), 0.01f);
            Assert.AreEqual(0f, m_Rig.transform.position.y, 1e-4f, "package +Y offset lands on the floor plane");
        }

        [Test]
        public void NorthRotatesAboutUp()
        {
            m_Package.northDeg = 90f;
            var content = SceneLoader.Load(m_Package, m_SceneRoot, m_Rig.transform);
            Assert.Less(Vector3.Angle(content.transform.rotation * Vector3.forward, Vector3.right), 0.01f);
            Assert.That(Vector3.Distance(m_Rig.transform.position, new Vector3(4f, 0f, 0f)), Is.LessThan(1e-4f));
        }

        [Test]
        public void InvisibleCollisionMeshKeepsVisualRenderers()
        {
            var collision = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                m_Package.collisionPrefab = collision;
                var content = SceneLoader.Load(m_Package, m_SceneRoot);
                var col = content.transform.Find("Collision");
                Assert.IsNotNull(col);
                Assert.IsFalse(col.GetComponent<Renderer>().enabled);
                Assert.IsTrue(content.GetComponent<Renderer>().enabled);
                Assert.AreEqual(SceneLayers.SceneSurface, col.gameObject.layer);
            }
            finally { Object.DestroyImmediate(collision); }
        }
    }

    public class LogTests
    {
        [Test]
        public void CheckLineMatchesSpecFormat()
        {
            Assert.AreEqual("[AirTools.Check] M2.tape.window value=1.5003 expected=1.500 tol=0.010 PASS",
                Log.FormatCheck("M2.tape.window", 1.5003, 1.5, 0.010, true));
        }
    }
}
