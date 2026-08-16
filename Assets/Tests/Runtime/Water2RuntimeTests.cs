using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Sol.Tests.Runtime
{
    public sealed class Water2RuntimeTests
    {
        [UnityTest]
        public IEnumerator HighTierOcean_ExecutesRenderGraphFftAndMsaaSurfacePasses()
        {
            Type profileType = FindType("Sol.Water.SolWaterProfile");
            Type qualityType = FindType("Sol.Water.SolWaterQualityProfile");
            Type qualityEnum = FindType("Sol.Water.SolWaterQualityTier");
            Type idType = FindType("Sol.Water.SolWaterBodyId");
            ScriptableObject profile = ScriptableObject.CreateInstance(profileType);
            ScriptableObject quality = ScriptableObject.CreateInstance(qualityType);
            Reflection.Set(quality, "tier", Enum.Parse(qualityEnum, "High"));
            Reflection.Set(quality, "planarReflections", false);

            GameObject worldRoot = new("Water 2 render world");
            worldRoot.SetActive(false);
            Reflection.Add(worldRoot, "Sol.Environment.SolEnvironmentWorld");
            Component waterWorld = Reflection.Add(worldRoot, "Sol.Water.SolWaterWorld");
            Reflection.Set(waterWorld, "defaultProfile", profile);
            Reflection.Set(waterWorld, "qualityProfile", quality);

            GameObject oceanObject = new("Water 2 test ocean");
            oceanObject.transform.SetParent(worldRoot.transform);
            Component ocean = Reflection.Add(oceanObject, "Sol.Water.SolWaterBody");
            Reflection.Set(ocean, "bodyId", Activator.CreateInstance(idType, "runtime-ocean"));
            Reflection.Set(ocean, "profile", profile);
            Reflection.Set(ocean, "enablePlanarReflection", false);

            GameObject cameraObject = new("Water 2 render camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            ConfigureUrpCamera(cameraObject);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.1f, 0.2f, 0.3f);
            camera.transform.SetPositionAndRotation(
                new Vector3(0f, 6f, -18f), Quaternion.Euler(12f, 0f, 0f));
            camera.farClipPlane = 2000f;
            RenderTexture target = new(320, 180, 24)
            {
                name = "Water 2 Runtime Test Target",
                antiAliasing = 2,
            };
            // URP's player-side RenderGraph treats a request-only destination as the
            // back buffer. Bind the target to the camera as well so renderer features
            // receive an intermediate colour target in both Editor and Player tests.
            camera.targetTexture = target;

            try
            {
                Assert.That(target.Create(), Is.True);
                worldRoot.SetActive(true);
                yield return null;
                RenderPipeline.StandardRequest request = new()
                {
                    destination = target,
                    mipLevel = 0,
                    slice = 0,
                    face = CubemapFace.Unknown,
                };
                RenderPipeline.SubmitRenderRequest(camera, request);
                yield return null;
                yield return null;
                Assert.That(target.IsCreated(), Is.True);
            }
            finally
            {
                camera.targetTexture = null;
                target.Release();
                UnityEngine.Object.Destroy(target);
                UnityEngine.Object.Destroy(cameraObject);
                UnityEngine.Object.Destroy(worldRoot);
                UnityEngine.Object.Destroy(profile);
                UnityEngine.Object.Destroy(quality);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator OpenOceanSkyFallback_RemainsTealWhenSsrHasNoHit()
        {
            Type profileType = FindType("Sol.Water.SolWaterProfile");
            Type qualityType = FindType("Sol.Water.SolWaterQualityProfile");
            Type qualityEnum = FindType("Sol.Water.SolWaterQualityTier");
            Type idType = FindType("Sol.Water.SolWaterBodyId");
            ScriptableObject profile = ScriptableObject.CreateInstance(profileType);
            ScriptableObject quality = ScriptableObject.CreateInstance(qualityType);
            Reflection.Set(profile, "skyReflectionStrength", 1f);
            Reflection.Set(quality, "tier", Enum.Parse(qualityEnum, "Low"));
            Reflection.Set(quality, "screenSpaceReflections", true);
            Reflection.Set(quality, "planarReflections", false);

            Color previousSky = RenderSettings.ambientSkyColor;
            Color previousEquator = RenderSettings.ambientEquatorColor;
            Color previousGround = RenderSettings.ambientGroundColor;
            RenderSettings.ambientSkyColor = new Color(0.08f, 0.48f, 0.78f);
            RenderSettings.ambientEquatorColor = new Color(0.06f, 0.38f, 0.58f);
            RenderSettings.ambientGroundColor = new Color(0.025f, 0.16f, 0.2f);

            GameObject worldRoot = new("Water 2 sky fallback world");
            worldRoot.SetActive(false);
            Reflection.Add(worldRoot, "Sol.Environment.SolEnvironmentWorld");
            Component waterWorld = Reflection.Add(worldRoot, "Sol.Water.SolWaterWorld");
            Reflection.Set(waterWorld, "defaultProfile", profile);
            Reflection.Set(waterWorld, "qualityProfile", quality);

            GameObject oceanObject = new("Water 2 sky fallback ocean");
            oceanObject.transform.SetParent(worldRoot.transform);
            Component ocean = Reflection.Add(oceanObject, "Sol.Water.SolWaterBody");
            Reflection.Set(ocean, "bodyId", Activator.CreateInstance(idType, "sky-fallback-ocean"));
            Reflection.Set(ocean, "profile", profile);
            Reflection.Set(ocean, "enablePlanarReflection", false);

            GameObject cameraObject = new("Water 2 sky fallback camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            ConfigureUrpCamera(cameraObject);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.transform.SetPositionAndRotation(
                new Vector3(0f, 5f, -12f), Quaternion.Euler(16f, 0f, 0f));
            camera.farClipPlane = 1000f;
            RenderTexture target = new(192, 108, 24)
            {
                name = "Water 2 Sky Fallback Test Target",
            };
            camera.targetTexture = target;

            try
            {
                Assert.That(target.Create(), Is.True);
                worldRoot.SetActive(true);
                yield return null;
                RenderPipeline.StandardRequest request = new()
                {
                    destination = target,
                    mipLevel = 0,
                    slice = 0,
                    face = CubemapFace.Unknown,
                };

                RenderPipeline.SubmitRenderRequest(camera, request);
                yield return null;
                yield return null;
                Color withSsr = ReadLowerWaterAverage(target);

                Reflection.Set(quality, "screenSpaceReflections", false);
                RenderPipeline.SubmitRenderRequest(camera, request);
                yield return null;
                yield return null;
                Color withoutSsr = ReadLowerWaterAverage(target);

                Assert.That(withSsr.b, Is.GreaterThan(withSsr.r * 1.15f));
                Assert.That(withSsr.g, Is.GreaterThan(withSsr.r * 1.08f));
                Assert.That(Mathf.Abs(withSsr.r - withoutSsr.r), Is.LessThan(0.08f));
                Assert.That(Mathf.Abs(withSsr.g - withoutSsr.g), Is.LessThan(0.08f));
                Assert.That(Mathf.Abs(withSsr.b - withoutSsr.b), Is.LessThan(0.08f));
            }
            finally
            {
                RenderSettings.ambientSkyColor = previousSky;
                RenderSettings.ambientEquatorColor = previousEquator;
                RenderSettings.ambientGroundColor = previousGround;
                camera.targetTexture = null;
                target.Release();
                UnityEngine.Object.Destroy(target);
                UnityEngine.Object.Destroy(cameraObject);
                UnityEngine.Object.Destroy(worldRoot);
                UnityEngine.Object.Destroy(profile);
                UnityEngine.Object.Destroy(quality);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator OpenOcean_RemainsConsistentAcrossFourHeadingsWithSsrEnabledOrDisabled()
        {
            WaterTestRig rig = new("four-heading-ocean");
            try
            {
                // Exercise the production ocean spectrum. A flat test surface cannot
                // expose downward fallback rays caused by strongly perturbed normals.
                rig.ConfigureSpectrum(0.85f, 0.92f);
                Reflection.Set(rig.Quality, "tier",
                    Enum.Parse(FindType("Sol.Water.SolWaterQualityTier"), "High"));
                float[] headings = { 0f, 90f, 180f, 270f };
                foreach (float heading in headings)
                {
                    rig.Camera.transform.rotation = Quaternion.Euler(16f, heading, 0f);
                    Reflection.Set(rig.Quality, "screenSpaceReflections", true);
                    rig.Render();
                    yield return null;
                    rig.Render();
                    yield return null;
                    Color withSsr = ReadLowerWaterAverage(rig.Target);
                    Color left = ReadAverage(rig.Target, new RectInt(12, 8, 40, 20));
                    Color right = ReadAverage(rig.Target, new RectInt(rig.Target.width - 52, 8, 40, 20));
                    Color middle = ReadAverage(rig.Target,
                        new RectInt(rig.Target.width / 2 - 20, 8, 40, 20));

                    Reflection.Set(rig.Quality, "screenSpaceReflections", false);
                    // Two renders, matching the SSR-enabled path above. Reading after a
                    // single frame compared a settled image against a transitional one:
                    // disabling SSR invalidates per-camera reflection history, and the
                    // frame that clears it is not representative.
                    rig.Render();
                    yield return null;
                    rig.Render();
                    yield return null;
                    Color withoutSsr = ReadLowerWaterAverage(rig.Target);

                    AssertTeal(withSsr, $"heading {heading}");
                    AssertColorNear(withSsr, withoutSsr, 0.08f,
                        $"Open-water SSR changed the fallback at heading {heading}.");
                    // This guards against the water splitting into two mismatched halves.
                    // Comparing the corners alone cannot tell a seam from a legitimate
                    // gradient, and with the sun off to one side the sky reflection is
                    // genuinely brighter on that side, so a corner-equality bound fails
                    // on correct output. A smooth gradient puts the midpoint between the
                    // two edges; a seam does not.
                    float leftLuminance = Luminance(left);
                    float rightLuminance = Luminance(right);
                    float middleLuminance = Luminance(middle);
                    float lower = Mathf.Min(leftLuminance, rightLuminance);
                    float upper = Mathf.Max(leftLuminance, rightLuminance);
                    float slack = 0.05f + (upper - lower) * 0.25f;
                    Assert.That(middleLuminance,
                        Is.InRange(lower - slack, upper + slack),
                        $"Open water split into mismatched halves at heading {heading}: "
                        + $"left {leftLuminance:F3}, middle {middleLuminance:F3}, right {rightLuminance:F3}.");
                    Assert.That(upper - lower, Is.LessThan(0.45f),
                        $"Open water brightness varied implausibly across the frame at heading {heading}.");
                }
            }
            finally
            {
                rig.Dispose();
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator FarPlaneSamples_AreRejectedInsteadOfWashingOutTheOcean()
        {
            WaterTestRig rig = new("far-plane-rejection");
            try
            {
                rig.Camera.farClipPlane = 80f;
                rig.Camera.transform.rotation = Quaternion.Euler(4f, 37f, 0f);
                Reflection.Set(rig.Quality, "screenSpaceReflections", true);
                rig.Render();
                yield return null;
                rig.Render();
                yield return null;
                Color withSsr = ReadAverage(rig.Target, new RectInt(8, 8, rig.Target.width - 16, 32));

                Reflection.Set(rig.Quality, "screenSpaceReflections", false);
                rig.Render();
                yield return null;
                Color withoutSsr = ReadAverage(rig.Target, new RectInt(8, 8, rig.Target.width - 16, 32));

                AssertTeal(withSsr, "far-plane horizon");
                AssertColorNear(withSsr, withoutSsr, 0.08f,
                    "Far-plane depth was accepted as an SSR hit.");
            }
            finally
            {
                rig.Dispose();
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator SubmergedTerrain_IsNotAcceptedAsAnSsrReflectionHit()
        {
            WaterTestRig rig = null;
            GameObject seabed = null;
            Material seabedMaterial = null;
            try
            {
                rig = new WaterTestRig("submerged-terrain-rejection");
                seabed = GameObject.CreatePrimitive(PrimitiveType.Cube);
                seabedMaterial = CreateUnlitMaterial(new Color(1f, 0.02f, 0.7f, 1f));
                seabed.name = "Bright submerged SSR rejection target";
                seabed.transform.SetPositionAndRotation(new Vector3(0f, -2.5f, 8f), Quaternion.identity);
                seabed.transform.localScale = new Vector3(50f, 1f, 50f);
                seabed.GetComponent<Renderer>().sharedMaterial = seabedMaterial;

                Reflection.Set(rig.Quality, "screenSpaceReflections", true);
                rig.Render();
                yield return null;
                rig.Render();
                yield return null;
                Color withSsr = ReadLowerWaterAverage(rig.Target);

                Reflection.Set(rig.Quality, "screenSpaceReflections", false);
                rig.Render();
                yield return null;
                Color withoutSsr = ReadLowerWaterAverage(rig.Target);

                AssertColorNear(withSsr, withoutSsr, 0.08f,
                    "Submerged terrain leaked into the SSR result.");
            }
            finally
            {
                UnityEngine.Object.Destroy(seabedMaterial);
                UnityEngine.Object.Destroy(seabed);
                if (rig != null)
                    rig.Dispose();
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator AboveWaterGeometry_ProducesOnlyALocalizedReflection()
        {
            WaterTestRig rig = null;
            GameObject reflector = null;
            Material reflectorMaterial = null;
            try
            {
                rig = new WaterTestRig("localized-object-reflection");
                reflector = GameObject.CreatePrimitive(PrimitiveType.Cube);
                reflectorMaterial = CreateUnlitMaterial(new Color(3f, 0.03f, 0.02f, 1f));
                reflector.name = "Bright above-water SSR target";
                reflector.transform.SetPositionAndRotation(new Vector3(0f, 2.8f, 5f), Quaternion.identity);
                reflector.transform.localScale = new Vector3(3f, 5f, 1.5f);
                reflector.GetComponent<Renderer>().sharedMaterial = reflectorMaterial;

                Reflection.Set(rig.Quality, "screenSpaceReflections", false);
                rig.Render();
                yield return null;
                Color[] withoutSsr = ReadPixels(rig.Target);

                Reflection.Set(rig.Quality, "screenSpaceReflections", true);
                rig.Render();
                yield return null;
                rig.Render();
                yield return null;
                Color[] withSsr = ReadPixels(rig.Target);

                int changed = 0;
                int lowerPixels = 0;
                for (int y = 0; y < rig.Target.height * 2 / 3; y++)
                {
                    for (int x = 0; x < rig.Target.width; x++)
                    {
                        int index = y * rig.Target.width + x;
                        float difference = MaxChannelDifference(withSsr[index], withoutSsr[index]);
                        if (difference > 0.025f)
                            changed++;
                        lowerPixels++;
                    }
                }

                Assert.That(changed, Is.GreaterThan(8), "No localized above-water SSR hit was reconstructed.");
                Assert.That(changed, Is.LessThan(lowerPixels / 3),
                    "The object reflection spread across too much of the water surface.");
            }
            finally
            {
                UnityEngine.Object.Destroy(reflectorMaterial);
                UnityEngine.Object.Destroy(reflector);
                if (rig != null)
                    rig.Dispose();
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator HalfResolutionSsr_BilateralResolvePreservesOpenWaterColor()
        {
            WaterTestRig rig = new("half-resolution-reconstruction");
            try
            {
                Reflection.Set(rig.Quality, "screenSpaceReflections", true);
                Reflection.Set(rig.Quality, "ssrResolutionScale", 1f);
                rig.Render();
                yield return null;
                rig.Render();
                yield return null;
                Color fullResolution = ReadLowerWaterAverage(rig.Target);

                Reflection.Set(rig.Quality, "ssrResolutionScale", 0.5f);
                rig.Render();
                yield return null;
                rig.Render();
                yield return null;
                Color halfResolution = ReadLowerWaterAverage(rig.Target);

                AssertColorNear(halfResolution, fullResolution, 0.06f,
                    "Half-resolution SSR reconstruction changed the open-water response.");
            }
            finally
            {
                rig.Dispose();
            }
            yield return null;
        }

        [UnityTest, Explicit("Run by the Phase 1 capture command with the Water 2 demo scene open.")]
        public IEnumerator DemoScene_CapturesPhase1AcceptanceViewsWhenRequested()
        {
            string outputDirectory = Environment.GetEnvironmentVariable("SOL_WATER_PHASE1_CAPTURE_DIR");
            if (string.IsNullOrWhiteSpace(outputDirectory))
                Assert.Ignore("SOL_WATER_PHASE1_CAPTURE_DIR was not provided.");

            GameObject cameraObject = GameObject.Find("PreviewCamera");
            if (cameraObject == null)
            {
                AsyncOperation load = SceneManager.LoadSceneAsync("Assets/Scenes/Sols_Water2_Demo.unity");
                Assert.That(load, Is.Not.Null,
                    "Add Assets/Scenes/Sols_Water2_Demo.unity to Editor Build Settings.");
                while (!load.isDone)
                    yield return null;
                yield return null;
                cameraObject = GameObject.Find("PreviewCamera");
            }
            Assert.That(cameraObject, Is.Not.Null, "Open Assets/Scenes/Sols_Water2_Demo.unity first.");
            Camera camera = cameraObject.GetComponent<Camera>();
            Assert.That(camera, Is.Not.Null);
            Type timeType = FindType("Sol.ToD.TimeOfDay");
            Component timeOfDay = Resources.FindObjectsOfTypeAll(timeType)
                .OfType<Component>()
                .FirstOrDefault(component => component.gameObject.scene.IsValid()
                    && component.gameObject.activeInHierarchy);
            Assert.That(timeOfDay, Is.Not.Null, "The Water 2 demo has no active TimeOfDay authority.");

            Directory.CreateDirectory(outputDirectory);
            Reflection.Invoke(timeOfDay, "SetPaused", new[] { typeof(bool) }, true);
            RenderTexture target = new(1064, 594, 24, RenderTextureFormat.ARGBHalf)
            {
                name = "Water 2 Phase 1 acceptance capture",
                antiAliasing = 1,
            };
            Assert.That(target.Create(), Is.True);
            RenderPipeline.StandardRequest request = new()
            {
                destination = target,
                mipLevel = 0,
                slice = 0,
                face = CubemapFace.Unknown,
            };
            try
            {
                Vector3 cameraPosition = camera.transform.position;
                Reflection.Invoke(timeOfDay, "SetClockHour",
                    new[] { typeof(float), typeof(UnityEngine.Object), typeof(string) },
                    8.25f, null, "Water 2 Phase 1 dawn capture");
                yield return null;
                yield return null;
                Vector4 sun = RenderSettings.skybox != null
                    && RenderSettings.skybox.HasProperty("_SunDirection")
                    ? RenderSettings.skybox.GetVector("_SunDirection")
                    : new Vector4(0f, 0f, 1f, 0f);
                float dawnYaw = Mathf.Atan2(sun.x, sun.z) * Mathf.Rad2Deg;
                camera.transform.SetPositionAndRotation(cameraPosition,
                    Quaternion.Euler(21.1f, dawnYaw, 0f));
                yield return Capture(camera, request, target, outputDirectory, "01-dawn-facing.png");

                camera.transform.rotation = Quaternion.Euler(21.1f, dawnYaw + 180f, 0f);
                yield return Capture(camera, request, target, outputDirectory, "02-dawn-opposed.png");

                Reflection.Invoke(timeOfDay, "SetClockHour",
                    new[] { typeof(float), typeof(UnityEngine.Object), typeof(string) },
                    12f, null, "Water 2 Phase 1 midday capture");
                yield return null;
                yield return null;
                camera.transform.rotation = Quaternion.Euler(21.1f, 90f, 0f);
                yield return Capture(camera, request, target, outputDirectory, "03-midday-x-axis.png");

                if (PositionCameraAtClosestBakedShoreline(camera))
                    yield return Capture(camera, request, target, outputDirectory, "05-shoreline-close.png");

                Reflection.Invoke(timeOfDay, "SetClockHour",
                    new[] { typeof(float), typeof(UnityEngine.Object), typeof(string) },
                    20.5f, null, "Water 2 Phase 1 night capture");
                yield return null;
                yield return null;
                camera.transform.rotation = Quaternion.Euler(21.1f, 45.823f, 0f);
                yield return Capture(camera, request, target, outputDirectory, "04-night-beach.png");
            }
            finally
            {
                target.Release();
                UnityEngine.Object.Destroy(target);
            }
        }

        static IEnumerator Capture(
            Camera camera,
            RenderPipeline.StandardRequest request,
            RenderTexture target,
            string outputDirectory,
            string fileName)
        {
            RenderPipeline.SubmitRenderRequest(camera, request);
            yield return null;
            yield return null;
            RenderTexture previous = RenderTexture.active;
            Texture2D readback = new(target.width, target.height, TextureFormat.RGBA32, false, false);
            try
            {
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                readback.Apply(false, false);
                File.WriteAllBytes(Path.Combine(outputDirectory, fileName), readback.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.Destroy(readback);
            }
        }

        static bool PositionCameraAtClosestBakedShoreline(Camera camera)
        {
            Type bodyType = FindType("Sol.Water.SolWaterBody");
            Component ocean = Resources.FindObjectsOfTypeAll(bodyType)
                .OfType<Component>()
                .FirstOrDefault(component => component.gameObject.scene.IsValid()
                    && component.gameObject.activeInHierarchy
                    && Reflection.Get<object>(component, "BodyType").ToString() == "Ocean");
            if (ocean == null)
                return false;
            ScriptableObject profile = Reflection.Get<ScriptableObject>(ocean, "Profile");
            Texture2D data = profile != null
                ? Reflection.Get<Texture2D>(profile, "shorelineData")
                : null;
            if (data == null || !data.isReadable)
                return false;

            Vector4 mapping = Reflection.Get<Vector4>(profile, "shorelineDataMapping");
            Vector2 cameraXZ = new(camera.transform.position.x, camera.transform.position.z);
            float bestDistance = float.PositiveInfinity;
            int bestX = -1;
            int bestY = -1;
            for (int y = 1; y < data.height - 1; y += 2)
            {
                for (int x = 1; x < data.width - 1; x += 2)
                {
                    Color value = data.GetPixel(x, y);
                    if (Mathf.Abs(value.g - 0.5f) > 0.025f)
                        continue;
                    Vector2 point = new(
                        mapping.x + ((float)x / (data.width - 1) - 0.5f) * mapping.z,
                        mapping.y + ((float)y / (data.height - 1) - 0.5f) * mapping.w);
                    float distance = (point - cameraXZ).sqrMagnitude;
                    if (distance >= bestDistance)
                        continue;
                    bestDistance = distance;
                    bestX = x;
                    bestY = y;
                }
            }
            if (bestX < 0)
                return false;

            Vector2 shore = new(
                mapping.x + ((float)bestX / (data.width - 1) - 0.5f) * mapping.z,
                mapping.y + ((float)bestY / (data.height - 1) - 0.5f) * mapping.w);
            float gradientX = data.GetPixel(bestX + 1, bestY).g
                - data.GetPixel(bestX - 1, bestY).g;
            float gradientY = data.GetPixel(bestX, bestY + 1).g
                - data.GetPixel(bestX, bestY - 1).g;
            Vector2 waterDirection = new(gradientX / Mathf.Max(0.001f, mapping.z),
                gradientY / Mathf.Max(0.001f, mapping.w));
            waterDirection = waterDirection.sqrMagnitude > 0.000001f
                ? waterDirection.normalized
                : Vector2.right;
            float waterLevel = Reflection.Get<float>(ocean, "SurfaceLevel");
            Vector3 position = new(shore.x + waterDirection.x * 18f,
                waterLevel + 6f, shore.y + waterDirection.y * 18f);
            Vector3 target = new(shore.x - waterDirection.x * 2f,
                waterLevel + 0.4f, shore.y - waterDirection.y * 2f);
            camera.transform.SetPositionAndRotation(position,
                Quaternion.LookRotation(target - position, Vector3.up));
            return true;
        }

        sealed class WaterTestRig : IDisposable
        {
            readonly Color previousSky;
            readonly Color previousEquator;
            readonly Color previousGround;
            readonly GameObject worldRoot;
            readonly GameObject cameraObject;
            readonly ScriptableObject profile;
            readonly RenderPipeline.StandardRequest request;

            public readonly ScriptableObject Quality;
            public readonly Camera Camera;
            public readonly RenderTexture Target;

            public WaterTestRig(string bodyName)
            {
                Type profileType = FindType("Sol.Water.SolWaterProfile");
                Type qualityType = FindType("Sol.Water.SolWaterQualityProfile");
                Type qualityEnum = FindType("Sol.Water.SolWaterQualityTier");
                Type idType = FindType("Sol.Water.SolWaterBodyId");
                profile = ScriptableObject.CreateInstance(profileType);
                Quality = ScriptableObject.CreateInstance(qualityType);
                Reflection.Set(profile, "skyReflectionStrength", 1f);
                Reflection.Set(profile, "waveSpeed", 0f);
                Reflection.Set(profile, "spectralStrength", 0f);
                Reflection.Set(Quality, "tier", Enum.Parse(qualityEnum, "Medium"));
                Reflection.Set(Quality, "screenSpaceReflections", true);
                Reflection.Set(Quality, "ssrResolutionScale", 0.5f);
                Reflection.Set(Quality, "planarReflections", false);

                previousSky = RenderSettings.ambientSkyColor;
                previousEquator = RenderSettings.ambientEquatorColor;
                previousGround = RenderSettings.ambientGroundColor;
                RenderSettings.ambientSkyColor = new Color(0.08f, 0.48f, 0.78f);
                RenderSettings.ambientEquatorColor = new Color(0.06f, 0.38f, 0.58f);
                RenderSettings.ambientGroundColor = new Color(0.025f, 0.16f, 0.2f);

                worldRoot = new GameObject($"Water 2 {bodyName} world");
                worldRoot.SetActive(false);
                Reflection.Add(worldRoot, "Sol.Environment.SolEnvironmentWorld");
                Component waterWorld = Reflection.Add(worldRoot, "Sol.Water.SolWaterWorld");
                Reflection.Set(waterWorld, "defaultProfile", profile);
                Reflection.Set(waterWorld, "qualityProfile", Quality);

                GameObject oceanObject = new($"Water 2 {bodyName} ocean");
                oceanObject.transform.SetParent(worldRoot.transform);
                Component ocean = Reflection.Add(oceanObject, "Sol.Water.SolWaterBody");
                Reflection.Set(ocean, "bodyId", Activator.CreateInstance(idType, bodyName));
                Reflection.Set(ocean, "profile", profile);
                Reflection.Set(ocean, "enablePlanarReflection", false);

                cameraObject = new GameObject($"Water 2 {bodyName} camera");
                Camera = cameraObject.AddComponent<Camera>();
                ConfigureUrpCamera(cameraObject);
                Camera.clearFlags = CameraClearFlags.SolidColor;
                Camera.backgroundColor = Color.black;
                Camera.transform.SetPositionAndRotation(new Vector3(0f, 5f, -12f),
                    Quaternion.Euler(16f, 0f, 0f));
                Camera.farClipPlane = 1000f;
                Target = new RenderTexture(192, 108, 24)
                {
                    name = $"Water 2 {bodyName} target",
                };
                Camera.targetTexture = Target;
                Assert.That(Target.Create(), Is.True);
                request = new RenderPipeline.StandardRequest
                {
                    destination = Target,
                    mipLevel = 0,
                    slice = 0,
                    face = CubemapFace.Unknown,
                };
                worldRoot.SetActive(true);
            }

            public void Render() => RenderPipeline.SubmitRenderRequest(Camera, request);

            public void ConfigureSpectrum(float strength, float choppiness)
            {
                Reflection.Set(profile, "spectralStrength", strength);
                Reflection.Set(profile, "spectralChoppiness", choppiness);
            }

            public void Dispose()
            {
                RenderSettings.ambientSkyColor = previousSky;
                RenderSettings.ambientEquatorColor = previousEquator;
                RenderSettings.ambientGroundColor = previousGround;
                Camera.targetTexture = null;
                Target.Release();
                UnityEngine.Object.Destroy(Target);
                UnityEngine.Object.Destroy(cameraObject);
                UnityEngine.Object.Destroy(worldRoot);
                UnityEngine.Object.Destroy(profile);
                UnityEngine.Object.Destroy(Quality);
            }
        }

        static Color ReadLowerWaterAverage(RenderTexture target)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D readback = new(32, 12, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(target.width / 2 - 16, 8, 32, 12), 0, 0, false);
                readback.Apply(false, false);
                Color[] pixels = readback.GetPixels();
                Color total = Color.clear;
                for (int i = 0; i < pixels.Length; i++)
                    total += pixels[i];
                return total / Mathf.Max(1, pixels.Length);
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.Destroy(readback);
            }
        }

        static Color ReadAverage(RenderTexture target, RectInt area)
        {
            Color[] pixels = ReadPixels(target, area);
            Color total = Color.clear;
            for (int i = 0; i < pixels.Length; i++)
                total += pixels[i];
            return total / Mathf.Max(1, pixels.Length);
        }

        static Color[] ReadPixels(RenderTexture target)
            => ReadPixels(target, new RectInt(0, 0, target.width, target.height));

        static Color[] ReadPixels(RenderTexture target, RectInt area)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D readback = new(area.width, area.height, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(area.x, area.y, area.width, area.height), 0, 0, false);
                readback.Apply(false, false);
                return readback.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.Destroy(readback);
            }
        }

        static Material CreateUnlitMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            Assert.That(shader, Is.Not.Null);
            Material material = new(shader);
            material.SetColor("_BaseColor", color);
            return material;
        }

        static void ConfigureUrpCamera(GameObject cameraObject)
        {
            Type cameraDataType = FindType(
                "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
            if (cameraObject.GetComponent(cameraDataType) == null)
                cameraObject.AddComponent(cameraDataType);
        }

        static void AssertTeal(Color color, string context)
        {
            Assert.That(color.b, Is.GreaterThan(color.r * 1.1f), $"{context} was not blue/teal: {color}");
            Assert.That(color.g, Is.GreaterThan(color.r * 1.04f), $"{context} was not green/teal: {color}");
        }

        static void AssertColorNear(Color actual, Color expected, float tolerance, string message)
        {
            Assert.That(MaxChannelDifference(actual, expected), Is.LessThan(tolerance),
                $"{message} Actual {actual}, expected {expected}.");
        }

        static float MaxChannelDifference(Color a, Color b)
            => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b));

        static float Luminance(Color color)
            => Vector3.Dot(new Vector3(color.r, color.g, color.b), new Vector3(0.2126f, 0.7152f, 0.0722f));

        static Type FindType(string fullName)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(candidate => candidate != null);
            return type ?? throw new TypeLoadException($"Could not find {fullName} in loaded Unity assemblies.");
        }
    }
}
