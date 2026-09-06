using System;
using UnityEngine;

/// <summary>Immutable, effective weather values shared by Sol environment consumers.</summary>
[Serializable]
public readonly struct SolWeatherState : IEquatable<SolWeatherState>
{
    public readonly SolCloudState Clouds;
    public float Cloudiness => Clouds.Coverage;
    public float CloudErosion => Clouds.Erosion;
    public readonly float RainIntensity;
    /// <summary>
    /// Authored shift applied to the temperature rain/snow split. 0 leaves temperature in
    /// charge, +1 forces the precipitation to fall as snow, -1 forces it to fall as rain.
    /// </summary>
    public readonly float SnowBias;
    public readonly Vector3 WindDirection;
    /// <summary>Horizontal wind speed in m/s at the standard 10 m reference height.</summary>
    public readonly float WindSpeedMetresPerSecond;
    public readonly float FogBoost;
    public readonly float Mistiness;
    public readonly float SkyObscuration;
    /// <summary>
    /// Minimum atmosphere extinction this weather insists on, in inverse metres. Zero
    /// leaves density to the fog boost and the climate model.
    /// </summary>
    public readonly float FogDensityFloor;
    public readonly float LightScattering;
    public readonly float Dim;
    public readonly float WaveSpeedMultiplier;
    public readonly float WaterTurbulence;
    public readonly float LightningIntensity;
    public readonly float LightningFlash;

    public SolWeatherState(
        SolCloudState clouds,
        float rainIntensity,
        Vector3 windDirection,
        float windSpeedMetresPerSecond,
        float fogBoost,
        float mistiness,
        float skyObscuration,
        float lightScattering,
        float dim,
        float waveSpeedMultiplier,
        float waterTurbulence,
        float lightningIntensity,
        float lightningFlash)
        : this(clouds, rainIntensity, 0f, windDirection, windSpeedMetresPerSecond, fogBoost,
            mistiness, skyObscuration, 0f, lightScattering, dim, waveSpeedMultiplier,
            waterTurbulence, lightningIntensity, lightningFlash) { }

    public SolWeatherState(
        SolCloudState clouds,
        float rainIntensity,
        float snowBias,
        Vector3 windDirection,
        float windSpeedMetresPerSecond,
        float fogBoost,
        float mistiness,
        float skyObscuration,
        float lightScattering,
        float dim,
        float waveSpeedMultiplier,
        float waterTurbulence,
        float lightningIntensity,
        float lightningFlash)
        : this(clouds, rainIntensity, snowBias, windDirection, windSpeedMetresPerSecond,
            fogBoost, mistiness, skyObscuration, 0f, lightScattering, dim,
            waveSpeedMultiplier, waterTurbulence, lightningIntensity, lightningFlash) { }

    public SolWeatherState(
        SolCloudState clouds,
        float rainIntensity,
        float snowBias,
        Vector3 windDirection,
        float windSpeedMetresPerSecond,
        float fogBoost,
        float mistiness,
        float skyObscuration,
        float fogDensityFloor,
        float lightScattering,
        float dim,
        float waveSpeedMultiplier,
        float waterTurbulence,
        float lightningIntensity,
        float lightningFlash)
    {
        Clouds = clouds;
        RainIntensity = Mathf.Clamp01(rainIntensity);
        SnowBias = Mathf.Clamp(snowBias, -1f, 1f);
        WindDirection = windDirection.sqrMagnitude > 0.0001f ? windDirection.normalized : Vector3.right;
        WindSpeedMetresPerSecond = Mathf.Max(0f, windSpeedMetresPerSecond);
        FogBoost = Mathf.Max(0f, fogBoost);
        Mistiness = Mathf.Clamp01(mistiness);
        SkyObscuration = Mathf.Clamp01(skyObscuration);
        FogDensityFloor = Mathf.Max(0f, fogDensityFloor);
        LightScattering = Mathf.Max(0f, lightScattering);
        Dim = Mathf.Clamp01(dim);
        WaveSpeedMultiplier = Mathf.Max(0f, waveSpeedMultiplier);
        WaterTurbulence = Mathf.Clamp01(waterTurbulence);
        LightningIntensity = Mathf.Clamp01(lightningIntensity);
        LightningFlash = Mathf.Clamp01(lightningFlash);
    }

    public SolWeatherState(
        float cloudiness,
        float cloudErosion,
        float rainIntensity,
        Vector3 windDirection,
        float windSpeedMetresPerSecond,
        float fogBoost,
        float mistiness,
        float skyObscuration,
        float lightScattering,
        float dim,
        float waveSpeedMultiplier,
        float waterTurbulence,
        float lightningIntensity,
        float lightningFlash)
        : this(SolCloudState.FromCompatibility(cloudiness, cloudErosion), rainIntensity,
            windDirection, windSpeedMetresPerSecond, fogBoost, mistiness, skyObscuration,
            lightScattering, dim, waveSpeedMultiplier, waterTurbulence, lightningIntensity,
            lightningFlash) { }

    /// <summary>Compatibility constructor for callers that predate mist and water turbulence.</summary>
    public SolWeatherState(
        float cloudiness,
        float cloudErosion,
        float rainIntensity,
        Vector3 windDirection,
        float windSpeedMetresPerSecond,
        float fogBoost,
        float skyObscuration,
        float lightScattering,
        float dim,
        float waveSpeedMultiplier,
        float lightningIntensity,
        float lightningFlash)
        : this(cloudiness, cloudErosion, rainIntensity, windDirection, windSpeedMetresPerSecond,
            fogBoost, 0f, skyObscuration, lightScattering, dim, waveSpeedMultiplier,
            0f, lightningIntensity, lightningFlash)
    {
    }

    /// <summary>Compatibility constructor for callers that predate explicit sky/lightning profile controls.</summary>
    public SolWeatherState(
        float cloudiness,
        float cloudErosion,
        float rainIntensity,
        Vector3 windDirection,
        float windSpeedMetresPerSecond,
        float fogBoost,
        float lightScattering,
        float dim,
        float waveSpeedMultiplier,
        float lightningFlash)
        : this(cloudiness, cloudErosion, rainIntensity, windDirection, windSpeedMetresPerSecond,
            fogBoost, 0f, 0f, lightScattering, dim, waveSpeedMultiplier, 0f, 1f, lightningFlash)
    {
    }

    public bool Equals(SolWeatherState other)
        => Clouds.Equals(other.Clouds)
        && Approximately(RainIntensity, other.RainIntensity)
        && Approximately(SnowBias, other.SnowBias)
        && (WindDirection - other.WindDirection).sqrMagnitude < 0.000001f
        && Approximately(WindSpeedMetresPerSecond, other.WindSpeedMetresPerSecond)
        && Approximately(FogBoost, other.FogBoost)
        && Approximately(Mistiness, other.Mistiness)
        && Approximately(SkyObscuration, other.SkyObscuration)
        && Approximately(FogDensityFloor, other.FogDensityFloor)
        && Approximately(LightScattering, other.LightScattering)
        && Approximately(Dim, other.Dim)
        && Approximately(WaveSpeedMultiplier, other.WaveSpeedMultiplier)
        && Approximately(WaterTurbulence, other.WaterTurbulence)
        && Approximately(LightningIntensity, other.LightningIntensity)
        && Approximately(LightningFlash, other.LightningFlash);

    public override bool Equals(object obj) => obj is SolWeatherState other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(
        HashCode.Combine(Clouds, RainIntensity, SnowBias, WindDirection, WindSpeedMetresPerSecond),
        HashCode.Combine(FogBoost, Mistiness, SkyObscuration, FogDensityFloor,
            LightScattering, Dim),
        HashCode.Combine(WaveSpeedMultiplier, WaterTurbulence, LightningIntensity, LightningFlash));

    public static bool operator ==(SolWeatherState left, SolWeatherState right) => left.Equals(right);
    public static bool operator !=(SolWeatherState left, SolWeatherState right) => !left.Equals(right);

    static bool Approximately(float a, float b) => Mathf.Abs(a - b) <= 0.0001f;
}
