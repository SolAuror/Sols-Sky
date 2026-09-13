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

float SolLandscapeRange(float value, float low, float high, float feather)
{
    return smoothstep(low - feather, low, value) * (1 - smoothstep(high, high + feather, value));
}
float2 SolLandscapePaintUV(float2 terrainUV)
{
    return (terrainUV * (_Sol_LandscapePaintFlags.z - 1) + .5) / _Sol_LandscapePaintFlags.z;
}
void SolLandscapeReadPaint(float2 terrainUV, bool exclusion, out float4 low, out float4 high)
{
    low = high = 0;
    float2 uv = SolLandscapePaintUV(terrainUV);
    if (exclusion && _Sol_LandscapePaintFlags.y > .5)
    {
        low = SAMPLE_TEXTURE2D(_Sol_LandscapePaint2, sampler_Sol_LandscapePaint0, uv);
        high = SAMPLE_TEXTURE2D(_Sol_LandscapePaint3, sampler_Sol_LandscapePaint0, uv);
    }
    if (!exclusion && _Sol_LandscapePaintFlags.x > .5)
    {
        low = SAMPLE_TEXTURE2D(_Sol_LandscapePaint0, sampler_Sol_LandscapePaint0, uv);
        high = SAMPLE_TEXTURE2D(_Sol_LandscapePaint1, sampler_Sol_LandscapePaint0, uv);
    }
}
float SolLandscapeChannel(float4 low, float4 high, int index)
{
    if (index == 0) return low.r;
    if (index == 1) return low.g;
    if (index == 2) return low.b;
    if (index == 3) return low.a;
    if (index == 4) return high.r;
    if (index == 5) return high.g;
    if (index == 6) return high.b;
    return high.a;
}
void SolLandscapeReadRemoval(float2 terrainUV, out float4 low, out float4 high)
{
    low = high = 0;
    if (_Sol_LandscapePaintFlags.w > .5)
    {
        float2 uv = SolLandscapePaintUV(terrainUV);
        low = SAMPLE_TEXTURE2D(_Sol_LandscapePaint4, sampler_Sol_LandscapePaint0, uv);
        high = SAMPLE_TEXTURE2D(_Sol_LandscapePaint5, sampler_Sol_LandscapePaint0, uv);
    }
}
float SolLandscapeRemoval(float4 low, float4 high, int index)
{
    return index == (int)_Sol_LandscapeFallback || _Sol_LandscapeRuleSettings[index].z > .5
        ? 0 : saturate(SolLandscapeChannel(low, high, index));
}
float SolLandscapeNoise(float2 p)
{
    float2 cell = floor(p), f = frac(p); f = f*f*(3-2*f);
    float4 n = frac(sin(float4(dot(cell,float2(127.1,311.7)), dot(cell+float2(1,0),float2(127.1,311.7)), dot(cell+float2(0,1),float2(127.1,311.7)), dot(cell+1,float2(127.1,311.7))))*43758.5453);
    return lerp(lerp(n.x,n.y,f.x),lerp(n.z,n.w,f.x),f.y)*2-1;
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

    // The slope rule above is monotonic: a layer can favour flat ground or steep ground, but on its
    // own it cannot occupy a band, because once it saturates it stays saturated all the way to
    // vertical. The ceiling supplies the falling edge, so soil can claim a mid-slope shoulder and
    // then shed off the cliffs above it the same way snow does. A ceiling of 90 degrees costs
    // nothing and changes nothing: the input can never rise above zero, so the curve stays at zero.
    float4 ceilingParams = _Sol_LandscapeAutoSlopeCeilingParams[layerIndex];
    float ceilingInput = (slopeDegrees - ceilingParams.x) / max(ceilingParams.y, 0.01f);
    slopeRule *= 1.0f - SolEvaluateLandscapeResponseCurve(ceilingInput, 0.0f);

    float4 heightParams = _Sol_LandscapeAutoHeightParams[layerIndex];
    // SolTerrainWetness.hlsl is included before this file and owns the existing
    // _Sol_GlobalWaterLevel declaration. Sand/shore rules can follow moving water
    // without introducing a second publisher or a duplicate shader global.
    float altitude = worldY
        - step(0.5f, _Sol_LandscapeAutoAltitudeReferences[layerIndex]) * _Sol_GlobalWaterLevel;
    float heightInput = (altitude - heightParams.x) / max(heightParams.y - heightParams.x, 0.01f);
    float heightResponse = SolEvaluateLandscapeResponseCurve(heightInput, heightParams.z);
    float heightRule = SolEvaluateLandscapeDirectedResponse(heightResponse, heightParams.w);

    if (_Sol_LandscapeRuleSettings[layerIndex].x > .5)
    {
        float4 range = _Sol_LandscapeRuleRanges[layerIndex];
        slopeRule = SolLandscapeRange(slopeDegrees, range.x, range.y, range.z);
        heightRule = _Sol_LandscapeRuleSettings[layerIndex].y > .5
            ? SolLandscapeRange(altitude, heightParams.x, heightParams.y, range.w) : 1;
    }
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
    float4 exclusion0, exclusion1, removal0, removal1;
    float2 terrainUV = (positionWS.xz - _Sol_LandscapeTerrainOriginSize.xy) * _Sol_LandscapeTerrainOriginSize.zw;
    SolLandscapeReadPaint(terrainUV, true, exclusion0, exclusion1);
    SolLandscapeReadRemoval(terrainUV, removal0, removal1);
    float manualWeight = 0.0f;
    float paintedAutoWeight = 0.0f;
    float anyAutoLayer = 0.0f;

    [unroll]
    for (int sumIndex = 0; sumIndex < SOL_LANDSCAPE_LAYER_COUNT; ++sumIndex)
    {
        float autoLayer = step(0.5f, _Sol_LandscapeLayerModes[sumIndex]);
        weights[sumIndex] *= 1 - SolLandscapeRemoval(removal0, removal1, sumIndex);
        float paintedWeight = weights[sumIndex];
        manualWeight += paintedWeight * (1.0f - autoLayer);
        paintedAutoWeight += paintedWeight * autoLayer;
        anyAutoLayer = max(anyAutoLayer, autoLayer);
    }

    // A fully hand-painted configuration is an exact no-op, and pays for none of what
    // follows. This return must precede the cavity derivatives and procedural evaluation:
    // with no Auto layer there is no budget to redistribute, and the painted alphamap
    // already is the answer.
    if (anyAutoLayer < 0.5f)
    {
        if (_Sol_LandscapePaintFlags.w > .5)
        {
            [unroll] for (int i = 0; i < SOL_LANDSCAPE_LAYER_COUNT; i++)
                weights[i] = manualWeight > kSolAutoMaterialWeightEpsilon ? weights[i] / manualWeight : (i == (int)_Sol_LandscapeFallback ? 1 : 0);
            manualWeight = 1;
        }
        manualAutoWeights = float2(saturate(manualWeight), 0.0f);
        return;
    }

    float proceduralWeight = 0.0f;
    float proceduralWeights[SOL_LANDSCAPE_LAYER_COUNT];
    float slopeDegrees = degrees(acos(saturate(geometricNormalWS.y)));
    float signedCavity = SolEvaluateLandscapeCavity(positionWS, geometricNormalWS);
    [unroll]
    for (int evaluateIndex = 0; evaluateIndex < SOL_LANDSCAPE_LAYER_COUNT; ++evaluateIndex)
    {
        float authoredProceduralWeight = SolEvaluateLandscapeProceduralWeight(
            evaluateIndex,
            slopeDegrees,
            positionWS.y,
            signedCavity);
        float exclusion = evaluateIndex == (int)_Sol_LandscapeFallback || _Sol_LandscapeRuleSettings[evaluateIndex].z > .5 ? 0 : SolLandscapeChannel(exclusion0, exclusion1, evaluateIndex);
        authoredProceduralWeight *= (1 - exclusion) * (1 - SolLandscapeRemoval(removal0, removal1, evaluateIndex));
        proceduralWeights[evaluateIndex] = authoredProceduralWeight;
        float autoLayer = step(0.5f, _Sol_LandscapeLayerModes[evaluateIndex]);
        proceduralWeight += authoredProceduralWeight * autoLayer;
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

    if (_Sol_LandscapeUseFallback > .5 || _Sol_LandscapePaintFlags.y > .5 || _Sol_LandscapePaintFlags.w > .5)
    {
        [unroll] for (int fallback = 0; fallback < SOL_LANDSCAPE_LAYER_COUNT; fallback++)
            if (_Sol_LandscapeLayerModes[fallback] > .5) weights[fallback] = 0;
        weights[(int)_Sol_LandscapeFallback] += autoBudget;
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

// Snow remains a post-material overlay, never a TerrainLayer or array slice. Weather
// coverage follows the integrated surface state while the separately authored altitude
// response supplies a permanent mountaintop floor.
static const half kSolLandscapeSnowNormalRetention = 0.35h;
static const float2 kSolLandscapeWeatherSnowAltitudeRange = float2(0.0f, 100.0f);
static const float kSolLandscapeWeatherSnowAltitudeBaseline = 0.85f;
static const float2 kSolLandscapeWeatherSnowSlopeSheddingRange = float2(15.0f, 35.0f);

float SolEvaluateLandscapeSnowCoverage(
    float3 positionWS,
    float3 geometricNormalWS,
    float weatherSusceptibility,
    float permanentSusceptibility,
    out float weatherAltitudeResponse,
    out float permanentCoverage,
    out float weatherCoverage,
    out float slopeShedding)
{
    float weatherAltitudeInput = (positionWS.y - kSolLandscapeWeatherSnowAltitudeRange.x)
        / (kSolLandscapeWeatherSnowAltitudeRange.y - kSolLandscapeWeatherSnowAltitudeRange.x);
    float weatherAltitudeCurve = SolEvaluateLandscapeResponseCurve(weatherAltitudeInput, 0.0f);
    weatherAltitudeResponse = lerp(
        kSolLandscapeWeatherSnowAltitudeBaseline,
        1.0f,
        weatherAltitudeCurve);

    float permanentAltitudeInput = (positionWS.y - _Sol_LandscapeSnowParams.x)
        / max(_Sol_LandscapeSnowParams.y - _Sol_LandscapeSnowParams.x, 0.01f);
    float permanentAltitudeResponse = SolEvaluateLandscapeResponseCurve(permanentAltitudeInput, 0.0f);

    float slopeDegrees = degrees(acos(saturate(geometricNormalWS.y)));
    float weatherSlopeInput = (slopeDegrees - kSolLandscapeWeatherSnowSlopeSheddingRange.x)
        / (kSolLandscapeWeatherSnowSlopeSheddingRange.y - kSolLandscapeWeatherSnowSlopeSheddingRange.x);
    slopeShedding = 1.0f - SolEvaluateLandscapeResponseCurve(weatherSlopeInput, 0.0f);
    float permanentSlopeInput = (slopeDegrees - _Sol_LandscapePermanentSnowSlopeSheddingRange.x)
        / max(
            _Sol_LandscapePermanentSnowSlopeSheddingRange.y
                - _Sol_LandscapePermanentSnowSlopeSheddingRange.x,
            0.01f);
    float permanentSlopeShedding = 1.0f
        - SolEvaluateLandscapeResponseCurve(permanentSlopeInput, 0.0f);

    // SnowCover is integrated surface state from SolEnvironmentWorld. Instantaneous
    // precipitation and temperature are intentionally absent so coverage accumulates
    // during freezing precipitation and recedes only as the simulation advances thaw.
    // Applying the current air temperature again here made an unchanged snowpack appear
    // on cold mornings and disappear during warm afternoons.
    // max is deliberate: permanent altitude Snow is a floor, never an additive
    // second coat. The split susceptibilities distinguish fresh-weather adhesion
    // from accumulated pack; both are evaluated from already-selected material
    // contributors and cannot feed back into layer weights.
    weatherCoverage = saturate(_Sol_SurfaceSnowCover)
        * weatherAltitudeResponse
        * slopeShedding;
    permanentCoverage = permanentAltitudeResponse * permanentSlopeShedding;
    return saturate(max(
        permanentCoverage * saturate(permanentSusceptibility),
        weatherCoverage * saturate(weatherSusceptibility)));
}

void SolApplyLandscapeSnow(
    inout SurfaceData surfaceData,
    float3 positionWS,
    float3 geometricNormalWS,
    float weatherSusceptibility,
    float permanentSusceptibility,
    out float snowCoverage)
{
    float weatherAltitudeResponse;
    float permanentCoverage;
    float weatherCoverage;
    float slopeShedding;
    snowCoverage = SolEvaluateLandscapeSnowCoverage(
        positionWS,
        geometricNormalWS,
        weatherSusceptibility,
        permanentSusceptibility,
        weatherAltitudeResponse,
        permanentCoverage,
        weatherCoverage,
        slopeShedding);

    // This branch intentionally precedes all three Snow texture samples. A frame
    // with no coverage pays zero overlay fetches; covered pixels pay three.
    if (snowCoverage <= kSolAutoMaterialWeightEpsilon)
        return;

    float2 snowUV = positionWS.xz * _Sol_LandscapeSnowParams.z;
    half4 snowColor = SAMPLE_TEXTURE2D(
        _Sol_LandscapeSnowColor,
        sampler_Sol_LandscapeSnowColor,
        snowUV);
    half3 snowNormalDetail = UnpackNormalScale(
        SAMPLE_TEXTURE2D(_Sol_LandscapeSnowNormal, sampler_Sol_LandscapeSnowNormal, snowUV),
        _Sol_LandscapeSnowParams.w);
    half4 snowPacked = SAMPLE_TEXTURE2D(
        _Sol_LandscapeSnowPacked,
        sampler_Sol_LandscapeSnowPacked,
        snowUV);

    // The preserved packed source carries authored height in B. Use it for subtle
    // granular albedo, smoothness, and occlusion variation without changing cover.
    half snowHeight = snowPacked.b;
    half3 snowAlbedo = snowColor.rgb * lerp(0.92h, 1.04h, snowHeight);
    half snowSmoothness = lerp(0.28h, 0.52h, snowHeight);
    half snowOcclusion = lerp(0.94h, 1.0h, snowHeight);
    surfaceData.albedo = lerp(surfaceData.albedo, snowAlbedo, snowCoverage);
    surfaceData.smoothness = lerp(
        surfaceData.smoothness,
        snowSmoothness,
        snowCoverage);
    surfaceData.occlusion = lerp(surfaceData.occlusion, snowOcclusion, snowCoverage);

    // Retain broad underlying relief, then compose the sampled granular normal on
    // top. The final coverage lerp keeps the zero-to-one transition continuous.
    half3 retainedBaseNormal = normalize(half3(
        surfaceData.normalTS.xy * kSolLandscapeSnowNormalRetention,
        surfaceData.normalTS.z));
    half3 texturedSnowNormal = normalize(half3(
        retainedBaseNormal.xy + snowNormalDetail.xy,
        retainedBaseNormal.z * snowNormalDetail.z));
    surfaceData.normalTS = normalize(lerp(
        surfaceData.normalTS,
        texturedSnowNormal,
        (half)snowCoverage));
}

#endif
