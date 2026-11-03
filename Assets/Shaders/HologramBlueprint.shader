// Holographic blueprint look: glowing feature edges, translucent blue fill, blueprint grid lines,
// rim glow, rising scanlines and a gentle pulse. Works by just assigning the material.
//
// - Glowing edges need edge data in UV2: put the model in a folder named "Hologram" and it is baked
//   on import (HologramModelPostprocessor). Without it, everything else still renders.
// - Optional HologramTown component on the root adds the bottom-up "build" reveal.
Shader "ProductivityTown/Hologram Blueprint"
{
    Properties
    {
        [Header(Edges (models in a Hologram folder))]
        [HDR] _EdgeColor ("Edge Color", Color) = (0.35, 0.9, 1, 1)
        _EdgeWidth ("Edge Width (pixels)", Range(0.5, 6)) = 1.5
        _EdgeGlowWidth ("Edge Glow Width (pixels)", Range(0.1, 20)) = 6
        _EdgeGlowStrength ("Edge Glow Strength", Range(0, 1)) = 0.35

        [Header(Fill)]
        _FillColor ("Fill Color (alpha = opacity)", Color) = (0.03, 0.18, 0.4, 0.25)
        [HDR] _RimColor ("Rim Color", Color) = (0.2, 0.7, 1, 1)
        _RimPower ("Rim Power", Range(0.5, 8)) = 3
        _RimStrength ("Rim Strength", Range(0, 1)) = 0.6
        _BackFaceDim ("Back Face Brightness", Range(0, 1)) = 0.35

        [Header(Blueprint Grid)]
        _GridColor ("Grid Color (alpha = strength)", Color) = (0.3, 0.85, 1, 0.5)
        _GridCellSize ("Grid Cell Size (meters)", Float) = 0.02
        _GridLineWidth ("Grid Line Width (pixels)", Range(0.5, 3)) = 1
        _GridWallStrength ("Grid on Walls", Range(0, 1)) = 0.3

        [Header(Scanlines and Pulse)]
        _ScanSpacing ("Scanline Spacing (meters)", Float) = 0.02
        _ScanSpeed ("Scanline Speed (m per s)", Float) = 0.01
        _ScanStrength ("Scanline Strength", Range(0, 1)) = 0.2
        _PulseSpeed ("Pulse Speed", Float) = 2
        _PulseStrength ("Pulse Strength", Range(0, 0.5)) = 0.1

        [Header(Build Reveal (needs HologramTown component))]
        _Reveal ("Reveal (0 = hidden, 1 = fully built)", Range(0, 1)) = 1
        [HDR] _RevealEdgeColor ("Reveal Edge Color", Color) = (0.6, 1, 1, 1)
        _RevealBand ("Reveal Edge Band (town units)", Float) = 0.02

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "HologramForward"
            Tags { "LightMode" = "UniversalForward" }

            // Premultiplied alpha: alpha is coverage over passthrough, so glowing edges stay solid.
            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _EdgeColor;
                float _EdgeWidth;
                float _EdgeGlowWidth;
                float _EdgeGlowStrength;
                float4 _FillColor;
                float4 _RimColor;
                float _RimPower;
                float _RimStrength;
                float _BackFaceDim;
                float4 _GridColor;
                float _GridCellSize;
                float _GridLineWidth;
                float _GridWallStrength;
                float _ScanSpacing;
                float _ScanSpeed;
                float _ScanStrength;
                float _PulseSpeed;
                float _PulseStrength;
                float _Reveal;
                float4 _RevealEdgeColor;
                float _RevealBand;
                float _Cull;
                // Set per renderer by HologramTown.cs (zero when the component is absent).
                float4x4 _TownWorldToLocal;
                float _UseTownSpace;
                float _TownMinY;
                float _TownMaxY;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 bary : TEXCOORD2; // xyz barycentric; +1 on a component hides that edge
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 bary : TEXCOORD2;
                float3 gridPos : TEXCOORD3;    // object-aligned, unscaled (meters), follows rotation
                float3 gridNormal : TEXCOORD4;
                float townY : TEXCOORD5;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.bary = input.bary.xyz;

                // Object axes without scale, so grid size is in meters whatever the import scale.
                float4x4 m = GetObjectToWorldMatrix();
                float3 ax = normalize(float3(m[0][0], m[1][0], m[2][0]));
                float3 ay = normalize(float3(m[0][1], m[1][1], m[2][1]));
                float3 az = normalize(float3(m[0][2], m[1][2], m[2][2]));
                float3 d = pos.positionWS - float3(m[0][3], m[1][3], m[2][3]);
                output.gridPos = float3(dot(ax, d), dot(ay, d), dot(az, d));
                output.gridNormal = float3(dot(ax, output.normalWS), dot(ay, output.normalWS), dot(az, output.normalWS));

                output.townY = _UseTownSpace > 0.5 ? mul(_TownWorldToLocal, float4(pos.positionWS, 1.0)).y : 0;
                return output;
            }

            float4 Frag(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Build reveal (only with HologramTown): cut above the rising line, glow at the cut.
                float revealGlow = 0;
                if (_UseTownSpace > 0.5 && _TownMaxY > _TownMinY)
                {
                    float revealY = lerp(_TownMinY - _RevealBand, _TownMaxY + _RevealBand, _Reveal);
                    float aboveCut = input.townY - revealY;
                    clip(-aboveCut);
                    revealGlow = (_Reveal < 0.999) ? saturate(1.0 + aboveCut / max(_RevealBand, 1e-4)) : 0;
                }

                float faceMul = IS_FRONT_VFACE(face, 1.0, _BackFaceDim);
                float pulse = 1.0 + sin(_Time.y * _PulseSpeed) * _PulseStrength;

                // Feature edges (only if the mesh has baked edge data), constant width in pixels.
                float edge = 0;
                if (input.bary.x + input.bary.y + input.bary.z > 0.5)
                {
                    float3 baryPx = input.bary / max(fwidth(input.bary), 1e-5);
                    float edgeDist = min(baryPx.x, min(baryPx.y, baryPx.z));
                    float edgeLine = saturate(_EdgeWidth - edgeDist + 0.5);
                    float edgeGlow = exp(-edgeDist / _EdgeGlowWidth) * _EdgeGlowStrength;
                    edge = saturate(edgeLine + edgeGlow) * faceMul * pulse;
                }

                // Fresnel rim: bright silhouettes.
                float3 n = normalize(input.normalWS);
                float3 v = normalize(GetWorldSpaceViewDir(input.positionWS));
                float rim = pow(1.0 - saturate(abs(dot(n, v))), _RimPower) * _RimStrength * faceMul * pulse;

                // Blueprint grid on every face (projected on the face's main axis), strongest on up-facing surfaces.
                float3 an = abs(input.gridNormal);
                float2 g = (an.y >= max(an.x, an.z)) ? input.gridPos.xz
                         : (an.x >= an.z) ? input.gridPos.yz : input.gridPos.xy;
                g /= _GridCellSize;
                float2 gDist = abs(frac(g - 0.5) - 0.5) / max(fwidth(g), 1e-5);
                float upMask = saturate((n.y - 0.7) / 0.2);
                float grid = saturate(_GridLineWidth - min(gDist.x, gDist.y) + 0.5)
                          * lerp(_GridWallStrength, 1.0, upMask) * _GridColor.a * faceMul;

                // Scanlines rising in world space.
                float s = frac((input.positionWS.y - _Time.y * _ScanSpeed) / _ScanSpacing);
                float scan = pow(1.0 - s, 8.0) * _ScanStrength * faceMul;

                float fillA = _FillColor.a * faceMul;
                float3 color = _FillColor.rgb * fillA
                            + _RimColor.rgb * rim
                            + _GridColor.rgb * grid
                            + _EdgeColor.rgb * (edge + scan)
                            + _RevealEdgeColor.rgb * revealGlow;
                float alpha = saturate(fillA + rim + grid + edge + scan + revealGlow);

                return float4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
