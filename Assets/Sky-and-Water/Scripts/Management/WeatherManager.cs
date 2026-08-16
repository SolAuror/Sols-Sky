using System;
using UnityEngine;
using Sol.ToD;

/// <summary>
/// ---------------------------------------------------------------------------
/// SOL WEATHER MANAGER - WEATHER DIRECTOR
/// ---------------------------------------------------------------------------
///
/// State machine that orchestrates the existing weather hooks into a living
/// system. Cycles between weather profiles (Clear / Overcast / Rain / Storm
/// by default), holds each for a random number of GAME HOURS, and blends
/// smoothly between them.
///
/// Drives:
///   - TimeOfDay.WeatherCloudiness / WeatherFogBoost / WeatherDim /
///     WeatherLightningFlash  (sky, clouds, fog, ambient, sun/moon dimming)
///   - SolWaterManager.rainIntensity / windStrength /
///     globalWaveSpeedMultiplier / windDirection (optional slow wander)
///
/// While this component is enabled it OWNS those SolWaterManager fields;
/// disable driveWind / driveWaves to keep authoring them by hand.
///
/// Durations use civil world hours; lightning and all visual motion use the
/// canonical Sol world-seconds clock. Both respect Unity time scale, the Sol
/// multiplier, and world pause exactly once.
///
/// Public API: SetWeather(index/name, instant), NextWeather(), WeatherChanged
/// event, CurrentProfile / TargetProfile / IsTransitioning.
/// ---------------------------------------------------------------------------
/// </summary>
[DefaultExecutionOrder(-500)]
public class SolWeatherManager : MonoBehaviour
{
    // --- Singleton -------------------------------------------------------
    public static SolWeatherManager Instance { get; private set; }

    // --- Profile ----------------------------------------------------------
    [Serializable]
    public class WeatherProfile
    {
        public string name = "Clear";

        [Tooltip("0 = leave the authored ToD cloud settings, 1 = fully overcast.")]
        [Range(0f, 1f)] public float cloudiness = 0f;

        [Tooltip("Breakup applied to cloud edges (0 = soft masses, 1 = strongly eroded).")]
        [Range(0f, 1f)] public float cloudErosion = 0.35f;

        [Tooltip("Rain intensity pushed to SolWaterManager (0 = dry, 1 = downpour).")]
        [Range(0f, 1f)] public float rainIntensity = 0f;

        [Tooltip("Wind strength pushed to SolWaterManager.")]
        [Range(0f, 3f)] public float windStrength = 1f;

        [Tooltip("Additional fog density multiplier (0 = none, 1 = double).")]
        [Range(0f, 2f)] public float fogBoost = 0f;

        [Tooltip("Moves atmosphere density toward the low-mist height profile without adding density.")]
        [Range(0f, 1f)] public float mistiness = 0f;

        [Tooltip("Additional sky-wide atmosphere obscuration. Horizon fog remains independently depth driven.")]
        [Range(0f, 1f)] public float skyObscuration = 0f;

        [Tooltip("Directional atmosphere light-scattering multiplier.")]
        [Range(0f, 2f)] public float lightScattering = 0.4f;

        [Tooltip("Storm darkening applied to sun/moon, ambient, sky, and clouds.")]
        [Range(0f, 1f)] public float dim = 0f;

        [Tooltip("Global wave speed multiplier pushed to SolWaterManager.")]
        [Range(0f, 3f)] public float waveSpeedMultiplier = 1f;

        [Tooltip("Art-directed water disorder: wave detail, steepness, swell, foam, roughness, and drift.")]
        [Range(0f, 1f)] public float waterTurbulence = 0f;

        [Tooltip("Enable random lightning flashes during this weather.")]
        public bool lightning = false;

        [Tooltip("Peak effective lightning flash contributed by this profile.")]
        [Range(0f, 1f)] public float lightningIntensity = 0.35f;

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

    // --- References -------------------------------------------------------
    [Header("References")]
    public TimeOfDay todManager;
    public SolWaterManager waterManager;

    // --- Profiles ---------------------------------------------------------
    [Header("Profiles")]
    public WeatherProfile[] profiles =
    {
        // Clear preserves the authored fair-weather cloud baseline. Its higher
        // erosion breaks those clouds into smaller, crisp shapes while calm
        // wind and sub-unity wave speed keep the water from reading stormy.
        new WeatherProfile { name = "Clear",    cloudiness = 0.00f, cloudErosion = 0.52f, rainIntensity = 0.00f, windStrength = 0.35f, fogBoost = 0.00f, mistiness = 0.00f, skyObscuration = 0.00f, lightScattering = 0.55f, dim = 0.00f, waveSpeedMultiplier = 0.85f, waterTurbulence = 0.05f, lightningIntensity = 0.00f, weight = 4f, springWeightMultiplier = 1.05f, summerWeightMultiplier = 1.45f, autumnWeightMultiplier = 0.85f, winterWeightMultiplier = 0.65f },

        // Overcast is a broad, comparatively soft cloud deck. It dims direct
        // light more than it adds fog, preserving depth and readable silhouettes.
        new WeatherProfile { name = "Overcast", cloudiness = 0.68f, cloudErosion = 0.22f, rainIntensity = 0.00f, windStrength = 0.75f, fogBoost = 0.03f, mistiness = 0.06f, skyObscuration = 0.04f, lightScattering = 0.30f, dim = 0.28f, waveSpeedMultiplier = 1.00f, waterTurbulence = 0.18f, lightningIntensity = 0.00f, weight = 3f, springWeightMultiplier = 0.95f, summerWeightMultiplier = 0.70f, autumnWeightMultiplier = 1.25f, winterWeightMultiplier = 1.35f },

        // Rain keeps visible overhead structure and useful middle-distance
        // visibility. Wetness, rain VFX, and water response carry the state.
        new WeatherProfile { name = "Rain",     cloudiness = 0.84f, cloudErosion = 0.46f, rainIntensity = 0.65f, windStrength = 1.35f, fogBoost = 0.12f, mistiness = 0.65f, skyObscuration = 0.12f, lightScattering = 0.24f, dim = 0.44f, waveSpeedMultiplier = 1.25f, waterTurbulence = 0.48f, lightningIntensity = 0.00f, weight = 2f, springWeightMultiplier = 1.35f, summerWeightMultiplier = 0.75f, autumnWeightMultiplier = 1.05f, winterWeightMultiplier = 1.25f },

        // Storm is dark and turbulent rather than a bright fog whiteout. Keep
        // obscuration below full coverage so pseudo-volume cloud detail survives.
        new WeatherProfile { name = "Storm",    cloudiness = 0.97f, cloudErosion = 0.64f, rainIntensity = 0.95f, windStrength = 2.65f, fogBoost = 0.30f, mistiness = 0.82f, skyObscuration = 0.36f, lightScattering = 0.16f, dim = 0.78f, waveSpeedMultiplier = 1.85f, waterTurbulence = 1.00f, lightning = true, lightningIntensity = 0.28f, weight = 1f, springWeightMultiplier = 0.75f, summerWeightMultiplier = 0.55f, autumnWeightMultiplier = 1.35f, winterWeightMultiplier = 1.15f },
    };

    [Header("Seasonal Climate")]
    [Tooltip("Stable seed combined with WorldDayIndex for deterministic daily climate.")]
    public int climateSeed = 1427;

    [Tooltip("Spring min/max deterministic daily fog tendency.")]
    public Vector2 springDailyFogRange = new(0.02f, 0.30f);
    [Tooltip("Summer min/max deterministic daily fog tendency.")]
    public Vector2 summerDailyFogRange = new(0.00f, 0.12f);
    [Tooltip("Autumn min/max deterministic daily fog tendency.")]
    public Vector2 autumnDailyFogRange = new(0.05f, 0.40f);
    [Tooltip("Winter min/max deterministic daily fog tendency.")]
    public Vector2 winterDailyFogRange = new(0.12f, 0.65f);

    [SerializeField, Min(0.01f), Tooltip("Unity-scaled seconds for date/time climate presentation changes.")]
    float climateTransitionDurationSeconds = 5f;

    // --- Auto Cycling -----------------------------------------------------
    [Header("Auto Cycling")]
    [Tooltip("Automatically pick the next weather when the current one expires.")]
    public bool autoCycle = true;

    [Tooltip("Min/max hold duration of a weather state, in game hours.")]
    public Vector2 durationHoursRange = new(6f, 18f);

    [Tooltip("Unity-scaled seconds used to present a weather change. Sol time multipliers do not shorten this blend.")]
    [Min(0.01f)]
    public float transitionDurationSeconds = 3f;

    [Obsolete("Visible weather transitions now use transitionDurationSeconds.")]
    [HideInInspector]
    public float transitionHours = 0.75f;

    [Tooltip("Never pick the same profile twice in a row when alternatives exist.")]
    public bool avoidRepeat = true;

    // --- Wind -------------------------------------------------------------
    [Header("Wind")]
    [Tooltip("Let the weather system own SolWaterManager.windDirection / windStrength.")]
    public bool driveWind = true;

    [Tooltip("Slow random wind direction wander, in degrees per game hour.")]
    [Range(0f, 90f)]
    public float windWanderDegPerHour = 8f;

    [Tooltip("Let the weather system own SolWaterManager.globalWaveSpeedMultiplier.")]
    public bool driveWaves = true;

    // --- Lightning --------------------------------------------------------
    [Header("Lightning")]
    [Tooltip("Min/max Sol world seconds between strikes while lightning weather is active.")]
    public Vector2 strikeIntervalSeconds = new(4f, 15f);

    [Tooltip("Chance that a strike is followed by a quick echo flash.")]
    [Range(0f, 1f)]
    public float doubleStrikeChance = 0.35f;

    // --- Events / Public state ---------------------------------------------
    /// <summary>Fired when a transition toward a new profile begins.</summary>
    public event Action<WeatherProfile> WeatherChanged;

    /// <summary>Fired when the effective blended state changes.</summary>
    public event Action<SolWeatherState> WeatherStateChanged;

    /// <summary>Fired once when a visible lightning strike begins.</summary>
    public event Action LightningTriggered;

    /// <summary>Profile the system is currently blending toward (or holding).</summary>
    public WeatherProfile TargetProfile =>
        profiles != null && profiles.Length > 0 ? profiles[_targetIndex] : null;

    /// <summary>True while blending between two profiles.</summary>
    public bool IsTransitioning => _blend < 1f;

    /// <summary>Shared progress for every presented weather channel.</summary>
    public float TransitionProgress => Mathf.Clamp01(_blend);

    /// <summary>Logical weather target selected by chronology before presentation smoothing.</summary>
    public SolWeatherState TargetState { get; private set; }

    /// <summary>Effective rain intensity after blending (for audio/VFX hooks).</summary>
    public float CurrentRainIntensity { get; private set; }

    /// <summary>Effective storm darkening after blending.</summary>
    public float CurrentDim { get; private set; }

    /// <summary>Effective immutable weather values for environment consumers.</summary>
    public SolWeatherState CurrentState { get; private set; }

    /// <summary>Deterministic fog tendency assigned to the current world date.</summary>
    public float DailyFogTarget { get; private set; }

    /// <summary>Presented daily/diurnal fog contribution after bounded smoothing.</summary>
    public float CurrentDailyFog { get; private set; }

    /// <summary>Current dawn-biased time-of-day multiplier for daily fog.</summary>
    public float FogDiurnalFactor { get; private set; }

    /// <summary>Unity-scaled duration used to present climate changes.</summary>
    public float ClimateTransitionDurationSeconds
    {
        get => climateTransitionDurationSeconds;
        set => climateTransitionDurationSeconds = Mathf.Max(0.01f, value);
    }

    // --- Private ------------------------------------------------------------
    struct Snapshot
    {
        public float cloudiness, cloudErosion, rain, wind, fog, mistiness, skyObscuration,
            scattering, dim, waveMul, turbulence, lightningIntensity;

        public static Snapshot From(WeatherProfile p) => new()
        {
            cloudiness = p.cloudiness,
            cloudErosion = p.cloudErosion,
            rain = p.rainIntensity,
            wind = p.windStrength,
            fog = p.fogBoost,
            mistiness = p.mistiness,
            skyObscuration = p.skyObscuration,
            scattering = p.lightScattering,
            dim = p.dim,
            waveMul = p.waveSpeedMultiplier,
            turbulence = p.waterTurbulence,
            lightningIntensity = p.lightning ? p.lightningIntensity : 0f,
        };

        public static Snapshot Lerp(in Snapshot a, in Snapshot b, float t) => new()
        {
            cloudiness = Mathf.Lerp(a.cloudiness, b.cloudiness, t),
            cloudErosion = Mathf.Lerp(a.cloudErosion, b.cloudErosion, t),
            rain = Mathf.Lerp(a.rain, b.rain, t),
            wind = Mathf.Lerp(a.wind, b.wind, t),
            fog = Mathf.Lerp(a.fog, b.fog, t),
            mistiness = Mathf.Lerp(a.mistiness, b.mistiness, t),
            skyObscuration = Mathf.Lerp(a.skyObscuration, b.skyObscuration, t),
            scattering = Mathf.Lerp(a.scattering, b.scattering, t),
            dim = Mathf.Lerp(a.dim, b.dim, t),
            waveMul = Mathf.Lerp(a.waveMul, b.waveMul, t),
            turbulence = Mathf.Lerp(a.turbulence, b.turbulence, t),
            lightningIntensity = Mathf.Lerp(a.lightningIntensity, b.lightningIntensity, t),
        };
    }

    int _targetIndex;
    Snapshot _from;
    Snapshot _presented;
    float _blend = 1f;          // 1 = fully arrived at the target profile
    float _hoursRemaining;
    float _windAngleDeg;
    float _flash;
    float _secondsUntilStrike = -1f;
    float _windNoiseTime;
    float _referenceRetryTimer;
    long _climateWorldDay = long.MinValue;
    bool _climateInitialized;
    bool _initialized;
    SolEnvironmentCoordinator _environmentCoordinator;

    struct OwnedWaterState
    {
        public float rain;
        public Vector3 windDirection;
        public float windStrength;
        public float waveSpeedMultiplier;
        public float waterTurbulence;
    }

    OwnedWaterState _ownedWaterState;
    bool _hasOwnedWaterState;

    // --- Lifecycle ----------------------------------------------------------

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[SolWeatherManager] Duplicate instance detected. Destroying.", gameObject);
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void OnEnable()
    {
        if (Instance == null || Instance == this)
            Instance = this;

        if (profiles == null || profiles.Length == 0)
        {
            enabled = false;
            Debug.LogWarning("[SolWeatherManager] No weather profiles configured.", this);
            return;
        }

        _environmentCoordinator = SolEnvironmentCoordinator.Resolve(this, createIfMissing: true);
        _environmentCoordinator?.Register(this);

        if (todManager == null) todManager = TimeOfDay.ResolveInstance();
        if (todManager != null) todManager.TimeSkipped += OnTimeSkipped;
        if (waterManager == null) waterManager = SolWaterManager.Instance;
        CaptureOwnedWaterState();

        _targetIndex = Mathf.Clamp(_targetIndex, 0, profiles.Length - 1);
        if (!_initialized)
        {
            _presented = Snapshot.From(profiles[_targetIndex]);
            _from = _presented;
            _blend = 1f;
            _hoursRemaining = NextWeatherDuration();

            if (waterManager != null && waterManager.windDirection.sqrMagnitude > 0.001f)
                _windAngleDeg = Mathf.Atan2(waterManager.windDirection.z, waterManager.windDirection.x)
                              * Mathf.Rad2Deg;
            _initialized = true;
        }

        RefreshClimateTarget(force: true);
        AdvanceClimatePresentation(0f, instant: !_climateInitialized);

        PublishTargetState();
        ApplyEffectiveState(0f, 0f);
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;
        if (todManager != null) todManager.TimeSkipped -= OnTimeSkipped;

        // Return the sky and water to neutral so nothing stays stormy.
        if (todManager != null)
        {
            todManager.WeatherCloudiness = 0f;
            todManager.WeatherFogBoost = 0f;
            todManager.WeatherDim = 0f;
            todManager.WeatherLightningFlash = 0f;
            todManager.WeatherCloudSpeedMul = 1f;
            todManager.WeatherCloudErosion = 0f;
            todManager.WeatherWindDirection = Vector3.right;
            todManager.RefreshEnvironmentFromWeather();
        }
        if (waterManager != null)
            waterManager.lightningFlash = 0f;
        RestoreOwnedWaterState();
        CurrentRainIntensity = 0f;
        CurrentDim = 0f;
        SolWeatherState previousState = CurrentState;
        CurrentState = default;
        if (previousState != CurrentState)
            WeatherStateChanged?.Invoke(CurrentState);
        _flash = 0f;
        _secondsUntilStrike = -1f;

        _environmentCoordinator?.Unregister(this);
        _environmentCoordinator = null;
    }

    void Update()
    {
        if (profiles == null || profiles.Length == 0) return;
        _targetIndex = Mathf.Clamp(_targetIndex, 0, profiles.Length - 1);

        _referenceRetryTimer -= Time.unscaledDeltaTime;
        if (todManager == null && _referenceRetryTimer <= 0f)
        {
            todManager = TimeOfDay.ResolveInstance();
            if (todManager == null)
                _referenceRetryTimer = 0.5f;
        }
        if (waterManager == null) waterManager = SolWaterManager.Instance;
        if (!_hasOwnedWaterState)
            CaptureOwnedWaterState();

        float deltaHours = ComputeDeltaHours();
        float worldDeltaSeconds = ComputeDeltaSeconds();
        float presentationDeltaSeconds = ComputePresentationDeltaSeconds();
        RefreshClimateTarget();
        AdvanceClimatePresentation(presentationDeltaSeconds);
        AdvanceWeatherTimeline(deltaHours);
        AdvancePresentation(presentationDeltaSeconds);
        ApplyEffectiveState(worldDeltaSeconds, presentationDeltaSeconds);
    }

    // --- Public API ---------------------------------------------------------

    /// <summary>Begin transitioning to the given profile index.</summary>
    public void SetWeather(int index, bool instant = false)
    {
        if (profiles == null || profiles.Length == 0) return;
        index = Mathf.Clamp(index, 0, profiles.Length - 1);

        // Retarget from the currently presented values so every channel stays continuous.
        _from = _presented;
        _targetIndex = index;
        _blend = instant ? 1f : 0f;
        if (instant)
            _presented = Snapshot.From(profiles[_targetIndex]);
        _hoursRemaining = NextWeatherDuration();
        PublishTargetState();
        WeatherChanged?.Invoke(profiles[_targetIndex]);
    }

    /// <summary>Begin transitioning to the named profile. Returns false if not found.</summary>
    public bool SetWeather(string profileName, bool instant = false)
    {
        if (profiles == null) return false;
        for (int i = 0; i < profiles.Length; i++)
        {
            if (string.Equals(profiles[i].name, profileName, StringComparison.OrdinalIgnoreCase))
            {
                SetWeather(i, instant);
                return true;
            }
        }
        return false;
    }

    /// <summary>Immediately roll the next weather (same weighted logic as the auto cycle).</summary>
    [ContextMenu("Advance Weather Now")]
    public void NextWeather() => SetWeather(PickNextIndex());

    // --- Internals ------------------------------------------------------------

    float ComputeDeltaHours()
    {
        if (todManager != null) return (float)todManager.WorldDeltaHours;
        // No world clock: fall back to the default 10-minute day pacing.
        return Time.deltaTime / 600f * 24f;
    }

    float ComputeDeltaSeconds()
        => todManager != null ? todManager.WorldDeltaSeconds : Time.deltaTime;

    float ComputePresentationDeltaSeconds()
        => todManager != null ? todManager.PresentationDeltaSeconds : Time.deltaTime;

    void AdvanceWeatherTimeline(float deltaHours)
    {
        float remaining = Mathf.Max(0f, deltaHours);
        int transitions = 0;

        while (remaining > 0.000001f && transitions++ < 64)
        {
            if (!autoCycle)
                return;

            if (remaining < _hoursRemaining)
            {
                _hoursRemaining -= remaining;
                return;
            }

            remaining -= Mathf.Max(_hoursRemaining, 0f);
            SetWeather(PickNextIndex());
        }

        // Very large skips are intentionally coalesced rather than creating
        // unbounded random profile changes in one frame.
        if (transitions >= 64)
        {
            _hoursRemaining = NextWeatherDuration();
        }
    }

    void OnTimeSkipped(TimeChangeResult result)
    {
        RefreshClimateTarget(force: true);
        PublishTargetState();

        if (result.AppliedWorldHours <= 0d)
            return;

        AdvanceWeatherTimeline((float)Math.Min(result.AppliedWorldHours, float.MaxValue));
    }

    void AdvancePresentation(float presentationDeltaSeconds)
    {
        float delta = Mathf.Max(0f, presentationDeltaSeconds);
        _windNoiseTime += delta;

        if (driveWind && delta > 0f)
        {
            float cycleSeconds = todManager != null
                ? Mathf.Max(0.1f, todManager.CycleDuration * 60f)
                : 600f;
            float wanderDegreesPerSecond = windWanderDegPerHour * 24f / cycleSeconds;
            _windAngleDeg += (Mathf.PerlinNoise(_windNoiseTime * 0.02f, 0.37f) - 0.5f)
                           * 2f * wanderDegreesPerSecond * delta;
        }
        PublishTargetState();

        if (_blend >= 1f)
        {
            _presented = Snapshot.From(profiles[_targetIndex]);
            return;
        }

        _blend = Mathf.Min(1f, _blend + delta / Mathf.Max(transitionDurationSeconds, 0.01f));
        float eased = Mathf.SmoothStep(0f, 1f, _blend);
        _presented = Snapshot.Lerp(_from, Snapshot.From(profiles[_targetIndex]), eased);
    }

    void RefreshClimateTarget(bool force = false)
    {
        Calendar calendar = todManager != null ? todManager.Calendar : null;
        long worldDay = calendar != null ? calendar.WorldDayIndex : 0L;
        if (!force && worldDay == _climateWorldDay)
            return;

        _climateWorldDay = worldDay;
        SolSeason season = calendar != null ? calendar.CurrentSeason : SolSeason.Spring;
        DailyFogTarget = EvaluateDailyFogTarget(worldDay, season);
    }

    float EvaluateDailyFogTarget(long worldDayIndex, SolSeason season)
    {
        Vector2 range = GetDailyFogRange(season);
        float minimum = Mathf.Clamp01(Mathf.Min(range.x, range.y));
        float maximum = Mathf.Clamp01(Mathf.Max(range.x, range.y));
        float hash = HashWorldDay(worldDayIndex, climateSeed);
        return Mathf.Lerp(minimum, maximum, hash * hash);
    }

    void AdvanceClimatePresentation(float presentationDeltaSeconds, bool instant = false)
    {
        float clockHour = todManager != null ? todManager.ClockHour : 12f;
        float sunriseHour = todManager != null ? todManager.SunriseClockHour : 6f;
        float dayFactor = todManager != null ? todManager.DayFactor : 1f;
        FogDiurnalFactor = EvaluateFogDiurnalFactor(clockHour, sunriseHour, dayFactor);
        float target = Mathf.Clamp01(DailyFogTarget * FogDiurnalFactor);

        if (instant || !_climateInitialized)
        {
            CurrentDailyFog = target;
            _climateInitialized = true;
            return;
        }

        float maxDelta = Mathf.Max(0f, presentationDeltaSeconds)
                       / Mathf.Max(0.01f, climateTransitionDurationSeconds);
        CurrentDailyFog = Mathf.MoveTowards(CurrentDailyFog, target, maxDelta);
    }

    static float EvaluateFogDiurnalFactor(float clockHour, float sunriseHour, float dayFactor)
    {
        float angleDelta = Mathf.Abs(Mathf.DeltaAngle(
            Mathf.Repeat(clockHour, 24f) * 15f,
            Mathf.Repeat(sunriseHour, 24f) * 15f));
        float hoursFromDawn = angleDelta / 15f;
        float dawn = 1f - Mathf.SmoothStep(0f, 1f,
            Mathf.InverseLerp(0.75f, 3f, hoursFromDawn));
        float overnightFloor = Mathf.Lerp(0.15f, 0.35f, 1f - Mathf.Clamp01(dayFactor));
        return Mathf.Clamp01(Mathf.Max(overnightFloor, dawn));
    }

    static float HashWorldDay(long worldDayIndex, int seed)
    {
        unchecked
        {
            ulong value = (ulong)worldDayIndex + 0x9E3779B97F4A7C15UL + (uint)seed;
            value ^= value >> 30;
            value *= 0xBF58476D1CE4E5B9UL;
            value ^= value >> 27;
            value *= 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return (value & 0xFFFFFFUL) / 16777215f;
        }
    }

    Vector2 GetDailyFogRange(SolSeason season) => season switch
    {
        SolSeason.Summer => summerDailyFogRange,
        SolSeason.Autumn => autumnDailyFogRange,
        SolSeason.Winter => winterDailyFogRange,
        _ => springDailyFogRange,
    };

    void ApplyEffectiveState(float worldDeltaSeconds, float presentationDeltaSeconds)
    {
        if (profiles == null || profiles.Length == 0)
            return;

        Snapshot now = _presented;
        float combinedFog = Mathf.Max(0f, now.fog + CurrentDailyFog);
        float climateMist = Mathf.Clamp01(CurrentDailyFog * 0.75f);
        float combinedMist = 1f - (1f - Mathf.Clamp01(now.mistiness)) * (1f - climateMist);
        CurrentRainIntensity = now.rain;
        CurrentDim = now.dim;
        float rad = _windAngleDeg * Mathf.Deg2Rad;
        Vector3 windDirection = new(Mathf.Cos(rad), 0f, Mathf.Sin(rad));

        UpdateLightning(worldDeltaSeconds, presentationDeltaSeconds, now.lightningIntensity);

        SolWeatherState nextState = new(
            now.cloudiness,
            now.cloudErosion,
            now.rain,
            windDirection,
            now.wind,
            combinedFog,
            combinedMist,
            now.skyObscuration,
            now.scattering,
            now.dim,
            now.waveMul,
            now.turbulence,
            now.lightningIntensity,
            _flash);

        if (nextState != CurrentState)
        {
            CurrentState = nextState;
            WeatherStateChanged?.Invoke(CurrentState);
        }

        if (todManager != null)
        {
            todManager.WeatherCloudiness = now.cloudiness;
            todManager.WeatherCloudErosion = now.cloudErosion;
            todManager.WeatherFogBoost = combinedFog;
            todManager.WeatherDim = now.dim;
            todManager.WeatherLightningFlash = _flash;
            todManager.WeatherCloudSpeedMul = 1f + now.wind * 0.35f;
            todManager.WeatherWindDirection = windDirection;
            todManager.RefreshEnvironmentFromWeather();
        }

        if (waterManager != null)
        {
            waterManager.rainIntensity = now.rain;
            waterManager.lightningFlash = _flash;

            if (driveWind)
            {
                waterManager.windDirection = windDirection;
                waterManager.windStrength = now.wind;
            }

            if (driveWaves)
            {
                waterManager.globalWaveSpeedMultiplier = now.waveMul;
                waterManager.waterTurbulence = now.turbulence;
            }
        }
    }

    int PickNextIndex()
    {
        SolSeason season = todManager != null && todManager.Calendar != null
            ? todManager.Calendar.CurrentSeason
            : SolSeason.Spring;
        float total = 0f;
        for (int i = 0; i < profiles.Length; i++)
        {
            if (avoidRepeat && i == _targetIndex && profiles.Length > 1) continue;
            total += GetEffectiveWeight(profiles[i], season);
        }

        if (total <= 0f)
            return (_targetIndex + 1) % profiles.Length;

        float roll = UnityEngine.Random.value * total;
        for (int i = 0; i < profiles.Length; i++)
        {
            if (avoidRepeat && i == _targetIndex && profiles.Length > 1) continue;
            roll -= GetEffectiveWeight(profiles[i], season);
            if (roll <= 0f) return i;
        }
        return _targetIndex;
    }

    static float GetEffectiveWeight(WeatherProfile profile, SolSeason season)
    {
        if (profile == null)
            return 0f;

        float seasonalMultiplier = season switch
        {
            SolSeason.Summer => profile.summerWeightMultiplier,
            SolSeason.Autumn => profile.autumnWeightMultiplier,
            SolSeason.Winter => profile.winterWeightMultiplier,
            _ => profile.springWeightMultiplier,
        };
        return Mathf.Max(0f, profile.weight) * Mathf.Max(0f, seasonalMultiplier);
    }

    float NextWeatherDuration()
    {
        float min = Mathf.Max(0.01f, Mathf.Min(durationHoursRange.x, durationHoursRange.y));
        float max = Mathf.Max(min, Mathf.Max(durationHoursRange.x, durationHoursRange.y));
        return UnityEngine.Random.Range(min, max);
    }

    void PublishTargetState()
    {
        if (profiles == null || profiles.Length == 0)
        {
            TargetState = default;
            return;
        }

        Snapshot target = Snapshot.From(profiles[Mathf.Clamp(_targetIndex, 0, profiles.Length - 1)]);
        float targetClimateFog = Mathf.Clamp01(DailyFogTarget * FogDiurnalFactor);
        float targetMist = 1f - (1f - Mathf.Clamp01(target.mistiness))
                         * (1f - Mathf.Clamp01(targetClimateFog * 0.75f));
        float rad = _windAngleDeg * Mathf.Deg2Rad;
        Vector3 windDirection = new(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
        TargetState = new SolWeatherState(
            target.cloudiness,
            target.cloudErosion,
            target.rain,
            windDirection,
            target.wind,
            target.fog + targetClimateFog,
            targetMist,
            target.skyObscuration,
            target.scattering,
            target.dim,
            target.waveMul,
            target.turbulence,
            target.lightningIntensity,
            0f);
    }

    void UpdateLightning(float worldDeltaSeconds, float presentationDeltaSeconds, float lightningIntensity)
    {
        float peak = Mathf.Clamp01(lightningIntensity);
        if (_flash > 0.0001f && peak > 0f)
            _flash *= Mathf.Exp(-Mathf.Max(0f, presentationDeltaSeconds) * 7f);
        else
            _flash = 0f;
        _flash = Mathf.Min(_flash, peak);

        bool active = worldDeltaSeconds > 0f
                   && profiles[_targetIndex].lightning
                   && peak > 0.01f;
        if (!active)
        {
            if (!profiles[_targetIndex].lightning)
                _secondsUntilStrike = -1f;
            return;
        }

        if (_secondsUntilStrike < 0f)
            _secondsUntilStrike = NextStrikeDelay();

        _secondsUntilStrike -= worldDeltaSeconds;
        if (_secondsUntilStrike <= 0f)
        {
            _flash = peak;
            LightningTriggered?.Invoke();
            bool echo = UnityEngine.Random.value < doubleStrikeChance;
            float nextDelay = echo
                ? UnityEngine.Random.Range(0.1f, 0.3f)
                : NextStrikeDelay();

            // Coalesce any number of strikes crossed by a time-lapse frame
            // into one visible flash and schedule the next future strike.
            _secondsUntilStrike += Mathf.Max(nextDelay, 0.01f);
            if (_secondsUntilStrike <= 0f)
                _secondsUntilStrike = NextStrikeDelay();
        }
    }

    float NextStrikeDelay()
    {
        float min = Mathf.Max(0.01f, Mathf.Min(strikeIntervalSeconds.x, strikeIntervalSeconds.y));
        float max = Mathf.Max(min, Mathf.Max(strikeIntervalSeconds.x, strikeIntervalSeconds.y));
        return UnityEngine.Random.Range(min, max);
    }

    void CaptureOwnedWaterState()
    {
        if (_hasOwnedWaterState || waterManager == null)
            return;

        _ownedWaterState = new OwnedWaterState
        {
            rain = waterManager.rainIntensity,
            windDirection = waterManager.windDirection,
            windStrength = waterManager.windStrength,
            waveSpeedMultiplier = waterManager.globalWaveSpeedMultiplier,
            waterTurbulence = waterManager.waterTurbulence,
        };
        _hasOwnedWaterState = true;
    }

    void RestoreOwnedWaterState()
    {
        if (!_hasOwnedWaterState || waterManager == null)
            return;

        waterManager.rainIntensity = _ownedWaterState.rain;
        if (driveWind)
        {
            waterManager.windDirection = _ownedWaterState.windDirection;
            waterManager.windStrength = _ownedWaterState.windStrength;
        }
        if (driveWaves)
        {
            waterManager.globalWaveSpeedMultiplier = _ownedWaterState.waveSpeedMultiplier;
            waterManager.waterTurbulence = _ownedWaterState.waterTurbulence;
        }

        _hasOwnedWaterState = false;
    }
}
