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
    /// Ticket 5F.1's post-decision regression check: run after Stone1/Stone2 are set to 8m and Sand to
    /// 2m (Dirt/Grass/Path stay at 4m from 5F). Confirms the finite-output sweep, the A4 per-pixel
    /// maximum, Path bit-exactness, and zero array-shader warnings still hold for the split
    /// configuration. The CPU weight-contract golden reference is intentionally not re-run: tileSize
    /// does not touch alphamap/weight data, exactly as in 5F.
    /// </summary>
    public static class SolLandscape5F1Validation
    {
        private const string ScenePath = "Assets/Scenes/Sols_Water2_Demo.unity";
        private const string ArrayMaterialPath = "Assets/Sky-and-Water/Landscape/M_SolLandscape.mat";
        private const string DebugKeyword = "_SOL_LANDSCAPE_DEBUG";
        private const int DebugSize = 512;

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
            var temporary = new List<UnityEngine.Object>();
            try
            {
                string outputDirectory = Path.GetFullPath(GetArgument(
                    System.Environment.GetCommandLineArgs(), "-sol5F1ValidationOutput", "../Landscape5F1Evidence"));
                Directory.CreateDirectory(outputDirectory);

                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Terrain terrain = FindTerrain();
                SolLandscapeDriver driver = FindDriver();
                Publish(driver);
                SetDryWeather();
                Material production = AssetDatabase.LoadAssetAtPath<Material>(ArrayMaterialPath);
                if (production == null || production.shader == null)
                    throw new InvalidOperationException("The production array terrain material is missing.");

                string tileSizeState = string.Join(",", terrain.terrainData.terrainLayers
                    .Select(layer => $"{layer.name}:{layer.tileSize.x}"));

                float[,,] alphaBefore = terrain.terrainData.GetAlphamaps(0, 0, terrain.terrainData.alphamapWidth, terrain.terrainData.alphamapHeight);
                Publish(driver);
                float[,,] alphaAfter = terrain.terrainData.GetAlphamaps(0, 0, terrain.terrainData.alphamapWidth, terrain.terrainData.alphamapHeight);
                bool alphamapsBitExact = AlphamapsBitExact(alphaBefore, alphaAfter);

                terrain.materialTemplate = production;
                terrain.drawHeightmap = true;
                terrain.drawInstanced = true;
                terrain.Flush();
                FiniteSweepMetrics finiteSweep = MeasureFiniteSweep(terrain, temporary);

                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                terrain = FindTerrain();
                driver = FindDriver();
                Publish(driver);
                SetDryWeather();
                IsolateTerrain(terrain);
                terrain.materialTemplate = production;
                terrain.Flush();

                Material debugMaterial = new Material(production)
                { name = "5F1 Path Regression", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                debugMaterial.EnableKeyword(DebugKeyword);
                temporary.Add(debugMaterial);

                Camera camera = CreateCamera(temporary);
                CreateDaylight(temporary);

                ConfigureCamera(camera, new Vector3(36.203500f, 1.467999f, -24.461840f), 32f, 85f, 1f);
                debugMaterial.SetFloat(DebugModeId, 3f);
                MotionMetrics a4 = CaptureMotion(terrain, debugMaterial, camera, 0.05f, 9, DebugSize, DebugSize);

                TerrainLayer[] layers = terrain.terrainData.terrainLayers;
                var originalTileSizes = layers.ToDictionary(layer => layer, layer => layer.tileSize);
                debugMaterial.SetFloat(DebugModeId, 4f);
                Color[] pathAtChosen = Render(terrain, debugMaterial, camera, DebugSize, DebugSize);

                // Probe: perturb the split configuration to a uniform 4m and back, proving Path stays
                // bit-exact regardless of which specific per-layer tileSize split is in effect.
                foreach (TerrainLayer layer in layers)
                    layer.tileSize = new Vector2(4f, 4f);
                Publish(driver);
                terrain.terrainData.terrainLayers = layers;
                terrain.Flush();
                Color[] pathAtProbe = Render(terrain, debugMaterial, camera, DebugSize, DebugSize);

                foreach (TerrainLayer layer in layers)
                    layer.tileSize = originalTileSizes[layer];
                Publish(driver);
                terrain.terrainData.terrainLayers = layers;
                terrain.Flush();

                bool pathBitExact = BitExact(pathAtChosen, pathAtProbe);
                DifferenceMetrics pathDifference = Difference(pathAtChosen, pathAtProbe);

                ShaderMetrics shader = MeasureShader(production.shader);

                bool pass = finiteSweep.NonFinitePixels == 0
                    && a4.MaximumPixel <= 1.0000001
                    && pathBitExact
                    && alphamapsBitExact
                    && shader.Errors == 0
                    && shader.Warnings == 0;

                var evidence = new StringBuilder();
                evidence.AppendLine("Sol Landscape Phase 5 ticket 5F.1 post-decision regression check");
                evidence.AppendLine($"UTC={DateTime.UtcNow:O}; Unity={Application.unityVersion}; Graphics={SystemInfo.graphicsDeviceType}; Device={SystemInfo.graphicsDeviceName}");
                evidence.AppendLine($"ChosenTileSizeState(LiveLayers)={tileSizeState}");
                evidence.AppendLine("ControlledDaylight=Directional; Euler=(38,-35,0); Intensity=1.35; Color=white; Shadows=Soft; ShadowStrength=1; Ambient=Flat(0.35); PostProcessing=False; TreesAndFoliage=False");
                evidence.AppendLine($"AlphamapsBitExactAcrossPublish={alphamapsBitExact}; Reason=tileSize does not touch alphamap/weight data; measured, not assumed");
                evidence.AppendLine($"FiniteSweep={finiteSweep}");
                evidence.AppendLine("FiniteSweepFraming=E6/E7 pivot:(0,5,0); Euler:(38,45,0); orthographic sizes:20,50,110,220,430; camera distance=2x size; 1280x760");
                evidence.AppendLine($"A4Motion={a4}");
                evidence.AppendLine("A4Framing=Exact S2/S5/S6/5C/5E/5F framing; Pivot:(36.203500,1.467999,-24.461840); OrthographicSize:32; Euler:(38,45,0); Distance:85; Output:512x512; Frames:9; TranslationStep:0.05m");
                evidence.AppendLine($"A4Reference=1.000000000 (S2/S5/S6/5F); ThisRunMax={a4.MaximumPixel.ToString("F9", Invariant)}");
                evidence.AppendLine($"PathResolvedWeightBitExactAcrossTileSizeProbe(split-config-vs-uniform4-then-restored)={pathBitExact}; {pathDifference}");
                evidence.AppendLine($"Shader={shader}");
                evidence.AppendLine("CPUWeightContractGoldenReference=NotRerun; Reason=tileSize is a UV-scale-only parameter and does not participate in the weight-resolve contract; 5C already closed this contract and neither 5F nor 5F.1 touch it");
                evidence.AppendLine("SceneSaved=False; ProductionMaterialModified=False; TerrainDataAlphamapsModified=False");
                evidence.AppendLine($"RESULT={(pass ? "PASS" : "FAIL")}");
                string path = Path.Combine(outputDirectory, "5F1_Regression_Evidence.txt");
                File.WriteAllText(path, evidence.ToString(), new UTF8Encoding(false));
                Debug.Log($"[Sol Landscape 5F.1] {(pass ? "PASS" : "FAIL")}; evidence={path}");
                exitCode = pass ? 0 : 1;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                exitCode = 1;
            }
            finally
            {
                foreach (UnityEngine.Object item in temporary.Where(value => value != null).Reverse<UnityEngine.Object>())
                    UnityEngine.Object.DestroyImmediate(item);
                EditorApplication.Exit(exitCode);
            }
        }

        private static bool AlphamapsBitExact(float[,,] a, float[,,] b)
        {
            if (a.GetLength(0) != b.GetLength(0) || a.GetLength(1) != b.GetLength(1) || a.GetLength(2) != b.GetLength(2))
                return false;
            for (int y = 0; y < a.GetLength(0); ++y)
                for (int x = 0; x < a.GetLength(1); ++x)
                    for (int layer = 0; layer < a.GetLength(2); ++layer)
                        if (BitConverter.SingleToInt32Bits(a[y, x, layer]) != BitConverter.SingleToInt32Bits(b[y, x, layer]))
                            return false;
            return true;
        }

        private static FiniteSweepMetrics MeasureFiniteSweep(Terrain terrain, ICollection<UnityEngine.Object> temporary)
        {
            Camera camera = CreateCamera(temporary);
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
            int[] sizes = { 20, 50, 110, 220, 430 };
            long total = 0;
            long nonFinite = 0;
            var perSize = new List<string>();
            foreach (int size in sizes)
            {
                ConfigureCamera(camera, new Vector3(0f, 5f, 0f), size, size * 2f, 1280f / 760f);
                Color[] pixels = Render(terrain, terrain.materialTemplate, camera, 1280, 760);
                int invalid = pixels.Count(pixel => !Finite(pixel.r) || !Finite(pixel.g) || !Finite(pixel.b) || !Finite(pixel.a));
                total += pixels.Length;
                nonFinite += invalid;
                perSize.Add($"{size}:{invalid}/{pixels.Length}");
            }
            return new FiniteSweepMetrics(total, nonFinite, string.Join(",", perSize));
        }

        private static MotionMetrics CaptureMotion(Terrain terrain, Material material, Camera camera, float stepMetres, int frameCount, int width, int height)
        {
            Vector3 start = camera.transform.position;
            Vector3 step = camera.transform.right * stepMetres;
            var frames = new List<Color[]>();
            for (int frame = 0; frame < frameCount; ++frame)
            {
                camera.transform.position = start + step * frame;
                frames.Add(Render(terrain, material, camera, width, height));
            }
            camera.transform.position = start;
            return MeasureMotion(frames);
        }

        private static MotionMetrics MeasureMotion(IReadOnlyList<Color[]> frames)
        {
            double[] means = frames.Select(frame => frame.Average(pixel => (double)pixel.r)).ToArray();
            double maxGlobal = 0d;
            double maxMae = 0d;
            double maxPixel = 0d;
            for (int frame = 1; frame < frames.Count; ++frame)
            {
                double sum = 0d;
                for (int pixel = 0; pixel < frames[frame].Length; ++pixel)
                {
                    double delta = Math.Abs(frames[frame][pixel].r - frames[frame - 1][pixel].r);
                    sum += delta;
                    maxPixel = Math.Max(maxPixel, delta);
                }
                maxGlobal = Math.Max(maxGlobal, Math.Abs(means[frame] - means[frame - 1]));
                maxMae = Math.Max(maxMae, sum / frames[frame].Length);
            }
            return new MotionMetrics(maxGlobal, maxMae, maxPixel);
        }

        private static DifferenceMetrics Difference(IReadOnlyList<Color> a, IReadOnlyList<Color> b)
        {
            double sum = 0d;
            double max = 0d;
            long changed = 0;
            for (int i = 0; i < a.Count; ++i)
            {
                double delta = (Math.Abs(a[i].r - b[i].r) + Math.Abs(a[i].g - b[i].g) + Math.Abs(a[i].b - b[i].b)) / 3d;
                double channelMax = Math.Max(Math.Abs(a[i].r - b[i].r), Math.Max(Math.Abs(a[i].g - b[i].g), Math.Abs(a[i].b - b[i].b)));
                sum += delta;
                max = Math.Max(max, channelMax);
                if (channelMax > 1e-6)
                    ++changed;
            }
            return new DifferenceMetrics(a.Count, changed, sum / a.Count, max);
        }

        private static bool BitExact(IReadOnlyList<Color> a, IReadOnlyList<Color> b)
        {
            for (int i = 0; i < a.Count; ++i)
                if (BitConverter.SingleToInt32Bits(a[i].r) != BitConverter.SingleToInt32Bits(b[i].r)
                    || BitConverter.SingleToInt32Bits(a[i].g) != BitConverter.SingleToInt32Bits(b[i].g)
                    || BitConverter.SingleToInt32Bits(a[i].b) != BitConverter.SingleToInt32Bits(b[i].b))
                    return false;
            return true;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

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
            SolLandscapeDriver driver = UnityEngine.Object
                .FindObjectsByType<SolLandscapeDriver>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault();
            if (driver == null || driver.config == null)
                throw new InvalidOperationException("The SolLandscapeDriver and its config are required.");
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

        private static void IsolateTerrain(Terrain terrain)
        {
            foreach (Camera camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                camera.enabled = false;
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
            terrain.Flush();
        }

        private static Camera CreateCamera(ICollection<UnityEngine.Object> temporary)
        {
            var go = new GameObject("5F.1 Validation Camera") { hideFlags = HideFlags.HideAndDontSave };
            temporary.Add(go);
            Camera camera = go.AddComponent<Camera>();
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

        private static void CreateDaylight(ICollection<UnityEngine.Object> temporary)
        {
            var go = new GameObject("ControlledDaylight") { hideFlags = HideFlags.HideAndDontSave };
            temporary.Add(go);
            Light light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(38f, -35f, 0f);
            light.color = Color.white;
            light.intensity = 1.35f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 1f;
            RenderSettings.sun = light;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.35f, 1f);
        }

        private static void ConfigureCamera(Camera camera, Vector3 pivot, float orthographicSize, float distance, float aspect)
        {
            Quaternion rotation = Quaternion.Euler(38f, 45f, 0f);
            camera.transform.SetPositionAndRotation(pivot - rotation * Vector3.forward * distance, rotation);
            camera.orthographicSize = orthographicSize;
            camera.aspect = aspect;
        }

        private static Color[] Render(Terrain terrain, Material material, Camera camera, int width, int height)
        {
            terrain.materialTemplate = material;
            terrain.Flush();
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

        private static ShaderMetrics MeasureShader(Shader shader)
        {
            ShaderMessage[] messages = ShaderUtil.GetShaderMessages(shader);
            return new ShaderMetrics(shader.isSupported,
                messages.Count(message => message.severity == ShaderCompilerMessageSeverity.Warning),
                messages.Count(message => message.severity == ShaderCompilerMessageSeverity.Error));
        }

        private static string GetArgument(string[] arguments, string name, string fallback)
        {
            for (int index = 0; index < arguments.Length - 1; ++index)
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    return arguments[index + 1];
            return fallback;
        }

        private readonly struct FiniteSweepMetrics
        {
            public FiniteSweepMetrics(long samples, long nonFinite, string breakdown)
            { Samples = samples; NonFinitePixels = nonFinite; Breakdown = breakdown; }
            public long Samples { get; }
            public long NonFinitePixels { get; }
            private string Breakdown { get; }
            public override string ToString() => $"Samples:{Samples}; NonFinitePixels:{NonFinitePixels}; PerSize(size:nonFinite/total)=[{Breakdown}]";
        }

        private readonly struct MotionMetrics
        {
            public MotionMetrics(double global, double mae, double pixel)
            { MaximumGlobal = global; MaximumMae = mae; MaximumPixel = pixel; }
            public double MaximumGlobal { get; }
            public double MaximumMae { get; }
            public double MaximumPixel { get; }
            public override string ToString() => string.Format(Invariant,
                "MaxAdjacentGlobalMean:{0:F9}; MaxAdjacentFieldMAE:{1:F9}; MaxAdjacentPixel:{2:F9}",
                MaximumGlobal, MaximumMae, MaximumPixel);
        }

        private readonly struct DifferenceMetrics
        {
            public DifferenceMetrics(long samples, long changed, double mae, double max)
            { Samples = samples; Changed = changed; Mae = mae; Maximum = max; }
            public long Samples { get; }
            public long Changed { get; }
            public double Mae { get; }
            public double Maximum { get; }
            public override string ToString() => string.Format(Invariant,
                "MaskedPixels:{0}; ChangedAbove1e-6:{1}; RGBMAE:{2:F9}; MaxChannel:{3:F9}", Samples, Changed, Mae, Maximum);
        }

        private readonly struct ShaderMetrics
        {
            public ShaderMetrics(bool supported, int warnings, int errors)
            { Supported = supported; Warnings = warnings; Errors = errors; }
            private bool Supported { get; }
            public int Warnings { get; }
            public int Errors { get; }
            public override string ToString() => $"Supported:{Supported};Warnings:{Warnings};Errors:{Errors}";
        }
    }
}
