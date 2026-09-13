using System.Collections.Generic;
using UnityEngine.UIElements;
using UI = Sol.Environment.EditorTools.ElementaPanelGui;

namespace Sol.Environment.EditorTools
{
    sealed class ElementaCloudPage : ElementaPanelPage
    {
        public override string Title => "Clouds";
        public override string Subtitle => "Shape, movement and light.";
        protected override void Build(ElementaPanelContext context, VisualElement root)
        {
            var target = new DropdownField("Editing", new List<string> { "Sky baseline", "Weather condition" }, context.State.cloudsWeather ? 1 : 0);
            target.AddToClassList("elementa-field"); root.Add(target);
            target.RegisterValueChangedCallback(e => { context.State.cloudsWeather = e.newValue == "Weather condition"; context.RefreshLayout(); });
            if (context.State.cloudsWeather)
            {
                var profile = ElementaCloudAuthoring.WeatherPicker(this, root);
                if (profile != null)
                {
                    int index = context.Weather.IndexOfProfile(profile);
                    UI.Source(this, root, context.Weather, $"profiles.Array.data[{index}].profile", "Weather profile");
                    var preview = UI.Row(root);
                    UI.Button(preview, "Preview condition", () => ElementaPanelActions.PreviewCondition(context, profile));
                    UI.Note(preview, "Profile edits persist. Weather preview is temporary.");
                    ElementaCloudAuthoring.Build(this, root, profile, true, "clouds/weather");
                }
            }
            else
            {
                if (context.Time != null)
                {
                    UI.Source(this, root, context.Time, "skyProfile", "Sky profile");
                    UI.Metric(this, root, "Weather influence", () => context.Time != null ? context.Time.WeatherCloudiness.ToString("P0") : "—");
                    UI.Button(root, "Open influencing weather", () => context.Window.ShowPage("Weather"));
                    ElementaCloudAuthoring.Build(this, root, context.Time.SkyProfile, false, "clouds/baseline");
                }
                else UI.Help(root, "Assign a clock and sky profile in Scene setup.");
            }
            var feature = ElementaPanelDoctor.ResolveFeature<SolCloudRendererFeature>();
            var rendering = UI.Foldout(this, root, "clouds/profile", "Lighting & detail · shared rendering profile");
            if (feature != null)
            {
                UI.Source(this, rendering, feature, "profile", "Rendering profile");
                var profile = context.Serialized(feature).FindProperty("profile")?.objectReferenceValue as SolCloudRenderingProfile;
                if (profile != null)
                {
                    UI.Fields(this, rendering, profile, "multipleScatteringStrength|Interior light", "innerGlowStrength|Inner glow", "cloudAdvectionMultiplier|Cloud motion multiplier", "sculptingStrength|Silhouette detail");
                    UI.Route(this, rendering, profile, "Quality", new List<string>(ElementaCloudAuthoring.QualityFields).ToArray());
                    UI.GroupedProperties(this, rendering, "clouds/rendering", profile);
                }
                UI.Advanced(this, root, "clouds/component/feature", "Advanced · renderer setup", feature, "renderInSceneView", "renderInReflectionCameras", "debugLog");
            }
            else UI.Help(rendering, "Cloud rendering is not installed. Use Overview to set it up.");
            var baked = UI.Foldout(this, root, "clouds/bakers", "Baked inputs");
            var actions = UI.Row(baked);
            UI.Button(actions, "Bake noise textures", () => ElementaPanelActions.Tool(context, SolCloudNoiseBaker.Bake));
            UI.Button(actions, "Export structure starter", () => ElementaPanelActions.Tool(context, SolCloudNoiseBaker.ExportAuthoredStructureStarter));
            var why = UI.Foldout(this, root, "clouds/sources", "Why does it look this way?");
            UI.ObjectRow(why, "Baseline", context.Time != null ? context.Time.SkyProfile : null);
            UI.Metric(this, why, "Weather", () => context.Weather != null ? UI.Describe(context.Weather.TargetProfile) : "Sky baseline");
            UI.Button(why, "Rendering quality and debug views", () => context.Window.ShowPage("Quality"));
        }
    }
}
