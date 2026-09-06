Shader "Sol/SnowParticle"
{
    Properties
    {
        [HDR] _BaseColor("Base Color", Color) = (0.92, 0.95, 1.0, 0.85)
        _BaseMap("Flake", 2D) = "white" {}
        _SoftEdge("Soft Edge", Range(0.0, 1.0)) = 0.35
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+20" "RenderType"="Transparent" }
        Pass
        {
            Name "Snow"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../Water/Shaders/SolAtmosphere.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                float3 positionWS : TEXCOORD1;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float4 _BaseMap_ST;
                half _SoftEdge;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.color = input.color * _BaseColor;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 flake = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);

                // A radial falloff on top of the texture keeps the billboard from showing
                // its quad edge once the flake is scaled up close to the camera, and lets
                // one shader serve both the round fall flake and the blowing streak.
                float2 centred = input.uv * 2.0 - 1.0;
                float radial = saturate(1.0 - dot(centred, centred));
                float alpha = flake.a * lerp(1.0, radial, _SoftEdge);

                // Snow is lit by the sky, not by itself. Running it through the same
                // atmosphere as the rain keeps flakes from punching bright holes in a
                // whiteout, which is exactly where a naive additive snow shader fails.
                float3 viewDirection = normalize(input.positionWS - _WorldSpaceCameraPos);
                float3 color = SolApplyAtmosphere(
                    input.color.rgb * flake.rgb,
                    _WorldSpaceCameraPos,
                    input.positionWS,
                    viewDirection,
                    0.0);
                return half4(color, input.color.a * alpha);
            }
            ENDHLSL
        }
    }
}
