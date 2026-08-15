Shader "Hidden/Sol/Water2/Resolve"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        TEXTURE2D_X(_SolWaterSceneColor);
        TEXTURE2D_X(_SolWaterPrepassData);
        TEXTURE2D_X(_SolWaterPrepassNormal);
        TEXTURE2D_X(_SolWaterSSRRawTexture);
        TEXTURE2D_X(_SolWaterSSRHistoryTexture);
        TEXTURE2D_X(_SolWaterSSRValidationHistoryTexture);

        float4 _SolWaterSSRTraceParams; // steps, maximum distance, thickness, edge fade
        float4 _SolWaterSSRTemporalParams; // binary steps, history weight, depth tolerance, normal tolerance
        float4 _SolWaterSSRResolveParams; // maximum luminance, debug mode, raw texel x, raw texel y
        float4x4 _SolWaterSSRPreviousViewProjection;

        bool SolHasSceneDepth(float rawDepth)
        {
        #if UNITY_REVERSED_Z
            return rawDepth > 0.00001;
        #else
            return rawDepth < 0.99999;
        #endif
        }

        float2 SolClipToUv(float4 clip)
        {
            float2 uv = clip.xy / max(0.00001, clip.w) * 0.5 + 0.5;
        #if UNITY_UV_STARTS_AT_TOP
            uv.y = 1.0 - uv.y;
        #endif
            return uv;
        }

        bool SolUvInside(float2 uv)
        {
            return all(uv > 0.001) && all(uv < 0.999);
        }

        float4 SolWaterData(float2 uv)
        {
            return SAMPLE_TEXTURE2D_X_LOD(_SolWaterPrepassData, sampler_PointClamp, uv, 0);
        }

        float3 SolWaterNormal(float2 uv)
        {
            return normalize(SAMPLE_TEXTURE2D_X_LOD(
                _SolWaterPrepassNormal, sampler_LinearClamp, uv, 0).xyz * 2.0 - 1.0);
        }

        float2 SolEncodeNormalOct(float3 normal)
        {
            normal /= max(0.00001, abs(normal.x) + abs(normal.y) + abs(normal.z));
            float2 encoded = normal.xz;
            if (normal.y < 0.0)
            {
                float2 signValue = float2(encoded.x >= 0.0 ? 1.0 : -1.0,
                    encoded.y >= 0.0 ? 1.0 : -1.0);
                encoded = (1.0 - abs(encoded.yx)) * signValue;
            }
            return encoded * 0.5 + 0.5;
        }

        float3 SolDecodeNormalOct(float2 encoded)
        {
            float2 value = encoded * 2.0 - 1.0;
            float3 normal = float3(value.x, 1.0 - abs(value.x) - abs(value.y), value.y);
            if (normal.y < 0.0)
            {
                float2 signValue = float2(normal.x >= 0.0 ? 1.0 : -1.0,
                    normal.z >= 0.0 ? 1.0 : -1.0);
                normal.xz = (1.0 - abs(normal.zx)) * signValue;
            }
            return normalize(normal);
        }

        float3 SolClampLuminance(float3 color, float maximumLuminance)
        {
            color = max(color, 0.0);
            float luminance = dot(color, float3(0.2126, 0.7152, 0.0722));
            return color * min(1.0, max(0.1, maximumLuminance) / max(0.0001, luminance));
        }
        ENDHLSL

        Pass
        {
            Name "RawScreenSpaceReflections"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment RawReflectionFragment

            half4 RawReflectionFragment(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float4 waterData = SolWaterData(uv);
                if (waterData.x <= 0.000001)
                    return 0;

                float3 normalWS = SolWaterNormal(uv);
                float3 positionWS = ComputeWorldSpacePosition(uv, waterData.z, UNITY_MATRIX_I_VP);
                float3 cameraRay = SafeNormalize(positionWS - GetCameraPositionWS());
                float3 reflectionRay = SafeNormalize(reflect(cameraRay, normalWS));
                // A water reflection ray belongs to the upper hemisphere. Downward rays
                // would intersect only submerged geometry and are handled by the sky fallback.
                if (reflectionRay.y <= 0.002)
                    return 0;

                float maximumDistance = max(1.0, _SolWaterSSRTraceParams.y);
                float3 rayStart = positionWS + normalWS * 0.04 + reflectionRay * 0.06;
                float3 rayEnd = rayStart + reflectionRay * maximumDistance;
                float4 startClip = TransformWorldToHClip(rayStart);
                float4 endClip = TransformWorldToHClip(rayEnd);
                if (startClip.w <= 0.0001 || endClip.w <= 0.0001)
                    return 0;

                float2 startUv = SolClipToUv(startClip);
                float2 endUv = SolClipToUv(endClip);
                float screenSpan = length((endUv - startUv) * _ScreenParams.xy);
                int maximumSteps = clamp((int)_SolWaterSSRTraceParams.x, 8, 64);
                int stepCount = clamp((int)ceil(screenSpan * 0.5), 8, maximumSteps);
                float previousT = 0.0;
                float previousDelta = -1e20;
                float hitLow = 0.0;
                float hitHigh = 0.0;
                bool foundHit = false;

                [loop]
                for (int i = 0; i < 64; i++)
                {
                    if (i >= stepCount)
                        break;
                    float t = (i + 1.0) / stepCount;
                    float3 rayPosition = lerp(rayStart, rayEnd, t);
                    float4 rayClip = TransformWorldToHClip(rayPosition);
                    if (rayClip.w <= 0.0001)
                        break;
                    float2 sampleUv = SolClipToUv(rayClip);
                    if (!SolUvInside(sampleUv))
                        break;

                    // A non-zero water prepass at the candidate means the opaque depth is
                    // behind visible water: sky, seabed, or another water self-hit.
                    if (SolWaterData(sampleUv).x > 0.000001)
                    {
                        previousT = t;
                        previousDelta = -1e20;
                        continue;
                    }

                    float rawSceneDepth = SampleSceneDepth(sampleUv);
                    if (!SolHasSceneDepth(rawSceneDepth))
                    {
                        previousT = t;
                        previousDelta = -1e20;
                        continue;
                    }

                    float3 scenePosition = ComputeWorldSpacePosition(
                        sampleUv, rawSceneDepth, UNITY_MATRIX_I_VP);
                    if (scenePosition.y <= positionWS.y + 0.03)
                    {
                        previousT = t;
                        previousDelta = -1e20;
                        continue;
                    }

                    float sceneEyeDepth = LinearEyeDepth(rawSceneDepth, _ZBufferParams);
                    float rayEyeDepth = -TransformWorldToView(rayPosition).z;
                    float delta = rayEyeDepth - sceneEyeDepth;
                    float thickness = _SolWaterSSRTraceParams.z * (1.0 + sceneEyeDepth * 0.002);
                    if (delta >= 0.0 && (delta <= thickness || previousDelta < 0.0))
                    {
                        hitLow = previousT;
                        hitHigh = t;
                        foundHit = true;
                        break;
                    }
                    previousT = t;
                    previousDelta = delta;
                }

                if (!foundHit)
                    return 0;

                int binarySteps = clamp((int)_SolWaterSSRTemporalParams.x, 0, 8);
                [loop]
                for (int iteration = 0; iteration < 8; iteration++)
                {
                    if (iteration >= binarySteps)
                        break;
                    float middle = (hitLow + hitHigh) * 0.5;
                    float3 rayPosition = lerp(rayStart, rayEnd, middle);
                    float2 sampleUv = SolClipToUv(TransformWorldToHClip(rayPosition));
                    if (!SolUvInside(sampleUv) || SolWaterData(sampleUv).x > 0.000001)
                    {
                        hitLow = middle;
                        continue;
                    }
                    float rawDepth = SampleSceneDepth(sampleUv);
                    if (!SolHasSceneDepth(rawDepth))
                    {
                        hitLow = middle;
                        continue;
                    }
                    float rayEyeDepth = -TransformWorldToView(rayPosition).z;
                    float delta = rayEyeDepth - LinearEyeDepth(rawDepth, _ZBufferParams);
                    if (delta >= 0.0)
                        hitHigh = middle;
                    else
                        hitLow = middle;
                }

                float hitT = hitHigh;
                float3 hitRayPosition = lerp(rayStart, rayEnd, hitT);
                float2 hitUv = SolClipToUv(TransformWorldToHClip(hitRayPosition));
                if (!SolUvInside(hitUv) || SolWaterData(hitUv).x > 0.000001)
                    return 0;
                float hitDepth = SampleSceneDepth(hitUv);
                if (!SolHasSceneDepth(hitDepth))
                    return 0;
                float3 hitPosition = ComputeWorldSpacePosition(hitUv, hitDepth, UNITY_MATRIX_I_VP);
                if (hitPosition.y <= positionWS.y + 0.03)
                    return 0;

                float2 texel = 1.0 / _ScreenParams.xy;
                float depthRight = SampleSceneDepth(saturate(hitUv + float2(texel.x, 0.0)));
                float depthUp = SampleSceneDepth(saturate(hitUv + float2(0.0, texel.y)));
                if (!SolHasSceneDepth(depthRight) || !SolHasSceneDepth(depthUp))
                    return 0;
                float3 positionRight = ComputeWorldSpacePosition(
                    hitUv + float2(texel.x, 0.0), depthRight, UNITY_MATRIX_I_VP);
                float3 positionUp = ComputeWorldSpacePosition(
                    hitUv + float2(0.0, texel.y), depthUp, UNITY_MATRIX_I_VP);
                float3 sceneNormal = SafeNormalize(cross(positionUp - hitPosition, positionRight - hitPosition));
                float3 toCamera = SafeNormalize(GetCameraPositionWS() - hitPosition);
                if (dot(sceneNormal, toCamera) < 0.0)
                    sceneNormal = -sceneNormal;
                float facingConfidence = saturate(dot(sceneNormal, -reflectionRay) * 4.0);

                float sceneEye = LinearEyeDepth(hitDepth, _ZBufferParams);
                float neighborRange = max(
                    abs(LinearEyeDepth(depthRight, _ZBufferParams) - sceneEye),
                    abs(LinearEyeDepth(depthUp, _ZBufferParams) - sceneEye));
                float allowedRange = _SolWaterSSRTemporalParams.z * (1.0 + sceneEye * 0.002);
                if (neighborRange > allowedRange * 4.0)
                    return 0;
                float depthConfidence = saturate(allowedRange / max(allowedRange, neighborRange));
                float2 edge = min(hitUv, 1.0 - hitUv);
                float edgeConfidence = saturate(min(edge.x, edge.y)
                    / max(0.001, _SolWaterSSRTraceParams.w));
                float confidence = edgeConfidence * facingConfidence * depthConfidence
                    * (1.0 - hitT) * saturate(reflectionRay.y * 12.0);
                if (confidence <= 0.001)
                    return 0;

                float3 color = SAMPLE_TEXTURE2D_X_LOD(
                    _SolWaterSceneColor, sampler_LinearClamp, hitUv, 0).rgb;
                color = SolClampLuminance(color, _SolWaterSSRResolveParams.x);
                return half4(color, confidence);
            }
            ENDHLSL
        }

        Pass
        {
            Name "TemporalBilateralResolve"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment TemporalResolveFragment

            void AccumulateRaw(
                float2 sampleUv,
                float4 centerData,
                float3 centerNormal,
                inout float4 total,
                inout float weightTotal,
                inout float3 neighborhoodMin,
                inout float3 neighborhoodMax)
            {
                if (!SolUvInside(sampleUv))
                    return;
                float4 sampleData = SolWaterData(sampleUv);
                float3 sampleNormal = SolWaterNormal(sampleUv);
                float bodyWeight = 1.0 - step(0.00001, abs(sampleData.x - centerData.x));
                float centerDepth = LinearEyeDepth(centerData.z, _ZBufferParams);
                float sampleDepth = LinearEyeDepth(sampleData.z, _ZBufferParams);
                float depthWeight = saturate(1.0 - abs(sampleDepth - centerDepth)
                    / max(0.01, _SolWaterSSRTemporalParams.z));
                float normalWeight = saturate((dot(centerNormal, sampleNormal)
                    - _SolWaterSSRTemporalParams.w) / max(0.0001, 1.0 - _SolWaterSSRTemporalParams.w));
                float weight = bodyWeight * depthWeight * normalWeight;
                float4 value = SAMPLE_TEXTURE2D_X_LOD(
                    _SolWaterSSRRawTexture, sampler_LinearClamp, sampleUv, 0);
                total += value * weight;
                weightTotal += weight;
                neighborhoodMin = min(neighborhoodMin, value.rgb);
                neighborhoodMax = max(neighborhoodMax, value.rgb);
            }

            half4 TemporalResolveFragment(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float4 waterData = SolWaterData(uv);
                if (waterData.x <= 0.000001)
                    return 0;
                float3 normalWS = SolWaterNormal(uv);
                float2 rawTexel = max(_SolWaterSSRResolveParams.zw, 0.000001);
                float4 total = 0;
                float weightTotal = 0;
                float3 neighborhoodMin = 1e20;
                float3 neighborhoodMax = -1e20;
                AccumulateRaw(uv, waterData, normalWS, total, weightTotal,
                    neighborhoodMin, neighborhoodMax);
                AccumulateRaw(uv + float2(rawTexel.x, 0), waterData, normalWS, total, weightTotal,
                    neighborhoodMin, neighborhoodMax);
                AccumulateRaw(uv - float2(rawTexel.x, 0), waterData, normalWS, total, weightTotal,
                    neighborhoodMin, neighborhoodMax);
                AccumulateRaw(uv + float2(0, rawTexel.y), waterData, normalWS, total, weightTotal,
                    neighborhoodMin, neighborhoodMax);
                AccumulateRaw(uv - float2(0, rawTexel.y), waterData, normalWS, total, weightTotal,
                    neighborhoodMin, neighborhoodMax);
                float4 current = weightTotal > 0.0001 ? total / weightTotal : 0;
                if ((int)round(_SolWaterSSRResolveParams.y) == 1)
                    return current;

                float3 positionWS = ComputeWorldSpacePosition(uv, waterData.z, UNITY_MATRIX_I_VP);
                float4 previousClip = mul(_SolWaterSSRPreviousViewProjection, float4(positionWS, 1.0));
                if (previousClip.w <= 0.0001 || current.a <= 0.001)
                    return current;
                float2 previousUv = SolClipToUv(previousClip);
                if (!SolUvInside(previousUv))
                    return current;

                float4 validation = SAMPLE_TEXTURE2D_X_LOD(
                    _SolWaterSSRValidationHistoryTexture, sampler_PointClamp, previousUv, 0);
                float bodyValid = 1.0 - step(0.00001, abs(validation.x - waterData.x));
                float expectedDepth = previousClip.z / previousClip.w;
                float expectedEye = LinearEyeDepth(expectedDepth, _ZBufferParams);
                float historyEye = LinearEyeDepth(validation.y, _ZBufferParams);
                float depthValid = saturate(1.0 - abs(historyEye - expectedEye)
                    / max(0.01, _SolWaterSSRTemporalParams.z));
                float3 historyNormal = SolDecodeNormalOct(validation.zw);
                float normalValid = step(_SolWaterSSRTemporalParams.w,
                    dot(normalWS, historyNormal));
                float4 history = SAMPLE_TEXTURE2D_X_LOD(
                    _SolWaterSSRHistoryTexture, sampler_LinearClamp, previousUv, 0);
                history.rgb = clamp(history.rgb, neighborhoodMin, neighborhoodMax);
                float historyWeight = _SolWaterSSRTemporalParams.y
                    * bodyValid * depthValid * normalValid * saturate(current.a * 4.0);
                return lerp(current, history, historyWeight);
            }
            ENDHLSL
        }

        Pass
        {
            Name "StoreValidationHistory"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment ValidationFragment

            half4 ValidationFragment(Varyings input) : SV_Target
            {
                float4 waterData = SolWaterData(input.texcoord);
                if (waterData.x <= 0.000001)
                    return 0;
                float2 encodedNormal = SolEncodeNormalOct(SolWaterNormal(input.texcoord));
                return half4(waterData.x, waterData.z, encodedNormal);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
