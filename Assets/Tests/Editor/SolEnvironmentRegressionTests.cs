using System.Reflection;
using NUnit.Framework;
using Sol.Water;
using Sol.Water.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Sol.Tests.Editor
{
    /// <summary>
    /// Edit-mode guards for the environment stack.
    ///
    /// Deliberately narrow. These live in the predefined Assembly-CSharp-Editor rather
    /// than under an assembly definition: the Sol runtime code is all in the predefined
    /// Assembly-CSharp, and an asmdef assembly cannot reference a predefined one. The
    /// previous Sol.Environment.EditorTests.asmdef tried exactly that and so could never
    /// see the types it was written against.
    ///
    /// Everything here is pure policy or asset state -- no scene loading, no render
    /// pipeline execution, no play mode -- so the suite stays fast and does not need the
    /// demo scenes to be in any particular condition.
    /// </summary>
    public sealed class SolEnvironmentRegressionTests
    {
        const string RendererPath = "Assets/Settings/Sol_Renderer.asset";

        /// <summary>
        /// The water feature's per-body underwater logging is a development aid. Shipped
        /// enabled it emits one stack-traced line per submerged body per camera per frame,
        /// which floods the console and distorts editor CPU timings badly enough to make
        /// profiling captures useless.
        /// </summary>
        [Test]
        public void RendererAsset_ShipsWithWaterDebugLoggingDisabled()
        {
            ScriptableRendererData rendererData =
                AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererPath);
            Assert.IsNotNull(rendererData, $"Sol renderer data was not found at {RendererPath}.");

            SolWaterRendererFeature water = null;
            foreach (ScriptableRendererFeature feature in rendererData.rendererFeatures)
            {
                if (feature is SolWaterRendererFeature match)
                {
                    water = match;
                    break;
                }
            }

            Assert.IsNotNull(water, "The Sol renderer has no SolWaterRendererFeature.");

            FieldInfo debugLog = typeof(SolWaterRendererFeature)
                .GetField("debugLog", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(debugLog, "SolWaterRendererFeature no longer has a debugLog field.");
            Assert.IsFalse((bool)debugLog.GetValue(water),
                "Water debug logging is enabled in the shipped renderer asset.");
        }

        /// <summary>
        /// The volumetric atmosphere skips recording its spatial filter pass when the
        /// authored strength is zero, because the shader's own early-out makes the pass a
        /// half-resolution identity copy in that case. If a tier below High ever authors a
        /// non-zero strength, that skip silently starts dropping a filter that is now
        /// meant to run, so the tier rule and the render-graph gate have to move together.
        /// </summary>
        [Test]
        public void AtmosphereSpatialFilter_IsAuthoredOnlyAtHighQuality()
        {
            GameObject host = new("SolAtmosphereControllerFixture");
            try
            {
                // Inactive before AddComponent so OnEnable never runs: the controller
                // claims a static authority and pushes shader globals from OnEnable, and
                // a test has no business doing either.
                host.SetActive(false);
                SolAtmosphereController controller = host.AddComponent<SolAtmosphereController>();

                controller.SetQuality(SolAtmosphereQuality.Low);
                Assert.AreEqual(0f, controller.CurrentSpatialFilterStrength, 1e-6f,
                    "Low quality must not author a spatial filter.");

                controller.SetQuality(SolAtmosphereQuality.Medium);
                Assert.AreEqual(0f, controller.CurrentSpatialFilterStrength, 1e-6f,
                    "Medium quality must not author a spatial filter.");

                controller.SetQuality(SolAtmosphereQuality.High);
                Assert.Greater(controller.CurrentSpatialFilterStrength, 0.001f,
                    "High quality must author a spatial filter above the shader epsilon.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// The tier table drives FFT cost directly, and the jump from Medium to High is
        /// roughly ninefold. Pinning it keeps an innocuous-looking edit from changing the
        /// simulation budget without anyone noticing.
        /// </summary>
        [Test]
        public void WaterQualityTiers_ExposeExpectedSpectralBudgets()
        {
            SolWaterQualityProfile profile = ScriptableObject.CreateInstance<SolWaterQualityProfile>();
            try
            {
                profile.tier = SolWaterQualityTier.Low;
                Assert.AreEqual(0, profile.FftResolution, "Low must disable the spectrum.");
                Assert.AreEqual(0, profile.FftCascadeCount, "Low must disable the spectrum.");

                profile.tier = SolWaterQualityTier.Medium;
                Assert.AreEqual(128, profile.FftResolution);
                Assert.AreEqual(2, profile.FftCascadeCount);

                profile.tier = SolWaterQualityTier.High;
                Assert.AreEqual(256, profile.FftResolution);
                Assert.AreEqual(4, profile.FftCascadeCount);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// A tier must never cost more than the tier above it in either dimension.
        /// </summary>
        [Test]
        public void WaterQualityTiers_DegradeMonotonically()
        {
            SolWaterQualityProfile profile = ScriptableObject.CreateInstance<SolWaterQualityProfile>();
            try
            {
                profile.tier = SolWaterQualityTier.Medium;
                int mediumResolution = profile.FftResolution;
                int mediumCascades = profile.FftCascadeCount;

                profile.tier = SolWaterQualityTier.High;
                Assert.GreaterOrEqual(profile.FftResolution, mediumResolution);
                Assert.GreaterOrEqual(profile.FftCascadeCount, mediumCascades);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// Rain resolves submersion from the shared _UnderwaterFactor global, not from
        /// UnderwaterVolumeController. That controller yields entirely whenever Water 2 is
        /// live, so a direct reference silently reported "not underwater" in every Water 2
        /// scene and rain kept falling through a submerged camera. Both water paths publish
        /// the global, so it is the only source that works for either.
        /// </summary>
        [Test]
        public void RainController_ResolvesSubmersionFromTheSharedGlobal()
        {
            foreach (FieldInfo field in typeof(SolRainVfxController)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                Assert.AreNotEqual(typeof(UnderwaterVolumeController), field.FieldType,
                    $"SolRainVfxController.{field.Name} reintroduces a dependency on the "
                    + "legacy underwater controller, which does not update under Water 2.");
            }
        }
    }
}
