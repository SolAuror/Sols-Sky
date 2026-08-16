using UnityEngine;

namespace Sol.Water
{
    /// <summary>
    /// CPU mirror of the water volume optics in
    /// <c>Assets/Sky-and-Water/Resources/Water2/SolWaterOptics.hlsl</c>.
    /// Exists so the absorption curve and scattering response can be validated in
    /// EditMode without a GPU, following the <see cref="SolOceanSpectrumMath"/>
    /// precedent. Any change here must be mirrored in the HLSL and vice versa.
    /// </summary>
    public static class SolWaterOpticsMath
    {
        /// <summary>Longest optical path considered when nothing is behind the surface.</summary>
        public const float MaximumRayLength = 5000f;

        /// <summary>Path length beyond which volume colour fully replaces the transmitted scene.</summary>
        public const float MaximumClarity = 50f;

        /// <summary>Per-channel transmittance floor; keeps deep water blue rather than black.</summary>
        public static readonly Vector3 TransmittanceFloor = new(0.0005f, 0.001f, 0.025f);

        /// <summary>HLSL <c>smoothstep</c> semantics, which differ from <see cref="Mathf.SmoothStep"/>.</summary>
        public static float SmoothStep(float edge0, float edge1, float value)
        {
            float t = Mathf.Clamp01((value - edge0) / Mathf.Max(1e-6f, edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Normalizes authored per-metre absorption coefficients into the per-channel
        /// weight the absorption curve expects, preserving the authored hue.
        /// </summary>
        public static Vector3 NormalizeAbsorption(Vector3 authoredAbsorption)
        {
            Vector3 absorptionColor = new(
                Mathf.Max(authoredAbsorption.x, 0.0001f),
                Mathf.Max(authoredAbsorption.y, 0.0001f),
                Mathf.Max(authoredAbsorption.z, 0.0001f));
            return absorptionColor / Mathf.Max(0.0001f, absorptionColor.x);
        }

        /// <summary>
        /// Transmittance through <paramref name="rayLength"/> metres of water in xyz,
        /// and in w the extinction: how much of the transmitted scene has been replaced
        /// by volume scattering.
        /// </summary>
        public static Vector4 ComputeAbsorption(float clarityDistance,
            Vector3 absorptionColor, float rayLength)
        {
            rayLength = Mathf.Max(rayLength, 0f);
            float multiplier = Mathf.Lerp(0.2f, 0.1f,
                Mathf.Clamp01((clarityDistance - MaximumClarity) / MaximumClarity));
            float scaled = -0.75f * Mathf.Pow(rayLength, 1.5f) * multiplier;
            Vector3 transmittance = new(
                Mathf.Max(TransmittanceFloor.x, Mathf.Pow(2f, scaled * absorptionColor.x)),
                Mathf.Max(TransmittanceFloor.y, Mathf.Pow(2f, scaled * absorptionColor.y)),
                Mathf.Max(TransmittanceFloor.z, Mathf.Pow(2f, scaled * absorptionColor.z)));

            float clamped = Mathf.Min(rayLength, Mathf.Min(MaximumClarity, clarityDistance));
            float integralCoefficient = clarityDistance * 0.2f;
            integralCoefficient = Mathf.Max(0.0001f, integralCoefficient * integralCoefficient);
            float extinction = 1f - Mathf.Clamp01(
                Mathf.Pow(2f, -clamped / integralCoefficient + 0.5f));

            return new Vector4(
                Mathf.Clamp01(transmittance.x),
                Mathf.Clamp01(transmittance.y),
                Mathf.Clamp01(transmittance.z),
                Mathf.Clamp01(extinction));
        }

        /// <summary>
        /// Fraction of sunlight reaching the water volume for a given sun direction.
        /// Stays slightly above zero just below the horizon, matching atmospheric
        /// scattering rather than cutting to black at sunset.
        /// </summary>
        public static float SunElevationPhase(Vector3 lightDirectionWS)
            => SmoothStep(-0.25f, 1f, Vector3.Dot(lightDirectionWS, Vector3.up));

        /// <summary>
        /// Volume scattering radiance. Lit by sun elevation and ambient sky so the water
        /// tracks time of day instead of reading as a fixed authored colour.
        /// </summary>
        public static Vector3 VolumeScattering(Color turbidityColor, Color ambientSky,
            Color mainLightColor, Vector3 mainLightDirection, float shadowing,
            float strength)
        {
            float phase = SunElevationPhase(mainLightDirection);
            Vector3 volumeLight = new(
                0.5f * ambientSky.r + mainLightColor.r * phase * shadowing,
                0.5f * ambientSky.g + mainLightColor.g * phase * shadowing,
                0.5f * ambientSky.b + mainLightColor.b * phase * shadowing);
            float gain = 0.5f * Mathf.Max(0f, strength);
            return new Vector3(
                turbidityColor.r * Mathf.Clamp01(volumeLight.x) * gain,
                turbidityColor.g * Mathf.Clamp01(volumeLight.y) * gain,
                turbidityColor.b * Mathf.Clamp01(volumeLight.z) * gain);
        }
    }
}
