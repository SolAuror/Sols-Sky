using System;
using UnityEngine;

/// <summary>
/// Serializable weather sequencer state. Mirrors the SolEnvironmentWorld snapshot contract:
/// a version gate, the values a reload cannot reconstruct, and nothing that can be derived.
///
/// The presented and from-channels are carried rather than the published SolWeatherState,
/// because that state already has the daily climate folded into its fog and mist. Restoring
/// from it would add the climate contribution a second time on every load.
/// </summary>
[Serializable]
public readonly struct SolWeatherSnapshot
{
    public readonly int Version;
    /// <summary>Target profile asset name. Authoritative over the index, which moves when a scene's selection list is reordered.</summary>
    public readonly string TargetProfileName;
    public readonly int TargetIndex;
    public readonly SolWeatherChannels From;
    public readonly SolWeatherChannels Presented;
    public readonly float Blend;
    public readonly float HoursRemaining;
    public readonly float WindAngleDegrees;
    public readonly float WindNoiseTime;
    public readonly float SecondsUntilStrike;
    public readonly float DailyFog;
    public readonly long ClimateWorldDay;
    public readonly int ClimateSeed;
    public readonly ulong SequencerState;

    public SolWeatherSnapshot(
        int version,
        string targetProfileName,
        int targetIndex,
        in SolWeatherChannels from,
        in SolWeatherChannels presented,
        float blend,
        float hoursRemaining,
        float windAngleDegrees,
        float windNoiseTime,
        float secondsUntilStrike,
        float dailyFog,
        long climateWorldDay,
        int climateSeed,
        ulong sequencerState)
    {
        Version = version;
        TargetProfileName = targetProfileName;
        TargetIndex = Mathf.Max(0, targetIndex);
        From = from;
        Presented = presented;
        Blend = Mathf.Clamp01(blend);
        HoursRemaining = Mathf.Max(0f, hoursRemaining);
        WindAngleDegrees = windAngleDegrees;
        WindNoiseTime = Mathf.Max(0f, windNoiseTime);
        SecondsUntilStrike = secondsUntilStrike;
        DailyFog = Mathf.Clamp01(dailyFog);
        ClimateWorldDay = climateWorldDay;
        ClimateSeed = climateSeed;
        SequencerState = sequencerState;
    }
}
