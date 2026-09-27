using System;
using AirTools.Core;
using UnityEngine;

namespace AirTools.Scene
{
    /// Parent of the loaded capture ("SceneRoot/Facade/..."). Shown in World mode, hidden in Passthrough.
    /// Tools measure in this transform's local space, so readings stay in scene metres at any tabletop scale.
    /// Transform layout: SceneRoot (tabletop scale, owned by TabletopController) → Content (package → scene rotation and
    /// the calibration scale from "set scale from a known dimension") → the meshes. The structure layer and the package
    /// cameras live in Content's local space (the package frame).
    public class SceneRoot : MonoBehaviour
    {
        public ScenePackage Package { get; private set; }
        public GameObject Content { get; private set; }

        /// Loaded from a scene package over HTTP (SceneStreamer) rather than a baked ScenePackage asset.
        public bool IsRuntimePackage { get; private set; }
        public string Site { get; private set; }
        public SceneManifest Manifest { get; private set; }
        /// The snapping layer tools use (null: preview revision or none published). While scene parts are out it is the
        /// published layer with the removed components' features taken out and their cavities' faces, edges and corners
        /// added (SceneParts); otherwise it is BaseStructure.
        public StructureLayer Structure { get; private set; }
        /// The structure layer as the package published it for this revision.
        public StructureLayer BaseStructure { get; private set; }
        public Transform StructureSpace => Content != null ? Content.transform : null;

        /// Scene metres per package unit (1 until corrected with a known dimension).
        public float Calibration => Content != null ? Content.transform.localScale.x : 1f;

        /// Raised after the calibration changes, with the factor (new / old). Everything stored in SceneRoot space
        /// (tool points, placed parts) must be multiplied by it to stay on the same features.
        public event Action<float> Rescaled;
        /// Raised when the content, structure or cameras change (load, revision swap, built-in fallback).
        public event Action ContentChanged;

        /// Called by SceneLoader (and tests) once the content is instantiated under this root.
        public void SetContent(ScenePackage package, GameObject content)
        {
            LeaveFor(SiteScope.Key(false, package != null ? package.siteName : null));   // sitescope
            Package = package;
            Content = content;
            IsRuntimePackage = false;
            Site = package != null ? package.siteName : null;
            Manifest = null;
            Structure = BaseStructure = null;
            SiteScope.Arrive(Calibration);   // sitescope: this site's items back (SceneStreamer may hold them for its scale)
            ContentChanged?.Invoke();
        }

        /// Called by SceneStreamer for a package loaded over HTTP (package holds the converted cameras).
        public void SetRuntimeContent(string site, SceneManifest manifest, ScenePackage package, GameObject content, StructureLayer structure)
        {
            LeaveFor(SiteScope.Key(true, site));   // sitescope
            Package = package;
            Content = content;
            IsRuntimePackage = true;
            Site = site;
            Manifest = manifest;
            Structure = BaseStructure = structure;
            SiteScope.Arrive(Calibration);   // sitescope: this site's items back (SceneStreamer may hold them for its scale)
            ContentChanged?.Invoke();
        }

        /// sitescope: a new content is coming. The owners park the site left while this root still shows it (its frame and
        /// calibration: an adjust session or a drag ends where it was), and the arrival waits for SetContent /
        /// SetRuntimeContent to finish (Arrive; SceneStreamer holds it further, until the site's scale is restored).
        void LeaveFor(string key)
        {
            if (Content != null) SiteScope.NoteCalibration(Calibration);   // the scale the site left had
            SiteScope.HoldArrival();
            SiteScope.Switch(key, 0f);   // 0: the arriving calibration isn't known yet (Arrive notes it)
        }

        /// Revision swap step 1: new structure (the collider is already in place under Content).
        public void SetStructure(SceneManifest manifest, StructureLayer structure, ScenePackage package)
        {
            Manifest = manifest;
            Structure = BaseStructure = structure;
            if (package != null) Package = package;
            ContentChanged?.Invoke();
        }

        /// Scene parts: the layer tools snap to while components are out (null = back to BaseStructure). Not a content
        /// change: overlays, pins and measurements stay.
        public void SetEditedStructure(StructureLayer edited) => Structure = edited ?? BaseStructure;

        /// Multiplies the calibration by `factor` (real / measured). Content scales about the root origin, so stored
        /// root-space points scale by the same factor (listeners of <see cref="Rescaled"/> do that).
        public void Rescale(float factor)
        {
            if (Content == null || !(factor > 0f) || float.IsInfinity(factor)) return;
            var t = Content.transform;
            t.localScale *= factor;
            t.localPosition *= factor;
            Physics.SyncTransforms();
            Log.Info($"Scene scale ×{factor:0.0000} → calibration {Calibration:0.0000} scene m per package unit");
            SiteScope.NoteCalibration(Calibration);   // sitescope: the site's scale, for its items' way back
            Rescaled?.Invoke(factor);
        }

        /// Sets the calibration outright (e.g. restored from a previous session); raises Rescaled with the ratio.
        public void SetCalibration(float calibration)
        {
            if (Content == null || !(calibration > 0f)) return;
            float factor = calibration / Calibration;
            if (Mathf.Abs(factor - 1f) > 1e-6f) Rescale(factor);
        }

        void OnEnable() => Services.Register(this);
        void OnDisable() => Services.Unregister(this);

        public void SetVisible(bool visible)
        {
            if (Content != null) Content.SetActive(visible);
        }

        public bool IsVisible => Content != null && Content.activeSelf;

        /// Package-space point → SceneRoot space (the space tools store their points in).
        public Vector3 PackageToRoot(Vector3 p) =>
            Content != null ? transform.InverseTransformPoint(Content.transform.TransformPoint(p)) : p;

        public Vector3 RootToPackage(Vector3 p) =>
            Content != null ? Content.transform.InverseTransformPoint(transform.TransformPoint(p)) : p;

        /// Package cameras expressed in this root's local space (the space tools store their points in).
        public SceneCameraInfo[] CamerasInRootSpace()
        {
            if (Package == null || Package.cameras == null) return Array.Empty<SceneCameraInfo>();
            var rot = Content != null ? Content.transform.localRotation : Quaternion.identity;
            var pos = Content != null ? Content.transform.localPosition : Vector3.zero;
            float scale = Calibration;
            var result = new SceneCameraInfo[Package.cameras.Length];
            for (int i = 0; i < result.Length; i++)
            {
                var c = Package.cameras[i];
                c.position = pos + rot * (c.position * scale);
                c.rotation = rot * c.rotation;
                result[i] = c;
            }
            return result;
        }
    }
}
