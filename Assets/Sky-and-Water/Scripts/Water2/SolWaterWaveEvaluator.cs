using Sol.Environment;
using UnityEngine;

namespace Sol.Water
{
    public readonly struct SolWaterWaveSample
    {
        public readonly Vector3 Displacement;
        public readonly Vector3 Normal;
        public readonly Vector3 Velocity;
        public readonly float Foam;

        public SolWaterWaveSample(Vector3 displacement, Vector3 normal, Vector3 velocity, float foam)
        {
            Displacement = displacement;
            Normal = normal;
            Velocity = velocity;
            Foam = Mathf.Clamp01(foam);
        }
    }

    /// <summary>CPU mirror of SolWaterWaves2.hlsl used by low-tier rendering and immediate queries.</summary>
    public static class SolWaterWaveEvaluator
    {
        const float Gravity = 9.81f;

        /// <summary>
        /// Wind speed, in metres per second, that counts as a full gale for wave
        /// response. SolEnvironmentWorld publishes wind in m/s as the authored 0..3
        /// WeatherProfile multiplier times windStrengthToMetresPerSecond (default 8),
        /// so the authored maximum lands at 24.
        /// </summary>
        public const float WindResponseReferenceSpeed = 24f;

        /// <summary>
        /// Normalised wind response, 0 at dead calm and 1 at gale.
        ///
        /// Three separate normalisations used to exist -- two dividing by 3 and one by 8 --
        /// all written against the old authored 0..3 range but fed metres per second after
        /// the conversion landed. Everything above 3 m/s therefore saturated, so Clear
        /// (2.8 m/s) and Storm (21.2 m/s) drove wave amplitude within about 6% of each
        /// other. Mirrored by SolWaterWindResponse01 in SolWaterWaves2.hlsl; the reference
        /// speed has to stay in step between the two.
        /// </summary>
        public static float WindResponse01(float windSpeedMetresPerSecond)
            => Mathf.Clamp01(windSpeedMetresPerSecond / WindResponseReferenceSpeed);

        public static SolWaterWaveSample Evaluate(
            SolWaterProfile profile,
            Vector2 localXZ,
            double time,
            in SolDouble3 logicalOrigin,
            Vector3 windDirection,
            float windStrength,
            float turbulence)
            => EvaluateCore(profile, localXZ, time, logicalOrigin, windDirection,
                windStrength, turbulence, true);

        internal static SolWaterWaveSample EvaluateFinite(
            SolWaterProfile profile,
            Vector2 localXZ,
            double time,
            in SolDouble3 logicalOrigin,
            Vector3 flowDirection,
            float windStrength,
            float turbulence)
            => EvaluateCore(profile, localXZ, time, logicalOrigin, flowDirection,
                windStrength, turbulence, false);

        static SolWaterWaveSample EvaluateCore(
            SolWaterProfile profile,
            Vector2 localXZ,
            double time,
            in SolDouble3 logicalOrigin,
            Vector3 windDirection,
            float windStrength,
            float turbulence,
            bool evaluateShoreline)
        {
            if (profile == null || profile.gerstnerWaves == null)
                return new SolWaterWaveSample(Vector3.zero, Vector3.up, Vector3.zero, 0f);

            Vector2 worldXZ = localXZ + new Vector2((float)logicalOrigin.X, (float)logicalOrigin.Z);
            Vector3 displacement = Vector3.zero;
            Vector3 tangentX = Vector3.right;
            Vector3 tangentZ = Vector3.forward;
            Vector3 velocity = Vector3.zero;
            float foam = 0f;
            int count = Mathf.Min(profile.gerstnerWaves.Length, 8);
            Vector2 wind = new(windDirection.x, windDirection.z);
            if (wind.sqrMagnitude < 0.0001f)
                wind = Vector2.right;
            wind.Normalize();

            float weatherAmplitude = Mathf.Lerp(1f, 1.8f, Mathf.Clamp01(turbulence))
                * Mathf.Lerp(0.65f, 1.35f, WindResponse01(windStrength));

            // Medium and High render the spectrum instead of Gerstner, and the shader
            // zeroes the Gerstner amplitude outright when a cascade count is set
            // (SolWaterWaves2.hlsl gerstnerGeometryWeight). The CPU has to make the same
            // substitution rather than adding the two: summing them here would put
            // gameplay on a surface that is neither the one on screen nor a plausible
            // sea. Finite bodies are Gerstner-only by design and never take this path.
            Vector3 spectralDisplacement = Vector3.zero;
            Vector3 spectralVelocity = Vector3.zero;
            bool useSpectral = evaluateShoreline
                && profile.spectralStrength > 0.0001f
                && SolWaterFftReadback.TrySampleDisplacement(worldXZ,
                    out spectralDisplacement, out spectralVelocity);
            float gerstnerWeight = useSpectral ? 0f : 1f;

            for (int i = 0; i < count; i++)
            {
                SolGerstnerWave wave = profile.gerstnerWaves[i];
                float wavelength = Mathf.Max(0.01f, wave.wavelength);
                float k = 2f * Mathf.PI / wavelength;
                float angularSpeed = Mathf.Sqrt(Gravity * k) * profile.waveSpeed;
                Vector2 authoredDirection = wave.direction.sqrMagnitude > 0.0001f
                    ? wave.direction.normalized
                    : Vector2.right;
                Vector2 direction = Vector2.Lerp(authoredDirection, wind, Mathf.Clamp01(profile.windResponse * 0.35f)).normalized;
                float phase = k * Vector2.Dot(direction, worldXZ)
                    - angularSpeed * (float)time
                    + wave.phaseOffset;
                float sin = Mathf.Sin(phase);
                float cos = Mathf.Cos(phase);
                float amplitude = wave.amplitude * weatherAmplitude * gerstnerWeight;
                float steepness = Mathf.Clamp01(wave.steepness + turbulence * 0.2f);
                float horizontal = steepness * amplitude;

                displacement.x += direction.x * horizontal * cos;
                displacement.y += amplitude * sin;
                displacement.z += direction.y * horizontal * cos;

                float slope = amplitude * k * cos;
                tangentX += new Vector3(
                    -direction.x * direction.x * horizontal * k * sin,
                    direction.x * slope,
                    -direction.x * direction.y * horizontal * k * sin);
                tangentZ += new Vector3(
                    -direction.x * direction.y * horizontal * k * sin,
                    direction.y * slope,
                    -direction.y * direction.y * horizontal * k * sin);

                velocity.x += direction.x * horizontal * angularSpeed * sin;
                velocity.y -= amplitude * angularSpeed * cos;
                velocity.z += direction.y * horizontal * angularSpeed * sin;
                foam += Mathf.Max(0f, steepness * k * amplitude - 0.22f);
            }

            Vector3 normal = Vector3.Cross(tangentZ, tangentX).normalized;
            if (normal.y < 0f)
                normal = -normal;

            if (useSpectral)
            {
                displacement += spectralDisplacement * profile.spectralStrength;
                velocity += spectralVelocity * profile.spectralStrength;
                if (SolWaterFftReadback.TrySampleNormal(worldXZ, out Vector3 spectralNormal))
                    normal = spectralNormal;
            }

            float shorelineFoam = 0f;
            float shallowAttenuation = evaluateShoreline
                ? EvaluateShorelineAttenuation(profile, worldXZ,
                    out float shoreDistance, out bool shorelineValid)
                : NoShoreline(out shoreDistance, out shorelineValid);
            displacement *= shallowAttenuation;
            velocity *= shallowAttenuation;
            float normalWeight = Mathf.Lerp(1f, shallowAttenuation,
                profile.shorelineNormalFlattening);
            normal = Vector3.Slerp(Vector3.up, normal, normalWeight).normalized;
            if (shorelineValid)
            {
                shorelineFoam = (1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.Abs(shoreDistance) / Mathf.Max(0.01f, profile.shorelineFoamWidth)))
                    * profile.shorelineFoamStrength;
            }
            float finalFoam = Mathf.Max(foam * profile.crestFoamStrength, shorelineFoam);
            if (evaluateShoreline)
            {
                EvaluateShorelineBreaker(profile, worldXZ, time, windStrength,
                    out Vector3 breakerDisplacement, out Vector3 breakerNormal,
                    out Vector3 breakerVelocity, out float breakerFoam);
                displacement += breakerDisplacement;
                velocity += breakerVelocity;
                normal = (normal + breakerNormal - Vector3.up).normalized;
                finalFoam = Mathf.Max(finalFoam, breakerFoam);
            }
            return new SolWaterWaveSample(displacement, normal, velocity,
                finalFoam);
        }

        static float NoShoreline(out float shoreDistance, out bool valid)
        {
            shoreDistance = 0f;
            valid = false;
            return 1f;
        }

        internal static float EvaluateShorelineAttenuation(
            SolWaterProfile profile,
            Vector2 logicalXZ,
            out float shoreDistance,
            out bool valid)
        {
            shoreDistance = 0f;
            valid = false;
            if (!TrySampleShorelineData(profile, logicalXZ,
                out float depth, out shoreDistance))
                return 1f;
            valid = true;
            float depthFade = Mathf.SmoothStep(0f, 1f,
                depth / Mathf.Max(0.01f, profile.shallowWaveAttenuationDepth));
            float distanceFade = Mathf.SmoothStep(0f, 1f,
                shoreDistance / Mathf.Max(0.05f, profile.shorelineContactFade * 2f));
            return depthFade * distanceFade;
        }

        internal static void EvaluateShorelineBreaker(
            SolWaterProfile profile,
            Vector2 logicalXZ,
            double time,
            float windStrength,
            out Vector3 displacement,
            out Vector3 normal,
            out Vector3 velocity,
            out float foam)
        {
            displacement = Vector3.zero;
            normal = Vector3.up;
            velocity = Vector3.zero;
            foam = 0f;
            if (!TrySampleShorelineData(profile, logicalXZ,
                    out float depth, out float shoreDistance)
                || profile.shorelineBreakerStrength <= 0f
                || depth <= 0f || shoreDistance <= 0f)
                return;

            float width = Mathf.Max(0.1f, profile.shorelineBreakerWidth);
            float normalizedDistance = Mathf.Clamp01(shoreDistance / width);
            float contactEnvelope = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(0.015f, 0.16f, normalizedDistance));
            float offshoreEnvelope = 1f - Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(0.55f, 1f, normalizedDistance));
            float depthEnvelope = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(0.02f,
                    Mathf.Max(0.25f, profile.shallowWaveAttenuationDepth * 0.5f), depth));
            float envelope = contactEnvelope * offshoreEnvelope * depthEnvelope;
            if (envelope <= 0.0001f)
                return;

            Vector4 mapping = profile.shorelineDataMapping;
            Texture2D data = profile.shorelineData;
            float sampleRadius = Mathf.Clamp(Mathf.Max(
                mapping.z / Mathf.Max(1, data.width),
                mapping.w / Mathf.Max(1, data.height)), 0.25f, 4f);
            TrySampleShorelineData(profile, logicalXZ - Vector2.right * sampleRadius,
                out _, out float distanceLeft);
            TrySampleShorelineData(profile, logicalXZ + Vector2.right * sampleRadius,
                out _, out float distanceRight);
            TrySampleShorelineData(profile, logicalXZ - Vector2.up * sampleRadius,
                out _, out float distanceDown);
            TrySampleShorelineData(profile, logicalXZ + Vector2.up * sampleRadius,
                out _, out float distanceUp);
            Vector2 waterDirection = new(distanceRight - distanceLeft,
                distanceUp - distanceDown);
            waterDirection = waterDirection.sqrMagnitude > 0.0001f
                ? waterDirection.normalized : Vector2.right;
            Vector2 shoreDirection = -waterDirection;
            Vector2 alongShore = new(-waterDirection.y, waterDirection.x);
            float wavelength = Mathf.Max(0.25f, profile.shorelineBreakerWavelength);
            float waveNumber = 2f * Mathf.PI / wavelength;
            float angularSpeed = waveNumber * Mathf.Max(0f, profile.shorelineBreakerSpeed);
            float alongCoordinate = Vector2.Dot(logicalXZ, alongShore);
            float phaseWarp = Mathf.Sin(alongCoordinate * waveNumber * 0.23f
                + Mathf.Sin(alongCoordinate * 0.071f) * 1.7f) * 0.42f;
            float phase = waveNumber * shoreDistance + angularSpeed * (float)time + phaseWarp;
            float sine = Mathf.Sin(phase);
            float cosine = Mathf.Cos(phase);
            float amplitude = profile.shorelineBreakerStrength * envelope
                * Mathf.Lerp(0.65f, 1.15f, WindResponse01(windStrength));
            float choppiness = Mathf.Max(0f, profile.shorelineBreakerChoppiness);

            displacement = new Vector3(
                shoreDirection.x * choppiness * amplitude * cosine,
                amplitude * sine,
                shoreDirection.y * choppiness * amplitude * cosine);
            velocity = new Vector3(
                waterDirection.x * choppiness * amplitude * angularSpeed * sine,
                amplitude * angularSpeed * cosine,
                waterDirection.y * choppiness * amplitude * angularSpeed * sine);
            float slope = amplitude * waveNumber * cosine;
            normal = new Vector3(-waterDirection.x * slope, 1f,
                -waterDirection.y * slope).normalized;
            float crest = Mathf.Pow(Mathf.Clamp01(sine * 0.5f + 0.5f), 6f);
            foam = crest * envelope * Mathf.Max(0f, profile.shorelineBreakerFoam);
        }

        static bool TrySampleShorelineData(SolWaterProfile profile, Vector2 logicalXZ,
            out float depth, out float shoreDistance)
        {
            depth = 0f;
            shoreDistance = 0f;
            if (profile == null || profile.shorelineData == null
                || !profile.shorelineData.isReadable)
                return false;
            Vector4 mapping = profile.shorelineDataMapping;
            float u = (logicalXZ.x - mapping.x) / Mathf.Max(0.001f, mapping.z) + 0.5f;
            float v = (logicalXZ.y - mapping.y) / Mathf.Max(0.001f, mapping.w) + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f)
                return false;
            Color encoded = profile.shorelineData.GetPixelBilinear(u, v);
            depth = (encoded.r * 2f - 1f) * profile.shorelineDepthRange;
            shoreDistance = (encoded.g * 2f - 1f) * profile.shorelineDistanceRange;
            return true;
        }

        /// <summary>
        /// Worst-case vertical reach of the surface, used to size culling bounds and the
        /// crack-hiding skirts.
        ///
        /// Summing the authored Gerstner amplitudes is only right on the Low tier. Medium
        /// and High zero those amplitudes and drive the surface from the spectrum, so a
        /// Gerstner-only estimate reported about 1.2 m while a storm sea was several
        /// metres tall. Everything derived from it was then too small: patches whose
        /// displaced water was plainly on screen failed the frustum test and vanished, and
        /// the skirts sat at their 8 cm floor and could not bridge the gap between
        /// neighbouring detail levels. Both got worse as the wind rose, which is why the
        /// tearing showed up during weather.
        /// </summary>
        public static float EstimateMaximumAmplitude(SolWaterProfile profile,
            float windSpeed = 0f, bool spectral = false)
        {
            if (profile == null)
                return 0f;

            float gerstner = 0f;
            if (profile.gerstnerWaves != null)
            {
                for (int i = 0; i < profile.gerstnerWaves.Length; i++)
                    gerstner += Mathf.Max(0f, profile.gerstnerWaves[i].amplitude);
            }
            gerstner *= 2f;
            if (!spectral)
                return gerstner;

            // Pierson-Moskowitz significant wave height for a fully developed sea,
            // Hs = 0.21 * U^2 / g. Individual crests in that sea reach roughly twice Hs,
            // and bounds are far cheaper to overestimate than to underestimate.
            float significantHeight = 0.21f * windSpeed * windSpeed / Gravity;
            float spectralReach = significantHeight * 2f
                * Mathf.Max(0f, profile.spectralStrength);
            return Mathf.Max(gerstner, spectralReach);
        }
    }
}
