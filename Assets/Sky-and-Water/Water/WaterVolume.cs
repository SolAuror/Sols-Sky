using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ---------------------------------------------------------------------------
/// WATER VOLUME
/// ---------------------------------------------------------------------------
///
/// Defines a water body for gameplay purposes:
///   - Replicates the Sol.Water shader wave formula in C# so swimming physics
///     and buoyancy stay synced with the visual surface - no guesswork.
///   - Tracks trigger occupants and reports water depth so game-specific
///     swimming adapters can decide how to react.
///   - Pushes _WaterSurfaceY as a global shader property for the underwater
///     overlay renderer feature.
///   - Acts as a static registry so any code can query WaterVolume.FindVolume().
///
/// SETUP:
///   1.  Create an empty GameObject at the vertical centre of your water body.
///   2.  Add this component. It auto-adds a BoxCollider and sets isTrigger.
///   3.  Resize the BoxCollider to cover the water body. The TOP FACE must
///       align with the flat water surface plane (no waves yet; waves are
///       added at runtime via GetSurfaceHeight()).
///   4.  Assign the Sol.Water material used on the water mesh so wave
///       parameters are synced automatically.
/// ---------------------------------------------------------------------------
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class WaterVolume : MonoBehaviour
{
    [Header("Wave Sync")]
    [Tooltip("Sol.Water material to read wave parameters from. Leave null to fill the wave parameters manually below.")]
    public Material waterMaterial;

    [HideInInspector] public float waveAmplitude = 0.3f;
    [HideInInspector] public float waveFrequency = 1.5f;
    [HideInInspector] public float waveSpeed = 1.0f;
    [HideInInspector] public Vector2 wave1Dir = new Vector2(1f, 0f);
    [HideInInspector] public Vector2 wave2Dir = new Vector2(0.496f, 0.868f);
    [HideInInspector] public float wave2Scale = 0.5f;
    [HideInInspector] public float waveSteepness = 0.55f;
    [HideInInspector] public float waveDetailScale = 0.5f;
    [HideInInspector] public float waveLodFadeDistance = 100f;
    [HideInInspector] public float swellAmplitude = 0.15f;
    [HideInInspector] public float swellSpeed = 0.2f;
    [HideInInspector] public Vector2 swellDir = new Vector2(0.05f, 0.03f);

    [Header("Swimming")]
    [Tooltip("Depth threshold helper for game-specific swimming adapters.")]
    public float swimDepthThreshold = 1.0f;

    [Tooltip("Extra depth margin before exiting swim state. Prevents flickering at the waterline caused by wave oscillation.")]
    public float swimExitHysteresis = 0.3f;

    [Tooltip("Vertical offset applied to SurfaceY (negative = lower the effective water surface).")]
    public float surfaceOffset = 0f;

    [Header("Global Shader Properties")]
    [Tooltip("Push _WaterSurfaceY to the global shader property each frame.")]
    public bool pushGlobalProperties = true;

    static readonly List<WaterVolume> s_Volumes = new();

    BoxCollider _col;
    readonly List<Collider> _trackedColliders = new();

    public event Action<Collider> TriggerEntered;
    public event Action<Collider> TriggerExited;
    public event Action<Collider, float, bool> WaterDepthUpdated;

    static readonly int _SID_WaterSurfaceY = Shader.PropertyToID("_WaterSurfaceY");

    public static int VolumeCount => s_Volumes.Count;

    public static WaterVolume FindVolume(Vector3 worldPos)
    {
        foreach (var v in s_Volumes)
        {
            if (v == null || !v.TryGetWaterCollider(out BoxCollider col))
                continue;

            Bounds bounds = col.bounds;
            if (worldPos.x < bounds.min.x || worldPos.x > bounds.max.x ||
                worldPos.z < bounds.min.z || worldPos.z > bounds.max.z)
                continue;

            if (worldPos.y < bounds.min.y)
                continue;

            if (worldPos.y < v.GetSurfaceHeight(worldPos))
                return v;
        }

        return null;
    }

    public static WaterVolume FindVolumeXZ(Vector3 worldPos)
    {
        foreach (var v in s_Volumes)
        {
            if (v == null || !v.TryGetWaterCollider(out BoxCollider col))
                continue;

            Bounds bounds = col.bounds;
            if (worldPos.x < bounds.min.x || worldPos.x > bounds.max.x ||
                worldPos.z < bounds.min.z || worldPos.z > bounds.max.z)
                continue;

            return v;
        }

        return null;
    }

    public static float DistanceToNearestWaterXZ(Vector3 worldPos)
    {
        float closest = float.PositiveInfinity;

        for (int i = 0; i < s_Volumes.Count; i++)
        {
            WaterVolume volume = s_Volumes[i];
            if (volume == null || !volume.TryGetWaterCollider(out BoxCollider col))
                continue;

            Bounds bounds = col.bounds;
            float dx = 0f;
            if (worldPos.x < bounds.min.x)
                dx = bounds.min.x - worldPos.x;
            else if (worldPos.x > bounds.max.x)
                dx = worldPos.x - bounds.max.x;

            float dz = 0f;
            if (worldPos.z < bounds.min.z)
                dz = bounds.min.z - worldPos.z;
            else if (worldPos.z > bounds.max.z)
                dz = worldPos.z - bounds.max.z;

            float distance = Mathf.Sqrt((dx * dx) + (dz * dz));
            if (distance < closest)
                closest = distance;
        }

        return float.IsPositiveInfinity(closest) ? float.MaxValue : closest;
    }

    void OnEnable()
    {
        _col = GetComponent<BoxCollider>();
        s_Volumes.Add(this);

        if (_col != null && !_col.isTrigger)
        {
            _col.isTrigger = true;
            Debug.LogWarning($"[WaterVolume] {name}: BoxCollider.isTrigger forced to true.", this);
        }

        ReadMaterial();
    }

    void OnDisable()
    {
        s_Volumes.Remove(this);
        _trackedColliders.Clear();
    }

    void OnValidate()
    {
        ReadMaterial();
    }

    void OnTriggerEnter(Collider other)
    {
        if (other != null && !_trackedColliders.Contains(other))
            _trackedColliders.Add(other);

        TriggerEntered?.Invoke(other);
    }

    void OnTriggerExit(Collider other)
    {
        if (other != null)
            _trackedColliders.Remove(other);

        TriggerExited?.Invoke(other);
    }

    void Update()
    {
        for (int i = _trackedColliders.Count - 1; i >= 0; i--)
        {
            Collider tracked = _trackedColliders[i];
            if (tracked == null)
            {
                _trackedColliders.RemoveAt(i);
                continue;
            }

            float surfaceHeight = GetSurfaceHeight(tracked.transform.position);
            float depth = surfaceHeight - tracked.transform.position.y;
            bool aboveSwimThreshold = depth > swimDepthThreshold;
            WaterDepthUpdated?.Invoke(tracked, depth, aboveSwimThreshold);
        }

        if (pushGlobalProperties)
            Shader.SetGlobalFloat(_SID_WaterSurfaceY, GetSurfaceHeight(transform.position));
    }

    public float SurfaceY
    {
        get
        {
            if (!TryGetWaterCollider(out BoxCollider col))
                return transform.position.y + surfaceOffset;

            Vector3 scale = transform.lossyScale;
            return transform.position.y
                   + col.center.y * scale.y
                   + col.size.y * 0.5f * scale.y
                   + surfaceOffset;
        }
    }

    public float GetSurfaceHeight(Vector3 worldPos)
    {
        return SolWaterSurfaceSampler.GetSurfaceHeight(this, worldPos);
    }

    public bool ContainsPoint(Vector3 worldPos)
    {
        return TryGetWaterCollider(out BoxCollider col) && col.bounds.Contains(worldPos);
    }

    public bool IsUnderwater(Vector3 worldPos)
    {
        return ContainsPoint(worldPos) && worldPos.y < GetSurfaceHeight(worldPos);
    }

    void ReadMaterial()
    {
        if (waterMaterial == null)
            return;

        waveAmplitude = waterMaterial.GetFloat("_WaveAmplitude");
        waveFrequency = waterMaterial.GetFloat("_WaveFrequency");
        waveSpeed = waterMaterial.GetFloat("_WaveSpeed");

        Vector4 w1 = waterMaterial.GetVector("_Wave1Direction");
        Vector4 w2 = waterMaterial.GetVector("_Wave2Direction");

        // Keep RAW vectors: the shader adds wind to the unnormalized material
        // vector before normalizing, so pre-normalizing here would skew the
        // wind bias and desync the sampled height from the rendered surface.
        Vector2 dir1 = new Vector2(w1.x, w1.z);
        Vector2 dir2 = new Vector2(w2.x, w2.z);
        wave1Dir = dir1.sqrMagnitude > 0f ? dir1 : Vector2.right;
        wave2Dir = dir2.sqrMagnitude > 0f ? dir2 : new Vector2(0.496f, 0.868f);

        wave2Scale = waterMaterial.GetFloat("_Wave2Scale");

        if (waterMaterial.HasProperty("_WaveSteepness"))
            waveSteepness = waterMaterial.GetFloat("_WaveSteepness");
        if (waterMaterial.HasProperty("_WaveDetailScale"))
            waveDetailScale = waterMaterial.GetFloat("_WaveDetailScale");
        if (waterMaterial.HasProperty("_WaveLODFadeDistance"))
            waveLodFadeDistance = waterMaterial.GetFloat("_WaveLODFadeDistance");

        if (waterMaterial.HasProperty("_SwellAmplitude"))
            swellAmplitude = waterMaterial.GetFloat("_SwellAmplitude");
        if (waterMaterial.HasProperty("_SwellSpeed"))
            swellSpeed = waterMaterial.GetFloat("_SwellSpeed");
        if (waterMaterial.HasProperty("_SwellDirection"))
        {
            // Keep RAW: the shader uses the unnormalized vector, whose
            // magnitude acts as the swell's spatial frequency.
            Vector4 swell = waterMaterial.GetVector("_SwellDirection");
            Vector2 dir = new Vector2(swell.x, swell.z);
            swellDir = dir.sqrMagnitude > 0f ? dir : new Vector2(0.05f, 0.03f);
        }
    }

    bool TryGetWaterCollider(out BoxCollider waterCollider)
    {
        if (_col == null)
            _col = GetComponent<BoxCollider>();

        waterCollider = _col;
        return waterCollider != null;
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (_col == null)
            _col = GetComponent<BoxCollider>();

        if (_col == null)
            return;

        Gizmos.color = new Color(0.1f, 0.6f, 1f, 0.35f);
        Vector3 scale = transform.lossyScale;
        Vector3 centre = transform.position + new Vector3(
            _col.center.x * scale.x,
            _col.center.y * scale.y + _col.size.y * 0.5f * scale.y,
            _col.center.z * scale.z);
        Vector3 size = new Vector3(_col.size.x * scale.x, 0.02f, _col.size.z * scale.z);
        Gizmos.DrawCube(centre, size);

        Gizmos.color = new Color(0.1f, 0.6f, 1f, 0.8f);
        Gizmos.DrawWireCube(centre, size);
    }
#endif
}

public static class SolWaterSurfaceSampler
{
    static readonly Vector2 DefaultWave2Direction = new(0.496f, 0.868f);

    /// <summary>
    /// All per-volume wave parameters needed to evaluate the surface.
    /// MUST stay in sync with SolWaterWaves.hlsl (see that file's header).
    /// </summary>
    public struct WaveSettings
    {
        public float waveTime;
        public float amplitude;
        public float frequency;
        public float speed;
        public Vector2 wave1Dir;
        public Vector2 wave2Dir;
        public float wave2Scale;
        public float steepness;
        public float detailScale;
        public float swellAmplitude;
        public float swellSpeed;
        public Vector2 swellDir;
        public Vector2 windDirXZ;
        public float windStrength;
        public float lodFadeDistance;
    }

    // Shared wave-LOD fade centre, published by WaterTileGrid each frame.
    // Mirrors the _Sol_WaveFadeCenter shader global so gameplay height
    // queries agree with the rendered surface at every distance.
    static Vector2 s_fadeCenter;
    static bool s_hasFadeCenter;

    public static void SetWaveFadeCenter(Vector2 centerXZ)
    {
        s_fadeCenter = centerXZ;
        s_hasFadeCenter = true;
    }

    public static void ClearWaveFadeCenter() => s_hasFadeCenter = false;

    public static float GetSurfaceHeight(WaterVolume volume, Vector3 worldPos)
    {
        if (volume == null)
            return worldPos.y;

        SolWaterManager manager = SolWaterManager.Instance;
        Vector3 windDirection = manager != null ? manager.WindDirectionNormalized : Vector3.right;

        var settings = new WaveSettings
        {
            waveTime = manager != null ? manager.WaveTime : Time.time,
            amplitude = volume.waveAmplitude,
            frequency = volume.waveFrequency,
            speed = volume.waveSpeed,
            wave1Dir = volume.wave1Dir,
            wave2Dir = volume.wave2Dir,
            wave2Scale = volume.wave2Scale,
            steepness = volume.waveSteepness,
            detailScale = volume.waveDetailScale,
            swellAmplitude = volume.swellAmplitude,
            swellSpeed = volume.swellSpeed,
            swellDir = volume.swellDir,
            windDirXZ = new Vector2(windDirection.x, windDirection.z),
            windStrength = manager != null ? manager.windStrength : 0f,
            lodFadeDistance = volume.waveLodFadeDistance,
        };

        return volume.SurfaceY + GetWaveHeight(new Vector2(worldPos.x, worldPos.z), settings);
    }

    /// <summary>
    /// Wave height at a world XZ position. Gerstner waves displace surface
    /// points horizontally, so this inverts the displacement field with a
    /// short fixed-point iteration before reading the height.
    /// </summary>
    public static float GetWaveHeight(Vector2 worldXZ, in WaveSettings s)
    {
        Vector2 sample = worldXZ;
        for (int i = 0; i < 3; i++)
        {
            Vector3 d = EvaluateDisplacement(sample, s);
            sample.x = worldXZ.x - d.x;
            sample.y = worldXZ.y - d.z;
        }
        return EvaluateDisplacement(sample, s).y;
    }

    /// <summary>
    /// Gerstner displacement at an UNDISPLACED sample point.
    /// Mirrors SolEvaluateWaves() in SolWaterWaves.hlsl exactly, including
    /// the distance-based wave LOD fades around the shared fade centre
    /// published by WaterTileGrid (when none is driving, fades are 1).
    /// </summary>
    public static Vector3 EvaluateDisplacement(Vector2 samplePosXZ, in WaveSettings s)
    {
        Vector2 dir1 = NormalizeOrFallback(s.wave1Dir + s.windDirXZ * (s.windStrength * 0.3f), Vector2.right);
        Vector2 dir2 = NormalizeOrFallback(s.wave2Dir + s.windDirXZ * (s.windStrength * 0.15f), DefaultWave2Direction);

        // Detail wave directions: fixed +/-36.87 deg rotations (cos 0.8, sin 0.6).
        Vector2 dir3 = new(dir1.x * 0.8f - dir1.y * 0.6f, dir1.x * 0.6f + dir1.y * 0.8f);
        Vector2 dir4 = new(dir2.x * 0.8f + dir2.y * 0.6f, -dir2.x * 0.6f + dir2.y * 0.8f);

        float amp = s.amplitude * (1f + s.windStrength * 0.2f);

        Vector3 fades = ComputeWaveFades(samplePosXZ, s.lodFadeDistance);
        float ampPrimary = amp * fades.x;
        float ampDetail  = amp * fades.x * fades.y * s.detailScale;
        float steep      = s.steepness * fades.z;

        Vector3 disp = Vector3.zero;
        AccumGerstner(samplePosXZ, dir1, ampPrimary,                s.frequency,        s.speed,         steep, s.waveTime, ref disp);
        AccumGerstner(samplePosXZ, dir2, ampPrimary * s.wave2Scale, s.frequency * 1.3f, s.speed * 0.8f,  steep, s.waveTime, ref disp);
        AccumGerstner(samplePosXZ, dir3, ampDetail * 0.35f,         s.frequency * 2.4f, s.speed * 1.25f, steep, s.waveTime, ref disp);
        AccumGerstner(samplePosXZ, dir4, ampDetail * 0.22f,         s.frequency * 3.9f, s.speed * 1.45f, steep, s.waveTime, ref disp);

        // Long ocean swell: vertical-only sine. The RAW direction vector's
        // magnitude is its spatial frequency.
        float swellPhase = (s.swellDir.x * samplePosXZ.x + s.swellDir.y * samplePosXZ.y)
                         + s.waveTime * s.swellSpeed;
        disp.y += Mathf.Sin(swellPhase) * s.swellAmplitude;

        return disp;
    }

    /// <summary>
    /// Mirrors SolComputeWaveFades() in SolWaterWaves.hlsl exactly.
    /// x = primary waves, y = detail waves, z = steepness.
    /// </summary>
    static Vector3 ComputeWaveFades(Vector2 posXZ, float fadeDistance)
    {
        if (!s_hasFadeCenter || fadeDistance <= 0f)
            return Vector3.one;

        float dist = Vector2.Distance(posXZ, s_fadeCenter);
        float f = Mathf.Max(fadeDistance, 1f);
        return new Vector3(
            1f - SmoothStep(f * 0.5f,  f,         dist),
            1f - SmoothStep(f * 0.25f, f * 0.55f, dist),
            1f - SmoothStep(f * 0.4f,  f * 0.8f,  dist));
    }

    // HLSL-style smoothstep(edge0, edge1, x). Mathf.SmoothStep has
    // different argument semantics, so implement it directly.
    static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    static void AccumGerstner(
        Vector2 posXZ, Vector2 dir, float amp, float freq, float phaseSpeed,
        float steepness, float waveTime, ref Vector3 disp)
    {
        float theta = (dir.x * posXZ.x + dir.y * posXZ.y) * freq + waveTime * phaseSpeed;
        float sin = Mathf.Sin(theta);
        float cos = Mathf.Cos(theta);

        // Clamp steepness so horizontal displacement vanishes with amplitude
        // and the 4-wave sum can never self-intersect (matches HLSL).
        float q = Mathf.Min(steepness, 1f / Mathf.Max(freq * amp * 4f, 1e-4f));

        disp.x += dir.x * (q * amp * cos);
        disp.z += dir.y * (q * amp * cos);
        disp.y += amp * sin;
    }

    public static Vector3 GetSurfaceDriftVelocity(WaterVolume volume)
    {
        SolWaterManager manager = SolWaterManager.Instance;
        if (manager == null)
            return Vector3.zero;

        Vector3 windDirection = manager.WindDirectionNormalized;
        windDirection.y = 0f;
        if (windDirection.sqrMagnitude <= 0.0001f)
            return Vector3.zero;

        float windStrength = Mathf.Max(0f, manager.windStrength);
        float waveAmplitude = volume != null ? Mathf.Max(0f, volume.waveAmplitude) : 0.3f;
        float driftSpeed = (0.015f + windStrength * 0.025f) * Mathf.Lerp(0.6f, 1.4f, Mathf.Clamp01(waveAmplitude));
        return windDirection.normalized * driftSpeed;
    }

    static Vector2 NormalizeOrFallback(Vector2 value, Vector2 fallback)
    {
        return value.sqrMagnitude > 0.000001f ? value.normalized : fallback.normalized;
    }
}



