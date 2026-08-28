using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Sol.Landscape;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;

namespace Sol.Landscape.Diagnostics.Editor
{
    /// <summary>
    /// Ticket 5G item 5: reports ACTUAL runtime GPU memory for the landscape's control-map textures
    /// and layer texture arrays, read from LIVE loaded objects via Profiler.GetRuntimeMemorySizeLong
    /// -- not computed from importer settings, which 5E showed can disagree with what the GPU
    /// actually holds.
    /// </summary>
    public static class SolLandscape5GMemory
    {
        private const string ScenePath = "Assets/Scenes/Sols_Water2_Demo.unity";

        public static void MeasureFromCommandLine()
        {
            int exitCode = 1;
            try
            {
                string outputDirectory = Path.GetFullPath(GetArgument(
                    System.Environment.GetCommandLineArgs(), "-sol5GMemoryOutput", "../Landscape5GEvidence"));
                Directory.CreateDirectory(outputDirectory);

                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                Terrain terrain = Terrain.activeTerrain
                    ?? UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault();
                SolLandscapeDriver driver = UnityEngine.Object
                    .FindObjectsByType<SolLandscapeDriver>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                    .FirstOrDefault();
                if (terrain == null || terrain.terrainData == null || driver == null || driver.config == null)
                    throw new InvalidOperationException("The demo terrain and driver/config are required.");

                Texture2D[] controls = terrain.terrainData.alphamapTextures;
                Texture2DArray cs = driver.config.CSArray;
                Texture2DArray noh = driver.config.NOHArray;
                if (controls == null || controls.Length == 0 || cs == null || noh == null)
                    throw new InvalidOperationException("Expected alphamap control textures and the CS/NOH arrays to be loaded.");

                var evidence = new StringBuilder();
                evidence.AppendLine("Sol Landscape Phase 5 ticket 5G memory measurement");
                evidence.AppendLine($"UTC={DateTime.UtcNow:O}; Unity={Application.unityVersion}");
                evidence.AppendLine("Method=Profiler.GetRuntimeMemorySizeLong on live loaded objects in an open scene -- NOT computed from importer settings (5E showed the two can disagree)");

                long controlTotal = 0;
                for (int i = 0; i < controls.Length; ++i)
                {
                    Texture2D control = controls[i];
                    long bytes = Profiler.GetRuntimeMemorySizeLong(control);
                    controlTotal += bytes;
                    evidence.AppendLine(FormatTexture($"AlphamapControl{i}", control.width, control.height, 1, control.format.ToString(), control.graphicsFormat.ToString(), control.mipmapCount, bytes) + $"; IsReadable:{control.isReadable}");
                }
                evidence.AppendLine($"AlphamapControlsTotalBytes={controlTotal}; MB={BytesToMB(controlTotal)}");

                long csBytes = Profiler.GetRuntimeMemorySizeLong(cs);
                evidence.AppendLine(FormatTexture("CSArray", cs.width, cs.height, cs.depth, cs.format.ToString(), cs.graphicsFormat.ToString(), cs.mipmapCount, csBytes) + $"; IsReadable:{cs.isReadable}");

                long nohBytes = Profiler.GetRuntimeMemorySizeLong(noh);
                evidence.AppendLine(FormatTexture("NOHArray", noh.width, noh.height, noh.depth, noh.format.ToString(), noh.graphicsFormat.ToString(), noh.mipmapCount, nohBytes) + $"; IsReadable:{noh.isReadable}");

                long arraysTotal = csBytes + nohBytes;
                evidence.AppendLine($"CSAndNOHArraysTotalBytes={arraysTotal}; MB={BytesToMB(arraysTotal)}");

                long grandTotal = controlTotal + arraysTotal;
                evidence.AppendLine($"LandscapeTextureGrandTotalBytes={grandTotal}; MB={BytesToMB(grandTotal)}");

                long systemMemoryMB = SystemInfo.graphicsMemorySize;
                double fractionOfGpuMemory = systemMemoryMB > 0 ? BytesToMB(grandTotal) / systemMemoryMB : double.NaN;
                evidence.AppendLine($"GraphicsDeviceName={SystemInfo.graphicsDeviceName}; ReportedGraphicsMemoryMB={systemMemoryMB}");
                evidence.AppendLine($"LandscapeTextureFractionOfReportedGpuMemory={fractionOfGpuMemory.ToString("F6", CultureInfo.InvariantCulture)} ({(fractionOfGpuMemory * 100.0).ToString("F3", CultureInfo.InvariantCulture)}%)");
                evidence.AppendLine("Verdict=See 5G_Decision_Summary.txt for the plain-language verdict on whether this constrains anything on this project.");
                evidence.AppendLine("SceneSaved=False");
                evidence.AppendLine("RESULT=PASS");

                string path = Path.Combine(outputDirectory, "5G_Memory_Evidence.txt");
                File.WriteAllText(path, evidence.ToString(), new UTF8Encoding(false));
                Debug.Log($"[Sol Landscape 5G] Memory evidence written to {path}");
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

        private static double BytesToMB(long bytes) => bytes / (1024.0 * 1024.0);

        private static string FormatTexture(string name, int width, int height, int depth, string format, string graphicsFormat, int mipCount, long bytes)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0}={1}x{2}x{3}; Format:{4}; GraphicsFormat:{5}; Mips:{6}; RuntimeBytes:{7}; RuntimeMB:{8:F3}",
                name, width, height, depth, format, graphicsFormat, mipCount, bytes, BytesToMB(bytes));
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
