using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Sol.Tests.Runtime
{
    public sealed class EnvironmentRuntimeTests
    {
        [UnityTest]
        public IEnumerator TimeMutations_CountOnlyForwardGameplayTime()
        {
            GameObject root = new("Time mutation test");
            root.SetActive(false);
            Component time = Reflection.Add(root, "Sol.ToD.TimeOfDay");
            Reflection.Invoke(time, "SetPaused", new[] { typeof(bool) }, true);
            root.SetActive(true);
            yield return null;

            try
            {
                Reflection.Invoke(time, "SetNormalizedTime", new[] { typeof(float), typeof(UnityEngine.Object), typeof(string) }, 0.25f, null, "test correction");
                Assert.That(Reflection.Get<double>(time, "PlayerDaysElapsed"), Is.Zero);

                Reflection.Invoke(time, "AdvanceHours", new[] { typeof(float), typeof(UnityEngine.Object), typeof(string) }, 6f, null, "test advance");
                Assert.That(Reflection.Get<double>(time, "PlayerDaysElapsed"), Is.EqualTo(0.25d).Within(0.00001d));

                Reflection.Invoke(time, "RewindHours", new[] { typeof(float), typeof(UnityEngine.Object), typeof(string) }, 6f, null, "test rewind");
                Reflection.Invoke(time, "SetClockHour", new[] { typeof(float), typeof(UnityEngine.Object), typeof(string) }, 18f, null, "test correction");
                Assert.That(Reflection.Get<double>(time, "PlayerDaysElapsed"), Is.EqualTo(0.25d).Within(0.00001d));

                Reflection.Invoke(time, "SkipForwardOneDay");
                Assert.That(Reflection.Get<double>(time, "PlayerDaysElapsed"), Is.EqualTo(1.25d).Within(0.00001d));
                Reflection.Invoke(time, "SkipBackwardOneDay");
                Assert.That(Reflection.Get<double>(time, "PlayerDaysElapsed"), Is.EqualTo(1.25d).Within(0.00001d));

                double beforeSunriseSkip = Reflection.Get<double>(time, "PlayerDaysElapsed");
                Reflection.Invoke(time, "SkipToNextSunrise");
                Assert.That(Reflection.Get<double>(time, "PlayerDaysElapsed"), Is.GreaterThan(beforeSunriseSkip));

                Reflection.Invoke(time, "RestoreTimeSnapshot",
                    new[] { typeof(float), typeof(int), typeof(int), typeof(int), typeof(long), typeof(double), typeof(UnityEngine.Object), typeof(string) },
                    0.75f, 4, 3, 150, -12L, 9.5d, null, "test restore");
                Assert.That(Reflection.Get<long>(time, "WorldDayIndex"), Is.EqualTo(-12L));
                Assert.That(Reflection.Get<double>(time, "PlayerDaysElapsed"), Is.EqualTo(9.5d).Within(0.00001d));
            }
            finally
            {
                UnityEngine.Object.Destroy(root);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator DemoTimeOfDaySlider_UsesCanonicalMutationPath()
        {
            GameObject timeRoot = new("Slider time authority");
            timeRoot.SetActive(false);
            Component time = Reflection.Add(timeRoot, "Sol.ToD.TimeOfDay");
            Reflection.Invoke(time, "SetPaused", new[] { typeof(bool) }, true);
            timeRoot.SetActive(true);

            GameObject uiRoot = new("Slider scrubber");
            uiRoot.SetActive(false);
            Component slider = Reflection.Add(uiRoot, "UnityEngine.UI.Slider");
            Component cameraController = Reflection.Add(uiRoot, "DemoCameraController");
            Reflection.Set(cameraController, "timeOfDay", time);
            Reflection.Set(cameraController, "timeOfDaySlider", slider);
            uiRoot.SetActive(true);
            yield return null;

            Reflection.Set(slider, "value", 0.75f);

            Assert.That(Reflection.Get<float>(time, "CurrentTime"), Is.EqualTo(0.75f).Within(0.0001f));

            UnityEngine.Object.Destroy(uiRoot);
            UnityEngine.Object.Destroy(timeRoot);
            yield return null;
        }

        [UnityTest]
        public IEnumerator NaturalTicking_AddsFractionalPlayerTime()
        {
            GameObject root = new("Natural ticking test");
            root.SetActive(false);
            Component time = Reflection.Add(root, "Sol.ToD.TimeOfDay");
            Reflection.Set(time, "cycleDurationMinutes", 0.1f);
            root.SetActive(true);
            yield return null;
            double before = Reflection.Get<double>(time, "PlayerDaysElapsed");
            yield return null;
            double after = Reflection.Get<double>(time, "PlayerDaysElapsed");

            UnityEngine.Object.Destroy(root);
            yield return null;
            Assert.That(after, Is.GreaterThan(before));
        }

        [UnityTest]
        public IEnumerator ManagedGridVolume_IsRemovedWhenGridDisables()
        {
            int baseline = Reflection.GetStatic<int>("WaterVolume", "VolumeCount");
            GameObject root = new("Water grid lifecycle test");
            root.SetActive(false);
            Component grid = Reflection.Add(root, "WaterTileGrid");
            Reflection.Set(grid, "gridRadius", 0);
            Reflection.Set(grid, "tileResolution", 2);
            Reflection.Set(grid, "followCamera", false);
            root.SetActive(true);
            yield return null;

            Assert.That(Reflection.GetStatic<int>("WaterVolume", "VolumeCount"), Is.EqualTo(baseline + 1));
            root.SetActive(false);
            yield return null;
            yield return null;
            Assert.That(Reflection.GetStatic<int>("WaterVolume", "VolumeCount"), Is.EqualTo(baseline));

            UnityEngine.Object.Destroy(root);
            yield return null;
        }

        [UnityTest]
        public IEnumerator WeatherDisable_RestoresOwnedWaterFields()
        {
            GameObject waterRoot = new("Water manager");
            waterRoot.SetActive(false);
            Component water = Reflection.Add(waterRoot, "SolWaterManager");
            Vector3 authoredWind = new(0.2f, 0f, 0.9f);
            Reflection.Set(water, "rainIntensity", 0.31f);
            Reflection.Set(water, "windDirection", authoredWind);
            Reflection.Set(water, "windStrength", 1.37f);
            Reflection.Set(water, "globalWaveSpeedMultiplier", 0.82f);
            Reflection.Set(water, "waterTurbulence", 0.19f);
            waterRoot.SetActive(true);

            GameObject weatherRoot = new("Weather manager");
            weatherRoot.SetActive(false);
            Component weather = Reflection.Add(weatherRoot, "SolWeatherManager");
            Reflection.Set(weather, "waterManager", water);
            weatherRoot.SetActive(true);
            yield return null;

            Reflection.Set(water, "rainIntensity", 1f);
            Reflection.Set(water, "windDirection", Vector3.left);
            Reflection.Set(water, "windStrength", 3f);
            Reflection.Set(water, "globalWaveSpeedMultiplier", 2f);
            Reflection.Set(water, "waterTurbulence", 1f);
            weatherRoot.SetActive(false);
            yield return null;

            Assert.That(Reflection.Get<float>(water, "rainIntensity"), Is.EqualTo(0.31f).Within(0.0001f));
            Assert.That(Reflection.Get<Vector3>(water, "windDirection"), Is.EqualTo(authoredWind));
            Assert.That(Reflection.Get<float>(water, "windStrength"), Is.EqualTo(1.37f).Within(0.0001f));
            Assert.That(Reflection.Get<float>(water, "globalWaveSpeedMultiplier"), Is.EqualTo(0.82f).Within(0.0001f));
            Assert.That(Reflection.Get<float>(water, "waterTurbulence"), Is.EqualTo(0.19f).Within(0.0001f));
            Assert.That(Reflection.Get<float>(weather, "CurrentRainIntensity"), Is.Zero);
            Assert.That(Reflection.Get<float>(weather, "CurrentDim"), Is.Zero);

            UnityEngine.Object.Destroy(weatherRoot);
            UnityEngine.Object.Destroy(waterRoot);
            yield return null;
        }

        [UnityTest]
        public IEnumerator CelestialBody_TracksChangedMainCamera()
        {
            GameObject cameraOneRoot = new("Camera one");
            Camera cameraOne = cameraOneRoot.AddComponent<Camera>();
            cameraOneRoot.tag = "MainCamera";
            cameraOneRoot.transform.position = new Vector3(10f, 20f, 30f);

            GameObject cameraTwoRoot = new("Camera two");
            Camera cameraTwo = cameraTwoRoot.AddComponent<Camera>();
            cameraTwoRoot.tag = "Untagged";
            cameraTwoRoot.transform.position = new Vector3(-40f, 5f, 70f);

            GameObject bodyRoot = new("Celestial body");
            Component body = Reflection.Add(bodyRoot, "Sol.ToD.CelestialBody");
            Reflection.Set(body, "Direction", Vector3.up);
            Reflection.Invoke(body, "Refresh");
            Vector3 firstOffset = bodyRoot.transform.position - cameraOne.transform.position;

            cameraOneRoot.tag = "Untagged";
            cameraTwoRoot.tag = "MainCamera";
            yield return null;
            Reflection.Invoke(body, "Refresh");
            Vector3 secondOffset = bodyRoot.transform.position - cameraTwo.transform.position;

            Assert.That(Vector3.Distance(firstOffset, Vector3.up * 800f), Is.LessThan(0.001f));
            Assert.That(Vector3.Distance(secondOffset, Vector3.up * 800f), Is.LessThan(0.001f));

            UnityEngine.Object.Destroy(bodyRoot);
            UnityEngine.Object.Destroy(cameraOneRoot);
            UnityEngine.Object.Destroy(cameraTwoRoot);
            yield return null;
        }

        [UnityTest]
        public IEnumerator CanonicalWorldDelta_RespondsToUnityScaleSolScaleAndPause()
        {
            float originalUnityScale = Time.timeScale;
            GameObject root = new("Runtime canonical clock test");
            root.SetActive(false);
            Component time = Reflection.Add(root, "Sol.ToD.TimeOfDay");
            Reflection.Invoke(time, "SetTimeScale", new[] { typeof(float) }, 10f);
            root.SetActive(true);

            try
            {
                Time.timeScale = 0.5f;
                yield return null;
                yield return null;
                float expected = Time.deltaTime * 10f;
                Assert.That(Reflection.Get<float>(time, "WorldDeltaSeconds"), Is.EqualTo(expected).Within(Mathf.Max(0.0001f, expected * 0.05f)));
                Assert.That(Reflection.Get<float>(time, "PresentationDeltaSeconds"), Is.EqualTo(Time.deltaTime).Within(0.0001f));

                Reflection.Invoke(time, "SetPaused", new[] { typeof(bool) }, true);
                yield return null;
                Assert.That(Reflection.Get<float>(time, "WorldDeltaSeconds"), Is.Zero);
                Assert.That(Reflection.Get<float>(time, "PresentationDeltaSeconds"), Is.Zero);

                Reflection.Invoke(time, "SetPaused", new[] { typeof(bool) }, false);
                Reflection.Invoke(time, "SetTimeScale", new[] { typeof(float) }, 100f);
                yield return null;
                expected = Time.deltaTime * 100f;
                Assert.That(Reflection.Get<float>(time, "WorldDeltaSeconds"), Is.EqualTo(expected).Within(Mathf.Max(0.0001f, expected * 0.05f)));
                Assert.That(Reflection.Get<float>(time, "PresentationDeltaSeconds"), Is.EqualTo(Time.deltaTime).Within(0.0001f));
            }
            finally
            {
                Time.timeScale = originalUnityScale;
                UnityEngine.Object.Destroy(root);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator PositiveSkipsRetargetChronologyWithoutSnappingPresentation()
        {
            GameObject timeRoot = new("Weather chronology clock");
            timeRoot.SetActive(false);
            Component time = Reflection.Add(timeRoot, "Sol.ToD.TimeOfDay");
            Reflection.Invoke(time, "SetPaused", new[] { typeof(bool) }, true);

            GameObject weatherRoot = new("Weather chronology manager");
            weatherRoot.SetActive(false);
            Component weather = Reflection.Add(weatherRoot, "SolWeatherManager");
            Reflection.Set(weather, "todManager", time);
            Reflection.Set(weather, "autoCycle", true);
            Reflection.Set(weather, "avoidRepeat", true);
            Reflection.Set(weather, "durationHoursRange", new Vector2(1f, 1f));
            Reflection.Set(weather, "transitionDurationSeconds", 3f);

            timeRoot.SetActive(true);
            weatherRoot.SetActive(true);
            yield return null;

            try
            {
                object initialTarget = Reflection.Get<object>(weather, "TargetProfile");
                Assert.That(Reflection.Get<float>(weather, "_blend"), Is.EqualTo(1f));

                Reflection.Invoke(time, "AdvanceHours", new[] { typeof(float), typeof(UnityEngine.Object), typeof(string) }, 1.1f, null, "positive skip");
                object afterAdvanceTarget = Reflection.Get<object>(weather, "TargetProfile");
                float afterAdvance = Reflection.Get<float>(weather, "_blend");
                Assert.That(afterAdvanceTarget, Is.Not.SameAs(initialTarget));
                Assert.That(afterAdvance, Is.Zero, "A skip selects a target but must not consume presentation time.");

                Reflection.Invoke(time, "RewindHours", new[] { typeof(float), typeof(UnityEngine.Object), typeof(string) }, 6f, null, "rewind");
                Assert.That(Reflection.Get<float>(weather, "_blend"), Is.EqualTo(afterAdvance).Within(0.001f));
                Assert.That(Reflection.Get<object>(weather, "TargetProfile"), Is.SameAs(afterAdvanceTarget));

                Reflection.Invoke(time, "SetClockHour", new[] { typeof(float), typeof(UnityEngine.Object), typeof(string) }, 3f, null, "correction");
                Assert.That(Reflection.Get<float>(weather, "_blend"), Is.EqualTo(afterAdvance).Within(0.001f));
                Assert.That(Reflection.Get<object>(weather, "TargetProfile"), Is.SameAs(afterAdvanceTarget));
            }
            finally
            {
                UnityEngine.Object.Destroy(weatherRoot);
                UnityEngine.Object.Destroy(timeRoot);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator RainController_ClampsExposureAndFollowsMainCameraChanges()
        {
            GameObject cameraOneRoot = new("Rain camera one");
            Camera cameraOne = cameraOneRoot.AddComponent<Camera>();
            cameraOneRoot.tag = "MainCamera";

            GameObject cameraTwoRoot = new("Rain camera two");
            Camera cameraTwo = cameraTwoRoot.AddComponent<Camera>();
            cameraTwoRoot.tag = "Untagged";

            GameObject rainRoot = new("Rain controller test");
            rainRoot.SetActive(false);
            Component rain = Reflection.Add(rainRoot, "SolRainVfxController");
            Reflection.Set(rain, "probeShelter", false);
            Reflection.Set(rain, "enableMist", false);
            rainRoot.SetActive(true);
            yield return null;

            try
            {
                Assert.That(Reflection.Get<Camera>(rain, "ActiveCamera"), Is.SameAs(cameraOne));
                Reflection.Invoke(rain, "SetRainExposure", new[] { typeof(float) }, -1f);
                Assert.That(Reflection.Get<float>(rain, "RainExposure"), Is.Zero);
                Reflection.Invoke(rain, "SetRainExposure", new[] { typeof(float) }, 0.4f);
                Assert.That(Reflection.Get<float>(rain, "RainExposure"), Is.EqualTo(0.4f).Within(0.0001f));
                Reflection.Invoke(rain, "SetRainExposure", new[] { typeof(float) }, 2f);
                Assert.That(Reflection.Get<float>(rain, "RainExposure"), Is.EqualTo(1f));

                cameraOneRoot.tag = "Untagged";
                cameraTwoRoot.tag = "MainCamera";
                yield return null;
                Assert.That(Reflection.Get<Camera>(rain, "ActiveCamera"), Is.SameAs(cameraTwo));
            }
            finally
            {
                UnityEngine.Object.Destroy(rainRoot);
                UnityEngine.Object.Destroy(cameraOneRoot);
                UnityEngine.Object.Destroy(cameraTwoRoot);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator RainController_PausesResumesAndBoundsTimeLapseDensity()
        {
            GameObject cameraRoot = new("Rain lifecycle camera");
            cameraRoot.tag = "MainCamera";
            cameraRoot.AddComponent<Camera>();

            GameObject timeRoot = new("Rain lifecycle clock");
            timeRoot.SetActive(false);
            Component time = Reflection.Add(timeRoot, "Sol.ToD.TimeOfDay");
            Reflection.Invoke(time, "SetTimeScale", new[] { typeof(float) }, 1f);

            GameObject weatherRoot = new("Rain lifecycle weather");
            weatherRoot.SetActive(false);
            Component weather = Reflection.Add(weatherRoot, "SolWeatherManager");
            Reflection.Set(weather, "todManager", time);
            Reflection.Set(weather, "autoCycle", false);

            GameObject rainRoot = new("Rain lifecycle controller");
            rainRoot.SetActive(false);
            Component rain = Reflection.Add(rainRoot, "SolRainVfxController");
            Reflection.Set(rain, "timeOfDay", time);
            Reflection.Set(rain, "weatherManager", weather);
            Reflection.Set(rain, "probeShelter", false);
            Reflection.Set(rain, "enableMist", false);

            timeRoot.SetActive(true);
            weatherRoot.SetActive(true);
            rainRoot.SetActive(true);
            Reflection.Invoke(weather, "SetWeather", new[] { typeof(int), typeof(bool) }, 3, true);
            yield return null;
            yield return null;

            try
            {
                ParticleSystem particles = Reflection.Get<ParticleSystem>(rain, "_rain");
                Assert.That(particles, Is.Not.Null);
                Assert.That(particles.isPlaying, Is.True);

                Reflection.Invoke(time, "SetPaused", new[] { typeof(bool) }, true);
                yield return null;
                Assert.That(particles.isPaused, Is.True);

                Reflection.Invoke(time, "SetPaused", new[] { typeof(bool) }, false);
                yield return null;
                Assert.That(particles.isPlaying, Is.True);

                Reflection.Invoke(time, "SetTimeScale", new[] { typeof(float) }, 0f);
                yield return null;
                Assert.That(particles.isPaused, Is.True);

                Reflection.Invoke(time, "SetTimeScale", new[] { typeof(float) }, 100f);
                yield return null;
                Assert.That(particles.isPlaying, Is.True);
                Assert.That(particles.main.simulationSpeed, Is.EqualTo(10f).Within(0.001f));
                Assert.That(particles.emission.rateOverTime.constant, Is.LessThanOrEqualTo(85.01f));

                Reflection.Invoke(weather, "SetWeather", new[] { typeof(int), typeof(bool) }, 0, true);
                yield return null;
                Assert.That(particles.isPaused, Is.False, "Dry weather must not freeze stale particles.");
                Assert.That(particles.emission.rateOverTime.constant, Is.Zero.Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.Destroy(rainRoot);
                UnityEngine.Object.Destroy(weatherRoot);
                UnityEngine.Object.Destroy(timeRoot);
                UnityEngine.Object.Destroy(cameraRoot);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator AtmosphereController_RestoresLegacyFogAndGlobalsOnDisable()
        {
            bool originalFog = RenderSettings.fog;
            float originalActive = Shader.GetGlobalFloat("_SolAtmosphereActive");
            RenderSettings.fog = true;

            GameObject root = new("Atmosphere lifecycle test");
            root.SetActive(false);
            Reflection.Add(root, "SolAtmosphereController");
            root.SetActive(true);
            yield return null;

            Assert.That(RenderSettings.fog, Is.False);
            Assert.That(Shader.GetGlobalFloat("_SolAtmosphereActive"), Is.EqualTo(1f));

            root.SetActive(false);
            yield return null;
            Assert.That(RenderSettings.fog, Is.True);
            Assert.That(Shader.GetGlobalFloat("_SolAtmosphereActive"), Is.EqualTo(originalActive).Within(0.0001f));

            UnityEngine.Object.Destroy(root);
            RenderSettings.fog = originalFog;
            yield return null;
        }

        [UnityTest]
        public IEnumerator AtmosphereController_DisablingDuplicateDoesNotClearActiveAuthority()
        {
            GameObject authorityRoot = new("Atmosphere authority");
            authorityRoot.SetActive(false);
            Component authority = Reflection.Add(authorityRoot, "SolAtmosphereController");
            Reflection.Set(authority, "disableLegacyFog", false);
            authorityRoot.SetActive(true);
            yield return null;

            GameObject duplicateRoot = new("Atmosphere duplicate");
            duplicateRoot.SetActive(false);
            Component duplicate = Reflection.Add(duplicateRoot, "SolAtmosphereController");
            Reflection.Set(duplicate, "disableLegacyFog", false);
            duplicateRoot.SetActive(true);
            yield return null;

            try
            {
                Assert.That(Reflection.Get<bool>(duplicate, "enabled"), Is.False);
                Assert.That(Reflection.GetStatic<object>("SolAtmosphereController", "Active"), Is.SameAs(authority));
                Assert.That(Shader.GetGlobalFloat("_SolAtmosphereActive"), Is.EqualTo(1f));
            }
            finally
            {
                UnityEngine.Object.Destroy(duplicateRoot);
                UnityEngine.Object.Destroy(authorityRoot);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator WeatherFog_IsRebuiltBeforeAtmosphereConsumesTheFrame()
        {
            float originalDensity = RenderSettings.fogDensity;

            GameObject timeRoot = new("Weather fog clock");
            timeRoot.SetActive(false);
            Component time = Reflection.Add(timeRoot, "Sol.ToD.TimeOfDay");
            Reflection.Set(time, "controlSkybox", false);
            Reflection.Set(time, "controlAmbient", false);
            Reflection.Set(time, "fogDayDensity", 0.01f);
            Reflection.Set(time, "fogNightDensity", 0.01f);
            Reflection.Invoke(time, "SetPaused", new[] { typeof(bool) }, true);

            GameObject weatherRoot = new("Weather fog director");
            weatherRoot.SetActive(false);
            Component weather = Reflection.Add(weatherRoot, "SolWeatherManager");
            Reflection.Set(weather, "todManager", time);
            Reflection.Set(weather, "autoCycle", false);
            Reflection.Set(weather, "springDailyFogRange", Vector2.zero);
            Reflection.Set(weather, "summerDailyFogRange", Vector2.zero);
            Reflection.Set(weather, "autumnDailyFogRange", Vector2.zero);
            Reflection.Set(weather, "winterDailyFogRange", Vector2.zero);

            GameObject atmosphereRoot = new("Weather fog atmosphere");
            atmosphereRoot.SetActive(false);
            Component atmosphere = Reflection.Add(atmosphereRoot, "SolAtmosphereController");
            Reflection.Set(atmosphere, "timeOfDay", time);
            Reflection.Set(atmosphere, "weatherManager", weather);
            Reflection.Set(atmosphere, "disableLegacyFog", false);

            timeRoot.SetActive(true);
            weatherRoot.SetActive(true);
            Reflection.Invoke(weather, "SetWeather", new[] { typeof(int), typeof(bool) }, 3, true);
            atmosphereRoot.SetActive(true);
            yield return null;

            try
            {
                Assert.That(RenderSettings.fogDensity, Is.EqualTo(0.013f).Within(0.0001f));
                Assert.That(Reflection.Get<float>(atmosphere, "CurrentDensity"),
                    Is.EqualTo(RenderSettings.fogDensity).Within(0.0001f));
            }
            finally
            {
                UnityEngine.Object.Destroy(atmosphereRoot);
                UnityEngine.Object.Destroy(weatherRoot);
                UnityEngine.Object.Destroy(timeRoot);
                RenderSettings.fogDensity = originalDensity;
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator AtmosphereController_ConsumesWeatherAdjustedFogDensityOnce()
        {
            float originalDensity = RenderSettings.fogDensity;
            GameObject weatherRoot = new("Atmosphere density weather");
            weatherRoot.SetActive(false);
            Component weather = Reflection.Add(weatherRoot, "SolWeatherManager");
            Reflection.Set(weather, "autoCycle", false);

            GameObject atmosphereRoot = new("Atmosphere density controller");
            atmosphereRoot.SetActive(false);
            Component atmosphere = Reflection.Add(atmosphereRoot, "SolAtmosphereController");
            Reflection.Set(atmosphere, "weatherManager", weather);
            Reflection.Set(atmosphere, "disableLegacyFog", false);

            weatherRoot.SetActive(true);
            Reflection.Invoke(weather, "SetWeather", new[] { typeof(int), typeof(bool) }, 3, true);
            atmosphereRoot.SetActive(true);
            RenderSettings.fogDensity = 0.0123f;
            yield return null;

            try
            {
                Assert.That(Reflection.Get<float>(atmosphere, "CurrentDensity"),
                    Is.EqualTo(0.0123f).Within(0.0001f));
            }
            finally
            {
                atmosphereRoot.SetActive(false);
                weatherRoot.SetActive(false);
                RenderSettings.fogDensity = originalDensity;
                UnityEngine.Object.Destroy(atmosphereRoot);
                UnityEngine.Object.Destroy(weatherRoot);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator AtmosphereRenderer_RendersAtTwoTimesMsaaWithDynamicScaleTarget()
        {
            GameObject atmosphereRoot = new("Atmosphere render smoke test");
            atmosphereRoot.SetActive(false);
            Component atmosphere = Reflection.Add(atmosphereRoot, "SolAtmosphereController");
            Type qualityType = atmosphere.GetType().Assembly.GetType("SolAtmosphereQuality");
            Reflection.Invoke(atmosphere, "SetQuality", new[] { qualityType }, Enum.ToObject(qualityType, 2));

            GameObject cameraRoot = new("Atmosphere render camera");
            Camera camera = cameraRoot.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.transform.position = new Vector3(0f, 5f, -10f);
            camera.transform.rotation = Quaternion.Euler(8f, 0f, 0f);

            RenderTexture target = null;
            try
            {
                target = new RenderTexture(320, 180, 24)
                {
                    antiAliasing = 2,
                    useDynamicScale = true,
                    name = "Sol Atmosphere Test Target",
                };
                Assert.That(target.Create(), Is.True, "The platform rejected the 2x MSAA dynamic-scale render target.");
                atmosphereRoot.SetActive(true);

                var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest
                {
                    destination = target,
                    mipLevel = 0,
                    slice = 0,
                    face = CubemapFace.Unknown,
                };
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);

                yield return null;
                yield return null;

                Assert.That(target.IsCreated(), Is.True);
                Assert.That(target.antiAliasing, Is.EqualTo(2));
            }
            finally
            {
                if (target != null)
                {
                    target.Release();
                    UnityEngine.Object.Destroy(target);
                }

                UnityEngine.Object.Destroy(cameraRoot);
                UnityEngine.Object.Destroy(atmosphereRoot);
            }
            yield return null;
        }
    }

    static class Reflection
    {
        public static Component Add(GameObject target, string fullName) => target.AddComponent(FindType(fullName));

        public static T Get<T>(object target, string name)
        {
            MemberInfo member = FindMember(target.GetType(), name);
            return member switch
            {
                PropertyInfo property => (T)property.GetValue(target),
                FieldInfo field => (T)field.GetValue(target),
                _ => throw new MissingMemberException(target.GetType().FullName, name),
            };
        }

        public static T GetStatic<T>(string fullName, string name)
        {
            Type type = FindType(fullName);
            PropertyInfo property = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null) return (T)property.GetValue(null);
            FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return (T)field.GetValue(null);
            throw new MissingMemberException(type.FullName, name);
        }

        public static void Set(object target, string name, object value)
        {
            MemberInfo member = FindMember(target.GetType(), name);
            switch (member)
            {
                case PropertyInfo property:
                    property.SetValue(target, value);
                    return;
                case FieldInfo field:
                    field.SetValue(target, value);
                    return;
                default:
                    throw new MissingMemberException(target.GetType().FullName, name);
            }
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

        static MemberInfo FindMember(Type type, string name)
            => (MemberInfo)type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
               ?? type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        static Type FindType(string fullName)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(candidate => candidate != null);
            return type ?? throw new TypeLoadException($"Could not find {fullName} in loaded Unity assemblies.");
        }
    }
}
