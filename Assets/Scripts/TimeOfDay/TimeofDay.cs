using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sol.ToD
{

/// <summary>
/// Scene-owned authority for Sol environment world time.
/// This component advances the gameplay clock, owns calendar integration, and drives sky/environment visuals.
/// </summary>
[ExecuteAlways]
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

    [Tooltip("Ambient color during the day.")]
    [SerializeField] Color ambientDayColor = new Color(0.5f, 0.55f, 0.6f);

    [Tooltip("Ambient color at night.")]
    [SerializeField] Color ambientNightColor = new Color(0.04f, 0.04f, 0.08f);

    // -- FOG ------------------------------------------
    [Header("-- Fog ----------------------------")]
    [Tooltip("Should this script control fog?")]
    [SerializeField] bool controlFog = true;

    [Tooltip("Keep fog active during the night?")]
    [SerializeField] bool enableNightFog = true;

    [Tooltip("Fog color during the day.")]
    [SerializeField] Color fogDayColor = new Color(0.7f, 0.75f, 0.8f);

    [Tooltip("Fog color at night.")]
    [SerializeField] Color fogNightColor = new Color(0.05f, 0.05f, 0.1f);

    [Tooltip("Fog density during the day.")]
    [Range(0f, 0.05f)]
    [SerializeField] float fogDayDensity = 0.001f;

    [Tooltip("Fog density at night.")]
    [Range(0f, 0.05f)]
    [SerializeField] float fogNightDensity = 0.008f;

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

    [Header("    Clouds")]
 [Tooltip("Cloud noise frequency - lower values = larger clouds.")]
    [Min(0.1f)]
    [SerializeField] float cloudScale = 5f;

    [Tooltip("Cloud wind speed at a 10-minute cycle. Actual speed scales with cycle duration and timeScale.")]
    [Min(0f)]
    [SerializeField] float cloudBaseSpeed = 0.05f;

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
    static readonly int _NadirColorID           = Shader.PropertyToID("_NadirColor");
    static readonly int _ZenithBlendID          = Shader.PropertyToID("_ZenithBlend");
    static readonly int _HorizonBlendID         = Shader.PropertyToID("_HorizonBlend");
    static readonly int _NadirBlendID           = Shader.PropertyToID("_NadirBlend");
    static readonly int _StarIntensityID        = Shader.PropertyToID("_StarIntensity");
    static readonly int _StarPowerID            = Shader.PropertyToID("_StarPower");
    static readonly int _StarHeightID           = Shader.PropertyToID("_StarHeight");
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

    // -- SEASONAL VARIATION ---------------------------
    [Header("-- Seasonal Variation -------------")]
    [Tooltip("Enable seasonal variation of day length based on calendar position within the year.")]
    [SerializeField] bool enableSeasons;

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

    /// <summary>World-space direction toward the sun (unit vector).</summary>
    public Vector3 SunDirection => cachedSunDirection;

    /// <summary>World-space direction toward the moon (unit vector).</summary>
    public Vector3 MoonDirection => cachedMoonDirection;

    /// <summary>0 = night, 1 = zenith. Continuous day/night blend factor.</summary>
    public float DayFactor => cachedDayFactor;

    Calendar calendar;
    #endregion

    #region Service Lifecycle
    /// <summary>Fired whenever normalized gameplay time or the calendar day changes.</summary>
    public event Action<TimeChangeResult> TimeChanged;

    /// <summary>Fired when the visible civil-clock hour bucket changes.</summary>
    public event Action<int, int> HourChanged;

    /// <summary>Fired when the calendar's total day counter changes.</summary>
    public event Action<int, int> DayChanged;

    /// <summary>Fired for explicit skips/sets/rewinds, not ordinary per-frame ticking.</summary>
    public event Action<TimeChangeResult> TimeSkipped;

    /// <summary>Fired when the game-world time multiplier changes.</summary>
    public event Action<float, float> TimeScaleChanged;

    /// <summary>Resolve the active time service without requiring callers to know scene wiring.</summary>
    public static TimeOfDay ResolveInstance()
    {
        if (Instance != null)
            return Instance;

        Instance = FindFirstObjectByType<TimeOfDay>();
        if (Instance != null)
            Instance.ResolveCalendar();

        return Instance;
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
        if (!calendarInitialized && calendar != null)
        {
            calendar.ResetToStart();
            calendarInitialized = true;
        }

        // Lunar synodic period = average calendar month length
        lunarPeriodDays = Calendar != null ? Calendar.AverageMonthLength : 28f;

        SpawnCelestialBodies();

        // Set render-settings modes once (they never change at runtime)
        if (controlAmbient) RenderSettings.ambientMode = AmbientMode.Flat;
        if (controlFog) RenderSettings.fogMode = FogMode.ExponentialSquared;
    }

    void OnDestroy()
    {
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
 // In edit mode, skip time advancement and celestial body spawning -
        // just drive the skybox / environment so changes preview in real time.
        if (!Application.isPlaying)
        {
            UpdateEditModePreview();
            return;
        }

        if (!Paused)
        {
            float cycleDurationSeconds = cycleDurationMinutes * 60f;
            float dayProgress = (timeScale * Time.deltaTime) / cycleDurationSeconds;
            ApplyNormalizedDelta(
                dayProgress,
                TimeChangeRequest.AdvanceHours(dayProgress * 24f, this, "Tick"),
                fireSkipped: false);
        }

        float effectiveDayRatio = GetEffectiveDayRatio();
        float daysFraction = (Calendar != null ? Calendar.TotalDaysElapsed : 0) + timeOfDay;

        UpdateSun(effectiveDayRatio);
        UpdateMoon(daysFraction);
        UpdateEclipses();
        UpdateCelestialBodies();
        UpdateTertiaryPlanets(daysFraction);
        UpdateEnvironment();
    }

    /// <summary>
    /// Lightweight preview path for edit mode. Computes sun direction and
    /// day factor from timeOfDay so the skybox, ambient, and fog update
    /// in the Scene view without spawning any prefabs.
    /// </summary>
    void UpdateEditModePreview()
    {
        float effectiveDayRatio = GetEffectiveDayRatio();

        // Reconstruct sun angle & dayFactor the same way UpdateSun does.
        float sunAngle = ComputeSunAngle(timeOfDay, effectiveDayRatio);

        Quaternion sunRot = Quaternion.AngleAxis(sunAngle, Vector3.right);
        float dot = Vector3.Dot(sunRot * Vector3.forward, Vector3.down);
        const float horizonThreshold = -0.05f;
        cachedDayFactor   = Mathf.InverseLerp(horizonThreshold, 1f, dot);
        cachedSunDirection = -(sunRot * Vector3.forward);

        // Approximate moon direction for edit-mode preview (assumes quarter phase).
        Quaternion moonRot = Quaternion.AngleAxis(sunAngle + initialLunarPhase * 360f, Vector3.right);
        cachedMoonDirection = -(moonRot * Vector3.forward);

        // Reset eclipse factors (no eclipse simulation in edit mode)
        solarEclipseFactor = 0f;
        lunarEclipseFactor = 0f;

        UpdateEnvironment();
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

        // Season flips discretely at year start and at the midpoint.
        float seasonOffset = resolvedCalendar.SeasonSign * seasonalDayVariation;
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
                moonLight.shadows = LightShadows.Soft;
                moonLight.shadowStrength = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(moonElevation * currentMoonIllumination * 10f));
            }
        }
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
        float eclipseEnv = solarEclipseFactor;

        if (controlAmbient)
        {
            Color ambient = Color.Lerp(ambientNightColor, ambientDayColor, cachedDayFactor);
            if (eclipseEnv > 0f)
                ambient = Color.Lerp(ambient, eclipseAmbientColor, eclipseEnv);
            RenderSettings.ambientLight = ambient;
        }

        if (controlFog)
        {
            bool isNight = cachedDayFactor < 0.1f;
            RenderSettings.fog = isNight ? enableNightFog : true;
            Color fogCol = Color.Lerp(fogNightColor, fogDayColor, cachedDayFactor);
            float fogDen = Mathf.Lerp(fogNightDensity, fogDayDensity, cachedDayFactor);
            if (eclipseEnv > 0f)
                fogCol = Color.Lerp(fogCol, eclipseFogColor, eclipseEnv);
            RenderSettings.fogColor = fogCol;
            RenderSettings.fogDensity = fogDen;
        }

        if (controlSkybox)
        {
            Material sky = RenderSettings.skybox;
            if (sky != null)
            {
                float df = cachedDayFactor;

                // Zenith: night ? day, with eclipse overlay
                Color zenith = Color.Lerp(skyZenithNight, skyZenithDay, df);
                if (eclipseEnv > 0f)
                    zenith = Color.Lerp(zenith, skyZenithEclipse, eclipseEnv);

                // Horizon: blend sunrise/sunset tint near the horizon transition.
                bool isMorning = timeOfDay < 0.5f;
                Color warmHorizon = isMorning ? skyHorizonSunrise : skyHorizonSunset;
                Color horizon;
                if (df < 0.3f)
                    horizon = Color.Lerp(skyHorizonNight, warmHorizon, df / 0.3f);
                else if (df < 0.7f)
                    horizon = Color.Lerp(warmHorizon, skyHorizonDay, (df - 0.3f) / 0.4f);
                else
                    horizon = skyHorizonDay;
                if (eclipseEnv > 0f)
                    horizon = Color.Lerp(horizon, skyHorizonEclipse, eclipseEnv);

                // Nadir: simple night ? day
                Color nadir = Color.Lerp(skyNadirNight, skyNadirDay, df);

                // Stars: visible at night, fade out during the day
                float starIntensity = Mathf.Lerp(starIntensityNight, 0f, df);

                // Blends: interpolate day/night sharpness
                float zenithBlend  = Mathf.Lerp(skyZenithBlendNight,  skyZenithBlendDay,  df);
                float horizonBlend = Mathf.Lerp(skyHorizonBlendNight, skyHorizonBlendDay, df);
                float nadirBlend   = Mathf.Lerp(skyNadirBlendNight,   skyNadirBlendDay,   df);

                // -- Sky gradient --
                sky.SetColor(_ZenithColorID,    zenith);
                sky.SetColor(_HorizonColorID,   horizon);
                sky.SetColor(_NadirColorID,     nadir);
                sky.SetFloat(_ZenithBlendID,    zenithBlend);
                sky.SetFloat(_HorizonBlendID,   horizonBlend);
                sky.SetFloat(_NadirBlendID,     nadirBlend);
                sky.SetFloat(_StarIntensityID,  starIntensity);
                sky.SetFloat(_StarPowerID,      starPower);
                sky.SetFloat(_StarHeightID,     starHeight);

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

                Color cloudLit;
                if (df < 0.3f)
                    cloudLit = Color.Lerp(cloudColorNight, cloudColorSunset, df / 0.3f);
                else if (df < 0.7f)
                    cloudLit = Color.Lerp(cloudColorSunset, cloudColorDay, (df - 0.3f) / 0.4f);
                else
                    cloudLit = cloudColorDay;

                Color cloudShadow = Color.Lerp(cloudShadowNight, cloudShadowDay, df);

                float effectiveCloudSpeed = cloudBaseSpeed
                                          * (10f / Mathf.Max(cycleDurationMinutes, 0.1f))
                                          * timeScale;

                sky.SetFloat(_CloudScaleID,        cloudScale);
                sky.SetFloat(_CloudSpeedID,        effectiveCloudSpeed);
                sky.SetFloat(_CloudCoverageID,     cloudCoverage);
                sky.SetFloat(_CloudDensityID,      cloudDensity);
                sky.SetFloat(_CloudHeightID,       cloudHeight);
                sky.SetColor(_CloudColorID,        cloudLit);
                sky.SetColor(_CloudShadowColorID,  cloudShadow);

                // -- Atmosphere --
                float glowIntensity = Mathf.Lerp(sunGlowIntensityNight, sunGlowIntensityDay, df);
                float hazeIntensity = Mathf.Lerp(hazeIntensityNight, hazeIntensityDay, df);

                sky.SetFloat(_SunGlowFalloffID,    sunGlowFalloff);
                sky.SetFloat(_SunGlowIntensityID,  glowIntensity);
                sky.SetFloat(_HazeIntensityID,     hazeIntensity);
            }
        }
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
                return ApplyNormalizedDelta(request.Value / 24f, request, fireSkipped: true);
            case TimeChangeType.RewindHours:
                return ApplyNormalizedDelta(-request.Value / 24f, request, fireSkipped: true);
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
                return ApplyCalendarDelta(1, request, fireSkipped: true);
            case TimeChangeType.SkipBackwardOneDay:
                return ApplyCalendarDelta(-1, request, fireSkipped: true);
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
        if (!Mathf.Approximately(oldScale, timeScale))
            TimeScaleChanged?.Invoke(oldScale, timeScale);
    }

    /// <summary>Pause or resume the world clock without changing its configured time multiplier.</summary>
    public void SetPaused(bool isPaused)
    {
        paused = isPaused;
    }

    /// <summary>Restore a saved time/calendar snapshot as one service-level operation.</summary>
    public TimeChangeResult RestoreTimeSnapshot(float normalizedTime, int day, int month, int year, int totalDays, UnityEngine.Object source = null, string reason = "SaveLoad")
    {
        float oldNormalized = timeOfDay;
        float oldClock = ClockHour;
        int oldDays = TotalDaysElapsed;

        Calendar resolvedCalendar = ResolveCalendar();
        if (resolvedCalendar != null)
        {
            resolvedCalendar.SetDate(day, month, year, totalDays);
            calendarInitialized = true;
        }

        timeOfDay = Mathf.Repeat(normalizedTime, 1f);
        TimeChangeRequest request = TimeChangeRequest.SetNormalizedTime(normalizedTime, source, reason);
        TimeChangeResult result = CreateTimeChangeResult(request, oldNormalized, oldClock, oldDays);
        PublishTimeResult(result, fireSkipped: false);
        return result;
    }

    private TimeChangeResult ApplyNormalizedDelta(float normalizedDelta, TimeChangeRequest request, bool fireSkipped)
    {
        float oldNormalized = timeOfDay;
        float oldClock = ClockHour;
        int oldDays = TotalDaysElapsed;

        float newTime = timeOfDay + normalizedDelta;
        int daysCrossed = Mathf.FloorToInt(newTime);
        timeOfDay = newTime - daysCrossed;
        ApplyCalendarDeltaOnly(daysCrossed);

        TimeChangeResult result = CreateTimeChangeResult(request, oldNormalized, oldClock, oldDays);
        PublishTimeResult(result, fireSkipped);
        return result;
    }

    private TimeChangeResult SetNormalizedTimeInternal(float normalizedTime, TimeChangeRequest request, bool fireSkipped)
    {
        float oldNormalized = timeOfDay;
        float oldClock = ClockHour;
        int oldDays = TotalDaysElapsed;

        timeOfDay = Mathf.Repeat(normalizedTime, 1f);

        TimeChangeResult result = CreateTimeChangeResult(request, oldNormalized, oldClock, oldDays);
        PublishTimeResult(result, fireSkipped);
        return result;
    }

    private TimeChangeResult ApplyCalendarDelta(int dayDelta, TimeChangeRequest request, bool fireSkipped)
    {
        float oldNormalized = timeOfDay;
        float oldClock = ClockHour;
        int oldDays = TotalDaysElapsed;

        ApplyCalendarDeltaOnly(dayDelta);

        TimeChangeResult result = CreateTimeChangeResult(request, oldNormalized, oldClock, oldDays);
        PublishTimeResult(result, fireSkipped);
        return result;
    }

    private TimeChangeResult SkipToSunriseInternal(TimeChangeRequest request)
    {
        float oldNormalized = timeOfDay;
        float oldClock = ClockHour;
        int oldDays = TotalDaysElapsed;

        float sunriseTime = GetSunriseTime(GetEffectiveDayRatio());

        if (timeOfDay >= sunriseTime)
            ApplyCalendarDeltaOnly(1);
        timeOfDay = sunriseTime;

        TimeChangeResult result = CreateTimeChangeResult(request, oldNormalized, oldClock, oldDays);
        PublishTimeResult(result, fireSkipped: true);
        return result;
    }

    private TimeChangeResult SkipToSunsetInternal(TimeChangeRequest request)
    {
        float oldNormalized = timeOfDay;
        float oldClock = ClockHour;
        int oldDays = TotalDaysElapsed;
        float sunsetTime = GetSunsetTime(GetEffectiveDayRatio());

        if (timeOfDay >= sunsetTime)
            ApplyCalendarDeltaOnly(1);
        timeOfDay = sunsetTime;

        TimeChangeResult result = CreateTimeChangeResult(request, oldNormalized, oldClock, oldDays);
        PublishTimeResult(result, fireSkipped: true);
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

    private TimeChangeResult CreateTimeChangeResult(TimeChangeRequest request, float oldNormalized, float oldClock, int oldDays)
    {
        float newClock = ClockHour;
        int newDays = TotalDaysElapsed;
        bool changed = !Mathf.Approximately(oldNormalized, timeOfDay) || oldDays != newDays;
        return new TimeChangeResult(request, oldNormalized, timeOfDay, oldClock, newClock, oldDays, newDays, changed);
    }

    private TimeChangeResult CreateNoTimeChangeResult(TimeChangeRequest request)
        => new(request, timeOfDay, timeOfDay, ClockHour, ClockHour, TotalDaysElapsed, TotalDaysElapsed, changed: false);

    private void PublishTimeResult(TimeChangeResult result, bool fireSkipped)
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

        if (fireSkipped)
            TimeSkipped?.Invoke(result);
    }
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

    /// <summary>Current calendar total days elapsed, or 0 if no calendar is present.</summary>
    public int TotalDaysElapsed => Calendar != null ? Calendar.TotalDaysElapsed : 0;

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

    /// <summary>Current lunar phase (0 = new moon, 0.5 = full moon).</summary>
    public float LunarPhase => currentLunarPhase;

    /// <summary>Current moon illumination fraction (0 = dark, 1 = fully lit).</summary>
    public float MoonIllumination => currentMoonIllumination;

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

