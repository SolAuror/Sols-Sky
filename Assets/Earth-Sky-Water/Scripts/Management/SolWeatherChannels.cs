using UnityEngine;

/// <summary>
/// The blendable channel set for one weather condition -- a flattened SolWeatherProfileAsset.
/// Promoted out of SolWeatherManager so the lead/lag choreography in
/// SolWeatherTransitionTimings can be exercised directly by tests and by the editor A/B
/// preview, rather than only through the manager's private state.
///
/// Snapshots of the presentation carry these rather than a published SolWeatherState:
/// that state already has the daily climate folded into its fog and mist, so restoring
/// from it would double-count the climate contribution.
/// </summary>
public struct SolWeatherChannels
{
    public SolCloudState clouds;
    public float cloudiness, cloudErosion, rain, snowBias, windSpeedMetresPerSecond, fog,
        mistiness, skyObscuration, fogDensityFloor,
        scattering, dim, waveMul, turbulence, lightningIntensity, coverageVariance;

    public static SolWeatherChannels From(SolWeatherProfileAsset p)
    {
        if (p == null)
            return new SolWeatherChannels { scattering = 1f, waveMul = 1f };

        return new SolWeatherChannels
        {
            clouds = p.ResolveCloudState(),
            cloudiness = p.cloudiness,
            cloudErosion = p.cloudErosion,
                coverageVariance = p.cloudCoverageVariance,
            rain = p.rainIntensity,
            snowBias = p.snowBias,
            windSpeedMetresPerSecond = p.windSpeedMetresPerSecond,
            fog = p.fogBoost,
            mistiness = p.mistiness,
            skyObscuration = p.skyObscuration,
            fogDensityFloor = p.ResolveFogDensityFloor(),
            scattering = p.lightScattering,
            dim = p.dim,
            waveMul = p.waveSpeedMultiplier,
            turbulence = p.waterTurbulence,
            lightningIntensity = p.lightning ? p.lightningIntensity : 0f,
        };
    }

    /// <summary>
    /// Uniform blend, kept for callers that want every channel on one curve.
    /// </summary>
    public static SolWeatherChannels Lerp(in SolWeatherChannels a, in SolWeatherChannels b, float t)
        => Lerp(a, b, null, t);

    /// <summary>
    /// Blends with per-channel lead/lag. A null timing table falls back to the uniform
    /// curve, so a scene that has never been opened since this was added still behaves
    /// exactly as it did before.
    /// </summary>
    public static SolWeatherChannels Lerp(
        in SolWeatherChannels a, in SolWeatherChannels b, SolWeatherTransitionTimings timings, float master)
    {
        float t = Mathf.Clamp01(master);
        float cloudT = timings != null ? timings.clouds.Evaluate(t) : t;
        float windT = timings != null ? timings.wind.Evaluate(t) : t;
        float fogT = timings != null ? timings.fog.Evaluate(t) : t;
        float waterT = timings != null ? timings.water.Evaluate(t) : t;
        float lightT = timings != null ? timings.light.Evaluate(t) : t;
        float lightningT = timings != null ? timings.lightning.Evaluate(t) : t;
        float precipitationT = timings != null ? timings.precipitation.Evaluate(t) : t;

        if (timings != null)
            precipitationT *= SolWeatherTransitionTimings.PrecipitationCoverGate(
                a.clouds.Coverage, b.clouds.Coverage, cloudT);

        return new SolWeatherChannels
        {
            clouds = SolCloudState.Lerp(a.clouds, b.clouds, cloudT),
            cloudiness = Mathf.Lerp(a.cloudiness, b.cloudiness, cloudT),
            cloudErosion = Mathf.Lerp(a.cloudErosion, b.cloudErosion, cloudT),
                coverageVariance = Mathf.Lerp(
                    a.coverageVariance, b.coverageVariance, cloudT),
            rain = Mathf.Lerp(a.rain, b.rain, precipitationT),
            snowBias = Mathf.Lerp(a.snowBias, b.snowBias, precipitationT),
            windSpeedMetresPerSecond = Mathf.Lerp(
                a.windSpeedMetresPerSecond, b.windSpeedMetresPerSecond, windT),
            fog = Mathf.Lerp(a.fog, b.fog, fogT),
            mistiness = Mathf.Lerp(a.mistiness, b.mistiness, fogT),
            skyObscuration = Mathf.Lerp(a.skyObscuration, b.skyObscuration, fogT),
            fogDensityFloor = Mathf.Lerp(a.fogDensityFloor, b.fogDensityFloor, fogT),
            scattering = Mathf.Lerp(a.scattering, b.scattering, lightT),
            dim = Mathf.Lerp(a.dim, b.dim, lightT),
            waveMul = Mathf.Lerp(a.waveMul, b.waveMul, waterT),
            turbulence = Mathf.Lerp(a.turbulence, b.turbulence, waterT),
            lightningIntensity = Mathf.Lerp(
                a.lightningIntensity, b.lightningIntensity, lightningT),
        };
    }
}
