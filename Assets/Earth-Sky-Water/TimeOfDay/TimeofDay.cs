using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sol.ToD
{

public enum SolCloudQuality
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>
/// Scene-owned authority for Sol environment world time.
/// This component advances the gameplay clock, owns calendar integration, and drives sky/environment visuals.
/// </summary>
[ExecuteAlways]
[DefaultExecutionOrder(-1000)]
[RequireComponent(typeof(Calendar))]
public class TimeOfDay : MonoBehaviour
{
    /// <summary>The active scene time service. Future multiplayer should treat this as host/server authoritative.</summary>
    public static TimeOfDay Instance { get; private set; }

    // -- PREFABS --------------------------------------
    #region Inspector Settings
    [Header("-- Prefabs ------------------------")]
    [Tooltip("Sun prefab (must contain a Light and optionally a CelestialBody).")]
    [SerializeField] GameObject sunPrefab;

    [Tooltip("Moon prefab (must contain a Light and optionally a CelestialBody).")]
    [SerializeField] GameObject moonPrefab;

    [Header("-- Celestial Body Configs ---------")]
    [Tooltip("Optional SO config for the sun body visuals. Leave null to use inspector defaults on the prefab.")]
    [SerializeField] CelestialBodyConfig sunConfig;

    [Tooltip("Optional SO config for the moon body visuals.")]
    [SerializeField] CelestialBodyConfig moonConfig;

    // -- TERTIARY PLANETS -----------------------------
    [Header("-- Tertiary Planets ---------------")]
    [Tooltip("Additional planets visible in the sky, each with its own orbital config.")]
    [SerializeField] TertiaryPlanetEntry[] tertiaryPlanets;

    // -- Runtime references (auto-resolved from prefab instances) --
    Light sunLight;
    Light moonLight;
    Light dominantAtmosphereLight;
    Light editPreviewSunLight;

    // -- CURRENT TIME OF DAY --------------------------
    [Header("-- Current Time of Day ------------")]
    [Tooltip("Current civil-day progress. 0 = 00:00, 1 = 24:00.")]
    [Range(0f, 1f)]
    [SerializeField] float timeOfDay = 0.25f;

    // -- SUN ------------------------------------------
    [Header("-- Sun ----------------------------")]
    [Tooltip("Sun color near the horizon (sunrise).")]
    [SerializeField] Color sunriseColor = new Color(1f, 0.55f, 0.2f);

    [Tooltip("Sun color at zenith.")]
    [SerializeField] Color noonColor = new Color(1f, 0.95f, 0.8f);

    [Tooltip("Sun color near the horizon (sunset).")]
    [SerializeField] Color sunsetColor = new Color(1f, 0.4f, 0.1f);

    [Tooltip("Sun intensity when directly overhead.")]
    [Min(0f)]
    [SerializeField] float maxIntensity = 1.5f;

    [Tooltip("Sun intensity at the horizon or below.")]
    [Min(0f)]
    [SerializeField] float minIntensity = 0.05f;

    // -- MOON -----------------------------------------
    [Header("-- Moon ---------------------------")]
    [Tooltip("Moon light color at night.")]
    [SerializeField] Color moonColorNight = new Color(0.6f, 0.7f, 0.9f);

    [Tooltip("Moon intensity at full night (full moon, directly overhead).")]
    [Min(0f)]
    [SerializeField] float moonMaxIntensity = 0.3f;

    // -- Runtime celestial body references --
    CelestialBody sunBody;
    CelestialBody moonBody;
    GameObject sunInstance;
    GameObject moonInstance;

    struct TertiaryRuntime
    {
        public GameObject instance;
        public CelestialBody body;
        public CelestialBodyConfig config;
    }
    List<TertiaryRuntime> tertiaryInstances;

    // -- LUNAR ORBIT / ECLIPSES -----------------------
    [Header("-- Lunar Orbit / Eclipses ---------")]

    [Tooltip("Orbital tilt of the moon in degrees. Higher values make eclipses rarer.")]
    [Range(0f, 15f)]
    [SerializeField] float lunarTiltDegrees = 5f;

    [Tooltip("Initial lunar phase at the start of the simulation. 0 = new moon (near sun), 0.25 = first quarter, 0.5 = full moon (opposite sun), 0.75 = last quarter.")]
    [Range(0f, 1f)]
    [SerializeField] float initialLunarPhase = 0.25f;

    [Tooltip("Initial nodal precession phase. Controls the starting tilt offset so eclipses don't happen immediately.")]
    [Range(0f, 1f)]
    [SerializeField] float initialNodalPhase = 0.3f;

    [Tooltip("Days for the lunar nodes to complete one full precession (controls eclipse season frequency).")]
    [Min(1f)]
    [SerializeField] float nodalPrecessionDays = 168f;

    [Tooltip("Angular threshold in degrees within which an eclipse occurs.")]
    [Range(0.5f, 10f)]
    [SerializeField] float eclipseThresholdDegrees = 3f;

    [Tooltip("Color the moon turns during a lunar eclipse.")]
    [SerializeField] Color lunarEclipseTint = new Color(0.6f, 0.15f, 0.1f);

    [Tooltip("Lunar phase must be within this fraction of new (0) or full (0.5) for an eclipse to be possible.")]
    [Range(0.01f, 0.15f)]
    [SerializeField] float eclipsePhaseWindow = 0.05f;

    [Tooltip("How strongly eclipses are biased toward the apex of the orbit. Higher = eclipses only near zenith.")]
    [Range(0f, 5f)]
    [SerializeField] float eclipseApexBias = 2f;

    [Tooltip("Sun light color at total solar eclipse (corona glow).")]
    [SerializeField] Color solarEclipseLightColor = new Color(0.4f, 0.2f, 0.15f);

    [Tooltip("Ambient color during a solar eclipse (eerie twilight).")]
    [SerializeField] Color eclipseAmbientColor = new Color(0.08f, 0.06f, 0.12f);

    [Tooltip("Fog color during a solar eclipse.")]
    [SerializeField] Color eclipseFogColor = new Color(0.12f, 0.1f, 0.15f);

    [Header("-- Eclipse / Debug -----------------")]
    [SerializeField] float debugLunarPhase;
    [SerializeField] float debugMoonIllumination;
    [SerializeField] float debugEclipseStrength;
    [SerializeField] bool debugIsSolarEclipse;
    [SerializeField] bool debugIsLunarEclipse;

    // -- AMBIENT --------------------------------------
    [Header("-- Ambient ------------------------")]
    [Tooltip("Should this script control ambient lighting?")]
    [SerializeField] bool controlAmbient = true;

    [Tooltip("Derive Trilight ambient from the computed sky colors (zenith / horizon / nadir) " +
             "so scene lighting always matches the sky - eclipses, storms, and lightning " +
             "included. Disable to use the flat day/night colors below.")]
    [SerializeField] bool ambientFromSky = true;

    [Tooltip("Intensity multiplier for sky-derived ambient.")]
    [Range(0f, 3f)]
    [SerializeField] float ambientSkyIntensity = 1.2f;

    [Tooltip("Ambient color during the day (flat mode only).")]
    [SerializeField] Color ambientDayColor = new Color(0.5f, 0.55f, 0.6f);

    [Tooltip("Ambient color at night (flat mode only).")]
    [SerializeField] Color ambientNightColor = new Color(0.04f, 0.04f, 0.08f);

    // -- FOG ------------------------------------------
    [Header("-- Fog ----------------------------")]
    [Tooltip("Should this script control fog?")]
    [SerializeField] bool controlFog = true;

    [Tooltip("Derive fog color from the computed sky horizon color so distant " +
             "geometry fades exactly into the sky (eclipse, storm, and lightning " +
             "tints included). Disable to use the flat colors below.")]
    [SerializeField] bool fogColorFromSky = true;

    [Tooltip("Keep fog active during the night?")]
    [SerializeField] bool enableNightFog = true;

    [Tooltip("Fog color during the day.")]
    [SerializeField] Color fogDayColor = new Color(0.7f, 0.75f, 0.8f);

    [Tooltip("Fog color at night.")]
    [SerializeField] Color fogNightColor = new Color(0.05f, 0.05f, 0.1f);

    [Tooltip("Fog density during the day.")]
    [Range(0f, 0.05f)]
    [SerializeField] float fogDayDensity = 0.0012f;

    [Tooltip("Fog density at night.")]
    [Range(0f, 0.05f)]
    [SerializeField] float fogNightDensity = 0.0024f;

    // -- SKYBOX ---------------------------------------
    [Header("-- Skybox -------------------------")]
    [Tooltip("Should this script control the skybox material colors?")]
    [SerializeField] bool controlSkybox = true;

    [Header("    Day")]
    [Tooltip("Zenith (top of sky) color during the day.")]
    [SerializeField] Color skyZenithDay = new Color(0.4f, 0.6f, 0.9f);

    [Tooltip("Horizon color during the day.")]
    [SerializeField] Color skyHorizonDay = new Color(0.7f, 0.8f, 0.95f);

    [Tooltip("Horizon color at sunrise.")]
    [SerializeField] Color skyHorizonSunrise = new Color(1f, 0.55f, 0.2f);

    [Tooltip("Horizon color at sunset.")]
    [SerializeField] Color skyHorizonSunset = new Color(1f, 0.4f, 0.1f);

    [Tooltip("Nadir (bottom of sky) color during the day.")]
    [SerializeField] Color skyNadirDay = new Color(0.5f, 0.45f, 0.4f);

    [Tooltip("Zenith blend sharpness during the day.")]
    [Range(0f, 10f)]
    [SerializeField] float skyZenithBlendDay = 0.2f;

    [Tooltip("Horizon blend power during the day.")]
    [Range(0f, 10f)]
    [SerializeField] float skyHorizonBlendDay = 1f;

    [Tooltip("Nadir blend sharpness during the day.")]
    [Range(0f, 10f)]
    [SerializeField] float skyNadirBlendDay = 0.1f;

    [Header("    Night")]
    [Tooltip("Zenith color at night.")]
    [SerializeField] Color skyZenithNight = new Color(0.02f, 0.02f, 0.06f);

    [Tooltip("Horizon color at night.")]
    [SerializeField] Color skyHorizonNight = new Color(0.03f, 0.03f, 0.06f);

    [Tooltip("Nadir color at night.")]
    [SerializeField] Color skyNadirNight = new Color(0.02f, 0.01f, 0.04f);

    [Tooltip("Zenith blend sharpness at night.")]
    [Range(0f, 10f)]
    [SerializeField] float skyZenithBlendNight = 0.4f;

    [Tooltip("Horizon blend power at night.")]
    [Range(0f, 10f)]
    [SerializeField] float skyHorizonBlendNight = 0.5f;

    [Tooltip("Nadir blend sharpness at night.")]
    [Range(0f, 10f)]
    [SerializeField] float skyNadirBlendNight = 0.2f;

    [Header("    Eclipse")]
    [Tooltip("Zenith color during a solar eclipse.")]
    [SerializeField] Color skyZenithEclipse = new Color(0.04f, 0.03f, 0.06f);

    [Tooltip("Horizon color during a solar eclipse.")]
    [SerializeField] Color skyHorizonEclipse = new Color(0.12f, 0.06f, 0.08f);

    [Header("    Stars")]
    [Tooltip("Star intensity at full night.")]
    [Min(0f)]
    [SerializeField] float starIntensityNight = 50f;

    [Tooltip("Star power (controls star sharpness / contrast).")]
    [Min(0f)]
    [SerializeField] float starPower = 30f;

    [Tooltip("Star height (controls noise scale / star density).")]
    [Min(0f)]
    [SerializeField] float starHeight = 100f;

    [Header("    Aurora")]
    [Tooltip("Enable aurora displays on some nights (deterministic per calendar day, " +
             "suppressed by storm cover).")]
    [SerializeField] bool enableAurora = true;

    [Tooltip("Fraction of nights that get an aurora display.")]
    [Range(0f, 1f)]
    [SerializeField] float auroraNightChance = 0.3f;

    [Tooltip("Peak aurora brightness on aurora nights.")]
    [Range(0f, 3f)]
    [SerializeField] float auroraIntensity = 1.2f;

    [Header("    Clouds")]
 [Tooltip("Cloud noise frequency - lower values = larger clouds.")]
    [Min(0.1f)]
    [SerializeField] float cloudScale = 5f;

    [Tooltip("Cloud wind speed at a 10-minute cycle. Actual speed scales with cycle duration and timeScale.")]
    [Min(0f)]
    [SerializeField] float cloudBaseSpeed = 0.05f;

    [Tooltip("Pseudo-volume cloud shell and lighting quality.")]
    [SerializeField] SolCloudQuality cloudQuality = SolCloudQuality.Medium;

    [Tooltip("Optional packed periodic cloud noise override. R = shape, G = erosion, "
        + "B/A = warp. The shipped Resources texture is used when this is null.")]
    [SerializeField] Texture2D cloudNoiseTexture;

    [Tooltip("Optional regional weather-map override. R biases coverage and G biases "
        + "cloud type. The shipped Resources texture is used when this is null.")]
    [SerializeField] Texture2D cloudWeatherMap;

    [Tooltip("Temporary art-review escape hatch. Uses the original hash-heavy procedural "
        + "cloud path instead of the baked packed texture.")]
    [SerializeField] bool useProceduralCloudNoise;

    [Tooltip("Regional coverage variation contributed by the weather map.")]
    [Range(0f, 0.5f)]
    [SerializeField] float cloudWeatherInfluence = 0.22f;

    [Tooltip("Regional erosion/cloud-type variation contributed by the weather map.")]
    [Range(0f, 0.5f)]
    [SerializeField] float cloudTypeInfluence = 0.18f;

 [Tooltip("Virtual cloud plane height - affects horizon stretching.")]
    [Range(0.01f, 1f)]
    [SerializeField] float cloudHeight = 0.15f;

    [Tooltip("Cloud coverage during the day (0 = overcast, 1 = clear).")]
    [Range(0f, 1f)]
    [SerializeField] float cloudCoverageDay = 0.5f;

    [Tooltip("Cloud coverage at night (0 = overcast, 1 = clear).")]
    [Range(0f, 1f)]
    [SerializeField] float cloudCoverageNight = 0.6f;

    [Tooltip("Cloud opacity during the day.")]
    [Range(0f, 1f)]
    [SerializeField] float cloudDensityDay = 0.8f;

    [Tooltip("Cloud opacity at night.")]
    [Range(0f, 1f)]
    [SerializeField] float cloudDensityNight = 0.3f;

    [Tooltip("Lit cloud color during the day.")]
    [SerializeField] Color cloudColorDay = Color.white;

    [Tooltip("Lit cloud color near sunrise/sunset.")]
    [SerializeField] Color cloudColorSunset = new Color(1f, 0.6f, 0.3f);

    [Tooltip("Lit cloud color at night.")]
    [SerializeField] Color cloudColorNight = new Color(0.06f, 0.06f, 0.1f);

    [Tooltip("Cloud shadow / unlit color during the day.")]
    [SerializeField] Color cloudShadowDay = new Color(0.4f, 0.45f, 0.55f);

    [Tooltip("Cloud shadow color at night.")]
    [SerializeField] Color cloudShadowNight = new Color(0.02f, 0.02f, 0.04f);

    [Header("    Sun Disc")]
    [Tooltip("Apparent disc size (closer to 1 = smaller disc).")]
    [Range(0.990f, 0.9999f)]
    [SerializeField] float sunDiscSize = 0.9995f;

    [Tooltip("HDR color multiplier for the sun disc. Driven automatically from sun light color.")]
    [SerializeField] float sunDiscIntensity = 10f;

 [Tooltip("Atmospheric glow power falloff - higher = tighter halo around the sun.")]
    [Min(1f)]
    [SerializeField] float sunGlowFalloff = 8f;

    [Tooltip("Atmospheric glow brightness during the day.")]
    [Min(0f)]
    [SerializeField] float sunGlowIntensityDay = 1.5f;

    [Tooltip("Atmospheric glow brightness at night.")]
    [Min(0f)]
    [SerializeField] float sunGlowIntensityNight = 0.1f;

    [Tooltip("Eclipse corona color (HDR).")]
    [SerializeField] Color coronaColor = new Color(1.5f, 0.4f, 0.15f, 1f);

    [Header("    Moon Disc")]
    [Tooltip("Apparent disc size (closer to 1 = smaller disc).")]
    [Range(0.990f, 0.9999f)]
    [SerializeField] float moonDiscSize = 0.9993f;

    [Tooltip("Lit face color of the moon.")]
    [SerializeField] Color moonLitColor = new Color(0.85f, 0.9f, 1f);

    [Tooltip("Dark face color of the moon.")]
    [SerializeField] Color moonDarkColor = new Color(0.01f, 0.01f, 0.02f);

    [Tooltip("Sharpness of the day/night terminator on the moon.")]
    [Range(1f, 10f)]
    [SerializeField] float moonTerminatorSharpness = 3f;

    [Header("    Atmosphere")]
    [Tooltip("Horizon haze intensity during the day.")]
    [Range(0f, 1f)]
    [SerializeField] float hazeIntensityDay = 0.15f;

    [Tooltip("Horizon haze intensity at night.")]
    [Range(0f, 1f)]
    [SerializeField] float hazeIntensityNight = 0.03f;

    // Cached property IDs (allocated once)
    static readonly int _ZenithColorID          = Shader.PropertyToID("_ZenithColor");
    static readonly int _HorizonColorID         = Shader.PropertyToID("_HorizonColor");
    static readonly int _HorizonWarmColorID     = Shader.PropertyToID("_HorizonWarmColor");
    static readonly int _NadirColorID           = Shader.PropertyToID("_NadirColor");
    static readonly int _ZenithBlendID          = Shader.PropertyToID("_ZenithBlend");
    static readonly int _HorizonBlendID         = Shader.PropertyToID("_HorizonBlend");
    static readonly int _NadirBlendID           = Shader.PropertyToID("_NadirBlend");
    static readonly int _StarIntensityID        = Shader.PropertyToID("_StarIntensity");
    static readonly int _StarPowerID            = Shader.PropertyToID("_StarPower");
    static readonly int _StarHeightID           = Shader.PropertyToID("_StarHeight");
    static readonly int _StarRotationID         = Shader.PropertyToID("_StarRotation");
    static readonly int _NightFactorID          = Shader.PropertyToID("_NightFactor");
    static readonly int _AuroraIntensityID      = Shader.PropertyToID("_AuroraIntensity");
    static readonly int _SunDirectionID         = Shader.PropertyToID("_SunDirection");
    static readonly int _SunDiscColorID         = Shader.PropertyToID("_SunDiscColor");
    static readonly int _SunDiscSizeID          = Shader.PropertyToID("_SunDiscSize");
    static readonly int _SunGlowFalloffID       = Shader.PropertyToID("_SunGlowFalloff");
    static readonly int _SunGlowIntensityID     = Shader.PropertyToID("_SunGlowIntensity");
    static readonly int _CoronaColorID          = Shader.PropertyToID("_CoronaColor");
    static readonly int _MoonDirectionID        = Shader.PropertyToID("_MoonDirection");
    static readonly int _MoonColorID            = Shader.PropertyToID("_MoonColor");
    static readonly int _MoonDarkColorID        = Shader.PropertyToID("_MoonDarkColor");
    static readonly int _MoonDiscSizeID         = Shader.PropertyToID("_MoonDiscSize");
    static readonly int _MoonSharpnessID        = Shader.PropertyToID("_MoonSharpness");
    static readonly int _SolarEclipseFactorID   = Shader.PropertyToID("_SolarEclipseFactor");
    static readonly int _LunarEclipseFactorID   = Shader.PropertyToID("_LunarEclipseFactor");
    static readonly int _EclipseTintID          = Shader.PropertyToID("_EclipseTint");
    static readonly int _CloudScaleID           = Shader.PropertyToID("_CloudScale");
    static readonly int _CloudSpeedID           = Shader.PropertyToID("_CloudSpeed");
    static readonly int _CloudTimeID            = Shader.PropertyToID("_CloudTime");
    static readonly int _CloudWindDirectionID   = Shader.PropertyToID("_CloudWindDirection");
    static readonly int _CloudErosionID         = Shader.PropertyToID("_CloudErosion");
    static readonly int _CloudNoiseTexID        = Shader.PropertyToID("_CloudNoiseTex");
    static readonly int _CloudWeatherMapID      = Shader.PropertyToID("_CloudWeatherMap");
    static readonly int _CloudNoiseTilingID     = Shader.PropertyToID("_CloudNoiseTiling");
    static readonly int _CloudWeatherScaleID    = Shader.PropertyToID("_CloudWeatherScale");
    static readonly int _CloudWeatherInfluenceID = Shader.PropertyToID("_CloudWeatherInfluence");
    static readonly int _CloudTypeInfluenceID   = Shader.PropertyToID("_CloudTypeInfluence");
    static readonly int _CloudCoverageID        = Shader.PropertyToID("_CloudCoverage");
    static readonly int _CloudDensityID         = Shader.PropertyToID("_CloudDensity");
    static readonly int _CloudHeightID          = Shader.PropertyToID("_CloudHeight");
    static readonly int _CloudColorID           = Shader.PropertyToID("_CloudColor");
    static readonly int _CloudShadowColorID     = Shader.PropertyToID("_CloudShadowColor");
    static readonly int _HazeIntensityID        = Shader.PropertyToID("_HazeIntensity");

    // -- CYCLE TIMING ---------------------------------
    [Header("-- Cycle Timing -------------------")]
    [Tooltip("Fraction of the civil day that is visually daytime, centered around noon. Modified by seasons.")]
    [Range(0.05f, 0.95f)]
    [SerializeField] float dayRatio = 0.6f;

    [Tooltip("How many real-time minutes one full day/night cycle takes.")]
    [Min(0.1f)]
    [SerializeField] float cycleDurationMinutes = 10f;

    [Tooltip("Multiplier to speed up or slow down time (1 = normal).")]
    [Min(0f)]
    [SerializeField] float timeScale = 1f;

    [Tooltip("Initialize a fresh scene instance at the configured calendar start date. Disable when an external bootstrap restores time before Start.")]
    [SerializeField] bool initializeNewGameOnStart = true;

    [Tooltip("Advance the clock in edit mode so the sky, weather and water animate in the "
        + "Scene View without entering play mode. Everything downstream already shares one "
        + "clock, so this drives the whole environment. Off by default because animating "
        + "moves the sun and moon transforms every frame, which keeps the scene marked "
        + "dirty; turn it on while dialling a look in, off for ordinary editing.")]
    [SerializeField] bool animateInEditMode;

    // -- SEASONAL VARIATION ---------------------------
    [Header("-- Seasonal Variation -------------")]
    [Tooltip("Enable seasonal variation of day length based on calendar position within the year.")]
    [SerializeField] bool enableSeasons = true;

    [Tooltip("How much the day ratio varies across seasons (0 = none, 0.2 = moderate, 0.4 = extreme).")]
    [Range(0f, 0.4f)]
    [SerializeField] float seasonalDayVariation = 0.1f;
    #endregion

    #region Runtime State
    // -- Cached per-frame values --
    float currentSunAngle;
    float currentLunarPhase;
    float currentMoonIllumination;
    float solarEclipseFactor;
    float lunarEclipseFactor;
    float cachedDayFactor;
    Vector3 cachedSunDirection;
    Vector3 cachedMoonDirection;
    float lunarPeriodDays;
    bool paused;
    bool calendarInitialized;
    float worldDeltaSeconds;
    double worldDeltaHours;
    double _editorClockStamp;
    float presentationDeltaSeconds;
    float cloudTime;
    SolEnvironmentCoordinator environmentCoordinator;
    Material controlledSkyboxMaterial;
    Material _cloudKeywordMaterial;
    SolCloudQuality _appliedCloudQuality;
    bool _appliedProceduralCloudNoise;
    bool _cloudKeywordsApplied;
    Texture2D _resolvedCloudNoiseTexture;
    Texture2D _resolvedCloudWeatherMap;
    Sol.Environment.SolWorldFrameGate environmentUpdateGate;

    /// <summary>World-space direction toward the sun (unit vector).</summary>
    public Vector3 SunDirection => cachedSunDirection;

    /// <summary>World-space direction toward the moon (unit vector).</summary>
    public Vector3 MoonDirection => cachedMoonDirection;

    /// <summary>0 = night, 1 = zenith. Continuous day/night blend factor.</summary>
    public float DayFactor => cachedDayFactor;

    Calendar calendar;
    #endregion

    #region Weather Modulation
    // Set per-frame by an external weather system (e.g. SolWeatherManager).
    // Non-serialized on purpose: they reset to neutral when nothing drives them.

    /// <summary>0 = clear, 1 = fully overcast. Pushes cloud coverage and density toward storm cover.</summary>
    public float WeatherCloudiness { get; set; }

    /// <summary>Additional fog density multiplier from weather (0 = none, 1 = double density).</summary>
    public float WeatherFogBoost { get; set; }

    /// <summary>Storm darkening 0-1. Dims sun/moon light, ambient, sky, and clouds.</summary>
    public float WeatherDim { get; set; }

    /// <summary>Momentary lightning flash 0-1. Brightens ambient, sky, and cloud bases.</summary>
    public float WeatherLightningFlash { get; set; }

    /// <summary>Cloud scroll speed multiplier from weather wind (1 = calm baseline).</summary>
    public float WeatherCloudSpeedMul { get; set; } = 1f;

    /// <summary>Weather-driven breakup of cloud edges.</summary>
    public float WeatherCloudErosion { get; set; }

    /// <summary>Shared horizontal weather-wind direction.</summary>
    public Vector3 WeatherWindDirection { get; set; } = Vector3.right;
    #endregion

    #region Service Lifecycle
    /// <summary>Fired whenever normalized gameplay time or the calendar day changes.</summary>
    public event Action<TimeChangeResult> TimeChanged;

    /// <summary>Fired when the visible civil-clock hour bucket changes.</summary>
    public event Action<int, int> HourChanged;

    /// <summary>Compatibility event for completed player-time day buckets.</summary>
    [Obsolete("Use PlayerTimeChanged for player time or Calendar.OnNewDay for world-date changes.")]
    public event Action<int, int> DayChanged;

    /// <summary>Fired whenever forward-only player-experienced time changes.</summary>
    public event Action<double, double> PlayerTimeChanged;

    /// <summary>Fired for explicit skips/sets/rewinds, not ordinary per-frame ticking.</summary>
    public event Action<TimeChangeResult> TimeSkipped;

    /// <summary>Fired when the game-world time multiplier changes.</summary>
    public event Action<float, float> TimeScaleChanged;

    /// <summary>Resolve the active time service without requiring callers to know scene wiring.</summary>
    /// <summary>
    /// True when this authority is advancing its clock outside play mode. The editor
    /// driver reads this to decide whether to keep the Scene View repainting.
    /// </summary>
    public bool AnimatesInEditMode => animateInEditMode;

    public static TimeOfDay ResolveInstance()
    {
        if (Instance != null)
            return Instance;

        Instance = FindFirstObjectByType<TimeOfDay>();
        if (Instance != null)
            Instance.ResolveCalendar();

        return Instance;
    }

    void OnEnable()
    {
        environmentUpdateGate.Invalidate();
        ResolveCalendar();

        if (!Application.isPlaying)
        {
            // Edit-mode preview must never replace the scene's serialized
            // skybox with a HideAndDontSave runtime clone. Saving a scene while
            // such a clone is assigned serializes the skybox reference as null.
            environmentCoordinator = null;
            controlledSkyboxMaterial = null;
            EnsureEditPreviewSun();
            return;
        }

        // Entering Play must never leave the edit-only light alongside the
        // real Sun spawned by Start.
        DestroyEditPreviewSun();

        if (Instance != null && Instance != this)
        {
            Debug.LogWarning($"[{nameof(TimeOfDay)}] Duplicate instance disabled. World time must have one active authority.", this);
            enabled = false;
            return;
        }

        // Awake is not called again when a scene authority is re-enabled.
        // Reclaim the service slot here so late-resolving consumers do not
        // retain a disabled instance.
        Instance = this;

        environmentCoordinator = SolEnvironmentCoordinator.Resolve(this, createIfMissing: true);
        environmentCoordinator?.Register(this);
        controlledSkyboxMaterial = environmentCoordinator != null
            ? environmentCoordinator.AcquireSkyboxMaterial(this)
            : RenderSettings.skybox;
    }

    void OnDisable()
    {
        environmentUpdateGate.Invalidate();
        DestroyEditPreviewSun();

        worldDeltaSeconds = 0f;
        worldDeltaHours = 0d;
        presentationDeltaSeconds = 0f;

        if (Instance == this)
            Instance = null;

        if (environmentCoordinator != null)
        {
            environmentCoordinator.ReleaseSkybox(this);
            environmentCoordinator.Unregister(this);
        }

        controlledSkyboxMaterial = null;
        _cloudKeywordMaterial = null;
        _cloudKeywordsApplied = false;
    }

    void Awake()
    {
        ResolveCalendar();

        if (!Application.isPlaying)
            return;

        if (Instance != null && Instance != this)
        {
            Debug.LogWarning($"[{nameof(TimeOfDay)}] Duplicate instance disabled. World time must have one active authority.", this);
            enabled = false;
            return;
        }

        Instance = this;
    }

    void Start()
    {
        if (!Application.isPlaying) return;

        ResolveCalendar();
        if (!calendarInitialized && calendar != null && initializeNewGameOnStart)
        {
            calendar.ResetToStart();
            calendarInitialized = true;
        }

        // Lunar synodic period = average calendar month length
        lunarPeriodDays = Calendar != null ? Calendar.AverageMonthLength : 28f;

        SpawnCelestialBodies();

        // Set render-settings modes once (they never change at runtime)
        // Ambient mode is managed per-frame by UpdateEnvironment
        // (Trilight when ambientFromSky, Flat otherwise).
        if (controlFog) RenderSettings.fogMode = FogMode.ExponentialSquared;
    }

    void OnValidate()
    {
        timeOfDay = Mathf.Repeat(timeOfDay, 1f);
        cloudWeatherInfluence = Mathf.Clamp(cloudWeatherInfluence, 0f, 0.5f);
        cloudTypeInfluence = Mathf.Clamp(cloudTypeInfluence, 0f, 0.5f);
        _cloudKeywordsApplied = false;
        _resolvedCloudNoiseTexture = null;
        _resolvedCloudWeatherMap = null;

        // Serialized inspector edits do not go through CurrentTime's setter.
        // Refresh the edit-mode preview immediately so the inspector slider
        // remains a functional time-of-day scrubber.
        if (!Application.isPlaying && isActiveAndEnabled)
            UpdateEditModePreview();
    }

    void OnDestroy()
    {
        DestroyEditPreviewSun();

        if (Instance == this)
            Instance = null;

        if (!Application.isPlaying) return;

        if (sunInstance != null) Destroy(sunInstance);
        if (moonInstance != null) Destroy(moonInstance);

        if (tertiaryInstances != null)
            for (int i = 0; i < tertiaryInstances.Count; i++)
                if (tertiaryInstances[i].instance != null)
                    Destroy(tertiaryInstances[i].instance);
    }
    #endregion

    #region Celestial Body Setup
    void EnsureEditPreviewSun()
    {
        if (Application.isPlaying || sunLight != null)
            return;

        var previewObject = new GameObject("Sol Edit Preview Sun")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        editPreviewSunLight = previewObject.AddComponent<Light>();
        editPreviewSunLight.hideFlags = HideFlags.HideAndDontSave;
        editPreviewSunLight.type = LightType.Directional;
        editPreviewSunLight.shadows = LightShadows.Soft;
        sunLight = editPreviewSunLight;
    }

    void DestroyEditPreviewSun()
    {
        if (editPreviewSunLight == null)
            return;

        Light preview = editPreviewSunLight;
        editPreviewSunLight = null;
        if (sunLight == preview)
            sunLight = null;
        DestroyImmediate(preview.gameObject);
    }

    void SpawnCelestialBodies()
    {
        if (sunPrefab != null)
        {
            sunInstance = Instantiate(sunPrefab, transform);
            sunInstance.name = "Sun";
            sunLight = sunInstance.GetComponentInChildren<Light>();
            if (sunLight != null) sunLight.shadows = LightShadows.Soft;
            sunBody  = sunInstance.GetComponentInChildren<CelestialBody>();
            if (sunBody != null && sunConfig != null) sunBody.Initialize(sunConfig);
        }

        if (moonPrefab != null)
        {
            moonInstance = Instantiate(moonPrefab, transform);
            moonInstance.name = "Moon";
            moonLight = moonInstance.GetComponentInChildren<Light>();
            moonBody  = moonInstance.GetComponentInChildren<CelestialBody>();
            if (moonBody != null && moonConfig != null) moonBody.Initialize(moonConfig);
        }

        // Tertiary planets
        tertiaryInstances = new List<TertiaryRuntime>();
        if (tertiaryPlanets != null)
        {
            for (int i = 0; i < tertiaryPlanets.Length; i++)
            {
                var entry = tertiaryPlanets[i];
                if (entry.prefab == null) continue;

                var inst = Instantiate(entry.prefab, transform);
                inst.name = entry.config != null ? entry.config.bodyName : $"Planet_{i}";

                var body = inst.GetComponentInChildren<CelestialBody>();
                if (body != null && entry.config != null) body.Initialize(entry.config);

                tertiaryInstances.Add(new TertiaryRuntime
                {
                    instance = inst,
                    body = body,
                    config = entry.config
                });
            }
        }
    }
    #endregion

    #region Time Advancement
    // ------------------------------------------------
    // Main loop
    // ------------------------------------------------

    void Update()
    {
        // Edit mode skips time advancement and spawned celestial instances,
        // but still refreshes the preview environment for inspector scrubbing.
        if (!Application.isPlaying)
        {
            UpdateEditModePreview();
            return;
        }

        presentationDeltaSeconds = GetPresentationDeltaSeconds(Time.deltaTime);
        worldDeltaSeconds = GetWorldDeltaSeconds(Time.deltaTime);
        worldDeltaHours = GetWorldDeltaHours(worldDeltaSeconds);
        cloudTime += worldDeltaSeconds
                   * cloudBaseSpeed
                   * (10f / Mathf.Max(cycleDurationMinutes, 0.1f))
                   * Mathf.Max(WeatherCloudSpeedMul, 0f);

        if (worldDeltaHours > 0d)
        {
            float dayProgress = (float)(worldDeltaHours / 24d);
            ApplyNormalizedDelta(
                dayProgress,
                TimeChangeRequest.AdvanceHours(dayProgress * 24f, this, "Tick"),
                fireSkipped: false,
                countPlayerTime: true);
        }

        float effectiveDayRatio = GetEffectiveDayRatio();
        double worldDay = Calendar != null ? Calendar.WorldDayIndex : 0L;
        float daysFraction = (float)(worldDay + timeOfDay);

        UpdateSun(effectiveDayRatio);
        UpdateMoon(daysFraction);
        UpdateEclipses();
        UpdateDominantAtmosphereLight();
        UpdateCelestialBodies();
        UpdateTertiaryPlanets(daysFraction);
    }

    /// <summary>
    /// Final environment fallback for scenes without an active weather director. When
    /// weather is present it refreshes after publishing its blended state in Update, so
    /// the frame gate turns this into a no-op. Unity runs every Update before any
    /// LateUpdate, making this independent of script execution order within LateUpdate.
    /// </summary>
    void LateUpdate() => UpdateEnvironment();

    /// <summary>
    /// Edit-mode preview. Runs the same solvers as the play path so what the Scene View
    /// shows is what will actually ship: the moon used to be approximated here as
    /// sunAngle + initialLunarPhase * 360, which put it in the wrong place in every
    /// editor screenshot, and eclipses were zeroed outright. Only the two steps that
    /// spawn prefab instances are skipped.
    ///
    /// With animateInEditMode set, the clock advances too, so the sky, the weather and
    /// the water all move together off one clock exactly as they do in play mode.
    /// </summary>
    void UpdateEditModePreview()
    {
        AdvanceEditModeClock();

        float effectiveDayRatio = GetEffectiveDayRatio();
        double worldDay = Calendar != null ? Calendar.WorldDayIndex : 0L;
        float daysFraction = (float)(worldDay + timeOfDay);

        UpdateSun(effectiveDayRatio);
        UpdateMoon(daysFraction);
        UpdateEclipses();
        UpdateDominantAtmosphereLight();
    }

    /// <summary>
    /// Advances the clock while the editor is not playing. Uses the editor's own wall
    /// clock rather than Time.deltaTime, which is not meaningful outside play mode.
    /// Leaves every delta at zero when previewing statically, so downstream simulations
    /// hold still rather than stepping on whatever value was last left in them.
    /// </summary>
    void AdvanceEditModeClock()
    {
        presentationDeltaSeconds = 0f;
        worldDeltaSeconds = 0f;
        worldDeltaHours = 0d;

#if UNITY_EDITOR
        double now = UnityEditor.EditorApplication.timeSinceStartup;
        double elapsed = now - _editorClockStamp;
        _editorClockStamp = now;
        if (!animateInEditMode)
            return;
        // A domain reload, a long inspector drag or a paused editor can leave an
        // arbitrarily large gap. Bound it so the preview eases forward instead of
        // teleporting the sun across the sky.
        float editorDelta = Mathf.Clamp((float)elapsed, 0f, 0.1f);

        presentationDeltaSeconds = GetPresentationDeltaSeconds(editorDelta);
        worldDeltaSeconds = GetWorldDeltaSeconds(editorDelta);
        worldDeltaHours = GetWorldDeltaHours(worldDeltaSeconds);
        cloudTime += worldDeltaSeconds
                   * cloudBaseSpeed
                   * (10f / Mathf.Max(cycleDurationMinutes, 0.1f))
                   * Mathf.Max(WeatherCloudSpeedMul, 0f);

        if (worldDeltaHours > 0d)
        {
            float dayProgress = (float)(worldDeltaHours / 24d);
            ApplyNormalizedDelta(
                dayProgress,
                TimeChangeRequest.AdvanceHours(dayProgress * 24f, this, "EditModeTick"),
                fireSkipped: false,
                countPlayerTime: false);
        }
#endif
    }
    #endregion

    #region Environment Rendering
    // ------------------------------------------------
    // Seasonal day-length
    // ------------------------------------------------

    float GetEffectiveDayRatio()
    {
        Calendar resolvedCalendar = Calendar;
        if (!enableSeasons || resolvedCalendar == null) return dayRatio;

        // Spring and Autumn are the equinox crossings; Summer and Winter
        // reach the authored positive and negative extrema continuously.
        float seasonOffset = Mathf.Sin(resolvedCalendar.YearProgress * Mathf.PI * 2f)
                           * seasonalDayVariation;
        return Mathf.Clamp(dayRatio + seasonOffset, 0.05f, 0.95f);
    }

    // Sunrise/sunset are derived from dayRatio so that day length changes visually while civil clock speed stays fixed.
    float GetSunriseTime(float effectiveDayRatio)
        => (1f - Mathf.Clamp01(effectiveDayRatio)) * 0.5f;

    float GetSunsetTime(float effectiveDayRatio)
        => 1f - GetSunriseTime(effectiveDayRatio);

    bool IsDaytimeAt(float normalizedTime, float effectiveDayRatio)
    {
        float wrappedTime = Mathf.Repeat(normalizedTime, 1f);
        float sunrise = GetSunriseTime(effectiveDayRatio);
        float sunset = GetSunsetTime(effectiveDayRatio);
        return wrappedTime >= sunrise && wrappedTime < sunset;
    }

    float ComputeSunAngle(float normalizedTime, float effectiveDayRatio)
    {
        float wrappedTime = Mathf.Repeat(normalizedTime, 1f);
        float dayLength = Mathf.Clamp(effectiveDayRatio, 0.05f, 0.95f);
        float nightLength = Mathf.Max(0.0001f, 1f - dayLength);
        float sunrise = GetSunriseTime(dayLength);
        float sunset = GetSunsetTime(dayLength);

        if (wrappedTime >= sunrise && wrappedTime < sunset)
        {
            float dayT = (wrappedTime - sunrise) / dayLength;
            return dayT * 180f;
        }

        float nightElapsed = wrappedTime >= sunset
            ? wrappedTime - sunset
            : wrappedTime + 1f - sunset;
        return 180f + (nightElapsed / nightLength) * 180f;
    }

    // ------------------------------------------------
    // Sun
    // ------------------------------------------------

    void UpdateSun(float effectiveDayRatio)
    {
        float sunAngle = ComputeSunAngle(timeOfDay, effectiveDayRatio);

        currentSunAngle = sunAngle;

        // Compute direction from angle (independent of Light component)
        Quaternion sunRot = Quaternion.AngleAxis(sunAngle, Vector3.right);
        Vector3 sunForward = sunRot * Vector3.forward;
        float dot = Vector3.Dot(sunForward, Vector3.down);

        // The sun body (CelestialBody) is visible while Direction.y > -0.05,
        // which equals dot > -0.05. Map elevation onto that same range so that
        // intensity, color, and environment all reach zero at the exact moment
 // the body disappears - keeping the light in lockstep with the sphere.
        const float horizonThreshold = -0.05f;
        float elevation = Mathf.InverseLerp(horizonThreshold, 1f, dot);

        // Cache for ambient/fog/eclipse (before eclipse modifies it)
        cachedDayFactor = elevation;
        cachedSunDirection = -sunForward;

        bool sunAboveHorizon = dot > horizonThreshold;

        // Drive the directional light if one exists on the prefab
        if (sunLight != null)
        {
            sunLight.transform.rotation = sunRot;
            sunLight.enabled = sunAboveHorizon;

            if (sunAboveHorizon)
            {
                sunLight.intensity = Mathf.Lerp(minIntensity, maxIntensity, elevation);
                sunLight.intensity *= 1f - Mathf.Clamp01(WeatherDim) * 0.75f;
                sunLight.shadows = LightShadows.Soft;
                sunLight.shadowStrength = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elevation / 0.15f));

                bool isMorning = timeOfDay < 0.5f;
                Color horizonColor = isMorning ? sunriseColor : sunsetColor;

                Color sunColor;
                if (elevation < 0.3f)
                    sunColor = Color.Lerp(Color.black, horizonColor, elevation / 0.3f);
                else if (elevation < 0.7f)
                    sunColor = Color.Lerp(horizonColor, noonColor, (elevation - 0.3f) / 0.4f);
                else
                    sunColor = noonColor;

                sunLight.color = sunColor;
            }
        }
    }

    // ------------------------------------------------
    // Moon
    // ------------------------------------------------

    void UpdateMoon(float daysFraction)
    {
        // Start is play-mode only, so the cached period is still zero when the
        // ExecuteAlways edit preview reaches this method. Resolve it at the
        // producer before dividing so invalid lunar data never reaches Daybox.
        if (!float.IsFinite(lunarPeriodDays) || lunarPeriodDays <= 0f)
        {
            float calendarPeriodDays = Calendar != null ? Calendar.AverageMonthLength : 28f;
            lunarPeriodDays = float.IsFinite(calendarPeriodDays) && calendarPeriodDays > 0f
                ? calendarPeriodDays
                : 28f;
        }

        currentLunarPhase = Mathf.Repeat(daysFraction / lunarPeriodDays + initialLunarPhase, 1f);

        // Moon illumination varies with phase.
        // Phase 0 = new moon (between camera and sun, dark face toward us).
        // Phase 0.5 = full moon (opposite sun, fully lit face toward us).
        currentMoonIllumination = (1f - Mathf.Cos(currentLunarPhase * 2f * Mathf.PI)) * 0.5f;

        // Moon angle = sun angle + phase offset
        float moonAngle = currentSunAngle + currentLunarPhase * 360f;

        // Orbital tilt oscillates via nodal precession
        float nodalAngle = daysFraction / nodalPrecessionDays * 360f + initialNodalPhase * 360f;
        float tiltOffset = lunarTiltDegrees * Mathf.Sin(nodalAngle * Mathf.Deg2Rad);

        // Compute direction from angle (independent of Light component)
        Quaternion moonRot = Quaternion.AngleAxis(tiltOffset, Vector3.forward)
                           * Quaternion.AngleAxis(moonAngle, Vector3.right);
        Vector3 moonForward = moonRot * Vector3.forward;

        cachedMoonDirection = -moonForward;

        float moonDot = Vector3.Dot(moonForward, Vector3.down);

        // Use the same horizon threshold as sun / CelestialBody (-0.05)
        const float moonHorizonThreshold = -0.05f;
        float moonElevation = Mathf.InverseLerp(moonHorizonThreshold, 1f, moonDot);

        bool moonAboveHorizon = moonDot > moonHorizonThreshold;

        // Drive the directional light if one exists on the prefab
        if (moonLight != null)
        {
            moonLight.transform.rotation = moonRot;
            moonLight.enabled = moonAboveHorizon;

            if (moonAboveHorizon)
            {
                moonLight.color = moonColorNight;
                moonLight.intensity = moonMaxIntensity * moonElevation * currentMoonIllumination;
                moonLight.intensity *= 1f - Mathf.Clamp01(WeatherDim) * 0.75f;
                moonLight.shadows = LightShadows.Soft;
                moonLight.shadowStrength = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(moonElevation * currentMoonIllumination * 10f));
            }
        }
    }

    void UpdateDominantAtmosphereLight()
    {
        float sunScore = AtmosphereLightScore(sunLight);
        float moonScore = AtmosphereLightScore(moonLight);
        const float switchHysteresis = 1.1f;

        if (dominantAtmosphereLight == sunLight)
        {
            if (moonScore > sunScore * switchHysteresis + 0.0001f)
                dominantAtmosphereLight = moonLight;
        }
        else if (dominantAtmosphereLight == moonLight)
        {
            if (sunScore > moonScore * switchHysteresis + 0.0001f)
                dominantAtmosphereLight = sunLight;
        }
        else
        {
            dominantAtmosphereLight = sunScore >= moonScore ? sunLight : moonLight;
        }

        if (AtmosphereLightScore(dominantAtmosphereLight) <= 0f)
            dominantAtmosphereLight = null;

        RenderSettings.sun = dominantAtmosphereLight;
    }

    static float AtmosphereLightScore(Light light)
    {
        if (light == null || !light.isActiveAndEnabled || light.type != LightType.Directional)
            return 0f;

        Color color = light.color;
        float luminance = color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f;
        return Mathf.Max(0f, light.intensity) * Mathf.Max(0f, luminance);
    }

    // ------------------------------------------------
    // Eclipses
    // ------------------------------------------------

    void UpdateEclipses()
    {
        Vector3 toSun  = cachedSunDirection;
        Vector3 toMoon = cachedMoonDirection;

        // Eclipses are only physically possible at specific phases:
        //   Solar eclipse  -> near new moon  (phase ~ 0 or ~ 1)
        //   Lunar eclipse  -> near full moon (phase ~ 0.5)
        float phaseDistFromNew  = Mathf.Min(currentLunarPhase, 1f - currentLunarPhase);
        float phaseDistFromFull = Mathf.Abs(currentLunarPhase - 0.5f);

        bool nearNewMoon  = phaseDistFromNew  < eclipsePhaseWindow;
        bool nearFullMoon = phaseDistFromFull < eclipsePhaseWindow;

        // Solar eclipse: moon overlaps the sun disc (requires new moon)
        float solarDist = Vector3.Angle(toSun, toMoon);
        solarEclipseFactor = nearNewMoon
            ? Mathf.Clamp01(1f - solarDist / eclipseThresholdDegrees)
            : 0f;

        // Lunar eclipse: moon enters Earth's shadow (requires full moon)
        float lunarDist = Vector3.Angle(-toSun, toMoon);
        lunarEclipseFactor = nearFullMoon
            ? Mathf.Clamp01(1f - lunarDist / eclipseThresholdDegrees)
            : 0f;

 // Bias eclipses toward the apex of the orbit - suppress near horizon
        if (eclipseApexBias > 0f)
        {
            float sunElev  = Mathf.Clamp01(Vector3.Dot(toSun,  Vector3.up));
            float moonElev = Mathf.Clamp01(Vector3.Dot(toMoon, Vector3.up));
            solarEclipseFactor *= Mathf.Pow(sunElev,  eclipseApexBias);
            lunarEclipseFactor *= Mathf.Pow(moonElev, eclipseApexBias);
        }

 // Apply solar eclipse - dim the sun and tint toward corona color
        if (solarEclipseFactor > 0f && sunLight != null && sunLight.enabled)
        {
            sunLight.intensity *= 1f - solarEclipseFactor * 0.95f;
            sunLight.color = Color.Lerp(sunLight.color, solarEclipseLightColor, solarEclipseFactor);
        }

 // Apply lunar eclipse - dim + tint the moon
        if (lunarEclipseFactor > 0f && moonLight != null && moonLight.enabled)
        {
            moonLight.intensity *= 1f - lunarEclipseFactor * 0.7f;
            moonLight.color = Color.Lerp(moonLight.color, lunarEclipseTint, lunarEclipseFactor);
        }

        // Apply eclipse darkening to the cached dayFactor used by ambient/fog
        cachedDayFactor *= 1f - solarEclipseFactor * 0.9f;

        debugLunarPhase       = currentLunarPhase;
        debugMoonIllumination = currentMoonIllumination;
        debugEclipseStrength  = Mathf.Max(solarEclipseFactor, lunarEclipseFactor);
        debugIsSolarEclipse   = solarEclipseFactor > 0.01f;
        debugIsLunarEclipse   = lunarEclipseFactor > 0.01f;
    }

    // ------------------------------------------------
    // Celestial body visuals (sun / moon spheres)
    // ------------------------------------------------

    void UpdateCelestialBodies()
    {
        if (sunBody != null)
        {
            sunBody.Direction     = cachedSunDirection;
            sunBody.EclipseFactor = solarEclipseFactor;
            // During eclipse, tint the sun sphere toward the corona glow color
            Color baseSunColor = sunLight != null ? sunLight.color : noonColor;
            sunBody.ColorOverride = Color.Lerp(baseSunColor, solarEclipseLightColor, solarEclipseFactor);
            // Point SunDirection AWAY from the sun (toward camera) so the
 // shader lights the camera-facing hemisphere - the sun is self-luminous.
            sunBody.SunDirection  = -cachedSunDirection;
            sunBody.Refresh();
        }

        if (moonBody != null)
        {
            moonBody.Direction     = cachedMoonDirection;
            moonBody.EclipseFactor = lunarEclipseFactor * 0.3f;
            moonBody.ColorOverride = Color.Lerp(moonColorNight, lunarEclipseTint, lunarEclipseFactor);
            moonBody.SunDirection  = cachedSunDirection;
            moonBody.Refresh();
        }
    }

    // ------------------------------------------------
    // Tertiary planets
    // ------------------------------------------------

    void UpdateTertiaryPlanets(float daysFraction)
    {
        if (tertiaryInstances == null) return;

        for (int i = 0; i < tertiaryInstances.Count; i++)
        {
            var rt = tertiaryInstances[i];
            if (rt.body == null || rt.config == null) continue;

            var cfg = rt.config;

            // Orbital phase for this planet
            float phase = Mathf.Repeat(daysFraction / cfg.orbitalPeriodDays + cfg.initialPhase, 1f);
            float angle = phase * 360f;

            // Build rotation: orbit around X (ecliptic), then apply inclination around Z
            Quaternion orbit = Quaternion.AngleAxis(cfg.orbitalTiltDegrees, Vector3.forward)
                             * Quaternion.AngleAxis(angle, Vector3.right);

            // Direction from camera toward the planet (negate light-forward convention)
            Vector3 direction = -(orbit * Vector3.forward);

            rt.body.Direction     = direction;
            rt.body.EclipseFactor = 0f;
            rt.body.SunDirection  = cachedSunDirection;
            rt.body.Refresh();

            // If the planet config declares a light, drive it
            Light planetLight = rt.body.AttachedLight;
            if (planetLight != null && cfg.hasLight)
            {
                float elev = Mathf.Clamp01(Vector3.Dot(orbit * Vector3.forward, Vector3.down));
                bool above = elev > 0.01f;
                planetLight.enabled = above;
                if (above)
                {
                    planetLight.transform.rotation = orbit;
                    planetLight.intensity = Mathf.Lerp(cfg.minLightIntensity, cfg.maxLightIntensity, elev);
                    planetLight.color = cfg.lightColor;
                    planetLight.shadows = cfg.castShadows ? LightShadows.Soft : LightShadows.None;
                }
            }
        }
    }

    // ------------------------------------------------
    // Ambient + Fog (single pass, cached dayFactor)
    // ------------------------------------------------

    void UpdateEnvironment()
    {
        if (!environmentUpdateGate.TryBeginFrame(Time.frameCount))
            return;

        Sol.Environment.SolEnvironmentBudget.AddEnvironmentUpdate();
        float eclipseEnv = solarEclipseFactor;
        float weatherDim = Mathf.Clamp01(WeatherDim);
        float cloudiness = Mathf.Clamp01(WeatherCloudiness);
        float lightning  = Mathf.Clamp01(WeatherLightningFlash);

        // Flat ambient fallback. When ambientFromSky is on, Trilight ambient
        // is derived from the computed sky colors inside the skybox block.
        if (controlAmbient && (!ambientFromSky || !controlSkybox))
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            Color ambient = Color.Lerp(ambientNightColor, ambientDayColor, cachedDayFactor);
            ambient *= 1f - weatherDim * 0.5f;
            if (eclipseEnv > 0f)
                ambient = Color.Lerp(ambient, eclipseAmbientColor, eclipseEnv);
            if (lightning > 0f)
                ambient += Color.white * (lightning * 0.5f);
            RenderSettings.ambientLight = ambient;
        }

        if (controlFog)
        {
            bool isNight = cachedDayFactor < 0.1f;
            RenderSettings.fog = isNight ? enableNightFog : true;
            Color fogCol = Color.Lerp(fogNightColor, fogDayColor, cachedDayFactor);
            float fogDen = Mathf.Lerp(fogNightDensity, fogDayDensity, cachedDayFactor);
            fogCol *= 1f - weatherDim * 0.4f;
            fogDen *= 1f + Mathf.Max(0f, WeatherFogBoost);
            if (eclipseEnv > 0f)
                fogCol = Color.Lerp(fogCol, eclipseFogColor, eclipseEnv);
            RenderSettings.fogColor = fogCol;
            RenderSettings.fogDensity = fogDen;
        }

        if (controlSkybox)
        {
            Material sky = controlledSkyboxMaterial != null
                ? controlledSkyboxMaterial
                : RenderSettings.skybox;
            if (sky != null)
            {
                float df = cachedDayFactor;

                // Zenith: night ? day, with eclipse overlay
                Color zenith = Color.Lerp(skyZenithNight, skyZenithDay, df);
                zenith *= 1f - weatherDim * 0.35f;
                if (eclipseEnv > 0f)
                    zenith = Color.Lerp(zenith, skyZenithEclipse, eclipseEnv);

                // Horizon: cool base gradient; the warm dawn/dusk tint is
                // applied azimuthally around the sun in the shader via
                // _HorizonWarmColor so the anti-solar side stays cool.
                Color horizon = Color.Lerp(skyHorizonNight, skyHorizonDay, df);
                horizon *= 1f - weatherDim * 0.3f;
                if (eclipseEnv > 0f)
                    horizon = Color.Lerp(horizon, skyHorizonEclipse, eclipseEnv);

                // Lightning: sheet flash brightens the sky, horizon most.
                if (lightning > 0f)
                {
                    zenith  += Color.white * (lightning * 0.6f);
                    horizon += Color.white * (lightning * 0.8f);
                }

                bool isMorning = timeOfDay < 0.5f;
                Color warmHorizon = isMorning ? skyHorizonSunrise : skyHorizonSunset;
                // Warmth peaks during the horizon transition, vanishes at
                // full day and full night, and is suppressed by eclipses
                // and storm cover.
                float warmth = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(df / 0.25f))
                             * (1f - Mathf.SmoothStep(0.35f, 0.75f, df))
                             * (1f - eclipseEnv)
                             * (1f - weatherDim * 0.85f)
                             * (1f - cloudiness * 0.45f);

                // Nadir: simple night ? day
                Color nadir = Color.Lerp(skyNadirNight, skyNadirDay, df);

                // -- Sky-derived ambient & fog --
                // Everything upstream (eclipse, storm dim, lightning) is
                // already folded into these colors, so scene lighting and
                // fog track the sky automatically.
                if (controlAmbient && ambientFromSky)
                {
                    RenderSettings.ambientMode         = AmbientMode.Trilight;
                    RenderSettings.ambientSkyColor     = zenith  * ambientSkyIntensity;
                    RenderSettings.ambientEquatorColor = horizon * ambientSkyIntensity;
                    RenderSettings.ambientGroundColor  = nadir   * (ambientSkyIntensity * 0.9f);
                }
                if (controlFog && fogColorFromSky)
                {
                    // Dense weather should inherit the horizon value without
                    // turning the whole fog volume into a saturated sunset band.
                    // The same result feeds both surface and sky atmosphere,
                    // preserving the horizon match.
                    Color neutralFog = Color.Lerp(fogNightColor, fogDayColor, df)
                                     * (1f - weatherDim * 0.15f);
                    float stormNeutrality = weatherDim * cloudiness * 0.65f;
                    RenderSettings.fogColor = Color.Lerp(horizon, neutralFog, stormNeutrality);
                }

                // Stars: visible at night, fade out during the day
                float starIntensity = Mathf.Lerp(starIntensityNight, 0f, df);

                // Blends: interpolate day/night sharpness
                float zenithBlend  = Mathf.Lerp(skyZenithBlendNight,  skyZenithBlendDay,  df);
                float horizonBlend = Mathf.Lerp(skyHorizonBlendNight, skyHorizonBlendDay, df);
                float nadirBlend   = Mathf.Lerp(skyNadirBlendNight,   skyNadirBlendDay,   df);

                // -- Sky gradient --
                sky.SetColor(_ZenithColorID,    zenith);
                sky.SetColor(_HorizonColorID,   horizon);
                sky.SetColor(_HorizonWarmColorID,
                    new Color(warmHorizon.r, warmHorizon.g, warmHorizon.b, warmth));
                sky.SetColor(_NadirColorID,     nadir);
                sky.SetFloat(_ZenithBlendID,    zenithBlend);
                sky.SetFloat(_HorizonBlendID,   horizonBlend);
                sky.SetFloat(_NadirBlendID,     nadirBlend);
                sky.SetFloat(_StarIntensityID,  starIntensity);
                sky.SetFloat(_StarPowerID,      starPower);
                sky.SetFloat(_StarHeightID,     starHeight);
                // One dome revolution per civil day. Whole days are identity
                // rotations, so timeOfDay alone keeps the angle small and
                // continuous across the midnight wrap.
                sky.SetFloat(_StarRotationID,   timeOfDay * 2f * Mathf.PI);
                // Night factor gates the milky way (stars are gated by
                // _StarIntensity already).
                sky.SetFloat(_NightFactorID,    1f - df);

                // Aurora: some nights get a display (deterministic hash of
                // the calendar day), visible only at night and suppressed
                // by storm cover.
                float aurora = 0f;
                if (enableAurora)
                {
                    float dayHash = Mathf.Abs(Mathf.Sin((float)WorldDayIndex * 12.9898f + 78.233f)) * 43758.5453f;
                    dayHash -= Mathf.Floor(dayHash);
                    if (dayHash < auroraNightChance)
                        aurora = auroraIntensity * (1f - df) * (1f - cloudiness);
                }
                sky.SetFloat(_AuroraIntensityID, aurora);

                // -- Sun disc --
                sky.SetVector(_SunDirectionID,  cachedSunDirection);
                Color sunDiscColor = (sunLight != null ? sunLight.color : noonColor) * sunDiscIntensity;
                sky.SetColor(_SunDiscColorID,   sunDiscColor);
                sky.SetFloat(_SunDiscSizeID,    sunDiscSize);
                sky.SetColor(_CoronaColorID,    coronaColor);

                // -- Moon disc --
                sky.SetVector(_MoonDirectionID,   cachedMoonDirection);
                sky.SetColor(_MoonColorID,        moonLitColor);
                sky.SetColor(_MoonDarkColorID,    moonDarkColor);
                sky.SetFloat(_MoonDiscSizeID,     moonDiscSize);
                sky.SetFloat(_MoonSharpnessID,    moonTerminatorSharpness);

                // -- Eclipses --
                sky.SetFloat(_SolarEclipseFactorID, solarEclipseFactor);
                sky.SetFloat(_LunarEclipseFactorID, lunarEclipseFactor);
                sky.SetColor(_EclipseTintID,        lunarEclipseTint);

                // -- Clouds --
                float cloudCoverage = Mathf.Lerp(cloudCoverageNight, cloudCoverageDay, df);
                float cloudDensity  = Mathf.Lerp(cloudDensityNight,  cloudDensityDay,  df);

                // Weather cloudiness pushes toward full overcast (coverage
                // parameter is inverted: lower value = more cloud).
                cloudCoverage = Mathf.Lerp(cloudCoverage, 0.02f, cloudiness);
                cloudDensity  = Mathf.Lerp(cloudDensity,  0.95f, cloudiness);

                Color cloudLit;
                if (df < 0.3f)
                    cloudLit = Color.Lerp(cloudColorNight, cloudColorSunset, df / 0.3f);
                else if (df < 0.7f)
                    cloudLit = Color.Lerp(cloudColorSunset, cloudColorDay, (df - 0.3f) / 0.4f);
                else
                    cloudLit = cloudColorDay;

                Color cloudShadow = Color.Lerp(cloudShadowNight, cloudShadowDay, df);

                // Storm darkening + lightning glow on cloud bases.
                cloudLit    *= 1f - weatherDim * 0.5f;
                cloudShadow *= 1f - weatherDim * 0.55f;
                if (lightning > 0f)
                    cloudLit += Color.white * (lightning * 0.7f);

                sky.SetFloat(_CloudScaleID,        cloudScale);
                sky.SetFloat(_CloudSpeedID,        1f);
                sky.SetFloat(_CloudTimeID,         cloudTime);
                Vector3 wind = WeatherWindDirection.sqrMagnitude > 0.0001f
                    ? WeatherWindDirection.normalized
                    : Vector3.right;
                sky.SetVector(_CloudWindDirectionID, new Vector4(wind.x, wind.z, 0f, 0f));
                sky.SetFloat(_CloudErosionID,       Mathf.Clamp01(WeatherCloudErosion));
                ResolveCloudTextures();
                if (_resolvedCloudNoiseTexture != null)
                    Shader.SetGlobalTexture(_CloudNoiseTexID, _resolvedCloudNoiseTexture);
                if (_resolvedCloudWeatherMap != null)
                    Shader.SetGlobalTexture(_CloudWeatherMapID, _resolvedCloudWeatherMap);
                sky.SetFloat(_CloudNoiseTilingID, 1f / 8f);
                sky.SetFloat(_CloudWeatherScaleID, 0.06f);
                sky.SetFloat(_CloudWeatherInfluenceID, cloudWeatherInfluence);
                sky.SetFloat(_CloudTypeInfluenceID, cloudTypeInfluence);
                sky.SetFloat(_CloudCoverageID,     cloudCoverage);
                sky.SetFloat(_CloudDensityID,      cloudDensity);
                sky.SetFloat(_CloudHeightID,       cloudHeight);
                sky.SetColor(_CloudColorID,        cloudLit);
                sky.SetColor(_CloudShadowColorID,  cloudShadow);
                ApplyCloudQualityKeywords(sky);

                // -- Atmosphere --
                float glowIntensity = Mathf.Lerp(sunGlowIntensityNight, sunGlowIntensityDay, df);
                float hazeIntensity = Mathf.Lerp(hazeIntensityNight, hazeIntensityDay, df);
                glowIntensity *= (1f - weatherDim * 0.6f)
                               * (1f - cloudiness * 0.75f);
                hazeIntensity *= (1f - weatherDim * 0.7f)
                               * (1f - cloudiness * 0.35f);

                sky.SetFloat(_SunGlowFalloffID,    sunGlowFalloff);
                sky.SetFloat(_SunGlowIntensityID,  glowIntensity);
                sky.SetFloat(_HazeIntensityID,     hazeIntensity);
            }
        }
    }

    /// <summary>
    /// Rebuilds the current frame's environment after the weather director has
    /// published its blended values. TimeOfDay runs before weather so its clock
    /// and celestial state are current; weather calls this once afterward so
    /// fog, ambient light, sky colors, and clouds use that same frame's weather.
    /// </summary>
    internal void RefreshEnvironmentFromWeather()
    {
        if (!isActiveAndEnabled)
            return;

        // Weather runs after the celestial solver and now owns the normal application
        // point. Invalidate also covers explicit weather changes made after an earlier
        // fallback in the same frame, where the better-informed state must replace it.
        environmentUpdateGate.Invalidate();
        UpdateEnvironment();
    }

    #endregion

    #region Time Change Requests
    /// <summary>
    /// Apply a command-style time change.
    /// This is the preferred mutation path because future multiplayer can validate this request before applying it.
    /// </summary>
    public TimeChangeResult ApplyTimeChange(TimeChangeRequest request)
    {
        switch (request.Type)
        {
            case TimeChangeType.AdvanceHours:
                return ApplyNormalizedDelta(request.Value / 24f, request, fireSkipped: true, countPlayerTime: request.Value > 0f);
            case TimeChangeType.RewindHours:
                return ApplyNormalizedDelta(-request.Value / 24f, request, fireSkipped: true, countPlayerTime: false);
            case TimeChangeType.SetClockHour:
                return SetNormalizedTimeInternal(ToNormalizedTimeFromClockHour(request.Value), request, fireSkipped: true);
            case TimeChangeType.SetNormalizedTime:
                return SetNormalizedTimeInternal(request.Value, request, fireSkipped: true);
            case TimeChangeType.SetTimeScale:
                SetTimeScale(request.Value);
                return CreateNoTimeChangeResult(request);
            case TimeChangeType.SetPaused:
                SetPaused(request.Value > 0.5f);
                return CreateNoTimeChangeResult(request);
            case TimeChangeType.SkipToSunrise:
                return SkipToSunriseInternal(request);
            case TimeChangeType.SkipToSunset:
                return SkipToSunsetInternal(request);
            case TimeChangeType.SkipForwardOneDay:
                return ApplyCalendarDelta(1, request, fireSkipped: true, countPlayerTime: true);
            case TimeChangeType.SkipBackwardOneDay:
                return ApplyCalendarDelta(-1, request, fireSkipped: true, countPlayerTime: false);
            default:
                return CreateNoTimeChangeResult(request);
        }
    }

    /// <summary>Set midnight-based civil-day progress directly. Prefer this over writing <see cref="CurrentTime"/>.</summary>
    public TimeChangeResult SetNormalizedTime(float normalizedTime, UnityEngine.Object source = null, string reason = null)
        => ApplyTimeChange(TimeChangeRequest.SetNormalizedTime(normalizedTime, source, reason));

    /// <summary>Set the constant-rate civil clock hour. 00:00 maps to normalized 0.</summary>
    public TimeChangeResult SetClockHour(float hour, UnityEngine.Object source = null, string reason = null)
        => ApplyTimeChange(TimeChangeRequest.SetClockHour(hour, source, reason));

    /// <summary>Advance world time by civil-clock hours and update calendar days as needed.</summary>
    public TimeChangeResult AdvanceHours(float hours, UnityEngine.Object source = null, string reason = null)
        => ApplyTimeChange(TimeChangeRequest.AdvanceHours(hours, source, reason));

    /// <summary>Rewind world time by civil-clock hours and rewind calendar days as needed.</summary>
    public TimeChangeResult RewindHours(float hours, UnityEngine.Object source = null, string reason = null)
        => ApplyTimeChange(TimeChangeRequest.RewindHours(hours, source, reason));

    /// <summary>
    /// Set the game-world time multiplier.
    /// This is intentionally separate from <see cref="UnityEngine.Time.timeScale"/>, which UI pause menus may use.
    /// </summary>
    public void SetTimeScale(float scale)
    {
        float oldScale = timeScale;
        timeScale = Mathf.Max(0f, scale);
        if (timeScale <= 0f)
            presentationDeltaSeconds = 0f;
        if (!Mathf.Approximately(oldScale, timeScale))
            TimeScaleChanged?.Invoke(oldScale, timeScale);
    }

    /// <summary>Pause or resume the world clock without changing its configured time multiplier.</summary>
    public void SetPaused(bool isPaused)
    {
        paused = isPaused;
        if (paused)
        {
            worldDeltaSeconds = 0f;
            worldDeltaHours = 0d;
            presentationDeltaSeconds = 0f;
        }
    }

    /// <summary>
    /// Unity-scaled presentation time used for bounded weather transitions and
    /// transient envelopes. It freezes whenever Sol time is not moving forward
    /// but deliberately ignores the Sol 1x/10x/100x multiplier.
    /// </summary>
    public float GetPresentationDeltaSeconds(float unityDeltaSeconds)
        => paused || timeScale <= 0f ? 0f : Mathf.Max(0f, unityDeltaSeconds);

    /// <summary>
    /// Convert Unity scaled seconds into Sol world-simulation seconds.
    /// Unity's time scale is already represented by <paramref name="unityDeltaSeconds"/>;
    /// this method applies the Sol multiplier and pause exactly once.
    /// </summary>
    public float GetWorldDeltaSeconds(float unityDeltaSeconds)
        => paused ? 0f : Mathf.Max(0f, unityDeltaSeconds) * timeScale;

    /// <summary>Convert Sol world-simulation seconds into civil world hours.</summary>
    public double GetWorldDeltaHours(float solWorldDeltaSeconds)
    {
        double cycleSeconds = Math.Max(cycleDurationMinutes * 60d, 0.1d);
        return Math.Max(solWorldDeltaSeconds, 0f) * 24d / cycleSeconds;
    }

    /// <summary>Start a new game from the configured calendar date and reset both clocks.</summary>
    public void StartNewGame()
    {
        Calendar resolvedCalendar = ResolveCalendar();
        resolvedCalendar?.ResetToStart();
        timeOfDay = Mathf.Repeat(timeOfDay, 1f);
        calendarInitialized = resolvedCalendar != null;
    }

    /// <summary>Compatibility restore overload. Legacy totalDays maps to completed player-time days.</summary>
    [Obsolete("Use RestoreTimeSnapshot with worldDayIndex and playerDaysElapsed.")]
    public TimeChangeResult RestoreTimeSnapshot(float normalizedTime, int day, int month, int year, int totalDays, UnityEngine.Object source = null, string reason = "SaveLoad")
    {
        Calendar resolvedCalendar = ResolveCalendar();
        if (resolvedCalendar != null)
        {
            resolvedCalendar.SetDate(day, month, year);
            return RestoreTimeSnapshot(normalizedTime, day, month, year,
                resolvedCalendar.WorldDayIndex, totalDays, source, reason);
        }

        return RestoreTimeSnapshot(normalizedTime, day, month, year, 0L, totalDays, source, reason);
    }

    /// <summary>Restore clock time, rewindable world date, and forward-only player time together.</summary>
    public TimeChangeResult RestoreTimeSnapshot(
        float normalizedTime,
        int day,
        int month,
        int year,
        long worldDayIndex,
        double playerDaysElapsed,
        UnityEngine.Object source = null,
        string reason = "SaveLoad")
    {
        float oldNormalized = timeOfDay;
        float oldClock = ClockHour;
        int oldDays = CompletedPlayerDays;
        long oldWorldDay = WorldDayIndex;
        double oldPlayerTime = PlayerDaysElapsed;

        Calendar resolvedCalendar = ResolveCalendar();
        if (resolvedCalendar != null)
        {
            resolvedCalendar.SetDate(day, month, year, worldDayIndex, playerDaysElapsed);
            calendarInitialized = true;
        }

        timeOfDay = Mathf.Repeat(normalizedTime, 1f);
        TimeChangeRequest request = TimeChangeRequest.SetNormalizedTime(normalizedTime, source, reason);
        TimeChangeResult result = CreateTimeChangeResult(request, oldNormalized, oldClock, oldDays, oldWorldDay, oldPlayerTime);
        PublishTimeResult(result, fireSkipped: false, oldPlayerTime);
        return result;
    }

    private TimeChangeResult ApplyNormalizedDelta(
        float normalizedDelta,
        TimeChangeRequest request,
        bool fireSkipped,
        bool countPlayerTime)
    {
        float oldNormalized = timeOfDay;
        float oldClock = ClockHour;
        int oldDays = CompletedPlayerDays;
        long oldWorldDay = WorldDayIndex;
        double oldPlayerTime = PlayerDaysElapsed;

        float newTime = timeOfDay + normalizedDelta;
        int daysCrossed = Mathf.FloorToInt(newTime);
        timeOfDay = newTime - daysCrossed;
        ApplyCalendarDeltaOnly(daysCrossed);
        if (countPlayerTime && normalizedDelta > 0f)
            ResolveCalendar()?.AdvancePlayerTime(normalizedDelta);

        TimeChangeResult result = CreateTimeChangeResult(request, oldNormalized, oldClock, oldDays, oldWorldDay, oldPlayerTime);
        PublishTimeResult(result, fireSkipped, oldPlayerTime);
        return result;
    }

    private TimeChangeResult SetNormalizedTimeInternal(float normalizedTime, TimeChangeRequest request, bool fireSkipped)
    {
        float oldNormalized = timeOfDay;
        float oldClock = ClockHour;
        int oldDays = CompletedPlayerDays;
        long oldWorldDay = WorldDayIndex;
        double oldPlayerTime = PlayerDaysElapsed;

        timeOfDay = Mathf.Repeat(normalizedTime, 1f);

        TimeChangeResult result = CreateTimeChangeResult(request, oldNormalized, oldClock, oldDays, oldWorldDay, oldPlayerTime);
        PublishTimeResult(result, fireSkipped, oldPlayerTime);
        return result;
    }

    private TimeChangeResult ApplyCalendarDelta(int dayDelta, TimeChangeRequest request, bool fireSkipped, bool countPlayerTime)
    {
        float oldNormalized = timeOfDay;
        float oldClock = ClockHour;
        int oldDays = CompletedPlayerDays;
        long oldWorldDay = WorldDayIndex;
        double oldPlayerTime = PlayerDaysElapsed;

        ApplyCalendarDeltaOnly(dayDelta);
        if (countPlayerTime && dayDelta > 0)
            ResolveCalendar()?.AdvancePlayerTime(dayDelta);

        TimeChangeResult result = CreateTimeChangeResult(request, oldNormalized, oldClock, oldDays, oldWorldDay, oldPlayerTime);
        PublishTimeResult(result, fireSkipped, oldPlayerTime);
        return result;
    }

    private TimeChangeResult SkipToSunriseInternal(TimeChangeRequest request)
    {
        float oldNormalized = timeOfDay;
        float oldClock = ClockHour;
        int oldDays = CompletedPlayerDays;
        long oldWorldDay = WorldDayIndex;
        double oldPlayerTime = PlayerDaysElapsed;

        float sunriseTime = GetSunriseTime(GetEffectiveDayRatio());
        float forwardDelta = sunriseTime - timeOfDay;
        if (forwardDelta <= 0f)
        {
            forwardDelta += 1f;
            ApplyCalendarDeltaOnly(1);
        }
        timeOfDay = sunriseTime;
        ResolveCalendar()?.AdvancePlayerTime(forwardDelta);

        TimeChangeResult result = CreateTimeChangeResult(request, oldNormalized, oldClock, oldDays, oldWorldDay, oldPlayerTime);
        PublishTimeResult(result, fireSkipped: true, oldPlayerTime);
        return result;
    }

    private TimeChangeResult SkipToSunsetInternal(TimeChangeRequest request)
    {
        float oldNormalized = timeOfDay;
        float oldClock = ClockHour;
        int oldDays = CompletedPlayerDays;
        long oldWorldDay = WorldDayIndex;
        double oldPlayerTime = PlayerDaysElapsed;
        float sunsetTime = GetSunsetTime(GetEffectiveDayRatio());

        float forwardDelta = sunsetTime - timeOfDay;
        if (forwardDelta <= 0f)
        {
            forwardDelta += 1f;
            ApplyCalendarDeltaOnly(1);
        }
        timeOfDay = sunsetTime;
        ResolveCalendar()?.AdvancePlayerTime(forwardDelta);

        TimeChangeResult result = CreateTimeChangeResult(request, oldNormalized, oldClock, oldDays, oldWorldDay, oldPlayerTime);
        PublishTimeResult(result, fireSkipped: true, oldPlayerTime);
        return result;
    }

    private void ApplyCalendarDeltaOnly(int dayDelta)
    {
        Calendar resolvedCalendar = ResolveCalendar();
        if (resolvedCalendar == null || dayDelta == 0)
            return;

        if (dayDelta > 0)
            resolvedCalendar.AdvanceDays(dayDelta);
        else
            resolvedCalendar.RewindDays(-dayDelta);

        calendarInitialized = true;
    }

    private TimeChangeResult CreateTimeChangeResult(
        TimeChangeRequest request,
        float oldNormalized,
        float oldClock,
        int oldDays,
        long oldWorldDay,
        double oldPlayerTime)
    {
        float newClock = ClockHour;
        int newDays = CompletedPlayerDays;
        bool changed = !Mathf.Approximately(oldNormalized, timeOfDay)
            || oldWorldDay != WorldDayIndex
            || !Approximately(PlayerDaysElapsed, oldPlayerTime);
        long newWorldDay = WorldDayIndex;
        double appliedWorldHours = IsChronologicalMutation(request.Type)
            ? ((newWorldDay - oldWorldDay) + (double)timeOfDay - oldNormalized) * 24d
            : 0d;

        return new TimeChangeResult(
            request,
            oldNormalized,
            timeOfDay,
            oldClock,
            newClock,
            oldDays,
            newDays,
            oldWorldDay,
            newWorldDay,
            appliedWorldHours,
            oldPlayerTime,
            PlayerDaysElapsed,
            changed);
    }

    private TimeChangeResult CreateNoTimeChangeResult(TimeChangeRequest request)
        => new(
            request,
            timeOfDay,
            timeOfDay,
            ClockHour,
            ClockHour,
            CompletedPlayerDays,
            CompletedPlayerDays,
            WorldDayIndex,
            WorldDayIndex,
            0d,
            PlayerDaysElapsed,
            PlayerDaysElapsed,
            changed: false);

    static bool IsChronologicalMutation(TimeChangeType type)
        => type == TimeChangeType.AdvanceHours
        || type == TimeChangeType.RewindHours
        || type == TimeChangeType.SkipToSunrise
        || type == TimeChangeType.SkipToSunset
        || type == TimeChangeType.SkipForwardOneDay
        || type == TimeChangeType.SkipBackwardOneDay;

    private void PublishTimeResult(TimeChangeResult result, bool fireSkipped, double oldPlayerTime)
    {
        if (!result.Changed)
            return;

        TimeChanged?.Invoke(result);

        int oldHour = Mathf.FloorToInt(Mathf.Repeat(result.OldClockHour, 24f));
        int newHour = Mathf.FloorToInt(Mathf.Repeat(result.NewClockHour, 24f));
        if (oldHour != newHour)
            HourChanged?.Invoke(oldHour, newHour);

        if (result.OldTotalDays != result.NewTotalDays)
            DayChanged?.Invoke(result.OldTotalDays, result.NewTotalDays);

        if (!Approximately(oldPlayerTime, PlayerDaysElapsed))
            PlayerTimeChanged?.Invoke(oldPlayerTime, PlayerDaysElapsed);

        if (fireSkipped)
            TimeSkipped?.Invoke(result);
    }

    static bool Approximately(double a, double b)
        => Math.Abs(a - b) <= 0.000000001d;
    #endregion

    #region Clock Conversion
    /// <summary>
    /// Current civil-day progress (0 = 00:00, 1 = 24:00).
    /// Setter remains for save/backward compatibility but routes through the service mutation path.
    /// </summary>
    public float CurrentTime
    {
        get => timeOfDay;
        set => SetNormalizedTime(value, this, "CurrentTime compatibility setter");
    }

    /// <summary>
    /// Current civil clock hour (0-24), advancing at a constant pace.
    /// Day ratio changes daylight duration, not clock speed.
    /// </summary>
    public float ClockHour => ToClockHour(timeOfDay);

    /// <summary>Raw normalized cycle projected onto 0-24 hours, where 0 is midnight.</summary>
    public float CycleHour => timeOfDay * 24f;

    /// <summary>Return the civil clock hour after advancing by the given number of game hours.</summary>
    public float GetClockHourAfterHours(float hours)
    {
        float projectedTime = Mathf.Repeat(timeOfDay + hours / 24f, 1f);
        return ToClockHour(projectedTime);
    }

    /// <summary>Convert midnight-based normalized cycle time to civil clock hour. Day ratio is deliberately ignored.</summary>
    public float ToClockHour(float normalizedTime)
        => Mathf.Repeat(Mathf.Repeat(normalizedTime, 1f) * 24f, 24f);

    /// <summary>Convert civil clock hour to midnight-based normalized cycle time.</summary>
    public float ToNormalizedTimeFromClockHour(float hour)
        => Mathf.Repeat(hour / 24f, 1f);
    #endregion

    #region Calendar Integration
    /// <summary>The Calendar component on this GameObject, resolved lazily for save/load ordering.</summary>
    public Calendar Calendar => ResolveCalendar();

    /// <summary>Signed world-date offset from the configured starting date.</summary>
    public long WorldDayIndex => Calendar != null ? Calendar.WorldDayIndex : 0L;

    /// <summary>Forward-only fractional days experienced by the player.</summary>
    public double PlayerDaysElapsed => Calendar != null ? Calendar.PlayerDaysElapsed : 0d;

    /// <summary>Completed forward-only player days.</summary>
    public int CompletedPlayerDays => PlayerDaysElapsed >= int.MaxValue
        ? int.MaxValue
        : Mathf.FloorToInt((float)Math.Max(0d, PlayerDaysElapsed));

    /// <summary>Compatibility alias for completed player days.</summary>
    [Obsolete("Use PlayerDaysElapsed for player time or WorldDayIndex for rewindable world-date time.")]
    public int TotalDaysElapsed => CompletedPlayerDays;

    private Calendar ResolveCalendar()
    {
        if (calendar == null)
            calendar = GetComponent<Calendar>();
        return calendar;
    }
    #endregion

    #region Compatibility API
    /// <summary>Compatibility alias for <see cref="ClockHour"/>. New gameplay should use ClockHour.</summary>
    public float Hour => ClockHour;

    /// <summary>Compatibility alias for <see cref="ClockHour"/>. The old name remains to avoid breaking authored systems.</summary>
    public float SkyAlignedHour => ClockHour;

    /// <summary>Compatibility alias for <see cref="GetClockHourAfterHours(float)"/>.</summary>
    public float GetSkyAlignedHourAfterHours(float hours) => GetClockHourAfterHours(hours);

    /// <summary>Compatibility alias for <see cref="ToClockHour(float)"/>.</summary>
    public float ToSkyAlignedHour(float normalizedTime) => ToClockHour(normalizedTime);

    /// <summary>Compatibility wrapper for <see cref="SetClockHour(float, UnityEngine.Object, string)"/>.</summary>
    public void SetHour(float hour) => SetClockHour(hour, this, "SetHour compatibility wrapper");
    #endregion

    #region Public Time State
    /// <summary>True when the sun is above the horizon according to the effective day ratio.</summary>
    public bool IsDaytime => IsDaytimeAt(timeOfDay, GetEffectiveDayRatio());

    /// <summary>How far the sun is above the horizon (-1 to 1).</summary>
    public float SunElevation => cachedDayFactor * 2f - 1f;

    /// <summary>Current sun light intensity.</summary>
    public float Intensity => sunLight ? sunLight.intensity : 0f;

    /// <summary>Current sun light color.</summary>
    public Color SunColor => sunLight ? sunLight.color : Color.black;

    /// <summary>Runtime directional light created for the sun, when configured.</summary>
    public Light SunLight => sunLight;

    /// <summary>Runtime directional light created for the moon, when configured.</summary>
    public Light MoonLight => moonLight;

    /// <summary>Directional light currently used by URP and the Sol atmosphere.</summary>
    public Light DominantAtmosphereLight => dominantAtmosphereLight;

    /// <summary>Base daytime ratio (0.05-0.95). This affects sunlight duration centered around noon, not civil clock speed.</summary>
    public float DayRatio
    {
        get => dayRatio;
        set => dayRatio = Mathf.Clamp(value, 0.05f, 0.95f);
    }

    /// <summary>The effective day ratio after seasonal variation is applied.</summary>
    public float EffectiveDayRatio => GetEffectiveDayRatio();

    /// <summary>Cycle duration in real-time minutes for one full day/night cycle.</summary>
    public float CycleDuration
    {
        get => cycleDurationMinutes;
        set => cycleDurationMinutes = Mathf.Max(0.1f, value);
    }

    /// <summary>Game-world time multiplier. This intentionally does not read or write UnityEngine.Time.timeScale.</summary>
    public float TimeScale
    {
        get => timeScale;
        set => SetTimeScale(value);
    }

    /// <summary>Pause the world clock without changing its configured time multiplier.</summary>
    public bool Paused
    {
        get => paused;
        set => SetPaused(value);
    }

    /// <summary>Current pseudo-volume cloud shader quality.</summary>
    public SolCloudQuality CloudQuality
    {
        get => cloudQuality;
        set => cloudQuality = value;
    }

    /// <summary>World-simulation seconds produced for the current frame.</summary>
    public float WorldDeltaSeconds => worldDeltaSeconds;

    /// <summary>Civil world hours produced for the current frame.</summary>
    public double WorldDeltaHours => worldDeltaHours;

    /// <summary>Bounded-presentation seconds for this frame, independent of the Sol multiplier.</summary>
    public float PresentationDeltaSeconds => presentationDeltaSeconds;

    static void SetCloudKeyword(Material material, string keyword, bool enabled)
    {
        if (enabled) material.EnableKeyword(keyword);
        else material.DisableKeyword(keyword);
    }

    void ApplyCloudQualityKeywords(Material sky)
    {
        if (_cloudKeywordsApplied && _cloudKeywordMaterial == sky
            && _appliedCloudQuality == cloudQuality
            && _appliedProceduralCloudNoise == useProceduralCloudNoise)
            return;

        SetCloudKeyword(sky, "_SOL_CLOUD_LOW", cloudQuality == SolCloudQuality.Low);
        SetCloudKeyword(sky, "_SOL_CLOUD_MEDIUM", cloudQuality == SolCloudQuality.Medium);
        SetCloudKeyword(sky, "_SOL_CLOUD_HIGH", cloudQuality == SolCloudQuality.High);
        SetCloudKeyword(sky, "_SOL_CLOUD_PROCEDURAL", useProceduralCloudNoise);
        _cloudKeywordMaterial = sky;
        _appliedCloudQuality = cloudQuality;
        _appliedProceduralCloudNoise = useProceduralCloudNoise;
        _cloudKeywordsApplied = true;
    }

    void ResolveCloudTextures()
    {
        _resolvedCloudNoiseTexture ??= cloudNoiseTexture != null
            ? cloudNoiseTexture
            : Resources.Load<Texture2D>("SolEnvironment/Sol_CloudNoisePacked");
        _resolvedCloudWeatherMap ??= cloudWeatherMap != null
            ? cloudWeatherMap
            : Resources.Load<Texture2D>("SolEnvironment/Sol_CloudWeatherMap");
    }

    /// <summary>Current lunar phase (0 = new moon, 0.5 = full moon).</summary>
    public float LunarPhase => currentLunarPhase;

    /// <summary>Current moon illumination fraction (0 = dark, 1 = fully lit).</summary>
    public float MoonIllumination => currentMoonIllumination;

    /// <summary>Spring/neap tide signal: 1 at new/full moon and 0 at quarter moons.</summary>
    public float LunarTideFactor => EvaluateLunarTideFactor(currentLunarPhase);

    static float EvaluateLunarTideFactor(float lunarPhase)
        => Mathf.Abs(Mathf.Cos(Mathf.Repeat(lunarPhase, 1f) * Mathf.PI * 2f));

    /// <summary>Current seasonal sunrise in civil clock hours.</summary>
    public float SunriseClockHour => GetSunriseTime(GetEffectiveDayRatio()) * 24f;

    /// <summary>Current seasonal sunset in civil clock hours.</summary>
    public float SunsetClockHour => GetSunsetTime(GetEffectiveDayRatio()) * 24f;

    /// <summary>Solar eclipse intensity (0 = none, 1 = total).</summary>
    public float SolarEclipseStrength => solarEclipseFactor;

    /// <summary>Lunar eclipse intensity (0 = none, 1 = total).</summary>
    public float LunarEclipseStrength => lunarEclipseFactor;

    /// <summary>True when any eclipse is active.</summary>
    public bool IsEclipse => solarEclipseFactor > 0.01f || lunarEclipseFactor > 0.01f;

    /// <summary>Set time to civil noon.</summary>
    public void SetNoon() => SetNormalizedTime(0.5f, this, "SetNoon");

    /// <summary>Set time to civil midnight.</summary>
    public void SetMidnight() => SetNormalizedTime(0f, this, "SetMidnight");

    /// <summary>Skip forward one full day and advance the calendar.</summary>
    public void SkipForwardOneDay() => ApplyTimeChange(new TimeChangeRequest(TimeChangeType.SkipForwardOneDay, source: this));

    /// <summary>Skip backward one full day and rewind the calendar.</summary>
    public void SkipBackwardOneDay() => ApplyTimeChange(new TimeChangeRequest(TimeChangeType.SkipBackwardOneDay, source: this));

    /// <summary>Jump to the computed sunrise, advancing the calendar only if today's sunrise has already passed.</summary>
    public void SkipToNextSunrise() => ApplyTimeChange(new TimeChangeRequest(TimeChangeType.SkipToSunrise, source: this));

    /// <summary>Jump to the computed sunset, advancing the calendar only if today's sunset has already passed.</summary>
    public void SkipToNextSunset() => ApplyTimeChange(new TimeChangeRequest(TimeChangeType.SkipToSunset, source: this));
    #endregion

    // ------------------------------------------------
 // Public API - Tertiary planets
    // ------------------------------------------------

    /// <summary>Number of tertiary planets currently in the sky.</summary>
    public int TertiaryPlanetCount => tertiaryInstances != null ? tertiaryInstances.Count : 0;

    /// <summary>Get the CelestialBody component of a spawned tertiary planet by index.</summary>
    public CelestialBody GetTertiaryPlanet(int index)
    {
        if (tertiaryInstances == null || index < 0 || index >= tertiaryInstances.Count)
            return null;
        return tertiaryInstances[index].body;
    }
}
}
