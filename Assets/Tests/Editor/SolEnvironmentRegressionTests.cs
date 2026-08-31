using System.IO;
using System.Reflection;
using NUnit.Framework;
using Sol.Environment;
using Sol.ToD;
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
        const string AtmosphereShaderPath = "Assets/Earth-Sky-Water/Shaders/SolAtmosphere.shader";
        const string WeatherProfileFolder = "Assets/Earth-Sky-Water/Weather Profiles";

        static readonly string[] WeatherScenePaths =
        {
            "Assets/Scenes/Sc_Sols_FiniteBodies.unity",
            "Assets/Scenes/Sc_Sols_Landscape.unity",
            "Assets/Scenes/Sols_Water2_Demo.unity",
        };

        /// <summary>
        /// The renderer feature addresses atmosphere passes by fixed numeric index. Keep
        /// those private constants pinned to the ShaderLab order so a pass insertion or
        /// reorder cannot silently run the wrong fullscreen program.
        /// </summary>
        [Test]
        public void AtmosphereShader_PassOrderMatchesRendererConstants()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(AtmosphereShaderPath);
            Assert.IsNotNull(shader, $"Atmosphere shader was not found at {AtmosphereShaderPath}.");

            Material material = new(shader);
            try
            {
                Assert.AreEqual(5, material.passCount,
                    "Atmosphere shader pass count changed; update the renderer indices deliberately.");

                System.Type passType = typeof(SolAtmosphereRendererFeature)
                    .GetNestedType("AtmospherePass", BindingFlags.NonPublic);
                Assert.IsNotNull(passType, "SolAtmosphereRendererFeature.AtmospherePass was not found.");

                AssertPassIndex(material, passType, "Sol Atmosphere Analytic", "AnalyticPassIndex");
                AssertPassIndex(material, passType, "Sol Atmosphere Directional Raymarch", "RaymarchPassIndex");
                AssertPassIndex(material, passType, "Sol Atmosphere Bilateral Composite", "CompositePassIndex");
                AssertPassIndex(material, passType, "Sol Atmosphere Half Resolution Spatial Filter",
                    "SpatialFilterPassIndex");
                AssertPassIndex(material, passType, "Sol Atmosphere Temporal Reprojection", "TemporalPassIndex");
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

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
        /// A camera with no active atmosphere Volume must inherit the baseline exactly.
        /// That equality is what makes the common Scene/Game-view path perform zero
        /// per-camera global writes instead of replaying the full atmosphere state.
        /// </summary>
        [Test]
        public void AtmosphereCameraResolve_WithoutVolumeReturnsBaselineFieldForField()
        {
            System.Type stateType = GetAtmosphereCameraStateType();
            object baseline = CreateAtmosphereCameraState(stateType);
            object resolved = ResolveAtmosphereCameraState(stateType, baseline, null);

            foreach (FieldInfo field in stateType.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.AreEqual(field.GetValue(baseline), field.GetValue(resolved),
                    $"Camera baseline field '{field.Name}' changed without an active Volume.");
            }
        }

        /// <summary>
        /// Resolve is deliberately sparse. A Volume that overrides only fog colour must
        /// change only the packed fog-colour field, otherwise ApplyCameraOverrides starts
        /// writing unrelated globals again.
        /// </summary>
        [Test]
        public void AtmosphereCameraResolve_OneOverrideChangesOneField()
        {
            System.Type stateType = GetAtmosphereCameraStateType();
            object baseline = CreateAtmosphereCameraState(stateType);
            SolAtmosphereVolume volume = ScriptableObject.CreateInstance<SolAtmosphereVolume>();
            try
            {
                volume.active = true;
                volume.enabledOverride.value = true;
                volume.fogColor.overrideState = true;
                volume.fogColor.value = Color.magenta;

                object resolved = ResolveAtmosphereCameraState(stateType, baseline, volume);
                int changedFields = 0;
                string changedFieldName = null;
                foreach (FieldInfo field in stateType.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (Equals(field.GetValue(baseline), field.GetValue(resolved)))
                        continue;

                    changedFields++;
                    changedFieldName = field.Name;
                }

                Assert.AreEqual(1, changedFields,
                    "A fog-colour-only Volume changed unrelated camera-state fields.");
                Assert.AreEqual("FogColor", changedFieldName);
            }
            finally
            {
                Object.DestroyImmediate(volume);
            }
        }

        /// <summary>
        /// The environment stack relies on this Update ordering: time solves celestial
        /// state, weather publishes modulation, world state and water consume it, then
        /// atmosphere and rain publish presentation globals. Attributes are easy to remove
        /// during a refactor and no compiler error reveals the one-frame latency it causes.
        /// </summary>
        [Test]
        public void EnvironmentAuthorities_KeepTheirExecutionOrderChain()
        {
            int time = ExecutionOrderOf<TimeOfDay>();
            int weather = ExecutionOrderOf<SolWeatherManager>();
            int environment = ExecutionOrderOf<SolEnvironmentWorld>();
            int water = ExecutionOrderOf<SolWaterWorld>();
            int atmosphere = ExecutionOrderOf<SolAtmosphereController>();
            int rain = ExecutionOrderOf<SolRainVfxController>();

            Assert.Less(time, weather);
            Assert.Less(weather, environment);
            Assert.Less(environment, water);
            Assert.Less(water, atmosphere);
            Assert.Less(atmosphere, rain);
        }

        /// <summary>
        /// TimeOfDay's environment application is a world-level operation. Multiple
        /// consumers reaching it in one frame must still produce one RenderSettings and
        /// sky-material push.
        /// </summary>
        [Test]
        public void TimeOfDayEnvironmentUpdate_IsIdempotentWithinAFrame()
        {
            GameObject host = new("TimeOfDayEnvironmentGateFixture");
            host.SetActive(false);
            try
            {
                TimeOfDay timeOfDay = host.AddComponent<TimeOfDay>();
                SetPrivateField(timeOfDay, "controlSkybox", false);
                SetPrivateField(timeOfDay, "controlAmbient", false);
                SetPrivateField(timeOfDay, "controlFog", false);

                MethodInfo updateEnvironment = typeof(TimeOfDay).GetMethod(
                    "UpdateEnvironment", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(updateEnvironment, "TimeOfDay.UpdateEnvironment was not found.");

                int before = SolEnvironmentBudget.Current.EnvironmentUpdates;
                updateEnvironment.Invoke(timeOfDay, null);
                updateEnvironment.Invoke(timeOfDay, null);
                int after = SolEnvironmentBudget.Current.EnvironmentUpdates;

                Assert.AreEqual(1, after - before,
                    "TimeOfDay applied its environment more than once in the same frame.");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// Weather identity now comes from one reusable asset set. Names are the public
        /// string-lookup keys, so both the expected set and uniqueness are contractual.
        /// </summary>
        [Test]
        public void WeatherProfiles_ShipTheExpectedUniqueAssetSet()
        {
            string[] guids = AssetDatabase.FindAssets(
                "t:SolWeatherProfileAsset", new[] { WeatherProfileFolder });
            Assert.AreEqual(4, guids.Length,
                "The shipped weather library must contain exactly four profiles.");

            var names = new string[guids.Length];
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                SolWeatherProfileAsset profile =
                    AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(path);
                Assert.IsNotNull(profile, $"Weather profile at {path} did not load.");
                names[i] = profile.name;
                for (int previous = 0; previous < i; previous++)
                {
                    Assert.AreNotEqual(names[previous], names[i],
                        $"Weather profile name '{names[i]}' is duplicated.");
                }
            }

            CollectionAssert.AreEquivalent(
                new[] { "Clear", "Overcast", "Rain", "Storm" }, names);
        }

        /// <summary>
        /// RangeAttribute controls the inspector but does not protect a hand-edited YAML
        /// asset. Validate the shipped data itself so a corrupt profile cannot feed
        /// out-of-contract values into every environment consumer.
        /// </summary>
        [Test]
        public void WeatherProfiles_StayWithinDeclaredRanges()
        {
            string[] guids = AssetDatabase.FindAssets(
                "t:SolWeatherProfileAsset", new[] { WeatherProfileFolder });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                SolWeatherProfileAsset profile =
                    AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(path);
                Assert.IsNotNull(profile, $"Weather profile at {path} did not load.");

                foreach (FieldInfo field in typeof(SolWeatherProfileAsset).GetFields(
                    BindingFlags.Instance | BindingFlags.Public))
                {
                    UnityEngine.RangeAttribute range =
                        field.GetCustomAttribute<UnityEngine.RangeAttribute>();
                    if (range == null || field.FieldType != typeof(float))
                        continue;

                    float value = (float)field.GetValue(profile);
                    Assert.IsFalse(float.IsNaN(value) || float.IsInfinity(value),
                        $"{profile.name}.{field.Name} is not finite.");
                    Assert.That(value, Is.InRange(range.min, range.max),
                        $"{profile.name}.{field.Name} bypassed its declared range.");
                }
            }
        }

        /// <summary>
        /// The manager must not grow a second inline presentation definition again, and
        /// each shipped scene must reference every shared profile asset.
        /// </summary>
        [Test]
        public void WeatherManagers_UseSharedProfileAssets()
        {
            Assert.IsNull(typeof(SolWeatherManager).GetNestedType(
                "WeatherProfile", BindingFlags.Public | BindingFlags.NonPublic),
                "SolWeatherManager has regained an inline WeatherProfile type.");

            string[] profileGuids = AssetDatabase.FindAssets(
                "t:SolWeatherProfileAsset", new[] { WeatherProfileFolder });
            foreach (string scenePath in WeatherScenePaths)
            {
                string yaml = File.ReadAllText(scenePath);
                foreach (string guid in profileGuids)
                {
                    Assert.That(yaml, Does.Contain($"guid: {guid}"),
                        $"{scenePath} does not reference weather profile {guid}.");
                }
                Assert.That(yaml, Does.Not.Contain("  - name: Clear"),
                    $"{scenePath} still contains inline weather presentation data.");
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
        /// The SSR histories are allocated at the resolution the reflection is traced at.
        ///
        /// They used to clone the camera descriptor unscaled -- two persistent full-screen
        /// R16G16B16A16 arrays per camera holding half-resolution information. The parameter
        /// is the mechanism, so its absence is the regression: without it the call silently
        /// goes back to full screen and nothing downstream complains, it just costs ~32 MB
        /// per camera again.
        /// </summary>
        [Test]
        public void ReflectionHistory_IsAllocatedAtTraceResolution()
        {
            MethodInfo ensure = typeof(SolEnvironmentCameraRegistry)
                .GetMethod("EnsureWaterReflectionHistory",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(ensure, "EnsureWaterReflectionHistory was not found.");

            // Two explicit int dimensions beyond the signature int: the caller computes the
            // size once and hands the same numbers to its resolve target, so the two ends of
            // the history blit cannot drift apart.
            int intParameters = 0;
            foreach (ParameterInfo parameter in ensure.GetParameters())
            {
                if (parameter.ParameterType == typeof(int))
                    intParameters++;
            }

            Assert.AreEqual(3, intParameters,
                "EnsureWaterReflectionHistory no longer takes explicit width and height, so "
                + "the SSR histories are either back to full screen resolution or sizing "
                + "themselves independently of the resolve target.");
        }

        /// <summary>
        /// The Low tier caps the SSR trace scale. Now that the resolve target and both
        /// histories follow that same scale, this clamp sets their size too, so it governs
        /// persistent memory rather than only trace cost.
        /// </summary>
        [Test]
        public void WaterQualityLowTier_ClampsSsrResolutionScale()
        {
            SolWaterQualityProfile profile = ScriptableObject.CreateInstance<SolWaterQualityProfile>();
            try
            {
                profile.tier = SolWaterQualityTier.Low;
                profile.ssrResolutionScale = 1f;
                InvokeOnValidate(profile);
                Assert.LessOrEqual(profile.ssrResolutionScale, 0.35f);

                // The clamp is a ceiling, not an assignment: a tier that already asks for
                // less than the cap must keep what it asked for.
                profile.ssrResolutionScale = 0.25f;
                InvokeOnValidate(profile);
                Assert.AreEqual(0.25f, profile.ssrResolutionScale, 1e-6f);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        static void InvokeOnValidate(SolWaterQualityProfile profile)
        {
            MethodInfo onValidate = typeof(SolWaterQualityProfile)
                .GetMethod("OnValidate", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(onValidate, "SolWaterQualityProfile.OnValidate was not found.");
            onValidate.Invoke(profile, null);
        }

        static void AssertPassIndex(
            Material material,
            System.Type passType,
            string passName,
            string constantName)
        {
            FieldInfo constant = passType.GetField(constantName,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(constant, $"{passType.Name}.{constantName} was not found.");
            Assert.AreEqual(material.FindPass(passName), (int)constant.GetRawConstantValue(),
                $"Shader pass '{passName}' no longer matches {constantName}.");
        }

        static System.Type GetAtmosphereCameraStateType()
        {
            System.Type stateType = typeof(SolAtmosphereController)
                .GetNestedType("CameraState", BindingFlags.NonPublic);
            Assert.IsNotNull(stateType,
                "SolAtmosphereController.CameraState was not found.");
            return stateType;
        }

        static object CreateAtmosphereCameraState(System.Type stateType)
        {
            return System.Activator.CreateInstance(
                stateType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[]
                {
                    new Color(0.2f, 0.3f, 0.4f, 1f),
                    new Vector4(0.01f, 5f, 500f, 0.92f),
                    new Vector4(0.65f, 0.55f, 1f, (float)SolAtmosphereQuality.High),
                    new Vector4(0.85f, 400f, 32f, 0.15f),
                    400f,
                },
                culture: null);
        }

        static object ResolveAtmosphereCameraState(
            System.Type stateType,
            object baseline,
            SolAtmosphereVolume volume)
        {
            MethodInfo resolve = typeof(SolAtmosphereController).GetMethod(
                "Resolve", BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: new[] { stateType, typeof(SolAtmosphereVolume) },
                modifiers: null);
            Assert.IsNotNull(resolve,
                "SolAtmosphereController.Resolve(CameraState, SolAtmosphereVolume) was not found.");
            return resolve.Invoke(null, new[] { baseline, volume });
        }

        static int ExecutionOrderOf<T>() where T : MonoBehaviour
        {
            DefaultExecutionOrder attribute = typeof(T)
                .GetCustomAttribute<DefaultExecutionOrder>();
            Assert.IsNotNull(attribute, $"{typeof(T).Name} has no DefaultExecutionOrder attribute.");
            return attribute.order;
        }

        static void SetPrivateField<T>(object target, string fieldName, T value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"{target.GetType().Name}.{fieldName} was not found.");
            field.SetValue(target, value);
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
