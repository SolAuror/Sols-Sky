using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Sol.Water;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Sol.Landscape.Editor
{
    /// <summary>
    /// Bakes the config-authored landscape response rules into TerrainData alphamaps. Hand-authored
    /// channels opt out explicitly in SolLandscapeConfig; their stored value is removed from the
    /// generated budget and copied back unchanged.
    /// </summary>
    internal static class SolLandscapeAlphamapGenerator
    {
        private const string ScenePath = "Assets/Scenes/Sols_Water2_Demo.unity";
        private const string ConfigPath = "Assets/Sky-and-Water/Landscape/SolLandscapeConfig.asset";
        private const string MaterialPath = "Assets/Sky-and-Water/Landscape/M_SolLandscape.mat";
        private const int CaptureWidth = 1280;
        private const int CaptureHeight = 720;
        private const float RuleEpsilon = 1e-6f;
        private const float ShoreBandMin = -4f;
        private const float ShoreBandMax = 4f;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly int SurfaceWetnessId = Shader.PropertyToID("_Sol_SurfaceWetness");
        private static readonly int SurfaceSnowCoverId = Shader.PropertyToID("_Sol_SurfaceSnowCover");
        private static readonly int SurfaceTemperatureId = Shader.PropertyToID("_Sol_SurfaceTemperature");
        private static readonly int TerrainWetnessId = Shader.PropertyToID("_Sol_TerrainWetness");
        private static readonly int GlobalWaterLevelId = Shader.PropertyToID("_Sol_GlobalWaterLevel");

        [MenuItem("Tools/Sol Landscape/Advanced/Regenerate All Alphamaps (Discard Painted Textures)")]
        private static void GenerateFromMenu()
        {
            SolLandscapeConfig config = AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);
            Terrain terrain = FindTerrain(config, openDemoSceneIfNeeded: false);
            if (!EditorUtility.DisplayDialog(
                    "Discard painted terrain textures?",
                    "This destructive rebuild replaces Dirt, Grass, Stone and Sand paint across the entire terrain. Only channels marked Preserve Painted Weight (Path) remain untouched. Use Regenerate Procedural Areas for the safe default.",
                    "Discard Paint and Regenerate",
                    "Cancel"))
                return;

            Undo.RegisterCompleteObjectUndo(terrain.terrainData, "Regenerate all terrain alphamaps");
            GenerationResult result = Generate(terrain, config);
            // Retain the last recoverable protection state if generation refuses before it writes.
            SolLandscapeLiveAlphamapUpdater.ClearProtectionForDestructiveGeneration(terrain.terrainData);
            AssetDatabase.SaveAssetIfDirty(terrain.terrainData);
            Debug.Log($"[Sol Landscape G1] Alphamap generation complete. {result.Summary}");
        }

        /// <summary>Captures the confirmed live-auto regression before the G1 config is switched to Manual.</summary>
        public static void CaptureRegressionBeforeFromCommandLine()
        {
            RunCommandLine((terrain, config, outputDirectory) =>
            {
                CaptureSites sites = FindCaptureSites(terrain);
                CaptureVisualSet(terrain, sites, outputDirectory, "BeforeLiveAuto");
                File.WriteAllText(
                    Path.Combine(outputDirectory, "G1_BeforeLiveAuto_Evidence.txt"),
                    BuildCaptureEvidence("BeforeLiveAuto", terrain, sites),
                    new UTF8Encoding(false));
            });
        }

        /// <summary>Generates the persisted alphamaps, then runs G1's numeric, paint, render and shader checks.</summary>
        public static void GenerateAndVerifyFromCommandLine()
        {
            RunCommandLine((terrain, config, outputDirectory) =>
            {
                GenerationResult generation = Generate(terrain, config);
                AssetDatabase.SaveAssets();
                terrain.Flush();

                CaptureSites sites = FindCaptureSites(terrain);
                CaptureVisualSet(terrain, sites, outputDirectory, "AfterBakedManual");
                VerificationResult verification = VerifyGeneratedState(terrain, config, generation, sites, outputDirectory);

                string evidence = BuildGenerationEvidence(terrain, config, generation, verification, sites);
                string evidencePath = Path.Combine(outputDirectory, "G1_AlphamapGeneration_Evidence.txt");
                File.WriteAllText(evidencePath, evidence, new UTF8Encoding(false));
                if (!verification.Passed)
                    throw new InvalidOperationException($"G1 alphamap verification failed. See {evidencePath}");
            });
        }

        internal static float EvaluateResponseCurve(float normalizedInput, float bias)
        {
            float response = Mathf.Clamp01(normalizedInput);
            response = response * response * (3f - 2f * response);
            return Mathf.Clamp01(response + Mathf.Clamp(bias, -1f, 1f) * response * (1f - response));
        }

        internal static float EvaluateDirectedResponse(float response, float signedInfluence)
        {
            float directedResponse = signedInfluence >= 0f ? response : 1f - response;
            return Mathf.Lerp(1f, directedResponse, Mathf.Clamp01(Mathf.Abs(signedInfluence)));
        }

        private static GenerationResult Generate(Terrain terrain, SolLandscapeConfig config)
        {
            ValidateInputs(terrain, config);
            TerrainData data = terrain.terrainData;
            int alphaWidth = data.alphamapWidth;
            int alphaHeight = data.alphamapHeight;
            int layerCount = data.alphamapLayers;
            float[,,] painted = data.GetAlphamaps(0, 0, alphaWidth, alphaHeight);
            float[,,] generated = new float[alphaHeight, alphaWidth, layerCount];
            float[,] heights = data.GetHeights(0, 0, data.heightmapResolution, data.heightmapResolution);
            HeightSampler heightSampler = new HeightSampler(heights, data.size);
            float waterLevel = ResolveWaterLevel(config);

            int[] preservedIndices = Enumerable.Range(0, layerCount)
                .Where(index => config.Layers[index].preservePaintedWeightDuringGeneration)
                .ToArray();
            int[] generatedIndices = Enumerable.Range(0, layerCount)
                .Where(index => !config.Layers[index].preservePaintedWeightDuringGeneration)
                .ToArray();
            if (preservedIndices.Length == 0)
                throw new InvalidOperationException("At least one hand-authored alphamap channel must opt into preservation.");
            if (generatedIndices.Length == 0)
                throw new InvalidOperationException("No generated alphamap channels remain after preservation exclusions.");
            int stone1Index = Array.FindIndex(data.terrainLayers, layer => layer != null && layer.name == "TerrainLayer_Stone1");
            int stone2Index = Array.FindIndex(data.terrainLayers, layer => layer != null && layer.name == "TerrainLayer_Stone2");
            if (stone1Index < 0 || stone2Index < 0)
                throw new InvalidOperationException("Stone1 and Stone2 are required for the 40-degree coverage contract.");

            double[] positiveClaims = new double[layerCount];
            double[] dominantClaims = new double[layerCount];
            double[] weightSums = new double[layerCount];
            double[] responseErrors = new double[2];
            double responseMaxError = 0d;
            double slopeErrorSum = 0d;
            double slopeMaxError = 0d;
            double heightErrorSum = 0d;
            double heightMaxError = 0d;
            long comparisonSamples = 0;
            long steepSamples = 0;
            float minimumSteepRockWeight = float.PositiveInfinity;
            float minimumTotalBeforeWrite = float.PositiveInfinity;
            float minimumGeneratedClaim = float.PositiveInfinity;
            float[] claims = new float[layerCount];

            for (int y = 0; y < alphaHeight; ++y)
            {
                float v = (y + 0.5f) / alphaHeight;
                for (int x = 0; x < alphaWidth; ++x)
                {
                    float u = (x + 0.5f) / alphaWidth;
                    HeightSample rawHeightSample = heightSampler.Sample(u, v);
                    float slopeDegrees = data.GetSteepness(u, v);
                    float localHeight = data.GetInterpolatedHeight(u, v);
                    float worldY = terrain.transform.position.y + localHeight;
                    float claimTotal = 0f;
                    foreach (int index in generatedIndices)
                    {
                        SolLandscapeLayerEntry entry = config.Layers[index];
                        float claim = EvaluateRule(entry, slopeDegrees, worldY, waterLevel);
                        claims[index] = claim;
                        claimTotal += claim;
                        if (claim > RuleEpsilon)
                            positiveClaims[index] += 1d;
                    }
                    minimumGeneratedClaim = Mathf.Min(minimumGeneratedClaim, claimTotal);
                    if (!(claimTotal > RuleEpsilon) || !Finite(claimTotal))
                        throw new InvalidOperationException($"No finite generated rule claims alphamap texel ({x},{y}); slope={slopeDegrees:R}; worldY={worldY:R}.");

                    float preservedTotal = 0f;
                    foreach (int index in preservedIndices)
                    {
                        float preserved = painted[y, x, index];
                        generated[y, x, index] = preserved;
                        preservedTotal += preserved;
                    }
                    if (preservedTotal > 1f + 1e-5f)
                        throw new InvalidOperationException($"Preserved channels exceed one at alphamap texel ({x},{y}).");
                    float generatedBudget = Mathf.Max(0f, 1f - preservedTotal);
                    float inverseClaim = 1f / claimTotal;
                    int dominantIndex = generatedIndices[0];
                    float dominantWeight = -1f;
                    float total = preservedTotal;
                    foreach (int index in generatedIndices)
                    {
                        float weight = generatedBudget * claims[index] * inverseClaim;
                        generated[y, x, index] = weight;
                        weightSums[index] += weight;
                        total += weight;
                        if (weight > dominantWeight)
                        {
                            dominantWeight = weight;
                            dominantIndex = index;
                        }
                    }
                    foreach (int index in preservedIndices)
                        weightSums[index] += generated[y, x, index];
                    dominantClaims[dominantIndex] += 1d;
                    minimumTotalBeforeWrite = Mathf.Min(minimumTotalBeforeWrite, total);

                    if (slopeDegrees >= 40f)
                    {
                        float rockWeight = generated[y, x, stone1Index] + generated[y, x, stone2Index];
                        minimumSteepRockWeight = Mathf.Min(minimumSteepRockWeight, rockWeight);
                        ++steepSamples;
                    }

                    if ((x & 15) == 0 && (y & 15) == 0)
                    {
                        double slopeError = Math.Abs(rawHeightSample.SlopeDegrees - slopeDegrees);
                        double heightError = Math.Abs(rawHeightSample.LocalHeight - localHeight);
                        slopeErrorSum += slopeError;
                        slopeMaxError = Math.Max(slopeMaxError, slopeError);
                        heightErrorSum += heightError;
                        heightMaxError = Math.Max(heightMaxError, heightError);
                        foreach (int index in generatedIndices)
                        {
                            SolLandscapeLayerEntry entry = config.Layers[index];
                            double normalized = (slopeDegrees - entry.slopeCenter) / Math.Max(entry.slopeContrast, 0.01f) + 0.5d;
                            float cpu = EvaluateResponseCurve((float)normalized, entry.slopeBias);
                            double reference = EvaluateResponseCurveDouble(normalized, entry.slopeBias);
                            double error = Math.Abs(cpu - reference);
                            responseErrors[0] += error;
                            responseErrors[1] += 1d;
                            responseMaxError = Math.Max(responseMaxError, error);
                        }
                        ++comparisonSamples;
                    }
                }
            }

            double[] preservedBefore = ExtractPreserved(painted, preservedIndices);
            Undo.RegisterCompleteObjectUndo(data, "Generate landscape alphamaps");
            data.SetAlphamaps(0, 0, generated);
            data.SetBaseMapDirty();
            terrain.Flush();
            EditorUtility.SetDirty(data);
            float[,,] persisted = data.GetAlphamaps(0, 0, alphaWidth, alphaHeight);
            DifferenceMetrics preservedDifference = ComparePreserved(preservedBefore, persisted, preservedIndices);
            float minimumTotalAfterWrite = MinimumTotal(persisted);

            double texelCount = (double)alphaWidth * alphaHeight;
            return new GenerationResult(
                alphaWidth,
                alphaHeight,
                layerCount,
                persisted,
                preservedIndices,
                positiveClaims.Select(value => value / texelCount).ToArray(),
                dominantClaims.Select(value => value / texelCount).ToArray(),
                weightSums.Select(value => value / texelCount).ToArray(),
                minimumGeneratedClaim,
                minimumTotalBeforeWrite,
                minimumTotalAfterWrite,
                steepSamples,
                minimumSteepRockWeight,
                preservedDifference,
                (long)responseErrors[1],
                responseErrors[1] > 0d ? responseErrors[0] / responseErrors[1] : 0d,
                responseMaxError,
                comparisonSamples > 0 ? slopeErrorSum / comparisonSamples : 0d,
                slopeMaxError,
                comparisonSamples > 0 ? heightErrorSum / comparisonSamples : 0d,
                heightMaxError);
        }

        internal static float EvaluateRule(
            SolLandscapeLayerEntry entry,
            float slopeDegrees,
            float worldY,
            float waterLevel)
        {
            float slopeInput = (slopeDegrees - entry.slopeCenter) / Mathf.Max(entry.slopeContrast, 0.01f) + 0.5f;
            float slopeResponse = EvaluateResponseCurve(slopeInput, entry.slopeBias);
            float slopeRule = EvaluateDirectedResponse(slopeResponse, entry.slopeInfluence);
            float altitude = worldY
                - (entry.altitudeReference == SolLandscapeAltitudeReference.RelativeToWaterLevel ? waterLevel : 0f);
            float heightInput = (altitude - entry.heightRange.x) / Mathf.Max(entry.heightRange.y - entry.heightRange.x, 0.01f);
            float heightResponse = EvaluateResponseCurve(heightInput, entry.heightBias);
            float heightRule = EvaluateDirectedResponse(heightResponse, entry.heightInfluence);
            return Mathf.Max(entry.autoWeight, 0f) * slopeRule * heightRule;
        }

        private static double EvaluateResponseCurveDouble(double normalizedInput, double bias)
        {
            double response = Math.Max(0d, Math.Min(1d, normalizedInput));
            response = response * response * (3d - 2d * response);
            return Math.Max(0d, Math.Min(1d, response + Math.Max(-1d, Math.Min(1d, bias)) * response * (1d - response)));
        }

        private static VerificationResult VerifyGeneratedState(
            Terrain terrain,
            SolLandscapeConfig config,
            GenerationResult generation,
            CaptureSites sites,
            string outputDirectory)
        {
            bool allManual = config.Layers.All(entry => entry.mode == SolLandscapeLayerMode.Manual);
            bool pathBitExact = generation.PreservedDifference.BitExact && generation.PreservedDifference.MeanAbsoluteError == 0d;
            bool validWeights = generation.MinimumTotalAfterWrite > 0f && Finite(generation.MinimumTotalAfterWrite);
            bool steepRock = generation.SteepSampleCount > 0 && generation.MinimumSteepRockWeight >= 0.5f;

            FiniteSweepMetrics finiteSweep = MeasureFiniteSweep(terrain);
            PaintTestResult paint = VerifyPaintSurvives(terrain, config, generation, sites.LowFace, outputDirectory);
            ShaderMetrics shader = MeasureShader(AssetDatabase.LoadAssetAtPath<Material>(MaterialPath)?.shader);
            bool pass = allManual
                && pathBitExact
                && validWeights
                && steepRock
                && finiteSweep.NonFinitePixels == 0
                && paint.Survived
                && paint.RestoredPathBitExact
                && shader.Errors == 0
                && shader.Warnings == 0;
            return new VerificationResult(pass, allManual, finiteSweep, paint, shader);
        }

        private static PaintTestResult VerifyPaintSurvives(
            Terrain terrain,
            SolLandscapeConfig config,
            GenerationResult generation,
            Site site,
            string outputDirectory)
        {
            TerrainData data = terrain.terrainData;
            TerrainLayer[] layers = data.terrainLayers;
            int grassIndex = Array.FindIndex(layers, layer => layer != null && layer.name == "TerrainLayer_Grass");
            int pathIndex = Array.FindIndex(config.Layers.ToArray(), entry => entry.preservePaintedWeightDuringGeneration);
            if (grassIndex < 0 || pathIndex < 0)
                throw new InvalidOperationException("Grass and preserved Path channels are required for the paint survival test.");

            const int brushSize = 17;
            int half = brushSize / 2;
            int centerX = Mathf.Clamp(Mathf.RoundToInt(site.U * (data.alphamapWidth - 1)), half, data.alphamapWidth - half - 1);
            int centerY = Mathf.Clamp(Mathf.RoundToInt(site.V * (data.alphamapHeight - 1)), half, data.alphamapHeight - half - 1);
            int startX = centerX - half;
            int startY = centerY - half;
            float[,,] original = data.GetAlphamaps(startX, startY, brushSize, brushSize);
            float[,,] painted = (float[,,])original.Clone();
            for (int y = 0; y < brushSize; ++y)
            {
                for (int x = 0; x < brushSize; ++x)
                {
                    float path = original[y, x, pathIndex];
                    for (int layer = 0; layer < data.alphamapLayers; ++layer)
                        painted[y, x, layer] = layer == pathIndex ? path : 0f;
                    painted[y, x, grassIndex] = 1f - path;
                }
            }

            data.SetAlphamaps(startX, startY, painted);
            terrain.Flush();
            PublishDriver();
            float[,,] afterPublish = data.GetAlphamaps(startX, startY, brushSize, brushSize);
            DifferenceMetrics grassDifference = CompareChannel(painted, afterPublish, grassIndex);
            DifferenceMetrics pathDuringPaint = CompareChannel(original, afterPublish, pathIndex);
            CaptureAtSite(terrain, site, Path.Combine(outputDirectory, "G1_GrassPaintOnGeneratedStone.png"));

            data.SetAlphamaps(startX, startY, original);
            data.SetBaseMapDirty();
            terrain.Flush();
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            float[,,] restored = data.GetAlphamaps(startX, startY, brushSize, brushSize);
            DifferenceMetrics restoration = CompareAll(original, restored);
            DifferenceMetrics restoredPath = CompareChannel(original, restored, pathIndex);
            bool survived = grassDifference.BitExact && pathDuringPaint.BitExact;
            return new PaintTestResult(survived, grassDifference, pathDuringPaint, restoration, restoredPath.BitExact);
        }

        private static FiniteSweepMetrics MeasureFiniteSweep(Terrain terrain)
        {
            var temporary = new List<UnityEngine.Object>();
            try
            {
                Camera camera = CreateOrthographicCamera(temporary);
                camera.clearFlags = CameraClearFlags.Skybox;
                int[] sizes = { 20, 50, 110, 220, 430 };
                long total = 0;
                long nonFinite = 0;
                var perSize = new List<string>();
                foreach (int size in sizes)
                {
                    Quaternion rotation = Quaternion.Euler(38f, 45f, 0f);
                    Vector3 pivot = new Vector3(0f, 5f, 0f);
                    camera.transform.SetPositionAndRotation(pivot - rotation * Vector3.forward * (size * 2f), rotation);
                    camera.orthographicSize = size;
                    camera.aspect = 1280f / 760f;
                    Color[] pixels = Render(terrain, camera, 1280, 760);
                    long invalid = pixels.LongCount(pixel => !Finite(pixel.r) || !Finite(pixel.g) || !Finite(pixel.b) || !Finite(pixel.a));
                    total += pixels.Length;
                    nonFinite += invalid;
                    perSize.Add($"{size}:{invalid}/{pixels.Length}");
                }
                return new FiniteSweepMetrics(total, nonFinite, string.Join(",", perSize));
            }
            finally
            {
                DestroyTemporary(temporary);
            }
        }

        private static void CaptureVisualSet(Terrain terrain, CaptureSites sites, string outputDirectory, string suffix)
        {
            CaptureAtSite(terrain, sites.Contour, Path.Combine(outputDirectory, $"G1_BlackContour_{suffix}.png"));
            CaptureAtSite(terrain, sites.LowFace, Path.Combine(outputDirectory, $"G1_LowAltitude60Face_{suffix}.png"));
            CaptureAtSite(terrain, sites.UnderwaterFace, Path.Combine(outputDirectory, $"G1_UnderwaterSteepFace_{suffix}.png"));
            CaptureAtSite(terrain, sites.Shoreline, Path.Combine(outputDirectory, $"G1_ShorelineGrazing_{suffix}.png"));
        }

        private static void CaptureAtSite(Terrain terrain, Site site, string path)
        {
            var temporary = new List<UnityEngine.Object>();
            try
            {
                Camera camera = CreatePerspectiveCamera(temporary);
                CreateDaylight(temporary);
                Vector3 horizontalOut = new Vector3(site.Normal.x, 0f, site.Normal.z);
                if (horizontalOut.sqrMagnitude < 1e-5f)
                {
                    Vector3 centre = terrain.transform.position + terrain.terrainData.size * 0.5f;
                    horizontalOut = new Vector3(site.WorldPosition.x - centre.x, 0f, site.WorldPosition.z - centre.z);
                }
                if (horizontalOut.sqrMagnitude < 1e-5f)
                    horizontalOut = Vector3.forward;
                horizontalOut.Normalize();
                float viewDistance = site.Kind == SiteKind.Shoreline ? 12f : 16f;
                Vector3 cameraXZ = site.WorldPosition + horizontalOut * viewDistance;
                float u = Mathf.Clamp01((cameraXZ.x - terrain.transform.position.x) / terrain.terrainData.size.x);
                float v = Mathf.Clamp01((cameraXZ.z - terrain.transform.position.z) / terrain.terrainData.size.z);
                float groundY = terrain.transform.position.y + terrain.terrainData.GetInterpolatedHeight(u, v);
                camera.transform.position = new Vector3(cameraXZ.x, groundY + (site.Kind == SiteKind.Shoreline ? 1.1f : 1.75f), cameraXZ.z);
                camera.transform.LookAt(site.WorldPosition + Vector3.up * (site.Kind == SiteKind.Shoreline ? 0.25f : 1f), Vector3.up);
                WritePng(path, Render(terrain, camera, CaptureWidth, CaptureHeight), CaptureWidth, CaptureHeight);
            }
            finally
            {
                DestroyTemporary(temporary);
            }
        }

        private static CaptureSites FindCaptureSites(Terrain terrain)
        {
            Site contour = FindSite(terrain, SiteKind.Contour, (slope, y) => Math.Abs(y - 4.978027f) * 8d + Math.Abs(slope - 25f), (slope, y) => slope >= 8f && slope <= 45f);
            Site low = FindSite(terrain, SiteKind.LowFace, (slope, y) => Math.Abs(slope - 60f) + Math.Abs(y - 3f) * 0.25d, (slope, y) => y >= 0f && y < 5f && slope >= 40f);
            Site underwater = FindSite(terrain, SiteKind.UnderwaterFace, (slope, y) => Math.Abs(slope - 55f) + Math.Abs(y + 2f) * 0.1d, (slope, y) => y < 0f && slope >= 40f);
            Site shore = FindSite(terrain, SiteKind.Shoreline, (slope, y) => Math.Abs(y) * 10d + slope * 0.05d, (slope, y) => Math.Abs(y) <= 1f && slope <= 25f);
            return new CaptureSites(contour, low, underwater, shore);
        }

        private static Site FindSite(Terrain terrain, SiteKind kind, Func<float, float, double> score, Func<float, float, bool> predicate)
        {
            TerrainData data = terrain.terrainData;
            double bestScore = double.PositiveInfinity;
            Site best = default;
            bool found = false;
            const int samples = 512;
            for (int y = 0; y <= samples; ++y)
            {
                float v = y / (float)samples;
                for (int x = 0; x <= samples; ++x)
                {
                    float u = x / (float)samples;
                    float slope = data.GetSteepness(u, v);
                    float worldY = terrain.transform.position.y + data.GetInterpolatedHeight(u, v);
                    if (!predicate(slope, worldY))
                        continue;
                    double current = score(slope, worldY);
                    if (current >= bestScore)
                        continue;
                    Vector3 normal = data.GetInterpolatedNormal(u, v);
                    Vector3 world = terrain.transform.position + new Vector3(u * data.size.x, data.GetInterpolatedHeight(u, v), v * data.size.z);
                    best = new Site(kind, u, v, slope, worldY, world, normal);
                    bestScore = current;
                    found = true;
                }
            }
            if (!found)
                throw new InvalidOperationException($"No terrain site satisfied the {kind} verification constraints.");
            return best;
        }

        private static string BuildCaptureEvidence(string state, Terrain terrain, CaptureSites sites)
        {
            var text = new StringBuilder();
            text.AppendLine($"Sol Landscape G1 visual state={state}");
            text.AppendLine($"UTC={DateTime.UtcNow:O}; Unity={Application.unityVersion}; Graphics={SystemInfo.graphicsDeviceType}; Device={SystemInfo.graphicsDeviceName}");
            text.AppendLine("ControlledDaylight=Directional; Euler=(38,-35,0); Intensity=1.35; Color=white; Shadows=Soft; Ambient=Flat(0.35); PostProcessing=False; TreesAndFoliage=False; Weather=DryFrozen");
            text.AppendLine($"ContourSite={sites.Contour}");
            text.AppendLine($"LowAltitude60FaceSite={sites.LowFace}");
            text.AppendLine($"UnderwaterSteepFaceSite={sites.UnderwaterFace}");
            text.AppendLine($"ShorelineGrazingSite={sites.Shoreline}");
            text.AppendLine("Resolution=1280x720; Camera=Perspective60; Contour/face distance=16m; Shoreline distance=12m");
            return text.ToString();
        }

        private static string BuildGenerationEvidence(
            Terrain terrain,
            SolLandscapeConfig config,
            GenerationResult generation,
            VerificationResult verification,
            CaptureSites sites)
        {
            var text = new StringBuilder();
            text.AppendLine("Sol Landscape G1 alphamap generator and live-auto removal verification");
            text.AppendLine($"UTC={DateTime.UtcNow:O}; Unity={Application.unityVersion}; Graphics={SystemInfo.graphicsDeviceType}; Device={SystemInfo.graphicsDeviceName}");
            text.AppendLine($"Terrain={terrain.name}; Size={FormatVector(terrain.terrainData.size)}; Origin={FormatVector(terrain.transform.position)}; HeightmapResolution={terrain.terrainData.heightmapResolution}; Alphamap={generation.Width}x{generation.Height}x{generation.LayerCount}");
            text.AppendLine("RuleMath=CPU port of SolEvaluateLandscapeResponseCurve and SolEvaluateLandscapeDirectedResponse; cubic smoothstep, endpoint-preserving quadratic bias, signed directed lerp; claims are autoWeight*slopeRule*heightRule; cavity is required disabled for this heightmap bake");
            text.AppendLine($"CPUCurveVsDoubleReference=Samples:{generation.ResponseSampleCount}; MeanAbsError:{generation.ResponseMeanError.ToString("F12", Invariant)}; MaxAbsError:{generation.ResponseMaxError.ToString("F12", Invariant)}");
            text.AppendLine($"RuleInputs=TerrainData.GetSteepness(u,v) and terrainOriginY+TerrainData.GetInterpolatedHeight(u,v), sampled at alphamap texel centres; RelativeToWaterLevel rules subtract Water2Level={ResolveWaterLevel(config).ToString("R", Invariant)}");
            text.AppendLine($"RawGetHeightsBilinearAudit(NotRuleInput)=SlopeMAE:{generation.SlopeMeanError.ToString("F9", Invariant)}deg; SlopeMax:{generation.SlopeMaxError.ToString("F9", Invariant)}deg; HeightMAE:{generation.HeightMeanError.ToString("F9", Invariant)}m; HeightMax:{generation.HeightMaxError.ToString("F9", Invariant)}m; Decision=use Unity TerrainData evaluation to avoid this rare triangulation/normal divergence");
            text.AppendLine($"ShorelineBand=WorldY[{ShoreBandMin.ToString("F3", Invariant)},{ShoreBandMax.ToString("F3", Invariant)}]m; Width:{(ShoreBandMax - ShoreBandMin).ToString("F3", Invariant)}m; TerrainMetresPerAlphaTexelX:{(terrain.terrainData.size.x / generation.Width).ToString("F6", Invariant)}; TexelsAcrossBand:{((ShoreBandMax - ShoreBandMin) / (terrain.terrainData.size.x / generation.Width)).ToString("F3", Invariant)}");
            text.AppendLine("AuthoredRuleRationale=Stone2 reaches full claim at 40deg and Stone1 starts at 40deg, so rock owns every >40deg face without an altitude gate. Dirt/Grass reach zero at 40deg. Sand retains 10% of its flat-ground slope claim on steep underwater faces so Stone2 reads silted while rock remains dominant. The 8m shoreline ramp is resolvable and feeds the retained N-layer height interlock.");
            for (int index = 0; index < config.Layers.Count; ++index)
            {
                SolLandscapeLayerEntry entry = config.Layers[index];
                text.AppendLine(string.Format(
                    Invariant,
                    "Layer[{0}]={1}; Mode={2}; PreservePainted={3}; AutoWeight={4:R}; SlopeCenter={5:R}; SlopeContrast={6:R}; SlopeBias={7:R}; SlopeInfluence={8:R}; AltitudeReference={9}; HeightRange=({10:R},{11:R}); HeightBias={12:R}; HeightInfluence={13:R}; CavityScale={14:R}; CavityInfluence={15:R}; PositiveClaimFraction={16:F9}; DominantFraction={17:F9}; MeanPersistedWeight={18:F9}",
                    index,
                    entry.terrainLayer != null ? entry.terrainLayer.name : "null",
                    entry.mode,
                    entry.preservePaintedWeightDuringGeneration,
                    entry.autoWeight,
                    entry.slopeCenter,
                    entry.slopeContrast,
                    entry.slopeBias,
                    entry.slopeInfluence,
                    entry.altitudeReference,
                    entry.heightRange.x,
                    entry.heightRange.y,
                    entry.heightBias,
                    entry.heightInfluence,
                    entry.cavityScale,
                    entry.cavityInfluence,
                    generation.PositiveClaimFractions[index],
                    generation.DominantFractions[index],
                    generation.MeanWeights[index]));
            }
            text.AppendLine($"MinimumGeneratedClaim={generation.MinimumGeneratedClaim.ToString("R", Invariant)}; MinimumTotalBeforeWrite={generation.MinimumTotalBeforeWrite.ToString("R", Invariant)}; MinimumTotalAfterWrite={generation.MinimumTotalAfterWrite.ToString("R", Invariant)}; BlackImpossible={generation.MinimumTotalAfterWrite > 0f}");
            text.AppendLine($"SteepContract=SamplesAtOrAbove40deg:{generation.SteepSampleCount}; MinimumCombinedStoneWeight:{generation.MinimumSteepRockWeight.ToString("F9", Invariant)}");
            text.AppendLine($"PreservedPath={generation.PreservedDifference}");
            text.AppendLine($"AllSixManual={verification.AllManual}");
            text.AppendLine($"FiniteSweep={verification.FiniteSweep}");
            text.AppendLine($"PaintGrassOnGeneratedStone={verification.Paint}");
            text.AppendLine($"Shader={verification.Shader}");
            text.AppendLine("ControlledDaylight=Directional; Euler=(38,-35,0); Intensity=1.35; Color=white; Shadows=Soft; Ambient=Flat(0.35); PostProcessing=False; TreesAndFoliage=False; Weather=DryFrozen");
            text.AppendLine($"ContourSite={sites.Contour}");
            text.AppendLine($"LowAltitude60FaceSite={sites.LowFace}");
            text.AppendLine($"UnderwaterSteepFaceSite={sites.UnderwaterFace}");
            text.AppendLine($"ShorelineGrazingSite={sites.Shoreline}");
            text.AppendLine("Captured=G1_BlackContour_BeforeLiveAuto/AfterBakedManual; G1_LowAltitude60Face_BeforeLiveAuto/AfterBakedManual; G1_UnderwaterSteepFace_BeforeLiveAuto/AfterBakedManual; G1_ShorelineGrazing_BeforeLiveAuto/AfterBakedManual; G1_GrassPaintOnGeneratedStone");
            text.AppendLine($"RESULT={(verification.Passed ? "PASS" : "FAIL")}");
            return text.ToString();
        }

        internal static void ValidateInputs(Terrain terrain, SolLandscapeConfig config)
        {
            if (terrain == null || terrain.terrainData == null || config == null)
                throw new InvalidOperationException("Terrain, TerrainData and SolLandscapeConfig are required.");
            if (config.TerrainData != terrain.terrainData)
                throw new InvalidOperationException("The config TerrainData differs from the selected TerrainData.");
            if (config.Layers.Count != terrain.terrainData.alphamapLayers)
                throw new InvalidOperationException("Config layer count differs from TerrainData alphamap layer count.");
            if (terrain.transform.rotation != Quaternion.identity || terrain.transform.lossyScale != Vector3.one)
                throw new InvalidOperationException("The generator currently requires an unrotated, unit-scale Unity Terrain.");
            TerrainLayer[] layers = terrain.terrainData.terrainLayers;
            for (int index = 0; index < layers.Length; ++index)
            {
                SolLandscapeLayerEntry entry = config.Layers[index];
                if (entry == null || entry.terrainLayer != layers[index])
                    throw new InvalidOperationException($"Config and TerrainData layer order differ at index {index}.");
                if (entry.preservePaintedWeightDuringGeneration)
                    continue;
                if (Mathf.Abs(entry.cavityInfluence) > RuleEpsilon)
                    throw new InvalidOperationException($"Generated layer {layers[index].name} has a view-dependent cavity rule. G1 requires cavity influence zero for a heightmap bake.");
            }
        }

        internal static Terrain FindTerrain(SolLandscapeConfig config, bool openDemoSceneIfNeeded)
        {
            Terrain terrain = UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault(value => config != null && value.terrainData == config.TerrainData);
            if (terrain == null && openDemoSceneIfNeeded)
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                terrain = UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                    .FirstOrDefault(value => config != null && value.terrainData == config.TerrainData);
            }
            if (terrain == null)
                throw new InvalidOperationException("Open the scene containing the config TerrainData before generating alphamaps.");
            return terrain;
        }

        internal static float ResolveWaterLevel(SolLandscapeConfig config)
        {
            SolWaterBody[] bodies = UnityEngine.Object.FindObjectsByType<SolWaterBody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            SolWaterBody selected = null;
            foreach (SolWaterBody body in bodies)
            {
                if (body == null || !body.isActiveAndEnabled)
                    continue;
                if (body.IsInfinite)
                    return body.SurfaceLevel;
                if (selected == null || body.Priority > selected.Priority)
                    selected = body;
            }
            if (selected != null)
                return selected.SurfaceLevel;
            if (config != null && config.Layers.Any(entry => entry.altitudeReference == SolLandscapeAltitudeReference.RelativeToWaterLevel))
                throw new InvalidOperationException("A RelativeToWaterLevel landscape rule requires an active Water2 body in the production scene.");
            return 0f;
        }

        private static void RunCommandLine(Action<Terrain, SolLandscapeConfig, string> action)
        {
            int exitCode = 1;
            try
            {
                string outputDirectory = Path.GetFullPath(GetArgument(System.Environment.GetCommandLineArgs(), "-solG1Output", "../LandscapeG1Evidence"));
                Directory.CreateDirectory(outputDirectory);
                SolLandscapeConfig config = AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);
                Terrain terrain = FindTerrain(config, openDemoSceneIfNeeded: true);
                PrepareVisualState(terrain);
                action(terrain, config, outputDirectory);
                Debug.Log($"[Sol Landscape G1] PASS; output={outputDirectory}");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                exitCode = 1;
            }
            finally
            {
                EditorApplication.Exit(exitCode);
            }
        }

        private static void PrepareVisualState(Terrain terrain)
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
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
                throw new InvalidOperationException("The production landscape material is missing.");
            terrain.materialTemplate = material;
            Shader.SetGlobalFloat(SurfaceWetnessId, 0f);
            Shader.SetGlobalFloat(SurfaceSnowCoverId, 0f);
            Shader.SetGlobalFloat(SurfaceTemperatureId, 18f);
            Shader.SetGlobalVector(TerrainWetnessId, Vector4.zero);
            Shader.SetGlobalFloat(GlobalWaterLevelId, ResolveWaterLevel(AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath)));
            PublishDriver();
            terrain.Flush();
        }

        private static void PublishDriver()
        {
            SolLandscapeDriver driver = UnityEngine.Object.FindObjectsByType<SolLandscapeDriver>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault();
            if (driver == null)
                throw new InvalidOperationException("The SolLandscapeDriver is missing.");
            driver.Invalidate();
            MethodInfo method = typeof(SolLandscapeDriver).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new MissingMethodException(typeof(SolLandscapeDriver).FullName, "Publish");
            method.Invoke(driver, null);
            if (driver.LastPublishRefused)
                throw new InvalidOperationException($"Landscape publication refused: {driver.LastRefusalReason}");
        }

        private static Camera CreatePerspectiveCamera(ICollection<UnityEngine.Object> temporary)
        {
            var go = new GameObject("G1 Verification Camera") { hideFlags = HideFlags.HideAndDontSave };
            temporary.Add(go);
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 2000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.03f, 0.04f, 1f);
            camera.allowMSAA = true;
            UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false;
            data.renderShadows = true;
            return camera;
        }

        private static Camera CreateOrthographicCamera(ICollection<UnityEngine.Object> temporary)
        {
            Camera camera = CreatePerspectiveCamera(temporary);
            camera.orthographic = true;
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
            RenderSettings.sun = light;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.35f, 1f);
        }

        private static Color[] Render(Terrain terrain, Camera camera, int width, int height)
        {
            terrain.Flush();
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            { antiAliasing = 1, hideFlags = HideFlags.HideAndDontSave };
            var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
            { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture priorActive = RenderTexture.active;
            RenderTexture priorTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                texture.Apply(false, false);
                return texture.GetPixels();
            }
            finally
            {
                camera.targetTexture = priorTarget;
                RenderTexture.active = priorActive;
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static void WritePng(string path, Color[] pixels, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            try
            {
                texture.SetPixels(pixels);
                texture.Apply(false, false);
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static ShaderMetrics MeasureShader(Shader shader)
        {
            if (shader == null)
                return new ShaderMetrics(false, 0, 1);
            ShaderMessage[] messages = ShaderUtil.GetShaderMessages(shader);
            return new ShaderMetrics(
                shader.isSupported,
                messages.Count(message => message.severity == ShaderCompilerMessageSeverity.Warning),
                messages.Count(message => message.severity == ShaderCompilerMessageSeverity.Error));
        }

        private static double[] ExtractPreserved(float[,,] source, IReadOnlyList<int> indices)
        {
            double[] values = new double[source.GetLength(0) * source.GetLength(1) * indices.Count];
            int cursor = 0;
            for (int y = 0; y < source.GetLength(0); ++y)
                for (int x = 0; x < source.GetLength(1); ++x)
                    foreach (int index in indices)
                        values[cursor++] = source[y, x, index];
            return values;
        }

        private static DifferenceMetrics ComparePreserved(double[] expected, float[,,] actual, IReadOnlyList<int> indices)
        {
            double sum = 0d;
            double max = 0d;
            long changed = 0;
            int cursor = 0;
            for (int y = 0; y < actual.GetLength(0); ++y)
            {
                for (int x = 0; x < actual.GetLength(1); ++x)
                {
                    foreach (int index in indices)
                    {
                        float expectedFloat = (float)expected[cursor++];
                        float value = actual[y, x, index];
                        double delta = Math.Abs(expectedFloat - value);
                        sum += delta;
                        max = Math.Max(max, delta);
                        if (BitConverter.SingleToInt32Bits(expectedFloat) != BitConverter.SingleToInt32Bits(value))
                            ++changed;
                    }
                }
            }
            return new DifferenceMetrics(expected.Length, changed, sum / expected.Length, max);
        }

        private static DifferenceMetrics CompareChannel(float[,,] expected, float[,,] actual, int channel)
        {
            double sum = 0d;
            double max = 0d;
            long changed = 0;
            long count = expected.GetLength(0) * expected.GetLength(1);
            for (int y = 0; y < expected.GetLength(0); ++y)
                for (int x = 0; x < expected.GetLength(1); ++x)
                {
                    float a = expected[y, x, channel];
                    float b = actual[y, x, channel];
                    double delta = Math.Abs(a - b);
                    sum += delta;
                    max = Math.Max(max, delta);
                    if (BitConverter.SingleToInt32Bits(a) != BitConverter.SingleToInt32Bits(b))
                        ++changed;
                }
            return new DifferenceMetrics(count, changed, sum / count, max);
        }

        private static DifferenceMetrics CompareAll(float[,,] expected, float[,,] actual)
        {
            double sum = 0d;
            double max = 0d;
            long changed = 0;
            long count = expected.LongLength;
            for (int y = 0; y < expected.GetLength(0); ++y)
                for (int x = 0; x < expected.GetLength(1); ++x)
                    for (int layer = 0; layer < expected.GetLength(2); ++layer)
                    {
                        float a = expected[y, x, layer];
                        float b = actual[y, x, layer];
                        double delta = Math.Abs(a - b);
                        sum += delta;
                        max = Math.Max(max, delta);
                        if (BitConverter.SingleToInt32Bits(a) != BitConverter.SingleToInt32Bits(b))
                            ++changed;
                    }
            return new DifferenceMetrics(count, changed, sum / count, max);
        }

        private static float MinimumTotal(float[,,] maps)
        {
            float minimum = float.PositiveInfinity;
            for (int y = 0; y < maps.GetLength(0); ++y)
                for (int x = 0; x < maps.GetLength(1); ++x)
                {
                    float total = 0f;
                    for (int layer = 0; layer < maps.GetLength(2); ++layer)
                        total += maps[y, x, layer];
                    minimum = Mathf.Min(minimum, total);
                }
            return minimum;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static string GetArgument(string[] arguments, string name, string fallback)
        {
            for (int index = 0; index < arguments.Length - 1; ++index)
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    return arguments[index + 1];
            return fallback;
        }

        private static string FormatVector(Vector3 value) => string.Format(Invariant, "({0:F6},{1:F6},{2:F6})", value.x, value.y, value.z);

        private static void DestroyTemporary(IEnumerable<UnityEngine.Object> temporary)
        {
            foreach (UnityEngine.Object item in temporary.Where(value => value != null).Reverse())
                UnityEngine.Object.DestroyImmediate(item);
        }

        private readonly struct HeightSampler
        {
            private readonly float[,] heights;
            private readonly int width;
            private readonly int height;
            private readonly Vector3 size;

            public HeightSampler(float[,] heights, Vector3 size)
            {
                this.heights = heights;
                height = heights.GetLength(0);
                width = heights.GetLength(1);
                this.size = size;
            }

            public HeightSample Sample(float u, float v)
            {
                float px = Mathf.Clamp01(u) * (width - 1);
                float py = Mathf.Clamp01(v) * (height - 1);
                int x0 = Mathf.Min(Mathf.FloorToInt(px), width - 2);
                int y0 = Mathf.Min(Mathf.FloorToInt(py), height - 2);
                int x1 = x0 + 1;
                int y1 = y0 + 1;
                float tx = px - x0;
                float ty = py - y0;
                float h00 = heights[y0, x0];
                float h10 = heights[y0, x1];
                float h01 = heights[y1, x0];
                float h11 = heights[y1, x1];
                float normalizedHeight = Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), ty);
                float derivativeU = Mathf.Lerp(h10 - h00, h11 - h01, ty) * (width - 1);
                float derivativeV = Mathf.Lerp(h01 - h00, h11 - h10, tx) * (height - 1);
                float dydx = derivativeU * size.y / size.x;
                float dydz = derivativeV * size.y / size.z;
                Vector3 normal = new Vector3(-dydx, 1f, -dydz).normalized;
                float slope = Mathf.Acos(Mathf.Clamp01(normal.y)) * Mathf.Rad2Deg;
                return new HeightSample(normalizedHeight * size.y, slope);
            }
        }

        private readonly struct HeightSample
        {
            public HeightSample(float localHeight, float slopeDegrees)
            {
                LocalHeight = localHeight;
                SlopeDegrees = slopeDegrees;
            }
            public float LocalHeight { get; }
            public float SlopeDegrees { get; }
        }

        private enum SiteKind { Contour, LowFace, UnderwaterFace, Shoreline }

        private readonly struct Site
        {
            public Site(SiteKind kind, float u, float v, float slope, float worldY, Vector3 worldPosition, Vector3 normal)
            {
                Kind = kind;
                U = u;
                V = v;
                Slope = slope;
                WorldY = worldY;
                WorldPosition = worldPosition;
                Normal = normal;
            }
            public SiteKind Kind { get; }
            public float U { get; }
            public float V { get; }
            public float Slope { get; }
            public float WorldY { get; }
            public Vector3 WorldPosition { get; }
            public Vector3 Normal { get; }
            public override string ToString() => string.Format(Invariant, "Kind:{0}; UV:({1:F6},{2:F6}); World:{3}; Slope:{4:F6}; WorldY:{5:F6}; Normal:{6}", Kind, U, V, FormatVector(WorldPosition), Slope, WorldY, FormatVector(Normal));
        }

        private readonly struct CaptureSites
        {
            public CaptureSites(Site contour, Site lowFace, Site underwaterFace, Site shoreline)
            {
                Contour = contour;
                LowFace = lowFace;
                UnderwaterFace = underwaterFace;
                Shoreline = shoreline;
            }
            public Site Contour { get; }
            public Site LowFace { get; }
            public Site UnderwaterFace { get; }
            public Site Shoreline { get; }
        }

        private readonly struct DifferenceMetrics
        {
            public DifferenceMetrics(long samples, long changed, double meanAbsoluteError, double maximumError)
            {
                Samples = samples;
                Changed = changed;
                MeanAbsoluteError = meanAbsoluteError;
                MaximumError = maximumError;
            }
            public long Samples { get; }
            public long Changed { get; }
            public double MeanAbsoluteError { get; }
            public double MaximumError { get; }
            public bool BitExact => Changed == 0;
            public override string ToString() => string.Format(Invariant, "Samples:{0}; Changed:{1}; MAE:{2:F12}; Max:{3:F12}; BitExact:{4}", Samples, Changed, MeanAbsoluteError, MaximumError, BitExact);
        }

        private readonly struct GenerationResult
        {
            public GenerationResult(int width, int height, int layerCount, float[,,] persistedMaps, int[] preservedIndices, double[] positiveClaimFractions, double[] dominantFractions, double[] meanWeights, float minimumGeneratedClaim, float minimumTotalBeforeWrite, float minimumTotalAfterWrite, long steepSampleCount, float minimumSteepRockWeight, DifferenceMetrics preservedDifference, long responseSampleCount, double responseMeanError, double responseMaxError, double slopeMeanError, double slopeMaxError, double heightMeanError, double heightMaxError)
            {
                Width = width; Height = height; LayerCount = layerCount; PersistedMaps = persistedMaps; PreservedIndices = preservedIndices;
                PositiveClaimFractions = positiveClaimFractions; DominantFractions = dominantFractions; MeanWeights = meanWeights;
                MinimumGeneratedClaim = minimumGeneratedClaim; MinimumTotalBeforeWrite = minimumTotalBeforeWrite; MinimumTotalAfterWrite = minimumTotalAfterWrite;
                SteepSampleCount = steepSampleCount; MinimumSteepRockWeight = minimumSteepRockWeight; PreservedDifference = preservedDifference;
                ResponseSampleCount = responseSampleCount; ResponseMeanError = responseMeanError; ResponseMaxError = responseMaxError; SlopeMeanError = slopeMeanError; SlopeMaxError = slopeMaxError; HeightMeanError = heightMeanError; HeightMaxError = heightMaxError;
            }
            public int Width { get; } public int Height { get; } public int LayerCount { get; }
            public float[,,] PersistedMaps { get; } public int[] PreservedIndices { get; }
            public double[] PositiveClaimFractions { get; } public double[] DominantFractions { get; } public double[] MeanWeights { get; }
            public float MinimumGeneratedClaim { get; } public float MinimumTotalBeforeWrite { get; } public float MinimumTotalAfterWrite { get; }
            public long SteepSampleCount { get; } public float MinimumSteepRockWeight { get; } public DifferenceMetrics PreservedDifference { get; }
            public long ResponseSampleCount { get; } public double ResponseMeanError { get; } public double ResponseMaxError { get; }
            public double SlopeMeanError { get; } public double SlopeMaxError { get; } public double HeightMeanError { get; } public double HeightMaxError { get; }
            public string Summary => string.Format(Invariant, "{0}x{1}x{2}; min total after write={3:R}; preserved={4}; steep min rock={5:F6}", Width, Height, LayerCount, MinimumTotalAfterWrite, PreservedDifference, MinimumSteepRockWeight);
        }

        private readonly struct FiniteSweepMetrics
        {
            public FiniteSweepMetrics(long samples, long nonFinitePixels, string perSize) { Samples = samples; NonFinitePixels = nonFinitePixels; PerSize = perSize; }
            public long Samples { get; } public long NonFinitePixels { get; } public string PerSize { get; }
            public override string ToString() => $"Samples:{Samples}; NonFinitePixels:{NonFinitePixels}; PerSize:[{PerSize}]";
        }

        private readonly struct PaintTestResult
        {
            public PaintTestResult(bool survived, DifferenceMetrics grassDifference, DifferenceMetrics pathDuringPaint, DifferenceMetrics restoration, bool restoredPathBitExact)
            { Survived = survived; GrassDifference = grassDifference; PathDuringPaint = pathDuringPaint; Restoration = restoration; RestoredPathBitExact = restoredPathBitExact; }
            public bool Survived { get; } public DifferenceMetrics GrassDifference { get; } public DifferenceMetrics PathDuringPaint { get; } public DifferenceMetrics Restoration { get; } public bool RestoredPathBitExact { get; }
            public override string ToString() => $"Survived:{Survived}; GrassAfterPublish={GrassDifference}; PathDuringPaint={PathDuringPaint}; Restoration={Restoration}; RestoredPathBitExact:{RestoredPathBitExact}";
        }

        private readonly struct ShaderMetrics
        {
            public ShaderMetrics(bool supported, int warnings, int errors) { Supported = supported; Warnings = warnings; Errors = errors; }
            public bool Supported { get; } public int Warnings { get; } public int Errors { get; }
            public override string ToString() => $"Supported:{Supported}; Warnings:{Warnings}; Errors:{Errors}";
        }

        private readonly struct VerificationResult
        {
            public VerificationResult(bool passed, bool allManual, FiniteSweepMetrics finiteSweep, PaintTestResult paint, ShaderMetrics shader)
            { Passed = passed; AllManual = allManual; FiniteSweep = finiteSweep; Paint = paint; Shader = shader; }
            public bool Passed { get; } public bool AllManual { get; } public FiniteSweepMetrics FiniteSweep { get; } public PaintTestResult Paint { get; } public ShaderMetrics Shader { get; }
        }
    }
}
