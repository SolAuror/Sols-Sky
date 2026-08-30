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

        float3 SolWaterNormal(float2 uv)
        {
            return SolDecodeNormalOct(SolWaterData(uv).zw);
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
                float3 positionWS = ComputeWorldSpacePosition(uv, waterData.y, UNITY_MATRIX_I_VP);
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

            // Mirrors SolWaterDebugMode.RawScreenSpaceReflections. Kept in step with the
            // named block at the top of SolOcean.shader and with the enum in
            // SolWaterRendererFeature.cs; see the note there before reordering any of them.
            #define SOL_WATER_DEBUG_RAW_SSR 1

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
                float centerDepth = LinearEyeDepth(centerData.y, _ZBufferParams);
                float sampleDepth = LinearEyeDepth(sampleData.y, _ZBufferParams);
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
                // SolWaterDebugMode.RawScreenSpaceReflections: return the spatially
                // resolved trace before the temporal blend, so the debug view shows what
                // the tracer produced rather than what history is holding on to.
                if ((int)round(_SolWaterSSRResolveParams.y) == SOL_WATER_DEBUG_RAW_SSR)
                    return current;

                float3 positionWS = ComputeWorldSpacePosition(uv, waterData.y, UNITY_MATRIX_I_VP);
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
                return half4(waterData.x, waterData.y, encodedNormal);
            }
            ENDHLSL
        }

        Pass
        {
            // Wind-driven anisotropic reflection filter, adapted from WaterFX.
            //
            // A choppy surface scatters a reflected ray across a spread of angles, and
            // because the surface is horizontal that spread is mostly vertical on screen.
            // Smearing the resolved reflection along the view-vertical axis reproduces
            // that; without it reflections stay mirror-sharp at any wind speed, which
            // reads as unnaturally still water.
            //
            // This sits downstream of the temporal resolve, so it cannot feed back into
            // reflection history.
            Name "AnisotropicReflectionFilter"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment AnisotropicFragment

            // x: smear strength in normalized viewport units, y: distance falloff per
            // metre of eye depth, z: unused, w: enabled
            float4 _SolWaterAnisoParams;
            // The already temporally resolved reflection, not the raw trace. Every tap
            // in this pass reads it -- see the note in the loop below.
            TEXTURE2D_X(_SolWaterAnisoSource);

            half4 AnisotropicFragment(Varyings input) : SV_Target
            {
                float4 center = SAMPLE_TEXTURE2D_X_LOD(
                    _SolWaterAnisoSource, sampler_LinearClamp, input.texcoord, 0);
                if (_SolWaterAnisoParams.w < 0.5 || center.a <= 0.0001)
                    return center;

                float4 waterData = SolWaterData(input.texcoord);
                if (waterData.x <= 0.000001)
                    return center;

                // Distant water occupies fewer pixels per wave, so the same angular
                // spread covers less screen space; shrink the kernel with distance.
                //
                // The prepass stores DEVICE depth in .y, not a distance. Feeding that
                // straight into the falloff made this term read ~0.996 next to the
                // camera and ~1.0 at the horizon under reversed-Z -- inverted, and flat
                // enough at the shipped 0.004 that the kernel never shrank at all.
                float eyeDepth = LinearEyeDepth(waterData.y, _ZBufferParams);
                float distanceFade = saturate(1.0 - eyeDepth * _SolWaterAnisoParams.y);
                float span = _SolWaterAnisoParams.x * distanceFade;
                if (span <= 0.000001)
                    return center;

                float4 accumulated = center;
                float weightSum = 1.0;
                UNITY_UNROLL
                for (int tap = 1; tap <= 4; tap++)
                {
                    float offset = span * tap * 0.25;
                    float weight = 1.0 - tap * 0.2;
                    float2 upUv = input.texcoord + float2(0.0, offset);
                    float2 downUv = input.texcoord - float2(0.0, offset);

                    // Only blend with texels that are the same water body, so a
                    // reflection never bleeds across a shoreline or onto terrain.
                    float4 upData = SolWaterData(upUv);
                    float4 downData = SolWaterData(downUv);
                    // Neighbours come from the same resolved source as the centre tap.
                    // They used to be read from _SolWaterSSRRawTexture, which is the
                    // half-resolution trace before the temporal resolve and is zero on
                    // every ray that missed -- so the smear averaged resolved reflection
                    // against black and darkened the water it was supposed to widen.
                    if (abs(upData.x - waterData.x) < 0.002)
                    {
                        accumulated += SAMPLE_TEXTURE2D_X_LOD(_SolWaterAnisoSource,
                            sampler_LinearClamp, upUv, 0) * weight;
                        weightSum += weight;
                    }
                    if (abs(downData.x - waterData.x) < 0.002)
                    {
                        accumulated += SAMPLE_TEXTURE2D_X_LOD(_SolWaterAnisoSource,
                            sampler_LinearClamp, downUv, 0) * weight;
                        weightSum += weight;
                    }
                }
                return accumulated / max(0.0001, weightSum);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
