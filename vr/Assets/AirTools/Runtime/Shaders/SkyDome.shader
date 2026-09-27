// The World sky (presence.md S1): an inverted dome around the head with a vertical gradient matching the procedural
// skybox it replaces (colours sampled from Default-Skybox by MainSceneBuilder). During the grow / pour transitions
// only a cone of it draws (global _AirSkyReveal: xyz direction, w half-angle in radians; π = all of it), with an accent
// band at the cone's edge, so the sky closes over the passthrough room (or opens to it) instead of cutting to black.
// Alpha is always 1 where drawn (covers the passthrough underlay). Stereo-instancing aware (SPI / multiview).
Shader "AirTools/SkyDome"
{
    Properties
    {
        _Zenith ("Zenith", Color) = (0.314, 0.396, 0.533, 1)
        _Horizon ("Horizon", Color) = (0.925, 0.980, 0.984, 1)
        _GroundHorizon ("Ground at the horizon", Color) = (0.671, 0.725, 0.733, 1)
        _Ground ("Ground", Color) = (0.412, 0.388, 0.373, 1)
        _Exponent ("Sky gradient exponent", Range(0.1, 2)) = 0.4
    }
    SubShader
    {
        Tags { "RenderType" = "Background" "Queue" = "Background" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" "PreviewType" = "Skybox" }
        Pass
        {
            Name "Sky"
            Tags { "LightMode" = "UniversalForward" }
            Cull Front
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Zenith;
                half4 _Horizon;
                half4 _GroundHorizon;
                half4 _Ground;
                half _Exponent;
            CBUFFER_END

            // Globals (RevealField).
            float4 _AirSkyReveal;   // xyz direction, w half-angle (rad)
            float _AirSkyBand;      // band width (rad)
            half4 _AirRevealColor;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 dir : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.dir = TransformObjectToWorldDir(input.positionOS.xyz, false);
                return o;
            }

            half4 frag (Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 d = normalize(input.dir);
                half glow = 0;
                // Unset globals (zero vector) and w ≥ π both mean the whole sky.
                if (dot(_AirSkyReveal.xyz, _AirSkyReveal.xyz) > 0.25 && _AirSkyReveal.w < 3.1415)
                {
                    float ang = acos(clamp(dot(d, normalize(_AirSkyReveal.xyz)), -1.0, 1.0));
                    float inside = _AirSkyReveal.w - ang;
                    clip(inside);
                    glow = saturate(1.0 - inside / max(_AirSkyBand, 1e-4));
                }
                half el = d.y;
                half3 sky = lerp(_Horizon.rgb, _Zenith.rgb, pow(saturate(el), _Exponent));
                half3 ground = lerp(_GroundHorizon.rgb, _Ground.rgb, smoothstep(0.0h, 0.12h, -el));
                half3 col = lerp(ground, sky, smoothstep(-0.035h, 0.0h, el));
                col = lerp(col, _AirRevealColor.rgb, glow * 0.8h);
                return half4(col, 1.0h);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
