using System.Collections.Generic;
using Sol.Landscape;
using Sol.Landscape.Editor;
using UnityEngine;
using UnityEngine.UIElements;
using UI = Sol.Environment.EditorTools.ElementaPanelGui;

namespace Sol.Environment.EditorTools
{
    sealed partial class ElementaLandscapePage : ElementaPanelPage
    {
        public override string Title => "Landscape";
        public override string Subtitle => "Terrain layers, material distribution and snow.";
        protected override void Build(ElementaPanelContext context, VisualElement root)
        {
            if (BuildDesigner(context, root)) return;
            var driver = context.Landscape;
            if (driver == null) { UI.Help(root, "No Elementa landscape driver is loaded. Terrain keeps its own material."); return; }
            var source = UI.Section(root, "Terrain", "Scene setting · " + UI.Describe(driver));
            UI.Fields(this, source, driver, "landscapeTerrain|Terrain");
            UI.Source(this, root, driver, "config", "Landscape config");
            var config = driver.config;
            if (config != null)
            {
                int count = config.Layers?.Count ?? 0;
                if (count > 0)
                {
                    var choices = new List<string>();
                    for (int i = 0; i < count; i++) choices.Add((i + 1) + ". " + (config.Layers[i]?.terrainLayer != null ? config.Layers[i].terrainLayer.name : "Unassigned layer"));
                    context.State.landscapeLayer = Mathf.Clamp(context.State.landscapeLayer, 0, count - 1);
                    var layer = new DropdownField("Layer", choices, context.State.landscapeLayer); layer.AddToClassList("elementa-field"); root.Add(layer);
                    layer.RegisterValueChangedCallback(e => { context.State.landscapeLayer = choices.IndexOf(e.newValue); context.RefreshLayout(); });
                    string prefix = "layers.Array.data[" + context.State.landscapeLayer + "].";
                    var rules = UI.Section(root, "Material distribution", "Profile asset · " + config.name);
                    UI.Field(this, rules, config, prefix + "terrainLayer", "Layer artwork", role: ElementaFieldRole.ReadOnly);
                    UI.Field(this, rules, config, prefix + "mode", "Layer mode");
                    UI.Field(this, rules, config, prefix + "paintProtection", "Paint protection");
                    var auto = new VisualElement(); rules.Add(auto);
                    foreach (var spec in new[] { "autoWeight|Weight", "slopeCenter|Slope midpoint (°)", "slopeContrast|Slope transition (°)", "slopeInfluence|Slope influence", "altitudeReference|Height reference", "heightRange|Height range (m)", "heightInfluence|Height influence" })
                    { var split = spec.Split('|'); UI.Field(this, auto, config, prefix + split[0], split[1]); }
                    var note = UI.Note(rules, "Manual layers use painted terrain weights. Auto rules are inactive.");
                    Track(() => { bool enabled = config.Layers[context.State.landscapeLayer]?.mode == SolLandscapeLayerMode.Auto; auto.SetEnabled(enabled); note.style.display = enabled ? DisplayStyle.None : DisplayStyle.Flex; });
                    var detail = UI.Foldout(this, root, "landscape/layer/details", "Layer details · cavity, projection and snow");
                    foreach (string field in new[] { "stochasticTiling", "triplanarProjection", "slopeCeiling", "slopeCeilingFeather", "slopeBias", "heightBias", "cavityScale", "cavityInfluence", "weatherSnowSusceptibility", "permanentSnowSusceptibility" }) UI.Field(this, detail, config, prefix + field);
                }
                var snow = UI.Foldout(this, root, "landscape/snow", "Snow and material blending");
                UI.Fields(this, snow, config, "permanentSnowAltitudeRange|Permanent snow height (m)", "permanentSnowSlopeSheddingRange|Snow shedding slope (°)", "snowTileSize|Snow texture size (m)", "snowNormalScale|Snow normal strength", "heightTransition|Height blend transition", "triplanarSharpness|Projection sharpness");
                var advanced = UI.Foldout(this, root, "landscape/advanced", "Advanced · artwork and layer order", note: "Layer order maps to texture-array slices. Rebuild textures after changing artwork or order.");
                UI.GroupedProperties(this, advanced, "landscape/body", config, readOnly: path => path.StartsWith("baked") || path == "lastBakeSummary" || path == "csStorageBytes" || path == "nohStorageBytes" || path == "csArray" || path == "nohArray" || path == "terrainData");
            }
            var bake = UI.Foldout(this, root, "landscape/bake", "Texture rebuilding", config == null);
            UI.Note(bake, "Rule changes update live. Texture artwork, import settings and layer order need a rebuild.");
            UI.Metric(this, bake, "Status", () => config == null ? "No texture data" : context.Issues.Exists(i => i.Id == "landscape/stale") ? "Textures need rebuilding" : "Textures current");
            var buttons = UI.Row(bake);
            UI.Button(buttons, "Rebuild terrain textures", () => ElementaPanelActions.Tool(context, () => ElementaPanelDoctor.BakeLandscape(config)));
            UI.Button(buttons, "Refresh terrain", () => { driver.Invalidate(); context.Refresh(); });
            if (config != null)
            {
                UI.ObjectRow(bake, "Colour / smoothness array", config.CSArray); UI.ObjectRow(bake, "Normal / occlusion / height array", config.NOHArray);
                UI.Metric(this, bake, "Last rebuild", () => config.BakedUtc);
                UI.Metric(this, bake, "Texture size", () => $"{config.BakedWidth} × {config.BakedHeight}, {config.BakedMipCount} mips");
                UI.Metric(this, bake, "Storage", () => ((config.CSStorageBytes + config.NOHStorageBytes) / (1024f * 1024f)).ToString("0.0") + " MB");
            }
            var problem = UI.Help(root, "");
            Track(() => { bool valid = driver.TryValidateContract(out string refusal); problem.style.display = valid ? DisplayStyle.None : DisplayStyle.Flex; problem.text = "Terrain settings are not being applied: " + refusal; });
            UI.Advanced(this, root, "landscape/component/driver", "Advanced · landscape driver", driver, "landscapeTerrain", "config");
            UI.Advanced(this, root, "landscape/component/wetness", "Advanced · surface wetness", context.Wetness);
        }
    }
}
