using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

    /// <summary>
    /// Packs each TerrainLayer's diffuse/normal/mask sources into the two BC7 texture arrays the
    /// landscape shader samples: CS (sRGB; RGB colour, A smoothness) and NOH (linear; RG normal,
    /// B ambient occlusion, A height).
    /// </summary>
    /// <remarks>
    /// This is the only offline step in the landscape system. Layer *weights* are resolved live in
    /// the shader from slope, altitude and cavity - nothing about the terrain's appearance is baked.
    /// Only the layer artwork is, because compressing textures is not something a fragment shader
    /// can do. A rebake is needed when layer artwork or import settings change, which is what the
    /// per-slice dependency hashes detect.
    /// </remarks>
    internal static class SolLandscapeArrayBaker
    {
        private const string OutputFolder = "Assets/Earth-Sky-Water/Landscape";
        private const string ConfigPath = OutputFolder + "/SolLandscapeConfig.asset";
        private const string CSArrayPath = OutputFolder + "/SolLandscapeCSArray.asset";
        private const string NOHArrayPath = OutputFolder + "/SolLandscapeNOHArray.asset";
        private const TextureFormat ArrayFormat = TextureFormat.BC7;

        private static readonly Color32 FlatNormal = new Color32(128, 128, 255, 255);
        private static readonly Color32 MidGreyMask = new Color32(128, 128, 128, 128);

        [MenuItem("Tools/Sol Landscape/Bake Layer Arrays")]
        private static void BakeFromMenu()
        {
            SolLandscapeConfig config = BakeTarget();
            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);
        }

        internal static SolLandscapeConfig BakeTarget(SolLandscapeConfig requestedConfig = null)
        {
            TerrainData terrainData = ResolveTargetTerrainData(requestedConfig);

            TerrainLayer[] layers = terrainData.terrainLayers;
            if (layers == null || layers.Length == 0)
            {
                throw new InvalidOperationException("The target TerrainData has no TerrainLayers.");
            }

            SourceLayout layout = ValidateSourceLayout(layers);
            EnsureOutputFolder();

            Texture2DArray csArray = CreateArray(layout.Width, layout.Height, layers.Length, linear: false,
                "SolLandscapeCSArray");
            Texture2DArray nohArray = CreateArray(layout.Width, layout.Height, layers.Length, linear: true,
                "SolLandscapeNOHArray");

            try
            {
                BakeSlices(layers, layout, csArray, nohArray);
                AssertArrayColourSpaces(csArray, nohArray);

                csArray.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                nohArray.Apply(updateMipmaps: false, makeNoLongerReadable: true);

                Texture2DArray persistedCS = PersistArray(csArray, CSArrayPath);
                csArray = null;
                Texture2DArray persistedNOH = PersistArray(nohArray, NOHArrayPath);
                nohArray = null;

                AssertArrayColourSpaces(persistedCS, persistedNOH);

                long storageBytesPerArray = CalculateBC7ChainBytes(layout.Width, layout.Height, layers.Length);

                SolLandscapeConfig config = requestedConfig != null
                    ? requestedConfig
                    : AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);

                if (config == null)
                {
                    config = ScriptableObject.CreateInstance<SolLandscapeConfig>();
                    config.name = "SolLandscapeConfig";
                    AssetDatabase.CreateAsset(config, ConfigPath);
                }

                config.RecordBake(
                    terrainData,
                    layers,
                    persistedCS,
                    persistedNOH,
                    AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(terrainData)),
                    BuildFingerprints(layers),
                    DateTime.UtcNow.ToString("O"),
                    storageBytesPerArray,
                    storageBytesPerArray,
                    BuildSummary(layers, layout, storageBytesPerArray * 2L));
                EditorUtility.SetDirty(config);
                AssetDatabase.SaveAssetIfDirty(config);

                SolLandscapeStaleness fresh = GetStaleness(config);
                if (fresh.IsStale)
                {
                    throw new InvalidOperationException($"A just-baked config is stale: {fresh.Message}");
                }

                Debug.Log(
                    $"[Sol Landscape] Baked {layers.Length} layer slices at {layout.Width}x{layout.Height} " +
                    $"({layout.MipCount} mips, BC7, {storageBytesPerArray * 2L / 1024L / 1024L} MB total).");
                return config;
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

                if (!string.Equals(baked.DependencyHash, current.DependencyHash, StringComparison.Ordinal))
                {
                    return new SolLandscapeStaleness(true,
                        $"Layer content or importer settings changed at slice {i} ({layer.name}); " +
                        "the same assets no longer produce the baked arrays.");
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

            return new SolLandscapeStaleness(false,
                "Baked arrays match layer count, order, texture GUIDs, and per-slice dependency hashes.");
        }

        private static SourceLayout ValidateSourceLayout(IReadOnlyList<TerrainLayer> layers)
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
                AssertDiffuseImporter(layer.diffuseTexture, i);

                if (layer.normalMapTexture != null)
                {
                    ValidateDimensions(layer.normalMapTexture, width, height, i, "normal");
                    ValidateRoleFormat(layer.normalMapTexture, ref normalFormat, i, "normal");
                    AssertNormalImporter(layer.normalMapTexture, i);
                }
                else
                {
                    WarnMissingMap(i, layer, "normal", "flat normal (128,128,255,255)");
                }

                if (layer.maskMapTexture != null)
                {
                    ValidateDimensions(layer.maskMapTexture, width, height, i, "mask");
                    ValidateRoleFormat(layer.maskMapTexture, ref maskFormat, i, "mask");
                    AssertMaskImporter(layer.maskMapTexture, i);
                }
                else
                {
                    WarnMissingMap(i, layer, "mask", "mid-grey mask (128,128,128,128)");
                }
            }

            return new SourceLayout(width, height, CalculateMipCount(width, height));
        }

        private static void BakeSlices(
            IReadOnlyList<TerrainLayer> layers,
            SourceLayout layout,
            Texture2DArray csArray,
            Texture2DArray nohArray)
        {
            for (int slice = 0; slice < layers.Count; slice++)
            {
                PackedBases packed = BuildPackedBases(layers[slice], layout);

                List<Color32[]> csMips = BuildMipChain(packed.CS, layout.Width, layout.Height, srgbRgb: true);
                List<Color32[]> nohMips = BuildMipChain(packed.NOH, layout.Width, layout.Height, srgbRgb: false);

                WriteCompressedSlice(csArray, slice, csMips, layout, "CS");
                WriteCompressedSlice(nohArray, slice, nohMips, layout, "NOH");
            }
        }

        private static PackedBases BuildPackedBases(TerrainLayer layer, SourceLayout layout)
        {
            Color32[] diffuse = ReadSourcePixels(layer.diffuseTexture, layout);
            Color32[] normal = layer.normalMapTexture != null
                ? ReadSourcePixels(layer.normalMapTexture, layout)
                : CreateSolidPixels(layout.Width * layout.Height, FlatNormal);
            Color32[] mask = layer.maskMapTexture != null
                ? ReadSourcePixels(layer.maskMapTexture, layout)
                : CreateSolidPixels(layout.Width * layout.Height, MidGreyMask);

            var cs = new Color32[diffuse.Length];
            var noh = new Color32[diffuse.Length];
            Vector4 diffuseMin = layer.diffuseRemapMin;
            Vector4 diffuseMax = layer.diffuseRemapMax;
            Vector4 maskMin = layer.maskMapRemapMin;
            Vector4 maskMax = layer.maskMapRemapMax;
            float smoothnessMin = layer.maskMapRemapMin.w;
            float smoothnessMax = layer.maskMapRemapMax.w;
            bool flipNormalGreen = layer.normalMapTexture != null && GetNormalFlipGreen(layer.normalMapTexture);

            for (int i = 0; i < diffuse.Length; i++)
            {
                // These authored masks deliberately use R=roughness, G=AO, B=height, A=unused,
                // rather than Unity's documented R=metallic/G=AO/B=height/A=smoothness layout.
                // The existing inverted W remap converts roughness to smoothness: 0.3 * (1 - R).
                byte smoothness = FloatToByte(Mathf.Lerp(smoothnessMin, smoothnessMax, mask[i].r / 255f));
                byte albedoR = FloatToByte(Mathf.Lerp(diffuseMin.x, diffuseMax.x, diffuse[i].r / 255f));
                byte albedoG = FloatToByte(Mathf.Lerp(diffuseMin.y, diffuseMax.y, diffuse[i].g / 255f));
                byte albedoB = FloatToByte(Mathf.Lerp(diffuseMin.z, diffuseMax.z, diffuse[i].b / 255f));
                byte ambientOcclusion = FloatToByte(Mathf.Lerp(maskMin.y, maskMax.y, mask[i].g / 255f));
                byte height = FloatToByte(Mathf.Lerp(maskMin.z, maskMax.z, mask[i].b / 255f));
                byte normalGreen = flipNormalGreen ? (byte)(255 - normal[i].g) : normal[i].g;

                cs[i] = new Color32(albedoR, albedoG, albedoB, smoothness);
                noh[i] = new Color32(normal[i].r, normalGreen, ambientOcclusion, height);
            }

            return new PackedBases(cs, noh);
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
            string label)
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

                    destination.SetPixelData(payload, mip, slice);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static TerrainData ResolveTargetTerrainData(SolLandscapeConfig requestedConfig)
        {
            if (requestedConfig != null && requestedConfig.TerrainData != null)
            {
                return requestedConfig.TerrainData;
            }

            var config = AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);
            if (config != null && config.TerrainData != null)
            {
                return config.TerrainData;
            }

            throw new InvalidOperationException(
                $"No target TerrainData is configured. Assign one on {ConfigPath}.");
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
                AssetDatabase.SaveAssetIfDirty(source);
                return source;
            }

            EditorUtility.CopySerialized(source, existing);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssetIfDirty(existing);
            UnityEngine.Object.DestroyImmediate(source);
            return existing;
        }

        /// <summary>
        /// The shader reads CS through the sRGB sampler and NOH raw. Silently swapping the two
        /// colour spaces produces a plausible-looking but wrong result, so it is a hard failure.
        /// </summary>
        private static void AssertArrayColourSpaces(Texture2DArray csArray, Texture2DArray nohArray)
        {
            if (!GraphicsFormatUtility.IsSRGBFormat(csArray.graphicsFormat))
            {
                throw new InvalidOperationException(
                    $"CS array colour-space mismatch: {csArray.graphicsFormat} is not sRGB.");
            }

            if (GraphicsFormatUtility.IsSRGBFormat(nohArray.graphicsFormat))
            {
                throw new InvalidOperationException(
                    $"NOH array colour-space mismatch: {nohArray.graphicsFormat} is sRGB, expected linear.");
            }
        }

        private static void ValidateDimensions(Texture2D texture, int width, int height, int slice, string role)
        {
            if (texture.width != width || texture.height != height)
            {
                throw new InvalidOperationException(
                    $"Texture size mismatch for slice {slice} {role} texture " +
                    $"{AssetDatabase.GetAssetPath(texture)}: {texture.width}x{texture.height}; " +
                    $"expected {width}x{height}.");
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

            if (texture.graphicsFormat != expectedFormat.Value)
            {
                throw new InvalidOperationException(
                    $"Texture format mismatch for slice {slice} {role} texture " +
                    $"{AssetDatabase.GetAssetPath(texture)}: {texture.graphicsFormat}; " +
                    $"expected {expectedFormat.Value}.");
            }
        }

        private static void WarnMissingMap(int slice, TerrainLayer layer, string role, string synthesized)
        {
            Debug.LogWarning(
                $"[Sol Landscape] Slice {slice} ({layer.name}) has no {role} map; synthesizing {synthesized}.");
        }

        private static void AssertDiffuseImporter(Texture2D texture, int slice)
        {
            TextureImporter importer = RequireTextureImporter(texture, slice, "diffuse");
            if (!importer.sRGBTexture)
            {
                throw new InvalidOperationException(
                    $"Slice {slice} diffuse importer must have sRGBTexture=true: {AssetDatabase.GetAssetPath(texture)}.");
            }
        }

        private static void AssertNormalImporter(Texture2D texture, int slice)
        {
            TextureImporter importer = RequireTextureImporter(texture, slice, "normal");
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

        private static void AssertMaskImporter(Texture2D texture, int slice)
        {
            TextureImporter importer = RequireTextureImporter(texture, slice, "mask");
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
                GuidFor(layer.maskMapTexture),
                DependencyHashFor(layer));
        }

        /// <summary>
        /// Combined content-and-importer hash for a layer and its source textures.
        /// </summary>
        /// <remarks>
        /// AssetDatabase.GetAssetDependencyHash covers the asset's contents and its importer settings,
        /// so editing a texture in place or changing an import setting moves this value where a GUID
        /// comparison does not.
        /// </remarks>
        private static string DependencyHashFor(TerrainLayer layer)
        {
            var builder = new StringBuilder();
            AppendDependencyHash(builder, layer);
            AppendDependencyHash(builder, layer.diffuseTexture);
            AppendDependencyHash(builder, layer.normalMapTexture);
            AppendDependencyHash(builder, layer.maskMapTexture);
            return builder.ToString();
        }

        private static void AppendDependencyHash(StringBuilder builder, UnityEngine.Object asset)
        {
            string path = asset == null ? string.Empty : AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path))
            {
                builder.Append("-;");
                return;
            }

            builder.Append(AssetDatabase.GetAssetDependencyHash(path).ToString()).Append(';');
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

        private static void EnsureOutputFolder()
        {
            if (AssetDatabase.IsValidFolder(OutputFolder))
            {
                return;
            }

            const string parent = "Assets/Earth-Sky-Water";
            string guid = AssetDatabase.CreateFolder(parent, "Landscape");
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException($"Could not create output folder {OutputFolder}.");
            }
        }

        private static string BuildSummary(IReadOnlyList<TerrainLayer> layers, SourceLayout layout, long totalBytes)
        {
            var summary = new StringBuilder();
            summary.AppendLine($"{layout.Width}x{layout.Height}, {layers.Count} slices, {layout.MipCount} mips, BC7.");
            summary.AppendLine(
                "CS is sRGB (RGB colour + A roughness-derived remapped smoothness); " +
                "NOH is linear (RG importer-oriented normal + B AO + A height).");
            summary.AppendLine($"Storage: {totalBytes} bytes across both arrays.");
            for (int i = 0; i < layers.Count; i++)
            {
                summary.AppendLine($"Slice {i}: {layers[i].name}");
            }

            return summary.ToString().TrimEnd();
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

            public PackedBases(Color32[] cs, Color32[] noh)
            {
                CS = cs;
                NOH = noh;
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
                "Layer weights resolve live in the shader. Set a layer to Auto and its slope, altitude "
                + "and cavity rules drive it in real time as you sculpt; set it to Manual and its painted "
                + "alphamap channel is authoritative. Auto layers share whatever weight the Manual layers "
                + "do not claim.",
                MessageType.Info);
            DrawDefaultInspector();
            serializedObject.ApplyModifiedProperties();

            var config = (SolLandscapeConfig)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Layer artwork bake", EditorStyles.boldLabel);
            SolLandscapeStaleness staleness = SolLandscapeArrayBaker.GetStaleness(config);
            EditorGUILayout.HelpBox(
                staleness.Message,
                staleness.IsStale ? MessageType.Warning : MessageType.Info);

            using (new EditorGUI.DisabledScope(EditorApplication.isCompiling || EditorApplication.isUpdating))
            {
                if (GUILayout.Button("Bake Layer Arrays"))
                {
                    SolLandscapeArrayBaker.BakeTarget(config);
                }
            }

            EditorGUILayout.Space();
            Sol.Water.SolWaterBody ocean = FindOcean();
            EditorGUILayout.LabelField(
                "Resolved water level",
                ocean != null ? ocean.SurfaceLevel.ToString("0.###") + " m" : "No active ocean");
        }

        private static Sol.Water.SolWaterBody FindOcean()
        {
            foreach (Sol.Water.SolWaterBody body in UnityEngine.Object.FindObjectsByType<Sol.Water.SolWaterBody>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (body != null && body.isActiveAndEnabled && body.IsInfinite)
                {
                    return body;
                }
            }

            return null;
        }
    }
}
