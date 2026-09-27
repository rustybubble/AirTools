using UnityEngine;

namespace AirTools.Scene
{
    /// UX W1.2: targeting tolerances that grow with distance. A hand ray jitters by an angle, not by a length, so a
    /// fixed 6 cm corner radius is generous at arm's length and too tight at the fascia 6 m up (and on the 1:50
    /// tabletop, where it shrinks to 1.2 mm). Each radius is max(metric, distance × tan θ): the metric one still holds
    /// up close, the angular one takes over beyond metric / tan θ (a 6 cm corner at 1° past 3.4 m).
    /// Structure-layer snapping on scene packages is already angular (StructureSnapper: corner 1.5°, edge 1.2°).
    /// Pure (EditMode-tested offline).
    public static class AngularRadii
    {
        /// Off = the metric radii only, exactly as before W1.2 (A/B on the headset).
        public static bool Enabled = true;

        /// Built-in facade box snapping (SnapService.CornerRadius / EdgeRadius), degrees.
        public static float CornerDegrees = 1.0f;
        public static float EdgeDegrees = 0.6f;
        /// Measure: pressing on a placed point grabs it within this angle.
        public static float GrabDegrees = 1.2f;
        /// Measure: the last point again finishes, the first point again closes, a second click on the only point is a
        /// duplicate — within this angle.
        public static float FinishDegrees = 1.0f;

        /// max(metric, distance × tan degrees); the metric radius alone when off or the distance is unknown (≤ 0).
        public static float Radius(float metric, float distance, float degrees)
        {
            if (!Enabled || distance <= 0f || degrees <= 0f) return metric;
            return Mathf.Max(metric, distance * Mathf.Tan(degrees * Mathf.Deg2Rad));
        }

        public static float Corner(float metric, float distance) => Radius(metric, distance, CornerDegrees);
        public static float Edge(float metric, float distance) => Radius(metric, distance, EdgeDegrees);
        public static float Grab(float metric, float distance) => Radius(metric, distance, GrabDegrees);
        public static float Finish(float metric, float distance) => Radius(metric, distance, FinishDegrees);
    }
}
