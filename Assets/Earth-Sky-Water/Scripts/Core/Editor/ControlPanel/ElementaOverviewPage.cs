using System.Collections.Generic;
using Sol.ToD;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Which scene the panel is authoring, what that scene is doing right now, and what is
    /// wrong with it.
    ///
    /// The health check is the reason this page leads. Every other page assumes the stack is
    /// wired up; this is the only one that says when it is not, and it is the difference
    /// between "the sky looks wrong" and "the atmosphere feature was never installed".
    /// </summary>
    sealed class ElementaOverviewPage : ElementaPanelPage
    {
        public override string Title => "Overview";

        public override string Subtitle =>
            "The authorities this panel edits, the state they are publishing, and the "
            + "configuration checks for the scene and project.";

        public override int Badge(ElementaPanelContext context)
            => ElementaPanelDoctor.Count(context.Issues, MessageType.Error)
             + ElementaPanelDoctor.Count(context.Issues, MessageType.Warning);

        public override void Draw(ElementaPanelContext context)
        {
            DrawAuthorities(context);
            DrawLiveState(context);
            DrawHealth(context);
        }

        // -- Authorities -------------------------------------------------------------

        void DrawAuthorities(ElementaPanelContext context)
        {
            if (!context.Section("overview/authorities", "Scene Authorities"))
                return;

            TimeOfDay previousTime = context.Time;
            SolWeatherManager previousWeather = context.Weather;

            context.State.timeOfDay = (TimeOfDay)EditorGUILayout.ObjectField(
                "Time of Day", context.State.timeOfDay, typeof(TimeOfDay), true);
            context.State.weatherManager = (SolWeatherManager)EditorGUILayout.ObjectField(
                "Weather", context.State.weatherManager, typeof(SolWeatherManager), true);

            if (previousWeather != context.Weather)
            {
                // A preview belongs to the manager it was applied to. Repointing the panel
                // without releasing it would leave the old manager blended forever.
                context.ClearWeatherPreview();
                context.State.weatherA = null;
                context.State.weatherB = null;
                context.State.windInitialized = false;
                context.State.inspectedWeatherIndex = -1;
            }

            if (previousTime != context.Time)
                context.RefreshLayout();

            if (!context.HasAuthorities)
            {
                EditorGUILayout.HelpBox(
                    "Open a scene with TimeOfDay and SolWeatherManager, or assign them above.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(4f);
            ElementaPanelGui.Note("Resolved automatically from the loaded scenes:");
            ElementaPanelGui.ObjectRow("Environment World", context.World);
            ElementaPanelGui.ObjectRow("Water World", context.Water);
            ElementaPanelGui.ObjectRow("Lighting Director", context.Lighting);
            ElementaPanelGui.ObjectRow("Atmosphere", context.Atmosphere);
            ElementaPanelGui.ObjectRow("Coordinator", context.Coordinator);
            ElementaPanelGui.ObjectRow("Landscape Driver", context.Landscape);

            EditorGUILayout.Space(4f);
            ElementaPanelGui.Note("Authored assets in use:");
            ElementaPanelGui.ObjectRow("Sky Profile",
                context.Time != null ? context.Time.SkyProfile : null);
            ElementaPanelGui.ObjectRow("Weather Profile",
                context.Weather != null ? context.Weather.TargetProfile : null);
            ElementaPanelGui.ObjectRow("Water Profile",
                context.Water != null ? context.Water.DefaultProfile : null);
            ElementaPanelGui.ObjectRow("Water Quality",
                context.Water != null ? context.Water.QualityProfile : null);
            ElementaPanelGui.ObjectRow("Lighting Quality",
                context.Lighting != null ? context.Lighting.QualityProfile : null);
        }

        // -- Live state --------------------------------------------------------------

        void DrawLiveState(ElementaPanelContext context)
        {
            if (!context.Section("overview/now", "Right Now"))
                return;

            SolEnvironmentState environment = context.Environment;

            if (context.Time != null)
            {
                Calendar calendar = context.Time.Calendar;
                ElementaPanelGui.Metric("Clock", ElementaPanelFormat.Clock(context.Time.ClockHour));
                ElementaPanelGui.Metric("Date", calendar != null
                    ? $"{calendar.DateString}  ·  {calendar.CurrentSeason}"
                    : "no calendar");
                ElementaPanelGui.Metric("Daylight",
                    $"{ElementaPanelFormat.Clock(context.Time.SunriseClockHour)} - "
                    + $"{ElementaPanelFormat.Clock(context.Time.SunsetClockHour)}");
            }
            else
            {
                ElementaPanelGui.Metric("Clock",
                    ElementaPanelFormat.Clock(environment.ClockHour) + " (stand-in)");
            }

            if (context.Weather != null)
            {
                SolWeatherProfileAsset target = context.Weather.TargetProfile;
                string weatherLabel = target != null ? target.name : "(none)";
                if (context.Weather.IsTransitioning)
                    weatherLabel += $"  ·  blending {context.Weather.TransitionProgress:P0}";
                if (context.HasWeatherPreview)
                    weatherLabel += "  ·  PREVIEW";
                ElementaPanelGui.Metric("Weather", weatherLabel);
            }

            ElementaPanelGui.Metric("Wind", ElementaPanelFormat.Wind(environment.Wind.Speed));
            ElementaPanelGui.Metric("Sea state", ElementaPanelFormat.SeaState(environment.Wind));

            EditorGUILayout.Space(2f);
            SolEnvironmentWeatherState weather = environment.Weather;
            ElementaPanelGui.MetricBar("Cloudiness", weather.Cloudiness);
            ElementaPanelGui.MetricBar("Rain", weather.Rain);
            ElementaPanelGui.MetricBar("Snow", weather.Snow);
            ElementaPanelGui.MetricBar("Fog boost", Mathf.Clamp01(weather.FogBoost * 0.5f),
                weather.FogBoost.ToString("0.00"));
            ElementaPanelGui.MetricBar("Water turbulence", weather.WaterTurbulence);

            EditorGUILayout.Space(2f);
            SolSurfaceConditionState surface = environment.Surface;
            ElementaPanelGui.Metric("Surface",
                $"wetness {surface.Wetness:0.00}   snow {surface.SnowCover:0.00}   "
                + $"{surface.TemperatureCelsius:0.0} °C   RH {surface.RelativeHumidity:P0}");
        }

        // -- Health ------------------------------------------------------------------

        void DrawHealth(ElementaPanelContext context)
        {
            List<ElementaIssue> issues = context.Issues;
            int errors = ElementaPanelDoctor.Count(issues, MessageType.Error);
            int warnings = ElementaPanelDoctor.Count(issues, MessageType.Warning);
            string summary = errors == 0 && warnings == 0
                ? "Health Check - nothing blocking"
                : $"Health Check - {errors} blocking, {warnings} to review";

            if (!context.Section("overview/health", summary))
                return;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (ElementaPanelGui.ActionButton("Re-check now",
                        "Re-run every scene and project check immediately.", true, 110f))
                {
                    context.Resolve(force: true);
                    context.InvalidateIssues();
                    context.RefreshLayout();
                }

                if (ElementaPanelGui.ActionButton("Add System Manager",
                        "Instantiate the prefab carrying TimeOfDay, Calendar, weather, water, "
                        + "atmosphere and the coordinator.",
                        ElementaPanelDoctor.HasSystemManagerPrefab, 150f))
                {
                    ElementaPanelDoctor.InstantiateSystemManager();
                    context.Resolve(force: true);
                    context.InvalidateIssues();
                    context.RefreshLayout();
                }
            }

            if (issues.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Authorities, render pipeline, authored assets and debug state all check out.",
                    MessageType.Info);
                return;
            }

            for (int i = 0; i < issues.Count; i++)
            {
                ElementaIssue issue = issues[i];
                EditorGUILayout.HelpBox(issue.Message, issue.Severity);
                if (issue.Fix == null)
                    continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(8f);
                    if (GUILayout.Button(issue.FixLabel, GUILayout.Width(170f)))
                    {
                        issue.Fix.Invoke();
                        context.Resolve(force: true);
                        context.InvalidateIssues();
                        context.RefreshLayout();
                    }

                    GUILayout.FlexibleSpace();
                }
            }
        }
    }
}
