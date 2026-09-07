using System.Linq;
using NUnit.Framework;
using Sol.Environment.EditorTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Sol.Tests.Editor
{
    public sealed class SolApvConfigurationTests
    {
        [Test]
        public void UrpUsesStreamingAdaptiveProbeVolumesWithoutScenarios()
        {
            UniversalRenderPipelineAsset pipeline =
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(
                    SolApvSetupUtility.PipelinePath);
            Assert.IsNotNull(pipeline);
            Assert.AreEqual(LightProbeSystem.ProbeVolumes, pipeline.lightProbeSystem);

            SerializedObject serialized = new(pipeline);
            Assert.IsTrue(serialized.FindProperty(
                "m_SupportProbeVolumeGPUStreaming").boolValue);
            Assert.IsTrue(serialized.FindProperty(
                "m_SupportProbeVolumeDiskStreaming").boolValue);
            Assert.IsFalse(serialized.FindProperty(
                "m_SupportProbeVolumeScenarios").boolValue);
            Assert.IsFalse(serialized.FindProperty(
                "m_SupportProbeVolumeScenarioBlending").boolValue);
        }

        [Test]
        public void BakingSetUsesDynamicSkyOcclusionAndOneDefaultScenario()
        {
            ProbeVolumeBakingSet bakingSet = LoadBakingSet();

            Assert.IsTrue(bakingSet.skyOcclusion);
            Assert.IsTrue(bakingSet.skyOcclusionShadingDirection);
            Assert.AreEqual(2048, bakingSet.skyOcclusionBakingSamples);
            Assert.AreEqual(2, bakingSet.skyOcclusionBakingBounces);
            Assert.AreEqual(0.6f, bakingSet.skyOcclusionAverageAlbedo, 1e-5f);
            Assert.AreEqual(3, bakingSet.simplificationLevels);
            Assert.AreEqual(2f, bakingSet.minDistanceBetweenProbes, 1e-5f);
            CollectionAssert.AreEqual(new[] { "Default" }, bakingSet.lightingScenarios,
                "Day/night APV scenarios would duplicate the runtime sky authority.");
        }

        [Test]
        public void EveryEnvironmentSceneBelongsToTheSharedBakingSet()
        {
            ProbeVolumeBakingSet bakingSet = LoadBakingSet();
            string[] expected = SolApvSetupUtility.EnvironmentScenePaths
                .Select(AssetDatabase.AssetPathToGUID)
                .ToArray();

            CollectionAssert.AreEquivalent(expected, bakingSet.sceneGUIDs);
        }

        [Test]
        public void EnvironmentScenesHaveAutoFitVolumesAndRealtimeLights()
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                foreach (string path in SolApvSetupUtility.EnvironmentScenePaths)
                {
                    Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    ProbeVolume[] volumes = ComponentsInScene<ProbeVolume>(scene);
                    ProbeVolumePerSceneData[] sceneData =
                        ComponentsInScene<ProbeVolumePerSceneData>(scene);
                    Light[] lights = ComponentsInScene<Light>(scene);

                    Assert.AreEqual(1, volumes.Length,
                        $"{path} must have one APV placement volume.");
                    Assert.AreEqual(ProbeVolume.Mode.Scene, volumes[0].mode,
                        $"{path} must auto-fit its own GI contributors.");
                    Assert.Greater(volumes[0].size.sqrMagnitude, 100f,
                        $"{path} APV volume was not fitted to scene geometry.");
                    Assert.AreEqual(1, sceneData.Length,
                        $"{path} is missing Core RP's per-scene APV data host.");
                    Assert.IsTrue(lights.All(light =>
                            light.lightmapBakeType == LightmapBakeType.Realtime),
                        $"{path} contains a light that would be baked into APV data.");

                    EditorSceneManager.CloseScene(scene, true);
                }
            }
            finally
            {
                if (previousSetup.Any(item => item.isLoaded && item.isActive))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                else
                    EditorSceneManager.NewScene(
                        NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static ProbeVolumeBakingSet LoadBakingSet()
        {
            ProbeVolumeBakingSet bakingSet =
                AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(
                    SolApvSetupUtility.BakingSetPath);
            Assert.IsNotNull(bakingSet,
                $"APV baking set was not found at {SolApvSetupUtility.BakingSetPath}.");
            return bakingSet;
        }

        static T[] ComponentsInScene<T>(Scene scene) where T : Component
            => scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<T>(true))
                .ToArray();
    }
}
