using UnityEngine;

namespace Sol.ToD
{
    /// <summary>
    /// The canonical categories of gameplay time mutation.
    /// Keep these command-like so future multiplayer can validate them on the host/server.
    /// </summary>
    public enum TimeChangeType
    {
        AdvanceHours,
        RewindHours,
        SetClockHour,
        SetNormalizedTime,
        SetTimeScale,
        SetPaused,
        SkipToSunrise,
        SkipToSunset,
        SkipForwardOneDay,
        SkipBackwardOneDay
    }

    /// <summary>
    /// Describes one requested change to world time.
    /// Single-player applies this immediately; multiplayer can later replicate the same request shape.
    /// </summary>
    public readonly struct TimeChangeRequest
    {
        public readonly Object Source;
        public readonly TimeChangeType Type;
        public readonly float Value;
        public readonly string Reason;

        public TimeChangeRequest(TimeChangeType type, float value = 0f, Object source = null, string reason = null)
        {
            Type = type;
            Value = value;
            Source = source;
            Reason = string.IsNullOrWhiteSpace(reason) ? type.ToString() : reason.Trim();
        }

        public static TimeChangeRequest AdvanceHours(float hours, Object source = null, string reason = null)
            => new(TimeChangeType.AdvanceHours, hours, source, reason);

        public static TimeChangeRequest RewindHours(float hours, Object source = null, string reason = null)
            => new(TimeChangeType.RewindHours, hours, source, reason);

        public static TimeChangeRequest SetClockHour(float hour, Object source = null, string reason = null)
            => new(TimeChangeType.SetClockHour, hour, source, reason);

        public static TimeChangeRequest SetNormalizedTime(float normalizedTime, Object source = null, string reason = null)
            => new(TimeChangeType.SetNormalizedTime, normalizedTime, source, reason);

        public static TimeChangeRequest SetTimeScale(float scale, Object source = null, string reason = null)
            => new(TimeChangeType.SetTimeScale, scale, source, reason);

        public static TimeChangeRequest SetPaused(bool paused, Object source = null, string reason = null)
            => new(TimeChangeType.SetPaused, paused ? 1f : 0f, source, reason);
    }

    /// <summary>
    /// Snapshot of one applied time change. Consumers can react without re-reading old state themselves.
    /// </summary>
    public readonly struct TimeChangeResult
    {
        public readonly TimeChangeRequest Request;
        public readonly float OldNormalizedTime;
        public readonly float NewNormalizedTime;
        public readonly float OldClockHour;
        public readonly float NewClockHour;
        public readonly int OldTotalDays;
        public readonly int NewTotalDays;
        public readonly long OldWorldDayIndex;
        public readonly long NewWorldDayIndex;
        public readonly double AppliedWorldHours;
        public readonly double OldPlayerDaysElapsed;
        public readonly double NewPlayerDaysElapsed;
        public readonly bool Changed;

        public int DaysDelta => NewTotalDays - OldTotalDays;
        public long WorldDaysDelta => NewWorldDayIndex - OldWorldDayIndex;
        public double PlayerDaysDelta => NewPlayerDaysElapsed - OldPlayerDaysElapsed;

        [System.Obsolete("Use the constructor that includes world-day, applied-hours, and player-time values.")]
        public TimeChangeResult(
            TimeChangeRequest request,
            float oldNormalizedTime,
            float newNormalizedTime,
            float oldClockHour,
            float newClockHour,
            int oldTotalDays,
            int newTotalDays,
            bool changed)
        {
            Request = request;
            OldNormalizedTime = oldNormalizedTime;
            NewNormalizedTime = newNormalizedTime;
            OldClockHour = oldClockHour;
            NewClockHour = newClockHour;
            OldTotalDays = oldTotalDays;
            NewTotalDays = newTotalDays;
            OldWorldDayIndex = 0L;
            NewWorldDayIndex = 0L;
            AppliedWorldHours = 0d;
            OldPlayerDaysElapsed = oldTotalDays;
            NewPlayerDaysElapsed = newTotalDays;
            Changed = changed;
        }

        public TimeChangeResult(
            TimeChangeRequest request,
            float oldNormalizedTime,
            float newNormalizedTime,
            float oldClockHour,
            float newClockHour,
            int oldTotalDays,
            int newTotalDays,
            long oldWorldDayIndex,
            long newWorldDayIndex,
            double appliedWorldHours,
            double oldPlayerDaysElapsed,
            double newPlayerDaysElapsed,
            bool changed)
        {
            Request = request;
            OldNormalizedTime = oldNormalizedTime;
            NewNormalizedTime = newNormalizedTime;
            OldClockHour = oldClockHour;
            NewClockHour = newClockHour;
            OldTotalDays = oldTotalDays;
            NewTotalDays = newTotalDays;
            OldWorldDayIndex = oldWorldDayIndex;
            NewWorldDayIndex = newWorldDayIndex;
            AppliedWorldHours = appliedWorldHours;
            OldPlayerDaysElapsed = oldPlayerDaysElapsed;
            NewPlayerDaysElapsed = newPlayerDaysElapsed;
            Changed = changed;
        }
    }
}
