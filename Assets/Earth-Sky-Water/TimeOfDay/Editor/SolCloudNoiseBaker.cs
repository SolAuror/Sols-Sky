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
    /// Packing: R base shape, G erosion, B/A two independent warp fields. The weather map
    /// is intentionally a separate, artist-editable low-frequency asset: R is regional
    /// coverage and G is cloud-type bias.
    /// </summary>
    public static class SolCloudNoiseBaker
    {
        public const int NoiseTextureSize = 256;
        public const int WeatherMapSize = 256;
        public const float ShaderNoiseTiling = 1f / 8f;
        public const string NoiseTexturePath =
            "Assets/Earth-Sky-Water/TimeOfDay/Resources/SolEnvironment/Sol_CloudNoisePacked.png";
        public const string WeatherMapPath =
            "Assets/Earth-Sky-Water/TimeOfDay/Resources/SolEnvironment/Sol_CloudWeatherMap.png";

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

        [MenuItem("Tools/Sol Environment/Bake Cloud Noise Textures")]
        public static void Bake()
        {
            string directory = Path.GetDirectoryName(NoiseTexturePath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            WritePng(NoiseTexturePath, NoiseTextureSize, EvaluatePackedNoise);
            WritePng(WeatherMapPath, WeatherMapSize, EvaluateWeatherMap);
            ConfigureImporter(NoiseTexturePath, NoiseTextureSize);
            ConfigureImporter(WeatherMapPath, WeatherMapSize);
            AssetDatabase.SaveAssets();
            Debug.Log("[SolCloudNoiseBaker] Baked periodic cloud noise and weather map.");
        }

        public static void BakeFromCommandLine() => Bake();

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
