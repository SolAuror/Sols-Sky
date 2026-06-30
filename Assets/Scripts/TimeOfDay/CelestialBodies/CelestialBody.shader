Shader "Sol/CelestialBody"
{
    Properties
    {
        _BaseColor      ("Lit Color",              Color)          = (1, 1, 1, 1)
        _DarkColor      ("Dark Side Color",        Color)          = (0.02, 0.02, 0.04, 1)
        [HDR]
        _EmissionColor  ("Emission",               Color)          = (0, 0, 0, 1)
        _SunDirection   ("Sun Direction (world)",   Vector)         = (0, 1, 0, 0)
        _TerminatorSharpness ("Terminator Sharpness", Range(1, 10)) = 3
        _EclipseFactor  ("Eclipse Factor",          Range(0, 1))    = 0
        _EclipseTint    ("Eclipse Tint",            Color)          = (0.6, 0.15, 0.1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Transparent"
        }

        Pass
        {
            Name "CelestialBodyForward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _DarkColor;
                float4 _EmissionColor;
                float4 _SunDirection;
                float  _TerminatorSharpness;
                float  _EclipseFactor;
                float4 _EclipseTint;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv         = IN.uv;
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // Remap UV [0,1] → centered [-1,1]
                float2 centered = IN.uv * 2.0 - 1.0;
                float r2 = dot(centered, centered);

                // Discard pixels outside the unit circle
                clip(1.0 - r2);

                // Anti-aliased edge via screen-space derivatives
                float dist  = length(centered);
                float alpha = saturate((1.0 - dist) / fwidth(dist));

                // Reconstruct spherical normal in object space
                // (quad faces camera, so local Z = toward camera)
                float3 localN = float3(centered.x, centered.y, sqrt(max(0.0, 1.0 - r2)));

                // Transform to world space using the object's rotation
                float3 N      = normalize(mul((float3x3)UNITY_MATRIX_M, localN));
                float3 sunDir = normalize(_SunDirection.xyz);

                float NdotL = dot(N, sunDir);
                float litFactor = saturate(NdotL * _TerminatorSharpness * 0.5 + 0.5);

                float3 body = lerp(_DarkColor.rgb, _BaseColor.rgb, litFactor);
                body += _EmissionColor.rgb;

                body  = lerp(body, body * _EclipseTint.rgb, _EclipseFactor);
                body *= 1.0 - _EclipseFactor * 0.9;

                return half4(body, alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
