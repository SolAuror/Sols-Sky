using System;
using UnityEngine;

/// <summary>
/// Where one weather channel sits inside the shared weather transition. Both values are
/// fractions of the master transition, so changing transitionDurationSeconds rescales the
/// whole choreography without disturbing the ordering.
/// </summary>
[Serializable]
public struct SolWeatherChannelTiming
{
    [Tooltip("Fraction of the master transition to wait before this channel starts moving.")]
    [Range(0f, 0.95f)] public float delay;

    [Tooltip("Fraction of the master transition this channel occupies once it starts.")]
    [Range(0.05f, 1f)] public float span;

    public SolWeatherChannelTiming(float delay, float span)
    {
        this.delay = delay;
        this.span = span;
    }

    /// <summary>
    /// Maps master transition progress to this channel's own eased progress. Every channel
    /// reaches exactly 1 at master 1, so an instant change needs no special case.
    /// </summary>
    public readonly float Evaluate(float master)
    {
        float start = Mathf.Clamp(delay, 0f, 0.95f);
        float end = Mathf.Min(1f, start + Mathf.Max(0.05f, span));
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(start, end, Mathf.Clamp01(master)));
    }
}

/// <summary>
/// Lead/lag choreography for a weather change. Blending every channel on one shared curve
/// makes rain reach full intensity under a sky that has not finished clouding over; giving
/// each channel its own window inside the same transition fixes that without introducing a
/// second clock or a coroutine per channel.
/// </summary>
[Serializable]
public sealed class SolWeatherTransitionTimings
{
    [Tooltip("Wind shifts first. The environment's own response lags then delay the cloud and sea reaction naturally.")]
    public SolWeatherChannelTiming wind = new(0f, 0.5f);

    [Tooltip("Cloud cover, formation, and shape. Cover must be building before anything falls out of it.")]
    public SolWeatherChannelTiming clouds = new(0f, 0.7f);

    [Tooltip("Fog boost, mistiness, and sky obscuration.")]
    public SolWeatherChannelTiming fog = new(0.15f, 0.7f);

    [Tooltip("Wave speed and water turbulence.")]
    public SolWeatherChannelTiming water = new(0.2f, 0.8f);

    [Tooltip("Precipitation intensity and its snow bias. Additionally gated on cloud cover while cover is building.")]
    public SolWeatherChannelTiming precipitation = new(0.35f, 0.65f);

    [Tooltip("Lightning peak intensity.")]
    public SolWeatherChannelTiming lightning = new(0.4f, 0.6f);

    [Tooltip("Storm darkening and directional light scattering.")]
    public SolWeatherChannelTiming light = new(0.1f, 0.75f);

    /// <summary>
    /// Extra factor applied to precipitation while cloud cover is still building, so rain
    /// or snow cannot appear under a sky that has not arrived yet. Clearing weather is
    /// deliberately not gated: precipitation should stop promptly when the cover breaks up,
    /// which is why this reads the direction of the coverage change rather than gating
    /// symmetrically.
    /// </summary>
    public static float PrecipitationCoverGate(
        float fromCoverage, float toCoverage, float cloudProgress)
    {
        if (toCoverage <= fromCoverage + 0.01f)
            return 1f;

        return Mathf.SmoothStep(0f, 1f,
            Mathf.InverseLerp(0.35f, 0.9f, Mathf.Clamp01(cloudProgress)));
    }
}
