using UnityEngine;

namespace Sol.Water
{
    public enum SolWaterQualityTier : byte
    {
        Low,
        Medium,
        High,
    }

    /// <summary>
    /// Central water quality policy. Bodies can disable features but cannot silently
    /// exceed it. Assign one to <see cref="SolWaterWorld"/>; the renderer feature falls
    /// back to defaults if none is set.
    ///
    /// Inspector groups run roughly from cheapest to most expensive, and each group is
    /// gated by the toggle at its head.
    /// </summary>
    [CreateAssetMenu(menuName = "Sol/Water/Quality Profile", fileName = "Sol Water Quality")]
    public sealed class SolWaterQualityProfile : ScriptableObject
    {
        [Header("Tier")]
        [Tooltip("Sets the FFT resolution and cascade count, which nothing else here "
            + "overrides. Low disables the spectrum entirely and drives the surface from "
            + "the profile's authored Gerstner waves.")]
        public SolWaterQualityTier tier = SolWaterQualityTier.Medium;

        [System.NonSerialized] SolWaterQualityTier? _runtimeTierOverride;

        /// <summary>Effective tier after the lighting director's non-destructive override.</summary>
        public SolWaterQualityTier ActiveTier => _runtimeTierOverride ?? tier;

        public void SetTierOverride(SolWaterQualityTier value)
            => _runtimeTierOverride = value;

        public void ClearTierOverride()
            => _runtimeTierOverride = null;

        [Header("Ocean Geometry")]
        [Tooltip("Vertices per side of one clipmap patch. Rounded up to an even number. "
            + "This is the single biggest ocean vertex cost.")]
        [Range(16, 128)] public int clipmapPatchResolution = 48;
        [Tooltip("Minimum detail rings around the camera. The horizon distance below can "
            + "raise this but never lower it.")]
        [Range(3, 9)] public int clipmapRingCount = 8;
        [Tooltip("World size in metres of the finest clipmap patch, nearest the camera.")]
        [Min(1f)] public float clipmapBasePatchSize = 16f;
        [Tooltip("How far out the ocean is drawn, in metres.")]
        [Min(10f)] public float oceanHorizonDistance = 8000f;

        [Header("Screen-Space Reflections")]
        [Tooltip("Trace reflections against the depth buffer. Off falls back to the sky "
            + "gradient and reflection probes.")]
        public bool screenSpaceReflections = true;
        [Tooltip("Resolution of the raw reflection trace, relative to the camera. The "
            + "trace is by far the most expensive part of the water.")]
        [Range(0.2f, 1f)] public float ssrResolutionScale = 0.5f;
        [Tooltip("Ray-march steps per reflected ray. More steps reach further before "
            + "the ray gives up.")]
        [Range(8, 64)] public int ssrSteps = 24;
        [Tooltip("Longest reflected ray, in metres.")]
        [Min(1f)] public float ssrMaximumDistance = 150f;
        [Tooltip("Assumed thickness of scene geometry, in metres. Too thin misses hits, "
            + "too thick reflects behind objects.")]
        [Range(0.02f, 2f)] public float ssrThickness = 0.35f;
        [Tooltip("Width of the fade where a ray leaves the screen, as a fraction of the "
            + "viewport. Wider hides the cut-off but loses reflection near the edges.")]
        [Range(0.001f, 0.25f)] public float ssrEdgeFade = 0.06f;
        [Tooltip("Refinement steps run after a coarse hit, to land the ray on the exact "
            + "surface.")]
        [Range(0, 8)] public int ssrBinarySearchSteps = 5;
        [Tooltip("How much of the previous frame's reflection is kept. High values are "
            + "stable but smear under fast camera motion.")]
        [Range(0f, 0.98f)] public float ssrHistoryWeight = 0.86f;
        [Tooltip("Depth agreement, in metres, required to accept a reprojected history "
            + "sample.")]
        [Range(0.01f, 2f)] public float ssrDepthTolerance = 0.2f;
        [Tooltip("Normal agreement required to accept a reprojected history sample. "
            + "1 accepts anything.")]
        [Range(0f, 1f)] public float ssrNormalTolerance = 0.8f;
        [Tooltip("HDR clamp on a reflected sample. This is what stops a single bright "
            + "pixel firing off as a persistent temporal smear.")]
        [Min(0.1f)] public float ssrMaximumLuminance = 4f;
        [Tooltip("Wind-driven anisotropic smear applied to the resolved reflection. "
            + "Its strength comes from anisotropicReflectionScale on the water profile.")]
        public bool anisotropicReflections = true;

        [Header("Planar Reflections")]
        [Tooltip("Experimental. Disabled by default until planar rendering is scheduled "
            + "outside URP's active camera graph; a nested render from endCameraRendering "
            + "conflicts with Unity 6 Forward+ jobs.")]
        public bool planarReflections;
        [Tooltip("Resolution of the planar reflection camera, relative to the source.")]
        [Range(0.1f, 1f)] public float planarResolutionScale = 0.5f;
        [Tooltip("How many frames old a planar capture may be before it is discarded.")]
        [Range(1, 8)] public int planarMaximumAgeFrames = 2;

        [Header("Volumetric Water Lighting")]
        [Tooltip("In-water light shafts and shadowed volume, marched from the surface "
            + "above water and from the camera below it. Off falls back to the analytic "
            + "scattering, which keeps the colour but loses the shafts.")]
        public bool volumetricWaterLighting = true;
        [Tooltip("Raymarch steps through the water volume.")]
        [Range(4, 32)] public int volumetricSteps = 12;
        [Tooltip("Resolution of the volumetric buffer, relative to the camera.")]
        [Range(0.1f, 1f)] public float volumetricResolutionScale = 0.5f;

        [Header("Underwater and Caustics")]
        [Tooltip("Composite the submerged view: absorption, haze and light shafts seen "
            + "from inside the water. Also gates the _UnderwaterFactor atmosphere hand-off.")]
        public bool underwater = true;
        [Tooltip("Project caustics onto the sea bed. On Medium and High these are "
            + "rendered live from the FFT displacement; on Low they use the authored "
            + "caustic texture on the water profile.")]
        public bool caustics = true;

        [Header("Post-Processing Integration")]
        [Tooltip("Write the water surface into the camera depth buffer so depth-of-field, "
            + "SSAO and motion vectors treat it as a surface rather than seeing through it. "
            + "Costs one extra full-ocean draw.")]
        public bool writeDepthForPostProcessing;

        /// <summary>
        /// FFT texture resolution per cascade, or 0 when the tier has no spectrum and the
        /// surface is driven by the profile's authored Gerstner waves instead.
        /// </summary>
        public int FftResolution => ActiveTier switch
        {
            SolWaterQualityTier.Low => 0,
            SolWaterQualityTier.Medium => 128,
            _ => 256,
        };

        /// <summary>Spectral cascades simulated, or 0 on a tier with no spectrum.</summary>
        public int FftCascadeCount => ActiveTier switch
        {
            SolWaterQualityTier.Low => 0,
            SolWaterQualityTier.Medium => 2,
            _ => 4,
        };

        void OnValidate()
        {
            clipmapPatchResolution = Mathf.Clamp(clipmapPatchResolution, 16, 128);
            clipmapPatchResolution += clipmapPatchResolution & 1;
            clipmapRingCount = Mathf.Clamp(clipmapRingCount, 3, 9);
            clipmapBasePatchSize = Mathf.Max(1f, clipmapBasePatchSize);
            ssrSteps = Mathf.Clamp(ssrSteps, 8, 64);
            ssrMaximumDistance = Mathf.Max(1f, ssrMaximumDistance);
            ssrThickness = Mathf.Clamp(ssrThickness, 0.02f, 2f);
            ssrEdgeFade = Mathf.Clamp(ssrEdgeFade, 0.001f, 0.25f);
            ssrBinarySearchSteps = Mathf.Clamp(ssrBinarySearchSteps, 0, 8);
            ssrHistoryWeight = Mathf.Clamp(ssrHistoryWeight, 0f, 0.98f);
            ssrDepthTolerance = Mathf.Clamp(ssrDepthTolerance, 0.01f, 2f);
            ssrNormalTolerance = Mathf.Clamp01(ssrNormalTolerance);
            ssrMaximumLuminance = Mathf.Max(0.1f, ssrMaximumLuminance);
            volumetricSteps = Mathf.Clamp(volumetricSteps, 4, 32);
            volumetricResolutionScale = Mathf.Clamp(volumetricResolutionScale, 0.1f, 1f);
            if (tier == SolWaterQualityTier.Low)
                ssrResolutionScale = Mathf.Min(ssrResolutionScale, 0.35f);
        }
    }
}
