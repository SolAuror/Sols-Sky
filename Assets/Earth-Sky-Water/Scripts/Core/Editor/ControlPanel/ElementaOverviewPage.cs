using Sol.ToD;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using UI = Sol.Environment.EditorTools.ElementaPanelGui;

namespace Sol.Environment.EditorTools
{
    sealed class ElementaOverviewPage : ElementaPanelPage
    {
        public override string Title => "Overview";
        public override string Subtitle => "Your environment and anything that needs attention.";
        public override int Badge(ElementaPanelContext context) => ElementaPanelDoctor.Count(context.Issues, MessageType.Error) + ElementaPanelDoctor.Count(context.Issues, MessageType.Warning);
        protected override void Build(ElementaPanelContext context, VisualElement root)
        {
            var now = UI.Section(root, "Current environment");
            UI.Metric(this, now, "Clock", () => context.Time != null ? ElementaPanelFormat.Clock(context.Time.ClockHour) : "Not assigned");
            UI.Metric(this, now, "Weather", () => context.Weather != null ? UI.Describe(context.Weather.TargetProfile) + (context.HasWeatherPreview ? " · preview" : "") : "Sky baseline");
            UI.Metric(this, now, "Wind", () => ElementaPanelFormat.Wind(context.Environment.Wind.Speed));
            UI.Metric(this, now, "Cloud quality", () => SolCloudController.Active.Quality + (context.Time != null ? " · scene default " + context.Time.CloudQuality : ""));
            UI.Metric(this, now, "Atmosphere quality", () => context.Atmosphere != null ? context.Atmosphere.Quality.ToString() : "Inactive");
            UI.Metric(this, now, "Water quality", () => context.Water != null && context.Water.QualityProfile != null ? context.Water.QualityProfile.ActiveTier + " · authored " + context.Water.QualityProfile.tier : "Built-in defaults");
            UI.Metric(this, now, "Health", () => Badge(context) == 0 ? "No blocking issues or warnings" : Badge(context) + " issues to review");
            var actions = UI.Row(root);
            UI.Button(actions, "Re-check now", () => { context.Resolve(true); context.InvalidateIssues(); context.RefreshLayout(); });
            UI.Button(actions, "Add System Manager", () => ElementaPanelActions.Tool(context, ElementaPanelDoctor.InstantiateSystemManager), enabled: ElementaPanelDoctor.HasSystemManagerPrefab);
            var issues = new VisualElement(); root.Add(issues); UI.Issues(this, issues);
            var setup = UI.Foldout(this, root, "overview/authorities", "Scene setup");
            UI.Note(setup, "Each subsystem shows its actual resolved object and scene. Changing the clock does not move other systems between scenes.");
            var time = new ObjectField("Time of Day") { objectType = typeof(TimeOfDay), allowSceneObjects = true, value = context.Time };
            var weather = new ObjectField("Weather") { objectType = typeof(SolWeatherManager), allowSceneObjects = true, value = context.Weather };
            time.AddToClassList("elementa-field"); weather.AddToClassList("elementa-field"); setup.Add(time); setup.Add(weather);
            time.RegisterValueChangedCallback(e => { context.ClearWeatherPreview(); context.State.timeOfDay = e.newValue as TimeOfDay; context.Resolve(true); context.RefreshLayout(); });
            weather.RegisterValueChangedCallback(e => { context.ClearWeatherPreview(); context.State.weatherManager = e.newValue as SolWeatherManager;
                context.State.inspectedWeather = null; context.State.weatherA = null; context.State.weatherB = null; context.State.windInitialized = false; context.Resolve(true); context.RefreshLayout(); });
            UI.ObjectRow(setup, "Environment world", context.World); UI.ObjectRow(setup, "Water world", context.Water);
            UI.ObjectRow(setup, "Lighting", context.Lighting); UI.ObjectRow(setup, "Atmosphere", context.Atmosphere);
            UI.ObjectRow(setup, "Coordinator", context.Coordinator); UI.ObjectRow(setup, "Landscape", context.Landscape);
            UI.ObjectRow(setup, "Precipitation VFX", context.RainVfx); UI.ObjectRow(setup, "Terrain shoreline", context.Shoreline);
            UI.ObjectRow(setup, "Surface wetness", context.Wetness); UI.ObjectRow(setup, "Planar reflections", context.PlanarReflections);
            var assets = UI.Foldout(this, root, "overview/assets", "Assigned assets");
            UI.ObjectRow(assets, "Sky", context.Time != null ? context.Time.SkyProfile : null);
            UI.ObjectRow(assets, "Water default", context.Water != null ? context.Water.DefaultProfile : null);
            UI.ObjectRow(assets, "Water quality", context.Water != null ? context.Water.QualityProfile : null);
            UI.ObjectRow(assets, "Lighting quality", context.Lighting != null ? context.Lighting.QualityProfile : null);
            UI.ObjectRow(assets, "Landscape", context.Landscape != null ? context.Landscape.config : null);
            if (context.Weather != null && context.Weather.profiles != null)
                foreach (var entry in context.Weather.profiles)
                    if (entry?.profile != null) UI.ObjectRow(assets, "Weather", entry.profile);
            UI.Advanced(this, root, "overview/component/world", "Advanced · environment settings", context.World);
            UI.Advanced(this, root, "overview/component/coordinator", "Advanced · coordinator", context.Coordinator);
        }
    }
}
