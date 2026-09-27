using System;
using System.Globalization;
using AirTools.Core;
using AirTools.Notes;
using AirTools.Scene;
using UnityEngine;

namespace AirTools.Structure
{
    /// "Set scale from a known dimension" (backend brief §2, p2-structure-bench §5.2.8): tape a feature whose real size
    /// you know (a cabinet door, a standard door, a tile), type its real length, and the whole scene is multiplied by
    /// real / measured. The structure layer, the cameras and every stored reading follow (they live in the scene
    /// content's space, SceneRoot.Rescaled moves the tools' points). Snapped structure corners make one tape enough.
    public static class ScaleCalibration
    {
        /// Refuse corrections outside this range (a typo, or the wrong unit).
        public const float MinFactor = 0.2f, MaxFactor = 5f;

        public static string LastResult { get; private set; } = "";
        /// The real size typed for the last successful "Set scale" (m); 0 = none this session.
        public static double LastRealMetres { get; private set; }

        /// The latest two-point measurement in the notebook (the tape the correction is based on), or null.
        public static NotebookEntry LatestTape()
        {
            var entries = Notebook.Entries;
            for (int i = entries.Count - 1; i >= 0; i--)
                if (entries[i].Tool == "measure" && entries[i].Unit == "m" && entries[i].Points != null && entries[i].Points.Length == 2 && entries[i].ValueSI > 1e-4
                    && entries[i].OnCurrentSite)   // sitescope: another scan's tape can't set this one's scale
                    return entries[i];
            return null;
        }

        public static bool TryFactor(double measuredM, double realM, out float factor, out string error)
        {
            factor = 1f; error = null;
            if (!(measuredM > 1e-4)) { error = "no tape reading to correct"; return false; }
            if (!(realM > 1e-4)) { error = "enter the real length"; return false; }
            double f = realM / measuredM;
            if (f < MinFactor || f > MaxFactor) { error = $"×{f:0.###} is out of range (check the unit)"; return false; }
            factor = (float)f;
            return true;
        }

        /// Apply real / measured for the given tape entry (default: the latest). Logs a notebook note.
        public static bool Apply(double realMetres, NotebookEntry tape = null)
        {
            tape ??= LatestTape();
            var root = Services.Get<SceneRoot>();
            if (root == null || root.Content == null) { LastResult = "no scene loaded"; return false; }
            if (tape == null) { LastResult = "measure a known dimension first (two points)"; return false; }
            double measured = tape.ValueSI;
            if (!TryFactor(measured, realMetres, out float factor, out var error)) { LastResult = error; Log.Warn($"Scale: {error}"); return false; }
            float before = root.Calibration;
            root.Rescale(factor);
            Services.Get<SceneStreamer>()?.SaveCalibration();   // scalemodels: a person's scale (CalibrationSync saves it too)
            LastRealMetres = realMetres;
            var c = CultureInfo.InvariantCulture;
            LastResult = $"Scale ×{factor.ToString("0.0000", c)}: {Units.Format(measured)} measured is {Units.Format(realMetres)} real " +
                         $"(calibration {before.ToString("0.####", c)} → {root.Calibration.ToString("0.####", c)})";
            Notebook.Add(new NotebookEntry("scale", factor, "×", tape.Points ?? Array.Empty<Vector3>(), DateTime.Now, tape.NearestCameraId, LastResult));
            Log.Check("scale.known_dimension", Math.Abs(tape.ValueSI - realMetres) < 0.001, $"tape now {tape.ValueSI:0.0000} m, real {realMetres:0.0000} m");
            return true;
        }

        /// Back to the site's default scale (scalemodels: SiteScales, e.g. the kitchen's ×1.63), else the package's own.
        /// The person's saved scale for the site is forgotten.
        public static bool Reset()
        {
            var root = Services.Get<SceneRoot>();
            if (root == null || root.Content == null) return false;
            var streamer = Services.Get<SceneStreamer>();
            if (streamer == null || !streamer.ResetCalibration()) root.SetCalibration(1f);
            LastRealMetres = 0;
            bool atDefault = streamer != null && streamer.ScaleSource == ScaleSource.SiteDefault;
            LastResult = atDefault ? $"Scale back to this scan's ×{SiteScales.Factor(root.Calibration)}" : "Scale reset to the capture's own";
            Notebook.Add(new NotebookEntry("scale", root.Calibration, "×", Array.Empty<Vector3>(), DateTime.Now, -1, LastResult));
            return true;
        }
    }
}
