using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Sol.Landscape;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Sol.Landscape.Diagnostics.Editor
{
    /// <summary>
    /// Ticket 5B's current-content L1 re-capture. This is an editor-only, unsaved visual
    /// observation under declared ControlledDaylight; it does not re-run Phase 4 metrics.
    /// </summary>
    public static class SolLandscape5BVisualCapture
    {
        private const string ScenePath = "Assets/Scenes/SolsWeather_Demo.unity";
        private const string ArrayMaterialPath = "Assets/Sky-and-Water/Landscape/M_SolLandscape.mat";
        private const int CaptureSize = 768;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private static readonly int SurfaceWetnessId = Shader.PropertyToID("_Sol_SurfaceWetness");
        private static readonly int SurfaceSnowCoverId = Shader.PropertyToID("_Sol_SurfaceSnowCover");
        private static readonly int SurfaceTemperatureId = Shader.PropertyToID("_Sol_SurfaceTemperature");
        private static readonly int TerrainWetnessId = Shader.PropertyToID("_Sol_TerrainWetness");
        private static readonly int GlobalWaterLevelId = Shader.PropertyToID("_Sol_GlobalWaterLevel");

        public static void CaptureFromCommandLine()
        {
            int exitCode = 1;
            GameObject cameraObject = null;
            GameObject lightObject = null;
            RenderTexture target = null;
            Texture2D readback = null;
            try
            {
                string outputDirectory = Path.GetFullPath(GetArgument(
                    System.Environment.GetCommandLineArgs(),
                    "-sol5BVisualOutput",
                    "../Landscape5BEvidence"));
                Directory.CreateDirectory(outputDirectory);

                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Terrain terrain = Terrain.activeTerrain
                    ?? UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault();
                if (terrain == null || terrain.terrainData == null)
                    throw new InvalidOperationException("The demo Terrain and TerrainData are required.");

                Material arrayMaterial = AssetDatabase.LoadAssetAtPath<Material>(ArrayMaterialPath);
                if (arrayMaterial == null || arrayMaterial.shader == null)
                    throw new InvalidOperationException("The production array terrain material is missing.");

                terrain.materialTemplate = arrayMaterial;
                terrain.drawHeightmap = true;
                terrain.drawInstanced = true;
                terrain.drawTreesAndFoliage = false;
                terrain.basemapDistance = float.MaxValue;
                terrain.Flush();

                foreach (Camera camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    camera.enabled = false;
                foreach (Renderer renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    renderer.enabled = false;
                foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    canvas.enabled = false;
                foreach (Light light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    light.enabled = false;

                SolLandscapeDriver driver = UnityEngine.Object
                    .FindObjectsByType<SolLandscapeDriver>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                    .FirstOrDefault();
                if (driver == null)
                    throw new InvalidOperationException("The SolLandscapeDriver is required to publish the current S2-S6 contract.");
                driver.Invalidate();
                MethodInfo publish = typeof(SolLandscapeDriver).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic);
                if (publish == null)
                    throw new MissingMethodException(typeof(SolLandscapeDriver).FullName, "Publish");
                publish.Invoke(driver, null);
                if (driver.LastPublishRefused)
                    throw new InvalidOperationException($"Landscape publication refused: {driver.LastRefusalReason}");

                cameraObject = new GameObject("5B Capture Camera") { hideFlags = HideFlags.HideAndDontSave };
                Camera captureCamera = cameraObject.AddComponent<Camera>();
                captureCamera.transform.SetPositionAndRotation(new Vector3(-30f, 835f, -17f), Quaternion.Euler(90f, 0f, 0f));
                captureCamera.orthographic = true;
                captureCamera.orthographicSize = 100f;
                captureCamera.aspect = 1f;
                captureCamera.nearClipPlane = 0.1f;
                captureCamera.farClipPlane = 1000f;
                captureCamera.clearFlags = CameraClearFlags.SolidColor;
                captureCamera.backgroundColor = new Color(0.025f, 0.03f, 0.04f, 1f);
                captureCamera.allowMSAA = true;
                captureCamera.allowDynamicResolution = false;
                UniversalAdditionalCameraData cameraData = captureCamera.GetUniversalAdditionalCameraData();
                cameraData.renderPostProcessing = false;
                cameraData.renderShadows = true;

                lightObject = new GameObject("ControlledDaylight") { hideFlags = HideFlags.HideAndDontSave };
                Light daylight = lightObject.AddComponent<Light>();
                daylight.type = LightType.Directional;
                daylight.transform.rotation = Quaternion.Euler(38f, -35f, 0f);
                daylight.color = Color.white;
                daylight.intensity = 1.35f;
                daylight.shadows = LightShadows.Soft;
                daylight.shadowStrength = 1f;
                RenderSettings.sun = daylight;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.35f, 1f);

                target = new RenderTexture(CaptureSize, CaptureSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "5B L1 Capture",
                    antiAliasing = 2,
                    hideFlags = HideFlags.HideAndDontSave
                };
                target.Create();
                captureCamera.targetTexture = target;
                readback = new Texture2D(CaptureSize, CaptureSize, TextureFormat.RGB24, false, false)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };

                SetWeather(surfaceSnow: 0f, temperatureC: 18f);
                CapturePng(captureCamera, target, readback, Path.Combine(outputDirectory, "5B_L1_4B_Stone_ControlledDaylight.png"));
                ImageMetrics stoneMetrics = ImageMetrics.Measure(readback);

                SetWeather(surfaceSnow: 1f, temperatureC: -6f);
                CapturePng(captureCamera, target, readback, Path.Combine(outputDirectory, "5B_L1_4C_Snow_ControlledDaylight.png"));
                ImageMetrics snowMetrics = ImageMetrics.Measure(readback);

                StringBuilder evidence = new StringBuilder();
                evidence.AppendLine("Sol Landscape Phase 5 ticket 5B L1 re-capture evidence");
                evidence.AppendLine($"UTC={DateTime.UtcNow:O}");
                evidence.AppendLine($"Unity={Application.unityVersion}");
                evidence.AppendLine($"Graphics={SystemInfo.graphicsDeviceType}; Name={SystemInfo.graphicsDeviceName}");
                evidence.AppendLine("Purpose=Correct the ambient-only Phase 4B/4C subjective relief readings under declared daylight; numeric Phase 4 evidence is not re-run");
                evidence.AppendLine("ContentCaveat=Current sculpted terrain and current S2-S6 rules; not a like-for-like Phase 4 comparison");
                evidence.AppendLine("Framing=Orthographic top-down 200m span centred at world XZ (-30,-17); 768x768; terrain only; post off; foliage off");
                evidence.AppendLine("CameraPosition=(-30,835,-17); CameraEuler=(90,0,0); OrthographicSize=100; DistanceToOriginalGround=850");
                evidence.AppendLine("ControlledDaylight=Directional; Euler=(38,-35,0); Intensity=1.35; Color=(1,1,1); Shadows=Soft; ShadowStrength=1");
                evidence.AppendLine("Ambient=Flat; Color=(0.35,0.35,0.35)");
                evidence.AppendLine("MSAA=2x capture target; CameraAllowMSAA=True");
                evidence.AppendLine("StoneWeather=SurfaceSnow:0; TemperatureC:18; SurfaceWetness:0; TerrainWetness:(0,0,0,0); WaterLevel:0");
                evidence.AppendLine("SnowWeather=SurfaceSnow:1; TemperatureC:-6; SurfaceWetness:0; TerrainWetness:(0,0,0,0); WaterLevel:0");
                evidence.AppendLine($"StoneCaptureMetrics={stoneMetrics}");
                evidence.AppendLine($"SnowCaptureMetrics={snowMetrics}");
                evidence.AppendLine("StoneCapture=5B_L1_4B_Stone_ControlledDaylight.png");
                evidence.AppendLine("SnowCapture=5B_L1_4C_Snow_ControlledDaylight.png");
                evidence.AppendLine("SceneSaved=False");
                File.WriteAllText(Path.Combine(outputDirectory, "5B_L1_Recapture_Evidence.txt"), evidence.ToString(), new UTF8Encoding(false));
                Debug.Log($"[Sol Landscape 5B] L1 recaptures written to {outputDirectory}");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                exitCode = 1;
            }
            finally
            {
                if (target != null)
                {
                    if (cameraObject != null)
                    {
                        Camera captureCamera = cameraObject.GetComponent<Camera>();
                        if (captureCamera != null)
                            captureCamera.targetTexture = null;
                    }
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }
                if (readback != null)
                    UnityEngine.Object.DestroyImmediate(readback);
                if (cameraObject != null)
                    UnityEngine.Object.DestroyImmediate(cameraObject);
                if (lightObject != null)
                    UnityEngine.Object.DestroyImmediate(lightObject);
                EditorApplication.Exit(exitCode);
            }
        }

        private static void SetWeather(float surfaceSnow, float temperatureC)
        {
            Shader.SetGlobalFloat(SurfaceWetnessId, 0f);
            Shader.SetGlobalFloat(SurfaceSnowCoverId, surfaceSnow);
            Shader.SetGlobalFloat(SurfaceTemperatureId, temperatureC);
            Shader.SetGlobalVector(TerrainWetnessId, Vector4.zero);
            Shader.SetGlobalFloat(GlobalWaterLevelId, 0f);
        }

        private static void CapturePng(Camera camera, RenderTexture target, Texture2D readback, string outputPath)
        {
            RenderTexture prior = RenderTexture.active;
            camera.Render();
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0f, 0f, target.width, target.height), 0, 0, false);
            readback.Apply(false, false);
            File.WriteAllBytes(outputPath, readback.EncodeToPNG());
            RenderTexture.active = prior;
        }

        private static string GetArgument(string[] arguments, string name, string fallback)
        {
            for (int index = 0; index < arguments.Length - 1; ++index)
            {
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    return arguments[index + 1];
            }
            return fallback;
        }

        private readonly struct ImageMetrics
        {
            private ImageMetrics(int pixelCount, int finitePixels, double meanLuminance, double luminanceStandardDeviation)
            {
                PixelCount = pixelCount;
                FinitePixels = finitePixels;
                MeanLuminance = meanLuminance;
                LuminanceStandardDeviation = luminanceStandardDeviation;
            }

            private int PixelCount { get; }
            private int FinitePixels { get; }
            private double MeanLuminance { get; }
            private double LuminanceStandardDeviation { get; }

            public static ImageMetrics Measure(Texture2D texture)
            {
                Color[] pixels = texture.GetPixels();
                int finite = 0;
                double sum = 0d;
                double sumSquares = 0d;
                foreach (Color pixel in pixels)
                {
                    double luminance = pixel.r * 0.2126d + pixel.g * 0.7152d + pixel.b * 0.0722d;
                    if (double.IsNaN(luminance) || double.IsInfinity(luminance))
                        continue;
                    ++finite;
                    sum += luminance;
                    sumSquares += luminance * luminance;
                }
                double mean = finite > 0 ? sum / finite : double.NaN;
                double variance = finite > 0 ? Math.Max(0d, sumSquares / finite - mean * mean) : double.NaN;
                return new ImageMetrics(pixels.Length, finite, mean, Math.Sqrt(variance));
            }

            public override string ToString()
            {
                return string.Format(
                    Invariant,
                    "Pixels:{0};Finite:{1};MeanLuminance:{2:F9};LuminanceStdDev:{3:F9}",
                    PixelCount,
                    FinitePixels,
                    MeanLuminance,
                    LuminanceStandardDeviation);
            }
        }
    }
}
