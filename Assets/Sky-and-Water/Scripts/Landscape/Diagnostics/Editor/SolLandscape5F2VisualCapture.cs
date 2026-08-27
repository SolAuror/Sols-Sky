using System;
using System.Collections.Generic;
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
    /// Ticket 5F.2's per-material stochastic on/off visual verdicts, at the exact 5F/5F.1 steep-face
    /// site and play-framing camera. Also revisits Sand tileSize (2m vs 4m) under stochastic per item 5.
    /// All TerrainLayer.tileSize changes here are transient, in-memory only, and restored before exit;
    /// nothing is saved to the .terrainlayer assets by this tool.
    /// </summary>
    public static class SolLandscape5F2VisualCapture
    {
        private const string ScenePath = "Assets/Scenes/SolsWeather_Demo.unity";
        private const string ArrayMaterialPath = "Assets/Sky-and-Water/Landscape/M_SolLandscape.mat";
        private const string StochasticKeyword = "_SOL_LANDSCAPE_STOCHASTIC";
        private const int Width = 1280;
        private const int Height = 720;
        private const float BaseArrayTexelsPerTileMetre = 2048f;
        private const float SlopeThresholdDegrees = 35f;
        private const float StoneWeightThreshold = 0.5f;
        private const int AlphamapStride = 8;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly int SurfaceWetnessId = Shader.PropertyToID("_Sol_SurfaceWetness");
        private static readonly int SurfaceSnowCoverId = Shader.PropertyToID("_Sol_SurfaceSnowCover");
        private static readonly int SurfaceTemperatureId = Shader.PropertyToID("_Sol_SurfaceTemperature");
        private static readonly int TerrainWetnessId = Shader.PropertyToID("_Sol_TerrainWetness");
        private static readonly int GlobalWaterLevelId = Shader.PropertyToID("_Sol_GlobalWaterLevel");

        public static void CaptureFromCommandLine()
        {
            int exitCode = 1;
            var temporary = new List<UnityEngine.Object>();
            var originalTileSizes = new Dictionary<TerrainLayer, Vector2>();
            try
            {
                string outputDirectory = Path.GetFullPath(GetArgument(
                    System.Environment.GetCommandLineArgs(), "-sol5F2VisualOutput", "../Landscape5F2Evidence"));
                Directory.CreateDirectory(outputDirectory);

                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Terrain terrain = FindTerrain();
                SolLandscapeDriver driver = FindDriver();
                Publish(driver);
                SetDryWeather();
                Material production = AssetDatabase.LoadAssetAtPath<Material>(ArrayMaterialPath);
                if (production == null || production.shader == null)
                    throw new InvalidOperationException("The production array terrain material is missing.");

                IsolateTerrain(terrain);

                TerrainLayer[] layers = terrain.terrainData.terrainLayers;
                foreach (TerrainLayer layer in layers)
                    originalTileSizes[layer] = layer.tileSize;

                SteepPoint steep = FindSteepFace(terrain, layers);
                Camera camera = CreatePerspectiveCamera(temporary);
                CreateDaylight(temporary);
                PositionCameraAtSlope(camera, terrain, steep, viewDistance: 16f, eyeHeight: 1.75f);

                string sandName = layers.First(layer => layer.name == "TerrainLayer_Sand").name;

                Material stochOff = new Material(production)
                { name = "5F2 Stochastic Off", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                stochOff.DisableKeyword(StochasticKeyword);
                temporary.Add(stochOff);
                Material stochOn = new Material(production)
                { name = "5F2 Stochastic On", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                stochOn.EnableKeyword(StochasticKeyword);
                temporary.Add(stochOn);

                var stoneRegion = (xStart: 820, xEnd: 1250, yStart: 40, yEnd: 300);
                var sandRegion = (xStart: 40, xEnd: 1240, yStart: 560, yEnd: 700);

                // Primary matrix: current production tileSize (Sand=2m) and the item-5 revisit
                // (Sand=4m), each at stochastic off and on. Stone stays at its shipped 8m throughout;
                // only Sand's tileSize varies, so the matrix answers item 4 (does stochastic help
                // stone/sand at their CURRENT tileSize) and item 5 (does stochastic make 4m sand
                // acceptable again) from one consistent set of renders.
                var configs = new List<(string id, float sandTileSize, Material material)>
                {
                    ("Sand2_StochOff", 2f, stochOff),
                    ("Sand2_StochOn", 2f, stochOn),
                    ("Sand4_StochOff", 4f, stochOff),
                    ("Sand4_StochOn", 4f, stochOn),
                };

                var results = new List<CaptureResult>();
                foreach ((string id, float sandTileSize, Material material) in configs)
                {
                    foreach (TerrainLayer layer in layers)
                        layer.tileSize = layer.name == sandName ? new Vector2(sandTileSize, sandTileSize) : originalTileSizes[layer];
                    Publish(driver);
                    terrain.terrainData.terrainLayers = layers;
                    terrain.Flush();

                    Color[] pixels = Render(terrain, material, camera, Width, Height);
                    string file = $"5F2_SteepFace_PlayFraming_{id}.png";
                    WritePng(Path.Combine(outputDirectory, file), pixels, Width, Height);
                    TerrainMaskMetrics mask = MeasureTerrainMask(pixels);
                    GradientAnisotropy stoneAnisotropy = MeasureGradientAnisotropy(pixels, Width, Height, stoneRegion.xStart, stoneRegion.xEnd, stoneRegion.yStart, stoneRegion.yEnd);
                    GradientAnisotropy sandAnisotropy = MeasureGradientAnisotropy(pixels, Width, Height, sandRegion.xStart, sandRegion.xEnd, sandRegion.yStart, sandRegion.yEnd);
                    results.Add(new CaptureResult(id, file, mask, stoneAnisotropy, sandAnisotropy));
                }

                // Supplementary close-ups (FOV narrowed 60->18 degrees, same position/look-at,
                // declared separately, NOT part of the primary fixed-camera comparison per rule 6):
                // stone close-up at the shipped 8m, sand close-up looking more steeply down at the
                // near-foreground so individual stochastic cells are legible.
                float wideFov = camera.fieldOfView;
                var closeUpFiles = new List<string>();

                foreach (TerrainLayer layer in layers)
                    layer.tileSize = originalTileSizes[layer];
                Publish(driver);
                terrain.terrainData.terrainLayers = layers;
                terrain.Flush();

                camera.fieldOfView = 18f;
                closeUpFiles.Add(RenderNamed(terrain, stochOff, camera, outputDirectory, "5F2_StoneCloseUp_StochOff.png"));
                closeUpFiles.Add(RenderNamed(terrain, stochOn, camera, outputDirectory, "5F2_StoneCloseUp_StochOn.png"));
                camera.fieldOfView = wideFov;

                Vector3 originalCameraPosition = camera.transform.position;
                Quaternion originalCameraRotation = camera.transform.rotation;
                Vector3 sandLookTarget = GroundPointInFrontOfCamera(terrain, camera, 8f);
                camera.transform.position = sandLookTarget + Vector3.up * 3f + (originalCameraPosition - sandLookTarget).normalized * 4f;
                camera.transform.LookAt(sandLookTarget, Vector3.up);
                camera.fieldOfView = 30f;
                foreach (float sandTile in new[] { 2f, 4f })
                {
                    foreach (TerrainLayer layer in layers)
                        layer.tileSize = layer.name == sandName ? new Vector2(sandTile, sandTile) : originalTileSizes[layer];
                    Publish(driver);
                    terrain.terrainData.terrainLayers = layers;
                    terrain.Flush();
                    closeUpFiles.Add(RenderNamed(terrain, stochOff, camera, outputDirectory, $"5F2_SandCloseUp_Sand{FormatTile(sandTile)}_StochOff.png"));
                    closeUpFiles.Add(RenderNamed(terrain, stochOn, camera, outputDirectory, $"5F2_SandCloseUp_Sand{FormatTile(sandTile)}_StochOn.png"));
                }
                camera.transform.SetPositionAndRotation(originalCameraPosition, originalCameraRotation);
                camera.fieldOfView = wideFov;

                foreach (KeyValuePair<TerrainLayer, Vector2> original in originalTileSizes)
                    original.Key.tileSize = original.Value;
                Publish(driver);
                terrain.terrainData.terrainLayers = layers;
                terrain.Flush();

                var evidence = new StringBuilder();
                evidence.AppendLine("Sol Landscape Phase 5 ticket 5F.2 stochastic per-material visual verdicts");
                evidence.AppendLine($"UTC={DateTime.UtcNow:O}; Unity={Application.unityVersion}; Graphics={SystemInfo.graphicsDeviceType}; Device={SystemInfo.graphicsDeviceName}");
                evidence.AppendLine("ControlledDaylight=Directional; Euler=(38,-35,0); Intensity=1.35; Color=white; Shadows=Soft; ShadowStrength=1; Ambient=Flat(0.35,0.35,0.35); PostProcessing=False; TreesAndFoliage=False; Weather=DryFrozen");
                evidence.AppendLine($"SteepFaceSite(IdenticalTo5F_5F1)={steep}");
                evidence.AppendLine($"PlayFramingCamera(IdenticalTo5F_5F1)=Perspective; FOV:{wideFov.ToString("F3", Invariant)}; Position:{FormatVector(originalCameraPosition)}; ViewDistance:16m; EyeHeight:1.75m; Resolution:{Width}x{Height}");
                evidence.AppendLine("StoneTileSize=8 (unchanged from 5F.1) in every primary-matrix capture; only Sand tileSize and the stochastic keyword vary.");
                evidence.AppendLine($"StoneRegionPx=x:[{stoneRegion.xStart},{stoneRegion.xEnd}],y:[{stoneRegion.yStart},{stoneRegion.yEnd}]; SandRegionPx=x:[{sandRegion.xStart},{sandRegion.xEnd}],y:[{sandRegion.yStart},{sandRegion.yEnd}]");
                evidence.AppendLine("GradientAnisotropyDefinition=(sumAbsVerticalLuminanceGradient - sumAbsHorizontalLuminanceGradient) / (sum of both); +1=purely horizontal banding (vertical gradient dominates), 0=isotropic, -1=purely vertical banding. Sand's near-horizontal ripple lines are expected to score strongly positive with stochastic off; a successful rotate/flip disruption should measurably reduce this toward 0 by mixing in patches rotated 90 degrees.");
                foreach (CaptureResult result in results)
                    evidence.AppendLine($"Config_{result.Id}={result}");
                evidence.AppendLine("StoneCloseUpSupplementary=SamePositionAndLookAtAsThePrimaryCamera; FOVNarrowedTo18Degrees(from 60); NOT part of the fixed-camera comparison in rule 6");
                evidence.AppendLine("SandCloseUpSupplementary=DifferentPositionAndFOV(30deg), pointed down at the near-foreground sand so individual stochastic cells are legible; NOT part of the fixed-camera comparison in rule 6");
                evidence.AppendLine("CloseUpFiles=" + string.Join(";", closeUpFiles));
                evidence.AppendLine("SceneSaved=False; TerrainLayerAssetsModified=False; TerrainDataAlphamapsModified=False; ProductionMaterialModified=False");
                evidence.AppendLine("Captures=" + string.Join(";", results.Select(result => result.FileName).Concat(closeUpFiles)));
                evidence.AppendLine("RESULT=PASS");
                string path = Path.Combine(outputDirectory, "5F2_VisualVerdict_Evidence.txt");
                File.WriteAllText(path, evidence.ToString(), new UTF8Encoding(false));
                Debug.Log($"[Sol Landscape 5F.2] Visual verdict captures written to {outputDirectory}");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                exitCode = 1;
            }
            finally
            {
                bool anyRestored = false;
                foreach (KeyValuePair<TerrainLayer, Vector2> original in originalTileSizes)
                {
                    if (original.Key == null)
                        continue;
                    original.Key.tileSize = original.Value;
                    anyRestored = true;
                }
                if (anyRestored)
                {
                    try
                    {
                        Terrain restoreTerrain = Terrain.activeTerrain;
                        if (restoreTerrain != null && restoreTerrain.terrainData != null)
                            restoreTerrain.terrainData.terrainLayers = restoreTerrain.terrainData.terrainLayers;
                    }
                    catch (Exception)
                    {
                        // best-effort; the process is exiting either way
                    }
                }
                foreach (UnityEngine.Object item in temporary.Where(value => value != null).Reverse<UnityEngine.Object>())
                    UnityEngine.Object.DestroyImmediate(item);
                EditorApplication.Exit(exitCode);
            }
        }

        private static string RenderNamed(Terrain terrain, Material material, Camera camera, string outputDirectory, string fileName)
        {
            Color[] pixels = Render(terrain, material, camera, Width, Height);
            WritePng(Path.Combine(outputDirectory, fileName), pixels, Width, Height);
            return fileName;
        }

        private static GradientAnisotropy MeasureGradientAnisotropy(Color[] pixels, int width, int height, int xStart, int xEnd, int yStart, int yEnd)
        {
            double sumVertical = 0d;
            double sumHorizontal = 0d;
            for (int y = yStart + 1; y < yEnd - 1; ++y)
            {
                for (int x = xStart + 1; x < xEnd - 1; ++x)
                {
                    double lumRight = Luminance(pixels[y * width + (x + 1)]);
                    double lumLeft = Luminance(pixels[y * width + (x - 1)]);
                    double lumUp = Luminance(pixels[(y + 1) * width + x]);
                    double lumDown = Luminance(pixels[(y - 1) * width + x]);
                    sumHorizontal += Math.Abs(lumRight - lumLeft);
                    sumVertical += Math.Abs(lumUp - lumDown);
                }
            }
            double total = sumVertical + sumHorizontal;
            double index = total > 1e-9 ? (sumVertical - sumHorizontal) / total : 0d;
            return new GradientAnisotropy(index, sumVertical, sumHorizontal);
        }

        private static double Luminance(Color pixel) => 0.2126 * pixel.r + 0.7152 * pixel.g + 0.0722 * pixel.b;

        private static SteepPoint FindSteepFace(Terrain terrain, TerrainLayer[] layers)
        {
            TerrainData data = terrain.terrainData;
            int stone1Index = Array.FindIndex(layers, layer => layer.name == "TerrainLayer_Stone1");
            int stone2Index = Array.FindIndex(layers, layer => layer.name == "TerrainLayer_Stone2");
            if (stone1Index < 0 || stone2Index < 0)
                throw new InvalidOperationException("Expected TerrainLayer_Stone1 and TerrainLayer_Stone2 among the live layers.");

            float[,,] painted = data.GetAlphamaps(0, 0, data.alphamapWidth, data.alphamapHeight);
            int alphaHeight = painted.GetLength(0);
            int alphaWidth = painted.GetLength(1);

            var candidates = new List<(float slope, float u, float v)>();
            for (int y = 0; y < alphaHeight; y += AlphamapStride)
            {
                float v = alphaHeight > 1 ? y / (float)(alphaHeight - 1) : 0f;
                for (int x = 0; x < alphaWidth; x += AlphamapStride)
                {
                    float u = alphaWidth > 1 ? x / (float)(alphaWidth - 1) : 0f;
                    float stoneWeight = painted[y, x, stone1Index] + painted[y, x, stone2Index];
                    if (stoneWeight < StoneWeightThreshold)
                        continue;
                    float slope = data.GetSteepness(u, v);
                    if (slope < SlopeThresholdDegrees)
                        continue;
                    candidates.Add((slope, u, v));
                }
            }

            if (candidates.Count == 0)
                throw new InvalidOperationException($"No stone-dominant point found with slope >= {SlopeThresholdDegrees} degrees.");

            candidates.Sort((a, b) => a.slope.CompareTo(b.slope));
            (float slope, float u, float v) median = candidates[candidates.Count / 2];

            float neighbourSlopeSum = 0f;
            int neighbourSamples = 0;
            float step = 4f / Mathf.Max(data.size.x, data.size.z);
            foreach ((float du, float dv) in new[] { (step, 0f), (-step, 0f), (0f, step), (0f, -step) })
            {
                float nu = Mathf.Clamp01(median.u + du);
                float nv = Mathf.Clamp01(median.v + dv);
                neighbourSlopeSum += data.GetSteepness(nu, nv);
                ++neighbourSamples;
            }
            float neighbourMeanSlope = neighbourSlopeSum / neighbourSamples;

            Vector3 normal = data.GetInterpolatedNormal(median.u, median.v);
            float worldY = terrain.transform.position.y + data.GetInterpolatedHeight(median.u, median.v);
            Vector3 worldPosition = new Vector3(
                terrain.transform.position.x + median.u * data.size.x,
                worldY,
                terrain.transform.position.z + median.v * data.size.z);

            return new SteepPoint(worldPosition, normal, median.slope, neighbourMeanSlope, median.u, median.v);
        }

        private static void PositionCameraAtSlope(Camera camera, Terrain terrain, SteepPoint site, float viewDistance, float eyeHeight)
        {
            TerrainData data = terrain.terrainData;
            Vector3 horizontalOut = new Vector3(site.Normal.x, 0f, site.Normal.z);
            if (horizontalOut.sqrMagnitude < 1e-6f)
                horizontalOut = Vector3.forward;
            horizontalOut.Normalize();

            Vector3 cameraXZ = site.WorldPosition + horizontalOut * viewDistance;
            float cu = Mathf.Clamp01((cameraXZ.x - terrain.transform.position.x) / data.size.x);
            float cv = Mathf.Clamp01((cameraXZ.z - terrain.transform.position.z) / data.size.z);
            float cameraGroundY = terrain.transform.position.y + data.GetInterpolatedHeight(cu, cv);
            Vector3 cameraPosition = new Vector3(cameraXZ.x, cameraGroundY + eyeHeight, cameraXZ.z);

            camera.transform.position = cameraPosition;
            camera.transform.LookAt(site.WorldPosition + Vector3.up * 1.0f, Vector3.up);
        }

        private static Vector3 GroundPointInFrontOfCamera(Terrain terrain, Camera camera, float distance)
        {
            TerrainData data = terrain.terrainData;
            Vector3 forwardFlat = new Vector3(camera.transform.forward.x, 0f, camera.transform.forward.z);
            if (forwardFlat.sqrMagnitude < 1e-6f)
                forwardFlat = Vector3.forward;
            forwardFlat.Normalize();
            Vector3 groundXZ = camera.transform.position + forwardFlat * distance;
            float u = Mathf.Clamp01((groundXZ.x - terrain.transform.position.x) / data.size.x);
            float v = Mathf.Clamp01((groundXZ.z - terrain.transform.position.z) / data.size.z);
            float worldY = terrain.transform.position.y + data.GetInterpolatedHeight(u, v);
            return new Vector3(
                terrain.transform.position.x + u * data.size.x,
                worldY,
                terrain.transform.position.z + v * data.size.z);
        }

        private static TerrainMaskMetrics MeasureTerrainMask(Color[] pixels)
        {
            Color background = new Color(0f, 1f, 1f, 1f);
            int terrainPixels = 0;
            foreach (Color pixel in pixels)
            {
                float distance = Mathf.Abs(pixel.r - background.r) + Mathf.Abs(pixel.g - background.g) + Mathf.Abs(pixel.b - background.b);
                if (distance > 0.05f)
                    ++terrainPixels;
            }
            return new TerrainMaskMetrics(pixels.Length, terrainPixels);
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

        private static Camera CreatePerspectiveCamera(ICollection<UnityEngine.Object> temporary)
        {
            var go = new GameObject("5F.2 Play Framing Camera") { hideFlags = HideFlags.HideAndDontSave };
            temporary.Add(go);
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = false;
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 2000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 1f, 1f, 1f);
            camera.allowMSAA = true;
            camera.allowDynamicResolution = false;
            camera.aspect = Width / (float)Height;
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

        private static Color[] Render(Terrain terrain, Material material, Camera camera, int width, int height)
        {
            terrain.materialTemplate = material;
            terrain.Flush();
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                hideFlags = HideFlags.HideAndDontSave,
                antiAliasing = 2
            };
            target.Create();
            var readback = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
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

        private static void WritePng(string path, Color[] pixels, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false, false);
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
        }

        private static string FormatTile(float tileSize) => tileSize.ToString("F0", Invariant);

        private static string FormatVector(Vector3 value) => string.Format(Invariant, "({0:F3},{1:F3},{2:F3})", value.x, value.y, value.z);

        private static string GetArgument(string[] arguments, string name, string fallback)
        {
            for (int index = 0; index < arguments.Length - 1; ++index)
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    return arguments[index + 1];
            return fallback;
        }

        private readonly struct SteepPoint
        {
            public SteepPoint(Vector3 worldPosition, Vector3 normal, float slopeDegrees, float neighbourMeanSlopeDegrees, float u, float v)
            {
                WorldPosition = worldPosition;
                Normal = normal;
                SlopeDegrees = slopeDegrees;
                NeighbourMeanSlopeDegrees = neighbourMeanSlopeDegrees;
                U = u;
                V = v;
            }

            public Vector3 WorldPosition { get; }
            public Vector3 Normal { get; }
            public float SlopeDegrees { get; }
            public float NeighbourMeanSlopeDegrees { get; }
            public float U { get; }
            public float V { get; }

            public override string ToString() => string.Format(Invariant,
                "WorldPosition:{0}; Normal:{1}; SlopeDegrees:{2:F3}; NeighbourMeanSlopeDegrees(4x4mCross):{3:F3}; UV:({4:F6},{5:F6})",
                FormatVector(WorldPosition), FormatVector(Normal), SlopeDegrees, NeighbourMeanSlopeDegrees, U, V);
        }

        private readonly struct TerrainMaskMetrics
        {
            public TerrainMaskMetrics(int totalPixels, int terrainPixels)
            {
                TotalPixels = totalPixels;
                TerrainPixels = terrainPixels;
            }

            public int TotalPixels { get; }
            public int TerrainPixels { get; }
            public double TerrainFraction => TotalPixels > 0 ? TerrainPixels / (double)TotalPixels : 0d;

            public override string ToString() => string.Format(Invariant,
                "TerrainPixels:{0}/{1} ({2:F4})", TerrainPixels, TotalPixels, TerrainFraction);
        }

        private readonly struct GradientAnisotropy
        {
            public GradientAnisotropy(double index, double sumVertical, double sumHorizontal)
            {
                Index = index;
                SumVertical = sumVertical;
                SumHorizontal = sumHorizontal;
            }

            public double Index { get; }
            public double SumVertical { get; }
            public double SumHorizontal { get; }

            public override string ToString() => string.Format(Invariant,
                "AnisotropyIndex:{0:F6}; SumVerticalGradient:{1:F1}; SumHorizontalGradient:{2:F1}", Index, SumVertical, SumHorizontal);
        }

        private readonly struct CaptureResult
        {
            public CaptureResult(string id, string fileName, TerrainMaskMetrics mask, GradientAnisotropy stoneAnisotropy, GradientAnisotropy sandAnisotropy)
            {
                Id = id;
                FileName = fileName;
                Mask = mask;
                StoneAnisotropy = stoneAnisotropy;
                SandAnisotropy = sandAnisotropy;
            }

            public string Id { get; }
            public string FileName { get; }
            private TerrainMaskMetrics Mask { get; }
            private GradientAnisotropy StoneAnisotropy { get; }
            private GradientAnisotropy SandAnisotropy { get; }

            public override string ToString() => string.Format(Invariant,
                "File:{0}; {1}; StoneGradientAnisotropy=[{2}]; SandGradientAnisotropy=[{3}]",
                FileName, Mask, StoneAnisotropy, SandAnisotropy);
        }
    }
}
