using UnityEngine;

public enum SolAtmosphereQuality
{
    Low = 0,
    Medium = 1,
    High = 2,
}

[CreateAssetMenu(menuName = "Sol/Environment/Atmosphere Profile", fileName = "Sol Atmosphere Profile")]
public sealed class SolAtmosphereProfile : ScriptableObject
{
    [Header("Extinction")]
    [Min(0f)] public float densityMultiplier = 1f;
    [Min(0f)] public float startDistance = 5f;
    [Min(1f)] public float maxDistance = 500f;
    [Range(0f, 1f)] public float maxOpacity = 0.92f;

    [Header("Height")]
    public float baseHeight = 12f;
    [Min(0.0001f)] public float heightFalloff = 0.035f;
    [Tooltip("Weather mist blends the authored base height toward this low layer.")]
    public float mistBaseHeight = 1.5f;
    [Tooltip("Weather mist blends height falloff toward this ground-hugging value.")]
    [Min(0.0001f)] public float mistHeightFalloff = 0.12f;

    [Header("Noise")]
    [Range(0f, 1f)] public float noiseIntensity = 0.1f;
    [Min(0.0001f)] public float noiseScale = 0.0035f;
    [Min(0f)] public float noiseSpeed = 0.08f;

    [Header("Sky Fog")]
    [Tooltip("Overall sky-extinction multiplier retained for compatibility.")]
    [Range(0f, 2f)] public float skyFogStrength = 1f;
    [Tooltip("Extinction applied looking away from the horizon.")]
    [Range(0f, 2f)] public float zenithFogStrength = 0.12f;
    [Tooltip("Extinction applied at the horizon. A value of one matches distant surface fog.")]
    [Range(0f, 2f)] public float horizonFogStrength = 1f;

    [Header("Lighting")]
    [Range(-0.9f, 0.9f)] public float phaseAnisotropy = 0.55f;
    [Range(0f, 3f)] public float directionalScatteringIntensity = 0.65f;
    [Range(0f, 1f)] public float shadowedScatteringStrength = 0.85f;
    [Range(0f, 1f)] public float fogSaturation = 0.35f;
    [Min(0f)] public float ambientScatteringIntensity = 0.65f;
    [Min(0.01f)] public float maxScatteringLuminance = 1.5f;
    [Range(0f, 1f)] public float lightningScatteringIntensity = 0.08f;
    [ColorUsage(true, true)] public Color dayScatteringColor = new(1f, 0.75f, 0.48f, 1f);
    [ColorUsage(true, true)] public Color nightScatteringColor = new(0.22f, 0.34f, 0.62f, 1f);

    [Header("High Quality Volumetrics")]
    [Min(1f)] public float raymarchDistance = 500f;
    [Range(8, 32)] public int raymarchStepCount = 32;
    [Range(0f, 1f)] public float raymarchJitter = 0.15f;
    [Min(0.01f)] public float bilateralDepthThreshold = 2f;
    [Range(0f, 1f)] public float spatialFilterStrength = 0.75f;

    // Retained so pre-enhancement profile data is not silently discarded.
    [HideInInspector] public float scatteringIntensity = 0.65f;
    [HideInInspector] public float scatteringPower = 12f;

    [Header("Quality")]
    public SolAtmosphereQuality quality = SolAtmosphereQuality.Medium;
}
