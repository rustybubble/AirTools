using UnityEngine;

namespace AirTools.Scene
{
    public static class SceneLayers
    {
        /// Layer that snapping, fit checks and tool raycasts hit (Project Settings ▸ Tags and Layers, index 8).
        public const string SceneSurfaceName = "SceneSurface";

        public static int SceneSurface
        {
            get
            {
                int layer = LayerMask.NameToLayer(SceneSurfaceName);
                return layer >= 0 ? layer : 8;
            }
        }

        public static int SceneSurfaceMask => 1 << SceneSurface;
    }
}
