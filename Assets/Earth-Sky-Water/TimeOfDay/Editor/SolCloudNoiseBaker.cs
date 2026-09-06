using System.IO;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Bakes the cloud shader's hash/value-noise/FBM chain into one packed texture.
    ///
    /// The critical constraint is periodicity. Sampling an arbitrary window from the old
    /// infinite field and marking it Repeat creates a seam. Worse, rotating later FBM
    /// octaves does not preserve a square lattice's period. Every octave below therefore
    /// owns a wrapped integer lattice and an integer orientation transform: crossing a U
    /// or V boundary moves by a whole number of that octave's lattice periods. Do not
    /// replace these with unconstrained floating-point rotations.
    ///
    /// Structure packing: R medium-scale density/billows, G/B independent domain-warp
    /// fields, and A wisp/cirrus structure. The generated PNG is the runtime fallback and
    /// may be regenerated at any time. Use Export Authored Cloud Structure Starter to make
    /// a separately named art asset before painting it; normal bakes never touch that copy.
    /// The weather map remains separate: R is regional coverage and G is cloud-type bias.
    /// </summary>
    [InitializeOnLoad]
    public static class SolCloudNoiseBaker
    {
        public const int NoiseTextureSize = 256;
        public const int WeatherMapSize = 256;
        public const int ShapeVolumeSize = 96;
        // Detail Worley uses 4/8/16 cells. At 32 voxels the mid and high channels resolved
        // to four and two voxels per cell, which is below their own Nyquist limit: they
        // carried aliasing rather than shape, so eroding against them added fizz instead of
        // lobes. 64 restores 16/8/4 voxels per cell for a one-megabyte asset.
        public const int DetailVolumeSize = 64;
        public const float ShaderNoiseTiling = 1f / 8f;
        public const string NoiseTexturePath =
            "Assets/Earth-Sky-Water/TimeOfDay/Resources/SolEnvironment/Sol_CloudNoisePacked.png";
        public const string WeatherMapPath =
            "Assets/Earth-Sky-Water/TimeOfDay/Resources/SolEnvironment/Sol_CloudWeatherMap.png";
        public const string ShapeVolumePath =
            "Assets/Earth-Sky-Water/TimeOfDay/Resources/SolEnvironment/Sol_CloudShapeVolume.asset";
        public const string DetailVolumePath =
            "Assets/Earth-Sky-Water/TimeOfDay/Resources/SolEnvironment/Sol_CloudDetailVolume.asset";

        readonly struct Octave
        {
            public readonly int Period;
            public readonly int M00;
            public readonly int M01;
            public readonly int M10;
            public readonly int M11;

            public Octave(int period, int m00, int m01, int m10, int m11)
            {
                Period = period;
                M00 = m00;
                M01 = m01;
                M10 = m10;
                M11 = m11;
            }
        }

        // Integer transforms approximate the old 1.94x / 19-degree octave rotation while
        // guaranteeing that both texture axes remain exact periods. Each octave has its
        // own lattice because sharing one after rotation is the classic seam regression.
        static readonly Octave[] CloudOctaves =
        {
            new(8,   1,  0, 0, 1),
            new(7,   2, -1, 1, 2),
            new(21,  1, -1, 1, 1),
            new(26,  1, -2, 2, 1),
            new(112, 0, -1, 1, 0),
        };

        static readonly Octave[] WeatherOctaves =
        {
            new(2, 1,  0, 0, 1),
            new(2, 2, -1, 1, 2),
            new(5, 1, -1, 1, 1),
            new(7, 1, -2, 2, 1),
        };

        static SolCloudNoiseBaker()
        {
            EditorApplication.delayCall += EnsureVolumeNoiseIsPresent;
        }

        [MenuItem("Tools/Sol Environment/Bake Cloud Noise Textures")]
        public static void Bake()
        {
            string directory = Path.GetDirectoryName(NoiseTexturePath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            WritePng(NoiseTexturePath, NoiseTextureSize, EvaluatePackedNoise);
            WritePng(WeatherMapPath, WeatherMapSize, EvaluateWeatherMap);
            BakeVolumeNoise();
            ConfigureImporter(NoiseTexturePath, NoiseTextureSize);
            ConfigureImporter(WeatherMapPath, WeatherMapSize);
            AssetDatabase.SaveAssets();
            Debug.Log("[SolCloudNoiseBaker] Baked periodic cloud noise and weather map.");
        }

        [MenuItem("Tools/Sol Environment/Export Authored Cloud Structure Starter...")]
        public static void ExportAuthoredStructureStarter()
        {
            if (!File.Exists(NoiseTexturePath))
            {
                Bake();
                if (!File.Exists(NoiseTexturePath))
                    throw new FileNotFoundException(
                        "The generated cloud structure fallback is missing.", NoiseTexturePath);
            }

            string targetPath = EditorUtility.SaveFilePanelInProject(
                "Export Authored Cloud Structure Starter",
                "Sol_CloudStructureAuthored", "png",
                "Save a separate RGBA structure map. R = billows, G/B = domain warp, "
                + "A = wisps/cirrus. Assign it to the cloud rendering profile after editing.");
            if (string.IsNullOrWhiteSpace(targetPath))
                return;

            // Never let this helper overwrite either the generated fallback or an existing
            // authored map. GenerateUniqueAssetPath also makes repeated exports harmless.
            targetPath = AssetDatabase.GenerateUniqueAssetPath(targetPath);
            FileUtil.CopyFileOrDirectory(NoiseTexturePath, targetPath);
            AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceSynchronousImport);
            ConfigureImporter(targetPath, NoiseTextureSize);
            AssetDatabase.SaveAssets();
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<Texture2D>(targetPath);
            Debug.Log($"[SolCloudNoiseBaker] Exported authored structure starter to {targetPath}. "
                + "Future cloud-noise bakes will not overwrite it.");
        }

        public static void BakeFromCommandLine() => Bake();

        // Existing projects need the new volume assets once, without requiring every
        // developer to discover and run the menu command before clouds render correctly.
        // A stale volume baked at a previous resolution counts as missing: the size
        // constants are what the shader's voxel-footprint maths and the tests both trust.
        static void EnsureVolumeNoiseIsPresent()
        {
            if (IsVolumeCurrent(ShapeVolumePath, ShapeVolumeSize)
                && IsVolumeCurrent(DetailVolumePath, DetailVolumeSize))
                return;
            BakeVolumeNoise();
            AssetDatabase.SaveAssets();
            Debug.Log("[SolCloudNoiseBaker] Generated missing or outdated 3D cloud noise assets.");
        }

        static bool IsVolumeCurrent(string path, int expectedSize)
        {
            Texture3D volume = AssetDatabase.LoadAssetAtPath<Texture3D>(path);
            return volume != null
                && volume.width == expectedSize
                && volume.height == expectedSize
                && volume.depth == expectedSize;
        }

        /// <summary>Reference evaluation used both by the bake and regression tests.</summary>
        public static Vector4 EvaluatePackedNoise(float u, float v)
        {
            return new Vector4(
                CloudFbm(u, v, 0),
                CloudFbm(u, v, 101),
                CloudFbm(u, v, 211),
                CloudFbm(u, v, 307));
        }

        /// <summary>Low-frequency starter geography; artists can paint over the PNG.</summary>
        public static Vector4 EvaluateWeatherMap(float u, float v)
        {
            float coverage = Fbm(u, v, 401, WeatherOctaves);
            float cloudType = Fbm(u, v, 557, WeatherOctaves);

            // Broader fronts with a few clear/storm pockets, all expressed through
            // periodic fields so the editable starting map has no privileged seam.
            coverage = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.18f, 0.82f, coverage));
            cloudType = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.22f, 0.78f, cloudType));
            return new Vector4(coverage, cloudType, 0.5f, 1f);
        }

        public static Color32 Quantize(Vector4 value)
        {
            return new Color32(
                Quantize(value.x), Quantize(value.y),
                Quantize(value.z), Quantize(value.w));
        }

        static void BakeVolumeNoise()
        {
            WriteTexture3D(ShapeVolumePath, ShapeVolumeSize, EvaluateShapeVolume);
            WriteTexture3D(DetailVolumePath, DetailVolumeSize, EvaluateDetailVolume);
        }

        static Vector4 EvaluateShapeVolume(float u, float v, float w)
        {
            // Four independent masses let the runtime vary cloud topology by date with
            // one RGBA volume lookup. Blending channels is substantially cheaper than a
            // second Texture3D sample and avoids returning to a repeating weather preset.
            return new Vector4(
                EvaluateShapeMass(u, v, w, 811, 0.34f),
                EvaluateShapeMass(u, v, w, 2719, 0.288f),
                EvaluateShapeMass(u, v, w, 4201, 0.301f),
                EvaluateShapeMass(u, v, w, 6151, 0.294f));
        }

        static float EvaluateShapeMass(float u, float v, float w, int seed, float remapStart)
        {
            float perlin = ValueFbm3D(u, v, w, seed);
            float low = InvertedWorley(u, v, w, 4, seed + 166);

            // Perlin provides broad connected masses; low-frequency Worley rounds them
            // into lobes instead of smoke columns. Keep substantial empty space so a
            // coverage value below one produces separate cloud bodies instead of a deck.
            float mass = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(remapStart, remapStart + 0.48f,
                    perlin * 0.68f + low * 0.32f));
            return mass;
        }

        static Vector4 EvaluateDetailVolume(float u, float v, float w)
        {
            float low = InvertedWorley(u, v, w, 4, 1871);
            float medium = InvertedWorley(u, v, w, 8, 2017);
            float high = InvertedWorley(u, v, w, 16, 2203);
            return new Vector4(low, medium, high, ValueFbm3D(u, v, w, 2381));
        }

        static float ValueFbm3D(float u, float v, float w, int seed)
        {
            float value = 0f;
            float amplitude = 0.5333333f;
            int period = 4;
            for (int octave = 0; octave < 4; octave++)
            {
                value += PeriodicValueNoise3D(u, v, w, period,
                    seed + octave * 131) * amplitude;
                amplitude *= 0.5f;
                period *= 2;
            }
            return Mathf.Clamp01(value);
        }

        static float PeriodicValueNoise3D(float u, float v, float w, int period, int seed)
        {
            float px = u * period;
            float py = v * period;
            float pz = w * period;
            int ix = Mathf.FloorToInt(px);
            int iy = Mathf.FloorToInt(py);
            int iz = Mathf.FloorToInt(pz);
            float fx = Smooth(px - Mathf.Floor(px));
            float fy = Smooth(py - Mathf.Floor(py));
            float fz = Smooth(pz - Mathf.Floor(pz));

            float x00 = Mathf.Lerp(Hash3(ix, iy, iz, period, seed),
                Hash3(ix + 1, iy, iz, period, seed), fx);
            float x10 = Mathf.Lerp(Hash3(ix, iy + 1, iz, period, seed),
                Hash3(ix + 1, iy + 1, iz, period, seed), fx);
            float x01 = Mathf.Lerp(Hash3(ix, iy, iz + 1, period, seed),
                Hash3(ix + 1, iy, iz + 1, period, seed), fx);
            float x11 = Mathf.Lerp(Hash3(ix, iy + 1, iz + 1, period, seed),
                Hash3(ix + 1, iy + 1, iz + 1, period, seed), fx);
            return Mathf.Lerp(Mathf.Lerp(x00, x10, fy), Mathf.Lerp(x01, x11, fy), fz);
        }

        static float InvertedWorley(float u, float v, float w, int cells, int seed)
        {
            float px = u * cells;
            float py = v * cells;
            float pz = w * cells;
            int ix = Mathf.FloorToInt(px);
            int iy = Mathf.FloorToInt(py);
            int iz = Mathf.FloorToInt(pz);
            float nearestSquared = 4f;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int cx = ix + dx;
                int cy = iy + dy;
                int cz = iz + dz;
                Vector3 feature = new(
                    cx + Hash3(cx, cy, cz, cells, seed),
                    cy + Hash3(cx, cy, cz, cells, seed + 17),
                    cz + Hash3(cx, cy, cz, cells, seed + 37));
                float distanceSquared = (feature - new Vector3(px, py, pz)).sqrMagnitude;
                nearestSquared = Mathf.Min(nearestSquared, distanceSquared);
            }
            return 1f - Mathf.Clamp01(Mathf.Sqrt(nearestSquared) * 0.82f);
        }

        static float Hash3(int x, int y, int z, int period, int seed)
        {
            unchecked
            {
                uint h = (uint)(Wrap(x, period) + seed * 1013);
                h ^= (uint)(Wrap(y, period) + seed * 1619) * 0x9e3779b9u;
                h ^= (uint)(Wrap(z, period) + seed * 3137) * 0x85ebca6bu;
                h ^= h >> 16;
                h *= 0x7feb352du;
                h ^= h >> 15;
                h *= 0x846ca68bu;
                h ^= h >> 16;
                return (h & 0x00ffffffu) * (1f / 0x00ffffffu);
            }
        }

        static float Smooth(float value) => value * value * (3f - 2f * value);

        delegate Vector4 VoxelEvaluator(float u, float v, float w);

        static void WriteTexture3D(string path, int size, VoxelEvaluator evaluate)
        {
            Texture3D generated = new(size, size, size, TextureFormat.RGBA32, true)
            {
                name = Path.GetFileNameWithoutExtension(path),
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 0,
            };
            Color32[] voxels = new Color32[size * size * size];
            for (int z = 0; z < size; z++)
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float v = (y + 0.5f) / size;
                float w = (z + 0.5f) / size;
                voxels[x + size * (y + size * z)] = Quantize(evaluate(u, v, w));
            }
            generated.SetPixels32(voxels);
            generated.Apply(true, true);

            Texture3D existing = AssetDatabase.LoadAssetAtPath<Texture3D>(path);
            if (existing == null)
                AssetDatabase.CreateAsset(generated, path);
            else
            {
                EditorUtility.CopySerialized(generated, existing);
                Object.DestroyImmediate(generated);
                EditorUtility.SetDirty(existing);
            }
        }

        static float CloudFbm(float u, float v, int seed)
            => Fbm(u, v, seed, CloudOctaves);

        static float Fbm(float u, float v, int seed, Octave[] octaves)
        {
            float result = 0f;
            float amplitude = 0.5f;
            for (int i = 0; i < octaves.Length; i++)
            {
                result += PeriodicValueNoise(u, v, seed + i * 37, octaves[i])
                        * amplitude;
                amplitude *= 0.5f;
            }
            return Mathf.Clamp01(result);
        }

        static float PeriodicValueNoise(float u, float v, int seed, in Octave octave)
        {
            float x = octave.Period * (octave.M00 * u + octave.M01 * v);
            float y = octave.Period * (octave.M10 * u + octave.M11 * v);
            int ix = Mathf.FloorToInt(x);
            int iy = Mathf.FloorToInt(y);
            float fx = x - Mathf.Floor(x);
            float fy = y - Mathf.Floor(y);
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);

            float r0 = HashTchou(Wrap(ix, octave.Period),
                Wrap(iy, octave.Period), seed);
            float r1 = HashTchou(Wrap(ix + 1, octave.Period),
                Wrap(iy, octave.Period), seed);
            float r2 = HashTchou(Wrap(ix, octave.Period),
                Wrap(iy + 1, octave.Period), seed);
            float r3 = HashTchou(Wrap(ix + 1, octave.Period),
                Wrap(iy + 1, octave.Period), seed);
            return Mathf.Lerp(Mathf.Lerp(r0, r1, fx), Mathf.Lerp(r2, r3, fx), fy);
        }

        // Exact C# port of Hash_Tchou_2_1_float from Core RP's Hashes.hlsl. Inputs are
        // already integer lattice coordinates, so HLSL's round/cast step is implicit.
        static float HashTchou(int x, int y, int seed)
        {
            unchecked
            {
                uint vx = (uint)(x + seed * 1013);
                uint vy = (uint)(y + seed * 1619);
                vy ^= 1103515245u;
                vx += vy;
                vx *= vy;
                vx ^= vx >> 5;
                vx *= 0x27d4eb2du;
                return (vx >> 8) * (1f / 0x00ffffff);
            }
        }

        static int Wrap(int value, int period)
        {
            int wrapped = value % period;
            return wrapped < 0 ? wrapped + period : wrapped;
        }

        static byte Quantize(float value)
            => (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 255f), 0, 255);

        delegate Vector4 PixelEvaluator(float u, float v);

        static void WritePng(string path, int size, PixelEvaluator evaluate)
        {
            Texture2D texture = new(size, size, TextureFormat.RGBA32, false, true);
            try
            {
                Color32[] pixels = new Color32[size * size];
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;
                    float v = (y + 0.5f) / size;
                    pixels[y * size + x] = Quantize(evaluate(u, v));
                }
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        static void ConfigureImporter(string path, int size)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
                throw new InvalidDataException($"Texture importer was not created for {path}.");

            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Bilinear;
            importer.anisoLevel = 1;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.crunchedCompression = false;
            importer.maxTextureSize = size;
            importer.isReadable = false;
            importer.SaveAndReimport();
        }
    }
}
