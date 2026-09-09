using System;
using System.Collections.Generic;
using Sol.Landscape;
using Sol.ToD;
using Sol.Water;
using Sol.Water.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Sol.Environment.EditorTools
{
    /// <summary>One thing that is wrong with the scene or project, and the fix for it.</summary>
    readonly struct ElementaIssue
    {
        public readonly MessageType Severity;
        public readonly string Message;
        public readonly string FixLabel;
        public readonly Action Fix;

        public ElementaIssue(
            MessageType severity, string message, string fixLabel = null, Action fix = null)
        {
            Severity = severity;
            Message = message;
            FixLabel = fixLabel;
            Fix = fix;
        }
    }

    /// <summary>
    /// The scene and project checks the control panel runs, with a repair for each one it can
    /// repair.
    ///
    /// Every check here corresponds to a way the stack can be silently half-configured:
    /// a renderer feature that was never installed, an opaque texture the water refraction
    /// needs, a landscape array that no longer matches its terrain, a debug view left on.
    /// None of those raise an error - they just render wrong - so the panel has to ask.
    /// </summary>
    static class ElementaPanelDoctor
    {
        public const string SystemManagerPrefabPath = "Assets/Prefabs/Sols System Manager.prefab";
        public const string SolRendererPath = "Assets/Settings/Sol_Renderer.asset";

        public static List<ElementaIssue> Collect(ElementaPanelContext context)
        {
            List<ElementaIssue> issues = new();
            CollectAuthorities(context, issues);
            CollectPipeline(context, issues);
            CollectAssets(context, issues);
            CollectDebugState(issues);
            return issues;
        }

        public static int Count(List<ElementaIssue> issues, MessageType severity)
        {
            int count = 0;
            for (int i = 0; i < issues.Count; i++)
                if (issues[i].Severity == severity)
                    count++;
            return count;
        }

        // -- Authorities -------------------------------------------------------------

        static void CollectAuthorities(ElementaPanelContext context, List<ElementaIssue> issues)
        {
            if (context.Time == null)
            {
                issues.Add(new ElementaIssue(MessageType.Error,
                    "No TimeOfDay authority is loaded. Nothing in Elementa has a clock to read.",
                    HasSystemManagerPrefab ? "Add System Manager" : null,
                    HasSystemManagerPrefab ? InstantiateSystemManager : null));
            }
            else if (context.TimeAuthorities.Length > 1)
            {
                issues.Add(new ElementaIssue(MessageType.Warning,
                    $"{context.TimeAuthorities.Length} TimeOfDay authorities are loaded across "
                    + "scenes. Confirm which one this panel should be editing."));
            }

            if (context.Weather == null)
            {
                issues.Add(new ElementaIssue(MessageType.Warning,
                    "No SolWeatherManager is loaded. Sky, fog, rain, wind and water fall back "
                    + "to the authored TimeOfDay values.",
                    HasSystemManagerPrefab ? "Add System Manager" : null,
                    HasSystemManagerPrefab ? InstantiateSystemManager : null));
            }
            else
            {
                CollectWeatherProfiles(context, issues);
                CollectMissingReferences(context, issues);
            }

            if (context.World == null)
            {
                issues.Add(new ElementaIssue(MessageType.Warning,
                    "No SolEnvironmentWorld is loaded, so wind lag, the wave clock and the sea "
                    + "state come from the fair-weather stand-in rather than the simulation.",
                    "Add Environment World",
                    () => AddAuthority<SolEnvironmentWorld>(context, "Sol Environment World")));
            }

            if (context.Coordinator == null)
            {
                issues.Add(new ElementaIssue(MessageType.Info,
                    "No SolEnvironmentCoordinator is loaded. RenderSettings and the skybox "
                    + "clone will not be restored when the scene closes.",
                    HasSystemManagerPrefab ? "Add System Manager" : null,
                    HasSystemManagerPrefab ? InstantiateSystemManager : null));
            }

            if (context.Time != null && context.Time.SkyProfile == null)
            {
                issues.Add(new ElementaIssue(MessageType.Warning,
                    "TimeOfDay has no sky profile, so the scene is running on the hidden inline "
                    + "compatibility values. Migrate it to author the sky as an asset.",
                    "Open Sky page",
                    () => context.Window.ShowPage("Sky")));
            }
        }

        static void CollectWeatherProfiles(ElementaPanelContext context, List<ElementaIssue> issues)
        {
            SolWeatherSelection[] profiles = context.Weather.profiles;
            if (profiles == null || profiles.Length == 0)
            {
                issues.Add(new ElementaIssue(MessageType.Warning,
                    "The weather manager has no profiles, so automatic selection has nothing "
                    + "to choose between.",
                    "Sync selection lists",
                    SyncSelectionLists));
                return;
            }

            int empty = 0;
            for (int i = 0; i < profiles.Length; i++)
                if (profiles[i] == null || profiles[i].profile == null)
                    empty++;

            if (empty > 0)
            {
                issues.Add(new ElementaIssue(MessageType.Warning,
                    $"{empty} of {profiles.Length} weather selection entries carry no profile "
                    + "asset. Those entries can be rolled and resolve to nothing.",
                    "Sync selection lists",
                    SyncSelectionLists));
            }

            float totalWeight = 0f;
            for (int i = 0; i < profiles.Length; i++)
                if (profiles[i] != null && profiles[i].profile != null)
                    totalWeight += Mathf.Max(0f, profiles[i].weight);

            if (context.Weather.autoCycle && totalWeight <= 0f)
            {
                issues.Add(new ElementaIssue(MessageType.Warning,
                    "Auto cycling is on but every profile weight is zero, so no weather can "
                    + "ever be selected."));
            }
        }

        /// <summary>
        /// References to objects that no longer exist.
        ///
        /// A null reference is a choice; a *missing* one is a deleted object the scene is still
        /// pointing at, and the two are indistinguishable from script - both read as null. The
        /// serialized instance id is what tells them apart, and it is the only way to catch a
        /// dependency that was quietly dropped when something was deleted.
        /// </summary>
        static void CollectMissingReferences(
            ElementaPanelContext context, List<ElementaIssue> issues)
        {
            SerializedObject serialized = new(context.Weather);
            AddIfMissing(issues, serialized, "todManager",
                "The weather manager's TimeOfDay reference points at a deleted object, so it "
                + "has no clock, season or world day to key its climate model off.");
            AddIfMissing(issues, serialized, "waterManager",
                "The weather manager's SolWaterManager reference points at a deleted object, "
                + "so Drive Wind and Drive Waves have nothing to write to.");
        }

        static void AddIfMissing(
            List<ElementaIssue> issues, SerializedObject serialized, string path, string message)
        {
            SerializedProperty property = serialized.FindProperty(path);
            if (property == null
                || property.propertyType != SerializedPropertyType.ObjectReference
                || property.objectReferenceValue != null
                || property.objectReferenceInstanceIDValue == 0)
                return;

            // Capture the target, not the SerializedObject: the repair runs on a later frame,
            // by which point this one is stale.
            UnityEngine.Object target = serialized.targetObject;
            issues.Add(new ElementaIssue(MessageType.Warning, message,
                "Clear the reference",
                () =>
                {
                    SerializedObject repair = new(target);
                    SerializedProperty reference = repair.FindProperty(path);
                    if (reference == null)
                        return;
                    reference.objectReferenceValue = null;
                    repair.ApplyModifiedProperties();
                }));
        }

        // -- Render pipeline ---------------------------------------------------------

        static void CollectPipeline(ElementaPanelContext context, List<ElementaIssue> issues)
        {
            UniversalRenderPipelineAsset pipeline = ResolvePipeline();
            if (pipeline == null)
            {
                issues.Add(new ElementaIssue(MessageType.Error,
                    "The active render pipeline is not a Universal Render Pipeline asset. "
                    + "Elementa's atmosphere, cloud and water features cannot run."));
                return;
            }

            if (!pipeline.supportsCameraDepthTexture)
            {
                issues.Add(new ElementaIssue(MessageType.Error,
                    $"{pipeline.name} has the camera depth texture disabled. Atmosphere, "
                    + "clouds and the water prepass all read it.",
                    "Enable depth texture",
                    () => SetPipelineFlag(pipeline, depth: true)));
            }

            if (!pipeline.supportsCameraOpaqueTexture)
            {
                issues.Add(new ElementaIssue(MessageType.Warning,
                    $"{pipeline.name} has the camera opaque texture disabled. Water "
                    + "refraction and the underwater composition read it.",
                    "Enable opaque texture",
                    () => SetPipelineFlag(pipeline, opaque: true)));
            }

            if (ResolveFeature<SolAtmosphereRendererFeature>() == null)
            {
                issues.Add(new ElementaIssue(MessageType.Error,
                    "No SolAtmosphereRendererFeature is installed on any active renderer, so "
                    + "height fog and aerial scattering never render.",
                    "Install feature",
                    EnsureAtmosphereFeature));
            }

            if (ResolveFeature<SolCloudRendererFeature>() == null)
            {
                issues.Add(new ElementaIssue(MessageType.Warning,
                    "No SolCloudRendererFeature is installed, so the volumetric cloud deck "
                    + "does not render and the sky shows its authored baseline only."));
            }

            if (context.Water != null && ResolveFeature<SolWaterRendererFeature>() == null)
            {
                issues.Add(new ElementaIssue(MessageType.Error,
                    "A SolWaterWorld is loaded but no SolWaterRendererFeature is installed, so "
                    + "no water surface will be drawn."));
            }
        }

        // -- Authored assets ---------------------------------------------------------

        static void CollectAssets(ElementaPanelContext context, List<ElementaIssue> issues)
        {
            SolSkyProfile sky = context.Time != null ? context.Time.SkyProfile : null;
            if (sky != null && sky.stellarBackdrop == null)
            {
                issues.Add(new ElementaIssue(MessageType.Info,
                    $"{sky.name} has no stellar backdrop cubemap, so the night sky falls back "
                    + "to the procedural star grid.",
                    "Bake backdrop",
                    SolStellarBackdropBaker.BakeDefault));
            }

            if (context.Water != null)
            {
                if (context.Water.DefaultProfile == null)
                {
                    issues.Add(new ElementaIssue(MessageType.Warning,
                        "The water world has no default profile, so any body without its own "
                        + "profile has no authored optics, spectrum or shoreline."));
                }

                if (context.Water.QualityProfile == null)
                {
                    issues.Add(new ElementaIssue(MessageType.Info,
                        "The water world has no quality profile. Built-in defaults are used, "
                        + "which no scene can tune."));
                }

                CollectWaterBodies(context, issues);
            }

            if (context.Landscape != null)
            {
                SolLandscapeConfig config = context.Landscape.config;
                if (config == null)
                {
                    issues.Add(new ElementaIssue(MessageType.Warning,
                        "The landscape driver has no config, so no layer arrays or blend "
                        + "parameters reach the terrain shader."));
                }
                else
                {
                    Sol.Landscape.Editor.SolLandscapeStaleness staleness =
                        Sol.Landscape.Editor.SolLandscapeArrayBaker.GetStaleness(config);
                    if (staleness.IsStale)
                    {
                        issues.Add(new ElementaIssue(MessageType.Warning,
                            $"Landscape layer arrays are stale: {staleness.Message}",
                            "Bake layer arrays",
                            () => BakeLandscape(config)));
                    }
                }

                if (!context.Landscape.TryValidateContract(out string refusal))
                {
                    issues.Add(new ElementaIssue(MessageType.Warning,
                        $"The landscape auto-material contract is not satisfied: {refusal}"));
                }
            }

            if (context.Lighting != null && context.Lighting.QualityProfile == null)
            {
                issues.Add(new ElementaIssue(MessageType.Info,
                    "The lighting director has no quality profile, so shadow, light and probe "
                    + "budgets are not being enforced from an asset."));
            }

            if (!SolSkyLightingScheduler.IsApvDataAvailable())
            {
                issues.Add(new ElementaIssue(MessageType.Info,
                    "No Adaptive Probe Volume data is loaded, so indirect sky lighting falls "
                    + "back to ambient probes.",
                    "Configure APV",
                    SolApvSetupUtility.ConfigureFromMenu));
            }
        }

        static void CollectWaterBodies(ElementaPanelContext context, List<ElementaIssue> issues)
        {
            IReadOnlyList<SolWaterBody> bodies = context.Water.Bodies;
            if (bodies == null || bodies.Count == 0)
            {
                issues.Add(new ElementaIssue(MessageType.Info,
                    "The water world has no registered bodies. Nothing marks any surface as "
                    + "water yet."));
                return;
            }

            int oceans = 0;
            for (int i = 0; i < bodies.Count; i++)
            {
                SolWaterBody body = bodies[i];
                if (body == null)
                    continue;
                if (body.BodyType == SolWaterBodyType.Ocean)
                    oceans++;
                if (!body.Id.IsValid)
                {
                    issues.Add(new ElementaIssue(MessageType.Warning,
                        $"Water body \"{body.name}\" has no stable id, so saves and streamed "
                        + "cells cannot address it."));
                }
            }

            if (oceans > 1)
            {
                issues.Add(new ElementaIssue(MessageType.Warning,
                    $"{oceans} bodies are typed Ocean. Only one infinite clipmap surface can "
                    + "win a query, so the others are unreachable."));
            }
        }

        // -- Debug state left on -----------------------------------------------------

        static void CollectDebugState(List<ElementaIssue> issues)
        {
            SolWaterRendererFeature water = ResolveFeature<SolWaterRendererFeature>();
            if (water != null)
            {
                SerializedObject serialized = new(water);
                SerializedProperty mode = serialized.FindProperty("debugMode");
                if (mode != null && mode.enumValueIndex != (int)SolWaterDebugMode.Disabled)
                {
                    issues.Add(new ElementaIssue(MessageType.Warning,
                        $"The water renderer feature is showing the "
                        + $"{(SolWaterDebugMode)mode.enumValueIndex} debug view instead of the "
                        + "final image.",
                        "Reset to final",
                        () => SetEnum(water, "debugMode", (int)SolWaterDebugMode.Disabled)));
                }
            }

            SolCloudRendererFeature clouds = ResolveFeature<SolCloudRendererFeature>();
            if (clouds == null)
                return;

            SerializedObject cloudSerialized = new(clouds);
            SolCloudRenderingProfile profile = cloudSerialized
                .FindProperty("profile")?.objectReferenceValue as SolCloudRenderingProfile;
            if (profile == null)
            {
                issues.Add(new ElementaIssue(MessageType.Warning,
                    "The cloud renderer feature has no rendering profile, so the cloud deck "
                    + "runs on built-in fallbacks nothing can tune."));
                return;
            }

            if (profile.debugView != SolCloudDebugView.FinalLighting)
            {
                issues.Add(new ElementaIssue(MessageType.Warning,
                    $"{profile.name} is showing the {profile.debugView} debug view instead of "
                    + "the final image.",
                    "Reset to final",
                    () =>
                    {
                        Undo.RecordObject(profile, "Reset Elementa cloud debug view");
                        profile.debugView = SolCloudDebugView.FinalLighting;
                        EditorUtility.SetDirty(profile);
                    }));
            }
        }

        // -- Shared resolution ------------------------------------------------------

        public static bool HasSystemManagerPrefab
            => AssetDatabase.LoadAssetAtPath<GameObject>(SystemManagerPrefabPath) != null;

        public static UniversalRenderPipelineAsset ResolvePipeline()
            => GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

        public static List<ScriptableRendererData> ResolveRendererData()
        {
            List<ScriptableRendererData> result = new();
            UniversalRenderPipelineAsset pipeline = ResolvePipeline();
            if (pipeline != null)
            {
                foreach (ScriptableRendererData data in pipeline.rendererDataList)
                    if (data != null)
                        result.Add(data);
            }

            if (result.Count != 0)
                return result;

            // A scene can be opened before the quality level that references the Sol renderer
            // is active. The panel still has to be able to report on the feature list.
            ScriptableRendererData fallback =
                AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(SolRendererPath);
            if (fallback != null)
                result.Add(fallback);
            return result;
        }

        public static T ResolveFeature<T>() where T : ScriptableRendererFeature
        {
            List<ScriptableRendererData> renderers = ResolveRendererData();
            for (int i = 0; i < renderers.Count; i++)
            {
                List<ScriptableRendererFeature> features = renderers[i].rendererFeatures;
                if (features == null)
                    continue;
                for (int f = 0; f < features.Count; f++)
                    if (features[f] is T match)
                        return match;
            }

            return null;
        }

        // -- Repairs ----------------------------------------------------------------

        public static void InstantiateSystemManager()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SystemManagerPrefabPath);
            if (prefab == null)
            {
                Debug.LogWarning(
                    $"[Elementa] No system manager prefab at {SystemManagerPrefabPath}.");
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(instance, "Add Elementa system manager");
            Selection.activeGameObject = instance;
            EditorGUIUtility.PingObject(instance);
        }

        static void AddAuthority<T>(ElementaPanelContext context, string newObjectName)
            where T : Component
        {
            GameObject host = context.ResolveAuthorityRoot();
            if (host == null)
            {
                host = new GameObject(newObjectName);
                Undo.RegisterCreatedObjectUndo(host, $"Add {newObjectName}");
            }

            if (host.GetComponent<T>() == null)
                Undo.AddComponent<T>(host);
            Selection.activeGameObject = host;
            context.Resolve(force: true);
        }

        static void SetPipelineFlag(
            UniversalRenderPipelineAsset pipeline, bool depth = false, bool opaque = false)
        {
            Undo.RecordObject(pipeline, "Change Elementa pipeline requirements");
            if (depth)
                pipeline.supportsCameraDepthTexture = true;
            if (opaque)
                pipeline.supportsCameraOpaqueTexture = true;
            EditorUtility.SetDirty(pipeline);
            AssetDatabase.SaveAssetIfDirty(pipeline);
        }

        static void SetEnum(UnityEngine.Object target, string propertyName, int value)
        {
            SerializedObject serialized = new(target);
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null)
                return;
            property.enumValueIndex = value;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
        }

        public static void EnsureAtmosphereFeature()
        {
            try
            {
                SolEnvironmentSetupUtility.EnsureAtmosphereRendererFeature();
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Elementa] Atmosphere feature install failed: {exception.Message}");
            }
        }

        public static void SyncSelectionLists()
        {
            int changed = SolWeatherSelectionSync.Sync(out string report);
            Debug.Log($"[Elementa] Weather selection sync touched {changed} container(s).\n{report}");
            AssetDatabase.Refresh();
        }

        public static void BakeLandscape(SolLandscapeConfig config)
        {
            try
            {
                SolLandscapeConfig baked =
                    Sol.Landscape.Editor.SolLandscapeArrayBaker.BakeTarget(config);
                Selection.activeObject = baked;
                EditorGUIUtility.PingObject(baked);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Elementa] Landscape bake failed: {exception.Message}");
            }
        }
    }
}
