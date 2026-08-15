Shader "Hidden/Sol/Water2/Underwater"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off
        Pass
        {
            Name "UnderwaterComposition"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Fragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float4 _SolUnderwaterColor;
            float4 _SolUnderwaterParams; // density, distortion, transition, camera depth
            float3 _SolWaterAbsorption;
            float _SolWaterWaveTime;

            float Hash21(float2 value)
            {
                float3 p = frac(float3(value.xyx) * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            float ValueNoise(float2 position)
            {
                float2 cell = floor(position);
                float2 local = frac(position);
                float2 blend = local * local * (3.0 - 2.0 * local);
                float a = Hash21(cell);
                float b = Hash21(cell + float2(1, 0));
                float c = Hash21(cell + float2(0, 1));
                float d = Hash21(cell + 1.0);
                return lerp(lerp(a, b, blend.x), lerp(c, d, blend.x), blend.y);
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float rawDepth = SampleSceneDepth(uv);
                float eyeDepth = min(LinearEyeDepth(rawDepth, _ZBufferParams), _ProjectionParams.z);
                float3 positionWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);

                #if UNITY_REVERSED_Z
                    float hasSceneGeometry = step(0.00001, rawDepth);
                #else
                    float hasSceneGeometry = step(rawDepth, 0.99999);
                #endif

                // Underwater refraction is intentionally subtle when no waterline normal is
                // available. A continuous world-space field avoids screen-locked stripes and
                // does not shimmer when resolution changes.
                float2 flowPosition = positionWS.xz * 0.035
                    + float2(_SolWaterWaveTime * 0.035, -_SolWaterWaveTime * 0.026);
                float2 flow = float2(ValueNoise(flowPosition), ValueNoise(flowPosition + 19.37)) - 0.5;
                float distortionConfidence = hasSceneGeometry * saturate(eyeDepth * 0.08);
                float2 distortedUV = clamp(uv + flow * _SolUnderwaterParams.y
                    * 0.0025 * distortionConfidence, 0.001, 0.999);
                float3 source = SAMPLE_TEXTURE2D_X_LOD(
                    _BlitTexture, sampler_LinearClamp, distortedUV, 0).rgb;

                float3 extinctionCoefficient = max(_SolWaterAbsorption,
                    _SolUnderwaterParams.x.xxx);
                float3 transmittance = exp(-extinctionCoefficient * eyeDepth);
                float depthConfidence = 1.0 - dot(transmittance,
                    float3(0.333333, 0.333333, 0.333333));
                float3 inscattering = _SolUnderwaterColor.rgb
                    * (1.0 - transmittance)
                    * (0.72 + 0.28 * saturate(_SolUnderwaterParams.w * 0.25));
                float3 color = source * transmittance + inscattering;
                float composition = _SolUnderwaterParams.z * saturate(0.35 + depthConfidence);
                return half4(lerp(source, color, composition), 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
