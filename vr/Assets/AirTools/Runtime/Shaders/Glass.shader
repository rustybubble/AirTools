// AirTools spatial glass (SPEC §9 UI design system; docs/UI.md §3). One shader for every UI surface; shape, look and
// tint come from vertex data (see GlassMesh), so each role shares one material and never needs instances or property
// blocks (SRP batcher friendly).
// Liquid Glass for every surface (glass lane): the tool ring's material (AirTools/LiquidGlass) on a rounded rectangle —
// a squircle bezel whose normal tilts outward in the rim, a key light (world up tilted toward the eye, like the ring's)
// that puts a specular highlight on the top rim and a dimmer fill on the bottom, Fresnel and a studio-gradient reflection
// (a pre-blurred room) at grazing angles, a thin inner stroke, a noise-normal ripple that makes the highlight liquid and
// gives the body a faint glint, a clearer bezel over a solid floor behind text. State: glow brightens the rim (hover,
// press, selected), press turns the bezel concave. Light, opaque fills (the D3 white primary, tape-yellow ink) keep
// their bevel. Quest-cheap: no grab pass, opaque texture, blur or render texture (in passthrough the room isn't in the
// eye buffer anyway); the gradient of the rounded rectangle is analytic (no extra distance evaluations), and the rim
// lighting only runs inside the bezel band. Global keyword AIRTOOLS_GLASS_LITE (GlassQuality) draws the flat glass.
// Soft variant (softness > 0) draws elevation shadows. Stereo-instancing aware (single-pass instanced / multiview).
Shader "AirTools/Glass"
{
    Properties
    {
        _RimColor ("Rim colour", Color) = (1, 1, 1, 1)
        _EnvTop ("Reflection top (sky side)", Color) = (0.92, 0.96, 1.0, 1)
        _EnvBottom ("Reflection bottom (floor side)", Color) = (0.20, 0.22, 0.26, 1)
        _SpecPower ("Specular power", Range(4, 64)) = 20
        _KeyLift ("Key light: up tilted toward the eye by", Range(0, 2)) = 0.55
        [Toggle] _ZWriteOn ("Write depth inside the shape", Float) = 1
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Depth test (Always = overlay labels)", Float) = 4
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Glass"
            ZWrite [_ZWriteOn]
            ZTest [_ZTest]
            Cull Back
            // Separate alpha so the panel also occludes the passthrough underlay behind it.
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ AIRTOOLS_GLASS_LITE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _RimColor;
                half4 _EnvTop;
                half4 _EnvBottom;
                half _SpecPower;
                half _KeyLift;
                half _ZWriteOn;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv0 : TEXCOORD0;   // shape-local metres
                float4 uv1 : TEXCOORD1;   // width, height, radius, rim width
                float4 uv2 : TEXCOORD2;   // rim strength, gradient, softness, state glow
                float4 uv3 : TEXCOORD3;   // bezel, highlight, edge clearness, press
                float4 uv4 : TEXCOORD4;   // sheen, frost
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 p : TEXCOORD0;
                float4 shape : TEXCOORD1;
                float4 look : TEXCOORD2;
                float4 liquid : TEXCOORD3;
                float2 grain : TEXCOORD4;
                float3 viewOS : TEXCOORD5;   // eye − position, object space (the surface faces −z)
                float3 upOS : TEXCOORD6;     // world up, object space
                half4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.p = input.uv0;
                o.shape = input.uv1;
                o.look = input.uv2;
                o.liquid = input.uv3;
                o.grain = input.uv4.xy;
                // Per-eye camera (GetCameraPositionWS reads the stereo eye set up above).
                o.viewOS = TransformWorldToObject(GetCameraPositionWS()) - input.positionOS.xyz;
                o.upOS = TransformWorldToObjectDir(float3(0.0, 1.0, 0.0));
                o.color = input.color;
                #ifndef UNITY_COLORSPACE_GAMMA
                // Theme tokens are authored in sRGB and Unity passes mesh vertex colours unconverted in a Linear project
                // (TMP converts its own; this made the accent #408FFF render as #90CAFF and panels a grey haze).
                o.color.rgb = SRGBToLinear(o.color.rgb);
                #endif
                return o;
            }

            float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

            half4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 halfSize = i.shape.xy * 0.5;
                float radius = i.shape.z;
                // Signed distance to the rounded rectangle (negative inside), keeping its pieces for the gradient.
                float2 q = abs(i.p) - halfSize + radius;
                float2 qp = max(q, 0.0);
                float lq = length(qp);
                float d = lq + min(max(q.x, q.y), 0.0) - radius;
                float softness = i.look.z;
                half4 c = i.color;

                if (softness > 0.0)
                {
                    // Elevation shadow: a soft falloff around the shape, no rim.
                    float sa = saturate(1.0 - (d + softness * 0.2) / softness);
                    c.a *= sa * sa;
                    return c;
                }

                float aa = max(fwidth(d), 1e-5);
                float mask = saturate(0.5 - d / aa);
                if (_ZWriteOn > 0.5) clip(mask - 0.5);

                // Top-lit body: a touch lighter at the top edge, darker toward the bottom.
                float v = i.p.y / max(halfSize.y, 1e-4);
                float top = saturate(v * 0.5 + 0.5);
                // UX D3: a light, opaque body (the white primary, tape-yellow ink) must still read as glass, not a flat
                // sticker. `light` is 0 for the dark panels and the translucent veils and 1 for those fills: their top
                // light becomes a ±x3 multiplier (additive would wash out or clip), and their rim a bevel, white along the
                // top shading to a darker keyline at the bottom (a white rim vanishes on white).
                float light = saturate((dot(c.rgb, float3(0.2126, 0.7152, 0.0722)) - 0.3) * 4.0) * saturate((c.a - 0.6) * 2.5);
                c.rgb += i.look.y * v * (1.0 - light);
                c.rgb *= 1.0 + i.look.y * v * 3.0 * light;
                // Fine frost grain, fixed in surface space (no shimmer): the blur stand-in.
                c.rgb += (hash(floor(i.p * 2500.0)) - 0.5) * i.grain.y;

                // Narrow rim line just inside the edge, brighter along the top (and with the state glow).
                float glow = i.look.w;
                float rimW = max(i.shape.w, aa);
                float rim = saturate(1.0 - abs(d + rimW * 0.5) / (rimW * 0.5 + aa));
                float rimLight = saturate(i.look.x * (1.0 + glow) * rim * lerp(0.55 + 0.45 * top, 3.0, light));
                half3 rimColor = lerp(_RimColor.rgb, lerp(c.rgb * 0.55, _RimColor.rgb, top), light);
                c.rgb = lerp(c.rgb, rimColor, rimLight);
                c.a = saturate(c.a + rimLight * 0.5);

            #if !defined(AIRTOOLS_GLASS_LITE)
                float bezel = i.liquid.x;
                float t = saturate(-d / max(bezel, 1e-6));
                // Only the bezel band (and surfaces with a body glint: small controls) pay for the lighting; a window's
                // interior costs what the flat glass did.
                if (bezel > 0.0 && (t < 1.0 || i.grain.x > 0.0))
                {
                    // Presence: the liquid layer scales with the veil, so a clear (borderless) control stays clear at rest
                    // and a fading surface takes its highlights with it.
                    half presence = saturate(c.a * 12.0);
                    half k = i.liquid.y * (1.0 + glow * 0.8) * presence;
                    half dark = 1.0 - light;
                    float3 V = normalize(i.viewOS);
                    float3 L = normalize(i.upOS + V * _KeyLift);
                    float3 H = normalize(L + V);
                    // Noise normal: smooth low-frequency ripples fixed in the surface (the highlight wobbles like liquid).
                    float2 ripple = float2(sin(i.p.x * 71.0 + 2.1 * sin(i.p.y * 43.0)), sin(i.p.y * 67.0 + 1.7 * sin(i.p.x * 37.0))) * 0.06;
                    // Body glint: the rippled normal catching the key light (broad lobe; counted in the text lift).
                    if (i.grain.x > 0.0)
                    {
                        float3 Nb = normalize(float3(ripple, -1.0));
                        c.rgb += pow(saturate(dot(Nb, H)), 12.0) * i.grain.x * presence;
                    }
                    if (t < 1.0)
                    {
                        float band = 1.0 - smoothstep(0.0, 1.0, t);   // 1 at the edge → 0 past the bezel
                        // Outward direction of the edge: the analytic gradient of the rounded rectangle.
                        float2 g = lq > 1e-6 ? qp / lq : (q.x > q.y ? float2(1.0, 0.0) : float2(0.0, 1.0));
                        g *= float2(i.p.x < 0.0 ? -1.0 : 1.0, i.p.y < 0.0 ? -1.0 : 1.0);
                        // Squircle bezel profile h(t) = (1 − (1 − t)^4)^(1/4): its slope tilts the normal outward in the
                        // rim. A press turns it concave (the lens reads pushed in).
                        float omt = 1.0 - t;
                        float omt2 = omt * omt;
                        float slope = min(omt2 * omt * pow(max(1.0 - omt2 * omt2, 1e-4), -0.75), 5.0);
                        float tilt = slope * 0.55 * (1.0 - 1.6 * i.liquid.w);
                        float3 N = normalize(float3(g * tilt + ripple, -1.0));
                        // Key light on the rim, the dimmer fill on the opposite edge (the key mirrored across the surface).
                        float3 Lf = float3(-L.xy, L.z);
                        half spec = pow(saturate(dot(N, H)), _SpecPower) * 0.42 * k * band;
                        half fill = pow(saturate(dot(N, normalize(Lf + V))), _SpecPower) * 0.126 * k * band;
                        half ndv = saturate(dot(N, V));
                        half fres = pow(1.0 - ndv, 4.0) * 0.2 * k;
                        float3 R = reflect(-V, N);
                        half3 env = lerp(_EnvBottom.rgb, _EnvTop.rgb, saturate(dot(R, i.upOS) * 0.5 + 0.5));
                        // A clearer edge over the solid floor (dark glass; a light fill stays opaque).
                        c.a *= 1.0 - i.liquid.z * band * presence * dark;
                        // Reflection and Fresnel in the rim (dark bodies); a light fill gets a lensing bevel instead: the
                        // top of the bezel lit, the bottom a touch darker (inverted while pressed).
                        c.rgb = lerp(c.rgb, env, saturate(fres + band * 0.18 * k) * dark);
                        c.a = saturate(c.a + (fres * 0.6 + band * 0.06 * k) * dark);
                        c.rgb *= 1.0 + light * band * g.y * 0.10 * sign(tilt + 1e-4);
                        // Thin inner stroke just inside the edge (scaled with the bezel, so labels in scaled spaces match).
                        float sd = (d + bezel * 0.12) / (bezel * 0.07);
                        half stroke = exp(-sd * sd) * 0.28 * k * (1.0 + glow * 0.5);
                        c.rgb += stroke * dark;
                        c.a = saturate(c.a + stroke * 0.8 * dark);
                        // Highlights.
                        c.rgb += spec + fill;
                        c.a = saturate(c.a + (spec + fill) * 0.7);
                    }
                }
            #endif
                c.a *= mask;
                return c;
            }
            ENDHLSL
        }
    }
}
