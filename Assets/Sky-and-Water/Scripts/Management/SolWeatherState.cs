using System;
using UnityEngine;

/// <summary>Immutable, effective weather values shared by Sol environment consumers.</summary>
[Serializable]
public readonly struct SolWeatherState : IEquatable<SolWeatherState>
{
    public readonly float Cloudiness;
    public readonly float CloudErosion;
    public readonly float RainIntensity;
    public readonly Vector3 WindDirection;
    public readonly float WindStrength;
    public readonly float FogBoost;
    public readonly float Mistiness;
    public readonly float SkyObscuration;
    public readonly float LightScattering;
    public readonly float Dim;
    public readonly float WaveSpeedMultiplier;
    public readonly float WaterTurbulence;
    public readonly float LightningIntensity;
    public readonly float LightningFlash;

    public SolWeatherState(
        float cloudiness,
        float cloudErosion,
        float rainIntensity,
        Vector3 windDirection,
        float windStrength,
        float fogBoost,
        float mistiness,
        float skyObscuration,
        float lightScattering,
        float dim,
        float waveSpeedMultiplier,
        float waterTurbulence,
        float lightningIntensity,
        float lightningFlash)
    {
        Cloudiness = Mathf.Clamp01(cloudiness);
        CloudErosion = Mathf.Clamp01(cloudErosion);
        RainIntensity = Mathf.Clamp01(rainIntensity);
        WindDirection = windDirection.sqrMagnitude > 0.0001f ? windDirection.normalized : Vector3.right;
        WindStrength = Mathf.Max(0f, windStrength);
        FogBoost = Mathf.Max(0f, fogBoost);
        Mistiness = Mathf.Clamp01(mistiness);
        SkyObscuration = Mathf.Clamp01(skyObscuration);
        LightScattering = Mathf.Max(0f, lightScattering);
        Dim = Mathf.Clamp01(dim);
        WaveSpeedMultiplier = Mathf.Max(0f, waveSpeedMultiplier);
        WaterTurbulence = Mathf.Clamp01(waterTurbulence);
        LightningIntensity = Mathf.Clamp01(lightningIntensity);
        LightningFlash = Mathf.Clamp01(lightningFlash);
    }

    /// <summary>Compatibility constructor for callers that predate mist and water turbulence.</summary>
    public SolWeatherState(
        float cloudiness,
        float cloudErosion,
        float rainIntensity,
        Vector3 windDirection,
        float windStrength,
        float fogBoost,
        float skyObscuration,
        float lightScattering,
        float dim,
        float waveSpeedMultiplier,
        float lightningIntensity,
        float lightningFlash)
        : this(cloudiness, cloudErosion, rainIntensity, windDirection, windStrength,
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
        float windStrength,
        float fogBoost,
        float lightScattering,
        float dim,
        float waveSpeedMultiplier,
        float lightningFlash)
        : this(cloudiness, cloudErosion, rainIntensity, windDirection, windStrength,
            fogBoost, 0f, 0f, lightScattering, dim, waveSpeedMultiplier, 0f, 1f, lightningFlash)
    {
    }

    public bool Equals(SolWeatherState other)
        => Approximately(Cloudiness, other.Cloudiness)
        && Approximately(CloudErosion, other.CloudErosion)
        && Approximately(RainIntensity, other.RainIntensity)
        && (WindDirection - other.WindDirection).sqrMagnitude < 0.000001f
        && Approximately(WindStrength, other.WindStrength)
        && Approximately(FogBoost, other.FogBoost)
        && Approximately(Mistiness, other.Mistiness)
        && Approximately(SkyObscuration, other.SkyObscuration)
        && Approximately(LightScattering, other.LightScattering)
        && Approximately(Dim, other.Dim)
        && Approximately(WaveSpeedMultiplier, other.WaveSpeedMultiplier)
        && Approximately(WaterTurbulence, other.WaterTurbulence)
        && Approximately(LightningIntensity, other.LightningIntensity)
        && Approximately(LightningFlash, other.LightningFlash);

    public override bool Equals(object obj) => obj is SolWeatherState other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(
        HashCode.Combine(Cloudiness, CloudErosion, RainIntensity, WindDirection, WindStrength),
        HashCode.Combine(FogBoost, Mistiness, SkyObscuration, LightScattering, Dim),
        HashCode.Combine(WaveSpeedMultiplier, WaterTurbulence, LightningIntensity, LightningFlash));

    public static bool operator ==(SolWeatherState left, SolWeatherState right) => left.Equals(right);
    public static bool operator !=(SolWeatherState left, SolWeatherState right) => !left.Equals(right);

    static bool Approximately(float a, float b) => Mathf.Abs(a - b) <= 0.0001f;
}
