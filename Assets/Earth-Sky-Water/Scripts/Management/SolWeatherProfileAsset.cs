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
    [Header("Cloud formation")]
    public SolCloudFormation cloudFormation = SolCloudFormation.Cumulus;

    [Tooltip("0 = leave the authored ToD cloud settings, 1 = fully overcast.")]
    [Range(0f, 1f)] public float cloudiness;

    [Tooltip("Breakup applied to cloud edges (0 = soft masses, 1 = strongly eroded).")]
    [Range(0f, 1f)] public float cloudErosion = 0.35f;

    [Tooltip("Use the formation-specific volumetric shape controls below.")]
    public bool overrideAdvancedCloudShape;
    [Min(0f)] public float cloudDensity = 0.72f;
    [Min(1f)] public float cloudBaseHeight = 1500f;
    [Min(10f)] public float cloudThickness = 3200f;
    [Range(0f, 1f)] public float cloudVerticalDevelopment = 0.45f;
    [Range(0f, 1f)] public float cloudAnvilAmount;
    [Range(0f, 1f)] public float cirrusAmount = 0.18f;
    [Tooltip("Density boundary width. 0 = hard cauliflower edges, 1 = soft stratus haze.")]
    [Range(0f, 1f)] public float cloudEdgeSoftness = 0.5f;
    [Tooltip("Underside diffuseness. 0 = crisp flat base, 1 = smeared nimbostratus base.")]
    [Range(0f, 1f)] public float cloudBaseSoftness = 0.5f;
    [Tooltip("Additive coverage nudge, independent of how far cloudiness overrides the authored sky.")]
    [Range(-1f, 1f)] public float cloudCoverageBias;
    [Range(0f, 1f)] public float cloudShadowStrength = 0.65f;

    [Tooltip("Per-world-day swing applied to the coverage bias, so the same weather is not "
        + "identical every time it occurs. 0 pins the profile to its authored cover.")]
    [Range(0f, 1f)] public float cloudCoverageVariance;

    [Tooltip("Precipitation intensity pushed to SolWaterManager (0 = dry, 1 = downpour). "
        + "Temperature and snowBias decide how much of it falls as snow rather than rain.")]
    [Range(0f, 1f)] public float rainIntensity;

    [Tooltip("Shifts the temperature rain/snow split. 0 = temperature decides, "
        + "+1 forces snow, -1 forces rain.")]
    [Range(-1f, 1f)] public float snowBias;

    [Tooltip("Horizontal wind speed in metres per second at the standard 10 m reference height.")]
    [Range(0f, 24f)] public float windSpeedMetresPerSecond = 8f;

    [Tooltip("Additional fog density multiplier (0 = none, 1 = double).")]
    [Range(0f, 2f)] public float fogBoost;

    [Tooltip("Moves atmosphere density toward the low-mist height profile without adding density.")]
    [Range(0f, 1f)] public float mistiness;

    [Tooltip("Additional sky-wide atmosphere obscuration. Horizon fog remains independently depth driven.")]
    [Range(0f, 1f)] public float skyObscuration;

    [Tooltip("Distance at which this weather hides the scene, in metres. 0 leaves fog "
        + "density entirely to fogBoost and the climate model. Authored conditions that "
        + "must reach a true whiteout set it directly; fogBoost alone cannot get there.")]
    [Min(0f)] public float visibilityMetres;

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
        cloudDensity = Mathf.Max(0f, cloudDensity);
        cloudBaseHeight = Mathf.Max(1f, cloudBaseHeight);
        cloudThickness = Mathf.Max(10f, cloudThickness);
        cloudVerticalDevelopment = Mathf.Clamp01(cloudVerticalDevelopment);
        cloudAnvilAmount = Mathf.Clamp01(cloudAnvilAmount);
        cirrusAmount = Mathf.Clamp01(cirrusAmount);
        cloudEdgeSoftness = Mathf.Clamp01(cloudEdgeSoftness);
        cloudBaseSoftness = Mathf.Clamp01(cloudBaseSoftness);
        cloudCoverageBias = Mathf.Clamp(cloudCoverageBias, -1f, 1f);
        cloudShadowStrength = Mathf.Clamp01(cloudShadowStrength);
        cloudCoverageVariance = Mathf.Clamp01(cloudCoverageVariance);
        rainIntensity = Mathf.Clamp01(rainIntensity);
        snowBias = Mathf.Clamp(snowBias, -1f, 1f);
        windSpeedMetresPerSecond = Mathf.Clamp(windSpeedMetresPerSecond, 0f, 24f);
        fogBoost = Mathf.Clamp(fogBoost, 0f, 2f);
        mistiness = Mathf.Clamp01(mistiness);
        skyObscuration = Mathf.Clamp01(skyObscuration);
        visibilityMetres = Mathf.Max(0f, visibilityMetres);
        lightScattering = Mathf.Clamp(lightScattering, 0f, 2f);
        dim = Mathf.Clamp01(dim);
        waveSpeedMultiplier = Mathf.Clamp(waveSpeedMultiplier, 0f, 3f);
        waterTurbulence = Mathf.Clamp01(waterTurbulence);
        lightningIntensity = Mathf.Clamp01(lightningIntensity);
    }

    /// <summary>
    /// Extinction coefficient that hides the scene at <see cref="visibilityMetres"/>,
    /// taking 2% remaining transmittance as the visibility threshold. Returned rather than
    /// the raw distance because a density floor blends correctly through a transition
    /// while a distance does not: lerping metres from "unused" would sweep through every
    /// value denser than the target on the way there.
    /// </summary>
    public float ResolveFogDensityFloor()
        => visibilityMetres > 0.01f
            ? -Mathf.Log(0.02f) / visibilityMetres
            : 0f;

    /// <summary>
    /// The cover this profile actually renders at, given the deck TimeOfDay authors.
    ///
    /// cloudiness is an influence toward overcast, not an absolute cover, so it cannot be
    /// compared between profiles on its own: Clear and Fair both author 0 and are told
    /// apart only by their coverage bias. Reading the bias off ResolveCloudState rather
    /// than off the field matters too -- a profile that does not override its shape
    /// inherits its formation preset bias, and the authored field is then dead data.
    /// </summary>
    public float ResolveEffectiveCoverage(float authoredCoverage)
        => Mathf.Clamp01(
            Mathf.Lerp(Mathf.Clamp01(authoredCoverage), 1f, Mathf.Clamp01(cloudiness))
            + ResolveCloudState().CoverageBias);

    public SolCloudState ResolveCloudState()
    {
        SolCloudState defaults = DefaultsFor(cloudFormation, cloudiness, cloudErosion);
        if (!overrideAdvancedCloudShape)
            return defaults;

        return new SolCloudState(cloudFormation, cloudiness, cloudErosion, cloudDensity,
            cloudBaseHeight, cloudThickness, cloudVerticalDevelopment, cloudAnvilAmount,
            cirrusAmount, cloudEdgeSoftness, cloudBaseSoftness, cloudCoverageBias,
            Vector2.zero, Vector2.zero, Vector2.zero, cloudShadowStrength);
    }

    /// <summary>
    /// The shape a formation resolves to when a profile does not override it. Exposed so
    /// the shipped assets can be validated against it rather than restating the numbers.
    /// </summary>
    public static SolCloudState FormationDefaults(
        SolCloudFormation formation, float coverage, float erosion)
        => DefaultsFor(formation, coverage, erosion);

    static SolCloudState DefaultsFor(SolCloudFormation formation, float coverage, float erosion)
        => formation switch
        {
            // Softness per formation: a stratus deck has no edge to speak of, a
            // cumulus tower is all edge, and a cumulonimbus is hard on top and soft
            // underneath where the rain shaft begins.
            SolCloudFormation.Stratus => new SolCloudState(formation, coverage, erosion, 0.82f,
                1100f, 1800f, 0.18f, 0f, 0.08f, 0.82f, 0.7f, 0f,
                Vector2.zero, Vector2.zero, Vector2.zero, 0.72f),
            SolCloudFormation.Nimbostratus => new SolCloudState(formation, coverage, erosion, 1.05f,
                850f, 2600f, 0.3f, 0f, 0.05f, 0.7f, 0.85f, 0.05f,
                Vector2.zero, Vector2.zero, Vector2.zero, 0.82f),
            SolCloudFormation.Cumulonimbus => new SolCloudState(formation, coverage, erosion, 1.2f,
                900f, 7200f, 0.95f, 0.72f, 0.16f, 0.24f, 0.6f, 0.04f,
                Vector2.zero, Vector2.zero, Vector2.zero, 0.92f),
            _ => new SolCloudState(formation, coverage, erosion, 0.72f,
                1500f, 3200f, 0.45f, 0f, 0.2f, 0.18f, 0.2f, 0f,
                Vector2.zero, Vector2.zero, Vector2.zero, 0.62f),
        };
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
