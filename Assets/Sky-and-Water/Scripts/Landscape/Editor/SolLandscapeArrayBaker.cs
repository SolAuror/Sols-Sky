using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Sol.Landscape.Editor
{
    internal readonly struct SolLandscapeStaleness
    {
        public readonly bool IsStale;
        public readonly string Message;

        public SolLandscapeStaleness(bool isStale, string message)
        {
            IsStale = isStale;
            Message = message;
        }
    }

    internal static class SolLandscapeArrayBaker
    {
        private const string TargetTerrainPath = "Assets/Scenes/SolsWeather_Demo/DemoTerrain.asset";
        private const string OutputFolder = "Assets/Sky-and-Water/Landscape";
        private const string ConfigPath = OutputFolder + "/SolLandscapeConfig.asset";
        private const string CSArrayPath = OutputFolder + "/SolLandscapeCSArray.asset";
        private const string NOHArrayPath = OutputFolder + "/SolLandscapeNOHArray.asset";
        private const string EvidenceFileName = "Phase1LandscapeBakeEvidence.txt";
        private const string HashBaselineFileName = "Phase1LandscapeBakeHashBaseline.txt";
        private const TextureFormat ArrayFormat = TextureFormat.BC7;
        private const int SampleGridSize = 8;

        private static readonly Regex BaselineHashPattern = new Regex(
            @"^(CS|NOH)Slice\[(\d+)\]Mip\[(\d+)\]=([0-9A-Fa-f]{16})$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Color32 FlatNormal = new Color32(128, 128, 255, 255);
        private static readonly Color32 MidGreyMask = new Color32(128, 128, 128, 128);

        [MenuItem("Tools/Sol Landscape/Phase 1/Bake Demo Terrain CSNOH Arrays")]
        private static void BakeFromMenu()
        {
            SolLandscapeConfig config = BakeTarget();
            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);
        }

        [MenuItem("Tools/Sol Landscape/Phase 1/Select Landscape Config")]
        private static void SelectConfig()
        {
            SolLandscapeConfig config = AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);
            if (config == null)
            {
                Debug.LogWarning($"[Sol Landscape Phase 1] No config exists at {ConfigPath}; bake first.");
                return;
            }

            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);
        }

        public static void BakeAndValidateFromCommandLine()
        {
            try
            {
                BakeTarget(requireTrustedHashBaseline: true);
                Debug.Log("[Sol Landscape Phase 1] COMMAND_LINE_RESULT=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Debug.LogError("[Sol Landscape Phase 1] COMMAND_LINE_RESULT=FAIL");
                throw;
            }
        }

        internal static SolLandscapeConfig BakeTarget(
            SolLandscapeConfig requestedConfig = null,
            bool requireTrustedHashBaseline = false)
        {
            TerrainData terrainData = requestedConfig != null && requestedConfig.TerrainData != null
                ? requestedConfig.TerrainData
                : AssetDatabase.LoadAssetAtPath<TerrainData>(TargetTerrainPath);

            if (terrainData == null)
            {
                throw new InvalidOperationException($"Missing target TerrainData at {TargetTerrainPath}.");
            }

            TerrainLayer[] layers = terrainData.terrainLayers;
            if (layers == null || layers.Length == 0)
            {
                throw new InvalidOperationException("The target TerrainData has no TerrainLayers.");
            }

            var report = new StringBuilder(16384);
            report.AppendLine("Sol Landscape Phase 1 bake evidence");
            report.AppendLine($"UTC={DateTime.UtcNow:O}");
            report.AppendLine($"Unity={Application.unityVersion}");
            report.AppendLine($"TerrainData={AssetDatabase.GetAssetPath(terrainData)}");
            report.AppendLine($"LayerCount={layers.Length}");

            HashBaseline hashBaseline = requireTrustedHashBaseline ? LoadHashBaseline(report) : null;
            report.AppendLine($"BaselineHashComparisonRequired={requireTrustedHashBaseline}");
            SourceLayout layout = ValidateSourceLayout(layers, report);
            EnsureOutputFolder();

            Texture2DArray csArray = CreateArray(layout.Width, layout.Height, layers.Length, linear: false,
                "SolLandscapeCSArray");
            Texture2DArray nohArray = CreateArray(layout.Width, layout.Height, layers.Length, linear: true,
                "SolLandscapeNOHArray");

            try
            {
                BakeSlices(layers, layout, csArray, nohArray, hashBaseline, report);
                hashBaseline?.AssertComplete(report);
                AssertArrayColourSpaces(csArray, nohArray, report);

                csArray.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                nohArray.Apply(updateMipmaps: false, makeNoLongerReadable: true);

                Texture2DArray persistedCS = PersistArray(csArray, CSArrayPath);
                csArray = null;
                Texture2DArray persistedNOH = PersistArray(nohArray, NOHArrayPath);
                nohArray = null;

                AssertArrayColourSpaces(persistedCS, persistedNOH, report);
                NumericValidation numeric = ValidatePersistedPixels(layers, layout, persistedCS, persistedNOH, report);

                long csStorageBytes = GetStorageMemorySize(persistedCS);
                long nohStorageBytes = GetStorageMemorySize(persistedNOH);
                long expectedPerArray = CalculateBC7ChainBytes(layout.Width, layout.Height, layers.Length);
                long totalStorageBytes = csStorageBytes + nohStorageBytes;
                long expectedTotalBytes = expectedPerArray * 2L;

                report.AppendLine($"CSStorageBytes={csStorageBytes}");
                report.AppendLine($"NOHStorageBytes={nohStorageBytes}");
                report.AppendLine($"TotalStorageBytes={totalStorageBytes}");
                report.AppendLine($"ExpectedBC7BytesPerArray={expectedPerArray}");
                report.AppendLine($"ExpectedBC7BytesTotal={expectedTotalBytes}");
                report.AppendLine($"StorageDeltaBytes={totalStorageBytes - expectedTotalBytes}");
                report.AppendLine($"CSAssetFileBytes={GetAssetFileLength(CSArrayPath)}");
                report.AppendLine($"NOHAssetFileBytes={GetAssetFileLength(NOHArrayPath)}");

                var fingerprints = BuildFingerprints(layers);
                SolLandscapeConfig config = requestedConfig != null
                    ? requestedConfig
                    : AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);

                if (config == null)
                {
                    config = ScriptableObject.CreateInstance<SolLandscapeConfig>();
                    config.name = "SolLandscapeConfig";
                    AssetDatabase.CreateAsset(config, ConfigPath);
                }

                string summary = BuildSummary(layers, layout, numeric, totalStorageBytes, expectedTotalBytes);
                config.RecordBake(
                    terrainData,
                    layers,
                    persistedCS,
                    persistedNOH,
                    AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(terrainData)),
                    fingerprints,
                    DateTime.UtcNow.ToString("O"),
                    csStorageBytes,
                    nohStorageBytes,
                    summary);
                EditorUtility.SetDirty(config);
                AssetDatabase.SaveAssets();

                SolLandscapeStaleness fresh = GetStaleness(config);
                if (fresh.IsStale)
                {
                    throw new InvalidOperationException($"A just-baked config is stale: {fresh.Message}");
                }

                report.AppendLine("FreshConfigStale=False");
                RunReorderStalenessProbe(config, layers, report);
                RunMissingMapDefaultProbe(report);
                RunFailureGuardProbes(layout, report);
                report.AppendLine("RESULT=PASS");

                WriteEvidence(report.ToString());
                Debug.Log(report.ToString());
                return config;
            }
            catch
            {
                report.AppendLine("RESULT=FAIL");
                WriteEvidence(report.ToString());
                throw;
            }
            finally
            {
                if (csArray != null)
                {
                    UnityEngine.Object.DestroyImmediate(csArray);
                }

                if (nohArray != null)
                {
                    UnityEngine.Object.DestroyImmediate(nohArray);
                }
            }
        }

        internal static SolLandscapeStaleness GetStaleness(SolLandscapeConfig config)
        {
            return GetStaleness(config, config != null && config.TerrainData != null
                ? config.TerrainData.terrainLayers
                : Array.Empty<TerrainLayer>());
        }

        private static SolLandscapeStaleness GetStaleness(
            SolLandscapeConfig config,
            IReadOnlyList<TerrainLayer> currentLayers)
        {
            if (config == null)
            {
                return new SolLandscapeStaleness(true, "Config reference is null.");
            }

            if (config.TerrainData == null)
            {
                return new SolLandscapeStaleness(true, "TerrainData reference is missing.");
            }

            string currentTerrainGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(config.TerrainData));
            if (!string.Equals(config.TerrainDataGuid, currentTerrainGuid, StringComparison.Ordinal))
            {
                return new SolLandscapeStaleness(true, "TerrainData GUID differs from the baked source.");
            }

            if (currentLayers.Count != config.BakeFingerprints.Count)
            {
                return new SolLandscapeStaleness(true,
                    $"Layer count changed from {config.BakeFingerprints.Count} to {currentLayers.Count}.");
            }

            if (config.Layers.Count != currentLayers.Count)
            {
                return new SolLandscapeStaleness(true,
                    $"Config layer-entry count {config.Layers.Count} differs from TerrainData count {currentLayers.Count}.");
            }

            for (int i = 0; i < currentLayers.Count; i++)
            {
                TerrainLayer layer = currentLayers[i];
                if (layer == null)
                {
                    return new SolLandscapeStaleness(true, $"TerrainData layer {i} is null.");
                }

                if (config.Layers[i].terrainLayer != layer)
                {
                    return new SolLandscapeStaleness(true, $"Config layer order differs at slice {i}.");
                }

                SolLandscapeBakeFingerprint baked = config.BakeFingerprints[i];
                SolLandscapeBakeFingerprint current = BuildFingerprint(layer);
                if (!string.Equals(baked.TerrainLayerGuid, current.TerrainLayerGuid, StringComparison.Ordinal))
                {
                    return new SolLandscapeStaleness(true, $"TerrainLayer order/GUID differs at slice {i}.");
                }

                if (!string.Equals(baked.DiffuseTextureGuid, current.DiffuseTextureGuid, StringComparison.Ordinal))
                {
                    return new SolLandscapeStaleness(true, $"Diffuse texture GUID differs at slice {i} ({layer.name}).");
                }

                if (!string.Equals(baked.NormalTextureGuid, current.NormalTextureGuid, StringComparison.Ordinal))
                {
                    return new SolLandscapeStaleness(true, $"Normal texture GUID differs at slice {i} ({layer.name}).");
                }

                if (!string.Equals(baked.MaskTextureGuid, current.MaskTextureGuid, StringComparison.Ordinal))
                {
                    return new SolLandscapeStaleness(true, $"Mask texture GUID differs at slice {i} ({layer.name}).");
                }
            }

            if (config.CSArray == null || config.NOHArray == null)
            {
                return new SolLandscapeStaleness(true, "One or both baked arrays are missing.");
            }

            bool csIsSRGB = GraphicsFormatUtility.IsSRGBFormat(config.CSArray.graphicsFormat);
            bool nohIsSRGB = GraphicsFormatUtility.IsSRGBFormat(config.NOHArray.graphicsFormat);
            if (!csIsSRGB || nohIsSRGB)
            {
                return new SolLandscapeStaleness(true,
                    $"Array colour-space flags are invalid (CS sRGB={csIsSRGB}, NOH sRGB={nohIsSRGB}).");
            }

            return new SolLandscapeStaleness(false, "Baked arrays match layer count, order, and texture GUIDs.");
        }

        private static SourceLayout ValidateSourceLayout(IReadOnlyList<TerrainLayer> layers, StringBuilder report)
        {
            int width = 0;
            int height = 0;
            GraphicsFormat? diffuseFormat = null;
            GraphicsFormat? normalFormat = null;
            GraphicsFormat? maskFormat = null;

            for (int i = 0; i < layers.Count; i++)
            {
                TerrainLayer layer = layers[i];
                if (layer == null)
                {
                    throw new InvalidOperationException($"TerrainData layer {i} is null.");
                }

                if (layer.diffuseTexture == null)
                {
                    throw new InvalidOperationException($"Layer {i} ({layer.name}) has no diffuse texture.");
                }

                if (width == 0)
                {
                    width = layer.diffuseTexture.width;
                    height = layer.diffuseTexture.height;
                }

                ValidateDimensions(layer.diffuseTexture, width, height, i, "diffuse");
                ValidateRoleFormat(layer.diffuseTexture, ref diffuseFormat, i, "diffuse");
                AssertDiffuseImporter(layer.diffuseTexture, i, report);

                if (layer.normalMapTexture != null)
                {
                    ValidateDimensions(layer.normalMapTexture, width, height, i, "normal");
                    ValidateRoleFormat(layer.normalMapTexture, ref normalFormat, i, "normal");
                    AssertNormalImporter(layer.normalMapTexture, i, report);
                }
                else
                {
                    WarnMissingMap(i, layer, "normal", "flat normal (128,128,255,255)");
                }

                if (layer.maskMapTexture != null)
                {
                    ValidateDimensions(layer.maskMapTexture, width, height, i, "mask");
                    ValidateRoleFormat(layer.maskMapTexture, ref maskFormat, i, "mask");
                    AssertMaskImporter(layer.maskMapTexture, i, report);
                }
                else
                {
                    WarnMissingMap(i, layer, "mask", "mid-grey mask (128,128,128,128)");
                }

                report.AppendLine(
                    $"Slice[{i}]={layer.name}; Layer={AssetDatabase.GetAssetPath(layer)}; " +
                    $"Diffuse={DescribeTexture(layer.diffuseTexture)}; Normal={DescribeTexture(layer.normalMapTexture)}; " +
                    $"Mask={DescribeTexture(layer.maskMapTexture)}");
            }

            int mipCount = CalculateMipCount(width, height);
            report.AppendLine($"Dimensions={width}x{height}");
            report.AppendLine($"MipCount={mipCount}");
            report.AppendLine($"DiffuseGraphicsFormat={diffuseFormat}");
            report.AppendLine($"NormalGraphicsFormat={normalFormat?.ToString() ?? "defaults-only"}");
            report.AppendLine($"MaskGraphicsFormat={maskFormat?.ToString() ?? "defaults-only"}");
            return new SourceLayout(width, height, mipCount);
        }

        private static void BakeSlices(
            IReadOnlyList<TerrainLayer> layers,
            SourceLayout layout,
            Texture2DArray csArray,
            Texture2DArray nohArray,
            HashBaseline hashBaseline,
            StringBuilder report)
        {
            var correlations = new double[layers.Count];
            for (int slice = 0; slice < layers.Count; slice++)
            {
                PackedBases packed = BuildPackedBases(layers[slice], layout, warnOnDefaults: true);
                packed.Movement.AssertExpected(slice);
                report.AppendLine(
                    $"LogicalChannelChanges[{slice}].CS=" +
                    $"R:{packed.Movement.CS[0]},G:{packed.Movement.CS[1]}," +
                    $"B:{packed.Movement.CS[2]},A:{packed.Movement.CS[3]}; " +
                    $"NOH=R:{packed.Movement.NOH[0]},G:{packed.Movement.NOH[1]}," +
                    $"B:{packed.Movement.NOH[2]},A:{packed.Movement.NOH[3]}");
                report.AppendLine(
                    $"SmoothnessPackedBase[{slice}]=Min={packed.SmoothnessMin:F9}; " +
                    $"Mean={packed.SmoothnessMean:F9}; Max={packed.SmoothnessMax:F9}; " +
                    $"NonDegenerate={packed.SmoothnessMax > packed.SmoothnessMin}");
                correlations[slice] = packed.NormalHeightCorrelation;

                List<Color32[]> csMips = BuildMipChain(packed.CS, layout.Width, layout.Height, srgbRgb: true);
                List<Color32[]> nohMips = BuildMipChain(packed.NOH, layout.Width, layout.Height, srgbRgb: false);

                WriteCompressedSlice(csArray, slice, csMips, layout, "CS", hashBaseline, report);
                WriteCompressedSlice(nohArray, slice, nohMips, layout, "NOH", hashBaseline, report);
            }

            AppendNormalConventionDiagnostics(layers, correlations, report);
        }

        private static PackedBases BuildPackedBases(TerrainLayer layer, SourceLayout layout, bool warnOnDefaults)
        {
            Color32[] diffuse = ReadSourcePixels(layer.diffuseTexture, layout);
            Color32[] normal;
            Color32[] mask;

            if (layer.normalMapTexture != null)
            {
                normal = ReadSourcePixels(layer.normalMapTexture, layout);
            }
            else
            {
                normal = CreateSolidPixels(layout.Width * layout.Height, FlatNormal);
                if (warnOnDefaults)
                {
                    WarnMissingMap(-1, layer, "normal", "flat normal (128,128,255,255)");
                }
            }

            if (layer.maskMapTexture != null)
            {
                mask = ReadSourcePixels(layer.maskMapTexture, layout);
            }
            else
            {
                mask = CreateSolidPixels(layout.Width * layout.Height, MidGreyMask);
                if (warnOnDefaults)
                {
                    WarnMissingMap(-1, layer, "mask", "mid-grey mask (128,128,128,128)");
                }
            }

            var cs = new Color32[diffuse.Length];
            var noh = new Color32[diffuse.Length];
            var movement = new LogicalChannelMovement();
            Vector4 diffuseMin = layer.diffuseRemapMin;
            Vector4 diffuseMax = layer.diffuseRemapMax;
            Vector4 maskMin = layer.maskMapRemapMin;
            Vector4 maskMax = layer.maskMapRemapMax;
            float smoothnessMin = layer.maskMapRemapMin.w;
            float smoothnessMax = layer.maskMapRemapMax.w;
            bool flipNormalGreen = layer.normalMapTexture != null && GetNormalFlipGreen(layer.normalMapTexture);
            byte smoothnessMinByte = byte.MaxValue;
            byte smoothnessMaxByte = byte.MinValue;
            long smoothnessSum = 0;

            for (int i = 0; i < diffuse.Length; i++)
            {
                // These authored masks deliberately use R=roughness, G=AO, B=height, A=unused,
                // rather than Unity's documented R=metallic/G=AO/B=height/A=smoothness layout.
                // The existing inverted W remap converts roughness to smoothness: 0.3 * (1 - R).
                byte legacySmoothness = FloatToByte(Mathf.Lerp(smoothnessMin, smoothnessMax, mask[i].a / 255f));
                byte smoothness = FloatToByte(Mathf.Lerp(smoothnessMin, smoothnessMax, mask[i].r / 255f));
                byte albedoR = FloatToByte(Mathf.Lerp(diffuseMin.x, diffuseMax.x, diffuse[i].r / 255f));
                byte albedoG = FloatToByte(Mathf.Lerp(diffuseMin.y, diffuseMax.y, diffuse[i].g / 255f));
                byte albedoB = FloatToByte(Mathf.Lerp(diffuseMin.z, diffuseMax.z, diffuse[i].b / 255f));
                byte ambientOcclusion = FloatToByte(Mathf.Lerp(maskMin.y, maskMax.y, mask[i].g / 255f));
                byte height = FloatToByte(Mathf.Lerp(maskMin.z, maskMax.z, mask[i].b / 255f));
                byte normalGreen = flipNormalGreen ? (byte)(255 - normal[i].g) : normal[i].g;

                Color32 legacyCS = new Color32(albedoR, albedoG, albedoB, legacySmoothness);
                Color32 legacyNOH = new Color32(normal[i].r, normal[i].g, ambientOcclusion, height);
                Color32 currentCS = new Color32(albedoR, albedoG, albedoB, smoothness);
                Color32 currentNOH = new Color32(normal[i].r, normalGreen, ambientOcclusion, height);
                movement.Add(legacyCS, currentCS, legacyNOH, currentNOH);
                cs[i] = currentCS;
                noh[i] = currentNOH;

                smoothnessMinByte = Math.Min(smoothnessMinByte, smoothness);
                smoothnessMaxByte = Math.Max(smoothnessMaxByte, smoothness);
                smoothnessSum += smoothness;
            }

            double correlation = CalculateNormalHeightCorrelation(noh, mask, layout.Width, layout.Height);
            return new PackedBases(
                cs,
                noh,
                movement,
                smoothnessMinByte / 255d,
                smoothnessSum / (255d * diffuse.Length),
                smoothnessMaxByte / 255d,
                correlation);
        }

        private static bool GetNormalFlipGreen(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
            {
                throw new InvalidOperationException($"Normal source does not have a TextureImporter: {path}.");
            }

            return importer.flipGreenChannel;
        }

        private static double CalculateNormalHeightCorrelation(
            IReadOnlyList<Color32> packedNOH,
            IReadOnlyList<Color32> sourceMask,
            int width,
            int height)
        {
            long count = 0;
            double sumNormal = 0d;
            double sumGradient = 0d;
            double sumNormalSquared = 0d;
            double sumGradientSquared = 0d;
            double sumProduct = 0d;

            for (int y = 1; y < height - 1; y++)
            {
                int row = y * width;
                int previousRow = (y - 1) * width;
                int nextRow = (y + 1) * width;
                for (int x = 0; x < width; x++)
                {
                    double normalY = packedNOH[row + x].g / 127.5d - 1d;
                    double heightGradient = (sourceMask[nextRow + x].b - sourceMask[previousRow + x].b) / 510d;
                    sumNormal += normalY;
                    sumGradient += heightGradient;
                    sumNormalSquared += normalY * normalY;
                    sumGradientSquared += heightGradient * heightGradient;
                    sumProduct += normalY * heightGradient;
                    count++;
                }
            }

            double covariance = sumProduct - sumNormal * sumGradient / count;
            double normalVariance = sumNormalSquared - sumNormal * sumNormal / count;
            double gradientVariance = sumGradientSquared - sumGradient * sumGradient / count;
            double denominator = Math.Sqrt(Math.Max(0d, normalVariance * gradientVariance));
            return denominator > 0d ? covariance / denominator : 0d;
        }

        private static void AppendNormalConventionDiagnostics(
            IReadOnlyList<TerrainLayer> layers,
            IReadOnlyList<double> correlations,
            StringBuilder report)
        {
            int positive = correlations.Count(value => value > 0d);
            int negative = correlations.Count(value => value < 0d);
            int majoritySign = positive == negative ? 0 : positive > negative ? 1 : -1;
            int warningCount = 0;

            for (int slice = 0; slice < layers.Count; slice++)
            {
                int sign = Math.Sign(correlations[slice]);
                bool disagrees = majoritySign != 0 && sign != 0 && sign != majoritySign;
                report.AppendLine(
                    $"NormalHeightCorrelation[{slice}]={correlations[slice]:F9}; Layer={layers[slice].name}; " +
                    $"Sign={sign}; MajoritySign={majoritySign}; Disagrees={disagrees}");
                if (!disagrees)
                {
                    continue;
                }

                warningCount++;
                Debug.LogWarning(
                    $"[Sol Landscape Phase 1] Slice {slice} ({layers[slice].name}) normal-green/height-gradient " +
                    $"correlation {correlations[slice]:F6} disagrees with the majority sign. " +
                    "This diagnostic never flips or rejects data; review the source importer convention.");
            }

            report.AppendLine(
                $"NormalConventionDiagnosticCount={correlations.Count}; Positive={positive}; Negative={negative}; " +
                $"MajoritySign={majoritySign}; WarningCount={warningCount}; Behaviour=ReportAndWarnOnly");
        }

        private static Color32[] ReadSourcePixels(Texture2D source, SourceLayout layout)
        {
            string assetPath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(assetPath))
            {
                throw new InvalidOperationException($"Texture {source.name} does not have an asset path.");
            }

            string absolutePath = Path.GetFullPath(assetPath);
            if (!File.Exists(absolutePath))
            {
                throw new InvalidOperationException($"Texture source file does not exist: {assetPath}.");
            }

            var readable = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false, linear: true)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            try
            {
                if (!ImageConversion.LoadImage(readable, File.ReadAllBytes(absolutePath), markNonReadable: false))
                {
                    throw new InvalidOperationException($"Unity could not decode source texture {assetPath}.");
                }

                if (readable.width != layout.Width || readable.height != layout.Height)
                {
                    throw new InvalidOperationException(
                        $"Decoded texture {assetPath} is {readable.width}x{readable.height}; " +
                        $"expected {layout.Width}x{layout.Height}.");
                }

                return readable.GetPixels32(0);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        private static List<Color32[]> BuildMipChain(Color32[] basePixels, int width, int height, bool srgbRgb)
        {
            var result = new List<Color32[]>(CalculateMipCount(width, height)) { basePixels };
            int sourceWidth = width;
            int sourceHeight = height;
            Color32[] source = basePixels;

            while (sourceWidth > 1 || sourceHeight > 1)
            {
                int targetWidth = Mathf.Max(1, sourceWidth >> 1);
                int targetHeight = Mathf.Max(1, sourceHeight >> 1);
                var target = new Color32[targetWidth * targetHeight];

                for (int y = 0; y < targetHeight; y++)
                {
                    int y0 = Mathf.Min(sourceHeight - 1, y * 2);
                    int y1 = Mathf.Min(sourceHeight - 1, y0 + 1);
                    for (int x = 0; x < targetWidth; x++)
                    {
                        int x0 = Mathf.Min(sourceWidth - 1, x * 2);
                        int x1 = Mathf.Min(sourceWidth - 1, x0 + 1);
                        Color32 a = source[y0 * sourceWidth + x0];
                        Color32 b = source[y0 * sourceWidth + x1];
                        Color32 c = source[y1 * sourceWidth + x0];
                        Color32 d = source[y1 * sourceWidth + x1];
                        target[y * targetWidth + x] = AverageFour(a, b, c, d, srgbRgb);
                    }
                }

                result.Add(target);
                source = target;
                sourceWidth = targetWidth;
                sourceHeight = targetHeight;
            }

            return result;
        }

        private static Color32 AverageFour(Color32 a, Color32 b, Color32 c, Color32 d, bool srgbRgb)
        {
            if (!srgbRgb)
            {
                return new Color32(
                    AverageByte(a.r, b.r, c.r, d.r),
                    AverageByte(a.g, b.g, c.g, d.g),
                    AverageByte(a.b, b.b, c.b, d.b),
                    AverageByte(a.a, b.a, c.a, d.a));
            }

            return new Color32(
                AverageSRGBByte(a.r, b.r, c.r, d.r),
                AverageSRGBByte(a.g, b.g, c.g, d.g),
                AverageSRGBByte(a.b, b.b, c.b, d.b),
                AverageByte(a.a, b.a, c.a, d.a));
        }

        private static byte AverageByte(byte a, byte b, byte c, byte d)
        {
            return (byte)((a + b + c + d + 2) / 4);
        }

        private static byte AverageSRGBByte(byte a, byte b, byte c, byte d)
        {
            float linear = (Mathf.GammaToLinearSpace(a / 255f)
                + Mathf.GammaToLinearSpace(b / 255f)
                + Mathf.GammaToLinearSpace(c / 255f)
                + Mathf.GammaToLinearSpace(d / 255f)) * 0.25f;
            return FloatToByte(Mathf.LinearToGammaSpace(linear));
        }

        private static void WriteCompressedSlice(
            Texture2DArray destination,
            int slice,
            IReadOnlyList<Color32[]> mips,
            SourceLayout layout,
            string label,
            HashBaseline hashBaseline,
            StringBuilder report)
        {
            bool linear = !GraphicsFormatUtility.IsSRGBFormat(destination.graphicsFormat);
            var texture = new Texture2D(layout.Width, layout.Height, TextureFormat.RGBA32, mipChain: true, linear)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = $"{label}_Slice_{slice}_CompressionSource",
            };

            try
            {
                if (texture.mipmapCount != mips.Count)
                {
                    throw new InvalidOperationException(
                        $"Compression source has {texture.mipmapCount} mips; explicit chain has {mips.Count}.");
                }

                for (int mip = 0; mip < mips.Count; mip++)
                {
                    texture.SetPixels32(mips[mip], mip);
                }

                texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                EditorUtility.CompressTexture(texture, ArrayFormat, TextureCompressionQuality.Best);

                for (int mip = 0; mip < mips.Count; mip++)
                {
                    NativeArray<byte> payload = texture.GetPixelData<byte>(mip);
                    int mipWidth = Mathf.Max(1, layout.Width >> mip);
                    int mipHeight = Mathf.Max(1, layout.Height >> mip);
                    int expectedBytes = CalculateBC7MipBytes(mipWidth, mipHeight);
                    if (payload.Length != expectedBytes)
                    {
                        throw new InvalidOperationException(
                            $"{label} slice {slice} mip {mip} payload is {payload.Length} bytes; expected {expectedBytes}.");
                    }

                    int nonZeroBytes = 0;
                    ulong hash = 14695981039346656037UL;
                    for (int i = 0; i < payload.Length; i++)
                    {
                        byte value = payload[i];
                        if (value != 0)
                        {
                            nonZeroBytes++;
                        }

                        hash ^= value;
                        hash *= 1099511628211UL;
                    }

                    if (nonZeroBytes == 0)
                    {
                        throw new InvalidOperationException($"{label} slice {slice} mip {mip} is an all-zero BC7 payload.");
                    }

                    destination.SetPixelData(payload, mip, slice);
                    if (mip == 0 || mip == 1 || mip == mips.Count - 1)
                    {
                        bool expectedChanged = label == "CS" || label == "NOH" && slice == 0;
                        HashComparison comparison = hashBaseline != null
                            ? hashBaseline.AssertExpectedMovement(label, slice, mip, hash, expectedChanged)
                            : default;
                        string baselineResult = hashBaseline != null
                            ? $"; BaselineChanged={comparison.Changed}; ExpectedChanged={expectedChanged}; " +
                              $"ExpectedMovementMatch={comparison.Changed == expectedChanged}"
                            : string.Empty;
                        report.AppendLine(
                            $"{label}Slice[{slice}]Mip[{mip}]={mipWidth}x{mipHeight}; Bytes={payload.Length}; " +
                            $"NonZeroBytes={nonZeroBytes}; FNV1a64={hash:X16}{baselineResult}");
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static NumericValidation ValidatePersistedPixels(
            IReadOnlyList<TerrainLayer> layers,
            SourceLayout layout,
            Texture2DArray csArray,
            Texture2DArray nohArray,
            StringBuilder report)
        {
            var csErrors = new ErrorDistributionAccumulator();
            var nohErrors = new ErrorDistributionAccumulator();
            double csCorrectColourError = 0d;
            double csWrongColourError = 0d;
            double nohCorrectColourError = 0d;
            double nohWrongColourError = 0d;
            long colourComparedChannels = 0;

            for (int slice = 0; slice < layers.Count; slice++)
            {
                PackedBases packed = BuildPackedBases(layers[slice], layout, warnOnDefaults: false);
                Color32[] expectedCSMip1 = BuildNextMip(packed.CS, layout.Width, layout.Height, srgbRgb: true);
                Color32[] expectedNOHMip1 = BuildNextMip(packed.NOH, layout.Width, layout.Height, srgbRgb: false);
                Color32[] actualCSMip1 = ReadBackArrayMip(csArray, slice, 1);
                Color32[] actualNOHMip1 = ReadBackArrayMip(nohArray, slice, 1);
                int mipWidth = Mathf.Max(1, layout.Width >> 1);
                int mipHeight = Mathf.Max(1, layout.Height >> 1);

                AppendSmoothnessStatistics(slice, actualCSMip1, report);

                AccumulateErrorDistribution(
                    expectedCSMip1, actualCSMip1, mipWidth, mipHeight, srgbRgb: true, slice, csErrors);
                AccumulateErrorDistribution(
                    expectedNOHMip1, actualNOHMip1, mipWidth, mipHeight, srgbRgb: false, slice, nohErrors);

                if (slice == 0)
                {
                    Color32[] actualCSMip0 = ReadBackArrayMip(csArray, 0, 0);
                    Color32[] actualNOHMip0 = ReadBackArrayMip(nohArray, 0, 0);
                    CompareColourSpaceHypotheses(packed.CS, actualCSMip0, layout.Width, layout.Height, expectedSRGB: true,
                        ref csCorrectColourError, ref csWrongColourError, ref colourComparedChannels);
                    long ignoredColourCount = 0;
                    CompareColourSpaceHypotheses(packed.NOH, actualNOHMip0, layout.Width, layout.Height, expectedSRGB: false,
                        ref nohCorrectColourError, ref nohWrongColourError, ref ignoredColourCount);
                }
            }

            ErrorDistribution csDistribution = csErrors.Build();
            ErrorDistribution nohDistribution = nohErrors.Build();
            double csCorrectMae = csCorrectColourError / colourComparedChannels;
            double csWrongMae = csWrongColourError / colourComparedChannels;
            double nohCorrectMae = nohCorrectColourError / colourComparedChannels;
            double nohWrongMae = nohWrongColourError / colourComparedChannels;

            AppendDistribution("CS", csDistribution, report);
            AppendDistribution("NOH", nohDistribution, report);
            report.AppendLine($"CSColourSpaceCorrectHypothesisMAE={csCorrectMae:F6}; WrongLinearHypothesisMAE={csWrongMae:F6}");
            report.AppendLine($"NOHColourSpaceCorrectHypothesisMAE={nohCorrectMae:F6}; WrongSRGBHypothesisMAE={nohWrongMae:F6}");

            // Aggregate error remains the pass/fail signal. Isolated maxima are reported with their
            // distribution and location for Phase 2 diagnosis, but deliberately are not gated.
            if (csDistribution.Mean > 8d || nohDistribution.Mean > 8d)
            {
                throw new InvalidOperationException(
                    $"Numerical mip-1 aggregate MAE exceeded BC7 tolerance: " +
                    $"CS {csDistribution.Mean:F3}, NOH {nohDistribution.Mean:F3}.");
            }

            if (csCorrectMae + 2d >= csWrongMae)
            {
                throw new InvalidOperationException(
                    $"CS numerical colour-space probe did not distinguish sRGB from linear: {csCorrectMae:F3} vs {csWrongMae:F3}.");
            }

            if (nohCorrectMae + 2d >= nohWrongMae)
            {
                throw new InvalidOperationException(
                    $"NOH numerical colour-space probe did not distinguish linear from sRGB: {nohCorrectMae:F3} vs {nohWrongMae:F3}.");
            }

            return new NumericValidation(csDistribution, nohDistribution,
                csCorrectMae, csWrongMae, nohCorrectMae, nohWrongMae);
        }

        private static void AppendSmoothnessStatistics(int slice, IReadOnlyList<Color32> pixels, StringBuilder report)
        {
            byte minimum = byte.MaxValue;
            byte maximum = byte.MinValue;
            long sum = 0;
            for (int i = 0; i < pixels.Count; i++)
            {
                byte value = pixels[i].a;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
                sum += value;
            }

            report.AppendLine(
                $"SmoothnessPersistedMip1[{slice}]=Min={minimum / 255d:F9}; " +
                $"Mean={sum / (255d * pixels.Count):F9}; Max={maximum / 255d:F9}; " +
                $"NonDegenerate={maximum > minimum}; Samples={pixels.Count}");
        }

        private static Color32[] BuildNextMip(Color32[] source, int width, int height, bool srgbRgb)
        {
            int targetWidth = Mathf.Max(1, width >> 1);
            int targetHeight = Mathf.Max(1, height >> 1);
            var target = new Color32[targetWidth * targetHeight];
            for (int y = 0; y < targetHeight; y++)
            {
                int y0 = Mathf.Min(height - 1, y * 2);
                int y1 = Mathf.Min(height - 1, y0 + 1);
                for (int x = 0; x < targetWidth; x++)
                {
                    int x0 = Mathf.Min(width - 1, x * 2);
                    int x1 = Mathf.Min(width - 1, x0 + 1);
                    target[y * targetWidth + x] = AverageFour(
                        source[y0 * width + x0],
                        source[y0 * width + x1],
                        source[y1 * width + x0],
                        source[y1 * width + x1],
                        srgbRgb);
                }
            }

            return target;
        }

        private static void AccumulateErrorDistribution(
            Color32[] expected,
            Color32[] actual,
            int width,
            int height,
            bool srgbRgb,
            int slice,
            ErrorDistributionAccumulator accumulator)
        {
            if (expected.Length != actual.Length || expected.Length != width * height)
            {
                throw new InvalidOperationException(
                    $"Mip error distribution input mismatch: expected {expected.Length}, actual {actual.Length}, " +
                    $"dimensions {width}x{height}.");
            }

            for (int index = 0; index < expected.Length; index++)
            {
                Color32 expectedReadback = ToLinearReadback(expected[index], srgbRgb);
                Color32 actualReadback = actual[index];
                accumulator.Add(ChannelAbsoluteError(expectedReadback.r, actualReadback.r), slice, index, 0, width);
                accumulator.Add(ChannelAbsoluteError(expectedReadback.g, actualReadback.g), slice, index, 1, width);
                accumulator.Add(ChannelAbsoluteError(expectedReadback.b, actualReadback.b), slice, index, 2, width);
                accumulator.Add(ChannelAbsoluteError(expectedReadback.a, actualReadback.a), slice, index, 3, width);
            }
        }

        private static void AppendDistribution(string label, ErrorDistribution distribution, StringBuilder report)
        {
            report.AppendLine(
                $"{label}Mip1ErrorDistribution=Mean={distribution.Mean:F6}; P50={distribution.P50}; " +
                $"P95={distribution.P95}; P99={distribution.P99}; Max={distribution.Max}; " +
                $"MaxSlice={distribution.MaxSlice}; MaxChannel={distribution.MaxChannel}; " +
                $"MaxPixel=({distribution.MaxX},{distribution.MaxY}); " +
                $"ComparedChannels={distribution.ComparedChannels}");
        }

        private static void CompareColourSpaceHypotheses(
            Color32[] expectedStored,
            Color32[] actualLinearReadback,
            int width,
            int height,
            bool expectedSRGB,
            ref double correctError,
            ref double wrongError,
            ref long comparedChannels)
        {
            for (int gy = 0; gy < SampleGridSize; gy++)
            {
                int y = Mathf.Min(height - 1, (gy * 2 + 1) * height / (SampleGridSize * 2));
                for (int gx = 0; gx < SampleGridSize; gx++)
                {
                    int x = Mathf.Min(width - 1, (gx * 2 + 1) * width / (SampleGridSize * 2));
                    int index = y * width + x;
                    Color32 stored = expectedStored[index];
                    Color32 correct = ToLinearReadback(stored, expectedSRGB);
                    Color32 wrong = ToLinearReadback(stored, !expectedSRGB);
                    Color32 actual = actualLinearReadback[index];

                    correctError += ChannelAbsoluteError(correct.r, actual.r)
                        + ChannelAbsoluteError(correct.g, actual.g)
                        + ChannelAbsoluteError(correct.b, actual.b)
                        + ChannelAbsoluteError(correct.a, actual.a);
                    wrongError += ChannelAbsoluteError(wrong.r, actual.r)
                        + ChannelAbsoluteError(wrong.g, actual.g)
                        + ChannelAbsoluteError(wrong.b, actual.b)
                        + ChannelAbsoluteError(wrong.a, actual.a);
                    comparedChannels += 4;
                }
            }
        }

        private static int ChannelAbsoluteError(byte expected, byte actual)
        {
            return Mathf.Abs(expected - actual);
        }

        private static Color32 ToLinearReadback(Color32 stored, bool srgbRgb)
        {
            if (!srgbRgb)
            {
                return stored;
            }

            return new Color32(
                FloatToByte(Mathf.GammaToLinearSpace(stored.r / 255f)),
                FloatToByte(Mathf.GammaToLinearSpace(stored.g / 255f)),
                FloatToByte(Mathf.GammaToLinearSpace(stored.b / 255f)),
                stored.a);
        }

        private static Color32[] ReadBackArrayMip(Texture2DArray array, int slice, int mip)
        {
            int width = Mathf.Max(1, array.width >> mip);
            int height = Mathf.Max(1, array.height >> mip);
            bool linear = !GraphicsFormatUtility.IsSRGBFormat(array.graphicsFormat);
            var copy = new Texture2D(width, height, ArrayFormat, mipChain: false, linear)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
            };
            RenderTexture renderTexture = null;
            Texture2D readback = null;
            RenderTexture previous = RenderTexture.active;

            try
            {
                Graphics.CopyTexture(array, slice, mip, copy, 0, 0);
                renderTexture = RenderTexture.GetTemporary(
                    width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                renderTexture.filterMode = FilterMode.Point;
                Graphics.Blit(copy, renderTexture);
                RenderTexture.active = renderTexture;
                readback = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false, linear: true)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, recalculateMipMaps: false);
                readback.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                return readback.GetPixels32(0);
            }
            finally
            {
                RenderTexture.active = previous;
                if (renderTexture != null)
                {
                    RenderTexture.ReleaseTemporary(renderTexture);
                }

                if (readback != null)
                {
                    UnityEngine.Object.DestroyImmediate(readback);
                }

                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        private static Texture2DArray CreateArray(int width, int height, int depth, bool linear, string name)
        {
            var array = new Texture2DArray(width, height, depth, ArrayFormat, mipChain: true, linear)
            {
                name = name,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Repeat,
                anisoLevel = 4,
            };
            return array;
        }

        private static Texture2DArray PersistArray(Texture2DArray source, string path)
        {
            Texture2DArray existing = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(source, path);
                AssetDatabase.SaveAssets();
                return source;
            }

            EditorUtility.CopySerialized(source, existing);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            UnityEngine.Object.DestroyImmediate(source);
            return existing;
        }

        private static void AssertArrayColourSpaces(Texture2DArray csArray, Texture2DArray nohArray, StringBuilder report)
        {
            bool csIsSRGB = GraphicsFormatUtility.IsSRGBFormat(csArray.graphicsFormat);
            bool nohIsSRGB = GraphicsFormatUtility.IsSRGBFormat(nohArray.graphicsFormat);
            report.AppendLine($"CSGraphicsFormat={csArray.graphicsFormat}; IsSRGB={csIsSRGB}");
            report.AppendLine($"NOHGraphicsFormat={nohArray.graphicsFormat}; IsSRGB={nohIsSRGB}");
            if (!csIsSRGB)
            {
                throw new InvalidOperationException(
                    $"CS array colour-space mismatch: {csArray.graphicsFormat} is not sRGB.");
            }

            if (nohIsSRGB)
            {
                throw new InvalidOperationException(
                    $"NOH array colour-space mismatch: {nohArray.graphicsFormat} is sRGB, expected linear.");
            }
        }

        private static void RunReorderStalenessProbe(
            SolLandscapeConfig config,
            IReadOnlyList<TerrainLayer> layers,
            StringBuilder report)
        {
            if (layers.Count < 2)
            {
                throw new InvalidOperationException("Reorder staleness probe requires at least two layers.");
            }

            TerrainLayer[] reordered = layers.ToArray();
            (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
            SolLandscapeStaleness result = GetStaleness(config, reordered);
            report.AppendLine($"ReorderProbeStale={result.IsStale}; Message={result.Message}");
            if (!result.IsStale)
            {
                throw new InvalidOperationException("Deliberately swapping layers 0 and 1 did not flag staleness.");
            }
        }

        private static void RunMissingMapDefaultProbe(StringBuilder report)
        {
            Color32[] normal = CreateSolidPixels(16, FlatNormal);
            Color32[] mask = CreateSolidPixels(16, MidGreyMask);
            bool normalPass = normal.All(pixel => pixel.Equals(FlatNormal));
            bool maskPass = mask.All(pixel => pixel.Equals(MidGreyMask));
            Debug.LogWarning(
                "[Sol Landscape Phase 1] Synthetic missing-map probe: using flat normal " +
                "(128,128,255,255) and mid-grey mask (128,128,128,128).");
            report.AppendLine($"MissingNormalDefaultProbe={normalPass}; RGBA=(128,128,255,255); Pixels={normal.Length}");
            report.AppendLine($"MissingMaskDefaultProbe={maskPass}; RGBA=(128,128,128,128); Pixels={mask.Length}");
            report.AppendLine("MissingMapWarningProbe=1 warning emitted");
            if (!normalPass || !maskPass)
            {
                throw new InvalidOperationException("Missing-map default synthesis probe failed.");
            }
        }

        private static void RunFailureGuardProbes(SourceLayout layout, StringBuilder report)
        {
            bool sizeFailed = false;
            bool formatFailed = false;
            try
            {
                ValidateDimensions(layout.Width, layout.Height, layout.Width / 2, layout.Height, "synthetic size probe");
            }
            catch (InvalidOperationException)
            {
                sizeFailed = true;
            }

            try
            {
                ValidateFormats(GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.R8G8B8A8_UNorm,
                    "synthetic format probe");
            }
            catch (InvalidOperationException)
            {
                formatFailed = true;
            }

            report.AppendLine($"MismatchedSizeGuardThrows={sizeFailed}");
            report.AppendLine($"MismatchedFormatGuardThrows={formatFailed}");
            if (!sizeFailed || !formatFailed)
            {
                throw new InvalidOperationException("A size/format failure guard did not throw.");
            }
        }

        private static void ValidateDimensions(Texture2D texture, int width, int height, int slice, string role)
        {
            ValidateDimensions(width, height, texture.width, texture.height,
                $"slice {slice} {role} texture {AssetDatabase.GetAssetPath(texture)}");
        }

        private static void ValidateDimensions(int expectedWidth, int expectedHeight, int actualWidth, int actualHeight,
            string context)
        {
            if (actualWidth != expectedWidth || actualHeight != expectedHeight)
            {
                throw new InvalidOperationException(
                    $"Texture size mismatch for {context}: {actualWidth}x{actualHeight}; " +
                    $"expected {expectedWidth}x{expectedHeight}.");
            }
        }

        private static void ValidateRoleFormat(
            Texture2D texture,
            ref GraphicsFormat? expectedFormat,
            int slice,
            string role)
        {
            if (!expectedFormat.HasValue)
            {
                expectedFormat = texture.graphicsFormat;
                return;
            }

            ValidateFormats(expectedFormat.Value, texture.graphicsFormat,
                $"slice {slice} {role} texture {AssetDatabase.GetAssetPath(texture)}");
        }

        private static void ValidateFormats(GraphicsFormat expected, GraphicsFormat actual, string context)
        {
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"Texture format mismatch for {context}: {actual}; expected {expected}.");
            }
        }

        private static void WarnMissingMap(int slice, TerrainLayer layer, string role, string synthesized)
        {
            string prefix = slice >= 0 ? $"slice {slice} " : string.Empty;
            Debug.LogWarning(
                $"[Sol Landscape Phase 1] {prefix}{layer.name} has no {role} map; synthesizing {synthesized}.");
        }

        private static string DescribeTexture(Texture2D texture)
        {
            return texture == null
                ? "missing/default"
                : $"{AssetDatabase.GetAssetPath(texture)}|{texture.width}x{texture.height}|{texture.graphicsFormat}";
        }

        private static void AssertDiffuseImporter(Texture2D texture, int slice, StringBuilder report)
        {
            TextureImporter importer = RequireTextureImporter(texture, slice, "diffuse");
            report.AppendLine(
                $"Importer[{slice}].Diffuse={AssetDatabase.GetAssetPath(texture)}; " +
                $"sRGBTexture={importer.sRGBTexture}; TextureType={importer.textureType}");
            if (!importer.sRGBTexture)
            {
                throw new InvalidOperationException(
                    $"Slice {slice} diffuse importer must have sRGBTexture=true: {AssetDatabase.GetAssetPath(texture)}.");
            }
        }

        private static void AssertNormalImporter(Texture2D texture, int slice, StringBuilder report)
        {
            TextureImporter importer = RequireTextureImporter(texture, slice, "normal");
            report.AppendLine(
                $"Importer[{slice}].Normal={AssetDatabase.GetAssetPath(texture)}; " +
                $"TextureType={importer.textureType}; ConvertToNormalmap={importer.convertToNormalmap}; " +
                $"FlipGreenChannel={importer.flipGreenChannel}");
            if (importer.textureType != TextureImporterType.NormalMap)
            {
                throw new InvalidOperationException(
                    $"Slice {slice} normal importer type is {importer.textureType}; expected NormalMap: " +
                    AssetDatabase.GetAssetPath(texture));
            }

            if (importer.convertToNormalmap)
            {
                throw new InvalidOperationException(
                    $"Slice {slice} normal importer has convertToNormalmap=true; expected false: " +
                    AssetDatabase.GetAssetPath(texture));
            }
        }

        private static void AssertMaskImporter(Texture2D texture, int slice, StringBuilder report)
        {
            TextureImporter importer = RequireTextureImporter(texture, slice, "mask");
            report.AppendLine(
                $"Importer[{slice}].Mask={AssetDatabase.GetAssetPath(texture)}; " +
                $"sRGBTexture={importer.sRGBTexture}; TextureType={importer.textureType}");
            if (importer.sRGBTexture)
            {
                throw new InvalidOperationException(
                    $"Slice {slice} mask importer must have sRGBTexture=false: {AssetDatabase.GetAssetPath(texture)}.");
            }
        }

        private static TextureImporter RequireTextureImporter(Texture2D texture, int slice, string role)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
            {
                throw new InvalidOperationException(
                    $"Slice {slice} {role} source does not have a TextureImporter: {path}.");
            }

            return importer;
        }

        private static List<SolLandscapeBakeFingerprint> BuildFingerprints(IReadOnlyList<TerrainLayer> layers)
        {
            var result = new List<SolLandscapeBakeFingerprint>(layers.Count);
            for (int i = 0; i < layers.Count; i++)
            {
                result.Add(BuildFingerprint(layers[i]));
            }

            return result;
        }

        private static SolLandscapeBakeFingerprint BuildFingerprint(TerrainLayer layer)
        {
            return new SolLandscapeBakeFingerprint(
                GuidFor(layer),
                GuidFor(layer.diffuseTexture),
                GuidFor(layer.normalMapTexture),
                GuidFor(layer.maskMapTexture));
        }

        private static string GuidFor(UnityEngine.Object asset)
        {
            return asset == null ? string.Empty : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
        }

        private static Color32[] CreateSolidPixels(int count, Color32 value)
        {
            var pixels = new Color32[count];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = value;
            }

            return pixels;
        }

        private static byte FloatToByte(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 255f), 0, 255);
        }

        private static int CalculateMipCount(int width, int height)
        {
            int count = 1;
            while (width > 1 || height > 1)
            {
                width = Mathf.Max(1, width >> 1);
                height = Mathf.Max(1, height >> 1);
                count++;
            }

            return count;
        }

        private static int CalculateBC7MipBytes(int width, int height)
        {
            int blocksWide = Mathf.Max(1, (width + 3) / 4);
            int blocksHigh = Mathf.Max(1, (height + 3) / 4);
            return checked(blocksWide * blocksHigh * 16);
        }

        private static long CalculateBC7ChainBytes(int width, int height, int depth)
        {
            long perSlice = 0;
            while (true)
            {
                perSlice += CalculateBC7MipBytes(width, height);
                if (width == 1 && height == 1)
                {
                    break;
                }

                width = Mathf.Max(1, width >> 1);
                height = Mathf.Max(1, height >> 1);
            }

            return perSlice * depth;
        }

        private static long GetStorageMemorySize(Texture texture)
        {
            Type textureUtil = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.TextureUtil");
            MethodInfo method = textureUtil?.GetMethod(
                "GetStorageMemorySizeLong",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Texture) },
                null);
            if (method == null)
            {
                throw new MissingMethodException("UnityEditor.TextureUtil.GetStorageMemorySizeLong(Texture) was not found.");
            }

            return Convert.ToInt64(method.Invoke(null, new object[] { texture }));
        }

        private static long GetAssetFileLength(string assetPath)
        {
            string absolutePath = Path.GetFullPath(assetPath);
            return File.Exists(absolutePath) ? new FileInfo(absolutePath).Length : -1L;
        }

        private static void EnsureOutputFolder()
        {
            if (AssetDatabase.IsValidFolder(OutputFolder))
            {
                return;
            }

            const string parent = "Assets/Sky-and-Water";
            string guid = AssetDatabase.CreateFolder(parent, "Landscape");
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException($"Could not create output folder {OutputFolder}.");
            }
        }

        private static string BuildSummary(
            IReadOnlyList<TerrainLayer> layers,
            SourceLayout layout,
            NumericValidation numeric,
            long actualBytes,
            long expectedBytes)
        {
            var summary = new StringBuilder();
            summary.AppendLine($"{layout.Width}x{layout.Height}, {layers.Count} slices, {layout.MipCount} explicit mips, BC7.");
            summary.AppendLine(
                "CS is sRGB (RGB colour + A roughness-derived remapped smoothness); " +
                "NOH is linear (RG importer-oriented normal + B AO + A height).");
            summary.AppendLine(
                $"Mip-1 BC7 error: CS mean {numeric.CSDistribution.Mean:F4}, " +
                $"p99 {numeric.CSDistribution.P99}, max {numeric.CSDistribution.Max}; " +
                $"NOH mean {numeric.NOHDistribution.Mean:F4}, " +
                $"p99 {numeric.NOHDistribution.P99}, max {numeric.NOHDistribution.Max}.");
            summary.AppendLine($"Storage: {actualBytes} bytes measured; {expectedBytes} bytes exact BC7 estimate.");
            for (int i = 0; i < layers.Count; i++)
            {
                summary.AppendLine($"Slice {i}: {layers[i].name}");
            }

            return summary.ToString().TrimEnd();
        }

        private static HashBaseline LoadHashBaseline(StringBuilder report)
        {
            string baselinePath = GetHashBaselinePath();
            if (!File.Exists(baselinePath))
            {
                throw new InvalidOperationException(
                    $"Immutable Phase 1.1 hash baseline is missing at {baselinePath}; refusing to rebake.");
            }

            var hashes = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(baselinePath))
            {
                Match match = BaselineHashPattern.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                string key = HashBaseline.MakeKey(
                    match.Groups[1].Value,
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
                ulong hash = ulong.Parse(match.Groups[4].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (!hashes.TryAdd(key, hash))
                {
                    throw new InvalidOperationException($"Duplicate trusted hash baseline record: {key}.");
                }
            }

            const int expectedRecords = 36;
            if (hashes.Count != expectedRecords)
            {
                throw new InvalidOperationException(
                    $"Trusted Phase 1 evidence contains {hashes.Count} hash records; expected {expectedRecords}.");
            }

            report.AppendLine($"BaselineHashRecordsLoaded={hashes.Count}; Source={baselinePath}; Immutable=True");
            return new HashBaseline(hashes);
        }

        private static void WriteEvidence(string contents)
        {
            File.WriteAllText(GetEvidencePath(), contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static string GetEvidencePath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", EvidenceFileName));
        }

        private static string GetHashBaselinePath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", HashBaselineFileName));
        }

        private readonly struct SourceLayout
        {
            public readonly int Width;
            public readonly int Height;
            public readonly int MipCount;

            public SourceLayout(int width, int height, int mipCount)
            {
                Width = width;
                Height = height;
                MipCount = mipCount;
            }
        }

        private readonly struct PackedBases
        {
            public readonly Color32[] CS;
            public readonly Color32[] NOH;
            public readonly LogicalChannelMovement Movement;
            public readonly double SmoothnessMin;
            public readonly double SmoothnessMean;
            public readonly double SmoothnessMax;
            public readonly double NormalHeightCorrelation;

            public PackedBases(
                Color32[] cs,
                Color32[] noh,
                LogicalChannelMovement movement,
                double smoothnessMin,
                double smoothnessMean,
                double smoothnessMax,
                double normalHeightCorrelation)
            {
                CS = cs;
                NOH = noh;
                Movement = movement;
                SmoothnessMin = smoothnessMin;
                SmoothnessMean = smoothnessMean;
                SmoothnessMax = smoothnessMax;
                NormalHeightCorrelation = normalHeightCorrelation;
            }
        }

        private sealed class LogicalChannelMovement
        {
            public readonly long[] CS = new long[4];
            public readonly long[] NOH = new long[4];

            public void Add(Color32 oldCS, Color32 newCS, Color32 oldNOH, Color32 newNOH)
            {
                AddChannels(oldCS, newCS, CS);
                AddChannels(oldNOH, newNOH, NOH);
            }

            public void AssertExpected(int slice)
            {
                bool csOnlyAlphaChanged = CS[0] == 0 && CS[1] == 0 && CS[2] == 0 && CS[3] > 0;
                bool nohExpected = NOH[0] == 0 && NOH[2] == 0 && NOH[3] == 0 &&
                    (slice == 0 ? NOH[1] > 0 : NOH[1] == 0);
                if (!csOnlyAlphaChanged || !nohExpected)
                {
                    throw new InvalidOperationException(
                        $"Unexpected logical channel movement at slice {slice}: " +
                        $"CS=({CS[0]},{CS[1]},{CS[2]},{CS[3]}), " +
                        $"NOH=({NOH[0]},{NOH[1]},{NOH[2]},{NOH[3]}). " +
                        "Expected CS alpha on every slice and NOH green on slice 0 only.");
                }
            }

            private static void AddChannels(Color32 oldValue, Color32 newValue, long[] counts)
            {
                if (oldValue.r != newValue.r) counts[0]++;
                if (oldValue.g != newValue.g) counts[1]++;
                if (oldValue.b != newValue.b) counts[2]++;
                if (oldValue.a != newValue.a) counts[3]++;
            }
        }

        private readonly struct HashComparison
        {
            public readonly bool Changed;

            public HashComparison(bool changed)
            {
                Changed = changed;
            }
        }

        private sealed class HashBaseline
        {
            private readonly IReadOnlyDictionary<string, ulong> hashes;
            private readonly HashSet<string> matchedKeys = new HashSet<string>(StringComparer.Ordinal);

            public HashBaseline(IReadOnlyDictionary<string, ulong> hashes)
            {
                this.hashes = hashes;
            }

            public HashComparison AssertExpectedMovement(
                string label,
                int slice,
                int mip,
                ulong actual,
                bool expectedChanged)
            {
                string key = MakeKey(label, slice, mip);
                if (!hashes.TryGetValue(key, out ulong expected))
                {
                    throw new InvalidOperationException($"Trusted Phase 1 hash baseline has no record for {key}.");
                }

                bool changed = actual != expected;
                if (changed != expectedChanged)
                {
                    throw new InvalidOperationException(
                        $"Unexpected compressed hash movement for {key}: baseline {expected:X16}, " +
                        $"actual {actual:X16}, changed {changed}, expected changed {expectedChanged}. " +
                        "Stopping rather than accepting unexplained array movement.");
                }

                matchedKeys.Add(key);
                return new HashComparison(changed);
            }

            public void AssertComplete(StringBuilder report)
            {
                if (matchedKeys.Count != hashes.Count)
                {
                    throw new InvalidOperationException(
                        $"Compared {matchedKeys.Count} trusted hashes; expected {hashes.Count}.");
                }

                report.AppendLine($"BaselineHashMovementChecks={matchedKeys.Count}/{hashes.Count}");
            }

            public static string MakeKey(string label, int slice, int mip)
            {
                return $"{label}Slice[{slice}]Mip[{mip}]";
            }
        }

        private sealed class ErrorDistributionAccumulator
        {
            private readonly long[] histogram = new long[256];
            private long errorSum;
            private long comparedChannels;
            private int max = -1;
            private int maxSlice = -1;
            private int maxChannelIndex = -1;
            private int maxX = -1;
            private int maxY = -1;

            public void Add(int error, int slice, int pixelIndex, int channelIndex, int width)
            {
                histogram[error]++;
                errorSum += error;
                comparedChannels++;
                if (error <= max)
                {
                    return;
                }

                max = error;
                maxSlice = slice;
                maxChannelIndex = channelIndex;
                maxX = pixelIndex % width;
                maxY = pixelIndex / width;
            }

            public ErrorDistribution Build()
            {
                if (comparedChannels == 0)
                {
                    throw new InvalidOperationException("Cannot build an empty BC7 error distribution.");
                }

                return new ErrorDistribution(
                    errorSum / (double)comparedChannels,
                    Quantile(0.50d),
                    Quantile(0.95d),
                    Quantile(0.99d),
                    max,
                    maxSlice,
                    ChannelName(maxChannelIndex),
                    maxX,
                    maxY,
                    comparedChannels);
            }

            private int Quantile(double percentile)
            {
                long targetRank = Math.Max(1L, (long)Math.Ceiling(percentile * comparedChannels));
                long cumulative = 0L;
                for (int error = 0; error < histogram.Length; error++)
                {
                    cumulative += histogram[error];
                    if (cumulative >= targetRank)
                    {
                        return error;
                    }
                }

                return 255;
            }

            private static string ChannelName(int channelIndex)
            {
                return channelIndex switch
                {
                    0 => "R",
                    1 => "G",
                    2 => "B",
                    3 => "A",
                    _ => "Unknown",
                };
            }
        }

        private readonly struct ErrorDistribution
        {
            public readonly double Mean;
            public readonly int P50;
            public readonly int P95;
            public readonly int P99;
            public readonly int Max;
            public readonly int MaxSlice;
            public readonly string MaxChannel;
            public readonly int MaxX;
            public readonly int MaxY;
            public readonly long ComparedChannels;

            public ErrorDistribution(
                double mean,
                int p50,
                int p95,
                int p99,
                int max,
                int maxSlice,
                string maxChannel,
                int maxX,
                int maxY,
                long comparedChannels)
            {
                Mean = mean;
                P50 = p50;
                P95 = p95;
                P99 = p99;
                Max = max;
                MaxSlice = maxSlice;
                MaxChannel = maxChannel;
                MaxX = maxX;
                MaxY = maxY;
                ComparedChannels = comparedChannels;
            }
        }

        private readonly struct NumericValidation
        {
            public readonly ErrorDistribution CSDistribution;
            public readonly ErrorDistribution NOHDistribution;
            public readonly double CSColourCorrectMAE;
            public readonly double CSColourWrongMAE;
            public readonly double NOHColourCorrectMAE;
            public readonly double NOHColourWrongMAE;

            public NumericValidation(
                ErrorDistribution csDistribution,
                ErrorDistribution nohDistribution,
                double csColourCorrectMAE,
                double csColourWrongMAE,
                double nohColourCorrectMAE,
                double nohColourWrongMAE)
            {
                CSDistribution = csDistribution;
                NOHDistribution = nohDistribution;
                CSColourCorrectMAE = csColourCorrectMAE;
                CSColourWrongMAE = csColourWrongMAE;
                NOHColourCorrectMAE = nohColourCorrectMAE;
                NOHColourWrongMAE = nohColourWrongMAE;
            }
        }
    }

    [CustomEditor(typeof(SolLandscapeConfig))]
    internal sealed class SolLandscapeConfigEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox(
                "Production uses baked Manual alphamaps. Sculpting refreshes unpainted procedural texels in the editor; texture-painted texels and Path are protected.",
                MessageType.Info);
            DrawDefaultInspector();
            serializedObject.ApplyModifiedProperties();

            var config = (SolLandscapeConfig)target;
            SolLandscapeStaleness staleness = SolLandscapeArrayBaker.GetStaleness(config);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bake status", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                staleness.Message,
                staleness.IsStale ? MessageType.Warning : MessageType.Info);

            using (new EditorGUI.DisabledScope(EditorApplication.isCompiling || EditorApplication.isUpdating))
            {
                if (GUILayout.Button("Rebake CSNOH Arrays"))
                {
                    SolLandscapeArrayBaker.BakeTarget(config);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Landscape authoring", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                Sol.Water.SolWaterBody ocean = FindOcean();
                EditorGUILayout.LabelField("Production scene", "Sols_Water2_Demo");
                EditorGUILayout.LabelField("Resolved Water2 level", ocean != null ? ocean.SurfaceLevel.ToString("0.###") + " m" : "No active ocean");
                bool liveUpdates = SolLandscapeLiveAlphamapUpdater.LiveUpdatesAreEnabled;
                bool requestedLiveUpdates = EditorGUILayout.Toggle("Live sculpt texture refresh", liveUpdates);
                if (requestedLiveUpdates != liveUpdates)
                    SolLandscapeLiveAlphamapUpdater.LiveUpdatesAreEnabled = requestedLiveUpdates;

                using (new EditorGUI.DisabledScope(EditorApplication.isCompiling || EditorApplication.isUpdating))
                {
                    if (GUILayout.Button("Regenerate Procedural Areas (Keep Painted Textures)"))
                        EditorApplication.ExecuteMenuItem("Tools/Sol Landscape/Regenerate Procedural Areas (Keep Painted Textures)");
                    if (GUILayout.Button("Protect / Re-detect Current Texture Work"))
                        EditorApplication.ExecuteMenuItem("Tools/Sol Landscape/Protect Current Texture Work");
                    if (GUILayout.Button("Validate Water2 Production Wiring"))
                        EditorApplication.ExecuteMenuItem("Tools/Sol Landscape/Validate Water2 Production Wiring");
                }
            }

            EditorGUILayout.HelpBox(
                "Destructive full regeneration is intentionally available only under Tools/Sol Landscape/Advanced. Height-sensitive sediment ranges are relative to the resolved Water2 level.",
                MessageType.Warning);
        }

        private static Sol.Water.SolWaterBody FindOcean()
        {
            foreach (Sol.Water.SolWaterBody body in UnityEngine.Object.FindObjectsByType<Sol.Water.SolWaterBody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (body != null && body.isActiveAndEnabled && body.IsInfinite)
                    return body;
            }
            return null;
        }
    }
}
