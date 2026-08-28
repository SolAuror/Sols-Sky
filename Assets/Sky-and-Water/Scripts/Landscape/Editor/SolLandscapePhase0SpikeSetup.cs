using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Sol.Landscape.Editor
{
    internal static class SolLandscapePhase0SpikeSetup
    {
        private const string TargetScenePath = "Assets/Scenes/Sols_Water2_Demo.unity";
        private const string TargetTerrainPath = "Assets/Scenes/SolsWeather_Demo/DemoTerrain.asset";
        private const string OriginalMaterialPath = "Assets/Sky-and-Water/Shaders/Terrain/M_TerrainWetness.mat";
        private const string ShaderPath = "Assets/Sky-and-Water/Shaders/Terrain/Phase0/Sol.LandscapePhase0Spike.shader";
        private const string ShaderName = "Hidden/Sol/Landscape/Phase 0 Spike";
        private const int EvidenceResolution = 512;
        private const int TerrainPatchQuads = 64;
        private const float CpuPaintedThreshold = 0.0001f;
        private const byte ImageVisibleThreshold = 4;

        private static Round4Runner activeRunner;

        private static readonly string[] ExpectedLayerPaths =
        {
            "Assets/Terrain/TerrainLayer_Dirt.terrainlayer",
            "Assets/Terrain/TerrainLayer_Grass.terrainlayer",
            "Assets/Terrain/TerrainLayer_Stone2.terrainlayer",
            "Assets/Terrain/TerrainLayer_Stone1.terrainlayer",
            "Assets/Terrain/TerrainLayer_Path.terrainlayer",
            "Assets/Terrain/TerrainLayer_Sand.terrainlayer"
        };

        private static readonly Color32[] DiagnosticColors =
        {
            new Color32(255, 32, 32, 255),
            new Color32(32, 255, 32, 255),
            new Color32(32, 96, 255, 255),
            new Color32(255, 32, 255, 255),
            new Color32(255, 224, 32, 255),
            new Color32(32, 255, 255, 255)
        };

        [MenuItem("Tools/Sol Landscape/Phase 0/Install Diagnostic Spike")]
        private static void InstallFromMenu()
        {
            StartRound4(exitEditorWhenComplete: false);
        }

        [MenuItem("Tools/Sol Landscape/Phase 0/Validate Diagnostic Spike")]
        private static void ValidateFromMenu()
        {
            StartRound4(exitEditorWhenComplete: false);
        }

        [MenuItem("Tools/Sol Landscape/Phase 0/Capture Diagnostic Evidence")]
        private static void CaptureFromMenu()
        {
            StartRound4(exitEditorWhenComplete: false);
        }

        [MenuItem("Tools/Sol Landscape/Phase 0/Restore Original Material")]
        private static void RestoreFromMenu()
        {
            Scene scene = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Single);
            Terrain terrain = FindTargetTerrain(scene);
            Material originalMaterial = AssetDatabase.LoadAssetAtPath<Material>(OriginalMaterialPath);
            if (originalMaterial == null)
            {
                throw new InvalidOperationException($"Missing original terrain material at {OriginalMaterialPath}.");
            }

            terrain.materialTemplate = originalMaterial;
            Debug.Log("[Sol Landscape Phase 0] Restored the original DemoTerrain material in memory.");
        }

        public static void InstallAndValidateFromCommandLine()
        {
            StartRound4(exitEditorWhenComplete: true);
        }

        private static void StartRound4(bool exitEditorWhenComplete)
        {
            if (activeRunner != null)
            {
                throw new InvalidOperationException("A Phase 0 Round 4 run is already active.");
            }

            activeRunner = new Round4Runner(exitEditorWhenComplete);
            EditorApplication.update += TickRound4;
            Debug.Log("[Sol Landscape Phase 0] Round 4 scheduled on EditorApplication.update.");
        }

        private static void TickRound4()
        {
            Round4Runner runner = activeRunner;
            if (runner == null)
            {
                EditorApplication.update -= TickRound4;
                return;
            }

            bool completed = false;
            bool passed = false;
            try
            {
                completed = runner.Tick();
                passed = completed;
            }
            catch (Exception exception)
            {
                completed = true;
                Debug.LogException(exception);
            }

            if (!completed)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                return;
            }

            EditorApplication.update -= TickRound4;
            activeRunner = null;
            try
            {
                runner.Dispose();
            }
            catch (Exception cleanupException)
            {
                passed = false;
                Debug.LogException(cleanupException);
            }

            string resultName = runner.ExitEditorWhenComplete ? "COMMAND_LINE_RESULT" : "MENU_RESULT";
            if (passed)
            {
                Debug.Log($"[Sol Landscape Phase 0] {resultName}=PASS");
            }
            else
            {
                Debug.LogError($"[Sol Landscape Phase 0] {resultName}=FAIL");
            }

            if (runner.ExitEditorWhenComplete)
            {
                EditorApplication.Exit(passed ? 0 : 1);
            }
        }

        private static Material CreateSpikeMaterial(TerrainData terrainData, out Texture2DArray array)
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null || shader.name != ShaderName)
            {
                throw new InvalidOperationException($"Phase 0 shader is missing or has the wrong name at {ShaderPath}.");
            }

            array = new Texture2DArray(1, 1, DiagnosticColors.Length, TextureFormat.RGBA32, false, true)
            {
                name = "SolLandscapePhase0DiagnosticArray",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0
            };

            for (int slice = 0; slice < DiagnosticColors.Length; ++slice)
            {
                array.SetPixels32(new[] { DiagnosticColors[slice] }, slice, 0);
            }

            array.Apply(updateMipmaps: false, makeNoLongerReadable: false);

            var material = new Material(shader)
            {
                name = "M_SolLandscapePhase0Spike",
                hideFlags = HideFlags.HideAndDontSave,
                enableInstancing = false
            };
            material.SetTexture("_SolPhase0_Control0", terrainData.GetAlphamapTexture(0));
            material.SetTexture("_SolPhase0_Control1", terrainData.GetAlphamapTexture(1));
            material.SetTexture("_SolPhase0_LayerArray", array);
            material.SetFloat("_SolPhase0_DebugMode", 0.0f);
            material.SetFloat("_SolPhase0_ArraySlice", 0.0f);
            material.SetFloat("_SolPhase0_DebugLayer", 0.0f);
            return material;
        }

        private static void ValidateInstalledSpike(Terrain terrain, Material material)
        {
            var failures = new List<string>();
            try
            {
                ValidateTerrainData(terrain.terrainData);
            }
            catch (Exception exception)
            {
                failures.Add(exception.Message);
            }

            if (terrain.materialTemplate != material)
            {
                failures.Add("DemoTerrain is not assigned the Phase 0 material in memory.");
            }

            if (material.GetTexture("_SolPhase0_Control0") != terrain.terrainData.GetAlphamapTexture(0))
            {
                failures.Add("Control 0 is not terrainData.GetAlphamapTexture(0).");
            }

            if (material.GetTexture("_SolPhase0_Control1") != terrain.terrainData.GetAlphamapTexture(1))
            {
                failures.Add("Control 1 is not terrainData.GetAlphamapTexture(1).");
            }

            var array = material.GetTexture("_SolPhase0_LayerArray") as Texture2DArray;
            if (array == null || array.depth != ExpectedLayerPaths.Length)
            {
                failures.Add("The diagnostic Texture2DArray is missing or does not have six slices.");
            }

            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
            {
                failures.Add("The Phase 0 shader asset is missing.");
            }
            else
            {
                string[] errors = ShaderUtil.GetShaderMessages(shader)
                    .Where(message => message.severity == ShaderCompilerMessageSeverity.Error)
                    .Select(message => message.message)
                    .ToArray();
                if (errors.Length > 0)
                {
                    failures.Add("Shader compile errors: " + string.Join(" | ", errors));
                }

                if (!shader.isSupported)
                {
                    failures.Add("The Phase 0 shader reports isSupported=false on the active graphics API.");
                }
            }

            string source = File.ReadAllText(Path.GetFullPath(ShaderPath));
            if (source.IndexOf("Dependency \"AddPassShader\"", StringComparison.Ordinal) >= 0)
            {
                failures.Add("The Phase 0 shader unexpectedly declares an AddPassShader dependency.");
            }

            if (source.IndexOf("#pragma target 4.5", StringComparison.Ordinal) < 0)
            {
                failures.Add("The Phase 0 shader is not targeting shader model 4.5.");
            }

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(string.Join(System.Environment.NewLine + "- ", failures));
            }

            Debug.Log(
                "[Sol Landscape Phase 0] Static validation PASS: exact layer order, two manual control bindings, " +
                "six array slices, target 4.5, supported shader, and no AddPassShader dependency.");
        }

        private static void CaptureIndependentEvidence(Terrain terrain, Material spikeMaterial)
        {
            string evidenceDirectory = GetEvidenceDirectory();
            var cameraObject = new GameObject("Sol Landscape Phase 0 Evidence Camera")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            var camera = cameraObject.AddComponent<Camera>();
            var renderTexture = new RenderTexture(
                EvidenceResolution,
                EvidenceResolution,
                24,
                RenderTextureFormat.ARGB32)
            {
                name = "Sol Landscape Phase 0 Evidence"
            };
            var readback = new Texture2D(
                EvidenceResolution,
                EvidenceResolution,
                TextureFormat.RGB24,
                false,
                true);

            int originalLayer = terrain.gameObject.layer;
            float originalBasemapDistance = terrain.basemapDistance;
            bool originalDrawInstanced = terrain.drawInstanced;
            bool originalSpikeEnableInstancing = spikeMaterial.enableInstancing;
            Material originalTemplate = terrain.materialTemplate;
            float originalDebugMode = spikeMaterial.GetFloat("_SolPhase0_DebugMode");
            float originalArraySlice = spikeMaterial.GetFloat("_SolPhase0_ArraySlice");
            float originalDebugLayer = spikeMaterial.GetFloat("_SolPhase0_DebugLayer");
            RenderTexture previousActive = RenderTexture.active;

            try
            {
                ConfigureEvidenceCamera(camera, renderTexture, terrain);
                terrain.gameObject.layer = 31;
                terrain.basemapDistance = float.MaxValue;

                terrain.materialTemplate = spikeMaterial;
                spikeMaterial.SetFloat("_SolPhase0_DebugMode", 0.0f);
                terrain.drawInstanced = false;
                spikeMaterial.enableInstancing = false;
                CaptureStats nonInstanced = SaveCapture(
                    camera,
                    renderTexture,
                    readback,
                    Path.Combine(evidenceDirectory, "Phase0_AllSixLayerBlend_NonInstanced.png"));
                RequireVisiblePixels("non-instanced all-six blend", nonInstanced);

                terrain.drawInstanced = true;
                spikeMaterial.enableInstancing = true;
                CaptureStats instanced = SaveCapture(
                    camera,
                    renderTexture,
                    readback,
                    Path.Combine(evidenceDirectory, "Phase0_AllSixLayerBlend_Instanced.png"));
                RequireVisiblePixels("instanced all-six blend", instanced);

                ImageComparison instancingComparison = CompareCaptures(nonInstanced, instanced);
                int nonInstancedRegions = CountDistinctDiagnosticRegions(nonInstanced.Pixels);
                int instancedRegions = CountDistinctDiagnosticRegions(instanced.Pixels);
                int nonInstancedDistinctColors = CountDistinctNonBlackColors(nonInstanced.Pixels);
                int instancedDistinctColors = CountDistinctNonBlackColors(instanced.Pixels);
                WriteInstancingEvidence(
                    evidenceDirectory,
                    nonInstanced,
                    instanced,
                    instancingComparison,
                    nonInstancedRegions,
                    instancedRegions,
                    nonInstancedDistinctColors,
                    instancedDistinctColors);
                ValidateInstancingComparison(
                    nonInstanced,
                    instanced,
                    instancingComparison,
                    nonInstancedRegions,
                    instancedRegions,
                    nonInstancedDistinctColors,
                    instancedDistinctColors);

                terrain.drawInstanced = false;
                spikeMaterial.enableInstancing = false;
                CpuGroundTruth cpuGroundTruth = AnalyzeCpuAlphamaps(terrain.terrainData);
                WriteCpuEvidence(evidenceDirectory, cpuGroundTruth);

                var layerCaptures = new CaptureStats[ExpectedLayerPaths.Length];
                for (int layer = 0; layer < ExpectedLayerPaths.Length; ++layer)
                {
                    spikeMaterial.SetFloat("_SolPhase0_DebugMode", 2.0f);
                    spikeMaterial.SetFloat("_SolPhase0_DebugLayer", layer);
                    string layerName = GetLayerName(layer);
                    layerCaptures[layer] = SaveCapture(
                        camera,
                        renderTexture,
                        readback,
                        Path.Combine(evidenceDirectory, $"Phase0_Round3_Layer{layer}_{layerName}_Weight.png"));
                    RequireVisiblePixels($"layer {layer} ({layerName}) weight", layerCaptures[layer]);
                }

                LogCpuGpuComparison(cpuGroundTruth, layerCaptures);
                CaptureWeakestLayerCloseup(
                    camera,
                    renderTexture,
                    readback,
                    terrain,
                    spikeMaterial,
                    cpuGroundTruth,
                    evidenceDirectory);

                spikeMaterial.SetFloat("_SolPhase0_DebugMode", 3.0f);
                CaptureStats divergent = SaveCapture(
                    camera,
                    renderTexture,
                    readback,
                    Path.Combine(evidenceDirectory, "Phase0_DivergentDominantSlice.png"));
                RequireVisiblePixels("divergent dominant-layer array index", divergent);
                int distinctDivergentColors = CountDistinctDiagnosticRegions(divergent.Pixels);
                File.WriteAllLines(
                    Path.Combine(evidenceDirectory, "Phase0_DivergentDominantSlice.txt"),
                    new[]
                    {
                        $"Non-black pixels: {divergent.NonBlackPixelCount}",
                        $"Maximum channel: {divergent.MaximumChannel}",
                        $"Distinct diagnostic regions: {distinctDivergentColors}"
                    });
                if (distinctDivergentColors < 2)
                {
                    throw new InvalidOperationException(
                        $"Divergent array-index capture exposed only {distinctDivergentColors} diagnostic colour region(s).");
                }

                Debug.Log(
                    $"[Sol Landscape Phase 0] Divergent array-index capture PASS: " +
                    $"distinctDiagnosticRegions={distinctDivergentColors}.");
            }
            finally
            {
                RenderTexture.active = previousActive;
                terrain.materialTemplate = originalTemplate;
                terrain.drawInstanced = originalDrawInstanced;
                terrain.gameObject.layer = originalLayer;
                terrain.basemapDistance = originalBasemapDistance;
                spikeMaterial.enableInstancing = originalSpikeEnableInstancing;
                spikeMaterial.SetFloat("_SolPhase0_DebugMode", originalDebugMode);
                spikeMaterial.SetFloat("_SolPhase0_ArraySlice", originalArraySlice);
                spikeMaterial.SetFloat("_SolPhase0_DebugLayer", originalDebugLayer);
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(readback);
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        private static void ConfigureEvidenceCamera(Camera camera, RenderTexture renderTexture, Terrain terrain)
        {
            Vector3 terrainPosition = terrain.transform.position;
            Vector3 terrainSize = terrain.terrainData.size;
            camera.transform.position = terrainPosition + new Vector3(
                terrainSize.x * 0.5f,
                terrainSize.y + 100.0f,
                terrainSize.z * 0.5f);
            camera.transform.rotation = Quaternion.Euler(90.0f, 0.0f, 0.0f);
            camera.orthographic = true;
            camera.orthographicSize = Mathf.Max(terrainSize.x, terrainSize.z) * 0.5f;
            camera.nearClipPlane = 1.0f;
            camera.farClipPlane = terrainSize.y + 250.0f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = 1 << 31;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.targetTexture = renderTexture;
        }

        private static CpuGroundTruth AnalyzeCpuAlphamaps(TerrainData terrainData)
        {
            int width = terrainData.alphamapWidth;
            int height = terrainData.alphamapHeight;
            float[,,] alphamaps = terrainData.GetAlphamaps(0, 0, width, height);
            var layers = new CpuLayerStats[ExpectedLayerPaths.Length];

            for (int layer = 0; layer < layers.Length; ++layer)
            {
                float maximumWeight = 0.0f;
                int maximumX = 0;
                int maximumY = 0;
                long coveredPixels = 0;
                double weightSum = 0.0;

                for (int y = 0; y < height; ++y)
                {
                    for (int x = 0; x < width; ++x)
                    {
                        float weight = alphamaps[y, x, layer];
                        weightSum += weight;
                        if (weight > CpuPaintedThreshold)
                        {
                            ++coveredPixels;
                        }

                        if (weight > maximumWeight)
                        {
                            maximumWeight = weight;
                            maximumX = x;
                            maximumY = y;
                        }
                    }
                }

                layers[layer] = new CpuLayerStats(
                    layer,
                    GetLayerName(layer),
                    maximumWeight,
                    maximumX,
                    maximumY,
                    coveredPixels,
                    coveredPixels * 100.0 / (width * (double)height),
                    weightSum / (width * (double)height));

                if (maximumWeight <= 0.0f || coveredPixels == 0)
                {
                    throw new InvalidOperationException($"CPU alphamap layer {layer} contains no authored weight.");
                }
            }

            CpuLayerStats weakest = layers.OrderBy(layer => layer.CoveredPixels).First();
            Debug.Log(
                $"[Sol Landscape Phase 0] CPU alphamap ground truth: resolution={width}x{height}, " +
                $"threshold={CpuPaintedThreshold}, weakestLayer={weakest.LayerIndex} ({weakest.LayerName}).");
            foreach (CpuLayerStats layer in layers)
            {
                Debug.Log(
                    $"[Sol Landscape Phase 0] CPU layer {layer.LayerIndex} ({layer.LayerName}): " +
                    $"maxWeight={layer.MaximumWeight:F6}, max=({layer.MaximumX},{layer.MaximumY}), " +
                    $"coveragePixels={layer.CoveredPixels}, coveragePercent={layer.CoveragePercent:F6}, " +
                    $"meanWeight={layer.MeanWeight:F8}.");
            }

            return new CpuGroundTruth(width, height, layers, weakest.LayerIndex);
        }

        private static void WriteCpuEvidence(string evidenceDirectory, CpuGroundTruth groundTruth)
        {
            var lines = new List<string>
            {
                $"Resolution: {groundTruth.Width}x{groundTruth.Height}",
                $"Coverage threshold: {CpuPaintedThreshold}",
                $"Weakest layer: {groundTruth.WeakestLayerIndex} ({GetLayerName(groundTruth.WeakestLayerIndex)})"
            };
            lines.AddRange(groundTruth.Layers.Select(layer =>
                $"Layer {layer.LayerIndex} {layer.LayerName}: maxWeight={layer.MaximumWeight:F6}, " +
                $"max=({layer.MaximumX},{layer.MaximumY}), coveragePixels={layer.CoveredPixels}, " +
                $"coveragePercent={layer.CoveragePercent:F6}, meanWeight={layer.MeanWeight:F8}"));
            File.WriteAllLines(Path.Combine(evidenceDirectory, "Phase0_CPUAlphamapGroundTruth.txt"), lines);
        }

        private static void LogCpuGpuComparison(CpuGroundTruth cpu, CaptureStats[] gpu)
        {
            string cpuRank = string.Join(
                ",",
                cpu.Layers.OrderByDescending(layer => layer.CoveredPixels).Select(layer => layer.LayerIndex));
            string gpuRank = string.Join(
                ",",
                Enumerable.Range(0, gpu.Length).OrderByDescending(layer => gpu[layer].NonBlackPixelCount));
            Debug.Log(
                $"[Sol Landscape Phase 0] CPU/GPU coverage rank descending: CPU={cpuRank}; GPU={gpuRank}.");

            for (int layer = 0; layer < gpu.Length; ++layer)
            {
                double gpuCoveragePercent = gpu[layer].NonBlackPixelCount * 100.0 / gpu[layer].Pixels.Length;
                Debug.Log(
                    $"[Sol Landscape Phase 0] CPU/GPU layer {layer} ({GetLayerName(layer)}): " +
                    $"cpuCoveragePercent={cpu.Layers[layer].CoveragePercent:F6}, " +
                    $"cpuMaxWeight={cpu.Layers[layer].MaximumWeight:F6}, " +
                    $"gpuCoveragePercent={gpuCoveragePercent:F6}, gpuMaxChannel={gpu[layer].MaximumChannel}.");
            }
        }

        private static void CaptureWeakestLayerCloseup(
            Camera camera,
            RenderTexture renderTexture,
            Texture2D readback,
            Terrain terrain,
            Material material,
            CpuGroundTruth cpu,
            string evidenceDirectory)
        {
            CpuLayerStats weakest = cpu.Layers[cpu.WeakestLayerIndex];
            Vector3 originalPosition = camera.transform.position;
            float originalOrthographicSize = camera.orthographicSize;
            try
            {
                Vector3 terrainPosition = terrain.transform.position;
                Vector3 terrainSize = terrain.terrainData.size;
                float normalizedX = (weakest.MaximumX + 0.5f) / cpu.Width;
                float normalizedZ = (weakest.MaximumY + 0.5f) / cpu.Height;
                camera.transform.position = new Vector3(
                    terrainPosition.x + normalizedX * terrainSize.x,
                    originalPosition.y,
                    terrainPosition.z + normalizedZ * terrainSize.z);
                camera.orthographicSize = Mathf.Max(
                    8.0f,
                    Mathf.Min(terrainSize.x, terrainSize.z) / 64.0f);

                material.SetFloat("_SolPhase0_DebugMode", 2.0f);
                material.SetFloat("_SolPhase0_DebugLayer", weakest.LayerIndex);
                CaptureStats closeup = SaveCapture(
                    camera,
                    renderTexture,
                    readback,
                    Path.Combine(
                        evidenceDirectory,
                        $"Phase0_WeakestLayerCloseup_{weakest.LayerIndex}_{weakest.LayerName}.png"));
                RequireVisiblePixels("weakest-layer close-up", closeup);
                byte expectedPeakChannel = LinearByteToSrgb((byte)Mathf.Clamp(
                    Mathf.RoundToInt(weakest.MaximumWeight * 255.0f),
                    0,
                    255));
                int minimumPeakChannel = Math.Max(16, expectedPeakChannel - 8);
                if (closeup.NonBlackPixelCount < 256 || closeup.MaximumChannel < minimumPeakChannel)
                {
                    throw new InvalidOperationException(
                        $"Weakest-layer close-up is not plainly visible: nonBlackPixels={closeup.NonBlackPixelCount}, " +
                        $"maxChannel={closeup.MaximumChannel}, expectedPeakChannel={expectedPeakChannel}, " +
                        $"minimumPeakChannel={minimumPeakChannel}.");
                }

                Debug.Log(
                    $"[Sol Landscape Phase 0] Weakest-layer close-up PASS: layer={weakest.LayerIndex} " +
                    $"({weakest.LayerName}), CPUmax=({weakest.MaximumX},{weakest.MaximumY}), " +
                    $"orthographicSize={camera.orthographicSize:F4}, nonBlackPixels={closeup.NonBlackPixelCount}, " +
                    $"maxChannel={closeup.MaximumChannel}, expectedPeakChannel={expectedPeakChannel}, " +
                    $"minimumPeakChannel={minimumPeakChannel}.");
            }
            finally
            {
                camera.transform.position = originalPosition;
                camera.orthographicSize = originalOrthographicSize;
            }
        }

        private static ImageComparison CompareCaptures(CaptureStats expected, CaptureStats actual)
        {
            if (expected.Pixels.Length != actual.Pixels.Length)
            {
                throw new InvalidOperationException("Instancing captures have different pixel counts.");
            }

            long absoluteChannelDifference = 0;
            int differingPixels = 0;
            byte maximumChannelDifference = 0;
            for (int index = 0; index < expected.Pixels.Length; ++index)
            {
                Color32 a = expected.Pixels[index];
                Color32 b = actual.Pixels[index];
                byte redDifference = (byte)Math.Abs(a.r - b.r);
                byte greenDifference = (byte)Math.Abs(a.g - b.g);
                byte blueDifference = (byte)Math.Abs(a.b - b.b);
                byte pixelMaximum = Math.Max(redDifference, Math.Max(greenDifference, blueDifference));
                maximumChannelDifference = Math.Max(maximumChannelDifference, pixelMaximum);
                absoluteChannelDifference += redDifference + greenDifference + blueDifference;
                if (pixelMaximum > 8)
                {
                    ++differingPixels;
                }
            }

            return new ImageComparison(
                absoluteChannelDifference / (expected.Pixels.Length * 3.0),
                maximumChannelDifference,
                differingPixels,
                differingPixels * 100.0 / expected.Pixels.Length);
        }

        private static void ValidateInstancingComparison(
            CaptureStats nonInstanced,
            CaptureStats instanced,
            ImageComparison comparison,
            int nonInstancedRegions,
            int instancedRegions,
            int nonInstancedDistinctColors,
            int instancedDistinctColors)
        {
            bool nonBlackCollapse = instanced.NonBlackPixelCount == 0 ||
                instanced.NonBlackPixelCount < nonInstanced.NonBlackPixelCount * 0.5;
            bool uniformImage = instancedDistinctColors <= 1;
            bool diagnosticRegionMismatch = instancedRegions != nonInstancedRegions;
            if (nonBlackCollapse || uniformImage || diagnosticRegionMismatch)
            {
                throw new InvalidOperationException(
                    $"Instanced terrain catastrophic signature: nonBlackCollapse={nonBlackCollapse}, " +
                    $"uniformImage={uniformImage}, diagnosticRegionMismatch={diagnosticRegionMismatch}, " +
                    $"nonInstancedNonBlack={nonInstanced.NonBlackPixelCount}, " +
                    $"instancedNonBlack={instanced.NonBlackPixelCount}, " +
                    $"nonInstancedRegions={nonInstancedRegions}, instancedRegions={instancedRegions}, " +
                    $"nonInstancedDistinctColors={nonInstancedDistinctColors}, " +
                    $"instancedDistinctColors={instancedDistinctColors}.");
            }

            Debug.Log(
                $"[Sol Landscape Phase 0] Instanced terrain comparison PASS: " +
                $"meanAbsoluteChannelDifference={comparison.MeanAbsoluteChannelDifference:F6}, " +
                $"differingPixels={comparison.DifferingPixels}, " +
                $"differingPixelPercent={comparison.DifferingPixelPercent:F6}, " +
                $"maxChannelDifference={comparison.MaximumChannelDifference}, " +
                $"nonInstancedRegions={nonInstancedRegions}, instancedRegions={instancedRegions}, " +
                $"nonInstancedDistinctColors={nonInstancedDistinctColors}, " +
                $"instancedDistinctColors={instancedDistinctColors}.");
        }

        private static void WriteInstancingEvidence(
            string evidenceDirectory,
            CaptureStats nonInstanced,
            CaptureStats instanced,
            ImageComparison comparison,
            int nonInstancedRegions,
            int instancedRegions,
            int nonInstancedDistinctColors,
            int instancedDistinctColors)
        {
            File.WriteAllLines(
                Path.Combine(evidenceDirectory, "Phase0_InstancingComparison.txt"),
                new[]
                {
                    $"Non-instanced nonBlackPixels: {nonInstanced.NonBlackPixelCount}",
                    $"Non-instanced maxChannel: {nonInstanced.MaximumChannel}",
                    $"Instanced nonBlackPixels: {instanced.NonBlackPixelCount}",
                    $"Instanced maxChannel: {instanced.MaximumChannel}",
                    $"Non-instanced diagnostic regions: {nonInstancedRegions}",
                    $"Instanced diagnostic regions: {instancedRegions}",
                    $"Non-instanced distinct non-black colours: {nonInstancedDistinctColors}",
                    $"Instanced distinct non-black colours: {instancedDistinctColors}",
                    $"Mean absolute channel difference: {comparison.MeanAbsoluteChannelDifference:F6}",
                    $"Maximum channel difference: {comparison.MaximumChannelDifference}",
                    $"Differing pixels (>8): {comparison.DifferingPixels}",
                    $"Differing pixel percent: {comparison.DifferingPixelPercent:F6}"
                });
        }

        private static int CountDistinctNonBlackColors(Color32[] pixels)
        {
            var colors = new HashSet<int>();
            for (int index = 0; index < pixels.Length; ++index)
            {
                Color32 pixel = pixels[index];
                if (Math.Max(pixel.r, Math.Max(pixel.g, pixel.b)) <= ImageVisibleThreshold)
                {
                    continue;
                }

                colors.Add((pixel.r << 16) | (pixel.g << 8) | pixel.b);
            }

            return colors.Count;
        }

        private static int CountDistinctDiagnosticRegions(Color32[] pixels)
        {
            int represented = 0;
            foreach (Color32 diagnostic in DiagnosticColors)
            {
                Color32 expectedCaptureColor = new Color32(
                    LinearByteToSrgb(diagnostic.r),
                    LinearByteToSrgb(diagnostic.g),
                    LinearByteToSrgb(diagnostic.b),
                    diagnostic.a);
                int matchingPixels = 0;
                for (int index = 0; index < pixels.Length; ++index)
                {
                    Color32 pixel = pixels[index];
                    if (Math.Abs(pixel.r - expectedCaptureColor.r) <= 8 &&
                        Math.Abs(pixel.g - expectedCaptureColor.g) <= 8 &&
                        Math.Abs(pixel.b - expectedCaptureColor.b) <= 8)
                    {
                        ++matchingPixels;
                    }
                }

                if (matchingPixels > 16)
                {
                    ++represented;
                }
            }

            return represented;
        }

        private static byte LinearByteToSrgb(byte value)
        {
            return (byte)Mathf.Clamp(
                Mathf.RoundToInt(Mathf.LinearToGammaSpace(value / 255f) * 255f),
                0,
                255);
        }

        private static CaptureStats SaveCapture(
            Camera camera,
            RenderTexture renderTexture,
            Texture2D readback,
            string outputPath)
        {
            camera.Render();
            RenderTexture.active = renderTexture;
            readback.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0, false);
            readback.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            File.WriteAllBytes(outputPath, readback.EncodeToPNG());

            Color32[] pixels = readback.GetPixels32();
            int nonBlackPixelCount = 0;
            byte maximumChannel = 0;
            for (int index = 0; index < pixels.Length; ++index)
            {
                Color32 pixel = pixels[index];
                byte pixelMaximum = Math.Max(pixel.r, Math.Max(pixel.g, pixel.b));
                maximumChannel = Math.Max(maximumChannel, pixelMaximum);
                if (pixelMaximum > ImageVisibleThreshold)
                {
                    ++nonBlackPixelCount;
                }
            }

            return new CaptureStats(
                nonBlackPixelCount,
                maximumChannel,
                pixels[(readback.height / 2) * readback.width + (readback.width / 2)],
                pixels);
        }

        private static void RequireVisiblePixels(string captureName, CaptureStats stats)
        {
            if (stats.NonBlackPixelCount == 0 || stats.MaximumChannel <= ImageVisibleThreshold)
            {
                throw new InvalidOperationException($"The {captureName} render contained no visible diagnostic pixels.");
            }
        }

        private static string GetEvidenceDirectory()
        {
            string projectParent = Directory.GetParent(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")))?.FullName;
            if (string.IsNullOrEmpty(projectParent))
            {
                throw new InvalidOperationException("Could not resolve the coordination-root evidence directory.");
            }

            string evidenceDirectory = Path.Combine(projectParent, "Phase0LandscapeEvidence");
            Directory.CreateDirectory(evidenceDirectory);
            return evidenceDirectory;
        }

        private static int CalculatePatchCount(TerrainData terrainData)
        {
            int quads = terrainData.heightmapResolution - 1;
            int patchesPerAxis = Mathf.CeilToInt(quads / (float)TerrainPatchQuads);
            return patchesPerAxis * patchesPerAxis;
        }

        private static long CalculateTerrainTessellationTriangles(TerrainData terrainData)
        {
            long quads = terrainData.heightmapResolution - 1L;
            return quads * quads * 2L;
        }

        private static string GetLayerName(int layerIndex)
        {
            return Path.GetFileNameWithoutExtension(ExpectedLayerPaths[layerIndex])
                .Replace("TerrainLayer_", string.Empty);
        }

        private static Terrain FindTargetTerrain(Scene scene)
        {
            Terrain[] terrains = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Terrain>(includeInactive: true))
                .Where(terrain => AssetDatabase.GetAssetPath(terrain.terrainData) == TargetTerrainPath)
                .ToArray();

            if (terrains.Length != 1)
            {
                throw new InvalidOperationException($"Expected exactly one Terrain using {TargetTerrainPath}, found {terrains.Length}.");
            }

            return terrains[0];
        }

        private static void ValidateTerrainData(TerrainData terrainData)
        {
            if (terrainData == null || AssetDatabase.GetAssetPath(terrainData) != TargetTerrainPath)
            {
                throw new InvalidOperationException($"The spike must target {TargetTerrainPath}.");
            }

            TerrainLayer[] layers = terrainData.terrainLayers;
            if (layers.Length != ExpectedLayerPaths.Length)
            {
                throw new InvalidOperationException($"DemoTerrain has {layers.Length} layers; expected six.");
            }

            for (int index = 0; index < ExpectedLayerPaths.Length; ++index)
            {
                string actualPath = AssetDatabase.GetAssetPath(layers[index]);
                if (actualPath != ExpectedLayerPaths[index])
                {
                    throw new InvalidOperationException(
                        $"Layer {index} is {actualPath}; expected {ExpectedLayerPaths[index]}.");
                }
            }

            if (terrainData.alphamapTextureCount != 2)
            {
                throw new InvalidOperationException(
                    $"DemoTerrain has {terrainData.alphamapTextureCount} alphamap textures; expected two.");
            }
        }

        private sealed class Round4Runner : IDisposable
        {
            private enum RunnerState
            {
                Initialize,
                ConfigureProbeNonInstanced,
                CaptureProbeNonInstanced,
                ConfigureProbeInstanced,
                CaptureProbeInstanced,
                ConfigureBlendNonInstanced,
                CaptureBlendNonInstanced,
                ConfigureBlendInstanced,
                CaptureBlendInstanced,
                ArmFrameDebuggerStock,
                RenderFrameDebuggerStock,
                ReadFrameDebuggerStock,
                ArmFrameDebuggerSpike,
                RenderFrameDebuggerSpike,
                ReadFrameDebuggerSpike,
                Complete
            }

            private RunnerState state = RunnerState.Initialize;
            private int updateTick;
            private int measurementStartTick;
            private Terrain terrain;
            private Material originalMaterial;
            private Material spikeMaterial;
            private Texture2DArray diagnosticArray;
            private bool originalDrawInstanced;
            private bool terrainStateSaved;
            private GameObject cameraObject;
            private Camera camera;
            private RenderTexture renderTexture;
            private Texture2D readback;
            private int originalTerrainLayer;
            private float originalBasemapDistance;
            private bool measurementStateSaved;
            private int patchCount;
            private string evidenceDirectory;
            private CaptureStats probeNonInstanced;
            private CaptureStats probeInstanced;
            private CaptureStats blendNonInstanced;
            private CaptureStats blendInstanced;
            private FrameDebuggerCapture frameDebuggerStock;
            private FrameDebuggerCapture frameDebuggerSpike;
            private string deferredInstancingFailure;
            private bool disposed;

            public Round4Runner(bool exitEditorWhenComplete)
            {
                ExitEditorWhenComplete = exitEditorWhenComplete;
            }

            public bool ExitEditorWhenComplete { get; }

            public bool Tick()
            {
                ++updateTick;
                switch (state)
                {
                    case RunnerState.Initialize:
                        Initialize();
                        state = RunnerState.ConfigureProbeNonInstanced;
                        break;

                    case RunnerState.ConfigureProbeNonInstanced:
                        ConfigureInstancingCapture(drawInstanced: false, debugMode: 4.0f);
                        state = RunnerState.CaptureProbeNonInstanced;
                        break;

                    case RunnerState.CaptureProbeNonInstanced:
                        RequireFrameBoundary("non-instanced probe", "Instancing");
                        probeNonInstanced = SaveCapture(
                            camera,
                            renderTexture,
                            readback,
                            Path.Combine(evidenceDirectory, "Phase0_InstancingProbe_NonInstanced.png"));
                        state = RunnerState.ConfigureProbeInstanced;
                        break;

                    case RunnerState.ConfigureProbeInstanced:
                        ConfigureInstancingCapture(drawInstanced: true, debugMode: 4.0f);
                        state = RunnerState.CaptureProbeInstanced;
                        break;

                    case RunnerState.CaptureProbeInstanced:
                        RequireFrameBoundary("instanced probe", "Instancing");
                        probeInstanced = SaveCapture(
                            camera,
                            renderTexture,
                            readback,
                            Path.Combine(evidenceDirectory, "Phase0_InstancingProbe_Instanced.png"));
                        ValidateAndWriteInstancingProbe(
                            evidenceDirectory,
                            probeNonInstanced,
                            probeInstanced);
                        state = RunnerState.ConfigureBlendNonInstanced;
                        break;

                    case RunnerState.ConfigureBlendNonInstanced:
                        ConfigureInstancingCapture(drawInstanced: false, debugMode: 0.0f);
                        state = RunnerState.CaptureBlendNonInstanced;
                        break;

                    case RunnerState.CaptureBlendNonInstanced:
                        RequireFrameBoundary("non-instanced blend", "Instancing");
                        blendNonInstanced = SaveCapture(
                            camera,
                            renderTexture,
                            readback,
                            Path.Combine(evidenceDirectory, "Phase0_AllSixLayerBlend_NonInstanced.png"));
                        state = RunnerState.ConfigureBlendInstanced;
                        break;

                    case RunnerState.ConfigureBlendInstanced:
                        ConfigureInstancingCapture(drawInstanced: true, debugMode: 0.0f);
                        state = RunnerState.CaptureBlendInstanced;
                        break;

                    case RunnerState.CaptureBlendInstanced:
                        RequireFrameBoundary("instanced blend", "Instancing");
                        blendInstanced = SaveCapture(
                            camera,
                            renderTexture,
                            readback,
                            Path.Combine(evidenceDirectory, "Phase0_AllSixLayerBlend_Instanced.png"));
                        deferredInstancingFailure = ValidateAndWriteBlendComparison(
                            evidenceDirectory,
                            blendNonInstanced,
                            blendInstanced);
                        state = RunnerState.ArmFrameDebuggerStock;
                        break;

                    case RunnerState.ArmFrameDebuggerStock:
                        ArmFrameDebugger(originalMaterial);
                        state = RunnerState.RenderFrameDebuggerStock;
                        break;

                    case RunnerState.RenderFrameDebuggerStock:
                        RenderArmedFrameDebugger();
                        state = RunnerState.ReadFrameDebuggerStock;
                        break;

                    case RunnerState.ReadFrameDebuggerStock:
                        frameDebuggerStock = ReadFrameDebugger("stock");
                        state = RunnerState.ArmFrameDebuggerSpike;
                        break;

                    case RunnerState.ArmFrameDebuggerSpike:
                        ArmFrameDebugger(spikeMaterial);
                        state = RunnerState.RenderFrameDebuggerSpike;
                        break;

                    case RunnerState.RenderFrameDebuggerSpike:
                        RenderArmedFrameDebugger();
                        state = RunnerState.ReadFrameDebuggerSpike;
                        break;

                    case RunnerState.ReadFrameDebuggerSpike:
                        frameDebuggerSpike = ReadFrameDebugger("spike");
                        CompleteFrameDebuggerMeasurement(frameDebuggerStock, frameDebuggerSpike);
                        state = RunnerState.Complete;
                        break;

                    case RunnerState.Complete:
                        Debug.Log(
                            $"[Sol Landscape Phase 0] Round 4 completed after {updateTick} editor update ticks.");
                        return true;

                    default:
                        throw new ArgumentOutOfRangeException();
                }

                return false;
            }

            private void Initialize()
            {
                Scene scene = EditorSceneManager.OpenScene(TargetScenePath, OpenSceneMode.Single);
                terrain = FindTargetTerrain(scene);
                ValidateTerrainData(terrain.terrainData);

                originalMaterial = terrain.materialTemplate;
                if (originalMaterial == null || AssetDatabase.GetAssetPath(originalMaterial) != OriginalMaterialPath)
                {
                    throw new InvalidOperationException(
                        $"DemoTerrain must begin with {OriginalMaterialPath}; actual material is " +
                        $"{AssetDatabase.GetAssetPath(originalMaterial)}.");
                }

                originalDrawInstanced = terrain.drawInstanced;
                terrainStateSaved = true;
                spikeMaterial = CreateSpikeMaterial(terrain.terrainData, out diagnosticArray);
                terrain.materialTemplate = spikeMaterial;
                ValidateInstalledSpike(terrain, spikeMaterial);

                evidenceDirectory = GetEvidenceDirectory();
                DeleteStaleRound4Evidence(evidenceDirectory);
                patchCount = CalculatePatchCount(terrain.terrainData);
                originalTerrainLayer = terrain.gameObject.layer;
                originalBasemapDistance = terrain.basemapDistance;
                measurementStateSaved = true;
                cameraObject = new GameObject("Sol Landscape Phase 0 Ticked Measurement Camera")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                camera = cameraObject.AddComponent<Camera>();
                renderTexture = new RenderTexture(
                    EvidenceResolution,
                    EvidenceResolution,
                    24,
                    RenderTextureFormat.ARGB32)
                {
                    name = "Sol Landscape Phase 0 Ticked Measurement",
                    hideFlags = HideFlags.HideAndDontSave
                };
                readback = new Texture2D(
                    EvidenceResolution,
                    EvidenceResolution,
                    TextureFormat.RGB24,
                    false,
                    true)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                ConfigureEvidenceCamera(camera, renderTexture, terrain);
                terrain.gameObject.layer = 31;
                terrain.basemapDistance = float.MaxValue;
                terrain.drawInstanced = false;
                spikeMaterial.enableInstancing = false;
            }

            private static void DeleteStaleRound4Evidence(string directory)
            {
                string[] staleFiles =
                {
                    "Phase0_FrameDebugger.txt",
                    "Phase0_FrameDebuggerEvents.txt",
                    "Phase0_DrawCountComparison.txt",
                    "Phase0_DrawMeasurementFailures.txt",
                    "Phase0_InstancingComparison.txt",
                    "Phase0_AllSixLayerBlend_NonInstanced.png",
                    "Phase0_AllSixLayerBlend_Instanced.png",
                    "Phase0_InstancingProbe_NonInstanced.png",
                    "Phase0_InstancingProbe_Instanced.png",
                    "Phase0_InstancingProbe.txt"
                };
                foreach (string fileName in staleFiles)
                {
                    string path = Path.Combine(directory, fileName);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }

            private void ConfigureInstancingCapture(bool drawInstanced, float debugMode)
            {
                terrain.materialTemplate = spikeMaterial;
                spikeMaterial.enableInstancing = true;
                spikeMaterial.SetFloat("_SolPhase0_DebugMode", debugMode);
                terrain.drawInstanced = drawInstanced;
                terrain.Flush();
                measurementStartTick = updateTick;
            }

            private static void ValidateAndWriteInstancingProbe(
                string directory,
                CaptureStats nonInstanced,
                CaptureStats instanced)
            {
                int nonInstancedRedPixels = CountProbePixels(nonInstanced.Pixels, expectGreen: false);
                int nonInstancedGreenPixels = CountProbePixels(nonInstanced.Pixels, expectGreen: true);
                int instancedRedPixels = CountProbePixels(instanced.Pixels, expectGreen: false);
                int instancedGreenPixels = CountProbePixels(instanced.Pixels, expectGreen: true);
                ImageComparison comparison = CompareCaptures(nonInstanced, instanced);
                bool byteIdentical = comparison.MeanAbsoluteChannelDifference == 0.0 &&
                    comparison.MaximumChannelDifference == 0 &&
                    comparison.DifferingPixels == 0;
                bool nonInstancedIsRed = nonInstancedRedPixels >= nonInstanced.NonBlackPixelCount * 0.9;
                bool instancedIsGreen = instancedGreenPixels >= instanced.NonBlackPixelCount * 0.9;

                File.WriteAllLines(
                    Path.Combine(directory, "Phase0_InstancingProbe.txt"),
                    new[]
                    {
                        $"Non-instanced center: {FormatColor(nonInstanced.Center)}",
                        $"Instanced center: {FormatColor(instanced.Center)}",
                        $"Non-instanced non-black pixels: {nonInstanced.NonBlackPixelCount}",
                        $"Instanced non-black pixels: {instanced.NonBlackPixelCount}",
                        $"Non-instanced red probe pixels: {nonInstancedRedPixels}",
                        $"Non-instanced green probe pixels: {nonInstancedGreenPixels}",
                        $"Instanced red probe pixels: {instancedRedPixels}",
                        $"Instanced green probe pixels: {instancedGreenPixels}",
                        $"Mean absolute channel difference: {comparison.MeanAbsoluteChannelDifference:F6}",
                        $"Maximum channel difference: {comparison.MaximumChannelDifference}",
                        $"Differing pixels (>8): {comparison.DifferingPixels}",
                        $"Byte identical: {byteIdentical}",
                        $"Probe flipped red-to-green: {nonInstancedIsRed && instancedIsGreen && !byteIdentical}"
                    });

                if (!nonInstancedIsRed || !instancedIsGreen || byteIdentical)
                {
                    throw new InvalidOperationException(
                        $"Instancing positive control did not engage: nonInstancedRedPixels={nonInstancedRedPixels}, " +
                        $"instancedGreenPixels={instancedGreenPixels}, " +
                        $"nonInstancedNonBlack={nonInstanced.NonBlackPixelCount}, " +
                        $"instancedNonBlack={instanced.NonBlackPixelCount}, byteIdentical={byteIdentical}.");
                }

                Debug.Log(
                    $"[Sol Landscape Phase 0] Instancing probe PASS: nonInstancedRedPixels={nonInstancedRedPixels}, " +
                    $"instancedGreenPixels={instancedGreenPixels}, differingPixels={comparison.DifferingPixels}.");
            }

            private static int CountProbePixels(Color32[] pixels, bool expectGreen)
            {
                int count = 0;
                foreach (Color32 pixel in pixels)
                {
                    bool matches = expectGreen
                        ? pixel.g >= 200 && pixel.r <= 32 && pixel.b <= 32
                        : pixel.r >= 200 && pixel.g <= 32 && pixel.b <= 32;
                    if (matches)
                    {
                        ++count;
                    }
                }

                return count;
            }

            private static string FormatColor(Color32 color)
            {
                return $"({color.r},{color.g},{color.b},{color.a})";
            }

            private static string ValidateAndWriteBlendComparison(
                string directory,
                CaptureStats nonInstanced,
                CaptureStats instanced)
            {
                ImageComparison comparison = CompareCaptures(nonInstanced, instanced);
                int nonInstancedRegions = CountDistinctDiagnosticRegions(nonInstanced.Pixels);
                int instancedRegions = CountDistinctDiagnosticRegions(instanced.Pixels);
                int nonInstancedDistinctColors = CountDistinctNonBlackColors(nonInstanced.Pixels);
                int instancedDistinctColors = CountDistinctNonBlackColors(instanced.Pixels);
                WriteInstancingEvidence(
                    directory,
                    nonInstanced,
                    instanced,
                    comparison,
                    nonInstancedRegions,
                    instancedRegions,
                    nonInstancedDistinctColors,
                    instancedDistinctColors);

                bool byteIdentical = comparison.MeanAbsoluteChannelDifference == 0.0 &&
                    comparison.MaximumChannelDifference == 0 &&
                    comparison.DifferingPixels == 0;
                if (byteIdentical)
                {
                    const string failure =
                        "Instanced and non-instanced blend captures are byte-identical; " +
                        "the comparison is a methodology failure, not an instancing pass.";
                    Debug.LogError("[Sol Landscape Phase 0] " + failure);
                    return failure;
                }

                ValidateInstancingComparison(
                    nonInstanced,
                    instanced,
                    comparison,
                    nonInstancedRegions,
                    instancedRegions,
                    nonInstancedDistinctColors,
                    instancedDistinctColors);
                return null;
            }

            private void ArmFrameDebugger(Material material)
            {
                terrain.drawInstanced = false;
                terrain.Flush();
                spikeMaterial.SetFloat("_SolPhase0_DebugMode", 0.0f);
                terrain.materialTemplate = material;
                FrameDebuggerReflection.SetEnabled(false);
                FrameDebuggerReflection.SetEnabled(true);
                measurementStartTick = updateTick;
            }

            private void RenderArmedFrameDebugger()
            {
                RequireFrameBoundary("armed", "FrameDebugger");
                measurementStartTick = updateTick;
                camera.Render();
            }

            private FrameDebuggerCapture ReadFrameDebugger(string label)
            {
                RequireFrameBoundary(label, "FrameDebugger readback");
                FrameDebuggerCapture capture = FrameDebuggerReflection.ReadCapture(
                    label,
                    updateTick - measurementStartTick,
                    terrain.GetInstanceID());
                FrameDebuggerReflection.SetEnabled(false);
                Debug.Log($"[Sol Landscape Phase 0] Ticked {label} Frame Debugger sample: {capture.ToEvidenceString()}.");
                return capture;
            }

            private void CompleteFrameDebuggerMeasurement(
                FrameDebuggerCapture stock,
                FrameDebuggerCapture spike)
            {
                WriteFrameDebuggerEvidence(evidenceDirectory, patchCount, stock, spike);
                long terrainTessellationTriangles = CalculateTerrainTessellationTriangles(terrain.terrainData);
                string stockFailure = GetPlausibilityFailure(stock, terrainTessellationTriangles);
                string spikeFailure = GetPlausibilityFailure(spike, terrainTessellationTriangles);
                if (!string.IsNullOrEmpty(stockFailure) || !string.IsNullOrEmpty(spikeFailure))
                {
                    string reason =
                        $"measurement did not observe the terrain: stock=[{stockFailure}], spike=[{spikeFailure}]";
                    WriteDrawFailureEvidence(reason, stock, spike, terrainTessellationTriangles);
                    throw new InvalidOperationException(
                        reason);
                }

                bool secondPassObserved = stock.DrawCallCount > spike.DrawCallCount &&
                    stock.SetPassCalls > spike.SetPassCalls;
                Debug.Log(
                    $"[Sol Landscape Phase 0] Frame Debugger comparison PASS: patchCount={patchCount}, " +
                    $"terrainTessellationTriangles={terrainTessellationTriangles}, " +
                    $"stockEvents={stock.EventCount}, stockDrawCalls={stock.DrawCallCount}, " +
                    $"stockSetPassCalls={stock.SetPassCalls}, stockTriangles={stock.Triangles}, " +
                    $"spikeEvents={spike.EventCount}, spikeDrawCalls={spike.DrawCallCount}, " +
                    $"spikeSetPassCalls={spike.SetPassCalls}, spikeTriangles={spike.Triangles}, " +
                    $"secondPassObserved={secondPassObserved}.");

                if (!string.IsNullOrEmpty(deferredInstancingFailure))
                {
                    throw new InvalidOperationException(deferredInstancingFailure);
                }
            }

            private string GetPlausibilityFailure(
                FrameDebuggerCapture capture,
                long terrainTessellationTriangles)
            {
                var failures = new List<string>();
                if (!capture.HasData)
                {
                    failures.Add("no terrain draw events");
                }

                if (capture.DrawCallCount < patchCount)
                {
                    failures.Add($"drawCalls {capture.DrawCallCount} < visiblePatchCount {patchCount}");
                }

                if (capture.SetPassCalls >= capture.DrawCallCount)
                {
                    failures.Add($"setPassCalls {capture.SetPassCalls} >= drawCalls {capture.DrawCallCount}");
                }

                long minimumTriangles = Math.Max(1, terrainTessellationTriangles / 10);
                long maximumTriangles = terrainTessellationTriangles * 10;
                if (capture.Triangles < minimumTriangles || capture.Triangles > maximumTriangles)
                {
                    failures.Add(
                        $"triangles {capture.Triangles} outside [{minimumTriangles},{maximumTriangles}]");
                }

                return string.Join("; ", failures);
            }

            private void WriteDrawFailureEvidence(
                string reason,
                FrameDebuggerCapture stock,
                FrameDebuggerCapture spike,
                long terrainTessellationTriangles)
            {
                File.WriteAllLines(
                    Path.Combine(evidenceDirectory, "Phase0_DrawMeasurementFailures.txt"),
                    new[]
                    {
                        $"Patch count: {patchCount}",
                        $"Terrain tessellation triangles: {terrainTessellationTriangles}",
                        $"Editor update ticks reached: {updateTick}",
                        $"Stock: {stock.ToEvidenceString()}",
                        $"Spike: {spike.ToEvidenceString()}",
                        $"Result: {reason}"
                    });
            }

            private void RequireFrameBoundary(string label, string mechanism)
            {
                if (updateTick <= measurementStartTick)
                {
                    throw new InvalidOperationException(
                        $"{mechanism} {label} was sampled without an EditorApplication.update boundary.");
                }
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                FrameDebuggerReflection.SetEnabled(false);

                if (camera != null)
                {
                    camera.targetTexture = null;
                }

                if (renderTexture != null)
                {
                    renderTexture.Release();
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                }

                if (readback != null)
                {
                    UnityEngine.Object.DestroyImmediate(readback);
                }

                if (cameraObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(cameraObject);
                }

                if (terrain != null)
                {
                    if (terrainStateSaved)
                    {
                        terrain.materialTemplate = originalMaterial;
                        terrain.drawInstanced = originalDrawInstanced;
                        terrain.Flush();
                    }

                    if (measurementStateSaved)
                    {
                        terrain.gameObject.layer = originalTerrainLayer;
                        terrain.basemapDistance = originalBasemapDistance;
                    }
                }

                if (spikeMaterial != null)
                {
                    UnityEngine.Object.DestroyImmediate(spikeMaterial);
                }

                if (diagnosticArray != null)
                {
                    UnityEngine.Object.DestroyImmediate(diagnosticArray);
                }

                if (terrainStateSaved && terrain != null &&
                    (terrain.materialTemplate != originalMaterial || terrain.drawInstanced != originalDrawInstanced))
                {
                    throw new InvalidOperationException("Phase 0 Round 4 did not restore the original terrain state.");
                }
            }
        }

        private static class FrameDebuggerReflection
        {
            private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            private const string UtilityTypeName =
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility, UnityEditor";
            private const string EventDataTypeName =
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData, UnityEditor";

            public static void SetEnabled(bool enabled)
            {
                Type utilityType = Type.GetType(UtilityTypeName, throwOnError: false);
                MethodInfo setEnabled = utilityType?.GetMethod("SetEnabled", StaticFlags);
                setEnabled?.Invoke(null, new object[] { enabled, 0 });
            }

            public static FrameDebuggerCapture ReadCapture(string label, int tickDelta, int terrainInstanceId)
            {
                Type utilityType = Type.GetType(UtilityTypeName, throwOnError: false);
                Type eventDataType = Type.GetType(EventDataTypeName, throwOnError: false);
                if (utilityType == null || eventDataType == null)
                {
                    return new FrameDebuggerCapture(label, 0, 0, 0, 0, tickDelta, new[] { "Reflection types unavailable." });
                }

                PropertyInfo countProperty = utilityType.GetProperty("count", StaticFlags);
                MethodInfo getEventName = utilityType.GetMethod("GetFrameEventInfoName", StaticFlags);
                MethodInfo getEventData = utilityType.GetMethod("GetFrameEventData", StaticFlags);
                if (countProperty == null || getEventName == null || getEventData == null)
                {
                    return new FrameDebuggerCapture(label, 0, 0, 0, 0, tickDelta, new[] { "Reflection members unavailable." });
                }

                int eventCount = (int)countProperty.GetValue(null);
                int drawCallCount = 0;
                long triangleCount = 0;
                var passSignatures = new HashSet<string>();
                var eventLines = new List<string>(Math.Max(1, eventCount));
                for (int eventIndex = 0; eventIndex < eventCount; ++eventIndex)
                {
                    string eventName = getEventName.Invoke(null, new object[] { eventIndex }) as string ?? string.Empty;
                    object eventData = Activator.CreateInstance(eventDataType);
                    bool hasEventData = (bool)getEventData.Invoke(null, new[] { (object)eventIndex, eventData });
                    string originalShader = hasEventData ? GetStringField(eventDataType, eventData, "m_OriginalShaderName") : string.Empty;
                    string realShader = hasEventData ? GetStringField(eventDataType, eventData, "m_RealShaderName") : string.Empty;
                    string passName = hasEventData ? GetStringField(eventDataType, eventData, "m_PassName") : string.Empty;
                    string lightMode = hasEventData ? GetStringField(eventDataType, eventData, "m_PassLightMode") : string.Empty;
                    int eventDrawCalls = hasEventData ? GetIntField(eventDataType, eventData, "m_DrawCallCount") : 0;
                    int componentInstanceId = hasEventData ? GetIntField(eventDataType, eventData, "m_ComponentInstanceID") : 0;
                    int vertexCount = hasEventData ? GetIntField(eventDataType, eventData, "m_VertexCount") : 0;
                    int indexCount = hasEventData ? GetIntField(eventDataType, eventData, "m_IndexCount") : 0;
                    int instanceCount = hasEventData ? GetIntField(eventDataType, eventData, "m_InstanceCount") : 0;
                    bool isTerrainEvent = componentInstanceId == terrainInstanceId &&
                        (!string.IsNullOrEmpty(originalShader) || !string.IsNullOrEmpty(realShader));
                    if (isTerrainEvent)
                    {
                        int eventMultiplicity = Math.Max(1, eventDrawCalls);
                        drawCallCount += eventMultiplicity;
                        passSignatures.Add($"{originalShader}|{realShader}|{passName}|{lightMode}");
                        long primitivesPerDraw = indexCount > 0 ? indexCount / 3L : vertexCount / 3L;
                        int geometryMultiplicity = Math.Max(eventMultiplicity, Math.Max(1, instanceCount));
                        triangleCount += primitivesPerDraw * geometryMultiplicity;
                    }

                    eventLines.Add(
                        $"Event {eventIndex}: name={eventName}; hasData={hasEventData}; " +
                        $"terrainEvent={isTerrainEvent}; componentInstanceID={componentInstanceId}; " +
                        $"drawCalls={eventDrawCalls}; vertices={vertexCount}; indices={indexCount}; " +
                        $"instances={instanceCount}; originalShader={originalShader}; realShader={realShader}; " +
                        $"pass={passName}; lightMode={lightMode}");
                }

                return new FrameDebuggerCapture(
                    label,
                    eventCount,
                    drawCallCount,
                    passSignatures.Count,
                    triangleCount,
                    tickDelta,
                    eventLines.ToArray());
            }

            private static string GetStringField(Type type, object instance, string name)
            {
                return type.GetField(name, InstanceFlags)?.GetValue(instance) as string ?? string.Empty;
            }

            private static int GetIntField(Type type, object instance, string name)
            {
                object value = type.GetField(name, InstanceFlags)?.GetValue(instance);
                return value is int intValue ? intValue : 0;
            }
        }

        private static void WriteFrameDebuggerEvidence(
            string evidenceDirectory,
            int patchCount,
            FrameDebuggerCapture stock,
            FrameDebuggerCapture spike)
        {
            var output = new StringBuilder();
            output.AppendLine($"Patch count: {patchCount}");
            output.AppendLine($"Stock event count: {stock.EventCount}");
            output.AppendLine($"Stock terrain draw-call count: {stock.DrawCallCount}");
            output.AppendLine($"Stock derived set-pass count: {stock.SetPassCalls}");
            output.AppendLine($"Stock derived triangle count: {stock.Triangles}");
            output.AppendLine($"Stock tick delta: {stock.TickDelta}");
            foreach (string line in stock.EventLines)
            {
                output.AppendLine("Stock " + line);
            }

            output.AppendLine($"Spike event count: {spike.EventCount}");
            output.AppendLine($"Spike terrain draw-call count: {spike.DrawCallCount}");
            output.AppendLine($"Spike derived set-pass count: {spike.SetPassCalls}");
            output.AppendLine($"Spike derived triangle count: {spike.Triangles}");
            output.AppendLine($"Spike tick delta: {spike.TickDelta}");
            foreach (string line in spike.EventLines)
            {
                output.AppendLine("Spike " + line);
            }

            File.WriteAllText(
                Path.Combine(evidenceDirectory, "Phase0_FrameDebuggerEvents.txt"),
                output.ToString());
        }

        private readonly struct FrameDebuggerCapture
        {
            public FrameDebuggerCapture(
                string label,
                int eventCount,
                int drawCallCount,
                int setPassCalls,
                long triangles,
                int tickDelta,
                string[] eventLines)
            {
                Label = label;
                EventCount = eventCount;
                DrawCallCount = drawCallCount;
                SetPassCalls = setPassCalls;
                Triangles = triangles;
                TickDelta = tickDelta;
                EventLines = eventLines;
            }

            public string Label { get; }
            public int EventCount { get; }
            public int DrawCallCount { get; }
            public int SetPassCalls { get; }
            public long Triangles { get; }
            public int TickDelta { get; }
            public string[] EventLines { get; }
            public bool HasData => EventCount > 0 && DrawCallCount > 0;

            public string ToEvidenceString()
            {
                return $"events={EventCount},terrainDrawCalls={DrawCallCount}," +
                    $"setPassCalls={SetPassCalls},triangles={Triangles},tickDelta={TickDelta}";
            }
        }

        private readonly struct CaptureStats
        {
            public CaptureStats(
                int nonBlackPixelCount,
                byte maximumChannel,
                Color32 center,
                Color32[] pixels)
            {
                NonBlackPixelCount = nonBlackPixelCount;
                MaximumChannel = maximumChannel;
                Center = center;
                Pixels = pixels;
            }

            public int NonBlackPixelCount { get; }
            public byte MaximumChannel { get; }
            public Color32 Center { get; }
            public Color32[] Pixels { get; }
        }

        private readonly struct ImageComparison
        {
            public ImageComparison(
                double meanAbsoluteChannelDifference,
                byte maximumChannelDifference,
                int differingPixels,
                double differingPixelPercent)
            {
                MeanAbsoluteChannelDifference = meanAbsoluteChannelDifference;
                MaximumChannelDifference = maximumChannelDifference;
                DifferingPixels = differingPixels;
                DifferingPixelPercent = differingPixelPercent;
            }

            public double MeanAbsoluteChannelDifference { get; }
            public byte MaximumChannelDifference { get; }
            public int DifferingPixels { get; }
            public double DifferingPixelPercent { get; }
        }

        private readonly struct CpuLayerStats
        {
            public CpuLayerStats(
                int layerIndex,
                string layerName,
                float maximumWeight,
                int maximumX,
                int maximumY,
                long coveredPixels,
                double coveragePercent,
                double meanWeight)
            {
                LayerIndex = layerIndex;
                LayerName = layerName;
                MaximumWeight = maximumWeight;
                MaximumX = maximumX;
                MaximumY = maximumY;
                CoveredPixels = coveredPixels;
                CoveragePercent = coveragePercent;
                MeanWeight = meanWeight;
            }

            public int LayerIndex { get; }
            public string LayerName { get; }
            public float MaximumWeight { get; }
            public int MaximumX { get; }
            public int MaximumY { get; }
            public long CoveredPixels { get; }
            public double CoveragePercent { get; }
            public double MeanWeight { get; }
        }

        private sealed class CpuGroundTruth
        {
            public CpuGroundTruth(int width, int height, CpuLayerStats[] layers, int weakestLayerIndex)
            {
                Width = width;
                Height = height;
                Layers = layers;
                WeakestLayerIndex = weakestLayerIndex;
            }

            public int Width { get; }
            public int Height { get; }
            public CpuLayerStats[] Layers { get; }
            public int WeakestLayerIndex { get; }
        }
    }
}
