using System;
using System.Collections.Generic;
using AirTools.Core;
using AirTools.Tools;
using UnityEngine;

namespace AirTools.Parts
{
    /// A part in the world at true size. The root's origin is the centre of the mounting face (part space, see
    /// PartMath); the loaded model is a child, uniformly scaled so its bounds match dims_mm and shifted so its
    /// mounting face centre sits on the origin. A BoxCollider of the published size (layer Parts) is used for
    /// grabbing and fit checks.
    public partial class PartInstance : MonoBehaviour   // assetgen: partial (PartInstance.Look.cs: size & finish)
    {
        public PartSpec Spec { get; private set; }
        public Transform Model { get; private set; }
        public BoxCollider Box { get; private set; }
        public PartOutline Outline { get; private set; }
        public FitReport Fit { get; private set; }
        /// "server" (glTF from the parts server) or "catalog" (shipped fallback).
        public string Source { get; private set; }
        public float ModelScale { get; private set; } = 1f;
        public float ScaleResidualPct { get; private set; }
        public bool Held { get; internal set; }
        public bool Placed { get; internal set; }
        public string FinishName { get; private set; }
        public Color? FinishColor { get; private set; }
        /// set_finish's `label` ("Not a listed finish"), shown under the spec card; null when there is none.
        public string FinishLabel { get; set; }
        /// The re-textured model the server rendered for the finish (set_finish model_url); null = the listing's model.
        public string FinishModelUrl { get; private set; }
        /// Outward normal of the surface it was placed on (world), and that surface's collider.
        public Vector3 SurfaceNormal { get; internal set; }
        public Collider Surface { get; internal set; }
        /// The preset / spoken search that found this part ("cabinet hinge"): names it for people (Copy.Noun).
        public string SearchQuery { get; set; }

        readonly List<Material> m_Materials = new List<Material>();
        SharedOwner m_Owner;

        /// The glTF import owning a model's meshes/textures, shared by an original and its array clones; disposed
        /// when the last of them is destroyed.
        sealed class SharedOwner
        {
            IDisposable m_Inner;
            int m_Refs = 1;
            public SharedOwner(IDisposable inner) { m_Inner = inner; }
            public SharedOwner Retain() { m_Refs++; return this; }
            public void Release() { if (--m_Refs <= 0) { m_Inner?.Dispose(); m_Inner = null; } }
        }
        static readonly int[] s_ColorProps =
        {
            Shader.PropertyToID("baseColorFactor"), Shader.PropertyToID("_BaseColor"), Shader.PropertyToID("_Color"),
        };

        public string DisplayName => Spec != null ? Spec.name : name;
        public Bounds LocalBox => PartMath.LocalBox(Spec);
        public Vector3 WorldBoxCentre => transform.TransformPoint(LocalBox.center);
        public Vector3 WorldMountDirection => transform.TransformDirection(PartMath.FaceDirection(Spec.mount.face));

        /// Build a part around an already-instantiated model (it is re-parented and fitted to dims_mm).
        /// owner (e.g. the GltfImport) is disposed with the part.
        public static PartInstance Create(PartSpec spec, GameObject model, Transform parent, MeasureStyle style, string source, IDisposable owner = null)
        {
            var go = new GameObject($"Part_{spec.id}");
            go.transform.SetParent(parent, false);
            go.layer = PartLayers.Parts;
            var p = go.AddComponent<PartInstance>();
            p.Spec = spec;
            p.InitLook();   // assetgen: the listed size is what its model is made at
            p.Source = source;
            p.m_Owner = owner != null ? new SharedOwner(owner) : null;

            var modelRoot = new GameObject("Model").transform;
            modelRoot.SetParent(go.transform, false);
            if (model != null)
            {
                model.transform.SetParent(modelRoot, false);
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                foreach (var col in model.GetComponentsInChildren<Collider>(true)) DestroyObj(col);
                foreach (var t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = PartLayers.Parts;
            }
            p.Model = modelRoot;
            p.FitModelToDims();
            p.OwnMaterials();

            var box = PartMath.LocalBox(spec);
            p.Box = go.AddComponent<BoxCollider>();
            p.Box.center = box.center;
            p.Box.size = box.size;
            p.Outline = PartOutline.Create(go.transform, box, style);
            p.Outline.Show(FitStatus.None, p.DimsCallout());
            if (!string.IsNullOrEmpty(spec.finish)) p.FinishName = spec.finish;
            return p;
        }

        /// Another instance of the same part (same model, finish and source), e.g. for an array.
        public PartInstance Clone(Transform parent, MeasureStyle style)
        {
            var model = Model.childCount > 0 ? Instantiate(Model.GetChild(0).gameObject) : null;
            if (model != null) { model.transform.localScale = Model.GetChild(0).localScale; }
            var c = Create(Spec, model, parent, style, Source);
            if (m_Owner != null) c.m_Owner = m_Owner.Retain();
            if (FinishColor.HasValue) c.SetColor(FinishColor.Value);
            c.FinishName = FinishName;
            c.FinishLabel = FinishLabel;
            c.FinishModelUrl = FinishModelUrl;
            c.SearchQuery = SearchQuery;
            return c;
        }

        /// set_finish with model_url (backend docs/api.md §5): the server's re-textured model of this same part (same
        /// size and origin: metres, +Y up, origin at the mount-face centre, +Z out) goes on every part in `parts` — the
        /// first gets `model`, the rest a copy — in place: transforms, colliders, outlines and fits stay. `owner` (the
        /// glTF import) is shared by all of them and disposed with the last one.
        public static void SwapModels(IList<PartInstance> parts, GameObject model, IDisposable owner, string finishName, string modelUrl)
        {
            if (parts == null || parts.Count == 0 || model == null) { DestroyObj(model); owner?.Dispose(); return; }
            var shared = owner != null ? new SharedOwner(owner) : null;
            bool first = true;
            foreach (var p in parts)
            {
                if (p == null) continue;
                var m = first ? model : Instantiate(model);
                if (!first) m.transform.localScale = model.transform.localScale;
                p.SwapModel(m, first ? shared : shared?.Retain());
                p.FinishName = finishName;
                p.FinishModelUrl = modelUrl;
                p.ModelKey = LookKey(p.NativeDims, finishName);   // assetgen: the voice's finish model is this look
                p.LookFinish = finishName;
                p.LookFinishSource = "imagine";
                first = false;
            }
            if (first) { DestroyObj(model); shared?.Release(); }
        }

        void SwapModel(GameObject model, SharedOwner owner)
        {
            foreach (var m in m_Materials) if (m != null) DestroyObj(m);
            m_Materials.Clear();
            for (int i = Model.childCount - 1; i >= 0; i--)
            {
                var old = Model.GetChild(i).gameObject;
                old.SetActive(false);
                old.transform.SetParent(null, false);   // Destroy is deferred in Play mode: out of the bounds and materials now
                DestroyObj(old);
            }
            m_Owner?.Release();
            m_Owner = owner;
            model.transform.SetParent(Model, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            foreach (var col in model.GetComponentsInChildren<Collider>(true)) DestroyObj(col);
            foreach (var t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = PartLayers.Parts;
            Model.localPosition = Vector3.zero;
            Model.localScale = Vector3.one;
            OnListedModelSwapped();   // assetgen: F11's variant is at the listing's size (a resized part stretches it)
            FitModelToDims();
            OwnMaterials();
            FinishColor = null;   // the variant carries the finish in its own textures
            Log.Info($"Part {Spec.id}: finish model swapped in place ({model.name})");
        }

        /// Uniform scale to the published size, then shift so the model's mounting face centre is at the origin.
        /// assetgen: the uniform scale fits the size the mesh was made at (NativeDims: the listing, or a made-to-size
        /// model's size); a part resized since is then stretched per axis onto its new size (1 when not resized).
        void FitModelToDims()
        {
            Model.localPosition = Vector3.zero;   // assetgen: refit from scratch (a resize refits the same model)
            Model.localScale = Vector3.one;
            if (!PartMath.RendererBounds(Model, out var b))
            {
                Log.Warn($"Part {Spec.id}: model has no meshes");
                return;
            }
            var published = Spec.dims_mm.Metres;
            var native = NativeDims.Metres;
            ModelScale = PartMath.FitScale(b.size, native, out float residual);
            ScaleResidualPct = residual;
            Model.localScale = Vector3.Scale(Vector3.one * ModelScale, Stretch(native, published));
            PartMath.RendererBounds(Model, Model.parent, out b);
            var mount = PartMath.FaceDirection(Spec.mount.face);
            var faceCentre = b.center + Vector3.Scale(mount, b.extents);
            Model.localPosition -= faceCentre;
            string msg = $"Part {Spec.id}: scaled ×{ModelScale:0.####} to {PartFormat.DimsMm(Spec.dims_mm)} (residual {residual:0.0}%)";
            if (residual > 5f) Log.Warn(msg + " — model proportions differ from the listing");
            else Log.Info(msg);
        }

        /// Give this instance its own copies of its materials so finishes can recolour it alone.
        void OwnMaterials()
        {
            foreach (var r in Model.GetComponentsInChildren<Renderer>(true))
            {
                var shared = r.sharedMaterials;
                for (int i = 0; i < shared.Length; i++)
                {
                    if (shared[i] == null) continue;
                    shared[i] = new Material(shared[i]) { name = shared[i].name + " (part)" };
                    m_Materials.Add(shared[i]);
                }
                r.sharedMaterials = shared;
            }
        }

        /// Measured size of the model's meshes (part space), millimetres.
        public Vector3 MeasuredSizeMm()
        {
            return PartMath.RendererBounds(Model, Model.parent, out var b) ? b.size * 1000f : Vector3.zero;
        }

        public Bounds MeasuredLocalBounds()
        {
            PartMath.RendererBounds(Model, Model.parent, out var b);
            return b;
        }

        /// Recolour to a named finish from the listing (e.g. "brown" → #5A3E2B).
        public bool SetFinish(string finishName)
        {
            var f = Spec.FindFinish(finishName);
            if (f == null) return false;
            SetColor(f.Color);
            FinishName = f.name;
            FinishLabel = null;   // a listed finish needs no "not listed" label
            Log.Info($"Part {Spec.id}: finish {f.name} {f.hex}");
            return true;
        }

        public void SetColor(Color c)
        {
            FinishColor = c;
            foreach (var m in m_Materials)
                foreach (var id in s_ColorProps)
                    if (m.HasProperty(id)) m.SetColor(id, c);
        }

        /// Colour currently on the model's first material (for tests / the harness).
        public Color? MaterialColor()
        {
            foreach (var m in m_Materials)
                foreach (var id in s_ColorProps)
                    if (m.HasProperty(id)) return m.GetColor(id);
            return null;
        }

        /// The callout's size line: the listing's W × D × H in ONE unit, the user's (D2): "5 × 1½ × 1¾ in" / "127 × 38 × 45 mm".
        public string DimsCallout() => AirTools.UI.Copy.Dims(Spec.dims_mm);

        /// place_part's verdict from the server (`fits` / `clearance_mm`, PlacePartFit): shown instead of the app's own
        /// check while the part stays where the server put it; picking it up (PartTool) clears it.
        public FitReport PinnedFit { get; set; }

        /// Show a fit that was worked out elsewhere (the server's) with the usual colours and callout.
        public void ShowFit(FitReport r)
        {
            if (r == null) { ClearFit(); return; }
            Fit = r;
            Outline.Show(r.Status, $"{DimsCallout()}\n<color=#{{c}}>{AirTools.UI.Copy.FitLine(r)}</color>");
        }

        /// Run the fit check (tape: the nearby tape reading, if any) and update the outline and callout.
        public FitReport Evaluate(TapeReading? tape, bool checkSupport = true)
        {
            if (PinnedFit != null && !Held) { ShowFit(PinnedFit); return PinnedFit; }
            Fit = FitChecker.Check(this, tape, checkSupport);
            // Display: glyph + verdict (the diagnostic Headline stays in Fit for logs and tests).
            Outline.Show(Fit.Status, $"{DimsCallout()}\n<color=#{{c}}>{AirTools.UI.Copy.FitLine(Fit)}</color>");
            return Fit;
        }

        public void ClearFit()
        {
            Fit = null;
            Outline.Show(FitStatus.None, DimsCallout());
        }

        void OnDestroy()
        {
            foreach (var m in m_Materials) if (m != null) DestroyObj(m);
            m_Materials.Clear();
            m_Owner?.Release();
            m_Owner = null;
            ReleaseVariants();   // assetgen
        }

        static void DestroyObj(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
