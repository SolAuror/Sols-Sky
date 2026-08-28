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
    /// Ticket 5E's editor-only anisotropy, flat-slice, control-map shimmer, and A4 checks.
    /// All texture/material/quality changes are transient and restored before exit.
    /// </summary>
    public static class SolLandscape5EValidation
    {
        private const string ScenePath = "Assets/Scenes/Sols_Water2_Demo.unity";
        private const string ArrayMaterialPath = "Assets/Sky-and-Water/Landscape/M_SolLandscape.mat";
        private const string FlatShaderPath = "Assets/Sky-and-Water/Scripts/Landscape/Diagnostics/Editor/SolLandscape5EFlatSlice.shader";
        private const int Width = 1024;
        private const int Height = 640;
        private const int A4Size = 512;
        private const string DebugKeyword = "_SOL_LANDSCAPE_DEBUG";

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly int FlatSlicesId = Shader.PropertyToID("_Sol5EFlatSlices");
        private static readonly int InjectBleedId = Shader.PropertyToID("_Sol5EInjectBleed");
        private static readonly int ForcedSliceId = Shader.PropertyToID("_Sol5EForcedSlice");
        private static readonly int DebugModeId = Shader.PropertyToID("_Sol_LandscapeDebugMode");
        private static readonly int SurfaceWetnessId = Shader.PropertyToID("_Sol_SurfaceWetness");
        private static readonly int SurfaceSnowCoverId = Shader.PropertyToID("_Sol_SurfaceSnowCover");
        private static readonly int SurfaceTemperatureId = Shader.PropertyToID("_Sol_SurfaceTemperature");
        private static readonly int TerrainWetnessId = Shader.PropertyToID("_Sol_TerrainWetness");
        private static readonly int GlobalWaterLevelId = Shader.PropertyToID("_Sol_GlobalWaterLevel");

        private static readonly Color[] Palette =
        {
            new Color(1f, 0f, 0f, 1f),
            new Color(0f, 1f, 0f, 1f),
            new Color(0f, 0f, 1f, 1f),
            new Color(1f, 1f, 0f, 1f),
            new Color(1f, 0f, 1f, 1f),
            new Color(0f, 1f, 1f, 1f)
        };

        public static void ValidateFromCommandLine()
        {
            int exitCode = 1;
            AnisotropicFiltering originalAnisotropy = QualitySettings.anisotropicFiltering;
            var temporary = new List<UnityEngine.Object>();
            try
            {
                string outputDirectory = Path.GetFullPath(GetArgument(
                    System.Environment.GetCommandLineArgs(),
                    "-sol5EOutput",
                    "../Landscape5EEvidence"));
                Directory.CreateDirectory(outputDirectory);

                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Terrain terrain = Terrain.activeTerrain
                    ?? UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault();
                SolLandscapeDriver driver = UnityEngine.Object.FindObjectsByType<SolLandscapeDriver>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None).FirstOrDefault();
                Material production = AssetDatabase.LoadAssetAtPath<Material>(ArrayMaterialPath);
                Shader flatShader = AssetDatabase.LoadAssetAtPath<Shader>(FlatShaderPath);
                if (terrain == null || terrain.terrainData == null || driver == null || driver.config == null
                    || production == null || flatShader == null)
                    throw new InvalidOperationException("The demo terrain, driver/config, production material, and 5E shader are required.");
                Publish(driver);
                SetDryWeather();

                string textureState = DescribeTextureState(terrain, driver.config);
                IsolateTerrain(terrain);
                Camera camera = CreateCamera(temporary);
                CreateDaylight(temporary);
                ConfigureCamera(camera, new Vector3(36.203500f, 1.467999f, -24.461840f),
                    Quaternion.Euler(18f, 45f, 0f), 70f, 260f, Width / (float)Height);

                Texture2DArray flatArray = CreateFlatArray();
                temporary.Add(flatArray);
                Material flatMaterial = new Material(flatShader)
                {
                    name = "5E Flat Slice Measurement",
                    hideFlags = HideFlags.HideAndDontSave,
                    enableInstancing = true
                };
                flatMaterial.SetTexture(FlatSlicesId, flatArray);
                flatMaterial.SetFloat(ForcedSliceId, -1f);
                temporary.Add(flatMaterial);

                QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
                flatMaterial.SetFloat(InjectBleedId, 0f);
                Color[] flatOn = Render(terrain, flatMaterial, camera, Width, Height);
                FlatMetrics flatOnMetrics = MeasureFlat(flatOn);
                flatMaterial.SetFloat(InjectBleedId, 0.125f);
                Color[] injected = Render(terrain, flatMaterial, camera, Width, Height);
                FlatMetrics injectedMetrics = MeasureFlat(injected);

                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
                flatMaterial.SetFloat(InjectBleedId, 0f);
                Color[] flatOff = Render(terrain, flatMaterial, camera, Width, Height);
                FlatMetrics flatOffMetrics = MeasureFlat(flatOff);
                DifferenceMetrics flatOnOff = DifferenceMasked(flatOn, flatOff, requireEncodedWinner: true);

                var everySliceOn = new FlatMetrics[Palette.Length];
                var everySliceOff = new FlatMetrics[Palette.Length];
                var everySliceInjected = new FlatMetrics[Palette.Length];
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
                for (int slice = 0; slice < Palette.Length; ++slice)
                {
                    flatMaterial.SetFloat(ForcedSliceId, slice);
                    flatMaterial.SetFloat(InjectBleedId, 0f);
                    everySliceOn[slice] = MeasureFlat(Render(terrain, flatMaterial, camera, Width, Height));
                    flatMaterial.SetFloat(InjectBleedId, 0.125f);
                    everySliceInjected[slice] = MeasureFlat(Render(terrain, flatMaterial, camera, Width, Height));
                }
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
                flatMaterial.SetFloat(InjectBleedId, 0f);
                for (int slice = 0; slice < Palette.Length; ++slice)
                {
                    flatMaterial.SetFloat(ForcedSliceId, slice);
                    everySliceOff[slice] = MeasureFlat(Render(terrain, flatMaterial, camera, Width, Height));
                }
                flatMaterial.SetFloat(ForcedSliceId, -1f);
                WritePng(Path.Combine(outputDirectory, "5E_FlatSlices_AnisoOn.png"), flatOn, Width, Height);
                WritePng(Path.Combine(outputDirectory, "5E_FlatSlices_AnisoOff.png"), flatOff, Width, Height);
                WritePng(Path.Combine(outputDirectory, "5E_FlatSlices_InjectedBleed_Control.png"), injected, Width, Height);

                Material litMaterial = new Material(production)
                {
                    name = "5E Production Lit A-B",
                    hideFlags = HideFlags.HideAndDontSave,
                    enableInstancing = true
                };
                temporary.Add(litMaterial);
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
                Color[] litOn = Render(terrain, litMaterial, camera, Width, Height);
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
                Color[] litOff = Render(terrain, litMaterial, camera, Width, Height);
                DifferenceMetrics litOnOff = DifferenceMasked(litOn, litOff, requireEncodedWinner: false);
                WritePng(Path.Combine(outputDirectory, "5E_GrazingDistance_AnisoOn.png"), litOn, Width, Height);
                WritePng(Path.Combine(outputDirectory, "5E_GrazingDistance_AnisoOff.png"), litOff, Width, Height);

                Material debugMaterial = new Material(production)
                {
                    name = "5E Resolved Weight Motion",
                    hideFlags = HideFlags.HideAndDontSave,
                    enableInstancing = true
                };
                debugMaterial.EnableKeyword(DebugKeyword);
                debugMaterial.SetFloat(DebugModeId, 3f);
                temporary.Add(debugMaterial);

                QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
                MotionMetrics distantOn = CaptureMotion(terrain, debugMaterial, camera, 0.05f, 9, Width, Height);
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
                MotionMetrics distantOff = CaptureMotion(terrain, debugMaterial, camera, 0.05f, 9, Width, Height);

                ConfigureCamera(camera, new Vector3(36.203500f, 1.467999f, -24.461840f),
                    Quaternion.Euler(38f, 45f, 0f), 32f, 85f, 1f);
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
                MotionMetrics a4On = CaptureMotion(terrain, debugMaterial, camera, 0.05f, 9, A4Size, A4Size);
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
                MotionMetrics a4Off = CaptureMotion(terrain, debugMaterial, camera, 0.05f, 9, A4Size, A4Size);

                QualitySettings.anisotropicFiltering = originalAnisotropy;
                ShaderMetrics productionShader = MeasureShader(production.shader);
                ShaderMetrics diagnosticShader = MeasureShader(flatShader);
                bool pass = flatOnMetrics.ContaminatedPixels == 0
                    && flatOffMetrics.ContaminatedPixels == 0
                    && flatOnMetrics.MaximumError <= 1e-6
                    && flatOffMetrics.MaximumError <= 1e-6
                    && injectedMetrics.ContaminatedPixels > 0
                    && injectedMetrics.MaximumError > 0.01
                    && everySliceOn.All(metrics => metrics.FullWeightPixels > 0
                        && metrics.ContaminatedPixels == 0 && metrics.MaximumError <= 1e-6)
                    && everySliceOff.All(metrics => metrics.FullWeightPixels > 0
                        && metrics.ContaminatedPixels == 0 && metrics.MaximumError <= 1e-6)
                    && everySliceInjected.All(metrics => metrics.FullWeightPixels > 0
                        && metrics.ContaminatedPixels == metrics.FullWeightPixels && metrics.MaximumError > 0.01)
                    && flatOnOff.Maximum <= 1e-6
                    && a4Off.MaximumPixel <= 1.0000001
                    && productionShader.Warnings == 0
                    && productionShader.Errors == 0
                    && diagnosticShader.Errors == 0;

                var evidence = new StringBuilder();
                evidence.AppendLine("Sol Landscape Phase 5 ticket 5E anisotropy and slice-edge validation");
                evidence.AppendLine($"UTC={DateTime.UtcNow:O}; Unity={Application.unityVersion}; Graphics={SystemInfo.graphicsDeviceType}; Device={SystemInfo.graphicsDeviceName}");
                evidence.AppendLine($"QualitySerializedExpectation=anisotropicTextures:2 (ForceEnable); RuntimeOriginal={originalAnisotropy}; RuntimeRestored={QualitySettings.anisotropicFiltering}; QualitySettingsAssetEdited=False");
                evidence.AppendLine($"TextureState={textureState}");
                evidence.AppendLine("FlatSlicePalette=0:red,1:green,2:blue,3:yellow,4:magenta,5:cyan; Texture2DArray RGBA32 linear, 64x64, seven mips, Trilinear, Repeat, anisoLevel16; output alpha encodes sampled slice+1; only resolved weight-one pixels are judged");
                evidence.AppendLine("GrazingDistanceFraming=Pivot:(36.203500,1.467999,-24.461840); CameraEuler:(18,45,0); OrthographicSize:70; Distance:260; 1024x640; terrain only; dry weather");
                evidence.AppendLine("ControlledDaylight=Directional; Euler:(38,-35,0); Intensity:1.35; Color:white; Shadows:Soft; ShadowStrength:1; Ambient:Flat(0.35); PostProcessing:False; TreesAndFoliage:False");
                evidence.AppendLine($"FlatSliceForceOn={flatOnMetrics}");
                evidence.AppendLine($"FlatSliceForceOff={flatOffMetrics}");
                evidence.AppendLine($"FlatSliceOnVsOff={flatOnOff}");
                evidence.AppendLine($"InjectedNeighbourPositiveControl={injectedMetrics}; Injection:0.125; DetectorResponded:{injectedMetrics.ContaminatedPixels > 0}");
                evidence.AppendLine($"EverySliceForceOn={DescribePerSlice(everySliceOn)}");
                evidence.AppendLine($"EverySliceForceOff={DescribePerSlice(everySliceOff)}");
                evidence.AppendLine($"EverySliceInjectedNeighbourPositiveControl={DescribePerSlice(everySliceInjected)}; Injection:0.125");
                evidence.AppendLine($"ProductionLitOnVsOff={litOnOff}");
                evidence.AppendLine($"ResolvedWeightDistanceMotionForceOn={distantOn}");
                evidence.AppendLine($"ResolvedWeightDistanceMotionForceOff={distantOff}");
                evidence.AppendLine($"A4ExactS2FramingForceOn={a4On}");
                evidence.AppendLine($"A4ExactS2FramingForceOff={a4Off}");
                evidence.AppendLine($"A4MaximumMoved={(Math.Abs(a4On.MaximumPixel - a4Off.MaximumPixel) > 1e-7)}; ForceOnMax:{a4On.MaximumPixel:F9}; ForceOffMax:{a4Off.MaximumPixel:F9}; Reference:1.000000000");
                evidence.AppendLine($"ProductionShader={productionShader}; DiagnosticShader={diagnosticShader}");
                evidence.AppendLine("Captures=5E_FlatSlices_AnisoOn.png;5E_FlatSlices_AnisoOff.png;5E_FlatSlices_InjectedBleed_Control.png;5E_GrazingDistance_AnisoOn.png;5E_GrazingDistance_AnisoOff.png");
                evidence.AppendLine("SceneSaved=False; ProductionShaderOrMaterialModified=False; ImportersModified=False; SerializedQualityModified=False");
                evidence.AppendLine($"RESULT={(pass ? "PASS" : "FAIL")}");
                string path = Path.Combine(outputDirectory, "5E_Validation_Evidence.txt");
                File.WriteAllText(path, evidence.ToString(), new UTF8Encoding(false));
                Debug.Log($"[Sol Landscape 5E] {(pass ? "PASS" : "FAIL")}; evidence={path}");
                exitCode = pass ? 0 : 1;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                exitCode = 1;
            }
            finally
            {
                QualitySettings.anisotropicFiltering = originalAnisotropy;
                foreach (UnityEngine.Object item in temporary.Where(value => value != null).Reverse<UnityEngine.Object>())
                    UnityEngine.Object.DestroyImmediate(item);
                EditorApplication.Exit(exitCode);
            }
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
            var go = new GameObject("5E Measurement Camera") { hideFlags = HideFlags.HideAndDontSave };
            temporary.Add(go);
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 2000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
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

        private static void ConfigureCamera(Camera camera, Vector3 pivot, Quaternion rotation,
            float orthographicSize, float distance, float aspect)
        {
            camera.transform.SetPositionAndRotation(pivot - rotation * Vector3.forward * distance, rotation);
            camera.orthographicSize = orthographicSize;
            camera.aspect = aspect;
        }

        private static Texture2DArray CreateFlatArray()
        {
            var array = new Texture2DArray(64, 64, Palette.Length, TextureFormat.RGBA32, true, true)
            {
                name = "5E Flat Slice Positive Control",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Repeat,
                anisoLevel = 16
            };
            for (int slice = 0; slice < Palette.Length; ++slice)
                array.SetPixels(Enumerable.Repeat(Palette[slice], 64 * 64).ToArray(), slice, 0);
            array.Apply(true, false);
            return array;
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

        private static MotionMetrics CaptureMotion(Terrain terrain, Material material, Camera camera,
            float stepMetres, int frameCount, int width, int height)
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

        private static FlatMetrics MeasureFlat(IEnumerable<Color> pixels)
        {
            int full = 0;
            int contaminated = 0;
            double sum = 0d;
            double max = 0d;
            var winners = new int[Palette.Length];
            foreach (Color pixel in pixels)
            {
                int winner = Mathf.RoundToInt(pixel.a * 8f) - 1;
                if (winner < 0 || winner >= Palette.Length)
                    continue;
                ++full;
                ++winners[winner];
                Color expected = Palette[winner];
                double error = Math.Max(Math.Abs(pixel.r - expected.r),
                    Math.Max(Math.Abs(pixel.g - expected.g), Math.Abs(pixel.b - expected.b)));
                sum += (Math.Abs(pixel.r - expected.r) + Math.Abs(pixel.g - expected.g) + Math.Abs(pixel.b - expected.b)) / 3d;
                max = Math.Max(max, error);
                if (error > 1e-6)
                    ++contaminated;
            }
            return new FlatMetrics(full, contaminated, full > 0 ? sum / full : double.NaN, max, winners);
        }

        private static DifferenceMetrics DifferenceMasked(IReadOnlyList<Color> a, IReadOnlyList<Color> b,
            bool requireEncodedWinner)
        {
            long samples = 0;
            long changed = 0;
            double sum = 0d;
            double max = 0d;
            for (int i = 0; i < a.Count; ++i)
            {
                bool included = requireEncodedWinner
                    ? Mathf.RoundToInt(a[i].a * 8f) > 0 && Mathf.RoundToInt(b[i].a * 8f) > 0
                    : a[i].a > 0.5f && b[i].a > 0.5f;
                if (!included)
                    continue;
                ++samples;
                double delta = (Math.Abs(a[i].r - b[i].r) + Math.Abs(a[i].g - b[i].g) + Math.Abs(a[i].b - b[i].b)) / 3d;
                double channelMax = Math.Max(Math.Abs(a[i].r - b[i].r),
                    Math.Max(Math.Abs(a[i].g - b[i].g), Math.Abs(a[i].b - b[i].b)));
                sum += delta;
                max = Math.Max(max, channelMax);
                if (channelMax > 1e-6)
                    ++changed;
            }
            return new DifferenceMetrics(samples, changed, samples > 0 ? sum / samples : double.NaN, max);
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
            return new MotionMetrics(means, maxGlobal, maxMae, maxPixel);
        }

        private static string DescribeTextureState(Terrain terrain, SolLandscapeConfig config)
        {
            Texture2D[] controls = terrain.terrainData.alphamapTextures;
            string controlText = string.Join("|", controls.Select((texture, index) => string.Format(Invariant,
                "Control{0}:{1}x{2};Format:{3};GraphicsFormat:{4};Mips:{5};Filter:{6};Wrap:{7};Aniso:{8};Readable:{9}",
                index, texture.width, texture.height, texture.format, texture.graphicsFormat, texture.mipmapCount,
                texture.filterMode, texture.wrapMode, texture.anisoLevel, texture.isReadable)));
            Texture2DArray cs = config.CSArray;
            Texture2DArray noh = config.NOHArray;
            string arrays = string.Format(Invariant,
                "CS:{0}x{1}x{2};Mips:{3};Format:{4};GraphicsFormat:{5};Filter:{6};Wrap:{7};Aniso:{8}|NOH:{9}x{10}x{11};Mips:{12};Format:{13};GraphicsFormat:{14};Filter:{15};Wrap:{16};Aniso:{17}",
                cs.width, cs.height, cs.depth, cs.mipmapCount, cs.format, cs.graphicsFormat, cs.filterMode, cs.wrapMode, cs.anisoLevel,
                noh.width, noh.height, noh.depth, noh.mipmapCount, noh.format, noh.graphicsFormat, noh.filterMode, noh.wrapMode, noh.anisoLevel);
            return controlText + "|" + arrays;
        }

        private static ShaderMetrics MeasureShader(Shader shader)
        {
            ShaderMessage[] messages = ShaderUtil.GetShaderMessages(shader);
            return new ShaderMetrics(shader.isSupported,
                messages.Count(message => message.severity == ShaderCompilerMessageSeverity.Warning),
                messages.Count(message => message.severity == ShaderCompilerMessageSeverity.Error));
        }

        private static string DescribePerSlice(IEnumerable<FlatMetrics> metrics)
        {
            return string.Join("|", metrics.Select((value, slice) => $"Slice{slice}:{value}"));
        }

        private static void WritePng(string path, Color[] linear, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false, false);
            texture.SetPixels(linear.Select(pixel => new Color(
                Mathf.LinearToGammaSpace(Mathf.Max(0f, pixel.r)),
                Mathf.LinearToGammaSpace(Mathf.Max(0f, pixel.g)),
                Mathf.LinearToGammaSpace(Mathf.Max(0f, pixel.b)), 1f)).ToArray());
            texture.Apply(false, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
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

        private static string GetArgument(string[] arguments, string name, string fallback)
        {
            for (int index = 0; index < arguments.Length - 1; ++index)
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    return arguments[index + 1];
            return fallback;
        }

        private readonly struct FlatMetrics
        {
            public FlatMetrics(int full, int contaminated, double mae, double max, int[] winners)
            { FullWeightPixels = full; ContaminatedPixels = contaminated; MeanError = mae; MaximumError = max; Winners = winners; }
            public int FullWeightPixels { get; }
            public int ContaminatedPixels { get; }
            public double MeanError { get; }
            public double MaximumError { get; }
            private int[] Winners { get; }
            public override string ToString() => string.Format(Invariant,
                "FullWeightPixels:{0}; ContaminatedPixels:{1}; MeanRGBError:{2:F9}; MaxChannelError:{3:F9}; Winners:[{4}]",
                FullWeightPixels, ContaminatedPixels, MeanError, MaximumError, string.Join(",", Winners));
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

        private readonly struct MotionMetrics
        {
            public MotionMetrics(double[] means, double global, double mae, double pixel)
            { Means = means; MaximumGlobal = global; MaximumMae = mae; MaximumPixel = pixel; }
            private double[] Means { get; }
            public double MaximumGlobal { get; }
            public double MaximumMae { get; }
            public double MaximumPixel { get; }
            public override string ToString() => string.Format(Invariant,
                "Means:[{0}]; MaxAdjacentGlobalMean:{1:F9}; MaxAdjacentFieldMAE:{2:F9}; MaxAdjacentPixel:{3:F9}",
                string.Join(",", Means.Select(value => value.ToString("F9", Invariant))), MaximumGlobal, MaximumMae, MaximumPixel);
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
