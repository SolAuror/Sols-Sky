using System;
using TMPro;
using UnityEngine;

using Sol.ToD;

/// <summary>
/// Live text feedback for the demo scene's calendar and time controls.
/// The default templates mirror the placeholder text authored in the scene.
/// </summary>
public class DemoMenuController : MonoBehaviour
{
    [Header("Calendar")]
    [SerializeField] TMP_Text calendarDateText;
    [SerializeField] string calendarDateFormat = "Dayname the xx[nd/nth] of MonthName[Monthnumber], Year xxxx";
    [Tooltip("The core calendar stores dates, but not a weekday. Index 0 is the authored calendar start date.")]
    [SerializeField] string[] weekdayNames = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
    [SerializeField, Range(0, 6)] int startingWeekdayIndex;

    [Header("Time")]
    [SerializeField] TMP_Text realTimeText;
    [SerializeField] string realTimeFormat = "Real Time HH:mm:ss";
    [SerializeField] TMP_Text gameTimeText;
    [SerializeField] string gameTimeFormat = "HH:mm:ss Game Time";
    [SerializeField] TMP_Text daysElapsedText;
    [SerializeField] string daysElapsedFormat = "xx Days Elapsed";

    TimeOfDay timeOfDay;
    Calendar calendar;
    float referenceRetryTimer;
    float displayRefreshTimer;

    void Start()
    {
        ResolveReferences();
        RefreshDisplay();
    }

    void Update()
    {
        ResolveReferences();
        displayRefreshTimer -= Time.unscaledDeltaTime;
        if (displayRefreshTimer <= 0f)
        {
            displayRefreshTimer = 0.1f;
            RefreshDisplay();
        }
    }

    void ResolveReferences()
    {
        referenceRetryTimer -= Time.unscaledDeltaTime;
        bool missingReference = timeOfDay == null || calendar == null
            || calendarDateText == null || realTimeText == null
            || gameTimeText == null || daysElapsedText == null;
        if (referenceRetryTimer > 0f && missingReference)
            return;

        if (timeOfDay == null)
            timeOfDay = TimeOfDay.ResolveInstance();

        if (calendar == null && timeOfDay != null)
            calendar = timeOfDay.Calendar;

        if (calendarDateText == null)
            calendarDateText = FindText("CalendarDateText");
        if (realTimeText == null)
            realTimeText = FindText("RealTime");
        if (gameTimeText == null)
            gameTimeText = FindText("GameTime");
        if (daysElapsedText == null)
            daysElapsedText = FindText("xx Days Elapsed");

        if (missingReference && (timeOfDay == null || calendar == null
            || calendarDateText == null || realTimeText == null
            || gameTimeText == null || daysElapsedText == null))
            referenceRetryTimer = 0.5f;
    }

    void RefreshDisplay()
    {
        if (timeOfDay == null || calendar == null)
            return;

        if (calendarDateText != null)
        {
            string weekday = GetWeekday(calendar.WorldDayIndex);
            string ordinal = GetOrdinal(calendar.Day);
            string dateText = calendarDateFormat
                .Replace("Dayname", weekday)
                .Replace("xx[nd/nth]", ordinal)
                .Replace("MonthName[Monthnumber]", $"{calendar.MonthName}[{calendar.Month}]")
                .Replace("xxxx", calendar.Year.ToString());
            if (calendarDateText.text != dateText)
                calendarDateText.text = dateText;
        }

        if (realTimeText != null)
        {
            string realTime = realTimeFormat.Replace("HH:mm:ss", DateTime.Now.ToString("HH:mm:ss"));
            if (realTimeText.text != realTime)
                realTimeText.text = realTime;
        }

        if (gameTimeText != null)
        {
            string gameTime = gameTimeFormat.Replace("HH:mm:ss", FormatGameTime(timeOfDay.ClockHour));
            if (gameTimeText.text != gameTime)
                gameTimeText.text = gameTime;
        }

        if (daysElapsedText != null)
        {
            string daysElapsed = daysElapsedFormat.Replace("xx", calendar.PlayerDaysElapsed.ToString("0.00"));
            if (daysElapsedText.text != daysElapsed)
                daysElapsedText.text = daysElapsed;
        }
    }

    string GetWeekday(long worldDayIndex)
    {
        if (weekdayNames == null || weekdayNames.Length == 0)
            return "Day";

        int index = (int)((worldDayIndex + startingWeekdayIndex) % weekdayNames.Length);
        if (index < 0)
            index += weekdayNames.Length;
        return weekdayNames[index];
    }

    static string GetOrdinal(int day)
    {
        int lastTwo = day % 100;
        string suffix = lastTwo is >= 11 and <= 13
            ? "th"
            : (day % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th"
            };

        return $"{day}{suffix}";
    }

    static string FormatGameTime(float clockHour)
    {
        int totalSeconds = Mathf.FloorToInt(Mathf.Repeat(clockHour, 24f) * 3600f);
        int hours = totalSeconds / 3600;
        int minutes = totalSeconds / 60 % 60;
        int seconds = totalSeconds % 60;
        return $"{hours:00}:{minutes:00}:{seconds:00}";
    }

    static TMP_Text FindText(string objectName)
    {
        GameObject target = GameObject.Find(objectName);
        return target != null ? target.GetComponent<TMP_Text>() : null;
    }
}
