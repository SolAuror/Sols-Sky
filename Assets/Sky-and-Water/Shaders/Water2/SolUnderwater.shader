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
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "SolWaterOptics.hlsl"

            float4 _SolUnderwaterColor;
            float4 _SolUnderwaterParams; // density, distortion, transition, camera depth
            float3 _SolWaterAbsorption;
            float _SolWaterWaveTime;
            float4 _SolWaterWorldOrigin;
            float4 _SolWaterWind;
            float4 _SolWaterWeatherExtended;
            float4 _SolWaterFoamParams; // crest, shoreline, caustic strength, caustic scale
            float4 _SolWaterVisibilityParams; // clarity distance, underwater density, horizon reflection, reserved
            TEXTURE2D(_SolWaterCausticTexture);
            SAMPLER(sampler_SolWaterCausticTexture);

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

                #if UNITY_REVERSED_Z
                    float hasSceneGeometry = step(0.00001, rawDepth);
                #else
                    float hasSceneGeometry = step(rawDepth, 0.99999);
                #endif
                // Never reconstruct a far-plane/sky sample directly: inverse-VP
                // precision there can produce infinities which survive a later
                // multiply-by-zero visibility mask on some GPUs.
                float safeRawDepth = lerp(0.5, rawDepth, hasSceneGeometry);
                float3 positionWS = ComputeWorldSpacePosition(
                    uv, safeRawDepth, UNITY_MATRIX_I_VP);

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

                // Project the same logical-world caustic field the surface uses onto
                // visible underwater geometry. The branch has to match SolOcean.shader:
                // if the surface projects live FFT caustics and this projects the
                // authored texture, the pattern changes character exactly at the
                // waterline, which is what sharing SolWaterOptics.hlsl exists to avoid.
                if (_SolWaterFoamParams.z > 0.0001)
                {
                    float surfaceY = GetCameraPositionWS().y + _SolUnderwaterParams.w;
                    float waterColumn = max(0.0, surfaceY - positionWS.y);
                    float2 causticWind = _SolWaterWind.xz;
                    causticWind = dot(causticWind, causticWind) > 0.0001
                        ? normalize(causticWind) : float2(1.0, 0.0);
                    float causticScale = max(0.25, _SolWaterFoamParams.w);
                    float2 logicalXZ = positionWS.xz + _SolWaterWorldOrigin.xz;
                    float2 causticDrift = causticWind * _SolWaterWaveTime * 0.035;
                    float causticPattern;

                    if (_SolWaterCausticArrayParams.w > 0.5)
                    {
                        // Live caustics rendered from this frame's FFT displacement.
                        // No focusing modulation here: the surface derives that from
                        // its own FFT area compression, and the array sample already
                        // carries the convergence it describes.
                        causticPattern = SolWaterSampleCausticArray(
                            logicalXZ, waterColumn, input.positionCS.xy);
                    }
                    else
                    {
                        // Low tier, or caustics disabled: the authored seamless texture,
                        // two rotated incommensurate taps.
                        float2 causticUv0 = logicalXZ / causticScale + causticDrift;
                        float2 rotatedXZ = mul(float2x2(0.819152, -0.573576,
                            0.573576, 0.819152), logicalXZ);
                        float2 causticUv1 = rotatedXZ / (causticScale * 1.731)
                            - causticDrift * 0.63 + float2(0.37, 0.61);
                        float caustic0 = SAMPLE_TEXTURE2D_GRAD(_SolWaterCausticTexture,
                            sampler_SolWaterCausticTexture, causticUv0,
                            ddx(causticUv0), ddy(causticUv0)).r;
                        float caustic1 = SAMPLE_TEXTURE2D_GRAD(_SolWaterCausticTexture,
                            sampler_SolWaterCausticTexture, causticUv1,
                            ddx(causticUv1), ddy(causticUv1)).r;
                        causticPattern = smoothstep(0.12, 0.82,
                            caustic0 * 0.68 + caustic1 * 0.32);
                    }
                    causticPattern = saturate(causticPattern);
                    Light causticLight = GetMainLight(
                        TransformWorldToShadowCoord(positionWS));
                    float cloudShadow = lerp(1.0, 0.45, _SolWaterWeatherExtended.x);
                    float depthFade = smoothstep(0.12, 1.25, waterColumn)
                        * (1.0 - smoothstep(max(2.0, _SolWaterVisibilityParams.x),
                            max(4.0, _SolWaterVisibilityParams.x * 2.5), waterColumn));
                    float visibility = hasSceneGeometry
                        * step(0.02, causticLight.direction.y)
                        * causticLight.shadowAttenuation * cloudShadow * depthFade;
                    float3 causticLighting = causticLight.color * causticPattern
                        * visibility * _SolWaterFoamParams.z;
                    source *= 1.0 + min(causticLighting, 1.5);
                }

                // Same absorption curve and scattering lighting as the surface, so
                // crossing the waterline does not pop. Underwater density stands in for
                // clarity distance: denser water is visible over a shorter path.
                float3 absorptionColor = SolWaterNormalizeAbsorption(_SolWaterAbsorption);
                float clarityDistance = max(0.5,
                    min(_SolWaterVisibilityParams.x,
                        1.0 / max(0.0001, _SolUnderwaterParams.x)));
                float4 absorption = SolWaterComputeAbsorption(clarityDistance,
                    absorptionColor, eyeDepth);
                float3 transmittance = absorption.rgb;
                float depthConfidence = 1.0 - dot(transmittance,
                    float3(0.333333, 0.333333, 0.333333));
                Light volumeLight = GetMainLight();
                float volumeCloudShadow = lerp(1.0, 0.45, _SolWaterWeatherExtended.x);
                // ApplyMaterialState publishes the Sol sky gradient to this material too,
                // so the submerged view lights its scattering from the same sky the
                // surface does.
                float3 inscattering = SolWaterVolumeScattering(
                    _SolUnderwaterColor.rgb,
                    SolWaterDynamicSky(float3(0.0, 1.0, 0.0)),
                    volumeLight.color, volumeLight.direction, volumeCloudShadow,
                    0.72 + 0.28 * saturate(_SolUnderwaterParams.w * 0.25));
                float3 color = lerp(absorption.rgb * source, inscattering, absorption.a);
                float composition = _SolUnderwaterParams.z * saturate(0.35 + depthConfidence);
                return half4(lerp(source, color, composition), 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
