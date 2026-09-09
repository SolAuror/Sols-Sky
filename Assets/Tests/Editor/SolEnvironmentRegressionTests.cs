using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Sol.Environment;
using Sol.Environment.EditorTools;
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
        const string CloudShaderPath = "Assets/Earth-Sky-Water/Shaders/SolVolumetricClouds.shader";
        const string CloudStateSourcePath =
            "Assets/Earth-Sky-Water/Scripts/Management/SolCloudState.cs";
        const string WeatherManagerSourcePath =
            "Assets/Earth-Sky-Water/Scripts/Management/WeatherManager.cs";
        const string WeatherProfileFolder = "Assets/Earth-Sky-Water/Weather Profiles";
        const string WaterWaveIncludePath =
            "Assets/Earth-Sky-Water/Shaders/Water2/SolWaterWaves2.hlsl";
        const string WaterFftComputePath =
            "Assets/Earth-Sky-Water/Shaders/Water2/SolWaterFFT.compute";
        const string WaterFftReadbackPath =
            "Assets/Earth-Sky-Water/Scripts/Water2/SolWaterFftReadback.cs";
        const string SkyboxShaderPath =
            "Assets/Earth-Sky-Water/TimeOfDay/CelestialBodies/Sol_Skybox.shader";
        const string CelestialBodyShaderPath =
            "Assets/Earth-Sky-Water/TimeOfDay/CelestialBodies/CelestialBody.shader";
        const string TimeOfDaySourcePath =
            "Assets/Earth-Sky-Water/TimeOfDay/TimeofDay.cs";

        // One home for the four containers that serialize a weather selection list,
        // shared with the migration and sync tools so they cannot drift apart.
        static string[] WeatherSelectionAssetPaths
            => SolWeatherProfileMigration.SerializedAssetPaths;

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

        [Test]
        public void CloudShader_PassOrderMatchesRendererConstants()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(CloudShaderPath);
            Assert.IsNotNull(shader, $"Cloud shader was not found at {CloudShaderPath}.");
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader), "The cloud shader has an import/compile error.");
            Material material = new(shader);
            try
            {
                Assert.AreEqual(4, material.passCount);
                System.Type passType = typeof(SolCloudRendererFeature)
                    .GetNestedType("CloudPass", BindingFlags.NonPublic);
                Assert.IsNotNull(passType);
                AssertPassIndex(material, passType, "Sol Cloud Raymarch", "RaymarchPassIndex");
                AssertPassIndex(material, passType, "Sol Cloud Temporal Reprojection", "TemporalPassIndex");
                AssertPassIndex(material, passType, "Sol Cloud Bilateral Composite", "CompositePassIndex");
                AssertPassIndex(material, passType, "Sol Cloud Shadow Map", "ShadowPassIndex");
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void RendererAsset_OrdersCloudsImmediatelyBeforeAtmosphere()
        {
            ScriptableRendererData rendererData =
                AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererPath);
            Assert.IsNotNull(rendererData);
            int cloud = -1;
            int atmosphere = -1;
            for (int i = 0; i < rendererData.rendererFeatures.Count; i++)
            {
                if (rendererData.rendererFeatures[i] is SolCloudRendererFeature) cloud = i;
                if (rendererData.rendererFeatures[i] is SolAtmosphereRendererFeature) atmosphere = i;
            }
            Assert.GreaterOrEqual(cloud, 0, "The Sol cloud renderer feature is missing.");
            Assert.AreEqual(cloud + 1, atmosphere,
                "Clouds must composite immediately before atmosphere at the shared pass event.");
        }

        /// <summary>
        /// Import and pass-index checks do not execute RenderGraph. This tiny off-screen
        /// render catches undeclared global state, invalid transient resources, and other
        /// runtime-only graph validation failures before a Scene View can log them per frame.
        /// </summary>
        [Test]
        public void CloudRenderer_ExecutesRenderGraphWithoutErrors()
        {
            GameObject cameraHost = new("SolCloudRenderTestCamera");
            GameObject timeHost = new("SolCloudRenderTestTime");
            RenderTexture target = new(64, 64, 24, RenderTextureFormat.ARGBHalf);
            try
            {
                Camera camera = cameraHost.AddComponent<Camera>();
                cameraHost.tag = "MainCamera";
                camera.targetTexture = target;
                camera.clearFlags = CameraClearFlags.Skybox;
                timeHost.AddComponent<TimeOfDay>();
                camera.Render();
            }
            finally
            {
                Object.DestroyImmediate(cameraHost);
                Object.DestroyImmediate(timeHost);
                Object.DestroyImmediate(target);
            }
        }

        /// <summary>
        /// The Phase 7 window relies on player-loop updates while stopped. Without
        /// ExecuteAlways the window still repaints, but atmosphere globals and rain stay
        /// frozen, which looks like a renderer bug rather than a missing lifecycle flag.
        /// </summary>
        [Test]
        public void AtmosphereAndRain_RunInEditMode()
        {
            Assert.IsNotNull(typeof(SolAtmosphereController)
                .GetCustomAttribute<ExecuteAlways>());
            Assert.IsNotNull(typeof(SolRainVfxController)
                .GetCustomAttribute<ExecuteAlways>());
        }

        /// <summary>
        /// Editor A/B preview must borrow the runtime blend. Pin the public seam so a
        /// future custom inspector cannot accidentally grow a parallel weather model.
        /// </summary>
        [Test]
        public void WeatherManager_ExposesTheSharedABPreviewSeam()
        {
            MethodInfo setPreview = typeof(SolWeatherManager).GetMethod(
                "SetPreview", BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(int), typeof(int), typeof(float) },
                modifiers: null);
            Assert.IsNotNull(setPreview,
                "SolWeatherManager.SetPreview(int, int, float) was not found.");
            Assert.IsNotNull(typeof(SolWeatherManager).GetMethod(
                "ClearPreview", BindingFlags.Instance | BindingFlags.Public));
        }

        /// <summary>
        /// A HideAndDontSave skybox clone must never become a serialized TimeOfDay field.
        /// Unity resolves such a scene reference to null on save/reopen, permanently
        /// clearing RenderSettings.skybox from the scene.
        /// </summary>
        [Test]
        public void TimeOfDay_RuntimeSkyboxCloneField_IsNotSerialized()
        {
            FieldInfo field = typeof(TimeOfDay).GetField(
                "controlledSkyboxMaterial", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "TimeOfDay.controlledSkyboxMaterial was not found.");
            Assert.IsFalse(field.IsPublic);
            Assert.IsNull(field.GetCustomAttribute<SerializeField>());
            Assert.IsNull(field.GetCustomAttribute<SerializeReference>());
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
            Assert.AreEqual(9, guids.Length,
                "The shipped weather library must contain exactly nine profiles.");

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
                new[]
                {
                    "Clear", "Fair", "Fog", "Overcast", "Drizzle",
                    "Rain", "Snow", "Storm", "Blizzard",
                }, names);
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
            foreach (string serializedAssetPath in WeatherSelectionAssetPaths)
            {
                string yaml = File.ReadAllText(serializedAssetPath);
                foreach (string guid in profileGuids)
                {
                    Assert.That(yaml, Does.Contain($"guid: {guid}"),
                        $"{serializedAssetPath} does not reference weather profile {guid}.");
                }
                Assert.That(yaml, Does.Not.Contain("  - name: Clear"),
                    $"{serializedAssetPath} still contains inline weather presentation data.");
            }
        }

        [TestCase("Clear", 2.6f)]
        [TestCase("Fair", 4.4f)]
        [TestCase("Fog", 1.2f)]
        [TestCase("Overcast", 6.4f)]
        [TestCase("Drizzle", 7.6f)]
        [TestCase("Rain", 11f)]
        [TestCase("Snow", 5.2f)]
        [TestCase("Storm", 21.2f)]
        [TestCase("Blizzard", 22f)]
        public void WeatherProfiles_AuthorPhysicalTenMetreWindSpeed(
            string profileName, float expectedMetresPerSecond)
        {
            string path = $"{WeatherProfileFolder}/{profileName}.asset";
            SolWeatherProfileAsset profile =
                AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(path);
            Assert.IsNotNull(profile, $"Weather profile was not found at {path}.");
            Assert.AreEqual(expectedMetresPerSecond,
                profile.windSpeedMetresPerSecond, 1e-6f);
        }

        [Test]
        public void WindLag_ReachesExactFirstOrderResponse()
        {
            SolWindLag result = new SolWindLag(0f).Step(1f, 10f, 10f);
            Assert.AreEqual(0.6321f, result.Value, 1e-3f);
        }

        [Test]
        public void WindLag_IsFrameRateIndependent()
        {
            SolWindLag oneStep = new SolWindLag(0f).Step(1f, 10f, 10f);
            SolWindLag manySteps = new(0f);
            for (int i = 0; i < 100; i++)
                manySteps = manySteps.Step(1f, 0.1f, 10f);

            Assert.AreEqual(oneStep.Value, manySteps.Value, 1e-4f);
        }

        [Test]
        public void WindLag_ZeroDeltaIsANoOp()
        {
            SolWindLag initial = new(0.37f);
            Assert.AreEqual(initial.Value, initial.Step(1f, 0f, 10f).Value, 0f);
        }

        [Test]
        public void WindLag_ZeroTimeConstantSnaps()
        {
            Assert.AreEqual(1f, new SolWindLag(0f).Step(1f, 0.1f, 0f).Value, 0f);
        }

        /// <summary>
        /// Compare the complete 256-square packed bake to its independent floating-point
        /// reference. PNG quantization is the only allowed error; a changed seed, channel
        /// order or octave transform fails over the full domain rather than at a few lucky
        /// sample points.
        /// </summary>
        [Test]
        public void CloudNoiseBake_MatchesReferenceWithinOneByte()
        {
            Texture2D baked = LoadReadablePng(
                SolCloudNoiseBaker.NoiseTexturePath,
                SolCloudNoiseBaker.NoiseTextureSize);
            try
            {
                Color32[] pixels = baked.GetPixels32();
                float worstError = 0f;
                int size = SolCloudNoiseBaker.NoiseTextureSize;
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    Vector4 expected = SolCloudNoiseBaker.EvaluatePackedNoise(
                        (x + 0.5f) / size, (y + 0.5f) / size);
                    Color32 actual = pixels[y * size + x];
                    worstError = Mathf.Max(worstError,
                        Mathf.Abs(expected.x - actual.r / 255f),
                        Mathf.Abs(expected.y - actual.g / 255f),
                        Mathf.Abs(expected.z - actual.b / 255f),
                        Mathf.Abs(expected.w - actual.a / 255f));
                }

                Assert.Less(worstError, 1f / 255f,
                    "The packed PNG no longer reproduces the baker's reference FBM.");
            }
            finally
            {
                Object.DestroyImmediate(baked);
            }
        }

        /// <summary>
        /// Rotated octaves are only safe when every octave's lattice wraps. Checking both
        /// axes over a full cross-section catches the tempting but broken implementation
        /// that bakes a finite window from the old infinite procedural field.
        /// </summary>
        [Test]
        public void CloudNoiseBake_IsPeriodicAcrossBothAxes()
        {
            for (int i = 0; i <= 256; i++)
            {
                float t = i / 256f;
                AssertVectorEqual(
                    SolCloudNoiseBaker.EvaluatePackedNoise(0f, t),
                    SolCloudNoiseBaker.EvaluatePackedNoise(1f, t), 1e-5f);
                AssertVectorEqual(
                    SolCloudNoiseBaker.EvaluatePackedNoise(t, 0f),
                    SolCloudNoiseBaker.EvaluatePackedNoise(t, 1f), 1e-5f);
                AssertVectorEqual(
                    SolCloudNoiseBaker.EvaluateWeatherMap(0f, t),
                    SolCloudNoiseBaker.EvaluateWeatherMap(1f, t), 1e-5f);
                AssertVectorEqual(
                    SolCloudNoiseBaker.EvaluateWeatherMap(t, 0f),
                    SolCloudNoiseBaker.EvaluateWeatherMap(t, 1f), 1e-5f);
            }
        }

        [TestCase(SolCloudNoiseBaker.NoiseTexturePath)]
        [TestCase(SolCloudNoiseBaker.WeatherMapPath)]
        public void CloudBake_UsesRuntimeSafeImporterSettings(string assetPath)
        {
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            Assert.IsNotNull(texture, $"Cloud texture was not found at {assetPath}.");
            Assert.AreEqual(256, texture.width);
            Assert.AreEqual(256, texture.height);

            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            Assert.IsNotNull(importer);
            Assert.AreEqual(TextureWrapMode.Repeat, importer.wrapMode);
            Assert.IsTrue(importer.mipmapEnabled);
            Assert.IsFalse(importer.sRGBTexture);
            Assert.IsFalse(importer.isReadable);
            Assert.AreEqual(TextureImporterCompression.Uncompressed,
                importer.textureCompression);
            Assert.IsFalse(importer.crunchedCompression);
        }

        [Test]
        public void CloudBake_IsAddressableByTheRuntimeFallbackPaths()
        {
            Assert.IsNotNull(Resources.Load<Texture2D>(
                "SolEnvironment/Sol_CloudNoisePacked"));
            Assert.IsNotNull(Resources.Load<Texture2D>(
                "SolEnvironment/Sol_CloudWeatherMap"));
            Texture3D shapeVolume = Resources.Load<Texture3D>(
                "SolEnvironment/Sol_CloudShapeVolume");
            Texture3D detailVolume = Resources.Load<Texture3D>(
                "SolEnvironment/Sol_CloudDetailVolume");
            Assert.IsNotNull(shapeVolume);
            Assert.IsNotNull(detailVolume);
            Assert.AreEqual(SolCloudNoiseBaker.ShapeVolumeSize, shapeVolume.width);
            Assert.AreEqual(SolCloudNoiseBaker.ShapeVolumeSize, shapeVolume.height);
            Assert.AreEqual(SolCloudNoiseBaker.ShapeVolumeSize, shapeVolume.depth);
            Assert.AreEqual(SolCloudNoiseBaker.DetailVolumeSize, detailVolume.width);
            Assert.AreEqual(TextureWrapMode.Repeat, shapeVolume.wrapMode);
            Assert.AreEqual(TextureWrapMode.Repeat, detailVolume.wrapMode);
            Assert.Greater(shapeVolume.mipmapCount, 1);
            Assert.Greater(detailVolume.mipmapCount, 1);
        }

        /// <summary>
        /// The skybox must remain cloud-free and the dedicated shell marcher must use the
        /// existing baked packed noise. This pins both ordering and the texture-fetch budget.
        /// </summary>
        [Test]
        public void CloudDensity_IsBakedAndNoLongerRunsInTheSkybox()
        {
            string source = File.ReadAllText(CloudShaderPath);
            string skySource = File.ReadAllText(SkyboxShaderPath);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(CloudShaderPath);
            Assert.IsNotNull(shader);
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader),
                "The baked cloud shader has an import/compile error.");
            string density = ExtractFunction(source, "SolCloudDensity");
            Assert.That(density, Does.Not.Contain("Hash_Tchou"));
            Assert.That(density, Does.Contain("SAMPLE_TEXTURE2D_LOD"));
            Assert.That(density, Does.Contain("SAMPLE_TEXTURE3D_LOD"),
                "Medium/High cloud density is not volumetric.");
            Assert.That(density, Does.Contain("weatherWarp"),
                "The short-period cloud volume can repeat as a visible horizon grid.");
            Assert.That(density, Does.Contain("windShear"),
                "Cloud wind is only translating a rigid layer rather than shaping it.");
            Assert.That(density, Does.Contain("float threshold = saturate(1.0 - coverage)"),
                "Coverage no longer follows the authored skybox threshold convention.");
            Assert.That(density, Does.Contain("float cirrusThreshold"),
                "Unthresholded cirrus can integrate into a solid horizon band.");
            Assert.That(density, Does.Contain("float3 volumeUVW"),
                "The cloud mass is not sampled in three dimensions.");
            Assert.That(density, Does.Contain("dot(shapeVariants, _SolCloudVariationWeights)"),
                "Dated cloud variation must reuse the existing RGBA volume sample.");
            Assert.That(source, Does.Contain("historyWeight *= 1.0 - smoothstep"),
                "Unstable horizon rays are accumulating temporal history.");
            Assert.That(source, Does.Contain("SolCloudShellSegment"));
            Assert.That(source, Does.Contain("stepLength / shapeVoxelMetres"),
                "Distant ray steps are sampling mip zero and aliasing into a grid.");
            Assert.That(skySource, Does.Not.Contain("float rawDensity"),
                "Cloud density is executing in both the skybox and the renderer feature.");
        }

        /// <summary>
        /// The sky dome is drawn in every direction, but the world it sits over runs out
        /// at a finite distance. Anything celestial painted below the horizon shows
        /// through the gap past the edge of the terrain or the ocean clipmap, which is
        /// how a set moon ended up visible through the waterline. Every body must be
        /// clipped against the view ray's own elevation, not its own.
        /// </summary>
        [Test]
        public void CelestialBodies_AreOccludedBelowTheHorizon()
        {
            Shader sky = AssetDatabase.LoadAssetAtPath<Shader>(SkyboxShaderPath);
            Assert.IsNotNull(sky);
            Assert.IsFalse(ShaderUtil.ShaderHasError(sky),
                "The skybox shader has an import/compile error.");

            Shader body = AssetDatabase.LoadAssetAtPath<Shader>(CelestialBodyShaderPath);
            Assert.IsNotNull(body);
            Assert.IsFalse(ShaderUtil.ShaderHasError(body),
                "The celestial body shader has an import/compile error.");

            string skySource = File.ReadAllText(SkyboxShaderPath);
            Assert.That(skySource, Does.Contain("float horizonVisibility = smoothstep("),
                "The sky no longer clips celestial bodies against the horizon.");
            foreach (string mask in new[] { "float visibleSun", "float visibleMoon" })
            {
                int index = skySource.IndexOf(mask, System.StringComparison.Ordinal);
                Assert.Greater(index, 0, mask + " is missing.");
                Assert.That(skySource.Substring(index, 220),
                    Does.Contain("horizonVisibility"),
                    mask + " is drawn without the horizon test, so the body shows "
                         + "below the waterline once it has set.");
            }

            string bodySource = File.ReadAllText(CelestialBodyShaderPath);
            Assert.That(bodySource, Does.Contain("_SolSkyHorizon"),
                "Tertiary planets clip against a different horizon than the sky.");

            string timeOfDay = File.ReadAllText(TimeOfDaySourcePath);
            Assert.That(timeOfDay, Does.Contain("Shader.SetGlobalVector(_SolSkyHorizonID"),
                "Nothing publishes the horizon the billboards clip against.");
        }

        /// <summary>
        /// A low sun reddens because its light crosses more atmosphere, and that is the
        /// shader's job: the disc is the sun itself. Driving it from the ground-light
        /// ramp applied the atmosphere twice and, because that ramp reaches black at the
        /// horizon threshold, put the disc out before it ever reached the horizon.
        /// </summary>
        [Test]
        public void SunDisc_RedensByAirMassRatherThanTheGroundLightRamp()
        {
            string skySource = File.ReadAllText(SkyboxShaderPath);
            Assert.That(skySource, Does.Contain("float SolAirMass("),
                "The sky has no air-mass model to redden a low sun with.");
            Assert.That(skySource, Does.Contain("_SunDiscColor.rgb * sunExtinction"),
                "The sun disc is not reddened by the atmosphere it is seen through.");
            Assert.That(skySource, Does.Contain("SolApparentBodyDirection"),
                "Refraction no longer lifts a setting body clear of the horizon.");

            string timeOfDay = File.ReadAllText(TimeOfDaySourcePath);
            Assert.That(timeOfDay,
                Does.Not.Contain("Color sunDiscColor = cachedSunLightColor * sunDiscIntensity"),
                "The sun disc is driven from the ground-light colour again, which "
                    + "fades it to black before it reaches the horizon.");
            Assert.That(timeOfDay, Does.Not.Contain("* (1f - cloudiness * 0.75f)"),
                "The sun's aureole is dimmed by average cloud coverage as well as by "
                    + "the per-pixel transmittance the cloud composite already applies, "
                    + "so the sun stays dim even through a break in the deck.");
        }

        [Test]
        public void CloudShell_IntersectsContinuouslyAcrossTheHorizon()
        {
            const float radius = 6371000f;
            Vector3 center = new(0f, -radius, 0f);
            float previousStart = 0f;
            for (int i = 0; i <= 16; i++)
            {
                Vector3 direction = new Vector3(1f, i * 0.001f, 0f).normalized;
                Assert.IsTrue(SolCloudMath.TryIntersectShell(Vector3.zero, direction,
                    center, radius, 1500f, 3200f, 160000f, out float start, out float end));
                Assert.Greater(end, start);
                Assert.IsFalse(float.IsNaN(start) || float.IsInfinity(start));
                if (i > 0)
                    Assert.Less(Mathf.Abs(start - previousStart), 12000f,
                        "A small horizon direction change opened a planar-style seam.");
                previousStart = start;
            }
        }

        [Test]
        public void CloudExtinction_IsMonotonicAndIndependentOfBrightness()
        {
            float clear = SolCloudMath.DensityToTransmittance(0.2f, 1000f, 1.35f);
            float dense = SolCloudMath.DensityToTransmittance(1.2f, 1000f, 1.35f);
            Assert.That(clear, Is.InRange(0f, 1f));
            Assert.Less(dense, clear);
            Assert.Less(SolCloudMath.DensityToTransmittance(1.2f, 3000f, 1.35f), dense);
        }

        [Test]
        public void CloudDailyVariation_IsContinuousDistinctAndNormalized()
        {
            Vector4 today = SolCloudMath.DailyVariationWeights(84L, 12f);
            Vector4 anotherDay = SolCloudMath.DailyVariationWeights(392L, 12f);
            Vector4 beforeMidnight = SolCloudMath.DailyVariationWeights(84L, 23.999f);
            Vector4 afterMidnight = SolCloudMath.DailyVariationWeights(85L, 0f);

            Assert.AreEqual(1f, today.x + today.y + today.z + today.w, 0.0001f);
            Assert.Greater((today - anotherDay).sqrMagnitude, 0.0001f,
                "Widely separated dates resolved to the same cloud topology blend.");
            Assert.Less((beforeMidnight - afterMidnight).sqrMagnitude, 0.0001f,
                "Daily cloud topology jumps at midnight.");

            Vector2 phaseBefore = SolCloudMath.DailyCloudOffset(84L, 23.999f, 811, 12000f);
            Vector2 phaseAfter = SolCloudMath.DailyCloudOffset(85L, 0f, 811, 12000f);
            Assert.Less((phaseBefore - phaseAfter).sqrMagnitude, 0.01f);
        }

        /// <summary>
        /// The light march used to step a fixed lightStepMetres per sample, which covered
        /// under four kilometres at High against an optical path through the deck that is
        /// nearly ten at a low sun. Most of the extinction was never accumulated, so storm
        /// decks integrated bright and flat.
        /// </summary>
        [Test]
        public void CloudLightMarch_SpansTheShellRatherThanAFixedDistance()
        {
            string source = File.ReadAllText(CloudShaderPath);
            string march = ExtractFunction(source, "SolCloudLightTransmittance");
            Assert.That(march, Does.Contain("SolCloudShellExitDistance"),
                "The light march is not integrating the remaining path through the shell.");
            Assert.That(march, Does.Not.Contain("_SolCloudLighting.y * i"),
                "The light march is still stepping a fixed distance per sample.");
            Assert.That(march, Does.Contain("_SolCloudDetailDistance.z"),
                "The shell light march is not capped by maximumLightMarchMetres.");
            Assert.That(march, Does.Contain("float stepLength = marchLength / lightSteps"),
                "Light samples do not span the complete capped shell path.");
            Assert.That(march, Does.Not.Contain("min(marchLength / lightSteps"),
                "The light path is being truncated instead of distributed across all samples.");
            Assert.That(march, Does.Contain("stratumJitter"),
                "The low-count light march has no stratified jitter.");
            Assert.That(source, Does.Contain("float SolCloudShellExitDistance"));
        }

        [Test]
        public void CloudHybridStructure_PreservesMesostructureLightingAndDistanceDetail()
        {
            SolCloudRenderingProfile profile = AssetDatabase
                .LoadAssetAtPath<SolCloudRenderingProfile>("Assets/Settings/Sol_Clouds.asset");
            Assert.IsNotNull(profile);
            Assert.IsNull(profile.authoredStructureMap,
                "The shipped profile should exercise the packed-texture fallback path.");
            Assert.AreEqual(8000f, profile.structureScaleMetres, 0.01f);
            Assert.AreEqual(0.38f, profile.structureInfluence, 0.0001f);
            Assert.AreEqual(0.38f, profile.volumeTilingSuppression, 0.0001f);
            Assert.AreEqual(0.12f, profile.structureWarpStrength, 0.0001f);
            Assert.AreEqual(25000f, profile.microDetailFadeStartMetres, 0.01f);
            Assert.AreEqual(45000f, profile.microDetailFadeEndMetres, 0.01f);
            Assert.AreEqual(30000f, profile.maximumLightMarchMetres, 0.01f);
            Assert.AreEqual(0.18f, profile.surfaceGradientStrength, 0.0001f);
            Assert.AreEqual(0.72f, profile.mediumHistoryWeight, 0.0001f);
            Assert.AreEqual(0.84f, profile.highHistoryWeight, 0.0001f);

            string source = File.ReadAllText(CloudShaderPath);
            string density = ExtractFunction(source, "SolCloudDensity");
            Assert.That(density, Does.Contain("packedStructure.gb"));
            Assert.That(density, Does.Contain("float4(0.13, 0.22, 0.18, 0.12)"));
            Assert.That(density, Does.Contain("microFade"));
            Assert.That(source, Does.Contain("lightOpticalDepth"));
            Assert.That(source, Does.Contain("SolCloudMacroGradient"));
            Assert.That(source, Does.Contain("alphaNeighborhood"));
            Assert.That(source, Does.Contain("recoveredOpacity"));
            Assert.That(source, Does.Contain("_SolCloudDebugMode"));
            Assert.That(density, Does.Contain("_SolCloudTileBreakup"),
                "The periodic shape volume needs its decorrelated anti-tiling sample.");
            Assert.That(density, Does.Contain("breakupVariants"),
                "Anti-tiling must sample a second volume domain, not just rename a scalar.");
        }

        /// <summary>
        /// SolCloudState.Lerp resolves DominantFormation with a hard switch at t = 0.5.
        /// Formation-specific density shaping has to run off a blended weight vector instead
        /// or the whole sky changes shape in one frame mid-transition.
        /// </summary>

        static SolWeatherProfileAsset LoadProfile(string profileName)
        {
            SolWeatherProfileAsset profile =
                AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(
                    $"{WeatherProfileFolder}/{profileName}.asset");
            Assert.IsNotNull(profile, $"Weather profile '{profileName}' was not found.");
            return profile;
        }

        /// <summary>
        /// The regression for the mid-transition sky snap. DominantFormation resolves with a
        /// hard switch at t = 0.5, so deriving the shader's weight vector from that label put
        /// most of a formation change into a single frame. The weights now ride inside the
        /// state and move continuously; the label is kept only as a display value.
        /// </summary>
        [Test]
        public void CloudFormationBlend_IsCarriedThroughTheWeatherBlend()
        {
            SolCloudState from = LoadProfile("Overcast").ResolveCloudState();
            SolCloudState to = LoadProfile("Storm").ResolveCloudState();
            Assert.Greater((from.FormationBlend - to.FormationBlend).sqrMagnitude, 0.5f,
                "This pair must actually change formation or the test proves nothing.");

            const int steps = 200;
            Vector4 previous = from.FormationBlend;
            for (int i = 1; i <= steps; i++)
            {
                Vector4 blend = SolCloudState.Lerp(from, to, i / (float)steps).FormationBlend;
                Assert.AreEqual(1f, blend.x + blend.y + blend.z + blend.w, 1e-4f,
                    "A formation blend left the weight vector unnormalised.");
                Assert.Less((blend - previous).sqrMagnitude, 0.0025f,
                    "The formation blend stepped discontinuously mid-transition.");
                previous = blend;
            }

            Assert.Less((previous - to.FormationBlend).sqrMagnitude, 1e-6f,
                "The blend must arrive exactly on the target formation.");
        }

        /// <summary>
        /// ApplyWeather is the second blend in the chain -- authored sky toward weather by
        /// coverage. It must carry the weights too, or the continuity above is discarded
        /// before it reaches the renderer.
        /// </summary>
        [Test]
        public void CloudFormationBlend_SurvivesApplyWeather()
        {
            SolCloudState authored = SolCloudState.FromCompatibility(0.45f, 0.3f);
            SolCloudState storm = LoadProfile("Storm").ResolveCloudState();
            SolCloudState halfway = SolCloudState.Lerp(
                LoadProfile("Overcast").ResolveCloudState(), storm, 0.5f);

            SolCloudState resolved = SolCloudState.ApplyWeather(authored, halfway);
            Vector4 blend = resolved.FormationBlend;
            Assert.AreEqual(1f, blend.x + blend.y + blend.z + blend.w, 1e-4f);
            Assert.Greater(blend.y, 0.01f, "The stratus share was dropped by ApplyWeather.");
            Assert.Greater(blend.w, 0.01f, "The cumulonimbus share was dropped by ApplyWeather.");

            SolCloudState unchanged = SolCloudState.ApplyWeather(
                authored, LoadProfile("Clear").ResolveCloudState());
            Assert.Less((unchanged.FormationBlend - authored.FormationBlend).sqrMagnitude, 1e-6f,
                "Zero-coverage weather must leave the authored formation alone.");
        }

        [Test]
        public void CloudController_DoesNotRebuildFormationWeightsFromTheEnum()
        {
            Assert.That(File.ReadAllText(CloudStateSourcePath),
                Does.Not.Contain("FormationWeights(weather.Clouds.DominantFormation)"),
                "The controller is deriving shader weights from the snapped enum again.");
        }

        /// <summary>
        /// Strike scheduling reads the blended peak, not the target profile's bool. Reading
        /// the bool cut lightning off at master 0 while the lightning channel's own 0.4 delay
        /// held the published intensity at its storm value, silencing a still-stormy sky.
        /// </summary>
        [Test]
        public void LightningTapersWithTheBlendNotTheTargetProfile()
        {
            Assert.That(File.ReadAllText(WeatherManagerSourcePath),
                Does.Not.Contain("&& targetProfile.lightning"),
                "Lightning is gated on the target profile again instead of the blend.");

            SolWeatherTransitionTimings timings = new();
            Assert.Greater(timings.lightning.delay, 0f,
                "The lightning channel must lag, or this fix has nothing to protect.");
            Assert.AreEqual(0f, timings.lightning.Evaluate(timings.lightning.delay * 0.5f), 1e-5f,
                "Lightning intensity must still be at its from-value early in the transition.");
        }

        [Test]
        public void CloudFormationWeights_AreOneHotAndBlendContinuously()
        {
            Vector4 cumulus = SolCloudMath.FormationWeights(SolCloudFormation.Cumulus);
            Vector4 stratus = SolCloudMath.FormationWeights(SolCloudFormation.Stratus);
            Vector4 nimbus = SolCloudMath.FormationWeights(SolCloudFormation.Nimbostratus);
            Vector4 cumulonimbus = SolCloudMath.FormationWeights(SolCloudFormation.Cumulonimbus);

            Assert.AreEqual(1f, cumulus.x + cumulus.y + cumulus.z + cumulus.w, 0.0001f);
            Assert.AreEqual(1f, cumulonimbus.x + cumulonimbus.y + cumulonimbus.z
                + cumulonimbus.w, 0.0001f);
            Assert.Greater((cumulus - stratus).sqrMagnitude, 0.5f);
            Assert.Greater((stratus - nimbus).sqrMagnitude, 0.5f);
            Assert.Greater((nimbus - cumulonimbus).sqrMagnitude, 0.5f);

            Vector4 previous = cumulus;
            for (int i = 1; i <= 20; i++)
            {
                Vector4 blend = Vector4.Lerp(cumulus, cumulonimbus, i / 20f);
                Assert.AreEqual(1f, blend.x + blend.y + blend.z + blend.w, 0.0001f,
                    "A formation blend left the weight vector unnormalised.");
                Assert.Less((blend - previous).sqrMagnitude, 0.02f,
                    "A formation blend stepped discontinuously.");
                previous = blend;
            }

            Assert.That(File.ReadAllText(CloudShaderPath),
                Does.Not.Contain("formation < 1.5 ? 0.85 : 0.62"),
                "The vertical profile is still branching on the formation enum.");
        }

        /// <summary>
        /// The detail volume packs three Worley bands at 4, 8 and 16 cells. Collapsing them
        /// into one weighted sum made every formation erode identically, and baking them at
        /// 32 voxels left the mid and high bands below their own Nyquist limit, so eroding
        /// against them added aliasing rather than form.
        /// </summary>
        [Test]
        public void CloudDetail_ResolvesAndUsesItsThreeWorleyBandsSeparately()
        {
            const int highestWorleyCellCount = 16;
            Assert.GreaterOrEqual(SolCloudNoiseBaker.DetailVolumeSize / highestWorleyCellCount, 4,
                "The highest detail band is baked below its own Nyquist limit.");

            string density = ExtractFunction(
                File.ReadAllText(CloudShaderPath), "SolCloudDensity");
            Assert.That(density, Does.Not.Contain("dot(detailNoise, float3(0.625, 0.25, 0.125))"),
                "The three detail bands are still collapsed into a single scalar.");
            Assert.That(density, Does.Contain("detailNoise.r"));
            Assert.That(density, Does.Contain("detailNoise.g"));
            Assert.That(density, Does.Contain("detailNoise.b"));
            Assert.That(density, Does.Contain("_SolCloudFormationWeights"),
                "Detail erosion is not formation aware.");
            Assert.That(density, Does.Not.Contain("heightFraction * 0.82"),
                "The shape volume is still sampled with a squashed vertical axis.");
        }

        [Test]
        public void CloudShadowMap_HasQualityDerivedBudgets()
        {
            SolCloudRenderingProfile profile = AssetDatabase
                .LoadAssetAtPath<SolCloudRenderingProfile>("Assets/Settings/Sol_Clouds.asset");
            Assert.IsNotNull(profile, "The shipped cloud profile is missing.");
            Assert.IsTrue(profile.enableWorldCloudShadows,
                "New serialised fields default to zero unless written into the asset.");
            Assert.AreEqual(16000f, profile.ShadowFootprint(SolCloudQuality.Low), 0.01f);
            Assert.AreEqual(24000f, profile.ShadowFootprint(SolCloudQuality.Medium), 0.01f);
            Assert.AreEqual(32000f, profile.ShadowFootprint(SolCloudQuality.High), 0.01f);
            Assert.That(File.ReadAllText(CloudShaderPath), Does.Contain("float borderFade"),
                "The cloud-shadow footprint still has a hard square boundary.");

            Assert.Less(profile.ShadowResolution(SolCloudQuality.Low),
                profile.ShadowResolution(SolCloudQuality.Medium));
            Assert.Less(profile.ShadowResolution(SolCloudQuality.Medium),
                profile.ShadowResolution(SolCloudQuality.High));
            Assert.LessOrEqual(profile.ShadowSamples(SolCloudQuality.Low),
                profile.ShadowSamples(SolCloudQuality.High));
            Assert.LessOrEqual(profile.ShadowUpdatesPerSecond(SolCloudQuality.Low),
                profile.ShadowUpdatesPerSecond(SolCloudQuality.High));
        }

        /// <summary>
        /// The shadow map is handed to the dominant light as a cookie, and that light belongs
        /// to TimeOfDay. Borrowing it has to be exactly reversible.
        /// </summary>
        [Test]
        public void CloudShadowCookie_RestoresTheBorrowedLightExactly()
        {
            GameObject host = new("SolCloudCookieTestLight");
            UnityEngine.Rendering.RTHandle handle = null;
            try
            {
                Light light = host.AddComponent<Light>();
                light.type = LightType.Directional;
                UniversalAdditionalLightData additional =
                    host.AddComponent<UniversalAdditionalLightData>();

                Texture2D originalCookie = Texture2D.grayTexture;
                Vector2 originalSize = new(37f, 91f);
                Vector2 originalOffset = new(-5f, 13f);
                light.cookie = originalCookie;
                additional.lightCookieSize = originalSize;
                additional.lightCookieOffset = originalOffset;

                System.Type passType = typeof(SolCloudRendererFeature)
                    .GetNestedType("CloudPass", BindingFlags.NonPublic);
                object pass = System.Activator.CreateInstance(passType, nonPublic: true);
                handle = UnityEngine.Rendering.RTHandles.Alloc(4, 4, name: "_SolCloudCookieTest");
                passType.GetField("_shadowMap", BindingFlags.NonPublic | BindingFlags.Instance)
                    .SetValue(pass, handle);

                const BindingFlags instanceMethod =
                    BindingFlags.NonPublic | BindingFlags.Instance;
                passType.GetMethod("LeaseCookie", instanceMethod).Invoke(pass, new object[]
                {
                    light, Vector3.zero, Vector3.right, Vector3.up, 12000f,
                });
                Assert.AreSame(handle.rt, light.cookie,
                    "The shadow map was not leased onto the light.");
                Assert.AreEqual(12000f, additional.lightCookieSize.x, 0.001f);

                passType.GetMethod("ReleaseCookie", instanceMethod).Invoke(pass, null);
                Assert.AreSame(originalCookie, light.cookie);
                Assert.AreEqual(originalSize, additional.lightCookieSize);
                Assert.AreEqual(originalOffset, additional.lightCookieOffset);

                passType.GetMethod("LeaseCookie", instanceMethod).Invoke(pass, new object[]
                {
                    light, Vector3.zero, Vector3.right, Vector3.up, 12000f,
                });
                Texture2D replacementCookie = Texture2D.blackTexture;
                Vector2 replacementSize = new(73f, 29f);
                light.cookie = replacementCookie;
                additional.lightCookieSize = replacementSize;
                passType.GetMethod("ReleaseCookie", instanceMethod).Invoke(pass, null);
                Assert.AreSame(replacementCookie, light.cookie,
                    "Releasing Sol's lease overwrote a newer cookie owner.");
                Assert.AreEqual(replacementSize, additional.lightCookieSize,
                    "Releasing Sol's lease overwrote a newer cookie projection.");
            }
            finally
            {
                handle?.Release();
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void CloudAdvection_WrapsWithoutLosingMotionAcrossLongTimeLapses()
        {
            const float period = 12000f;
            Vector2 advanced = SolCloudMath.AdvancePeriodicOffset(
                new Vector2(11998f, 3f), new Vector2(7f, -8f), period);
            Assert.AreEqual(5f, advanced.x, 0.0001f);
            Assert.AreEqual(11995f, advanced.y, 0.0001f);

            Vector2 delta = SolCloudMath.PeriodicOffsetDelta(
                advanced, new Vector2(11998f, 3f), period);
            Assert.AreEqual(7f, delta.x, 0.0001f);
            Assert.AreEqual(-8f, delta.y, 0.0001f);

            Vector2 veryLong = SolCloudMath.AdvancePeriodicOffset(
                Vector2.zero, new Vector2(98765432f, -98765432f), period);
            Assert.That(veryLong.x, Is.InRange(0f, period));
            Assert.That(veryLong.y, Is.InRange(0f, period));

            Vector2 wind = new(73f, -41f);
            Vector2 rotatedStart = SolCloudMath.WrapRotatedOffset(
                new Vector2(11880f, 11940f), period);
            Vector2 rotatedEnd = SolCloudMath.AdvanceRotatedPeriodicOffset(
                rotatedStart, wind, period);
            Vector2 rotatedDelta = SolCloudMath.RotatedPeriodicOffsetDelta(
                rotatedEnd, rotatedStart, period);
            Assert.AreEqual(wind.x, rotatedDelta.x, 0.005f);
            Assert.AreEqual(wind.y, rotatedDelta.y, 0.005f);
        }

        [Test]
        public void CloudAmbient_DarkensWithRainStormAndFormationDepth()
        {
            float clear = SolCloudMath.CloudAmbientMultiplier(0f, 0f, 0.62f);
            float rain = SolCloudMath.CloudAmbientMultiplier(0.44f, 0.65f, 0.82f);
            float storm = SolCloudMath.CloudAmbientMultiplier(0.78f, 0.95f, 0.92f);

            Assert.That(clear, Is.InRange(0f, 1f));
            Assert.Less(rain, clear);
            Assert.Less(storm, rain);
        }

        [Test]
        public void CloudFormationInterpolation_SwitchesLabelAtMidpointOnly()
        {
            SolCloudState a = new(SolCloudFormation.Cumulus, 0.2f, 0.5f, 0.7f,
                1500f, 3000f, 0.4f, 0f, 0.2f, Vector2.zero, Vector2.zero, Vector2.zero, 0.6f);
            SolCloudState b = new(SolCloudFormation.Cumulonimbus, 1f, 0.2f, 1.2f,
                900f, 7000f, 1f, 0.8f, 0.1f, Vector2.one, Vector2.one, Vector2.one, 0.95f);
            SolCloudState early = SolCloudState.Lerp(a, b, 0.49f);
            SolCloudState late = SolCloudState.Lerp(a, b, 0.5f);
            Assert.AreEqual(SolCloudFormation.Cumulus, early.DominantFormation);
            Assert.AreEqual(SolCloudFormation.Cumulonimbus, late.DominantFormation);
            Assert.AreEqual(Mathf.Lerp(a.Thickness, b.Thickness, 0.49f), early.Thickness, 0.001f);
        }

        /// <summary>
        /// Zero cloudiness leaves the authored deck alone *when the profile adds no coverage
        /// bias*. The shipped Clear deliberately does add one now -- it is the state that
        /// clears the sky -- so this pins the influence semantics rather than that profile.
        /// Fair is the profile that inherits the old preserve-the-authored-deck identity.
        /// </summary>
        [Test]
        public void ZeroCloudinessPreservesAuthoredCoverWhenBiasIsZero()
        {
            SolCloudState authored = new(SolCloudFormation.Cumulus, 0.56f, 0.4f, 0.4f,
                1080f, 3600f, 0.45f, 0f, 0.18f, Vector2.zero, Vector2.zero,
                Vector2.zero, 0.65f);
            SolCloudState clearWeather = new(SolCloudFormation.Cumulus, 0f, 0.52f, 0.72f,
                1500f, 3200f, 0.45f, 0f, 0.2f, Vector2.zero, Vector2.zero,
                Vector2.zero, 0.62f);

            SolCloudState resolved = SolCloudState.ApplyWeather(authored, clearWeather);

            Assert.AreEqual(authored, resolved);
        }

        [Test]
        public void FullWeatherCloudiness_ReachesOvercastFormation()
        {
            SolCloudState authored = SolCloudState.FromCompatibility(0.45f, 0.3f);
            SolCloudState storm = new(SolCloudFormation.Cumulonimbus, 1f, 0.64f, 1.2f,
                900f, 7200f, 0.95f, 0.72f, 0.16f, Vector2.zero, Vector2.zero,
                Vector2.zero, 0.92f);

            SolCloudState resolved = SolCloudState.ApplyWeather(authored, storm);

            Assert.AreEqual(1f, resolved.Coverage, 0.0001f);
            Assert.AreEqual(SolCloudFormation.Cumulonimbus, resolved.DominantFormation);
            Assert.AreEqual(storm.Thickness, resolved.Thickness, 0.0001f);
        }

        [Test]
        public void CloudQualityProfile_UsesBoundedShippingBudgets()
        {
            SolCloudRenderingProfile profile = ScriptableObject.CreateInstance<SolCloudRenderingProfile>();
            try
            {
                Assert.AreEqual(4, profile.ViewSteps(SolCloudQuality.Low));
                Assert.AreEqual(32, profile.ViewSteps(SolCloudQuality.Medium));
                Assert.AreEqual(48, profile.ViewSteps(SolCloudQuality.High));
                Assert.AreEqual(0.5f, profile.RenderScale(SolCloudQuality.Low), 0.001f);
                Assert.Greater(profile.RenderScale(SolCloudQuality.Medium),
                    profile.RenderScale(SolCloudQuality.Low));
                Assert.Greater(profile.RenderScale(SolCloudQuality.High),
                    profile.RenderScale(SolCloudQuality.Medium));
                Assert.AreEqual(0, profile.LightSteps(SolCloudQuality.Low));
                Assert.Greater(profile.HistoryWeight(SolCloudQuality.High),
                    profile.HistoryWeight(SolCloudQuality.Medium));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void WeatherCompatibilityConstructor_MapsCloudProperties()
        {
            SolWeatherState state = new(0.64f, 0.31f, 0f, Vector3.right, 4f,
                0f, 1f, 0f, 1f, 0f);
            Assert.AreEqual(0.64f, state.Cloudiness, 0.0001f);
            Assert.AreEqual(0.31f, state.CloudErosion, 0.0001f);
            Assert.AreEqual(state.Cloudiness, state.Clouds.Coverage, 0.0001f);
        }

        [Test]
        public void WaterWindReferenceSpeed_MatchesBetweenCpuAndShader()
        {
            string source = File.ReadAllText(WaterWaveIncludePath);
            float shaderValue = ParseShaderDefine(
                source, "SOL_WATER_WIND_REFERENCE_SPEED");
            Assert.AreEqual(SolWaterWaveEvaluator.WindResponseReferenceSpeed,
                shaderValue, 1e-6f);
        }

        [Test]
        public void WaterCascadeDomains_MatchAcrossComputeShaderIncludeAndCpu()
        {
            float[] expected = { 5f, 20f, 100f, 600f };
            CollectionAssert.AreEqual(expected, ParseCascadeTable(
                File.ReadAllText(WaterFftComputePath), "CascadeSize"));
            CollectionAssert.AreEqual(expected, ParseCascadeTable(
                File.ReadAllText(WaterWaveIncludePath), "SolWaterCascadeSize"));
            CollectionAssert.AreEqual(expected, ParseCascadeTable(
                File.ReadAllText(WaterFftReadbackPath), "CascadeSize"));

            for (int cascade = 0; cascade < expected.Length; cascade++)
                Assert.AreEqual(expected[cascade], SolWaterFftReadback.CascadeSize(cascade));
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

        static float ParseShaderDefine(string source, string defineName)
        {
            Match match = Regex.Match(source,
                $@"^\s*#define\s+{Regex.Escape(defineName)}\s+(?<value>[0-9]+(?:\.[0-9]+)?)",
                RegexOptions.Multiline);
            Assert.IsTrue(match.Success, $"Shader define {defineName} was not found.");
            return float.Parse(match.Groups["value"].Value,
                CultureInfo.InvariantCulture);
        }

        static float[] ParseCascadeTable(string source, string functionName)
        {
            Match signature = Regex.Match(source,
                $@"\b{Regex.Escape(functionName)}\s*\(\s*(?:u?int)\s+cascade\s*\)\s*(?:=>|\{{)");
            Assert.IsTrue(signature.Success,
                $"Cascade function {functionName} was not found.");
            int functionStart = signature.Index;
            int statementEnd = source.IndexOf(';', functionStart);
            Assert.Greater(statementEnd, functionStart,
                $"Cascade function {functionName} has no return statement terminator.");
            string statement = source.Substring(
                functionStart, statementEnd - functionStart + 1);
            const string Number = @"[0-9]+(?:\.[0-9]+)?";
            Match match = Regex.Match(statement,
                $@"cascade\s*==\s*0\s*\?\s*(?<a>{Number})[fF]?\s*:\s*"
                + $@"cascade\s*==\s*1\s*\?\s*(?<b>{Number})[fF]?\s*:\s*"
                + $@"cascade\s*==\s*2\s*\?\s*(?<c>{Number})[fF]?\s*:\s*"
                + $@"(?<d>{Number})[fF]?",
                RegexOptions.Singleline);
            Assert.IsTrue(match.Success,
                $"Cascade function {functionName} does not expose the expected four-entry table.");

            return new[]
            {
                ParseInvariant(match.Groups["a"].Value),
                ParseInvariant(match.Groups["b"].Value),
                ParseInvariant(match.Groups["c"].Value),
                ParseInvariant(match.Groups["d"].Value),
            };
        }

        static float ParseInvariant(string value)
            => float.Parse(value, CultureInfo.InvariantCulture);

        static Texture2D LoadReadablePng(string assetPath, int expectedSize)
        {
            byte[] bytes = File.ReadAllBytes(assetPath);
            Texture2D texture = new(2, 2, TextureFormat.RGBA32, false, true);
            Assert.IsTrue(ImageConversion.LoadImage(texture, bytes, false),
                $"Could not decode {assetPath}.");
            Assert.AreEqual(expectedSize, texture.width);
            Assert.AreEqual(expectedSize, texture.height);
            return texture;
        }

        static void AssertVectorEqual(Vector4 expected, Vector4 actual, float tolerance)
        {
            Assert.AreEqual(expected.x, actual.x, tolerance);
            Assert.AreEqual(expected.y, actual.y, tolerance);
            Assert.AreEqual(expected.z, actual.z, tolerance);
            Assert.AreEqual(expected.w, actual.w, tolerance);
        }

        static string ExtractFunction(string source, string functionName)
        {
            Match signature = Regex.Match(source,
                $@"\b(?:float|float[234])\s+{Regex.Escape(functionName)}\s*\([^)]*\)\s*\{{");
            Assert.IsTrue(signature.Success, $"Shader function {functionName} was not found.");

            int openBrace = source.IndexOf('{', signature.Index);
            int depth = 0;
            for (int i = openBrace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(openBrace, i - openBrace + 1);
            }

            Assert.Fail($"Shader function {functionName} has no closing brace.");
            return string.Empty;
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

        // --- Snow, blizzard, transition choreography, and cloud definition ---------

        /// <summary>
        /// Temperature still decides the split by default, but a profile may bias it so a
        /// Blizzard authored in a temperate scene still snows. Whatever the bias, the two
        /// halves must continue to sum to the precipitation that was actually authored.
        /// </summary>
        [TestCase(-1f)]
        [TestCase(0f)]
        [TestCase(0.35f)]
        [TestCase(1f)]
        public void SnowBias_ShiftsTheSplitWithoutBreakingConservation(float bias)
        {
            const float precipitation = 0.8f;
            for (float temperature = -12f; temperature <= 24f; temperature += 1.5f)
            {
                float snowFraction = Mathf.Clamp01(
                    Mathf.InverseLerp(2f, -1f, temperature) + bias);
                float rain = precipitation * (1f - snowFraction);
                float snow = precipitation * snowFraction;

                Assert.That(rain + snow, Is.EqualTo(precipitation).Within(1e-5f),
                    $"Rain and snow stopped summing to precipitation at {temperature} C.");
                Assert.That(snowFraction, Is.InRange(0f, 1f));
            }

            if (bias >= 1f)
            {
                Assert.That(Mathf.Clamp01(Mathf.InverseLerp(2f, -1f, 25f) + bias),
                    Is.EqualTo(1f).Within(1e-5f),
                    "A fully biased profile must snow even in warm weather.");
            }
        }

        /// <summary>
        /// The environment world owns the split. Keep the bias reaching it, and keep
        /// ResolveSnowFraction argument-free so the accumulation and publish steps cannot
        /// disagree within one frame.
        /// </summary>
        [Test]
        public void SnowBias_ReachesTheEnvironmentSimulation()
        {
            string source = File.ReadAllText(
                "Assets/Earth-Sky-Water/Scripts/Core/SolEnvironmentWorld.cs");

            Assert.That(source, Does.Contain("float ResolveSnowFraction()"),
                "ResolveSnowFraction must stay argument-free.");
            Assert.That(source, Does.Contain("+ _snowBias"),
                "The authored bias must shift the temperature split.");
            Assert.That(source, Does.Contain("weather.CurrentState.SnowBias"),
                "The bias must be mirrored out of the weather authority.");
        }

        /// <summary>
        /// Rain used to be driven from SolWeatherState.RainIntensity, which is total
        /// precipitation, so rain fell at -10 C. The VFX must read the environment's
        /// temperature split instead.
        /// </summary>
        [Test]
        public void PrecipitationVfx_UsesTheSplitRatherThanTotalPrecipitation()
        {
            string source = File.ReadAllText(
                "Assets/Earth-Sky-Water/Scripts/Management/SolRainVfxController.cs");

            Assert.That(source, Does.Contain("precipitation.Rain"),
                "Rain VFX must read the environment's liquid share.");
            Assert.That(source, Does.Contain("precipitation.Snow"),
                "Snow VFX must read the environment's frozen share.");
            Assert.That(source, Does.Not.Contain("state.RainIntensity * exposure"),
                "The controller must no longer drive rain from total precipitation.");
        }


        [Test]
        public void WeatherSequencer_IsSeededAndReproducible()
        {
            SolWeatherRandom a = SolWeatherRandom.FromSeed(1427, 0x5745415448UL);
            SolWeatherRandom b = SolWeatherRandom.FromSeed(1427, 0x5745415448UL);
            for (int i = 0; i < 1000; i++)
            {
                float left = a.NextFloat01();
                Assert.AreEqual(left, b.NextFloat01(), 0f,
                    "Two streams from the same seed diverged.");
                Assert.That(left, Is.InRange(0f, 1f));
            }

            SolWeatherRandom other = SolWeatherRandom.FromSeed(1428, 0x5745415448UL);
            SolWeatherRandom baseline = SolWeatherRandom.FromSeed(1427, 0x5745415448UL);
            bool diverged = false;
            for (int i = 0; i < 8 && !diverged; i++)
                diverged = !Mathf.Approximately(other.NextFloat01(), baseline.NextFloat01());
            Assert.IsTrue(diverged, "Two different seeds produced the same opening sequence.");
        }

        /// <summary>
        /// The stream position is the whole point of capturing it: a restored save must
        /// replay the identical future, not merely a same-looking one.
        /// </summary>
        [Test]
        public void WeatherSequencer_ResumesExactlyFromACapturedState()
        {
            SolWeatherRandom live = SolWeatherRandom.FromSeed(99, 0x5745415448UL);
            for (int i = 0; i < 37; i++)
                live.NextFloat01();

            ulong captured = live.State;
            float[] expected = new float[20];
            for (int i = 0; i < expected.Length; i++)
                expected[i] = live.NextFloat01();

            SolWeatherRandom restored = new() { State = captured };
            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], restored.NextFloat01(), 0f,
                    $"Restored stream diverged at draw {i}.");
        }

        /// <summary>
        /// The daily climate model was already deterministic while selection, hold duration
        /// and lightning drew from the global generator, so weather could not be replayed and
        /// any gameplay code seeding UnityEngine.Random perturbed the sky.
        /// </summary>
        [Test]
        public void WeatherSequencer_DoesNotTouchUnityRandom()
        {
            Assert.That(File.ReadAllText(WeatherManagerSourcePath),
                Does.Not.Contain("UnityEngine.Random"),
                "The weather manager is drawing from the global generator again.");

            // Comparing Random.state structs would be vacuous -- JsonUtility does not
            // serialise its private fields -- so observe the generator's next draw instead.
            UnityEngine.Random.State entry = UnityEngine.Random.state;
            try
            {
                UnityEngine.Random.InitState(12345);
                float undisturbed = UnityEngine.Random.value;

                UnityEngine.Random.InitState(12345);
                SolWeatherRandom sequencer = SolWeatherRandom.FromSeed(7, 0x5745415448UL);
                for (int i = 0; i < 500; i++)
                    sequencer.Range(0.1f, 0.3f);

                Assert.AreEqual(undisturbed, UnityEngine.Random.value, 0f,
                    "Driving the weather sequencer advanced the global Unity generator.");
            }
            finally
            {
                UnityEngine.Random.state = entry;
            }
        }

        [Test]
        public void WeatherSnapshot_RejectsAForeignVersion()
        {
            Assert.AreEqual(1, SolWeatherManager.SnapshotVersion);

            string source = File.ReadAllText(WeatherManagerSourcePath);
            Assert.That(source, Does.Contain("if (snapshot.Version != SnapshotVersion)"),
                "RestoreSnapshot no longer gates on the contract version.");
            Assert.That(source, Does.Contain("_sequencer = new SolWeatherRandom { State = snapshot.SequencerState };"),
                "RestoreSnapshot no longer restores the sequencer position.");
            Assert.That(source, Does.Contain("_previewActive ? _previewRestoreFrom : _from"),
                "CaptureSnapshot can bake an editor A/B preview into a save again.");
        }

        /// <summary>
        /// Round-trips the payload itself. The manager is not instantiated here on purpose:
        /// this suite loads no scenes, and SolWeatherManager is [ExecuteAlways] with a
        /// singleton and a coordinator registration, so building one would leave objects in
        /// whichever scene the runner happens to have open.
        /// </summary>
        [Test]
        public void WeatherSnapshot_CarriesEveryFieldItRestores()
        {
            SolWeatherChannels from = SolWeatherChannels.From(LoadProfile("Clear"));
            SolWeatherChannels presented = SolWeatherChannels.From(LoadProfile("Storm"));
            SolWeatherSnapshot snapshot = new(
                SolWeatherManager.SnapshotVersion, "Storm", 3, from, presented,
                0.42f, 7.5f, 128f, 63f, 2.25f, 0.18f, 4242L, 1427, 0xDEADBEEFCAFEUL);

            Assert.AreEqual(SolWeatherManager.SnapshotVersion, snapshot.Version);
            Assert.AreEqual("Storm", snapshot.TargetProfileName);
            Assert.AreEqual(3, snapshot.TargetIndex);
            Assert.AreEqual(0.42f, snapshot.Blend, 1e-6f);
            Assert.AreEqual(7.5f, snapshot.HoursRemaining, 1e-6f);
            Assert.AreEqual(128f, snapshot.WindAngleDegrees, 1e-6f);
            Assert.AreEqual(63f, snapshot.WindNoiseTime, 1e-6f);
            Assert.AreEqual(2.25f, snapshot.SecondsUntilStrike, 1e-6f);
            Assert.AreEqual(0.18f, snapshot.DailyFog, 1e-6f);
            Assert.AreEqual(4242L, snapshot.ClimateWorldDay);
            Assert.AreEqual(1427, snapshot.ClimateSeed);
            Assert.AreEqual(0xDEADBEEFCAFEUL, snapshot.SequencerState);
            Assert.AreEqual(presented.dim, snapshot.Presented.dim, 1e-6f);
            Assert.AreEqual(from.cloudiness, snapshot.From.cloudiness, 1e-6f);
        }

        /// <summary>
        /// SolEnvironmentWorld.climateSeed was carried in its snapshot but fed no
        /// computation, so a scene could report one seed while the climate model used
        /// another. The manager owns it now.
        /// </summary>
        [Test]
        public void WeatherSeed_IsOwnedByTheWeatherManager()
        {
            Assert.That(File.ReadAllText(
                    "Assets/Earth-Sky-Water/Scripts/Core/SolEnvironmentWorld.cs"),
                Does.Contain("weather != null ? weather.climateSeed : climateSeed"),
                "The environment world is reporting its own unused seed again.");

            foreach (string containerPath in WeatherSelectionAssetPaths)
            {
                string yaml = File.ReadAllText(containerPath);
                var seeds = new System.Collections.Generic.HashSet<string>();
                foreach (Match match in Regex.Matches(yaml, @"^\s*climateSeed:\s*(-?\d+)\s*$",
                    RegexOptions.Multiline))
                {
                    seeds.Add(match.Groups[1].Value);
                }
                Assert.That(seeds.Count, Is.LessThanOrEqualTo(1),
                    $"{containerPath} carries disagreeing climate seeds: "
                    + string.Join(", ", seeds));
            }
        }


        /// <summary>
        /// The deck TimeOfDay authors, which every profile's cloudiness is an influence
        /// toward overcast *from*. Nothing pinned this before, so retuning the authored sky
        /// would have silently shifted the effective cover of all nine weather states.
        /// </summary>
        const float NominalAuthoredCoverage = SolWeatherAdjacency.NominalAuthoredCoverage;

        [Test]
        public void AuthoredCloudDeck_MatchesTheProfileTuningBaseline()
        {
            foreach (string containerPath in WeatherSelectionAssetPaths)
            {
                string yaml = File.ReadAllText(containerPath);
                MatchCollection matches = Regex.Matches(
                    yaml, @"^\s*cloudCoverageDay:\s*([0-9.]+)\s*$", RegexOptions.Multiline);
                Assert.That(matches.Count, Is.GreaterThan(0),
                    $"{containerPath} authors no TimeOfDay cloud deck.");

                foreach (Match match in matches)
                {
                    float authoredDay = float.Parse(
                        match.Groups[1].Value, CultureInfo.InvariantCulture);
                    Assert.AreEqual(NominalAuthoredCoverage, 1f - authoredDay, 1e-3f,
                        $"{containerPath} moved the authored deck the weather ladder is "
                        + "tuned against. Retune the profiles or restore the deck.");
                }
            }
        }

        /// <summary>
        /// cloudCoverageBias is documented as independent of how far cloudiness overrides
        /// the authored sky, and the shader adds it straight into coverage -- but it used to
        /// be lerped by that same influence, so at cloudiness 0 it did nothing at all. That
        /// is why no weather could produce a sky clearer than whatever TimeOfDay authored.
        /// </summary>
        [Test]
        public void CoverageBias_AppliesIndependentlyOfCloudiness()
        {
            SolCloudState authored = SolCloudState.FromCompatibility(0.563f, 0.4f);
            SolCloudState clearing = new(SolCloudFormation.Cumulus, 0f, 0.55f, 0.62f,
                1700f, 2200f, 0.35f, 0f, 0.22f, 0.16f, 0.18f, -0.5f,
                Vector2.zero, Vector2.zero, Vector2.zero, 0.55f);

            SolCloudState resolved = SolCloudState.ApplyWeather(authored, clearing);
            Assert.AreEqual(-0.5f, resolved.CoverageBias, 1e-4f,
                "A zero-cloudiness profile still cannot thin the authored deck.");
            Assert.AreEqual(authored.Coverage, resolved.Coverage, 1e-4f,
                "The bias must nudge cover without also overriding the authored sky.");
        }

        [Test]
        public void ClearProfile_ReachesATrulyCloudlessSky()
        {
            SolWeatherProfileAsset clear = LoadProfile("Clear");
            float centre = clear.ResolveEffectiveCoverage(NominalAuthoredCoverage);
            Assert.That(centre, Is.LessThan(0.12f),
                "Clear must read as a clear sky, not as the authored half-deck.");

            Assert.That(clear.cloudCoverageVariance, Is.GreaterThan(0.1f),
                "Clear days must vary, or every clear day renders identically.");
            Assert.That(centre - clear.cloudCoverageVariance, Is.LessThanOrEqualTo(0f),
                "Clear must be able to reach a genuinely cloudless day.");
            Assert.That(centre + clear.cloudCoverageVariance, Is.GreaterThan(0.15f),
                "Clear must also be able to reach a partly clouded day.");

            SolWeatherProfileAsset fair = LoadProfile("Fair");
            Assert.That(fair.ResolveEffectiveCoverage(NominalAuthoredCoverage),
                Is.GreaterThan(centre + 0.15f),
                "Fair must stay a distinctly cloudier state than Clear.");
        }

        /// <summary>
        /// A cloudless sky should be the exception rather than a third of the year. Samples
        /// the real per-day draw instead of assuming it is uniform: the day-boundary
        /// crossfade narrows the distribution, so the clamped share is noticeably smaller
        /// than the variance on its own suggests.
        /// </summary>
        [Test]
        public void ClearDays_AreMostlyLightlyCloudedRatherThanCloudless()
        {
            SolWeatherProfileAsset clear = LoadProfile("Clear");
            float centre = clear.ResolveEffectiveCoverage(NominalAuthoredCoverage);

            const int days = 512;
            int cloudless = 0;
            int carryingCloud = 0;
            for (long day = 0; day < days; day++)
            {
                float offset = SolWeatherManager.EvaluateDailyCoverageOffset(day, 12f, 1427);
                float cover = Mathf.Clamp01(centre + clear.cloudCoverageVariance * offset);
                if (cover <= 1e-6f)
                    cloudless++;
                if (cover > 0.1f)
                    carryingCloud++;
            }

            float cloudlessShare = cloudless / (float)days;
            Assert.That(cloudlessShare, Is.GreaterThan(0.02f),
                "Clear can no longer reach a genuinely cloudless day.");
            Assert.That(cloudlessShare, Is.LessThan(0.22f),
                $"Cloudless days are meant to be the exception, but {cloudlessShare:P0} of "
                + "days reach one.");
            Assert.That(carryingCloud, Is.GreaterThan(days / 8),
                "Clear almost never carries cloud, so the state reads flat.");
        }

        /// <summary>
        /// Sorted by the cover they actually render, the shipped profiles must form one
        /// ladder with no hole in it. The set used to start at the authored deck and only
        /// climb, so there was no rung below 0.563 at all.
        /// </summary>
        [Test]
        public void WeatherProfiles_FormOneMonotonicEffectiveCoverageLadder()
        {
            var ladder = new List<(string Name, float Coverage)>();
            foreach (string guid in AssetDatabase.FindAssets(
                "t:SolWeatherProfileAsset", new[] { WeatherProfileFolder }))
            {
                SolWeatherProfileAsset profile =
                    AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(
                        AssetDatabase.GUIDToAssetPath(guid));
                ladder.Add((profile.name,
                    profile.ResolveEffectiveCoverage(NominalAuthoredCoverage)));
            }
            ladder.Sort((left, right) => left.Coverage.CompareTo(right.Coverage));

            Assert.That(ladder[0].Coverage, Is.LessThan(0.12f),
                "The set has no genuinely clear rung.");
            Assert.That(ladder[ladder.Count - 1].Coverage, Is.GreaterThan(0.95f),
                "The set has no fully overcast rung.");

            for (int i = 1; i < ladder.Count; i++)
            {
                float gap = ladder[i].Coverage - ladder[i - 1].Coverage;
                Assert.That(gap, Is.GreaterThanOrEqualTo(-1e-4f), "The ladder is not sorted.");
                Assert.That(gap, Is.LessThanOrEqualTo(0.30f),
                    $"{ladder[i - 1].Name} -> {ladder[i].Name} leaves a {gap:0.00} hole in "
                    + "the coverage ladder; the set needs a rung between them.");
            }
        }

        [Test]
        public void DailyCoverage_VariesAcrossWorldDaysAndIsDeterministic()
        {
            float minimum = float.MaxValue;
            float maximum = float.MinValue;
            for (long day = 0; day < 64; day++)
            {
                float offset = SolWeatherManager.EvaluateDailyCoverageOffset(day, 12f, 1427);
                Assert.That(offset, Is.InRange(-1f, 1f));
                Assert.AreEqual(offset,
                    SolWeatherManager.EvaluateDailyCoverageOffset(day, 12f, 1427), 0f,
                    "The same world date produced two different coverage draws.");
                minimum = Mathf.Min(minimum, offset);
                maximum = Mathf.Max(maximum, offset);
            }

            Assert.That(maximum - minimum, Is.GreaterThan(1.2f),
                "The daily coverage draw barely moves, so clear days would still look alike.");
        }

        /// <summary>
        /// The draw crossfades into the next day's, the same way the baked cloud variation
        /// does. Sampling one draw per day instead would step the whole sky at midnight.
        /// </summary>
        [Test]
        public void DailyCoverage_IsContinuousAcrossTheDayBoundary()
        {
            float endOfDay = SolWeatherManager.EvaluateDailyCoverageOffset(10L, 23.999f, 1427);
            float startOfNext = SolWeatherManager.EvaluateDailyCoverageOffset(11L, 0.001f, 1427);
            Assert.AreEqual(endOfDay, startOfNext, 0.01f,
                "Coverage steps at midnight instead of crossfading into the next day.");

            // Exclusive of 24: hour 24 of day 10 is hour 0 of day 11, and sampling it as
            // day 10 wraps the crossfade back to its start. The boundary itself is what the
            // assertion above covers.
            float previous = SolWeatherManager.EvaluateDailyCoverageOffset(10L, 0f, 1427);
            for (float hour = 0.25f; hour < 24f; hour += 0.25f)
            {
                float value = SolWeatherManager.EvaluateDailyCoverageOffset(10L, hour, 1427);
                Assert.That(Mathf.Abs(value - previous), Is.LessThan(0.06f),
                    $"The coverage draw jumped at hour {hour}.");
                previous = value;
            }
        }

        /// <summary>
        /// Unity writes no key for a field a hand-edited asset omits, and deserializes the
        /// missing key as zero rather than as the C# initialiser. Overcast, Rain and Storm
        /// all omitted the advanced shape block, so ticking overrideAdvancedCloudShape on
        /// any of them produced a ten-metre-thick deck at one metre altitude.
        /// </summary>
        [Test]
        public void WeatherProfiles_AuthorEveryFieldExplicitly()
        {
            string[] required =
            {
                "cloudFormation", "cloudiness", "cloudErosion", "overrideAdvancedCloudShape",
                "cloudDensity", "cloudBaseHeight", "cloudThickness",
                "cloudVerticalDevelopment", "cloudAnvilAmount", "cirrusAmount",
                "cloudEdgeSoftness", "cloudBaseSoftness", "cloudCoverageBias",
                "cloudShadowStrength", "cloudCoverageVariance", "rainIntensity", "snowBias",
                "windSpeedMetresPerSecond", "fogBoost", "mistiness", "skyObscuration",
                "visibilityMetres", "lightScattering", "dim", "waveSpeedMultiplier",
                "waterTurbulence", "lightning", "lightningIntensity",
            };

            foreach (string guid in AssetDatabase.FindAssets(
                "t:SolWeatherProfileAsset", new[] { WeatherProfileFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string yaml = File.ReadAllText(path);
                foreach (string field in required)
                {
                    Assert.That(yaml, Does.Match($@"(?m)^\s*{field}:"),
                        $"{path} omits '{field}', which deserializes as zero.");
                }

                SolWeatherProfileAsset profile =
                    AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(path);
                Assert.That(profile.cloudBaseHeight, Is.GreaterThanOrEqualTo(50f),
                    $"{profile.name} has a collapsed cloud base.");
                Assert.That(profile.cloudThickness, Is.GreaterThanOrEqualTo(400f),
                    $"{profile.name} has a collapsed cloud layer.");
                Assert.That(profile.cloudDensity, Is.GreaterThan(0.1f),
                    $"{profile.name} has no cloud density authored.");
            }
        }

        /// <summary>
        /// A profile that does not override its shape renders its formation preset, so the
        /// authored block is dead data unless it matches. Keeping the two in step makes
        /// ticking the override a visible no-op instead of a trap.
        /// </summary>
        [Test]
        public void WeatherProfiles_MatchTheirFormationPresetUnlessTheyOverrideIt()
        {
            foreach (string guid in AssetDatabase.FindAssets(
                "t:SolWeatherProfileAsset", new[] { WeatherProfileFolder }))
            {
                SolWeatherProfileAsset profile =
                    AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(
                        AssetDatabase.GUIDToAssetPath(guid));
                if (profile.overrideAdvancedCloudShape)
                    continue;

                SolCloudState preset = SolWeatherProfileAsset.FormationDefaults(
                    profile.cloudFormation, profile.cloudiness, profile.cloudErosion);
                string why = $"{profile.name} does not override its shape, so its authored "
                    + "block must match the formation preset it actually renders.";
                Assert.AreEqual(preset.Density, profile.cloudDensity, 1e-4f, why);
                Assert.AreEqual(preset.BaseHeight, profile.cloudBaseHeight, 1e-3f, why);
                Assert.AreEqual(preset.Thickness, profile.cloudThickness, 1e-3f, why);
                Assert.AreEqual(preset.VerticalDevelopment,
                    profile.cloudVerticalDevelopment, 1e-4f, why);
                Assert.AreEqual(preset.AnvilAmount, profile.cloudAnvilAmount, 1e-4f, why);
                Assert.AreEqual(preset.CirrusAmount, profile.cirrusAmount, 1e-4f, why);
                Assert.AreEqual(preset.EdgeSoftness, profile.cloudEdgeSoftness, 1e-4f, why);
                Assert.AreEqual(preset.BaseSoftness, profile.cloudBaseSoftness, 1e-4f, why);
                Assert.AreEqual(preset.CoverageBias, profile.cloudCoverageBias, 1e-4f, why);
                Assert.AreEqual(preset.ShadowStrength, profile.cloudShadowStrength, 1e-4f, why);
            }
        }

        /// <summary>
        /// The selection list is duplicated across three scenes and a prefab. Only a test
        /// keeps those four copies in agreement.
        /// </summary>
        [Test]
        public void WeatherSelectionLists_AreIdenticalAcrossEveryContainer()
        {
            string reference = null;
            string referencePath = null;
            foreach (string containerPath in WeatherSelectionAssetPaths)
            {
                string block = ExtractProfilesBlock(containerPath);
                if (reference == null)
                {
                    reference = block;
                    referencePath = containerPath;
                    continue;
                }

                Assert.AreEqual(reference, block,
                    $"{containerPath} and {referencePath} carry different weather "
                    + "selection lists. Run Sol/Weather/Sync Selection Lists.");
            }
        }

        static string ExtractProfilesBlock(string containerPath)
        {
            string[] lines = File.ReadAllText(containerPath)
                .Replace("\r\n", "\n").Split('\n');
            int start = System.Array.IndexOf(lines, "  profiles:");
            Assert.That(start, Is.GreaterThanOrEqualTo(0),
                $"{containerPath} has no weather selection list.");

            var block = new List<string>();
            for (int i = start + 1; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("  - ") && !lines[i].StartsWith("    "))
                    break;
                block.Add(lines[i]);
            }
            Assert.That(block.Count, Is.GreaterThan(0),
                $"{containerPath} has an empty weather selection list.");
            return string.Join("\n", block);
        }


        static readonly string[] ShippedProfileNames =
        {
            "Clear", "Fair", "Fog", "Overcast", "Drizzle",
            "Rain", "Snow", "Storm", "Blizzard",
        };

        [Test]
        public void WeatherAdjacency_IsSymmetricBoundedAndNeverZero()
        {
            foreach (string a in ShippedProfileNames)
            {
                foreach (string b in ShippedProfileNames)
                {
                    float forward = SolWeatherAdjacency.Affinity(LoadProfile(a), LoadProfile(b));
                    float backward = SolWeatherAdjacency.Affinity(LoadProfile(b), LoadProfile(a));
                    Assert.AreEqual(forward, backward, 1e-5f,
                        $"{a} -> {b} is not symmetric with {b} -> {a}.");
                    Assert.That(forward,
                        Is.InRange(SolWeatherAdjacency.MinimumAffinity, 1f),
                        $"{a} -> {b} left the affinity range.");
                }
            }
        }

        /// <summary>
        /// Pins the ordering rather than the values, so Sigma stays a tuning knob. Selection
        /// used to be memoryless, so Clear could hand straight over to Blizzard.
        /// </summary>
        [Test]
        public void WeatherAdjacency_RanksPlausibleSuccessorsAboveImplausibleOnes()
        {
            float Affinity(string a, string b)
                => SolWeatherAdjacency.Affinity(LoadProfile(a), LoadProfile(b));

            Assert.That(Affinity("Clear", "Fair"), Is.GreaterThan(Affinity("Clear", "Overcast")));
            Assert.That(Affinity("Clear", "Overcast"), Is.GreaterThan(Affinity("Clear", "Rain")));
            Assert.That(Affinity("Clear", "Rain"), Is.GreaterThan(Affinity("Clear", "Storm")));
            Assert.That(Affinity("Clear", "Fog"), Is.GreaterThan(Affinity("Clear", "Overcast")));
            Assert.That(Affinity("Overcast", "Drizzle"), Is.GreaterThan(Affinity("Overcast", "Rain")));
            Assert.That(Affinity("Drizzle", "Rain"), Is.GreaterThan(Affinity("Overcast", "Rain")));
            Assert.That(Affinity("Rain", "Storm"), Is.GreaterThan(Affinity("Overcast", "Storm")));

            Assert.That(Affinity("Clear", "Blizzard"),
                Is.EqualTo(SolWeatherAdjacency.MinimumAffinity).Within(1e-6f),
                "Clear must not be able to hand straight over to a blizzard.");
        }

        /// <summary>
        /// Rain and Blizzard are both near-total cover with heavy precipitation and strong
        /// wind; only the phase tells them apart. Without weighting that axis they would
        /// read as neighbours.
        /// </summary>
        [Test]
        public void WeatherAdjacency_KeepsFrozenAndLiquidPrecipitationApart()
        {
            float rainToBlizzard =
                SolWeatherAdjacency.Affinity(LoadProfile("Rain"), LoadProfile("Blizzard"));
            float snowToBlizzard =
                SolWeatherAdjacency.Affinity(LoadProfile("Snow"), LoadProfile("Blizzard"));

            Assert.AreEqual(SolWeatherAdjacency.MinimumAffinity, rainToBlizzard, 1e-6f,
                "Rain must not be a plausible step into a blizzard.");
            Assert.That(snowToBlizzard, Is.GreaterThan(3f * rainToBlizzard),
                "Snow must be a far more plausible step into a blizzard than rain is.");
        }

        /// <summary>
        /// snowBias decides nothing in a dry profile, so it must not cost anything there.
        /// </summary>
        [Test]
        public void WeatherAdjacency_PhaseCostsNothingBetweenDryStates()
        {
            SolWeatherProfileAsset dryA = ScriptableObject.CreateInstance<SolWeatherProfileAsset>();
            SolWeatherProfileAsset dryB = ScriptableObject.CreateInstance<SolWeatherProfileAsset>();
            try
            {
                dryA.cloudiness = 0.2f;
                dryB.cloudiness = 0.3f;
                dryA.rainIntensity = 0f;
                dryB.rainIntensity = 0f;

                dryB.snowBias = 0f;
                float withoutPhase = SolWeatherAdjacency.Distance(dryA, dryB);
                dryB.snowBias = 1f;
                float withPhase = SolWeatherAdjacency.Distance(dryA, dryB);

                Assert.AreEqual(withoutPhase, withPhase, 1e-6f,
                    "Phase separated two dry states, where it decides nothing.");

                dryA.rainIntensity = 0.8f;
                dryB.rainIntensity = 0.8f;
                Assert.That(SolWeatherAdjacency.Distance(dryA, dryB),
                    Is.GreaterThan(withPhase + 0.1f),
                    "Phase must separate two wet states.");
            }
            finally
            {
                ScriptableObject.DestroyImmediate(dryA);
                ScriptableObject.DestroyImmediate(dryB);
            }
        }

        [Test]
        public void WeatherAdjacency_LeavesEveryStateAWayOut()
        {
            foreach (string from in ShippedProfileNames)
            {
                float best = 0f;
                string bestName = null;
                foreach (string to in ShippedProfileNames)
                {
                    if (to == from)
                        continue;
                    float affinity =
                        SolWeatherAdjacency.Affinity(LoadProfile(from), LoadProfile(to));
                    if (affinity > best)
                    {
                        best = affinity;
                        bestName = to;
                    }
                }

                Assert.That(best, Is.GreaterThan(3f * SolWeatherAdjacency.MinimumAffinity),
                    $"{from} has no plausible successor; its best was {bestName} at {best:0.00}.");
            }
        }

        /// <summary>
        /// Adjacency multiplies the seasonal weight, and Snow and Blizzard carry a summer
        /// multiplier of zero. If the product could reach zero for every candidate the
        /// cycle would fall back to a round-robin step, which is the one behaviour the
        /// weighting exists to avoid.
        /// </summary>
        [Test]
        public void WeatherSelection_HasNoDeadEndInAnySeason()
        {
            var weights = new Dictionary<string, float[]>
            {
                // base, spring, summer, autumn, winter -- mirrors the shipped containers.
                { "Clear",    new[] { 4f,   1.05f, 1.45f, 0.85f, 0.65f } },
                { "Overcast", new[] { 3f,   0.95f, 0.70f, 1.25f, 1.35f } },
                { "Rain",     new[] { 2f,   1.35f, 0.75f, 1.05f, 1.25f } },
                { "Storm",    new[] { 1f,   0.75f, 0.55f, 1.35f, 1.15f } },
                { "Snow",     new[] { 2f,   0.35f, 0f,    0.30f, 2.60f } },
                { "Blizzard", new[] { 1f,   0.05f, 0f,    0.05f, 1.40f } },
                { "Fair",     new[] { 3.5f, 1.15f, 1.35f, 0.95f, 0.75f } },
                { "Fog",      new[] { 1.5f, 1.30f, 0.40f, 1.90f, 1.10f } },
                { "Drizzle",  new[] { 2f,   1.20f, 0.70f, 1.35f, 1.10f } },
            };

            for (int season = 0; season < 4; season++)
            {
                foreach (string current in ShippedProfileNames)
                {
                    float total = 0f;
                    foreach (string candidate in ShippedProfileNames)
                    {
                        if (candidate == current)
                            continue;
                        float[] w = weights[candidate];
                        total += w[0] * w[1 + season]
                               * SolWeatherAdjacency.Affinity(
                                   LoadProfile(current), LoadProfile(candidate));
                    }

                    Assert.That(total, Is.GreaterThan(0f),
                        $"Season {(SolSeason)season} dead-ends on {current}.");
                }
            }
        }

        [Test]
        public void WeatherSelection_MultipliesAdjacencyIntoTheSeasonalWeight()
        {
            string source = File.ReadAllText(WeatherManagerSourcePath);
            Assert.That(source, Does.Contain("SolWeatherAdjacency.Affinity(current, GetProfile(index))"),
                "Selection is no longer weighted by successor plausibility.");
            Assert.That(Regex.Matches(source, @"SelectionWeight\(i, current, season\)").Count,
                Is.EqualTo(2),
                "The total pass and the roulette pass must use the identical expression.");
        }

        [Test]
        public void WeatherChannelTiming_IsMonotonicAndCompletesAtMasterOne()
        {
            SolWeatherTransitionTimings timings = new();
            SolWeatherChannelTiming[] channels =
            {
                timings.wind, timings.clouds, timings.fog, timings.water,
                timings.precipitation, timings.lightning, timings.light,
            };

            foreach (SolWeatherChannelTiming channel in channels)
            {
                Assert.AreEqual(0f, channel.Evaluate(0f), 1e-5f,
                    "Every channel must start at zero.");
                Assert.AreEqual(1f, channel.Evaluate(1f), 1e-5f,
                    "Every channel must complete exactly at master one.");

                float previous = -1f;
                for (float master = 0f; master <= 1.0001f; master += 0.02f)
                {
                    float value = channel.Evaluate(master);
                    Assert.That(value, Is.InRange(-1e-5f, 1f + 1e-5f));
                    Assert.That(value, Is.GreaterThanOrEqualTo(previous - 1e-5f),
                        "Channel progress must never move backwards.");
                    previous = value;
                }
            }
        }

        /// <summary>
        /// The donor gated precipitation behind cloud cover with a coroutine WaitUntil.
        /// The same intent, expressed continuously: while cover is building, precipitation
        /// waits; while it is breaking up, precipitation is free to stop immediately.
        /// </summary>
        [Test]
        public void PrecipitationGate_WaitsForBuildingCoverButNotForClearing()
        {
            SolWeatherTransitionTimings timings = new();

            float earlyCloud = timings.clouds.Evaluate(0.2f);
            Assert.That(earlyCloud, Is.GreaterThan(0f),
                "Cloud cover must lead the transition.");

            float buildingGate = SolWeatherTransitionTimings.PrecipitationCoverGate(
                0.1f, 0.95f, earlyCloud);
            Assert.AreEqual(0f, buildingGate, 1e-5f,
                "Precipitation must not start under a sky that has not clouded over.");

            float clearingGate = SolWeatherTransitionTimings.PrecipitationCoverGate(
                0.95f, 0.1f, earlyCloud);
            Assert.AreEqual(1f, clearingGate, 1e-5f,
                "Clearing weather must be free to stop precipitating immediately.");

            float arrivedGate = SolWeatherTransitionTimings.PrecipitationCoverGate(
                0.1f, 0.95f, timings.clouds.Evaluate(1f));
            Assert.AreEqual(1f, arrivedGate, 1e-5f,
                "A completed transition must never hold precipitation back.");
        }

        /// <summary>
        /// An instant weather change must apply every channel fully, whatever the
        /// choreography says, or SetWeather(instant: true) would leave a partial sky.
        /// </summary>
        [Test]
        public void InstantWeatherChange_CompletesEveryChannel()
        {
            SolWeatherTransitionTimings timings = new();
            Assert.AreEqual(1f, timings.precipitation.Evaluate(1f), 1e-5f);
            Assert.AreEqual(1f, SolWeatherTransitionTimings.PrecipitationCoverGate(
                0f, 1f, timings.clouds.Evaluate(1f)), 1e-5f);
        }

        /// <summary>
        /// Edge softness scales the formation's own boundary width rather than replacing
        /// it, so a cumulus stays harder-edged than a stratus deck at the same setting.
        /// </summary>
        [Test]
        public void CloudEdgeSoftness_WidensTheBoundaryMonotonically()
        {
            const float formationWidth = 0.13f;
            float previous = -1f;
            for (float softness = 0f; softness <= 1.0001f; softness += 0.05f)
            {
                float width = Mathf.Max(0.025f,
                    formationWidth * Mathf.Lerp(0.18f, 1.7f, Mathf.Clamp01(softness)));
                Assert.That(width, Is.GreaterThan(previous),
                    "Boundary width must widen monotonically with edge softness.");
                Assert.That(width, Is.InRange(0.025f, formationWidth * 1.7f + 1e-5f));
                previous = width;
            }

            // The range has to be wide enough that the parameter can actually reach a
            // crisp edge. A hard/soft ratio near one is a parameter that does nothing.
            float hardest = formationWidth * Mathf.Lerp(0.18f, 1.7f, 0f);
            float softest = formationWidth * Mathf.Lerp(0.18f, 1.7f, 1f);
            Assert.That(softest / hardest, Is.GreaterThan(6f),
                "Edge softness must span a wide enough range to matter.");
        }

        /// <summary>
        /// Distant banks gain body, but the gain is bounded: an unbounded distance term
        /// would drive the far shell opaque and defeat the atmosphere behind it.
        /// </summary>
        [Test]
        public void CloudDistanceGain_IsBoundedAndMonotonic()
        {
            const float gain = 0.45f;
            float previous = 0f;
            for (float distance = 0f; distance <= 200000f; distance += 2500f)
            {
                float factor = 1f + gain
                    * Mathf.Clamp01((distance - 12000f) * 0.00002f);
                Assert.That(factor, Is.GreaterThanOrEqualTo(previous - 1e-6f));
                Assert.That(factor, Is.InRange(1f, 1f + gain + 1e-5f),
                    "The distance density gain must stay bounded.");
                previous = factor;
            }

            Assert.AreEqual(1f, 1f + gain * Mathf.Clamp01((9000f - 12000f) * 0.00002f), 1e-6f,
                "Near clouds must not be affected by the distance gain.");
        }

        /// <summary>
        /// Splitting the shell segment evenly gave a near-horizon ray kilometre-long steps
        /// and sliced the deck into visible horizontal shells. The growing step must still
        /// cover the whole segment, must start far finer than the even split, and must
        /// never regress to uniform.
        /// </summary>
        [TestCase(32)]
        [TestCase(48)]
        public void CloudRaymarchSteps_GrowAlongTheRayAndStillSpanTheShell(int stepCount)
        {
            const float growth = 10f;
            const float segment = 60000f;

            float stepGrowth = (growth - 1f) / Mathf.Max(1f, stepCount - 1f);
            float baseStep = segment / (stepCount * (1f + (growth - 1f) * 0.5f));

            float total = 0f;
            float previous = 0f;
            for (int i = 0; i < stepCount; i++)
            {
                float step = baseStep * (1f + i * stepGrowth);
                Assert.That(step, Is.GreaterThanOrEqualTo(previous),
                    "Steps must grow, never shrink, along the ray.");
                previous = step;
                total += step;
            }

            Assert.That(total, Is.EqualTo(segment).Within(segment * 0.01f),
                "The growing steps must still span the whole shell segment.");

            float uniformStep = segment / stepCount;
            Assert.That(baseStep, Is.LessThan(uniformStep * 0.25f),
                "The near field must be sampled far more finely than an even split.");
            Assert.That(previous / baseStep, Is.EqualTo(growth).Within(0.01f),
                "The last step must be the intended multiple of the first.");
        }

        /// <summary>
        /// Every step needs independent jitter addressed in cloud-render pixels. Addressing
        /// InterleavedGradientNoise with full-resolution coordinates while rendering at half
        /// resolution selected a regular subset of its lattice and made the pattern visible.
        /// </summary>
        [Test]
        public void CloudRaymarch_StratifiesEveryStepNotJustTheRay()
        {
            string shader = File.ReadAllText(
                "Assets/Earth-Sky-Water/Shaders/SolVolumetricClouds.shader");

            Assert.That(shader, Does.Contain("SolCloudStepJitter("),
                "Each ray-march step must carry an independently hashed offset.");
            Assert.That(shader, Does.Contain("float2 cloudSize = SolCloudBufferSize()"),
                "Jitter must be addressed in the cloud buffer's pixel grid.");
            Assert.That(shader, Does.Not.Contain("InterleavedGradientNoise("),
                "The patterned full-resolution hash must not return.");
            Assert.That(shader, Does.Contain("stepLength * stepJitter"),
                "The per-step offset must be applied to the sample position.");
            Assert.That(shader, Does.Not.Contain(
                "float stepLength = (endDistance - startDistance) / stepCount;"),
                "The even segment split is what banded the deck; it must not return.");

            // The mip has to track a step footprint that now changes along the ray.
            // Match the declaration, not the SolCloudDensity parameter of the same name.
            int lodIndex = shader.IndexOf(
                "float sampleLod = clamp(", System.StringComparison.Ordinal);
            int loopIndex = shader.IndexOf(
                "for (int stepIndex = 0", System.StringComparison.Ordinal);
            Assert.That(lodIndex, Is.GreaterThan(loopIndex),
                "The sample mip must be resolved per step, inside the march loop.");
        }

        /// <summary>
        /// Authored density legitimately exceeds one -- cumulonimbus ships at 1.2 -- and
        /// clamping it flattens exactly the dense cores that give a cloud its definition.
        /// </summary>
        [Test]
        public void CloudDensity_IsNotClampedAwayFromItsAuthoredPeak()
        {
            string shader = File.ReadAllText(
                "Assets/Earth-Sky-Water/Shaders/SolVolumetricClouds.shader");
            Assert.That(shader, Does.Not.Contain("density = saturate(density);\n            breakdown.w"),
                "Clamping post-gain density flattens authored cumulonimbus cores.");

            SolWeatherProfileAsset storm =
                AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(
                    $"{WeatherProfileFolder}/Storm.asset");
            Assert.IsNotNull(storm);
            SolCloudState state = storm.ResolveCloudState();
            Assert.That(state.Density, Is.GreaterThan(1f),
                "The convective formations must keep their above-unity density.");
        }

        /// <summary>
        /// Cloud definition rides in its own uniform because every slot of _SolCloudShape
        /// and _SolCloudSculpting is already spoken for. Keep the shader and the upload
        /// agreeing on that.
        /// </summary>
        [Test]
        public void CloudDefinition_IsUploadedAndConsumed()
        {
            string shader = File.ReadAllText(
                "Assets/Earth-Sky-Water/Shaders/SolVolumetricClouds.shader");
            string feature = File.ReadAllText(
                "Assets/Earth-Sky-Water/Scripts/Management/SolCloudRendererFeature.cs");

            Assert.That(shader, Does.Contain("float4 _SolCloudDefinition;"));
            Assert.That(shader, Does.Contain("float _SolCloudDistanceGain;"));
            Assert.That(feature, Does.Contain("_SolCloudDefinition"));
            Assert.That(feature, Does.Contain("_SolCloudDistanceGain"));

            // Curl must swirl erosion only. Applying it to the base shape makes whole
            // masses drift under temporal reprojection.
            Assert.That(shader, Does.Contain("float2 curlWarp"));
            Assert.That(shader, Does.Contain("detailHorizontal"));
        }

        /// <summary>
        /// A whiteout is bright. Authoring a blizzard the way the donor authored heavy snow
        /// -- near-black fog and heavy darkening -- makes it read as night rather than as
        /// snow, so the blizzard must obscure more than the storm while darkening less.
        /// </summary>
        [Test]
        public void BlizzardProfile_ObscuresBrightlyRatherThanDarkening()
        {
            SolWeatherProfileAsset blizzard =
                AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(
                    $"{WeatherProfileFolder}/Blizzard.asset");
            SolWeatherProfileAsset storm =
                AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(
                    $"{WeatherProfileFolder}/Storm.asset");
            Assert.IsNotNull(blizzard, "Blizzard profile was not found.");
            Assert.IsNotNull(storm, "Storm profile was not found.");

            Assert.That(blizzard.dim, Is.LessThan(storm.dim),
                "A blizzard must darken the scene less than a thunderstorm does.");
            Assert.That(blizzard.skyObscuration, Is.GreaterThan(storm.skyObscuration),
                "A blizzard must obscure the sky more than a thunderstorm does.");
            Assert.That(blizzard.fogBoost, Is.GreaterThan(storm.fogBoost),
                "A blizzard needs the visibility collapse that produces a whiteout.");
            Assert.AreEqual(1f, blizzard.snowBias, 1e-5f,
                "A blizzard must fall as snow regardless of the scene temperature model.");
            Assert.That(blizzard.lightning, Is.False,
                "Thunder snow is a separate condition and is not what this profile is.");
        }

        /// <summary>
        /// fogBoost is capped at 2, which on the shipped atmosphere reaches only about 83%
        /// opacity at the distance cap -- haze, not a whiteout. A stated visibility must
        /// therefore drive a density floor, and that floor must actually hide the scene at
        /// the distance it names.
        /// </summary>
        [TestCase(55f)]
        [TestCase(180f)]
        [TestCase(300f)]
        public void VisibilityDistance_HidesTheSceneWhereItSaysItDoes(float visibility)
        {
            float floor = -Mathf.Log(0.02f) / visibility;
            float transmittanceAtVisibility = Mathf.Exp(-floor * visibility);
            Assert.AreEqual(0.02f, transmittanceAtVisibility, 1e-4f,
                "The density floor must leave 2% transmittance at the stated distance.");

            // The boost-only path the floor exists to replace.
            const float boostedDensity = 0.0012f * (1f + 2f);
            Assert.That(floor, Is.GreaterThan(boostedDensity),
                "A visibility floor that fogBoost could already reach would be pointless.");

            Assert.That(Mathf.Exp(-floor * visibility * 0.5f), Is.GreaterThan(0.02f),
                "Half the stated distance must still be visible.");
        }

        /// <summary>
        /// A visibility distance blends as a density floor rather than as metres. Lerping
        /// metres out of "unused" would sweep through every value denser than the target on
        /// the way there, so a transition into a blizzard would black out and recover.
        /// </summary>
        [Test]
        public void VisibilityFloor_BlendsMonotonicallyOutOfUnused()
        {
            SolWeatherProfileAsset clear = ScriptableObject.CreateInstance<SolWeatherProfileAsset>();
            SolWeatherProfileAsset blizzard = ScriptableObject.CreateInstance<SolWeatherProfileAsset>();
            try
            {
                clear.visibilityMetres = 0f;
                blizzard.visibilityMetres = 55f;

                float from = clear.ResolveFogDensityFloor();
                float to = blizzard.ResolveFogDensityFloor();
                Assert.AreEqual(0f, from, 1e-6f, "Unused visibility must impose no floor.");

                float previous = -1f;
                for (float t = 0f; t <= 1.0001f; t += 0.05f)
                {
                    float blended = Mathf.Lerp(from, to, t);
                    Assert.That(blended, Is.GreaterThanOrEqualTo(previous - 1e-6f),
                        "The floor must rise monotonically into the blizzard.");
                    Assert.That(blended, Is.LessThanOrEqualTo(to + 1e-6f),
                        "The blend must never overshoot the target density.");
                    previous = blended;
                }
            }
            finally
            {
                Object.DestroyImmediate(clear);
                Object.DestroyImmediate(blizzard);
            }
        }

        /// <summary>
        /// The stated visibility must survive the trip from the profile to the atmosphere.
        /// </summary>
        [Test]
        public void VisibilityFloor_ReachesTheAtmosphereController()
        {
            string source = File.ReadAllText(
                "Assets/Earth-Sky-Water/Scripts/Management/SolAtmosphereController.cs");
            Assert.That(source, Does.Contain("weather.FogDensityFloor"),
                "The atmosphere must raise its density floor from the weather state.");
            Assert.That(source, Does.Contain("ResolveMaxOpacity"),
                "A stated visibility must lift the opacity ceiling as well as the floor.");

            SolWeatherProfileAsset blizzard =
                AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(
                    $"{WeatherProfileFolder}/Blizzard.asset");
            Assert.IsNotNull(blizzard);
            Assert.That(blizzard.visibilityMetres, Is.InRange(20f, 120f),
                "A blizzard must state a whiteout-scale visibility distance.");
        }

        /// <summary>
        /// Snow drifts with the wind, so a camera-centred emitter box empties downwind
        /// before a flake can fall through the view -- which is why snow appeared to stream
        /// overhead rather than fall. The emitter must aim downwind, sit upwind, and stretch
        /// along the wind, while lifetime stays capped so the volume cannot outgrow the
        /// particle budget.
        /// </summary>
        [Test]
        public void SnowEmitter_TracksTheWindWithoutOutgrowingItsBudget()
        {
            string source = File.ReadAllText(
                "Assets/Earth-Sky-Water/Scripts/Management/SolRainVfxController.cs");

            Assert.That(source, Does.Contain("Quaternion.LookRotation(wind"),
                "The snow emitter must aim downwind.");
            Assert.That(source, Does.Contain("position.z = -drift * 0.5f"),
                "The emitter must sit upwind by half the drift so its sweep covers the camera.");
            Assert.That(source, Does.Contain("snowMaxDriftRadii"),
                "Lifetime must be bounded by drift, not by fall time alone.");

            // A flake must never outrun the wind that carries it.
            Assert.That(source, Does.Not.Contain("PrecipitationKind.Snow => 1.15f"),
                "Snow carry above one makes flakes outpace the wind.");

            const float radius = 18f;
            const float maxDriftRadii = 2f;
            const float fallLifetime = 8f;
            foreach (float windSpeed in new[] { 0f, 3f, 9f, 22f })
            {
                float driftSpeed = windSpeed * 0.95f;
                float lifetime = driftSpeed > 0.05f
                    ? Mathf.Min(fallLifetime,
                        Mathf.Max(0.5f, radius * maxDriftRadii / driftSpeed))
                    : fallLifetime;
                float drift = driftSpeed * lifetime;

                Assert.That(drift, Is.LessThanOrEqualTo(radius * maxDriftRadii + 1e-3f),
                    $"Drift ran away at {windSpeed} m/s and would thin the emitter out.");
                Assert.That(lifetime, Is.GreaterThan(0f));
            }
        }

        /// <summary>
        /// The blowing layer is a wind phenomenon: snow present is not enough, it needs a
        /// wind strong enough to lift it. Without the threshold every snowfall would read
        /// as a blizzard.
        /// </summary>
        [Test]
        public void BlowingSnow_IsGatedOnWindRatherThanOnSnowAlone()
        {
            string source = File.ReadAllText(
                "Assets/Earth-Sky-Water/Scripts/Management/SolRainVfxController.cs");
            Assert.That(source, Does.Contain("blowingWindThreshold"));
            Assert.That(source, Does.Contain("EffectiveBlowingSnow"));

            const float threshold = 9f;
            const float full = 18f;
            float calm = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(threshold, full, 3f));
            float gale = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(threshold, full, 22f));
            Assert.AreEqual(0f, calm, 1e-5f, "Calm snowfall must not blow.");
            Assert.AreEqual(1f, gale, 1e-5f, "A gale must drive the blowing layer fully.");
        }

    }
}
