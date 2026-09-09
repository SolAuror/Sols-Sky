using System.Collections.Generic;
using Sol.Water;
using Sol.Water.Rendering;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Water: the scene's bodies, the profile that gives them their look, and the quality
    /// policy that decides how much of it is actually simulated.
    ///
    /// Sea state lives here rather than with the wind that causes it, because the developed
    /// sea is on a twenty-minute lag - the slowest response anywhere in the stack. An author
    /// who changes the wind and looks at the water immediately will conclude the water is
    /// broken unless something says it is still building.
    /// </summary>
    sealed class ElementaWaterPage : ElementaPanelPage
    {
        SerializedObject _serializedWorld;
        SerializedObject _serializedFeature;
        SerializedObject _serializedProfile;
        SerializedObject _serializedQuality;

        public override string Title => "Water";

        public override string Subtitle =>
            "Registered bodies, the shared water profile, and the quality policy for the "
            + "spectrum, reflections and volumetrics.";

        public override void Draw(ElementaPanelContext context)
        {
            DrawSeaState(context);

            if (context.Water == null)
            {
                EditorGUILayout.HelpBox(
                    "No SolWaterWorld is loaded. Add one to the scene to author water bodies, "
                    + "or use the Overview page's health check.",
                    MessageType.Info);
                return;
            }

            DrawWorld(context);
            DrawBodies(context);
            DrawFeature(context);
            DrawProfileBody(context);
            DrawQualityBody(context);
        }

        // -- Sea state ---------------------------------------------------------------

        void DrawSeaState(ElementaPanelContext context)
        {
            if (!context.Section("water/sea", "Sea State"))
                return;

            SolEnvironmentState environment = context.Environment;
            SolEnvironmentWindState wind = environment.Wind;

            ElementaPanelGui.Metric("Wind", ElementaPanelFormat.Wind(wind.Speed));
            ElementaPanelGui.Metric("Developed sea",
                $"{wind.SeaStateSpeed:0.0} m/s equivalent (20 min lag)");
            ElementaPanelGui.Metric("Sea state", ElementaPanelFormat.SeaState(wind));
            ElementaPanelGui.MetricBar("Turbulence", wind.Turbulence);
            ElementaPanelGui.Metric("Wave speed",
                $"{environment.Weather.WaveSpeedMultiplier:0.00}x");
            ElementaPanelGui.Metric("Wave clock", $"{environment.WaveSeconds:0.0} s");
            ElementaPanelGui.Note(
                "The wave clock integrates world delta against the wave-speed multiplier, so a "
                + "weather change cannot shift wave phase by the whole elapsed session.");
        }

        // -- World -------------------------------------------------------------------

        void DrawWorld(ElementaPanelContext context)
        {
            if (!context.Section("water/world", "Water World"))
                return;

            SolWaterWorld water = context.Water;
            SerializedObject serialized = ResolveSerialized(ref _serializedWorld, water);
            serialized.Update();
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(
                serialized.FindProperty("defaultProfile"), new GUIContent("Default profile"));
            EditorGUILayout.PropertyField(
                serialized.FindProperty("qualityProfile"), new GUIContent("Quality profile"));
            if (EditorGUI.EndChangeCheck())
            {
                serialized.ApplyModifiedProperties();
                context.RefreshLayout();
            }

            SolWaterQualityProfile quality = water.QualityProfile;
            if (quality != null)
            {
                ElementaPanelGui.Metric("Authored tier", quality.tier.ToString());
                ElementaPanelGui.Metric("Active tier", quality.ActiveTier.ToString());
                if (quality.ActiveTier != quality.tier)
                    ElementaPanelGui.Note(
                        "The lighting director is overriding the water tier without writing to "
                        + "the asset, so the authored value is still what ships.");
                if (quality.ActiveTier == SolWaterQualityTier.Low)
                    EditorGUILayout.HelpBox(
                        "Low disables the spectrum entirely: the surface is driven from the "
                        + "profile's authored Gerstner waves instead of the FFT.",
                        MessageType.Info);
            }

            ElementaPanelGui.ObjectRow("Query service", water.QueryService as Object);
            if (water.TryGetOcean(out SolWaterBody ocean))
                ElementaPanelGui.ObjectRow("Ocean", ocean);
        }

        // -- Bodies ------------------------------------------------------------------

        void DrawBodies(ElementaPanelContext context)
        {
            IReadOnlyList<SolWaterBody> bodies = context.Water.Bodies;
            int count = bodies != null ? bodies.Count : 0;

            if (!context.Section("water/bodies", $"Bodies ({count})"))
                return;

            if (count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No bodies are registered. A SolWaterBody component is what marks any "
                    + "object as water.",
                    MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Body", EditorStyles.miniBoldLabel, GUILayout.Width(130f));
                EditorGUILayout.LabelField("Type", EditorStyles.miniBoldLabel, GUILayout.Width(80f));
                EditorGUILayout.LabelField("Level", EditorStyles.miniBoldLabel, GUILayout.Width(60f));
                EditorGUILayout.LabelField("Pri", EditorStyles.miniBoldLabel, GUILayout.Width(34f));
                EditorGUILayout.LabelField("Profile", EditorStyles.miniBoldLabel, GUILayout.Width(120f));
                GUILayout.FlexibleSpace();
            }

            for (int i = 0; i < count; i++)
            {
                SolWaterBody body = bodies[i];
                if (body == null)
                    continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(body.name, EditorStyles.miniButton, GUILayout.Width(130f)))
                    {
                        Selection.activeGameObject = body.gameObject;
                        EditorGUIUtility.PingObject(body.gameObject);
                    }

                    EditorGUILayout.LabelField(body.BodyType.ToString(), GUILayout.Width(80f));
                    EditorGUILayout.LabelField($"{body.SurfaceLevel:0.00}", GUILayout.Width(60f));
                    EditorGUILayout.LabelField(body.Priority.ToString(), GUILayout.Width(34f));
                    SolWaterProfile profile = body.Profile;
                    EditorGUILayout.LabelField(
                        profile != null ? profile.name : "(none)", GUILayout.Width(120f));
                    if (body.EnablePlanarReflection)
                        EditorGUILayout.LabelField("planar", EditorStyles.miniLabel,
                            GUILayout.Width(46f));
                    GUILayout.FlexibleSpace();
                }
            }

            ElementaPanelGui.Note(
                "Priority is the tie-break where bodies overlap: the highest priority "
                + "containing a query position wins, and the ocean is always the last resort.");
        }

        // -- Renderer feature --------------------------------------------------------

        void DrawFeature(ElementaPanelContext context)
        {
            SolWaterRendererFeature feature =
                ElementaPanelDoctor.ResolveFeature<SolWaterRendererFeature>();

            if (!context.Section("water/feature", "Renderer Feature"))
                return;

            if (feature == null)
            {
                EditorGUILayout.HelpBox(
                    "No SolWaterRendererFeature is installed on any active renderer, so no "
                    + "water surface is drawn at all.",
                    MessageType.Error);
                return;
            }

            SerializedObject serialized = ResolveSerialized(ref _serializedFeature, feature);
            serialized.Update();
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(
                serialized.FindProperty("renderInSceneView"),
                new GUIContent("Render in Scene View",
                    "Turn off to author terrain or geometry under the surface without the "
                    + "water in the way."));
            EditorGUILayout.PropertyField(
                serialized.FindProperty("debugMode"), new GUIContent("Debug view"));
            EditorGUILayout.PropertyField(
                serialized.FindProperty("debugLog"), new GUIContent("Log water decisions"));
            if (EditorGUI.EndChangeCheck())
            {
                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(feature);
                context.RefreshLayout();
            }

            SerializedProperty mode = serialized.FindProperty("debugMode");
            if (mode != null && mode.enumValueIndex != (int)SolWaterDebugMode.Disabled)
                EditorGUILayout.HelpBox(
                    "A debug view is replacing the water surface on every body. Never ship "
                    + "with this enabled.",
                    MessageType.Warning);
        }

        // -- Profile bodies ----------------------------------------------------------

        void DrawProfileBody(ElementaPanelContext context)
        {
            SolWaterProfile profile = context.Water.DefaultProfile;
            if (profile == null)
                return;

            ElementaPanelGui.Rule();
            EditorGUILayout.LabelField(
                $"Water profile  {profile.name}", EditorStyles.boldLabel);
            ElementaPanelGui.Note(
                "Shoreline data is rebuilt from the live terrain heightmaps whenever either "
                + "side moves, so the terrain fields below are inputs to that build rather "
                + "than a baked result.");

            if (ElementaPanelGui.DrawGroupedProperties(
                    context, "water/profile",
                    ResolveSerialized(ref _serializedProfile, profile),
                    ref context.State.waterFilter))
            {
                EditorUtility.SetDirty(profile);
                context.Refresh();
            }
        }

        void DrawQualityBody(ElementaPanelContext context)
        {
            SolWaterQualityProfile quality = context.Water.QualityProfile;
            if (quality == null)
                return;

            ElementaPanelGui.Rule();
            EditorGUILayout.LabelField(
                $"Quality profile  {quality.name}", EditorStyles.boldLabel);

            if (ElementaPanelGui.DrawGroupedProperties(
                    context, "water/quality",
                    ResolveSerialized(ref _serializedQuality, quality),
                    ref context.State.waterQualityFilter))
            {
                EditorUtility.SetDirty(quality);
                context.Refresh();
            }
        }

        // -- Shared ------------------------------------------------------------------

        static SerializedObject ResolveSerialized(ref SerializedObject cache, Object target)
        {
            if (cache == null || cache.targetObject != target)
                cache = new SerializedObject(target);
            return cache;
        }
    }
}
