using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AirTools.Tests
{
    public class ScriptFileTests
    {
        /// Unity can only serialize a MonoBehaviour/ScriptableObject into a scene or asset when its script file is
        /// named after the class. A mismatch works when added from code but turns into "missing script" on reload.
        [Test]
        public void EveryComponentTypeHasAMatchingScriptFile()
        {
            var runtime = typeof(AirTools.Core.AppState).Assembly;
            var types = runtime.GetTypes()
                .Where(t => !t.IsAbstract && (typeof(MonoBehaviour).IsAssignableFrom(t) || typeof(ScriptableObject).IsAssignableFrom(t)))
                .ToList();
            var scripts = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets/AirTools/Runtime" })
                .Select(g => AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(s => s != null)
                .Select(s => s.GetClass())
                .Where(c => c != null)
                .ToHashSet();
            var missing = types.Where(t => !scripts.Contains(t)).Select(t => t.FullName).ToList();
            Assert.IsEmpty(missing, "classes whose file name does not match: " + string.Join(", ", missing));
        }
    }
}
