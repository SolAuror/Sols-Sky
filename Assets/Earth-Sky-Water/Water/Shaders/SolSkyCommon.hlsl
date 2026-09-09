#ifndef SOL_SKY_COMMON_INCLUDED
#define SOL_SKY_COMMON_INCLUDED

float _SolSkyFrameActive;
float4 _SolSkyZenithColor;
float4 _SolSkyHorizonColor;
float4 _SolSkyNadirColor;
float4 _SolSkyTwilightColor;
float4 _SolSkyAntiSolarColor;
float4 _SolSkySunDirection;
float4 _SolSkyGradientParams;    // zenith exponent, nadir exponent, horizon exponent, azimuth exponent
float4 _SolSkyDirectionalParams; // twilight amount, aureole intensity, aureole power, altitude factor

float3 SolEvaluateResolvedSkyRadiance(float3 direction)
{
    direction = normalize(direction);
    float upper = pow(saturate(direction.y), max(0.1, _SolSkyGradientParams.x));
    float lower = pow(saturate(-direction.y), max(0.1, _SolSkyGradientParams.y));
    float horizon = pow(saturate(1.0 - abs(direction.y)), max(0.1, _SolSkyGradientParams.z));
    float normalization = max(upper + lower + horizon, 1e-5);
    float3 radiance = (_SolSkyZenithColor.rgb * upper
                     + _SolSkyNadirColor.rgb * lower
                     + _SolSkyHorizonColor.rgb * horizon) / normalization;

    float2 directionAzimuth = direction.xz;
    float2 sunAzimuth = _SolSkySunDirection.xz;
    float azimuthLength = max(length(directionAzimuth) * length(sunAzimuth), 1e-4);
    float azimuthAlignment = dot(directionAzimuth, sunAzimuth) / azimuthLength;
    float towardSun = pow(saturate(azimuthAlignment * 0.5 + 0.5),
        max(0.5, _SolSkyGradientParams.w));
    float awayFromSun = pow(saturate(-azimuthAlignment * 0.5 + 0.5),
        max(0.5, _SolSkyGradientParams.w));
    float twilightMask = saturate(_SolSkyDirectionalParams.x) * horizon;
    radiance = lerp(radiance, _SolSkyTwilightColor.rgb, twilightMask * towardSun);
    radiance = lerp(radiance, _SolSkyAntiSolarColor.rgb,
        twilightMask * awayFromSun * 0.42);

    float solarAlignment = saturate(dot(direction, normalize(_SolSkySunDirection.xyz)));
    float aureole = pow(solarAlignment, max(1.0, _SolSkyDirectionalParams.z))
                  * max(0.0, _SolSkyDirectionalParams.y)
                  * saturate(1.0 - _SolSkyDirectionalParams.w * 0.55);
    radiance += _SolSkyTwilightColor.rgb * aureole;
    return max(radiance, 0.0);
}

#endif
