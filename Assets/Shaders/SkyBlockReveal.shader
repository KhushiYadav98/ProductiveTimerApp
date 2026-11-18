// Used only while the sky sphere "builds" itself (TownBuildReveal swaps it in, then restores the original).
// The sphere is split into a latitude/longitude grid of tiles; tiles appear bottom-to-top (with some randomness),
// each one growing from its center with a glowing border, so the sky assembles block by block.
// Lit like URP/Lit so the swap back to the original material is seamless.
Shader "ProductivityTown/Sky Block Reveal"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _Metallic ("Metallic", Range(0, 1)) = 0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5
        [HDR] _EmissionColor ("Emission", Color) = (0, 0, 0, 1)
        _EmissionMap ("Emission Map", 2D) = "white" {}
        _SkyReveal ("Reveal (0 = none, 1 = all tiles)", Range(0, 1)) = 1
        _Tiles ("Tiles (around, up)", Vector) = (36, 18, 0, 0)
        _TileSpread ("Tile Pop Length (fraction of reveal)", Range(0.02, 0.5)) = 0.18
        _TileRandomness ("Order Randomness", Range(0, 1)) = 0.35
        _TileGap ("Gap Between Tiles", Range(0, 0.3)) = 0.06
        [HDR] _TileGlowColor ("Tile Glow", Color) = (0.55, 0.85, 1.3, 1)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        [ToggleOff] _SpecularHighlights ("Specular Highlights", Float) = 1
        [ToggleOff] _EnvironmentReflections ("Environment Reflections", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Metallic;
                half _Smoothness;
                half4 _EmissionColor;
                float _SkyReveal;
                float4 _Tiles;
                float _TileSpread;
                float _TileRandomness;
                float _TileGap;
                half4 _TileGlowColor;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float3 dirOS : TEXCOORD3;
                float fogFactor : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float Hash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.dirOS = input.positionOS.xyz;
                output.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Which tile this pixel is in (longitude/latitude grid around the sphere's center).
                float3 d = normalize(input.dirOS);
                float lon = atan2(d.z, d.x) / (2.0 * PI) + 0.5;     // 0..1 around
                float lat = asin(clamp(d.y, -1.0, 1.0)) / PI + 0.5; // 0 bottom .. 1 top
                float2 grid = float2(lon, lat) * _Tiles.xy;
                float2 cell = floor(grid);
                float2 inCell = frac(grid);

                // Tile order: bottom to top, shuffled a little. Each tile then grows from its center.
                float order = lerp((cell.y + 0.5) / _Tiles.y, Hash(cell), _TileRandomness);
                float start = order * (1.0 - _TileSpread);
                float grow = saturate((_SkyReveal - start) / _TileSpread);
                grow = 1.0 - (1.0 - grow) * (1.0 - grow); // ease out

                float2 fromCenter = abs(inCell - 0.5);
                float reach = max(fromCenter.x, fromCenter.y);
                // Fully grown tiles close their gaps, so the finished sky is seamless.
                float gap = _TileGap * (1.0 - step(0.999, grow));
                clip(0.5 * grow - gap * 0.5 - reach);

                // Glowing border on tiles still growing, fading as they settle.
                float border = saturate(1.0 - (0.5 * grow - gap * 0.5 - reach) / 0.08);
                float glow = border * (1.0 - grow) + (1.0 - grow) * 0.12;

                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = NormalizeNormalPerPixel(input.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.bakedGI = SampleSH(inputData.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo.rgb;
                surface.metallic = _Metallic;
                surface.smoothness = _Smoothness;
                surface.normalTS = half3(0, 0, 1);
                surface.emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, input.uv).rgb * _EmissionColor.rgb + _TileGlowColor.rgb * glow;
                surface.occlusion = 1;
                surface.alpha = 1;

                half4 color = UniversalFragmentPBR(inputData, surface);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = 1;
                return color;
            }
            ENDHLSL
        }
    }
}
