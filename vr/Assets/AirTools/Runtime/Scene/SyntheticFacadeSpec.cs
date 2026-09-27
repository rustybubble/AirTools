using UnityEngine;

namespace AirTools.Scene
{
    /// Ground truth for the synthetic facade (SPEC §4). The builder generates geometry from these numbers and the
    /// acceptance tests measure against them. Metres, Unity scene coordinates: wall in the XY plane at z = 0, facing +Z.
    public static class SyntheticFacadeSpec
    {
        public const float WallWidth = 8.00f;
        public const float WallHeight = 6.50f;
        public const float WallThickness = 0.30f;

        public const float WindowWidth = 1.500f;
        public const float WindowHeight = 1.200f;
        public const float SillHeight = 3.500f;        // bottom of the opening = top of the sill
        public const float WindowRecess = 0.10f;       // glass sits this far behind the wall face
        public static readonly Vector3 WindowCentre = new Vector3(0f, SillHeight + WindowHeight / 2f, -WindowRecess);
        public static readonly Vector3 SillSize = new Vector3(1.60f, 0.05f, 0.15f);

        public const float FasciaLength = 4.200f;      // x = -2.10 .. +2.10
        public const float FasciaBottom = 6.00f;
        public const float FasciaTop = 6.20f;
        public const float FasciaProud = 0.025f;

        public const float GutterRun = 4.200f;
        public const float GutterWidth = 0.127f;       // 5" K-style

        public const float DoorWidth = 0.914f;
        public const float DoorHeight = 2.032f;
        public const float DoorCentreX = 2.80f;

        public const float LedgeLength = 2.0f;
        public const float LedgeHeight = 1.0f;         // top surface at the wall
        public const float LedgeTiltDeg = 2.0f;        // about X, sloping away from the wall
        public const float LedgeCentreX = -2.5f;
        public const float LedgeDepth = 0.30f;

        public static readonly Vector3 SpawnPosition = new Vector3(0f, 0f, 4.0f);
        public const float SpawnYawDeg = 180f;         // looking at the wall (-Z)

        public const int CameraCount = 12;
    }
}
