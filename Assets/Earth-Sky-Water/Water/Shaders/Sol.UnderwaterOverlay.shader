Shader "Sol/UnderwaterOverlay"
{
    Properties
    {
        [Header(Color)]
        [HDR] _UnderwaterTint ("Underwater Tint", Color) = (0.04, 0.22, 0.38, 1)
        _TintStrength ("Tint Strength", Range(0, 1)) = 0.55
        _AbsorptionR ("Red Absorption", Range(0, 6)) = 0.95
        _AbsorptionG ("Green Absorption", Range(0, 4)) = 0.35
        _AbsorptionB ("Blue Absorption", Range(0, 2)) = 0.14

        [Header(Fog)]
        [Toggle(_FOG_ON)] _FogEnabled ("Enable Fog", Float) = 1
        [HDR] _FogColorNear ("Fog Color Near", Color) = (0.06, 0.22, 0.36, 1)
        [HDR] _FogColorFar ("Fog Color Far", Color) = (0.02, 0.10, 0.20, 1)
        _FogDensity ("Fog Density", Range(0, 4)) = 0.24
        _FogSurfaceHaze ("Surface Haze", Range(0, 1)) = 0.14
        _FogNearDistance ("Near Clarity Distance", Range(0, 12)) = 1.5
        _FogFarDistance ("Far Fog Distance", Range(2, 120)) = 24
        _FogDepthGain ("Depth Gain", Range(0, 2)) = 0.45

        [Header(Volumetric)]
        [Toggle(_VOLUMETRICFOG_ON)] _VolumetricFog ("Enable Volumetric", Float) = 1
        _VolumeFogDensity ("Volume Density", Range(0, 2)) = 0.35
        _VolumeAnisotropy ("Anisotropy", Range(-0.2, 0.9)) = 0.55
        _VolumeScattering ("Scattering", Range(0, 2)) = 0.7
        [HDR] _VolumeScatteringTint ("Scattering Tint", Color) = (0.16, 0.55, 1.25, 1)
        _VolumeAmbient ("Ambient Fill", Range(0, 1)) = 0.28
        _VolumeEntryDepth ("Entry Softness Depth", Range(0.1, 5)) = 1.4
        _VolumeMaxDistance ("Max Distance", Range(4, 80)) = 18
        _VolumeHorizonFade ("Horizon Fade", Range(0.02, 0.6)) = 0.34

        [Header(Distortion)]
        [Toggle(_DISTORTION_ON)] _DistortionEnabled ("Enable Distortion", Float) = 1
        _DistortionAmount ("Amount", Range(0, 0.03)) = 0.004
        _DistortionSpeed ("Speed", Range(0, 5)) = 0.9
        _DistortionScale ("Scale", Range(0.5, 10)) = 2.2

        [Header(Caustics)]
        [Toggle(_CAUSTICS_ON)] _CausticsEnabled ("Enable Caustics", Float) = 1
        [NoScaleOffset] _CausticsMap ("Caustics Texture", 2D) = "white" {}
        _CausticsTiling ("Tiling", Range(0.5, 20)) = 2.4
        _CausticsSpeed ("Speed", Range(0, 2)) = 0.22
        _CausticsIntensity ("Intensity", Range(0, 4)) = 0.9
        _CausticsDrift ("Drift", Range(0, 0.5)) = 0.05
        _CausticsDepthRange ("Visibility Depth", Range(2, 80)) = 24

        [Header(Debug)]
        [KeywordEnum(Off, Fog, Caustics, Depth)] _DebugMode ("Debug View", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "UnderwaterOverlay"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment UnderwaterFrag

            #pragma shader_feature_local _DISTORTION_ON
            #pragma shader_feature_local _CAUSTICS_ON
            #pragma shader_feature_local _FOG_ON
            #pragma shader_feature_local _VOLUMETRICFOG_ON
            #pragma shader_feature_local _DEBUGMODE_OFF _DEBUGMODE_FOG _DEBUGMODE_CAUSTICS _DEBUGMODE_DEPTH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            half _UnderwaterFactor;
            half _UnderwaterDepth;

            float4 _Sol_SunDirection;
            float4 _Sol_SunColor;
            half _Sol_DayFactor;
            half _Sol_EclipseFactor;

            half4 _UnderwaterTint;
            half _TintStrength;
            half _AbsorptionR;
            half _AbsorptionG;
            half _AbsorptionB;

            half _FogDensity;
            half _FogSurfaceHaze;
            half _FogNearDistance;
            half _FogFarDistance;
            half _FogDepthGain;
            half4 _FogColorNear;
            half4 _FogColorFar;

            half _VolumeFogDensity;
            half _VolumeAnisotropy;
            half _VolumeScattering;
            half4 _VolumeScatteringTint;
            half _VolumeAmbient;
            half _VolumeEntryDepth;
            half _VolumeMaxDistance;
            half _VolumeHorizonFade;

            half _DistortionAmount;
            half _DistortionSpeed;
            half _DistortionScale;

            half _CausticsTiling;
            half _CausticsSpeed;
            half _CausticsIntensity;
            half _CausticsDrift;
            half _CausticsDepthRange;

            TEXTURE2D(_CausticsMap);
            SAMPLER(sampler_CausticsMap);

            void SampleSceneData(half2 uv, out half eyeDepth, out float3 worldPos, out half hasScene)
            {
                eyeDepth = 0.0h;
                worldPos = float3(0.0, 0.0, 0.0);
                hasScene = 0.0h;

                real raw = SampleSceneDepth(uv);
                #if UNITY_REVERSED_Z
                    if (raw <= 0.0001) return;
                #else
                    raw = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, raw);
                    if (raw >= 0.9999) return;
                #endif

                eyeDepth = (half)LinearEyeDepth(raw, _ZBufferParams);
                worldPos = ComputeWorldSpacePosition(uv, raw, UNITY_MATRIX_I_VP);
                hasScene = 1.0h;
            }

            half2 ComputeDistortion(half2 uv, half factor, half depth)
            {
                half depthFade = 1.0h - saturate(depth / 1.5h);
                half2 centered = uv - 0.5h;
                half angleFade = 1.0h - saturate(dot(centered, centered) * 2.0h);

                half wt = _Time.y * _DistortionSpeed;
                half2 d = half2(
                    sin(uv.y * _DistortionScale * TWO_PI + wt) * _DistortionAmount,
                    cos(uv.x * _DistortionScale * TWO_PI + wt * 0.7h) * _DistortionAmount * 0.5h
                );

                return d * factor * depthFade * angleFade;
            }

            half3 ApplyAbsorption(half3 col, half factor, half mediumDepth)
            {
                half3 absorption = half3(_AbsorptionR, _AbsorptionG, _AbsorptionB);
                half3 attenuated = col * exp(-absorption * mediumDepth);
                return lerp(col, attenuated, factor * _TintStrength);
            }

            half3 ApplyFog(half3 col, half factor, half mediumDepth, half sceneDepth)
            {
                half surfaceHaze = _FogSurfaceHaze * factor;
                half depthFog = 1.0h - exp(-max(0.0h, mediumDepth) * _FogDensity);
                half distFog = smoothstep(_FogNearDistance, max(_FogFarDistance, _FogNearDistance + 0.01h), sceneDepth);
                half fogColorT = saturate(sceneDepth / max(_FogFarDistance, 0.01h));
                half3 fogColor = lerp(_FogColorNear.rgb, _FogColorFar.rgb, fogColorT);
                half day = saturate(_Sol_DayFactor);
                half eclipse = 1.0h - _Sol_EclipseFactor * 0.7h;
                half3 todFogTint = lerp(half3(0.70h, 0.82h, 1.0h), _Sol_SunColor.rgb * 0.65h + 0.35h, day);
                fogColor *= todFogTint * eclipse;

                half fogT = saturate(surfaceHaze + depthFog * distFog * factor);
                return lerp(col, fogColor, fogT);
            }

            half PhaseHG(half cosTheta, half g)
            {
                half g2 = g * g;
                half denom = max(0.001h, 1.0h + g2 - 2.0h * g * cosTheta);
                return (1.0h - g2) / (4.0h * PI * denom * sqrt(denom));
            }

            half3 ApplyVolumetricFog(half3 col, half2 uv, half factor, half cameraWaterDepth, half sceneDepth, float3 sceneWorldPos, half hasScene)
            {
                float3 camPos = _WorldSpaceCameraPos;
                // Build a deterministic fallback ray first so every backend sees initialized values.
                real farRaw = UNITY_REVERSED_Z ? 0.0 : 1.0;
                #if !UNITY_REVERSED_Z
                    farRaw = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, farRaw);
                #endif
                float3 farPos = ComputeWorldSpacePosition(uv, farRaw, UNITY_MATRIX_I_VP);
                float3 fallbackRay = farPos - camPos;
                half fallbackLen = (half)length(fallbackRay);
                float3 rayDir = fallbackLen > 0.001h
                    ? fallbackRay / fallbackLen
                    : float3(0.0, 1.0, 0.0);
                half rayLen = max(_FogFarDistance, max(fallbackLen, 8.0h));

                if (hasScene > 0.5h && sceneDepth > 0.001h)
                {
                    float3 toScene = sceneWorldPos - camPos;
                    rayLen = (half)length(toScene);
                    if (rayLen <= 0.001h) return col;
                    rayDir = toScene / rayLen;
                }
                rayLen = min(rayLen, max(_VolumeMaxDistance, 0.01h));

                const int kSteps = 8;
                half stepLen = rayLen / (half)kSteps;
                half surfaceY = (half)camPos.y + cameraWaterDepth;

                float3 sunDirWs = normalize(-_Sol_SunDirection.xyz);
                if (dot(sunDirWs, sunDirWs) < 0.01) sunDirWs = float3(0.0, 1.0, 0.0);
                half3 sunCol = max((half3)_Sol_SunColor.rgb, half3(0.05h, 0.05h, 0.05h));
                // Prevent HDR sun color from producing a fake underwater sun disk.
                sunCol = min(sunCol, half3(1.2h, 1.2h, 1.2h));
                half day = saturate(_Sol_DayFactor);
                half eclipse = 1.0h - _Sol_EclipseFactor * 0.7h;
                sunCol *= lerp(0.55h, 1.0h, day) * eclipse;
                half3 scatterTint = max(_VolumeScatteringTint.rgb, half3(0.0h, 0.0h, 0.0h));

                half transmittance = 1.0h;
                half3 inscatter = (half3)0.0h;
                half cosTheta = saturate(dot((half3)rayDir, (half3)sunDirWs));
                // Artist-safe anisotropy: clamp lobe strength and blend toward isotropic.
                half gSafe = min(_VolumeAnisotropy, 0.68h);
                half phaseHg = PhaseHG(min(cosTheta, 0.95h), gSafe);
                half phaseIso = 1.0h / (4.0h * PI);
                half anisotropyMix = saturate(_VolumeAnisotropy / 0.68h);
                half phase = lerp(phaseIso, phaseHg, anisotropyMix);
                // Extra guard against concentrated hotspot.
                phase = min(phase, 0.16h);
                // Reduce horizon-angle blowout where shallow rays otherwise read as a flat wall.
                half horizonSuppress = smoothstep(_VolumeHorizonFade * 0.35h, _VolumeHorizonFade, abs((half)rayDir.y));
                half surfaceFade = smoothstep(0.03h, max(_VolumeEntryDepth, 0.04h), cameraWaterDepth);

                [unroll]
                for (int i = 0; i < kSteps; i++)
                {
                    half t = ((half)i + 0.5h) * stepLen;
                    float3 samplePos = camPos + rayDir * t;

                    half belowSurface = max(0.0h, surfaceY - (half)samplePos.y);
                    half depthRamp = saturate(belowSurface / max(_VolumeEntryDepth, 0.01h));
                    half localDensity = _VolumeFogDensity * depthRamp * (1.0h - exp(-belowSurface * _FogDepthGain));
                    half sigma = localDensity * stepLen;
                    half att = exp(-sigma);

                    inscatter += transmittance * localDensity * phase * horizonSuppress * surfaceFade * _VolumeScattering * sunCol * scatterTint * stepLen;
                    transmittance *= att;
                }

                // Keep the entry transition readable without forcing high forward scattering.
                half entryLift = 1.0h - saturate(cameraWaterDepth / max(_VolumeEntryDepth, 0.01h));
                half minTrans = lerp(0.08h, 0.55h, entryLift);
                transmittance = max(transmittance, minTrans);

                half volumeColorT = saturate(rayLen / max(_FogFarDistance, 0.01h));
                half3 fogBaseColor = lerp(_FogColorNear.rgb, _FogColorFar.rgb, volumeColorT);
                half3 ambientInscatter = fogBaseColor * lerp(half3(1.0h, 1.0h, 1.0h), scatterTint, 0.35h) * (_VolumeAmbient + entryLift * 0.12h) * surfaceFade;
                half3 fogColor = ambientInscatter + inscatter;
                return col * transmittance + fogColor * (1.0h - transmittance) * factor;
            }

            half3 ApplyCaustics(half3 col, float2 worldXZ, half factor, half cameraWaterDepth)
            {
                half fade = 1.0h - saturate(cameraWaterDepth / max(_CausticsDepthRange, 0.01h));
                half vis = factor * fade;

                half2 drift = half2(_Time.y * _CausticsDrift, _Time.y * _CausticsDrift * 0.8h);
                half2 cauUV = (half2)worldXZ * _CausticsTiling + drift;

                half2 uv1 = cauUV + _Time.y * _CausticsSpeed * half2(1.0h, 0.7h);
                half2 uv2 = cauUV + _Time.y * _CausticsSpeed * half2(-0.6h, -1.0h);

                half c1 = SAMPLE_TEXTURE2D(_CausticsMap, sampler_CausticsMap, uv1).r;
                half c2 = SAMPLE_TEXTURE2D(_CausticsMap, sampler_CausticsMap, uv2).r;
                half c = saturate(lerp(min(c1, c2), max(c1, c2), 0.6h) * 1.35h);

                return col + c * _CausticsIntensity * vis;
            }

            half4 UnderwaterFrag(Varyings input) : SV_Target
            {
                half factor = _UnderwaterFactor;
                half cameraWaterDepth = _UnderwaterDepth;

                UNITY_BRANCH
                if (factor < 0.002h)
                    return SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord);

                half2 uv = input.texcoord;

                half sceneDepth;
                float3 sceneWorldPos;
                half hasScene;
                SampleSceneData(uv, sceneDepth, sceneWorldPos, hasScene);

                // Sky pixels have no scene depth; keep upward rays clearer so the sky stays visible
                // through the surface when looking up from underwater.
                real farRaw = UNITY_REVERSED_Z ? 0.0 : 1.0;
                #if !UNITY_REVERSED_Z
                    farRaw = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, farRaw);
                #endif
                float3 farPos = ComputeWorldSpacePosition(uv, farRaw, UNITY_MATRIX_I_VP);
                float3 viewDirWs = normalize(farPos - _WorldSpaceCameraPos);
                half skyPixel = 1.0h - hasScene;
                half upwardView = saturate((half)viewDirWs.y);
                half skyClarity = skyPixel * upwardView;

                half2 distort = half2(0.0h, 0.0h);
                #if defined(_DISTORTION_ON)
                    distort = ComputeDistortion(uv, factor, cameraWaterDepth);
                #endif

                half3 sceneRGB = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, saturate(uv + distort)).rgb;
                half sceneA = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv).a;

                half sceneDepthNorm = saturate(sceneDepth / max(_FogFarDistance, 0.01h));
                half mediumDepth = cameraWaterDepth + sceneDepthNorm * _FogDepthGain;
                mediumDepth = lerp(mediumDepth, cameraWaterDepth * 0.08h, skyClarity);
                half overlayFactor = factor * (1.0h - 0.75h * skyClarity);

                #if defined(_DEBUGMODE_DEPTH)
                    return half4((half3)saturate(mediumDepth / 10.0h), 1.0h);
                #endif

                half3 col = ApplyAbsorption(sceneRGB, overlayFactor, mediumDepth);

                #if defined(_FOG_ON)
                    col = ApplyFog(col, overlayFactor, mediumDepth, sceneDepth);
                #endif

                #if defined(_VOLUMETRICFOG_ON)
                    col = ApplyVolumetricFog(col, uv, overlayFactor, cameraWaterDepth, sceneDepth, sceneWorldPos, hasScene);
                #endif

                #if defined(_DEBUGMODE_FOG)
                    half fogDbg = 1.0h - exp(-mediumDepth * _FogDensity);
                    return half4((half3)fogDbg, 1.0h);
                #endif

                #if defined(_CAUSTICS_ON)
                    if (hasScene > 0.5h)
                        col = ApplyCaustics(col, sceneWorldPos.xz, factor, cameraWaterDepth);
                #endif

                #if defined(_DEBUGMODE_CAUSTICS)
                    if (hasScene < 0.5h) return half4(0.0h, 0.0h, 0.0h, 1.0h);
                    half2 drift = half2(_Time.y * _CausticsDrift, _Time.y * _CausticsDrift * 0.8h);
                    half2 cuv = (half2)sceneWorldPos.xz * _CausticsTiling + drift;
                    half c = SAMPLE_TEXTURE2D(_CausticsMap, sampler_CausticsMap, cuv).r;
                    return half4((half3)c, 1.0h);
                #endif

                return half4(col, sceneA);
            }
            ENDHLSL
        }
    }
}
