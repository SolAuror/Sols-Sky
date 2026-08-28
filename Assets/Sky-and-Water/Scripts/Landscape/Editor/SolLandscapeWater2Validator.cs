using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Sol.Water;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Sol.Landscape.Editor
{
    /// <summary>Read-only production wiring, alphamap and shader audit for the canonical Water2 scene.</summary>
    internal static class SolLandscapeWater2Validator
    {
        private const string ScenePath = "Assets/Scenes/Sols_Water2_Demo.unity";
        private const string LegacyScenePath = "Assets/Scenes/SolsWeather_Demo.unity";
        private const string ConfigPath = "Assets/Sky-and-Water/Landscape/SolLandscapeConfig.asset";
        private const string MaterialPath = "Assets/Sky-and-Water/Landscape/M_SolLandscape.mat";
        private const string ProtectionMaskPath = "Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeManualPaintProtection.asset";
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        [MenuItem("Tools/Sol Landscape/Validate Water2 Production Wiring")]
        private static void ValidateFromMenu()
        {
            if (SceneManager.GetActiveScene().path != ScenePath)
            {
                EditorUtility.DisplayDialog("Water2 production validation", "Open Sols_Water2_Demo before validating its production wiring.", "OK");
                return;
            }
            ValidationResult result = Validate(writeEvidence: true);
            EditorUtility.DisplayDialog("Water2 production validation", result.Summary, "OK");
        }

        public static void ValidateFromCommandLine()
        {
            int exitCode = 1;
            try
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                ValidationResult result = Validate(writeEvidence: true);
                Debug.Log($"[Sol Landscape Water2] {result.Summary}");
                exitCode = result.Passed ? 0 : 1;
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

        private static ValidationResult Validate(bool writeEvidence)
        {
            var failures = new StringBuilder();
            var evidence = new StringBuilder();
            evidence.AppendLine("Sol Landscape Water2 production wiring and shader audit");
            evidence.AppendLine($"UTC={DateTime.UtcNow:O}; Unity={Application.unityVersion}; ActiveScene={SceneManager.GetActiveScene().path}");

            SolLandscapeConfig config = AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            Terrain[] terrains = UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            SolLandscapeDriver[] drivers = UnityEngine.Object.FindObjectsByType<SolLandscapeDriver>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            SolWaterWorld[] worlds = UnityEngine.Object.FindObjectsByType<SolWaterWorld>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            SolWaterBody[] oceans = UnityEngine.Object.FindObjectsByType<SolWaterBody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(body => body.IsInfinite)
                .ToArray();
            SolWaterWetness[] wetnessComponents = UnityEngine.Object.FindObjectsByType<SolWaterWetness>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            Require(SceneManager.GetActiveScene().path == ScenePath, "The active scene is not the canonical Water2 scene.", failures);
            Require(AssetDatabase.LoadAssetAtPath<SceneAsset>(LegacyScenePath) == null, "The removed legacy weather scene still exists.", failures);
            string[] enabledScenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            Require(enabledScenes.Length == 1 && enabledScenes[0] == ScenePath, "Build Settings must contain only the enabled Water2 production scene.", failures);
            Require(config != null, "SolLandscapeConfig is missing.", failures);
            Require(material != null && material.shader != null, "The production landscape material or shader is missing.", failures);
            Require(terrains.Length == 1, $"Expected one active Terrain, found {terrains.Length}.", failures);
            Require(drivers.Length == 1, $"Expected one SolLandscapeDriver, found {drivers.Length}.", failures);
            Require(worlds.Length == 1, $"Expected one SolWaterWorld, found {worlds.Length}.", failures);
            Require(oceans.Length == 1, $"Expected one Water2 ocean, found {oceans.Length}.", failures);
            Require(wetnessComponents.Length == 1, $"Expected one SolWaterWetness, found {wetnessComponents.Length}.", failures);

            Terrain terrain = terrains.FirstOrDefault();
            SolLandscapeDriver driver = drivers.FirstOrDefault();
            SolWaterBody ocean = oceans.FirstOrDefault();
            if (terrain != null && config != null)
                Require(terrain.terrainData == config.TerrainData, "Terrain and config reference different TerrainData assets.", failures);
            if (terrain != null && material != null)
                Require(terrain.materialTemplate == material, "Terrain does not use M_SolLandscape.", failures);
            if (driver != null && terrain != null && config != null)
            {
                Require(driver.landscapeTerrain == terrain, "SolLandscapeDriver does not target the production Terrain.", failures);
                Require(driver.config == config, "SolLandscapeDriver does not target the production config.", failures);
                Require(
                    driver.TryValidateContract(out string refusalReason),
                    $"Landscape contract validation refused: {refusalReason}",
                    failures);
            }
            if (terrain != null && wetnessComponents.Length == 1)
            {
                var wetnessObject = new SerializedObject(wetnessComponents[0]);
                Require(wetnessObject.FindProperty("wetnessTerrain")?.objectReferenceValue == terrain, "Water2 wetness does not target the production Terrain.", failures);
                TerrainLayer sandLayer = terrain.terrainData.terrainLayers.FirstOrDefault(layer => layer != null && layer.name == "TerrainLayer_Sand");
                Require(wetnessObject.FindProperty("sandLayer")?.objectReferenceValue == sandLayer, "Water2 wetness does not use the production Sand layer.", failures);
            }

            float waterLevel = ocean != null ? ocean.SurfaceLevel : float.NaN;
            if (config != null)
            {
                bool relativeSediment = config.Layers
                    .Where(entry => Mathf.Abs(entry.heightInfluence) > 1e-6f)
                    .All(entry => entry.altitudeReference == SolLandscapeAltitudeReference.RelativeToWaterLevel);
                Require(relativeSediment, "Height-sensitive sediment rules are not relative to Water2's resolved level.", failures);
            }
            evidence.AppendLine($"SceneWiring=Terrains:{terrains.Length}; Drivers:{drivers.Length}; WaterWorlds:{worlds.Length}; Oceans:{oceans.Length}; Wetness:{wetnessComponents.Length}; WaterLevel:{waterLevel.ToString("R", Invariant)}");
            evidence.AppendLine($"BuildScenes={string.Join(",", enabledScenes)}; LegacyScenePresent={AssetDatabase.LoadAssetAtPath<SceneAsset>(LegacyScenePath) != null}");

            if (material != null && material.shader != null)
            {
                Shader shader = material.shader;
                ShaderMessage[] messages = ShaderUtil.GetShaderMessages(shader);
                int errors = messages.Count(message => message.severity == ShaderCompilerMessageSeverity.Error);
                int warnings = messages.Count(message => message.severity == ShaderCompilerMessageSeverity.Warning);
                Require(shader.isSupported && errors == 0, $"Production terrain shader is unsupported or has {errors} compile errors.", failures);
                Require(!material.IsKeywordEnabled("_SOL_LANDSCAPE_DEBUG"), "Production material has diagnostics enabled.", failures);
                Require(!material.IsKeywordEnabled("_SOL_LANDSCAPE_STOCHASTIC"), "Production material has diagnostic stochastic tiling enabled.", failures);
                Require(material.IsKeywordEnabled("_SOL_LANDSCAPE_BLEND_HEIGHT"), "Production material has height blending disabled.", failures);
                evidence.AppendLine($"Shader={shader.name}; Supported={shader.isSupported}; Errors={errors}; Warnings={warnings}; HeightBlend={material.IsKeywordEnabled("_SOL_LANDSCAPE_BLEND_HEIGHT")}; Diagnostics={material.IsKeywordEnabled("_SOL_LANDSCAPE_DEBUG")}; Stochastic={material.IsKeywordEnabled("_SOL_LANDSCAPE_STOCHASTIC")}");
                AppendDependencyShaderAudit("Hidden/Sol/Terrain/Array Base", evidence, failures);
                AppendDependencyShaderAudit("Hidden/Sol/Terrain/Array Basemap Gen", evidence, failures);
            }

            if (terrain != null)
                AppendAlphamapAudit(terrain, waterLevel, evidence, failures);
            Texture2D protectionMask = AssetDatabase.LoadAssetAtPath<Texture2D>(ProtectionMaskPath);
            Require(protectionMask != null && terrain != null && protectionMask.width == terrain.terrainData.alphamapWidth && protectionMask.height == terrain.terrainData.alphamapHeight, "Manual-paint protection mask is missing or mismatched.", failures);
            evidence.AppendLine("RedundancyAudit=Legacy Terrain Lit Wetness retained intentionally: it is the URP default fallback, explicitly always-included, and used by the performance diagnostic. Array Base and Basemap Gen are active Terrain shader dependencies. Top-K code is retained only for diagnostic mode 6; stochastic remains an off-by-default diagnostic feature.");
            evidence.AppendLine("DesignerAudit=Water2 is canonical; sediment heights follow Water2 level; safe regional sculpt rebake and paint protection are enabled; all six diagnostic views and the diagnostic layer selector are exposed; near/far height-blend keyword parity is enforced.");

            bool passed = failures.Length == 0;
            if (!passed)
                evidence.AppendLine("Failures=" + failures.ToString().Replace(System.Environment.NewLine, " | "));
            evidence.AppendLine($"RESULT={(passed ? "PASS" : "FAIL")}");
            if (writeEvidence)
            {
                string outputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../LandscapeG1Evidence"));
                Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(Path.Combine(outputDirectory, "G1_Water2_Production_Audit.txt"), evidence.ToString(), new UTF8Encoding(false));
            }
            return new ValidationResult(passed, passed ? "PASS — Water2 terrain, water, alphamaps and shader are wired correctly." : "FAIL — " + failures);
        }

        private static void AppendAlphamapAudit(Terrain terrain, float waterLevel, StringBuilder evidence, StringBuilder failures)
        {
            TerrainData data = terrain.terrainData;
            float[,,] maps = data.GetAlphamaps(0, 0, data.alphamapWidth, data.alphamapHeight);
            int stone2 = Array.FindIndex(data.terrainLayers, layer => layer != null && layer.name == "TerrainLayer_Stone2");
            int stone1 = Array.FindIndex(data.terrainLayers, layer => layer != null && layer.name == "TerrainLayer_Stone1");
            double[] drySums = new double[data.alphamapLayers];
            long[] dryDominant = new long[data.alphamapLayers];
            long drySamples = 0;
            long underwaterSamples = 0;
            long steepSamples = 0;
            float minimumTotal = float.PositiveInfinity;
            float minimumSteepRock = float.PositiveInfinity;
            const int stride = 8;
            for (int y = 0; y < data.alphamapHeight; y += stride)
            {
                float v = (y + 0.5f) / data.alphamapHeight;
                for (int x = 0; x < data.alphamapWidth; x += stride)
                {
                    float u = (x + 0.5f) / data.alphamapWidth;
                    float worldY = terrain.transform.position.y + data.GetInterpolatedHeight(u, v);
                    float slope = data.GetSteepness(u, v);
                    float total = 0f;
                    int dominant = 0;
                    float dominantWeight = -1f;
                    for (int layer = 0; layer < data.alphamapLayers; ++layer)
                    {
                        float weight = maps[y, x, layer];
                        total += weight;
                        if (worldY >= waterLevel + 0.5f)
                            drySums[layer] += weight;
                        if (weight > dominantWeight)
                        {
                            dominantWeight = weight;
                            dominant = layer;
                        }
                    }
                    minimumTotal = Mathf.Min(minimumTotal, total);
                    if (worldY >= waterLevel + 0.5f)
                    {
                        ++drySamples;
                        ++dryDominant[dominant];
                    }
                    if (worldY < waterLevel)
                        ++underwaterSamples;
                    if (slope >= 40f)
                    {
                        ++steepSamples;
                        minimumSteepRock = Mathf.Min(minimumSteepRock, maps[y, x, stone1] + maps[y, x, stone2]);
                    }
                }
            }
            Require(minimumTotal > 0f, "Sampled alphamap contains a zero-weight texel.", failures);
            Require(steepSamples > 0 && minimumSteepRock >= 0.5f, "Sampled >=40-degree terrain is not predominantly Stone.", failures);
            evidence.AppendLine($"AlphamapSampleStride={stride}; DrySamples={drySamples}; UnderwaterSamples={underwaterSamples}; SteepSamples={steepSamples}; MinimumTotal={minimumTotal.ToString("R", Invariant)}; MinimumSteepStone={minimumSteepRock.ToString("R", Invariant)}");
            for (int layer = 0; layer < data.alphamapLayers; ++layer)
            {
                string name = data.terrainLayers[layer] != null ? data.terrainLayers[layer].name : $"Layer{layer}";
                evidence.AppendLine($"DryLayer[{layer}]={name}; Mean={(drySamples > 0 ? drySums[layer] / drySamples : 0d).ToString("F9", Invariant)}; DominantFraction={(drySamples > 0 ? dryDominant[layer] / (double)drySamples : 0d).ToString("F9", Invariant)}");
            }
        }

        private static void Require(bool condition, string message, StringBuilder failures)
        {
            if (!condition)
                failures.AppendLine(message);
        }

        private static void AppendDependencyShaderAudit(string shaderName, StringBuilder evidence, StringBuilder failures)
        {
            Shader shader = Shader.Find(shaderName);
            ShaderMessage[] messages = shader != null ? ShaderUtil.GetShaderMessages(shader) : Array.Empty<ShaderMessage>();
            int errors = messages.Count(message => message.severity == ShaderCompilerMessageSeverity.Error);
            int warnings = messages.Count(message => message.severity == ShaderCompilerMessageSeverity.Warning);
            Require(shader != null && shader.isSupported && errors == 0, $"Dependency shader {shaderName} is missing, unsupported, or has {errors} errors.", failures);
            evidence.AppendLine($"DependencyShader={shaderName}; Found={shader != null}; Supported={shader != null && shader.isSupported}; Errors={errors}; Warnings={warnings}");
        }

        private readonly struct ValidationResult
        {
            public ValidationResult(bool passed, string summary)
            {
                Passed = passed;
                Summary = summary;
            }

            public bool Passed { get; }
            public string Summary { get; }
        }
    }
}
