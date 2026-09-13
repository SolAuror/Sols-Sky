using System;
using System.Collections.Generic;
using System.Linq;
using Sol.ToD;
using UnityEngine;
using UnityEngine.UIElements;
using UI = Sol.Environment.EditorTools.ElementaPanelGui;

namespace Sol.Environment.EditorTools
{
    /// <summary>The same authored cloud controls wherever the user enters the workflow.</summary>
    static class ElementaCloudAuthoring
    {
        public static readonly string[] SkyFields = {
            "cloudFormation", "cloudCoverage", "cloudErosion", "cloudDensity", "cloudBaseHeight",
            "cloudThickness", "cloudVerticalDevelopment", "cloudAnvilAmount", "cloudCirrusAmount",
            "cloudEdgeSoftness", "cloudBaseSoftness", "cloudShadowStrength" };
        public static readonly HashSet<string> QualityFields = new() {
            "debugView", "temporalRejectStartMetres", "temporalRejectEndMetres", "lightStepMetres",
            "maximumRayDistance", "maximumLightMarchMetres", "lowShadowFootprintMetres",
            "mediumShadowFootprintMetres", "highShadowFootprintMetres", "lowRenderScale",
            "mediumRenderScale", "highRenderScale", "lowViewSteps", "mediumViewSteps", "highViewSteps",
            "mediumLightSteps", "highLightSteps", "mediumHistoryWeight", "highHistoryWeight",
            "bilateralDepthThreshold" };

        public static void Build(ElementaPanelPage page, VisualElement parent,
            UnityEngine.Object profile, bool weather, string key)
        {
            if (profile == null) { UI.Help(parent, "Assign a profile to author cloud shape."); return; }
            var layer = UI.Section(parent, "Cloud layer", "Profile asset · " + profile.name);
            UI.Fields(page, layer, profile, "cloudFormation|Formation");
            UI.Field(page, layer, profile, weather ? "cloudiness" : "cloudCoverage",
                weather ? "Weather influence" : "Cloud coverage",
                weather ? "0 uses the sky baseline; 1 drives toward overcast. This is not absolute coverage." : "Fraction of the baseline sky covered by clouds.");
            if (weather)
            {
                UI.Note(layer, "Weather influence: 0 uses the sky baseline; 1 drives toward overcast.");
                UI.Field(page, layer, profile, "overrideAdvancedCloudShape", "Custom layer shape");
            }
            var shape = new VisualElement(); layer.Add(shape);
            UI.Fields(page, shape, profile, "cloudBaseHeight|Cloud base (m)", "cloudThickness|Layer thickness (m)", "cloudDensity|Density");
            var explanation = UI.Note(layer, "Layer shape comes from the formation preset. Enable Custom layer shape to edit it.");
            page.Track(() =>
            {
                bool custom = !weather || profile is SolWeatherProfileAsset p && p.overrideAdvancedCloudShape;
                shape.SetEnabled(custom); explanation.style.display = custom ? DisplayStyle.None : DisplayStyle.Flex;
            });
            var detail = UI.Foldout(page, parent, key + "/shape", "Shape details");
            UI.Fields(page, detail, profile, "cloudErosion|Edge breakup");
            var customDetail = new VisualElement(); detail.Add(customDetail);
            UI.Fields(page, customDetail, profile, "cloudVerticalDevelopment|Vertical development", "cloudAnvilAmount|Anvil amount",
                (weather ? "cirrusAmount" : "cloudCirrusAmount") + "|Cirrus amount", "cloudEdgeSoftness|Edge softness", "cloudBaseSoftness|Underside softness", "cloudShadowStrength|Shadow strength");
            if (weather)
            {
                UI.Fields(page, customDetail, profile, "cloudCoverageBias|Coverage adjustment");
                UI.Field(page, detail, profile, "cloudCoverageVariance", "Daily coverage variation");
                page.Track(() => customDetail.SetEnabled(profile is SolWeatherProfileAsset p && p.overrideAdvancedCloudShape));
            }
            UI.Metric(page, layer, weather ? "Condition coverage" : "Baseline coverage", () =>
                weather && profile is SolWeatherProfileAsset p ? p.ResolveEffectiveCoverage(page.Context.Time != null && page.Context.Time.SkyProfile != null ? page.Context.Time.SkyProfile.cloudCoverage : SolWeatherAdjacency.NominalAuthoredCoverage).ToString("P0")
                    : profile is SolSkyProfile sky ? sky.cloudCoverage.ToString("P0") : "—");
            if (weather) UI.Note(layer, "Condition coverage combines this profile with the sky baseline, before daily variation and transition blending.");
        }

        public static SolWeatherProfileAsset SelectedWeather(ElementaPanelContext context)
        {
            var manager = context.Weather;
            if (manager == null) return null;
            if (context.State.inspectedWeather != null && manager.IndexOfProfile(context.State.inspectedWeather) >= 0)
                return context.State.inspectedWeather;
            var selections = manager.profiles;
            int oldIndex = context.State.inspectedWeatherIndex;
            var selected = selections != null && oldIndex >= 0 && oldIndex < selections.Length ? selections[oldIndex]?.profile : null;
            context.State.inspectedWeather = selected != null ? selected : manager.TargetProfile
                ?? selections?.FirstOrDefault(s => s?.profile != null)?.profile;
            context.State.inspectedWeatherIndex = -1;
            return context.State.inspectedWeather;
        }

        public static SolWeatherProfileAsset WeatherPicker(ElementaPanelPage page, VisualElement parent)
        {
            var context = page.Context; var profile = SelectedWeather(context);
            if (context.Weather == null) { UI.Help(parent, "Assign a weather manager in Scene setup."); return null; }
            var entries = context.Weather.profiles ?? Array.Empty<SolWeatherSelection>();
            var indices = Enumerable.Range(0, entries.Length).Where(i => entries[i]?.profile != null).ToArray();
            var choices = indices.Select(i => entries[i].profile.name + "  [" + (i + 1) + "]").ToList();
            if (choices.Count == 0) { UI.Help(parent, "Add conditions to the scene's weather selection list."); return null; }
            int selected = Array.FindIndex(indices, i => entries[i].profile == profile);
            var picker = new DropdownField("Condition", choices, Math.Max(0, selected));
            picker.AddToClassList("elementa-field"); parent.Add(picker);
            picker.RegisterValueChangedCallback(e =>
            {
                context.State.inspectedWeather = entries[indices[choices.IndexOf(e.newValue)]].profile;
                context.RefreshLayout();
            });
            return profile;
        }
    }
}
