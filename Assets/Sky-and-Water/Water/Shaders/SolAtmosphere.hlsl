#ifndef SOL_ATMOSPHERE_INCLUDED
#define SOL_ATMOSPHERE_INCLUDED

float _SolAtmosphereActive;
float4 _SolAtmosphereFogColor;
float4 _SolAtmosphereParams0; // density, start distance, max distance, max opacity
float4 _SolAtmosphereParams1; // base height, falloff, noise intensity, noise scale
float4 _SolAtmosphereParams2; // directional scattering, anisotropy, sky strength, quality
float4 _SolAtmosphereVolumetricParams; // shadow strength, distance, steps, jitter
float4 _SolAtmosphereUpsampleParams; // depth threshold, spatial filter strength, reserved
float4 _SolAtmosphereSkyParams; // zenith strength, horizon strength, saturation, ambient scattering
float4 _SolAtmosphereLightingParams; // sky obscuration, cloudiness, storm dim, max luminance
float4 _SolAtmosphereSunDirection;
float4 _SolAtmosphereSunColor;
float4 _SolAtmosphereWindTime; // wind xz, accumulated time, strength
float _SolAtmosphereLightning;
float _SolAtmosphereLightningScattering;
float _SolAtmosphereLightAvailable;

float SolAtmosphereHash(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float SolAtmosphereNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(
        lerp(SolAtmosphereHash(i), SolAtmosphereHash(i + float2(1, 0)), f.x),
        lerp(SolAtmosphereHash(i + float2(0, 1)), SolAtmosphereHash(i + 1.0), f.x),
        f.y);
}

float SolAtmosphereHeightIntegral(float startHeight, float verticalSlope, float distanceAlongRay, float falloff)
{
    if (distanceAlongRay <= 0.0)
        return 0.0;

    falloff = max(falloff, 0.000001);
    float endHeight = startHeight + verticalSlope * distanceAlongRay;
    if (startHeight <= 0.0 && endHeight <= 0.0)
        return distanceAlongRay;

    if (startHeight >= 0.0 && endHeight >= 0.0)
    {
        if (abs(verticalSlope) < 0.000001)
            return distanceAlongRay * exp(-falloff * startHeight);
        return max(0.0, (exp(-falloff * startHeight) - exp(-falloff * endHeight))
            / (falloff * verticalSlope));
    }

    float crossing = clamp(-startHeight / verticalSlope, 0.0, distanceAlongRay);
    if (startHeight < 0.0)
    {
        float upperDistance = distanceAlongRay - crossing;
        return crossing + max(0.0, (1.0 - exp(-falloff * verticalSlope * upperDistance))
            / (falloff * verticalSlope));
    }

    float upperIntegral = (exp(-falloff * startHeight) - 1.0)
        / (falloff * verticalSlope);
    return max(0.0, upperIntegral) + distanceAlongRay - crossing;
}

float SolAtmosphereNoiseMultiplier(float3 positionWS)
{
    if (_SolAtmosphereParams1.z <= 0.001 || _SolAtmosphereParams2.w < 0.5)
        return 1.0;

    // Rotate and shear the low-frequency domain so height fog does not expose
    // axis-aligned or vertically repeated XZ cells.
    float2 domain = float2(
        positionWS.x * 0.8 + positionWS.z * 0.6 + positionWS.y * 0.19,
        positionWS.z * 0.8 - positionWS.x * 0.6 - positionWS.y * 0.13);
    float2 samplePosition = domain * _SolAtmosphereParams1.w
                          + _SolAtmosphereWindTime.xy * _SolAtmosphereWindTime.z;
    float noise = SolAtmosphereNoise(samplePosition);
    if (_SolAtmosphereParams2.w > 1.5)
        noise = noise * 0.7 + SolAtmosphereNoise(samplePosition * 2.03 + 7.1) * 0.3;
    return max(0.05, 1.0 + (noise - 0.5) * 2.0 * _SolAtmosphereParams1.z);
}

float SolAtmosphereDensityAt(float3 positionWS)
{
    float height = max(0.0, positionWS.y - _SolAtmosphereParams1.x);
    float heightDensity = exp(-height * _SolAtmosphereParams1.y);
    return max(0.0, _SolAtmosphereParams0.x * heightDensity
        * SolAtmosphereNoiseMultiplier(positionWS));
}

float SolAtmosphereOpticalDepth(float3 cameraWS, float3 viewDirection, float distanceToPoint)
{
    float fogDistance = max(0.0, distanceToPoint - _SolAtmosphereParams0.y);
    if (fogDistance <= 0.0)
        return 0.0;

    float startOffset = distanceToPoint - fogDistance;
    float3 startWS = cameraWS + viewDirection * startOffset;
    float startHeight = startWS.y - _SolAtmosphereParams1.x;
    float heightIntegral = SolAtmosphereHeightIntegral(
        startHeight,
        viewDirection.y,
        fogDistance,
        _SolAtmosphereParams1.y);
    float3 midpointWS = startWS + viewDirection * (fogDistance * 0.5);
    return max(0.0, _SolAtmosphereParams0.x * heightIntegral
        * SolAtmosphereNoiseMultiplier(midpointWS));
}

float SolAtmosphereCornetteShanks(float cosineTheta, float anisotropy)
{
    float g = clamp(anisotropy, -0.9, 0.9);
    float c = clamp(cosineTheta, -1.0, 1.0);
    float denominatorBase = max(0.0001, 1.0 + g * g - 2.0 * g * c);
    float normalized = 3.0 * (1.0 - g * g) * (1.0 + c * c)
        / (8.0 * PI * (2.0 + g * g) * pow(denominatorBase, 1.5));
    return normalized;
}

float3 SolAtmosphereAmbientColor()
{
    float luminance = dot(_SolAtmosphereFogColor.rgb, float3(0.2126, 0.7152, 0.0722));
    float3 desaturated = lerp(luminance.xxx, _SolAtmosphereFogColor.rgb,
        saturate(_SolAtmosphereSkyParams.z));
    return desaturated * max(0.0, _SolAtmosphereSkyParams.w);
}

float3 SolAtmosphereClampLuminance(float3 color)
{
    float luminance = dot(max(color, 0.0), float3(0.2126, 0.7152, 0.0722));
    float maximum = max(0.01, _SolAtmosphereLightingParams.w);
    return luminance > maximum ? color * (maximum / luminance) : color;
}

float3 SolAtmosphereLighting(float3 viewDirection, float shadowAttenuation)
{
    float lightDot = dot(viewDirection, normalize(_SolAtmosphereSunDirection.xyz));
    float phase = SolAtmosphereCornetteShanks(lightDot, _SolAtmosphereParams2.y);
    float shadow = lerp(1.0, saturate(shadowAttenuation), _SolAtmosphereVolumetricParams.x);
    float ambientVisibility = (1.0 - saturate(_SolAtmosphereLightingParams.z) * 0.55)
                            * (1.0 - saturate(_SolAtmosphereLightingParams.y) * 0.15);
    float directVisibility = (1.0 - saturate(_SolAtmosphereLightingParams.y) * 0.85)
                           * (1.0 - saturate(_SolAtmosphereLightingParams.z) * 0.65);
    float3 lighting = SolAtmosphereAmbientColor() * ambientVisibility
        + _SolAtmosphereSunColor.rgb * phase * _SolAtmosphereParams2.x * shadow * directVisibility
        + _SolAtmosphereLightning.xxx * max(0.0, _SolAtmosphereLightningScattering);
    return SolAtmosphereClampLuminance(lighting);
}

float SolAtmosphereSkyOpticalDepthScale(float3 viewDirection)
{
    float horizon = pow(saturate(1.0 - abs(viewDirection.y)), 3.0);
    float shaped = lerp(max(0.0, _SolAtmosphereSkyParams.x),
        max(0.0, _SolAtmosphereSkyParams.y), horizon);
    shaped = lerp(shaped, max(0.0, _SolAtmosphereSkyParams.y),
        saturate(_SolAtmosphereLightingParams.x));
    return max(0.0, _SolAtmosphereParams2.z) * shaped;
}

float SolAtmosphereAmount(float3 cameraWS, float3 positionWS, float3 viewDirection, float isSky)
{
    float distanceToPoint = isSky > 0.5
        ? _SolAtmosphereParams0.z
        : min(distance(cameraWS, positionWS), _SolAtmosphereParams0.z);
    float opticalDepth = SolAtmosphereOpticalDepth(cameraWS, viewDirection, distanceToPoint);
    if (isSky > 0.5)
        opticalDepth *= SolAtmosphereSkyOpticalDepthScale(viewDirection);
    float amount = 1.0 - exp(-opticalDepth);
    return min(amount, _SolAtmosphereParams0.w);
}

float3 SolApplyAtmosphere(float3 color, float3 cameraWS, float3 positionWS, float3 viewDirection, float isSky)
{
    if (_SolAtmosphereActive < 0.5)
        return color;

    float amount = SolAtmosphereAmount(cameraWS, positionWS, viewDirection, isSky);
    float transmittance = 1.0 - amount;
    float3 inScattering = SolAtmosphereLighting(viewDirection, 1.0) * amount;
    return color * transmittance + inScattering;
}

#endif
