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

        public static SolWaterWaveSample Evaluate(
            SolWaterProfile profile,
            Vector2 localXZ,
            double time,
            in SolDouble3 logicalOrigin,
            Vector3 windDirection,
            float windStrength,
            float turbulence)
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
                * Mathf.Lerp(0.65f, 1.35f, Mathf.Clamp01(windStrength / 3f));

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
                float amplitude = wave.amplitude * weatherAmplitude;
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
            float shorelineFoam = 0f;
            float shallowAttenuation = EvaluateShorelineAttenuation(
                profile, worldXZ, out float shoreDistance, out bool shorelineValid);
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
            return new SolWaterWaveSample(displacement, normal, velocity,
                Mathf.Max(foam * profile.crestFoamStrength, shorelineFoam));
        }

        internal static float EvaluateShorelineAttenuation(
            SolWaterProfile profile,
            Vector2 logicalXZ,
            out float shoreDistance,
            out bool valid)
        {
            shoreDistance = 0f;
            valid = false;
            if (profile == null || profile.shorelineData == null
                || !profile.shorelineData.isReadable)
                return 1f;

            Vector4 mapping = profile.shorelineDataMapping;
            float u = (logicalXZ.x - mapping.x) / Mathf.Max(0.001f, mapping.z) + 0.5f;
            float v = (logicalXZ.y - mapping.y) / Mathf.Max(0.001f, mapping.w) + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f)
                return 1f;

            Color encoded = profile.shorelineData.GetPixelBilinear(u, v);
            float depth = (encoded.r * 2f - 1f) * profile.shorelineDepthRange;
            shoreDistance = (encoded.g * 2f - 1f) * profile.shorelineDistanceRange;
            valid = true;
            float depthFade = Mathf.SmoothStep(0f, 1f,
                depth / Mathf.Max(0.01f, profile.shallowWaveAttenuationDepth));
            float distanceFade = Mathf.SmoothStep(0f, 1f,
                shoreDistance / Mathf.Max(0.05f, profile.shorelineContactFade * 2f));
            return depthFade * distanceFade;
        }

        public static float EstimateMaximumAmplitude(SolWaterProfile profile)
        {
            if (profile == null || profile.gerstnerWaves == null)
                return 0f;
            float amplitude = 0f;
            for (int i = 0; i < profile.gerstnerWaves.Length; i++)
                amplitude += Mathf.Max(0f, profile.gerstnerWaves[i].amplitude);
            return amplitude * 2f;
        }
    }
}
