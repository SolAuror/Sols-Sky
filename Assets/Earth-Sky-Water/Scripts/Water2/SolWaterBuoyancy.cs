using UnityEngine;

namespace Sol.Water
{
    /// <summary>Allocation-free multi-point buoyancy with flow-relative drag and body transitions.</summary>
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public sealed class SolWaterBuoyancy : MonoBehaviour
    {
        [Header("Body")]
        [Tooltip("Rigidbody the forces are applied to. Resolved from this object.")]
        [SerializeField] Rigidbody body;
        [Tooltip("Local-space points where the water is sampled, one force per point. "
            + "The default is a unit cube's corners. Each point costs a full water query "
            + "every physics step, so keep the count to what the hull actually needs.")]
        [SerializeField] Vector3[] localSamplePoints =
        {
            new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f),
            new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f),
            new(-0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, -0.5f),
            new(-0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, 0.5f),
        };

        [Header("Forces")]
        [Tooltip("Depth below the surface, in metres, at which a sample point counts as "
            + "fully submerged and produces its full share of lift.")]
        [SerializeField, Min(0.01f)] float submersionDepth = 1f;
        [Tooltip("Upward acceleration applied by fully submerged water, in m/s squared. "
            + "It has to exceed gravity for the object to float.")]
        [SerializeField, Min(0f)] float buoyancyAcceleration = 12f;
        [Tooltip("Resistance to moving through the water, measured against the water's "
            + "own velocity rather than the world.")]
        [SerializeField, Min(0f)] float linearDrag = 1.8f;
        [Tooltip("Torque that turns the object upright against the water surface normal. "
            + "Raise it to stop a hull rolling in a swell.")]
        [SerializeField, Min(0f)] float angularStabilization = 2f;
        [Tooltip("How strongly a current carries the object along. Reads authored flow "
            + "plus whatever the geometry supplies, so it is what moves a boat downriver.")]
        [SerializeField, Min(0f)] float flowForce = 1f;

        SolWaterBodyId _activeBody;

        void Reset() => body = GetComponent<Rigidbody>();

        void FixedUpdate()
        {
            if (body == null || SolWaterWorld.Active?.QueryService == null
                || localSamplePoints == null || localSamplePoints.Length == 0)
                return;

            int submerged = 0;
            Vector3 averageNormal = Vector3.zero;
            SolWaterBodyId resolvedBody = default;
            float perPointMass = body.mass / localSamplePoints.Length;
            for (int i = 0; i < localSamplePoints.Length; i++)
            {
                Vector3 point = transform.TransformPoint(localSamplePoints[i]);
                if (!SolWaterWorld.Active.QueryService.TrySampleImmediate(point, out SolWaterSurfaceSample sample)
                    || !sample.HasWater || sample.Depth <= 0f)
                    continue;
                float submergedFraction = Mathf.Clamp01(sample.Depth / submersionDepth);
                Vector3 pointVelocity = body.GetPointVelocity(point);
                Vector3 relativeVelocity = pointVelocity - sample.Velocity;
                body.AddForceAtPosition(Vector3.up
                    * (buoyancyAcceleration * perPointMass * submergedFraction), point,
                    ForceMode.Force);
                body.AddForceAtPosition(-relativeVelocity
                    * (linearDrag * perPointMass * submergedFraction), point,
                    ForceMode.Force);
                body.AddForceAtPosition(sample.Flow
                    * (flowForce * perPointMass * submergedFraction), point,
                    ForceMode.Force);
                averageNormal += sample.Normal * submergedFraction;
                resolvedBody = sample.BodyId;
                submerged++;
            }

            if (submerged > 0 && averageNormal.sqrMagnitude > 0.0001f)
            {
                Vector3 correction = Vector3.Cross(transform.up, averageNormal.normalized);
                body.AddTorque(correction * angularStabilization, ForceMode.Acceleration);
            }
            if (resolvedBody.IsValid && resolvedBody != _activeBody)
            {
                _activeBody = resolvedBody;
                body.WakeUp();
            }
        }
    }
}
