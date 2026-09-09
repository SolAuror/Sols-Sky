using System.Collections.Generic;
using UnityEngine;

namespace Sol.Lighting
{
    /// <summary>Reusable GPU publication of independent celestial emitters.</summary>
    public sealed class SolCelestialLighting
    {
        // Matches SolCelestialLighting.hlsl. Surface lighting remains in URP; this
        // bounds the cost of custom volumetrics even with many authored planets.
        public const int Capacity = 8;
        static readonly int ActiveId = Shader.PropertyToID("_SolCelestialLightingActive");
        static readonly int CountId = Shader.PropertyToID("_SolCelestialLightCount");
        static readonly int DirectionsId = Shader.PropertyToID("_SolCelestialDirections");
        static readonly int ColorsId = Shader.PropertyToID("_SolCelestialColors");
        readonly Vector4[] _directions = new Vector4[Capacity];
        readonly Vector4[] _colors = new Vector4[Capacity];
        public int Count { get; private set; }

        public void Publish(in SolLightingFrame frame,
            IReadOnlyList<SolDirectionalLightState> additional)
        {
            Count = 0;
            Add(frame.Sun, frame.DominantLight);
            Add(frame.Moon, frame.DominantLight);
            for (int i = 0; i < additional.Count && Count < Capacity; i++)
                Add(additional[i], frame.DominantLight);
            Shader.SetGlobalFloat(ActiveId, 1f);
            Shader.SetGlobalInt(CountId, Count);
            Shader.SetGlobalVectorArray(DirectionsId, _directions);
            Shader.SetGlobalVectorArray(ColorsId, _colors);
        }

        void Add(in SolDirectionalLightState state, Light dominant)
        {
            if (state.Score <= 0.000001f || Count >= Capacity) return;
            _directions[Count] = new Vector4(state.Direction.x, state.Direction.y,
                state.Direction.z, state.Source != null && state.Source == dominant ? 1f : 0f);
            Color radiance = state.Radiance;
            _colors[Count] = new Vector4(radiance.r, radiance.g, radiance.b, 0f);
            Count++;
        }

        public static void Clear()
        {
            Shader.SetGlobalInt(CountId, 0);
            Shader.SetGlobalFloat(ActiveId, 0f);
        }
    }
}
