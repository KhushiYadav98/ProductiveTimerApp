// Stylized soft smoke for the train's puffs: translucent with soft edges, billowing noise on the surface, gentle
// sun shading, and a noise dissolve for breaking up as a puff fades. _Fade and _Dissolve are driven per puff by
// SmokePuffs (via MaterialPropertyBlock).
// Alpha is blended separately so the eye buffer stays opaque behind the smoke (passthrough is composited wherever
// the eye buffer's alpha is < 1, and normal alpha blending would let it leak through).
Shader "ProductivityTown/Smoke"
{
    Properties
    {
        _Color ("Lit Color", Color) = (0.97, 0.97, 0.98, 1)
        _ShadeColor ("Shade Color", Color) = (0.66, 0.69, 0.78, 1)
        _Opacity ("Opacity", Range(0, 1)) = 0.9
        _EdgeSoftness ("Edge Softness", Range(0.1, 4)) = 1.2
        _NoiseScale ("Noise Scale", Float) = 6
        _NoiseSpeed ("Noise Speed", Float) = 0.8
        _Billow ("Billow (surface wobble)", Range(0, 0.5)) = 0.18
        _Fade ("Fade", Range(0, 1)) = 1
        _Dissolve ("Dissolve", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Smoke"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _ShadeColor;
                half _Opacity;
                half _EdgeSoftness;
                float _NoiseScale;
                float _NoiseSpeed;
                half _Billow;
                half _Fade;
                half _Dissolve;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 noisePos : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            // Smooth value noise in [0, 1].
            float Noise(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(Hash(i + float3(0, 0, 0)), Hash(i + float3(1, 0, 0)), f.x),
                                 lerp(Hash(i + float3(0, 1, 0)), Hash(i + float3(1, 1, 0)), f.x), f.y),
                            lerp(lerp(Hash(i + float3(0, 0, 1)), Hash(i + float3(1, 0, 1)), f.x),
                                 lerp(Hash(i + float3(0, 1, 1)), Hash(i + float3(1, 1, 1)), f.x), f.y), f.z);
            }

            float Fbm(float3 p)
            {
                return Noise(p) * 0.6 + Noise(p * 2.03 + 7.1) * 0.3 + Noise(p * 4.1 + 3.3) * 0.1;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // Noise in a unit-size space so it looks the same on every puff size; it drifts upward over time.
                float radius = max(length(input.positionOS.xyz), 1e-5);
                float3 unit = input.positionOS.xyz / radius;
                float3 noisePos = unit * _NoiseScale * 0.25 + float3(0, -_Time.y * _NoiseSpeed, _Time.y * 0.17);

                // Billow: push the surface in and out along the normal.
                float n = Fbm(noisePos);
                float3 displaced = input.positionOS.xyz + input.normalOS * (n - 0.45) * _Billow * radius * 2.0;

                output.positionWS = TransformObjectToWorld(displaced);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.noisePos = noisePos;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 normal = normalize(input.normalWS);
                float3 viewDir = normalize(GetWorldSpaceViewDir(input.positionWS));

                // Half-Lambert sun shading, soft and cartoony.
                Light sun = GetMainLight();
                half lit = saturate(dot(normal, sun.direction) * 0.5 + 0.5);
                half3 color = lerp(_ShadeColor.rgb, _Color.rgb, lit) * lerp(1.0h, sun.color, 0.35h);

                // Soft edges: thinner where the surface turns away from the viewer.
                half facing = saturate(dot(normal, viewDir));
                half alpha = _Opacity * pow(facing, _EdgeSoftness);

                // Wispy breakup, stronger as the puff dissolves.
                float detail = Fbm(input.noisePos * 2.0 + 11.0);
                alpha *= smoothstep(_Dissolve - 0.05, _Dissolve + 0.25, detail);
                color *= 0.92 + 0.08 * detail;

                return half4(color, saturate(alpha * _Fade));
            }
            ENDHLSL
        }
    }
}
