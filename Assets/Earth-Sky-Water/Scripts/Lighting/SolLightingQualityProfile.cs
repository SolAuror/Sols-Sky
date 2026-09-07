using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Sol.Lighting
{
    public enum SolLightingQualityTier : byte
    {
        Low,
        Medium,
        High,
    }

    /// <summary>Deterministic budgets associated with one manual lighting tier.</summary>
    [Serializable]
    public readonly struct SolLightingQualitySettings
    {
        public readonly float MainShadowDistance;
        public readonly int MainShadowCascades;
        public readonly int MainShadowAtlasResolution;
        public readonly int PunctualShadowAtlasResolution;
        public readonly int ManagedLightLimit;
        public readonly int ShadowSliceLimit;
        public readonly int LocalVolumetricLightLimit;
        public readonly float ReflectionProbeRefreshInterval;

        public SolLightingQualitySettings(
            float mainShadowDistance,
            int mainShadowCascades,
            int mainShadowAtlasResolution,
            int punctualShadowAtlasResolution,
            int managedLightLimit,
            int shadowSliceLimit,
            int localVolumetricLightLimit,
            float reflectionProbeRefreshInterval)
        {
            MainShadowDistance = Mathf.Max(0f, mainShadowDistance);
            MainShadowCascades = Mathf.Clamp(mainShadowCascades, 1, 4);
            MainShadowAtlasResolution = Mathf.Max(256, mainShadowAtlasResolution);
            PunctualShadowAtlasResolution = Mathf.Max(256, punctualShadowAtlasResolution);
            ManagedLightLimit = Mathf.Max(0, managedLightLimit);
            ShadowSliceLimit = Mathf.Max(0, shadowSliceLimit);
            LocalVolumetricLightLimit = Mathf.Max(0, localVolumetricLightLimit);
            ReflectionProbeRefreshInterval = Mathf.Max(0f, reflectionProbeRefreshInterval);
        }

        public static SolLightingQualitySettings ForTier(SolLightingQualityTier tier)
            => tier switch
            {
                SolLightingQualityTier.Low => new SolLightingQualitySettings(
                    35f, 2, 1024, 1024, 48, 4, 0, 8f),
                SolLightingQualityTier.High => new SolLightingQualitySettings(
                    100f, 4, 4096, 4096, 160, 24, 8, 2f),
                _ => new SolLightingQualitySettings(
                    60f, 3, 2048, 2048, 96, 12, 4, 4f),
            };

        public static int ShadowSliceCost(LightType lightType)
            => lightType switch
            {
                LightType.Spot => 1,
                LightType.Point => 6,
                _ => 0,
            };

        public void ApplyTo(UniversalRenderPipelineAsset pipeline)
        {
            if (pipeline == null)
                return;

            pipeline.shadowDistance = MainShadowDistance;
            pipeline.shadowCascadeCount = MainShadowCascades;
            pipeline.mainLightShadowmapResolution = MainShadowAtlasResolution;
            pipeline.additionalLightsShadowmapResolution = PunctualShadowAtlasResolution;
        }
    }

    /// <summary>
    /// Manual, deterministic environment-lighting quality policy. Runtime selection is
    /// deliberately non-serialized so changing quality never dirties the authored asset.
    /// </summary>
    [CreateAssetMenu(
        menuName = "Sol/Lighting/Quality Profile",
        fileName = "Sol Lighting Quality")]
    public sealed class SolLightingQualityProfile : ScriptableObject
    {
        [SerializeField] SolLightingQualityTier defaultTier = SolLightingQualityTier.Medium;

        [NonSerialized] SolLightingQualityTier _activeTier;
        [NonSerialized] bool _initialized;

        public SolLightingQualityTier DefaultTier => defaultTier;
        public SolLightingQualityTier ActiveTier
        {
            get
            {
                EnsureInitialized();
                return _activeTier;
            }
        }

        public SolLightingQualitySettings ActiveSettings
            => SolLightingQualitySettings.ForTier(ActiveTier);

        public event Action<SolLightingQualityTier> TierChanged;

        public void SetTier(SolLightingQualityTier tier)
        {
            EnsureInitialized();
            tier = ClampTier(tier);
            if (_activeTier == tier)
                return;

            _activeTier = tier;
            TierChanged?.Invoke(tier);
        }

        public void ResetToDefaultTier() => SetTier(defaultTier);

        void OnEnable()
        {
            _activeTier = ClampTier(defaultTier);
            _initialized = true;
        }

        void OnValidate()
        {
            defaultTier = ClampTier(defaultTier);
            if (!Application.isPlaying)
            {
                _activeTier = defaultTier;
                _initialized = true;
            }
        }

        void EnsureInitialized()
        {
            if (_initialized)
                return;
            _activeTier = ClampTier(defaultTier);
            _initialized = true;
        }

        static SolLightingQualityTier ClampTier(SolLightingQualityTier tier)
            => (SolLightingQualityTier)Mathf.Clamp((int)tier, 0, 2);
    }
}
