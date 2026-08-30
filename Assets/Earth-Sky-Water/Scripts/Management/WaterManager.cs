using UnityEngine;
using UnityEngine.Rendering;
using Sol.ToD;
using Sol.Water;

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
/// Per-material properties (colors, waves, foam, etc.) are owned by the
/// assigned water materials. This class only pushes shared global state.
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
    float _referenceRetryTimer;

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

    [Tooltip("Art-directed water disorder driven by weather: geometry, foam, roughness, and drift.")]
    [Range(0f, 1f)]
    public float waterTurbulence;

    [Tooltip("Maximum subtle spring-tide response applied at new and full moon.")]
    [Range(0f, 0.3f)]
    public float lunarResponseStrength = 0.12f;

    // --- Water Level -----------------------------------------------------
    [Header("Water Level")]
    [Tooltip("Global water surface Y. Used by gameplay queries when no WaterVolume is present.")]
    public float waterLevel = 0.0f;

    // --- Night Lighting ----------------------------------------------------
    [Header("Night Lighting")]
    [Tooltip("Light tint pushed to the water at night instead of the sun's " +
             "(stale) sunset color. Scaled by the moon's illumination phase, " +
             "so new-moon nights read darker than full-moon nights.")]
    public Color moonlightColor = new Color(0.45f, 0.55f, 0.75f);

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

    // --- Terrain wetness -------------------------------------------------
    [Header("Terrain Wetness")]
    [Tooltip("How strongly rain wets terrain layers. 0 disables rain wetness.")]
    [Range(0f, 1f)]
    public float terrainRainWetness = 1.0f;

    [Tooltip("How strongly water wets terrain at and below the shoreline.")]
    [Range(0f, 1f)]
    public float terrainWaterWetness = 1.0f;

    [Tooltip("World-space fade distance above the water level for shoreline wetness.")]
    [Min(0.01f)]
    public float terrainWaterWetnessRange = 0.75f;

    [Tooltip("Terrain whose sand layer receives wet-color darkening. Leave null to use the active terrain.")]
    public Terrain terrainWetnessTerrain;

    [Tooltip("Sand TerrainLayer to darken when wet. Leave null to resolve TerrainLayer_Sand by name.")]
    public TerrainLayer terrainWetnessSandLayer;

    [Tooltip("Maximum albedo darkening applied to wet sand only.")]
    [Range(0f, 0.4f)]
    public float terrainSandWetDarkening = 0.14f;

    [Tooltip("Target smoothness for fully wet terrain. Authored values above this target are preserved.")]
    [Range(0f, 0.8f)]
    public float terrainWetSmoothness = 0.48f;

    [Tooltip("Momentary weather-lightning illumination shared with water and atmosphere.")]
    [Range(0f, 1f)]
    public float lightningFlash;

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

    /// <summary>Current spring/neap tide signal from TimeOfDay.</summary>
    public float LunarTideFactor => todManager != null ? todManager.LunarTideFactor : 0f;

    /// <summary>Current illuminated moon fraction used by water lighting.</summary>
    public float MoonIllumination => todManager != null ? todManager.MoonIllumination : 0f;

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
    static readonly int _SolSurfaceWetnessID         = Shader.PropertyToID("_Sol_SurfaceWetness");
    static readonly int _SolRainIntensityID          = Shader.PropertyToID("_Sol_RainIntensity");
    static readonly int _SolRainRoughnessBoostID     = Shader.PropertyToID("_Sol_RainRoughnessBoost");
    static readonly int _SolRainNormalBoostID        = Shader.PropertyToID("_Sol_RainNormalBoost");
    static readonly int _SolRainReflectionDampenID   = Shader.PropertyToID("_Sol_RainReflectionDampen");
    static readonly int _SolTerrainWetnessID         = Shader.PropertyToID("_Sol_TerrainWetness");
    static readonly int _SolTerrainWetSmoothnessID   = Shader.PropertyToID("_Sol_TerrainWetSmoothness");
    static readonly int _SolTerrainSandMaskID        = Shader.PropertyToID("_Sol_TerrainSandMask");
    static readonly int _SolTerrainSandChannelID     = Shader.PropertyToID("_Sol_TerrainSandChannel");
    static readonly int _SolTerrainOriginInvSizeID   = Shader.PropertyToID("_Sol_TerrainOriginInvSize");
    static readonly int _SolLightningFlashID         = Shader.PropertyToID("_Sol_LightningFlash");
    static readonly int _SolGlobalWaterLevelID       = Shader.PropertyToID("_Sol_GlobalWaterLevel");
    static readonly int _SolWaterDynamicsID          = Shader.PropertyToID("_Sol_WaterDynamics");

    // Cached values to avoid redundant global property writes every frame.
    Vector4 _lastWindDirection = new(float.NaN, float.NaN, float.NaN, float.NaN);
    float _lastWindStrength = float.NaN;
    float _lastGlobalWaveSpeedMultiplier = float.NaN;
    float _lastWaterLevel = float.NaN;
    Vector4 _lastWaterDynamics = new(float.NaN, float.NaN, float.NaN, float.NaN);
    float _lastRainIntensity = float.NaN;
    float _lastSurfaceWetness = float.NaN;
    float _lastRainRoughnessBoost = float.NaN;
    float _lastRainNormalBoost = float.NaN;
    float _lastRainReflectionDampen = float.NaN;
    Vector4 _lastTerrainWetness = new(float.NaN, float.NaN, float.NaN, float.NaN);
    float _lastTerrainWetSmoothness = float.NaN;
    Texture _lastTerrainSandMask;
    Vector4 _lastTerrainSandChannel = new(float.NaN, float.NaN, float.NaN, float.NaN);
    Vector4 _lastTerrainOriginInvSize = new(float.NaN, float.NaN, float.NaN, float.NaN);
    Terrain _resolvedTerrainWetnessTerrain;
    TerrainData _resolvedTerrainWetnessData;
    TerrainLayer _resolvedTerrainWetnessSandLayer;
    Texture _terrainSandMask;
    Vector4 _terrainSandChannel;
    Vector4 _terrainOriginInvSize;
    float _lastLightningFlash = float.NaN;
    Vector4 _lastLightDirection = new(float.NaN, float.NaN, float.NaN, float.NaN);
    Color _lastSunColor = new(float.NaN, float.NaN, float.NaN, float.NaN);
    float _lastDayFactor = float.NaN;
    float _lastEclipseFactor = float.NaN;
    SolEnvironmentCoordinator _environmentCoordinator;

    // --- Unity Lifecycle -------------------------------------------------

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[SolWaterManager] Duplicate instance detected. Destroying.", gameObject);
            // Destroy() is illegal in edit mode ([ExecuteAlways]); only destroy while playing.
            if (Application.isPlaying)
                Destroy(gameObject);
            else
                enabled = false;
            return;
        }
        Instance = this;
    }

    void OnEnable()
    {
        if (Instance != null && Instance != this)
        {
            enabled = false;
            return;
        }

        if (Instance == null || Instance == this)
            Instance = this;

        // The environment coordinator restores globals when the last owner
        // releases them. Force the first frame after re-enable to republish
        // this manager's authored state instead of trusting stale cache data.
        InvalidateGlobalCache();

        _environmentCoordinator = SolEnvironmentCoordinator.Resolve(this, createIfMissing: true);
        _environmentCoordinator?.Register(this);
        ResolveTerrainSandMask(force: true);
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;
        _environmentCoordinator?.Unregister(this);
        _environmentCoordinator = null;
    }

    void OnValidate()
    {
        ResolveTerrainSandMask(force: true);
        PushGlobalMotion();
        PushWeather();
        if (todManager != null)
            PushToD();
    }

    void Update()
    {
        _referenceRetryTimer -= Time.unscaledDeltaTime;
        if (todManager == null && _referenceRetryTimer <= 0f)
        {
            todManager = TimeOfDay.ResolveInstance();
            if (todManager == null)
                _referenceRetryTimer = 0.5f;
        }

        // Analytic wave phase uses the canonical Sol world clock.
        float deltaSeconds = todManager != null ? todManager.WorldDeltaSeconds : Time.deltaTime;
        _waveTime += deltaSeconds * globalWaveSpeedMultiplier;

        PushGlobalMotion();
        PushWeather();

        if (todManager != null)
        {
            PushToD();
            UpdateReflectionProbe();
        }
    }

    // --- Global Motion Push ----------------------------------------------

    void InvalidateGlobalCache()
    {
        _lastWindDirection = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        _lastWindStrength = float.NaN;
        _lastGlobalWaveSpeedMultiplier = float.NaN;
        _lastWaterLevel = float.NaN;
        _lastWaterDynamics = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        _lastRainIntensity = float.NaN;
        _lastRainRoughnessBoost = float.NaN;
        _lastRainNormalBoost = float.NaN;
        _lastRainReflectionDampen = float.NaN;
        _lastTerrainWetness = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        _lastTerrainWetSmoothness = float.NaN;
        _lastTerrainSandMask = null;
        _lastTerrainSandChannel = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        _lastTerrainOriginInvSize = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        _lastLightningFlash = float.NaN;
        _lastLightDirection = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        _lastSunColor = new Color(float.NaN, float.NaN, float.NaN, float.NaN);
        _lastDayFactor = float.NaN;
        _lastEclipseFactor = float.NaN;
    }

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

        if (!Water2OwnsSurfaceGlobals && !Mathf.Approximately(_lastWaterLevel, waterLevel))
        {
            Shader.SetGlobalFloat(_SolGlobalWaterLevelID, waterLevel);
            _lastWaterLevel = waterLevel;
        }

        Vector4 waterDynamics = new(
            Mathf.Clamp01(waterTurbulence),
            Mathf.Clamp01(LunarTideFactor),
            Mathf.Clamp01(MoonIllumination),
            Mathf.Clamp(lunarResponseStrength, 0f, 0.3f));
        if (_lastWaterDynamics != waterDynamics)
        {
            Shader.SetGlobalVector(_SolWaterDynamicsID, waterDynamics);
            _lastWaterDynamics = waterDynamics;
        }

        Shader.SetGlobalFloat(_SolWaveTimeID, _waveTime);
    }

    // --- Weather Push ----------------------------------------------------

    /// <summary>
    /// True when a Water 2 <see cref="SolWaterWetness"/> owns the terrain wetness and
    /// water level globals.
    ///
    /// Both components write the same seven globals from their own duplicated inspector
    /// fields -- this one from Update, the Water 2 one from LateUpdate -- so with both
    /// alive the last writer each frame won and the shoreline height and rain response
    /// oscillated between two independent sources. Water 2 wins where it is present; in a
    /// Water 1 only scene nothing changes.
    /// </summary>
    static bool Water2OwnsSurfaceGlobals => SolWaterWetness.Active != null;

    /// <summary>
    /// Forces the contested globals to be rewritten the next time this manager owns them.
    ///
    /// The publish paths are cache-gated on the last value *this* component wrote. While
    /// Water 2 owns the globals those caches keep going stale against what Water 2 wrote,
    /// so if the Water 2 world is torn down at runtime the legacy fields would compare
    /// equal to their own cache and never republish, stranding Water 2's final values.
    /// </summary>
    void InvalidateSurfaceGlobalCache()
    {
        _lastWaterLevel = float.NaN;
        _lastRainIntensity = float.NaN;
        _lastSurfaceWetness = float.NaN;
        _lastTerrainWetness = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        _lastTerrainWetSmoothness = float.NaN;
        _lastTerrainSandMask = null;
        _lastTerrainSandChannel = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        _lastTerrainOriginInvSize = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
    }

    void PushWeather()
    {
        if (!Water2OwnsSurfaceGlobals && !Mathf.Approximately(_lastRainIntensity, rainIntensity))
        {
            Shader.SetGlobalFloat(_SolRainIntensityID, rainIntensity);
            _lastRainIntensity = rainIntensity;
        }

        // Water 1 has no wetness integrator, so it feeds the terrain the instantaneous
        // value. Publishing it anyway is what keeps a Water 1 only scene from reading a
        // never-written global as zero and rendering permanently dry ground.
        if (!Water2OwnsSurfaceGlobals && !Mathf.Approximately(_lastSurfaceWetness, rainIntensity))
        {
            Shader.SetGlobalFloat(_SolSurfaceWetnessID, rainIntensity);
            _lastSurfaceWetness = rainIntensity;
        }

        if (!Mathf.Approximately(_lastRainRoughnessBoost, rainRoughnessBoost))
        {
            Shader.SetGlobalFloat(_SolRainRoughnessBoostID, rainRoughnessBoost);
            _lastRainRoughnessBoost = rainRoughnessBoost;
        }

        if (!Mathf.Approximately(_lastRainNormalBoost, rainNormalBoost))
        {
            Shader.SetGlobalFloat(_SolRainNormalBoostID, rainNormalBoost);
            _lastRainNormalBoost = rainNormalBoost;
        }

        if (!Mathf.Approximately(_lastRainReflectionDampen, rainReflectionDampen))
        {
            Shader.SetGlobalFloat(_SolRainReflectionDampenID, rainReflectionDampen);
            _lastRainReflectionDampen = rainReflectionDampen;
        }

        if (!Water2OwnsSurfaceGlobals)
        {
            Vector4 terrainWetness = new(
                Mathf.Clamp01(terrainRainWetness),
                Mathf.Clamp01(terrainWaterWetness),
                Mathf.Max(0.01f, terrainWaterWetnessRange),
                Mathf.Clamp(terrainSandWetDarkening, 0f, 0.4f));
            if (_lastTerrainWetness != terrainWetness)
            {
                Shader.SetGlobalVector(_SolTerrainWetnessID, terrainWetness);
                _lastTerrainWetness = terrainWetness;
            }

            float wetSmoothness = Mathf.Clamp(terrainWetSmoothness, 0f, 0.8f);
            if (!Mathf.Approximately(_lastTerrainWetSmoothness, wetSmoothness))
            {
                Shader.SetGlobalFloat(_SolTerrainWetSmoothnessID, wetSmoothness);
                _lastTerrainWetSmoothness = wetSmoothness;
            }

            ResolveTerrainSandMask(force: false);
            PushTerrainSandMask();
        }
        else
        {
            InvalidateSurfaceGlobalCache();
        }

        if (!Mathf.Approximately(_lastLightningFlash, lightningFlash))
        {
            Shader.SetGlobalFloat(_SolLightningFlashID, lightningFlash);
            _lastLightningFlash = lightningFlash;
        }
    }

    void ResolveTerrainSandMask(bool force)
    {
        Terrain target = terrainWetnessTerrain != null
            ? terrainWetnessTerrain
            : Terrain.activeTerrain;
        TerrainData data = target != null ? target.terrainData : null;

        if (!force
            && target == _resolvedTerrainWetnessTerrain
            && data == _resolvedTerrainWetnessData
            && terrainWetnessSandLayer == _resolvedTerrainWetnessSandLayer)
            return;

        _resolvedTerrainWetnessTerrain = target;
        _resolvedTerrainWetnessData = data;
        _resolvedTerrainWetnessSandLayer = terrainWetnessSandLayer;
        _terrainSandMask = Texture2D.blackTexture;
        _terrainSandChannel = Vector4.zero;
        _terrainOriginInvSize = Vector4.zero;

        if (target == null || data == null)
            return;

        TerrainLayer[] layers = data.terrainLayers;
        int sandIndex = -1;
        for (int index = 0; index < layers.Length; index++)
        {
            TerrainLayer layer = layers[index];
            bool matches = terrainWetnessSandLayer != null
                ? layer == terrainWetnessSandLayer
                : layer != null && layer.name == "TerrainLayer_Sand";
            if (matches)
            {
                sandIndex = index;
                break;
            }
        }

        int alphamapIndex = sandIndex / 4;
        if (sandIndex < 0 || alphamapIndex >= data.alphamapTextureCount)
            return;

        _terrainSandMask = data.GetAlphamapTexture(alphamapIndex);
        _terrainSandChannel[sandIndex & 3] = 1f;
        Vector3 origin = target.transform.position;
        Vector3 size = data.size;
        _terrainOriginInvSize = new Vector4(
            origin.x,
            origin.z,
            1f / Mathf.Max(size.x, 0.001f),
            1f / Mathf.Max(size.z, 0.001f));
    }

    void PushTerrainSandMask()
    {
        if (_lastTerrainSandMask != _terrainSandMask)
        {
            Shader.SetGlobalTexture(_SolTerrainSandMaskID, _terrainSandMask);
            _lastTerrainSandMask = _terrainSandMask;
        }

        if (_lastTerrainSandChannel != _terrainSandChannel)
        {
            Shader.SetGlobalVector(_SolTerrainSandChannelID, _terrainSandChannel);
            _lastTerrainSandChannel = _terrainSandChannel;
        }

        if (_lastTerrainOriginInvSize != _terrainOriginInvSize)
        {
            Shader.SetGlobalVector(_SolTerrainOriginInvSizeID, _terrainOriginInvSize);
            _lastTerrainOriginInvSize = _terrainOriginInvSize;
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

        // At night TimeOfDay.SunColor holds the stale last-sunset color;
        // blend to moonlight scaled by lunar phase so night water reads
        // moonlit (and darker on new-moon nights).
        Color moonlit = moonlightColor * (0.2f + 0.8f * todManager.MoonIllumination);
        Color sunColor = Color.Lerp(moonlit, todManager.SunColor, tod);
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
