using UnityEngine;

namespace Sol.Water
{
    /// <summary>Base surface information supplied by generated finite-water geometry.</summary>
    public readonly struct SolWaterGeometrySample
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public readonly Vector3 Flow;
        public readonly float ChannelDepth;
        public readonly float Foam;

        public SolWaterGeometrySample(Vector3 position, Vector3 normal, Vector3 flow,
            float channelDepth, float foam)
        {
            Position = position;
            Normal = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
            Flow = flow;
            ChannelDepth = Mathf.Max(0f, channelDepth);
            Foam = Mathf.Clamp01(foam);
        }
    }

    /// <summary>
    /// Optional geometry contract used by finite water bodies. Implementations remain scene-owned,
    /// so streamed cells can register and release their mesh and sampling data with the body.
    /// </summary>
    public interface ISolWaterGeometry
    {
        Renderer SurfaceRenderer { get; }
        Bounds WorldBounds { get; }
        Vector3 RepresentativeFlow { get; }
        bool SupportsSurfaceWaves { get; }
        bool ContainsPoint(Vector3 localWorldPosition);
        bool TrySample(Vector3 localWorldPosition, out SolWaterGeometrySample sample);
    }
}
