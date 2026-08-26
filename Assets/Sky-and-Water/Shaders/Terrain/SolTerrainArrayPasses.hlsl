#ifndef SOL_TERRAIN_ARRAY_PASSES_INCLUDED
#define SOL_TERRAIN_ARRAY_PASSES_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "SolTerrainWetness.hlsl"

#define SOL_LANDSCAPE_LAYER_COUNT 6
#define SOL_LANDSCAPE_TOP_K 4
#define SOL_LANDSCAPE_DEBUG_LAYER_WEIGHT 1
#define SOL_LANDSCAPE_DEBUG_MANUAL_AUTO_SPLIT 2
#define SOL_LANDSCAPE_DEBUG_RESOLVED_STONE 3
#define SOL_LANDSCAPE_DEBUG_RESOLVED_PATH 4
#define SOL_LANDSCAPE_DEBUG_SNOW_COVERAGE 5

#include "SolTerrainAutoMaterial.hlsl"

struct SolTerrainArrayAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float2 terrainUV : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct SolTerrainArrayVaryings
{
    float2 terrainUV : TEXCOORD0;
    float3 positionWS : TEXCOORD1;
    half4 normalWSAndViewX : TEXCOORD2;
    half4 tangentWSAndViewY : TEXCOORD3;
    half4 bitangentWSAndViewZ : TEXCOORD4;
    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 5);

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    half3 vertexLighting : TEXCOORD6;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord : TEXCOORD7;
#endif

#if defined(DYNAMICLIGHTMAP_ON)
    float2 dynamicLightmapUV : TEXCOORD8;
#endif

#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion : TEXCOORD9;
#endif

    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

SolTerrainArrayVaryings SolTerrainArrayVertex(SolTerrainArrayAttributes input)
{
    SolTerrainArrayVaryings output = (SolTerrainArrayVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    SolTerrainInstancing(input.positionOS, input.normalOS, input.terrainUV);

    VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
    half3 viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionInputs.positionWS);
    float4 tangentOS = float4(cross(float3(0.0f, 0.0f, 1.0f), input.normalOS), 1.0f);
    VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, tangentOS);

    output.terrainUV = input.terrainUV;
    output.positionWS = positionInputs.positionWS;
    output.normalWSAndViewX = half4(normalInputs.normalWS, viewDirectionWS.x);
    output.tangentWSAndViewY = half4(normalInputs.tangentWS, viewDirectionWS.y);
    output.bitangentWSAndViewZ = half4(normalInputs.bitangentWS, viewDirectionWS.z);
    output.positionCS = positionInputs.positionCS;

    OUTPUT_LIGHTMAP_UV(input.terrainUV, unity_LightmapST, output.staticLightmapUV);
    OUTPUT_SH4(
        positionInputs.positionWS,
        normalInputs.normalWS,
        viewDirectionWS,
        output.vertexSH,
        output.probeOcclusion);

#if defined(DYNAMICLIGHTMAP_ON)
    output.dynamicLightmapUV = input.terrainUV * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
#endif

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    output.vertexLighting = VertexLighting(positionInputs.positionWS, normalInputs.normalWS);
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(positionInputs);
#endif

    return output;
}

struct SolLandscapeRawWeights
{
    half4 control0;
    half2 control1;
};

SolLandscapeRawWeights SolDecodeLandscapeRawWeights(float2 terrainUV)
{
    // Stock TerrainLit half-texel correction. The driver publishes the renamed texel-size global.
    float2 splatUV =
        (terrainUV * (_Sol_LandscapeControlTexelSize.zw - 1.0f) + 0.5f)
        * _Sol_LandscapeControlTexelSize.xy;

    SolLandscapeRawWeights weights;
    weights.control0 = SAMPLE_TEXTURE2D(
        _Sol_LandscapeControl0,
        sampler_Sol_LandscapeControl0,
        splatUV);
    weights.control1 = SAMPLE_TEXTURE2D(
        _Sol_LandscapeControl1,
        sampler_Sol_LandscapeControl1,
        splatUV).rg;
    return weights;
}

half SolSelectLandscapeRawWeight(SolLandscapeRawWeights weights, int layerIndex)
{
    if (layerIndex == 0) return weights.control0.r;
    if (layerIndex == 1) return weights.control0.g;
    if (layerIndex == 2) return weights.control0.b;
    if (layerIndex == 3) return weights.control0.a;
    if (layerIndex == 4) return weights.control1.r;
    if (layerIndex == 5) return weights.control1.g;
    return 0.0h;
}

// Retained for Phase 3. Height is baked into NOH alpha but does not affect Phase 2 blending.
half SolLandscapeHeight(half4 noh)
{
    return noh.a;
}

half3 SolDecodeLandscapeNormalTS(half4 noh, half normalScale)
{
    half2 normalXY = noh.rg * 2.0h - 1.0h;
    normalXY *= normalScale;
    half normalZ = sqrt(saturate(1.0h - dot(normalXY, normalXY)));
    return half3(normalXY, normalZ);
}

struct SolLandscapeSurface
{
    SurfaceData surfaceData;
    half postBlendDebugWeight;
    half2 manualAutoDebugWeights;
    half resolvedAutoDebugWeight;
    half resolvedPathDebugWeight;
    half snowCoverage;
};

struct SolLandscapeLayerWeight
{
    float weight;
    int layerIndex;
};

struct SolLandscapeLayerSample
{
    float weight;
    int layerIndex;
    half4 noh;
};

void SolSelectLandscapeTopK(
    float resolvedWeights[SOL_LANDSCAPE_LAYER_COUNT],
    out SolLandscapeLayerWeight selectedLayers[SOL_LANDSCAPE_TOP_K])
{
    [unroll]
    for (int initializationSlot = 0; initializationSlot < SOL_LANDSCAPE_TOP_K; ++initializationSlot)
    {
        selectedLayers[initializationSlot].weight = -1.0h;
        selectedLayers[initializationSlot].layerIndex = 0;
    }

    // Stable insertion keeps the lower authored layer index first when two weights tie.
    [unroll]
    for (int layerIndex = 0; layerIndex < SOL_LANDSCAPE_LAYER_COUNT; ++layerIndex)
    {
        SolLandscapeLayerWeight candidate;
        candidate.weight = resolvedWeights[layerIndex];
        candidate.layerIndex = layerIndex;

        [unroll]
        for (int insertionSlot = 0; insertionSlot < SOL_LANDSCAPE_TOP_K; ++insertionSlot)
        {
            if (candidate.weight > selectedLayers[insertionSlot].weight)
            {
                SolLandscapeLayerWeight displaced = selectedLayers[insertionSlot];
                selectedLayers[insertionSlot] = candidate;
                candidate = displaced;
            }
        }
    }

}

float2 SolLandscapeLayerUV(float2 terrainUV, int layerIndex)
{
    float4 layerST = _Sol_LandscapeLayerST[layerIndex];
    return terrainUV * layerST.xy + layerST.zw;
}

half4 SolSampleLandscapeNOH(float2 terrainUV, int layerIndex)
{
    return SAMPLE_TEXTURE2D_ARRAY(
        _Sol_LandscapeNOH,
        sampler_Sol_LandscapeNOH,
        SolLandscapeLayerUV(terrainUV, layerIndex),
        layerIndex);
}

void SolPopulateLandscapeTopKSamples(
    float2 terrainUV,
    SolLandscapeLayerWeight selectedLayers[SOL_LANDSCAPE_TOP_K],
    out SolLandscapeLayerSample selectedSamples[SOL_LANDSCAPE_TOP_K])
{
    [unroll]
    for (int populateSampleSlot = 0; populateSampleSlot < SOL_LANDSCAPE_TOP_K; ++populateSampleSlot)
    {
        selectedSamples[populateSampleSlot].weight = selectedLayers[populateSampleSlot].weight;
        selectedSamples[populateSampleSlot].layerIndex = selectedLayers[populateSampleSlot].layerIndex;
        selectedSamples[populateSampleSlot].noh = SolSampleLandscapeNOH(
            terrainUV,
            selectedLayers[populateSampleSlot].layerIndex);
    }
}

void SolNormalizeLandscapeTopK(inout SolLandscapeLayerSample samples[SOL_LANDSCAPE_TOP_K])
{
    float weightSum = 0.0f;
    [unroll]
    for (int normalizationSumSlot = 0; normalizationSumSlot < SOL_LANDSCAPE_TOP_K; ++normalizationSumSlot)
        weightSum += samples[normalizationSumSlot].weight;

    float inverseWeightSum = rcp(max(weightSum, 1e-6f));
    [unroll]
    for (int normalizationApplySlot = 0; normalizationApplySlot < SOL_LANDSCAPE_TOP_K; ++normalizationApplySlot)
        samples[normalizationApplySlot].weight *= inverseWeightSum;
}

void SolHeightBlendLandscapeTopK(inout SolLandscapeLayerSample samples[SOL_LANDSCAPE_TOP_K])
{
    float maxSplatHeight = -1.0f;
    [unroll]
    for (int maximumHeightSlot = 0; maximumHeightSlot < SOL_LANDSCAPE_TOP_K; ++maximumHeightSlot)
    {
        float splatHeight = (float)SolLandscapeHeight(samples[maximumHeightSlot].noh)
            * samples[maximumHeightSlot].weight;
        maxSplatHeight = max(maxSplatHeight, splatHeight);
    }

    float transition = max(_Sol_LandscapeHeightTransition, 1e-5f);
    float weightedSum = 0.0f;
    [unroll]
    for (int heightBlendSlot = 0; heightBlendSlot < SOL_LANDSCAPE_TOP_K; ++heightBlendSlot)
    {
        float paintedWeight = samples[heightBlendSlot].weight;
        float splatHeight = (float)SolLandscapeHeight(samples[heightBlendSlot].noh) * paintedWeight;
        float weightedHeight = max(0.0f, splatHeight + transition - maxSplatHeight);
        weightedHeight = (weightedHeight + 1e-6f) * paintedWeight;
        samples[heightBlendSlot].weight = weightedHeight;
        weightedSum += weightedHeight;
    }

    float inverseWeightedSum = rcp(max(weightedSum, 1e-6f));
    [unroll]
    for (int finalNormalizationSlot = 0; finalNormalizationSlot < SOL_LANDSCAPE_TOP_K; ++finalNormalizationSlot)
        samples[finalNormalizationSlot].weight *= inverseWeightedSum; // The shipping path's single normalization.
}

void SolBuildLandscapeAllSamples(
    float2 terrainUV,
    float resolvedWeights[SOL_LANDSCAPE_LAYER_COUNT],
    out SolLandscapeLayerSample samples[SOL_LANDSCAPE_LAYER_COUNT])
{
    [unroll]
    for (int buildLayerIndex = 0; buildLayerIndex < SOL_LANDSCAPE_LAYER_COUNT; ++buildLayerIndex)
    {
        samples[buildLayerIndex].weight = resolvedWeights[buildLayerIndex];
        samples[buildLayerIndex].layerIndex = buildLayerIndex;
        samples[buildLayerIndex].noh = SolSampleLandscapeNOH(terrainUV, buildLayerIndex);
    }
}

void SolNormalizeLandscapeAllLayers(
    inout SolLandscapeLayerSample samples[SOL_LANDSCAPE_LAYER_COUNT])
{
    float weightSum = 0.0f;
    [unroll]
    for (int allLayerSumIndex = 0; allLayerSumIndex < SOL_LANDSCAPE_LAYER_COUNT; ++allLayerSumIndex)
        weightSum += samples[allLayerSumIndex].weight;

    float inverseWeightSum = rcp(max(weightSum, 1e-6f));
    [unroll]
    for (int allLayerNormalizeIndex = 0; allLayerNormalizeIndex < SOL_LANDSCAPE_LAYER_COUNT; ++allLayerNormalizeIndex)
        samples[allLayerNormalizeIndex].weight *= inverseWeightSum;
}

void SolHeightBlendLandscapeAllLayers(
    inout SolLandscapeLayerSample samples[SOL_LANDSCAPE_LAYER_COUNT],
    bool normalizeResult)
{
    float maxSplatHeight = -1.0f;
    [unroll]
    for (int allLayerMaximumIndex = 0; allLayerMaximumIndex < SOL_LANDSCAPE_LAYER_COUNT; ++allLayerMaximumIndex)
    {
        float splatHeight = (float)SolLandscapeHeight(samples[allLayerMaximumIndex].noh)
            * samples[allLayerMaximumIndex].weight;
        maxSplatHeight = max(maxSplatHeight, splatHeight);
    }

    float transition = max(_Sol_LandscapeHeightTransition, 1e-5f);
    float weightedSum = 0.0f;
    [unroll]
    for (int allLayerBlendIndex = 0; allLayerBlendIndex < SOL_LANDSCAPE_LAYER_COUNT; ++allLayerBlendIndex)
    {
        float paintedWeight = samples[allLayerBlendIndex].weight;
        float splatHeight = (float)SolLandscapeHeight(samples[allLayerBlendIndex].noh) * paintedWeight;
        float weightedHeight = max(0.0f, splatHeight + transition - maxSplatHeight);
        weightedHeight = (weightedHeight + 1e-6f) * paintedWeight;
        samples[allLayerBlendIndex].weight = weightedHeight;
        weightedSum += weightedHeight;
    }

    if (normalizeResult)
    {
        float inverseWeightedSum = rcp(max(weightedSum, 1e-6f));
        [unroll]
        for (int allLayerFinalIndex = 0; allLayerFinalIndex < SOL_LANDSCAPE_LAYER_COUNT; ++allLayerFinalIndex)
            samples[allLayerFinalIndex].weight *= inverseWeightedSum;
    }
}

void SolSelectLandscapeTopKFromSamples(
    SolLandscapeLayerSample candidates[SOL_LANDSCAPE_LAYER_COUNT],
    out SolLandscapeLayerSample selectedSamples[SOL_LANDSCAPE_TOP_K])
{
    [unroll]
    for (int initializationSlot = 0; initializationSlot < SOL_LANDSCAPE_TOP_K; ++initializationSlot)
    {
        selectedSamples[initializationSlot].weight = -1.0f;
        selectedSamples[initializationSlot].layerIndex = 0;
        selectedSamples[initializationSlot].noh = 0.0h;
    }

    [unroll]
    for (int layerIndex = 0; layerIndex < SOL_LANDSCAPE_LAYER_COUNT; ++layerIndex)
    {
        SolLandscapeLayerSample candidate = candidates[layerIndex];
        [unroll]
        for (int insertionSlot = 0; insertionSlot < SOL_LANDSCAPE_TOP_K; ++insertionSlot)
        {
            if (candidate.weight > selectedSamples[insertionSlot].weight)
            {
                SolLandscapeLayerSample displaced = selectedSamples[insertionSlot];
                selectedSamples[insertionSlot] = candidate;
                candidate = displaced;
            }
        }
    }
}

void SolAccumulateLandscapeLayer(
    float2 terrainUV,
    SolLandscapeLayerSample sample,
    inout half3 albedo,
    inout half smoothness,
    inout half occlusion,
    inout half3 normalTS,
    inout half postBlendDebugWeight)
{
    int layerIndex = sample.layerIndex;
    half weight = (half)sample.weight;
    half4 cs = SAMPLE_TEXTURE2D_ARRAY(
        _Sol_LandscapeCS,
        sampler_Sol_LandscapeCS,
        SolLandscapeLayerUV(terrainUV, layerIndex),
        layerIndex);
    half4 noh = sample.noh;

    albedo += cs.rgb * weight;
    smoothness += cs.a * weight;
    occlusion += noh.b * weight;
    normalTS += SolDecodeLandscapeNormalTS(noh, _Sol_LandscapeNormalScale[layerIndex]) * weight;

    if (abs(_Sol_LandscapeWeightDebugLayer - (float)layerIndex) < 0.25f)
        postBlendDebugWeight = weight;
}

SolLandscapeSurface SolEvaluateLandscapeSurface(
    float2 terrainUV,
    float3 positionWS,
    half3 geometricNormalWS)
{
    SolLandscapeRawWeights rawWeights = SolDecodeLandscapeRawWeights(terrainUV);
    float resolvedWeights[SOL_LANDSCAPE_LAYER_COUNT];
    [unroll]
    for (int resolveInputIndex = 0; resolveInputIndex < SOL_LANDSCAPE_LAYER_COUNT; ++resolveInputIndex)
        resolvedWeights[resolveInputIndex] = SolSelectLandscapeRawWeight(rawWeights, resolveInputIndex);

    // Resolve every authored layer before paint-ranked top-K. This contract is ALU-only;
    // Phase 4B supplies procedural inputs without adding texture fetches here.
    float2 manualAutoDebugWeights;
    SolResolveLandscapeAutoMaterial(
        resolvedWeights,
        positionWS,
        geometricNormalWS,
        manualAutoDebugWeights);
    float resolvedAutoDebugWeight = resolvedWeights[2] + resolvedWeights[3];
    float resolvedPathDebugWeight = resolvedWeights[4];

    half3 albedo = 0.0h;
    half smoothness = 0.0h;
    half occlusion = 0.0h;
    half3 normalTS = 0.0h;
    half postBlendDebugWeight = 0.0h;
    float weatherSnowSusceptibility = 0.0f;
    float permanentSnowSusceptibility = 0.0f;

#ifdef _SOL_LANDSCAPE_TOPK_REFERENCE
    // Ticket 2C reference path: all six layers, unsorted, retained for validation diffs.
    SolLandscapeLayerSample allLayers[SOL_LANDSCAPE_LAYER_COUNT];
    SolBuildLandscapeAllSamples(terrainUV, resolvedWeights, allLayers);

    #ifdef _SOL_LANDSCAPE_BLEND_HEIGHT
        SolHeightBlendLandscapeAllLayers(allLayers, true);
    #else
        SolNormalizeLandscapeAllLayers(allLayers);
    #endif

    [unroll]
    for (int layerIndex = 0; layerIndex < SOL_LANDSCAPE_LAYER_COUNT; ++layerIndex)
    {
        weatherSnowSusceptibility += allLayers[layerIndex].weight
            * _Sol_LandscapeWeatherSnowSusceptibilities[allLayers[layerIndex].layerIndex];
        permanentSnowSusceptibility += allLayers[layerIndex].weight
            * _Sol_LandscapePermanentSnowSusceptibilities[allLayers[layerIndex].layerIndex];
        SolAccumulateLandscapeLayer(
            terrainUV,
            allLayers[layerIndex],
            albedo,
            smoothness,
            occlusion,
            normalTS,
            postBlendDebugWeight);
    }
#else
    SolLandscapeLayerSample selectedSamples[SOL_LANDSCAPE_TOP_K];

    #if defined(_SOL_LANDSCAPE_BLEND_HEIGHT) && defined(_SOL_LANDSCAPE_HEIGHT_ALL_LAYERS_REFERENCE)
        // Option B reference: form the all-layer height-weighted numerators, select by them,
        // then normalize the kept K once. The omitted all-layer denominator is common to every
        // candidate and therefore cannot change the ranking.
        SolLandscapeLayerSample allLayerCandidates[SOL_LANDSCAPE_LAYER_COUNT];
        SolBuildLandscapeAllSamples(terrainUV, resolvedWeights, allLayerCandidates);
        SolHeightBlendLandscapeAllLayers(allLayerCandidates, false);
        SolSelectLandscapeTopKFromSamples(allLayerCandidates, selectedSamples);
        SolNormalizeLandscapeTopK(selectedSamples);
    #else
        // Shipping Option A: select by unnormalized painted weight, then height-blend within K.
        SolLandscapeLayerWeight selectedLayers[SOL_LANDSCAPE_TOP_K];
        SolSelectLandscapeTopK(resolvedWeights, selectedLayers);
        SolPopulateLandscapeTopKSamples(terrainUV, selectedLayers, selectedSamples);

        #ifdef _SOL_LANDSCAPE_BLEND_HEIGHT
            SolHeightBlendLandscapeTopK(selectedSamples);
        #else
            SolNormalizeLandscapeTopK(selectedSamples);
        #endif
    #endif

    [unroll]
    for (int selectedIndex = 0; selectedIndex < SOL_LANDSCAPE_TOP_K; ++selectedIndex)
    {
        weatherSnowSusceptibility += selectedSamples[selectedIndex].weight
            * _Sol_LandscapeWeatherSnowSusceptibilities[selectedSamples[selectedIndex].layerIndex];
        permanentSnowSusceptibility += selectedSamples[selectedIndex].weight
            * _Sol_LandscapePermanentSnowSusceptibilities[selectedSamples[selectedIndex].layerIndex];
        SolAccumulateLandscapeLayer(
            terrainUV,
            selectedSamples[selectedIndex],
            albedo,
            smoothness,
            occlusion,
            normalTS,
            postBlendDebugWeight);
    }
#endif

    // Match stock TerrainLit protection against a zero-length blended tangent-space normal.
#if !HALF_IS_FLOAT
    normalTS.z += 0.01h;
#else
    normalTS.z += 1e-5f;
#endif

    SolLandscapeSurface result = (SolLandscapeSurface)0;
    result.surfaceData.albedo = albedo;
    result.surfaceData.metallic = 0.0h;
    result.surfaceData.specular = 0.0h;
    result.surfaceData.smoothness = smoothness;
    result.surfaceData.normalTS = normalize(normalTS);
    result.surfaceData.occlusion = occlusion;
    result.surfaceData.emission = 0.0h;
    result.surfaceData.alpha = 1.0h;
    result.surfaceData.clearCoatMask = 0.0h;
    result.surfaceData.clearCoatSmoothness = 0.0h;
    result.postBlendDebugWeight = postBlendDebugWeight;
    result.manualAutoDebugWeights = (half2)manualAutoDebugWeights;
    result.resolvedAutoDebugWeight = (half)resolvedAutoDebugWeight;
    result.resolvedPathDebugWeight = (half)resolvedPathDebugWeight;
    float snowCoverage;
    // Overlay ordering is deliberate: material resolve/top-K/height blend are complete,
    // then snow modifies the assembled surface, and the shared 4D wetness function runs later.
    SolApplyLandscapeSnow(
        result.surfaceData,
        positionWS,
        geometricNormalWS,
        weatherSnowSusceptibility,
        permanentSnowSusceptibility,
        snowCoverage);
    result.snowCoverage = (half)snowCoverage;
    return result;
}

void SolResolveLandscapeFrame(
    float2 terrainUV,
    half3 interpolatedNormalWS,
    half3 interpolatedTangentWS,
    half3 interpolatedBitangentWS,
    out half3 geometricNormalWS,
    out half3 tangentWS,
    out half3 bitangentWS)
{
#ifdef ENABLE_TERRAIN_PERPIXEL_NORMAL
    float2 sampleCoords =
        (terrainUV / _TerrainHeightmapRecipSize.zw + 0.5f)
        * _TerrainHeightmapRecipSize.xy;
    half3 geometricNormalOS = normalize(SAMPLE_TEXTURE2D(
        _TerrainNormalmapTexture,
        sampler_TerrainNormalmapTexture,
        sampleCoords).rgb * 2.0h - 1.0h);
    geometricNormalWS = TransformObjectToWorldNormal(geometricNormalOS);
    tangentWS = cross(GetObjectToWorldMatrix()._13_23_33, geometricNormalWS);
    bitangentWS = cross(geometricNormalWS, tangentWS);
#else
    geometricNormalWS = NormalizeNormalPerPixel(interpolatedNormalWS);
    tangentWS = normalize(interpolatedTangentWS);
    bitangentWS = normalize(interpolatedBitangentWS);
#endif
}

void SolInitializeInputData(
    SolTerrainArrayVaryings input,
    half3 normalTS,
    half3 geometricNormalWS,
    half3 tangentWS,
    half3 bitangentWS,
    out InputData inputData)
{
    inputData = (InputData)0;
    inputData.positionWS = input.positionWS;
    inputData.positionCS = input.positionCS;

    inputData.tangentToWorld = half3x3(-tangentWS, bitangentWS, geometricNormalWS);
    inputData.normalWS = NormalizeNormalPerPixel(
        TransformTangentToWorld(normalTS, inputData.tangentToWorld));
    inputData.viewDirectionWS = normalize(half3(
        input.normalWSAndViewX.w,
        input.tangentWSAndViewY.w,
        input.bitangentWSAndViewZ.w));

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    inputData.shadowCoord = input.shadowCoord;
#elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
#else
    inputData.shadowCoord = float4(0.0f, 0.0f, 0.0f, 0.0f);
#endif

    // Sol atmosphere is screen-space; terrain deliberately carries no URP fog variant or MixFog.
    inputData.fogCoord = 0.0h;

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    inputData.vertexLighting = input.vertexLighting;
#endif

    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

#if defined(DEBUG_DISPLAY)
    #if defined(DYNAMICLIGHTMAP_ON)
    inputData.dynamicLightmapUV = input.dynamicLightmapUV;
    #endif
    #if defined(LIGHTMAP_ON)
    inputData.staticLightmapUV = input.staticLightmapUV;
    #else
    inputData.vertexSH = input.vertexSH;
    #endif
    #if defined(USE_APV_PROBE_OCCLUSION)
    inputData.probeOcclusion = input.probeOcclusion;
    #endif
#endif
}

void SolInitializeBakedGIData(SolTerrainArrayVaryings input, inout InputData inputData)
{
#if defined(_SCREEN_SPACE_IRRADIANCE)
    inputData.bakedGI = SAMPLE_GI(_ScreenSpaceIrradiance, inputData.positionCS.xy);
#elif defined(DYNAMICLIGHTMAP_ON)
    inputData.bakedGI = SAMPLE_GI(
        input.staticLightmapUV,
        input.dynamicLightmapUV,
        input.vertexSH,
        inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
#elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
    inputData.bakedGI = SAMPLE_GI(
        input.vertexSH,
        GetAbsolutePositionWS(inputData.positionWS),
        inputData.normalWS,
        inputData.viewDirectionWS,
        inputData.positionCS.xy,
        input.probeOcclusion,
        inputData.shadowMask);
#else
    inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
#endif
}

void SolTerrainArrayFragment(
    SolTerrainArrayVaryings input,
    out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

#ifdef _ALPHATEST_ON
    SolClipTerrainHoles(input.terrainUV);
#endif

    half3 geometricNormalWS;
    half3 tangentWS;
    half3 bitangentWS;
    SolResolveLandscapeFrame(
        input.terrainUV,
        input.normalWSAndViewX.xyz,
        input.tangentWSAndViewY.xyz,
        input.bitangentWSAndViewZ.xyz,
        geometricNormalWS,
        tangentWS,
        bitangentWS);
    SolLandscapeSurface landscape = SolEvaluateLandscapeSurface(
        input.terrainUV,
        input.positionWS,
        geometricNormalWS);

#ifdef _SOL_LANDSCAPE_DEBUG
    // One keyword owns every landscape diagnostic. Modes 1-2 retain the reviewed 4A
    // layer and Manual/Auto views. Mode 3 exposes pre-top-K resolved Stone weight for
    // 4B threshold sweeps and motion stability; mode 4 exposes pre-top-K Path weight
    // at the exact point where the reviewed paint-authority contract applies; mode 5
    // shows final post-rule snow coverage as grayscale.
    if (_Sol_LandscapeDebugMode >= 0.5f)
    {
        if (_Sol_LandscapeDebugMode < (float)SOL_LANDSCAPE_DEBUG_MANUAL_AUTO_SPLIT - 0.5f)
            outColor = half4(landscape.postBlendDebugWeight.xxx, 1.0h);
        else if (_Sol_LandscapeDebugMode < (float)SOL_LANDSCAPE_DEBUG_RESOLVED_STONE - 0.5f)
            outColor = half4(landscape.manualAutoDebugWeights, 0.0h, 1.0h);
        else if (_Sol_LandscapeDebugMode < (float)SOL_LANDSCAPE_DEBUG_RESOLVED_PATH - 0.5f)
            outColor = half4(landscape.resolvedAutoDebugWeight.xxx, 1.0h);
        else if (_Sol_LandscapeDebugMode < (float)SOL_LANDSCAPE_DEBUG_SNOW_COVERAGE - 0.5f)
            outColor = half4(landscape.resolvedPathDebugWeight.xxx, 1.0h);
        else
            outColor = half4(landscape.snowCoverage.xxx, 1.0h);
#ifdef _WRITE_RENDERING_LAYERS
        outRenderingLayers = EncodeMeshRenderingLayer();
#endif
        return;
    }
#endif

    InputData inputData;
    SolInitializeInputData(
        input,
        landscape.surfaceData.normalTS,
        geometricNormalWS,
        tangentWS,
        bitangentWS,
        inputData);
    SolInitializeBakedGIData(input, inputData);

    // geometricNormalWS intentionally remains distinct from inputData.normalWS for Phase 4 slope rules.
    SolApplyTerrainWetness(
        inputData.positionWS,
        landscape.surfaceData.albedo,
        landscape.surfaceData.metallic,
        landscape.surfaceData.smoothness,
        landscape.surfaceData.occlusion);
    half4 color = UniversalFragmentPBR(inputData, landscape.surfaceData);
    outColor = half4(color.rgb, 1.0h);

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

void SolTerrainArrayBaseFragment(
    SolTerrainArrayVaryings input,
    out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

#ifdef _ALPHATEST_ON
    SolClipTerrainHoles(input.terrainUV);
#endif

    half4 albedoSmoothness = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.terrainUV);

    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo = albedoSmoothness.rgb;
    surfaceData.metallic = 0.0h;
    surfaceData.specular = 0.0h;
    surfaceData.smoothness = albedoSmoothness.a;
    surfaceData.normalTS = half3(0.0h, 0.0h, 1.0h);
    surfaceData.occlusion = 1.0h;
    surfaceData.emission = 0.0h;
    surfaceData.alpha = 1.0h;
    surfaceData.clearCoatMask = 0.0h;
    surfaceData.clearCoatSmoothness = 0.0h;

    InputData inputData;
    half3 geometricNormalWS;
    half3 tangentWS;
    half3 bitangentWS;
    SolResolveLandscapeFrame(
        input.terrainUV,
        input.normalWSAndViewX.xyz,
        input.tangentWSAndViewY.xyz,
        input.bitangentWSAndViewZ.xyz,
        geometricNormalWS,
        tangentWS,
        bitangentWS);
    SolInitializeInputData(
        input,
        surfaceData.normalTS,
        geometricNormalWS,
        tangentWS,
        bitangentWS,
        inputData);
    SolInitializeBakedGIData(input, inputData);

    SolApplyTerrainWetness(
        inputData.positionWS,
        surfaceData.albedo,
        surfaceData.metallic,
        surfaceData.smoothness,
        surfaceData.occlusion);
    half4 color = UniversalFragmentPBR(inputData, surfaceData);
    outColor = half4(color.rgb, 1.0h);

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

// ShadowCaster and DepthOnly retain the stock URP 17.3 terrain geometry path.
float3 _LightDirection;
float3 _LightPosition;

struct SolTerrainArrayLeanAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float2 terrainUV : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct SolTerrainArrayLeanVaryings
{
    float4 positionCS : SV_POSITION;
    float2 terrainUV : TEXCOORD0;
    UNITY_VERTEX_OUTPUT_STEREO
};

SolTerrainArrayLeanVaryings SolTerrainArrayShadowVertex(SolTerrainArrayLeanAttributes input)
{
    SolTerrainArrayLeanVaryings output = (SolTerrainArrayLeanVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    SolTerrainInstancing(input.positionOS, input.normalOS, input.terrainUV);

    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

#if UNITY_REVERSED_Z
    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif

    output.positionCS = positionCS;
    output.terrainUV = input.terrainUV;
    return output;
}

half4 SolTerrainArrayShadowFragment(SolTerrainArrayLeanVaryings input) : SV_Target
{
#ifdef _ALPHATEST_ON
    SolClipTerrainHoles(input.terrainUV);
#endif
    return 0.0h;
}

SolTerrainArrayLeanVaryings SolTerrainArrayDepthOnlyVertex(SolTerrainArrayLeanAttributes input)
{
    SolTerrainArrayLeanVaryings output = (SolTerrainArrayLeanVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    SolTerrainInstancing(input.positionOS, input.normalOS, input.terrainUV);

    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    output.terrainUV = input.terrainUV;
    return output;
}

half4 SolTerrainArrayDepthOnlyFragment(SolTerrainArrayLeanVaryings input) : SV_Target
{
#ifdef _ALPHATEST_ON
    SolClipTerrainHoles(input.terrainUV);
#endif

#ifdef SCENESELECTIONPASS
    return half4(_ObjectId, _PassValue, 1.0h, 1.0h);
#else
    return input.positionCS.z;
#endif
}

struct SolTerrainArrayDepthNormalsVaryings
{
    float2 terrainUV : TEXCOORD0;
    half3 normalWS : TEXCOORD1;
    half3 tangentWS : TEXCOORD2;
    half3 bitangentWS : TEXCOORD3;
    float3 positionWS : TEXCOORD4;
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

SolTerrainArrayDepthNormalsVaryings SolTerrainArrayDepthNormalsVertex(SolTerrainArrayAttributes input)
{
    SolTerrainArrayDepthNormalsVaryings output = (SolTerrainArrayDepthNormalsVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    SolTerrainInstancing(input.positionOS, input.normalOS, input.terrainUV);

    float4 tangentOS = float4(cross(float3(0.0f, 0.0f, 1.0f), input.normalOS), 1.0f);
    VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, tangentOS);

    output.terrainUV = input.terrainUV;
    output.normalWS = normalInputs.normalWS;
    output.tangentWS = normalInputs.tangentWS;
    output.bitangentWS = normalInputs.bitangentWS;
    output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    return output;
}

void SolTerrainArrayDepthNormalsFragment(
    SolTerrainArrayDepthNormalsVaryings input,
    out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
#ifdef _ALPHATEST_ON
    SolClipTerrainHoles(input.terrainUV);
#endif

    half3 geometricNormalWS;
    half3 tangentWS;
    half3 bitangentWS;
    SolResolveLandscapeFrame(
        input.terrainUV,
        input.normalWS,
        input.tangentWS,
        input.bitangentWS,
        geometricNormalWS,
        tangentWS,
        bitangentWS);
    SolLandscapeSurface landscape = SolEvaluateLandscapeSurface(
        input.terrainUV,
        input.positionWS,
        geometricNormalWS);

    half3 detailNormalWS = TransformTangentToWorld(
        landscape.surfaceData.normalTS,
        half3x3(-tangentWS, bitangentWS, geometricNormalWS));
    outNormalWS = half4(NormalizeNormalPerPixel(detailNormalWS), 0.0h);

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

void SolTerrainArrayBaseDepthNormalsFragment(
    SolTerrainArrayDepthNormalsVaryings input,
    out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
#ifdef _ALPHATEST_ON
    SolClipTerrainHoles(input.terrainUV);
#endif

    half3 geometricNormalWS;
    half3 tangentWS;
    half3 bitangentWS;
    SolResolveLandscapeFrame(
        input.terrainUV,
        input.normalWS,
        input.tangentWS,
        input.bitangentWS,
        geometricNormalWS,
        tangentWS,
        bitangentWS);
    outNormalWS = half4(NormalizeNormalPerPixel(geometricNormalWS), 0.0h);

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif
