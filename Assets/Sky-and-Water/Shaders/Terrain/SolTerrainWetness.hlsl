#ifndef SOL_TERRAIN_WETNESS_INCLUDED
#define SOL_TERRAIN_WETNESS_INCLUDED

// _Sol_TerrainWetness: x = rain response, y = water response, z = shoreline
// fade distance in world units, w = maximum wet-sand albedo darkening.
float4 _Sol_TerrainWetness;
float4 _Sol_TerrainSandChannel;
float4 _Sol_TerrainOriginInvSize;
float _Sol_RainIntensity;
// Smoothed ground wetness: accumulates while it rains and dries slowly afterwards.
// Distinct from _Sol_RainIntensity, which is instantaneous precipitation and is what
// the legacy water shader wants for its ripple and roughness response. Driving the
// terrain from the instantaneous value made the ground snap dry the moment rain
// stopped, and left SolEnvironmentWorld's wetness integrator with no consumer at all.
float _Sol_SurfaceWetness;
float _Sol_GlobalWaterLevel;
float _Sol_TerrainWetSmoothness;
TEXTURE2D(_Sol_TerrainSandMask);
SAMPLER(sampler_Sol_TerrainSandMask);

float SolTerrainWetness(float3 positionWS)
{
    float shorelineRange = max(_Sol_TerrainWetness.z, 0.001);
    float waterProximity = saturate(
        (_Sol_GlobalWaterLevel + shorelineRange - positionWS.y) / shorelineRange);
    float rainWetness = saturate(_Sol_SurfaceWetness * _Sol_TerrainWetness.x);
    float waterWetness = waterProximity * saturate(_Sol_TerrainWetness.y);
    return saturate(rainWetness + waterWetness);
}

half SolTerrainSandWeight(float3 positionWS)
{
    float2 terrainUV = (positionWS.xz - _Sol_TerrainOriginInvSize.xy)
                     * _Sol_TerrainOriginInvSize.zw;
    half4 layerWeights = SAMPLE_TEXTURE2D(
        _Sol_TerrainSandMask,
        sampler_Sol_TerrainSandMask,
        saturate(terrainUV));
    return saturate(dot(layerWeights, (half4)_Sol_TerrainSandChannel));
}

void SolApplyTerrainWetness(
    float3 positionWS,
    inout half3 albedo,
    inout half metallic,
    inout half smoothness,
    inout half occlusion)
{
    half wetness = (half)SolTerrainWetness(positionWS);

    // Keep the authored URP terrain smoothness untouched while dry. The demo
    // layer remaps intentionally keep this value low; inverting it globally
    // would make clear-weather terrain read like polished plastic. A water
    // film raises the existing smoothness only where rain or water is active.
    // Keep the target moderate: the packed micro-normal detail becomes noisy,
    // mirror-like sparkle when driven toward physically ideal water smoothness
    // without a dedicated specular anti-aliasing pass.
    smoothness = saturate(smoothness);
    half wetSand = wetness * SolTerrainSandWeight(positionWS);
    albedo *= 1.0h - wetSand * (half)saturate(_Sol_TerrainWetness.w);
    half wetSmoothness = (half)saturate(_Sol_TerrainWetSmoothness);
    smoothness = lerp(smoothness, max(smoothness, wetSmoothness), wetness);
}

#endif
