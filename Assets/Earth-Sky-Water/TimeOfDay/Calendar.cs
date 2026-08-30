using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace Sol.ToD
{

/// <summary>
/// Tracks an in-game calendar (day / month / year) with configurable month
/// lengths. The configured months are divided into four ordered seasons,
/// beginning with Spring, while actual month lengths determine progress.
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

    [Tooltip("Signed world-date offset from the configured starting date. Rewinds may make this negative.")]
    [SerializeField] long worldDayIndex;

    [Tooltip("Forward-only fractional days experienced by the player. World-date rewinds never reduce this value.")]
    [FormerlySerializedAs("totalDaysElapsed")]
    [SerializeField] double playerDaysElapsed;
    #endregion

    // -- Events ---------------------------------------

    /// <summary>Fired when a new day begins. Passes (day, month, year).</summary>
    public event Action<int, int, int> OnNewDay;

    /// <summary>Fired when a new month begins. Passes (month, year).</summary>
    public event Action<int, int> OnNewMonth;

    /// <summary>Fired when a new year begins. Passes (year).</summary>
    public event Action<int> OnNewYear;

    /// <summary>Fired whenever the four-season calendar changes quarter.</summary>
    public event Action<SolSeason> SeasonChanged;

    /// <summary>Compatibility event for the old first-half/second-half model.</summary>
    [Obsolete("Use SeasonChanged(SolSeason) and CurrentSeason.")]
    public event Action<bool> OnSeasonChanged;

    // -- Cached values --------------------------------
    int cachedDaysPerYear;
    int cachedDayOfYear;
    bool cachedFirstHalf;
    SolSeason cachedSeason;

    // ------------------------------------------------
    // Initialisation (called by TimeofDay.Start)
    // ------------------------------------------------

    /// <summary>Start a new game at the configured date with both clocks reset.</summary>
    public void ResetToStart()
    {
        currentDay        = startDay;
        currentMonth      = startMonth;
        currentYear       = startYear;
        worldDayIndex     = 0;
        playerDaysElapsed = 0d;
        cachedDaysPerYear = DaysPerYear;
        cachedDayOfYear   = DayOfYear;
        cachedFirstHalf   = cachedDayOfYear <= cachedDaysPerYear / 2;
        cachedSeason      = GetSeasonForMonth(currentMonth);
    }

    // ------------------------------------------------
    // Day advancement / rewind
    // ------------------------------------------------

    public void AdvanceDay()
    {
        worldDayIndex++;
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
        worldDayIndex--;
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
    /// Retained for compatibility with the former binary season model.
    /// </summary>
    [Obsolete("Use CurrentSeason, SeasonProgress, or YearProgress.")]
    public float SeasonSign => cachedFirstHalf ? 1f : -1f;

    /// <summary>True when the calendar is in the first half of the year.</summary>
    [Obsolete("Use CurrentSeason, SeasonProgress, or YearProgress.")]
    public bool IsFirstHalfOfYear => cachedFirstHalf;

    /// <summary>Current four-season calendar value.</summary>
    public SolSeason CurrentSeason => cachedSeason;

    /// <summary>Normalized progress through the current season, from 0 toward 1.</summary>
    public float SeasonProgress
    {
        get
        {
            GetSeasonDayRange((int)cachedSeason, out int firstDay, out int length);
            return length > 0 ? Mathf.Clamp01((cachedDayOfYear - firstDay) / (float)length) : 0f;
        }
    }

    /// <summary>Normalized progress through the current year, from 0 toward 1.</summary>
    public float YearProgress => cachedDaysPerYear > 0
        ? Mathf.Clamp01((cachedDayOfYear - 1f) / cachedDaysPerYear)
        : 0f;

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

    /// <summary>Signed world-date offset from the configured starting date.</summary>
    public long WorldDayIndex => worldDayIndex;

    /// <summary>Forward-only fractional days experienced by the player.</summary>
    public double PlayerDaysElapsed => playerDaysElapsed;

    /// <summary>Completed player-experienced days. Use <see cref="PlayerDaysElapsed"/> for new code.</summary>
    [Obsolete("Use PlayerDaysElapsed for player time or WorldDayIndex for rewindable world-date time.")]
    public int TotalDaysElapsed => playerDaysElapsed >= int.MaxValue
        ? int.MaxValue
        : Mathf.FloorToInt((float)Math.Max(0d, playerDaysElapsed));

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
        worldDayIndex = CalculateWorldDayIndex(currentDay, currentMonth, currentYear);
        RefreshCachedValues();
    }

    /// <summary>Compatibility restore overload. The legacy counter maps to completed player days.</summary>
    [Obsolete("Use SetDate(day, month, year, worldDayIndex, playerDaysElapsed).")]
    public void SetDate(int day, int month, int year, int totalDays)
    {
        SetDate(day, month, year);
        playerDaysElapsed = Math.Max(0d, totalDays);
    }

    /// <summary>Restore the world date and player-time counter independently.</summary>
    public void SetDate(int day, int month, int year, long restoredWorldDayIndex, double restoredPlayerDaysElapsed)
    {
        currentYear = year;
        currentMonth = Mathf.Clamp(month, 1, MonthsPerYear);
        currentDay = Mathf.Clamp(day, 1, GetDaysInMonth(currentMonth));
        worldDayIndex = restoredWorldDayIndex;
        playerDaysElapsed = Math.Max(0d, restoredPlayerDaysElapsed);
        RefreshCachedValues();
    }

    /// <summary>Add forward-only player-experienced time. Negative values are ignored.</summary>
    public void AdvancePlayerTime(double days)
    {
        if (days <= 0d || double.IsNaN(days) || double.IsInfinity(days))
            return;

        playerDaysElapsed += days;
    }

    /// <summary>Restore player-experienced time without changing the world date.</summary>
    public void SetPlayerDaysElapsed(double days)
    {
        playerDaysElapsed = Math.Max(0d, double.IsNaN(days) || double.IsInfinity(days) ? 0d : days);
    }

    // ------------------------------------------------
    // Internal
    // ------------------------------------------------

    void RefreshCachedValues()
    {
        cachedDayOfYear   = DayOfYear;
        cachedDaysPerYear = DaysPerYear;

        SolSeason season = GetSeasonForMonth(currentMonth);
        if (season != cachedSeason)
        {
            cachedSeason = season;
            SeasonChanged?.Invoke(cachedSeason);
        }

        bool firstHalf = cachedDayOfYear <= cachedDaysPerYear / 2;
        if (firstHalf != cachedFirstHalf)
        {
            cachedFirstHalf = firstHalf;
            OnSeasonChanged?.Invoke(cachedFirstHalf);
        }
    }

    SolSeason GetSeasonForMonth(int month)
    {
        int monthCount = Mathf.Max(1, MonthsPerYear);
        int clampedMonth = Mathf.Clamp(month, 1, monthCount);
        int seasonIndex = Mathf.Min(3, (clampedMonth - 1) * 4 / monthCount);
        return (SolSeason)seasonIndex;
    }

    void GetSeasonDayRange(int seasonIndex, out int firstDay, out int length)
    {
        int monthCount = Mathf.Max(1, MonthsPerYear);
        int firstMonthIndex = seasonIndex * monthCount / 4;
        int endMonthIndex = (seasonIndex + 1) * monthCount / 4;
        if (seasonIndex == 3)
            endMonthIndex = monthCount;

        firstDay = 1;
        for (int i = 0; i < firstMonthIndex; i++)
            firstDay += daysPerMonth[i];

        length = 0;
        for (int i = firstMonthIndex; i < endMonthIndex; i++)
            length += daysPerMonth[i];
    }

    long CalculateWorldDayIndex(int day, int month, int year)
    {
        long years = (long)year - startYear;
        long index = years * DaysPerYear;
        index += GetDayOfYear(day, month) - GetDayOfYear(startDay, startMonth);
        return index;
    }

    int GetDayOfYear(int day, int month)
    {
        int clampedMonth = Mathf.Clamp(month, 1, MonthsPerYear);
        int result = 0;
        for (int m = 1; m < clampedMonth; m++)
            result += GetDaysInMonth(m);
        return result + Mathf.Clamp(day, 1, GetDaysInMonth(clampedMonth));
    }
}
}

