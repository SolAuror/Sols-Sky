#ifndef SOL_WATER_OPTICS_INCLUDED
#define SOL_WATER_OPTICS_INCLUDED

// Shared water volume optics for the surface (SolOcean) and the submerged
// composition (SolUnderwater). Both views must agree or the waterline pops, so
// the absorption curve and the scattering lighting live here rather than being
// duplicated per shader.
//
// Adapted from WaterFX's ComputeAbsorbtion / GetVolumetricLightWithAbsorbtion
// analytic path and its GGX sun reflection, rewritten in Sol style.

// Live Sol sky gradient, published by SolWaterSkyReflectionState.Apply. Declared
// here rather than in SolOcean so the underwater composition can light its
// scattering from the identical sky and the waterline stays continuous.
float4 _SolWaterReflectionSkyColor;
float4 _SolWaterReflectionEquatorColor;
float4 _SolWaterReflectionGroundColor;
float4 _SolWaterReflectionWarmColor;
float4 _SolWaterReflectionSkyParams; // zenith blend, nadir blend, horizon power, warmth falloff
float4 _SolWaterReflectionSunDirection; // xyz direction, w Sol gradient valid

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

// Maximum optical path considered when nothing is behind the surface. The
// refracted sample is the sky in open water, so the path has to be long enough
// for absorption to reach its floor instead of transmitting the sky through.
#define SOL_WATER_MAX_RAY_LENGTH 5000.0

// Longest path that still contributes to the extinction integral. Beyond this
// the volume colour has fully replaced the transmitted scene.
#define SOL_WATER_MAX_CLARITY 50.0

// Returns rgb transmittance through `rayLength` metres of water, and in w the
// extinction: how much of the transmitted scene has been replaced by volume
// scattering. The exponent is superlinear in path length, which is what gives
// water its rapid near-surface falloff and long blue tail.
float4 SolWaterComputeAbsorption(float clarityDistance, float3 absorptionColor,
    float rayLength)
{
    rayLength = max(rayLength, 0.0);
    float multiplier = lerp(0.2, 0.1,
        saturate((clarityDistance - SOL_WATER_MAX_CLARITY) / SOL_WATER_MAX_CLARITY));
    // The floor keeps deep water from collapsing to black and preserves the
    // characteristic blue once red and green are fully absorbed.
    float3 transmittance = max(float3(0.0005, 0.001, 0.025),
        exp2(-0.75 * pow(rayLength, 1.5) * absorptionColor * multiplier));
    float clamped = min(rayLength, min(SOL_WATER_MAX_CLARITY, clarityDistance));
    float integralCoefficient = clarityDistance * 0.2;
    integralCoefficient = max(0.0001, integralCoefficient * integralCoefficient);
    float extinction = 1.0 - saturate(exp2(-clamped / integralCoefficient + 0.5));
    return saturate(float4(transmittance, extinction));
}

// Normalizes authored per-metre absorption coefficients into the per-channel
// weight the curve above expects, so existing profiles keep their hue.
float3 SolWaterNormalizeAbsorption(float3 authoredAbsorption)
{
    float3 absorptionColor = max(authoredAbsorption, 0.0001);
    return absorptionColor / max(0.0001, absorptionColor.r);
}

// How much light reaches the water volume, as a function of sun elevation. Below
// the horizon the sun still contributes a little via atmospheric scattering,
// hence the -0.25 lower bound rather than 0.
float SolWaterSunElevationPhase(float3 lightDirectionWS)
{
    return smoothstep(-0.25, 1.0, dot(lightDirectionWS, float3(0.0, 1.0, 0.0)));
}

// ---------------------------------------------------------------------------
// Caustic projection
//
// Caustics are cast by the surface, not by the ground. The pattern landing on a
// sea bed point belongs to the patch of surface the refracted light passed
// through, which sits up-light of the point directly overhead by the light's
// slant -- tens of metres for a low sun over a deep column.
//
// The surface pass projected along the light and then warped by the surface
// normal; the submerged pass sampled the sea bed's own XZ with neither. Same bed,
// two different patterns, so the caustics slid sideways as the camera crossed the
// waterline. Both sides now call these, which is what sharing this header is for.
// ---------------------------------------------------------------------------

// Where the light landing at `groundXZ` entered the water surface.
float2 SolWaterCausticSurfaceXZ(float2 groundXZ, float waterColumn,
    float3 lightDirectionWS)
{
    float lightElevation = max(0.08, lightDirectionWS.y);
    return groundXZ + lightDirectionWS.xz * (waterColumn / lightElevation);
}

// Surface-normal warp applied on top of the light projection, in logical space.
// `surfaceNormal` is the water normal at the entry point; pass float3(0, 1, 0)
// where the wave field is not bound and only the light slant can be accounted
// for -- the slant is the dominant term, the warp a metre-scale refinement.
float2 SolWaterCausticSampleXZ(float2 logicalSurfaceXZ, float waterColumn,
    float3 surfaceNormal)
{
    return logicalSurfaceXZ + surfaceNormal.xz * waterColumn * 1.4;
}

// Volume scattering radiance. Lighting the turbidity colour by sun elevation and
// ambient sky is what makes the water track time of day; a fixed authored colour
// reads dead at every hour.
float3 SolWaterVolumeScattering(float3 turbidityColor, float3 ambientSky,
    float3 mainLightColor, float3 mainLightDirection, float shadowing,
    float strength)
{
    float3 volumeLight = 0.5 * ambientSky
        + mainLightColor * SolWaterSunElevationPhase(mainLightDirection) * shadowing;
    return 0.5 * turbidityColor * saturate(volumeLight) * max(0.0, strength);
}

// ---------------------------------------------------------------------------
// Stochastic texture tiling
//
// Adapted from WaterFX's Tex2DStochastic. A regular UV lookup repeats on a
// visible lattice; foam stretched along the wind direction made that lattice
// read as parallel dashes. Blending three hash-offset lookups weighted by
// barycentric coordinates on a triangle grid removes the repetition without
// needing a domain warp or extra octaves.
// ---------------------------------------------------------------------------

float2 SolWaterStochasticHash(float2 gridVertex)
{
    return frac(sin(mul(float2x2(127.1, 311.7, 269.5, 183.3), gridVertex)) * 43758.5453);
}

void SolWaterTriangleGrid(float2 uv, out float3 weights,
    out float2 vertex0, out float2 vertex1, out float2 vertex2)
{
    // 2 * sqrt(3): scales the lattice so one triangle spans roughly one texture
    // tile, which keeps the three lookups decorrelated at the working scale.
    uv *= 3.4641016;
    float2 skewed = mul(float2x2(1.0, 0.0, -0.5773503, 1.1547005), uv);
    float2 baseVertex = floor(skewed);
    float2 fraction = frac(skewed);
    float remainder = 1.0 - fraction.x - fraction.y;

    if (remainder > 0.0)
    {
        weights = float3(remainder, fraction.y, fraction.x);
        vertex0 = baseVertex;
        vertex1 = baseVertex + float2(0.0, 1.0);
        vertex2 = baseVertex + float2(1.0, 0.0);
    }
    else
    {
        weights = float3(-remainder, 1.0 - fraction.y, 1.0 - fraction.x);
        vertex0 = baseVertex + float2(1.0, 1.0);
        vertex1 = baseVertex + float2(1.0, 0.0);
        vertex2 = baseVertex + float2(0.0, 1.0);
    }
}

// Explicit gradients are required: the hash offsets are discontinuous between
// triangles, so implicit derivatives would select wildly wrong mip levels along
// every lattice edge.
float4 SolWaterSampleStochastic(TEXTURE2D_PARAM(sourceTexture, sourceSampler),
    float2 uv, float2 gradientX, float2 gradientY)
{
    float3 weights;
    float2 vertex0, vertex1, vertex2;
    SolWaterTriangleGrid(uv, weights, vertex0, vertex1, vertex2);

    float4 sample0 = SAMPLE_TEXTURE2D_GRAD(sourceTexture, sourceSampler,
        uv + SolWaterStochasticHash(vertex0), gradientX, gradientY);
    float4 sample1 = SAMPLE_TEXTURE2D_GRAD(sourceTexture, sourceSampler,
        uv + SolWaterStochasticHash(vertex1), gradientX, gradientY);
    float4 sample2 = SAMPLE_TEXTURE2D_GRAD(sourceTexture, sourceSampler,
        uv + SolWaterStochasticHash(vertex2), gradientX, gradientY);

    return sample0 * weights.x + sample1 * weights.y + sample2 * weights.z;
}

// ---------------------------------------------------------------------------
// FFT-rendered caustics
//
// One slice per near cascade, produced by SolWaterCausticRenderGraph. Shared by
// the surface and the submerged composition so both project the same field.
// ---------------------------------------------------------------------------
// Response curve for the caustic field, shared by the surface and the submerged
// composition so both project the same lighting.
//
// The field is the area compression of the displaced surface, centred so that a typical
// sea bed texel sits near zero, with bright filaments above and dark cells below. Both
// sides are shaped as saturating exponentials rather than clamps: a clamp collapses
// everything past its limit onto one value, which is what turns a caustic field into flat
// blotches instead of a pattern.
//
// GAIN replaces a hardcoded 5.0. That value was tuned when the caustic pass divided
// displacement by a cascade domain about 6.4x too large, so the density deviations it saw
// were correspondingly small. With the domain corrected the same gain — 16.5x once the
// profile's causticStrength is folded in — pushed almost the whole field past the
// brightening ceiling, leaving a uniformly blown sea bed whose only visible structure was
// the minority of texels dark enough to fall out the bottom.
//
// causticStrength in the water profile remains the authored knob and multiplies this.
#define SOL_WATER_CAUSTIC_GAIN 0.35
// Most a caustic field may add to, or take back off, the sea bed. Caustics redistribute
// light, so the dark cells are a dip in an otherwise lit floor, not a shadow, and the
// bright filaments carry most of the contrast.
#define SOL_WATER_CAUSTIC_MAX_BRIGHTENING 1.75
#define SOL_WATER_CAUSTIC_MAX_DARKENING 0.28

// Applies the response above to a signed caustic field value. Returns the multiplier the
// refracted sea bed is scaled by, which is 1.0 exactly where the field is neutral.
float3 SolWaterApplyCausticResponse(float3 causticLighting)
{
    float3 gained = causticLighting * SOL_WATER_CAUSTIC_GAIN;
    float3 brightening = SOL_WATER_CAUSTIC_MAX_BRIGHTENING
        * (1.0 - exp2(-max(0.0, gained)));
    float3 darkening = SOL_WATER_CAUSTIC_MAX_DARKENING
        * (1.0 - exp2(-max(0.0, -gained)));
    return 1.0 + brightening - darkening;
}

TEXTURE2D_ARRAY(_SolWaterCausticArray);
SAMPLER(sampler_SolWaterCausticArray);
// x/y: cascade 0/1 domain size in metres, z: slice count, w: array valid
float4 _SolWaterCausticArrayParams;

float SolWaterSampleCausticArray(float2 logicalXZ, float waterColumn, float2 pixelPosition)
{
    // Shallow water is focused by the short cascade, deeper water by the longer
    // one. Jittering the selection depth dissolves the boundary between them
    // instead of leaving a visible ring where the cascade swaps.
    float jitter = frac(52.9829189
        * frac(dot(pixelPosition, float2(0.06711056, 0.00583715)))) * 0.5;
    float slices = max(1.0, _SolWaterCausticArrayParams.z);
    float cascade = (waterColumn + jitter) > 2.5 ? 1.0 : 0.0;
    cascade = min(cascade, slices - 1.0);
    float domain = cascade < 0.5
        ? _SolWaterCausticArrayParams.x : _SolWaterCausticArrayParams.y;

    float2 uv = logicalXZ / max(0.001, domain);
    float caustic = SAMPLE_TEXTURE2D_ARRAY_GRAD(_SolWaterCausticArray,
        sampler_SolWaterCausticArray, uv, cascade, ddx(uv), ddy(uv)).r;

    // The pass accumulates area compression around unity for a flat surface, so
    // subtract the unfocused baseline. The result is deliberately SIGNED and
    // unclamped: a caustic field starves light exactly where it concentrates it,
    // and the dark cells between the bright filaments are most of what makes the
    // pattern legible. Saturating here threw all of that away and left a faint
    // additive haze, which is why the effect only showed up in the debug view
    // where nothing else was competing with it.
    // Subtract less than the full unfocused baseline of 1.0. Removing all of it put
    // the mean of the field at zero, so half of every frame darkened the sea bed and
    // the net effect read as dimming rather than lighting. WaterFX subtracts only a
    // quarter of its multiplier for the same reason: the dark cells are supposed to
    // be the minority that make the bright filaments legible, not half the picture.
    return (caustic - 0.72) * 1.6;
}

// ---------------------------------------------------------------------------
// Advected foam
//
// A single scrolling UV slides foam across the surface as one rigid sheet: it
// reads as the texture moving rather than as foam being carried by the water.
// WaterFX advects the UV along the flow and periodically resets it, crossfading
// two half-period-offset copies so the reset is never visible. Foam then drifts
// with the water for a while, dissolves, and is replaced.
// ---------------------------------------------------------------------------

// Seconds for one advection cycle. Longer drifts further before resetting but
// stretches the pattern more; WaterFX uses three.
#define SOL_WATER_ADVECTION_PERIOD 3.0

struct SolWaterAdvectedUv
{
    float2 uv0;
    float2 uv1;
    float weight0;
    float weight1;
};

SolWaterAdvectedUv SolWaterBuildAdvectedUv(float2 baseUv, float2 flowDirection,
    float flowSpeed, float time)
{
    SolWaterAdvectedUv result;
    float cycle = time / SOL_WATER_ADVECTION_PERIOD;
    float phase0 = frac(cycle);
    float phase1 = frac(cycle + 0.5);

    // Each copy walks along the flow for one period, then snaps back.
    result.uv0 = baseUv - flowDirection * (phase0 * flowSpeed);
    result.uv1 = baseUv - flowDirection * (phase1 * flowSpeed);

    // Sine weighting takes each copy to zero exactly at its own reset, so the
    // discontinuity is always hidden behind the other copy. The square root
    // keeps the crossfade from dipping in total energy mid-blend.
    result.weight0 = sqrt(max(0.0, sin(phase0 * 3.14159265)));
    result.weight1 = sqrt(max(0.0, sin(phase1 * 3.14159265)));
    return result;
}

// Sub-pixel normal variation, used to widen the specular lobe. The spectral
// normal footprint filter deliberately removes short-wave detail with distance;
// folding the variance it discards back in as roughness is what turns a
// saturated highlight disc into a dispersed glitter path.
float SolWaterNormalVariance(float3 normalWS)
{
    float3 gradientX = ddx(normalWS);
    float3 gradientY = ddy(normalWS);
    return dot(gradientX, gradientX) + dot(gradientY, gradientY);
}

// Sun glitter as a GGX lobe with Smith joint visibility rather than a
// Blinn-Phong power. The subtractive clamp removes the low-energy tail so the
// highlight reads as a discrete glitter path instead of a broad sheen; the
// result is an HDR value intended to bloom.
float3 SolWaterSunSpecular(float3 normalWS, float3 viewDirection,
    float3 lightDirection, float3 lightColor, float shadow, float viewDistance,
    float roughnessValue, float strength, float maximumValue, float normalVariance)
{
    float3 halfDirection = SafeNormalize(lightDirection + viewDirection);
    float nh = saturate(dot(normalWS, halfDirection));
    float nl = saturate(dot(normalWS, lightDirection));
    float nv = saturate(dot(normalWS, viewDirection));
    // Widening the lobe with distance stops the far field aliasing into
    // sparkling pixels once a wave covers less than one pixel.
    float viewNormalized = saturate(viewDistance / max(1.0, _ProjectionParams.z * 2.0));
    float roughness = viewNormalized * 0.1 + max(0.005, roughnessValue);
    // Geometric specular antialiasing. A narrow lobe on a surface whose normals
    // vary within the pixel produces a peak far above any sane HDR ceiling, so
    // every pixel clamps to the ceiling and the highlight fills in as a solid
    // disc. Converting that normal variance into lobe width lowers the peak
    // where detail was lost and lets the highlight break into individual glints.
    roughness = saturate(sqrt(roughness * roughness
        + min(0.5, 2.0 * max(0.0, normalVariance))));
    float visibility = 0.5 / (nl * (nv * (1.0 - roughness) + roughness)
        + nv * (nl * (1.0 - roughness) + roughness) + 0.00001);
    float a2 = roughness * roughness;
    float denominator = (nh * a2 - nh) * nh + 1.0;
    float distribution = 0.31830988618 * a2 / (denominator * denominator + 0.0000001);
    float specular = max(0.0, visibility * distribution * nl * strength);
    specular = clamp(specular * 10.0
        - 2.5 * saturate(1.0 - roughnessValue * 10.0), 0.0, maximumValue);
    return shadow * specular * lightColor;
}

/// Spatial cloud shadowing for water.
///
/// Water reaches its main light through GetMainLight(shadowCoord), the overload without a
/// world position, and URP only applies a light cookie inside the overload that takes one.
/// So the ocean never receives the directional cookie that carries cloud shadows onto
/// terrain and Lit surfaces, and has to sample the shared map itself.
///
/// _SolCloudShadowStrength is zero whenever the cloud feature is not publishing a map, which
/// leaves this at 1.0 and hands shadowing back to the caller's low-frequency weather term.
TEXTURE2D(_SolCloudShadowTexture);
SAMPLER(sampler_SolCloudShadowTexture);
float4x4 _SolCloudShadowMatrix;
float _SolCloudShadowStrength;

float SolSampleCloudShadow(float3 positionWS)
{
    if (_SolCloudShadowStrength <= 0.0001)
        return 1.0;
    float2 uv = mul(_SolCloudShadowMatrix, float4(positionWS, 1.0)).xy;
    // Outside the mapped footprint the map is not authoritative. Its border is neutral
    // white, but clamping would still smear the edge texel to the horizon.
    float2 edge = min(uv, 1.0 - uv);
    if (min(edge.x, edge.y) <= 0.0)
        return 1.0;
    return SAMPLE_TEXTURE2D_LOD(_SolCloudShadowTexture,
        sampler_SolCloudShadowTexture, uv, 0).r;
}

#endif
