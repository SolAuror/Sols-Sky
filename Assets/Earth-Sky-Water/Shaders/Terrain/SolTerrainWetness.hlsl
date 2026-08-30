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

// The ocean's live shoreline field, republished for the terrain by SolWaterWetness.
// G holds signed distance to the waterline in metres, positive on the water side. The
// mapping is in render space rather than the logical space the water shaders sample it
// in, because the terrain has no world-origin uniform to convert with.
TEXTURE2D(_Sol_TerrainShorelineData);
SAMPLER(sampler_Sol_TerrainShorelineData);
float4 _Sol_TerrainShorelineMapping; // centre xz, size xz
float4 _Sol_TerrainShorelineParams;  // distance range, inland wet distance, unused, valid

// Keep this local to the shared terrain include: the packed _Sol_TerrainWetness
// contract has no free component. It can be promoted to an authored global later
// without changing the wetness calculation or the Water2 publisher contract.
static const half kWetAlbedoDarkening = 0.7h;

// How wet the ground is from its horizontal distance to the waterline, as opposed to
// its height above it. 1 everywhere there is no shoreline field, which leaves the
// height band below exactly as it was.
//
// Height alone cannot describe a damp shore. It says a flat beach is uniformly wet for
// its entire width no matter how far inland it runs, and that a clifftop is as wet as
// the sand at its foot as long as both sit inside the same fraction of a metre above
// the sea. What actually wets a shore is wave run-up, which is a distance travelled
// across the ground -- so that is what this measures.
float SolTerrainShorelineProximity(float3 positionWS)
{
    if (_Sol_TerrainShorelineParams.w < 0.5)
        return 1.0;

    float2 uv = (positionWS.xz - _Sol_TerrainShorelineMapping.xy)
        / max(_Sol_TerrainShorelineMapping.zw, 0.001) + 0.5;
    float2 inside = step(0.0, uv) * step(uv, 1.0);
    // Off the mapped rectangle there is no coast to be near, so defer to the height
    // band rather than declaring the ground dry.
    if (inside.x * inside.y < 0.5)
        return 1.0;

    float encoded = SAMPLE_TEXTURE2D(_Sol_TerrainShorelineData,
        sampler_Sol_TerrainShorelineData, uv).g;
    float distanceToShore = (encoded * 2.0 - 1.0) * _Sol_TerrainShorelineParams.x;
    // Positive is the water side, which is submerged and fully wet. Only the land side
    // fades, over the authored run-up distance.
    float inland = max(0.0, -distanceToShore);
    return 1.0 - smoothstep(0.0, max(0.01, _Sol_TerrainShorelineParams.y), inland);
}

float SolTerrainWetness(float3 positionWS)
{
    float shorelineRange = max(_Sol_TerrainWetness.z, 0.001);
    float heightProximity = saturate(
        (_Sol_GlobalWaterLevel + shorelineRange - positionWS.y) / shorelineRange);
    // Both conditions have to hold, so the weaker one wins: near the water in height
    // AND near it across the ground. Multiplying instead would darken the whole band
    // twice over and never reach full wetness at the waterline itself.
    float waterProximity = min(heightProximity, SolTerrainShorelineProximity(positionWS));
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
    albedo *= lerp(1.0h, kWetAlbedoDarkening, wetness);
    albedo *= 1.0h - wetSand * (half)saturate(_Sol_TerrainWetness.w);
    half wetSmoothness = (half)saturate(_Sol_TerrainWetSmoothness);
    smoothness = lerp(smoothness, max(smoothness, wetSmoothness), wetness);
}

#endif
