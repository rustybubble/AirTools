using System.Collections;
using AirTools.Core;
using AirTools.Scene;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AirTools.Tests
{
    /// Real frames and real time: the chest command reaches World visuals within the 1.5 s acceptance budget.
    public class ModeTransitionPlayTests
    {
        [UnityTest]
        public IEnumerator OpenChestShowsWorldWithinBudget()
        {
            AppState.Reset();
            var root = new GameObject("mode-play-test");
            try
            {
                var sceneRoot = new GameObject("SceneRoot").AddComponent<SceneRoot>();
                sceneRoot.transform.SetParent(root.transform);
                var content = new GameObject("Content");
                content.transform.SetParent(sceneRoot.transform);
                sceneRoot.SetContent(null, content);
                var mode = root.AddComponent<ModeController>();
                mode.sceneRoot = sceneRoot;
                mode.ApplyVisuals(AppState.Mode);
                yield return null;
                Assert.IsFalse(sceneRoot.IsVisible);

                float start = Time.realtimeSinceStartup;
                AppCommands.OpenChest();
                while ((mode.IsTransitioning || mode.VisualMode != AppMode.World) && Time.realtimeSinceStartup - start < 3f)
                    yield return null;
                float elapsed = Time.realtimeSinceStartup - start;

                Assert.AreEqual(AppMode.World, mode.VisualMode);
                Assert.IsTrue(sceneRoot.IsVisible);
                Assert.Less(elapsed, 1.5f);
            }
            finally
            {
                Object.Destroy(root);
                AppState.Reset();
            }
        }
    }
}
