using System.Collections.Generic;
using Sol.ToD;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UI = Sol.Environment.EditorTools.ElementaPanelGui;

namespace Sol.Environment.EditorTools
{
    sealed class ElementaSkyPage : ElementaPanelPage
    {
        public override string Title => "Sky";
        public override string Subtitle => "Day, night, horizon and atmosphere.";
        protected override void Build(ElementaPanelContext context, VisualElement root)
        {
            var time = context.Time;
            if (time == null) { UI.Help(root, "Assign Time of Day in Scene setup."); return; }
            var source = UI.Source(this, root, time, "skyProfile", "Sky profile");
            var tools = UI.Row(source);
            UI.Button(tools, "New profile", () => Create(context));
            UI.Button(tools, "Migrate inline values", () => Migrate(context), enabled: time.SkyProfile == null);
            var profile = time.SkyProfile;
            if (profile == null) { UI.Help(root, "Create a profile or migrate this scene's inline sky values."); return; }
            var appearance = UI.Section(root, "Appearance", "Profile asset · " + profile.name);
            var mode = new DropdownField("Editing colours", new List<string> { "Day", "Night" }, context.State.skyNight ? 1 : 0);
            mode.AddToClassList("elementa-field"); appearance.Add(mode);
            mode.RegisterValueChangedCallback(e => { context.State.skyNight = e.newValue == "Night"; context.RefreshLayout(); });
            var day = new VisualElement(); appearance.Add(day);
            UI.Fields(this, day, profile, "dayZenith|Zenith colour", "dayHorizon|Horizon colour", "dayNadir|Ground colour");
            var night = new VisualElement(); appearance.Add(night);
            UI.Fields(this, night, profile, "nightZenith|Zenith colour", "nightHorizon|Horizon colour", "nightNadir|Ground colour");
            day.style.display = context.State.skyNight ? DisplayStyle.None : DisplayStyle.Flex;
            night.style.display = context.State.skyNight ? DisplayStyle.Flex : DisplayStyle.None;
            UI.Fields(this, appearance, profile, "twilightIntensity|Twilight strength", "sunriseHorizon|Sunrise horizon", "sunsetHorizon|Sunset horizon");
            var atmosphere = UI.Section(root, "Atmosphere");
            UI.Fields(this, atmosphere, profile, "atmosphereDensityMultiplier|Density", "atmosphereBaseHeight|Base height (m)", "atmosphereHeightFalloff|Height falloff");
            UI.Route(this, root, profile, "Clouds", ElementaCloudAuthoring.SkyFields);
            UI.Route(this, root, profile, "Quality", "atmosphereQuality", "atmosphereRaymarchDistance", "atmosphereRaymarchStepCount", "atmosphereRaymarchJitter", "atmosphereBilateralDepthThreshold", "atmosphereSpatialFilterStrength");
            var detail = UI.Foldout(this, root, "sky/details", "Detailed sky appearance");
            UI.GroupedProperties(this, detail, "sky/body", profile);
            var links = UI.Row(root);
            UI.Button(links, "Cloud baseline", () => { context.State.cloudsWeather = false; context.Window.ShowPage("Clouds"); });
            UI.Button(links, "Rendering quality", () => context.Window.ShowPage("Quality"));
            var backdrop = UI.Foldout(this, root, "sky/night", "Backdrop baking");
            UI.ObjectRow(backdrop, "Backdrop cubemap", profile.stellarBackdrop);
            UI.Button(backdrop, "Bake default backdrop", () => ElementaPanelActions.Tool(context, SolStellarBackdropBaker.BakeDefault));
            UI.Metric(this, backdrop, "Default bake", () => SolStellarBackdropBaker.IsCurrentDefaultBake() ? "Current" : "Needs rebuilding");
            UI.Advanced(this, root, "sky/component/timeofday", "Advanced · scene sky settings", time, "skyProfile", "cloudQuality");
            UI.Advanced(this, root, "sky/component/atmosphere", "Advanced · atmosphere controller", context.Atmosphere);
            UI.Advanced(this, root, "sky/component/feature", "Advanced · atmosphere renderer", ElementaPanelDoctor.ResolveFeature<SolAtmosphereRendererFeature>());
        }
        static void Create(ElementaPanelContext context)
        {
            string path = EditorUtility.SaveFilePanelInProject("Create Sky Profile", "Elementa Sky", "asset", "Choose where the sky profile is saved.");
            if (string.IsNullOrEmpty(path)) return;
            var profile = ScriptableObject.CreateInstance<SolSkyProfile>();
            AssetDatabase.CreateAsset(profile, AssetDatabase.GenerateUniqueAssetPath(path));
            Undo.RecordObject(context.Time, "Assign Elementa sky profile"); context.Time.SetSkyProfile(profile);
            EditorUtility.SetDirty(context.Time); PrefabUtility.RecordPrefabInstancePropertyModifications(context.Time); context.RefreshLayout();
        }
        static void Migrate(ElementaPanelContext context)
        {
            string path = EditorUtility.SaveFilePanelInProject("Migrate Sky Profile", context.Time.gameObject.scene.name + " Sky", "asset", "Choose where the migrated profile is saved.");
            if (string.IsNullOrEmpty(path)) return;
            var result = SolSkyProfileMigration.CreateOrReuse(context.Time, path); Selection.activeObject = result.Profile; context.RefreshLayout();
        }
    }
}
