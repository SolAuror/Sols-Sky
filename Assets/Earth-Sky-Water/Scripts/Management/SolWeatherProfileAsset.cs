using System;
using UnityEngine;

/// <summary>
/// Reusable presentation values for one named weather condition. Selection probability
/// deliberately does not live here: two scenes can share the same Storm presentation
/// while choosing it at different seasonal frequencies.
/// </summary>
[CreateAssetMenu(menuName = "Sol/Environment/Weather Profile")]
public sealed class SolWeatherProfileAsset : ScriptableObject
{
    [Tooltip("0 = leave the authored ToD cloud settings, 1 = fully overcast.")]
    [Range(0f, 1f)] public float cloudiness;

    [Tooltip("Breakup applied to cloud edges (0 = soft masses, 1 = strongly eroded).")]
    [Range(0f, 1f)] public float cloudErosion = 0.35f;

    [Tooltip("Rain intensity pushed to SolWaterManager (0 = dry, 1 = downpour).")]
    [Range(0f, 1f)] public float rainIntensity;

    [Tooltip("Wind strength pushed to SolWaterManager.")]
    [Range(0f, 3f)] public float windStrength = 1f;

    [Tooltip("Additional fog density multiplier (0 = none, 1 = double).")]
    [Range(0f, 2f)] public float fogBoost;

    [Tooltip("Moves atmosphere density toward the low-mist height profile without adding density.")]
    [Range(0f, 1f)] public float mistiness;

    [Tooltip("Additional sky-wide atmosphere obscuration. Horizon fog remains independently depth driven.")]
    [Range(0f, 1f)] public float skyObscuration;

    [Tooltip("Directional atmosphere light-scattering multiplier.")]
    [Range(0f, 2f)] public float lightScattering = 0.4f;

    [Tooltip("Storm darkening applied to sun/moon, ambient, sky, and clouds.")]
    [Range(0f, 1f)] public float dim;

    [Tooltip("Global wave speed multiplier pushed to SolWaterManager.")]
    [Range(0f, 3f)] public float waveSpeedMultiplier = 1f;

    [Tooltip("Art-directed water disorder: wave detail, steepness, swell, foam, roughness, and drift.")]
    [Range(0f, 1f)] public float waterTurbulence;

    [Tooltip("Enable random lightning flashes during this weather.")]
    public bool lightning;

    [Tooltip("Peak effective lightning flash contributed by this profile.")]
    [Range(0f, 1f)] public float lightningIntensity = 0.35f;

    void OnValidate()
    {
        cloudiness = Mathf.Clamp01(cloudiness);
        cloudErosion = Mathf.Clamp01(cloudErosion);
        rainIntensity = Mathf.Clamp01(rainIntensity);
        windStrength = Mathf.Clamp(windStrength, 0f, 3f);
        fogBoost = Mathf.Clamp(fogBoost, 0f, 2f);
        mistiness = Mathf.Clamp01(mistiness);
        skyObscuration = Mathf.Clamp01(skyObscuration);
        lightScattering = Mathf.Clamp(lightScattering, 0f, 2f);
        dim = Mathf.Clamp01(dim);
        waveSpeedMultiplier = Mathf.Clamp(waveSpeedMultiplier, 0f, 3f);
        waterTurbulence = Mathf.Clamp01(waterTurbulence);
        lightningIntensity = Mathf.Clamp01(lightningIntensity);
    }
}

/// <summary>Scene-local selection policy for a reusable weather presentation asset.</summary>
[Serializable]
public sealed class SolWeatherSelection
{
    [Tooltip("Reusable weather presentation selected by this entry.")]
    public SolWeatherProfileAsset profile;

    [Tooltip("Relative chance of this profile being picked by the auto cycle.")]
    [Min(0f)] public float weight = 1f;

    [Tooltip("Automatic-selection multiplier during Spring.")]
    [Min(0f)] public float springWeightMultiplier = 1f;

    [Tooltip("Automatic-selection multiplier during Summer.")]
    [Min(0f)] public float summerWeightMultiplier = 1f;

    [Tooltip("Automatic-selection multiplier during Autumn.")]
    [Min(0f)] public float autumnWeightMultiplier = 1f;

    [Tooltip("Automatic-selection multiplier during Winter.")]
    [Min(0f)] public float winterWeightMultiplier = 1f;
}
