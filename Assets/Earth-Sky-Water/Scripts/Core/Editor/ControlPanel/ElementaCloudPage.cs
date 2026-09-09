using Sol.ToD;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// The volumetric cloud deck: its rendering profile, its quality budget, and the noise
    /// textures it samples.
    ///
    /// Cloud shape arrives from three places at once - the sky profile's authored baseline,
    /// the weather profile's formation override, and this rendering profile's scales and step
    /// counts - and only the third is a renderer feature setting rather than scene state.
    /// This page is where that split is visible.
    /// </summary>
    sealed class ElementaCloudPage : ElementaPanelPage
    {
        SerializedObject _serializedFeature;
        SerializedObject _serializedProfile;

        public override string Title => "Clouds";

        public override string Subtitle =>
            "Cloud rendering profile, quality budget, advection and the baked noise the deck "
            + "samples. Shape itself is authored on the sky and weather profiles.";

        public override void Draw(ElementaPanelContext context)
        {
            SolCloudRendererFeature feature =
                ElementaPanelDoctor.ResolveFeature<SolCloudRendererFeature>();

            DrawLiveState(context);
            DrawQuality(context);
            DrawFeature(context, feature);
            DrawBakers(context);
            DrawProfileBody(context, feature);
        }

        // -- Live state --------------------------------------------------------------

        void DrawLiveState(ElementaPanelContext context)
        {
            if (!context.Section("clouds/state", "Resolved Deck"))
                return;

            SolCloudController controller = SolCloudController.Active;
            SolCloudState state = controller.CurrentState;

            ElementaPanelGui.Metric("Dominant formation", state.DominantFormation.ToString());
            ElementaPanelGui.MetricBar("Coverage", state.Coverage);
            ElementaPanelGui.MetricBar("Erosion", state.Erosion);
            ElementaPanelGui.Metric("Density", $"{state.Density:0.00}");
            ElementaPanelGui.Metric("Base / thickness",
                $"{state.BaseHeight:0} m  ·  {state.Thickness:0} m");
            ElementaPanelGui.MetricBar("Vertical development", state.VerticalDevelopment);
            ElementaPanelGui.MetricBar("Anvil", state.AnvilAmount);
            ElementaPanelGui.MetricBar("Cirrus", state.CirrusAmount);
            ElementaPanelGui.MetricBar("Edge softness", state.EdgeSoftness);
            ElementaPanelGui.MetricBar("Base softness", state.BaseSoftness);
            ElementaPanelGui.MetricBar("Shadow strength", state.ShadowStrength);

            EditorGUILayout.Space(2f);
            Vector4 blend = controller.FormationWeights;
            ElementaPanelGui.MetricBar("Cumulus", blend.x);
            ElementaPanelGui.MetricBar("Stratus", blend.y);
            ElementaPanelGui.MetricBar("Nimbostratus", blend.z);
            ElementaPanelGui.MetricBar("Cumulonimbus", blend.w);
            ElementaPanelGui.Note(
                "The renderer shapes density from these continuous weights, not from the "
                + "dominant-formation label, which switches hard at the midpoint of a blend.");

            EditorGUILayout.Space(2f);
            ElementaPanelGui.Metric("History revision", controller.HistoryRevision.ToString());
            ElementaPanelGui.Metric("Advection offsets",
                $"weather {state.WeatherOffset.x:0} / {state.WeatherOffset.y:0}   "
                + $"shape {state.ShapeOffset.x:0} / {state.ShapeOffset.y:0}");
        }

        // -- Quality -----------------------------------------------------------------

        void DrawQuality(ElementaPanelContext context)
        {
            if (!context.Section("clouds/quality", "Quality"))
                return;

            SolCloudController controller = SolCloudController.Active;

            // The controller keeps its override private, so there is no flag to read. The
            // override is the only thing that can make the resolved quality differ from the
            // authored one, so the difference itself is the signal - and without saying so the
            // page reports "Active Medium" next to "Authored High" and looks broken.
            bool differs = context.Time != null && controller.Quality != context.Time.CloudQuality;
            ElementaPanelGui.Metric("Active quality", differs
                ? $"{controller.Quality}  (not the authored value)"
                : controller.Quality.ToString());

            if (context.Time != null)
            {
                SerializedObject serializedTime = new(context.Time);
                serializedTime.Update();
                SerializedProperty quality = serializedTime.FindProperty("cloudQuality");
                if (quality != null)
                {
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(quality, new GUIContent("Authored quality"));
                    if (EditorGUI.EndChangeCheck())
                    {
                        serializedTime.ApplyModifiedProperties();
                        context.Refresh();
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Override", GUILayout.Width(60f));
                if (GUILayout.Button("Low"))
                    Override(context, SolCloudQuality.Low);
                if (GUILayout.Button("Medium"))
                    Override(context, SolCloudQuality.Medium);
                if (GUILayout.Button("High"))
                    Override(context, SolCloudQuality.High);
                if (GUILayout.Button("Clear"))
                {
                    controller.ClearQualityOverride();
                    context.Refresh();
                }
            }

            if (differs)
                EditorGUILayout.HelpBox(
                    $"The deck is rendering at {controller.Quality} while the scene authors "
                    + $"{context.Time.CloudQuality}. An override is in force - Clear returns it "
                    + "to the authored tier. Overrides are runtime-only and never ship.",
                    MessageType.Info);

            ElementaPanelGui.Note(
                "Changing tier invalidates the temporal history, so the first frames after a "
                + "switch are noisier than the settled result. Judge a tier once it has "
                + "reconverged, not on the frame it changes.");
        }

        static void Override(ElementaPanelContext context, SolCloudQuality quality)
        {
            SolCloudController.Active.SetQualityOverride(quality);
            context.Refresh();
        }

        // -- Renderer feature --------------------------------------------------------

        void DrawFeature(ElementaPanelContext context, SolCloudRendererFeature feature)
        {
            if (!context.Section("clouds/feature", "Renderer Feature"))
                return;

            if (feature == null)
            {
                EditorGUILayout.HelpBox(
                    "No SolCloudRendererFeature is installed on any active renderer, so the "
                    + "volumetric deck never renders and the sky shows its authored baseline "
                    + "only.",
                    MessageType.Warning);
                return;
            }

            SerializedObject serialized = ResolveSerializedFeature(feature);
            serialized.Update();
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(
                serialized.FindProperty("profile"), new GUIContent("Rendering profile"));
            EditorGUILayout.PropertyField(
                serialized.FindProperty("renderInSceneView"),
                new GUIContent("Render in Scene View"));
            EditorGUILayout.PropertyField(
                serialized.FindProperty("renderInReflectionCameras"),
                new GUIContent("Render in reflections"));
            EditorGUILayout.PropertyField(
                serialized.FindProperty("debugLog"), new GUIContent("Log cloud decisions"));
            if (EditorGUI.EndChangeCheck())
            {
                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(feature);
                SolCloudController.Active.InvalidateHistory();
                context.RefreshLayout();
            }
        }

        // -- Bakers ------------------------------------------------------------------

        void DrawBakers(ElementaPanelContext context)
        {
            if (!context.Section("clouds/bakers", "Baked Inputs", false))
                return;

            ElementaPanelGui.Note(
                "The cloud deck samples baked packed noise and a weather map. Rebake after "
                + "changing the generator; nothing about the deck's appearance is baked, only "
                + "the noise it reads.");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (ElementaPanelGui.ActionButton("Bake Noise Textures",
                        "Regenerate the packed cloud noise and weather map textures.", true))
                {
                    SolCloudNoiseBaker.Bake();
                    context.RefreshLayout();
                }

                if (ElementaPanelGui.ActionButton("Export Structure Starter",
                        "Write a starter authored-mesostructure texture to edit by hand.", true))
                {
                    SolCloudNoiseBaker.ExportAuthoredStructureStarter();
                    context.RefreshLayout();
                }
            }
        }

        // -- Profile body ------------------------------------------------------------

        void DrawProfileBody(ElementaPanelContext context, SolCloudRendererFeature feature)
        {
            if (feature == null)
                return;

            SerializedObject serialized = ResolveSerializedFeature(feature);
            SolCloudRenderingProfile profile =
                serialized.FindProperty("profile")?.objectReferenceValue as SolCloudRenderingProfile;
            if (profile == null)
                return;

            ElementaPanelGui.Rule();
            EditorGUILayout.LabelField($"Editing  {profile.name}", EditorStyles.boldLabel);
            if (profile.debugView != SolCloudDebugView.FinalLighting)
                EditorGUILayout.HelpBox(
                    $"This profile is showing the {profile.debugView} debug view. The final "
                    + "image is not what the scene view is drawing.",
                    MessageType.Warning);

            if (_serializedProfile == null || _serializedProfile.targetObject != profile)
                _serializedProfile = new SerializedObject(profile);

            if (!ElementaPanelGui.DrawGroupedProperties(
                    context, "clouds/body", _serializedProfile, ref context.State.cloudFilter))
                return;

            EditorUtility.SetDirty(profile);

            // Step counts, scales and history weights all describe the temporal accumulation,
            // so an edit that keeps the old history would blend two different reconstructions.
            SolCloudController.Active.InvalidateHistory();
            context.Refresh();
        }

        // -- Shared ------------------------------------------------------------------

        SerializedObject ResolveSerializedFeature(SolCloudRendererFeature feature)
        {
            if (_serializedFeature == null || _serializedFeature.targetObject != feature)
                _serializedFeature = new SerializedObject(feature);
            return _serializedFeature;
        }
    }
}
