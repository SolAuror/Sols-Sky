using System.Reflection;
using NUnit.Framework;
using Sol.Environment;
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
        /// The legacy Water 1 underwater feature must stay disabled. Water 2 publishes its
        /// own submersion contract and composites underwater from
        /// SolWaterRendererFeature; with both live the screen is composited twice, which
        /// reads as a murky over-dark tint rather than an obvious failure. Nothing in the
        /// renderer asset guards this, and it is one inspector checkbox away.
        /// </summary>
        [Test]
        public void RendererAsset_KeepsTheLegacyUnderwaterFeatureDisabled()
        {
            ScriptableRendererData rendererData =
                AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererPath);
            Assert.IsNotNull(rendererData, $"Sol renderer data was not found at {RendererPath}.");

            foreach (ScriptableRendererFeature feature in rendererData.rendererFeatures)
            {
                if (feature is UnderwaterRendererFeature legacy)
                    Assert.IsFalse(legacy.isActive,
                        "The legacy UnderwaterRendererFeature is enabled; it double-composites "
                        + "underwater alongside Water 2.");
            }
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
        /// The world frame gate admits exactly one caller per frame. This is the mechanism
        /// that will keep the ocean spectrum from being recorded once per camera, so its
        /// truth table is worth pinning before anything depends on it.
        /// </summary>
        [Test]
        public void WorldFrameGate_AdmitsOneCallerPerFrame()
        {
            SolWorldFrameGate gate = default;

            Assert.IsTrue(gate.TryBeginFrame(10), "First caller in a frame must be admitted.");
            Assert.IsFalse(gate.TryBeginFrame(10), "Second caller in the same frame must be refused.");
            Assert.IsTrue(gate.TryBeginFrame(11), "A new frame must be admitted.");

            // Frame regression: a domain reload or a play-mode exit restarts Unity's frame
            // counter. A greater-than test would latch here and refuse everything after.
            Assert.IsTrue(gate.TryBeginFrame(9), "A backwards frame stamp must still be admitted.");

            gate.Invalidate();
            Assert.IsTrue(gate.TryBeginFrame(9), "Invalidate must re-admit the current frame.");
        }

        /// <summary>
        /// A default-constructed gate must admit frame zero, which is the frame a fresh
        /// domain reload starts on. A struct whose stamp defaults to 0 with no validity flag
        /// would refuse it, and the symptom -- work silently skipped on exactly one frame
        /// after every reload -- is close to undiagnosable.
        /// </summary>
        [Test]
        public void WorldFrameGate_DefaultInstanceAdmitsFrameZero()
        {
            SolWorldFrameGate gate = default;
            Assert.IsTrue(gate.TryBeginFrame(0));
            Assert.IsFalse(gate.TryBeginFrame(0));
        }

        /// <summary>
        /// The ocean spectrum is recorded once per world frame, not once per camera. The
        /// mechanism that enforces that is the absence of a camera parameter on Record --
        /// with one available, adding a per-camera dependency is a one-word change and
        /// nothing downstream would complain. The cost of regressing this is the full FFT
        /// chain a second time whenever the Scene view is open beside the Game view.
        /// </summary>
        [Test]
        public void WaterSpectrum_IsRecordedWithoutACameraContext()
        {
            // Reached by name: the type is internal to the runtime assembly, which the test
            // assembly cannot see directly.
            System.Type fft = typeof(SolWaterRendererFeature).Assembly
                .GetType("Sol.Water.Rendering.SolWaterFftRenderGraph");
            Assert.IsNotNull(fft, "SolWaterFftRenderGraph was not found in the runtime assembly.");

            MethodInfo record = fft.GetMethod("Record",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(record, "SolWaterFftRenderGraph.Record was not found.");

            foreach (ParameterInfo parameter in record.GetParameters())
            {
                Assert.AreNotEqual(typeof(SolEnvironmentCameraRegistry.Context), parameter.ParameterType,
                    $"Record takes a camera context via '{parameter.Name}', which reintroduces "
                    + "per-camera recording of a world-level simulation.");
            }
        }

        /// <summary>
        /// The FFT normal/foam history is FFT-domain, not screen-space, so a per-camera copy
        /// is byte-identical duplication rather than a temporal necessity. It lives on
        /// SolWaterSpectralTargets now; this guards the camera registry against growing it
        /// back.
        /// </summary>
        [Test]
        public void CameraRegistry_DoesNotOwnTheNormalFoamHistory()
        {
            Assert.IsNull(
                typeof(SolEnvironmentCameraRegistry.Context).GetProperty("WaterNormalFoamHistory"),
                "The per-camera normal/foam history is back; it is a world-level resource.");
            Assert.IsNull(
                typeof(SolEnvironmentCameraRegistry).GetMethod("EnsureWaterNormalFoamHistory"),
                "EnsureWaterNormalFoamHistory is back; it is a world-level resource.");
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
