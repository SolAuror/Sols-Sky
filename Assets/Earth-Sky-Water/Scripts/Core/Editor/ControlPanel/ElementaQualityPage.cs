using System;
using Sol.Lighting;
using Sol.ToD;
using Sol.Water;
using Sol.Water.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UI = Sol.Environment.EditorTools.ElementaPanelGui;

namespace Sol.Environment.EditorTools
{
    sealed class ElementaQualityPage : ElementaPanelPage
    {
        public override string Title => "Quality";
        public override string Subtitle => "Authored defaults, active quality and rendering budgets.";
        public override int Badge(ElementaPanelContext context) => context.Issues.FindAll(i => i.Id.StartsWith("debug/")).Count;

        protected override void Build(ElementaPanelContext context, VisualElement root)
        {
            var presets = UI.Section(root, "Compare quality", "Live control · temporary selections");
            UI.Note(presets, "Sets cloud and atmosphere overrides, and lighting when a quality profile is assigned. A lighting director can also enforce cloud, atmosphere and water quality; the active results below show what is in use.");
            var actions = UI.Row(presets);
            foreach (SolLightingQualityTier tier in Enum.GetValues(typeof(SolLightingQualityTier)))
                UI.Button(actions, tier.ToString(), () =>
                {
                    if (context.Lighting != null && context.Lighting.QualityProfile != null) context.Lighting.SetTier(tier);
                    SolCloudController.Active.SetQualityOverride((SolCloudQuality)tier);
                    if (context.Atmosphere != null) context.Atmosphere.SetQuality((SolAtmosphereQuality)tier);
                    context.Refresh();
                });
            UI.Note(presets, "Allow a few frames for temporal histories to settle after a quality change.");
            Lighting(context, root); Atmosphere(context, root); Clouds(context, root); Water(context, root);
            Pipeline(context, root); DebugViews(context, root);
        }

        void Tiers(VisualElement parent, Func<string> active, Action low, Action medium, Action high, Action clear)
        {
            UI.Metric(this, parent, "Active quality", active);
            var row = UI.Row(parent);
            UI.Button(row, "Preview Low", () => { low(); Context.Refresh(); });
            UI.Button(row, "Preview Medium", () => { medium(); Context.Refresh(); });
            UI.Button(row, "Preview High", () => { high(); Context.Refresh(); });
            UI.Button(row, "Clear override", () => { clear(); Context.Refresh(); });
        }

        void Lighting(ElementaPanelContext ctx, VisualElement root)
        {
            var section = UI.Section(root, "Lighting"); section.name = "quality/lighting";
            var director = ctx.Lighting;
            if (director == null) { UI.Help(section, "No lighting director is loaded."); return; }
            UI.Source(this, section, director, "qualityProfile", "Lighting quality profile");
            var profile = director.QualityProfile;
            if (profile != null)
            {
                UI.Field(this, section, profile, "defaultTier", "Authored default");
                Tiers(section, () => director.ActiveTier.ToString(),
                    () => director.SetTier(SolLightingQualityTier.Low), () => director.SetTier(SolLightingQualityTier.Medium),
                    () => director.SetTier(SolLightingQualityTier.High), profile.ResetToDefaultTier);
                UI.Advanced(this, section, "quality/lighting/profile", "Lighting profile details", profile);
            }
            else
            {
                bool preview = UI.IsPreviewTarget(director);
                UI.Note(section, preview ? "Live control · generated editor preview. Add a scene lighting director to author a saved fallback tier."
                    : "Scene setting · without a profile, the director saves its fallback tier in this scene.");
                UI.Field(this, section, director, "fallbackTier", preview ? "Preview fallback tier" : "Saved fallback tier",
                    role: preview ? ElementaFieldRole.ReadOnly : ElementaFieldRole.Curated);
                UI.Metric(this, section, "Active quality", () => director.ActiveTier.ToString());
            }
            var budget = UI.Foldout(this, section, "quality/lighting", "Lighting budgets");
            UI.Metric(this, budget, "Shadow distance", () => $"{director.ActiveQualitySettings.MainShadowDistance:0} m");
            UI.Metric(this, budget, "Cascades", () => director.ActiveQualitySettings.MainShadowCascades.ToString());
            UI.Metric(this, budget, "Main / punctual atlas", () => $"{director.ActiveQualitySettings.MainShadowAtlasResolution} / {director.ActiveQualitySettings.PunctualShadowAtlasResolution}");
            UI.Metric(this, budget, "Managed light limit", () => director.ActiveQualitySettings.ManagedLightLimit.ToString());
            UI.Metric(this, budget, "Shadow slice limit", () => director.ActiveQualitySettings.ShadowSliceLimit.ToString());
            UI.Metric(this, budget, "Volumetric light limit", () => director.ActiveQualitySettings.LocalVolumetricLightLimit.ToString());
            UI.Metric(this, budget, "Probe refresh", () => $"{director.ActiveQualitySettings.ReflectionProbeRefreshInterval:0.0} s");
        }

        void Atmosphere(ElementaPanelContext ctx, VisualElement root)
        {
            var section = UI.Section(root, "Atmosphere"); section.name = "quality/atmosphere";
            var sky = ctx.Time != null ? ctx.Time.SkyProfile : null;
            if (sky != null) { UI.Note(section, "Profile asset · " + sky.name); UI.Field(this, section, sky, "atmosphereQuality", "Authored default"); }
            var atmosphere = ctx.Atmosphere;
            if (atmosphere != null)
                Tiers(section, () => atmosphere.Quality.ToString(),
                    () => atmosphere.SetQuality(SolAtmosphereQuality.Low), () => atmosphere.SetQuality(SolAtmosphereQuality.Medium),
                    () => atmosphere.SetQuality(SolAtmosphereQuality.High), atmosphere.ClearQualityOverride);
            else UI.Help(section, "No atmosphere controller is loaded.");
            if (sky == null) return;
            var budget = UI.Foldout(this, section, "quality/sky", "Atmosphere budgets");
            UI.Fields(this, budget, sky, "atmosphereRaymarchDistance", "atmosphereRaymarchStepCount", "atmosphereRaymarchJitter", "atmosphereBilateralDepthThreshold", "atmosphereSpatialFilterStrength");
        }

        void Clouds(ElementaPanelContext ctx, VisualElement root)
        {
            var section = UI.Section(root, "Clouds", "Scene setting · authored default / Live control · override"); section.name = "quality/clouds";
            UI.Field(this, section, ctx.Time, "cloudQuality", "Authored default");
            var controller = SolCloudController.Active;
            Tiers(section, () => controller.Quality.ToString(),
                () => controller.SetQualityOverride(SolCloudQuality.Low), () => controller.SetQualityOverride(SolCloudQuality.Medium),
                () => controller.SetQualityOverride(SolCloudQuality.High), controller.ClearQualityOverride);
            var feature = ElementaPanelDoctor.ResolveFeature<SolCloudRendererFeature>();
            if (feature == null) return;
            var budget = UI.Foldout(this, section, "quality/clouds", "Cloud rendering budgets");
            UI.Source(this, budget, feature, "profile", "Cloud rendering profile");
            var profile = ctx.Serialized(feature).FindProperty("profile").objectReferenceValue as SolCloudRenderingProfile;
            UI.GroupedProperties(this, budget, "quality/clouds/profile", profile,
                include: p => ElementaCloudAuthoring.QualityFields.Contains(p) && p != "debugView");
            UI.Fields(this, budget, feature, "renderInSceneView", "renderInReflectionCameras");
        }

        void Water(ElementaPanelContext ctx, VisualElement root)
        {
            var section = UI.Section(root, "Water"); section.name = "quality/water";
            if (ctx.Water == null) { UI.Help(section, "No water world is loaded."); return; }
            UI.Source(this, section, ctx.Water, "qualityProfile", "Water quality profile");
            var profile = ctx.Water.QualityProfile;
            if (profile == null) { UI.Note(section, "Using built-in defaults."); return; }
            UI.Field(this, section, profile, "tier", "Authored default");
            Tiers(section, () => profile.ActiveTier.ToString(),
                () => profile.SetTierOverride(SolWaterQualityTier.Low), () => profile.SetTierOverride(SolWaterQualityTier.Medium),
                () => profile.SetTierOverride(SolWaterQualityTier.High), profile.ClearTierOverride);
            UI.Note(section, "Low disables the ocean spectrum and uses authored Gerstner waves. The lighting director may enforce the water tier.");
            UI.Advanced(this, section, "quality/water/profile", "Water budgets", profile);
        }

        void Pipeline(ElementaPanelContext ctx, VisualElement root)
        {
            var section = UI.Foldout(this, root, "quality/pipeline", "Render pipeline");
            var pipeline = ElementaPanelDoctor.ResolvePipeline();
            if (pipeline == null) { UI.Help(section, "No Universal Render Pipeline asset is active.", HelpBoxMessageType.Error); return; }
            UI.ObjectRow(section, "Pipeline asset", pipeline);
            UI.Note(section, "Profile asset · these settings persist. Depth is required by atmosphere, clouds and water; opaque colour is required for water refraction.");
            UI.Field(this, section, pipeline, "m_RequireDepthTexture", "Camera depth texture");
            UI.Field(this, section, pipeline, "m_RequireOpaqueTexture", "Camera opaque texture");
            UI.Metric(this, section, "Shadow distance", () => $"{pipeline.shadowDistance:0} m");
            UI.Metric(this, section, "Shadow cascades", () => pipeline.shadowCascadeCount.ToString());
            UI.Metric(this, section, "Main shadow atlas", () => pipeline.mainLightShadowmapResolution.ToString());
            UI.ObjectRow(section, "Atmosphere feature", ElementaPanelDoctor.ResolveFeature<SolAtmosphereRendererFeature>());
            UI.ObjectRow(section, "Cloud feature", ElementaPanelDoctor.ResolveFeature<SolCloudRendererFeature>());
            UI.ObjectRow(section, "Water feature", ElementaPanelDoctor.ResolveFeature<SolWaterRendererFeature>());
        }

        void DebugViews(ElementaPanelContext ctx, VisualElement root)
        {
            var section = UI.Foldout(this, root, "quality/debug", "Debug views", Badge(ctx) > 0,
                "Profile assets · these replace the final image and persist until reset.");
            var water = ElementaPanelDoctor.ResolveFeature<SolWaterRendererFeature>();
            var clouds = ElementaPanelDoctor.ResolveFeature<SolCloudRendererFeature>();
            var profile = clouds != null ? ctx.Serialized(clouds).FindProperty("profile").objectReferenceValue as SolCloudRenderingProfile : null;
            UI.Fields(this, section, water, "debugMode|Water surface", "debugLog|Log water decisions", "renderInSceneView|Water in Scene View");
            UI.Field(this, section, profile, "debugView", "Cloud view");
            UI.Field(this, section, clouds, "debugLog", "Log cloud decisions");
            UI.Button(section, "Reset every debug view", () =>
            {
                if (water != null)
                {
                    var serialized = ctx.Serialized(water);
                    serialized.FindProperty("debugMode").enumValueIndex = (int)SolWaterDebugMode.Disabled;
                    serialized.FindProperty("debugLog").boolValue = false;
                    serialized.ApplyModifiedProperties(); ctx.PublishEdit(water);
                }
                if (clouds != null)
                {
                    var serialized = ctx.Serialized(clouds); serialized.FindProperty("debugLog").boolValue = false;
                    serialized.ApplyModifiedProperties(); ctx.PublishEdit(clouds);
                }
                if (profile != null)
                {
                    var serialized = ctx.Serialized(profile); serialized.FindProperty("debugView").enumValueIndex = (int)SolCloudDebugView.FinalLighting;
                    serialized.ApplyModifiedProperties(); ctx.PublishEdit(profile);
                }
            });
        }
    }
}
