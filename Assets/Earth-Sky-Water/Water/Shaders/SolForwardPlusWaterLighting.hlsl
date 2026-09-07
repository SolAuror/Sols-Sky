#ifndef SOL_FORWARD_PLUS_WATER_LIGHTING_INCLUDED
#define SOL_FORWARD_PLUS_WATER_LIGHTING_INCLUDED

// Shared URP 17 clustered-light path for both Sol water implementations. This contributes
// specular only: local lights must not brighten volume scattering, caustics, or refraction.
half3 SolWaterEvaluateLightSpecular(
    Light light,
    half3 normalWS,
    half3 viewDirectionWS,
    half roughness,
    half intensity,
    half hdrCeiling)
{
    half3 lightDirection = SafeNormalize(light.direction);
    half ndotl = saturate(dot(normalWS, lightDirection));
    half3 halfDirection = SafeNormalize(viewDirectionWS + lightDirection);
    half ndoth = saturate(dot(normalWS, halfDirection));
    half alpha = max(0.0025h, roughness * roughness);
    half alphaSquared = alpha * alpha;
    half denominator = ndoth * ndoth * (alphaSquared - 1.0h) + 1.0h;
    half distribution = alphaSquared /
        max(0.0001h, PI * denominator * denominator);
    half attenuation = light.distanceAttenuation * light.shadowAttenuation;
    return min(light.color * (distribution * ndotl * attenuation * intensity),
        max(0.0h, hdrCeiling).xxx);
}

half3 SolWaterAdditionalSpecular(
    float3 positionWS,
    float4 positionCS,
    half3 normalWS,
    half3 viewDirectionWS,
    half roughness,
    half intensity,
    half hdrCeiling)
{
    half3 result = 0.0h;
#if defined(_ADDITIONAL_LIGHTS)
    InputData inputData = (InputData)0;
    inputData.positionWS = positionWS;
    inputData.normalWS = normalWS;
    inputData.viewDirectionWS = viewDirectionWS;
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionCS);
    inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);

    // Forward+ keeps additional directional lights outside the spatial clusters.
#if USE_CLUSTER_LIGHT_LOOP
    [loop] for (uint lightIndex = 0u;
        lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS);
        ++lightIndex)
    {
        CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
        Light light = GetAdditionalLight(lightIndex, positionWS, inputData.shadowMask);
        result += SolWaterEvaluateLightSpecular(
            light, normalWS, viewDirectionWS, roughness, intensity, hdrCeiling);
    }
#endif

    uint pixelLightCount = GetAdditionalLightsCount();
    LIGHT_LOOP_BEGIN(pixelLightCount)
        Light light = GetAdditionalLight(lightIndex, positionWS, inputData.shadowMask);
        result += SolWaterEvaluateLightSpecular(
            light, normalWS, viewDirectionWS, roughness, intensity, hdrCeiling);
    LIGHT_LOOP_END
#endif
    return result;
}

#endif
