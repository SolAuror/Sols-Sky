using UnityEngine;

namespace Sol.Water
{
    /// <summary>Allocation-free multi-point buoyancy with flow-relative drag and body transitions.</summary>
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public sealed class SolWaterBuoyancy : MonoBehaviour
    {
        [SerializeField] Rigidbody body;
        [SerializeField] Vector3[] localSamplePoints =
        {
            new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f),
            new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f),
            new(-0.5f, 0.5f, -0.5f), new(0.5f, 0.5f, -0.5f),
            new(-0.5f, 0.5f, 0.5f), new(0.5f, 0.5f, 0.5f),
        };
        [SerializeField, Min(0.01f)] float submersionDepth = 1f;
        [SerializeField, Min(0f)] float buoyancyAcceleration = 12f;
        [SerializeField, Min(0f)] float linearDrag = 1.8f;
        [SerializeField, Min(0f)] float angularStabilization = 2f;
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
