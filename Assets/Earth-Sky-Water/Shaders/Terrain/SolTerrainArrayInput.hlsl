#ifndef SOL_TERRAIN_ARRAY_INPUT_INCLUDED
#define SOL_TERRAIN_ARRAY_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

#define _Surface 0.0
#define SOL_LANDSCAPE_MAX_LAYERS 16

CBUFFER_START(UnityPerMaterial)
    float _Sol_LandscapeWeightDebugLayer;
    float _Sol_LandscapeDebugMode;
    float4 _MainTex_ST;
    half4 _BaseColor;
CBUFFER_END

CBUFFER_START(_Terrain)
    #ifdef UNITY_INSTANCING_ENABLED
        // Supplied by Unity's terrain instancing path.
        float4 _TerrainHeightmapRecipSize;
        float4 _TerrainHeightmapScale;
    #endif

    #ifdef SCENESELECTIONPASS
        int _ObjectId;
        int _PassValue;
    #endif
CBUFFER_END

TEXTURE2D(_Sol_LandscapeControl0);
SAMPLER(sampler_Sol_LandscapeControl0);
TEXTURE2D(_Sol_LandscapeControl1);
SAMPLER(sampler_Sol_LandscapeControl1);
TEXTURE2D_ARRAY(_Sol_LandscapeCS);
SAMPLER(sampler_Sol_LandscapeCS);
TEXTURE2D_ARRAY(_Sol_LandscapeNOH);
SAMPLER(sampler_Sol_LandscapeNOH);
TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);
TEXTURE2D(_MetallicTex);
SAMPLER(sampler_MetallicTex);
TEXTURE2D(_Sol_LandscapeSnowColor);
SAMPLER(sampler_Sol_LandscapeSnowColor);
TEXTURE2D(_Sol_LandscapeSnowNormal);
SAMPLER(sampler_Sol_LandscapeSnowNormal);
TEXTURE2D(_Sol_LandscapeSnowPacked);
SAMPLER(sampler_Sol_LandscapeSnowPacked);

float4 _Sol_LandscapeControlTexelSize;
float4 _Sol_LandscapeLayerST[SOL_LANDSCAPE_MAX_LAYERS];
float _Sol_LandscapeNormalScale[SOL_LANDSCAPE_MAX_LAYERS];
float _Sol_LandscapeLayerModes[SOL_LANDSCAPE_MAX_LAYERS];
float _Sol_LandscapeAutoWeights[SOL_LANDSCAPE_MAX_LAYERS];
float _Sol_LandscapeWeatherSnowSusceptibilities[SOL_LANDSCAPE_MAX_LAYERS];
float _Sol_LandscapePermanentSnowSusceptibilities[SOL_LANDSCAPE_MAX_LAYERS];
float4 _Sol_LandscapeAutoSlopeParams[SOL_LANDSCAPE_MAX_LAYERS];
// x: slope ceiling in degrees, y: feather width in degrees. A ceiling of 90 is an exact no-op,
// because no surface exceeds 90 degrees and the response curve saturates to zero below its range.
float4 _Sol_LandscapeAutoSlopeCeilingParams[SOL_LANDSCAPE_MAX_LAYERS];
// 1 when the layer opted into stochastic tiling, 0 otherwise.
float _Sol_LandscapeStochastic[SOL_LANDSCAPE_MAX_LAYERS];
// 1 when the layer opted into triplanar projection, 0 for top-down planar only.
float _Sol_LandscapeTriplanar[SOL_LANDSCAPE_MAX_LAYERS];
// Exponent on the projection weights. Higher keeps the top-down plane dominant for longer and
// narrows the band where a face pays for more than one projection.
float _Sol_LandscapeTriplanarSharpness;
float _Sol_LandscapeAutoAltitudeReferences[SOL_LANDSCAPE_MAX_LAYERS];
float4 _Sol_LandscapeAutoHeightParams[SOL_LANDSCAPE_MAX_LAYERS];
float4 _Sol_LandscapeAutoCavityParams[SOL_LANDSCAPE_MAX_LAYERS];
int _Sol_LandscapeLayerCount;
float4 _Sol_LandscapeTerrainOriginSize;
float _Sol_LandscapeHeightTransition;
// x/y: permanent Snow absolute world-Y range; z: inverse tile size; w: normal scale.
float4 _Sol_LandscapeSnowParams;
// x/y: accumulated permanent Snow retention-to-shedding slope range in degrees.
float2 _Sol_LandscapePermanentSnowSlopeSheddingRange;
// Integrated surface climate from SolEnvironmentWorld, consumed by the snow overlay.
float _Sol_SurfaceSnowCover;
float _Sol_SurfaceTemperature;
#if defined(UNITY_INSTANCING_ENABLED)
    // The custom terrain path always uses Unity's per-pixel geometric normal when instanced.
    #define ENABLE_TERRAIN_PERPIXEL_NORMAL 1

    TEXTURE2D(_TerrainHeightmapTexture);
    TEXTURE2D(_TerrainNormalmapTexture);
    SAMPLER(sampler_TerrainNormalmapTexture);
#endif

UNITY_INSTANCING_BUFFER_START(Terrain)
    UNITY_DEFINE_INSTANCED_PROP(float4, _TerrainPatchInstanceData)
UNITY_INSTANCING_BUFFER_END(Terrain)

#ifdef _ALPHATEST_ON
    TEXTURE2D(_TerrainHolesTexture);
    SAMPLER(sampler_TerrainHolesTexture);

    float SolSampleTerrainHole(float2 uv)
    {
        return SAMPLE_TEXTURE2D(_TerrainHolesTexture, sampler_TerrainHolesTexture, uv).r;
    }

    void SolClipTerrainHoles(float2 uv)
    {
        const float epsilon = 0.0005f;
        clip(SolSampleTerrainHole(uv) < epsilon ? -1.0f : 1.0f);
    }
#endif

void SolTerrainInstancing(inout float4 positionOS, inout float3 normalOS, inout float2 terrainUV)
{
#ifdef UNITY_INSTANCING_ENABLED
    float2 patchVertex = positionOS.xy;
    float4 instanceData = UNITY_ACCESS_INSTANCED_PROP(Terrain, _TerrainPatchInstanceData);
    float2 sampleCoords = (patchVertex + instanceData.xy) * instanceData.z;
    float height = UnpackHeightmap(_TerrainHeightmapTexture.Load(int3(sampleCoords, 0)));

    positionOS.xz = sampleCoords * _TerrainHeightmapScale.xz;
    positionOS.y = height * _TerrainHeightmapScale.y;

    normalOS = float3(0.0f, 1.0f, 0.0f);

    terrainUV = sampleCoords * _TerrainHeightmapRecipSize.zw;
#endif
}

void SolTerrainInstancing(inout float4 positionOS, inout float3 normalOS)
{
    float2 terrainUV = float2(0.0f, 0.0f);
    SolTerrainInstancing(positionOS, normalOS, terrainUV);
}

#endif
