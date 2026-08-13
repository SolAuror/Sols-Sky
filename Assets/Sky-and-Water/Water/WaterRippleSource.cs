using UnityEngine;

/// <summary>
/// Attach to any object (player, NPC, boat, barrel-) that should create
/// ripples when it touches or moves through water.
///
/// Periodically emits ripples via WaterRippleManager while the object is
/// inside a WaterVolume and moving above a minimum speed threshold.
///
/// Supports both Rigidbody-based and Transform-based velocity detection.
/// </summary>
public class WaterRippleSource : MonoBehaviour
{
    [Header("Emission")]
    [Tooltip("Seconds between ripple emissions while in motion.")]
    [Range(0.05f, 2f)]
    public float emitInterval = 0.25f;

    [Tooltip("Minimum XZ speed (m/s) before ripples start emitting.")]
    [Range(0f, 5f)]
    public float minSpeed = 0.3f;

    [Tooltip("Normal perturbation strength per ripple (scales with speed).")]
    [Range(0.01f, 1f)]
    public float strength = 0.15f;

    [Tooltip("Maximum strength clamp (prevents extreme ripples at high speed).")]
    [Range(0.01f, 1f)]
    public float maxStrength = 0.4f;

    [Header("Entry Splash")]
    [Tooltip("Emit a stronger ripple the instant the object enters the water.")]
    public bool emitOnEntry = true;

    [Tooltip("Strength multiplier for the entry splash ripple.")]
    [Range(1f, 5f)]
    public float entrySplashMultiplier = 2.5f;

    // --- Internals -------------------------------------------------------

    Rigidbody _rb;
    Vector3   _prevPos;
    float     _nextEmitTime;
    bool      _wasInWater;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _prevPos = transform.position;
    }

    void Update()
    {
        WaterRippleManager rippleManager = WaterRippleManager.Instance;
        if (rippleManager == null || rippleManager.WorldDeltaSeconds <= 0f) return;

        float worldTime = rippleManager.WorldTime;
        float worldDelta = rippleManager.WorldDeltaSeconds;

        Vector3 pos = transform.position;

        // Check if we're inside a water volume
        WaterVolume vol = WaterVolume.FindVolumeXZ(pos);
        bool inWater = vol != null && pos.y < vol.GetSurfaceHeight(pos);

        // Entry splash
        if (inWater && !_wasInWater && emitOnEntry)
        {
            float entrySt = Mathf.Min(strength * entrySplashMultiplier, maxStrength);
            rippleManager.Emit(pos, entrySt);
            _nextEmitTime = worldTime + emitInterval;
        }

        _wasInWater = inWater;

        if (!inWater)
        {
            _prevPos = pos;
            return;
        }

        // Velocity (XZ only)
        float speedSq;
        if (_rb != null)
        {
            Vector3 vel = _rb.linearVelocity;
            speedSq = vel.x * vel.x + vel.z * vel.z;
        }
        else
        {
            Vector3 delta = (pos - _prevPos) / Mathf.Max(worldDelta, 0.0001f);
            speedSq = delta.x * delta.x + delta.z * delta.z;
        }

        _prevPos = pos;

        float minSpeedSq = minSpeed * minSpeed;
        if (speedSq < minSpeedSq) return;
        if (worldTime < _nextEmitTime) return;

        // Scale strength with speed (clamped)
        float speedXZ = Mathf.Sqrt(speedSq);
        float st = Mathf.Min(strength * (speedXZ / Mathf.Max(minSpeed, 0.01f)), maxStrength);
        rippleManager.Emit(pos, st);
        _nextEmitTime = worldTime + emitInterval;
    }
}
