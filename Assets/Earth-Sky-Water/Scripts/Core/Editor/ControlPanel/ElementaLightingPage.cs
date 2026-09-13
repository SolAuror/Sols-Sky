using UnityEngine.UIElements;
using UI = Sol.Environment.EditorTools.ElementaPanelGui;

namespace Sol.Environment.EditorTools
{
    sealed class ElementaLightingPage : ElementaPanelPage
    {
        public override string Title => "Lighting";
        public override string Subtitle => "Sunlight, moonlight and indirect illumination.";
        protected override void Build(ElementaPanelContext context, VisualElement root)
        {
            var time = context.Time;
            if (time != null)
            {
                var sunlight = UI.Section(root, "Sunlight", "Scene setting · " + UI.Describe(time));
                UI.Fields(this, sunlight, time, "noonColor|Noon colour", "maxIntensity|Peak intensity");
                var moonlight = UI.Section(root, "Moonlight");
                UI.Fields(this, moonlight, time, "moonColorNight|Night colour", "moonMaxIntensity|Peak intensity");
                var ambient = UI.Section(root, "Ambient light and fog");
                UI.Fields(this, ambient, time, "controlAmbient|Control ambient light", "ambientFromSky|Ambient from sky", "controlFog|Control Unity fog");
                UI.Field(this, ambient, time.SkyProfile != null ? (UnityEngine.Object)time.SkyProfile : time,
                    time.SkyProfile != null ? "ambientIntensity" : "ambientSkyIntensity", "Ambient intensity");
                UI.Note(ambient, time.SkyProfile != null ? "Ambient intensity edits the sky profile · " + time.SkyProfile.name : "Ambient intensity is a scene setting.");
                UI.Note(ambient, "Atmosphere height fog is separate from Unity fog. Profile colours apply when a sky profile is assigned.");
                var detail = UI.Foldout(this, root, "lighting/celestial", "Detailed colour and intensity");
                UI.Fields(this, detail, time, "sunriseColor|Sunrise colour", "sunsetColor|Sunset colour", "minIntensity|Minimum sunlight", "moonMinIntensity|Minimum moonlight", "fogColorFromSky|Fog colour from sky");
                UI.Field(this, detail, time.SkyProfile != null ? (UnityEngine.Object)time.SkyProfile : time, "enableNightFog", "Night fog");
                UI.Button(detail, "Sun and moon appearance", () => context.Window.ShowPage("Sky"));
            }
            else UI.Help(root, "Assign Time of Day in Scene setup to author sunlight and moonlight.");
            var probes = UI.Foldout(this, root, "lighting/probes", "Indirect lighting · probe volumes");
            UI.Metric(this, probes, "Probe data", () => SolSkyLightingScheduler.IsApvDataAvailable() ? "Loaded" : "Using ambient probe fallback");
            UI.Button(probes, "Configure Adaptive Probe Volumes", () => ElementaPanelActions.Tool(context, SolApvSetupUtility.ConfigureFromMenu));
            UI.Note(probes, "After placement, bake in Window → Rendering → Lighting using SolEnvironmentAPV. Contributors must enable Contribute Global Illumination.");
            UI.Button(root, "Lighting quality", () => context.Window.ShowPage("Quality", "quality/lighting"));
            UI.Advanced(this, root, "lighting/component/director", "Advanced · lighting director", context.Lighting, "qualityProfile");
        }
    }
}
