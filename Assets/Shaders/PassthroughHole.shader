// Punches a soft round hole through everything drawn behind it, so passthrough (the real room) shows there.
// Passthrough is composited behind the app wherever the eye buffer's alpha is < 1; this multiplies the
// color and alpha already drawn by (1 - punch). Put it on a flat disc just above the virtual floor:
// virtual things above the disc (town, screen, hands) are untouched; everything under it inside the ring is cleared.
// Hole center/radius are global, set by OnboardingManager from the boundary ring.
Shader "ProductivityTown/Passthrough Hole"
{
    Properties
    {
        _HoleFeather ("Edge Softness (m)", Range(0.01, 1)) = 0.3
        _HoleExtra ("Extra Radius (m)", Range(-1, 2)) = 0
    }

    SubShader
    {
        // After opaques and the sky, before transparents (the ring and the hologram draw over it).
        Tags { "RenderType" = "Transparent" "Queue" = "AlphaTest+51" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        Pass
        {
            Name "PassthroughHole"
            Tags { "LightMode" = "UniversalForward" }

            Blend Zero OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                float _HoleFeather;
                float _HoleExtra;
            CBUFFER_END

            float4 _PassthroughHoleCenter;
            float _PassthroughHoleRadius;

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            float4 frag(Varyings input) : SV_Target
            {
                float dist = length(input.positionWS.xz - _PassthroughHoleCenter.xz);
                float radius = _PassthroughHoleRadius + _HoleExtra;
                float punch = 1.0 - smoothstep(radius, radius + _HoleFeather, dist);
                return float4(0, 0, 0, punch);
            }
            ENDHLSL
        }
    }
}
