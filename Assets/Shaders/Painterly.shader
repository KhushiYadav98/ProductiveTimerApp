// Ghibli-style illustrated look for the town (Quest-friendly):
//  - flat cel shading with a crisp, organic light/shadow shape (broken up by a soft noise, like painted shadows);
//  - shadows are the surface color shifted cooler and a little richer, never gray;
//  - soft gouache "dapple" patches and tiny light speckles from one small tileable texture, projected per vertex
//    from the surface's dominant axis in object space — one texture fetch per pixel;
//  - warm sky light on upward-facing surfaces (tops of trees and roofs glow), soft distance haze;
//  - thin ink lines on the meshes' real feature edges, colored as a dark version of each surface, from the edge
//    data the Hologram import step bakes into UV2 — no extra outline pass. Lines fade with distance.
// Ambient (SH) is evaluated per vertex; all properties live in UnityPerMaterial, so the SRP Batcher applies.
Shader "ProductivityTown/Ghibli Toon"
{
    Properties
    {
        [MainColor] _BaseColor ("Color", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap ("Albedo (optional)", 2D) = "white" {}
        [HDR] _EmissionColor ("Emission", Color) = (0, 0, 0, 0)

        [Header(Gouache Texture)]
        [NoScaleOffset] _BrushTex ("Pattern (R dapple  G breakup  B speckle  A grain)", 2D) = "gray" {}
        _BrushScale ("Pattern Tiling (per object unit)", Float) = 3
        _Dapple ("Dapple Patches", Range(0, 0.5)) = 0.08
        _Speckle ("Speckles", Range(0, 1)) = 0.35

        [Header(Cel Lighting)]
        _ShadowTint ("Shadow Tint", Color) = (0.66, 0.74, 0.8, 1)
        _ShadowSaturation ("Shadow Richness", Range(0.5, 2)) = 1.15
        _ShadowThreshold ("Shadow Threshold", Range(-1, 1)) = -0.12
        _ShadowSoftness ("Shadow Edge Softness", Range(0.001, 0.3)) = 0.025
        _Breakup ("Shadow Shape Breakup", Range(0, 1)) = 0.1
        _LightIntensity ("Sun Intensity", Range(0, 2)) = 0.9
        _SunTint ("Sun Tint", Color) = (1.0, 0.97, 0.88, 1)
        _SkyLight ("Warm Light On Tops", Range(0, 1)) = 0.18
        _SkyLightColor ("Top Light Color", Color) = (1.0, 0.95, 0.7, 1)
        _AmbientStrength ("Ambient Strength", Range(0, 1)) = 0.15
        _Saturation ("Overall Saturation", Range(0, 2)) = 0.85

        [Header(Ink Lines (baked feature edges))]
        _InkDarkness ("Ink Darkness", Range(0, 1)) = 0.7
        _InkTint ("Ink Tint", Color) = (0.2, 0.26, 0.16, 1)
        _InkWidth ("Ink Width (pixels)", Range(0, 3)) = 1.0
        _InkStrength ("Ink Strength", Range(0, 1)) = 0.85
        _InkInterior ("Ink on Inner Edges", Range(0, 1)) = 0.2
        _InkFadeStart ("Ink Fade Start (m)", Float) = 3
        _InkFadeEnd ("Ink Fade End (m)", Float) = 9

        [Header(Atmosphere)]
        _HazeColor ("Haze Color", Color) = (0.78, 0.88, 0.86, 1)
        _HazeStart ("Haze Start (m)", Float) = 3
        _HazeEnd ("Haze End (m)", Float) = 14
        _HazeStrength ("Haze Strength", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor;
            float4 _BaseMap_ST;
            half4 _EmissionColor;
            float _BrushScale;
            half _Dapple;
            half _Speckle;
            half4 _ShadowTint;
            half _ShadowSaturation;
            half _ShadowThreshold;
            half _ShadowSoftness;
            half _Breakup;
            half _LightIntensity;
            half4 _SunTint;
            half _SkyLight;
            half4 _SkyLightColor;
            half _AmbientStrength;
            half _Saturation;
            half _InkDarkness;
            half4 _InkTint;
            half _InkWidth;
            half _InkStrength;
            half _InkInterior;
            float _InkFadeStart;
            float _InkFadeEnd;
            half4 _HazeColor;
            float _HazeStart;
            float _HazeEnd;
            half _HazeStrength;
        CBUFFER_END

        TEXTURE2D(_BaseMap);  SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BrushTex); SAMPLER(sampler_BrushTex);

        // Planar projection on the dominant axis of the object-space normal (offset per axis to hide repeats).
        float2 PatternUV(float3 positionOS, float3 normalOS)
        {
            float3 a = abs(normalOS);
            float2 uv = a.x > a.y && a.x > a.z ? positionOS.zy
                      : (a.y > a.z ? positionOS.xz * float2(1, -1) + 0.37 : positionOS.xy + 0.71);
            return uv * _BrushScale;
        }

        half Luma(half3 c) { return dot(c, half3(0.299h, 0.587h, 0.114h)); }
        ENDHLSL

        Pass
        {
            Name "GhibliToon"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float3 bary : TEXCOORD2; // baked feature-edge barycentrics (+1 hides an edge); zero if absent
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float4 uv : TEXCOORD2;        // xy = albedo uv, zw = pattern uv
                half4 ambientFog : TEXCOORD3; // rgb = SH ambient, a = fog factor
                float3 bary : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv.xy = TRANSFORM_TEX(input.uv, _BaseMap);
                output.uv.zw = PatternUV(input.positionOS.xyz, input.normalOS);
                output.ambientFog.rgb = SampleSH(output.normalWS);
                output.ambientFog.a = ComputeFogFactor(pos.positionCS.z);
                output.bary = input.bary;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 pattern = SAMPLE_TEXTURE2D(_BrushTex, sampler_BrushTex, input.uv.zw);
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv.xy).rgb * _BaseColor.rgb;

                // Gouache dapple: soft lighter/darker patches of the same color.
                albedo *= 1.0h + (pattern.r - 0.5h) * 2.0h * _Dapple;

                half3 normal = normalize(input.normalWS);
                Light sun = GetMainLight(TransformWorldToShadowCoord(input.positionWS));

                // Cel light with an organic edge.
                half ndl = dot(normal, sun.direction);
                half light = ndl * sun.shadowAttenuation + (pattern.g - 0.5h) * _Breakup;
                half lit = smoothstep(_ShadowThreshold - _ShadowSoftness, _ShadowThreshold + _ShadowSoftness, light);

                // Shadow = same hue, cooler and richer.
                half3 shadowColor = albedo * _ShadowTint.rgb;
                shadowColor = max(0.0h, lerp(Luma(shadowColor).xxx, shadowColor, _ShadowSaturation));
                half3 litColor = albedo * _SunTint.rgb * lerp(1.0h, sun.color, 0.5h) * _LightIntensity;
                half3 color = lerp(shadowColor, litColor, lit);

                // Warm sky light on top-facing surfaces (sunlit tops of foliage and roofs), plus a little ambient.
                half up = saturate(normal.y);
                color += albedo * _SkyLightColor.rgb * (up * up) * _SkyLight * (0.4h + 0.6h * lit);
                color += albedo * input.ambientFog.rgb * _AmbientStrength;

                // Tiny light speckles (moss, pollen), mostly in the light.
                half speckle = smoothstep(0.55h, 0.8h, pattern.b) * _Speckle * (0.35h + 0.65h * lit);
                color = lerp(color, color * 1.25h + 0.03h, speckle);
                color *= 0.96h + 0.08h * pattern.a; // paper grain

                // Ink: a dark, hue-keeping version of the surface on its feature edges.
                if (input.bary.x + input.bary.y + input.bary.z > 0.5)
                {
                    float3 baryPx = input.bary / max(fwidth(input.bary), 1e-5);
                    float edgeDist = min(baryPx.x, min(baryPx.y, baryPx.z));
                    half ink = saturate(_InkWidth - edgeDist + 0.5h);

                    float3 toCamera = GetCameraPositionWS() - input.positionWS;
                    float distance = length(toCamera);
                    ink *= 1.0h - saturate((distance - _InkFadeStart) / max(_InkFadeEnd - _InkFadeStart, 1e-3));
                    half facing = saturate(dot(normal, toCamera / max(distance, 1e-4)));
                    ink *= lerp(_InkInterior, 1.0h, saturate((1.0h - facing) * 2.5h));

                    half3 inkColor = lerp(albedo, _InkTint.rgb, 0.5h) * (1.0h - _InkDarkness);
                    color = lerp(color, inkColor, ink * _InkStrength);
                }

                // Softer, watercolor-like palette.
                color = max(0.0h, lerp(Luma(color).xxx, color, _Saturation));

                color += _EmissionColor.rgb;

                // Soft aerial haze toward the distance.
                float d = length(GetCameraPositionWS() - input.positionWS);
                half haze = saturate((d - _HazeStart) / max(_HazeEnd - _HazeStart, 1e-3)) * _HazeStrength;
                color = lerp(color, _HazeColor.rgb, haze);

                color = MixFog(color, input.ambientFog.a);
                return half4(color, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 vert(Attributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirection = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirection = _LightDirection;
                #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirection));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

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

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 frag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
