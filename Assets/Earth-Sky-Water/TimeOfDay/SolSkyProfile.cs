using UnityEngine;

namespace Sol.ToD
{
    /// <summary>
    /// Single visual-authoring asset for the Sol sky and the environment systems which
    /// must agree with it. Runtime simulation remains on TimeOfDay and WeatherManager;
    /// this asset contains authored appearance only.
    /// </summary>
    [CreateAssetMenu(menuName = "Sol/Environment/Sky Profile", fileName = "Sol Sky Profile")]
    public sealed class SolSkyProfile : ScriptableObject
    {
        [Header("Sky Anchors")]
        [ColorUsage(true, true)] public Color dayZenith = new(0.19f, 0.43f, 0.82f, 1f);
        [ColorUsage(true, true)] public Color dayHorizon = new(0.66f, 0.78f, 0.92f, 1f);
        [ColorUsage(true, true)] public Color dayNadir = new(0.13f, 0.20f, 0.27f, 1f);
        [ColorUsage(true, true)] public Color nightZenith = new(0.006f, 0.011f, 0.03f, 1f);
        [ColorUsage(true, true)] public Color nightHorizon = new(0.025f, 0.035f, 0.075f, 1f);
        [ColorUsage(true, true)] public Color nightNadir = new(0.004f, 0.006f, 0.012f, 1f);
        [ColorUsage(true, true)] public Color sunriseHorizon = new(1.25f, 0.35f, 0.10f, 1f);
        [ColorUsage(true, true)] public Color sunsetHorizon = new(1.35f, 0.24f, 0.075f, 1f);
        [ColorUsage(true, true)] public Color antiSolarHorizon = new(0.30f, 0.39f, 0.64f, 1f);
        [ColorUsage(true, true)] public Color eclipseZenith = new(0.025f, 0.018f, 0.055f, 1f);
        [ColorUsage(true, true)] public Color eclipseHorizon = new(0.11f, 0.065f, 0.12f, 1f);

        [Header("Sky Shape")]
        [Range(0.1f, 8f)] public float zenithBlend = 1.35f;
        [Range(0.1f, 8f)] public float nadirBlend = 1.2f;
        [Range(0.1f, 12f)] public float horizonPower = 2.2f;
        [Range(0.5f, 32f)] public float twilightAzimuthPower = 8f;
        [Range(0f, 3f)] public float twilightIntensity = 0.72f;
        [Range(0f, 3f)] public float solarAureoleIntensity = 0.45f;
        [Range(1f, 128f)] public float solarAureolePower = 18f;
        [Min(1f)] public float aerialViewHeight = 12000f;
        [Range(0f, 1f)] public float aerialZenithDarkening = 0.38f;
        [Range(0f, 1f)] public float aerialHorizonExtinctionReduction = 0.55f;

        [Header("Horizon and Refraction")]
        [Range(-0.25f, 0.25f)] public float horizonOcclusionLevel;
        [Range(0.0005f, 0.05f)] public float horizonOcclusionSoftness = 0.0035f;
        [Range(0f, 4f)] public float horizonRefraction = 1f;
        [Range(0f, 1f)] public float horizonFlatten = 0.45f;
        [Range(0f, 1f)] public float horizonGlowRetention = 0.2f;
        [Range(0f, 1f)] public float airMassExtinction = 0.35f;

        [Header("Weather Response")]
        [Range(0f, 1f)] public float weatherSkyDimming = 0.38f;
        [Range(0f, 1f)] public float weatherHorizonNeutrality = 0.65f;
        [Range(0f, 2f)] public float lightningSkyIntensity = 0.72f;
        [Range(0f, 2f)] public float lightningAmbientIntensity = 0.5f;

        [Header("Ambient and Fog")]
        [Range(0f, 3f)] public float ambientIntensity = 1.05f;
        [ColorUsage(true, true)] public Color fogDayColor = new(0.61f, 0.70f, 0.80f, 1f);
        [ColorUsage(true, true)] public Color fogNightColor = new(0.018f, 0.025f, 0.055f, 1f);
        [Min(0f)] public float fogDayDensity = 0.0012f;
        [Min(0f)] public float fogNightDensity = 0.00055f;
        public bool enableNightFog = true;

        [Header("Atmosphere Extinction")]
        [Min(0f)] public float atmosphereDensityMultiplier = 1f;
        [Min(0f)] public float atmosphereStartDistance = 5f;
        [Min(1f)] public float atmosphereMaxDistance = 500f;
        [Range(0f, 1f)] public float atmosphereMaxOpacity = 0.92f;

        [Header("Atmosphere Height")]
        public float atmosphereBaseHeight = 12f;
        [Min(0.0001f)] public float atmosphereHeightFalloff = 0.035f;
        public float atmosphereMistBaseHeight = 1.5f;
        [Min(0.0001f)] public float atmosphereMistHeightFalloff = 0.12f;

        [Header("Atmosphere Noise")]
        [Range(0f, 1f)] public float atmosphereNoiseIntensity = 0.1f;
        [Min(0.0001f)] public float atmosphereNoiseScale = 0.0035f;
        [Min(0f)] public float atmosphereNoiseSpeed = 0.08f;

        [Header("Atmosphere Aerial View")]
        [Range(0f, 2f)] public float skyFogStrength = 1f;
        [Range(0f, 2f)] public float zenithFogStrength = 0.12f;
        [Range(0f, 2f)] public float horizonFogStrength = 0.86f;
        [Range(-0.9f, 0.9f)] public float phaseAnisotropy = 0.55f;
        [Range(0f, 3f)] public float directionalScatteringIntensity = 0.65f;
        [Range(0f, 1f)] public float shadowedScatteringStrength = 0.85f;
        [Range(0f, 1f)] public float fogSaturation = 0.35f;
        [Min(0f)] public float ambientScatteringIntensity = 0.65f;
        [Min(0.01f)] public float maxScatteringLuminance = 1.5f;
        [Range(0f, 1f)] public float lightningScatteringIntensity = 0.08f;
        [ColorUsage(true, true)] public Color dayScatteringColor = new(1f, 0.75f, 0.48f, 1f);
        [ColorUsage(true, true)] public Color nightScatteringColor = new(0.22f, 0.34f, 0.62f, 1f);

        [Header("Atmosphere High Quality")]
        [Min(1f)] public float atmosphereRaymarchDistance = 500f;
        [Range(8, 32)] public int atmosphereRaymarchStepCount = 32;
        [Range(0f, 1f)] public float atmosphereRaymarchJitter = 0.15f;
        [Min(0.01f)] public float atmosphereBilateralDepthThreshold = 2f;
        [Range(0f, 1f)] public float atmosphereSpatialFilterStrength = 0.75f;
        public SolAtmosphereQuality atmosphereQuality = SolAtmosphereQuality.Medium;

        [Header("Sun and Eclipse")]
        [Tooltip("Artistic apparent diameter in degrees. Oversized for a readable fantasy sky.")]
        [Range(0.05f, 10f)] public float sunAngularDiameter = 3.5f;
        [ColorUsage(true, true)] public Color sunColor = new(1f, 0.97f, 0.87f, 1f);
        [Min(0f)] public float sunIntensity = 12f;
        [Range(0f, 1f)] public float sunLimbDarkening = 0.12f;
        [ColorUsage(true, true)] public Color coronaColor = new(0.8f, 0.65f, 0.42f, 1f);
        [Range(0f, 4f)] public float coronaIntensity = 1f;
        [Range(1f, 96f)] public float coronaPower = 24f;

        [Header("Moon")]
        [Tooltip("Artistic apparent diameter in degrees; independent of directional-light intensity.")]
        [Range(0.05f, 10f)] public float moonAngularDiameter = 4.5f;
        [ColorUsage(true, true)] public Color moonLitColor = new(0.92f, 0.95f, 1f, 1f);
        [ColorUsage(true, true)] public Color moonDarkColor = new(0.015f, 0.02f, 0.035f, 1f);
        [ColorUsage(true, true)] public Color lunarEclipseTint = new(0.6f, 0.15f, 0.1f, 1f);
        [Range(0.5f, 20f)] public float moonTerminatorSharpness = 4f;
        [Range(0f, 360f)] public float moonSurfaceRotation;
        public Texture2D lunarSurface;

        [Header("Stars and Milky Way")]
        public Cubemap stellarBackdrop;
        [Min(0f)] public float starIntensity = 2.4f;
        [Range(8f, 256f)] public float starPower = 72f;
        [Tooltip("Procedural fallback star-grid scale. Higher values produce more, smaller stars.")]
        [Min(1f)] public float starHeight = 100f;
        [Min(0f)] public float milkyWayIntensity = 0.35f;
        [ColorUsage(true, true)] public Color milkyWayColor = new(0.44f, 0.52f, 0.78f, 1f);
        [Range(0f, 1f)] public float twinkleAmount = 0.65f;
        [Tooltip("Approximate twinkle cycles per presentation second, independent of cloud speed and day length.")]
        [Range(0f, 5f)] public float twinkleSpeed = 1.25f;

        [Header("Aurora")]
        public bool enableAurora = true;
        [Range(0f, 1f)] public float auroraNightChance = 0.12f;
        [Min(0f)] public float auroraIntensity = 0.7f;
        [ColorUsage(true, true)] public Color auroraLowColor = new(0.03f, 0.75f, 0.34f, 1f);
        [ColorUsage(true, true)] public Color auroraHighColor = new(0.22f, 0.25f, 0.9f, 1f);
        [Range(-1f, 1f)] public float auroraMinElevation = 0.08f;
        [Range(-1f, 1f)] public float auroraMaxElevation = 0.62f;
        [Range(0.001f, 0.3f)] public float auroraEdgeSoftness = 0.08f;

        [Header("Authored Cloud Baseline")]
        public SolCloudFormation cloudFormation = SolCloudFormation.Cumulus;
        [Range(0f, 1f)] public float cloudCoverage = 0.35f;
        [Range(0f, 1f)] public float cloudErosion = 0.45f;
        [Min(0f)] public float cloudDensity = 0.72f;
        [Min(1f)] public float cloudBaseHeight = 1500f;
        [Min(10f)] public float cloudThickness = 3200f;
        [Range(0f, 1f)] public float cloudVerticalDevelopment = 0.45f;
        [Range(0f, 1f)] public float cloudAnvilAmount;
        [Range(0f, 1f)] public float cloudCirrusAmount = 0.18f;
        [Range(0f, 1f)] public float cloudEdgeSoftness = 0.5f;
        [Range(0f, 1f)] public float cloudBaseSoftness = 0.5f;
        [Range(0f, 1f)] public float cloudShadowStrength = 0.65f;

        public SolCloudState AuthoredCloudState => new(
            cloudFormation, cloudCoverage, cloudErosion, cloudDensity, cloudBaseHeight,
            cloudThickness, cloudVerticalDevelopment, cloudAnvilAmount, cloudCirrusAmount,
            cloudEdgeSoftness, cloudBaseSoftness, 0f, Vector2.zero, Vector2.zero,
            Vector2.zero, cloudShadowStrength);

        void OnValidate()
        {
            if (auroraMaxElevation < auroraMinElevation)
                auroraMaxElevation = auroraMinElevation;
        }
    }
}
