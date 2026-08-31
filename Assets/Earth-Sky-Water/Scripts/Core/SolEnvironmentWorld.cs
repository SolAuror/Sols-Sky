using System;
using Sol.ToD;
using UnityEngine;

namespace Sol.Environment
{
    /// <summary>
    /// Scene-owned environment authority. It currently consumes the proven Sol calendar/weather
    /// producers while exposing the clean immutable contract used by the replacement renderers.
    /// </summary>
    // Runs outside play mode because everything downstream already does: SolWaterWorld,
    // SolWaterBody, SolWaterWetness and the spline geometry are all [ExecuteAlways]. With
    // this component play-only, Active stayed null in the editor, State stayed default, and
    // the water read zero wind and a wave clock frozen at zero while the FFT generated a
    // 6 m/s sea and the clipmap sized its bounds for dead calm. Time only advances here if
    // TimeOfDay is advancing it, so a static preview stays static.
    [ExecuteAlways]
    [DefaultExecutionOrder(-900)]
    [DisallowMultipleComponent]
    public sealed class SolEnvironmentWorld : MonoBehaviour
    {
        public const int SnapshotVersion = 3;

        const float FogWindTimeConstantSeconds = 20f;
        const float CloudWindTimeConstantSeconds = 120f;
        const float SeaStateTimeConstantSeconds = 20f * 60f;
        const float LegacyCloudSpeedUnitMetresPerSecond = 8f;

        public static SolEnvironmentWorld Active { get; private set; }

        /// <summary>
        /// Coherent fair-weather state used when no world has published yet. Its values
        /// mirror the serialized fallback defaults below so a scene without an environment
        /// world looks like one with a calm day rather than like three different days.
        /// </summary>
        static readonly SolEnvironmentState Fallback = new(
            0UL, 0L, 0d, 0d, 0L, 12f,
            new SolEnvironmentLightingState(Vector3.up, Vector3.down, Color.white, 1f, 1f, 0f, 0f),
            new SolEnvironmentWindState(
                new Vector3(0.92f, 0f, 0.38f), 6f,
                new Vector3(0.92f, 0f, 0.38f), 6f,
                new Vector3(0.92f, 0f, 0.38f), 6f,
                6f, 0f),
            new SolEnvironmentWeatherState(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 1f),
            new SolSurfaceConditionState(0f, 0f, 18f, 0.55f));

        /// <summary>
        /// The active world's published state, or the fair-weather stand-in.
        ///
        /// Consumers used to improvise their own fallbacks and disagreed about them: the
        /// FFT generated a 6 m/s sea while the clipmap sized its culling bounds and the
        /// surface material for dead calm, so the spectrum displaced geometry past bounds
        /// that assumed a flat plane and opened holes at the edge of the ocean. Read the
        /// environment through this and there is one answer.
        /// </summary>
        public static SolEnvironmentState ResolveState()
            => Active != null ? Active.State : Fallback;

        [Header("Authoritative Sources")]
        [SerializeField] TimeOfDay timeOfDay;
        [SerializeField] SolWeatherManager weather;

        [Header("Deterministic Simulation")]
        [SerializeField, Min(1)] int simulationHz = 10;
        [SerializeField] int climateSeed = 1427;
        [Tooltip("Sub-scale applied on top of TimeOfDay.timeScale. It scales the "
            + "environment simulation, the wave clock and hydrology, but NOT the sky: "
            + "sun, moon and calendar stay on the TimeOfDay scale. Leave at 1 unless you "
            + "deliberately want water and weather running at a different rate from the sky.")]
        [SerializeField, Min(0f)] float environmentTimeScale = 1f;
        [Tooltip("Sub-pause on top of TimeOfDay.paused. Pausing TimeOfDay already freezes "
            + "this world, because its delta comes from TimeOfDay.WorldDeltaSeconds, which "
            + "is zero while paused. This freezes the environment while the sky keeps running.")]
        [SerializeField] bool paused;

        [Header("Fair Weather Fallback")]
        [SerializeField] Vector2 fallbackWindDirection = new(0.92f, 0.38f);
        [SerializeField, Min(0f)] float fallbackWindSpeed = 6f;

        [Header("Surface Climate")]
        [SerializeField, Range(0f, 1f)] float initialWetness;
        [SerializeField, Range(0f, 1f)] float initialSnowCover;
        [SerializeField, Range(-80f, 80f)] float initialTemperatureCelsius = 18f;
        [SerializeField, Range(0f, 1f)] float initialRelativeHumidity = 0.55f;
        [SerializeField, Min(0f)] float wettingPerWorldSecond = 0.004f;
        [SerializeField, Min(0f)] float dryingPerWorldSecond = 0.0004f;

        [Header("Seasonal Climate")]
        [Tooltip("Drive temperature from the calendar instead of holding the initial value. "
            + "Temperature was a static serialized constant, so SolHydrologyWorld's freeze "
            + "and melt branches could only ever be reached by an explicit command.")]
        [SerializeField] bool driveTemperatureFromSeason = true;
        [Tooltip("Air temperature at the height of summer.")]
        [SerializeField, Range(-80f, 80f)] float summerTemperatureCelsius = 26f;
        [Tooltip("Air temperature at the depth of winter.")]
        [SerializeField, Range(-80f, 80f)] float winterTemperatureCelsius = -2f;
        [Tooltip("Peak-to-trough swing between local noon and midnight.")]
        [SerializeField, Range(0f, 20f)] float diurnalTemperatureSwing = 6f;

        public event Action<SolEnvironmentState> StateChanged;
        public event Action<SolEnvironmentCommand> CommandApplied;

        public SolEnvironmentState State { get; private set; }
        /// <summary>
        /// World seconds advanced this frame, after pause and both time scales. Consumers
        /// that step their own simulation read this instead of Time.deltaTime so they
        /// share the pause and the scrub with the rest of the environment.
        /// </summary>
        public double WorldDeltaSeconds { get; private set; }
        public bool Paused => paused;
        public float EnvironmentTimeScale => environmentTimeScale;
        public int ClimateSeed => climateSeed;
        public double FixedStepSeconds => 1d / Mathf.Max(1, simulationHz);

        double _accumulator;
        double _absoluteWorldSeconds;
        double _waveSeconds;
        long _simulationTick;
        ulong _revision;
        float _wetness;
        float _snowCover;
        float _temperature;
        bool _temperatureOverridden;
        float _humidity;
        SolWindLag _fogWindX;
        SolWindLag _fogWindZ;
        SolWindLag _cloudWindX;
        SolWindLag _cloudWindZ;
        SolWindLag _seaStateSpeed;
        bool _windResponseInitialized;

        void Awake()
        {
            if (Active != null && Active != this)
            {
                Debug.LogError("[SolEnvironmentWorld] Only one active world is allowed per scene.", this);
                enabled = false;
                return;
            }

            Active = this;
            ResolveSources();
            _wetness = initialWetness;
            _snowCover = initialSnowCover;
            _temperature = initialTemperatureCelsius;
            _humidity = initialRelativeHumidity;
            InitializeWindResponse();
            PublishState(force: true);
        }

        void OnEnable()
        {
            if (Active == null || Active == this)
                Active = this;
        }

        void OnDisable()
        {
            if (Active == this)
                Active = null;
        }

        void OnValidate()
        {
            simulationHz = Mathf.Max(1, simulationHz);
            environmentTimeScale = Mathf.Max(0f, environmentTimeScale);
            wettingPerWorldSecond = Mathf.Max(0f, wettingPerWorldSecond);
            dryingPerWorldSecond = Mathf.Max(0f, dryingPerWorldSecond);
            fallbackWindSpeed = Mathf.Max(0f, fallbackWindSpeed);
            if (fallbackWindDirection.sqrMagnitude < 0.0001f)
                fallbackWindDirection = Vector2.right;
        }

        void Update()
        {
            ResolveSources();

            double delta = timeOfDay != null
                ? Math.Max(0d, timeOfDay.WorldDeltaSeconds)
                : Math.Max(0d, Time.deltaTime);
            if (paused)
                delta = 0d;
            delta *= environmentTimeScale;

            WorldDeltaSeconds = delta;
            _absoluteWorldSeconds += delta;
            // Wave phase is the integral of delta against the live multiplier, never
            // absoluteSeconds * multiplier. The product form shifted the whole elapsed
            // session's phase every time the weather changed, which re-randomised the
            // ocean mid-transition and got worse the longer the session ran.
            _waveSeconds += delta * ResolveWaveSpeedMultiplier();
            _accumulator += delta;

            double step = FixedStepSeconds;
            int boundedSteps = 0;
            while (_accumulator >= step && boundedSteps < 64)
            {
                StepSimulation((float)step);
                _accumulator -= step;
                boundedSteps++;
            }

            // Large chronology jumps update low-frequency state once instead of replaying unbounded frames.
            if (_accumulator >= step)
            {
                StepSimulation((float)Math.Min(_accumulator, 3600d));
                _accumulator = 0d;
            }

            PublishState(force: boundedSteps > 0);
        }

        public bool ApplyCommand(in SolEnvironmentCommand command)
        {
            switch (command.Type)
            {
                case SolEnvironmentCommandType.SetPaused:
                    paused = command.Value >= 0.5d;
                    break;
                case SolEnvironmentCommandType.SetTimeScale:
                    environmentTimeScale = Mathf.Max(0f, (float)command.Value);
                    break;
                case SolEnvironmentCommandType.SetSurfaceWetness:
                    _wetness = Mathf.Clamp01((float)command.Value);
                    break;
                case SolEnvironmentCommandType.SetSnowCover:
                    _snowCover = Mathf.Clamp01((float)command.Value);
                    break;
                case SolEnvironmentCommandType.SetTemperature:
                    _temperature = Mathf.Clamp((float)command.Value, -100f, 100f);
                    // An explicit command outranks the seasonal model. Without this the
                    // next simulation step would overwrite it and the command would look
                    // like it silently did nothing.
                    _temperatureOverridden = true;
                    break;
                case SolEnvironmentCommandType.SetHumidity:
                    _humidity = Mathf.Clamp01((float)command.Value);
                    break;
                case SolEnvironmentCommandType.AddWorldSeconds:
                    _absoluteWorldSeconds = Math.Max(0d, _absoluteWorldSeconds + command.Value);
                    _waveSeconds = Math.Max(0d,
                        _waveSeconds + command.Value * ResolveWaveSpeedMultiplier());
                    break;
                default:
                    return false;
            }

            CommandApplied?.Invoke(command);
            PublishState(force: true);
            return true;
        }

        public SolEnvironmentSnapshot CaptureSnapshot()
            => new(SnapshotVersion, climateSeed, paused, environmentTimeScale, State);

        public bool RestoreSnapshot(in SolEnvironmentSnapshot snapshot)
        {
            if (snapshot.Version != SnapshotVersion)
                return false;

            climateSeed = snapshot.ClimateSeed;
            paused = snapshot.Paused;
            environmentTimeScale = snapshot.TimeScale;
            _absoluteWorldSeconds = snapshot.State.AbsoluteWorldSeconds;
            _waveSeconds = snapshot.State.WaveSeconds;
            _simulationTick = snapshot.State.SimulationTick;
            _revision = snapshot.State.Revision;
            _wetness = snapshot.State.Surface.Wetness;
            _snowCover = snapshot.State.Surface.SnowCover;
            _temperature = snapshot.State.Surface.TemperatureCelsius;
            // The calendar is restored alongside this, so the seasonal model reproduces
            // the saved climate rather than fighting it. Resume it.
            _temperatureOverridden = false;
            _humidity = snapshot.State.Surface.RelativeHumidity;
            Vector3 fogVelocity = snapshot.State.Wind.FogAdvectionDirection
                * snapshot.State.Wind.FogAdvectionSpeed;
            Vector3 cloudVelocity = snapshot.State.Wind.CloudDirection
                * snapshot.State.Wind.CloudSpeed;
            _fogWindX = new SolWindLag(fogVelocity.x);
            _fogWindZ = new SolWindLag(fogVelocity.z);
            _cloudWindX = new SolWindLag(cloudVelocity.x);
            _cloudWindZ = new SolWindLag(cloudVelocity.z);
            _seaStateSpeed = new SolWindLag(snapshot.State.Wind.SeaStateSpeed);
            _windResponseInitialized = true;
            _accumulator = 0d;
            PublishState(force: true);
            return true;
        }

        /// <summary>
        /// Air temperature for the current calendar position and hour.
        ///
        /// Anchored so the warm peak lands mid-Summer: Calendar buckets months into
        /// seasons as (month-1)*4/monthCount, which puts the middle of Summer at about
        /// 0.458 of the way through the year, hence the quarter-cycle offset of 0.208.
        /// </summary>
        float ResolveSeasonalTemperature()
        {
            if (_temperatureOverridden || !driveTemperatureFromSeason || timeOfDay == null)
                return _temperature;

            Calendar calendar = timeOfDay.Calendar;
            float yearProgress = calendar != null ? calendar.YearProgress : 0f;
            const float SummerPeakOffset = 0.208f;
            float warmth = 0.5f + 0.5f * Mathf.Sin(
                (yearProgress - SummerPeakOffset) * Mathf.PI * 2f);
            float seasonal = Mathf.Lerp(
                winterTemperatureCelsius, summerTemperatureCelsius, warmth);
            // DayFactor is already the sun's elevation response, so it carries the
            // diurnal shape without needing a second clock.
            float diurnal = (timeOfDay.DayFactor - 0.5f) * diurnalTemperatureSwing;
            return Mathf.Clamp(seasonal + diurnal, -100f, 100f);
        }

        /// <summary>
        /// Fraction of precipitation falling as snow rather than rain. Snow was hard-coded
        /// to zero in the published weather state, which is why SolHydrologyWorld's snow
        /// accumulation branch was unreachable and SnowCover could only ever melt.
        /// </summary>
        float ResolveSnowFraction() => Mathf.Clamp01(Mathf.InverseLerp(2f, -1f, _temperature));

        void StepSimulation(float deltaSeconds)
        {
            _simulationTick++;
            StepWindResponse(deltaSeconds);
            _temperature = ResolveSeasonalTemperature();
            float precipitation = weather != null ? weather.CurrentState.RainIntensity : 0f;
            // Only liquid precipitation wets the ground.
            float rain = precipitation * (1f - ResolveSnowFraction());
            _snowCover = Mathf.Clamp01(_snowCover
                + precipitation * ResolveSnowFraction() * wettingPerWorldSecond * deltaSeconds);
            _wetness = Mathf.Clamp01(_wetness
                + rain * wettingPerWorldSecond * deltaSeconds
                - (1f - rain) * dryingPerWorldSecond * deltaSeconds);

            if (_temperature > 0f && _snowCover > 0f)
                _snowCover = Mathf.Max(0f, _snowCover - _temperature * 0.00001f * deltaSeconds);
        }

        void PublishState(bool force)
        {
            SolWeatherState sourceWeather = weather != null ? weather.CurrentState : default;
            TimeOfDay tod = timeOfDay;

            Light dominant = tod != null ? tod.DominantAtmosphereLight : RenderSettings.sun;
            Color lightColor = dominant != null ? dominant.color : Color.white;
            float lightIntensity = dominant != null ? dominant.intensity : 1f;

            SolEnvironmentLightingState lighting = new(
                tod != null ? tod.SunDirection : Vector3.up,
                tod != null ? tod.MoonDirection : Vector3.down,
                lightColor,
                lightIntensity,
                tod != null ? tod.DayFactor : 1f,
                tod != null ? tod.MoonIllumination : 0f,
                tod != null ? Mathf.Max(tod.SolarEclipseStrength, tod.LunarEclipseStrength) : 0f);

            ResolveSourceWind(out Vector3 windDirection, out float windSpeed,
                out float turbulence);
            if (!_windResponseInitialized)
                InitializeWindResponse();
            Vector3 fogVelocity = new(_fogWindX.Value, 0f, _fogWindZ.Value);
            Vector3 cloudVelocity = new(_cloudWindX.Value, 0f, _cloudWindZ.Value);
            Vector3 fogDirection = ResolveDirection(fogVelocity, windDirection);
            Vector3 cloudDirection = ResolveDirection(cloudVelocity, windDirection);
            SolEnvironmentWindState wind = new(
                windDirection,
                windSpeed,
                fogDirection,
                fogVelocity.magnitude,
                cloudDirection,
                cloudVelocity.magnitude,
                _seaStateSpeed.Value,
                turbulence);

            SolEnvironmentWeatherState presentedWeather = new(
                sourceWeather.Cloudiness,
                sourceWeather.CloudErosion,
                sourceWeather.RainIntensity * (1f - ResolveSnowFraction()),
                sourceWeather.RainIntensity * ResolveSnowFraction(),
                sourceWeather.FogBoost,
                sourceWeather.Mistiness,
                sourceWeather.LightningFlash,
                sourceWeather.WaterTurbulence,
                sourceWeather.WaveSpeedMultiplier <= 0f ? 1f : sourceWeather.WaveSpeedMultiplier);

            SolSurfaceConditionState surface = new(_wetness, _snowCover, _temperature, _humidity);
            SolEnvironmentState next = new(
                ++_revision,
                _simulationTick,
                _absoluteWorldSeconds,
                _waveSeconds,
                tod != null ? tod.WorldDayIndex : 0,
                tod != null ? tod.ClockHour : 12f,
                lighting,
                wind,
                presentedWeather,
                surface);

            State = next;
            ApplyCloudWindToTimeOfDay(wind);
            if (force)
                StateChanged?.Invoke(next);
        }

        void InitializeWindResponse()
        {
            ResolveSourceWind(out Vector3 direction, out float speed, out _);
            Vector3 velocity = direction * speed;
            _fogWindX = new SolWindLag(velocity.x);
            _fogWindZ = new SolWindLag(velocity.z);
            _cloudWindX = new SolWindLag(velocity.x);
            _cloudWindZ = new SolWindLag(velocity.z);
            _seaStateSpeed = new SolWindLag(speed);
            _windResponseInitialized = true;
        }

        void StepWindResponse(float deltaSeconds)
        {
            if (!_windResponseInitialized)
                InitializeWindResponse();

            ResolveSourceWind(out Vector3 direction, out float speed, out _);
            Vector3 targetVelocity = direction * speed;
            _fogWindX = _fogWindX.Step(
                targetVelocity.x, deltaSeconds, FogWindTimeConstantSeconds);
            _fogWindZ = _fogWindZ.Step(
                targetVelocity.z, deltaSeconds, FogWindTimeConstantSeconds);
            _cloudWindX = _cloudWindX.Step(
                targetVelocity.x, deltaSeconds, CloudWindTimeConstantSeconds);
            _cloudWindZ = _cloudWindZ.Step(
                targetVelocity.z, deltaSeconds, CloudWindTimeConstantSeconds);
            _seaStateSpeed = _seaStateSpeed.Step(
                speed, deltaSeconds, SeaStateTimeConstantSeconds);
        }

        void ResolveSourceWind(
            out Vector3 direction,
            out float speedMetresPerSecond,
            out float turbulence)
        {
            SolWeatherState source = weather != null ? weather.CurrentState : default;
            Vector3 fallbackDirection = new(
                fallbackWindDirection.x, 0f, fallbackWindDirection.y);
            direction = weather != null && source.WindDirection.sqrMagnitude > 0.0001f
                ? source.WindDirection.normalized
                : fallbackDirection.normalized;
            speedMetresPerSecond = weather != null
                ? source.WindSpeedMetresPerSecond
                : fallbackWindSpeed;
            turbulence = weather != null ? source.WaterTurbulence : 0f;
        }

        static Vector3 ResolveDirection(Vector3 velocity, Vector3 fallback)
            => velocity.sqrMagnitude > 0.000001f ? velocity.normalized : fallback;

        void ApplyCloudWindToTimeOfDay(in SolEnvironmentWindState wind)
        {
            if (timeOfDay == null)
                return;

            timeOfDay.WeatherWindDirection = wind.CloudDirection;
            // Preserve the existing scenic cloud pacing while deriving it from the
            // physical 10 m speed. The sky shader still consumes a dimensionless trim.
            timeOfDay.WeatherCloudSpeedMul = 1f + wind.CloudSpeed
                * (0.35f / LegacyCloudSpeedUnitMetresPerSecond);
        }

        /// <summary>
        /// The weather's wave speed multiplier, guarded the same way the published
        /// weather state guards it so the clock and the presented value never disagree.
        /// </summary>
        float ResolveWaveSpeedMultiplier()
        {
            float multiplier = weather != null ? weather.CurrentState.WaveSpeedMultiplier : 1f;
            return multiplier <= 0f ? 1f : multiplier;
        }

        void ResolveSources()
        {
            if (timeOfDay == null)
                timeOfDay = TimeOfDay.ResolveInstance();
            if (weather == null)
                weather = SolWeatherManager.Instance;
        }
    }
}
