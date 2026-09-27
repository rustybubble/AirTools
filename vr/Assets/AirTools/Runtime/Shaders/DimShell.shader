// edit6dof: the Edit view's dimmed world (Parts/EditDim). A translucent dark veil on an inverted sphere round the eye,
// drawn after the world and its annotations (queue 3050, before the edited item at 3060 and the UI glass at 3100+),
// ZTest Always. _ZWrite 1 (the item at the centre): the veil also writes its depth (3 m, behind the item), so the item,
// its arrows and panel draw over any wall nearer than them; 0 (Move / placing): the item is depth-tested against the
// room as usual. One translucent full-view pass: no post-processing, no grab pass. Stereo-instancing aware (SPI /
// multiview).
Shader "AirTools/DimShell"
{
    Properties
    {
        _Color ("Color", Color) = (0.04, 0.045, 0.06, 0.55)
        [Toggle] _ZWrite ("Write depth (the item at the centre)", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+50" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Dim"
            ZTest Always
            ZWrite [_ZWrite]
            Cull Front
            // Separate alpha so the veil also darkens what the compositor would show under the app.
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _ZWrite;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 frag (Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return _Color;
            }
            ENDHLSL
        }
    }
}
