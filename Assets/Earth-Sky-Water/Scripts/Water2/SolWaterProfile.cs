using System;
using UnityEngine;

namespace Sol.Water
{
    [Serializable]
    public struct SolGerstnerWave
    {
        [Tooltip("Crest-to-crest spacing in metres. Also sets how fast the wave travels: "
            + "long waves outrun short ones.")]
        [Min(0.01f)] public float wavelength;
        [Tooltip("Crest height above the mean surface, in metres.")]
        [Min(0f)] public float amplitude;
        [Tooltip("How far the crest is pinched horizontally. 0 is a sine wave, 1 is "
            + "sharp enough to break.")]
        [Range(0f, 1f)] public float steepness;
        [Tooltip("Travel direction on the world XZ plane. Normalized on validate.")]
        public Vector2 direction;
        [Tooltip("Phase shift in radians. Use it to stop two waves of the same "
            + "wavelength lining up.")]
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

    /// <summary>
    /// Authored look and behaviour of one body of water. Assign it to a
    /// <see cref="SolWaterBody"/>, or to <see cref="SolWaterWorld"/> as the scene default.
    ///
    /// Inspector groups run in the order the water is actually composed: what colour the
    /// volume is, how the surface bends and reflects light, how it moves, where it meets
    /// land, and finally what weather and a submerged camera do to all of it.
    /// </summary>
    [CreateAssetMenu(menuName = "Sol/Water/Water Profile", fileName = "Sol Water Profile")]
    public sealed class SolWaterProfile : ScriptableObject
    {
        [Header("Water Colour")]
        [Tooltip("Scattered colour of shallow water. HDR. This is the hue the volume "
            + "returns to the eye, not the hue of the water itself.")]
        [ColorUsage(false, true)] public Color shallowScattering = new(0.03f, 0.26f, 0.32f, 1f);
        [Tooltip("Scattered colour of deep water, blended in as the optical path grows.")]
        [ColorUsage(false, true)] public Color deepScattering = new(0.004f, 0.05f, 0.09f, 1f);
        [Tooltip("Absorption coefficient in inverse world metres, per RGB channel. Red "
            + "is absorbed first, which is what turns deep water blue.")]
        public Vector3 absorption = new(0.18f, 0.0216f, 0.0036f);
        [Tooltip("How far you can see through the water, in metres, before the volume "
            + "colour has fully replaced the transmitted scene.")]
        [Min(1f)] public float clarityDistance = 10f;
        [Tooltip("Brightness of the scattered volume colour. Raise for milky water, "
            + "lower for clear.")]
        [Range(0f, 2f)] public float scatteringStrength = 1f;

        [Header("Surface Optics")]
        [Tooltip("Index of refraction. 1.333 is water. Higher bends the refracted view "
            + "further and strengthens the Fresnel rim.")]
        [Range(1f, 1.5f)] public float indexOfRefraction = 1.333f;
        [Tooltip("Surface smoothness. High values give mirror reflections and a tight "
            + "sun lobe; low values a diffuse sheen.")]
        [Range(0f, 1f)] public float smoothness = 0.94f;

        [Header("Refraction")]
        [Tooltip("Strength of the physically projected refraction ray.")]
        [Range(0f, 2f)] public float refractionStrength = 0.45f;
        [Tooltip("Maximum world-space distance used to project a refracted ray. This "
            + "bounds the screen-space offset only, not the optical path length.")]
        [Min(0.1f)] public float refractionMaximumDistance = 10f;
        [Tooltip("Sub-pixel RGB separation applied along the refracted surface normal.")]
        [Range(0f, 4f)] public float refractionDispersion = 0.15f;
        [Tooltip("Maximum refraction displacement as a normalized fraction of the "
            + "viewport. Larger values bend further but leak across scene depth edges.")]
        [Range(0.001f, 0.1f)] public float refractionMaximumScreenOffset = 0.025f;

        [Header("Sun Glitter")]
        [Tooltip("Energy of the specular sun lobe, and the main dispersal control. "
            + "The GGX peak is far above the HDR ceiling, so high values clamp across "
            + "the whole lobe and the highlight fills in as a solid disc; lower values "
            + "let it break into individual glints. Expected to bloom.")]
        [Range(0f, 2f)] public float sunSpecularStrength = 0.08f;
        [Tooltip("Roughness of the sun lobe. Low values give a tight glitter path, "
            + "higher values a broad overcast sheen.")]
        [Range(0.005f, 0.5f)] public float sunSpecularRoughness = 0.04f;
        [Tooltip("HDR ceiling on the sun highlight.")]
        [Range(0f, 16f)] public float sunSpecularMaximum = 3f;

        [Header("Reflections")]
        [Tooltip("Blend from reflection probes to the current Sol sky gradient, applied "
            + "before screen-space and planar reflections.")]
        [Range(0f, 1f)] public float skyReflectionStrength = 1f;
        [Tooltip("Energy multiplier for the open-sky and probe fallback, used wherever "
            + "there is no valid screen-space or planar hit.")]
        [Range(0f, 2f)] public float reflectionIntensity = 1f;
        [Tooltip("Multiplier applied to the open-sky fallback at grazing view angles.")]
        [Range(0f, 1f)] public float horizonReflectionStrength = 1f;
        [Tooltip("How far reflections smear along the view-vertical axis as wind rises. "
            + "A choppy surface scatters a reflected ray across a range of angles, so a "
            + "mirror-sharp reflection reads as unnaturally still water.")]
        [Range(0f, 1f)] public float anisotropicReflectionScale = 0.5f;

        [Header("Wave Spectrum")]
        [Tooltip("Multiplier on how fast waves travel. It scales the whole dispersion "
            + "relation, so long waves speed up more than short ones.")]
        [Min(0f)] public float waveSpeed = 1f;
        [Tooltip("How strongly wind turns the authored wave directions downwind. "
            + "0 keeps the authored headings exactly.")]
        [Min(0f)] public float windResponse = 1f;
        [Tooltip("Height of the FFT spectrum on the Medium and High tiers. Zero falls "
            + "back to the authored Gerstner set below.")]
        [Range(0f, 2f)] public float spectralStrength = 0.65f;
        [Tooltip("Horizontal spectral displacement relative to wave height. Higher "
            + "values sharpen crests toward breaking.")]
        [Range(0f, 2f)] public float spectralChoppiness = 0.85f;
        [Tooltip("Horizontal displacement Jacobian below which a crest begins breaking.")]
        [Range(-0.25f, 1f)] public float spectralFoamThreshold = 0.5f;
        [Tooltip("Gain applied after the breaking-wave Jacobian threshold.")]
        [Range(0f, 8f)] public float spectralFoamGain = 2.5f;

        [Header("Gerstner Waves")]
        [Tooltip("Authored wave train. Used directly on the Low tier and on every finite "
            + "body (lakes, rivers, pools); Medium and High replace it with the FFT "
            + "spectrum on the ocean. Only the first eight entries are read.")]
        public SolGerstnerWave[] gerstnerWaves =
        {
            SolGerstnerWave.Default(18f, 0.35f, new Vector2(0.91f, 0.42f)),
            SolGerstnerWave.Default(9f, 0.16f, new Vector2(-0.36f, 0.93f)),
            SolGerstnerWave.Default(4.5f, 0.07f, new Vector2(0.62f, -0.78f)),
            SolGerstnerWave.Default(2.2f, 0.025f, new Vector2(-0.82f, -0.57f)),
        };

        [Header("Shoreline - Terrain Data")]
        [Tooltip("Fallback shoreline texture, used only where no SolTerrainShoreline is "
            + "generating one. R stores signed vertical water depth, G stores signed "
            + "horizontal shore distance. Nothing in the project produces this any more: "
            + "the ocean's field is built from the live terrain at runtime, which is what "
            + "keeps it in step with sculpting and with the water level. Leave it empty "
            + "unless a scene has no terrain to measure against.")]
        public Texture2D shorelineData;
        [Tooltip("World XZ centre and size covered by shorelineData. Ignored while a live "
            + "field is being generated, which supplies its own mapping.")]
        public Vector4 shorelineDataMapping = new(0f, 0f, 100f, 100f);
        [Tooltip("Metres of depth the R channel's full range encodes. Applies to the live "
            + "field as well as the fallback texture, and a change to it rebuilds the field.")]
        [Min(0.01f)] public float shorelineDepthRange = 30f;
        [Tooltip("Metres of shore distance the G channel's full range encodes. Also the "
            + "distance beyond which the field stops resolving a shoreline at all, so it "
            + "has to comfortably exceed the breaker width below.")]
        [Min(0.01f)] public float shorelineDistanceRange = 60f;
        [Tooltip("Depth over which open-water waves are flattened as they run aground. "
            + "This is what stops swell pushing the surface through the beach.")]
        [Min(0.01f)] public float shallowWaveAttenuationDepth = 5f;
        [Tooltip("Width of the soft fade where the water surface meets land, in metres.")]
        [Range(0.01f, 2f)] public float shorelineContactFade = 0.4f;
        [Tooltip("How far the surface normal is flattened toward vertical in shallow "
            + "water. 1 removes all wave shading at the waterline.")]
        [Range(0f, 1f)] public float shorelineNormalFlattening = 0.82f;

        [Header("Shoreline - Authored Mask")]
        [Tooltip("Optional painted override. White denotes shoreline influence. Use it "
            + "where no baked shoreline data exists.")]
        public Texture2D shorelineMask;
        [Tooltip("World XZ centre and size of shorelineMask.")]
        public Vector4 shorelineMaskMapping = new(0f, 0f, 100f, 100f);
        [Tooltip("Weight of the painted mask against the baked data.")]
        [Range(0f, 4f)] public float shorelineMaskStrength = 1f;
        [Tooltip("Width of the foam band along the shoreline, in metres.")]
        [Min(0.001f)] public float shorelineFoamWidth = 2f;
        [Tooltip("How much the shoreline darkens the surface as wet sand shows through.")]
        [Range(0f, 1f)] public float shorelineWetness = 0.25f;

        [Header("Shoreline - Breakers")]
        [Tooltip("Height of signed-distance waves that turn and break toward the "
            + "shoreline. Needs baked shoreline data above to do anything.")]
        [Range(0f, 2f)] public float shorelineBreakerStrength = 0.22f;
        [Tooltip("Water-side distance over which shoreline breakers are generated.")]
        [Min(0.1f)] public float shorelineBreakerWidth = 14f;
        [Tooltip("Crest-to-crest spacing of the breaker train, in metres.")]
        [Min(0.25f)] public float shorelineBreakerWavelength = 6.5f;
        [Tooltip("How fast breakers run toward the shore, in metres per second.")]
        [Min(0f)] public float shorelineBreakerSpeed = 1.2f;
        [Tooltip("Shoreward horizontal displacement of the breaker crest, as a fraction "
            + "of its height.")]
        [Range(0f, 2f)] public float shorelineBreakerChoppiness = 0.35f;
        [Tooltip("Foam generated on the breaker crest.")]
        [Range(0f, 4f)] public float shorelineBreakerFoam = 0.7f;

        [Header("Foam")]
        [Tooltip("Foam produced by breaking crests out on open water.")]
        [Range(0f, 4f)] public float crestFoamStrength = 0.45f;
        [Tooltip("Foam produced where the surface meets land.")]
        [Range(0f, 4f)] public float shorelineFoamStrength = 0.38f;
        [Tooltip("Tint of lit foam. HDR.")]
        [ColorUsage(false, true)] public Color foamColor = new(1f, 1f, 1f, 1f);
        [Tooltip("Seamless grayscale texture used to break crest and shoreline "
            + "confidence into foam detail.")]
        public Texture2D foamTexture;
        [Tooltip("World-space foam texture frequency in inverse metres.")]
        [Min(0.0001f)] public float foamTextureScale = 0.075f;
        [Tooltip("Contrast applied to the foam texture. Higher values break the wash "
            + "into distinct patches.")]
        [Range(0.1f, 8f)] public float foamTextureContrast = 2.4f;
        [Tooltip("Overall foam brightness.")]
        [Range(0f, 2f)] public float foamBrightness = 1f;

        [Header("Caustics")]
        [Tooltip("Authored strength of the caustic pattern cast onto the sea bed. Use "
            + "the renderer feature's CausticResponse debug mode to read the "
            + "brightening and darkening headroom directly.")]
        [Range(0f, 5f)] public float causticStrength = 2f;
        [Tooltip("World size of one caustic cell, in metres. Used only by the authored "
            + "texture fallback; live FFT caustics use the cascade domains instead.")]
        [Min(0.01f)] public float causticScale = 3f;
        [Tooltip("Seamless caustic intensity texture. Used on the Low tier and wherever "
            + "live FFT caustics are unavailable.")]
        public Texture2D causticTexture;

        [Header("Weather Response")]
        [Tooltip("How much rain roughens the surface.")]
        [Range(0f, 1f)] public float rainRoughness = 0.18f;
        [Tooltip("Strength of the rain ripple normal detail.")]
        [Range(0f, 1f)] public float rainNormalStrength = 0.15f;
        [Tooltip("How much cloud cover darkens sun-driven water lighting.")]
        [Range(0f, 1f)] public float cloudShadowStrength = 0.35f;
        [Tooltip("How brightly lightning flashes register in the surface reflection.")]
        [Range(0f, 1f)] public float lightningReflectionStrength = 0.75f;

        [Header("Underwater")]
        [Tooltip("Haze colour seen from a submerged camera. HDR.")]
        [ColorUsage(false, true)] public Color underwaterHaze = new(0.02f, 0.19f, 0.24f, 1f);
        [Tooltip("Fog density below the surface. It stands in for clarity distance in "
            + "the submerged view: denser water is visible over a shorter path.")]
        [Min(0f)] public float underwaterDensity = 0.1f;
        [Tooltip("Strength of the refraction wobble applied to the submerged view.")]
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
