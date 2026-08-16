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
        [ColorUsage(false, true)] public Color shallowScattering = new(0.03f, 0.26f, 0.32f, 1f);
        [ColorUsage(false, true)] public Color deepScattering = new(0.004f, 0.05f, 0.09f, 1f);
        [Tooltip("Absorption coefficient in inverse world metres.")]
        public Vector3 absorption = new(0.18f, 0.0216f, 0.0036f);
        [Min(1f)] public float clarityDistance = 10f;
        [Range(1f, 1.5f)] public float indexOfRefraction = 1.333f;
        [Tooltip("Strength of the physically projected refraction ray.")]
        [Range(0f, 2f)] public float refractionStrength = 0.45f;
        [Tooltip("Maximum world-space distance used to project a refracted ray.")]
        [Min(0.1f)] public float refractionMaximumDistance = 10f;
        [Tooltip("Sub-pixel RGB separation applied along the refracted surface normal.")]
        [Range(0f, 4f)] public float refractionDispersion = 0.15f;
        [Tooltip("Maximum refraction displacement as a normalized fraction of the viewport.")]
        [Range(0.001f, 0.1f)] public float refractionMaximumScreenOffset = 0.025f;
        [Range(0f, 2f)] public float scatteringStrength = 1f;
        [Range(0f, 1f)] public float smoothness = 0.94f;

        [Header("Sun Glitter")]
        [Tooltip("Energy of the specular sun lobe, and the main dispersal control. " +
            "The GGX peak is far above the HDR ceiling, so high values clamp across " +
            "the whole lobe and the highlight fills in as a solid disc; lower values " +
            "let it break into individual glints. Expected to bloom.")]
        [Range(0f, 2f)] public float sunSpecularStrength = 0.08f;
        [Tooltip("Roughness of the sun lobe. Low values give a tight glitter path, " +
            "higher values a broad overcast sheen.")]
        [Range(0.005f, 0.5f)] public float sunSpecularRoughness = 0.04f;
        [Tooltip("HDR ceiling on the sun highlight.")]
        [Range(0f, 16f)] public float sunSpecularMaximum = 3f;
        [Header("Weather Response")]
        [Range(0f, 1f)] public float rainRoughness = 0.18f;
        [Range(0f, 1f)] public float rainNormalStrength = 0.15f;
        [Range(0f, 1f)] public float cloudShadowStrength = 0.35f;
        [Range(0f, 1f)] public float lightningReflectionStrength = 0.75f;

        [Header("Wave Spectrum")]
        [Min(0f)] public float waveSpeed = 1f;
        [Min(0f)] public float windResponse = 1f;
        [Range(0f, 2f)] public float spectralStrength = 0.65f;
        [Tooltip("Horizontal spectral displacement relative to wave height.")]
        [Range(0f, 2f)] public float spectralChoppiness = 0.85f;
        [Tooltip("Horizontal displacement Jacobian below which a crest begins breaking.")]
        [Range(-0.25f, 1f)] public float spectralFoamThreshold = 0.5f;
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
        [Min(0.01f)] public float shorelineDepthRange = 30f;
        [Min(0.01f)] public float shorelineDistanceRange = 60f;
        [Min(0.01f)] public float shallowWaveAttenuationDepth = 5f;
        [Range(0.01f, 2f)] public float shorelineContactFade = 0.4f;
        [Range(0f, 1f)] public float shorelineNormalFlattening = 0.82f;
        [Header("Shoreline Breakers")]
        [Tooltip("Height of signed-distance waves that turn and break toward the shoreline.")]
        [Range(0f, 2f)] public float shorelineBreakerStrength = 0.22f;
        [Tooltip("Water-side distance over which shoreline breakers are generated.")]
        [Min(0.1f)] public float shorelineBreakerWidth = 14f;
        [Min(0.25f)] public float shorelineBreakerWavelength = 6.5f;
        [Min(0f)] public float shorelineBreakerSpeed = 1.2f;
        [Range(0f, 2f)] public float shorelineBreakerChoppiness = 0.35f;
        [Range(0f, 4f)] public float shorelineBreakerFoam = 0.7f;
        public SolGerstnerWave[] gerstnerWaves =
        {
            SolGerstnerWave.Default(18f, 0.35f, new Vector2(0.91f, 0.42f)),
            SolGerstnerWave.Default(9f, 0.16f, new Vector2(-0.36f, 0.93f)),
            SolGerstnerWave.Default(4.5f, 0.07f, new Vector2(0.62f, -0.78f)),
            SolGerstnerWave.Default(2.2f, 0.025f, new Vector2(-0.82f, -0.57f)),
        };

        [Header("Foam / Caustics")]
        [Range(0f, 4f)] public float crestFoamStrength = 0.45f;
        [Range(0f, 4f)] public float shorelineFoamStrength = 0.38f;
        [ColorUsage(false, true)] public Color foamColor = new(1f, 1f, 1f, 1f);
        [Tooltip("Seamless grayscale texture used to break crest and shoreline confidence into foam detail.")]
        public Texture2D foamTexture;
        [Tooltip("World-space foam texture frequency in inverse metres.")]
        [Min(0.0001f)] public float foamTextureScale = 0.075f;
        [Range(0.1f, 8f)] public float foamTextureContrast = 2.4f;
        [Range(0f, 2f)] public float foamBrightness = 1f;
        [Range(0f, 5f)] public float causticStrength = 2f;
        [Min(0.01f)] public float causticScale = 3f;
        [Tooltip("Seamless caustic intensity texture projected in logical world space.")]
        public Texture2D causticTexture;

        [Header("Reflections")]
        [Tooltip("Blend from reflection probes to the current Sol sky gradient before SSR and planar reflections.")]
        [Range(0f, 1f)] public float skyReflectionStrength = 1f;
        [Tooltip("Energy multiplier for the open-sky/probe fallback before valid SSR and planar hits.")]
        [Range(0f, 2f)] public float reflectionIntensity = 1f;
        [Tooltip("Multiplier applied to the open-sky fallback at grazing view angles.")]
        [Range(0f, 1f)] public float horizonReflectionStrength = 1f;
        [Tooltip("How far reflections smear along the view-vertical axis as wind rises. "
            + "A choppy surface scatters a reflected ray across a range of angles, so a "
            + "mirror-sharp reflection reads as unnaturally still water.")]
        [Range(0f, 1f)] public float anisotropicReflectionScale = 0.5f;

        [Header("Underwater")]
        [ColorUsage(false, true)] public Color underwaterHaze = new(0.02f, 0.19f, 0.24f, 1f);
        [Min(0f)] public float underwaterDensity = 0.1f;
        [Range(0f, 1f)] public float underwaterDistortion = 0.05f;

        void OnValidate()
        {
            absorption = Vector3.Max(absorption, Vector3.zero);
            clarityDistance = Mathf.Max(1f, clarityDistance);
            indexOfRefraction = Mathf.Clamp(indexOfRefraction, 1f, 1.5f);
            refractionStrength = Mathf.Clamp(refractionStrength, 0f, 2f);
            refractionMaximumDistance = Mathf.Max(0.1f, refractionMaximumDistance);
            refractionDispersion = Mathf.Clamp(refractionDispersion, 0f, 4f);
            refractionMaximumScreenOffset = Mathf.Clamp(
                refractionMaximumScreenOffset, 0.001f, 0.1f);
            scatteringStrength = Mathf.Clamp(scatteringStrength, 0f, 2f);
            smoothness = Mathf.Clamp01(smoothness);
            sunSpecularStrength = Mathf.Clamp(sunSpecularStrength, 0f, 2f);
            sunSpecularRoughness = Mathf.Clamp(sunSpecularRoughness, 0.005f, 0.5f);
            sunSpecularMaximum = Mathf.Clamp(sunSpecularMaximum, 0f, 16f);
            spectralChoppiness = Mathf.Max(0f, spectralChoppiness);
            spectralFoamGain = Mathf.Max(0f, spectralFoamGain);
            foamTextureScale = Mathf.Max(0.0001f, foamTextureScale);
            foamTextureContrast = Mathf.Clamp(foamTextureContrast, 0.1f, 8f);
            foamBrightness = Mathf.Clamp(foamBrightness, 0f, 2f);
            causticStrength = Mathf.Clamp(causticStrength, 0f, 5f);
            causticScale = Mathf.Max(0.01f, causticScale);
            skyReflectionStrength = Mathf.Clamp01(skyReflectionStrength);
            reflectionIntensity = Mathf.Clamp(reflectionIntensity, 0f, 2f);
            horizonReflectionStrength = Mathf.Clamp01(horizonReflectionStrength);
            anisotropicReflectionScale = Mathf.Clamp01(anisotropicReflectionScale);
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
            shorelineBreakerStrength = Mathf.Clamp(shorelineBreakerStrength, 0f, 2f);
            shorelineBreakerWidth = Mathf.Max(0.1f, shorelineBreakerWidth);
            shorelineBreakerWavelength = Mathf.Max(0.25f, shorelineBreakerWavelength);
            shorelineBreakerSpeed = Mathf.Max(0f, shorelineBreakerSpeed);
            shorelineBreakerChoppiness = Mathf.Clamp(shorelineBreakerChoppiness, 0f, 2f);
            shorelineBreakerFoam = Mathf.Clamp(shorelineBreakerFoam, 0f, 4f);
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
