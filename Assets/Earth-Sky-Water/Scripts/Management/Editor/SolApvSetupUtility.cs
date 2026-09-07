using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Idempotent authoring for the environment Adaptive Probe Volume baking set.
    /// This configures placement and scene membership; the lighting bake remains an
    /// explicit authoring operation because its cost depends on final static geometry.
    /// </summary>
    public static class SolApvSetupUtility
    {
        public const string BakingSetPath = "Assets/Settings/SolEnvironmentAPV.asset";
        public const string PipelinePath = "Assets/Settings/URP_Sol.asset";
        public const string VolumeName = "Sol APV Scene Volume";

        public static readonly string[] EnvironmentScenePaths =
        {
            "Assets/Scenes/Sols_Water2_Demo.unity",
            "Assets/Scenes/Sols_Lights.unity",
            "Assets/Scenes/Sc_Sols_FiniteBodies.unity",
        };

        [MenuItem("Tools/Sol Environment/Configure Adaptive Probe Volumes")]
        public static void ConfigureFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            Configure();
        }

        // Public entry point for CI and one-time project migration.
        public static void ConfigureFromCommandLine() => Configure();

        static void Configure()
        {
            ConfigurePipeline();
            ProbeVolumeBakingSet bakingSet = LoadOrCreateBakingSet();
            ConfigureBakingSet(bakingSet);
            ConfigureScenes();
            AssetDatabase.SaveAssets();
            Debug.Log(
                "[SolApvSetup] Configured the shared sky-occlusion baking set and " +
                $"{EnvironmentScenePaths.Length} environment scenes. Run Bake Probe Volumes " +
                "after finalizing static geometry.");
        }

        static void ConfigurePipeline()
        {
            UniversalRenderPipelineAsset pipeline =
                AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
                throw new InvalidOperationException($"URP asset was not found at {PipelinePath}.");

            SerializedObject serialized = new(pipeline);
            serialized.FindProperty("m_LightProbeSystem").intValue =
                (int)LightProbeSystem.ProbeVolumes;
            serialized.FindProperty("m_SupportProbeVolumeGPUStreaming").boolValue = true;
            serialized.FindProperty("m_SupportProbeVolumeDiskStreaming").boolValue = true;
            serialized.FindProperty("m_SupportProbeVolumeScenarios").boolValue = false;
            serialized.FindProperty("m_SupportProbeVolumeScenarioBlending").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pipeline);
        }

        static ProbeVolumeBakingSet LoadOrCreateBakingSet()
        {
            ProbeVolumeBakingSet bakingSet =
                AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(BakingSetPath);
            if (bakingSet != null)
                return bakingSet;

            bakingSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            bakingSet.name = "Sol Environment APV";
            AssetDatabase.CreateAsset(bakingSet, BakingSetPath);
            return bakingSet;
        }

        static void ConfigureBakingSet(ProbeVolumeBakingSet bakingSet)
        {
            bakingSet.probeOffset = Vector3.zero;
            bakingSet.simplificationLevels = 3;
            bakingSet.minDistanceBetweenProbes = 2f;
            bakingSet.renderersLayerMask = -1;
            bakingSet.minRendererVolumeSize = 0.1f;
            bakingSet.skyOcclusion = true;
            bakingSet.skyOcclusionBakingSamples = 2048;
            bakingSet.skyOcclusionBakingBounces = 2;
            bakingSet.skyOcclusionAverageAlbedo = 0.6f;
            bakingSet.skyOcclusionBackFaceCulling = false;
            bakingSet.skyOcclusionShadingDirection = true;

            SerializedObject serialized = new(bakingSet);
            serialized.FindProperty("singleSceneMode").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            foreach (string scenario in bakingSet.lightingScenarios.ToArray())
            {
                if (!string.Equals(scenario, "Default", StringComparison.Ordinal))
                    bakingSet.RemoveScenario(scenario);
            }
            if (!bakingSet.lightingScenarios.Contains("Default"))
                bakingSet.TryAddScenario("Default");

            foreach (string scenePath in EnvironmentScenePaths)
            {
                string guid = AssetDatabase.AssetPathToGUID(scenePath);
                if (string.IsNullOrEmpty(guid))
                    throw new InvalidOperationException($"Environment scene was not found at {scenePath}.");
                if (bakingSet.sceneGUIDs.Contains(guid))
                    continue;
                if (!bakingSet.TryAddScene(guid))
                {
                    throw new InvalidOperationException(
                        $"{scenePath} already belongs to a different APV baking set.");
                }
            }

            bakingSet.SetAllSceneBaking(true);
            EditorUtility.SetDirty(bakingSet);
        }

        static void ConfigureScenes()
        {
            MethodInfo updateSceneBounds = typeof(ProbeVolumeBakingSet).GetMethod(
                "UpdateSceneBounds", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo ensurePerSceneData = typeof(ProbeVolumeBakingSet).GetMethod(
                "EnsurePerSceneData", BindingFlags.Instance | BindingFlags.NonPublic);
            if (updateSceneBounds == null || ensurePerSceneData == null)
                throw new MissingMethodException(
                    "The installed render pipeline no longer exposes the APV scene setup hooks.");

            foreach (string scenePath in EnvironmentScenePaths)
            {
                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                ProbeVolume volume = FindSceneVolume(scene);
                if (volume == null)
                {
                    GameObject host = new(VolumeName);
                    SceneManager.MoveGameObjectToScene(host, scene);
                    volume = host.AddComponent<ProbeVolume>();
                }

                volume.gameObject.name = VolumeName;
                volume.mode = ProbeVolume.Mode.Scene;
                volume.objectLayerMask = -1;
                volume.overrideRendererFilters = false;
                volume.fillEmptySpaces = false;
                EditorUtility.SetDirty(volume);

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Light light in root.GetComponentsInChildren<Light>(true))
                    {
                        if (light.lightmapBakeType == LightmapBakeType.Realtime)
                            continue;
                        light.lightmapBakeType = LightmapBakeType.Realtime;
                        EditorUtility.SetDirty(light);
                    }
                }

                // These are the same Core RP hooks used by its scene-save callback. Invoke
                // them explicitly so command-line migrations also auto-fit the volume and
                // create the hidden per-scene APV data host before the first bake.
                ProbeVolumeBakingSet sceneSet =
                    AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(BakingSetPath);
                string sceneGuid = AssetDatabase.AssetPathToGUID(scenePath);
                updateSceneBounds.Invoke(sceneSet, new object[] { scene, sceneGuid, false });
                ensurePerSceneData.Invoke(sceneSet, new object[] { scene, sceneGuid });

                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene))
                    throw new InvalidOperationException($"Failed to save APV setup in {scenePath}.");
                AssetDatabase.SaveAssetIfDirty(sceneSet);
            }
        }

        static ProbeVolume FindSceneVolume(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                ProbeVolume[] volumes = root.GetComponentsInChildren<ProbeVolume>(true);
                for (int i = 0; i < volumes.Length; i++)
                {
                    if (volumes[i].gameObject.name == VolumeName)
                        return volumes[i];
                }
            }
            return null;
        }
    }
}
