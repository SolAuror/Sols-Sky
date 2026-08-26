#ifndef SOL_TERRAIN_AUTO_MATERIAL_INCLUDED
#define SOL_TERRAIN_AUTO_MATERIAL_INCLUDED

static const float kSolAutoMaterialWeightEpsilon = 1e-6f;
static const float kSolAutoMaterialDerivativeEpsilon = 1e-8f;

// Cubic smoothstep followed by an endpoint-preserving quadratic bias. Positive
// bias ramps early and plateaus; negative bias delays the response. This remains
// ALU-only and avoids a per-layer LUT fetch on the project's fetch-bound terrain.
float SolEvaluateLandscapeResponseCurve(float normalizedInput, float bias)
{
    float response = saturate(normalizedInput);
    response = response * response * (3.0f - 2.0f * response);
    return saturate(response + clamp(bias, -1.0f, 1.0f) * response * (1.0f - response));
}

float SolEvaluateLandscapeDirectedResponse(float response, float signedInfluence)
{
    float directedResponse = signedInfluence >= 0.0f ? response : 1.0f - response;
    return lerp(1.0f, directedResponse, saturate(abs(signedInfluence)));
}

// Screen derivatives estimate signed mean curvature in world units. Positive
// output denotes a concavity. No terrain-heightmap declaration or texture fetch
// is required, so the rule remains valid if terrain instancing is later disabled.
float SolEvaluateLandscapeCavity(float3 positionWS, float3 geometricNormalWS)
{
    float3 positionDx = ddx(positionWS);
    float3 positionDy = ddy(positionWS);
    float3 normalDx = ddx(geometricNormalWS);
    float3 normalDy = ddy(geometricNormalWS);
    float curvature = dot(normalDx, positionDx)
        / max(dot(positionDx, positionDx), kSolAutoMaterialDerivativeEpsilon);
    curvature += dot(normalDy, positionDy)
        / max(dot(positionDy, positionDy), kSolAutoMaterialDerivativeEpsilon);
    return -curvature;
}

float SolEvaluateLandscapeProceduralWeight(
    int layerIndex,
    float slopeDegrees,
    float worldY,
    float signedCavity)
{
    float4 slopeParams = _Sol_LandscapeAutoSlopeParams[layerIndex];
    float slopeInput = (slopeDegrees - slopeParams.x) / max(slopeParams.y, 0.01f) + 0.5f;
    float slopeResponse = SolEvaluateLandscapeResponseCurve(slopeInput, slopeParams.z);
    float slopeRule = SolEvaluateLandscapeDirectedResponse(slopeResponse, slopeParams.w);

    float4 heightParams = _Sol_LandscapeAutoHeightParams[layerIndex];
    float heightInput = (worldY - heightParams.x) / max(heightParams.y - heightParams.x, 0.01f);
    float heightResponse = SolEvaluateLandscapeResponseCurve(heightInput, heightParams.z);
    float heightRule = SolEvaluateLandscapeDirectedResponse(heightResponse, heightParams.w);

    float4 cavityParams = _Sol_LandscapeAutoCavityParams[layerIndex];
    float cavityResponse = clamp(signedCavity * max(cavityParams.x, 0.0f), -1.0f, 1.0f);
    float cavityRule = max(0.0f, 1.0f + cavityResponse * clamp(cavityParams.y, -1.0f, 1.0f));

    return max(_Sol_LandscapeAutoWeights[layerIndex], 0.0f)
        * slopeRule
        * heightRule
        * cavityRule;
}

void SolResolveLandscapeAutoMaterial(
    inout float weights[SOL_LANDSCAPE_LAYER_COUNT],
    float3 positionWS,
    float3 geometricNormalWS,
    out float2 manualAutoWeights)
{
    float manualWeight = 0.0f;
    float paintedAutoWeight = 0.0f;
    float proceduralWeight = 0.0f;
    float anyAutoLayer = 0.0f;
    float proceduralWeights[SOL_LANDSCAPE_LAYER_COUNT];
    float slopeDegrees = degrees(acos(saturate(geometricNormalWS.y)));
    float signedCavity = SolEvaluateLandscapeCavity(positionWS, geometricNormalWS);

    [unroll]
    for (int sumIndex = 0; sumIndex < SOL_LANDSCAPE_LAYER_COUNT; ++sumIndex)
    {
        float autoLayer = step(0.5f, _Sol_LandscapeLayerModes[sumIndex]);
        float paintedWeight = weights[sumIndex];
        float authoredProceduralWeight = SolEvaluateLandscapeProceduralWeight(
            sumIndex,
            slopeDegrees,
            positionWS.y,
            signedCavity);
        proceduralWeights[sumIndex] = authoredProceduralWeight;
        manualWeight += paintedWeight * (1.0f - autoLayer);
        paintedAutoWeight += paintedWeight * autoLayer;
        proceduralWeight += authoredProceduralWeight * autoLayer;
        anyAutoLayer = max(anyAutoLayer, autoLayer);
    }

    // The all-Manual shipping configuration is an exact no-op. Keeping this as
    // an explicit early return also prevents harmless normalization drift.
    if (anyAutoLayer < 0.5f)
    {
        manualAutoWeights = float2(saturate(manualWeight), 0.0f);
        return;
    }

    // Terrain control weights are normalized, so this budget is exactly the sum
    // of the Auto layers' painted weights. The resolve redistributes that existing
    // budget and needs no additional control texture.
    float autoBudget = saturate(1.0f - manualWeight);
    manualAutoWeights = float2(saturate(manualWeight), autoBudget);

    if (proceduralWeight > kSolAutoMaterialWeightEpsilon)
    {
        float inverseProceduralWeight = rcp(proceduralWeight);
        [unroll]
        for (int resolveIndex = 0; resolveIndex < SOL_LANDSCAPE_LAYER_COUNT; ++resolveIndex)
        {
            float autoLayer = step(0.5f, _Sol_LandscapeLayerModes[resolveIndex]);
            float resolvedAutoWeight = autoBudget
                * proceduralWeights[resolveIndex]
                * inverseProceduralWeight;
            weights[resolveIndex] = lerp(weights[resolveIndex], resolvedAutoWeight, autoLayer);
        }
        return;
    }

    // No Auto rule claims the budget. Re-normalize the painted Auto weights back
    // onto that same budget so the shader degrades to today's painted behaviour,
    // never to black and never through a divide by zero.
    float fallbackScale = paintedAutoWeight > kSolAutoMaterialWeightEpsilon
        ? autoBudget / paintedAutoWeight
        : 0.0f;
    [unroll]
    for (int fallbackIndex = 0; fallbackIndex < SOL_LANDSCAPE_LAYER_COUNT; ++fallbackIndex)
    {
        float autoLayer = step(0.5f, _Sol_LandscapeLayerModes[fallbackIndex]);
        float fallbackWeight = weights[fallbackIndex] * fallbackScale;
        weights[fallbackIndex] = lerp(weights[fallbackIndex], fallbackWeight, autoLayer);
    }
}

// Phase 4C snow is a post-material overlay, never a TerrainLayer. These named
// parameters are deliberately ALU-only defaults: no texture, slice, re-bake, or
// top-K participation. The terrain has only 21.6 m of authored relief, so altitude
// is a mild modifier rather than a manufactured low-elevation "snow line".
static const half3 kSolLandscapeSnowAlbedo = half3(0.82h, 0.87h, 0.92h);
static const half kSolLandscapeSnowSmoothness = 0.42h;
static const half kSolLandscapeSnowNormalRetention = 0.35h;
static const float2 kSolLandscapeSnowTemperatureRange = float2(-2.0f, 2.0f);
static const float2 kSolLandscapeSnowAltitudeRange = float2(0.0f, 100.0f);
static const float kSolLandscapeSnowAltitudeBaseline = 0.85f;
static const float2 kSolLandscapeSnowSlopeSheddingRange = float2(15.0f, 35.0f);

float SolEvaluateLandscapeSnowCoverage(
    float3 positionWS,
    float3 geometricNormalWS,
    out float temperatureResponse,
    out float altitudeResponse,
    out float slopeShedding)
{
    float temperatureInput = (_Sol_SurfaceTemperature - kSolLandscapeSnowTemperatureRange.x)
        / (kSolLandscapeSnowTemperatureRange.y - kSolLandscapeSnowTemperatureRange.x);
    temperatureResponse = 1.0f - SolEvaluateLandscapeResponseCurve(temperatureInput, 0.0f);

    float altitudeInput = (positionWS.y - kSolLandscapeSnowAltitudeRange.x)
        / (kSolLandscapeSnowAltitudeRange.y - kSolLandscapeSnowAltitudeRange.x);
    float altitudeCurve = SolEvaluateLandscapeResponseCurve(altitudeInput, 0.0f);
    altitudeResponse = lerp(kSolLandscapeSnowAltitudeBaseline, 1.0f, altitudeCurve);

    float slopeDegrees = degrees(acos(saturate(geometricNormalWS.y)));
    float slopeInput = (slopeDegrees - kSolLandscapeSnowSlopeSheddingRange.x)
        / (kSolLandscapeSnowSlopeSheddingRange.y - kSolLandscapeSnowSlopeSheddingRange.x);
    slopeShedding = 1.0f - SolEvaluateLandscapeResponseCurve(slopeInput, 0.0f);

    // SnowCover is integrated surface state from SolEnvironmentWorld. Instantaneous
    // precipitation is intentionally absent so coverage accumulates and recedes.
    return saturate(_Sol_SurfaceSnowCover)
        * temperatureResponse
        * altitudeResponse
        * slopeShedding;
}

void SolApplyLandscapeSnow(
    inout SurfaceData surfaceData,
    float3 positionWS,
    float3 geometricNormalWS,
    out float snowCoverage)
{
    float temperatureResponse;
    float altitudeResponse;
    float slopeShedding;
    snowCoverage = SolEvaluateLandscapeSnowCoverage(
        positionWS,
        geometricNormalWS,
        temperatureResponse,
        altitudeResponse,
        slopeShedding);

    if (snowCoverage <= kSolAutoMaterialWeightEpsilon)
        return;

    surfaceData.albedo = lerp(surfaceData.albedo, kSolLandscapeSnowAlbedo, snowCoverage);
    surfaceData.smoothness = lerp(
        surfaceData.smoothness,
        kSolLandscapeSnowSmoothness,
        snowCoverage);

    // Preserve the blended surface normal while damping its XY relief beneath snow.
    // Z is retained and the result re-normalized, so this is neither a flat decal nor
    // an extra normal sample.
    half normalAttenuation = lerp(1.0h, kSolLandscapeSnowNormalRetention, (half)snowCoverage);
    surfaceData.normalTS.xy *= normalAttenuation;
    surfaceData.normalTS = normalize(surfaceData.normalTS);
}

#endif
