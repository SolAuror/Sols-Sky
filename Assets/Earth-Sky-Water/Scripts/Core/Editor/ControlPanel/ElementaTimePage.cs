using Sol.ToD;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UI = Sol.Environment.EditorTools.ElementaPanelGui;

namespace Sol.Environment.EditorTools
{
    sealed class ElementaTimePage : ElementaPanelPage
    {
        public override string Title => "Time";
        public override string Subtitle => "Set the moment, day length and calendar.";
        protected override void Build(ElementaPanelContext context, VisualElement root)
        {
            var time = context.Time;
            if (time == null) { UI.Help(root, "Assign Time of Day in Scene setup."); return; }
            var clock = UI.Section(root, "Clock", "Live control · " + UI.Describe(time));
            UI.Metric(this, clock, "Time", () => ElementaPanelFormat.Clock(time.ClockHour));
            var step = UI.Row(clock);
            foreach (float hours in new[] { -1f, -.25f, .25f, 1f })
            {
                float amount = hours;
                UI.Button(step, hours == -1 ? "−1 h" : hours == -.25f ? "−15 min" : hours == .25f ? "+15 min" : "+1 h", () =>
                { time.ApplyTimeChange(amount < 0 ? TimeChangeRequest.RewindHours(-amount, context.Window, "Elementa") : TimeChangeRequest.AdvanceHours(amount, context.Window, "Elementa")); context.Refresh(); });
            }
            var anchors = UI.Row(clock);
            UI.Button(anchors, "Sunrise", () => { time.SkipToNextSunrise(); context.Refresh(); });
            UI.Button(anchors, "Noon", () => { time.SetNoon(); context.Refresh(); });
            UI.Button(anchors, "Sunset", () => { time.SkipToNextSunset(); context.Refresh(); });
            UI.Button(anchors, "Midnight", () => { time.SetMidnight(); context.Refresh(); });
            var days = UI.Row(clock);
            UI.Button(days, "Previous day", () => { time.SkipBackwardOneDay(); context.Refresh(); });
            UI.Button(days, "Next day", () => { time.SkipForwardOneDay(); context.Refresh(); });
            UI.Metric(this, clock, "Daylight", () => ElementaPanelFormat.Clock(time.SunriseClockHour) + "–" + ElementaPanelFormat.Clock(time.SunsetClockHour));
            var cycle = UI.Section(root, "Day length", "Scene setting · " + UI.Describe(time));
            UI.Fields(this, cycle, time, "cycleDurationMinutes|Cycle duration (minutes)", "dayRatio|Daylight fraction", "enableSeasons|Seasonal day length");
            UI.Metric(this, cycle, "Effective daylight", () => ElementaPanelFormat.Duration(time.EffectiveDayRatio * 24));
            var seasonal = UI.Foldout(this, root, "time/seasonal", "Seasonal variation");
            UI.Fields(this, seasonal, time, "seasonalDayVariation|Daylight variation");
            BuildCalendar(context, root);
            var live = UI.Foldout(this, root, "time/flow", "Simulation controls", note: "Live pause and speed are not saved. Edit-mode animation is a scene setting.");
            var paused = new Toggle("Pause clock"); live.Add(paused);
            paused.RegisterValueChangedCallback(e => { time.SetPaused(e.newValue); context.Refresh(); });
            Track(() => paused.SetValueWithoutNotify(time.Paused));
            var speed = new Slider("Time scale", 0, 20) { showInputField = true }; speed.AddToClassList("elementa-field"); live.Add(speed);
            speed.RegisterValueChangedCallback(e => { time.SetTimeScale(e.newValue); context.Refresh(); });
            Track(() => { if (!Editing(speed)) speed.SetValueWithoutNotify(time.TimeScale); });
            var speeds = UI.Row(live);
            foreach (float value in new[] { .5f, 1f, 5f, 20f }) { float s = value; UI.Button(speeds, s + "×", () => { time.SetTimeScale(s); context.Refresh(); }); }
            UI.Fields(this, live, time, "animateInEditMode|Animate in edit mode");
            var orbits = UI.Foldout(this, root, "time/orbits", "Orbits and eclipses", note: "Scene settings · determines when eclipses occur.");
            UI.Fields(this, orbits, time, "lunarTiltDegrees|Lunar orbit tilt (°)", "initialLunarPhase|Starting lunar phase", "initialNodalPhase|Starting nodal phase", "nodalPrecessionDays|Nodal precession (days)", "eclipseThresholdDegrees|Eclipse threshold (°)", "eclipsePhaseWindow", "eclipseApexBias");
            UI.Advanced(this, root, "time/component/timeofday", "Advanced · clock and scene settings", time, "skyProfile", "cloudQuality");
            UI.Note(root, "Compatibility appearance is read-only while a sky profile is assigned. Current clock and calendar outputs are read-only; use the live commands above.");
            UI.Advanced(this, root, "time/component/calendar", "Advanced · calendar configuration", context.Calendar);
        }

        void BuildCalendar(ElementaPanelContext context, VisualElement root)
        {
            var calendar = context.Calendar;
            if (calendar == null) { UI.Help(root, "No calendar is assigned. The clock remains available."); return; }
            var section = UI.Section(root, "Calendar", "Live control · " + UI.Describe(calendar));
            UI.Metric(this, section, "Date", () => calendar.DateString);
            UI.Metric(this, section, "Season", () => calendar.CurrentSeason + " · " + calendar.SeasonProgress.ToString("P0"));
            UI.MetricBar(this, section, "Year", () => calendar.YearProgress, () => calendar.DayOfYear + "/" + calendar.DaysPerYear);
            if (calendar.Day < 1 || calendar.Month < 1)
            {
                UI.Button(section, "Initialize to start date", () => { Undo.RecordObject(calendar, "Initialize Elementa calendar"); calendar.ResetToStart(); EditorUtility.SetDirty(calendar); PrefabUtility.RecordPrefabInstancePropertyModifications(calendar); context.RefreshLayout(); });
                return;
            }
            var day = new IntegerField("Day") { isDelayed = true };
            var month = new IntegerField("Month") { isDelayed = true };
            var year = new IntegerField("Year") { isDelayed = true };
            foreach (var field in new[] { day, month, year }) { field.AddToClassList("elementa-field"); section.Add(field); }
            void SetDate()
            {
                int m = Mathf.Clamp(month.value, 1, calendar.MonthsPerYear);
                int d = Mathf.Clamp(day.value, 1, calendar.GetDaysInMonth(m));
                Undo.RecordObject(calendar, "Set Elementa date"); calendar.SetDate(d, m, year.value); EditorUtility.SetDirty(calendar); PrefabUtility.RecordPrefabInstancePropertyModifications(calendar); context.Refresh();
            }
            day.RegisterValueChangedCallback(_ => SetDate()); month.RegisterValueChangedCallback(_ => SetDate()); year.RegisterValueChangedCallback(_ => SetDate());
            Track(() => { if (!Editing(day)) day.SetValueWithoutNotify(calendar.Day); if (!Editing(month)) month.SetValueWithoutNotify(calendar.Month); if (!Editing(year)) year.SetValueWithoutNotify(calendar.Year); });
            var row = UI.Row(section);
            foreach (int value in new[] { -30, -7, 7, 30 })
            {
                int days = value;
                UI.Button(row, (days > 0 ? "+" : "") + days + " days", () =>
                { Undo.RecordObject(calendar, "Change Elementa date"); if (days > 0) calendar.AdvanceDays(days); else calendar.RewindDays(-days); EditorUtility.SetDirty(calendar); PrefabUtility.RecordPrefabInstancePropertyModifications(calendar); context.Refresh(); });
            }
        }
        internal static bool Editing(VisualElement field) => field.panel?.focusController?.focusedElement is VisualElement focus && (focus == field || field.Contains(focus));
    }
}
