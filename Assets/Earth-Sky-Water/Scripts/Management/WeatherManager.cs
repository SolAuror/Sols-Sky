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
///   - SolWaterManager.rainIntensity / legacy windStrength /
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
// Runs before SolEnvironmentWorld (-900), which reads CurrentState when it publishes.
// At -500 it ran after, so everything fed through SolEnvironmentWorld -- all of Water2 --
// saw last frame's weather while SolAtmosphereController (-100) saw this frame's, leaving
// sky and water one frame apart on every weather channel. Only TimeOfDay (-1000) has to
// run first, for WorldDeltaHours.
// Runs outside play mode so the editor previews the authored weather instead of a
// default-constructed one. Without it SolEnvironmentWorld read CurrentState = default in
// the editor, so the sky showed zero cloudiness and dim and the water zero rain and
// turbulence, no matter which profile was selected.
[ExecuteAlways]
[DefaultExecutionOrder(-950)]
public class SolWeatherManager : MonoBehaviour
{
    // --- Singleton -------------------------------------------------------
    public static SolWeatherManager Instance { get; private set; }

    // --- References -------------------------------------------------------
    [Header("References")]
    public TimeOfDay todManager;
    public SolWaterManager waterManager;

    // --- Profiles ---------------------------------------------------------
    [Header("Profiles")]
    [Tooltip("Reusable presentation assets plus scene-specific selection weights.")]
    public SolWeatherSelection[] profiles = Array.Empty<SolWeatherSelection>();

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
    public event Action<SolWeatherProfileAsset> WeatherChanged;

    /// <summary>Fired when the effective blended state changes.</summary>
    public event Action<SolWeatherState> WeatherStateChanged;

    /// <summary>Fired once when a visible lightning strike begins.</summary>
    public event Action LightningTriggered;

    /// <summary>Profile the system is currently blending toward (or holding).</summary>
    public SolWeatherProfileAsset TargetProfile => GetProfile(_targetIndex);

    /// <summary>True while blending between two profiles.</summary>
    public bool IsTransitioning => _blend < 1f;

    /// <summary>Shared progress for every presented weather channel.</summary>
    public float TransitionProgress => Mathf.Clamp01(_blend);

    /// <summary>True while an editor A/B preview owns the presentation snapshot.</summary>
    public bool HasPreview => _previewActive;

    /// <summary>Instantaneous horizontal wind heading in the Sol X/Z plane.</summary>
    public float WindDirectionDegrees => Mathf.Repeat(_windAngleDeg, 360f);

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
        public float cloudiness, cloudErosion, rain, windSpeedMetresPerSecond, fog,
            mistiness, skyObscuration,
            scattering, dim, waveMul, turbulence, lightningIntensity;

        public static Snapshot From(SolWeatherProfileAsset p)
        {
            if (p == null)
                return new Snapshot { scattering = 1f, waveMul = 1f };

            return new Snapshot
            {
                cloudiness = p.cloudiness,
                cloudErosion = p.cloudErosion,
                rain = p.rainIntensity,
                windSpeedMetresPerSecond = p.windSpeedMetresPerSecond,
                fog = p.fogBoost,
                mistiness = p.mistiness,
                skyObscuration = p.skyObscuration,
                scattering = p.lightScattering,
                dim = p.dim,
                waveMul = p.waveSpeedMultiplier,
                turbulence = p.waterTurbulence,
                lightningIntensity = p.lightning ? p.lightningIntensity : 0f,
            };
        }

        public static Snapshot Lerp(in Snapshot a, in Snapshot b, float t) => new()
        {
            cloudiness = Mathf.Lerp(a.cloudiness, b.cloudiness, t),
            cloudErosion = Mathf.Lerp(a.cloudErosion, b.cloudErosion, t),
            rain = Mathf.Lerp(a.rain, b.rain, t),
            windSpeedMetresPerSecond = Mathf.Lerp(
                a.windSpeedMetresPerSecond, b.windSpeedMetresPerSecond, t),
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

    // Editor preview state is deliberately non-serialized. Closing the window restores
    // the exact live presentation that it borrowed, and a domain reload cannot bake an
    // A/B blend into the scene or grow a second weather-blend implementation.
    bool _previewActive;
    int _previewRestoreTargetIndex;
    Snapshot _previewRestoreFrom;
    Snapshot _previewRestorePresented;
    float _previewRestoreBlend;
    float _previewRestoreHoursRemaining;
    float _previewRestoreWindAngleDeg;

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
    const float LegacyWaterWindUnitMetresPerSecond = 8f;

    SolWeatherProfileAsset GetProfile(int index)
    {
        if (profiles == null || index < 0 || index >= profiles.Length)
            return null;
        return profiles[index]?.profile;
    }

    bool HasValidProfiles(out string reason)
    {
        if (profiles == null || profiles.Length == 0)
        {
            reason = "No weather profile selections are configured.";
            return false;
        }

        for (int i = 0; i < profiles.Length; i++)
        {
            SolWeatherProfileAsset profile = GetProfile(i);
            if (profile == null)
            {
                reason = $"Weather selection {i} has no profile asset.";
                return false;
            }

            for (int previous = 0; previous < i; previous++)
            {
                SolWeatherProfileAsset other = GetProfile(previous);
                if (other != null && string.Equals(other.name, profile.name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"Weather profile name '{profile.name}' is duplicated.";
                    return false;
                }
            }
        }

        reason = null;
        return true;
    }

    // --- Lifecycle ----------------------------------------------------------

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // Never destroy scene objects in edit mode: Destroy is deferred and illegal
            // there, and silently deleting an author's GameObject is not a reasonable
            // response to a duplicate anyway. Disable and say so.
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[SolWeatherManager] Duplicate instance disabled. "
                    + "Weather must have one active authority.", gameObject);
                enabled = false;
                return;
            }

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

        if (!HasValidProfiles(out string profileError))
        {
            enabled = false;
            Debug.LogWarning($"[SolWeatherManager] {profileError}", this);
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
            _presented = Snapshot.From(GetProfile(_targetIndex));
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
        RestorePreviewState(apply: false);
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
        if (!HasValidProfiles(out _)) return;
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

        // Edit mode presents the authored target profile rather than cycling. PickNextIndex
        // draws from UnityEngine.Random, so advancing the timeline outside play mode would
        // change the scene's weather at random while the author is working on it.
        float deltaHours = Application.isPlaying ? ComputeDeltaHours() : 0f;
        float worldDeltaSeconds = ComputeDeltaSeconds();
        float presentationDeltaSeconds = ComputePresentationDeltaSeconds();
        RefreshClimateTarget();
        AdvanceClimatePresentation(presentationDeltaSeconds);
        AdvanceWeatherTimeline(deltaHours);
        if (_previewActive)
            PublishTargetState();
        else
            AdvancePresentation(presentationDeltaSeconds);
        ApplyEffectiveState(worldDeltaSeconds, presentationDeltaSeconds);
    }

    // --- Public API ---------------------------------------------------------

    /// <summary>Begin transitioning to the given profile index.</summary>
    public void SetWeather(int index, bool instant = false)
    {
        if (!HasValidProfiles(out _)) return;
        RestorePreviewState(apply: false);
        index = Mathf.Clamp(index, 0, profiles.Length - 1);

        // Retarget from the currently presented values so every channel stays continuous.
        _from = _presented;
        _targetIndex = index;
        _blend = instant ? 1f : 0f;
        if (instant)
            _presented = Snapshot.From(GetProfile(_targetIndex));
        _hoursRemaining = NextWeatherDuration();
        PublishTargetState();
        WeatherChanged?.Invoke(GetProfile(_targetIndex));
    }

    /// <summary>Begin transitioning to the named profile. Returns false if not found.</summary>
    public bool SetWeather(string profileName, bool instant = false)
    {
        if (profiles == null) return false;
        for (int i = 0; i < profiles.Length; i++)
        {
            SolWeatherProfileAsset profile = GetProfile(i);
            if (profile != null
                && string.Equals(profile.name, profileName, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Present an arbitrary point between two configured profiles without starting a
    /// second transition. This directly borrows the existing from/presented/blend state;
    /// <see cref="ClearPreview"/> restores it field-for-field.
    /// </summary>
    public void SetPreview(int indexA, int indexB, float t)
    {
        if (!HasValidProfiles(out _))
            return;

        indexA = Mathf.Clamp(indexA, 0, profiles.Length - 1);
        indexB = Mathf.Clamp(indexB, 0, profiles.Length - 1);
        if (!_previewActive)
        {
            _previewRestoreTargetIndex = _targetIndex;
            _previewRestoreFrom = _from;
            _previewRestorePresented = _presented;
            _previewRestoreBlend = _blend;
            _previewRestoreHoursRemaining = _hoursRemaining;
            _previewRestoreWindAngleDeg = _windAngleDeg;
            _previewActive = true;
        }

        _from = Snapshot.From(GetProfile(indexA));
        _targetIndex = indexB;
        _blend = Mathf.Clamp01(t);
        float eased = Mathf.SmoothStep(0f, 1f, _blend);
        _presented = Snapshot.Lerp(_from, Snapshot.From(GetProfile(indexB)), eased);
        RefreshClimateTarget();
        AdvanceClimatePresentation(0f);
        PublishTargetState();
        ApplyEffectiveState(0f, 0f);
    }

    /// <summary>Restore the presentation that was live before <see cref="SetPreview"/>.</summary>
    public void ClearPreview() => RestorePreviewState(apply: true);

    /// <summary>Change the non-serialized preview wind heading used by every consumer.</summary>
    public void SetPreviewWindDirection(float degrees)
    {
        if (!_previewActive)
            return;

        _windAngleDeg = Mathf.Repeat(degrees, 360f);
        PublishTargetState();
        ApplyEffectiveState(0f, 0f);
    }

    /// <summary>Find a shared profile in this scene's selection policy.</summary>
    public int IndexOfProfile(SolWeatherProfileAsset profile)
    {
        if (profile == null || profiles == null)
            return -1;

        for (int i = 0; i < profiles.Length; i++)
            if (GetProfile(i) == profile)
                return i;
        return -1;
    }

    // --- Internals ------------------------------------------------------------

    void RestorePreviewState(bool apply)
    {
        if (!_previewActive)
            return;

        _targetIndex = _previewRestoreTargetIndex;
        _from = _previewRestoreFrom;
        _presented = _previewRestorePresented;
        _blend = _previewRestoreBlend;
        _hoursRemaining = _previewRestoreHoursRemaining;
        _windAngleDeg = _previewRestoreWindAngleDeg;
        _previewActive = false;

        if (!apply || !isActiveAndEnabled)
            return;

        PublishTargetState();
        ApplyEffectiveState(0f, 0f);
    }

    float ComputeDeltaHours()
    {
        if (todManager != null) return (float)todManager.WorldDeltaHours;
        // No world clock: fall back to the default 10-minute day pacing. Time.deltaTime is
        // not meaningful outside play mode, so a clockless editor session holds still.
        return Application.isPlaying ? Time.deltaTime / 600f * 24f : 0f;
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
            _presented = Snapshot.From(GetProfile(_targetIndex));
            return;
        }

        _blend = Mathf.Min(1f, _blend + delta / Mathf.Max(transitionDurationSeconds, 0.01f));
        float eased = Mathf.SmoothStep(0f, 1f, _blend);
        _presented = Snapshot.Lerp(_from, Snapshot.From(GetProfile(_targetIndex)), eased);
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
        if (!HasValidProfiles(out _))
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
            now.windSpeedMetresPerSecond,
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
            // Cloud wind is published by SolEnvironmentWorld after its dedicated
            // response lag. Do not overwrite it here with the instantaneous target.
            todManager.RefreshEnvironmentFromWeather();
        }

        if (waterManager != null)
        {
            waterManager.rainIntensity = now.rain;
            waterManager.lightningFlash = _flash;

            if (driveWind)
            {
                waterManager.windDirection = windDirection;
                // Water 1 is the sole legacy adapter seam. Its shaders still author
                // wind in the old 0..3 unit, while every shared/Water2 path is m/s.
                waterManager.windStrength = now.windSpeedMetresPerSecond
                    / LegacyWaterWindUnitMetresPerSecond;
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

    static float GetEffectiveWeight(SolWeatherSelection selection, SolSeason season)
    {
        if (selection == null || selection.profile == null)
            return 0f;

        float seasonalMultiplier = season switch
        {
            SolSeason.Summer => selection.summerWeightMultiplier,
            SolSeason.Autumn => selection.autumnWeightMultiplier,
            SolSeason.Winter => selection.winterWeightMultiplier,
            _ => selection.springWeightMultiplier,
        };
        return Mathf.Max(0f, selection.weight) * Mathf.Max(0f, seasonalMultiplier);
    }

    float NextWeatherDuration()
    {
        float min = Mathf.Max(0.01f, Mathf.Min(durationHoursRange.x, durationHoursRange.y));
        float max = Mathf.Max(min, Mathf.Max(durationHoursRange.x, durationHoursRange.y));
        return UnityEngine.Random.Range(min, max);
    }

    void PublishTargetState()
    {
        if (!HasValidProfiles(out _))
        {
            TargetState = default;
            return;
        }

        Snapshot target = Snapshot.From(GetProfile(Mathf.Clamp(_targetIndex, 0, profiles.Length - 1)));
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
            target.windSpeedMetresPerSecond,
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

        SolWeatherProfileAsset targetProfile = GetProfile(_targetIndex);
        bool active = worldDeltaSeconds > 0f
                   && targetProfile != null
                   && targetProfile.lightning
                   && peak > 0.01f;
        if (!active)
        {
            if (targetProfile == null || !targetProfile.lightning)
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
