using UnityEngine;

namespace Sol.Water
{
    /// <summary>
    /// Inverts horizontal wave displacement so a world XZ query returns the surface
    /// point that actually sits above it.
    ///
    /// Choppy water moves points sideways: the surface sample evaluated at XZ ends up
    /// somewhere else, and the point that ends up at XZ came from somewhere else again.
    /// Sampling the surface directly at the query position therefore reports the wrong
    /// height, and the error grows with choppiness — worst exactly at crests and
    /// troughs, which is where buoyancy cares most.
    ///
    /// Adapted from WaterFX's buoyancy readback, which solves the same fixed point:
    ///     find u such that u + D(u).xz == target
    /// by iterating u -= (u + D(u).xz) - target.
    ///
    /// The iteration contracts by roughly the wave steepness per step, so it is fast
    /// on calm water and slow as a wave approaches breaking. The solve early-outs on
    /// convergence and reports its residual, so callers can lower query confidence
    /// rather than silently trusting a sample that never resolved.
    /// </summary>
    public static class SolWaterDisplacementInversion
    {
        /// <summary>
        /// Iteration cap. The fixed point contracts by roughly the wave steepness per
        /// step, so four iterations (the donor's choice) only converges tightly for
        /// gentle water: at steepness 0.7 it still leaves ~0.2 m of error. The solve
        /// early-outs on convergence, so the higher cap costs nothing on calm water and
        /// buys accuracy on choppy water.
        /// </summary>
        public const int DefaultIterations = 8;

        /// <summary>Horizontal error in metres below which the solve stops early.</summary>
        public const float ConvergenceEpsilon = 0.005f;

        /// <summary>Evaluates horizontal displacement at a sample position.</summary>
        public delegate Vector2 HorizontalDisplacement(Vector2 samplePositionXZ);

        /// <summary>
        /// Returns the un-displaced sample position whose displaced result lands on
        /// <paramref name="targetXZ"/>. Feed the result back into the wave evaluator to
        /// get the surface point above the query.
        /// </summary>
        public static Vector2 Solve(Vector2 targetXZ, HorizontalDisplacement displacement,
            int iterations = DefaultIterations)
        {
            if (displacement == null)
                return targetXZ;

            Vector2 estimate = targetXZ;
            for (int i = 0; i < Mathf.Max(0, iterations); i++)
            {
                Vector2 displaced = estimate + displacement(estimate);
                Vector2 error = displaced - targetXZ;
                estimate -= error;
                // Calm water converges in one or two steps; stopping early keeps the
                // common case as cheap as the fixed four-iteration version was.
                if (error.sqrMagnitude
                    <= ConvergenceEpsilon * ConvergenceEpsilon)
                    break;
            }
            return estimate;
        }

        /// <summary>
        /// Residual horizontal error left after solving, in metres. Exposed so callers
        /// can fold convergence into query confidence rather than reporting a fixed
        /// value: a steep, near-breaking wave genuinely is a less reliable sample.
        /// </summary>
        public static float ResidualError(Vector2 targetXZ, Vector2 solved,
            HorizontalDisplacement displacement)
        {
            if (displacement == null)
                return 0f;
            Vector2 displaced = solved + displacement(solved);
            return (displaced - targetXZ).magnitude;
        }

        /// <summary>
        /// Maps residual error to a 0-1 confidence. One metre of unresolved horizontal
        /// error is treated as fully unreliable; the falloff between is linear.
        /// </summary>
        public static float ConfidenceFromResidual(float residualError)
            => Mathf.Clamp01(1f - Mathf.Max(0f, residualError));
    }
}
