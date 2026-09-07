using System;

/// <summary>
/// Deterministic weather sequencer stream. The daily climate model is already a stable
/// splitmix64 hash of the world date, but profile selection, hold duration and lightning
/// timing all drew from the global UnityEngine.Random, so two runs of the same save
/// diverged and nothing could be replayed. This is the same mixer as
/// SolWeatherManager.HashWorldDay, carried as an advancing state rather than sampled by
/// date, because hold durations are sub-day and variable.
/// </summary>
[Serializable]
public struct SolWeatherRandom
{
    ulong _state;

    /// <summary>Raw stream position, so a save can resume the identical future sequence.</summary>
    public ulong State
    {
        get => _state;
        set => _state = value;
    }

    public static SolWeatherRandom FromSeed(int seed, ulong salt)
        => new() { _state = unchecked((ulong)seed + salt + 0x9E3779B97F4A7C15UL) };

    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong value = _state;
            value ^= value >> 30;
            value *= 0xBF58476D1CE4E5B9UL;
            value ^= value >> 27;
            value *= 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return value;
        }
    }

    /// <summary>Uniform in [0,1). 24 bits is the same precision the climate hash publishes.</summary>
    public float NextFloat01() => (NextUInt64() & 0xFFFFFFUL) / 16777216f;

    public float Range(float minimum, float maximum)
        => minimum + (maximum - minimum) * NextFloat01();
}
