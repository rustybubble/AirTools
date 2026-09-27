using AirTools.Core;
using UnityEngine;

namespace AirTools.Scene
{
    /// The reveal state the SceneReveal and SkyDome shaders read (globals), set by the TransitionDirector:
    /// - a sphere (`_AirReveal` xyz centre, w radius; `_AirRevealBand`) behind keyword AIR_REVEAL_SPHERE: only scene
    ///   fragments inside it draw, with an accent band at its surface;
    /// - a "seen" progress (`_AirRevealProgress`) behind AIR_REVEAL_SEEN (S2's drone paint-in);
    /// - the sky dome's cone (`_AirSkyReveal` xyz direction, w half-angle in radians; π = the whole dome).
    /// The keywords are on only during an effect, so the scene keeps early-Z the rest of the time.
    /// <see cref="Contains"/> mirrors the shader's test so EditMode tests can check it.
    public static class RevealField
    {
        public const string SphereKeyword = "AIR_REVEAL_SPHERE";
        public const string SeenKeyword = "AIR_REVEAL_SEEN";

        static readonly int s_Reveal = Shader.PropertyToID("_AirReveal"), s_Band = Shader.PropertyToID("_AirRevealBand"),
            s_Color = Shader.PropertyToID("_AirRevealColor"), s_Progress = Shader.PropertyToID("_AirRevealProgress"),
            s_Sky = Shader.PropertyToID("_AirSkyReveal"), s_SkyBand = Shader.PropertyToID("_AirSkyBand");

        public static bool SphereOn { get; private set; }
        public static bool SeenOn { get; private set; }
        public static Vector3 Centre { get; private set; }
        public static float Radius { get; private set; }
        public static float Band { get; private set; }
        public static float Progress { get; private set; }
        public static Vector3 SkyDirection { get; private set; } = Vector3.up;
        public static float SkyAngle { get; private set; } = Mathf.PI;

        /// Only scene fragments within `radius` of `centre` draw; an accent band `band` metres deep glows at the edge.
        public static void SetSphere(Vector3 centre, float radius, float band, Color color)
        {
            Centre = centre; Radius = radius; Band = band;
            Shader.SetGlobalVector(s_Reveal, new Vector4(centre.x, centre.y, centre.z, radius));
            Shader.SetGlobalFloat(s_Band, Mathf.Max(band, 1e-5f));
            Shader.SetGlobalColor(s_Color, color);   // SetGlobalColor converts the sRGB token to linear
            if (!SphereOn) { Shader.EnableKeyword(SphereKeyword); SphereOn = true; }
            if (SeenOn) { Shader.DisableKeyword(SeenKeyword); SeenOn = false; }
        }

        /// S2: fragments draw once `progress` passes their seen time (mesh uv3.x).
        public static void SetSeen(float progress, Color color)
        {
            Progress = progress;
            Shader.SetGlobalFloat(s_Progress, progress);
            Shader.SetGlobalColor(s_Color, color);
            if (!SeenOn) { Shader.EnableKeyword(SeenKeyword); SeenOn = true; }
            if (SphereOn) { Shader.DisableKeyword(SphereKeyword); SphereOn = false; }
        }

        /// The sky dome shows the directions within `angle` (radians) of `direction`; π shows it all.
        public static void SetSky(Vector3 direction, float angle, float bandRadians)
        {
            SkyDirection = direction.sqrMagnitude > 1e-8f ? direction.normalized : Vector3.up;
            SkyAngle = Mathf.Clamp(angle, 0f, Mathf.PI);
            Shader.SetGlobalVector(s_Sky, new Vector4(SkyDirection.x, SkyDirection.y, SkyDirection.z, SkyAngle));
            Shader.SetGlobalFloat(s_SkyBand, Mathf.Max(bandRadians, 1e-4f));
        }

        public static void SetSkyFull() => SetSky(Vector3.up, Mathf.PI, 0.06f);

        /// Scene effects off (everything draws, no clip); the sky dome whole.
        public static void Clear()
        {
            if (SphereOn) Shader.DisableKeyword(SphereKeyword);
            if (SeenOn) Shader.DisableKeyword(SeenKeyword);
            SphereOn = SeenOn = false;
            Radius = 0f;
            SetSkyFull();
        }

        /// Like Clear, but also forces the global keywords off (they persist in the Editor between Play sessions).
        public static void Reset()
        {
            Shader.DisableKeyword(SphereKeyword);
            Shader.DisableKeyword(SeenKeyword);
            SphereOn = SeenOn = false;
            Radius = 0f;
            SetSkyFull();
        }

        /// Would the SceneReveal shader draw a scene fragment at world point p right now?
        public static bool Contains(Vector3 p) => !SphereOn || TransitionMath.InsideSphere(p, Centre, Radius);

        /// The band glow the shader gives a visible fragment at p (0 when no sphere is active).
        public static float Glow(Vector3 p) => SphereOn ? TransitionMath.BandGlow(p, Centre, Radius, Band) : 0f;

        /// Would the sky dome draw in world direction `dir` right now?
        public static bool SkyContains(Vector3 dir) => TransitionMath.InsideCone(dir, SkyDirection, SkyAngle);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetOnLoad() => Reset();
    }
}
