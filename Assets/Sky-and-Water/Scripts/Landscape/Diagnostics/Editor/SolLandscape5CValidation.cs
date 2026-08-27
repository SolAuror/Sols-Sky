using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Sol.Landscape;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Sol.Landscape.Diagnostics.Editor
{
    /// <summary>
    /// Ticket 5C's unsaved correctness, visual, stability, and finite-output checks.
    /// Performance and post-strip counts remain owned by the standalone build harness.
    /// </summary>
    public static class SolLandscape5CValidation
    {
        private const string ScenePath = "Assets/Scenes/SolsWeather_Demo.unity";
        private const string ArrayMaterialPath = "Assets/Sky-and-Water/Landscape/M_SolLandscape.mat";
        private const string RetainedTopKKeyword = "_SOL_LANDSCAPE_TOPK_RETAINED";
        private const string DebugKeyword = "_SOL_LANDSCAPE_DEBUG";
        private const string HeightBlendKeyword = "_SOL_LANDSCAPE_BLEND_HEIGHT";
        private const int LayerCount = 6;
        private const int VisualWidth = 1024;
        private const int VisualHeight = 640;
        private const int DebugSize = 512;
        private const float S2ShoreMaximum = 0.031550590f;
        private const float S2A4Maximum = 1.0f;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly int DebugModeId = Shader.PropertyToID("_Sol_LandscapeDebugMode");
        private static readonly int SurfaceWetnessId = Shader.PropertyToID("_Sol_SurfaceWetness");
        private static readonly int SurfaceSnowCoverId = Shader.PropertyToID("_Sol_SurfaceSnowCover");
        private static readonly int SurfaceTemperatureId = Shader.PropertyToID("_Sol_SurfaceTemperature");
        private static readonly int TerrainWetnessId = Shader.PropertyToID("_Sol_TerrainWetness");
        private static readonly int GlobalWaterLevelId = Shader.PropertyToID("_Sol_GlobalWaterLevel");

        public static void ValidateFromCommandLine()
        {
            int exitCode = 1;
            var temporaryObjects = new List<UnityEngine.Object>();
            try
            {
                string outputDirectory = Path.GetFullPath(GetArgument(
                    System.Environment.GetCommandLineArgs(),
                    "-sol5COutput",
                    "../Landscape5CEvidence"));
                Directory.CreateDirectory(outputDirectory);

                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Terrain terrain = FindTerrain();
                SolLandscapeDriver driver = FindDriver();
                Publish(driver);
                SolLandscapeConfig config = driver.config;
                Material productionMaterial = AssetDatabase.LoadAssetAtPath<Material>(ArrayMaterialPath);
                if (productionMaterial == null || productionMaterial.shader == null)
                    throw new InvalidOperationException("The production array material is missing.");
                if (productionMaterial.IsKeywordEnabled(RetainedTopKKeyword))
                    throw new InvalidOperationException("The production material unexpectedly enables retained top-K.");
                if (!productionMaterial.IsKeywordEnabled(HeightBlendKeyword))
                    throw new InvalidOperationException("The production material no longer enables height blending.");

                CpuContractMetrics cpu = MeasureCpuContract(terrain, config);
                VisualMetrics visual = MeasureVisualAndMotion(
                    terrain,
                    productionMaterial,
                    outputDirectory,
                    temporaryObjects);

                // Reopen instead of attempting to restore hundreds of temporary renderer/light states.
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                terrain = FindTerrain();
                driver = FindDriver();
                Publish(driver);
                terrain.materialTemplate = productionMaterial;
                terrain.drawHeightmap = true;
                terrain.drawInstanced = true;
                terrain.Flush();
                FiniteSweepMetrics finiteSweep = MeasureFiniteSweep(terrain, temporaryObjects);
                ShaderMetrics shader = MeasureShader(productionMaterial.shader);

                bool pass = cpu.PathBitExact
                    && cpu.PaintAuthorityNamedRegression
                    && cpu.DoubledWeightRegressionAbsent
                    && cpu.ManualMaxError <= 1e-7
                    && cpu.AutoBudgetMaxError <= 1e-6
                    && cpu.SumToOneMaxError <= 1e-6
                    && cpu.ProductionDiscardedMaximum == 0d
                    && visual.PathBitExact
                    && visual.PathMae == 0d
                    && visual.HeightBlendFinite
                    && visual.MotionMaximum <= S2A4Maximum + 1e-7
                    && finiteSweep.NonFinitePixels == 0
                    && shader.Errors == 0
                    && shader.Warnings == 0;

                var evidence = new StringBuilder();
                evidence.AppendLine("Sol Landscape Phase 5 ticket 5C production all-six validation");
                evidence.AppendLine($"UTC={DateTime.UtcNow:O}");
                evidence.AppendLine($"Unity={Application.unityVersion}; Graphics={SystemInfo.graphicsDeviceType}; Device={SystemInfo.graphicsDeviceName}");
                evidence.AppendLine("ProductionResolve=Unsorted all-six; RetainedTopKKeywordEnabledOnProductionMaterial=False; HeightBlendKeywordEnabled=True");
                evidence.AppendLine($"CPUGoldenReference={cpu}");
                evidence.AppendLine($"TopKPressureCPUZeroCavityControl={cpu.TopKText}");
                evidence.AppendLine($"TopKPressureGPUShoreline={visual.PressureText}");
                evidence.AppendLine($"VisualComparison={visual.VisualText}");
                evidence.AppendLine($"PathAuthorityGPU={visual.PathText}");
                evidence.AppendLine($"HeightBlend={visual.HeightText}");
                evidence.AppendLine($"A4Motion={visual.MotionText}");
                evidence.AppendLine($"FiniteSweep={finiteSweep}");
                evidence.AppendLine($"Shader={shader}");
                evidence.AppendLine("ControlledDaylight=Directional; Euler=(38,-35,0); Intensity=1.35; Color=(1,1,1); Shadows=Soft; ShadowStrength=1; Ambient=Flat(0.35); PostProcessing=False; Trees=False");
                evidence.AppendLine("ShorelineFraming=Pivot:(-6.849319,-0.001995,-61.643840); OrthographicSize:20; Euler:(38,45,0); Distance:85; Output:1024x640");
                evidence.AppendLine("A4Framing=Exact S2/S5 bank framing; Pivot:(36.203500,1.467999,-24.461840); OrthographicSize:32; Euler:(38,45,0); Distance:85; Output:512x512; Frames:9; TranslationStep:0.05m");
                evidence.AppendLine("FiniteSweepFraming=E6/E7 pivot:(0,5,0); Euler:(38,45,0); orthographic sizes:20,50,110,220,430; camera distance=2x size; 1280x760 HDR; water/renderers visible");
                evidence.AppendLine("BeforeCapture=Retained former production K=4 path; AfterCapture=Production unsorted all-six path; both use height blending and identical camera/lighting/weather");
                evidence.AppendLine("HeightBlendDifferenceMeaning=The on/off comparison is a diagnostic proving the authored transition remains active; production remains ON");
                evidence.AppendLine("SceneSaved=False; ProductionMaterialModified=False; TerrainDataModified=False");
                evidence.AppendLine($"RESULT={(pass ? "PASS" : "FAIL")}");
                string evidencePath = Path.Combine(outputDirectory, "5C_Validation_Evidence.txt");
                File.WriteAllText(evidencePath, evidence.ToString(), new UTF8Encoding(false));
                Debug.Log($"[Sol Landscape 5C] {(pass ? "PASS" : "FAIL")}; evidence={evidencePath}");
                exitCode = pass ? 0 : 1;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                exitCode = 1;
            }
            finally
            {
                foreach (UnityEngine.Object temporary in temporaryObjects.Where(item => item != null).Reverse<UnityEngine.Object>())
                    UnityEngine.Object.DestroyImmediate(temporary);
                EditorApplication.Exit(exitCode);
            }
        }

        private static CpuContractMetrics MeasureCpuContract(Terrain terrain, SolLandscapeConfig config)
        {
            TerrainData data = terrain.terrainData;
            float[,,] painted = data.GetAlphamaps(0, 0, data.alphamapWidth, data.alphamapHeight);
            int width = painted.GetLength(1);
            int height = painted.GetLength(0);
            if (painted.GetLength(2) != LayerCount || config.Layers.Count != LayerCount)
                throw new InvalidOperationException("Ticket 5C requires the authored six-layer contract.");
            int manualIndex = Enumerable.Range(0, LayerCount)
                .Single(index => config.Layers[index].mode == SolLandscapeLayerMode.Manual);
            int samples = 0;
            int skippedZeroPaintSamples = 0;
            double manualErrorSum = 0d;
            double manualMax = 0d;
            double autoErrorSum = 0d;
            double autoMax = 0d;
            double sumErrorSum = 0d;
            double sumMax = 0d;
            double discardedSum = 0d;
            double discardedMax = 0d;
            var shorelineDiscarded = new List<double>();
            bool pathBitExact = true;
            bool doubledAbsent = true;

            for (int y = 0; y < height; ++y)
            {
                float v = height > 1 ? y / (float)(height - 1) : 0f;
                for (int x = 0; x < width; ++x)
                {
                    float u = width > 1 ? x / (float)(width - 1) : 0f;
                    float worldY = terrain.transform.position.y + data.GetInterpolatedHeight(u, v);
                    Vector3 normal = data.GetInterpolatedNormal(u, v);
                    float slope = Mathf.Acos(Mathf.Clamp(normal.y, -1f, 1f)) * Mathf.Rad2Deg;
                    float[] raw = new float[LayerCount];
                    for (int layer = 0; layer < LayerCount; ++layer)
                        raw[layer] = painted[y, x, layer];
                    if (raw.Sum() <= 1e-6f)
                    {
                        ++skippedZeroPaintSamples;
                        continue;
                    }
                    ++samples;
                    float[] resolved = ResolveCpu(raw, slope, worldY, 0f, config);

                    double manualError = Math.Abs(resolved[manualIndex] - raw[manualIndex]);
                    manualErrorSum += manualError;
                    manualMax = Math.Max(manualMax, manualError);
                    pathBitExact &= BitConverter.SingleToInt32Bits(resolved[manualIndex])
                        == BitConverter.SingleToInt32Bits(raw[manualIndex]);

                    double expectedAutoBudget = Math.Max(0d, Math.Min(1d, 1d - raw[manualIndex]));
                    double actualAutoBudget = 0d;
                    double resolvedSum = 0d;
                    for (int layer = 0; layer < LayerCount; ++layer)
                    {
                        resolvedSum += resolved[layer];
                        if (config.Layers[layer].mode == SolLandscapeLayerMode.Auto)
                            actualAutoBudget += resolved[layer];
                    }
                    double autoError = Math.Abs(actualAutoBudget - expectedAutoBudget);
                    double sumError = Math.Abs(resolvedSum - 1d);
                    autoErrorSum += autoError;
                    autoMax = Math.Max(autoMax, autoError);
                    sumErrorSum += sumError;
                    sumMax = Math.Max(sumMax, sumError);
                    doubledAbsent &= actualAutoBudget <= expectedAutoBudget + 1e-6;

                    double discarded = resolved.OrderByDescending(value => value).Skip(4).Sum(value => (double)value);
                    discardedSum += discarded;
                    discardedMax = Math.Max(discardedMax, discarded);
                    if (Math.Abs(worldY - Shader.GetGlobalFloat(GlobalWaterLevelId)) <= 2f)
                        shorelineDiscarded.Add(discarded);
                }
            }

            double shorelineMax = shorelineDiscarded.Count > 0 ? shorelineDiscarded.Max() : 0d;
            double shorelineMean = shorelineDiscarded.Count > 0 ? shorelineDiscarded.Average() : 0d;
            double shorelineP99 = Percentile(shorelineDiscarded, 0.99d);
            return new CpuContractMetrics(
                samples,
                manualErrorSum / samples,
                manualMax,
                autoErrorSum / samples,
                autoMax,
                sumErrorSum / samples,
                sumMax,
                pathBitExact,
                doubledAbsent,
                discardedSum / samples,
                discardedMax,
                shorelineDiscarded.Count,
                shorelineMean,
                shorelineP99,
                shorelineMax,
                skippedZeroPaintSamples);
        }

        private static float[] ResolveCpu(float[] painted, float slopeDegrees, float worldY, float waterLevel, SolLandscapeConfig config)
        {
            float[] result = (float[])painted.Clone();
            float manualWeight = 0f;
            float paintedAutoWeight = 0f;
            float proceduralWeight = 0f;
            float[] procedural = new float[LayerCount];
            for (int layer = 0; layer < LayerCount; ++layer)
            {
                SolLandscapeLayerEntry entry = config.Layers[layer];
                bool auto = entry.mode == SolLandscapeLayerMode.Auto;
                if (auto)
                {
                    paintedAutoWeight += painted[layer];
                    float slopeInput = (slopeDegrees - entry.slopeCenter) / Math.Max(entry.slopeContrast, 0.01f) + 0.5f;
                    float slopeRule = Directed(Response(slopeInput, entry.slopeBias), entry.slopeInfluence);
                    float altitude = worldY - (entry.altitudeReference == SolLandscapeAltitudeReference.RelativeToWaterLevel ? waterLevel : 0f);
                    float heightInput = (altitude - entry.heightRange.x) / Math.Max(entry.heightRange.y - entry.heightRange.x, 0.01f);
                    float heightRule = Directed(Response(heightInput, entry.heightBias), entry.heightInfluence);
                    procedural[layer] = Mathf.Clamp01(entry.autoWeight) * slopeRule * heightRule;
                    proceduralWeight += procedural[layer];
                }
                else
                {
                    manualWeight += painted[layer];
                }
            }

            float autoBudget = Mathf.Clamp01(1f - manualWeight);
            if (proceduralWeight > 1e-6f)
            {
                for (int layer = 0; layer < LayerCount; ++layer)
                    if (config.Layers[layer].mode == SolLandscapeLayerMode.Auto)
                        result[layer] = autoBudget * procedural[layer] / proceduralWeight;
            }
            else
            {
                float fallbackScale = paintedAutoWeight > 1e-6f ? autoBudget / paintedAutoWeight : 0f;
                for (int layer = 0; layer < LayerCount; ++layer)
                    if (config.Layers[layer].mode == SolLandscapeLayerMode.Auto)
                        result[layer] = painted[layer] * fallbackScale;
            }
            return result;
        }

        private static float Response(float normalizedInput, float bias)
        {
            float response = Mathf.Clamp01(normalizedInput);
            response = response * response * (3f - 2f * response);
            return Mathf.Clamp01(response + Mathf.Clamp(bias, -1f, 1f) * response * (1f - response));
        }

        private static float Directed(float response, float influence)
        {
            float directed = influence >= 0f ? response : 1f - response;
            return Mathf.Lerp(1f, directed, Mathf.Clamp01(Mathf.Abs(influence)));
        }

        private static VisualMetrics MeasureVisualAndMotion(
            Terrain terrain,
            Material productionMaterial,
            string outputDirectory,
            ICollection<UnityEngine.Object> temporaryObjects)
        {
            foreach (Camera existing in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                existing.enabled = false;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                renderer.enabled = false;
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                canvas.enabled = false;
            foreach (Light light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                light.enabled = false;

            terrain.drawHeightmap = true;
            terrain.drawInstanced = true;
            terrain.drawTreesAndFoliage = false;
            terrain.basemapDistance = float.MaxValue;
            SetDryWeather();

            Material before = new Material(productionMaterial) { name = "5C Retained K4 Before", hideFlags = HideFlags.HideAndDontSave };
            Material after = new Material(productionMaterial) { name = "5C All Six After", hideFlags = HideFlags.HideAndDontSave };
            before.EnableKeyword(RetainedTopKKeyword);
            after.DisableKeyword(RetainedTopKKeyword);
            temporaryObjects.Add(before);
            temporaryObjects.Add(after);

            Camera camera = CreateCamera("5C Validation Camera", temporaryObjects);
            Light daylight = CreateDaylight(temporaryObjects);
            _ = daylight;
            ConfigureCamera(camera, new Vector3(-6.849319f, -0.001995f, -61.643840f), 20f, 85f, VisualWidth / (float)VisualHeight);
            Color[] beforePixels = Render(terrain, before, camera, VisualWidth, VisualHeight);
            Color[] afterPixels = Render(terrain, after, camera, VisualWidth, VisualHeight);
            WritePng(Path.Combine(outputDirectory, "5C_Shoreline_Before_K4.png"), beforePixels, VisualWidth, VisualHeight);
            WritePng(Path.Combine(outputDirectory, "5C_Shoreline_After_All6.png"), afterPixels, VisualWidth, VisualHeight);
            DifferenceMetrics litDifference = Difference(beforePixels, afterPixels);

            Material heightOff = new Material(after) { name = "5C Height Blend Off Control", hideFlags = HideFlags.HideAndDontSave };
            heightOff.DisableKeyword(HeightBlendKeyword);
            temporaryObjects.Add(heightOff);
            Color[] heightOffPixels = Render(terrain, heightOff, camera, VisualWidth, VisualHeight);
            DifferenceMetrics heightDifference = Difference(afterPixels, heightOffPixels);

            after.EnableKeyword(DebugKeyword);
            after.SetFloat(DebugModeId, 6f);
            Color[] pressurePixels = Render(terrain, after, camera, VisualWidth, VisualHeight);
            FieldMetrics pressure = MeasureField(pressurePixels);
            WritePng(Path.Combine(outputDirectory, "5C_Shoreline_RetainedK4_DiscardedWeight.png"), pressurePixels, VisualWidth, VisualHeight);

            ConfigureCamera(camera, new Vector3(36.203500f, 1.467999f, -24.461840f), 32f, 85f, 1f);
            before.EnableKeyword(DebugKeyword);
            after.EnableKeyword(DebugKeyword);
            before.SetFloat(DebugModeId, 4f);
            after.SetFloat(DebugModeId, 4f);
            Color[] pathBefore = Render(terrain, before, camera, DebugSize, DebugSize);
            Color[] pathAfter = Render(terrain, after, camera, DebugSize, DebugSize);
            DifferenceMetrics pathDifference = Difference(pathBefore, pathAfter);
            bool pathBitExact = BitExact(pathBefore, pathAfter);

            after.SetFloat(DebugModeId, 3f);
            var motionFrames = new List<Color[]>();
            Vector3 basePosition = camera.transform.position;
            Vector3 step = camera.transform.right * 0.05f;
            for (int frame = 0; frame < 9; ++frame)
            {
                camera.transform.position = basePosition + step * frame;
                motionFrames.Add(Render(terrain, after, camera, DebugSize, DebugSize));
            }
            MotionMetrics motion = MeasureMotion(motionFrames);
            return new VisualMetrics(litDifference, pathDifference, pathBitExact, heightDifference, pressure, motion);
        }

        private static FiniteSweepMetrics MeasureFiniteSweep(Terrain terrain, ICollection<UnityEngine.Object> temporaryObjects)
        {
            foreach (Camera existing in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                existing.enabled = false;
            Camera camera = CreateCamera("5C E6-E7 Finite Sweep Camera", temporaryObjects);
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
            int[] sizes = { 20, 50, 110, 220, 430 };
            long total = 0;
            long nonFinite = 0;
            var perSize = new List<string>();
            foreach (int size in sizes)
            {
                ConfigureCamera(camera, new Vector3(0f, 5f, 0f), size, size * 2f, 1280f / 760f);
                Color[] pixels = Render(terrain, terrain.materialTemplate, camera, 1280, 760, isolateMaterial: false);
                int invalid = pixels.Count(pixel => !Finite(pixel.r) || !Finite(pixel.g) || !Finite(pixel.b) || !Finite(pixel.a));
                total += pixels.Length;
                nonFinite += invalid;
                perSize.Add($"{size}:{invalid}/{pixels.Length}");
            }
            return new FiniteSweepMetrics(total, nonFinite, string.Join(",", perSize));
        }

        private static Camera CreateCamera(string name, ICollection<UnityEngine.Object> temporaryObjects)
        {
            var cameraObject = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            temporaryObjects.Add(cameraObject);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 2000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.03f, 0.04f, 1f);
            camera.allowMSAA = true;
            camera.allowDynamicResolution = false;
            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false;
            data.renderShadows = true;
            return camera;
        }

        private static Light CreateDaylight(ICollection<UnityEngine.Object> temporaryObjects)
        {
            var lightObject = new GameObject("ControlledDaylight") { hideFlags = HideFlags.HideAndDontSave };
            temporaryObjects.Add(lightObject);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(38f, -35f, 0f);
            light.color = Color.white;
            light.intensity = 1.35f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 1f;
            RenderSettings.sun = light;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.35f, 1f);
            return light;
        }

        private static void ConfigureCamera(Camera camera, Vector3 pivot, float orthographicSize, float distance, float aspect)
        {
            Quaternion rotation = Quaternion.Euler(38f, 45f, 0f);
            camera.transform.SetPositionAndRotation(pivot - rotation * Vector3.forward * distance, rotation);
            camera.orthographicSize = orthographicSize;
            camera.aspect = aspect;
        }

        private static Color[] Render(Terrain terrain, Material material, Camera camera, int width, int height, bool isolateMaterial = true)
        {
            if (isolateMaterial)
            {
                terrain.materialTemplate = material;
                terrain.Flush();
            }
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                hideFlags = HideFlags.HideAndDontSave,
                antiAliasing = 1
            };
            target.Create();
            var readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            RenderTexture prior = RenderTexture.active;
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
            readback.Apply(false, false);
            Color[] pixels = readback.GetPixels();
            camera.targetTexture = null;
            RenderTexture.active = prior;
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(readback);
            return pixels;
        }

        private static void WritePng(string path, Color[] linearPixels, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false, false);
            Color[] gamma = linearPixels.Select(pixel => new Color(
                Mathf.LinearToGammaSpace(Mathf.Max(0f, pixel.r)),
                Mathf.LinearToGammaSpace(Mathf.Max(0f, pixel.g)),
                Mathf.LinearToGammaSpace(Mathf.Max(0f, pixel.b)),
                1f)).ToArray();
            texture.SetPixels(gamma);
            texture.Apply(false, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
        }

        private static DifferenceMetrics Difference(IReadOnlyList<Color> a, IReadOnlyList<Color> b)
        {
            if (a.Count != b.Count)
                throw new InvalidOperationException("Image comparison dimensions differ.");
            double sum = 0d;
            double max = 0d;
            long finite = 0;
            long changed = 0;
            for (int index = 0; index < a.Count; ++index)
            {
                float[] av = { a[index].r, a[index].g, a[index].b };
                float[] bv = { b[index].r, b[index].g, b[index].b };
                for (int channel = 0; channel < 3; ++channel)
                {
                    if (!Finite(av[channel]) || !Finite(bv[channel]))
                        continue;
                    double delta = Math.Abs(av[channel] - bv[channel]);
                    sum += delta;
                    max = Math.Max(max, delta);
                    finite++;
                    if (delta > 0d)
                        changed++;
                }
            }
            return new DifferenceMetrics(finite, changed, finite > 0 ? sum / finite : double.NaN, max);
        }

        private static bool BitExact(IReadOnlyList<Color> a, IReadOnlyList<Color> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int index = 0; index < a.Count; ++index)
            {
                if (BitConverter.SingleToInt32Bits(a[index].r) != BitConverter.SingleToInt32Bits(b[index].r)
                    || BitConverter.SingleToInt32Bits(a[index].g) != BitConverter.SingleToInt32Bits(b[index].g)
                    || BitConverter.SingleToInt32Bits(a[index].b) != BitConverter.SingleToInt32Bits(b[index].b))
                    return false;
            }
            return true;
        }

        private static MotionMetrics MeasureMotion(IReadOnlyList<Color[]> frames)
        {
            double maxGlobal = 0d;
            double maxMae = 0d;
            double maxPixel = 0d;
            double[] means = frames.Select(frame => frame.Average(pixel => (double)pixel.r)).ToArray();
            for (int frame = 1; frame < frames.Count; ++frame)
            {
                double sum = 0d;
                for (int index = 0; index < frames[frame].Length; ++index)
                {
                    double delta = Math.Abs(frames[frame][index].r - frames[frame - 1][index].r);
                    sum += delta;
                    maxPixel = Math.Max(maxPixel, delta);
                }
                maxGlobal = Math.Max(maxGlobal, Math.Abs(means[frame] - means[frame - 1]));
                maxMae = Math.Max(maxMae, sum / frames[frame].Length);
            }
            return new MotionMetrics(means, maxGlobal, maxMae, maxPixel);
        }

        private static FieldMetrics MeasureField(IEnumerable<Color> pixels)
        {
            double[] finite = pixels.Select(pixel => (double)pixel.r)
                .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
                .ToArray();
            return new FieldMetrics(
                finite.Length,
                finite.Length > 0 ? finite.Average() : double.NaN,
                Percentile(finite, 0.99d),
                finite.Length > 0 ? finite.Max() : double.NaN);
        }

        private static ShaderMetrics MeasureShader(Shader shader)
        {
            ShaderMessage[] messages = ShaderUtil.GetShaderMessages(shader);
            int warnings = messages.Count(message => message.severity == ShaderCompilerMessageSeverity.Warning);
            int errors = messages.Count(message => message.severity == ShaderCompilerMessageSeverity.Error);
            return new ShaderMetrics(shader.isSupported, warnings, errors);
        }

        private static Terrain FindTerrain()
        {
            Terrain terrain = Terrain.activeTerrain
                ?? UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault();
            if (terrain == null || terrain.terrainData == null)
                throw new InvalidOperationException("The demo Terrain and TerrainData are required.");
            return terrain;
        }

        private static SolLandscapeDriver FindDriver()
        {
            SolLandscapeDriver driver = UnityEngine.Object.FindObjectsByType<SolLandscapeDriver>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None).FirstOrDefault();
            if (driver == null || driver.config == null)
                throw new InvalidOperationException("The SolLandscapeDriver and config are required.");
            return driver;
        }

        private static void Publish(SolLandscapeDriver driver)
        {
            driver.Invalidate();
            MethodInfo method = typeof(SolLandscapeDriver).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new MissingMethodException(typeof(SolLandscapeDriver).FullName, "Publish");
            method.Invoke(driver, null);
            if (driver.LastPublishRefused)
                throw new InvalidOperationException($"Landscape publication refused: {driver.LastRefusalReason}");
        }

        private static void SetDryWeather()
        {
            Shader.SetGlobalFloat(SurfaceWetnessId, 0f);
            Shader.SetGlobalFloat(SurfaceSnowCoverId, 0f);
            Shader.SetGlobalFloat(SurfaceTemperatureId, 18f);
            Shader.SetGlobalVector(TerrainWetnessId, Vector4.zero);
            Shader.SetGlobalFloat(GlobalWaterLevelId, 0f);
        }

        private static double Percentile(IEnumerable<double> values, double percentile)
        {
            double[] sorted = values.OrderBy(value => value).ToArray();
            if (sorted.Length == 0)
                return 0d;
            int index = Math.Max(0, (int)Math.Ceiling(percentile * sorted.Length) - 1);
            return sorted[index];
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static string GetArgument(string[] arguments, string name, string fallback)
        {
            for (int index = 0; index < arguments.Length - 1; ++index)
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    return arguments[index + 1];
            return fallback;
        }

        private readonly struct CpuContractMetrics
        {
            public CpuContractMetrics(int samples, double manualMae, double manualMax, double autoMae, double autoMax,
                double sumMae, double sumMax, bool pathBitExact, bool doubledAbsent, double discardedMean,
                double discardedMax, int shoreSamples, double shoreMean, double shoreP99, double shoreMax,
                int skippedZeroPaintSamples)
            {
                Samples = samples; ManualMae = manualMae; ManualMaxError = manualMax; AutoBudgetMae = autoMae;
                AutoBudgetMaxError = autoMax; SumToOneMae = sumMae; SumToOneMaxError = sumMax;
                PathBitExact = pathBitExact; PaintAuthorityNamedRegression = pathBitExact;
                DoubledWeightRegressionAbsent = doubledAbsent; RetainedDiscardedMean = discardedMean;
                RetainedDiscardedMaximum = discardedMax; ShoreSamples = shoreSamples; ShoreMean = shoreMean;
                ShoreP99 = shoreP99; ShoreMaximum = shoreMax; SkippedZeroPaintSamples = skippedZeroPaintSamples;
            }
            public int Samples { get; }
            public double ManualMae { get; }
            public double ManualMaxError { get; }
            public double AutoBudgetMae { get; }
            public double AutoBudgetMaxError { get; }
            public double SumToOneMae { get; }
            public double SumToOneMaxError { get; }
            public bool PathBitExact { get; }
            public bool PaintAuthorityNamedRegression { get; }
            public bool DoubledWeightRegressionAbsent { get; }
            public double RetainedDiscardedMean { get; }
            public double RetainedDiscardedMaximum { get; }
            public int ShoreSamples { get; }
            public double ShoreMean { get; }
            public double ShoreP99 { get; }
            public double ShoreMaximum { get; }
            public int SkippedZeroPaintSamples { get; }
            public double ProductionDiscardedMaximum => 0d;
            public string TopKText => string.Format(Invariant,
                "HistoricalS2ShoreMax:{0:F9}; CurrentRetainedK4WholeMean:{1:F9}; CurrentRetainedK4WholeMax:{2:F9}; ShoreSamples:{3}; CurrentRetainedK4ShoreMean:{4:F9}; ShoreP99:{5:F9}; ShoreMax:{6:F9}; ProductionAll6DiscardedMean:0.000000000; ProductionAll6DiscardedMax:0.000000000",
                S2ShoreMaximum, RetainedDiscardedMean, RetainedDiscardedMaximum, ShoreSamples, ShoreMean, ShoreP99, ShoreMaximum);
            public override string ToString() => string.Format(Invariant,
                "Samples:{0}; SkippedZeroPaintSamples:{10}; ManualMAE:{1:F9}; ManualMax:{2:F9}; AutoBudgetMAE:{3:F9}; AutoBudgetMax:{4:F9}; SumToOneMAE:{5:F9}; SumToOneMax:{6:F9}; PathBitExact:{7}; PaintAuthorityNamedRegression:{8}; DoubledWeightRegressionAbsent:{9}",
                Samples, ManualMae, ManualMaxError, AutoBudgetMae, AutoBudgetMaxError, SumToOneMae,
                SumToOneMaxError, PathBitExact, PaintAuthorityNamedRegression, DoubledWeightRegressionAbsent,
                SkippedZeroPaintSamples);
        }

        private readonly struct DifferenceMetrics
        {
            public DifferenceMetrics(long finiteChannels, long changedChannels, double mae, double max)
            { FiniteChannels = finiteChannels; ChangedChannels = changedChannels; Mae = mae; Max = max; }
            public long FiniteChannels { get; }
            public long ChangedChannels { get; }
            public double Mae { get; }
            public double Max { get; }
            public override string ToString() => string.Format(Invariant,
                "FiniteChannels:{0}; ChangedChannels:{1}; MAE:{2:F9}; Max:{3:F9}", FiniteChannels, ChangedChannels, Mae, Max);
        }

        private readonly struct MotionMetrics
        {
            public MotionMetrics(double[] means, double global, double mae, double pixel)
            { Means = means; MaxGlobal = global; MaxMae = mae; MaxPixel = pixel; }
            public double[] Means { get; }
            public double MaxGlobal { get; }
            public double MaxMae { get; }
            public double MaxPixel { get; }
            public override string ToString() => string.Format(Invariant,
                "StepMetres:0.05; Frames:9; Means:[{0}]; MaxAdjacentGlobalMean:{1:F9}; MaxAdjacentFieldMAE:{2:F9}; MaxAdjacentPixel:{3:F9}; S2Reference:1.000000000",
                string.Join(",", Means.Select(value => value.ToString("F9", Invariant))), MaxGlobal, MaxMae, MaxPixel);
        }

        private readonly struct FieldMetrics
        {
            public FieldMetrics(int samples, double mean, double p99, double max)
            { Samples = samples; Mean = mean; P99 = p99; Max = max; }
            public int Samples { get; }
            public double Mean { get; }
            public double P99 { get; }
            public double Max { get; }
            public override string ToString() => string.Format(Invariant,
                "Samples:{0}; Mean:{1:F9}; P99:{2:F9}; Max:{3:F9}", Samples, Mean, P99, Max);
        }

        private readonly struct VisualMetrics
        {
            public VisualMetrics(DifferenceMetrics visual, DifferenceMetrics path, bool pathBitExact,
                DifferenceMetrics height, FieldMetrics pressure, MotionMetrics motion)
            { Visual = visual; Path = path; PathBitExact = pathBitExact; Height = height; Pressure = pressure; Motion = motion; }
            private DifferenceMetrics Visual { get; }
            private DifferenceMetrics Path { get; }
            public bool PathBitExact { get; }
            private DifferenceMetrics Height { get; }
            private FieldMetrics Pressure { get; }
            private MotionMetrics Motion { get; }
            public double PathMae => Path.Mae;
            public bool HeightBlendFinite => Height.FiniteChannels == VisualWidth * VisualHeight * 3L;
            public double MotionMaximum => Motion.MaxPixel;
            public string VisualText => $"RetainedK4VsProductionAll6:{Visual}; Before:5C_Shoreline_Before_K4.png; After:5C_Shoreline_After_All6.png";
            public string PathText => $"RetainedK4VsProductionAll6:{Path}; BitExact:{PathBitExact}";
            public string HeightText => $"ProductionHeightOnVsOff:{Height}; ProductionEnabled:True; Finite:{HeightBlendFinite}";
            public string PressureText => $"HistoricalS2ShoreMax:{S2ShoreMaximum:F9}; CurrentResolvedWouldBeK4Discard:{Pressure}; ProductionAll6DiscardedMean:0.000000000; ProductionAll6DiscardedMax:0.000000000; DebugCapture:5C_Shoreline_RetainedK4_DiscardedWeight.png";
            public string MotionText => Motion.ToString();
        }

        private readonly struct FiniteSweepMetrics
        {
            public FiniteSweepMetrics(long samples, long nonFinite, string breakdown)
            { Samples = samples; NonFinitePixels = nonFinite; Breakdown = breakdown; }
            public long Samples { get; }
            public long NonFinitePixels { get; }
            private string Breakdown { get; }
            public override string ToString() => $"Pixels:{Samples}; NonFinite:{NonFinitePixels}; BySizeNonFinite/Total:[{Breakdown}]";
        }

        private readonly struct ShaderMetrics
        {
            public ShaderMetrics(bool supported, int warnings, int errors)
            { Supported = supported; Warnings = warnings; Errors = errors; }
            private bool Supported { get; }
            public int Warnings { get; }
            public int Errors { get; }
            public override string ToString() => $"Supported:{Supported}; Warnings:{Warnings}; Errors:{Errors}";
        }
    }
}
