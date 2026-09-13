using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Sol.Environment.EditorTools;
using Sol.Water;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Sol.Tests.Editor
{
    /// <summary>
    /// Whole-project integrity, as opposed to the behaviour of any one system.
    ///
    /// These catch the damage a structural change does rather than a logic change: an
    /// #include left pointing at a moved file, a material orphaned by a deleted shader, a
    /// component whose script no longer exists, a renderer feature that was never
    /// reinstalled. None of it raises an error at edit time - the project simply renders
    /// wrong, or renders nothing - and none of it is visible to a test that exercises one
    /// class.
    /// </summary>
    public sealed class SolProjectIntegrityTests
    {
        const string DemoScenePath = "Assets/Scenes/Elementa_Demo.unity";

        static readonly string[] RequiredRendererFeatures =
        {
            "SolAtmosphereRendererFeature",
            "SolCloudRendererFeature",
            "SolWaterRendererFeature",
        };

        /// <summary>
        /// Retired with the Water 1 stack. Installing one again would composite underwater
        /// twice, on top of what SolWaterRendererFeature already does.
        /// </summary>
        static readonly string[] RetiredRendererFeatures =
        {
            "UnderwaterRendererFeature",
        };

        /// <summary>
        /// Every shader in the project compiles.
        ///
        /// This is the only check that actually runs HLSL through the URP includes, so it is
        /// what catches a broken or moved #include. A shader with an error still loads and
        /// still has a name; it just draws magenta at runtime.
        /// </summary>
        [Test]
        public void EveryShader_CompilesWithoutErrors()
        {
            List<string> broken = new();
            foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null)
                {
                    broken.Add($"{path} (failed to load)");
                    continue;
                }

                if (ShaderUtil.ShaderHasError(shader))
                    broken.Add(path);
            }

            Assert.IsEmpty(broken,
                "Shaders with compile errors:\n  " + string.Join("\n  ", broken));
        }

        [Test]
        public void EveryComputeShader_CompilesWithoutErrors()
        {
            List<string> broken = new();
            foreach (string guid in AssetDatabase.FindAssets(
                         "t:ComputeShader", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                ComputeShader compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (compute == null)
                {
                    broken.Add($"{path} (failed to load)");
                    continue;
                }

                if (ShaderUtil.GetComputeShaderMessageCount(compute) <= 0)
                    continue;

                foreach (ShaderMessage message in ShaderUtil.GetComputeShaderMessages(compute))
                    if (message.severity == ShaderCompilerMessageSeverity.Error)
                        broken.Add($"{path}: {message.message}");
            }

            Assert.IsEmpty(broken,
                "Compute shaders with errors:\n  " + string.Join("\n  ", broken));
        }

        /// <summary>
        /// Unity keeps deleted shaders in Graphics Settings as unresolved GUIDs. They do
        /// not produce a C# compile error, but leave Missing entries in Always Included
        /// Shaders and can conceal build-time shader stripping mistakes.
        /// </summary>
        [Test]
        public void AlwaysIncludedShaders_AllResolve()
        {
            const string path = "ProjectSettings/GraphicsSettings.asset";
            string yaml = File.ReadAllText(path);
            int start = yaml.IndexOf("  m_AlwaysIncludedShaders:",
                System.StringComparison.Ordinal);
            int end = yaml.IndexOf("  m_PreloadedShaders:", start,
                System.StringComparison.Ordinal);

            Assert.That(start, Is.GreaterThanOrEqualTo(0),
                $"{path} has no Always Included Shaders section.");
            Assert.That(end, Is.GreaterThan(start),
                $"{path} has no section after Always Included Shaders.");

            string section = yaml.Substring(start, end - start);
            List<string> missing = new();
            foreach (Match match in Regex.Matches(section, @"guid: ([0-9a-f]{32})"))
            {
                string guid = match.Groups[1].Value;
                if (guid == "0000000000000000f000000000000000")
                    continue;

                if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                    missing.Add(guid);
            }

            Assert.IsEmpty(missing,
                "Always Included Shaders contains missing GUIDs:\n  "
                + string.Join("\n  ", missing));
        }

        /// <summary>
        /// A material whose shader was deleted silently falls back to the internal error
        /// shader, which is magenta at runtime and reads as a lighting bug in the editor.
        /// </summary>
        [Test]
        public void EveryMaterial_ResolvesItsShader()
        {
            List<string> broken = new();
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    broken.Add($"{path} (failed to load)");
                    continue;
                }

                if (material.shader == null
                    || material.shader.name == "Hidden/InternalErrorShader")
                    broken.Add(path);
            }

            Assert.IsEmpty(broken,
                "Materials with a missing or errored shader:\n  " + string.Join("\n  ", broken));
        }

        /// <summary>
        /// A deleted script leaves its component behind as an empty slot that reads as null.
        /// Nothing warns about it outside the inspector, and the object silently stops doing
        /// whatever it was there to do.
        /// </summary>
        [Test]
        public void EnvironmentScenes_HaveNoMissingScripts()
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            List<string> missing = new();
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                foreach (string path in SolApvSetupUtility.EnvironmentScenePaths)
                {
                    Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    foreach (GameObject root in scene.GetRootGameObjects())
                        CollectMissingScripts(root, path, missing);
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

            Assert.IsEmpty(missing,
                "Missing scripts in environment scenes:\n  " + string.Join("\n  ", missing));
        }

        /// <summary>
        /// The shipping demo is the canonical Water 2 composition. Loading it must produce
        /// one enabled water authority, a registered ocean and all of the services needed
        /// by rendering, queries, wetness and shoreline generation.
        /// </summary>
        [Test]
        public void ElementaDemo_HasACompleteWater2Authority()
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.OpenScene(DemoScenePath, OpenSceneMode.Single);
                SolWaterWorld world = Object.FindFirstObjectByType<SolWaterWorld>(
                    FindObjectsInactive.Include);

                Assert.IsNotNull(world, $"{DemoScenePath} has no SolWaterWorld.");
                Assert.IsTrue(world.isActiveAndEnabled,
                    "The demo's SolWaterWorld is disabled.");
                Assert.IsNotNull(world.DefaultProfile,
                    "The demo's SolWaterWorld has no default water profile.");
                Assert.IsNotNull(world.QualityProfile,
                    "The demo's SolWaterWorld has no quality profile.");
                Assert.IsNotNull(world.QueryService,
                    "The demo's SolWaterWorld has no query service.");
                Assert.IsNotNull(world.GetComponent<SolWaterWetness>(),
                    "The demo's SolWaterWorld has no wetness authority.");
                Assert.IsNotNull(world.GetComponent<SolTerrainShoreline>(),
                    "The demo's SolWaterWorld has no terrain shoreline source.");
                Assert.IsTrue(world.TryGetOcean(out SolWaterBody ocean),
                    "The demo has no registered Water 2 ocean.");
                Assert.IsNotNull(ocean.Profile,
                    "The demo ocean cannot resolve a Water 2 profile.");
                Assert.IsTrue(world.TrySampleApproximate(Vector3.zero,
                        out SolWaterSurfaceSample sample),
                    "The demo's Water 2 authority cannot answer an ocean surface query.");
                Assert.IsTrue(sample.HasWater,
                    "The demo's ocean surface query returned a no-water sample.");
                Assert.That(sample.Normal.sqrMagnitude, Is.GreaterThan(0.99f),
                    "The demo's ocean surface query returned an invalid normal.");
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

        [Test]
        public void EveryPrefab_HasNoMissingScripts()
        {
            List<string> missing = new();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                {
                    missing.Add($"{path} (failed to load)");
                    continue;
                }

                CollectMissingScripts(prefab, path, missing);
            }

            Assert.IsEmpty(missing,
                "Missing scripts in prefabs:\n  " + string.Join("\n  ", missing));
        }

        /// <summary>
        /// Every Sol renderer feature fails silently when it is absent - the scene just
        /// renders without atmosphere, or without clouds, or without any water surface at
        /// all - so the installed list is worth asserting rather than eyeballing.
        /// </summary>
        [Test]
        public void Renderer_CarriesTheRequiredFeaturesAndNoRetiredOnes()
        {
            ScriptableRendererData data =
                AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(
                    ElementaPanelDoctor.SolRendererPath);
            Assert.IsNotNull(data,
                $"Sol renderer data was not found at {ElementaPanelDoctor.SolRendererPath}.");

            List<string> installed = new();
            for (int i = 0; i < data.rendererFeatures.Count; i++)
            {
                ScriptableRendererFeature feature = data.rendererFeatures[i];
                Assert.IsNotNull(feature,
                    $"Renderer feature slot {i} is null, which means a missing script.");
                installed.Add(feature.GetType().Name);
            }

            foreach (string required in RequiredRendererFeatures)
            {
                Assert.Contains(required, installed,
                    $"{required} is not installed; the system it drives will not render.");
                ScriptableRendererFeature feature = data.rendererFeatures.First(item =>
                    item != null && item.GetType().Name == required);
                Assert.IsTrue(feature.isActive,
                    $"{required} is installed but disabled; the system it drives will not render.");
            }

            foreach (string retired in RetiredRendererFeatures)
                Assert.IsFalse(installed.Contains(retired),
                    $"{retired} was retired with the Water 1 stack but is installed again.");
        }

        static void CollectMissingScripts(GameObject root, string path, List<string> missing)
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                Component[] components = transform.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                    if (components[i] == null)
                        missing.Add($"{path}: '{transform.name}' slot {i}");
            }
        }
    }
}
