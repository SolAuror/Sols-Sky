Shader "Hidden/Sol/Atmosphere"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Assets/Sky-and-Water/Water/Shaders/SolAtmosphere.hlsl"

        float SolRawDepthIsSky(float rawDepth)
        {
            #if UNITY_REVERSED_Z
                return rawDepth <= 0.00001;
            #else
                return rawDepth >= 0.99999;
            #endif
        }

        float SolSafeRawDepth(float rawDepth, float isSky)
        {
            #if UNITY_REVERSED_Z
                return isSky > 0.5 ? 0.00001 : rawDepth;
            #else
                return isSky > 0.5 ? 0.99999 : rawDepth;
            #endif
        }

        void SolReconstructRay(float2 uv, out float3 viewDirection, out float distanceToPoint, out float isSky)
        {
            float rawDepth = SampleSceneDepth(uv);
            isSky = SolRawDepthIsSky(rawDepth);
            float depth = SolSafeRawDepth(rawDepth, isSky);
            float3 positionWS = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
            float3 cameraToPoint = positionWS - _WorldSpaceCameraPos;
            float cameraDistance = length(cameraToPoint);
            distanceToPoint = isSky > 0.5
                ? _SolAtmosphereParams0.z
                : min(cameraDistance, _SolAtmosphereParams0.z);
            viewDirection = cameraDistance > 0.000001
                ? cameraToPoint / cameraDistance
                : float3(0.0, 1.0, 0.0);
        }
        ENDHLSL

        Pass
        {
            Name "Sol Atmosphere Analytic"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragAnalytic

            half4 FragAnalytic(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half4 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                float3 viewDirection;
                float distanceToPoint;
                float isSky;
                SolReconstructRay(uv, viewDirection, distanceToPoint, isSky);
                float3 positionWS = _WorldSpaceCameraPos + viewDirection * distanceToPoint;
                source.rgb = SolApplyAtmosphere(
                    source.rgb,
                    _WorldSpaceCameraPos,
                    positionWS,
                    viewDirection,
                    isSky);
                return source;
            }
            ENDHLSL
        }

        Pass
        {
            Name "Sol Atmosphere Directional Raymarch"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragRaymarch
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float SolInterleavedGradientNoise(float2 pixelPosition)
            {
                return frac(52.9829189 * frac(dot(pixelPosition, float2(0.06711056, 0.00583715))));
            }

            half SolSampleMainDirectionalShadow(float3 positionWS)
            {
                float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN) && !defined(_SURFACE_TYPE_TRANSPARENT)
                    return SampleScreenSpaceShadowmap(shadowCoord);
                #else
                    half4 shadowParams = GetMainLightShadowParams();
                    half attenuation = SAMPLE_TEXTURE2D_SHADOW(
                        _MainLightShadowmapTexture,
                        sampler_LinearClampCompare,
                        shadowCoord.xyz);
                    attenuation = LerpWhiteTo(attenuation, shadowParams.x);
                    return BEYOND_SHADOW_FAR(shadowCoord) ? 1.0h : attenuation;
                #endif
            }

            half4 FragRaymarch(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float3 viewDirection;
                float sceneDistance;
                float isSky;
                SolReconstructRay(uv, viewDirection, sceneDistance, isSky);

                float rayDistance = min(sceneDistance, _SolAtmosphereVolumetricParams.y);
                float startDistance = min(rayDistance, _SolAtmosphereParams0.y);
                float marchDistance = max(0.0, rayDistance - startDistance);
                int stepCount = clamp((int)_SolAtmosphereVolumetricParams.z, 8, 32);
                float stepLength = marchDistance / max(1, stepCount);
                if (marchDistance <= 0.0001)
                    return half4(0.0, 0.0, 0.0, 1.0);

                float jitter = SolInterleavedGradientNoise(uv * _ScaledScreenParams.xy * 0.5);
                float sampleOffset = lerp(0.5, jitter, _SolAtmosphereVolumetricParams.w);
                float3 samplePosition = _WorldSpaceCameraPos
                    + viewDirection * (startDistance + stepLength * sampleOffset);
                float transmittance = 1.0;
                float3 inScattering = 0.0;

                [loop]
                for (int stepIndex = 0; stepIndex < 32; stepIndex++)
                {
                    if (stepIndex >= stepCount)
                        break;

                    float density = SolAtmosphereDensityAt(samplePosition);
                    float segmentTransmittance = exp(-density * stepLength);
                    float shadowAttenuation = 1.0;
                    #if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    if (_SolAtmosphereLightAvailable > 0.5)
                        shadowAttenuation = SolSampleMainDirectionalShadow(samplePosition);
                    #endif

                    float segmentOpacity = 1.0 - segmentTransmittance;
                    float3 sampleLighting = SolAtmosphereLighting(viewDirection, shadowAttenuation);
                    if (_SolAtmosphereParams2.w > 1.5)
                        sampleLighting += SolAtmosphereLocalLighting(samplePosition);
                    inScattering += transmittance * sampleLighting * segmentOpacity;
                    transmittance *= segmentTransmittance;
                    if (transmittance <= 0.01)
                        break;

                    samplePosition += viewDirection * stepLength;
                }

                float opacity = 1.0 - transmittance;
                float targetOpacity = min(opacity, _SolAtmosphereParams0.w);
                if (isSky > 0.5)
                {
                    float opticalDepth = -log(max(transmittance, 0.000001));
                    float skyOpticalDepth = opticalDepth
                        * SolAtmosphereSkyOpticalDepthScale(viewDirection);
                    targetOpacity = min(1.0 - exp(-skyOpticalDepth),
                        _SolAtmosphereParams0.w);
                }

                if (opacity > 0.00001)
                    inScattering *= targetOpacity / opacity;
                else if (targetOpacity > 0.0)
                    inScattering = SolAtmosphereLighting(viewDirection, 1.0) * targetOpacity;
                transmittance = 1.0 - targetOpacity;
                return half4(inScattering, transmittance);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Sol Atmosphere Bilateral Composite"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite

            TEXTURE2D_X(_SolAtmosphereVolumetricTexture);

            float SolLinearDepthAt(float2 uv)
            {
                return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
            }

            void SolAccumulateVolumetricTap(
                float2 uv,
                float weight,
                float centerDepth,
                inout float4 accumulated,
                inout float accumulatedWeight)
            {
                float sampleDepth = SolLinearDepthAt(uv);
                float depthWeight = exp(-abs(sampleDepth - centerDepth)
                    / max(0.01, _SolAtmosphereUpsampleParams.x));
                float finalWeight = weight * depthWeight;
                accumulated += SAMPLE_TEXTURE2D_X(
                    _SolAtmosphereVolumetricTexture, sampler_PointClamp, uv) * finalWeight;
                accumulatedWeight += finalWeight;
            }

            half4 FragComposite(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half4 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                float2 lowSize = max(ceil(_ScaledScreenParams.xy * 0.5), 1.0);
                float2 lowPixel = uv * lowSize - 0.5;
                float2 fraction = frac(lowPixel);
                float2 basePixel = floor(lowPixel) + 0.5;
                float2 texel = rcp(lowSize);
                float centerDepth = SolLinearDepthAt(uv);

                float4 volumetric = 0.0;
                float totalWeight = 0.0;
                SolAccumulateVolumetricTap((basePixel + float2(0, 0)) * texel,
                    (1.0 - fraction.x) * (1.0 - fraction.y), centerDepth, volumetric, totalWeight);
                SolAccumulateVolumetricTap((basePixel + float2(1, 0)) * texel,
                    fraction.x * (1.0 - fraction.y), centerDepth, volumetric, totalWeight);
                SolAccumulateVolumetricTap((basePixel + float2(0, 1)) * texel,
                    (1.0 - fraction.x) * fraction.y, centerDepth, volumetric, totalWeight);
                SolAccumulateVolumetricTap((basePixel + float2(1, 1)) * texel,
                    fraction.x * fraction.y, centerDepth, volumetric, totalWeight);

                if (totalWeight > 0.00001)
                    volumetric /= totalWeight;
                else
                    volumetric = SAMPLE_TEXTURE2D_X(
                        _SolAtmosphereVolumetricTexture, sampler_PointClamp, uv);

                source.rgb = source.rgb * volumetric.a + volumetric.rgb;
                return source;
            }
            ENDHLSL
        }

        Pass
        {
            Name "Sol Atmosphere Half Resolution Spatial Filter"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragSpatialFilter

            float SolFilterDepth(float2 uv)
            {
                return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
            }

            void SolFilterTap(
                float2 uv,
                float baseWeight,
                float centerDepth,
                inout float4 accumulated,
                inout float totalWeight)
            {
                float sampleDepth = SolFilterDepth(uv);
                float depthWeight = exp(-abs(sampleDepth - centerDepth)
                    / max(0.01, _SolAtmosphereUpsampleParams.x));
                float weight = baseWeight * depthWeight;
                accumulated += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv) * weight;
                totalWeight += weight;
            }

            half4 FragSpatialFilter(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float strength = saturate(_SolAtmosphereUpsampleParams.y);
                float4 center = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                if (strength <= 0.001)
                    return center;

                float2 texel = _BlitTexture_TexelSize.xy;
                float centerDepth = SolFilterDepth(uv);
                float4 filtered = center * 2.0;
                float totalWeight = 2.0;
                SolFilterTap(uv + float2(texel.x, 0.0), 1.0, centerDepth, filtered, totalWeight);
                SolFilterTap(uv - float2(texel.x, 0.0), 1.0, centerDepth, filtered, totalWeight);
                SolFilterTap(uv + float2(0.0, texel.y), 1.0, centerDepth, filtered, totalWeight);
                SolFilterTap(uv - float2(0.0, texel.y), 1.0, centerDepth, filtered, totalWeight);
                filtered /= max(totalWeight, 0.00001);
                return lerp(center, filtered, strength);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Sol Atmosphere Temporal Reprojection"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragTemporal

            TEXTURE2D_X(_SolAtmosphereHistoryTexture);
            float4x4 _SolAtmospherePreviousViewProjection;
            float4 _SolAtmosphereTemporalParams; // history weight, clamp expansion, reserved

            half4 FragTemporal(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float4 current = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                if (_SolAtmosphereTemporalParams.x <= 0.0001)
                    return current;

                float rawDepth = SampleSceneDepth(uv);
                float isSky = SolRawDepthIsSky(rawDepth);
                float3 positionWS = ComputeWorldSpacePosition(
                    uv, SolSafeRawDepth(rawDepth, isSky), UNITY_MATRIX_I_VP);
                float4 previousClip = mul(_SolAtmospherePreviousViewProjection, float4(positionWS, 1.0));
                float2 previousUV = previousClip.xy / max(0.0001, previousClip.w) * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                    previousUV.y = 1.0 - previousUV.y;
                #endif
                float2 edge = min(previousUV, 1.0 - previousUV);
                float validity = step(0.0, previousClip.w)
                    * step(0.0, min(edge.x, edge.y));
                if (validity <= 0.0)
                    return current;

                float4 minimumValue = current;
                float4 maximumValue = current;
                float2 texel = _BlitTexture_TexelSize.xy;
                float4 tap0 = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(texel.x, 0));
                float4 tap1 = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - float2(texel.x, 0));
                float4 tap2 = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(0, texel.y));
                float4 tap3 = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - float2(0, texel.y));
                minimumValue = min(minimumValue, min(min(tap0, tap1), min(tap2, tap3)));
                maximumValue = max(maximumValue, max(max(tap0, tap1), max(tap2, tap3)));
                float4 expansion = (maximumValue - minimumValue) * _SolAtmosphereTemporalParams.y;
                float4 history = SAMPLE_TEXTURE2D_X(
                    _SolAtmosphereHistoryTexture, sampler_LinearClamp, previousUV);
                history = clamp(history, minimumValue - expansion, maximumValue + expansion);
                return lerp(current, history, _SolAtmosphereTemporalParams.x * validity);
            }
            ENDHLSL
        }
    }
}
