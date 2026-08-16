using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sol.Water
{
    /// <summary>Canonical sampled water surface data shared by rendering and gameplay queries.</summary>
    public readonly struct SolWaterSurfaceFrame
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public readonly Vector3 Velocity;
        public readonly float Foam;
        public readonly float Depth;
        public readonly float Confidence;

        public SolWaterSurfaceFrame(Vector3 position, Vector3 normal, Vector3 velocity,
            float foam, float depth, float confidence)
        {
            Position = position;
            Normal = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
            Velocity = velocity;
            Foam = Mathf.Clamp01(foam);
            Depth = Mathf.Max(0f, depth);
            Confidence = Mathf.Clamp01(confidence);
        }

        public static SolWaterSurfaceFrame FromSample(in SolWaterSurfaceSample sample)
            => new(sample.Position, sample.Normal, sample.Velocity,
                sample.Foam, sample.Depth, sample.Confidence);
    }

    [Serializable]
    public readonly struct SolWaterSurfaceSample
    {
        public readonly bool HasWater;
        public readonly SolWaterBodyId BodyId;
        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public readonly Vector3 Velocity;
        public readonly Vector3 Flow;
        public readonly float Depth;
        public readonly float Foam;
        public readonly float SampleAgeSeconds;
        public readonly float Confidence;

        public SolWaterSurfaceSample(
            bool hasWater,
            SolWaterBodyId bodyId,
            Vector3 position,
            Vector3 normal,
            Vector3 velocity,
            Vector3 flow,
            float depth,
            float foam,
            float sampleAgeSeconds,
            float confidence)
        {
            HasWater = hasWater;
            BodyId = bodyId;
            Position = position;
            Normal = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
            Velocity = velocity;
            Flow = flow;
            Depth = Mathf.Max(0f, depth);
            Foam = Mathf.Clamp01(foam);
            SampleAgeSeconds = Mathf.Max(0f, sampleAgeSeconds);
            Confidence = Mathf.Clamp01(confidence);
        }

        public static SolWaterSurfaceSample NoWater(Vector3 position)
            => new(false, default, position, Vector3.up, Vector3.zero, Vector3.zero, 0f, 0f, 0f, 1f);
    }

    public readonly struct SolWaterQueryBatchHandle : IEquatable<SolWaterQueryBatchHandle>
    {
        public readonly uint Value;
        public bool IsValid => Value != 0;
        public SolWaterQueryBatchHandle(uint value) => Value = value;
        public bool Equals(SolWaterQueryBatchHandle other) => Value == other.Value;
        public override bool Equals(object obj) => obj is SolWaterQueryBatchHandle other && Equals(other);
        public override int GetHashCode() => (int)Value;
    }

    public interface ISolWaterQueryService
    {
        bool TrySampleImmediate(Vector3 localPosition, out SolWaterSurfaceSample sample);
        SolWaterQueryBatchHandle RequestBatch(
            IReadOnlyList<Vector3> localPositions,
            Action<SolWaterQueryBatchHandle, IReadOnlyList<SolWaterSurfaceSample>> completed);
        bool Cancel(SolWaterQueryBatchHandle handle);
    }
}
