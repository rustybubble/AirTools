using AirTools.Core;
using AirTools.Editor;
using AirTools.Parts;
using AirTools.Scene;
using AirTools.Dev;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AirTools.Tests
{
    /// presence.md S1 acceptance (h): the table fallback chain (mat > raycast > assumed) and the site mat frame.
    public class RealTableTests
    {
        static readonly Vector3 Near = new Vector3(0.2f, 1.6f, 0.5f);
        static readonly Vector3 Fwd = new Vector3(0.3f, -0.4f, 1f);

        static Pose Resolve(Pose? mat, (Vector3, Vector3)? hit, out string source) =>
            RealTable.Resolve(mat, hit, Near, Fwd, 0f, 0.75f, 0.5f, 1.1f, 10f, out source);

        [Test]
        public void FallbackOrderIsMatThenRaycastThenAssumed()
        {
            var mat = new Pose(new Vector3(0.1f, 0.74f, 0.6f), Quaternion.Euler(0f, 30f, 0f));
            var hit = (new Vector3(0.2f, 0.72f, 0.5f), Vector3.up);
            var p = Resolve(mat, hit, out var s);
            Assert.AreEqual(RealTable.SourceMat, s);
            Assert.AreEqual(mat.position, p.position);
            p = Resolve(null, hit, out s);
            Assert.AreEqual(RealTable.SourceRaycast, s);
            Assert.AreEqual(0.72f, p.position.y, 1e-6f);
            p = Resolve(null, null, out s);
            Assert.AreEqual(RealTable.SourceAssumed, s);
            Assert.AreEqual(0.75f, p.position.y, 1e-6f);
            Assert.AreEqual(Near.x, p.position.x, 1e-6f);
            Assert.AreEqual(Near.z, p.position.z, 1e-6f);
            foreach (var frame in new[] { Resolve(null, hit, out _), Resolve(null, null, out _) })
            {
                Assert.That(Vector3.Angle(frame.rotation * Vector3.up, Vector3.up), Is.LessThan(0.01f), "level");
                Assert.That(Vector3.Angle(frame.rotation * Vector3.forward, Vector3.ProjectOnPlane(Fwd, Vector3.up)), Is.LessThan(0.01f), "z = your forward");
            }
        }

        [Test]
        public void RaycastHitsMustLookLikeATable()
        {
            // Too tilted, too low (the floor), too high (a shelf / the ceiling ray): fall through to the assumed height.
            foreach (var bad in new[] { (new Vector3(0f, 0.8f, 0f), Quaternion.Euler(15f, 0f, 0f) * Vector3.up), (new Vector3(0f, 0.02f, 0f), Vector3.up), (new Vector3(0f, 1.3f, 0f), Vector3.up) })
            {
                Resolve(null, bad, out var s);
                Assert.AreEqual(RealTable.SourceAssumed, s, $"hit at {bad.Item1} normal {bad.Item2}");
            }
            Resolve(null, (new Vector3(0f, 0.9f, 0f), Quaternion.Euler(6f, 0f, 3f) * Vector3.up), out var ok);
            Assert.AreEqual(RealTable.SourceRaycast, ok, "a slightly uneven table still counts");
        }

        [Test]
        public void ComponentUsesTheChainInOrder()
        {
            var go = new GameObject("table");
            var rig = new GameObject("rig");
            var head = new GameObject("head"); head.transform.SetParent(rig.transform, false); head.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            try
            {
                rig.transform.position = new Vector3(0f, 0.2f, 0f);   // the floor is the rig's height
                var table = go.AddComponent<RealTable>();
                table.rig = rig.transform; table.head = head.transform;
                Assert.IsTrue(table.TryGetTableFrame(new Vector3(0f, 1.8f, 0.5f), out var f, out var s));
                Assert.AreEqual(RealTable.SourceAssumed, s);
                Assert.AreEqual(0.95f, f.position.y, 1e-5f, "assumed 0.75 m above the floor");
                Ray asked = default;
                table.raycast = (Ray r, float max, out Vector3 p, out Vector3 n) => { asked = r; p = new Vector3(r.origin.x, 1.0f, r.origin.z); n = Vector3.up; return true; };
                table.TryGetTableFrame(new Vector3(0f, 1.8f, 0.5f), out f, out s);
                Assert.AreEqual(RealTable.SourceRaycast, s);
                Assert.AreEqual(1.0f, f.position.y, 1e-5f);
                Assert.That(Vector3.Angle(asked.direction, Vector3.down), Is.LessThan(0.01f), "cast straight down");
                Assert.Greater(asked.origin.y, 0.2f + 1.1f, "from above the highest table");
                var mat = go.AddComponent<SiteMatTracker>();
                table.mat = mat;
                Assert.IsTrue(SiteMatSpec.TryParse("airtools:mat:v1;w=0.420;h=0.297;qr=0.120;pad=0.300,0.150", out var spec));
                mat.Lock(new Pose(new Vector3(0.1f, 0.93f, 0.6f), Quaternion.Euler(0f, 20f, 0f)), spec);
                table.TryGetTableFrame(new Vector3(0f, 1.8f, 0.5f), out f, out s);
                Assert.AreEqual(RealTable.SourceMat, s, "the mat beats the raycast");
                Assert.AreEqual(0.93f, f.position.y, 1e-5f);
                var spot = table.ModelSpot(f);
                Assert.That(Vector3.Distance(spot, f.position + f.rotation * spec.ModelSpot), Is.LessThan(1e-5f));
                table.TryGetTableFrame(new Vector3(5f, 1.8f, 5f), out _, out s);
                Assert.AreEqual(RealTable.SourceRaycast, s, "a mat far from where you're looking is another table");
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(rig); }
        }

        [Test]
        public void MatFrameIsAlwaysLevelWhateverTheQrAxisConvention()
        {
            var rng = new System.Random(3);
            float R(float a) => ((float)rng.NextDouble() * 2f - 1f) * a;
            // The QR anchor's convention isn't documented: its normal may be any of ±x, ±y, ±z.
            var conventions = new[]
            {
                Quaternion.identity, Quaternion.Euler(90f, 0f, 0f), Quaternion.Euler(-90f, 0f, 0f),
                Quaternion.Euler(0f, 0f, 90f), Quaternion.Euler(0f, 0f, -90f), Quaternion.Euler(180f, 0f, 0f),
            };
            for (int i = 0; i < 300; i++)
            {
                float yaw = R(180f);
                var tilt = Quaternion.Euler(R(12f), 0f, R(12f));   // a mat on a slightly uneven table, tracking noise
                var qr = tilt * Quaternion.Euler(0f, yaw, 0f) * conventions[i % conventions.Length];
                Assert.IsTrue(SiteMatSpec.MatFrame(new Pose(new Vector3(0f, 0.75f, 0.5f), qr), out var frame), $"#{i}");
                Assert.That(Vector3.Angle(frame.rotation * Vector3.up, Vector3.up), Is.LessThan(5f), "mat up = world up ± 5°");
                Assert.AreEqual(0.75f, frame.position.y, 1e-6f, "origin at the QR");
                // Stable: a second sighting a hair different gives (almost) the same frame.
                var qr2 = Quaternion.Euler(0.3f, 0.2f, -0.2f) * qr;
                Assert.IsTrue(SiteMatSpec.MatFrame(new Pose(Vector3.zero, qr2), out var frame2));
                Assert.That(Quaternion.Angle(frame.rotation, frame2.rotation), Is.LessThan(2f));
            }
            // A QR on a wall is not the mat.
            Assert.IsFalse(SiteMatSpec.MatFrame(new Pose(Vector3.zero, Quaternion.Euler(0f, 30f, 0f) * Quaternion.Euler(45f, 0f, 0f)), out _));
        }

        [Test]
        public void MatPayloadParses()
        {
            Assert.IsTrue(SiteMatSpec.TryParse("airtools:mat:v1;w=0.420;h=0.297;qr=0.120;pad=0.300,0.150", out var s));
            Assert.AreEqual(0.42f, s.width, 1e-6f); Assert.AreEqual(0.297f, s.height, 1e-6f); Assert.AreEqual(0.12f, s.qr, 1e-6f);
            Assert.AreEqual(new Vector2(0.3f, 0.15f), s.pad);
            Assert.IsTrue(SiteMatSpec.TryParse("airtools:mat:v1;w=0.5;h=0.3;qr=0.15;model=0.2,0.1;parts=0.4,0.05;extra=1", out s));
            Assert.AreEqual(new Vector2(0.2f, 0.1f), s.model);
            Assert.AreEqual(new Vector3(0.4f, 0f, 0.05f), s.PartsSpot);
            Assert.IsFalse(SiteMatSpec.TryParse("https://example.com", out _), "other QR codes");
            Assert.IsFalse(SiteMatSpec.TryParse("airtools:mat:v2;w=0.4", out _), "unknown version");
            Assert.IsFalse(SiteMatSpec.TryParse("airtools:mat:v1;w=abc", out _));
            Assert.IsFalse(SiteMatSpec.TryParse(null, out _));
        }

        [Test]
        public void MatLocksOnlyAfterHoldingStill()
        {
            var go = new GameObject("mat");
            try
            {
                var mat = go.AddComponent<SiteMatTracker>();
                const string payload = "airtools:mat:v1;w=0.420;h=0.297;qr=0.120;pad=0.300,0.150";
                var qr = new Pose(new Vector3(0f, 0.75f, 0.5f), Quaternion.Euler(90f, 10f, 0f));
                Assert.IsFalse(mat.Sample(qr, "https://example.com", 0f), "ignores other codes");
                Assert.IsFalse(mat.Sample(qr, payload, 0f));
                Assert.IsFalse(mat.Sample(qr, payload, 0.5f));
                var moved = new Pose(qr.position + new Vector3(0.03f, 0f, 0f), qr.rotation);
                Assert.IsFalse(mat.Sample(moved, payload, 0.9f), "moved: the clock restarts");
                Assert.IsFalse(mat.Sample(moved, payload, 1.5f));
                Assert.IsTrue(mat.Sample(moved, payload, 1.95f));
                Assert.IsTrue(mat.TryGetMatPose(out var pose));
                Assert.That(Vector3.Distance(pose.position, moved.position), Is.LessThan(1e-5f));
                mat.Unlock();
                Assert.IsFalse(mat.TryGetMatPose(out _));
            }
            finally { Object.DestroyImmediate(go); }
        }
    }

    public class TruthBarTests
    {
        [Test]
        public void RulerIsOneMetreWithCentimetreTicksFromTheMatCorner()
        {
            var m = TruthBar.BuildMesh(1f);
            try
            {
                Assert.AreEqual(0f, m.bounds.min.x, 1e-6f);
                Assert.AreEqual(1.000f, m.bounds.max.x, 1e-6f, "exactly 1.000 m");
                Assert.AreEqual((1 + 101) * 2, m.triangles.Length / 3, "the line + 101 cm ticks");
                foreach (var n in m.normals) Assert.That(Vector3.Angle(n, Vector3.up), Is.LessThan(0.01f), "lies flat, facing up");
            }
            finally { Object.DestroyImmediate(m); }
            var go = new GameObject("bar");
            var matGo = new GameObject("mat");
            try
            {
                go.AddComponent<MeshFilter>(); go.AddComponent<MeshRenderer>();
                var mat = matGo.AddComponent<SiteMatTracker>();
                var bar = go.AddComponent<TruthBar>();
                bar.mat = mat;
                SiteMatSpec.TryParse("airtools:mat:v1;w=0.420;h=0.297;qr=0.120;pad=0.300,0.150", out var spec);
                var frame = new Pose(new Vector3(0.3f, 0.75f, 0.6f), Quaternion.Euler(0f, -25f, 0f));
                mat.Lock(frame, spec);
                AppState.Reset();
                bar.Refresh();
                Assert.IsTrue(bar.Shown);
                var start = frame.position + frame.rotation * spec.Corner;
                Assert.That(Vector3.Distance(bar.transform.position, start + Vector3.up * bar.lift), Is.LessThan(1e-5f), "starts at the mat corner");
                Assert.That(Vector3.Angle(bar.transform.right, frame.rotation * Vector3.right), Is.LessThan(0.01f), "along the mat's long edge");
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(matGo); AppState.Reset(); }
        }
    }

    /// presence.md S1: take it home onto the mat / beside the model after the building shrinks back.
    public class TakeItHomeTableTests
    {
        [Test]
        public void PartsStandBesideTheModelInTabletopAtTrueSize()
        {
            AppState.Reset();
            var catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath) ?? PartCatalogBuilder.Build();
            var rootGo = new GameObject("SceneRoot");
            var rig = new GameObject("rig");
            var head = new GameObject("head"); head.transform.SetParent(rig.transform, false); head.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            var go = new GameObject("home");
            try
            {
                var scene = rootGo.AddComponent<SceneRoot>();
                var facade = SyntheticFacadeBuilder.CreateHierarchy();
                facade.transform.SetParent(rootGo.transform, false);
                scene.SetContent(null, facade);
                var table = go.AddComponent<TabletopController>();
                table.root = scene; table.rig = rig.transform; table.head = head.transform;
                table.fitToTable = false;   // modelview: the placement is checked at the fixed 1:50
                table.placement = ModelPlacement.OnTable;   // modelwheel: the table option (floating: the test below)
                var realTable = go.AddComponent<RealTable>();
                realTable.rig = rig.transform; realTable.head = head.transform;
                table.table = realTable;
                var loader = go.AddComponent<PartLoader>(); loader.catalog = catalog;
                var home = go.AddComponent<TakeItHome>();
                home.rig = rig.transform; home.head = head.transform; home.loader = loader; home.table = realTable; home.tabletop = table;
                home.OnPurchased(new CheckoutReceipt { status = "AUTHORIZED", qty = 8, total_usd = 34.16f, seller = "Home Depot (mock)" }, catalog.Spec(PartScenarios.Hanger));
                AppState.Set(AppMode.Tabletop);
                table.Apply(true);
                home.ShowOnTable();
                Assert.AreEqual(1, home.OnTable.Count);
                var p = home.OnTable[0];
                Assert.AreEqual(1f, p.transform.lossyScale.x, 1e-5f, "true size next to the 1:50 model");
                var bottom = p.transform.TransformPoint(new Vector3(0f, p.LocalBox.min.y, p.LocalBox.center.z));
                Assert.AreEqual(rootGo.transform.position.y, bottom.y, 0.001f, "on the table, level with the model's ground");
                var fp = table.FootprintLocal(out _);
                var modelCentre = rootGo.transform.TransformPoint(new Vector3(fp.center.x, 0f, fp.center.z));
                var right = Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(modelCentre - head.transform.position, Vector3.up).normalized);
                float side = Vector3.Dot(p.transform.position - modelCentre, right);
                float halfModel = new Vector2(fp.extents.x, fp.extents.z).magnitude * 0.02f;
                Assert.Greater(side, halfModel, "to the right of the model, clear of it");
                Assert.Less(side, halfModel + 0.3f, "but right beside it");
                StringAssert.StartsWith("beside model", home.LastPlacement);
                home.ClearTable();
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(rig); Object.DestroyImmediate(rootGo); SnapService.Scale = 1f; AppState.Reset(); }
        }

        /// modelwheel: with the model floating in front of you, the bought parts stand on the floor to its right at true
        /// size, clear of the wheel under it.
        [Test]
        public void PartsStandOnTheFloorBesideTheFloatingModel()
        {
            AppState.Reset();
            var catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(PartCatalogBuilder.CatalogPath) ?? PartCatalogBuilder.Build();
            var rootGo = new GameObject("SceneRoot");
            var rig = new GameObject("rig");
            rig.transform.position = new Vector3(0.3f, 0.1f, -0.2f);
            var head = new GameObject("head"); head.transform.SetParent(rig.transform, false); head.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            var go = new GameObject("home");
            try
            {
                var scene = rootGo.AddComponent<SceneRoot>();
                var facade = SyntheticFacadeBuilder.CreateHierarchy();
                facade.transform.SetParent(rootGo.transform, false);
                scene.SetContent(null, facade);
                var table = go.AddComponent<TabletopController>();
                table.root = scene; table.rig = rig.transform; table.head = head.transform;
                var loader = go.AddComponent<PartLoader>(); loader.catalog = catalog;
                var home = go.AddComponent<TakeItHome>();
                home.rig = rig.transform; home.head = head.transform; home.loader = loader; home.tabletop = table;
                home.OnPurchased(new CheckoutReceipt { status = "AUTHORIZED", qty = 8, total_usd = 34.16f, seller = "Home Depot (mock)" }, catalog.Spec(PartScenarios.Hanger));
                AppState.Set(AppMode.Tabletop);
                table.Apply(true);
                Assert.IsTrue(table.Floating);
                home.ShowOnTable();
                Assert.AreEqual(1, home.OnTable.Count);
                var p = home.OnTable[0];
                Assert.AreEqual(1f, p.transform.lossyScale.x, 1e-5f, "true size");
                var bottom = p.transform.TransformPoint(new Vector3(0f, p.LocalBox.min.y, p.LocalBox.center.z));
                Assert.AreEqual(rig.transform.position.y, bottom.y, 0.001f, "on the floor, not in the air beside the model");
                var c = table.ModelCentreWorld;
                var right = Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(c - head.transform.position, Vector3.up).normalized);
                Assert.Greater(Vector3.Dot(p.transform.position - c, right), ModelViewLayout.PartsClearance, "to the right, clear of the wheel");
                StringAssert.StartsWith("beside model (floor)", home.LastPlacement);

                // Turn round and recentre: the model comes in front of you (Recentred fires once it has landed) and the
                // parts go beside it again (TakeItHome follows Recentred / Refitted; its OnEnable doesn't run here).
                int recentred = 0;
                table.Recentred += () => recentred++;
                rig.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                Assert.IsTrue(table.Recentre("test"));
                Assert.AreEqual(1, recentred, "instant in EditMode: landed at once");
                home.ShowOnTable(false);
                Assert.AreEqual(1, home.OnTable.Count);
                var c2 = table.ModelCentreWorld;
                var right2 = Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(c2 - head.transform.position, Vector3.up).normalized);
                Assert.Greater(Vector3.Dot(home.OnTable[0].transform.position - c2, right2), ModelViewLayout.PartsClearance, "beside the recentred model");
                home.ClearTable();
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(rig); Object.DestroyImmediate(rootGo); SnapService.Scale = 1f; AppState.Reset(); }
        }
    }
}
