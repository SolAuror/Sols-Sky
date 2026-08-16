using UnityEngine;

namespace Sol.Water
{
    /// <summary>
    /// CPU-side definition of the spectral distribution used by the GPU ocean.
    /// Kept small and deterministic so authoring validation and future GPU query tests
    /// can use the same physical units as the renderer.
    /// </summary>
    internal static class SolOceanSpectrumMath
    {
        internal const float Gravity = 9.81f;

        internal static float CascadeSize(int cascade) => cascade switch
        {
            0 => 32f,
            1 => 128f,
            2 => 512f,
            _ => 2048f,
        };

        internal static float LowerWaveNumber(int cascade) => cascade switch
        {
            0 => 0.72f,
            1 => 0.16f,
            2 => 0.035f,
            _ => 0.004f,
        };

        internal static float UpperWaveNumber(int cascade) => cascade switch
        {
            0 => 100000f,
            1 => 1.224f,
            2 => 0.272f,
            _ => 0.0595f,
        };

        internal static float Dispersion(float waveNumber)
            => Mathf.Sqrt(Gravity * Mathf.Max(0f, waveNumber));

        internal static float CascadeVisibleDistance(int cascade) => cascade switch
        {
            0 => 40f,
            1 => 160f,
            2 => 800f,
            _ => 4800f,
        };

        internal static float CascadeHeightScale(int cascade) => cascade switch
        {
            0 => 0.5f,
            1 => 0.5f,
            2 => 0.6f,
            _ => 0.9f,
        };

        internal static float DirectionalSpreading(Vector2 waveVector, Vector2 windDirection,
            int cascade, float turbulence, bool negativeFrequency)
        {
            float magnitude = waveVector.magnitude;
            if (magnitude < 0.00001f)
                return 0f;
            windDirection = windDirection.sqrMagnitude > 0.00001f
                ? windDirection.normalized : Vector2.right;
            float alignment = Vector2.Dot(waveVector / magnitude, windDirection);
            if (negativeFrequency)
                alignment = -alignment;
            float lodTurbulence = cascade == 0 ? Mathf.Max(turbulence, 0.5f)
                : cascade == 1 ? Mathf.Max(turbulence, 0.25f) : turbulence;
            lodTurbulence = Mathf.Clamp01(lodTurbulence);
            return 0.6366197724f * alignment * alignment * (1f - lodTurbulence)
                + 0.3366197724f * lodTurbulence;
        }

        internal static float CheckerboardSign(int x, int y)
            => ((x + y) & 1) == 0 ? 1f : -1f;

        internal static Vector2Int CenteredIndex(int x, int y, int resolution)
        {
            int center = Mathf.Max(1, resolution) / 2;
            return new Vector2Int(x - center, y - center);
        }

        internal static float PiersonMoskowitz(float angularFrequency, float windSpeed)
        {
            float omega = Mathf.Max(0.0001f, angularFrequency);
            float speed = Mathf.Max(1.5f, windSpeed);
            float peakOmega = 0.87f * Gravity / speed;
            float ratio = peakOmega / omega;
            return 0.0081f * Gravity * Gravity / Mathf.Pow(omega, 5f)
                * Mathf.Exp(-1.291f * Mathf.Pow(ratio, 4f));
        }

        internal static float CascadeBand(int cascade, float waveNumber)
        {
            float lower = LowerWaveNumber(cascade);
            float upper = UpperWaveNumber(cascade);
            float lowerBlend = SmoothStep(lower, lower * 1.7f, waveNumber);
            float upperBlend = 1f - SmoothStep(upper / 1.7f, upper, waveNumber);
            return Mathf.Clamp01(lowerBlend * upperBlend);
        }

        static float SmoothStep(float from, float to, float value)
        {
            float t = Mathf.Clamp01((value - from) / Mathf.Max(0.000001f, to - from));
            return t * t * (3f - 2f * t);
        }
    }
}
