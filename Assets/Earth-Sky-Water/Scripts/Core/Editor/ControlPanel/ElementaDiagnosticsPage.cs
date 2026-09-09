using Sol.Lighting;
using Sol.ToD;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// What the last completed frame cost, what state the stack actually resolved to, and the
    /// maintenance tools that used to live scattered across three top-level menus.
    ///
    /// The budget counters describe the frame that just finished, which is why they are a
    /// readout rather than an overlay: anything that repainted to display them would itself be
    /// part of the frame being measured. Questions like "is the spectrum recorded once or
    /// twice" get an answer here instead of a guess.
    /// </summary>
    sealed class ElementaDiagnosticsPage : ElementaPanelPage
    {
        public override string Title => "Diagnostics";

        public override string Subtitle =>
            "Per-frame budget, resolved state, and the migration, validation and baking tools.";

        public override void Draw(ElementaPanelContext context)
        {
            DrawBudget(context);
            DrawResolved(context);
            DrawTools(context);
        }

        // -- Budget ------------------------------------------------------------------

        void DrawBudget(ElementaPanelContext context)
        {
            if (!context.Section("diagnostics/budget", "Last Completed Frame"))
                return;

            SolEnvironmentBudget.Counters counters = SolEnvironmentBudget.Previous;
            ElementaPanelGui.Metric("FFT dispatches", counters.FftDispatches.ToString(),
                "One per cascade per frame is expected. Two sets means the spectrum is being "
                + "recorded twice.");
            ElementaPanelGui.Metric("Full-res colour copies", counters.FullResColorCopies.ToString());
            ElementaPanelGui.Metric("Environment updates", counters.EnvironmentUpdates.ToString());
            ElementaPanelGui.Metric("Atmosphere pushes", counters.AtmosphereGlobalPushes.ToString());
            ElementaPanelGui.Metric("Lighting tier", counters.HasLightingDirector
                ? ((SolLightingQualityTier)counters.ActiveLightingTier).ToString()
                : "no director");
            ElementaPanelGui.Metric("Lights registered / active",
                $"{counters.RegisteredLights} / {counters.ActiveLights}");
            ElementaPanelGui.Metric("Shadow slices", counters.ShadowSlices.ToString());
            ElementaPanelGui.Metric("Volumetric lights", counters.VolumetricLights.ToString());
            ElementaPanelGui.Metric("GI requests", counters.GiRequests.ToString());
            ElementaPanelGui.Metric("Probe requests / done",
                $"{counters.ProbeRequests} / {counters.ProbeCompletions}");
            ElementaPanelGui.Metric("Camera contexts", counters.CameraContexts.ToString());
            ElementaPanelGui.Metric("SSR history",
                $"{counters.SsrHistoryBytes / (1024f * 1024f):0.0} MB");
        }

        // -- Resolved ----------------------------------------------------------------

        void DrawResolved(ElementaPanelContext context)
        {
            if (!context.Section("diagnostics/resolved", "Resolved State"))
                return;

            SolEnvironmentState environment = context.Environment;
            ElementaPanelGui.Metric("Environment world", context.World != null
                ? "publishing" : "fair-weather stand-in");
            ElementaPanelGui.Metric("Revision", environment.Revision.ToString());
            ElementaPanelGui.Metric("Simulation tick", environment.SimulationTick.ToString());
            ElementaPanelGui.Metric("Absolute world seconds",
                $"{environment.AbsoluteWorldSeconds:0.0}");
            ElementaPanelGui.Metric("Wave clock seconds", $"{environment.WaveSeconds:0.0}");
            ElementaPanelGui.Metric("World day index", environment.WorldDayIndex.ToString());
            ElementaPanelGui.Metric("Clock hour",
                $"{environment.ClockHour:0.000} ({ElementaPanelFormat.Clock(environment.ClockHour)})");

            if (context.World != null)
            {
                ElementaPanelGui.Metric("Fixed step",
                    $"{context.World.FixedStepSeconds * 1000d:0.0} ms");
                ElementaPanelGui.Metric("Climate seed", context.World.ClimateSeed.ToString());
                ElementaPanelGui.Metric("Paused", context.World.Paused ? "yes" : "no");
                ElementaPanelGui.Metric("Environment time scale",
                    $"{context.World.EnvironmentTimeScale:0.00}x");
            }

            EditorGUILayout.Space(2f);
            SolSkyFrame frame = context.Time != null ? context.Time.CurrentSkyFrame : default;
            ElementaPanelGui.Metric("Sky revision",
                frame.IsValid ? frame.Revision.ToString() : "compatibility path");
            ElementaPanelGui.Metric("Cloud history revision",
                SolCloudController.Active.HistoryRevision.ToString());
            ElementaPanelGui.Metric("Camera history contexts",
                SolEnvironmentCameraRegistry.Count.ToString());
            ElementaPanelGui.Metric("APV data",
                SolSkyLightingScheduler.IsApvDataAvailable() ? "loaded" : "unavailable");
            ElementaPanelGui.Metric("Atmosphere", context.Atmosphere != null
                ? context.Atmosphere.Quality.ToString() : "inactive");
            ElementaPanelGui.Metric("Water world", context.Water != null
                ? $"{context.Water.Bodies.Count} bodies" : "none");

            if (ElementaPanelGui.ActionButton("Log Frame Budget",
                    "Write the counters above to the console, so they can be compared across "
                    + "frames or pasted into a report.", true, 150f))
                LogBudget();
        }

        static void LogBudget()
        {
            SolEnvironmentBudget.Counters counters = SolEnvironmentBudget.Previous;
            Debug.Log(
                "[Elementa] last completed frame\n"
                + $"  FFT dispatches:           {counters.FftDispatches}\n"
                + $"  Full-res colour copies:   {counters.FullResColorCopies}\n"
                + $"  Environment updates:      {counters.EnvironmentUpdates}\n"
                + $"  Atmosphere pushes:        {counters.AtmosphereGlobalPushes}\n"
                + $"  Lighting tier:            "
                + $"{(counters.HasLightingDirector ? ((SolLightingQualityTier)counters.ActiveLightingTier).ToString() : "none")}\n"
                + $"  Lights registered/active: {counters.RegisteredLights}/{counters.ActiveLights}\n"
                + $"  Shadow slices:            {counters.ShadowSlices}\n"
                + $"  Volumetric lights:        {counters.VolumetricLights}\n"
                + $"  GI requests:              {counters.GiRequests}\n"
                + $"  Probe requests/done:      {counters.ProbeRequests}/{counters.ProbeCompletions}\n"
                + $"  Live camera contexts:     {counters.CameraContexts}\n"
                + $"  SSR history:              {counters.SsrHistoryBytes / (1024f * 1024f):F1} MB");
        }

        // -- Tools -------------------------------------------------------------------

        void DrawTools(ElementaPanelContext context)
        {
            if (!context.Section("diagnostics/tools", "Maintenance"))
                return;

            ElementaPanelGui.Note(
                "Every one of these was a separate top-level menu item. They are collected "
                + "here so the panel is the whole tool surface rather than most of it.");

            EditorGUILayout.LabelField("Validation", EditorStyles.miniBoldLabel);
            if (ElementaPanelGui.ActionButton("Open Visual Capture Matrix",
                    "Deterministic 4 solar states x 9 weather profiles x 4 views x 3 quality "
                    + "tiers capture run for one sky profile.", true, 240f))
            {
                SolSkyVisualPass window = EditorWindow.GetWindow<SolSkyVisualPass>();
                window.titleContent = new GUIContent("Sol Sky Visual Pass");
                window.Show();
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Migration", EditorStyles.miniBoldLabel);
            if (ElementaPanelGui.ActionButton("Sync Weather Selection Lists",
                    "Reconcile every scene and prefab selection list against the weather "
                    + "profile assets on disk.", true, 240f))
            {
                ElementaPanelDoctor.SyncSelectionLists();
                context.RefreshLayout();
            }

            if (ElementaPanelGui.ActionButton("Migrate Inline Weather Profiles",
                    "Convert legacy inline weather profile values in scenes into shared "
                    + "profile assets.", true, 240f))
            {
                SolWeatherProfileMigration.MigrateInlineProfilesToAssets();
                context.RefreshLayout();
            }

            using (new EditorGUI.DisabledScope(
                       Selection.activeGameObject == null
                       || Selection.activeGameObject.GetComponent<TimeOfDay>() == null))
            {
                if (GUILayout.Button(new GUIContent("Migrate Selected Time Of Day",
                        "Build a sky profile from the selected TimeOfDay's inline values."),
                        GUILayout.Width(240f)))
                    EditorApplication.ExecuteMenuItem("Tools/Elementa/Migrate Selected Time Of Day");
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Baking", EditorStyles.miniBoldLabel);
            if (ElementaPanelGui.ActionButton("Bake Cloud Noise Textures",
                    "Regenerate the packed cloud noise and weather map.", true, 240f))
            {
                SolCloudNoiseBaker.Bake();
                context.RefreshLayout();
            }

            if (ElementaPanelGui.ActionButton("Bake Stellar Backdrop",
                    "Regenerate the deterministic star cubemap.", true, 240f))
            {
                SolStellarBackdropBaker.BakeDefault();
                context.RefreshLayout();
            }

            if (ElementaPanelGui.ActionButton("Bake Landscape Layer Arrays",
                    "Repack terrain layer artwork into the two BC7 arrays.", true, 240f))
            {
                ElementaPanelDoctor.BakeLandscape(
                    context.Landscape != null ? context.Landscape.config : null);
                context.RefreshLayout();
            }
        }
    }
}
