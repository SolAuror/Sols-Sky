using System;
using UnityEngine;

namespace Sol.ToD
{

/// <summary>
/// Tracks an in-game calendar (day / month / year) with configurable month
/// lengths. Seasons flip discretely at the year boundary and at the midpoint
/// of the year (half the total days).
/// Must live on the same GameObject as <see cref="TimeOfDay"/>.
/// </summary>
[RequireComponent(typeof(TimeOfDay))]
public class Calendar : MonoBehaviour
{
    // -- Configuration --------------------------------
    #region Inspector Settings
    [Header("-- Calendar -----------------------")]
    [Tooltip("Number of days in each month. Length = number of months per year.")]
    [SerializeField] int[] daysPerMonth = { 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 };

    [Tooltip("Display name for each month. Must match the length of daysPerMonth.")]
    [SerializeField] string[] monthNames = {
        "Aurion", "Solven", "Thalmer", "Verdane",
        "Caelith", "Embera", "Lithane", "Duskara",
        "Falmere", "Nocturn", "Wrethis", "Glacium"
    };

    [Tooltip("Starting day (1-based).")]
    [Min(1)]
    [SerializeField] int startDay = 1;

    [Tooltip("Starting month (1-based).")]
    [Min(1)]
    [SerializeField] int startMonth = 1;

    [Tooltip("Starting year.")]
    [SerializeField] int startYear = 142;

    [Header("-- Calendar / Debug ----------------")]
    [Tooltip("Current calendar day (read-only in play mode).")]
    [SerializeField] int currentDay;

    [Tooltip("Current calendar month (read-only in play mode).")]
    [SerializeField] int currentMonth;

    [Tooltip("Current calendar year (read-only in play mode).")]
    [SerializeField] int currentYear;

    [Tooltip("Total in-game days elapsed since start.")]
    [SerializeField] int totalDaysElapsed;
    #endregion

    // -- Events ---------------------------------------

    /// <summary>Fired when a new day begins. Passes (day, month, year).</summary>
    public event Action<int, int, int> OnNewDay;

    /// <summary>Fired when a new month begins. Passes (month, year).</summary>
    public event Action<int, int> OnNewMonth;

    /// <summary>Fired when a new year begins. Passes (year).</summary>
    public event Action<int> OnNewYear;

    /// <summary>Fired when the season flips (at year start and year midpoint).
    /// Passes true for the first half of the year, false for the second.</summary>
    public event Action<bool> OnSeasonChanged;

    // -- Cached values --------------------------------
    int cachedDaysPerYear;
    int cachedDayOfYear;
    bool cachedFirstHalf;

    // ------------------------------------------------
    // Initialisation (called by TimeofDay.Start)
    // ------------------------------------------------

    /// <summary>Reset to the starting date. Called once by TimeofDay at play-mode start.</summary>
    public void ResetToStart()
    {
        currentDay        = startDay;
        currentMonth      = startMonth;
        currentYear       = startYear;
        totalDaysElapsed  = 0;
        cachedDaysPerYear = DaysPerYear;
        cachedDayOfYear   = DayOfYear;
        cachedFirstHalf   = cachedDayOfYear <= cachedDaysPerYear / 2;
    }

    // ------------------------------------------------
    // Day advancement / rewind
    // ------------------------------------------------

    public void AdvanceDay()
    {
        totalDaysElapsed++;
        currentDay++;

        int maxDay = GetDaysInMonth(currentMonth);
        if (currentDay > maxDay)
        {
            currentDay = 1;
            currentMonth++;

            if (currentMonth > MonthsPerYear)
            {
                currentMonth = 1;
                currentYear++;
                OnNewYear?.Invoke(currentYear);
            }

            OnNewMonth?.Invoke(currentMonth, currentYear);
        }

        OnNewDay?.Invoke(currentDay, currentMonth, currentYear);
        RefreshCachedValues();
    }

    public void RewindDay()
    {
        totalDaysElapsed = Mathf.Max(0, totalDaysElapsed - 1);
        currentDay--;

        if (currentDay < 1)
        {
            currentMonth--;

            if (currentMonth < 1)
            {
                currentMonth = MonthsPerYear;
                currentYear--;
                OnNewYear?.Invoke(currentYear);
            }

            currentDay = GetDaysInMonth(currentMonth);
            OnNewMonth?.Invoke(currentMonth, currentYear);
        }

        OnNewDay?.Invoke(currentDay, currentMonth, currentYear);
        RefreshCachedValues();
    }

    /// <summary>Advance the calendar by N days, firing events for each.</summary>
    public void AdvanceDays(int count)
    {
        for (int i = 0; i < count; i++)
            AdvanceDay();
    }

    /// <summary>Rewind the calendar by N days.</summary>
    public void RewindDays(int count)
    {
        for (int i = 0; i < count; i++)
            RewindDay();
    }

    // ------------------------------------------------
    // Seasonal helpers
    // ------------------------------------------------

    /// <summary>
    /// Returns +1 during the first half of the year and -1 during the second.
    /// Seasons flip at day 1 (year start) and at the midpoint of the year.
    /// </summary>
    public float SeasonSign => cachedFirstHalf ? 1f : -1f;

    /// <summary>True when the calendar is in the first half of the year.</summary>
    public bool IsFirstHalfOfYear => cachedFirstHalf;

    // ------------------------------------------------
    // Public API
    // ------------------------------------------------

    /// <summary>Current day of the month (1-based).</summary>
    public int Day => currentDay;

    /// <summary>Current month of the year (1-based).</summary>
    public int Month => currentMonth;

    /// <summary>Display name of the current month.</summary>
    public string MonthName => GetMonthName(currentMonth);

    /// <summary>Returns the display name for a month (1-based index).</summary>
    public string GetMonthName(int month)
    {
        int idx = Mathf.Clamp(month - 1, 0, monthNames.Length - 1);
        return idx < monthNames.Length ? monthNames[idx] : $"Month {month}";
    }

    /// <summary>Current year.</summary>
    public int Year => currentYear;

    /// <summary>Total in-game days elapsed since the start.</summary>
    public int TotalDaysElapsed => totalDaysElapsed;

    /// <summary>Number of months per year (derived from daysPerMonth array length).</summary>
    public int MonthsPerYear => daysPerMonth.Length;

    /// <summary>Average month length in days (DaysPerYear / MonthsPerYear). Used as the lunar synodic period.</summary>
    public float AverageMonthLength => MonthsPerYear > 0 ? (float)DaysPerYear / MonthsPerYear : 28f;

    /// <summary>Total number of days in one year.</summary>
    public int DaysPerYear
    {
        get
        {
            int total = 0;
            for (int i = 0; i < daysPerMonth.Length; i++) total += daysPerMonth[i];
            return total;
        }
    }

    /// <summary>Current day-of-year (1-based).</summary>
    public int DayOfYear
    {
        get
        {
            int d = 0;
            for (int m = 0; m < currentMonth - 1 && m < daysPerMonth.Length; m++)
                d += daysPerMonth[m];
            return d + currentDay;
        }
    }

    /// <summary>Cached day-of-year (updated once per day, not every frame).</summary>
    public int CachedDayOfYear => cachedDayOfYear;

    /// <summary>Cached total days per year.</summary>
    public int CachedDaysPerYear => cachedDaysPerYear;

    /// <summary>Returns how many days the given month has (1-based month index).</summary>
    public int GetDaysInMonth(int month)
    {
        int idx = Mathf.Clamp(month - 1, 0, daysPerMonth.Length - 1);
        return daysPerMonth[idx];
    }

    /// <summary>Formatted date string, e.g. "Day 15, Aurion, Year 142".</summary>
    public string DateString => $"Day {currentDay}, {MonthName}, Year {currentYear}";

    /// <summary>Set the calendar date directly. Does not fire events.</summary>
    public void SetDate(int day, int month, int year)
    {
        currentYear  = year;
        currentMonth = Mathf.Clamp(month, 1, MonthsPerYear);
        currentDay   = Mathf.Clamp(day, 1, GetDaysInMonth(currentMonth));
        RefreshCachedValues();
    }

    /// <summary>Set the calendar date directly, including the running day counter.</summary>
    public void SetDate(int day, int month, int year, int totalDays)
    {
        currentYear = year;
        currentMonth = Mathf.Clamp(month, 1, MonthsPerYear);
        currentDay = Mathf.Clamp(day, 1, GetDaysInMonth(currentMonth));
        totalDaysElapsed = Mathf.Max(0, totalDays);
        RefreshCachedValues();
    }

    // ------------------------------------------------
    // Internal
    // ------------------------------------------------

    void RefreshCachedValues()
    {
        cachedDayOfYear   = DayOfYear;
        cachedDaysPerYear = DaysPerYear;

        bool firstHalf = cachedDayOfYear <= cachedDaysPerYear / 2;
        if (firstHalf != cachedFirstHalf)
        {
            cachedFirstHalf = firstHalf;
            OnSeasonChanged?.Invoke(cachedFirstHalf);
        }
    }
}
}

