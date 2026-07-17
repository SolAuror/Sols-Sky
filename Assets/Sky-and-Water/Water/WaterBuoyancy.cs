using UnityEngine;

/// <summary>
/// ---------------------------------------------------------------------------
/// WATER BUOYANCY
/// ---------------------------------------------------------------------------
///
/// Makes a Rigidbody float on Sol water. Samples the animated Gerstner
/// surface at several float points (via WaterVolume / SolWaterSurfaceSampler,
/// which mirror the wave shader exactly) and applies:
///   - An upward buoyant force per submerged point, scaled by depth.
///   - Water linear/angular damping blended by how submerged the body is.
///   - Optional wind-driven surface drift.
///
/// SETUP:
///   1. Add to any Rigidbody object (boat, barrel, crate...).
///   2. Optionally assign explicit float points. Leave empty to derive four
///      corner points from the collider bounds automatically.
///   3. Requires a WaterVolume covering the water body (WaterTileGrid's
///      auto-volume works out of the box).
///
/// TUNING:
///   - buoyancy &lt; 1 sinks, 1 = neutral, &gt; 1 floats. 1.5-3 suits wooden
///     objects.
///   - submersionDepth controls float stiffness: smaller = snappier bobbing.
///   - Use 3+ spread-out float points (or the auto corners) so the body
///     rolls and pitches with the waves.
/// ---------------------------------------------------------------------------
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class WaterBuoyancy : MonoBehaviour
{
    [Header("Buoyancy")]
    [Tooltip("Gravity multiple counteracted when a point is fully submerged. <1 sinks, 1 = neutral, >1 floats.")]
    [Range(0f, 10f)]
    public float buoyancy = 2f;

    [Tooltip("Depth (metres) below the surface at which a float point reaches full buoyant force.")]
    [Range(0.05f, 5f)]
    public float submersionDepth = 0.6f;

    [Tooltip("Explicit float points. Leave empty to derive 4 corner points from the collider bounds.")]
    public Transform[] floatPoints;

    [Header("Water Damping")]
    [Tooltip("Rigidbody linear damping while fully submerged.")]
    public float waterDrag = 2f;

    [Tooltip("Rigidbody angular damping while fully submerged.")]
    public float waterAngularDrag = 1.5f;

    [Header("Drift")]
    [Tooltip("Push floating objects along the wind-driven surface drift (SolWaterSurfaceSampler.GetSurfaceDriftVelocity).")]
    public bool applyWindDrift = true;

    [Tooltip("How quickly the horizontal velocity converges toward the drift velocity.")]
    [Range(0f, 5f)]
    public float driftResponse = 0.5f;

    Rigidbody _rb;
    Vector3[] _localPoints;
    float _baseLinearDamping;
    float _baseAngularDamping;
    float _submergedFraction;

    /// <summary>0 = fully out of the water, 1 = all float points submerged.</summary>
    public float SubmergedFraction => _submergedFraction;

    /// <summary>True while at least one float point is below the surface.</summary>
    public bool IsInWater => _submergedFraction > 0f;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _baseLinearDamping = _rb.linearDamping;
        _baseAngularDamping = _rb.angularDamping;
        BuildAutoPoints();
    }

    void BuildAutoPoints()
    {
        if (floatPoints != null && floatPoints.Length > 0)
            return;

        // Derive four XZ corner points from the collider bounds, stored in
        // local space so they follow the body as it rotates. Bounds are a
        // world-space AABB, so this assumes a roughly upright spawn pose.
        var col = GetComponentInChildren<Collider>();
        Bounds b = col != null ? col.bounds : new Bounds(transform.position, Vector3.one);
        Vector3 c = b.center;
        Vector3 e = b.extents * 0.7f;

        _localPoints = new[]
        {
            transform.InverseTransformPoint(c + new Vector3(-e.x, 0f, -e.z)),
            transform.InverseTransformPoint(c + new Vector3( e.x, 0f, -e.z)),
            transform.InverseTransformPoint(c + new Vector3(-e.x, 0f,  e.z)),
            transform.InverseTransformPoint(c + new Vector3( e.x, 0f,  e.z)),
        };
    }

    int PointCount => floatPoints != null && floatPoints.Length > 0
        ? floatPoints.Length
        : (_localPoints != null ? _localPoints.Length : 0);

    Vector3 GetPoint(int i)
    {
        if (floatPoints != null && floatPoints.Length > 0)
            return floatPoints[i] != null ? floatPoints[i].position : transform.position;
        return transform.TransformPoint(_localPoints[i]);
    }

    void FixedUpdate()
    {
        int count = PointCount;
        if (count == 0)
            return;

        int submerged = 0;
        WaterVolume lastVolume = null;

        for (int i = 0; i < count; i++)
        {
            Vector3 p = GetPoint(i);
            WaterVolume vol = WaterVolume.FindVolumeXZ(p);
            if (vol == null)
                continue;

            float depth = vol.GetSurfaceHeight(p) - p.y;
            if (depth <= 0f)
                continue;

            submerged++;
            lastVolume = vol;

            float t = Mathf.Clamp01(depth / submersionDepth);
            Vector3 force = -Physics.gravity * (_rb.mass * buoyancy * t / count);
            _rb.AddForceAtPosition(force, p);
        }

        _submergedFraction = (float)submerged / count;

        // Blend damping toward the water values by submerged fraction.
        _rb.linearDamping = Mathf.Lerp(_baseLinearDamping, waterDrag, _submergedFraction);
        _rb.angularDamping = Mathf.Lerp(_baseAngularDamping, waterAngularDrag, _submergedFraction);

        if (applyWindDrift && submerged > 0 && driftResponse > 0f)
        {
            Vector3 drift = SolWaterSurfaceSampler.GetSurfaceDriftVelocity(lastVolume);
            Vector3 vel = _rb.linearVelocity;
            Vector3 horizontal = new Vector3(vel.x, 0f, vel.z);
            _rb.AddForce((drift - horizontal) * (driftResponse * _submergedFraction),
                ForceMode.Acceleration);
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        bool hasExplicit = floatPoints != null && floatPoints.Length > 0;
        if (!hasExplicit && _localPoints == null)
            return;

        int count = hasExplicit ? floatPoints.Length : _localPoints.Length;
        for (int i = 0; i < count; i++)
        {
            Vector3 p = GetPoint(i);
            bool wet = false;
            if (Application.isPlaying)
            {
                WaterVolume vol = WaterVolume.FindVolumeXZ(p);
                wet = vol != null && p.y < vol.GetSurfaceHeight(p);
            }
            Gizmos.color = wet ? new Color(0.2f, 0.7f, 1f) : new Color(1f, 0.8f, 0.2f);
            Gizmos.DrawWireSphere(p, 0.12f);
        }
    }
#endif
}
