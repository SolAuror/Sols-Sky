using System.Collections.Generic;
using UnityEngine;

internal static class SolAtmosphereLocalRegistry
{
    internal static readonly List<SolAtmosphereDensityVolume> DensityVolumes = new(16);
    internal static readonly List<SolAtmosphereExclusionVolume> ExclusionVolumes = new(8);
    internal static readonly List<SolVolumetricLight> Lights = new(16);
}

[ExecuteAlways, DisallowMultipleComponent]
public sealed class SolAtmosphereDensityVolume : MonoBehaviour
{
    [SerializeField] Collider volumeCollider;
    [SerializeField, Min(0f)] float density = 0.02f;
    [SerializeField, Min(0.01f)] float blendDistance = 2f;

    public float Density => density;
    public float BlendDistance => blendDistance;
    public Bounds WorldBounds => volumeCollider != null
        ? volumeCollider.bounds
        : new Bounds(transform.position, Vector3.one * 10f);

    void Reset() => volumeCollider = GetComponent<Collider>();
    void OnEnable()
    {
        if (!SolAtmosphereLocalRegistry.DensityVolumes.Contains(this))
            SolAtmosphereLocalRegistry.DensityVolumes.Add(this);
    }
    void OnDisable() => SolAtmosphereLocalRegistry.DensityVolumes.Remove(this);
}

[ExecuteAlways, DisallowMultipleComponent]
public sealed class SolAtmosphereExclusionVolume : MonoBehaviour
{
    [SerializeField] Collider volumeCollider;
    [SerializeField, Range(0f, 1f)] float strength = 1f;
    [SerializeField, Min(0.01f)] float blendDistance = 1f;

    public float Strength => strength;
    public float BlendDistance => blendDistance;
    public Bounds WorldBounds => volumeCollider != null
        ? volumeCollider.bounds
        : new Bounds(transform.position, Vector3.one * 10f);

    void Reset() => volumeCollider = GetComponent<Collider>();
    void OnEnable()
    {
        if (!SolAtmosphereLocalRegistry.ExclusionVolumes.Contains(this))
            SolAtmosphereLocalRegistry.ExclusionVolumes.Add(this);
    }
    void OnDisable() => SolAtmosphereLocalRegistry.ExclusionVolumes.Remove(this);
}

[ExecuteAlways, DisallowMultipleComponent]
public sealed class SolVolumetricLight : MonoBehaviour
{
    [SerializeField] Light source;
    [SerializeField, Range(0f, 4f)] float scatteringMultiplier = 1f;

    public Light Source => source;
    public float ScatteringMultiplier => scatteringMultiplier;

    void Reset() => source = GetComponent<Light>();
    void OnEnable()
    {
        if (source == null)
            source = GetComponent<Light>();
        if (!SolAtmosphereLocalRegistry.Lights.Contains(this))
            SolAtmosphereLocalRegistry.Lights.Add(this);
    }
    void OnDisable() => SolAtmosphereLocalRegistry.Lights.Remove(this);
}
