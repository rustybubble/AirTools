using System;
using System.Collections.Generic;
using AirTools.Core;
using UnityEngine;

namespace AirTools.Parts
{
    /// assetgen: a part's size and finish as the Size & finish panel changes them (PlacementEditor.Look).
    /// - **Size.** SetDims gives the part a new true size (its spec's dims_mm; the collider, outline and callout follow).
    ///   The model it has is stretched per axis from the size it was made at (NativeDims) onto the new one: "resized".
    ///   A model the server made at that size (POST /parts/{id}/resize) is swapped in with AdoptModel: "made to size".
    /// - **Variants.** Models it had are parked (inactive, up to MaxVariants) by their size and finish, so undo / redo
    ///   and switching back are instant (TryShowVariant); the listing's own model is always one of them.
    /// - **Tint.** A finish the server doesn't render tints every material but the glass (TintExceptGlass).
    public partial class PartInstance
    {
        /// The size the listing gives (what the part is as sold).
        public PartDims ListedDims { get; private set; }
        /// The size its current model was made at: the listing's, or a made-to-size model's.
        public PartDims NativeDims => m_NativeDims ?? Spec?.dims_mm;
        /// The key of the model on it now (LookKey of its size and finish).
        public string ModelKey { get; private set; }
        /// The finish chosen in the panel (null: as listed) and where it came from ("imagine", "tint", null).
        public string LookFinish { get; set; }
        public string LookFinishSource { get; set; }

        PartDims m_NativeDims;

        /// Models parked for undo and switching back.
        public const int MaxVariants = 4;

        sealed class Variant
        {
            public string Key;
            public GameObject Root;
            public SharedOwner Owner;
            public List<Material> Materials;
            public PartDims Native;
            public string Url;
        }

        readonly List<Variant> m_Variants = new List<Variant>();
        Transform m_Parked;

        void InitLook()
        {
            ListedDims = Copy(Spec.dims_mm);
            m_NativeDims = Copy(Spec.dims_mm);
            ModelKey = LookKey(ListedDims, null);
        }

        /// F11's set_finish model is the listing's size.
        void OnListedModelSwapped()
        {
            m_Untinted.Clear();   // those materials went with the old model
            m_NativeDims = Copy(ListedDims ?? Spec.dims_mm);
            ModelKey = LookKey(m_NativeDims, FinishName);
        }

        static PartDims Copy(PartDims d) => d == null ? null : new PartDims { w = d.w, h = d.h, d = d.d };

        static Vector3 Stretch(Vector3 native, Vector3 published) => new Vector3(
            native.x > 1e-6f ? published.x / native.x : 1f, native.y > 1e-6f ? published.y / native.y : 1f, native.z > 1e-6f ? published.z / native.z : 1f);

        /// "1487x1187x83|bronze" (PartLookMath.LookKey).
        public static string LookKey(PartDims d, string finish) => PartLookMath.LookKey(d, finish);

        /// Within half a millimetre on every axis (PartLookMath.SameDims).
        public static bool SameDims(PartDims a, PartDims b) => PartLookMath.SameDims(a, b);

        /// "as listed", "made to size" (a model built at this size) or "resized" (the model stretched onto it).
        public string SizeState => SameDims(Spec.dims_mm, ListedDims) ? "as listed" : SameDims(Spec.dims_mm, NativeDims) ? "made to size" : "resized";

        /// A new true size: the spec (a copy: the candidate's own stays as listed), the collider, the outline and the
        /// model (stretched from its NativeDims). The caller keeps the anchor where it was and re-runs the fit.
        public void SetDims(PartDims dims)
        {
            if (dims == null || dims.w <= 0f || dims.h <= 0f || dims.d <= 0f) return;
            Spec = Spec.WithDims(dims);
            var box = LocalBox;
            if (Box != null) { Box.center = box.center; Box.size = box.size; }
            Outline?.SetBox(box);
            FitModelToDims();
        }

        /// A model made at `native` size (the server's made-to-size / finish GLB) goes on in place; the one on it now is
        /// parked under its key. `owner` (its glTF import) is disposed with the last user.
        public void AdoptModel(GameObject model, IDisposable owner, PartDims native, string key, string url)
        {
            if (model == null) { owner?.Dispose(); return; }
            Park();
            m_Owner = owner != null ? new SharedOwner(owner) : null;
            model.transform.SetParent(Model, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            foreach (var col in model.GetComponentsInChildren<Collider>(true)) DestroyObj(col);
            foreach (var t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = PartLayers.Parts;
            m_NativeDims = Copy(native);
            ModelKey = key;
            FinishModelUrl = url;
            FitModelToDims();
            OwnMaterials();
            FinishColor = null;
            Log.Info($"Part {Spec.id}: model {key} ({SizeState}) from {url ?? "?"}");
        }

        /// Put back a parked model (undo, switching back). False when none has that key.
        public bool TryShowVariant(string key)
        {
            if (key == ModelKey) return true;
            int i = m_Variants.FindIndex(v => v.Key == key);
            if (i < 0) return false;
            var v = m_Variants[i];
            m_Variants.RemoveAt(i);
            Park();
            v.Root.transform.SetParent(Model, false);
            v.Root.SetActive(true);
            m_Owner = v.Owner;
            m_Materials.Clear();
            m_Materials.AddRange(v.Materials);
            m_NativeDims = v.Native;
            ModelKey = v.Key;
            FinishModelUrl = v.Url;
            FinishColor = null;
            FitModelToDims();
            Log.Info($"Part {Spec.id}: model {key} back ({SizeState})");
            return true;
        }

        public bool HasVariant(string key) => key == ModelKey || m_Variants.Exists(v => v.Key == key);

        /// The model on it now into the parked list (its materials and owner kept), oldest evicted past MaxVariants.
        void Park()
        {
            ClearTint();   // a parked model goes back in its own colours
            if (Model.childCount == 0) return;
            if (m_Parked == null)
            {
                m_Parked = new GameObject("Variants").transform;
                m_Parked.SetParent(transform, false);
                m_Parked.gameObject.SetActive(false);
            }
            var root = Model.GetChild(0).gameObject;
            for (int i = Model.childCount - 1; i >= 1; i--) DestroyObj(Model.GetChild(i).gameObject);
            root.transform.SetParent(m_Parked, false);
            m_Variants.RemoveAll(v => v.Key == ModelKey);
            m_Variants.Add(new Variant { Key = ModelKey, Root = root, Owner = m_Owner, Materials = new List<Material>(m_Materials), Native = Copy(NativeDims), Url = FinishModelUrl });
            m_Owner = null;
            m_Materials.Clear();
            while (m_Variants.Count > MaxVariants)
            {
                // The listing's own model stays (the way back to "as listed").
                int drop = m_Variants.FindIndex(v => v.Key != LookKey(ListedDims, null));
                if (drop < 0) drop = 0;
                Drop(m_Variants[drop]);
                m_Variants.RemoveAt(drop);
            }
        }

        void Drop(Variant v)
        {
            foreach (var m in v.Materials) if (m != null) DestroyObj(m);
            if (v.Root != null) { v.Root.SetActive(false); v.Root.transform.SetParent(null, false); DestroyObj(v.Root); }
            v.Owner?.Release();
        }

        void ReleaseVariants()
        {
            foreach (var v in m_Variants) Drop(v);
            m_Variants.Clear();
        }

        /// Models parked (tests / the harness).
        public int VariantCount => m_Variants.Count;

        /// A finish the server doesn't render: every material but the glass takes the colour (a bronze frame keeps
        /// clear glass). The node names are the template's roles ("frame", "sash", "glass"). ClearTint undoes it.
        public void TintExceptGlass(Color c, string finishName)
        {
            FinishColor = c;
            FinishName = finishName;
            foreach (var r in Model.GetComponentsInChildren<Renderer>(true))
            {
                if (r.gameObject.name.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !m_Materials.Contains(m)) continue;
                    foreach (var id in s_ColorProps)
                    {
                        if (!m.HasProperty(id)) continue;
                        if (!m_Untinted.ContainsKey((m, id))) m_Untinted[(m, id)] = m.GetColor(id);
                        m.SetColor(id, c);
                    }
                }
            }
        }

        /// The model's own colours back (after TintExceptGlass).
        public void ClearTint()
        {
            foreach (var kv in m_Untinted)
                if (kv.Key.Item1 != null) kv.Key.Item1.SetColor(kv.Key.Item2, kv.Value);
            m_Untinted.Clear();
            FinishColor = null;
        }

        readonly Dictionary<(Material, int), Color> m_Untinted = new Dictionary<(Material, int), Color>();
    }
}
