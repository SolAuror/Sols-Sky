Shader "Sol/RainParticle"
{
    Properties
    {
        [HDR] _BaseColor("Base Color", Color) = (0.68, 0.78, 0.9, 0.42)
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+20" "RenderType"="Transparent" }
        Pass
        {
            Name "Rain"
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

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = input.uv;
                output.color = input.color * _BaseColor;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float edge = abs(input.uv.x * 2.0 - 1.0);
                float endFade = saturate(input.uv.y * (1.0 - input.uv.y) * 7.0);
                float alpha = saturate(1.0 - edge * edge) * endFade;
                float3 viewDirection = normalize(input.positionWS - _WorldSpaceCameraPos);
                float3 color = SolApplyAtmosphere(
                    input.color.rgb,
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
