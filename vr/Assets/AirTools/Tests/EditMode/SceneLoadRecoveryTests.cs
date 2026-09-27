using AirTools.Core;
using AirTools.Scene;
using AirTools.Tools;
using NUnit.Framework;
using UnityEngine;

namespace AirTools.Tests
{
    /// fix-ux (headset 2026-09-26, builds from 15:40 on): the kitchen load logged nothing and the heads-up line said
    /// "Loading the site…" for the whole session. SceneStreamer.Loading was set in the load coroutine and cleared only in
    /// its finally, which Unity never runs for a stopped coroutine, and no await had a timeout. Now Loading is derived
    /// from a SceneLoadWatchdog (plain fields), a load that stalls gives up with a Retry, and a stale coroutine is ignored.
    public class SceneLoadRecoveryTests
    {
        // ---------------- the watchdog (pure) ----------------

        static bool Loading(SceneLoadWatchdog w, float now) => w.Active && !w.TimedOut(now);

        [Test]
        public void Watchdog_NoProgressForStallSeconds_TimesOut_AndAbortClearsLoading()
        {
            var w = new SceneLoadWatchdog { StallSeconds = 20f, TotalSeconds = 60f };
            int gen = w.Begin("kitchen", 100f);
            Assert.IsTrue(Loading(w, 100f));
            Assert.IsTrue(w.Step(gen, "scene.json", 101f));
            Assert.IsTrue(w.Step(gen, "import", 102f));
            Assert.IsTrue(Loading(w, 121.9f), "19.9 s without progress: still loading");
            Assert.IsTrue(w.TimedOut(122.1f), "20 s without progress");
            Assert.IsFalse(Loading(w, 122.1f), "Loading reads false at once, before anything gives up");
            StringAssert.Contains("no progress for 20 s", w.Why(122.1f));
            Assert.AreEqual("import", w.Stage, "the stuck stage, for the Warn");
            Assert.IsTrue(w.Abort());
            Assert.IsFalse(w.Active);
            Assert.IsFalse(w.Abort(), "nothing left to abort");
        }

        [Test]
        public void Watchdog_StoppedCoroutine_DoesNotLeaveLoadingStuck()
        {
            // The coroutine begins, reaches the imports and is stopped: it never calls End (Unity skips its finally).
            var w = new SceneLoadWatchdog();
            int gen = w.Begin("kitchen", 0f);
            w.Step(gen, "import", 0.5f);
            Assert.IsTrue(Loading(w, 5f));
            Assert.IsFalse(Loading(w, 25f), "the watchdog, not the dead coroutine, decides");
            // The streamer gives up (the abort path): a new load starts fresh.
            Assert.IsTrue(w.Abort());
            int next = w.Begin("kitchen", 26f);
            Assert.AreNotEqual(gen, next);
            Assert.IsTrue(Loading(w, 26f));
        }

        [Test]
        public void Watchdog_AStaleGenerationFinishingLater_IsIgnored()
        {
            var w = new SceneLoadWatchdog();
            int old = w.Begin("kitchen", 0f);
            Assert.IsTrue(w.Abort(), "timed out: given up");
            int cur = w.Begin("kitchen", 30f);   // Retry
            w.Step(cur, "scene.json", 30.1f);
            // The old coroutine wakes up (its import finished after all) and tries to report and finish.
            Assert.IsFalse(w.IsCurrent(old));
            Assert.IsFalse(w.Step(old, "swap mesh", 31f));
            Assert.AreEqual("scene.json", w.Stage, "a stale step changes nothing");
            Assert.IsFalse(w.End(old));
            Assert.IsTrue(w.Active, "the retry is still loading");
            Assert.IsTrue(w.End(cur));
            Assert.IsFalse(w.Active);
            // An aborted load's coroutine can't end or touch it either.
            int third = w.Begin("kitchen", 40f);
            w.Abort();
            Assert.IsFalse(w.Touch(third, 41f));
            Assert.IsFalse(w.End(third));
        }

        [Test]
        public void Watchdog_TotalLimitCountsWorkNotDownloads()
        {
            var w = new SceneLoadWatchdog { StallSeconds = 20f, TotalSeconds = 60f };
            int gen = w.Begin("kitchen", 0f);
            w.Step(gen, "file mesh.r1.glb", 1f);
            w.NetBegin(gen, 1f);
            // A slow 90 s download that keeps receiving bytes is not a stall and doesn't use up the 60 s.
            for (float t = 2f; t <= 91f; t += 3f) Assert.IsTrue(w.Touch(gen, t));
            w.NetEnd(gen, 91f);
            Assert.IsFalse(w.TimedOut(92f));
            Assert.That(w.WorkSeconds(92f), Is.EqualTo(2f).Within(1e-3f));
            // Work that keeps stepping but never finishes: over 60 s of work.
            for (float t = 95f; t <= 150f; t += 5f) w.Step(gen, "swap", t);
            Assert.IsTrue(w.TimedOut(152f));
            StringAssert.Contains("over 60 s", w.Why(152f));
        }

        [Test]
        public void Watchdog_ADownloadThatStopsReceivingBytes_Stalls()
        {
            var w = new SceneLoadWatchdog { StallSeconds = 20f };
            int gen = w.Begin("kitchen", 0f);
            w.NetBegin(gen, 1f);
            w.Touch(gen, 5f);
            Assert.IsFalse(w.TimedOut(24f));
            Assert.IsTrue(w.TimedOut(25.5f));
        }

        // ---------------- the rail: Retry, not "Loading…" ----------------

        [Test]
        public void NextStep_AfterAFailedLoad_ShowsRetry_NotLoading()
        {
            var s = NextStepTests.Base();
            s.Scene = SceneKind.Facade;          // the kitchen didn't load: the built-in wall is showing
            s.SceneLoading = true;
            var loading = NextStep.For(s);
            Assert.AreEqual(RuleId.R07, loading.Rule);
            Assert.AreEqual("Loading the site…", loading.Status);

            s.SceneLoading = false;
            s.SceneLoadFailed = true;
            var failed = NextStep.For(s);
            Assert.AreEqual(RuleId.R46, failed.Rule);
            Assert.AreEqual("failed", failed.Variant);
            Assert.AreEqual("Couldn't load the kitchen · Retry", failed.Status);
            Assert.AreEqual(StepCommand.LoadSite, failed.Primary.Command);
            Assert.AreEqual("kitchen", failed.Primary.Arg);
            Assert.AreEqual("Retry", failed.Primary.Label);
            Assert.IsTrue(failed.PillVisible, "the Retry is one tap away");
            Assert.AreEqual(StepTone.Caution, failed.Tone);
            Assert.AreNotEqual(NextStep.Key(WithLoading(s)), NextStep.Key(s), "the rail rebuilds when the load fails");
        }

        static AppSnapshot WithLoading(AppSnapshot s) { s.SceneLoading = true; s.SceneLoadFailed = false; return s; }

        [Test]
        public void NextStep_FailedLoad_WinsOverTheToolHint_ButNotOverWork()
        {
            var s = NextStepTests.Base();
            s.Scene = SceneKind.Facade;
            s.SceneLoadFailed = true;
            s.Tool = ToolKind.Move;
            Assert.AreEqual(RuleId.R46, NextStep.Match(s), "over the Move hint");
            s.RingOpen = true;
            Assert.AreEqual(RuleId.R46, NextStep.Match(s), "like Loading: before the ring's hint");
            s.RingOpen = false;
            s.Shapes = 1;
            Assert.AreNotEqual(RuleId.R46, NextStep.Match(s), "after the first saved reading the line guides the work (Settings keeps the Retry)");
            s.Shapes = 0;
            s.Mode = AppMode.Passthrough;
            Assert.AreEqual(RuleId.R06, NextStep.Match(s), "passthrough first");
        }

        [Test]
        public void NextStep_OldFallbackCopyIsUnchanged()
        {
            var s = NextStepTests.Base();
            s.Scene = SceneKind.Facade;
            s.SceneFallback = true;
            var step = NextStep.For(s);
            Assert.AreEqual(RuleId.R46, step.Rule);
            Assert.AreEqual("", step.Variant);
            Assert.AreEqual("Reload the kitchen · this is the sample wall", step.Status);
            Assert.AreEqual("Try again", step.Primary.Label);
        }

        // ---------------- the streamer (engine: the Editor gate runs these) ----------------

        [Test]
        public void Streamer_TimedOutLoad_GivesUp_WithTheRetryState()
        {
            var go = new GameObject("streamer");
            try
            {
                var st = go.AddComponent<SceneStreamer>();
                st.startSite = "kitchen";
                int gen = st.Watchdog.Begin("kitchen", Time.realtimeSinceStartup - 30f);   // a load whose coroutine died 30 s ago
                st.Watchdog.Step(gen, "import", Time.realtimeSinceStartup - 25f);
                Assert.IsFalse(st.Loading, "derived: timed out");
                Assert.IsTrue(st.CheckWatchdog(), "gives up");
                Assert.IsFalse(st.Watchdog.Active);
                Assert.IsTrue(st.LoadFailed);
                Assert.AreEqual("kitchen", st.FailedSite);
                StringAssert.Contains("timed out at import", st.LastError);
                Assert.IsFalse(st.CheckWatchdog(), "once");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void Streamer_BackToTheBuiltInScene_MovesHomeToItsSpawn()
        {
            // Gate 2026-09-27: after the kitchen, back on the built-in facade, Home (and a demo reset) still put you at
            // the kitchen's spot: RestoreBuiltIn moved the rig to the facade's spawn but left Locomotion.Home behind.
            var go = new GameObject("streamer");
            var rig = new GameObject("rig");
            var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var package = ScriptableObject.CreateInstance<ScenePackage>();
            AirTools.Input.Locomotion loco = null;
            try
            {
                package.visualPrefab = prefab;
                package.spawnPosition = new Vector3(0f, 0f, 4f);
                package.spawnYawDeg = 180f;
                var root = new GameObject("SceneRoot").AddComponent<SceneRoot>();
                root.transform.SetParent(go.transform, false);
                var boot = go.AddComponent<AppBootstrap>();
                boot.scenePackage = package; boot.sceneRoot = root; boot.rig = rig.transform;
                loco = go.AddComponent<AirTools.Input.Locomotion>();
                loco.rig = rig.transform;
                Services.Register(loco);   // edit mode doesn't run OnEnable
                var kitchen = new Pose(new Vector3(-1.33f, -1.93f, 0.05f), Quaternion.Euler(0f, 94f, 0f));
                loco.SetHome(kitchen);     // the kitchen's spawn (SceneStreamer.ApplySpawn)
                rig.transform.SetPositionAndRotation(kitchen.position, kitchen.rotation);

                var st = go.AddComponent<SceneStreamer>();
                st.sceneRoot = root; st.rig = rig.transform;
                st.LoadBuiltIn();
                Assert.That(Vector3.Distance(rig.transform.position, new Vector3(0f, 0f, 4f)), Is.LessThan(1e-4f), "the rig at the facade's spawn");
                Assert.That(Vector3.Distance(loco.Home.position, new Vector3(0f, 0f, 4f)), Is.LessThan(1e-4f), "Home follows it");
                Assert.AreEqual(180f, loco.Home.rotation.eulerAngles.y, 0.01f);

                rig.transform.position = new Vector3(3f, 0f, 1f);   // walk off, then Home
                loco.GoHome();
                Assert.That(Vector3.Distance(rig.transform.position, new Vector3(0f, 0f, 4f)), Is.LessThan(1e-4f), "Home is the facade's spawn, not the kitchen's");
            }
            finally
            {
                if (loco != null) Services.Unregister(loco);
                Object.DestroyImmediate(go); Object.DestroyImmediate(rig); Object.DestroyImmediate(prefab); Object.DestroyImmediate(package);
            }
        }

        [Test]
        public void Streamer_DisabledMidLoad_ClearsLoading()
        {
            var go = new GameObject("streamer");
            try
            {
                var st = go.AddComponent<SceneStreamer>();
                st.Watchdog.Begin("kitchen", Time.realtimeSinceStartup);
                Assert.IsTrue(st.Loading);
                // In Play mode, deactivating the object stops its coroutines without running their finally, and calls
                // OnDisable. Edit mode doesn't call OnDisable on a plain MonoBehaviour, so the test calls it itself.
                typeof(SceneStreamer).GetMethod("OnDisable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(st, null);
                Assert.IsFalse(st.Loading);
                Assert.IsTrue(st.LoadFailed);
                Assert.AreEqual("kitchen", st.FailedSite);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
