// The captured scene's visual material (presence.md S1): URP unlit (photogrammetry has its lighting baked in; the
// synthetic facade gets a fixed per-face shade instead of lights) plus the reveal effects the grow / pour
// transitions drive through globals (RevealField):
//   AIR_REVEAL_SPHERE  only fragments inside the sphere _AirReveal (xyz centre, w radius) draw; an accent band of
//                      _AirRevealBand metres glows at its surface.
//   AIR_REVEAL_SEEN    (S2) fragments draw once _AirRevealProgress passes their "seen" time (uv3.x), with a band and a
//                      0.5 m world grid in it.
// With neither keyword there is no clip, so early-Z stays intact; the keywords are on only during an effect.
// _VertexColors 1 multiplies by the mesh's vertex colour: scene-part cavities (cavity.r<rev>.glb) carry flat colours
// as glTF COLOR_0, which is linear; glTFast copies it into the mesh unconverted and the project renders in Linear, so
// it multiplies in as is (no gamma step). Meshes without colours leave it 0 (their COLOR stream may read as 0).
// Alpha is always 1, so what's drawn covers the passthrough underlay and what's clipped shows the room.
// Stereo-instancing aware (SPI / multiview).
Shader "AirTools/SceneReveal"
{
    Properties
    {
        [MainTexture] _BaseMap ("Base map", 2D) = "white" {}
        [MainColor] _BaseColor ("Base colour", Color) = (1, 1, 1, 1)
        _FaceShade ("Face shade (x sides, y top, z front)", Vector) = (0.7, 1, 0.85, 0)
        [Toggle] _FaceShading ("Shade faces by object-space normal", Float) = 0
        [Toggle] _VertexColors ("Multiply by the vertex colour (linear glTF COLOR_0)", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        _StencilRef ("Stencil ref", Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil comparison", Float) = 8
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _FaceShade;
            half _FaceShading;
            half _VertexColors;
            half _Cull;
            half _StencilRef;
            half _StencilComp;
        CBUFFER_END
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);

        // Globals (RevealField).
        float4 _AirReveal;          // xyz centre (world), w radius (m)
        float _AirRevealBand;       // band width (m)
        half4 _AirRevealColor;      // band colour
        float _AirRevealProgress;   // AIR_REVEAL_SEEN: 0..1

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            float2 uv3 : TEXCOORD3;
            half4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            float3 positionWS : TEXCOORD1;
            half shade : TEXCOORD2;
            float seen : TEXCOORD3;
            half3 tint : TEXCOORD4;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings vert (Attributes input)
        {
            Varyings o;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            o.positionCS = TransformWorldToHClip(o.positionWS);
            o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
            // Fixed per-face light for untextured boxes (axis-aligned faces: sides / top / front). A mesh without
            // normals (n = 0) stays unshaded.
            float3 n = input.normalOS;
            float nn = dot(n, n);
            half faces = nn > 1e-4 ? dot(n * n, _FaceShade.xyz) / nn : 1.0;
            o.shade = lerp(1.0h, faces, _FaceShading);
            o.seen = input.uv3.x;
            o.tint = lerp(half3(1.0h, 1.0h, 1.0h), input.color.rgb, _VertexColors);
            return o;
        }

        // Clips hidden fragments; returns the band glow (0..1) for the visible ones.
        half RevealGlow (float3 positionWS, float seen)
        {
            half glow = 0;
            #if defined(AIR_REVEAL_SPHERE)
                float inside = _AirReveal.w - distance(positionWS, _AirReveal.xyz);
                clip(inside);
                glow = saturate(1.0 - inside / max(_AirRevealBand, 1e-5));
            #elif defined(AIR_REVEAL_SEEN)
                float ahead = _AirRevealProgress - seen;
                clip(ahead);
                glow = saturate(1.0 - ahead / 0.04);
                float3 g = positionWS / 0.5;
                float3 w = abs(frac(g - 0.5) - 0.5) / max(fwidth(g), 1e-5);
                half grid = 1.0h - saturate(min(w.x, min(w.y, w.z)));
                glow = max(glow, grid * glow * 2.0h);
            #endif
            return saturate(glow);
        }
        ENDHLSL

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]
            ZWrite On
            Stencil { Ref [_StencilRef] Comp [_StencilComp] }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ AIR_REVEAL_SPHERE AIR_REVEAL_SEEN

            half4 frag (Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half glow = RevealGlow(input.positionWS, input.seen);
                half3 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb * input.shade * input.tint;
                col = lerp(col, _AirRevealColor.rgb, glow * 0.8h);
                return half4(col, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull [_Cull]
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragDepth
            #pragma multi_compile_instancing
            #pragma multi_compile _ AIR_REVEAL_SPHERE AIR_REVEAL_SEEN

            half4 fragDepth (Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                RevealGlow(input.positionWS, input.seen);
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
