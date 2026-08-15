using UnityEngine;

namespace Sol.Water
{
    /// <summary>Deposits velocity-scaled wakes into any bounded interaction zone it crosses.</summary>
    [DisallowMultipleComponent]
    public sealed class SolWaterInteractor : MonoBehaviour
    {
        [SerializeField, Min(0.01f)] float radius = 0.75f;
        [SerializeField, Min(0f)] float strength = 0.18f;
        [SerializeField, Min(0f)] float maximumImpulse = 2f;
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
