using AirTools.Editor;
using AirTools.Scene;
using NUnit.Framework;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Tests
{
    /// Measures the generated facade against SPEC §4 ground truth using physics raycasts on SceneSurface.
    public class SyntheticFacadeTests
    {
        GameObject m_Facade;
        const float Mm = 0.001f;

        [OneTimeSetUp]
        public void Build()
        {
            m_Facade = SyntheticFacadeBuilder.CreateHierarchy();
            Physics.SyncTransforms();
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (m_Facade != null) Object.DestroyImmediate(m_Facade);
        }

        static RaycastHit Cast(Vector3 from, Vector3 dir)
        {
            Assert.IsTrue(Physics.Raycast(from, dir, out var hit, 50f, SceneLayers.SceneSurfaceMask, QueryTriggerInteraction.Ignore),
                $"ray from {from} dir {dir} hit nothing");
            return hit;
        }

        [Test]
        public void WindowWidthIs1500()
        {
            var from = new Vector3(0f, 4.1f, -0.05f);
            float left = Cast(from, Vector3.left).point.x;
            float right = Cast(from, Vector3.right).point.x;
            Assert.AreEqual(S.WindowWidth, right - left, Mm);
        }

        [Test]
        public void WindowHeightIs1200AndSillAt3500()
        {
            var from = new Vector3(0f, 4.1f, -0.05f);
            var down = Cast(from, Vector3.down);
            float top = Cast(from, Vector3.up).point.y;
            Assert.AreEqual(S.SillHeight, down.point.y, Mm, "sill height");
            Assert.AreEqual(S.WindowHeight, top - down.point.y, Mm, "window height");
            Assert.AreEqual(0f, Vector3.Angle(down.normal, Vector3.up), 0.05f, "sill is level");
        }

        [Test]
        public void WindowCentreMarker()
        {
            var window = m_Facade.transform.Find("Window");
            Assert.IsNotNull(window);
            Assert.That(Vector3.Distance(window.position, new Vector3(0f, 4.10f, -0.10f)), Is.LessThan(Mm));
        }

        [Test]
        public void GlassIsRecessed100mm()
        {
            var hit = Cast(new Vector3(0f, 4.1f, 2f), Vector3.back);
            Assert.AreEqual(-S.WindowRecess, hit.point.z, Mm);
        }

        [Test]
        public void GutterRunIs4200AndWidth127()
        {
            var b = ColliderBounds(m_Facade.transform.Find("Gutter"));
            Assert.AreEqual(S.GutterRun, b.size.x, Mm);
            Assert.AreEqual(S.GutterWidth, b.size.z, Mm);
        }

        [Test]
        public void FasciaIs4200LongAnd25mmProud()
        {
            var b = ColliderBounds(m_Facade.transform.Find("Fascia"));
            Assert.AreEqual(S.FasciaLength, b.size.x, Mm);
            var hit = Cast(new Vector3(0f, 6.15f, 2f), Vector3.back);
            Assert.AreEqual(S.FasciaProud, hit.point.z, Mm);
        }

        [Test]
        public void DoorIs914By2032()
        {
            var b = ColliderBounds(m_Facade.transform.Find("Door"));
            Assert.AreEqual(S.DoorWidth, b.size.x, Mm);
            Assert.AreEqual(S.DoorHeight, b.size.y, Mm);
            Assert.AreEqual(0f, b.min.y, Mm);
        }

        [Test]
        public void LedgeTiltIs2Degrees()
        {
            var hit = Cast(new Vector3(S.LedgeCentreX, 2f, 0.15f), Vector3.down);
            Assert.AreEqual(S.LedgeTiltDeg, Vector3.Angle(hit.normal, Vector3.up), 0.01f);
            Assert.Less(hit.point.y, S.LedgeHeight, "slopes down away from the wall");
        }

        [Test]
        public void WallIs8By6500()
        {
            var b = ColliderBounds(m_Facade.transform.Find("Wall"));
            Assert.AreEqual(S.WallWidth, b.size.x, Mm);
            Assert.AreEqual(S.WallHeight, b.size.y, Mm);
            Assert.AreEqual(0f, b.max.z, Mm, "wall face at z = 0");
        }

        [Test]
        public void EverythingIsOnSceneSurface()
        {
            foreach (var t in m_Facade.GetComponentsInChildren<Transform>(true))
                Assert.AreEqual(SceneLayers.SceneSurface, t.gameObject.layer, t.name);
        }

        [Test]
        public void MeshWindingMatchesNormals()
        {
            foreach (var mf in m_Facade.GetComponentsInChildren<MeshFilter>())
            {
                var m = mf.sharedMesh;
                var v = m.vertices; var n = m.normals; var tri = m.triangles;
                for (int i = 0; i < tri.Length; i += 3)
                {
                    var face = Vector3.Cross(v[tri[i + 1]] - v[tri[i]], v[tri[i + 2]] - v[tri[i]]).normalized;
                    Assert.Greater(Vector3.Dot(face, n[tri[i]]), 0.99f, $"{m.name} triangle {i / 3} is back-facing");
                }
            }
        }

        [Test]
        public void CamerasLookAtTheWall()
        {
            var cams = SyntheticFacadeBuilder.CreateCameras();
            Assert.AreEqual(S.CameraCount, cams.Length);
            var wallCentre = new Vector3(0f, S.WallHeight / 2f, 0f);
            foreach (var c in cams)
            {
                var toWall = (wallCentre - c.position).normalized;
                Assert.Less(Vector3.Angle(c.rotation * Vector3.forward, toWall), 0.5f, $"camera {c.id}");
                Assert.Greater(c.position.z, 1f, $"camera {c.id} in front of the wall");
            }
        }

        static Bounds ColliderBounds(Transform t)
        {
            Assert.IsNotNull(t);
            var cols = t.GetComponentsInChildren<Collider>();
            Assert.IsNotEmpty(cols, t.name);
            var b = cols[0].bounds;
            foreach (var c in cols) b.Encapsulate(c.bounds);
            return b;
        }
    }
}
