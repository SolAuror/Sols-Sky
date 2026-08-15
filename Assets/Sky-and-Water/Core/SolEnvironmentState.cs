using System;
using UnityEngine;

namespace Sol.Environment
{
    /// <summary>Immutable directional lighting values consumed by environment subsystems.</summary>
    [Serializable]
    public readonly struct SolEnvironmentLightingState
    {
        public readonly Vector3 SunDirection;
        public readonly Vector3 MoonDirection;
        public readonly Color MainLightColor;
        public readonly float MainLightIntensity;
        public readonly float DayFactor;
        public readonly float MoonIllumination;
        public readonly float EclipseFactor;

        public SolEnvironmentLightingState(
            Vector3 sunDirection,
            Vector3 moonDirection,
            Color mainLightColor,
            float mainLightIntensity,
            float dayFactor,
            float moonIllumination,
            float eclipseFactor)
        {
            SunDirection = NormalizeOr(sunDirection, Vector3.up);
            MoonDirection = NormalizeOr(moonDirection, Vector3.down);
            MainLightColor = mainLightColor;
            MainLightIntensity = Mathf.Max(0f, mainLightIntensity);
            DayFactor = Mathf.Clamp01(dayFactor);
            MoonIllumination = Mathf.Clamp01(moonIllumination);
            EclipseFactor = Mathf.Clamp01(eclipseFactor);
        }

        static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
            => value.sqrMagnitude > 0.000001f ? value.normalized : fallback;
    }

    /// <summary>Immutable wind state shared by clouds, fog, vegetation, precipitation, and water.</summary>
    [Serializable]
    public readonly struct SolEnvironmentWindState
    {
        public readonly Vector3 Direction;
        public readonly float Speed;
        public readonly float Turbulence;

        public SolEnvironmentWindState(Vector3 direction, float speed, float turbulence)
        {
            Direction = direction.sqrMagnitude > 0.000001f ? direction.normalized : Vector3.right;
            Speed = Mathf.Max(0f, speed);
            Turbulence = Mathf.Clamp01(turbulence);
        }
    }

    /// <summary>Immutable weather presentation and forcing values.</summary>
    [Serializable]
    public readonly struct SolEnvironmentWeatherState
    {
        public readonly float Cloudiness;
        public readonly float CloudErosion;
        public readonly float Rain;
        public readonly float Snow;
        public readonly float FogBoost;
        public readonly float Mist;
        public readonly float Lightning;
        public readonly float WaterTurbulence;
        public readonly float WaveSpeedMultiplier;

        public SolEnvironmentWeatherState(
            float cloudiness,
            float cloudErosion,
            float rain,
            float snow,
            float fogBoost,
            float mist,
            float lightning,
            float waterTurbulence,
            float waveSpeedMultiplier)
        {
            Cloudiness = Mathf.Clamp01(cloudiness);
            CloudErosion = Mathf.Clamp01(cloudErosion);
            Rain = Mathf.Clamp01(rain);
            Snow = Mathf.Clamp01(snow);
            FogBoost = Mathf.Max(0f, fogBoost);
            Mist = Mathf.Clamp01(mist);
            Lightning = Mathf.Clamp01(lightning);
            WaterTurbulence = Mathf.Clamp01(waterTurbulence);
            WaveSpeedMultiplier = Mathf.Max(0f, waveSpeedMultiplier);
        }
    }

    /// <summary>Slowly changing surface conditions suitable for saving and streaming.</summary>
    [Serializable]
    public readonly struct SolSurfaceConditionState
    {
        public readonly float Wetness;
        public readonly float SnowCover;
        public readonly float TemperatureCelsius;
        public readonly float RelativeHumidity;

        public SolSurfaceConditionState(
            float wetness,
            float snowCover,
            float temperatureCelsius,
            float relativeHumidity)
        {
            Wetness = Mathf.Clamp01(wetness);
            SnowCover = Mathf.Clamp01(snowCover);
            TemperatureCelsius = Mathf.Clamp(temperatureCelsius, -100f, 100f);
            RelativeHumidity = Mathf.Clamp01(relativeHumidity);
        }
    }

    /// <summary>
    /// Canonical immutable state published by <see cref="SolEnvironmentWorld"/>.
    /// Renderers may interpolate presentation, but gameplay consumes this exact tick state.
    /// </summary>
    [Serializable]
    public readonly struct SolEnvironmentState
    {
        public readonly ulong Revision;
        public readonly long SimulationTick;
        public readonly double AbsoluteWorldSeconds;
        public readonly long WorldDayIndex;
        public readonly float ClockHour;
        public readonly SolEnvironmentLightingState Lighting;
        public readonly SolEnvironmentWindState Wind;
        public readonly SolEnvironmentWeatherState Weather;
        public readonly SolSurfaceConditionState Surface;

        public SolEnvironmentState(
            ulong revision,
            long simulationTick,
            double absoluteWorldSeconds,
            long worldDayIndex,
            float clockHour,
            in SolEnvironmentLightingState lighting,
            in SolEnvironmentWindState wind,
            in SolEnvironmentWeatherState weather,
            in SolSurfaceConditionState surface)
        {
            Revision = revision;
            SimulationTick = simulationTick;
            AbsoluteWorldSeconds = Math.Max(0d, absoluteWorldSeconds);
            WorldDayIndex = worldDayIndex;
            ClockHour = Mathf.Repeat(clockHour, 24f);
            Lighting = lighting;
            Wind = wind;
            Weather = weather;
            Surface = surface;
        }
    }

    public enum SolEnvironmentCommandType : byte
    {
        SetPaused,
        SetTimeScale,
        SetSurfaceWetness,
        SetSnowCover,
        SetTemperature,
        SetHumidity,
        AddWorldSeconds,
    }

    /// <summary>Transport-neutral deterministic mutation request.</summary>
    [Serializable]
    public readonly struct SolEnvironmentCommand
    {
        public readonly SolEnvironmentCommandType Type;
        public readonly double Value;
        public readonly ulong Sequence;

        public SolEnvironmentCommand(SolEnvironmentCommandType type, double value, ulong sequence = 0)
        {
            Type = type;
            Value = value;
            Sequence = sequence;
        }
    }

    /// <summary>Complete authoritative state required for save/load and network snapshots.</summary>
    [Serializable]
    public readonly struct SolEnvironmentSnapshot
    {
        public readonly int Version;
        public readonly int ClimateSeed;
        public readonly bool Paused;
        public readonly float TimeScale;
        public readonly SolEnvironmentState State;

        public SolEnvironmentSnapshot(
            int version,
            int climateSeed,
            bool paused,
            float timeScale,
            in SolEnvironmentState state)
        {
            Version = version;
            ClimateSeed = climateSeed;
            Paused = paused;
            TimeScale = Mathf.Max(0f, timeScale);
            State = state;
        }
    }
}
