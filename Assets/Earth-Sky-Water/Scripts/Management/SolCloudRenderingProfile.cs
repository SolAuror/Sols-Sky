using Sol.ToD;
using UnityEngine;

public enum SolCloudDebugView
{
    FinalLighting = 0,
    MacroDensity = 1,
    AuthoredStructure = 2,
    MicroErosion = 3,
    OpticalDepth = 4,
}

[CreateAssetMenu(menuName = "Sol/Environment/Cloud Rendering Profile", fileName = "Sol Cloud Rendering Profile")]
public sealed class SolCloudRenderingProfile : ScriptableObject
{
    [Header("Planet and shell")]
    [Min(1000f)] public float planetRadius = 6371000f;
    [Min(100f)] public float maximumRayDistance = 160000f;

    [Header("Density textures")]
    public Texture2D packedShapeNoise;
    [Tooltip("Optional artist-authored RGBA structure map. R shapes medium billows, G/B "
        + "warp the volume domain, and A shapes wisps/cirrus. Falls back to Packed Shape "
        + "Noise so existing profiles remain valid.")]
    public Texture2D authoredStructureMap;
    public Texture2D weatherMap;
    public Texture3D shapeNoiseVolume;
    public Texture3D detailNoiseVolume;
    [Min(100f)] public float shapeScaleMetres = 12000f;
    [Min(10f)] public float detailScaleMetres = 1800f;
    [Min(1000f)] public float weatherScaleMetres = 85000f;

    [Header("Authored mesostructure")]
    [Min(100f)] public float structureScaleMetres = 8000f;
    [Range(0f, 1f)] public float structureInfluence = 0.38f;
    [Tooltip("Divergence-free swirl applied to the micro-detail lookup only. Curling the "
        + "base shape instead makes whole cloud masses swim under temporal reprojection.")]
    [Range(0f, 2f)] public float curlWarpStrength = 0.6f;

    [Tooltip("Extra density applied to distant cloud banks so they read as solid rather "
        + "than washing out into the atmosphere.")]
    [Range(0f, 1f)] public float distanceDensityGain = 0.45f;

    [Range(0f, 0.5f)] public float structureWarpStrength = 0.12f;
    [Min(0f)] public float microDetailFadeStartMetres = 25000f;
    [Min(1000f)] public float microDetailFadeEndMetres = 45000f;

    [Header("Advection")]
    [Tooltip("Cloud-layer wind is normally faster than the weather profile's 10 m wind. "
        + "This multiplier preserves physical direction while making cloud motion legible.")]
    [Range(0f, 4f)] public float cloudAdvectionMultiplier = 2f;

    [Header("Temporal stability")]
    [Tooltip("Temporal accumulation starts reducing beyond this shell-entry distance.")]
    [Min(1000f)] public float temporalRejectStartMetres = 35000f;
    [Tooltip("Temporal accumulation is disabled beyond this shell-entry distance.")]
    [Min(1000f)] public float temporalRejectEndMetres = 85000f;

    [Header("Optics")]
    [Min(0.001f)] public float extinctionPerKilometre = 1.35f;
    [Range(-0.9f, 0.9f)] public float forwardAnisotropy = 0.62f;
    [Range(-0.9f, 0.9f)] public float backwardAnisotropy = -0.2f;
    [Range(0f, 1f)] public float backwardLobeWeight = 0.18f;
    [Range(0f, 2f)] public float ambientIntensity = 0.72f;
    [Range(0f, 2f)] public float powderIntensity = 0.75f;
    [Tooltip("Legacy serialized light-step control retained for profile compatibility. New "
        + "lighting spans the shell using Maximum Light March Metres.")]
    [Min(10f)] public float lightStepMetres = 1400f;
    [Tooltip("Maximum physically relevant shell distance integrated toward the sun or moon.")]
    [Min(1000f)] public float maximumLightMarchMetres = 30000f;

    [Header("Sculpting")]
    [Tooltip("How hard the mid and high frequency detail bands carve the silhouette.")]
    [Range(0f, 1f)] public float sculptingStrength = 0.35f;
    [Tooltip("Boundary-only billow relief derived from the spread of the four baked masses.")]
    [Range(0f, 1f)] public float baseLobeStrength = 0.3f;
    [Tooltip("One low-cost density gradient is evaluated when a Medium/High ray enters cloud.")]
    [Range(0f, 0.5f)] public float surfaceGradientStrength = 0.18f;

    [Header("Internal lighting")]
    [Tooltip("Weight of the second scattering octave. Keeps dense bodies lit inside.")]
    [Range(0f, 1f)] public float multipleScatteringStrength = 0.55f;
    [Tooltip("Diffused glow through sun-facing transition regions. Not a rim light.")]
    [Range(0f, 1f)] public float innerGlowStrength = 0.35f;
    [Tooltip("Ground-bounce share of the ambient term underneath the deck.")]
    [Range(0f, 1f)] public float ambientBottomMultiplier = 0.38f;
    [Tooltip("Extra absorption and desaturation at full precipitation.")]
    [Range(0f, 1f)] public float stormAbsorption = 0.45f;
    [Tooltip("Fill from the non-dominant celestial body during the sun/moon handover.")]
    [Range(0f, 1f)] public float secondaryCelestialStrength = 0.2f;

    [Header("Lightning")]
    [ColorUsage(false, true)] public Color lightningColor = new(0.78f, 0.86f, 1f);
    [Range(0f, 4f)] public float lightningEmissionStrength = 0.8f;

    [Header("World cloud shadows")]
    public bool enableWorldCloudShadows = true;
    [HideInInspector]
    [Min(1000f)] public float shadowFootprintMetres = 12000f;
    [Min(1000f)] public float lowShadowFootprintMetres = 16000f;
    [Min(1000f)] public float mediumShadowFootprintMetres = 24000f;
    [Min(1000f)] public float highShadowFootprintMetres = 32000f;
    [Range(0f, 1f)] public float worldShadowStrength = 0.85f;

    [Header("Quality budgets")]
    [Range(2, 8)] public int lowViewSteps = 4;
    [Range(16, 64)] public int mediumViewSteps = 32;
    [Range(24, 64)] public int highViewSteps = 48;
    [Range(1, 8)] public int mediumLightSteps = 4;
    [Range(1, 10)] public int highLightSteps = 6;
    [Range(0f, 0.98f)] public float mediumHistoryWeight = 0.72f;
    [Range(0f, 0.98f)] public float highHistoryWeight = 0.84f;
    [Min(0.01f)] public float bilateralDepthThreshold = 3f;

    [Header("Development")]
    public SolCloudDebugView debugView = SolCloudDebugView.FinalLighting;

    public int ViewSteps(SolCloudQuality quality) => quality switch
    {
        SolCloudQuality.Low => lowViewSteps,
        SolCloudQuality.High => highViewSteps,
        _ => mediumViewSteps,
    };

    public int LightSteps(SolCloudQuality quality) => quality switch
    {
        SolCloudQuality.Low => 0,
        SolCloudQuality.High => highLightSteps,
        _ => mediumLightSteps,
    };

    public float HistoryWeight(SolCloudQuality quality) => quality switch
    {
        SolCloudQuality.Low => 0f,
        SolCloudQuality.High => highHistoryWeight,
        _ => mediumHistoryWeight,
    };

    public int ShadowResolution(SolCloudQuality quality) => quality switch
    {
        SolCloudQuality.Low => 256,
        SolCloudQuality.High => 1024,
        _ => 512,
    };

    public int ShadowSamples(SolCloudQuality quality) => quality switch
    {
        SolCloudQuality.Low => 4,
        SolCloudQuality.High => 8,
        _ => 6,
    };

    public float ShadowUpdatesPerSecond(SolCloudQuality quality) => quality switch
    {
        SolCloudQuality.Low => 2f,
        SolCloudQuality.High => 6f,
        _ => 4f,
    };

    public float ShadowFootprint(SolCloudQuality quality) => quality switch
    {
        SolCloudQuality.Low => lowShadowFootprintMetres,
        SolCloudQuality.High => highShadowFootprintMetres,
        _ => mediumShadowFootprintMetres,
    };

    void OnValidate()
    {
        cloudAdvectionMultiplier = Mathf.Clamp(cloudAdvectionMultiplier, 0f, 4f);
        temporalRejectStartMetres = Mathf.Max(1000f, temporalRejectStartMetres);
        temporalRejectEndMetres = Mathf.Max(temporalRejectStartMetres + 1000f,
            temporalRejectEndMetres);
        structureScaleMetres = Mathf.Max(100f, structureScaleMetres);
        microDetailFadeStartMetres = Mathf.Max(0f, microDetailFadeStartMetres);
        microDetailFadeEndMetres = Mathf.Max(microDetailFadeStartMetres + 1000f,
            microDetailFadeEndMetres);
        maximumLightMarchMetres = Mathf.Max(1000f, maximumLightMarchMetres);
        shadowFootprintMetres = Mathf.Max(1000f, shadowFootprintMetres);
        lowShadowFootprintMetres = Mathf.Max(1000f, lowShadowFootprintMetres);
        mediumShadowFootprintMetres = Mathf.Max(lowShadowFootprintMetres,
            mediumShadowFootprintMetres);
        highShadowFootprintMetres = Mathf.Max(mediumShadowFootprintMetres,
            highShadowFootprintMetres);
    }
}
