#ifndef SOL_TERRAIN_ARRAY_PASSES_INCLUDED
#define SOL_TERRAIN_ARRAY_PASSES_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "SolTerrainWetness.hlsl"

#define SOL_LANDSCAPE_LAYER_COUNT 6
#define SOL_LANDSCAPE_DEBUG_LAYER_WEIGHT 1
#define SOL_LANDSCAPE_DEBUG_MANUAL_AUTO_SPLIT 2
#define SOL_LANDSCAPE_DEBUG_RESOLVED_AUTO 3
#define SOL_LANDSCAPE_DEBUG_SNOW_COVERAGE 4

// A projection contributing less than this fraction of the dominant one is dropped. Applied as a
// subtraction before renormalising, so the weight reaches zero continuously and the skip is exact.
static const float kSolLandscapeTriplanarCull = 0.06f;

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

// Per-layer authored height, packed into NOH alpha by the array baker. Drives height blending.
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
    half snowCoverage;
};

struct SolLandscapeLayerSample
{
    float weight;
    int layerIndex;
    half4 noh;
    // Resolved at sample time rather than at accumulation time, because a triplanar layer needs all
    // of its projections in hand to reorient the normal and they are not recoverable from the blend.
    half3 normalTS;
};

#ifdef _SOL_LANDSCAPE_STOCHASTIC
// Triangle-grid stochastic tiling, three taps per layer per array, enabled per layer by the config.
// Each of the three nearest lattice cells samples the SAME texture through its own per-cell
// rotation/flip/offset, and the three taps are blended by barycentric weight. The per-cell transform
// including a rotation or flip (not just an offset) is what disrupts a strongly directional texture;
// offset alone only re-phases a repeat, it does not change which way its stripes point.
// Reference: Heitz & Neyret's histogram-preserving-blend triangle-grid technique as prior art only --
// this is an independent implementation, not a port of any specific source.

float SolLandscapeStochasticHash(float2 cell, float salt)
{
    float3 p = float3(cell, salt);
    return frac(sin(dot(p, float3(12.9898f, 78.233f, 37.719f))) * 43758.5453123f);
}

float2 SolLandscapeStochasticHash2(float2 cell, float salt)
{
    return float2(
        SolLandscapeStochasticHash(cell, salt),
        SolLandscapeStochasticHash(cell, salt + 91.7f));
}

// One of the 8 symmetries of a square (4 rotations, each optionally mirrored first). Built from
// integer sign/swap logic rather than sin/cos so the four rotated variants are exact.
float2x2 SolLandscapeDihedralMatrix(uint index)
{
    uint rotation = index & 3u;
    float2x2 result = float2x2(1.0f, 0.0f, 0.0f, 1.0f);
    if (rotation == 1u)      result = float2x2(0.0f, -1.0f, 1.0f, 0.0f);
    else if (rotation == 2u) result = float2x2(-1.0f, 0.0f, 0.0f, -1.0f);
    else if (rotation == 3u) result = float2x2(0.0f, 1.0f, -1.0f, 0.0f);
    if ((index & 4u) != 0u)
        result = mul(result, float2x2(-1.0f, 0.0f, 0.0f, 1.0f));
    return result;
}

struct SolLandscapeStochasticCell
{
    float2 id;
    float weight;
};

// Splits the UV plane into unit cells, each cut along one diagonal into two triangles, and returns
// the three cell corners surrounding the sample point with their barycentric weights (sum to 1).
void SolLandscapeTriangleGridCells(float2 grid, out SolLandscapeStochasticCell cells[3])
{
    float2 origin = floor(grid);
    float2 fractional = grid - origin;
    if (fractional.x + fractional.y < 1.0f)
    {
        cells[0].id = origin;                       cells[0].weight = 1.0f - fractional.x - fractional.y;
        cells[1].id = origin + float2(1.0f, 0.0f);  cells[1].weight = fractional.x;
        cells[2].id = origin + float2(0.0f, 1.0f);  cells[2].weight = fractional.y;
    }
    else
    {
        cells[0].id = origin + float2(1.0f, 1.0f);  cells[0].weight = fractional.x + fractional.y - 1.0f;
        cells[1].id = origin + float2(1.0f, 0.0f);  cells[1].weight = 1.0f - fractional.y;
        cells[2].id = origin + float2(0.0f, 1.0f);  cells[2].weight = 1.0f - fractional.x;
    }
}

struct SolLandscapeStochasticTap
{
    float2 uv;
    float2 ddxUV;
    float2 ddyUV;
    float weight;
};

// Builds the three (UV, explicit gradient, weight) taps for one layer at one pixel. Screen-space
// derivatives of terrainUV are passed in rather than taken here: this runs under a per-layer branch,
// and a derivative inside flow control is undefined. Gradients are carried explicitly (not left to
// the hardware's implicit ddx/ddy) because the per-cell rotation is a discontinuous function of
// screen position: an implicit derivative at a cell boundary would blend two unrelated cells'
// derivatives and pick the wrong mip. Rotating the source UV's own derivative by the same per-cell
// matrix keeps the mip selection correct on both sides of every cell boundary.
void SolLandscapeBuildStochasticTaps(
    float2 layerUV,
    float2 ddxLayerUV,
    float2 ddyLayerUV,
    int layerIndex,
    out SolLandscapeStochasticTap taps[3])
{
    SolLandscapeStochasticCell cells[3];
    SolLandscapeTriangleGridCells(layerUV, cells);

    [unroll]
    for (int i = 0; i < 3; ++i)
    {
        float layerSalt = (float)layerIndex * 4.0f;
        float rotationHash = SolLandscapeStochasticHash(cells[i].id, layerSalt);
        float2 offsetHash = SolLandscapeStochasticHash2(cells[i].id, layerSalt + 1.0f);
        uint dihedralIndex = min((uint)(rotationHash * 8.0f), 7u);
        float2x2 transform = SolLandscapeDihedralMatrix(dihedralIndex);

        float2 pivot = cells[i].id + 0.5f;
        float2 centered = layerUV - pivot;
        float2 transformedUV = mul(transform, centered) + pivot + (offsetHash - 0.5f);

        taps[i].uv = transformedUV;
        taps[i].ddxUV = mul(transform, ddxLayerUV);
        taps[i].ddyUV = mul(transform, ddyLayerUV);
        taps[i].weight = cells[i].weight;
    }
}

// True when this layer opted into stochastic tiling. The value is a uniform indexed by an unrolled
// loop constant, so every lane of the wave takes the same side of the branch.
bool SolLandscapeLayerIsStochastic(int layerIndex)
{
    return _Sol_LandscapeStochastic[layerIndex] > 0.5f;
}
#endif

// Everything one pixel needs to address a layer through any projection. Built once per pixel in
// SolEvaluateLandscapeSurface, because the geometric normal and the screen-space derivatives are
// the same for all six layers.
struct SolLandscapeUVContext
{
    float2 terrainUV;
    float2 ddxTerrainUV;
    float2 ddyTerrainUV;
    float3 positionWS;
    float3 ddxPositionWS;
    float3 ddyPositionWS;
    half3 geometricNormalWS;
    half3 tangentWS;
    half3 bitangentWS;
    // Projection weights, summing to one. Under planar-only builds this is unused.
    float3 triplanarWeights;
};

// One projection's addressing for one layer: UV plus the explicit gradients that go with it.
struct SolLandscapePlaneUV
{
    float2 uv;
    float2 ddxUV;
    float2 ddyUV;
};

// The top-down projection, unchanged from the planar path: terrainUV already is world XZ over the
// terrain extents, so this is exactly what the shader sampled before triplanar existed. Flat ground
// therefore renders identically whether or not a layer opts in.
SolLandscapePlaneUV SolLandscapeTopDownPlane(SolLandscapeUVContext ctx, int layerIndex)
{
    float4 layerST = _Sol_LandscapeLayerST[layerIndex];
    SolLandscapePlaneUV plane;
    plane.uv = ctx.terrainUV * layerST.xy + layerST.zw;
    plane.ddxUV = ctx.ddxTerrainUV * layerST.xy;
    plane.ddyUV = ctx.ddyTerrainUV * layerST.xy;
    return plane;
}

#ifdef _SOL_LANDSCAPE_TRIPLANAR
// Reciprocal of the layer's authored tile size in world metres. layerST.xy is terrainSize/tileSize
// and _Sol_LandscapeTerrainOriginSize.zw is 1/terrainSize, so the product is 1/tileSize. The two
// side projections are addressed at that same world frequency, so a layer keeps one texel density
// no matter which way the ground faces.
float2 SolLandscapeWorldFrequency(int layerIndex)
{
    return _Sol_LandscapeLayerST[layerIndex].xy * _Sol_LandscapeTerrainOriginSize.zw;
}

// Projection looking down world X: addresses the surface by world ZY.
SolLandscapePlaneUV SolLandscapeSidePlaneX(SolLandscapeUVContext ctx, int layerIndex)
{
    float2 frequency = SolLandscapeWorldFrequency(layerIndex);
    SolLandscapePlaneUV plane;
    plane.uv = float2(ctx.positionWS.z * frequency.y, ctx.positionWS.y * frequency.x);
    plane.ddxUV = float2(ctx.ddxPositionWS.z * frequency.y, ctx.ddxPositionWS.y * frequency.x);
    plane.ddyUV = float2(ctx.ddyPositionWS.z * frequency.y, ctx.ddyPositionWS.y * frequency.x);
    return plane;
}

// Projection looking down world Z: addresses the surface by world XY.
SolLandscapePlaneUV SolLandscapeSidePlaneZ(SolLandscapeUVContext ctx, int layerIndex)
{
    float2 frequency = SolLandscapeWorldFrequency(layerIndex);
    SolLandscapePlaneUV plane;
    plane.uv = float2(ctx.positionWS.x * frequency.x, ctx.positionWS.y * frequency.y);
    plane.ddxUV = float2(ctx.ddxPositionWS.x * frequency.x, ctx.ddxPositionWS.y * frequency.y);
    plane.ddyUV = float2(ctx.ddyPositionWS.x * frequency.x, ctx.ddyPositionWS.y * frequency.y);
    return plane;
}

bool SolLandscapeLayerIsTriplanar(int layerIndex)
{
    return _Sol_LandscapeTriplanar[layerIndex] > 0.5f;
}

// Sharpened projection weights. Planes that contribute a negligible fraction of the dominant one
// are driven to exactly zero and the rest renormalised, so skipping them is exact rather than an
// approximation: the weight reaches zero continuously, and no seam appears where a plane drops out.
// This is what keeps the common case cheap -- on ground flatter than roughly 30 degrees only the
// top-down projection survives, so a triplanar layer costs exactly what a planar one costs.
float3 SolLandscapeTriplanarWeights(half3 geometricNormalWS)
{
    float3 weights = abs((float3)geometricNormalWS);
    weights = pow(max(weights, 1e-4f), max(_Sol_LandscapeTriplanarSharpness, 1.0f));
    float dominant = max(max(weights.x, weights.y), weights.z);
    weights = max(weights - kSolLandscapeTriplanarCull * dominant, 0.0f);
    return weights * rcp(max(weights.x + weights.y + weights.z, 1e-6f));
}
#endif

// Samples one array through one projection. Stochastic tiling, where the layer opted in, applies
// per projection, so a triplanar stochastic layer stays broken up on its side faces too.
half4 SolSampleLandscapePlane(
    TEXTURE2D_ARRAY_PARAM(arrayTexture, arraySampler),
    SolLandscapePlaneUV plane,
    int layerIndex)
{
#ifdef _SOL_LANDSCAPE_STOCHASTIC
    if (SolLandscapeLayerIsStochastic(layerIndex))
    {
        SolLandscapeStochasticTap taps[3];
        SolLandscapeBuildStochasticTaps(plane.uv, plane.ddxUV, plane.ddyUV, layerIndex, taps);
        half4 stochastic = 0.0h;
        [unroll]
        for (int i = 0; i < 3; ++i)
        {
            stochastic += SAMPLE_TEXTURE2D_ARRAY_GRAD(
                arrayTexture, arraySampler,
                taps[i].uv, layerIndex, taps[i].ddxUV, taps[i].ddyUV) * (half)taps[i].weight;
        }
        return stochastic;
    }
#endif
    return SAMPLE_TEXTURE2D_ARRAY_GRAD(
        arrayTexture, arraySampler, plane.uv, layerIndex, plane.ddxUV, plane.ddyUV);
}

half4 SolSampleLandscapeCS(SolLandscapeUVContext ctx, int layerIndex)
{
#ifdef _SOL_LANDSCAPE_TRIPLANAR
    if (SolLandscapeLayerIsTriplanar(layerIndex))
    {
        float3 weights = ctx.triplanarWeights;
        half4 accumulated = 0.0h;
        if (weights.x > 0.0f)
            accumulated += SolSampleLandscapePlane(
                TEXTURE2D_ARRAY_ARGS(_Sol_LandscapeCS, sampler_Sol_LandscapeCS),
                SolLandscapeSidePlaneX(ctx, layerIndex), layerIndex) * (half)weights.x;
        if (weights.y > 0.0f)
            accumulated += SolSampleLandscapePlane(
                TEXTURE2D_ARRAY_ARGS(_Sol_LandscapeCS, sampler_Sol_LandscapeCS),
                SolLandscapeTopDownPlane(ctx, layerIndex), layerIndex) * (half)weights.y;
        if (weights.z > 0.0f)
            accumulated += SolSampleLandscapePlane(
                TEXTURE2D_ARRAY_ARGS(_Sol_LandscapeCS, sampler_Sol_LandscapeCS),
                SolLandscapeSidePlaneZ(ctx, layerIndex), layerIndex) * (half)weights.z;
        return accumulated;
    }
#endif
    return SolSampleLandscapePlane(
        TEXTURE2D_ARRAY_ARGS(_Sol_LandscapeCS, sampler_Sol_LandscapeCS),
        SolLandscapeTopDownPlane(ctx, layerIndex), layerIndex);
}

#ifdef _SOL_LANDSCAPE_TRIPLANAR
// Whiteout blend. Each projection's tangent-space normal is reoriented onto the geometric normal in
// world space before the three are summed; blending them as raw tangent-space vectors would be wrong,
// because each plane's tangent basis points a different way.
half3 SolLandscapeWhiteoutNormalWS(half3 normalX, half3 normalY, half3 normalZ, half3 g, float3 weights)
{
    half3 orientedX = half3(normalX.xy + g.zy, abs(normalX.z) * g.x).zyx;
    half3 orientedY = half3(normalY.xy + g.xz, abs(normalY.z) * g.y).xzy;
    half3 orientedZ = half3(normalZ.xy + g.xy, abs(normalZ.z) * g.z).xyz;
    return SafeNormalize(
        orientedX * (half)weights.x + orientedY * (half)weights.y + orientedZ * (half)weights.z);
}
#endif

// Resolves a layer's NOH array into the two things the rest of the shader needs: the blended sample,
// whose B carries ambient occlusion and A the authored height that drives height blending, and the
// detail normal already expressed in the surface's own tangent space so downstream code -- the
// per-layer accumulation, the snow overlay, the DepthNormals pass -- is unchanged by the projection.
void SolResolveLandscapeNOH(
    SolLandscapeUVContext ctx,
    int layerIndex,
    out half4 blendedNOH,
    out half3 normalTS)
{
    half normalScale = _Sol_LandscapeNormalScale[layerIndex];

#ifdef _SOL_LANDSCAPE_TRIPLANAR
    if (SolLandscapeLayerIsTriplanar(layerIndex))
    {
        float3 weights = ctx.triplanarWeights;
        half4 nohX = 0.0h;
        half4 nohY = 0.0h;
        half4 nohZ = 0.0h;
        if (weights.x > 0.0f)
            nohX = SolSampleLandscapePlane(
                TEXTURE2D_ARRAY_ARGS(_Sol_LandscapeNOH, sampler_Sol_LandscapeNOH),
                SolLandscapeSidePlaneX(ctx, layerIndex), layerIndex);
        if (weights.y > 0.0f)
            nohY = SolSampleLandscapePlane(
                TEXTURE2D_ARRAY_ARGS(_Sol_LandscapeNOH, sampler_Sol_LandscapeNOH),
                SolLandscapeTopDownPlane(ctx, layerIndex), layerIndex);
        if (weights.z > 0.0f)
            nohZ = SolSampleLandscapePlane(
                TEXTURE2D_ARRAY_ARGS(_Sol_LandscapeNOH, sampler_Sol_LandscapeNOH),
                SolLandscapeSidePlaneZ(ctx, layerIndex), layerIndex);

        blendedNOH = nohX * (half)weights.x + nohY * (half)weights.y + nohZ * (half)weights.z;

        half3 worldNormal = SolLandscapeWhiteoutNormalWS(
            SolDecodeLandscapeNormalTS(nohX, normalScale),
            SolDecodeLandscapeNormalTS(nohY, normalScale),
            SolDecodeLandscapeNormalTS(nohZ, normalScale),
            ctx.geometricNormalWS,
            weights);

        // Back into the surface tangent basis. The basis is (-tangent, bitangent, normal) to match
        // the one SolInitializeInputData and the DepthNormals pass build, and it is orthonormal, so
        // its inverse is three dot products.
        normalTS = half3(
            dot(worldNormal, -ctx.tangentWS),
            dot(worldNormal, ctx.bitangentWS),
            dot(worldNormal, ctx.geometricNormalWS));
        return;
    }
#endif

    blendedNOH = SolSampleLandscapePlane(
        TEXTURE2D_ARRAY_ARGS(_Sol_LandscapeNOH, sampler_Sol_LandscapeNOH),
        SolLandscapeTopDownPlane(ctx, layerIndex), layerIndex);
    normalTS = SolDecodeLandscapeNormalTS(blendedNOH, normalScale);
}

void SolBuildLandscapeAllSamples(
    SolLandscapeUVContext ctx,
    float resolvedWeights[SOL_LANDSCAPE_LAYER_COUNT],
    out SolLandscapeLayerSample samples[SOL_LANDSCAPE_LAYER_COUNT])
{
    [unroll]
    for (int buildLayerIndex = 0; buildLayerIndex < SOL_LANDSCAPE_LAYER_COUNT; ++buildLayerIndex)
    {
        samples[buildLayerIndex].weight = resolvedWeights[buildLayerIndex];
        samples[buildLayerIndex].layerIndex = buildLayerIndex;
        SolResolveLandscapeNOH(
            ctx,
            buildLayerIndex,
            samples[buildLayerIndex].noh,
            samples[buildLayerIndex].normalTS);
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

void SolAccumulateLandscapeLayer(
    SolLandscapeUVContext ctx,
    SolLandscapeLayerSample sample,
    inout half3 albedo,
    inout half smoothness,
    inout half occlusion,
    inout half3 normalTS,
    inout half postBlendDebugWeight)
{
    int layerIndex = sample.layerIndex;
    half weight = (half)sample.weight;
    half4 cs = SolSampleLandscapeCS(ctx, layerIndex);
    half4 noh = sample.noh;

    albedo += cs.rgb * weight;
    smoothness += cs.a * weight;
    occlusion += noh.b * weight;
    normalTS += sample.normalTS * weight;

#ifdef _SOL_LANDSCAPE_DEBUG
    if (abs(_Sol_LandscapeWeightDebugLayer - (float)layerIndex) < 0.25f)
        postBlendDebugWeight = weight;
#endif
}

SolLandscapeSurface SolEvaluateLandscapeSurface(
    float2 terrainUV,
    float3 positionWS,
    half3 geometricNormalWS,
    half3 tangentWS,
    half3 bitangentWS)
{
    SolLandscapeRawWeights rawWeights = SolDecodeLandscapeRawWeights(terrainUV);
    float resolvedWeights[SOL_LANDSCAPE_LAYER_COUNT];
    [unroll]
    for (int resolveInputIndex = 0; resolveInputIndex < SOL_LANDSCAPE_LAYER_COUNT; ++resolveInputIndex)
        resolvedWeights[resolveInputIndex] = SolSelectLandscapeRawWeight(rawWeights, resolveInputIndex);

    // Resolve every authored layer's weight from the live terrain before any sampling. This
    // contract is ALU-only: the procedural rules add no texture fetches of their own, which is
    // what lets them run per pixel every frame instead of being baked into the alphamap.
    float2 manualAutoDebugWeights;
    SolResolveLandscapeAutoMaterial(
        resolvedWeights,
        positionWS,
        geometricNormalWS,
        manualAutoDebugWeights);
    float resolvedAutoDebugWeight = 0.0f;
#ifdef _SOL_LANDSCAPE_DEBUG
    // The resolved weight of whichever layer the Diagnostic Layer property selects, read before
    // height blending. This is the view to watch while tuning a layer's slope/altitude/cavity rules.
    [unroll]
    for (int debugResolveIndex = 0; debugResolveIndex < SOL_LANDSCAPE_LAYER_COUNT; ++debugResolveIndex)
    {
        if (abs(_Sol_LandscapeWeightDebugLayer - (float)debugResolveIndex) < 0.25f)
            resolvedAutoDebugWeight = resolvedWeights[debugResolveIndex];
    }
#endif

    half3 albedo = 0.0h;
    half smoothness = 0.0h;
    half occlusion = 0.0h;
    half3 normalTS = 0.0h;
    half postBlendDebugWeight = 0.0h;
    float weatherSnowSusceptibility = 0.0f;
    float permanentSnowSusceptibility = 0.0f;

    // Every authored layer is evaluated, unsorted. At six layers with one texture sample each
    // this beat selecting a subset, and it keeps the blend complete: no layer is ever dropped.
    // Built once here, outside every per-layer and per-projection branch: a screen-space derivative
    // is only defined in uniform flow, and both stochastic tiling and triplanar sample under
    // conditions. Everything downstream addresses textures through explicit gradients instead.
    SolLandscapeUVContext ctx;
    ctx.terrainUV = terrainUV;
    ctx.ddxTerrainUV = ddx(terrainUV);
    ctx.ddyTerrainUV = ddy(terrainUV);
    ctx.positionWS = positionWS;
    ctx.ddxPositionWS = ddx(positionWS);
    ctx.ddyPositionWS = ddy(positionWS);
    ctx.geometricNormalWS = geometricNormalWS;
    ctx.tangentWS = tangentWS;
    ctx.bitangentWS = bitangentWS;
#ifdef _SOL_LANDSCAPE_TRIPLANAR
    ctx.triplanarWeights = SolLandscapeTriplanarWeights(geometricNormalWS);
#else
    ctx.triplanarWeights = float3(0.0f, 1.0f, 0.0f);
#endif

    SolLandscapeLayerSample allLayers[SOL_LANDSCAPE_LAYER_COUNT];
    SolBuildLandscapeAllSamples(ctx, resolvedWeights, allLayers);

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
            ctx,
            allLayers[layerIndex],
            albedo,
            smoothness,
            occlusion,
            normalTS,
            postBlendDebugWeight);
    }

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
    float snowCoverage;
    // Overlay ordering is deliberate: material resolve and height blend are complete,
    // then snow modifies the assembled surface, and the shared wetness function runs later.
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
    // cross() of two unit vectors is only unit-length when they are perpendicular, so this basis
    // was slightly short wherever the surface tilted toward the object's Z axis, and degenerate
    // on a wall facing it. Triplanar inverts this basis to bring a world normal back into tangent
    // space, and that inverse is only the transpose when the basis is orthonormal.
    tangentWS = SafeNormalize(cross(GetObjectToWorldMatrix()._13_23_33, geometricNormalWS));
    bitangentWS = SafeNormalize(cross(geometricNormalWS, tangentWS));
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
        geometricNormalWS,
        tangentWS,
        bitangentWS);

#ifdef _SOL_LANDSCAPE_DEBUG
    // One keyword owns every landscape diagnostic, and all four views are authoring aids for
    // tuning the live rules. Mode 1 shows the Diagnostic Layer's final blended weight; mode 2
    // shows the Manual/Auto budget split as red/green; mode 3 shows that layer's resolved weight
    // before height blending, which is the one to watch while dragging a slope or altitude rule;
    // mode 4 shows final post-rule snow coverage as grayscale.
    if (_Sol_LandscapeDebugMode >= 0.5f)
    {
        if (_Sol_LandscapeDebugMode < (float)SOL_LANDSCAPE_DEBUG_MANUAL_AUTO_SPLIT - 0.5f)
            outColor = half4(landscape.postBlendDebugWeight.xxx, 1.0h);
        else if (_Sol_LandscapeDebugMode < (float)SOL_LANDSCAPE_DEBUG_RESOLVED_AUTO - 0.5f)
            outColor = half4(landscape.manualAutoDebugWeights, 0.0h, 1.0h);
        else if (_Sol_LandscapeDebugMode < (float)SOL_LANDSCAPE_DEBUG_SNOW_COVERAGE - 0.5f)
            outColor = half4(landscape.resolvedAutoDebugWeight.xxx, 1.0h);
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

    // geometricNormalWS intentionally remains distinct from inputData.normalWS: the auto-material
    // slope and cavity rules must read the terrain surface, not the layer-perturbed normal.
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
        geometricNormalWS,
        tangentWS,
        bitangentWS);

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
