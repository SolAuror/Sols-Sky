using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Sol.Tests.Editor
{
    public sealed class SolForwardPlusWaterLightingTests
    {
        [Test]
        public void SharedHelper_ImplementsBothForwardPlusLoops()
        {
            string source = ReadAsset(
                "Earth-Sky-Water/Water/Shaders/SolForwardPlusWaterLighting.hlsl");

            StringAssert.Contains("#if USE_CLUSTER_LIGHT_LOOP", source);
            StringAssert.Contains("URP_FP_DIRECTIONAL_LIGHTS_COUNT", source);
            StringAssert.Contains("LIGHT_LOOP_BEGIN(pixelLightCount)", source);
            StringAssert.Contains("light.distanceAttenuation * light.shadowAttenuation", source);
        }

        [TestCase("Earth-Sky-Water/Water/Shaders/Sol.Water.shader")]
        [TestCase("Earth-Sky-Water/Shaders/Water2/SolOcean.shader")]
        public void WaterShaders_CompileAndUseTheClusteredSpecularHelper(string assetPath)
        {
            string source = ReadAsset(assetPath);
            StringAssert.Contains("#pragma multi_compile _ _CLUSTER_LIGHT_LOOP", source);
            StringAssert.Contains("SolForwardPlusWaterLighting.hlsl", source);
            StringAssert.Contains("SolWaterAdditionalSpecular(", source);

            string unityPath = "Assets/" + assetPath.Replace('\\', '/');
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(unityPath);
            Assert.IsNotNull(shader);
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader),
                $"{unityPath} has an import or shader compile error.");
        }

        [Test]
        public void LegacyWater_NoLongerAddsLocalLightDiffuse()
        {
            string source = ReadAsset(
                "Earth-Sky-Water/Water/Shaders/Sol.Water.shader");
            StringAssert.DoesNotContain("addNdotL * addAtten * shallowCol", source);
        }

        static string ReadAsset(string relativePath)
            => File.ReadAllText(Path.Combine(Application.dataPath, relativePath));
    }
}
