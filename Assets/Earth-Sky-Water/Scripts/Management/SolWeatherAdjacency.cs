using UnityEngine;

/// <summary>
/// How plausible one weather profile is as the successor of another.
///
/// Selection used to be memoryless: a weighted roll over every profile with nothing but an
/// avoid-repeat filter, so Clear could hand straight over to Blizzard. Weighting candidates
/// by their distance from the current state makes fronts build and clear through the
/// intermediate rungs instead.
///
/// Derived from the profiles' own values rather than authored as a matrix. An N x N table
/// would be 81 cells duplicated across three scenes and a prefab, and it would have to be
/// re-reviewed every time a profile was retuned. Deriving makes the relation correct by
/// construction -- Drizzle is a plausible successor to Overcast because Drizzle *is*
/// Overcast plus a little precipitation -- and it stays correct when a profile is added or
/// retuned. This matches how the rest of the system already works: HashWorldDay,
/// DailyCloudOffset, ResolveFogDensityFloor and FormationWeights are all derivations, not
/// tables.
/// </summary>
public static class SolWeatherAdjacency
{
    /// <summary>
    /// Width of the plausibility falloff, in the same units as <see cref="Distance"/>.
    /// The single tuning knob: larger makes weather wander further per change.
    /// </summary>
    public const float Sigma = 0.38f;

    /// <summary>
    /// Floor under every affinity, so adjacency can only ever bias the existing weighted
    /// roll and never veto a candidate outright. Without it a season whose plausible
    /// successors all carry a zero seasonal weight could dead-end.
    /// </summary>
    public const float MinimumAffinity = 0.05f;

    /// <summary>
    /// The deck TimeOfDay authors, which cloudiness is an influence toward overcast from.
    /// Pinned against the shipped scenes by AuthoredCloudDeck_MatchesTheProfileTuningBaseline.
    /// </summary>
    public const float NominalAuthoredCoverage = 0.563f;

    // Cover, precipitation amount, precipitation phase, wind and darkening. Five axes,
    // because they are the ones a viewer reads as "this is different weather".
    const float CoverWeight = 1.00f;
    const float PrecipitationWeight = 1.10f;
    const float FrozenWeight = 1.70f;
    const float WindWeight = 0.80f;
    const float DimWeight = 0.55f;
    const float AxisCount = 5f;

    /// <summary>Wind is compared as a fraction of the profile field's own range.</summary>
    const float WindReferenceMetresPerSecond = 24f;

    /// <summary>
    /// Cover this profile actually renders at. Not <c>cloudiness</c>, which is an influence
    /// rather than an absolute: Clear and Fair both author 0 and differ only in their bias.
    /// </summary>
    public static float EffectiveCoverage(SolWeatherProfileAsset profile)
        => profile != null ? profile.ResolveEffectiveCoverage(NominalAuthoredCoverage) : 0f;

    /// <summary>
    /// Weighted RMS separation. Dividing by the axis count keeps <see cref="Sigma"/> a
    /// readable "how far apart is one rung" number instead of a constant that would have to
    /// be re-derived if an axis were ever added.
    /// </summary>
    public static float Distance(SolWeatherProfileAsset a, SolWeatherProfileAsset b)
    {
        if (a == null || b == null || a == b)
            return 0f;

        float cover = CoverWeight * (EffectiveCoverage(a) - EffectiveCoverage(b));
        float precipitation = PrecipitationWeight
            * (Mathf.Clamp01(a.rainIntensity) - Mathf.Clamp01(b.rainIntensity));

        // The phase axis is gated on how much is actually falling. Comparing snowBias
        // outright would make Clear -> Snow expensive over a difference that decides
        // nothing -- Clear is dry, so its snow bias never resolves anything -- and would
        // tie phase to amount. Gating on the wetter of the two keeps it symmetric while
        // still holding Rain and Blizzard apart, which is the case that matters.
        float phaseGate = Mathf.Max(Mathf.Clamp01(a.rainIntensity), Mathf.Clamp01(b.rainIntensity));
        float frozen = FrozenWeight * phaseGate
            * (Mathf.Clamp01(a.snowBias) - Mathf.Clamp01(b.snowBias));

        float wind = WindWeight
            * ((a.windSpeedMetresPerSecond - b.windSpeedMetresPerSecond)
                / WindReferenceMetresPerSecond);
        float dim = DimWeight * (Mathf.Clamp01(a.dim) - Mathf.Clamp01(b.dim));

        float sum = cover * cover + precipitation * precipitation + frozen * frozen
                  + wind * wind + dim * dim;
        return Mathf.Sqrt(sum / AxisCount);
    }

    /// <summary>
    /// Plausibility of <paramref name="to"/> following <paramref name="from"/>, in
    /// (0, 1]. Symmetric, because a front that can build can also clear.
    /// </summary>
    public static float Affinity(SolWeatherProfileAsset from, SolWeatherProfileAsset to)
    {
        if (from == null || to == null)
            return 1f;

        float normalised = Distance(from, to) / Sigma;
        return Mathf.Max(MinimumAffinity, Mathf.Exp(-normalised * normalised));
    }
}
