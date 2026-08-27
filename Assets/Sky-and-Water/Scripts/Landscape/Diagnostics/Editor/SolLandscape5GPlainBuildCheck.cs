using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Sol.Landscape.Diagnostics.Editor
{
    /// <summary>
    /// Ticket 5G (N12) proof: builds the player through the PLAIN Unity build path -- no
    /// EnsureHarnessAssets, no StageResourceAsset, exactly what a normal Build Settings build (or
    /// any build made some other way than SolLandscapePerformanceBuild) would do -- and records the
    /// post-strip variant count the same way, to directly demonstrate a production build carries
    /// none of the harness's measurement-only variants.
    /// </summary>
    public static class SolLandscape5GPlainBuildCheck
    {
        private const string ScenePath = "Assets/Scenes/SolsWeather_Demo.unity";

        public static void BuildFromCommandLine()
        {
            int exitCode = 1;
            try
            {
                string[] arguments = System.Environment.GetCommandLineArgs();
                string buildPath = Path.GetFullPath(GetArgument(arguments, "-solPerfBuildPath", "../builds/Landscape5GPlain/SolLandscapePerf.exe"));
                string variantPath = Path.GetFullPath(GetArgument(arguments, "-solPerfVariantOutput", "../Landscape5GEvidence/5G_PlainPostStripVariants.txt"));
                Directory.CreateDirectory(Path.GetDirectoryName(buildPath) ?? ".");
                Directory.CreateDirectory(Path.GetDirectoryName(variantPath) ?? ".");

                SolLandscapeShaderVariantCollector.Begin();
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
                string variantReport = SolLandscapeShaderVariantCollector.End(report, buildPath);
                File.WriteAllText(variantPath, variantReport, new UTF8Encoding(false));

                bool success = report.summary.result == BuildResult.Succeeded
                    && report.summary.totalErrors == 0
                    && SolLandscapeShaderVariantCollector.LastArrayVariantCount > 0
                    && SolLandscapeShaderVariantCollector.LastArrayStochasticOnVariantCount == 0;
                Debug.Log(
                    $"[Sol Landscape 5G Plain] Build result={report.summary.result}; errors={report.summary.totalErrors}; " +
                    $"arrayPostStrip={SolLandscapeShaderVariantCollector.LastArrayVariantCount}; " +
                    $"arrayStochasticOn={SolLandscapeShaderVariantCollector.LastArrayStochasticOnVariantCount}; " +
                    $"variants={variantPath}");
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
                EditorApplication.Exit(exitCode);
            }
        }

        private static string GetArgument(string[] arguments, string name, string fallback)
        {
            for (int index = 0; index < arguments.Length - 1; ++index)
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    return arguments[index + 1];
            return fallback;
        }
    }
}
