using AirTools.Scene;
using UnityEngine;

namespace AirTools.Parts
{
    public static class PartLayers
    {
        /// Layer for placed/held part colliders (Project Settings ▸ Tags and Layers, index 9). Scene raycasts
        /// (tools, snapping) use SceneSurface only, so parts never snap to themselves.
        public const string PartsName = "Parts";

        public static int Parts
        {
            get
            {
                int layer = LayerMask.NameToLayer(PartsName);
                return layer >= 0 ? layer : 9;
            }
        }

        public static int PartsMask => 1 << Parts;
        public static int FitMask => SceneLayers.SceneSurfaceMask | PartsMask;
    }
}
