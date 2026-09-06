Shader "Sol/CelestialBody"
{
    Properties
    {
        _BaseColor      ("Lit Color",              Color)          = (1, 1, 1, 1)
        _DarkColor      ("Dark Side Color",        Color)          = (0.02, 0.02, 0.04, 1)
        [NoScaleOffset]
        _SurfaceTex     ("Surface Texture (full disc)", 2D)        = "white" {}
        _SurfaceRotation ("Surface Rotation",      Range(0, 360))  = 0
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
                float  _SurfaceRotation;
            CBUFFER_END

            TEXTURE2D(_SurfaceTex);
            SAMPLER(sampler_SurfaceTex);
            TEXTURE2D_X(_SolCloudRenderTexture);
            float _SolCloudActive;

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

                // Full-disc surface texture (e.g. a moon face photo), rotated
                // on the disc. Default white texture leaves the body flat.
                float sr, cr;
                sincos(radians(_SurfaceRotation), sr, cr);
                float2 texUV = float2(centered.x * cr - centered.y * sr,
                                      centered.x * sr + centered.y * cr) * 0.5 + 0.5;
                half3 albedo = SAMPLE_TEXTURE2D(_SurfaceTex, sampler_SurfaceTex, texUV).rgb;

                // Dark side keeps a faint luminance hint of the surface
                // detail (earthshine-style) instead of going fully flat.
                float albedoLuma = dot(albedo, half3(0.299, 0.587, 0.114));
                float3 darkCol = _DarkColor.rgb * lerp(1.0, albedoLuma, 0.4);

                float3 body = lerp(darkCol, _BaseColor.rgb * albedo, litFactor);
                body += _EmissionColor.rgb;

                body  = lerp(body, body * _EclipseTint.rgb, _EclipseFactor);
                body *= 1.0 - _EclipseFactor * 0.9;

                // Tertiary bodies are transparent geometry, so they execute after the
                // cloud composite. Re-apply the same radiance/transmittance field here to
                // preserve the physical cloud ordering without a second cloud evaluation.
                if (_SolCloudActive > 0.5)
                {
                    float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                    float4 cloud = SAMPLE_TEXTURE2D_X(
                        _SolCloudRenderTexture, sampler_LinearClamp, screenUV);
                    body = body * cloud.a + cloud.rgb;
                }

                return half4(body, alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
