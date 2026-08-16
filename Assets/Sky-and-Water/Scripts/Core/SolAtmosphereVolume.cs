using System;
using UnityEngine;
using UnityEngine.Rendering;

[Serializable, VolumeComponentMenu("Sol/Atmosphere")]
public sealed class SolAtmosphereVolume : VolumeComponent, IPostProcessComponent
{
    public BoolParameter enabledOverride = new(false);
    public ClampedFloatParameter densityMultiplier = new(1f, 0f, 8f);
    public MinFloatParameter startDistance = new(5f, 0f);
    public MinFloatParameter maximumDistance = new(500f, 1f);
    public ClampedFloatParameter maximumOpacity = new(0.92f, 0f, 1f);
    public ColorParameter fogColor = new(Color.gray, true, false, true);
    public ClampedIntParameter quality = new(1, 0, 2);

    public bool IsActive() => active && enabledOverride.value;
}
