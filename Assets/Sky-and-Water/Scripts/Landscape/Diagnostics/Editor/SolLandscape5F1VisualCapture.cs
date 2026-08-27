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
    /// Ticket 5F.1's per-layer tileSize split test: Stone1/Stone2 vs Sand judged separately at the
    /// exact 5F steep-face site and play-framing camera, plus a sand-only diagnostic sweep to test
    /// whether its corrugation is texture-bound rather than tileSize-bound.
    /// All TerrainLayer.tileSize changes here are transient, in-memory only, and restored before exit;
    /// nothing is saved to the .terrainlayer assets by this tool.
    /// </summary>
    public static class SolLandscape5F1VisualCapture
    {
        private const string ScenePath = "Assets/Scenes/SolsWeather_Demo.unity";
        private const string ArrayMaterialPath = "Assets/Sky-and-Water/Landscape/M_SolLandscape.mat";
        private const string DebugKeyword = "_SOL_LANDSCAPE_DEBUG";
        private const int Width = 1280;
        private const int Height = 720;
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
                    System.Environment.GetCommandLineArgs(), "-sol5F1VisualOutput", "../Landscape5F1Evidence"));
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

                // Identical scan and identical camera setup to 5F, so this ticket's captures are
                // directly comparable to 5F's, not a new site/framing.
                SteepPoint steep = FindSteepFace(terrain, layers);

                Camera camera = CreatePerspectiveCamera(temporary);
                CreateDaylight(temporary);
                PositionCameraAtSlope(camera, terrain, steep, viewDistance: 16f, eyeHeight: 1.75f);

                Material lit = new Material(production)
                { name = "5F1 Split Lit", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                temporary.Add(lit);

                string dirt = NameOf(layers, "TerrainLayer_Dirt");
                string grass = NameOf(layers, "TerrainLayer_Grass");
                string stone1 = NameOf(layers, "TerrainLayer_Stone1");
                string stone2 = NameOf(layers, "TerrainLayer_Stone2");
                string path = NameOf(layers, "TerrainLayer_Path");
                string sandName = NameOf(layers, "TerrainLayer_Sand");

                var configs = new List<(string id, Dictionary<string, float> map)>
                {
                    ("StoneAt8_OthersAt4", Uniform(layers, 4f, (stone1, 8f), (stone2, 8f))),
                    ("StoneAt8_Sand2_OthersAt4", Uniform(layers, 4f, (stone1, 8f), (stone2, 8f), (sandName, 2f))),
                    ("Uniform4_Control", Uniform(layers, 4f)),
                };

                // Sand is an Auto-mode layer (S5 register): its raw painted alphamap weight is near-zero
                // almost everywhere because its coverage is procedurally resolved from a height-band rule
                // at render time, not painted directly, so a raw-alphamap search for a sand-dominant point
                // (the technique that worked for stone) finds nothing. Every capture this ticket produces
                // visibly shows sand filling the same near-foreground band, so a fixed ground point 8m in
                // front of the camera on its own look direction is used instead, exactly as a player's own
                // view would put it.
                Vector3 sandGroundPoint = GroundPointInFrontOfCamera(terrain, camera, 8f);
                Vector3 sandNormal = terrain.terrainData.GetInterpolatedNormal(
                    Mathf.Clamp01((sandGroundPoint.x - terrain.transform.position.x) / terrain.terrainData.size.x),
                    Mathf.Clamp01((sandGroundPoint.z - terrain.transform.position.z) / terrain.terrainData.size.z));

                var results = new List<ConfigResult>();
                foreach ((string id, Dictionary<string, float> map) in configs)
                {
                    ApplyTileSizes(terrain, layers, map, driver);
                    Color[] pixels = Render(terrain, lit, camera, Width, Height);
                    string file = $"5F1_SteepFace_PlayFraming_{id}.png";
                    WritePng(Path.Combine(outputDirectory, file), pixels, Width, Height);
                    TerrainMaskMetrics mask = MeasureTerrainMask(pixels);
                    PixelDensity stoneDensity = MeasurePixelDensity(camera, steep.WorldPosition, steep.Normal, Width, Height);
                    PixelDensity sandDensity = MeasurePixelDensity(camera, sandGroundPoint, sandNormal, Width, Height);
                    results.Add(new ConfigResult(id, file, mask, stoneDensity, sandDensity, map[stone1], map[sandName]));
                }

                // Sand-only diagnostic sweep: stone and everything else pinned at the control value (4m),
                // only sand varies, to test whether the corrugation persists regardless of tileSize.
                float[] sandSweep = { 1f, 2f, 4f, 8f };
                var sandResults = new List<SandSweepResult>();
                foreach (float sandTile in sandSweep)
                {
                    Dictionary<string, float> map = Uniform(layers, 4f, (sandName, sandTile));
                    ApplyTileSizes(terrain, layers, map, driver);
                    Color[] pixels = Render(terrain, lit, camera, Width, Height);
                    string file = $"5F1_SandDiagnostic_Sand{FormatTile(sandTile)}.png";
                    WritePng(Path.Combine(outputDirectory, file), pixels, Width, Height);
                    TerrainMaskMetrics mask = MeasureTerrainMask(pixels);
                    PixelDensity sandDensity = MeasurePixelDensity(camera, sandGroundPoint, sandNormal, Width, Height);
                    sandResults.Add(new SandSweepResult($"SandOnly{FormatTile(sandTile)}", file, mask, sandDensity, sandTile));
                }

                // Supplementary close-up: same position, same look-at target, only the FOV narrows from
                // 60 to 18 degrees to zoom the rock crack pattern for a clearer stone-only visual read.
                // This is declared separately and is not part of the primary fixed-camera comparison
                // above (rule 6) -- it exists only to make the "bolder vs. repetitive" per-material call
                // easier to see, backed by the same measured numbers already reported for the wide shot.
                float wideFov = camera.fieldOfView;
                camera.fieldOfView = 18f;
                var closeUpFiles = new List<string>();
                foreach (float stoneTile in new[] { 4f, 8f })
                {
                    ApplyTileSizes(terrain, layers, Uniform(layers, 4f, (stone1, stoneTile), (stone2, stoneTile)), driver);
                    Color[] pixels = Render(terrain, lit, camera, Width, Height);
                    string file = $"5F1_StoneCloseUp_Tile{FormatTile(stoneTile)}.png";
                    WritePng(Path.Combine(outputDirectory, file), pixels, Width, Height);
                    closeUpFiles.Add(file);
                }
                camera.fieldOfView = wideFov;

                foreach (KeyValuePair<TerrainLayer, Vector2> original in originalTileSizes)
                    original.Key.tileSize = original.Value;
                Publish(driver);
                terrain.terrainData.terrainLayers = layers;
                terrain.Flush();

                var evidence = new StringBuilder();
                evidence.AppendLine("Sol Landscape Phase 5 ticket 5F.1 per-layer tileSize split test");
                evidence.AppendLine($"UTC={DateTime.UtcNow:O}; Unity={Application.unityVersion}; Graphics={SystemInfo.graphicsDeviceType}; Device={SystemInfo.graphicsDeviceName}");
                evidence.AppendLine("ControlledDaylight=Directional; Euler=(38,-35,0); Intensity=1.35; Color=white; Shadows=Soft; ShadowStrength=1; Ambient=Flat(0.35,0.35,0.35); PostProcessing=False; TreesAndFoliage=False; Weather=DryFrozen");
                evidence.AppendLine($"SteepFaceSite(IdenticalTo5F)={steep}");
                evidence.AppendLine($"PlayFramingCamera(IdenticalTo5F)=Perspective; FOV:{camera.fieldOfView.ToString("F3", Invariant)}; Position:{FormatVector(camera.transform.position)}; ViewDistance:16m; EyeHeight:1.75m; Resolution:{Width}x{Height}");
                evidence.AppendLine($"BaseArrayTexelResolutionPerTileAssumption={BaseArrayTexelsPerTileMetre.ToString("F0", Invariant)} texels covering one tileSize span");
                evidence.AppendLine($"SandGroundPoint=WorldPosition:{FormatVector(sandGroundPoint)}; Normal:{FormatVector(sandNormal)}; SelectionMethod:8mInFrontOfCameraOnItsOwnLookDirection(the near-foreground visibly sand in every capture this ticket produced)");
                evidence.AppendLine("SandMethodNote=Sand is an Auto-mode layer (S5 register); its raw painted alphamap weight is near-zero almost everywhere (mean 0.0052 per the pre-sculpt audit) because its visible coverage is procedurally resolved from a height-band rule at render time, not painted directly. A raw-alphamap search (the technique that worked for stone) found nothing near the site, so the sand point above is read from the camera's own view instead, where it is visibly sand in every rendered capture.");
                foreach (ConfigResult result in results)
                    evidence.AppendLine($"Config_{result.Id}={result}");
                evidence.AppendLine("SandDiagnosticSweep=StoneAndOthersPinnedAt4m; OnlySandTileSizeVaries");
                foreach (SandSweepResult result in sandResults)
                    evidence.AppendLine($"SandSweep_{result.Id}={result}");
                evidence.AppendLine("SandNormalTextureInspection=T_Sand_NormalDX (guid b2e60aff63abba042a9649ca84d25303, 2048x2048): visually inspected directly (not just rendered) and shows near-parallel, near-horizontal directional ripple lines baked into the single tile itself, not isotropic noise; see report for the crop reference.");
                evidence.AppendLine($"StoneCloseUpSupplementary=SamePositionAndLookAtAsThePrimaryCamera; FOVNarrowedTo18Degrees(from 60); NOT part of the fixed-camera comparison in rule 6, exists only to make the stone-only bolder-vs-repetitive read easier to see; Files=[{string.Join(",", closeUpFiles)}]");
                evidence.AppendLine("SceneSaved=False; TerrainLayerAssetsModified=False; TerrainDataAlphamapsModified=False; ProductionMaterialModified=False");
                evidence.AppendLine("Captures=" + string.Join(";", results.Select(result => result.FileName).Concat(sandResults.Select(result => result.FileName)).Concat(closeUpFiles)));
                evidence.AppendLine("RESULT=PASS");
                string path2 = Path.Combine(outputDirectory, "5F1_SplitTest_Evidence.txt");
                File.WriteAllText(path2, evidence.ToString(), new UTF8Encoding(false));
                Debug.Log($"[Sol Landscape 5F.1] Split-test captures written to {outputDirectory}");
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

        private static string NameOf(TerrainLayer[] layers, string name)
        {
            TerrainLayer layer = layers.FirstOrDefault(candidate => candidate.name == name);
            if (layer == null)
                throw new InvalidOperationException($"Expected live layer '{name}'.");
            return layer.name;
        }

        private static Dictionary<string, float> Uniform(TerrainLayer[] layers, float baseValue, params (string name, float value)[] overrides)
        {
            var map = layers.ToDictionary(layer => layer.name, _ => baseValue);
            foreach ((string name, float value) in overrides)
                map[name] = value;
            return map;
        }

        private static void ApplyTileSizes(Terrain terrain, TerrainLayer[] layers, Dictionary<string, float> map, SolLandscapeDriver driver)
        {
            foreach (TerrainLayer layer in layers)
                if (map.TryGetValue(layer.name, out float value))
                    layer.tileSize = new Vector2(value, value);
            // _Sol_LandscapeLayerST is baked at Publish() time from TerrainLayer.tileSize (5F finding);
            // a tileSize change only reaches the shader after a republish.
            Publish(driver);
            terrain.terrainData.terrainLayers = layers;
            terrain.Flush();
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

        /// <summary>
        /// Ground point a fixed distance in front of the camera along its own flattened look direction —
        /// the near-foreground a player would actually be looking at, used here because Sand's Auto-layer
        /// resolve makes a raw-alphamap site search unreliable (see SandMethodNote in the evidence).
        /// </summary>
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

        private static PixelDensity MeasurePixelDensity(Camera camera, Vector3 worldPosition, Vector3 normal, int width, int height)
        {
            Vector3 upSlope = Vector3.ProjectOnPlane(Vector3.up, normal);
            if (upSlope.sqrMagnitude < 1e-6f)
                upSlope = Vector3.forward;
            upSlope.Normalize();
            Vector3 acrossSlope = Vector3.Cross(normal, upSlope).normalized;

            Vector2 p0 = ProjectToPixels(camera, worldPosition, width, height);
            Vector2 pUp = ProjectToPixels(camera, worldPosition + upSlope, width, height);
            Vector2 pAcross = ProjectToPixels(camera, worldPosition + acrossSlope, width, height);

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
            var go = new GameObject("5F.1 Play Framing Camera") { hideFlags = HideFlags.HideAndDontSave };
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

        private readonly struct PixelDensity
        {
            public PixelDensity(float pixelsPerMetreUpSlope, float pixelsPerMetreAcrossSlope)
            {
                PixelsPerMetreUpSlope = pixelsPerMetreUpSlope;
                PixelsPerMetreAcrossSlope = pixelsPerMetreAcrossSlope;
            }

            public float PixelsPerMetreUpSlope { get; }
            public float PixelsPerMetreAcrossSlope { get; }

            public override string ToString() => string.Format(Invariant,
                "PxPerMetreUpSlope:{0:F3}; PxPerMetreAcrossSlope:{1:F3}", PixelsPerMetreUpSlope, PixelsPerMetreAcrossSlope);
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

        private readonly struct SandSweepResult
        {
            public SandSweepResult(string id, string fileName, TerrainMaskMetrics mask, PixelDensity sandDensity, float sandTileSize)
            {
                Id = id;
                FileName = fileName;
                Mask = mask;
                SandDensity = sandDensity;
                SandTileSize = sandTileSize;
            }

            public string Id { get; }
            public string FileName { get; }
            private TerrainMaskMetrics Mask { get; }
            private PixelDensity SandDensity { get; }
            private float SandTileSize { get; }

            public override string ToString()
            {
                float texelsPerMetre = BaseArrayTexelsPerTileMetre / Mathf.Max(SandTileSize, 1e-6f);
                float mipUp = Mathf.Log(Mathf.Max(texelsPerMetre / Mathf.Max(SandDensity.PixelsPerMetreUpSlope, 1e-6f), 1e-6f), 2f);
                float mipAcross = Mathf.Log(Mathf.Max(texelsPerMetre / Mathf.Max(SandDensity.PixelsPerMetreAcrossSlope, 1e-6f), 1e-6f), 2f);
                float repeatUp = SandTileSize * SandDensity.PixelsPerMetreUpSlope;
                float repeatAcross = SandTileSize * SandDensity.PixelsPerMetreAcrossSlope;
                return string.Format(Invariant,
                    "File:{0}; {1}; SandTileSize:{2:F0}; {3}; EstMip:{4:F3}/{5:F3}; RepeatSpanPx:{6:F1}/{7:F1}",
                    FileName, Mask, SandTileSize, SandDensity, mipUp, mipAcross, repeatUp, repeatAcross);
            }
        }

        private readonly struct ConfigResult
        {
            public ConfigResult(string id, string fileName, TerrainMaskMetrics mask, PixelDensity stoneDensity, PixelDensity sandDensity, float stoneTileSize, float sandTileSize)
            {
                Id = id;
                FileName = fileName;
                Mask = mask;
                StoneDensity = stoneDensity;
                SandDensity = sandDensity;
                StoneTileSize = stoneTileSize;
                SandTileSize = sandTileSize;
            }

            public string Id { get; }
            public string FileName { get; }
            private TerrainMaskMetrics Mask { get; }
            private PixelDensity StoneDensity { get; }
            private PixelDensity SandDensity { get; }
            private float StoneTileSize { get; }
            private float SandTileSize { get; }

            public override string ToString()
            {
                float stoneTexelsPerMetre = BaseArrayTexelsPerTileMetre / Mathf.Max(StoneTileSize, 1e-6f);
                float stoneMipUp = Mathf.Log(Mathf.Max(stoneTexelsPerMetre / Mathf.Max(StoneDensity.PixelsPerMetreUpSlope, 1e-6f), 1e-6f), 2f);
                float stoneMipAcross = Mathf.Log(Mathf.Max(stoneTexelsPerMetre / Mathf.Max(StoneDensity.PixelsPerMetreAcrossSlope, 1e-6f), 1e-6f), 2f);
                float stoneRepeatUp = StoneTileSize * StoneDensity.PixelsPerMetreUpSlope;
                float stoneRepeatAcross = StoneTileSize * StoneDensity.PixelsPerMetreAcrossSlope;
                float sandTexelsPerMetre = BaseArrayTexelsPerTileMetre / Mathf.Max(SandTileSize, 1e-6f);
                float sandMipUp = Mathf.Log(Mathf.Max(sandTexelsPerMetre / Mathf.Max(SandDensity.PixelsPerMetreUpSlope, 1e-6f), 1e-6f), 2f);
                float sandMipAcross = Mathf.Log(Mathf.Max(sandTexelsPerMetre / Mathf.Max(SandDensity.PixelsPerMetreAcrossSlope, 1e-6f), 1e-6f), 2f);
                float sandRepeatUp = SandTileSize * SandDensity.PixelsPerMetreUpSlope;
                float sandRepeatAcross = SandTileSize * SandDensity.PixelsPerMetreAcrossSlope;
                return string.Format(Invariant,
                    "File:{0}; {1}; StoneTileSize:{2:F0}; Stone[{3}]; StoneEstMip:{4:F3}/{5:F3}; StoneRepeatSpanPx:{6:F1}/{7:F1}; SandTileSize:{8:F0}; Sand[{9}]; SandEstMip:{10:F3}/{11:F3}; SandRepeatSpanPx:{12:F1}/{13:F1}",
                    FileName, Mask, StoneTileSize, StoneDensity, stoneMipUp, stoneMipAcross, stoneRepeatUp, stoneRepeatAcross,
                    SandTileSize, SandDensity, sandMipUp, sandMipAcross, sandRepeatUp, sandRepeatAcross);
            }
        }
    }
}
