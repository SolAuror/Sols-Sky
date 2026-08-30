using UnityEngine;

namespace Sol.Water
{
    /// <summary>Deposits velocity-scaled wakes into any bounded interaction zone it crosses.</summary>
    [DisallowMultipleComponent]
    public sealed class SolWaterInteractor : MonoBehaviour
    {
        [Header("Wake")]
        [Tooltip("Radius of the disturbance left in the water, in metres. Roughly the "
            + "width of the object cutting through the surface.")]
        [SerializeField, Min(0.01f)] float radius = 0.75f;
        [Tooltip("Wake amplitude per metre per second of travel. The wake is "
            + "velocity-scaled, so a stationary object leaves nothing.")]
        [SerializeField, Min(0f)] float strength = 0.18f;
        [Tooltip("Ceiling on the wake amplitude, so a fast object cannot blow the "
            + "ripple simulation out.")]
        [SerializeField, Min(0f)] float maximumImpulse = 2f;

        [Header("Velocity Source")]
        [Tooltip("Rigidbody to take velocity from. Empty differences this transform's "
            + "own position instead, which is what a kinematic or animated object needs.")]
        [SerializeField] Rigidbody sourceRigidbody;

        Vector3 _previousPosition;
        bool _initialized;

        void OnEnable()
        {
            _previousPosition = transform.position;
            _initialized = true;
        }

        void FixedUpdate()
        {
            Vector3 position = transform.position;
            Vector3 velocity = sourceRigidbody != null
                ? sourceRigidbody.linearVelocity
                : _initialized ? (position - _previousPosition) / Mathf.Max(Time.fixedDeltaTime, 0.0001f) : Vector3.zero;
            _previousPosition = position;
            _initialized = true;
            float impulse = Mathf.Min(maximumImpulse, velocity.magnitude * strength);
            if (impulse <= 0.0001f)
                return;

            var zones = SolWaterInteractionZone.ActiveZones;
            for (int i = 0; i < zones.Count; i++)
            {
                SolWaterInteractionZone zone = zones[i];
                if (zone != null && zone.isActiveAndEnabled)
                    zone.AddImpulse(position, radius, impulse);
            }
        }
    }
}
