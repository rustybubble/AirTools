using System;
using System.Collections.Generic;
using AirTools.Core;
using AirTools.Dev;
using AirTools.Editor;
using AirTools.Scene;
using NUnit.Framework;
using UnityEngine;
using S = AirTools.Scene.SyntheticFacadeSpec;

namespace AirTools.Tests
{
    /// Scene-package contract (backend docs/api.md): scene.json, the glTF → Unity X flip, the structure layer and the
    /// R6 snap policy (corner 2.5 cm / 1.5°, edge 2 cm / 1.2°, corner-beats-edge + 1 cm, plane 3 cm, depth gate,
    /// hysteresis 1.5×), camera pin rays, and "set scale from a known dimension".
    public class StructureLayerTests
    {
        [Test]
        public void SceneJson_ParsesTheBriefExample_AndFlipsSpawnX()
        {
            const string json = @"{""name"":""Klaus east window"",""units"":""meters"",""up"":[0,1,0],""north"":[0,0,-1],
              ""scale_method"":""scalebar_aruco"",""scale_residual_m"":0.012,""gravity_residual_deg"":0.4,""origin_gps"":[33.777,-84.396,285.0],
              ""recommended_spawn"":{""pos"":[1,1.6,4],""look"":[0,2,0]},
              ""mesh"":{""file"":""mesh.r2.glb"",""triangles"":199977,""texture_px"":4096},""collision"":{""file"":""collision.r2.glb"",""triangles"":41000},
              ""cameras"":""cameras.r2.json"",""quality"":""full"",""revision"":2,
              ""frame"":{""id"":""klaus-east-7f3a"",""aligned_to_preview"":true,""alignment_residual_m"":0.04},""pipeline_version"":""0.3.0"",
              ""scale_candidates"":[{""method"":""altitude"",""scale"":0.68,""residual_m"":0.3}],""future_field"":{""x"":1}}";
            var m = SceneManifest.Parse(json);
            Assert.AreEqual("mesh.r2.glb", m.MeshFile);
            Assert.AreEqual("collision.r2.glb", m.CollisionFile);
            Assert.AreEqual("cameras.r2.json", m.cameras);
            Assert.IsFalse(m.HasStructure, "no structure entry → mesh-only snapping, never a failure");
            Assert.AreEqual(2, m.revision);
            Assert.AreEqual("klaus-east-7f3a", m.FrameId);
            Assert.AreEqual(1, m.scale_candidates.Count);
            Assert.IsTrue(m.TryGetSpawn(out var eye, out var look));
            Assert.AreEqual(new Vector3(-1f, 1.6f, 4f), eye, "glTF x must be negated");
            Assert.AreEqual(new Vector3(0f, 2f, 0f), look);
            // north [0,0,-1] (glTF) → Unity (0,0,-1): a 180° yaw puts it on +Z.
            Assert.AreEqual(180f, Mathf.Abs(m.NorthDeg), 1e-3f);
        }

        [Test]
        public void SceneJson_NullStructureAndNullNorth_AreFine_OldFlatPackageIsRejected()
        {
            var m = SceneManifest.Parse(@"{""mesh"":{""file"":""mesh.r1.glb""},""structure"":null,""north"":null,""revision"":1}");
            Assert.IsFalse(m.HasStructure);
            Assert.AreEqual(0f, m.NorthDeg);
            Assert.Throws<FormatException>(() => SceneManifest.Parse(@"{""name"":""old"",""units"":""meters""}"));
        }

        [Test]
        public void Structure_AllPolygonForms_GiveTheSameOutline_AndObjectRectsMatchTheFile()
        {
            // One plane z = -2 (glTF), normal +z, offset 2; the same square in the three schema forms.
            const string planes = @"{""schema"":""airtools.structure/1"",""frame"":""scene"",""planes"":[
              {""id"":""a"",""normal"":[0,0,1],""offset"":2,""polygon3d"":[[1,0,-2],[2,0,-2],[2,1,-2],[1,1,-2]]},
              {""id"":""b"",""normal"":[0,0,1],""offset"":2,""polygon"":[[1,0,-2],[2,0,-2],[2,1,-2],[1,1,-2]]},
              {""id"":""c"",""normal"":[0,0,1],""offset"":2,""origin"":[1.5,0.5,-2],""u"":[1,0,0],""polygon"":[[-0.5,-0.5],[0.5,-0.5],[0.5,0.5],[-0.5,0.5]]}],
              ""objects"":[{""id"":""o1"",""label"":""cabinet_door"",""plane"":""c"",""rect"":[-0.5,-0.5,0.5,0.5],""w_m"":1,""h_m"":1}],
              ""edges"":[],""corners"":[]}";
            var layer = StructureLayer.Parse(planes);
            Assert.AreEqual(3, layer.Planes.Length);
            foreach (var pl in layer.Planes)
            {
                Assert.AreEqual(new Vector3(0, 0, 1), pl.normal, $"{pl.id} normal");
                Assert.AreEqual(2f, pl.offset, 1e-6f);
                var set = new HashSet<Vector3>(pl.outline3d);
                foreach (var p in new[] { new Vector3(-1, 0, -2), new Vector3(-2, 0, -2), new Vector3(-2, 1, -2), new Vector3(-1, 1, -2) })
                    Assert.IsTrue(Contains(pl.outline3d, p), $"{pl.id} outline lacks {p} (X flip)");
                foreach (var p in pl.outline3d) Assert.AreEqual(0f, pl.SignedDistance(p), 1e-5f, $"{pl.id}: outline off its plane");
                Assert.IsTrue(StructureLayer.Contains(pl, pl.ToPlane(new Vector3(-1.5f, 0.5f, -2f))));
                Assert.IsFalse(StructureLayer.Contains(pl, pl.ToPlane(new Vector3(1.5f, 0.5f, -2f))), "mirrored point must be outside");
            }
            // The rect (u, v) coordinates mean what they mean in the file: same corners as the flipped polygon.
            Assert.AreEqual(1, layer.Objects.Length);
            foreach (var p in layer.Objects[0].corners) Assert.IsTrue(Contains(layer.Planes[2].outline3d, p), $"object corner {p}");
        }

        static bool Contains(Vector3[] pts, Vector3 p)
        {
            foreach (var q in pts) if (Vector3.Distance(q, p) < 1e-5f) return true;
            return false;
        }

        [Test]
        public void Structure_RejectsBenchFrameAndOtherSchemas_IgnoresUnknownFields()
        {
            Assert.Throws<FormatException>(() => StructureLayer.Parse(@"{""schema"":""airtools.structure/1"",""frame"":""cameras""}"));
            Assert.Throws<FormatException>(() => StructureLayer.Parse(@"{""schema"":""other/2"",""frame"":""scene""}"));
            var l = StructureLayer.Parse(@"{""schema"":""airtools.structure/1"",""frame"":""scene"",""cost"":{""host"":""hfbox""},
              ""planes"":[{""id"":""inf"",""normal"":[0,1,0],""offset"":0}],
              ""edges"":[{""id"":""e"",""a"":[0,0,0],""b"":[1,0,0],""kind"":""line"",""src"":[""limap""],""reproj_px"":0.8}],
              ""corners"":[{""id"":""c"",""p"":[1,2,3],""kind"":""2edge"",""confidence"":""high""}]}");
            Assert.AreEqual(0, l.Planes.Length, "an unbounded plane would capture every snap: skipped");
            Assert.AreEqual(1, l.SkippedPlanes);
            Assert.AreEqual(new Vector3(-1, 2, 3), l.Corners[0].p);
            Assert.AreEqual(new Vector3(-1, 0, 0), l.Edges[0].b);
        }
    }

    public class StructureSnapperTests
    {
        static readonly StructureSnapper.Settings S0 = StructureSnapper.Settings.Default;

        /// Ray from `range` in front (+z) straight at `tip` on the z = 0 surface. At 0.5 m, 1.5° is 1.3 cm, so the
        /// 2.5 cm tip radius decides; at range the angle criterion takes over.
        static StructureSnapper.Query At(Vector3 tip, float range = 0.5f) => new StructureSnapper.Query
        {
            origin = tip + new Vector3(0, 0, range), dir = Vector3.back, meshHit = tip, meshNormal = Vector3.forward,
        };

        static StructureLayer Layer(Vector3[] corners = null, (Vector3 a, Vector3 b)[] edges = null)
        {
            var l = new StructureLayer();
            if (corners != null) { l.Corners = new StructureCorner[corners.Length]; for (int i = 0; i < corners.Length; i++) l.Corners[i] = new StructureCorner { p = corners[i], planes = new int[0] }; }
            if (edges != null) { l.Edges = new StructureEdge[edges.Length]; for (int i = 0; i < edges.Length; i++) l.Edges[i] = new StructureEdge { a = edges[i].a, b = edges[i].b, planes = new int[0] }; }
            return l;
        }

        [Test]
        public void DepthGate_HiddenCornerBehindTheSurfaceNeverWins_ProtrudingCornerOnTheRayDoes()
        {
            var tip = Vector3.zero;
            var hidden = Layer(new[] { new Vector3(0, 0, -0.5f) });   // exactly on the ray, 50 cm behind the surface
            Assert.AreEqual(SnapKind.Face, StructureSnapper.Snap(hidden, At(tip), S0).kind);
            var protruding = Layer(new[] { new Vector3(0.0005f, 0, 0.3f) });   // 30 cm in front, 0.5 mm off the ray
            var r = StructureSnapper.Snap(protruding, At(tip), S0);
            Assert.AreEqual(SnapKind.Corner, r.kind, "a cabinet corner in front of the wall the ray grazes");
            var near = Layer(new[] { new Vector3(0.01f, 0.01f, 0.005f) });
            Assert.AreEqual(SnapKind.Corner, StructureSnapper.Snap(near, At(tip), S0).kind);
        }

        [Test]
        public void CornerRadius_AndAngle()
        {
            Assert.AreEqual(SnapKind.Corner, StructureSnapper.Snap(Layer(new[] { new Vector3(0.024f, 0, 0) }), At(Vector3.zero), S0).kind);
            Assert.AreEqual(SnapKind.Face, StructureSnapper.Snap(Layer(new[] { new Vector3(0.026f, 0, 0) }), At(Vector3.zero), S0).kind);
            // From 5 m, 1.5° of ray ≈ 13 cm laterally: the angle criterion takes over at range (R6 §7.3).
            var q = new StructureSnapper.Query { origin = new Vector3(0, 0, 5f), dir = Vector3.back, meshHit = Vector3.zero, meshNormal = Vector3.forward };
            // (The depth gate, 2 % of 5 m = 10 cm, also bounds it laterally.)
            Assert.AreEqual(SnapKind.Corner, StructureSnapper.Snap(Layer(new[] { new Vector3(0.08f, 0, 0) }), q, S0).kind);
            Assert.AreEqual(SnapKind.Face, StructureSnapper.Snap(Layer(new[] { new Vector3(0.16f, 0, 0) }), q, S0).kind);
        }

        [Test]
        public void CornerBeatsAnInRangeEdge_OnlyWithinOneCentimetreOfIt()
        {
            var edge = (new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0));   // passes through the tip: d_edge = 0
            var close = Layer(new[] { new Vector3(0, 0.009f, 0) }, new[] { edge });
            var far = Layer(new[] { new Vector3(0, 0.015f, 0) }, new[] { edge });
            var s = S0; s.midpoints = false;
            Assert.AreEqual(SnapKind.Corner, StructureSnapper.Snap(close, At(Vector3.zero), s).kind);
            var r = StructureSnapper.Snap(far, At(Vector3.zero), s);
            Assert.AreEqual(SnapKind.Edge, r.kind);
            Assert.AreEqual(0f, Vector3.Distance(r.point, Vector3.zero), 1e-5f, "closest point on the segment");
        }

        [Test]
        public void EdgeSnap_PointOnSegment_AndMidpointOffered()
        {
            var l = Layer(null, new[] { (new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0)) });
            var r = StructureSnapper.Snap(l, At(new Vector3(0.2f, 0.015f, 0)), S0);
            Assert.AreEqual(SnapKind.Edge, r.kind);
            Assert.AreEqual(0.2f, r.point.x, 1e-4f);
            Assert.AreEqual(0f, r.point.y, 1e-5f);
            var m = StructureSnapper.Snap(l, At(new Vector3(0.008f, 0.005f, 0)), S0);
            Assert.AreEqual(SnapKind.Midpoint, m.kind);
            Assert.AreEqual(Vector3.zero, m.point);
            Assert.AreEqual(SnapKind.Face, StructureSnapper.Snap(l, At(new Vector3(0.2f, 0.021f, 0)), S0).kind, "beyond 2 cm");
        }

        [Test]
        public void PlaneSnap_InsidePolygonOnly_NormalFromThePlaneFacingTheViewer()
        {
            var l = StructureLayer.Parse(@"{""schema"":""airtools.structure/1"",""frame"":""scene"",""planes"":[
              {""id"":""w"",""normal"":[0,0,-1],""offset"":0.01,""polygon3d"":[[-1,-1,0.01],[1,-1,0.01],[1,1,0.01],[-1,1,0.01]]}]}");
            // Plane z = 0.01 (glTF and Unity alike), normal −z in the file: the snap faces the ray origin (+z).
            var q = At(Vector3.zero);
            q.meshNormal = new Vector3(0.05f, 0.02f, 1f).normalized;   // lumpy mesh normal
            var r = StructureSnapper.Snap(l, q, S0);
            Assert.AreEqual(SnapKind.Plane, r.kind);
            Assert.AreEqual(0.01f, r.point.z, 1e-6f);
            Assert.AreEqual(Vector3.forward, r.normal, "never the mesh normal");
            Assert.AreEqual(SnapKind.Face, StructureSnapper.Snap(l, At(new Vector3(1.2f, 0, 0)), S0).kind, "outside the polygon");
            var planesOnly = S0; planesOnly.planesOnly = true;
            Assert.AreEqual(SnapKind.Plane, StructureSnapper.Snap(l, q, planesOnly).kind);
        }

        [Test]
        public void Hysteresis_KeepsTheSnapUntilOneAndAHalfTimesTheRadius()
        {
            var l = Layer(new[] { Vector3.zero });
            var mem = new StructureSnapper.Memory();
            Assert.AreEqual(SnapKind.Corner, StructureSnapper.Snap(l, At(new Vector3(0.02f, 0, 0)), S0, mem).kind);
            Assert.AreEqual(SnapKind.Corner, StructureSnapper.Snap(l, At(new Vector3(0.035f, 0, 0)), S0, mem).kind, "kept inside 1.5×");
            Assert.AreEqual(SnapKind.Face, StructureSnapper.Snap(l, At(new Vector3(0.035f, 0, 0)), S0).kind, "no memory: not acquired");
            Assert.AreEqual(SnapKind.Face, StructureSnapper.Snap(l, At(new Vector3(0.04f, 0, 0)), S0, mem).kind, "released beyond 1.5×");
            Assert.AreEqual(SnapKind.None, mem.kind);
        }

        [Test]
        public void NoMeshHit_StillSnapsCornersAlongTheRay_WithinTheAngle()
        {
            var l = Layer(new[] { new Vector3(0.01f, 0f, 0f) });
            Assert.IsTrue(StructureSnapper.SnapWithoutHit(l, new Vector3(0, 0, 2f), Vector3.back, S0, 20f, out var r));
            Assert.AreEqual(SnapKind.Corner, r.kind);
            Assert.IsFalse(StructureSnapper.SnapWithoutHit(Layer(new[] { new Vector3(0.2f, 0f, 0f) }), new Vector3(0, 0, 2f), Vector3.back, S0, 20f, out _));
            Assert.IsFalse(StructureSnapper.SnapWithoutHit(Layer(new[] { new Vector3(0f, 0f, 3f) }), new Vector3(0, 0, 2f), Vector3.back, S0, 20f, out _), "behind the origin");
            var planesOnly = S0; planesOnly.planesOnly = true;
            Assert.IsFalse(StructureSnapper.SnapWithoutHit(l, new Vector3(0, 0, 2f), Vector3.back, planesOnly, 20f, out _));
        }

        [Test]
        public void NoMeshHit_ThroughAHoleInTheCollider_LandsOnThePlane_WhichHidesFeaturesBehindIt()
        {
            // A 40 × 60 cm door in z = 0 (normal +z), seen from z = 2; the collision mesh has a hole there (the
            // kitchen's decimated collider misses 7 of its 40 object centres).
            var door = new StructurePlane
            {
                id = "p0", normal = Vector3.forward, offset = 0f, origin = Vector3.zero, u = Vector3.right, v = Vector3.up,
                outline = new[] { new Vector2(-0.2f, -0.3f), new Vector2(0.2f, -0.3f), new Vector2(0.2f, 0.3f), new Vector2(-0.2f, 0.3f) },
                min = new Vector2(-0.2f, -0.3f), max = new Vector2(0.2f, 0.3f),
            };
            StructureLayer With(params Vector3[] corners) { var l = Layer(corners); l.Planes = new[] { door }; return l; }
            var origin = new Vector3(0.05f, 0.1f, 2f);

            Assert.IsTrue(StructureSnapper.SnapWithoutHit(With(), origin, Vector3.back, S0, 20f, out var r));
            Assert.AreEqual(SnapKind.Plane, r.kind);
            Assert.AreEqual(0f, Vector3.Distance(r.point, new Vector3(0.05f, 0.1f, 0f)), 1e-5f);
            Assert.AreEqual(0f, Vector3.Distance(r.normal, Vector3.forward), 1e-6f, "the plane normal, facing the ray");
            var planesOnly = S0; planesOnly.planesOnly = true;
            Assert.IsTrue(StructureSnapper.SnapWithoutHit(With(), origin, Vector3.back, planesOnly, 20f, out r), "part seating / level");
            Assert.AreEqual(SnapKind.Plane, r.kind);
            Assert.IsFalse(StructureSnapper.SnapWithoutHit(With(), new Vector3(0.5f, 0.1f, 2f), Vector3.back, S0, 20f, out _), "outside the outline");
            Assert.IsFalse(StructureSnapper.SnapWithoutHit(With(), origin, Vector3.back, S0, 1.5f, out _), "beyond maxDist");

            Assert.AreEqual(SnapKind.Plane, Snap(With(new Vector3(0.05f, 0.1f, -0.5f))).kind, "a corner behind the door is hidden");
            Assert.AreEqual(SnapKind.Corner, Snap(With(new Vector3(0.05f, 0.1f, 0.04f))).kind, "a handle corner in front of it wins");
            Assert.AreEqual(SnapKind.Corner, Snap(With(new Vector3(0.05f, 0.1f, -0.02f))).kind, "within the depth gate (max(3 cm, 2 %))");

            // Near the outline (the scan edge) the plane doesn't hide anything: a corner behind it still snaps.
            var nearEdge = new Vector3(0.19f, 0.1f, 2f);
            StructureSnapper.SnapWithoutHit(With(new Vector3(0.19f, 0.1f, -0.5f)), nearEdge, Vector3.back, S0, 20f, out r);
            Assert.AreEqual(SnapKind.Corner, r.kind, "1 cm inside the outline: not an occluder");
            Assert.IsTrue(StructureSnapper.SnapWithoutHit(With(), nearEdge, Vector3.back, S0, 20f, out r));
            Assert.AreEqual(SnapKind.Plane, r.kind, "…but still the fallback surface");

            Assert.IsTrue(StructureSnapper.SnapWithoutHit(With(), new Vector3(0.05f, 0.1f, -2f), Vector3.forward, S0, 20f, out r));
            Assert.AreEqual(0f, Vector3.Distance(r.normal, Vector3.back), 1e-6f, "seen from behind, the normal flips toward the ray");

            StructureSnapper.Result Snap(StructureLayer l) { StructureSnapper.SnapWithoutHit(l, origin, Vector3.back, S0, 20f, out var x); return x; }
        }

        [Test]
        public void PartSeating_StaysOnTheDoorAtItsEdge_InsteadOfFlickingToTheWallBehind()
        {
            // A 40 × 60 cm door at z = 0 with a wall 10 cm behind it (the mesh hit when the ray slips past the edge).
            var door = new StructurePlane
            {
                id = "p0", normal = Vector3.forward, offset = 0f, origin = Vector3.zero, u = Vector3.right, v = Vector3.up,
                outline = new[] { new Vector2(-0.2f, -0.3f), new Vector2(0.2f, -0.3f), new Vector2(0.2f, 0.3f), new Vector2(-0.2f, 0.3f) },
                min = new Vector2(-0.2f, -0.3f), max = new Vector2(0.2f, 0.3f),
            };
            var l = Layer(); l.Planes = new[] { door };
            var seat = S0; seat.planesOnly = true;
            var onDoor = At(new Vector3(0.18f, 0f, 0f));
            var pastEdge = new StructureSnapper.Query { origin = new Vector3(0.22f, 0f, 0.5f), dir = Vector3.back, meshHit = new Vector3(0.22f, 0f, -0.1f), meshNormal = Vector3.forward };
            var farPast = new StructureSnapper.Query { origin = new Vector3(0.30f, 0f, 0.5f), dir = Vector3.back, meshHit = new Vector3(0.30f, 0f, -0.1f), meshNormal = Vector3.forward };
            var occluded = new StructureSnapper.Query { origin = new Vector3(0.18f, 0f, 0.5f), dir = Vector3.back, meshHit = new Vector3(0.18f, 0f, 0.2f), meshNormal = Vector3.forward };

            Assert.AreEqual(SnapKind.Face, StructureSnapper.Snap(l, pastEdge, seat).kind, "no memory: 2 cm past the edge is the wall");
            var mem = new StructureSnapper.Memory();
            Assert.AreEqual(SnapKind.Plane, StructureSnapper.Snap(l, onDoor, seat, mem).kind);
            var held = StructureSnapper.Snap(l, pastEdge, seat, mem);
            Assert.AreEqual(SnapKind.Plane, held.kind, "held: stays on the door within the 4 cm seat margin");
            Assert.AreEqual(0f, held.point.z, 1e-5f);
            Assert.AreEqual(SnapKind.Face, StructureSnapper.Snap(l, farPast, seat, mem).kind, "10 cm past: lets go");
            StructureSnapper.Snap(l, onDoor, seat, mem);
            Assert.AreEqual(SnapKind.Face, StructureSnapper.Snap(l, occluded, seat, mem).kind, "something in front of the door wins");
            // The measure tool's policy (features on) is unchanged: no seat margin.
            var measure = new StructureSnapper.Memory();
            StructureSnapper.Snap(l, onDoor, S0, measure);
            Assert.AreEqual(SnapKind.Face, StructureSnapper.Snap(l, pastEdge, S0, measure).kind);
        }

        [Test]
        public void AxisLock_SnapsANearlyHorizontalTapeToTheAxis()
        {
            var l = new StructureLayer { Axes = new[] { Vector3.right, Vector3.up, Vector3.forward } };
            Assert.IsTrue(StructureSnapper.TryAxisLock(l, Vector3.zero, new Vector3(1.5f, 0.05f, 0f), 3f, 0.05f, out var locked, out var dir));
            Assert.AreEqual(Vector3.right, dir);
            Assert.AreEqual(1.5f, locked.x, 1e-5f);
            Assert.AreEqual(0f, locked.y, 1e-6f);
            Assert.IsFalse(StructureSnapper.TryAxisLock(l, Vector3.zero, new Vector3(1.5f, 0.2f, 0.3f), 3f, 0.05f, out _, out _));
        }
    }

    /// The synthetic facade exported as a runtime package (structure from its boxes, glTF frame) under a SceneRoot,
    /// snapped through the real SnapService with aim noise.
    public class PackageSnapTests
    {
        GameObject m_Root;
        SceneRoot m_SceneRoot;
        GameObject m_Content;
        StructureLayer m_Layer;
        static readonly Vector3 Eye = new Vector3(0.3f, 1.6f, 4f);

        [OneTimeSetUp]
        public void Build()
        {
            var facade = SyntheticFacadeBuilder.CreateHierarchy();
            m_Layer = StructureLayer.Parse(PackageFixtures.StructureJson(facade.transform, 1f, PackageFixtures.FacadeObjects()));
            m_Root = new GameObject("SceneRoot");
            m_SceneRoot = m_Root.AddComponent<SceneRoot>();
            m_Content = new GameObject("Content");
            m_Content.transform.SetParent(m_Root.transform, false);
            facade.transform.SetParent(m_Content.transform, false);
            var manifest = SceneManifest.Parse(@"{""mesh"":{""file"":""mesh.r1.glb""},""revision"":1}");
            m_SceneRoot.SetRuntimeContent("synthetic-facade", manifest, null, m_Content, m_Layer);
            Services.Register(m_SceneRoot);
            Physics.SyncTransforms();
        }

        [OneTimeTearDown]
        public void Teardown()
        {
            Services.Unregister(m_SceneRoot);
            UnityEngine.Object.DestroyImmediate(m_Root);
            SnapService.Enabled = true;
            SnapService.Scale = 1f;
        }

        [SetUp]
        public void Reset()
        {
            if (Math.Abs(m_SceneRoot.Calibration - 1f) > 1e-6f) m_SceneRoot.SetCalibration(1f);
            SnapService.Enabled = true;
        }

        SurfaceHit Aim(Vector3 target, StructureSnapper.Memory mem = null, bool features = true)
        {
            var world = m_Content.transform.TransformPoint(target);
            var eye = m_Content.transform.TransformPoint(Eye);
            Assert.IsTrue(SnapService.TryRaySnap(new Ray(eye, (world - eye).normalized), out var hit, 50f, features, mem), $"missed {target}");
            return hit;
        }

        [Test]
        public void SceneAsk_WithoutABox_PinsTheAnswerOnTheSpotAsked()
        {
            // The live vision model often answers without frame_id / box: the pin goes where the wearer asked.
            var go = new GameObject("Ask");
            var ask = go.AddComponent<AirTools.Agent.SceneAsk>();
            ask.sceneRoot = m_SceneRoot;
            try
            {
                var target = m_Content.transform.TransformPoint(new Vector3(-3.5f, 6f, 0f));
                var eye = m_Content.transform.TransformPoint(Eye);
                Assert.IsTrue(Physics.Raycast(eye, (target - eye).normalized, out var rh, 50f, SceneLayers.SceneSurfaceMask));
                Assert.IsTrue(ask.PinAtFocus(rh.point + rh.normal * 0.01f, "Brick wall"));
                Assert.AreEqual(1, ask.Pins.Count);
                Assert.AreEqual("focus", ask.Pins[0].frameId);
                var pinWorld = m_Content.transform.TransformPoint(ask.Pins[0].packagePoint);
                Assert.AreEqual(0f, Vector3.Distance(pinWorld, rh.point), 1e-3f, "on the surface under the asked spot");
                Assert.IsTrue(ask.Pins[0].marker.transform.IsChildOf(m_Content.transform), "follows calibration and the tabletop");
            }
            finally
            {
                foreach (var pin in ask.Pins) if (pin.marker != null) UnityEngine.Object.DestroyImmediate(pin.marker);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void TheXFlip_PutsTheDoorCornerOnTheDoor()
        {
            // The door is at +x: a mirrored layer would put its corners at −x, 5.6 m away.
            var doorCorner = new Vector3(S.DoorCentreX + S.DoorWidth / 2f, S.DoorHeight, 0f);
            float best = float.MaxValue;
            foreach (var c in m_Layer.Corners) best = Mathf.Min(best, Vector3.Distance(c.p, doorCorner));
            Assert.Less(best, 0.06f, "a structure corner must sit on the door corner (±door frame depth)");
        }

        [Test]
        public void WindowWidth_FromStructureCorners_WithOneCentimetreAimMiss_IsWithinAMillimetre()
        {
            var rng = new System.Random(7);
            Vector3 Noise() => new Vector3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), 0f) * 0.01f;
            float hw = S.WindowWidth / 2f, y = S.SillHeight + S.WindowHeight;
            int n = 50;
            for (int i = 0; i < n; i++)
            {
                var a = Aim(new Vector3(-hw, y, 0f) + Noise());
                var b = Aim(new Vector3(hw, y, 0f) + Noise());
                Assert.AreEqual(SnapKind.Corner, a.kind, $"seed {i}: left");
                Assert.AreEqual(SnapKind.Corner, b.kind, $"seed {i}: right");
                float width = Vector3.Distance(m_SceneRoot.transform.InverseTransformPoint(a.point), m_SceneRoot.transform.InverseTransformPoint(b.point));
                Assert.AreEqual(S.WindowWidth, width, 0.001f, $"seed {i}");
            }
        }

        [Test]
        public void GutterRun_EdgeAndCorner_AndMidpoint()
        {
            // From the ground the gutter hides the fascia: its front plate's top edge (y 6.100, z 0.152) is the run.
            const float top = 6.100f, front = 0.152f;
            var mid = Aim(new Vector3(0.004f, top - 0.004f, front));
            Assert.AreEqual(SnapKind.Midpoint, mid.kind);
            Assert.AreEqual(0f, mid.point.x, 1e-4f);
            var along = Aim(new Vector3(1.0f, top - 0.008f, front));
            Assert.AreEqual(SnapKind.Edge, along.kind);
            Assert.AreEqual(top, along.point.y, 0.001f);
            Assert.AreEqual(1.0f, along.point.x, 0.01f);
        }

        [Test]
        public void Level_OnTheLedge_UsesThePlaneNormal_Exactly2Degrees()
        {
            var hit = Aim(new Vector3(S.LedgeCentreX, S.LedgeHeight, 0.15f), features: false);
            Assert.AreEqual(SnapKind.Plane, hit.kind);
            float tilt = Vector3.Angle(hit.normal, Vector3.up);
            Assert.AreEqual(S.LedgeTiltDeg, tilt, 0.001f);
        }

        [Test]
        public void AimingJustPastTheEdgeOfTheScan_SnapsTheOuterCorner()
        {
            // The wall's top-left corner (-4, 6.5, 0); aim 1 cm beyond it into the sky: no mesh under the ray.
            var world = m_Content.transform.TransformPoint(new Vector3(-4.01f, 6.51f, 0f));
            var eye = m_Content.transform.TransformPoint(Eye);
            Assert.IsFalse(Physics.Raycast(eye, (world - eye).normalized, 50f, SceneLayers.SceneSurfaceMask), "test needs a ray that misses");
            Assert.IsTrue(SnapService.TryRaySnap(new Ray(eye, (world - eye).normalized), out var hit, 50f));
            Assert.AreEqual(SnapKind.Corner, hit.kind);
            Assert.AreEqual(0f, Vector3.Distance(hit.point, m_Content.transform.TransformPoint(new Vector3(-4f, 6.5f, 0f))), 1e-4f);
            SnapService.Enabled = false;
            Assert.IsFalse(SnapService.TryRaySnap(new Ray(eye, (world - eye).normalized), out _, 50f), "snapping off: a miss is a miss");
        }

        [Test]
        public void SnappingOff_GivesTheRawSurfacePoint()
        {
            SnapService.Enabled = false;
            var target = new Vector3(-S.WindowWidth / 2f - 0.01f, S.SillHeight + S.WindowHeight + 0.01f, 0f);   // wall, 1 cm off the corner
            var hit = Aim(target);
            Assert.AreEqual(SnapKind.Face, hit.kind);
            Assert.AreEqual(0f, Vector3.Distance(hit.point, m_Content.transform.TransformPoint(target)), 0.002f);
        }

        [Test]
        public void SetScaleFromAKnownDimension_RescalesContentAndSnapsStayExact()
        {
            float factor = 0;
            Action<float> onRescale = f => factor = f;
            m_SceneRoot.Rescaled += onRescale;
            try
            {
                m_SceneRoot.Rescale(1.47f);
                Assert.AreEqual(1.47f, factor, 1e-6f);
                Assert.AreEqual(1.47f, m_SceneRoot.Calibration, 1e-6f);
                float hw = S.WindowWidth / 2f, y = S.SillHeight + S.WindowHeight;
                var a = Aim(new Vector3(-hw, y, 0f) + new Vector3(0.006f, -0.004f, 0));
                var b = Aim(new Vector3(hw, y, 0f) + new Vector3(-0.005f, 0.007f, 0));
                Assert.AreEqual(SnapKind.Corner, a.kind);
                float width = Vector3.Distance(m_SceneRoot.transform.InverseTransformPoint(a.point), m_SceneRoot.transform.InverseTransformPoint(b.point));
                Assert.AreEqual(S.WindowWidth * 1.47f, width, 0.0015f, "readings grow with the calibration");
                Assert.AreEqual(1.47f * 3f, m_SceneRoot.PackageToRoot(new Vector3(3f, 0, 0)).x, 1e-4f);
            }
            finally { m_SceneRoot.Rescaled -= onRescale; }
        }

        [Test]
        public void Tabletop_OneToFifty_StillSnapsCorners()
        {
            var t = m_Root.transform;
            var prev = t.localScale;
            try
            {
                t.localScale = Vector3.one * 0.02f;
                SnapService.Scale = 0.02f;
                Physics.SyncTransforms();
                float hw = S.WindowWidth / 2f, y = S.SillHeight + S.WindowHeight;
                var a = Aim(new Vector3(-hw + 0.01f, y - 0.005f, 0f));
                Assert.AreEqual(SnapKind.Corner, a.kind);
                Assert.AreEqual(0f, Vector3.Distance(m_SceneRoot.transform.InverseTransformPoint(a.point), new Vector3(-hw, y, 0f)), 1e-4f);
            }
            finally { t.localScale = prev; SnapService.Scale = 1f; Physics.SyncTransforms(); }
        }
    }

    public class SpawnApproachTests
    {
        [Test]
        public void SmallIndoorScan_StepsInUntilItFillsTheView_AFacadeStaysPut()
        {
            // The kitchen at altitude scale: ~1 m deep, 3 m wide, spawned 4 m back from its near face.
            var kitchen = new Bounds(new Vector3(1.165f, -0.26f, -0.12f), new Vector3(0.95f, 1.84f, 2.96f));
            float step = SceneStreamer.ApproachDistance(kitchen, new Vector3(-3.3f, -1.18f, 0.22f), Vector3.right, 1.2f);
            Assert.AreEqual(3.99f - 1.48f, step, 0.01f, "stand half its width (1.48 m) from the near face");
            // A 9 m facade 4 m away already spans ~90°: no step.
            var facade = new Bounds(new Vector3(0f, 3.5f, 0f), new Vector3(9f, 7f, 0.6f));
            Assert.AreEqual(0f, SceneStreamer.ApproachDistance(facade, new Vector3(0f, 0f, 4.3f), Vector3.back, 1.2f), 1e-4f);
            // Narrow scene: never closer than the minimum; inside or looking past it: stay.
            var post = new Bounds(Vector3.zero, new Vector3(0.4f, 2f, 0.4f));
            Assert.AreEqual(5f - 0.2f - 1.2f, SceneStreamer.ApproachDistance(post, new Vector3(-5f, 0f, 0f), Vector3.right, 1.2f), 1e-4f);
            Assert.AreEqual(0f, SceneStreamer.ApproachDistance(post, Vector3.zero, Vector3.right, 1.2f));
            Assert.AreEqual(0f, SceneStreamer.ApproachDistance(post, new Vector3(-5f, 0f, 3f), Vector3.right, 1.2f));
        }
    }

    public class PlacementGuideTests
    {
        [Test]
        public void EdgesAreClippedToTheSphereAroundThePart_AndTheOnesThroughItsBoxAreFound()
        {
            var a = new Vector3(-2f, 0f, 0f); var b = new Vector3(2f, 0f, 0f);
            Assert.IsTrue(AirTools.Parts.PlacementGuides.ClipToSphere(ref a, ref b, Vector3.zero, 0.35f));
            Assert.AreEqual(-0.35f, a.x, 1e-4f); Assert.AreEqual(0.35f, b.x, 1e-4f);
            var c = new Vector3(-2f, 1f, 0f); var d = new Vector3(2f, 1f, 0f);
            Assert.IsFalse(AirTools.Parts.PlacementGuides.ClipToSphere(ref c, ref d, Vector3.zero, 0.35f), "passes 1 m away");
            var e = new Vector3(0.1f, 0f, 0f); var f = new Vector3(0.2f, 0f, 0f);
            Assert.IsTrue(AirTools.Parts.PlacementGuides.ClipToSphere(ref e, ref f, Vector3.zero, 0.35f));
            Assert.AreEqual(0.1f, e.x, 1e-5f, "inside: untouched");

            var box = new Bounds(Vector3.zero, new Vector3(0.1f, 0.1f, 0.1f));
            Assert.IsTrue(AirTools.Parts.PlacementGuides.SegmentHitsBox(new Vector3(-1f, 0.02f, 0f), new Vector3(1f, 0.02f, 0f), box), "cuts through");
            Assert.IsFalse(AirTools.Parts.PlacementGuides.SegmentHitsBox(new Vector3(-1f, 0.06f, 0f), new Vector3(1f, 0.06f, 0f), box), "passes above");
            Assert.IsFalse(AirTools.Parts.PlacementGuides.SegmentHitsBox(new Vector3(0.2f, 0f, 0f), new Vector3(1f, 0f, 0f), box), "stops short");
        }
    }

    public class SceneCameraTests
    {
        [Test]
        public void CamerasJson_RoundTrips_PoseAndPinRayThroughAKnownPoint()
        {
            var cams = SyntheticFacadeBuilder.CreateCameras();
            var json = PackageFixtures.CamerasJson(cams, 640, 480);
            var parsed = SceneCameras.Parse(json);
            Assert.AreEqual(cams.Length, parsed.Count);
            for (int i = 0; i < cams.Length; i++)
            {
                var info = SceneCameras.ToInfo(parsed[i], i);
                Assert.AreEqual(0f, Vector3.Distance(info.position, cams[i].position), 1e-4f, $"cam {i} position");
                Assert.Less(Quaternion.Angle(info.rotation, cams[i].rotation), 0.01f, $"cam {i} rotation (X flip + OpenCV y-down)");
                Assert.AreEqual(cams[i].verticalFovDeg, info.verticalFovDeg, 0.01f);
                Assert.AreEqual(i, info.id);
                Assert.AreEqual($"{i:0000}", info.key);

                // Pin: project the window centre, cast back through that pixel; the ray must pass through the point.
                var target = S.WindowCentre;
                Assert.IsTrue(SceneCameras.Project(parsed[i], target, out var uv));
                var ray = SceneCameras.PixelRay(parsed[i], uv.x, uv.y);
                float miss = Vector3.Cross(ray.direction, target - ray.origin).magnitude;
                Assert.Less(miss, 0.001f, $"cam {i}: pin ray misses by {miss * 1000:0.0} mm");
            }
            var near = SceneCameras.NearestLookingAt(parsed, S.WindowCentre, 3);
            Assert.AreEqual(3, near.Count);
        }

        [Test]
        public void PixelRay_TopLeftIsUpAndLeftOfCentre()
        {
            var cams = SyntheticFacadeBuilder.CreateCameras();
            var c = SceneCameras.Parse(PackageFixtures.CamerasJson(cams, 640, 480))[6];
            var info = SceneCameras.ToInfo(c, 6);
            var centre = SceneCameras.PixelRay(c, 0.5f, 0.5f).direction;
            var topLeft = SceneCameras.PixelRay(c, 0.1f, 0.1f).direction;
            var local = Quaternion.Inverse(info.rotation) * topLeft;
            Assert.Less(local.x, 0f, "image left = camera left");
            Assert.Greater(local.y, 0f, "image top = up (OpenCV y is down)");
            Assert.Greater(Vector3.Dot(centre, info.rotation * Vector3.forward), 0.9999f);
        }
    }
}
