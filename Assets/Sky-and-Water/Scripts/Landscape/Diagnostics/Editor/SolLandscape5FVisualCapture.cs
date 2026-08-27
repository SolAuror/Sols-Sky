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
    /// Ticket 5F's steep-face tileSize 2/4/8 comparison at realistic (perspective) play framing.
    /// All TerrainLayer.tileSize changes here are transient, in-memory only, and restored before exit;
    /// nothing is saved to the .terrainlayer assets by this tool.
    /// </summary>
    public static class SolLandscape5FVisualCapture
    {
        private const string ScenePath = "Assets/Scenes/SolsWeather_Demo.unity";
        private const string ArrayMaterialPath = "Assets/Sky-and-Water/Landscape/M_SolLandscape.mat";
        private const string DebugKeyword = "_SOL_LANDSCAPE_DEBUG";
        private const int Width = 1280;
        private const int Height = 720;
        private const int TopDownSize = 900;
        private const float BaseArrayTexelsPerTileMetre = 2048f;
        private const int AlphamapStride = 8;
        private const float SlopeThresholdDegrees = 35f;
        private const float StoneWeightThreshold = 0.5f;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly int DebugModeId = Shader.PropertyToID("_Sol_LandscapeDebugMode");
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
                    System.Environment.GetCommandLineArgs(), "-sol5FVisualOutput", "../Landscape5FEvidence"));
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

                Camera topDown = CreateOrthographicCamera(temporary);
                ConfigureTopDown(topDown, steep.WorldPosition, orthographicSize: 22f);

                Material lit = new Material(production)
                { name = "5F Steep Face Lit", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                temporary.Add(lit);

                var perspectiveResults = new List<TileSizeCaptureResult>();
                var topDownResults = new List<TileSizeCaptureResult>();
                float[] tileSizes = { 2f, 4f, 8f };
                foreach (float tileSize in tileSizes)
                {
                    SetTileSizeAll(terrain, layers, tileSize, driver);
                    terrain.Flush();

                    Color[] perspectivePixels = Render(terrain, lit, camera, Width, Height);
                    string perspectiveFile = $"5F_SteepFace_PlayFraming_Tile{FormatTile(tileSize)}.png";
                    WritePng(Path.Combine(outputDirectory, perspectiveFile), perspectivePixels, Width, Height);
                    TerrainMaskMetrics perspectiveMask = MeasureTerrainMask(perspectivePixels);
                    PixelDensity density = MeasurePixelDensity(camera, steep, Width, Height);
                    perspectiveResults.Add(new TileSizeCaptureResult(tileSize, perspectiveFile, perspectiveMask, density));

                    Color[] topDownPixels = Render(terrain, lit, topDown, TopDownSize, TopDownSize);
                    string topDownFile = $"5F_SteepFace_TopDownSuperseded_Tile{FormatTile(tileSize)}.png";
                    WritePng(Path.Combine(outputDirectory, topDownFile), topDownPixels, TopDownSize, TopDownSize);
                    TerrainMaskMetrics topDownMask = MeasureTerrainMask(topDownPixels);
                    PixelDensity topDownDensity = MeasurePixelDensity(topDown, steep, TopDownSize, TopDownSize);
                    topDownResults.Add(new TileSizeCaptureResult(tileSize, topDownFile, topDownMask, topDownDensity));
                }

                foreach (KeyValuePair<TerrainLayer, Vector2> original in originalTileSizes)
                    original.Key.tileSize = original.Value;
                Publish(driver);
                terrain.terrainData.terrainLayers = layers;
                terrain.Flush();

                Material stoneDebug = new Material(production)
                { name = "5F Stone Weight Debug", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                stoneDebug.EnableKeyword(DebugKeyword);
                stoneDebug.SetFloat(DebugModeId, 3f);
                temporary.Add(stoneDebug);
                Color[] stoneWeightFrame = Render(terrain, stoneDebug, camera, Width, Height);
                double stoneWeightAtCenter = SampleCenterBox(stoneWeightFrame, Width, Height, 32);

                var evidence = new StringBuilder();
                evidence.AppendLine("Sol Landscape Phase 5 ticket 5F steep-face tileSize 2/4/8 comparison");
                evidence.AppendLine($"UTC={DateTime.UtcNow:O}; Unity={Application.unityVersion}; Graphics={SystemInfo.graphicsDeviceType}; Device={SystemInfo.graphicsDeviceName}");
                evidence.AppendLine("ControlledDaylight=Directional; Euler=(38,-35,0); Intensity=1.35; Color=white; Shadows=Soft; ShadowStrength=1; Ambient=Flat(0.35,0.35,0.35); PostProcessing=False; TreesAndFoliage=False; Weather=DryFrozen");
                evidence.AppendLine($"SteepFaceSite={steep}");
                evidence.AppendLine($"StoneWeightAtSiteCentre(DebugMode3,32x32box)={stoneWeightAtCenter.ToString("F6", Invariant)}");
                evidence.AppendLine($"PlayFramingCamera=Perspective; FOV:{camera.fieldOfView.ToString("F3", Invariant)}; Position:{FormatVector(camera.transform.position)}; LookAtSite; ViewDistance:16m (horizontal outward from slope along its normal); EyeHeight:1.75m above local ground; Resolution:{Width}x{Height}");
                evidence.AppendLine("PlayFramingRationale=C1/D1: tiling repetition is a mid-to-far-field effect on slopes; this frames the steep face from a nearby vantage the way a player approaching the mountain would see it, not top-down.");
                evidence.AppendLine($"TopDownSupersededCamera=Orthographic top-down over the same XZ site; OrthographicSize:22; Resolution:{TopDownSize}x{TopDownSize}; NOT used for the verdict, captured only to show why the original D1 argument does not survive on this content");
                evidence.AppendLine($"BaseArrayTexelResolutionPerTileAssumption={BaseArrayTexelsPerTileMetre.ToString("F0", Invariant)} texels covering one tileSize span (5E: CS/NOH arrays are 2048x2048)");
                foreach (TileSizeCaptureResult result in perspectiveResults)
                    evidence.AppendLine($"PlayFraming_Tile{FormatTile(result.TileSize)}={result}");
                foreach (TileSizeCaptureResult result in topDownResults)
                    evidence.AppendLine($"TopDownSuperseded_Tile{FormatTile(result.TileSize)}={result}");
                evidence.AppendLine("RepeatCountAcross1000m=Tile2:500;Tile4:250;Tile8:125");
                evidence.AppendLine("SceneSaved=False; TerrainLayerAssetsModified=False; TerrainDataAlphamapsModified=False; ProductionMaterialModified=False");
                evidence.AppendLine("Captures=" + string.Join(";", perspectiveResults.Select(result => result.FileName).Concat(topDownResults.Select(result => result.FileName))));
                evidence.AppendLine("RESULT=PASS");
                string path = Path.Combine(outputDirectory, "5F_SteepFace_Evidence.txt");
                File.WriteAllText(path, evidence.ToString(), new UTF8Encoding(false));
                Debug.Log($"[Sol Landscape 5F] Steep-face captures written to {outputDirectory}");
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

        private static void ConfigureTopDown(Camera camera, Vector3 site, float orthographicSize)
        {
            camera.transform.SetPositionAndRotation(new Vector3(site.x, site.y + 400f, site.z), Quaternion.Euler(90f, 0f, 0f));
            camera.orthographicSize = orthographicSize;
            camera.aspect = 1f;
        }

        private static void SetTileSizeAll(Terrain terrain, TerrainLayer[] layers, float tileSize, SolLandscapeDriver driver)
        {
            foreach (TerrainLayer layer in layers)
                layer.tileSize = new Vector2(tileSize, tileSize);
            // _Sol_LandscapeLayerST (world-to-tile UV scale) is computed from TerrainLayer.tileSize and
            // pushed to the shader once at SolLandscapeDriver.Publish() time, not read live from the
            // TerrainLayer object every frame. A tileSize override only reaches the shader if we
            // republish after changing it.
            Publish(driver);
            terrain.terrainData.terrainLayers = layers;
        }

        private static PixelDensity MeasurePixelDensity(Camera camera, SteepPoint site, int width, int height)
        {
            Vector3 normal = site.Normal;
            Vector3 upSlope = Vector3.ProjectOnPlane(Vector3.up, normal);
            if (upSlope.sqrMagnitude < 1e-6f)
                upSlope = Vector3.forward;
            upSlope.Normalize();
            Vector3 acrossSlope = Vector3.Cross(normal, upSlope).normalized;

            Vector2 p0 = ProjectToPixels(camera, site.WorldPosition, width, height);
            Vector2 pUp = ProjectToPixels(camera, site.WorldPosition + upSlope, width, height);
            Vector2 pAcross = ProjectToPixels(camera, site.WorldPosition + acrossSlope, width, height);

            float pixelsPerMetreUpSlope = Vector2.Distance(p0, pUp);
            float pixelsPerMetreAcrossSlope = Vector2.Distance(p0, pAcross);
            return new PixelDensity(pixelsPerMetreUpSlope, pixelsPerMetreAcrossSlope);
        }

        private static Vector2 ProjectToPixels(Camera camera, Vector3 world, int width, int height)
        {
            Vector4 viewSpace = camera.worldToCameraMatrix * new Vector4(world.x, world.y, world.z, 1f);
            Vector4 clip = camera.projectionMatrix * viewSpace;
            Vector2 ndc = new Vector2(clip.x / clip.w, clip.y / clip.w);
            return new Vector2((ndc.x * 0.5f + 0.5f) * width, (ndc.y * 0.5f + 0.5f) * height);
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

        private static double SampleCenterBox(Color[] pixels, int width, int height, int box)
        {
            int cx = width / 2;
            int cy = height / 2;
            double sum = 0d;
            int count = 0;
            for (int y = cy - box / 2; y < cy + box / 2; ++y)
            {
                for (int x = cx - box / 2; x < cx + box / 2; ++x)
                {
                    if (x < 0 || x >= width || y < 0 || y >= height)
                        continue;
                    sum += pixels[y * width + x].r;
                    ++count;
                }
            }
            return count > 0 ? sum / count : double.NaN;
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
            var go = new GameObject("5F Play Framing Camera") { hideFlags = HideFlags.HideAndDontSave };
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

        private static Camera CreateOrthographicCamera(ICollection<UnityEngine.Object> temporary)
        {
            var go = new GameObject("5F Top Down Superseded Camera") { hideFlags = HideFlags.HideAndDontSave };
            temporary.Add(go);
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 2000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 1f, 1f, 1f);
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
                "WorldPosition:{0}; Normal:{1}; SlopeDegrees:{2:F3}; NeighbourMeanSlopeDegrees(4x4mCross):{3:F3}; SelectionMethod:MedianOfStoneDominant(>=0.5)SlopeAtLeast35DegCandidates; UV:({4:F6},{5:F6})",
                FormatVector(WorldPosition), FormatVector(Normal), SlopeDegrees, NeighbourMeanSlopeDegrees, U, V);
        }

        private readonly struct PixelDensity
        {
            public PixelDensity(float pixelsPerMetreUpSlope, float pixelsPerMetreAcrossSlope)
            {
                PixelsPerMetreUpSlope = pixelsPerMetreUpSlope;
                PixelsPerMetreAcrossSlope = pixelsPerMetreAcrossSlope;
            }

            public float PixelsPerMetreUpSlope { get; }
            public float PixelsPerMetreAcrossSlope { get; }
            public float MinPixelsPerMetre => Mathf.Min(PixelsPerMetreUpSlope, PixelsPerMetreAcrossSlope);
            public float MaxPixelsPerMetre => Mathf.Max(PixelsPerMetreUpSlope, PixelsPerMetreAcrossSlope);
            public float AnisotropyRatio => MinPixelsPerMetre > 1e-6f ? MaxPixelsPerMetre / MinPixelsPerMetre : float.PositiveInfinity;

            public override string ToString() => string.Format(Invariant,
                "PxPerMetreUpSlope:{0:F3}; PxPerMetreAcrossSlope:{1:F3}; AnisotropyRatio:{2:F3}",
                PixelsPerMetreUpSlope, PixelsPerMetreAcrossSlope, AnisotropyRatio);
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

        private readonly struct TileSizeCaptureResult
        {
            public TileSizeCaptureResult(float tileSize, string fileName, TerrainMaskMetrics mask, PixelDensity density)
            {
                TileSize = tileSize;
                FileName = fileName;
                Mask = mask;
                Density = density;
            }

            public float TileSize { get; }
            public string FileName { get; }
            private TerrainMaskMetrics Mask { get; }
            private PixelDensity Density { get; }

            public override string ToString()
            {
                float texelsPerMetre = BaseArrayTexelsPerTileMetre / TileSize;
                float mipUpSlope = Mathf.Log(Mathf.Max(texelsPerMetre / Mathf.Max(Density.PixelsPerMetreUpSlope, 1e-6f), 1e-6f), 2f);
                float mipAcrossSlope = Mathf.Log(Mathf.Max(texelsPerMetre / Mathf.Max(Density.PixelsPerMetreAcrossSlope, 1e-6f), 1e-6f), 2f);
                float repeatSpanPixelsUpSlope = TileSize * Density.PixelsPerMetreUpSlope;
                float repeatSpanPixelsAcrossSlope = TileSize * Density.PixelsPerMetreAcrossSlope;
                return string.Format(Invariant,
                    "File:{0}; {1}; {2}; BaseTexelsPerMetre:{3:F1}; EstimatedMipUpSlope:{4:F3}; EstimatedMipAcrossSlope:{5:F3}; OnScreenRepeatSpanPxUpSlope:{6:F1}; OnScreenRepeatSpanPxAcrossSlope:{7:F1}",
                    FileName, Mask, Density, texelsPerMetre, mipUpSlope, mipAcrossSlope, repeatSpanPixelsUpSlope, repeatSpanPixelsAcrossSlope);
            }
        }
    }
}
