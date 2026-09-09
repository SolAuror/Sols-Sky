using Sol.Landscape;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// The terrain side of Elementa: the layer arrays, the live weight rules, and the wetness
    /// and snow the weather drives across them.
    ///
    /// Only the layer artwork is baked, because compressing textures is not something a
    /// fragment shader can do. Everything about how the terrain looks - which layer wins on a
    /// slope, at an altitude, in a cavity - is resolved live, so a stale bake means the wrong
    /// artwork, never the wrong rules. Saying which of the two is wrong is what this page is
    /// for.
    /// </summary>
    sealed class ElementaLandscapePage : ElementaPanelPage
    {
        SerializedObject _serializedDriver;
        SerializedObject _serializedConfig;

        public override string Title => "Landscape";

        public override string Subtitle =>
            "Terrain layer arrays, the auto-material contract, and the bake they depend on.";

        public override void Draw(ElementaPanelContext context)
        {
            SolLandscapeDriver driver = context.Landscape;
            if (driver == null)
            {
                EditorGUILayout.HelpBox(
                    "No SolLandscapeDriver is loaded. Terrain in this scene renders with "
                    + "whatever material it carries, outside Elementa's layer system.",
                    MessageType.Info);
                return;
            }

            DrawDriver(context, driver);
            DrawBake(context, driver);
            DrawSurface(context);
            DrawConfigBody(context, driver);
        }

        // -- Driver ------------------------------------------------------------------

        void DrawDriver(ElementaPanelContext context, SolLandscapeDriver driver)
        {
            if (!context.Section("landscape/driver", "Driver"))
                return;

            SerializedObject serialized = ResolveSerialized(ref _serializedDriver, driver);
            serialized.Update();
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(
                serialized.FindProperty("landscapeTerrain"), new GUIContent("Terrain"));
            EditorGUILayout.PropertyField(
                serialized.FindProperty("config"), new GUIContent("Config"));
            if (EditorGUI.EndChangeCheck())
            {
                serialized.ApplyModifiedProperties();
                driver.Invalidate();
                context.RefreshLayout();
            }

            bool valid = driver.TryValidateContract(out string refusal);
            if (!valid)
            {
                EditorGUILayout.HelpBox(
                    $"The auto-material contract is not satisfied, so nothing is published to "
                    + $"the terrain shader: {refusal}",
                    MessageType.Warning);
            }

            ElementaPanelGui.Metric("Contract", valid ? "satisfied" : "refused");
            ElementaPanelGui.Metric("Last publish writes", driver.LastPublishWriteCount.ToString());
            ElementaPanelGui.Metric("Total writes", driver.TotalGlobalWriteCount.ToString());
            if (driver.LastPublishRefused)
                ElementaPanelGui.Metric("Last refusal", driver.LastRefusalReason ?? "unknown");

            if (ElementaPanelGui.ActionButton("Republish",
                    "Drop the cached publish state so the driver re-resolves and re-pushes on "
                    + "the next tick.", true, 110f))
            {
                driver.Invalidate();
                context.Refresh();
            }
        }

        // -- Bake --------------------------------------------------------------------

        void DrawBake(ElementaPanelContext context, SolLandscapeDriver driver)
        {
            if (!context.Section("landscape/bake", "Layer Arrays"))
                return;

            SolLandscapeConfig config = driver.config;
            if (config == null)
            {
                EditorGUILayout.HelpBox(
                    "No config, so there are no baked arrays to check. Baking creates one.",
                    MessageType.Warning);
                if (ElementaPanelGui.ActionButton("Bake Layer Arrays",
                        "Pack every TerrainLayer's diffuse, normal and mask into the two BC7 "
                        + "arrays the landscape shader samples.", true, 150f))
                    Bake(context, null);
                return;
            }

            Sol.Landscape.Editor.SolLandscapeStaleness staleness =
                Sol.Landscape.Editor.SolLandscapeArrayBaker.GetStaleness(config);

            EditorGUILayout.HelpBox(
                staleness.IsStale
                    ? $"The baked arrays no longer match the terrain: {staleness.Message}"
                    : "The baked arrays match the terrain and its layer import settings.",
                staleness.IsStale ? MessageType.Warning : MessageType.Info);

            ElementaPanelGui.ObjectRow("CS array", config.CSArray);
            ElementaPanelGui.ObjectRow("NOH array", config.NOHArray);
            ElementaPanelGui.Metric("Baked", string.IsNullOrEmpty(config.BakedUtc)
                ? "never" : config.BakedUtc);
            ElementaPanelGui.Metric("Slice size",
                $"{config.BakedWidth} x {config.BakedHeight}, {config.BakedMipCount} mips");
            ElementaPanelGui.Metric("Storage",
                $"{(config.CSStorageBytes + config.NOHStorageBytes) / (1024f * 1024f):0.0} MB");
            ElementaPanelGui.Metric("Layers", config.Layers != null
                ? config.Layers.Count.ToString() : "0");

            if (!string.IsNullOrEmpty(config.LastBakeSummary))
                ElementaPanelGui.Note(config.LastBakeSummary);

            if (ElementaPanelGui.ActionButton("Bake Layer Arrays",
                    "Repack the layer artwork. Needed after layer textures or their import "
                    + "settings change, and after the layer order changes.", true, 150f))
                Bake(context, config);
        }

        void Bake(ElementaPanelContext context, SolLandscapeConfig config)
        {
            ElementaPanelDoctor.BakeLandscape(config);
            _serializedConfig = null;
            context.RefreshLayout();
        }

        // -- Surface conditions ------------------------------------------------------

        void DrawSurface(ElementaPanelContext context)
        {
            if (!context.Section("landscape/surface", "Wetness and Snow", false))
                return;

            SolSurfaceConditionState surface = context.Environment.Surface;
            ElementaPanelGui.MetricBar("Wetness", surface.Wetness);
            ElementaPanelGui.MetricBar("Snow cover", surface.SnowCover);
            ElementaPanelGui.Metric("Temperature", $"{surface.TemperatureCelsius:0.0} °C");
            ElementaPanelGui.MetricBar("Relative humidity", surface.RelativeHumidity);

            SolWaterManager water = SolWaterManager.Instance;
            if (water == null)
            {
                ElementaPanelGui.Note(
                    "No SolWaterManager is loaded, so terrain wetness has no authority driving "
                    + "it from rain and shoreline proximity.");
                return;
            }

            ElementaPanelGui.Metric("Rain wetness weight", $"{water.terrainRainWetness:0.00}");
            ElementaPanelGui.Metric("Shoreline wetness weight", $"{water.terrainWaterWetness:0.00}");
            ElementaPanelGui.Metric("Shoreline range", $"{water.terrainWaterWetnessRange:0.00} m");
            ElementaPanelGui.ObjectRow("Wetness terrain", water.terrainWetnessTerrain);
            ElementaPanelGui.ObjectRow("Sand layer", water.terrainWetnessSandLayer);
        }

        // -- Config body -------------------------------------------------------------

        void DrawConfigBody(ElementaPanelContext context, SolLandscapeDriver driver)
        {
            SolLandscapeConfig config = driver.config;
            if (config == null)
                return;

            ElementaPanelGui.Rule();
            EditorGUILayout.LabelField($"Editing  {config.name}", EditorStyles.boldLabel);
            ElementaPanelGui.Note(
                "Layer weights are resolved in the shader from slope, altitude and cavity. "
                + "Changing a rule here needs no rebake; changing layer artwork does.");

            if (!ElementaPanelGui.DrawGroupedProperties(
                    context, "landscape/body",
                    ResolveSerialized(ref _serializedConfig, config),
                    ref context.State.landscapeFilter))
                return;

            EditorUtility.SetDirty(config);
            driver.Invalidate();
            context.Refresh();
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
