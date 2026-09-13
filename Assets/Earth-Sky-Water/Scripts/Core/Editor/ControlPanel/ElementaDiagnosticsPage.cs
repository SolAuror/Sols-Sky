using System.Collections.Generic;
using UnityEngine.UIElements;
using UI = Sol.Environment.EditorTools.ElementaPanelGui;
using Sol.Landscape;
using Sol.Lighting;
using Sol.ToD;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Everything Elementa currently resolves to, and the tools that operate on the project
    /// rather than on a scene.
    ///
    /// This page is read-only by design. The authoring pages carry only the feedback needed
    /// to judge the edit in front of you - the day length while you drag the day ratio, the
    /// sea state while you turn the wind - and every other number lives here. That split is
    /// what keeps an authoring page about the thing being authored: a designer choosing a
    /// cloud formation does not need the history revision, and someone diagnosing a stuck
    /// temporal buffer does not want to scroll past a colour picker to find it.
    /// </summary>
    sealed class ElementaDiagnosticsPage : ElementaPanelPage
    {
        public override string Title => "Diagnostics";

        public override string Subtitle =>
            "Resolved state for every subsystem, the per-frame budget, and the migration, "
            + "validation and baking tools.";

        void RefreshValues(ElementaPanelContext context)
        {
            DrawNow(context);
            DrawEnvironment(context);
            DrawWeather(context);
            DrawClouds(context);
            DrawSea(context);
            DrawCelestial(context);
            DrawLighting(context);
            DrawLandscape(context);
            DrawBudget(context);

        }

        // -- At a glance -------------------------------------------------------------

        void DrawNow(ElementaPanelContext context)
        {
            if (!Section("diagnostics/now", "Right Now", true,
                    "What the scene is presenting at this instant."))
                return;

            SolEnvironmentState environment = context.Environment;

            if (context.Time != null)
            {
                Calendar calendar = context.Calendar;
                Metric("Clock",
                    ElementaPanelFormat.Clock(context.Time.ClockHour));
                Metric("Date", calendar != null
                    ? $"{calendar.DateString}  ·  {calendar.CurrentSeason}"
                    : "no calendar");
                Metric("Daylight",
                    $"{ElementaPanelFormat.Clock(context.Time.SunriseClockHour)} - "
                    + $"{ElementaPanelFormat.Clock(context.Time.SunsetClockHour)}");
            }
            else
            {
                Metric("Clock",
                    ElementaPanelFormat.Clock(environment.ClockHour) + "  (stand-in)");
            }

            if (context.Weather != null)
            {
                SolWeatherProfileAsset target = context.Weather.TargetProfile;
                string label = target != null ? target.name : "(none)";
                if (context.Weather.IsTransitioning)
                    label += $"  ·  blending {context.Weather.TransitionProgress:P0}";
                if (context.HasWeatherPreview)
                    label += "  ·  PREVIEW";
                Metric("Weather", label);
            }

            Metric("Wind", ElementaPanelFormat.Wind(environment.Wind.Speed));
            Metric("Sea state",
                ElementaPanelFormat.SeaState(environment.Wind));
        }

        // -- Environment -------------------------------------------------------------

        void DrawEnvironment(ElementaPanelContext context)
        {
            if (!Section("diagnostics/environment", "Environment State", false,
                    "The canonical tick every other system reads. A stand-in is published "
                    + "when no environment world is loaded, so consumers never improvise "
                    + "their own fallbacks."))
                return;

            SolEnvironmentState environment = context.Environment;
            Metric("Source", context.World != null
                ? "SolEnvironmentWorld" : "fair-weather stand-in");
            Metric("Revision", environment.Revision.ToString());
            Metric("Simulation tick", environment.SimulationTick.ToString());
            Metric("Absolute world seconds",
                $"{environment.AbsoluteWorldSeconds:0.0}");
            Metric("Wave clock", $"{environment.WaveSeconds:0.0} s",
                "Integral of world delta against the wave-speed multiplier, so a weather "
                + "change cannot shift wave phase by the whole elapsed session.");
            Metric("World day index", environment.WorldDayIndex.ToString());
            Metric("Clock hour",
                $"{environment.ClockHour:0.000}  "
                + $"({ElementaPanelFormat.Clock(environment.ClockHour)})");

            if (context.World != null)
            {
                Metric("Fixed step",
                    $"{context.World.FixedStepSeconds * 1000d:0.0} ms");
                Metric("Climate seed", context.World.ClimateSeed.ToString());
                Metric("Paused", context.World.Paused ? "yes" : "no");
                Metric("Environment time scale",
                    $"{context.World.EnvironmentTimeScale:0.00}x");
            }

            if (context.Time != null)
                Metric("Frame deltas",
                    $"world {context.Time.WorldDeltaSeconds:0.000} s  ·  "
                    + $"{context.Time.WorldDeltaHours:0.0000} h  ·  "
                    + $"presentation {context.Time.PresentationDeltaSeconds:0.000} s");

            SolSurfaceConditionState surface = environment.Surface;
            MetricBar("Surface wetness", surface.Wetness);
            MetricBar("Snow cover", surface.SnowCover);
            Metric("Temperature", $"{surface.TemperatureCelsius:0.0} °C");
            MetricBar("Relative humidity", surface.RelativeHumidity);
        }

        // -- Weather -----------------------------------------------------------------

        void DrawWeather(ElementaPanelContext context)
        {
            if (!Section("diagnostics/weather", "Weather Channels", false,
                    "The blended state every system consumes. Nine simultaneous scalars, so "
                    + "the bars show which one is actually moving."))
                return;

            if (context.Weather == null)
            {
                Warning("No SolWeatherManager is loaded.", MessageType.Info);
                return;
            }

            SolWeatherManager manager = context.Weather;
            SolWeatherState current = manager.CurrentState;
            SolWeatherProfileAsset target = manager.TargetProfile;

            Metric("Target", target != null ? target.name : "(none)");
            MetricBar("Transition", manager.TransitionProgress,
                manager.IsTransitioning ? $"{manager.TransitionProgress:P0}" : "settled");

            MetricBar("Cloudiness", current.Cloudiness);
            MetricBar("Cloud erosion", current.CloudErosion);
            MetricBar("Rain", current.RainIntensity);
            MetricBar("Snow bias",
                Mathf.InverseLerp(-1f, 1f, current.SnowBias), current.SnowBias.ToString("0.00"));
            MetricBar("Fog boost", Mathf.Clamp01(current.FogBoost * 0.5f),
                current.FogBoost.ToString("0.00"));
            MetricBar("Mistiness", current.Mistiness);
            MetricBar("Sky obscuration", current.SkyObscuration);
            MetricBar("Dim", current.Dim);
            MetricBar("Water turbulence", current.WaterTurbulence);
            MetricBar("Lightning", current.LightningIntensity);
            Metric("Wave speed", $"{current.WaveSpeedMultiplier:0.00}x");
            Metric("Fog density floor", $"{current.FogDensityFloor:0.00000}");

            Metric("Daily fog target", $"{manager.DailyFogTarget:0.00}");
            Metric("Daily fog now", $"{manager.CurrentDailyFog:0.00}");
            Metric("Fog diurnal factor", $"{manager.FogDiurnalFactor:0.00}");
            Metric("Daily coverage offset",
                $"{manager.DailyCoverageOffset:+0.000;-0.000;0.000}");
            Note(
                "Daily fog and the coverage offset are derived from the climate seed and the "
                + "world day, so the same profile never renders identically twice.");
        }

        // -- Clouds ------------------------------------------------------------------

        void DrawClouds(ElementaPanelContext context)
        {
            if (!Section("diagnostics/clouds", "Cloud Deck", false,
                    "What the authored baseline and the weather override resolved to."))
                return;

            SolCloudController controller = SolCloudController.Active;
            SolCloudState state = controller.CurrentState;

            Metric("Dominant formation", state.DominantFormation.ToString());
            MetricBar("Coverage", state.Coverage);
            MetricBar("Erosion", state.Erosion);
            Metric("Density", $"{state.Density:0.00}");
            Metric("Base / thickness",
                $"{state.BaseHeight:0} m  ·  {state.Thickness:0} m");
            MetricBar("Vertical development", state.VerticalDevelopment);
            MetricBar("Anvil", state.AnvilAmount);
            MetricBar("Cirrus", state.CirrusAmount);
            MetricBar("Edge softness", state.EdgeSoftness);
            MetricBar("Base softness", state.BaseSoftness);
            MetricBar("Shadow strength", state.ShadowStrength);

            Vector4 blend = controller.FormationWeights;
            MetricBar("Cumulus", blend.x);
            MetricBar("Stratus", blend.y);
            MetricBar("Nimbostratus", blend.z);
            MetricBar("Cumulonimbus", blend.w);
            Note(
                "The renderer shapes density from these continuous weights, not from the "
                + "dominant-formation label, which switches hard at the midpoint of a blend.");

            Metric("Active quality", controller.Quality.ToString());
            Metric("History revision", controller.HistoryRevision.ToString(),
                "Increments whenever the temporal reconstruction is discarded.");
            Metric("Advection offsets",
                $"weather {state.WeatherOffset.x:0} / {state.WeatherOffset.y:0}   "
                + $"shape {state.ShapeOffset.x:0} / {state.ShapeOffset.y:0}");
        }

        // -- Sea ---------------------------------------------------------------------

        void DrawSea(ElementaPanelContext context)
        {
            if (!Section("diagnostics/sea", "Wind and Sea", false,
                    "Wind reaches each system through its own response lag, so these four "
                    + "numbers disagree with each other by design."))
                return;

            SolEnvironmentWindState wind = context.Environment.Wind;
            Metric("Wind", ElementaPanelFormat.Wind(wind.Speed));
            Metric("Heading",
                $"{ElementaPanelFormat.WindDegrees(wind.Direction):0}° in X/Z");
            Metric("Fog response",
                $"{wind.FogAdvectionSpeed:0.0} m/s  (20 s lag)");
            Metric("Cloud response",
                $"{wind.CloudSpeed:0.0} m/s at "
                + $"{ElementaPanelFormat.WindDegrees(wind.CloudDirection):0}°  (2 min lag)");
            Metric("Developed sea",
                $"{wind.SeaStateSpeed:0.0} m/s equivalent  (20 min lag)");
            Metric("Sea state", ElementaPanelFormat.SeaState(wind));
            MetricBar("Turbulence", wind.Turbulence);

            if (context.Water == null)
                return;

            Metric("Registered bodies",
                context.Water.Bodies != null ? context.Water.Bodies.Count.ToString() : "0");
            Metric("Wave clock", $"{context.Water.WaveTime:0.0} s");
        }

        // -- Celestial ---------------------------------------------------------------

        void DrawCelestial(ElementaPanelContext context)
        {
            if (!Section("diagnostics/celestial", "Sun, Moon and Eclipses", false,
                    "Where the bodies actually are, and how far into an eclipse the geometry "
                    + "has taken them."))
                return;

            if (context.Time == null)
            {
                Warning("No TimeOfDay authority.", MessageType.Info);
                return;
            }

            TimeOfDay time = context.Time;
            Metric("Sun",
                $"elev {ElementaPanelFormat.Elevation(time.SunDirection):0.0}°  ·  "
                + $"{ElementaPanelFormat.WindDegrees(time.SunDirection):0}° in X/Z");
            Metric("Moon",
                $"elev {ElementaPanelFormat.Elevation(time.MoonDirection):0.0}°  ·  "
                + $"{ElementaPanelFormat.WindDegrees(time.MoonDirection):0}° in X/Z");
            MetricBar("Day factor", time.DayFactor);
            Metric("Daytime now", time.IsDaytime ? "yes" : "no");
            Metric("Day length",
                ElementaPanelFormat.Duration(time.EffectiveDayRatio * 24f));

            Metric("Lunar phase",
                ElementaPanelFormat.LunarPhase(time.LunarPhase));
            MetricBar("Moon illumination", time.MoonIllumination);
            MetricBar("Lunar tide", Mathf.Clamp01(time.LunarTideFactor),
                time.LunarTideFactor.ToString("0.00"));

            MetricBar("Solar eclipse", time.SolarEclipseStrength);
            MetricBar("Lunar eclipse", time.LunarEclipseStrength);
            Metric("Tertiary planets", time.TertiaryPlanetCount.ToString());

            SolSkyFrame frame = time.CurrentSkyFrame;
            Metric("Sky revision",
                frame.IsValid ? frame.Revision.ToString() : "compatibility path");
        }

        // -- Lighting ----------------------------------------------------------------

        void DrawLighting(ElementaPanelContext context)
        {
            if (!Section("diagnostics/lighting", "Lighting Frame", false,
                    "The resolved directional and ambient state, and what the director is "
                    + "currently managing against its budget."))
                return;

            SolLightingFrame frame = SolLightingDirector.ResolveFrame();
            Metric("Revision", frame.Revision.ToString());
            Metric("Dominant light", frame.Dominant.ToString());
            MetricBar("Day factor", frame.DayFactor);
            MetricBar("Weather attenuation", frame.WeatherAttenuation);
            MetricBar("Cloud shadow strength", frame.CloudShadowStrength);
            MetricBar("Eclipse", frame.Eclipse);
            MetricBar("Lightning", frame.Lightning);

            DrawDirectional("Sun", frame.Sun);
            DrawDirectional("Moon", frame.Moon);

            Metric("Ambient sky", Describe(frame.Ambient.Sky));
            Metric("Ambient equator", Describe(frame.Ambient.Equator));
            Metric("Ambient ground", Describe(frame.Ambient.Ground));
            Note(
                "The stable ambient is the same trilight without the per-frame weather and "
                + "lightning terms, which is what probe rebakes read so a flash cannot be "
                + "baked into indirect lighting.");

            if (context.Lighting != null)
            {
                SolLightingQualitySettings settings = context.Lighting.ActiveQualitySettings;

                Metric("Managed lights",
                    $"{context.Lighting.ManagedLightCount} / {settings.ManagedLightLimit}");
                Metric("Managed shadows",
                    context.Lighting.ManagedShadowCount.ToString());
                Metric("Shadow slices",
                    $"{context.Lighting.ManagedShadowSliceCount} / {settings.ShadowSliceLimit}");
                Metric("Volumetric lights",
                    $"{context.Lighting.ManagedVolumetricLightCount} / "
                    + $"{settings.LocalVolumetricLightLimit}");
            }

            Metric("APV data",
                SolSkyLightingScheduler.IsApvDataAvailable() ? "loaded" : "unavailable");
            Metric("Atmosphere", context.Atmosphere != null
                ? context.Atmosphere.Quality.ToString() : "inactive");
            if (context.Atmosphere != null)
            {
                Metric("Fog density",
                    $"{context.Atmosphere.CurrentDensity:0.00000}");
                Metric("Directional scattering",
                    $"{context.Atmosphere.CurrentDirectionalScattering:0.000}");
            }
        }

        void DrawDirectional(string label, in SolDirectionalLightState state)
            => Metric(label,
                state.Enabled
                    ? $"{state.Intensity:0.000} at elev "
                      + $"{ElementaPanelFormat.Elevation(state.Direction):0.0}°, shadow "
                      + $"{state.ShadowStrength:0.00}"
                    : "disabled");

        static string Describe(Color color)
            => $"{color.r:0.000}, {color.g:0.000}, {color.b:0.000}";

        // -- Landscape ---------------------------------------------------------------

        void DrawLandscape(ElementaPanelContext context)
        {
            SolLandscapeDriver driver = context.Landscape;
            if (driver == null)
                return;

            if (!Section("diagnostics/landscape", "Landscape Publishing", false,
                    "Whether the terrain contract is satisfied and how much the driver is "
                    + "writing to the shader."))
                return;

            bool valid = driver.TryValidateContract(out string refusal);
            Metric("Contract", valid ? "satisfied" : "refused");
            if (!valid)
                Warning(
                    $"Nothing is published to the terrain shader: {refusal}",
                    MessageType.Warning);

            Metric("Last publish writes",
                driver.LastPublishWriteCount.ToString());
            Metric("Total writes", driver.TotalGlobalWriteCount.ToString());
            if (driver.LastPublishRefused)
                Metric("Last refusal", driver.LastRefusalReason ?? "unknown");
        }

        // -- Budget ------------------------------------------------------------------

        void DrawBudget(ElementaPanelContext context)
        {
            if (!Section("diagnostics/budget", "Last Completed Frame", false,
                    "Counters for the frame that just finished. They describe a completed "
                    + "frame rather than this one, because anything repainting to display "
                    + "them would itself be part of what it measured."))
                return;

            SolEnvironmentBudget.Counters counters = SolEnvironmentBudget.Previous;
            Metric("FFT dispatches", counters.FftDispatches.ToString(),
                "One set per cascade per frame is expected. Two sets means the spectrum is "
                + "being recorded twice.");
            Metric("Full-res colour copies",
                counters.FullResColorCopies.ToString());
            Metric("Environment updates",
                counters.EnvironmentUpdates.ToString());
            Metric("Atmosphere pushes",
                counters.AtmosphereGlobalPushes.ToString());
            Metric("Lighting tier", counters.HasLightingDirector
                ? ((SolLightingQualityTier)counters.ActiveLightingTier).ToString()
                : "no director");
            Metric("Lights registered / active",
                $"{counters.RegisteredLights} / {counters.ActiveLights}");
            Metric("Shadow slices", counters.ShadowSlices.ToString());
            Metric("Volumetric lights", counters.VolumetricLights.ToString());
            Metric("GI requests", counters.GiRequests.ToString());
            Metric("Probe requests / done",
                $"{counters.ProbeRequests} / {counters.ProbeCompletions}");
            Metric("Camera contexts", counters.CameraContexts.ToString());
            Metric("Camera history contexts",
                SolEnvironmentCameraRegistry.Count.ToString());
            Metric("SSR history",
                $"{counters.SsrHistoryBytes / (1024f * 1024f):0.0} MB");


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


        readonly Dictionary<string, VisualElement> _sections = new();
        readonly Dictionary<string, VisualElement> _outputs = new();
        VisualElement _section;
        string _sectionId;

        protected override void Build(ElementaPanelContext context, VisualElement root)
        {
            _sections.Clear(); _outputs.Clear();
            UI.Issues(this, root);
            RefreshValues(context);
            Track(() => RefreshValues(context));
            UI.Button(_sections["diagnostics/budget"], "Log Frame Budget", LogBudget);
            BuildTools(context, root);
        }

        // Only values are refreshed. Each native control is created once per page binding.
        bool Section(string key, string title, bool open, string explanation)
        {
            _sectionId = key;
            if (!_sections.TryGetValue(key, out _section))
            {
                _section = UI.Foldout(this, Root, key, title, open, explanation);
                _sections.Add(key, _section);
            }
            return true;
        }
        void Metric(string label, string value, string tooltip = null)
        {
            string key = _sectionId + "/" + label;
            if (!_outputs.TryGetValue(key, out var control))
            {
                var row = UI.Row(_section); row.AddToClassList("elementa-metric");
                var name = new Label(label) { tooltip = tooltip }; name.AddToClassList("elementa-metric-name"); row.Add(name);
                control = new Label(); control.AddToClassList("elementa-metric-value"); row.Add(control);
                _outputs.Add(key, control);
            }
            var output = (Label)control;
            if (output.text != value) output.text = value;
        }
        void MetricBar(string label, float value, string description = null)
        {
            string key = _sectionId + "/" + label;
            if (!_outputs.TryGetValue(key, out var control))
            {
                control = new ProgressBar { lowValue = 0, highValue = 1 }; control.AddToClassList("elementa-metric-bar");
                _section.Add(control); _outputs.Add(key, control);
            }
            var bar = (ProgressBar)control;
            bar.value = Mathf.Clamp01(value); bar.title = label + "  " + (description ?? value.ToString("P0"));
        }
        void Note(string message)
        {
            string key = _sectionId + "/note/" + message;
            if (!_outputs.ContainsKey(key)) _outputs.Add(key, UI.Note(_section, message));
        }
        void Warning(string message, MessageType type) => Metric("Status", message);

        void BuildTools(ElementaPanelContext context, VisualElement root)
        {
            var section = UI.Foldout(this, root, "diagnostics/tools", "Project tools");
            UI.Note(section, "Validation, migration and baking actions operate on the project.");
            UI.Button(section, "Open Visual Capture Matrix", () =>
            {
                var window = EditorWindow.GetWindow<SolSkyVisualPass>();
                window.titleContent = new GUIContent("Sol Sky Visual Pass"); window.Show();
            });
            UI.Button(section, "Sync Weather Selection Lists", () => ElementaPanelActions.Tool(context, ElementaPanelDoctor.SyncSelectionLists));
            UI.Button(section, "Migrate Inline Weather Profiles", () => ElementaPanelActions.Tool(context, SolWeatherProfileMigration.MigrateInlineProfilesToAssets));
            var migrate = UI.Button(section, "Migrate Selected Time Of Day", () =>
                EditorApplication.ExecuteMenuItem("Tools/Elementa/Migrate Selected Time Of Day"));
            Track(() => migrate.SetEnabled(Selection.activeGameObject != null && Selection.activeGameObject.GetComponent<TimeOfDay>() != null));
            UI.Button(section, "Bake Cloud Noise Textures", () => ElementaPanelActions.Tool(context, SolCloudNoiseBaker.Bake));
            UI.Button(section, "Bake Stellar Backdrop", () => ElementaPanelActions.Tool(context, SolStellarBackdropBaker.BakeDefault));
            UI.Button(section, "Bake Landscape Layer Arrays", () => ElementaPanelActions.Tool(context, () =>
                ElementaPanelDoctor.BakeLandscape(context.Landscape != null ? context.Landscape.config : null)));
        }
    }
}
