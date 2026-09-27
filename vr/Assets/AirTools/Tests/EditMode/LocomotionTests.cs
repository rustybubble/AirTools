using AirTools.Core;
using AirTools.Input;
using AirTools.UI;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    public class LocomotionTests
    {
        GameObject m_Rig, m_Head, m_Go;
        Locomotion m_Loco;
        ToolInputHub m_Hub;

        [SetUp]
        public void SetUp()
        {
            AppState.Reset();
            AppState.Set(AppMode.World);
            m_Rig = new GameObject("rig");
            m_Rig.transform.position = new Vector3(0f, 0f, 4f);
            m_Rig.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            m_Head = new GameObject("head");
            m_Head.transform.SetParent(m_Rig.transform, false);
            m_Head.transform.localPosition = new Vector3(0.1f, 1.6f, 0.2f);
            m_Go = new GameObject("loco");
            m_Hub = m_Go.AddComponent<ToolInputHub>();
            m_Loco = m_Go.AddComponent<Locomotion>();
            m_Loco.rig = m_Rig.transform; m_Loco.head = m_Head.transform;
            m_Loco.SetInput(m_Hub);
            m_Loco.Equip(true);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(m_Go); Object.DestroyImmediate(m_Rig);
            AppState.Reset();
        }

        [Test]
        public void TeleportPutsYourFeetOnThePointUnderYourHead()
        {
            m_Loco.TeleportTo(new Vector3(-2.5f, 6.5f, -0.15f));   // the top of the wall
            Assert.AreEqual(6.5f, m_Rig.transform.position.y, 1e-4f, "feet on the surface");
            Assert.AreEqual(-2.5f, m_Head.transform.position.x, 1e-4f);
            Assert.AreEqual(-0.15f, m_Head.transform.position.z, 1e-4f);
        }

        [Test]
        public void GrabDragMovesYouOppositeYourHand()
        {
            var start = m_Rig.transform.position;
            // Pull the hand 10 cm down (world) and 10 cm toward you: you rise and move forward, ×2 gain.
            var hand0 = m_Rig.transform.TransformPoint(new Vector3(0.2f, 1.2f, 0.4f));
            m_Hub.RaisePressStart(ToolHand.Right, new Pose(hand0, Quaternion.identity));
            var hand1 = m_Rig.transform.TransformPoint(new Vector3(0.2f, 1.1f, 0.3f));
            m_Hub.RaisePressMove(ToolHand.Right, new Pose(hand1, Quaternion.identity));
            var moved = m_Rig.transform.position - start;
            Assert.AreEqual(0.2f, moved.y, 1e-4f, "rise 20 cm");
            Assert.AreEqual(0.2f, Vector3.Dot(moved, m_Rig.transform.forward), 1e-4f, "forward 20 cm");
        }

        [Test]
        public void QuickTapOnTheGroundTeleportsTallWallDoesNot()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.layer = AirTools.Scene.SceneLayers.SceneSurface;
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            Physics.SyncTransforms();
            try
            {
                var eye = m_Head.transform.position;
                Assert.IsTrue(m_Loco.TeleportAlong(new Pose(eye, Quaternion.LookRotation(new Vector3(0f, -1f, -1f)))));
                Assert.AreEqual(0f, m_Rig.transform.position.y, 1e-3f);
                Assert.IsFalse(m_Loco.TeleportAlong(new Pose(m_Head.transform.position, Quaternion.LookRotation(Vector3.up))), "nothing up there");
            }
            finally { Object.DestroyImmediate(ground); }
        }

        /// UX W0.2: a quick pinch at a countertop or a wall never puts you on it — only floor-like spots (facing up,
        /// ≤ 0.3 m above the floor) — and a refused pinch says so.
        [Test]
        public void CounterAndWallPinchesDoNotTeleportAndSayWhy()
        {
            AirTools.UI.InputHints.Reset();
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.layer = AirTools.Scene.SceneLayers.SceneSurface;
            ground.transform.position = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            var counter = GameObject.CreatePrimitive(PrimitiveType.Cube);
            counter.layer = AirTools.Scene.SceneLayers.SceneSurface;
            counter.transform.position = new Vector3(0f, 0.45f, 2.5f);          // top at 0.90 m, in front of you
            counter.transform.localScale = new Vector3(2f, 0.9f, 0.6f);
            Physics.SyncTransforms();
            try
            {
                var eye = m_Head.transform.position;
                var start = m_Rig.transform.position;
                Assert.IsFalse(m_Loco.TeleportAlong(ToolInputHub.RayPose(eye, new Vector3(0f, 0.9f, 2.5f))), "the counter top faces up but isn't floor");
                Assert.AreEqual(start, m_Rig.transform.position);
                Assert.AreEqual("Aim at the floor to walk", AirTools.UI.InputHints.Last);
                Assert.IsFalse(m_Loco.TeleportAlong(ToolInputHub.RayPose(eye, new Vector3(0f, 0.45f, 2.2f))), "the counter's front is a wall");
                Assert.IsTrue(Locomotion.IsFloor(Vector3.up, 0.02f));
                Assert.IsFalse(Locomotion.IsFloor(Vector3.up, 0.9f));
                Assert.IsFalse(Locomotion.IsFloor(new Vector3(0f, 0.8f, 0.6f).normalized, 0f), "a ramp steeper than ~25° isn't floor");
                Assert.IsTrue(m_Loco.TeleportAlong(ToolInputHub.RayPose(eye, new Vector3(2.5f, 0f, 3.5f))), "the floor beside it is");
            }
            finally { Object.DestroyImmediate(ground); Object.DestroyImmediate(counter); }
        }

        [Test]
        public void SnapTurnKeepsYourHeadInPlaceAndHomeResets()
        {
            m_Loco.SetHome(new Pose(m_Rig.transform.position, m_Rig.transform.rotation));
            var head = m_Head.transform.position;
            m_Loco.SnapTurn(30f);
            Assert.That(Vector3.Distance(head, m_Head.transform.position), Is.LessThan(1e-4f));
            Assert.AreEqual(210f, m_Rig.transform.eulerAngles.y, 1e-3f);
            m_Loco.TeleportTo(new Vector3(3f, 2f, 1f));
            m_Loco.GoHome();
            Assert.AreEqual(new Vector3(0f, 0f, 4f), m_Rig.transform.position);
        }

        [Test]
        public void NothingMovesInPassthrough()
        {
            AppState.Set(AppMode.Passthrough);
            var start = m_Rig.transform.position;
            m_Hub.RaisePressStart(ToolHand.Right, new Pose(Vector3.one, Quaternion.identity));
            m_Hub.RaisePressMove(ToolHand.Right, new Pose(Vector3.zero, Quaternion.identity));
            Assert.AreEqual(start, m_Rig.transform.position);
        }
    }

    public class PanelBehaviourTests
    {
        [Test]
        public void InspectorCentresOnThePalm()
        {
            var go = new GameObject("palm");
            try
            {
                var menu = go.AddComponent<PalmMenu>();
                menu.toolsSize = new Vector2(0.23f, 0.184f);
                menu.inspectorWidth = 0.28f;
                Assert.AreEqual(Vector3.zero, menu.ContentOffsetTarget);
                menu.SetInspector(true);
                // Inspector section centre (content space) = −tools/2 − inspector/2; the offset cancels it.
                Assert.AreEqual(0.23f * 0.5f + 0.28f * 0.5f, menu.ContentOffsetTarget.x, 1e-6f);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void WindowsCloseWhenLeavingTheWorld()
        {
            AppState.Reset();
            AppState.Set(AppMode.World);
            var go = new GameObject("win");
            try
            {
                var w = go.AddComponent<FloatingWindow>();
                typeof(FloatingWindow).GetMethod("OnEnable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(w, null);
                w.Open();
                Assert.IsTrue(w.IsOpen);
                AppState.Set(AppMode.Passthrough);
                Assert.IsFalse(w.IsOpen, "sellers / checkout close on exit");
                typeof(FloatingWindow).GetMethod("OnDisable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(w, null);
            }
            finally { Object.DestroyImmediate(go); AppState.Reset(); }
        }
    }
}
