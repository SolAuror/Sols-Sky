using UnityEngine;

namespace Sol.Water
{
    public enum SolWaterQualityTier : byte
    {
        Low,
        Medium,
        High,
    }

    /// <summary>Central water quality policy; bodies can disable features but cannot silently exceed it.</summary>
    [CreateAssetMenu(menuName = "Sol/Water/Quality Profile", fileName = "Sol Water Quality")]
    public sealed class SolWaterQualityProfile : ScriptableObject
    {
        public SolWaterQualityTier tier = SolWaterQualityTier.Medium;

        [Header("Ocean")]
        [Range(16, 128)] public int clipmapPatchResolution = 48;
        [Range(3, 9)] public int clipmapRingCount = 8;
        [Min(1f)] public float clipmapBasePatchSize = 16f;
        [Min(10f)] public float oceanHorizonDistance = 8000f;

        [Header("Reflections")]
        public bool screenSpaceReflections = true;
        [Range(0.2f, 1f)] public float ssrResolutionScale = 0.5f;
        [Range(8, 64)] public int ssrSteps = 24;
        [Min(1f)] public float ssrMaximumDistance = 150f;
        [Range(0.02f, 2f)] public float ssrThickness = 0.35f;
        [Range(0.001f, 0.25f)] public float ssrEdgeFade = 0.06f;
        [Range(0, 8)] public int ssrBinarySearchSteps = 5;
        [Range(0f, 0.98f)] public float ssrHistoryWeight = 0.86f;
        [Range(0.01f, 2f)] public float ssrDepthTolerance = 0.2f;
        [Range(0f, 1f)] public float ssrNormalTolerance = 0.8f;
        [Min(0.1f)] public float ssrMaximumLuminance = 4f;
        [Tooltip("Experimental. Disabled by default until planar rendering is scheduled outside URP's active camera graph.")]
        public bool planarReflections;
        [Range(0.1f, 1f)] public float planarResolutionScale = 0.5f;
        [Range(1, 8)] public int planarMaximumAgeFrames = 2;

        [Header("Underwater / Effects")]
        public bool underwater = true;
        public bool caustics = true;
        public bool interactionSimulation = true;
        [Range(64, 1024)] public int interactionResolution = 256;

        public int FftResolution => tier switch
        {
            SolWaterQualityTier.Low => 0,
            SolWaterQualityTier.Medium => 128,
            _ => 256,
        };

        public int FftCascadeCount => tier switch
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
            if (tier == SolWaterQualityTier.Low)
            {
                ssrResolutionScale = Mathf.Min(ssrResolutionScale, 0.35f);
                interactionResolution = Mathf.Min(interactionResolution, 128);
            }
        }
    }
}
