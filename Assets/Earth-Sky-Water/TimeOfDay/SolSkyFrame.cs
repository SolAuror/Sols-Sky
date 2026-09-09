using System;
using UnityEngine;

namespace Sol.ToD
{
    public readonly struct SolSkyAtmosphereFrame
    {
        public readonly float DensityMultiplier, StartDistance, MaxDistance, MaxOpacity;
        public readonly float BaseHeight, HeightFalloff, MistBaseHeight, MistHeightFalloff;
        public readonly float NoiseIntensity, NoiseScale, NoiseSpeed;
        public readonly float SkyFogStrength, ZenithFogStrength, HorizonFogStrength;
        public readonly float PhaseAnisotropy, DirectionalScattering, ShadowedScattering;
        public readonly float FogSaturation, AmbientScattering, MaxScatteringLuminance;
        public readonly float LightningScattering, RaymarchDistance, RaymarchJitter;
        public readonly float BilateralDepthThreshold, SpatialFilterStrength;
        public readonly int RaymarchSteps;
        public readonly SolAtmosphereQuality Quality;
        public readonly Color DayScatteringColor, NightScatteringColor;

        internal SolSkyAtmosphereFrame(SolSkyProfile p)
        {
            DensityMultiplier = p.atmosphereDensityMultiplier;
            StartDistance = p.atmosphereStartDistance;
            MaxDistance = p.atmosphereMaxDistance;
            MaxOpacity = p.atmosphereMaxOpacity;
            BaseHeight = p.atmosphereBaseHeight;
            HeightFalloff = p.atmosphereHeightFalloff;
            MistBaseHeight = p.atmosphereMistBaseHeight;
            MistHeightFalloff = p.atmosphereMistHeightFalloff;
            NoiseIntensity = p.atmosphereNoiseIntensity;
            NoiseScale = p.atmosphereNoiseScale;
            NoiseSpeed = p.atmosphereNoiseSpeed;
            SkyFogStrength = p.skyFogStrength;
            ZenithFogStrength = p.zenithFogStrength;
            HorizonFogStrength = p.horizonFogStrength;
            PhaseAnisotropy = p.phaseAnisotropy;
            DirectionalScattering = p.directionalScatteringIntensity;
            ShadowedScattering = p.shadowedScatteringStrength;
            FogSaturation = p.fogSaturation;
            AmbientScattering = p.ambientScatteringIntensity;
            MaxScatteringLuminance = p.maxScatteringLuminance;
            LightningScattering = p.lightningScatteringIntensity;
            RaymarchDistance = p.atmosphereRaymarchDistance;
            RaymarchSteps = p.atmosphereRaymarchStepCount;
            RaymarchJitter = p.atmosphereRaymarchJitter;
            BilateralDepthThreshold = p.atmosphereBilateralDepthThreshold;
            SpatialFilterStrength = p.atmosphereSpatialFilterStrength;
            Quality = p.atmosphereQuality;
            DayScatteringColor = p.dayScatteringColor;
            NightScatteringColor = p.nightScatteringColor;
        }
    }

    /// <summary>Immutable inputs to the pure sky resolver.</summary>
    public readonly struct SolSkyResolveInput
    {
        public readonly Vector3 SunDirection;
        public readonly Vector3 MoonDirection;
        public readonly float SolarEclipse;
        public readonly float LunarEclipse;
        public readonly float WeatherDim;
        public readonly float Cloudiness;
        public readonly float FogBoost;
        public readonly float FogDensityFloor;
        public readonly float Lightning;
        public readonly float CameraAltitude;
        public readonly bool IsMorning;
        public readonly bool AuroraNight;
        public readonly uint Revision;

        public SolSkyResolveInput(Vector3 sunDirection, Vector3 moonDirection,
            float solarEclipse, float lunarEclipse, float weatherDim, float cloudiness,
            float fogBoost, float fogDensityFloor, float lightning, float cameraAltitude,
            bool isMorning, bool auroraNight, uint revision)
        {
            SunDirection = sunDirection.sqrMagnitude > 0f ? sunDirection.normalized : Vector3.up;
            MoonDirection = moonDirection.sqrMagnitude > 0f ? moonDirection.normalized : Vector3.down;
            SolarEclipse = Mathf.Clamp01(solarEclipse);
            LunarEclipse = Mathf.Clamp01(lunarEclipse);
            WeatherDim = Mathf.Clamp01(weatherDim);
            Cloudiness = Mathf.Clamp01(cloudiness);
            FogBoost = Mathf.Max(0f, fogBoost);
            FogDensityFloor = Mathf.Max(0f, fogDensityFloor);
            Lightning = Mathf.Clamp01(lightning);
            CameraAltitude = cameraAltitude;
            IsMorning = isMorning;
            AuroraNight = auroraNight;
            Revision = revision;
        }
    }

    /// <summary>Complete resolved presentation shared by sky, atmosphere, lighting, clouds and water.</summary>
    public readonly struct SolSkyFrame
    {
        public readonly bool IsValid;
        public readonly uint Revision;
        public readonly Color Zenith;
        public readonly Color Horizon;
        public readonly Color Nadir;
        public readonly Color Twilight;
        public readonly Color AntiSolarHorizon;
        public readonly Color FogColor;
        public readonly float FogDensity;
        public readonly Color StableAmbientSky;
        public readonly Color StableAmbientEquator;
        public readonly Color StableAmbientGround;
        public readonly Color PresentedAmbientSky;
        public readonly Color PresentedAmbientEquator;
        public readonly Color PresentedAmbientGround;
        public readonly Vector3 SunDirection;
        public readonly Vector3 MoonDirection;
        public readonly Vector3 DirectionalLightDirection;
        public readonly Color DirectionalLightColor;
        public readonly float DayFactor;
        public readonly float NightFactor;
        public readonly float TwilightFactor;
        public readonly float AltitudeFactor;
        public readonly float StarIntensity;
        public readonly float AuroraIntensity;
        public readonly float SunAngularDiameter;
        public readonly float MoonAngularDiameter;
        public readonly Vector4 GradientParameters;
        public readonly Vector4 DirectionalParameters;
        public readonly SolCloudState AuthoredCloudState;
        public readonly SolSkyAtmosphereFrame Atmosphere;

        internal SolSkyFrame(uint revision, Color zenith, Color horizon, Color nadir,
            Color twilight, Color antiSolarHorizon, Color fogColor, float fogDensity,
            Color stableAmbientSky, Color stableAmbientEquator, Color stableAmbientGround,
            Color presentedAmbientSky, Color presentedAmbientEquator, Color presentedAmbientGround,
            Vector3 sunDirection, Vector3 moonDirection, Vector3 directionalLightDirection,
            Color directionalLightColor, float dayFactor, float nightFactor,
            float twilightFactor, float altitudeFactor, float starIntensity, float auroraIntensity,
            float sunAngularDiameter, float moonAngularDiameter, Vector4 gradientParameters,
            Vector4 directionalParameters, SolCloudState authoredCloudState,
            SolSkyAtmosphereFrame atmosphere)
        {
            IsValid = true;
            Revision = revision;
            Zenith = zenith;
            Horizon = horizon;
            Nadir = nadir;
            Twilight = twilight;
            AntiSolarHorizon = antiSolarHorizon;
            FogColor = fogColor;
            FogDensity = fogDensity;
            StableAmbientSky = stableAmbientSky;
            StableAmbientEquator = stableAmbientEquator;
            StableAmbientGround = stableAmbientGround;
            PresentedAmbientSky = presentedAmbientSky;
            PresentedAmbientEquator = presentedAmbientEquator;
            PresentedAmbientGround = presentedAmbientGround;
            SunDirection = sunDirection;
            MoonDirection = moonDirection;
            DirectionalLightDirection = directionalLightDirection;
            DirectionalLightColor = directionalLightColor;
            DayFactor = dayFactor;
            NightFactor = nightFactor;
            TwilightFactor = twilightFactor;
            AltitudeFactor = altitudeFactor;
            StarIntensity = starIntensity;
            AuroraIntensity = auroraIntensity;
            SunAngularDiameter = sunAngularDiameter;
            MoonAngularDiameter = moonAngularDiameter;
            GradientParameters = gradientParameters;
            DirectionalParameters = directionalParameters;
            AuthoredCloudState = authoredCloudState;
            Atmosphere = atmosphere;
        }

        public Color EvaluateRadiance(Vector3 direction)
        {
            direction = direction.sqrMagnitude > 0f ? direction.normalized : Vector3.up;
            float up = direction.y;
            Vector3 weights = SolSkyResolver.NormalizedGradientWeights(up, GradientParameters);
            float upper = weights.x;
            float horizonWeight = weights.y;
            float lower = weights.z;
            Color result = Zenith * upper + Horizon * horizonWeight + Nadir * lower;
            Vector3 flatDirection = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
            Vector3 flatSun = Vector3.ProjectOnPlane(SunDirection, Vector3.up).normalized;
            float azimuth = flatDirection.sqrMagnitude > 0f && flatSun.sqrMagnitude > 0f
                ? Vector3.Dot(flatDirection, flatSun) : 0f;
            float towardSun = Mathf.Pow(Mathf.Clamp01(azimuth * 0.5f + 0.5f), GradientParameters.w);
            float towardAnti = Mathf.Pow(Mathf.Clamp01(-azimuth * 0.5f + 0.5f), GradientParameters.w);
            result = Color.LerpUnclamped(result, Twilight, TwilightFactor * horizonWeight * towardSun);
            result = Color.LerpUnclamped(result, AntiSolarHorizon,
                TwilightFactor * horizonWeight * towardAnti * 0.42f);
            return Finite(result);
        }

        static Color Finite(Color value) => new(
            float.IsFinite(value.r) ? value.r : 0f,
            float.IsFinite(value.g) ? value.g : 0f,
            float.IsFinite(value.b) ? value.b : 0f, 1f);
    }

    /// <summary>Pure, deterministic profile + simulation-state to presentation resolver.</summary>
    public static class SolSkyResolver
    {
        /// <summary>Normalized zenith/horizon/nadir weights used by CPU validation and HLSL.</summary>
        public static Vector3 NormalizedGradientWeights(float elevationY, Vector4 parameters)
        {
            float upper = Mathf.Pow(Mathf.Clamp01(elevationY), Mathf.Max(0.1f, parameters.x));
            float lower = Mathf.Pow(Mathf.Clamp01(-elevationY), Mathf.Max(0.1f, parameters.y));
            float horizon = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(elevationY)),
                Mathf.Max(0.1f, parameters.z));
            float sum = Mathf.Max(1e-5f, upper + horizon + lower);
            return new Vector3(upper / sum, horizon / sum, lower / sum);
        }

        public static SolSkyFrame Resolve(SolSkyProfile profile, in SolSkyResolveInput input)
        {
            if (profile == null)
                return default;

            float sunElevation = Mathf.Clamp(input.SunDirection.y, -1f, 1f);
            float day = Smooth(-0.055f, 0.18f, sunElevation);
            float night = 1f - Smooth(-0.18f, -0.035f, sunElevation);
            float twilight = Mathf.Clamp01(1f - day - night);
            twilight = Mathf.Max(twilight, (1f - Mathf.Abs(sunElevation) / 0.18f) * (1f - night));
            float altitude = Mathf.Clamp01(Mathf.Max(0f, input.CameraAltitude - profile.atmosphereBaseHeight)
                                            / Mathf.Max(1f, profile.aerialViewHeight));

            Color zenith = Color.LerpUnclamped(profile.nightZenith, profile.dayZenith, day);
            Color horizon = Color.LerpUnclamped(profile.nightHorizon, profile.dayHorizon, day);
            Color nadir = Color.LerpUnclamped(profile.nightNadir, profile.dayNadir, day);
            Color twilightColor = input.IsMorning ? profile.sunriseHorizon : profile.sunsetHorizon;
            zenith = Color.LerpUnclamped(zenith, profile.eclipseZenith, input.SolarEclipse);
            horizon = Color.LerpUnclamped(horizon, profile.eclipseHorizon, input.SolarEclipse);

            float weatherAttenuation = 1f - input.WeatherDim * profile.weatherSkyDimming;
            zenith *= weatherAttenuation;
            horizon *= Mathf.Lerp(weatherAttenuation, 1f, 0.12f);
            nadir *= weatherAttenuation;
            zenith *= 1f - altitude * profile.aerialZenithDarkening;
            horizon *= Mathf.Lerp(1f, 1f + profile.aerialHorizonExtinctionReduction * 0.35f, altitude);

            Color stableSky = zenith * profile.ambientIntensity;
            Color stableEquator = horizon * profile.ambientIntensity;
            Color stableGround = nadir * (profile.ambientIntensity * 0.9f);
            Color flash = Color.white * (input.Lightning * profile.lightningAmbientIntensity);
            Color presentedSky = stableSky + flash;
            Color presentedEquator = stableEquator + flash * 1.25f;
            Color presentedGround = stableGround + flash * 0.65f;
            Color skyFlash = Color.white * (input.Lightning * profile.lightningSkyIntensity);
            Color displayedZenith = zenith + skyFlash * 0.75f;
            Color displayedHorizon = horizon + skyFlash;

            Color neutralFog = Color.LerpUnclamped(profile.fogNightColor, profile.fogDayColor, day)
                             * (1f - input.WeatherDim * 0.15f);
            float neutrality = input.WeatherDim * input.Cloudiness * profile.weatherHorizonNeutrality;
            Color fogColor = Color.LerpUnclamped(horizon, neutralFog, neutrality);
            float fogDensity = Mathf.Max(input.FogDensityFloor,
                Mathf.Lerp(profile.fogNightDensity, profile.fogDayDensity, day) * (1f + input.FogBoost));

            float celestialWeather = 1f - input.WeatherDim * 0.72f;
            float stars = profile.starIntensity * night * celestialWeather * (1f - input.Cloudiness * 0.35f);
            float aurora = profile.enableAurora && input.AuroraNight
                ? profile.auroraIntensity * night * celestialWeather * (1f - input.Cloudiness)
                : 0f;
            bool sunDominant = day >= night;
            Vector3 directionalLightDirection = sunDominant
                ? input.SunDirection : input.MoonDirection;
            Color directionalLightColor = sunDominant
                ? profile.sunColor * Mathf.Lerp(0.08f, 1f, day)
                : profile.moonLitColor * (night * 0.18f);
            directionalLightColor *= celestialWeather * (1f - input.SolarEclipse * 0.85f);

            return new SolSkyFrame(input.Revision, Opaque(displayedZenith), Opaque(displayedHorizon), Opaque(nadir),
                Opaque(twilightColor * profile.twilightIntensity), Opaque(profile.antiSolarHorizon),
                Opaque(fogColor), fogDensity, Opaque(stableSky), Opaque(stableEquator),
                Opaque(stableGround), Opaque(presentedSky), Opaque(presentedEquator),
                Opaque(presentedGround), input.SunDirection, input.MoonDirection,
                directionalLightDirection, Opaque(directionalLightColor), day, night,
                twilight, altitude, stars, aurora, profile.sunAngularDiameter,
                profile.moonAngularDiameter,
                new Vector4(profile.zenithBlend, profile.nadirBlend, profile.horizonPower,
                    profile.twilightAzimuthPower),
                new Vector4(twilight, profile.solarAureoleIntensity * (1f - input.SolarEclipse),
                    profile.solarAureolePower, altitude), profile.AuthoredCloudState,
                new SolSkyAtmosphereFrame(profile));
        }

        static float Smooth(float a, float b, float value)
        {
            float t = Mathf.Clamp01((value - a) / Mathf.Max(1e-5f, b - a));
            return t * t * (3f - 2f * t);
        }

        static Color Opaque(Color value)
        {
            value.r = float.IsFinite(value.r) ? Mathf.Max(0f, value.r) : 0f;
            value.g = float.IsFinite(value.g) ? Mathf.Max(0f, value.g) : 0f;
            value.b = float.IsFinite(value.b) ? Mathf.Max(0f, value.b) : 0f;
            value.a = 1f;
            return value;
        }
    }
}
