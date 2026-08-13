using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Sol.Tests.Editor
{
    public sealed class EnvironmentEditorTests
    {
        [Test]
        public void Calendar_UsesIndependentWorldAndPlayerClocks()
        {
            GameObject root = new("Calendar test");
            root.SetActive(false);

            try
            {
                Component calendar = Reflection.Add(root, "Sol.ToD.Calendar");
                Reflection.Invoke(calendar, "ResetToStart");

                Assert.That(Reflection.Get<long>(calendar, "WorldDayIndex"), Is.Zero);
                Assert.That(Reflection.Get<double>(calendar, "PlayerDaysElapsed"), Is.Zero);

                int startDay = Reflection.Get<int>(calendar, "Day");
                int startMonth = Reflection.Get<int>(calendar, "Month");
                int startYear = Reflection.Get<int>(calendar, "Year");

                Reflection.Invoke(calendar, "RewindDay");
                Assert.That(Reflection.Get<long>(calendar, "WorldDayIndex"), Is.EqualTo(-1));
                Assert.That(Reflection.Get<double>(calendar, "PlayerDaysElapsed"), Is.Zero);

                Reflection.Invoke(calendar, "AdvanceDay");
                Assert.That(Reflection.Get<int>(calendar, "Day"), Is.EqualTo(startDay));
                Assert.That(Reflection.Get<int>(calendar, "Month"), Is.EqualTo(startMonth));
                Assert.That(Reflection.Get<int>(calendar, "Year"), Is.EqualTo(startYear));
                Assert.That(Reflection.Get<long>(calendar, "WorldDayIndex"), Is.Zero);

                Reflection.Invoke(calendar, "AdvancePlayerTime", new[] { typeof(double) }, 0.375d);
                Reflection.Invoke(calendar, "RewindDay");
                Assert.That(Reflection.Get<double>(calendar, "PlayerDaysElapsed"), Is.EqualTo(0.375d).Within(1e-9));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Calendar_SetDateComputesSignedWorldOffset()
        {
            GameObject root = new("Calendar date test");
            root.SetActive(false);

            try
            {
                Component calendar = Reflection.Add(root, "Sol.ToD.Calendar");
                Reflection.Invoke(calendar, "ResetToStart");
                int year = Reflection.Get<int>(calendar, "Year");

                Reflection.Invoke(calendar, "SetDate", new[] { typeof(int), typeof(int), typeof(int) }, 1, 1, year - 1);
                int daysPerYear = Reflection.Get<int>(calendar, "DaysPerYear");
                Assert.That(Reflection.Get<long>(calendar, "WorldDayIndex"), Is.EqualTo(-daysPerYear));
                Assert.That(Reflection.Get<double>(calendar, "PlayerDaysElapsed"), Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Calendar_MapsFourSeasonsAndKeepsProgressBounded()
        {
            GameObject root = new("Four-season calendar test");
            root.SetActive(false);

            try
            {
                Component calendar = Reflection.Add(root, "Sol.ToD.Calendar");
                Reflection.Invoke(calendar, "ResetToStart");
                int year = Reflection.Get<int>(calendar, "Year");

                foreach ((int month, string season) in new[]
                {
                    (1, "Spring"), (4, "Summer"), (7, "Autumn"), (10, "Winter")
                })
                {
                    Reflection.Invoke(calendar, "SetDate",
                        new[] { typeof(int), typeof(int), typeof(int) }, 1, month, year);
                    Assert.That(Reflection.Get<object>(calendar, "CurrentSeason").ToString(),
                        Is.EqualTo(season));
                    Assert.That(Reflection.Get<float>(calendar, "SeasonProgress"), Is.InRange(0f, 1f));
                    Assert.That(Reflection.Get<float>(calendar, "YearProgress"), Is.InRange(0f, 1f));
                }

                Reflection.Invoke(calendar, "SetDate",
                    new[] { typeof(int), typeof(int), typeof(int) }, 1, 10, year);
                Reflection.Invoke(calendar, "RewindDay");
                Assert.That(Reflection.Get<object>(calendar, "CurrentSeason").ToString(),
                    Is.EqualTo("Autumn"));
                Reflection.Invoke(calendar, "AdvanceDay");
                Assert.That(Reflection.Get<object>(calendar, "CurrentSeason").ToString(),
                    Is.EqualTo("Winter"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TimeOfDay_SeasonalDayLengthPeaksInSummerAndIsShortestInWinter()
        {
            GameObject root = new("Seasonal day length test");
            root.SetActive(false);

            try
            {
                Component time = Reflection.Add(root, "Sol.ToD.TimeOfDay");
                Component calendar = root.GetComponent(Reflection.FindType("Sol.ToD.Calendar"))
                    ?? Reflection.Add(root, "Sol.ToD.Calendar");
                Reflection.Invoke(calendar, "ResetToStart");
                Reflection.Set(time, "enableSeasons", true);
                Reflection.Set(time, "dayRatio", 0.5f);
                Reflection.Set(time, "seasonalDayVariation", 0.1f);
                int year = Reflection.Get<int>(calendar, "Year");

                float[] ratios = new float[4];
                int[] months = { 1, 4, 7, 10 };
                for (int i = 0; i < months.Length; i++)
                {
                    Reflection.Invoke(calendar, "SetDate",
                        new[] { typeof(int), typeof(int), typeof(int) }, 1, months[i], year);
                    ratios[i] = (float)Reflection.Invoke(time, "GetEffectiveDayRatio");
                }

                Assert.That(ratios[0], Is.EqualTo(0.5f).Within(0.005f));
                Assert.That(ratios[1], Is.GreaterThan(ratios[0]));
                Assert.That(ratios[2], Is.EqualTo(0.5f).Within(0.005f));
                Assert.That(ratios[3], Is.LessThan(ratios[2]));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DemoTimeOfDay_UsesLightDayAndNightFogBaselines()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/Sols System Manager.prefab");
            Assert.That(prefab, Is.Not.Null);
            Component time = prefab.GetComponent(Reflection.FindType("Sol.ToD.TimeOfDay"));
            Assert.That(time, Is.Not.Null);
            Assert.That(Reflection.Get<float>(time, "fogDayDensity"), Is.EqualTo(0.0012f));
            Assert.That(Reflection.Get<float>(time, "fogNightDensity"), Is.EqualTo(0.0024f));
        }

        [Test]
        public void AstronomyAndAurora_UseWorldDayIndex()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string sourcePath = Path.Combine(projectRoot, "Assets", "Sky-and-Water", "TimeOfDay", "TimeofDay.cs");
            string source = File.ReadAllText(sourcePath);

            StringAssert.Contains("Calendar.WorldDayIndex", source);
            StringAssert.Contains("(float)WorldDayIndex * 12.9898f", source);
            StringAssert.DoesNotContain("Calendar.TotalDaysElapsed", source);
        }

        [Test]
        public void WaterVolume_MaterialRefreshReadsWaveParametersImmediately()
        {
            Shader shader = Shader.Find("Sol/Water");
            Assert.That(shader, Is.Not.Null, "Sol/Water shader was not imported.");

            Material material = new(shader);
            material.SetFloat("_WaveAmplitude", 0.73f);
            material.SetFloat("_WaveFrequency", 2.4f);
            GameObject root = new("Water volume test");
            root.SetActive(false);

            try
            {
                Component volume = Reflection.Add(root, "WaterVolume");
                Reflection.Invoke(volume, "SetWaterMaterial", new[] { typeof(Material) }, material);

                Assert.That(Reflection.Get<float>(volume, "waveAmplitude"), Is.EqualTo(0.73f).Within(0.0001f));
                Assert.That(Reflection.Get<float>(volume, "waveFrequency"), Is.EqualTo(2.4f).Within(0.0001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void Coordinator_RestoresRenderSettingsAndKnownShaderGlobals()
        {
            Color originalFog = RenderSettings.fogColor;
            float originalRain = Shader.GetGlobalFloat("_Sol_RainIntensity");
            Vector4 originalWaterDynamics = Shader.GetGlobalVector("_Sol_WaterDynamics");
            Color authoredFog = new(0.17f, 0.29f, 0.41f, 1f);
            const float authoredRain = 0.23f;
            Vector4 authoredWaterDynamics = new(0.19f, 0.73f, 0.42f, 0.12f);
            GameObject root = new("Coordinator test");
            root.SetActive(false);

            try
            {
                RenderSettings.fogColor = authoredFog;
                Shader.SetGlobalFloat("_Sol_RainIntensity", authoredRain);
                Shader.SetGlobalVector("_Sol_WaterDynamics", authoredWaterDynamics);
                Component coordinator = Reflection.Add(root, "SolEnvironmentCoordinator");

                Reflection.Invoke(coordinator, "Register", new[] { typeof(UnityEngine.Object) }, root);
                RenderSettings.fogColor = Color.magenta;
                Shader.SetGlobalFloat("_Sol_RainIntensity", 0.97f);
                Shader.SetGlobalVector("_Sol_WaterDynamics", Vector4.one);
                Reflection.Invoke(coordinator, "Unregister", new[] { typeof(UnityEngine.Object) }, root);

                Assert.That(RenderSettings.fogColor, Is.EqualTo(authoredFog));
                Assert.That(Shader.GetGlobalFloat("_Sol_RainIntensity"), Is.EqualTo(authoredRain).Within(0.0001f));
                Assert.That(Shader.GetGlobalVector("_Sol_WaterDynamics"), Is.EqualTo(authoredWaterDynamics));
            }
            finally
            {
                RenderSettings.fogColor = originalFog;
                Shader.SetGlobalFloat("_Sol_RainIntensity", originalRain);
                Shader.SetGlobalVector("_Sol_WaterDynamics", originalWaterDynamics);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Coordinator_DoesNotRestoreWhileANewerAuthorityRemainsRegistered()
        {
            Color originalFog = RenderSettings.fogColor;
            Color authoredFog = new(0.11f, 0.22f, 0.33f, 1f);
            GameObject root = new("Coordinator authority test");
            GameObject first = new("First authority");
            GameObject newer = new("Newer authority");
            root.SetActive(false);

            try
            {
                RenderSettings.fogColor = authoredFog;
                Component coordinator = Reflection.Add(root, "SolEnvironmentCoordinator");
                Reflection.Invoke(coordinator, "Register", new[] { typeof(UnityEngine.Object) }, first);
                Reflection.Invoke(coordinator, "Register", new[] { typeof(UnityEngine.Object) }, newer);
                RenderSettings.fogColor = Color.yellow;

                Reflection.Invoke(coordinator, "Unregister", new[] { typeof(UnityEngine.Object) }, first);
                Assert.That(RenderSettings.fogColor, Is.EqualTo(Color.yellow));

                Reflection.Invoke(coordinator, "Unregister", new[] { typeof(UnityEngine.Object) }, newer);
                Assert.That(RenderSettings.fogColor, Is.EqualTo(authoredFog));
            }
            finally
            {
                RenderSettings.fogColor = originalFog;
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(newer);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TimeOfDay_DoesNotReplaceSerializedSkyboxInEditMode()
        {
            Shader shader = Shader.Find("Sol/Skybox");
            Assert.That(shader, Is.Not.Null);

            Material previousSkybox = RenderSettings.skybox;
            Material authored = new(shader);
            GameObject root = new("Edit-mode skybox ownership test");
            root.SetActive(false);

            try
            {
                RenderSettings.skybox = authored;
                Reflection.Add(root, "Sol.ToD.TimeOfDay");
                root.SetActive(true);

                Assert.That(RenderSettings.skybox, Is.SameAs(authored));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                RenderSettings.skybox = previousSkybox;
                UnityEngine.Object.DestroyImmediate(authored);
            }
        }

        [Test]
        public void Coordinator_UsesRuntimeSkyboxCloneWithoutMutatingAuthoredMaterial()
        {
            Shader shader = Shader.Find("Skybox/Procedural") ?? Shader.Find("Universal Render Pipeline/Lit");
            Assert.That(shader, Is.Not.Null);

            Material previousSkybox = RenderSettings.skybox;
            Material authored = new(shader);
            int propertyId = authored.HasProperty("_Exposure")
                ? Shader.PropertyToID("_Exposure")
                : Shader.PropertyToID("_Smoothness");
            authored.SetFloat(propertyId, 0.42f);
            GameObject root = new("Skybox clone test");
            root.SetActive(false);

            try
            {
                RenderSettings.skybox = authored;
                Component coordinator = Reflection.Add(root, "SolEnvironmentCoordinator");
                Material runtime = (Material)Reflection.Invoke(
                    coordinator, "AcquireSkyboxMaterial", new[] { typeof(UnityEngine.Object) }, root);

                Assert.That(runtime, Is.Not.SameAs(authored));
                runtime.SetFloat(propertyId, 1.17f);
                Assert.That(authored.GetFloat(propertyId), Is.EqualTo(0.42f).Within(0.0001f));

                Reflection.Invoke(coordinator, "ReleaseSkybox", new[] { typeof(UnityEngine.Object) }, root);
                Reflection.Invoke(coordinator, "Unregister", new[] { typeof(UnityEngine.Object) }, root);
                Assert.That(RenderSettings.skybox, Is.SameAs(authored));
            }
            finally
            {
                RenderSettings.skybox = previousSkybox;
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(authored);
            }
        }

        [Test]
        public void RainControls_ShaderAndManagerDefaultsStayInSync()
        {
            float originalRoughness = Shader.GetGlobalFloat("_Sol_RainRoughnessBoost");
            float originalNormal = Shader.GetGlobalFloat("_Sol_RainNormalBoost");
            float originalReflection = Shader.GetGlobalFloat("_Sol_RainReflectionDampen");
            GameObject root = new("Water defaults test");
            root.SetActive(false);

            try
            {
                Component manager = Reflection.Add(root, "SolWaterManager");
                Assert.That(Reflection.Get<float>(manager, "rainRoughnessBoost"), Is.EqualTo(0.15f));
                Assert.That(Reflection.Get<float>(manager, "rainNormalBoost"), Is.EqualTo(0.8f));
                Assert.That(Reflection.Get<float>(manager, "rainReflectionDampen"), Is.EqualTo(0.3f));

                Reflection.Invoke(manager, "PushWeather");
                Assert.That(Shader.GetGlobalFloat("_Sol_RainRoughnessBoost"), Is.EqualTo(0.15f).Within(0.0001f));
                Assert.That(Shader.GetGlobalFloat("_Sol_RainNormalBoost"), Is.EqualTo(0.8f).Within(0.0001f));
                Assert.That(Shader.GetGlobalFloat("_Sol_RainReflectionDampen"), Is.EqualTo(0.3f).Within(0.0001f));

                string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
                string shaderPath = Path.Combine(projectRoot, "Assets", "Sky-and-Water", "Water", "Shaders", "Sol.Water.shader");
                string source = File.ReadAllText(shaderPath);
                StringAssert.Contains("_Sol_RainRoughnessBoost", source);
                StringAssert.Contains("_Sol_RainNormalBoost", source);
                StringAssert.Contains("_Sol_RainReflectionDampen", source);
            }
            finally
            {
                Shader.SetGlobalFloat("_Sol_RainRoughnessBoost", originalRoughness);
                Shader.SetGlobalFloat("_Sol_RainNormalBoost", originalNormal);
                Shader.SetGlobalFloat("_Sol_RainReflectionDampen", originalReflection);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TerrainWetnessShader_IsImportedAndAssignedToDemoTerrain()
        {
            Shader shader = Shader.Find("Sol/Terrain/Lit Wetness");
            Assert.That(shader, Is.Not.Null, "Terrain wetness shader was not imported.");
            Shader addPassShader = Shader.Find("Hidden/Sol/Terrain/Lit Wetness Add Pass");
            Assert.That(addPassShader, Is.Not.Null,
                "Terrain wetness add-pass shader was not imported.");
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False,
                "Terrain wetness shader contains compilation errors.");
            Assert.That(ShaderUtil.ShaderHasError(addPassShader), Is.False,
                "Terrain wetness add-pass shader contains compilation errors.");

            Material material = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/Sky-and-Water/Terrain/M_TerrainWetness.mat");
            Assert.That(material, Is.Not.Null, "Terrain wetness material was not imported.");
            Assert.That(material.shader, Is.SameAs(shader));

            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string shaderSource = File.ReadAllText(Path.Combine(
                projectRoot, "Assets", "Sky-and-Water", "Terrain", "Sol.TerrainLitWet.shader"));
            StringAssert.Contains("TerrainCompatible", shaderSource);
            StringAssert.Contains("SolApplyTerrainWetness", shaderSource);
            StringAssert.Contains("SolTerrainWetness.hlsl", shaderSource);
            StringAssert.Contains("Hidden/Sol/Terrain/Lit Wetness Add Pass", shaderSource);
            string addPassSource = File.ReadAllText(Path.Combine(
                projectRoot, "Assets", "Sky-and-Water", "Terrain", "Sol.TerrainLitWetAddPass.shader"));
            StringAssert.Contains("TERRAIN_SPLAT_ADDPASS", addPassSource);
            StringAssert.Contains("SolApplyTerrainWetness", addPassSource);
            string wetnessSource = File.ReadAllText(Path.Combine(
                projectRoot, "Assets", "Sky-and-Water", "Terrain", "SolTerrainWetness.hlsl"));
            StringAssert.Contains("_Sol_TerrainSandMask", wetnessSource);
            StringAssert.Contains("_Sol_TerrainWetSmoothness", wetnessSource);
            StringAssert.Contains("wetSand", wetnessSource);
            StringAssert.DoesNotContain("max(smoothness, 0.88h)", wetnessSource);

            string[] terrainLayerGuids = AssetDatabase.FindAssets(
                "t:TerrainLayer", new[] { "Assets/Sky-and-Water/Resources/Terrain" });
            Assert.That(terrainLayerGuids, Has.Length.EqualTo(6));
            foreach (string guid in terrainLayerGuids)
            {
                string layerPath = AssetDatabase.GUIDToAssetPath(guid);
                TerrainLayer layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(layerPath);
                Assert.That(layer.maskMapTexture, Is.Not.Null, $"{layer.name} must retain its packed mask.");
                Assert.That(layer.maskMapRemapMin.w, Is.GreaterThan(layer.maskMapRemapMax.w),
                    $"{layer.name} mask A stores roughness and must be remapped to inverse smoothness.");
            }

            TerrainData demoTerrain = AssetDatabase.LoadAssetAtPath<TerrainData>(
                "Assets/Scenes/SolsWeather_Demo/DemoTerrain.asset");
            Assert.That(demoTerrain, Is.Not.Null);
            int sandLayerIndex = Array.FindIndex(demoTerrain.terrainLayers,
                layer => layer != null && layer.name == "TerrainLayer_Sand");
            Assert.That(sandLayerIndex, Is.GreaterThanOrEqualTo(0));

            GameObject systemPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefabs/Sols System Manager.prefab");
            Component waterManager = systemPrefab.GetComponent(Reflection.FindType("SolWaterManager"));
            float wetSmoothness = Reflection.Get<float>(waterManager, "terrainWetSmoothness");
            Assert.That(wetSmoothness, Is.InRange(0.4f, 0.55f),
                "Demo wet terrain should retain a damp sheen without mirror-like sparkle.");
        }

        [Test]
        public void WeatherProfiles_HaveDistinctOrderedTuningAndPrefabMatchesDefaults()
        {
            GameObject root = new("Weather tuning defaults test");
            root.SetActive(false);

            try
            {
                Component defaults = Reflection.Add(root, "SolWeatherManager");
                Array defaultProfiles = Reflection.Get<Array>(defaults, "profiles");

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/Prefabs/Sols System Manager.prefab");
                Assert.That(prefab, Is.Not.Null);
                Component prefabManager = prefab.GetComponent(Reflection.FindType("SolWeatherManager"));
                Assert.That(prefabManager, Is.Not.Null);
                Array prefabProfiles = Reflection.Get<Array>(prefabManager, "profiles");

                AssertWeatherTuning(defaultProfiles);
                AssertWeatherTuning(prefabProfiles);

                for (int i = 0; i < defaultProfiles.Length; i++)
                {
                    object expected = defaultProfiles.GetValue(i);
                    object serialized = prefabProfiles.GetValue(i);
                    foreach (string field in new[]
                    {
                        "name", "cloudiness", "cloudErosion", "rainIntensity",
                        "windStrength", "fogBoost", "mistiness", "skyObscuration",
                        "lightScattering", "dim", "waveSpeedMultiplier", "waterTurbulence",
                        "lightning", "lightningIntensity", "weight",
                        "springWeightMultiplier", "summerWeightMultiplier",
                        "autumnWeightMultiplier", "winterWeightMultiplier"
                    })
                    {
                        object expectedValue = expected.GetType().GetField(field)!.GetValue(expected);
                        object serializedValue = serialized.GetType().GetField(field)!.GetValue(serialized);
                        Assert.That(serializedValue, Is.EqualTo(expectedValue),
                            $"Demo prefab {field} drifted for profile index {i}.");
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static void AssertWeatherTuning(Array profiles)
        {
            Assert.That(profiles, Has.Length.EqualTo(4));
            object clear = profiles.GetValue(0);
            object overcast = profiles.GetValue(1);
            object rain = profiles.GetValue(2);
            object storm = profiles.GetValue(3);

            Assert.That(Reflection.Get<string>(clear, "name"), Is.EqualTo("Clear"));
            Assert.That(Reflection.Get<string>(overcast, "name"), Is.EqualTo("Overcast"));
            Assert.That(Reflection.Get<string>(rain, "name"), Is.EqualTo("Rain"));
            Assert.That(Reflection.Get<string>(storm, "name"), Is.EqualTo("Storm"));

            Assert.That(Reflection.Get<float>(clear, "rainIntensity"), Is.Zero);
            Assert.That(Reflection.Get<float>(clear, "fogBoost"), Is.Zero);
            Assert.That(Reflection.Get<float>(clear, "skyObscuration"), Is.Zero);
            Assert.That(Reflection.Get<float>(clear, "dim"), Is.Zero);
            Assert.That(Reflection.Get<float>(clear, "lightningIntensity"), Is.Zero);
            Assert.That(Reflection.Get<float>(clear, "waveSpeedMultiplier"), Is.LessThan(1f));

            Assert.That(Reflection.Get<float>(clear, "cloudiness"),
                Is.LessThan(Reflection.Get<float>(overcast, "cloudiness")));
            Assert.That(Reflection.Get<float>(overcast, "cloudiness"),
                Is.LessThan(Reflection.Get<float>(rain, "cloudiness")));
            Assert.That(Reflection.Get<float>(rain, "cloudiness"),
                Is.LessThan(Reflection.Get<float>(storm, "cloudiness")));

            foreach (string field in new[]
            {
                "windStrength", "fogBoost", "mistiness", "skyObscuration", "dim",
                "waveSpeedMultiplier", "waterTurbulence"
            })
            {
                Assert.That(Reflection.Get<float>(clear, field),
                    Is.LessThan(Reflection.Get<float>(overcast, field)), field);
                Assert.That(Reflection.Get<float>(overcast, field),
                    Is.LessThan(Reflection.Get<float>(rain, field)), field);
                Assert.That(Reflection.Get<float>(rain, field),
                    Is.LessThan(Reflection.Get<float>(storm, field)), field);
            }

            Assert.That(Reflection.Get<float>(clear, "lightScattering"),
                Is.GreaterThan(Reflection.Get<float>(overcast, "lightScattering")));
            Assert.That(Reflection.Get<float>(overcast, "lightScattering"),
                Is.GreaterThan(Reflection.Get<float>(rain, "lightScattering")));
            Assert.That(Reflection.Get<float>(rain, "lightScattering"),
                Is.GreaterThan(Reflection.Get<float>(storm, "lightScattering")));

            Assert.That(Reflection.Get<float>(overcast, "rainIntensity"), Is.Zero);
            Assert.That(Reflection.Get<float>(overcast, "fogBoost"), Is.LessThan(0.05f));
            Assert.That(Reflection.Get<float>(rain, "mistiness"), Is.GreaterThan(0.6f));
            Assert.That(Reflection.Get<float>(storm, "waterTurbulence"), Is.EqualTo(1f));
            Assert.That(Reflection.Get<bool>(storm, "lightning"), Is.True);
            Assert.That(Reflection.Get<float>(storm, "lightningIntensity"),
                Is.InRange(0.2f, 0.35f));
            Assert.That(Reflection.Get<float>(storm, "skyObscuration"), Is.LessThan(0.65f));
        }

        [Test]
        public void DailyClimate_IsDeterministicLowBiasedAndWinterExceedsSummer()
        {
            GameObject root = new("Daily climate test");
            root.SetActive(false);

            try
            {
                Component weather = Reflection.Add(root, "SolWeatherManager");
                Type seasonType = Reflection.FindType("Sol.ToD.SolSeason");
                Type[] signature = { typeof(long), seasonType };
                object summer = Enum.Parse(seasonType, "Summer");
                object winter = Enum.Parse(seasonType, "Winter");
                float summerTotal = 0f;
                float winterTotal = 0f;

                for (long day = -64; day < 64; day++)
                {
                    float summerValue = (float)Reflection.Invoke(weather,
                        "EvaluateDailyFogTarget", signature, day, summer);
                    float repeated = (float)Reflection.Invoke(weather,
                        "EvaluateDailyFogTarget", signature, day, summer);
                    float winterValue = (float)Reflection.Invoke(weather,
                        "EvaluateDailyFogTarget", signature, day, winter);
                    Assert.That(repeated, Is.EqualTo(summerValue));
                    summerTotal += summerValue;
                    winterTotal += winterValue;
                }

                Assert.That(winterTotal / 128f, Is.GreaterThan(summerTotal / 128f));
                Assert.That(summerTotal / 128f, Is.LessThan(0.06f),
                    "Squared climate hashing should bias samples toward each range's low end.");

                float dawn = (float)Reflection.InvokeStatic("SolWeatherManager",
                    "EvaluateFogDiurnalFactor",
                    new[] { typeof(float), typeof(float), typeof(float) }, 6f, 6f, 0.25f);
                float noon = (float)Reflection.InvokeStatic("SolWeatherManager",
                    "EvaluateFogDiurnalFactor",
                    new[] { typeof(float), typeof(float), typeof(float) }, 12f, 6f, 1f);
                Assert.That(dawn, Is.GreaterThan(noon));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SeasonalWeatherWeightsAffectAutomaticSelectionInputsOnly()
        {
            GameObject root = new("Seasonal weather weights test");
            root.SetActive(false);

            try
            {
                Component weather = Reflection.Add(root, "SolWeatherManager");
                Array profiles = Reflection.Get<Array>(weather, "profiles");
                Type profileType = profiles.GetValue(0).GetType();
                Type seasonType = Reflection.FindType("Sol.ToD.SolSeason");
                Type[] signature = { profileType, seasonType };
                object summer = Enum.Parse(seasonType, "Summer");
                object autumn = Enum.Parse(seasonType, "Autumn");
                object winter = Enum.Parse(seasonType, "Winter");

                float clearSummer = (float)Reflection.InvokeStatic("SolWeatherManager",
                    "GetEffectiveWeight", signature, profiles.GetValue(0), summer);
                float clearWinter = (float)Reflection.InvokeStatic("SolWeatherManager",
                    "GetEffectiveWeight", signature, profiles.GetValue(0), winter);
                float stormAutumn = (float)Reflection.InvokeStatic("SolWeatherManager",
                    "GetEffectiveWeight", signature, profiles.GetValue(3), autumn);
                float stormSummer = (float)Reflection.InvokeStatic("SolWeatherManager",
                    "GetEffectiveWeight", signature, profiles.GetValue(3), summer);
                Assert.That(clearSummer, Is.GreaterThan(clearWinter));
                Assert.That(stormAutumn, Is.GreaterThan(stormSummer));

                Reflection.Invoke(weather, "SetWeather", new[] { typeof(int), typeof(bool) }, 3, true);
                Assert.That(Reflection.Get<object>(weather, "TargetProfile"), Is.SameAs(profiles.GetValue(3)),
                    "Direct profile selection must not be remapped by season.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TimeOfDay_InspectorScrubRefreshesEditPreview()
        {
            Shader shader = Shader.Find("Sol/Skybox");
            Assert.That(shader, Is.Not.Null);

            Material previousSkybox = RenderSettings.skybox;
            Material authored = new(shader);
            GameObject root = new("Edit-mode time scrub test");
            root.SetActive(false);

            try
            {
                RenderSettings.skybox = authored;
                Component time = Reflection.Add(root, "Sol.ToD.TimeOfDay");
                Reflection.Set(time, "controlAmbient", false);
                Reflection.Set(time, "controlFog", false);
                Reflection.Set(time, "timeOfDay", 0.25f);
                root.SetActive(true);

                Reflection.Invoke(time, "OnValidate");
                Vector3 morningDirection = Reflection.Get<Vector3>(time, "SunDirection");
                Vector4 morningSkyDirection = authored.GetVector("_SunDirection");

                Reflection.Set(time, "timeOfDay", 0.75f);
                Reflection.Invoke(time, "OnValidate");
                Vector3 eveningDirection = Reflection.Get<Vector3>(time, "SunDirection");
                Vector4 eveningSkyDirection = authored.GetVector("_SunDirection");

                Assert.That(Vector3.Distance(morningDirection, eveningDirection), Is.GreaterThan(0.1f));
                Assert.That(Vector4.Distance(morningSkyDirection, eveningSkyDirection), Is.GreaterThan(0.1f));
            }
            finally
            {
                RenderSettings.skybox = previousSkybox;
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(authored);
            }
        }

        [Test]
        public void LunarTideFactor_PeaksAtNewAndFullMoon()
        {
            Type[] signature = { typeof(float) };
            float newMoon = (float)Reflection.InvokeStatic(
                "Sol.ToD.TimeOfDay", "EvaluateLunarTideFactor", signature, 0f);
            float firstQuarter = (float)Reflection.InvokeStatic(
                "Sol.ToD.TimeOfDay", "EvaluateLunarTideFactor", signature, 0.25f);
            float fullMoon = (float)Reflection.InvokeStatic(
                "Sol.ToD.TimeOfDay", "EvaluateLunarTideFactor", signature, 0.5f);
            float lastQuarter = (float)Reflection.InvokeStatic(
                "Sol.ToD.TimeOfDay", "EvaluateLunarTideFactor", signature, 0.75f);

            Assert.That(newMoon, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(fullMoon, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(firstQuarter, Is.Zero.Within(0.0001f));
            Assert.That(lastQuarter, Is.Zero.Within(0.0001f));
        }

        [Test]
        public void CanonicalClock_CombinesUnityDeltaSolScaleAndPauseOnce()
        {
            GameObject root = new("Canonical clock test");
            root.SetActive(false);

            try
            {
                Component time = Reflection.Add(root, "Sol.ToD.TimeOfDay");
                Reflection.Set(time, "cycleDurationMinutes", 10f);

                Reflection.Invoke(time, "SetTimeScale", new[] { typeof(float) }, 1f);
                Assert.That((float)Reflection.Invoke(time, "GetWorldDeltaSeconds", new[] { typeof(float) }, 0.5f), Is.EqualTo(0.5f));
                Assert.That((float)Reflection.Invoke(time, "GetPresentationDeltaSeconds", new[] { typeof(float) }, 0.5f), Is.EqualTo(0.5f));

                Reflection.Invoke(time, "SetTimeScale", new[] { typeof(float) }, 10f);
                Assert.That((float)Reflection.Invoke(time, "GetWorldDeltaSeconds", new[] { typeof(float) }, 0.5f), Is.EqualTo(5f));
                Assert.That((float)Reflection.Invoke(time, "GetPresentationDeltaSeconds", new[] { typeof(float) }, 0.5f), Is.EqualTo(0.5f));

                Reflection.Invoke(time, "SetTimeScale", new[] { typeof(float) }, 100f);
                float worldSeconds = (float)Reflection.Invoke(time, "GetWorldDeltaSeconds", new[] { typeof(float) }, 0.5f);
                Assert.That(worldSeconds, Is.EqualTo(50f));
                Assert.That((double)Reflection.Invoke(time, "GetWorldDeltaHours", new[] { typeof(float) }, worldSeconds), Is.EqualTo(2d).Within(0.000001d));

                Reflection.Invoke(time, "SetPaused", new[] { typeof(bool) }, true);
                Assert.That((float)Reflection.Invoke(time, "GetWorldDeltaSeconds", new[] { typeof(float) }, 0.5f), Is.Zero);
                Assert.That((float)Reflection.Invoke(time, "GetPresentationDeltaSeconds", new[] { typeof(float) }, 0.5f), Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void EnvironmentShaders_UseSharedWorldClockAndAtmosphereContract()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string sky = File.ReadAllText(Path.Combine(projectRoot, "Assets", "Sky-and-Water", "TimeOfDay", "CelestialBodies", "Sol_Skybox.shader"));
            string water = File.ReadAllText(Path.Combine(projectRoot, "Assets", "Sky-and-Water", "Water", "Shaders", "Sol.Water.shader"));
            string feature = File.ReadAllText(Path.Combine(projectRoot, "Assets", "Sky-and-Water", "Management", "SolAtmosphereRendererFeature.cs"));
            string renderer = File.ReadAllText(Path.Combine(projectRoot, "Assets", "Settings", "Sol_Renderer.asset"));

            StringAssert.Contains("_CloudTime", sky);
            StringAssert.DoesNotContain("_Time.y", sky);
            StringAssert.Contains("_SOL_CLOUD_LOW", sky);
            StringAssert.Contains("_SOL_CLOUD_MEDIUM", sky);
            StringAssert.Contains("_SOL_CLOUD_HIGH", sky);
            StringAssert.Contains("if (_SolAtmosphereActive > 0.5)", water);
            StringAssert.Contains("SolApplyAtmosphere", water);
            StringAssert.DoesNotContain("_Time.y", water,
                "All animated water detail must follow the canonical Sol wave clock.");
            StringAssert.Contains("BeforeRenderingTransparents", feature);
            StringAssert.Contains("GetTextureDesc(activeColor)", feature);
            StringAssert.Contains("SolAtmosphereRendererFeature", renderer);
            StringAssert.Contains("ScreenSpaceAmbientOcclusion", renderer);
            StringAssert.Contains("UnderwaterRendererFeature", renderer);
        }

        [Test]
        public void AtmosphereQualityOverride_DoesNotMutateAuthoredProfile()
        {
            Type profileType = Reflection.FindType("SolAtmosphereProfile");
            Type qualityType = Reflection.FindType("SolAtmosphereQuality");
            ScriptableObject profile = ScriptableObject.CreateInstance(profileType);
            GameObject root = new("Atmosphere quality test");
            root.SetActive(false);

            try
            {
                object medium = Enum.ToObject(qualityType, 1);
                object high = Enum.ToObject(qualityType, 2);
                profileType.GetField("quality")!.SetValue(profile, medium);

                Component controller = Reflection.Add(root, "SolAtmosphereController");
                Reflection.Set(controller, "profile", profile);
                Reflection.Invoke(controller, "SetQuality", new[] { qualityType }, high);

                Assert.That(profileType.GetField("quality")!.GetValue(profile), Is.EqualTo(medium));
                Assert.That(Reflection.Get<object>(controller, "Quality"), Is.EqualTo(high));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void AtmosphereModel_RemainsFiniteAcrossEdgeCases()
        {
            float[] heights = { -20f, 0f, 20f, 500f };
            float[] slopes = { -1f, -0.0000001f, 0f, 0.0000001f, 1f };
            float[] distances = { 0f, 1f, 500f, 100000f };

            foreach (float height in heights)
            foreach (float slope in slopes)
            foreach (float distance in distances)
            {
                float opticalDepth = (float)Reflection.InvokeStatic(
                    "SolAtmosphereController",
                    "EvaluateHeightOpticalDepth",
                    new[] { typeof(float), typeof(float), typeof(float), typeof(float), typeof(float) },
                    height, slope, distance, 0.035f, 0.01f);
                Assert.That(float.IsNaN(opticalDepth) || float.IsInfinity(opticalDepth), Is.False);
                Assert.That(opticalDepth, Is.GreaterThanOrEqualTo(0f));
            }

            foreach (float anisotropy in new[] { -10f, -0.9f, 0f, 0.55f, 0.9f, 10f })
            foreach (float cosine in new[] { -1f, -0.25f, 0f, 0.75f, 1f })
            {
                float phase = (float)Reflection.InvokeStatic(
                    "SolAtmosphereController",
                    "EvaluateCornetteShanksPhase",
                    new[] { typeof(float), typeof(float) },
                    cosine, anisotropy);
                Assert.That(float.IsNaN(phase) || float.IsInfinity(phase), Is.False);
                Assert.That(phase, Is.GreaterThanOrEqualTo(0f));
            }
        }

        [Test]
        public void AtmosphereProfileAndShader_HighQualityContractsStaySynchronized()
        {
            Type profileType = Reflection.FindType("SolAtmosphereProfile");
            ScriptableObject profile = ScriptableObject.CreateInstance(profileType);
            try
            {
                Assert.That((float)profileType.GetField("phaseAnisotropy")!.GetValue(profile), Is.EqualTo(0.55f));
                Assert.That((int)profileType.GetField("raymarchStepCount")!.GetValue(profile), Is.EqualTo(32));
                Assert.That((float)profileType.GetField("raymarchJitter")!.GetValue(profile), Is.EqualTo(0.15f));
                Assert.That((float)profileType.GetField("zenithFogStrength")!.GetValue(profile), Is.EqualTo(0.12f));
                Assert.That((float)profileType.GetField("horizonFogStrength")!.GetValue(profile), Is.EqualTo(1f));
                Assert.That((float)profileType.GetField("mistBaseHeight")!.GetValue(profile), Is.EqualTo(1.5f));
                Assert.That((float)profileType.GetField("mistHeightFalloff")!.GetValue(profile), Is.EqualTo(0.12f));
                Assert.That(profileType.GetField("scatteringPower"), Is.Not.Null, "Legacy serialized field must remain available.");

                string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
                string include = File.ReadAllText(Path.Combine(projectRoot,
                    "Assets", "Sky-and-Water", "Water", "Shaders", "SolAtmosphere.hlsl"));
                string shader = File.ReadAllText(Path.Combine(projectRoot,
                    "Assets", "Sky-and-Water", "Resources", "SolAtmosphere.shader"));
                string feature = File.ReadAllText(Path.Combine(projectRoot,
                    "Assets", "Sky-and-Water", "Management", "SolAtmosphereRendererFeature.cs"));

                StringAssert.Contains("SolAtmosphereHeightIntegral", include);
                StringAssert.Contains("SolAtmosphereCornetteShanks", include);
                StringAssert.Contains("SolAtmosphereAmbientColor() * ambientVisibility", include,
                    "Storm dimming must attenuate ambient fog scattering as well as direct scattering.");
                StringAssert.Contains("source.rgb * volumetric.a + volumetric.rgb", shader);
                StringAssert.Contains("stepIndex < 32", shader);
                StringAssert.Contains("transmittance <= 0.01", shader);
                StringAssert.Contains("Sol Atmosphere Bilateral Composite", shader);
                StringAssert.Contains("Sol Atmosphere Half Resolution Spatial Filter", shader);
                StringAssert.Contains("SolAtmosphereSkyOpticalDepthScale", include);
                StringAssert.Contains("MSAASamples.None", feature);
                StringAssert.Contains("R16G16B16A16_SFloat", feature);
                StringAssert.Contains("(source.width + 1) / 2", feature);
                StringAssert.Contains("_SolAtmosphereVolumetricFiltered", feature);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void WaterDynamics_ShaderAndSurfaceSamplerShareTurbulenceContract()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string waves = File.ReadAllText(Path.Combine(projectRoot,
                "Assets", "Sky-and-Water", "Water", "Shaders", "SolWaterWaves.hlsl"));
            string water = File.ReadAllText(Path.Combine(projectRoot,
                "Assets", "Sky-and-Water", "Water", "Shaders", "Sol.Water.shader"));
            string sampler = File.ReadAllText(Path.Combine(projectRoot,
                "Assets", "Sky-and-Water", "Water", "WaterVolume.cs"));
            string manager = File.ReadAllText(Path.Combine(projectRoot,
                "Assets", "Sky-and-Water", "Management", "WaterManager.cs"));

            foreach (string contract in new[]
            {
                "turbulence * 0.18", "turbulence * 0.35",
                "turbulence * 0.75", "turbulence * 0.45"
            })
            {
                StringAssert.Contains(contract, waves);
                StringAssert.Contains(contract, sampler);
            }

            StringAssert.Contains("_Sol_WaterDynamics", water);
            StringAssert.Contains("_Sol_WaterDynamics", manager);
            StringAssert.Contains("lunarTideFactor", waves);
            StringAssert.Contains("lunarTideFactor", sampler);
            StringAssert.Contains("+ 2.37", waves);
            StringAssert.Contains("+ 2.37f", sampler);
        }

        [Test]
        public void AtmosphereSkyShaping_PreservesZenithAndMatchesHorizon()
        {
            Type[] signature =
            {
                typeof(float), typeof(float), typeof(float), typeof(float), typeof(float)
            };
            float zenith = (float)Reflection.InvokeStatic(
                "SolAtmosphereController", "EvaluateSkyOpticalDepthScale", signature,
                1f, 1f, 0.12f, 1f, 0f);
            float horizon = (float)Reflection.InvokeStatic(
                "SolAtmosphereController", "EvaluateSkyOpticalDepthScale", signature,
                0f, 1f, 0.12f, 1f, 0f);
            float whiteoutZenith = (float)Reflection.InvokeStatic(
                "SolAtmosphereController", "EvaluateSkyOpticalDepthScale", signature,
                1f, 1f, 0.12f, 1f, 1f);

            Assert.That(zenith, Is.EqualTo(0.12f).Within(0.0001f));
            Assert.That(horizon, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(whiteoutZenith, Is.EqualTo(horizon).Within(0.0001f));
        }

        [Test]
        public void TimeOfDay_DominantAtmosphereLightUsesHysteresis()
        {
            Light originalSun = RenderSettings.sun;
            GameObject timeRoot = new("Dominant atmosphere light test");
            GameObject sunRoot = new("Test sun");
            GameObject moonRoot = new("Test moon");
            timeRoot.SetActive(false);
            Light sun = sunRoot.AddComponent<Light>();
            Light moon = moonRoot.AddComponent<Light>();
            sun.type = LightType.Directional;
            moon.type = LightType.Directional;
            sun.color = Color.white;
            moon.color = Color.white;

            try
            {
                Component time = Reflection.Add(timeRoot, "Sol.ToD.TimeOfDay");
                Reflection.Set(time, "sunLight", sun);
                Reflection.Set(time, "moonLight", moon);

                sun.intensity = 1f;
                moon.intensity = 0.5f;
                Reflection.Invoke(time, "UpdateDominantAtmosphereLight");
                Assert.That(Reflection.Get<Light>(time, "DominantAtmosphereLight"), Is.SameAs(sun));

                moon.intensity = 1.2f;
                Reflection.Invoke(time, "UpdateDominantAtmosphereLight");
                Assert.That(Reflection.Get<Light>(time, "DominantAtmosphereLight"), Is.SameAs(moon));

                sun.intensity = 1.25f;
                Reflection.Invoke(time, "UpdateDominantAtmosphereLight");
                Assert.That(Reflection.Get<Light>(time, "DominantAtmosphereLight"), Is.SameAs(moon));

                sun.intensity = 1.4f;
                Reflection.Invoke(time, "UpdateDominantAtmosphereLight");
                Assert.That(Reflection.Get<Light>(time, "DominantAtmosphereLight"), Is.SameAs(sun));
                Assert.That(RenderSettings.sun, Is.SameAs(sun));
            }
            finally
            {
                RenderSettings.sun = originalSun;
                UnityEngine.Object.DestroyImmediate(timeRoot);
                UnityEngine.Object.DestroyImmediate(sunRoot);
                UnityEngine.Object.DestroyImmediate(moonRoot);
            }
        }

        [Test]
        public void Coordinator_RestoresAuthoredRenderSettingsSun()
        {
            Light originalSun = RenderSettings.sun;
            GameObject coordinatorRoot = new("Sun coordinator test");
            GameObject authoredRoot = new("Authored sun");
            GameObject runtimeRoot = new("Runtime sun");
            coordinatorRoot.SetActive(false);
            Light authored = authoredRoot.AddComponent<Light>();
            Light runtime = runtimeRoot.AddComponent<Light>();

            try
            {
                RenderSettings.sun = authored;
                Component coordinator = Reflection.Add(coordinatorRoot, "SolEnvironmentCoordinator");
                Reflection.Invoke(coordinator, "Register", new[] { typeof(UnityEngine.Object) }, coordinatorRoot);
                RenderSettings.sun = runtime;
                Reflection.Invoke(coordinator, "Unregister", new[] { typeof(UnityEngine.Object) }, coordinatorRoot);
                Assert.That(RenderSettings.sun, Is.SameAs(authored));
            }
            finally
            {
                RenderSettings.sun = originalSun;
                UnityEngine.Object.DestroyImmediate(coordinatorRoot);
                UnityEngine.Object.DestroyImmediate(authoredRoot);
                UnityEngine.Object.DestroyImmediate(runtimeRoot);
            }
        }
    }

    static class Reflection
    {
        public static Component Add(GameObject target, string fullName) => target.AddComponent(FindType(fullName));

        public static T Get<T>(object target, string name)
        {
            Type type = target.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null) return (T)property.GetValue(target);
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return (T)field.GetValue(target);
            throw new MissingMemberException(type.FullName, name);
        }

        public static void Set(object target, string name, object value)
        {
            Type type = target.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                property.SetValue(target, value);
                return;
            }

            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }

            throw new MissingMemberException(type.FullName, name);
        }

        public static object Invoke(object target, string name, Type[] parameterTypes = null, params object[] arguments)
        {
            parameterTypes ??= Type.EmptyTypes;
            MethodInfo method = target.GetType().GetMethod(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, parameterTypes, null);
            if (method == null) throw new MissingMethodException(target.GetType().FullName, name);
            return method.Invoke(target, arguments);
        }

        public static object InvokeStatic(string fullName, string name, Type[] parameterTypes, params object[] arguments)
        {
            Type type = FindType(fullName);
            MethodInfo method = type.GetMethod(name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, parameterTypes, null);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            return method.Invoke(null, arguments);
        }

        public static Type FindType(string fullName)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(candidate => candidate != null);
            return type ?? throw new TypeLoadException($"Could not find {fullName} in loaded Unity assemblies.");
        }
    }
}
