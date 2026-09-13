using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Sol.Environment.EditorTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Sol.Tests.Editor
{
    /// <summary>
    /// The Elementa control panel is meant to be the authoritative authoring surface: an
    /// author should not need the inspector to reach a scene-wide setting.
    ///
    /// Curation cannot hold that on its own. A manager added to the demo scene tomorrow is
    /// reachable only from the inspector until someone remembers to surface it, and nothing
    /// says when that has happened. So the criterion here is the demo scene itself rather
    /// than a hand-kept list: whatever authority the scene actually uses has to appear in
    /// the panel.
    /// </summary>
    public sealed class SolControlPanelCoverageTests
    {
        const string DemoScene = "Assets/Scenes/Elementa_Demo.unity";
        const string PanelDirectory = "Assets/Earth-Sky-Water/Scripts/Core/Editor";

        /// <summary>
        /// Authored on the object they affect rather than owned by the scene. A water body
        /// or a celestial light belongs on its own transform, so the panel lists these
        /// rather than editing them.
        /// </summary>
        static readonly HashSet<string> PerObjectComponents = new()
        {
            "CelestialBody", "SolWaterBody", "SolWaterBuoyancy", "SolWaterInteractionZone",
            "SolWaterInteractor", "SolLakeGeometry", "SolRiverGeometry",
            "SolWaterfallGeometry", "SolFloodGeometry", "SolEnvironmentLight",
            "SolVolumetricLight", "SolReflectionProbeAnchor", "SolAtmosphereDensityVolume",
            "SolAtmosphereExclusionVolume", "SolTransparentAtmosphereBinding",
            "SolOriginShiftRoot",
        };

        /// <summary>Demo scaffolding, not part of the environment stack.</summary>
        static readonly HashSet<string> DemoComponents = new()
        {
            "DemoCameraController", "DemoMenuController", "DemoTimeControls",
        };

        /// <summary>
        /// The retired Water 1 stack. These must not come back into the panel: the panel
        /// offering to author a superseded system would invite edits that change nothing.
        /// </summary>
        static readonly string[] RetiredWater1 =
        {
            "SolWaterManager", "WaterRippleManager", "WaterRippleSource",
            "UnderwaterVolumeController", "UnderwaterRendererFeature", "WaterVolume",
            "WaterTileGrid", "WaterBuoyancy", "WaterSystemAdapter",
        };

        [Test]
        public void ControlPanel_ExposesEveryManagerialComponentTheDemoSceneUses()
        {
            string panel = ReadPanelSource();
            List<string> missing = new();

            foreach (string name in ResolveSceneComponentTypes())
            {
                if (PerObjectComponents.Contains(name) || DemoComponents.Contains(name))
                    continue;
                if (!HasAuthoredFields(name))
                    continue;
                if (!MentionsType(panel, name))
                    missing.Add(name);
            }

            Assert.IsEmpty(missing,
                "These scene authorities are only reachable from the inspector:\n  "
                + string.Join("\n  ", missing)
                + "\nAdd a ComponentSection for each on the page that owns it.");
        }

        /// <summary>
        /// Renderer features are installed on the renderer asset rather than the scene, so
        /// the scene sweep above cannot see them, and each one carries authored settings.
        /// </summary>
        [Test]
        public void ControlPanel_ExposesEveryInstalledSolRendererFeature()
        {
            string panel = ReadPanelSource();
            ScriptableRendererData data =
                AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(
                    ElementaPanelDoctor.SolRendererPath);
            Assert.IsNotNull(data);

            List<string> missing = new();
            foreach (ScriptableRendererFeature feature in data.rendererFeatures)
            {
                if (feature == null)
                    continue;
                string name = feature.GetType().Name;
                if (!name.StartsWith("Sol", StringComparison.Ordinal))
                    continue;
                if (!MentionsType(panel, name))
                    missing.Add(name);
            }

            Assert.IsEmpty(missing,
                "Installed Sol renderer features the panel cannot author:\n  "
                + string.Join("\n  ", missing));
        }

        [Test]
        public void ControlPanel_DoesNotReferenceTheRetiredWaterStack()
        {
            string panel = ReadPanelSource();
            List<string> leaked = RetiredWater1.Where(name => MentionsType(panel, name)).ToList();

            Assert.IsEmpty(leaked,
                "The control panel references retired Water 1 types:\n  "
                + string.Join("\n  ", leaked));
        }

        // -- Helpers -----------------------------------------------------------------

        static string ReadPanelSource()
        {
            string[] files = Directory.GetFiles(
                PanelDirectory, "*.cs", SearchOption.AllDirectories);
            Assert.IsNotEmpty(files, $"No control panel source found under {PanelDirectory}.");
            return string.Concat(files.Select(File.ReadAllText));
        }

        /// <summary>
        /// Reads the component types the demo scene actually instantiates. Loading the scene
        /// rather than parsing its YAML means nested prefabs resolve for free, and the names
        /// are the runtime types rather than file names.
        /// </summary>
        static SortedSet<string> ResolveSceneComponentTypes()
        {
            SortedSet<string> names = new(StringComparer.Ordinal);
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Scene scene = EditorSceneManager.OpenScene(DemoScene, OpenSceneMode.Additive);
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Component component in
                             root.GetComponentsInChildren<Component>(true))
                    {
                        if (component == null)
                            continue;
                        Type type = component.GetType();
                        // Only this project's own components; Unity's built-ins and package
                        // components are not ours to surface.
                        if (type.Namespace != null
                            && (type.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal)
                                || type.Namespace.StartsWith("UnityEditor", StringComparison.Ordinal)
                                || type.Namespace.StartsWith("TMPro", StringComparison.Ordinal)))
                            continue;
                        if (type.Assembly.GetName().Name != "Assembly-CSharp")
                            continue;
                        names.Add(type.Name);
                    }
                }

                EditorSceneManager.CloseScene(scene, true);
            }
            finally
            {
                if (previousSetup.Any(item => item.isLoaded && item.isActive))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                else
                    EditorSceneManager.NewScene(
                        NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }

            return names;
        }

        /// <summary>
        /// A component with nothing serialized has nothing for the panel to author, so it is
        /// not a coverage gap. Resolved from the type rather than the source file, because
        /// a public field is as authored as an explicit [SerializeField] one.
        /// </summary>
        static bool HasAuthoredFields(string typeName)
        {
            Type type = TypeCache.GetTypesDerivedFrom<Component>()
                .FirstOrDefault(candidate => candidate.Name == typeName);
            if (type == null)
                return false;

            return type.GetFields(System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.DeclaredOnly)
                .Any(field => field.IsPublic
                    || field.GetCustomAttributes(typeof(SerializeField), false).Length > 0);
        }

        static bool MentionsType(string source, string typeName)
            => System.Text.RegularExpressions.Regex.IsMatch(
                source, @"\b" + System.Text.RegularExpressions.Regex.Escape(typeName) + @"\b");
    }
}
