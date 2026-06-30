using UnityEngine;

/// <summary>
/// Manages interactive water ripples - ring waves that expand outward from
/// objects moving through the water surface.
///
/// Singleton. Maintains a fixed-size ring buffer of ripple events and uploads
/// them to global shader properties so the Sol.Water shader can
/// render concentric ring normals.
/// </summary>
[ExecuteAlways]
public class WaterRippleManager : MonoBehaviour
{
    // --- Singleton -------------------------------------------------------
    public static WaterRippleManager Instance { get; private set; }

    // --- Settings --------------------------------------------------------
    [Header("Ripple Settings")]
    [Tooltip("Expansion speed of each ring (world units / second).")]
    [Range(0.5f, 20f)]
    public float rippleSpeed = 5f;

 [Tooltip("Ring density - higher values produce tighter concentric rings.")]
    [Range(1f, 60f)]
    public float rippleFrequency = 20f;

    [Tooltip("Seconds before a ripple fades out completely.")]
    [Range(0.5f, 10f)]
    public float rippleLifetime = 3f;

    // --- Internals -------------------------------------------------------
    const int MaxRipples = 32;

    // Ring buffer: each entry is (worldX, worldZ, birthTime, strength)
    readonly Vector4[] _ripples = new Vector4[MaxRipples];
    readonly Vector4[] _gpuRipples = new Vector4[MaxRipples];
    int _writeIndex;
    int _activeCount;

    bool _gpuDirty = true;
    float _lastRippleSpeed = float.NaN;
    float _lastRippleFrequency = float.NaN;
    float _lastRippleLifetime = float.NaN;

    // Shader property IDs
    static readonly int _RipplesID         = Shader.PropertyToID("_Sol_Ripples");
    static readonly int _RippleCountID     = Shader.PropertyToID("_Sol_RippleCount");
    static readonly int _RippleSpeedID     = Shader.PropertyToID("_Sol_RippleSpeed");
    static readonly int _RippleFrequencyID = Shader.PropertyToID("_Sol_RippleFrequency");
    static readonly int _RippleLifetimeID  = Shader.PropertyToID("_Sol_RippleLifetime");

    // --- Lifecycle -------------------------------------------------------

    void OnEnable()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[WaterRippleManager] Duplicate instance detected. Destroying this one.", this);
            enabled = false;
            return;
        }
        Instance = this;
        _gpuDirty = true;
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;

        // Clear GPU data so ripples don't persist after disable.
        Shader.SetGlobalInt(_RippleCountID, 0);
    }

    void Update()
    {
        if (_activeCount > 0)
            PurgeExpired();

        PushToGPU();
    }

    // --- Public API ------------------------------------------------------

    /// <summary>
    /// Emit a ripple at the given world position.
    /// Call from WaterRippleSource or any gameplay code.
    /// </summary>
    /// <param name="worldPos">World-space position (only XZ is used).</param>
 /// <param name="strength">Normal perturbation strength (0.01-1 typical).</param>
    public void Emit(Vector3 worldPos, float strength = 0.15f)
    {
        _ripples[_writeIndex] = new Vector4(worldPos.x, worldPos.z, Time.time, strength);
        _writeIndex = (_writeIndex + 1) % MaxRipples;
        if (_activeCount < MaxRipples) _activeCount++;
        _gpuDirty = true;
    }

    // --- Internals -------------------------------------------------------

    void PurgeExpired()
    {
        float now = Time.time;
        for (int i = 0; i < MaxRipples; i++)
        {
            if (_ripples[i].w == 0f) continue;
            if (now - _ripples[i].z > rippleLifetime)
            {
                _ripples[i] = Vector4.zero;
                _activeCount = Mathf.Max(0, _activeCount - 1);
                _gpuDirty = true;
            }
        }
    }

    void PushToGPU()
    {
        bool settingsDirty = !Mathf.Approximately(_lastRippleSpeed, rippleSpeed)
            || !Mathf.Approximately(_lastRippleFrequency, rippleFrequency)
            || !Mathf.Approximately(_lastRippleLifetime, rippleLifetime);

        if (_gpuDirty)
        {
            int count = 0;
            float now = Time.time;

            for (int i = 0; i < MaxRipples; i++)
            {
                Vector4 ripple = _ripples[i];
                if (ripple.w <= 0f)
                    continue;

                if (now - ripple.z > rippleLifetime)
                {
                    _ripples[i] = Vector4.zero;
                    continue;
                }

                _gpuRipples[count++] = ripple;
            }

            for (int i = count; i < MaxRipples; i++)
                _gpuRipples[i] = Vector4.zero;

            _activeCount = count;
            Shader.SetGlobalVectorArray(_RipplesID, _gpuRipples);
            Shader.SetGlobalInt(_RippleCountID, _activeCount);
            _gpuDirty = false;
        }

        if (settingsDirty)
        {
            Shader.SetGlobalFloat(_RippleSpeedID, rippleSpeed);
            Shader.SetGlobalFloat(_RippleFrequencyID, rippleFrequency);
            Shader.SetGlobalFloat(_RippleLifetimeID, rippleLifetime);
            _lastRippleSpeed = rippleSpeed;
            _lastRippleFrequency = rippleFrequency;
            _lastRippleLifetime = rippleLifetime;
        }
    }
}
