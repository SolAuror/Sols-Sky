using NUnit.Framework;
using Sol.Lighting;
using Sol.Water;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Sol.Tests.Editor
{
    public sealed class SolLightingQualityProfileTests
    {
        [TestCase(SolLightingQualityTier.Low, 35f, 2, 1024, 1024, 48, 4, 0, 8f)]
        [TestCase(SolLightingQualityTier.Medium, 60f, 3, 2048, 2048, 96, 12, 4, 4f)]
        [TestCase(SolLightingQualityTier.High, 100f, 4, 4096, 4096, 160, 24, 8, 2f)]
        public void TierSettings_ArePinnedToTheShippingBudgets(
            SolLightingQualityTier tier,
            float shadowDistance,
            int cascades,
            int mainAtlas,
            int punctualAtlas,
            int managedLights,
            int shadowSlices,
            int volumetricLights,
            float probeInterval)
        {
            SolLightingQualitySettings settings = SolLightingQualitySettings.ForTier(tier);

            Assert.AreEqual(shadowDistance, settings.MainShadowDistance, 1e-5f);
            Assert.AreEqual(cascades, settings.MainShadowCascades);
            Assert.AreEqual(mainAtlas, settings.MainShadowAtlasResolution);
            Assert.AreEqual(punctualAtlas, settings.PunctualShadowAtlasResolution);
            Assert.AreEqual(managedLights, settings.ManagedLightLimit);
            Assert.AreEqual(shadowSlices, settings.ShadowSliceLimit);
            Assert.AreEqual(volumetricLights, settings.LocalVolumetricLightLimit);
            Assert.AreEqual(probeInterval, settings.ReflectionProbeRefreshInterval, 1e-5f);
        }

        [Test]
        public void SetTier_ChangesRuntimeStateAndFiresOnlyForARealTransition()
        {
            SolLightingQualityProfile profile =
                ScriptableObject.CreateInstance<SolLightingQualityProfile>();
            try
            {
                int eventCount = 0;
                SolLightingQualityTier eventTier = SolLightingQualityTier.Low;
                profile.TierChanged += tier =>
                {
                    eventCount++;
                    eventTier = tier;
                };

                Assert.AreEqual(SolLightingQualityTier.Medium, profile.ActiveTier);
                profile.SetTier(SolLightingQualityTier.High);
                profile.SetTier(SolLightingQualityTier.High);

                Assert.AreEqual(SolLightingQualityTier.High, profile.ActiveTier);
                Assert.AreEqual(SolLightingQualityTier.High, eventTier);
                Assert.AreEqual(1, eventCount);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void WaterTierOverride_DoesNotMutateTheAuthoredAssetTier()
        {
            SolWaterQualityProfile water =
                ScriptableObject.CreateInstance<SolWaterQualityProfile>();
            try
            {
                water.tier = SolWaterQualityTier.High;
                water.SetTierOverride(SolWaterQualityTier.Low);

                Assert.AreEqual(SolWaterQualityTier.High, water.tier);
                Assert.AreEqual(SolWaterQualityTier.Low, water.ActiveTier);
                Assert.AreEqual(0, water.FftResolution);
                Assert.AreEqual(0, water.FftCascadeCount);

                water.ClearTierOverride();
                Assert.AreEqual(SolWaterQualityTier.High, water.ActiveTier);
                Assert.AreEqual(256, water.FftResolution);
                Assert.AreEqual(4, water.FftCascadeCount);
            }
            finally
            {
                Object.DestroyImmediate(water);
            }
        }

        [Test]
        public void ShadowSliceCost_MatchesUrpAtlasPacking()
        {
            Assert.AreEqual(1, SolLightingQualitySettings.ShadowSliceCost(LightType.Spot));
            Assert.AreEqual(6, SolLightingQualitySettings.ShadowSliceCost(LightType.Point));
            Assert.AreEqual(0, SolLightingQualitySettings.ShadowSliceCost(LightType.Directional));
        }

        [Test]
        public void MediumTier_AppliesItsShadowPolicyToUrp()
        {
            UniversalRenderPipelineAsset pipeline =
                ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            try
            {
                SolLightingQualitySettings.ForTier(SolLightingQualityTier.Medium)
                    .ApplyTo(pipeline);

                Assert.AreEqual(60f, pipeline.shadowDistance, 1e-5f);
                Assert.AreEqual(3, pipeline.shadowCascadeCount);
                Assert.AreEqual(2048, pipeline.mainLightShadowmapResolution);
                Assert.AreEqual(2048, pipeline.additionalLightsShadowmapResolution);
            }
            finally
            {
                Object.DestroyImmediate(pipeline);
            }
        }

        [Test]
        public void ShippingProfile_LoadsAsMedium()
        {
            SolLightingQualityProfile profile =
                Resources.Load<SolLightingQualityProfile>(
                    "SolEnvironment/Sol_LightingQuality");

            Assert.IsNotNull(profile);
            Assert.AreEqual(SolLightingQualityTier.Medium, profile.DefaultTier);
            Assert.AreEqual(SolLightingQualityTier.Medium, profile.ActiveTier);
        }

        [Test]
        public void DemoControls_RouteQualityThroughTheLightingDirector()
        {
            string source = File.ReadAllText(
                Path.Combine(Application.dataPath, "Earth-Sky-Water/DemoTimeControls.cs"));

            StringAssert.Contains("lightingDirector.SetTier(next)", source);
            StringAssert.DoesNotContain("timeOfDay.CloudQuality =", source);
            StringAssert.DoesNotContain("atmosphereController.SetQuality", source);
        }
    }
}
