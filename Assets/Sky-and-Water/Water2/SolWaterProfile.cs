using System;
using UnityEngine;

namespace Sol.Water
{
    [Serializable]
    public struct SolGerstnerWave
    {
        [Min(0.01f)] public float wavelength;
        [Min(0f)] public float amplitude;
        [Range(0f, 1f)] public float steepness;
        public Vector2 direction;
        public float phaseOffset;

        public static SolGerstnerWave Default(float wavelength, float amplitude, Vector2 direction)
            => new()
            {
                wavelength = wavelength,
                amplitude = amplitude,
                steepness = 0.55f,
                direction = direction.normalized,
            };
    }

    [CreateAssetMenu(menuName = "Sol/Water/Water Profile", fileName = "Sol Water Profile")]
    public sealed class SolWaterProfile : ScriptableObject
    {
        [Header("Optics")]
        [ColorUsage(false, true)] public Color shallowScattering = new(0.08f, 0.48f, 0.55f, 1f);
        [ColorUsage(false, true)] public Color deepScattering = new(0.005f, 0.08f, 0.12f, 1f);
        [Tooltip("Absorption coefficient in inverse world metres.")]
        public Vector3 absorption = new(0.18f, 0.07f, 0.035f);
        [Min(1f)] public float clarityDistance = 18f;
        [Range(1f, 1.5f)] public float indexOfRefraction = 1.333f;
        [Range(0f, 2f)] public float scatteringStrength = 0.8f;
        [Range(0f, 1f)] public float smoothness = 0.92f;
        [Header("Weather Response")]
        [Range(0f, 1f)] public float rainRoughness = 0.18f;
        [Range(0f, 1f)] public float rainNormalStrength = 0.15f;
        [Range(0f, 1f)] public float cloudShadowStrength = 0.35f;
        [Range(0f, 1f)] public float lightningReflectionStrength = 0.75f;

        [Header("Wave Spectrum")]
        [Min(0f)] public float waveSpeed = 1f;
        [Min(0f)] public float windResponse = 1f;
        [Range(0f, 2f)] public float spectralStrength = 0.35f;
        [Tooltip("Horizontal spectral displacement relative to wave height.")]
        [Range(0f, 2f)] public float spectralChoppiness = 0.85f;
        [Tooltip("Horizontal displacement Jacobian below which a crest begins breaking.")]
        [Range(-0.25f, 1f)] public float spectralFoamThreshold = 0.35f;
        [Tooltip("Gain applied after the breaking-wave Jacobian threshold.")]
        [Range(0f, 8f)] public float spectralFoamGain = 2.5f;
        [Header("Authored Shoreline")]
        public Texture2D shorelineMask;
        [Tooltip("World XZ center and size of shorelineMask. White denotes shoreline influence.")]
        public Vector4 shorelineMaskMapping = new(0f, 0f, 100f, 100f);
        [Range(0f, 4f)] public float shorelineMaskStrength = 1f;
        [Min(0.001f)] public float shorelineFoamWidth = 2f;
        [Range(0f, 1f)] public float shorelineWetness = 0.25f;
        [Tooltip("Optional baked terrain shoreline data. R stores signed vertical water depth and G stores signed horizontal shore distance.")]
        public Texture2D shorelineData;
        [Tooltip("World XZ center and size covered by shorelineData.")]
        public Vector4 shorelineDataMapping = new(0f, 0f, 100f, 100f);
        [Min(0.01f)] public float shorelineDepthRange = 20f;
        [Min(0.01f)] public float shorelineDistanceRange = 50f;
        [Min(0.01f)] public float shallowWaveAttenuationDepth = 4f;
        [Range(0.01f, 2f)] public float shorelineContactFade = 0.3f;
        [Range(0f, 1f)] public float shorelineNormalFlattening = 0.8f;
        public SolGerstnerWave[] gerstnerWaves =
        {
            SolGerstnerWave.Default(18f, 0.35f, new Vector2(0.91f, 0.42f)),
            SolGerstnerWave.Default(9f, 0.16f, new Vector2(-0.36f, 0.93f)),
            SolGerstnerWave.Default(4.5f, 0.07f, new Vector2(0.62f, -0.78f)),
            SolGerstnerWave.Default(2.2f, 0.025f, new Vector2(-0.82f, -0.57f)),
        };

        [Header("Foam / Caustics")]
        [Range(0f, 4f)] public float crestFoamStrength = 0.28f;
        [Range(0f, 4f)] public float shorelineFoamStrength = 0.38f;
        [ColorUsage(false, true)] public Color foamColor = new(0.82f, 0.9f, 0.92f, 1f);
        [Tooltip("Seamless grayscale texture used to break crest and shoreline confidence into foam detail.")]
        public Texture2D foamTexture;
        [Tooltip("World-space foam texture frequency in inverse metres.")]
        [Min(0.0001f)] public float foamTextureScale = 0.055f;
        [Range(0.1f, 8f)] public float foamTextureContrast = 2.4f;
        [Range(0f, 2f)] public float foamBrightness = 0.62f;
        [Range(0f, 5f)] public float causticStrength = 1.2f;
        [Min(0.01f)] public float causticScale = 3f;

        [Header("Reflections")]
        [Tooltip("Blend from reflection probes to the current Sol sky gradient before SSR and planar reflections.")]
        [Range(0f, 1f)] public float skyReflectionStrength = 0.8f;

        [Header("Underwater")]
        [ColorUsage(false, true)] public Color underwaterHaze = new(0.015f, 0.18f, 0.22f, 1f);
        [Min(0f)] public float underwaterDensity = 0.12f;
        [Range(0f, 1f)] public float underwaterDistortion = 0.05f;

        void OnValidate()
        {
            absorption = Vector3.Max(absorption, Vector3.zero);
            spectralChoppiness = Mathf.Max(0f, spectralChoppiness);
            spectralFoamGain = Mathf.Max(0f, spectralFoamGain);
            foamTextureScale = Mathf.Max(0.0001f, foamTextureScale);
            foamTextureContrast = Mathf.Clamp(foamTextureContrast, 0.1f, 8f);
            foamBrightness = Mathf.Clamp(foamBrightness, 0f, 2f);
            skyReflectionStrength = Mathf.Clamp01(skyReflectionStrength);
            shorelineMaskStrength = Mathf.Max(0f, shorelineMaskStrength);
            shorelineFoamWidth = Mathf.Max(0.001f, shorelineFoamWidth);
            shorelineMaskMapping.z = Mathf.Max(0.001f, shorelineMaskMapping.z);
            shorelineMaskMapping.w = Mathf.Max(0.001f, shorelineMaskMapping.w);
            shorelineDataMapping.z = Mathf.Max(0.001f, shorelineDataMapping.z);
            shorelineDataMapping.w = Mathf.Max(0.001f, shorelineDataMapping.w);
            shorelineDepthRange = Mathf.Max(0.01f, shorelineDepthRange);
            shorelineDistanceRange = Mathf.Max(0.01f, shorelineDistanceRange);
            shallowWaveAttenuationDepth = Mathf.Max(0.01f, shallowWaveAttenuationDepth);
            shorelineContactFade = Mathf.Clamp(shorelineContactFade, 0.01f, 2f);
            shorelineNormalFlattening = Mathf.Clamp01(shorelineNormalFlattening);
            if (gerstnerWaves == null || gerstnerWaves.Length == 0)
                gerstnerWaves = new[] { SolGerstnerWave.Default(12f, 0.25f, Vector2.right) };
            for (int i = 0; i < gerstnerWaves.Length; i++)
            {
                SolGerstnerWave wave = gerstnerWaves[i];
                wave.wavelength = Mathf.Max(0.01f, wave.wavelength);
                wave.amplitude = Mathf.Max(0f, wave.amplitude);
                wave.direction = wave.direction.sqrMagnitude > 0.0001f ? wave.direction.normalized : Vector2.right;
                gerstnerWaves[i] = wave;
            }
        }
    }
}
