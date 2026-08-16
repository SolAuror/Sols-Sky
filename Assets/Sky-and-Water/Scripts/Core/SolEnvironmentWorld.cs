using System;
using Sol.ToD;
using UnityEngine;

namespace Sol.Environment
{
    /// <summary>
    /// Scene-owned environment authority. It currently consumes the proven Sol calendar/weather
    /// producers while exposing the clean immutable contract used by the replacement renderers.
    /// </summary>
    [DefaultExecutionOrder(-900)]
    [DisallowMultipleComponent]
    public sealed class SolEnvironmentWorld : MonoBehaviour
    {
        public const int SnapshotVersion = 1;

        public static SolEnvironmentWorld Active { get; private set; }

        [Header("Authoritative Sources")]
        [SerializeField] TimeOfDay timeOfDay;
        [SerializeField] SolWeatherManager weather;

        [Header("Deterministic Simulation")]
        [SerializeField, Min(1)] int simulationHz = 10;
        [SerializeField] int climateSeed = 1427;
        [SerializeField, Min(0f)] float environmentTimeScale = 1f;
        [SerializeField] bool paused;

        [Header("Fair Weather Fallback")]
        [SerializeField] Vector2 fallbackWindDirection = new(0.92f, 0.38f);
        [SerializeField, Min(0f)] float fallbackWindSpeed = 6f;

        [Header("Weather Wind Conversion")]
        [Tooltip("Metres per second represented by a WeatherProfile windStrength of 1. "
            + "WeatherProfile authors wind as a 0-3 normalised multiplier, but the water "
            + "spectrum needs a real speed: it derives the Pierson-Moskowitz peak "
            + "frequency from it, so feeding the raw multiplier put all wave energy into "
            + "sub-two-metre ripples and the ocean read as flat.")]
        [SerializeField, Min(0f)] float windStrengthToMetresPerSecond = 8f;

        [Header("Surface Climate")]
        [SerializeField, Range(0f, 1f)] float initialWetness;
        [SerializeField, Range(0f, 1f)] float initialSnowCover;
        [SerializeField, Range(-80f, 80f)] float initialTemperatureCelsius = 18f;
        [SerializeField, Range(0f, 1f)] float initialRelativeHumidity = 0.55f;
        [SerializeField, Min(0f)] float wettingPerWorldSecond = 0.004f;
        [SerializeField, Min(0f)] float dryingPerWorldSecond = 0.0004f;

        public event Action<SolEnvironmentState> StateChanged;
        public event Action<SolEnvironmentCommand> CommandApplied;

        public SolEnvironmentState State { get; private set; }
        public bool Paused => paused;
        public float EnvironmentTimeScale => environmentTimeScale;
        public int ClimateSeed => climateSeed;
        public double FixedStepSeconds => 1d / Mathf.Max(1, simulationHz);

        double _accumulator;
        double _absoluteWorldSeconds;
        long _simulationTick;
        ulong _revision;
        float _wetness;
        float _snowCover;
        float _temperature;
        float _humidity;

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
            windStrengthToMetresPerSecond = Mathf.Max(0f, windStrengthToMetresPerSecond);
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

            _absoluteWorldSeconds += delta;
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
                    break;
                case SolEnvironmentCommandType.SetHumidity:
                    _humidity = Mathf.Clamp01((float)command.Value);
                    break;
                case SolEnvironmentCommandType.AddWorldSeconds:
                    _absoluteWorldSeconds = Math.Max(0d, _absoluteWorldSeconds + command.Value);
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
            _simulationTick = snapshot.State.SimulationTick;
            _revision = snapshot.State.Revision;
            _wetness = snapshot.State.Surface.Wetness;
            _snowCover = snapshot.State.Surface.SnowCover;
            _temperature = snapshot.State.Surface.TemperatureCelsius;
            _humidity = snapshot.State.Surface.RelativeHumidity;
            _accumulator = 0d;
            PublishState(force: true);
            return true;
        }

        void StepSimulation(float deltaSeconds)
        {
            _simulationTick++;
            float rain = weather != null ? weather.CurrentState.RainIntensity : 0f;
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

            bool hasWeatherSource = weather != null;
            Vector3 fairDirection = new(fallbackWindDirection.x, 0f, fallbackWindDirection.y);
            SolEnvironmentWindState wind = new(
                hasWeatherSource
                    ? sourceWeather.WindDirection == Vector3.zero ? fairDirection : sourceWeather.WindDirection
                    : fairDirection,
                hasWeatherSource
                    ? sourceWeather.WindStrength * windStrengthToMetresPerSecond
                    : fallbackWindSpeed,
                sourceWeather.WaterTurbulence);

            SolEnvironmentWeatherState presentedWeather = new(
                sourceWeather.Cloudiness,
                sourceWeather.CloudErosion,
                sourceWeather.RainIntensity,
                0f,
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
                tod != null ? tod.WorldDayIndex : 0,
                tod != null ? tod.ClockHour : 12f,
                lighting,
                wind,
                presentedWeather,
                surface);

            State = next;
            if (force)
                StateChanged?.Invoke(next);
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
