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
/// Durations use the TimeOfDay clock, so weather respects timeScale and
/// pauses with the world. Lightning flashes run on real time so they stay
/// snappy at any timeScale.
///
/// Public API: SetWeather(index/name, instant), NextWeather(), WeatherChanged
/// event, CurrentProfile / TargetProfile / IsTransitioning.
/// ---------------------------------------------------------------------------
/// </summary>
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

        [Tooltip("Rain intensity pushed to SolWaterManager (0 = dry, 1 = downpour).")]
        [Range(0f, 1f)] public float rainIntensity = 0f;

        [Tooltip("Wind strength pushed to SolWaterManager.")]
        [Range(0f, 3f)] public float windStrength = 1f;

        [Tooltip("Additional fog density multiplier (0 = none, 1 = double).")]
        [Range(0f, 2f)] public float fogBoost = 0f;

        [Tooltip("Storm darkening applied to sun/moon, ambient, sky, and clouds.")]
        [Range(0f, 1f)] public float dim = 0f;

        [Tooltip("Global wave speed multiplier pushed to SolWaterManager.")]
        [Range(0f, 3f)] public float waveSpeedMultiplier = 1f;

        [Tooltip("Enable random lightning flashes during this weather.")]
        public bool lightning = false;

        [Tooltip("Relative chance of this profile being picked by the auto cycle.")]
        [Min(0f)] public float weight = 1f;
    }

    // --- References -------------------------------------------------------
    [Header("References")]
    public TimeOfDay todManager;
    public SolWaterManager waterManager;

    // --- Profiles ---------------------------------------------------------
    [Header("Profiles")]
    public WeatherProfile[] profiles =
    {
        new WeatherProfile { name = "Clear",    cloudiness = 0.00f, rainIntensity = 0.0f, windStrength = 0.8f, fogBoost = 0.0f, dim = 0.00f, waveSpeedMultiplier = 1.0f,  weight = 4f },
        new WeatherProfile { name = "Overcast", cloudiness = 0.65f, rainIntensity = 0.0f, windStrength = 1.2f, fogBoost = 0.4f, dim = 0.15f, waveSpeedMultiplier = 1.1f,  weight = 3f },
        new WeatherProfile { name = "Rain",     cloudiness = 0.85f, rainIntensity = 0.6f, windStrength = 1.6f, fogBoost = 0.9f, dim = 0.35f, waveSpeedMultiplier = 1.25f, weight = 2f },
        new WeatherProfile { name = "Storm",    cloudiness = 1.00f, rainIntensity = 1.0f, windStrength = 2.5f, fogBoost = 1.2f, dim = 0.55f, waveSpeedMultiplier = 1.6f,  lightning = true, weight = 1f },
    };

    // --- Auto Cycling -----------------------------------------------------
    [Header("Auto Cycling")]
    [Tooltip("Automatically pick the next weather when the current one expires.")]
    public bool autoCycle = true;

    [Tooltip("Min/max hold duration of a weather state, in game hours.")]
    public Vector2 durationHoursRange = new(6f, 18f);

    [Tooltip("How long a transition between weathers takes, in game hours.")]
    [Min(0.01f)]
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
    [Tooltip("Min/max real-time seconds between strikes while lightning weather is active.")]
    public Vector2 strikeIntervalSeconds = new(4f, 15f);

    [Tooltip("Chance that a strike is followed by a quick echo flash.")]
    [Range(0f, 1f)]
    public float doubleStrikeChance = 0.35f;

    // --- Events / Public state ---------------------------------------------
    /// <summary>Fired when a transition toward a new profile begins.</summary>
    public event Action<WeatherProfile> WeatherChanged;

    /// <summary>Profile the system is currently blending toward (or holding).</summary>
    public WeatherProfile TargetProfile =>
        profiles != null && profiles.Length > 0 ? profiles[_targetIndex] : null;

    /// <summary>True while blending between two profiles.</summary>
    public bool IsTransitioning => _blend < 1f;

    /// <summary>Effective rain intensity after blending (for audio/VFX hooks).</summary>
    public float CurrentRainIntensity { get; private set; }

    /// <summary>Effective storm darkening after blending.</summary>
    public float CurrentDim { get; private set; }

    // --- Private ------------------------------------------------------------
    struct Snapshot
    {
        public float cloudiness, rain, wind, fog, dim, waveMul;

        public static Snapshot From(WeatherProfile p) => new()
        {
            cloudiness = p.cloudiness,
            rain = p.rainIntensity,
            wind = p.windStrength,
            fog = p.fogBoost,
            dim = p.dim,
            waveMul = p.waveSpeedMultiplier,
        };

        public static Snapshot Lerp(in Snapshot a, in Snapshot b, float t) => new()
        {
            cloudiness = Mathf.Lerp(a.cloudiness, b.cloudiness, t),
            rain = Mathf.Lerp(a.rain, b.rain, t),
            wind = Mathf.Lerp(a.wind, b.wind, t),
            fog = Mathf.Lerp(a.fog, b.fog, t),
            dim = Mathf.Lerp(a.dim, b.dim, t),
            waveMul = Mathf.Lerp(a.waveMul, b.waveMul, t),
        };
    }

    int _targetIndex;
    Snapshot _from;
    float _blend = 1f;          // 1 = fully arrived at the target profile
    float _hoursRemaining;
    float _windAngleDeg;
    float _flash;
    float _nextStrikeRealTime;

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

        _targetIndex = Mathf.Clamp(_targetIndex, 0, profiles.Length - 1);
        _from = Snapshot.From(profiles[_targetIndex]);
        _blend = 1f;
        _hoursRemaining = UnityEngine.Random.Range(durationHoursRange.x, durationHoursRange.y);

        if (waterManager != null && waterManager.windDirection.sqrMagnitude > 0.001f)
            _windAngleDeg = Mathf.Atan2(waterManager.windDirection.z, waterManager.windDirection.x)
                          * Mathf.Rad2Deg;
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;

        // Return the sky and water to neutral so nothing stays stormy.
        if (todManager != null)
        {
            todManager.WeatherCloudiness = 0f;
            todManager.WeatherFogBoost = 0f;
            todManager.WeatherDim = 0f;
            todManager.WeatherLightningFlash = 0f;
            todManager.WeatherCloudSpeedMul = 1f;
        }
        if (waterManager != null)
            waterManager.rainIntensity = 0f;
    }

    void Update()
    {
        if (profiles == null || profiles.Length == 0) return;
        _targetIndex = Mathf.Clamp(_targetIndex, 0, profiles.Length - 1);

        if (todManager == null) todManager = TimeOfDay.ResolveInstance();
        if (waterManager == null) waterManager = SolWaterManager.Instance;

        float deltaHours = ComputeDeltaHours();

        // --- Advance the state machine (game time) ---
        if (_blend < 1f)
            _blend = Mathf.Min(1f, _blend + deltaHours / transitionHours);
        else if (autoCycle)
        {
            _hoursRemaining -= deltaHours;
            if (_hoursRemaining <= 0f)
                SetWeather(PickNextIndex());
        }

        // --- Effective values ---
        float t = Mathf.SmoothStep(0f, 1f, _blend);
        Snapshot now = Snapshot.Lerp(_from, Snapshot.From(profiles[_targetIndex]), t);
        CurrentRainIntensity = now.rain;
        CurrentDim = now.dim;

        // --- Lightning (real time; paused world = no new strikes) ---
        UpdateLightning(deltaHours > 0f);

        // --- Push to TimeOfDay ---
        if (todManager != null)
        {
            todManager.WeatherCloudiness = now.cloudiness;
            todManager.WeatherFogBoost = now.fog;
            todManager.WeatherDim = now.dim;
            todManager.WeatherLightningFlash = _flash;
            // Storm winds visibly push the clouds.
            todManager.WeatherCloudSpeedMul = 1f + now.wind * 0.35f;
        }

        // --- Push to SolWaterManager ---
        if (waterManager != null)
        {
            waterManager.rainIntensity = now.rain;

            if (driveWind)
            {
                _windAngleDeg += (Mathf.PerlinNoise(Time.time * 0.02f, 0.37f) - 0.5f)
                               * 2f * windWanderDegPerHour * deltaHours;
                float rad = _windAngleDeg * Mathf.Deg2Rad;
                waterManager.windDirection = new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
                waterManager.windStrength = now.wind;
            }

            if (driveWaves)
                waterManager.globalWaveSpeedMultiplier = now.waveMul;
        }
    }

    // --- Public API ---------------------------------------------------------

    /// <summary>Begin transitioning to the given profile index.</summary>
    public void SetWeather(int index, bool instant = false)
    {
        if (profiles == null || profiles.Length == 0) return;
        index = Mathf.Clamp(index, 0, profiles.Length - 1);

        // Capture the currently-effective values as the blend origin.
        float t = Mathf.SmoothStep(0f, 1f, _blend);
        _from = Snapshot.Lerp(_from, Snapshot.From(profiles[_targetIndex]), t);

        _targetIndex = index;
        _blend = instant ? 1f : 0f;
        _hoursRemaining = UnityEngine.Random.Range(durationHoursRange.x, durationHoursRange.y);
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
        if (todManager != null)
        {
            if (todManager.Paused) return 0f;
            float cycleSeconds = Mathf.Max(todManager.CycleDuration, 0.1f) * 60f;
            return Time.deltaTime * todManager.TimeScale / cycleSeconds * 24f;
        }
        // No world clock: fall back to the default 10-minute day pacing.
        return Time.deltaTime / 600f * 24f;
    }

    int PickNextIndex()
    {
        float total = 0f;
        for (int i = 0; i < profiles.Length; i++)
        {
            if (avoidRepeat && i == _targetIndex && profiles.Length > 1) continue;
            total += Mathf.Max(0f, profiles[i].weight);
        }

        if (total <= 0f)
            return (_targetIndex + 1) % profiles.Length;

        float roll = UnityEngine.Random.value * total;
        for (int i = 0; i < profiles.Length; i++)
        {
            if (avoidRepeat && i == _targetIndex && profiles.Length > 1) continue;
            roll -= Mathf.Max(0f, profiles[i].weight);
            if (roll <= 0f) return i;
        }
        return _targetIndex;
    }

    void UpdateLightning(bool worldRunning)
    {
        // Flash envelope decays on real time so strikes look right at any timeScale.
        _flash = _flash > 0.01f ? _flash * Mathf.Exp(-Time.unscaledDeltaTime * 7f) : 0f;

        bool active = worldRunning
                   && profiles[_targetIndex].lightning
                   && _blend > 0.4f;
        if (!active)
            return;

        if (Time.unscaledTime >= _nextStrikeRealTime)
        {
            _flash = 1f;
            bool echo = UnityEngine.Random.value < doubleStrikeChance;
            _nextStrikeRealTime = Time.unscaledTime + (echo
                ? UnityEngine.Random.Range(0.1f, 0.3f)
                : UnityEngine.Random.Range(strikeIntervalSeconds.x, strikeIntervalSeconds.y));
        }
    }
}
