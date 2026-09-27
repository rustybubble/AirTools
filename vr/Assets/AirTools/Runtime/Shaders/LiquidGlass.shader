// AirTools Liquid Glass (after Apple's 2025 material): glass that bends light at its rim instead of frosting it.
// Quest-cheap: no grab pass or blur (passthrough isn't readable and an opaque-texture copy costs ~1.5 ms). The shape
// is an analytic signed distance field in surface metres (UV0), drawn on a tight mesh:
//   _Shape 0: the tool ring — a horseshoe arc (open at the bottom) smoothly merged with the selection lens at the top;
//   _Shape 1: a rounded rectangle / capsule (_RectSize, _CornerRadius), e.g. Undo / Redo.
// Look: a squircle bezel gives a surface normal that tilts outward in the rim (the lens); a world-fixed key light
// (_KeyLight, above and toward the viewer, set per frame) puts a specular highlight on the rim that slides as the
// hand moves, with a dimmer fill on the opposite edge; Fresnel and a studio-gradient reflection brighten grazing
// angles; a thin inner stroke outlines the edge; the body is a dark neutral veil (the visionOS dimming layer, so white
// glyphs stay legible over a bright room); a soft outer halo stands in for a shadow in mid-air. The lens is clearer and
// brighter; _Touch adds a glow under the fingertip and _Pulse flashes the lens on each detent. UX D3: _LensInk (a = amount)
// inks the lens while it holds the tool in hand — a tape-yellow keyline over a faint wash. Stereo-instanced; one
// transparent pass; separate alpha so it also occludes the passthrough underlay.
Shader "AirTools/LiquidGlass"
{
    Properties
    {
        _Shape ("Shape (0 ring, 1 rounded rect)", Float) = 0
        _ArcRadius ("Arc centreline radius (m)", Float) = 0.105
        _ArcHalfWidth ("Arc half width (m)", Float) = 0.017
        _ArcHalfAngle ("Arc half aperture from the top (rad)", Float) = 2.0
        _LensCenter ("Lens centre (m, xy)", Vector) = (0, 0.105, 0, 0)
        _LensRadius ("Lens radius (m)", Float) = 0.024
        _LensBlend ("Lens smooth-union (m)", Float) = 0.008
        _RectSize ("Rect size (m)", Vector) = (0.04, 0.03, 0, 0)
        _CornerRadius ("Rect corner radius (m)", Float) = 0.015
        _Bezel ("Bezel width (m)", Float) = 0.0065
        _Tint ("Body tint", Color) = (0.07, 0.08, 0.10, 0.34)
        _LensTint ("Lens tint", Color) = (0.30, 0.33, 0.38, 0.16)
        _LensInk ("Lens ink (UX D3: the active tool; a = amount)", Color) = (1, 0.8235, 0.2471, 0)
        _EnvTop ("Reflection top", Color) = (0.92, 0.96, 1.0, 1)
        _EnvBottom ("Reflection bottom", Color) = (0.20, 0.22, 0.26, 1)
        _KeyLight ("Key light (world dir)", Vector) = (0, 1, 0, 0)
        _Spec ("Specular", Range(0, 1)) = 0.42
        _SpecPower ("Specular power", Range(4, 64)) = 20
        _Fresnel ("Fresnel", Range(0, 1)) = 0.2
        _Stroke ("Inner stroke", Range(0, 1)) = 0.28
        _Halo ("Halo width (m)", Float) = 0.0035
        _HaloAlpha ("Halo alpha", Range(0, 1)) = 0.2
        _Touch ("Touch (xy m, z distance m, w strength)", Vector) = (0, 0, 1, 0)
        _Pulse ("Lens pulse", Range(0, 1)) = 0
        _Opacity ("Opacity", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-6" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "LiquidGlass"
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Shape, _ArcRadius, _ArcHalfWidth, _ArcHalfAngle, _LensRadius, _LensBlend, _CornerRadius, _Bezel;
                float4 _LensCenter, _RectSize;
                half4 _Tint, _LensTint, _EnvTop, _EnvBottom, _LensInk;
                float4 _KeyLight;
                half _Spec, _SpecPower, _Fresnel, _Stroke, _HaloAlpha, _Pulse, _Opacity;
                float _Halo;
                float4 _Touch;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0 : TEXCOORD0;   // surface-local metres
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 p : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.p = input.uv0;
                return o;
            }

            // iq: arc symmetric about +y with half aperture a (sc = sin a, cos a), centreline radius ra, half width rb.
            float sdArc(float2 p, float2 sc, float ra, float rb)
            {
                p.x = abs(p.x);
                return ((sc.y * p.x > sc.x * p.y) ? length(p - sc * ra) : abs(length(p) - ra)) - rb;
            }

            float sdRoundRect(float2 p, float2 halfSize, float r)
            {
                float2 q = abs(p) - halfSize + r;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            float smin(float a, float b, float k)
            {
                float h = max(k - abs(a - b), 0.0) / max(k, 1e-6);
                return min(a, b) - h * h * k * 0.25;
            }

            float Shape(float2 p)
            {
                if (_Shape > 0.5) return sdRoundRect(p, _RectSize.xy * 0.5, min(_CornerRadius, min(_RectSize.x, _RectSize.y) * 0.5));
                float2 sc = float2(sin(_ArcHalfAngle), cos(_ArcHalfAngle));
                float arc = sdArc(p, sc, _ArcRadius, _ArcHalfWidth);
                float lens = length(p - _LensCenter.xy) - _LensRadius;
                return smin(arc, lens, _LensBlend);
            }

            half4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 p = i.p;
                float d = Shape(p);
                float aa = max(fwidth(d), 1e-5);

                // Outside the glass: only the soft halo.
                if (d > 0.0)
                {
                    float hal = saturate(1.0 - d / max(_Halo, 1e-5));
                    float edge = saturate(0.5 - d / aa);
                    half a = _HaloAlpha * hal * hal * _Opacity;
                    return half4(0, 0, 0, max(a, edge * _Tint.a * _Opacity));
                }

                // Surface normal from the squircle bezel profile h(t) = (1 - (1 - t)^4)^(1/4), t = depth / bezel.
                const float e = 0.0004;
                float2 g = float2(Shape(p + float2(e, 0)) - Shape(p - float2(e, 0)), Shape(p + float2(0, e)) - Shape(p - float2(0, e)));
                g = g / max(length(g), 1e-6);
                float t = saturate(-d / max(_Bezel, 1e-5));
                float omt = 1.0 - t;
                float slope = min(pow(omt, 3.0) * pow(max(1.0 - omt * omt * omt * omt, 1e-4), -0.75), 5.0);
                float3 nLocal = normalize(float3(g * slope * 0.55, 1.0));   // tilts outward in the rim

                // Local frame of the surface: x right, y up, z toward the viewer (object −z).
                float3 right = normalize(UNITY_MATRIX_M._m00_m10_m20);
                float3 up = normalize(UNITY_MATRIX_M._m01_m11_m21);
                float3 toward = -normalize(UNITY_MATRIX_M._m02_m12_m22);
                float3 N = normalize(right * nLocal.x + up * nLocal.y + toward * nLocal.z);
                float3 V = normalize(GetCameraPositionWS() - i.positionWS);
                float3 L = normalize(_KeyLight.xyz);

                float rim = 1.0 - smoothstep(0.0, 1.0, t);   // 1 at the edge → 0 past the bezel
                float ndv = saturate(dot(N, V));
                float3 H = normalize(L + V);
                half spec = pow(saturate(dot(N, H)), _SpecPower) * _Spec * rim;
                // Dimmer fill on the opposite edge (the "double rim").
                float3 Lf = normalize(reflect(-L, toward));   // key light mirrored across the surface
                half fill = pow(saturate(dot(N, normalize(Lf + V))), _SpecPower) * _Spec * 0.3 * rim;
                half fres = pow(1.0 - ndv, 4.0) * _Fresnel;
                float3 R = reflect(-V, N);
                half3 env = lerp(_EnvBottom.rgb, _EnvTop.rgb, saturate(dot(R, up) * 0.5 + 0.5));

                // Lens: clearer and brighter at the top of the ring.
                float lensD = _Shape > 0.5 ? 1.0 : length(p - _LensCenter.xy) - _LensRadius;
                float lens = _Shape > 0.5 ? 0.0 : saturate(1.0 - (lensD + _LensBlend) / max(_LensBlend * 2.0, 1e-5));
                half4 body = lerp(_Tint, _LensTint, lens);

                half3 c = body.rgb;
                half a = body.a;
                // Reflection and Fresnel mostly in the rim (the middle stays nearly clear).
                c = lerp(c, env, saturate(fres + rim * 0.18));
                a = saturate(a + fres * 0.6 + rim * 0.06);
                // Inner stroke just inside the edge. UX D3: on an inked lens the stroke turns tape yellow (a keyline, as on a
                // selected row) over a faint ink wash; the body stays dark so the white glyph and its label still read.
                half stroke = exp(-pow((d + 0.0008) / 0.00045, 2.0)) * _Stroke;
                half ink = lens * _LensInk.a;
                half inkRim = saturate(1.0 - lensD / max(_LensBlend, 1e-5)) * _LensInk.a;
                c = lerp(c, _LensInk.rgb, ink * 0.16);
                c += stroke * lerp(half3(1, 1, 1), _LensInk.rgb * 2.5, inkRim);
                a = saturate(a + stroke * 0.8 + ink * 0.05);
                // Highlights.
                c += (spec + fill) * (1.0 + lens * 0.6);
                a = saturate(a + (spec + fill) * 0.7);
                // Fingertip glow (spreads from under the finger, fades with distance from the surface).
                float2 dt = p - _Touch.xy;
                half glow = _Touch.w * exp(-dot(dt, dt) / (0.0009 + 1e-6)) * saturate(1.0 - _Touch.z / 0.06);
                c += glow * 0.35; a = saturate(a + glow * 0.2);
                // Detent pulse in the lens.
                c += _Pulse * lens * 0.18; a = saturate(a + _Pulse * lens * 0.08);

                float mask = saturate(0.5 - d / aa);
                return half4(c, a * mask * _Opacity);
            }
            ENDHLSL
        }
    }
}
