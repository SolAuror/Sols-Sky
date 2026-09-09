using System;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    [InitializeOnLoad]
    static class SolSkyDefaultAssetBootstrap
    {
        const string SessionKey = "Elementa.DefaultStellarBackdrop.Checked.v2";

        static SolSkyDefaultAssetBootstrap()
        {
            if (!Application.isBatchMode)
                EditorApplication.delayCall += EnsureDefaultBackdrop;
        }

        static void EnsureDefaultBackdrop()
        {
            if (SessionState.GetBool(SessionKey, false)
                || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            SessionState.SetBool(SessionKey, true);
            Cubemap backdrop = AssetDatabase.LoadAssetAtPath<Cubemap>(
                SolStellarBackdropBaker.DefaultAssetPath);
            if (backdrop == null || !SolStellarBackdropBaker.IsCurrentDefaultBake())
                backdrop = SolStellarBackdropBaker.Bake(
                    SolStellarBackdropBaker.DefaultAssetPath,
                    SolStellarBackdropBaker.DefaultFaceSize,
                    SolStellarBackdropBaker.DefaultSeed);
            Assign("Assets/Earth-Sky-Water/Sky Profiles/Sol_Sky_Grounded.asset", backdrop);
            Assign("Assets/Earth-Sky-Water/Sky Profiles/Sol_Sky_Legacy.asset", backdrop);
            AssetDatabase.SaveAssets();
        }

        static void Assign(string path, Cubemap backdrop)
        {
            Sol.ToD.SolSkyProfile profile =
                AssetDatabase.LoadAssetAtPath<Sol.ToD.SolSkyProfile>(path);
            if (profile == null || profile.stellarBackdrop == backdrop)
                return;
            SerializedObject serialized = new(profile);
            serialized.FindProperty("stellarBackdrop").objectReferenceValue = backdrop;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
        }
    }

    /// <summary>
    /// Direction-space baker for the packed stellar cubemap consumed by Sol/Skybox.
    /// Every mip re-evaluates the same continuous domain and takes nearby maximum
    /// brightness samples, retaining isolated stars without face-space streaking.
    /// </summary>
    public static class SolStellarBackdropBaker
    {
        public const int DefaultFaceSize = 1024;
        public const uint DefaultSeed = 0x51A7C0DEu;
        public const string DefaultAssetPath =
            "Assets/Earth-Sky-Water/TimeOfDay/Textures/Sol_StellarBackdrop.asset";
        const string BakeVersion = "SolStellarBackdrop:v2";

        internal static bool IsCurrentDefaultBake()
            => AssetImporter.GetAtPath(DefaultAssetPath)?.userData == BakeVersion;

        [MenuItem("Tools/Elementa/Bake Stellar Backdrop")]
        public static void BakeDefault()
        {
            Bake(DefaultAssetPath, DefaultFaceSize, DefaultSeed);
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<Cubemap>(DefaultAssetPath);
        }

        public static Cubemap Bake(string assetPath, int faceSize, uint seed)
        {
            Cubemap cubemap = BuildCubemap(faceSize, seed);
            cubemap.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            Cubemap existing = AssetDatabase.LoadAssetAtPath<Cubemap>(assetPath);
            if (existing == null)
                AssetDatabase.CreateAsset(cubemap, assetPath);
            else
            {
                EditorUtility.CopySerialized(cubemap, existing);
                UnityEngine.Object.DestroyImmediate(cubemap);
                cubemap = existing;
                EditorUtility.SetDirty(cubemap);
            }
            AssetDatabase.SaveAssets();
            AssetImporter importer = AssetImporter.GetAtPath(assetPath);
            importer.userData = BakeVersion;
            importer.SaveAndReimport();
            return cubemap;
        }

        internal static Cubemap BuildCubemap(int faceSize, uint seed)
        {
            if (faceSize < 4 || (faceSize & (faceSize - 1)) != 0)
                throw new ArgumentException("Face size must be a power of two and at least four.", nameof(faceSize));

            Cubemap cubemap = new(faceSize, TextureFormat.RGBAHalf, true)
            {
                name = "Sol Stellar Backdrop",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 0,
            };

            int mipCount = Mathf.FloorToInt(Mathf.Log(faceSize, 2f)) + 1;
            for (int mip = 0; mip < mipCount; mip++)
            {
                int size = Mathf.Max(1, faceSize >> mip);
                for (int face = 0; face < 6; face++)
                {
                    Color[] pixels = new Color[size * size];
                    for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        Vector3 direction = Direction((CubemapFace)face,
                            (x + 0.5f) / size, (y + 0.5f) / size);
                        pixels[y * size + x] = Evaluate(direction, seed, mip, size);
                    }
                    cubemap.SetPixels(pixels, (CubemapFace)face, mip);
                }
            }
            cubemap.Apply(false, true);

            return cubemap;
        }

        public static Hash128 ContentChecksum(int faceSize = 64, uint seed = DefaultSeed)
        {
            Hash128 hash = new();
            for (int face = 0; face < 6; face++)
            for (int y = 0; y < faceSize; y++)
            for (int x = 0; x < faceSize; x++)
            {
                Vector3 direction = Direction((CubemapFace)face,
                    (x + 0.5f) / faceSize, (y + 0.5f) / faceSize);
                Color value = Evaluate(direction, seed, 0, faceSize);
                hash.Append(BitConverter.GetBytes(value.r));
                hash.Append(BitConverter.GetBytes(value.g));
                hash.Append(BitConverter.GetBytes(value.b));
                hash.Append(BitConverter.GetBytes(value.a));
            }
            return hash;
        }

        internal static Color Evaluate(Vector3 direction, uint seed, int mip, int size)
        {
            direction.Normalize();
            float footprint = 1f / Mathf.Max(1, size);
            float brightness = StarBrightness(direction, seed);
            if (mip > 0)
            {
                Vector3 tangent = Vector3.Cross(Mathf.Abs(direction.y) < 0.9f
                    ? Vector3.up : Vector3.right, direction).normalized;
                Vector3 bitangent = Vector3.Cross(direction, tangent);
                float radius = footprint * 1.25f;
                brightness = Mathf.Max(brightness, StarBrightness((direction + tangent * radius).normalized, seed));
                brightness = Mathf.Max(brightness, StarBrightness((direction - tangent * radius).normalized, seed));
                brightness = Mathf.Max(brightness, StarBrightness((direction + bitangent * radius).normalized, seed));
                brightness = Mathf.Max(brightness, StarBrightness((direction - bitangent * radius).normalized, seed));
            }

            float temperature = Hash(direction * 811.7f, seed ^ 0xA341316Cu);
            float phase = Hash(direction * 391.3f, seed ^ 0xC8013EA4u);
            Vector3 galacticNormal = new Vector3(0.20f, 0.35f, 0.91f).normalized;
            float distance = Vector3.Dot(direction, galacticNormal);
            float density = Mathf.Exp(-distance * distance * 18f);
            density *= Mathf.Lerp(0.45f, 1f,
                Hash(direction * 23.1f, seed ^ 0xAD90777Du));
            return new Color(brightness, temperature, phase, density);
        }

        static float StarBrightness(Vector3 direction, uint seed)
        {
            float noise = Hash(direction * 4093.1f, seed);
            float star = Mathf.Pow(noise, 620f);
            float magnitude = Mathf.Lerp(0.18f, 1f,
                Hash(direction * 1301.7f, seed ^ 0x7E95761Eu));
            return Mathf.Clamp01(star * magnitude);
        }

        static float Hash(Vector3 value, uint seed)
        {
            float seedFloat = (seed & 0xffffu) * 0.0001220703125f;
            float n = Mathf.Sin(Vector3.Dot(value,
                new Vector3(12.9898f, 78.233f, 37.719f)) + seedFloat) * 43758.5453f;
            return n - Mathf.Floor(n);
        }

        internal static Vector3 Direction(CubemapFace face, float u, float v)
        {
            float x = u * 2f - 1f;
            // Cubemap.SetPixels rows sample downward in the cube-face basis. A
            // Texture2D-style upward Y mirrors each face independently, so even a
            // continuous directional field acquires square seams when sampled.
            float y = 1f - v * 2f;
            return face switch
            {
                CubemapFace.PositiveX => new Vector3(1f, y, -x).normalized,
                CubemapFace.NegativeX => new Vector3(-1f, y, x).normalized,
                CubemapFace.PositiveY => new Vector3(x, 1f, -y).normalized,
                CubemapFace.NegativeY => new Vector3(x, -1f, y).normalized,
                CubemapFace.PositiveZ => new Vector3(x, y, 1f).normalized,
                _ => new Vector3(-x, y, -1f).normalized,
            };
        }
    }
}
