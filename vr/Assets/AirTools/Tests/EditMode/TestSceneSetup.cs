using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace AirTools.Tests
{
    /// Every EditMode test in AirTools.Tests runs inside a throwaway additive scene, so test objects never dirty the
    /// open scene (a dirty Main.unity later blocks the Editor with a "save changes?" dialog).
    [SetUpFixture]
    public class TestSceneSetup
    {
        UnityEngine.SceneManagement.Scene m_Scene;
        UnityEngine.SceneManagement.Scene m_Previous;

        [OneTimeSetUp]
        public void CreateScratchScene()
        {
            m_Previous = SceneManager.GetActiveScene();
            // The Test Runner normally already runs EditMode tests in a fresh untitled scene; only add our own
            // scratch scene when tests are running against a saved scene (e.g. invoked some other way).
            if (string.IsNullOrEmpty(m_Previous.path)) return;
            m_Scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(m_Scene);
        }

        [OneTimeTearDown]
        public void CloseScratchScene()
        {
            if (!m_Scene.IsValid()) return;
            if (m_Previous.IsValid() && m_Previous.isLoaded) SceneManager.SetActiveScene(m_Previous);
            EditorSceneManager.CloseScene(m_Scene, removeScene: true);
        }
    }
}
