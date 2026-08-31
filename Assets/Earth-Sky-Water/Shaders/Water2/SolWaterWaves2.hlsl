#ifndef SOL_WATER_WAVES_2_INCLUDED
#define SOL_WATER_WAVES_2_INCLUDED

#define SOL_WATER_MAX_WAVES 8

float4 _SolWaterWaveDataA[SOL_WATER_MAX_WAVES]; // direction.xy, wavelength, amplitude
float4 _SolWaterWaveDataB[SOL_WATER_MAX_WAVES]; // steepness, phase offset, reserved
int _SolWaterWaveCount;
float _SolWaterWaveTime;
float4 _SolWaterWorldOrigin;
float4 _SolWaterWind;    // direction.xyz, sea-state speed m/s
float4 _SolWaterWeather; // seaStateSpeedMetresPerSecond, turbulence, rain, wave-speed multiplier
float4 _SolWaterWeatherExtended; // cloudiness, lightning, rain roughness, rain normal
float4 _SolWaterOptics;  // IOR, smoothness, scattering, profile wave speed
float4 _SolWaterSpectrum; // wind response, reserved
float4 _SolWaterFoamParams;
TEXTURE2D(_SolWaterInteractionTexture);
SAMPLER(sampler_SolWaterInteractionTexture);
float4 _SolWaterInteractionMapping; // center xz, world size xz
float4 _SolWaterInteractionTexel;
float _SolWaterInteractionStrength;
TEXTURE2D(_SolWaterShorelineMask);
SAMPLER(sampler_SolWaterShorelineMask);
float4 _SolWaterShorelineParams; // center xz, size xz
float4 _SolWaterShorelineDetail; // mask strength, foam width, wetness, mask enabled
TEXTURE2D(_SolWaterShorelineData);
SAMPLER(sampler_SolWaterShorelineData);
float4 _SolWaterShorelineData_TexelSize;
float4 _SolWaterShorelineDataMapping; // center xz, size xz
float4 _SolWaterShorelineDataParams; // depth range, distance range, shallow attenuation depth, data valid
float4 _SolWaterShorelineSurfaceParams; // contact fade, normal flattening, reserved
float4 _SolWaterShorelineBreakerParams; // strength, width, wavelength, speed
float4 _SolWaterShorelineBreakerDetail; // choppiness, foam gain, reserved
TEXTURE2D_ARRAY(_SolWaterSpectralDisplacement);
SAMPLER(sampler_SolWaterSpectralDisplacement);
TEXTURE2D_ARRAY(_SolWaterSpectralNormalFoam);
SAMPLER(sampler_SolWaterSpectralNormalFoam);
float4 _SolWaterSpectralParams; // cascade count, strength, resolution, reserved

// Wind speed in metres per second that counts as a full gale. Mirrors
// SolWaterWaveEvaluator.WindResponseReferenceSpeed -- keep the two in step.
#define SOL_WATER_WIND_REFERENCE_SPEED 24.0
#define SOL_WATER_SEA_STATE_SPEED_METRES_PER_SECOND (_SolWaterWeather.x)

// Normalised wind response, 0 at dead calm and 1 at gale. The material parameter is a
// physical speed; the divide-by-3 and divide-by-8 forms this replaced were
// written against the old authored 0..3 range and saturated at a light breeze.
float SolWaterWindResponse01(float windSpeedMetresPerSecond)
{
    return saturate(windSpeedMetresPerSecond / SOL_WATER_WIND_REFERENCE_SPEED);
}

struct SolWaterWaveResult
{
    float3 displacement;
    float3 normal;
    float3 velocity;
    float foam;
};

float4 SolSampleShorelineData(float2 localXZ)
{
    float2 logicalXZ = localXZ + _SolWaterWorldOrigin.xz;
    float2 uv = (logicalXZ - _SolWaterShorelineDataMapping.xy)
        / max(_SolWaterShorelineDataMapping.zw, 0.001) + 0.5;
    float2 inside = step(0.0, uv) * step(uv, 1.0);
    float valid = inside.x * inside.y * _SolWaterShorelineDataParams.w;
    float2 encoded = SAMPLE_TEXTURE2D_LOD(
        _SolWaterShorelineData, sampler_SolWaterShorelineData, saturate(uv), 0).rg;
    float depth = (encoded.x * 2.0 - 1.0) * _SolWaterShorelineDataParams.x;
    float distanceToShore = (encoded.y * 2.0 - 1.0) * _SolWaterShorelineDataParams.y;
    return float4(depth, distanceToShore, valid, 0.0);
}

float SolShallowWaveAttenuation(float4 shorelineData)
{
    if (shorelineData.z < 0.5)
        return 1.0;
    float depthFade = smoothstep(0.0,
        max(0.01, _SolWaterShorelineDataParams.z), shorelineData.x);
    float distanceFade = smoothstep(0.0,
        max(0.05, _SolWaterShorelineSurfaceParams.x * 2.0), shorelineData.y);
    return depthFade * distanceFade;
}

float2 SolWaterShorelineGradient(float2 localXZ)
{
    float2 texelWorld = _SolWaterShorelineDataMapping.zw
        * max(_SolWaterShorelineData_TexelSize.xy, 0.000001);
    float sampleRadius = clamp(max(texelWorld.x, texelWorld.y), 0.25, 4.0);
    float distanceLeft = SolSampleShorelineData(
        localXZ - float2(sampleRadius, 0.0)).y;
    float distanceRight = SolSampleShorelineData(
        localXZ + float2(sampleRadius, 0.0)).y;
    float distanceDown = SolSampleShorelineData(
        localXZ - float2(0.0, sampleRadius)).y;
    float distanceUp = SolSampleShorelineData(
        localXZ + float2(0.0, sampleRadius)).y;
    float2 gradient = float2(distanceRight - distanceLeft,
        distanceUp - distanceDown);
    float gradientLength = length(gradient);
    if (gradientLength < 0.0001)
    {
        float2 wind = _SolWaterWind.xz;
        return dot(wind, wind) > 0.0001 ? normalize(wind) : float2(1.0, 0.0);
    }
    return gradient / gradientLength;
}

void SolApplyShorelineBreaker(float2 localXZ, float4 shorelineData,
    inout SolWaterWaveResult result)
{
    float strength = max(0.0, _SolWaterShorelineBreakerParams.x);
    if (shorelineData.z < 0.5 || strength <= 0.0001
        || shorelineData.x <= 0.0 || shorelineData.y <= 0.0)
        return;

    float width = max(0.1, _SolWaterShorelineBreakerParams.y);
    float wavelength = max(0.25, _SolWaterShorelineBreakerParams.z);
    float speed = max(0.0, _SolWaterShorelineBreakerParams.w);
    float normalizedDistance = saturate(shorelineData.y / width);
    // WaterFX's dynamic shoreline waves build energy in shallow water, then
    // dissipate it at the obstacle boundary. This smooth envelope reproduces
    // that behavior from Sol's signed terrain field without a screen-space band.
    float contactEnvelope = smoothstep(0.015, 0.16, normalizedDistance);
    float offshoreEnvelope = 1.0 - smoothstep(0.55, 1.0, normalizedDistance);
    float depthEnvelope = smoothstep(0.02,
        max(0.25, _SolWaterShorelineDataParams.z * 0.5), shorelineData.x);
    float envelope = contactEnvelope * offshoreEnvelope * depthEnvelope;
    if (envelope <= 0.0001)
        return;

    float2 waterDirection = SolWaterShorelineGradient(localXZ);
    float2 shoreDirection = -waterDirection;
    float2 alongShore = float2(-waterDirection.y, waterDirection.x);
    float2 logicalXZ = localXZ + _SolWaterWorldOrigin.xz;
    float waveNumber = TWO_PI / wavelength;
    float angularSpeed = waveNumber * speed;
    float alongCoordinate = dot(logicalXZ, alongShore);
    float phaseWarp = sin(alongCoordinate * waveNumber * 0.23
        + sin(alongCoordinate * 0.071) * 1.7) * 0.42;
    // Increasing time moves constant-phase contours toward decreasing signed
    // distance: waves turn naturally and travel toward the baked shoreline.
    float phase = waveNumber * shorelineData.y
        + angularSpeed * _SolWaterWaveTime + phaseWarp;
    float sine;
    float cosine;
    sincos(phase, sine, cosine);
    float windScale = lerp(0.65, 1.15,
        SolWaterWindResponse01(SOL_WATER_SEA_STATE_SPEED_METRES_PER_SECOND));
    float amplitude = strength * envelope * windScale;
    float choppiness = max(0.0, _SolWaterShorelineBreakerDetail.x);

    result.displacement += float3(
        shoreDirection.x * choppiness * amplitude * cosine,
        amplitude * sine,
        shoreDirection.y * choppiness * amplitude * cosine);
    result.velocity += float3(
        waterDirection.x * choppiness * amplitude * angularSpeed * sine,
        amplitude * angularSpeed * cosine,
        waterDirection.y * choppiness * amplitude * angularSpeed * sine);

    float slope = amplitude * waveNumber * cosine;
    float3 breakerNormal = normalize(float3(
        -waterDirection.x * slope, 1.0, -waterDirection.y * slope));
    result.normal = normalize(result.normal + breakerNormal - float3(0.0, 1.0, 0.0));
    float crest = pow(saturate(sine * 0.5 + 0.5), 6.0);
    result.foam = max(result.foam, crest * envelope
        * max(0.0, _SolWaterShorelineBreakerDetail.y));
}

float SolWaterGeometryVisibility(float wavelength, float vertexSpacing)
{
    if (vertexSpacing < 0.0001)
        return 1.0;
    // Nyquist is two vertices per cycle, but ocean displacement needs a wider
    // transition to keep slopes stable. Four vertices per wave is fully resolved.
    return smoothstep(vertexSpacing * 2.0, vertexSpacing * 4.0, wavelength);
}

float SolWaterCascadeVisibleDistance(int cascade)
{
    // WaterFX's visible-area schedule is intentionally wider than one
    // simulation domain. It gives each cascade a cubic handoff before its
    // replacement becomes dominant and avoids a moving ring boundary.
    return cascade == 0 ? 40.0 : cascade == 1 ? 160.0
        : cascade == 2 ? 800.0 : 4800.0;
}

// Per-cascade rotation basis, as (cos, sin).
//
// Slightly different bases prevent the four periodic FFT domains from resolving
// into one shared world-axis lattice, without destroying the authored wind
// direction at any individual scale.
//
// Mirrored by SolWaterFftReadback.CascadeBasis on the CPU; the two have to agree
// or a query samples a differently oriented field than the one on screen. This
// existed as two identical inline copies below, which is exactly how the cascade
// domain table drifted out of step once already.
float2 SolWaterCascadeBasis(int cascade)
{
    return cascade == 0 ? float2(1.0, 0.0)
        : cascade == 1 ? float2(0.992546, 0.121869)
        : cascade == 2 ? float2(0.981627, -0.190809)
        : float2(0.997564, 0.069756);
}

float2 SolWaterRotateCascade(float2 value, int cascade)
{
    float2 cs = SolWaterCascadeBasis(cascade);
    return float2(cs.x * value.x - cs.y * value.y,
        cs.y * value.x + cs.x * value.y);
}

float2 SolWaterUnrotateCascade(float2 value, int cascade)
{
    float2 cs = SolWaterCascadeBasis(cascade);
    return float2(cs.x * value.x + cs.y * value.y,
        -cs.y * value.x + cs.x * value.y);
}

// Must stay identical to CascadeSize in SolWaterFFT.compute and to
// SolWaterFftReadback.CascadeSize on the CPU. Sampling a domain the simulation
// did not write reads as the whole surface sliding at the wrong scale, so this
// exists once rather than as a literal at each sampling site.
float SolWaterCascadeSize(int cascade)
{
    return cascade == 0 ? 5.0 : cascade == 1 ? 20.0 : cascade == 2 ? 100.0 : 600.0;
}

float2 SolWaterCascadeCoord(float2 logicalXZ, float mapSize, int cascade)
{
    float2 offset = cascade == 0 ? float2(0.0, 0.0)
        : cascade == 1 ? float2(0.371, 0.173)
        : cascade == 2 ? float2(0.619, 0.427)
        : float2(0.211, 0.793);
    return SolWaterRotateCascade(logicalXZ, cascade) / mapSize + offset;
}

float2 SolWaterCascadeUv(float2 logicalXZ, float mapSize, int cascade)
{
    return frac(SolWaterCascadeCoord(logicalXZ, mapSize, cascade));
}

SolWaterWaveResult SolEvaluateWaterWaves(float2 localXZ, float geometrySpacing,
    float2 flowDirection, float finiteGeometry)
{
    const float gravity = 9.81;
    SolWaterWaveResult result;
    result.displacement = 0;
    result.velocity = 0;
    result.foam = 0;
    float3 tangentX = float3(1, 0, 0);
    float3 tangentZ = float3(0, 0, 1);
    float2 logicalXZ = localXZ + _SolWaterWorldOrigin.xz;
    float2 wind = _SolWaterWind.xz;
    if (dot(flowDirection, flowDirection) > 0.0001)
        wind = flowDirection;
    wind = dot(wind, wind) > 0.0001 ? normalize(wind) : float2(1, 0);
    float turbulence = saturate(_SolWaterWeather.y);
    float weatherAmplitude = lerp(1.0, 1.8, turbulence)
        * lerp(0.65, 1.35,
            SolWaterWindResponse01(SOL_WATER_SEA_STATE_SPEED_METRES_PER_SECOND));
    // Gerstner is the deterministic Low-tier fallback. Medium/High use the
    // directional spectrum directly; stacking both produces coherent sine bands
    // that expose the clipmap triangulation at grazing angles.
    float gerstnerGeometryWeight = 1.0 - step(0.5, _SolWaterSpectralParams.x);

    [loop]
    for (int i = 0; i < min(_SolWaterWaveCount, SOL_WATER_MAX_WAVES); i++)
    {
        float4 a = _SolWaterWaveDataA[i];
        float4 b = _SolWaterWaveDataB[i];
        float wavelength = max(0.01, a.z);
        float k = TWO_PI / wavelength;
        float omega = sqrt(gravity * k) * _SolWaterOptics.w;
        float2 authored = dot(a.xy, a.xy) > 0.0001 ? normalize(a.xy) : float2(1, 0);
        float2 direction = normalize(lerp(authored, wind, saturate(_SolWaterSpectrum.x * 0.35)));
        float phase = k * dot(direction, logicalXZ) - omega * _SolWaterWaveTime + b.y;
        float sine;
        float cosine;
        sincos(phase, sine, cosine);
        float geometryVisibility = SolWaterGeometryVisibility(wavelength, geometrySpacing);
        float amplitude = max(0, a.w) * weatherAmplitude
            * geometryVisibility * gerstnerGeometryWeight;
        float steepness = saturate(b.x + turbulence * 0.2);
        float horizontal = steepness * amplitude;

        result.displacement += float3(direction.x * horizontal * cosine,
            amplitude * sine, direction.y * horizontal * cosine);
        float slope = amplitude * k * cosine;
        tangentX += float3(
            -direction.x * direction.x * horizontal * k * sine,
            direction.x * slope,
            -direction.x * direction.y * horizontal * k * sine);
        tangentZ += float3(
            -direction.x * direction.y * horizontal * k * sine,
            direction.y * slope,
            -direction.y * direction.y * horizontal * k * sine);
        result.velocity += float3(
            direction.x * horizontal * omega * sine,
            -amplitude * omega * cosine,
            direction.y * horizontal * omega * sine);
        result.foam += max(0, steepness * k * amplitude - 0.22);
    }

    result.normal = normalize(cross(tangentZ, tangentX));
    if (result.normal.y < 0)
        result.normal = -result.normal;
    result.foam = saturate(result.foam * _SolWaterFoamParams.x);

    float2 shorelineUV = (logicalXZ - _SolWaterShorelineParams.xy)
        / max(_SolWaterShorelineParams.zw, 0.001) + 0.5;
    float2 shorelineInside = step(0.0, shorelineUV) * step(shorelineUV, 1.0);
    float shorelineMask = shorelineInside.x * shorelineInside.y
        * SAMPLE_TEXTURE2D_LOD(_SolWaterShorelineMask, sampler_SolWaterShorelineMask,
            shorelineUV, 0).r;
    result.foam = saturate(max(result.foam,
        shorelineMask * _SolWaterShorelineDetail.x * _SolWaterFoamParams.y
        * (1.0 - finiteGeometry)));

    if (_SolWaterSpectralParams.x > 0.5 && _SolWaterSpectralParams.y > 0.0001)
    {
        float3 spectralDisplacement = 0;
        float3 spectralNormalOffset = 0;
        float spectralFoam = 0;
        float cameraDistance = distance(localXZ, GetCameraPositionWS().xz);
        [loop]
        for (int cascade = 0; cascade < min((int)_SolWaterSpectralParams.x, 4); cascade++)
        {
            float mapSize = SolWaterCascadeSize(cascade);
            float2 spectralUV = SolWaterCascadeUv(logicalXZ, mapSize, cascade);
            float4 displacementSample = SAMPLE_TEXTURE2D_ARRAY_LOD(
                _SolWaterSpectralDisplacement, sampler_SolWaterSpectralDisplacement,
                spectralUV, cascade, 0);
            float4 normalFoamSample = SAMPLE_TEXTURE2D_ARRAY_LOD(
                _SolWaterSpectralNormalFoam, sampler_SolWaterSpectralNormalFoam,
                spectralUV, cascade, 0);
            displacementSample.xz = SolWaterUnrotateCascade(
                displacementSample.xz, cascade);
            normalFoamSample.xz = SolWaterUnrotateCascade(
                normalFoamSample.xz, cascade);
            // Short cascades are removed by distance alone, exactly as WaterFX does in
            // KWS_GetFftFade4. Distance is continuous across a patch boundary, so
            // neighbouring patches agree on how much of each cascade they carry.
            //
            // A second term used to multiply this by the patch's own vertex spacing.
            // That spacing doubles at every detail step, so two patches sharing an edge
            // included measurably different sets of waves, and the resulting displacement
            // step read as rectangular blocks — clearest in shallow water, where it also
            // shifted the refracted seabed. WaterFX has no such term; the visible-area
            // schedule above already retires a cascade well before its texels approach
            // the vertex density, and the per-pixel path keeps its own derivative-driven
            // footprint filter, which is continuous because derivatives are.
            float filterDistance = SolWaterCascadeVisibleDistance(cascade);
            float cascadeRatio = saturate(cameraDistance / filterDistance);
            float cascadeVisibility = 1.0 - cascadeRatio * cascadeRatio * cascadeRatio;
            if (cascade == (int)_SolWaterSpectralParams.x - 1)
                cascadeVisibility = 1.0 - saturate(cameraDistance / max(500.0, mapSize * 2.0));
            spectralDisplacement += displacementSample.xyz * cascadeVisibility;
            spectralNormalOffset += (normalFoamSample.xyz - float3(0, 1, 0)) * cascadeVisibility;
            spectralFoam = max(spectralFoam, normalFoamSample.w * cascadeVisibility);
        }
        result.displacement += spectralDisplacement * _SolWaterSpectralParams.y;
        float2 rainUV = logicalXZ * 0.18
            + float2(_SolWaterWaveTime * 0.08, -_SolWaterWaveTime * 0.05);
        float rainDetail = sin(rainUV.x * 17.0 + sin(rainUV.y * 7.0))
            * sin(rainUV.y * 19.0 - rainUV.x * 3.0) * 0.5 + 0.5;
        float3 rainNormal = float3((rainDetail - 0.5) * 2.0, 0,
            (0.5 - rainDetail) * 2.0);
        result.normal = normalize(result.normal + spectralNormalOffset * _SolWaterSpectralParams.y
            + rainNormal * _SolWaterWeather.z * _SolWaterWeatherExtended.w * 0.08);
        result.foam = saturate(max(result.foam, spectralFoam * _SolWaterFoamParams.x));
    }

    // Baked terrain depth and signed shore distance suppress open-ocean
    // displacement before it can push the clipmap through land. Local
    // interaction waves are intentionally applied afterward.
    if (finiteGeometry < 0.5)
    {
        float4 shorelineData = SolSampleShorelineData(localXZ);
        float shallowAttenuation = SolShallowWaveAttenuation(shorelineData);
        result.displacement *= shallowAttenuation;
        result.velocity *= shallowAttenuation;
        result.normal = normalize(lerp(float3(0, 1, 0), result.normal,
            lerp(1.0, shallowAttenuation, _SolWaterShorelineSurfaceParams.y)));
        if (shorelineData.z > 0.5)
        {
            float shorelineBand = 1.0 - smoothstep(0.0,
                max(0.01, _SolWaterShorelineDetail.y), abs(shorelineData.y));
            result.foam = saturate(max(result.foam,
                shorelineBand * _SolWaterFoamParams.y));
        }
        SolApplyShorelineBreaker(localXZ, shorelineData, result);
    }

    if (_SolWaterInteractionStrength > 0.0001)
    {
        float2 interactionUV = (localXZ - _SolWaterInteractionMapping.xy)
            / max(_SolWaterInteractionMapping.zw, 0.001) + 0.5;
        float2 inside = step(0.0, interactionUV) * step(interactionUV, 1.0);
        float interactionMask = inside.x * inside.y;
        if (interactionMask > 0.5)
        {
            float4 interaction = SAMPLE_TEXTURE2D_LOD(
                _SolWaterInteractionTexture, sampler_SolWaterInteractionTexture, interactionUV, 0);
            float heightL = SAMPLE_TEXTURE2D_LOD(_SolWaterInteractionTexture,
                sampler_SolWaterInteractionTexture, interactionUV - float2(_SolWaterInteractionTexel.x, 0), 0).x;
            float heightR = SAMPLE_TEXTURE2D_LOD(_SolWaterInteractionTexture,
                sampler_SolWaterInteractionTexture, interactionUV + float2(_SolWaterInteractionTexel.x, 0), 0).x;
            float heightD = SAMPLE_TEXTURE2D_LOD(_SolWaterInteractionTexture,
                sampler_SolWaterInteractionTexture, interactionUV - float2(0, _SolWaterInteractionTexel.y), 0).x;
            float heightU = SAMPLE_TEXTURE2D_LOD(_SolWaterInteractionTexture,
                sampler_SolWaterInteractionTexture, interactionUV + float2(0, _SolWaterInteractionTexel.y), 0).x;
            result.displacement.y += interaction.x * _SolWaterInteractionStrength;
            float3 interactionNormal = normalize(float3(
                (heightL - heightR) * _SolWaterInteractionStrength,
                2.0,
                (heightD - heightU) * _SolWaterInteractionStrength));
            result.normal = normalize(result.normal + interactionNormal - float3(0, 1, 0));
            result.foam = saturate(max(result.foam, interaction.z));
        }
    }
    return result;
}

// Debug only. Returns the strongest per-cascade detail fade the pixel spectral path
// applies at this point, so a screenshot can show where that fade collapses.
//
// This is the one term in the surface shading that can go to zero along a line: when it
// does, `normal` falls back to flat (0,1,0) and the cascade foam disappears with it, so a
// stroke of flat, foamless water appears in an otherwise choppy surface. Mirrors the fade
// computed in SolEvaluateWaterPixelNormalFoam; keep the two in step if either changes.
float SolWaterDebugSpectralDetailFade(float2 localXZ)
{
    if (_SolWaterSpectralParams.x < 0.5 || _SolWaterSpectralParams.y <= 0.0001)
        return 1.0;
    float2 logicalXZ = localXZ + _SolWaterWorldOrigin.xz;
    float cameraDistance = distance(localXZ, GetCameraPositionWS().xz);
    float strongestFade = 0.0;
    [loop]
    for (int cascade = 0; cascade < min((int)_SolWaterSpectralParams.x, 4); cascade++)
    {
        float mapSize = SolWaterCascadeSize(cascade);
        float2 spectralCoord = SolWaterCascadeCoord(logicalXZ, mapSize, cascade);
        float2 derivativeX = ddx(spectralCoord);
        float2 derivativeY = ddy(spectralCoord);
        float footprintTexels = sqrt(max(dot(derivativeX, derivativeX),
            dot(derivativeY, derivativeY))) * max(1.0, _SolWaterSpectralParams.z);
        float fade = saturate(cameraDistance
            / max(1.0, SolWaterCascadeVisibleDistance(cascade)));
        fade = 1.0 - fade * fade * fade;
        if (cascade == (int)_SolWaterSpectralParams.x - 1)
            fade = 1.0 - saturate(cameraDistance / max(500.0, mapSize * 2.0));
        fade *= 1.0 - smoothstep(0.8, 2.4, footprintTexels);
        strongestFade = max(strongestFade, fade);
    }
    return strongestFade;
}

void SolEvaluateWaterPixelNormalFoam(float2 localXZ, inout float3 normal, inout float foam)
{
    if (_SolWaterSpectralParams.x < 0.5 || _SolWaterSpectralParams.y <= 0.0001)
        return;

    float baseFoam = foam;
    normal = float3(0, 1, 0);
    foam = 0;
    float2 logicalXZ = localXZ + _SolWaterWorldOrigin.xz;
    float cameraDistance = distance(localXZ, GetCameraPositionWS().xz);
    float3 normalOffset = 0;
    [loop]
    for (int cascade = 0; cascade < min((int)_SolWaterSpectralParams.x, 4); cascade++)
    {
        float mapSize = SolWaterCascadeSize(cascade);
        // Use the unwrapped coordinate for derivatives. Taking derivatives of
        // frac() produces a false full-texture footprint at every wrap seam.
        float2 spectralCoord = SolWaterCascadeCoord(logicalXZ, mapSize, cascade);
        float2 derivativeX = ddx(spectralCoord);
        float2 derivativeY = ddy(spectralCoord);
        float derivativeLengthX = dot(derivativeX, derivativeX);
        float derivativeLengthY = dot(derivativeY, derivativeY);
        float2 majorAxis = derivativeLengthX > derivativeLengthY
            ? derivativeX : derivativeY;
        float footprintTexels = sqrt(max(derivativeLengthX, derivativeLengthY))
            * max(1.0, _SolWaterSpectralParams.z);
        float2 spectralUV = frac(spectralCoord);

        // The FFT targets deliberately have no mip chain because they are
        // reconstructed every frame. A small anisotropic footprint filter
        // prevents the shortest directional cascade from resolving into
        // parallel comb lines at grazing angles. The visibility handoff then
        // removes detail only after a pixel spans multiple simulation texels.
        float4 centerSample = SAMPLE_TEXTURE2D_ARRAY(
            _SolWaterSpectralNormalFoam, sampler_SolWaterSpectralNormalFoam,
            spectralUV, cascade);
        float4 normalFoam = centerSample;

        // tapExtent is a count of TEXELS along the footprint's major axis, so the
        // offset it produces has to be a unit direction scaled into UV. The offset
        // used to be `majorAxis * tapExtent` -- the raw derivative, on the order of
        // 1e-3 in UV, times at most 0.75. Both side taps therefore landed a small
        // fraction of one texel from the centre, the three samples averaged to the
        // centre sample, and the filter did nothing at all while still costing two
        // array fetches per cascade in each of the three places this is called.
        float footprintScale = max(derivativeLengthX, derivativeLengthY);
        // Below one texel of coverage the taps are sub-texel by construction and the
        // filter cannot contribute; skipping them there is free. It also keeps this
        // from touching the near field, which was never the aliasing case.
        if (footprintTexels > 1.0 && footprintScale > 1e-20)
        {
            float2 tapDirection = majorAxis * rsqrt(footprintScale);
            float texelSize = 1.0 / max(1.0, _SolWaterSpectralParams.z);
            float2 tapOffset = tapDirection * min(0.75, footprintTexels * 0.25) * texelSize;
            float4 positiveSample = SAMPLE_TEXTURE2D_ARRAY(
                _SolWaterSpectralNormalFoam, sampler_SolWaterSpectralNormalFoam,
                frac(spectralUV + tapOffset), cascade);
            float4 negativeSample = SAMPLE_TEXTURE2D_ARRAY(
                _SolWaterSpectralNormalFoam, sampler_SolWaterSpectralNormalFoam,
                frac(spectralUV - tapOffset), cascade);
            normalFoam = centerSample * 0.5
                + (positiveSample + negativeSample) * 0.25;
        }
        normalFoam.xz = SolWaterUnrotateCascade(normalFoam.xz, cascade);
        float fade = saturate(cameraDistance
            / max(1.0, SolWaterCascadeVisibleDistance(cascade)));
        fade = 1.0 - fade * fade * fade;
        if (cascade == (int)_SolWaterSpectralParams.x - 1)
        {
            float farDistance = max(500.0, mapSize * 2.0);
            fade = 1.0 - saturate(cameraDistance / farDistance);
        }
        fade *= 1.0 - smoothstep(0.8, 2.4, footprintTexels);
        normalOffset += (normalFoam.xyz - float3(0, 1, 0)) * fade;
        foam = max(foam, normalFoam.w * fade);
    }
    normal = normalize(float3(0, 1, 0) + normalOffset * _SolWaterSpectralParams.y);
    foam = saturate(max(baseFoam, foam * _SolWaterFoamParams.x));
    float4 shorelineData = SolSampleShorelineData(localXZ);
    float shallowAttenuation = SolShallowWaveAttenuation(shorelineData);
    normal = normalize(lerp(float3(0, 1, 0), normal,
        lerp(1.0, shallowAttenuation, _SolWaterShorelineSurfaceParams.y)));
    if (shorelineData.z > 0.5)
    {
        float shorelineBand = 1.0 - smoothstep(0.0,
            max(0.01, _SolWaterShorelineDetail.y), abs(shorelineData.y));
        foam = saturate(max(foam, shorelineBand * _SolWaterFoamParams.y));
    }
    // The vertex path already carries breaker displacement, but pixel spectral
    // normals intentionally replace interpolated FFT normals. Re-evaluate only
    // the inexpensive SDF breaker normal so the shore crest remains curved.
    SolWaterWaveResult breaker;
    breaker.displacement = 0;
    breaker.velocity = 0;
    breaker.normal = float3(0, 1, 0);
    breaker.foam = 0;
    SolApplyShorelineBreaker(localXZ, shorelineData, breaker);
    normal = normalize(normal + breaker.normal - float3(0, 1, 0));
    foam = saturate(max(max(baseFoam, foam), breaker.foam));
}

#endif
