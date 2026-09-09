using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sol.Lighting
{
    /// <summary>Identifies the directional light selected to represent the environment.</summary>
    public enum SolDominantLightKind : byte
    {
        None,
        Sun,
        Moon,
        AdditionalCelestial,
    }

    /// <summary>Immutable state for one environment directional light.</summary>
    [Serializable]
    public readonly struct SolDirectionalLightState
    {
        public readonly Light Source;
        public readonly Vector3 Direction;
        public readonly Quaternion Rotation;
        public readonly Color Color;
        public readonly float Intensity;
        public readonly float ShadowStrength;
        public readonly bool Enabled;

        public SolDirectionalLightState(
            Light source,
            Vector3 direction,
            Color color,
            float intensity,
            float shadowStrength,
            bool enabled)
            : this(source, direction, color, intensity, shadowStrength, enabled,
                ResolveRotation(direction))
        {
        }

        public SolDirectionalLightState(
            Light source,
            Vector3 direction,
            Color color,
            float intensity,
            float shadowStrength,
            bool enabled,
            Quaternion rotation)
        {
            Source = source;
            Direction = direction.sqrMagnitude > 0.000001f
                ? direction.normalized
                : Vector3.up;
            Rotation = rotation;
            Color = color;
            Intensity = Mathf.Max(0f, intensity);
            ShadowStrength = Mathf.Clamp01(shadowStrength);
            Enabled = enabled;
        }

        public float Score
        {
            get
            {
                if (!Enabled)
                    return 0f;

                float luminance = Color.r * 0.2126f
                    + Color.g * 0.7152f
                    + Color.b * 0.0722f;
                return Intensity * Mathf.Max(0f, luminance);
            }
        }

        public Color Radiance => Color * Intensity;

        static Quaternion ResolveRotation(Vector3 direction)
        {
            Vector3 normalized = direction.sqrMagnitude > 0.000001f
                ? direction.normalized
                : Vector3.up;
            Vector3 up = Mathf.Abs(Vector3.Dot(normalized, Vector3.up)) > 0.999f
                ? Vector3.forward
                : Vector3.up;
            return Quaternion.LookRotation(-normalized, up);
        }
    }

    /// <summary>Immutable upper-hemisphere, horizon, and ground ambient colors.</summary>
    [Serializable]
    public readonly struct SolTrilightAmbient
    {
        public readonly Color Sky;
        public readonly Color Equator;
        public readonly Color Ground;

        public SolTrilightAmbient(Color sky, Color equator, Color ground)
        {
            Sky = sky;
            Equator = equator;
            Ground = ground;
        }

        public Color Average => (Sky + Equator + Ground) / 3f;
    }

    /// <summary>
    /// Continuous celestial key used by effects that can afford only one directional
    /// lighting evaluation. Radiance is the sum of the live sun and moon contributions;
    /// direction follows their relative luminance without depending on the discrete
    /// RenderSettings.sun selection.
    /// </summary>
    public readonly struct SolCelestialLightBlend
    {
        public readonly Vector3 Direction;
        public readonly Color Radiance;
        public readonly float Intensity;
        public readonly float ShadowStrength;
        public readonly float SunWeight;

        public SolCelestialLightBlend(
            Vector3 direction,
            Color radiance,
            float intensity,
            float shadowStrength,
            float sunWeight)
        {
            Direction = direction.sqrMagnitude > 0.000001f
                ? direction.normalized
                : Vector3.up;
            Radiance = radiance;
            Intensity = Mathf.Max(0f, intensity);
            ShadowStrength = Mathf.Clamp01(shadowStrength);
            SunWeight = Mathf.Clamp01(sunWeight);
        }
    }

    /// <summary>
    /// One coherent, immutable environment-lighting publication. StableAmbient excludes
    /// transient lightning and is the only ambient state eligible for GI and probe updates.
    /// </summary>
    [Serializable]
    public readonly struct SolLightingFrame
    {
        public readonly ulong Revision;
        public readonly SolDirectionalLightState Sun;
        public readonly SolDirectionalLightState Moon;
        public readonly SolDominantLightKind Dominant;
        public readonly SolDirectionalLightState AdditionalDominant;
        public readonly SolTrilightAmbient Ambient;
        public readonly SolTrilightAmbient StableAmbient;
        public readonly float WeatherAttenuation;
        public readonly float CloudShadowStrength;
        public readonly float DayFactor;
        public readonly float Eclipse;
        public readonly float Lightning;

        public SolLightingFrame(
            ulong revision,
            in SolDirectionalLightState sun,
            in SolDirectionalLightState moon,
            SolDominantLightKind dominant,
            in SolTrilightAmbient ambient,
            in SolTrilightAmbient stableAmbient,
            float weatherAttenuation,
            float cloudShadowStrength,
            float dayFactor,
            float eclipse,
            float lightning,
            SolDirectionalLightState additionalDominant = default)
        {
            Revision = revision;
            Sun = sun;
            Moon = moon;
            Dominant = dominant;
            AdditionalDominant = additionalDominant;
            Ambient = ambient;
            StableAmbient = stableAmbient;
            WeatherAttenuation = Mathf.Clamp01(weatherAttenuation);
            CloudShadowStrength = Mathf.Clamp01(cloudShadowStrength);
            DayFactor = Mathf.Clamp01(dayFactor);
            Eclipse = Mathf.Clamp01(eclipse);
            Lightning = Mathf.Clamp01(lightning);
        }

        public Light DominantLight => Dominant switch
        {
            SolDominantLightKind.Sun => Sun.Source,
            SolDominantLightKind.Moon => Moon.Source,
            SolDominantLightKind.AdditionalCelestial => AdditionalDominant.Source,
            _ => null,
        };

        public SolDirectionalLightState DominantState => Dominant switch
        {
            SolDominantLightKind.Sun => Sun,
            SolDominantLightKind.Moon => Moon,
            SolDominantLightKind.AdditionalCelestial => AdditionalDominant,
            _ => default,
        };
    }

    /// <summary>Pure lighting-selection policy shared by runtime code and editor tests.</summary>
    public static class SolLightingResolver
    {
        public const float DominantSwitchHysteresis = 1.1f;

        // Fade over the small band in which a body rises through the horizon.
        // Elevation shapes its intensity; it never gates one body on another's clock.
        public static float CelestialVisibility(float directionY)
        {
            float t = Mathf.InverseLerp(-0.05f, 0.02f, directionY);
            return t * t * (3f - 2f * t);
        }

        public static float CelestialIntensity(float directionY, float minimum, float maximum)
            => Mathf.Lerp(Mathf.Max(0f, minimum), Mathf.Max(0f, maximum),
                Mathf.Sqrt(Mathf.Clamp01(directionY))) * CelestialVisibility(directionY);

        // URP has one directional shadow map. Fade it out around an ownership
        // crossover, while leaving both bodies' emitted radiance untouched.
        public static float MainShadowVisibility(float mainScore, float competingScore)
        {
            if (competingScore <= 0.000001f) return mainScore > 0f ? 1f : 0f;
            float t = Mathf.InverseLerp(1.15f, 2f, mainScore / competingScore);
            return t * t * (3f - 2f * t);
        }

        public static SolDirectionalLightState ResolveDominantState(
            in SolDirectionalLightState sun, in SolDirectionalLightState moon,
            IReadOnlyList<SolDirectionalLightState> additional, Light previous)
        {
            SolDirectionalLightState best = default;
            float bestScore = 0f;
            Consider(sun, previous, ref best, ref bestScore);
            Consider(moon, previous, ref best, ref bestScore);
            for (int i = 0; i < additional.Count; i++)
                Consider(additional[i], previous, ref best, ref bestScore);
            return best;
        }

        static void Consider(in SolDirectionalLightState state, Light previous,
            ref SolDirectionalLightState best, ref float bestScore)
        {
            if (state.Source == null) return;
            float score = state.Score * (state.Source == previous ? DominantSwitchHysteresis : 1f);
            if (score <= bestScore) return;
            best = state;
            bestScore = score;
        }

        public static SolDominantLightKind ResolveDominant(
            in SolDirectionalLightState sun,
            in SolDirectionalLightState moon,
            SolDominantLightKind previous)
        {
            float sunScore = sun.Score;
            float moonScore = moon.Score;
            SolDominantLightKind resolved;

            if (previous == SolDominantLightKind.Sun)
            {
                resolved = moonScore > sunScore * DominantSwitchHysteresis + 0.0001f
                    ? SolDominantLightKind.Moon
                    : SolDominantLightKind.Sun;
            }
            else if (previous == SolDominantLightKind.Moon)
            {
                resolved = sunScore > moonScore * DominantSwitchHysteresis + 0.0001f
                    ? SolDominantLightKind.Sun
                    : SolDominantLightKind.Moon;
            }
            else
            {
                resolved = sunScore >= moonScore
                    ? SolDominantLightKind.Sun
                    : SolDominantLightKind.Moon;
            }

            float winningScore = resolved == SolDominantLightKind.Sun ? sunScore : moonScore;
            return winningScore > 0f ? resolved : SolDominantLightKind.None;
        }

        public static float ResolveWeatherAttenuation(float weatherDim)
            => 1f - Mathf.Clamp01(weatherDim) * 0.75f;

        /// <summary>
        /// Resolves a continuous single-direction approximation of sun and moon lighting.
        /// Disabled or zero-radiance bodies contribute nothing. When neither body emits,
        /// the result is deliberately black so ambient twilight remains the sole light
        /// source instead of introducing a synthetic white key.
        /// </summary>
        public static SolCelestialLightBlend ResolveCelestialBlend(
            in SolDirectionalLightState sun,
            in SolDirectionalLightState moon,
            float dayFactor)
        {
            float sunScore = sun.Score;
            float moonScore = moon.Score;
            float scoreSum = sunScore + moonScore;

            Color sunRadiance = sunScore > 0f ? sun.Radiance : Color.black;
            Color moonRadiance = moonScore > 0f ? moon.Radiance : Color.black;
            float sunIntensity = sunScore > 0f ? sun.Intensity : 0f;
            float moonIntensity = moonScore > 0f ? moon.Intensity : 0f;

            if (scoreSum <= 0.000001f)
            {
                Vector3 unlitDirection = Vector3.Slerp(
                    moon.Direction, sun.Direction, Mathf.Clamp01(dayFactor));
                return new SolCelestialLightBlend(
                    unlitDirection, Color.black, 0f, 0f, dayFactor);
            }

            float sunWeight = sunScore / scoreSum;
            Vector3 direction = Vector3.Slerp(moon.Direction, sun.Direction, sunWeight);
            float shadowStrength = Mathf.Lerp(
                moon.ShadowStrength, sun.ShadowStrength, sunWeight);
            return new SolCelestialLightBlend(
                direction,
                sunRadiance + moonRadiance,
                sunIntensity + moonIntensity,
                shadowStrength,
                sunWeight);
        }

        public static SolDirectionalLightState ResolveDirectional(
            in SolDirectionalLightState candidate,
            float weatherAttenuation)
            => new(
                candidate.Source,
                candidate.Direction,
                candidate.Color,
                candidate.Intensity * Mathf.Clamp01(weatherAttenuation),
                candidate.ShadowStrength,
                candidate.Enabled,
                candidate.Rotation);

        public static float ResolveCloudShadowStrength(float cloudiness, float weatherDim)
        {
            float cloud = Mathf.Clamp01(cloudiness);
            return cloud * Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(weatherDim));
        }
    }
}
