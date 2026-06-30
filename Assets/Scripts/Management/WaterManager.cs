using UnityEngine;
using UnityEngine.Rendering;
using Sol.ToD;

/// <summary>
/// ---------------------------------------------------------------------------
/// SOL WATER MANAGER - GLOBAL WATER MANAGER
/// ---------------------------------------------------------------------------
///
/// The runtime brain of the Sol.Water framework. Single singleton that
/// ensures ALL water bodies in the scene behave as one unified system.
///
/// Responsibilities:
///   1. TIME OF DAY SYNCING
///      - Reads sun direction, color, DayFactor from TimeOfDay
///      - Pushes _Sol_* globals so every water material responds
///
///   2. GLOBAL MOTION
///      - Wind direction + strength ? influences wave and normal scroll
///      - Global wave speed multiplier
///      - Centralized time accumulator so all water shares the same phase
///
///   3. WEATHER INTEGRATION
///      - Rain intensity modifies roughness, ripple normals, reflection clarity
///      - External systems (weather controller, etc.) set RainIntensity
///
///   4. REFLECTION PROBE BAKING
///      - Re-bakes once per ToD period change
///
///   5. WATER LEVEL
///      - Single authoritative water level for gameplay queries
///
/// Per-material properties (colors, waves, foam, etc.) are owned exclusively
/// by WaterProfile + WaterProfileApplier. This class never touches them.
///
/// Any system can access via SolWaterManager.Instance.
/// ---------------------------------------------------------------------------
///
[ExecuteAlways]
public class SolWaterManager : MonoBehaviour
{
    // --- Singleton -------------------------------------------------------
    public static SolWaterManager Instance { get; private set; }

    // --- References ------------------------------------------------------
    [Header("References")]
    public TimeOfDay todManager;

    // --- Global Motion ---------------------------------------------------
    [Header("Global Motion")]
    [Tooltip("Multiplier on all wave speeds. 0 = frozen, 1 = normal, 2 = double.")]
    [Range(0f, 3f)]
    public float globalWaveSpeedMultiplier = 1.0f;

    [Tooltip("World-space wind direction (XZ). Influences wave scroll and normal directions.")]
    public Vector3 windDirection = new Vector3(1f, 0f, 0.5f);

    [Tooltip("Wind strength multiplier. Affects wave amplitude scaling.")]
    [Range(0f, 3f)]
    public float windStrength = 1.0f;

    // --- Water Level -----------------------------------------------------
    [Header("Water Level")]
    [Tooltip("Global water surface Y. Used by gameplay queries when no WaterVolume is present.")]
    public float waterLevel = 0.0f;

    // --- Weather ---------------------------------------------------------
    [Header("Weather Integration")]
    [Tooltip("Rain intensity (0 = dry, 1 = heavy rain). Increases roughness and ripple activity.")]
    [Range(0f, 1f)]
    public float rainIntensity = 0.0f;

    [Tooltip("How much rain boosts surface roughness.")]
    [Range(0f, 0.5f)]
    public float rainRoughnessBoost = 0.15f;

    [Tooltip("How much rain boosts normal detail strength.")]
    [Range(0f, 2f)]
    public float rainNormalBoost = 0.8f;

    [Tooltip("How much rain reduces reflection clarity (0 = no reduction).")]
    [Range(0f, 1f)]
    public float rainReflectionDampen = 0.3f;

    // --- Reflection Probe ------------------------------------------------
    [Header("Reflection Probe")]
    [Tooltip("Realtime reflection probe to bake when the time period changes. " +
             "Leave null to skip probe baking.")]
    public ReflectionProbe reflectionProbe;

    [Tooltip("Hour boundaries for Morning / Day / Afternoon / Night. " +
             "Probe re-bakes once each time the period changes.")]
    public float morningStart   = 5f;
    public float dayStart       = 9f;
    public float afternoonStart = 14f;
    public float nightStart     = 20f;

    // --- Public read-only state ------------------------------------------

    /// <summary>Accumulated wave time, affected by globalWaveSpeedMultiplier.</summary>
    public float WaveTime => _waveTime;

    /// <summary>Normalised wind direction (XZ plane).</summary>
    public Vector3 WindDirectionNormalized =>
        windDirection.sqrMagnitude > 0.001f ? windDirection.normalized : Vector3.right;

    // --- Private ---------------------------------------------------------

    float _waveTime;

    // Reflection probe bake tracking
    enum TimePeriod { Night, Morning, Day, Afternoon }
    TimePeriod _lastPeriod = (TimePeriod)(-1);
    int _pendingBakeID = -1;

    // Global property IDs
    static readonly int _SolSunDirectionID           = Shader.PropertyToID("_Sol_SunDirection");
    static readonly int _SolSunColorID               = Shader.PropertyToID("_Sol_SunColor");
    static readonly int _SolDayFactorID              = Shader.PropertyToID("_Sol_DayFactor");
    static readonly int _SolEclipseFactorID          = Shader.PropertyToID("_Sol_EclipseFactor");
    static readonly int _SolWindDirectionID          = Shader.PropertyToID("_Sol_WindDirection");
    static readonly int _SolWindStrengthID           = Shader.PropertyToID("_Sol_WindStrength");
    static readonly int _SolGlobalWaveSpeedMulID     = Shader.PropertyToID("_Sol_GlobalWaveSpeedMul");
    static readonly int _SolWaveTimeID               = Shader.PropertyToID("_Sol_WaveTime");
    static readonly int _SolRainIntensityID          = Shader.PropertyToID("_Sol_RainIntensity");
    static readonly int _SolGlobalWaterLevelID       = Shader.PropertyToID("_Sol_GlobalWaterLevel");

    // Cached values to avoid redundant global property writes every frame.
    Vector4 _lastWindDirection = new(float.NaN, float.NaN, float.NaN, float.NaN);
    float _lastWindStrength = float.NaN;
    float _lastGlobalWaveSpeedMultiplier = float.NaN;
    float _lastWaterLevel = float.NaN;
    float _lastRainIntensity = float.NaN;
    Vector4 _lastLightDirection = new(float.NaN, float.NaN, float.NaN, float.NaN);
    Color _lastSunColor = new(float.NaN, float.NaN, float.NaN, float.NaN);
    float _lastDayFactor = float.NaN;
    float _lastEclipseFactor = float.NaN;

    // --- Unity Lifecycle -------------------------------------------------

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[SolWaterManager] Duplicate instance detected. Destroying.", gameObject);
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void OnEnable()
    {
        if (Instance == null || Instance == this)
            Instance = this;
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;
    }

    void OnValidate()
    {
        PushGlobalMotion();
        PushWeather();
        if (todManager != null)
            PushToD();
    }

    void Update()
    {
        // Accumulate wave time using the global speed multiplier
        _waveTime += Time.deltaTime * globalWaveSpeedMultiplier;

        PushGlobalMotion();
        PushWeather();

        if (todManager == null)
            todManager = TimeOfDay.ResolveInstance();

        if (todManager != null)
        {
            PushToD();
            UpdateReflectionProbe();
        }
    }

    // --- Global Motion Push ----------------------------------------------

    void PushGlobalMotion()
    {
        Vector3 windNorm = WindDirectionNormalized;
        Vector4 windDirection4 = new(windNorm.x, windNorm.y, windNorm.z, 0f);

        if (_lastWindDirection != windDirection4)
        {
            Shader.SetGlobalVector(_SolWindDirectionID, windDirection4);
            _lastWindDirection = windDirection4;
        }

        if (!Mathf.Approximately(_lastWindStrength, windStrength))
        {
            Shader.SetGlobalFloat(_SolWindStrengthID, windStrength);
            _lastWindStrength = windStrength;
        }

        if (!Mathf.Approximately(_lastGlobalWaveSpeedMultiplier, globalWaveSpeedMultiplier))
        {
            Shader.SetGlobalFloat(_SolGlobalWaveSpeedMulID, globalWaveSpeedMultiplier);
            _lastGlobalWaveSpeedMultiplier = globalWaveSpeedMultiplier;
        }

        if (!Mathf.Approximately(_lastWaterLevel, waterLevel))
        {
            Shader.SetGlobalFloat(_SolGlobalWaterLevelID, waterLevel);
            _lastWaterLevel = waterLevel;
        }

        Shader.SetGlobalFloat(_SolWaveTimeID, _waveTime);
    }

    // --- Weather Push ----------------------------------------------------

    void PushWeather()
    {
        if (!Mathf.Approximately(_lastRainIntensity, rainIntensity))
        {
            Shader.SetGlobalFloat(_SolRainIntensityID, rainIntensity);
            _lastRainIntensity = rainIntensity;
        }
    }

    // --- ToD ? Global Shader Push ----------------------------------------

    void PushToD()
    {
        float tod = todManager.DayFactor;

        // Dominant light direction: blend sun?moon across day/night
        Vector3 sunDir  = todManager.SunDirection;
        Vector3 moonDir = todManager.MoonDirection;
        Vector3 lightDir = Vector3.Slerp(moonDir, sunDir, tod);
        Color sunColor = todManager.SunColor;
        float eclipse = todManager.SolarEclipseStrength;

        Vector4 lightDir4 = new(lightDir.x, lightDir.y, lightDir.z, 0f);
        if (_lastLightDirection != lightDir4)
        {
            Shader.SetGlobalVector(_SolSunDirectionID, lightDir4);
            _lastLightDirection = lightDir4;
        }

        if (_lastSunColor != sunColor)
        {
            Shader.SetGlobalColor(_SolSunColorID, sunColor);
            _lastSunColor = sunColor;
        }

        if (!Mathf.Approximately(_lastDayFactor, tod))
        {
            Shader.SetGlobalFloat(_SolDayFactorID, tod);
            _lastDayFactor = tod;
        }

        if (!Mathf.Approximately(_lastEclipseFactor, eclipse))
        {
            Shader.SetGlobalFloat(_SolEclipseFactorID, eclipse);
            _lastEclipseFactor = eclipse;
        }
    }

    // --- Reflection Probe Baking -----------------------------------------

    TimePeriod GetCurrentPeriod(float hour)
    {
        if (hour >= nightStart || hour < morningStart) return TimePeriod.Night;
        if (hour < dayStart)       return TimePeriod.Morning;
        if (hour < afternoonStart) return TimePeriod.Day;
        return TimePeriod.Afternoon;
    }

    void UpdateReflectionProbe()
    {
        if (reflectionProbe == null || todManager == null) return;

        if (_pendingBakeID >= 0 && reflectionProbe.IsFinishedRendering(_pendingBakeID))
            _pendingBakeID = -1;

        TimePeriod current = GetCurrentPeriod(todManager.ClockHour);
        if (current == _lastPeriod) return;

        _lastPeriod = current;

        if (_pendingBakeID < 0)
            _pendingBakeID = reflectionProbe.RenderProbe();
    }
}
