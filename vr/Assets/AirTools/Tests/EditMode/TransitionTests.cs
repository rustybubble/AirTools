using AirTools.Core;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// presence.md S1 acceptance (a)–(c): the grow pose, the anchor under the feet and the log-linear scale.
    public class TransitionMathTests
    {
        static readonly Pose Table = new Pose(new Vector3(0.12f, 0.75f, 3.45f), Quaternion.Euler(0f, 17f, 0f));
        static readonly Pose World = new Pose(Vector3.zero, Quaternion.identity);
        static readonly Vector3 Feet = new Vector3(0.3f, 0f, 4.1f);
        const float TableScale = 0.02f;

        static Pose Grow(float e, out float s) =>
            TransitionMath.GrowPose(e, Table, TableScale, World, 1f, TransitionMath.AnchorLocal(World, 1f, Feet), Feet, out s);

        [Test]
        public void GrowPoseStartsExactlyOnTheTableAndEndsExactlyAtTheWorldPose()
        {
            var p0 = Grow(0f, out float s0);
            Assert.That(Vector3.Distance(p0.position, Table.position), Is.LessThan(1e-4f));
            Assert.That(Quaternion.Angle(p0.rotation, Table.rotation), Is.LessThan(0.01f));
            Assert.AreEqual(TableScale, s0, 1e-6f);
            var p1 = Grow(1f, out float s1);
            Assert.That(Vector3.Distance(p1.position, World.position), Is.LessThan(1e-4f));
            Assert.That(Quaternion.Angle(p1.rotation, World.rotation), Is.LessThan(0.01f));
            Assert.AreEqual(1f, s1, 1e-6f);
        }

        [Test]
        public void GrowPoseHandlesARotatedWorldPoseAndScale()
        {
            var world = new Pose(new Vector3(2f, 0f, -1f), Quaternion.Euler(0f, -120f, 0f));
            var anchor = TransitionMath.AnchorLocal(world, 1.5f, Feet);
            var p1 = TransitionMath.GrowPose(1f, Table, TableScale, world, 1.5f, anchor, Feet, out float s1);
            Assert.That(Vector3.Distance(p1.position, world.position), Is.LessThan(1e-4f));
            Assert.That(Quaternion.Angle(p1.rotation, world.rotation), Is.LessThan(0.01f));
            Assert.AreEqual(1.5f, s1, 1e-5f);
            // The anchor really is the point under the feet at full size.
            Assert.That(Vector3.Distance(world.position + world.rotation * (anchor * 1.5f), Feet), Is.LessThan(1e-4f));
        }

        [Test]
        public void AnchorGlidesToTheFeetMonotonically()
        {
            var anchor = TransitionMath.AnchorLocal(World, 1f, Feet);
            float prev = float.MaxValue;
            for (int i = 0; i <= 40; i++)
            {
                float t = i / 40f;
                var pose = Grow(TransitionMath.Ease(t), out float s);
                var anchorNow = pose.position + pose.rotation * (anchor * s);
                float d = Vector3.Distance(anchorNow, Feet);
                if (i > 0 && i < 40) Assert.Less(d, prev, $"t={t}: the anchor's distance to the feet decreases strictly");
                prev = d;
            }
            Assert.That(prev, Is.LessThan(1e-4f), "ends under the feet");
        }

        [Test]
        public void ScaleIsLogLinearAndMonotonic()
        {
            Assert.AreEqual(Mathf.Sqrt(0.02f), TransitionMath.ScaleAt(0.5f, 0.02f, 1f), 1e-4f, "s(0.5) = √0.02");
            float prev = 0f;
            for (int i = 0; i <= 50; i++)
            {
                float s = TransitionMath.ScaleAt(TransitionMath.Ease(i / 50f), 0.02f, 1f);
                if (i > 0 && i < 50) Assert.Greater(s, prev, "monotonic");
                prev = s;
            }
            Assert.AreEqual(1f, prev, 1e-6f);
            // Constant zoom speed: equal steps of e multiply the scale by the same factor.
            float r1 = TransitionMath.ScaleAt(0.3f, 0.02f, 1f) / TransitionMath.ScaleAt(0.2f, 0.02f, 1f);
            float r2 = TransitionMath.ScaleAt(0.8f, 0.02f, 1f) / TransitionMath.ScaleAt(0.7f, 0.02f, 1f);
            Assert.AreEqual(r1, r2, 1e-4f);
        }

        [Test]
        public void EaseAndSchedulesHitTheirEnds()
        {
            Assert.AreEqual(0f, TransitionMath.Ease(0f), 1e-6f);
            Assert.AreEqual(0.5f, TransitionMath.Ease(0.5f), 1e-6f);
            Assert.AreEqual(1f, TransitionMath.Ease(1f), 1e-6f);
            Assert.AreEqual(0.05f, TransitionMath.PourRadius(0f), 1e-6f);
            Assert.AreEqual(90.05f, TransitionMath.PourRadius(1f), 1e-4f);
            Assert.AreEqual(0.04f, TransitionMath.PourBand(1f), 1e-6f);
            Assert.AreEqual(0.4f, TransitionMath.PourBand(50f), 1e-6f);
            Assert.AreEqual(0f, TransitionMath.GrowSkyRadius(0.59f));
            Assert.AreEqual(90f, TransitionMath.GrowSkyRadius(1f), 1e-4f);
            Assert.AreEqual(0f, TransitionMath.SkyAngle(0.6f, 0.6f));
            Assert.AreEqual(Mathf.PI, TransitionMath.SkyAngle(1f, 0.6f), 1e-5f);
            Assert.AreEqual(1.4f, TransitionMath.Duration(TransitionKind.GrowIn), 1e-6f);
            Assert.AreEqual(1.6f, TransitionMath.Duration(TransitionKind.ShrinkOut), 1e-6f);
        }
    }
}

namespace AirTools.Tests
{
    using System.Linq;
    using AirTools.Dev;
    using AirTools.Editor;
    using AirTools.Input;
    using AirTools.Notes;
    using AirTools.Parts;
    using AirTools.Scene;
    using AirTools.Tools;
    using AirTools.UI;
    using UnityEditor;
    using S = AirTools.Scene.SyntheticFacadeSpec;

    /// presence.md S1 acceptance (d)–(g): the director on the real facade, stepped through ModeController.Tick.
    public class TransitionDirectorTests
    {
        GameObject m_App, m_Rig, m_Head, m_Root, m_Sky;
        SceneRoot m_Scene;
        ModeController m_Mode;
        TabletopController m_Table;
        TransitionDirector m_Director;
        Camera m_Camera;
        int m_Swaps;
        bool m_ReducedMotionWas;

        [SetUp]
        public void SetUp()
        {
            m_ReducedMotionWas = UiSettings.ReducedMotion;
            UiSettings.ReducedMotion = false;
            AppState.Reset();
            Notebook.Clear();
            RevealField.Reset();
            m_Root = new GameObject("SceneRoot");
            m_Scene = m_Root.AddComponent<SceneRoot>();
            var facade = SyntheticFacadeBuilder.CreateHierarchy();
            facade.transform.SetParent(m_Root.transform, false);
            m_Scene.SetContent(null, facade);
            m_Rig = new GameObject("rig");
            m_Rig.transform.SetPositionAndRotation(S.SpawnPosition, Quaternion.Euler(0f, S.SpawnYawDeg, 0f));
            m_Head = new GameObject("head");
            m_Head.transform.SetParent(m_Rig.transform, false);
            m_Head.transform.localPosition = new Vector3(0.1f, 1.6f, 0.05f);   // not exactly over the rig origin
            m_Camera = m_Head.AddComponent<Camera>();
            m_Sky = new GameObject("sky");
            m_Sky.AddComponent<MeshFilter>();
            m_Sky.AddComponent<MeshRenderer>();
            var sky = m_Sky.AddComponent<SkyDome>();
            sky.head = m_Head.transform;
            m_App = new GameObject("app");
            m_Mode = m_App.AddComponent<ModeController>();
            m_Mode.sceneRoot = m_Scene;
            m_Mode.cameras = new[] { m_Camera };
            m_Mode.skyDome = sky;
            m_Table = m_App.AddComponent<TabletopController>();
            m_Table.root = m_Scene; m_Table.rig = m_Rig.transform; m_Table.head = m_Head.transform; m_Table.mode = m_Mode;
            m_Table.fitToTable = false;   // modelview: the transitions are checked at the fixed 1:50
            m_Director = m_App.AddComponent<TransitionDirector>();
            m_Director.mode = m_Mode; m_Director.sceneRoot = m_Scene; m_Director.tabletop = m_Table; m_Director.sky = sky;
            m_Director.rig = m_Rig.transform; m_Director.head = m_Head.transform;
            m_Mode.director = m_Director;
            m_Mode.Subscribe();   // OnEnable doesn't run in EditMode
            m_Mode.Swapped += OnSwap;   // what TabletopController.OnEnable would do, counted
            m_Mode.ApplyVisuals(AppState.Mode);
            m_Swaps = 0;
            Physics.SyncTransforms();
        }

        void OnSwap(AppMode m) { m_Swaps++; m_Table.Apply(m == AppMode.Tabletop); }

        [TearDown]
        public void TearDown()
        {
            m_Mode.Swapped -= OnSwap;
            m_Mode.Unsubscribe();
            Object.DestroyImmediate(m_App); Object.DestroyImmediate(m_Rig); Object.DestroyImmediate(m_Root); Object.DestroyImmediate(m_Sky);
            SnapService.Scale = 1f;
            RevealField.Reset();
            Notebook.Clear();
            AppState.Reset();
            UiSettings.ReducedMotion = m_ReducedMotionWas;
        }

        /// Tick at 72 Hz until the mode controller is idle; returns the seconds it took.
        float Run(float limit = 4f)
        {
            float t = 0f;
            const float dt = 1f / 72f;
            while (m_Mode.IsTransitioning && t < limit) { m_Mode.Tick(dt); t += dt; }
            return t;
        }

        void ToTabletop()
        {
            AppState.Set(AppMode.Tabletop);
            Run();
            m_Swaps = 0;
        }

        [Test]
        public void PourInEndsInWorldWithinOnePointThreeSecondsWithoutBlack()
        {
            AppState.Set(AppMode.World);
            Assert.AreEqual(TransitionKind.PourIn, m_Director.Kind);
            Assert.IsTrue(m_Scene.IsVisible, "the world pours in while the room is still there");
            Assert.AreEqual(0f, m_Camera.backgroundColor.a, "cameras clear transparent: passthrough behind");
            float maxFade = 0f, t = 0f;
            const float dt = 1f / 72f;
            while (m_Mode.IsTransitioning && t < 4f) { m_Mode.Tick(dt); t += dt; maxFade = Mathf.Max(maxFade, m_Mode.FadeAlpha); }
            Assert.LessOrEqual(t, 1.3f, "Passthrough → World ≤ 1.3 s");
            Assert.AreEqual(0f, maxFade, "no black fade");
            Assert.AreEqual(AppMode.World, m_Mode.VisualMode);
            Assert.LessOrEqual(m_Swaps, 1, "Swapped at most once");
            Assert.IsFalse(RevealField.SphereOn, "reveal keyword off afterwards (early-Z back)");
            Assert.AreEqual(Mathf.PI, RevealField.SkyAngle, 1e-4f, "the whole sky");
            Assert.AreEqual(CameraClearFlags.SolidColor, m_Camera.clearFlags);
            Assert.AreEqual(1f, m_Camera.backgroundColor.a, "World: opaque behind the dome");
            Assert.IsTrue(m_Sky.GetComponent<MeshRenderer>().enabled, "the dome is the World sky");
        }

        [Test]
        public void PourRevealsNearFirstAndTheSkyLast()
        {
            AppState.Set(AppMode.World);
            var centre = RevealField.Centre;
            var ahead = m_Head.transform.position + Vector3.ProjectOnPlane(m_Head.transform.forward, Vector3.up).normalized * 1.2f;
            Assert.That(Vector3.Distance(centre, ahead), Is.LessThan(1e-3f), "sphere centre 1.2 m ahead at eye height");
            m_Mode.Tick(0.2f);
            Assert.IsTrue(RevealField.SphereOn);
            Assert.AreEqual(0f, RevealField.SkyAngle, 1e-4f, "the sky waits for the building");
            float r1 = RevealField.Radius;
            m_Mode.Tick(0.5f);
            Assert.Greater(RevealField.Radius, r1);
            Assert.Greater(RevealField.SkyAngle, 0f, "then the sky closes");
            Run();
            Assert.AreEqual(AppMode.World, m_Mode.VisualMode);
        }

        [Test]
        public void GrowInEndsInWorldWithinOnePointFiveSecondsExactlyWhereItWas()
        {
            var world = new Pose(m_Root.transform.position, m_Root.transform.rotation);
            ToTabletop();
            Assert.IsTrue(m_Table.OnTable);
            Assert.AreEqual(0.02f, m_Root.transform.lossyScale.x, 1e-6f);
            var anchor = m_Table.StepInAnchorLocal();
            AppCommands.StepIn();
            Assert.AreEqual(TransitionKind.GrowIn, m_Director.Kind);
            float prevScale = 0f, prevDist = float.MaxValue, t = 0f;
            const float dt = 1f / 72f;
            var feet = m_Table.FeetWorld();
            var phases = new System.Collections.Generic.List<TransitionPhase>();
            while (m_Mode.IsTransitioning && t < 4f)
            {
                m_Mode.Tick(dt); t += dt;
                if (!m_Director.Playing) break;
                if (phases.Count == 0 || phases[phases.Count - 1] != m_Director.Phase) phases.Add(m_Director.Phase);
                float s = m_Root.transform.lossyScale.x;
                Assert.GreaterOrEqual(s, prevScale - 1e-6f, "scale rises monotonically");
                float d = Vector3.Distance(m_Root.transform.TransformPoint(anchor), feet);
                Assert.LessOrEqual(d, prevDist + 1e-5f, "the pin's foot glides toward your feet");
                prevScale = s; prevDist = d;
            }
            Assert.LessOrEqual(t, 1.5f, "Tabletop → World ≤ 1.5 s");
            CollectionAssert.AreEqual(new[] { TransitionPhase.Grow, TransitionPhase.Sky }, phases);
            Assert.AreEqual(AppMode.World, m_Mode.VisualMode);
            Assert.AreEqual(0, m_Swaps, "grow places the scene itself (no Swapped)");
            Assert.IsFalse(m_Table.OnTable);
            Assert.AreEqual(1f, m_Root.transform.lossyScale.x, 1e-6f);
            Assert.AreEqual(1f, SnapService.Scale, 1e-6f);
            Assert.That(Vector3.Distance(m_Root.transform.position, world.position), Is.LessThan(1e-4f));
            Assert.That(Quaternion.Angle(m_Root.transform.rotation, world.rotation), Is.LessThan(0.01f));
        }

        [Test]
        public void ExitAfterStepInShrinksBackOntoTheTable()
        {
            ToTabletop();
            var tablePos = m_Root.transform.position;
            AppCommands.StepIn();
            Run();
            Assert.AreEqual(AppMode.World, AppState.Mode);
            AppCommands.ToggleChest();   // "Exit world"
            Assert.AreEqual(AppMode.Tabletop, AppState.Mode, "you stepped in from the table: exit goes back to it");
            Assert.AreEqual(TransitionKind.ShrinkOut, m_Director.Kind);
            m_Mode.Tick(0.2f);
            Assert.AreEqual(TransitionPhase.Sky, m_Director.Phase, "the sky opens first");
            Assert.AreEqual(1f, m_Root.transform.lossyScale.x, 1e-5f, "full size while the sky opens");
            float t = 0.2f + Run();
            Assert.LessOrEqual(t, 1.7f);
            Assert.AreEqual(AppMode.Tabletop, m_Mode.VisualMode);
            Assert.IsTrue(m_Table.OnTable);
            Assert.AreEqual(0.02f, m_Root.transform.lossyScale.x, 1e-6f);
            Assert.AreEqual(0.02f, SnapService.Scale, 1e-6f);
            Assert.That(Vector3.Distance(m_Root.transform.position, tablePos), Is.LessThan(1e-3f), "back where it floated (modelwheel: in front of you; placed afresh from the same head)");
            Assert.AreEqual(m_Head.transform.position.y - ModelViewLayout.DropBelowEye, m_Table.ModelCentreWorld.y, 1e-3f, "modelwheel: it shrinks to eye height, not onto a table");
            Assert.IsFalse(m_Sky.GetComponent<MeshRenderer>().enabled, "no sky in tabletop");
            // Exit again from the table: the model scans out and you're in passthrough.
            AppCommands.ToggleChest();
            Assert.AreEqual(TransitionKind.Dematerialize, m_Director.Kind);
            Run();
            Assert.AreEqual(AppMode.Passthrough, m_Mode.VisualMode);
            Assert.IsFalse(m_Scene.IsVisible);
            Assert.AreEqual(1f, m_Root.transform.lossyScale.x, 1e-6f, "back at full size, hidden");
        }

        [Test]
        public void EnteringByThePillStillExitsToPassthrough()
        {
            AppCommands.ToggleChest();
            Run();
            AppCommands.ToggleChest();
            Assert.AreEqual(AppMode.Passthrough, AppState.Mode);
            Assert.AreEqual(TransitionKind.PourOut, m_Director.Kind);
            Run();
            Assert.AreEqual(AppMode.Passthrough, m_Mode.VisualMode);
            Assert.IsFalse(m_Scene.IsVisible);
        }

        [Test]
        public void ReducedMotionTakesTheFadePath()
        {
            UiSettings.ReducedMotion = true;
            AppState.Set(AppMode.World);
            Assert.IsFalse(m_Director.Playing, "no animated transition");
            m_Mode.Tick(m_Mode.fadeOutSeconds);
            Assert.Greater(m_Mode.FadeAlpha, 0.9f, "black at the midpoint (the current fade)");
            Run();
            Assert.AreEqual(AppMode.World, m_Mode.VisualMode);
            // And with Reduce motion, Exit after a step-in goes to passthrough as before.
            UiSettings.ReducedMotion = false;
            AppState.Set(AppMode.Tabletop); Run();
            UiSettings.ReducedMotion = true;
            AppCommands.StepIn(); Run();
            Assert.AreEqual(AppMode.World, m_Mode.VisualMode);
            AppCommands.ToggleChest();
            Assert.AreEqual(AppMode.Passthrough, AppState.Mode);
        }

        [Test]
        public void ChangingModeMidTransitionEndsInTheLatestMode()
        {
            AppState.Set(AppMode.World);
            m_Mode.Tick(0.3f);
            AppState.Set(AppMode.Passthrough);
            Assert.AreEqual(TransitionKind.PourIn, m_Director.Kind, "the running transition finishes first");
            Run(6f);
            Assert.AreEqual(AppMode.Passthrough, m_Mode.VisualMode);
            Assert.IsFalse(m_Scene.IsVisible);
            Assert.IsFalse(m_Mode.IsTransitioning);
        }

        [Test]
        public void TapeReadsTheSameAfterSteppingIn()
        {
            ToTabletop();
            AppCommands.StepIn();
            Run();
            Physics.SyncTransforms();
            var hub = m_App.AddComponent<ToolInputHub>();
            var measure = m_App.AddComponent<MeasureTool>();
            measure.frame = m_Root.transform;
            measure.SetInput(hub);
            measure.Equip(true);
            try
            {
                var win = MeasureScenarios.M2().First(s => s.Id == "M2.measure.window.width");
                var r = MeasureScenarios.Run(win, measure, hub, m_Root.transform, 1);
                Assert.IsTrue(r.Passed, r.Detail);
                Assert.AreEqual(S.WindowWidth, Notebook.Last.ValueSI, 0.010, "1.500 ± 0.010 m after stepping in");
            }
            finally { if (measure.viewRoot != null) Object.DestroyImmediate(measure.viewRoot.gameObject); }
        }

        [Test]
        public void PinStandsWhereYouWillStand()
        {
            ToTabletop();
            var anchor = m_Table.StepInAnchorLocal();
            var pinWorld = m_Root.transform.TransformPoint(anchor);
            Assert.AreEqual(m_Root.transform.position.y, pinWorld.y, 1e-4f, "on the model's ground");
            AppCommands.StepIn();
            Run();
            var feet = m_Table.FeetWorld();
            Assert.That(Vector3.Distance(m_Root.transform.TransformPoint(anchor), feet), Is.LessThan(1e-3f), "the pin's point is under your feet at 1:1");
        }
    }

    /// (f) RevealField mirrors the SceneReveal shader: the globals it sets give the same inside/outside answer.
    public class RevealFieldTests
    {
        [TearDown] public void TearDown() => RevealField.Reset();

        [Test]
        public void ContainsAgreesWithTheShaderFormulaOnRandomPoints()
        {
            var rng = new System.Random(11);
            float R() => (float)rng.NextDouble();
            var centre = new Vector3(0.3f, 1.6f, 2.8f);
            RevealField.SetSphere(centre, 7.5f, 0.3f, Color.cyan);
            Assert.IsTrue(Shader.IsKeywordEnabled(RevealField.SphereKeyword));
            var g = Shader.GetGlobalVector("_AirReveal");
            float band = Shader.GetGlobalFloat("_AirRevealBand");
            int inside = 0;
            for (int i = 0; i < 1000; i++)
            {
                var p = new Vector3(R() * 30f - 15f, R() * 20f - 5f, R() * 30f - 15f);
                // The shader: inside = _AirReveal.w − distance(posWS, _AirReveal.xyz); clip(inside).
                float dx = p.x - g.x, dy = p.y - g.y, dz = p.z - g.z;
                float shaderInside = g.w - Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
                Assert.AreEqual(shaderInside >= 0f, RevealField.Contains(p), $"point {p}");
                if (shaderInside >= 0f)
                {
                    inside++;
                    Assert.AreEqual(Mathf.Clamp01(1f - shaderInside / band), RevealField.Glow(p), 1e-4f);
                }
            }
            Assert.That(inside, Is.InRange(50, 950), "the sample straddles the surface");
            RevealField.Clear();
            Assert.IsFalse(Shader.IsKeywordEnabled(RevealField.SphereKeyword));
            Assert.IsTrue(RevealField.Contains(new Vector3(100f, 0f, 0f)), "no effect: everything draws");
        }

        [Test]
        public void SkyConeMatchesTheDomeShader()
        {
            RevealField.SetSky(Vector3.forward, Mathf.PI / 3f, 0.05f);
            var g = Shader.GetGlobalVector("_AirSkyReveal");
            var rng = new System.Random(5);
            for (int i = 0; i < 500; i++)
            {
                var d = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f).normalized;
                float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(d, new Vector3(g.x, g.y, g.z).normalized), -1f, 1f));
                Assert.AreEqual(g.w - ang >= 0f, RevealField.SkyContains(d));
            }
            RevealField.Clear();
            Assert.IsTrue(RevealField.SkyContains(Vector3.back), "cleared: the whole dome");
        }

        [Test]
        public void DomeMeshIsClosedAndFacesOutward()
        {
            var m = SkyDome.Icosphere(3, 80f);
            Assert.AreEqual(1280, m.triangles.Length / 3);
            var v = m.vertices; var t = m.triangles;
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                Assert.Greater(Vector3.Dot(Vector3.Cross(b - a, c - a), a + b + c), 0f);
            }
            foreach (var p in v) Assert.AreEqual(80f, p.magnitude, 1e-3f);
            Object.DestroyImmediate(m);
        }
    }
}
