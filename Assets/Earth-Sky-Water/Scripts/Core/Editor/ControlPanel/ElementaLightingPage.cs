using System.Collections.Generic;
using Sol.Lighting;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Lighting and the render pipeline it runs inside: the resolved celestial frame, the
    /// manual quality tier and its budgets, the probe volumes, and which Sol renderer features
    /// are actually installed.
    ///
    /// The feature list matters more than it looks. Every one of these features fails silently
    /// when it is missing - the scene simply renders without atmosphere, or without clouds, or
    /// without water - so a list of what is installed is the fastest answer to "why does this
    /// scene look nothing like the other one".
    /// </summary>
    sealed class ElementaLightingPage : ElementaPanelPage
    {
        SerializedObject _serializedDirector;

        public override string Title => "Lighting";

        public override string Subtitle =>
            "Resolved celestial lighting, the manual quality tier and its budgets, probe "
            + "volumes, and the installed renderer features.";

        public override void Draw(ElementaPanelContext context)
        {
            DrawFrame(context);
            DrawTier(context);
            DrawProbes(context);
            DrawPipeline(context);
        }

        // -- Resolved frame ----------------------------------------------------------

        void DrawFrame(ElementaPanelContext context)
        {
            if (!context.Section("lighting/frame", "Resolved Frame"))
                return;

            SolLightingFrame frame = SolLightingDirector.ResolveFrame();
            ElementaPanelGui.Metric("Revision", frame.Revision.ToString());
            ElementaPanelGui.Metric("Dominant light", frame.Dominant.ToString());
            ElementaPanelGui.MetricBar("Day factor", frame.DayFactor);
            ElementaPanelGui.MetricBar("Weather attenuation", frame.WeatherAttenuation);
            ElementaPanelGui.MetricBar("Cloud shadow strength", frame.CloudShadowStrength);
            ElementaPanelGui.MetricBar("Eclipse", frame.Eclipse);
            ElementaPanelGui.MetricBar("Lightning", frame.Lightning);

            EditorGUILayout.Space(2f);
            DrawDirectional("Sun", frame.Sun);
            DrawDirectional("Moon", frame.Moon);

            EditorGUILayout.Space(2f);
            ElementaPanelGui.Metric("Ambient sky", Describe(frame.Ambient.Sky));
            ElementaPanelGui.Metric("Ambient equator", Describe(frame.Ambient.Equator));
            ElementaPanelGui.Metric("Ambient ground", Describe(frame.Ambient.Ground));
            ElementaPanelGui.Note(
                "The stable ambient is the same trilight without the per-frame weather and "
                + "lightning terms, which is what probe rebakes read so a flash cannot be "
                + "baked into indirect lighting.");
        }

        static void DrawDirectional(string label, in SolDirectionalLightState state)
        {
            ElementaPanelGui.Metric(label,
                state.Enabled
                    ? $"{state.Intensity:0.000} at elev "
                      + $"{ElementaPanelFormat.Elevation(state.Direction):0.0}°, shadow "
                      + $"{state.ShadowStrength:0.00}"
                    : "disabled");
        }

        static string Describe(Color color)
            => $"{color.r:0.000}, {color.g:0.000}, {color.b:0.000}";

        // -- Tier --------------------------------------------------------------------

        void DrawTier(ElementaPanelContext context)
        {
            if (!context.Section("lighting/tier", "Quality Tier"))
                return;

            SolLightingDirector director = context.Lighting;
            if (director == null)
            {
                EditorGUILayout.HelpBox(
                    "No SolLightingDirector is loaded, so light, shadow and probe budgets are "
                    + "not being enforced and no tier can be selected.",
                    MessageType.Info);
                return;
            }

            SerializedObject serialized = ResolveSerialized(director);
            serialized.Update();
            EditorGUI.BeginChangeCheck();
            SerializedProperty profile = serialized.FindProperty("qualityProfile");
            if (profile != null)
                EditorGUILayout.PropertyField(profile, new GUIContent("Quality profile"));
            if (EditorGUI.EndChangeCheck())
            {
                serialized.ApplyModifiedProperties();
                context.RefreshLayout();
            }

            ElementaPanelGui.Metric("Active tier", director.ActiveTier.ToString());
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Set tier", GUILayout.Width(60f));
                if (GUILayout.Button("Low"))
                    SetTier(context, SolLightingQualityTier.Low);
                if (GUILayout.Button("Medium"))
                    SetTier(context, SolLightingQualityTier.Medium);
                if (GUILayout.Button("High"))
                    SetTier(context, SolLightingQualityTier.High);
            }

            SolLightingQualitySettings settings = director.ActiveQualitySettings;
            ElementaPanelGui.Metric("Main shadow distance", $"{settings.MainShadowDistance:0} m");
            ElementaPanelGui.Metric("Shadow cascades", settings.MainShadowCascades.ToString());
            ElementaPanelGui.Metric("Main / punctual atlas",
                $"{settings.MainShadowAtlasResolution} / {settings.PunctualShadowAtlasResolution}");
            ElementaPanelGui.Metric("Managed light limit", settings.ManagedLightLimit.ToString());
            ElementaPanelGui.Metric("Shadow slice limit", settings.ShadowSliceLimit.ToString());
            ElementaPanelGui.Metric("Volumetric light limit",
                settings.LocalVolumetricLightLimit.ToString());
            ElementaPanelGui.Metric("Probe refresh interval",
                $"{settings.ReflectionProbeRefreshInterval:0.0} s");

            EditorGUILayout.Space(2f);
            ElementaPanelGui.Metric("Managed lights",
                $"{director.ManagedLightCount} / {settings.ManagedLightLimit}");
            ElementaPanelGui.Metric("Managed shadows", director.ManagedShadowCount.ToString());
            ElementaPanelGui.Metric("Shadow slices",
                $"{director.ManagedShadowSliceCount} / {settings.ShadowSliceLimit}");
            ElementaPanelGui.Metric("Volumetric lights",
                $"{director.ManagedVolumetricLightCount} / {settings.LocalVolumetricLightLimit}");
            ElementaPanelGui.Note(
                "The tier is a runtime selection and is never serialized, so comparing tiers "
                + "here cannot re-author the profile's default.");
        }

        static void SetTier(ElementaPanelContext context, SolLightingQualityTier tier)
        {
            context.Lighting.SetTier(tier);
            context.Refresh();
        }

        // -- Probes ------------------------------------------------------------------

        void DrawProbes(ElementaPanelContext context)
        {
            if (!context.Section("lighting/probes", "Probe Volumes", false))
                return;

            bool available = SolSkyLightingScheduler.IsApvDataAvailable();
            EditorGUILayout.HelpBox(
                available
                    ? "Adaptive Probe Volume data is loaded, so sky-driven indirect lighting is "
                      + "coming from the probe volume."
                    : "No Adaptive Probe Volume data is loaded, so indirect sky lighting falls "
                      + "back to ambient probes.",
                available ? MessageType.Info : MessageType.Warning);

            if (ElementaPanelGui.ActionButton("Configure Adaptive Probe Volumes",
                    "Set up the pipeline, the baking set and the scene volumes idempotently.",
                    true, 240f))
            {
                SolApvSetupUtility.ConfigureFromMenu();
                context.RefreshLayout();
            }

            ElementaPanelGui.Metric("Camera contexts", SolEnvironmentCameraRegistry.Count.ToString());
        }

        // -- Pipeline ----------------------------------------------------------------

        void DrawPipeline(ElementaPanelContext context)
        {
            if (!context.Section("lighting/pipeline", "Render Pipeline"))
                return;

            UniversalRenderPipelineAsset pipeline = ElementaPanelDoctor.ResolvePipeline();
            if (pipeline == null)
            {
                EditorGUILayout.HelpBox(
                    "The active render pipeline is not a Universal Render Pipeline asset, so "
                    + "none of Elementa's renderer features can run.",
                    MessageType.Error);
                return;
            }

            ElementaPanelGui.ObjectRow("Pipeline asset", pipeline);

            EditorGUI.BeginChangeCheck();
            bool depth = EditorGUILayout.ToggleLeft(
                new GUIContent("Camera depth texture",
                    "Atmosphere, clouds and the water prepass all read it."),
                pipeline.supportsCameraDepthTexture);
            bool opaque = EditorGUILayout.ToggleLeft(
                new GUIContent("Camera opaque texture",
                    "Water refraction and the underwater composition read it."),
                pipeline.supportsCameraOpaqueTexture);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(pipeline, "Change Elementa pipeline requirements");
                pipeline.supportsCameraDepthTexture = depth;
                pipeline.supportsCameraOpaqueTexture = opaque;
                EditorUtility.SetDirty(pipeline);
                AssetDatabase.SaveAssetIfDirty(pipeline);
                context.Refresh();
            }

            ElementaPanelGui.Metric("Shadow distance", $"{pipeline.shadowDistance:0} m");
            ElementaPanelGui.Metric("Shadow cascades", pipeline.shadowCascadeCount.ToString());
            ElementaPanelGui.Metric("Main light shadow atlas",
                pipeline.mainLightShadowmapResolution.ToString());

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Installed Sol features", EditorStyles.miniBoldLabel);
            DrawFeatureRow<SolAtmosphereRendererFeature>("Atmosphere", true);
            DrawFeatureRow<SolCloudRendererFeature>("Volumetric clouds", false);
            DrawFeatureRow<Sol.Water.Rendering.SolWaterRendererFeature>("Water", false);

            List<ScriptableRendererData> renderers = ElementaPanelDoctor.ResolveRendererData();
            for (int i = 0; i < renderers.Count; i++)
                ElementaPanelGui.ObjectRow($"Renderer {i}", renderers[i]);

            if (ElementaPanelGui.ActionButton("Install Atmosphere Feature",
                    "Add SolAtmosphereRendererFeature to the Sol renderer without touching any "
                    + "feature already on it.", true, 220f))
            {
                ElementaPanelDoctor.EnsureAtmosphereFeature();
                context.RefreshLayout();
            }
        }

        static void DrawFeatureRow<T>(string label, bool required)
            where T : ScriptableRendererFeature
        {
            T feature = ElementaPanelDoctor.ResolveFeature<T>();
            ElementaPanelGui.Metric(label, feature != null
                ? "installed"
                : required ? "MISSING (required)" : "not installed");
        }

        // -- Shared ------------------------------------------------------------------

        SerializedObject ResolveSerialized(SolLightingDirector director)
        {
            if (_serializedDirector == null || _serializedDirector.targetObject != director)
                _serializedDirector = new SerializedObject(director);
            return _serializedDirector;
        }
    }
}
