// Texture-free particle look for the town's effects, drawn from the quad's UVs:
//   Shape 0 = soft, noisy dust/smoke puff, 1 = four-point sparkle, 2 = rounded confetti rectangle.
// Color and alpha come from the particle's vertex color. Color blending is set per material (alpha blend or
// additive), while alpha always uses (One, OneMinusSrcAlpha) so the eye buffer stays opaque and passthrough doesn't
// leak through the effects.
Shader "ProductivityTown/Particle Soft"
{
    Properties
    {
        [Enum(Puff,0,Sparkle,1,Confetti,2)] _Shape ("Shape", Float) = 0
        _Softness ("Edge Softness", Range(0.01, 1)) = 0.6
        _NoiseAmount ("Noise (puffs)", Range(0, 1)) = 0.45
        _Intensity ("Intensity", Range(0, 4)) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5   // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10  // OneMinusSrcAlpha
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

        Pass
        {
            Name "ParticleSoft"
            Tags { "LightMode" = "UniversalForward" }

            Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Shape;
                half _Softness;
                half _NoiseAmount;
                half _Intensity;
                float _SrcBlend;
                float _DstBlend;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float3 seed : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float Noise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x),
                            lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), f.x), f.y);
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.color = input.color;
                output.uv = input.uv;
                // Per-particle variation for the noise: the quad's position, quantized so it's stable across its corners.
                output.seed = floor(positionWS * 37.0);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 p = input.uv * 2.0 - 1.0; // -1..1 across the quad
                float r = length(p);
                half shape;

                if (_Shape < 0.5)
                {
                    // Puff: soft disc whose edge is broken up by noise, plus some inner density variation.
                    float2 offset = float2(Hash(input.seed.xy), Hash(input.seed.yz)) * 50.0;
                    float n = Noise(p * 2.5 + offset) * 0.65 + Noise(p * 5.0 + offset * 1.7) * 0.35;
                    float edge = 1.0 - _NoiseAmount * 0.5 + (n - 0.5) * _NoiseAmount;
                    shape = 1.0 - smoothstep(edge - _Softness, edge, r);
                    shape *= lerp(1.0, 0.65 + 0.35 * n, _NoiseAmount);
                }
                else if (_Shape < 1.5)
                {
                    // Sparkle: bright core with a four-point glint.
                    float core = saturate(1.0 - r * 2.2);
                    float rays = saturate(1.0 - abs(p.x) * 9.0) * saturate(1.0 - abs(p.y)) +
                                 saturate(1.0 - abs(p.y) * 9.0) * saturate(1.0 - abs(p.x));
                    shape = saturate(core * core * 1.5 + rays * 0.9) * saturate(1.0 - r);
                }
                else
                {
                    // Confetti: rounded rectangle.
                    float2 q = abs(p) - float2(0.75, 0.45);
                    float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - 0.15;
                    shape = 1.0 - smoothstep(-0.05, 0.05, d);
                }

                half alpha = saturate(shape * input.color.a);
                half3 rgb = input.color.rgb * _Intensity;
                // With Src Blend = One (additive / premultiplied), the color must carry the alpha itself.
                rgb *= (_SrcBlend == 1.0) ? alpha : 1.0;
                return half4(rgb, alpha);
            }
            ENDHLSL
        }
    }
}
