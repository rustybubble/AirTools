using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AirTools.Parts;
using UnityEditor;
using UnityEngine;

namespace AirTools.Editor
{
    /// AirTools ▸ Build Part Catalog: collects Assets/AirTools/Fixtures/Parts/&lt;id&gt;/{part.json, model.glb, image.jpg}
    /// into PartCatalog.asset (the offline fallback shipped in the app). Also makes sure the Parts layer exists.
    public static class PartCatalogBuilder
    {
        public const string PartsFolder = "Assets/AirTools/Fixtures/Parts";
        public const string CatalogPath = PartsFolder + "/PartCatalog.asset";

        // Same keywords as tools/mock_parts_server.py, so offline search behaves like the server.
        static readonly Dictionary<string, string[]> s_Keywords = new Dictionary<string, string[]>
        {
            ["hidden-hanger-5k"] = new[] { "hanger", "gutter", "bracket", "k-style", "fascia" },
            ["window-ac-small"] = new[] { "ac", "air", "conditioner", "window", "cooling" },
            // assetgen: made by the backend's window template (docs/handoff/p4-asset), for the facade's 59 × 47¼″ opening
            ["window-frame-58x46"] = new[] { "window", "frame", "replacement", "sash", "slider", "vinyl" },
        };

        [MenuItem("AirTools/Build Part Catalog")]
        public static void BuildMenu() => Build();

        public static PartCatalog Build()
        {
            EnsurePartsLayer();
            var catalog = AssetDatabase.LoadAssetAtPath<PartCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<PartCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }
            catalog.entries.Clear();
            foreach (var dir in Directory.GetDirectories(PartsFolder).OrderBy(d => d))
            {
                string id = Path.GetFileName(dir);
                string folder = $"{PartsFolder}/{id}";
                var json = AssetDatabase.LoadAssetAtPath<TextAsset>($"{folder}/part.json");
                if (json == null) continue;
                var spec = PartSpec.Parse(json.text);
                var words = new HashSet<string>(s_Keywords.TryGetValue(id, out var k) ? k : new string[0]);
                foreach (Match m in Regex.Matches($"{spec.name} {spec.id}".ToLowerInvariant(), "[a-z][a-z0-9-]{2,}")) words.Add(m.Value);
                catalog.entries.Add(new PartCatalog.Entry
                {
                    id = id,
                    partJson = json,
                    model = AssetDatabase.LoadAssetAtPath<GameObject>($"{folder}/model.glb"),
                    image = AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/image.jpg"),
                    keywords = words.OrderBy(w => w).ToArray(),
                });
            }
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AirTools] Part catalog: {string.Join(", ", catalog.entries.Select(e => $"{e.id}(model={(e.model != null)}, image={(e.image != null)})"))}");
            return catalog;
        }

        /// Layer 9 = "Parts" (placed/held part colliders).
        public static void EnsurePartsLayer()
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layer = tagManager.FindProperty("layers").GetArrayElementAtIndex(9);
            if (layer.stringValue == PartLayers.PartsName) return;
            if (!string.IsNullOrEmpty(layer.stringValue))
            {
                Debug.LogWarning($"[AirTools] Layer 9 is '{layer.stringValue}', expected '{PartLayers.PartsName}'");
                return;
            }
            layer.stringValue = PartLayers.PartsName;
            tagManager.ApplyModifiedProperties();
            Debug.Log("[AirTools] Added layer 9 'Parts'");
        }
    }
}
