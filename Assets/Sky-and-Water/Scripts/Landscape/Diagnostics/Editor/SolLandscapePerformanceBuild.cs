using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Sol.Landscape.Diagnostics;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sol.Landscape.Diagnostics.Editor
{
    /// <summary>
    /// Builds ticket 5A's release Windows player and records the variants that reach
    /// the compiler after Unity/URP stripping callbacks have run.
    ///
    /// Ticket 5G (N12): the harness's persisted materials/ScriptableObject live in
    /// <see cref="HarnessAssetsFolder"/>, a plain (non-Resources) folder, so they carry no build
    /// cost by default. Immediately before BuildPipeline.BuildPlayer this build script stages a
    /// COPY of the ScriptableObject into a Resources/ folder -- Unity then pulls the referenced
    /// harness materials in as build dependencies for exactly this one build -- and deletes that
    /// staged copy again immediately after, win or lose, so the checked-in project state (and any
    /// ordinary production build made some other way) never has a Resources/ presence for this at
    /// all. This was chosen over an editor-only assembly definition because
    /// SolLandscapePerformanceHarness.cs (the MonoBehaviour) must still compile into and run inside
    /// the STANDALONE PLAYER during a diagnostic build -- an editor-only asmdef would strip it from
    /// every player build, including this one, and break the harness entirely.
    /// </summary>
    public static class SolLandscapePerformanceBuild
    {
        private const string ScenePath = "Assets/Scenes/SolsWeather_Demo.unity";
        private const string HarnessAssetsFolder = "Assets/Sky-and-Water/Scripts/Landscape/Diagnostics/HarnessAssets";
        private const string ResourcesFolder = "Assets/Sky-and-Water/Scripts/Landscape/Diagnostics/Resources";
        private const string ResourceAssetPersistedPath = HarnessAssetsFolder + "/SolLandscapePerformanceAssets.asset";
        private const string ResourceAssetStagedPath = ResourcesFolder + "/SolLandscapePerformanceAssets.asset";
        private const string LegacyHarnessMaterialPath = HarnessAssetsFolder + "/M_LegacyPerformanceHarness.mat";
        private const string ArrayStochasticHarnessMaterialPath = HarnessAssetsFolder + "/M_ArrayPerformanceHarness_Stochastic.mat";
        private const string ArrayMaterialPath = "Assets/Sky-and-Water/Landscape/M_SolLandscape.mat";
        private const string LegacyMaterialPath = "Assets/Sky-and-Water/Shaders/Terrain/M_TerrainWetness.mat";
        private const string StochasticKeyword = "_SOL_LANDSCAPE_STOCHASTIC";

        public static void BuildFromCommandLine()
        {
            BuildFromCommandLine(collectVariantEvidence: true);
        }

        /// <summary>
        /// Ticket 5A.1 timing-only build. It deliberately leaves the accepted P5 variant collector
        /// inactive so the closed variant measurement is not re-run.
        /// </summary>
        public static void BuildTimerFromCommandLine()
        {
            BuildFromCommandLine(collectVariantEvidence: false);
        }

        private static void BuildFromCommandLine(bool collectVariantEvidence)
        {
            int exitCode = 1;
            bool staged = false;
            try
            {
                string[] arguments = System.Environment.GetCommandLineArgs();
                string buildPath = Path.GetFullPath(GetArgument(arguments, "-solPerfBuildPath", "../builds/Landscape5A/SolLandscapePerf.exe"));
                string variantPath = Path.GetFullPath(GetArgument(arguments, "-solPerfVariantOutput", "../Landscape5AEvidence/5A_PostStripVariants.txt"));
                EnsureHarnessAssets();
                StageResourceAsset();
                staged = true;
                Directory.CreateDirectory(Path.GetDirectoryName(buildPath) ?? ".");
                Directory.CreateDirectory(Path.GetDirectoryName(variantPath) ?? ".");

                if (collectVariantEvidence)
                {
                    SolLandscapeShaderVariantCollector.Begin();
                }
                BuildPlayerOptions options = new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = buildPath,
                    target = BuildTarget.StandaloneWindows64,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.CleanBuildCache
                        | BuildOptions.DetailedBuildReport
                        | BuildOptions.StrictMode
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                bool success = report.summary.result == BuildResult.Succeeded
                    && report.summary.totalErrors == 0;
                if (collectVariantEvidence)
                {
                    string variantReport = SolLandscapeShaderVariantCollector.End(report, buildPath);
                    File.WriteAllText(variantPath, variantReport, new UTF8Encoding(false));
                    success = success
                        && SolLandscapeShaderVariantCollector.LastArrayVariantCount > 0
                        && SolLandscapeShaderVariantCollector.LastLegacyVariantCount > 0;
                    Debug.Log(
                        $"[Sol Landscape 5A] Build result={report.summary.result}; errors={report.summary.totalErrors}; " +
                        $"arrayPostStrip={SolLandscapeShaderVariantCollector.LastArrayVariantCount}; " +
                        $"legacyPostStrip={SolLandscapeShaderVariantCollector.LastLegacyVariantCount}; " +
                        $"variants={variantPath}");
                }
                else
                {
                    Debug.Log(
                        $"[Sol Landscape 5A.1] Timer-only build result={report.summary.result}; " +
                        $"errors={report.summary.totalErrors}; variant collector not run.");
                }
                exitCode = success ? 0 : 1;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                exitCode = 1;
            }
            finally
            {
                SolLandscapeShaderVariantCollector.Cancel();
                if (staged)
                {
                    UnstageResourceAsset();
                }
                EditorApplication.Exit(exitCode);
            }
        }

        /// <summary>Ticket 5G (N12): creates/updates the harness's persisted materials and ScriptableObject in HarnessAssetsFolder -- a plain folder, not Resources/, so this step alone has no build-time effect.</summary>
        private static void EnsureHarnessAssets()
        {
            Material arrayMaterial = AssetDatabase.LoadAssetAtPath<Material>(ArrayMaterialPath);
            Material legacyMaterial = AssetDatabase.LoadAssetAtPath<Material>(LegacyMaterialPath);
            if (arrayMaterial == null || legacyMaterial == null)
            {
                throw new InvalidOperationException("The array and retained legacy terrain materials must both exist.");
            }

            Directory.CreateDirectory(Path.GetFullPath(HarnessAssetsFolder));
            AssetDatabase.Refresh();

            Material legacyHarnessMaterial = AssetDatabase.LoadAssetAtPath<Material>(LegacyHarnessMaterialPath);
            if (legacyHarnessMaterial == null)
            {
                legacyHarnessMaterial = new Material(legacyMaterial)
                {
                    name = "M_LegacyPerformanceHarness",
                    enableInstancing = true
                };
                AssetDatabase.CreateAsset(legacyHarnessMaterial, LegacyHarnessMaterialPath);
            }
            else
            {
                legacyHarnessMaterial.shader = legacyMaterial.shader;
                legacyHarnessMaterial.CopyPropertiesFromMaterial(legacyMaterial);
                legacyHarnessMaterial.shaderKeywords = legacyMaterial.shaderKeywords;
                legacyHarnessMaterial.enableInstancing = true;
                EditorUtility.SetDirty(legacyHarnessMaterial);
            }

            // _SOL_LANDSCAPE_STOCHASTIC is shader_feature_local with no material [Toggle]; Unity's
            // build-time stripper only keeps its ON variant if some serialized material in the
            // build already has it enabled -- toggling it on arrayMaterial at RUNTIME in the player
            // is not enough by itself (verified in 5F.2: doing only that produced zero post-strip
            // ON variants). This persisted harness copy exists so the ON variant actually ships
            // whenever this build script stages it in.
            Material stochasticHarnessMaterial = CreateOrUpdateArrayHarnessMaterial(
                arrayMaterial, ArrayStochasticHarnessMaterialPath, "M_ArrayPerformanceHarness_Stochastic", stochasticOn: true);

            SolLandscapePerformanceAssets resource = AssetDatabase.LoadAssetAtPath<SolLandscapePerformanceAssets>(ResourceAssetPersistedPath);
            if (resource == null)
            {
                resource = ScriptableObject.CreateInstance<SolLandscapePerformanceAssets>();
                AssetDatabase.CreateAsset(resource, ResourceAssetPersistedPath);
            }

            resource.arrayMaterial = arrayMaterial;
            resource.arrayMaterialStochastic = stochasticHarnessMaterial;
            resource.legacyMaterial = legacyHarnessMaterial;
            EditorUtility.SetDirty(resource);
            AssetDatabase.SaveAssets();
        }

        /// <summary>Ticket 5G (N12): copies the persisted ScriptableObject into Resources/ so Resources.Load finds it and its referenced materials are pulled in as build dependencies -- for exactly this one build.</summary>
        private static void StageResourceAsset()
        {
            Directory.CreateDirectory(Path.GetFullPath(ResourcesFolder));
            AssetDatabase.Refresh();
            if (AssetDatabase.LoadAssetAtPath<SolLandscapePerformanceAssets>(ResourceAssetStagedPath) != null)
            {
                AssetDatabase.DeleteAsset(ResourceAssetStagedPath);
            }

            if (!AssetDatabase.CopyAsset(ResourceAssetPersistedPath, ResourceAssetStagedPath))
            {
                throw new InvalidOperationException($"Failed to stage {ResourceAssetPersistedPath} into {ResourceAssetStagedPath}.");
            }

            AssetDatabase.Refresh();
            SolLandscapePerformanceAssets staged = AssetDatabase.LoadAssetAtPath<SolLandscapePerformanceAssets>(ResourceAssetStagedPath);
            if (staged == null || staged.arrayMaterial == null || staged.arrayMaterialStochastic == null || staged.legacyMaterial == null)
            {
                throw new InvalidOperationException("The staged resource asset is missing a required material reference.");
            }
        }

        /// <summary>Ticket 5G (N12): removes the staged copy so the repository's resting state (and any build made another way) has no Resources/ presence for the harness.</summary>
        private static void UnstageResourceAsset()
        {
            if (AssetDatabase.LoadAssetAtPath<SolLandscapePerformanceAssets>(ResourceAssetStagedPath) != null)
            {
                AssetDatabase.DeleteAsset(ResourceAssetStagedPath);
                AssetDatabase.Refresh();
            }
        }

        private static Material CreateOrUpdateArrayHarnessMaterial(
            Material source, string assetPath, string materialName, bool stochasticOn)
        {
            Material harnessMaterial = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (harnessMaterial == null)
            {
                harnessMaterial = new Material(source) { name = materialName, enableInstancing = true };
                AssetDatabase.CreateAsset(harnessMaterial, assetPath);
            }
            else
            {
                harnessMaterial.shader = source.shader;
                harnessMaterial.CopyPropertiesFromMaterial(source);
                harnessMaterial.shaderKeywords = source.shaderKeywords;
                harnessMaterial.enableInstancing = true;
            }

            if (stochasticOn) harnessMaterial.EnableKeyword(StochasticKeyword); else harnessMaterial.DisableKeyword(StochasticKeyword);

            if (harnessMaterial.IsKeywordEnabled(StochasticKeyword) != stochasticOn)
            {
                throw new InvalidOperationException($"Failed to bake keyword state into {materialName}.");
            }

            EditorUtility.SetDirty(harnessMaterial);
            return harnessMaterial;
        }

        private static string GetArgument(string[] arguments, string name, string fallback)
        {
            for (int index = 0; index < arguments.Length - 1; ++index)
            {
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[index + 1];
                }
            }

            return fallback;
        }
    }

    public sealed class SolLandscapeShaderVariantCollector : IPreprocessShaders
    {
        private const string ArrayShaderName = "Sol/Terrain/Array Lit";
        private const string LegacyShaderName = "Sol/Terrain/Lit Wetness";
        private const string LegacyAddPassShaderName = "Hidden/Sol/Terrain/Lit Wetness Add Pass";
        private const string StochasticKeyword = "_SOL_LANDSCAPE_STOCHASTIC";
        private static readonly Dictionary<string, long> Counts = new Dictionary<string, long>();
        private static bool active;

        public int callbackOrder => 10000;
        public static long LastArrayVariantCount { get; private set; }
        public static long LastLegacyVariantCount { get; private set; }
        // Ticket 5F.2/5G: whether ANY post-strip array-shader variant actually carries the keyword
        // ON. _SOL_LANDSCAPE_STOCHASTIC has no exposed material [Toggle], so it is not safe to
        // assume a build kept its ON variant just because nothing errored -- this is measured
        // directly against the actual post-strip compiler data instead of assumed.
        public static long LastArrayStochasticOnVariantCount { get; private set; }

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (!active || shader == null || !IsTargetShader(shader.name))
            {
                return;
            }

            if (data.Count == 0)
            {
                string emptyKey = string.Join("|", shader.name, snippet.passName, snippet.passType, snippet.shaderType, "NoVariants");
                if (!Counts.ContainsKey(emptyKey))
                {
                    Counts.Add(emptyKey, 0L);
                }
                return;
            }

            foreach (IGrouping<ShaderCompilerPlatform, ShaderCompilerData> platformGroup in data.GroupBy(item => item.shaderCompilerPlatform))
            {
                string key = string.Join("|", shader.name, snippet.passName, snippet.passType, snippet.shaderType, platformGroup.Key);
                Counts.TryGetValue(key, out long current);
                Counts[key] = current + platformGroup.LongCount();
            }

            if (string.Equals(shader.name, ArrayShaderName, StringComparison.Ordinal))
            {
                ShaderKeyword stochasticKeyword = new ShaderKeyword(shader, StochasticKeyword);
                foreach (ShaderCompilerData item in data)
                {
                    if (item.shaderKeywordSet.IsEnabled(stochasticKeyword))
                    {
                        ArrayStochasticOnCount++;
                    }
                }
            }
        }

        private static long ArrayStochasticOnCount;

        public static void Begin()
        {
            Counts.Clear();
            LastArrayVariantCount = 0;
            LastLegacyVariantCount = 0;
            ArrayStochasticOnCount = 0;
            active = true;
        }

        public static string End(BuildReport report, string buildPath)
        {
            active = false;
            LastArrayVariantCount = SumShader(ArrayShaderName);
            LastLegacyVariantCount = SumShader(LegacyShaderName) + SumShader(LegacyAddPassShaderName);
            LastArrayStochasticOnVariantCount = ArrayStochasticOnCount;
            StringBuilder output = new StringBuilder();
            output.AppendLine("Sol Landscape Phase 5 ticket 5A post-strip shader variant evidence");
            output.AppendLine($"UTC={DateTime.UtcNow:O}");
            output.AppendLine($"Unity={Application.unityVersion}");
            output.AppendLine($"BuildTarget={report.summary.platform}");
            output.AppendLine($"BuildResult={report.summary.result}");
            output.AppendLine($"BuildErrors={report.summary.totalErrors}");
            output.AppendLine($"BuildWarnings={report.summary.totalWarnings}");
            output.AppendLine($"BuildPath={buildPath}");
            output.AppendLine("CountDefinition=Variants present in IPreprocessShaders at callback order 10000, after Unity built-in filtering and the URP stripper at callback order 0; summed across player shader snippets and compiler platforms");
            output.AppendLine($"ArrayShaderPostStripVariantPrograms={LastArrayVariantCount}");
            output.AppendLine($"ArrayShaderPostStripVariantsWithStochasticOn={LastArrayStochasticOnVariantCount}");
            output.AppendLine($"LegacyBaseAndAddPassPostStripVariantPrograms={LastLegacyVariantCount}");
            output.AppendLine($"LegacyBasePostStripVariantPrograms={SumShader(LegacyShaderName)}");
            output.AppendLine($"LegacyAddPassPostStripVariantPrograms={SumShader(LegacyAddPassShaderName)}");
            output.AppendLine("Breakdown=Shader|PassName|PassType|ShaderType|CompilerPlatform|Count");
            foreach (KeyValuePair<string, long> entry in Counts.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                output.AppendLine($"{entry.Key}|{entry.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            return output.ToString();
        }

        public static void Cancel()
        {
            active = false;
        }

        private static long SumShader(string shaderName)
        {
            string prefix = shaderName + "|";
            return Counts.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(entry => entry.Value);
        }

        private static bool IsTargetShader(string shaderName)
        {
            return string.Equals(shaderName, ArrayShaderName, StringComparison.Ordinal)
                || string.Equals(shaderName, LegacyShaderName, StringComparison.Ordinal)
                || string.Equals(shaderName, LegacyAddPassShaderName, StringComparison.Ordinal);
        }
    }
}
