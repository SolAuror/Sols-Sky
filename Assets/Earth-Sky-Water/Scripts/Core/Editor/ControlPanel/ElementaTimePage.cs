using Sol.ToD;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// The world clock and calendar, and the celestial state they resolve to.
    ///
    /// Every edit here goes through TimeChangeRequest or the calendar's own mutators rather
    /// than writing the serialized hour, so the panel produces exactly the events a save
    /// system, an NPC schedule or a sleep transition would see at runtime. A panel that set
    /// the field directly would be able to move time in a way the game never can.
    /// </summary>
    sealed class ElementaTimePage : ElementaPanelPage
    {
        SerializedObject _serializedTime;

        public override string Title => "Time";

        public override string Subtitle =>
            "Clock, cycle timing and calendar. Scrubbing issues the same time-change requests "
            + "gameplay does, so sun, moon, fog, weather and water all follow.";

        public override void Draw(ElementaPanelContext context)
        {
            if (context.Time == null)
            {
                EditorGUILayout.HelpBox(
                    "No TimeOfDay authority. Assign one on the Overview page.",
                    MessageType.Warning);
                return;
            }

            DrawClock(context);
            DrawFlow(context);
            DrawCalendar(context);
            DrawCelestial(context);
        }

        // -- Clock -------------------------------------------------------------------

        void DrawClock(ElementaPanelContext context)
        {
            if (!context.Section("time/clock", "Clock"))
                return;

            TimeOfDay time = context.Time;
            float hour = time.ClockHour;

            EditorGUI.BeginChangeCheck();
            float changed = EditorGUILayout.Slider(
                $"Clock  {ElementaPanelFormat.Clock(hour)}", hour, 0f, 23.999f);
            if (EditorGUI.EndChangeCheck())
                Apply(context, TimeChangeRequest.SetClockHour(
                    changed, context.Window, "Control Panel Scrub"));

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("-1h"))
                    Apply(context, TimeChangeRequest.RewindHours(
                        1f, context.Window, "Control Panel"));
                if (GUILayout.Button("-15m"))
                    Apply(context, TimeChangeRequest.RewindHours(
                        0.25f, context.Window, "Control Panel"));
                if (GUILayout.Button("+15m"))
                    Apply(context, TimeChangeRequest.AdvanceHours(
                        0.25f, context.Window, "Control Panel"));
                if (GUILayout.Button("+1h"))
                    Apply(context, TimeChangeRequest.AdvanceHours(
                        1f, context.Window, "Control Panel"));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Sunrise"))
                    Skip(context, () => context.Time.SkipToNextSunrise());
                if (GUILayout.Button("Noon"))
                    Skip(context, () => context.Time.SetNoon());
                if (GUILayout.Button("Sunset"))
                    Skip(context, () => context.Time.SkipToNextSunset());
                if (GUILayout.Button("Midnight"))
                    Skip(context, () => context.Time.SetMidnight());
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("<< Previous day"))
                    Skip(context, () => context.Time.SkipBackwardOneDay());
                if (GUILayout.Button("Next day >>"))
                    Skip(context, () => context.Time.SkipForwardOneDay());
            }

            ElementaPanelGui.Note(
                "Day skips move world chronology backwards and forwards; forward-only player "
                + "time keeps accumulating either way.");
        }

        // -- Flow --------------------------------------------------------------------

        void DrawFlow(ElementaPanelContext context)
        {
            if (!context.Section("time/flow", "Cycle Timing"))
                return;

            TimeOfDay time = context.Time;

            // Live controls, not authored data: these go through the API so the pause and
            // time-scale events reach everything listening, and neither dirties the scene.
            EditorGUI.BeginChangeCheck();
            bool paused = EditorGUILayout.ToggleLeft("Paused", time.Paused);
            float scale = EditorGUILayout.Slider("Time scale", time.TimeScale, 0f, 20f);
            if (EditorGUI.EndChangeCheck())
            {
                time.SetPaused(paused);
                time.SetTimeScale(scale);
                context.Refresh();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("0.5x", GUILayout.Width(50f)))
                    SetScale(context, 0.5f);
                if (GUILayout.Button("1x", GUILayout.Width(50f)))
                    SetScale(context, 1f);
                if (GUILayout.Button("5x", GUILayout.Width(50f)))
                    SetScale(context, 5f);
                if (GUILayout.Button("20x", GUILayout.Width(50f)))
                    SetScale(context, 20f);
                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space(4f);

            // Authored data: written through SerializedObject so it is undoable and marks
            // the scene dirty, which the property setters do not.
            SerializedObject serialized = ResolveSerializedTime(time);
            serialized.Update();
            EditorGUI.BeginChangeCheck();

            // Plain controls rather than PropertyField: TimeOfDay carries its own [Header]
            // decorators on these fields, and PropertyField draws them, so the page ended up
            // printing "-- Cycle Timing ---" underneath its own Cycle Timing heading.
            SerializedProperty cycle = serialized.FindProperty("cycleDurationMinutes");
            if (cycle != null)
                cycle.floatValue = Mathf.Max(0.1f, EditorGUILayout.FloatField(
                    new GUIContent("Cycle duration (min)",
                        "Real-time minutes for one full day/night cycle."),
                    cycle.floatValue));

            SerializedProperty ratio = serialized.FindProperty("dayRatio");
            if (ratio != null)
                ratio.floatValue = EditorGUILayout.Slider(
                    new GUIContent("Day ratio",
                        "Fraction of the civil day that is visually daytime, centred on noon."),
                    ratio.floatValue, 0.05f, 0.95f);

            SerializedProperty seasons = serialized.FindProperty("enableSeasons");
            if (seasons != null)
                seasons.boolValue = EditorGUILayout.Toggle(
                    new GUIContent("Seasonal day length",
                        "Vary the day ratio with the calendar's position in the year."),
                    seasons.boolValue);

            SerializedProperty variation = serialized.FindProperty("seasonalDayVariation");
            if (variation != null)
                variation.floatValue = EditorGUILayout.Slider(
                    new GUIContent("Seasonal variation",
                        "How far the day ratio swings across the seasons."),
                    variation.floatValue, 0f, 0.4f);

            SerializedProperty animate = serialized.FindProperty("animateInEditMode");
            if (animate != null)
                animate.boolValue = EditorGUILayout.Toggle(
                    new GUIContent("Animate in edit mode",
                        "Advance the clock without entering play mode. Keeps the scene dirty "
                        + "while on, because it moves the sun and moon transforms."),
                    animate.boolValue);

            if (EditorGUI.EndChangeCheck())
            {
                serialized.ApplyModifiedProperties();
                context.Refresh();
            }

            ElementaPanelGui.Metric("Effective day ratio", $"{time.EffectiveDayRatio:0.000}");
            ElementaPanelGui.Metric("Day length",
                ElementaPanelFormat.Duration(time.EffectiveDayRatio * 24f));
            ElementaPanelGui.Metric("World delta",
                $"{time.WorldDeltaSeconds:0.000} s  ·  {time.WorldDeltaHours:0.0000} h");
        }

        void SetScale(ElementaPanelContext context, float scale)
        {
            context.Time.SetTimeScale(scale);
            context.Refresh();
        }

        // -- Calendar ----------------------------------------------------------------

        void DrawCalendar(ElementaPanelContext context)
        {
            if (!context.Section("time/calendar", "Calendar"))
                return;

            Calendar calendar = context.Time.Calendar;
            if (calendar == null)
            {
                EditorGUILayout.HelpBox(
                    "This TimeOfDay has no Calendar, so date, season and world-day chronology "
                    + "are unavailable. The clock still runs.",
                    MessageType.Info);
                return;
            }

            // Day and month are 1-based everywhere in Calendar: AdvanceDay wraps to 1 and
            // GetMonthName subtracts one. Zeroes mean the serialized fields have never been
            // through ResetToStart, which only runs at Start, so an edit-mode scene that has
            // never been played reads as a date that does not exist.
            bool initialised = calendar.Day >= 1 && calendar.Month >= 1;
            if (!initialised)
            {
                EditorGUILayout.HelpBox(
                    "This calendar has never been initialised - day and month are still 0, "
                    + "which is not a real date. Season, day-of-year and every seasonal weight "
                    + "are resolving against month 0 until it is.",
                    MessageType.Warning);
                if (ElementaPanelGui.ActionButton("Initialise to start date",
                        "Run the calendar's own reset, the same one a new game does.",
                        true, 180f))
                {
                    Undo.RecordObject(calendar, "Initialise Elementa calendar");
                    calendar.ResetToStart();
                    EditorUtility.SetDirty(calendar);
                    context.RefreshLayout();
                }
            }

            ElementaPanelGui.Metric("Date", calendar.DateString);
            ElementaPanelGui.Metric("Season",
                $"{calendar.CurrentSeason}  ·  {calendar.SeasonProgress:P0} through");
            ElementaPanelGui.MetricBar("Year progress", calendar.YearProgress,
                $"{calendar.DayOfYear}/{calendar.DaysPerYear}");

            if (!initialised)
                return;

            EditorGUILayout.Space(4f);
            int day = calendar.Day;
            int month = calendar.Month;
            int year = calendar.Year;
            EditorGUI.BeginChangeCheck();
            day = EditorGUILayout.IntSlider("Day", day, 1, Mathf.Max(1, calendar.GetDaysInMonth(month)));
            month = EditorGUILayout.IntSlider("Month", month, 1, Mathf.Max(1, calendar.MonthsPerYear));
            year = EditorGUILayout.IntField("Year", year);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(calendar, "Set Elementa date");
                calendar.SetDate(day, month, year);
                EditorUtility.SetDirty(calendar);
                context.Refresh();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("-30 days"))
                    AdvanceDays(context, calendar, -30);
                if (GUILayout.Button("-7 days"))
                    AdvanceDays(context, calendar, -7);
                if (GUILayout.Button("+7 days"))
                    AdvanceDays(context, calendar, 7);
                if (GUILayout.Button("+30 days"))
                    AdvanceDays(context, calendar, 30);
            }

            ElementaPanelGui.Note(
                "Jumping a month is the quickest way to see a profile under a different "
                + "season: seasonal day length, the fog range and the selection weights all "
                + "key off the calendar rather than off the clock.");

            EditorGUILayout.Space(2f);
            ElementaPanelGui.Metric("World day index", calendar.WorldDayIndex.ToString());
            ElementaPanelGui.Metric("Player days elapsed", $"{calendar.PlayerDaysElapsed:0.000}");
            ElementaPanelGui.Metric("Month name", calendar.MonthName);
            ElementaPanelGui.Metric("Average month", $"{calendar.AverageMonthLength:0.0} days");
        }

        static void AdvanceDays(ElementaPanelContext context, Calendar calendar, int days)
        {
            Undo.RecordObject(calendar, "Advance Elementa calendar");
            if (days >= 0)
                calendar.AdvanceDays(days);
            else
                calendar.RewindDays(-days);
            EditorUtility.SetDirty(calendar);
            context.Refresh();
        }

        // -- Celestial ---------------------------------------------------------------

        void DrawCelestial(ElementaPanelContext context)
        {
            if (!context.Section("time/celestial", "Sun, Moon and Eclipses", false))
                return;

            TimeOfDay time = context.Time;

            ElementaPanelGui.Metric("Sun",
                $"elev {ElementaPanelFormat.Elevation(time.SunDirection):0.0}°  ·  "
                + $"heading {ElementaPanelFormat.WindDegrees(time.SunDirection):0}°");
            ElementaPanelGui.Metric("Moon",
                $"elev {ElementaPanelFormat.Elevation(time.MoonDirection):0.0}°  ·  "
                + $"heading {ElementaPanelFormat.WindDegrees(time.MoonDirection):0}°");
            ElementaPanelGui.MetricBar("Day factor", time.DayFactor);
            ElementaPanelGui.Metric("Sunrise / sunset",
                $"{ElementaPanelFormat.Clock(time.SunriseClockHour)} - "
                + $"{ElementaPanelFormat.Clock(time.SunsetClockHour)}");
            ElementaPanelGui.Metric("Daytime now", time.IsDaytime ? "yes" : "no");

            EditorGUILayout.Space(2f);
            ElementaPanelGui.Metric("Lunar phase", ElementaPanelFormat.LunarPhase(time.LunarPhase));
            ElementaPanelGui.MetricBar("Moon illumination", time.MoonIllumination);
            ElementaPanelGui.MetricBar("Lunar tide", Mathf.Clamp01(time.LunarTideFactor),
                time.LunarTideFactor.ToString("0.00"));

            EditorGUILayout.Space(2f);
            ElementaPanelGui.MetricBar("Solar eclipse", time.SolarEclipseStrength);
            ElementaPanelGui.MetricBar("Lunar eclipse", time.LunarEclipseStrength);
            if (time.IsEclipse)
                EditorGUILayout.HelpBox(
                    "An eclipse is in progress, so sky, ambient and fog are on their eclipse "
                    + "anchors rather than their day or night ones.",
                    MessageType.Info);

            ElementaPanelGui.Metric("Tertiary planets", time.TertiaryPlanetCount.ToString());
            ElementaPanelGui.Note(
                "Eclipses are geometric: they need the lunar and nodal phases to line up, so "
                + "stepping days is how you find one rather than scrubbing hours.");
        }

        // -- Shared ------------------------------------------------------------------

        SerializedObject ResolveSerializedTime(TimeOfDay time)
        {
            if (_serializedTime == null || _serializedTime.targetObject != time)
                _serializedTime = new SerializedObject(time);
            return _serializedTime;
        }

        static void Apply(ElementaPanelContext context, TimeChangeRequest request)
        {
            context.Time.ApplyTimeChange(request);
            context.Refresh();
        }

        static void Skip(ElementaPanelContext context, System.Action action)
        {
            action.Invoke();
            context.Refresh();
        }
    }
}
