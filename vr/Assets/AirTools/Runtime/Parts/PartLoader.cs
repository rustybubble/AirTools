using System;
using System.Threading.Tasks;
using AirTools.Core;
using AirTools.Tools;
using GLTFast;
using UnityEngine;

namespace AirTools.Parts
{
    /// part.json + model.glb → a true-size PartInstance. glTF comes from the parts server (glTFast); if the
    /// download or import fails, the shipped catalog model is used instead (logged, and the part's Source says so).
    public class PartLoader : MonoBehaviour
    {
        public PartsClient client;
        public PartCatalog catalog;
        [Tooltip("New parts are parented here (the scene root).")]
        public Transform parent;
        public MeasureStyle style = new MeasureStyle();
        [Tooltip("URP/Lit and URP/Unlit templates for glTF materials (glTFast's shader graphs are stripped from player builds).")]
        public Material litTemplate, unlitTemplate;

        public bool Busy { get; private set; }
        public string LastError { get; private set; }
        /// "Building the AI mesh… 12 s" while waiting for the server's model; null otherwise.
        public string Status { get; private set; }

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);

        public void Load(PartSummary summary, Action<PartInstance> done) => Load(summary, null, done);

        /// settings-assets: `mode` = how the server makes the model (part.json / model.glb ?mode=); null = Settings ▸ 3D
        /// models (UserPrefs.Assets).
        public void Load(PartSummary summary, AssetMode? mode, Action<PartInstance> done)
        {
            if (summary == null) { done?.Invoke(null); return; }
            Busy = true;
            LastError = null;
            var m = mode ?? UserPrefs.Assets;
            var c = client != null ? client : Services.Get<PartsClient>();
            if (c == null) { Finish(LoadFromCatalog(summary.id), done); return; }
            c.GetSpec(summary, (spec, source) =>
            {
                if (spec == null) { LastError = $"no part.json for {summary.id}"; Finish(null, done); return; }
                if (source != "server") { Finish(Build(spec, CatalogModel(spec), "catalog"), done); return; }
                string url = AssetModes.WithMode(summary.model_url ?? $"/parts/{spec.id}/model.glb", m);   // settings-assets
                if (spec.asset.Ready) { c.GetBytes(url, bytes => ImportThenFinish(spec, bytes, summary, done)); return; }
                // The server is still building the model (AI mesh ~15–20 s): wait, then load it; if it fails or takes
                // too long the part still comes in at its exact size as a proxy box.
                c.WaitForAsset(spec, status => { Status = status; }, (fresh, ready) =>
                {
                    Status = null;
                    if (ready) c.GetBytes(url, bytes => ImportThenFinish(fresh, bytes, summary, done));
                    else
                    {
                        Log.Warn($"Part {fresh.id}: model {(fresh.asset.Failed ? "failed" : "not ready in time")}; loading an exact-size proxy");
                        Finish(Build(fresh, CatalogModel(fresh), "proxy"), done);
                    }
                }, m);
            }, m);
        }

        async void ImportThenFinish(PartSpec spec, byte[] glb, PartSummary summary, Action<PartInstance> done)
        {
            PartInstance part = null;
            try
            {
                if (glb != null) part = await ImportGlb(spec, glb, PartsClient.Resolve(summary.model_url));
            }
            catch (Exception ex)
            {
                LastError = $"glTF import failed: {ex.Message}";
            }
            if (part == null)
            {
                Log.Warn($"Part {spec.id}: {(LastError ?? "model download failed")}; using the catalog model");
                part = Build(spec, CatalogModel(spec), "catalog");
            }
            Finish(part, done);
        }

        async Task<PartInstance> ImportGlb(PartSpec spec, byte[] glb, string url)
        {
            var gltf = litTemplate != null || unlitTemplate != null
                ? new GltfImport(null, null, new AirTools.Scene.UrpMaterialGenerator(litTemplate, unlitTemplate))
                : new GltfImport();
            bool ok = await gltf.Load(glb, url != null ? new Uri(url) : null);
            var holder = new GameObject($"{spec.id}.glb");
            ok = ok && await gltf.InstantiateMainSceneAsync(holder.transform);
            if (!ok)
            {
                LastError = "glTF import failed";
                Destroy(holder);
                gltf.Dispose();
                return null;
            }
            return Build(spec, holder, "server", gltf);
        }

        /// A finish variant of a part's model (set_finish model_url, e.g. /parts/{id}/model-matte-black.glb): downloaded
        /// and imported like model.glb, not yet placed anywhere. done(model root or null, its glTF import) — the caller
        /// hands both to PartInstance.SwapModels.
        public void LoadVariant(PartSpec spec, string modelUrl, Action<GameObject, IDisposable> done)
        {
            var c = client != null ? client : Services.Get<PartsClient>();
            if (spec == null || string.IsNullOrEmpty(modelUrl) || c == null) { done?.Invoke(null, null); return; }
            c.GetBytes(modelUrl, async bytes =>
            {
                if (bytes == null) { LastError = $"finish model {modelUrl}: {c.LastError}"; done?.Invoke(null, null); return; }
                var gltf = litTemplate != null || unlitTemplate != null
                    ? new GltfImport(null, null, new AirTools.Scene.UrpMaterialGenerator(litTemplate, unlitTemplate))
                    : new GltfImport();
                GameObject holder = null;
                bool ok;
                try
                {
                    ok = await gltf.Load(bytes, new Uri(PartsClient.Resolve(modelUrl)));
                    if (ok)
                    {
                        holder = new GameObject($"{spec.id}.{System.IO.Path.GetFileNameWithoutExtension(modelUrl.Split('?')[0])}.glb");
                        ok = await gltf.InstantiateMainSceneAsync(holder.transform);
                    }
                }
                catch (Exception ex) { LastError = $"finish model import failed: {ex.Message}"; ok = false; }
                if (!ok)
                {
                    if (holder != null) Destroy(holder);
                    gltf.Dispose();
                    LastError ??= "finish model import failed";
                    done?.Invoke(null, null);
                    return;
                }
                done?.Invoke(holder, gltf);
            });
        }

        /// place_part: the part by id (part.json from the server, else the catalog) with its model from `modelUrl`
        /// (relative to the server base). With no part.json anywhere but a model URL, the GLB alone at its own size (a
        /// spec made from its bounds, standing on its bottom face) — the voice flow still shows something true-size.
        public void LoadForPlacement(string partId, string modelUrl, string name, Action<PartInstance> done)
        {
            string id = !string.IsNullOrEmpty(partId) ? partId : IdFromUrl(modelUrl);
            if (string.IsNullOrEmpty(id)) { LastError = "place_part without a part_id or model_url"; done?.Invoke(null); return; }
            Load(new PartSummary { id = id, name = name, model_url = modelUrl }, part =>
            {
                if (part != null || string.IsNullOrEmpty(modelUrl)) { done?.Invoke(part); return; }
                Log.Warn($"Part {id}: no part.json ({LastError}); loading {modelUrl} at its own size");
                LoadModelOnly(id, modelUrl, name, done);
            });
        }

        /// "/parts/midea-123/model.glb" → "midea-123"; else the file name without its extension.
        public static string IdFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var path = url.Split('?')[0].TrimEnd('/');
            var parts = path.Split('/');
            for (int i = 0; i + 1 < parts.Length; i++) if (parts[i] == "parts" && i + 1 < parts.Length - 1) return parts[i + 1];
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        void LoadModelOnly(string id, string modelUrl, string name, Action<PartInstance> done)
        {
            var c = client != null ? client : Services.Get<PartsClient>();
            if (c == null) { done?.Invoke(null); return; }
            Busy = true;
            c.GetBytes(modelUrl, async bytes =>
            {
                PartInstance part = null;
                if (bytes == null) LastError = $"{modelUrl}: {c.LastError}";
                else
                {
                    var gltf = litTemplate != null || unlitTemplate != null
                        ? new GltfImport(null, null, new AirTools.Scene.UrpMaterialGenerator(litTemplate, unlitTemplate))
                        : new GltfImport();
                    GameObject holder = null;
                    try
                    {
                        bool ok = await gltf.Load(bytes, new Uri(PartsClient.Resolve(modelUrl)));
                        if (ok)
                        {
                            holder = new GameObject($"{id}.glb");
                            ok = await gltf.InstantiateMainSceneAsync(holder.transform);
                        }
                        if (ok && PartMath.RendererBounds(holder.transform, out var b) && b.size.x > 1e-4f && b.size.y > 1e-4f && b.size.z > 1e-4f)
                        {
                            var spec = new PartSpec
                            {
                                id = id, name = string.IsNullOrEmpty(name) ? id : name,
                                dims_mm = new PartDims { w = b.size.x * 1000f, h = b.size.y * 1000f, d = b.size.z * 1000f },
                                mount = new PartMount { face = "-y" }, dims_source = "model bounds",
                            };
                            part = Build(spec, holder, "server (model only)", gltf);
                        }
                        else
                        {
                            LastError = $"{modelUrl}: glTF import failed";
                            if (holder != null) Destroy(holder);
                            gltf.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        LastError = $"{modelUrl}: {ex.Message}";
                        if (holder != null) Destroy(holder);
                        gltf.Dispose();
                    }
                }
                Finish(part, done);
            });
        }

        /// Synchronous: spec and model from the shipped catalog (offline fallback, EditMode tests, agent harness).
        public PartInstance LoadFromCatalog(string id)
        {
            var spec = catalog != null ? catalog.Spec(id) : null;
            if (spec == null) { LastError = $"{id} is not in the catalog"; return null; }
            return Build(spec, CatalogModel(spec), "catalog");
        }

        GameObject CatalogModel(PartSpec spec)
        {
            var prefab = catalog != null ? catalog.Find(spec.id)?.model : null;
            if (prefab == null)
            {
                // Last resort: a plain box of the published size is still true-size.
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "ProxyBox";
                box.transform.localScale = spec.dims_mm.Metres;
                return box;
            }
            var go = Instantiate(prefab);
            go.name = prefab.name;
            return go;
        }

        PartInstance Build(PartSpec spec, GameObject model, string source, IDisposable owner = null)
        {
            var p = PartInstance.Create(spec, model, parent, style, source, owner);
            return p;
        }

        void Finish(PartInstance part, Action<PartInstance> done)
        {
            Busy = false;
            if (part != null) Log.Info($"Part {part.Spec.id} loaded from {part.Source}: {PartFormat.DimsMm(part.Spec.dims_mm)}");
            done?.Invoke(part);
        }
    }
}
