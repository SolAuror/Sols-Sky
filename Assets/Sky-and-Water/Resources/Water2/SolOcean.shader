Shader "Sol/Water2/Ocean"
{
    // Shared water globals are declared by SolWaterWaves2.hlsl.
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent-10" "RenderType"="Transparent" }

        HLSLINCLUDE
        #pragma target 4.5
        #pragma multi_compile_instancing
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "SolWaterWaves2.hlsl"
        #include "../../Water/Shaders/SolAtmosphere.hlsl"

        float _SolWaterBodyHash;
        float4 _SolWaterShallowColor;
        float4 _SolWaterDeepColor;
        float3 _SolWaterAbsorption;
        float4 _SolWaterFoamColor;
        TEXTURE2D(_SolWaterFoamTexture);
        SAMPLER(sampler_SolWaterFoamTexture);
        float4 _SolWaterFoamDetail; // world scale, contrast, brightness, texture valid
        float4 _SolWaterReflectionParams; // dynamic sky blend, reserved
        float4 _SolWaterReflectionSkyColor;
        float4 _SolWaterReflectionEquatorColor;
        float4 _SolWaterReflectionGroundColor;
        float4 _SolWaterReflectionWarmColor;
        float4 _SolWaterReflectionSkyParams; // zenith blend, nadir blend, horizon power, warmth falloff
        float4 _SolWaterReflectionSunDirection; // xyz direction, w Sol gradient valid
        TEXTURE2D_X(_SolWaterSceneColor);
        SAMPLER(sampler_SolWaterSceneColor);
        TEXTURE2D_X(_SolWaterSSRTexture);
        SAMPLER(sampler_SolWaterSSRTexture);
        TEXTURE2D_X(_SolWaterSSRRawTexture);
        SAMPLER(sampler_SolWaterSSRRawTexture);
        TEXTURE2D(_SolWaterPlanarReflectionTexture);
        SAMPLER(sampler_SolWaterPlanarReflectionTexture);
        float4x4 _SolWaterPlanarViewProjection;
        float4 _SolWaterPlanarParams; // valid, body hash, temporal confidence, reserved

        float3 SolWaterDynamicSky(float3 directionWS)
        {
            directionWS = SafeNormalize(directionWS);
            float y = directionWS.y;
            float zenithMask = smoothstep(0.0, max(0.0001, _SolWaterReflectionSkyParams.x), y);
            float nadirMask = smoothstep(0.0, max(0.0001, _SolWaterReflectionSkyParams.y), -y);
            float horizonMask = pow(max(1.0 - zenithMask - nadirMask, 0.0),
                max(0.0001, _SolWaterReflectionSkyParams.z));

            float2 directionAzimuth = directionWS.xz;
            float2 sunAzimuth = _SolWaterReflectionSunDirection.xz;
            float azimuthNorm = max(length(directionAzimuth) * length(sunAzimuth), 0.0001);
            float alignment = saturate(dot(directionAzimuth, sunAzimuth) / azimuthNorm * 0.5 + 0.5);
            float warmth = pow(alignment, max(0.5, _SolWaterReflectionSkyParams.w))
                * _SolWaterReflectionWarmColor.a * _SolWaterReflectionSunDirection.w;
            float3 horizon = lerp(_SolWaterReflectionEquatorColor.rgb,
                _SolWaterReflectionWarmColor.rgb, warmth);
            return max(_SolWaterReflectionSkyColor.rgb * zenithMask
                + horizon * horizonMask
                + _SolWaterReflectionGroundColor.rgb * nadirMask, 0.0);
        }

        float SolWaterFoamTextureMask(float2 positionXZ, float confidence)
        {
            confidence = saturate(confidence);
            if (confidence <= 0.0001 || _SolWaterFoamDetail.w < 0.5)
                return confidence;

            float2 logicalXZ = positionXZ + _SolWaterWorldOrigin.xz;
            float2 wind = _SolWaterWind.xz;
            wind = dot(wind, wind) > 0.0001 ? normalize(wind) : float2(1.0, 0.0);
            float2 crossWind = float2(-wind.y, wind.x);
            float scale = max(0.0001, _SolWaterFoamDetail.x);
            float2 drift = (wind * 0.011 + crossWind * 0.0025) * _SolWaterWaveTime;
            float2 warpUv = logicalXZ * (scale * 0.173) - drift * 0.31 + float2(11.7, 5.3);
            float2 warp = float2(
                SAMPLE_TEXTURE2D(_SolWaterFoamTexture, sampler_SolWaterFoamTexture, warpUv).r,
                SAMPLE_TEXTURE2D(_SolWaterFoamTexture, sampler_SolWaterFoamTexture,
                    warpUv.yx * 1.37 + float2(37.1, 19.4)).r) - 0.5;
            float2 warpedXZ = logicalXZ + warp * (2.8 / scale);
            float2 uv0 = warpedXZ * scale + drift;
            float2 rotatedXZ = float2(warpedXZ.y, -warpedXZ.x);
            float2 uv1 = rotatedXZ * (scale * 2.371) - drift * 0.61 + float2(17.13, 31.71);
            float2 uv2 = mul(float2x2(0.8192, -0.5736, 0.5736, 0.8192), warpedXZ)
                * (scale * 0.617) + drift * 0.23 + float2(53.9, 7.6);
            float noise0 = SAMPLE_TEXTURE2D(_SolWaterFoamTexture,
                sampler_SolWaterFoamTexture, uv0).r;
            float noise1 = SAMPLE_TEXTURE2D(_SolWaterFoamTexture,
                sampler_SolWaterFoamTexture, uv1).r;
            float noise2 = SAMPLE_TEXTURE2D(_SolWaterFoamTexture,
                sampler_SolWaterFoamTexture, uv2).r;
            float contrast = max(0.1, _SolWaterFoamDetail.y);
            float detail = saturate(((noise0 * 0.52 + noise1 * 0.29 + noise2 * 0.19) - 0.34)
                * contrast + 0.5);
            float footprint = max(length(ddx(uv1)), length(ddy(uv1)));
            detail = lerp(detail, 0.5, saturate((footprint - 0.35) * 1.5));
            detail = pow(detail, max(0.1, contrast * 0.65));
            return saturate(detail - (1.0 - sqrt(confidence)));
        }

        UNITY_INSTANCING_BUFFER_START(SolOceanPatch)
            UNITY_DEFINE_INSTANCED_PROP(float4, _SolOceanPatchData) // tile size, resolution, edge mask, lod
        UNITY_INSTANCING_BUFFER_END(SolOceanPatch)

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            float3 velocityWS : TEXCOORD2;
            float4 data : TEXCOORD3; // x foam, y linear eye depth, z skirt, w unused
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings WaterVertex(Attributes input)
        {
            Varyings output;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            float3 baseWS = TransformObjectToWorld(input.positionOS.xyz);
            float isSkirt = step(1.5, input.uv.x);
            float4 patchData = UNITY_ACCESS_INSTANCED_PROP(SolOceanPatch, _SolOceanPatchData);
            int resolution = max(2, (int)round(patchData.y));
            int edgeMask = (int)round(patchData.z);
            float vertexStep = patchData.x / resolution;
            int2 vertexIndex = (int2)round(saturate(input.uv) * resolution);
            float edgeMorph = 0.0;
            if ((edgeMask & 1) != 0) edgeMorph = max(edgeMorph, 1.0 - saturate(vertexIndex.x * 0.5));
            if ((edgeMask & 2) != 0) edgeMorph = max(edgeMorph, 1.0 - saturate((resolution - vertexIndex.x) * 0.5));
            if ((edgeMask & 4) != 0) edgeMorph = max(edgeMorph, 1.0 - saturate(vertexIndex.y * 0.5));
            if ((edgeMask & 8) != 0) edgeMorph = max(edgeMorph, 1.0 - saturate((resolution - vertexIndex.y) * 0.5));
            float evaluationSpacing = vertexStep * lerp(1.0, 2.0, edgeMorph);
            bool verticalBoundary = (((edgeMask & 1) != 0) && vertexIndex.x == 0)
                || (((edgeMask & 2) != 0) && vertexIndex.x == resolution);
            bool horizontalBoundary = (((edgeMask & 4) != 0) && vertexIndex.y == 0)
                || (((edgeMask & 8) != 0) && vertexIndex.y == resolution);
            bool stitchVertical = isSkirt < 0.5 && verticalBoundary && ((vertexIndex.y & 1) != 0);
            bool stitchHorizontal = isSkirt < 0.5 && horizontalBoundary && ((vertexIndex.x & 1) != 0);
            if (stitchVertical)
                baseWS.z += ((edgeMask & 2) != 0 ? 1.0 : -1.0) * vertexStep;
            if (stitchHorizontal)
                baseWS.x += ((edgeMask & 8) != 0 ? 1.0 : -1.0) * vertexStep;

            // Odd fine-edge vertices now duplicate a real coarse-grid endpoint before
            // displacement. The resulting degenerate edge triangles cannot form a
            // perspective-space T-junction.
            SolWaterWaveResult wave = SolEvaluateWaterWaves(baseWS.xz, evaluationSpacing);
            float3 positionWS = baseWS + wave.displacement;
            output.positionWS = positionWS;
            output.normalWS = wave.normal;
            output.velocityWS = wave.velocity;
            output.positionCS = TransformWorldToHClip(positionWS);
            output.data = float4(wave.foam, -TransformWorldToView(positionWS).z, isSkirt, 0);
            return output;
        }
        ENDHLSL

        Pass
        {
            Name "SolWaterPrepass"
            Tags { "LightMode"="SolWaterPrepass" }
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex WaterVertex
            #pragma fragment WaterPrepassFragment

            struct PrepassOutput
            {
                float4 data : SV_Target0;
                float4 normal : SV_Target1;
            };

            PrepassOutput WaterPrepassFragment(Varyings input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                if (input.data.z > 0.5)
                    discard;
                PrepassOutput output;
                float3 pixelNormal = input.normalWS;
                float pixelFoam = input.data.x;
                if (_SolWaterSpectralParams.x > 0.5)
                    SolEvaluateWaterPixelNormalFoam(input.positionWS.xz, pixelNormal, pixelFoam);
                float frontFace = step(input.positionWS.y, GetCameraPositionWS().y);
                output.data = float4(_SolWaterBodyHash, frontFace, input.positionCS.z, pixelFoam);
                output.normal = float4(normalize(pixelNormal) * 0.5 + 0.5, input.data.z);
                return output;
            }
            ENDHLSL
        }

        Pass
        {
            Name "SolWaterForward"
            Tags { "LightMode"="SolWaterForward" }
            ZWrite Off
            ZTest LEqual
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex WaterVertex
            #pragma fragment WaterForwardFragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            half4 WaterForwardFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                if (input.data.z > 0.5)
                    discard;
                float3 normalWS = normalize(input.normalWS);
                float surfaceFoam = input.data.x;
                if (_SolWaterSpectralParams.x > 0.5)
                    SolEvaluateWaterPixelNormalFoam(input.positionWS.xz, normalWS, surfaceFoam);
                float3 viewDirection = SafeNormalize(GetCameraPositionWS() - input.positionWS);
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float rawDepth = SampleSceneDepth(screenUV);
                float sceneDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float thickness = max(0, sceneDepth - input.data.y);
                float4 shorelineData = SolSampleShorelineData(input.positionWS.xz);

                float roughness = saturate(1.0 - _SolWaterOptics.y
                    + _SolWaterWeather.z * _SolWaterWeatherExtended.z);
                float3 normalVS = TransformWorldToViewDir(normalWS, true);
                float2 refractOffset = float2(normalVS.x, -normalVS.y)
                    * (0.008 + roughness * 0.012)
                    * saturate(thickness * 0.25);
                float2 refractUV = clamp(screenUV + refractOffset, 0.001, 0.999);
                float refractedRawDepth = SampleSceneDepth(refractUV);
                float refractedEyeDepth = LinearEyeDepth(refractedRawDepth, _ZBufferParams);
                // Reject offsets that cross an object in front of the water surface.
                // This avoids the rectangular foreground pulls visible at clipmap edges.
                float refractionConfidence = smoothstep(0.0, 0.12,
                    refractedEyeDepth - input.data.y);
                refractUV = lerp(screenUV, refractUV, refractionConfidence);
                float3 refracted = SAMPLE_TEXTURE2D_X_LOD(
                    _SolWaterSceneColor, sampler_SolWaterSceneColor, refractUV, 0).rgb;

                float3 transmittance = exp(-max(_SolWaterAbsorption, 0) * thickness);
                float depthBlend = saturate(thickness / max(0.01, 12.0));
                float3 scatter = lerp(_SolWaterShallowColor.rgb, _SolWaterDeepColor.rgb, depthBlend)
                    * (1.0 - transmittance) * _SolWaterOptics.z;
                refracted = refracted * transmittance + scatter;

                float eta = max(1.001, _SolWaterOptics.x);
                float f0 = pow((1.0 - eta) / (1.0 + eta), 2.0);
                float fresnel = f0 + (1.0 - f0) * pow(1.0 - saturate(dot(normalWS, viewDirection)), 5.0);
                float4 ssr = SAMPLE_TEXTURE2D_X_LOD(
                    _SolWaterSSRTexture, sampler_SolWaterSSRTexture, screenUV, 0);
                float4 rawSsr = SAMPLE_TEXTURE2D_X_LOD(
                    _SolWaterSSRRawTexture, sampler_SolWaterSSRRawTexture, screenUV, 0);
                float3 reflectionDirection = reflect(-viewDirection, normalWS);
                float3 probe = GlossyEnvironmentReflection(reflectionDirection, roughness, 1.0);
                float3 dynamicSky = SolWaterDynamicSky(reflectionDirection);
                float3 reflection = lerp(probe, dynamicSky,
                    saturate(_SolWaterReflectionParams.x));
                float ssrConfidence = saturate(ssr.a) * (1.0 - roughness * 0.65)
                    * saturate(fresnel * 4.0);
                reflection = lerp(reflection, ssr.rgb, ssrConfidence);
                float4 planarClip = mul(_SolWaterPlanarViewProjection, float4(input.positionWS, 1.0));
                float2 planarUV = planarClip.xy / max(0.0001, planarClip.w) * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                    planarUV.y = 1.0 - planarUV.y;
                #endif
                float2 planarEdge = min(planarUV, 1.0 - planarUV);
                float planarConfidence = _SolWaterPlanarParams.x * _SolWaterPlanarParams.z
                    * step(0.0, planarClip.w)
                    * saturate(min(planarEdge.x, planarEdge.y) * 30.0)
                    * step(abs(_SolWaterBodyHash - _SolWaterPlanarParams.y), 0.000001);
                float3 planar = SAMPLE_TEXTURE2D_LOD(_SolWaterPlanarReflectionTexture,
                    sampler_SolWaterPlanarReflectionTexture, saturate(planarUV), 0).rgb;
                reflection = lerp(reflection, planar, planarConfidence);

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float3 halfDirection = SafeNormalize(mainLight.direction + viewDirection);
                float specular = pow(saturate(dot(normalWS, halfDirection)), lerp(24.0, 256.0, _SolWaterOptics.y));
                float cloudShadow = lerp(1.0, 0.45, _SolWaterWeatherExtended.x);
                float lightning = 1.0 + _SolWaterWeatherExtended.y
                    * saturate(_SolWaterWeatherExtended.y);
                reflection += mainLight.color * specular * mainLight.shadowAttenuation
                    * cloudShadow * lightning;

                // Restrained light-space caustic response. Do not synthesize a
                // periodic sine grid here: WaterFX uses projected caustic maps,
                // while procedural bands create the dotted lattice artifact at
                // grazing angles. The full projected caustic pass remains a
                // separate world-space resource.
                float3 lightUp = SafeNormalize(mainLight.direction);
#if UNITY_REVERSED_Z
                    float causticDepthValid = step(0.00001, rawDepth);
#else
                    float causticDepthValid = step(rawDepth, 0.99999);
#endif
                float causticPattern = pow(saturate(dot(normalWS, lightUp)), 2.0);
                float causticVisibility = causticPattern
                    * mainLight.shadowAttenuation * causticDepthValid
                    * saturate(thickness * 0.08);
                refracted += mainLight.color * causticPattern * causticVisibility
                    * _SolWaterFoamParams.z * 0.02;

                #if UNITY_REVERSED_Z
                    float hasSceneGeometry = step(0.00001, rawDepth);
                #else
                    float hasSceneGeometry = step(rawDepth, 0.99999);
                #endif
                float2 shorelineUV = (input.positionWS.xz + _SolWaterWorldOrigin.xz
                    - _SolWaterShorelineParams.xy)
                    / max(_SolWaterShorelineParams.zw, 0.001) + 0.5;
                float2 shorelineInside = step(0.0, shorelineUV) * step(shorelineUV, 1.0);
                float authoredShoreline = shorelineInside.x * shorelineInside.y
                    * SAMPLE_TEXTURE2D_LOD(_SolWaterShorelineMask,
                        sampler_SolWaterShorelineMask, shorelineUV, 0).r;
                float shorelineWidth = max(_SolWaterShorelineDetail.y, 0.001);
                float widthContact = (1.0 - smoothstep(0.025, shorelineWidth,
                    thickness)) * step(0.002, thickness) * hasSceneGeometry;
                float shorelineDistanceAa = fwidth(shorelineData.y) + 0.02;
                float signedDistanceContact = 1.0 - smoothstep(shorelineWidth,
                    shorelineWidth + shorelineDistanceAa, abs(shorelineData.y));
                signedDistanceContact *= step(0.0, shorelineData.x);
                widthContact = lerp(widthContact, signedDistanceContact,
                    saturate(shorelineData.z));
                float shorelineFoam = saturate(max(widthContact, authoredShoreline
                    * _SolWaterShorelineDetail.x)) * saturate(_SolWaterFoamParams.y);
                // Breaking-wave foam is already a confidence value from the horizontal
                // displacement Jacobian. Squaring it suppresses isolated numerical specks.
                float crestFoam = surfaceFoam * surfaceFoam;
                float foamConfidence = saturate(crestFoam
                    + shorelineFoam * (1.0 - crestFoam));
                float foam = SolWaterFoamTextureMask(input.positionWS.xz, foamConfidence);
                float foamNdotL = saturate(dot(normalWS, mainLight.direction));
                float3 foamLighting = SolWaterDynamicSky(normalWS) * 0.55
                    + mainLight.color * lerp(0.15, 1.0, foamNdotL)
                    * mainLight.shadowAttenuation * cloudShadow;
                foamLighting = clamp(foamLighting, 0.18, 1.15);
                float3 litFoam = _SolWaterFoamColor.rgb * foamLighting
                    * max(0.0, _SolWaterFoamDetail.z);
                float3 waterSurface = lerp(reflection, litFoam, foam);
                waterSurface = lerp(waterSurface,
                    waterSurface * lerp(1.0, 0.72, saturate(_SolWaterShorelineDetail.z)),
                    saturate(shorelineFoam * _SolWaterShorelineDetail.z));
                waterSurface = SolApplyAtmosphere(
                    waterSurface,
                    GetCameraPositionWS(),
                    input.positionWS,
                    -viewDirection,
                    0.0);

                float meanTransmittance = dot(transmittance, float3(1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0));
                float opacity = saturate(0.25 + (1.0 - meanTransmittance) + fresnel * 0.55 + foam * 0.4);
                float contactMeasure = shorelineData.z > 0.5
                    ? min(max(0.0, shorelineData.x), max(0.0, shorelineData.y))
                    : thickness;
                float contactFade = smoothstep(0.0,
                    max(0.01, _SolWaterShorelineSurfaceParams.x) + fwidth(contactMeasure),
                    contactMeasure);
                opacity *= lerp(contactFade, 1.0, saturate(foam * 0.85));
                float3 sourceColor = lerp(refracted, waterSurface, saturate(fresnel + foam * 0.65));
                int debugMode = (int)round(_SolWaterReflectionParams.y);
                if (debugMode == 1)
                    return half4(rawSsr.rgb, 1.0);
                if (debugMode == 2)
                    return half4(ssr.rgb, 1.0);
                if (debugMode == 3)
                    return half4(ssr.aaa, 1.0);
                if (debugMode == 4)
                    return half4(dynamicSky, 1.0);
                if (debugMode == 5)
                    return half4(foamConfidence.xxx, 1.0);
                return half4(sourceColor, opacity);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
